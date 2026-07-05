# Paraboloid shield design

## Context

`ShieldProjectorBlock` ([ShieldProjectorBlock.cs](../../../src/BeyondSpiderAssembly/ShieldProjectorBlock.cs)) currently checks containment with a plain `Vector3.Distance` sphere and explicitly skips same-team targets (`if (missile.Team == Team) continue`). Both of these already contradict the committed design: `docs/space-combat-framework.md` describes the shield as "a configurable paraboloid electromagnetic field" from the start, and `docs/agent-besiege-mod-guide.md`'s roadmap (item 11) lists "从球形距离检测过渡到可配置抛物面/扇面场" as the next shield task. This spec finishes that transition and adds two things the docs didn't yet specify: the field has no IFF (it slows everything inside it, including the owner's own fire) and maintaining the field costs power on its own, separate from the cost of actually slowing something down. It also adds a visualization of the dish, since none exists today (the only precedent, `NanoArmorBlock`'s HP overlay, reuses a ported prefab that has no paraboloid equivalent — this one is generated procedurally).

## Scope

**In scope**: replacing the sphere check with a paraboloid containment test, removing the team skip from both the missile and kinetic-round loops, replacing the ad-hoc velocity-decay effect with a linear-in-speed deceleration model, adding a standing upkeep energy draw, and a build-mode-togglable procedural dish visualization with a flash-on-intercept pulse and a player-chosen hue.

**Explicitly deferred**:
- Sector/cone-limited variants ("扇面场" mentioned alongside "抛物面" in the roadmap item) — this spec only implements the paraboloid.
- Laser interaction — unchanged; the shield still only ever touches `HeavyNuclearMissileBlock` and `SpaceKineticRound`, never beams (no laser weapon exists in this codebase yet, consistent with `NanoArmorBlock`'s deferral of the same thing).
- Multiplayer sync of the visualization or the new sliders — consistent with the rest of the mod's current MP maturity level (`EnergyGrid` bus levels and `ShowArmorHP` aren't synced either).

## Components

### 1. Shape and orientation

Vertex at the block's position, axis = `transform.forward` (same convention `RadarPanelBlock` already uses for its cone, so the player's build-mode orientation of the block decides which arc is protected).

For a candidate target position `P`:
```
delta = P - origin
a = Dot(delta, forward)          // axial distance from the vertex
r2 = delta.sqrMagnitude - a*a    // squared perpendicular distance from the axis
```
Contained iff `0 <= a <= Depth.Value` and `r2 <= Radius.Value * Radius.Value * a / Depth.Value`.

This is the standard paraboloid-of-revolution `a = r² / (4f)` rearranged so the two exposed sliders are directly "how wide" and "how deep" instead of a raw focal length. At `a = 0` (right at the block) the field pinches to a point; at `a = Depth` the cross-section radius reaches `Radius`. Both `Radius` (existing slider, key `BSShieldRadius`, unchanged range 20–500, default 120 — reinterpreted from "sphere radius" to "aperture radius at max depth" but the key and numeric range stay so old saves still load) and the new `Depth` slider (key `BSShieldDepth`, label "Shield Depth", default 70, range 15–250) are exposed.

### 2. No IFF

Both loops in `SimulateFixedUpdateHost` drop their `target.Team == Team` skip entirely. The only remaining eligibility gates are `IsAlive`, the paraboloid containment test above, and (for kinetic rounds only, unchanged) the existing `HyperVelocityThreshold = 2200` speed floor — missiles have no speed floor, matching today's behavior. A ship's own gunfire and own missiles are just as subject to the field as an enemy's.

### 3. Deceleration physics

Per eligible target, per tick, with `speed = velocity.magnitude`:
```
effectiveStrength = Strength.Value * upkeepRatio          // upkeepRatio: see §4
decelAccel        = DecelCoefficient * effectiveStrength * speed   // linear-in-speed, so faster => harder braking
desiredDeltaV     = min(speed, decelAccel * Time.fixedDeltaTime)
energyCost        = EnergyPerDeltaV * mass * speed * desiredDeltaV
ratio             = ship.Energy.Request(EnergyBus.Shield, energyCost)
appliedDeltaV     = desiredDeltaV * ratio
newVelocity       = speed > 0 ? velocity * ((speed - appliedDeltaV) / speed) : velocity
```
`DecelCoefficient = 0.02` and `EnergyPerDeltaV = 0.02` are code constants (no slider — `Strength` is already the player-facing knob; these two just fix its units). The energy cost formula keeps the same "kinetic-threat-shaped" shape the old code used (`mass * speed²`-ish, since `desiredDeltaV` itself scales with `speed`), so the docs' "faster = pricier" rule still holds, but the cost now feeds back through the same `effectiveStrength` the deceleration itself uses, rather than two independently-tuned formulas. `EnergyPerDeltaV` is calibrated against `SuperCapacitorBlock`'s default capacity (600, scaled by block size) and `SpaceShipCore`'s default reactor output (1200/second total, split across buses) so a typical kinetic-round hit costs on the order of a few ticks' worth of reactor trickle rather than draining the entire Shield-bus capacitor pool on first contact.

Both target types replace their `ApplyShieldEffect(float strength)` method with `ApplyShieldDeceleration(Vector3 newVelocity, float appliedDeltaV)`, called by `ShieldProjectorBlock` with the values computed above:
- **`SpaceKineticRound`**: sets `body.velocity = newVelocity`. If the resulting speed drops below `RoundStallSpeed = 40`, the round is treated as absorbed and destroyed (mirrors the old "power > 1.5 ⇒ destroy" overwhelm case, but now driven by the actual physical outcome instead of a separate magic threshold).
- **`HeavyNuclearMissileBlock`**: sets `Body.velocity = newVelocity` and calls the existing `ApplyDamage(MissileDamagePerDeltaV * appliedDeltaV)` (`MissileDamagePerDeltaV = 0.4`) — deceleration stress replaces the old flat `8f * strength * dt` damage tick, so a missile that's barely being slowed takes barely any damage.

### 4. Upkeep energy

Before the per-target loop, every tick:
```
upkeepCost = UpkeepBase + UpkeepPerVolume * Radius.Value * Depth.Value
upkeepRatio = ship.Energy.Request(EnergyBus.Shield, upkeepCost * Time.fixedDeltaTime)
```
`UpkeepBase = 6`, `UpkeepPerVolume = 0.02` (code constants). This draw happens unconditionally — even with nothing inside the field — and `upkeepRatio` feeds into `effectiveStrength` above, so an underpowered shield is uniformly weaker rather than binary on/off, consistent with this mod's "systems degrade by ratio" rule. A bigger dish (`Radius * Depth`) costs more to sustain.

### 5. Visualization

New build-mode controls on `ShieldProjectorBlock`:
- `MToggle AlwaysVisible` (key `BSShieldAlwaysVisible`, label "Always Show Shield", default off).
- `MSlider ColorHue` (key `BSShieldColorHue`, label "Shield Color", default 0.55, range 0–1), mapped through `Color.HSVToRGB(hue, 1f, 1f)`.

On `OnSimulateStart`, a child `GameObject` ("BS Shield Field Vis") is created with a `MeshFilter`/`MeshRenderer` using a new `Material(Shader.Find("Particles/Additive"))` (same shader family `SpaceFlakTurretBlock` already uses for its glow effects — additive blending means visibility is controlled by scaling the color's brightness, not the alpha channel, which is what the flash/dim behavior below relies on). Destroyed in `OnSimulateStop`.

**Mesh**: a polar grid (`RingCount = 10` rings × `SegmentCount = 32` segments) built directly from the same formula as the containment test — ring `j`'s axial position is `aj = Depth.Value * j / RingCount`, its radius is `rj = Radius.Value * Sqrt(aj / Depth.Value)`, vertices are `(rj*cos θ, rj*sin θ, aj)` in the child's local space (identity local rotation, so local +Z lines up with the parent's `transform.forward`, matching the containment axis). Triangles connect adjacent rings; each quad is emitted with both winding orders so the dish is visible from either side without depending on shader cull state. The mesh is only rebuilt when `Radius.Value`/`Depth.Value` differ from the last-built values by more than 0.01, checked once per tick (cheap comparison, rare rebuild).

**Brightness**: 
```
baseAlpha  = AlwaysVisible.IsActive ? BaseAlpha * upkeepRatio : 0f     // BaseAlpha = 0.12
flashLevel = max(0, flashLevel - Time.fixedDeltaTime / FlashDecayTime) // FlashDecayTime = 0.25
if (any target had appliedDeltaV > 0 this tick) flashLevel = 1f
intensity  = Clamp01(baseAlpha + FlashPeakAlpha * flashLevel)          // FlashPeakAlpha = 0.35
renderer.material.color = hueColor * intensity
renderer.enabled = intensity > 0.001f
```
`BaseAlpha`, `FlashPeakAlpha`, `FlashDecayTime` are code constants (deliberately not sliders, per instruction — "brightness doesn't need to be high, keep it code-tunable"). This gives exactly the two requested behaviors from one shared code path: with "Always Show Shield" on, the dish is a dim, constantly-visible film whose dimness already reflects upkeep starvation; with it off, the dish is invisible except for a brief pulse at the instant it actually brakes something, whether or not the toggle is on.

## Data flow summary

1. Build mode: player places/rotates `ShieldProjectorBlock`, sets `Radius`, `Depth`, `Strength`, `AlwaysVisible`, `ColorHue`.
2. Simulation start: registers into `ShipState.Shields` (unchanged); creates the dish visualization child.
3. Every `FixedUpdate`: requests upkeep energy → `upkeepRatio`; rebuilds the dish mesh if `Radius`/`Depth` changed; for every live `HeavyNuclearMissileBlock`/`SpaceKineticRound` in `SpaceCombatRegistry.Trackables` (no team filter) that's inside the paraboloid (and, for rounds, above the hyper-velocity threshold), computes and applies deceleration + its energy cost; updates flash/base brightness and the mesh material color.
4. A round braked below `RoundStallSpeed` is destroyed; a missile takes deceleration-proportional damage every tick it's inside the field (on top of whatever `DamageRouter` hits it takes from direct weapon fire, unrelated).

## Constants (tunable, not precision-engineered)

- `DecelCoefficient = 0.02`, `EnergyPerDeltaV = 0.02` (lowered from an initial 0.6 after the final whole-branch review found the original value made per-target deceleration cost ~100x the Shield bus's typical per-tick reactor/capacitor scale, effectively neutering the shield on first contact)
- `UpkeepBase = 6`, `UpkeepPerVolume = 0.02`
- `RoundStallSpeed = 40`, `MissileDamagePerDeltaV = 0.4`
- `RingCount = 10`, `SegmentCount = 32`
- `BaseAlpha = 0.12`, `FlashPeakAlpha = 0.35`, `FlashDecayTime = 0.25`
- Slider defaults: `Depth` 70 (range 15–250), `ColorHue` 0.55 (range 0–1)

## Files touched

- Modify: `src/BeyondSpiderAssembly/ShieldProjectorBlock.cs` (containment test, IFF removal, deceleration/upkeep energy model, visualization — the bulk of this change)
- Modify: `src/BeyondSpiderAssembly/SpaceBallistics.cs` (`SpaceKineticRound.ApplyShieldEffect` → `ApplyShieldDeceleration`)
- Modify: `src/BeyondSpiderAssembly/HeavyNuclearMissileBlock.cs` (`ApplyShieldEffect` → `ApplyShieldDeceleration`)
- Modify: `docs/space-combat-framework.md` (note no-IFF + upkeep cost under the `ShieldProjector`/defensive-interactions sections)
- Modify: `docs/agent-besiege-mod-guide.md` (check off roadmap item 11; add the no-IFF rule to 防御交互规则)
- No `Mod.xml`/`ShieldProjector.xml` changes — no new placeable Block, all new controls are sliders/toggles added in code like every other block's build-mode UI.

## Self-review

- **Placeholder scan**: no TBDs; every formula and constant has a concrete starting value.
- **Internal consistency**: the upkeep ratio feeds into `effectiveStrength`, which both the deceleration model and (indirectly, since cost depends on `desiredDeltaV`) the per-target energy draw use — a starved shield is weaker and cheaper to run per-target, not contradictory. Visualization dimness under `AlwaysVisible` also reads the same `upkeepRatio`, so the dish's own brightness is an honest readout of shield health rather than a separate cosmetic value.
- **Scope check**: sized for one implementation plan — the physics rewrite and its visualization are coupled (the flash directly reflects `appliedDeltaV`, which only exists because of the physics rewrite), not independent subsystems.
- **Ambiguity check**: shape orientation, deceleration power law (linear, not quadratic), and the visualization toggle/color/flash behavior were all explicitly resolved with the user rather than assumed.
