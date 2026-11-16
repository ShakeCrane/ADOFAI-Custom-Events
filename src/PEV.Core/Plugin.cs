using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using ADOFAI;
using PEV.Loading;
using PEV.Registry;
using PEV.Storage;
using UnityEngine;
using UnityModManagerNet;

namespace PEV.Core
{
    /// <summary>
    /// P0 scope: prove the storage loop is lossless end to end.
    ///   .event -> registry -> EditorComment -> reopen -> virtual events
    /// No scripting yet; that is P1.
    /// </summary>
    public static class Plugin
    {
        private const string HarmonyId = "org.shakecrane.pev.core";

        public static UnityModManager.ModEntry Mod;
        public static string EventsDirectory;
        public static bool Verbose = true;
        internal static EventGlobalSettings EventSettings = new EventGlobalSettings();
        private static string _settingsPath;

        private static string _logPath;

        public static bool Load(UnityModManager.ModEntry entry)
        {
            Mod = entry;
            _logPath = Path.Combine(entry.Path, "pev-core.log");
            TryResetLog();

            EventSettings = new EventGlobalSettings();
            _settingsPath = Path.Combine(entry.Path, "event-settings.json");
            try { EventSettings.Load(_settingsPath); }
            catch (Exception e) { Log("event settings load failed; original file preserved: " + e.Message); }
            entry.OnGUI = OnGUI;
            entry.OnSaveGUI = OnSaveGUI;

            EventsDirectory = Path.Combine(entry.Path, "Events");
            Directory.CreateDirectory(EventsDirectory);

            var report = EventLoader.Scan(new[] { EventsDirectory });
            // The placeholder type is built in, never shipped as a package: it exists so
            // unresolved payloads stay visible on the track instead of quietly vanishing.
            var builtIn = new List<PEV.Model.EventManifest> { PEV.Registry.PlaceholderEvent.Build() };
            builtIn.AddRange(report.Loaded);
            EventRegistry.RegisterAll(builtIn, w => Log("registry: " + w));

            Log($"scan: {report.Summary()}");
            foreach (var m in report.Loaded)
                Log($"  {m.Id} v{m.Version} ({m.Params.Count} params) <- {Path.GetFileName(m.SourcePath)}");
            foreach (var e in report.Errors)
                Log("  ERROR " + e);

            new Harmony(HarmonyId).PatchAll(Assembly.GetExecutingAssembly());
            Diagnostics.P0Harness.Configure(entry.Path);
            Diagnostics.P1Harness.Configure(entry.Path);
            Diagnostics.P2Harness.Configure(entry.Path);
            Diagnostics.P3Harness.Configure(entry.Path);
            Diagnostics.P4Harness.Configure(entry.Path);
            Diagnostics.PN1Harness.Configure(entry.Path);
            Diagnostics.ReviewHarness.Configure(entry.Path);
            Log("harmony patched");
            return true;
        }

        private static void OnGUI(UnityModManager.ModEntry entry)
        {
            DrawEventSettings();
        }

        internal static void DrawEventSettings()
        {
            string locale;
            try { locale = RDString.language.ToString(); } catch { locale = "English"; }
            foreach (var manifest in EventRegistry.All.OrderBy(m => m.LocaleName(locale), StringComparer.Ordinal))
            {
                if (manifest.GlobalSettings.Count == 0) continue;
                GUILayout.Label(manifest.LocaleName(locale));
                foreach (var setting in manifest.GlobalSettings)
                {
                    bool current = EventSettings.Get(manifest, setting);
                    bool selected = GUILayout.Toggle(current, setting.Label(locale));
                    if (selected != current)
                    {
                        try { EventSettings.Set(manifest, setting, selected); }
                        catch (Exception e) { Log("event setting change failed: " + e.Message); }
                    }
                }
            }
        }

        private static void OnSaveGUI(UnityModManager.ModEntry entry)
        {
            try { EventSettings.Save(_settingsPath); }
            catch (Exception e) { Log("settings save failed: " + e.Message); }
        }

        public static void Log(string message)
        {
            try
            {
                if (_logPath != null)
                    File.AppendAllText(_logPath, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message + Environment.NewLine);
            }
            catch { }
            if (Verbose) UnityEngine.Debug.Log("[PEV] " + message);
        }

        private static void TryResetLog()
        {
            try { if (_logPath != null) File.WriteAllText(_logPath, ""); } catch { }
        }
    }

    [HarmonyPatch(typeof(ADOStartup), "SetupLevelEventsInfo")]
    public static class PublishPatch
    {
        public static void Postfix()
        {
            try
            {
                EventRegistry.PublishToGame(Plugin.Log);
            }
            catch (Exception e)
            {
                Plugin.Log("publish failed: " + e);
            }
        }
    }

    [HarmonyPatch(typeof(scnEditor), "LoadEditorProperties")]
    public static class IconPatch
    {
        /// <summary>
        /// Runs before the editor builds its inspectors.
        ///
        /// Two things have to happen here rather than later:
        ///   - icons, because the palette indexes GCS.levelEventIcons unconditionally and a
        ///     missing key aborts the whole property load;
        ///   - decoration parameters, because PropertiesPanel.Init walks propertiesInfo
        ///     exactly once. A parameter added after that exists in the metadata but never
        ///     gets a control, so the author would see the rows only after a level reload.
        /// </summary>
        public static void Prefix()
        {
            try { EventRegistry.EnsureAllIcons(Plugin.Log); }
            catch (Exception e) { Plugin.Log("icon pass failed: " + e); }

            try { Registry.DecorationParams.Publish(Plugin.Log); }
            catch (Exception e) { Plugin.Log("decoration param publish failed: " + e); }
        }
    }

    [HarmonyPatch(typeof(LevelData), "Decode")]
    public static class DecodePatch
    {
        public static void Postfix(LevelData __instance)
        {
            try
            {
                LevelStorage.Clear();
                Editor.EditorReport.Reset();

                var summary = LevelStorage.Restore(__instance);
                Plugin.Log($"decode: {summary}");
                foreach (var p in summary.Problems) Plugin.Log("  " + p);
                Editor.EditorReport.AddFrom(summary);

                // Arm the runtime for whatever just loaded. Rebuilding is cheap and
                // makes reloads idempotent.
                Scripting.RuntimeHost.Ensure().Rebuild(__instance);
            }
            catch (Exception e)
            {
                Plugin.Log("decode failed: " + e);
            }
        }
    }

    [HarmonyPatch(typeof(scnEditor), "Play")]
    public static class EditorPlayPatch
    {
        /// <summary>
        /// Re-arm the runtime from the editor's current event list.
        ///
        /// Decode happens when the level is loaded, but an author places events after that
        /// and then presses play. Without rebuilding here, anything placed in the editor
        /// silently does nothing when tested — the first thing a chart author tries.
        /// </summary>
        public static void Prefix()
        {
            try
            {
                Scripting.RuntimeHost.RebuildFromEditor();
                Plugin.Log("runtime re-armed for editor playback");
            }
            catch (Exception e) { Plugin.Log("play re-arm failed: " + e); }
        }
    }

    [HarmonyPatch(typeof(scnEditor), "Start")]
    public static class EditorReadyPatch
    {
        /// <summary>
        /// The editor is live by the time this runs, so ShowNotification is available.
        /// Reporting here (rather than during decode) also means the message is shown once
        /// per open instead of once per internal decode pass.
        /// </summary>
        public static void Postfix()
        {
            try
            {
                foreach (var line in Editor.EditorReport.Details()) Plugin.Log("  " + line);
                Editor.EditorReport.ShowIfNeeded();
            }
            catch (Exception e) { Plugin.Log("report failed: " + e); }
        }
    }

    [HarmonyPatch(typeof(LevelData), "EncodeToDictionary")]
    public static class EncodePatch
    {
        /// <summary>
        /// Writes every decoration payload back onto its row before the encoder serialises it.
        ///
        /// This has to be a prefix: LevelData builds the decorations array inside
        /// EncodeToDictionary, so by the time the postfix runs the JSON has already been
        /// assembled from whatever the rows held a moment earlier.
        /// </summary>
        public static void Prefix(LevelData __instance)
        {
            try { LevelStorage.SyncDecorations(__instance); }
            catch (Exception e) { Plugin.Log("decoration sync before encode failed: " + e); }
        }

        public static void Postfix(LevelData __instance, ref Dictionary<string, object> __result)
        {
            try
            {
                if (__result == null) return;
                var actions = __result.TryGetValue("actions", out var raw)
                    ? raw as List<Dictionary<string, object>>
                    : null;
                var summary = LevelStorage.Bake(__instance, actions);
                if (summary.Written > 0 || summary.RemovedStale > 0)
                    Plugin.Log($"encode: {summary}");
            }
            catch (Exception e)
            {
                Plugin.Log("encode failed (original data preserved by vanilla path): " + e);
            }
        }
    }
}
