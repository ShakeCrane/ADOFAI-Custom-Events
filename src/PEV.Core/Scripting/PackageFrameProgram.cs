using System;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using PEV.Loading;
using PEV.Model;
using UnityEngine;

namespace PEV.Scripting
{
    // Runs a package-defined plan and frame program over a plain value snapshot.
    // All decoding, script execution and pixel conversion run on background workers.
    internal static class PackageFrameProgram
    {
        internal struct Plan { internal int Width, Height, Frames; }
        internal static Plan Prepare<T>(EventManifest manifest, T request, string planBinding, CancellationToken cancellation) where T : struct
        {
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));
            if (!manifest.TryRuntimeResource(planBinding, manifest.VectorPrograms, out var path)) throw new InvalidOperationException("Package frame plan is missing");
            byte[] bytes = EventLoader.ReadResource(manifest.SourcePath, path, 262144);
            if (bytes == null) throw new InvalidOperationException("Package bake plan is missing or too large");
            cancellation.ThrowIfCancellationRequested();
            var controller = new JsonController(new UTF8Encoding(false, true).GetString(bytes), 500);
            string input = "{\"request\":" + PublicFieldsSnapshot.Serialize(request) + "}";
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
            if ((long)plan.Width * plan.Height > 1048576 ||
                (long)plan.Width * plan.Height * plan.Frames > 4200000)
                throw new ArgumentException("Package bake plan exceeds host limits");
            return plan;
        }
        internal static void Render<T>(EventManifest manifest, T snapshot, string programBinding, Color32[][] frames,
            int width, int height, CancellationToken cancellation) where T : struct
        {
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));
            string request = PublicFieldsSnapshot.Serialize(snapshot);
            if (!manifest.RuntimeBindings.TryGetValue(programBinding, out var frameProgram))
                throw new InvalidOperationException("Package frame program binding is missing");
            PixelFrameBatch.Render(manifest, frameProgram, frames, width, height, k =>
                "{\"stage\":\"frame\",\"width\":" + width.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                ",\"height\":" + height.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                ",\"frameCount\":" + frames.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                ",\"frameIndex\":" + k.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"request\":" + request + "}", cancellation);
        }
    }
}
