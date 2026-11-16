using System;
using System.Collections.Generic;

namespace PEV.Scripting
{
    internal enum NoiseFieldPhase { Disabled, Ready, Active }
    public enum NoiseFieldStateAction { Disabled, Ready, Active, WarnThenActive }

    /// <summary>
    /// Absolute-time state track. Insertion order breaks same-time ties, matching the
    /// level's stable event order. Evaluation is stateless, including after a rewind.
    /// Visibility is deliberately not represented here: Disabled is a material phase.
    /// </summary>
    internal sealed class NoiseFieldStateTimeline
    {
        private struct Change
        {
            public double Time;
            public double Warning;
            public NoiseFieldStateAction Action;
            public int Sequence;
        }

        private readonly List<Change> _changes = new List<Change>();
        private bool _sorted = true;

        internal void Add(double time, NoiseFieldStateAction action, double warning = 0.5)
        {
            if (double.IsNaN(time) || double.IsInfinity(time))
                throw new ArgumentOutOfRangeException(nameof(time));
            if (!Enum.IsDefined(typeof(NoiseFieldStateAction), action))
                throw new ArgumentOutOfRangeException(nameof(action));
            if (double.IsNaN(warning) || double.IsInfinity(warning) || warning < 0)
                throw new ArgumentOutOfRangeException(nameof(warning));
            _changes.Add(new Change { Time = time, Action = action, Warning = warning, Sequence = _changes.Count });
            _sorted = false;
        }

        internal NoiseFieldPhase At(double time)
        {
            if (double.IsNaN(time) || double.IsInfinity(time))
                throw new ArgumentOutOfRangeException(nameof(time));
            if (!_sorted)
            {
                _changes.Sort((a, b) =>
                {
                    int order = a.Time.CompareTo(b.Time);
                    return order != 0 ? order : a.Sequence.CompareTo(b.Sequence);
                });
                _sorted = true;
            }

            // Only the most recent command applies; an older delayed activation cannot
            // overwrite a newer explicit phase command.
            int lo = 0, hi = _changes.Count;
            while (lo < hi)
            {
                int mid = lo + (hi - lo) / 2;
                if (_changes[mid].Time <= time) lo = mid + 1;
                else hi = mid;
            }
            if (lo == 0) return NoiseFieldPhase.Active;
            Change change = _changes[lo - 1];
            switch (change.Action)
            {
                case NoiseFieldStateAction.Disabled: return NoiseFieldPhase.Disabled;
                case NoiseFieldStateAction.Ready: return NoiseFieldPhase.Ready;
                case NoiseFieldStateAction.Active: return NoiseFieldPhase.Active;
                case NoiseFieldStateAction.WarnThenActive:
                    return time - change.Time < change.Warning ? NoiseFieldPhase.Ready : NoiseFieldPhase.Active;
                default: throw new InvalidOperationException("Unknown field state action");
            }
        }
    }
}
