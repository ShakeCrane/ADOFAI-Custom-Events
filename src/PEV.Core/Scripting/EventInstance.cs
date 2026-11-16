using System;
using System.Collections.Generic;
using ADOFAI;
using PEV.Core;
using Newtonsoft.Json;
using PEV.Model;
using PEV.Storage;
using UnityEngine;

namespace PEV.Scripting
{
    public enum InstancePhase { Dormant, Running, Faulted }

    /// <summary>
    /// One placed event: its stored data, its script host, its lifecycle position.
    ///
    /// Time is derived from the game's song clock minus the tile's entry time, never from
    /// accumulated deltas, so pause, restart, checkpoints and mid-level seeks stay correct
    /// without special cases.
    /// </summary>
    public class EventInstance
    {
        public LevelEvent LevelEvent;
        public EventInstanceData Data;
        public EventManifest Manifest;

        public InstancePhase Phase = InstancePhase.Dormant;
        public ScriptHost Host;

        public double EntryTime;
        public bool EntryTimeResolved;
        public double BpmRatio = 2.0;
        public double LastT = double.NegativeInfinity;

        public string Key => $"{Manifest?.Id}#{Data?.Uid}";

        public EventContext Context;
        internal int EventOrder;
        private int _settingsRevision = -1;

        /// <summary>
        /// Resolves the tile's entry time from the live floor list.
        ///
        /// It cannot be captured when the runtime is armed: arming happens before the path
        /// is rebuilt for playback, so floor times are still stale and produce NaN. Look it
        /// up on the frame the instance starts instead.
        /// </summary>
        internal void ResolveEntryTime()
        {
            try
            {
                var floors = ADOBase.lm != null ? ADOBase.lm.listFloors : null;
                if (floors == null) return;

                int idx = LevelEvent.floor;
                if (idx < 0 || idx >= floors.Count) return;

                double t = floors[idx].entryTime;
                if (double.IsNaN(t) || double.IsInfinity(t)) return;

                EntryTime = t;
                EntryTimeResolved = true;
            }
            catch { }
        }

        public bool Begin()
        {
            if (Manifest == null || Data == null) { Phase = InstancePhase.Faulted; return false; }

            if (!EntryTimeResolved) ResolveEntryTime();

            string source = EventScriptCache.Get(Manifest);
            if (source == null) { Phase = InstancePhase.Faulted; return false; }

            Host = ScriptHost.Create(source, Key, out string err);
            if (Host == null)
            {
                Plugin.Log("create failed: " + err);
                Phase = InstancePhase.Faulted;
                return false;
            }

            Context = new EventContext
            {
                Bpm = ReadBpm(),
                EventTime = EntryTime,
                FloorSeq = LevelEvent.floor,
                EventOrder = EventOrder,
                Params = ReadLiveParams()
            };
            if (Manifest.ApiVersion >= 2)
            {
                Context.Settings = Plugin.EventSettings.Snapshot(Manifest);
                _settingsRevision = Plugin.EventSettings.Revision;
            }

            if (!Host.Initialize(Context, Context.Params, out string initError))
            {
                Plugin.Log("init failed: " + initError);
                Phase = InstancePhase.Faulted;
                Host.Dispose();
                Host = null;
                return false;
            }

            Phase = InstancePhase.Running;
            return true;
        }

        /// <summary>
        /// Parameters as they stand in the editor right now.
        ///
        /// Data.Effective is the snapshot taken when the level was decoded or the event was
        /// placed. An author who edits a field and immediately presses play would otherwise
        /// get the old values — the effect would look like the change did nothing. Read the
        /// LevelEvent itself, and fall back to the snapshot for anything it does not carry.
        /// </summary>
        private Dictionary<string, object> ReadLiveParams()
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);

            foreach (var kv in Data.Effective)
            {
                object value = kv.Value;
                try
                {
                    object live = LevelEvent.Get<object>(kv.Key);
                    if (live != null) value = live;
                }
                catch { /* keep the snapshot value */ }
                result[kv.Key] = value;
            }

            return result;
        }

        public void Tick(double songTime, bool playing)
        {
            if (Phase != InstancePhase.Running) return;

            double t = songTime - EntryTime;

            // Backwards time means the player restarted or jumped to a checkpoint.
            // Tear down and let the next crossing rebuild the instance from scratch.
            if (t + 0.0001 < LastT && t < 0)
            {
                Reset();
                return;
            }

            LastT = t;
            if (t < 0 || !playing) return;

            Context.Time = songTime;
            Context.T = t;
            Context.Bpm = ReadBpm();
            BpmRatio = Context.Bpm / 60.0;
            Context.Beat = t * BpmRatio;
            Context.IsPlaying = true;
            Context.IsPaused = false;
            if (Manifest.ApiVersion >= 2 && _settingsRevision != Plugin.EventSettings.Revision)
            {
                Context.Settings = Plugin.EventSettings.Snapshot(Manifest);
                _settingsRevision = Plugin.EventSettings.Revision;
            }

            Host.Update(t, Context.ToStateJson());
            if (Host.Faulted)
            {
                Plugin.Log("instance faulted: " + Host.LastError);
                Host.Dispose();
                Host = null;
                Phase = InstancePhase.Faulted;
            }
        }

        public void Reset()
        {
            if (Host != null) Host.Dispose();
            Host = null;
            Phase = InstancePhase.Dormant;
            LastT = double.NegativeInfinity;
            EntryTimeResolved = false;
        }

        private static double ReadBpm()
        {
            var conductor = scrConductor.instance;
            double bpm = conductor != null ? conductor.bpm : 120.0;
            return bpm > 0 && !double.IsInfinity(bpm) ? bpm : 120.0;
        }

        public void End()
        {
            if (Host != null) Host.Dispose();
            Host = null;
            Phase = InstancePhase.Dormant;
        }
    }

    /// <summary>Reads each package's entry script once.</summary>
    public static class EventScriptCache
    {
        private static readonly Dictionary<string, string> Cache = new Dictionary<string, string>();

        public static string Get(EventManifest manifest)
        {
            if (manifest == null) return null;
            if (Cache.TryGetValue(manifest.Id, out var cached)) return cached;

            try
            {
                byte[] bytes = PEV.Loading.EventLoader.ReadResource(manifest.SourcePath, manifest.Entry);
                if (bytes == null)
                {
                    Plugin.Log($"'{manifest.Id}': entry script '{manifest.Entry}' missing from package");
                    Cache[manifest.Id] = null;
                    return null;
                }
                string text = System.Text.Encoding.UTF8.GetString(bytes);
                Cache[manifest.Id] = text;
                return text;
            }
            catch (Exception e)
            {
                Plugin.Log($"'{manifest.Id}': cannot read script: {e.Message}");
                Cache[manifest.Id] = null;
                return null;
            }
        }

        public static void Clear() => Cache.Clear();
    }
}
