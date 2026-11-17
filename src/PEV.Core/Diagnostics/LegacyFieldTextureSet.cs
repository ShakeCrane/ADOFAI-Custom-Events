using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Newtonsoft.Json.Linq;
using PEV.Loading;
using PEV.Model;
using PEV.Registry;
using UnityEngine;

namespace PEV.Scripting
{
    // Historical package inputs used only by the diagnostic reference implementation.
    internal sealed class LegacyFieldTextureSet
    {
        internal Color32[] Block, Point;
        internal int BlockWidth, BlockHeight, PointWidth, PointHeight;
        internal FieldBakeProfile Profile;
        private sealed class Definition
        {
            internal string Block, Point;
            internal Dictionary<string, float[]> Profile = new Dictionary<string, float[]>(StringComparer.Ordinal);
        }
        private static readonly ConditionalWeakTable<EventManifest, Definition> Definitions = new ConditionalWeakTable<EventManifest, Definition>();
        private static readonly Dictionary<string, LegacyFieldTextureSet> Cache = new Dictionary<string, LegacyFieldTextureSet>(StringComparer.Ordinal);
        private static Definition ReadDefinition(EventManifest manifest)
        {
            var bytes = EventLoader.ReadResource(manifest.SourcePath, "assets/legacy/reference.json", 131072);
            if (bytes == null) throw new InvalidOperationException("diagnostic reference is missing");
            var root = JObject.Parse(System.Text.Encoding.UTF8.GetString(bytes));
            var definition = new Definition { Block = root["textures"]?.Value<string>("block"), Point = root["textures"]?.Value<string>("point") };
            if (!(root["profile"] is JObject profile) || !EventLoader.IsPackageRelativePath(definition.Block) ||
                !EventLoader.IsPackageRelativePath(definition.Point)) throw new InvalidOperationException("invalid diagnostic reference");
            foreach (var property in profile.Properties())
                definition.Profile.Add(property.Name, property.Value is JArray a ? a.Select(x => x.Value<float>()).ToArray() : new[] { property.Value.Value<float>() });
            return definition;
        }
        private static Definition Data(EventManifest manifest) => Definitions.GetValue(manifest, ReadDefinition);
        internal static void ConfigureForTest(EventManifest manifest, string block, string point, Dictionary<string, float[]> profile = null)
        {
            Definitions.Add(manifest, new Definition { Block = block, Point = point,
                Profile = profile ?? new Dictionary<string, float[]>(StringComparer.Ordinal) });
        }
        internal static Dictionary<string, float[]> ProfileData(EventManifest manifest) => Data(manifest).Profile;
        internal static EventManifest ResolveProvider(EventManifest manifest)
        {
            if (manifest != null) return manifest;
            EventRegistry.TryGetById("pev.noise-field", out var result); return result;
        }
        internal static string Identity(EventManifest manifest) => PackageIdentity.Of(manifest);
        internal static LegacyFieldTextureSet Load(EventManifest manifest, out string error)
        {
            error = null;
            if (manifest == null || string.IsNullOrEmpty(manifest.SourcePath)) { error = "diagnostic package is missing"; return null; }
            string key = Identity(manifest);
            if (Cache.TryGetValue(key, out var samples)) return samples;
            try
            {
                var definition = Data(manifest);
                var block = EventLoader.ReadResource(manifest.SourcePath, definition.Block);
                var point = EventLoader.ReadResource(manifest.SourcePath, definition.Point);
                samples = new LegacyFieldTextureSet();
                samples.Block = NoiseTextures.DecodePng(block, out samples.BlockWidth, out samples.BlockHeight);
                samples.Point = NoiseTextures.DecodePng(point, out samples.PointWidth, out samples.PointHeight);
                if (samples.Block == null || samples.Point == null) throw new InvalidOperationException("invalid or missing PNG");
                if (definition.Profile.Count != 0) samples.Profile = new FieldBakeProfile(definition.Profile);
                Cache.Add(key, samples); return samples;
            }
            catch (Exception e) { error = "diagnostic reference could not be loaded: " + e.Message; return null; }
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
