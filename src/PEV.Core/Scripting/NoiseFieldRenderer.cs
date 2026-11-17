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
            internal long GpuSequence;
            internal PEV.Model.EventManifest Provider;
            /// <summary>The key the script passes to fx.field, without the scope prefix.</summary>
            public string ScriptKey;
            public string Tag;
            public int FloorSeq;
            public int Depth;

            /// <summary>
            /// True when the entry exists because a decoration row exists, not because a
            /// script has started.
            ///
            /// A decoration is on the level from the moment it is placed; its field must be
            /// addressable by tag from that moment too. Registering only when the instance
            /// reaches its own tile is how the placement tile quietly became a delayed-appear
            /// condition — a state event at time 0 could not find a field whose row sat on
            /// floor 40. A described entry survives the script ending: the decoration was
            /// never destroyed, so neither is its field.
            /// </summary>
            public bool Described;

            /// <summary>
            /// True once the script has taken the entry over.
            ///
            /// A described entry keeps following its decoration frame by frame until then.
            /// Afterwards the script owns the parameters, and a refresh from the row would
            /// overwrite them with defaults on the very frame the script set them.
            /// </summary>
            public bool ScriptActive;
            internal Dictionary<string, object> DescriptionArgs;

            /// <summary>
            /// Last native state seen for a not-yet-active described entry, so a frame where
            /// nothing moved costs a comparison instead of a recomposite.
            /// </summary>
            private Vector3 _lastCentre;
            private Vector2 _lastSize;
            private float _lastRotation;
            private Color _lastColor;
            private float _lastOpacity;
            private bool _lastVisible;
            private int _lastDepth;
            private int _lastSortingLayer;
            private int _lastSortingOrder;
            private int _lastUnityLayer;
            private bool _lastRenderable;
            private string _lastTag;
            private bool _hasSnapshot;

            internal void TakeSnapshot(DecorationBridge.Live live)
            {
                _lastCentre = live.Frame.Center;
                _lastSize = live.Frame.Size;
                _lastRotation = live.Frame.Rotation;
                _lastColor = live.Color;
                _lastOpacity = live.Opacity;
                _lastVisible = live.Visible;
                _lastDepth = live.Depth;
                _lastSortingLayer = live.SortingLayerID;
                _lastSortingOrder = live.SortingOrder;
                _lastUnityLayer = live.UnityLayer;
                _lastRenderable = live.Renderable;
                _lastTag = live.Tag;
                _hasSnapshot = true;
            }

            /// <summary>True when the decoration moved or changed since the previous frame.</summary>
            internal bool NativeChanged(DecorationBridge.Live live)
            {
                if (!_hasSnapshot) return true;
                return _lastCentre != live.Frame.Center
                       || _lastSize != live.Frame.Size
                       || !Mathf.Approximately(_lastRotation, live.Frame.Rotation)
                       || _lastColor != live.Color
                       || !Mathf.Approximately(_lastOpacity, live.Opacity)
                       || _lastVisible != live.Visible
                       || _lastDepth != live.Depth
                       || _lastSortingLayer != live.SortingLayerID
                       || _lastSortingOrder != live.SortingOrder
                       || _lastUnityLayer != live.UnityLayer
                       || _lastRenderable != live.Renderable
                       || !string.Equals(_lastTag, live.Tag, StringComparison.Ordinal);
            }

            public float Opacity;
            public bool Visible;
            internal bool GpuInverted;

            /// <summary>
            /// The sorting the carrier renders at, read from the decoration itself.
            ///
            /// depth is still what identifies a field in our bookkeeping, but it is not what
            /// decides draw order — the engine already turned depth into a sorting layer and
            /// an order on the renderer, and two fields can differ there at the same depth
            /// (syncFloorDepth). Compositing by depth alone would draw those in front of each
            /// other; compositing by these values reproduces whatever the engine would do.
            /// </summary>
            public int SortingLayerID;
            public int SortingOrder;
            public int UnityLayer;

            /// <summary>
            /// Whether the decoration has any area. A zero scale is the engine's own way of
            /// not drawing something, so it must not fall back to a default size here.
            /// </summary>
            public bool Renderable = true;

            public int FrameIndex;
            public Vector2 Uv;

            // Geometry as reported by the script, kept per field so the layer can be
            // recomposited when any member changes.
            public Placement.Frame World;
            public NoiseFieldShape Shape;
            public float Feather;
            // The reference effect composes four layers from one mask: a translucent fill,
            // an opaque edge traced around it, outer glow ridges, and sparse spark grains.
            // Keeping them separate is what makes the region read as a cut-out rather than
            // a smear.
            public Color FillColor;
            public Color EdgeColor;
            public Color GlowColor;
            public Color SparkColor;
            public float FillOpacity = 1f;
            public float Exposure;

            /// <summary>
            /// Frames per second the flipbook is played at, matching what the script does with
            /// its frame index. The baker needs it: each baked frame must represent 1/fps of
            /// shader time, or playing the set at fps runs the field at fps times its true
            /// speed. That mismatch was ~90x, which is exactly why the field read as random
            /// regeneration instead of flow.
            /// </summary>
            public float EvolveSpeed = 1f;
            public float EdgeOpacity = 1f;
            public float GlowOpacity;
            public float SparkOpacity;
            public float GlowPasses;
            public float Displace;
            public float Glitch;
            public float Brightness;
            public float Saturation;
            public float SampleRate;
            public float LineOpacity;

            // Generic fallback values; event packages supply their own visual profile.
            public float EdgeDistance = DefaultEdgeDistance;
            public float EdgeDetail = DefaultEdgeDetail;
            public int EdgeThickness = DefaultEdgeThickness;
            public float EdgeMargin = DefaultEdgeMargin;
            public float EdgeFragment = DefaultEdgeFragment;

            /// <summary>
            /// Seconds of pre-state before the border arrives. The reference flashes a flat,
            /// borderless copy of the region first; at 0 the field simply appears.
            /// </summary>
            public float PreDuration;

            /// <summary>
            /// True while the field is still in its pre-state: the flat, borderless copy the
            /// reference shows before the border arrives. Driven by the script so it stays
            /// in sync with the song clock rather than a wall clock.
            /// </summary>
            public bool Ready;

            public int FrameCount;
            public float PxWidthHint;
            public Vector2 Tiling = Vector2.one;

            // Tag-driven overrides, written by Move/State events and applied at composite
            // time. Kept separate from the base values so the owning field's own update()
            // and a mover can both write without one clobbering the other.
            public Vector2 OverrideOffset;
            public float OverrideRotation;
            public float OverrideScale = 1f;
            public float OverrideAlpha = 1f;
            public int OverrideVisibility; // 0 = leave, 1 = force visible, 2 = force hidden

            /// <summary>Set by the layer when it composites; diagnostics point at the quad.</summary>
            public GameObject GameObject;
        }

        private sealed class Layer
        {
            public GameObject GameObject;
            public SpriteRenderer Renderer;
            public Material Material;
            public NoiseTextureFactory.Frames Frames;
            public NoiseTextureFactory.Frames PendingFrames;
            public bool ErrorReported;
            public string StructureKey;
            /// <summary>
            /// The pixel-width bucket this layer was baked at.
            ///
            /// Kept separately from the request so a new bucket can be refused unless it is
            /// clearly different. The camera pulses on every hit, which moves the requested
            /// width by a few percent — enough to cross a bucket boundary and rebake the whole
            /// layer on the beat. Parking on the current bucket rather than chasing the
            /// boundary is what stops that.
            /// </summary>
            public int PixelBucket;
            public int FrameCount = 2;
            public Vector2 Uv;
            public int FrameIndex;
            public float Alpha = 1f;
        }

        private static readonly Dictionary<string, Entry> Live =
            new Dictionary<string, Entry>(StringComparer.Ordinal);
        private static long _nextGpuSequence;

        /// <summary>
        /// Neutral defaults for fields that omit edge controls.
        /// </summary>
        public const float DefaultEdgeDistance = 0f;
        public const float DefaultEdgeDetail = 1f;
        public const int DefaultEdgeThickness = 1;
        public const float DefaultEdgeMargin = 0f;
        public const float DefaultEdgeFragment = 0f;

        /// <summary>
        /// One quad per overlapping cluster of same-depth fields, keyed "depth#cluster".
        /// Clustering is by bounding-box overlap: unioning two distant fields into one
        /// texture stretches the grains across the gap between them, which looks far worse
        /// than rendering them separately.
        /// </summary>
        private static readonly Dictionary<string, Layer> Layers =
            new Dictionary<string, Layer>();

#if PEV_DIAGNOSTICS
        public static int LiveCount => Live.Count;
        public static int LayerCount => Layers.Count;
        private static bool _gpuRendering;
#endif
        private static readonly HashSet<PEV.Model.EventManifest> GpuProviders =
            new HashSet<PEV.Model.EventManifest>();
#if PEV_DIAGNOSTICS
        internal static bool IsGpuProviderActive(PEV.Model.EventManifest provider) => GpuProviders.Contains(provider);
#endif

        // GPU ownership is per package. A failed or unready provider must not suspend
        // the CPU fallback of another package using the same field primitive.
        internal static void SetGpuProviders(HashSet<PEV.Model.EventManifest> providers)
        {
            if (GpuProviders.SetEquals(providers)) return;
            GpuProviders.Clear();
            GpuProviders.UnionWith(providers);
            var depths = new HashSet<int>();
            foreach (var entry in Live.Values) depths.Add(entry.Depth);
            foreach (int depth in depths) RebuildDepth(depth);
        }

#if PEV_DIAGNOSTICS
        // Diagnostic-only override for exercising CPU/GPU transitions.
        // On failure, rebuild the legacy path from the retained entries.
        internal static void SetGpuRendering(bool enabled)
        {
            if (_gpuRendering == enabled) return;
            _gpuRendering = enabled;
            if (enabled)
            {
                ReleaseLegacyLayers();
                foreach (var entry in Live.Values) entry.GameObject = null;
            }
            else
            {
                var depths = new HashSet<int>();
                foreach (var entry in Live.Values) depths.Add(entry.Depth);
                foreach (int depth in depths) RebuildDepth(depth);
            }
        }
        public static int TextureSets => NoiseTextureFactory.CachedFrameSets;
        public static string LayerReport()
        {
            var sb = new StringBuilder();
            foreach (var kv in Layers)
            {
                var t = kv.Value.GameObject != null ? kv.Value.GameObject.transform : null;
                string dims = "gone";
                if (kv.Value.Renderer != null && kv.Value.Renderer.sprite != null)
                {
                    var b = kv.Value.Renderer.bounds.size;
                    dims = $"{b.x:F1}x{b.y:F1}";
                }
                sb.Append(kv.Key).Append('=').Append(dims).Append("; ");
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
#endif

        // The GPU plan orders entries by their sequence; avoid allocating and sorting a
        // diagnostics snapshot every frame on its high-refresh rendering path.
        internal static void CopyEntriesTo(List<Entry> destination)
        {
            destination.Clear();
            foreach (var entry in Live.Values) destination.Add(entry);
        }

#if PEV_DIAGNOSTICS
        /// <summary>
        /// Writes every composited layer's current frame to a PNG.
        ///
        /// Looking at a screenshot cannot separate our texture from the track drawn over it,
        /// the camera's exposure, or the alpha the layer ends up with. This dumps the texture
        /// that leaves the generator, so a shape or colour problem can be attributed rather
        /// than guessed at. Diagnostics only: never called from the render path.
        /// </summary>
        public static int DumpTextures(string directory)
        {
            int written = 0;
            try
            {
                if (!System.IO.Directory.Exists(directory))
                    System.IO.Directory.CreateDirectory(directory);
            }
            catch { return 0; }

            foreach (var kv in Layers)
            {
                var layer = kv.Value;
                if (layer == null || layer.Frames == null || layer.Frames.Textures == null) continue;

                // ':' is legal in the cluster key but not in a Windows filename, and the group
                // key carries the sorting layer id and order as "id:order".
                string safe = kv.Key.Replace('/', '_').Replace('#', '-').Replace(':', '_');

                // Every frame of the set, not just the one on screen. Whether the motion
                // flows is a question about consecutive frames, and a single still cannot
                // answer it — the answer is the difference between frame k and frame k+1.
                var textures = layer.Frames.Textures;
                for (int i = 0; i < textures.Length; i++)
                {
                    var tex = textures[i];
                    if (tex == null) continue;
                    try
                    {
                        // Encode with the bundled writer: UnityEngine.ImageConversionModule is
                        // built against netstandard 2.1 and this project targets 2.0, so
                        // EncodeToPNG is not available to reference at all.
                        byte[] png = NoiseTextures.EncodePng(tex.GetPixels32(), tex.width, tex.height);
                        if (png == null) continue;
                        System.IO.File.WriteAllBytes(
                            System.IO.Path.Combine(directory,
                                "layer-" + safe + "_f" + i.ToString("D2") + ".png"), png);
                        written++;
                    }
                    catch { }
                }
            }
            return written;
        }
#endif

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
                entry = new Entry { Key = id, Scope = scope, ScriptKey = key, GpuSequence = _nextGpuSequence++ };
                Live[id] = entry;
            }

            int oldDepth = entry.Depth;
            try
            {
                Apply(entry, args);
                // The script is now authoritative for this entry's parameters. Platform
                // clears only on a successful apply, so a script that threw every frame keeps
                // the frozen description instead of re-enabling the native refresh.
                entry.ScriptActive = true;
            }
            catch (Exception e)
            {
                error = "fx.field apply failed: " + e.Message;
                return false;
            }

            // A depth change moves the field between layers; both sides need recompositing.
            if (oldDepth != entry.Depth)
                RebuildDepth(oldDepth);

            return RebuildDepth(entry.Depth);
        }

        /// <summary>
        /// Registers a decoration-backed field from its row, at level init.
        ///
        /// Nothing here needs the script: geometry, visibility, opacity, tag, depth and the
        /// native sorting all live on the decoration, and the row's own fields carry them
        /// before its scrDecoration exists. Until the script takes over, the field uses the
        /// row's stored parameters instead of replacing authored values with defaults.
        /// </summary>
        public static void Describe(string scope, string key, ADOFAI.LevelEvent ev,
            PEV.Storage.EventInstanceData data = null)
        {
            if (string.IsNullOrEmpty(scope) || ev == null) return;

            string id = Scoped(scope, key);
            if (!Live.TryGetValue(id, out var entry))
            {
                entry = new Entry { Key = id, Scope = scope, ScriptKey = key, GpuSequence = _nextGpuSequence++ };
                Live[id] = entry;
            }

            entry.Described = true;
            entry.DescriptionArgs = DescriptionParameters(ev, data);
            Bind(scope, ev);

            int oldDepth = entry.Depth;
            try { Apply(entry, entry.DescriptionArgs); }
            catch (Exception e) { PEV.Core.Plugin.Log("field describe failed: " + e.Message); return; }

            if (oldDepth != entry.Depth) RebuildDepth(oldDepth);
            RebuildDepth(entry.Depth);
        }

        /// <summary>Empty call arguments: every parameter takes its declared default.</summary>
        private static readonly Dictionary<string, object> EmptyArgs =
            new Dictionary<string, object>(StringComparer.Ordinal);

        private static Dictionary<string, object> DescriptionParameters(ADOFAI.LevelEvent ev,
            PEV.Storage.EventInstanceData data)
        {
            var args = new Dictionary<string, object>(StringComparer.Ordinal) { ["__floor"] = ev.floor };
            if (data == null) return args;
            if (PEV.Registry.EventRegistry.TryGetById(data.Id, out var manifest)) args["__manifest"] = manifest;
            foreach (var kv in data.Effective)
            {
                object value = kv.Value;
                try { value = ev.Get<object>(kv.Key) ?? value; } catch { }
                args[kv.Key] = value;
            }
            ApplyFieldBindings(manifest, args);
            return args;
        }

        internal static void ApplyFieldBindings(PEV.Model.EventManifest manifest, Dictionary<string, object> args)
        {
            if (manifest == null || args == null) return;
            foreach (var binding in manifest.FieldParameterBindings)
                if (args.TryGetValue(binding.Key, out var value)) args[binding.Value] = value;
        }

        /// <summary>Manifests whose instances render through the field primitive.</summary>
        public static bool IsFieldEvent(PEV.Model.EventManifest m)
        {
            return m != null && m.HostPrimitive == "fx.field" && !string.IsNullOrEmpty(m.FieldPreviewKey);
        }

        /// <summary>
        /// Drops what the script owned, keeps what the decoration owns.
        ///
        /// A described entry is the rendering of a row that still exists, so a script ending
        /// — a rewind, a checkpoint jump, the end of playback — must not take the field with
        /// it. The parameters go back to their stored values and the geometry stays on the
        /// decoration, which is the same state the field was in before its first Set.
        /// </summary>
        private static void ResetToDescription(Entry entry)
        {
            entry.FrameIndex = 0;
            entry.Uv = Vector2.zero;
            entry.ScriptActive = false;
            // Old charts predate the invert parameter. Do not retain a script-owned
            // inversion when resetting an old row that has no stored value for it.
            entry.GpuInverted = false;
            try { Apply(entry, entry.DescriptionArgs ?? EmptyArgs); }
            catch (Exception e) { PEV.Core.Plugin.Log("field reset failed: " + e.Message); }
        }

        /// <summary>
        /// Follows the decoration for every described entry no script has taken over yet.
        ///
        /// A field registered at level init has no script until its tile is reached; without
        /// this it would sit at the geometry read on the init frame while the author parses
        /// the row, drags it in the editor, or a MoveDecorations tween plays on it. The check
        /// is a field comparison, so a still decoration costs nothing and only a real change
        /// recomposites.
        /// </summary>
        public static void Refresh()
        {
            if (Live.Count == 0) return;
            var dirty = new HashSet<int>();
            foreach (var kv in Live)
            {
                var entry = kv.Value;
                if (!entry.Described || entry.ScriptActive) continue;
                if (!TryReadDecoration(entry, null, out var live)) continue;
                if (!entry.NativeChanged(live)) continue;

                entry.TakeSnapshot(live);
                int oldDepth = entry.Depth;
                try { Apply(entry, entry.DescriptionArgs ?? EmptyArgs); }
                catch (Exception e) { PEV.Core.Plugin.Log("field refresh failed: " + e.Message); continue; }
                dirty.Add(oldDepth);
                dirty.Add(entry.Depth);
            }
            foreach (var depth in dirty) RebuildDepth(depth);
        }

        public static bool Destroy(string key, Dictionary<string, object> args, out string error)
        {
            error = null;
            string id = Scoped(ScopeOf(args), key);
            if (!Live.TryGetValue(id, out var entry)) return true;

            if (entry.Described)
            {
                ResetToDescription(entry);
                RebuildDepth(entry.Depth);
                return true;
            }

            Live.Remove(id);
            RebuildDepth(entry.Depth);
            return true;
        }

        public static void DestroyScope(string scope)
        {
            var keys = new List<string>();
            foreach (var kv in Live)
                if (kv.Value.Scope == scope) keys.Add(kv.Key);
            var depths = new HashSet<int>();
            foreach (var key in keys)
            {
                var entry = Live[key];
                if (entry.Described)
                {
                    ResetToDescription(entry);
                    depths.Add(entry.Depth);
                    continue;
                }
                depths.Add(entry.Depth);
                Live.Remove(key);
            }
            foreach (var depth in depths) RebuildDepth(depth);
        }

        /// <summary>
        /// Removes every entry that exists only because a row exists.
        ///
        /// Used when the editor preview takes over: while editing, the preview drives each
        /// field from the row's live values, and a described copy of the same row would draw
        /// a second field with defaults over the same rectangle. Playback does the opposite —
        /// it drops the preview and keeps the described entries, because those are what the
        /// runtime instances then take over.
        /// </summary>
        public static int DropDescribed()
        {
            var keys = new List<string>();
            foreach (var kv in Live)
                if (kv.Value.Described) keys.Add(kv.Key);
            if (keys.Count == 0) return 0;

            var depths = new HashSet<int>();
            foreach (var key in keys)
            {
                depths.Add(Live[key].Depth);
                Live.Remove(key);
            }
            foreach (var depth in depths) RebuildDepth(depth);
            return keys.Count;
        }

        /// <summary>
        /// True while the field should be registered from decoration rows.
        ///
        /// In the editor's idle state the preview owns the canvas and reads the row's current
        /// values every frame; describing again there would duplicate it. Playback, and the
        /// game outside the editor, must register from the level data itself.
        /// </summary>
        public static bool ShouldDescribeFromRows()
        {
            try
            {
                var editor = ADOBase.editor;
                return editor == null || editor.playMode;
            }
            catch { return true; }
        }

        public static void DestroyAll()
        {
#if PEV_DIAGNOSTICS
            _gpuRendering = false;
#endif
            GpuProviders.Clear();
            ReleaseLegacyLayers();
            Live.Clear();
            NoiseTextureFactory.Clear();
        }

        private static void ReleaseLegacyLayers()
        {
            foreach (var kv in Layers)
            {
                try
                {
                    if (kv.Value.GameObject != null) UnityEngine.Object.Destroy(kv.Value.GameObject);
                    if (kv.Value.Material != null) UnityEngine.Object.Destroy(kv.Value.Material);
                    NoiseTextureFactory.Release(kv.Value.Frames);
                    NoiseTextureFactory.Release(kv.Value.PendingFrames);
                }
                catch { }
            }
            Layers.Clear();
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

        /// <summary>
        /// Writes tag-driven overrides and recomposites the affected depths. Values are
        /// absolute, not incremental: a mover recomputes them from the song clock every
        /// frame, which is what keeps pause and checkpoint jumps correct.
        ///
        /// visibility: 0 leaves the field's own value, 1 forces visible, 2 forces hidden.
        /// </summary>
        public static int ApplyOverrides(string tag, Vector2 offset, float rotation,
            float scale, float alpha, int visibility, bool clear)
        {
            var targets = ByTag(tag);
            if (targets.Count == 0) return 0;

            var depths = new HashSet<int>();
            foreach (var e in targets)
            {
                if (clear)
                {
                    e.OverrideOffset = Vector2.zero;
                    e.OverrideRotation = 0f;
                    e.OverrideScale = 1f;
                    e.OverrideAlpha = 1f;
                    e.OverrideVisibility = 0;
                }
                else
                {
                    e.OverrideOffset = offset;
                    e.OverrideRotation = rotation;
                    e.OverrideScale = scale > 0f ? scale : 1f;
                    e.OverrideAlpha = Mathf.Clamp01(alpha);
                    e.OverrideVisibility = visibility;
                }
                depths.Add(e.Depth);
            }

            foreach (var d in depths) RebuildDepth(d);
            return targets.Count;
        }

        /// <summary>
        /// Instance scope -> the decoration row that owns it.
        ///
        /// A decoration-backed event does not compute its own geometry: the engine does.
        /// scrDecoration holds pivotPosVec, scaleVec, rotAngle, color and opacity, and every
        /// tween the game runs — Transform gizmo drags, MoveDecorations, editor scrubs —
        /// writes those same fields. Reading them here is what makes the effect follow a
        /// decoration instead of merely imitating one.
        /// </summary>
        private static readonly Dictionary<string, ADOFAI.LevelEvent> Bound =
            new Dictionary<string, ADOFAI.LevelEvent>(StringComparer.Ordinal);

        /// <summary>Ties an instance scope to the decoration row it should mirror.</summary>
        public static void Bind(string scope, ADOFAI.LevelEvent ev)
        {
            if (string.IsNullOrEmpty(scope)) return;
            if (ev == null) Bound.Remove(scope);
            else Bound[scope] = ev;
        }

        public static void Unbind(string scope)
        {
            if (!string.IsNullOrEmpty(scope)) Bound.Remove(scope);
        }

        /// <summary>
        /// Drops every binding. Called from teardown, where the LevelEvent objects are about
        /// to be replaced by the next decode — a stale reference here would keep a destroyed
        /// row alive and, worse, render it.
        /// </summary>
        public static void UnbindAll() => Bound.Clear();

        private static void Apply(Entry entry, Dictionary<string, object> args)
        {
            if (args != null && args.TryGetValue("__manifest", out var provider) && provider is PEV.Model.EventManifest manifest)
                entry.Provider = manifest;
            var cam = GameCamera.Current;
            bool mirrored = TryReadDecoration(entry, args, out var live);
            entry.World = mirrored ? live.Frame : Placement.Compute(BuildInput(args, entry, cam));

            entry.Shape = ParseEnum(args, "shape", NoiseFieldShape.Rect);
            entry.GpuInverted = ParseBool(args, "invert", entry.GpuInverted);
            entry.Feather = Mathf.Max(0f, ParseFloat(args, "feather", 0f));
            // The decoration's own colour wins when there is one: an author who recolours the
            // decoration expects the field to follow, and MoveDecorations tweens colour on the
            // component rather than in the event data.
            var baseColor = mirrored
                ? live.Color
                : ParseColor(args, "color", Color.white);
            entry.FillColor = baseColor;
            entry.EdgeColor = ParseColor(args, "edgeColor", baseColor);
            entry.GlowColor = ParseColor(args, "glowColor", baseColor);
            entry.SparkColor = ParseColor(args, "sparkColor", baseColor);
            entry.FillOpacity = Mathf.Clamp01(ParseFloat(args, "fillOpacity", 1f));
            entry.Exposure = Mathf.Clamp(ParseFloat(args, "exposure", 0f), 0f, 2f);
            entry.EvolveSpeed = Mathf.Clamp(ParseFloat(args, "evolveSpeed", 1f), 1f, 60f);
            entry.EdgeOpacity = Mathf.Clamp01(ParseFloat(args, "edgeOpacity", 1f));
            entry.GlowOpacity = Mathf.Clamp01(ParseFloat(args, "glowOpacity", 0f));
            entry.SparkOpacity = Mathf.Clamp01(ParseFloat(args, "sparkOpacity", 0f));
            entry.GlowPasses = Mathf.Clamp(ParseFloat(args, "glowPasses", 0f), 0f, 6f);
            entry.PreDuration = Mathf.Max(0f, ParseFloat(args, "preDuration", 0f));
            entry.Ready = ParseBool(args, "pre", false);
            entry.Displace = Mathf.Clamp01(ParseFloat(args, "displace", 0f));
            entry.Glitch = Mathf.Clamp01(ParseFloat(args, "glitch", 0f));
            entry.Brightness = Mathf.Max(0f, ParseFloat(args, "brightness", 1f));
            entry.Saturation = Mathf.Max(0f, ParseFloat(args, "saturation", 1f));
            entry.SampleRate = Mathf.Max(0.1f, ParseFloat(args, "sampleRate", 1f));
            entry.LineOpacity = Mathf.Clamp01(ParseFloat(args, "lineOpacity", 0f));

            entry.EdgeDistance = Mathf.Clamp(ParseFloat(args, "edgeDistance", DefaultEdgeDistance), 0f, 2f);
            entry.EdgeDetail = Mathf.Clamp(ParseFloat(args, "edgeDetail", DefaultEdgeDetail), 1f, 32f);
            entry.EdgeThickness = Mathf.Clamp(ParseInt(args, "edgeThickness", DefaultEdgeThickness), 1, 6);
            entry.EdgeMargin = Mathf.Clamp(ParseFloat(args, "edgeMargin", DefaultEdgeMargin), 0f, 2f);
            entry.EdgeFragment = Mathf.Clamp01(ParseFloat(args, "edgeFragment", DefaultEdgeFragment));

            entry.FrameCount = Mathf.Clamp(ParseInt(args, "frameCount", 2), 2, 128);

            // When mirrored, visibility, opacity, tag and depth are the decoration's. That is
            // the whole point of the mapping: an author hides the decoration or retags it and
            // the field follows, without an extra parameter to keep in sync.
            entry.Visible = mirrored ? live.Visible : ParseBool(args, "visible", true);
            entry.Opacity = mirrored ? live.Opacity : Mathf.Clamp01(ParseFloat(args, "opacity", 1f));
            entry.Tag = mirrored ? live.Tag : ParseString(args, "tag", "");
            entry.FloorSeq = ParseInt(args, "__floor", entry.FloorSeq);
            entry.Depth = mirrored ? live.Depth : ParseInt(args, "depth", 0);

            if (mirrored)
            {
                // Whatever the engine wrote on the carrier. This is what makes the field obey
                // the native depth instead of a number we invented for it.
                entry.SortingLayerID = live.SortingLayerID;
                entry.SortingOrder = live.SortingOrder;
                entry.UnityLayer = live.UnityLayer;
                entry.Renderable = live.Renderable;
            }
            else
            {
                DecorationBridge.NativeSorting(entry.Depth,
                    out entry.SortingLayerID, out entry.SortingOrder, out entry.UnityLayer);
                entry.Renderable = entry.World.Size.x > 1e-4f && entry.World.Size.y > 1e-4f;
            }

            entry.FrameIndex = ParseInt(args, "frame", 0);
            entry.Uv = ParseVector2(args, "uv", Vector2.zero);
            entry.Tiling = ParseVector2(args, "tiling", Vector2.one);
            if (entry.Tiling.x <= 0f) entry.Tiling.x = 1f;
            if (entry.Tiling.y <= 0f) entry.Tiling.y = 1f;

            float halfH = cam != null && cam.orthographic ? cam.orthographicSize : 5f;
            entry.PxWidthHint = entry.World.Size.x / (2f * Mathf.Max(1e-4f, halfH)) * Screen.height;
        }

        /// <summary>
        /// The decoration's current frame, if this instance is bound to one that still exists.
        ///
        /// ScrDecoration is recreated whenever the decoration list is rebuilt — undo, play,
        /// level reload — so the component is looked up every frame instead of cached. The
        /// lookup is a dictionary hit inside the game's own manager, which is cheaper than
        /// the texture work it feeds.
        /// </summary>
        private static bool TryReadDecoration(Entry entry, Dictionary<string, object> args,
            out DecorationBridge.Live live)
        {
            live = default(DecorationBridge.Live);
            var ev = ResolveBinding(entry, args);

            // With no row there is nothing to mirror, and the script's own geometry (if the
            // manifest still declares any) is the only source. That is the harness path.
            if (ev == null) return false;

            // The component comes and goes: the manager builds it after decode and rebuilds
            // it on undo, play and reload. Between those moments the row's own fields hold
            // the same numbers, so the field keeps its place instead of blinking out.
            if (!DecorationBridge.TryRead(ev, out live) &&
                !DecorationBridge.TryReadEvent(ev, out live))
                return false;

            // A collapsed decoration stays in the table and is simply not drawn: the engine
            // does not render a zero-sized sprite, and substituting the script's default
            // geometry here is what turned "the author scaled it to nothing" into "the field
            // is visible anyway". Renderable carries the answer; the member filter reads it.
            return true;
        }

        private static ADOFAI.LevelEvent ResolveBinding(Entry entry, Dictionary<string, object> args)
        {
            if (Bound.TryGetValue(entry.Scope, out var ev) && ev != null) return ev;

            // The script may pass the row's identity itself, which is what the preview path
            // does: it has no EventInstance, only the event object.
            if (args == null) return null;
            if (!args.TryGetValue("__event", out var raw)) return null;
            return raw as ADOFAI.LevelEvent;
        }

        /// <summary>
        /// Rebuilds the quads for one depth. Fields at the same depth that overlap are
        /// composited into a single quad so their masks take a union instead of summing
        /// alpha; fields far apart stay separate, because unioning them would stretch the
        /// grain across the empty space between them.
        /// </summary>
        private static bool RebuildDepth(int depth)
        {
#if PEV_DIAGNOSTICS
            if (_gpuRendering) return true;
#endif
            var members = new List<Entry>();
            foreach (var kv in Live)
            {
                var entry = kv.Value;
                if (entry.Depth != depth) continue;
                if (entry.Provider != null && GpuProviders.Contains(entry.Provider))
                {
                    entry.GameObject = null;
                    continue;
                }
                bool visible = entry.OverrideVisibility == 0 ? entry.Visible : entry.OverrideVisibility == 1;
                if (visible && entry.Renderable && entry.Opacity * entry.OverrideAlpha > 0f) members.Add(entry);
                else entry.GameObject = null;
            }
            members.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));

            // Same depth, different sorting. The engine turns depth into a sorting layer and
            // an order, and syncFloorDepth can put two rows of the same depth on the floor's
            // own key instead — one quad can only draw at one order, so those cannot share a
            // layer. Grouping first and clustering inside each group keeps the union where it
            // belongs without pretending the two draw at the same place in the stack.
            var groups = new Dictionary<string, List<Entry>>(StringComparer.Ordinal);
            var order = new List<string>();
            foreach (var m in members)
            {
                // Camera culling also depends on the GameObject layer. Two carriers
                // with the same sorting position but different layers cannot safely
                // share a renderer, even if their rectangles overlap.
                string groupKey = m.SortingLayerID + ":" + m.SortingOrder + ":" + m.UnityLayer + ":" +
                    PackageIdentity.Of(m.Provider);
                if (!groups.TryGetValue(groupKey, out var list))
                {
                    list = new List<Entry>();
                    groups[groupKey] = list;
                    order.Add(groupKey);
                }
                list.Add(m);
            }

            var wanted = new HashSet<string>(StringComparer.Ordinal);
            bool ok = true;
            foreach (var groupKey in order)
            {
                var list = groups[groupKey];
                // Greedy clustering by bounding-box overlap. Fields that do not touch can never
                // share a texel, so there is nothing to merge and everything to lose by merging.
                var clusters = new List<List<Entry>>();
                var boxes = new List<Rect>();
                foreach (var m in list)
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
                        boxes[target] = Rect.MinMaxRect(
                            Mathf.Min(boxes[target].xMin, box.xMin), Mathf.Min(boxes[target].yMin, box.yMin),
                            Mathf.Max(boxes[target].xMax, box.xMax), Mathf.Max(boxes[target].yMax, box.yMax));
                        // A bridging field may join several existing clusters. Merge until
                        // the expanded box no longer touches another cluster.
                        for (int i = 0; i < boxes.Count; i++)
                        {
                            if (i == target || !boxes[target].Overlaps(boxes[i])) continue;
                            clusters[target].AddRange(clusters[i]);
                            boxes[target] = Rect.MinMaxRect(
                                Mathf.Min(boxes[target].xMin, boxes[i].xMin), Mathf.Min(boxes[target].yMin, boxes[i].yMin),
                                Mathf.Max(boxes[target].xMax, boxes[i].xMax), Mathf.Max(boxes[target].yMax, boxes[i].yMax));
                            clusters.RemoveAt(i);
                            boxes.RemoveAt(i);
                            if (i < target) target--;
                            i = -1;
                        }
                    }
                }

                for (int i = 0; i < clusters.Count; i++)
                {
                    clusters[i].Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
                    // The sorting is part of the key, not just of the render: a row moved to a
                    // different order needs a different quad, because a SpriteRenderer cannot
                    // change its order without also changing where it sits among its peers.
                    string key = depth + "#" + groupKey + "#" + clusters[i][0].Key;
                    wanted.Add(key);
                    if (!CompositeCluster(key, depth, clusters[i])) ok = false;
                }
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
                if (layer.Material != null) UnityEngine.Object.Destroy(layer.Material);
                NoiseTextureFactory.Release(layer.Frames);
                NoiseTextureFactory.Release(layer.PendingFrames);
                Layers.Remove(key);
            }
            return ok;
        }

        private static bool CompositeCluster(string key, int depth, List<Entry> members)
        {
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
                // Overrides are applied here, not in Apply(): a mover writes Entry state
                // without knowing geometry, and the composite is the single place where
                // base parameters and overridden ones meet.
                Vector2 centreWorld = new Vector2(m.World.Center.x, m.World.Center.y)
                                      + m.OverrideOffset;
                float sizeFactor = m.OverrideScale > 0f ? m.OverrideScale : 1f;

                pieces[i] = new NoiseTextureFactory.Piece
                {
                    Offset = new Vector2(centreWorld.x - centre.x, centreWorld.y - centre.y),
                    Half = new Vector2(m.World.Size.x * 0.5f * sizeFactor, m.World.Size.y * 0.5f * sizeFactor),
                    Rotation = m.World.Rotation + m.OverrideRotation,
                    Feather = m.Feather,
                    Circle = m.Shape == NoiseFieldShape.Circle,
                    // Coastline band from this field's own size, so a small field inside a
                    // cluster with a big one keeps its own shape instead of being eroded by
                    // the neighbour's band.
                    Band = m.Displace * m.EdgeDistance * Mathf.Min(
                        m.World.Size.x * sizeFactor, m.World.Size.y * sizeFactor),
                    FillColor = m.FillColor,
                    EdgeColor = m.EdgeColor,
                    FillOpacity = m.FillOpacity * Mathf.Clamp01(m.OverrideAlpha),
                    EdgeOpacity = m.EdgeOpacity * Mathf.Clamp01(m.OverrideAlpha)
                };
            }

            // The quad is grown beyond the geometry so detached flakes have somewhere to
            // drift; the coastline itself is unchanged. Margin scales with the field's own
            // short side, and follows the same displace parameter as the coastline band —
            // with no displacement there is no coast, so no need for the extra area.
            float margin = lead.Displace * lead.EdgeMargin * Mathf.Min(size.x, size.y);

            // The layer object is needed before the request: its baked resolution is part of the
            // hysteresis that keeps camera motion from triggering a rebake.
            if (!Layers.TryGetValue(key, out var layer) || layer.GameObject == null)
            {
                var go = EffectCanvas.CreateObject("PEV.Field." + key);
                var sr = go.AddComponent<SpriteRenderer>();
                layer = new Layer { GameObject = go, Renderer = sr };
                Layers[key] = layer;
            }

            // The sorting is the carrier's, every time. It changes on a depth edit and on
            // syncFloorDepth, and the cluster key already carries it, so a changed value
            // arrives here as a different layer; setting it unconditionally costs nothing and
            // means an object reused after a rebuild cannot keep a stale order.
            layer.Renderer.sortingLayerID = lead.SortingLayerID;
            layer.Renderer.sortingOrder = lead.SortingOrder;
            layer.GameObject.layer = lead.UnityLayer;

            var request = new NoiseTextureFactory.Request
            {
                Provider = lead.Provider,
                WidthWorld = size.x,
                HeightWorld = size.y,
                Margin = margin,
                Pieces = pieces,
                FillColor = lead.FillColor,
                EdgeColor = lead.EdgeColor,
                GlowColor = lead.GlowColor,
                SparkColor = lead.SparkColor,
                FillOpacity = lead.FillOpacity,
                Exposure = lead.Exposure,
                EvolveSpeed = lead.EvolveSpeed,
                FillStrength = lead.FillOpacity,
                SceneColor = SceneBackground(),
                EdgeOpacity = lead.EdgeOpacity,
                GlowOpacity = lead.GlowOpacity,
                SparkOpacity = lead.SparkOpacity,
                GlowPasses = lead.GlowPasses,
                Displace = lead.Displace,
                Glitch = lead.Glitch,
                Ready = lead.Ready,
                EdgeDistance = lead.EdgeDistance,
                EdgeDetail = lead.EdgeDetail,
                EdgeThickness = lead.EdgeThickness,
                EdgeMargin = lead.EdgeMargin,
                EdgeFragment = lead.EdgeFragment,
                Brightness = lead.Brightness,
                Saturation = lead.Saturation,
                SampleRate = lead.SampleRate,
                LineOpacity = lead.LineOpacity,
                FrameCount = lead.FrameCount,
                Seed = StableSeed(lead.Scope),
                // The cluster box is wider than the lead field, so scale the hint by area
                // ratio to keep grains at a constant on-screen size.
                TargetPixelWidth = StickyBucket(layer, lead.PxWidthHint * (size.x / Mathf.Max(1e-4f, lead.World.Size.x))),
                // How wide the quad actually is on screen. The composer sizes its grids from
                // this, because the reference's mask and effects targets are fractions of the
                // screen — not of the texture the effect happens to be baked into.
                ScreenPixelWidth = layer.PixelBucket
            };

            // One key, owned by the factory: a second copy in the renderer is how a
            // structure that never changes still manages to regenerate every frame.
            string structureKey = NoiseTextureFactory.Key(request);

            if ((layer.Frames == null && layer.PendingFrames == null) || layer.StructureKey != structureKey)
            {
                if (layer.StructureKey != null)
                    PEV.Core.Plugin.Log("noise rebake: " + Differ(layer.StructureKey, structureKey));

                // Begin, not Get: the pixels are built on a worker and uploaded a few frames
                // at a time by NoiseTextureFactory.Pump, so nothing here waits. The previous
                // set stays on screen until the new one is complete — which is what makes a
                // rebake (live parameter edits, a zoom crossing a bucket) invisible instead
                // of a hitch.
                var frames = NoiseTextureFactory.Begin(request, out string genError);
                if (frames == null)
                {
                    PEV.Core.Plugin.Log("noise layer: " + genError);
                    return false;
                }
                NoiseTextureFactory.Release(layer.PendingFrames);
                layer.PendingFrames = frames;
                layer.ErrorReported = false;
                layer.StructureKey = structureKey;
            }

            if (layer.PendingFrames?.Error != null && !layer.ErrorReported)
            {
                PEV.Core.Plugin.Log("noise layer: " + layer.PendingFrames.Error);
                layer.ErrorReported = true;
            }

            if (layer.PendingFrames != null && layer.PendingFrames.Ready)
            {
                NoiseTextureFactory.Release(layer.Frames);
                layer.Frames = layer.PendingFrames;
                layer.PendingFrames = null;
                layer.FrameCount = layer.Frames.Textures.Length;
            }

            // A set that has not finished uploading has no sprite yet. Keep whatever the
            // layer was already showing rather than blanking it.
            if (layer.Frames == null || !layer.Frames.Ready)
            {
                if (layer.Renderer.sprite == null) layer.GameObject.SetActive(false);
                return false;
            }
            layer.GameObject.SetActive(true);

            int idx = lead.FrameIndex % layer.FrameCount;
            if (idx < 0) idx += layer.FrameCount;

            layer.GameObject.transform.position = new Vector3(centre.x, centre.y, 0f);
            // Divide by the sprite's natural world size: the sprite is 100 PPU, so its
            // quad is already texW/100 units wide. Multiplying by size directly rendered
            // the layer about ten times too large and flooded the screen with grain.
            // The texture now also covers the margin on each side, so the quad spans the
            // geometry box plus that margin — the region itself stays exactly `size`.
            Vector2 natural = layer.Frames.NaturalSize;
            layer.GameObject.transform.localScale = new Vector3(
                (size.x + margin * 2f) / Mathf.Max(1e-4f, natural.x),
                (size.y + margin * 2f) / Mathf.Max(1e-4f, natural.y),
                1f);
            layer.GameObject.transform.rotation = Quaternion.identity;

            layer.Renderer.sprite = layer.Frames.Sprites[idx];
            layer.FrameIndex = idx;
            layer.Uv = lead.Uv;

            var mat = layer.Material;
            if (mat == null) mat = layer.Material = layer.Renderer.material;
            if (mat != null)
            {
                // No offset. The baked set carries its own motion, and the border and coastline
                // are part of the same texture — scrolling it slides the whole edge off the
                // geometry, which is what made the field look like a drifting decal rather
                // than a region of the scene.
                mat.mainTextureOffset = Vector2.zero;
                mat.mainTextureScale = lead.Tiling;
            }

            // One pre-multiplied texture cannot vary alpha per region, so visibility and
            // opacity collapse to the strongest member of the cluster.
            float alpha = 0f;
            foreach (var m in members)
            {
                bool visible = m.OverrideVisibility == 0 ? m.Visible : m.OverrideVisibility == 1;
                if (!visible) continue;
                float a = m.Opacity * Mathf.Clamp01(m.OverrideAlpha);
                if (a > alpha) alpha = a;
            }
            layer.Alpha = alpha;
            layer.Renderer.color = new Color(1f, 1f, 1f, alpha);

            foreach (var m in members) m.GameObject = layer.GameObject;
            return true;
        }

        /// <summary>
        /// Keeps the layer on its current pixel-width bucket unless the new request is clearly
        /// outside it.
        ///
        /// The camera pulses on every hit and zooms during gameplay, which moves the requested
        /// width by a few percent. Bucket boundaries are 96 pixels apart, so a few percent is
        /// enough to cross one — and crossing one rebakes the whole layer, on the beat. The
        /// hysteresis is one full bucket: the layer only moves once the request lands a bucket
        /// away from where it was baked, which a pulse can never do.
        /// </summary>
        private static float StickyBucket(Layer layer, float px)
        {
            int wanted = NoiseTextureFactory.ResolutionBucket(px);
            if (layer.PixelBucket <= 0) { layer.PixelBucket = wanted; return wanted; }
            if (Mathf.Abs(px - layer.PixelBucket) >= NoiseTextureFactory.ResolutionStep)
                layer.PixelBucket = wanted;
            return layer.PixelBucket;
        }

        /// <summary>
        /// Reports the first field of two structure keys that differs.
        ///
        /// A key that drifts every frame is the difference between a smooth scene and a
        /// stutter, and the fingerprint has ~30 fields — guessing which one moved has cost
        /// this project whole iterations. This names it from the two strings themselves.
        /// </summary>
        private static string Differ(string before, string after)
        {
            var a = before.Split('|');
            var b = after.Split('|');
            int n = Mathf.Min(a.Length, b.Length);
            for (int i = 0; i < n; i++)
                if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
                    return $"field #{i}: {a[i]} -> {b[i]}";
            return a.Length == b.Length ? "identical" : $"length {a.Length} -> {b.Length}";
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
            float rad = (m.World.Rotation + m.OverrideRotation) * Mathf.Deg2Rad;
            float cos = Mathf.Cos(rad), sin = Mathf.Sin(rad);
            float factor = m.OverrideScale > 0f ? m.OverrideScale : 1f;
            float hw = m.World.Size.x * 0.5f * factor, hh = m.World.Size.y * 0.5f * factor;
            for (int sx = -1; sx <= 1; sx += 2)
            {
                for (int sy = -1; sy <= 1; sy += 2)
                {
                    float x = sx * hw, y = sy * hh;
                    var p = new Vector2(
                        m.World.Center.x + m.OverrideOffset.x + x * cos - y * sin,
                        m.World.Center.y + m.OverrideOffset.y + x * sin + y * cos);
                    if (p.x < min.x) min.x = p.x;
                    if (p.y < min.y) min.y = p.y;
                    if (p.x > max.x) max.x = p.x;
                    if (p.y > max.y) max.y = p.y;
                }
            }
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

        /// <summary>
        /// The colour the region is drawn over, standing in for the reference's screen grab.
        ///
        /// scrCamera points a dedicated camera at the background colour (scnGame sets
        /// Bgcamstatic.backgroundColor from the level), so this is the same value the game
        /// clears to. Falls back to the level's own field if that camera is not up yet — both
        /// exist for the same purpose, and one of them is always available once a level is
        /// loaded.
        /// </summary>
        internal static Color SceneBackground()
        {
            try
            {
                var cam = scrCamera.instance;
                if (cam != null && cam.Bgcamstatic != null)
                    return cam.Bgcamstatic.backgroundColor;
            }
            catch { }

            try
            {
                var level = ADOBase.editor != null ? ADOBase.editor.levelData : null;
                if (level != null) return level.backgroundColor;
            }
            catch { }

            // Black is the game's own default and the common case for a level with no art.
            return Color.black;
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
            if (v is Color color) return color;
            string s = v.ToString();
            if (ColorUtility.TryParseHtmlString(s.StartsWith("#") ? s : "#" + s, out var c)) return c;
            return fallback;
        }
    }
}
