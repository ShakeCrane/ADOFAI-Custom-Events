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
    // Read native decorations after the game's LateUpdate has applied parallax.
    [DefaultExecutionOrder(1000)]
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
        private NoiseFieldGpuDriver _gpuDriver = new NoiseFieldGpuDriver();
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
#if PEV_DIAGNOSTICS
            LegacyFieldTextureSet.Clear();
            ReferenceNoiseTextures.Clear();
#endif
            if (_instance == null) { ShaderBundleCache.Clear(); PackageImageCache.Clear(); return; }
            _instance.Teardown();
            ShaderBundleCache.Clear();
            PackageImageCache.Clear();
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
                // Called from the Play prefix, which runs before playMode is set inside
                // scnEditor.Play. Asking ShouldDescribeFromRows here would answer "idle" and
                // register nothing for the whole of playback, so this path states its intent.
                Ensure().Rebuild(level, true);
            }
            catch (Exception e)
            {
                Plugin.Log("runtime rebuild from editor failed: " + e.Message);
            }
        }

        /// <summary>Rebuilds instances for the level now loaded. Safe to call repeatedly.</summary>
        public void Rebuild(LevelData level) => Rebuild(level, NoiseFieldRenderer.ShouldDescribeFromRows());

        /// <summary>
        /// Rebuilds instances, optionally registering decoration-backed fields from their rows.
        ///
        /// describeFields is false in the editor's idle state, where the preview component
        /// already draws each row from its live values; registering again would draw the same
        /// rectangle twice with different parameters.
        /// </summary>
        public void Rebuild(LevelData level, bool describeFields)
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
                        if (ev == null || !ev.active) continue;
                        if (!LevelStorage.TryGetData(ev, out var data)) continue;
                        if (data.IsPlaceholder) continue;
                        if (!EventRegistry.TryGetById(data.Id, out var manifest)) continue;

                        Arm(ev, data, manifest, floors, describeFields);
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
                if (ev == null || !ev.active) continue;
                if (!LevelStorage.TryGetData(ev, out var data)) continue;

                // A placeholder stands for a package we could not resolve; there is no
                // script to run, and running the placeholder's own would be wrong.
                if (data.IsPlaceholder) continue;

                if (!EventRegistry.TryGetById(data.Id, out var manifest)) continue;

                Arm(ev, data, manifest, floors, describeFields);
                built++;
            }

            _live = built > 0;
            Plugin.Log($"runtime: {built} instance(s) armed");
        }

        private void Arm(LevelEvent ev, EventInstanceData data, PEV.Model.EventManifest manifest, List<scrFloor> floors,
            bool describeFields)
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
                EntryTimeResolved = false,
                EventOrder = _instances.Count
            };

            // A decoration row is a decoration: bind the instance to it so every frame reads
            // the live component instead of the values frozen at the last save. The scope has
            // to be the same string the script host stamps into its calls, which is the
            // instance's own key.
            if (ev.IsDecoration)
            {
                NoiseFieldRenderer.Bind(inst.Key, ev);

                // And register the field now, not when the song reaches this tile. A
                // decoration exists from the moment the level loads; a tag-addressed state
                // event at time 0 must be able to find a field whose row sits on a later
                // floor. The placement tile is where the event sits, not when it appears.
                if (describeFields && NoiseFieldRenderer.IsFieldEvent(manifest))
                    NoiseFieldRenderer.Describe(inst.Key, manifest.FieldPreviewKey, ev, data);
            }

            _instances.Add(inst);
        }

        private double _lastSongTime = double.NegativeInfinity;

        private void LateUpdate()
        {
            bool playing = _live && (ADOBase.editor == null || ADOBase.editor.playMode);
            double time = _lastSongTime;
            _gpuDriver.Tick(playing, time);
        }

        private void Update()
        {
            // Upload a slice of any finished bake, before this frame's rendering. Texture
            // creation is main-thread-only, so this is the only place it can be bounded; the
            // pixel work itself runs on a background thread. Kept outside the early-outs
            // below so a bake still finishes while the song is paused or nothing is playing.
            NoiseTextureFactory.Pump(1.5f);

            // Fields registered at level init follow their decoration until a script takes
            // them over. Outside the early-outs below so a paused or not-yet-started song
            // still mirrors an editor drag or a MoveDecorations tween.
            try { NoiseFieldRenderer.Refresh(); } catch { }

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
                    if (inst.Phase != InstancePhase.Dormant) { inst.Reset(); reset++; }
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
                    if (inst.Phase == InstancePhase.Dormant && advancing)
                        inst.ResolveEntryTime();
                    if (inst.Phase == InstancePhase.Dormant && advancing && songTime >= inst.EntryTime)
                        inst.Begin();

                    if (inst.Phase == InstancePhase.Running)
                        inst.Tick(songTime, advancing);
                }
                catch (Exception e)
                {
                    Plugin.Log($"instance {inst.Key} threw: {e}");
                    inst.End();
                    inst.Phase = InstancePhase.Faulted;
                }
            }
        }

        public void Teardown()
        {
            _gpuDriver.Dispose();
            _gpuDriver = new NoiseFieldGpuDriver();
            foreach (var inst in _instances)
            {
                try { inst.End(); } catch { }
            }
            _instances.Clear();
            _live = false;
            _lastSongTime = double.NegativeInfinity;

            // Bindings point at LevelEvent rows that the next decode replaces. Clearing them
            // is what stops a stale reference from resurrecting a destroyed decoration.
            try { NoiseFieldRenderer.UnbindAll(); } catch { }

            // Belt and braces: even a script that leaked objects or crashed leaves nothing.
            try { GameBridge.DestroyAll(); } catch { }
        }

        private void OnDestroy()
        {
            if (_instance != this) return;
            Teardown();
            _instance = null;
        }
    }
}
