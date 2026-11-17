using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PEV.Scripting
{
    // Generic immutable value snapshots for background package vector programs.
    internal sealed class StructuredVectorWorker<TItem, TOptions> : IDisposable
        where TItem : struct where TOptions : struct
    {
        private struct Snapshot { public TItem[] Items; public TOptions Options; }
        private readonly VectorWorker<Snapshot> _worker;
        private readonly int _outputCount;
        private byte[][] _last;
        internal byte[][] Latest { get; private set; }
        internal int LatestWidth => _worker.LatestWidth;
        internal int LatestHeight => _worker.LatestHeight;
        internal float LatestAspect => _worker.LatestAspect;
        internal Exception LastError => _worker.LastError;

        internal StructuredVectorWorker(string program, int outputCount)
        {
            if (outputCount < 1 || outputCount > 8) throw new ArgumentOutOfRangeException(nameof(outputCount));
            PublicFieldsSnapshot.RequireImmutableRecord(typeof(TItem));
            PublicFieldsSnapshot.RequireImmutableRecord(typeof(TOptions));
            _outputCount = outputCount;
            _worker = new VectorWorker<Snapshot>(program,
                data => new Snapshot { Items = (TItem[])data.Items.Clone(), Options = data.Options }, Same, Serialize);
        }
        internal bool Update(int width, int height, float aspect, TItem[] items, TOptions options)
        {
            if (items == null || items.Length > 1024) throw new ArgumentException("Invalid vector snapshot items");
            bool updated = _worker.Update(width, height, aspect, new Snapshot { Items = items, Options = options });
            var buffers = _worker.Latest;
            if (buffers != null && !ReferenceEquals(buffers, _last))
            {
                int expected = checked(_worker.LatestWidth * _worker.LatestHeight * 4);
                if (buffers.Length != _outputCount) throw new InvalidOperationException("Invalid vector output count");
                foreach (var buffer in buffers)
                    if (buffer == null || buffer.Length != expected) throw new InvalidOperationException("Invalid vector output size");
                _last = buffers; Latest = buffers;
            }
            return updated;
        }
        private static bool Same(Snapshot left, Snapshot right)
        {
            if (!EqualityComparer<TOptions>.Default.Equals(left.Options, right.Options) || left.Items.Length != right.Items.Length) return false;
            for (int i = 0; i < left.Items.Length; i++)
                if (!EqualityComparer<TItem>.Default.Equals(left.Items[i], right.Items[i])) return false;
            return true;
        }
        private static string Serialize(int width, int height, float aspect, Snapshot data)
        {
            return JsonConvert.SerializeObject(new { width, height, aspect, request = JObject.Parse(PublicFieldsSnapshot.Serialize(data)) });
        }
        public void Dispose() { _worker.Dispose(); Latest = null; _last = null; }
    }
}
