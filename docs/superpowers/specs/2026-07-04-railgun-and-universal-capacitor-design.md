# Railgun barrel replacement + Universal capacitor charging

## Context

This is the first concrete step inside the "energy-firepower-armor" validation chain (see `docs/adr/0002-prioritize-energy-firepower-armor-chain.md`): improve the firepower and energy halves before armor. Two independent changes:

1. `SpaceGunBlock` ("Space Kinetic Gun", plain chemical-style gun) is replaced in place by an electromagnetic railgun barrel. Not a new block — the existing block (ID 10) becomes the railgun.
2. `EnergyGrid`'s Universal capacitor bus currently never gets charged by the reactor at all. Fix it so reactor surplus that a full Armor/Shield/Weapon capacitor can't absorb spills into Universal.

## 1. Universal capacitor charging

**Current behavior** (`SpaceCombatCore.cs`):
- `EnergyGrid.Request()` already lets any bus draw its shortfall from Universal (`SpaceCombatCore.cs:113-129`). This part of the ask is already correct and untouched.
- `EnergyGrid.Tick()` only calls `Charge()` for Armor/Shield/Weapon. `reactorShare[Universal]` is hardcoded to `0f` and nothing else feeds Universal. A Universal-bus `SuperCapacitorBlock` only ever holds the half-full seed value from its first `AddCapacity()` call and never recharges.

**Change**: `Charge(bus, amount)` returns the leftover it could not absorb (because the bus is already at capacity). `Tick()` sums the leftover from Armor/Shield/Weapon and routes it into Universal:

```csharp
private float Charge(EnergyBus bus, float amount)
{
    int index = (int)bus;
    float room = capacity[index] - capacitor[index];
    float applied = Mathf.Min(Mathf.Max(0f, room), amount);
    capacitor[index] += applied;
    return amount - applied;
}

public void Tick(float deltaTime)
{
    // ...existing clamp + decay loops unchanged...
    float overflow = Charge(EnergyBus.Armor, ReactorOutput * reactorShare[0] * deltaTime)
                    + Charge(EnergyBus.Shield, ReactorOutput * reactorShare[1] * deltaTime)
                    + Charge(EnergyBus.Weapon, ReactorOutput * reactorShare[2] * deltaTime);
    if (overflow > 0f)
    {
        Charge(EnergyBus.Universal, overflow);
    }
}
```

Overflow is evaluated **per bus, per tick** — if only Shield is full this tick, only Shield's surplus spills to Universal; Armor/Weapon still receive their normal share. A ship with zero capacity on a bus (no capacitor built for it) has 100% of that bus's reactor share overflow to Universal, which is correct (no point letting it evaporate).

No other callers of `Charge()` exist, so the signature change (`void` → `float`) is contained to this file.

## 2. Railgun barrel (replaces `SpaceGunBlock`)

### Renames (in place, same numeric block ID 10)

| Old | New |
|---|---|
| `BeyondSpider/SpaceGun.xml` | `BeyondSpider/Railgun.xml` |
| `SpaceGunBlock` class | `RailgunBarrelBlock` class |
| Block `<Name>` "Space Kinetic Gun" | "Railgun Barrel" |
| Slider/key identifiers `BSSpaceGun*` | `BSRailgun*` |

`SpaceGunBlock.cs` is renamed to `RailgunBarrelBlock.cs` (keeps `SpaceGunnerBlock` in the same file, matching the existing convention of weapon+its gunner co-located, e.g. `SpaceInterceptorLauncherBlock.cs`). `ShipState.Guns` (in `SpaceCombatCore.cs`) is retyped from `List<SpaceGunBlock>` to `List<RailgunBarrelBlock>`; `SpaceGunnerBlock`'s existing fire-control loop needs no new list, just the type swap since this is a direct replacement, not an addition. `Mod.xml`'s `<Block path="SpaceGun.xml" />` entry becomes `<Block path="Railgun.xml" />`.

Mesh/texture for the block's own visual body: unchanged, reuses `BS Migrated Gun Mesh` / `BS Migrated Gun Texture` (`Gun.obj`/`Gun.png`) — confirmed no dedicated railgun 3D asset exists in this repo or in the reference `WW2-Naval` project, and generating new mesh geometry is out of reach for this change.

### Sliders / fields

- `Caliber`: `BSRailgunCaliber`, default 90, range 20–300 (mm).
- `MuzzleVelocity`: `BSRailgunVelocity`, default 2600, range 1200–6000 (m/s) — well above `SpaceFlakTurretBlock`'s shell speed (280–720 m/s, from its `InitialVelocity()`), so the railgun reads as "the fast one."
- `FireKey`: `BSRailgunFire`, default unchanged from current default (`KeyCode.C`).
- `FireControl`: `BSRailgunFireControl` toggle, default false.
- `GunGroup`: `BSRailgunGroup` text, default `"g0"`.
- No standalone energy-per-shot slider.

### Energy cost and damage (derived, not slider-set)

```csharp
float massFactor = Caliber.Value / 300f;               // matches existing SpaceKineticRound mass convention
float energyPerShot = 40f + massFactor * MuzzleVelocity.Value * MuzzleVelocity.Value * 0.00035f;
float damage = Caliber.Value * 1.1f + massFactor * MuzzleVelocity.Value * MuzzleVelocity.Value * 0.0006f;
```

Kinetic-energy-shaped (∝ mass·v²): pushing muzzle velocity up is what makes a shot expensive and powerful, unlike the old flat `EnergyPerShot` slider. Firing still uses the existing gate pattern: `ship.Energy.Request(EnergyBus.Weapon, energyPerShot) < 0.25f` → refuse to fire (same as today's `SpaceGunBlock.FireAt`).

Reload/cooldown time keeps the same shape as today: `Mathf.Clamp(0.35f + Caliber.Value / 180f, 0.18f, 6f)`.

Recoil impulse scales with caliber and velocity instead of the old flat `Caliber*3f`: `Body.AddForce(-direction * (Caliber.Value/100f) * (MuzzleVelocity.Value/500f) * 6f, ForceMode.Impulse)`.

### Projectile

Still spawns `SpaceKineticRound` (`SpaceBallistics.cs`), visualized with the already-ported `Cannon Mesh`/`Cannon Texture` (established convention, already used by `SpaceFlakTurretBlock`).

New field on `SpaceKineticRound`: `public float MassEstimate` (set by every spawner to the same value used for `Rigidbody.mass`, so the shield loop can read it without an extra `GetComponent` call — see below). Existing spawners (`RailgunBarrelBlock`, `SpaceFlakTurretBlock`) both set this; defaults to 0 if unset (harmless — only the new shield logic reads it, and it only matters for the round's own spawner-set value).

### Pierce effect (reused from WW2-Naval, not procedural)

Confirmed: `BeyondSpider/Resources/WW2Migrated/space-perice.ab` is a byte-for-byte copy of WW2-Naval's `perice.ab` (both 59,340 bytes), containing a `GameObject` named `"Perice"` internally. Add a small static cache (no need for a full `AssetManager` singleton — this is the only asset being loaded right now):

```csharp
internal static class SpaceEffectAssets
{
    private static GameObject pierceEffectPrefab;

    public static GameObject PierceEffectPrefab
    {
        get
        {
            if (pierceEffectPrefab == null)
            {
                pierceEffectPrefab = ModResource.GetAssetBundle("space-perice").LoadAsset<GameObject>("Perice");
            }
            return pierceEffectPrefab;
        }
    }
}
```

On `SpaceKineticRound.OnCollisionEnter`, when `SpawnImpactSpark` is `true` (new bool field, default `false` so `SpaceFlakTurretBlock`'s existing rounds are unaffected): instantiate `SpaceEffectAssets.PierceEffectPrefab` at the hit point, scale by `Caliber/400f` (matching WW2-Naval's `Gun.cs:213-214` scaling), destroy after 1s, and play the already-ported `BS Migrated GunPierce Audio` clip through a one-shot `AudioSource` (same shape as WW2-Naval's `AddPierceSound`). `RailgunBarrelBlock` sets `SpawnImpactSpark = true` on the rounds it spawns. **No explosion instantiate, no explosion sound** — matches "no charge, so no detonation."

Muzzle effect: minimal. Reuse the already-ported `fire.wav` (`BS Migrated GunShot Audio`) as a one-shot cue on fire, the same pattern as WW2-Naval's `AddFireSound`. No propellant-smoke instantiate (same "no charge" reasoning applies to muzzle smoke as to explosions) — this was previously planned as a procedural particle spark; dropped in favor of just the sound cue to avoid inventing a new unrequested visual.

### Gunner integration

Unchanged shape: `SpaceGunnerBlock.SimulateFixedUpdateHost()` already loops `ship.Guns` (now typed `RailgunBarrelBlock`) and calls `TryFireControl(ship.DefensiveSolution)` when the aim angle is within tolerance. No new list, no new loop.

### Shield interaction (generalizing `ShieldProjectorBlock`)

Today `ShieldProjectorBlock.SimulateFixedUpdateHost()` only ever special-cases `HeavyNuclearMissileBlock` — it ignores every `SpaceKineticRound` entirely, so "very high speed makes shield interception more expensive" (the framework's stated railgun-shield behavior) currently does nothing for any gun. Generalize the loop to also handle high-velocity `SpaceKineticRound`s:

```csharp
const float HyperVelocityThreshold = 2200f; // above SpaceFlakTurretBlock's 280-720 m/s shell range

// inside the existing SimulateFixedUpdateHost loop over SpaceCombatRegistry.Trackables:
SpaceKineticRound round = targets[i] as SpaceKineticRound;
if (round != null && round.IsAlive && round.Team != Team
    && round.Velocity.magnitude > HyperVelocityThreshold)
{
    float distance = Vector3.Distance(transform.position, round.Position);
    if (distance <= Radius.Value)
    {
        float energyCost = round.Velocity.sqrMagnitude * round.MassEstimate * 0.002f / Mathf.Max(0.2f, Strength.Value);
        float ratio = ship.Energy.Request(EnergyBus.Shield, energyCost * Time.fixedDeltaTime);
        round.ApplyShieldEffect(ratio, Strength.Value);
    }
}
```

New `SpaceKineticRound.ApplyShieldEffect(float ratio, float strength)`: reduces the round's rigidbody velocity by `Mathf.Lerp(1f, 0.35f, Mathf.Clamp01(ratio * strength))` (heating/slowdown per the framework's defensive-interactions rule), and if `ratio * strength` exceeds a full-stop threshold (`> 1.5f`), destroys the round outright (fully intercepted). This only ever fires for rounds above the hyper-velocity threshold, so regular flak shells (max 720 m/s) are never affected — only railgun-tier slugs are.

### Explicitly out of scope

- Hull/armor damage on hit: `SpaceKineticRound.OnCollisionEnter` still only applies damage to `HeavyNuclearMissileBlock`. Nano armor / `DamageRouter` remain tracked separately (`docs/adr/0002-prioritize-energy-firepower-armor-chain.md`), not part of this change.
- No new mesh/model asset — barrel body keeps `Gun.obj`/`Gun.png` per the confirmed decision.

## 3. Documentation follow-up

`docs/agent-besiege-mod-guide.md` and `docs/space-combat-framework.md` currently describe `SpaceGunBlock` as a separate plain kinetic gun alongside a planned-but-unbuilt `RailgunBarrel`. Both need a small update to reflect that the kinetic gun *is* the railgun now (no separate plain gun exists). `docs/adr/0002-...md` references `SpaceGunBlock` by name and should be updated to `RailgunBarrelBlock` for accuracy.

## Self-review notes

- No placeholders/TBDs remain; every formula and asset name above was checked against actual files (`SpaceCombatCore.cs`, `SpaceFlakTurretBlock.cs`, `WW2-Naval/src/WW2NavalAssembly/{Gun.cs,AssetManager.cs}`, byte-size comparison of `perice.ab` vs `space-perice.ab`).
- Scope check: this is one coherent change (one block replacement + one energy-grid fix). Armor/DamageRouter work is explicitly excluded and cross-referenced to the existing ADR rather than silently expanded into.
- Consistency check: `MassEstimate` is added to the shared `SpaceKineticRound` class but defaults to 0 and is only consumed by the new shield-interaction code, so `SpaceFlakTurretBlock`'s existing behavior is unaffected by the field's addition.
