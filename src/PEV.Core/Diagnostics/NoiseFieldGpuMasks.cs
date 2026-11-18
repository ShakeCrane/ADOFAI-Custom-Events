using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace PEV.Scripting
{
    // Transitional adapter: the dispatch sequence and resources live in the package graph.
    internal sealed class NoiseFieldGpuMasks : IComputeFrame, IDisposable
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
        private readonly GpuRecordBuffer _geometryRecords, _contactRecords;
        private readonly int[] _geometryFields, _contactFields;
        private readonly Dictionary<string, object> _inputs = new Dictionary<string, object>();
        private readonly Dictionary<string, Texture> _textures = new Dictionary<string, Texture>();
        private readonly int[] _dimensions = new int[2];
        private readonly Dictionary<string, int[]> _imageDimensions = new Dictionary<string, int[]>();
#if PEV_DIAGNOSTICS
        internal RenderTexture Effect => _graph.Output("main");
        internal RenderTexture Auxiliary => _graph.Output("auxiliary");
#endif
        public IEnumerable<KeyValuePair<string, RenderTexture>> Outputs => _graph.Outputs;
        public int Width => _graph.Width;
        public int Height => _graph.Height;
        internal NoiseFieldGpuMasks(ComputeShader shader, GpuGraphDefinition definition)
        {
            if (!definition.Resources.TryGetValue("geometry", out var geometry) || !geometry.Buffer ||
                !definition.Resources.TryGetValue("contacts", out var contacts) || !contacts.Buffer)
                throw new ArgumentException("Required compute buffers are missing");
            _geometryFields = new[] { geometry.Field("centerX", "float"), geometry.Field("centerY", "float"),
                geometry.Field("halfX", "float"), geometry.Field("halfY", "float"), geometry.Field("angle", "float"),
                geometry.Field("yScale", "float"), geometry.Field("opacity", "float"), geometry.Field("flags", "uint") };
            _contactFields = new[] { contacts.Field("x", "float"), contacts.Field("y", "float"),
                contacts.Field("scale", "float"), contacts.Field("padding", "float") };
            _geometryRecords = new GpuRecordBuffer(geometry.Stride);
            _contactRecords = new GpuRecordBuffer(contacts.Stride);
            _graph = new GpuComputeGraph(shader, definition);
        }
        internal void Render(Area[] areas, int count, int viewportWidth, int viewportHeight,
            float aspect, float songTime, IReadOnlyDictionary<string, Texture2D> images, Touch[] touches, int touchCount)
        {
            if (areas == null || count < 0 || count > areas.Length) throw new ArgumentOutOfRangeException(nameof(count));
            if (touchCount < 0 || touchCount > 64 ||
                (touchCount > 0 && (touches == null || touchCount > touches.Length)))
                throw new ArgumentOutOfRangeException(nameof(touchCount));
            if (viewportWidth <= 0 || viewportHeight <= 0 || aspect <= 0 || float.IsNaN(aspect) ||
                float.IsInfinity(aspect) || float.IsNaN(songTime) || float.IsInfinity(songTime))
                throw new ArgumentOutOfRangeException("Invalid viewport, aspect or song time");
            if (images == null) throw new ArgumentNullException(nameof(images));
            _graph.Resize(viewportWidth, viewportHeight);
            _geometryRecords.Prepare(count);
            for (int i = 0; i < count; i++)
            {
                var a = areas[i];
                _geometryRecords.SetFloat(i, _geometryFields[0], a.Center.x); _geometryRecords.SetFloat(i, _geometryFields[1], a.Center.y);
                _geometryRecords.SetFloat(i, _geometryFields[2], a.HalfSize.x); _geometryRecords.SetFloat(i, _geometryFields[3], a.HalfSize.y);
                _geometryRecords.SetFloat(i, _geometryFields[4], a.AngleRadians); _geometryRecords.SetFloat(i, _geometryFields[5], a.YScale);
                _geometryRecords.SetFloat(i, _geometryFields[6], a.Opacity); _geometryRecords.SetUInt(i, _geometryFields[7], a.Flags);
            }
            _geometryRecords.Upload(_graph, "geometry");
            if (touchCount > 0)
            {
                _contactRecords.Prepare(touchCount);
                for (int i = 0; i < touchCount; i++)
                {
                    var t = touches[i];
                    _contactRecords.SetFloat(i, _contactFields[0], t.Center.x); _contactRecords.SetFloat(i, _contactFields[1], t.Center.y);
                    _contactRecords.SetFloat(i, _contactFields[2], t.Scale); _contactRecords.SetFloat(i, _contactFields[3], t.Padding);
                }
                _contactRecords.Upload(_graph, "contacts");
            }
            _dimensions[0] = Width; _dimensions[1] = Height;
            _inputs["dimensions"] = _dimensions;
            _inputs["geometryCount"] = count; _inputs["aspect"] = aspect; _inputs["time"] = songTime;
            _inputs["contactCount"] = touchCount; _inputs["screenAspect"] = (float)viewportWidth / viewportHeight;
            _inputs["hasContacts"] = touchCount > 0;
            _inputs["coastScaleX"] = 2f; _inputs["coastScaleY"] = 2f;
            _inputs["ringStep"] = 1f; _inputs["touchOpacity"] = 2f;
            foreach (var image in images)
            {
                if (image.Value == null || image.Value.width <= 0 || image.Value.height <= 0) throw new ArgumentException("Invalid package image");
                if (!_imageDimensions.TryGetValue(image.Key, out var size)) _imageDimensions.Add(image.Key, size = new int[2]);
                size[0] = image.Value.width; size[1] = image.Value.height;
                _inputs[image.Key + "Dimensions"] = size; _textures[image.Key] = image.Value;
            }
            _graph.Run("render", _inputs, _textures);
        }
        public void RunExternal(string entry, string inputName, Texture input, string outputName, RenderTexture output)
        {
            if (input == null || output == null || input.width != Width || input.height != Height ||
                output.width != Width || output.height != Height)
                throw new ArgumentException("External textures must match the compute graph size");
            _textures[inputName] = input; _textures[outputName] = output;
            _graph.Run(entry, _inputs, _textures);
        }
        public void Dispose() => _graph.Dispose();
    }
}
