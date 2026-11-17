using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using PEV.Model;
using UnityEngine;

namespace PEV.Scripting
{
    // Converts package-declared values and resources into compositor bindings.
    internal sealed class PackageSpriteLayer : IDisposable
    {
        private readonly SpriteCompositor _compositor;
        private readonly Dictionary<string, string> _types = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, bool> _conditions = new Dictionary<string, bool>(StringComparer.Ordinal);
        private readonly Dictionary<string, object> _inputs = new Dictionary<string, object>(StringComparer.Ordinal);
        private readonly Dictionary<string, Vector4[]> _arrays = new Dictionary<string, Vector4[]>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _arrayCounts = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly int _arrayCapacity;

        internal PackageSpriteLayer(EventManifest manifest, CompositorDefinition definition, int sortingLayer,
            int sortingOrder, int unityLayer, int arrayCapacity)
        {
            if (arrayCapacity < 1 || arrayCapacity > 64) throw new ArgumentOutOfRangeException(nameof(arrayCapacity));
            _arrayCapacity = arrayCapacity;
            AddTypes(definition.Bindings);
            foreach (var layer in definition.Layers) AddTypes(layer.Bindings);
            _compositor = new SpriteCompositor(manifest, definition, sortingLayer, sortingOrder, unityLayer);
        }
        private void AddTypes(IEnumerable<CompositorDefinition.Binding> bindings)
        {
            foreach (var binding in bindings)
            {
                if (_types.TryGetValue(binding.Input, out var type) && type != binding.Type)
                    throw new ArgumentException("Inconsistent compositor input type");
                _types[binding.Input] = binding.Type;
                if (binding.CountInput != null)
                {
                    if (_arrayCounts.TryGetValue(binding.Input, out var count) && count != binding.CountInput)
                        throw new ArgumentException("Inconsistent compositor array count");
                    _arrayCounts[binding.Input] = binding.CountInput;
                }
            }
        }
        internal bool Draw(Camera camera, IComputeFrame frame, IReadOnlyDictionary<string, Texture2D> images,
            float aspect, float time, IReadOnlyDictionary<string, bool> settings, JObject conditions, JObject values,
            Color background, IReadOnlyDictionary<string, Texture> textures)
        {
            if (camera == null || camera.pixelWidth <= 0 || camera.pixelHeight <= 0 || frame == null || images == null ||
                settings == null || conditions == null || conditions.Count > 64 || values == null || values.Count > 64 || textures == null)
                throw new ArgumentException("Invalid package compositor commands");
            _conditions.Clear(); _inputs.Clear();
            foreach (var condition in conditions.Properties())
            {
                if (condition.Value.Type != JTokenType.Boolean || condition.Name.StartsWith("setting_", StringComparison.Ordinal))
                    throw new ArgumentException("Invalid compositor condition");
                _conditions.Add(condition.Name, condition.Value.Value<bool>());
            }
            foreach (var setting in settings) _conditions.Add("setting_" + setting.Key, setting.Value);
            float width = camera.pixelWidth, height = camera.pixelHeight;
            _inputs.Add("view", new Vector4(width, height, aspect, 0));
            _inputs.Add("time", new Vector4(time / 20f, time, time * 2f, time * 3f));
            _inputs.Add("screen", new Vector4(width, height, 1f + 1f / width, 1f + 1f / height));
            _inputs.Add("background", background);
            foreach (var output in frame.Outputs)
            {
                if (output.Value == null) throw new ArgumentException("Invalid compute output");
                _inputs.Add("output_" + output.Key, output.Value);
                _inputs.Add("output_" + output.Key + "_texelSize", new Vector4(1f / output.Value.width,
                    1f / output.Value.height, output.Value.width, output.Value.height));
            }
            foreach (var image in images) _inputs.Add("image_" + image.Key, image.Value);
            foreach (var texture in textures) if (texture.Value != null) _inputs.Add(texture.Key, texture.Value);
            foreach (var value in values.Properties())
            {
                if (!_types.TryGetValue(value.Name, out var type)) throw new ArgumentException("Undeclared compositor input");
                if (_arrayCounts.TryGetValue(value.Name, out var countInput) &&
                    (!(value.Value is JArray items) || values[countInput]?.Type != JTokenType.Integer || values.Value<long>(countInput) != items.Count))
                    throw new ArgumentException("Compositor array count does not match input");
                _inputs.Add(value.Name, ConvertValue(value.Name, value.Value, type));
            }
            return _compositor.Draw(camera, _conditions, _inputs, time);
        }
        private object ConvertValue(string name, JToken value, string type)
        {
            switch (type)
            {
                case "int":
                    if (value.Type != JTokenType.Integer) throw new ArgumentException("Invalid integer compositor input");
                    long n = value.Value<long>();
                    if (n < -1000000 || n > 1000000) throw new ArgumentOutOfRangeException("compositor input");
                    return (int)n;
                case "float": return Number(value);
                case "vector": return Vector(value);
                case "color":
                    var v = Vector(value); return new Color(v.x, v.y, v.z, v.w);
                case "vectorArray":
                    if (!(value is JArray items) || items.Count > _arrayCapacity) throw new ArgumentException("Invalid compositor array");
                    if (!_arrays.TryGetValue(name, out var result)) _arrays.Add(name, result = new Vector4[_arrayCapacity]);
                    for (int i = 0; i < items.Count; i++) result[i] = Vector(items[i]);
                    Array.Clear(result, items.Count, result.Length - items.Count);
                    return result;
                default: throw new ArgumentException("Unsupported package compositor value");
            }
        }
        private static Vector4 Vector(JToken value)
        {
            if (!(value is JArray a) || a.Count != 4) throw new ArgumentException("Invalid compositor vector");
            return new Vector4(Number(a[0]), Number(a[1]), Number(a[2]), Number(a[3]));
        }
        private static float Number(JToken value)
        {
            if (value == null || (value.Type != JTokenType.Integer && value.Type != JTokenType.Float)) throw new ArgumentException("Invalid compositor number");
            float n = value.Value<float>();
            if (float.IsNaN(n) || float.IsInfinity(n) || Math.Abs(n) > 1000000f) throw new ArgumentOutOfRangeException("compositor number");
            return n;
        }
        internal void Hide() => _compositor.Hide();
        public void Dispose() => _compositor.Dispose();
    }
}
