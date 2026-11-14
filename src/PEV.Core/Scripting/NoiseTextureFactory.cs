using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace PEV.Scripting
{
    /// <summary>
    /// Procedural noise frames for the NoiseField event.
    ///
    /// The Phigros effect is a pre-generated sequence of K noise textures stepped through
    /// time, not a per-frame shader. We reproduce that: generate K blocky textures once from
    /// a deterministic integer hash, then the renderer swaps sprites and scrolls UVs.
    /// There is no per-pixel work at runtime.
    ///
    /// A request carries a list of mask pieces rather than one shape. That is what makes
    /// same-depth fields merge into a union: several regions are baked into one texture, so
    /// overlapping areas never double their brightness.
    ///
    /// Frames are ref-counted and shared between identical requests. Without the count, the
    /// second instance to release would leave the first one rendering a destroyed texture.
    /// </summary>
    public static class NoiseTextureFactory
    {
        public const int MaxSide = 1024;
        public const int MinCells = 8;
        /// <summary>
        /// Upper bound on texels across all K frames of one set. Generation runs inside a
        /// host call from the script thread, so a set that takes longer than a frame hitch
        /// is a visible stall.
        /// </summary>
        public const int MaxTexelsPerSet = 2_600_000;

        /// <summary>
        /// Grains are this many screen pixels wide at noiseSampleRate 1. The reference
        /// effect is fine dust, not blocks: at 6 px the result read as a coarse mosaic.
        /// </summary>
        private const float GrainPixels = 1.5f;

        public sealed class Frames
        {
            public Texture2D[] Textures;
            public Sprite[] Sprites;
            public string Key;
            internal int Refs;
        }

        /// <summary>One region participating in the union, in texture-local world space.</summary>
        public struct Piece
        {
            public Vector2 Offset;   // centre relative to the texture centre
            public Vector2 Half;     // half extents; for a circle, Half.x is the radius
            public float Rotation;   // degrees, counter-clockwise
            public float Feather;    // world units of edge falloff
            public bool Circle;
        }

        public struct Request
        {
            public float WidthWorld;
            public float HeightWorld;
            public Piece[] Pieces;
            public Color Tint;
            public float Brightness;
            public float Saturation;
            public float SampleRate;
            public float LineOpacity;
            public int FrameCount;
            public int Seed;
            /// <summary>Region width in screen pixels; drives texture resolution.</summary>
            public float TargetPixelWidth;
        }

        private static readonly Dictionary<string, Frames> Cache =
            new Dictionary<string, Frames>(StringComparer.Ordinal);

        public static int CachedFrameSets => Cache.Count;

        /// <summary>Returns shared frames for the request, generating them on first use.</summary>
        public static Frames Get(Request req, out string error)
        {
            error = null;
            string key = Fingerprint(req);
            if (Cache.TryGetValue(key, out var hit) && HitIsAlive(hit))
            {
                hit.Refs++;
                return hit;
            }

            try
            {
                var frames = Generate(req);
                frames.Key = key;
                frames.Refs = 1;
                Cache[key] = frames;
                return frames;
            }
            catch (Exception e)
            {
                error = "noise texture generation failed: " + e.Message;
                return null;
            }
        }

        /// <summary>Drops one reference; destroys the textures when the last user lets go.</summary>
        public static void Release(Frames frames)
        {
            if (frames == null) return;
            frames.Refs--;
            if (frames.Refs > 0) return;
            if (frames.Key != null) Cache.Remove(frames.Key);
            DestroyFrames(frames);
        }

        public static void Clear()
        {
            foreach (var kv in Cache) DestroyFrames(kv.Value);
            Cache.Clear();
        }

        private static void DestroyFrames(Frames frames)
        {
            if (frames.Sprites != null)
            {
                foreach (var s in frames.Sprites)
                    if (s != null) UnityEngine.Object.Destroy(s);
                frames.Sprites = null;
            }
            if (frames.Textures != null)
            {
                foreach (var t in frames.Textures)
                    if (t != null) UnityEngine.Object.Destroy(t);
                frames.Textures = null;
            }
        }

        private static bool HitIsAlive(Frames f)
        {
            if (f.Textures == null || f.Sprites == null) return false;
            foreach (var t in f.Textures) if (t == null) return false;
            foreach (var s in f.Sprites) if (s == null) return false;
            return true;
        }

        private static string Fingerprint(Request r)
        {
            var sb = new StringBuilder(220);
            sb.Append(F(r.WidthWorld)).Append('|')
              .Append(F(r.HeightWorld)).Append('|')
              .Append(F(r.Tint.r)).Append(',')
              .Append(F(r.Tint.g)).Append(',')
              .Append(F(r.Tint.b)).Append('|')
              .Append(F(r.Brightness)).Append('|')
              .Append(F(r.Saturation)).Append('|')
              .Append(F(r.SampleRate)).Append('|')
              .Append(F(r.LineOpacity)).Append('|')
              .Append(r.FrameCount).Append('|')
              .Append(r.Seed).Append('|')
              .Append(Mathf.RoundToInt(r.TargetPixelWidth)).Append('|');

            if (r.Pieces != null)
            {
                foreach (var p in r.Pieces)
                {
                    sb.Append(F(p.Offset.x)).Append(',')
                      .Append(F(p.Offset.y)).Append(',')
                      .Append(F(p.Half.x)).Append(',')
                      .Append(F(p.Half.y)).Append(',')
                      .Append(F(p.Rotation)).Append(',')
                      .Append(F(p.Feather)).Append(',')
                      .Append(p.Circle ? 'c' : 'r').Append(';');
                }
            }
            return sb.ToString();
        }

        private static string F(float v) => v.ToString("F3", CultureInfo.InvariantCulture);

        /// <summary>
        /// Builds the K frames. One texel block is one grain; cells are quantised to block
        /// pixels so the blocky look survives bilinear upscaling.
        /// </summary>
        private static Frames Generate(Request req)
        {
            int frames = Mathf.Clamp(req.FrameCount, 2, 32);
            float sampleRate = Mathf.Clamp(req.SampleRate, 0.1f, 8f);
            float worldToPx = req.WidthWorld > 1e-4f ? req.TargetPixelWidth / req.WidthWorld : 1f;
            // One grain may occupy a single texel: when the texture cannot be large enough, a
            // 1-texel grain still reads as fine dust, whereas forcing a 2-texel minimum
            // doubles the grain size exactly when the region is largest.
            int block = Mathf.Clamp(Mathf.RoundToInt(GrainPixels / sampleRate), 1, 16);

            float worldToPxY = req.HeightWorld > 1e-4f
                ? (req.TargetPixelWidth * (req.HeightWorld / req.WidthWorld)) / req.HeightWorld
                : worldToPx;
            int pxW = Mathf.Max(16, Mathf.RoundToInt(req.WidthWorld * worldToPx));
            int pxH = Mathf.Max(16, Mathf.RoundToInt(req.HeightWorld * worldToPxY));

            int maxCells = Mathf.Max(MinCells, MaxSide / block);
            int cellsX = Mathf.Clamp(Mathf.RoundToInt(pxW / (float)block), MinCells, maxCells);
            int cellsY = Mathf.Clamp(Mathf.RoundToInt(pxH / (float)block), MinCells, maxCells);

            // Over budget, cut the flipbook length first. Shrinking the texture would force
            // every grain to cover more screen pixels, which is exactly the blocky look we
            // are trying to avoid; a shorter flipbook with the same UV scroll reads almost
            // identically because the scroll carries most of the motion.
            while (cellsX * block * cellsY * block * frames > MaxTexelsPerSet && frames > 2)
                frames--;

            int texW = cellsX * block;
            int texH = cellsY * block;

            // Tint with saturation applied at generation time. Per-frame we then only touch
            // opacity, so the common case never re-uploads pixels.
            float lum = 0.299f * req.Tint.r + 0.587f * req.Tint.g + 0.114f * req.Tint.b;
            var grey = new Color(lum, lum, lum, 1f);
            var grain = Color.LerpUnclamped(grey, req.Tint, Mathf.Clamp01(req.Saturation));

            // Sparse on purpose: the reference is fine dust with scattered bright specks,
            // not a filled red rectangle. Density rises with brightness so the parameter
            // still reads as "how much noise".
            float density = Mathf.Clamp01(0.10f * Mathf.Clamp01(req.Brightness));
            float lineProb = Mathf.Clamp01(req.LineOpacity);
            uint seed = (uint)req.Seed;
            var pieces = req.Pieces ?? Array.Empty<Piece>();

            var outFrames = new Frames
            {
                Textures = new Texture2D[frames],
                Sprites = new Sprite[frames]
            };

            for (int k = 0; k < frames; k++)
            {
                var pixels = new Color32[texW * texH];
                uint kseed = (uint)(k * 7919) + seed * 2654435761u;

                var rowAlpha = new float[cellsY];
                for (int cy = 0; cy < cellsY; cy++)
                {
                    uint h = Hash(0x9E37u, (uint)cy, kseed);
                    rowAlpha[cy] = (h & 0xFFFF) / 65535f < lineProb
                        ? lineProb * (0.25f + 0.75f * ((h >> 16) & 0xFFFF) / 65535f)
                        : 0f;
                }

                for (int cy = 0; cy < cellsY; cy++)
                {
                    for (int cx = 0; cx < cellsX; cx++)
                    {
                        uint h = Hash((uint)cx, (uint)cy, kseed);
                        float r = (h & 0xFFFF) / 65535f;
                        if (r >= density) continue;

                        // Skew toward dim grains with a few hot ones: a uniform brightness
                        // distribution reads as flat speckle instead of dust.
                        float r2 = ((h >> 16) & 0xFFFF) / 65535f;
                        float lit = 0.35f + 0.65f * Mathf.Sqrt(r2);
                        float a = lit;

                        float lineA = rowAlpha[cy];
                        if (lineA > 0f) a = Mathf.Clamp01(a + lineA * (0.35f + 0.65f * lit));

                        var c = new Color32(
                            (byte)Mathf.RoundToInt(grain.r * 255f),
                            (byte)Mathf.RoundToInt(grain.g * 255f),
                            (byte)Mathf.RoundToInt(grain.b * 255f),
                            (byte)Mathf.RoundToInt(a * 255f));

                        int x0 = cx * block, y0 = cy * block;
                        for (int y = y0; y < y0 + block; y++)
                        {
                            int row = y * texW;
                            for (int x = x0; x < x0 + block; x++)
                            {
                                float u = (x + 0.5f) / texW;
                                float v = (y + 0.5f) / texH;
                                float m = Mask(req, pieces, u, v);
                                if (m <= 0f) continue;
                                c.a = (byte)Mathf.RoundToInt(a * m * 255f);
                                pixels[row + x] = c;
                            }
                        }
                    }
                }

                var tex = new Texture2D(texW, texH, TextureFormat.RGBA32, false)
                {
                    // Scrolling depends on Repeat: Clamp would show a frozen band at the edge.
                    wrapMode = TextureWrapMode.Repeat,
                    filterMode = FilterMode.Bilinear,
                    name = "PEV.Noise." + k
                };
                tex.SetPixels32(pixels);
                tex.Apply(false);

                outFrames.Textures[k] = tex;
                outFrames.Sprites[k] = Sprite.Create(
                    tex, new Rect(0f, 0f, texW, texH), new Vector2(0.5f, 0.5f), 100f);
                outFrames.Sprites[k].name = "PEV.NoiseSprite." + k;
            }

            return outFrames;
        }

        /// <summary>Union of every piece's mask at this texel. Overlaps take the max, never sum.</summary>
        private static float Mask(Request req, Piece[] pieces, float u, float v)
        {
            if (pieces.Length == 0) return 1f;

            float px = (u - 0.5f) * req.WidthWorld;
            float py = (v - 0.5f) * req.HeightWorld;
            float best = 0f;

            for (int i = 0; i < pieces.Length; i++)
            {
                var p = pieces[i];
                float dx = px - p.Offset.x;
                float dy = py - p.Offset.y;

                // Rotate into the piece's local frame (inverse of a CCW Z rotation).
                float rad = p.Rotation * Mathf.Deg2Rad;
                float cos = Mathf.Cos(rad), sin = Mathf.Sin(rad);
                float lx = dx * cos + dy * sin;
                float ly = -dx * sin + dy * cos;

                float m;
                if (p.Circle)
                {
                    float d = Mathf.Sqrt(lx * lx + ly * ly);
                    float edge = Mathf.Max(1e-4f, p.Half.x);
                    m = p.Feather <= 0f
                        ? (d <= edge ? 1f : 0f)
                        : Mathf.Clamp01((edge - d) / p.Feather);
                }
                else
                {
                    float inside = Mathf.Min(p.Half.x - Mathf.Abs(lx), p.Half.y - Mathf.Abs(ly));
                    m = p.Feather <= 0f
                        ? (inside >= 0f ? 1f : 0f)
                        : Mathf.Clamp01(inside / Mathf.Max(1e-4f, p.Feather));
                }

                if (m > best) best = m;
                if (best >= 1f) break;
            }

            return best;
        }

        /// <summary>Deterministic integer hash. Never UnityEngine.Random: no global state.</summary>
        private static uint Hash(uint a, uint b, uint c)
        {
            uint h = a * 374761393u + b * 668265263u + c * 2147483647u;
            h = (h ^ (h >> 13)) * 1274126177u;
            return h ^ (h >> 16);
        }
    }
}
