using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using ADOFAI;
using HarmonyLib;
using PEV.Registry;
using PEV.Storage;
using UnityEngine;

namespace PEV.Editor
{
    /// <summary>
    /// The grading panel: curves, colour wheels, LUT and HSL ranges.
    ///
    /// The native property panel can express a float, a colour and a two-number vector. It
    /// cannot express a tone curve or a colour wheel, and flattening those into columns of
    /// numbers is exactly the "edit a config file in another tool" workflow this event is
    /// meant to remove. So the package declares the panel in its manifest and Core draws it,
    /// while the values still live in ordinary declared parameters — which is what keeps the
    /// event saveable, copyable and readable by a chart that has no mod installed.
    ///
    /// Editing writes the parameter on the live LevelEvent, the same place the native panel
    /// and the save path read, so nothing here has a private copy of the truth.
    /// </summary>
    public class ScreenEffectPanel : MonoBehaviour
    {
        private const float WindowWidth = 340f;
        private const int CurveResolution = 96;

        private static readonly string[] Tabs = { "Curves", "Wheels", "LUT", "HSL", "Info" };
        private static readonly string[] Channels = { "Master", "Red", "Green", "Blue" };
        private static readonly string[] HueChannels = { "Hue vs Hue", "Hue vs Sat", "Hue vs Luma" };
        private static readonly string[] BandNames =
            { "Red", "Orange", "Yellow", "Green", "Cyan", "Blue", "Purple", "Magenta" };

        private Rect _window = new Rect(-1f, 96f, WindowWidth, 470f);
        private int _tab;
        private int _curveChannel;
        private int _eventIndex;

        private Texture2D _wheel;
        private Texture2D _white;
        private GUIStyle _headerStyle;
        private GUIStyle _smallStyle;

        private bool _draggingCurve;
        private int _dragPoint = -1;
        private string _dragEvent;

        [HarmonyPatch(typeof(scnEditor), "Start")]
        public static class AttachPatch
        {
            public static void Postfix(scnEditor __instance)
            {
                if (__instance == null) return;
                try
                {
                    if (__instance.GetComponent<ScreenEffectPanel>() == null)
                        __instance.gameObject.AddComponent<ScreenEffectPanel>();
                }
                catch (Exception e) { PEV.Core.Plugin.Log("grading panel attach failed: " + e.Message); }
            }
        }

#if PEV_DIAGNOSTICS
        /// <summary>Diagnostics hook: lets the probe photograph each tab.</summary>
        internal void ProbeTab(int tab) => _tab = tab;
#endif

        private void OnDestroy()
        {
            if (_wheel != null) Destroy(_wheel);
            if (_white != null) Destroy(_white);
        }

        private void OnGUI()
        {
            var editor = ADOBase.editor;
            if (editor == null) return;
            if (editor.playMode) return;

            var events = Collect(editor);
            if (events.Count == 0) return;

            if (_headerStyle == null)
            {
                _headerStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };
                _smallStyle = new GUIStyle(GUI.skin.label) { fontSize = 10, wordWrap = true };
            }
            EnsureTextures();

            if (_eventIndex >= events.Count) _eventIndex = 0;

            // Sit on the right edge, clear of the game's own panels, and size to the tab so
            // the widgets are never clipped by a height guessed for a different tab.
            float height = TabHeight(_tab);
            if (_window.x < 0f || _window.x > Screen.width - 80f)
                _window.x = Screen.width - WindowWidth - 24f;
            _window.x = Mathf.Clamp(_window.x, 0f, Mathf.Max(0f, Screen.width - WindowWidth));
            _window.y = Mathf.Clamp(_window.y, 0f, Mathf.Max(0f, Screen.height - 80f));
            _window.width = WindowWidth;
            _window.height = height;
            _window = GUILayout.Window(GetInstanceID(), _window, Draw, "Color Grading");
        }

        private static float TabHeight(int tab)
        {
            switch (tab)
            {
                case 0: return 470f;   // curves canvas plus presets
                case 1: return 430f;   // two rows of wheels
                case 2: return 260f;   // LUT picker
                case 3: return 330f;   // eight HSL rows
                default: return 250f;  // info
            }
        }

        private void Draw(int id)
        {
            var editor = ADOBase.editor;
            var events = Collect(editor);
            if (events.Count == 0) { GUILayout.Label("No grading event in this chart."); return; }

            DrawEventPicker(events);
            var entry = events[Mathf.Clamp(_eventIndex, 0, events.Count - 1)];
            if (entry.Event == null) return;

            // The panel is only meaningful while the row still exists; a deleted or undone
            // event leaves an empty slot rather than an exception.
            if (!LevelStorage.TryGetData(entry.Event, out var data)) { GUILayout.Label("Row is not attached."); return; }
            var manifest = entry.Manifest;

            GUILayout.BeginHorizontal();
            bool enabled = GetBool(entry.Event, "enabled", true);
            bool newEnabled = GUILayout.Toggle(enabled, "On", GUILayout.Width(42));
            if (newEnabled != enabled) SetValue(entry.Event, data, "enabled", newEnabled);

            if (GUILayout.Button("A/B", GUILayout.Width(40)))
                SetValue(entry.Event, data, "enabled", !enabled);
            if (GUILayout.Button("Reset", GUILayout.Width(52)))
                ResetAll(entry.Event, data, manifest);
            if (GUILayout.Button("Copy", GUILayout.Width(48)))
                CopySettings(entry.Event, manifest);
            if (GUILayout.Button("Paste", GUILayout.Width(50)))
                PasteSettings(entry.Event, data, manifest);
            GUILayout.FlexibleSpace();
            GUILayout.Label($"f{entry.Event.floor}", _smallStyle);
            GUILayout.EndHorizontal();

            GUILayout.Space(2);
            _tab = GUILayout.Toolbar(_tab, Tabs);
            GUILayout.Space(4);

            switch (_tab)
            {
                case 0: DrawCurves(entry.Event, data, manifest); break;
                case 1: DrawWheels(entry.Event, data, manifest); break;
                case 2: DrawLut(entry.Event, data, manifest); break;
                case 3: DrawHsl(entry.Event, data, manifest); break;
                default: DrawInfo(entry.Event, data, manifest); break;
            }

            GUI.DragWindow(new Rect(0, 0, WindowWidth, 20));
        }

        private struct Entry
        {
            internal LevelEvent Event;
            internal PEV.Model.EventManifest Manifest;
        }

        private static readonly List<Entry> Buffer = new List<Entry>();

        private static List<Entry> Collect(scnEditor editor)
        {
            Buffer.Clear();
            if (editor == null) return Buffer;
            AddRows(editor.events);
            var level = editor.levelData;
            if (level != null) AddRows(level.decorations);
            return Buffer;

            void AddRows(List<LevelEvent> rows)
            {
                if (rows == null) return;
                foreach (var ev in rows)
                {
                    if (ev == null || !ev.active) continue;
                    if (!LevelStorage.TryGetData(ev, out var data)) continue;
                    if (data.IsPlaceholder) continue;
                    if (!EventRegistry.TryGetById(data.Id, out var manifest)) continue;
                    if (manifest.ScreenEffect == null) continue;
                    Buffer.Add(new Entry { Event = ev, Manifest = manifest });
                }
            }
        }

        private void DrawEventPicker(List<Entry> events)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Event", GUILayout.Width(44));
            string label = $"{_eventIndex + 1}/{events.Count}  floor {events[Mathf.Clamp(_eventIndex, 0, events.Count - 1)].Event.floor}";
            if (GUILayout.Button("<", GUILayout.Width(24)))
                _eventIndex = (_eventIndex + events.Count - 1) % events.Count;
            GUILayout.Label(label, GUILayout.Width(120));
            if (GUILayout.Button(">", GUILayout.Width(24)))
                _eventIndex = (_eventIndex + 1) % events.Count;
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        // ── curves ────────────────────────────────────────────────────────────────

        private void DrawCurves(LevelEvent ev, EventInstanceData data, PEV.Model.EventManifest manifest)
        {
            var panel = manifest.EditorPanel;
            string masterParam = ParamFor(panel, "curves", "master", "curveMaster");
            string redParam = ParamFor(panel, "curves", "red", "curveRed");
            string greenParam = ParamFor(panel, "curves", "green", "curveGreen");
            string blueParam = ParamFor(panel, "curves", "blue", "curveBlue");
            string hueParam = ParamFor(panel, "hueCurves", "hue", "hueVsHue");
            string satParam = ParamFor(panel, "hueCurves", "saturation", "hueVsSat");
            string lumaParam = ParamFor(panel, "hueCurves", "luma", "hueVsLuma");

            bool hueMode = _curveChannel >= 4;
            string param;
            switch (_curveChannel)
            {
                case 0: param = masterParam; break;
                case 1: param = redParam; break;
                case 2: param = greenParam; break;
                case 3: param = blueParam; break;
                case 4: param = hueParam; break;
                case 5: param = satParam; break;
                default: param = lumaParam; break;
            }

            GUILayout.BeginHorizontal();
            for (int i = 0; i < Channels.Length; i++)
                if (GUILayout.Toggle(_curveChannel == i, Channels[i], GUI.skin.button))
                    _curveChannel = i;
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            for (int i = 0; i < HueChannels.Length; i++)
                if (GUILayout.Toggle(_curveChannel == 4 + i, HueChannels[i], GUI.skin.button))
                    _curveChannel = 4 + i;
            GUILayout.EndHorizontal();

            string text = GetString(ev, param, hueMode ? "0,0.5;1,0.5" : "0,0;1,1");
            var points = ParseCurve(text, hueMode);

            float size = Mathf.Min(WindowWidth - 24f, 300f);
            var canvas = GUILayoutUtility.GetRect(size, size);
            DrawCurveCanvas(canvas, points, hueMode, ev, data, param);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Identity")) SetValue(ev, data, param, hueMode ? "0,0.5;1,0.5" : "0,0;1,1");
            if (GUILayout.Button("S-Curve")) SetValue(ev, data, param, "0,0;0.25,0.15;0.75,0.85;1,1");
            if (GUILayout.Button("Contrast+")) SetValue(ev, data, param, "0,0;0.35,0.2;0.65,0.8;1,1");
            if (GUILayout.Button("Fade")) SetValue(ev, data, param, "0,0.08;1,0.95");
            GUILayout.EndHorizontal();
            GUILayout.Label("Click to add, drag to move, right-click a point to remove.", _smallStyle);
        }

        private void DrawCurveCanvas(Rect rect, List<Vector2> points, bool hueMode, LevelEvent ev,
            EventInstanceData data, string param)
        {
            var previous = GUI.color;
            GUI.color = new Color(0.09f, 0.09f, 0.11f, 1f);
            GUI.DrawTexture(rect, _white);
            GUI.color = previous;

            // Grid.
            GUI.color = new Color(1f, 1f, 1f, 0.10f);
            for (int i = 0; i <= 4; i++)
            {
                float t = i / 4f;
                GUI.DrawTexture(new Rect(rect.x, rect.y + rect.height * t, rect.width, 1f), _white);
                GUI.DrawTexture(new Rect(rect.x + rect.width * t, rect.y, 1f, rect.height), _white);
            }
            // Neutral reference: the diagonal for channel curves, the centre line for the
            // hue offsets, which are offsets from zero rather than from identity.
            GUI.color = new Color(1f, 1f, 1f, 0.22f);
            if (hueMode) GUI.DrawTexture(new Rect(rect.x, rect.y + rect.height * 0.5f, rect.width, 1f), _white);
            else DrawLine(rect, new Vector2(0, 1), new Vector2(1, 0), new Color(1f, 1f, 1f, 0.18f), 1f);
            GUI.color = previous;

            // Curve.
            var colour = _curveChannel switch
            {
                1 => new Color(1f, 0.42f, 0.42f),
                2 => new Color(0.42f, 1f, 0.5f),
                3 => new Color(0.45f, 0.6f, 1f),
                _ => new Color(0.92f, 0.92f, 0.92f)
            };
            Vector2 previousPoint = Vector2.zero;
            for (int i = 0; i <= CurveResolution; i++)
            {
                float x = i / (float)CurveResolution;
                float y = Evaluate(points, x);
                var pixel = ToPixel(rect, x, y);
                if (i > 0) DrawLine(rect, previousPoint, pixel, colour, 1.4f);
                previousPoint = pixel;
            }

            // Points.
            foreach (var point in points)
            {
                var pixel = ToPixel(rect, point.x, point.y);
                GUI.color = new Color(1f, 1f, 1f, 0.95f);
                GUI.DrawTexture(new Rect(pixel.x - 3f, pixel.y - 3f, 6f, 6f), _white);
            }
            GUI.color = previous;

            HandleCurveInput(rect, points, hueMode, ev, data, param);
        }

        private void HandleCurveInput(Rect rect, List<Vector2> points, bool hueMode, LevelEvent ev,
            EventInstanceData data, string param)
        {
            var current = Event.current;
            if (current == null) return;

            Vector2 ToCurve(Vector2 pixel)
            {
                float x = Mathf.Clamp01((pixel.x - rect.x) / rect.width);
                float y = Mathf.Clamp01(1f - (pixel.y - rect.y) / rect.height);
                return new Vector2(x, y);
            }

            if (current.type == EventType.MouseDown && rect.Contains(current.mousePosition))
            {
                var target = ToCurve(current.mousePosition);
                int nearest = -1;
                float best = 0.03f;
                for (int i = 0; i < points.Count; i++)
                {
                    float distance = Mathf.Abs(points[i].x - target.x);
                    if (distance < best) { best = distance; nearest = i; }
                }

                if (current.button == 1)
                {
                    // Endpoints are kept: a curve with no 0 and no 1 stops being a tone curve.
                    if (nearest > 0 && nearest < points.Count - 1)
                    {
                        points.RemoveAt(nearest);
                        WriteCurve(ev, data, param, points, hueMode);
                    }
                }
                else
                {
                    if (nearest < 0)
                    {
                        nearest = points.FindIndex(p => p.x > target.x);
                        if (nearest < 0) nearest = points.Count;
                        points.Insert(nearest, target);
                    }
                    else points[nearest] = target;
                    _draggingCurve = true;
                    _dragPoint = nearest;
                    _dragEvent = param;
                    WriteCurve(ev, data, param, points, hueMode);
                }
                current.Use();
                return;
            }

            if (_draggingCurve && _dragEvent == param)
            {
                if (current.type == EventType.MouseDrag)
                {
                    var target = ToCurve(current.mousePosition);
                    // Endpoints keep their X so the curve still spans 0..1.
                    if (_dragPoint == 0) target.x = 0f;
                    else if (_dragPoint == points.Count - 1) target.x = 1f;
                    else
                    {
                        float low = points[_dragPoint - 1].x + 0.01f;
                        float high = points[_dragPoint + 1].x - 0.01f;
                        target.x = Mathf.Clamp(target.x, low, high);
                    }
                    points[_dragPoint] = target;
                    WriteCurve(ev, data, param, points, hueMode);
                    current.Use();
                }
                else if (current.type == EventType.MouseUp)
                {
                    _draggingCurve = false;
                    _dragPoint = -1;
                    _dragEvent = null;
                    current.Use();
                }
            }
        }

        private static Vector2 ToPixel(Rect rect, float x, float y) =>
            new Vector2(rect.x + x * rect.width, rect.y + (1f - y) * rect.height);

        private static float Evaluate(List<Vector2> points, float x)
        {
            if (points.Count == 0) return x;
            if (x <= points[0].x) return points[0].y;
            if (x >= points[points.Count - 1].x) return points[points.Count - 1].y;
            for (int i = 0; i + 1 < points.Count; i++)
            {
                if (x > points[i + 1].x) continue;
                float span = Mathf.Max(1e-5f, points[i + 1].x - points[i].x);
                float t = (x - points[i].x) / span;
                // Smoothstep matches the monotone feel of the shader's baked table closely
                // enough to drag against; the exact curve comes from the shader's own bake.
                t = t * t * (3f - 2f * t);
                return Mathf.Lerp(points[i].y, points[i + 1].y, t);
            }
            return x;
        }

        private void DrawLine(Rect clip, Vector2 from, Vector2 to, Color colour, float width)
        {
            var delta = to - from;
            float length = delta.magnitude;
            if (length < 0.01f) return;
            float angle = Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg;
            var saved = GUI.matrix;
            GUI.color = colour;
            GUIUtility.RotateAroundPivot(angle, from);
            GUI.DrawTexture(new Rect(from.x, from.y - width * 0.5f, length, width), _white);
            GUI.matrix = saved;
            GUI.color = Color.white;
        }

        // ── wheels ────────────────────────────────────────────────────────────────

        private void DrawWheels(LevelEvent ev, EventInstanceData data, PEV.Model.EventManifest manifest)
        {
            var wheels = manifest.EditorPanel?["wheels"] as Newtonsoft.Json.Linq.JArray;
            if (wheels == null || wheels.Count == 0)
            {
                GUILayout.Label("This package declares no colour wheels.");
                return;
            }

            float size = (WindowWidth - 40f) * 0.5f;
            for (int i = 0; i < wheels.Count; i += 2)
            {
                GUILayout.BeginHorizontal();
                for (int j = i; j < Mathf.Min(i + 2, wheels.Count); j++)
                    DrawWheel(ev, data, wheels[j], size);
                GUILayout.EndHorizontal();
            }
        }

        private void DrawWheel(LevelEvent ev, EventInstanceData data, Newtonsoft.Json.Linq.JToken spec, float size)
        {
            string xyName = spec?.Value<string>("xy");
            string lumaName = spec?.Value<string>("luma");
            string label = spec?["label"] != null ? LocaleLabel(spec["label"]) : "Wheel";

            GUILayout.BeginVertical(GUILayout.Width(size + 8f));
            GUILayout.Label(label, _smallStyle);
            var rect = GUILayoutUtility.GetRect(size, size);
            GUI.DrawTexture(rect, _wheel);

            var value = GetVec2(ev, xyName, Vector2.zero);
            if (value.magnitude > 1f) value = value.normalized;
            var centre = new Vector2(rect.x + rect.width * 0.5f, rect.y + rect.height * 0.5f);
            var puck = new Vector2(centre.x + value.x * rect.width * 0.5f,
                centre.y - value.y * rect.height * 0.5f);

            var previous = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.85f);
            GUI.DrawTexture(new Rect(puck.x - 5f, puck.y - 5f, 10f, 10f), _white);
            GUI.color = Color.white;
            GUI.DrawTexture(new Rect(puck.x - 4f, puck.y - 4f, 8f, 8f), _white);
            GUI.color = new Color(1f, 1f, 1f, 0.5f);
            GUI.DrawTexture(new Rect(centre.x - 1f, centre.y - 1f, 2f, 2f), _white);
            GUI.color = previous;

            var current = Event.current;
            if (current != null && current.type == EventType.MouseDown && rect.Contains(current.mousePosition))
            {
                Vector2 local = (current.mousePosition - centre) / (rect.width * 0.5f);
                local.y = -local.y;
                if (local.magnitude > 1f) local = local.normalized;
                SetValue(ev, data, xyName, local);
                current.Use();
            }

            float luma = GetFloat(ev, lumaName, 0f);
            float newLuma = GUILayout.HorizontalSlider(luma, -1f, 1f);
            if (!Mathf.Approximately(newLuma, luma)) SetValue(ev, data, lumaName, Mathf.Round(newLuma * 100f) / 100f);
            if (GUILayout.Button("0", GUILayout.Height(18)))
            {
                SetValue(ev, data, xyName, Vector2.zero);
                SetValue(ev, data, lumaName, 0f);
            }
            GUILayout.EndVertical();
        }

        // ── LUT ───────────────────────────────────────────────────────────────────

        private void DrawLut(LevelEvent ev, EventInstanceData data, PEV.Model.EventManifest manifest)
        {
            var lut = manifest.EditorPanel?["lut"];
            string presetName = lut?.Value<string>("preset") ?? "lutPreset";
            string fileName = lut?.Value<string>("file") ?? "lutFile";
            string strengthName = lut?.Value<string>("strength") ?? "lutStrength";

            string preset = GetString(ev, presetName, "None");
            GUILayout.Label("Built-in look", _smallStyle);
            var options = new[] { "None", "BundledTealOrange", "BundledBleachBypass", "BundledWarmFilm" };
            var labels = new[] { "None", "Teal & Orange", "Bleach Bypass", "Warm Film" };
            GUILayout.BeginHorizontal();
            for (int i = 0; i < options.Length; i++)
                if (GUILayout.Toggle(preset == options[i], labels[i], GUI.skin.button) && preset != options[i])
                    SetValue(ev, data, presetName, options[i]);
            GUILayout.EndHorizontal();

            GUILayout.Space(4);
            GUILayout.Label("Custom .cube (in the package or beside the chart):", _smallStyle);
            string file = GetString(ev, fileName, "");
            string edited = GUILayout.TextField(file ?? "", GUILayout.Height(20));
            if (edited != file) SetValue(ev, data, fileName, edited);

            float strength = GetFloat(ev, strengthName, 1f);
            GUILayout.Label($"LUT strength  {strength:F2}", _smallStyle);
            float newStrength = GUILayout.HorizontalSlider(strength, 0f, 1f);
            if (!Mathf.Approximately(newStrength, strength))
                SetValue(ev, data, strengthName, Mathf.Round(newStrength * 100f) / 100f);

            GUILayout.Space(4);
            GUILayout.Label("A built-in look wins over the file name; set it to None to use the file.",
                _smallStyle);
        }

        // ── HSL ranges ────────────────────────────────────────────────────────────

        private void DrawHsl(LevelEvent ev, EventInstanceData data, PEV.Model.EventManifest manifest)
        {
            string name = "hslBands";
            var bands = ParseBands(GetString(ev, name, ""));

            GUILayout.Label("Hue shift / saturation / luminance per range", _smallStyle);
            for (int i = 0; i < 8; i++)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(BandNames[i], _smallStyle, GUILayout.Width(60));
                float hue = GUILayout.HorizontalSlider(bands[i].x, -60f, 60f, GUILayout.Width(70));
                float sat = GUILayout.HorizontalSlider(bands[i].y, -1f, 1f, GUILayout.Width(70));
                float lum = GUILayout.HorizontalSlider(bands[i].z, -1f, 1f, GUILayout.Width(70));
                if (!Mathf.Approximately(hue, bands[i].x) || !Mathf.Approximately(sat, bands[i].y) ||
                    !Mathf.Approximately(lum, bands[i].z))
                {
                    bands[i] = new Vector3(Mathf.Round(hue), Mathf.Round(sat * 100f) / 100f,
                        Mathf.Round(lum * 100f) / 100f);
                    SetValue(ev, data, name, FormatBands(bands));
                }
                GUILayout.EndHorizontal();
            }
            if (GUILayout.Button("Reset ranges")) SetValue(ev, data, name, FormatBands(new List<Vector3>
                { Vector3.zero, Vector3.zero, Vector3.zero, Vector3.zero, Vector3.zero, Vector3.zero, Vector3.zero, Vector3.zero }));
        }

        // ── info ──────────────────────────────────────────────────────────────────

        private void DrawInfo(LevelEvent ev, EventInstanceData data, PEV.Model.EventManifest manifest)
        {
            GUILayout.Label($"{manifest.Id}  {manifest.Version}", _smallStyle);
            GUILayout.Label("Order applied: exposure, white balance, gain/offset, wheels, tone, " +
                            "contrast, gamma, curves, HSL, LUT.", _smallStyle);
            GUILayout.Space(4);
            GUILayout.Label("Combine", _smallStyle);
            string mode = GetString(ev, "mode", "Replace");
            GUILayout.BeginHorizontal();
            if (GUILayout.Toggle(mode == "Replace", "Replace previous", GUI.skin.button) && mode != "Replace")
                SetValue(ev, data, "mode", "Replace");
            if (GUILayout.Toggle(mode == "Stack", "Stack", GUI.skin.button) && mode != "Stack")
                SetValue(ev, data, "mode", "Stack");
            GUILayout.EndHorizontal();
            GUILayout.Label("Replace: only the newest grade is shown. Stack: later grades are applied " +
                            "on top of earlier ones.", _smallStyle);
            GUILayout.Space(4);
            GUILayout.Label("Runtime: " + Scripting.ScreenEffectPipeline.Report(), _smallStyle);
        }

        // ── value plumbing ────────────────────────────────────────────────────────

        private static string ParamFor(Newtonsoft.Json.Linq.JObject panel, string group, string key, string fallback)
        {
            var node = panel?[group];
            return node?[key]?.Type == Newtonsoft.Json.Linq.JTokenType.String
                ? node[key].ToString() : fallback;
        }

        private static string LocaleLabel(Newtonsoft.Json.Linq.JToken label)
        {
            if (label == null) return "";
            if (label.Type == Newtonsoft.Json.Linq.JTokenType.String) return label.ToString();
            string locale = "English";
            try { locale = RDString.language.ToString(); } catch { }
            var value = label[locale] ?? label["en"] ?? label["English"];
            return value != null && value.Type == Newtonsoft.Json.Linq.JTokenType.String ? value.ToString() : "";
        }

        private static bool GetBool(LevelEvent ev, string name, bool fallback)
        {
            try
            {
                object value = ev.Get<object>(name);
                if (value is bool flag) return flag;
                if (value != null && bool.TryParse(value.ToString(), out bool parsed)) return parsed;
            }
            catch { }
            return fallback;
        }

        private static float GetFloat(LevelEvent ev, string name, float fallback)
        {
            if (string.IsNullOrEmpty(name)) return fallback;
            try
            {
                object value = ev.Get<object>(name);
                if (value == null) return fallback;
                return Convert.ToSingle(value, CultureInfo.InvariantCulture);
            }
            catch { return fallback; }
        }

        private static Vector2 GetVec2(LevelEvent ev, string name, Vector2 fallback)
        {
            if (string.IsNullOrEmpty(name)) return fallback;
            try
            {
                object value = ev.Get<object>(name);
                if (value is Vector2 vector) return vector;
                if (value is IList<float> list && list.Count >= 2) return new Vector2(list[0], list[1]);
            }
            catch { }
            return fallback;
        }

        private static string GetString(LevelEvent ev, string name, string fallback)
        {
            if (string.IsNullOrEmpty(name)) return fallback;
            try
            {
                object value = ev.Get<object>(name);
                return value == null ? fallback : value.ToString();
            }
            catch { return fallback; }
        }

        private static void SetValue(LevelEvent ev, EventInstanceData data, string name, object value)
        {
            if (string.IsNullOrEmpty(name) || ev == null) return;
            object coerced = value;
            try
            {
                ADOFAI.PropertyInfo info = null;
                ev.info?.propertiesInfo.TryGetValue(name, out info);
                coerced = ParamCoercion.Coerce(info, value);
            }
            catch { }
            try { ev[name] = coerced; } catch (Exception e) { PEV.Core.Plugin.Log("panel set failed: " + e.Message); }
            if (data != null) data.Set(name, coerced);
        }

        private void WriteCurve(LevelEvent ev, EventInstanceData data, string param, List<Vector2> points,
            bool hueMode)
        {
            points.Sort((a, b) => a.x.CompareTo(b.x));
            var builder = new StringBuilder();
            for (int i = 0; i < points.Count; i++)
            {
                if (i > 0) builder.Append(';');
                builder.Append(points[i].x.ToString("F4", CultureInfo.InvariantCulture)).Append(',')
                    .Append(points[i].y.ToString("F4", CultureInfo.InvariantCulture));
            }
            SetValue(ev, data, param, builder.ToString());
        }

        private static void ResetAll(LevelEvent ev, EventInstanceData data, PEV.Model.EventManifest manifest)
        {
            if (manifest?.Params == null) return;
            foreach (var param in manifest.Params)
            {
                if (param.Name == "enabled") continue;
                SetValue(ev, data, param.Name, param.Default);
            }
        }

        /// <summary>
        /// A grade is a parameter set, so reusing one is copying it.
        ///
        /// The clipboard lives for the session and holds plain values, which means copying a
        /// look between two events — or between two charts open in one session — works
        /// without a file, and the pasted result is indistinguishable from one typed by hand.
        /// </summary>
        private static Dictionary<string, object> _clipboard;

        private static void CopySettings(LevelEvent ev, PEV.Model.EventManifest manifest)
        {
            _clipboard = new Dictionary<string, object>(StringComparer.Ordinal);
            if (manifest?.Params == null) return;
            foreach (var param in manifest.Params)
            {
                if (param.Name == "enabled") continue;
                try
                {
                    object value = ev.Get<object>(param.Name);
                    if (value != null) _clipboard[param.Name] = value;
                }
                catch { }
            }
            PEV.Core.Plugin.Log($"grading panel: copied {_clipboard.Count} value(s)");
        }

        private static void PasteSettings(LevelEvent ev, EventInstanceData data, PEV.Model.EventManifest manifest)
        {
            if (_clipboard == null || _clipboard.Count == 0) return;
            if (manifest?.Params == null) return;
            foreach (var param in manifest.Params)
            {
                if (param.Name == "enabled") continue;
                if (_clipboard.TryGetValue(param.Name, out var value)) SetValue(ev, data, param.Name, value);
            }
        }

        private static List<Vector2> ParseCurve(string text, bool hueMode)
        {
            var points = new List<Vector2>();
            if (!string.IsNullOrEmpty(text))
            {
                foreach (var part in text.Split(';'))
                {
                    var pair = part.Split(',');
                    if (pair.Length < 2) continue;
                    if (!float.TryParse(pair[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)) continue;
                    if (!float.TryParse(pair[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y)) continue;
                    points.Add(new Vector2(Mathf.Clamp01(x), Mathf.Clamp01(y)));
                    if (points.Count >= 16) break;
                }
            }
            if (points.Count < 2) points.AddRange(hueMode
                ? new[] { new Vector2(0, .5f), new Vector2(1, .5f) }
                : new[] { new Vector2(0, 0), new Vector2(1, 1) });
            points.Sort((a, b) => a.x.CompareTo(b.x));
            return points;
        }

        private static List<Vector3> ParseBands(string text)
        {
            var bands = new List<Vector3>();
            if (!string.IsNullOrEmpty(text))
            {
                foreach (var part in text.Split(';'))
                {
                    var fields = part.Split(',');
                    if (fields.Length < 3) continue;
                    float.TryParse(fields[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float h);
                    float.TryParse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float s);
                    float.TryParse(fields[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float l);
                    bands.Add(new Vector3(h, s, l));
                    if (bands.Count >= 8) break;
                }
            }
            while (bands.Count < 8) bands.Add(Vector3.zero);
            return bands;
        }

        private static string FormatBands(List<Vector3> bands)
        {
            var builder = new StringBuilder();
            for (int i = 0; i < bands.Count; i++)
            {
                if (i > 0) builder.Append(';');
                builder.Append(bands[i].x.ToString("F0", CultureInfo.InvariantCulture)).Append(',')
                    .Append(bands[i].y.ToString("F2", CultureInfo.InvariantCulture)).Append(',')
                    .Append(bands[i].z.ToString("F2", CultureInfo.InvariantCulture));
            }
            return builder.ToString();
        }

        private void EnsureTextures()
        {
            if (_white == null)
            {
                _white = new Texture2D(1, 1, TextureFormat.RGBA32, false) { hideFlags = HideFlags.DontSave };
                _white.SetPixel(0, 0, Color.white);
                _white.Apply();
            }
            if (_wheel != null) return;

            const int size = 128;
            _wheel = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.DontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            var pixels = new Color32[size * size];
            float centre = (size - 1) * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x - centre) / centre;
                    float dy = (y - centre) / centre;
                    float radius = Mathf.Sqrt(dx * dx + dy * dy);
                    if (radius > 1f) { pixels[y * size + x] = new Color32(0, 0, 0, 0); continue; }
                    float hue = Mathf.Atan2(dy, dx) / (Mathf.PI * 2f);
                    if (hue < 0f) hue += 1f;
                    // Saturation ramps to the rim and value falls slightly, which is the
                    // convention every grading wheel uses and makes the middle read as neutral.
                    var colour = Color.HSVToRGB(hue, Mathf.Clamp01(radius), 1f - 0.12f * radius);
                    float alpha = radius > 0.92f ? Mathf.Clamp01((1f - radius) / 0.08f) : 1f;
                    pixels[y * size + x] = new Color32((byte)(colour.r * 255), (byte)(colour.g * 255),
                        (byte)(colour.b * 255), (byte)(alpha * 255));
                }
            }
            _wheel.SetPixels32(pixels);
            _wheel.Apply();
        }
    }
}
