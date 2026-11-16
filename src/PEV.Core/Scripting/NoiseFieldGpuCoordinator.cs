using System;
using System.Collections.Generic;
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
            internal readonly NoiseFieldTouchHistory History;
            internal readonly List<NoiseFieldTouchHistory.Contact> Contacts = new List<NoiseFieldTouchHistory.Contact>();
            internal readonly NoiseFieldGpuMasks.Touch[] Touches = new NoiseFieldGpuMasks.Touch[NoiseFieldTouchHistory.Capacity];
            internal readonly Vector4[] TouchPositions = new Vector4[NoiseFieldTouchHistory.Capacity];
            internal NoiseFieldGpuMasks.Area[] Areas = new NoiseFieldGpuMasks.Area[4];
            internal NoiseFieldColorOwnership.Zone[] Colors = Array.Empty<NoiseFieldColorOwnership.Zone>();
            internal int ViewWidth, ViewHeight;
            internal bool Colored, ShowDisabled, ShowActive;

            internal Group(ComputeShader shader, EventManifest manifest, NoiseFieldGpuPlan.Key key,
                NoiseFieldTouchHistory history = null)
            {
                History = history ?? new NoiseFieldTouchHistory();
                Palette = new NoiseFieldGpuPalette(manifest);
                manifest.LegacyFieldProfile.TryGetValue("glowWeights", out var glowWeights);
                Masks = new NoiseFieldGpuMasks(shader, glowWeights);
                try { Layer = new NoiseFieldGpuLayer(manifest, key.SortingLayer, key.SortingOrder, key.UnityLayer); }
                catch { Masks.Dispose(); throw; }
            }
            public void Dispose() { Layer.Dispose(); Palette.Dispose(); Masks.Dispose(); }
        }

        private readonly EventManifest _manifest;
        private readonly ComputeShader _compute;
        private readonly Texture2D _displacement, _spark, _noise, _touch;
        private readonly NoiseFieldGpuPlan _plan = new NoiseFieldGpuPlan();
        private readonly NoiseFieldGroupStaging<NoiseFieldGpuPlan.Key, Group> _staging =
            new NoiseFieldGroupStaging<NoiseFieldGpuPlan.Key, Group>();
        private readonly List<NoiseFieldTouchHistory.Contact> _planets = new List<NoiseFieldTouchHistory.Contact>();

        internal NoiseFieldGpuCoordinator(EventManifest manifest)
        {
            _manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
            if (!manifest.FieldShaders.TryGetValue("compute", out var computePath))
                throw new InvalidOperationException("Field compute shader role is not declared");
            _compute = ShaderBundleCache.LoadCompute(manifest, computePath, out string error);
            if (_compute == null) throw new InvalidOperationException(error);
            _displacement = RequireTexture(manifest, "displacement");
            _spark = RequireTexture(manifest, "spark");
            _noise = RequireTexture(manifest, "noise");
            _touch = RequireTexture(manifest, "touch");
        }

        private static Texture2D RequireTexture(EventManifest manifest, string name)
        {
            var texture = ReferenceNoiseTextures.Load(manifest, name, out string error);
            return texture != null ? texture : throw new InvalidOperationException(error);
        }

        internal bool Render(IReadOnlyList<NoiseFieldRenderer.Entry> entries, Camera camera, float songTime,
            bool captureScene, Color fallbackBackground)
        {
            if (camera == null || !camera.orthographic) throw new NotSupportedException("An orthographic camera is required");
            float aspect = Mathf.Min(camera.aspect, 16f / 9f);
            _plan.Build(entries, camera, aspect);
            NoiseFieldPlanetInputs.Collect(camera, _planets);
            _staging.DiscardPendingExcept(_plan.Groups);

            bool ready = true;
            foreach (var pair in _plan.Groups)
            {
                var plan = pair.Value;
                _staging.Pending.TryGetValue(pair.Key, out var pending);
                _staging.Active.TryGetValue(pair.Key, out var previous);
                var group = pending ?? previous;
                int width = Math.Max(1, camera.pixelWidth / 8) * 2;
                int height = Math.Max(1, camera.pixelHeight / 8) * 2;
                if (group == null)
                    _staging.SetPending(pair.Key, group = new Group(_compute, _manifest, pair.Key)
                    {
                        ViewWidth = width, ViewHeight = height, Colored = plan.Colored,
                        ShowDisabled = plan.HasDisabled, ShowActive = plan.HasActive
                    });
                group.Contacts.Clear();
                foreach (var planet in _planets)
                {
                    var chart = new Vector2(planet.Position.x * 2f - 1f, (planet.Position.y * 2f - 1f) / aspect);
                    if (NoiseFieldTouchGeometry.Contains(plan.TouchAreas, chart)) group.Contacts.Add(planet);
                }
                group.History.Update(group.Contacts, songTime);
                bool showDisabled = plan.HasDisabled;
                bool showActive = plan.HasActive || group.History.Visible;
                bool matches = group.ViewWidth == width && group.ViewHeight == height &&
                    group.Colored == plan.Colored && group.ShowDisabled == showDisabled && group.ShowActive == showActive;
                if (!matches)
                {
                    var replacement = new Group(_compute, _manifest, pair.Key, group.History)
                    {
                        ViewWidth = width, ViewHeight = height, Colored = plan.Colored,
                        ShowDisabled = showDisabled, ShowActive = showActive
                    };
                    replacement.Contacts.AddRange(group.Contacts);
                    _staging.SetPending(pair.Key, replacement);
                    group = replacement;
                }
                if (group.Areas.Length < plan.Areas.Count)
                    Array.Resize(ref group.Areas, Math.Max(plan.Areas.Count, group.Areas.Length * 2));
                plan.Areas.CopyTo(group.Areas, 0);
                int touchCount = 0;
                for (int i = 0; i < NoiseFieldTouchHistory.Capacity; i++)
                {
                    var slot = group.History.GetSlot(i);
                    if (slot.Scale <= 0f) continue;
                    group.Touches[touchCount++] = new NoiseFieldGpuMasks.Touch { Center = slot.Position, Scale = slot.Scale };
                }
                int positionCount = Math.Min(group.Contacts.Count, NoiseFieldTouchHistory.Capacity);
                for (int i = 0; i < positionCount; i++)
                {
                    Vector2 p = group.Contacts[i].Position;
                    group.TouchPositions[i] = NoiseFieldGpuLayer.TouchPosition(p, camera.pixelWidth, camera.pixelHeight);
                }
                group.Masks.Render(group.Areas, plan.Areas.Count, camera.pixelWidth, camera.pixelHeight,
                    aspect, songTime, _displacement, group.Touches, touchCount, _touch);
                if (plan.Colored)
                {
                    if (group.Colors.Length != plan.Colors.Count) Array.Resize(ref group.Colors, plan.Colors.Count);
                    plan.Colors.CopyTo(group.Colors, 0);
                    if (!group.Palette.Update(group.Masks, _displacement, aspect, group.Colors))
                    {
                        // Do not silently render a colored group as default red while its
                        // first palette or resized palette is still being calculated.
                        group.Layer.Hide();
                        ready = false;
                        continue;
                    }
                }
                ready &= group.Layer.Draw(camera, group.Masks, _displacement, _spark, _noise, aspect, songTime,
                    captureScene, group.History.Visible, showDisabled, showActive,
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

        public void Dispose()
        {
            _staging.Dispose();
        }
    }
}
