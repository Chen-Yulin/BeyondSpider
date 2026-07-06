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

    public class CaptainLockNet : SingleInstance<CaptainLockNet>
    {
        public override string Name { get { return "BeyondSpider Captain Lock Net"; } }

        // (shipPlayerId, targetPlayerId, targetGuidHash, hasLock)
        public static MessageType LockMsg = ModNetworking.CreateMessageType(
            DataType.Integer, DataType.Integer, DataType.Integer, DataType.Boolean);

        public void LockReceiver(Message msg)
        {
            int shipPlayerId = (int)msg.GetData(0);
            int targetPlayerId = (int)msg.GetData(1);
            int targetGuidHash = (int)msg.GetData(2);
            bool hasLock = (bool)msg.GetData(3);

            ShipState ship = SpaceCombatRegistry.FindShip(shipPlayerId);
            if (ship == null)
            {
                return;
            }
            if (!hasLock)
            {
                ship.LockedTarget = null;
                return;
            }

            IList<ITrackable> all = SpaceCombatRegistry.Trackables;
            for (int i = 0; i < all.Count; i++)
            {
                ILockable candidate = all[i] as ILockable;
                if (candidate != null && candidate.PlayerID == targetPlayerId && candidate.GuidHash == targetGuidHash)
                {
                    ship.LockedTarget = candidate;
                    return;
                }
            }
        }
    }
}
