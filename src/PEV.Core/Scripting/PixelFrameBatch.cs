using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PEV.Loading;
using PEV.Model;
using UnityEngine;

namespace PEV.Scripting
{
    // Executes package-defined frame programs without interpreting their input data.
    // Unity texture creation remains the caller's main-thread responsibility.
    internal static class PixelFrameBatch
    {
        internal static void Render(EventManifest package, string programRole, Color32[][] frames,
            int width, int height, Func<int, string> makeInput, CancellationToken cancellation)
        {
            if (package == null || frames == null || makeInput == null) throw new ArgumentNullException();
            if (width < 1 || height < 1 || (long)width * height > 1048576 || frames.Length < 1 ||
                frames.Length > 128 || (long)width * height * frames.Length > 4200000)
                throw new ArgumentOutOfRangeException("Frame batch exceeds host limits");
            if (!package.VectorPrograms.TryGetValue(programRole, out var path))
                throw new InvalidOperationException("Package frame program is not declared");
            byte[] sourceBytes = EventLoader.ReadResource(package.SourcePath, path, 262144);
            if (sourceBytes == null) throw new InvalidOperationException("Package frame program is missing or too large");
            string source = new UTF8Encoding(false, true).GetString(sourceBytes);
            var resources = PixelResourceLoader.Load(package, cancellation);
            // Leave a worker slot for interactive vector work whenever possible.
            int degree = Math.Max(1, Math.Min(4, VectorWorkerLimits.Count > 1 ? VectorWorkerLimits.Count - 1 : 1));
            using (var programs = new ThreadLocal<VectorProgram>(() => new VectorProgram(source, resources, cancellation, 15)))
            {
                var parallel = new ParallelOptions { MaxDegreeOfParallelism = degree, CancellationToken = cancellation };
                Parallel.For(0, frames.Length, parallel, k =>
                {
                    VectorWorkerLimits.Slots.Wait(cancellation);
                    try
                    {
                        var output = programs.Value.Execute(width, height, 1f, makeInput(k));
                        if (output.Length != 1 || output[0].Length != width * height * 4)
                            throw new InvalidOperationException("Package frame program returned an invalid frame");
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
