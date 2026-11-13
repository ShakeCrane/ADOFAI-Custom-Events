using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;
using UnityModManagerNet;
using ADOFAI;
using PropertyInfo = ADOFAI.PropertyInfo;

namespace Probe
{
    public enum ProbeMode { A, B }

    public static class Results
    {
        private static readonly StringBuilder Sb = new StringBuilder();
        private static string _path;
        public static string LevelPath;

        public static void Init(string modDir)
        {
            _path = Path.Combine(modDir, "stage0-results.txt");
            try { File.WriteAllText(_path, ""); } catch { }
        }

        public static void Row(string id, string value, string note = "")
        {
            string line = $"{id}\t{value}\t{note}";
            Sb.AppendLine(line);
            Debug.Log("[PEV] " + line);
            try { File.AppendAllText(_path, line + Environment.NewLine); } catch { }
        }
    }

    /// <summary>V1/V2/V3: can a numerically-named custom type be registered and resolved?</summary>
    public static class CustomType
    {
        public const int Id = 100001;
        public static readonly LevelEventType Type = (LevelEventType)Id;
        public const string Name = "100001";

        public static LevelEventInfo Info;

        public static LevelEventInfo BuildInfo()
        {
            var info = new LevelEventInfo
            {
                name = Name,
                type = Type,
                categories = new List<LevelEventCategory> { LevelEventCategory.VisualFx },
                propertiesInfo = new Dictionary<string, PropertyInfo>(),
                executionTime = LevelEventExecutionTime.OnBar,
                allowFirstFloor = true,
                isDecoration = false,
                useGroups = false,
                stretchViewport = false,
                taroDLC = false,
                pro = false
            };
            info.propertiesInfo["strength"] = new PropertyInfo(
                new Dictionary<string, object>
                {
                    { "name", "strength" },
                    { "type", "Float" },
                    { "default", 0.8f },
                    { "min", 0f },
                    { "max", 2f }
                }, info);
            return info;
        }

        public static void Register()
        {
            Info = BuildInfo();
            GCS.levelEventsInfo[Name] = Info;
            GCS.levelEventTypeString[Type] = Name;
        }
    }

    [HarmonyPatch(typeof(ADOStartup), "SetupLevelEventsInfo")]
    public static class RegisterPatch
    {
        /// <summary>Set before PatchAll to reproduce a vanilla install: nothing gets registered.</summary>
        public static bool SkipRegistration;

        public static void Postfix()
        {
            if (SkipRegistration)
            {
                Results.Row("REG", "skipped", "registration disabled for this run");
                return;
            }
            try
            {
                CustomType.Register();
                Results.Row("REG", "ok", $"registered '{CustomType.Name}' -> {(int)CustomType.Type}");

                var parsed = RDUtils.ParseEnum(CustomType.Name, LevelEventType.None);
                Results.Row("V1-ParseEnum", parsed == CustomType.Type ? "PASS" : "FAIL",
                    $"RDUtils.ParseEnum(\"{CustomType.Name}\") = {(int)parsed} (expect {CustomType.Id})");

                var et = (LevelEventType)Enum.Parse(typeof(LevelEventType), CustomType.Name);
                Results.Row("V1b-EnumParse", (int)et == CustomType.Id ? "PASS" : "FAIL", $"Enum.Parse = {(int)et}");

                bool tryParse = Enum.TryParse<LevelEventType>(CustomType.Name, out var tv);
                Results.Row("V1c-EnumTryParse", tryParse && (int)tv == CustomType.Id ? "PASS" : "FAIL",
                    $"TryParse={tryParse}, value={(int)tv}");
            }
            catch (Exception e) { Results.Row("REG", "EXCEPTION", e.ToString()); }
        }
    }

    /// <summary>V2: how does the game resolve "Enum:TypeName"?</summary>
    public static class EnumResolution
    {
        public static void Run()
        {
            var gameAsm = typeof(LevelEvent).Assembly;
            foreach (string name in new[] { "Ease", "TileRelativeTo", "SpeedType", "CameraWarpMode" })
            {
                Type viaGetType = null, viaAsm = null;
                try { viaGetType = Type.GetType(name); } catch { }
                try { viaAsm = gameAsm.GetType(name); } catch { }
                Results.Row("V2-" + name,
                    (viaGetType != null ? "PASS" : (viaAsm != null ? "ASMONLY" : "FAIL")),
                    $"Type.GetType={Describe(viaGetType)}; Assembly.GetType={Describe(viaAsm)}");
            }

            // Does the real PropertyInfo pipeline accept our custom enum name?
            try
            {
                var info = new LevelEventInfo { name = "x", type = (LevelEventType)100002, propertiesInfo = new Dictionary<string, PropertyInfo>(), categories = new List<LevelEventCategory>() };
                var pi = new PropertyInfo(new Dictionary<string, object> { { "name", "mode" }, { "type", "Enum:CameraWarpMode" } }, info);
                Results.Row("V2-CustomEnumCtor", pi.enumType == null ? "EXPECTED-NULL" : "UNEXPECTED",
                    $"PropertyType={pi.type}, enumType={Describe(pi.enumType)}");
            }
            catch (Exception e) { Results.Row("V2-CustomEnumCtor", "EXCEPTION", e.Message); }

            // The decisive test: what the game's own ctor can resolve. Type.GetType inside
            // Assembly-CSharp searches *that* assembly, not ours, so direct Type.GetType
            // calls from the probe are not representative.
            foreach (string spec in new[]
            {
                "Enum:Ease",                         // hardcoded special case
                "Enum:TileRelativeTo",               // lives in Assembly-CSharp
                "Enum:SpeedType",                    // lives in Assembly-CSharp
                "Enum:Probe.ProbeMode",              // ours, unqualified
                "Enum:Probe.ProbeMode, PEVStage0"   // ours, assembly-qualified
            })
            {
                try
                {
                    var info = new LevelEventInfo { name = "x", type = (LevelEventType)100003, propertiesInfo = new Dictionary<string, PropertyInfo>(), categories = new List<LevelEventCategory>() };
                    var pi = new PropertyInfo(new Dictionary<string, object> { { "name", "v" }, { "type", spec }, { "default", "A" } }, info);
                    Results.Row("V2b-" + spec, pi.enumType == null ? "NULL" : "PASS", Describe(pi.enumType));
                }
                catch (Exception e) { Results.Row("V2b-" + spec, "EXCEPTION", e.GetType().Name + ": " + e.Message); }
            }
        }

        private static string Describe(Type t) => t == null ? "null" : $"{t.FullName}{(t.IsEnum ? " (enum)" : "")}";
    }

    /// <summary>Core: read @PEV1 comments into virtual events on decode, write them back on encode.</summary>
    public static class Storage
    {
        public const string Prefix = "@PEV1 ";

        /// <summary>When true, storage hooks become no-ops so decode follows the vanilla path.</summary>
        public static bool VanillaMode;

        [HarmonyPatch(typeof(LevelData), "Decode")]
        public static class DecodePatch
        {
            public static void Postfix(LevelData __instance)
            {
                if (VanillaMode) return;
                try
                {
                    int restored = 0, failed = 0;
                    var toRemove = new List<LevelEvent>();

                    // Materialize first: adding to the list while enumerating it throws.
                    var snapshot = __instance.levelEvents.ToList();
                    foreach (var ev in snapshot)
                    {
                        if (ev.eventType != LevelEventType.EditorComment) continue;
                        string comment = ev.Get<string>("comment") ?? "";
                        if (!comment.StartsWith(Prefix)) continue;

                        string json = comment.Substring(Prefix.Length);
                        var dict = GDMiniJSON.Json.Deserialize(json) as Dictionary<string, object>;
                        if (dict == null) { failed++; continue; }

                        string id = dict.ContainsKey("id") ? dict["id"] as string : null;
                        if (id != "probe.test") { failed++; continue; }

                        var instance = new LevelEvent(ev.floor, CustomType.Type, CustomType.Info);
                        instance.active = ev.active;
                        if (dict.TryGetValue("p", out var pObj) && pObj is Dictionary<string, object> ps)
                            foreach (var kv in ps) instance[kv.Key] = Convert.ToSingle(kv.Value);

                        __instance.levelEvents.Add(instance);
                        toRemove.Add(ev);
                        restored++;
                    }

                    foreach (var ev in toRemove) __instance.levelEvents.Remove(ev);

                    Results.Row("V4-DecodeInject", restored > 0 ? "PASS" : "FAIL",
                        $"restored {restored} virtual events, {failed} skipped, list now {__instance.levelEvents.Count}");
                }
                catch (Exception e) { Results.Row("V4-DecodeInject", "EXCEPTION", e.ToString()); }
            }
        }

        [HarmonyPatch(typeof(LevelData), "EncodeToDictionary")]
        public static class EncodePatch
        {
            public static void Postfix(LevelData __instance, ref Dictionary<string, object> __result)
            {
                if (VanillaMode) return;
                try
                {
                    var custom = __instance.levelEvents.Where(e => e.eventType == CustomType.Type).ToList();
                    if (custom.Count == 0) { Results.Row("ENCODE", "none", "no custom events in list"); return; }

                    // vanilla encoder already wrote them; strip those entries out
                    var actions = __result["actions"] as List<Dictionary<string, object>>;
                    if (actions != null)
                        actions.RemoveAll(a =>
                        {
                            if (!a.TryGetValue("eventType", out var t)) return false;
                            if ((t as string) == CustomType.Name) return true;
                            // also drop stale @PEV1 comments so re-encoding never duplicates them
                            if ((t as string) == "EditorComment" &&
                                a.TryGetValue("comment", out var c) &&
                                (c as string ?? "").StartsWith(Prefix)) return true;
                            return false;
                        });

                    foreach (var ev in custom)
                    {
                        var p = new Dictionary<string, object>();
                        p["strength"] = ev.GetFloat("strength");
                        var payload = new Dictionary<string, object>
                        {
                            { "id", "probe.test" },
                            { "v", "1.0.0" },
                            { "uid", "u" + ev.floor },
                            { "p", p }
                        };
                        actions.Add(new Dictionary<string, object>
                        {
                            { "floor", ev.floor },
                            { "eventType", "EditorComment" },
                            { "comment", Prefix + GDMiniJSON.Json.Serialize(payload) }
                        });
                    }
                    Results.Row("ENCODE", "ok", $"rewrote {custom.Count} events as EditorComment");
                }
                catch (Exception e) { Results.Row("ENCODE", "EXCEPTION", e.ToString()); }
            }
        }
    }

    /// <summary>Open the probe level automatically, then exercise the editor.</summary>
    [HarmonyPatch(typeof(scnLevelSelect), "Start")]
    public static class AutoOpen
    {
        public static void Postfix()
        {
            try
            {
                if (string.IsNullOrEmpty(Results.LevelPath) || !File.Exists(Results.LevelPath))
                {
                    Results.Row("AUTOOPEN", "SKIP", "level missing: " + Results.LevelPath);
                    return;
                }
                scnEditor.levelToOpenOnLoad = Results.LevelPath;
                ADOBase.loader.LoadScene("scnEditor");
                Results.Row("AUTOOPEN", "ok", Results.LevelPath);
            }
            catch (Exception e) { Results.Row("AUTOOPEN", "EXCEPTION", e.ToString()); }
        }
    }

    /// <summary>
    /// V3 finding: LevelEventButton.Init does GCS.levelEventIcons[type] unconditionally,
    /// so a registered type WITHOUT an icon throws KeyNotFoundException and the whole
    /// palette build aborts. The icon is a hard dependency, not decoration.
    /// Sprites are loaded inside scnEditor.Start (local functions), before LoadEditorProperties.
    /// </summary>
    [HarmonyPatch(typeof(scnEditor), "LoadEditorProperties")]
    public static class IconPatch
    {
        public static void Prefix()
        {
            if (RegisterPatch.SkipRegistration) return;
            try
            {
                if (GCS.levelEventIcons == null)
                {
                    Results.Row("V3c-IconPatch", "TOOEARLY", "GCS.levelEventIcons is null");
                    return;
                }
                if (GCS.levelEventIcons.ContainsKey(CustomType.Type)) return;

                // borrow any existing sprite as a placeholder
                Sprite fallback = null;
                foreach (var kv in GCS.levelEventIcons)
                {
                    if (kv.Value != null) { fallback = kv.Value; break; }
                }
                if (fallback == null)
                {
                    Results.Row("V3c-IconPatch", "FAIL", "no sprite available to borrow");
                    return;
                }
                GCS.levelEventIcons[CustomType.Type] = fallback;
                Results.Row("V3c-IconPatch", "ok", $"registered placeholder icon for {(int)CustomType.Type}");
            }
            catch (Exception e) { Results.Row("V3c-IconPatch", "EXCEPTION", e.Message); }
        }
    }

    [HarmonyPatch(typeof(scnEditor), "Start")]
    public static class EditorProbe
    {
        public static void Postfix(scnEditor __instance)
        {
            ((MonoBehaviour)__instance).StartCoroutine(Run(__instance));
        }

        private static IEnumerator RunVanilla(scnEditor editor)
        {
            yield return null;
            Results.Row("C1-Floors", editor.floors.Count.ToString(), "level geometry decoded");
            Results.Row("C1-EventCount", editor.events.Count.ToString(),
                "actions surviving the vanilla decode path");

            int comments = editor.events.Count(e => e.eventType == LevelEventType.EditorComment);
            Results.Row("C1-Comments", comments.ToString(), "EditorComment entries present");

            var pev = editor.events
                .Where(e => e.eventType == LevelEventType.EditorComment)
                .Select(e => e.GetString("comment") ?? "")
                .Where(c => c.StartsWith(Storage.Prefix))
                .ToList();
            Results.Row("C1-PEVComments", pev.Count.ToString(), "our payload comments preserved verbatim");
            foreach (var c in pev) Results.Row("C1-Payload", "ok", c);

            int foreign = editor.events.Count(e =>
                e.eventType == LevelEventType.EditorComment &&
                !(e.GetString("comment") ?? "").StartsWith(Storage.Prefix));
            Results.Row("C1-ForeignComments", foreign.ToString(), "non-namespaced comments untouched");

            // Encode without touching anything: does vanilla drop or rewrite our data?
            try
            {
                var dict = editor.levelData.EncodeToDictionary();
                var actions = dict["actions"] as List<Dictionary<string, object>>;
                int encodedPev = actions.Count(a =>
                    Str(a, "eventType") == "EditorComment" && (Str(a, "comment") ?? "").StartsWith(Storage.Prefix));
                int unknown = actions.Count(a =>
                {
                    var t = Str(a, "eventType");
                    return t != null && !Enum.IsDefined(typeof(LevelEventType), t);
                });
                Results.Row("C3-ReEncodePEV", encodedPev.ToString(), "payload comments after vanilla re-encode");
                Results.Row("C3-UnknownTypes", unknown.ToString(), "must be 0 for C1");

                string vanillaOut = Path.Combine(
                    Path.GetDirectoryName(Results.LevelPath) ?? ".", "PEV-roundtrip-vanilla.adofai");
                File.WriteAllText(vanillaOut, JsonOut.Serialize(dict));
                Results.Row("C3-WroteFile", "ok", vanillaOut);
            }
            catch (Exception e) { Results.Row("C3-ReEncodePEV", "EXCEPTION", e.ToString()); }
        }

        private static IEnumerator Run(scnEditor editor)
        {
            yield return new WaitForSeconds(3f);

            if (RegisterPatch.SkipRegistration)
            {
                // Pure-vanilla reproduction: report only what a mod-less player would see.
                yield return RunVanilla(editor);
                Results.Row("DONE", "ok", "vanilla observation finished");
                yield break;
            }
            try
            {
                Results.Row("V3-EditorReady", "ok", $"floors={editor.floors.Count}, events={editor.events.Count}");

                Results.Row("V3a-InfoPresent", GCS.levelEventsInfo.ContainsKey(CustomType.Name) ? "PASS" : "FAIL",
                    "GCS.levelEventsInfo has our key");
                Results.Row("V3b-TypeStringPresent", GCS.levelEventTypeString.ContainsKey(CustomType.Type) ? "PASS" : "FAIL",
                    "GCS.levelEventTypeString has our mapping");
                Results.Row("V3c-IconPresent",
                    GCS.levelEventIcons != null && GCS.levelEventIcons.ContainsKey(CustomType.Type) ? "PASS" : "NONE",
                    "we intentionally did not register an icon");

                Results.Row("V3d-PaletteButtons", CountPaletteButtons(editor).ToString(),
                    "palette buttons built for our type");

                var custom = editor.events.Where(e => e.eventType == CustomType.Type).ToList();
                Results.Row("V4a-InEditorList", custom.Count > 0 ? "PASS" : "FAIL",
                    $"our events visible in editor.events: {custom.Count}");

                foreach (var ev in custom)
                    Results.Row("V4b-Instance", "info", $"floor={ev.floor}, strength={ev.GetFloat("strength")}");

                if (custom.Count >= 2)
                {
                    float a = custom[0].GetFloat("strength"), b = custom[1].GetFloat("strength");
                    Results.Row("V4c-InstanceIndependence", Math.Abs(a - b) > 1e-4 ? "PASS" : "FAIL",
                        $"values {a} vs {b}");
                }

                try
                {
                    var floorId = custom.FirstOrDefault()?.floor ?? 2;
                    var f = editor.floors.FirstOrDefault(x => x.seqID == floorId);
                    if (f != null)
                    {
                        editor.selectedFloors.Clear();
                        editor.selectedFloors.Add(f);
                        Results.Row("V4d-Select", "ok", $"selected floor {floorId}");
                        try
                        {
                            editor.levelEventsPanel.ShowPanel(CustomType.Type, 0);
                            Results.Row("V4e-ShowPanel", "ok", "no exception");
                        }
                        catch (Exception e) { Results.Row("V4e-ShowPanel", "EXCEPTION", e.Message); }
                    }
                    else Results.Row("V4d-Select", "FAIL", "floor not found");
                }
                catch (Exception e) { Results.Row("V4d-Select", "EXCEPTION", e.Message); }

                // round-trip: encode now, inspect, and save to a separate file for V5
                try
                {
                    var dict = editor.levelData.EncodeToDictionary();
                    var actions = dict["actions"] as List<Dictionary<string, object>>;
                    int comments = actions.Count(a => Str(a, "eventType") == "EditorComment");
                    int unknown = actions.Count(a =>
                    {
                        var t = Str(a, "eventType");
                        return t != null && !Enum.IsDefined(typeof(LevelEventType), t);
                    });
                    Results.Row("V5a-EncodedActions", "info", $"total={actions.Count}, EditorComment={comments}, unknownTypes={unknown}");

                    string outPath = Path.Combine(Path.GetDirectoryName(Results.LevelPath) ?? ".",
                        RegisterPatch.SkipRegistration ? "PEV-roundtrip-vanilla.adofai" : "PEV-roundtrip-full.adofai");
                    File.WriteAllText(outPath, JsonOut.Serialize(dict));
                    Results.Row("V5b-WroteFile", "ok", outPath);
                }
                catch (Exception e) { Results.Row("V5a-EncodedActions", "EXCEPTION", e.ToString()); }
            }
            catch (Exception e) { Results.Row("V3-EditorReady", "EXCEPTION", e.ToString()); }

            EnumResolution.Run();
            Results.Row("DONE", "ok", "stage 0 probe finished");
        }

        private static string Str(Dictionary<string, object> d, string key)
        {
            return d != null && d.TryGetValue(key, out var v) ? v as string : null;
        }

        private static int CountPaletteButtons(scnEditor editor)        {
            var field = typeof(scnEditor).GetField("eventButtons",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (field == null) return -1;
            if (!(field.GetValue(editor) is IDictionary buttons)) return -2;
            int found = 0;
            foreach (var key in buttons.Keys)
            {
                if (!(buttons[key] is IEnumerable list)) continue;
                foreach (var b in list)
                {
                    var tf = b.GetType().GetField("type");
                    if (tf != null && (LevelEventType)tf.GetValue(b) == CustomType.Type) found++;
                }
            }
            return found;
        }
    }

    public static class JsonOut
    {
        public static string Serialize(object obj) => GDMiniJSON.Json.Serialize(obj);
    }

    public static class Loader
    {
        private const string HarmonyId = "org.shakecrane.pev.stage0";

        /// <summary>
        /// V5 harness. A marker file switches the probe into "vanilla decode" mode:
        /// only the auto-open patch is applied, so LevelData.Decode runs with no
        /// storage patches at all. That reproduces what a player without the mod sees.
        /// </summary>
        public static bool Load(UnityModManager.ModEntry entry)
        {
            Results.Init(entry.Path);

            string marker = Path.Combine(entry.Path, "vanilla-mode.txt");
            string levelOverride = Path.Combine(entry.Path, "level.txt");
            bool vanilla = File.Exists(marker);

            Results.LevelPath = File.Exists(levelOverride)
                ? File.ReadAllText(levelOverride).Trim()
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "A Dance of Fire and Ice", "Levels", "Stage0Probe.adofai");

            if (vanilla)
            {
                Storage.VanillaMode = true;
                RegisterPatch.SkipRegistration = true;
                Results.Row("MODE", "pure-vanilla", "no type registration, no storage hooks");
            }
            else
            {
                Storage.VanillaMode = false;
                RegisterPatch.SkipRegistration = false;
                Results.Row("MODE", "full", "all stage 0 patches applied");
            }
            new Harmony(HarmonyId).PatchAll(Assembly.GetExecutingAssembly());

            Results.Row("INIT-Level", File.Exists(Results.LevelPath) ? "exists" : "missing", Results.LevelPath);
            return true;
        }
    }
}
