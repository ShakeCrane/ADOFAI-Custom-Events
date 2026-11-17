using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace PEV.Scripting
{
    // Uploads bounded vector outputs and optionally runs package-declared GPU passes.
    // Existing textures stay live until all replacements are ready.
    internal sealed class VectorTextureStorage : IDisposable
    {
        private readonly VectorTextureDefinition _definition;
        private Texture2D[] _raw;
        private RenderTexture[] _transformed;

        internal VectorTextureStorage(VectorTextureDefinition definition)
        {
            _definition = definition ?? throw new ArgumentNullException(nameof(definition));
        }
        internal bool Update(int width, int height, float aspect, bool updated, byte[][] latest,
            int latestWidth, int latestHeight, float latestAspect,
            Action<string, string, Texture, string, RenderTexture> transform)
        {
            if (latest != null)
            {
                if (latest.Length != _definition.Outputs.Count) throw new InvalidOperationException("Invalid vector output count");
                int expected = checked(latestWidth * latestHeight * 4);
                foreach (var buffer in latest) if (buffer == null || buffer.Length != expected)
                    throw new InvalidOperationException("Invalid vector output size");
            }
            bool matches = Matches(width, height);
            if (!updated && !matches && latest != null && latestWidth == width &&
                latestHeight == height && latestAspect == aspect) updated = true;
            if (updated)
            {
                if (matches)
                {
                    for (int i = 0; i < _raw.Length; i++) Upload(_raw[i], latest[i]);
                }
                else Allocate(width, height, latest);
            }
            if (!Matches(width, height)) return false;
            for (int i = 0; i < _definition.Outputs.Count; i++)
            {
                var output = _definition.Outputs[i];
                if (output.Entry == null) continue;
                if (transform == null) throw new ArgumentNullException(nameof(transform));
                transform(output.Entry, output.Input, _raw[i], output.Target, _transformed[i]);
            }
            return true;
        }
        internal Texture Get(string name)
        {
            for (int i = 0; i < _definition.Outputs.Count; i++)
                if (_definition.Outputs[i].Name == name) return _raw == null ? null :
                    (Texture)(_transformed[i] != null ? (Texture)_transformed[i] : _raw[i]);
            throw new ArgumentException("Unknown vector texture output");
        }
        private bool Matches(int width, int height)
        {
            if (_raw == null || _transformed == null) return false;
            for (int i = 0; i < _raw.Length; i++)
                if (_raw[i] == null || _raw[i].width != width || _raw[i].height != height ||
                    (_definition.Outputs[i].Entry != null && (_transformed[i] == null || !_transformed[i].IsCreated() ||
                        _transformed[i].width != width || _transformed[i].height != height))) return false;
            return true;
        }
        private void Allocate(int width, int height, byte[][] buffers)
        {
            var raw = new Texture2D[_definition.Outputs.Count];
            var transformed = new RenderTexture[raw.Length];
            try
            {
                for (int i = 0; i < raw.Length; i++)
                {
                    var output = _definition.Outputs[i];
                    raw[i] = MakeTexture(width, height, "PEV.Vector." + output.Name, buffers[i], output.Filter, output.Wrap, output.Linear);
                    if (output.Entry == null) continue;
                    var desc = new RenderTextureDescriptor(width, height)
                    {
                        graphicsFormat = GraphicsFormat.R8G8B8A8_UNorm,
                        depthBufferBits = 0, msaaSamples = 1, enableRandomWrite = true,
                        sRGB = !output.Linear, useMipMap = false, autoGenerateMips = false
                    };
                    transformed[i] = new RenderTexture(desc) { name = "PEV.VectorOutput." + output.Name,
                        filterMode = output.Filter, wrapMode = output.Wrap };
                    if (!transformed[i].Create()) throw new InvalidOperationException("Could not allocate vector output");
                }
                ReleaseTextures(); _raw = raw; _transformed = transformed; raw = null; transformed = null;
            }
            finally { Destroy(raw, transformed); }
        }
        internal static Texture2D MakeTexture(int width, int height, string name, byte[] bytes,
            FilterMode filter, TextureWrapMode wrap, bool linear)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false, linear)
            { name = name, filterMode = filter, wrapMode = wrap };
            try { Upload(texture, bytes); return texture; }
            catch { UnityEngine.Object.Destroy(texture); throw; }
        }
        private static void Upload(Texture2D texture, byte[] bytes) { texture.LoadRawTextureData(bytes); texture.Apply(false, false); }
        private void ReleaseTextures() { Destroy(_raw, _transformed); _raw = null; _transformed = null; }
        private static void Destroy(Texture2D[] raw, RenderTexture[] transformed)
        {
            if (raw != null) foreach (var texture in raw) if (texture != null) UnityEngine.Object.Destroy(texture);
            if (transformed != null) foreach (var texture in transformed)
                if (texture != null) { texture.Release(); UnityEngine.Object.Destroy(texture); }
        }
        public void Dispose() => ReleaseTextures();
    }

    internal sealed class VectorTextureSet<TItem, TOptions> : IDisposable where TItem : struct where TOptions : struct
    {
        private readonly StructuredVectorWorker<TItem, TOptions> _worker;
        private readonly VectorTextureStorage _storage;
        internal VectorTextureSet(string source, VectorTextureDefinition definition)
        {
            _worker = new StructuredVectorWorker<TItem, TOptions>(source, definition.Outputs.Count);
            _storage = new VectorTextureStorage(definition);
        }
        internal bool Update(int width, int height, float aspect, TItem[] items, TOptions options,
            Action<string, string, Texture, string, RenderTexture> transform)
        {
            bool updated = _worker.Update(width, height, aspect, items, options);
            if (_worker.LastError != null) throw new InvalidOperationException("Vector texture worker failed", _worker.LastError);
            return _storage.Update(width, height, aspect, updated, _worker.Latest, _worker.LatestWidth,
                _worker.LatestHeight, _worker.LatestAspect, transform);
        }
        internal Texture Get(string name) => _storage.Get(name);
        internal static Texture2D MakeTexture(int width, int height, string name, byte[] bytes,
            FilterMode filter, TextureWrapMode wrap, bool linear) => VectorTextureStorage.MakeTexture(width, height, name, bytes, filter, wrap, linear);
        public void Dispose() { _worker.Dispose(); _storage.Dispose(); }
    }
}
