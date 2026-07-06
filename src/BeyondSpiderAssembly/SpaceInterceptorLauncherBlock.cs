using UnityEngine;

namespace BeyondSpiderAssembly
{
    public class SpaceInterceptorLauncherBlock : SpaceBlock
    {
        public MKey Launch;
        public MToggle AutoLaunch;
        public MSlider Range;
        public MSlider Thrust;
        public MSlider EnergyPerLaunch;
        public MSlider WarheadDamage;

        private float reload;
        private float reloadTime = 3f;
        private bool manualLaunchQueued;

        public override void SafeAwake()
        {
            base.SafeAwake();
            gameObject.name = "BeyondSpider Space Interceptor Launcher";
            Launch = AddKey("Launch", "BSSpaceInterceptorLaunch", KeyCode.V);
            AutoLaunch = AddToggle("Auto Launch", "BSSpaceInterceptorAuto", true);
            Range = AddSlider("Range", "BSSpaceInterceptorRange", 900f, 100f, 2500f);
            Thrust = AddSlider("Thrust", "BSSpaceInterceptorThrust", 850f, 100f, 4000f);
            EnergyPerLaunch = AddSlider("Energy Per Launch", "BSSpaceInterceptorEnergy", 130f, 10f, 1000f);
            WarheadDamage = AddSlider("Warhead Damage", "BSSpaceInterceptorDamage", 420f, 50f, 2200f);
        }

        public override void OnSimulateStart()
        {
            base.OnSimulateStart();
            reload = reloadTime;
            manualLaunchQueued = false;
            ShipState ship = OwnShip();
            if (ship != null)
            {
                SpaceCombatRegistry.RegisterSubsystem(PlayerID, this, ship.Launchers);
            }
        }

        public override void OnSimulateStop()
        {
            ShipState ship = OwnShip();
            if (ship != null)
            {
                SpaceCombatRegistry.RemoveSubsystem(this, ship.Launchers);
            }
        }

        public override void SimulateUpdateHost()
        {
            if (Launch.IsPressed)
            {
                manualLaunchQueued = true;
            }
        }

        public override void SimulateFixedUpdateHost()
        {
            reload = Mathf.Min(reloadTime, reload + Time.fixedDeltaTime);
            ShipState ship = OwnShip();
            bool manual = manualLaunchQueued;
            manualLaunchQueued = false;
            if (!manual && (!AutoLaunch.IsActive || ship == null || ship.DefensiveSolution.Target == null))
            {
                return;
            }

            ITrackable target = ship == null ? null : ship.DefensiveSolution.Target;
            if (manual && target == null)
            {
                target = FindNearestHostile();
            }
            if (target == null || !SpaceBallistics.IsHostile(this, target))
            {
                return;
            }
            if (Vector3.Distance(transform.position, target.Position) > Range.Value)
            {
                return;
            }
            LaunchInterceptor(target);
        }

        private ITrackable FindNearestHostile()
        {
            ITrackable best = null;
            float bestDistance = Range.Value;
            for (int i = 0; i < SpaceCombatRegistry.Trackables.Count; i++)
            {
                ITrackable candidate = SpaceCombatRegistry.Trackables[i];
                if (!SpaceBallistics.IsHostile(this, candidate))
                {
                    continue;
                }
                float distance = Vector3.Distance(transform.position, candidate.Position);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = candidate;
                }
            }
            return best;
        }

        private void LaunchInterceptor(ITrackable target)
        {
            if (reload < reloadTime)
            {
                return;
            }

            ShipState ship = OwnShip();
            if (ship != null && ship.Energy.Request(EnergyBus.Weapon, EnergyPerLaunch.Value) < 0.35f)
            {
                return;
            }

            reload = 0f;
            GameObject missile = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            missile.name = "BeyondSpider Interceptor Missile";
            missile.transform.position = transform.position + transform.forward * 1.5f;
            missile.transform.rotation = Quaternion.LookRotation(transform.forward, transform.up);
            missile.transform.localScale = new Vector3(0.28f, 0.9f, 0.28f);
            Rigidbody rb = missile.AddComponent<Rigidbody>();
            rb.mass = 0.5f;
            rb.drag = 0.02f;
            rb.useGravity = false;
            rb.velocity = (Body == null ? Vector3.zero : Body.velocity) + transform.forward * 35f;
            SpaceInterceptorMissile interceptor = missile.AddComponent<SpaceInterceptorMissile>();
            interceptor.OwnerPlayerID = PlayerID;
            interceptor.OwnerTeam = Team;
            interceptor.Target = target;
            interceptor.Thrust = Thrust.Value;
            interceptor.Damage = WarheadDamage.Value;
        }
    }

    public class SpaceInterceptorMissile : MonoBehaviour, ITrackable
    {
        public int OwnerPlayerID;
        public MPTeam OwnerTeam;
        public ITrackable Target;
        public float Thrust;
        public float Damage;

        private Rigidbody body;
        private float spawnTime;

        public int PlayerID { get { return OwnerPlayerID; } }
        public MPTeam Team { get { return OwnerTeam; } }
        public TrackKind Kind { get { return TrackKind.DefensiveMissile; } }
        public Vector3 Position { get { return transform.position; } }
        public Vector3 Velocity { get { return body == null ? Vector3.zero : body.velocity; } }
        public float Radius { get { return 1.2f; } }
        public bool IsAlive { get { return gameObject.activeSelf && Time.time - spawnTime < 12f; } }

        private void Awake()
        {
            body = GetComponent<Rigidbody>();
            spawnTime = Time.time;
            SpaceCombatRegistry.RegisterTrackable(this);
        }

        private void FixedUpdate()
        {
            if (Target == null || !Target.IsAlive || Time.time - spawnTime > 12f)
            {
                Destroy(gameObject);
                return;
            }

            Vector3 intercept = Target.Position + Target.Velocity * 0.35f;
            Vector3 desired = (intercept - transform.position).normalized;
            body.AddForce(desired * Thrust, ForceMode.Force);
            if (body.velocity.sqrMagnitude > 1f)
            {
                transform.rotation = Quaternion.LookRotation(body.velocity.normalized, Vector3.up);
            }

            if (Vector3.Distance(transform.position, Target.Position) <= 5f + Target.Radius)
            {
                HeavyNuclearMissileBlock missile = Target as HeavyNuclearMissileBlock;
                if (missile != null)
                {
                    missile.ApplyDamage(Damage);
                }
                Destroy(gameObject);
            }
        }

        private void OnDestroy()
        {
            SpaceCombatRegistry.UnregisterTrackable(this);
        }
    }
}
