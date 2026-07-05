using System.Collections.Generic;
using UnityEngine;

namespace BeyondSpiderAssembly
{
    public class ShieldProjectorBlock : SpaceBlock
    {
        private const float HyperVelocityThreshold = 2200f;

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
    }
}
