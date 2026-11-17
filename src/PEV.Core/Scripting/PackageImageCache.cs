using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PEV.Loading;
using PEV.Model;
using UnityEngine;

namespace PEV.Scripting
{
    // Decodes declared package images off-thread; creates and destroys Unity textures on the owner thread.
    internal static class PackageImageCache
    {
        private sealed class Decoded { internal int Width, Height; internal Color32[] Pixels; }
        private sealed class Entry
        {
            internal readonly CancellationTokenSource Cancellation = new CancellationTokenSource();
            internal Task<Decoded> Pending;
            internal Texture2D Texture;
            internal string Error;
        }
        private static readonly Dictionary<string, Entry> Entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private static int _lastUploadFrame = -1;

        internal static bool TryGet(EventManifest manifest, string role, out Texture2D texture, out string error)
        {
            texture = null; error = null;
            if (manifest == null || string.IsNullOrEmpty(manifest.SourcePath) || role == null ||
                !manifest.ImageAssets.TryGetValue(role, out var spec) || spec == null || !EventLoader.IsPackageRelativePath(spec.Path))
            { error = "Unknown package image"; return false; }
            string key = PackageIdentity.Of(manifest) + ":" + role;
            if (!Entries.TryGetValue(key, out var entry))
            {
                entry = new Entry(); Entries.Add(key, entry);
                string source = manifest.SourcePath, path = spec.Path; bool flip = spec.FlipY;
                var cancellation = entry.Cancellation.Token;
                entry.Pending = Task.Run(async () =>
                {
                    await VectorWorkerLimits.Slots.WaitAsync(cancellation).ConfigureAwait(false);
                    try
                    {
                        cancellation.ThrowIfCancellationRequested();
                        byte[] bytes = EventLoader.ReadResource(source, path, 8 * 1024 * 1024);
                        PixelResourceLoader.Dimensions(bytes, out int width, out int height);
                        var pixels = NoiseTextures.DecodePng(bytes, out int actualWidth, out int actualHeight);
                        if (pixels == null || width != actualWidth || height != actualHeight) throw new ArgumentException("Invalid package PNG");
                        if (flip)
                            for (int y = 0; y < height / 2; y++)
                            {
                                if ((y & 31) == 0) cancellation.ThrowIfCancellationRequested();
                                int opposite = height - 1 - y;
                                for (int x = 0; x < width; x++)
                                {
                                    int a = y * width + x, b = opposite * width + x;
                                    var swap = pixels[a]; pixels[a] = pixels[b]; pixels[b] = swap;
                                }
                            }
                        cancellation.ThrowIfCancellationRequested();
                        return new Decoded { Width = width, Height = height, Pixels = pixels };
                    }
                    finally { VectorWorkerLimits.Slots.Release(); }
                });
            }
            if (entry.Texture != null) { texture = entry.Texture; return true; }
            if (entry.Error != null) { error = entry.Error; return false; }
            if (entry.Pending == null || !entry.Pending.IsCompleted) return false;
            // Bound main-thread texture creation to one image per Unity frame.
            if (_lastUploadFrame == Time.frameCount) return false;
            try
            {
                var decoded = entry.Pending.GetAwaiter().GetResult();
                _lastUploadFrame = Time.frameCount;
                var created = new Texture2D(decoded.Width, decoded.Height, TextureFormat.RGBA32, false, spec.Linear)
                {
                    name = "PEV.Image." + role,
                    wrapMode = spec.Wrap == "mirror" ? TextureWrapMode.Mirror : spec.Wrap == "repeat" ? TextureWrapMode.Repeat : TextureWrapMode.Clamp,
                    filterMode = spec.Filter == "linear" ? FilterMode.Bilinear : FilterMode.Point
                };
                try { created.SetPixels32(decoded.Pixels); created.Apply(false, false); }
                catch { UnityEngine.Object.Destroy(created); throw; }
                entry.Texture = created; texture = created;
                entry.Pending = null;
                return true;
            }
            catch (Exception e) { entry.Error = error = "Package image load failed: " + e.Message; entry.Pending = null; return false; }
        }

        internal static void Clear()
        {
            foreach (var entry in Entries.Values)
            {
                entry.Cancellation.Cancel();
                if (entry.Texture != null) UnityEngine.Object.Destroy(entry.Texture);
                var cancellation = entry.Cancellation;
                if (entry.Pending != null)
                    entry.Pending.ContinueWith(task => { var ignored = task.Exception; cancellation.Dispose(); }, TaskContinuationOptions.ExecuteSynchronously);
                else cancellation.Dispose();
            }
            Entries.Clear();
            _lastUploadFrame = -1;
        }
    }
}
