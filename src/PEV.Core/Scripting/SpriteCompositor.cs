using System;
using System.Collections.Generic;
using PEV.Model;
using UnityEngine;
using UnityEngine.Rendering;

namespace PEV.Scripting
{
    // Generic sorted fullscreen sprite layers with package-declared variants and bindings.
    internal sealed class SpriteCompositor : IDisposable
    {
        private sealed class Layer
        {
            internal CompositorDefinition.Layer Definition;
            internal SpriteRenderer Renderer;
            internal VisibilityActivationGate Activation;
        }
        private readonly CompositorDefinition _definition;
        private readonly IReadOnlyList<FieldWaveSpec> _waves;
        private readonly Dictionary<string, Material> _materials = new Dictionary<string, Material>(StringComparer.Ordinal);
        private readonly List<Layer> _layers = new List<Layer>();
        private GameObject _root;
        private VisibilityActivationGate _activation;

        internal SpriteCompositor(EventManifest manifest, CompositorDefinition definition, int sortingLayer, int sortingOrder, int unityLayer)
        {
            _definition = definition ?? throw new ArgumentNullException(nameof(definition));
            _waves = manifest.FieldWaves;
            try
            {
                foreach (var layer in definition.Layers)
                    foreach (var variant in layer.Variants)
                    {
                        if (_materials.ContainsKey(variant.Shader)) continue;
                        if (!manifest.ShaderAssets.TryGetValue(variant.Shader, out var path))
                            throw new InvalidOperationException("Shader role is not declared: " + variant.Shader);
                        var shader = ShaderBundleCache.Load(manifest, path, out string error);
                        if (shader == null) throw new InvalidOperationException(error);
                        var material = new Material(shader) { name = "PEV." + variant.Shader };
                        _materials.Add(variant.Shader, material);
                        ApplyUniforms(material, manifest.FieldUniforms);
                    }
                _root = new GameObject("PEV.SpriteCompositor");
                _root.layer = unityLayer; _root.SetActive(false);
                var sorting = _root.AddComponent<SortingGroup>();
                sorting.sortingLayerID = sortingLayer; sorting.sortingOrder = sortingOrder;
                foreach (var definitionLayer in definition.Layers)
                {
                    var child = new GameObject(definitionLayer.Name) { layer = unityLayer };
                    child.transform.SetParent(_root.transform, false);
                    var renderer = child.AddComponent<SpriteRenderer>();
                    renderer.sprite = SolidSprite.White; renderer.sortingOrder = definitionLayer.Order; renderer.color = Color.white;
                    _layers.Add(new Layer { Definition = definitionLayer, Renderer = renderer });
                }
            }
            catch { Dispose(); throw; }
        }

        internal static void ApplyUniforms(Material material, IReadOnlyList<FieldUniformSpec> uniforms)
        {
            foreach (var uniform in uniforms)
            {
                var values = uniform.Values;
                if (values.Length == 1) material.SetFloat(uniform.Name, values[0]);
                else material.SetVector(uniform.Name, new Vector4(values[0], values[1], values[2], values[3]));
            }
        }

        internal bool Draw(Camera camera, IReadOnlyDictionary<string, bool> conditions, IReadOnlyDictionary<string, object> inputs, float time)
        {
            if (camera == null || !camera.orthographic || camera.pixelWidth <= 0 || camera.pixelHeight <= 0)
                throw new ArgumentException("A valid orthographic camera is required");
            float distance = Mathf.Max(camera.nearClipPlane + .1f, 1f);
            if (distance >= camera.farClipPlane) throw new ArgumentException("Camera clipping range cannot contain quad");
            _root.transform.position = camera.transform.position + camera.transform.forward * distance;
            _root.transform.rotation = camera.transform.rotation;
            float height = camera.orthographicSize * 2f;
            _root.transform.localScale = new Vector3(height * camera.aspect, height, 1f);
            bool any = false;
            foreach (var layer in _layers)
            {
                if (!conditions.TryGetValue(layer.Definition.Visible, out bool visible)) throw new ArgumentException("Missing layer visibility");
                layer.Renderer.enabled = visible; layer.Renderer.color = Color.white; any |= visible;
            }
            _root.SetActive(any);
            bool settled = _activation.Observe(any, Time.frameCount);
            foreach (var layer in _layers) settled &= layer.Activation.Observe(layer.Renderer.enabled, Time.frameCount);
            if (!any) return true;
            foreach (var layer in _layers)
            {
                if (!layer.Renderer.enabled) continue;
                Material material = null;
                foreach (var variant in layer.Definition.Variants)
                    if (Matches(variant.When, conditions)) { material = _materials[variant.Shader]; break; }
                if (material == null) throw new InvalidOperationException("No compositor material matches the inputs");
                layer.Renderer.sharedMaterial = material;
                Bind(material, _definition.Bindings, conditions, inputs);
                Bind(material, layer.Definition.Bindings, conditions, inputs);
                foreach (var wave in _waves) material.SetFloat(wave.Name, wave.At(time));
            }
            return settled;
        }
        private static bool Matches(Dictionary<string, bool> expected, IReadOnlyDictionary<string, bool> actual)
        {
            foreach (var item in expected)
            {
                if (!actual.TryGetValue(item.Key, out bool value)) throw new ArgumentException("Missing compositor condition " + item.Key);
                if (value != item.Value) return false;
            }
            return true;
        }
        private static void Bind(Material material, List<CompositorDefinition.Binding> bindings,
            IReadOnlyDictionary<string, bool> conditions, IReadOnlyDictionary<string, object> inputs)
        {
            foreach (var binding in bindings)
            {
                if (!Matches(binding.When, conditions)) continue;
                if (!inputs.TryGetValue(binding.Input, out var value) || value == null) throw new ArgumentException("Missing compositor input " + binding.Input);
                switch (binding.Type)
                {
                    case "texture": if (value is Texture t && t != null) { material.SetTexture(binding.Name, t); break; } goto default;
                    case "vector": if (value is Vector4 v) { material.SetVector(binding.Name, v); break; } goto default;
                    case "color": if (value is Color c) { material.SetColor(binding.Name, c); break; } goto default;
                    case "int": if (value is int i) { material.SetInt(binding.Name, i); break; } goto default;
                    case "float": if (value is float f && !float.IsNaN(f) && !float.IsInfinity(f)) { material.SetFloat(binding.Name, f); break; } goto default;
                    case "vectorArray": if (value is Vector4[] a && a.Length <= 64) { material.SetVectorArray(binding.Name, a); break; } goto default;
                    default: throw new ArgumentException("Invalid compositor input " + binding.Input);
                }
            }
        }
        internal void Hide() { foreach (var layer in _layers) layer.Renderer.color = Color.clear; }
        public void Dispose()
        {
            if (_root != null) { UnityEngine.Object.Destroy(_root); _root = null; }
            foreach (var material in _materials.Values) if (material != null) UnityEngine.Object.Destroy(material);
            _materials.Clear(); _layers.Clear();
        }
    }
}
