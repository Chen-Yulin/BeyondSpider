using UnityEngine;

namespace BeyondSpiderAssembly
{
    public static class SpaceBallistics
    {
        public static float EstimateInterceptTime(Vector3 shooter, Vector3 target, Vector3 targetVelocity, float muzzleVelocity)
        {
            Vector3 delta = target - shooter;
            float a = Vector3.Dot(targetVelocity, targetVelocity) - muzzleVelocity * muzzleVelocity;
            float b = 2f * Vector3.Dot(delta, targetVelocity);
            float c = Vector3.Dot(delta, delta);

            if (Mathf.Abs(a) < 0.001f)
            {
                return c > 0.001f ? Mathf.Max(0f, -c / Mathf.Max(0.001f, b)) : 0f;
            }

            float discriminant = b * b - 4f * a * c;
            if (discriminant < 0f)
            {
                return delta.magnitude / Mathf.Max(1f, muzzleVelocity);
            }

            float root = Mathf.Sqrt(discriminant);
            float t0 = (-b - root) / (2f * a);
            float t1 = (-b + root) / (2f * a);
            float best = 999f;
            if (t0 > 0f)
            {
                best = t0;
            }
            if (t1 > 0f && t1 < best)
            {
                best = t1;
            }
            return best == 999f ? delta.magnitude / Mathf.Max(1f, muzzleVelocity) : best;
        }

        public static Vector3 AimPoint(Vector3 shooter, SensorTrack track, float muzzleVelocity)
        {
            float time = EstimateInterceptTime(shooter, track.Position, track.Velocity, muzzleVelocity);
            return track.Position + track.Velocity * Mathf.Clamp(time, 0f, 8f);
        }

        public static bool IsHostile(SpaceBlock block, ITrackable target)
        {
            return block != null && target != null && target.PlayerID != block.PlayerID && target.Team != block.Team;
        }
    }

    public class SpaceKineticRound : MonoBehaviour, ITrackable
    {
        public int OwnerPlayerID;
        public MPTeam OwnerTeam;
        public float Damage = 100f;
        public float Lifetime = 8f;
        public float RadiusValue = 0.45f;

        private Rigidbody body;
        private float spawnTime;

        public int PlayerID { get { return OwnerPlayerID; } }
        public MPTeam Team { get { return OwnerTeam; } }
        public TrackKind Kind { get { return TrackKind.LargeProjectile; } }
        public Vector3 Position { get { return transform.position; } }
        public Vector3 Velocity { get { return body == null ? Vector3.zero : body.velocity; } }
        public float Radius { get { return RadiusValue; } }
        public bool IsAlive { get { return gameObject.activeSelf && Time.time - spawnTime < Lifetime; } }

        private void Awake()
        {
            body = GetComponent<Rigidbody>();
            spawnTime = Time.time;
            SpaceCombatRegistry.RegisterTrackable(this);
        }

        private void FixedUpdate()
        {
            if (Time.time - spawnTime > Lifetime)
            {
                Destroy(gameObject);
            }
        }

        private void OnCollisionEnter(Collision collision)
        {
            HeavyNuclearMissileBlock missile = collision.collider.GetComponentInParent<HeavyNuclearMissileBlock>();
            if (missile != null && missile.PlayerID != OwnerPlayerID)
            {
                missile.ApplyDamage(Damage);
            }
            Destroy(gameObject);
        }

        private void OnDestroy()
        {
            SpaceCombatRegistry.UnregisterTrackable(this);
        }
    }
}
