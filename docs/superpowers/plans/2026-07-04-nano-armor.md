# Nano Armor + DamageRouter Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give every vanilla wood block on a ship a self-repairing nano-armor layer (`Integrity`) backed by a non-repairing structural layer (`StructuralValue` that weakens and eventually disconnects the block's physics joints), route existing weapon hits and heavy-missile blast damage through a new `DamageRouter`, and add a toggleable HP visualization overlay plus a HUD readout.

**Architecture:** `NanoArmorBehaviour` is a plain `MonoBehaviour` parasitically attached to vanilla `SingleWoodenBlock`/`DoubleWoodenBlock`/`Log` blocks by a new `NanoArmorController` singleton (via `Events.OnBlockInit`) — not a new placeable custom Block, matching `CONTEXT.md`'s and `策划案.md`'s explicit requirement that nano armor's carrier is the vanilla wood block. `DamageRouter` is a thin static entry point every physical-damage source calls. All of this mirrors real, shipped WW2-Naval reference code (`WoodenArmour.cs`, `CustomBlockController.cs`, `CannonWell.cs`'s joint-weakening, `AssetManager.cs`'s `ArmourVis` bundle) rather than inventing new Unity/Besiege API usage.

**Tech Stack:** C# (.NET Framework 3.5), Besiege ModLoader (`Modding`/`Modding.Blocks` namespaces), Unity (old API surface — avoid declaring explicit types for `BlockBehaviour.iJointTo`/`.jointsToMe` elements, always use `var`, matching the verified WW2 reference pattern).

## Global Constraints

- **No build toolchain available in this environment** (same as the previous railgun plan): no `dotnet`/`msbuild`/`mono`, and the `.csproj`'s `Managed/` DLL paths don't resolve here. Verification below is grep-based reference checks and manual code-trace review, not an actual compile. A real Unity/Besiege build is required to confirm this compiles and behaves correctly in-game.
- **Highest-risk assumption in this plan**: `BlockBehaviour.iJointTo` / `.jointsToMe` and `Joint.breakForce`/`.breakTorque` are used exactly as WW2-Naval's `CannonWell.cs` (`AmmoExploforce`/`WellExploForce`) uses them — real, working code in a shipped Besiege mod, but for a *different* mod's compiled assembly. If this specific API doesn't exist on the Besiege version `BeyondSpiderAssembly` actually targets, Task 2 (joint weakening) is what will fail to compile. Flag this explicitly to the user after implementation — it's the one piece that isn't independently double-checked (unlike the asset bundles below, which are confirmed byte-identical to WW2-Naval's).
- Confirmed by direct file comparison (not just code-reading): `BeyondSpider/Resources/WW2Migrated/space-armourvis.ab` is byte-identical (236,886 bytes) to WW2-Naval's `armourvis.ab`, containing a `"SingleVis"` GameObject asset (per `AssetManager.cs:128`, `SingleArmour = modAssetBundle.LoadAsset<GameObject>("SingleVis")`). The asset bundle name declared in `Mod.xml:239` is `"space-armourvis"`.
- No new `Mod.xml` entries needed — no new placeable Block, and `space-armourvis` is already declared as a resource.
- Follow existing codebase conventions: `SpaceBlock` subclasses use `SafeAwake`/`OnSimulateStart`/`OnSimulateStop`/`SimulateFixedUpdateHost`; `SingleInstance<T>` singletons (already used by `SpaceFlakTurretNet`) override a `Name` property and hook into `Mod.cs`'s `OnLoad()`.

---

### Task 1: Foundational plumbing — `ShipState.Armor` list and `ShowArmorHP` toggle

**Files:**
- Modify: `src/BeyondSpiderAssembly/SpaceCombatCore.cs` (`ShipState` class)
- Modify: `src/BeyondSpiderAssembly/SpaceShipCore.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `ShipState.Armor` (`List<NanoArmorBehaviour>`, type doesn't exist until Task 2 — this compiles fine since C# resolves types project-wide, not file-by-file), `SpaceShipCore.ShowArmorHP` (`MToggle`).

- [ ] **Step 1: Add the `Armor` list to `ShipState`**

In `src/BeyondSpiderAssembly/SpaceCombatCore.cs`, find:

```csharp
        public readonly List<RailgunBarrelBlock> Guns = new List<RailgunBarrelBlock>();
        public readonly List<SpaceInterceptorLauncherBlock> Launchers = new List<SpaceInterceptorLauncherBlock>();
```

and add a new line between them:

```csharp
        public readonly List<RailgunBarrelBlock> Guns = new List<RailgunBarrelBlock>();
        public readonly List<NanoArmorBehaviour> Armor = new List<NanoArmorBehaviour>();
        public readonly List<SpaceInterceptorLauncherBlock> Launchers = new List<SpaceInterceptorLauncherBlock>();
```

- [ ] **Step 2: Add the `ShowArmorHP` toggle to `SpaceShipCore`**

In `src/BeyondSpiderAssembly/SpaceShipCore.cs`, find:

```csharp
        public MSlider TotalEnergy;
        public MSlider ArmorShare;
        public MSlider ShieldShare;
        public MSlider WeaponShare;
```

replace with:

```csharp
        public MSlider TotalEnergy;
        public MSlider ArmorShare;
        public MSlider ShieldShare;
        public MSlider WeaponShare;
        public MToggle ShowArmorHP;
```

Then find:

```csharp
            TotalEnergy = AddSlider("Total Energy", "BSTotalEnergy", 1200f, 200f, 8000f);
            ArmorShare = AddSlider("Armor Share", "BSArmorShare", 1f, 0f, 5f);
            ShieldShare = AddSlider("Shield Share", "BSShieldShare", 2f, 0f, 5f);
            WeaponShare = AddSlider("Weapon Share", "BSWeaponShare", 2f, 0f, 5f);
```

replace with:

```csharp
            TotalEnergy = AddSlider("Total Energy", "BSTotalEnergy", 1200f, 200f, 8000f);
            ArmorShare = AddSlider("Armor Share", "BSArmorShare", 1f, 0f, 5f);
            ShieldShare = AddSlider("Shield Share", "BSShieldShare", 2f, 0f, 5f);
            WeaponShare = AddSlider("Weapon Share", "BSWeaponShare", 2f, 0f, 5f);
            ShowArmorHP = AddToggle("Show Armor HP", "BSShowArmorHP", false);
```

- [ ] **Step 3: Grep-verify both edits landed**

Run: `grep -n "Armor = new List\|ShowArmorHP" src/BeyondSpiderAssembly/SpaceCombatCore.cs src/BeyondSpiderAssembly/SpaceShipCore.cs`
Expected: one match in `SpaceCombatCore.cs` (the list declaration) and two matches in `SpaceShipCore.cs` (field declaration + `AddToggle` call).

- [ ] **Step 4: Commit**

```bash
git add src/BeyondSpiderAssembly/SpaceCombatCore.cs src/BeyondSpiderAssembly/SpaceShipCore.cs
git commit -m "feat: add ShipState.Armor list and SpaceShipCore.ShowArmorHP toggle"
```

---

### Task 2: NanoArmorController + NanoArmorBehaviour core mechanics (no visuals yet)

**Files:**
- Create: `src/BeyondSpiderAssembly/NanoArmorBlock.cs`
- Modify: `src/BeyondSpiderAssembly/Mod.cs`

**Interfaces:**
- Consumes: `ShipState.Armor` (Task 1), `SpaceCombatRegistry.FindShip`/`RegisterSubsystem`/`RemoveSubsystem` (existing), `EnergyBus.Armor`, `ship.Energy.Request` (existing).
- Produces: `NanoArmorBehaviour.Integrity` (float, public), `NanoArmorBehaviour.StructuralValue` (float, public), `NanoArmorBehaviour.ApplyPhysicalDamage(float damage)` (public method — this is what Task 3 (visual) reads and Task 4/5 call), `NanoArmorBehaviour.LaserDamageMultiplier` (public float property, unused until a laser weapon exists), `NanoArmorController` (registered as a singleton in `Mod.cs`).

- [ ] **Step 1: Create `NanoArmorBlock.cs` with the controller and behavior (mechanics only)**

Create `src/BeyondSpiderAssembly/NanoArmorBlock.cs`:

```csharp
using Modding;
using Modding.Blocks;
using UnityEngine;

namespace BeyondSpiderAssembly
{
    public class NanoArmorController : SingleInstance<NanoArmorController>
    {
        public override string Name { get { return "BeyondSpider Nano Armor Controller"; } }

        private void Awake()
        {
            Events.OnBlockInit += AddArmor;
        }

        private void AddArmor(BlockBehaviour block)
        {
            switch (block.BlockID)
            {
                case (int)BlockType.SingleWoodenBlock:
                case (int)BlockType.DoubleWoodenBlock:
                case (int)BlockType.Log:
                    if (block.gameObject.GetComponent<NanoArmorBehaviour>() == null)
                    {
                        block.gameObject.AddComponent<NanoArmorBehaviour>();
                    }
                    break;
                default:
                    break;
            }
        }
    }

    public class NanoArmorBehaviour : MonoBehaviour
    {
        private const float IntegrityPool = 420f;
        private const float StructuralPool = 620f;
        private const float RepairRatePerSecond = 0.05f;
        private const float EnergyPerRepairPoint = 4f;
        private const float HealthyJointForce = 45000f;

        public float Integrity = 1f;
        public float StructuralValue = 1f;

        private BlockBehaviour bb;
        private Rigidbody body;
        private int playerID;
        private MPTeam team;
        private ShipState ship;

        public float LaserDamageMultiplier
        {
            get { return Mathf.Clamp01(0.05f + 0.95f * (1f - Integrity)); }
        }

        private void Awake()
        {
            bb = GetComponent<BlockBehaviour>();
            body = GetComponent<Rigidbody>();
            playerID = bb.ParentMachine.PlayerID;
            team = bb.Team;
        }

        private void Start()
        {
            if (bb == null || !bb.isSimulating)
            {
                return;
            }
            ship = SpaceCombatRegistry.FindShip(playerID);
            if (ship != null)
            {
                SpaceCombatRegistry.RegisterSubsystem(playerID, this, ship.Armor);
            }
        }

        private void OnDestroy()
        {
            if (ship != null)
            {
                SpaceCombatRegistry.RemoveSubsystem(this, ship.Armor);
            }
        }

        private void FixedUpdate()
        {
            if (bb == null || !bb.isSimulating)
            {
                return;
            }

            bool hostAuthoritative = !StatMaster.isMP || !StatMaster.isClient;
            if (hostAuthoritative && ship != null && Integrity < 1f)
            {
                float wantedRepair = RepairRatePerSecond * Time.fixedDeltaTime;
                float energyNeeded = wantedRepair * IntegrityPool * EnergyPerRepairPoint;
                float ratio = ship.Energy.Request(EnergyBus.Armor, energyNeeded);
                Integrity = Mathf.Clamp01(Integrity + wantedRepair * ratio * ratio);
            }
        }

        public void ApplyPhysicalDamage(float damage)
        {
            if (damage <= 0f)
            {
                return;
            }
            if (Integrity > 0f)
            {
                Integrity = Mathf.Clamp01(Integrity - damage / IntegrityPool);
                return;
            }

            StructuralValue = Mathf.Clamp01(StructuralValue - damage / StructuralPool);
            ApplyStructuralWeakening();
        }

        private void ApplyStructuralWeakening()
        {
            if (StructuralValue >= 1f || bb == null)
            {
                return;
            }

            float scaled = Mathf.Lerp(0f, HealthyJointForce, Mathf.Clamp01(StructuralValue));
            foreach (var joint in bb.iJointTo)
            {
                if (joint == null)
                {
                    continue;
                }
                joint.breakForce = scaled;
                joint.breakTorque = scaled;
            }
            foreach (var joint in bb.jointsToMe)
            {
                if (joint == null)
                {
                    continue;
                }
                joint.breakForce = scaled;
                joint.breakTorque = scaled;
            }

            if (StructuralValue <= 0f && body != null)
            {
                body.AddExplosionForce(400f, transform.position, 3f);
            }
        }
    }
}
```

- [ ] **Step 2: Register `NanoArmorController` in `Mod.cs`**

In `src/BeyondSpiderAssembly/Mod.cs`, find:

```csharp
			Root.AddComponent<SpaceFlakTurretNet>();
```

and add a line right after it:

```csharp
			Root.AddComponent<SpaceFlakTurretNet>();
			Root.AddComponent<NanoArmorController>();
```

- [ ] **Step 3: Grep-verify the new type is referenced where expected**

Run: `grep -n "NanoArmorController\|NanoArmorBehaviour" src/BeyondSpiderAssembly/Mod.cs src/BeyondSpiderAssembly/NanoArmorBlock.cs src/BeyondSpiderAssembly/SpaceCombatCore.cs`
Expected: `Mod.cs` shows the `AddComponent<NanoArmorController>()` call; `NanoArmorBlock.cs` shows both class declarations; `SpaceCombatCore.cs` shows the `Armor` list typed to `NanoArmorBehaviour` (from Task 1).

- [ ] **Step 4: Manual trace of the repair formula (stand-in for a unit test — no test runner available)**

Trace by hand: `Integrity = 0.5`, ship's Armor bus fully supplies the request (`ratio = 1`), `Time.fixedDeltaTime = 0.02` (50 Hz): `wantedRepair = 0.05 * 0.02 = 0.001`; `Integrity += 0.001 * 1 * 1 = 0.001` → `0.501`. At `ratio = 0.5` (half power available): `Integrity += 0.001 * 0.25 = 0.00025` — a quarter of the full-power gain, matching the documented `ratio²` nonlinearity (50% power → 25% effect).

- [ ] **Step 5: Commit**

```bash
git add src/BeyondSpiderAssembly/NanoArmorBlock.cs src/BeyondSpiderAssembly/Mod.cs
git commit -m "feat: add NanoArmorController/NanoArmorBehaviour with self-repair and joint weakening"
```

---

### Task 3: Armor HP visualization overlay

**Files:**
- Modify: `src/BeyondSpiderAssembly/NanoArmorBlock.cs`

**Interfaces:**
- Consumes: `SpaceShipCore.ShowArmorHP` (Task 1), `ModResource.GetAssetBundle`/`.LoadAsset<GameObject>` (already used elsewhere, e.g. `SpaceEffectAssets` in `SpaceBallistics.cs`), `NanoArmorBehaviour.Integrity`/`.StructuralValue` (Task 2).
- Produces: nothing new consumed by later tasks — this is purely additive display logic on top of Task 2's state.

- [ ] **Step 1: Add the asset cache and visual fields/methods**

In `src/BeyondSpiderAssembly/NanoArmorBlock.cs`, add a new class after `NanoArmorController` and before `NanoArmorBehaviour`:

```csharp
    internal static class NanoArmorAssets
    {
        private static GameObject singleVisPrefab;
        private static bool loadAttempted;

        public static GameObject SingleVisPrefab
        {
            get
            {
                if (singleVisPrefab == null && !loadAttempted)
                {
                    loadAttempted = true;
                    singleVisPrefab = ModResource.GetAssetBundle("space-armourvis").LoadAsset<GameObject>("SingleVis");
                }
                return singleVisPrefab;
            }
        }
    }
```

Then, inside `NanoArmorBehaviour`, add these fields right after the existing `private ShipState ship;` field:

```csharp
        private GameObject overlay;
        private MeshRenderer overlayRenderer;
        private GameObject normalVis;
```

Add a constant alongside the other `private const float` declarations:

```csharp
        private const float MaxOverlayAlpha = 0.6f;
```

Change `Start()` from:

```csharp
        private void Start()
        {
            if (bb == null || !bb.isSimulating)
            {
                return;
            }
            ship = SpaceCombatRegistry.FindShip(playerID);
            if (ship != null)
            {
                SpaceCombatRegistry.RegisterSubsystem(playerID, this, ship.Armor);
            }
        }
```

to:

```csharp
        private void Start()
        {
            if (bb == null || !bb.isSimulating)
            {
                return;
            }
            ship = SpaceCombatRegistry.FindShip(playerID);
            if (ship != null)
            {
                SpaceCombatRegistry.RegisterSubsystem(playerID, this, ship.Armor);
            }
            InitVisual();
        }
```

Change `FixedUpdate()` from:

```csharp
        private void FixedUpdate()
        {
            if (bb == null || !bb.isSimulating)
            {
                return;
            }

            bool hostAuthoritative = !StatMaster.isMP || !StatMaster.isClient;
            if (hostAuthoritative && ship != null && Integrity < 1f)
            {
                float wantedRepair = RepairRatePerSecond * Time.fixedDeltaTime;
                float energyNeeded = wantedRepair * IntegrityPool * EnergyPerRepairPoint;
                float ratio = ship.Energy.Request(EnergyBus.Armor, energyNeeded);
                Integrity = Mathf.Clamp01(Integrity + wantedRepair * ratio * ratio);
            }
        }
```

to:

```csharp
        private void FixedUpdate()
        {
            if (bb == null || !bb.isSimulating)
            {
                return;
            }

            bool hostAuthoritative = !StatMaster.isMP || !StatMaster.isClient;
            if (hostAuthoritative && ship != null && Integrity < 1f)
            {
                float wantedRepair = RepairRatePerSecond * Time.fixedDeltaTime;
                float energyNeeded = wantedRepair * IntegrityPool * EnergyPerRepairPoint;
                float ratio = ship.Energy.Request(EnergyBus.Armor, energyNeeded);
                Integrity = Mathf.Clamp01(Integrity + wantedRepair * ratio * ratio);
            }

            UpdateVisual();
        }
```

Finally, add these two new private methods at the end of the class, right after `ApplyStructuralWeakening`:

```csharp
        private void InitVisual()
        {
            GameObject prefab = NanoArmorAssets.SingleVisPrefab;
            if (prefab == null)
            {
                return;
            }

            overlay = Instantiate(prefab, transform);
            overlay.name = "NanoArmorVis";
            overlay.transform.localRotation = Quaternion.identity;
            overlay.layer = 25;

            bool halfVisEnabled = false;
            Transform vis = transform.Find("Vis");
            Transform halfVis = vis == null ? null : vis.Find("HalfVis");
            if (halfVis != null)
            {
                MeshRenderer halfRenderer = halfVis.GetComponent<MeshRenderer>();
                halfVisEnabled = halfRenderer != null && halfRenderer.enabled;
            }

            switch (bb.BlockID)
            {
                case (int)BlockType.SingleWoodenBlock:
                    overlay.transform.localPosition = new Vector3(0f, 0f, 0.5f);
                    overlay.transform.localScale = new Vector3(0.8f, 0.8f, 1f);
                    break;
                case (int)BlockType.DoubleWoodenBlock:
                    if (halfVisEnabled)
                    {
                        overlay.transform.localPosition = new Vector3(0f, 0f, 0.5f);
                        overlay.transform.localScale = new Vector3(0.95f, 0.95f, 1f);
                    }
                    else
                    {
                        overlay.transform.localPosition = new Vector3(0f, 0f, 1f);
                        overlay.transform.localScale = new Vector3(0.95f, 0.95f, 2f);
                    }
                    break;
                case (int)BlockType.Log:
                    if (halfVisEnabled)
                    {
                        overlay.transform.localPosition = new Vector3(0f, 0f, 1f);
                        overlay.transform.localScale = new Vector3(0.95f, 0.95f, 2f);
                    }
                    else
                    {
                        overlay.transform.localPosition = new Vector3(0f, 0f, 1.5f);
                        overlay.transform.localScale = new Vector3(0.95f, 0.95f, 3f);
                    }
                    break;
                default:
                    overlay.transform.localPosition = Vector3.zero;
                    overlay.transform.localScale = Vector3.one;
                    break;
            }

            overlayRenderer = overlay.GetComponent<MeshRenderer>();
            normalVis = vis == null ? null : vis.gameObject;
            overlay.SetActive(false);
        }

        private void UpdateVisual()
        {
            if (overlay == null || overlayRenderer == null)
            {
                return;
            }

            bool show = ship != null && ship.Core != null && ship.Core.ShowArmorHP.IsActive;
            overlay.SetActive(show);
            if (normalVis != null)
            {
                normalVis.SetActive(!show);
            }

            if (!show)
            {
                return;
            }

            if (Integrity > 0f)
            {
                overlayRenderer.material.color = new Color(0f, 1f, 0f, MaxOverlayAlpha * Integrity);
            }
            else
            {
                overlayRenderer.material.color = new Color(1f, 0f, 0f, MaxOverlayAlpha * StructuralValue);
            }
        }
```

- [ ] **Step 2: Grep-verify field/method names are consistent**

Run: `grep -n "overlay\|ShowArmorHP\|MaxOverlayAlpha" src/BeyondSpiderAssembly/NanoArmorBlock.cs`
Expected: shows `overlay`/`overlayRenderer`/`normalVis` field declarations, their use in `InitVisual`/`UpdateVisual`, and the `ship.Core.ShowArmorHP.IsActive` read — confirming the visual layer only reads state, never introduces a second source of truth for `Integrity`/`StructuralValue`.

- [ ] **Step 3: Commit**

```bash
git add src/BeyondSpiderAssembly/NanoArmorBlock.cs
git commit -m "feat: add toggleable armor HP visualization overlay"
```

---

### Task 4: DamageRouter and wiring into SpaceKineticRound

**Files:**
- Create: `src/BeyondSpiderAssembly/DamageRouter.cs`
- Modify: `src/BeyondSpiderAssembly/SpaceBallistics.cs:154-166` (`SpaceKineticRound.OnCollisionEnter`)

**Interfaces:**
- Consumes: `NanoArmorBehaviour.ApplyPhysicalDamage(float)` (Task 2).
- Produces: `DamageRouter.RoutePhysicalHit(Collider, float)` (public static, returns bool) — used by Task 4 here and Task 5 could use it too, though Task 5 calls `NanoArmorBehaviour.ApplyPhysicalDamage` directly since it already has the `NanoArmorBehaviour` reference from a registry list, not a `Collider`.

- [ ] **Step 1: Create `DamageRouter.cs`**

Create `src/BeyondSpiderAssembly/DamageRouter.cs`:

```csharp
using UnityEngine;

namespace BeyondSpiderAssembly
{
    public static class DamageRouter
    {
        public static bool RoutePhysicalHit(Collider hitCollider, float damage)
        {
            if (hitCollider == null || damage <= 0f)
            {
                return false;
            }

            NanoArmorBehaviour armor = hitCollider.GetComponentInParent<NanoArmorBehaviour>();
            if (armor == null)
            {
                return false;
            }

            armor.ApplyPhysicalDamage(damage);
            return true;
        }
    }
}
```

- [ ] **Step 2: Wire it into `SpaceKineticRound.OnCollisionEnter`**

In `src/BeyondSpiderAssembly/SpaceBallistics.cs`, find:

```csharp
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
```

replace with:

```csharp
        private void OnCollisionEnter(Collision collision)
        {
            HeavyNuclearMissileBlock missile = collision.collider.GetComponentInParent<HeavyNuclearMissileBlock>();
            if (missile != null && missile.PlayerID != OwnerPlayerID)
            {
                missile.ApplyDamage(Damage);
            }
            DamageRouter.RoutePhysicalHit(collision.collider, Damage);
            if (SpawnImpactSpark)
            {
                SpaceEffectAssets.PlayPierceEffect(transform.position, Caliber);
            }
            Destroy(gameObject);
        }
```

- [ ] **Step 3: Grep-verify the call site and confirm single-hit-resolution is preserved**

Run: `grep -n "DamageRouter\|Destroy(gameObject)" src/BeyondSpiderAssembly/SpaceBallistics.cs`
Expected: `DamageRouter.RoutePhysicalHit(...)` appears once, immediately followed later in the same method by `Destroy(gameObject);` — confirming the round still destroys itself on this same collision regardless of whether armor was hit (no penetration chain introduced).

- [ ] **Step 4: Commit**

```bash
git add src/BeyondSpiderAssembly/DamageRouter.cs src/BeyondSpiderAssembly/SpaceBallistics.cs
git commit -m "feat: route kinetic round hits through DamageRouter to nano armor"
```

---

### Task 5: Heavy nuclear missile blast damage to nearby armor

**Files:**
- Modify: `src/BeyondSpiderAssembly/HeavyNuclearMissileBlock.cs`

**Interfaces:**
- Consumes: `ShipState.Armor` (Task 1), `NanoArmorBehaviour.ApplyPhysicalDamage(float)` (Task 2), `SpaceCombatRegistry.Ships` (existing `IEnumerable<ShipState>`).
- Produces: nothing new consumed by later tasks.

- [ ] **Step 1: Add the `BlastRadius` slider**

In `src/BeyondSpiderAssembly/HeavyNuclearMissileBlock.cs`, find:

```csharp
        public MKey Launch;
        public MSlider HealthSlider;
        public MSlider Thrust;
        public MSlider ThreatMassSlider;
        public MToggle AutoLaunch;
```

replace with:

```csharp
        public MKey Launch;
        public MSlider HealthSlider;
        public MSlider Thrust;
        public MSlider ThreatMassSlider;
        public MSlider BlastRadius;
        public MToggle AutoLaunch;
```

Find:

```csharp
            HealthSlider = AddSlider("Missile Health", "BSMissileHealth", 650f, 100f, 3000f);
            Thrust = AddSlider("Thrust", "BSMissileThrust", 450f, 50f, 3000f);
            ThreatMassSlider = AddSlider("Threat Mass", "BSThreatMass", 50f, 5f, 400f);
```

replace with:

```csharp
            HealthSlider = AddSlider("Missile Health", "BSMissileHealth", 650f, 100f, 3000f);
            Thrust = AddSlider("Thrust", "BSMissileThrust", 450f, 50f, 3000f);
            ThreatMassSlider = AddSlider("Threat Mass", "BSThreatMass", 50f, 5f, 400f);
            BlastRadius = AddSlider("Blast Radius", "BSMissileBlastRadius", 40f, 10f, 120f);
```

- [ ] **Step 2: Call blast damage from `Detonate` and add the scan method**

Find:

```csharp
        private void Detonate(bool hit)
        {
            SpaceCombatRegistry.UnregisterTrackable(this);
            if (Body != null)
            {
                Body.velocity *= 0.1f;
            }
            gameObject.SetActive(false);
        }
```

replace with:

```csharp
        private void Detonate(bool hit)
        {
            SpaceCombatRegistry.UnregisterTrackable(this);
            ApplyBlastDamageToArmor();
            if (Body != null)
            {
                Body.velocity *= 0.1f;
            }
            gameObject.SetActive(false);
        }

        private void ApplyBlastDamageToArmor()
        {
            float baseBlastDamage = HealthSlider.Value * 0.6f;
            foreach (ShipState blastShip in SpaceCombatRegistry.Ships)
            {
                for (int i = 0; i < blastShip.Armor.Count; i++)
                {
                    NanoArmorBehaviour armor = blastShip.Armor[i];
                    if (armor == null)
                    {
                        continue;
                    }
                    float distance = Vector3.Distance(transform.position, armor.transform.position);
                    if (distance > BlastRadius.Value)
                    {
                        continue;
                    }
                    float falloff = Mathf.Clamp01(1f - distance / BlastRadius.Value);
                    armor.ApplyPhysicalDamage(baseBlastDamage * falloff);
                }
            }
        }
```

- [ ] **Step 3: Grep-verify no team/IFF filter was accidentally added**

Run: `grep -n "ApplyBlastDamageToArmor\|Team" src/BeyondSpiderAssembly/HeavyNuclearMissileBlock.cs`
Expected: `ApplyBlastDamageToArmor` appears in `Detonate` and its own definition; no `Team` comparison inside `ApplyBlastDamageToArmor` — confirming the blast intentionally damages every ship in range including the missile's own, per the design decision.

- [ ] **Step 4: Commit**

```bash
git add src/BeyondSpiderAssembly/HeavyNuclearMissileBlock.cs
git commit -m "feat: heavy nuclear missile detonation damages nearby armor by distance"
```

---

### Task 6: HUD readout

**Files:**
- Modify: `src/BeyondSpiderAssembly/SpaceCombatCore.cs` (`SpaceCombatRuntime.OnGUI`)

**Interfaces:**
- Consumes: `ShipState.Armor` (Task 1), `NanoArmorBehaviour.Integrity`/`.StructuralValue` (Task 2).
- Produces: nothing consumed elsewhere.

- [ ] **Step 1: Add the armor line and a formatting helper**

In `src/BeyondSpiderAssembly/SpaceCombatCore.cs`, find:

```csharp
            GUILayout.Label("Tracks: " + ship.Tracks.Count + "  CIWS: " + ship.Ciws.Count + "  Shields: " + ship.Shields.Count);
```

replace with:

```csharp
            GUILayout.Label("Tracks: " + ship.Tracks.Count + "  CIWS: " + ship.Ciws.Count + "  Shields: " + ship.Shields.Count);
            GUILayout.Label("Armor blocks: " + ship.Armor.Count + "  " + FormatArmor(ship));
```

Then find:

```csharp
        private static string FormatBus(ShipState ship, EnergyBus bus)
        {
            return (ship.Energy.ChargeLevel(bus) * 100f).ToString("0") + "%";
        }
```

replace with:

```csharp
        private static string FormatBus(ShipState ship, EnergyBus bus)
        {
            return (ship.Energy.ChargeLevel(bus) * 100f).ToString("0") + "%";
        }

        private static string FormatArmor(ShipState ship)
        {
            if (ship.Armor.Count == 0)
            {
                return "Integrity n/a";
            }

            float integritySum = 0f;
            float structuralSum = 0f;
            for (int i = 0; i < ship.Armor.Count; i++)
            {
                NanoArmorBehaviour armor = ship.Armor[i];
                if (armor == null)
                {
                    continue;
                }
                integritySum += armor.Integrity;
                structuralSum += armor.StructuralValue;
            }

            float count = Mathf.Max(1, ship.Armor.Count);
            return "Integrity " + (integritySum / count * 100f).ToString("0") + "%  Structural " + (structuralSum / count * 100f).ToString("0") + "%";
        }
```

- [ ] **Step 2: Grep-verify `FormatArmor` is defined once and called once**

Run: `grep -n "FormatArmor" src/BeyondSpiderAssembly/SpaceCombatCore.cs`
Expected: exactly two matches — the call site in `OnGUI` and the method definition.

- [ ] **Step 3: Commit**

```bash
git add src/BeyondSpiderAssembly/SpaceCombatCore.cs
git commit -m "feat: show armor block count and average integrity/structural on HUD"
```

---

### Task 7: Docs update and final consistency audit

**Files:**
- Modify: `docs/agent-besiege-mod-guide.md`, `docs/space-combat-framework.md`

**Interfaces:** none (docs only).

- [ ] **Step 1: Update `docs/agent-besiege-mod-guide.md`**

Find (in "当前可用实现"):

```
能量分配已经支持 Armor bus（`SpaceShipCore.ArmorShare`、`EnergyGrid.Charge`/`Request`），但目前**没有任何东西消费 Armor bus**——仓库里还没有纳米装甲 Block/行为，也没有 `DamageRouter`。也就是说"能量-火力"这半条链路已经打通（`RailgunBarrelBlock`/`SpaceFlakTurretBlock` 都会向 `EnergyGrid` 请求 Weapon 能量），"火力-装甲"这一段命中/削弱/修复的结算是当前唯一缺失的环节。
```

replace with:

```
能量分配已经支持 Armor bus（`SpaceShipCore.ArmorShare`、`EnergyGrid.Charge`/`Request`），现在也有了真正的消费者：`NanoArmorBehaviour`（`NanoArmorBlock.cs`）挂在每个原版木头零件（`SingleWoodenBlock`/`DoubleWoodenBlock`/`Log`）上，维护 `Integrity`（自维护，从 Armor bus 按 `ratio^2` 非线性修复）和 `StructuralValue`（不自我修复，按比例削弱直到断开该零件的 joint）两段血量。`DamageRouter`（`DamageRouter.cs`）是统一入口，`RailgunBarrelBlock`/`SpaceFlakTurretBlock` 的命中和 `HeavyNuclearMissileBlock` 的爆炸都会打到这里。"能量-火力-装甲"链路已经完整闭环。
```

Find (in "推荐开发顺序"):

```
1. 实现纳米装甲 Block/行为：木头零件上挂类似 WW2 `WoodenArmour` 的组件，维护局部 integrity，并从 EnergyGrid 的 Armor bus 请求能量做自维护修复（`ratio^2` 非线性，参考策划案 50% 功率约 25% 效果）。
2. 实现 DamageRouter：统一入口，把命中事件按"护盾 → 纳米装甲 → 结构"顺序结算；先确认 `RailgunBarrelBlock`/`SpaceFlakTurretBlock` 现在命中后到底怎么处理伤害，再决定要不要接管。
3. 让单零件炮/炮台的命中结果实际打到 DamageRouter 并影响装甲 integrity，而不是只请求 Weapon 能量却没有可验证的装甲反馈。
4. 打通 HUD/Debug 显示：能量三条 bus 的分配与消耗、装甲 integrity、开火节奏，一屏内能看到"分配能量→开火→装甲掉血/修复"这条链路是否闭环。
5. 补一个最小验证场景（一门炮 + 一段纳米装甲 + 能量预算），手动改变能量分配验证伤害/削弱是否符合"功率不够时系统变差"的设计味道。
```

replace with:

```
1. ~~实现纳米装甲 Block/行为~~ 已完成：`NanoArmorBehaviour` 挂在原版木头零件上，`Integrity`/`StructuralValue` 两段血量，`ratio^2` 非线性自修复。
2. ~~实现 DamageRouter~~ 已完成：`DamageRouter.RoutePhysicalHit` 是统一入口。
3. ~~让炮的命中结果影响装甲 integrity~~ 已完成：`RailgunBarrelBlock`/`SpaceFlakTurretBlock` 经 `SpaceKineticRound.OnCollisionEnter` 打到 `DamageRouter`；`HeavyNuclearMissileBlock` 爆炸按距离打到附近所有装甲。
4. ~~打通 HUD 显示~~ 已完成：`SpaceCombatRuntime.OnGUI` 显示装甲零件数和平均 Integrity/Structural 百分比；`SpaceShipCore.ShowArmorHP` 控制场景内的绿/红半透明可视化。
5. 补一个最小验证场景（一门炮 + 一段纳米装甲 + 能量预算），手动改变能量分配验证伤害/削弱是否符合"功率不够时系统变差"的设计味道——仍待手动在 Besiege 里搭建验证，不是代码任务。
6. 结构断裂之后的后续效果（零件真的飞出去之后怎么处理、是否需要额外的连锁反应）、激光炮管与 `LaserDamageMultiplier` 的实际对接，留给后续。
```

- [ ] **Step 2: Update `docs/space-combat-framework.md`**

Find:

```
`RailgunBarrelBlock`/`SpaceFlakTurretBlock` already draw from the Weapon bus; nano armor and `DamageRouter` are the missing pieces and should be built first.
```

replace with:

```
`RailgunBarrelBlock`/`SpaceFlakTurretBlock` already draw from the Weapon bus, and nano armor/`DamageRouter` are now implemented (`NanoArmorBlock.cs`, `DamageRouter.cs`) — the energy-firepower-armor loop is closed end to end.
```

- [ ] **Step 3: Repo-wide grep for consistency**

Run:
```bash
grep -rln "NanoArmorBehaviour\|NanoArmorController\|DamageRouter" --include="*.cs" .
```
Expected: `src/BeyondSpiderAssembly/NanoArmorBlock.cs`, `src/BeyondSpiderAssembly/DamageRouter.cs`, `src/BeyondSpiderAssembly/Mod.cs`, `src/BeyondSpiderAssembly/SpaceCombatCore.cs`, `src/BeyondSpiderAssembly/SpaceBallistics.cs`, `src/BeyondSpiderAssembly/HeavyNuclearMissileBlock.cs`.

Run:
```bash
python3 -c "import xml.dom.minidom; xml.dom.minidom.parse('BeyondSpider/Mod.xml'); print('Mod.xml still OK (unchanged, no new blocks)')"
```
Expected: prints OK (this task doesn't touch `Mod.xml`, this just confirms nothing else broke it).

- [ ] **Step 4: Commit**

```bash
git add docs/agent-besiege-mod-guide.md docs/space-combat-framework.md
git commit -m "docs: mark nano armor and DamageRouter as implemented"
```

No further audit task needed beyond this — Task 7 already includes the repo-wide grep.

## Self-review

- **Spec coverage**: two-stage HP model (Task 2), self-repair formula (Task 2, traced by hand in Step 4), laser multiplier property (Task 2, included though uncalled — matches spec's explicit "included but no caller yet"), joint weakening/disconnect (Task 2), `DamageRouter` (Task 4), kinetic weapon wiring (Task 4), missile blast (Task 5), HUD (Task 6), visualization overlay with WW2-exact positioning and the confirmed opacity direction (Task 3), `ShowArmorHP` toggle location on `SpaceShipCore` (Task 1), docs (Task 7). All spec sections have a corresponding task.
- **Placeholder scan**: no TBDs; every step has complete code.
- **Type consistency**: `NanoArmorBehaviour.ApplyPhysicalDamage(float)` is defined once in Task 2 and called identically from Task 4 (`SpaceKineticRound`, via `DamageRouter`) and Task 5 (`HeavyNuclearMissileBlock`, directly) — same signature both places. `ShipState.Armor` is declared as `List<NanoArmorBehaviour>` in Task 1 and consumed with that exact type in Tasks 2, 5, and 6.
