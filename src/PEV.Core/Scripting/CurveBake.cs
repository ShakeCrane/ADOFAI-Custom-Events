using System;
using System.Collections.Generic;
using UnityEngine;

namespace PEV.Scripting
{
    /// <summary>
    /// Turns a package-supplied control-point curve into a lookup texture the shader samples.
    ///
    /// Curves are the one grading control that cannot be a slider, so the event package sends
    /// points and Core bakes them. Baking once per change (not per frame) keeps the shader to
    /// a single texture fetch and makes the curve shape identical in the editor and in play —
    /// both sides run this same bake over the same points.
    ///
    /// Interpolation is monotone cubic Hermite (Fritsch–Carlson). A plain cubic spline
    /// overshoots between control points, which on a tone curve shows up as banding and
    /// reversed highlights; clamping the tangents removes that without flattening the curve.
    /// </summary>
    internal static class CurveBake
    {
        internal const int Resolution = 256;
        internal const int MaxPoints = 16;

        /// <summary>
        /// Evaluates <paramref name="points"/> (flat x0,y0,x1,y1,… normalised 0..1) into
        /// <paramref name="destination"/> as 0..255 bytes.
        ///
        /// An empty or single-point curve is the identity, which is what "neutral" means:
        /// a missing curve must not darken the picture.
        /// </summary>
        internal static bool TryEvaluate(IReadOnlyList<float> points, byte[] destination)
        {
            if (destination == null || destination.Length < Resolution) return false;

            var xs = new List<float>();
            var ys = new List<float>();
            if (points != null && points.Count >= 4)
            {
                for (int i = 0; i + 1 < points.Count && xs.Count < MaxPoints; i += 2)
                {
                    float x = points[i];
                    float y = points[i + 1];
                    if (float.IsNaN(x) || float.IsInfinity(x) || float.IsNaN(y) || float.IsInfinity(y)) return false;
                    x = Mathf.Clamp01(x);
                    y = Mathf.Clamp01(y);
                    // Sorted and de-duplicated: two points at the same input have no defined
                    // output, and a shader reading them would flicker between the two.
                    if (xs.Count > 0 && x <= xs[xs.Count - 1] + 1e-4f) continue;
                    xs.Add(x);
                    ys.Add(y);
                }
            }

            if (xs.Count == 0)
            {
                for (int i = 0; i < Resolution; i++) destination[i] = (byte)Mathf.RoundToInt(i * 255f / (Resolution - 1));
                return true;
            }
            if (xs.Count == 1)
            {
                byte value = (byte)Mathf.RoundToInt(ys[0] * 255f);
                for (int i = 0; i < Resolution; i++) destination[i] = value;
                return true;
            }

            var slopes = Tangents(xs, ys);
            int segment = 0;
            for (int i = 0; i < Resolution; i++)
            {
                float x = i / (float)(Resolution - 1);
                // Advance to the segment containing x. Points are sorted, so a single forward
                // walk covers the whole table instead of a binary search per entry.
                while (segment + 2 < xs.Count && x > xs[segment + 1]) segment++;
                float value;
                if (x <= xs[0]) value = ys[0];
                else if (x >= xs[xs.Count - 1]) value = ys[ys.Count - 1];
                else
                {
                    while (segment + 2 < xs.Count && x > xs[segment + 1]) segment++;
                    float h = xs[segment + 1] - xs[segment];
                    float t = h <= 0f ? 0f : (x - xs[segment]) / h;
                    float t2 = t * t;
                    float t3 = t2 * t;
                    float h00 = 2f * t3 - 3f * t2 + 1f;
                    float h10 = t3 - 2f * t2 + t;
                    float h01 = -2f * t3 + 3f * t2;
                    float h11 = t3 - t2;
                    value = h00 * ys[segment] + h10 * h * slopes[segment] +
                            h01 * ys[segment + 1] + h11 * h * slopes[segment + 1];
                }
                destination[i] = (byte)Mathf.Clamp(Mathf.RoundToInt(value * 255f), 0, 255);
            }
            return true;
        }

        /// <summary>
        /// Fritsch–Carlson tangents: the average of the neighbouring secants, scaled down so
        /// no segment can overshoot. This is what makes the curve monotone where the author
        /// made it monotone.
        /// </summary>
        private static float[] Tangents(List<float> xs, List<float> ys)
        {
            int n = xs.Count;
            var slopes = new float[n];
            var secants = new float[n - 1];
            for (int i = 0; i < n - 1; i++)
            {
                float dx = xs[i + 1] - xs[i];
                secants[i] = dx > 0f ? (ys[i + 1] - ys[i]) / dx : 0f;
            }
            slopes[0] = secants[0];
            slopes[n - 1] = secants[n - 2];
            for (int i = 1; i < n - 1; i++)
            {
                if (secants[i - 1] * secants[i] <= 0f) slopes[i] = 0f;
                else
                {
                    slopes[i] = (secants[i - 1] + secants[i]) * 0.5f;
                    // Clamp: a tangent beyond 3x the smaller secant creates a loop.
                    float limit = 3f * Mathf.Min(Mathf.Abs(secants[i - 1]), Mathf.Abs(secants[i]));
                    slopes[i] = Mathf.Clamp(slopes[i], -limit, limit);
                }
            }
            return slopes;
        }

        /// <summary>
        /// Builds the RGBA table a grading shader samples: R, G and B are the per-channel
        /// curves and A is the master curve. Packing all four into one texture keeps the
        /// number of texture fetches and sampler slots at one regardless of how many curves
        /// the author used.
        /// </summary>
        internal static Texture2D BuildTexture(IReadOnlyList<float> master, IReadOnlyList<float> red,
            IReadOnlyList<float> green, IReadOnlyList<float> blue, string name)
        {
            var redBytes = new byte[Resolution];
            var greenBytes = new byte[Resolution];
            var blueBytes = new byte[Resolution];
            var masterBytes = new byte[Resolution];
            if (!TryEvaluate(red, redBytes)) return null;
            if (!TryEvaluate(green, greenBytes)) return null;
            if (!TryEvaluate(blue, blueBytes)) return null;
            if (!TryEvaluate(master, masterBytes)) return null;

            var pixels = new Color32[Resolution];
            for (int i = 0; i < Resolution; i++)
                pixels[i] = new Color32(redBytes[i], greenBytes[i], blueBytes[i], masterBytes[i]);

            var texture = new Texture2D(Resolution, 1, TextureFormat.RGBA32, false, true)
            {
                name = name,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                anisoLevel = 0,
                hideFlags = HideFlags.DontSave
            };
            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            return texture;
        }

        /// <summary>
        /// Same bake, but into a single-channel row. Secondary curves (hue against hue,
        /// saturation and luma) need only one value per input, and a 256-wide luminance
        /// texture costs a quarter of the memory of the RGBA form.
        /// </summary>
        internal static Texture2D BuildLuminanceTexture(IReadOnlyList<float> points, string name)
        {
            var bytes = new byte[Resolution];
            if (!TryEvaluate(points, bytes)) return null;
            var pixels = new Color32[Resolution];
            for (int i = 0; i < Resolution; i++) pixels[i] = new Color32(bytes[i], bytes[i], bytes[i], 255);
            var texture = new Texture2D(Resolution, 1, TextureFormat.RGBA32, false, true)
            {
                name = name,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                anisoLevel = 0,
                hideFlags = HideFlags.DontSave
            };
            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            return texture;
        }
    }
}
