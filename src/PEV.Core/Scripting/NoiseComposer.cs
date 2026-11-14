using System.Diagnostics;
using System.Threading;
using UnityEngine;

namespace PEV.Scripting
{
    /// <summary>
    /// A CPU transliteration of Phira-Pro's noise-field skin, pass for pass, straight from
    /// assets/rpen/noise-skins/NoiseField (config.json + the GLSL files).
    ///
    /// The pass graph and the resolutions config.json allocates:
    ///
    ///   activeCompose  r8    0.125            ← composeActive.glsl
    ///   effects        rg8   0.25  filter linear ← edge.glsl writes .r
    ///   pingA/pingB    rg8   0.25              ← glow.glsl ×5 writes .r/.g
    ///   effects.g      —                       ← glowExtract.glsl
    ///   $screen       full                     ← main.glsl
    ///
    /// Three details decide what the border looks like, and each is easy to get wrong when
    /// re-implementing this from the shader text alone:
    ///
    ///   · edge.glsl runs at 1/4, but its `mainTex` is the 1/8 compose target and it offsets by
    ///     `targetTexelSize` — one texel of ITS OWN target. So each tap lands half a compose
    ///     texel away, and the 3×3 window covers a 3-compose-texel span. Resampling compose to
    ///     1/4 first and then walking ±1 looks equivalent but produces a wider ring.
    ///   · glow.glsl's first pass reads compose at 1/8 the same way; later passes ping-pong at
    ///     1/4, where a tap really is a whole texel.
    ///   · main.glsl reads `effects`, declared linear, so the *glow* channel interpolates while
    ///     the *edge* channel is fetched at quantised texel centres and stays hard. Sampling
    ///     both nearest is what turned the falloff into four visible stair steps.
    ///
    /// Two substitutions are unavoidable and flagged where they happen: the screen grab behind
    /// the region is the level background colour, and SpriteRenderer blends with SrcAlpha so
    /// the premultiplied result is divided back out at the end.
    ///
    /// ## Why this is split into Frame and rows
    ///
    /// A bake is the one moment this effect can cost the player a frame, and the main pass is
    /// per-pixel over up to 1024×628 — hundreds of milliseconds of work that must not land on
    /// one thread. Everything that depends only on the frame (the three low-res grids and the
    /// 6-pixel cell table) is built once in Frame.Build; the pixel loop then writes disjoint
    /// horizontal bands, so the caller can fan it out across however many cores it has.
    ///
    /// The reference's own structure is what makes the cell table possible:
    /// `pixelCenter(uv, backgroundPixelScale)` quantises the block-noise taps, the
    /// displacement and hence the no-grain fill to a 6-pixel screen cell, so those are
    /// constant within a cell. The spark map is the only term that genuinely varies per pixel.
    /// </summary>
    internal static class NoiseComposer
    {
        // --- config.json ---------------------------------------------------------------
        private const float ComposeScale = 0.125f;   // activeCompose
        private const float EffectsScale = 0.25f;    // effects, pingA, pingB

        // --- composeActive.glsl ---------------------------------------------------------
        private const float ComposeDisplaceSpeed = 2.5899999141693115f;
        private const float ComposeDisplaceStrength = 0.10000000149011612f;
        private static readonly Vector2 ComposeDisplaceDirection = new Vector2(0.5f, 0.5f);
        private static readonly Vector2 ComposeDisplaceMapST = new Vector2(2.13f, 1.02f);

        // --- main.glsl constants ---------------------------------------------------------
        private const float DisplaceBlendIntensity = 0.41100001335144043f;
        private static readonly Vector2 ActiveDisplaceDirection = new Vector2(1f, 1f);
        private static readonly Vector2 ActiveDisplaceMapST = new Vector2(0.80f, 0.30f);
        private static readonly Vector2 ActiveSparkMapST = new Vector2(3.00f, 1.20f);
        private const float ActiveSparkDisplaceIntensity = 2.390000104904175f;
        private const float ActiveSparkHueShiftAmount = 0.20000000298023224f;

        // --- main.glsl editor defaults ---------------------------------------------------
        private const float DefaultFillStrength = 0.667f;
        private const float DefaultActiveDisplaceSpeed = 1.5f;
        private const float DefaultActiveDisplaceStrength = 0.15f;
        private const float DefaultActiveSparkMapOpacity = 5.69f;

        // nfFill / nfGlow / nfSpark.
        private const float FillValue = 0.713207483291626f;
        private const float GlowSaturation = 1.2253520800891937f;
        private const float SparkSaturation = 1.0676054460523543f;

        // glow.glsl passWeight values, in pipeline order.
        private static readonly float[] GlowWeights =
        {
            0.458567970132f, 0.282861223139f, 0.156589227109f,
            0.07305908496f, 0.024947807942f
        };

        // --- texture caches ---------------------------------------------------------------
        private static Color32[] _blockPx;
        private static int _blockW, _blockH;
        private static Color32[] _pointPx;
        private static int _pointW, _pointH;

        public static void WarmTextures()
        {
            if (_blockPx != null) return;
            try
            {
                var t = NoiseTextures.Block;
                if (t != null) { _blockPx = t.GetPixels32(); _blockW = t.width; _blockH = t.height; }
                var p = NoiseTextures.Point;
                if (p != null) { _pointPx = p.GetPixels32(); _pointW = p.width; _pointH = p.height; }
            }
            catch { }
        }

        /// <summary>texture2D(blockNoise, uv).r, wrap=mirror, filter=nearest.</summary>
        private static float BlockAt(float u, float v)
        {
            if (_blockPx == null) return 0.5f;
            int x = Mathf.Clamp(Mathf.FloorToInt(NoiseTextures.Mirror(u) * _blockW), 0, _blockW - 1);
            int y = Mathf.Clamp(Mathf.FloorToInt(NoiseTextures.Mirror(v) * _blockH), 0, _blockH - 1);
            return _blockPx[y * _blockW + x].r / 255f;
        }

        /// <summary>texture2D(pointNoise, uv).r, wrap=repeat, filter=nearest.</summary>
        private static float PointAt(float u, float v)
        {
            if (_pointPx == null) return 0f;
            int x = Mathf.Clamp(Mathf.FloorToInt(NoiseTextures.Repeat(u) * _pointW), 0, _pointW - 1);
            int y = Mathf.Clamp(Mathf.FloorToInt(NoiseTextures.Repeat(v) * _pointH), 0, _pointH - 1);
            return _pointPx[y * _pointW + x].r / 255f;
        }

        /// <summary>texture2D(t, uv) bilinear (GL_LINEAR), edge-clamped as the host allocates it.</summary>
        private static float Linear(float[] grid, int w, int h, float u, float v)
        {
            float px = u * w - 0.5f, py = v * h - 0.5f;
            float x0 = Mathf.Floor(px), y0 = Mathf.Floor(py);
            float fx = px - x0, fy = py - y0;
            int ix0 = Mathf.Clamp((int)x0, 0, w - 1), ix1 = Mathf.Clamp((int)x0 + 1, 0, w - 1);
            int iy0 = Mathf.Clamp((int)y0, 0, h - 1), iy1 = Mathf.Clamp((int)y0 + 1, 0, h - 1);
            float a = Mathf.Lerp(grid[iy0 * w + ix0], grid[iy0 * w + ix1], fx);
            float b = Mathf.Lerp(grid[iy1 * w + ix0], grid[iy1 * w + ix1], fx);
            return Mathf.Lerp(a, b, fy);
        }

        // --- stage timings, milliseconds, summed over all threads ---------------------------
        // The bake is the only place this effect can cost a frame, and "it feels fine" is not
        // a measurement. These tell the harness which pass to attack when the budget is
        // missed; reading them as anything but diagnostics would be a mistake.
        public static long ComposeTicks;
        public static long EffectsTicks;
        public static long MainTicks;

        // --- per-cell cache -----------------------------------------------------------------

        /// <summary>
        /// Everything main.glsl derives from a quantised screen cell.
        ///
        /// `pixelCenter(uv, 6)` snaps the block-noise taps, the displacement and hence the
        /// no-grain fill to a 6-pixel screen cell, so within a cell they are all identical.
        /// Computing these per pixel instead is ~36 redundant block-noise lookups each, and
        /// that cost lands entirely at bake time.
        ///
        /// FillFlat is the whole no-grain path — fill, strength and exposure — precomputed,
        /// because the spark map is zero over most of the quad and every pixel there would
        /// otherwise redo it. SceneHsv is hoisted for the same reason on the pixels that are lit.
        /// </summary>
        private struct Cell
        {
            public float DispAvg;
            public Vector2 Disp;
            public Vector3 FillBase;
            public Vector3 SceneHsv;
            public Vector3 FillFlat;
        }

        // --- passes ---------------------------------------------------------------------------

        /// <summary>
        /// One frame's low-resolution grids and cell table, ready to render in row bands.
        ///
        /// Build is the only part that must run once; RenderRows is pure per-row work writing
        /// into the caller's buffer, so the same Frame can be rendered by any number of threads
        /// as long as the bands do not overlap.
        /// </summary>
        public sealed class Frame
        {
            public int TexW, TexH;
            public int Rows => TexH;

            private float[] _compose; private int _cw, _ch;
            private float[] _edges; private int _ew, _eh;
            private float[] _glow;

            // Per-output-column/row compose indices, precomputed once: every tap of the edge
            // and first glow pass is a fixed function of the column, so resolving them per
            // texel was dividing by ew and flooring twice for the same input.
            private int[] _tapX;
            private int[] _tapY;

            private Cell[] _cells; private int _cellCols, _cellRows;

            // Per-output-column tables. Every index in the pixel loop is a function of x alone
            // (compose texel, edge texel, bilinear neighbours and weights, cell, spark u), so
            // resolving them per pixel meant ~6 divisions, 4 floors and 6 clamps repeated for
            // every texel of every row. The main pass dominates bake time, and this removes
            // most of its integer work.
            private int[] _colCompose, _colEdge, _colCell;
            private int[] _colGlowX0, _colGlowX1;
            private float[] _colGlowFx, _colSparkU;

            private float _fillOpacity, _fillStrength, _exposure, _edgeOpacity, _glowIntensity;
            private float _sparkMapOpacity, _displaceStrength;
            private Vector3 _nfFill, _nfEdge, _nfGlow, _nfSpark;
            private Vector2 _dir, _perp;
            private Vector2 _drift, _ortho;
            private float _span = 2f;
            private int _frames = 8;
            private Vector3 _scene;

            /// <summary>Builds every grid this frame needs. Serial; cheap next to the main pass.</summary>
            public static Frame Build(NoiseTextureFactory.Piece[] pieces, NoiseTextureFactory.Request req,
                int texW, int texH, float time, uint seed, int frameCount)
            {
                var f = new Frame
                {
                    TexW = texW,
                    TexH = texH,
                    _span = Mathf.Max(1e-3f, req.LoopPeriod),
                    _frames = Mathf.Max(1, frameCount)
                };
                f.BuildPasses(pieces, req, time);
                f.BuildShading(req, time);
                return f;
            }

            // ============================ pass 1: composeActive ============================
            // target: activeCompose, 0.125. `nfMaskBit` snaps the displaced coordinate to the
            // centre of a mask texel before reading, so the coast always follows mask-texel
            // blocks no matter how finely the target is sampled.
            private void BuildPasses(NoiseTextureFactory.Piece[] pieces, NoiseTextureFactory.Request req, float time)
            {
                var total = Stopwatch.StartNew();

                float margin = Mathf.Max(0f, req.Margin);
                float totalW = Mathf.Max(1e-3f, req.WidthWorld + margin * 2f);
                float totalH = Mathf.Max(1e-3f, req.HeightWorld + margin * 2f);

                // Target densities, as fractions of the SCREEN — not of the texture.
                //
                // The reference allocates activeCompose at floor(screen / maskPixelScale)
                // with maskPixelScale = 8, and effects at screen / 4: one mask texel is eight
                // screen pixels wide. Sizing the same fraction off the texture instead made
                // every stair step 4-5x too large here, because this texture is only a part
                // of the screen — the coast then moved in big rectangular bites, which is
                // exactly what read as random rather than as a flowing edge.
                //
                // The 1:2 ratio between the two grids is load-bearing: edge.glsl taps
                // mainTex at one *effects* texel, which is two compose texels, so the border
                // is two mask cells thick. Scaling both by the same factor keeps that.
                int screenW = req.ScreenPixelWidth > 0 ? req.ScreenPixelWidth : TexW;
                int cw = _cw = Mathf.Clamp(Mathf.RoundToInt(screenW * ComposeScale), 1, TexW);
                int ch = _ch = Mathf.Clamp(
                    Mathf.RoundToInt(cw * (float)TexH / Mathf.Max(1, TexW)), 1, TexH);
                var compose = _compose = new float[cw * ch];

                // The drift runs around a circle rather than in a straight line.
                //
                // The reference samples a displacement field with a monotonically advancing
                // offset, so it never repeats. A flipbook must: a linear offset only returns
                // after ~21.8 shader-seconds, which would need a quarter-thousand frames, and
                // any shorter cycle ends in a visible jump. Rotating the offset instead makes
                // every cycle close exactly, at any length, while the sample point still
                // travels at the reference's own speed — the motion reads as flow, not as a
                // loop. Radius is chosen so the arc speed matches 2.59/20 units per second.
                // Fixed basis the two taps are recombined with, exactly as the shader does;
                // only the sample offsets rotate.
                Vector2 cdir = Normalize(ComposeDisplaceDirection);
                Vector2 cperp = new Vector2(-cdir.y, cdir.x);
                // Distance the sample point must travel per DISPLAYED frame.
                //
                // The reference advances its field at 2.59/20 uv per second and is drawn at
                // 60 fps, so one frame moves it 0.00216 uv. A flipbook must move the same
                // amount per frame regardless of how fast the script plays the set: tying the
                // step to wall time makes a 12 fps set lurch 5.9 noise texels per frame, which
                // is exactly the "random regeneration" the reference never shows. Fixing the
                // step and letting the cycle length follow keeps every playback rate coherent.
                float cStep = (ComposeDisplaceSpeed / 20f) / 60f;
                float cAngle = (2f * Mathf.PI) * (time / _span);
                float cRadius = cStep * _frames / (2f * Mathf.PI);
                // The two taps use the radius and its quarter turn, so the pair still spans
                // two independent directions exactly as `dir * t` and `perp * t` did, while
                // both return to their start when the cycle closes.
                Vector2 cdrift = new Vector2(Mathf.Cos(cAngle), Mathf.Sin(cAngle)) * cRadius;
                Vector2 cortho = new Vector2(-Mathf.Sin(cAngle), Mathf.Cos(cAngle)) * cRadius;

                for (int j = 0; j < ch; j++)
                {
                    for (int i = 0; i < cw; i++)
                    {
                        float u = (i + 0.5f) / cw;
                        float v = (j + 0.5f) / ch;

                        float d1 = BlockAt(u * ComposeDisplaceMapST.x + cdrift.x,
                                           v * ComposeDisplaceMapST.y + cdrift.y) - 0.5f;
                        float d2 = BlockAt(u * ComposeDisplaceMapST.x + cortho.x,
                                           v * ComposeDisplaceMapST.y + cortho.y) - 0.5f;
                        float dispx = cdir.x * d1 + cperp.x * d2;
                        float dispy = cdir.y * d1 + cperp.y * d2;

                        float maskU = u + dispx * ComposeDisplaceStrength;
                        float maskV = v + dispy * ComposeDisplaceStrength;

                        // nfMaskBit: q = (floor(p * msz) + 0.5) / msz.
                        float qx = (Mathf.Floor(maskU * cw) + 0.5f) / cw;
                        float qy = (Mathf.Floor(maskV * ch) + 0.5f) / ch;

                        float wx = qx * totalW - totalW * 0.5f;
                        float wy = qy * totalH - totalH * 0.5f;
                        compose[j * cw + i] = Inside(pieces, wx, wy) ? 1f : 0f;
                    }
                }

                Interlocked.Add(ref ComposeTicks, total.ElapsedTicks);
                long composeDone = total.ElapsedTicks;

                // ==================== passes 2..8: effects.r, pingA, pingB ====================
                // Half a compose texel per effects texel, exactly as the reference (compose at
                // screen/8, effects at screen/4). Keeping the 2:1 ratio is what makes the
                // border one compose cell wide and the glow ramp the same number of cells.
                int ew = _ew = Mathf.Clamp(Mathf.Max(2, cw * 2), 1, TexW);
                int eh = _eh = Mathf.Clamp(Mathf.Max(2, ch * 2), 1, TexH);

                BuildTaps();

                // effects.r ← edge.glsl
                var edges = _edges = new float[ew * eh];
                for (int j = 0; j < eh; j++)
                {
                    int rowBase = j * ew;
                    for (int i = 0; i < ew; i++)
                    {
                        float m = MaxTap(compose, _tapX, _tapY, i, j, cw);
                        edges[rowBase + i] = Mathf.Clamp01(m - compose[_tapY[j * 3 + 1] * cw + _tapX[i * 3 + 1]]);
                    }
                }

                // pingA ← glow.glsl(firstPass), then four more ping-pong passes; each returns
                // (m, accum) as (r, g).
                var pingR = new float[ew * eh];
                var accum = new float[ew * eh];
                var nextR = new float[ew * eh];
                var nextAccum = new float[ew * eh];

                for (int p = 0; p < GlowWeights.Length; p++)
                {
                    float weight = GlowWeights[p];
                    bool firstPass = p == 0;

                    for (int j = 0; j < eh; j++)
                    {
                        int rowBase = j * ew;
                        for (int i = 0; i < ew; i++)
                        {
                            int o = rowBase + i;
                            float curR, m;
                            if (firstPass)
                            {
                                curR = compose[_tapY[j * 3 + 1] * cw + _tapX[i * 3 + 1]];
                                m = MaxTap(compose, _tapX, _tapY, i, j, cw);
                            }
                            else
                            {
                                curR = pingR[o];
                                m = Max3Ping(pingR, i, j);
                            }
                            float curG = firstPass ? 0f : accum[o];

                            float comp = compose[_tapY[j * 3 + 1] * cw + _tapX[i * 3 + 1]];
                            float ring = Mathf.Clamp01(m - curR) * (1f - comp);
                            float weighted = ring * weight;

                            nextR[o] = m;
                            nextAccum[o] = Mathf.Clamp01(firstPass ? weighted : curG + weighted);
                        }
                    }

                    var sr = pingR; pingR = nextR; nextR = sr;
                    var sa = accum; accum = nextAccum; nextAccum = sa;
                }

                // glowExtract.glsl writes pingA.g into effects.g
                _glow = accum;

                Interlocked.Add(ref EffectsTicks, total.ElapsedTicks - composeDone);
            }

            /// <summary>
            /// Resolves the nine-tap window indices once per column and row.
            ///
            /// The taps are at ±targetTexelSize while the target is coarser than mainTex, so
            /// two neighbouring taps can resolve to the same source texel — which is exactly
            /// why the 1/4 passes read as a 3-compose-texel window rather than 4.
            /// </summary>
            private void BuildTaps()
            {
                float tx = 1f / _ew, ty = 1f / _eh;
                _tapX = new int[_ew * 3];
                _tapY = new int[_eh * 3];

                for (int i = 0; i < _ew; i++)
                {
                    float u = (i + 0.5f) / _ew;
                    _tapX[i * 3 + 0] = Mathf.Clamp(Mathf.FloorToInt((u - tx) * _cw), 0, _cw - 1);
                    _tapX[i * 3 + 1] = Mathf.Clamp(Mathf.FloorToInt(u * _cw), 0, _cw - 1);
                    _tapX[i * 3 + 2] = Mathf.Clamp(Mathf.FloorToInt((u + tx) * _cw), 0, _cw - 1);
                }
                for (int j = 0; j < _eh; j++)
                {
                    float v = (j + 0.5f) / _eh;
                    _tapY[j * 3 + 0] = Mathf.Clamp(Mathf.FloorToInt((v - ty) * _ch), 0, _ch - 1);
                    _tapY[j * 3 + 1] = Mathf.Clamp(Mathf.FloorToInt(v * _ch), 0, _ch - 1);
                    _tapY[j * 3 + 2] = Mathf.Clamp(Mathf.FloorToInt((v + ty) * _ch), 0, _ch - 1);
                }
            }

            private float MaxTap(float[] grid, int[] tapX, int[] tapY, int i, int j, int w)
            {
                int x0 = tapX[i * 3 + 0], x1 = tapX[i * 3 + 1], x2 = tapX[i * 3 + 2];
                int y0 = tapY[j * 3 + 0] * w, y1 = tapY[j * 3 + 1] * w, y2 = tapY[j * 3 + 2] * w;

                float m = grid[y0 + x0];
                m = Mathf.Max(m, grid[y0 + x1]);
                m = Mathf.Max(m, grid[y0 + x2]);
                m = Mathf.Max(m, grid[y1 + x0]);
                m = Mathf.Max(m, grid[y1 + x1]);
                m = Mathf.Max(m, grid[y1 + x2]);
                m = Mathf.Max(m, grid[y2 + x0]);
                m = Mathf.Max(m, grid[y2 + x1]);
                m = Mathf.Max(m, grid[y2 + x2]);
                return m;
            }

            /// <summary>3×3 max on the 1/4 ping target, where a tap really is one texel.</summary>
            private float Max3Ping(float[] grid, int i, int j)
            {
                int x0 = Mathf.Max(0, i - 1), x1 = i, x2 = Mathf.Min(_ew - 1, i + 1);
                int base0 = Mathf.Max(0, j - 1) * _ew, base1 = j * _ew, base2 = Mathf.Min(_eh - 1, j + 1) * _ew;

                float m = grid[base0 + x0];
                m = Mathf.Max(m, grid[base0 + x1]);
                m = Mathf.Max(m, grid[base0 + x2]);
                m = Mathf.Max(m, grid[base1 + x0]);
                m = Mathf.Max(m, grid[base1 + x1]);
                m = Mathf.Max(m, grid[base1 + x2]);
                m = Mathf.Max(m, grid[base2 + x0]);
                m = Mathf.Max(m, grid[base2 + x1]);
                m = Mathf.Max(m, grid[base2 + x2]);
                return m;
            }

            // ================================ main.glsl =================================
            private void BuildShading(NoiseTextureFactory.Request req, float time)
            {
                Color outline = req.EdgeColor;
                _nfFill = Variant(outline, 1f, FillValue);
                _nfEdge = Variant(outline, 1f, 1f);
                _nfGlow = Variant(outline, GlowSaturation, 1f);
                _nfSpark = Variant(outline, SparkSaturation, 1f);

                _fillOpacity = Mathf.Clamp01(req.FillOpacity);
                _fillStrength = Mathf.Clamp01(req.FillStrength <= 0f ? DefaultFillStrength : req.FillStrength);
                _exposure = Mathf.Clamp(req.Exposure, 0f, 2f);
                _edgeOpacity = Mathf.Max(0f, req.EdgeOpacity);
                _glowIntensity = Mathf.Max(0f, req.GlowOpacity);
                _sparkMapOpacity = DefaultActiveSparkMapOpacity * Mathf.Max(0f, req.SparkOpacity);
                _displaceStrength = req.Displace > 0f ? Mathf.Clamp01(req.Displace) : DefaultActiveDisplaceStrength;

                _dir = Normalize(ActiveDisplaceDirection);
                _perp = new Vector2(-_dir.y, _dir.x);
                _scene = new Vector3(req.SceneColor.r, req.SceneColor.g, req.SceneColor.b);

                // Same circular drift as the compose pass, at the main pass's own speed. The
                // two must share the period or the fill would drift against its own coast.
                float stepPerFrame = (DefaultActiveDisplaceSpeed / 20f) / 60f;
                float angle = (2f * Mathf.PI) * (time / _span);
                float radius = stepPerFrame * _frames / (2f * Mathf.PI);
                _drift = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius;
                _ortho = new Vector2(-Mathf.Sin(angle), Mathf.Cos(angle)) * radius;

                // The cell table is built here, serially, rather than lazily during rendering.
                // A cell spans six rows, so a band boundary could put two worker threads on
                // the same cell; filling it up front removes the shared write entirely and the
                // cost is one pass over ~18k cells.
                int cellCols = _cellCols = Mathf.Max(1, (TexW + 5) / 6);
                int cellRows = _cellRows = Mathf.Max(1, (TexH + 5) / 6);
                _cells = new Cell[cellCols * cellRows];
                for (int cy = 0; cy < cellRows; cy++)
                for (int cx = 0; cx < cellCols; cx++)
                {
                    _cells[cy * cellCols + cx] = BuildCell(
                        (cx + 0.5f) * 6f / TexW, (cy + 0.5f) * 6f / TexH,
                        _nfFill, _scene);
                }

                BuildColumnTables();
            }

            /// <summary>
            /// Resolves everything in the pixel loop that depends only on x.
            ///
            /// The loop used to divide, floor and clamp six times per texel to find the same
            /// four grid indices for every row of the same column. Hoisting them turns the
            /// inner loop into multiply-adds over precomputed integers, which is the difference
            /// between a bake that misses its budget and one that does not.
            /// </summary>
            private void BuildColumnTables()
            {
                _colCompose = new int[TexW];
                _colEdge = new int[TexW];
                _colCell = new int[TexW];
                _colGlowX0 = new int[TexW];
                _colGlowX1 = new int[TexW];
                _colGlowFx = new float[TexW];
                _colSparkU = new float[TexW];

                int w = _ew, h = _eh;
                for (int x = 0; x < TexW; x++)
                {
                    float u = (x + 0.5f) / TexW;
                    _colCompose[x] = Mathf.Clamp((int)(u * _cw), 0, _cw - 1);
                    _colEdge[x] = Mathf.Clamp((int)(u * _ew), 0, _ew - 1);
                    _colCell[x] = Mathf.Clamp(x / 6, 0, _cellCols - 1);
                    _colSparkU[x] = u * ActiveSparkMapST.x;

                    // Same arithmetic as Linear(), resolved once per column.
                    float px = u * w - 0.5f;
                    float x0 = Mathf.Floor(px);
                    _colGlowX0[x] = Mathf.Clamp((int)x0, 0, w - 1);
                    _colGlowX1[x] = Mathf.Clamp((int)x0 + 1, 0, w - 1);
                    _colGlowFx[x] = px - x0;
                }
            }

            /// <summary>
            /// Renders rows [y0, y1) into `dst`. Bands must not overlap; each writes only its
            /// own rows, so no synchronisation is needed between callers.
            /// </summary>
            public void RenderRows(Color32[] dst, int y0, int y1)
            {
                long startedTicks = Stopwatch.GetTimestamp();
                for (int y = y0; y < y1; y++)
                {
                    float v = (y + 0.5f) / TexH;
                    int cy = Mathf.Clamp(y / 6, 0, _cellRows - 1);
                    int cellRowBase = cy * _cellCols;
                    int py = Mathf.Clamp((int)(v * _ch), 0, _ch - 1) * _cw;
                    int ey = Mathf.Clamp((int)(v * _eh), 0, _eh - 1) * _ew;
                    int row = y * TexW;

                    // Glow's vertical pair and weight, the same for every column of this row.
                    float gy = v * _eh - 0.5f;
                    float gy0 = Mathf.Floor(gy);
                    int iy0 = Mathf.Clamp((int)gy0, 0, _eh - 1) * _ew;
                    int iy1 = Mathf.Clamp((int)gy0 + 1, 0, _eh - 1) * _ew;
                    float fy = gy - gy0;

                    float sparkV = v * ActiveSparkMapST.y;
                    float sparkScale = _sparkMapOpacity;

                    for (int x = 0; x < TexW; x++)
                    {
                        float comp = _compose[py + _colCompose[x]];
                        float edge = _edges[ey + _colEdge[x]];

                        // Inline of Linear() with the column tables.
                        int gx0 = _colGlowX0[x], gx1 = _colGlowX1[x];
                        float fx = _colGlowFx[x];
                        float a = _glow[iy0 + gx0] + (_glow[iy0 + gx1] - _glow[iy0 + gx0]) * fx;
                        float b = _glow[iy1 + gx0] + (_glow[iy1 + gx1] - _glow[iy1 + gx0]) * fx;
                        float glow = a + (b - a) * fy;

                        if (comp + edge + glow <= 0.0001f) continue;

                        // Spark is the only per-pixel term: its UV is not quantised.
                        Cell cell = _cells[cellRowBase + _colCell[x]];
                        float spark = PointAt(_colSparkU[x] + cell.Disp.x * ActiveSparkDisplaceIntensity,
                                              sparkV + cell.Disp.y * ActiveSparkDisplaceIntensity);
                        float sv = spark * cell.DispAvg * sparkScale;

                        Vector3 fill = cell.FillFlat;
                        if (sv > 1e-4f)
                        {
                            Vector3 sparkVec = _nfSpark * sv;
                            Vector3 hsv = cell.SceneHsv + sparkVec * ActiveSparkHueShiftAmount;
                            Vector3 processed = Overlay(HsvToRgb(hsv), sparkVec);
                            fill = new Vector3(
                                cell.FillBase.x + _fillStrength * (processed.x - cell.FillBase.x),
                                cell.FillBase.y + _fillStrength * (processed.y - cell.FillBase.y),
                                cell.FillBase.z + _fillStrength * (processed.z - cell.FillBase.z));
                            // Exposure last, as in the reference: it lifts the finished fill.
                            if (_exposure > 0f) fill += (_nfEdge - fill) * _exposure;
                        }

                        float edgeTerm = edge * _edgeOpacity;
                        float glowAdj = glow * (1f - (edge + comp));
                        float glowTerm = glowAdj * _glowIntensity;

                        Vector3 rgb = fill * comp + _nfEdge * edgeTerm + _nfGlow * glowTerm;
                        float alpha = comp * _fillOpacity + edgeTerm + glowTerm;

                        // One / OneMinusSrcAlpha in the skin; Sprites/Default is SrcAlpha, and
                        // Sprites/Default's source colour is rgb*alpha, so store rgb
                        // un-premultiplied.
                        if (alpha > 1e-4f) rgb /= alpha;

                        dst[row + x] = ToColor32(rgb, alpha);
                    }
                }

                Interlocked.Add(ref MainTicks, Stopwatch.GetTimestamp() - startedTicks);
            }

            /// <summary>Fills one 6-pixel cell: the block-noise taps, displacement and no-grain fill.</summary>
            private Cell BuildCell(float cellU, float cellV, Vector3 nfFill, Vector3 scene)
            {
                Vector2 duv = new Vector2(cellU * ActiveDisplaceMapST.x, cellV * ActiveDisplaceMapST.y);

                float a1 = BlockAt(duv.x + _drift.x, duv.y + _drift.y);
                float a2 = BlockAt(duv.x + _ortho.x, duv.y + _ortho.y);
                float dispAvg = (a1 + a2) * 0.5f;
                Vector2 disp = new Vector2(_dir.x * (a1 - 0.5f) + _perp.x * (a2 - 0.5f),
                                           _dir.y * (a1 - 0.5f) + _perp.y * (a2 - 0.5f));

                Vector3 fillBase = nfFill - Vector3.one * (dispAvg * DisplaceBlendIntensity);

                // overlay(scene, 0): the fill with no grain. Ch(b,0) is 2b·0 below 0.5 and
                // 1−2(1−b) at or above, i.e. max(0, 2b−1).
                Vector3 processed0 = new Vector3(
                    Mathf.Max(0f, 2f * scene.x - 1f),
                    Mathf.Max(0f, 2f * scene.y - 1f),
                    Mathf.Max(0f, 2f * scene.z - 1f));

                // The unlit fill, with exposure folded in.
                //
                // Exposure lifts the fill toward the outline's own full-value tone. The sprite
                // is alpha blended over the live scene, so inside the region the screen shows
                // `fill * alpha + behind * (1 - alpha)`: brightening `fill` brightens whatever
                // is behind the region without reading any of it, so the field acts as a
                // light-lifting film rather than a cover. The target is the outline colour at
                // full value rather than white, so the lift keeps the field's hue instead of
                // washing the region grey. Folded in here because the no-grain path is what
                // almost every pixel takes.
                Vector3 lit = fillBase + _fillStrength * (processed0 - fillBase);
                if (_exposure > 0f) lit += (_nfEdge - lit) * _exposure;

                return new Cell
                {
                    DispAvg = dispAvg,
                    Disp = disp,
                    FillBase = fillBase,
                    SceneHsv = RgbToHsv(scene),
                    FillFlat = lit
                };
            }
        }

        /// <summary>
        /// Builds one frame's grids and renders the whole texture. Kept for callers that have
        /// no worker pool of their own (diagnostics, tests); the factory fans out instead.
        /// </summary>
        public static Color32[] Compose(NoiseTextureFactory.Piece[] pieces,
            NoiseTextureFactory.Request req, int texW, int texH, float time, uint seed)
        {
            WarmTextures();
            var frame = Frame.Build(pieces, req, texW, texH, time, seed, 8);

            var pixels = new Color32[texW * texH];
            var watch = Stopwatch.StartNew();
            frame.RenderRows(pixels, 0, texH);
            Interlocked.Add(ref MainTicks, watch.ElapsedTicks);
            return pixels;
        }

        private static Vector2 Normalize(Vector2 d)
        {
            float len = Mathf.Sqrt(d.x * d.x + d.y * d.y);
            return len > 0.00001f ? new Vector2(d.x / len, d.y / len) : new Vector2(0.70710678f, 0.70710678f);
        }

        private static bool Inside(NoiseTextureFactory.Piece[] pieces, float wx, float wy)
        {
            if (pieces == null || pieces.Length == 0) return true;
            for (int i = 0; i < pieces.Length; i++)
            {
                var p = pieces[i];
                float dx = wx - p.Offset.x, dy = wy - p.Offset.y;
                float rad = p.Rotation * Mathf.Deg2Rad;
                float cos = Mathf.Cos(rad), sin = Mathf.Sin(rad);
                float lx = dx * cos + dy * sin;
                float ly = -dx * sin + dy * cos;

                if (p.Circle)
                {
                    float nx = lx / Mathf.Max(1e-4f, p.Half.x);
                    float ny = ly / Mathf.Max(1e-4f, p.Half.y);
                    if (nx * nx + ny * ny <= 1f) return true;
                }
                else if (Mathf.Abs(lx) <= p.Half.x && Mathf.Abs(ly) <= p.Half.y) return true;
            }
            return false;
        }

        // --- main.glsl's faithful colour helpers -----------------------------------------------

        private static Vector3 RgbToHsv(Vector3 c)
        {
            float hi = Mathf.Max(Mathf.Max(c.x, c.y), c.z);
            float lo = Mathf.Min(Mathf.Min(c.x, c.y), c.z);
            float d = hi - lo;
            float hh = 0f;
            if (d > 1e-10f)
            {
                if (hi == c.x) hh = Mathf.Repeat((c.y - c.z) / d, 6f);
                else if (hi == c.y) hh = (c.z - c.x) / d + 2f;
                else hh = (c.x - c.y) / d + 4f;
                hh = Mathf.Repeat(hh / 6f, 1f);
            }
            float s = hi <= 1e-10f ? 0f : d / hi;
            return new Vector3(hh, s, hi);
        }

        private static Vector3 HsvToRgb(Vector3 hsv)
        {
            float p0 = Mathf.Clamp(Mathf.Abs(Mathf.Repeat(hsv.x + 1f, 1f) * 6f - 3f) - 1f, 0f, 1f);
            float p1 = Mathf.Clamp(Mathf.Abs(Mathf.Repeat(hsv.x + 2f / 3f, 1f) * 6f - 3f) - 1f, 0f, 1f);
            float p2 = Mathf.Clamp(Mathf.Abs(Mathf.Repeat(hsv.x + 1f / 3f, 1f) * 6f - 3f) - 1f, 0f, 1f);
            return new Vector3(
                hsv.z * (1f + hsv.y * (p0 - 1f)),
                hsv.z * (1f + hsv.y * (p1 - 1f)),
                hsv.z * (1f + hsv.y * (p2 - 1f)));
        }

        private static Vector3 Overlay(Vector3 b, Vector3 x)
        {
            return new Vector3(Ch(b.x, x.x), Ch(b.y, x.y), Ch(b.z, x.z));
        }

        private static float Ch(float b, float x)
        {
            float low = 2f * b * x;
            float high = 1f - 2f * (1f - b) * (1f - x);
            return Mathf.Clamp(b >= 0.5f ? high : low, 0f, 1f);
        }

        private static Vector3 Variant(Color c, float satScale, float valueScale)
        {
            Vector3 hsv = RgbToHsv(new Vector3(c.r, c.g, c.b));
            hsv.y = Mathf.Clamp01(hsv.y * satScale);
            hsv.z = Mathf.Clamp01(hsv.z * valueScale);
            return HsvToRgb(hsv);
        }

        private static Color32 ToColor32(Vector3 rgb, float alpha)
        {
            return new Color32(
                (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(rgb.x) * 255f), 0, 255),
                (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(rgb.y) * 255f), 0, 255),
                (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(rgb.z) * 255f), 0, 255),
                (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(alpha) * 255f), 0, 255));
        }
    }
}
