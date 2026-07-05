# Paraboloid Shield Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace `ShieldProjectorBlock`'s sphere-distance shield check with a real paraboloid containment test that has no friend/foe filtering, drive the deceleration it applies from an explicit linear-in-speed physics model with its own standing upkeep energy cost, and add a procedural, togglable visualization of the dish.

**Architecture:** The paraboloid is defined relative to the block's own transform (vertex at the block, axis = `transform.forward`, matching the same orientation convention `RadarPanelBlock` already uses for its cone) and tested with a closed-form containment check derived from `a = r²/(4f)`. Deceleration is computed once per target per tick as an explicit `(desired deceleration, energy cost, applied deceleration)` pipeline, replacing the two targets' ad-hoc `ApplyShieldEffect` methods with a single `ApplyShieldDeceleration(Vector3, float)` signature both `SpaceKineticRound` and `HeavyNuclearMissileBlock` implement. Visualization is a procedurally generated polar-grid mesh (no ported asset needed, unlike `NanoArmorBlock`'s prefab-based overlay) rendered with an additive material, following the same `Shader.Find("Particles/Additive")` pattern `SpaceFlakTurretBlock` already uses for its glow effects.

**Tech Stack:** C# (.NET Framework 3.5), Besiege ModLoader (`Modding`/`Modding.Blocks`), Unity (old API surface).

## Global Constraints

- **No build toolchain available in this environment** (same situation as the previous nano-armor and railgun plans): no `dotnet`/`msbuild`/`mono`, and the `.csproj`'s `Managed/` DLL paths don't resolve here. Verification below is grep-based reference checks and manual code-trace review, not an actual compile. A real Unity/Besiege build is required to confirm this compiles and behaves correctly in-game.
- **No C# 7+ syntax.** Confirmed by grep (`out var`, `out Type name` inline declarations do not appear anywhere in `src/BeyondSpiderAssembly`) and by the project targeting `.NET Framework 3.5`. Every step below pre-declares `out`-style values as ordinary local variables instead of using inline `out Type name` — in fact this plan avoids `out` parameters entirely (see Task 3) rather than relying on that pattern.
- No `Mod.xml`/`ShieldProjector.xml` changes anywhere in this plan — no new placeable Block, and every new player-facing control is a slider/toggle added in code exactly like every existing block's build-mode UI (`AddSlider`/`AddToggle`).
- Existing slider keys `BSShieldRadius` and `BSShieldStrength` must keep their exact key strings and numeric ranges so old saved machines still load — only their label text and in-code meaning may change, and only new sliders/toggles get new keys.
- Follow existing codebase conventions: `SpaceBlock` subclasses use `SafeAwake`/`OnSimulateStart`/`OnSimulateStop`/`SimulateFixedUpdateHost`.

---

### Task 1: New build-mode sliders/toggle on `ShieldProjectorBlock`

**Files:**
- Modify: `src/BeyondSpiderAssembly/ShieldProjectorBlock.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `ShieldProjectorBlock.Depth` (`MSlider`), `.ColorHue` (`MSlider`), `.AlwaysVisible` (`MToggle`) — all read by Task 3 (physics, uses `Depth`) and Task 4 (visualization, uses all three).

- [ ] **Step 1: Add the new fields and sliders/toggle**

In `src/BeyondSpiderAssembly/ShieldProjectorBlock.cs`, find:

```csharp
        public MSlider Radius;
        public MSlider Strength;

        public override void SafeAwake()
        {
            base.SafeAwake();
            gameObject.name = "BeyondSpider Shield Projector";
            Radius = AddSlider("Shield Radius", "BSShieldRadius", 120f, 20f, 500f);
            Strength = AddSlider("Strength", "BSShieldStrength", 1f, 0.2f, 5f);
        }
```

replace with:

```csharp
        public MSlider Radius;
        public MSlider Depth;
        public MSlider Strength;
        public MSlider ColorHue;
        public MToggle AlwaysVisible;

        public override void SafeAwake()
        {
            base.SafeAwake();
            gameObject.name = "BeyondSpider Shield Projector";
            Radius = AddSlider("Shield Aperture Radius", "BSShieldRadius", 120f, 20f, 500f);
            Depth = AddSlider("Shield Depth", "BSShieldDepth", 70f, 15f, 250f);
            Strength = AddSlider("Strength", "BSShieldStrength", 1f, 0.2f, 5f);
            ColorHue = AddSlider("Shield Color", "BSShieldColorHue", 0.55f, 0f, 1f);
            AlwaysVisible = AddToggle("Always Show Shield", "BSShieldAlwaysVisible", false);
        }
```

Note: the `BSShieldRadius` and `BSShieldStrength` key strings and numeric ranges are untouched (only the `Radius` label text changes, from "Shield Radius" to "Shield Aperture Radius") — this is what keeps old saved machines compatible per the Global Constraints.

- [ ] **Step 2: Grep-verify the new keys exist exactly once each and old keys are untouched**

Run: `grep -n "BSShieldRadius\|BSShieldDepth\|BSShieldStrength\|BSShieldColorHue\|BSShieldAlwaysVisible" src/BeyondSpiderAssembly/ShieldProjectorBlock.cs`
Expected: each of the five key strings appears exactly once.

- [ ] **Step 3: Commit**

```bash
git add src/BeyondSpiderAssembly/ShieldProjectorBlock.cs
git commit -m "feat: add shield depth, color, and always-visible controls"
```

---

### Task 2: Replace `ApplyShieldEffect` with `ApplyShieldDeceleration` on both shield targets

**Files:**
- Modify: `src/BeyondSpiderAssembly/SpaceBallistics.cs` (`SpaceKineticRound`)
- Modify: `src/BeyondSpiderAssembly/HeavyNuclearMissileBlock.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `SpaceKineticRound.ApplyShieldDeceleration(Vector3 newVelocity, float appliedDeltaV)` and `HeavyNuclearMissileBlock.ApplyShieldDeceleration(Vector3 newVelocity, float appliedDeltaV)` — both consumed by Task 3's rewritten `ShieldProjectorBlock.SimulateFixedUpdateHost`.

- [ ] **Step 1: Replace `SpaceKineticRound.ApplyShieldEffect`**

In `src/BeyondSpiderAssembly/SpaceBallistics.cs`, find:

```csharp
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
```

replace with:

```csharp
        public void ApplyShieldDeceleration(Vector3 newVelocity, float appliedDeltaV)
        {
            if (body == null || appliedDeltaV <= 0f)
            {
                return;
            }
            body.velocity = newVelocity;
            if (newVelocity.magnitude < RoundStallSpeed)
            {
                Destroy(gameObject);
            }
        }
```

Then, in the same file, find the field block:

```csharp
        public bool SpawnImpactSpark;
        public bool UseRaycastDetection;

        private Rigidbody body;
        private float spawnTime;
```

replace with:

```csharp
        public bool SpawnImpactSpark;
        public bool UseRaycastDetection;

        private const float RoundStallSpeed = 40f;

        private Rigidbody body;
        private float spawnTime;
```

- [ ] **Step 2: Replace `HeavyNuclearMissileBlock.ApplyShieldEffect`**

In `src/BeyondSpiderAssembly/HeavyNuclearMissileBlock.cs`, find:

```csharp
        public void ApplyShieldEffect(float strength)
        {
            if (Body == null || strength <= 0f)
            {
                return;
            }
            Body.velocity *= Mathf.Clamp01(1f - 0.015f * strength);
            ApplyDamage(8f * strength * Time.fixedDeltaTime);
        }
```

replace with:

```csharp
        private const float MissileDamagePerDeltaV = 0.4f;

        public void ApplyShieldDeceleration(Vector3 newVelocity, float appliedDeltaV)
        {
            if (Body == null || appliedDeltaV <= 0f)
            {
                return;
            }
            Body.velocity = newVelocity;
            ApplyDamage(MissileDamagePerDeltaV * appliedDeltaV);
        }
```

- [ ] **Step 3: Grep-verify no more callers of the old method name exist**

Run: `grep -rn "ApplyShieldEffect" src/BeyondSpiderAssembly/`
Expected: no matches (the only two definitions, plus their two call sites in `ShieldProjectorBlock.cs`, are all being replaced — the call sites are rewritten in Task 3, so a stray `ApplyShieldEffect` reference there is expected to still exist until Task 3 lands; if this grep is run only after Task 2, two matches in `ShieldProjectorBlock.cs` are still expected and are fixed by Task 3).

Run: `grep -rn "ApplyShieldDeceleration" src/BeyondSpiderAssembly/`
Expected: two matches, one method definition each in `SpaceBallistics.cs` and `HeavyNuclearMissileBlock.cs`.

- [ ] **Step 4: Manual trace of the new methods (stand-in for a unit test — no test runner available)**

`SpaceKineticRound.ApplyShieldDeceleration`: a round moving at 3000 m/s gets `newVelocity` magnitude 2900 (`appliedDeltaV = 100`) → `body.velocity` set to that vector, `2900 >= RoundStallSpeed (40)` → not destroyed. A round already braked down to `newVelocity` magnitude 25 with `appliedDeltaV = 5` → `25 < 40` → destroyed (absorbed).

`HeavyNuclearMissileBlock.ApplyShieldDeceleration`: `appliedDeltaV = 10` → `ApplyDamage(0.4 * 10) = ApplyDamage(4)` — a small, proportional stress hit rather than the old flat per-tick damage.

- [ ] **Step 5: Commit**

```bash
git add src/BeyondSpiderAssembly/SpaceBallistics.cs src/BeyondSpiderAssembly/HeavyNuclearMissileBlock.cs
git commit -m "feat: replace ApplyShieldEffect with physics-driven ApplyShieldDeceleration"
```

---

### Task 3: Paraboloid containment, no-IFF, and the deceleration/upkeep energy model

**Files:**
- Modify: `src/BeyondSpiderAssembly/ShieldProjectorBlock.cs`

**Interfaces:**
- Consumes: `ShieldProjectorBlock.Depth`/`Radius`/`Strength` (Task 1 and pre-existing), `SpaceKineticRound.ApplyShieldDeceleration`/`HeavyNuclearMissileBlock.ApplyShieldDeceleration` (Task 2), `ShipState.Energy.Request` (existing).
- Produces: `ShieldProjectorBlock.Contains(Vector3)` (private, used by this task only), `.Decelerate(...)` (private, used by this task only), `ShieldProjectorBlock.lastUpkeepRatio` (private field) and `.interceptedThisTick` (private field) — both consumed by Task 4's visualization update.

- [ ] **Step 1: Add the new constants and fields**

In `src/BeyondSpiderAssembly/ShieldProjectorBlock.cs`, find:

```csharp
    public class ShieldProjectorBlock : SpaceBlock
    {
        private const float HyperVelocityThreshold = 2200f;

        public MSlider Radius;
```

replace with:

```csharp
    public class ShieldProjectorBlock : SpaceBlock
    {
        private const float HyperVelocityThreshold = 2200f;
        private const float DecelCoefficient = 0.02f;
        private const float EnergyPerDeltaV = 0.6f;
        private const float UpkeepBase = 6f;
        private const float UpkeepPerVolume = 0.02f;

        private float lastUpkeepRatio;
        private bool interceptedThisTick;

        public MSlider Radius;
```

- [ ] **Step 2: Replace `SimulateFixedUpdateHost`**

Find:

```csharp
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
```

replace with:

```csharp
        public override void SimulateFixedUpdateHost()
        {
            ShipState ship = OwnShip();
            if (ship == null)
            {
                return;
            }

            float upkeepCost = UpkeepBase + UpkeepPerVolume * Radius.Value * Depth.Value;
            lastUpkeepRatio = ship.Energy.Request(EnergyBus.Shield, upkeepCost * Time.fixedDeltaTime);
            float effectiveStrength = Strength.Value * lastUpkeepRatio;
            interceptedThisTick = false;

            IList<ITrackable> targets = SpaceCombatRegistry.Trackables;
            for (int i = 0; i < targets.Count; i++)
            {
                HeavyNuclearMissileBlock missile = targets[i] as HeavyNuclearMissileBlock;
                if (missile != null)
                {
                    if (!missile.IsAlive || !Contains(missile.Position))
                    {
                        continue;
                    }

                    float missileDeltaV = Decelerate(ship, effectiveStrength, missile.Velocity, missile.ThreatMass);
                    if (missileDeltaV > 0f)
                    {
                        float missileSpeed = missile.Velocity.magnitude;
                        Vector3 missileNewVelocity = missile.Velocity * ((missileSpeed - missileDeltaV) / missileSpeed);
                        missile.ApplyShieldDeceleration(missileNewVelocity, missileDeltaV);
                        interceptedThisTick = true;
                    }
                    continue;
                }

                SpaceKineticRound round = targets[i] as SpaceKineticRound;
                if (round != null)
                {
                    if (!round.IsAlive || round.Velocity.magnitude <= HyperVelocityThreshold || !Contains(round.Position))
                    {
                        continue;
                    }

                    float roundDeltaV = Decelerate(ship, effectiveStrength, round.Velocity, round.MassEstimate);
                    if (roundDeltaV > 0f)
                    {
                        float roundSpeed = round.Velocity.magnitude;
                        Vector3 roundNewVelocity = round.Velocity * ((roundSpeed - roundDeltaV) / roundSpeed);
                        round.ApplyShieldDeceleration(roundNewVelocity, roundDeltaV);
                        interceptedThisTick = true;
                    }
                }
            }
        }

        private bool Contains(Vector3 position)
        {
            Vector3 delta = position - transform.position;
            float a = Vector3.Dot(delta, transform.forward);
            if (a < 0f || a > Depth.Value)
            {
                return false;
            }
            float r2 = delta.sqrMagnitude - a * a;
            float maxR2 = Radius.Value * Radius.Value * a / Depth.Value;
            return r2 <= maxR2;
        }

        private float Decelerate(ShipState ship, float effectiveStrength, Vector3 velocity, float mass)
        {
            float speed = velocity.magnitude;
            if (speed <= 0f)
            {
                return 0f;
            }

            float decelAccel = DecelCoefficient * effectiveStrength * speed;
            float desiredDeltaV = Mathf.Min(speed, decelAccel * Time.fixedDeltaTime);
            if (desiredDeltaV <= 0f)
            {
                return 0f;
            }

            float energyCost = EnergyPerDeltaV * mass * speed * desiredDeltaV;
            float ratio = ship.Energy.Request(EnergyBus.Shield, energyCost);
            return desiredDeltaV * ratio;
        }
```

- [ ] **Step 3: Grep-verify the team check and old method name are gone**

Run: `grep -n "Team == Team\|ApplyShieldEffect\|Vector3.Distance" src/BeyondSpiderAssembly/ShieldProjectorBlock.cs`
Expected: no matches — confirms both IFF skips are removed, both call sites now use `ApplyShieldDeceleration`, and the sphere-distance check is gone.

Run: `grep -rn "ApplyShieldEffect" src/BeyondSpiderAssembly/`
Expected: no matches anywhere in the assembly now (this closes out the loose end flagged in Task 2 Step 3).

- [ ] **Step 4: Manual trace of `Contains` and `Decelerate` (stand-in for a unit test — no test runner available)**

`Contains`, with `Radius.Value = 120`, `Depth.Value = 70`, block at world origin facing `+Z` (`transform.forward = (0,0,1)`):
- Point `(0, 0, 0)` → `a = 0`, `r2 = 0`, `maxR2 = 120*120*0/70 = 0` → `0 <= 0` → **inside** (the vertex itself).
- Point `(0, 0, -5)` → `a = -5 < 0` → **outside** (behind the block).
- Point `(0, 0, 100)` → `a = 100 > 70 (Depth)` → **outside** (past the far cap).
- Point `(0, 120, 70)` → `a = 70`, `r2 = 120*120 = 14400`, `maxR2 = 120*120*70/70 = 14400` → `14400 <= 14400` → **inside** (exactly on the rim at max depth, matching the spec's "aperture radius at max depth" framing).
- Point `(0, 130, 70)` → `r2 = 16900 > 14400` → **outside** (past the rim).

`Decelerate`, with `effectiveStrength = 1`, `velocity` magnitude `3000`, `mass = 50`, `Time.fixedDeltaTime = 0.02`, and the ship's Shield bus fully able to supply the request (`ratio = 1`):
- `decelAccel = 0.02 * 1 * 3000 = 60`
- `desiredDeltaV = min(3000, 60 * 0.02) = 1.2`
- `energyCost = 0.6 * 50 * 3000 * 1.2 = 108000`
- returns `1.2 * 1 = 1.2` — a small per-tick speed reduction, matching the intent that this compounds continuously while the target stays inside the field rather than resolving in one tick.

- [ ] **Step 5: Commit**

```bash
git add src/BeyondSpiderAssembly/ShieldProjectorBlock.cs
git commit -m "feat: paraboloid containment, no-IFF, and upkeep/deceleration energy model"
```

---

### Task 4: Procedural dish visualization with always-visible toggle and intercept flash

**Files:**
- Modify: `src/BeyondSpiderAssembly/ShieldProjectorBlock.cs`

**Interfaces:**
- Consumes: `ShieldProjectorBlock.AlwaysVisible`/`ColorHue` (Task 1), `.lastUpkeepRatio`/`.interceptedThisTick` (Task 3).
- Produces: nothing consumed by later tasks (this is the last code task).

- [ ] **Step 1: Add visualization constants and fields**

Find:

```csharp
        private const float HyperVelocityThreshold = 2200f;
        private const float DecelCoefficient = 0.02f;
        private const float EnergyPerDeltaV = 0.6f;
        private const float UpkeepBase = 6f;
        private const float UpkeepPerVolume = 0.02f;

        private float lastUpkeepRatio;
        private bool interceptedThisTick;

        public MSlider Radius;
```

replace with:

```csharp
        private const float HyperVelocityThreshold = 2200f;
        private const float DecelCoefficient = 0.02f;
        private const float EnergyPerDeltaV = 0.6f;
        private const float UpkeepBase = 6f;
        private const float UpkeepPerVolume = 0.02f;
        private const int RingCount = 10;
        private const int SegmentCount = 32;
        private const float BaseAlpha = 0.12f;
        private const float FlashPeakAlpha = 0.35f;
        private const float FlashDecayTime = 0.25f;

        private float lastUpkeepRatio;
        private bool interceptedThisTick;

        private GameObject visObject;
        private MeshFilter visMeshFilter;
        private MeshRenderer visRenderer;
        private float builtRadius = -1f;
        private float builtDepth = -1f;
        private float flashLevel;

        public MSlider Radius;
```

- [ ] **Step 2: Create/destroy the visualization object with the block's lifecycle**

Find:

```csharp
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
```

replace with:

```csharp
        public override void OnSimulateStart()
        {
            base.OnSimulateStart();
            ShipState ship = OwnShip();
            if (ship != null)
            {
                SpaceCombatRegistry.RegisterSubsystem(PlayerID, this, ship.Shields);
            }
            InitVisual();
        }

        public override void OnSimulateStop()
        {
            ShipState ship = OwnShip();
            if (ship != null)
            {
                SpaceCombatRegistry.RemoveSubsystem(this, ship.Shields);
            }
            if (visObject != null)
            {
                Destroy(visObject);
                visObject = null;
            }
        }
```

- [ ] **Step 3: Call `UpdateVisual` at the end of the fixed-update tick**

Find (this is the end of the method Task 3 just rewrote):

```csharp
                    if (roundDeltaV > 0f)
                    {
                        float roundSpeed = round.Velocity.magnitude;
                        Vector3 roundNewVelocity = round.Velocity * ((roundSpeed - roundDeltaV) / roundSpeed);
                        round.ApplyShieldDeceleration(roundNewVelocity, roundDeltaV);
                        interceptedThisTick = true;
                    }
                }
            }
        }

        private bool Contains(Vector3 position)
```

replace with:

```csharp
                    if (roundDeltaV > 0f)
                    {
                        float roundSpeed = round.Velocity.magnitude;
                        Vector3 roundNewVelocity = round.Velocity * ((roundSpeed - roundDeltaV) / roundSpeed);
                        round.ApplyShieldDeceleration(roundNewVelocity, roundDeltaV);
                        interceptedThisTick = true;
                    }
                }
            }

            UpdateVisual();
        }

        private bool Contains(Vector3 position)
```

- [ ] **Step 4: Add the mesh-building and visual-update methods**

Add these new methods at the end of the class, right after the existing `Decelerate` method (i.e., just before the final closing braces of the class and namespace):

```csharp
        private void InitVisual()
        {
            visObject = new GameObject("BS Shield Field Vis");
            visObject.transform.SetParent(transform);
            visObject.transform.localPosition = Vector3.zero;
            visObject.transform.localRotation = Quaternion.identity;
            visObject.transform.localScale = Vector3.one;
            visMeshFilter = visObject.AddComponent<MeshFilter>();
            visRenderer = visObject.AddComponent<MeshRenderer>();
            visRenderer.material = new Material(Shader.Find("Particles/Additive"));
            visRenderer.enabled = false;
            RebuildMesh();
        }

        private void RebuildMesh()
        {
            builtRadius = Radius.Value;
            builtDepth = Depth.Value;

            List<Vector3> vertices = new List<Vector3>();
            List<int> triangles = new List<int>();

            for (int ring = 0; ring <= RingCount; ring++)
            {
                float a = builtDepth * ring / RingCount;
                float r = builtRadius * Mathf.Sqrt(a / builtDepth);
                for (int seg = 0; seg < SegmentCount; seg++)
                {
                    float angle = seg * Mathf.PI * 2f / SegmentCount;
                    vertices.Add(new Vector3(r * Mathf.Cos(angle), r * Mathf.Sin(angle), a));
                }
            }

            for (int ring = 0; ring < RingCount; ring++)
            {
                for (int seg = 0; seg < SegmentCount; seg++)
                {
                    int nextSeg = (seg + 1) % SegmentCount;
                    int a0 = ring * SegmentCount + seg;
                    int a1 = ring * SegmentCount + nextSeg;
                    int b0 = (ring + 1) * SegmentCount + seg;
                    int b1 = (ring + 1) * SegmentCount + nextSeg;

                    AddQuad(triangles, a0, a1, b1, b0);
                }
            }

            Mesh mesh = new Mesh();
            mesh.vertices = vertices.ToArray();
            mesh.triangles = triangles.ToArray();
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            visMeshFilter.sharedMesh = mesh;
        }

        private static void AddQuad(List<int> triangles, int a, int b, int c, int d)
        {
            triangles.Add(a);
            triangles.Add(b);
            triangles.Add(c);
            triangles.Add(a);
            triangles.Add(c);
            triangles.Add(d);
            triangles.Add(a);
            triangles.Add(c);
            triangles.Add(b);
            triangles.Add(a);
            triangles.Add(d);
            triangles.Add(c);
        }

        private void UpdateVisual()
        {
            if (visRenderer == null)
            {
                return;
            }

            if (Mathf.Abs(Radius.Value - builtRadius) > 0.01f || Mathf.Abs(Depth.Value - builtDepth) > 0.01f)
            {
                RebuildMesh();
            }

            flashLevel = Mathf.Max(0f, flashLevel - Time.fixedDeltaTime / FlashDecayTime);
            if (interceptedThisTick)
            {
                flashLevel = 1f;
            }

            float baseAlpha = AlwaysVisible.IsActive ? BaseAlpha * lastUpkeepRatio : 0f;
            float intensity = Mathf.Clamp01(baseAlpha + FlashPeakAlpha * flashLevel);
            Color hueColor = Color.HSVToRGB(ColorHue.Value, 1f, 1f);
            visRenderer.material.color = hueColor * intensity;
            visRenderer.enabled = intensity > 0.001f;
        }
```

- [ ] **Step 5: Grep-verify the visualization is wired into both lifecycle methods and the tick**

Run: `grep -n "InitVisual\|UpdateVisual\|visObject" src/BeyondSpiderAssembly/ShieldProjectorBlock.cs`
Expected: `InitVisual()` called once in `OnSimulateStart`, `visObject` destroyed once in `OnSimulateStop`, `UpdateVisual()` called once at the end of `SimulateFixedUpdateHost`, plus the method definitions themselves.

- [ ] **Step 6: Manual trace of `UpdateVisual` brightness (stand-in for a unit test — no test runner available)**

`AlwaysVisible.IsActive = false`, no interception this tick, `flashLevel` already at 0 → `baseAlpha = 0`, `intensity = 0` → `visRenderer.enabled = false` (fully hidden, matches the "off" mode).

`AlwaysVisible.IsActive = false`, `interceptedThisTick = true` this tick → `flashLevel` set to `1` → `intensity = 0 + 0.35 * 1 = 0.35` → briefly visible, then decays back toward 0 over `FlashDecayTime = 0.25` seconds as subsequent ticks reduce `flashLevel` (matches "flash-only" mode).

`AlwaysVisible.IsActive = true`, `lastUpkeepRatio = 0.5` (shield half-starved), no interception → `baseAlpha = 0.12 * 0.5 = 0.06`, `intensity = 0.06` → dim but visible, and dimmer than a fully-powered shield (`lastUpkeepRatio = 1` → `intensity = 0.12`), matching the "starved shield looks weaker" requirement from the spec.

- [ ] **Step 7: Commit**

```bash
git add src/BeyondSpiderAssembly/ShieldProjectorBlock.cs
git commit -m "feat: add procedural paraboloid shield visualization with intercept flash"
```

---

### Task 5: Docs update and final consistency audit

**Files:**
- Modify: `docs/space-combat-framework.md`
- Modify: `docs/agent-besiege-mod-guide.md`

**Interfaces:** none (docs only).

- [ ] **Step 1: Update the `ShieldProjector` section in `docs/space-combat-framework.md`**

Find:

```
**护盾发生器 / ShieldProjector**

- Creates a configurable paraboloid electromagnetic field.
- Consumes shield energy when an eligible physical projectile intersects the field.
- Can fully intercept, partially slow, or fail based on projectile speed, mass/caliber, shield allocation, and available capacitor power.
- Does not affect laser beams.
```

replace with:

```
**护盾发生器 / ShieldProjector**

- Creates a configurable paraboloid electromagnetic field: narrow at the projector, flaring out to a build-configured aperture radius over a build-configured depth along the block's facing direction.
- Has no IFF — every eligible high-speed projectile inside the field is slowed, including the owning ship's own kinetic rounds and missiles.
- Maintaining the field costs standing shield energy on its own (scaling with field volume), on top of the extra per-target energy spent while actively decelerating something; both draws degrade the field's effectiveness gracefully with available power rather than switching it off.
- Consumes shield energy when an eligible physical projectile intersects the field.
- Can fully intercept, partially slow, or fail based on projectile speed, mass/caliber, shield allocation, and available capacitor power.
- Does not affect laser beams.
```

- [ ] **Step 2: Update the "Shield vs kinetic" section in `docs/space-combat-framework.md`**

Find:

```
**Shield vs kinetic**

- Eligible: railgun slugs, large shells, missiles.
- Ineligible: laser beams.
```

replace with:

```
**Shield vs kinetic**

- Eligible: railgun slugs, large shells, missiles — from any team, including the shield's own ship. The field has no IFF filtering.
- Ineligible: laser beams.
```

- [ ] **Step 3: Update `docs/agent-besiege-mod-guide.md`'s implementation-inventory line for the shield**

Find:

```
- `ShieldProjectorBlock.cs`：电磁场护盾，拦截高速实体威胁。
```

replace with:

```
- `ShieldProjectorBlock.cs`：电磁场护盾，用抛物面（而非球形）容纳判定拦截高速实体威胁，无敌我识别，护盾维持自身也要耗电，含可视化。
```

- [ ] **Step 4: Update roadmap item 11 in `docs/agent-besiege-mod-guide.md`**

Find:

```
11. 强化护盾：从球形距离检测过渡到可配置抛物面/扇面场。
```

replace with:

```
11. ~~强化护盾：从球形距离检测过渡到可配置抛物面/扇面场~~ 已完成抛物面部分：`ShieldProjectorBlock` 现在用真正的抛物面容纳判定（`Radius`/`Depth` 两个滑条），去掉了敌我识别（对本方发射物同样生效），新增护盾维持本身的电量开销，以及可选常显/拦截闪烁的护盾罩可视化。扇面场变体仍留待后续。
```

- [ ] **Step 5: Add the no-IFF rule to 防御交互规则 in `docs/agent-besiege-mod-guide.md`**

Find:

```
- 电磁场护盾影响实体高速威胁：电磁炮弹、大型炮弹、导弹。
- 激光不被护盾拦截。
```

replace with:

```
- 电磁场护盾影响实体高速威胁：电磁炮弹、大型炮弹、导弹。
- 电磁场护盾没有敌我识别：本方发射的高速炮弹和导弹进入抛物面同样会被减速，不做阵营过滤。
- 激光不被护盾拦截。
```

- [ ] **Step 6: Repo-wide grep for consistency**

Run:
```bash
grep -rn "ApplyShieldEffect\|Team == Team" src/BeyondSpiderAssembly/ShieldProjectorBlock.cs src/BeyondSpiderAssembly/SpaceBallistics.cs src/BeyondSpiderAssembly/HeavyNuclearMissileBlock.cs
```
Expected: no matches — confirms the rename and IFF removal are complete across all three files touched by this plan.

Run:
```bash
grep -c "ApplyShieldDeceleration" src/BeyondSpiderAssembly/ShieldProjectorBlock.cs src/BeyondSpiderAssembly/SpaceBallistics.cs src/BeyondSpiderAssembly/HeavyNuclearMissileBlock.cs
```
Expected: `ShieldProjectorBlock.cs:2` (both call sites), `SpaceBallistics.cs:1` (definition), `HeavyNuclearMissileBlock.cs:1` (definition).

Run:
```bash
python3 -c "import xml.dom.minidom; xml.dom.minidom.parse('BeyondSpider/Mod.xml'); print('Mod.xml still OK (unchanged, no new blocks)')"
```
Expected: prints OK (this plan never touches `Mod.xml`; this just confirms nothing else broke it).

- [ ] **Step 7: Commit**

```bash
git add docs/space-combat-framework.md docs/agent-besiege-mod-guide.md
git commit -m "docs: mark paraboloid shield rework as implemented"
```

## Self-review

- **Spec coverage**: paraboloid shape/orientation (Task 3, `Contains`), no IFF (Task 3, removed `Team == Team` in both branches), linear-in-speed deceleration (Task 3, `Decelerate`), per-target energy cost (Task 3, `Decelerate`'s `energyCost`), standing upkeep cost (Task 3, `upkeepCost`/`lastUpkeepRatio`), round-stall-destroy and missile deceleration-damage (Task 2), always-visible toggle + color slider + flash-on-intercept (Task 1 controls, Task 4 behavior), depth slider and reused radius/strength keys for save compatibility (Task 1), docs (Task 5). All spec sections have a corresponding task.
- **Placeholder scan**: no TBDs; every step has complete code and concrete constants.
- **Type consistency**: `ApplyShieldDeceleration(Vector3 newVelocity, float appliedDeltaV)` is defined identically in Task 2 for both `SpaceKineticRound` and `HeavyNuclearMissileBlock`, and called with that exact signature from Task 3's `SimulateFixedUpdateHost`. `Decelerate(ShipState, float, Vector3, float)` and `Contains(Vector3)` are defined in Task 3 and only ever called from within the same class (Task 3's own loop, Task 4 never calls them). `lastUpkeepRatio`/`interceptedThisTick` are declared in Task 3 and consumed by Task 4's `UpdateVisual` with the same names and types.
