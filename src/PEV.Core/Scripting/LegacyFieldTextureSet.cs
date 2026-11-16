using System;
using System.Collections.Generic;
using PEV.Loading;
using PEV.Model;
using PEV.Registry;
using UnityEngine;

namespace PEV.Scripting
{
    /// <summary>Immutable CPU texture samples loaded from an event's declared resources.</summary>
    internal sealed class LegacyFieldTextureSet
    {
        internal Color32[] Block, Point;
        internal int BlockWidth, BlockHeight, PointWidth, PointHeight;
        internal FieldBakeProfile Profile;
        private static readonly Dictionary<string, LegacyFieldTextureSet> Cache =
            new Dictionary<string, LegacyFieldTextureSet>(StringComparer.Ordinal);
        private sealed class IdentityHolder { internal string Value; }
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<EventManifest, IdentityHolder> Identities =
            new System.Runtime.CompilerServices.ConditionalWeakTable<EventManifest, IdentityHolder>();
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<EventManifest, IdentityHolder>.CreateValueCallback CreateIdentity =
            manifest => new IdentityHolder { Value = ComputeIdentity(manifest,
                manifest.LegacyFieldTextures["block"], manifest.LegacyFieldTextures["point"]) };

        internal static EventManifest ResolveProvider(EventManifest manifest)
        {
            if (manifest != null) return manifest;
            EventManifest found = null;
            foreach (var candidate in EventRegistry.All)
            {
                if (candidate.LegacyFieldTextures.Count == 0) continue;
                if (found != null) return null;
                found = candidate;
            }
            return found;
        }

        internal static string Identity(EventManifest manifest)
        {
            if (manifest == null || !manifest.LegacyFieldTextures.ContainsKey("block") ||
                !manifest.LegacyFieldTextures.ContainsKey("point")) return "";
            return Identities.GetValue(manifest, CreateIdentity).Value;
        }

        private static string ComputeIdentity(EventManifest manifest, string block, string point)
        {
            string source = manifest.SourcePath ?? "";
            string version = manifest.Version ?? "";
            string identity = source.Length + ":" + source + version.Length + ":" + version +
                block.Length + ":" + block + point.Length + ":" + point;
            // A package can be reloaded with the same version and different profile data.
            // Its baked pixels must never reuse the previous profile's cache entry.
            var keys = new List<string>(manifest.LegacyFieldProfile.Keys);
            keys.Sort(StringComparer.Ordinal);
            var profile = new System.Text.StringBuilder(identity);
            foreach (var key in keys)
            {
                profile.Append(key.Length).Append(':').Append(key);
                foreach (float value in manifest.LegacyFieldProfile[key])
                    profile.Append(':').Append(value.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            }
            using (var sha = System.Security.Cryptography.SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(profile.ToString()))).Replace("-", "");
        }

        internal static LegacyFieldTextureSet Load(EventManifest manifest, out string error)
        {
            error = null;
            string key = Identity(manifest);
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(manifest?.SourcePath))
            { error = "field package does not declare legacy textures; update the event package"; return null; }
            if (Cache.TryGetValue(key, out var samples)) return samples;
            try
            {
                var block = EventLoader.ReadResource(manifest.SourcePath, manifest.LegacyFieldTextures["block"]);
                var point = EventLoader.ReadResource(manifest.SourcePath, manifest.LegacyFieldTextures["point"]);
                samples = new LegacyFieldTextureSet();
                samples.Block = NoiseTextures.DecodePng(block, out samples.BlockWidth, out samples.BlockHeight);
                samples.Point = NoiseTextures.DecodePng(point, out samples.PointWidth, out samples.PointHeight);
                if (samples.Block == null || samples.Point == null) throw new InvalidOperationException("invalid or missing PNG");
                if (manifest.LegacyFieldProfile.Count != 0) samples.Profile = new FieldBakeProfile(manifest.LegacyFieldProfile);
                Cache.Add(key, samples);
                return samples;
            }
            catch (Exception e) { error = "field package textures could not be loaded: " + e.Message; return null; }
        }

        internal static void Clear() => Cache.Clear();
    }

    /// <summary>Typed, immutable view of a package's validated CPU field profile.</summary>
    internal sealed class FieldBakeProfile
    {
        internal readonly float ComposeScale, ComposeDisplaceSpeed, ComposeDisplaceStrength, DisplaceBlendIntensity;
        internal readonly float ActiveSparkDisplaceIntensity, ActiveSparkHueShiftAmount, FillStrength;
        internal readonly float ActiveDisplaceSpeed, ActiveDisplaceStrength, SparkMapOpacity, FillValue, GlowSaturation, SparkSaturation, GrainPixels;
        internal readonly Vector2 ComposeDisplaceDirection, ComposeDisplaceMapScale, ActiveDisplaceDirection, ActiveDisplaceMapScale, ActiveSparkMapScale;
        internal readonly float[] GlowWeights;

        internal FieldBakeProfile(Dictionary<string, float[]> data)
        {
            float Scalar(string key) => data[key][0];
            Vector2 Vec(string key) => new Vector2(data[key][0], data[key][1]);
            ComposeScale = Scalar("composeScale"); ComposeDisplaceSpeed = Scalar("composeDisplaceSpeed");
            ComposeDisplaceStrength = Scalar("composeDisplaceStrength"); DisplaceBlendIntensity = Scalar("displaceBlendIntensity");
            ActiveSparkDisplaceIntensity = Scalar("activeSparkDisplaceIntensity"); ActiveSparkHueShiftAmount = Scalar("activeSparkHueShiftAmount");
            FillStrength = Scalar("fillStrength"); ActiveDisplaceSpeed = Scalar("activeDisplaceSpeed");
            ActiveDisplaceStrength = Scalar("activeDisplaceStrength"); SparkMapOpacity = Scalar("sparkMapOpacity");
            FillValue = Scalar("fillValue"); GlowSaturation = Scalar("glowSaturation"); SparkSaturation = Scalar("sparkSaturation");
            GrainPixels = Scalar("grainPixels");
            ComposeDisplaceDirection = Vec("composeDisplaceDirection"); ComposeDisplaceMapScale = Vec("composeDisplaceMapScale");
            ActiveDisplaceDirection = Vec("activeDisplaceDirection"); ActiveDisplaceMapScale = Vec("activeDisplaceMapScale");
            ActiveSparkMapScale = Vec("activeSparkMapScale"); GlowWeights = (float[])data["glowWeights"].Clone();
        }
    }
}
