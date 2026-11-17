using System;
using System.Collections.Generic;

namespace PEV.Scripting
{
    /// <summary>Keep committed resources alive until every replacement is ready.</summary>
    internal sealed class ResourceGroupStaging<TKey, TValue> : IDisposable where TValue : class, IDisposable
    {
        internal readonly Dictionary<TKey, TValue> Active = new Dictionary<TKey, TValue>();
        internal readonly Dictionary<TKey, TValue> Pending = new Dictionary<TKey, TValue>();
        private readonly List<TKey> _stale = new List<TKey>();
        internal bool Committed { get; private set; }

        internal void SetPending(TKey key, TValue value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            if (Pending.TryGetValue(key, out var old))
            {
                if (ReferenceEquals(old, value)) return;
                Pending.Remove(key);
                old.Dispose();
            }
            Pending.Add(key, value);
        }

        internal void DiscardPendingExcept<TPlan>(IReadOnlyDictionary<TKey, TPlan> keep)
        {
            _stale.Clear();
            foreach (var pair in Pending) if (!keep.ContainsKey(pair.Key)) _stale.Add(pair.Key);
            foreach (var key in _stale) { Pending[key].Dispose(); Pending.Remove(key); }
        }

        internal void Commit<TPlan>(IReadOnlyDictionary<TKey, TPlan> keep)
        {
            if (keep == null) throw new ArgumentNullException(nameof(keep));
            // A pending resource can become obsolete between preparation and commit.
            // Commit must enforce this invariant even when called independently.
            DiscardPendingExcept(keep);
            _stale.Clear();
            foreach (var pair in Active)
                if (!keep.ContainsKey(pair.Key) || Pending.ContainsKey(pair.Key)) _stale.Add(pair.Key);
            foreach (var key in _stale) { Active[key].Dispose(); Active.Remove(key); }
            foreach (var pair in Pending) Active.Add(pair.Key, pair.Value);
            Pending.Clear();
            Committed = true;
        }

        public void Dispose()
        {
            foreach (var group in Active.Values) group.Dispose();
            foreach (var group in Pending.Values) group.Dispose();
            Active.Clear(); Pending.Clear(); Committed = false;
        }
    }
}
