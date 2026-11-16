using System;
using System.Collections.Generic;

namespace PEV.Scripting
{
    // Package-scoped, tag-addressed string values, ordered by event time and chart order.
    internal sealed class TaggedValues
    {
        private struct Value
        {
            internal string Scope, Package, Key, Tag, Text;
            internal double Time;
            internal int Order;
        }
        private readonly Dictionary<(string Scope, string Key), Value> _values = new Dictionary<(string, string), Value>();
        private readonly Dictionary<(string Package, string Key, string Tag), string> _cache = new Dictionary<(string, string, string), string>();

        internal void Set(string scope, string package, string key, string tag, string text, double time, int order)
        {
            if (string.IsNullOrEmpty(scope) || scope.Length > 1024 || string.IsNullOrEmpty(package) || package.Length > 4096 ||
                string.IsNullOrWhiteSpace(key) || key.Length > 128 || string.IsNullOrWhiteSpace(tag) || tag.Length > 512 ||
                text == null || text.Length > 256 || double.IsNaN(time) || double.IsInfinity(time))
                throw new ArgumentException("Invalid tagged value");
            var id = (scope, key);
            if (_values.TryGetValue(id, out var previous) && previous.Package == package && previous.Tag == tag &&
                previous.Text == text && previous.Time == time && previous.Order == order) return;
            _values[id] = new Value { Scope = scope, Package = package, Key = key, Tag = tag, Text = text, Time = time, Order = order };
            _cache.Clear();
        }
        internal void Remove(string scope)
        {
            if (scope == null) return;
            var remove = new List<(string, string)>();
            foreach (var item in _values) if (item.Value.Scope == scope) remove.Add(item.Key);
            foreach (var id in remove) _values.Remove(id);
            if (remove.Count > 0) _cache.Clear();
        }
        internal void Clear() { _values.Clear(); _cache.Clear(); }
        internal string At(string package, string key, string tag)
        {
            if (_values.Count == 0 || package == null || key == null) return null;
            var cacheKey = (package, key, tag ?? "");
            if (_cache.TryGetValue(cacheKey, out var cached)) return cached;
            var tokens = new HashSet<string>(cacheKey.Item3.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
            string result = null; double bestTime = double.NegativeInfinity; int bestOrder = int.MinValue;
            foreach (var command in _values.Values)
            {
                if (command.Package != package || command.Key != key || command.Time < bestTime ||
                    (command.Time == bestTime && command.Order < bestOrder)) continue;
                bool matches = false;
                foreach (var candidate in command.Tag.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                    if (tokens.Contains(candidate)) { matches = true; break; }
                if (!matches) continue;
                bestTime = command.Time; bestOrder = command.Order; result = command.Text;
            }
            _cache[cacheKey] = result;
            return result;
        }
    }
}
