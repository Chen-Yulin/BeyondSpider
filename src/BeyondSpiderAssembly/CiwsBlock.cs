using UnityEngine;

namespace BeyondSpiderAssembly
{
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
}
