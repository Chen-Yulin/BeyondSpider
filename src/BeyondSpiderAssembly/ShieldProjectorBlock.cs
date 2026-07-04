using System.Collections.Generic;
using UnityEngine;

namespace BeyondSpiderAssembly
{
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
}
