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

            /// <summary>
            /// Coastline band for this piece, in world units. Per piece, not per cluster:
            /// a small rectangle sharing a depth with a large region would otherwise be
            /// eroded by that region's band and end up as debris. Its own size decides.
            /// </summary>
            public float Band;

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

            /// <summary>
            /// How far the coastline may wander, as a fraction of the field's shorter side.
            /// This is the "edge distance": at 0 the region is its plain geometry, at 1 the
            /// bays reach about half a field-width inward.
            /// </summary>
            public float EdgeDistance;

            /// <summary>
            /// Number of noise features across the shorter side. Low values give a few big
            /// bays; high values give a finely shredded coast.
            /// </summary>
            public float EdgeDetail;

            /// <summary>Border line thickness, in mask cells.</summary>
            public int EdgeThickness;

            /// <summary>
            /// How far detached fragments survive outside the geometry, as a fraction of
            /// the shorter side. Also controls how much of the quad is padded to show them.
            /// </summary>
            public float EdgeMargin;

            /// <summary>
            /// How much debris survives outside the coast. 0 leaves a clean boundary; higher
            /// values scatter torn fragments around the region.
            /// </summary>
            public float EdgeFragment;

            public int FrameCount;
            public int Seed;
            public float TargetPixelWidth;

            /// <summary>
            /// Horizontal slice displacement, 0..1. Rows are shifted sideways by a
            /// per-frame amount, which is the reference's GlitchMap: whole scanlines of the
            /// field tear sideways for a frame and snap back.
            /// </summary>
            public float Glitch;

            /// <summary>
            /// World-space padding on every side of the quad, beyond the geometry's own
            /// bounds. Without it the coastline can only bite inward and detached flakes
            /// are clipped away at the quad edge; with it they drift outside the region and
            /// stay visible, which is what the reference shows.
            /// </summary>
            public float Margin;
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
              .Append(F(r.Glitch)).Append('|')
              .Append(F(r.EdgeDistance)).Append('|').Append(F(r.EdgeDetail)).Append('|')
              .Append(r.EdgeThickness).Append('|')
              .Append(F(r.EdgeMargin)).Append('|').Append(F(r.EdgeFragment)).Append('|')
              .Append(r.FrameCount).Append('|').Append(r.Seed).Append('|')
              .Append(F(r.Margin)).Append('|')
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

            // The quad is the geometry box grown by the margin on every side. Geometry
            // size stays the reference for the coastline band and feature size, so padding
            // the quad does not change the shape — only how much of it survives clipping.
            float margin = Mathf.Max(0f, req.Margin);
            float totalW = req.WidthWorld + margin * 2f;
            float totalH = req.HeightWorld + margin * 2f;
            if (totalW < 1e-3f) totalW = 1e-3f;
            if (totalH < 1e-3f) totalH = 1e-3f;

            int pxW = ResolutionBucket(req.TargetPixelWidth);
            float aspect = totalH / totalW;
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

            // --- coverage parameters --------------------------------------------------
            // Mirrors the reference's ActiveBlock pass. Its mask is not a warped
            // silhouette: coverage is a level set of a scrolling noise field, with the
            // threshold slid by the signed distance to the region. That is the mechanism
            // behind the behaviour a warped outline cannot produce — bays pinching off
            // into islands, islands drifting back and merging, flakes detaching from the
            // coast, holes flickering open inside the fill.
            //
            // The numbers that shape the coast are author-facing parameters now (see
            // Request.EdgeDistance &c.), so events can dial the look without rebuilding.
            // Displace still gates the whole effect: at 0 the region is plain geometry.
            float displace = Mathf.Clamp01(req.Displace);
            uint seed = (uint)req.Seed;

            // Cells are the staircase unit. Fixed in screen pixels rather than in field
            // units, because the reference's cell size is a screen-space constant: zooming
            // in must not make the steps bigger.
            int maskDiv = Mathf.Clamp(Mathf.RoundToInt(11f / Mathf.Max(1, block)), 1, 12);
            int maskW = Mathf.Max(3, Mathf.CeilToInt(cellsX / (float)maskDiv));
            int maskH = Mathf.Max(3, Mathf.CeilToInt(cellsY / (float)maskDiv));

            // Feature size: EdgeDetail noise cells across the shorter side. Larger features
            // give big smooth bays, smaller ones give confetti.
            float minSide = Mathf.Max(0.01f, Mathf.Min(req.WidthWorld, req.HeightWorld));
            float detail = Mathf.Clamp(req.EdgeDetail <= 0f ? 8f : req.EdgeDetail, 1f, 32f);
            float noiseFreq = detail / minSide;

            // Threshold swing, asymmetric on purpose. Inside, the threshold sits well below
            // the noise median so the fill stays solid with only occasional holes. Outside,
            // it sits below the noise maximum so isolated peaks survive as detached flakes
            // rather than vanishing — symmetric swings lose those flakes entirely.
            float swingIn = 0.45f;
            // EdgeFragment 1.0 (the default) keeps the reference's amount of debris; 0 makes
            // the exterior threshold unreachable so the coast is clean.
            float fragment = Mathf.Clamp01(req.EdgeFragment <= 0f ? 1f : req.EdgeFragment);
            float swingOut = 0.32f * fragment;

            // --- layers ---------------------------------------------------------------
            // One mask per frame, so these live outside the per-frame loop and are reused.
            var insideGrid = new bool[cellsX * cellsY];
            var edgeGrid = new bool[cellsX * cellsY];
            var ownerGrid = new int[cellsX * cellsY];
            var coarseIn = new bool[maskW * maskH];
            var coarseOwner = new int[maskW * maskH];

            // --- colours -------------------------------------------------------------
            float lum = 0.299f * req.SparkColor.r + 0.587f * req.SparkColor.g + 0.114f * req.SparkColor.b;
            var grey = new Color(lum, lum, lum, 1f);
            var sparkTint = Color.LerpUnclamped(grey, req.SparkColor, Mathf.Clamp01(req.Saturation));

            float sparkDensity = Mathf.Clamp01(0.075f * Mathf.Clamp01(req.Brightness));
            float lineProb = Mathf.Clamp01(req.LineOpacity);

            var outFrames = new Frames
            {
                Textures = new Texture2D[frames],
                Sprites = new Sprite[frames]
            };


            for (int k = 0; k < frames; k++)
            {
                var pixels = new Color32[texW * texH];
                uint kseed = (uint)(k * 7919) + seed * 2654435761u;

                // Per-row horizontal tear, the reference's GlitchMap. Rows are addressed in
                // texture space so a shift wraps instead of smearing, and only a fraction of
                // rows move — a full shift of every row reads as a scroll, not a glitch.
                int[] rowShift = null;
                float glitch = Mathf.Clamp01(req.Glitch);
                if (glitch > 0f && texH > 0)
                {
                    rowShift = new int[texH];
                    for (int y = 0; y < texH; y++)
                    {
                        uint gh = Hash(0x5BD1u, (uint)y, kseed);
                        if ((gh & 0xFFFF) / 65535f >= glitch * 0.35f) continue;
                        int span = Mathf.Max(1, Mathf.RoundToInt(glitch * texW * 0.18f));
                        rowShift[y] = Mathf.RoundToInt(((gh >> 16) & 0xFFFF) / 65535f * 2f - 1f) * span;
                    }
                }

                // Drift the noise field by whole lattice periods across the frame set, so
                // the flipbook loops seamlessly instead of snapping back to the first
                // frame. This is the "evolve" axis: the reference scrolls its noise map at
                // _NoiseEvoSpeed and lets the coastline redraw itself.
                float drift = k * (LoopPeriod / (float)frames);

                BuildFrameMask(pieces, req, cellsX, cellsY, kseed, drift, noiseFreq,
                    swingIn, swingOut,
                    maskDiv, maskW, maskH, coarseIn, coarseOwner,
                    insideGrid, edgeGrid, ownerGrid);

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
                        if (!insideGrid[idx]) continue;

                        // Appearance comes from the piece that owns this cell, so a union
                        // keeps every region's own colour instead of tinting the cluster.
                        int own = ownerGrid[idx];
                        var fillC = own >= 0 ? pieces[own].FillColor : req.FillColor;
                        var edgeC = own >= 0 ? pieces[own].EdgeColor : req.EdgeColor;
                        float edgeOpacity = own >= 0 ? pieces[own].EdgeOpacity : req.EdgeOpacity;
                        float fillOpacity = own >= 0 ? pieces[own].FillOpacity : req.FillOpacity;

                        bool isEdge = edgeGrid[idx];

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
                            int shift = rowShift != null ? rowShift[y] : 0;
                            for (int x = x0; x < x0 + block; x++)
                            {
                                int sx = x + shift;
                                if (shift != 0)
                                {
                                    sx %= texW;
                                    if (sx < 0) sx += texW;
                                }
                                pixels[row + sx] = c;
                            }
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
        /// Rasterises one frame's coverage mask, in two stages:
        ///
        ///   1. a coarse mask, one sample per <paramref name="maskDiv"/> texture cells, of
        ///      a thresholded scrolling noise field biased by the signed distance to the
        ///      region. That is the mechanism the reference shader uses (ActiveBlock:
        ///      SDF cell + `_NoiseMap` sampled at `uv * _NoiseEvoSpeed`, compared against
        ///      `_NoiseRadius`), and it is what produces the behaviour a warped outline
        ///      cannot: bays that pinch off into islands, islands that drift back and
        ///      merge, holes flickering open inside the fill;
        ///   2. the fine grid inherits that mask, and the border is traced at fine
        ///      resolution so the line stays one texel thick while its shape follows the
        ///      coarse steps.
        ///
        /// Tracing at coarse resolution would make the border as thick as a step, which
        /// reads as a filled band rather than a line.
        /// </summary>
        private static void BuildFrameMask(Piece[] pieces, Request req,
            int cellsX, int cellsY, uint kseed, float drift, float noiseFreq,
            float swingIn, float swingOut,
            int maskDiv, int maskW, int maskH, bool[] coarseIn, int[] coarseOwner,
            bool[] inside, bool[] edge, int[] owner)
        {
            // Sampling covers the padded quad, not just the geometry: the margin is where
            // detached flakes live.
            float margin = Mathf.Max(0f, req.Margin);
            float totalW = req.WidthWorld + margin * 2f;
            float totalH = req.HeightWorld + margin * 2f;
            float halfW = totalW * 0.5f;
            float halfH = totalH * 0.5f;

            for (int j = 0; j < maskH; j++)
            {
                for (int i = 0; i < maskW; i++)
                {
                    float wx = (i + 0.5f) / maskW * totalW - halfW;
                    float wy = (j + 0.5f) / maskH * totalH - halfH;

                    float d;
                    int own = SignedDistance(pieces, wx, wy, out d, out float pieceBand);

                    // +1 well inside, 0 on the boundary, -1 outside the band. The band is
                    // the nearest piece's own, so each region keeps a coastline
                    // proportional to itself.
                    float t = Mathf.Clamp(-d / pieceBand, -1f, 1f);

                    float n = Fbm(wx * noiseFreq + drift, wy * noiseFreq, LoopPeriod, kseed);

                    // The threshold moves with the distance; the noise stays put. That is
                    // what makes the boundary a level set of the noise, so it bites into
                    // the region in bays and leaves detached fragments behind, instead of
                    // being a rectangle with a wiggly border.
                    float thr = 0.5f - (t > 0f ? swingIn : swingOut) * t;
                    bool cover = n > thr;

                    int m = j * maskW + i;
                    // Covered but outside every piece: a detached flake. Keeping the owner
                    // from the geometry would tint it with a neighbour's colour; -1 lets it
                    // fall back to the cluster's own palette, which is correct.
                    coarseOwner[m] = cover ? own : -1;
                    coarseIn[m] = cover;
                }
            }

            for (int cy = 0; cy < cellsY; cy++)
            {
                int mj = Mathf.Min(maskH - 1, cy / maskDiv);
                for (int cx = 0; cx < cellsX; cx++)
                {
                    int mi = Mathf.Min(maskW - 1, cx / maskDiv);
                    int m = mj * maskW + mi;
                    int idx = cy * cellsX + cx;
                    inside[idx] = coarseIn[m];
                    owner[idx] = coarseOwner[m];
                }
            }

            int thickness = Mathf.Clamp(req.EdgeThickness <= 0 ? 1 : req.EdgeThickness, 1, 6);

            for (int cy = 0; cy < cellsY; cy++)
            {
                for (int cx = 0; cx < cellsX; cx++)
                {
                    int i = cy * cellsX + cx;
                    if (!inside[i]) { edge[i] = false; continue; }

                    // Edge = covered cell within `thickness` cells of an uncovered one.
                    // Thickness 1 is the reference's single-cell border; thicker borders
                    // read as a drawn outline rather than a cut-out, which is sometimes
                    // wanted on a small field.
                    bool nearOutside = false;
                    for (int oy = -thickness + 1; oy < thickness && !nearOutside; oy++)
                    {
                        int ny = cy + oy;
                        if (ny < 0 || ny >= cellsY) { nearOutside = true; break; }
                        for (int ox = -thickness + 1; ox < thickness; ox++)
                        {
                            int nx = cx + ox;
                            if (nx < 0 || nx >= cellsX) { nearOutside = true; break; }
                            if (!inside[ny * cellsX + nx]) { nearOutside = true; break; }
                        }
                    }
                    edge[i] = nearOutside;

                    // Edge = covered cell with an uncovered 4-neighbour. Cell edges, not
                    // texel edges, so the line is one cell thick at texture resolution.
                    edge[i] =
                        cx == 0 || !inside[i - 1] ||
                        cx == cellsX - 1 || !inside[i + 1] ||
                        cy == 0 || !inside[i - cellsX] ||
                        cy == cellsY - 1 || !inside[i + cellsX];
                }
            }
        }

        /// <summary>Lattice period of the shape noise; drift completes whole periods per loop.</summary>
        private const int LoopPeriod = 8;

        /// <summary>
        /// Signed distance to the union of the pieces, negative inside, plus the index of
        /// the piece that decides the colour at this point (-1 outside every piece) and the
        /// coastline band of the nearest piece.
        ///
        /// The band travels with the distance rather than being a cluster-wide constant:
        /// band width has to follow each region's own size, or a small rectangle sharing a
        /// depth with a large one is eroded by the large one's band and renders as debris.
        /// </summary>
        private static int SignedDistance(Piece[] pieces, float px, float py,
            out float distance, out float band)
        {
            distance = float.MaxValue;
            band = 0.05f;
            int ownerIdx = -1;
            bool any = false;

            for (int i = 0; i < pieces.Length; i++)
            {
                var p = pieces[i];
                float dx = px - p.Offset.x;
                float dy = py - p.Offset.y;

                float rad = p.Rotation * Mathf.Deg2Rad;
                float cos = Mathf.Cos(rad), sin = Mathf.Sin(rad);
                float lx = dx * cos + dy * sin;
                float ly = -dx * sin + dy * cos;

                float d;
                if (p.Circle)
                {
                    d = Mathf.Sqrt(lx * lx + ly * ly) - Mathf.Max(0f, p.Half.x - p.Feather * 0.5f);
                }
                else
                {
                    // Box distance: negative inside, and its magnitude is the distance to
                    // the nearest side, which is what the bias needs.
                    float qx = Mathf.Abs(lx) - p.Half.x;
                    float qy = Mathf.Abs(ly) - p.Half.y;
                    d = qx > 0f && qy > 0f
                        ? Mathf.Sqrt(qx * qx + qy * qy)
                        : Mathf.Max(qx, qy);
                }

                if (d < distance)
                {
                    distance = d;
                    band = Mathf.Max(0.05f, p.Band);
                    // Colour comes from whichever region the point actually sits in.
                    if (d <= 0f) { ownerIdx = i; any = true; }
                }
            }

            if (!any) ownerIdx = -1;
            return ownerIdx;
        }

        /// <summary>
        /// Two-octave periodic value noise. One octave gives smooth bays but a coastline
        /// that reads as a sine wave; the second octave roughens the border while keeping
        /// the large structure, which is what the reference's coastlines look like.
        /// </summary>
        private static float Fbm(float x, float y, int period, uint seed)
        {
            float n = PeriodicNoise(x, y, period, seed);
            n += 0.5f * PeriodicNoise(x * 2f, y * 2f, period * 2, seed + 977u);
            return n / 1.5f;
        }

        /// <summary>
        /// Smooth value noise on a lattice that wraps every <paramref name="period"/> cells.
        /// Wrapping is what lets the flipbook loop: drifting by a whole number of periods
        /// over the frame set returns exactly to the first frame, so the animation repeats
        /// without a visible jump instead of snapping back.
        /// </summary>
        private static float PeriodicNoise(float x, float y, int period, uint seed)
        {
            int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y);
            float fx = x - x0, fy = y - y0;
            fx = fx * fx * (3f - 2f * fx);
            fy = fy * fy * (3f - 2f * fy);

            uint v00 = Hash(Wrap(x0, period), Wrap(y0, period), seed);
            uint v10 = Hash(Wrap(x0 + 1, period), Wrap(y0, period), seed);
            uint v01 = Hash(Wrap(x0, period), Wrap(y0 + 1, period), seed);
            uint v11 = Hash(Wrap(x0 + 1, period), Wrap(y0 + 1, period), seed);

            float a = (v00 >> 8) / 16777215f;
            float b = (v10 >> 8) / 16777215f;
            float c = (v01 >> 8) / 16777215f;
            float e = (v11 >> 8) / 16777215f;
            float top = a + (b - a) * fx;
            float bot = c + (e - c) * fx;
            return top + (bot - top) * fy;
        }

        private static uint Wrap(int v, int period)
        {
            int m = v % period;
            if (m < 0) m += period;
            return (uint)m;
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
