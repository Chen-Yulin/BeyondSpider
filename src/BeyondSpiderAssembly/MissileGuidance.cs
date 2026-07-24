using UnityEngine;

namespace BeyondSpiderAssembly
{
    // Shared guidance for every guided munition (ADR-0008). Thrust acts ONLY along the missile's
    // nose (transform.forward) — a single rear nozzle — so steering means rotating the body, at a
    // finite turn rate, to point that nozzle where guidance wants it. Two channels are summed each
    // tick: true proportional navigation for the lateral (line-of-sight) channel, and a
    // closing-speed governor for the longitudinal channel that keeps the LOS-parallel closing speed
    // under a cap. The nozzle is bang-bang — full thrust or nothing.
    public static class MissileGuidance
    {
        public struct GuidanceParams
        {
            public float NavConstant;      // N, proportional-navigation gain
            public float GovernorGain;     // Kv (1/s), closing-speed regulation
            public float ClosingSpeedCap;  // Vcap (m/s) on the LOS-parallel closing speed
            public float MaxTurnRateDeg;   // attitude slew limit (deg/s)
            public float Thrust;           // nozzle force (N) when burning
            // Ships are large, slow-turning targets where a tight terminal turn radius (which the
            // cap buys, r = Vc^2/a_lat) matters more than raw closing speed. Shells and missiles are
            // fast, near-ballistic point targets where catching up matters more than a tidy turn
            // radius, so their closing speed is left uncapped (see the ship-only check at the call
            // site). When false, the governor never fades or brakes — see Steer().
            public bool LimitClosingSpeed;
            // Extra world-space acceleration folded straight into the steering command alongside
            // aLat/aLong — e.g. a repulsive bias away from the launching ship's own hull (see
            // BoxAvoidAccel) so a missile guiding onto a target behind its own ship arcs around the
            // hull instead of carving through it. Vector3.zero when there's nothing to avoid.
            public Vector3 AvoidAccel;
        }

        // Below this command magnitude the missile neither steers nor burns (coast).
        private const float CommandDeadband = 1f;
        // Burn only when the nose is within this cone of the command, so a missile mid-flip toward a
        // retrograde brake never thrusts sideways. cos(50 deg).
        private const float BurnConeCos = 0.6428f;
        // A small overshoot above the cap is coasted off rather than actively braked; only a genuine
        // overspeed beyond this band pitches the nozzle retrograde. Without it, bang-bang thrust
        // would flip the missile end-over-end chattering around the cap.
        private const float GovernorBrakeBand = 50f;

        // One tick of guidance: rotate the body toward the synthesized command at the turn-rate
        // limit and apply bang-bang nose thrust. Target position/velocity are world-frame. Returns
        // true when the nozzle fired this tick, so callers can drive an engine-glow effect.
        public static bool Steer(Transform tf, Rigidbody body, Vector3 targetPosition, Vector3 targetVelocity, GuidanceParams p)
        {
            if (tf == null || body == null)
            {
                return false;
            }

            Vector3 R = targetPosition - tf.position;
            float range = R.magnitude;
            if (range < 0.001f)
            {
                return false;
            }
            Vector3 rHat = R / range;
            Vector3 vr = targetVelocity - body.velocity;   // target relative to missile (PN convention)

            // Lateral channel: true proportional navigation. omega is the LOS rotation rate; the
            // command is perpendicular to the relative velocity and drives that rotation to zero.
            Vector3 omega = Vector3.Cross(R, vr) / (range * range);
            Vector3 aLat = p.NavConstant * Vector3.Cross(vr, omega);

            // Longitudinal channel: closing-speed governor on the LOS-parallel component. Below the
            // cap it commands prograde thrust (fading to zero at the cap); a small overshoot coasts;
            // a large overspeed commands retrograde braking. Uncapped targets (shells, missiles)
            // skip the fade/brake entirely and always push prograde at full strength, so the missile
            // keeps closing as hard as it can instead of throttling back near an arbitrary speed.
            float aLong;
            if (p.LimitClosingSpeed)
            {
                float closingSpeed = Vector3.Dot(body.velocity - targetVelocity, rHat);
                float speedError = p.ClosingSpeedCap - closingSpeed;
                if (speedError >= 0f)
                {
                    aLong = p.GovernorGain * speedError;
                }
                else if (speedError < -GovernorBrakeBand)
                {
                    aLong = p.GovernorGain * (speedError + GovernorBrakeBand);
                }
                else
                {
                    aLong = 0f;
                }
            }
            else
            {
                aLong = p.GovernorGain * p.ClosingSpeedCap;
            }

            Vector3 command = aLat + aLong * rHat + p.AvoidAccel;
            float commandMag = command.magnitude;
            Vector3 desiredDir = commandMag > 0.0001f ? command / commandMag : tf.forward;

            // Attitude: kinematic rate-limited slew toward the command direction.
            Vector3 upRef = Mathf.Abs(Vector3.Dot(desiredDir, Vector3.up)) > 0.99f ? Vector3.forward : Vector3.up;
            Quaternion want = Quaternion.LookRotation(desiredDir, upRef);
            tf.rotation = Quaternion.RotateTowards(tf.rotation, want, p.MaxTurnRateDeg * Time.fixedDeltaTime);

            // Thrust: bang-bang, gated by the command deadband and the burn cone.
            bool burning = commandMag > CommandDeadband && Vector3.Dot(tf.forward, desiredDir) > BurnConeCos;
            if (burning)
            {
                body.AddForce(tf.forward * p.Thrust, ForceMode.Force);
            }
            return burning;
        }

        // Repulsive potential field against an oriented box, used to steer a missile around an
        // obstacle (its own launching ship's hull) instead of straight through it. Returns a
        // world-space acceleration bias to fold into the steering command: zero beyond `margin`
        // past the box surface, pointing away from the nearest surface point and ramping up to
        // `gain` at the surface; when already inside the box (e.g. still clearing the launch rail)
        // it instead pushes out along whichever face is shallowest to reach, at full `gain`.
        public static Vector3 BoxAvoidAccel(Vector3 position, Vector3 boxCenter, Quaternion boxRotation, Vector3 halfSize, float margin, float gain)
        {
            if (gain <= 0f)
            {
                return Vector3.zero;
            }

            Quaternion toLocal = Quaternion.Inverse(boxRotation);
            Vector3 localPos = toLocal * (position - boxCenter);
            Vector3 clamped = new Vector3(
                Mathf.Clamp(localPos.x, -halfSize.x, halfSize.x),
                Mathf.Clamp(localPos.y, -halfSize.y, halfSize.y),
                Mathf.Clamp(localPos.z, -halfSize.z, halfSize.z));
            Vector3 outward = localPos - clamped;
            float outwardDist = outward.magnitude;

            Vector3 localDir;
            float strength;
            if (outwardDist > 0.0001f)
            {
                if (outwardDist >= margin)
                {
                    return Vector3.zero;
                }
                localDir = outward / outwardDist;
                strength = gain * (1f - outwardDist / margin);
            }
            else
            {
                float bestPenetration = float.MaxValue;
                localDir = Vector3.forward;
                for (int axis = 0; axis < 3; axis++)
                {
                    float penetration = halfSize[axis] - Mathf.Abs(localPos[axis]);
                    if (penetration < bestPenetration)
                    {
                        bestPenetration = penetration;
                        Vector3 push = Vector3.zero;
                        push[axis] = localPos[axis] >= 0f ? 1f : -1f;
                        localDir = push;
                    }
                }
                strength = gain;
            }

            return (boxRotation * localDir) * strength;
        }

        // Proximity fuze for point-defense engagements (a shell or another missile): the target is
        // small and fast enough that waiting for the contact collider to catch it is unreliable, so
        // this ALSO detonates the instant range drops inside the hard kill radius (fuzeRadius, plus
        // the target's own body radius) — not just on a miss. Once inside the arming radius, it
        // additionally self-destructs the instant range stops shrinking (range rate R.Vr turns
        // non-negative), which catches a fast fly-by that would otherwise tunnel through the kill
        // radius between ticks. Ship engagements use FlybyTriggered instead — see there for why.
        public static bool FuzeTriggered(Vector3 position, Vector3 velocity, Vector3 targetPosition, Vector3 targetVelocity, float targetRadius, float fuzeRadius, float armingRadius)
        {
            Vector3 R = targetPosition - position;
            float range = R.magnitude;
            if (range <= fuzeRadius + targetRadius)
            {
                return true;
            }
            return FlybyTriggered(position, velocity, targetPosition, targetVelocity, armingRadius);
        }

        // Miss-only self-destruct for ship engagements. A ship is a large, physically-solid target —
        // an actual direct hit is always caught by the missile's own contact collider (the sweep
        // raycast plus OnTriggerEnter) against the real hull, at the real hull surface, so this fuze
        // must NOT pre-empt a converging approach the way FuzeTriggered's hard-proximity term does
        // (that term, keyed to a target radius, was going off many meters before the visible hull on
        // any ship with a scaled-up core — see MissileProjectile history). This only fires once the
        // closest-approach point has been passed (closing rate flips to opening) while still inside
        // the arming radius, i.e. once it's clear the shot missed, so a defeated shot self-destructs
        // in the vicinity instead of coasting on forever.
        public static bool FlybyTriggered(Vector3 position, Vector3 velocity, Vector3 targetPosition, Vector3 targetVelocity, float armingRadius)
        {
            Vector3 R = targetPosition - position;
            float range = R.magnitude;
            if (range > armingRadius)
            {
                return false;
            }
            Vector3 vr = targetVelocity - velocity;
            return Vector3.Dot(R, vr) >= 0f;
        }
    }
}
