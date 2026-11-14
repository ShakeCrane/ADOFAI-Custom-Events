using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace PEV.Scripting
{
    /// <summary>
    /// Owns NoiseField instances and applies one frame of script state per call.
    ///
    /// Rendering unit is a LAYER, not a field: every field sharing a depth is composited
    /// into one texture so overlapping regions take the union of their masks. Drawing them
    /// as separate quads would double their brightness wherever they overlap, which is
    /// visibly wrong the moment two fields at the same depth touch.
    ///
    /// Deliberately holds no animation state: frame index and UV offset arrive from the
    /// script every update, computed from the song clock. Anything accumulated here would
    /// desync on pause or a checkpoint jump, and there would be two clocks to reconcile.
    /// </summary>
    public static class NoiseFieldRenderer
    {
        /// <summary>
        /// Public because ByTag returns it. Nested so PEV.Scripting gains no top-level type.
        /// </summary>
        public sealed class Entry
        {
            public string Key;
            public string Scope;
            public string Tag;
            public int FloorSeq;
            public int Depth;
            public int SortingOrder;

            public float Opacity;
            public bool Visible;
            public int FrameIndex;
            public Vector2 Uv;

            // Geometry as reported by the script, kept per field so the layer can be
            // recomposited when any member changes.
            public Placement.Frame World;
            public NoiseFieldShape Shape;
            public float Feather;
            public Color Tint;
            public float Brightness;
            public float Saturation;
            public float SampleRate;
            public float LineOpacity;
            public int FrameCount;
            public float PxWidthHint;
            public Vector2 Tiling = Vector2.one;

            /// <summary>Set by the layer when it composites; diagnostics point at the quad.</summary>
            public GameObject GameObject;
        }

        private sealed class Layer
        {
            public GameObject GameObject;
            public SpriteRenderer Renderer;
            public NoiseTextureFactory.Frames Frames;
            public string StructureKey;
            public int FrameCount = 2;
            public Vector2 Uv;
            public int FrameIndex;
            public float Alpha = 1f;
        }

        private static readonly Dictionary<string, Entry> Live =
            new Dictionary<string, Entry>(StringComparer.Ordinal);

        /// <summary>
        /// One quad per overlapping cluster of same-depth fields, keyed "depth#cluster".
        /// Clustering is by bounding-box overlap: unioning two distant fields into one
        /// texture stretches the grains across the gap between them, which looks far worse
        /// than rendering them separately.
        /// </summary>
        private static readonly Dictionary<string, Layer> Layers =
            new Dictionary<string, Layer>();

        public static int LiveCount => Live.Count;
        public static int LayerCount => Layers.Count;
        public static int TextureSets => NoiseTextureFactory.CachedFrameSets;
        public static string LayerReport()
        {
            var sb = new StringBuilder();
            foreach (var kv in Layers)
            {
                var t = kv.Value.GameObject != null ? kv.Value.GameObject.transform : null;
                sb.Append(kv.Key).Append('=')
                  .Append(t != null ? $"{t.localScale.x:F1}x{t.localScale.y:F1}" : "gone")
                  .Append("; ");
            }
            return sb.ToString();
        }

        /// <summary>Snapshot for diagnostics and for tag-driven events that need iteration.</summary>
        public static List<Entry> AllEntries()
        {
            var list = new List<Entry>(Live.Count);
            foreach (var kv in Live) list.Add(kv.Value);
            list.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            return list;
        }

        private static string ScopeOf(Dictionary<string, object> args)
        {
            args.TryGetValue("__instance", out var inst);
            return inst?.ToString() ?? "";
        }

        private static string Scoped(string scope, string key) => scope + "/" + (key ?? "");

        public static bool Set(string key, Dictionary<string, object> args, out string error)
        {
            error = null;
            string scope = ScopeOf(args);
            string id = Scoped(scope, key);

            if (!Live.TryGetValue(id, out var entry))
            {
                entry = new Entry { Key = id, Scope = scope };
                Live[id] = entry;
            }

            int oldDepth = entry.Depth;
            try
            {
                Apply(entry, args);
            }
            catch (Exception e)
            {
                error = "fx.field apply failed: " + e.Message;
                return false;
            }

            // A depth change moves the field between layers; both sides need recompositing.
            if (oldDepth != entry.Depth && Live.Count > 1)
                RebuildDepth(oldDepth);

            return RebuildDepth(entry.Depth);
        }

        public static bool Destroy(string key, Dictionary<string, object> args, out string error)
        {
            error = null;
            string id = Scoped(ScopeOf(args), key);
            if (!Live.TryGetValue(id, out var entry)) return true;

            Live.Remove(id);
            RebuildDepth(entry.Depth);
            return true;
        }

        public static void DestroyAll()
        {
            foreach (var kv in Layers)
            {
                try
                {
                    if (kv.Value.GameObject != null) UnityEngine.Object.Destroy(kv.Value.GameObject);
                    NoiseTextureFactory.Release(kv.Value.Frames);
                }
                catch { }
            }
            Layers.Clear();
            Live.Clear();
            NoiseTextureFactory.Clear();
        }

        /// <summary>Fields registered under a tag (space-separated tags match any of them).</summary>
        public static List<Entry> ByTag(string tag)
        {
            var result = new List<Entry>();
            if (string.IsNullOrEmpty(tag)) return result;
            var wanted = tag.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var kv in Live)
            {
                var e = kv.Value;
                if (string.IsNullOrEmpty(e.Tag)) continue;
                var have = new HashSet<string>(
                    e.Tag.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries),
                    StringComparer.Ordinal);
                foreach (var w in wanted)
                    if (have.Contains(w)) { result.Add(e); break; }
            }
            return result;
        }

        private static void Apply(Entry entry, Dictionary<string, object> args)
        {
            var cam = GameCamera.Current;
            var placement = BuildInput(args, entry, cam);
            var frame = Placement.Compute(placement);

            entry.World = frame;
            entry.Shape = ParseEnum(args, "shape", NoiseFieldShape.Rect);
            entry.Feather = Mathf.Max(0f, ParseFloat(args, "feather", 0f));
            entry.Tint = ParseColor(args, "color", new Color(1f, 0.125f, 0.125f));
            entry.Brightness = Mathf.Max(0f, ParseFloat(args, "brightness", 1f));
            entry.Saturation = Mathf.Max(0f, ParseFloat(args, "saturation", 1f));
            entry.SampleRate = Mathf.Max(0.1f, ParseFloat(args, "sampleRate", 1f));
            entry.LineOpacity = Mathf.Clamp01(ParseFloat(args, "lineOpacity", 0.35f));
            entry.FrameCount = Mathf.Clamp(ParseInt(args, "frameCount", 8), 2, 32);
            entry.Visible = ParseBool(args, "visible", true);
            entry.Opacity = Mathf.Clamp01(ParseFloat(args, "opacity", 1f));
            entry.Tag = ParseString(args, "tag", "");
            entry.FloorSeq = ParseInt(args, "__floor", entry.FloorSeq);
            entry.Depth = ParseInt(args, "depth", 0);

            entry.FrameIndex = ParseInt(args, "frame", 0);
            entry.Uv = ParseVector2(args, "uv", Vector2.zero);
            entry.Tiling = ParseVector2(args, "tiling", Vector2.one);
            if (entry.Tiling.x <= 0f) entry.Tiling.x = 1f;
            if (entry.Tiling.y <= 0f) entry.Tiling.y = 1f;

            float halfH = cam != null && cam.orthographic ? cam.orthographicSize : 5f;
            entry.PxWidthHint = frame.Size.x / (2f * Mathf.Max(1e-4f, halfH)) * Screen.height;
        }

        /// <summary>
        /// Rebuilds the quads for one depth. Fields at the same depth that overlap are
        /// composited into a single quad so their masks take a union instead of summing
        /// alpha; fields far apart stay separate, because unioning them would stretch the
        /// grain across the empty space between them.
        /// </summary>
        private static bool RebuildDepth(int depth)
        {
            var members = new List<Entry>();
            foreach (var kv in Live)
                if (kv.Value.Depth == depth) members.Add(kv.Value);
            members.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));

            // Greedy clustering by bounding-box overlap. Fields that do not touch can never
            // share a texel, so there is nothing to merge and everything to lose by merging.
            var clusters = new List<List<Entry>>();
            var boxes = new List<Rect>();
            foreach (var m in members)
            {
                Rect box = Bounds(m);
                int target = -1;
                for (int i = 0; i < boxes.Count; i++)
                {
                    if (boxes[i].Overlaps(box)) { target = i; break; }
                }
                if (target < 0)
                {
                    clusters.Add(new List<Entry> { m });
                    boxes.Add(box);
                }
                else
                {
                    clusters[target].Add(m);
                    var merged = Rect.MinMaxRect(
                        Mathf.Min(boxes[target].xMin, box.xMin), Mathf.Min(boxes[target].yMin, box.yMin),
                        Mathf.Max(boxes[target].xMax, box.xMax), Mathf.Max(boxes[target].yMax, box.yMax));
                    boxes[target] = merged;
                }
            }

            var wanted = new HashSet<string>(StringComparer.Ordinal);
            bool ok = true;
            for (int i = 0; i < clusters.Count; i++)
            {
                string key = depth + "#" + clusters[i][0].Key;
                wanted.Add(key);
                if (!CompositeCluster(key, depth, clusters[i])) ok = false;
            }

            // Layers whose cluster no longer exists are stale; reclaim them now rather than
            // letting a destroyed texture linger under a live renderer.
            var dead = new List<string>();
            foreach (var kv in Layers)
                if (kv.Key.StartsWith(depth + "#", StringComparison.Ordinal) && !wanted.Contains(kv.Key))
                    dead.Add(kv.Key);
            foreach (var key in dead)
            {
                var layer = Layers[key];
                if (layer.GameObject != null) UnityEngine.Object.Destroy(layer.GameObject);
                NoiseTextureFactory.Release(layer.Frames);
                Layers.Remove(key);
            }
            return ok;
        }

        private static bool CompositeCluster(string key, int depth, List<Entry> members)
        {
            int sortingOrder = 900 + depth * 10;
            var lead = members[0];

            Vector2 min = new Vector2(float.MaxValue, float.MaxValue);
            Vector2 max = new Vector2(float.MinValue, float.MinValue);
            foreach (var m in members) Accumulate(m, ref min, ref max);

            Vector2 size = new Vector2(Mathf.Max(0.001f, max.x - min.x), Mathf.Max(0.001f, max.y - min.y));
            Vector2 centre = (min + max) * 0.5f;

            var pieces = new NoiseTextureFactory.Piece[members.Count];
            for (int i = 0; i < members.Count; i++)
            {
                var m = members[i];
                pieces[i] = new NoiseTextureFactory.Piece
                {
                    Offset = new Vector2(m.World.Center.x - centre.x, m.World.Center.y - centre.y),
                    Half = new Vector2(m.World.Size.x * 0.5f, m.World.Size.y * 0.5f),
                    Rotation = m.World.Rotation,
                    Feather = m.Feather,
                    Circle = m.Shape == NoiseFieldShape.Circle
                };
            }

            var request = new NoiseTextureFactory.Request
            {
                WidthWorld = size.x,
                HeightWorld = size.y,
                Pieces = pieces,
                Tint = lead.Tint,
                Brightness = lead.Brightness,
                Saturation = lead.Saturation,
                SampleRate = lead.SampleRate,
                LineOpacity = lead.LineOpacity,
                FrameCount = lead.FrameCount,
                Seed = StableSeed(lead.Scope),
                TargetPixelWidth = lead.PxWidthHint * (size.x / Mathf.Max(1e-4f, lead.World.Size.x))
            };

            string structureKey = StructureKey(request);

            if (!Layers.TryGetValue(key, out var layer) || layer.GameObject == null)
            {
                var go = EffectCanvas.CreateObject("PEV.Field." + key);
                var sr = go.AddComponent<SpriteRenderer>();
                sr.sortingOrder = sortingOrder;
                layer = new Layer { GameObject = go, Renderer = sr };
                Layers[key] = layer;
            }

            if (layer.Frames == null || layer.StructureKey != structureKey)
            {
                var frames = NoiseTextureFactory.Get(request, out string genError);
                if (frames == null)
                {
                    PEV.Core.Plugin.Log("noise layer: " + genError);
                    return false;
                }
                NoiseTextureFactory.Release(layer.Frames);
                layer.Frames = frames;
                layer.StructureKey = structureKey;
                layer.FrameCount = frames.Textures.Length;
            }

            int idx = lead.FrameIndex % layer.FrameCount;
            if (idx < 0) idx += layer.FrameCount;

            layer.GameObject.transform.position = new Vector3(centre.x, centre.y, 0f);
            layer.GameObject.transform.localScale = new Vector3(size.x, size.y, 1f);
            layer.GameObject.transform.rotation = Quaternion.identity;

            layer.Renderer.sprite = layer.Frames.Sprites[idx];
            layer.FrameIndex = idx;
            layer.Uv = lead.Uv;

            var mat = layer.Renderer.material;
            if (mat != null)
            {
                mat.mainTextureOffset = lead.Uv;
                mat.mainTextureScale = lead.Tiling;
            }

            // One pre-multiplied texture cannot vary alpha per region, so visibility and
            // opacity collapse to the strongest member of the cluster.
            float alpha = 0f;
            foreach (var m in members)
                if (m.Visible && m.Opacity > alpha) alpha = m.Opacity;
            layer.Alpha = alpha;
            layer.Renderer.color = new Color(1f, 1f, 1f, alpha);

            foreach (var m in members) m.GameObject = layer.GameObject;
            return true;
        }

        private static Rect Bounds(Entry m)
        {
            Vector2 min = new Vector2(float.MaxValue, float.MaxValue);
            Vector2 max = new Vector2(float.MinValue, float.MinValue);
            Accumulate(m, ref min, ref max);
            return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
        }

        private static void Accumulate(Entry m, ref Vector2 min, ref Vector2 max)
        {
            float rad = m.World.Rotation * Mathf.Deg2Rad;
            float cos = Mathf.Cos(rad), sin = Mathf.Sin(rad);
            float hw = m.World.Size.x * 0.5f, hh = m.World.Size.y * 0.5f;
            for (int sx = -1; sx <= 1; sx += 2)
            {
                for (int sy = -1; sy <= 1; sy += 2)
                {
                    float x = sx * hw, y = sy * hh;
                    var p = new Vector2(
                        m.World.Center.x + x * cos - y * sin,
                        m.World.Center.y + x * sin + y * cos);
                    if (p.x < min.x) min.x = p.x;
                    if (p.y < min.y) min.y = p.y;
                    if (p.x > max.x) max.x = p.x;
                    if (p.y > max.y) max.y = p.y;
                }
            }
        }

        private static string StructureKey(NoiseTextureFactory.Request r)
        {
            var sb = new StringBuilder(160);
            sb.Append(F(r.WidthWorld)).Append('|').Append(F(r.HeightWorld)).Append('|')
              .Append(F(r.Tint.r)).Append(',').Append(F(r.Tint.g)).Append(',').Append(F(r.Tint.b)).Append('|')
              .Append(F(r.Brightness)).Append('|').Append(F(r.Saturation)).Append('|')
              .Append(F(r.SampleRate)).Append('|').Append(F(r.LineOpacity)).Append('|')
              .Append(r.FrameCount).Append('|').Append(r.Seed).Append('|')
              .Append(Mathf.RoundToInt(r.TargetPixelWidth)).Append('|');
            if (r.Pieces != null)
            {
                foreach (var p in r.Pieces)
                    sb.Append(F(p.Offset.x)).Append(',').Append(F(p.Offset.y)).Append(',')
                      .Append(F(p.Half.x)).Append(',').Append(F(p.Half.y)).Append(',')
                      .Append(F(p.Rotation)).Append(',').Append(F(p.Feather)).Append(',')
                      .Append(p.Circle ? 'c' : 'r').Append(';');
            }
            return sb.ToString();
        }

        private static string F(float v) => v.ToString("F3", CultureInfo.InvariantCulture);

        private static Placement.Input BuildInput(Dictionary<string, object> args, Entry entry, Camera cam)
        {
            var relativeTo = ParseEnum(args, "relativeTo", NoiseFieldRelativeTo.Tile);
            var anchor = ParseEnum(args, "anchor", NoiseFieldAnchor.Center);
            var shape = ParseEnum(args, "shape", NoiseFieldShape.Rect);

            var input = new Placement.Input
            {
                RelativeTo = relativeTo,
                Anchor = anchor,
                Shape = shape,
                Size = ParseVector2(args, "size", new Vector2(4f, 3f)),
                Radius = ParseFloat(args, "radius", 2f),
                PositionOffset = ParseVector2(args, "position", Vector2.zero),
                Rotation = ParseFloat(args, "rotation", 0f),
                LockRotation = ParseBool(args, "lockRotation", false),
                LockSize = ParseBool(args, "lockSize", false),
                Parallax = ParseVector2(args, "parallax", Vector2.one),
                TileSize = TileSize(),
                CameraPosition = Vector3.zero,
                CameraRight = Vector3.right,
                CameraUp = Vector3.up,
                HalfWidth = 9f,
                HalfHeight = 5f,
                BasePosition = Vector3.zero,
                BaseRotation = 0f
            };

            if (cam != null)
            {
                input.CameraPosition = cam.transform.position;
                input.CameraRight = cam.transform.right;
                input.CameraUp = cam.transform.up;
                input.HalfHeight = cam.orthographic
                    ? cam.orthographicSize
                    : Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) * Mathf.Abs(cam.transform.position.z);
                input.HalfWidth = input.HalfHeight * cam.aspect;
            }

            switch (relativeTo)
            {
                case NoiseFieldRelativeTo.Camera:
                case NoiseFieldRelativeTo.CameraRatio:
                    input.BasePosition = input.CameraPosition;
                    break;

                case NoiseFieldRelativeTo.PlanetRed:
                case NoiseFieldRelativeTo.PlanetBlue:
                case NoiseFieldRelativeTo.PlanetGreen:
                    var planet = PlanetTransform(relativeTo);
                    if (planet != null)
                    {
                        input.BasePosition = planet.position;
                        input.BaseRotation = planet.eulerAngles.z;
                    }
                    else
                    {
                        // Missing planet (old level / different planet set): fall back to the
                        // tile rather than rendering at the origin with no explanation.
                        input.RelativeTo = NoiseFieldRelativeTo.Tile;
                        input.BasePosition = TilePosition(entry, out float rot);
                        input.BaseRotation = rot;
                    }
                    break;

                default:
                    input.BasePosition = TilePosition(entry, out float tileRot);
                    input.BaseRotation = tileRot;
                    break;
            }

            return input;
        }

        private static Vector3 TilePosition(Entry entry, out float rotation)
        {
            rotation = 0f;
            try
            {
                var lm = ADOBase.lm;
                if (lm != null && entry.FloorSeq >= 0 && entry.FloorSeq < lm.listFloors.Count)
                {
                    var t = lm.listFloors[entry.FloorSeq].transform;
                    rotation = t.eulerAngles.z;
                    return t.position;
                }
            }
            catch { }
            return Vector3.zero;
        }

        private static Transform PlanetTransform(NoiseFieldRelativeTo which)
        {
            try
            {
                var ctrl = ADOBase.controller;
                if (ctrl == null) return null;
                var r = ctrl.planetRed;
                var b = ctrl.planetBlue;
                var g = ctrl.planetGreen;
                var planet = which == NoiseFieldRelativeTo.PlanetRed ? r
                    : which == NoiseFieldRelativeTo.PlanetBlue ? b : g;
                return planet != null ? planet.transform : null;
            }
            catch { return null; }
        }

        private static float TileSize()
        {
            try
            {
                var ctrl = ADOBase.controller;
                if (ctrl != null && ctrl.tileSize > 1e-4f) return ctrl.tileSize;
            }
            catch { }
            return Placement.FallbackTileSize;
        }

        /// <summary>Stable per-instance seed so identical fields on two floors differ.</summary>
        private static int StableSeed(string scope)
        {
            unchecked
            {
                uint h = 2166136261u;
                foreach (char c in scope ?? "")
                {
                    h ^= c;
                    h *= 16777619u;
                }
                return (int)(h & 0x7FFFFFFF);
            }
        }

        private static float ParseFloat(Dictionary<string, object> args, string key, float fallback)
        {
            if (args != null && args.TryGetValue(key, out var v) && v != null)
            {
                try { return Convert.ToSingle(v, CultureInfo.InvariantCulture); } catch { }
            }
            return fallback;
        }

        private static int ParseInt(Dictionary<string, object> args, string key, int fallback)
        {
            if (args != null && args.TryGetValue(key, out var v) && v != null)
            {
                try { return Convert.ToInt32(v, CultureInfo.InvariantCulture); } catch { }
            }
            return fallback;
        }

        private static bool ParseBool(Dictionary<string, object> args, string key, bool fallback)
        {
            if (args != null && args.TryGetValue(key, out var v) && v != null)
            {
                if (v is bool b) return b;
                try { return Convert.ToBoolean(v); } catch { }
            }
            return fallback;
        }

        private static string ParseString(Dictionary<string, object> args, string key, string fallback)
        {
            if (args != null && args.TryGetValue(key, out var v) && v != null) return v.ToString();
            return fallback;
        }

        private static T ParseEnum<T>(Dictionary<string, object> args, string key, T fallback) where T : struct
        {
            if (args == null || !args.TryGetValue(key, out var v) || v == null) return fallback;
            try { return (T)Enum.Parse(typeof(T), v.ToString(), true); }
            catch { return fallback; }
        }

        /// <summary>Array [x,y] or object {x,y} — the script sends any JSON shape we accept.</summary>
        private static Vector2 ParseVector2(Dictionary<string, object> args, string key, Vector2 fallback)
        {
            if (args == null || !args.TryGetValue(key, out var v) || v == null) return fallback;
            try
            {
                if (v is System.Collections.IList list && list.Count >= 2)
                    return new Vector2(Convert.ToSingle(list[0]), Convert.ToSingle(list[1]));
                if (v is IDictionary<string, object> map && map.ContainsKey("x") && map.ContainsKey("y"))
                    return new Vector2(Convert.ToSingle(map["x"]), Convert.ToSingle(map["y"]));
                if (v is Vector2 vec) return vec;
            }
            catch { }
            return fallback;
        }

        private static Color ParseColor(Dictionary<string, object> args, string key, Color fallback)
        {
            if (args == null || !args.TryGetValue(key, out var v) || v == null) return fallback;
            string s = v.ToString();
            if (ColorUtility.TryParseHtmlString(s.StartsWith("#") ? s : "#" + s, out var c)) return c;
            return fallback;
        }
    }
}
