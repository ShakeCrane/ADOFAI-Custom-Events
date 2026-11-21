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
        private HashSet<string> _seen = new HashSet<string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> LegacyBindings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "file", "file" }, { "trimStart", "trimStart" }, { "trimEnd", "trimEnd" }, { "mode", "fit" }
        };

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
                    if (manifest.HostPrimitive != "fx.video" ||
                        (manifest.SchemaVersion >= 5 && manifest.VideoPreviewBindings.Count == 0)) continue;

                    string scope = PreviewScopePrefix + data.Id + "#" + data.Uid;
                    live.Add(scope);
                    Preview(ev, manifest, data, scope);
                }
            }

            // A deleted or undone row is gone from the list entirely; drop its surface.
            foreach (var scope in _seen) if (!live.Contains(scope)) VideoSurface.DestroyScope(scope);
            _seen = live;
        }

        private static void Preview(LevelEvent ev, PEV.Model.EventManifest manifest, EventInstanceData data, string scope)
        {
            try
            {
                var args = new Dictionary<string, object>
                {
                    { "__instance", scope },
                    { "__event", ev },
                    { "__manifest", manifest },
                    { "playing", false },
                    { "muted", true }
                };

                // Read the row's current values, not the load-time snapshot: this is what makes
                // a path typed into the panel appear without reopening the level.
                var bindings = manifest.SchemaVersion >= 5 ? manifest.VideoPreviewBindings : LegacyBindings;
                foreach (var binding in bindings)
                {
                    if (!data.Effective.TryGetValue(binding.Key, out var value)) continue;
                    try
                    {
                        object liveValue = ev.Get<object>(binding.Key);
                        if (liveValue != null) value = liveValue;
                    }
                    catch { }
                    args[binding.Value] = value is Enum choice ? choice.ToString() : value;
                }

                if (!args.ContainsKey("time") && args.TryGetValue("trimStart", out var start))
                    args["time"] = start;
                VideoSurface.Set("preview", args, out _);
            }
            catch (Exception e)
            {
                PEV.Core.Plugin.Log("video preview failed: " + e.Message);
            }
        }

        private void Clear()
        {
            foreach (var scope in _seen) VideoSurface.DestroyScope(scope);
            _seen.Clear();
        }

        private void OnDestroy() => Clear();

    }
}
