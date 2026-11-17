using System;
using System.Runtime.InteropServices;

namespace PEV.Scripting
{
    // Bounded, reusable structured-buffer records. The caller chooses every field and
    // its offset; this class knows neither the shader layout nor event semantics.
    internal sealed partial class GpuRecordBuffer
    {
        [StructLayout(LayoutKind.Explicit)]
        private struct Bits
        {
            [FieldOffset(0)] internal float Float;
            [FieldOffset(0)] internal uint UInt;
        }
        private readonly int _stride;
        private byte[] _bytes;
        private Array _records;
        private int _count;

        internal GpuRecordBuffer(int stride)
        {
            if (stride < 4 || stride > 256 || stride % 4 != 0) throw new ArgumentOutOfRangeException(nameof(stride));
            _stride = stride;
        }
        internal void Prepare(int count)
        {
            if (count < 0 || count > 65536 || (long)Math.Max(1, count) * _stride > 16 * 1024 * 1024)
                throw new ArgumentOutOfRangeException(nameof(count));
            int required = Math.Max(1, count);
            if (_records == null || _records.Length < required)
            {
                int capacity = 1;
                while (capacity < required) capacity *= 2;
                _records = Array.CreateInstance(Types[_stride / 4 - 1], capacity);
                _bytes = new byte[capacity * _stride];
            }
            _count = count;
        }
        internal void SetFloat(int row, int word, float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) throw new ArgumentException("Non-finite buffer value");
            var bits = new Bits { Float = value };
            SetUInt(row, word, bits.UInt);
        }
        internal void SetUInt(int row, int word, uint value)
        {
            if (_bytes == null || row < 0 || row >= _count || word < 0 || word >= _stride / 4)
                throw new ArgumentOutOfRangeException("Invalid buffer field");
            int i = row * _stride + word * 4;
            _bytes[i] = (byte)value; _bytes[i + 1] = (byte)(value >> 8);
            _bytes[i + 2] = (byte)(value >> 16); _bytes[i + 3] = (byte)(value >> 24);
        }
        internal void Upload(GpuComputeGraph graph, string name)
        {
            if (graph == null || _records == null) throw new InvalidOperationException("Buffer is not prepared");
            if (_count != 0)
            {
                var pinned = GCHandle.Alloc(_records, GCHandleType.Pinned);
                try { Marshal.Copy(_bytes, 0, pinned.AddrOfPinnedObject(), _count * _stride); }
                finally { pinned.Free(); }
            }
            graph.Upload(name, _records, _count);
        }
    }
}
