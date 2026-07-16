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
            // a large overspeed commands retrograde braking.
            float closingSpeed = Vector3.Dot(body.velocity - targetVelocity, rHat);
            float speedError = p.ClosingSpeedCap - closingSpeed;
            float aLong;
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

            Vector3 command = aLat + aLong * rHat;
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

        // Proximity / closest-approach fuze (ADR-0008). Detonate when inside the hard proximity
        // radius (plus the target's own radius), or — once inside the arming radius — the instant the
        // range stops shrinking (range rate R.Vr turns non-negative), which catches a fast fly-by
        // that would otherwise tunnel through the proximity sphere between ticks.
        public static bool FuzeTriggered(Vector3 position, Vector3 velocity, Vector3 targetPosition, Vector3 targetVelocity, float targetRadius, float fuzeRadius, float armingRadius)
        {
            Vector3 R = targetPosition - position;
            float range = R.magnitude;
            if (range <= fuzeRadius + targetRadius)
            {
                return true;
            }
            if (range <= armingRadius)
            {
                Vector3 vr = targetVelocity - velocity;
                if (Vector3.Dot(R, vr) >= 0f)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
