using System.Collections.Generic;
using Modding;
using UnityEngine;

namespace BeyondSpiderAssembly
{
    public class SpaceCaptainBlock : SpaceBlock
    {
        public MKey SwitchPriority;
        public MToggle DefaultAntiAir;

        public override void SafeAwake()
        {
            base.SafeAwake();
            gameObject.name = "BeyondSpider Captain";
            SwitchPriority = AddKey("Switch Priority", "BSCaptainSwitchPriority", KeyCode.T);
            DefaultAntiAir = AddToggle("Default Anti-Air", "BSCaptainDefaultAntiAir", true);
        }

        public override void OnSimulateStart()
        {
            base.OnSimulateStart();
            ShipState ship = OwnShip();
            if (ship != null)
            {
                ship.Captain = this;
                ship.Priority = DefaultAntiAir.IsActive ? CommandPriority.AntiAir : CommandPriority.AntiShip;
            }
        }

        public override void OnSimulateStop()
        {
            ShipState ship = OwnShip();
            if (ship != null && ship.Captain == this)
            {
                ship.Captain = null;
            }
        }

        public override void SimulateUpdateHost()
        {
            ShipState ship = OwnShip();
            if (ship == null)
            {
                return;
            }

            if (SwitchPriority.IsPressed)
            {
                ship.Priority = ship.Priority == CommandPriority.AntiAir
                    ? CommandPriority.AntiShip
                    : CommandPriority.AntiAir;
            }
        }

        public override void SimulateFixedUpdateHost()
        {
            ShipState ship = OwnShip();
            if (ship == null)
            {
                return;
            }

            ship.LockedSolution = default(FireSolution);
            if (ship.LockedTarget == null || !ship.LockedTarget.IsAlive)
            {
                return;
            }

            for (int i = 0; i < ship.Tracks.Count; i++)
            {
                SensorTrack track = ship.Tracks[i];
                if (!ReferenceEquals(track.Target, ship.LockedTarget))
                {
                    continue;
                }
                ship.LockedSolution.Target = track.Target;
                ship.LockedSolution.AimPoint = track.Position + track.Velocity * Mathf.Clamp(track.TimeToImpact, 0f, 2f);
                ship.LockedSolution.TimeToImpact = track.TimeToImpact;
                break;
            }
        }
    }
}
