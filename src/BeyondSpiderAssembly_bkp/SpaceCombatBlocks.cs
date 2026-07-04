using System.Collections.Generic;
using Modding;
using Modding.Blocks;
using Modding.Modules;
using UnityEngine;

namespace BeyondSpiderAssembly
{
    public abstract class SpaceBlock : BlockScript
    {
        public int PlayerID { get; protected set; }
        public MPTeam Team { get; protected set; }
        protected Rigidbody Body;

        public override void SafeAwake()
        {
            PlayerID = BlockBehaviour.ParentMachine.PlayerID;
            Team = BlockBehaviour.Team;
            Body = GetComponent<Rigidbody>();
        }

        protected ShipState OwnShip()
        {
            return SpaceCombatRegistry.FindShip(PlayerID);
        }
    }

    public class SpaceShipCore : SpaceBlock, ITrackable
    {
        public MSlider TotalEnergy;
        public MSlider ArmorShare;
        public MSlider ShieldShare;
        public MSlider WeaponShare;
        public MMenu PriorityMode;

        public string DisplayName;

        public TrackKind Kind { get { return TrackKind.Ship; } }
        public Vector3 Position { get { return transform.position; } }
        public Vector3 Velocity { get { return Body == null ? Vector3.zero : Body.velocity; } }
        public float Radius { get { return Mathf.Max(4f, transform.lossyScale.magnitude * 8f); } }
        public bool IsAlive { get { return BlockBehaviour != null && BlockBehaviour.isSimulating; } }

        public override void SafeAwake()
        {
            base.SafeAwake();
            gameObject.name = "BeyondSpider Ship Core";
            TotalEnergy = AddSlider("Total Energy", "BSTotalEnergy", 1200f, 200f, 8000f);
            ArmorShare = AddSlider("Armor Share", "BSArmorShare", 1f, 0f, 5f);
            ShieldShare = AddSlider("Shield Share", "BSShieldShare", 2f, 0f, 5f);
            WeaponShare = AddSlider("Weapon Share", "BSWeaponShare", 2f, 0f, 5f);
            PriorityMode = AddMenu("Priority", 0, new List<string> { "Defense first", "Ship first" }, false);
        }

        public override void OnSimulateStart()
        {
            base.OnSimulateStart();
            DisplayName = "P" + PlayerID + " Core";
            SpaceCombatRegistry.RegisterCore(this);
            ApplyReactorMass();
        }

        public override void OnSimulateStop()
        {
            SpaceCombatRegistry.UnregisterCore(this);
        }

        public void ConfigureEnergy(EnergyGrid grid)
        {
            grid.Configure(TotalEnergy.Value, ArmorShare.Value, ShieldShare.Value, WeaponShare.Value);
        }

        private void ApplyReactorMass()
        {
            if (Body == null)
            {
                return;
            }
            Body.mass = Mathf.Max(0.3f, 0.3f + Mathf.Pow(TotalEnergy.Value / 1000f, 1.25f) * 0.8f);
        }
    }

    public class SuperCapacitorBlock : SpaceBlock
    {
        public MMenu BusMenu;
        public MSlider CapacityScale;
        public EnergyBus Bus;
        public float Capacity;

        public override void SafeAwake()
        {
            base.SafeAwake();
            gameObject.name = "BeyondSpider Super Capacitor";
            BusMenu = AddMenu("Energy Bus", 3, new List<string> { "Armor", "Shield", "Weapon", "Universal" }, false);
            CapacityScale = AddSlider("Capacity", "BSCapacity", 600f, 100f, 5000f);
        }

        public override void OnSimulateStart()
        {
            base.OnSimulateStart();
            Bus = (EnergyBus)Mathf.Clamp(BusMenu.Value, 0, 3);
            Capacity = CapacityScale.Value * Mathf.Max(0.2f, transform.lossyScale.magnitude);
            ShipState ship = OwnShip();
            if (ship != null)
            {
                SpaceCombatRegistry.RegisterSubsystem(PlayerID, this, ship.Capacitors);
            }
            if (Body != null)
            {
                Body.mass = 0.2f + Capacity / 2500f;
            }
        }

        public override void OnSimulateStop()
        {
            ShipState ship = OwnShip();
            if (ship != null)
            {
                SpaceCombatRegistry.RemoveSubsystem(this, ship.Capacitors);
            }
        }
    }

    public class RadarPanelBlock : SpaceBlock
    {
        public MSlider Range;
        public MSlider ConeAngle;
        public MToggle Iff;

        private float nextScanTime;

        public override void SafeAwake()
        {
            base.SafeAwake();
            gameObject.name = "BeyondSpider Radar Panel";
            Range = AddSlider("Range", "BSRadarRange", 850f, 100f, 3000f);
            ConeAngle = AddSlider("Cone Angle", "BSConeAngle", 65f, 10f, 170f);
            Iff = AddToggle("IFF", "BSIFF", true);
        }

        public override void OnSimulateStart()
        {
            base.OnSimulateStart();
            ShipState ship = OwnShip();
            if (ship != null)
            {
                SpaceCombatRegistry.RegisterSubsystem(PlayerID, this, ship.Radars);
            }
        }

        public override void OnSimulateStop()
        {
            ShipState ship = OwnShip();
            if (ship != null)
            {
                SpaceCombatRegistry.RemoveSubsystem(this, ship.Radars);
            }
        }

        public override void SimulateFixedUpdateHost()
        {
            if (Time.time < nextScanTime)
            {
                return;
            }
            nextScanTime = Time.time + 0.25f;

            ShipState ship = OwnShip();
            if (ship == null)
            {
                return;
            }

            if (Time.time - ship.LastRefreshTime > 0.05f)
            {
                ship.Tracks.Clear();
                ship.LastRefreshTime = Time.time;
            }
            IList<ITrackable> targets = SpaceCombatRegistry.Trackables;
            for (int i = 0; i < targets.Count; i++)
            {
                ITrackable target = targets[i];
                if (target == null || !target.IsAlive || target.PlayerID == PlayerID)
                {
                    continue;
                }
                if (Iff.IsActive && target.Team == Team)
                {
                    continue;
                }

                Vector3 delta = target.Position - transform.position;
                float distance = delta.magnitude;
                if (distance > Range.Value)
                {
                    continue;
                }
                if (Vector3.Angle(transform.forward, delta) > ConeAngle.Value * 0.5f)
                {
                    continue;
                }

                SensorTrack track = new SensorTrack();
                track.Target = target;
                track.Position = target.Position;
                track.Velocity = target.Velocity;
                track.Distance = distance;
                track.Kind = target.Kind;
                track.TimeToImpact = EstimateTimeToImpact(target, ship.Core);
                ship.Tracks.Add(track);
            }
        }

        private static float EstimateTimeToImpact(ITrackable target, SpaceShipCore core)
        {
            if (core == null)
            {
                return 999f;
            }
            Vector3 delta = core.Position - target.Position;
            float closing = Vector3.Dot(target.Velocity - core.Velocity, delta.normalized);
            if (closing <= 1f)
            {
                return 999f;
            }
            return delta.magnitude / closing;
        }
    }

    public class DefenseDirectorBlock : SpaceBlock
    {
        public MSlider DefendedRadius;
        public MMenu Priority;

        public override void SafeAwake()
        {
            base.SafeAwake();
            gameObject.name = "BeyondSpider Defense Director";
            DefendedRadius = AddSlider("Defended Radius", "BSDefendedRadius", 450f, 50f, 1500f);
            Priority = AddMenu("Target Priority", 0, new List<string> { "Time to impact", "Heavy threats" }, false);
        }

        public override void OnSimulateStart()
        {
            base.OnSimulateStart();
            ShipState ship = OwnShip();
            if (ship != null)
            {
                SpaceCombatRegistry.RegisterSubsystem(PlayerID, this, ship.Directors);
            }
        }

        public override void OnSimulateStop()
        {
            ShipState ship = OwnShip();
            if (ship != null)
            {
                SpaceCombatRegistry.RemoveSubsystem(this, ship.Directors);
            }
        }

        public override void SimulateFixedUpdateHost()
        {
            ShipState ship = OwnShip();
            if (ship == null || ship.Core == null)
            {
                return;
            }

            FireSolution best = new FireSolution();
            float bestScore = -1f;
            for (int i = 0; i < ship.Tracks.Count; i++)
            {
                SensorTrack track = ship.Tracks[i];
                if (track.Distance > DefendedRadius.Value)
                {
                    continue;
                }

                float threat = track.Kind == TrackKind.HeavyMissile ? 1000f : 200f;
                float ttiScore = 100f / Mathf.Max(1f, track.TimeToImpact);
                float score = Priority.Value == 0 ? ttiScore + threat * 0.01f : threat + ttiScore;
                if (score > bestScore)
                {
                    bestScore = score;
                    best.Target = track.Target;
                    best.AimPoint = track.Position + track.Velocity * Mathf.Clamp(track.TimeToImpact, 0f, 2f);
                    best.TimeToImpact = track.TimeToImpact;
                    best.Priority = score;
                }
            }
            ship.DefensiveSolution = best;
        }
    }

    public class ShieldProjectorBlock : SpaceBlock
    {
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
                if (missile == null || !missile.IsAlive || missile.Team == Team)
                {
                    continue;
                }

                float distance = Vector3.Distance(transform.position, missile.Position);
                if (distance > Radius.Value)
                {
                    continue;
                }

                float energyCost = missile.Velocity.sqrMagnitude * missile.ThreatMass * 0.002f / Mathf.Max(0.2f, Strength.Value);
                float ratio = ship.Energy.Request(EnergyBus.Shield, energyCost * Time.fixedDeltaTime);
                missile.ApplyShieldEffect(ratio * Strength.Value);
            }
        }
    }

    public class CiwsBlock : SpaceBlock
    {
        public MToggle DefaultActive;
        public MSlider Range;
        public MSlider DamagePerSecond;
        public MSlider EnergyPerSecond;

        public override void SafeAwake()
        {
            base.SafeAwake();
            gameObject.name = "BeyondSpider CIWS";
            DefaultActive = AddToggle("Default Active", "BSCIWSActive", true);
            Range = AddSlider("Range", "BSCIWSRange", 260f, 30f, 800f);
            DamagePerSecond = AddSlider("Damage", "BSCIWSDamage", 90f, 10f, 500f);
            EnergyPerSecond = AddSlider("Energy Use", "BSCIWSEnergy", 80f, 5f, 500f);
        }

        public override void OnSimulateStart()
        {
            base.OnSimulateStart();
            ShipState ship = OwnShip();
            if (ship != null)
            {
                SpaceCombatRegistry.RegisterSubsystem(PlayerID, this, ship.Ciws);
            }
        }

        public override void OnSimulateStop()
        {
            ShipState ship = OwnShip();
            if (ship != null)
            {
                SpaceCombatRegistry.RemoveSubsystem(this, ship.Ciws);
            }
        }

        public override void SimulateFixedUpdateHost()
        {
            if (!DefaultActive.IsActive)
            {
                return;
            }

            ShipState ship = OwnShip();
            if (ship == null || ship.DefensiveSolution.Target == null)
            {
                return;
            }

            HeavyNuclearMissileBlock missile = ship.DefensiveSolution.Target as HeavyNuclearMissileBlock;
            if (missile == null || !missile.IsAlive || missile.Team == Team)
            {
                return;
            }

            float distance = Vector3.Distance(transform.position, missile.Position);
            if (distance > Range.Value)
            {
                return;
            }

            float aim = Vector3.Angle(transform.forward, missile.Position - transform.position);
            if (aim > 90f)
            {
                return;
            }

            float energyRatio = ship.Energy.Request(EnergyBus.Weapon, EnergyPerSecond.Value * Time.fixedDeltaTime);
            float speedPenalty = Mathf.Clamp01(1f - missile.Velocity.magnitude / 500f);
            missile.ApplyDamage(DamagePerSecond.Value * energyRatio * (0.35f + speedPenalty) * Time.fixedDeltaTime);
            Debug.DrawLine(transform.position, missile.Position, Color.red, 0.08f);
        }
    }

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
