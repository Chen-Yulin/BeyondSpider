using System.Collections.Generic;
using Modding;
using UnityEngine;

namespace BeyondSpiderAssembly
{
    public class SpaceCaptainBlock : SpaceBlock
    {
        public MKey SwitchPriority;
        public MToggle DefaultAntiAir;
        public MToggle Iff;

        private const float FireControlPositionBucket = 10f;
        private const float FireControlVelocityBucket = 10f;

        private readonly Dictionary<string, FireSolution> lockedFireSolutionCache = new Dictionary<string, FireSolution>();
        private float lockedFireSolutionCacheTime = -1f;

        public override void SafeAwake()
        {
            base.SafeAwake();
            gameObject.name = "BeyondSpider Captain";
            SwitchPriority = AddKey("Switch Priority", "BSCaptainSwitchPriority", KeyCode.T);
            DefaultAntiAir = AddToggle("Default Anti-Air", "BSCaptainDefaultAntiAir", true);
            Iff = AddToggle("IFF", "BSCaptainIFF", true);
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

            int localPlayer = StatMaster.isMP ? PlayerData.localPlayer.networkId : 0;
            if (PlayerID == localPlayer && CaptainRadarView.Instance != null)
            {
                CaptainRadarView.Instance.SetOpen(false);
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

            // Self-heals against block init order: if SpaceShipCore's OnSimulateStart (which creates
            // the ShipState) happens to run after this block's own OnSimulateStart — always the case
            // when loading a save, where every block starts simulating in whatever order the engine
            // iterates the machine, unlike incrementally placing a Captain onto an already-simulating
            // ship — OwnShip() returned null there and ship.Captain was never set. This tick keeps
            // retrying every frame until it succeeds, and only applies the default priority once, on
            // first claim, so it never fights a player's later manual SwitchPriority toggle.
            if (ship.Captain != this)
            {
                ship.Captain = this;
                ship.Priority = DefaultAntiAir.IsActive ? CommandPriority.AntiAir : CommandPriority.AntiShip;
            }

            ship.LockedSolution = default(FireSolution);
            if (ship.LockedTarget == null || !ship.LockedTarget.IsAlive)
            {
                return;
            }

            SensorTrack lockedTrack;
            if (TryGetLockedTrack(ship, out lockedTrack))
            {
                ship.LockedSolution.Target = lockedTrack.Target;
                ship.LockedSolution.AimPoint = lockedTrack.Position + lockedTrack.Velocity * Mathf.Clamp(lockedTrack.TimeToImpact, 0f, 2f);
                ship.LockedSolution.TimeToImpact = lockedTrack.TimeToImpact;
            }
        }

        public bool CanCommandLockedFireAt(ITrackable target)
        {
            if (target == null)
            {
                return false;
            }

            return !Iff.IsActive || SpaceBallistics.IsHostile(this, target);
        }

        public bool TryGetLockedFireSolution(Vector3 approximateMuzzlePosition, float muzzleVelocity, out FireSolution solution)
        {
            solution = default(FireSolution);
            ShipState ship = OwnShip();
            if (ship == null || ship.LockedTarget == null || !ship.LockedTarget.IsAlive)
            {
                return false;
            }

            SensorTrack lockedTrack;
            if (!TryGetLockedTrack(ship, out lockedTrack))
            {
                return false;
            }

            if (!Mathf.Approximately(lockedFireSolutionCacheTime, Time.fixedTime))
            {
                lockedFireSolutionCache.Clear();
                lockedFireSolutionCacheTime = Time.fixedTime;
            }

            Vector3 localPosition = transform.InverseTransformPoint(approximateMuzzlePosition);
            int bucketX = Mathf.RoundToInt(localPosition.x / FireControlPositionBucket);
            int bucketY = Mathf.RoundToInt(localPosition.y / FireControlPositionBucket);
            int bucketZ = Mathf.RoundToInt(localPosition.z / FireControlPositionBucket);
            int velocityBucket = Mathf.Max(1, Mathf.RoundToInt(muzzleVelocity / FireControlVelocityBucket));
            string key = bucketX.ToString() + ":" + bucketY.ToString() + ":" + bucketZ.ToString() + ":" + velocityBucket.ToString();

            if (lockedFireSolutionCache.TryGetValue(key, out solution))
            {
                return solution.Target != null;
            }

            Vector3 bucketLocal = new Vector3(
                bucketX * FireControlPositionBucket,
                bucketY * FireControlPositionBucket,
                bucketZ * FireControlPositionBucket);
            Vector3 bucketWorld = transform.TransformPoint(bucketLocal);
            float quantizedVelocity = velocityBucket * FireControlVelocityBucket;
            float time = SpaceBallistics.EstimateInterceptTime(bucketWorld, lockedTrack.Position, lockedTrack.Velocity, quantizedVelocity);

            solution.Target = lockedTrack.Target;
            solution.TimeToImpact = time;
            solution.AimPoint = lockedTrack.Position + lockedTrack.Velocity * Mathf.Clamp(time, 0f, 8f);
            solution.Priority = 1f;
            lockedFireSolutionCache.Add(key, solution);
            return true;
        }

        private static bool TryGetLockedTrack(ShipState ship, out SensorTrack lockedTrack)
        {
            lockedTrack = default(SensorTrack);
            for (int i = 0; i < ship.Tracks.Count; i++)
            {
                SensorTrack track = ship.Tracks[i];
                if (ReferenceEquals(track.Target, ship.LockedTarget))
                {
                    lockedTrack = track;
                    return true;
                }
            }
            return false;
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
