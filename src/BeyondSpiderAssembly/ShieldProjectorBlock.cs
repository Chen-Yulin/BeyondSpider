using System.Collections.Generic;
using UnityEngine;

namespace BeyondSpiderAssembly
{
    public class ShieldProjectorBlock : SpaceBlock
    {
        // The relative-speed gate (m/s): a round/missile is bled only while its speed RELATIVE to
        // this shield generator exceeds this, and only down to this same (absolute) floor. See
        // docs/adr/0006-relative-velocity-after-inheritance.md.
        private const float HyperVelocityThreshold = 600f;
        // How hard the field bites per tick — only sets how many ticks it takes to bleed a round
        // down to the threshold, not the total energy (that is priced by velocity-damage removed,
        // see Decelerate). Big enough that most rounds reach the threshold within a tick or two.
        private const float DecelCoefficient = 80f;
        private const float UpkeepBase = 6f;
        private const float UpkeepPerVolume = 0.02f;
        private const int RingCount = 10;
        private const int SegmentCount = 32;
        // Idle glow tracks how much of its requested upkeep the shield is actually getting (see
        // lastUpkeepRatio) — a starved bus visibly dims the field instead of an all-or-nothing cutoff.
        private const float MinIdleAlpha = 0.05f;
        private const float MaxIdleAlpha = 0.2f;
        private const float FlashPeakAlpha = 0.35f;
        private const float FlashDecayTime = 0.25f;

        private float lastUpkeepRatio;
        private bool interceptedThisTick;
        private bool active = true;
        // See agent-besiege-mod-guide.md's "注册时序规范" — retried each tick until it
        // succeeds, since ShipState may not exist yet when this OnSimulateStart runs.
        private bool registered;

        private GameObject visObject;
        private MeshFilter visMeshFilter;
        private MeshRenderer visRenderer;
        private float builtRadius = -1f;
        private float builtDepth = -1f;
        private float builtHeight = -1f;
        private float flashLevel;

        public MSlider Radius;
        public MSlider Depth;
        public MSlider Strength;
        public MSlider ColorHue;
        public MSlider Height;
        public MToggle AlwaysVisible;
        public MKey PowerToggle;

        public override void SafeAwake()
        {
            base.SafeAwake();
            gameObject.name = "BeyondSpider Shield Projector";
            Radius = AddSlider("Shield Aperture Radius", "BSShieldRadius", 120f, 20f, 500f);
            Depth = AddSlider("Shield Depth", "BSShieldDepth", 70f, 15f, 250f);
            Height = AddSlider("Shield Height", "BSShieldHeight", 70f, 15f, 250f);
            Strength = AddSlider("Strength", "BSShieldStrength", 1f, 0.2f, 5f);
            ColorHue = AddSlider("Shield Color", "BSShieldColorHue", 0.55f, 0f, 1f);
            AlwaysVisible = AddToggle("Always Show Shield", "BSShieldAlwaysVisible", false);
            PowerToggle = AddKey("Toggle Shield Power", "BSShieldPower", KeyCode.None);
        }

        public override void OnSimulateStart()
        {
            base.OnSimulateStart();
            ShipState ship = OwnShip();
            if (ship != null)
            {
                OnAssignedToShip(ship);
            }
            InitVisual();
        }

        public override void OnAssignedToShip(ShipState ship)
        {
            SpaceCombatRegistry.RegisterSubsystem(PlayerID, this, ship.Shields);
            registered = true;
        }

        public override void OnSimulateStop()
        {
            ShipState ship = OwnShip();
            if (ship != null)
            {
                SpaceCombatRegistry.RemoveSubsystem(this, ship.Shields);
            }
            registered = false;
            if (visObject != null)
            {
                Destroy(visObject);
                visObject = null;
            }
        }

        // Key is read here (SimulateUpdateHost, not the fixed step) so a tap in between fixed ticks
        // isn't missed — same convention as the other MKey toggles in this codebase.
        public override void SimulateUpdateHost()
        {
            if (PowerToggle.IsPressed)
            {
                active = !active;
            }
        }

        public override void SimulateFixedUpdateHost()
        {
            ShipState ship = OwnShip();
            if (ship != null && !registered)
            {
                SpaceCombatRegistry.RegisterSubsystem(PlayerID, this, ship.Shields);
                registered = true;
            }
            if (ship == null)
            {
                return;
            }

            if (!active)
            {
                lastUpkeepRatio = 0f;
                interceptedThisTick = false;
                UpdateVisual();
                return;
            }

            float upkeepCost = UpkeepBase + UpkeepPerVolume * Radius.Value * Depth.Value;
            lastUpkeepRatio = ship.Energy.Request(EnergyBus.Shield, upkeepCost * Time.fixedDeltaTime);
            float effectiveStrength = Strength.Value * lastUpkeepRatio;
            interceptedThisTick = false;
            // Reference for the relative-speed gate below: the shield generator's own motion
            // (≈ ship velocity). Gate on speed relative to this, but decelerate absolute speed toward
            // the absolute HyperVelocityThreshold floor — see ADR 0006.
            Vector3 shieldVelocity = Body != null ? Body.velocity : Vector3.zero;

            IList<ITrackable> targets = SpaceCombatRegistry.Trackables;
            for (int i = 0; i < targets.Count; i++)
            {
                MissileProjectile missile = targets[i] as MissileProjectile;
                if (missile != null)
                {
                    // Check current position (keeps decelerating every tick the missile is actually
                    // inside) and the next-tick predicted position (catches fast entries the same
                    // tick they'd otherwise cross the field), so slow-moving intercepts aren't missed
                    // either way. The speed gate is the missile's speed RELATIVE to the shield
                    // generator (ADR 0006): like a round it's bled only while closing faster than the
                    // threshold, and only down to that same floor — the field no longer stops it dead.
                    Vector3 missilePredictedPos = missile.Position + missile.Velocity * Time.fixedDeltaTime;
                    if (!missile.IsAlive || (missile.Velocity - shieldVelocity).magnitude <= HyperVelocityThreshold ||
                        (!Contains(missile.Position) && !Contains(missilePredictedPos)))
                    {
                        continue;
                    }

                    float missileDeltaV = Decelerate(ship, effectiveStrength, missile.Velocity, missile.ThreatMass, HyperVelocityThreshold);
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
                    // Eligible only while the round's speed RELATIVE to the shield generator exceeds
                    // HyperVelocityThreshold (ADR 0006); the field caps that closing speed at the
                    // threshold, it doesn't fully stop the round.
                    Vector3 roundPredictedPos = round.Position + round.Velocity * Time.fixedDeltaTime;
                    if (!round.IsAlive || (round.Velocity - shieldVelocity).magnitude <= HyperVelocityThreshold ||
                        (!Contains(round.Position) && !Contains(roundPredictedPos)))
                    {
                        continue;
                    }

                    float roundDeltaV = Decelerate(ship, effectiveStrength, round.Velocity, round.MassEstimate, HyperVelocityThreshold);
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
        {
            Vector3 delta = position - (transform.position + Height.Value * transform.forward);
            float a = Vector3.Dot(delta, -transform.forward);
            if (a < 0f || a > Depth.Value)
            {
                return false;
            }
            float r2 = delta.sqrMagnitude - a * a;
            float maxR2 = Radius.Value * Radius.Value * a / Depth.Value;
            return r2 <= maxR2;
        }

        // minSpeed is the ABSOLUTE speed this threat may be bled down to but not past in one tick —
        // now the HyperVelocityThreshold for both kinetic rounds and missiles (ADR 0006). The field
        // shaves the hyper-velocity off down to the threshold and lets the round/missile reach the
        // hull for its reduced velocity-damage rather than deleting it, matching "slow/weaken, don't
        // necessarily fully block". The eligibility gate (see the caller) is on RELATIVE speed while
        // this floor is absolute, so a threat whose absolute speed is already below the floor is a
        // no-op here even if its closing speed cleared the gate.
        private float Decelerate(ShipState ship, float effectiveStrength, Vector3 velocity, float mass, float minSpeed)
        {
            mass = Mathf.Max(0.05f, mass);
            float speed = velocity.magnitude;
            float slowable = speed - minSpeed;
            if (slowable <= 0f)
            {
                return 0f;
            }

            float decelAccel = DecelCoefficient * effectiveStrength * speed;
            float desiredDeltaV = Mathf.Min(slowable, decelAccel * Time.fixedDeltaTime);
            if (desiredDeltaV <= 0f)
            {
                return 0f;
            }

            // Price the deceleration on the Shield bus by the velocity-damage it removes from the
            // round, using the exact kinetic-damage coefficient the weapon paid to put there —
            // see SpaceBalance. vAfter = speed - desiredDeltaV, so the bracket is the (v_before^2 -
            // v_after^2) worth of potential damage the round no longer carries. This makes a
            // hyper-velocity intercept a genuine capacitor-draining burst (block enough per second
            // and the Shield bus can't keep up — the round leaks through), instead of the near-free
            // deletion the old flat deltaV cost allowed.
            float vAfter = speed - desiredDeltaV;
            float damageRemoved = mass * (speed * speed - vAfter * vAfter) * SpaceBalance.KineticDamagePerKE;
            float energyCost = SpaceBalance.ShieldEnergyPerDamage * damageRemoved;
            float ratio = ship.Energy.Request(EnergyBus.Shield, energyCost);
            return desiredDeltaV * ratio;
        }

        private void InitVisual()
        {
            visObject = new GameObject("BS Shield Field Vis");
            visObject.transform.SetParent(transform);
            visObject.transform.localPosition = Vector3.zero;
            // Mesh is generated along local +Z (see RebuildMesh); rotate 180° so it points along
            // -transform.forward in world space, matching Contains's flipped axis above.
            visObject.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
            visObject.transform.localScale = Vector3.one;
            visMeshFilter = visObject.AddComponent<MeshFilter>();
            visRenderer = visObject.AddComponent<MeshRenderer>();
            visRenderer.material = new Material(Shader.Find("Particles/Additive"));
            visRenderer.enabled = false;
            RebuildMesh();
            PlaceMesh();
        }
        private void PlaceMesh()
        {
            visObject.transform.localPosition = new Vector3(0f, 0f, Height.Value);
            builtHeight = Height.Value;
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
            // Both winding orders share vertices, so normals average toward zero here — harmless
            // under the unlit Particles/Additive material, but would render black under a lit shader.
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

            if (Mathf.Abs(Radius.Value - builtRadius) > 0.1f || Mathf.Abs(Depth.Value - builtDepth) > 0.1f)
            {
                RebuildMesh();
            }
            if (Mathf.Abs(Height.Value - builtHeight) > 0.1f)
            {
                PlaceMesh();
            }

            flashLevel = Mathf.Max(0f, flashLevel - Time.fixedDeltaTime / FlashDecayTime);
            if (interceptedThisTick)
            {
                flashLevel = 1f;
            }

            if (!active)
            {
                flashLevel = 0f;
                visRenderer.enabled = false;
                return;
            }

            float baseAlpha = AlwaysVisible.IsActive ? Mathf.Lerp(MinIdleAlpha, MaxIdleAlpha, lastUpkeepRatio) : 0f;
            float intensity = Mathf.Clamp01(baseAlpha + FlashPeakAlpha * flashLevel);
            Color hueColor = Color.HSVToRGB(ColorHue.Value, 1f, 1f);
            // Particles/Additive reads _TintColor (not the standard _Color that .material.color sets);
            // leaving _TintColor at the shader's own default (0.5,0.5,0.5,0.5) is what rendered solid
            // opaque white regardless of intensity/hue — set it directly instead.
            visRenderer.material.SetColor("_TintColor", hueColor * intensity);
            visRenderer.enabled = intensity > 0.001f;
        }
    }
}
