using UnityEngine;

namespace BeyondSpiderAssembly
{
    public class HeavyNuclearMissileBlock : SpaceBlock, ITrackable
    {
        public MKey Launch;
        public MSlider HealthSlider;
        public MSlider Thrust;
        public MSlider ThreatMassSlider;
        public MToggle AutoLaunch;

        private bool launched;
        private float health;

        public TrackKind Kind { get { return TrackKind.HeavyMissile; } }
        public Vector3 Position { get { return transform.position; } }
        public Vector3 Velocity { get { return Body == null ? Vector3.zero : Body.velocity; } }
        public float Radius { get { return 3f; } }
        public bool IsAlive { get { return BlockBehaviour != null && BlockBehaviour.isSimulating && health > 0f; } }
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
        }

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

        public override void OnSimulateStop()
        {
            SpaceCombatRegistry.UnregisterTrackable(this);
        }

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

        public void ApplyShieldEffect(float strength)
        {
            if (Body == null || strength <= 0f)
            {
                return;
            }
            Body.velocity *= Mathf.Clamp01(1f - 0.015f * strength);
            ApplyDamage(8f * strength * Time.fixedDeltaTime);
        }

        private void Detonate(bool hit)
        {
            SpaceCombatRegistry.UnregisterTrackable(this);
            if (Body != null)
            {
                Body.velocity *= 0.1f;
            }
            gameObject.SetActive(false);
        }
    }
}
