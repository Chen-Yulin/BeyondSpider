using Modding;
using UnityEngine;

namespace BeyondSpiderAssembly
{
    // Vacuum exhaust plume for the thruster block (ADR-0018 §8).
    //
    // This mod cannot ship shaders: Besiege's Unity build has no runtime shader compilation and the
    // repo has no editor project to build shader AssetBundles (see ShieldHexFx.cs's note; the
    // shipped .ab files are salvaged prefabs). So "shader" here means doing the shader's job in
    // C# — a procedurally generated bell mesh on the stock Particles/Additive shader whose vertex
    // colors and UV offset are rewritten every rendered frame. Exactly the technique the shield
    // ripple already uses, applied to a different problem.
    //
    // Three layers:
    //   * the bell — carries ~90% of the look and ALL of the throttle continuity. A particle-only
    //     plume goes granular at low throttle (particle counts are integers) and can't hold the
    //     wide vacuum flare in shape; a mesh scales smoothly from 0 to 1 at fixed cost.
    //   * a sparse particle layer for diffuse glow and irregularity, so the bell's clean edge
    //     doesn't read as plastic.
    //   * a point light at the throat, scaled by output — the cheapest way to make the plume feel
    //     like it is actually emitting, and the same trick the muzzle flash already uses.
    //
    // Plume radius comes from the nozzle cross-section (the same number that sets max thrust and
    // efficiency); brightness comes from the allocator's per-nozzle output, which is replicated, so
    // on every machine the exhaust is a live readout of what the allocator is doing.
    public class ThrusterFx : MonoBehaviour
    {
        // Bell geometry. Rings along the axis, segments around it — 10×12 is 120 vertices, so the
        // per-frame color repaint is free.
        private const int AxialRings = 10;
        private const int RadialSegments = 12;
        // How far the bell flares by its tail, in throat radii. Wide, because that is what a plume
        // does with no atmosphere to confine it — this is the single most recognizable cue that
        // the engine is firing in vacuum rather than in air.
        private const float FlareFactor = 1.6f;
        // Plume length at full throttle, in throat radii.
        private const float LengthPerRadius = 7f;
        // Length and width at idle-but-lit, as fractions of the full-throttle figures: the plume
        // shortens much faster than it narrows as throttle drops, which is what makes a low burn
        // read as a stub rather than a thin thread.
        private const float MinLengthFactor = 0.25f;
        private const float MinWidthFactor = 0.7f;

        private const float FlickerSpeed = 9f;
        private const float FlickerDepth = 0.22f;

        // Counter-flow layers: how fast each bell scrolls its u coordinate (texture-widths per
        // second) at full throttle, the second layer's size relative to the first (a touch bigger
        // so the two don't sit exactly coplanar), and how much dimmer the second layer renders
        // (its _TintColor) so it reads as an underglow rather than a doubled plume.
        private const float UvSpinSpeed = 0.55f;
        private const float Layer2ScaleFactor = 1.08f;
        private const float Layer2Tint = 0.4f;

        private const float ParticlesPerSecondAtFull = 26f;
        private const float LightIntensityAtFull = 2.6f;

        // Overall bell brightness scalar. The two additive layers plus the bright texture read too
        // hot, so the vertex pass carries only this fraction of full opacity into the alpha channel
        // (which scales the additive contribution) — a flat 1/3 dim across both layers.
        private const float ConeOpacity = 1f / 3f;

        // Haze-particle colours (the bell's own colour now comes from the plume texture). Mid = the
        // cyan-blue body, Tail = the violet fade.
        private static readonly Color MidColor = new Color(0.45f, 0.72f, 1f, 0.75f);
        private static readonly Color TailColor = new Color(0.55f, 0.35f, 1f, 0f);

        // The hand-drawn plume sheet registered in Mod.xml; falls back to a procedural one when the
        // resource is absent. Base Texture type because ModResource hands back a Texture, not a
        // Texture2D.
        private const string PlumeTextureResource = "BS Thruster Plume Texture";
        private static Texture plumeTexture;
        private static bool plumeTextureResolved;
        private static Texture2D dotTexture;

        private Transform nozzle;
        private float throatRadius = 0.5f;

        private GameObject bellObject;
        private MeshFilter bellFilter;
        private MeshRenderer bellRenderer;
        // Second, counter-flowing bell sharing the same mesh (see BuildBellLayer): the two layers
        // scroll their texture's u (around-the-axis) coordinate in OPPOSITE directions, so their
        // streaks slide across each other and the plume reads as churning flow instead of a static
        // shell. Done via UV rather than physically spinning the mesh: u is the "around the cone"
        // axis, so scrolling it rotates the streaks without touching geometry — no shear even when
        // a non-square nozzle gives the bell an elliptical world cross-section.
        private GameObject bellObject2;
        private MeshRenderer bellRenderer2;
        private Mesh bellMesh;
        private Color32[] bellColors;
        private float[] ringT;
        private float spinPhase;

        private ParticleSystem haze;
        private Light throatLight;

        private float output;
        private float noiseSeed;
        private float particleCredit;

        public void Configure(Transform nozzleTransform, float nozzleArea)
        {
            nozzle = nozzleTransform;
            // Throat radius from the cross-section: a square nozzle of area A has half-width
            // sqrt(A)/2, which is close enough to a radius for a plume nobody measures.
            throatRadius = Mathf.Max(0.08f, Mathf.Sqrt(Mathf.Max(0.01f, nozzleArea)) * 0.5f);
            noiseSeed = Random.value * 100f;
            Build();
            SetOutput(0f);
        }

        public void SetOutput(float value)
        {
            output = Mathf.Clamp01(value);
        }

        public void Shutdown()
        {
            output = 0f;
            Apply();
        }

        private void LateUpdate()
        {
            Apply();
        }

        private void Apply()
        {
            if (bellObject == null)
            {
                return;
            }

            bool lit = output > 0.004f;
            if (bellRenderer != null)
            {
                bellRenderer.enabled = lit;
            }
            if (bellRenderer2 != null)
            {
                bellRenderer2.enabled = lit;
            }
            if (throatLight != null)
            {
                throatLight.intensity = lit ? LightIntensityAtFull * output : 0f;
                throatLight.range = Mathf.Max(1f, throatRadius * 14f * Mathf.Sqrt(Mathf.Max(0.05f, output)));
            }
            if (!lit)
            {
                particleCredit = 0f;
                return;
            }

            // The parent block carries the player's build-mode scale, and the plume's dimensions
            // are already derived from that scale (via nozzle area). Counter-scaling here keeps the
            // bell from being distorted twice — the same pitfall the particle layers avoid with
            // ParticleSystemScalingMode.Shape.
            Vector3 parentScale = nozzle != null ? nozzle.lossyScale : Vector3.one;
            float width = throatRadius * Mathf.Lerp(MinWidthFactor, 1f, output);
            float length = throatRadius * LengthPerRadius * Mathf.Lerp(MinLengthFactor, 1f, output);
            float sx = width / Mathf.Max(0.0001f, Mathf.Abs(parentScale.x));
            float sy = width / Mathf.Max(0.0001f, Mathf.Abs(parentScale.y));
            float sz = length / Mathf.Max(0.0001f, Mathf.Abs(parentScale.z));
            bellObject.transform.localScale = new Vector3(sx, sy, sz);
            // Layer 2 rides a touch wider/longer so it never sits exactly coplanar with layer 1
            // (additive doesn't z-fight, but perfectly coincident streaks would just look like one
            // brighter layer instead of two flowing past each other).
            bellObject2.transform.localScale = new Vector3(sx * Layer2ScaleFactor, sy * Layer2ScaleFactor, sz);

            // Slide the plume forward to the model's actual nozzle mouth: the block origin sits
            // behind the nozzle, so offset by one block length along forward (transform.forward *
            // localScale.z). Set in world space each frame so it tracks the ship's motion; rotation
            // is inherited from the parent, so the bells still point down the exhaust axis.
            Vector3 nozzleTip = transform.position + transform.forward * transform.localScale.z;
            bellObject.transform.position = nozzleTip;
            bellObject2.transform.position = nozzleTip;
            if (throatLight != null)
            {
                throatLight.transform.position = nozzleTip;
            }

            // Counter-flow: advance a shared phase (faster at higher throttle) and scroll the two
            // layers' u coordinate in opposite directions. u wraps once around the bell, so this
            // slides the streaks around the axis — the two sheets shear across each other and the
            // plume churns. v (the axial colour ramp) is left untouched so the gradient stays put.
            spinPhase += UvSpinSpeed * (0.4f + output) * Time.deltaTime;
            spinPhase -= Mathf.Floor(spinPhase);
            if (bellRenderer != null && bellRenderer.material != null)
            {
                bellRenderer.material.mainTextureOffset = new Vector2(spinPhase, 0f);
            }
            if (bellRenderer2 != null && bellRenderer2.material != null)
            {
                bellRenderer2.material.mainTextureOffset = new Vector2(-spinPhase, 0f);
            }

            RepaintBell();
            EmitHaze(width, length);
        }

        // The "fragment shader", run per vertex on the CPU: axial color ramp × throttle × a
        // per-ring turbulence flicker, written into the mesh's vertex colors, with the texture's
        // UV scrolled along the axis so the pattern appears to stream outward.
        private void RepaintBell()
        {
            if (bellMesh == null || bellColors == null)
            {
                return;
            }

            // Colour and shape now live in the texture; the vertex pass only carries a per-ring
            // GREY level (throttle × turbulence flicker), which the additive shader multiplies onto
            // the texture — so the sheet's own white-hot-to-violet gradient shows through, brighter
            // as the throttle opens. A near-white grey keeps the texture's hue intact; using output
            // in both the rgb and the alpha gives the intensity-squared throttle response the
            // shield field also uses.
            float time = Time.time;
            int index = 0;
            for (int ring = 0; ring <= AxialRings; ring++)
            {
                float t = ringT[ring];
                // Turbulence: an independent noise track per ring, so the plume ripples along its
                // length instead of pulsing as one solid object.
                float flicker = 1f + (Mathf.PerlinNoise(noiseSeed + t * 3.5f, time * FlickerSpeed) - 0.5f) * 2f * FlickerDepth;
                float bright = Mathf.Clamp01(output * flicker);
                // Alpha scales the additive contribution (Blend SrcAlpha One), so ConeOpacity here
                // dims the whole plume to 1/3 without touching its hue; rgb keeps the full level so
                // the colour ramp from the texture still reads at temperature.
                byte rgbLevel = (byte)(bright * 255f);
                byte alphaLevel = (byte)(bright * ConeOpacity * 255f);
                Color32 packed = new Color32(rgbLevel, rgbLevel, rgbLevel, alphaLevel);

                for (int segment = 0; segment <= RadialSegments; segment++)
                {
                    bellColors[index++] = packed;
                }
            }
            bellMesh.colors32 = bellColors;
        }

        private void EmitHaze(float width, float length)
        {
            if (haze == null || nozzle == null)
            {
                return;
            }

            particleCredit += ParticlesPerSecondAtFull * output * Time.deltaTime;
            int count = Mathf.FloorToInt(particleCredit);
            if (count <= 0)
            {
                return;
            }
            particleCredit -= count;
            count = Mathf.Min(count, 6);

            Vector3 axis = nozzle.forward;
            // Same nozzle-mouth offset as the bells (one block length forward), so the haze streams
            // from the model's opening rather than the block origin behind it.
            Vector3 origin = nozzle.position + axis * nozzle.localScale.z;
            for (int i = 0; i < count; i++)
            {
                float t = Random.value;
                float radius = width * (1f + FlareFactor * Mathf.Pow(t, 0.7f)) * Random.Range(0.3f, 1.15f);
                Vector3 lateral = Random.insideUnitSphere;
                lateral -= axis * Vector3.Dot(lateral, axis);
                lateral = lateral.sqrMagnitude > 0.0001f ? lateral.normalized : nozzle.right;

                ParticleSystem.EmitParams p = new ParticleSystem.EmitParams();
                p.position = origin + axis * (length * t) + lateral * radius;
                p.velocity = axis * (length * Random.Range(1.4f, 2.6f)) + lateral * (width * Random.Range(0.5f, 2f));
                p.startLifetime = Random.Range(0.12f, 0.32f);
                p.startSize = width * Random.Range(1.2f, 3.4f);
                p.startColor = Color.Lerp(MidColor, TailColor, t) * (0.5f + 0.5f * output);
                haze.Emit(p, 1);
            }
        }

        private void Build()
        {
            bellMesh = BuildBellMesh(out ringT);
            bellColors = new Color32[bellMesh.vertexCount];

            // Two layers over one shared mesh: layer 1 at the neutral tint, layer 2 dimmer and a
            // touch larger, counter-scrolling in Apply() for the flow effect.
            bellObject = BuildBellLayer("Plume", 0.5f, out bellRenderer);
            bellFilter = bellObject.GetComponent<MeshFilter>();
            bellObject2 = BuildBellLayer("PlumeFlow", Layer2Tint, out bellRenderer2);

            GameObject hazeObject = new GameObject("PlumeHaze");
            hazeObject.transform.SetParent(transform, false);
            haze = hazeObject.AddComponent<ParticleSystem>();
            haze.playOnAwake = false;
            haze.gravityModifier = 0f;
            // World space so motes stay behind as the ship accelerates away from them, and Shape
            // scaling so the block's build-mode scale doesn't distort particle size.
            haze.simulationSpace = ParticleSystemSimulationSpace.World;
            haze.scalingMode = ParticleSystemScalingMode.Shape;
            ParticleSystem.EmissionModule emission = haze.emission;
            emission.enabled = false;
            ParticleSystem.ShapeModule shape = haze.shape;
            shape.enabled = false;
            ParticleSystemRenderer hazeRenderer = hazeObject.GetComponent<ParticleSystemRenderer>();
            if (hazeRenderer != null)
            {
                hazeRenderer.material = new Material(Shader.Find("Particles/Additive"));
                hazeRenderer.material.mainTexture = DotTexture();
            }

            GameObject lightObject = new GameObject("ThroatLight");
            lightObject.transform.SetParent(transform, false);
            throatLight = lightObject.AddComponent<Light>();
            throatLight.type = LightType.Point;
            throatLight.color = new Color(0.6f, 0.78f, 1f);
            throatLight.intensity = 0f;
        }

        // One bell layer: a child renderer over the shared bell mesh, on Particles/Additive with
        // the plume texture and the given tint. `tint` sets _TintColor (the shader doubles it, so
        // 0.5 is neutral and less is dimmer); the counter-flow underlay uses a lower value.
        private GameObject BuildBellLayer(string layerName, float tint, out MeshRenderer renderer)
        {
            GameObject layer = new GameObject(layerName);
            layer.transform.SetParent(transform, false);
            layer.transform.localPosition = Vector3.zero;
            layer.transform.localRotation = Quaternion.identity;

            layer.AddComponent<MeshFilter>().sharedMesh = bellMesh;
            renderer = layer.AddComponent<MeshRenderer>();
            renderer.material = new Material(Shader.Find("Particles/Additive"));
            renderer.material.SetColor("_TintColor", new Color(tint, tint, tint, tint));
            renderer.material.mainTexture = PlumeTexture();
            renderer.material.mainTextureScale = new Vector2(1f, 1f);
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            // Start disabled: a MeshRenderer is enabled the instant it is added, and Configure does
            // NOT call Apply(), so without this the default-scale bell would render for the one
            // frame between OnSimulateStart and the first LateUpdate — the start-of-sim flash.
            renderer.enabled = false;
            return layer;
        }

        // Bell of revolution along +z (the nozzle's forward, which is where the exhaust goes).
        // Built at unit throat radius and unit length so throttle can drive both by scaling the
        // transform instead of rewriting vertices — the flare profile is baked in as multiples of
        // the throat radius, so uniform x/y scaling preserves its shape.
        private static Mesh BuildBellMesh(out float[] ringTs)
        {
            int ringVerts = RadialSegments + 1;
            Vector3[] vertices = new Vector3[(AxialRings + 1) * ringVerts];
            Vector2[] uvs = new Vector2[vertices.Length];
            ringTs = new float[AxialRings + 1];

            int index = 0;
            for (int ring = 0; ring <= AxialRings; ring++)
            {
                float t = ring / (float)AxialRings;
                ringTs[ring] = t;
                float radius = 1f + FlareFactor * Mathf.Pow(t, 0.7f);
                for (int segment = 0; segment <= RadialSegments; segment++)
                {
                    float around = segment / (float)RadialSegments;
                    float angle = around * Mathf.PI * 2f;
                    // Mirror-fold the u coordinate into a 0→1→0 triangle wave: the texture is drawn
                    // once on each half of the cone, mirrored. Both fold lines (front u=0, back u=1)
                    // sample identical texels on either side, so the bell has NO hard seam no matter
                    // whether the source image tiles left-to-right — and it stays seamless under the
                    // counter-flow u-scroll, since a global u offset shifts the whole wave uniformly.
                    float u = 1f - Mathf.Abs(1f - 2f * around);
                    vertices[index] = new Vector3(Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius, t);
                    uvs[index] = new Vector2(u, t);
                    index++;
                }
            }

            // Both windings, so the plume renders from inside and outside alike — Particles/Additive
            // is single-sided and a player will fly the camera through their own exhaust.
            int[] triangles = new int[AxialRings * RadialSegments * 12];
            int tri = 0;
            for (int ring = 0; ring < AxialRings; ring++)
            {
                for (int segment = 0; segment < RadialSegments; segment++)
                {
                    int a = ring * ringVerts + segment;
                    int b = a + 1;
                    int c = a + ringVerts;
                    int d = c + 1;
                    triangles[tri++] = a; triangles[tri++] = c; triangles[tri++] = b;
                    triangles[tri++] = b; triangles[tri++] = c; triangles[tri++] = d;
                    triangles[tri++] = a; triangles[tri++] = b; triangles[tri++] = c;
                    triangles[tri++] = b; triangles[tri++] = d; triangles[tri++] = c;
                }
            }

            Mesh mesh = new Mesh();
            mesh.vertices = vertices;
            mesh.uv = uvs;
            mesh.triangles = triangles;
            mesh.RecalculateBounds();
            return mesh;
        }

        // The plume's whole appearance — axial colour ramp (throat-white → cyan → blue → violet →
        // black tail) AND turbulence streaks — comes from this sheet; the vertex-colour pass only
        // scales it by throttle. A hand-drawn sheet registered in Mod.xml is used when present,
        // otherwise a procedural greyscale-plus-ramp stand-in. The texture's v axis is the plume
        // axis (v=0, the image BOTTOM in Unity, is the throat), mapped once with no scroll so the
        // painted gradient stays put; u wraps once around the bell.
        private static Texture PlumeTexture()
        {
            if (plumeTextureResolved)
            {
                return plumeTexture;
            }
            plumeTextureResolved = true;

            try
            {
                ModTexture resource = ModResource.GetTexture(PlumeTextureResource);
                if (resource != null && resource.Texture != null)
                {
                    // Repeat so the counter-flow u-scroll tiles around the bell. Safe on v too,
                    // which is sampled only in [0,1] with no offset.
                    resource.Texture.wrapMode = TextureWrapMode.Repeat;
                    plumeTexture = resource.Texture;
                    return plumeTexture;
                }
            }
            catch
            {
                // Resource not registered / not found — fall through to the procedural stand-in.
            }

            const int width = 64;
            const int height = 128;
            Texture2D tex = new Texture2D(width, height, TextureFormat.ARGB32, false);
            tex.wrapMode = TextureWrapMode.Repeat;

            for (int y = 0; y < height; y++)
            {
                float v = y / (float)height;
                // Colour ramp along the axis: white-hot throat (v=0) → cyan → blue → violet tail,
                // matching the hand-drawn sheet this stands in for. Bottom row is the throat.
                Color hue = v < 0.5f
                    ? Color.Lerp(new Color(1f, 1f, 1f), new Color(0.25f, 0.75f, 1f), v * 2f)
                    : Color.Lerp(new Color(0.25f, 0.75f, 1f), new Color(0.55f, 0.2f, 1f), (v - 0.5f) * 2f);
                // Bright, dense core near the throat thinning out along the axis.
                float axial = Mathf.Pow(1f - v, 1.4f);
                for (int x = 0; x < width; x++)
                {
                    float u = x / (float)width;
                    // Seamless in both axes: sample the noise on a torus so the left/right and
                    // top/bottom edges match exactly.
                    float angle = u * Mathf.PI * 2f;
                    float vAngle = v * Mathf.PI * 2f;
                    float noise =
                        Mathf.PerlinNoise(3f + Mathf.Cos(angle) * 1.5f + Mathf.Cos(vAngle) * 3f,
                                          7f + Mathf.Sin(angle) * 1.5f + Mathf.Sin(vAngle) * 3f) * 0.6f
                        + Mathf.PerlinNoise(11f + Mathf.Cos(angle) * 3f + Mathf.Cos(vAngle) * 7f,
                                            5f + Mathf.Sin(angle) * 3f + Mathf.Sin(vAngle) * 7f) * 0.4f;
                    float intensity = Mathf.Clamp01(axial * (0.55f + 0.75f * noise));
                    tex.SetPixel(x, y, new Color(hue.r, hue.g, hue.b, intensity));
                }
            }
            tex.Apply();
            plumeTexture = tex;
            return plumeTexture;
        }

        // Soft round mote — Particles/Additive with no texture renders a hard square (same reason
        // LaserFxAssets builds one).
        private static Texture2D DotTexture()
        {
            if (dotTexture != null)
            {
                return dotTexture;
            }
            const int size = 16;
            float radius = size * 0.5f;
            dotTexture = new Texture2D(size, size, TextureFormat.ARGB32, false);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float distance = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(radius, radius));
                    dotTexture.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01(1f - distance / radius)));
                }
            }
            dotTexture.Apply();
            return dotTexture;
        }
    }
}
