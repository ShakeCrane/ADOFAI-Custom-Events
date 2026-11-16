using System;
using System.Collections.Generic;

namespace PEV.Scripting
{
    /// <summary>Builds an absolute-time track for each distinct set of field tags.</summary>
    internal sealed class NoiseFieldStateTracks
    {
        private struct TaggedChange
        {
            public string[] Tags;
            public double Time, Warning;
            public NoiseFieldStateAction Action;
        }

        private readonly List<TaggedChange> _changes = new List<TaggedChange>();
        private readonly Dictionary<string, NoiseFieldStateTimeline> _tracks =
            new Dictionary<string, NoiseFieldStateTimeline>(StringComparer.Ordinal);

        internal void Add(string tags, double time, NoiseFieldStateAction action, double warning)
        {
            if (string.IsNullOrWhiteSpace(tags)) return;
            // Reuse the timeline's validation at registration, not during playback.
            var validation = new NoiseFieldStateTimeline();
            validation.Add(time, action, warning);
            _changes.Add(new TaggedChange
            {
                Tags = tags.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries),
                Time = time, Action = action, Warning = warning
            });
            _tracks.Clear();
        }

        internal NoiseFieldPhase At(string fieldTags, double time)
        {
            fieldTags = fieldTags ?? "";
            if (!_tracks.TryGetValue(fieldTags, out var track))
            {
                var tags = new HashSet<string>(fieldTags.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries),
                    StringComparer.Ordinal);
                track = new NoiseFieldStateTimeline();
                foreach (var change in _changes)
                {
                    bool matches = false;
                    foreach (string tag in change.Tags)
                        if (tags.Contains(tag)) { matches = true; break; }
                    if (matches) track.Add(change.Time, change.Action, change.Warning);
                }
                _tracks.Add(fieldTags, track);
            }
            return track.At(time);
        }
    }
}
