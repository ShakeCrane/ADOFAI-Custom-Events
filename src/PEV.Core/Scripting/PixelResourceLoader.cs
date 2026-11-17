using System;
using System.Collections.Generic;
using System.Threading;
using PEV.Loading;
using PEV.Model;

namespace PEV.Scripting
{
    // Pure decoding and package reads: invoke on a worker, never in a frame callback.
    internal static class PixelResourceLoader
    {
        internal static IReadOnlyDictionary<string, PixelResource> Load(EventManifest manifest, CancellationToken cancellation)
        {
            if (manifest == null || manifest.PixelResources.Count > 16) throw new ArgumentException("Invalid pixel resource provider");
            var result = new Dictionary<string, PixelResource>(StringComparer.Ordinal);
            long total = 0;
            foreach (var item in manifest.PixelResources)
            {
                cancellation.ThrowIfCancellationRequested();
                byte[] bytes = EventLoader.ReadResource(manifest.SourcePath, item.Value, 8 * 1024 * 1024);
                Dimensions(bytes, out int width, out int height);
                total += (long)width * height * 4 * sizeof(float);
                if (total > 32L * 1024 * 1024) throw new ArgumentException("Package image budget exceeded");
                var pixels = NoiseTextures.DecodePng(bytes, out int decodedWidth, out int decodedHeight);
                if (pixels == null || decodedWidth != width || decodedHeight != height) throw new ArgumentException("Invalid package PNG " + item.Key);
                var rgba = new byte[pixels.Length * 4];
                for (int i = 0; i < pixels.Length; i++)
                {
                    if ((i & 16383) == 0) cancellation.ThrowIfCancellationRequested();
                    rgba[4 * i] = pixels[i].r; rgba[4 * i + 1] = pixels[i].g;
                    rgba[4 * i + 2] = pixels[i].b; rgba[4 * i + 3] = pixels[i].a;
                }
                result.Add(item.Key, new PixelResource(decodedWidth, decodedHeight, 4, rgba, cancellation));
            }
            return result;
        }
        internal static void Dimensions(byte[] bytes, out int width, out int height)
        {
            if (bytes == null || bytes.Length < 24 || bytes.Length > 8 * 1024 * 1024 ||
                bytes[12] != 73 || bytes[13] != 72 || bytes[14] != 68 || bytes[15] != 82)
                throw new ArgumentException("Invalid package image");
            long w = ((long)bytes[16] << 24) | ((long)bytes[17] << 16) | ((long)bytes[18] << 8) | bytes[19];
            long h = ((long)bytes[20] << 24) | ((long)bytes[21] << 16) | ((long)bytes[22] << 8) | bytes[23];
            if (w < 1 || h < 1 || w > 4096 || h > 4096 || w * h > 1048576)
                throw new ArgumentException("Package image dimensions exceed the limit");
            width = (int)w; height = (int)h;
        }
    }
}
