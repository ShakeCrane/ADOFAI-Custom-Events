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
        private readonly int[] _dimensions = new int[2];
        private readonly Dictionary<string, int[]> _imageDimensions = new Dictionary<string, int[]>();
#if PEV_DIAGNOSTICS
        internal RenderTexture Effect => _graph.Output("main");
        internal RenderTexture Auxiliary => _graph.Output("auxiliary");
#endif
        internal IEnumerable<KeyValuePair<string, RenderTexture>> Outputs => _graph.Outputs;
        internal int Width => _graph.Width;
        internal int Height => _graph.Height;
        internal NoiseFieldGpuMasks(ComputeShader shader, GpuGraphDefinition definition)
        {
            _graph = new GpuComputeGraph(shader, definition);
        }
        internal void Render(Area[] areas, int count, int viewportWidth, int viewportHeight,
            float aspect, float songTime, IReadOnlyDictionary<string, Texture2D> images, Touch[] touches, int touchCount)
        {
            if (areas == null || count < 0 || count > areas.Length) throw new ArgumentOutOfRangeException(nameof(count));
            if (touchCount < 0 || touchCount > FieldContactController.Capacity ||
                (touchCount > 0 && (touches == null || touchCount > touches.Length)))
                throw new ArgumentOutOfRangeException(nameof(touchCount));
            if (viewportWidth <= 0 || viewportHeight <= 0 || aspect <= 0 || float.IsNaN(aspect) ||
                float.IsInfinity(aspect) || float.IsNaN(songTime) || float.IsInfinity(songTime))
                throw new ArgumentOutOfRangeException("Invalid viewport, aspect or song time");
            if (images == null) throw new ArgumentNullException(nameof(images));
            _graph.Resize(viewportWidth, viewportHeight);
            _graph.Upload("geometry", areas, count);
            if (touchCount > 0) _graph.Upload("contacts", touches, touchCount);
            _dimensions[0] = Width; _dimensions[1] = Height;
            _inputs["dimensions"] = _dimensions;
            _inputs["geometryCount"] = count; _inputs["aspect"] = aspect; _inputs["time"] = songTime;
            _inputs["contactCount"] = touchCount; _inputs["screenAspect"] = (float)viewportWidth / viewportHeight;
            _inputs["hasContacts"] = touchCount > 0;
            foreach (var image in images)
            {
                if (image.Value == null || image.Value.width <= 0 || image.Value.height <= 0) throw new ArgumentException("Invalid package image");
                if (!_imageDimensions.TryGetValue(image.Key, out var size)) _imageDimensions.Add(image.Key, size = new int[2]);
                size[0] = image.Value.width; size[1] = image.Value.height;
                _inputs[image.Key + "Dimensions"] = size; _textures[image.Key] = image.Value;
            }
            _graph.Run("render", _inputs, _textures);
        }
        internal void WarpColors(Texture2D raw, RenderTexture output)
        {
            if (raw == null || output == null || raw.width != Width || raw.height != Height ||
                output.width != Width || output.height != Height)
                throw new ArgumentException("Palette textures must match the current mask size");
            _textures["inputColors"] = raw; _textures["outputColors"] = output;
            _graph.Run("colors", _inputs, _textures);
        }
        public void Dispose() => _graph.Dispose();
    }
}
