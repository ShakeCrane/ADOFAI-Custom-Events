using System;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PEV.Loading;
using UnityEngine;

namespace PEV.Scripting
{
    // Transitional adapter from the legacy field request to a package-owned vector program.
    // All decoding, script execution and pixel conversion run on background workers.
    internal static class CpuPackageBake
    {
        internal struct Plan { internal int Width, Height, Frames; }
        internal static Plan Prepare(NoiseTextureFactory.Request req, CancellationToken cancellation)
        {
            var manifest = req.Provider ?? throw new ArgumentException("Missing package provider");
            if (!manifest.VectorPrograms.TryGetValue("bakePlan", out var path)) throw new InvalidOperationException("Package bake plan is missing");
            byte[] bytes = EventLoader.ReadResource(manifest.SourcePath, path, 262144);
            if (bytes == null) throw new InvalidOperationException("Package bake plan is missing or too large");
            cancellation.ThrowIfCancellationRequested();
            var controller = new JsonController(new UTF8Encoding(false, true).GetString(bytes), 500);
            string input = JsonConvert.SerializeObject(new { widthWorld = req.WidthWorld, heightWorld = req.HeightWorld,
                margin = req.Margin, targetPixelWidth = req.TargetPixelWidth, frameCount = req.FrameCount, sampleRate = req.SampleRate });
            var result = controller.Update(input);
            var plan = ValidatePlan(result);
            cancellation.ThrowIfCancellationRequested();
            return plan;
        }
        internal static Plan ValidatePlan(JObject result)
        {
            if (result == null) throw new ArgumentException("Invalid package bake plan");
            int Integer(string key, int max)
            {
                if (result[key]?.Type != JTokenType.Integer) throw new ArgumentException("Invalid package bake plan");
                long n = result.Value<long>(key);
                if (n < 1 || n > max) throw new ArgumentException("Package bake plan exceeds host limits");
                return (int)n;
            }
            var plan = new Plan { Width = Integer("width", 4096), Height = Integer("height", 4096), Frames = Integer("frames", 128) };
            if (plan.Frames < 2 || (long)plan.Width * plan.Height > 1048576 ||
                (long)plan.Width * plan.Height * plan.Frames > 4200000)
                throw new ArgumentException("Package bake plan exceeds host limits");
            return plan;
        }
        internal static void Render(NoiseTextureFactory.Request req, Color32[][] frames, int width, int height, CancellationToken cancellation)
        {
            var manifest = req.Provider ?? throw new ArgumentException("Missing package provider");
            var pieces = (req.Pieces ?? Array.Empty<NoiseTextureFactory.Piece>()).Select(p => new
            {
                x = p.Offset.x, y = p.Offset.y, hx = p.Half.x, hy = p.Half.y,
                rotation = p.Rotation, circle = p.Circle
            }).ToArray();
            var request = new
            {
                req.WidthWorld, req.HeightWorld, req.Margin, req.EvolveSpeed, req.ScreenPixelWidth, req.Seed,
                EdgeColor = new[] { req.EdgeColor.r, req.EdgeColor.g, req.EdgeColor.b },
                SceneColor = new[] { req.SceneColor.r, req.SceneColor.g, req.SceneColor.b },
                req.FillOpacity, req.FillStrength, req.Exposure, req.EdgeOpacity, req.GlowOpacity, req.SparkOpacity, req.Glitch,
                pieces
            };
            PixelFrameBatch.Render(manifest, "bake", frames, width, height, k => JsonConvert.SerializeObject(new
            {
                stage = "frame", width, height, frameCount = frames.Length, frameIndex = k, request
            }), cancellation);
        }
    }
}
