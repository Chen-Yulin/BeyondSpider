using System.Collections.Generic;
using Modding;
using UnityEngine;

namespace BeyondSpiderAssembly
{
    public static class SpaceBallistics
    {
        // targetVelocity and shooterVelocity are both world velocities; the intercept is solved in
        // the shooter's frame (relative velocity = targetVelocity - shooterVelocity) because a fired
        // round inherits the shooter's own velocity — see docs/adr/0006-relative-velocity-after-inheritance.md.
        // Pass Vector3.zero for shooterVelocity to solve in the world frame (e.g. a pure flight-time
        // estimate to a fixed point).
        public static float EstimateInterceptTime(Vector3 shooter, Vector3 target, Vector3 targetVelocity, float muzzleVelocity, Vector3 shooterVelocity)
        {
            Vector3 delta = target - shooter;
            Vector3 relativeVelocity = targetVelocity - shooterVelocity;
            float a = Vector3.Dot(relativeVelocity, relativeVelocity) - muzzleVelocity * muzzleVelocity;
            float b = 2f * Vector3.Dot(delta, relativeVelocity);
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

        public static Vector3 AimPoint(Vector3 shooter, SensorTrack track, float muzzleVelocity, Vector3 shooterVelocity)
        {
            float time = EstimateInterceptTime(shooter, track.Position, track.Velocity, muzzleVelocity, shooterVelocity);
            // Extrapolate along the target's velocity RELATIVE to the shooter, so the barrel — which
            // also carries the shooter's velocity — ends up pointing along the true intercept.
            return track.Position + (track.Velocity - shooterVelocity) * Mathf.Clamp(time, 0f, 8f);
        }

        public static bool IsHostile(SpaceBlock block, ITrackable target)
        {
            return block != null && target != null && target.PlayerID != block.PlayerID && target.Team != block.Team;
        }

        private const float ProjectileVisualScale = 0.5f;

        // Single spawn path for every physical cannon shell (railgun slugs and single-block
        // flak-turret large rounds alike) — same mesh, same raycast penetration, same railgun-style
        // tail glow, and no drag (rb.drag is always 0; real projectiles don't decelerate in the
        // vacuum this mod's ships fight in). Every round is now a pure kinetic penetrator (ADR-0007):
        // its damage budget is mass*v_rel^2*coeff with no flat floor, so callers only supply the KE
        // coefficient, mass and lifetime.
        public static void SpawnKineticRound(
            Vector3 position,
            Vector3 forward,
            Vector3 velocity,
            float caliber,
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
            projectile.VelocityDamageCoefficient = velocityDamageCoefficient;
            projectile.Lifetime = lifetime;
            projectile.MassEstimate = rb.mass;
            projectile.Caliber = caliber;
            projectile.RadiusValue = Mathf.Clamp(caliber / 100f * ProjectileVisualScale, 1.2f, 7.5f);
            projectile.SpawnImpactSpark = true;

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
        // applies to the shell mesh — the CannonLight flare is sized off the same caliber
        // terms as the mesh, so if this ever drifts out of sync with the mesh's own scale
        // the flare stops lining up with the shell.
        //
        // The shell mesh's own local origin IS its tail center (CannonVis sits at round's
        // local zero with no offset), so the glow anchors at round's local zero too — no
        // backward Z offset. An earlier version pushed it back by caliber/220*visualScale
        // on the assumption the origin was the mesh's center, which shot the glow out past
        // the actual tail (worse the larger the caliber).
        public static void AttachRailgunTailGlow(Transform round, Rigidbody body, float caliber, float maxSpeed, float visualScale)
        {
            if (round == null || body == null)
            {
                return;
            }

            GameObject glow = new GameObject("Railgun Velocity Tail Glow");
            glow.transform.SetParent(round);
            glow.transform.localPosition = Vector3.zero;
            glow.transform.localRotation = Quaternion.identity;
            glow.transform.localScale = Vector3.one;

            GameObject prefab = CannonLightPrefab;
            if (prefab != null)
            {
                GameObject cannonLight = (GameObject)Object.Instantiate(prefab);
                cannonLight.transform.SetParent(glow.transform);
                cannonLight.transform.localPosition = Vector3.zero;
                cannonLight.transform.localRotation = Quaternion.identity;
                cannonLight.transform.localScale = Vector3.one * Mathf.Clamp(caliber / 120f * visualScale, 0.6f, 10f);
            }

            RailgunTailGlow tailGlow = glow.AddComponent<RailgunTailGlow>();
            tailGlow.Body = body;
            tailGlow.Caliber = caliber;
            tailGlow.MaxSpeed = maxSpeed;
            // Explicit Initialize instead of Awake: Awake fires inside AddComponent, before the
            // three fields above are assigned, so anything sized off Caliber in Awake reads 0.
            tailGlow.Initialize();
        }

        // Vacuum warhead detonation (visual counterpart of ApplyBlastDamageToArmor's numbers).
        // Everything about it is deliberately "space", the opposite of Besiege's stock ground
        // explosions:
        //  - zero gravity and zero drag on every layer (gravityModifier 0, no velocity limiting), so
        //    nothing rises, mushrooms or slows — debris and gas fly perfectly straight at the one
        //    velocity they were born with;
        //  - free molecular expansion: each gas parcel gets a single radial velocity and keeps it, so
        //    the cloud's radius grows linearly with time while colorOverLifetime cools it white-hot →
        //    orange → dull red → black (on an additive shader that fade-to-black reads as the gas
        //    rarefying into vacuum, not as smoke dispersing on wind);
        //  - momentum is conserved: all layers simulate in LOCAL space under a root that
        //    MissileBlastDrift moves at the missile's final velocity, so a missile killed at speed
        //    leaves a fireball streaking on along its old trajectory instead of hanging at the
        //    detonation point;
        //  - no audio: vacuum carries no sound.
        // Sized off the same warhead charge the damage model uses, so the visible cloud roughly fills
        // SpaceBalance.MissileBlastRadius(charge). Runs on host and clients alike (each machine's own
        // missile copy detonates locally), purely cosmetic.
        public static void PlayMissileBlast(Vector3 position, Vector3 driftVelocity, float warheadCharge)
        {
            float blastRadius = SpaceBalance.MissileBlastRadius(warheadCharge);

            GameObject root = new GameObject("BeyondSpider Missile Blast");
            root.transform.position = position;
            root.transform.rotation = Quaternion.identity;

            ParticleSystem.EmitParams emit = new ParticleSystem.EmitParams();

            // Layer 1 — detonation flash: a few white-hot billboards blooming at the centre for a
            // fraction of a second. sizeOverLifetime does the blooming since the particles themselves
            // barely move.
            ParticleSystem flash = CreateBlastLayer(root.transform, "Flash", false, FlashFade());
            ParticleSystem.SizeOverLifetimeModule flashSize = flash.sizeOverLifetime;
            flashSize.enabled = true;
            flashSize.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0.45f, 1f, 1.8f));
            for (int i = 0; i < 3; i++)
            {
                emit.velocity = Random.insideUnitSphere * blastRadius * 0.05f;
                emit.startLifetime = Random.Range(0.16f, 0.26f);
                emit.startSize = blastRadius * Random.Range(0.35f, 0.55f);
                emit.startColor = new Color(1f, 0.96f, 0.85f);
                flash.Emit(emit, 1);
            }

            // Layer 2 — expanding gas cloud: an isotropic ball of parcels on constant radial
            // velocities (speeds spread from near-still to full so the sphere fills rather than
            // reading as a hollow soap-bubble shell). The fastest parcels reach roughly the damage
            // radius by end of life; sizeOverLifetime swells each parcel as its gas rarefies.
            ParticleSystem gas = CreateBlastLayer(root.transform, "Gas", false, GasFade());
            ParticleSystem.SizeOverLifetimeModule gasSize = gas.sizeOverLifetime;
            gasSize.enabled = true;
            gasSize.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0.6f, 1f, 2.8f));
            int gasCount = Mathf.Clamp(Mathf.RoundToInt(30f + warheadCharge * 0.9f), 40, 200);
            float gasSpeed = blastRadius * 0.45f;
            for (int i = 0; i < gasCount; i++)
            {
                emit.velocity = Random.onUnitSphere * gasSpeed * Random.Range(0.12f, 1f);
                emit.startLifetime = Random.Range(1.3f, 2.6f);
                emit.startSize = blastRadius * Random.Range(0.09f, 0.2f);
                emit.startColor = Color.Lerp(new Color(1f, 0.85f, 0.55f), new Color(1f, 0.5f, 0.2f), Random.value);
                gas.Emit(emit, 1);
            }

            // Layer 3 — debris sparks: fast stretch-rendered streaks that overtake the gas and keep
            // going in dead-straight lines — the clearest "no drag, no gravity" tell in the whole
            // effect.
            ParticleSystem sparks = CreateBlastLayer(root.transform, "Sparks", true, SparkFade());
            int sparkCount = Mathf.Clamp(Mathf.RoundToInt(8f + warheadCharge * 0.28f), 10, 56);
            float sparkSpeed = blastRadius * 1.1f;
            float sparkSizeScale = Mathf.Clamp(blastRadius / 28f, 0.8f, 2.2f);
            for (int i = 0; i < sparkCount; i++)
            {
                emit.velocity = Random.onUnitSphere * sparkSpeed * Random.Range(0.55f, 1f);
                emit.startLifetime = Random.Range(0.7f, 1.6f);
                emit.startSize = Random.Range(0.25f, 0.5f) * sparkSizeScale;
                emit.startColor = new Color(1f, 0.9f, 0.6f);
                sparks.Emit(emit, 1);
            }

            Light light = root.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = new Color(1f, 0.72f, 0.4f);
            light.range = Mathf.Clamp(blastRadius * 1.4f, 12f, 110f);
            light.intensity = 5f;

            MissileBlastDrift drift = root.AddComponent<MissileBlastDrift>();
            drift.Velocity = driftVelocity;
            drift.FlashLight = light;

            // Backstop teardown just past the longest gas lifetime; the drift script tears it down
            // sooner if the simulation ends first.
            Object.Destroy(root, 3.2f);
        }

        // Reactor cook-off (damage-refinement spec: "similar to the missile explosion but a bigger
        // scale, with lightning"). Same vacuum layer recipe as PlayMissileBlast — flash, expanding
        // gas, debris sparks, all momentum-conserving under MissileBlastDrift — scaled up off the
        // core's own totalPower instead of a warhead charge, plus a dense corona of arcing bolts
        // (SpawnLightningBurst) that's the one element a plain warhead blast never had. Runs on
        // host and clients alike (each machine plays its own local copy off the same
        // position/velocity/scale via SubsystemDetonationNet); the real damage is
        // SubsystemDetonation.ApplySecondaryBlast, host-only.
        public static void PlayReactorExplosion(Vector3 position, Vector3 driftVelocity, float totalPower)
        {
            float blastRadius = SpaceBalance.ReactorBlastRadius(totalPower);

            GameObject root = new GameObject("BeyondSpider Reactor Blast");
            root.transform.position = position;
            root.transform.rotation = Quaternion.identity;

            ParticleSystem.EmitParams emit = new ParticleSystem.EmitParams();

            ParticleSystem flash = CreateBlastLayer(root.transform, "Flash", false, FlashFade());
            ParticleSystem.SizeOverLifetimeModule flashSize = flash.sizeOverLifetime;
            flashSize.enabled = true;
            flashSize.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0.5f, 1f, 2.1f));
            for (int i = 0; i < 6; i++)
            {
                emit.velocity = Random.insideUnitSphere * blastRadius * 0.06f;
                emit.startLifetime = Random.Range(0.22f, 0.4f);
                emit.startSize = blastRadius * Random.Range(0.4f, 0.65f);
                emit.startColor = new Color(1f, 0.98f, 0.92f);
                flash.Emit(emit, 1);
            }

            ParticleSystem gas = CreateBlastLayer(root.transform, "Gas", false, GasFade());
            ParticleSystem.SizeOverLifetimeModule gasSize = gas.sizeOverLifetime;
            gasSize.enabled = true;
            gasSize.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0.65f, 1f, 3.1f));
            int gasCount = Mathf.Clamp(Mathf.RoundToInt(80f + totalPower * 0.05f), 100, 320);
            float gasSpeed = blastRadius * 0.5f;
            for (int i = 0; i < gasCount; i++)
            {
                emit.velocity = Random.onUnitSphere * gasSpeed * Random.Range(0.12f, 1f);
                emit.startLifetime = Random.Range(1.6f, 3.2f);
                emit.startSize = blastRadius * Random.Range(0.08f, 0.19f);
                emit.startColor = Color.Lerp(new Color(1f, 0.9f, 0.6f), new Color(0.65f, 0.6f, 1f), Random.value * 0.3f);
                gas.Emit(emit, 1);
            }

            ParticleSystem sparks = CreateBlastLayer(root.transform, "Sparks", true, SparkFade());
            int sparkCount = Mathf.Clamp(Mathf.RoundToInt(24f + totalPower * 0.018f), 30, 110);
            float sparkSpeed = blastRadius * 1.2f;
            float sparkSizeScale = Mathf.Clamp(blastRadius / 30f, 1f, 3.5f);
            for (int i = 0; i < sparkCount; i++)
            {
                emit.velocity = Random.onUnitSphere * sparkSpeed * Random.Range(0.55f, 1f);
                emit.startLifetime = Random.Range(0.9f, 2f);
                emit.startSize = Random.Range(0.3f, 0.6f) * sparkSizeScale;
                emit.startColor = new Color(1f, 0.9f, 0.65f);
                sparks.Emit(emit, 1);
            }

            // Lightning is the element that separates a reactor breach from an ordinary warhead: a
            // dense corona of arcs discharging outward as containment fails.
            SpawnLightningBurst(root.transform, blastRadius * 0.85f, 16, new Color(0.75f, 0.85f, 1f), 0.22f, 0.5f);

            Light light = root.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = new Color(0.85f, 0.9f, 1f);
            light.range = Mathf.Clamp(blastRadius * 1.6f, 20f, 220f);
            light.intensity = 8f;

            MissileBlastDrift drift = root.AddComponent<MissileBlastDrift>();
            drift.Velocity = driftVelocity;
            drift.FlashLight = light;
            drift.LightFadeSeconds = 1.2f;

            Object.Destroy(root, 4f);
        }

        // Capacitor bank discharge (damage-refinement spec: "particle effect emphasizing lightning
        // and ions scattering explosively"). Deliberately NOT a scaled-down fireball: a small hot
        // flash, a corona of arcing bolts denser than the reactor's own, and fast electric-blue
        // "ion" sparks flying outward, all scaled off the bank's own Capacity. Runs on host and
        // clients alike, same as PlayReactorExplosion.
        public static void PlayCapacitorExplosion(Vector3 position, Vector3 driftVelocity, float capacity)
        {
            float blastRadius = SpaceBalance.CapacitorBlastRadius(capacity);

            GameObject root = new GameObject("BeyondSpider Capacitor Blast");
            root.transform.position = position;
            root.transform.rotation = Quaternion.identity;

            ParticleSystem.EmitParams emit = new ParticleSystem.EmitParams();

            ParticleSystem flash = CreateBlastLayer(root.transform, "Flash", false, IonFlashFade());
            ParticleSystem.SizeOverLifetimeModule flashSize = flash.sizeOverLifetime;
            flashSize.enabled = true;
            flashSize.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0.5f, 1f, 1.6f));
            for (int i = 0; i < 3; i++)
            {
                emit.velocity = Random.insideUnitSphere * blastRadius * 0.05f;
                emit.startLifetime = Random.Range(0.12f, 0.2f);
                emit.startSize = blastRadius * Random.Range(0.3f, 0.5f);
                emit.startColor = new Color(0.85f, 0.95f, 1f);
                flash.Emit(emit, 1);
            }

            // Ions: fast, small, electric-blue sparks scattering radially — the "四散的离子" the user
            // asked for, distinct from the reactor's slower orange-white gas cloud.
            ParticleSystem ions = CreateBlastLayer(root.transform, "Ions", true, IonSparkFade());
            int ionCount = Mathf.Clamp(Mathf.RoundToInt(40f + capacity * 0.05f), 50, 220);
            float ionSpeed = blastRadius * 1.6f;
            for (int i = 0; i < ionCount; i++)
            {
                emit.velocity = Random.onUnitSphere * ionSpeed * Random.Range(0.4f, 1f);
                emit.startLifetime = Random.Range(0.35f, 0.9f);
                emit.startSize = Random.Range(0.12f, 0.28f);
                emit.startColor = Color.Lerp(new Color(0.6f, 0.85f, 1f), new Color(0.85f, 0.6f, 1f), Random.value * 0.4f);
                ions.Emit(emit, 1);
            }

            // Lightning dominates this effect — dense, bright, cyan-white, shorter-lived than the
            // reactor's (a capacitor discharges instantly, it doesn't keep burning).
            SpawnLightningBurst(root.transform, blastRadius, 20, new Color(0.7f, 0.9f, 1f), 0.1f, 0.28f);

            Light light = root.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = new Color(0.7f, 0.85f, 1f);
            light.range = Mathf.Clamp(blastRadius * 1.5f, 10f, 90f);
            light.intensity = 6f;

            MissileBlastDrift drift = root.AddComponent<MissileBlastDrift>();
            drift.Velocity = driftVelocity;
            drift.FlashLight = light;

            Object.Destroy(root, 1.5f);
        }

        // One-shot radiating lightning arcs shared by the reactor's and capacitor's detonation FX
        // (user spec: emphasize lightning). Unlike LaserFx's continuously re-aimed beam bolts, each
        // arc here is built once, crackles in place for its own short lifetime, then self-destructs.
        private static void SpawnLightningBurst(Transform parent, float radius, int boltCount, Color color, float lifetimeMin, float lifetimeMax)
        {
            for (int i = 0; i < boltCount; i++)
            {
                GameObject go = new GameObject("Lightning" + i);
                go.transform.SetParent(parent);
                go.transform.localPosition = Vector3.zero;
                go.transform.localRotation = Quaternion.identity;

                LineRenderer line = go.AddComponent<LineRenderer>();
                line.useWorldSpace = false;
                line.material = new Material(Shader.Find("Particles/Additive"));
                line.material.mainTexture = SoftDotTexture();

                DetonationLightningArc arc = go.AddComponent<DetonationLightningArc>();
                arc.EndPoint = Random.onUnitSphere * radius * Random.Range(0.55f, 1f);
                arc.BoltColor = color;
                arc.Lifetime = Random.Range(lifetimeMin, lifetimeMax);
                arc.Initialize();
            }
        }

        // Blue-white variants of FlashFade/SparkFade for the capacitor's ion-themed layers.
        private static Gradient IonFlashFade()
        {
            Gradient gradient = new Gradient();
            gradient.SetKeys(
                new GradientColorKey[]
                {
                    new GradientColorKey(Color.white, 0f),
                    new GradientColorKey(new Color(0.7f, 0.85f, 1f), 1f)
                },
                new GradientAlphaKey[]
                {
                    new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(0f, 1f)
                });
            return gradient;
        }

        private static Gradient IonSparkFade()
        {
            Gradient gradient = new Gradient();
            gradient.SetKeys(
                new GradientColorKey[]
                {
                    new GradientColorKey(new Color(0.85f, 0.95f, 1f), 0f),
                    new GradientColorKey(new Color(0.55f, 0.75f, 1f), 1f)
                },
                new GradientAlphaKey[]
                {
                    new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(0.7f, 0.4f),
                    new GradientAlphaKey(0f, 1f)
                });
            return gradient;
        }

        // One blast layer: a burst-only, manually-emitted ParticleSystem with the vacuum ground
        // rules baked in. Local simulation space is load-bearing — it's what lets MissileBlastDrift
        // move the root and carry every already-emitted particle with it (momentum drift); the flak
        // tracer needed the opposite (World) for the opposite reason.
        private static ParticleSystem CreateBlastLayer(Transform parent, string name, bool stretch, Gradient fade)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;

            ParticleSystem ps = go.AddComponent<ParticleSystem>();
            ps.playOnAwake = false;
            ps.loop = false;
            ps.gravityModifier = 0f;
            ps.simulationSpace = ParticleSystemSimulationSpace.Local;
            ParticleSystem.EmissionModule emission = ps.emission;
            emission.enabled = false;
            ParticleSystem.ShapeModule shape = ps.shape;
            shape.enabled = false;
            // No drag: emitted velocity is kept for the particle's whole life.
            ParticleSystem.LimitVelocityOverLifetimeModule limit = ps.limitVelocityOverLifetime;
            limit.enabled = false;
            ParticleSystem.ColorOverLifetimeModule color = ps.colorOverLifetime;
            color.enabled = true;
            color.color = new ParticleSystem.MinMaxGradient(fade);

            ParticleSystemRenderer renderer = go.GetComponent<ParticleSystemRenderer>();
            if (renderer != null)
            {
                renderer.material = new Material(Shader.Find("Particles/Additive"));
                renderer.material.mainTexture = SoftDotTexture();
                if (stretch)
                {
                    renderer.renderMode = ParticleSystemRenderMode.Stretch;
                    renderer.lengthScale = 4f;
                }
            }
            return ps;
        }

        // Radiative cooling: with no air, a hot gas cloud dims by radiating — white-hot through
        // orange to dull red before going dark. Alpha tracks the cloud thinning out.
        private static Gradient GasFade()
        {
            Gradient gradient = new Gradient();
            gradient.SetKeys(
                new GradientColorKey[]
                {
                    new GradientColorKey(new Color(1f, 0.97f, 0.9f), 0f),
                    new GradientColorKey(new Color(1f, 0.55f, 0.22f), 0.3f),
                    new GradientColorKey(new Color(0.45f, 0.14f, 0.07f), 0.75f),
                    new GradientColorKey(new Color(0.1f, 0.03f, 0.02f), 1f)
                },
                new GradientAlphaKey[]
                {
                    new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(0.55f, 0.35f),
                    new GradientAlphaKey(0f, 1f)
                });
            return gradient;
        }

        private static Gradient FlashFade()
        {
            Gradient gradient = new Gradient();
            gradient.SetKeys(
                new GradientColorKey[]
                {
                    new GradientColorKey(Color.white, 0f),
                    new GradientColorKey(new Color(1f, 0.8f, 0.5f), 1f)
                },
                new GradientAlphaKey[]
                {
                    new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(0f, 1f)
                });
            return gradient;
        }

        private static Gradient SparkFade()
        {
            Gradient gradient = new Gradient();
            gradient.SetKeys(
                new GradientColorKey[]
                {
                    new GradientColorKey(new Color(1f, 0.95f, 0.8f), 0f),
                    new GradientColorKey(new Color(1f, 0.55f, 0.25f), 1f)
                },
                new GradientAlphaKey[]
                {
                    new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(0.7f, 0.5f),
                    new GradientAlphaKey(0f, 1f)
                });
            return gradient;
        }

        // Soft radial dot shared by every blast layer. Quadratic alpha falloff (vs the flak tracer's
        // linear dot) so overlapping additive gas parcels blend into one cloud instead of reading as
        // individual balls.
        private static Texture2D softDotTexture;

        private static Texture2D SoftDotTexture()
        {
            if (softDotTexture == null)
            {
                const int size = 32;
                float radius = size * 0.5f;
                softDotTexture = new Texture2D(size, size, TextureFormat.ARGB32, false);
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        float dist = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(radius, radius));
                        float alpha = Mathf.Clamp01(1f - dist / radius);
                        softDotTexture.SetPixel(x, y, new Color(1f, 1f, 1f, alpha * alpha));
                    }
                }
                softDotTexture.Apply();
            }
            return softDotTexture;
        }

        // One shared additive soft-dot material for every round's wake/spark layers — per-round
        // colour comes from ParticleSystem.startColor, not the material, so instancing a material
        // per shot would only leak (materials are not destroyed with their renderer).
        private static Material softAdditiveMaterial;

        internal static Material SoftAdditiveMaterial()
        {
            if (softAdditiveMaterial == null)
            {
                softAdditiveMaterial = new Material(Shader.Find("Particles/Additive"));
                softAdditiveMaterial.mainTexture = SoftDotTexture();
            }
            return softAdditiveMaterial;
        }

        // Engine glow for a missile's single rear nozzle (ADR-0008): the same CannonLight flare and a
        // warm point light the cannon shell carries, but WITHOUT the world-space particle trail — a
        // motor flares, it doesn't streak. The returned component is toggled by the guidance each
        // tick via SetBurning, so the glow only shows while the nozzle is actually firing.
        public static MissileEngineGlow AttachMissileEngineGlow(Transform missile, float size, float tailOffset)
        {
            GameObject glow = new GameObject("Missile Engine Glow");
            glow.transform.SetParent(missile);
            // The round models are pivoted at the NOSE, so local zero is the tip and the body runs back
            // along -Z: the nozzle is a full body-length behind zero. A short fixed offset here (the old
            // -max(0.3, size*0.6), written for a centre-pivoted model) planted the flame on the nose
            // instead. The caller measures the real tail distance — see MissileLauncherAssets.TailOffset.
            glow.transform.localPosition = new Vector3(0f, 0f, -tailOffset);
            glow.transform.localRotation = Quaternion.identity;
            glow.transform.localScale = Vector3.one;

            Light light = glow.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = new Color(1f, 0.45f, 0.12f);
            light.range = Mathf.Clamp(size * 8f, 4f, 24f);
            light.intensity = 2.2f;

            GameObject prefab = CannonLightPrefab;
            if (prefab != null)
            {
                GameObject flare = (GameObject)Object.Instantiate(prefab);
                flare.transform.SetParent(glow.transform);
                flare.transform.localPosition = Vector3.zero;
                flare.transform.localRotation = Quaternion.identity;
                flare.transform.localScale = Vector3.one * Mathf.Clamp(size, 0.4f, 3f);
            }

            MissileEngineGlow engineGlow = glow.AddComponent<MissileEngineGlow>();
            engineGlow.Initialize();
            return engineGlow;
        }
    }

    // Carries a detonation cloud along at the missile's final velocity — with no air to brake it,
    // the fireball's centre of mass keeps the missile's momentum and streaks on down its old
    // trajectory (the particle layers simulate in local space under this root, so moving the root
    // moves every already-emitted particle uniformly). Also fades the flash light with a quadratic
    // decay, and — being a free-standing object like the missile itself — self-destructs the moment
    // the simulation ends (same StatMaster.levelSimulating idiom as MissileProjectile.Update).
    public class MissileBlastDrift : MonoBehaviour
    {
        public Vector3 Velocity;
        public Light FlashLight;
        public float LightFadeSeconds = 0.5f;

        private float age;
        private float baseIntensity = -1f;

        private void Update()
        {
            if (!StatMaster.levelSimulating)
            {
                Destroy(gameObject);
                return;
            }

            transform.position += Velocity * Time.deltaTime;
            age += Time.deltaTime;

            if (FlashLight != null)
            {
                if (baseIntensity < 0f)
                {
                    baseIntensity = FlashLight.intensity;
                }
                float k = Mathf.Clamp01(1f - age / LightFadeSeconds);
                FlashLight.enabled = k > 0.01f;
                FlashLight.intensity = baseIntensity * k * k;
            }
        }
    }

    // One-shot lightning arc built by SpawnLightningBurst: a jagged LineRenderer from the blast
    // center to a random EndPoint, redrawn with fresh per-segment jitter every frame for a crackle
    // effect (same jitter idea as LaserFx.RebuildBolt, but built once and short-lived rather than
    // continuously re-aimed along a moving beam), fading out and self-destructing after Lifetime.
    public class DetonationLightningArc : MonoBehaviour
    {
        public Vector3 EndPoint;
        public Color BoltColor;
        public float Lifetime = 0.28f;

        private const int Segments = 9;
        private const float JitterFactor = 0.22f;

        private LineRenderer line;
        private float timer;

        public void Initialize()
        {
            line = GetComponent<LineRenderer>();
            line.SetVertexCount(Segments + 1);
            Redraw();
        }

        private void Update()
        {
            timer += Time.deltaTime;
            if (timer >= Lifetime)
            {
                Destroy(gameObject);
                return;
            }
            Redraw();
        }

        private void Redraw()
        {
            float fade = 1f - timer / Lifetime;
            float length = EndPoint.magnitude;
            Vector3 dir = length > 0.001f ? EndPoint / length : Vector3.forward;
            Vector3 perpA = Vector3.Cross(dir, Vector3.up);
            if (perpA.sqrMagnitude < 0.0001f)
            {
                perpA = Vector3.Cross(dir, Vector3.right);
            }
            perpA.Normalize();
            Vector3 perpB = Vector3.Cross(dir, perpA);
            float jitterRadius = length * JitterFactor;

            for (int i = 0; i <= Segments; i++)
            {
                float t = (float)i / Segments;
                Vector3 point = EndPoint * t;
                if (i != 0 && i != Segments)
                {
                    // sin envelope keeps both ends pinned (origin at the blast center, tip at
                    // EndPoint) so the jitter reads as a bolt rather than a loose scribble.
                    float envelope = Mathf.Sin(t * Mathf.PI);
                    Vector3 jitter = (perpA * Random.Range(-1f, 1f) + perpB * Random.Range(-1f, 1f)) * jitterRadius * envelope;
                    point += jitter;
                }
                line.SetPosition(i, point);
            }

            float width = Mathf.Lerp(0.06f, 0.32f, Mathf.Clamp01(length / 12f)) * fade;
            line.SetWidth(width, width * 0.15f);
            line.SetColors(BoltColor * fade, BoltColor * fade * 0.35f);
        }
    }

    // Tracer visuals for a kinetic round, colour-coded by current speed (orange → yellow → cyan).
    // Two particle layers, both sized off caliber:
    //  - a soft ionised-wake ParticleSystem emitting per unit of DISTANCE flown. Per-time emission
    //    was the old "string of squares": at 1000+ u/s even the max rate left one blob every dozen
    //    units, and its material had no texture, so each blob rendered as a solid square (same
    //    lesson as SpaceFlakTurretBlock.RoundDotTexture);
    //  - stretch-rendered sparks shed off larger calibers.
    // A first iteration also had a TrailRenderer ribbon as the main streak; playtest verdict was
    // that the ionised wake alone reads better, so the ribbon was removed — don't reintroduce it
    // without asking.
    // Configured in Initialize, not Awake: AddComponent runs Awake before the caller can assign
    // Caliber/MaxSpeed, so anything sized in Awake reads Caliber = 0 (the old version's emission
    // rate was pinned at its clamp floor because of exactly that).
    public class RailgunTailGlow : MonoBehaviour
    {
        public Rigidbody Body;
        public float Caliber;
        public float MaxSpeed;

        // Playtest-tuned: the CannonLight flare billboard read three times too big at the shell's
        // tail, so its per-tick startSize gets this factor on top of the shared wake size formula.
        private const float FlareSizeScale = 1f / 10f;

        private ParticleSystem wake;
        private ParticleSystem sparks;
        private ParticleSystem[] flareParticles;
        private Light[] childLights;
        private float baseWakeSize;

        public void Initialize()
        {
            // Captured before the wake/spark layers are added: these are the CannonLight prefab's
            // own systems, which keep the old uniform colour/size treatment in FixedUpdate.
            flareParticles = GetComponentsInChildren<ParticleSystem>();

            baseWakeSize = Mathf.Clamp(Caliber / 160f, 0.25f, 2.8f);
            wake = CreateWakeLayer();
            if (Caliber >= 120f)
            {
                sparks = CreateSparkLayer();
            }

            Light pointLight = gameObject.AddComponent<Light>();
            pointLight.type = LightType.Point;
            pointLight.range = Mathf.Clamp(Caliber / 20f, 4f, 18f);
            pointLight.intensity = 1.4f;

            childLights = GetComponentsInChildren<Light>();
        }

        // Soft glow parcels left hanging along the flight path — hot gas dispersing, not a spray:
        // near-still isotropic drift, swelling and fading as it cools. World simulation space plus
        // distance-based emission keeps the wake density identical however fast the round flies
        // (and thins it out correctly when a shield bleeds the round's speed).
        private ParticleSystem CreateWakeLayer()
        {
            GameObject go = new GameObject("Wake");
            go.transform.SetParent(transform);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;

            ParticleSystem ps = go.AddComponent<ParticleSystem>();
            ps.playOnAwake = true;
            ps.loop = true;
            ps.gravityModifier = 0f;
            // Playtest-tuned down from 0.5s: with the ribbon gone this IS the visible trail, and
            // half a second of parcels per round was far too many live particles under fire.
            ps.startLifetime = 0.05f;
            ps.startSpeed = 0.4f;
            ps.startSize = baseWakeSize;
            ps.simulationSpace = ParticleSystemSimulationSpace.World;
            ps.scalingMode = ParticleSystemScalingMode.Shape;

            ParticleSystem.ShapeModule shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 0.05f;

            ParticleSystem.EmissionModule emission = ps.emission;
            emission.type = ParticleSystemEmissionType.Distance;
            emission.rate = Mathf.Clamp(0.4f / baseWakeSize, 0.1f, 0.6f);

            ParticleSystem.SizeOverLifetimeModule size = ps.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0.7f, 1f, 1.9f));

            ParticleSystem.ColorOverLifetimeModule color = ps.colorOverLifetime;
            color.enabled = true;
            color.color = new ParticleSystem.MinMaxGradient(AlphaFade(0.85f, 0.45f));

            ParticleSystemRenderer renderer = go.GetComponent<ParticleSystemRenderer>();
            if (renderer != null)
            {
                renderer.material = SpaceEffectAssets.SoftAdditiveMaterial();
            }
            return ps;
        }

        // Sparse hot fragments flicked sideways off the round — stretch-rendered so each reads as
        // a glint, not a dot. Distance-emitted like the wake, but far sparser.
        private ParticleSystem CreateSparkLayer()
        {
            GameObject go = new GameObject("Sparks");
            go.transform.SetParent(transform);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;

            ParticleSystem ps = go.AddComponent<ParticleSystem>();
            ps.playOnAwake = true;
            ps.loop = true;
            ps.gravityModifier = 0f;
            ps.startLifetime = 0.4f;
            ps.startSpeed = 5f;
            ps.startSize = Mathf.Clamp(Caliber / 700f, 0.12f, 0.7f);
            ps.simulationSpace = ParticleSystemSimulationSpace.World;
            ps.scalingMode = ParticleSystemScalingMode.Shape;

            ParticleSystem.ShapeModule shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 0.05f;

            ParticleSystem.EmissionModule emission = ps.emission;
            emission.type = ParticleSystemEmissionType.Distance;
            emission.rate = 0.06f;

            ParticleSystem.ColorOverLifetimeModule color = ps.colorOverLifetime;
            color.enabled = true;
            color.color = new ParticleSystem.MinMaxGradient(AlphaFade(1f, 0.6f));

            ParticleSystemRenderer renderer = go.GetComponent<ParticleSystemRenderer>();
            if (renderer != null)
            {
                renderer.material = SpaceEffectAssets.SoftAdditiveMaterial();
                renderer.renderMode = ParticleSystemRenderMode.Stretch;
                renderer.lengthScale = 6f;
            }
            return ps;
        }

        // White→transparent alpha ramp; colour stays neutral so the per-tick startColor
        // (velocity colour) is what actually paints the layer.
        private static Gradient AlphaFade(float startAlpha, float midAlpha)
        {
            Gradient gradient = new Gradient();
            gradient.SetKeys(
                new GradientColorKey[]
                {
                    new GradientColorKey(Color.white, 0f),
                    new GradientColorKey(Color.white, 1f)
                },
                new GradientAlphaKey[]
                {
                    new GradientAlphaKey(startAlpha, 0f),
                    new GradientAlphaKey(midAlpha, 0.4f),
                    new GradientAlphaKey(0f, 1f)
                });
            return gradient;
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

            float ratio = SpeedRatio(speed);
            Color color = VelocityColor(speed);
            float size = baseWakeSize * Mathf.Lerp(0.75f, 1.4f, ratio);

            if (wake != null)
            {
                Color wakeColor = color;
                wakeColor.a *= 0.6f;
                wake.startColor = wakeColor;
                wake.startSize = size;
            }
            if (sparks != null)
            {
                sparks.startColor = Color.Lerp(color, Color.white, 0.4f);
            }
            if (flareParticles != null)
            {
                for (int i = 0; i < flareParticles.Length; i++)
                {
                    if (flareParticles[i] == null)
                    {
                        continue;
                    }
                    flareParticles[i].startColor = color;
                    flareParticles[i].startSize = size * FlareSizeScale;
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
                    childLights[i].intensity = Mathf.Lerp(0.7f, 2.2f, ratio);
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

    // Toggleable exhaust glow for a missile motor (ADR-0008). Holds a burn flag the guidance sets
    // each tick and smooths it into a brightness, so a bang-bang nozzle flares and fades rather than
    // hard-flickering; when dark it disables its light and renderers, so an idle nozzle is invisible
    // and costs nothing.
    public class MissileEngineGlow : MonoBehaviour
    {
        private const float FadeSeconds = 0.08f;

        private bool burning;
        private float brightness;
        private Light[] lights;
        private float[] baseIntensities;
        private Renderer[] renderers;
        private Vector3 baseScale;

        // Call once after the flare children are parented, so they are captured for toggling.
        public void Initialize()
        {
            lights = GetComponentsInChildren<Light>(true);
            baseIntensities = new float[lights.Length];
            for (int i = 0; i < lights.Length; i++)
            {
                baseIntensities[i] = lights[i].intensity;
            }
            renderers = GetComponentsInChildren<Renderer>(true);
            baseScale = transform.localScale;
            Apply(0f);
        }

        public void SetBurning(bool value)
        {
            burning = value;
        }

        private void LateUpdate()
        {
            float target = burning ? 1f : 0f;
            brightness = Mathf.MoveTowards(brightness, target, Time.deltaTime / FadeSeconds);
            Apply(brightness);
        }

        private void Apply(float b)
        {
            bool visible = b > 0.02f;
            if (lights != null)
            {
                for (int i = 0; i < lights.Length; i++)
                {
                    if (lights[i] == null)
                    {
                        continue;
                    }
                    lights[i].enabled = visible;
                    lights[i].intensity = baseIntensities[i] * b;
                }
            }
            if (renderers != null)
            {
                for (int i = 0; i < renderers.Length; i++)
                {
                    if (renderers[i] != null)
                    {
                        renderers[i].enabled = visible;
                    }
                }
            }
            transform.localScale = baseScale * Mathf.Lerp(0.35f, 1f, b);
        }
    }

    public class SpaceKineticRound : MonoBehaviour, ITrackable
    {
        public int OwnerPlayerID;
        public MPTeam OwnerTeam;
        public float VelocityDamageCoefficient;
        public float Lifetime = 8f;
        public float RadiusValue = 0.45f;
        public float MassEstimate;
        public float Caliber;
        public bool SpawnImpactSpark;

        private Rigidbody body;
        private float spawnTime;
        // Targets already resolved on the current tick's sweep, so a part with several colliders is
        // counted once (ADR-0007). Cleared and reused each FixedUpdate to avoid per-tick allocation.
        private readonly HashSet<IKineticTarget> seenTargets = new HashSet<IKineticTarget>();

        private enum HitOutcome { Continue, Stop, Ricochet }

        public int PlayerID { get { return OwnerPlayerID; } }
        public MPTeam Team { get { return OwnerTeam; } }
        public TrackKind Kind { get { return TrackKind.LargeProjectile; } }
        public Vector3 Position { get { return transform.position; } }
        public Vector3 Velocity { get { return body == null ? Vector3.zero : body.velocity; } }
        public float Radius { get { return RadiusValue; } }
        // `this != null` is the destroyed-object test (see ITrackable.IsAlive), not dead weight: a
        // round is destroyed the moment it hits, while cached track/lock references still point at it.
        public bool IsAlive { get { return this != null && gameObject.activeSelf && Time.time - spawnTime < Lifetime; } }

        private void Awake()
        {
            body = GetComponent<Rigidbody>();
            spawnTime = Time.time;
            SpaceCombatRegistry.RegisterTrackable(this);
        }

        // Kinetic penetration march (ADR-0007). Each tick the round sweeps v*dt and resolves the
        // whole chain of penetrable targets in one pass: RaycastAll, sort by distance, and for each
        // hit deposit damage / bleed KE, breaching and continuing until its budget runs out, it
        // ricochets, embeds, or hits terrain. Non-armour ship blocks are passed straight through.
        private void FixedUpdate()
        {
            if (body == null)
            {
                return;
            }
            if (Time.time - spawnTime > Lifetime)
            {
                Destroy(gameObject);
                return;
            }

            float speed = body.velocity.magnitude;
            if (speed < SpaceBalance.RoundStallSpeed)
            {
                Destroy(gameObject);
                return;
            }

            float sweep = speed * Time.fixedDeltaTime;
            Vector3 dir = body.velocity / speed;
            transform.rotation = Quaternion.LookRotation(dir, Vector3.up);

            // MP rule: every round on a client is a visual mirror (authoritative rounds only ever
            // spawn host-side), so the damage march below is authority-only.
            if (NetAuthority.IsClient)
            {
                ClientVisualMarch(dir, sweep);
                return;
            }

            RaycastHit[] hits = Physics.RaycastAll(transform.position, dir, sweep);
            if (hits == null || hits.Length == 0)
            {
                return;
            }
            System.Array.Sort(hits, CompareHitDistance);

            seenTargets.Clear();
            for (int i = 0; i < hits.Length; i++)
            {
                RaycastHit hit = hits[i];
                if (hit.collider == null)
                {
                    continue;
                }

                IKineticTarget target = hit.collider.GetComponentInParent<IKineticTarget>();
                if (target != null)
                {
                    // Breached targets are transparent; a part hit through several colliders is
                    // resolved once per tick.
                    if (target.IsBreached || !seenTargets.Add(target))
                    {
                        continue;
                    }
                    HitOutcome outcome = ResolveTarget(target, hit);
                    if (outcome == HitOutcome.Continue)
                    {
                        continue;
                    }
                    return; // Stop (round destroyed) or Ricochet (redirected) ends this tick
                }

                // A direct hit on the reactor or capacitor detonates it outright (damage-refinement
                // spec) and stops the round here, same as striking hard geometry.
                if (SubsystemDetonation.TryDirectHit(hit.collider))
                {
                    if (SpawnImpactSpark)
                    {
                        SpaceEffectAssets.PlayPierceEffect(hit.point, Caliber);
                    }
                    Destroy(gameObject);
                    return;
                }

                // Not a penetrable target: a plain ship block passes straight through, anything
                // else (terrain / static geometry) hard-stops the round — except an unrecognized
                // trigger collider (e.g. the spectator camera's own collider), which isn't real
                // geometry at all and the round should simply fly through.
                if (hit.collider.GetComponentInParent<BlockBehaviour>() != null
                    || SpaceCombatUtil.IsUnrecognizedObstacle(hit.collider))
                {
                    continue;
                }
                if (SpawnImpactSpark)
                {
                    SpaceEffectAssets.PlayPierceEffect(hit.point, Caliber);
                }
                Destroy(gameObject);
                return;
            }
        }

        private static int CompareHitDistance(RaycastHit a, RaycastHit b)
        {
            return a.distance.CompareTo(b.distance);
        }

        // Client mirror flight: same sweep, but no damage, no penetration continuation, no
        // knockback — it decides where the round visually stops or bounces. Pass through breached
        // targets and plain ship blocks exactly like the host march (breach state arrives via the
        // host's armor batches, which keeps the pass-through honest over time). Ricochets ARE
        // rolled here, with the client's own Random (user decision: no host sync — the mirror's
        // bounce may diverge from the authoritative round's fate, accepted for the visual), using
        // the shared RollRicochet gate and ApplyRicochetKinematics bounce.
        private void ClientVisualMarch(Vector3 dir, float sweep)
        {
            RaycastHit[] hits = Physics.RaycastAll(transform.position, dir, sweep);
            if (hits == null || hits.Length == 0)
            {
                return;
            }
            System.Array.Sort(hits, CompareHitDistance);
            for (int i = 0; i < hits.Length; i++)
            {
                RaycastHit hit = hits[i];
                if (hit.collider == null)
                {
                    continue;
                }

                IKineticTarget target = hit.collider.GetComponentInParent<IKineticTarget>();
                if (target != null)
                {
                    if (target.IsBreached)
                    {
                        continue;
                    }
                    if (TryClientRicochet(target, hit))
                    {
                        return; // bounced — redirected and flying on; this tick's march ends
                    }
                }
                else if (hit.collider.GetComponentInParent<BlockBehaviour>() != null
                    && hit.collider.GetComponentInParent<SpaceShipCore>() == null
                    && hit.collider.GetComponentInParent<SuperCapacitorBlock>() == null)
                {
                    continue;
                }
                else if (SpaceCombatUtil.IsUnrecognizedObstacle(hit.collider))
                {
                    continue; // e.g. the spectator camera's own collider — not real geometry
                }

                // First standing penetrable target, reactor/capacitor (the host detonates it; this
                // is only the visual stop so the mirror round doesn't sail through), or hard
                // geometry.
                if (SpawnImpactSpark)
                {
                    SpaceEffectAssets.PlayPierceEffect(hit.point, Caliber);
                }
                Destroy(gameObject);
                return;
            }
        }

        // The client-side visual ricochet: same order of resolution as the host's ResolveTarget —
        // a round that would have punched through (budget >= remaining HP, meaningful on clients
        // because armor pools are net-synced) never bounces there, so it doesn't bounce here
        // either; otherwise the shared angle-scaled roll decides. No damage is dealt.
        private bool TryClientRicochet(IKineticTarget target, RaycastHit hit)
        {
            if (!target.CanRicochet)
            {
                return false;
            }
            Vector3 targetVel = target.KineticVelocity;
            Vector3 relVel = body.velocity - targetVel;
            float relSpeedSq = relVel.sqrMagnitude;
            if (relSpeedSq <= 0.0001f)
            {
                return false;
            }
            float budget = MassEstimate * relSpeedSq * VelocityDamageCoefficient;
            float hp = target.RemainingKineticHP;
            if (hp > 0f && budget >= hp)
            {
                return false;
            }
            if (!RollRicochet(relVel, Mathf.Sqrt(relSpeedSq), hit.normal))
            {
                return false;
            }
            ApplyRicochetKinematics(targetVel, relVel, budget * SpaceBalance.RicochetKEKeep, hit);
            return true;
        }

        // Physical push in the round's own travel direction, applied at the exact impact point —
        // scoped to armour specifically (not e.g. an intercepted missile) to match the design ask.
        // MissileProjectile.Detonate applies the identical push for a missile's own direct hits.
        private void ApplyImpactKnockback(IKineticTarget target, RaycastHit hit)
        {
            NanoArmorBehaviour armor = target as NanoArmorBehaviour;
            Rigidbody targetBody = hit.rigidbody;
            if (armor == null || targetBody == null)
            {
                return;
            }
            float speed = body.velocity.magnitude;
            if (speed < 0.01f)
            {
                return;
            }
            Vector3 impulse = (body.velocity / speed) * (MassEstimate * speed * SpaceBalance.KineticImpactImpulseFraction);
            targetBody.AddForceAtPosition(impulse, hit.point, ForceMode.Impulse);
        }

        // Resolve the round against one penetrable target. Damage dealt equals kinetic energy lost,
        // in the target's rest frame: budget D = mass * |v_rel|^2 * coeff (ADR-0007).
        private HitOutcome ResolveTarget(IKineticTarget target, RaycastHit hit)
        {
            // Unreachable on clients (FixedUpdate branches to ClientVisualMarch first) — defense
            // in depth for the MP damage rule.
            if (!NetAuthority.IsAuthority)
            {
                return HitOutcome.Continue;
            }
            Vector3 targetVel = target.KineticVelocity;
            Vector3 relVel = body.velocity - targetVel;
            float relSpeedSq = relVel.sqrMagnitude;
            float coeff = VelocityDamageCoefficient;
            float budget = MassEstimate * relSpeedSq * coeff; // D
            float hp = target.RemainingKineticHP;             // H

            // Called only for targets that are still standing (callers filter IsBreached before this),
            // so every resolved hit — breach, ricochet, or embed alike — also physically shoves the
            // armour it struck (ADR: kinetic impact knockback in SpaceBalance).
            ApplyImpactKnockback(target, hit);

            if (hp > 0f && budget >= hp)
            {
                // Breach: deposit exactly H, lose H of KE, fly on with the remainder (D - H).
                target.ApplyKineticDamage(hp);
                SetRelativeBudget(targetVel, relVel, budget - hp, coeff);
                if (SpawnImpactSpark)
                {
                    SpaceEffectAssets.PlayPierceEffect(hit.point, Caliber);
                }
                return HitOutcome.Continue;
            }

            // Did not penetrate. Armour at a steep incidence may ricochet; everything else embeds.
            if (target.CanRicochet && relSpeedSq > 0.0001f
                && RollRicochet(relVel, Mathf.Sqrt(relSpeedSq), hit.normal))
            {
                Ricochet(target, hit, targetVel, relVel, budget);
                return HitOutcome.Ricochet;
            }

            // Embed: dump all remaining KE into the target as damage, round is spent.
            target.ApplyKineticDamage(budget);
            if (SpawnImpactSpark)
            {
                SpaceEffectAssets.PlayPierceEffect(hit.point, Caliber);
            }
            Destroy(gameObject);
            return HitOutcome.Stop;
        }

        // Keep the round's travel direction (in the target's frame) but drop its relative speed so
        // the remaining relative KE equals `budget`, then hand the world velocity back with the
        // target's own motion added in.
        private void SetRelativeBudget(Vector3 targetVel, Vector3 relVel, float budget, float coeff)
        {
            float newRelSpeed = Mathf.Sqrt(Mathf.Max(0f, budget) / Mathf.Max(1e-6f, MassEstimate * coeff));
            Vector3 relDir = relVel.sqrMagnitude > 1e-6f ? relVel.normalized : transform.forward;
            body.velocity = targetVel + relDir * newRelSpeed;
        }

        // A ricochet deposits only part of the surrendered budget as damage, keeps a small residual
        // reflected off the surface, and dissipates the rest — the one place KE lost exceeds damage
        // dealt (ADR-0007).
        private void Ricochet(IKineticTarget target, RaycastHit hit, Vector3 targetVel, Vector3 relVel, float budget)
        {
            float residualBudget = budget * SpaceBalance.RicochetKEKeep;
            float damage = (budget - residualBudget) * SpaceBalance.RicochetDamageFraction;
            target.ApplyKineticDamage(damage);
            ApplyRicochetKinematics(targetVel, relVel, residualBudget, hit);
        }

        // Angle-scaled ricochet roll (ADR-0007): only past RicochetMinAngleDeg, likelier the more
        // oblique. Rolled with whichever machine's Random asks — the host's authoritative march and
        // the client's visual march each roll their own (a mirror's bounce needn't match the real
        // round's fate; user decision).
        private static bool RollRicochet(Vector3 relVel, float relSpeed, Vector3 normal)
        {
            float cosIncidence = Mathf.Clamp01(Mathf.Abs(Vector3.Dot(relVel / relSpeed, normal)));
            float angleDeg = Mathf.Acos(cosIncidence) * Mathf.Rad2Deg;
            if (angleDeg <= SpaceBalance.RicochetMinAngleDeg)
            {
                return false;
            }
            float t = Mathf.Clamp01((angleDeg - SpaceBalance.RicochetMinAngleDeg) /
                                    (90f - SpaceBalance.RicochetMinAngleDeg));
            return Random.value < t * SpaceBalance.RicochetMaxProbability;
        }

        // Shared bounce kinematics (host Ricochet and the client's visual ricochet): keep the
        // travel direction reflected off the surface in the target's frame, drop the relative
        // speed so the remaining relative KE equals residualBudget, add scatter, then hand the
        // world velocity back with the target's own motion added in — plus the impact spark.
        private void ApplyRicochetKinematics(Vector3 targetVel, Vector3 relVel, float residualBudget, RaycastHit hit)
        {
            float residualRelSpeed = Mathf.Sqrt(Mathf.Max(0f, residualBudget) / Mathf.Max(1e-6f, MassEstimate * VelocityDamageCoefficient));
            Vector3 relDir = relVel.sqrMagnitude > 1e-6f ? relVel.normalized : -hit.normal;
            Vector3 reflected = Vector3.Reflect(relDir, hit.normal).normalized;
            if (SpaceBalance.RicochetScatterDeg > 0f)
            {
                reflected = Quaternion.AngleAxis(
                    Random.Range(-SpaceBalance.RicochetScatterDeg, SpaceBalance.RicochetScatterDeg),
                    Random.onUnitSphere) * reflected;
            }
            body.velocity = targetVel + reflected * residualRelSpeed;
            if (SpawnImpactSpark)
            {
                SpaceEffectAssets.PlayPierceEffect(hit.point, Caliber);
            }
        }

        public void ApplyShieldDeceleration(Vector3 newVelocity, float appliedDeltaV)
        {
            // Deliberately NOT authority-fenced: on the host the authoritative interception loop
            // calls this; on a client only ShieldProjectorBlock.ClientIntercept does, and slowing a
            // visual mirror round to match the host's field is display smoothing, not damage.
            if (body == null || appliedDeltaV <= 0f)
            {
                return;
            }
            body.transform.position += body.velocity * Time.fixedDeltaTime;
            body.velocity = newVelocity;
            if (newVelocity.magnitude < SpaceBalance.RoundStallSpeed)
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
