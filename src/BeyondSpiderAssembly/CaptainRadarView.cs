using System.Collections.Generic;
using Modding;
using UnityEngine;
using UnityEngine.UI;

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
        // Missile blips are sized per size-tier rather than literally by physical Radius — the three
        // tiers' radii (3/5/10, see MissileLauncherAssets.MissileRadius) don't read as a clean visual
        // progression on the tiny radar display, so heavy/medium/small markers instead sit in a fixed
        // 5:2:1 ratio. Thresholds sit at the midpoints between those three radii so a track still
        // classifies correctly if that table gets retuned a little.
        private const float MissileMarkerSmallMaxRadius = 4f;
        private const float MissileMarkerMediumMaxRadius = 7.5f;
        private const float MissileMarkerHeavyFactor = 1.25f;
        private const float MissileMarkerMediumFactor = 0.5f;
        private const float MissileMarkerSmallFactor = 0.25f;
        // Lock reticle size as a multiple of the locked marker's scale — >1 so it always encircles the
        // blip. 1.7 reproduces the old fixed 0.5 reticle for a default 0.3 marker while growing for big ones.
        private const float LockIconMarkerScaleRatio = 1.7f;
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
        private const int VignetteTextureSize = 64;
        private const int RippleCount = 3;
        private const float RippleCycleSeconds = 2.4f;
        private const float RippleMinScale = 0.15f;
        private const float RippleBaseAlpha = 0.18f;
        private const float RippleYOffset = 0.015f;
        private const float DockMargin = 20f;
        private const float DockHeaderWidth = 140f;
        private const float DockHeaderHeight = 26f;
        private const float DockGap = 4f;
        private const float DockAnimationSeconds = 0.18f;
        // Fire-channel tab strip (ADR-0010), drawn along the panel's top-left edge: one tab per
        // channel in that channel's color; the active tab decides which channel a radar click
        // (un)locks. Dimmed lock reticles keep showing the other channels' locks underneath.
        private const float TabWidth = 34f;
        private const float TabHeight = 22f;
        private const float TabGap = 6f;
        private const float TabMargin = 8f;
        private const float ActiveLockIconAlpha = 0.95f;
        private const float InactiveLockIconAlpha = 0.3f;
        // Nest multiple reticles on one blip: each higher channel's ring is a bit larger, so a
        // target locked by several channels shows concentric colored rings instead of one quad
        // z-fighting another.
        private const float LockIconChannelScaleStep = 0.22f;

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
        private readonly GameObject[] lockIcons = new GameObject[FireChannels.Count];
        private int activeChannel;
        private GUIStyle tabLabelStyle;
        private Mesh arrowMesh;
        private Mesh rippleRingMesh;
        private Transform[] rippleTransforms;
        private Renderer[] rippleRenderers;
        private float rippleTime;
        private Texture2D vignetteTexture;
        private Texture2D scanlineTexture;
        private Texture2D framePixel;

        private bool collapsed;
        private float dockProgress;
        private RectTransform radarBody;
        private Toggle radarHeaderToggle;
        private Text radarHeaderText;

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
            try
            {
                BuildRadarDock();
            }
            catch (System.Exception ex)
            {
                radarBody = null;
                Debug.LogWarning("BeyondSpider: radar dock failed to build, falling back to legacy frame. " + ex);
            }
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
            BuildRipples();
            BuildLockIcons();
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

        // A soft ring built once at unit radius (three vertex rings — inner/mid/outer — so it fades
        // in on the way out from the center and fades out again at its own leading edge, no hard
        // cutoff either way). RippleCount instances of this same shared mesh are scaled up from the
        // center to DisplayRadius on a loop in UpdateRipples(), the classic sonar-ping cue.
        private Mesh BuildRippleRingMesh()
        {
            const float innerFrac = 0.75f;
            const float midFrac = 0.9f;
            const float outerFrac = 1f;
            Color edge = new Color(0.55f, 0.95f, 0.95f, 0f);
            Color peak = new Color(0.55f, 0.95f, 0.95f, 1f);

            List<Vector3> vertices = new List<Vector3>();
            List<Color> colors = new List<Color>();
            List<int> triangles = new List<int>();

            for (int seg = 0; seg <= SegmentCount; seg++)
            {
                float angle = seg * Mathf.PI * 2f / SegmentCount;
                float cos = Mathf.Cos(angle);
                float sin = Mathf.Sin(angle);
                vertices.Add(new Vector3(innerFrac * cos, 0f, innerFrac * sin));
                colors.Add(edge);
                vertices.Add(new Vector3(midFrac * cos, 0f, midFrac * sin));
                colors.Add(peak);
                vertices.Add(new Vector3(outerFrac * cos, 0f, outerFrac * sin));
                colors.Add(edge);
            }

            for (int seg = 0; seg < SegmentCount; seg++)
            {
                int a0 = seg * 3;
                int b0 = (seg + 1) * 3;
                AddFanTriangle(triangles, a0, a0 + 1, b0 + 1);
                AddFanTriangle(triangles, a0, b0 + 1, b0);
                AddFanTriangle(triangles, a0 + 1, a0 + 2, b0 + 2);
                AddFanTriangle(triangles, a0 + 1, b0 + 2, b0 + 1);
            }

            Mesh mesh = new Mesh();
            mesh.vertices = vertices.ToArray();
            mesh.colors = colors.ToArray();
            mesh.triangles = triangles.ToArray();
            mesh.RecalculateBounds();
            return mesh;
        }

        private void BuildRipples()
        {
            rippleRingMesh = BuildRippleRingMesh();
            rippleTransforms = new Transform[RippleCount];
            rippleRenderers = new Renderer[RippleCount];
            for (int i = 0; i < RippleCount; i++)
            {
                GameObject rippleObject = new GameObject("BS Radar Ripple " + i);
                rippleObject.transform.SetParent(pocketRoot, false);
                rippleObject.transform.localPosition = new Vector3(0f, RippleYOffset, 0f);
                rippleObject.transform.localRotation = Quaternion.identity;

                MeshFilter filter = rippleObject.AddComponent<MeshFilter>();
                filter.sharedMesh = rippleRingMesh;
                MeshRenderer renderer = rippleObject.AddComponent<MeshRenderer>();
                renderer.material = new Material(Shader.Find("Particles/Additive"));

                rippleTransforms[i] = rippleObject.transform;
                rippleRenderers[i] = renderer;
            }
        }

        private void UpdateRipples()
        {
            if (rippleTransforms == null)
            {
                return;
            }

            rippleTime += Time.deltaTime;
            for (int i = 0; i < rippleTransforms.Length; i++)
            {
                float phase = rippleTime / RippleCycleSeconds + (float)i / RippleCount;
                float t = phase - Mathf.Floor(phase);
                float radius = Mathf.Lerp(RippleMinScale, DisplayRadius, t);
                float alpha = (1f - t) * RippleBaseAlpha;

                rippleTransforms[i].localScale = Vector3.one * radius;
                rippleRenderers[i].material.SetColor("_TintColor", new Color(1f, 1f, 1f, alpha));
            }
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

        // One reticle quad per fire channel, tinted with the channel's color every frame in
        // SyncMarkers (active channel bright, others dimmed).
        private void BuildLockIcons()
        {
            for (int channel = 0; channel < FireChannels.Count; channel++)
            {
                GameObject icon = GameObject.CreatePrimitive(PrimitiveType.Quad);
                icon.name = "BS Radar Lock Icon " + channel;
                Object.DestroyImmediate(icon.GetComponent<Collider>());
                icon.transform.SetParent(pocketRoot, false);
                icon.transform.localScale = Vector3.one * 0.5f;
                MeshRenderer renderer = icon.GetComponent<MeshRenderer>();
                renderer.material = new Material(Shader.Find("Particles/Additive"));
                renderer.material.mainTexture = ModResource.GetTexture("BS Migrated AA Lock Texture").Texture;
                icon.SetActive(false);
                lockIcons[channel] = icon;
            }
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
            for (int channel = 0; channel < lockIcons.Length; channel++)
            {
                lockIcons[channel].SetActive(false);
            }
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

        // Ship markers stay a fixed size; a missile marker is sized by tier so the three missile sizes
        // show up as distinctly-sized dots. Only HeavyMissile tracks reach here besides ships (the
        // radar draws no other kind), so anything else falls back to MarkerScale.
        private static float MarkerScaleFor(SensorTrack track)
        {
            if (track.Kind != TrackKind.HeavyMissile || track.Target == null)
            {
                return MarkerScale;
            }
            float radius = track.Target.Radius;
            float factor = radius < MissileMarkerSmallMaxRadius ? MissileMarkerSmallFactor
                : radius < MissileMarkerMediumMaxRadius ? MissileMarkerMediumFactor
                : MissileMarkerHeavyFactor;
            return MarkerScale * factor;
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
                marker.transform.localScale = Vector3.one * MarkerScaleFor(track);

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

            // One reticle per fire channel, in the channel's color: the active tab's lock draws
            // bright, the other channels' locks stay visible but dimmed, so the player always
            // sees the whole fire-control picture regardless of which tab is selected.
            for (int channel = 0; channel < FireChannels.Count; channel++)
            {
                GameObject icon = lockIcons[channel];
                ITrackable target = ship.ChannelTargets[channel];
                GameObject lockedMarker;
                if (target == null || !target.IsAlive || !activeMarkers.TryGetValue(target, out lockedMarker))
                {
                    icon.SetActive(false);
                    continue;
                }

                icon.SetActive(true);
                icon.transform.localPosition = lockedMarker.transform.localPosition;
                // Size the reticle to the locked marker so it always frames the blip. A fixed size was
                // smaller than a scaled-up heavy-missile dot and vanished behind it; scaling a bit larger
                // than the marker keeps the reticle encircling whatever it's locked onto, big or small.
                // Higher channels ring slightly wider so several locks on one blip nest concentrically.
                float ratio = LockIconMarkerScaleRatio * (1f + LockIconChannelScaleStep * channel);
                icon.transform.localScale = Vector3.one * (lockedMarker.transform.localScale.x * ratio);
                // Billboard toward the orbiting radar camera every frame — the quad's own rotation
                // never changes otherwise, so it would render edge-on/backwards once the player
                // orbits away from whatever angle it happened to be built facing.
                icon.transform.rotation = radarCamera.transform.rotation;

                Color tint = FireChannels.Colors[channel];
                tint.a = channel == activeChannel ? ActiveLockIconAlpha : InactiveLockIconAlpha;
                SetRendererColor(icon.GetComponent<Renderer>(), tint);
            }
        }

        public void SetOpen(bool open)
        {
            IsOpen = open;
            if (radarHeaderToggle != null)
            {
                radarHeaderToggle.gameObject.SetActive(open);
            }
            if (radarBody != null)
            {
                radarBody.gameObject.SetActive(open);
            }
            if (open)
            {
                if (radarHeaderToggle != null)
                {
                    radarHeaderToggle.isOn = true;
                }
                SetCollapsed(false);
                // Every open re-plays the slide-up reveal from nothing, rather than resuming
                // wherever a previous session's animation left off.
                dockProgress = 0f;
                if (radarBody != null)
                {
                    radarBody.sizeDelta = new Vector2(PanelSize, 0f);
                }
            }
            else
            {
                orbiting = false;
                leftDownInRect = false;
                SetRadarOwnsMouse(false);
            }
            if (radarCamera != null)
            {
                radarCamera.enabled = open && !collapsed;
            }
        }

        // Bottom-right uGUI dock: a fixed header Toggle at the very corner, and a dark 420x420 Body
        // panel extending upward from it (so collapsing always leaves just the header hugging the
        // corner). IMGUI still draws all the live radar content — RenderTexture, bloom, ripples'
        // reflection in the label, mouse handling — on top of/within whatever rect the Body currently
        // occupies (see CurrentPanelRect/TryGetDockedRect below); this dock only owns position,
        // collapse state, and background chrome.
        private void BuildRadarDock()
        {
            Transform canvasRoot = BeyondSpiderUI.GetOrCreateRootCanvas();

            Text headerText;
            Toggle toggle = BeyondSpiderUI.CreateHeaderToggle(canvasRoot, "BS Radar Header", "▾ RADAR", out headerText);
            RectTransform headerRect = toggle.GetComponent<RectTransform>();
            headerRect.anchorMin = new Vector2(1f, 0f);
            headerRect.anchorMax = new Vector2(1f, 0f);
            headerRect.pivot = new Vector2(1f, 0f);
            headerRect.anchoredPosition = new Vector2(-DockMargin, DockMargin);
            headerRect.sizeDelta = new Vector2(DockHeaderWidth, DockHeaderHeight);
            radarHeaderToggle = toggle;
            radarHeaderText = headerText;

            Image bodyImage = BeyondSpiderUI.CreatePanel(canvasRoot, "BS Radar Body", BeyondSpiderUI.PanelColor);
            bodyImage.raycastTarget = false;
            RectTransform bodyRect = bodyImage.rectTransform;
            bodyRect.anchorMin = new Vector2(1f, 0f);
            bodyRect.anchorMax = new Vector2(1f, 0f);
            bodyRect.pivot = new Vector2(1f, 0f);
            bodyRect.anchoredPosition = new Vector2(-DockMargin, DockMargin + DockHeaderHeight + DockGap);
            // Height starts at 0, not PanelSize -- UpdateDockAnimation() grows it toward PanelSize
            // each time the radar opens, the sidebar-style slide-up reveal. Width stays fixed; only
            // height animates, since the bottom edge (pivot) is anchored just above the header and
            // growth reads as "sliding up from behind the header."
            bodyRect.sizeDelta = new Vector2(PanelSize, 0f);
            radarBody = bodyRect;

            toggle.onValueChanged.AddListener(OnHeaderToggleChanged);

            // Both start hidden -- IsOpen defaults false (matches the old IMGUI code, which simply
            // never drew anything until SetOpen(true), e.g. in build mode or before any captain has
            // simulated). Unlike IMGUI draws, these are real always-present GameObjects, so their
            // initial active state has to be set explicitly instead of just "not called this frame".
            toggle.gameObject.SetActive(false);
            bodyImage.gameObject.SetActive(false);
        }

        private void OnHeaderToggleChanged(bool expanded)
        {
            SetCollapsed(!expanded);
            if (radarHeaderText != null)
            {
                radarHeaderText.text = (expanded ? "▾ " : "▸ ") + "RADAR";
            }
        }

        private void SetCollapsed(bool value)
        {
            collapsed = value;
            // radarBody's own active state is only ever toggled by SetOpen (whole radar on/off);
            // while IsOpen it stays active permanently and UpdateDockAnimation's sizeDelta is what
            // actually shows/hides it, sidebar-style, instead of an instant pop.
            if (radarCamera != null)
            {
                radarCamera.enabled = IsOpen && !collapsed;
            }
            if (collapsed)
            {
                orbiting = false;
                leftDownInRect = false;
                SetRadarOwnsMouse(false);
            }
        }

        // Eases radarBody's height toward PanelSize (expanded) or 0 (collapsed) every frame —
        // unscaledDeltaTime so it keeps animating even if gameplay time is paused/slowed, matching
        // BeyondSpiderInfoPanel's equivalent. DockSettled() gates IMGUI's actual content draw
        // (RenderTexture/bloom/mouse handling) on the slide having finished, rather than drawing a
        // squished mid-transition texture into a rect that's mid-slide.
        private void UpdateDockAnimation()
        {
            if (radarBody == null)
            {
                return;
            }
            float target = collapsed ? 0f : 1f;
            float step = Time.unscaledDeltaTime / DockAnimationSeconds;
            dockProgress = Mathf.MoveTowards(dockProgress, target, step);
            float eased = Mathf.SmoothStep(0f, 1f, dockProgress);
            radarBody.sizeDelta = new Vector2(PanelSize, PanelSize * eased);
        }

        private bool DockSettled()
        {
            return !collapsed && dockProgress > 0.98f;
        }

        // ScreenSpaceOverlay canvases map RectTransform world corners 1:1 to screen pixels (Y
        // increasing upward, origin bottom-left) — same reasoning FlippedMouse() below already
        // relies on for Input.mousePosition. Converts to IMGUI's Rect convention (Y down from top).
        private bool TryGetDockedRect(out Rect rect)
        {
            if (radarBody == null || !radarBody.gameObject.activeInHierarchy)
            {
                rect = default(Rect);
                return false;
            }

            Vector3[] corners = new Vector3[4];
            radarBody.GetWorldCorners(corners);
            float xMin = corners[0].x;
            float yMaxFromBottom = corners[2].y;
            float width = corners[2].x - corners[0].x;
            float height = corners[2].y - corners[0].y;
            rect = new Rect(xMin, Screen.height - yMaxFromBottom, width, height);
            return true;
        }

        private Rect CurrentPanelRect()
        {
            Rect rect;
            if (TryGetDockedRect(out rect))
            {
                return rect;
            }
            return GetPanelRect();
        }

        private static Rect GetPanelRect()
        {
            return new Rect(Screen.width - PanelSize - DockMargin, Screen.height - PanelSize - DockMargin, PanelSize, PanelSize);
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

            UpdateDockAnimation();
            if (!DockSettled())
            {
                SetRadarOwnsMouse(false);
                return;
            }

            Rect rect = CurrentPanelRect();
            SetRadarOwnsMouse(MouseIsOwnedByRadar(rect));
            HandleOrbitAndZoom(rect);
            UpdateGridScale();
            UpdateRipples();
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
                    // Tab strip first: a click on a channel tab switches the active channel and
                    // must not fall through into a lock attempt on whatever blip sits behind it.
                    if (!TrySelectTabAtMouse(rect))
                    {
                        TryLockAtMouse(rect);
                    }
                }
            }
        }

        private static Rect TabRect(Rect panelRect, int channel)
        {
            return new Rect(
                panelRect.x + TabMargin + channel * (TabWidth + TabGap),
                panelRect.y + TabMargin,
                TabWidth,
                TabHeight);
        }

        private bool TrySelectTabAtMouse(Rect rect)
        {
            Vector2 mouse = FlippedMouse();
            for (int channel = 0; channel < FireChannels.Count; channel++)
            {
                if (TabRect(rect, channel).Contains(mouse))
                {
                    activeChannel = channel;
                    return true;
                }
            }
            return false;
        }

        // (Un)locks the clicked blip on the ACTIVE channel. A manual lock on channel 0 pauses
        // the captain's auto air-defence selection (Channel0ManualLock) until unlocked or the
        // target dies; unlocking hands the channel back to auto.
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

            int channel = activeChannel;
            bool willLock = !ReferenceEquals(ship.ChannelTargets[channel], target);
            ship.ChannelTargets[channel] = willLock ? target : null;
            if (channel == 0)
            {
                ship.Channel0ManualLock = willLock;
            }

            ILockable lockable = (ILockable)target;
            ModNetworking.SendToAll(CaptainLockNet.LockMsg.CreateMessage(ship.Captain.PlayerID, channel, target.PlayerID, lockable.GuidHash, willLock, true));
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
            // DockSettled(), not just !collapsed -- while the uGUI dock is mid-slide, radarBody's
            // rect is a shorter-than-square work in progress; drawing the (square) RenderTexture into
            // it would render squished. The uGUI panel itself still visibly slides every frame either
            // way (Update() -> UpdateDockAnimation() runs regardless); only the IMGUI content (texture,
            // bloom, mouse handling) waits for the slide to finish before it starts drawing.
            if (!IsOpen || StatMaster.hudHidden || radarTexture == null || !DockSettled())
            {
                return;
            }

            Rect rect = CurrentPanelRect();
            ConsumePanelMouseEvent(rect);
            DrawRadarTexture(rect);
            DrawPanelFrame(rect);
            DrawChannelTabs(rect);
            DrawRingScaleLabel(rect);
        }

        // Fire-channel tab strip along the panel's top-left edge. Pure IMGUI drawing — clicks
        // are handled in Update()'s HandleClick via TrySelectTabAtMouse (ConsumePanelMouseEvent
        // eats IMGUI mouse events over the panel before any GUI.Button could see them).
        private void DrawChannelTabs(Rect rect)
        {
            if (tabLabelStyle == null)
            {
                tabLabelStyle = new GUIStyle(GUI.skin.label);
                tabLabelStyle.alignment = TextAnchor.MiddleCenter;
                tabLabelStyle.fontSize = 12;
            }

            Color previous = GUI.color;
            for (int channel = 0; channel < FireChannels.Count; channel++)
            {
                Rect tab = TabRect(rect, channel);
                Color color = FireChannels.Colors[channel];
                bool active = channel == activeChannel;

                GUI.color = new Color(color.r, color.g, color.b, active ? 0.85f : 0.25f);
                GUI.DrawTexture(tab, framePixel);
                if (active)
                {
                    GUI.color = Color.white;
                    GUI.DrawTexture(new Rect(tab.x, tab.yMax, tab.width, 2f), framePixel);
                }

                GUI.color = active ? Color.white : new Color(1f, 1f, 1f, 0.75f);
                GUI.Label(tab, channel.ToString(), tabLabelStyle);
            }
            GUI.color = previous;
        }

        // Scale readout moved inside the panel's bottom-left corner (was drawn above the rect
        // before) — a small backed label using the same dark panel color as the uGUI dock.
        private void DrawRingScaleLabel(Rect rect)
        {
            string text = "1 ring = " + ComputeRingStepMeters().ToString("0") + "m";
            Rect backdrop = new Rect(rect.x + 8f, rect.yMax - 24f, 116f, 18f);
            Color previous = GUI.color;
            GUI.color = new Color(0.0235f, 0.0235f, 0.0549f, 0.75f);
            GUI.DrawTexture(backdrop, framePixel);
            GUI.color = Color.white;
            GUI.Label(new Rect(backdrop.x + 6f, backdrop.y, backdrop.width - 8f, backdrop.height), text);
            GUI.color = previous;
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
