using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using PEV.Loading;
using PEV.Model;
using UnityEngine;

namespace PEV.Scripting
{
    /// <summary>Internal host-side bundle loader. Nothing is exposed to event scripts.</summary>
    internal static class ShaderBundleCache
    {
        private static readonly Dictionary<string, AssetBundle> Bundles =
            new Dictionary<string, AssetBundle>(StringComparer.Ordinal);
        private sealed class Identity { internal string Hash; }
        private static ConditionalWeakTable<EventManifest, Identity> Identities = new ConditionalWeakTable<EventManifest, Identity>();

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
            try
            {
                if (!Identities.TryGetValue(manifest, out var identity))
                {
                    byte[] bytes = EventLoader.ReadResource(manifest.SourcePath, manifest.ShaderBundlePath, 64 * 1024 * 1024);
                    if (bytes == null || bytes.Length == 0)
                    {
                        error = $"bundle '{manifest.ShaderBundlePath}' is missing or empty";
                        return null;
                    }
                    string hash;
                    using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(bytes));
                    if (!Bundles.TryGetValue(hash, out var cached) || cached == null)
                    {
                        cached = AssetBundle.LoadFromMemory(bytes);
                        if (cached == null)
                        {
                            error = $"bundle '{manifest.ShaderBundlePath}' could not be loaded";
                            return null;
                        }
                        Bundles[hash] = cached;
                    }
                    identity = new Identity { Hash = hash }; Identities.Add(manifest, identity);
                }
                var bundle = Bundles[identity.Hash];
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
            Identities = new ConditionalWeakTable<EventManifest, Identity>();
        }
    }
}
