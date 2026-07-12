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

        private const float ProjectileVisualScale = 3f;

        // Single spawn path for every physical cannon shell (railgun slugs and single-block
        // flak-turret large rounds alike) — same mesh, same raycast hit detection, same
        // railgun-style tail glow, and no drag (rb.drag is always 0; real projectiles don't
        // decelerate in the vacuum this mod's ships fight in). Callers still supply their own
        // damage/mass/lifetime numbers since those are weapon-specific balance, not shared
        // mechanics.
        public static void SpawnKineticRound(
            Vector3 position,
            Vector3 forward,
            Vector3 velocity,
            float caliber,
            float damage,
            float velocityDamageCoefficient,
            float mass,
            float lifetime,
            int ownerPlayerId,
            MPTeam ownerTeam,
            float muzzleVelocity)
        {
            GameObject round = new GameObject("BeyondSpider Cannon Round");
            round.transform.position = position;
            round.transform.rotation = Quaternion.LookRotation(forward, Vector3.up);

            Rigidbody rb = round.AddComponent<Rigidbody>();
            rb.interpolation = RigidbodyInterpolation.Extrapolate;
            rb.mass = mass;
            rb.drag = 0f;
            rb.useGravity = false;
            rb.velocity = velocity;

            GameObject vis = new GameObject("CannonVis");
            vis.transform.SetParent(round.transform);
            vis.transform.localPosition = Vector3.zero;
            vis.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            vis.transform.localScale = Vector3.one * caliber / 500f * ProjectileVisualScale;
            MeshFilter meshFilter = vis.AddComponent<MeshFilter>();
            meshFilter.sharedMesh = ModResource.GetMesh("Cannon Mesh").Mesh;
            MeshRenderer meshRenderer = vis.AddComponent<MeshRenderer>();
            meshRenderer.material.mainTexture = ModResource.GetTexture("Cannon Texture").Texture;

            SpaceKineticRound projectile = round.AddComponent<SpaceKineticRound>();
            projectile.OwnerPlayerID = ownerPlayerId;
            projectile.OwnerTeam = ownerTeam;
            projectile.Damage = damage;
            projectile.VelocityDamageCoefficient = velocityDamageCoefficient;
            projectile.Lifetime = lifetime;
            projectile.MassEstimate = rb.mass;
            projectile.Caliber = caliber;
            projectile.RadiusValue = Mathf.Clamp(caliber / 100f * ProjectileVisualScale, 1.2f, 7.5f);
            projectile.SpawnImpactSpark = true;
            projectile.UseRaycastDetection = true;

            SpaceEffectAssets.AttachRailgunTailGlow(round.transform, rb, caliber, muzzleVelocity, ProjectileVisualScale);
            SpaceEffectAssets.PlayMuzzleSound(position, caliber);

            Object.Destroy(round, lifetime + 0.2f);
        }
    }

    internal static class SpaceEffectAssets
    {
        private static GameObject pierceEffectPrefab;
        private static bool pierceLoadAttempted;
        private static GameObject cannonLightPrefab;
        private static bool cannonLightLoadAttempted;

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

        private static GameObject CannonLightPrefab
        {
            get
            {
                if (cannonLightPrefab == null && !cannonLightLoadAttempted)
                {
                    cannonLightLoadAttempted = true;
                    cannonLightPrefab = ModResource.GetAssetBundle("space-gunsmoke").LoadAsset<GameObject>("CannonLight");
                }
                return cannonLightPrefab;
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
            PlayMuzzleSound(origin.position, caliber);
        }

        public static void PlayMuzzleSound(Vector3 point, float caliber)
        {
            GameObject sound = new GameObject("BeyondSpider Railgun Muzzle Sound");
            sound.transform.position = point;
            AudioSource audioSource = sound.AddComponent<AudioSource>();
            audioSource.clip = ModResource.GetAudioClip("BS Migrated GunShot Audio");
            audioSource.spatialBlend = 1f;
            audioSource.volume = Mathf.Clamp01(caliber / 1000f);
            audioSource.rolloffMode = AudioRolloffMode.Linear;
            audioSource.maxDistance = 500f;
            audioSource.Play();
            Object.Destroy(sound, 3f);
        }

        // visualScale must be the same ProjectileVisualScale multiplier SpawnKineticRound
        // applies to the shell mesh — the glow's anchor and the CannonLight flare are
        // positioned/sized off the same caliber terms as the mesh, so if this ever drifts
        // out of sync with the mesh's own scale the flare stops lining up with the shell.
        public static void AttachRailgunTailGlow(Transform round, Rigidbody body, float caliber, float maxSpeed, float visualScale)
        {
            if (round == null || body == null)
            {
                return;
            }

            GameObject glow = new GameObject("Railgun Velocity Tail Glow");
            glow.transform.SetParent(round);
            glow.transform.localPosition = new Vector3(0f, 0f, -Mathf.Max(0.15f, caliber / 220f * visualScale));
            glow.transform.localRotation = Quaternion.identity;
            glow.transform.localScale = Vector3.one;

            GameObject prefab = CannonLightPrefab;
            if (prefab != null)
            {
                GameObject cannonLight = (GameObject)Object.Instantiate(prefab);
                cannonLight.transform.SetParent(glow.transform);
                cannonLight.transform.localPosition = Vector3.zero;
                cannonLight.transform.localRotation = Quaternion.identity;
                cannonLight.transform.localScale = Vector3.one * Mathf.Clamp(caliber / 120f * visualScale, 0.6f, 3.5f);
            }

            RailgunTailGlow tailGlow = glow.AddComponent<RailgunTailGlow>();
            tailGlow.Body = body;
            tailGlow.Caliber = caliber;
            tailGlow.MaxSpeed = maxSpeed;
        }
    }

    public class RailgunTailGlow : MonoBehaviour
    {
        public Rigidbody Body;
        public float Caliber;
        public float MaxSpeed;

        private ParticleSystem particles;
        private Light pointLight;
        private ParticleSystem[] childParticles;
        private Light[] childLights;

        private void Awake()
        {
            particles = gameObject.AddComponent<ParticleSystem>();
            particles.playOnAwake = true;
            particles.loop = true;
            particles.gravityModifier = 0f;
            particles.startLifetime = 0.16f;
            particles.startSpeed = 0.25f;
            particles.startSize = Mathf.Clamp(Caliber / 160f, 0.25f, 2.8f);
            particles.simulationSpace = ParticleSystemSimulationSpace.World;

            ParticleSystem.EmissionModule emission = particles.emission;
            emission.rate = Mathf.Clamp(Caliber * 0.75f, 45f, 180f);

            ParticleSystemRenderer renderer = gameObject.GetComponent<ParticleSystemRenderer>();
            if (renderer != null)
            {
                renderer.material = new Material(Shader.Find("Particles/Additive"));
            }

            pointLight = gameObject.AddComponent<Light>();
            pointLight.type = LightType.Point;
            pointLight.range = Mathf.Clamp(Caliber / 20f, 4f, 18f);
            pointLight.intensity = 1.4f;

            childParticles = GetComponentsInChildren<ParticleSystem>();
            childLights = GetComponentsInChildren<Light>();
        }

        private void FixedUpdate()
        {
            if (Body == null)
            {
                Destroy(gameObject);
                return;
            }

            float speed = Body.velocity.magnitude;
            if (speed > 0.1f && transform.parent != null)
            {
                transform.parent.rotation = Quaternion.LookRotation(Body.velocity.normalized);
            }

            Color color = VelocityColor(speed);
            if (childParticles != null)
            {
                float size = Mathf.Clamp(Caliber / 160f, 0.25f, 2.8f) * Mathf.Lerp(0.75f, 1.4f, SpeedRatio(speed));
                for (int i = 0; i < childParticles.Length; i++)
                {
                    if (childParticles[i] == null)
                    {
                        continue;
                    }
                    childParticles[i].startColor = color;
                    childParticles[i].startSize = size;
                }
            }
            if (childLights != null)
            {
                for (int i = 0; i < childLights.Length; i++)
                {
                    if (childLights[i] == null)
                    {
                        continue;
                    }
                    childLights[i].color = color;
                    childLights[i].intensity = Mathf.Lerp(0.7f, 2.2f, SpeedRatio(speed));
                    childLights[i].range = Mathf.Clamp(Caliber / 20f, 4f, 18f);
                }
            }
        }

        private Color VelocityColor(float speed)
        {
            float ratio = SpeedRatio(speed);
            Color slow = new Color(1f, 0.32f, 0.06f, 0.85f);
            Color mid = new Color(0.95f, 0.95f, 0.25f, 0.95f);
            Color fast = new Color(0.18f, 0.86f, 1f, 1f);
            return ratio < 0.5f
                ? Color.Lerp(slow, mid, ratio * 2f)
                : Color.Lerp(mid, fast, (ratio - 0.5f) * 2f);
        }

        private float SpeedRatio(float speed)
        {
            return Mathf.InverseLerp(900f, Mathf.Max(1200f, MaxSpeed), speed);
        }
    }

    public class SpaceKineticRound : MonoBehaviour, ITrackable
    {
        public int OwnerPlayerID;
        public MPTeam OwnerTeam;
        public float Damage = 100f;
        public float VelocityDamageCoefficient;
        public float Lifetime = 8f;
        public float RadiusValue = 0.45f;
        public float MassEstimate;
        public float Caliber;
        public bool SpawnImpactSpark;
        public bool UseRaycastDetection;

        public float CurrentDamage
        {
            get
            {
                if (VelocityDamageCoefficient <= 0f)
                {
                    return Damage;
                }
                float speed = Velocity.magnitude;
                return Damage + MassEstimate * speed * speed * VelocityDamageCoefficient;
            }
        }

        private const float RoundStallSpeed = 40f;

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
                    transform.rotation = Quaternion.LookRotation(body.velocity.normalized, Vector3.up);
                    RaycastHit hit;
                    Ray ray = new Ray(transform.position, transform.forward);
                    if (Physics.Raycast(ray, out hit, sweepDistance))
                    {
                        ResolveHit(hit.collider, hit.point);
                    }
                }
            }
        }

        private void OnCollisionEnter(Collision collision)
        {
            Vector3 hitPoint = transform.position;
            if (collision.contacts != null && collision.contacts.Length > 0)
            {
                hitPoint = collision.contacts[0].point;
            }
            ResolveHit(collision.collider, hitPoint);
        }

        private void ResolveHit(Collider hitCollider, Vector3 hitPoint)
        {
            float damage = CurrentDamage;
            HeavyNuclearMissileBlock missile = hitCollider.GetComponentInParent<HeavyNuclearMissileBlock>();
            if (missile != null && missile.PlayerID != OwnerPlayerID)
            {
                missile.ApplyDamage(damage);
            }
            DamageRouter.RoutePhysicalHit(hitCollider, damage);
            if (SpawnImpactSpark)
            {
                SpaceEffectAssets.PlayPierceEffect(hitPoint, Caliber);
            }
            Destroy(gameObject);
        }

        public void ApplyShieldDeceleration(Vector3 newVelocity, float appliedDeltaV)
        {
            if (body == null || appliedDeltaV <= 0f)
            {
                return;
            }
            body.transform.position += body.velocity * Time.fixedDeltaTime;
            body.velocity = newVelocity;
            if (newVelocity.magnitude < RoundStallSpeed)
            {
                Destroy(gameObject);
            }
        }

        private void OnDestroy()
        {
            SpaceCombatRegistry.UnregisterTrackable(this);
        }
    }
}
