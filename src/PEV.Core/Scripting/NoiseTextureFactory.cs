using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace PEV.Scripting
{
    /// <summary>
    /// Procedural noise-field textures, modelled on the original BlockRender pipeline.
    ///
    /// The reference effect is not "a noise pattern": it composes three layers from one
    /// low-resolution mask — a translucent fill, a bright edge traced around the mask, and
    /// sparse spark grains — with a displacement field that warps them so the boundary
    /// reads as jagged rather than geometric. That is reproduced here per texel:
    ///
    ///   1. rasterise the union of every region into a cell grid (boolean union, max)
    ///   2. trace the edge as "inside cell with an outside neighbour"
    ///   3. fill = fillColour * fillOpacity, edge = edgeColour * edgeOpacity
    ///   4. sparks = sparse bright grains, sampled through the displacement field
    ///
    /// Working at cell resolution is what gives the authentic blocky silhouette: the
    /// original mask is low-resolution too, and its edge is quantised to those same blocks.
    ///
    /// Frames are ref-counted and shared. Generation is O(cells) per frame, and the result
    /// is only rebuilt when the structure key changes — which is why the key must not
    /// contain anything that drifts every frame (see ResolutionBucket).
    /// </summary>
    public static class NoiseTextureFactory
    {
        public const int MaxSide = 1024;
        public const int MinCells = 8;
        public const int MaxTexelsPerSet = 2_600_000;

        /// <summary>
        /// Pixel widths are quantised to this step before they enter the structure key.
        /// The camera zooms on the beat, so a continuous width would invalidate the key on
        /// every frame and regenerate eight textures per update — the reported stutter.
        /// </summary>
        private const int ResolutionStep = 96;

        private const float GrainPixels = 1.5f;
        public const float PixelsPerUnit = 100f;

        public sealed class Frames
        {
            public Texture2D[] Textures;
            public Sprite[] Sprites;
            /// <summary>
            /// World size of one sprite at scale 1. Sprites are built at 100 pixels per
            /// unit, so this is (texW/100, texH/100). Callers must divide their desired
            /// world size by it: multiplying a scale by the size directly multiplies the
            /// quad by the texture resolution as well.
            /// </summary>
            public Vector2 NaturalSize;
            public string Key;
            internal int Refs;
        }

        public struct Piece
        {
            public Vector2 Offset;
            public Vector2 Half;
            public float Rotation;
            public float Feather;
            public bool Circle;

            // Per-piece colours: a union merges geometry, not appearance. Two regions of
            // different colours at the same depth must each keep their own fill and edge,
            // with only the silhouette shared.
            public Color FillColor;
            public Color EdgeColor;
            public float FillOpacity;
            public float EdgeOpacity;
        }

        public struct Request
        {
            public float WidthWorld;
            public float HeightWorld;
            public Piece[] Pieces;

            public Color FillColor;
            public Color EdgeColor;
            public Color SparkColor;
            public float FillOpacity;
            public float EdgeOpacity;
            public float SparkOpacity;

            public float Brightness;
            public float Saturation;
            public float SampleRate;
            public float LineOpacity;
            public float Displace;

            public int FrameCount;
            public int Seed;
            public float TargetPixelWidth;
        }

        private static readonly Dictionary<string, Frames> Cache =
            new Dictionary<string, Frames>(StringComparer.Ordinal);

        public static int CachedFrameSets => Cache.Count;

        /// <summary>
        /// Monotonic count of texture sets built. The harness samples it across a steady
        /// window: a value that climbs while nothing changed means a key includes
        /// per-frame data, which shows up to the player as stutter.
        /// </summary>
        public static int Generations;

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
                Generations++;
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

        /// <summary>Coarse bucket so camera zoom does not invalidate the texture every frame.</summary>
        public static int ResolutionBucket(float px)
        {
            int steps = Mathf.Clamp(Mathf.RoundToInt(px / ResolutionStep), 1, 12);
            return steps * ResolutionStep;
        }

        /// <summary>
        /// The generation key, exposed so the renderer compares exactly what the factory
        /// caches. A separate key in the renderer drifts from this one and causes either
        /// missed rebuilds or a rebuild on every frame.
        /// </summary>
        public static string Key(Request req) => Fingerprint(req);

        private static string Fingerprint(Request r)
        {
            var sb = new StringBuilder(240);
            sb.Append(F(r.WidthWorld)).Append('|').Append(F(r.HeightWorld)).Append('|')
              .Append(C(r.FillColor)).Append('|').Append(C(r.EdgeColor)).Append('|')
              .Append(C(r.SparkColor)).Append('|')
              .Append(F(r.FillOpacity)).Append('|').Append(F(r.EdgeOpacity)).Append('|')
              .Append(F(r.SparkOpacity)).Append('|')
              .Append(F(r.Brightness)).Append('|').Append(F(r.Saturation)).Append('|')
              .Append(F(r.SampleRate)).Append('|').Append(F(r.LineOpacity)).Append('|')
              .Append(F(r.Displace)).Append('|')
              .Append(r.FrameCount).Append('|').Append(r.Seed).Append('|')
              .Append(ResolutionBucket(r.TargetPixelWidth)).Append('|');

            if (r.Pieces != null)
            {
                foreach (var p in r.Pieces)
                {
                    sb.Append(F(p.Offset.x)).Append(',').Append(F(p.Offset.y)).Append(',')
                      .Append(F(p.Half.x)).Append(',').Append(F(p.Half.y)).Append(',')
                      .Append(F(p.Rotation)).Append(',').Append(F(p.Feather)).Append(',')
                      .Append(p.Circle ? 'c' : 'r').Append(';');
                }
            }
            return sb.ToString();
        }

        private static string F(float v) => v.ToString("F3", CultureInfo.InvariantCulture);
        private static string C(Color c) => F(c.r) + "," + F(c.g) + "," + F(c.b);

        private static Frames Generate(Request req)
        {
            int frames = Mathf.Clamp(req.FrameCount, 2, 32);
            float sampleRate = Mathf.Clamp(req.SampleRate, 0.1f, 8f);
            int block = Mathf.Clamp(Mathf.RoundToInt(GrainPixels / sampleRate), 1, 16);

            int pxW = ResolutionBucket(req.TargetPixelWidth);
            float aspect = req.WidthWorld > 1e-4f ? req.HeightWorld / req.WidthWorld : 1f;
            int pxH = Mathf.Max(16, Mathf.RoundToInt(pxW * aspect));

            int maxCells = Mathf.Max(MinCells, MaxSide / block);
            int cellsX = Mathf.Clamp(Mathf.RoundToInt(pxW / (float)block), MinCells, maxCells);
            int cellsY = Mathf.Clamp(Mathf.RoundToInt(pxH / (float)block), MinCells, maxCells);

            // Over budget, cut the flipbook length first: shrinking the texture would force
            // every grain to cover more screen pixels, which is the blockiness we avoid.
            while (cellsX * block * cellsY * block * frames > MaxTexelsPerSet && frames > 2)
                frames--;

            int texW = cellsX * block;
            int texH = cellsY * block;

            float cellW = req.WidthWorld / cellsX;
            float cellH = req.HeightWorld / cellsY;

            var pieces = req.Pieces ?? Array.Empty<Piece>();

            // --- boolean union -------------------------------------------------------
            // One insideness grid shared by every frame: the silhouette itself does not
            // animate, only the grains and the displacement inside it do. Tracing the edge
            // on the union is what keeps two overlapping regions from drawing two borders.
            var inside = new bool[cellsX * cellsY];
            for (int cy = 0; cy < cellsY; cy++)
            {
                for (int cx = 0; cx < cellsX; cx++)
                {
                    float wx = (cx + 0.5f) / cellsX * req.WidthWorld - req.WidthWorld * 0.5f;
                    float wy = (cy + 0.5f) / cellsY * req.HeightWorld - req.HeightWorld * 0.5f;
                    inside[cy * cellsX + cx] = InUnion(pieces, wx, wy);
                }
            }

            var edge = new bool[cellsX * cellsY];
            for (int cy = 0; cy < cellsY; cy++)
            {
                for (int cx = 0; cx < cellsX; cx++)
                {
                    int i = cy * cellsX + cx;
                    if (!inside[i]) continue;
                    // Edge = inside cell with at least one outside 4-neighbour, the same
                    // test EdgeMask performs with its dilate pass.
                    bool border =
                        cx == 0 || !inside[i - 1] ||
                        cx == cellsX - 1 || !inside[i + 1] ||
                        cy == 0 || !inside[i - cellsX] ||
                        cy == cellsY - 1 || !inside[i + cellsX];
                    edge[i] = border;
                }
            }

            // --- colours -------------------------------------------------------------
            float lum = 0.299f * req.SparkColor.r + 0.587f * req.SparkColor.g + 0.114f * req.SparkColor.b;
            var grey = new Color(lum, lum, lum, 1f);
            var sparkTint = Color.LerpUnclamped(grey, req.SparkColor, Mathf.Clamp01(req.Saturation));

            float sparkDensity = Mathf.Clamp01(0.075f * Mathf.Clamp01(req.Brightness));
            float lineProb = Mathf.Clamp01(req.LineOpacity);
            float displace = Mathf.Clamp01(req.Displace) * 1.6f;
            uint seed = (uint)req.Seed;

            var outFrames = new Frames
            {
                Textures = new Texture2D[frames],
                Sprites = new Sprite[frames]
            };

            // Which piece owns each cell, resolved once. -1 means outside the union.
            var owner = new int[cellsX * cellsY];
            for (int cy = 0; cy < cellsY; cy++)
            {
                for (int cx = 0; cx < cellsX; cx++)
                {
                    float wx = (cx + 0.5f) / cellsX * req.WidthWorld - req.WidthWorld * 0.5f;
                    float wy = (cy + 0.5f) / cellsY * req.HeightWorld - req.HeightWorld * 0.5f;
                    owner[cy * cellsX + cx] = OwnerPiece(pieces, wx, wy);
                }
            }

            for (int k = 0; k < frames; k++)
            {
                var pixels = new Color32[texW * texH];
                uint kseed = (uint)(k * 7919) + seed * 2654435761u;

                var rowLine = new float[cellsY];
                for (int cy = 0; cy < cellsY; cy++)
                {
                    uint h = Hash(0x9E37u, (uint)cy, kseed);
                    rowLine[cy] = (h & 0xFFFF) / 65535f < lineProb
                        ? lineProb * (0.25f + 0.75f * ((h >> 16) & 0xFFFF) / 65535f)
                        : 0f;
                }

                for (int cy = 0; cy < cellsY; cy++)
                {
                    for (int cx = 0; cx < cellsX; cx++)
                    {
                        int idx = cy * cellsX + cx;
                        if (!inside[idx]) continue;

                        // Appearance comes from the piece that owns this cell, so a union
                        // keeps every region's own colour instead of tinting the cluster.
                        int own = owner[idx];
                        var fillC = own >= 0 ? pieces[own].FillColor : req.FillColor;
                        var edgeC = own >= 0 ? pieces[own].EdgeColor : req.EdgeColor;
                        float edgeOpacity = own >= 0 ? pieces[own].EdgeOpacity : req.EdgeOpacity;
                        float fillOpacity = own >= 0 ? pieces[own].FillOpacity : req.FillOpacity;

                        bool isEdge = edge[idx];

                        Color32 c;
                        if (isEdge)
                        {
                            // The boundary is opaque and saturated; that is what makes the
                            // region read as a cut-out rather than a soft glow.
                            float wobble = 0.85f + 0.15f * ((Hash((uint)cx, (uint)cy, kseed + 31u) & 0xFFFF) / 65535f);
                            c = new Color32(
                                (byte)Mathf.RoundToInt(edgeC.r * 255f * wobble),
                                (byte)Mathf.RoundToInt(edgeC.g * 255f * wobble),
                                (byte)Mathf.RoundToInt(edgeC.b * 255f * wobble),
                                (byte)Mathf.RoundToInt(Mathf.Clamp01(edgeOpacity) * 255f));
                        }
                        else
                        {
                            // Fill is translucent and carries most of the region's colour.
                            float a = Mathf.Clamp01(fillOpacity);

                            // Sparse bright grains, sampled through the displacement field:
                            // the warp is what makes the texture look torn rather than tiled.
                            float du = 0f, dv = 0f;
                            if (displace > 0f)
                            {
                                uint dh = Hash((uint)cx, (uint)cy, kseed ^ 0x51EDu);
                                du = (((dh & 0xFF) / 255f) - 0.5f) * displace;
                                dv = ((((dh >> 8) & 0xFF) / 255f) - 0.5f) * displace;
                            }
                            int sx = Mathf.Clamp(cx + Mathf.RoundToInt(du * 2f), 0, cellsX - 1);
                            int sy = Mathf.Clamp(cy + Mathf.RoundToInt(dv * 2f), 0, cellsY - 1);

                            uint gh = Hash((uint)sx, (uint)sy, kseed);
                            float gr = (gh & 0xFFFF) / 65535f;
                            float lit = 0f;
                            if (gr < sparkDensity)
lit = req.SparkOpacity * (0.30f + 0.70f * Mathf.Pow(((gh >> 16) & 0xFFFF) / 65535f, 2f));

                            float lineA = rowLine[cy];
                            if (lineA > 0f) lit = Mathf.Clamp01(lit + lineA * 0.5f);

                            float r = fillC.r + (sparkTint.r - fillC.r) * lit;
                            float g = fillC.g + (sparkTint.g - fillC.g) * lit;
                            float b = fillC.b + (sparkTint.b - fillC.b) * lit;
                            a = Mathf.Clamp01(a + lit * (1f - a));

                            c = new Color32(
                                (byte)Mathf.RoundToInt(Mathf.Clamp01(r) * 255f),
                                (byte)Mathf.RoundToInt(Mathf.Clamp01(g) * 255f),
                                (byte)Mathf.RoundToInt(Mathf.Clamp01(b) * 255f),
                                (byte)Mathf.RoundToInt(a * 255f));
                        }

                        // One texel block is one cell: quantisation is the look, not an
                        // artefact. Bilinear upscaling keeps it soft like the original.
                        int x0 = cx * block, y0 = cy * block;
                        for (int y = y0; y < y0 + block; y++)
                        {
                            int row = y * texW;
                            for (int x = x0; x < x0 + block; x++) pixels[row + x] = c;
                        }
                    }
                }

                var tex = new Texture2D(texW, texH, TextureFormat.RGBA32, false)
                {
                    wrapMode = TextureWrapMode.Repeat,
                    filterMode = FilterMode.Bilinear,
                    name = "PEV.Noise." + k
                };
                tex.SetPixels32(pixels);
                tex.Apply(false);

                outFrames.Textures[k] = tex;
                outFrames.Sprites[k] = Sprite.Create(
                    tex, new Rect(0f, 0f, texW, texH), new Vector2(0.5f, 0.5f), PixelsPerUnit);
                outFrames.Sprites[k].name = "PEV.NoiseSprite." + k;
            }

            outFrames.NaturalSize = new Vector2(texW / PixelsPerUnit, texH / PixelsPerUnit);
            return outFrames;
        }

        /// <summary>
        /// Index of the piece containing this point, or -1. Pieces are independent shapes,
        /// so the first match decides the colour; later pieces only extend the silhouette.
        /// </summary>
        private static int OwnerPiece(Piece[] pieces, float px, float py)
        {
            for (int i = 0; i < pieces.Length; i++)
            {
                var p = pieces[i];
                float dx = px - p.Offset.x;
                float dy = py - p.Offset.y;

                float rad = p.Rotation * Mathf.Deg2Rad;
                float cos = Mathf.Cos(rad), sin = Mathf.Sin(rad);
                float lx = dx * cos + dy * sin;
                float ly = -dx * sin + dy * cos;

                if (p.Circle)
                {
                    if (Mathf.Sqrt(lx * lx + ly * ly) <= p.Half.x) return i;
                }
                else if (Mathf.Abs(lx) <= p.Half.x && Mathf.Abs(ly) <= p.Half.y)
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>Boolean union of every piece at this world position.</summary>
        private static bool InUnion(Piece[] pieces, float px, float py)
        {
            if (pieces.Length == 0) return true;

            for (int i = 0; i < pieces.Length; i++)
            {
                var p = pieces[i];
                float dx = px - p.Offset.x;
                float dy = py - p.Offset.y;

                float rad = p.Rotation * Mathf.Deg2Rad;
                float cos = Mathf.Cos(rad), sin = Mathf.Sin(rad);
                float lx = dx * cos + dy * sin;
                float ly = -dx * sin + dy * cos;

                if (p.Circle)
                {
                    float d = Mathf.Sqrt(lx * lx + ly * ly);
                    if (d <= Mathf.Max(0f, p.Half.x - p.Feather * 0.5f)) return true;
                }
                else
                {
                    if (Mathf.Abs(lx) <= p.Half.x && Mathf.Abs(ly) <= p.Half.y) return true;
                }
            }
            return false;
        }

        private static uint Hash(uint a, uint b, uint c)
        {
            uint h = a * 374761393u + b * 668265263u + c * 2147483647u;
            h = (h ^ (h >> 13)) * 1274126177u;
            return h ^ (h >> 16);
        }
    }
}
