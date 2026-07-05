using Modding;
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

    internal static class SpaceEffectAssets
    {
        private static GameObject pierceEffectPrefab;
        private static bool pierceLoadAttempted;

        private static GameObject PierceEffectPrefab
        {
            get
            {
                if (pierceEffectPrefab == null && !pierceLoadAttempted)
                {
                    pierceLoadAttempted = true;
                    pierceEffectPrefab = ModResource.GetAssetBundle("space-perice").LoadAsset<GameObject>("Perice");
                }
                return pierceEffectPrefab;
            }
        }

        public static void PlayPierceEffect(Vector3 point, float caliber)
        {
            GameObject prefab = PierceEffectPrefab;
            if (prefab == null)
            {
                return;
            }

            GameObject pierceEffect = (GameObject)Object.Instantiate(prefab, point, Quaternion.identity);
            pierceEffect.transform.localScale = Vector3.one * Mathf.Max(0.1f, caliber / 400f);
            Object.Destroy(pierceEffect, 1f);

            AudioSource audioSource = pierceEffect.AddComponent<AudioSource>();
            audioSource.clip = ModResource.GetAudioClip("BS Migrated GunPierce Audio");
            audioSource.spatialBlend = 1f;
            audioSource.volume = Mathf.Clamp01(caliber / 1000f);
            audioSource.rolloffMode = AudioRolloffMode.Linear;
            audioSource.maxDistance = 200f;
            audioSource.Play();
        }

        public static void PlayMuzzleSound(Transform origin, float caliber)
        {
            GameObject sound = new GameObject("BeyondSpider Railgun Muzzle Sound");
            sound.transform.position = origin.position;
            AudioSource audioSource = sound.AddComponent<AudioSource>();
            audioSource.clip = ModResource.GetAudioClip("BS Migrated GunShot Audio");
            audioSource.spatialBlend = 1f;
            audioSource.volume = Mathf.Clamp01(caliber / 1000f);
            audioSource.rolloffMode = AudioRolloffMode.Linear;
            audioSource.maxDistance = 500f;
            audioSource.Play();
            Object.Destroy(sound, 3f);
        }
    }

    public class SpaceKineticRound : MonoBehaviour, ITrackable
    {
        public int OwnerPlayerID;
        public MPTeam OwnerTeam;
        public float Damage = 100f;
        public float Lifetime = 8f;
        public float RadiusValue = 0.45f;
        public float MassEstimate;
        public float Caliber;
        public bool SpawnImpactSpark;
        public bool UseRaycastDetection;

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
                return;
            }

            if (UseRaycastDetection && body != null)
            {
                float sweepDistance = body.velocity.magnitude * Time.fixedDeltaTime;
                if (sweepDistance > 0f)
                {
                    RaycastHit hit;
                    Ray ray = new Ray(transform.position, body.velocity);
                    if (Physics.Raycast(ray, out hit, sweepDistance))
                    {
                        ResolveHit(hit.collider);
                    }
                }
            }
        }

        private void OnCollisionEnter(Collision collision)
        {
            ResolveHit(collision.collider);
        }

        private void ResolveHit(Collider hitCollider)
        {
            HeavyNuclearMissileBlock missile = hitCollider.GetComponentInParent<HeavyNuclearMissileBlock>();
            if (missile != null && missile.PlayerID != OwnerPlayerID)
            {
                missile.ApplyDamage(Damage);
            }
            DamageRouter.RoutePhysicalHit(hitCollider, Damage);
            if (SpawnImpactSpark)
            {
                SpaceEffectAssets.PlayPierceEffect(transform.position, Caliber);
            }
            Destroy(gameObject);
        }

        public void ApplyShieldEffect(float power)
        {
            if (body == null || power <= 0f)
            {
                return;
            }
            if (power > 1.5f)
            {
                Destroy(gameObject);
                return;
            }
            body.velocity *= Mathf.Lerp(1f, 0.35f, Mathf.Clamp01(power));
        }

        private void OnDestroy()
        {
            SpaceCombatRegistry.UnregisterTrackable(this);
        }
    }
}
