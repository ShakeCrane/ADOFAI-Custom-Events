using System;
using System.Collections.Generic;
using System.Text;
using ADOFAI;
using PEV.Scripting;
using PEV.Storage;
using UnityEngine;

namespace PEV.Editor
{
    /// <summary>
    /// Runs the real event script against the editor's live rows so the grade is visible while
    /// it is being authored.
    ///
    /// The alternative was re-implementing the parameter mapping in C#, and that is how
    /// previews and playback start to disagree. Instead the script that will run in the song
    /// runs here too, with the same parameters and the same shader — the only difference is
    /// the clock, which is held at a point past any fade so the settled look is what the
    /// author sees.
    ///
    /// The script host is rebuilt only when a parameter actually changes. Jint engine
    /// creation is far too expensive to do per frame, and a grade whose sliders are not
    /// moving should cost nothing at all.
    /// </summary>
    internal static class ScreenEffectPreview
    {
        private const string ScopePrefix = "editor/screen/";
        private const double SettledSeconds = 3600.0;

        private sealed class Instance
        {
            internal ScriptHost Host;
            internal EventContext Context;
            internal string Signature;
        }

        private static readonly Dictionary<string, Instance> Live = new Dictionary<string, Instance>(StringComparer.Ordinal);

        /// <summary>Mirrors every screen-effect event in the level into a live preview.</summary>
        internal static void Tick(scnEditor editor)
        {
            if (editor == null) { Clear(); return; }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            double bpm = ReadBpm();

            var level = editor.levelData;
            if (level != null && level.decorations != null)
                foreach (var ev in level.decorations) Apply(ev, seen, bpm);

            var events = editor.events;
            if (events != null)
                foreach (var ev in events) Apply(ev, seen, bpm);

            var stale = new List<string>();
            foreach (var key in Live.Keys)
                if (!seen.Contains(key)) stale.Add(key);
            foreach (var key in stale)
            {
                Live[key].Host?.Dispose();
                Live.Remove(key);
                ScreenEffectPipeline.Clear(ScopePrefix + key);
            }
        }

        private static void Apply(LevelEvent ev, HashSet<string> seen, double bpm)
        {
            if (ev == null || !ev.active) return;
            if (!LevelStorage.TryGetData(ev, out var data)) return;
            if (data.IsPlaceholder) return;
            if (!Registry.EventRegistry.TryGetById(data.Id, out var manifest)) return;
            if (manifest.ScreenEffect == null) return;

            string uid = data.Uid ?? data.Id;
            string scope = ScopePrefix + uid;
            seen.Add(uid);

            string signature = Signature(ev, data);
            if (Live.TryGetValue(uid, out var instance) && instance.Signature == signature)
            {
                TickInstance(instance, bpm);
                return;
            }

            instance?.Host?.Dispose();
            if (!TryCreate(manifest, data, ev, scope, out var created, out string error))
            {
                PEV.Core.Plugin.Log("screen preview: " + error);
                Live[uid] = new Instance { Signature = signature };
                return;
            }
            created.Signature = signature;
            Live[uid] = created;
            TickInstance(created, bpm);
        }

        private static bool TryCreate(PEV.Model.EventManifest manifest, EventInstanceData data, LevelEvent ev,
            string scope, out Instance instance, out string error)
        {
            instance = null;
            error = null;
            string source = EventScriptCache.Get(manifest);
            if (source == null) { error = "entry script missing"; return false; }

            var host = ScriptHost.Create(source, scope, out string createError, manifest);
            if (host == null) { error = createError; return false; }

            var parameters = LiveParams(data, ev);
            var context = new EventContext
            {
                Bpm = ReadBpmSafe(),
                EventTime = 0,
                FloorSeq = ev.floor,
                EventOrder = 0,
                Params = parameters
            };
            if (manifest.ApiVersion >= 2) context.Settings = PEV.Core.Plugin.EventSettings.Snapshot(manifest);

            if (!host.Initialize(context, parameters, out string initError))
            {
                host.Dispose();
                error = initError;
                return false;
            }
            instance = new Instance { Host = host, Context = context };
            return true;
        }

        private static void TickInstance(Instance instance, double bpm)
        {
            if (instance?.Host == null) return;
            try
            {
                instance.Context.Bpm = bpm > 0 ? bpm : 120;
                instance.Context.Time = SettledSeconds;
                instance.Context.T = SettledSeconds;
                instance.Context.Beat = SettledSeconds * instance.Context.Bpm / 60.0;
                instance.Context.IsPlaying = true;
                instance.Host.Update(SettledSeconds, instance.Context.ToStateJson());
                if (instance.Host.Faulted)
                {
                    PEV.Core.Plugin.Log("screen preview faulted: " + instance.Host.LastError);
                    instance.Host.Dispose();
                    instance.Host = null;
                }
            }
            catch (Exception e)
            {
                PEV.Core.Plugin.Log("screen preview tick failed: " + e.Message);
            }
        }

        /// <summary>
        /// The signature covers everything the script reads. Parameter edits change it, which
        /// is what makes a slider drag rebuild the grade instead of leaving the preview stale.
        /// </summary>
        private static string Signature(LevelEvent ev, EventInstanceData data)
        {
            var builder = new StringBuilder(128);
            foreach (var pair in data.Effective)
            {
                object value = pair.Value;
                try
                {
                    object live = ev.Get<object>(pair.Key);
                    if (live != null) value = live;
                }
                catch { }
                builder.Append(pair.Key).Append('=');
                if (value is Vector2 vector) builder.Append(vector.x).Append(',').Append(vector.y);
                else if (value is float number) builder.Append(number.ToString("R"));
                else builder.Append(value);
                builder.Append(';');
            }
            return builder.ToString();
        }

        private static Dictionary<string, object> LiveParams(EventInstanceData data, LevelEvent ev)
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var pair in data.Effective)
            {
                object value = pair.Value;
                try
                {
                    object live = ev.Get<object>(pair.Key);
                    if (live != null) value = live;
                }
                catch { }
                result[pair.Key] = value;
            }
            return result;
        }

        private static double ReadBpm()
        {
            try
            {
                var conductor = scrConductor.instance;
                if (conductor != null && conductor.bpm > 0) return conductor.bpm;
            }
            catch { }
            return 120;
        }

        private static double ReadBpmSafe() => ReadBpm();

        internal static void Clear()
        {
            foreach (var pair in Live)
            {
                try { pair.Value.Host?.Dispose(); } catch { }
                // Only the preview's own rows: playback owns its layers and clearing those
                // would blank the grade the moment the editor stops previewing.
                ScreenEffectPipeline.Clear(ScopePrefix + pair.Key);
            }
            Live.Clear();
        }
    }
}
