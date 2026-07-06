using UnityEngine;

namespace BeyondSpiderAssembly
{
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
        private bool hadGuidance;
        private bool launchQueued;

        public int GuidHash { get; private set; }

        public TrackKind Kind { get { return TrackKind.HeavyMissile; } }
        public Vector3 Position { get { return transform.position; } }
        public Vector3 Velocity { get { return Body == null ? Vector3.zero : Body.velocity; } }
        public float Radius { get { return 3f; } }
        // Requires `launched` so a still-mounted, unfired missile (including an ally's) never
        // shows up as an independent radar target — see docs/adr/0004-hostile-filtering-moves-to-consumers.md.
        public bool IsAlive { get { return launched && BlockBehaviour != null && BlockBehaviour.isSimulating && health > 0f; } }
        public float ThreatMass { get { return ThreatMassSlider.Value; } }

        public override void SafeAwake()
        {
            base.SafeAwake();
            gameObject.name = "BeyondSpider Heavy Nuclear Missile";
            Launch = AddKey("Launch", "BSLaunchHeavyMissile", KeyCode.X);
            AutoLaunch = AddToggle("Auto Launch", "BSAutoLaunch", false);
            HealthSlider = AddSlider("Missile Health", "BSMissileHealth", 650f, 100f, 3000f);
            Thrust = AddSlider("Thrust", "BSMissileThrust", 450f, 50f, 3000f);
            ThreatMassSlider = AddSlider("Threat Mass", "BSThreatMass", 50f, 5f, 400f);
            BlastRadius = AddSlider("Blast Radius", "BSMissileBlastRadius", 40f, 10f, 120f);
        }

        public override void OnSimulateStart()
        {
            base.OnSimulateStart();
            GuidHash = BlockBehaviour.BuildingBlock.Guid.GetHashCode();
            health = HealthSlider.Value;
            launched = AutoLaunch.IsActive;
            launchQueued = false;
            SpaceCombatRegistry.RegisterTrackable(this);
            if (Body != null)
            {
                Body.mass = ThreatMassSlider.Value;
                Body.drag = 0.02f;
            }
        }

        public override void OnSimulateStop()
        {
            SpaceCombatRegistry.UnregisterTrackable(this);
        }

        public override void SimulateUpdateHost()
        {
            if (Launch.IsHeld)
            {
                launchQueued = true;
            }
        }

        public override void SimulateFixedUpdateHost()
        {
            if (!launched && launchQueued)
            {
                launched = true;
            }
            launchQueued = false;

            if (!launched || Body == null || health <= 0f)
            {
                return;
            }

            ShipState ship = OwnShip();
            ITrackable target = ship == null ? null : ship.LockedTarget;
            bool trackedThisTick = false;
            if (target != null && target.IsAlive)
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

        public void ApplyDamage(float damage)
        {
            if (damage <= 0f || health <= 0f)
            {
                return;
            }
            health -= damage;
            if (health <= 0f)
            {
                Detonate(false);
            }
        }

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
    }
}
