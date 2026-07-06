# Captain Radar Lock and Heavy-Missile Fire Control Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the ship's Captain block a 3D radar screen with orbit/zoom/click-to-lock, make heavy missiles home on the ship-wide lock (braking to a stop if radar loses it), and let `CiwsBlock`/`SpaceGunnerBlock` fire on a manual lock ahead of `DefenseDirectorBlock`'s auto solution when the ship is in anti-ship priority.

**Architecture:** `ShipState` gains a single ship-wide `LockedTarget`/`LockedSolution` pair. Only `SpaceShipCore` and `HeavyNuclearMissileBlock` implement a new `ILockable` interface (Guid-backed identity for cross-network resolution), which is also why the radar screen only ever renders those two track kinds. Hostility filtering moves out of `RadarPanelBlock` and into `DefenseDirectorBlock` (via `SpaceBallistics.IsHostile`), so `ship.Tracks` reports both factions and the radar screen can color friend green / foe red. The radar screen itself (`CaptainRadarView`, a `Mod.Root`-level singleton) renders a "pocket scene" — a grid and pooled arrow/sphere markers sitting at a fixed, far-off world coordinate — through a dedicated orbiting `Camera`/`RenderTexture`, displayed as an `OnGUI` panel. Heavy missiles and `CiwsBlock`/`SpaceGunnerBlock` read `ship.LockedTarget`/`ship.LockedSolution` directly; nothing about them depends on the radar screen being open. Lock changes are broadcast via a new `ModNetworking` message (`CaptainLockNet`), following the exact pattern `SpaceFlakTurretNet` already establishes.

**Tech Stack:** C# (.NET Framework 3.5), Besiege ModLoader (`Modding`/`Modding.Blocks`), Unity (old API surface, no C# 7+ syntax).

## Global Constraints

- **No build toolchain available in this environment** (same situation as the nano-armor, railgun, and paraboloid-shield plans): no `dotnet`/`msbuild`/`mono`, and the `.csproj`'s `Managed/` DLL paths don't resolve here. Verification below is grep-based reference checks and manual code-trace review, not an actual compile. A real Unity/Besiege build is required to confirm this compiles and behaves correctly in-game.
- **No C# 7+ syntax** (inline `out var`, tuples, pattern matching, etc.) — matches the rest of `src/BeyondSpiderAssembly` and the `.NET Framework 3.5` target.
- **No `Mod.xml`/block XML changes anywhere in this plan** — no new placeable block, no new sliders/toggles/keys on any existing block, so no save-compatibility risk. The lock icon reuses the already-registered-but-unused resource `"BS Migrated AA Lock Texture"` (`WW2Migrated\AAController\AALock.png`).
- **The captain block's own transform is the ship's orientation** (`docs/adr/0003-captain-hosts-radar-lock-ui.md`, `CONTEXT.md`'s 船长 entry) — every basis conversion in this plan reads `ship.Captain.transform`, never `ship.Core.transform`, for right/forward/up.
- **Only `TrackKind.Ship` and `TrackKind.HeavyMissile` are lockable/radar-screen-visible** — `DefensiveMissile`/`LargeProjectile` tracks are untouched and still flow to `DefenseDirectorBlock` as before, just never drawn on this screen.
- Follow existing codebase conventions: `SpaceBlock` subclasses use `SafeAwake`/`OnSimulateStart`/`OnSimulateStop`/`SimulateFixedUpdateHost`/`SimulateUpdateHost`; `GuidHash` is computed in `OnSimulateStart` as `BlockBehaviour.BuildingBlock.Guid.GetHashCode()` (see `SpaceFlakTurretBlock.cs:163`); `ModNetworking` message classes (`SingleInstance<T>` subclasses with a `Name` override, `MessageType`s created via `ModNetworking.CreateMessageType`, data read back via `(int)msg.GetData(0)`-style casts, not the generic `GetData<T>()` form) mirror `SpaceFlakTurretNet` exactly.

---

### Task 1: `ILockable` interface and `ShipState` lock fields

**Files:**
- Modify: `src/BeyondSpiderAssembly/SpaceCombatCore.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `ILockable` (interface, `int GuidHash { get; }` extending `ITrackable`), `ShipState.LockedTarget` (`ITrackable`), `ShipState.LockedSolution` (`FireSolution`) — all consumed by every later task in this plan.

- [ ] **Step 1: Add the `ILockable` interface**

In `src/BeyondSpiderAssembly/SpaceCombatCore.cs`, find:

```csharp
    public interface ITrackable
    {
        int PlayerID { get; }
        MPTeam Team { get; }
        TrackKind Kind { get; }
        Vector3 Position { get; }
        Vector3 Velocity { get; }
        float Radius { get; }
        bool IsAlive { get; }
    }
```

replace with:

```csharp
    public interface ITrackable
    {
        int PlayerID { get; }
        MPTeam Team { get; }
        TrackKind Kind { get; }
        Vector3 Position { get; }
        Vector3 Velocity { get; }
        float Radius { get; }
        bool IsAlive { get; }
    }

    // Only trackables with a stable, Guid-backed identity can be locked from the radar screen —
    // that identity is what CaptainLockNet resolves across clients (see Task 7). Ships and heavy
    // missiles are the only ITrackable kinds that are also placed blocks with a BuildingBlock.Guid.
    public interface ILockable : ITrackable
    {
        int GuidHash { get; }
    }
```

- [ ] **Step 2: Add the lock fields to `ShipState`**

Find:

```csharp
        public readonly List<SensorTrack> Tracks = new List<SensorTrack>();
        public FireSolution DefensiveSolution;
        public float LastRefreshTime;
    }
```

replace with:

```csharp
        public readonly List<SensorTrack> Tracks = new List<SensorTrack>();
        public FireSolution DefensiveSolution;
        public ITrackable LockedTarget;
        public FireSolution LockedSolution;
        public float LastRefreshTime;
    }
```

- [ ] **Step 3: Grep-verify both additions landed exactly once**

Run: `grep -n "interface ILockable\|LockedTarget\|LockedSolution" src/BeyondSpiderAssembly/SpaceCombatCore.cs`
Expected: `interface ILockable` once, `LockedTarget` once (field declaration), `LockedSolution` once (field declaration).

- [ ] **Step 4: Commit**

```bash
git add src/BeyondSpiderAssembly/SpaceCombatCore.cs
git commit -m "feat: add ILockable interface and ship-wide lock state"
```

---

### Task 2: `SpaceShipCore`/`HeavyNuclearMissileBlock` become `ILockable`; missiles hide until launched

**Files:**
- Modify: `src/BeyondSpiderAssembly/SpaceShipCore.cs`
- Modify: `src/BeyondSpiderAssembly/HeavyNuclearMissileBlock.cs`

**Interfaces:**
- Consumes: `ILockable` (Task 1).
- Produces: `SpaceShipCore.GuidHash` (`int`), `HeavyNuclearMissileBlock.GuidHash` (`int`) — both consumed by Task 7 (`CaptainLockNet`) and Task 9 (radar marker click resolution). `HeavyNuclearMissileBlock.IsAlive` now requires `launched` — consumed by every radar/shield/CIWS check that already gates on `IsAlive`.

- [ ] **Step 1: `SpaceShipCore` implements `ILockable`**

In `src/BeyondSpiderAssembly/SpaceShipCore.cs`, find:

```csharp
    public class SpaceShipCore : SpaceBlock, ITrackable
    {
        public MSlider TotalPower;
        public MSlider ArmorPowerShare;
        public MSlider ShieldPowerShare;
        public MSlider WeaponPowerShare;


        public string DisplayName;

        public TrackKind Kind { get { return TrackKind.Ship; } }
        public Vector3 Position { get { return transform.position; } }
        public Vector3 Velocity { get { return Body == null ? Vector3.zero : Body.velocity; } }
        public float Radius { get { return Mathf.Max(4f, transform.lossyScale.magnitude * 8f); } }
        public bool IsAlive { get { return BlockBehaviour != null && BlockBehaviour.isSimulating; } }
```

replace with:

```csharp
    public class SpaceShipCore : SpaceBlock, ILockable
    {
        public MSlider TotalPower;
        public MSlider ArmorPowerShare;
        public MSlider ShieldPowerShare;
        public MSlider WeaponPowerShare;


        public string DisplayName;

        public int GuidHash { get; private set; }

        public TrackKind Kind { get { return TrackKind.Ship; } }
        public Vector3 Position { get { return transform.position; } }
        public Vector3 Velocity { get { return Body == null ? Vector3.zero : Body.velocity; } }
        public float Radius { get { return Mathf.Max(4f, transform.lossyScale.magnitude * 8f); } }
        public bool IsAlive { get { return BlockBehaviour != null && BlockBehaviour.isSimulating; } }
```

- [ ] **Step 2: Compute `GuidHash` in `OnSimulateStart`**

Find:

```csharp
        public override void OnSimulateStart()
        {
            base.OnSimulateStart();
            DisplayName = "P" + PlayerID + " Core";
            SpaceCombatRegistry.RegisterCore(this);
            ApplyReactorMass();
        }
```

replace with:

```csharp
        public override void OnSimulateStart()
        {
            base.OnSimulateStart();
            GuidHash = BlockBehaviour.BuildingBlock.Guid.GetHashCode();
            DisplayName = "P" + PlayerID + " Core";
            SpaceCombatRegistry.RegisterCore(this);
            ApplyReactorMass();
        }
```

- [ ] **Step 3: `HeavyNuclearMissileBlock` implements `ILockable`, and `IsAlive` requires `launched`**

In `src/BeyondSpiderAssembly/HeavyNuclearMissileBlock.cs`, find:

```csharp
    public class HeavyNuclearMissileBlock : SpaceBlock, ITrackable
    {
        public MKey Launch;
        public MSlider HealthSlider;
        public MSlider Thrust;
        public MSlider ThreatMassSlider;
        public MSlider BlastRadius;
        public MToggle AutoLaunch;

        private bool launched;
        private float health;

        public TrackKind Kind { get { return TrackKind.HeavyMissile; } }
        public Vector3 Position { get { return transform.position; } }
        public Vector3 Velocity { get { return Body == null ? Vector3.zero : Body.velocity; } }
        public float Radius { get { return 3f; } }
        public bool IsAlive { get { return BlockBehaviour != null && BlockBehaviour.isSimulating && health > 0f; } }
        public float ThreatMass { get { return ThreatMassSlider.Value; } }
```

replace with:

```csharp
    public class HeavyNuclearMissileBlock : SpaceBlock, ILockable
    {
        public MKey Launch;
        public MSlider HealthSlider;
        public MSlider Thrust;
        public MSlider ThreatMassSlider;
        public MSlider BlastRadius;
        public MToggle AutoLaunch;

        private bool launched;
        private float health;

        public int GuidHash { get; private set; }

        public TrackKind Kind { get { return TrackKind.HeavyMissile; } }
        public Vector3 Position { get { return transform.position; } }
        public Vector3 Velocity { get { return Body == null ? Vector3.zero : Body.velocity; } }
        public float Radius { get { return 3f; } }
        // Requires `launched` so a still-mounted, unfired missile (including an ally's) never
        // shows up as an independent radar target — see docs/adr/0004-hostile-filtering-moves-to-consumers.md.
        public bool IsAlive { get { return launched && BlockBehaviour != null && BlockBehaviour.isSimulating && health > 0f; } }
        public float ThreatMass { get { return ThreatMassSlider.Value; } }
```

- [ ] **Step 4: Compute `GuidHash` in `OnSimulateStart`**

Find:

```csharp
        public override void OnSimulateStart()
        {
            base.OnSimulateStart();
            health = HealthSlider.Value;
            launched = AutoLaunch.IsActive;
            SpaceCombatRegistry.RegisterTrackable(this);
            if (Body != null)
            {
                Body.mass = ThreatMassSlider.Value;
                Body.drag = 0.02f;
            }
        }
```

replace with:

```csharp
        public override void OnSimulateStart()
        {
            base.OnSimulateStart();
            GuidHash = BlockBehaviour.BuildingBlock.Guid.GetHashCode();
            health = HealthSlider.Value;
            launched = AutoLaunch.IsActive;
            SpaceCombatRegistry.RegisterTrackable(this);
            if (Body != null)
            {
                Body.mass = ThreatMassSlider.Value;
                Body.drag = 0.02f;
            }
        }
```

- [ ] **Step 5: Grep-verify**

Run: `grep -n "ILockable\|GuidHash" src/BeyondSpiderAssembly/SpaceShipCore.cs src/BeyondSpiderAssembly/HeavyNuclearMissileBlock.cs`
Expected: each file shows one `: SpaceBlock, ILockable` (or similar) class line, one `GuidHash` property declaration, and one `GuidHash = BlockBehaviour.BuildingBlock.Guid.GetHashCode();` assignment.

Run: `grep -n "bool IsAlive" src/BeyondSpiderAssembly/HeavyNuclearMissileBlock.cs`
Expected: one match, containing `launched &&`.

- [ ] **Step 6: Manual trace of `IsAlive` (stand-in for a unit test — no test runner available)**

A missile block that just finished `OnSimulateStart` (`launched = AutoLaunch.IsActive = false` by default), `health = 650` (default), `BlockBehaviour.isSimulating = true`: `IsAlive` = `false && ... ` = `false` — correctly hidden while still mounted. After `Launch` is pressed and `launched` flips `true`: `IsAlive` = `true && true && true && (650 > 0)` = `true` — now visible/trackable, matching "only launched missiles show on radar."

- [ ] **Step 7: Commit**

```bash
git add src/BeyondSpiderAssembly/SpaceShipCore.cs src/BeyondSpiderAssembly/HeavyNuclearMissileBlock.cs
git commit -m "feat: make ship core and heavy missile ILockable, hide missiles until launched"
```

---

### Task 3: Move hostility filtering from `RadarPanelBlock` to `DefenseDirectorBlock`

**Files:**
- Modify: `src/BeyondSpiderAssembly/RadarPanelBlock.cs`
- Modify: `src/BeyondSpiderAssembly/DefenseDirectorBlock.cs`

**Interfaces:**
- Consumes: `SpaceBallistics.IsHostile(SpaceBlock, ITrackable)` (existing).
- Produces: `ship.Tracks` now contains both factions in range/cone (consumed by Task 5, Task 9, and unchanged consumers); `DefenseDirectorBlock` continues writing only-hostile solutions into `ship.DefensiveSolution` (consumed unchanged by `CiwsBlock`/`SpaceGunnerBlock`, Task 6).

- [ ] **Step 1: Drop the IFF skip in `RadarPanelBlock`**

In `src/BeyondSpiderAssembly/RadarPanelBlock.cs`, find:

```csharp
                Vector3 delta = target.Position - transform.position;
                float distance = delta.magnitude;
                if (distance > Range.Value)
                {
                    continue;
                }
                if (Vector3.Angle(transform.forward, delta) > ConeAngle.Value * 0.5f)
                {
                    continue;
                }
```

Wait — first find the whole loop body that still contains the IFF check, a few lines above this. Find:

```csharp
                ITrackable target = targets[i];
                if (target == null || !target.IsAlive || target.PlayerID == PlayerID)
                {
                    continue;
                }
                if (Iff.IsActive && target.Team == Team)
                {
                    continue;
                }

                Vector3 delta = target.Position - transform.position;
```

replace with:

```csharp
                ITrackable target = targets[i];
                if (target == null || !target.IsAlive || target.PlayerID == PlayerID)
                {
                    continue;
                }

                Vector3 delta = target.Position - transform.position;
```

Note: the `Iff` toggle/slider itself (`public MToggle Iff;` field and its `AddToggle("IFF", "BSIFF", true)` line in `SafeAwake`) stays untouched for build/save compatibility — it's simply no longer read anywhere. Do not remove the field or its `AddToggle` call.

- [ ] **Step 2: Add the hostility filter to `DefenseDirectorBlock`'s scoring loop**

In `src/BeyondSpiderAssembly/DefenseDirectorBlock.cs`, find:

```csharp
            FireSolution best = new FireSolution();
            float bestScore = -1f;
            for (int i = 0; i < ship.Tracks.Count; i++)
            {
                SensorTrack track = ship.Tracks[i];
                if (track.Distance > DefendedRadius.Value)
                {
                    continue;
                }
```

replace with:

```csharp
            FireSolution best = new FireSolution();
            float bestScore = -1f;
            for (int i = 0; i < ship.Tracks.Count; i++)
            {
                SensorTrack track = ship.Tracks[i];
                if (!SpaceBallistics.IsHostile(this, track.Target))
                {
                    continue;
                }
                if (track.Distance > DefendedRadius.Value)
                {
                    continue;
                }
```

- [ ] **Step 3: Grep-verify**

Run: `grep -n "Iff.IsActive" src/BeyondSpiderAssembly/RadarPanelBlock.cs`
Expected: no matches (the field/slider declaration lines don't contain this exact string; only the removed filtering line did).

Run: `grep -n "public MToggle Iff\|AddToggle(\"IFF\"" src/BeyondSpiderAssembly/RadarPanelBlock.cs`
Expected: one match each — confirms the toggle itself is untouched.

Run: `grep -n "IsHostile" src/BeyondSpiderAssembly/DefenseDirectorBlock.cs`
Expected: one match, the new filter line.

- [ ] **Step 4: Manual trace (stand-in for a unit test — no test runner available)**

Two players on the same team, player A's `RadarPanelBlock` (Team=Blue, PlayerID=1) scanning player B's ship (Team=Blue, PlayerID=2, in range/cone): before this change, `Iff.IsActive && target.Team == Team` (`true && Blue==Blue`) → skipped, never enters `ship.Tracks`. After this change: no team check at all, only the pre-existing `target.PlayerID == PlayerID` (`2 == 1` → false, doesn't skip) — so it's added to `ship.Tracks`. Then in `DefenseDirectorBlock`: `SpaceBallistics.IsHostile(this, track.Target)` evaluates `target.PlayerID != block.PlayerID && target.Team != block.Team` → `2 != 1 (true) && Blue != Blue (false)` → `false` → filtered out of `ship.DefensiveSolution` scoring, exactly matching today's "don't auto-intercept friendlies" behavior, just computed at the consumer instead of the sensor.

- [ ] **Step 5: Commit**

```bash
git add src/BeyondSpiderAssembly/RadarPanelBlock.cs src/BeyondSpiderAssembly/DefenseDirectorBlock.cs
git commit -m "feat: report both factions from radar, filter hostiles in DefenseDirector"
```

---

### Task 4: Heavy missile live guidance to the ship-wide lock

**Files:**
- Modify: `src/BeyondSpiderAssembly/HeavyNuclearMissileBlock.cs`

**Interfaces:**
- Consumes: `ShipState.LockedTarget` (Task 1), `ship.Tracks` (existing, now unfiltered per Task 3), `SpaceBlock.OwnShip()` (existing).
- Produces: nothing new consumed by later tasks — this is the terminal missile-guidance behavior.

- [ ] **Step 1: Add the `hadGuidance` field**

Find:

```csharp
        private bool launched;
        private float health;

        public int GuidHash { get; private set; }
```

replace with:

```csharp
        private bool launched;
        private float health;
        private bool hadGuidance;

        public int GuidHash { get; private set; }
```

- [ ] **Step 2: Rewrite the post-launch thrust in `SimulateFixedUpdateHost`**

Find:

```csharp
        public override void SimulateFixedUpdateHost()
        {
            if (!launched && Launch.IsHeld)
            {
                launched = true;
            }
            if (launched && Body != null && health > 0f)
            {
                Body.AddForce(transform.forward * Thrust.Value, ForceMode.Force);
            }
        }
```

replace with:

```csharp
        public override void SimulateFixedUpdateHost()
        {
            if (!launched && Launch.IsHeld)
            {
                launched = true;
            }
            if (!launched || Body == null || health <= 0f)
            {
                return;
            }

            ShipState ship = OwnShip();
            ITrackable target = ship == null ? null : ship.LockedTarget;
            bool trackedThisTick = false;
            if (target != null && target.IsAlive && ship != null)
            {
                for (int i = 0; i < ship.Tracks.Count; i++)
                {
                    if (ReferenceEquals(ship.Tracks[i].Target, target))
                    {
                        trackedThisTick = true;
                        break;
                    }
                }
            }

            if (trackedThisTick)
            {
                hadGuidance = true;
                Vector3 intercept = target.Position + target.Velocity * 0.35f;
                Vector3 desired = (intercept - transform.position).normalized;
                Body.AddForce(desired * Thrust.Value, ForceMode.Force);
                if (Body.velocity.sqrMagnitude > 1f)
                {
                    transform.rotation = Quaternion.LookRotation(Body.velocity.normalized, Vector3.up);
                }
            }
            else if (hadGuidance)
            {
                float speed = Body.velocity.magnitude;
                if (speed > 0.05f)
                {
                    float maxDecelForce = speed * Body.mass / Time.fixedDeltaTime;
                    Body.AddForce(-Body.velocity.normalized * Mathf.Min(Thrust.Value, maxDecelForce), ForceMode.Force);
                }
                else
                {
                    Body.velocity = Vector3.zero;
                }
            }
            else
            {
                Body.AddForce(transform.forward * Thrust.Value, ForceMode.Force);
            }
        }
```

- [ ] **Step 3: Grep-verify**

Run: `grep -n "hadGuidance\|ship.LockedTarget\|trackedThisTick" src/BeyondSpiderAssembly/HeavyNuclearMissileBlock.cs`
Expected: `hadGuidance` appears 3 times (field + set + read), `ship.LockedTarget` once, `trackedThisTick` 3 times (declare + set + check).

- [ ] **Step 4: Manual trace of the three guidance branches (stand-in for a unit test — no test runner available)**

- **Homing**: `ship.LockedTarget` is an alive enemy `SpaceShipCore` that's in `ship.Tracks` this tick → `trackedThisTick = true` → `hadGuidance` latches `true`, thrust vectors toward `target.Position + target.Velocity * 0.35`, missile visually turns to face its own velocity. Matches `SpaceInterceptorMissile`'s existing lead/rotation math exactly (same `0.35` constant, same `Quaternion.LookRotation(velocity.normalized, Vector3.up)` pattern).
- **Braking**: same missile, next tick the target leaves `ship.Tracks` (radar lost it, lock itself untouched) → `trackedThisTick = false`, but `hadGuidance` is already `true` from the previous tick → braking branch: with `speed = 400`, `Body.mass = 8` (default `HeavyNuclearMissileBlock` mass from XML), `Time.fixedDeltaTime = 0.02` → `maxDecelForce = 400 * 8 / 0.02 = 160000`, far above `Thrust.Value` (default 450) → applies `-velocity.normalized * 450`, i.e. full thrust braking, never overshooting past zero in one tick since `Thrust.Value` is always far below `maxDecelForce` at any realistic speed/mass. Once `speed <= 0.05`, `Body.velocity` is hard-set to `Vector3.zero` (clean stop, no residual drift).
- **Legacy straight flight**: a missile launched with `ship.LockedTarget == null` and never previously guided (`hadGuidance` still `false` from field default) → `trackedThisTick = false`, `hadGuidance` false → final `else` branch → `Body.AddForce(transform.forward * Thrust.Value, ForceMode.Force)`, byte-for-byte the original pre-this-plan behavior.

- [ ] **Step 5: Commit**

```bash
git add src/BeyondSpiderAssembly/HeavyNuclearMissileBlock.cs
git commit -m "feat: heavy missile homes on ship lock, brakes on lost track, else flies straight"
```

---

### Task 5: `SpaceCaptainBlock` computes `ship.LockedSolution`

**Files:**
- Modify: `src/BeyondSpiderAssembly/SpaceCaptainBlock.cs`

**Interfaces:**
- Consumes: `ShipState.LockedTarget`/`LockedSolution` (Task 1), `ship.Tracks` (existing).
- Produces: `ship.LockedSolution` kept fresh every tick — consumed by Task 6 (`CiwsBlock`/`SpaceGunnerBlock`).

- [ ] **Step 1: Add the `using` directive needed for `IList<ITrackable>` later in this file (Task 7)**

In `src/BeyondSpiderAssembly/SpaceCaptainBlock.cs`, find:

```csharp
using UnityEngine;

namespace BeyondSpiderAssembly
{
    public class SpaceCaptainBlock : SpaceBlock
```

replace with:

```csharp
using System.Collections.Generic;
using Modding;
using UnityEngine;

namespace BeyondSpiderAssembly
{
    public class SpaceCaptainBlock : SpaceBlock
```

- [ ] **Step 2: Add `SimulateFixedUpdateHost` to compute `ship.LockedSolution`**

Find:

```csharp
        public override void SimulateUpdateHost()
        {
            ShipState ship = OwnShip();
            if (ship == null)
            {
                return;
            }

            if (SwitchPriority.IsPressed)
            {
                ship.Priority = ship.Priority == CommandPriority.AntiAir
                    ? CommandPriority.AntiShip
                    : CommandPriority.AntiAir;
            }
        }
    }
}
```

replace with:

```csharp
        public override void SimulateUpdateHost()
        {
            ShipState ship = OwnShip();
            if (ship == null)
            {
                return;
            }

            if (SwitchPriority.IsPressed)
            {
                ship.Priority = ship.Priority == CommandPriority.AntiAir
                    ? CommandPriority.AntiShip
                    : CommandPriority.AntiAir;
            }
        }

        public override void SimulateFixedUpdateHost()
        {
            ShipState ship = OwnShip();
            if (ship == null)
            {
                return;
            }

            ship.LockedSolution = default(FireSolution);
            if (ship.LockedTarget == null || !ship.LockedTarget.IsAlive)
            {
                return;
            }

            for (int i = 0; i < ship.Tracks.Count; i++)
            {
                SensorTrack track = ship.Tracks[i];
                if (!ReferenceEquals(track.Target, ship.LockedTarget))
                {
                    continue;
                }
                ship.LockedSolution.Target = track.Target;
                ship.LockedSolution.AimPoint = track.Position + track.Velocity * Mathf.Clamp(track.TimeToImpact, 0f, 2f);
                ship.LockedSolution.TimeToImpact = track.TimeToImpact;
                break;
            }
        }
    }
}
```

- [ ] **Step 3: Grep-verify**

Run: `grep -n "SimulateFixedUpdateHost\|LockedSolution" src/BeyondSpiderAssembly/SpaceCaptainBlock.cs`
Expected: one `SimulateFixedUpdateHost` method, and `LockedSolution` referenced 4 times (reset, `.Target` set, `.AimPoint` set, `.TimeToImpact` set).

- [ ] **Step 4: Manual trace (stand-in for a unit test — no test runner available)**

`ship.LockedTarget` set to an enemy `SpaceShipCore`, present in `ship.Tracks[2]` this tick with `Position=(100,0,0)`, `Velocity=(0,0,10)`, `TimeToImpact=1.5`: loop finds `i=2` via `ReferenceEquals`, sets `ship.LockedSolution.AimPoint = (100,0,0) + (0,0,10)*Clamp(1.5,0,2) = (100,0,15)`, `TimeToImpact=1.5`, `Target` = the same `SpaceShipCore` reference. Next tick the target is no longer in `ship.Tracks` (radar lost it) → loop never matches → `ship.LockedSolution` stays at the `default(FireSolution)` reset at the top (`Target == null`), which is exactly the signal Task 6 needs to fall back to `ship.DefensiveSolution`.

- [ ] **Step 5: Commit**

```bash
git add src/BeyondSpiderAssembly/SpaceCaptainBlock.cs
git commit -m "feat: captain resolves ship-wide lock into a fire solution each tick"
```

---

### Task 6: `CiwsBlock`/`SpaceGunnerBlock` prefer the manual lock per `ship.Priority`

**Files:**
- Modify: `src/BeyondSpiderAssembly/CiwsBlock.cs`
- Modify: `src/BeyondSpiderAssembly/RailgunBarrelBlock.cs` (the `SpaceGunnerBlock` class in this file)

**Interfaces:**
- Consumes: `ship.LockedSolution` (Task 5), `ship.Priority`/`CommandPriority` (existing, previously unused).
- Produces: nothing new consumed by later tasks.

- [ ] **Step 1: `CiwsBlock` picks a solution before reading `Target`**

In `src/BeyondSpiderAssembly/CiwsBlock.cs`, find:

```csharp
        public override void SimulateFixedUpdateHost()
        {
            if (!DefaultActive.IsActive)
            {
                return;
            }

            ShipState ship = OwnShip();
            if (ship == null || ship.DefensiveSolution.Target == null)
            {
                return;
            }

            HeavyNuclearMissileBlock missile = ship.DefensiveSolution.Target as HeavyNuclearMissileBlock;
            if (missile == null || !missile.IsAlive || missile.Team == Team)
            {
                return;
            }
```

replace with:

```csharp
        public override void SimulateFixedUpdateHost()
        {
            if (!DefaultActive.IsActive)
            {
                return;
            }

            ShipState ship = OwnShip();
            if (ship == null)
            {
                return;
            }

            FireSolution solution = (ship.Priority == CommandPriority.AntiShip && ship.LockedSolution.Target != null)
                ? ship.LockedSolution
                : ship.DefensiveSolution;
            if (solution.Target == null)
            {
                return;
            }

            HeavyNuclearMissileBlock missile = solution.Target as HeavyNuclearMissileBlock;
            if (missile == null || !missile.IsAlive || missile.Team == Team)
            {
                return;
            }
```

(The rest of the method — the distance/aim checks against `missile.Position` and the damage application — is unchanged; it already reads `missile`, not `ship.DefensiveSolution` directly, so no further edits are needed in this file.)

- [ ] **Step 2: `SpaceGunnerBlock` picks a solution before reading `Target`/`AimPoint`**

In `src/BeyondSpiderAssembly/RailgunBarrelBlock.cs`, find:

```csharp
        public override void SimulateFixedUpdateHost()
        {
            if (!active)
            {
                return;
            }

            ShipState ship = OwnShip();
            if (ship == null || ship.DefensiveSolution.Target == null)
            {
                return;
            }

            for (int i = 0; i < ship.Guns.Count; i++)
            {
                RailgunBarrelBlock gun = ship.Guns[i];
                if (gun == null || !gun.FireControl.IsActive || gun.GunGroup.Value != GunGroup.Value)
                {
                    continue;
                }

                Vector3 delta = ship.DefensiveSolution.AimPoint - gun.transform.position;
                if (Vector3.Angle(gun.transform.forward, delta) <= AimTolerance.Value)
                {
                    gun.TryFireControl(ship.DefensiveSolution);
                }
            }
        }
```

replace with:

```csharp
        public override void SimulateFixedUpdateHost()
        {
            if (!active)
            {
                return;
            }

            ShipState ship = OwnShip();
            if (ship == null)
            {
                return;
            }

            FireSolution solution = (ship.Priority == CommandPriority.AntiShip && ship.LockedSolution.Target != null)
                ? ship.LockedSolution
                : ship.DefensiveSolution;
            if (solution.Target == null)
            {
                return;
            }

            for (int i = 0; i < ship.Guns.Count; i++)
            {
                RailgunBarrelBlock gun = ship.Guns[i];
                if (gun == null || !gun.FireControl.IsActive || gun.GunGroup.Value != GunGroup.Value)
                {
                    continue;
                }

                Vector3 delta = solution.AimPoint - gun.transform.position;
                if (Vector3.Angle(gun.transform.forward, delta) <= AimTolerance.Value)
                {
                    gun.TryFireControl(solution);
                }
            }
        }
```

- [ ] **Step 3: Grep-verify**

Run: `grep -n "ship.LockedSolution\|CommandPriority.AntiShip" src/BeyondSpiderAssembly/CiwsBlock.cs src/BeyondSpiderAssembly/RailgunBarrelBlock.cs`
Expected: both files show one `FireSolution solution = (ship.Priority == CommandPriority.AntiShip ...` line.

- [ ] **Step 4: Manual trace (stand-in for a unit test — no test runner available)**

`ship.Priority = CommandPriority.AntiShip`, `ship.LockedSolution.Target` is an enemy `SpaceShipCore` (alive), `ship.DefensiveSolution.Target` is a separate incoming `HeavyNuclearMissileBlock`: `solution` picks `ship.LockedSolution` (the enemy ship). In `CiwsBlock`, `solution.Target as HeavyNuclearMissileBlock` → `null` (it's a `SpaceShipCore`) → `CiwsBlock` no-ops this tick, exactly as intended (CIWS never engages ships). In `SpaceGunnerBlock`, no such type restriction — its grouped `RailgunBarrelBlock`s aim at `solution.AimPoint` (the locked ship's lead point) and fire, i.e. manual anti-ship gunnery works. Flip `ship.Priority` to `AntiAir` with the same two solutions live: `solution` picks `ship.DefensiveSolution` (the missile) instead — both `CiwsBlock` and `SpaceGunnerBlock` engage the incoming missile as they did before this plan, confirming the arbitration doesn't regress today's auto-defense when the ship is in `AntiAir` mode.

- [ ] **Step 5: Commit**

```bash
git add src/BeyondSpiderAssembly/CiwsBlock.cs src/BeyondSpiderAssembly/RailgunBarrelBlock.cs
git commit -m "feat: CIWS and gunner prefer the manual lock in anti-ship priority"
```

---

### Task 7: `CaptainLockNet` multiplayer sync

**Files:**
- Modify: `src/BeyondSpiderAssembly/SpaceCaptainBlock.cs`
- Modify: `src/BeyondSpiderAssembly/Mod.cs`

**Interfaces:**
- Consumes: `ILockable` (Task 1), `SpaceCombatRegistry.FindShip`/`.Trackables` (existing).
- Produces: `CaptainLockNet.Instance` (singleton), `CaptainLockNet.LockMsg` (`MessageType`), `CaptainLockNet.LockReceiver(Message)` — consumed by Task 10 (radar click handler sends this message) and by `Mod.cs`'s callback registration in this same task.

- [ ] **Step 1: Add the `CaptainLockNet` class**

At the end of `src/BeyondSpiderAssembly/SpaceCaptainBlock.cs`, find the final lines:

```csharp
            for (int i = 0; i < ship.Tracks.Count; i++)
            {
                SensorTrack track = ship.Tracks[i];
                if (!ReferenceEquals(track.Target, ship.LockedTarget))
                {
                    continue;
                }
                ship.LockedSolution.Target = track.Target;
                ship.LockedSolution.AimPoint = track.Position + track.Velocity * Mathf.Clamp(track.TimeToImpact, 0f, 2f);
                ship.LockedSolution.TimeToImpact = track.TimeToImpact;
                break;
            }
        }
    }
}
```

replace with:

```csharp
            for (int i = 0; i < ship.Tracks.Count; i++)
            {
                SensorTrack track = ship.Tracks[i];
                if (!ReferenceEquals(track.Target, ship.LockedTarget))
                {
                    continue;
                }
                ship.LockedSolution.Target = track.Target;
                ship.LockedSolution.AimPoint = track.Position + track.Velocity * Mathf.Clamp(track.TimeToImpact, 0f, 2f);
                ship.LockedSolution.TimeToImpact = track.TimeToImpact;
                break;
            }
        }
    }

    public class CaptainLockNet : SingleInstance<CaptainLockNet>
    {
        public override string Name { get { return "BeyondSpider Captain Lock Net"; } }

        // (shipPlayerId, targetPlayerId, targetGuidHash, hasLock)
        public static MessageType LockMsg = ModNetworking.CreateMessageType(
            DataType.Integer, DataType.Integer, DataType.Integer, DataType.Boolean);

        public void LockReceiver(Message msg)
        {
            int shipPlayerId = (int)msg.GetData(0);
            int targetPlayerId = (int)msg.GetData(1);
            int targetGuidHash = (int)msg.GetData(2);
            bool hasLock = (bool)msg.GetData(3);

            ShipState ship = SpaceCombatRegistry.FindShip(shipPlayerId);
            if (ship == null)
            {
                return;
            }
            if (!hasLock)
            {
                ship.LockedTarget = null;
                return;
            }

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
}
```

- [ ] **Step 2: Register `CaptainLockNet` in `Mod.cs`**

In `src/BeyondSpiderAssembly/Mod.cs`, find:

```csharp
		public override void OnLoad()
		{
			Root = new GameObject("BeyondSpider Space Combat");
			UnityEngine.Object.DontDestroyOnLoad(Root);
			Root.AddComponent<SpaceCombatRuntime>();
			Root.AddComponent<SpaceFlakTurretNet>();
			Root.AddComponent<NanoArmorController>();
			ModNetworking.Callbacks[SpaceFlakTurretNet.ActiveMsg] += SpaceFlakTurretNet.Instance.ActiveReceiver;
			ModNetworking.Callbacks[SpaceFlakTurretNet.StateMsg] += SpaceFlakTurretNet.Instance.StateReceiver;
			ModNetworking.Callbacks[SpaceFlakTurretNet.ShotMsg] += SpaceFlakTurretNet.Instance.ShotReceiver;
			Debug.Log("BeyondSpider Space Combat loaded.");
		}
```

replace with:

```csharp
		public override void OnLoad()
		{
			Root = new GameObject("BeyondSpider Space Combat");
			UnityEngine.Object.DontDestroyOnLoad(Root);
			Root.AddComponent<SpaceCombatRuntime>();
			Root.AddComponent<SpaceFlakTurretNet>();
			Root.AddComponent<NanoArmorController>();
			Root.AddComponent<CaptainLockNet>();
			ModNetworking.Callbacks[SpaceFlakTurretNet.ActiveMsg] += SpaceFlakTurretNet.Instance.ActiveReceiver;
			ModNetworking.Callbacks[SpaceFlakTurretNet.StateMsg] += SpaceFlakTurretNet.Instance.StateReceiver;
			ModNetworking.Callbacks[SpaceFlakTurretNet.ShotMsg] += SpaceFlakTurretNet.Instance.ShotReceiver;
			ModNetworking.Callbacks[CaptainLockNet.LockMsg] += CaptainLockNet.Instance.LockReceiver;
			Debug.Log("BeyondSpider Space Combat loaded.");
		}
```

- [ ] **Step 3: Grep-verify**

Run: `grep -n "class CaptainLockNet\|LockMsg\|LockReceiver" src/BeyondSpiderAssembly/SpaceCaptainBlock.cs`
Expected: one class declaration, one `LockMsg` field, one `LockReceiver` method.

Run: `grep -n "CaptainLockNet" src/BeyondSpiderAssembly/Mod.cs`
Expected: two matches (`AddComponent<CaptainLockNet>()` and the `Callbacks[CaptainLockNet.LockMsg] += ...` line).

- [ ] **Step 4: Manual trace (stand-in for a unit test — no test runner available)**

Client sends `CaptainLockNet.LockMsg.CreateMessage(1, 2, 12345, true)` (ship owned by player 1 locks player 2's target with `GuidHash=12345`). On receipt: `SpaceCombatRegistry.FindShip(1)` resolves the ship; loop over `Trackables` finds the one `ILockable` with `PlayerID==2 && GuidHash==12345` (an enemy `SpaceShipCore` or `HeavyNuclearMissileBlock`) and sets `ship.LockedTarget` to it. A later `CreateMessage(1, 0, 0, false)` (clear) sets `ship.LockedTarget = null` immediately, without needing to resolve any candidate.

- [ ] **Step 5: Commit**

```bash
git add src/BeyondSpiderAssembly/SpaceCaptainBlock.cs src/BeyondSpiderAssembly/Mod.cs
git commit -m "feat: sync captain target lock across clients via CaptainLockNet"
```

---

### Task 8: `CaptainRadarView` — pocket scene, camera, grid, marker prefabs

**Files:**
- Create: `src/BeyondSpiderAssembly/CaptainRadarView.cs`
- Modify: `src/BeyondSpiderAssembly/BeyondSpiderAssembly.csproj` (register the new file for compilation)

**Interfaces:**
- Consumes: `ModResource.GetTexture("BS Migrated AA Lock Texture")` (existing resource, unused until now).
- Produces: `CaptainRadarView` (a `SingleInstance<CaptainRadarView>`) with fields `pocketRoot`, `gimbal`, `radarCamera`, `radarTexture`, `lockIcon`, `arrowMesh`, `arrowPool`, `spherePool`, `markerToTrackable`, `activeMarkers`, and a `CreateMarker(TrackKind)` helper — all consumed by Task 9 (marker sync) and Task 10 (input/OnGUI).

- [ ] **Step 1: Create the file with scene construction**

Create `src/BeyondSpiderAssembly/CaptainRadarView.cs`:

```csharp
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

        private const float PocketOriginHeight = 500000f;
        private const float DisplayRadius = 4f;
        private const int RingCount = 8;
        private const int SegmentCount = 32;
        private const float MarkerColliderRadius = 0.3f;
        private const float MarkerScale = 0.3f;

        public bool IsOpen;
        public float MetersPerUnit = 50f;

        private Transform pocketRoot;
        private Transform gimbal;
        private Camera radarCamera;
        private RenderTexture radarTexture;
        private GameObject lockIcon;
        private Mesh arrowMesh;

        private readonly List<GameObject> arrowPool = new List<GameObject>();
        private readonly List<GameObject> spherePool = new List<GameObject>();
        private readonly Dictionary<GameObject, ITrackable> markerToTrackable = new Dictionary<GameObject, ITrackable>();
        private readonly Dictionary<ITrackable, GameObject> activeMarkers = new Dictionary<ITrackable, GameObject>();

        public Camera RadarCamera { get { return radarCamera; } }
        public RenderTexture RadarTexture { get { return radarTexture; } }
        public Transform Gimbal { get { return gimbal; } }

        private void Awake()
        {
            arrowMesh = BuildArrowMesh();
            BuildScene();
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
            radarCamera.backgroundColor = new Color(0.02f, 0.05f, 0.03f);
            radarCamera.nearClipPlane = 0.1f;
            radarCamera.farClipPlane = 100f;
            radarTexture = new RenderTexture(512, 512, 16);
            radarCamera.targetTexture = radarTexture;
            radarCamera.enabled = false;

            BuildGrid();
            BuildLockIcon();
        }

        private void BuildGrid()
        {
            GameObject gridObject = new GameObject("BS Radar Grid");
            gridObject.transform.SetParent(pocketRoot, false);
            gridObject.transform.localPosition = Vector3.zero;
            gridObject.transform.localRotation = Quaternion.identity;

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
            renderer.material.SetColor("_TintColor", new Color(0.2f, 0.9f, 0.3f, 0.5f));
        }

        private void BuildLockIcon()
        {
            lockIcon = GameObject.CreatePrimitive(PrimitiveType.Quad);
            lockIcon.name = "BS Radar Lock Icon";
            Object.Destroy(lockIcon.GetComponent<Collider>());
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

        private GameObject CreateMarker(TrackKind kind)
        {
            GameObject marker;
            if (kind == TrackKind.Ship)
            {
                marker = new GameObject("BS Radar Ship Marker");
                MeshFilter filter = marker.AddComponent<MeshFilter>();
                filter.sharedMesh = arrowMesh;
                MeshRenderer renderer = marker.AddComponent<MeshRenderer>();
                renderer.material = new Material(Shader.Find("Particles/Additive"));
            }
            else
            {
                marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                marker.name = "BS Radar Missile Marker";
                Object.Destroy(marker.GetComponent<Collider>());
            }

            marker.transform.SetParent(pocketRoot, false);
            marker.transform.localScale = Vector3.one * MarkerScale;
            SphereCollider collider = marker.AddComponent<SphereCollider>();
            collider.radius = MarkerColliderRadius;
            marker.SetActive(false);
            return marker;
        }
    }
}
```

- [ ] **Step 2: Register the new file in the csproj**

In `src/BeyondSpiderAssembly/BeyondSpiderAssembly.csproj`, find:

```
    <Compile Include="SpaceInterceptorLauncherBlock.cs" />
    <Compile Include="Properties\AssemblyInfo.cs" />
```

replace with:

```
    <Compile Include="SpaceInterceptorLauncherBlock.cs" />
    <Compile Include="CaptainRadarView.cs" />
    <Compile Include="Properties\AssemblyInfo.cs" />
```

- [ ] **Step 3: Grep-verify**

Run: `grep -n "class CaptainRadarView\|CreateMarker\|BuildArrowMesh\|BuildGrid\|BuildLockIcon" src/BeyondSpiderAssembly/CaptainRadarView.cs`
Expected: one match each for the class and every method named.

Run: `grep -n "CaptainRadarView.cs" src/BeyondSpiderAssembly/BeyondSpiderAssembly.csproj`
Expected: exactly one match (the new `<Compile Include>` line).

- [ ] **Step 4: Manual trace of `BuildArrowMesh` (stand-in for a unit test — no test runner available)**

Vertices: nose at local `(0, 0, 0.5)` (points along +Z), two rear corners at `(±0.25, 0, -0.25)`, a rear notch at `(0, 0, -0.1)` — a flat kite/dart lying in the local XZ plane (Y=0 throughout), consistent with §4's "small flat dart/kite shape." Triangles `(0,1,2)` and `(0,2,3)` each reference three of the four vertices with no out-of-range indices (max index used is 3, `vertices.Length == 4`). Since the mesh is built with `+Z` as "nose forward," `Quaternion.LookRotation(velLocal.normalized, Vector3.up)` in Task 9 correctly points the nose along the target's local-space velocity direction.

- [ ] **Step 5: Commit**

```bash
git add src/BeyondSpiderAssembly/CaptainRadarView.cs src/BeyondSpiderAssembly/BeyondSpiderAssembly.csproj
git commit -m "feat: add CaptainRadarView pocket scene, camera, grid, and marker prefabs"
```

---

### Task 9: Per-frame marker sync from `ship.Tracks`

**Files:**
- Modify: `src/BeyondSpiderAssembly/CaptainRadarView.cs`

**Interfaces:**
- Consumes: `ship.Tracks`/`ship.Captain`/`ship.LockedTarget` (existing/Task 1), `SpaceBallistics.IsHostile` (existing), `CaptainRadarView`'s pool/marker fields (Task 8).
- Produces: `CaptainRadarView.SyncMarkers()` (called from `Update()`, wired in Task 10), `OwnShipState()` (private static helper reused by Task 10).

- [ ] **Step 1: Add `OwnShipState`, `GetOrCreateMarker`, `HideAllMarkers`, and `SyncMarkers`**

In `src/BeyondSpiderAssembly/CaptainRadarView.cs`, find the closing of `CreateMarker` and the class:

```csharp
            marker.transform.SetParent(pocketRoot, false);
            marker.transform.localScale = Vector3.one * MarkerScale;
            SphereCollider collider = marker.AddComponent<SphereCollider>();
            collider.radius = MarkerColliderRadius;
            marker.SetActive(false);
            return marker;
        }
    }
}
```

replace with:

```csharp
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

        public GameObject MarkerFor(ITrackable target)
        {
            GameObject marker;
            activeMarkers.TryGetValue(target, out marker);
            return marker;
        }

        public ITrackable TrackableFor(GameObject marker)
        {
            ITrackable target;
            markerToTrackable.TryGetValue(marker, out target);
            return target;
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
            List<ITrackable> seen = new List<ITrackable>();

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

                Vector3 relative = captain.InverseTransformVector(track.Position - captain.position);
                Vector3 displayPos = relative / MetersPerUnit;
                if (displayPos.magnitude > DisplayRadius)
                {
                    displayPos = displayPos.normalized * DisplayRadius;
                }
                marker.transform.localPosition = displayPos;

                if (track.Kind == TrackKind.Ship)
                {
                    Vector3 velLocal = captain.InverseTransformDirection(track.Velocity);
                    marker.transform.localRotation = velLocal.sqrMagnitude > 0.01f
                        ? Quaternion.LookRotation(velLocal.normalized, Vector3.up)
                        : Quaternion.identity;
                }

                Color color = SpaceBallistics.IsHostile(ship.Captain, track.Target) ? Color.red : Color.green;
                Renderer renderer = marker.GetComponent<Renderer>();
                if (track.Kind == TrackKind.Ship)
                {
                    renderer.material.SetColor("_TintColor", color);
                }
                else
                {
                    renderer.material.color = color;
                }
            }

            List<ITrackable> stale = new List<ITrackable>();
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
            }
            else
            {
                lockIcon.SetActive(false);
            }
        }
    }
}
```

- [ ] **Step 2: Grep-verify**

Run: `grep -n "SyncMarkers\|OwnShipState\|GetOrCreateMarker\|HideAllMarkers" src/BeyondSpiderAssembly/CaptainRadarView.cs`
Expected: one method definition each, plus `SyncMarkers`/`OwnShipState`/`GetOrCreateMarker` each called at least once from within `SyncMarkers` or its helpers.

- [ ] **Step 3: Manual trace (stand-in for a unit test — no test runner available)**

`MetersPerUnit = 50`, `DisplayRadius = 4`, captain at world origin facing world `+Z`, a hostile `SpaceShipCore` track at world `(0, 0, 300)` with `Velocity = (0,0,-20)` (closing): `relative = captain.InverseTransformVector((0,0,300) - (0,0,0)) = (0,0,300)` (captain's own local frame, unrotated in this example) → `displayPos = (0,0,6)` → magnitude `6 > DisplayRadius (4)` → clamped to `(0,0,4)` (exactly at the dish's rim, matching "clamp to the visible dish instead of leaving frame"). `velLocal = (0,0,-20)`, normalized `(0,0,-1)` → `Quaternion.LookRotation((0,0,-1), Vector3.up)` turns the arrow's nose to point back toward the captain (closing target). `SpaceBallistics.IsHostile(ship.Captain, track.Target)` → `true` (different player, different team) → `renderer.material.SetColor("_TintColor", Color.red)`. On a later tick where this same `SpaceShipCore` drops out of `ship.Tracks` (destroyed or out of range), the cleanup loop finds it in `activeMarkers` but not in this tick's `seen` list → `SetActive(false)` and removes it from `activeMarkers` — its pooled `GameObject` becomes available for `GetOrCreateMarker` to hand out to the next new `Ship`-kind track.

- [ ] **Step 4: Commit**

```bash
git add src/BeyondSpiderAssembly/CaptainRadarView.cs
git commit -m "feat: sync radar markers, colors, and lock icon from ship.Tracks"
```

---

### Task 10: Orbit/zoom/click-to-lock input and the radar panel

**Files:**
- Modify: `src/BeyondSpiderAssembly/CaptainRadarView.cs`

**Interfaces:**
- Consumes: `CaptainRadarView`'s fields (Task 8/9), `CaptainLockNet.LockMsg` (Task 7), `ILockable` (Task 1).
- Produces: `CaptainRadarView.SetOpen(bool)` (public method) — consumed by Task 11's HUD button.

- [ ] **Step 1: Add panel/input constants and state fields**

In `src/BeyondSpiderAssembly/CaptainRadarView.cs`, find:

```csharp
        public bool IsOpen;
        public float MetersPerUnit = 50f;
```

replace with:

```csharp
        public bool IsOpen;
        public float MetersPerUnit = 50f;

        private const float PanelSize = 420f;
        private const float MinMetersPerUnit = 5f;
        private const float MaxMetersPerUnit = 2000f;
        private const float ZoomStep = 1.15f;
        private const float OrbitSpeed = 3f;
        private const float ClickPixelThreshold = 4f;

        private bool orbiting;
        private bool leftDownInRect;
        private Vector2 mouseDownPos;
        private float yaw;
        private float pitch = 25f;
```

- [ ] **Step 2: Add `SetOpen`, `GetPanelRect`, `FlippedMouse`, `Update`, and `OnGUI`**

Find the end of `SyncMarkers` and the class closing (from Task 9):

```csharp
            GameObject lockedMarker;
            if (ship.LockedTarget != null && activeMarkers.TryGetValue(ship.LockedTarget, out lockedMarker))
            {
                lockIcon.SetActive(true);
                lockIcon.transform.localPosition = lockedMarker.transform.localPosition;
            }
            else
            {
                lockIcon.SetActive(false);
            }
        }
    }
}
```

replace with:

```csharp
            GameObject lockedMarker;
            if (ship.LockedTarget != null && activeMarkers.TryGetValue(ship.LockedTarget, out lockedMarker))
            {
                lockIcon.SetActive(true);
                lockIcon.transform.localPosition = lockedMarker.transform.localPosition;
            }
            else
            {
                lockIcon.SetActive(false);
            }
        }

        public void SetOpen(bool open)
        {
            IsOpen = open;
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
                return;
            }

            Rect rect = GetPanelRect();
            HandleOrbitAndZoom(rect);
            HandleClick(rect);
            SyncMarkers();
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

            RaycastHit hit;
            if (!Physics.Raycast(ray, out hit, 100f))
            {
                return;
            }

            ITrackable target = TrackableFor(hit.collider.gameObject);
            if (target == null)
            {
                return;
            }

            bool willLock = !ReferenceEquals(ship.LockedTarget, target);
            ship.LockedTarget = willLock ? target : null;

            ILockable lockable = target as ILockable;
            int targetGuidHash = lockable != null ? lockable.GuidHash : 0;
            ModNetworking.SendToAll(CaptainLockNet.LockMsg.CreateMessage(ship.Captain.PlayerID, target.PlayerID, targetGuidHash, willLock));
        }

        private void OnGUI()
        {
            if (!IsOpen || StatMaster.hudHidden || radarTexture == null)
            {
                return;
            }

            Rect rect = GetPanelRect();
            GUI.DrawTexture(rect, radarTexture);
            float ringMeters = MetersPerUnit * DisplayRadius / RingCount;
            GUI.Label(new Rect(rect.x, rect.y - 20f, rect.width, 20f), "1 ring = " + ringMeters.ToString("0") + "m");
        }
    }
}
```

- [ ] **Step 3: Grep-verify**

Run: `grep -n "SetOpen\|HandleOrbitAndZoom\|HandleClick\|TryLockAtMouse\|private void OnGUI" src/BeyondSpiderAssembly/CaptainRadarView.cs`
Expected: one method definition each.

Run: `grep -n "private void Update" src/BeyondSpiderAssembly/CaptainRadarView.cs`
Expected: exactly one (no duplicate Unity message methods).

- [ ] **Step 4: Manual trace (stand-in for a unit test — no test runner available)**

**Orbit**: right-mouse-down inside `rect` → `orbiting = true`; while held, `Input.GetAxis("Mouse X") = 0.5` for one frame → `yaw += 0.5 * 3 = 1.5`; `gimbal.localRotation` updates to `Euler(pitch, 1.5, 0)`. Releasing right-mouse anywhere sets `orbiting = false` regardless of cursor position (drag continues to track once started, per the design), matching §5's drag UX.

**Zoom**: cursor over `rect`, `scroll = 1` (wheel up) → `MetersPerUnit = 50 / 1.15 ≈ 43.5` (zoom in, clamped no lower than 5); repeated scroll-down ticks multiply back up, clamped no higher than 2000.

**Click vs. drag**: left-mouse-down inside `rect` at `(100, 100)`, mouse-up at `(101, 101)` (distance `√2 ≈ 1.41 < ClickPixelThreshold (4)`) and still inside `rect` → registers as a click → `TryLockAtMouse` runs. The same down/up but ending at `(150, 100)` (distance `50 ≥ 4`) → not a click, no lock change (this was a drag/other mouse movement, not an intentional click).

**Lock resolution**: click ray hits a marker `GameObject` previously returned by `GetOrCreateMarker` for an enemy `HeavyNuclearMissileBlock` with `GuidHash = 777`, `PlayerID = 3`. `TrackableFor` resolves it back to that same `HeavyNuclearMissileBlock` instance. `ship.LockedTarget` was previously a different target, so `willLock = true`; the network message sent is `CreateMessage(ship.Captain.PlayerID, 3, 777, true)`. Clicking the *same* marker again: `willLock = false` (it already equals `ship.LockedTarget`), message sent is `CreateMessage(ship.Captain.PlayerID, 3, 777, false)` and `ship.LockedTarget` clears locally right away.

- [ ] **Step 5: Commit**

```bash
git add src/BeyondSpiderAssembly/CaptainRadarView.cs
git commit -m "feat: add radar orbit/zoom/click-to-lock input and the OnGUI panel"
```

---

### Task 11: "Open/Close Radar" button and `CaptainRadarView` registration

**Files:**
- Modify: `src/BeyondSpiderAssembly/SpaceCombatCore.cs` (`SpaceCombatRuntime.OnGUI`)
- Modify: `src/BeyondSpiderAssembly/Mod.cs`

**Interfaces:**
- Consumes: `CaptainRadarView.Instance`/`.IsOpen`/`.SetOpen(bool)` (Task 8/10).
- Produces: nothing new consumed by later tasks — this wires the feature's entry point.

- [ ] **Step 1: Register `CaptainRadarView` on `Mod.Root`**

In `src/BeyondSpiderAssembly/Mod.cs`, find:

```csharp
			Root.AddComponent<CaptainLockNet>();
```

replace with:

```csharp
			Root.AddComponent<CaptainLockNet>();
			Root.AddComponent<CaptainRadarView>();
```

- [ ] **Step 2: Add the button to the existing debug HUD window**

In `src/BeyondSpiderAssembly/SpaceCombatCore.cs`, find:

```csharp
            GUILayout.Label("Tracks: " + ship.Tracks.Count + "  CIWS: " + ship.Ciws.Count + "  Shields: " + ship.Shields.Count);
            GUILayout.Label("Armor blocks: " + ship.Armor.Count + "  " + FormatArmor(ship));
            ShowArmorHP = GUILayout.Toggle(ShowArmorHP, "Show Armor HP");
```

replace with:

```csharp
            GUILayout.Label("Tracks: " + ship.Tracks.Count + "  CIWS: " + ship.Ciws.Count + "  Shields: " + ship.Shields.Count);
            GUILayout.Label("Armor blocks: " + ship.Armor.Count + "  " + FormatArmor(ship));
            ShowArmorHP = GUILayout.Toggle(ShowArmorHP, "Show Armor HP");
            if (ship.Captain != null && CaptainRadarView.Instance != null)
            {
                if (GUILayout.Button(CaptainRadarView.Instance.IsOpen ? "Close Radar" : "Open Radar"))
                {
                    CaptainRadarView.Instance.SetOpen(!CaptainRadarView.Instance.IsOpen);
                }
            }
```

- [ ] **Step 3: Grep-verify**

Run: `grep -n "CaptainRadarView" src/BeyondSpiderAssembly/Mod.cs src/BeyondSpiderAssembly/SpaceCombatCore.cs`
Expected: `Mod.cs` shows one `AddComponent<CaptainRadarView>()`; `SpaceCombatCore.cs` shows three references (`ship.Captain != null && CaptainRadarView.Instance != null`, `CaptainRadarView.Instance.IsOpen` × 2, `CaptainRadarView.Instance.SetOpen(...)`).

- [ ] **Step 4: Manual trace (stand-in for a unit test — no test runner available)**

A ship with no `SpaceCaptainBlock` at all: `ship.Captain == null` → button never renders, matching "a ship with no Captain block has no way to open the radar UI" (`docs/adr/0003-...md`). A ship with a Captain block, button not yet pressed: label reads "Open Radar"; pressing it calls `SetOpen(true)` → `IsOpen = true`, `radarCamera.enabled = true` → next frame's `Update()`/`OnGUI()` in `CaptainRadarView` start running (Task 10's `if (!IsOpen) return;` guards no longer trigger). Pressing again: `SetOpen(false)` → camera disabled, panel stops drawing, and `Update()` short-circuits before touching input or markers.

- [ ] **Step 5: Commit**

```bash
git add src/BeyondSpiderAssembly/Mod.cs src/BeyondSpiderAssembly/SpaceCombatCore.cs
git commit -m "feat: add Open/Close Radar button to the captain's HUD"
```

---

### Task 12: Docs finalization and repo-wide consistency pass

**Files:**
- Modify: `docs/agent-besiege-mod-guide.md`

**Interfaces:** none (docs only).

- [ ] **Step 1: Mark roadmap item 10 advanced in `docs/agent-besiege-mod-guide.md`**

Find:

```
10. 强化重型导弹：发射状态、制导、生命值、爆炸效果、网络显示。
```

replace with:

```
10. ~~强化重型导弹：发射状态、制导、生命值、爆炸效果、网络显示~~ 制导和网络显示已完成：`HeavyNuclearMissileBlock` 发射后实时跟随全舰当前锁定（`ship.LockedTarget`），雷达丢失目标时主动减速到静止，从未锁定则沿本体朝向直飞；`IsAlive` 现在要求 `launched`，未发射的导弹（含盟友的）不会出现在任何雷达上。爆炸效果与生命值模型本身未变动，仍是后续项。
```

- [ ] **Step 2: Update the implementation inventory to include `SpaceCaptainBlock`'s new role and `CaptainRadarView`**

Find:

```
- `SpaceCaptainBlock.cs`：舰船指挥/UI 入口。
```

replace with:

```
- `SpaceCaptainBlock.cs`：舰船指挥/UI 入口，持有全舰目标锁定权威（`ShipState.LockedTarget`/`LockedSolution`），并托管 `CaptainLockNet` 联机同步。
- `CaptainRadarView.cs`：船长的 3D 雷达图（口袋维度相机渲染 + `OnGUI` 面板），显示舰船/重型导弹为箭头/圆球，支持右键旋转、滚轮缩放、左键点选锁定。
```

- [ ] **Step 3: Grep-verify (repo-wide consistency)**

Run: `grep -rn "ApplyShieldEffect\|Iff.IsActive && target.Team" src/BeyondSpiderAssembly/`
Expected: no matches (confirms this plan didn't reintroduce anything the earlier shield plan or Task 3 removed).

Run: `grep -rln "CaptainRadarView" src/BeyondSpiderAssembly/`
Expected: `CaptainRadarView.cs`, `Mod.cs`, `SpaceCombatCore.cs`.

Run:
```bash
python3 -c "import xml.dom.minidom; xml.dom.minidom.parse('BeyondSpider/Mod.xml'); print('Mod.xml still OK (unchanged, no new blocks)')"
```
Expected: prints OK (this plan never touches `Mod.xml`, no new placeable block — this just confirms nothing else broke it).

- [ ] **Step 4: Commit**

```bash
git add docs/agent-besiege-mod-guide.md
git commit -m "docs: mark captain radar lock and heavy-missile guidance as implemented"
```

## Self-review

- **Spec coverage**: §0 orientation (Task 9's `captain.InverseTransformVector`/`InverseTransformDirection`, never `ship.Core.transform`), §1 lockable targets/lock state (Tasks 1–2), §2 hostility/coloring (Task 3, Task 9's `IsHostile` call), §3 missile radar visibility (Task 2 Step 3), §4 radar rendering (Tasks 8–9), §5 input/HUD button (Tasks 10–11), §6 heavy missile guidance (Task 4), §7 CIWS/gunner priority (Tasks 5–6), §8 multiplayer sync (Task 7). Every spec component has at least one task.
- **Placeholder scan**: no TBDs; every step has complete, concrete code and exact constants (matching the spec's §"Constants" section: pocket origin `(0, 500000, 0)`, `DisplayRadius = 4`, camera offset `(0,0,-9)`, FOV 45°, `RenderTexture` 512×512, panel 420×420, `MetersPerUnit` default 50 clamped `[5, 2000]`, pitch clamp `[-85, 85]`, lead heuristic `0.35`).
- **Type consistency**: `FireSolution` (existing struct: `Target`, `AimPoint`, `TimeToImpact`, `Priority`) is used identically by `ship.DefensiveSolution` (existing) and the new `ship.LockedSolution` (Task 1/5), and both `CiwsBlock`/`SpaceGunnerBlock` (Task 6) read the same `.Target`/`.AimPoint` fields off whichever `FireSolution` they pick. `ILockable.GuidHash` (Task 1) is implemented identically (`public int GuidHash { get; private set; }`, set in `OnSimulateStart`) by `SpaceShipCore` and `HeavyNuclearMissileBlock` (Task 2), and read with the same name by `CaptainLockNet` (Task 7) and `CaptainRadarView.TryLockAtMouse` (Task 10). `CaptainRadarView.SetOpen(bool)` (Task 10) is the only method Task 11's button calls to change `IsOpen`/`radarCamera.enabled` — no other task duplicates that toggle.
- **Ambiguity check**: every open design question (host block, hostile-filter ownership, lock binding live-vs-snapshot, no-lock-at-launch fallback, guidance model, un-launched-missile visibility, lock-icon asset, mouse-capture scope, CIWS/gunner priority arbitration) was already resolved in the design spec before this plan was written; this plan doesn't introduce any new unresolved choice.
