using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using ADOFAI;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using PEV.Editor;
using PEV.Loading;
using PEV.Model;
using PEV.Registry;
using PEV.Scripting;
using PEV.Storage;
using UnityEngine;

namespace PEV.Diagnostics
{
    // Regression checks for data loss, sandbox isolation and asynchronous texture ownership.
    // Like the stage harnesses, this is inert unless its opt-in file exists.
    public static class ReviewHarness
    {
        private const string StateEventId = "pev.noise-field-state";
        private static string _results;
        private static bool _ran;

        public static void Configure(string modDir)
        {
            if (!File.Exists(Path.Combine(modDir, "review-test.txt"))) return;
            _results = Path.Combine(modDir, "review-results.txt");
            File.WriteAllText(_results, "");
        }

        private static void Check(string name, Func<bool> test)
        {
            try { Row(name, test() ? "PASS" : "FAIL"); }
            catch (Exception e) { Row(name, "FAIL " + e); }
        }

        private static void Row(string name, string result, string note = null) =>
            File.AppendAllText(_results, name + "\t" + result + (note == null ? "" : "\t" + note) + Environment.NewLine);

        [HarmonyPatch(typeof(scnLevelSelect), "Start")]
        public static class AutoOpen
        {
            public static void Postfix()
            {
                if (_results == null || _ran) return;
                scnEditor.levelToOpenOnLoad = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "A Dance of Fire and Ice", "Levels", "P3Test.adofai");
                ADOBase.loader.LoadScene("scnEditor");
            }
        }

        [HarmonyPatch(typeof(scnEditor), "Start")]
        public static class Probe
        {
            public static void Postfix(scnEditor __instance)
            {
                if (_results == null || _ran) return;
                _ran = true;
                __instance.StartCoroutine(Run(__instance));
            }
        }

        private static IEnumerator Run(scnEditor editor)
        {
            yield return new WaitForSeconds(2f);
            RuntimeHost.Ensure().Teardown();
            CheckPayloads();
            CheckStorage(editor);
            CheckScripts();
            CheckKeys();
            yield return CheckTextures();
            yield return CheckFields();
            yield return ProbeGlobalSettingsGui();
            CheckEditorPlacement(editor);
            Row("DONE", "ok");
        }

        private sealed class SettingsGuiProbe : MonoBehaviour
        {
            internal bool Ran;
            internal string Error;
            private void OnGUI()
            {
                if (Ran) return;
                try { PEV.Core.Plugin.DrawEventSettings(); }
                catch (Exception e) { Error = e.ToString(); }
                Ran = true;
            }
        }

        private static IEnumerator ProbeGlobalSettingsGui()
        {
            var go = new GameObject("PEV.ReviewSettingsGui");
            var probe = go.AddComponent<SettingsGuiProbe>();
            float deadline = Time.realtimeSinceStartup + 3f;
            while (!probe.Ran && Time.realtimeSinceStartup < deadline) yield return null;
            Row("GLOBAL-umm-gui-draw", !probe.Ran ? "SKIP" : probe.Error == null ? "PASS" : "FAIL",
                !probe.Ran ? "Unity did not invoke OnGUI in the hidden test process" : probe.Error);
            UnityEngine.Object.Destroy(go);
        }

        private static void CheckEditorPlacement(scnEditor editor)
        {
            if (editor == null || editor.levelData == null || editor.levelData.decorations == null) return;
            if (EventRegistry.TryGetById("pev.noise-field", out var field) && field.SchemaVersion >= 2)
            {
                Check("FIELD-editor-place-default-color", () =>
                {
                    var old = new HashSet<LevelEvent>(editor.levelData.decorations);
                    LevelEvent added = null;
                    try
                    {
                        EditorTestHelpers.SelectFloor(editor, 1);
                        editor.AddEventAtSelected((LevelEventType)PEV.Util.StableId.FromEventId(field.Id));
                        added = editor.levelData.decorations.FirstOrDefault(row => !old.Contains(row));
                        return added != null && added.GetString("color") == "ff5454" &&
                            LevelStorage.TryGetData(added, out var data) && data.Id == field.Id;
                    }
                    finally
                    {
                        if (added != null) { LevelStorage.Detach(added); editor.levelData.decorations.Remove(added); }
                    }
                });
            }
            if (EventRegistry.TryGetById(StateEventId, out var state))
            {
                Check("FIELD-editor-place-state-event", () =>
                {
                    var old = new HashSet<LevelEvent>(editor.events);
                    LevelEvent added = null;
                    try
                    {
                        EditorTestHelpers.SelectFloor(editor, 1);
                        editor.AddEventAtSelected((LevelEventType)PEV.Util.StableId.FromEventId(state.Id));
                        added = editor.events.FirstOrDefault(row => !old.Contains(row));
                        if (added == null || !LevelStorage.TryGetData(added, out var data) || data.Id != state.Id) return false;
                        added["tag"] = "review-field";
                        var actionInfo = added.info.propertiesInfo["action"];
                        added["action"] = Enum.Parse(actionInfo.enumType, "WarnThenActive");
                        added["warningDuration"] = 0.75f;
                        Check("FIELD-editor-state-panel", () =>
                        {
                            editor.levelEventsPanel.ShowPanel(added.eventType, 0);
                            var panel = editor.levelEventsPanel.panelsList.FirstOrDefault(p => p.levelEventType == added.eventType);
                            if (panel == null) return false;
                            if (panel.properties.TryGetValue("action", out var choice) && choice?.control != null)
                            {
                                Row("FIELD-editor-state-control", "INFO", choice.control.GetType().FullName);
                                Check("FIELD-editor-state-control-change", () =>
                                {
                                    if (!(choice.control is ADOFAI.LevelEditor.Controls.PropertyControl_Toggle toggle)) return false;
                                    if (!panel.properties.TryGetValue("warningDuration", out var durationProperty) ||
                                        durationProperty?.control?.propertyInfo == null) return false;
                                    var info = durationProperty.control.propertyInfo;
                                    toggle.SelectVar("Disabled");
                                    bool disabled = added.Get<object>("action")?.ToString() == "Disabled" &&
                                        !info.CheckIfShown(added, info.groupName);
                                    toggle.SelectVar("WarnThenActive");
                                    return disabled && added.Get<object>("action")?.ToString() == "WarnThenActive" &&
                                        info.CheckIfShown(added, info.groupName);
                                });
                            }
                            return panel.properties.TryGetValue("tag", out var tag) && tag?.control != null &&
                                panel.properties.TryGetValue("action", out var action) && action?.control != null &&
                                panel.properties.TryGetValue("warningDuration", out var duration) && duration?.control != null;
                        });
                        var instance = new EventInstance { LevelEvent = added, Data = data, Manifest = state,
                            EntryTime = 10, EntryTimeResolved = true, EventOrder = 5 };
                        try
                        {
                            if (!instance.Begin()) return false;
                            instance.Tick(10, true);
                            bool ready = GameBridge.FieldPhases.At("review-field") == NoiseFieldPhase.Ready;
                            instance.Tick(10.75, true);
                            return ready && GameBridge.FieldPhases.At("review-field") == NoiseFieldPhase.Active;
                        }
                        finally { instance.End(); }
                    }
                    finally
                    {
                        if (added != null)
                        {
                            LevelStorage.Detach(added);
                            editor.events.Remove(added);
                            editor.levelData.levelEvents.Remove(added);
                        }
                    }
                });
            }
        }

        private static void CheckPayloads()
        {
            Check("PARSE-invalid-fields", () => EventInstanceData.Parse("@PEV1 {\"id\":{},\"p\":{}}", out _) == null);
            Check("PARSE-invalid-params", () => EventInstanceData.Parse("@PEV1 {\"id\":\"test\",\"p\":[]}", out _) == null);
            Check("RAW-unknown-shapes", () =>
            {
                var d = EventInstanceData.Parse("@PEV1 {\"id\":\"test\",\"p\":{\"n\":9223372036854775807,\"shape\":{\"x\":1,\"y\":2}}}", out _);
                d.Materialize(null);
                var copy = EventInstanceData.Parse(d.ToComment(), out _);
                return JToken.DeepEquals(d.Raw["shape"], copy.Raw["shape"]) && d.GetString("n") == "9223372036854775807";
            });
            Check("MANIFEST-invalid-fields", () => EventManifest.Parse("{\"id\":\"test\",\"schemaVersion\":{}}", "", out _) == null);
            Check("MANIFEST-api-version", () => EventManifest.Parse("{\"id\":\"test\",\"apiVersion\":3}", "", out _) == null);
            Check("MANIFEST-shader-bundle", () =>
            {
                var valid = EventManifest.Parse("{\"id\":\"test\",\"schemaVersion\":2,\"shaderBundle\":\"assets/field.bundle\"}", "", out _);
                return valid != null && valid.ShaderBundlePath == "assets/field.bundle" &&
                       EventManifest.Parse("{\"id\":\"test\",\"shaderBundle\":\"assets/field.bundle\"}", "", out _) == null &&
                       EventManifest.Parse("{\"id\":\"test\",\"schemaVersion\":2,\"shaderBundle\":\"../field.bundle\"}", "", out _) == null &&
                       EventManifest.Parse("{\"id\":\"test\",\"schemaVersion\":2,\"shaderBundle\":{}}", "", out _) == null;
            });
            Check("MANIFEST-duplicate-param", () => EventManifest.Parse("{\"id\":\"test\",\"params\":[{\"name\":\"a\",\"type\":\"Float\"},{\"name\":\"a\",\"type\":\"Int\"}]}", "", out _) == null);
            Check("FIELD-row-sorting-layer", () =>
            {
                var row = new LevelEvent(1, LevelEventType.AddDecoration);
                row["depth"] = 7;
                if (!DecorationBridge.TryReadEvent(row, out var bg) || bg.UnityLayer != 9 || bg.SortingOrder != -7) return false;
                row["depth"] = -3;
                return DecorationBridge.TryReadEvent(row, out var front) && front.UnityLayer == 7 && front.SortingOrder == 3;
            });
            Check("GLOBAL-settings-roundtrip", () =>
            {
                const string json = "{\"id\":\"example.a\",\"schemaVersion\":3,\"apiVersion\":2,\"globalSettings\":[{\"name\":\"enabled\",\"type\":\"Bool\",\"default\":true,\"label\":{\"zh-CN\":\"开关\"}}]}";
                var a = EventManifest.Parse(json, "", out _);
                var b = EventManifest.Parse(json.Replace("example.a", "example.b"), "", out _);
                if (a == null || b == null || a.GlobalSettings[0].Label("ChineseSimplified") != "开关") return false;
                string path = Path.GetTempFileName();
                try
                {
                    File.WriteAllText(path, "{\"uninstalled\":{\"keep\":true}}");
                    var store = new PEV.Core.EventGlobalSettings();
                    store.Load(path);
                    if (!store.Get(a, a.GlobalSettings[0]) || !store.Get(b, b.GlobalSettings[0])) return false;
                    store.Set(a, a.GlobalSettings[0], false);
                    store.Save(path);
                    var loaded = new PEV.Core.EventGlobalSettings();
                    loaded.Load(path);
                    return !loaded.Get(a, a.GlobalSettings[0]) && loaded.Get(b, b.GlobalSettings[0]) &&
                        JObject.Parse(File.ReadAllText(path))["uninstalled"]["keep"].Value<bool>() &&
                        (bool)loaded.Snapshot(a)["enabled"] == false;
                }
                finally { File.Delete(path); }
            });
            Check("GLOBAL-settings-invalid-declarations", () =>
                EventManifest.Parse("{\"id\":\"x\",\"globalSettings\":[]}", "", out _) == null &&
                EventManifest.Parse("{\"id\":\"x\",\"schemaVersion\":3,\"apiVersion\":2,\"globalSettings\":[{\"name\":\"__proto__\",\"type\":\"Bool\",\"default\":true}]}", "", out _) == null);
            Check("GLOBAL-primitive-binding", () =>
            {
                const string json = "{\"id\":\"another.author\",\"schemaVersion\":3,\"apiVersion\":2,\"hostPrimitive\":\"fx.field\",\"globalSettings\":[{\"name\":\"authorChoice\",\"type\":\"Bool\",\"default\":true,\"binding\":\"fx.field.captureScene\"}]}";
                var manifest = EventManifest.Parse(json, "", out _);
                var store = new PEV.Core.EventGlobalSettings();
                if (manifest == null || !store.GetBoundBool(manifest, "fx.field.captureScene", false)) return false;
                store.Set(manifest, manifest.GlobalSettings[0], false);
                return !store.GetBoundBool(manifest, "fx.field.captureScene", true) &&
                    store.GetBoundBool(manifest, "not.declared", true);
            });
            Check("FIELD-event-uniform-declarations", () =>
            {
                const string valid = "{\"id\":\"example.shader\",\"schemaVersion\":3,\"hostPrimitive\":\"fx.field\",\"fieldWaves\":{\"_Pulse\":{\"base\":0.63,\"amplitude\":0.37,\"frequency\":43,\"scale\":2}},\"fieldUniforms\":{\"_Intensity\":0.5,\"_Tint\":[1,0,0.25,1]}}";
                var manifest = EventManifest.Parse(valid, "", out _);
                return manifest != null && manifest.FieldUniforms.Count == 2 && manifest.FieldWaves.Count == 1 &&
                    Mathf.Abs(manifest.FieldWaves[0].At(0) - 1.63f) < 0.00001f &&
                    manifest.FieldUniforms[0].Values[0] == 0.5f && manifest.FieldUniforms[1].Values[2] == 0.25f &&
                    EventManifest.Parse(valid.Replace("_Intensity", "uView"), "", out _) == null &&
                    EventManifest.Parse(valid.Replace("[1,0,0.25,1]", "[1,2]"), "", out _) == null;
            });
            Check("FIELD-event-uniform-material", () =>
            {
                var manifest = EventManifest.Parse("{\"id\":\"example\",\"schemaVersion\":3,\"hostPrimitive\":\"fx.field\",\"fieldUniforms\":{\"_Color\":[0.25,0.5,0.75,1]}}", "", out _);
                var shader = Shader.Find("Hidden/BlendModes/VisualDeco/Grab");
                if (manifest == null || shader == null) return false;
                var material = new Material(shader);
                try
                {
                    NoiseFieldGpuLayer.ApplyUniforms(material, manifest.FieldUniforms);
                    var value = material.GetVector("_Color");
                    return value == new Vector4(0.25f, 0.5f, 0.75f, 1f);
                }
                finally { UnityEngine.Object.Destroy(material); }
            });
            Check("FIELD-event-asset-roles", () =>
            {
                if (!EventRegistry.TryGetById("pev.noise-field", out var manifest) || manifest.SchemaVersion < 3) return true;
                if (!manifest.FieldTextures.ContainsKey("displacement") || !manifest.FieldShaders.ContainsKey("compute") ||
                    manifest.FieldShaders.Count != 11 || manifest.FieldTextures.Count != 4) return false;
                var json = JObject.Parse(System.Text.Encoding.UTF8.GetString(EventLoader.ReadResource(manifest.SourcePath, "event.json")));
                json["fieldTextures"]["touch"] = "../outside";
                return EventManifest.Parse(json.ToString(), "", out _) == null;
            });
            Check("GLOBAL-settings-corrupt-file-preserved", () =>
            {
                string path = Path.GetTempFileName();
                try
                {
                    File.WriteAllText(path, "{broken");
                    var store = new PEV.Core.EventGlobalSettings();
                    try { store.Load(path); return false; } catch { }
                    try { store.Save(path); return false; } catch { }
                    return File.ReadAllText(path) == "{broken";
                }
                finally { File.Delete(path); }
            });
            Check("ENUM-dynamic-runtime", () =>
            {
                var name = new AssemblyName("PEV.ReviewEnum." + Guid.NewGuid().ToString("N"));
                var assembly = AppDomain.CurrentDomain.DefineDynamicAssembly(name, System.Reflection.Emit.AssemblyBuilderAccess.Run);
                var module = assembly.DefineDynamicModule(name.Name);
                var builder = module.DefineEnum("Review.Choice", TypeAttributes.Public, typeof(int));
                builder.DefineLiteral("First", 0); builder.DefineLiteral("Second", 1);
                var type = builder.CreateType();
                return Type.GetType(type.AssemblyQualifiedName) == type && Enum.Parse(type, "Second").ToString() == "Second";
            });
            Check("CHOICE-legacy-missing-enum", () =>
            {
                const string json = "{\"id\":\"example.legacy\",\"params\":[{\"name\":\"mode\",\"type\":\"Enum:Missing.Type, Missing.Assembly\",\"default\":\"On\",\"optionLabels\":{\"Off\":{\"en\":\"Off\"},\"On\":{\"en\":\"On\"}}}]}";
                var manifest = EventManifest.Parse(json, "", out _);
                if (manifest == null) return false;
                ChoiceEnumFactory.Prepare(manifest, manifest.Params[0]);
                var resolved = Type.GetType(manifest.Params[0].EffectiveType.Substring(5));
                return resolved != null && Enum.Parse(resolved, "On").ToString() == "On";
            });
            Check("FIELD-manifest-old-data", () =>
            {
                if (!EventRegistry.TryGetById("pev.noise-field", out var manifest)) return false;
                var data = EventInstanceData.Parse("@PEV1 {\"id\":\"pev.noise-field\",\"v\":\"1.0.0\",\"p\":{\"noiseFrameCount\":73,\"edgeDistance\":0.42,\"futureData\":{\"keep\":true}}}", out _);
                if (data == null) return false;
                var original = data.Raw["futureData"].DeepClone();
                data.Materialize(manifest.Params);
                var copy = EventInstanceData.Parse(data.ToComment(), out _);
                return copy != null && copy.Raw["noiseFrameCount"].Value<int>() == 73 &&
                    Math.Abs(copy.Raw["edgeDistance"].Value<double>() - 0.42) < 0.00001 &&
                    JToken.DeepEquals(original, copy.Raw["futureData"]) &&
                    (manifest.SchemaVersion < 2 || (data.TryGet("invert", out var invert) && invert is bool b && !b));
            });
            Check("FIELD-manifest-invert-panel", () =>
                EventRegistry.TryGetById("pev.noise-field", out var manifest) &&
                (manifest.SchemaVersion < 2 || DecorationParams.IsInjected("invert")));
            Check("FIELD-new-native-color", () =>
            {
                var draft = EventManifest.Parse("{\"id\":\"example.decoration\",\"schemaVersion\":3,\"decoration\":true,\"decorationDefaults\":{\"color\":\"ff5454\"}}", "", out _);
                var old = EventManifest.Parse("{\"id\":\"example.decoration\",\"schemaVersion\":1}", "", out _);
                var row = new LevelEvent(1, LevelEventType.AddDecoration);
                row["color"] = "ffffff";
                DecorationPlacement.ApplyDecorationDefaults(row, old);
                if (row.GetString("color") != "ffffff") return false;
                DecorationPlacement.ApplyDecorationDefaults(row, draft);
                return row.GetString("color") == "ff5454";
            });
            if (EventRegistry.TryGetById(StateEventId, out var stateManifest))
            {
                Check("FIELD-state-package-registration", () =>
                {
                    var type = (LevelEventType)PEV.Util.StableId.FromEventId(stateManifest.Id);
                    return stateManifest.Params.Exists(p => p.Name == "tag") &&
                        stateManifest.Params.Exists(p => p.Name == "action") &&
                        stateManifest.Params.Exists(p => p.Name == "warningDuration") &&
                        EventRegistry.TryGetInfo(type, out var info) && info.propertiesInfo.ContainsKey("action");
                });
                Check("FIELD-state-enum-localization", () =>
                {
                    var param = stateManifest.Params.Single(p => p.Name == "action");
                    var option = param.OptionLabels.Single(o => o.Key == "WarnThenActive");
                    string key = "enum." + param.EffectiveType.Substring("Enum:".Length).Trim() + ".WarnThenActive";
                    var zh = SA.GoogleDoc.Localization.Client.GetFullSectionLocalization(SystemLanguage.ChineseSimplified);
                    var en = SA.GoogleDoc.Localization.Client.GetFullSectionLocalization(SystemLanguage.English);
                    return zh != null && en != null && zh.TryGetValue(key, out var zhValue) &&
                        en.TryGetValue(key, out var enValue) && zhValue == option.Value["ChineseSimplified"] &&
                        enValue == option.Value["English"];
                });
                Check("CHOICE-storage-and-isolation", () =>
                {
                    var type = (LevelEventType)PEV.Util.StableId.FromEventId(stateManifest.Id);
                    if (!EventRegistry.TryGetInfo(type, out var info) || !info.propertiesInfo.TryGetValue("action", out var prop)) return false;
                    var value = ParamCoercion.Coerce(prop, "WarnThenActive");
                    if (value == null || value.GetType() != prop.enumType ||
                        StorageValue.Canonical(prop, value).Value<string>() != "WarnThenActive") return false;
                    const string json = "{\"id\":\"example.choice\",\"schemaVersion\":3,\"params\":[{\"name\":\"action\",\"type\":\"Choice\",\"default\":\"Active\",\"options\":[\"Disabled\",\"Ready\",\"Active\",\"WarnThenActive\"],\"optionLabels\":{\"WarnThenActive\":{\"en\":\"Different label\"}}}]}";
                    var other = EventManifest.Parse(json, "", out _);
                    if (other == null) return false;
                    ChoiceEnumFactory.Prepare(other, other.Params[0]);
                    EnumLabels.Register(other.Params[0]);
                    string otherType = other.Params[0].EffectiveType.Substring("Enum:".Length).Trim();
                    var en = SA.GoogleDoc.Localization.Client.GetFullSectionLocalization(SystemLanguage.English);
                    return Type.GetType(otherType) != prop.enumType &&
                        en.TryGetValue("enum." + otherType + ".WarnThenActive", out var label) && label == "Different label";
                });
                Check("FIELD-state-script-order-and-cleanup", () =>
                {
                    string script = EventScriptCache.Get(stateManifest);
                    if (script == null) return false;
                    var first = ScriptHost.Create(script, "review/state-a", out _);
                    var second = ScriptHost.Create(script, "review/state-b", out _);
                    try
                    {
                        var a = new EventContext { EventTime = 1, EventOrder = 0 };
                        var b = new EventContext { EventTime = 2, EventOrder = 1 };
                        if (first == null || second == null ||
                            !first.Initialize(a, new Dictionary<string, object> { ["tag"] = "red blue", ["action"] = "Disabled" }, out _) ||
                            !second.Initialize(b, new Dictionary<string, object> { ["tag"] = "blue", ["action"] = "WarnThenActive", ["warningDuration"] = 0.5 }, out _)) return false;
                        first.Update(1, a.ToStateJson()); second.Update(0, b.ToStateJson());
                        if (GameBridge.FieldPhases.At("red") != NoiseFieldPhase.Disabled ||
                            GameBridge.FieldPhases.At("blue") != NoiseFieldPhase.Ready) return false;
                        second.Update(0.5, b.ToStateJson()); first.Update(1.5, a.ToStateJson());
                        if (GameBridge.FieldPhases.At("blue") != NoiseFieldPhase.Active) return false;
                        second.Dispose();
                        return GameBridge.FieldPhases.At("blue") == NoiseFieldPhase.Disabled;
                    }
                    finally { first?.Dispose(); second?.Dispose(); }
                });
            }
            Check("FIELD-touch-history", () =>
            {
                var h = new NoiseFieldTouchHistory();
                var a = new NoiseFieldTouchHistory.Contact { Id = 42, Position = new Vector2(0.2f, 0.3f) };
                var b = new NoiseFieldTouchHistory.Contact { Id = 7, Position = new Vector2(0.8f, 0.7f) };
                h.Update(new[] { a, b }, 1f);
                if (h.GetSlot(0).Scale != 0 || h.GetSlot(1).Scale != 0) return false;
                h.Update(new[] { b, a }, 1.05f);
                if (h.GetSlot(0).Id != 42 || h.GetSlot(1).Id != 7 ||
                    Mathf.Abs(h.GetSlot(0).Scale - 0.5f) > 0.001f) return false;
                h.Update(new[] { b }, 1.05f);
                h.Update(new[] { b }, 1.10f);
                if (h.GetSlot(0).Held || Mathf.Abs(h.GetSlot(0).Scale - 0.25f) > 0.001f) return false;
                h.Update(new[] { a, b }, 1.10f);
                if (!h.GetSlot(0).Held || h.GetSlot(0).Scale != 0) return false;
                h.Update(new[] { b }, 0.5f);
                return h.GetSlot(0).Held && h.GetSlot(0).Id == 7 && h.GetSlot(0).Scale == 0 && !h.GetSlot(1).Held;
            });
            Check("FIELD-state-tags", () =>
            {
                var overrides = new FieldPhaseOverrides();
                overrides.Set("a", "red blue", NoiseFieldPhase.Disabled, 1, 0);
                overrides.Set("b", "blue", NoiseFieldPhase.Active, 2, 1);
                overrides.Set("c", "red", NoiseFieldPhase.Ready, 2, 2);
                bool ok = overrides.At("green") == NoiseFieldPhase.Active &&
                    overrides.At("blue") == NoiseFieldPhase.Active &&
                    overrides.At("red blue") == NoiseFieldPhase.Ready;
                overrides.Remove("c"); overrides.Remove("b");
                return ok && overrides.At("blue") == NoiseFieldPhase.Disabled;
            });
            Check("FIELD-touch-geometry", () =>
            {
                var normal = new NoiseFieldTouchGeometry.Area
                {
                    Center = Vector2.zero, Size = new Vector2(4f, 2f), RotationDegrees = 90f,
                    Active = true, Visible = true
                };
                var hole = new NoiseFieldTouchGeometry.Area
                {
                    Center = Vector2.zero, Size = Vector2.one, Inverted = true, Active = true, Visible = true
                };
                return NoiseFieldTouchGeometry.Contains(new[] { normal }, new Vector2(0, 1.5f)) &&
                       !NoiseFieldTouchGeometry.Contains(new[] { normal }, new Vector2(1.5f, 0)) &&
                       !NoiseFieldTouchGeometry.Contains(new[] { normal, hole }, Vector2.zero) &&
                       NoiseFieldTouchGeometry.Contains(new[] { normal, hole, hole }, Vector2.zero) &&
                       !NoiseFieldTouchGeometry.Contains(new[] { normal }, new Vector2(0, 1.95f), 0.1f) &&
                       !NoiseFieldTouchGeometry.Contains(new[] { new NoiseFieldTouchGeometry.Area { Active = true, Visible = true } }, Vector2.zero);
            });
            Check("FIELD-gpu-area-layout", () => System.Runtime.InteropServices.Marshal.SizeOf(typeof(NoiseFieldGpuMasks.Area)) == 32 &&
                System.Runtime.InteropServices.Marshal.SizeOf(typeof(NoiseFieldGpuMasks.Touch)) == 16);
            Check("FIELD-renderer-activation-gate", () =>
            {
                var gate = new NoiseFieldActivationGate();
                return !gate.Observe(true, 10) && !gate.Observe(true, 10) && gate.Observe(true, 11) &&
                    gate.Observe(false, 11) && !gate.Observe(true, 11) && gate.Observe(true, 12);
            });
            Check("FIELD-touch-shader-uniforms", () =>
            {
                var p = NoiseFieldGpuLayer.TouchPosition(new Vector2(0.5f, 0.25f), 1600, 900);
                var wave = new FieldWaveSpec { Base = 0.63f, Amplitude = 0.37f, Frequency = 43f, Scale = 2f };
                return Mathf.Abs(p.x - 8f / 9f) < 0.00001f && Mathf.Abs(p.y - 0.25f) < 0.00001f &&
                       Mathf.Abs(wave.At(0f) - 1.63f) < 0.00001f &&
                       Mathf.Abs(wave.At(Mathf.PI / 86f) - 2f) < 0.00001f;
            });
            Check("FIELD-viewport-projection", () =>
            {
                var go = new GameObject("PEV.ReviewProjection");
                try
                {
                    var cam = go.AddComponent<Camera>();
                    cam.orthographic = true;
                    cam.orthographicSize = 5f;
                    cam.aspect = 16f / 9f;
                    go.transform.position = new Vector3(0, 0, -10);
                    var frame = new Placement.Frame { Center = Vector3.zero, Size = new Vector2(4, 2), Rotation = 0 };
                    if (!NoiseFieldProjection.TryProject(cam, frame, cam.aspect, out var result)) return false;
                    float expected = 1f / (5f * cam.aspect);
                    if (result.Center.sqrMagnitude > 0.000001f || Mathf.Abs(result.HalfSize.x - 2 * expected) > 0.00001f ||
                        Mathf.Abs(result.HalfSize.y - expected) > 0.00001f || Mathf.Abs(result.AngleRadians) > 0.00001f) return false;
                    frame.Rotation = 45;
                    if (!NoiseFieldProjection.TryProject(cam, frame, cam.aspect, out result) ||
                        Mathf.Abs(result.AngleRadians - Mathf.PI / 4) > 0.0001f) return false;
                    frame.Center = go.transform.position;
                    if (!NoiseFieldProjection.TryProject(cam, frame, cam.aspect, out result)) return false;
                    cam.orthographic = false;
                    return !NoiseFieldProjection.TryProject(cam, frame, cam.aspect, out _);
                }
                finally { UnityEngine.Object.Destroy(go); }
            });
            Check("FIELD-gpu-plan-groups", () =>
            {
                var go = new GameObject("PEV.ReviewGpuPlan");
                try
                {
                    var camera = go.AddComponent<Camera>();
                    camera.orthographic = true; camera.orthographicSize = 5; camera.aspect = 16f / 9f;
                    go.transform.position = new Vector3(0, 0, -10);
                    var a = new NoiseFieldRenderer.Entry { GpuSequence = 2, Visible = true, Renderable = true,
                        Opacity = 1, World = new Placement.Frame { Size = new Vector2(2, 2) },
                        SortingOrder = 1, UnityLayer = 7, GpuPhase = NoiseFieldPhase.Active,
                        FillColor = Color.blue, EdgeColor = Color.red };
                    var b = new NoiseFieldRenderer.Entry { GpuSequence = 1, Visible = true, Renderable = true,
                        Opacity = 0.5f, World = new Placement.Frame { Size = new Vector2(2, 2) },
                        SortingOrder = 1, UnityLayer = 7, GpuPhase = NoiseFieldPhase.Ready, GpuInverted = true };
                    var c = new NoiseFieldRenderer.Entry { GpuSequence = 3, Visible = true, Renderable = true,
                        Opacity = 1, World = new Placement.Frame { Size = new Vector2(2, 2) },
                        SortingOrder = 1, UnityLayer = 9, GpuPhase = NoiseFieldPhase.Disabled,
                        Shape = NoiseFieldShape.Circle };
                    var plan = new NoiseFieldGpuPlan();
                    plan.Build(new[] { a, b, c }, camera, camera.aspect);
                    if (plan.Groups.Count != 2) return false;
                    var key = new NoiseFieldGpuPlan.Key { SortingOrder = 1, UnityLayer = 7 };
                    if (!plan.Groups.TryGetValue(key, out var group) || group.Areas.Count != 2 ||
                        group.Areas[0].Flags != 6 || group.Areas[1].Flags != 1 || !group.HasActive || !group.HasDisabled ||
                        group.Colors[1].Red != 0f || group.Colors[1].Blue != 1f || !group.Colored) return false;
                    plan.Build(new[] { a }, camera, camera.aspect);
                    return plan.Groups.Count == 1 && plan.Groups[key].Areas.Count == 1;
                }
                finally { UnityEngine.Object.Destroy(go); }
            });
            Check("FIELD-gpu-formats", () =>
                SystemInfo.supportsComputeShaders &&
                SystemInfo.IsFormatSupported(UnityEngine.Experimental.Rendering.GraphicsFormat.R32G32B32A32_UInt,
                    UnityEngine.Experimental.Rendering.FormatUsage.LoadStore) &&
                SystemInfo.IsFormatSupported(UnityEngine.Experimental.Rendering.GraphicsFormat.R32_UInt,
                    UnityEngine.Experimental.Rendering.FormatUsage.LoadStore) &&
                SystemInfo.IsFormatSupported(UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_UNorm,
                    UnityEngine.Experimental.Rendering.FormatUsage.LoadStore));
            Check("RESOURCE-package-boundary", () =>
            {
                string root = Path.Combine(Path.GetTempPath(), "pev-review-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);
                try
                {
                    File.WriteAllText(Path.Combine(root, "main.js"), "test");
                    string zip = Path.Combine(root, "test.event");
                    using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
                    using (var writer = new StreamWriter(archive.CreateEntry("main.js").Open())) writer.Write("test");
                    return EventLoader.ReadResource(root, "../outside") == null &&
                           EventLoader.ReadResource(root, Path.Combine(root, "main.js")) == null &&
                           EventLoader.ReadResource(root, "main.js").Length == 4 &&
                           EventLoader.ReadResource(zip, "main.js").Length == 4;
                }
                finally { Directory.Delete(root, true); }
            });
            Check("FIELD-reference-texture", () =>
            {
                string root = Path.Combine(Path.GetTempPath(), "pev-texture-" + Guid.NewGuid().ToString("N"));
                string assets = Path.Combine(root, "assets");
                Directory.CreateDirectory(assets);
                try
                {
                    // Independent PNG fixture: top row red bytes 1,4; bottom row 7,10.
                    byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAYAAABytg0kAAAAGElEQVR4nGNkZGL+z8zMzMDCxsbGAGIAAA+dAS/kUtAIAAAAAElFTkSuQmCC");
                    File.WriteAllBytes(Path.Combine(assets, "BlockNoise1.png"), png);
                    var manifest = new EventManifest { SourcePath = root };
                    manifest.FieldTextures["displacement"] = "assets/BlockNoise1.png";
                    var texture = ReferenceNoiseTextures.Load(manifest, "displacement", out var error);
                    if (texture == null || error != null || texture.wrapMode != TextureWrapMode.Mirror ||
                        texture.filterMode != FilterMode.Point) return false;
                    var pixels = texture.GetPixels32();
                    return pixels.Length == 4 && pixels[0].r == 7 && pixels[1].r == 10 &&
                           pixels[2].r == 1 && pixels[3].r == 4 &&
                           ReferenceNoiseTextures.Load(manifest, "displacement", out _) == texture &&
                           ReferenceNoiseTextures.Load(manifest, "../outside", out _) == null;
                }
                finally { ReferenceNoiseTextures.Clear(); Directory.Delete(root, true); }
            });
        }

        private static void CheckStorage(scnEditor editor)
        {
            Check("DELETE-undo-binding", () =>
            {
                var type = (LevelEventType)PEV.Util.StableId.FromEventId("pev.camera-warp");
                var ev = new LevelEvent(1, type);
                var data = new EventInstanceData { Id = "pev.camera-warp" };
                LevelStorage.Attach(ev, data);
                var snapshot = ev.Copy();
                LevelStorage.Detach(ev);
                return !LevelStorage.TryGetData(ev, out _) && LevelStorage.TryGetData(snapshot, out var restored) && ReferenceEquals(data, restored);
            });
            Check("DECORATION-copy-identity", () =>
            {
                var row = new LevelEvent(1, LevelEventType.AddDecoration);
                var data = new EventInstanceData { Id = "pev.noise-field" };
                data.Set("fillOpacity", 0.4f);
                LevelStorage.Attach(row, data);
                var copy = row.Copy();
                editor.levelData.decorations.Add(row);
                editor.levelData.decorations.Add(copy);
                try
                {
                    PEV.Editor.DecorationPlacement.PasteDecorationsPatch.Postfix();
                    LevelStorage.TryGetData(row, out var a);
                    LevelStorage.TryGetData(copy, out var b);
                    return !ReferenceEquals(a, b) && a.Uid != b.Uid;
                }
                finally { editor.levelData.decorations.Remove(row); editor.levelData.decorations.Remove(copy); }
            });
            Check("MIGRATE-legacy-decoration", () =>
            {
                var level = new LevelData();
                var comment = new LevelEvent(1, LevelEventType.EditorComment);
                comment["comment"] = "@PEV1 {\"id\":\"pev.noise-field\",\"p\":{\"size\":[8,6],\"opacity\":0,\"tag\":\"\"}}";
                level.levelEvents.Add(comment);
                var summary = LevelStorage.Restore(level);
                return summary.Migrated == 1 && level.levelEvents.Count == 0 && level.decorations.Count == 1 &&
                       level.decorations[0].GetFloat("opacity") == 0 &&
                       level.decorations[0].Get<Vector2>("scale") == new Vector2(200, 200) &&
                       !string.IsNullOrEmpty(level.decorations[0].GetString("tag"));
            });
            Check("MIGRATE-generic-no-field-defaults", () =>
            {
                if (!DecorationParams.TryGetNativeInfo(out var nativeInfo)) return false;
                var level = new LevelData();
                var source = new LevelEvent(1, LevelEventType.EditorComment);
                var data = new EventInstanceData { Id = "example.non-field" };
                data.Set("size", new[] { 8, 6 });
                data.Materialize(null);
                var manifest = new EventManifest { Id = data.Id, DecorationBacked = true, SchemaVersion = 3 };
                var baseline = new LevelEvent(1, LevelEventType.AddDecoration, nativeInfo);
                var method = typeof(LevelStorage).GetMethod("MigrateToDecoration", BindingFlags.NonPublic | BindingFlags.Static);
                try
                {
                    bool migrated = (bool)method.Invoke(null, new object[] { level, source, data, manifest, new DecodeSummary() });
                    var row = level.decorations.Single();
                    return migrated && row.GetString("color") == baseline.GetString("color") &&
                        row.Get<Vector2>("scale") == baseline.Get<Vector2>("scale");
                }
                finally { foreach (var row in level.decorations) LevelStorage.Detach(row); }
            });
            Check("MIGRATE-failure-keeps-comment", () =>
            {
                DecorationParams.TryGetNativeInfo(out var native);
                var removed = GCS.levelEventsInfo.Where(kv => ReferenceEquals(kv.Value, native)).ToList();
                foreach (var kv in removed) GCS.levelEventsInfo.Remove(kv.Key);
                try
                {
                    var level = new LevelData();
                    var comment = new LevelEvent(1, LevelEventType.EditorComment);
                    comment["comment"] = "@PEV1 {\"id\":\"pev.noise-field\"}";
                    level.levelEvents.Add(comment);
                    LevelStorage.Restore(level);
                    return level.levelEvents.Contains(comment) && level.decorations.Count == 0;
                }
                finally { foreach (var kv in removed) GCS.levelEventsInfo[kv.Key] = kv.Value; }
            });
            Check("PLACEHOLDER-failure-keeps-comment", () =>
            {
                var infos = (Dictionary<LevelEventType, LevelEventInfo>)typeof(EventRegistry)
                    .GetField("InfoByType", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
                var type = (LevelEventType)PEV.Util.StableId.FromEventId(PlaceholderEvent.Id);
                var info = infos[type];
                infos.Remove(type);
                try
                {
                    var level = new LevelData();
                    var ev = new LevelEvent(1, LevelEventType.EditorComment);
                    ev["comment"] = "@PEV1 malformed";
                    level.levelEvents.Add(ev);
                    LevelStorage.Restore(level);
                    return level.levelEvents.Contains(ev);
                }
                finally { infos[type] = info; }
            });
            Check("ENCODE-failure-is-atomic", () =>
            {
                var level = new LevelData();
                var type = (LevelEventType)PEV.Util.StableId.FromEventId("pev.camera-warp");
                var ev = new LevelEvent(1, type);
                LevelStorage.Attach(ev, new EventInstanceData { Id = "pev.camera-warp", Raw = null });
                level.levelEvents.Add(ev);
                var original = new Dictionary<string, object> { { "eventType", ((int)type).ToString() } };
                var actions = new List<Dictionary<string, object>> { original };
                try { LevelStorage.Bake(level, actions); return false; }
                catch { return actions.Count == 1 && ReferenceEquals(actions[0], original); }
            });
            Check("ENCODE-preserves-editor-flags", () =>
            {
                var level = new LevelData();
                var ev = new LevelEvent(1, (LevelEventType)PEV.Util.StableId.FromEventId("pev.camera-warp")) { visible = false, locked = true };
                LevelStorage.Attach(ev, new EventInstanceData { Id = "pev.camera-warp" });
                level.levelEvents.Add(ev);
                var actions = new List<Dictionary<string, object>>();
                LevelStorage.Bake(level, actions);
                return actions.Count == 1 && (bool)actions[0]["locked"] && !(bool)actions[0]["visible"];
            });
        }

        private static ScriptHost Host(string source, string scope)
        {
            var host = ScriptHost.Create(source, scope, out var error);
            if (host == null) throw new Exception(error);
            if (!host.Initialize(new EventContext(), new Dictionary<string, object>(), out error)) throw new Exception(error);
            return host;
        }

        private static void CheckScripts()
        {
            Check("SCRIPT-statement-budget", () =>
            {
                using (var host = Host("function create(){return {update:function(){while(true){}}};}", "review/loop"))
                {
                    host.Update(0, "{}");
                    return host.Faulted && host.LastError.Contains("statement budget");
                }
            });
            Check("SCRIPT-context-refresh", () =>
            {
                using (var host = Host("function create(ctx){return {update:function(t){if(ctx.t!==t || ctx.bpm!==180)throw Error('stale context');}};}", "review/context"))
                {
                    host.Update(3, "{\"t\":3,\"bpm\":180}");
                    host.Update(4, "{\"t\":4,\"bpm\":180}");
                    return !host.Faulted && host.UpdateCalls == 2;
                }
            });
            Check("SCRIPT-global-settings-refresh", () =>
            {
                const string source = "function create(ctx){if(!ctx.settings.enabled || !Object.isFrozen(ctx.settings))throw Error('initial');" +
                    "return {update:function(){if(ctx.settings.enabled || !Object.isFrozen(ctx.settings))throw Error('updated');}};}";
                var context = new EventContext { Settings = new Dictionary<string, object> { ["enabled"] = true } };
                using (var host = ScriptHost.Create(source, "review/settings", out _))
                {
                    if (host == null || !host.Initialize(context, new Dictionary<string, object>(), out _)) return false;
                    context.Settings["enabled"] = false;
                    host.Update(1, context.ToStateJson());
                    return !host.Faulted && host.UpdateCalls == 1 &&
                        !new EventContext().ToStateJson().Contains("\"settings\"");
                }
            });
            Check("SCRIPT-dispose-this", () =>
            {
                var host = Host("var disposed=false;function create(){return {marker:7,dispose:function(){disposed=this.marker===7;}};}", "review/dispose");
                var engine = (Jint.Engine)typeof(ScriptHost).GetField("_engine", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(host);
                host.Dispose();
                return Convert.ToBoolean(engine.GetValue("disposed").ToObject());
            });
            Check("SCRIPT-scoped-effects", () =>
            {
                const string source = "function create(){fx.create('same',{color:'ff0000',scale:2});return {}; }";
                using (var a = Host(source, "review/a"))
                using (var b = Host(source, "review/b"))
                {
                    bool both = ContinuousEffects.Exists(GameBridge.ScopedKey("review/a", "same")) &&
                                ContinuousEffects.Exists(GameBridge.ScopedKey("review/b", "same"));
                    a.Dispose();
                    return both && !ContinuousEffects.Exists(GameBridge.ScopedKey("review/a", "same")) &&
                           ContinuousEffects.Exists(GameBridge.ScopedKey("review/b", "same"));
                }
            });
            Check("SCRIPT-init-failure-cleans", () =>
            {
                var host = ScriptHost.Create("function create(){fx.create('leak',{});throw Error('init');}", "review/fail", out _);
                bool failed = !host.Initialize(new EventContext(), new Dictionary<string, object>(), out _);
                return failed && !ContinuousEffects.Exists(GameBridge.ScopedKey("review/fail", "leak"));
            });
            Check("FX-partial-update", () =>
            {
                ContinuousEffects.Create("review/partial", new Dictionary<string, object> { { "scale", 2f }, { "alpha", 0.3f }, { "x", 0.2f } }, out _);
                ContinuousEffects.TryGetStats("review/partial", out _, out _, out _, out var before);
                ContinuousEffects.Set("review/partial", new Dictionary<string, object> { { "alpha", 0.5f } }, out _);
                ContinuousEffects.TryGetStats("review/partial", out _, out float scale, out float alpha, out var after);
                ContinuousEffects.Destroy("review/partial", out _);
                return scale == 2 && alpha == 0.5f && before == after;
            });
        }

        private static NoiseTextureFactory.Request Request() => new NoiseTextureFactory.Request
        {
            WidthWorld = 2, HeightWorld = 2, FrameCount = 2, TargetPixelWidth = 192,
            FillOpacity = 0.6f, EdgeOpacity = 0.8f, GlowOpacity = 0.8f,
            EdgeColor = Color.red, SampleRate = 1f, ScreenPixelWidth = 192
        };

        private static void CheckKeys()
        {
            var req = Request();
            string key = NoiseTextureFactory.Key(req);
            Check("CACHE-glow-opacity", () => { var r = req; r.GlowOpacity = 0.4f; return NoiseTextureFactory.Key(r) != key; });
            Check("CACHE-scene-colour", () => { var r = req; r.SceneColor = Color.white; return NoiseTextureFactory.Key(r) != key; });
            Check("CACHE-screen-density", () => { var r = req; r.ScreenPixelWidth = 384; return NoiseTextureFactory.Key(r) != key; });
        }

        private static IEnumerator CheckTextures()
        {
            var req = Request();
            var frames = NoiseTextureFactory.Begin(req, out _);
            NoiseTextureFactory.Release(frames); // cancel while the worker may still be starting
            var replacement = NoiseTextureFactory.Begin(req, out _);
            NoiseTextureFactory.Release(frames); // stale release must not evict the replacement
            Check("CACHE-stale-release", () => NoiseTextureFactory.CachedFrameSets == 1);
            float deadline = Time.realtimeSinceStartup + 20f;
            while (!replacement.Ready && replacement.Error == null && Time.realtimeSinceStartup < deadline)
            {
                NoiseTextureFactory.Pump(2f);
                yield return null;
            }
            Check("BAKE-worker-completes", () => replacement.Ready && replacement.Textures.All(t => t != null));
            NoiseTextureFactory.Release(replacement);
            yield return null;
            Check("BAKE-cleanup", () => NoiseTextureFactory.CachedFrameSets == 0);
        }

        private static IEnumerator CheckFields()
        {
            var args = new Dictionary<string, object>
            {
                { "__instance", "review/field" }, { "relativeTo", "Camera" },
                { "size", new[] { 2f, 2f } }, { "frameCount", 2 }, { "frame", 0 }, { "depth", 123 }
            };
            NoiseFieldRenderer.Set("field", args, out _);
            args["depth"] = 124;
            NoiseFieldRenderer.Set("field", args, out _);
            Check("FIELD-depth-change", () => NoiseFieldRenderer.LayerCount == 1);
            NoiseFieldRenderer.SetGpuRendering(true);
            NoiseFieldRenderer.Set("field", args, out _);
            Check("FIELD-gpu-takeover-suspends-bake", () => NoiseFieldRenderer.LayerCount == 0 && NoiseFieldRenderer.LiveCount == 1);
            NoiseFieldRenderer.SetGpuRendering(false);
            Check("FIELD-gpu-failure-restores-legacy", () => NoiseFieldRenderer.LayerCount == 1 && NoiseFieldRenderer.LiveCount == 1);

            float deadline = Time.realtimeSinceStartup + 20f;
            while (NoiseFieldRenderer.AllEntries()[0].GameObject == null && Time.realtimeSinceStartup < deadline)
            {
                NoiseTextureFactory.Pump(2f);
                NoiseFieldRenderer.Set("field", args, out _);
                yield return null;
            }
            var go = NoiseFieldRenderer.AllEntries()[0].GameObject;
            if (go != null)
            {
                var sr = go.GetComponent<SpriteRenderer>();
                var old = sr.sprite;
                args["exposure"] = 0.75f;
                NoiseFieldRenderer.Set("field", args, out _);
                yield return null; // Unity's deferred destruction would invalidate the old sprite here.
                Check("FIELD-retains-old-texture", () => old != null && sr.sprite == old && old.texture != null);
                deadline = Time.realtimeSinceStartup + 20f;
                while (sr.sprite == old && Time.realtimeSinceStartup < deadline)
                {
                    NoiseTextureFactory.Pump(2f);
                    NoiseFieldRenderer.Set("field", args, out _);
                    yield return null;
                }
                Check("FIELD-swaps-ready-texture", () => sr.sprite != null && sr.sprite != old);
            }
            else Row("FIELD-first-texture", "FAIL timeout");
            NoiseFieldRenderer.DestroyAll();

            args["size"] = new[] { 4f, 4f };
            foreach (var pair in new[] { new[] { "a", "0" }, new[] { "b", "6" }, new[] { "c", "3" } })
            {
                args["__instance"] = "review/cluster-" + pair[0];
                args["position"] = new[] { float.Parse(pair[1]), 0f };
                NoiseFieldRenderer.Set("field", args, out _);
            }
            Check("FIELD-transitive-union", () => NoiseFieldRenderer.LayerCount == 1);
            args["visible"] = false;
            NoiseFieldRenderer.Set("field", args, out _); // hide the bridge between the two disjoint fields
            Check("FIELD-hidden-member", () => NoiseFieldRenderer.LayerCount == 2);
            NoiseFieldRenderer.DestroyAll();
            yield return null;
            Check("FIELD-cleanup", () => NoiseTextureFactory.CachedFrameSets == 0 && NoiseFieldRenderer.LiveCount == 0);

            args["visible"] = true;
            args["position"] = new[] { 0f, 0f };
            args["__instance"] = "review/layer-a";
            NoiseFieldRenderer.Set("field", args, out _);
            args["__instance"] = "review/layer-b";
            NoiseFieldRenderer.Set("field", args, out _);
            var layerA = NoiseFieldRenderer.AllEntries().Single(e => e.Scope == "review/layer-a");
            layerA.UnityLayer = layerA.UnityLayer == 7 ? 9 : 7;
            NoiseFieldRenderer.Set("field", args, out _); // rebuild with one differing culling layer
            Check("FIELD-unity-layer-isolation", () => NoiseFieldRenderer.LayerCount == 2);
            NoiseFieldRenderer.DestroyAll();

            using (var worker = new NoiseFieldColorWorker())
            {
                var zones = new[] { new NoiseFieldColorOwnership.Zone {
                    CenterX = 0, CenterY = 0, HalfX = 0.5f, HalfY = 0.5f, YScale = 1f,
                    Opacity = 1f, Active = true, Red = 0f, Green = 1f, Blue = 0f } };
                worker.Update(16, 8, 2f, zones);
                float workerDeadline = Time.realtimeSinceStartup + 5f;
                while (worker.Latest == null && worker.LastError == null && Time.realtimeSinceStartup < workerDeadline)
                {
                    worker.Update(16, 8, 2f, zones);
                    yield return null;
                }
                Check("FIELD-color-worker-runtime", () => worker.LastError == null && worker.Latest != null &&
                    worker.Latest.Active[0] == 0 && worker.Latest.Active[1] == 255 && worker.Latest.Active[2] == 0);
            }
            Check("FIELD-color-texture-upload", () =>
            {
                var rgba = new byte[] { 255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 255, 255, 255, 255 };
                var texture = NoiseFieldGpuPalette.MakeTexture(2, 2, "PEV.ReviewPalette", rgba);
                try
                {
                    var pixels = texture.GetPixels32();
                    return pixels.Length == 4 && pixels[0].r == 255 && pixels[1].g == 255 &&
                           pixels[2].b == 255 && pixels[3].r == 255 && pixels[3].g == 255 && pixels[3].b == 255 &&
                           texture.filterMode == FilterMode.Point && texture.wrapMode == TextureWrapMode.Clamp;
                }
                finally { UnityEngine.Object.Destroy(texture); }
            });
            yield return ProbeNativeGrabOrderingSafe();

            var row = new LevelEvent(1, LevelEventType.AddDecoration);
            row["visible"] = false;
            var stored = new EventInstanceData { Id = "pev.noise-field" };
            stored.Set("fillOpacity", 0.23f);
            stored.Set("noiseFrameCount", 7);
            stored.Set("noiseBrightness", 1.7f);
            // Adopt normally places the saved values on the live AddDecoration row.
            row["fillOpacity"] = 0.23f;
            row["noiseFrameCount"] = 7;
            row["noiseBrightness"] = 1.7f;
            NoiseFieldRenderer.Describe("review/description", "noisefield", row, stored);
            Check("FIELD-description-params", () =>
            {
                var field = NoiseFieldRenderer.AllEntries().Single();
                return Mathf.Abs(field.FillOpacity - 0.23f) < 0.001f && field.FrameCount == 7 &&
                       Mathf.Abs(field.Brightness - 1.7f) < 0.001f && !field.Visible;
            });
            NoiseFieldRenderer.Set("noisefield", new Dictionary<string, object>
                { ["__instance"] = "review/description", ["invert"] = true }, out _);
            NoiseFieldRenderer.Destroy("noisefield", new Dictionary<string, object> { ["__instance"] = "review/description" }, out _);
            Check("FIELD-description-reset-params", () =>
                Mathf.Abs(NoiseFieldRenderer.AllEntries().Single().FillOpacity - 0.23f) < 0.001f &&
                !NoiseFieldRenderer.AllEntries().Single().GpuInverted);
            NoiseFieldRenderer.DestroyAll();
            NoiseFieldRenderer.UnbindAll();
        }

        private static IEnumerator ProbeNativeGrabOrderingSafe()
        {
            var probe = ProbeNativeGrabOrdering();
            while (true)
            {
                bool next;
                try { next = probe.MoveNext(); }
                catch (Exception e) { Row("FIELD-built-in-grab-sorting", "CHECK", e.Message); yield break; }
                if (!next) yield break;
                yield return probe.Current;
            }
        }

        private static IEnumerator ProbeNativeGrabOrdering()
        {
            var shader = Shader.Find("Hidden/BlendModes/VisualDeco/Grab");
            if (shader == null || !shader.isSupported) { Row("FIELD-built-in-grab-sorting", "SKIP unavailable shader"); yield break; }
            var objects = new List<GameObject>();
            RenderTexture target = null;
            Texture2D read = null;
            Material material = null;
            Material secondMaterial = null;
            var prior = RenderTexture.active;
            try
            {
                var cameraGo = new GameObject("PEV.ReviewGrabCamera"); objects.Add(cameraGo);
                var camera = cameraGo.AddComponent<Camera>(); camera.enabled = false; camera.orthographic = true;
                camera.orthographicSize = 1; camera.aspect = 1; camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.black; camera.cullingMask = unchecked((int)0x80000000u);
                cameraGo.transform.position = new Vector3(0, 0, -10);
                target = new RenderTexture(64, 64, 0, RenderTextureFormat.ARGB32) { antiAliasing = 1 };
                if (!target.Create()) throw new InvalidOperationException("Cannot create grab probe target");
                camera.targetTexture = target;
                read = new Texture2D(64, 64, TextureFormat.RGBA32, false, true);
                Func<string, Color, int, float, float, SpriteRenderer> sprite = (name, color, order, x, size) =>
                {
                    var go = new GameObject(name); objects.Add(go); go.layer = 31;
                    go.transform.position = new Vector3(x, 0, 0); go.transform.localScale = new Vector3(size, size, 1);
                    var renderer = go.AddComponent<SpriteRenderer>(); renderer.sprite = SolidSprite.White;
                    renderer.color = color; renderer.sortingOrder = order; return renderer;
                };
                var background = sprite("PEV.ReviewGrabBack", Color.red, 0, 0, 2);
                sprite("PEV.ReviewGrabFront", Color.green, 10, .5f, 1);
                var fieldRoot = new GameObject("PEV.ReviewGrabGroup"); objects.Add(fieldRoot); fieldRoot.layer = 31;
                var field = sprite("PEV.ReviewGrabField", Color.white, 0, 0, 2);
                field.transform.SetParent(fieldRoot.transform, true);
                var sorting = fieldRoot.AddComponent<UnityEngine.Rendering.SortingGroup>();
                sorting.sortingOrder = 5;
                material = new Material(shader); material.SetColor("_Color", new Color(.2f, .5f, 1f, 1f));
                material.SetFloat("_Opacity", 1f); material.SetVector("_Tile", new Vector4(1, 1, 0, 0));
                // This game shader selects its sampling operation through a keyword;
                // without one it falls back to a solid-color path and proves no capture.
                material.EnableKeyword("BLENDMODES_MODE_MULTIPLY");
                field.sharedMaterial = material;
                var secondRoot = new GameObject("PEV.ReviewGrabSecondGroup"); objects.Add(secondRoot); secondRoot.layer = 31;
                var second = sprite("PEV.ReviewGrabSecond", Color.white, 0, 0, 2);
                second.transform.SetParent(secondRoot.transform, true);
                var secondSorting = secondRoot.AddComponent<UnityEngine.Rendering.SortingGroup>();
                secondSorting.sortingOrder = 7;
                secondMaterial = new Material(shader); secondMaterial.SetColor("_Color", new Color(.5f, 1f, 1f, 1f));
                secondMaterial.SetFloat("_Opacity", 1f); secondMaterial.SetVector("_Tile", new Vector4(1, 1, 0, 0));
                secondMaterial.EnableKeyword("BLENDMODES_MODE_MULTIPLY"); second.sharedMaterial = secondMaterial;
                secondRoot.SetActive(false);
                Func<Color32[]> capture = () =>
                {
                    try
                    {
                        camera.Render(); RenderTexture.active = target;
                        read.ReadPixels(new Rect(0, 0, 64, 64), 0, 0); read.Apply(false, false);
                        var pixels = read.GetPixels32(); return new[] { pixels[32 * 64 + 16], pixels[32 * 64 + 48] };
                    }
                    finally { RenderTexture.active = prior; }
                };
                fieldRoot.SetActive(false); var before = capture();
                fieldRoot.SetActive(true); var after = capture();
                bool backgroundChanged = !before[0].Equals(after[0]);
                bool foregroundPreserved = before[1].Equals(after[1]);
                Row("FIELD-built-in-grab-sorting", backgroundChanged && foregroundPreserved ? "PASS" : "CHECK",
                    $"back {before[0]}->{after[0]}, front {before[1]}->{after[1]}; built-in shader only");
                background.color = Color.blue;
                var alternate = capture();
                Row("FIELD-built-in-grab-sampling", !after[0].Equals(alternate[0]) ? "PASS" : "CHECK",
                    $"back changed red to blue; rendered {after[0]}->{alternate[0]}; this does not test the draft shader");
                background.color = Color.red;
                second.color = Color.clear;
                secondRoot.SetActive(true);
                yield return null; // allow Unity to update renderer/culling registration
                second.color = Color.white;
                var twice = capture();
                bool secondSeesFirst = Math.Abs(twice[0].r - after[0].r * .5f) <= 2 && twice[1].Equals(after[1]);
                fieldRoot.SetActive(false);
                yield return null;
                var secondOnly = capture();
                Row("FIELD-built-in-grab-two-layers", secondSeesFirst ? "PASS" : "CHECK",
                    $"first {after[0]}, both {twice[0]}, second alone {secondOnly[0]}, foreground {twice[1]}; built-in shader only");
                if (!secondSeesFirst)
                {
                    Row("FIELD-built-in-grab-diagnostic", "INFO",
                        $"second active={second.gameObject.activeInHierarchy} enabled={second.enabled} bounds={second.bounds} " +
                        $"layer={second.gameObject.layer} sort={secondSorting.sortingOrder} tint={second.sharedMaterial.GetColor("_Color")} " +
                        $"keyword={second.sharedMaterial.IsKeywordEnabled("BLENDMODES_MODE_MULTIPLY")}");
                    second.sharedMaterial = background.sharedMaterial;
                    second.color = Color.blue;
                    yield return null;
                    var solidOnly = capture();
                    Row("FIELD-built-in-grab-solid-control", "INFO", $"second blue solid control={solidOnly[0]}");
                }
            }
            finally
            {
                RenderTexture.active = prior;
                if (target != null) { target.Release(); UnityEngine.Object.Destroy(target); }
                if (read != null) UnityEngine.Object.Destroy(read);
                if (material != null) UnityEngine.Object.Destroy(material);
                if (secondMaterial != null) UnityEngine.Object.Destroy(secondMaterial);
                foreach (var go in objects) if (go != null) UnityEngine.Object.Destroy(go);
            }
        }
    }
}
