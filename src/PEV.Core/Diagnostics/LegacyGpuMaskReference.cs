using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using Area = PEV.Scripting.NoiseFieldGpuMasks.Area;
using Touch = PEV.Scripting.NoiseFieldGpuMasks.Touch;

namespace PEV.Scripting
{
    /// <summary>
    /// Draft GPU mask dispatch and ownership for one sorting group. This class is not
    /// connected to the production renderer until its Unity bundle is built and tested.
    /// All methods must run on Unity's main thread.
    /// </summary>
    internal sealed class LegacyGpuMaskReference : IDisposable
    {
        private readonly ComputeShader _shader;
        private readonly float[] _glowWeights;
        private readonly int _build, _compose, _warp, _ring, _export, _touch, _warpColors;
        private RenderTexture _sources, _auxSources, _composeMask, _auxCompose, _effect, _ping, _pong;
        private RenderTexture _effectNormalized, _auxNormalized;
        private ComputeBuffer _areas, _touches;
        private int _capacity, _width, _height;

        internal RenderTexture Effect => _effectNormalized;
        internal RenderTexture Auxiliary => _auxNormalized;
        internal int Width => _width;
        internal int Height => _height;

        internal LegacyGpuMaskReference(ComputeShader shader, float[] glowWeights)
        {
            _shader = shader != null ? shader : throw new ArgumentNullException(nameof(shader));
            _glowWeights = glowWeights != null ? (float[])glowWeights.Clone() : Array.Empty<float>();
            _build = shader.FindKernel("BuildSources");
            _compose = shader.FindKernel("ComposeSources");
            _warp = shader.FindKernel("WarpActive");
            _ring = shader.FindKernel("DilateRing");
            _export = shader.FindKernel("ExportMasks");
            _touch = shader.FindKernel("BuildTouch");
            _warpColors = shader.FindKernel("WarpColors");
        }

        private static RenderTexture Create(int width, int height, GraphicsFormat format, string name)
        {
            if (!SystemInfo.IsFormatSupported(format, FormatUsage.LoadStore))
                throw new NotSupportedException("GPU random-write format unavailable: " + format);
            var descriptor = new RenderTextureDescriptor(width, height)
            {
                graphicsFormat = format,
                depthBufferBits = 0,
                msaaSamples = 1,
                enableRandomWrite = true,
                sRGB = false,
                useMipMap = false,
                autoGenerateMips = false
            };
            var texture = new RenderTexture(descriptor)
            {
                name = name,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            if (!texture.Create())
            {
                UnityEngine.Object.Destroy(texture);
                throw new InvalidOperationException("Could not create GPU mask " + name);
            }
            return texture;
        }

        private void Resize(int viewportWidth, int viewportHeight)
        {
            int width = Math.Max(1, viewportWidth / 8) * 2;
            int height = Math.Max(1, viewportHeight / 8) * 2;
            if (_width == width && _height == height && _effectNormalized != null) return;
            ReleaseTextures();
            try
            {
                _sources = Create(width, height, GraphicsFormat.R32G32B32A32_UInt, "PEV.MaskSources");
                _auxSources = Create(width, height, GraphicsFormat.R32G32B32A32_UInt, "PEV.MaskAuxSources");
                _composeMask = Create(width, height, GraphicsFormat.R32G32B32A32_UInt, "PEV.MaskCompose");
                _auxCompose = Create(width, height, GraphicsFormat.R32G32B32A32_UInt, "PEV.MaskAuxCompose");
                _effect = Create(width, height, GraphicsFormat.R32G32B32A32_UInt, "PEV.MaskEffect");
                _ping = Create(width, height, GraphicsFormat.R32_UInt, "PEV.MaskPing");
                _pong = Create(width, height, GraphicsFormat.R32_UInt, "PEV.MaskPong");
                _effectNormalized = Create(width, height, GraphicsFormat.R8G8B8A8_UNorm, "PEV.MaskNormalized");
                _auxNormalized = Create(width, height, GraphicsFormat.R8G8B8A8_UNorm, "PEV.MaskAuxNormalized");
                // Phira-Pro samples both effect and auxiliary textures with linear filtering;
                // point lookups in the fragment shader explicitly snap to texel centers.
                _effectNormalized.filterMode = FilterMode.Bilinear;
                _auxNormalized.filterMode = FilterMode.Bilinear;
                _width = width;
                _height = height;
            }
            catch { ReleaseTextures(); throw; }
        }

        internal void Render(Area[] areas, int count, int viewportWidth, int viewportHeight,
            float aspect, float songTime, Texture2D noise, Touch[] touches, int touchCount, Texture2D touchTexture)
        {
            if (areas == null || count < 0 || count > areas.Length) throw new ArgumentOutOfRangeException(nameof(count));
            if (touchCount < 0 || touchCount > NoiseFieldTouchHistory.Capacity ||
                (touchCount > 0 && (touches == null || touchCount > touches.Length || touchTexture == null)))
                throw new ArgumentOutOfRangeException(nameof(touchCount));
            if (viewportWidth <= 0 || viewportHeight <= 0 || aspect <= 0 ||
                float.IsNaN(aspect) || float.IsInfinity(aspect) || float.IsNaN(songTime) || float.IsInfinity(songTime))
                throw new ArgumentOutOfRangeException("Invalid viewport, aspect or song time");
            if (noise == null || noise.width <= 0 || noise.height <= 0) throw new ArgumentNullException(nameof(noise));
            Resize(viewportWidth, viewportHeight);
            if (_areas == null || _capacity < Math.Max(1, count))
            {
                int capacity = Math.Max(1, count);
                // Keep the current buffer valid if allocation fails; disposal/fallback
                // must not see an already released buffer after an exception.
                var replacement = new ComputeBuffer(capacity, Marshal.SizeOf(typeof(Area)));
                var previous = _areas;
                _areas = replacement;
                _capacity = capacity;
                previous?.Release();
            }
            if (count > 0) _areas.SetData(areas, 0, 0, count);
            _shader.SetInts("_Dimensions", _width, _height);
            _shader.SetInts("_NoiseDimensions", noise.width, noise.height);
            _shader.SetInt("_AreaCount", count);
            _shader.SetFloat("_Aspect", aspect);
            _shader.SetFloat("_TimeSeconds", songTime);
            _shader.SetBuffer(_build, "_Areas", _areas);
            _shader.SetTexture(_build, "_Sources", _sources);
            _shader.SetTexture(_build, "_AuxSources", _auxSources);
            Dispatch(_build);
            _shader.SetTexture(_compose, "_Sources", _sources);
            _shader.SetTexture(_compose, "_AuxSources", _auxSources);
            _shader.SetTexture(_compose, "_Compose", _composeMask);
            _shader.SetTexture(_compose, "_AuxCompose", _auxCompose);
            Dispatch(_compose);
            if (touchCount > 0)
            {
                if (_touches == null) _touches = new ComputeBuffer(NoiseFieldTouchHistory.Capacity, Marshal.SizeOf(typeof(Touch)));
                _touches.SetData(touches, 0, 0, touchCount);
                _shader.SetInt("_TouchCount", touchCount);
                _shader.SetFloat("_ScreenAspect", (float)viewportWidth / viewportHeight);
                _shader.SetInts("_TouchTextureDimensions", touchTexture.width, touchTexture.height);
                _shader.SetBuffer(_touch, "_Touches", _touches);
                _shader.SetTexture(_touch, "_TouchTexture", touchTexture);
                _shader.SetTexture(_touch, "_AuxCompose", _auxCompose);
                Dispatch(_touch);
            }
            _shader.SetTexture(_warp, "_NoiseTexture", noise);
            _shader.SetTexture(_warp, "_Compose", _composeMask);
            _shader.SetTexture(_warp, "_Effect", _effect);
            _shader.SetTexture(_warp, "_RingOutput", _ping);
            Dispatch(_warp);

            for (int pass = 0; pass < _glowWeights.Length; pass++)
            {
                _shader.SetFloat("_GlowWeight", _glowWeights[pass]);
                _shader.SetInt("_RingPass", pass);
                _shader.SetTexture(_ring, "_Effect", _effect);
                _shader.SetTexture(_ring, "_RingInput", _ping);
                _shader.SetTexture(_ring, "_RingOutput", _pong);
                Dispatch(_ring);
                var swap = _ping; _ping = _pong; _pong = swap;
            }
            _shader.SetTexture(_export, "_Effect", _effect);
            _shader.SetTexture(_export, "_AuxCompose", _auxCompose);
            _shader.SetTexture(_export, "_EffectNormalized", _effectNormalized);
            _shader.SetTexture(_export, "_AuxNormalized", _auxNormalized);
            Dispatch(_export);
        }

        private void Dispatch(int kernel) => _shader.Dispatch(kernel, (_width + 7) / 8, (_height + 7) / 8, 1);

        internal void WarpColors(Texture2D raw, RenderTexture output, Texture2D noise)
        {
            if (raw == null || output == null || noise == null || raw.width != _width || raw.height != _height ||
                output.width != _width || output.height != _height)
                throw new ArgumentException("Palette textures must match the current mask size");
            // Render() set dimensions, clock and noise dimensions for this frame.
            _shader.SetTexture(_warpColors, "_NoiseTexture", noise);
            _shader.SetTexture(_warpColors, "_RawActiveColors", raw);
            _shader.SetTexture(_warpColors, "_WarpedActiveColors", output);
            Dispatch(_warpColors);
        }

        private static void Release(ref RenderTexture texture)
        {
            if (texture == null) return;
            texture.Release();
            UnityEngine.Object.Destroy(texture);
            texture = null;
        }

        private void ReleaseTextures()
        {
            Release(ref _sources); Release(ref _auxSources); Release(ref _composeMask); Release(ref _auxCompose);
            Release(ref _effect); Release(ref _ping); Release(ref _pong); Release(ref _effectNormalized); Release(ref _auxNormalized);
            _width = _height = 0;
        }

        public void Dispose()
        {
            ReleaseTextures();
            _areas?.Release();
            _touches?.Release();
            _areas = null;
            _touches = null;
            _capacity = 0;
        }
    }
}