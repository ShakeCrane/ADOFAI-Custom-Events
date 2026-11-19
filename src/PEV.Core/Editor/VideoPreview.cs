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
    /// Shows video rows in the editor canvas before the author presses play.
    ///
    /// A decoration is visible the moment it is placed; a custom event that renders itself is
    /// not, because its script only runs under the song clock. For video that gap is the whole
    /// authoring experience — an author cannot position a rectangle they cannot see. This
    /// mirrors every video row into a surface showing the clip's first frame, re-reads the row
    /// every frame so panel edits and gizmo drags apply immediately, and steps aside while
    /// playback is running so the real instance owns the screen.
    ///
    /// Deliberately not a script runner: driving the package's update() from an editor clock
    /// would give authors a second, wrong time base. Only the host-rendered surface previews.
    /// </summary>
    public class VideoPreview : MonoBehaviour
    {
        private const string PreviewScopePrefix = "editorvideo/";

        private static VideoPreview _instance;
        private readonly Dictionary<string, string> _seen = new Dictionary<string, string>(StringComparer.Ordinal);

        [HarmonyPatch(typeof(scnEditor), "Start")]
        public static class AttachPatch
        {
            public static void Postfix(scnEditor __instance)
            {
                if (__instance == null || _instance != null) return;
                try
                {
                    _instance = __instance.gameObject.AddComponent<VideoPreview>();
                }
                catch (Exception e)
                {
                    PEV.Core.Plugin.Log("video preview attach failed: " + e.Message);
                }
            }
        }

        private void Update()
        {
            var editor = ADOBase.editor;
            if (editor == null) return;

            // Playback has its own clock-aligned instances. Leaving the preview registered
            // would draw the same clip twice at different times.
            if (editor.playMode)
            {
                Clear();
                return;
            }

            var live = new HashSet<string>(StringComparer.Ordinal);

            var level = editor.levelData;
            var rows = level != null ? level.decorations : null;
            if (rows != null)
            {
                foreach (var ev in rows)
                {
                    if (ev == null || !ev.active) continue;
                    if (!LevelStorage.TryGetData(ev, out var data)) continue;
                    if (!EventRegistry.TryGetById(data.Id, out var manifest)) continue;
                    if (manifest.HostPrimitive != "fx.video") continue;

                    string uid = data.Uid;
                    live.Add(uid);
                    Preview(ev, manifest, data, uid);
                }
            }

            // A deleted or undone row is gone from the list entirely; drop its surface.
            var stale = new List<string>();
            foreach (var key in _seen.Keys) if (!live.Contains(key)) stale.Add(key);
            foreach (var key in stale)
            {
                var args = new Dictionary<string, object> { { "__instance", PreviewScopePrefix + key } };
                VideoSurface.Destroy(_seen[key], args, out _);
                _seen.Remove(key);
            }
        }

        private void Preview(LevelEvent ev, PEV.Model.EventManifest manifest, EventInstanceData data, string uid)
        {
            try
            {
                var args = new Dictionary<string, object>
                {
                    { "__instance", PreviewScopePrefix + uid },
                    { "__event", ev },
                    { "__manifest", manifest },
                    { "__floor", ev.floor }
                };

                // Read the row's current values, not the load-time snapshot: this is what makes
                // a path typed into the panel appear without reopening the level.
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

                // The editor has no song clock, so the surface shows the first frame of the
                // clip. The author is placing a picture, not previewing a performance.
                args["time"] = TrimStart(args);
                args["playing"] = false;
                args["loop"] = false;

                // The package declares this input as `mode` and forwards it to the host as
                // `fit`; the preview sees the authored parameter names, so the same translation
                // has to happen here or the editor draws a stretched picture while playback
                // draws a fitted one.
                if (args.TryGetValue("mode", out var mode) && mode != null)
                    args["fit"] = mode.ToString();

                VideoSurface.Set("video", args, out string error);
                if (!string.IsNullOrEmpty(error)) PEV.Core.Plugin.Log("video preview: " + error);
                _seen[uid] = "video";
            }
            catch (Exception e)
            {
                PEV.Core.Plugin.Log("video preview failed: " + e.Message);
            }
        }

        private static double TrimStart(Dictionary<string, object> args)
        {
            object raw;
            if (args.TryGetValue("trimStart", out raw) && raw != null)
            {
                try { return Math.Max(0, Convert.ToDouble(raw)); } catch { }
            }
            return 0;
        }

        private void Clear()
        {
            if (_seen.Count == 0) return;
            foreach (var pair in _seen)
            {
                var args = new Dictionary<string, object> { { "__instance", PreviewScopePrefix + pair.Key } };
                VideoSurface.Destroy(pair.Value, args, out _);
            }
            _seen.Clear();
        }

        private void OnDestroy() => Clear();

        /// <summary>Unity structs and enums do not survive JSON-style parsing; flatten once.</summary>
        private static object Normalize(object value)
        {
            if (value is Vector2 v2) return new[] { v2.x, v2.y };
            if (value is Color c) return ColorUtility.ToHtmlStringRGB(c);
            if (value is Enum e) return e.ToString();
            return value;
        }
    }
}
