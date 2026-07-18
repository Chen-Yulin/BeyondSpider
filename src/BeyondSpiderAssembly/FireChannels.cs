using System.Collections.Generic;
using UnityEngine;

namespace BeyondSpiderAssembly
{
    // A weapon's (or in-flight missile's) personal channel-0 target assignment (ADR-0014).
    // Channel 0 holds a rank-ordered THREAT LIST of up to Channel0MaxTargets targets
    // (ShipState.Channel0Threats) rather than a single lock; every channel-0 subscriber owns
    // one of these and draws its own target from the list with the rank-weighted lottery
    // p_n = remaining * (n == N-1 ? 1 : 0.5) — [1], [.5 .5], [.5 .25 .25], [.5 .25 .125 .125]
    // — so on average half the ship's air defence engages the top threat and the rest spreads.
    // The draw is sticky: a weapon keeps its target until it dies or drops off the list
    // (目标失效才重抽), so turrets don't flicker between targets as the ranking shuffles.
    public sealed class FireChannelAssignment
    {
        private ITrackable assigned;

        public ITrackable Resolve(ShipState ship)
        {
            if (ship == null)
            {
                assigned = null;
                return null;
            }

            List<ITrackable> threats = ship.Channel0Threats;
            if (threats.Count == 0)
            {
                assigned = null;
                return null;
            }
            if (assigned != null && assigned.IsAlive && threats.Contains(assigned))
            {
                return assigned;
            }

            assigned = Draw(threats);
            return assigned;
        }

        public void Clear()
        {
            assigned = null;
        }

        // Cumulative walk of p_n = remaining * (n == N-1 ? 1 : 0.5): each rank takes half the
        // leftover probability mass, the last rank takes all of it (so the p_n always sum to 1).
        private static ITrackable Draw(List<ITrackable> threats)
        {
            int count = threats.Count;
            float roll = Random.value;
            float remaining = 1f;
            for (int rank = 0; rank < count; rank++)
            {
                float p = rank == count - 1 ? remaining : remaining * 0.5f;
                if (roll < p)
                {
                    return threats[rank];
                }
                roll -= p;
                remaining -= p;
            }
            return threats[count - 1];
        }
    }

    // Multi-channel fire control (ADR-0010): the ship carries four independent target locks
    // ("fire channels") instead of the old single LockedTarget. Channel 0 is the air-defence
    // channel — the captain auto-locks the highest-threat closing hostile onto it, and the
    // player may override it by hand from the radar screen; channels 1-3 are purely
    // player-assigned. Every automatic weapon (flak turret, missile launcher) and the gunner
    // carries an MFireChannel bitmask choosing which channels it listens to; when several
    // enabled channels hold targets, selection picks channel 0 first, then the best
    // angle/distance score among the rest — a target out of the weapon's own range or fire
    // arc is never considered at all.
    public static class FireChannels
    {
        public const int Count = 4;
        public const int AllMask = (1 << Count) - 1;
        // Channel 0 holds up to this many simultaneous threat locks (ADR-0014).
        public const int Channel0MaxTargets = 4;

        // Channel tint used everywhere a channel shows up (radar tab buttons, lock reticles,
        // the MFireChannel mapper buttons): 0 red, 1 yellow, 2 cyan, 3 purple.
        public static readonly Color[] Colors =
        {
            new Color(1f, 0.3f, 0.25f),
            new Color(1f, 0.85f, 0.2f),
            new Color(0.25f, 0.9f, 0.95f),
            new Color(0.75f, 0.45f, 1f)
        };

        // 权重系数 i in the priority score 1/(夹角+10°)*i + 1/距离 — angle in degrees between
        // the barrel and the target, distance in meters. At 1 the angle term dominates for
        // anything beyond point-blank, so a weapon prefers the target nearest its boresight
        // and only tie-breaks on distance.
        public const float AngleScoreWeight = 1f;
        public const float AngleScoreSofteningDeg = 10f;

        // A weapon's own reachability test for a proposed aim point (its 射界). Null means the
        // weapon has no arc restriction (missiles turn, the gunner's hinge limits are opaque).
        public delegate bool ArcCheck(Vector3 aimPoint);

        public static bool Contains(int mask, int channel)
        {
            return (mask & (1 << channel)) != 0;
        }

        public static float Score(float angleDeg, float distance)
        {
            return AngleScoreWeight / (angleDeg + AngleScoreSofteningDeg) + 1f / Mathf.Max(1f, distance);
        }

        // The channel-c target this particular subscriber should engage: channels 1-3 are the
        // ship-wide single locks; channel 0 goes through the subscriber's own sticky lottery
        // assignment over the ship's threat list (ADR-0014). A null assignment falls back to
        // the ship-wide rank-0 threat (used by code paths with no per-subscriber identity).
        private static ITrackable ChannelTargetFor(ShipState ship, int channel, FireChannelAssignment channel0Assignment)
        {
            if (channel != 0)
            {
                return ship.ChannelTargets[channel];
            }
            return channel0Assignment != null ? channel0Assignment.Resolve(ship) : ship.ChannelTargets[0];
        }

        // Picks the lead fire solution a barrel weapon (flak turret / gunner-driven gun) should
        // engage from its enabled channels. Per-channel gating mirrors the old single-lock path:
        // the captain's IFF override (CanCommandLockedFireAt) decides whether a locked
        // friendly/own-team target may be engaged at all.
        public static bool TrySelectSolution(ShipState ship, int mask, FireChannelAssignment channel0Assignment,
            Vector3 muzzlePosition, float muzzleVelocity, Vector3 barrelForward, float maxRange, ArcCheck inArc,
            out FireSolution solution)
        {
            solution = default(FireSolution);
            if (ship == null || ship.Captain == null)
            {
                return false;
            }

            bool found = false;
            float bestScore = float.MinValue;
            for (int channel = 0; channel < Count; channel++)
            {
                if (!Contains(mask, channel))
                {
                    continue;
                }

                ITrackable target = ChannelTargetFor(ship, channel, channel0Assignment);
                FireSolution candidate;
                if (target == null || !ship.Captain.TryGetTargetFireSolution(target, muzzlePosition, muzzleVelocity, out candidate))
                {
                    continue;
                }
                if (!ship.Captain.CanCommandLockedFireAt(candidate.Target))
                {
                    continue;
                }

                float distance = Vector3.Distance(muzzlePosition, candidate.Target.Position);
                if (distance > maxRange)
                {
                    continue;
                }
                if (inArc != null && !inArc(candidate.AimPoint))
                {
                    continue;
                }

                // The air-defence channel outranks every other channel outright.
                if (channel == 0)
                {
                    solution = candidate;
                    return true;
                }

                float angle = Vector3.Angle(barrelForward, candidate.AimPoint - muzzlePosition);
                float score = Score(angle, distance);
                if (score > bestScore)
                {
                    bestScore = score;
                    solution = candidate;
                    found = true;
                }
            }
            return found;
        }

        // Same channel walk for a guided missile (or its launcher), which wants a target rather
        // than a lead solution. `self` is excluded so an in-flight missile can never pick itself
        // (see MissileFireControl.SelectTarget).
        public static ITrackable SelectTarget(ShipState ship, int mask, FireChannelAssignment channel0Assignment,
            Vector3 origin, Vector3 forward, float maxRange, int playerId, MPTeam team, ITrackable self)
        {
            if (ship == null)
            {
                return null;
            }

            ITrackable best = null;
            float bestScore = float.MinValue;
            for (int channel = 0; channel < Count; channel++)
            {
                if (!Contains(mask, channel))
                {
                    continue;
                }

                ITrackable candidate = ChannelTargetFor(ship, channel, channel0Assignment);
                if (candidate == null || ReferenceEquals(candidate, self))
                {
                    continue;
                }
                if (!MissileFireControl.CanEngage(ship, candidate, playerId, team))
                {
                    continue;
                }
                float distance = Vector3.Distance(origin, candidate.Position);
                if (distance > maxRange)
                {
                    continue;
                }

                if (channel == 0)
                {
                    return candidate;
                }

                float angle = Vector3.Angle(forward, candidate.Position - origin);
                float score = Score(angle, distance);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = candidate;
                }
            }
            return best;
        }

        // True when `target` is locked on ANY of the ship's channels — the multi-channel
        // successor to "ReferenceEquals(ship.LockedTarget, target)" identity checks. Channel 0
        // counts every member of its multi-target threat list (ADR-0014), not just rank 0.
        public static bool IsChannelTarget(ShipState ship, ITrackable target)
        {
            if (ship == null || target == null)
            {
                return false;
            }
            for (int channel = 0; channel < Count; channel++)
            {
                if (ReferenceEquals(ship.ChannelTargets[channel], target))
                {
                    return true;
                }
            }
            for (int i = 0; i < ship.Channel0Threats.Count; i++)
            {
                if (ReferenceEquals(ship.Channel0Threats[i], target))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
