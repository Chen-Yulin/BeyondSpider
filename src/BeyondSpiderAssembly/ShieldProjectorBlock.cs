using System.Collections.Generic;
using UnityEngine;

namespace BeyondSpiderAssembly
{
    public class ShieldProjectorBlock : SpaceBlock
    {
        private const float HyperVelocityThreshold = 2200f;
        private const float DecelCoefficient = 0.02f;
        private const float EnergyPerDeltaV = 0.6f;
        private const float UpkeepBase = 6f;
        private const float UpkeepPerVolume = 0.02f;

        private float lastUpkeepRatio;
        private bool interceptedThisTick;

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
        }

        private bool Contains(Vector3 position)
        {
            Vector3 delta = position - transform.position;
            float a = Vector3.Dot(delta, transform.forward);
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
    }
}
