# Railgun Barrel Replacement + Universal Capacitor Charging Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the plain `SpaceGunBlock` ("Space Kinetic Gun") with a fully-realized electromagnetic railgun (`RailgunBarrelBlock`), and fix `EnergyGrid` so reactor surplus that Armor/Shield/Weapon capacitors can't absorb (because they're already full) spills into the Universal capacitor bus.

**Architecture:** No new blocks or subsystems — this reshapes two existing pieces in place. The gun block keeps its numeric ID (10) and slot in `ShipState.Guns`/`SpaceGunnerBlock`'s existing fire-control loop, just retyped and reformulated. The shared `SpaceKineticRound` projectile class gains a few fields and a shield-interaction method that `SpaceFlakTurretBlock`'s existing rounds simply don't opt into (default-off booleans), so nothing about the flak turret's current behavior changes. `EnergyGrid.Charge` changes from `void` to returning leftover/overflow, consumed only inside `Tick()`.

**Tech Stack:** C# (.NET Framework 3.5), Besiege ModLoader (`Modding` namespace), Unity (old, pre-`ContactPoint`-uncertainty API — avoid relying on `Collision.contacts`), XML block definitions.

## Global Constraints

- **No build toolchain is available in this environment**: no `dotnet`, `msbuild`, or `mono` on this machine, and the `.csproj`'s `HintPath`s (`..\..\..\..\Managed\Assembly-CSharp.dll` etc.) don't resolve here either. Verification in every task below is therefore **static**: grep-based reference checks (which do run for real) and manual code-trace review against the exact existing patterns in this codebase, not an actual compile. Real compilation happens in the user's own Unity/Besiege build environment.
- Follow the existing codebase's exact conventions: `SpaceBlock` subclass shape (`SafeAwake` → `AddKey`/`AddSlider`/`AddToggle`/`AddText`, `OnSimulateStart`/`OnSimulateStop` register/unregister with `SpaceCombatRegistry`, `SimulateFixedUpdateHost` for per-tick host logic).
- Do not touch hull/armor damage routing (`SpaceKineticRound.OnCollisionEnter` still only special-cases `HeavyNuclearMissileBlock`) — that's out of scope, tracked separately in `docs/adr/0002-prioritize-energy-firepower-armor-chain.md`.
- Do not invent a new mesh/texture asset for the barrel — reuse `BS Migrated Gun Mesh`/`BS Migrated Gun Texture` (`Gun.obj`/`Gun.png`), per the confirmed design decision.

---

### Task 1: EnergyGrid Universal capacitor overflow charging

**Files:**
- Modify: `src/BeyondSpiderAssembly/SpaceCombatCore.cs:98-111` (`EnergyGrid.Tick`), `:151-155` (`EnergyGrid.Charge`)

**Interfaces:**
- Consumes: nothing new.
- Produces: `EnergyGrid.Charge` now returns `float` (leftover/overflow amount) instead of `void`. No other file calls `Charge` (confirmed by grep in Task 1 Step 4), so this is a contained change.

- [ ] **Step 1: Replace `Charge` and `Tick`**

In `src/BeyondSpiderAssembly/SpaceCombatCore.cs`, replace:

```csharp
        public void Tick(float deltaTime)
        {
            for (int i = 0; i < capacitor.Length; i++)
            {
                capacitor[i] = Mathf.Min(capacity[i], capacitor[i]);
            }
            for (int i = 0; i < spentThisSecond.Length; i++)
            {
                spentThisSecond[i] = Mathf.Lerp(spentThisSecond[i], 0f, Mathf.Clamp01(deltaTime * 2f));
            }
            Charge(EnergyBus.Armor, ReactorOutput * reactorShare[0] * deltaTime);
            Charge(EnergyBus.Shield, ReactorOutput * reactorShare[1] * deltaTime);
            Charge(EnergyBus.Weapon, ReactorOutput * reactorShare[2] * deltaTime);
        }
```

with:

```csharp
        public void Tick(float deltaTime)
        {
            for (int i = 0; i < capacitor.Length; i++)
            {
                capacitor[i] = Mathf.Min(capacity[i], capacitor[i]);
            }
            for (int i = 0; i < spentThisSecond.Length; i++)
            {
                spentThisSecond[i] = Mathf.Lerp(spentThisSecond[i], 0f, Mathf.Clamp01(deltaTime * 2f));
            }
            float overflow = Charge(EnergyBus.Armor, ReactorOutput * reactorShare[0] * deltaTime)
                           + Charge(EnergyBus.Shield, ReactorOutput * reactorShare[1] * deltaTime)
                           + Charge(EnergyBus.Weapon, ReactorOutput * reactorShare[2] * deltaTime);
            if (overflow > 0f)
            {
                Charge(EnergyBus.Universal, overflow);
            }
        }
```

And replace:

```csharp
        private void Charge(EnergyBus bus, float amount)
        {
            int index = (int)bus;
            capacitor[index] = Mathf.Min(capacity[index], capacitor[index] + amount);
        }
```

with:

```csharp
        private float Charge(EnergyBus bus, float amount)
        {
            int index = (int)bus;
            float room = Mathf.Max(0f, capacity[index] - capacitor[index]);
            float applied = Mathf.Min(room, amount);
            capacitor[index] += applied;
            return amount - applied;
        }
```

- [ ] **Step 2: Confirm no other caller of `Charge` exists**

Run: `grep -n "\.Charge(\|Charge(EnergyBus" src/BeyondSpiderAssembly/*.cs`
Expected: every match is inside `SpaceCombatCore.cs`'s `EnergyGrid` class (the 3 calls inside the new `Tick`, plus the 1 for `Universal`, plus the `private float Charge(...)` definition itself). If a match appears in another file, stop and reconcile before continuing.

- [ ] **Step 3: Manual trace (stand-in for a unit test — no test runner available)**

Trace through by hand and confirm each case:
1. `capacity[Armor]=100, capacitor[Armor]=100` (full), `reactorShare[0]=0.5`, `ReactorOutput=10`, `deltaTime=1` → `Charge(Armor, 5)`: `room = max(0, 100-100) = 0`, `applied = min(0,5) = 0`, capacitor unchanged, returns `5` (full overflow). Correct: a full bus contributes its entire share to overflow.
2. `capacity[Armor]=100, capacitor[Armor]=80`, same inputs → `Charge(Armor, 5)`: `room=20`, `applied=min(20,5)=5`, capacitor becomes `85`, returns `0` (no overflow). Correct: an unfilled bus absorbs normally and contributes nothing to Universal.
3. `capacity[Armor]=0` (no Armor capacitor built) → `Charge(Armor, 5)`: `room = max(0, 0-0) = 0`, `applied=0`, returns `5` (all of it overflows to Universal). Correct per spec: no point letting it evaporate.
4. Confirm `Request()` (`SpaceCombatCore.cs:113-129`) is untouched by this change — it already draws Universal as a fallback for any bus, independent of how Universal gets charged.

- [ ] **Step 4: Commit**

```bash
git add src/BeyondSpiderAssembly/SpaceCombatCore.cs
git commit -m "feat: overflow charge into Universal capacitor bus when other buses are full"
```

---

### Task 2: SpaceKineticRound gains MassEstimate/Caliber/SpawnImpactSpark + shield effect + ported pierce effect

**Files:**
- Modify: `src/BeyondSpiderAssembly/SpaceBallistics.cs` (entire file rewritten below)

**Interfaces:**
- Consumes: `ModResource.GetAssetBundle(string)` / `.LoadAsset<GameObject>(string)`, `ModResource.GetAudioClip(string)` (both already used elsewhere in this codebase, e.g. `SpaceFlakTurretBlock.cs:423-425` for meshes/textures — same `ModResource` static API, just the AssetBundle/AudioClip variants).
- Produces:
  - `SpaceKineticRound.MassEstimate` (float, public field, default `0`)
  - `SpaceKineticRound.Caliber` (float, public field, default `0`)
  - `SpaceKineticRound.SpawnImpactSpark` (bool, public field, default `false`)
  - `SpaceKineticRound.ApplyShieldEffect(float power)` (public method)
  - `SpaceEffectAssets.PlayPierceEffect(Vector3 point, float caliber)` (internal static method)
  - `SpaceEffectAssets.PlayMuzzleSound(Transform origin, float caliber)` (internal static method)

- [ ] **Step 1: Rewrite `SpaceBallistics.cs`**

Replace the entire contents of `src/BeyondSpiderAssembly/SpaceBallistics.cs` with:

```csharp
using Modding;
using UnityEngine;

namespace BeyondSpiderAssembly
{
    public static class SpaceBallistics
    {
        public static float EstimateInterceptTime(Vector3 shooter, Vector3 target, Vector3 targetVelocity, float muzzleVelocity)
        {
            Vector3 delta = target - shooter;
            float a = Vector3.Dot(targetVelocity, targetVelocity) - muzzleVelocity * muzzleVelocity;
            float b = 2f * Vector3.Dot(delta, targetVelocity);
            float c = Vector3.Dot(delta, delta);

            if (Mathf.Abs(a) < 0.001f)
            {
                return c > 0.001f ? Mathf.Max(0f, -c / Mathf.Max(0.001f, b)) : 0f;
            }

            float discriminant = b * b - 4f * a * c;
            if (discriminant < 0f)
            {
                return delta.magnitude / Mathf.Max(1f, muzzleVelocity);
            }

            float root = Mathf.Sqrt(discriminant);
            float t0 = (-b - root) / (2f * a);
            float t1 = (-b + root) / (2f * a);
            float best = 999f;
            if (t0 > 0f)
            {
                best = t0;
            }
            if (t1 > 0f && t1 < best)
            {
                best = t1;
            }
            return best == 999f ? delta.magnitude / Mathf.Max(1f, muzzleVelocity) : best;
        }

        public static Vector3 AimPoint(Vector3 shooter, SensorTrack track, float muzzleVelocity)
        {
            float time = EstimateInterceptTime(shooter, track.Position, track.Velocity, muzzleVelocity);
            return track.Position + track.Velocity * Mathf.Clamp(time, 0f, 8f);
        }

        public static bool IsHostile(SpaceBlock block, ITrackable target)
        {
            return block != null && target != null && target.PlayerID != block.PlayerID && target.Team != block.Team;
        }
    }

    internal static class SpaceEffectAssets
    {
        private static GameObject pierceEffectPrefab;
        private static bool pierceLoadAttempted;

        private static GameObject PierceEffectPrefab
        {
            get
            {
                if (pierceEffectPrefab == null && !pierceLoadAttempted)
                {
                    pierceLoadAttempted = true;
                    pierceEffectPrefab = ModResource.GetAssetBundle("space-perice").LoadAsset<GameObject>("Perice");
                }
                return pierceEffectPrefab;
            }
        }

        public static void PlayPierceEffect(Vector3 point, float caliber)
        {
            GameObject prefab = PierceEffectPrefab;
            if (prefab == null)
            {
                return;
            }

            GameObject pierceEffect = Object.Instantiate(prefab, point, Quaternion.identity);
            pierceEffect.transform.localScale = Vector3.one * Mathf.Max(0.1f, caliber / 400f);
            Object.Destroy(pierceEffect, 1f);

            AudioSource audioSource = pierceEffect.AddComponent<AudioSource>();
            audioSource.clip = ModResource.GetAudioClip("BS Migrated GunPierce Audio");
            audioSource.spatialBlend = 1f;
            audioSource.volume = Mathf.Clamp01(caliber / 1000f);
            audioSource.rolloffMode = AudioRolloffMode.Linear;
            audioSource.maxDistance = 200f;
            audioSource.Play();
        }

        public static void PlayMuzzleSound(Transform origin, float caliber)
        {
            GameObject sound = new GameObject("BeyondSpider Railgun Muzzle Sound");
            sound.transform.position = origin.position;
            AudioSource audioSource = sound.AddComponent<AudioSource>();
            audioSource.clip = ModResource.GetAudioClip("BS Migrated GunShot Audio");
            audioSource.spatialBlend = 1f;
            audioSource.volume = Mathf.Clamp01(caliber / 1000f);
            audioSource.rolloffMode = AudioRolloffMode.Linear;
            audioSource.maxDistance = 500f;
            audioSource.Play();
            Object.Destroy(sound, 3f);
        }
    }

    public class SpaceKineticRound : MonoBehaviour, ITrackable
    {
        public int OwnerPlayerID;
        public MPTeam OwnerTeam;
        public float Damage = 100f;
        public float Lifetime = 8f;
        public float RadiusValue = 0.45f;
        public float MassEstimate;
        public float Caliber;
        public bool SpawnImpactSpark;

        private Rigidbody body;
        private float spawnTime;

        public int PlayerID { get { return OwnerPlayerID; } }
        public MPTeam Team { get { return OwnerTeam; } }
        public TrackKind Kind { get { return TrackKind.LargeProjectile; } }
        public Vector3 Position { get { return transform.position; } }
        public Vector3 Velocity { get { return body == null ? Vector3.zero : body.velocity; } }
        public float Radius { get { return RadiusValue; } }
        public bool IsAlive { get { return gameObject.activeSelf && Time.time - spawnTime < Lifetime; } }

        private void Awake()
        {
            body = GetComponent<Rigidbody>();
            spawnTime = Time.time;
            SpaceCombatRegistry.RegisterTrackable(this);
        }

        private void FixedUpdate()
        {
            if (Time.time - spawnTime > Lifetime)
            {
                Destroy(gameObject);
            }
        }

        private void OnCollisionEnter(Collision collision)
        {
            HeavyNuclearMissileBlock missile = collision.collider.GetComponentInParent<HeavyNuclearMissileBlock>();
            if (missile != null && missile.PlayerID != OwnerPlayerID)
            {
                missile.ApplyDamage(Damage);
            }
            if (SpawnImpactSpark)
            {
                SpaceEffectAssets.PlayPierceEffect(transform.position, Caliber);
            }
            Destroy(gameObject);
        }

        public void ApplyShieldEffect(float power)
        {
            if (body == null || power <= 0f)
            {
                return;
            }
            if (power > 1.5f)
            {
                Destroy(gameObject);
                return;
            }
            body.velocity *= Mathf.Lerp(1f, 0.35f, Mathf.Clamp01(power));
        }

        private void OnDestroy()
        {
            SpaceCombatRegistry.UnregisterTrackable(this);
        }
    }
}
```

Note: the impact-spark position uses `transform.position` (the round's own position at the moment `OnCollisionEnter` fires), not `collision.contacts[0].point` — this old Unity/Besiege API surface is uncertain enough that it's safer to avoid, and the existing code in this file never used `collision.contacts` either.

- [ ] **Step 2: Grep-verify field/method names match what Tasks 3-5 will call**

Run: `grep -n "MassEstimate\|Caliber\|SpawnImpactSpark\|ApplyShieldEffect" src/BeyondSpiderAssembly/SpaceBallistics.cs`
Expected: shows the 3 field declarations, the `ApplyShieldEffect` method, and their usages inside `OnCollisionEnter`. Keep this exact spelling in mind for Tasks 3, 4, 5 — this is the single source of truth for these names.

- [ ] **Step 3: Commit**

```bash
git add src/BeyondSpiderAssembly/SpaceBallistics.cs
git commit -m "feat: add shield-effect, mass/caliber tracking, and ported pierce effect to SpaceKineticRound"
```

---

### Task 3: SpaceFlakTurretBlock sets MassEstimate/Caliber on its rounds

**Files:**
- Modify: `src/BeyondSpiderAssembly/SpaceFlakTurretBlock.cs:427-432` (inside `SpawnLargeRound`)

**Interfaces:**
- Consumes: `SpaceKineticRound.MassEstimate`, `SpaceKineticRound.Caliber` (from Task 2).
- Produces: nothing new; existing behavior for flak rounds is otherwise unchanged (`SpawnImpactSpark` is left at its default `false`, so flak shells still don't get a pierce spark — only the railgun opts into that).

- [ ] **Step 1: Set the two new fields when spawning a large round**

In `src/BeyondSpiderAssembly/SpaceFlakTurretBlock.cs`, find:

```csharp
            SpaceKineticRound projectile = round.AddComponent<SpaceKineticRound>();
            projectile.OwnerPlayerID = PlayerID;
            projectile.OwnerTeam = Team;
            projectile.Damage = damage;
            projectile.Lifetime = lifetime;
            projectile.RadiusValue = Mathf.Clamp(shotCaliber / 100f, 0.8f, 2.5f);
```

and add two lines immediately after `projectile.RadiusValue = ...`:

```csharp
            projectile.RadiusValue = Mathf.Clamp(shotCaliber / 100f, 0.8f, 2.5f);
            projectile.MassEstimate = rb.mass;
            projectile.Caliber = shotCaliber;
```

- [ ] **Step 2: Grep-verify `rb` is the same Rigidbody set earlier in this method**

Run: `grep -n "rb.mass\|Rigidbody rb" src/BeyondSpiderAssembly/SpaceFlakTurretBlock.cs`
Expected: one `Rigidbody rb = round.AddComponent<Rigidbody>();` declaration and one `rb.mass = 0.2f;` assignment, both above the `projectile.MassEstimate = rb.mass;` line you just added, inside the same `SpawnLargeRound` method — confirming `rb` is in scope and already assigned before you read it.

- [ ] **Step 3: Commit**

```bash
git add src/BeyondSpiderAssembly/SpaceFlakTurretBlock.cs
git commit -m "feat: record mass/caliber on flak turret rounds for shield-interaction consistency"
```

---

### Task 4: ShieldProjectorBlock reacts to hyper-velocity SpaceKineticRounds

**Files:**
- Modify: `src/BeyondSpiderAssembly/ShieldProjectorBlock.cs`

**Interfaces:**
- Consumes: `SpaceKineticRound.Velocity`, `.MassEstimate`, `.IsAlive`, `.Team`, `.Position`, `.ApplyShieldEffect(float power)` (all from Task 2).
- Produces: nothing new consumed elsewhere.

- [ ] **Step 1: Add the threshold constant and restructure the target loop**

Replace the entire `ShieldProjectorBlock` class body in `src/BeyondSpiderAssembly/ShieldProjectorBlock.cs` with:

```csharp
using System.Collections.Generic;
using UnityEngine;

namespace BeyondSpiderAssembly
{
    public class ShieldProjectorBlock : SpaceBlock
    {
        private const float HyperVelocityThreshold = 2200f;

        public MSlider Radius;
        public MSlider Strength;

        public override void SafeAwake()
        {
            base.SafeAwake();
            gameObject.name = "BeyondSpider Shield Projector";
            Radius = AddSlider("Shield Radius", "BSShieldRadius", 120f, 20f, 500f);
            Strength = AddSlider("Strength", "BSShieldStrength", 1f, 0.2f, 5f);
        }

        public override void OnSimulateStart()
        {
            base.OnSimulateStart();
            ShipState ship = OwnShip();
            if (ship != null)
            {
                SpaceCombatRegistry.RegisterSubsystem(PlayerID, this, ship.Shields);
            }
        }

        public override void OnSimulateStop()
        {
            ShipState ship = OwnShip();
            if (ship != null)
            {
                SpaceCombatRegistry.RemoveSubsystem(this, ship.Shields);
            }
        }

        public override void SimulateFixedUpdateHost()
        {
            ShipState ship = OwnShip();
            if (ship == null)
            {
                return;
            }

            IList<ITrackable> targets = SpaceCombatRegistry.Trackables;
            for (int i = 0; i < targets.Count; i++)
            {
                HeavyNuclearMissileBlock missile = targets[i] as HeavyNuclearMissileBlock;
                if (missile != null)
                {
                    if (!missile.IsAlive || missile.Team == Team)
                    {
                        continue;
                    }

                    float missileDistance = Vector3.Distance(transform.position, missile.Position);
                    if (missileDistance > Radius.Value)
                    {
                        continue;
                    }

                    float missileEnergyCost = missile.Velocity.sqrMagnitude * missile.ThreatMass * 0.002f / Mathf.Max(0.2f, Strength.Value);
                    float missileRatio = ship.Energy.Request(EnergyBus.Shield, missileEnergyCost * Time.fixedDeltaTime);
                    missile.ApplyShieldEffect(missileRatio * Strength.Value);
                    continue;
                }

                SpaceKineticRound round = targets[i] as SpaceKineticRound;
                if (round != null)
                {
                    if (!round.IsAlive || round.Team == Team || round.Velocity.magnitude <= HyperVelocityThreshold)
                    {
                        continue;
                    }

                    float roundDistance = Vector3.Distance(transform.position, round.Position);
                    if (roundDistance > Radius.Value)
                    {
                        continue;
                    }

                    float roundEnergyCost = round.Velocity.sqrMagnitude * round.MassEstimate * 0.002f / Mathf.Max(0.2f, Strength.Value);
                    float roundRatio = ship.Energy.Request(EnergyBus.Shield, roundEnergyCost * Time.fixedDeltaTime);
                    round.ApplyShieldEffect(roundRatio * Strength.Value);
                }
            }
        }
    }
}
```

(Renamed the missile branch's local `distance`/`energyCost`/`ratio` to `missileDistance`/`missileEnergyCost`/`missileRatio` since both branches now live in the same method scope and need distinct names.)

- [ ] **Step 2: Grep-verify `SpaceFlakTurretBlock`'s shell speed stays below the threshold**

Run: `grep -n "InitialVelocity" src/BeyondSpiderAssembly/SpaceFlakTurretBlock.cs`
Expected: `return Mathf.Clamp(190f + Mathf.Sqrt(shotCaliber) * 42f, 280f, 720f);` — confirms flak shells top out at 720 m/s, safely under the 2200 m/s `HyperVelocityThreshold`, so this change doesn't affect flak turret rounds at all.

- [ ] **Step 3: Commit**

```bash
git add src/BeyondSpiderAssembly/ShieldProjectorBlock.cs
git commit -m "feat: shield reacts to hyper-velocity kinetic rounds, not just heavy missiles"
```

---

### Task 5: Replace SpaceGunBlock with RailgunBarrelBlock

**Files:**
- Delete: `src/BeyondSpiderAssembly/SpaceGunBlock.cs`
- Create: `src/BeyondSpiderAssembly/RailgunBarrelBlock.cs`
- Modify: `src/BeyondSpiderAssembly/SpaceCombatCore.cs` (one line, `ShipState.Guns` type)

**Interfaces:**
- Consumes: `SpaceKineticRound.MassEstimate`/`.Caliber`/`.SpawnImpactSpark` (Task 2), `SpaceEffectAssets.PlayMuzzleSound` (Task 2, `internal` — accessible since same assembly/namespace).
- Produces: `RailgunBarrelBlock` class (replaces `SpaceGunBlock` in `ShipState.Guns`); `SpaceGunnerBlock` (unchanged responsibilities, just retyped local variable) stays in the same file.

- [ ] **Step 1: Retype `ShipState.Guns`**

In `src/BeyondSpiderAssembly/SpaceCombatCore.cs`, find:

```csharp
        public readonly List<SpaceGunBlock> Guns = new List<SpaceGunBlock>();
```

replace with:

```csharp
        public readonly List<RailgunBarrelBlock> Guns = new List<RailgunBarrelBlock>();
```

- [ ] **Step 2: Create `RailgunBarrelBlock.cs`, delete `SpaceGunBlock.cs`**

Create `src/BeyondSpiderAssembly/RailgunBarrelBlock.cs`:

```csharp
using System.Collections.Generic;
using UnityEngine;

namespace BeyondSpiderAssembly
{
    public class RailgunBarrelBlock : SpaceBlock
    {
        public MKey FireKey;
        public MToggle FireControl;
        public MText GunGroup;
        public MSlider Caliber;
        public MSlider MuzzleVelocity;

        private float reloadTime;
        private float reload;

        public override void SafeAwake()
        {
            base.SafeAwake();
            gameObject.name = "BeyondSpider Railgun Barrel";
            FireKey = AddKey("Fire", "BSRailgunFire", KeyCode.C);
            FireControl = AddToggle("Fire Control", "BSRailgunFireControl", false);
            GunGroup = AddText("Gun Group", "BSRailgunGroup", "g0");
            Caliber = AddSlider("Caliber", "BSRailgunCaliber", 90f, 20f, 300f);
            MuzzleVelocity = AddSlider("Muzzle Velocity", "BSRailgunVelocity", 2600f, 1200f, 6000f);
        }

        public override void OnSimulateStart()
        {
            base.OnSimulateStart();
            reloadTime = Mathf.Clamp(0.35f + Caliber.Value / 180f, 0.18f, 6f);
            reload = reloadTime;
            ShipState ship = OwnShip();
            if (ship != null)
            {
                SpaceCombatRegistry.RegisterSubsystem(PlayerID, this, ship.Guns);
            }
        }

        public override void OnSimulateStop()
        {
            ShipState ship = OwnShip();
            if (ship != null)
            {
                SpaceCombatRegistry.RemoveSubsystem(this, ship.Guns);
            }
        }

        public override void SimulateFixedUpdateHost()
        {
            reload = Mathf.Min(reloadTime, reload + Time.fixedDeltaTime);
            if (FireKey.IsPressed)
            {
                FireAt(transform.forward, 20f);
            }
        }

        public bool TryFireControl(FireSolution solution)
        {
            if (solution.Target == null || !FireControl.IsActive)
            {
                return false;
            }

            Vector3 aim = solution.AimPoint - transform.position;
            if (aim.sqrMagnitude < 0.001f)
            {
                return false;
            }
            return FireAt(aim.normalized, solution.TimeToImpact);
        }

        public bool FireAt(Vector3 direction, float life)
        {
            if (reload < reloadTime)
            {
                return false;
            }

            float massFactor = Caliber.Value / 300f;
            float energyPerShot = 40f + massFactor * MuzzleVelocity.Value * MuzzleVelocity.Value * 0.00035f;

            ShipState ship = OwnShip();
            if (ship != null && ship.Energy.Request(EnergyBus.Weapon, energyPerShot) < 0.25f)
            {
                return false;
            }

            reload = 0f;
            float damage = Caliber.Value * 1.1f + massFactor * MuzzleVelocity.Value * MuzzleVelocity.Value * 0.0006f;

            GameObject round = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            round.name = "BeyondSpider Railgun Slug";
            round.transform.position = transform.position + transform.forward * 1.2f;
            round.transform.localScale = Vector3.one * Mathf.Clamp(Caliber.Value / 220f, 0.12f, 1.7f);
            Rigidbody rb = round.AddComponent<Rigidbody>();
            rb.mass = Mathf.Max(0.05f, massFactor);
            rb.drag = 0.005f;
            rb.useGravity = false;
            rb.velocity = direction.normalized * MuzzleVelocity.Value + (Body == null ? Vector3.zero : Body.velocity);

            SpaceKineticRound projectile = round.AddComponent<SpaceKineticRound>();
            projectile.OwnerPlayerID = PlayerID;
            projectile.OwnerTeam = Team;
            projectile.Damage = damage;
            projectile.Lifetime = Mathf.Clamp(life + 2f, 2f, 12f);
            projectile.MassEstimate = rb.mass;
            projectile.Caliber = Caliber.Value;
            projectile.SpawnImpactSpark = true;

            SpaceEffectAssets.PlayMuzzleSound(transform, Caliber.Value);

            if (Body != null)
            {
                Body.AddForce(-direction.normalized * (Caliber.Value / 100f) * (MuzzleVelocity.Value / 500f) * 6f, ForceMode.Impulse);
            }
            return true;
        }
    }

    public class SpaceGunnerBlock : SpaceBlock
    {
        public MKey ActiveSwitch;
        public MToggle DefaultActive;
        public MText GunGroup;
        public MSlider AimTolerance;

        private bool active;

        public override void SafeAwake()
        {
            base.SafeAwake();
            gameObject.name = "BeyondSpider Space Gunner";
            ActiveSwitch = AddKey("Switch Active", "BSSpaceGunnerActive", KeyCode.None);
            DefaultActive = AddToggle("Default Active", "BSSpaceGunnerDefault", true);
            GunGroup = AddText("Gun Group", "BSSpaceGunnerGroup", "g0");
            AimTolerance = AddSlider("Aim Tolerance", "BSSpaceGunnerTolerance", 12f, 1f, 45f);
        }

        public override void OnSimulateStart()
        {
            base.OnSimulateStart();
            active = DefaultActive.IsActive;
            ShipState ship = OwnShip();
            if (ship != null)
            {
                SpaceCombatRegistry.RegisterSubsystem(PlayerID, this, ship.Gunners);
            }
        }

        public override void OnSimulateStop()
        {
            ShipState ship = OwnShip();
            if (ship != null)
            {
                SpaceCombatRegistry.RemoveSubsystem(this, ship.Gunners);
            }
        }

        public override void SimulateUpdateHost()
        {
            if (ActiveSwitch.IsPressed)
            {
                active = !active;
            }
        }

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
    }
}
```

Delete the old file:

```bash
git rm src/BeyondSpiderAssembly/SpaceGunBlock.cs
```

- [ ] **Step 3: Grep-verify no remaining reference to the old type name in code**

Run: `grep -rn "SpaceGunBlock" src/BeyondSpiderAssembly/`
Expected: no output (the class no longer exists anywhere in the assembly).

- [ ] **Step 4: Commit**

```bash
git add src/BeyondSpiderAssembly/RailgunBarrelBlock.cs src/BeyondSpiderAssembly/SpaceCombatCore.cs
git commit -m "feat: replace SpaceGunBlock with RailgunBarrelBlock (velocity-driven energy cost and damage)"
```

---

### Task 6: XML block, Mod.xml registration, and .csproj update

**Files:**
- Delete: `BeyondSpider/SpaceGun.xml`
- Create: `BeyondSpider/Railgun.xml`
- Modify: `BeyondSpider/Mod.xml:100` (Blocks list), `src/BeyondSpiderAssembly/BeyondSpiderAssembly.csproj` (Compile item)

**Interfaces:**
- Consumes: `RailgunBarrelBlock` class name (Task 5), `BS Migrated Gun Mesh`/`BS Migrated Gun Texture` resource names (already declared in `Mod.xml`, unchanged).
- Produces: nothing consumed by later tasks.

- [ ] **Step 1: Create `Railgun.xml`**

Read the current `BeyondSpider/SpaceGun.xml` content first (for reference, its current content is):

```xml
<Block>
	<Debug>false</Debug>
	<ID>10</ID>
	<Name>Space Kinetic Gun</Name>
	<Mass>0.45</Mass>
	<Script>BeyondSpiderAssembly.SpaceGunBlock</Script>
	<Mesh name="BS Migrated Gun Mesh">
		<Position x="0.0" y="0" z="0.0" />
		<Rotation x="90" y="0" z="0" />
		<Scale x="0.22" y="0.22" z="0.22" />
	</Mesh>
	<Texture name="BS Migrated Gun Texture" />
	<Icon>
		<Position x="0.1" y="-0.1" z="-0.4" />
		<Rotation x="45" y="225" z="0" />
		<Scale x="0.45" y="0.45" z="0.45" />
	</Icon>
	<Colliders>
		<BoxCollider>
			<Position x="0.0" y="0.0" z="0.2" />
			<Rotation x="0" y="0" z="0" />
			<Scale x="0.35" y="0.35" z="0.5" />
		</BoxCollider>
	</Colliders>
	<BasePoint hasAddingPoint="false">
		<Stickiness enabled="true" radius="0.3" />
		<Motion x="false" y="false" z="false" />
	</BasePoint>
	<AddingPoints />
</Block>
```

Create `BeyondSpider/Railgun.xml` with the same structure, `<Name>` and `<Script>` updated:

```xml
<Block>
	<Debug>false</Debug>
	<ID>10</ID>
	<Name>Railgun Barrel</Name>
	<Mass>0.45</Mass>
	<Script>BeyondSpiderAssembly.RailgunBarrelBlock</Script>
	<Mesh name="BS Migrated Gun Mesh">
		<Position x="0.0" y="0" z="0.0" />
		<Rotation x="90" y="0" z="0" />
		<Scale x="0.22" y="0.22" z="0.22" />
	</Mesh>
	<Texture name="BS Migrated Gun Texture" />
	<Icon>
		<Position x="0.1" y="-0.1" z="-0.4" />
		<Rotation x="45" y="225" z="0" />
		<Scale x="0.45" y="0.45" z="0.45" />
	</Icon>
	<Colliders>
		<BoxCollider>
			<Position x="0.0" y="0.0" z="0.2" />
			<Rotation x="0" y="0" z="0" />
			<Scale x="0.35" y="0.35" z="0.5" />
		</BoxCollider>
	</Colliders>
	<BasePoint hasAddingPoint="false">
		<Stickiness enabled="true" radius="0.3" />
		<Motion x="false" y="false" z="false" />
	</BasePoint>
	<AddingPoints />
</Block>
```

Delete the old file: `git rm BeyondSpider/SpaceGun.xml`

- [ ] **Step 2: Update `Mod.xml`'s block registration**

In `BeyondSpider/Mod.xml`, find:

```xml
		<Block path="SpaceGun.xml" />
```

replace with:

```xml
		<Block path="Railgun.xml" />
```

- [ ] **Step 3: Update the `.csproj` Compile item**

In `src/BeyondSpiderAssembly/BeyondSpiderAssembly.csproj`, find:

```xml
    <Compile Include="SpaceGunBlock.cs" />
```

replace with:

```xml
    <Compile Include="RailgunBarrelBlock.cs" />
```

- [ ] **Step 4: XML well-formedness check (stand-in for a build — no MSBuild/dotnet available)**

Run:
```bash
python3 -c "import xml.dom.minidom; xml.dom.minidom.parse('BeyondSpider/Railgun.xml'); print('Railgun.xml OK')"
python3 -c "import xml.dom.minidom; xml.dom.minidom.parse('BeyondSpider/Mod.xml'); print('Mod.xml OK')"
python3 -c "import xml.dom.minidom; xml.dom.minidom.parse('src/BeyondSpiderAssembly/BeyondSpiderAssembly.csproj'); print('csproj OK')"
```
Expected: all three print `OK` with no traceback. This only proves well-formed XML, not gameplay correctness — flag to the user that a real Unity/Besiege build is still needed to confirm it loads.

- [ ] **Step 5: Grep-verify no remaining reference to the old XML filename**

Run: `grep -rln "SpaceGun\.xml" BeyondSpider/ src/`
Expected: no output.

- [ ] **Step 6: Commit**

```bash
git add BeyondSpider/Railgun.xml BeyondSpider/Mod.xml src/BeyondSpiderAssembly/BeyondSpiderAssembly.csproj
git commit -m "chore: register Railgun.xml in place of SpaceGun.xml"
```

---

### Task 7: Update docs that name SpaceGunBlock/SpaceGun.xml

**Files:**
- Modify: `docs/agent-besiege-mod-guide.md`, `docs/space-combat-framework.md`, `docs/adr/0002-prioritize-energy-firepower-armor-chain.md`

**Interfaces:** none (docs only).

- [ ] **Step 1: `docs/agent-besiege-mod-guide.md`**

Find:

```
- `SpaceGunBlock.cs`：单零件炮与炮手（`SpaceGunnerBlock`），已从 EnergyGrid 的 Weapon bus 请求能量。
```

replace with:

```
- `RailgunBarrelBlock.cs`：电磁炮管与炮手（`SpaceGunnerBlock`），命中伤害和耗电按口径/初速的动能公式推算，已从 EnergyGrid 的 Weapon bus 请求能量；击穿特效复用 WW2-Naval 移植的 `space-perice` 资源包。
```

Find:

```
- `ShipCore.xml`、`Captain.xml`、`SuperCapacitor.xml`、`RadarPanel.xml`、`DefenseDirector.xml`、`ShieldProjector.xml`、`CiwsBlock.xml`、`HeavyNuclearMissile.xml`、`SpaceFlakTurret.xml`、`SpaceGun.xml`、`SpaceInterceptorLauncher.xml`、`SpaceGunner.xml`：当前已注册零件。
```

replace with:

```
- `ShipCore.xml`、`Captain.xml`、`SuperCapacitor.xml`、`RadarPanel.xml`、`DefenseDirector.xml`、`ShieldProjector.xml`、`CiwsBlock.xml`、`HeavyNuclearMissile.xml`、`SpaceFlakTurret.xml`、`Railgun.xml`、`SpaceInterceptorLauncher.xml`、`SpaceGunner.xml`：当前已注册零件。
```

Find (in the "推荐开发顺序" section, item 3):

```
3. 让单零件炮/炮台的命中结果实际打到 DamageRouter 并影响装甲 integrity，而不是只请求 Weapon 能量却没有可验证的装甲反馈。
```

Leave this line as-is (it's generic "单零件炮/炮台", not naming `SpaceGunBlock` specifically — no change needed).

- [ ] **Step 2: `docs/space-combat-framework.md`**

Find (in "New Blocks"):

```
**电磁炮管 / RailgunBarrel**

- Barrel-only kinetic weapon with build sliders for caliber and muzzle velocity.
- Higher caliber and speed increase damage and energy draw.
- Very high speed makes shield interception more expensive but also more likely to generate heating/slowdown effects.
- Uses gravity-affected projectile simulation adapted from WW2 `Gun`/`BulletBehaviour`.
```

replace with:

```
**电磁炮管 / RailgunBarrel** — implemented, replaces the earlier plain kinetic gun placeholder (`RailgunBarrelBlock`, block ID 10)

- Barrel-only kinetic weapon with build sliders for caliber and muzzle velocity.
- Energy cost and damage are derived from a kinetic-energy-shaped formula (∝ mass·velocity²), not a flat slider — pushing muzzle velocity up is what makes a shot expensive.
- Very high speed makes shield interception more expensive: `ShieldProjectorBlock` reacts to any `SpaceKineticRound` above a hyper-velocity threshold (2200 m/s), slowing or fully stopping it depending on available Shield-bus power.
- Zero-gravity projectile (`SpaceKineticRound`, `useGravity=false`), consistent with the rest of this mod's space-combat kinetic rounds — not gravity-affected like the original WW2 naval `Gun`/`BulletBehaviour` reference.
- Pierce/impact effect is the actual WW2-Naval `Perice` prefab (from the ported `space-perice` asset bundle) plus its ported audio clip; no explosion effect, since a railgun slug has no charge to detonate.
```

Find (in "Current priority: Energy-Firepower-Armor slice"):

```
`SpaceGunBlock`/`SpaceFlakTurretBlock` already draw from the Weapon bus; nano armor and `DamageRouter` are the missing pieces and should be built first.
```

replace with:

```
`RailgunBarrelBlock`/`SpaceFlakTurretBlock` already draw from the Weapon bus; nano armor and `DamageRouter` are the missing pieces and should be built first.
```

- [ ] **Step 3: `docs/adr/0002-prioritize-energy-firepower-armor-chain.md`**

Find:

```
`SpaceShipCore`/`EnergyGrid` allocate an Armor bus, and `SpaceGunBlock`/`SpaceFlakTurretBlock` already draw from the Weapon bus
```

replace with:

```
`SpaceShipCore`/`EnergyGrid` allocate an Armor bus, and `RailgunBarrelBlock`/`SpaceFlakTurretBlock` already draw from the Weapon bus
```

Find:

```
Near-term development builds nano armor and `DamageRouter` first, and routes `SpaceGunBlock`/`SpaceFlakTurretBlock` hits through it
```

replace with:

```
Near-term development builds nano armor and `DamageRouter` first, and routes `RailgunBarrelBlock`/`SpaceFlakTurretBlock` hits through it
```

- [ ] **Step 4: Grep-verify no doc still names the old block**

Run: `grep -rln "SpaceGunBlock\|SpaceGun\.xml" docs/`
Expected: no output.

- [ ] **Step 5: Commit**

```bash
git add docs/agent-besiege-mod-guide.md docs/space-combat-framework.md docs/adr/0002-prioritize-energy-firepower-armor-chain.md
git commit -m "docs: reflect SpaceGunBlock -> RailgunBarrelBlock replacement"
```

---

### Task 8: Final repo-wide consistency audit

**Files:** none modified — read-only verification task.

- [ ] **Step 1: Confirm zero stale references anywhere in the repo**

Run:
```bash
grep -rn "SpaceGunBlock\|SpaceGun\.xml\|BSSpaceGunFire\|BSSpaceGunFireControl\|BSSpaceGunGroup\|BSSpaceGunCaliber\|BSSpaceGunVelocity\|BSSpaceGunEnergy" --include="*.cs" --include="*.xml" --include="*.md" .
```
Expected: no output. (Note this intentionally does not match `BSSpaceGunnerActive`/`BSSpaceGunnerDefault`/`BSSpaceGunnerGroup`/`BSSpaceGunnerTolerance` — those belong to `SpaceGunnerBlock`, which is untouched.)

- [ ] **Step 2: Confirm the new names are wired everywhere expected**

Run:
```bash
grep -rln "RailgunBarrelBlock" --include="*.cs" --include="*.xml" .
```
Expected: `src/BeyondSpiderAssembly/RailgunBarrelBlock.cs`, `src/BeyondSpiderAssembly/SpaceCombatCore.cs`, `src/BeyondSpiderAssembly/BeyondSpiderAssembly.csproj` (as XML-ish text), `BeyondSpider/Railgun.xml`.

- [ ] **Step 3: Report to the user**

Summarize: what changed, that a real Unity/Besiege build is still needed to confirm in-game behavior (no toolchain available in this environment to verify compilation), and point at the two things most worth eyeballing first when they do build it: the railgun's fire/reload/energy feel at slider extremes, and whether the shield visibly reacts when a railgun round crosses the 2200 m/s threshold.

No commit for this task (read-only).
