using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using PEV.Loading;
using UnityEngine;

namespace PEV.Scripting
{
    // Transitional adapter from the legacy field request to a package-owned vector program.
    // All decoding, script execution and pixel conversion run on background workers.
    internal static class CpuPackageBake
    {
        internal static void Render(NoiseTextureFactory.Request req, Color32[][] frames, int width, int height, CancellationToken cancellation)
        {
            var manifest = req.Provider ?? throw new ArgumentException("Missing package provider");
            if (!manifest.VectorPrograms.TryGetValue("legacyBake", out var path))
                throw new InvalidOperationException("Package does not declare its CPU program; update the event package");
            byte[] sourceBytes = EventLoader.ReadResource(manifest.SourcePath, path, 262144);
            if (sourceBytes == null) throw new InvalidOperationException("Package CPU program is missing or too large");
            string source = new UTF8Encoding(false, true).GetString(sourceBytes);
            var resources = PixelResourceLoader.Load(manifest, cancellation);
            var profile = manifest.LegacyFieldProfile.ToDictionary(p => p.Key,
                p => p.Value.Length == 1 ? (object)p.Value[0] : p.Value);
            var pieces = (req.Pieces ?? Array.Empty<NoiseTextureFactory.Piece>()).Select(p => new
            {
                x = p.Offset.x, y = p.Offset.y, hx = p.Half.x, hy = p.Half.y,
                rotation = p.Rotation, circle = p.Circle
            }).ToArray();
            var options = new
            {
                edgeColor = new[] { req.EdgeColor.r, req.EdgeColor.g, req.EdgeColor.b },
                sceneColor = new[] { req.SceneColor.r, req.SceneColor.g, req.SceneColor.b },
                fillOpacity = req.FillOpacity, fillStrength = req.FillStrength, exposure = req.Exposure,
                edgeOpacity = req.EdgeOpacity, glowOpacity = req.GlowOpacity, sparkOpacity = req.SparkOpacity, glitch = req.Glitch
            };
            float margin = Math.Max(0f, req.Margin);
            float totalWidth = Math.Max(.001f, req.WidthWorld + margin * 2f);
            float totalHeight = Math.Max(.001f, req.HeightWorld + margin * 2f);
            float speed = Math.Max(1f, req.EvolveSpeed), period = frames.Length / speed;
            // Leave a worker slot for interactive palette updates whenever possible.
            int degree = Math.Max(1, Math.Min(4, VectorWorkerLimits.Count > 1 ? VectorWorkerLimits.Count - 1 : 1));
            using (var programs = new ThreadLocal<VectorProgram>(() => new VectorProgram(source, resources, cancellation, 15)))
            {
                var parallel = new ParallelOptions { MaxDegreeOfParallelism = degree, CancellationToken = cancellation };
                Parallel.For(0, frames.Length, parallel, k =>
                {
                    VectorWorkerLimits.Slots.Wait(cancellation);
                    try
                    {
                        string input = JsonConvert.SerializeObject(new
                        {
                            stage = "frame", width, height, screenPixelWidth = req.ScreenPixelWidth > 0 ? req.ScreenPixelWidth : width,
                            time = k / speed, loopPeriod = period, frameCount = frames.Length, frameIndex = k, seed = req.Seed,
                            totalWidth, totalHeight, profile, options, pieces
                        });
                        var output = programs.Value.Execute(width, height, 1f, input);
                        if (output.Length != 1 || output[0].Length != width * height * 4)
                            throw new InvalidOperationException("Package CPU program returned an invalid frame");
                        var pixels = new Color32[width * height];
                        for (int i = 0; i < pixels.Length; i++)
                        {
                            if ((i & 16383) == 0) cancellation.ThrowIfCancellationRequested();
                            pixels[i] = new Color32(output[0][4*i], output[0][4*i+1], output[0][4*i+2], output[0][4*i+3]);
                        }
                        frames[k] = pixels;
                    }
                    finally { VectorWorkerLimits.Slots.Release(); }
                });
            }
        }
    }
}
