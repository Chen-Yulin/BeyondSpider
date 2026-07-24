using System.Collections.Generic;
using Modding;
using UnityEngine;

namespace BeyondSpiderAssembly
{
    // Applies the host control panel's two world overrides — "no boundary" and "defog" — to the local
    // scene. Both are plain desired-state flags (BoundaryOff / DefogOn) that something else owns: the
    // host's HostControlPanel toggles set them locally, and on an MP client HostControlNet writes the
    // host's values in. This component just makes the scene match the flags, idempotently, at 2 Hz.
    // Referenced ../ModernAirCombat for the fog objects; boundary removal uses the engine's own levers.
    //
    // NO BOUNDARY (BoundaryOff) — see ADR-0019 for the full anatomy. Three layers:
    //   * Walls: the per-scene BoundingBoxController.DisableBounds(machine) (the god-mode "Disable
    //     Bounding Box" call) — six wall colliders off, StatMaster.Bounding.*Pos to ±10000, and
    //     Bounding.Enabled cleared (which also un-gates AddPiece's "out of bounds → can't simulate"
    //     check, so an oversized hull can start a run). Plus the separate "WORLD BOUNDARIES" object's
    //     colliders where a level carries one.
    //   * Ground: DisableBounds never touches the floor. Besiege's ground = layers 24/28/29
    //     (MachineGround.CreateLayerMask). We switch off the solid (non-trigger) colliders there, only
    //     while simulating (build mode's placement tool needs them; zero-g has nothing to fall onto).
    //   * MP packet range: a networked block's position is compressed into 17/14 bits relative to
    //     NetworkCompression.worldBounds (the level's small box) with no clamp, so a hull past that box
    //     wraps to garbage on remote clients. While flying in MP we widen that box deterministically so
    //     every peer's encode/decode still agree, and restore it after. (This is why the client must be
    //     told to remove its boundary too — the widened range has to match on both ends.)
    //
    // DEFOG (DefogOn) — extend the game camera's far clip to 8 km and drop the level fog so distant
    // ships are actually visible: RenderSettings.fog off plus the fog-volume children of "Main Camera"
    // (Fog Volume / Fog Volume Dark / FOG SPHERE, the objects ModernAirCombat's SetFog toggles). Purely
    // local to each peer's own camera — sync just means the toggle state reaches the client so it defogs
    // its own view too.
    public sealed class SpaceBoundary : SingleInstance<SpaceBoundary>
    {
        public override string Name { get { return "BeyondSpider Space Boundary"; } }

        private const float CheckInterval = 0.5f;

        // Besiege's own "ground" layer set (MachineGround.CreateLayerMask(24, 28, 29)):
        // 24 = Environment, 28 = BrickWall, 29 = Floor. Verified against the game's layer table.
        private static readonly int[] GroundLayers = { 24, 28, 29 };

        // Full side length of the MP position-compression volume while flying (see UpdateNetworkBounds).
        // 20 km / 2^17 ≈ 0.15 m resolution on X/Z — the reason it's applied only during flight, not
        // always. ModernAirCombat uses the same ~20 km figure.
        private const float MpNetworkWorldSize = 20000f;

        // Defog camera reach (8 km, per the request) and the fog it clears.
        private const float DefogFarClip = 8000f;

        // The two desired states, owned by the host control panel (host) / HostControlNet (client).
        public bool BoundaryOff;
        public bool DefogOn;

        private BoundingBoxController boundsController;
        private float nextCheck;

        // Boundary applied-state trackers.
        private bool wallRemoved;
        private bool groundRemoved;
        private readonly List<Collider> disabledGround = new List<Collider>();
        private bool netExpanded;
        private Bounds netOriginal;

        // Defog applied-state trackers.
        private bool defogApplied;
        private float originalFarClip;
        private bool originalRenderFog;
        private readonly List<GameObject> disabledFog = new List<GameObject>();

        private void Update()
        {
            if (Time.time < nextCheck)
            {
                return;
            }
            nextCheck = Time.time + CheckInterval;

            ApplyBoundary();
            ApplyDefog();
        }

        // ---------------------------------------------------------------- Boundary

        private void ApplyBoundary()
        {
            bool simulating = StatMaster.levelSimulating;

            // Walls: mirror BoundaryOff whenever a boundary is present (build + sim).
            if (BoundaryOff)
            {
                // Re-asserted against the live flag: a level reload re-enables bounds behind our back
                // (Machine.Init -> EnableBounds), and a player may re-arm the god-mode toggle.
                if (StatMaster.Bounding.Enabled)
                {
                    SetWall(false);
                }
                wallRemoved = true;
            }
            else if (wallRemoved)
            {
                SetWall(true);
                wallRemoved = false;
            }

            // Ground: only while simulating (build mode's placement tool relies on it).
            bool groundOff = BoundaryOff && simulating;
            if (groundOff && !groundRemoved)
            {
                SetGround(false);
                groundRemoved = true;
            }
            else if (!groundOff && groundRemoved)
            {
                SetGround(true);
                groundRemoved = false;
            }

            // MP compression range: only while flying (no-op in single-player).
            UpdateNetworkBounds(BoundaryOff && simulating);
        }

        // enabled == false -> take the walls down; enabled == true -> put them back.
        private void SetWall(bool enabled)
        {
            if (boundsController == null)
            {
                boundsController = Object.FindObjectOfType<BoundingBoxController>();
            }

            if (boundsController != null)
            {
                Machine machine = boundsController.machine;
                if (machine == null)
                {
                    machine = Machine.Active();
                }

                if (machine != null)
                {
                    if (enabled && !StatMaster.Bounding.Enabled)
                    {
                        boundsController.EnableBounds(machine);
                    }
                    else if (!enabled && StatMaster.Bounding.Enabled)
                    {
                        boundsController.DisableBounds(machine);
                    }
                }
                else
                {
                    // No machine to hand DisableBounds (its trailing Check() dereferences one). Fall
                    // back to the state it would have produced so the walls still come down.
                    StatMaster.Bounding.Enabled = enabled;
                    ToggleColliders(boundsController.colliders, enabled);
                }
            }
            else
            {
                StatMaster.Bounding.Enabled = enabled;
            }

            GameObject worldBounds = GameObject.Find("WORLD BOUNDARIES");
            if (worldBounds != null)
            {
                ToggleColliders(worldBounds.GetComponentsInChildren<Collider>(true), enabled);
            }

            Debug.Log(enabled
                ? "[BeyondSpider] world boundary restored."
                : "[BeyondSpider] world boundary removed.");
        }

        // enabled == false -> disable solid ground colliders; enabled == true -> re-enable exactly ours.
        private void SetGround(bool enabled)
        {
            if (enabled)
            {
                for (int i = 0; i < disabledGround.Count; i++)
                {
                    if (disabledGround[i] != null)
                    {
                        disabledGround[i].enabled = true;
                    }
                }
                disabledGround.Clear();
                Debug.Log("[BeyondSpider] ground collision restored.");
                return;
            }

            disabledGround.Clear();
            Collider[] all = Object.FindObjectsOfType<Collider>();
            for (int i = 0; i < all.Length; i++)
            {
                Collider c = all[i];
                // Skip triggers (gravity/water/kill volumes — not solid geometry) and colliders already
                // off for other reasons, so restore never turns on something we didn't turn off.
                if (c == null || !c.enabled || c.isTrigger || !IsGroundLayer(c.gameObject.layer))
                {
                    continue;
                }
                c.enabled = false;
                disabledGround.Add(c);
            }

            if (boundsController == null)
            {
                boundsController = Object.FindObjectOfType<BoundingBoxController>();
            }
            if (boundsController != null && boundsController.bottomCollider != null)
            {
                Collider bottom = boundsController.bottomCollider.GetComponent<Collider>();
                if (bottom != null && bottom.enabled)
                {
                    bottom.enabled = false;
                    disabledGround.Add(bottom);
                }
            }

            Debug.Log("[BeyondSpider] ground collision removed (" + disabledGround.Count + " colliders).");
        }

        // Widen NetworkCompression's world box while flying, restore it afterwards. Deterministic across
        // peers: same level-derived centre (read back from the public NetworkCompression.wMin*/wMax*,
        // identical on every peer) + a fixed size, so host encode and client decode stay agreed. See
        // ADR-0019.
        private void UpdateNetworkBounds(bool expand)
        {
            if (!StatMaster.isMP)
            {
                return;
            }

            if (expand)
            {
                Bounds current = CurrentNetworkBounds();
                if (current.size.x > 1f && current.size.x < MpNetworkWorldSize - 1f)
                {
                    netOriginal = current;
                    netExpanded = true;
                    NetworkCompression.SetWorldBounds(new Bounds(
                        current.center,
                        new Vector3(MpNetworkWorldSize, MpNetworkWorldSize, MpNetworkWorldSize)));
                    Debug.Log("[BeyondSpider] MP position-compression range widened for space flight.");
                }
            }
            else if (netExpanded)
            {
                NetworkCompression.SetWorldBounds(netOriginal);
                netExpanded = false;
                Debug.Log("[BeyondSpider] MP position-compression range restored.");
            }
        }

        private static Bounds CurrentNetworkBounds()
        {
            Bounds b = default(Bounds);
            b.SetMinMax(
                new Vector3(NetworkCompression.wMinX, NetworkCompression.wMinY, NetworkCompression.wMinZ),
                new Vector3(NetworkCompression.wMaxX, NetworkCompression.wMaxY, NetworkCompression.wMaxZ));
            return b;
        }

        private static bool IsGroundLayer(int layer)
        {
            for (int i = 0; i < GroundLayers.Length; i++)
            {
                if (GroundLayers[i] == layer)
                {
                    return true;
                }
            }
            return false;
        }

        private static void ToggleColliders(Collider[] colliders, bool enabled)
        {
            if (colliders == null)
            {
                return;
            }
            for (int i = 0; i < colliders.Length; i++)
            {
                if (colliders[i] != null)
                {
                    colliders[i].enabled = enabled;
                }
            }
        }

        // ---------------------------------------------------------------- Defog

        private void ApplyDefog()
        {
            Camera cam = Camera.main;

            if (DefogOn)
            {
                if (!defogApplied)
                {
                    originalFarClip = cam != null ? cam.farClipPlane : 0f;
                    originalRenderFog = RenderSettings.fog;
                    defogApplied = true;
                }
                // Re-assert every poll: the camera can be swapped (sim start/stop) and fog volumes
                // recreated, either of which would quietly bring the murk back.
                if (cam != null && cam.farClipPlane < DefogFarClip)
                {
                    cam.farClipPlane = DefogFarClip;
                }
                RenderSettings.fog = false;
                SweepFog();
            }
            else if (defogApplied)
            {
                if (cam != null && originalFarClip > 0f)
                {
                    cam.farClipPlane = originalFarClip;
                }
                RenderSettings.fog = originalRenderFog;
                for (int i = 0; i < disabledFog.Count; i++)
                {
                    if (disabledFog[i] != null)
                    {
                        disabledFog[i].SetActive(true);
                    }
                }
                disabledFog.Clear();
                defogApplied = false;
            }
        }

        // Disable every fog-volume object hanging off the game camera. Matched by name ("fog") rather
        // than the exact ModernAirCombat list so a renamed/extra volume is still caught; tracked so
        // ApplyDefog can switch exactly these back on.
        private void SweepFog()
        {
            GameObject mainCamera = GameObject.Find("Main Camera");
            if (mainCamera == null)
            {
                return;
            }
            Transform[] children = mainCamera.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < children.Length; i++)
            {
                GameObject go = children[i].gameObject;
                if (go == mainCamera)
                {
                    continue;
                }
                if (go.activeSelf && go.name.ToLower().Contains("fog"))
                {
                    go.SetActive(false);
                    if (!disabledFog.Contains(go))
                    {
                        disabledFog.Add(go);
                    }
                }
            }
        }
    }
}
