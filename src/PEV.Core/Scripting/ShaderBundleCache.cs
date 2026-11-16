using System;
using System.Collections.Generic;
using PEV.Loading;
using PEV.Model;
using UnityEngine;

namespace PEV.Scripting
{
    /// <summary>Internal host-side bundle loader. Nothing is exposed to event scripts.</summary>
    internal static class ShaderBundleCache
    {
        private static readonly Dictionary<string, AssetBundle> Bundles =
            new Dictionary<string, AssetBundle>(StringComparer.OrdinalIgnoreCase);

        // Called on Unity's main thread. The caller owns materials created from the shader;
        // release those before Clear unloads the cached bundles.
        internal static Shader Load(EventManifest manifest, string assetName, out string error)
        {
            var shader = LoadAsset<Shader>(manifest, assetName, out error);
            if (shader != null && !shader.isSupported)
            {
                error = $"shader '{assetName}' is not supported by this graphics device";
                return null;
            }
            return shader;
        }

        internal static ComputeShader LoadCompute(EventManifest manifest, string assetName, out string error)
        {
            if (!SystemInfo.supportsComputeShaders)
            {
                error = "compute shaders are not supported by this graphics device";
                return null;
            }
            return LoadAsset<ComputeShader>(manifest, assetName, out error);
        }

        private static T LoadAsset<T>(EventManifest manifest, string assetName, out string error)
            where T : UnityEngine.Object
        {
            error = null;
            if (manifest == null || string.IsNullOrWhiteSpace(manifest.SourcePath) ||
                string.IsNullOrWhiteSpace(manifest.ShaderBundlePath) || string.IsNullOrWhiteSpace(assetName))
            {
                error = "shader bundle, package or asset name is missing";
                return null;
            }
            string key = manifest.SourcePath + "\n" + manifest.ShaderBundlePath;
            try
            {
                if (!Bundles.TryGetValue(key, out var bundle) || bundle == null)
                {
                    byte[] bytes = EventLoader.ReadResource(manifest.SourcePath, manifest.ShaderBundlePath);
                    if (bytes == null || bytes.Length == 0)
                    {
                        error = $"bundle '{manifest.ShaderBundlePath}' is missing or empty";
                        return null;
                    }
                    bundle = AssetBundle.LoadFromMemory(bytes);
                    if (bundle == null)
                    {
                        error = $"bundle '{manifest.ShaderBundlePath}' could not be loaded";
                        return null;
                    }
                    Bundles[key] = bundle;
                }
                T asset = bundle.LoadAsset<T>(assetName);
                if (asset == null) error = $"asset '{assetName}' is missing from '{manifest.ShaderBundlePath}'";
                return asset;
            }
            catch (Exception e)
            {
                error = "shader bundle load failed: " + e.Message;
                return null;
            }
        }

        internal static void Clear()
        {
            foreach (var bundle in Bundles.Values)
                if (bundle != null) bundle.Unload(true);
            Bundles.Clear();
        }
    }
}
