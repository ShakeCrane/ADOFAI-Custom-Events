using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PEV.Scripting
{
    /// <summary>
    /// Bounded, package-neutral image operations for isolated background scripts.
    /// Handles are scoped to one job; no Unity object or CLR object is exposed to a script.
    /// </summary>
    internal sealed class VectorCompute
    {
        private const int MaxBuffers = 64, MaxOutputs = 8, MaxCommands = 64;
        private const long MaxBytes = 96L * 1024 * 1024, MaxWork = 50000000;
        private sealed class Surface { internal byte[] Values; internal int[] Owners; }
        private sealed class Seeds { internal int[] Indices, Owners; }
        private sealed class ResultBuffer { internal byte[] Bytes; }
        private readonly List<object> _buffers = new List<object>();
        private readonly Stack<Surface> _surfaces = new Stack<Surface>();
        private readonly Stack<Seeds> _seeds = new Stack<Seeds>();
        private readonly Stack<byte[]> _bytes = new Stack<byte[]>();
        private readonly byte[][][] _outputs = { new byte[MaxOutputs][], new byte[MaxOutputs][] };
        private readonly Dictionary<string, VectorExpression.Program> _programs = new Dictionary<string, VectorExpression.Program>(StringComparer.Ordinal);
        private readonly int _width, _height, _count;
        private readonly float _aspect;
        private readonly Action _checkpoint;
        private int _generation, _outputSlot = 1, _outputIndex, _commands;
        private long _allocated, _work, _deadline;
        private CancellationToken _cancellation;

        internal VectorCompute(int width, int height, float aspect)
        {
            if (width < 1 || height < 1 || width > 4096 || height > 4096 ||
                (long)width * height > 1048576 || !Finite(aspect) || aspect <= 0f || aspect > 16f)
                throw new ArgumentOutOfRangeException("invalid vector dimensions or aspect");
            _width = width; _height = height; _count = width * height; _aspect = aspect;
            _checkpoint = Checkpoint;
        }

        internal void Begin(CancellationToken cancellation)
        {
            foreach (var buffer in _buffers)
            {
                if (buffer is Surface surface) _surfaces.Push(surface);
                else if (buffer is Seeds seeds) _seeds.Push(seeds);
                else if (buffer is byte[] bytes) _bytes.Push(bytes);
            }
            _buffers.Clear();
            _generation = _generation >= (int.MaxValue / MaxBuffers - 1) ? 1 : _generation + 1;
            _outputSlot = 1 - _outputSlot; _outputIndex = 0; _commands = 0; _work = 0;
            _cancellation = cancellation;
            _deadline = Stopwatch.GetTimestamp() + 2 * Stopwatch.Frequency;
            Checkpoint();
        }

        private void Checkpoint()
        {
            _cancellation.ThrowIfCancellationRequested();
            if (Stopwatch.GetTimestamp() > _deadline) throw new TimeoutException("vector job exceeded its time limit");
        }
        private void Charge(long work)
        {
            if (work < 0 || _work > MaxWork - work) throw new InvalidOperationException("vector job exceeded its work limit");
            _work += work; Checkpoint();
        }
        private void Reserve(long size)
        {
            if (size < 0 || _allocated > MaxBytes - size) throw new InvalidOperationException("vector worker exceeded its memory limit");
            _allocated += size;
        }
        private int Put(object value)
        {
            if (_buffers.Count >= MaxBuffers) throw new InvalidOperationException("too many vector buffers");
            int handle = checked(_generation * MaxBuffers + _buffers.Count);
            _buffers.Add(value);
            return handle;
        }
        private T Get<T>(int handle) where T : class
        {
            int generation = handle / MaxBuffers, index = handle % MaxBuffers;
            if (handle < 0 || generation != _generation || index >= _buffers.Count || !(_buffers[index] is T result))
                throw new ArgumentException("invalid or expired vector buffer");
            return result;
        }
        internal byte[] Result(int handle) => Get<ResultBuffer>(handle).Bytes;
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static int Integer(JToken obj, string name, int min, int max)
        {
            var value = obj?[name];
            if (value == null || value.Type != JTokenType.Integer) throw new ArgumentException("invalid vector integer " + name);
            long number = value.Value<long>();
            if (number < min || number > max) throw new ArgumentOutOfRangeException(name);
            return (int)number;
        }
        private static float Number(JToken obj, string name, float min, float max)
        {
            var value = obj?[name];
            if (value == null || (value.Type != JTokenType.Float && value.Type != JTokenType.Integer))
                throw new ArgumentException("invalid vector number " + name);
            float number = value.Value<float>();
            if (!Finite(number) || number < min || number > max) throw new ArgumentOutOfRangeException(name);
            return number;
        }
        private static byte Unorm(float x)
        {
            float v = Math.Max(0f, Math.Min(1f, x)) * 255f;
            int n = (int)v;
            return (byte)(n + (v - n >= 0.5f ? 1 : 0));
        }
        private static int Bound(int value, int max) => Math.Max(0, Math.Min(value, max));
        private static void Clip(float a, float b, float half, float center, ref float lo, ref float hi)
        {
            if (Math.Abs(a) < 1e-7f) { if (Math.Abs(b) > half) hi = lo - 1f; return; }
            float x = (-half - b) / a + center, y = (half - b) / a + center;
            lo = Math.Max(lo, Math.Min(x, y)); hi = Math.Min(hi, Math.Max(x, y));
        }

        private int Raster(JToken shapes, string blend)
        {
            if (!(shapes is JArray list) || list.Count > 256 || (blend != "add" && blend != "set" && blend != "max"))
                throw new ArgumentException("invalid raster command");
            Charge((long)_count * Math.Max(1, list.Count));
            Surface result;
            if (_surfaces.Count != 0) result = _surfaces.Pop();
            else { Reserve((long)_count * 5); result = new Surface { Values = new byte[_count], Owners = new int[_count] }; }
            Array.Clear(result.Values, 0, _count);
            for (int i = 0; i < _count; i++) result.Owners[i] = -1;
            foreach (var shape in list)
            {
                Checkpoint();
                float cx = Number(shape, "x", -32f, 32f), cy = Number(shape, "y", -32f, 32f);
                float hx = Number(shape, "hx", 0f, 32f), hy = Number(shape, "hy", 0f, 32f);
                float scale = Number(shape, "scale", 0f, 16f), angle = Number(shape, "angle", -1000f, 1000f);
                float value = Number(shape, "value", 0f, 1f);
                int owner = Integer(shape, "owner", 0, 255);
                if (!(value > 0f) || !(hx > 0f) || !(hy > 0f) || !(scale > 0f)) continue;
                float s = (float)Math.Sin(angle), c = (float)Math.Cos(angle);
                float yHalf = (Math.Abs(s) * hx + Math.Abs(c) * hy) * scale;
                int start = Bound((int)Math.Ceiling(((cy - yHalf) * _aspect + 1f) * _height * .5f - .5f), _height);
                int end = Bound((int)Math.Floor(((cy + yHalf) * _aspect + 1f) * _height * .5f - .5f) + 1, _height);
                for (int y = start; y < end; y++)
                {
                    if ((y & 255) == 0) Checkpoint();
                    float dy = (((y + .5f) * 2f / _height - 1f) / _aspect - cy) / scale, lo = -1f, hi = 1f;
                    Clip(c, s * dy, hx, cx, ref lo, ref hi); Clip(-s, c * dy, hy, cx, ref lo, ref hi);
                    if (lo > hi) continue;
                    int first = Bound((int)Math.Ceiling((lo + 1f) * _width * .5f - .5f), _width);
                    int last = Bound((int)Math.Floor((hi + 1f) * _width * .5f - .5f) + 1, _width);
                    for (int i = y * _width + first; i < y * _width + last; i++)
                    {
                        byte next = blend == "add" ? Unorm(result.Values[i] / 255f + value) : Unorm(value);
                        if (blend == "max" && next < result.Values[i]) continue;
                        result.Values[i] = next; result.Owners[i] = owner;
                    }
                }
            }
            return Put(result);
        }

        private int Evaluate(JToken source, JToken inputs)
        {
            if (!(source is JArray program) || !(inputs is JArray inputList) || inputList.Count < 1 || inputList.Count > 16)
                throw new ArgumentException("invalid vector expression");
            Charge((long)_count * program.Count);
            var data = new byte[inputList.Count][];
            for (int i = 0; i < inputList.Count; i++)
            {
                if (inputList[i].Type != JTokenType.Integer) throw new ArgumentException("invalid vector input handle");
                data[i] = Get<Surface>(inputList[i].Value<int>()).Values;
            }
            string key = program.ToString(Formatting.None) + "/" + data.Length;
            if (!_programs.TryGetValue(key, out var evaluator))
            {
                if (_programs.Count >= 32) throw new InvalidOperationException("too many vector expressions");
                _programs.Add(key, evaluator = VectorExpression.Compile(program, data.Length));
            }
            byte[] output;
            if (_bytes.Count != 0) output = _bytes.Pop();
            else { Reserve(_count); output = new byte[_count]; }
            evaluator(data, output, _count, _checkpoint);
            return Put(output);
        }

        private int DifferenceSeeds(int firstHandle, int secondHandle, int valueHandle)
        {
            Charge(_count);
            var first = Get<Surface>(firstHandle); var second = Get<Surface>(secondHandle); var value = Get<byte[]>(valueHandle);
            Seeds result;
            if (_seeds.Count != 0) result = _seeds.Pop();
            else { Reserve((long)_count * 8); result = new Seeds { Indices = new int[_count], Owners = new int[_count] }; }
            for (int i = 0; i < _count; i++)
            {
                if ((i & 4095) == 0) Checkpoint();
                result.Indices[i] = -1; result.Owners[i] = -1;
                if (first.Values[i] == value[i]) continue;
                int owner = first.Values[i] < value[i] ? second.Owners[i] : first.Owners[i];
                if (owner >= 0) { result.Indices[i] = i; result.Owners[i] = owner; }
            }
            return Put(result);
        }
        private long Distance(int seed, int x, int y)
        {
            if (seed < 0) return long.MaxValue;
            long dx = x - seed % _width, dy = y - seed / _width;
            return dx * dx + dy * dy;
        }
        private int Propagate(int handle, JToken passes)
        {
            var seeds = Get<Seeds>(handle);
            if (!(passes is JArray list) || list.Count < 1 || list.Count > 8) throw new ArgumentException("invalid vector passes");
            int work = 0;
            var parsed = new List<(bool Reverse, int[][] Offsets)>();
            foreach (var pass in list)
            {
                if (pass?["reverse"]?.Type != JTokenType.Boolean || !(pass["offsets"] is JArray offsets) || offsets.Count < 1 || offsets.Count > 8)
                    throw new ArgumentException("invalid vector pass");
                var delta = new int[offsets.Count][];
                for (int i = 0; i < offsets.Count; i++)
                {
                    if (!(offsets[i] is JArray pair) || pair.Count != 2 || pair.Any(v => v.Type != JTokenType.Integer))
                        throw new ArgumentException("invalid vector offset");
                    long x = pair[0].Value<long>(), y = pair[1].Value<long>();
                    if (x < -8 || x > 8 || y < -8 || y > 8) throw new ArgumentOutOfRangeException("offset");
                    delta[i] = new[] { (int)x, (int)y };
                }
                work += delta.Length; parsed.Add((pass["reverse"].Value<bool>(), delta));
            }
            Charge((long)_count * work);
            foreach (var pass in parsed)
                for (int n = 0; n < _count; n++)
                {
                    if ((n & 4095) == 0) Checkpoint();
                    int i = pass.Reverse ? _count - 1 - n : n;
                    if (seeds.Indices[i] == i) continue;
                    int x = i % _width, y = i / _width, best = seeds.Indices[i];
                    long distance = Distance(best, x, y);
                    foreach (var offset in pass.Offsets)
                    {
                        int nx = x + offset[0], ny = y + offset[1];
                        if (nx < 0 || ny < 0 || nx >= _width || ny >= _height) continue;
                        int candidate = seeds.Indices[ny * _width + nx];
                        long d = Distance(candidate, x, y);
                        if (d < distance) { best = candidate; distance = d; }
                    }
                    seeds.Indices[i] = best;
                }
            return handle;
        }

        private int Map(int handle, JToken colors, int fallback)
        {
            var seeds = Get<Seeds>(handle);
            if (!(colors is JArray list) || list.Count > 256 || _outputIndex >= MaxOutputs)
                throw new ArgumentException("invalid vector palette");
            Charge(_count);
            var palette = new byte[list.Count][];
            for (int i = 0; i < list.Count; i++)
            {
                if (!(list[i] is JArray rgb) || rgb.Count != 3) throw new ArgumentException("invalid palette entry");
                palette[i] = new byte[3];
                for (int j = 0; j < 3; j++)
                {
                    if (rgb[j].Type != JTokenType.Float && rgb[j].Type != JTokenType.Integer) throw new ArgumentException("invalid palette color");
                    float value = rgb[j].Value<float>();
                    if (!Finite(value) || value < 0f || value > 1f) throw new ArgumentOutOfRangeException("palette color");
                    palette[i][j] = Unorm(value);
                }
            }
            var dst = _outputs[_outputSlot][_outputIndex];
            if (dst == null) { Reserve((long)_count * 4); _outputs[_outputSlot][_outputIndex] = dst = new byte[_count * 4]; }
            _outputIndex++;
            for (int i = 0; i < _count; i++)
            {
                if ((i & 4095) == 0) Checkpoint();
                int owner = seeds.Indices[i] < 0 ? -1 : seeds.Owners[seeds.Indices[i]], p = i * 4;
                if (owner >= palette.Length) throw new ArgumentException("palette owner is out of range");
                dst[p] = owner < 0 ? (byte)(fallback >> 16) : palette[owner][0];
                dst[p + 1] = owner < 0 ? (byte)(fallback >> 8) : palette[owner][1];
                dst[p + 2] = owner < 0 ? (byte)fallback : palette[owner][2]; dst[p + 3] = 255;
            }
            return Put(new ResultBuffer { Bytes = dst });
        }

        internal string Apply(string input)
        {
            Checkpoint();
            if (input == null || input.Length > 131072 || ++_commands > MaxCommands)
                throw new ArgumentException("vector command limit exceeded");
            JObject root;
            using (var reader = new JsonTextReader(new StringReader(input)) { MaxDepth = 32, DateParseHandling = DateParseHandling.None })
            {
                root = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                if (reader.Read()) throw new ArgumentException("trailing vector command data");
            }
            if (root["op"]?.Type != JTokenType.String) throw new ArgumentException("missing vector operation");
            int result;
            switch (root.Value<string>("op"))
            {
                case "raster": result = Raster(root["shapes"], root.Value<string>("blend")); break;
                case "evaluate": result = Evaluate(root["program"], root["inputs"]); break;
                case "differenceSeeds": result = DifferenceSeeds(Integer(root, "a", 0, int.MaxValue), Integer(root, "b", 0, int.MaxValue), Integer(root, "value", 0, int.MaxValue)); break;
                case "propagate": result = Propagate(Integer(root, "seeds", 0, int.MaxValue), root["passes"]); break;
                case "map": result = Map(Integer(root, "seeds", 0, int.MaxValue), root["colors"], Integer(root, "fallback", 0, 0xffffff)); break;
                default: throw new ArgumentException("unknown vector operation");
            }
            return result.ToString(CultureInfo.InvariantCulture);
        }
    }
}
