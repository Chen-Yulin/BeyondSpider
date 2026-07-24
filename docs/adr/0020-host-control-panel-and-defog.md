# Host control panel + defog, and boundary removal made button-driven

**Status**: implemented, builds clean (`dotnet build`, 0 errors); pending in-game (and double-client)
playtest.

New files: `HostControlPanel.cs` (top-left uGUI panel), `HostControlNet.cs` (MP state sync). Touched:
`SpaceBoundary.cs` (now flag-driven + defog), `Mod.cs` (registers the two components + the net
callback), `BeyondSpiderAssembly.csproj`.

**Ask (user).** A host control panel top-left that collapses to the left (a room-info panel on
clients). Move all of the boundary removal — including the MP compression-box widening — behind a host
**"无边界"** (no boundary) toggle: single-player host enabling it removes the boundary; MP host enabling
it removes the boundary and tells clients so they sync. Add a **"去雾"** (defog) toggle that extends the
camera view to 8 km and clears the fog, synced to clients; defog referencing `../ModernAirCombat`.

---

## Boundary removal: auto → button-driven

ADR-0019 removed the boundary automatically whenever a `SpaceShipCore` existed. That's now replaced by
an explicit host toggle. `SpaceBoundary` became a `SingleInstance` holding two public desired-state
flags — `BoundaryOff` and `DefogOn` — and its `Update` just makes the scene match them (idempotent,
2 Hz), no longer polling for ship presence. Consequence the host must know: since the boundary is no
longer auto-removed, **an oversized hull can't start simulating until the host enables 无边界 in build
mode** (the out-of-bounds gate is `StatMaster.Bounding.Enabled`, which 无边界 clears).

Who writes the flags: on the host (SP or MP host) the panel toggles; on an MP client, `HostControlNet`
writes the host's values in. The three boundary layers (walls / ground / MP compression box) are
unchanged from ADR-0019 — only their *trigger* moved from "ship present" to "`BoundaryOff`".

## Defog (`DefogOn`)

Extends `Camera.main.farClipPlane` to 8 km and clears the level fog: `RenderSettings.fog = false` plus
disabling the fog-volume children of the `Main Camera` object (`Fog Volume` / `Fog Volume Dark` /
`FOG SPHERE` — the objects ModernAirCombat's `ModController.SetFog` toggles). Matched by name containing
"fog" rather than the exact MAC list, so a renamed/extra volume is still caught, and tracked so restore
switches exactly those back on. Re-asserted every poll because the camera can be swapped (sim
start/stop) and fog volumes recreated. Purely local to each peer's own camera — the sync just carries
the toggle so a client defogs its own view too.

## MP sync (`HostControlNet`)

A `SingleInstance` with one message, `StateMsg(bool boundaryOff, bool defogOn)`. The host broadcasts on
change **and on a 1 s heartbeat**; clients only apply (`StateReceiver` → `SpaceBoundary.Instance`
flags), never send. The heartbeat exists for late joiners, and is deliberately short: the widened MP
position-compression box (ADR-0019) only round-trips when host and client agree on it, so a client that
joins while 无边界 is on decodes *every* block's position wrong until it catches a heartbeat — 1 s bounds
that window. This is why 无边界 must sync at all, not just be a local host convenience.

## The panel (`HostControlPanel`)

Procedural uGUI via the `BeyondSpiderUI` helpers, same as `BeyondSpiderInfoPanel`. Docked top-left;
**collapse slides the body horizontally off the left screen edge** (the header stays as the re-open
handle), rather than the info panel's vertical height animation — the screen edge clips it, so no mask
is needed. Role-adaptive, rebuilt on a host/client role change:

- **Host** (`NetAuthority.IsAuthority`): two toggle rows, `NO BOUNDARY` and `DEFOG`, driving the
  `SpaceBoundary` flags. Row colour reflects on/off (the Toggle's `transition` is set to `None` so its
  built-in hover tint doesn't fight the per-frame colour).
- **Client** (`NetAuthority.IsClient`): a read-only room-info panel — role, the synced NO BOUNDARY /
  DEFOG states, and `StatMaster.activePlayerCount`.

## Deliberate limitations

- **Labels are ASCII English** (`CONTROL`, `NO BOUNDARY`, `DEFOG`, `ROOM INFO`), not the Chinese the
  request used: the uGUI panels render with Unity's builtin Arial, which has no CJK glyphs (same reason
  the radar/info HUD is English) — Chinese would come out as boxes. A bundled CJK font asset is the only
  way to change that.
- **Compression-box widening stays flag-gated (not always-on)** per the request that 无边界 own it,
  accepting the ≤1 s all-blocks-misplaced window on a mid-session join described above. Widening
  unconditionally during MP sim would remove that window at the cost of coarser precision whenever
  simulating; not chosen because the user tied it to the button.
