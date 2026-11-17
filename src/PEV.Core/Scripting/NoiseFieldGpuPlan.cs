using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using PEV.Loading;
using PEV.Model;
using UnityEngine;

namespace PEV.Scripting
{
    // Transitional adapter: package code decides groups and their visual state.
    // Core only supplies live decoration snapshots and validates the returned plan.
    internal sealed class NoiseFieldGpuPlan : IDisposable
    {
        internal struct Key : IEquatable<Key>
        {
            public int SortingLayer, SortingOrder, UnityLayer;
            public bool Equals(Key other) => SortingLayer == other.SortingLayer && SortingOrder == other.SortingOrder && UnityLayer == other.UnityLayer;
            public override bool Equals(object other) => other is Key key && Equals(key);
            public override int GetHashCode() => ((SortingLayer * 397) ^ SortingOrder) * 397 ^ UnityLayer;
        }
        internal sealed class Group
        {
            internal int Count;
            internal JObject Data;
            internal void Clear() { Count = 0; Data = null; }
        }
        private struct Snapshot : IEquatable<Snapshot>
        {
            public int SortingLayer, SortingOrder, UnityLayer;
            public float X, Y, HalfX, HalfY, Angle, Opacity, Red, Green, Blue, DefaultRed, DefaultGreen, DefaultBlue;
            public bool Inverted, HasDefault;
            public string State;
            public bool Equals(Snapshot x) => SortingLayer == x.SortingLayer && SortingOrder == x.SortingOrder && UnityLayer == x.UnityLayer &&
                X == x.X && Y == x.Y && HalfX == x.HalfX && HalfY == x.HalfY && Angle == x.Angle && Opacity == x.Opacity &&
                Red == x.Red && Green == x.Green && Blue == x.Blue && DefaultRed == x.DefaultRed && DefaultGreen == x.DefaultGreen &&
                DefaultBlue == x.DefaultBlue && Inverted == x.Inverted && HasDefault == x.HasDefault && State == x.State;
        }
        private struct Input { public Snapshot[] Items; }
        private readonly Dictionary<Key, Group> _groups = new Dictionary<Key, Group>();
        private readonly List<NoiseFieldRenderer.Entry> _ordered = new List<NoiseFieldRenderer.Entry>();
        private readonly List<Snapshot> _items = new List<Snapshot>();
        private readonly List<Key> _stale = new List<Key>();
        private readonly Dictionary<EventManifest, Color?> _defaultColors = new Dictionary<EventManifest, Color?>();
        private readonly string _source;
        private JsonController _controller, _backgroundController;
        private readonly string _tagKey;
        private Snapshot[] _previous;
        private bool[] _seen = Array.Empty<bool>();
        private const int BackgroundThreshold = 32;
        private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();
        private Task<JObject> _pending;
        private Snapshot[] _pendingItems;
        private int _generation, _pendingGeneration;
        private bool _disposed;
        private static readonly Comparison<NoiseFieldRenderer.Entry> CompareSequence = (a, b) => a.GpuSequence.CompareTo(b.GpuSequence);
        internal IReadOnlyDictionary<Key, Group> Groups => _groups;

        internal static NoiseFieldGpuPlan Create(EventManifest manifest, byte[] graphBytes)
        {
            if (manifest == null || graphBytes == null || graphBytes.Length > 131072) throw new ArgumentException("Invalid plan provider");
            var graph = JObject.Parse(new UTF8Encoding(false, true).GetString(graphBytes));
            string tagKey = graph["tagValues"]?["tagKey"]?.Type == JTokenType.String ? graph["tagValues"].Value<string>("tagKey") : null;
            if (string.IsNullOrWhiteSpace(tagKey) || tagKey.Length > 128) throw new ArgumentException("Missing plan tag key");
            if (!manifest.TryRuntimeResource("renderPlan", manifest.VectorPrograms, out var path))
                throw new ArgumentException("Package render plan is not declared");
            byte[] source = EventLoader.ReadResource(manifest.SourcePath, path, 262144);
            if (source == null) throw new ArgumentException("Package render plan is missing or too large");
            return new NoiseFieldGpuPlan(tagKey, new UTF8Encoding(false, true).GetString(source));
        }
        private NoiseFieldGpuPlan(string tagKey, string source)
        {
            _tagKey = tagKey; _source = source;
        }

        internal bool Build(IReadOnlyList<NoiseFieldRenderer.Entry> entries, Camera camera, float aspect, TaggedValues values)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(NoiseFieldGpuPlan));
            if (values == null) throw new ArgumentNullException(nameof(values));
            _ordered.Clear(); _items.Clear();
            if (entries != null)
                for (int i = 0; i < entries.Count; i++) if (entries[i] != null) _ordered.Add(entries[i]);
            _ordered.Sort(CompareSequence);
            foreach (var entry in _ordered)
            {
                bool visible = entry.OverrideVisibility == 0 ? entry.Visible : entry.OverrideVisibility == 1;
                float opacity = entry.Opacity * entry.OverrideAlpha;
                if (!visible || !entry.Renderable || opacity <= 0f || float.IsNaN(opacity) || float.IsInfinity(opacity)) continue;
                var frame = entry.World;
                frame.Center += (Vector3)entry.OverrideOffset; frame.Size *= entry.OverrideScale; frame.Rotation += entry.OverrideRotation;
                if (!ViewportRectangleProjection.TryProject(camera, frame, new Vector2(1f, aspect), out var projected))
                    throw new NotSupportedException("Decoration cannot be projected without shear or perspective");
                var provider = entry.Provider;
                Color? expected = null;
                if (provider != null && !_defaultColors.TryGetValue(provider, out expected))
                {
                    string value = provider.DecorationDefaultColor;
                    expected = !string.IsNullOrEmpty(value) && ColorUtility.TryParseHtmlString("#" + value, out var parsed) ? parsed : (Color?)null;
                    _defaultColors.Add(provider, expected);
                }
                var color = entry.FillColor;
                _items.Add(new Snapshot { SortingLayer = entry.SortingLayerID, SortingOrder = entry.SortingOrder, UnityLayer = entry.UnityLayer,
                    X = projected.Center.x, Y = projected.Center.y, HalfX = projected.HalfSize.x, HalfY = projected.HalfSize.y,
                    Angle = projected.AngleRadians, Opacity = Mathf.Clamp01(opacity), Red = color.r, Green = color.g, Blue = color.b,
                    HasDefault = expected.HasValue, DefaultRed = expected?.r ?? 0, DefaultGreen = expected?.g ?? 0, DefaultBlue = expected?.b ?? 0,
                    Inverted = entry.GpuInverted, State = values.At(provider?.SourcePath, _tagKey, entry.Tag) });
                if (_items.Count > 1024) throw new ArgumentException("Too many projected decorations");
            }
            if (_items.Count == 0)
            {
                if (_pending != null && _pendingGeneration == _generation) _generation++;
                _groups.Clear(); _previous = null; return true;
            }
            if (_pending != null && _pending.IsCompleted)
            {
                try
                {
                    var plan = _pending.GetAwaiter().GetResult();
                    if (_pendingGeneration == _generation) { ApplyPlan(plan, _pendingItems); _previous = _pendingItems; }
                }
                catch { if (_pendingGeneration == _generation) throw; }
                finally { _pending = null; _pendingItems = null; }
            }
            bool unchanged = _previous != null && _previous.Length == _items.Count;
            for (int i = 0; unchanged && i < _items.Count; i++) unchanged = _items[i].Equals(_previous[i]);
            if (unchanged)
            {
                if (_pending != null && _pendingGeneration == _generation) _generation++;
                return true;
            }
            if (_items.Count < BackgroundThreshold)
            {
                if (_pending != null && _pendingGeneration == _generation) _generation++;
                var snapshot = _items.ToArray();
                if (_controller == null) _controller = new JsonController(_source, 50, 524288, 262144);
                var result = _controller.Update(PublicFieldsSnapshot.Serialize(new Input { Items = snapshot }));
                ApplyPlan(result, snapshot); _previous = snapshot; return true;
            }
            if (_pending == null)
            {
                _pendingItems = _items.ToArray(); _pendingGeneration = _generation;
                var snapshot = _pendingItems; var cancellation = _cancellation.Token;
                _pending = Task.Run(async () =>
                {
                    await VectorWorkerLimits.Slots.WaitAsync(cancellation).ConfigureAwait(false);
                    try
                    {
                        if (_backgroundController == null) _backgroundController = new JsonController(_source, 50, 524288, 262144, cancellation);
                        return _backgroundController.Update(PublicFieldsSnapshot.Serialize(new Input { Items = snapshot }));
                    }
                    finally { VectorWorkerLimits.Slots.Release(); }
                });
            }
            // Keep the last completed plan visible while new data is prepared off-thread.
            return _previous != null;
        }

        private void ApplyPlan(JObject plan, IReadOnlyList<Snapshot> items)
        {
            if (!(plan?["groups"] is JArray groups) || groups.Count > items.Count) throw new ArgumentException("Invalid package render groups");
            foreach (var group in _groups.Values) group.Clear();
            if (_seen.Length < items.Count) Array.Resize(ref _seen, items.Count);
            Array.Clear(_seen, 0, items.Count); int count = 0;
            foreach (var token in groups)
            {
                if (!(token is JObject item) || !(item["sorting"] is JArray sorting) || sorting.Count != 3 ||
                    !(item["members"] is JArray members) || !(item["data"] is JArray data) || data.Count != members.Count || members.Count == 0)
                    throw new ArgumentException("Invalid package render group");
                var key = new Key { SortingLayer = Integer(sorting[0], int.MinValue, int.MaxValue),
                    SortingOrder = Integer(sorting[1], int.MinValue, int.MaxValue), UnityLayer = Integer(sorting[2], 0, 31) };
                if (!_groups.TryGetValue(key, out var group)) _groups.Add(key, group = new Group());
                else if (group.Count != 0) throw new ArgumentException("Duplicate package render group");
                group.Data = item; group.Count = members.Count;
                foreach (var member in members)
                {
                    int index = Integer(member, 0, items.Count - 1);
                    var snapshot = items[index];
                    if (_seen[index] || snapshot.SortingLayer != key.SortingLayer || snapshot.SortingOrder != key.SortingOrder || snapshot.UnityLayer != key.UnityLayer)
                        throw new ArgumentException("Invalid package render membership");
                    _seen[index] = true; count++;
                }
            }
            if (count != items.Count) throw new ArgumentException("Package render plan omitted a decoration");
            _stale.Clear(); foreach (var pair in _groups) if (pair.Value.Count == 0) _stale.Add(pair.Key);
            foreach (var key in _stale) _groups.Remove(key);
        }
        private static int Integer(JToken token, int min, int max)
        {
            if (token?.Type != JTokenType.Integer) throw new ArgumentException("Invalid package render integer");
            long value = token.Value<long>();
            if (value < min || value > max) throw new ArgumentOutOfRangeException("Package render integer");
            return (int)value;
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true; _cancellation.Cancel();
            var pending = _pending; _pending = null; _pendingItems = null; _previous = null; _groups.Clear();
            if (pending != null) pending.ContinueWith(t => { var ignored = t.Exception; _cancellation.Dispose(); }, TaskContinuationOptions.ExecuteSynchronously);
            else _cancellation.Dispose();
        }
    }
}
