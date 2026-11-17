using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace PEV.Scripting
{
    /// <summary>Draft per-group palette upload and warp; all Unity calls are main-thread-only.</summary>
    internal sealed class NoiseFieldGpuPalette : IDisposable
    {
        private NoiseFieldColorWorker _worker;
        private readonly PEV.Model.EventManifest _provider;
        private readonly int _defaultRgb;
        private Texture2D _rawActive, _disabled;
        private RenderTexture _active;

        internal Texture Active => _active;
        internal Texture Disabled => _disabled;

        internal NoiseFieldGpuPalette(PEV.Model.EventManifest provider)
        {
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
            _defaultRgb = int.TryParse(provider?.DecorationDefaultColor, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out int rgb) ? rgb : 0xffffff;
        }

        internal bool Update(NoiseFieldGpuMasks masks, float aspect,
            NoiseFieldColorOwnership.Zone[] zones)
        {
            if (_worker == null)
            {
                if (!_provider.VectorPrograms.TryGetValue("palette", out var path))
                    throw new InvalidOperationException("field package does not declare a palette vector program; update the event package");
                byte[] bytes = PEV.Loading.EventLoader.ReadResource(_provider.SourcePath, path, 262144);
                if (bytes == null || bytes.Length == 0 || bytes.Length > 262144)
                    throw new InvalidOperationException("field package palette vector program is missing or too large");
                string source = new System.Text.UTF8Encoding(false, true).GetString(bytes);
                _worker = new NoiseFieldColorWorker(source);
            }
            int width = masks.Width, height = masks.Height;
            bool updated = _worker.Update(width, height, aspect, zones, _defaultRgb);
            if (_worker.LastError != null) throw new InvalidOperationException("Palette worker failed", _worker.LastError);
            if (!updated && _worker.Latest != null && _worker.LatestWidth == width && _worker.LatestHeight == height && _worker.LatestAspect == aspect &&
                (_rawActive == null || _disabled == null || _active == null || !_active.IsCreated())) updated = true;
            if (updated && _rawActive != null && _rawActive.width == width && _rawActive.height == height &&
                _disabled != null && _active != null && _active.IsCreated())
            {
                // Animated colors and geometry must not create three Unity textures on
                // every completed worker result. Reuse the existing same-sized resources.
                Upload(_rawActive, _worker.Latest.Active);
                Upload(_disabled, _worker.Latest.Disabled);
            }
            else if (updated)
            {
                Texture2D raw = null, disabled = null;
                RenderTexture active = null;
                try
                {
                    raw = MakeTexture(width, height, "PEV.RawActivePalette", _worker.Latest.Active);
                    disabled = MakeTexture(width, height, "PEV.DisabledPalette", _worker.Latest.Disabled);
                    var desc = new RenderTextureDescriptor(width, height)
                    {
                        graphicsFormat = GraphicsFormat.R8G8B8A8_UNorm,
                        depthBufferBits = 0, msaaSamples = 1, enableRandomWrite = true,
                        sRGB = false, useMipMap = false, autoGenerateMips = false
                    };
                    active = new RenderTexture(desc) { name = "PEV.ActivePalette", filterMode = FilterMode.Point,
                        wrapMode = TextureWrapMode.Clamp };
                    if (!active.Create()) throw new InvalidOperationException("Could not allocate active palette");
                    ReleaseTextures();
                    _rawActive = raw; _disabled = disabled; _active = active;
                    raw = null; disabled = null; active = null;
                }
                finally
                {
                    if (raw != null) UnityEngine.Object.Destroy(raw);
                    if (disabled != null) UnityEngine.Object.Destroy(disabled);
                    if (active != null) { active.Release(); UnityEngine.Object.Destroy(active); }
                }
            }
            if (_rawActive == null || _rawActive.width != width || _rawActive.height != height) return false;
            masks.WarpColors(_rawActive, _active);
            return true;
        }

        internal static Texture2D MakeTexture(int width, int height, string name, byte[] bytes)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false, true)
            {
                name = name, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp
            };
            try { Upload(texture, bytes); return texture; }
            catch { UnityEngine.Object.Destroy(texture); throw; }
        }

        private static void Upload(Texture2D texture, byte[] bytes)
        {
            texture.LoadRawTextureData(bytes);
            texture.Apply(false, false);
        }

        private void ReleaseTextures()
        {
            if (_rawActive != null) UnityEngine.Object.Destroy(_rawActive);
            if (_disabled != null) UnityEngine.Object.Destroy(_disabled);
            if (_active != null) { _active.Release(); UnityEngine.Object.Destroy(_active); }
            _rawActive = null; _disabled = null; _active = null;
        }

        public void Dispose() { _worker?.Dispose(); _worker = null; ReleaseTextures(); }
    }
}
