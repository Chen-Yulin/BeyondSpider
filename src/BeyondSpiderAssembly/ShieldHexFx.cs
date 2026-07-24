using System.Collections.Generic;
using UnityEngine;

namespace BeyondSpiderAssembly
{
    // Localized hit feedback for the shield field (replaces the old whole-mesh _TintColor flash):
    // every round/missile the field is actively decelerating keeps a "hit source" alive here, and
    // this component repaints the shield mesh's vertex colors each rendered frame — a gaussian
    // patch around each source modulated by outward-travelling cosine rings (the water ripple),
    // multiplied by a hex-cell pattern baked into the material's texture.
    //
    // This IS the "shader", split across the only two inputs the stock pipeline gives us: Besiege's
    // Unity build has no runtime shader compilation (new Material(string) is long dead) and this
    // repo has no Unity-editor project to build shader AssetBundles (the shipped .ab files are
    // migrated prefabs), so the effect targets the built-in Particles/Additive shader — hex
    // pattern in _MainTex, ripple envelope in per-vertex colors. _TintColor must stay at the
    // shader-neutral (0.5,0.5,0.5,0.5): the shader doubles it, so additive output becomes
    // (vertColor.rgb*tex.rgb)*(vertColor.a*tex.a) — the same intensity-squared response the old
    // MinIdleAlpha/MaxIdleAlpha constants were tuned against when _TintColor carried everything.
    public class ShieldHexFx : MonoBehaviour
    {
        private const int MaxSources = 16;
        // World-space hex cell size (center-to-center spacing). Deliberately small next to the
        // default 120-radius aperture so a hit reads as "a patch of cells", never the whole field.
        public const float HexCellSize = 9f;
        // The generated texture tiles HexCols hex columns horizontally and HexRows rectangular
        // lattice periods (each sqrt(3) lattice-units tall) vertically; both must stay integers or
        // the texture stops tiling seamlessly.
        private const int HexCols = 4;
        private const int HexRows = 3;
        private const float RowPeriod = 1.7320508f; // sqrt(3): vertical rect period of a hex lattice
        // Ripple shaping. The patch radius scales with the aperture (with a floor so small shields
        // still show something); rings travel outward one wavelength per WavePeriod; a source
        // lingers FadeAfterHit seconds after its projectile stops being decelerated.
        private const float PatchRadiusFraction = 0.18f;
        private const float MinPatchRadius = 12f;
        private const float WaveLengthFraction = 0.8f; // of patch radius; the mesh tessellation
        // (RingCount/SegmentCount in ShieldProjectorBlock) is sized to sample roughly 2 verts per
        // wavelength at the rim — shrink this and the rings alias into shimmer.
        private const float WavePeriod = 0.45f;
        private const float FadeAfterHit = 0.35f;
        private const float TwoPi = 6.2831853f;
        // Overload ripples (foreign-shield overlap, see ShieldProjectorBlock.OverlapsForeignShield):
        // fixed alarm-red instead of the field hue, bigger patch, faster rings, and an
        // over-unity amplitude that slams the patch center into saturation — unmistakably
        // different from ordinary hit feedback.
        private static readonly Color OverloadColor = new Color(1f, 0.12f, 0.08f);
        private const float OverloadAmplitude = 1.6f;
        private const float OverloadPatchScale = 1.7f;
        private const float OverloadWavePeriod = 0.28f;

        private struct HitSource
        {
            public object Key;
            public Vector3 LocalPos;
            public float BirthTime;
            public float LastRefresh;
            public bool Overload;
        }

        private static Texture2D hexTexture;

        private readonly List<HitSource> sources = new List<HitSource>();
        private MeshFilter meshFilter;
        private Vector3[] localVerts;
        private Color32[] colors;
        // Per-vertex accumulation buffers (kept allocated between frames): ripple sources can
        // carry different colors (overload red vs field hue), so contributions accumulate in
        // float rgba before the final clamp-and-convert pass.
        private float[] accumR;
        private float[] accumG;
        private float[] accumB;
        private float[] accumA;
        private Color hue = Color.white;
        private float idleAlpha;
        private float patchRadius = MinPatchRadius;
        // True whenever the colors currently baked into the mesh may no longer match the idle
        // state (a ripple just ended, hue/idle changed, mesh rebuilt) — lets Update be a no-op on
        // quiet frames instead of rewriting ~2k vertex colors forever.
        private bool repaintNeeded = true;

        public bool HasActiveFx { get { return sources.Count > 0; } }

        public void Configure(MeshFilter filter, float fieldRadius)
        {
            meshFilter = filter;
            OnMeshChanged(fieldRadius);
        }

        // Call after every mesh rebuild: re-caches vertices and rescales the ripple patch.
        public void OnMeshChanged(float fieldRadius)
        {
            if (meshFilter == null || meshFilter.sharedMesh == null)
            {
                return;
            }
            patchRadius = Mathf.Max(MinPatchRadius, PatchRadiusFraction * fieldRadius);
            localVerts = meshFilter.sharedMesh.vertices;
            colors = new Color32[localVerts.Length];
            accumR = new float[localVerts.Length];
            accumG = new float[localVerts.Length];
            accumB = new float[localVerts.Length];
            accumA = new float[localVerts.Length];
            repaintNeeded = true;
        }

        public void SetBase(Color hueColor, float idle)
        {
            // Epsilon on idle: upkeepRatio jitters a little every tick and shouldn't force a full
            // vertex-color rewrite while the field is otherwise quiet.
            if (hueColor != hue || Mathf.Abs(idle - idleAlpha) > 0.004f)
            {
                hue = hueColor;
                idleAlpha = idle;
                repaintNeeded = true;
            }
        }

        // localPos is in this transform's (vis mesh) space. key identifies the projectile (or, for
        // overload ripples, the foreign projector) so a round being bled — or an overlap lasting —
        // over many consecutive ticks keeps refreshing (and moving) one ripple source instead of
        // spawning a new ripple per tick.
        public void RegisterHit(object key, Vector3 localPos)
        {
            RegisterHit(key, localPos, false);
        }

        public void RegisterHit(object key, Vector3 localPos, bool overload)
        {
            float now = Time.time;
            for (int i = 0; i < sources.Count; i++)
            {
                if (ReferenceEquals(sources[i].Key, key))
                {
                    HitSource refreshed = sources[i];
                    refreshed.LocalPos = localPos;
                    refreshed.LastRefresh = now;
                    refreshed.Overload = overload;
                    sources[i] = refreshed;
                    return;
                }
            }
            HitSource source = new HitSource();
            source.Key = key;
            source.LocalPos = localPos;
            source.BirthTime = now;
            source.LastRefresh = now;
            source.Overload = overload;
            if (sources.Count >= MaxSources)
            {
                int stalest = 0;
                for (int i = 1; i < sources.Count; i++)
                {
                    if (sources[i].LastRefresh < sources[stalest].LastRefresh)
                    {
                        stalest = i;
                    }
                }
                sources[stalest] = source;
                return;
            }
            sources.Add(source);
        }

        private void Update()
        {
            if (meshFilter == null || localVerts == null)
            {
                return;
            }
            float now = Time.time;
            for (int i = sources.Count - 1; i >= 0; i--)
            {
                if (now - sources[i].LastRefresh > FadeAfterHit)
                {
                    sources.RemoveAt(i);
                    repaintNeeded = true;
                }
            }
            if (sources.Count == 0 && !repaintNeeded)
            {
                return;
            }

            int vertCount = localVerts.Length;
            float idleR = hue.r * idleAlpha;
            float idleG = hue.g * idleAlpha;
            float idleB = hue.b * idleAlpha;
            for (int v = 0; v < vertCount; v++)
            {
                accumR[v] = idleR;
                accumG[v] = idleG;
                accumB[v] = idleB;
                accumA[v] = idleAlpha;
            }

            for (int s = 0; s < sources.Count; s++)
            {
                HitSource source = sources[s];
                float radius = source.Overload ? patchRadius * OverloadPatchScale : patchRadius;
                float radiusSq = radius * radius;
                float cutoffSq = radiusSq * 4.84f; // 2.2 patch radii out the envelope is < 1%
                float waveLength = radius * WaveLengthFraction;
                float period = source.Overload ? OverloadWavePeriod : WavePeriod;
                Color tint = source.Overload ? OverloadColor : hue;
                float fade = 1f - Mathf.Clamp01((now - source.LastRefresh) / FadeAfterHit);
                float amp = (source.Overload ? OverloadAmplitude : 1f) * fade;
                float agePhase = (now - source.BirthTime) / period;
                float sx = source.LocalPos.x;
                float sy = source.LocalPos.y;
                float sz = source.LocalPos.z;
                for (int v = 0; v < vertCount; v++)
                {
                    Vector3 vert = localVerts[v];
                    float dx = vert.x - sx;
                    float dy = vert.y - sy;
                    float dz = vert.z - sz;
                    float distSq = dx * dx + dy * dy + dz * dz;
                    if (distSq > cutoffSq)
                    {
                        continue;
                    }
                    float dist = Mathf.Sqrt(distSq);
                    float wave = 0.6f + 0.4f * Mathf.Cos((dist / waveLength - agePhase) * TwoPi);
                    float contribution = amp * Mathf.Exp(-distSq / radiusSq) * wave;
                    accumR[v] += contribution * tint.r;
                    accumG[v] += contribution * tint.g;
                    accumB[v] += contribution * tint.b;
                    accumA[v] += contribution;
                }
            }

            for (int v = 0; v < vertCount; v++)
            {
                float r = accumR[v] < 1f ? accumR[v] : 1f;
                float g = accumG[v] < 1f ? accumG[v] : 1f;
                float b = accumB[v] < 1f ? accumB[v] : 1f;
                float a = accumA[v] < 1f ? accumA[v] : 1f;
                colors[v] = new Color32((byte)(r * 255f), (byte)(g * 255f), (byte)(b * 255f), (byte)(a * 255f));
            }
            meshFilter.sharedMesh.colors32 = colors;
            repaintNeeded = false;
        }

        // Shared, lazily generated tileable hex-grid pattern: bright thin cell borders over a dim
        // interior. Lives in both rgb and alpha so the additive shader's src.rgb*src.a blend
        // squares it — borders end up ~8x hotter than cell interiors.
        public static Texture2D GetHexTexture()
        {
            if (hexTexture != null)
            {
                return hexTexture;
            }
            const int width = 256;
            const int height = 256;
            hexTexture = new Texture2D(width, height, TextureFormat.ARGB32, true);
            hexTexture.wrapMode = TextureWrapMode.Repeat;
            hexTexture.filterMode = FilterMode.Bilinear;
            Color32[] pixels = new Color32[width * height];
            for (int y = 0; y < height; y++)
            {
                float v = (y + 0.5f) / height * HexRows * RowPeriod;
                for (int x = 0; x < width; x++)
                {
                    float u = (x + 0.5f) / width * HexCols;
                    float edge = HexEdgeDistance(u, v);
                    float border = Mathf.InverseLerp(0.40f, 0.5f, edge);
                    float value = Mathf.Lerp(0.35f, 1f, border * border);
                    byte b = (byte)(value * 255f);
                    pixels[y * width + x] = new Color32(b, b, b, b);
                }
            }
            hexTexture.SetPixels32(pixels);
            hexTexture.Apply(true);
            return hexTexture;
        }

        // World-space size one repeat of the hex texture should cover, for Material.mainTextureScale.
        public static Vector2 HexTextureWorldSize()
        {
            return new Vector2(HexCols * HexCellSize, HexRows * RowPeriod * HexCellSize);
        }

        // Hex-norm distance to the nearest cell center, in lattice units (centers are 1 apart, the
        // cell border sits exactly at 0.5). Classic two-interleaved-rect-lattices construction:
        // pick the closer of the two candidate centers by euclidean distance, measure with the
        // hexagonal norm so the value is constant along the whole border.
        private static float HexEdgeDistance(float u, float v)
        {
            float ax = Mathf.Repeat(u, 1f) - 0.5f;
            float ay = Mathf.Repeat(v, RowPeriod) - RowPeriod * 0.5f;
            float bx = Mathf.Repeat(u - 0.5f, 1f) - 0.5f;
            float by = Mathf.Repeat(v - RowPeriod * 0.5f, RowPeriod) - RowPeriod * 0.5f;
            if (ax * ax + ay * ay <= bx * bx + by * by)
            {
                return HexNorm(ax, ay);
            }
            return HexNorm(bx, by);
        }

        private static float HexNorm(float x, float y)
        {
            x = Mathf.Abs(x);
            y = Mathf.Abs(y);
            return Mathf.Max(x, 0.5f * x + 0.8660254f * y);
        }
    }
}
