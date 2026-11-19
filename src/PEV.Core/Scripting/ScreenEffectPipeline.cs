using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;
using PEV.Model;
using UnityEngine;

namespace PEV.Scripting
{
    /// <summary>
    /// The full-screen post-processing stages declared by event packages.
    ///
    /// Colour grading has to see the finished picture, so the stage runs where the game's own
    /// image effects already run: on the gameplay camera's OnRenderImage chain. That hook was
    /// measured, not assumed — in the editor the world cameras render straight to the screen
    /// and the gameplay camera's chain receives the composed frame; during playback they
    /// render into camRT and the same chain receives the same composed frame before the
    /// overlay quad displays it. One attachment therefore covers both, and the UI, which is a
    /// screen-space overlay drawn after every camera, is correctly left alone.
    ///
    /// The stage owns no shader of its own. The package declares one, the package's script
    /// supplies uniforms, and this class only moves data across: it never decides what a
    /// "shadow lift" means. That boundary is what lets a second event reuse the same
    /// machinery for an unrelated screen effect.
    /// </summary>
    internal static class ScreenEffectPipeline
    {
        private const int MaxLayers = 8;
        private const int MaxCurveRoles = 4;
        private const int MaxVectorArrayLength = 32;
        private const string MainTexture = "_MainTex";

        /// <summary>Uniform names Core writes itself; a package may not shadow them.</summary>
        private static readonly HashSet<string> Reserved = new HashSet<string>(StringComparer.Ordinal)
        {
            MainTexture, "_LutTex", "_LutTex2D", "_LutSize", "_LutMode", "_LutEnabled",
            "_LutDomainMin", "_LutDomainMax", "_PEVLinearProject", "_PEVWeight"
        };

        internal sealed class CurveSlot
        {
            internal Texture2D Texture;
            internal string Signature;
        }

        internal sealed class Layer
        {
            internal string Scope;
            internal EventManifest Manifest;
            internal Material Material;
            internal int Order;
            internal int Sequence;
            internal bool Enabled;
            internal string Payload;

            /// <summary>Layer weight requested by the package for this frame (0..1).</summary>
            internal float Weight = 1f;

            /// <summary>Set when a newer replace-mode layer superseded this one.</summary>
            internal bool FadingOut;
            internal float FadeOutRemaining;
            internal float FadeOutDuration;
            internal float FadeOutApplied = 1f;

            internal string Mode = "replace";

            internal readonly Dictionary<string, CurveSlot> Curves =
                new Dictionary<string, CurveSlot>(StringComparer.Ordinal);

            internal string LutKey;
            internal string LutResolved;
            internal string LutError;

            /// <summary>Identity table bound when a role is not supplied, so sampling is safe.</summary>
            internal Texture2D Identity;
        }

        private static readonly Dictionary<string, Layer> Layers =
            new Dictionary<string, Layer>(StringComparer.Ordinal);
        private static readonly List<Layer> Ordered = new List<Layer>();
        private static int _sequence;
        private static int _warnedLimit;
        private static GameObject _driverObject;
        private static ScreenEffectDriver _driver;

        // Scope capture is off unless a panel asks for it: a diagnostic view must not cost
        // the running chart anything.
        internal static bool CaptureScopes;
        internal static RenderTexture ScopeTarget;

        internal static int LayerCount => Layers.Count;

        internal static string Report() =>
            $"layers={Layers.Count} driver={(_driver != null && _driver.isActiveAndEnabled)} luts=[{LutCache.Report()}]";

        /// <summary>The renderable stack, in the order the shader passes will run.</summary>
        internal static List<Layer> ActiveLayers()
        {
            Ordered.Clear();
            foreach (var layer in Layers.Values)
                if (layer.Enabled && layer.Material != null && EffectiveWeight(layer) > 0.0001f) Ordered.Add(layer);
            Ordered.Sort((a, b) => a.Order != b.Order ? a.Order.CompareTo(b.Order) : a.Sequence.CompareTo(b.Sequence));
            return Ordered;
        }

        /// <summary>Finds one layer by scope, for callers that drive a single stage.</summary>
        internal static Layer Find(string scope) =>
            !string.IsNullOrEmpty(scope) && Layers.TryGetValue(scope, out var layer) ? layer : null;

        /// <summary>Weight the driver publishes for a layer on the frame it is drawn.</summary>
        internal static float WeightFor(Layer layer) => EffectiveWeight(layer);

        private static float EffectiveWeight(Layer layer)
        {
            float weight = Mathf.Clamp01(layer.Weight);
            if (layer.FadingOut) weight *= Mathf.Clamp01(layer.FadeOutApplied);
            return weight;
        }

        /// <summary>
        /// Applies one script call. The payload is compared against the previous one first:
        /// a static grade re-sends the same values sixty times a second, and rebuilding
        /// textures for them would be pure waste.
        /// </summary>
        internal static bool Set(string scope, EventManifest manifest, Dictionary<string, object> args,
            out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(scope)) { error = "a screen effect needs an instance scope"; return false; }
            if (manifest == null || manifest.ScreenEffect == null)
            { error = "the package does not declare a screenEffect"; return false; }

            string payload = Convert.ToString(args.TryGetValue("__payload", out var raw) ? raw : null,
                CultureInfo.InvariantCulture);
            if (string.IsNullOrEmpty(payload)) { error = "missing screen effect payload"; return false; }

            if (Layers.TryGetValue(scope, out var existing) && existing.Payload == payload &&
                existing.Manifest == manifest && existing.Material != null && !existing.FadingOut)
                return true;

            if (!Layers.ContainsKey(scope) && Layers.Count >= MaxLayers)
            {
                if (_warnedLimit != MaxLayers)
                {
                    _warnedLimit = MaxLayers;
                    PEV.Core.Plugin.Log($"screen effect layer limit reached ({MaxLayers}); ignoring '{scope}'");
                }
                error = "too many simultaneous screen effects";
                return false;
            }

            JObject spec;
            try { spec = JObject.Parse(payload); }
            catch (Exception e) { error = "screen effect payload is not valid JSON: " + e.Message; return false; }

            var layer = existing;
            if (layer == null)
            {
                layer = new Layer { Scope = scope, Sequence = _sequence++ };
                Layers[scope] = layer;
            }

            layer.Manifest = manifest;
            layer.Payload = payload;
            layer.Enabled = spec.Value<bool?>("enabled") ?? true;
            layer.Order = Mathf.Clamp(spec.Value<int?>("order") ?? 0, -1024, 1024);
            layer.Weight = Mathf.Clamp01(spec.Value<float?>("weight") ?? 1f);
            string mode = spec.Value<string>("mode");
            layer.Mode = mode == "stack" ? "stack" : "replace";

            // A replace-mode layer supersedes older replace-mode layers: the newest grade is
            // the one the author means, and keeping the old one underneath would double the
            // effect instead of switching it. Stack-mode layers are left alone on purpose.
            if (layer.Mode == "replace")
            {
                float fade = Mathf.Clamp(spec.Value<float?>("fade") ?? 0f, 0f, 60f);
                foreach (var other in Layers.Values)
                {
                    if (other == layer || other.Mode != "replace" || other.Sequence >= layer.Sequence) continue;
                    if (other.FadingOut) continue;
                    other.FadingOut = true;
                    other.FadeOutDuration = fade;
                    other.FadeOutRemaining = fade;
                    other.FadeOutApplied = 1f;
                }
            }
            else
            {
                layer.FadingOut = false;
                layer.FadeOutApplied = 1f;
            }

            if (!EnsureMaterial(layer, out error)) return false;

            try
            {
                ApplyFloats(layer.Material, spec["floats"] as JObject);
                ApplyVectors(layer.Material, spec["vectors"] as JObject);
                ApplyVectorArrays(layer.Material, spec["vectorArrays"] as JObject);
                ApplyColors(layer.Material, spec["colors"] as JObject);
                ApplyCurves(layer, spec["curves"] as JObject, out error);
                if (error != null) return false;
                ApplyLut(layer, spec["lut"] as JObject, out error);
                if (error != null) return false;
            }
            catch (Exception e)
            {
                error = "screen effect uniforms failed: " + e.Message;
                return false;
            }

            EnsureDriver();
            return true;
        }

        private static bool EnsureMaterial(Layer layer, out string error)
        {
            error = null;
            if (layer.Material != null && layer.Material.shader != null) return true;

            var spec = layer.Manifest.ScreenEffect;
            if (!layer.Manifest.ShaderAssets.TryGetValue(spec.ShaderRole, out string assetName))
            { error = "shader role is not declared: " + spec.ShaderRole; return false; }

            var shader = ShaderBundleCache.Load(layer.Manifest, assetName, out string loadError);
            if (shader == null) { error = loadError; return false; }

            DestroyMaterial(layer);
            layer.Material = new Material(shader)
            {
                name = "PEV.Screen." + layer.Scope,
                hideFlags = HideFlags.DontSave
            };
            // A shader that samples display-referred values has to be told which colour space
            // the project runs in; converting already-linear pixels again is the difference
            // between a correct grade and a washed-out one.
            layer.Material.SetFloat("_PEVLinearProject",
                QualitySettings.activeColorSpace == ColorSpace.Linear ? 1f : 0f);
            layer.Material.SetFloat("_PEVWeight", 1f);
            layer.Material.SetFloat("_LutEnabled", 0f);
            return true;
        }

        private static void ApplyFloats(Material material, JObject values)
        {
            if (values == null) return;
            foreach (var property in values.Properties())
            {
                ValidateName(property.Name);
                if (property.Value.Type != JTokenType.Integer && property.Value.Type != JTokenType.Float) continue;
                float value = property.Value.Value<float>();
                if (float.IsNaN(value) || float.IsInfinity(value)) continue;
                material.SetFloat(property.Name, value);
            }
        }

        private static void ApplyVectors(Material material, JObject values)
        {
            if (values == null) return;
            foreach (var property in values.Properties())
            {
                ValidateName(property.Name);
                if (!(property.Value is JArray array) || array.Count != 4) continue;
                material.SetVector(property.Name, new Vector4(
                    Finite(array[0]), Finite(array[1]), Finite(array[2]), Finite(array[3])));
            }
        }

        private static void ApplyVectorArrays(Material material, JObject values)
        {
            if (values == null) return;
            foreach (var property in values.Properties())
            {
                ValidateName(property.Name);
                if (!(property.Value is JArray rows) || rows.Count == 0 || rows.Count > MaxVectorArrayLength) continue;
                var packed = new Vector4[rows.Count];
                bool ok = true;
                for (int i = 0; i < rows.Count; i++)
                {
                    if (!(rows[i] is JArray row) || row.Count != 4) { ok = false; break; }
                    packed[i] = new Vector4(Finite(row[0]), Finite(row[1]), Finite(row[2]), Finite(row[3]));
                }
                if (ok) material.SetVectorArray(property.Name, packed);
            }
        }

        private static void ApplyColors(Material material, JObject values)
        {
            if (values == null) return;
            foreach (var property in values.Properties())
            {
                ValidateName(property.Name);
                if (property.Value.Type != JTokenType.String) continue;
                if (ColorUtility.TryParseHtmlString("#" + property.Value.Value<string>(), out var color))
                    material.SetColor(property.Name, color);
            }
        }

        /// <summary>
        /// Bakes each requested curve role into its own RGBA table.
        ///
        /// Roles are named by the package, so a grading stage can ask for a curve table and a
        /// hue table without Core knowing what either is for. A role with no points keeps an
        /// identity table bound: sampling an unbound sampler returns white, which would blow
        /// the picture out, so "not configured" has to mean "no change", not "no texture".
        /// </summary>
        private static void ApplyCurves(Layer layer, JObject curves, out string error)
        {
            error = null;
            var seen = new List<string>();
            if (curves != null)
            {
                foreach (var property in curves.Properties())
                {
                    ValidateName(property.Name);
                    if (!(property.Value is JObject set)) continue;
                    if (seen.Count >= MaxCurveRoles) { error = "too many curve roles"; return; }
                    seen.Add(property.Name);

                    var master = Floats(set["master"]);
                    var red = Floats(set["red"]);
                    var green = Floats(set["green"]);
                    var blue = Floats(set["blue"]);
                    string signature = Signature(master, red, green, blue);

                    if (!layer.Curves.TryGetValue(property.Name, out var slot))
                    {
                        slot = new CurveSlot();
                        layer.Curves[property.Name] = slot;
                    }
                    if (slot.Texture != null && slot.Signature == signature)
                    {
                        layer.Material.SetTexture(property.Name, slot.Texture);
                        continue;
                    }

                    var texture = CurveBake.BuildTexture(master, red, green, blue,
                        "PEV.Curve." + layer.Scope + "." + property.Name);
                    if (texture == null) { error = "curve points are invalid for " + property.Name; return; }
                    if (slot.Texture != null) UnityEngine.Object.Destroy(slot.Texture);
                    slot.Texture = texture;
                    slot.Signature = signature;
                    layer.Material.SetTexture(property.Name, texture);
                }
            }

            EnsureIdentity(layer);
            foreach (var pair in layer.Curves)
            {
                if (seen.Contains(pair.Key)) continue;
                if (pair.Value.Texture != null)
                {
                    UnityEngine.Object.Destroy(pair.Value.Texture);
                    pair.Value.Texture = null;
                    pair.Value.Signature = null;
                }
                layer.Material.SetTexture(pair.Key, layer.Identity);
            }
        }

        private static void EnsureIdentity(Layer layer)
        {
            if (layer.Identity != null) return;
            layer.Identity = CurveBake.BuildTexture(null, null, null, null, "PEV.Identity");
        }

        private static void ApplyLut(Layer layer, JObject lut, out string error)
        {
            error = null;
            if (lut == null || !(lut.Value<bool?>("enabled") ?? true))
            {
                ReleaseLut(layer);
                layer.Material.SetFloat("_LutEnabled", 0f);
                return;
            }

            string path = lut.Value<string>("path");
            if (string.IsNullOrEmpty(path))
            {
                layer.Material.SetFloat("_LutEnabled", 0f);
                return;
            }

            string resolved = LutCache.Resolve(path, layer.Manifest.SourcePath, out string resolveError);
            if (resolved == null)
            {
                // Fail once per path rather than every frame; the author needs to see it, but
                // a broken LUT must not spam the log at frame rate.
                if (layer.LutResolved != path || layer.LutError != resolveError)
                {
                    layer.LutResolved = path;
                    layer.LutError = resolveError;
                    PEV.Core.Plugin.Log("screen LUT: " + resolveError);
                }
                layer.Material.SetFloat("_LutEnabled", 0f);
                return;
            }

            if (layer.LutKey != null && layer.LutResolved == resolved)
            {
                layer.Material.SetFloat("_LutEnabled", 1f);
                return;
            }

            var entry = LutCache.Acquire(resolved, layer.Manifest.SourcePath, path, out string acquireError);
            if (entry == null)
            {
                if (layer.LutError != acquireError)
                {
                    layer.LutError = acquireError;
                    PEV.Core.Plugin.Log("screen LUT: " + acquireError);
                }
                layer.Material.SetFloat("_LutEnabled", 0f);
                return;
            }

            ReleaseLut(layer);
            layer.LutKey = entry.Key;
            layer.LutResolved = resolved;
            layer.LutError = null;

            var texture = (Texture)(entry.Volume != null ? (Texture)entry.Volume : entry.Strip);
            // A volume and a strip cannot share a uniform slot: Unity binds by texture type,
            // so the shader declares one of each and Core fills the matching one.
            layer.Material.SetTexture(entry.Volume != null ? "_LutTex" : "_LutTex2D", texture);
            layer.Material.SetFloat("_LutSize", entry.Size);
            layer.Material.SetFloat("_LutMode", entry.IsThreeDimensional ? 0f : 1f);
            layer.Material.SetVector("_LutDomainMin", entry.DomainMin);
            layer.Material.SetVector("_LutDomainMax", entry.DomainMax);
            layer.Material.SetFloat("_LutEnabled", 1f);
        }

        private static void ReleaseLut(Layer layer)
        {
            if (layer.LutKey == null) return;
            LutCache.Release(layer.LutKey);
            layer.LutKey = null;
            layer.LutResolved = null;
        }

        internal static void Clear(string scope)
        {
            if (string.IsNullOrEmpty(scope)) return;
            if (!Layers.TryGetValue(scope, out var layer)) return;
            Layers.Remove(scope);
            Dispose(layer);
            if (Layers.Count == 0) DetachDriver();
        }

        internal static void ClearAll()
        {
            var layers = new List<Layer>(Layers.Values);
            Layers.Clear();
            foreach (var layer in layers) Dispose(layer);
            DetachDriver();
            LutCache.Clear();
            ReleaseScope();
            _warnedLimit = 0;
        }

        private static void Dispose(Layer layer)
        {
            if (layer == null) return;
            ReleaseLut(layer);
            foreach (var slot in layer.Curves.Values)
                if (slot.Texture != null) UnityEngine.Object.Destroy(slot.Texture);
            layer.Curves.Clear();
            if (layer.Identity != null) UnityEngine.Object.Destroy(layer.Identity);
            layer.Identity = null;
            DestroyMaterial(layer);
        }

        private static void DestroyMaterial(Layer layer)
        {
            if (layer.Material == null) return;
            UnityEngine.Object.Destroy(layer.Material);
            layer.Material = null;
        }

        /// <summary>
        /// Keeps the image effect on whichever camera currently draws the game.
        ///
        /// The camera object survives scene changes but the components on it do not
        /// necessarily, and the editor and the gameplay scene do not use the same camera
        /// stack. Re-checking each frame is cheap and removes a whole class of "worked until
        /// I pressed play" failures.
        /// </summary>
        internal static void Tick()
        {
            if (Layers.Count == 0) { DetachDriver(); return; }
            AdvanceFades();
            // A layer that exists but is switched off still has to cost nothing: keeping the
            // component attached would put the camera through an image-effect pass that only
            // copies the frame.
            if (Layers.Count == 0 || ActiveLayers().Count == 0) { DetachDriver(); return; }
            EnsureDriver();
        }

        /// <summary>
        /// Advances replaced layers toward zero and drops them when they arrive.
        ///
        /// The remaining weight is published as a uniform rather than as a per-layer render
        /// target because a sequential blend already produces a continuous crossfade when the
        /// outgoing layer fades out and the incoming one fades in over the same interval.
        /// </summary>
        private static void AdvanceFades()
        {
            float delta = Time.unscaledDeltaTime;
            if (delta <= 0f || float.IsNaN(delta) || float.IsInfinity(delta)) delta = 1f / 60f;
            List<string> expired = null;
            foreach (var layer in Layers.Values)
            {
                if (!layer.FadingOut) continue;
                if (layer.FadeOutDuration <= 0.0001f)
                {
                    layer.FadeOutApplied = 0f;
                }
                else
                {
                    layer.FadeOutRemaining = Mathf.Max(0f, layer.FadeOutRemaining - delta);
                    float progress = 1f - layer.FadeOutRemaining / layer.FadeOutDuration;
                    // Smoothstep: a linear ramp on a full-screen grade has a visible kink at
                    // both ends.
                    layer.FadeOutApplied = 1f - (progress * progress * (3f - 2f * progress));
                }
                if (layer.FadeOutApplied <= 0.0005f)
                {
                    if (expired == null) expired = new List<string>();
                    expired.Add(layer.Scope);
                }
            }
            if (expired == null) return;
            foreach (var scope in expired)
            {
                if (!Layers.TryGetValue(scope, out var layer)) continue;
                Layers.Remove(scope);
                Dispose(layer);
            }
        }

        /// <summary>
        /// Keeps a small copy of the incoming frame for the panel's scopes.
        ///
        /// Downscaled on the GPU rather than read back every frame: the scopes only need a
        /// statistical sample, and a full-resolution readback four times a second would cost
        /// more than the grade itself. Nothing here runs unless a panel is open.
        /// </summary>
        internal static void CaptureScope(RenderTexture source)
        {
            const int width = 192, height = 108;
            try
            {
                if (ScopeTarget == null || ScopeTarget.width != width || ScopeTarget.height != height)
                {
                    if (ScopeTarget != null) { ScopeTarget.Release(); UnityEngine.Object.Destroy(ScopeTarget); }
                    ScopeTarget = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32)
                    {
                        name = "PEV.Scope",
                        filterMode = FilterMode.Point,
                        wrapMode = TextureWrapMode.Clamp,
                        hideFlags = HideFlags.DontSave
                    };
                    ScopeTarget.Create();
                }
                Graphics.Blit(source, ScopeTarget);
            }
            catch (Exception e) { PEV.Core.Plugin.Log("scope capture failed: " + e.Message); }
        }

        internal static void ReleaseScope()
        {
            if (ScopeTarget == null) return;
            ScopeTarget.Release();
            UnityEngine.Object.Destroy(ScopeTarget);
            ScopeTarget = null;
        }

        private static void EnsureDriver()
        {
            var camera = GameCamera.Current;
            if (camera == null) return;
            if (_driver != null && _driverObject == camera.gameObject && _driver.isActiveAndEnabled) return;

            DetachDriver();
            try
            {
                _driverObject = camera.gameObject;
                _driver = _driverObject.AddComponent<ScreenEffectDriver>();
                _driver.hideFlags = HideFlags.DontSave;
            }
            catch (Exception e)
            {
                PEV.Core.Plugin.Log("screen effect driver attach failed: " + e.Message);
                _driver = null;
                _driverObject = null;
            }
        }

        private static void DetachDriver()
        {
            if (_driver == null) return;
            try { UnityEngine.Object.Destroy(_driver); } catch { }
            _driver = null;
            _driverObject = null;
        }

        private static void ValidateName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > 33 || name[0] != '_')
                throw new ArgumentException("shader uniform names must start with '_': " + name);
            for (int i = 1; i < name.Length; i++)
            {
                char c = name[i];
                if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_'))
                    throw new ArgumentException("invalid shader uniform name: " + name);
            }
            if (Reserved.Contains(name)) throw new ArgumentException("reserved shader uniform name: " + name);
        }

        private static float Finite(JToken token)
        {
            if (token == null || (token.Type != JTokenType.Integer && token.Type != JTokenType.Float)) return 0f;
            float value = token.Value<float>();
            return float.IsNaN(value) || float.IsInfinity(value) ? 0f : value;
        }

        private static float[] Floats(JToken token)
        {
            if (!(token is JArray array)) return null;
            var result = new float[array.Count];
            for (int i = 0; i < array.Count; i++) result[i] = Finite(array[i]);
            return result;
        }

        private static string Signature(params float[][] sets)
        {
            var builder = new System.Text.StringBuilder(64);
            if (sets == null) return "";
            foreach (var set in sets)
            {
                builder.Append('|');
                if (set == null) { builder.Append('-'); continue; }
                for (int i = 0; i < set.Length; i++)
                    builder.Append(set[i].ToString("F4", CultureInfo.InvariantCulture)).Append(',');
            }
            return builder.ToString();
        }
    }

    /// <summary>
    /// The camera component that runs the declared stages, in order.
    ///
    /// With one stage this is a single blit and no render-target allocation. Extra stages
    /// ping-pong through temporaries, which is the honest cost of stacking grades.
    /// </summary>
    public class ScreenEffectDriver : MonoBehaviour
    {
        private void OnRenderImage(RenderTexture source, RenderTexture destination)
        {
            if (source == null) { Graphics.Blit(null, destination); return; }

            var layers = ScreenEffectPipeline.ActiveLayers();
            if (layers.Count == 0) { Graphics.Blit(source, destination); return; }

            RenderTexture current = source;
            try
            {
                if (ScreenEffectPipeline.CaptureScopes) ScreenEffectPipeline.CaptureScope(source);

                for (int i = 0; i < layers.Count; i++)
                {
                    var material = layers[i].Material;
                    material.SetFloat("_PEVWeight", ScreenEffectPipeline.WeightFor(layers[i]));
                    bool last = i == layers.Count - 1;
                    RenderTexture target = last
                        ? destination
                        : RenderTexture.GetTemporary(source.width, source.height, 0, source.format);
                    try { Graphics.Blit(current, target, material); }
                    catch (Exception e)
                    {
                        PEV.Core.Plugin.Log("screen effect blit failed: " + e.Message);
                        Graphics.Blit(current, target);
                    }
                    if (current != source) RenderTexture.ReleaseTemporary(current);
                    current = target;
                }
            }
            catch
            {
                if (current != source && current != destination) Graphics.Blit(source, destination);
                throw;
            }
        }
    }
}
