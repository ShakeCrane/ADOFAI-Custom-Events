using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;
using PEV.Loading;
using PEV.Model;
using UnityEngine;

namespace PEV.Scripting
{
    /// <summary>
    /// Experimental GPU groups. A replacement is staged behind the previous frame until
    /// every changed group has a palette and its renderers have entered Unity's culling list.
    /// </summary>
    internal sealed class NoiseFieldGpuCoordinator : IDisposable
    {
        private sealed class Group : IDisposable
        {
            internal readonly PackageComputeFrame Masks;
            internal readonly NoiseFieldGpuLayer Layer;
            internal readonly NoiseFieldGpuPalette Palette;
            internal readonly Vector4[] TouchPositions;
            internal NoiseFieldColorOwnership.Zone[] Colors = Array.Empty<NoiseFieldColorOwnership.Zone>();
            internal int ViewWidth, ViewHeight;
            internal bool Colored, ShowDisabled, ShowActive;

            internal Group(ComputeShader shader, GpuGraphDefinition graph, CompositorDefinition compositor, VectorTextureDefinition textures,
                GpuGraphDefinition.ControllerLimits limits, EventManifest manifest, NoiseFieldGpuPlan.Key key)
            {
                TouchPositions = new Vector4[limits.Capacity];
                Palette = new NoiseFieldGpuPalette(manifest, textures);
                Masks = new PackageComputeFrame(shader, graph);
                try { Layer = new NoiseFieldGpuLayer(manifest, compositor, key.SortingLayer, key.SortingOrder, key.UnityLayer); }
                catch { Masks.Dispose(); throw; }
            }
            public void Dispose() { Layer.Dispose(); Palette.Dispose(); Masks.Dispose(); }
        }

        private readonly EventManifest _manifest;
        private readonly ComputeShader _compute;
        private readonly GpuGraphDefinition _graph;
        private readonly CompositorDefinition _compositor;
        private readonly VectorTextureDefinition _textures;
        private readonly GpuGraphDefinition.ControllerLimits _inputLimits;
        private readonly string _groupProgram;
        private readonly Dictionary<string, Texture2D> _images = new Dictionary<string, Texture2D>(StringComparer.Ordinal);
        private readonly NoiseFieldGpuPlan _plan;
        private readonly ResourceGroupStaging<NoiseFieldGpuPlan.Key, Group> _staging =
            new ResourceGroupStaging<NoiseFieldGpuPlan.Key, Group>();
        private readonly List<ViewportPoint> _planets = new List<ViewportPoint>();
        private readonly Dictionary<NoiseFieldGpuPlan.Key, GroupScriptRunner> _runners = new Dictionary<NoiseFieldGpuPlan.Key, GroupScriptRunner>();
        private readonly List<NoiseFieldGpuPlan.Key> _staleRunners = new List<NoiseFieldGpuPlan.Key>();

        internal NoiseFieldGpuCoordinator(EventManifest manifest)
        {
            _manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
            if (!manifest.TryRuntimeResource("renderGraph", manifest.ComputeGraphs, out var graphPath))
                throw new InvalidOperationException("Field compute graph is not declared");
            var graphBytes = EventLoader.ReadResource(manifest.SourcePath, graphPath, 131072);
            _graph = GpuGraphDefinition.Parse(graphBytes);
            if (!_graph.Controllers.TryGetValue("input", out _inputLimits))
                throw new InvalidOperationException("Input controller limits are not declared");
            _plan = NoiseFieldGpuPlan.Create(manifest, graphBytes);
            _compositor = CompositorDefinition.Parse(graphBytes);
            _textures = VectorTextureDefinition.Parse(graphBytes, _graph);
            if (!manifest.ShaderAssets.TryGetValue(_graph.ShaderRole, out var computePath))
                throw new InvalidOperationException("Compute shader role is not declared");
            _compute = ShaderBundleCache.LoadCompute(manifest, computePath, out string error);
            if (_compute == null) throw new InvalidOperationException(error);
            if (!manifest.TryRuntimeResource("inputController", manifest.VectorPrograms, out var contactPath))
                throw new InvalidOperationException("Field contact controller is not declared");
            var contactBytes = EventLoader.ReadResource(manifest.SourcePath, contactPath, 262144);
            if (contactBytes == null) throw new InvalidOperationException("Field contact controller is missing");
            if (!manifest.TryRuntimeResource("renderGroup", manifest.VectorPrograms, out var groupPath))
                throw new InvalidOperationException("Package group program is not declared");
            var groupBytes = EventLoader.ReadResource(manifest.SourcePath, groupPath, 262144);
            if (groupBytes == null) throw new InvalidOperationException("Package group program is missing");
            _groupProgram = new UTF8Encoding(false, true).GetString(contactBytes) + "\n" + new UTF8Encoding(false, true).GetString(groupBytes);
            if (_groupProgram.Length > 262144) throw new InvalidOperationException("Package group program is too large");
        }

        private bool EnsureImages()
        {
            bool ready = true;
            foreach (var item in _manifest.ImageAssets)
            {
                if (_images.TryGetValue(item.Key, out var existing) && existing != null) continue;
                bool loaded = PackageImageCache.TryGet(_manifest, item.Key, out var texture, out string error);
                if (error != null) throw new InvalidOperationException(error);
                if (loaded) _images[item.Key] = texture; else ready = false;
            }
            return ready;
        }

        internal bool Render(IReadOnlyList<NoiseFieldRenderer.Entry> entries, Camera camera, float songTime,
            IReadOnlyDictionary<string, bool> settings, Color fallbackBackground)
        {
            if (camera == null || !camera.orthographic) throw new NotSupportedException("An orthographic camera is required");
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (!EnsureImages()) return _staging.Committed;
            float aspect = Mathf.Min(camera.aspect, 16f / 9f);
            if (!_plan.Build(entries, camera, aspect, GameBridge.Values)) return _staging.Committed;
            LivePlanetPositions.Collect(camera, _planets);
            _staging.DiscardPendingExcept(_plan.Groups);
            _staleRunners.Clear();
            foreach (var pair in _runners) if (!_plan.Groups.ContainsKey(pair.Key)) _staleRunners.Add(pair.Key);
            foreach (var key in _staleRunners) { _runners[key].Dispose(); _runners.Remove(key); }

            bool ready = true;
            foreach (var pair in _plan.Groups)
            {
                var plan = pair.Value;
                _staging.Pending.TryGetValue(pair.Key, out var pending);
                _staging.Active.TryGetValue(pair.Key, out var previous);
                var group = pending ?? previous;
                _graph.Size(camera.pixelWidth, camera.pixelHeight, out int width, out int height);
                if (plan.Count > _inputLimits.MaxInputs || _planets.Count > _inputLimits.MaxInputs)
                    throw new ArgumentException("Group input exceeds package limits");
                if (!_runners.TryGetValue(pair.Key, out var runner))
                    _runners.Add(pair.Key, runner = new GroupScriptRunner(_groupProgram));
                if (!runner.Update(plan.Data, songTime, aspect, _inputLimits.Capacity, camera.pixelWidth, camera.pixelHeight,
                    _manifest.DecorationDefaultColor, _planets, settings, plan.Count, out var commands))
                {
                    if (pending != null) pending.Layer.Hide();
                    ready = false;
                    continue;
                }
                if (!(commands["conditions"] is JObject conditions) || !(commands["buffers"] is JObject buffers) ||
                    !(commands["values"] is JObject values) || !(commands["compute"] is JObject compute))
                    throw new ArgumentException("Invalid package group commands");
                bool showActive = Flag(conditions, "showPrimary"), showDisabled = Flag(conditions, "showSecondary");
                bool colored = Flag(conditions, "colored"), hover = Flag(conditions, "hover");
                var geometry = Rows(buffers["geometry"], 1024, 8);
                var touches = Rows(buffers["contacts"], _inputLimits.Capacity, 4);
                var positions = Rows(values["contacts"], _inputLimits.Capacity, 4);
                if (Integer(compute["geometryCount"], 0, 1024) != geometry.Count ||
                    Integer(compute["contactCount"], 0, _inputLimits.Capacity) != touches.Count ||
                    Integer(values["contactCount"], 0, _inputLimits.Capacity) != positions.Count ||
                    Flag(compute, "hasContacts") != (touches.Count > 0) || Flag(conditions, "hasContacts") != (positions.Count > 0))
                    throw new ArgumentException("Inconsistent package group commands");
                if (group == null)
                    _staging.SetPending(pair.Key, group = new Group(_compute, _graph, _compositor, _textures, _inputLimits, _manifest, pair.Key)
                    {
                        ViewWidth = width, ViewHeight = height, Colored = colored,
                        ShowDisabled = showDisabled, ShowActive = showActive
                    });
                bool matches = group.ViewWidth == width && group.ViewHeight == height &&
                    group.Colored == colored && group.ShowDisabled == showDisabled && group.ShowActive == showActive;
                if (!matches)
                {
                    var replacement = new Group(_compute, _graph, _compositor, _textures, _inputLimits, _manifest, pair.Key)
                    {
                        ViewWidth = width, ViewHeight = height, Colored = colored,
                        ShowDisabled = showDisabled, ShowActive = showActive
                    };
                    _staging.SetPending(pair.Key, replacement);
                    group = replacement;
                }
                for (int i = 0; i < positions.Count; i++)
                {
                    var row = (JArray)positions[i];
                    group.TouchPositions[i] = new Vector4(Number(row[0]), Number(row[1]), Number(row[2]), Number(row[3]));
                }
                group.Masks.Render(camera.pixelWidth, camera.pixelHeight, songTime, buffers, compute, _images);
                if (colored)
                {
                    if (!(commands["vector"] is JObject vector) || !(vector["Items"] is JArray colors) || colors.Count > 1024)
                        throw new ArgumentException("Invalid package vector input");
                    if (group.Colors.Length != colors.Count) Array.Resize(ref group.Colors, colors.Count);
                    for (int i = 0; i < colors.Count; i++) group.Colors[i] = ColorRecord(colors[i]);
                    int defaultRgb = Integer(vector["Options"], 0, 0xffffff);
                    if (!group.Palette.Update(group.Masks, aspect, group.Colors, defaultRgb))
                    {
                        // Do not silently render a colored group as default red while its
                        // first palette or resized palette is still being calculated.
                        group.Layer.Hide();
                        ready = false;
                        continue;
                    }
                }
                ready &= group.Layer.Draw(camera, group.Masks, _images, aspect, songTime,
                    settings, hover, showDisabled, showActive,
                    fallbackBackground, group.TouchPositions, positions.Count,
                    colored ? group.Palette.Active : null, colored ? group.Palette.Disabled : null);
            }
            if (!ready)
            {
                // The existing GPU groups stay visible, or the legacy renderer remains
                // visible before the first commit. Pending groups must not double-blend.
                foreach (var group in _staging.Pending.Values) group.Layer.Hide();
                return _staging.Committed;
            }
            _staging.Commit(_plan.Groups);
            return true;
        }

        private static bool Flag(JObject source, string name)
        {
            if (source[name]?.Type != JTokenType.Boolean) throw new ArgumentException("Invalid group command flag");
            return source.Value<bool>(name);
        }
        private static int Integer(JToken token, int min, int max)
        {
            if (token?.Type != JTokenType.Integer) throw new ArgumentException("Invalid group command integer");
            long value = token.Value<long>();
            if (value < min || value > max) throw new ArgumentOutOfRangeException("Group command integer");
            return (int)value;
        }
        private static float Number(JToken token)
        {
            if (token == null || (token.Type != JTokenType.Integer && token.Type != JTokenType.Float))
                throw new ArgumentException("Invalid group command number");
            float value = token.Value<float>();
            if (float.IsNaN(value) || float.IsInfinity(value) || Math.Abs(value) > 1000000f)
                throw new ArgumentOutOfRangeException("Group command number");
            return value;
        }
        private static JArray Rows(JToken token, int max, int width)
        {
            if (!(token is JArray rows) || rows.Count > max) throw new ArgumentException("Invalid group command rows");
            foreach (var item in rows) if (!(item is JArray row) || row.Count != width)
                throw new ArgumentException("Invalid group command row");
            return rows;
        }
        private static NoiseFieldColorOwnership.Zone ColorRecord(JToken token)
        {
            if (!(token is JObject item)) throw new ArgumentException("Invalid group vector record");
            return new NoiseFieldColorOwnership.Zone { CenterX = Number(item["CenterX"]), CenterY = Number(item["CenterY"]),
                HalfX = Number(item["HalfX"]), HalfY = Number(item["HalfY"]), Angle = Number(item["Angle"]),
                YScale = Number(item["YScale"]), Opacity = Number(item["Opacity"]), Red = Number(item["Red"]),
                Green = Number(item["Green"]), Blue = Number(item["Blue"]), Active = Flag(item, "Active"), Inverted = Flag(item, "Inverted") };
        }

#if PEV_DIAGNOSTICS
        internal bool Render(IReadOnlyList<NoiseFieldRenderer.Entry> entries, Camera camera, float songTime,
            bool captureScene, Color fallbackBackground) =>
            Render(entries, camera, songTime, new Dictionary<string, bool> { ["captureScene"] = captureScene }, fallbackBackground);
#endif

        public void Dispose()
        {
            _plan.Dispose();
            foreach (var runner in _runners.Values) runner.Dispose();
            _runners.Clear();
            _staging.Dispose();
        }
    }
}
