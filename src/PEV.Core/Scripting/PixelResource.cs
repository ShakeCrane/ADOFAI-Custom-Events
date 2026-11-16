using System;
using System.Threading;

namespace PEV.Scripting
{
    // A host-decoded immutable image snapshot; event scripts receive only bounded handles.
    internal sealed class PixelResource
    {
        internal readonly int Width, Height, Channels;
        private readonly float[] _values;
        internal int SizeBytes => _values.Length * sizeof(float);
        internal PixelResource(int width, int height, int channels, byte[] data, CancellationToken cancellation = default)
        {
            if (width < 1 || height < 1 || width > 4096 || height > 4096 || channels < 1 || channels > 4 ||
                (long)width * height > 1048576 || data == null || data.LongLength != (long)width * height * channels)
                throw new ArgumentException("Invalid pixel resource");
            Width = width; Height = height; Channels = channels;
            _values = new float[data.Length];
            for (int i = 0; i < data.Length; i++)
            {
                if ((i & 16383) == 0) cancellation.ThrowIfCancellationRequested();
                _values[i] = data[i] / 255f;
            }
        }
        internal PixelPlane Plane(string filter, string wrap) => new PixelPlane(Width, Height, Channels, _values, filter, wrap);
    }
}
