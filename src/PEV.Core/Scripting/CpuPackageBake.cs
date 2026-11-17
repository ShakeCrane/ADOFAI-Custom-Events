using System;
using System.Text;
using System.Threading;
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
            if (!manifest.TryRuntimeResource("framePlan", manifest.VectorPrograms, out var path)) throw new InvalidOperationException("Package bake plan is missing");
            byte[] bytes = EventLoader.ReadResource(manifest.SourcePath, path, 262144);
            if (bytes == null) throw new InvalidOperationException("Package bake plan is missing or too large");
            cancellation.ThrowIfCancellationRequested();
            var controller = new JsonController(new UTF8Encoding(false, true).GetString(bytes), 500);
            string input = "{\"request\":" + PublicFieldsSnapshot.Serialize(req) + "}";
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
            string request = PublicFieldsSnapshot.Serialize(req);
            if (!manifest.RuntimeBindings.TryGetValue("frameProgram", out var frameProgram))
                throw new InvalidOperationException("Package frame program binding is missing");
            PixelFrameBatch.Render(manifest, frameProgram, frames, width, height, k =>
                "{\"stage\":\"frame\",\"width\":" + width.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                ",\"height\":" + height.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                ",\"frameCount\":" + frames.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                ",\"frameIndex\":" + k.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"request\":" + request + "}", cancellation);
        }
    }
}
