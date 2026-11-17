using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace PEV.Scripting
{
    // Executes a bounded, package-declared sequence; it has no knowledge of event semantics.
    internal sealed class GpuComputeGraph : IDisposable
    {
        private sealed class BufferSlot { internal ComputeBuffer Buffer; internal int Capacity; }
        private sealed class Kernel { internal int Id, X, Y; }
        private readonly ComputeShader _shader;
        private readonly GpuGraphDefinition _definition;
        private readonly Dictionary<string, RenderTexture> _textures = new Dictionary<string, RenderTexture>();
        private readonly Dictionary<string, BufferSlot> _buffers = new Dictionary<string, BufferSlot>();
        private readonly Dictionary<string, Kernel> _kernels = new Dictionary<string, Kernel>();
        internal int Width { get; private set; }
        internal int Height { get; private set; }

        internal GpuComputeGraph(ComputeShader shader, GpuGraphDefinition definition)
        {
            _shader = shader != null ? shader : throw new ArgumentNullException(nameof(shader));
            _definition = definition ?? throw new ArgumentNullException(nameof(definition));
            foreach (var entry in definition.Entries.Values)
                foreach (var pass in entry)
                {
                    if (_kernels.ContainsKey(pass.Kernel)) continue;
                    int id = shader.FindKernel(pass.Kernel);
                    shader.GetKernelThreadGroupSizes(id, out uint x, out uint y, out uint z);
                    if (x == 0 || y == 0 || z != 1 || x > 1024 || y > 1024 || x * y > 1024)
                        throw new InvalidOperationException("Unsupported compute thread group");
                    _kernels.Add(pass.Kernel, new Kernel { Id = id, X = (int)x, Y = (int)y });
                }
        }

        internal void Resize(int viewportWidth, int viewportHeight)
        {
            _definition.Size(viewportWidth, viewportHeight, out int width, out int height);
            if (Width == width && Height == height && _textures.Count > 0) return;
            ReleaseTextures();
            try
            {
                foreach (var resource in _definition.Resources.Values)
                {
                    if (resource.Buffer) continue;
                    if (!SystemInfo.IsFormatSupported(resource.Format, FormatUsage.LoadStore))
                        throw new NotSupportedException("GPU random-write format unavailable: " + resource.Format);
                    var descriptor = new RenderTextureDescriptor(width, height)
                    {
                        graphicsFormat = resource.Format, depthBufferBits = 0, msaaSamples = 1,
                        enableRandomWrite = true, sRGB = false, useMipMap = false, autoGenerateMips = false
                    };
                    var texture = new RenderTexture(descriptor)
                    { name = "PEV.Graph." + resource.Name, filterMode = resource.Filter, wrapMode = TextureWrapMode.Clamp };
                    if (!texture.Create())
                    { UnityEngine.Object.Destroy(texture); throw new InvalidOperationException("Could not create compute texture"); }
                    _textures.Add(resource.Name, texture);
                }
                Width = width; Height = height;
            }
            catch { ReleaseTextures(); throw; }
        }

        internal void Upload(string name, Array data, int count)
        {
            if (!_definition.Resources.TryGetValue(name, out var resource) || !resource.Buffer)
                throw new ArgumentException("Unknown compute buffer");
            if (data == null || count < 0 || count > data.Length || count > 65536 ||
                data.Rank != 1 || Marshal.SizeOf(data.GetType().GetElementType()) != resource.Stride ||
                (long)Math.Max(1, count) * resource.Stride > 16 * 1024 * 1024)
                throw new ArgumentException("Invalid compute buffer data");
            if (!_buffers.TryGetValue(name, out var slot)) _buffers.Add(name, slot = new BufferSlot());
            int capacity = Math.Max(1, count);
            if (slot.Buffer == null || slot.Capacity < capacity)
            {
                var replacement = new ComputeBuffer(capacity, resource.Stride);
                var old = slot.Buffer; slot.Buffer = replacement; slot.Capacity = capacity; old?.Release();
            }
            if (count > 0) slot.Buffer.SetData(data, 0, 0, count);
        }

        internal RenderTexture Output(string name) => _textures[_definition.Outputs[name]];
        internal IEnumerable<KeyValuePair<string, RenderTexture>> Outputs
        {
            get { foreach (var item in _definition.Outputs) yield return new KeyValuePair<string, RenderTexture>(item.Key, _textures[item.Value]); }
        }

        internal void Run(string entry, IReadOnlyDictionary<string, object> inputs, IReadOnlyDictionary<string, Texture> externals)
        {
            if (Width == 0 || !_definition.Entries.TryGetValue(entry, out var passes))
                throw new InvalidOperationException("Compute graph is not ready");
            SetUniforms(_definition.Uniforms, inputs);
            foreach (var pass in passes)
            {
                if (pass.When != null)
                {
                    if (!inputs.TryGetValue(pass.When, out var enabled) || !(enabled is bool))
                        throw new ArgumentException("Missing compute condition " + pass.When);
                    if (!(bool)enabled) continue;
                }
                SetUniforms(pass.Uniforms, inputs);
                var kernel = _kernels[pass.Kernel];
                foreach (var binding in pass.Textures)
                {
                    Texture texture;
                    if (binding.Value[0] == '$')
                    {
                        if (!externals.TryGetValue(binding.Value.Substring(1), out texture) || texture == null)
                            throw new ArgumentException("Missing external compute texture");
                    }
                    else texture = _textures[binding.Value];
                    _shader.SetTexture(kernel.Id, binding.Key, texture);
                }
                foreach (var binding in pass.Buffers)
                {
                    if (!_buffers.TryGetValue(binding.Value, out var slot) || slot.Buffer == null)
                        throw new ArgumentException("Missing compute buffer");
                    _shader.SetBuffer(kernel.Id, binding.Key, slot.Buffer);
                }
                _shader.Dispatch(kernel.Id, (Width + kernel.X - 1) / kernel.X, (Height + kernel.Y - 1) / kernel.Y, 1);
            }
        }

        private void SetUniforms(List<GpuGraphDefinition.Uniform> uniforms, IReadOnlyDictionary<string, object> inputs)
        {
            foreach (var uniform in uniforms)
            {
                object value = null;
                if (uniform.Input != null && !inputs.TryGetValue(uniform.Input, out value))
                    throw new ArgumentException("Missing compute input " + uniform.Input);
                switch (uniform.Type)
                {
                    case "int":
                        if (uniform.Input == null) _shader.SetInt(uniform.Name, uniform.IntValue);
                        else if (value is int integer) _shader.SetInt(uniform.Name, integer);
                        else throw new ArgumentException("Invalid integer compute input");
                        break;
                    case "float":
                        float number = uniform.Input == null ? uniform.FloatValue : value is float f ? f : float.NaN;
                        if (float.IsNaN(number) || float.IsInfinity(number)) throw new ArgumentException("Invalid float compute input");
                        _shader.SetFloat(uniform.Name, number);
                        break;
                    case "int2":
                        if (!(value is int[] vector) || vector.Length != 2) throw new ArgumentException("Invalid vector compute input");
                        _shader.SetInts(uniform.Name, vector);
                        break;
                }
            }
        }

        private void ReleaseTextures()
        {
            foreach (var texture in _textures.Values) { texture.Release(); UnityEngine.Object.Destroy(texture); }
            _textures.Clear(); Width = Height = 0;
        }
        public void Dispose()
        {
            ReleaseTextures();
            foreach (var slot in _buffers.Values) slot.Buffer?.Release();
            _buffers.Clear();
        }
    }
}
