using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace PEV.Scripting
{
    // Transitional adapter: the dispatch sequence and resources live in the package graph.
    internal sealed class NoiseFieldGpuMasks : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct Area
        {
            public Vector2 Center, HalfSize;
            public float AngleRadians, YScale, Opacity;
            public uint Flags;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct Touch { public Vector2 Center; public float Scale, Padding; }
        private readonly GpuComputeGraph _graph;
        private readonly Dictionary<string, object> _inputs = new Dictionary<string, object>();
        private readonly Dictionary<string, Texture> _textures = new Dictionary<string, Texture>();
        private readonly int[] _dimensions = new int[2], _noiseDimensions = new int[2], _contactDimensions = new int[2];
        internal RenderTexture Effect => _graph.Output("main");
        internal RenderTexture Auxiliary => _graph.Output("auxiliary");
        internal int Width => _graph.Width;
        internal int Height => _graph.Height;
        internal NoiseFieldGpuMasks(ComputeShader shader, GpuGraphDefinition definition)
        {
            _graph = new GpuComputeGraph(shader, definition);
        }
        internal void Render(Area[] areas, int count, int viewportWidth, int viewportHeight,
            float aspect, float songTime, Texture2D noise, Touch[] touches, int touchCount, Texture2D touchTexture)
        {
            if (areas == null || count < 0 || count > areas.Length) throw new ArgumentOutOfRangeException(nameof(count));
            if (touchCount < 0 || touchCount > FieldContactController.Capacity ||
                (touchCount > 0 && (touches == null || touchCount > touches.Length || touchTexture == null)))
                throw new ArgumentOutOfRangeException(nameof(touchCount));
            if (viewportWidth <= 0 || viewportHeight <= 0 || aspect <= 0 || float.IsNaN(aspect) ||
                float.IsInfinity(aspect) || float.IsNaN(songTime) || float.IsInfinity(songTime))
                throw new ArgumentOutOfRangeException("Invalid viewport, aspect or song time");
            if (noise == null || noise.width <= 0 || noise.height <= 0) throw new ArgumentNullException(nameof(noise));
            _graph.Resize(viewportWidth, viewportHeight);
            _graph.Upload("geometry", areas, count);
            if (touchCount > 0) _graph.Upload("contacts", touches, touchCount);
            _dimensions[0] = Width; _dimensions[1] = Height;
            _noiseDimensions[0] = noise.width; _noiseDimensions[1] = noise.height;
            _contactDimensions[0] = touchTexture != null ? touchTexture.width : 0;
            _contactDimensions[1] = touchTexture != null ? touchTexture.height : 0;
            _inputs["dimensions"] = _dimensions; _inputs["noiseDimensions"] = _noiseDimensions;
            _inputs["geometryCount"] = count; _inputs["aspect"] = aspect; _inputs["time"] = songTime;
            _inputs["contactCount"] = touchCount; _inputs["screenAspect"] = (float)viewportWidth / viewportHeight;
            _inputs["contactDimensions"] = _contactDimensions; _inputs["hasContacts"] = touchCount > 0;
            _textures["noise"] = noise;
            if (touchTexture != null) _textures["contactTexture"] = touchTexture;
            _graph.Run("render", _inputs, _textures);
        }
        internal void WarpColors(Texture2D raw, RenderTexture output, Texture2D noise)
        {
            if (raw == null || output == null || noise == null || raw.width != Width || raw.height != Height ||
                output.width != Width || output.height != Height)
                throw new ArgumentException("Palette textures must match the current mask size");
            _textures["noise"] = noise; _textures["inputColors"] = raw; _textures["outputColors"] = output;
            _graph.Run("colors", _inputs, _textures);
        }
        public void Dispose() => _graph.Dispose();
    }
}
