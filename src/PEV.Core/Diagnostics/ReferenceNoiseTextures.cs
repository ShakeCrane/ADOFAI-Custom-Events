using System;
using System.Collections.Generic;
using PEV.Loading;
using PEV.Model;
using UnityEngine;

namespace PEV.Scripting
{
    /// <summary>Internal package-owned reference textures; no script file API is exposed.</summary>
    internal static class ReferenceNoiseTextures
    {
        private static readonly Dictionary<string, Texture2D> Cache =
            new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);

        internal static Texture2D Load(EventManifest manifest, string role, out string error)
        {
            error = null;
            if (manifest == null || string.IsNullOrEmpty(manifest.SourcePath) ||
                !manifest.ImageAssets.TryGetValue(role, out var spec) || !EventLoader.IsPackageRelativePath(spec.Path))
            {
                error = "unknown texture role, package or resource path";
                return null;
            }
            string path = spec.Path;
            string key = PackageIdentity.Of(manifest) + "\n" + role + "\n" + path;
            if (Cache.TryGetValue(key, out var existing) && existing != null) return existing;
            Texture2D texture = null;
            try
            {
                byte[] bytes = EventLoader.ReadResource(manifest.SourcePath, path);
                if (bytes == null) { error = "field texture is missing: " + path; return null; }
                Color32[] pixels = NoiseTextures.DecodePng(bytes, out int width, out int height);
                if (pixels == null || width <= 0 || height <= 0)
                {
                    error = "field texture is invalid: " + path;
                    return null;
                }
                // The decoder returns PNG top-down rows; Unity's first row is bottom-up.
                for (int y = 0; spec.FlipY && y < height / 2; y++)
                {
                    int opposite = height - 1 - y;
                    for (int x = 0; x < width; x++)
                    {
                        int a = y * width + x, b = opposite * width + x;
                        var swap = pixels[a]; pixels[a] = pixels[b]; pixels[b] = swap;
                    }
                }
                texture = new Texture2D(width, height, TextureFormat.RGBA32, false, spec.Linear)
                {
                    name = "PEV.Field." + role,
                    wrapMode = spec.Wrap == "mirror" ? TextureWrapMode.Mirror : spec.Wrap == "repeat" ? TextureWrapMode.Repeat : TextureWrapMode.Clamp,
                    filterMode = spec.Filter == "linear" ? FilterMode.Bilinear : FilterMode.Point
                };
                texture.SetPixels32(pixels);
                texture.Apply(false, false);
                Cache[key] = texture;
                return texture;
            }
            catch (Exception e)
            {
                if (texture != null) UnityEngine.Object.Destroy(texture);
                error = "reference texture load failed: " + e.Message;
                return null;
            }
        }

        internal static void Clear()
        {
            foreach (var texture in Cache.Values)
                if (texture != null) UnityEngine.Object.Destroy(texture);
            Cache.Clear();
        }
    }
}
