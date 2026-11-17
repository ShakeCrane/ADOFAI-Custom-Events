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
    /// every changed group has its textures and renderers in Unity's culling list.
    /// </summary>
    internal sealed class NoiseFieldGpuCoordinator : IDisposable
    {
        private sealed class Group : IDisposable
        {
            internal readonly PackageComputeFrame Masks;
            internal readonly PackageSpriteLayer Layer;
            internal readonly JsonVectorTextureSet Vectors;
            internal readonly Dictionary<string, Texture> Textures = new Dictionary<string, Texture>(StringComparer.Ordinal);
            internal int ViewWidth, ViewHeight;
            internal int Layout;

            internal Group(ComputeShader shader, GpuGraphDefinition graph, CompositorDefinition compositor, VectorTextureDefinition textures,
                GpuGraphDefinition.ControllerLimits limits, EventManifest manifest, NoiseFieldGpuPlan.Key key, string vectorSource)
            {
                Vectors = new JsonVectorTextureSet(vectorSource, textures);
                try
                {
                    Masks = new PackageComputeFrame(shader, graph);
                    Layer = new PackageSpriteLayer(manifest, compositor, key.SortingLayer, key.SortingOrder, key.UnityLayer, limits.Capacity);
                }
                catch { Masks?.Dispose(); Vectors.Dispose(); throw; }
            }
            public void Dispose() { Layer.Dispose(); Vectors.Dispose(); Masks.Dispose(); }
        }

        private readonly EventManifest _manifest;
        private readonly ComputeShader _compute;
        private readonly GpuGraphDefinition _graph;
        private readonly CompositorDefinition _compositor;
        private readonly VectorTextureDefinition _textures;
        private readonly GpuGraphDefinition.ControllerLimits _inputLimits;
        private readonly string _groupProgram;
        private readonly string _vectorProgram;
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
            if (!manifest.TryRuntimeResource("vectorProgram", manifest.VectorPrograms, out var vectorPath))
                throw new InvalidOperationException("Package vector program is not declared");
            var vectorBytes = EventLoader.ReadResource(manifest.SourcePath, vectorPath, 262144);
            if (vectorBytes == null) throw new InvalidOperationException("Package vector program is missing");
            _vectorProgram = new UTF8Encoding(false, true).GetString(vectorBytes);
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
            float aspect = _graph.AspectLimit > 0 ? Mathf.Min(camera.aspect, _graph.AspectLimit) : camera.aspect;
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
                var vector = commands["vector"];
                int layout = vector != null && vector.Type != JTokenType.Null ? 1 : 0;
                foreach (var layer in _compositor.Layers)
                {
                    if (conditions[layer.Visible]?.Type != JTokenType.Boolean) throw new ArgumentException("Missing compositor visibility");
                    layout = layout * 2 + (conditions.Value<bool>(layer.Visible) ? 1 : 0);
                }
                if (group == null)
                    _staging.SetPending(pair.Key, group = new Group(_compute, _graph, _compositor, _textures, _inputLimits, _manifest, pair.Key, _vectorProgram)
                    {
                        ViewWidth = width, ViewHeight = height, Layout = layout
                    });
                bool matches = group.ViewWidth == width && group.ViewHeight == height && group.Layout == layout;
                if (!matches)
                {
                    var replacement = new Group(_compute, _graph, _compositor, _textures, _inputLimits, _manifest, pair.Key, _vectorProgram)
                    {
                        ViewWidth = width, ViewHeight = height, Layout = layout
                    };
                    _staging.SetPending(pair.Key, replacement);
                    group = replacement;
                }
                group.Masks.Render(camera.pixelWidth, camera.pixelHeight, songTime, buffers, compute, _images);
                if (vector != null && vector.Type != JTokenType.Null)
                {
                    if (!group.Vectors.Update(group.Masks.Width, group.Masks.Height, aspect, vector, group.Masks.RunExternal))
                    {
                        // Keep the previous group until required textures are ready.
                        group.Layer.Hide();
                        ready = false;
                        continue;
                    }
                }
                group.Textures.Clear();
                if (vector != null && vector.Type != JTokenType.Null)
                    foreach (var role in _textures.Roles) group.Textures.Add(role.Key, group.Vectors.Get(role.Value));
                ready &= group.Layer.Draw(camera, group.Masks, _images, aspect, songTime,
                    settings, conditions, values, fallbackBackground, group.Textures);
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
