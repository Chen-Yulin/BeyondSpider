using System.Collections.Generic;
using Modding;
using UnityEngine;

namespace BeyondSpiderAssembly
{
    public class SpaceCaptainBlock : SpaceBlock
    {
        public MToggle Iff;

        private const float FireControlPositionBucket = 10f;
        private const float FireControlVelocityBucket = 10f;

        // Channel-0 (air-defence) auto-lock: a hostile counts as a threat only while it closes
        // on the ship faster than this — 距离近 且 速度矢量指向本舰 — and among threats the
        // score is closing speed over distance, so near, head-on targets rank highest. The
        // incumbent keeps its lock unless a challenger beats it by the hysteresis factor, so
        // the lock doesn't flap between two similar threats every scan.
        private const float AutoLockMinClosingSpeed = 1f;
        private const float AutoLockHysteresis = 1.3f;

        private readonly Dictionary<string, FireSolution> channelFireSolutionCache = new Dictionary<string, FireSolution>();
        private float channelFireSolutionCacheTime = -1f;

        public override void SafeAwake()
        {
            base.SafeAwake();
            gameObject.name = "BeyondSpider Captain";
            Iff = AddToggle("IFF", "BSCaptainIFF", true);
        }

        public override void OnSimulateStart()
        {
            base.OnSimulateStart();
            ShipState ship = OwnShip();
            if (ship != null)
            {
                ship.Captain = this;
            }

            int localPlayer = StatMaster.isMP ? PlayerData.localPlayer.networkId : 0;
            if (PlayerID == localPlayer && CaptainRadarView.Instance != null)
            {
                CaptainRadarView.Instance.SetOpen(true);
            }
        }

        public override void OnSimulateStop()
        {
            ShipState ship = OwnShip();
            if (ship != null && ship.Captain == this)
            {
                ship.Captain = null;
                for (int channel = 0; channel < FireChannels.Count; channel++)
                {
                    ship.ChannelTargets[channel] = null;
                }
                ship.Channel0ManualLock = false;
            }

            int localPlayer = StatMaster.isMP ? PlayerData.localPlayer.networkId : 0;
            if (PlayerID == localPlayer && CaptainRadarView.Instance != null)
            {
                CaptainRadarView.Instance.SetOpen(false);
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
            // retrying every frame until it succeeds.
            if (ship.Captain != this)
            {
                ship.Captain = this;
            }

            PruneDeadChannelTargets(ship);
            UpdateAutoAirDefenseLock(ship);
        }

        // Host-authoritative cleanup: a channel whose target died is cleared everywhere (clients
        // learn via the unlock broadcast), and a dead manual channel-0 lock hands the channel
        // back to the auto threat selection.
        private void PruneDeadChannelTargets(ShipState ship)
        {
            for (int channel = 0; channel < FireChannels.Count; channel++)
            {
                ITrackable target = ship.ChannelTargets[channel];
                if (target == null || target.IsAlive)
                {
                    continue;
                }

                ship.ChannelTargets[channel] = null;
                if (channel == 0)
                {
                    ship.Channel0ManualLock = false;
                }
                ModNetworking.SendToAll(CaptainLockNet.LockMsg.CreateMessage(PlayerID, channel, 0, 0, false, false));
            }
        }

        // Channel 0 is the air-defence channel: host-side, the captain continuously locks the
        // highest-threat hostile it has sensor coverage of. Candidates must be radar-lockable
        // (ILockable — ships and missiles, same kinds the radar screen shows) and actually
        // closing on the ship; changes are broadcast so every machine's radar shows the same
        // channel-0 reticle. A player's manual channel-0 lock (Channel0ManualLock) pauses this
        // entirely until the lock is cleared or the target dies.
        private void UpdateAutoAirDefenseLock(ShipState ship)
        {
            if (ship.Channel0ManualLock)
            {
                return;
            }

            ITrackable current = ship.ChannelTargets[0];
            ITrackable best = null;
            float bestScore = float.MinValue;
            float currentScore = float.MinValue;
            for (int i = 0; i < ship.Tracks.Count; i++)
            {
                SensorTrack track = ship.Tracks[i];
                ITrackable target = track.Target;
                if (target == null || !target.IsAlive || !(target is ILockable))
                {
                    continue;
                }
                if (!SpaceBallistics.IsHostile(this, target))
                {
                    continue;
                }

                float score = ThreatScore(ship, track);
                if (score == float.MinValue)
                {
                    continue;
                }
                if (ReferenceEquals(target, current))
                {
                    currentScore = score;
                }
                if (score > bestScore)
                {
                    bestScore = score;
                    best = target;
                }
            }

            // Hysteresis: hold the incumbent while it's still a live threat and no challenger
            // clearly outranks it.
            if (current != null && currentScore != float.MinValue && bestScore < currentScore * AutoLockHysteresis)
            {
                return;
            }
            if (ReferenceEquals(best, current))
            {
                return;
            }

            ship.ChannelTargets[0] = best;
            if (best != null)
            {
                ILockable lockable = (ILockable)best;
                ModNetworking.SendToAll(CaptainLockNet.LockMsg.CreateMessage(PlayerID, 0, best.PlayerID, lockable.GuidHash, true, false));
            }
            else
            {
                ModNetworking.SendToAll(CaptainLockNet.LockMsg.CreateMessage(PlayerID, 0, 0, 0, false, false));
            }
        }

        private static float ThreatScore(ShipState ship, SensorTrack track)
        {
            if (ship.Core == null)
            {
                return float.MinValue;
            }

            Vector3 delta = ship.Core.Position - track.Position;
            float distance = Mathf.Max(1f, delta.magnitude);
            float closingSpeed = Vector3.Dot(track.Velocity - ship.Core.Velocity, delta / distance);
            if (closingSpeed < AutoLockMinClosingSpeed)
            {
                return float.MinValue;
            }
            return closingSpeed / distance;
        }

        public bool CanCommandLockedFireAt(ITrackable target)
        {
            if (target == null)
            {
                return false;
            }

            return !Iff.IsActive || SpaceBallistics.IsHostile(this, target);
        }

        // Per-channel successor of the old TryGetLockedFireSolution: a lead solution against the
        // given channel's lock, bucketed/cached exactly as before (with the channel folded into
        // the key) so many barrels of one ship share solutions per physics tick.
        public bool TryGetChannelFireSolution(int channel, Vector3 approximateMuzzlePosition, float muzzleVelocity, out FireSolution solution)
        {
            solution = default(FireSolution);
            ShipState ship = OwnShip();
            if (ship == null || channel < 0 || channel >= FireChannels.Count)
            {
                return false;
            }

            ITrackable target = ship.ChannelTargets[channel];
            if (target == null || !target.IsAlive)
            {
                return false;
            }

            SensorTrack lockedTrack;
            if (!TryGetTrackFor(ship, target, out lockedTrack))
            {
                return false;
            }

            if (!Mathf.Approximately(channelFireSolutionCacheTime, Time.fixedTime))
            {
                channelFireSolutionCache.Clear();
                channelFireSolutionCacheTime = Time.fixedTime;
            }

            Vector3 localPosition = transform.InverseTransformPoint(approximateMuzzlePosition);
            int bucketX = Mathf.RoundToInt(localPosition.x / FireControlPositionBucket);
            int bucketY = Mathf.RoundToInt(localPosition.y / FireControlPositionBucket);
            int bucketZ = Mathf.RoundToInt(localPosition.z / FireControlPositionBucket);
            int velocityBucket = Mathf.Max(1, Mathf.RoundToInt(muzzleVelocity / FireControlVelocityBucket));
            string key = channel.ToString() + "|" + bucketX.ToString() + ":" + bucketY.ToString() + ":" + bucketZ.ToString() + ":" + velocityBucket.ToString();

            if (channelFireSolutionCache.TryGetValue(key, out solution))
            {
                return solution.Target != null;
            }

            Vector3 bucketLocal = new Vector3(
                bucketX * FireControlPositionBucket,
                bucketY * FireControlPositionBucket,
                bucketZ * FireControlPositionBucket);
            Vector3 bucketWorld = transform.TransformPoint(bucketLocal);
            float quantizedVelocity = velocityBucket * FireControlVelocityBucket;
            // Every barrel is assumed to share the ship's own linear velocity (the core's), so the
            // lead is solved in that frame rather than per-turret — see ADR 0006. The core is present
            // whenever a ShipState exists, but guard anyway.
            Vector3 shipVelocity = ship.Core != null ? ship.Core.Velocity : Vector3.zero;
            float time = SpaceBallistics.EstimateInterceptTime(bucketWorld, lockedTrack.Position, lockedTrack.Velocity, quantizedVelocity, shipVelocity);

            solution.Target = lockedTrack.Target;
            solution.TimeToImpact = time;
            solution.AimPoint = lockedTrack.Position + (lockedTrack.Velocity - shipVelocity) * Mathf.Clamp(time, 0f, 8f);
            solution.Priority = 1f;
            channelFireSolutionCache.Add(key, solution);
            return true;
        }

        private static bool TryGetTrackFor(ShipState ship, ITrackable target, out SensorTrack track)
        {
            track = default(SensorTrack);
            for (int i = 0; i < ship.Tracks.Count; i++)
            {
                if (ReferenceEquals(ship.Tracks[i].Target, target))
                {
                    track = ship.Tracks[i];
                    return true;
                }
            }
            return false;
        }
    }

    public class CaptainLockNet : SingleInstance<CaptainLockNet>
    {
        public override string Name { get { return "BeyondSpider Captain Lock Net"; } }

        // (shipPlayerId, channel, targetPlayerId, targetGuidHash, hasLock, manual)
        // `manual` matters only on channel 0: a manual lock pauses the captain's auto threat
        // selection on every machine (Channel0ManualLock), an auto broadcast doesn't.
        public static MessageType LockMsg = ModNetworking.CreateMessageType(
            DataType.Integer, DataType.Integer, DataType.Integer, DataType.Integer, DataType.Boolean, DataType.Boolean);

        public void LockReceiver(Message msg)
        {
            int shipPlayerId = (int)msg.GetData(0);
            int channel = Mathf.Clamp((int)msg.GetData(1), 0, FireChannels.Count - 1);
            int targetPlayerId = (int)msg.GetData(2);
            int targetGuidHash = (int)msg.GetData(3);
            bool hasLock = (bool)msg.GetData(4);
            bool manual = (bool)msg.GetData(5);

            ShipState ship = SpaceCombatRegistry.FindShip(shipPlayerId);
            if (ship == null)
            {
                return;
            }
            if (!hasLock)
            {
                ship.ChannelTargets[channel] = null;
                if (channel == 0)
                {
                    ship.Channel0ManualLock = false;
                }
                return;
            }

            IList<ITrackable> all = SpaceCombatRegistry.Trackables;
            for (int i = 0; i < all.Count; i++)
            {
                ILockable candidate = all[i] as ILockable;
                if (candidate != null && candidate.PlayerID == targetPlayerId && candidate.GuidHash == targetGuidHash)
                {
                    ship.ChannelTargets[channel] = candidate;
                    if (channel == 0)
                    {
                        ship.Channel0ManualLock = manual;
                    }
                    return;
                }
            }
        }
    }
}
