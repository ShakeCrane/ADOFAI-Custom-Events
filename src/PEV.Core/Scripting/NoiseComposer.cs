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
    /// The shader's own variable names are kept throughout so the mapping stays checkable.
    /// Two substitutions are unavoidable and flagged where they happen: the screen grab behind
    /// the region is the level background colour, and SpriteRenderer blends with SrcAlpha so
    /// the premultiplied result is divided back out at the end.
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
        private const float MaskPixelScale = 8f;
        private const float DisplaceBlendIntensity = 0.41100001335144043f;
        private static readonly Vector2 ActiveDisplaceDirection = new Vector2(1f, 1f);
        private static readonly Vector2 ActiveDisplaceMapST = new Vector2(0.80f, 0.30f);
        private static readonly Vector2 ActiveSparkMapST = new Vector2(3.00f, 1.20f);
        private const float ActiveSparkDisplaceIntensity = 2.390000104904175f;
        private const float ActiveSparkHueShiftAmount = 0.20000000298023224f;

        // --- main.glsl editor defaults (the ones this event exposes) ----------------------
        private const float DefaultFillStrength = 0.667f;
        private const float DefaultBackgroundPixelScale = 6f;
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

        // --- samplers -----------------------------------------------------------------------

        /// <summary>texture2D(t, uv) nearest (GL_NEAREST).</summary>
        private static float Nearest(float[] grid, int w, int h, float u, float v)
        {
            int x = Mathf.Clamp(Mathf.FloorToInt(u * w), 0, w - 1);
            int y = Mathf.Clamp(Mathf.FloorToInt(v * h), 0, h - 1);
            return grid[y * w + x];
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

        /// <summary>Main pass reads `effects.r` at the centre of its own texel: `edgeUV` in main.glsl.</summary>
        private static float EdgeAt(float[] grid, int w, int h, float u, float v)
        {
            float qx = (Mathf.Floor(u * w) + 0.5f) / w;
            float qy = (Mathf.Floor(v * h) + 0.5f) / h;
            return Nearest(grid, w, h, qx, qy);
        }

        /// <summary>main.glsl's pixelCenter(p, scale).</summary>
        private static Vector2 PixelCenter(float u, float v, float scale, int w, int h)
        {
            float s = Mathf.Max(scale, 1f);
            return new Vector2(
                (Mathf.Floor(u * w / s) * s + s * 0.5f) / w,
                (Mathf.Floor(v * h / s) * s + s * 0.5f) / h);
        }

        // --- passes ---------------------------------------------------------------------------

        public static Color32[] Compose(NoiseTextureFactory.Piece[] pieces,
            NoiseTextureFactory.Request req, int texW, int texH, float time, uint seed)
        {
            WarmTextures();

            // The quad covers the geometry plus whatever margin the caller padded for debris.
            float margin = Mathf.Max(0f, req.Margin);
            float totalW = Mathf.Max(1e-3f, req.WidthWorld + margin * 2f);
            float totalH = Mathf.Max(1e-3f, req.HeightWorld + margin * 2f);

            // ============================ pass 1: composeActive ============================
            // target: activeCompose, 0.125. Output r = |s − n| of the packed mask bits, which
            // for a single region reduces to plain coverage. `nfMaskBit` snaps the displaced
            // coordinate to the centre of a mask texel before reading, so the coast always
            // follows mask-texel blocks no matter how finely the target is sampled.
            int cw = Mathf.Max(1, Mathf.FloorToInt(texW * ComposeScale));
            int ch = Mathf.Max(1, Mathf.FloorToInt(texH * ComposeScale));
            var compose = new float[cw * ch];

            Vector2 cdir = Normalize(ComposeDisplaceDirection);
            Vector2 cperp = new Vector2(-cdir.y, cdir.x);
            float ct = (time / 20f) * ComposeDisplaceSpeed;

            for (int j = 0; j < ch; j++)
            {
                for (int i = 0; i < cw; i++)
                {
                    float u = (i + 0.5f) / cw;
                    float v = (j + 0.5f) / ch;

                    float duvx = u * ComposeDisplaceMapST.x;
                    float duvy = v * ComposeDisplaceMapST.y;

                    float d1 = BlockAt(duvx + cdir.x * ct, duvy + cdir.y * ct) - 0.5f;
                    float d2 = BlockAt(duvx + cperp.x * ct, duvy + cperp.y * ct) - 0.5f;
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

            // ==================== passes 2..8: effects.r, pingA, pingB ====================
            // Every one of these runs at 0.25, so targetTexelSize is 1/ew by 1/eh, but pass 1
            // of the glow reads compose at 1/8 — a tap there moves half a compose texel.
            int ew = Mathf.Max(1, Mathf.FloorToInt(texW * EffectsScale));
            int eh = Mathf.Max(1, Mathf.FloorToInt(texH * EffectsScale));
            float tx = 1f / ew, ty = 1f / eh;

            // effects.r ← edge.glsl
            var edges = new float[ew * eh];
            for (int j = 0; j < eh; j++)
            {
                for (int i = 0; i < ew; i++)
                {
                    float u = (i + 0.5f) / ew;
                    float v = (j + 0.5f) / eh;
                    float m = Max3x3(compose, cw, ch, u, v, tx, ty);
                    edges[j * ew + i] = Mathf.Clamp01(m - Nearest(compose, cw, ch, u, v));
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
                    for (int i = 0; i < ew; i++)
                    {
                        float u = (i + 0.5f) / ew;
                        float v = (j + 0.5f) / eh;

                        // Pass 1 reads compose at 1/8; later passes read the previous ping at
                        // 1/4. The offsets are the same either way — they are in target texels.
                        float curR = firstPass
                            ? Nearest(compose, cw, ch, u, v)
                            : pingR[j * ew + i];
                        float curG = firstPass ? 0f : accum[j * ew + i];

                        float m = firstPass
                            ? Max3x3(compose, cw, ch, u, v, tx, ty)
                            : Max3x3(pingR, ew, eh, u, v, tx, ty);

                        float comp = Nearest(compose, cw, ch, u, v);
                        float ring = Mathf.Clamp01(m - curR) * (1f - comp);
                        float weighted = ring * weight;

                        nextR[j * ew + i] = m;
                        nextAccum[j * ew + i] = Mathf.Clamp01(firstPass ? weighted : curG + weighted);
                    }
                }

                var sr = pingR; pingR = nextR; nextR = sr;
                var sa = accum; accum = nextAccum; nextAccum = sa;
            }

            // glowExtract.glsl writes pingA.g into effects.g; pingR/accum now hold that state.

            // ================================ main.glsl =================================
            Color outline = req.EdgeColor;
            Vector3 nfFill = Variant(outline, 1f, FillValue);
            Vector3 nfEdge = Variant(outline, 1f, 1f);
            Vector3 nfGlow = Variant(outline, GlowSaturation, 1f);
            Vector3 nfSpark = Variant(outline, SparkSaturation, 1f);

            float fillOpacity = Mathf.Clamp01(req.FillOpacity);
            float fillStrength = Mathf.Clamp01(req.FillStrength <= 0f ? DefaultFillStrength : req.FillStrength);
            float edgeOpacity = Mathf.Max(0f, req.EdgeOpacity);
            float glowIntensity = Mathf.Max(0f, req.GlowOpacity);
            float backgroundPixelScale = DefaultBackgroundPixelScale;
            float activeDisplaceSpeed = DefaultActiveDisplaceSpeed;
            float activeDisplaceStrength = req.Displace > 0f ? Mathf.Clamp01(req.Displace) : DefaultActiveDisplaceStrength;
            float activeSparkMapOpacity = DefaultActiveSparkMapOpacity * Mathf.Max(0f, req.SparkOpacity);

            Vector2 dir = Normalize(ActiveDisplaceDirection);
            Vector2 perp = new Vector2(-dir.y, dir.x);
            float t = (time / 20f) * activeDisplaceSpeed;

            Color sceneColor = req.SceneColor;

            var pixels = new Color32[texW * texH];
            for (int y = 0; y < texH; y++)
            {
                for (int x = 0; x < texW; x++)
                {
                    float u = (x + 0.5f) / texW;
                    float v = (y + 0.5f) / texH;

                    float comp = Nearest(compose, cw, ch, u, v);
                    float edge = EdgeAt(edges, ew, eh, u, v);
                    float glow = Linear(accum, ew, eh, u, v);

                    if (comp + edge + glow <= 0.0001f) continue;

                    float edgeTerm = edge * edgeOpacity;
                    float glowAdj = glow * (1f - (edge + comp));
                    float glowTerm = glowAdj * glowIntensity;

                    // Scene sample, quantised to backgroundPixelScale like the shader's
                    // pixelCenter. The grab is replaced by the level background.
                    Vector2 duv = new Vector2(u * ActiveDisplaceMapST.x, v * ActiveDisplaceMapST.y);
                    Vector2 p1 = PixelCenter(duv.x + dir.x * t, duv.y + dir.y * t,
                                             backgroundPixelScale, texW, texH);
                    Vector2 p2 = PixelCenter(duv.x + perp.x * t, duv.y + perp.y * t,
                                             backgroundPixelScale, texW, texH);
                    float a1 = BlockAt(p1.x, p1.y);
                    float a2 = BlockAt(p2.x, p2.y);
                    float dispAvg = (a1 + a2) * 0.5f;
                    float dispx = dir.x * (a1 - 0.5f) + perp.x * (a2 - 0.5f);
                    float dispy = dir.y * (a1 - 0.5f) + perp.y * (a2 - 0.5f);

                    float spark = PointAt(u * ActiveSparkMapST.x + dispx * ActiveSparkDisplaceIntensity,
                                          v * ActiveSparkMapST.y + dispy * ActiveSparkDisplaceIntensity);
                    Vector3 sparkVec = nfSpark * (spark * dispAvg * activeSparkMapOpacity);

                    // rgb2hsvFaithful / hsv2rgbFaithful / overlayFaithful, per channel-block.
                    Vector3 hsv = RgbToHsv(sceneColor);
                    hsv += sparkVec * ActiveSparkHueShiftAmount;
                    Vector3 shifted = HsvToRgb(hsv);
                    Vector3 processed = Overlay(shifted, sparkVec);

                    Vector3 fillBase = nfFill - Vector3.one * (dispAvg * DisplaceBlendIntensity);
                    Vector3 fill = new Vector3(
                        fillBase.x + fillStrength * (processed.x - fillBase.x),
                        fillBase.y + fillStrength * (processed.y - fillBase.y),
                        fillBase.z + fillStrength * (processed.z - fillBase.z));

                    Vector3 rgb = fill * comp + nfEdge * edgeTerm + nfGlow * glowTerm;
                    float alpha = comp * fillOpacity + edgeTerm + glowTerm;

                    // One / OneMinusSrcAlpha in the skin; Sprites/Default is SrcAlpha, and
                    // Sprites/Default's source colour is rgb*alpha, so store rgb un-premultiplied.
                    if (alpha > 1e-4f) rgb /= alpha;

                    pixels[y * texW + x] = ToColor32(rgb, alpha);
                }
            }

            return pixels;
        }

        private static Vector2 Normalize(Vector2 d)
        {
            float len = Mathf.Sqrt(d.x * d.x + d.y * d.y);
            return len > 0.00001f ? new Vector2(d.x / len, d.y / len) : new Vector2(0.70710678f, 0.70710678f);
        }

        /// <summary>
        /// The shader's nine-tap max. `t` is one texel of the TARGET, which is what makes the
        /// 1/4 passes stepped when they read the 1/8 compose target: each tap is half a
        /// compose texel, so neighbours collapse and the window is 3 compose texels wide.
        /// </summary>
        private static float Max3x3(float[] grid, int w, int h, float u, float v, float tx, float ty)
        {
            float m = Nearest(grid, w, h, u, v);
            m = Mathf.Max(m, Nearest(grid, w, h, u + tx, v));
            m = Mathf.Max(m, Nearest(grid, w, h, u - tx, v));
            m = Mathf.Max(m, Nearest(grid, w, h, u, v + ty));
            m = Mathf.Max(m, Nearest(grid, w, h, u, v - ty));
            m = Mathf.Max(m, Nearest(grid, w, h, u + tx, v + ty));
            m = Mathf.Max(m, Nearest(grid, w, h, u - tx, v + ty));
            m = Mathf.Max(m, Nearest(grid, w, h, u + tx, v - ty));
            m = Mathf.Max(m, Nearest(grid, w, h, u - tx, v - ty));
            return m;
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

        private static Vector3 RgbToHsv(Color c)
        {
            float hi = Mathf.Max(Mathf.Max(c.r, c.g), c.b);
            float lo = Mathf.Min(Mathf.Min(c.r, c.g), c.b);
            float d = hi - lo;
            float hh = 0f;
            if (d > 1e-10f)
            {
                if (hi == c.r) hh = Mathf.Repeat((c.g - c.b) / d, 6f);
                else if (hi == c.g) hh = (c.b - c.r) / d + 2f;
                else hh = (c.r - c.g) / d + 4f;
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
            Vector3 hsv = RgbToHsv(c);
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
