using System;
using System.Collections.Generic;

namespace PEV.Scripting
{
    /// <summary>Combines script-owned field phase overrides by event time and stable order.</summary>
    internal sealed class FieldPhaseOverrides
    {
        private struct Override
        {
            internal string Tag;
            internal NoiseFieldPhase Phase;
            internal double Time;
            internal int Order;
        }
        private readonly Dictionary<string, Override> _sources = new Dictionary<string, Override>(StringComparer.Ordinal);
        private readonly Dictionary<string, NoiseFieldPhase> _cache = new Dictionary<string, NoiseFieldPhase>(StringComparer.Ordinal);

        internal void Set(string scope, string tag, NoiseFieldPhase phase, double time, int order)
        {
            if (string.IsNullOrEmpty(scope) || string.IsNullOrWhiteSpace(tag) ||
                double.IsNaN(time) || double.IsInfinity(time) || !Enum.IsDefined(typeof(NoiseFieldPhase), phase))
                throw new ArgumentException("Invalid field phase override");
            if (_sources.TryGetValue(scope, out var previous) && previous.Tag == tag && previous.Phase == phase &&
                previous.Time == time && previous.Order == order) return;
            _sources[scope] = new Override { Tag = tag, Phase = phase, Time = time, Order = order };
            _cache.Clear();
        }

        internal void Remove(string scope) { if (scope != null && _sources.Remove(scope)) _cache.Clear(); }
        internal void Clear() { _sources.Clear(); _cache.Clear(); }

        internal NoiseFieldPhase At(string tag)
        {
            if (_sources.Count == 0) return NoiseFieldPhase.Active;
            tag = tag ?? "";
            if (_cache.TryGetValue(tag, out var cached)) return cached;
            var tokens = new HashSet<string>(tag.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
            var result = NoiseFieldPhase.Active;
            double bestTime = double.NegativeInfinity;
            int bestOrder = int.MinValue;
            foreach (var command in _sources.Values)
            {
                if (command.Time < bestTime || (command.Time == bestTime && command.Order < bestOrder)) continue;
                bool matches = false;
                foreach (var candidate in command.Tag.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                    if (tokens.Contains(candidate)) { matches = true; break; }
                if (!matches) continue;
                bestTime = command.Time; bestOrder = command.Order; result = command.Phase;
            }
            _cache[tag] = result;
            return result;
        }
    }
}
