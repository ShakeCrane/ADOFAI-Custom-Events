using System;
using System.Collections.Generic;
using System.IO;
using PEV.Scripting;
using UnityEngine;

namespace PEV.Diagnostics
{
    /// <summary>
    /// Reads the frame the gameplay camera actually draws, and refuses to call a blank one
    /// evidence.
    ///
    /// ScreenCapture.CaptureScreenshot was the previous path and it is unusable here: the
    /// five captures it produced across two harnesses are byte-identical (sha256 b50177e9…)
    /// and every one of them is a flat white image. An all-white frame, an empty frame and a
    /// stale frame from an earlier run are indistinguishable from "the effect is gone", which
    /// is exactly the failure a pixel comparison is supposed to catch. So the frame is taken
    /// through the camera itself: render into a RenderTexture we own, read it back once, and
    /// check the statistics before reporting success.
    ///
    /// The readback is diagnostic only. It is never called from the render path, and the
    /// steady-state budget in the plan forbids a per-frame CPU readback; this runs once per
    /// capture point in a harness.
    /// </summary>
    public static class FrameCapture
    {
        /// <summary>Sampling stride for the validity statistics. 4 → 1/16 of the pixels.</summary>
        private const int SampleStride = 4;

        /// <summary>
        /// Below this many distinct sampled colours the frame is treated as flat.
        ///
        /// A real gameplay frame has hundreds: the background, the track, the planets and the
        /// HUD all contribute. A flat clear, a lost camera or a white readback has one.
        /// </summary>
        private const int MinDistinctColors = 8;

        public struct Result
        {
            public bool Ok;
            public string Path;
            public string Detail;
            public int Width, Height;
            public float Mean;
            public float Variance;
            public int DistinctColors;
            public string Environment;
        }

        /// <summary>
        /// Renders <paramref name="cam"/> into an off-screen target and writes a PNG.
        ///
        /// Any file already at <paramref name="path"/> is deleted first, so a capture that
        /// fails to produce pixels cannot leave the previous run's image in place to be read
        /// as this run's evidence.
        /// </summary>
        public static Result Capture(Camera cam, string path)
        {
            var result = new Result { Path = path };
            result.Environment = DescribeEnvironment();

            if (cam == null)
            {
                result.Detail = "no gameplay camera";
                return Fail(result);
            }

            int w = Screen.width, h = Screen.height;
            if (w <= 0 || h <= 0)
            {
                result.Detail = $"degenerate viewport {w}x{h}";
                return Fail(result);
            }
            result.Width = w;
            result.Height = h;

            try { if (File.Exists(path)) File.Delete(path); } catch { }

            RenderTexture rt = null;
            Texture2D read = null;
            var previousTarget = cam.targetTexture;
            var previousActive = RenderTexture.active;
            string captureError = null;

            try
            {
                // Default read/write mode follows the project's colour space. Forcing sRGB
                // here would silently convert every pixel in a linear project and make the
                // comparison wrong in a way that looks like a rendering difference.
                rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default)
                {
                    name = "PEV.Capture",
                    antiAliasing = 1
                };
                rt.Create();

                cam.targetTexture = rt;
                cam.Render();
                cam.targetTexture = previousTarget;

                RenderTexture.active = rt;
                read = new Texture2D(w, h, TextureFormat.RGBA32, false) { name = "PEV.CaptureRead" };
                read.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                read.Apply(false);
            }
            catch (Exception e)
            {
                captureError = e.Message;
            }
            finally
            {
                RenderTexture.active = previousActive;
                cam.targetTexture = previousTarget;
            }

            if (captureError != null)
            {
                result.Detail = "capture failed: " + captureError;
                Cleanup(rt, read);
                return Fail(result);
            }

            try
            {
                var pixels = read.GetPixels32();
                var stats = Measure(pixels, w, h);
                result.Mean = stats.Mean;
                result.Variance = stats.Variance;
                result.DistinctColors = stats.Distinct;
                // Validate before writing, so an empty frame cannot be presented as evidence.
                if (stats.Distinct < MinDistinctColors)
                {
                    result.Detail = $"flat frame: distinct={stats.Distinct} mean={stats.Mean:F4} " +
                                    $"variance={stats.Variance:F6}";
                    return Fail(result);
                }
                byte[] png = NoiseTextures.EncodePng(pixels, w, h);
                if (png == null || png.Length == 0)
                {
                    result.Detail = "png encode produced no bytes";
                    return Fail(result);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
                File.WriteAllBytes(path, png);
                result.Ok = true;
                result.Detail = $"{w}x{h} bytes={png.Length} distinct={stats.Distinct} " +
                                $"mean={stats.Mean:F4} variance={stats.Variance:F6}";
                return result;
            }
            catch (Exception e)
            {
                result.Detail = "capture processing failed: " + e.Message;
                return Fail(result);
            }
            finally { Cleanup(rt, read); }
        }

        private struct Stats
        {
            public float Mean;
            public float Variance;
            public int Distinct;
        }

        /// <summary>
        /// Mean and variance of luminance plus a distinct-colour count over a sampled grid.
        ///
        /// The three together separate the failures that matter: a uniform clear has one
        /// colour, an all-black frame has zero mean and variance, and a frame that is mostly
        /// black but drawn has both.
        /// </summary>
        private static Stats Measure(Color32[] pixels, int w, int h)
        {
            var seen = new HashSet<int>();
            double sum = 0, sumSq = 0;
            long n = 0;

            for (int y = 0; y < h; y += SampleStride)
            {
                int row = y * w;
                for (int x = 0; x < w; x += SampleStride)
                {
                    var c = pixels[row + x];
                    float luma = (0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b) / 255f;
                    sum += luma;
                    sumSq += luma * luma;
                    n++;
                    seen.Add((c.r << 16) | (c.g << 8) | c.b);
                }
            }

            if (n == 0) return new Stats { Distinct = 0 };

            double mean = sum / n;
            double variance = Math.Max(0, sumSq / n - mean * mean);
            return new Stats
            {
                Mean = (float)mean,
                Variance = (float)variance,
                Distinct = seen.Count
            };
        }

        private static Result Fail(Result result)
        {
            result.Ok = false;
            return result;
        }

        private static void Cleanup(RenderTexture rt, Texture2D read)
        {
            if (read != null) UnityEngine.Object.Destroy(read);
            if (rt != null)
            {
                rt.Release();
                UnityEngine.Object.Destroy(rt);
            }
        }

        /// <summary>
        /// The environment facts a capture is only comparable within.
        ///
        /// A colour difference between two runs on different APIs or colour spaces is not a
        /// regression in the effect, so the capture records what it was taken under.
        /// </summary>
        public static string DescribeEnvironment()
        {
            try
            {
                return $"api={SystemInfo.graphicsDeviceType} " +
                       $"colorSpace={QualitySettings.activeColorSpace} " +
                       $"msaa={QualitySettings.antiAliasing} " +
                       $"viewport={Screen.width}x{Screen.height} " +
                       $"device={SystemInfo.graphicsDeviceName}";
            }
            catch (Exception e)
            {
                return "environment unavailable: " + e.Message;
            }
        }
    }
}
