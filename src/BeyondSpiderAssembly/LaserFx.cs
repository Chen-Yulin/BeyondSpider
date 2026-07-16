using UnityEngine;

namespace BeyondSpiderAssembly
{
    // Laser visual toolkit, per the design brief: "使用粒子效果优化激光束，突出粗细和明暗高频变化，
    // 雷电缠绕；命中完整纳米装甲被反射，命中不完整装甲火花不停四溅". Damage stays a pure raycast in
    // LaserLanceBlock — nothing in this file spawns projectiles or touches the damage path.
    internal static class LaserFxAssets
    {
        private static Texture2D dotTexture;

        // Soft round dot shared by every laser particle layer — "Particles/Additive" with no
        // texture renders a hard square (same reasoning as the flak tracer dot).
        public static Texture2D RoundDotTexture()
        {
            if (dotTexture == null)
            {
                const int size = 16;
                float radius = size * 0.5f;
                dotTexture = new Texture2D(size, size, TextureFormat.ARGB32, false);
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        float dist = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(radius, radius));
                        dotTexture.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01(1f - dist / radius)));
                    }
                }
                dotTexture.Apply();
            }
            return dotTexture;
        }

        public static Material NewAdditiveMaterial(bool roundDot)
        {
            Material material = new Material(Shader.Find("Particles/Additive"));
            if (roundDot)
            {
                material.mainTexture = RoundDotTexture();
            }
            return material;
        }
    }

    // One beam pulse drawn as four layers flickered by a shared high-frequency noise, so the
    // lance reads as a live energy discharge instead of a static line:
    //   * core LineRenderer — the hot center;
    //   * halo LineRenderer — a wider, dimmer sheath whose width breathes on its own noise track,
    //     so core and halo pulse against each other (粗细高频变化);
    //   * two bolt LineRenderers — jagged helices re-randomized every frame that coil around the
    //     beam axis in opposite phase and randomly drop out for a frame (雷电缠绕);
    //   * sheath ParticleSystem — additive glow motes scattered along the beam every frame, plus
    //     an origin burst on Show (muzzle flash for the main beam, impact flash for a reflection).
    // Brightness rides a second noise track times the fade-out, and the point light at the beam
    // origin follows it, which is what makes the 明暗 flicker readable at any camera angle.
    //
    // Used two ways: the lance owns one instance parented under the barrel (anchored, so the
    // beam start tracks the muzzle while fading), and LaserImpactFx spawns throwaway
    // auto-destroy instances for reflected beams.
    public class LaserBeamFx : MonoBehaviour
    {
        private const float HaloWidthFactor = 2.6f;
        private const float BoltWidthFactor = 0.35f;
        // Bolt helix radius in beam widths, and how much of that radius the per-frame vertex
        // jitter may add — the jitter is what turns a clean helix into crackling lightning.
        private const float BoltCoilRadiusFactor = 3.2f;
        private const float BoltJitterFactor = 0.55f;
        private const int MaxBoltSegments = 34;
        private const float BoltSegmentLength = 3f;
        // Helix twist along the beam. Capped per-segment below so a long-range beam whose
        // segments span tens of meters can't alias into random zigzag (>~1 rad between verts).
        private const float BoltTwistPerMeter = 0.45f;
        private const float BoltSpinSpeed = 40f;
        // Chance per frame that a bolt blanks out — electric intermittency.
        private const float BoltDropoutChance = 0.15f;
        // Perlin sample speeds for the two decorrelated noise tracks. ~30/s steps the noise far
        // enough per frame at 60fps that consecutive frames land on unrelated values: the
        // "high-frequency" part of the brief.
        private const float WidthNoiseSpeed = 27f;
        private const float BrightnessNoiseSpeed = 33f;
        private const float LightIntensity = 2.4f;
        private const int OriginBurstCount = 10;
        // Sheath motes per frame (cosmetic, so per-frame rather than per-second is fine); the
        // cap keeps a 1200 m full-range miss from flooding the particle budget.
        private const float SheathMotesPerMeter = 0.3f;
        private const int MaxSheathMotesPerFrame = 14;

        private LineRenderer coreLine;
        private LineRenderer haloLine;
        private readonly LineRenderer[] bolts = new LineRenderer[2];
        private ParticleSystem sheath;
        private Light glowLight;

        private Transform anchor;
        private float anchorForwardOffset;
        private Vector3 startPoint;
        private Vector3 endPoint;
        private float baseWidth = 0.1f;
        private float duration = 0.1f;
        private float timer;
        private float intensity = 1f;
        private float noiseSeed;
        private bool autoDestroy;

        private Color coreColor;
        private Color edgeColor;
        private Color boltColor;

        public static LaserBeamFx Create(Transform parent, string name, float width, Color core, Color edge, Color bolt, bool autoDestroy)
        {
            GameObject go = new GameObject(name);
            if (parent != null)
            {
                go.transform.SetParent(parent);
                go.transform.localPosition = Vector3.zero;
                go.transform.localRotation = Quaternion.identity;
            }
            LaserBeamFx fx = go.AddComponent<LaserBeamFx>();
            fx.baseWidth = Mathf.Max(0.02f, width);
            fx.coreColor = core;
            fx.edgeColor = edge;
            fx.boltColor = bolt;
            fx.autoDestroy = autoDestroy;
            fx.noiseSeed = Random.value * 100f;
            fx.Build();
            go.SetActive(false);
            return fx;
        }

        // While visible, the beam start re-reads anchor.position + anchor.forward * offset every
        // frame so the fading beam stays glued to the muzzle as the barrel keeps re-aiming
        // (world-space rendering would otherwise leave it hanging where the pulse was fired).
        public void SetAnchor(Transform anchorTransform, float forwardOffset)
        {
            anchor = anchorTransform;
            anchorForwardOffset = forwardOffset;
        }

        public void Show(Vector3 start, Vector3 end, float visibleTime, float intensityScale)
        {
            startPoint = start;
            endPoint = end;
            duration = Mathf.Max(0.01f, visibleTime);
            timer = duration;
            intensity = Mathf.Max(0f, intensityScale);
            gameObject.SetActive(true);
            EmitBurst(start, OriginBurstCount);
            ApplyVisual();
        }

        private void Update()
        {
            timer -= Time.deltaTime;
            if (timer <= 0f)
            {
                if (autoDestroy)
                {
                    Destroy(gameObject);
                }
                else
                {
                    gameObject.SetActive(false);
                }
                return;
            }

            if (anchor != null)
            {
                startPoint = anchor.position + anchor.forward * anchorForwardOffset;
            }
            ApplyVisual();
            EmitSheathMotes();
        }

        private float Noise(float speed, float offset)
        {
            // 0.55..1.45 band: never fully dark (a beam that blinks off reads as misfire),
            // never more than ~1.5x wide.
            return 0.55f + 0.9f * Mathf.PerlinNoise((Time.time + offset) * speed, noiseSeed);
        }

        private void ApplyVisual()
        {
            float fade = Mathf.Clamp01(timer / duration);
            float widthNoise = Noise(WidthNoiseSpeed, 0f);
            float haloNoise = Noise(WidthNoiseSpeed, 5.3f);
            float glow = fade * Noise(BrightnessNoiseSpeed, 13.7f) * intensity;

            if (coreLine != null)
            {
                float coreWidth = baseWidth * widthNoise;
                coreLine.SetPosition(0, startPoint);
                coreLine.SetPosition(1, endPoint);
                coreLine.SetWidth(coreWidth, coreWidth * 0.55f);
                coreLine.SetColors(coreColor * glow, coreColor * glow * 0.7f);
            }
            if (haloLine != null)
            {
                float haloWidth = baseWidth * HaloWidthFactor * haloNoise;
                haloLine.SetPosition(0, startPoint);
                haloLine.SetPosition(1, endPoint);
                haloLine.SetWidth(haloWidth, haloWidth * 0.7f);
                haloLine.SetColors(edgeColor * glow * 0.45f, edgeColor * glow * 0.25f);
            }
            for (int i = 0; i < bolts.Length; i++)
            {
                RebuildBolt(bolts[i], i == 0 ? 0f : Mathf.PI, glow, widthNoise);
            }
            if (glowLight != null)
            {
                glowLight.transform.position = startPoint;
                glowLight.intensity = LightIntensity * glow;
            }
        }

        private void RebuildBolt(LineRenderer bolt, float phase, float glow, float widthNoise)
        {
            if (bolt == null)
            {
                return;
            }
            Vector3 axis = endPoint - startPoint;
            float length = axis.magnitude;
            if (length < 1f || Random.value < BoltDropoutChance)
            {
                bolt.enabled = false;
                return;
            }
            bolt.enabled = true;

            Vector3 dir = axis / length;
            Vector3 perpA = Vector3.Cross(dir, Vector3.up);
            if (perpA.sqrMagnitude < 0.0001f)
            {
                perpA = Vector3.Cross(dir, Vector3.right);
            }
            perpA.Normalize();
            Vector3 perpB = Vector3.Cross(dir, perpA);

            int segments = Mathf.Clamp(Mathf.CeilToInt(length / BoltSegmentLength), 6, MaxBoltSegments);
            float twist = Mathf.Min(BoltTwistPerMeter, segments / length);
            float coil = baseWidth * BoltCoilRadiusFactor;

            bolt.SetVertexCount(segments + 1);
            for (int i = 0; i <= segments; i++)
            {
                float t = (float)i / segments;
                // sin envelope pins both ends onto the beam so the bolt wraps it rather than
                // waving loose at the muzzle/impact.
                float envelope = Mathf.Sin(t * Mathf.PI);
                float angle = phase + t * length * twist + Time.time * BoltSpinSpeed;
                Vector3 radial = perpA * Mathf.Cos(angle) + perpB * Mathf.Sin(angle);
                Vector3 jitter = Random.insideUnitSphere * coil * BoltJitterFactor;
                bolt.SetPosition(i, startPoint + dir * (t * length) + (radial * coil + jitter) * envelope);
            }
            float width = baseWidth * BoltWidthFactor * widthNoise;
            bolt.SetWidth(width, width);
            bolt.SetColors(boltColor * glow, boltColor * glow * 0.6f);
        }

        private void EmitSheathMotes()
        {
            if (sheath == null)
            {
                return;
            }
            Vector3 axis = endPoint - startPoint;
            float length = axis.magnitude;
            if (length < 1f)
            {
                return;
            }
            Vector3 dir = axis / length;
            float fade = Mathf.Clamp01(timer / duration);
            int count = Mathf.Clamp(Mathf.CeilToInt(length * SheathMotesPerMeter), 4, MaxSheathMotesPerFrame);
            for (int i = 0; i < count; i++)
            {
                ParticleSystem.EmitParams p = new ParticleSystem.EmitParams();
                p.position = startPoint + dir * (Random.value * length) + Random.insideUnitSphere * (baseWidth * 1.4f);
                p.velocity = Random.insideUnitSphere * 2f;
                p.startLifetime = 0.1f + Random.value * 0.18f;
                p.startSize = baseWidth * (1.6f + Random.value * 2.2f) * fade;
                p.startColor = edgeColor * Mathf.Clamp01(fade * intensity);
                sheath.Emit(p, 1);
            }
        }

        private void EmitBurst(Vector3 position, int count)
        {
            if (sheath == null)
            {
                return;
            }
            for (int i = 0; i < count; i++)
            {
                ParticleSystem.EmitParams p = new ParticleSystem.EmitParams();
                p.position = position + Random.insideUnitSphere * (baseWidth * 0.8f);
                p.velocity = Random.insideUnitSphere * 5f;
                p.startLifetime = 0.12f + Random.value * 0.15f;
                p.startSize = baseWidth * (2.5f + Random.value * 2.5f);
                p.startColor = coreColor * Mathf.Clamp01(intensity);
                sheath.Emit(p, 1);
            }
        }

        private void Build()
        {
            coreLine = CreateLine("Core");
            haloLine = CreateLine("Halo");
            bolts[0] = CreateLine("Bolt0");
            bolts[1] = CreateLine("Bolt1");

            GameObject sheathObject = new GameObject("Sheath");
            sheathObject.transform.SetParent(transform);
            sheathObject.transform.localPosition = Vector3.zero;
            sheathObject.transform.localRotation = Quaternion.identity;
            sheath = sheathObject.AddComponent<ParticleSystem>();
            sheath.playOnAwake = false;
            sheath.gravityModifier = 0f;
            // World space + Shape scaling mode: motes must stay where they were emitted as the
            // barrel re-aims, and the parent block's build-mode scale must not distort particle
            // size (same pitfalls the flak tracer documents).
            sheath.simulationSpace = ParticleSystemSimulationSpace.World;
            sheath.scalingMode = ParticleSystemScalingMode.Shape;
            ParticleSystem.EmissionModule emission = sheath.emission;
            emission.enabled = false;
            ParticleSystem.ShapeModule shape = sheath.shape;
            shape.enabled = false;
            ParticleSystemRenderer sheathRenderer = sheathObject.GetComponent<ParticleSystemRenderer>();
            if (sheathRenderer != null)
            {
                sheathRenderer.material = LaserFxAssets.NewAdditiveMaterial(true);
            }

            GameObject lightObject = new GameObject("GlowLight");
            lightObject.transform.SetParent(transform);
            lightObject.transform.localPosition = Vector3.zero;
            glowLight = lightObject.AddComponent<Light>();
            glowLight.type = LightType.Point;
            glowLight.color = coreColor;
            glowLight.range = Mathf.Clamp(baseWidth * 40f, 4f, 20f);
            glowLight.intensity = 0f;
        }

        private LineRenderer CreateLine(string childName)
        {
            GameObject go = new GameObject(childName);
            go.transform.SetParent(transform);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            LineRenderer line = go.AddComponent<LineRenderer>();
            line.material = LaserFxAssets.NewAdditiveMaterial(false);
            line.useWorldSpace = true;
            return line;
        }
    }

    // Impact-side visuals: which one plays is decided by the armor's integrity (see
    // NanoArmorBehaviour.ReflectsLaser) — an intact nano coating is a mirror, a stripped one is
    // a surface being burned.
    internal static class LaserImpactFx
    {
        // 完整装甲：整束沿 Vector3.Reflect 弹出去。Purely visual — the energy split is already
        // settled by ApplyLaserDamage, so the reflected beam deals no damage; its brightness is
        // the armor's actual reflectivity, and it is trimmed by a raycast so it can't lance
        // through whatever stands in the reflected path.
        public static void PlayReflection(Vector3 hitPoint, Vector3 incomingDir, Vector3 normal, float width,
            Color core, Color edge, Color bolt, float maxLength, float visibleTime, float reflectivity)
        {
            Vector3 reflectDir = Vector3.Reflect(incomingDir.normalized, normal.normalized).normalized;
            // Lift the origin off the surface so the trim raycast can't re-hit the very collider
            // the beam just bounced from.
            Vector3 origin = hitPoint + normal * 0.05f;
            float length = Mathf.Max(5f, maxLength);
            RaycastHit obstruction;
            if (Physics.Raycast(origin, reflectDir, out obstruction, length))
            {
                length = obstruction.distance;
            }
            LaserBeamFx fx = LaserBeamFx.Create(null, "BS Laser Reflection", width * 0.8f, core, edge, bolt, true);
            fx.Show(origin, origin + reflectDir * length, visibleTime, Mathf.Clamp01(reflectivity));
        }

        // 不完整装甲：命中点火花不停四溅。One pulse spawns one short-lived emitter; at the lance's
        // fastest reload the emitters of consecutive pulses overlap, so sustained fire reads as a
        // continuous shower. Parented to the struck block so the shower rides the moving hull.
        public static void PlayArmorSparks(Vector3 point, Vector3 normal, Transform follow, float apertureMm)
        {
            GameObject go = new GameObject("BS Laser Armor Sparks");
            go.transform.position = point;
            go.transform.rotation = Quaternion.LookRotation(normal);
            if (follow != null)
            {
                go.transform.SetParent(follow, true);
            }
            go.AddComponent<LaserSparkFx>().Init(apertureMm);
        }
    }

    // Spark shower for a laser chewing on integrity-stripped armor: white-hot streaks sprayed in
    // a cone off the surface normal (transform.forward), cooling to orange and fading via
    // ColorOverLifetime, plus a slow molten glow at the burn point and a flickering orange light.
    // Zero gravity — this is space, sparks fly straight until they fade.
    public class LaserSparkFx : MonoBehaviour
    {
        private const float EmitSeconds = 0.45f;
        // tan of the spray half-angle around the surface normal (~60 degrees).
        private const float ConeSpreadTan = 1.7f;
        private const int MaxSparksPerFrame = 40;
        private const float LightRange = 7f;
        private const float LightPeakIntensity = 2.6f;

        private static readonly Color SparkHotColor = new Color(1f, 0.92f, 0.6f, 1f);
        private static readonly Color SparkCoolColor = new Color(1f, 0.55f, 0.15f, 1f);

        private ParticleSystem sparks;
        private Light flashLight;
        private float sparksPerSecond;
        private float emitTimer;
        private float emitCarry;

        public void Init(float apertureMm)
        {
            sparksPerSecond = Mathf.Clamp(apertureMm * 0.9f, 90f, 360f);
            emitTimer = EmitSeconds;
            Build();
            // Outlive the last spark's lifetime, then clean up.
            Destroy(gameObject, EmitSeconds + 1f);
        }

        private void Update()
        {
            if (sparks == null || emitTimer <= 0f)
            {
                if (flashLight != null)
                {
                    flashLight.intensity = Mathf.Max(0f, flashLight.intensity - Time.deltaTime * 12f);
                }
                return;
            }

            emitTimer -= Time.deltaTime;
            emitCarry += sparksPerSecond * Time.deltaTime;
            int count = Mathf.Min(MaxSparksPerFrame, Mathf.FloorToInt(emitCarry));
            emitCarry -= count;

            for (int i = 0; i < count; i++)
            {
                ParticleSystem.EmitParams p = new ParticleSystem.EmitParams();
                Vector3 dir = (transform.forward + Random.insideUnitSphere * ConeSpreadTan).normalized;
                p.position = transform.position;
                p.velocity = dir * Random.Range(6f, 26f);
                p.startLifetime = Random.Range(0.2f, 0.55f);
                p.startSize = Random.Range(0.05f, 0.16f);
                p.startColor = Color.Lerp(SparkHotColor, SparkCoolColor, Random.value);
                sparks.Emit(p, 1);
            }

            // Molten glow sitting on the burn point itself — a fat slow dot per frame.
            ParticleSystem.EmitParams glow = new ParticleSystem.EmitParams();
            glow.position = transform.position + Random.insideUnitSphere * 0.05f;
            glow.velocity = Random.insideUnitSphere * 0.5f;
            glow.startLifetime = 0.15f;
            glow.startSize = Random.Range(0.3f, 0.5f);
            glow.startColor = SparkHotColor;
            sparks.Emit(glow, 1);

            if (flashLight != null)
            {
                flashLight.intensity = LightPeakIntensity * (0.7f + 0.5f * Random.value) * Mathf.Clamp01(emitTimer / EmitSeconds * 2f);
            }
        }

        private void Build()
        {
            sparks = gameObject.AddComponent<ParticleSystem>();
            sparks.playOnAwake = false;
            sparks.gravityModifier = 0f;
            // World space: already-flying sparks must not swing with the hull the emitter is
            // parented to; Shape scaling mode so the hull block's scale can't distort them.
            sparks.simulationSpace = ParticleSystemSimulationSpace.World;
            sparks.scalingMode = ParticleSystemScalingMode.Shape;
            ParticleSystem.EmissionModule emission = sparks.emission;
            emission.enabled = false;
            ParticleSystem.ShapeModule shape = sparks.shape;
            shape.enabled = false;

            // White-hot -> orange -> gone. Multiplies the per-particle start color, so streaks
            // emitted cooler just fade along the same ramp.
            Gradient gradient = new Gradient();
            gradient.SetKeys(
                new GradientColorKey[]
                {
                    new GradientColorKey(Color.white, 0f),
                    new GradientColorKey(new Color(1f, 0.6f, 0.25f), 0.5f),
                    new GradientColorKey(new Color(0.6f, 0.15f, 0.03f), 1f)
                },
                new GradientAlphaKey[]
                {
                    new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(0.8f, 0.5f),
                    new GradientAlphaKey(0f, 1f)
                });
            ParticleSystem.ColorOverLifetimeModule colorOverLifetime = sparks.colorOverLifetime;
            colorOverLifetime.enabled = true;
            colorOverLifetime.color = new ParticleSystem.MinMaxGradient(gradient);

            ParticleSystemRenderer renderer = gameObject.GetComponent<ParticleSystemRenderer>();
            if (renderer != null)
            {
                renderer.material = LaserFxAssets.NewAdditiveMaterial(true);
                // Stretched along velocity: dots read as debris, streaks read as sparks.
                renderer.renderMode = ParticleSystemRenderMode.Stretch;
                renderer.lengthScale = 4f;
            }

            GameObject lightObject = new GameObject("SparkLight");
            lightObject.transform.SetParent(transform);
            lightObject.transform.localPosition = Vector3.zero;
            flashLight = lightObject.AddComponent<Light>();
            flashLight.type = LightType.Point;
            flashLight.color = SparkCoolColor;
            flashLight.range = LightRange;
            flashLight.intensity = 0f;
        }
    }
}
