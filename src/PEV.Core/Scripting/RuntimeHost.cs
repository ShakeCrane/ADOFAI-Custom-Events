using System;
using System.Collections.Generic;
using System.Linq;
using ADOFAI;
using PEV.Core;
using PEV.Registry;
using PEV.Storage;
using UnityEngine;

namespace PEV.Scripting
{
    /// <summary>
    /// Owns every live instance for the current level and drives them from the song clock.
    ///
    /// One host for the whole level rather than one component per event: tick order is
    /// deterministic and teardown happens at exactly one moment.
    /// </summary>
    public class RuntimeHost : MonoBehaviour
    {
        private static RuntimeHost _instance;

        /// <summary>Clock advance must exceed this to count as "playing".</summary>
        private const double AdvanceEpsilon = 1e-4;

        /// <summary>
        /// How far the song clock must jump backwards before we call it a rewind.
        /// Measured jitter on ADOFAI's audio-derived clock is a few milliseconds.
        /// </summary>
        private const double RewindThreshold = 0.05;

        private readonly List<EventInstance> _instances = new List<EventInstance>();
        private bool _live;

        public static RuntimeHost Ensure()
        {
            if (_instance == null)
            {
                var go = new GameObject("PEV.Runtime");
                UnityEngine.Object.DontDestroyOnLoad(go);
                _instance = go.AddComponent<RuntimeHost>();
            }
            return _instance;
        }

        public static void Shutdown()
        {
            if (_instance == null) return;
            _instance.Teardown();
            UnityEngine.Object.Destroy(_instance.gameObject);
            _instance = null;
        }

        /// <summary>
        /// Re-arms from whatever the editor currently holds.
        ///
        /// Rebuild() runs when a level is decoded, but an author places events *after* that
        /// and then presses play. Without this the events they just placed never run, which
        /// is the first thing anyone would try.
        /// </summary>
        public static void RebuildFromEditor()
        {
            try
            {
                if (ADOBase.editor == null) return;
                var level = ADOBase.editor.levelData;
                if (level == null) return;
                Ensure().Rebuild(level);
            }
            catch (Exception e)
            {
                Plugin.Log("runtime rebuild from editor failed: " + e.Message);
            }
        }

        /// <summary>Rebuilds instances for the level now loaded. Safe to call repeatedly.</summary>
        public void Rebuild(LevelData level)
        {
            Teardown();
            if (level == null) return;

            var floors = ADOBase.lm != null ? ADOBase.lm.listFloors : null;
            int built = 0;

            // Decorations first. A decoration-backed event lives in level.decorations, not in
            // levelEvents, so iterating only the latter is why a placed field never ran.
            try
            {
                var decorations = level.decorations;
                if (decorations != null)
                {
                    foreach (var ev in decorations)
                    {
                        if (ev == null) continue;
                        if (!LevelStorage.TryGetData(ev, out var data)) continue;
                        if (data.IsPlaceholder) continue;
                        if (!EventRegistry.TryGetById(data.Id, out var manifest)) continue;

                        Arm(ev, data, manifest, floors);
                        built++;
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log("runtime: decoration arm failed: " + e.Message);
            }

            foreach (var ev in level.levelEvents)
            {
                if (!LevelStorage.TryGetData(ev, out var data)) continue;

                // A placeholder stands for a package we could not resolve; there is no
                // script to run, and running the placeholder's own would be wrong.
                if (data.IsPlaceholder) continue;

                if (!EventRegistry.TryGetById(data.Id, out var manifest)) continue;

                Arm(ev, data, manifest, floors);
                built++;
            }

            _live = built > 0;
            Plugin.Log($"runtime: {built} instance(s) armed");
        }

        private void Arm(LevelEvent ev, EventInstanceData data, PEV.Model.EventManifest manifest, List<scrFloor> floors)
        {
            // Floor times are not reliable at arm time (the path may not be rebuilt yet),
            // so this is only a first guess. EventInstance re-resolves it on start and
            // ignores a NaN here rather than propagating it into every frame.
            double entry = 0;
            if (floors != null && ev.floor >= 0 && ev.floor < floors.Count)
            {
                double t = floors[ev.floor].entryTime;
                if (!double.IsNaN(t) && !double.IsInfinity(t)) entry = t;
            }

            var inst = new EventInstance
            {
                LevelEvent = ev,
                Data = data,
                Manifest = manifest,
                EntryTime = entry,
                EntryTimeResolved = false
            };

            // A decoration row is a decoration: bind the instance to it so every frame reads
            // the live component instead of the values frozen at the last save. The scope has
            // to be the same string the script host stamps into its calls, which is the
            // instance's own key.
            if (ev.IsDecoration) NoiseFieldRenderer.Bind(inst.Key, ev);

            _instances.Add(inst);
        }

        private double _lastSongTime = double.NegativeInfinity;

        private void Update()
        {
            // Upload a slice of any finished bake, before this frame's rendering. Texture
            // creation is main-thread-only, so this is the only place it can be bounded; the
            // pixel work itself runs on a background thread. Kept outside the early-outs
            // below so a bake still finishes while the song is paused or nothing is playing.
            NoiseTextureFactory.Pump(1.5f);

            if (!_live || _instances.Count == 0) return;

            double songTime;
            try
            {
                var conductor = scrConductor.instance;
                if (conductor == null) return;
                songTime = conductor.songposition_minusi;
            }
            catch { return; }

            // "Is the level running" is answered by the clock itself, not by an enum.
            // The song position freezes when paused, so a time delta is the honest signal.
            //
            // Rewind detection needs a real threshold: songposition is derived from the
            // audio device clock and jitters by a few milliseconds frame to frame. Treating
            // that jitter as a rewind would reset every instance several times a second.
            bool advancing = songTime > _lastSongTime + AdvanceEpsilon;
            bool rewound = songTime < _lastSongTime - RewindThreshold;

            if (rewound)
            {
                int reset = 0;
                foreach (var inst in _instances)
                {
                    if (inst.Phase == InstancePhase.Running) { inst.Reset(); reset++; }
                }
                Plugin.Log($"rewind detected (songTime {_lastSongTime:F3} -> {songTime:F3}); reset {reset} instance(s)");

                // Rebase on the new position. Without this the baseline stays at the old
                // peak and every subsequent frame is misread as another rewind.
                _lastSongTime = songTime;
            }
            else if (songTime > _lastSongTime)
            {
                _lastSongTime = songTime;
            }

            for (int i = 0; i < _instances.Count; i++)
            {
                var inst = _instances[i];
                try
                {
                    if (inst.Phase == InstancePhase.Dormant && advancing && songTime >= inst.EntryTime)
                        inst.Begin();

                    if (inst.Phase == InstancePhase.Running)
                        inst.Tick(songTime, advancing);
                }
                catch (Exception e)
                {
                    Plugin.Log($"instance {inst.Key} threw: {e}");
                    inst.Phase = InstancePhase.Faulted;
                }
            }
        }

        public void Teardown()
        {
            foreach (var inst in _instances)
            {
                try { inst.End(); } catch { }
            }
            _instances.Clear();
            _live = false;

            // Bindings point at LevelEvent rows that the next decode replaces. Clearing them
            // is what stops a stale reference from resurrecting a destroyed decoration.
            try { NoiseFieldRenderer.UnbindAll(); } catch { }

            // Belt and braces: even a script that leaked objects or crashed leaves nothing.
            try { GameBridge.DestroyAll(); } catch { }
        }

        private void OnDestroy()
        {
            Teardown();
            if (_instance == this) _instance = null;
        }
    }
}
