# Captain radar lock and heavy-missile fire control design

## Context

`docs/space-combat-framework.md` originally assigned "hosts or opens the 3D radar/command UI" to `舰船核心`/`SpaceShipCore`, but the original `策划案.md` design put the 3D radar view, click-to-lock, and command-of-weapons behavior on `船长`/`SpaceCaptainBlock`, and current code already reflects that split (`SpaceCaptainBlock` is the priority-switching command block; `SpaceShipCore` has no UI code at all). This spec builds the 3D radar/lock UI on Captain, matching `策划案.md` and codifying the split in `docs/adr/0003-captain-hosts-radar-lock-ui.md`. `docs/agent-besiege-mod-guide.md` roadmap item 10 ("强化重型导弹：发射状态、制导、生命值、爆炸效果、网络显示") is the umbrella this spec fills in (guidance + network display), and item 8 ("强化雷达轨迹") is partially advanced (hostility handling, not dedup/confidence/last-update-time, which remain deferred).

Cross-referencing existing code surfaced two bugs this feature depends on fixing, recorded in `docs/adr/0004-hostile-filtering-moves-to-consumers.md`:
- `RadarPanelBlock`'s `Iff` toggle drops same-team tracks from `ship.Tracks` entirely, so friendly ships/missiles — which this radar UI must show in green — never reached `ship.Tracks` at all. Hostility filtering moves to the consumers that actually need it (`DefenseDirectorBlock`), and `RadarPanelBlock` now reports everything in range/cone regardless of team.
- `HeavyNuclearMissileBlock.IsAlive` doesn't check `launched`, so an ally's still-mounted, unfired heavy missile would already show up as an independent radar target today. `IsAlive` now requires `launched`.

## Scope

**In scope**: `ShipState.LockedTarget`/`LockedSolution`, a `Captain`-hosted 3D radar screen (dedicated camera + RenderTexture, orbit/zoom/click-to-lock), heavy-missile live-guidance to the ship-wide lock (with active-brake on lost track), `CiwsBlock`/`SpaceGunnerBlock` reading the lock ahead of `DefenseDirectorBlock`'s auto solution when `ship.Priority == AntiShip`, full multiplayer sync of the lock, and the two existing-code fixes above.

**Explicitly deferred**:
- Sensor track dedup, confidence, and last-update-time (agent-guide roadmap item 8) — out of scope, `SensorTrack` is unchanged beyond what's needed here.
- `SpaceInterceptorLauncherBlock` (point-defense missiles) reading the manual lock — 策划案 only ties captain command to "炮手/单零件炮"; point defense stays purely `DefenseDirector`-driven.
- Any change to `RadarPanelBlock.Iff`'s build-time behavior beyond removing its consumption — the slider/key stays for save compatibility but is currently unused.
- Turn-rate-limited missile steering — heavy missiles reuse `SpaceInterceptorMissile`'s omnidirectional-thruster model verbatim (instant thrust-vector redirect, cosmetic rotation to face velocity).
- Multi-target lock, target deconfliction across multiple heavy missiles — one ship-wide `LockedTarget`, shared by everything that reads it.

## Components

### 0. Captain's transform is the ship's orientation

The captain block's own `transform.right`/`transform.up`/`transform.forward` is the canonical definition of the ship's orientation — not `SpaceShipCore`'s. This is a general fact about the ship model going forward (see the updated `舰船核心`/`船长` entries in `CONTEXT.md` and the `ShipCore`/`Captain` bullets in `docs/space-combat-framework.md`), not something scoped only to this feature: every basis conversion below (`captain.InverseTransformVector`/`InverseTransformDirection` in §4, and any future system that needs "ship forward/right") reads Captain's transform. `SpaceShipCore` still owns position/center/volume/ownership (§ADR-0003), just not orientation. A ship with no Captain block has no defined orientation for these purposes — consistent with it also having no radar UI or lock capability.

### 1. Lockable targets and ship-wide lock state

`SpaceCombatCore.cs` adds:

```csharp
public interface ILockable : ITrackable
{
    int GuidHash { get; }
}
```

Only `SpaceShipCore` and `HeavyNuclearMissileBlock` implement `ILockable`, each computing `GuidHash = BlockBehaviour.BuildingBlock.Guid.GetHashCode()` in `SafeAwake` (same pattern `SpaceFlakTurretBlock.GuidHash` already uses). This is also why the radar screen only ever renders `TrackKind.Ship` and `TrackKind.HeavyMissile` blips: those are the only two kinds with a Guid-backed identity that resolves consistently across clients, which the network sync in §7 depends on. `DefensiveMissile`/`LargeProjectile` tracks are not shown on this screen at all (unchanged elsewhere, e.g. `DefenseDirectorBlock` still sees them).

`ShipState` adds:

```csharp
public ITrackable LockedTarget;
public FireSolution LockedSolution;
```

Both ship-wide, single-target (not per-weapon, not per-missile). `LockedTarget` is set/cleared by radar-screen clicks (§5) and by incoming network messages (§7); nothing else writes it.

### 2. Hostility and coloring

`RadarPanelBlock.SimulateFixedUpdateHost` drops its `if (Iff.IsActive && target.Team == Team) continue;` line entirely — `ship.Tracks` now contains every trackable in range/cone regardless of team. `Iff` stays as a build-mode toggle (save/XML compatibility) but is no longer read anywhere.

`DefenseDirectorBlock.SimulateFixedUpdateHost` adds a hostility check inside its scoring loop: `if (!SpaceBallistics.IsHostile(this, track.Target)) continue;` before computing `threat`/`ttiScore`. This restores today's "auto-intercept only touches enemies" behavior now that `ship.Tracks` itself is unfiltered.

The radar screen (§4) colors every Ship/HeavyMissile marker using the same helper: `SpaceBallistics.IsHostile(ship.Captain, track.Target) ? red : green`. `IsHostile` already treats same-team-different-player as friendly (`target.PlayerID != block.PlayerID && target.Team != block.Team`), so allied players' ships/missiles render green, enemies render red. A ship's own objects never appear on its own radar in the first place (`RadarPanelBlock` already skips `target.PlayerID == PlayerID`), so there's no "self" marker to color.

### 3. Heavy missile radar visibility

`HeavyNuclearMissileBlock.IsAlive` becomes:

```csharp
public bool IsAlive { get { return launched && BlockBehaviour != null && BlockBehaviour.isSimulating && health > 0f; } }
```

A missile is already registered as an `ITrackable` in `OnSimulateStart` (unchanged), but reports not-alive until `launched` flips true, so it's invisible to every radar (including its own ship's, and allies') while still mounted. `ShieldProjectorBlock`/`CiwsBlock`, which both gate on `IsAlive` before touching a missile, are unaffected in practice since nothing interacts with a still-mounted missile anyway.

### 4. Radar screen rendering

New file `CaptainRadarView.cs`, a `MonoBehaviour` singleton attached to `Mod.Root` (created lazily on first open, matching `Mod.cs`'s existing root-object pattern). Holds:

- A "pocket scene" root `Transform` positioned once at a fixed, far-off world coordinate (`(0, 500000, 0)`) so it can never appear in or collide with anything in real play space, and no Unity layer/culling-mask configuration is needed — nothing else exists out there for `radarCamera`'s default culling mask to accidentally pick up.
- `gimbal`: child of the pocket root, holds the `Camera` at a fixed local offset (e.g. `(0, 0, -9)`) always looking at the pocket root's origin. Only `gimbal`'s local rotation changes (yaw/pitch from drag, §5) — camera-orbits-static-content, not the reverse, so marker/grid local-position math never has to account for view rotation.
- `radarCamera`: perspective, FOV ~45°, `clearFlags = SolidColor` (dark navy), `targetTexture` = a 512×512 `RenderTexture`, `enabled` only while the panel is open.
- `gridObject`: a flat polar grid mesh (concentric rings + radial spokes, built once — no rebuild needed since it never represents a changing shape, only the *meaning* of its scale changes with zoom) sitting in the pocket root's local XZ plane, fixed visual radius `DisplayRadius = 4` (pocket-scene units, unrelated to meters).
- A pooled `List<GameObject>` of markers (arrow prefab for `Ship`, sphere prefab for `HeavyMissile`) plus a `Dictionary<GameObject, ITrackable>` for click resolution (§5) and a `Dictionary<ITrackable, GameObject>` for reuse across frames — pool grows on demand, excess pooled instances are deactivated rather than destroyed.
- A separate lock-icon `GameObject` (a billboard quad textured with `ModResource.GetTexture("BS Migrated AA Lock Texture").Texture`, i.e. the existing unused `WW2Migrated/AAController/AALock.png`), enabled and repositioned onto whichever marker currently represents `ship.LockedTarget`, disabled otherwise.

Arrow marker mesh: a small flat dart/kite shape (5 vertices, two triangles), built once procedurally the same way the shield's polar grid is (explicit vertex/triangle arrays, no imported asset) since no arrow mesh exists in `Resources`. Sphere marker: `GameObject.CreatePrimitive(PrimitiveType.Sphere)`, per the request's "小圆球" wording.

Per open ship's `Captain` transform, each frame while the panel is open:

```
metersPerUnit: float, default 50, adjustable by scroll (§5), clamped [5, 2000]
for each track in ship.Tracks where Kind is Ship or HeavyMissile:
    relative = captain.InverseTransformVector(track.Position - captain.position)
    displayPos = relative / metersPerUnit
    if displayPos.magnitude > DisplayRadius: displayPos = displayPos.normalized * DisplayRadius   // clamp to the visible dish instead of leaving frame
    marker.localPosition = displayPos              // under the static pocket root, NOT the orbiting gimbal
    marker.localScale = MarkerScale (constant)      // never touched by zoom — this is the "arrow mesh itself doesn't scale" requirement
    if track.Kind == Ship:
        velLocal = captain.InverseTransformDirection(track.Velocity)
        marker.localRotation = velLocal.sqrMagnitude > 0.01
            ? Quaternion.LookRotation(velLocal.normalized, Vector3.up)
            : Quaternion.identity                    // near-stationary target: keep last/default facing
    marker.GetComponent<Renderer>().material.color = SpaceBallistics.IsHostile(ship.Captain, track.Target) ? Color.red : Color.green
```

A text label (`GUI.Label` in the same panel, not part of the 3D scene) shows the current scale, e.g. `"1 grid = {metersPerUnit * DisplayRadius / RingCount:0}m"`, which is what changes when the player zooms — the grid mesh itself never rebuilds.

### 5. Input and the HUD button

`SpaceCombatRuntime.OnGUI` (existing debug window) gets one more line: when `ship.Captain != null`, a `GUILayout.Button("Open Radar" / "Close Radar")` that flips a bool on `CaptainRadarView`, which enables/disables `radarCamera` and starts/stops the per-frame update in §4.

`CaptainRadarView` draws its own panel (a fixed-size `Rect`, e.g. bottom-right, 420×420) via `GUI.Window`/`GUI.DrawTexture` showing `radarTexture`, only while open. All mouse handling is gated on the panel `Rect`:

- **Orbit**: a drag starts only if `Input.GetMouseButtonDown(1)` occurs with `Input.mousePosition` inside the panel rect (GUI-space Y needs flipping against `Input.mousePosition`'s bottom-left origin). While the drag is held, `gimbal`'s local yaw/pitch accumulate from `Input.GetAxis("Mouse X")`/`"Mouse Y")`; pitch clamped to `[-85, 85]`. Once started, the drag continues to track even if the cursor leaves the rect (standard drag UX), and stops on mouse-up.
- **Zoom**: `Input.GetAxis("Mouse ScrollWheel")` only counts while the cursor is over the rect (no drag needed); each nonzero tick multiplies/divides `metersPerUnit` by `1.15`.
- **Lock/unlock**: `Input.GetMouseButtonUp(0)` counts as a click only if both mouse-down and mouse-up occurred inside the rect and cursor movement between them stayed under a small pixel threshold (distinguishes a click from a drag). On a valid click, convert the click position into a fraction of the rect, build a `Ray` via `radarCamera.ScreenPointToRay` (using `radarTexture.width/height`-scaled coordinates, which is what `Camera.pixelWidth/pixelHeight` resolve to for a camera with a `targetTexture`), and `Physics.Raycast` against the pooled markers' colliders (a small `SphereCollider` per marker; the pocket scene has nothing else in it, so an unfiltered raycast is safe). Resolve the hit `GameObject` back to its `ITrackable` via the pool's dictionary, then:
  - if it's already `ship.LockedTarget`: clear the lock
  - otherwise: set it as the new lock
  
  Either way, send the network message in §7 and set `ship.LockedTarget` optimistically/locally right away for responsiveness (the network round-trip only matters for the host's authoritative copy and other clients).

Outside the rect (no drag in progress, no hover), the player's normal ship look/pilot controls are completely untouched — this radar view never captures the mouse globally.

### 6. Heavy missile guidance

`HeavyNuclearMissileBlock` adds `private bool hadGuidance;` and rewrites the post-launch half of `SimulateFixedUpdateHost`:

```csharp
ShipState ship = OwnShip();
ITrackable target = ship == null ? null : ship.LockedTarget;
bool trackedThisTick = false;
if (target != null && target.IsAlive)
{
    for (int i = 0; i < ship.Tracks.Count; i++)
    {
        if (ReferenceEquals(ship.Tracks[i].Target, target)) { trackedThisTick = true; break; }
    }
}

if (trackedThisTick)
{
    hadGuidance = true;
    Vector3 intercept = target.Position + target.Velocity * 0.35f;   // same lead heuristic as SpaceInterceptorMissile
    Vector3 desired = (intercept - transform.position).normalized;
    Body.AddForce(desired * Thrust.Value, ForceMode.Force);
    if (Body.velocity.sqrMagnitude > 1f)
        transform.rotation = Quaternion.LookRotation(Body.velocity.normalized, Vector3.up);
}
else if (hadGuidance)
{
    // was homing at some point (this tick's lock is gone, dead, or radar-untracked); brake to a stop
    float speed = Body.velocity.magnitude;
    if (speed > 0.05f)
    {
        float maxDecelForce = speed * Body.mass / Time.fixedDeltaTime;  // don't overshoot past zero this tick
        Body.AddForce(-Body.velocity.normalized * Mathf.Min(Thrust.Value, maxDecelForce), ForceMode.Force);
    }
    else
    {
        Body.velocity = Vector3.zero;
    }
}
else
{
    Body.AddForce(transform.forward * Thrust.Value, ForceMode.Force);   // never had a lock: legacy straight flight
}
```

`hadGuidance` latches permanently once true, so: a missile launched with an active, tracked lock homes immediately and brakes on any later loss (radar dropout *or* the captain clearing/changing the lock, both look identical from the missile's perspective — it just re-reads `ship.LockedTarget` and `ship.Tracks` every tick, live). A missile launched with no lock at all flies straight until (if ever) a lock appears and is tracked, at which point it starts homing like any other. `ApplyShieldDeceleration`/`ApplyDamage` are untouched.

### 7. `CiwsBlock`/`SpaceGunnerBlock` reading the manual lock

`SpaceCaptainBlock.SimulateFixedUpdateHost` computes `ship.LockedSolution` every tick (Captain owns the lock, so Captain computes what it implies for weapons, mirroring how `DefenseDirectorBlock` computes `ship.DefensiveSolution`):

```csharp
ShipState ship = OwnShip();
if (ship == null) return;
ship.LockedSolution = default(FireSolution);
if (ship.LockedTarget != null && ship.LockedTarget.IsAlive)
{
    for (int i = 0; i < ship.Tracks.Count; i++)
    {
        SensorTrack track = ship.Tracks[i];
        if (!ReferenceEquals(track.Target, ship.LockedTarget)) continue;
        ship.LockedSolution.Target = track.Target;
        ship.LockedSolution.AimPoint = track.Position + track.Velocity * Mathf.Clamp(track.TimeToImpact, 0f, 2f);
        ship.LockedSolution.TimeToImpact = track.TimeToImpact;
        break;
    }
}
```

If the locked target isn't in `ship.Tracks` this tick, `ship.LockedSolution.Target` stays null — CIWS/gunner naturally fall back to `DefenseDirector`'s solution in that moment (see below), consistent with how the missile brakes when it can't see its target either.

`CiwsBlock` and `SpaceGunnerBlock` (via `RailgunBarrelBlock.TryFireControl`) each change their one `ship.DefensiveSolution.Target`/`AimPoint` read to pick a solution first:

```csharp
FireSolution solution = (ship.Priority == CommandPriority.AntiShip && ship.LockedSolution.Target != null)
    ? ship.LockedSolution
    : ship.DefensiveSolution;
```

`CiwsBlock` still casts `solution.Target as HeavyNuclearMissileBlock` and no-ops if that fails, so a manual lock on an enemy `SpaceShipCore` naturally never engages CIWS — only railgun/gunner (already ship-agnostic) act on it. This is the first real consumer of `ShipState.Priority`, which previously existed only as a debug-HUD readout.

### 8. Multiplayer sync

New file addition (in `SpaceCaptainBlock.cs`, alongside the block it serves, mirroring where `SpaceFlakTurretNet` sits next to `SpaceFlakTurretBlock`):

```csharp
public class CaptainLockNet : SingleInstance<CaptainLockNet>
{
    public static MessageType LockMsg = ModNetworking.CreateMessageType(
        DataType.Integer, DataType.Integer, DataType.Integer, DataType.Boolean);
        // (shipPlayerId, targetPlayerId, targetGuidHash, hasLock)

    public void LockReceiver(Message m)
    {
        int shipPlayerId = m.GetData<int>(0);
        int targetPlayerId = m.GetData<int>(1);
        int targetGuidHash = m.GetData<int>(2);
        bool hasLock = m.GetData<bool>(3);

        ShipState ship = SpaceCombatRegistry.FindShip(shipPlayerId);
        if (ship == null) return;
        if (!hasLock) { ship.LockedTarget = null; return; }

        IList<ITrackable> all = SpaceCombatRegistry.Trackables;
        for (int i = 0; i < all.Count; i++)
        {
            ILockable candidate = all[i] as ILockable;
            if (candidate != null && candidate.PlayerID == targetPlayerId && candidate.GuidHash == targetGuidHash)
            {
                ship.LockedTarget = candidate;
                return;
            }
        }
    }
}
```

Registered in `Mod.cs`'s `OnLoad` the same way `SpaceFlakTurretNet`'s callbacks are. `CaptainRadarView`'s click handler (§5) sends `ModNetworking.SendToAll(CaptainLockNet.LockMsg.CreateMessage(ship.Captain.PlayerID, targetPlayerId, targetGuidHash, hasLock))` — broadcast to everyone (not just host) so every client's copy of `ship.LockedTarget` (used for that ship's own lock-icon rendering on anyone who can see it, and read by `SimulateFixedUpdateHost` wherever it actually runs) stays consistent, the same all-clients-broadcast approach `SpaceFlakTurretNet` already uses. No late-join resync exists for this message, consistent with the rest of the mod's current MP maturity (matches the shield spec's precedent of not adding sync infrastructure beyond what other systems already have).

## Data flow summary

1. Build mode: unchanged for existing blocks; no new sliders/toggles, no new placeable blocks, no `Mod.xml`/XML changes.
2. Simulation start: `SpaceShipCore`/`HeavyNuclearMissileBlock` compute their `GuidHash`; everything registers as today.
3. Every `RadarPanelBlock` scan tick: `ship.Tracks` now includes both factions in range/cone (no IFF drop).
4. Every `DefenseDirectorBlock` tick: filters `ship.Tracks` by `SpaceBallistics.IsHostile` before scoring into `ship.DefensiveSolution`.
5. Every `SpaceCaptainBlock` tick: resolves `ship.LockedTarget` against this tick's `ship.Tracks` into `ship.LockedSolution` (or clears it if untracked).
6. While the radar panel is open: `CaptainRadarView` repositions/colors pooled Ship/HeavyMissile markers from `ship.Tracks` each frame; drag/scroll/click within its `Rect` drive orbit/zoom/lock, sending `CaptainLockNet.LockMsg` on a lock change.
7. Every `HeavyNuclearMissileBlock` tick (once launched): reads `ship.LockedTarget` live against this tick's `ship.Tracks` to home, brake, or fly straight per §6.
8. Every `CiwsBlock`/`SpaceGunnerBlock` tick: picks `ship.LockedSolution` or `ship.DefensiveSolution` per `ship.Priority`.

## Constants (tunable, not precision-engineered)

- Pocket scene origin `(0, 500000, 0)`; `DisplayRadius = 4`; gimbal camera offset `(0, 0, -9)`, FOV ~45°.
- `RenderTexture` 512×512; panel `Rect` 420×420, bottom-right.
- `metersPerUnit` default 50, clamp `[5, 2000]`, scroll step ×1.15.
- Pitch clamp `[-85, 85]`.
- Lead heuristic `0.35` (unchanged from `SpaceInterceptorMissile`).

## Files touched

- Modify: `src/BeyondSpiderAssembly/SpaceCombatCore.cs` (`ILockable`, `ShipState.LockedTarget`/`LockedSolution`)
- Modify: `src/BeyondSpiderAssembly/SpaceShipCore.cs` (`ILockable` + `GuidHash`)
- Modify: `src/BeyondSpiderAssembly/HeavyNuclearMissileBlock.cs` (`ILockable` + `GuidHash`, `IsAlive` gated on `launched`, guidance rewrite)
- Modify: `src/BeyondSpiderAssembly/RadarPanelBlock.cs` (drop IFF filtering)
- Modify: `src/BeyondSpiderAssembly/DefenseDirectorBlock.cs` (add `IsHostile` filter)
- Modify: `src/BeyondSpiderAssembly/CiwsBlock.cs`, `RailgunBarrelBlock.cs` (`SpaceGunnerBlock`) (prefer `ship.LockedSolution` per `ship.Priority`)
- Modify: `src/BeyondSpiderAssembly/SpaceCaptainBlock.cs` (`LockedSolution` computation, `CaptainLockNet`)
- Modify: `src/BeyondSpiderAssembly/SpaceCombatCore.cs`'s `SpaceCombatRuntime.OnGUI` (Open/Close Radar button)
- Modify: `src/BeyondSpiderAssembly/Mod.cs` (register `CaptainLockNet` callback)
- New: `src/BeyondSpiderAssembly/CaptainRadarView.cs` (camera/RenderTexture setup, pocket scene, marker pool, input, panel `OnGUI`)
- No `Mod.xml`/block XML changes — no new placeable block, and the lock icon reuses the already-registered-but-unused `"BS Migrated AA Lock Texture"`.
- Docs already updated this session: `CONTEXT.md`, `docs/space-combat-framework.md`, `docs/agent-besiege-mod-guide.md`, `docs/adr/0003-captain-hosts-radar-lock-ui.md`, `docs/adr/0004-hostile-filtering-moves-to-consumers.md`.

## Self-review

- **Placeholder scan**: no TBDs; every new field, message shape, and formula has a concrete starting value.
- **Internal consistency**: `ship.LockedSolution` and the missile's own live `ship.LockedTarget`/`ship.Tracks` check both go stale the same way (target not in this tick's `ship.Tracks`), so CIWS/gunner and the heavy missile lose and regain "aim" at the same moments, not independently.
- **Scope check**: sized for one implementation plan — radar rendering, lock networking, and missile guidance are coupled (the missile literally reads the same `LockedTarget`/`Tracks` the screen writes/reads), not independent subsystems. The `Priority`-arbitrated CIWS/gunner piece is additive scope confirmed mid-session, not a separate spec.
- **Ambiguity check**: host block (Captain vs ShipCore), hostile-filter ownership, lock binding (live vs snapshot), no-lock-at-launch fallback, guidance model (omni-thruster vs turn-rate-limited), un-launched-missile visibility, lock-icon asset, and the CIWS/gunner priority arbitration were all explicitly resolved with the user rather than assumed.
