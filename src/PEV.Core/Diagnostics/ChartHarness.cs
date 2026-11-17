using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using ADOFAI;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PEV.Editor;
using PEV.Registry;
using PEV.Storage;
using PEV.Util;
using UnityEngine;

namespace PEV.Diagnostics
{
    public static class ChartHarness
    {
        private static string _result, _root;
        private static bool _ran;
        public static void Configure(string modDir)
        {
            if (!File.Exists(Path.Combine(modDir, "chart-test.txt"))) return;
            _root = Environment.GetEnvironmentVariable("PEV_CHART_TEST_ROOT");
            if (string.IsNullOrEmpty(_root)) throw new InvalidOperationException("Missing chart test root");
            _result = Path.Combine(modDir, "chart-results.txt"); File.WriteAllText(_result, "");
        }
        private static void Row(string name, bool ok, string detail = null) =>
            File.AppendAllText(_result, name + "\t" + (ok ? "PASS" : "FAIL") + (detail == null ? "" : "\t" + detail) + "\n");

        [HarmonyPatch(typeof(scnLevelSelect), "Start")]
        public static class AutoOpen
        {
            public static void Postfix()
            {
                if (_result == null || _ran) return;
                scnEditor.levelToOpenOnLoad = Path.Combine(_root, "A", "level.adofai");
                ADOBase.loader.LoadScene("scnEditor");
            }
        }
        [HarmonyPatch(typeof(scnEditor), "Start")]
        public static class Start
        {
            public static void Postfix(scnEditor __instance)
            {
                if (_result == null || _ran) return;
                _ran = true; __instance.StartCoroutine(RunSafe(__instance));
            }
        }
        private static IEnumerator RunSafe(scnEditor editor)
        {
            var probe = Run(editor);
            while (true)
            {
                bool next;
                try { next = probe.MoveNext(); }
                catch (Exception e) { Row("EXCEPTION", false, e.ToString().Replace('\n', ' ')); break; }
                if (!next) break;
                yield return probe.Current;
            }
            File.AppendAllText(_result, "DONE\tok\n");
        }
        private static IEnumerator Open(scnEditor editor, string path)
        {
            editor.OpenLevel(path);
            float deadline = Time.realtimeSinceStartup + 20f;
            do { yield return null; }
            while ((editor.isLoading || scnGame.instance == null ||
                !string.Equals(scnGame.instance.levelPath, path, StringComparison.OrdinalIgnoreCase)) && Time.realtimeSinceStartup < deadline);
            if (scnGame.instance == null || !string.Equals(scnGame.instance.levelPath, path, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Could not open chart " + path);
            yield return null;
        }
        private static IEnumerator Run(scnEditor editor)
        {
            string a = Path.Combine(_root, "A", "level.adofai"), b = Path.Combine(_root, "B", "level.adofai"),
                c = Path.Combine(_root, "C", "level.adofai"), d = Path.Combine(_root, "D", "level.adofai"),
                e = Path.Combine(_root, "E", "level.adofai");
            float deadline = Time.realtimeSinceStartup + 20f;
            while ((editor.isLoading || scnGame.instance == null ||
                !string.Equals(scnGame.instance.levelPath, a, StringComparison.OrdinalIgnoreCase)) && Time.realtimeSinceStartup < deadline)
                yield return null;
            yield return null;
            bool hasField = EventRegistry.TryGetById("pev.noise-field", out var field);
            bool hasState = EventRegistry.TryGetById("pev.noise-field-state", out var state);
            bool loaded = hasField && hasState &&
                string.Equals(Path.GetDirectoryName(field.SourcePath), Path.GetDirectoryName(a), StringComparison.OrdinalIgnoreCase);
            Row("CHART-local-load", loaded);
            if (!loaded) yield break;
            string originalScript = PEV.Scripting.EventScriptCache.Get(field);
            var fieldType = (LevelEventType)StableId.FromEventId(field.Id);
            var stateType = (LevelEventType)StableId.FromEventId(state.Id);
            var nativePanel = editor.levelEventsPanel.panelsList.FirstOrDefault(p => p.levelEventType == LevelEventType.AddDecoration);
            bool buttons = editor.eventButtons.Values.SelectMany(x => x).Any(x => x.type == fieldType) &&
                editor.eventButtons.Values.SelectMany(x => x).Any(x => x.type == stateType);
            bool panels = nativePanel != null && nativePanel.properties.ContainsKey("invert") &&
                editor.levelEventsPanel.panelsList.Any(p => p.levelEventType == stateType);
            Row("CHART-dynamic-editor", buttons && panels, "buttons=" + buttons + " panels=" + panels);

            EditorTestHelpers.SelectFloor(editor, 1);
            editor.AddEventAtSelected(fieldType);
            editor.AddEventAtSelected(stateType);
            yield return null;
            bool placed = editor.levelData.decorations.Any(x => LevelStorage.TryGetData(x, out var data) && data.Id == field.Id) &&
                editor.levelData.levelEvents.Any(x => LevelStorage.TryGetData(x, out var data) && data.Id == state.Id);
            Row("CHART-place-events", placed);
            string serialized = editor.levelData.Encode();
            var document = JObject.Parse(serialized);
            var dependencies = document[ChartDependencies.Key] as JArray;
            bool declared = dependencies != null && new[] { field.Id, state.Id }.All(id => dependencies.OfType<JObject>()
                .Any(d => d.Value<string>("id") == id && d.Value<string>("file") == "NoiseField.event"));
            Row("CHART-dependency-write", declared, dependencies?.ToString(Formatting.None));
            var copy = editor.levelData.Copy();
            var copied = JObject.Parse(copy.Encode())[ChartDependencies.Key] as JArray;
            Row("CHART-undo-copy", copied != null && copied.Count == dependencies?.Count);
            File.WriteAllText(a, serialized);

            var export = AccessTools.Method(typeof(scnEditor), "GetExportLevelFiles",
                new[] { typeof(string), typeof(bool).MakeByRefType(), typeof(DLCManager[]).MakeByRefType() });
            string temp = Path.Combine(_root, "export-temp"); Directory.CreateDirectory(temp);
            object[] arguments = { temp, false, null };
            var files = (List<string>)export.Invoke(editor, arguments);
            string eventPath = Path.Combine(Path.GetDirectoryName(a), "NoiseField.event");
            bool included = files.Any(p => string.Equals(p, eventPath, StringComparison.OrdinalIgnoreCase));
            bool markerOmitted = !files.Any(p => p.Replace('\\', '/').EndsWith("/pev/field", StringComparison.OrdinalIgnoreCase));
            string zip = Path.Combine(_root, "export.adozip");
            if (included && files.All(File.Exists)) ZipUtils.Zip(zip, files.Distinct().ToArray());
            bool archive = false;
            if (File.Exists(zip)) using (var zipped = ZipFile.OpenRead(zip)) archive = zipped.Entries.Any(e => e.FullName == "NoiseField.event");
            Row("CHART-export-package", included && markerOmitted && archive,
                "included=" + included + " markerOmitted=" + markerOmitted + " archive=" + archive);
            Row("CHART-export-keeps-registry", EventRegistry.TryGetById(field.Id, out var afterExport) &&
                afterExport.SourcePath == field.SourcePath && editor.levelData.decorations.Any(x => LevelStorage.TryGetData(x, out _)));

            File.WriteAllText(b, serialized);
            yield return Open(editor, b);
            bool missing = !EventRegistry.TryGetById(field.Id, out _) && !EventRegistry.TryGetById(state.Id, out _);
            bool missingMessage = EditorReport.Details().Any(x => x.Contains("NoiseField.event"));
            deadline = Time.realtimeSinceStartup + 4f;
            while (Time.realtimeSinceStartup < deadline &&
                (Notification.instance.text == null || !Notification.instance.text.text.Contains("NoiseField.event"))) yield return null;
            bool nativeNotice = Notification.instance.text != null && Notification.instance.text.text.Contains("NoiseField.event");
            Row("CHART-missing-no-global-fallback", missing && missingMessage && nativeNotice,
                "missing=" + missing + " reported=" + missingMessage + " nativeNotice=" + nativeNotice);
            bool staleButtons = editor.eventButtons.Values.SelectMany(x => x).Any(x => x.type == fieldType || x.type == stateType);
            nativePanel = editor.levelEventsPanel.panelsList.FirstOrDefault(p => p.levelEventType == LevelEventType.AddDecoration);
            Row("CHART-switch-cleanup", !staleButtons && nativePanel != null && !nativePanel.properties.ContainsKey("noiseFrameCount"));
            var missingSaved = JObject.Parse(editor.levelData.Encode());
            Row("CHART-missing-preserved", missingSaved[ChartDependencies.Key] is JArray missingRefs && missingRefs.Count == dependencies.Count &&
                missingSaved["decorations"] is JArray missingRows && missingRows.OfType<JObject>().Any(r => (r.Value<string>("maskingTarget") ?? "").StartsWith("pev1:")));

            var legacy = JObject.Parse(serialized); legacy.Remove(ChartDependencies.Key);
            File.WriteAllText(c, legacy.ToString(Formatting.Indented));
            yield return Open(editor, c);
            bool localC = EventRegistry.TryGetById(field.Id, out var cField) && cField.Version == "1.33.1" &&
                string.Equals(Path.GetDirectoryName(cField.SourcePath), Path.GetDirectoryName(c), StringComparison.OrdinalIgnoreCase);
            bool restored = editor.levelData.decorations.Any(x => LevelStorage.TryGetData(x, out var data) && data.Id == field.Id);
            var upgraded = JObject.Parse(editor.levelData.Encode())[ChartDependencies.Key] as JArray;
            string switchedScript = localC ? PEV.Scripting.EventScriptCache.Get(cField) : null;
            bool scriptSwitched = originalScript != null && switchedScript != null && originalScript != switchedScript &&
                switchedScript.Contains("CHART_TEST_VARIANT_C");
            Row("CHART-legacy-and-switch", localC && restored && upgraded != null && upgraded.Count >= 2 && scriptSwitched,
                "local=" + localC + " restored=" + restored + " scriptSwitched=" + scriptSwitched);

            var unsafeState = ChartDependencies.Read(new Dictionary<string, object> {
                [ChartDependencies.Key] = new[] { new Dictionary<string, object> { ["id"] = "x", ["file"] = "../global.event" } } });
            Row("CHART-invalid-path-rejected", unsafeState.Error != null && unsafeState.InvalidRaw != null);

            yield return Open(editor, d);
            bool declarationNotice = EditorReport.Details().Any(x => x.Contains("Missing.event"));
            var declarationSaved = JObject.Parse(editor.levelData.Encode())[ChartDependencies.Key] as JArray;
            Row("CHART-declaration-only", declarationNotice && declarationSaved != null &&
                declarationSaved.OfType<JObject>().Any(x => x.Value<string>("file") == "Missing.event"));
            yield return Open(editor, e);
            bool clean = !EventRegistry.All.Any() && JObject.Parse(editor.levelData.Encode())[ChartDependencies.Key] == null &&
                !editor.eventButtons.Values.SelectMany(x => x).Any(x => x.type == fieldType || x.type == stateType);
            Row("CHART-empty-is-inert", clean);
        }
    }
}
