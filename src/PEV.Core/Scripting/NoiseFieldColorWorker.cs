using System;
using System.Linq;
using Newtonsoft.Json;

namespace PEV.Scripting
{
    /// <summary>
    /// Transitional adapter from field geometry to the generic sandboxed vector worker.
    /// The palette algorithm and its constants are supplied by the event package.
    /// </summary>
    internal sealed class NoiseFieldColorWorker : IDisposable
    {
        private struct Snapshot
        {
            internal NoiseFieldColorOwnership.Zone[] Zones;
            internal int DefaultRgb;
        }
        private readonly VectorWorker<Snapshot> _worker;
        private byte[][] _lastBuffers;
        internal NoiseFieldColorOwnership.Result Latest { get; private set; }
        internal int LatestWidth => _worker.LatestWidth;
        internal int LatestHeight => _worker.LatestHeight;
        internal float LatestAspect => _worker.LatestAspect;
        internal Exception LastError => _worker.LastError;

        internal NoiseFieldColorWorker(string program)
        {
            _worker = new VectorWorker<Snapshot>(program,
                s => new Snapshot { Zones = (NoiseFieldColorOwnership.Zone[])s.Zones.Clone(), DefaultRgb = s.DefaultRgb },
                Same, Serialize);
        }

        internal bool Update(int width, int height, float aspect, NoiseFieldColorOwnership.Zone[] zones, int defaultRgb)
        {
            if (zones == null) throw new ArgumentNullException(nameof(zones));
            bool updated = _worker.Update(width, height, aspect, new Snapshot { Zones = zones, DefaultRgb = defaultRgb });
            var buffers = _worker.Latest;
            if (buffers != null && !ReferenceEquals(buffers, _lastBuffers))
            {
                int expected = checked(_worker.LatestWidth * _worker.LatestHeight * 4);
                if (buffers.Length != 2 || buffers[0] == null || buffers[1] == null ||
                    buffers[0].Length != expected || buffers[1].Length != expected)
                    throw new InvalidOperationException("vector program returned invalid field buffers");
                _lastBuffers = buffers;
                Latest = new NoiseFieldColorOwnership.Result { Active = buffers[0], Disabled = buffers[1] };
            }
            return updated;
        }

        private static bool Same(Snapshot a, Snapshot b)
        {
            if (a.DefaultRgb != b.DefaultRgb || a.Zones.Length != b.Zones.Length) return false;
            for (int i = 0; i < a.Zones.Length; i++)
            {
                var x = a.Zones[i]; var y = b.Zones[i];
                if (x.CenterX != y.CenterX || x.CenterY != y.CenterY || x.HalfX != y.HalfX || x.HalfY != y.HalfY ||
                    x.Angle != y.Angle || x.YScale != y.YScale || x.Opacity != y.Opacity || x.Red != y.Red ||
                    x.Green != y.Green || x.Blue != y.Blue || x.Active != y.Active || x.Inverted != y.Inverted) return false;
            }
            return true;
        }

        private static string Serialize(int width, int height, float aspect, Snapshot data)
        {
            var zones = data.Zones.Select(z => new {
                centerX = z.CenterX, centerY = z.CenterY, halfX = z.HalfX, halfY = z.HalfY,
                angle = z.Angle, yScale = z.YScale, opacity = z.Opacity,
                red = z.Red, green = z.Green, blue = z.Blue, active = z.Active, inverted = z.Inverted
            }).ToArray();
            return JsonConvert.SerializeObject(new { width, height, aspect, defaultRgb = data.DefaultRgb, zones });
        }

        public void Dispose() { _worker.Dispose(); Latest = null; _lastBuffers = null; }
    }
}
