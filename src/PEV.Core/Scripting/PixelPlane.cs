using System;

namespace PEV.Scripting
{
    // Immutable dimensions and sampling configuration around a host-owned float plane.
    // This object is never exposed to scripts.
    internal sealed class PixelPlane
    {
        internal readonly int Width, Height, Channels;
        internal readonly float[] Data;
        private readonly bool _linear;
        private readonly int _wrap; // 0: clamp, 1: repeat, 2: mirror

        internal PixelPlane(int width, int height, int channels, float[] data, string filter, string wrap)
        {
            if (width < 1 || height < 1 || width > 4096 || height > 4096 || channels < 1 || channels > 4 ||
                (long)width * height > 1048576 || data == null || data.LongLength != (long)width * height * channels ||
                (filter != "point" && filter != "linear") || (wrap != "clamp" && wrap != "repeat" && wrap != "mirror"))
                throw new ArgumentException("Invalid pixel plane");
            Width = width; Height = height; Channels = channels; Data = data; _linear = filter == "linear";
            _wrap = wrap == "repeat" ? 1 : wrap == "mirror" ? 2 : 0;
        }
        private float Wrap(float coordinate)
        {
            if (float.IsNaN(coordinate) || float.IsInfinity(coordinate)) return 0f;
            if (_wrap == 0) return Math.Max(0f, Math.Min(1f, coordinate));
            // Keep the integer conversion inside a precisely representable range.
            double value = coordinate;
            double tile = Math.Floor(value);
            float fraction = (float)(value - tile);
            if (_wrap == 2 && Math.Abs(tile % 2d) == 1d) fraction = 1f - fraction;
            return fraction;
        }
        private int Index(int x, int y, int channel)
        {
            if (_wrap == 1)
            {
                x = ((x % Width) + Width) % Width;
                y = ((y % Height) + Height) % Height;
            }
            else { x = Math.Max(0, Math.Min(Width - 1, x)); y = Math.Max(0, Math.Min(Height - 1, y)); }
            return (y * Width + x) * Channels + channel;
        }
        internal float Sample(float u, float v, int channel)
        {
            u = Wrap(u); v = Wrap(v);
            if (!_linear)
            {
                int px = (int)(u * Width), py = (int)(v * Height);
                if (px >= Width) px = Width - 1;
                if (py >= Height) py = Height - 1;
                return Data[(py * Width + px) * Channels + channel];
            }
            float x = u * Width - .5f, y = v * Height - .5f;
            int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
            float fx = x - x0, fy = y - y0;
            float a = Data[Index(x0, y0, channel)], b = Data[Index(x0 + 1, y0, channel)];
            float c = Data[Index(x0, y0 + 1, channel)], d = Data[Index(x0 + 1, y0 + 1, channel)];
            float first = a + (b - a) * fx, second = c + (d - c) * fx;
            return first + (second - first) * fy;
        }
    }
}
