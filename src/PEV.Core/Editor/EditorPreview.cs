using System;
using System.Collections.Generic;
using ADOFAI;
using HarmonyLib;
using PEV.Registry;
using PEV.Scripting;
using PEV.Storage;
using UnityEngine;

namespace PEV.Editor
{
    /// <summary>
    /// Draws custom effects in the editor canvas, not just during playback.
    ///
    /// The runtime arms instances from LevelData.Decode and scnEditor.Play, so a placed event
    /// is invisible until the author presses play — unlike native decorations, which appear
    /// the moment they are placed. This component closes that gap for events that render
    /// themselves through the field primitive: it mirrors every event of ours in the level
    /// into a live field, re-reads its properties every frame (so panel edits apply
    /// immediately), and hides itself while playback is running so the real instance owns
    /// the screen.
    ///
    /// Deliberately not a general script runner: a preview driven by Time.time would give
    /// authors a second, wrong clock and turn every update() into a physics question. Only
    /// the host-rendered primitives preview.
    /// </summary>
    public class EditorPreview : MonoBehaviour
    {
        private const string PreviewScopePrefix = "editor/";

        private static EditorPreview _instance;
        private readonly Dictionary<string, string> _seen = new Dictionary<string, string>(StringComparer.Ordinal);
        private double _clock;

        [HarmonyPatch(typeof(scnEditor), "Start")]
        public static class AttachPatch
        {
            public static void Postfix(scnEditor __instance)
            {
                if (__instance == null || _instance != null) return;
                try
                {
                    _instance = __instance.gameObject.AddComponent<EditorPreview>();
                }
                catch (Exception e)
                {
                    PEV.Core.Plugin.Log("editor preview attach failed: " + e.Message);
                }
            }
        }

        private void Update()
        {
            var editor = ADOBase.editor;
            if (editor == null) return;

            // Upload a slice of any finished bake. Texture creation is main-thread-only, so
            // this is the frame-time budget for it: enough per frame to finish a set in a
            // fraction of a second, small enough that the frame it happens in is not felt.
            NoiseTextureFactory.Pump(1.5f);

            // Playback has its own clock-aligned instances; two renders would double the
            // brightness and disagree at pause.
            if (editor.playMode)
            {
                Clear();
                return;
            }

            _clock += Time.unscaledDeltaTime;

            // Playback leaves its described entries behind when it stops, and this preview
            // then re-registers the same rows under its own keys. Dropping the described
            // copies is what keeps a stopped field from being drawn twice with different
            // parameters. The call is a no-op once they are gone.
            NoiseFieldRenderer.DropDescribed();

            var live = new HashSet<string>(StringComparer.Ordinal);

            // Decorations first: a decoration-backed event is a row in level.decorations, and
            // the editor's `events` list does not contain it at all. Iterating only that list
            // is why a placed field stayed invisible until the author hit play.
            var level = editor.levelData;
            var rows = level != null ? level.decorations : null;
            if (rows != null)
            {
                foreach (var ev in rows)
                {
                    if (ev == null || !ev.active) continue;
                    if (!LevelStorage.TryGetData(ev, out var data)) continue;
                    if (!EventRegistry.TryGetById(data.Id, out var manifest)) continue;
                    if (!DeclaresField(manifest)) continue;

                    string key = data.Uid;
                    live.Add(key);
                    ApplyInstance(ev, manifest, data, key);
                }
            }

            var events = editor.events;
            if (events != null)
            {
                foreach (var ev in events)
                {
                    if (ev == null || !ev.active) continue;
                    if (!LevelStorage.TryGetData(ev, out var data)) continue;
                    if (!EventRegistry.TryGetById(data.Id, out var manifest)) continue;
                    if (!DeclaresField(manifest)) continue;

                    string key = data.Uid;
                    live.Add(key);
                    ApplyInstance(ev, manifest, data, key);
                }
            }

            // Events deleted, undone away, or reverted must not leave a field behind: they
            // are no longer in the list at all.
            var stale = new List<string>();
            foreach (var key in _seen.Keys)
                if (!live.Contains(key)) stale.Add(key);
            foreach (var key in stale)
            {
                var args = new Dictionary<string, object> { { "__instance", PreviewScopePrefix + key } };
                NoiseFieldRenderer.Destroy(_seen[key], args, out _);
                _seen.Remove(key);
            }
        }

        private void ApplyInstance(LevelEvent ev, PEV.Model.EventManifest manifest, EventInstanceData data, string uid)
        {
            try
            {
                var args = new Dictionary<string, object>
                {
                    { "__instance", PreviewScopePrefix + uid },
                    { "__floor", ev.floor },
                    // The row itself: the renderer reads geometry, colour, opacity and tag off
                    // the live scrDecoration. In the editor there is no EventInstance, so the
                    // event object is what carries the binding.
                    { "__event", ev },
                    { "__manifest", manifest },
                    { "key", manifest.FieldPreviewKey }
                };

                // Read the event's current values, not the load-time snapshot: this is what
                // makes dragging a slider or typing a number update the canvas immediately.
                foreach (var kv in data.Effective)
                {
                    object value = kv.Value;
                    try
                    {
                        object liveValue = ev.Get<object>(kv.Key);
                        if (liveValue != null) value = liveValue;
                    }
                    catch { }
                    args[kv.Key] = Normalize(value);
                }

                args["shape"] = EnumName(ArgString(args, "shape", "Rect"), "Rect");
                NoiseFieldRenderer.ApplyFieldBindings(manifest, args);

                // relativeTo and anchor are gone on purpose: they are decoration properties
                // now, and the renderer reads them off the row rather than from this call.

                int frameCount = Mathf.Clamp(Convert.ToInt32(args.TryGetValue("frameCount", out var fc) ? fc : 2), 2, 128);
                float evolve = Convert.ToSingle(args.TryGetValue("evolveSpeed", out var es) ? es : 0f);
                int frame = Mathf.FloorToInt((float)_clock * evolve) % frameCount;
                if (frame < 0) frame += frameCount;
                args["frame"] = frame;
                args["frameCount"] = frameCount;

                var move = args.TryGetValue("moveSpeed", out var mv) ? mv as float[] : null;
                if (move == null || move.Length < 2) move = new[] { 0f, 0f };
                args["uv"] = new[] { (float)_clock * move[0], (float)_clock * move[1] };

                // Opacity is the decoration's, not a parameter: the panel row the author drags
                // is the native one, and its value lives on the component the renderer reads.

                NoiseFieldRenderer.Set(manifest.FieldPreviewKey, args, out string error);
                if (!string.IsNullOrEmpty(error)) PEV.Core.Plugin.Log("preview: " + error);
                _seen[uid] = manifest.FieldPreviewKey;
            }
            catch (Exception e)
            {
                PEV.Core.Plugin.Log("preview apply failed: " + e.Message);
            }
        }

        private void Clear()
        {
            if (_seen.Count == 0) return;
            foreach (var pair in _seen)
            {
                var args = new Dictionary<string, object> { { "__instance", PreviewScopePrefix + pair.Key } };
                NoiseFieldRenderer.Destroy(pair.Value, args, out _);
            }
            _seen.Clear();
        }

        private void OnDestroy() => Clear();

        /// <summary>Only manifests whose script drives fx.field can be previewed here.</summary>
        private static bool DeclaresField(PEV.Model.EventManifest m) => NoiseFieldRenderer.IsFieldEvent(m);

        /// <summary>Unity structs and enums do not survive JSON-style parsing; flatten once.</summary>
        private static object Normalize(object value)
        {
            if (value is Vector2 v2) return new[] { v2.x, v2.y };
            if (value is Color c) return ColorUtility.ToHtmlStringRGB(c);
            if (value is Enum e) return e.ToString();
            return value;
        }

        private static string ArgString(Dictionary<string, object> args, string key, string fallback)
        {
            return args.TryGetValue(key, out var v) && v != null ? v.ToString() : fallback;
        }

        private static string EnumName(string raw, string fallback)
        {
            if (string.IsNullOrEmpty(raw)) return fallback;
            return char.IsDigit(raw[0]) ? null : raw;
        }
    }
}
