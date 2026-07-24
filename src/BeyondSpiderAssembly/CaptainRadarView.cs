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
        private const float MarkerScale = 0.3f;
        // Missile blips are sized per size-tier rather than literally by physical Radius — the three
        // tiers' radii (3/5/10, see MissileLauncherAssets.MissileRadius) don't read as a clean visual
        // progression on the tiny radar display, so heavy/medium/small markers instead sit in a fixed
        // 5:2:1 ratio. Which tier a track falls in is RadarFilter's call (it has to agree with the
        // filter bar's rows), this is only how big each tier draws.
        private const float MissileMarkerHeavyFactor = 1.25f;
        private const float MissileMarkerMediumFactor = 0.5f;
        private const float MissileMarkerSmallFactor = 0.25f;
        // Missiles draw as velocity-aligned capsules rather than dots, so a glance shows where each
        // one is headed. Unity's capsule primitive is 1 wide x 2 tall at unit scale; these factors
        // shape the tier scale into a slender ~3:1 body of about the same visual mass as the old
        // sphere of that tier.
        private const float MissileCapsuleWidthFactor = 0.55f;
        private const float MissileCapsuleLengthFactor = 0.9f;
        // Big (>=76mm) cannon shells, shown only while the SHL filter row is on — the smallest
        // dot on the display, under even a small missile.
        private const float ShellMarkerFactor = 0.2f;
        // Shell dots grow linearly with caliber around this baseline; the clamp keeps a monster
        // bore from drawing wider than a ship and a threshold-caliber 76mm round from vanishing.
        private const float ShellMarkerRefCaliberMm = 150f;
        private const float ShellMarkerMinCaliberFactor = 0.5f;
        private const float ShellMarkerMaxCaliberFactor = 2f;
        // Ship markers scale with the hull's measured volume (ShipPartition's tick-3 OBB stat):
        // sqrt keeps the growth readable rather than literal, and the clamp holds the biggest and
        // smallest ship icons to exactly a 3x spread so neither corvettes nor dreadnoughts break
        // the display.
        private const float ShipMarkerRefVolume = 4000f;
        private const float ShipMarkerMinVolumeFactor = 0.6f;
        private const float ShipMarkerMaxVolumeFactor = 1.8f;
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
        // Per-rank alpha falloff for channel 0's multi-lock reticles (ADR-0014): rank 0 full,
        // rank 3 at 1 - 3*step of the channel alpha.
        private const float Channel0RankAlphaStep = 0.18f;
        // 雷达筛选 bar (ADR-0013), drawn just above the panel's top edge: one button per
        // RadarFilter category, tinted with the ACTIVE channel's color because the mask it edits
        // belongs to that channel alone. Unchecking a row hides those contacts and bars the
        // channel from locking them.
        private const float FilterButtonWidth = 62f;
        private const float FilterButtonHeight = 20f;
        private const float FilterButtonGap = 4f;
        private const float FilterBarGap = 6f;
        // How far off a blip a click may land and still lock it, in radar-texture pixels (the
        // texture is RadarTextureSize across, drawn into a PanelSize-wide rect, so this is a
        // slightly smaller radius in screen pixels). A marker's own on-screen footprint is far too
        // small to demand a direct hit — a small missile's blip draws at MarkerScale * 0.25 and is
        // a couple of pixels wide — so the click takes the NEAREST lockable blip within this
        // radius instead. Nearest-wins means a generous radius still resolves a tight cluster.
        private const float ClickGrabRadiusPixels = 30f;
        // Every blip drops a white plumb line to the radar plane, so its height reads off the grid
        // instead of being guesswork on an orbiting 3D display; a locked blip thickens its line and
        // extends it along the plane back to the origin, fixing the target's bearing as well.
        private const float DropLineWidth = 0.015f;
        private const float DropLineLockedWidth = 0.045f;
        private const float DropLineAlpha = 0.4f;
        private const float DropLineLockedAlpha = 0.85f;
        // Blip interpolation: a blip dead-reckons along its own track velocity every frame and is
        // pulled toward the latest scan at this rate (1/s). Dead reckoning is what removes the lag
        // — smoothing alone would trail a fast contact by velocity/rate — so this only has to
        // absorb the small step each new scan brings.
        private const float BlipSmoothingRate = 12f;
        // Contacts beyond the displayed range draw as a bearing arrow hugging the panel's 2D edge
        // instead of a normal blip clamped to the outer ring — a real-looking icon parked at the
        // rim reads as "target at ring 4", which is exactly the misread this avoids. The arrows
        // are an OnGUI overlay clamped to the square panel border (the 3D outer ring projects well
        // inside the panel, the old complaint), inset by just enough margin to fit the arrow.
        private const float EdgeArrowViewportMargin = 0.04f;
        private const float EdgeArrowSizePixels = 20f;
        private const int EdgeArrowTextureSize = 64;
        // Per-ring range labels, drawn where each ring passes nearest the viewer.
        private const float RingLabelEdgeMargin = 0.02f;

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
        // Channel-0 multi-lock reticles (ADR-0014): one quad per threat-list slot.
        private readonly GameObject[] channel0Icons = new GameObject[FireChannels.Channel0MaxTargets];
        private int activeChannel;
        private GUIStyle tabLabelStyle;
        private GUIStyle ringLabelStyle;
        private Texture2D edgeArrowTexture;
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

        // Everything the screen keeps per contact between frames. Marker and DropLine are on loan
        // from the pools below and go back the moment the contact stops being drawn (it died, left
        // sensor range, or the active channel's filter now hides it); the record itself is minted
        // fresh each time a contact reappears, which is exactly when the smoothed position must
        // snap rather than slide in from wherever the recycled marker last sat.
        private sealed class Blip
        {
            // Rented like DropLine below: non-null if and only if this contact currently draws a
            // 3D marker. A contact beyond the displayed range keeps its Blip (so its smoothing
            // state survives and it eases back in when it returns) but holds no marker — it shows
            // as a panel-edge bearing arrow instead (see edgeIndicators).
            public GameObject Marker;
            // Non-null if and only if this blip currently owns an ACTIVE plumb line: rented on
            // first use by UpdateDropLine, handed back by ReleaseDropLine. Both halves matter,
            // because a line is "free" precisely when it is inactive — renting one up front would
            // hand it straight back to the pool for the next contact to rent out from under this
            // blip, and holding the reference after deactivating it lets this blip scribble on
            // whoever rented it next. A blip may own a line at some times and not others: a core
            // draws one as a foreign contact and none once the HUD switches to its own ship.
            public LineRenderer DropLine;
            // Smoothed WORLD position. Kept in world space, not display space, so that ship
            // rotation and zoom stay exact and instant — only the contact's own motion is smoothed.
            public Vector3 World;
            public bool Fresh = true;
        }

        private readonly List<GameObject> arrowPool = new List<GameObject>();
        private readonly List<GameObject> capsulePool = new List<GameObject>();
        private readonly List<GameObject> spherePool = new List<GameObject>();
        private readonly List<LineRenderer> dropLinePool = new List<LineRenderer>();
        private readonly Dictionary<ITrackable, Blip> blips = new Dictionary<ITrackable, Blip>();
        private readonly HashSet<ITrackable> seen = new HashSet<ITrackable>();
        private readonly List<ITrackable> stale = new List<ITrackable>();

        // A contact past the displayed range this frame: drawn as a bearing arrow pinned to the
        // panel's edge (OnGUI overlay, see AddEdgeIndicator/DrawEdgeArrows) and still lockable by
        // click through FindLockableTarget. Rebuilt every SyncMarkers pass.
        private struct EdgeIndicator
        {
            public ITrackable Target;
            // Radar-camera viewport position (0..1, y up), already clamped to the square border.
            public Vector2 Viewport;
            public float AngleDeg;
            public Color Color;
        }

        private readonly List<EdgeIndicator> edgeIndicators = new List<EdgeIndicator>();

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
            // The whole pocket scene (camera, grid, glow, ripples, lock icons, and every pooled
            // marker below) hangs off this one root. BuildScene() only ever runs once, in Awake, on
            // the SingleInstance component that lives on Mod.Root (DontDestroyOnLoad) -- but these
            // GameObjects are minted in the *active* scene, so without this they'd be destroyed on
            // the next level load and never rebuilt, leaving the radar panel present but blank after
            // the first scene change. Persisting the root keeps its whole subtree alive; it's a
            // world-space root at Y=PocketOriginHeight, so it stays an independent DDOL root rather
            // than parenting under Mod.Root the way the UI canvas does.
            Object.DontDestroyOnLoad(rootObject);

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

            BuildEdgeArrowTexture();
        }

        // A white notched dart pointing up (texture space, tip at the top), tinted per contact by
        // GUI.color at draw time and rotated to its bearing via GUIUtility.RotateAroundPivot. Drawn
        // small with bilinear filtering, so a binary in/out alpha is smooth enough.
        private void BuildEdgeArrowTexture()
        {
            edgeArrowTexture = new Texture2D(EdgeArrowTextureSize, EdgeArrowTextureSize, TextureFormat.ARGB32, false);
            edgeArrowTexture.wrapMode = TextureWrapMode.Clamp;
            Vector2 tip = new Vector2(0.5f, 0.95f);
            Vector2 left = new Vector2(0.1f, 0.08f);
            Vector2 right = new Vector2(0.9f, 0.08f);
            Vector2 notch = new Vector2(0.5f, 0.32f);
            Color solid = Color.white;
            Color clear = new Color(1f, 1f, 1f, 0f);
            for (int y = 0; y < EdgeArrowTextureSize; y++)
            {
                for (int x = 0; x < EdgeArrowTextureSize; x++)
                {
                    Vector2 p = new Vector2((x + 0.5f) / EdgeArrowTextureSize, (y + 0.5f) / EdgeArrowTextureSize);
                    bool inside = PointInTriangle(p, tip, left, notch) || PointInTriangle(p, tip, notch, right);
                    edgeArrowTexture.SetPixel(x, y, inside ? solid : clear);
                }
            }
            edgeArrowTexture.Apply();
        }

        private static float EdgeSide(Vector2 a, Vector2 b, Vector2 p)
        {
            return (b.x - a.x) * (p.y - a.y) - (b.y - a.y) * (p.x - a.x);
        }

        private static bool PointInTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = EdgeSide(a, b, p);
            float d2 = EdgeSide(b, c, p);
            float d3 = EdgeSide(c, a, p);
            bool hasNegative = d1 < 0f || d2 < 0f || d3 < 0f;
            bool hasPositive = d1 > 0f || d2 > 0f || d3 > 0f;
            return !(hasNegative && hasPositive);
        }

        // One reticle quad per single-lock channel (1-3) plus one per channel-0 threat slot
        // (ADR-0014 multi-lock: channel 0 rings up to Channel0MaxTargets contacts at once), all
        // tinted with their channel's color every frame in SyncMarkers. lockIcons[0] stays null
        // — channel 0 draws exclusively through channel0Icons.
        private void BuildLockIcons()
        {
            for (int channel = 1; channel < FireChannels.Count; channel++)
            {
                lockIcons[channel] = CreateLockIcon("BS Radar Lock Icon " + channel);
            }
            for (int slot = 0; slot < channel0Icons.Length; slot++)
            {
                channel0Icons[slot] = CreateLockIcon("BS Radar Ch0 Lock Icon " + slot);
            }
        }

        private GameObject CreateLockIcon(string name)
        {
            GameObject icon = GameObject.CreatePrimitive(PrimitiveType.Quad);
            icon.name = name;
            Object.DestroyImmediate(icon.GetComponent<Collider>());
            icon.transform.SetParent(pocketRoot, false);
            icon.transform.localScale = Vector3.one * 0.5f;
            MeshRenderer renderer = icon.GetComponent<MeshRenderer>();
            renderer.material = new Material(Shader.Find("Particles/Additive"));
            renderer.material.mainTexture = ModResource.GetTexture("BS Migrated AA Lock Texture").Texture;
            icon.SetActive(false);
            return icon;
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

        // No collider: clicks are resolved by projecting each drawn blip into the radar camera and
        // taking the nearest one to the click (FindLockableTarget), not by raycasting the pocket
        // scene, so nothing here needs to be hittable.
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
            else if (kind == TrackKind.HeavyMissile)
            {
                // Capsule, not sphere: SyncMarkers aligns its long (Y) axis with the missile's
                // velocity so the blip itself shows the flight direction.
                marker = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                marker.name = "BS Radar Missile Marker";
                Object.DestroyImmediate(marker.GetComponent<Collider>());
                marker.GetComponent<Renderer>().material = MarkerMaterial();
            }
            else
            {
                marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                marker.name = "BS Radar Shell Marker";
                Object.DestroyImmediate(marker.GetComponent<Collider>());
                marker.GetComponent<Renderer>().material = MarkerMaterial();
            }

            marker.transform.SetParent(pocketRoot, false);
            marker.transform.localScale = Vector3.one * MarkerScale;
            marker.SetActive(false);
            return marker;
        }

        // Sits at pocketRoot's own origin with an identity transform, so useWorldSpace = false lets
        // SetPosition take the very same radar-space coordinates the markers use. A LineRenderer
        // (rather than a mesh) because it billboards toward the camera on its own — the radar
        // camera orbits, and a flat quad would vanish edge-on.
        private LineRenderer CreateDropLine()
        {
            GameObject lineObject = new GameObject("BS Radar Drop Line");
            lineObject.transform.SetParent(pocketRoot, false);
            lineObject.transform.localPosition = Vector3.zero;
            lineObject.transform.localRotation = Quaternion.identity;
            lineObject.transform.localScale = Vector3.one;

            LineRenderer line = lineObject.AddComponent<LineRenderer>();
            line.material = new Material(Shader.Find("Particles/Additive"));
            // Additive shaders multiply vertex color by _TintColor, which defaults to mid-grey and
            // would halve every width/alpha chosen below; white here leaves SetColors in charge.
            line.material.SetColor("_TintColor", Color.white);
            line.useWorldSpace = false;
            line.SetVertexCount(2);
            lineObject.SetActive(false);
            return line;
        }

        // The radar always shows the ACTIVE local ship — whichever of the local player's ships
        // the info panel's tab row currently selects (ADR-0011 multi-ship).
        private static ShipState OwnShipState()
        {
            return SpaceCombatRegistry.ActiveLocalShip;
        }

        // Pool "in use" is tracked by activeSelf alone (releases below just SetActive(false)), the
        // same bookkeeping the marker pools have always used.
        private Blip GetOrCreateBlip(ITrackable target)
        {
            Blip blip;
            if (blips.TryGetValue(target, out blip))
            {
                return blip;
            }

            blip = new Blip();
            blips[target] = blip;
            return blip;
        }

        // Rents the blip a marker of its kind if it doesn't hold one (it was off-display, or the
        // record is fresh) and activates it. Same non-null-iff-owned discipline as DropLine.
        private void EnsureMarker(Blip blip, TrackKind kind)
        {
            if (blip.Marker == null)
            {
                blip.Marker = RentMarker(kind);
            }
            blip.Marker.SetActive(true);
        }

        private static void ReleaseMarker(Blip blip)
        {
            if (blip.Marker == null)
            {
                return;
            }
            blip.Marker.SetActive(false);
            blip.Marker = null;
        }

        private GameObject RentMarker(TrackKind kind)
        {
            List<GameObject> pool = kind == TrackKind.Ship ? arrowPool
                : kind == TrackKind.HeavyMissile ? capsulePool
                : spherePool;
            for (int i = 0; i < pool.Count; i++)
            {
                if (!pool[i].activeSelf)
                {
                    return pool[i];
                }
            }

            GameObject created = CreateMarker(kind);
            pool.Add(created);
            return created;
        }

        private LineRenderer RentDropLine()
        {
            for (int i = 0; i < dropLinePool.Count; i++)
            {
                if (!dropLinePool[i].gameObject.activeSelf)
                {
                    return dropLinePool[i];
                }
            }

            LineRenderer created = CreateDropLine();
            dropLinePool.Add(created);
            return created;
        }

        // Hands a blip's plumb line back to the pool. Dropping the reference is not optional: a
        // line counts as free exactly when it is inactive, so a blip that kept pointing at one it
        // had deactivated could later write to — or switch off — a line some other contact has
        // since rented. Hence the invariant DropLine upholds: non-null if and only if this blip
        // currently owns an active line.
        private static void ReleaseDropLine(Blip blip)
        {
            if (blip.DropLine == null)
            {
                return;
            }
            blip.DropLine.gameObject.SetActive(false);
            blip.DropLine = null;
        }

        private static void ReleaseBlip(Blip blip)
        {
            ReleaseMarker(blip);
            ReleaseDropLine(blip);
        }

        private void HideAllMarkers()
        {
            foreach (KeyValuePair<ITrackable, Blip> pair in blips)
            {
                ReleaseBlip(pair.Value);
            }
            blips.Clear();
            edgeIndicators.Clear();
            for (int channel = 0; channel < lockIcons.Length; channel++)
            {
                if (lockIcons[channel] != null)
                {
                    lockIcons[channel].SetActive(false);
                }
            }
            for (int slot = 0; slot < channel0Icons.Length; slot++)
            {
                // Null-guarded like lockIcons above: SetOpen(false) can arrive after a sim-end
                // teardown already destroyed the marker objects (logged NRE in the MP playtest).
                if (channel0Icons[slot] != null)
                {
                    channel0Icons[slot].SetActive(false);
                }
            }
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

        // A ship marker scales with its hull's measured volume, a missile marker by tier, a shell
        // dot by caliber. Anything RadarFilter can't categorize never gets drawn at all, so it
        // only falls back to MarkerScale defensively.
        private static float MarkerScaleFor(ITrackable target)
        {
            RadarCategory category;
            if (!RadarFilter.TryGetCategory(target, out category))
            {
                return MarkerScale;
            }

            switch (category)
            {
                case RadarCategory.Ship:
                    return MarkerScale * ShipVolumeFactor(target);
                case RadarCategory.HeavyMissile:
                    return MarkerScale * MissileMarkerHeavyFactor;
                case RadarCategory.MediumMissile:
                    return MarkerScale * MissileMarkerMediumFactor;
                case RadarCategory.SmallMissile:
                    return MarkerScale * MissileMarkerSmallFactor;
                case RadarCategory.Shell:
                    return MarkerScale * ShellMarkerFactor * ShellCaliberFactor(target);
                default:
                    return MarkerScale;
            }
        }

        // sqrt of hull volume relative to the reference hull, clamped to the 3x min-to-max spread.
        // HullVolume comes from the tick-3 partition's OBB stat, which every client computes for
        // every machine, so foreign ships size correctly too. 1 (reference size) when the ship or
        // its stat isn't resolvable — a contact should never vanish over a missing stat.
        private static float ShipVolumeFactor(ITrackable target)
        {
            SpaceShipCore core = target as SpaceShipCore;
            if (core == null || core.BlockBehaviour == null)
            {
                return 1f;
            }
            ShipState ship = SpaceCombatRegistry.ShipOf(core.BlockBehaviour);
            if (ship == null || ship.HullVolume <= 0f)
            {
                return 1f;
            }
            return Mathf.Clamp(Mathf.Sqrt(ship.HullVolume / ShipMarkerRefVolume),
                ShipMarkerMinVolumeFactor, ShipMarkerMaxVolumeFactor);
        }

        private static float ShellCaliberFactor(ITrackable target)
        {
            SpaceKineticRound round = target as SpaceKineticRound;
            if (round == null)
            {
                return 1f;
            }
            return Mathf.Clamp(round.Caliber / ShellMarkerRefCaliberMm,
                ShellMarkerMinCaliberFactor, ShellMarkerMaxCaliberFactor);
        }

        // Where a blip should be RIGHT NOW in world space, given that its track is up to one scan
        // interval old: dead-reckon along the track's own velocity for the age of the record, then
        // ease out whatever error the newest scan revealed. Advancing by velocity is what keeps
        // this lag-free — easing alone would leave a fast contact trailing its true position by
        // velocity/BlipSmoothingRate — so the ease only ever has a scan-sized step to absorb.
        private static void AdvanceBlip(Blip blip, SensorTrack track)
        {
            Vector3 predicted = track.Position + track.Velocity * Mathf.Max(0f, Time.time - track.ScanTime);
            if (blip.Fresh)
            {
                blip.World = predicted;
                blip.Fresh = false;
                return;
            }

            blip.World += track.Velocity * Time.deltaTime;
            blip.World = Vector3.Lerp(blip.World, predicted, 1f - Mathf.Exp(-BlipSmoothingRate * Time.deltaTime));
        }

        // Unclamped: a contact beyond DisplayRadius returns its true (off-grid) display position,
        // and SyncMarkers turns it into a panel-edge bearing arrow instead of a marker — a normal
        // blip pinned to the outer ring used to read as "target at ring 4".
        private Vector3 ToDisplaySpace(Transform captain, Vector3 world)
        {
            Vector3 relative = CaptainLocalToRadarSpace(captain.InverseTransformVector(world - captain.position));
            return relative / MetersPerUnit;
        }

        // The plumb line down to the radar plane, plus — once locked — a leg from that foot back to
        // the origin. One polyline does both: marker -> foot -> origin, so the corner at the foot
        // is where height stops being read and bearing starts.
        private void UpdateDropLine(Blip blip, Vector3 displayPos, bool locked)
        {
            if (blip.DropLine == null)
            {
                blip.DropLine = RentDropLine();
            }

            LineRenderer line = blip.DropLine;
            line.gameObject.SetActive(true);

            Vector3 foot = new Vector3(displayPos.x, 0f, displayPos.z);
            float width = locked ? DropLineLockedWidth : DropLineWidth;
            Color color = new Color(1f, 1f, 1f, locked ? DropLineLockedAlpha : DropLineAlpha);

            line.SetVertexCount(locked ? 3 : 2);
            line.SetPosition(0, displayPos);
            line.SetPosition(1, foot);
            if (locked)
            {
                line.SetPosition(2, Vector3.zero);
            }
            line.SetWidth(width, width);
            line.SetColors(color, color);
        }

        // Projects an off-display contact through the radar camera and pins it to the square panel
        // border (2D screen space — the 3D outer ring projects well inside the panel, so clamping
        // there left a visible gap to the screen edge). The arrow points from the panel center
        // toward the contact's projection, i.e. its bearing in the current view.
        private void AddEdgeIndicator(ITrackable target, Vector3 displayPos, Color color)
        {
            if (radarCamera == null || pocketRoot == null)
            {
                return;
            }

            Vector3 viewport = radarCamera.WorldToViewportPoint(pocketRoot.TransformPoint(displayPos));
            Vector2 direction = new Vector2(viewport.x - 0.5f, viewport.y - 0.5f);
            if (viewport.z < 0f)
            {
                // Behind the camera the projection mirrors; flip so the arrow still points the way
                // the player would orbit/zoom to bring the contact into view.
                direction = -direction;
            }
            if (direction.sqrMagnitude < 0.0001f)
            {
                direction = Vector2.up;
            }

            // Scale the center-to-projection ray until its LARGER axis touches the inset border —
            // that's the square-edge clamp.
            float half = 0.5f - EdgeArrowViewportMargin;
            float reach = Mathf.Max(Mathf.Abs(direction.x), Mathf.Abs(direction.y));
            Vector2 clamped = new Vector2(0.5f, 0.5f) + direction * (half / reach);

            EdgeIndicator indicator;
            indicator.Target = target;
            indicator.Viewport = clamped;
            indicator.AngleDeg = Mathf.Atan2(direction.x, direction.y) * Mathf.Rad2Deg;
            indicator.Color = color;
            edgeIndicators.Add(indicator);
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
            // The ACTIVE channel's filter decides what the screen shows, which is also what makes
            // "only what you can see is lockable" fall out for free: a click can only ever pick
            // from the blips drawn here, and it locks the very channel whose filter drew them.
            int filter = ship.ChannelFilters[activeChannel];
            seen.Clear();
            edgeIndicators.Clear();

            if (ship.Core != null)
            {
                seen.Add(ship.Core);
                Blip self = GetOrCreateBlip(ship.Core);
                EnsureMarker(self, TrackKind.Ship);
                self.Marker.transform.localPosition = Vector3.zero;
                self.Marker.transform.localRotation = Quaternion.identity;
                self.Marker.transform.localScale = Vector3.one * MarkerScaleFor(ship.Core) * OriginMarkerScale;
                SetRendererColor(self.Marker.GetComponent<Renderer>(), Color.green);
                // No plumb line: own hull sits on the origin, so it would have nowhere to drop.
                // This has to actively release rather than just skip drawing — blips outlive a
                // change of viewed ship, and THIS core was a foreign contact with a line of its own
                // right up until the HUD switched to it (ADR-0011 multi-ship: a sister ship is an
                // ordinary contact on the other's radar). That line would otherwise stay lit,
                // frozen where the previous ship's radar last put it.
                ReleaseDropLine(self);
            }

            for (int i = 0; i < ship.Tracks.Count; i++)
            {
                SensorTrack track = ship.Tracks[i];
                if (track.Kind != TrackKind.Ship && track.Kind != TrackKind.HeavyMissile
                    && track.Kind != TrackKind.LargeProjectile)
                {
                    continue;
                }
                // Shows() also owns the shell rules: only >=76mm shells have a category at all,
                // and the SHL row (default off) decides whether those draw.
                if (!RadarFilter.Shows(filter, track.Target))
                {
                    continue;
                }
                // Add() answering false means some earlier track already claimed this contact this
                // frame — two radars whose cones overlap both report it. Drawing it once is merely
                // redundant, but advancing its interpolation twice would double-step the blip.
                if (!seen.Add(track.Target))
                {
                    continue;
                }

                Blip blip = GetOrCreateBlip(track.Target);
                AdvanceBlip(blip, track);

                Color color = SpaceBallistics.IsHostile(ship.Captain, track.Target) ? Color.red : Color.green;
                Vector3 displayPos = ToDisplaySpace(captain, blip.World);
                if (displayPos.magnitude > DisplayRadius)
                {
                    // Off the displayed range: no marker, no plumb line — just a bearing arrow
                    // pinned to the panel edge, so nothing suggests a readable distance.
                    ReleaseMarker(blip);
                    ReleaseDropLine(blip);
                    AddEdgeIndicator(track.Target, displayPos, color);
                    continue;
                }

                EnsureMarker(blip, track.Kind);
                float markerScale = MarkerScaleFor(track.Target);
                blip.Marker.transform.localScale = track.Kind == TrackKind.HeavyMissile
                    ? new Vector3(markerScale * MissileCapsuleWidthFactor,
                        markerScale * MissileCapsuleLengthFactor,
                        markerScale * MissileCapsuleWidthFactor)
                    : Vector3.one * markerScale;
                blip.Marker.transform.localPosition = displayPos;

                if (track.Kind == TrackKind.Ship)
                {
                    Vector3 velLocal = ProjectDirectionToRadarPlane(CaptainLocalToRadarSpace(captain.InverseTransformDirection(track.Velocity)));
                    blip.Marker.transform.localRotation = velLocal.sqrMagnitude > 0.01f
                        ? Quaternion.LookRotation(velLocal.normalized, Vector3.up)
                        : Quaternion.identity;
                }
                else if (track.Kind == TrackKind.HeavyMissile)
                {
                    // Capsule long axis along the missile's velocity — full 3D, not projected to
                    // the radar plane: the display is a 3D scene, so a diving missile should
                    // visibly point down through the grid.
                    Vector3 velRadar = CaptainLocalToRadarSpace(captain.InverseTransformDirection(track.Velocity));
                    blip.Marker.transform.localRotation = velRadar.sqrMagnitude > 0.01f
                        ? Quaternion.FromToRotation(Vector3.up, velRadar.normalized)
                        : Quaternion.identity;
                }

                SetRendererColor(blip.Marker.GetComponent<Renderer>(), color);
                UpdateDropLine(blip, displayPos, FireChannels.IsChannelTarget(ship, track.Target));
            }

            stale.Clear();
            foreach (KeyValuePair<ITrackable, Blip> pair in blips)
            {
                if (!seen.Contains(pair.Key))
                {
                    ReleaseBlip(pair.Value);
                    stale.Add(pair.Key);
                }
            }
            for (int i = 0; i < stale.Count; i++)
            {
                blips.Remove(stale[i]);
            }

            // One reticle per fire channel, in the channel's color: the active tab's lock draws
            // bright, the other channels' locks stay visible but dimmed, so the player always
            // sees the whole fire-control picture regardless of which tab is selected. A channel
            // whose lock the ACTIVE channel's filter hides has no blip to ring, so its reticle
            // drops out until that row is checked again — a lock is still a lock, just not drawn.
            // Channel 0 is the multi-lock air-defence channel (ADR-0014): it rings its WHOLE
            // threat list, rank 0 at full channel alpha, lower ranks progressively dimmer.
            for (int channel = 1; channel < FireChannels.Count; channel++)
            {
                float alpha = channel == activeChannel ? ActiveLockIconAlpha : InactiveLockIconAlpha;
                SyncLockIcon(lockIcons[channel], ship.ChannelTargets[channel], channel, alpha);
            }
            for (int slot = 0; slot < channel0Icons.Length; slot++)
            {
                ITrackable target = slot < ship.Channel0Threats.Count ? ship.Channel0Threats[slot] : null;
                float alpha = activeChannel == 0 ? ActiveLockIconAlpha : InactiveLockIconAlpha;
                // Rank falloff: rank 0 full strength, each lower rank a step dimmer, so the
                // radar reads the threat ordering at a glance.
                alpha *= 1f - Channel0RankAlphaStep * slot;
                SyncLockIcon(channel0Icons[slot], target, 0, alpha);
            }
        }

        private void SyncLockIcon(GameObject icon, ITrackable target, int channel, float alpha)
        {
            Blip lockedBlip;
            // Marker == null: the locked contact is currently past the displayed range and shows
            // only as an edge arrow — no blip to ring, so the reticle sits out until it returns
            // (same "a lock is still a lock, just not drawn" rule as a filtered-out lock).
            if (target == null || !target.IsAlive || !blips.TryGetValue(target, out lockedBlip)
                || lockedBlip.Marker == null)
            {
                icon.SetActive(false);
                return;
            }

            GameObject lockedMarker = lockedBlip.Marker;
            icon.SetActive(true);
            icon.transform.localPosition = lockedMarker.transform.localPosition;
            // Size the reticle to the locked marker so it always frames the blip. A fixed size was
            // smaller than a scaled-up heavy-missile dot and vanished behind it; scaling a bit larger
            // than the marker keeps the reticle encircling whatever it's locked onto, big or small.
            // Largest scale component, because missile capsules are non-uniform (slim x/z, long y).
            // Higher channels ring slightly wider so several locks on one blip nest concentrically.
            Vector3 markerSize = lockedMarker.transform.localScale;
            float extent = Mathf.Max(markerSize.x, Mathf.Max(markerSize.y, markerSize.z));
            float ratio = LockIconMarkerScaleRatio * (1f + LockIconChannelScaleStep * channel);
            icon.transform.localScale = Vector3.one * (extent * ratio);
            // Billboard toward the orbiting radar camera every frame — the quad's own rotation
            // never changes otherwise, so it would render edge-on/backwards once the player
            // orbits away from whatever angle it happened to be built facing.
            icon.transform.rotation = radarCamera.transform.rotation;

            Color tint = FireChannels.Colors[channel];
            tint.a = alpha;
            SetRendererColor(icon.GetComponent<Renderer>(), tint);
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
                HideAllMarkers();
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
                // Drop every blip's interpolation state along with its marker: SyncMarkers stops
                // running while collapsed, so keeping them would have each blip slide in from
                // wherever it froze when the panel reopens. Empty means every contact snaps.
                HideAllMarkers();
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
            // Poll-driven open state: the radar shows exactly when the active ship has a captain.
            // (Captains used to SetOpen from their own OnSimulateStart/Stop; with multiple ships
            // per player the view has to follow the tab selection instead.)
            ShipState active = OwnShipState();
            bool shouldOpen = active != null && active.Captain != null;
            if (shouldOpen != IsOpen)
            {
                SetOpen(shouldOpen);
            }

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

        // The filter bar hangs outside the panel rect, so every "is the mouse ours?" test has to
        // cover both — otherwise a click meant for a filter button also swings the game camera.
        private bool IsInteractivePoint(Rect panelRect, Vector2 point)
        {
            return panelRect.Contains(point) || FilterBarRect(panelRect).Contains(point);
        }

        private bool MouseIsOwnedByRadar(Rect rect)
        {
            return IsInteractivePoint(rect, FlippedMouse()) || orbiting || leftDownInRect;
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
            bool overInteractive = IsInteractivePoint(rect, FlippedMouse());

            if (Input.GetMouseButtonDown(0) && overInteractive)
            {
                mouseDownPos = Input.mousePosition;
                leftDownInRect = true;
            }
            if (Input.GetMouseButtonUp(0))
            {
                bool wasDownInRect = leftDownInRect;
                leftDownInRect = false;
                if (wasDownInRect && overInteractive && Vector2.Distance(mouseDownPos, Input.mousePosition) < ClickPixelThreshold)
                {
                    // Chrome first, in the order it overlays the display: a click on a channel tab
                    // or a filter button must not also fall through into a lock attempt on whatever
                    // blip happens to sit behind it. The panel test then stops a click that merely
                    // landed in a gap between filter buttons — outside the display entirely — from
                    // still grabbing whatever blip sits nearest the display's top edge.
                    if (!TrySelectTabAtMouse(rect) && !TryToggleFilterAtMouse(rect) && rect.Contains(FlippedMouse()))
                    {
                        TryLockAtMouse(rect);
                    }
                }
            }
        }

        private static Rect FilterBarRect(Rect panelRect)
        {
            float width = RadarFilter.Count * FilterButtonWidth + (RadarFilter.Count - 1) * FilterButtonGap;
            return new Rect(
                panelRect.x + TabMargin,
                panelRect.y - FilterBarGap - FilterButtonHeight,
                width,
                FilterButtonHeight);
        }

        private static Rect FilterButtonRect(Rect panelRect, int category)
        {
            Rect bar = FilterBarRect(panelRect);
            return new Rect(
                bar.x + category * (FilterButtonWidth + FilterButtonGap),
                bar.y,
                FilterButtonWidth,
                FilterButtonHeight);
        }

        // Toggles one 雷达筛选 row on the ACTIVE channel only — each channel carries its own mask,
        // so the same bar edits a different configuration per tab. Broadcast because the mask now
        // gates channel 0's auto air-defence, which the host runs.
        private bool TryToggleFilterAtMouse(Rect rect)
        {
            ShipState ship = OwnShipState();
            if (ship == null || ship.Captain == null)
            {
                return false;
            }

            Vector2 mouse = FlippedMouse();
            for (int category = 0; category < RadarFilter.Count; category++)
            {
                if (!FilterButtonRect(rect, category).Contains(mouse))
                {
                    continue;
                }

                int mask = RadarFilter.Toggle(ship.ChannelFilters[activeChannel], (RadarCategory)category);
                ship.ChannelFilters[activeChannel] = mask;
                ModNetworking.SendToAll(CaptainLockNet.FilterMsg.CreateMessage(
                    ship.Captain.PlayerID, ship.CoreGuidHash, activeChannel, mask));
                return true;
            }
            return false;
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

        // (Un)locks the clicked blip on the ACTIVE channel. On channel 0 a click pins the target
        // as the MANUAL rank-0 threat (手动目标置顶, ADR-0014) — the rest of the threat list
        // keeps auto-filling around it — and clicking the current manual pin releases it back to
        // full-auto. On channel 0 the toggle test is therefore "is this already the manual pin",
        // NOT "is this ChannelTargets[0]": rank 0 usually holds an AUTO pick, and clicking that
        // blip must pin it, not bounce off as an unlock.
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
            Vector2 rtPoint = new Vector2(fracX * radarCamera.pixelWidth, (1f - fracY) * radarCamera.pixelHeight);

            ITrackable target = FindLockableTarget(rtPoint, ship);
            if (target == null)
            {
                return;
            }

            int channel = activeChannel;
            bool alreadyHeld = channel == 0
                ? ship.Channel0ManualLock && ReferenceEquals(ship.ChannelTargets[0], target)
                : ReferenceEquals(ship.ChannelTargets[channel], target);
            bool willLock = !alreadyHeld;
            ship.ChannelTargets[channel] = willLock ? target : null;
            if (channel == 0)
            {
                ship.Channel0ManualLock = willLock;
            }

            ILockable lockable = (ILockable)target;
            ModNetworking.SendToAll(CaptainLockNet.LockMsg.CreateMessage(ship.Captain.PlayerID, ship.CoreGuidHash, channel, target.PlayerID, lockable.GuidHash, willLock, true));
        }

        // The nearest lockable blip to where the player clicked, measured in the radar camera's
        // projected pixel space, rather than whatever a ray happened to strike. Demanding a direct
        // hit made this close to unusable: markers are tiny (a small missile draws at
        // MarkerScale * 0.25, a couple of pixels across), and a ray that did connect reported only
        // its closest hit — so a missile riding on top of the ship it's chasing, or the un-lockable
        // self marker, would swallow the click and silently do nothing. Nearest-within-a-radius
        // instead: it grabs the blip the player was obviously aiming at, and a dense cluster still
        // resolves to whichever one is actually closest.
        //
        // Only blips currently on screen are candidates, which is what enforces "a channel may lock
        // only what its own 雷达筛选 shows" (ADR-0013) — SyncMarkers drew exactly the active
        // channel's filtered set, and this locks that same channel.
        private ITrackable FindLockableTarget(Vector2 rtPoint, ShipState ship)
        {
            ITrackable best = null;
            float bestDistance = ClickGrabRadiusPixels;
            foreach (KeyValuePair<ITrackable, Blip> pair in blips)
            {
                ITrackable candidate = pair.Key;
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
                if (pair.Value.Marker == null || !pair.Value.Marker.activeSelf)
                {
                    continue;
                }

                Vector3 projected = radarCamera.WorldToScreenPoint(pair.Value.Marker.transform.position);
                if (projected.z <= 0f)
                {
                    continue;
                }

                float distance = Vector2.Distance(new Vector2(projected.x, projected.y), rtPoint);
                if (distance >= bestDistance)
                {
                    continue;
                }

                best = candidate;
                bestDistance = distance;
            }

            // Edge arrows are clickable too: an off-display contact has no marker, but its arrow
            // marks a definite spot on the border, and locking from there beats zooming out first.
            for (int i = 0; i < edgeIndicators.Count; i++)
            {
                ITrackable candidate = edgeIndicators[i].Target;
                if (candidate == null || !candidate.IsAlive || !(candidate is ILockable))
                {
                    continue;
                }
                if (ReferenceEquals(candidate, ship.Core))
                {
                    continue;
                }

                Vector2 pixel = new Vector2(
                    edgeIndicators[i].Viewport.x * radarCamera.pixelWidth,
                    edgeIndicators[i].Viewport.y * radarCamera.pixelHeight);
                float distance = Vector2.Distance(pixel, rtPoint);
                if (distance >= bestDistance)
                {
                    continue;
                }

                best = candidate;
                bestDistance = distance;
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
            DrawRingLabels(rect);
            DrawEdgeArrows(rect);
            DrawPanelFrame(rect);
            DrawChannelTabs(rect);
            DrawFilterBar(rect);
            DrawRingScaleLabel(rect);
        }

        // Range readout on each grid ring, drawn where the ring passes nearest the viewer (the
        // camera's forward projected onto the radar plane points away from the viewer, so the near
        // side is its negation — labels sit along the screen's bottom half and follow the orbit).
        private void DrawRingLabels(Rect rect)
        {
            if (radarCamera == null || gridTransform == null || pocketRoot == null)
            {
                return;
            }
            if (ringLabelStyle == null)
            {
                ringLabelStyle = new GUIStyle(GUI.skin.label);
                ringLabelStyle.alignment = TextAnchor.MiddleCenter;
                ringLabelStyle.fontSize = 10;
                ringLabelStyle.normal.textColor = new Color(0.55f, 0.95f, 1f, 0.85f);
            }

            float ringStep = ComputeRingStepMeters();
            // The grid mesh scales continuously with zoom (UpdateGridScale); ring radii must track
            // the same scale or the labels drift off their rings between snap tiers.
            float gridScale = gridTransform.localScale.x;
            Vector3 planar = radarCamera.transform.forward;
            planar.y = 0f;
            Vector3 nearSide = planar.sqrMagnitude > 0.001f ? -planar.normalized : Vector3.back;

            for (int ring = 1; ring <= RingCount; ring++)
            {
                float radius = DisplayRadius * ring / RingCount * gridScale;
                Vector3 world = pocketRoot.TransformPoint(nearSide * radius);
                Vector3 viewport = radarCamera.WorldToViewportPoint(world);
                if (viewport.z <= 0f
                    || viewport.x < RingLabelEdgeMargin || viewport.x > 1f - RingLabelEdgeMargin
                    || viewport.y < RingLabelEdgeMargin || viewport.y > 1f - RingLabelEdgeMargin)
                {
                    continue;
                }

                Vector2 screen = new Vector2(
                    rect.x + viewport.x * rect.width,
                    rect.y + (1f - viewport.y) * rect.height);
                string text = (ringStep * ring).ToString("0") + "m";
                // Nudged up a few pixels so the text rides just above the ring line.
                GUI.Label(new Rect(screen.x - 30f, screen.y - 16f, 60f, 14f), text, ringLabelStyle);
            }
        }

        private void DrawEdgeArrows(Rect rect)
        {
            if (edgeIndicators.Count == 0 || edgeArrowTexture == null)
            {
                return;
            }

            Color previousColor = GUI.color;
            Matrix4x4 previousMatrix = GUI.matrix;
            for (int i = 0; i < edgeIndicators.Count; i++)
            {
                EdgeIndicator indicator = edgeIndicators[i];
                Vector2 pivot = new Vector2(
                    rect.x + indicator.Viewport.x * rect.width,
                    rect.y + (1f - indicator.Viewport.y) * rect.height);
                // RotateAroundPivot turns clockwise on screen; AngleDeg was measured the same way
                // (atan2 of the y-up viewport bearing), so up-pointing texture -> bearing.
                GUIUtility.RotateAroundPivot(indicator.AngleDeg, pivot);
                GUI.color = indicator.Color;
                GUI.DrawTexture(new Rect(
                    pivot.x - EdgeArrowSizePixels * 0.5f,
                    pivot.y - EdgeArrowSizePixels * 0.5f,
                    EdgeArrowSizePixels,
                    EdgeArrowSizePixels), edgeArrowTexture);
                GUI.matrix = previousMatrix;
            }
            GUI.color = previousColor;
        }

        // 雷达筛选 bar above the panel's top edge — same IMGUI-drawn, Update()-clicked arrangement
        // as the channel tabs (see DrawChannelTabs). Tinted with the active channel's color, since
        // the mask it edits is that channel's alone and switching tabs switches the whole bar.
        private void DrawFilterBar(Rect rect)
        {
            ShipState ship = OwnShipState();
            if (ship == null)
            {
                return;
            }

            if (tabLabelStyle == null)
            {
                tabLabelStyle = new GUIStyle(GUI.skin.label);
                tabLabelStyle.alignment = TextAnchor.MiddleCenter;
                tabLabelStyle.fontSize = 12;
            }

            int mask = ship.ChannelFilters[activeChannel];
            Color channelColor = FireChannels.Colors[activeChannel];
            Color previous = GUI.color;
            for (int category = 0; category < RadarFilter.Count; category++)
            {
                Rect button = FilterButtonRect(rect, category);
                bool enabled = RadarFilter.Contains(mask, (RadarCategory)category);

                GUI.color = new Color(channelColor.r, channelColor.g, channelColor.b, enabled ? 0.85f : 0.25f);
                GUI.DrawTexture(button, framePixel);
                GUI.color = enabled ? Color.white : new Color(1f, 1f, 1f, 0.75f);
                GUI.Label(button, RadarFilter.Labels[category], tabLabelStyle);
            }
            GUI.color = previous;
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

            bool overPanel = IsInteractivePoint(rect, evt.mousePosition);
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
