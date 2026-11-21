using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using ADOFAI;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using PEV.Model;
using PEV.Scripting;
using PEV.Registry;
using PEV.Storage;
using UnityEngine;

namespace PEV.Editor
{
    /// <summary>Draws the screen-effect controls declared by a package.</summary>
    public class ScreenEffectPanel : MonoBehaviour
    {
        private const float WindowWidth = 340f;
        private const float ScopesWidth = 460f;
        private const int CurveResolution = 96;

        private static readonly string[] Tabs = { "Curves", "Wheels", "LUT", "HSL", "Scopes", "Info" };
        private static readonly string[] Channels = { "Master", "Red", "Green", "Blue" };
        private static readonly string[] HueChannels = { "Hue vs Hue", "Hue vs Sat", "Hue vs Luma" };

        private Rect _window = new Rect(-1f, 96f, WindowWidth, 470f);
        private int _tab;
        private int _curveChannel;
        private int _eventIndex;
        private Vector2 _bodyScroll;

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
                catch (Exception e) { PEV.Core.Plugin.Log("screen effect panel attach failed: " + e.Message); }
            }
        }

#if PEV_DIAGNOSTICS
        /// <summary>Diagnostics hook: lets the probe photograph each tab.</summary>
        internal void ProbeTab(int tab) => _tab = tab;
#endif

        private void OnDestroy()
        {
            Scripting.ScreenEffectPipeline.CaptureScopes = false;
            Scripting.ScreenEffectPipeline.ReleaseScope();
            _scopes?.Dispose();
            _scopes = null;
            if (_wheel != null) Destroy(_wheel);
            if (_white != null) Destroy(_white);
        }

        private void OnGUI()
        {
            var editor = ADOBase.editor;
            if (editor == null || editor.playMode)
            {
                StopScopeCapture();
                return;
            }

            var events = Collect(editor);
            if (events.Count == 0)
            {
                StopScopeCapture();
                return;
            }

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
            _window.width = _tab == 4 ? ScopesWidth : WindowWidth;
            _window.height = height;
            var manifest = events[_eventIndex].Manifest;
            string title = LocaleLabel(manifest.EditorPanel["title"]);
            if (string.IsNullOrEmpty(title)) title = manifest.LocaleName(RDString.language.ToString());
            _window = GUILayout.Window(GetInstanceID(), _window, Draw, title);
        }

        private static float TabHeight(int tab)
        {
            switch (tab)
            {
                case 0: return 470f;   // curves canvas plus presets
                case 1: return 430f;   // two rows of wheels
                case 2: return 260f;   // LUT picker
                case 3: return 330f;   // eight HSL rows
                case 4: return 470f;   // four scopes
                default: return 250f;  // info
            }
        }

        private void Draw(int id)
        {
            var editor = ADOBase.editor;
            var events = Collect(editor);
            if (events.Count == 0) { GUILayout.Label("No event with a screen effect panel."); return; }

            DrawEventPicker(events);
            var entry = events[Mathf.Clamp(_eventIndex, 0, events.Count - 1)];
            if (entry.Event == null) return;

            // The panel is only meaningful while the row still exists; a deleted or undone
            // event leaves an empty slot rather than an exception.
            if (!LevelStorage.TryGetData(entry.Event, out var data)) { GUILayout.Label("Row is not attached."); return; }
            var manifest = entry.Manifest;

            GUILayout.BeginHorizontal();
            string compare = manifest.EditorPanel.Value<string>("compare");
            if (compare != null)
            {
                bool enabled = GetBool(entry.Event, compare, true);
                bool newEnabled = GUILayout.Toggle(enabled, "On", GUILayout.Width(42));
                if (GUILayout.Button("A/B", GUILayout.Width(40))) newEnabled = !enabled;
                if (newEnabled != enabled) SetValue(entry.Event, data, compare, newEnabled);
            }
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
            var panel = manifest.EditorPanel;
            var tabs = new List<int>();
            if (panel["curves"] != null || panel["hueCurves"] != null) tabs.Add(0);
            if (panel["wheels"] != null) tabs.Add(1);
            if (panel["lut"] != null) tabs.Add(2);
            if (panel["hsl"] != null) tabs.Add(3);
            if (panel.Value<bool?>("scopes") ?? true) tabs.Add(4);
            tabs.Add(5);
            int selected = Mathf.Max(0, tabs.IndexOf(_tab));
            int current = tabs[GUILayout.Toolbar(selected, tabs.Select(index => Tabs[index]).ToArray())];
            if (_tab != current) _bodyScroll = Vector2.zero;
            _tab = current;
            if (_tab != 4) StopScopeCapture();
            GUILayout.Space(4);

            _bodyScroll = GUILayout.BeginScrollView(_bodyScroll);
            switch (_tab)
            {
                case 0: DrawCurves(entry.Event, data, manifest); break;
                case 1: DrawWheels(entry.Event, data, manifest); break;
                case 2: DrawLut(entry.Event, data, manifest); break;
                case 3: DrawHsl(entry.Event, data, manifest); break;
                case 4: DrawScopes(); break;
                default: DrawInfo(entry.Event, data, manifest); break;
            }

            GUILayout.EndScrollView();
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
                    if (manifest.ScreenEffect == null || manifest.EditorPanel == null) continue;
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
            string[] parameters =
            {
                ParamFor(panel, "curves", "master"), ParamFor(panel, "curves", "red"),
                ParamFor(panel, "curves", "green"), ParamFor(panel, "curves", "blue"),
                ParamFor(panel, "hueCurves", "hue"), ParamFor(panel, "hueCurves", "saturation"),
                ParamFor(panel, "hueCurves", "luma")
            };
            int first = Array.FindIndex(parameters, name => !string.IsNullOrEmpty(name));
            if (first < 0) return;
            if (parameters[_curveChannel] == null) _curveChannel = first;

            GUILayout.BeginHorizontal();
            for (int i = 0; i < Channels.Length; i++)
                if (parameters[i] != null && GUILayout.Toggle(_curveChannel == i, Channels[i], GUI.skin.button))
                    _curveChannel = i;
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            for (int i = 0; i < HueChannels.Length; i++)
                if (parameters[4 + i] != null && GUILayout.Toggle(_curveChannel == 4 + i, HueChannels[i], GUI.skin.button))
                    _curveChannel = 4 + i;
            GUILayout.EndHorizontal();

            bool hueMode = _curveChannel >= 4;
            string param = parameters[_curveChannel];
            string identity = hueMode ? "0,0.5;1,0.5" : "0,0;1,1";
            string text = GetString(ev, param, identity);
            var points = ParseCurve(text, hueMode);

            float size = Mathf.Min(WindowWidth - 24f, 300f);
            var canvas = GUILayoutUtility.GetRect(size, size);
            DrawCurveCanvas(canvas, points, hueMode, ev, data, param);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Identity")) SetValue(ev, data, param, identity);
            if (!hueMode && panel["curvePresets"] is JArray presets)
                foreach (var preset in presets)
                    if (GUILayout.Button(LocaleLabel(preset["label"])))
                        SetValue(ev, data, param, preset.Value<string>("value"));
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
            var lut = (JObject)manifest.EditorPanel["lut"];
            DrawChoices(ev, data, manifest, lut.Value<string>("preset"));
            string fileName = lut.Value<string>("file");
            if (fileName != null)
            {
                GUILayout.Label(".cube path in the package or chart", _smallStyle);
                string file = GetString(ev, fileName, "");
                string edited = GUILayout.TextField(file, GUILayout.Height(20));
                if (edited != file) SetValue(ev, data, fileName, edited);
            }

            string strengthName = lut.Value<string>("strength");
            if (strengthName != null)
            {
                float strength = GetFloat(ev, strengthName, 1f);
                GUILayout.Label($"LUT strength  {strength:F2}", _smallStyle);
                float value = GUILayout.HorizontalSlider(strength, 0f, 1f);
                if (!Mathf.Approximately(value, strength))
                    SetValue(ev, data, strengthName, Mathf.Round(value * 100f) / 100f);
            }
            string note = LocaleLabel(lut["note"]);
            if (note.Length > 0) GUILayout.Label(note, _smallStyle);
        }

        private void DrawHsl(LevelEvent ev, EventInstanceData data, EventManifest manifest)
        {
            var hsl = manifest.EditorPanel["hsl"];
            string name = hsl.Value<string>("parameter");
            var labels = (JArray)hsl["bands"];
            var bands = ParseBands(GetString(ev, name, ""), labels.Count);

            GUILayout.Label("Hue shift / saturation / luminance per range", _smallStyle);
            for (int i = 0; i < bands.Count; i++)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(LocaleLabel(labels[i]), _smallStyle, GUILayout.Width(60));
                float hue = GUILayout.HorizontalSlider(bands[i].x, -60f, 60f, GUILayout.Width(65));
                float sat = GUILayout.HorizontalSlider(bands[i].y, -1f, 1f, GUILayout.Width(65));
                float lum = GUILayout.HorizontalSlider(bands[i].z, -1f, 1f, GUILayout.Width(65));
                if (!Mathf.Approximately(hue, bands[i].x) || !Mathf.Approximately(sat, bands[i].y) ||
                    !Mathf.Approximately(lum, bands[i].z))
                {
                    bands[i] = new Vector3(Mathf.Round(hue), Mathf.Round(sat * 100f) / 100f,
                        Mathf.Round(lum * 100f) / 100f);
                    SetValue(ev, data, name, FormatBands(bands));
                }
                GUILayout.EndHorizontal();
            }
            if (GUILayout.Button("Reset ranges"))
                SetValue(ev, data, name, FormatBands(Enumerable.Repeat(Vector3.zero, bands.Count).ToList()));
        }

        // ── scopes ────────────────────────────────────────────────────────────────

        private ScreenScopeView _scopes;

        private void DrawScopes()
        {
            if (_scopes == null) _scopes = new ScreenScopeView();
            // The driver only keeps the small frame while this flag is set, so closing the
            // panel stops the capture as well as the analysis.
            Scripting.ScreenEffectPipeline.CaptureScopes = true;
            _scopes.Refresh();

            if (!_scopes.Ready)
            {
                GUILayout.Label("Enable a screen effect to see the scopes.",
                    _smallStyle);
                GUILayout.Space(4);
            }
            _scopes.Draw(_window.width - 24f);
            GUILayout.Label("Sampled four times a second from the screen effect input.", _smallStyle);
        }

        // ── info ──────────────────────────────────────────────────────────────────

        private static void StopScopeCapture()
        {
            if (!ScreenEffectPipeline.CaptureScopes) return;
            ScreenEffectPipeline.CaptureScopes = false;
            ScreenEffectPipeline.ReleaseScope();
        }

        private void DrawInfo(LevelEvent ev, EventInstanceData data, EventManifest manifest)
        {
            string locale = RDString.language.ToString();
            GUILayout.Label($"{manifest.Id}  {manifest.Version}", _smallStyle);
            string summary = LocaleLabel(manifest.EditorPanel["summary"]);
            GUILayout.Label(summary.Length > 0 ? summary : manifest.LocaleDescription(locale), _smallStyle);
            DrawChoices(ev, data, manifest, manifest.EditorPanel.Value<string>("composition"));
        }

        private static void DrawChoices(LevelEvent ev, EventInstanceData data, EventManifest manifest, string name)
        {
            var param = manifest.Params.Find(candidate => candidate.Name == name);
            if (param?.Options == null) return;
            string current = GetString(ev, name, param.Default as string);
            string locale = RDString.language.ToString();
            GUILayout.BeginHorizontal();
            for (int i = 0; i < param.Options.Count; i++)
            {
                if (i > 0 && i % 3 == 0)
                {
                    GUILayout.EndHorizontal();
                    GUILayout.BeginHorizontal();
                }
                string option = param.Options[i];
                var labels = param.OptionLabels.Find(item => item.Key == option)?.Value;
                string label = labels != null && labels.TryGetValue(locale, out var text) ? text :
                    labels != null && labels.TryGetValue("English", out var english) ? english : option;
                if (GUILayout.Toggle(current == option, label, GUI.skin.button) && current != option)
                    SetValue(ev, data, name, option);
            }
            GUILayout.EndHorizontal();
        }

        private static string ParamFor(JObject panel, string group, string key) =>
            (panel[group] as JObject)?.Value<string>(key);

        private static string LocaleLabel(Newtonsoft.Json.Linq.JToken label)
        {
            return EventManifest.LocalizedText(label, RDString.language.ToString());
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
                if (param.Name == manifest.EditorPanel.Value<string>("compare")) continue;
                SetValue(ev, data, param.Name, param.Default);
            }
        }

        /// <summary>
        /// Copies the parameters between instances of a package during the editor session.
        /// </summary>
        private static Dictionary<string, object> _clipboard;
        private static string _clipboardId;

        private static void CopySettings(LevelEvent ev, PEV.Model.EventManifest manifest)
        {
            _clipboard = new Dictionary<string, object>(StringComparer.Ordinal);
            _clipboardId = manifest.Id;
            foreach (var param in manifest.Params)
            {
                if (param.Name == manifest.EditorPanel.Value<string>("compare")) continue;
                try
                {
                    object value = ev.Get<object>(param.Name);
                    if (value != null) _clipboard[param.Name] = value;
                }
                catch { }
            }
            PEV.Core.Plugin.Log($"screen effect panel: copied {_clipboard.Count} value(s)");
        }

        private static void PasteSettings(LevelEvent ev, EventInstanceData data, PEV.Model.EventManifest manifest)
        {
            if (_clipboard == null || _clipboard.Count == 0 || _clipboardId != manifest.Id) return;
            if (manifest?.Params == null) return;
            foreach (var param in manifest.Params)
            {
                if (param.Name == manifest.EditorPanel.Value<string>("compare")) continue;
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

        private static List<Vector3> ParseBands(string text, int count)
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
                    if (bands.Count >= count) break;
                }
            }
            while (bands.Count < count) bands.Add(Vector3.zero);
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
