# Nano armor + DamageRouter design

## Context

This closes the last unbuilt link in the "energy-firepower-armor" validation chain (`docs/adr/0002-prioritize-energy-firepower-armor-chain.md`): the Armor energy bus exists and is allocated at the ship level, but nothing consumes it and no hit anywhere in the codebase currently affects hull integrity. Firepower (`RailgunBarrelBlock`, `SpaceFlakTurretBlock`) and energy (`EnergyGrid`) are already wired; this spec adds the armor side and the routing that connects hits to it.

Grounding for every mechanic below comes from three places, cross-checked against each other: `CONTEXT.md`'s and `docs/space-combat-framework.md`'s existing (already-committed) nano-armor terminology and formulas, `策划案.md`'s original design brief, and the actual WW2-Naval reference source (`WoodenArmour.cs`, `CannonWell.cs`, `AssetManager.cs`) which has real, shipped, working code for the closest analogous mechanics (per-block armor component, joint-breaking on destruction, and the damage-visualization overlay).

## Scope

**In scope**: a per-wood-block armor component with two damage stages, a `DamageRouter` entry point, wiring the two existing kinetic weapons and the heavy nuclear missile's detonation through it, a HUD readout, and a toggleable HP visualization overlay.

**Explicitly deferred** (flagged, not silently dropped):
- The laser weapon itself (`LaserBarrelBlock` doesn't exist in this codebase yet). The armor's laser-damage-multiplier formula is included since it's already fully specified in existing docs and is a one-line pure function, but nothing will call it yet.
- Multi-block penetration for kinetic rounds. `SpaceKineticRound` already destroys itself on its first collision today (against everything, including `HeavyNuclearMissileBlock`); this spec keeps that single-hit-resolution convention rather than introducing WW2-style multi-block bullet penetration.
- Multiplayer sync of `Integrity`/`StructuralValue`/HUD values. Consistent with the fact that `EnergyGrid`'s own bus levels aren't synced to clients today either — this isn't a regression, just staying at the same MP-maturity level as everything else in this codebase.

## Components

### 1. `NanoArmorController` (new, `SingleInstance<NanoArmorController>`)

Same base class `SpaceFlakTurretNet` already uses. In `Awake()`, subscribes `Events.OnBlockInit`. When the initialized block's `BlockID` matches `SingleWoodenBlock`, `DoubleWoodenBlock`, or `Log` (the same three types WW2-Naval's `CustomBlockController` attaches `WoodenArmour` to), adds a `NanoArmorBehaviour` if one isn't already present. Registered once in `Mod.cs` (`Root.AddComponent<NanoArmorController>()`), matching how `SpaceFlakTurretNet` is registered today.

This is a parasitic attachment onto vanilla blocks, not a new placeable custom Block — no new XML file, no `Mod.xml` block registration. `CONTEXT.md` and `策划案.md` both explicitly require the carrier to be the vanilla wood block, matching WW2-Naval's own precedent.

### 2. `NanoArmorBehaviour` (new, plain `MonoBehaviour` — not a `SpaceBlock`/`BlockScript`, since it isn't a player-placed Block)

Resolves its own `PlayerID`/`Team` from `GetComponent<BlockBehaviour>().ParentMachine` (same info `SpaceBlock.SafeAwake()` reads, just done manually since this component isn't a `BlockScript`). Looks up its ship via `SpaceCombatRegistry.FindShip(playerID)` and registers itself into a new `ShipState.Armor` list (`List<NanoArmorBehaviour>`, registered/unregistered via the existing generic `SpaceCombatRegistry.RegisterSubsystem`/`RemoveSubsystem`, same pattern every other subsystem list already uses).

**Two damage stages, both on this one component:**

1. **`Integrity`** (float, 1→0). The self-repairing nano coating.
   - Kinetic/blast damage while `Integrity > 0` reduces it: `Integrity -= damage / IntegrityPool` (`IntegrityPool = 420`, a tunable constant — no player-facing slider; nano armor is fully automatic per its "self-maintaining" framing, the player's only configuration lever is the `ArmorShare` energy allocation on `SpaceShipCore`, which already exists).
   - No carry-over: if a single hit's damage exceeds what's needed to zero `Integrity`, the excess is discarded rather than spilling into `StructuralValue` in the same hit. Keeps the model simple; the next hit (now with `Integrity == 0`) starts consuming `StructuralValue`.
   - Repairs continuously from the `Armor` energy bus, every `FixedUpdate`, host-only: `wantedRepair = RepairRatePerSecond * Time.fixedDeltaTime` (`RepairRatePerSecond = 0.05`, i.e. 5%/second of `IntegrityPool` at full power); `energyNeeded = wantedRepair * IntegrityPool * EnergyPerRepairPoint` (`EnergyPerRepairPoint = 4`); `ratio = ship.Energy.Request(EnergyBus.Armor, energyNeeded)`; `Integrity += wantedRepair * ratio²`. This is the already-documented `ratio²` nonlinearity (50% power → 25% effective repair).
   - Governs laser reflectivity via `LaserDamageMultiplier = 0.05 + 0.95 * (1 - Integrity)` — the already-documented formula (full integrity reflects 95%, only 5% gets through). Exposed as a public read-only property; unused until a laser weapon exists.

2. **`StructuralValue`** (float, 1→0). Only takes damage once `Integrity` has reached 0 (from further kinetic/blast hits, or whatever fraction a laser hit "gets through" once a laser weapon exists). Does **not** self-repair — no repair mechanic is specified for it anywhere in the design brief. `StructuralValue -= damage / StructuralPool` (`StructuralPool = 620` — tougher than the coating, matching the intuition that bare wood/frame still has some resilience once the nano coating is gone).
   - Governs the block's physics joints: at `StructuralValue == 1`, joints are left completely untouched (matches "100% = invincible" exactly as specified). Once it drops below 1, every joint in both `BlockBehaviour.iJointTo` and `.jointsToMe` gets `breakForce`/`breakTorque` set to `Lerp(0, HealthyJointForce, StructuralValue)` (`HealthyJointForce = 45000`, chosen comfortably above WW2's own 35000 floor reference in `WoodenArmour.BreakForceOptimize()`).
   - At `StructuralValue == 0`: joints are driven to ~0 break force, and a small local `AddExplosionForce` is applied at the block's own position — mirroring WW2-Naval's `CannonWell.WellExploForce()`/`AmmoExploforce()` technique exactly, because lowering `breakForce` alone doesn't retroactively snap an already-stable joint (Unity only breaks a joint when currently-applied force exceeds the threshold that frame); the explosion force guarantees the disconnect actually happens immediately rather than waiting on ambient gameplay forces.

### 3. `DamageRouter` (new, static class, own file)

One real method for now: `RoutePhysicalHit(Collider hitCollider, float damage)` — finds `NanoArmorBehaviour` via `hitCollider.GetComponentInParent<NanoArmorBehaviour>()` (same resolution pattern `SpaceKineticRound.OnCollisionEnter` already uses for `HeavyNuclearMissileBlock`) and calls `ApplyPhysicalDamage` if found. This is the single entry point every physical-damage source below goes through, giving a stable seam for the deferred laser/structural work later without needing to touch call sites again.

### 4. Wiring existing weapons

- **`SpaceKineticRound.OnCollisionEnter`**: calls `DamageRouter.RoutePhysicalHit(collision.collider, Damage)` unconditionally, alongside the existing `HeavyNuclearMissileBlock` check. Still destroys itself on this same collision either way (single-hit-resolution, per the scope decision above).
- **`HeavyNuclearMissileBlock.Detonate()`**: gets a new `BlastRadius` slider (default 40, range 10–120). On detonation, iterates every `ShipState.Armor` entry across `SpaceCombatRegistry.Ships` (registry-based, not a `Physics.OverlapSphere` scan — matching this codebase's own stated performance rule of preferring registries over broad physics scans) and applies `HealthSlider.Value * 0.6 * falloff` to each armor component within `BlastRadius`, where `falloff = Clamp01(1 - distance/BlastRadius)`. No IFF/team check — a nuke detonating that close should damage its own ship too, matching physical reality and the `策划案` rationale for why these missiles need to be slow and interceptable in the first place.

### 5. HUD

`SpaceCombatRuntime.OnGUI()` gets one more line reading the local ship's new `Armor` list: block count plus average `Integrity`/`StructuralValue` percentages, in the same style as the existing energy-bus percentage line.

### 6. Visualization

New `MToggle ShowArmorHP` on `SpaceShipCore` (default off) — matching this codebase's existing convention that all configuration is a per-block toggle/slider, not a separate settings window.

Each `NanoArmorBehaviour`, in `Start()`, instantiates the ported `"SingleVis"` prefab from the `space-armourvis.ab` asset bundle (confirmed byte-identical, 236,886 bytes, to WW2-Naval's own `armourvis.ab` — this is the exact prefab `WoodenArmour.InitVis()` already uses for every wood block type in the shipped WW2 mod) as a child, positioned/scaled per `BlockID` using WW2's own exact per-type values (including checking `Vis/HalfVis`'s enabled state to know which length-variant a Double/Log block currently is), on render layer 25 (matches WW2, needed so the overlay isn't occluded by the hull mesh it's laid over).

Each `FixedUpdate`, reads `ship.Core.ShowArmorHP.IsActive`:
- **Off**: overlay object inactive, the block's own normal visual (`Vis` child, per WW2-Naval's vanilla-block hierarchy convention) is shown.
- **On**: overlay active, normal visual hidden, overlay's material color set to:
  - Green with `alpha = MaxOverlayAlpha * Integrity` while `Integrity > 0` (confirmed direction: higher HP → more opaque, fading toward invisible as it depletes).
  - Red with `alpha = MaxOverlayAlpha * StructuralValue` once `Integrity` has reached 0.
  - `MaxOverlayAlpha = 0.6`, matching WW2-Naval's own armor-overlay alpha constant.

No shared palette array (WW2's 66/20-entry `ArmorMat`/`CrewMat`) is needed since Unity's `Renderer.material` already clones per-instance on first access, and this codebase already creates fresh per-instance materials elsewhere (`SpaceFlakTurretBlock`'s particle effects) rather than pooling — one material per armored block is consistent with existing practice, not a new pattern.

## Data flow summary

1. Ship builds with wood blocks → `NanoArmorController` attaches a `NanoArmorBehaviour` to each.
2. Simulation starts → each registers into its ship's `ShipState.Armor` list.
3. Every `FixedUpdate`: `Integrity` repairs from the Armor bus if below 1; the visualization overlay's color/visibility updates.
4. A `RailgunBarrelBlock`/`SpaceFlakTurretBlock` round hits a wood block → `SpaceKineticRound.OnCollisionEnter` → `DamageRouter.RoutePhysicalHit` → `NanoArmorBehaviour.ApplyPhysicalDamage` → `Integrity` (or `StructuralValue`, if `Integrity` is already 0) decreases.
5. A `HeavyNuclearMissileBlock` detonates → iterates every ship's `Armor` list within blast radius → same `ApplyPhysicalDamage` path, scaled by distance falloff.
6. If `StructuralValue` reaches 0 on some block → its joints are weakened to near-zero and force-disconnected that same frame.

## Constants (tunable, not precision-engineered — same treatment as every other hand-picked number already in this codebase)

- `IntegrityPool = 420`, `StructuralPool = 620`
- `RepairRatePerSecond = 0.05`, `EnergyPerRepairPoint = 4`
- `HealthyJointForce = 45000`
- Missile: `BlastRadius` slider default 40 (range 10–120), `baseBlastDamage = HealthSlider.Value * 0.6`
- `MaxOverlayAlpha = 0.6`

## Files touched

- Create: `src/BeyondSpiderAssembly/NanoArmorBlock.cs` (`NanoArmorController` + `NanoArmorBehaviour`, mirroring the existing convention of co-locating a controller with its behavior, e.g. `SpaceFlakTurretNet`+`SpaceFlakTurretBlock`)
- Create: `src/BeyondSpiderAssembly/DamageRouter.cs`
- Modify: `SpaceCombatCore.cs` (`ShipState.Armor` list, HUD line), `SpaceShipCore.cs` (`ShowArmorHP` toggle), `SpaceBallistics.cs` (`SpaceKineticRound.OnCollisionEnter` routing), `HeavyNuclearMissileBlock.cs` (blast damage), `Mod.cs` (register `NanoArmorController`)
- No `Mod.xml` changes needed — no new placeable Block, and `space-armourvis.ab` is already declared as a resource.

## Self-review

- **Placeholder scan**: no TBDs; every formula/constant has a concrete starting value.
- **Internal consistency**: the two-stage model (Integrity gates/absorbs first, Structural only after) is applied identically for kinetic hits and missile blast; laser is specified but has no caller yet, flagged explicitly rather than left ambiguous.
- **Scope check**: appropriately sized for one implementation plan — mechanics and visualization are coupled (visualization directly displays the mechanics' own state), not independent subsystems that need separate specs.
- **Ambiguity check**: the "opacity vs HP" direction and the toggle's location were both explicitly resolved with the user rather than assumed.
