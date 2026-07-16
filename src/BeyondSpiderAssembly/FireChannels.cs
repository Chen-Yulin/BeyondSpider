using UnityEngine;

namespace BeyondSpiderAssembly
{
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

        // Picks the lead fire solution a barrel weapon (flak turret / gunner-driven gun) should
        // engage from its enabled channels. Per-channel gating mirrors the old single-lock path:
        // the captain's IFF override (CanCommandLockedFireAt) decides whether a locked
        // friendly/own-team target may be engaged at all.
        public static bool TrySelectSolution(ShipState ship, int mask, Vector3 muzzlePosition, float muzzleVelocity,
            Vector3 barrelForward, float maxRange, ArcCheck inArc, out FireSolution solution)
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

                FireSolution candidate;
                if (!ship.Captain.TryGetChannelFireSolution(channel, muzzlePosition, muzzleVelocity, out candidate))
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
        public static ITrackable SelectTarget(ShipState ship, int mask, Vector3 origin, Vector3 forward,
            float maxRange, int playerId, MPTeam team, ITrackable self)
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

                ITrackable candidate = ship.ChannelTargets[channel];
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
        // successor to "ReferenceEquals(ship.LockedTarget, target)" identity checks.
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
            return false;
        }
    }
}
