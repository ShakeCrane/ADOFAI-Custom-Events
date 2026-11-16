using System;
using System.Collections.Generic;
using ADOFAI;
using PEV.Storage;

namespace PEV.Scripting
{
    /// <summary>Resolves tag state events against the rebuilt floor times once per play.</summary>
    internal sealed class NoiseFieldStateSchedule
    {
        internal const string EventId = "pev.noise-field-state";
        private readonly List<(LevelEvent Row, EventInstanceData Data)> _events =
            new List<(LevelEvent, EventInstanceData)>();
        private NoiseFieldStateTracks _tracks;
        internal bool Any => _events.Count != 0;

        internal void Add(LevelEvent row, EventInstanceData data)
        {
            if (row == null || data == null) return;
            _events.Add((row, data));
            _tracks = null;
        }

        internal bool Resolve(List<scrFloor> floors)
        {
            if (_tracks != null) return true;
            if (floors == null) return false;
            var tracks = new NoiseFieldStateTracks();
            foreach (var item in _events)
            {
                if (item.Row.floor < 0 || item.Row.floor >= floors.Count || floors[item.Row.floor] == null)
                {
                    PEV.Core.Plugin.Log("noise field state: skipping invalid floor " + item.Row.floor);
                    continue;
                }
                double time = floors[item.Row.floor].entryTime;
                if (double.IsNaN(time) || double.IsInfinity(time)) return false;
                string tags = Read(item.Row, item.Data, "tag")?.ToString();
                string actionText = Read(item.Row, item.Data, "action")?.ToString() ?? "Active";
                if (!Enum.TryParse(actionText, true, out NoiseFieldStateAction action) ||
                    !Enum.IsDefined(typeof(NoiseFieldStateAction), action)) continue;
                double warning = 0.5;
                object raw = Read(item.Row, item.Data, "warningDuration");
                try { if (raw != null) warning = Convert.ToDouble(raw); } catch { warning = 0.5; }
                if (double.IsNaN(warning) || double.IsInfinity(warning) || warning < 0) warning = 0.5;
                tracks.Add(tags, time, action, warning);
            }
            _tracks = tracks;
            return true;
        }

        internal NoiseFieldPhase At(string tags, double time) =>
            _tracks == null ? NoiseFieldPhase.Active : _tracks.At(tags, time);

        internal void Reset() => _tracks = null;

        private static object Read(LevelEvent row, EventInstanceData data, string key)
        {
            object value = null;
            data.TryGet(key, out value);
            try { return row.Get<object>(key) ?? value; } catch { return value; }
        }
    }
}
