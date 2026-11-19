using System;
using System.Collections.Generic;
using PEV.Scripting;
using UnityEngine;

namespace PEV.Diagnostics
{
    /// <summary>
    /// A CPU implementation of exactly what the grading shader computes.
    ///
    /// It exists so "the picture changed" can be replaced by "the picture matches the
    /// documented arithmetic". Every formula here is the same one in
    /// <c>src/UnityShaders/ColorGrading/ColorGrading.shader</c>; when the two disagree the
    /// harness reports the number of levels they differ by, which is how a real defect —
    /// a missing half-texel inset, a saturation that is not luma-preserving — is told apart
    /// from float rounding.
    ///
    /// Keep this in step with the shader. It is the specification the shader is tested
    /// against, not an independent implementation that happens to look similar.
    /// </summary>
    internal static class GradingReference
    {
        internal sealed class Lut
        {
            public int Size;
            public bool OneDimensional;
            public Color32[] Pixels;
            public Vector3 DomainMin = Vector3.zero;
            public Vector3 DomainMax = Vector3.one;
        }

        internal sealed class Settings
        {
            public float Exposure, Brightness, Contrast, Saturation, Vibrance, Temperature, Tint;
            public float Highlights, Shadows, Whites, Blacks;
            public float Gamma = 1f;
            public Vector3 Gain = Vector3.one, Offset = Vector3.zero;
            public Vector4 Lift, GammaWheel, GainWheel, OffsetWheel;
            public float[] MasterCurve, RedCurve, GreenCurve, BlueCurve;
            public float[] HueHue, HueSat, HueLuma;
            public Vector4[] HslBands;
            public Lut Lut;
            public float LutStrength = 1f;
            public float Strength = 1f;
            public float Weight = 1f;
            public bool LinearProject;
        }

        private static readonly Vector3 LumaWeights = new Vector3(0.2126f, 0.7152f, 0.0722f);

        internal static float Luma(Vector3 c) => c.x * LumaWeights.x + c.y * LumaWeights.y + c.z * LumaWeights.z;

        private static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;

        private static Vector3 Saturate(Vector3 c) =>
            new Vector3(Clamp01(c.x), Clamp01(c.y), Clamp01(c.z));

        private static float SmoothStep(float edge0, float edge1, float x)
        {
            if (edge1 <= edge0) return 0f;
            float t = Clamp01((x - edge0) / (edge1 - edge0));
            return t * t * (3f - 2f * t);
        }

        internal static Vector3 SRGBToLinear(Vector3 c)
        {
            c = Saturate(c);
            return new Vector3(ChannelToLinear(c.x), ChannelToLinear(c.y), ChannelToLinear(c.z));

            float ChannelToLinear(float v) =>
                v <= 0.04045f ? v / 12.92f : Mathf.Pow((v + 0.055f) / 1.055f, 2.4f);
        }

        internal static Vector3 LinearToSRGB(Vector3 c)
        {
            c = new Vector3(Mathf.Max(c.x, 0f), Mathf.Max(c.y, 0f), Mathf.Max(c.z, 0f));
            return new Vector3(ChannelToSrgb(c.x), ChannelToSrgb(c.y), ChannelToSrgb(c.z));

            float ChannelToSrgb(float v) =>
                v <= 0.0031308f ? v * 12.92f : 1.055f * Mathf.Pow(v, 1f / 2.4f) - 0.055f;
        }

        private static Vector3 HSVToRGB(Vector3 c)
        {
            float h = c.x * 6f;
            if (h < 0f) h += 6f;
            float s = c.y, v = c.z;
            int i = Mathf.FloorToInt(h) % 6;
            float f = h - Mathf.Floor(h);
            float p = v * (1f - s);
            float q = v * (1f - f * s);
            float t = v * (1f - (1f - f) * s);
            switch (i)
            {
                case 0: return new Vector3(v, t, p);
                case 1: return new Vector3(q, v, p);
                case 2: return new Vector3(p, v, t);
                case 3: return new Vector3(p, q, v);
                case 4: return new Vector3(t, p, v);
                default: return new Vector3(v, p, q);
            }
        }

        private static Vector3 RGBToHSL(Vector3 c)
        {
            float maxc = Mathf.Max(c.x, Mathf.Max(c.y, c.z));
            float minc = Mathf.Min(c.x, Mathf.Min(c.y, c.z));
            float l = (maxc + minc) * 0.5f;
            float d = maxc - minc;
            float h = 0f, s = 0f;
            if (d > 1e-6f)
            {
                s = l > 0.5f ? d / (2f - maxc - minc) : d / (maxc + minc);
                if (maxc == c.x) h = (c.y - c.z) / d + (c.y < c.z ? 6f : 0f);
                else if (maxc == c.y) h = (c.z - c.x) / d + 2f;
                else h = (c.x - c.y) / d + 4f;
                h /= 6f;
            }
            return new Vector3(h, s, l);
        }

        /// <summary>Mirrors the shader's branchy HSL-to-RGB, which is not the textbook form.</summary>
        private static Vector3 HSLToRGB(Vector3 c)
        {
            float h = c.x, s = c.y, l = c.z;
            Vector3 rgb = new Vector3(l, l, l);
            if (s > 1e-6f)
            {
                float q = l < 0.5f ? l * (1f + s) : l + s - l * s;
                float p = 2f * l - q;
                rgb = new Vector3(HueChannel(h + 1f / 3f), HueChannel(h), HueChannel(h - 1f / 3f));

                float HueChannel(float t)
                {
                    t -= Mathf.Floor(t);
                    if (t * 6f < 1f) return p + (q - p) * 6f * t;
                    if (t * 2f < 1f) return q;
                    if (t * 3f < 2f) return p + (q - p) * (2f / 3f - t) * 6f;
                    return p;
                }
            }
            return rgb;
        }

        private static Vector3 WheelChroma(Vector4 wheel, float scale)
        {
            float radius = Mathf.Min(Mathf.Sqrt(wheel.x * wheel.x + wheel.y * wheel.y), 1f);
            Vector3 chroma = Vector3.zero;
            if (radius > 1e-5f)
            {
                float hue = Mathf.Atan2(wheel.y, wheel.x) / 6.28318530718f;
                if (hue < 0f) hue += 1f;
                chroma = HSVToRGB(new Vector3(hue, 1f, 1f)) - Vector3.one;
                chroma *= radius * scale;
            }
            return chroma;
        }

        /// <summary>Bilinear sample of a 256-wide RGBA row, matching texture sampling rules.</summary>
        private static Vector4 SampleCurve(byte[] table, float u)
        {
            if (table == null) return new Vector4(u, u, u, u);
            const int width = 256;
            float x = Clamp01(u) * width - 0.5f;
            int i0 = Mathf.FloorToInt(x);
            float f = x - i0;
            int a = Mathf.Clamp(i0, 0, width - 1);
            int b = Mathf.Clamp(i0 + 1, 0, width - 1);
            return Vector4.Lerp(
                new Vector4(table[a * 4] / 255f, table[a * 4 + 1] / 255f, table[a * 4 + 2] / 255f, table[a * 4 + 3] / 255f),
                new Vector4(table[b * 4] / 255f, table[b * 4 + 1] / 255f, table[b * 4 + 2] / 255f, table[b * 4 + 3] / 255f),
                f);
        }

        internal static Vector3 Evaluate(Settings s, Vector3 source)
        {
            Vector3 original = Saturate(source);
            Vector3 c = s.LinearProject ? LinearToSRGB(original) : original;

            if (Mathf.Abs(s.Exposure) > 1e-5f)
            {
                Vector3 decoded = SRGBToLinear(c) * Mathf.Pow(2f, s.Exposure);
                c = LinearToSRGB(decoded);
            }

            if (Mathf.Abs(s.Temperature) > 1e-5f || Mathf.Abs(s.Tint) > 1e-5f)
            {
                Vector3 gains = new Vector3(1f + 0.30f * s.Temperature, 1f - 0.22f * s.Tint,
                    1f - 0.30f * s.Temperature);
                gains /= Mathf.Max(Luma(gains), 1e-4f);
                c = Saturate(new Vector3(c.x * gains.x, c.y * gains.y, c.z * gains.z));
            }

            c = new Vector3(c.x * s.Gain.x + s.Offset.x, c.y * s.Gain.y + s.Offset.y, c.z * s.Gain.z + s.Offset.z);

            // Wheels.
            Vector3 lift = WheelChroma(s.Lift, 0.5f) + new Vector3(s.Lift.z, s.Lift.z, s.Lift.z);
            Vector3 gain = WheelChroma(s.GainWheel, 0.5f) + new Vector3(s.GainWheel.z, s.GainWheel.z, s.GainWheel.z);
            Vector3 offset = WheelChroma(s.OffsetWheel, 0.25f) +
                             new Vector3(s.OffsetWheel.z, s.OffsetWheel.z, s.OffsetWheel.z);
            Vector3 gammaVector = Vector3.one + WheelChroma(s.GammaWheel, 0.5f) +
                                  new Vector3(s.GammaWheel.z, s.GammaWheel.z, s.GammaWheel.z);
            gammaVector = new Vector3(Mathf.Max(gammaVector.x, 0.05f), Mathf.Max(gammaVector.y, 0.05f),
                Mathf.Max(gammaVector.z, 0.05f));

            c += offset;
            c = new Vector3(c.x * (1f + gain.x), c.y * (1f + gain.y), c.z * (1f + gain.z));
            Vector3 saturated = Saturate(c);
            c = new Vector3(c.x + lift.x * (1f - saturated.x), c.y + lift.y * (1f - saturated.y),
                c.z + lift.z * (1f - saturated.z));
            c = new Vector3(PowPositive(c.x, 1f / gammaVector.x), PowPositive(c.y, 1f / gammaVector.y),
                PowPositive(c.z, 1f / gammaVector.z));

            // Tone.
            float l = Luma(c);
            float shadowWeight = 1f - SmoothStep(0f, 0.55f, l);
            float highlightWeight = SmoothStep(0.45f, 1f, l);
            float whiteWeight = SmoothStep(0.7f, 1f, l);
            float blackWeight = 1f - SmoothStep(0f, 0.30f, l);
            Vector3 sat = Saturate(c);
            float whiteZone = SmoothStep(0.55f, 1f, l);
            float blackZone = 1f - SmoothStep(0f, 0.45f, l);
            c = new Vector3(
                c.x + (s.Shadows * 0.5f) * shadowWeight * (1f - sat.x)
                    - (s.Highlights * 0.5f) * highlightWeight * sat.x
                    + (s.Whites * 0.4f) * whiteWeight * whiteZone
                    + (s.Blacks * 0.4f) * blackWeight * blackZone,
                c.y + (s.Shadows * 0.5f) * shadowWeight * (1f - sat.y)
                    - (s.Highlights * 0.5f) * highlightWeight * sat.y
                    + (s.Whites * 0.4f) * whiteWeight * whiteZone
                    + (s.Blacks * 0.4f) * blackWeight * blackZone,
                c.z + (s.Shadows * 0.5f) * shadowWeight * (1f - sat.z)
                    - (s.Highlights * 0.5f) * highlightWeight * sat.z
                    + (s.Whites * 0.4f) * whiteWeight * whiteZone
                    + (s.Blacks * 0.4f) * blackWeight * blackZone);

            // Contrast.
            if (Mathf.Abs(s.Contrast) > 1e-5f)
            {
                float factor = s.Contrast >= 0f ? 1f + s.Contrast * 2f : 1f / (1f - s.Contrast * 2f);
                c = Saturate((c - new Vector3(0.5f, 0.5f, 0.5f)) * factor + new Vector3(0.5f, 0.5f, 0.5f));
            }

            if (Mathf.Abs(s.Gamma - 1f) > 1e-5f)
                c = Saturate(new Vector3(PowPositive(c.x, 1f / Mathf.Max(s.Gamma, 1e-3f)),
                    PowPositive(c.y, 1f / Mathf.Max(s.Gamma, 1e-3f)),
                    PowPositive(c.z, 1f / Mathf.Max(s.Gamma, 1e-3f))));

            c = Saturate(c + new Vector3(s.Brightness * 0.5f, s.Brightness * 0.5f, s.Brightness * 0.5f));

            // Curves: master first, then the channel curves, exactly as the shader orders them.
            if (s.MasterCurve != null || s.RedCurve != null)
            {
                var masterRow = CurveBakeRow(s.MasterCurve, true);
                var redRow = CurveBakeRow(s.RedCurve, false);
                var greenRow = CurveBakeRow(s.GreenCurve, false);
                var blueRow = CurveBakeRow(s.BlueCurve, false);
                if (masterRow != null)
                {
                    float mr = SampleCurve(masterRow, c.x).w;
                    float mg = SampleCurve(masterRow, c.y).w;
                    float mb = SampleCurve(masterRow, c.z).w;
                    c = new Vector3(mr, mg, mb);
                }
                if (redRow != null) c.x = SampleCurve(redRow, c.x).x;
                if (greenRow != null) c.y = SampleCurve(greenRow, c.y).y;
                if (blueRow != null) c.z = SampleCurve(blueRow, c.z).z;
            }

            // Hue curves.
            if (s.HueHue != null || s.HueSat != null || s.HueLuma != null)
            {
                Vector3 hsl = RGBToHSL(Saturate(c));
                float u = hsl.x - Mathf.Floor(hsl.x);
                float hueShift = 0f, satScale = 0f, lumShift = 0f;
                if (s.HueHue != null) hueShift = (SampleCurve(CurveBakeRow(s.HueHue, false), u).x - 0.5f) * 2f;
                if (s.HueSat != null) satScale = (SampleCurve(CurveBakeRow(s.HueSat, false), u).y - 0.5f) * 2f;
                if (s.HueLuma != null) lumShift = (SampleCurve(CurveBakeRow(s.HueLuma, false), u).z - 0.5f) * 2f;
                if (Mathf.Abs(hueShift) > 1e-5f || Mathf.Abs(satScale) > 1e-5f || Mathf.Abs(lumShift) > 1e-5f)
                {
                    hsl.x = hsl.x + hueShift * 0.5f;
                    hsl.x -= Mathf.Floor(hsl.x);
                    hsl.y = Clamp01(hsl.y * (1f + satScale));
                    hsl.z = Clamp01(hsl.z + lumShift * 0.5f);
                    c = HSLToRGB(hsl);
                }
            }

            // Saturation and vibrance.
            float luma = Luma(c);
            if (Mathf.Abs(s.Saturation) > 1e-5f)
                c = LerpUnclamped(new Vector3(luma, luma, luma), c, 1f + s.Saturation);
            if (Mathf.Abs(s.Vibrance) > 1e-5f)
            {
                float current = Mathf.Max(c.x, Mathf.Max(c.y, c.z)) - Mathf.Min(c.x, Mathf.Min(c.y, c.z));
                float weight = 1f - Clamp01(current);
                float l2 = Luma(c);
                var boosted = LerpUnclamped(new Vector3(l2, l2, l2), c, 1.6f);
                c = LerpUnclamped(c, boosted, s.Vibrance * weight);
            }
            c = Saturate(c);

            // HSL bands.
            c = ApplyBands(s, c);

            // LUT.
            if (s.Lut != null && s.LutStrength > 0f) c = Saturate(SampleLut(s, c));

            float amount = Clamp01(s.Strength) * Clamp01(s.Weight);
            Vector3 blended = Vector3.Lerp(original, c, amount);
            if (s.LinearProject) blended = SRGBToLinear(blended);
            return blended;
        }

        private static Vector3 ApplyBands(Settings s, Vector3 c)
        {
            if (s.HslBands == null) return c;
            Vector3 hsl = RGBToHSL(Saturate(c));
            float hue = hsl.x;
            float hueShift = 0f, satMul = 0f, lumAdd = 0f;
            for (int i = 0; i < 8 && i < s.HslBands.Length; i++)
            {
                var band = s.HslBands[i];
                if (Mathf.Abs(band.x) < 1e-5f && Mathf.Abs(band.y) < 1e-5f && Mathf.Abs(band.z) < 1e-5f) continue;
                float distance = Mathf.Abs(hue - i / 8f);
                distance = Mathf.Min(distance, 1f - distance);
                float weight = Clamp01(1f - distance / (1f / 8f)) * band.w;
                hueShift += band.x * weight;
                satMul += band.y * weight;
                lumAdd += band.z * weight;
            }
            if (Mathf.Abs(hueShift) < 1e-5f && Mathf.Abs(satMul) < 1e-5f && Mathf.Abs(lumAdd) < 1e-5f) return c;
            hsl.x = hsl.x + hueShift / 360f;
            hsl.x -= Mathf.Floor(hsl.x);
            hsl.y = Clamp01(hsl.y * (1f + satMul));
            hsl.z = Clamp01(hsl.z + lumAdd * 0.5f);
            return HSLToRGB(hsl);
        }

        private static Vector3 SampleLut(Settings s, Vector3 c)
        {
            var lut = s.Lut;
            Vector3 span = new Vector3(
                Mathf.Max(lut.DomainMax.x - lut.DomainMin.x, 1e-5f),
                Mathf.Max(lut.DomainMax.y - lut.DomainMin.y, 1e-5f),
                Mathf.Max(lut.DomainMax.z - lut.DomainMin.z, 1e-5f));
            Vector3 uvw = Saturate(new Vector3((c.x - lut.DomainMin.x) / span.x,
                (c.y - lut.DomainMin.y) / span.y, (c.z - lut.DomainMin.z) / span.z));

            if (lut.OneDimensional)
            {
                var r = SampleStrip(lut, uvw.x).x;
                var g = SampleStrip(lut, uvw.y).y;
                var b = SampleStrip(lut, uvw.z).z;
                return Vector3.Lerp(c, new Vector3(r, g, b), Clamp01(s.LutStrength));
            }

            float size = Mathf.Max(lut.Size, 2f);
            float inset = 0.5f / size;
            float scale = (size - 1f) / size;
            Vector3 coordinate = uvw * scale + new Vector3(inset, inset, inset);

            // Trilinear sample of the volume, matching bilinear texture filtering in hardware.
            float x = coordinate.x * lut.Size - 0.5f;
            float y = coordinate.y * lut.Size - 0.5f;
            float z = coordinate.z * lut.Size - 0.5f;
            int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y), z0 = Mathf.FloorToInt(z);
            float fx = x - x0, fy = y - y0, fz = z - z0;

            Vector3 Sample(int ix, int iy, int iz)
            {
                int cx = Mathf.Clamp(ix, 0, lut.Size - 1);
                int cy = Mathf.Clamp(iy, 0, lut.Size - 1);
                int cz = Mathf.Clamp(iz, 0, lut.Size - 1);
                int index = cx + cy * lut.Size + cz * lut.Size * lut.Size;
                var pixel = lut.Pixels[index];
                return new Vector3(pixel.r / 255f, pixel.g / 255f, pixel.b / 255f);
            }

            var c00 = Vector3.Lerp(Sample(x0, y0, z0), Sample(x0 + 1, y0, z0), fx);
            var c10 = Vector3.Lerp(Sample(x0, y0 + 1, z0), Sample(x0 + 1, y0 + 1, z0), fx);
            var c01 = Vector3.Lerp(Sample(x0, y0, z0 + 1), Sample(x0 + 1, y0, z0 + 1), fx);
            var c11 = Vector3.Lerp(Sample(x0, y0 + 1, z0 + 1), Sample(x0 + 1, y0 + 1, z0 + 1), fx);
            var lower = Vector3.Lerp(c00, c10, fy);
            var upper = Vector3.Lerp(c01, c11, fy);
            var sampled = Vector3.Lerp(lower, upper, fz);
            return Vector3.Lerp(c, sampled, Clamp01(s.LutStrength));
        }

        private static Vector3 SampleStrip(Lut lut, float u)
        {
            float x = Clamp01(u) * lut.Size - 0.5f;
            int i0 = Mathf.FloorToInt(x);
            float f = x - i0;
            var a = lut.Pixels[Mathf.Clamp(i0, 0, lut.Size - 1)];
            var b = lut.Pixels[Mathf.Clamp(i0 + 1, 0, lut.Size - 1)];
            return Vector3.Lerp(new Vector3(a.r / 255f, a.g / 255f, a.b / 255f),
                new Vector3(b.r / 255f, b.g / 255f, b.b / 255f), f);
        }

        /// <summary>Unity's Vector3.Lerp clamps its factor; the shader's lerp does not.</summary>
        private static Vector3 LerpUnclamped(Vector3 a, Vector3 b, float t) => a + (b - a) * t;

        private static float PowPositive(float value, float power) => Mathf.Pow(Mathf.Max(value, 0f), power);

        private static readonly Dictionary<string, byte[]> CurveCache = new Dictionary<string, byte[]>();

        private static byte[] CurveBakeRow(float[] points, bool masterChannel)
        {
            if (points == null) return null;
            string key = (masterChannel ? "m" : "c") + string.Join(",", points);
            if (CurveCache.TryGetValue(key, out var cached)) return cached;

            var table = new byte[CurveBake.Resolution * 4];
            var channel = new byte[CurveBake.Resolution];
            if (!CurveBake.TryEvaluate(points, channel)) return null;
            for (int i = 0; i < CurveBake.Resolution; i++)
            {
                // Only one channel matters here; the shader reads the same table through a
                // different component for master and for each colour.
                table[i * 4 + 0] = channel[i];
                table[i * 4 + 1] = channel[i];
                table[i * 4 + 2] = channel[i];
                table[i * 4 + 3] = channel[i];
            }
            if (CurveCache.Count > 64) CurveCache.Clear();
            CurveCache[key] = table;
            return table;
        }
    }
}
