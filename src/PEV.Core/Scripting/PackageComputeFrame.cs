using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace PEV.Scripting
{
    internal interface IComputeFrame
    {
        int Width { get; }
        int Height { get; }
        IEnumerable<KeyValuePair<string, RenderTexture>> Outputs { get; }
        void RunExternal(string entry, string inputName, Texture input, string outputName, RenderTexture output);
    }

    // Uploads the package's declared records and inputs without interpreting their meaning.
    internal sealed class PackageComputeFrame : IComputeFrame, IDisposable
    {
        private readonly GpuGraphDefinition _definition;
        private readonly GpuComputeGraph _graph;
        private readonly Dictionary<string, GpuRecordBuffer> _buffers = new Dictionary<string, GpuRecordBuffer>(StringComparer.Ordinal);
        private readonly Dictionary<string, object> _inputs = new Dictionary<string, object>(StringComparer.Ordinal);
        private readonly Dictionary<string, Texture> _images = new Dictionary<string, Texture>(StringComparer.Ordinal);
        public int Width => _graph.Width;
        public int Height => _graph.Height;
        public IEnumerable<KeyValuePair<string, RenderTexture>> Outputs => _graph.Outputs;

        internal PackageComputeFrame(ComputeShader shader, GpuGraphDefinition definition)
        {
            _definition = definition ?? throw new ArgumentNullException(nameof(definition));
            foreach (var resource in definition.Resources.Values)
                if (resource.Buffer)
                {
                    if (resource.Fields.Count != resource.Stride / 4) throw new ArgumentException("Buffer fields are required");
                    _buffers.Add(resource.Name, new GpuRecordBuffer(resource.Stride));
                }
            _graph = new GpuComputeGraph(shader, definition);
        }

        internal void Render(int width, int height, float time, JObject buffers, JObject inputs,
            IReadOnlyDictionary<string, Texture2D> images)
        {
            if (buffers == null || inputs == null || images == null || buffers.Count != _buffers.Count || inputs.Count > 64)
                throw new ArgumentException("Invalid compute commands");
            _graph.Resize(width, height);
            foreach (var pair in _buffers)
            {
                var spec = _definition.Resources[pair.Key];
                if (!(buffers[pair.Key] is JArray rows) || rows.Count > 4096) throw new ArgumentException("Invalid compute records");
                pair.Value.Prepare(rows.Count);
                for (int i = 0; i < rows.Count; i++)
                {
                    if (!(rows[i] is JArray row) || row.Count != spec.Stride / 4) throw new ArgumentException("Invalid compute record");
                    foreach (var field in spec.Fields.Values)
                    {
                        var value = row[field.Word];
                        if (field.Type == "uint")
                        {
                            if (value.Type != JTokenType.Integer) throw new ArgumentException("Invalid unsigned record");
                            long number = value.Value<long>();
                            if (number < 0 || number > uint.MaxValue) throw new ArgumentOutOfRangeException("record");
                            pair.Value.SetUInt(i, field.Word, (uint)number);
                        }
                        else pair.Value.SetFloat(i, field.Word, Number(value));
                    }
                }
                pair.Value.Upload(_graph, pair.Key);
            }
            _inputs.Clear(); _images.Clear();
            foreach (var property in inputs.Properties())
            {
                var value = property.Value;
                if (value.Type == JTokenType.Boolean) _inputs[property.Name] = value.Value<bool>();
                else if (value.Type == JTokenType.Integer)
                {
                    long n = value.Value<long>();
                    if (n < -1000000 || n > 1000000) throw new ArgumentOutOfRangeException("compute input");
                    _inputs[property.Name] = (int)n;
                }
                else if (value.Type == JTokenType.Float) _inputs[property.Name] = Number(value);
                else throw new ArgumentException("Invalid compute input");
            }
            // Uniform declarations determine whether a JSON integer must be passed as a float.
            foreach (var uniform in _definition.Uniforms) ConvertUniform(uniform);
            foreach (var entry in _definition.Entries.Values)
                foreach (var pass in entry) foreach (var uniform in pass.Uniforms) ConvertUniform(uniform);
            _inputs["dimensions"] = new[] { Width, Height };
            _inputs["time"] = time;
            foreach (var image in images)
            {
                if (image.Value == null || image.Value.width <= 0 || image.Value.height <= 0) throw new ArgumentException("Invalid package image");
                _images[image.Key] = image.Value;
                _inputs[image.Key + "Dimensions"] = new[] { image.Value.width, image.Value.height };
            }
            _graph.Run("render", _inputs, _images);
        }
        private void ConvertUniform(GpuGraphDefinition.Uniform uniform)
        {
            if (uniform.Input != null && uniform.Type == "float" && _inputs.TryGetValue(uniform.Input, out var value) && value is int integer)
                _inputs[uniform.Input] = (float)integer;
        }
        private static float Number(JToken value)
        {
            if (value == null || (value.Type != JTokenType.Integer && value.Type != JTokenType.Float)) throw new ArgumentException("Invalid compute number");
            float n = value.Value<float>();
            if (float.IsNaN(n) || float.IsInfinity(n) || Math.Abs(n) > 1000000f) throw new ArgumentOutOfRangeException("compute number");
            return n;
        }
        public void RunExternal(string entry, string inputName, Texture input, string outputName, RenderTexture output)
        {
            if (input == null || output == null || input.width != Width || input.height != Height || output.width != Width || output.height != Height)
                throw new ArgumentException("External textures must match the compute graph size");
            _images[inputName] = input; _images[outputName] = output;
            _graph.Run(entry, _inputs, _images);
        }
        public void Dispose() => _graph.Dispose();
    }
}
