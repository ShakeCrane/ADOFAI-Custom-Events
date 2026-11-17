using System;
using System.Collections.Generic;
using System.Text;
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
            internal readonly NoiseFieldGpuMasks Masks;
            internal readonly NoiseFieldGpuLayer Layer;
            internal readonly NoiseFieldGpuPalette Palette;
            internal readonly FieldContactController History;
            internal readonly NoiseFieldGpuMasks.Touch[] Touches = new NoiseFieldGpuMasks.Touch[FieldContactController.Capacity];
            internal readonly Vector4[] TouchPositions = new Vector4[FieldContactController.Capacity];
            internal NoiseFieldGpuMasks.Area[] Areas = new NoiseFieldGpuMasks.Area[4];
            internal NoiseFieldColorOwnership.Zone[] Colors = Array.Empty<NoiseFieldColorOwnership.Zone>();
            internal int ViewWidth, ViewHeight;
            internal bool Colored, ShowDisabled, ShowActive;

            internal Group(ComputeShader shader, GpuGraphDefinition graph, CompositorDefinition compositor, EventManifest manifest, NoiseFieldGpuPlan.Key key,
                string contactProgram, FieldContactController history = null)
            {
                History = history ?? new FieldContactController(contactProgram);
                Palette = new NoiseFieldGpuPalette(manifest);
                Masks = new NoiseFieldGpuMasks(shader, graph);
                try { Layer = new NoiseFieldGpuLayer(manifest, compositor, key.SortingLayer, key.SortingOrder, key.UnityLayer); }
                catch { Masks.Dispose(); throw; }
            }
            public void Dispose() { Layer.Dispose(); Palette.Dispose(); Masks.Dispose(); }
        }

        private readonly EventManifest _manifest;
        private readonly ComputeShader _compute;
        private readonly GpuGraphDefinition _graph;
        private readonly TaggedValueTable _modes;
        private readonly CompositorDefinition _compositor;
        private readonly string _contactProgram;
        private readonly Dictionary<string, Texture2D> _images = new Dictionary<string, Texture2D>(StringComparer.Ordinal);
        private readonly NoiseFieldGpuPlan _plan = new NoiseFieldGpuPlan();
        private readonly NoiseFieldGroupStaging<NoiseFieldGpuPlan.Key, Group> _staging =
            new NoiseFieldGroupStaging<NoiseFieldGpuPlan.Key, Group>();
        private readonly List<FieldContactController.Contact> _planets = new List<FieldContactController.Contact>();

        internal NoiseFieldGpuCoordinator(EventManifest manifest)
        {
            _manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
            if (!manifest.ComputeGraphs.TryGetValue("coverage", out var graphPath))
                throw new InvalidOperationException("Field compute graph is not declared");
            var graphBytes = EventLoader.ReadResource(manifest.SourcePath, graphPath, 131072);
            _graph = GpuGraphDefinition.Parse(graphBytes);
            _modes = TaggedValueTable.Parse(graphBytes);
            _compositor = CompositorDefinition.Parse(graphBytes);
            if (!manifest.ShaderAssets.TryGetValue(_graph.ShaderRole, out var computePath))
                throw new InvalidOperationException("Compute shader role is not declared");
            _compute = ShaderBundleCache.LoadCompute(manifest, computePath, out string error);
            if (_compute == null) throw new InvalidOperationException(error);
            if (!manifest.VectorPrograms.TryGetValue("contacts", out var contactPath))
                throw new InvalidOperationException("Field contact controller is not declared");
            var contactBytes = EventLoader.ReadResource(manifest.SourcePath, contactPath, 262144);
            if (contactBytes == null) throw new InvalidOperationException("Field contact controller is missing");
            _contactProgram = new UTF8Encoding(false, true).GetString(contactBytes);
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
            _plan.Build(entries, camera, aspect, _modes, GameBridge.Values);
            NoiseFieldPlanetInputs.Collect(camera, _planets);
            _staging.DiscardPendingExcept(_plan.Groups);

            bool ready = true;
            foreach (var pair in _plan.Groups)
            {
                var plan = pair.Value;
                _staging.Pending.TryGetValue(pair.Key, out var pending);
                _staging.Active.TryGetValue(pair.Key, out var previous);
                var group = pending ?? previous;
                _graph.Size(camera.pixelWidth, camera.pixelHeight, out int width, out int height);
                if (group == null)
                    _staging.SetPending(pair.Key, group = new Group(_compute, _graph, _compositor, _manifest, pair.Key, _contactProgram)
                    {
                        ViewWidth = width, ViewHeight = height, Colored = plan.Colored,
                        ShowDisabled = plan.HasDisabled, ShowActive = plan.HasActive
                    });
                // With no input and no live contact animation, no controller work can
                // affect the frame; retain its clock so it still detects later seeks.
                if (_planets.Count > 0 || group.History.Visible)
                    group.History.Update(plan.TouchAreas, _planets, aspect, songTime);
                bool showDisabled = plan.HasDisabled;
                bool showActive = plan.HasActive || group.History.Visible;
                bool matches = group.ViewWidth == width && group.ViewHeight == height &&
                    group.Colored == plan.Colored && group.ShowDisabled == showDisabled && group.ShowActive == showActive;
                if (!matches)
                {
                    var replacement = new Group(_compute, _graph, _compositor, _manifest, pair.Key, _contactProgram, group.History)
                    {
                        ViewWidth = width, ViewHeight = height, Colored = plan.Colored,
                        ShowDisabled = showDisabled, ShowActive = showActive
                    };
                    _staging.SetPending(pair.Key, replacement);
                    group = replacement;
                }
                if (group.Areas.Length < plan.Areas.Count)
                    Array.Resize(ref group.Areas, Math.Max(plan.Areas.Count, group.Areas.Length * 2));
                plan.Areas.CopyTo(group.Areas, 0);
                int touchCount = 0;
                for (int i = 0; i < group.History.Slots.Count; i++)
                {
                    var slot = group.History.Slots[i];
                    group.Touches[touchCount++] = new NoiseFieldGpuMasks.Touch { Center = slot.Position, Scale = slot.Scale };
                }
                int positionCount = group.History.Contacts.Count;
                for (int i = 0; i < positionCount; i++)
                {
                    Vector2 p = group.History.Contacts[i].Position;
                    group.TouchPositions[i] = NoiseFieldGpuLayer.TouchPosition(p, camera.pixelWidth, camera.pixelHeight);
                }
                group.Masks.Render(group.Areas, plan.Areas.Count, camera.pixelWidth, camera.pixelHeight,
                    aspect, songTime, _images, group.Touches, touchCount);
                if (plan.Colored)
                {
                    if (group.Colors.Length != plan.Colors.Count) Array.Resize(ref group.Colors, plan.Colors.Count);
                    plan.Colors.CopyTo(group.Colors, 0);
                    if (!group.Palette.Update(group.Masks, aspect, group.Colors))
                    {
                        // Do not silently render a colored group as default red while its
                        // first palette or resized palette is still being calculated.
                        group.Layer.Hide();
                        ready = false;
                        continue;
                    }
                }
                ready &= group.Layer.Draw(camera, group.Masks, _images, aspect, songTime,
                    settings, group.History.Visible, showDisabled, showActive,
                    fallbackBackground, group.TouchPositions, positionCount,
                    plan.Colored ? group.Palette.Active : null, plan.Colored ? group.Palette.Disabled : null);
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
            _staging.Dispose();
        }
    }
}
