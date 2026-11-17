using System;
using System.Collections.Generic;
using UnityEngine;

namespace PEV.Scripting
{
    /// <summary>
    /// Draft preparation for GPU groups. No GPU resources are allocated here.
    /// Groups and lists are reused between frames; they must not be retained by callers.
    /// </summary>
    internal sealed class NoiseFieldGpuPlan
    {
        internal struct Key : IEquatable<Key>
        {
            public int SortingLayer, SortingOrder, UnityLayer;
            public bool Equals(Key other) => SortingLayer == other.SortingLayer &&
                SortingOrder == other.SortingOrder && UnityLayer == other.UnityLayer;
            public override bool Equals(object other) => other is Key key && Equals(key);
            public override int GetHashCode() => ((SortingLayer * 397) ^ SortingOrder) * 397 ^ UnityLayer;
        }

        internal sealed class Group
        {
            public readonly List<NoiseFieldGpuMasks.Area> Areas = new List<NoiseFieldGpuMasks.Area>();
            public readonly List<NoiseFieldColorOwnership.Zone> Colors = new List<NoiseFieldColorOwnership.Zone>();
            public readonly List<FieldContactController.Area> TouchAreas = new List<FieldContactController.Area>();
            public bool HasActive, HasDisabled, Colored;
            internal void Clear() { Areas.Clear(); Colors.Clear(); TouchAreas.Clear(); HasActive = HasDisabled = Colored = false; }
        }

        private readonly Dictionary<Key, Group> _groups = new Dictionary<Key, Group>();
        private readonly List<NoiseFieldRenderer.Entry> _ordered = new List<NoiseFieldRenderer.Entry>();
        private readonly List<Key> _stale = new List<Key>();
        private readonly Dictionary<PEV.Model.EventManifest, Color?> _defaultColors =
            new Dictionary<PEV.Model.EventManifest, Color?>();
        private static readonly Comparison<NoiseFieldRenderer.Entry> CompareSequence =
            (a, b) => a.GpuSequence.CompareTo(b.GpuSequence);
        internal IReadOnlyDictionary<Key, Group> Groups => _groups;

        internal void Build(IReadOnlyList<NoiseFieldRenderer.Entry> entries, Camera camera, float aspect,
            TaggedValueTable modes, TaggedValues values)
        {
            if (modes == null || values == null) throw new ArgumentNullException("region modes");
            foreach (var group in _groups.Values) group.Clear();
            _ordered.Clear();
            if (entries != null)
                for (int i = 0; i < entries.Count; i++) if (entries[i] != null) _ordered.Add(entries[i]);
            _ordered.Sort(CompareSequence);
            foreach (var entry in _ordered)
            {
                bool visible = entry.OverrideVisibility == 0 ? entry.Visible : entry.OverrideVisibility == 1;
                float opacity = entry.Opacity * entry.OverrideAlpha;
                if (!visible || !entry.Renderable || opacity <= 0f || float.IsNaN(opacity) || float.IsInfinity(opacity)) continue;
                // Reference areas are rectangular. Retain an old chart's deprecated
                // shape value in its data, but do not let it disable every GPU group.
                var frame = entry.World;
                frame.Center += (Vector3)entry.OverrideOffset;
                frame.Size *= entry.OverrideScale;
                frame.Rotation += entry.OverrideRotation;
                if (!ViewportRectangleProjection.TryProject(camera, frame, new Vector2(1f, aspect), out var projected))
                    throw new NotSupportedException("Field cannot be projected without shear or perspective");
                var area = new NoiseFieldGpuMasks.Area { Center = projected.Center, HalfSize = projected.HalfSize,
                    AngleRadians = projected.AngleRadians, YScale = 1f };
                area.Opacity = Mathf.Clamp01(opacity);
                var mode = modes.Resolve(values.At(entry.Provider?.SourcePath, modes.TagKey, entry.Tag));
                uint shaderFlags = mode.Unsigned("shaderFlags", 65535), invertFlag = modes.Metadata.Unsigned("invertFlag", 65535);
                if ((shaderFlags & invertFlag) != 0) throw new ArgumentException("Mode and inversion flags overlap");
                area.Flags = shaderFlags | (entry.GpuInverted ? invertFlag : 0u);
                var key = new Key { SortingLayer = entry.SortingLayerID, SortingOrder = entry.SortingOrder, UnityLayer = entry.UnityLayer };
                if (!_groups.TryGetValue(key, out var group)) _groups.Add(key, group = new Group());
                group.Areas.Add(area);
                // The native decoration color also receives MoveDecorations tweens. The
                // legacy edgeColor remains stored for old charts but is not the GPU tint.
                var color = entry.FillColor;
                group.Colors.Add(new NoiseFieldColorOwnership.Zone
                {
                    CenterX = area.Center.x, CenterY = area.Center.y, HalfX = area.HalfSize.x, HalfY = area.HalfSize.y,
                    Angle = area.AngleRadians, YScale = area.YScale, Opacity = area.Opacity,
                    Red = color.r, Green = color.g, Blue = color.b,
                    Active = mode.Boolean("palette"), Inverted = entry.GpuInverted
                });
                // The non-palette shader uses the package's own declared default color.
                // A missing default is conservatively treated as custom, so no event's
                // preferred color becomes an implicit Core-wide rule.
                var provider = entry.Provider;
                if (provider == null) group.Colored = true;
                else
                {
                    if (!_defaultColors.TryGetValue(provider, out var expected))
                    {
                        string value = provider.DecorationDefaultColor;
                        expected = !string.IsNullOrEmpty(value) && ColorUtility.TryParseHtmlString("#" + value, out var parsed)
                            ? parsed : (Color?)null;
                        _defaultColors.Add(provider, expected);
                    }
                    if (!expected.HasValue) group.Colored = true;
                    else group.Colored |= color.r != expected.Value.r || color.g != expected.Value.g || color.b != expected.Value.b;
                }
                group.TouchAreas.Add(new FieldContactController.Area
                {
                    Center = area.Center, HalfSize = area.HalfSize,
                    Angle = area.AngleRadians,
                    Active = mode.Boolean("contacts"),
                    Inverted = entry.GpuInverted, Visible = true
                });
                group.HasActive |= mode.Boolean("mainLayer");
                group.HasDisabled |= mode.Boolean("auxLayer");
            }
            _stale.Clear();
            foreach (var pair in _groups) if (pair.Value.Areas.Count == 0) _stale.Add(pair.Key);
            foreach (var key in _stale) _groups.Remove(key);
        }
    }
}
