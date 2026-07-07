using UnityEngine;

namespace BeyondSpiderAssembly
{
    public class CiwsBlock : SpaceBlock
    {
        public MToggle DefaultActive;
        public MSlider Range;
        public MSlider DamagePerSecond;
        public MSlider EnergyPerSecond;

        // See agent-besiege-mod-guide.md's "注册时序规范" — retried each tick until it
        // succeeds, since ShipState may not exist yet when this OnSimulateStart runs.
        private bool registered;

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
                registered = true;
            }
        }

        public override void OnSimulateStop()
        {
            ShipState ship = OwnShip();
            if (ship != null)
            {
                SpaceCombatRegistry.RemoveSubsystem(this, ship.Ciws);
            }
            registered = false;
        }

        public override void SimulateFixedUpdateHost()
        {
            ShipState ship = OwnShip();
            if (ship != null && !registered)
            {
                SpaceCombatRegistry.RegisterSubsystem(PlayerID, this, ship.Ciws);
                registered = true;
            }

            if (!DefaultActive.IsActive)
            {
                return;
            }

            if (ship == null)
            {
                return;
            }

            FireSolution solution = (ship.Priority == CommandPriority.AntiShip && ship.LockedSolution.Target != null)
                ? ship.LockedSolution
                : ship.DefensiveSolution;
            if (solution.Target == null)
            {
                return;
            }

            HeavyNuclearMissileBlock missile = solution.Target as HeavyNuclearMissileBlock;
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
