using System.Collections.Generic;
using Modding;
using UnityEngine;

namespace BeyondSpiderAssembly
{
    // Captain-hosted 3D radar screen. A Mod.Root-level singleton (one screen for whichever
    // ship the local player currently owns), not a per-block component — see
    // docs/adr/0003-captain-hosts-radar-lock-ui.md for why this lives on Captain, and
    // docs/superpowers/specs/2026-07-06-captain-radar-lock-fire-control-design.md §4 for the
    // "pocket scene" rendering approach this implements.
    public class CaptainRadarView : SingleInstance<CaptainRadarView>
    {
        public override string Name { get { return "BeyondSpider Captain Radar View"; } }

        // Far enough that no real gameplay content (Besiege's build/play area is nowhere near this
        // far out) can ever land here, but small enough that float32 precision stays sub-millimeter
        // at this magnitude — 500,000 previously caused ~6cm/frame jitter on the orbiting camera.
        private const float PocketOriginHeight = 5000f;
        private const float DisplayRadius = 4f;
        private const int RingCount = 4;
        private const int SegmentCount = 64;
        private const float MarkerColliderRadius = 0.3f;
        private const float MarkerScale = 0.3f;
        private const float PanelSize = 420f;
        private const float MinMetersPerUnit = 5f;
        private const float MaxMetersPerUnit = 2000f;
        private const float ZoomStep = 1.15f;
        private const float OrbitSpeed = 3f;
        private const float ClickPixelThreshold = 4f;
        private const float RingStepBase = 10f;
        private const float RingStepRatio = 5f;
        private const int RadarTextureSize = 512;
        private const int RadarTextureAntiAliasing = 4;
        private const float OriginMarkerScale = 2f;
        private const float RadarBloomAlpha = 0.16f;
        private const float RadarBloomOffset = 1.5f;
        private const float GlowRadius = DisplayRadius * 1.35f;
        private const float SweepArcDegrees = 55f;
        private const float SweepSpeedDegPerSec = 90f;
        private const int VignetteTextureSize = 64;

        public bool IsOpen;
        public float MetersPerUnit = 50f;

        private bool orbiting;
        private bool leftDownInRect;
        private bool radarOwnsMouse;
        private bool mouseOrbitPaused;
        private bool mouseOrbitWasEnabled;
        private bool cameraSnapshotActive;
        private Vector2 mouseDownPos;
        private float yaw;
        private float pitch = 25f;

        private Transform pocketRoot;
        private Transform gimbal;
        private Transform gridTransform;
        private Camera radarCamera;
        private RenderTexture radarTexture;
        private Camera guardedGameCamera;
        private MouseOrbit guardedMouseOrbit;
        private Vector3 guardedCameraPosition;
        private Quaternion guardedCameraRotation;
        private float guardedCameraFieldOfView;
        private float guardedCameraOrthographicSize;
        private Material markerMaterial;
        private GameObject lockIcon;
        private Mesh arrowMesh;
        private Transform sweepTransform;
        private float sweepAngle;
        private Texture2D vignetteTexture;
        private Texture2D scanlineTexture;
        private Texture2D framePixel;

        private readonly List<GameObject> arrowPool = new List<GameObject>();
        private readonly List<GameObject> spherePool = new List<GameObject>();
        private readonly Dictionary<GameObject, ITrackable> markerToTrackable = new Dictionary<GameObject, ITrackable>();
        private readonly Dictionary<ITrackable, GameObject> activeMarkers = new Dictionary<ITrackable, GameObject>();
        private readonly HashSet<ITrackable> seen = new HashSet<ITrackable>();
        private readonly List<ITrackable> stale = new List<ITrackable>();

        private void Awake()
        {
            arrowMesh = BuildArrowMesh();
            BuildScene();
            BuildOverlayTextures();
        }

        private void BuildScene()
        {
            GameObject rootObject = new GameObject("BS Radar Pocket Root");
            rootObject.transform.position = new Vector3(0f, PocketOriginHeight, 0f);
            pocketRoot = rootObject.transform;

            GameObject gimbalObject = new GameObject("BS Radar Gimbal");
            gimbalObject.transform.SetParent(pocketRoot, false);
            gimbalObject.transform.localRotation = Quaternion.Euler(25f, 0f, 0f);
            gimbal = gimbalObject.transform;

            GameObject cameraObject = new GameObject("BS Radar Camera");
            cameraObject.transform.SetParent(gimbal, false);
            cameraObject.transform.localPosition = new Vector3(0f, 0f, -9f);
            cameraObject.transform.localRotation = Quaternion.identity;
            radarCamera = cameraObject.AddComponent<Camera>();
            radarCamera.fieldOfView = 45f;
            radarCamera.clearFlags = CameraClearFlags.SolidColor;
            // Deep teal-navy "hologram void" instead of a flat near-black — still dark enough for
            // the grid/markers to read clearly, but reads as a projected display, not a dead screen.
            radarCamera.backgroundColor = new Color(0.012f, 0.045f, 0.075f);
            radarCamera.nearClipPlane = 0.1f;
            radarCamera.farClipPlane = 100f;
            radarTexture = new RenderTexture(RadarTextureSize, RadarTextureSize, 16);
            radarTexture.antiAliasing = RadarTextureAntiAliasing;
            radarTexture.filterMode = FilterMode.Bilinear;
            radarCamera.targetTexture = radarTexture;
            radarCamera.enabled = false;

            BuildGlow();
            BuildGrid();
            BuildSweep();
            BuildLockIcon();
        }

        private void BuildGrid()
        {
            GameObject gridObject = new GameObject("BS Radar Grid");
            gridObject.transform.SetParent(pocketRoot, false);
            gridObject.transform.localPosition = Vector3.zero;
            gridObject.transform.localRotation = Quaternion.identity;
            gridTransform = gridObject.transform;

            List<Vector3> vertices = new List<Vector3>();
            List<int> lines = new List<int>();

            for (int ring = 1; ring <= RingCount; ring++)
            {
                float r = DisplayRadius * ring / RingCount;
                int start = vertices.Count;
                for (int seg = 0; seg < SegmentCount; seg++)
                {
                    float angle = seg * Mathf.PI * 2f / SegmentCount;
                    vertices.Add(new Vector3(r * Mathf.Cos(angle), 0f, r * Mathf.Sin(angle)));
                }
                for (int seg = 0; seg < SegmentCount; seg++)
                {
                    lines.Add(start + seg);
                    lines.Add(start + (seg + 1) % SegmentCount);
                }
            }

            int spokeStart = vertices.Count;
            vertices.Add(new Vector3(-DisplayRadius, 0f, 0f));
            vertices.Add(new Vector3(DisplayRadius, 0f, 0f));
            vertices.Add(new Vector3(0f, 0f, -DisplayRadius));
            vertices.Add(new Vector3(0f, 0f, DisplayRadius));
            lines.Add(spokeStart);
            lines.Add(spokeStart + 1);
            lines.Add(spokeStart + 2);
            lines.Add(spokeStart + 3);

            Mesh mesh = new Mesh();
            mesh.vertices = vertices.ToArray();
            mesh.SetIndices(lines.ToArray(), MeshTopology.Lines, 0);
            mesh.RecalculateBounds();

            MeshFilter filter = gridObject.AddComponent<MeshFilter>();
            filter.sharedMesh = mesh;
            MeshRenderer renderer = gridObject.AddComponent<MeshRenderer>();
            renderer.material = new Material(Shader.Find("Particles/Additive"));
            renderer.material.SetColor("_TintColor", new Color(0.3f, 0.85f, 0.95f, 0.55f));
        }

        // Both the glow disc and the sweep beam below add every triangle in both winding orders,
        // so they render correctly regardless of which side "Particles/Additive" treats as front —
        // cheap insurance for a purely decorative mesh we can't preview outside the game.
        private static void AddFanTriangle(List<int> triangles, int center, int a, int b)
        {
            triangles.Add(center);
            triangles.Add(a);
            triangles.Add(b);
            triangles.Add(center);
            triangles.Add(b);
            triangles.Add(a);
        }

        // A soft, additively-blended radial glow sitting just under the grid plane so the display
        // reads as a glowing dome rather than markers floating over a flat void.
        private void BuildGlow()
        {
            GameObject glowObject = new GameObject("BS Radar Glow");
            glowObject.transform.SetParent(pocketRoot, false);
            glowObject.transform.localPosition = new Vector3(0f, -0.03f, 0f);
            glowObject.transform.localRotation = Quaternion.identity;

            Color core = new Color(0.12f, 0.5f, 0.7f, 0.4f);
            Color rim = new Color(0.12f, 0.5f, 0.7f, 0f);

            List<Vector3> vertices = new List<Vector3>();
            List<Color> colors = new List<Color>();
            List<int> triangles = new List<int>();

            vertices.Add(Vector3.zero);
            colors.Add(core);
            for (int seg = 0; seg <= SegmentCount; seg++)
            {
                float angle = seg * Mathf.PI * 2f / SegmentCount;
                vertices.Add(new Vector3(GlowRadius * Mathf.Cos(angle), 0f, GlowRadius * Mathf.Sin(angle)));
                colors.Add(rim);
            }
            for (int seg = 0; seg < SegmentCount; seg++)
            {
                AddFanTriangle(triangles, 0, 1 + seg, 1 + seg + 1);
            }

            Mesh mesh = new Mesh();
            mesh.vertices = vertices.ToArray();
            mesh.colors = colors.ToArray();
            mesh.triangles = triangles.ToArray();
            mesh.RecalculateBounds();

            MeshFilter filter = glowObject.AddComponent<MeshFilter>();
            filter.sharedMesh = mesh;
            MeshRenderer renderer = glowObject.AddComponent<MeshRenderer>();
            renderer.material = new Material(Shader.Find("Particles/Additive"));
        }

        // A slowly rotating sweep wedge — the classic radar/hologram "scanning" cue — built as a
        // fan whose vertex alpha fades from bright at the leading edge to transparent at the
        // trailing edge. sweepTransform's yaw is advanced continuously in UpdateSweep().
        private void BuildSweep()
        {
            GameObject sweepObject = new GameObject("BS Radar Sweep");
            sweepObject.transform.SetParent(pocketRoot, false);
            sweepObject.transform.localPosition = new Vector3(0f, 0.01f, 0f);
            sweepObject.transform.localRotation = Quaternion.identity;
            sweepTransform = sweepObject.transform;

            Color leading = new Color(0.55f, 0.95f, 0.95f, 0.5f);
            Color trailing = new Color(0.55f, 0.95f, 0.95f, 0f);
            const int sweepSegments = 20;

            List<Vector3> vertices = new List<Vector3>();
            List<Color> colors = new List<Color>();
            List<int> triangles = new List<int>();

            vertices.Add(Vector3.zero);
            colors.Add(leading);
            for (int seg = 0; seg <= sweepSegments; seg++)
            {
                float t = (float)seg / sweepSegments;
                float angle = -t * SweepArcDegrees * Mathf.Deg2Rad;
                vertices.Add(new Vector3(DisplayRadius * Mathf.Cos(angle), 0f, DisplayRadius * Mathf.Sin(angle)));
                colors.Add(Color.Lerp(leading, trailing, t));
            }
            for (int seg = 0; seg < sweepSegments; seg++)
            {
                AddFanTriangle(triangles, 0, 1 + seg, 1 + seg + 1);
            }

            Mesh mesh = new Mesh();
            mesh.vertices = vertices.ToArray();
            mesh.colors = colors.ToArray();
            mesh.triangles = triangles.ToArray();
            mesh.RecalculateBounds();

            MeshFilter filter = sweepObject.AddComponent<MeshFilter>();
            filter.sharedMesh = mesh;
            MeshRenderer renderer = sweepObject.AddComponent<MeshRenderer>();
            renderer.material = new Material(Shader.Find("Particles/Additive"));
        }

        private void UpdateSweep()
        {
            if (sweepTransform == null)
            {
                return;
            }
            sweepAngle += Time.deltaTime * SweepSpeedDegPerSec;
            if (sweepAngle > 360f)
            {
                sweepAngle -= 360f;
            }
            sweepTransform.localRotation = Quaternion.Euler(0f, sweepAngle, 0f);
        }

        // Procedural textures for the OnGUI overlay pass (DrawRadarTexture/DrawPanelFrame) — a
        // radial vignette, a repeating scanline, and a 1x1 solid pixel for stretching into frame
        // bars. Generated once; none of this depends on any shader beyond Unity's built-in GUI blit.
        private void BuildOverlayTextures()
        {
            vignetteTexture = new Texture2D(VignetteTextureSize, VignetteTextureSize, TextureFormat.ARGB32, false);
            vignetteTexture.wrapMode = TextureWrapMode.Clamp;
            Vector2 center = new Vector2((VignetteTextureSize - 1) * 0.5f, (VignetteTextureSize - 1) * 0.5f);
            float maxDist = center.magnitude;
            for (int y = 0; y < VignetteTextureSize; y++)
            {
                for (int x = 0; x < VignetteTextureSize; x++)
                {
                    float dist = Vector2.Distance(new Vector2(x, y), center) / maxDist;
                    float alpha = Mathf.Clamp01(Mathf.Pow(dist, 2.2f)) * 0.65f;
                    vignetteTexture.SetPixel(x, y, new Color(0f, 0.02f, 0.05f, alpha));
                }
            }
            vignetteTexture.Apply();

            scanlineTexture = new Texture2D(1, 4, TextureFormat.ARGB32, false);
            scanlineTexture.wrapMode = TextureWrapMode.Repeat;
            scanlineTexture.filterMode = FilterMode.Point;
            scanlineTexture.SetPixel(0, 0, new Color(0.6f, 0.95f, 1f, 0.10f));
            scanlineTexture.SetPixel(0, 1, new Color(0f, 0f, 0f, 0f));
            scanlineTexture.SetPixel(0, 2, new Color(0f, 0f, 0f, 0f));
            scanlineTexture.SetPixel(0, 3, new Color(0f, 0f, 0f, 0f));
            scanlineTexture.Apply();

            framePixel = new Texture2D(1, 1, TextureFormat.ARGB32, false);
            framePixel.SetPixel(0, 0, Color.white);
            framePixel.Apply();
        }

        private void BuildLockIcon()
        {
            lockIcon = GameObject.CreatePrimitive(PrimitiveType.Quad);
            lockIcon.name = "BS Radar Lock Icon";
            Object.DestroyImmediate(lockIcon.GetComponent<Collider>());
            lockIcon.transform.SetParent(pocketRoot, false);
            lockIcon.transform.localScale = Vector3.one * 0.5f;
            MeshRenderer renderer = lockIcon.GetComponent<MeshRenderer>();
            renderer.material = new Material(Shader.Find("Particles/Additive"));
            renderer.material.mainTexture = ModResource.GetTexture("BS Migrated AA Lock Texture").Texture;
            lockIcon.SetActive(false);
        }

        private static Mesh BuildArrowMesh()
        {
            Mesh mesh = new Mesh();
            Vector3[] vertices = new Vector3[]
            {
                new Vector3(0f, 0f, 0.5f),
                new Vector3(0.25f, 0f, -0.25f),
                new Vector3(0f, 0f, -0.1f),
                new Vector3(-0.25f, 0f, -0.25f)
            };
            int[] triangles = new int[] { 0, 1, 2, 0, 2, 3 };
            mesh.vertices = vertices;
            mesh.triangles = triangles;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private Material MarkerMaterial()
        {
            if (markerMaterial == null)
            {
                Shader shader = Shader.Find("Unlit/Color");
                if (shader == null)
                {
                    shader = Shader.Find("Particles/Additive");
                }

                markerMaterial = new Material(shader);
            }

            return markerMaterial;
        }

        private static void SetRendererColor(Renderer renderer, Color color)
        {
            if (renderer == null || renderer.material == null)
            {
                return;
            }

            if (renderer.material.HasProperty("_Color"))
            {
                renderer.material.SetColor("_Color", color);
            }
            if (renderer.material.HasProperty("_TintColor"))
            {
                renderer.material.SetColor("_TintColor", color);
            }
        }

        private GameObject CreateMarker(TrackKind kind)
        {
            GameObject marker;
            if (kind == TrackKind.Ship)
            {
                marker = new GameObject("BS Radar Ship Marker");
                MeshFilter filter = marker.AddComponent<MeshFilter>();
                filter.sharedMesh = arrowMesh;
                MeshRenderer renderer = marker.AddComponent<MeshRenderer>();
                renderer.material = MarkerMaterial();
            }
            else
            {
                marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                marker.name = "BS Radar Missile Marker";
                Object.DestroyImmediate(marker.GetComponent<Collider>());
                marker.GetComponent<Renderer>().material = MarkerMaterial();
            }

            marker.transform.SetParent(pocketRoot, false);
            marker.transform.localScale = Vector3.one * MarkerScale;
            SphereCollider collider = marker.AddComponent<SphereCollider>();
            collider.radius = MarkerColliderRadius;
            marker.SetActive(false);
            return marker;
        }

        private static ShipState OwnShipState()
        {
            int localPlayer = StatMaster.isMP ? PlayerData.localPlayer.networkId : 0;
            return SpaceCombatRegistry.FindShip(localPlayer);
        }

        private GameObject GetOrCreateMarker(ITrackable target, TrackKind kind)
        {
            GameObject marker;
            if (activeMarkers.TryGetValue(target, out marker))
            {
                return marker;
            }

            List<GameObject> pool = kind == TrackKind.Ship ? arrowPool : spherePool;
            for (int i = 0; i < pool.Count; i++)
            {
                GameObject pooled = pool[i];
                if (!pooled.activeSelf)
                {
                    markerToTrackable[pooled] = target;
                    activeMarkers[target] = pooled;
                    return pooled;
                }
            }

            GameObject created = CreateMarker(kind);
            pool.Add(created);
            markerToTrackable[created] = target;
            activeMarkers[target] = created;
            return created;
        }

        private void HideAllMarkers()
        {
            for (int i = 0; i < arrowPool.Count; i++)
            {
                arrowPool[i].SetActive(false);
            }
            for (int i = 0; i < spherePool.Count; i++)
            {
                spherePool[i].SetActive(false);
            }
            activeMarkers.Clear();
            lockIcon.SetActive(false);
        }

        public ITrackable TrackableFor(GameObject marker)
        {
            ITrackable target;
            markerToTrackable.TryGetValue(marker, out target);
            return target;
        }

        // Snaps the continuous, scroll-driven MetersPerUnit to the nearest "nice" ring step from
        // the geometric family RingStepBase * RingStepRatio^n (10, 50, 250, 1250, ...), so the grid
        // always reads in round numbers, Blender-viewport style, instead of an arbitrary float.
        private float ComputeRingStepMeters()
        {
            float desiredRingStep = MetersPerUnit * DisplayRadius / RingCount;
            float n = Mathf.Round(Mathf.Log(desiredRingStep / RingStepBase, RingStepRatio));
            return RingStepBase * Mathf.Pow(RingStepRatio, n);
        }

        // What MetersPerUnit would need to be for the current ring step to exactly fill the grid's
        // built radius — used only to derive the grid's continuous visual scale below. Markers are
        // NOT positioned from this; they use the raw, unsnapped MetersPerUnit so they always sit at
        // their true relative distance while the grid smoothly grows/shrinks underneath them.
        private float EffectiveMetersPerUnit()
        {
            return ComputeRingStepMeters() * RingCount / DisplayRadius;
        }

        // Grid mesh is built once at a fixed radius meaning "RingStepBase * RingStepRatio^n" per
        // ring; its uniform scale continuously tracks how far the raw zoom has drifted from that
        // built meaning, so it visibly grows/shrinks as the player scrolls. The scale jumps by
        // exactly RingStepRatio the instant the ring step itself snaps to the next tier, which is
        // the Blender-style "grid resets size right as the labels change" sensation.
        private void UpdateGridScale()
        {
            if (gridTransform == null)
            {
                return;
            }
            float scale = EffectiveMetersPerUnit() / MetersPerUnit;
            gridTransform.localScale = Vector3.one * scale;
        }

        private static Vector3 CaptainLocalToRadarSpace(Vector3 local)
        {
            return new Vector3(local.x, local.z, -local.y);
        }

        private static Vector3 ProjectDirectionToRadarPlane(Vector3 radarDirection)
        {
            return new Vector3(radarDirection.x, 0f, radarDirection.z);
        }

        private void SyncMarkers()
        {
            ShipState ship = OwnShipState();
            if (ship == null || ship.Captain == null)
            {
                HideAllMarkers();
                return;
            }

            Transform captain = ship.Captain.transform;
            seen.Clear();

            if (ship.Core != null)
            {
                seen.Add(ship.Core);
                GameObject selfMarker = GetOrCreateMarker(ship.Core, TrackKind.Ship);
                selfMarker.SetActive(true);
                selfMarker.transform.localPosition = Vector3.zero;
                selfMarker.transform.localRotation = Quaternion.identity;
                selfMarker.transform.localScale = Vector3.one * MarkerScale * OriginMarkerScale;
                SetRendererColor(selfMarker.GetComponent<Renderer>(), Color.green);
            }

            for (int i = 0; i < ship.Tracks.Count; i++)
            {
                SensorTrack track = ship.Tracks[i];
                if (track.Kind != TrackKind.Ship && track.Kind != TrackKind.HeavyMissile)
                {
                    continue;
                }

                seen.Add(track.Target);
                GameObject marker = GetOrCreateMarker(track.Target, track.Kind);
                marker.SetActive(true);
                marker.transform.localScale = Vector3.one * MarkerScale;

                Vector3 relative = CaptainLocalToRadarSpace(captain.InverseTransformVector(track.Position - captain.position));
                Vector3 displayPos = relative / MetersPerUnit;
                if (displayPos.magnitude > DisplayRadius)
                {
                    displayPos = displayPos.normalized * DisplayRadius;
                }
                marker.transform.localPosition = displayPos;

                if (track.Kind == TrackKind.Ship)
                {
                    Vector3 velLocal = ProjectDirectionToRadarPlane(CaptainLocalToRadarSpace(captain.InverseTransformDirection(track.Velocity)));
                    marker.transform.localRotation = velLocal.sqrMagnitude > 0.01f
                        ? Quaternion.LookRotation(velLocal.normalized, Vector3.up)
                        : Quaternion.identity;
                }

                Color color = SpaceBallistics.IsHostile(ship.Captain, track.Target) ? Color.red : Color.green;
                SetRendererColor(marker.GetComponent<Renderer>(), color);
            }

            stale.Clear();
            foreach (KeyValuePair<ITrackable, GameObject> pair in activeMarkers)
            {
                if (!seen.Contains(pair.Key))
                {
                    pair.Value.SetActive(false);
                    stale.Add(pair.Key);
                }
            }
            for (int i = 0; i < stale.Count; i++)
            {
                activeMarkers.Remove(stale[i]);
            }

            GameObject lockedMarker;
            if (ship.LockedTarget != null && activeMarkers.TryGetValue(ship.LockedTarget, out lockedMarker))
            {
                lockIcon.SetActive(true);
                lockIcon.transform.localPosition = lockedMarker.transform.localPosition;
                // Billboard toward the orbiting radar camera every frame — the quad's own rotation
                // never changes otherwise, so it would render edge-on/backwards once the player
                // orbits away from whatever angle it happened to be built facing.
                lockIcon.transform.rotation = radarCamera.transform.rotation;
            }
            else
            {
                lockIcon.SetActive(false);
            }
        }

        public void SetOpen(bool open)
        {
            IsOpen = open;
            if (!open)
            {
                orbiting = false;
                leftDownInRect = false;
                SetRadarOwnsMouse(false);
            }
            if (radarCamera != null)
            {
                radarCamera.enabled = open;
            }
        }

        private static Rect GetPanelRect()
        {
            return new Rect(Screen.width - PanelSize - 20f, Screen.height - PanelSize - 20f, PanelSize, PanelSize);
        }

        private static Vector2 FlippedMouse()
        {
            return new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
        }

        private void Update()
        {
            if (!IsOpen)
            {
                SetRadarOwnsMouse(false);
                return;
            }

            Rect rect = GetPanelRect();
            SetRadarOwnsMouse(MouseIsOwnedByRadar(rect));
            HandleOrbitAndZoom(rect);
            UpdateGridScale();
            UpdateSweep();
            HandleClick(rect);
            SyncMarkers();
        }

        private void LateUpdate()
        {
            if (cameraSnapshotActive && radarOwnsMouse)
            {
                RestoreGameCameraSnapshot();
            }
        }

        private bool MouseIsOwnedByRadar(Rect rect)
        {
            return rect.Contains(FlippedMouse()) || orbiting || leftDownInRect;
        }

        private void SetRadarOwnsMouse(bool ownsMouse)
        {
            if (radarOwnsMouse == ownsMouse)
            {
                return;
            }

            radarOwnsMouse = ownsMouse;
            if (radarOwnsMouse)
            {
                CaptureGameCameraSnapshot();
                PauseGameCameraInput();
            }
            else
            {
                RestoreGameCameraSnapshot();
                cameraSnapshotActive = false;
                ResumeGameCameraInput();
            }
        }

        private void CaptureGameCameraSnapshot()
        {
            Camera gameCamera = Camera.main;
            if (gameCamera == null || gameCamera == radarCamera)
            {
                cameraSnapshotActive = false;
                guardedGameCamera = null;
                return;
            }

            guardedGameCamera = gameCamera;
            guardedCameraPosition = gameCamera.transform.position;
            guardedCameraRotation = gameCamera.transform.rotation;
            guardedCameraFieldOfView = gameCamera.fieldOfView;
            guardedCameraOrthographicSize = gameCamera.orthographicSize;
            cameraSnapshotActive = true;
        }

        private void RestoreGameCameraSnapshot()
        {
            if (!cameraSnapshotActive || guardedGameCamera == null)
            {
                return;
            }

            guardedGameCamera.transform.position = guardedCameraPosition;
            guardedGameCamera.transform.rotation = guardedCameraRotation;
            guardedGameCamera.fieldOfView = guardedCameraFieldOfView;
            guardedGameCamera.orthographicSize = guardedCameraOrthographicSize;
        }

        private void PauseGameCameraInput()
        {
            guardedMouseOrbit = FindGameMouseOrbit();
            if (guardedMouseOrbit == null)
            {
                return;
            }

            mouseOrbitWasEnabled = guardedMouseOrbit.enabled;
            if (mouseOrbitWasEnabled)
            {
                guardedMouseOrbit.enabled = false;
                mouseOrbitPaused = true;
            }
        }

        private void ResumeGameCameraInput()
        {
            if (mouseOrbitPaused && guardedMouseOrbit != null)
            {
                guardedMouseOrbit.enabled = mouseOrbitWasEnabled;
            }

            mouseOrbitPaused = false;
            guardedMouseOrbit = null;
        }

        private MouseOrbit FindGameMouseOrbit()
        {
            Camera gameCamera = Camera.main;
            if (gameCamera != null && gameCamera != radarCamera)
            {
                MouseOrbit orbit = gameCamera.GetComponent<MouseOrbit>();
                if (orbit != null)
                {
                    return orbit;
                }
            }

            return Object.FindObjectOfType(typeof(MouseOrbit)) as MouseOrbit;
        }

        private void HandleOrbitAndZoom(Rect rect)
        {
            bool overRect = rect.Contains(FlippedMouse());

            if (Input.GetMouseButtonDown(1) && overRect)
            {
                orbiting = true;
            }
            if (Input.GetMouseButtonUp(1))
            {
                orbiting = false;
            }
            if (orbiting)
            {
                yaw += Input.GetAxis("Mouse X") * OrbitSpeed;
                pitch = Mathf.Clamp(pitch - Input.GetAxis("Mouse Y") * OrbitSpeed, -85f, 85f);
                gimbal.localRotation = Quaternion.Euler(pitch, yaw, 0f);
            }

            if (overRect)
            {
                float scroll = Input.GetAxis("Mouse ScrollWheel");
                if (scroll > 0.0001f)
                {
                    MetersPerUnit = Mathf.Clamp(MetersPerUnit / ZoomStep, MinMetersPerUnit, MaxMetersPerUnit);
                }
                else if (scroll < -0.0001f)
                {
                    MetersPerUnit = Mathf.Clamp(MetersPerUnit * ZoomStep, MinMetersPerUnit, MaxMetersPerUnit);
                }
            }
        }

        private void HandleClick(Rect rect)
        {
            bool overRect = rect.Contains(FlippedMouse());

            if (Input.GetMouseButtonDown(0) && overRect)
            {
                mouseDownPos = Input.mousePosition;
                leftDownInRect = true;
            }
            if (Input.GetMouseButtonUp(0))
            {
                bool wasDownInRect = leftDownInRect;
                leftDownInRect = false;
                if (wasDownInRect && overRect && Vector2.Distance(mouseDownPos, Input.mousePosition) < ClickPixelThreshold)
                {
                    TryLockAtMouse(rect);
                }
            }
        }

        private void TryLockAtMouse(Rect rect)
        {
            ShipState ship = OwnShipState();
            if (ship == null || ship.Captain == null || radarCamera == null)
            {
                return;
            }

            Vector2 mouse = FlippedMouse();
            float fracX = (mouse.x - rect.x) / rect.width;
            float fracY = (mouse.y - rect.y) / rect.height;
            Vector3 rtPoint = new Vector3(fracX * radarCamera.pixelWidth, (1f - fracY) * radarCamera.pixelHeight, 0f);
            Ray ray = radarCamera.ScreenPointToRay(rtPoint);

            ITrackable target = FindLockableTarget(ray, ship);
            if (target == null)
            {
                return;
            }

            bool willLock = !ReferenceEquals(ship.LockedTarget, target);
            ship.LockedTarget = willLock ? target : null;

            ILockable lockable = (ILockable)target;
            ModNetworking.SendToAll(CaptainLockNet.LockMsg.CreateMessage(ship.Captain.PlayerID, target.PlayerID, lockable.GuidHash, willLock));
        }

        // A click point can land on more than one overlapping marker collider — e.g. a missile
        // about to hit its target sits almost exactly on top of the ship it's homing on. A plain
        // Physics.Raycast only reports the single closest hit, so if that happens to be a target
        // that can't be locked (our own ship's self marker; anything not ILockable), the click
        // would silently do nothing even though a lockable target was right behind it. Walk every
        // hit closest-first and take the nearest one that's actually lockable instead.
        private ITrackable FindLockableTarget(Ray ray, ShipState ship)
        {
            RaycastHit[] hits = Physics.RaycastAll(ray, 100f);
            ITrackable best = null;
            float bestDistance = float.MaxValue;
            for (int i = 0; i < hits.Length; i++)
            {
                if (hits[i].distance >= bestDistance)
                {
                    continue;
                }

                ITrackable candidate = TrackableFor(hits[i].collider.gameObject);
                if (candidate == null || !candidate.IsAlive)
                {
                    continue;
                }
                if (ReferenceEquals(candidate, ship.Core))
                {
                    continue;
                }
                if (!(candidate is ILockable))
                {
                    continue;
                }

                best = candidate;
                bestDistance = hits[i].distance;
            }
            return best;
        }

        private void OnGUI()
        {
            if (!IsOpen || StatMaster.hudHidden || radarTexture == null)
            {
                return;
            }

            Rect rect = GetPanelRect();
            ConsumePanelMouseEvent(rect);
            DrawRadarTexture(rect);
            DrawPanelFrame(rect);
            float ringMeters = ComputeRingStepMeters();
            GUI.Label(new Rect(rect.x, rect.y - 20f, rect.width, 20f), "1 ring = " + ringMeters.ToString("0") + "m");
        }

        private void DrawRadarTexture(Rect rect)
        {
            Color previousColor = GUI.color;
            GUI.color = new Color(0.55f, 0.9f, 1f, RadarBloomAlpha);
            DrawRadarBloomPass(rect, -RadarBloomOffset, 0f);
            DrawRadarBloomPass(rect, RadarBloomOffset, 0f);
            DrawRadarBloomPass(rect, 0f, -RadarBloomOffset);
            DrawRadarBloomPass(rect, 0f, RadarBloomOffset);
            GUI.color = new Color(0.55f, 0.9f, 1f, RadarBloomAlpha * 0.6f);
            DrawRadarBloomPass(rect, -RadarBloomOffset, -RadarBloomOffset);
            DrawRadarBloomPass(rect, RadarBloomOffset, -RadarBloomOffset);
            DrawRadarBloomPass(rect, -RadarBloomOffset, RadarBloomOffset);
            DrawRadarBloomPass(rect, RadarBloomOffset, RadarBloomOffset);
            GUI.color = previousColor;
            GUI.DrawTexture(rect, radarTexture);

            // CRT-style scanlines (tiled) and a radial vignette on top of the raw render — sells
            // the "projected display" read instead of a flat photo of the pocket scene.
            GUI.color = Color.white;
            GUI.DrawTextureWithTexCoords(rect, scanlineTexture, new Rect(0f, 0f, 1f, rect.height / 4f));
            GUI.DrawTexture(rect, vignetteTexture);
            GUI.color = previousColor;
        }

        private void DrawRadarBloomPass(Rect rect, float xOffset, float yOffset)
        {
            GUI.DrawTexture(new Rect(rect.x + xOffset, rect.y + yOffset, rect.width, rect.height), radarTexture);
        }

        // Thin glowing border with brighter corner accents around the panel rect, reading as a
        // projector frame rather than a bare texture floating over the game view.
        private void DrawPanelFrame(Rect rect)
        {
            const float border = 2f;
            const float cornerSize = 10f;
            Color edgeColor = new Color(0.35f, 0.85f, 1f, 0.7f);
            Color cornerColor = new Color(0.75f, 1f, 1f, 0.95f);
            Color previousColor = GUI.color;

            GUI.color = edgeColor;
            GUI.DrawTexture(new Rect(rect.x - border, rect.y - border, rect.width + border * 2f, border), framePixel);
            GUI.DrawTexture(new Rect(rect.x - border, rect.yMax, rect.width + border * 2f, border), framePixel);
            GUI.DrawTexture(new Rect(rect.x - border, rect.y - border, border, rect.height + border * 2f), framePixel);
            GUI.DrawTexture(new Rect(rect.xMax, rect.y - border, border, rect.height + border * 2f), framePixel);

            GUI.color = cornerColor;
            GUI.DrawTexture(new Rect(rect.x - border, rect.y - border, cornerSize, border), framePixel);
            GUI.DrawTexture(new Rect(rect.x - border, rect.y - border, border, cornerSize), framePixel);
            GUI.DrawTexture(new Rect(rect.xMax - cornerSize + border, rect.y - border, cornerSize, border), framePixel);
            GUI.DrawTexture(new Rect(rect.xMax, rect.y - border, border, cornerSize), framePixel);
            GUI.DrawTexture(new Rect(rect.x - border, rect.yMax, cornerSize, border), framePixel);
            GUI.DrawTexture(new Rect(rect.x - border, rect.yMax - cornerSize + border, border, cornerSize), framePixel);
            GUI.DrawTexture(new Rect(rect.xMax - cornerSize + border, rect.yMax, cornerSize, border), framePixel);
            GUI.DrawTexture(new Rect(rect.xMax, rect.yMax - cornerSize + border, border, cornerSize), framePixel);

            GUI.color = previousColor;
        }

        private void ConsumePanelMouseEvent(Rect rect)
        {
            Event evt = Event.current;
            if (evt == null)
            {
                return;
            }

            bool overPanel = rect.Contains(evt.mousePosition);
            bool activeDrag = orbiting || leftDownInRect;
            if (!overPanel && !activeDrag)
            {
                return;
            }

            if (evt.type == EventType.MouseDown
                || evt.type == EventType.MouseUp
                || evt.type == EventType.MouseDrag
                || evt.type == EventType.ScrollWheel)
            {
                evt.Use();
            }
        }

        private void OnDestroy()
        {
            SetRadarOwnsMouse(false);
        }
    }
}
