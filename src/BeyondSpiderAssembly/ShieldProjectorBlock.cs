using System.Collections.Generic;
using UnityEngine;

namespace BeyondSpiderAssembly
{
    public class ShieldProjectorBlock : SpaceBlock
    {
        private const float HyperVelocityThreshold = 2200f;
        private const float DecelCoefficient = 0.02f;
        private const float EnergyPerDeltaV = 0.02f;
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
        private float builtHeight = -1f;
        private float flashLevel;

        public MSlider Radius;
        public MSlider Depth;
        public MSlider Strength;
        public MSlider ColorHue;
        public MSlider Height;
        public MToggle AlwaysVisible;

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
        }

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
                    // Once deceleration brings a round's speed to/below HyperVelocityThreshold, it stops
                    // being eligible here — the field caps a round down to the threshold, it doesn't fully stop it.
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

        private float Decelerate(ShipState ship, float effectiveStrength, Vector3 velocity, float mass)
        {
            mass = Mathf.Max(0.05f, mass);
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

            float baseAlpha = AlwaysVisible.IsActive ? BaseAlpha * lastUpkeepRatio : 0f;
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
