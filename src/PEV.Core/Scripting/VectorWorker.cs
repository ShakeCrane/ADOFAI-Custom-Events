using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jint;
using Newtonsoft.Json.Linq;

namespace PEV.Scripting
{
    internal static class VectorWorkerLimits
    {
        internal static readonly SemaphoreSlim Slots = new SemaphoreSlim(Math.Max(1, Environment.ProcessorCount / 2));
    }

    /// <summary>
    /// Runs a package-supplied, sandboxed vector program off the Unity thread.
    /// Only immutable JSON and bounded byte-buffer operations cross the boundary.
    /// </summary>
    internal sealed class VectorWorker<T> : IDisposable
    {
        private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();
        private readonly string _source;
        private readonly Func<T, T> _snapshot;
        private readonly Func<T, T, bool> _same;
        private readonly Func<int, int, float, T, string> _serialize;
        private readonly Dictionary<string, PixelResource> _resources;
        private Jint.Engine _engine;
        private VectorCompute _compute;
        private int _computeWidth, _computeHeight;
        private float _computeAspect;

        private sealed class Request
        {
            internal int Width, Height;
            internal float Aspect;
            internal T Data;
        }
        private sealed class Completed
        {
            internal Request Request;
            internal byte[][] Buffers;
        }
        private Request _desired, _running, _finished, _failed;
        private Task<Completed> _pending;
        private bool _disposed;
        internal byte[][] Latest { get; private set; }
        internal int LatestWidth { get; private set; }
        internal int LatestHeight { get; private set; }
        internal float LatestAspect { get; private set; }
        internal Exception LastError { get; private set; }

        internal VectorWorker(string source, Func<T, T> snapshot, Func<T, T, bool> same,
            Func<int, int, float, T, string> serialize, IReadOnlyDictionary<string, PixelResource> resources = null)
        {
            if (string.IsNullOrWhiteSpace(source) || source.Length > 262144) throw new ArgumentException("invalid vector program");
            _source = source;
            _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
            _same = same ?? throw new ArgumentNullException(nameof(same));
            _serialize = serialize ?? throw new ArgumentNullException(nameof(serialize));
            if (resources != null)
            {
                if (resources.Count > 16) throw new ArgumentException("too many pixel resources");
                _resources = new Dictionary<string, PixelResource>(StringComparer.Ordinal);
                foreach (var resource in resources) _resources.Add(resource.Key, resource.Value);
            }
        }

        // Called by the owner thread. A completed result remains valid while the next job runs.
        internal bool Update(int width, int height, float aspect, T data)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(VectorWorker<T>));
            if (_desired == null || _desired.Width != width || _desired.Height != height ||
                _desired.Aspect != aspect || !_same(_desired.Data, data))
            {
                _desired = new Request { Width = width, Height = height, Aspect = aspect, Data = _snapshot(data) };
                _failed = null; LastError = null;
            }
            bool updated = false;
            if (_pending != null && _pending.IsCompleted)
            {
                try
                {
                    var done = _pending.GetAwaiter().GetResult();
                    Latest = done.Buffers; LatestWidth = done.Request.Width; LatestHeight = done.Request.Height;
                    LatestAspect = done.Request.Aspect;
                    updated = done.Request.Width == width && done.Request.Height == height && done.Request.Aspect == aspect;
                    _finished = done.Request; _failed = null;
                }
                catch (Exception e) { LastError = e; _failed = _running; }
                _pending = null;
            }
            if (_pending == null && !ReferenceEquals(_desired, _finished) && !ReferenceEquals(_desired, _failed))
            {
                var request = _desired; var cancellation = _cancellation.Token; _running = request;
                _pending = Task.Run(async () =>
                {
                    await VectorWorkerLimits.Slots.WaitAsync(cancellation).ConfigureAwait(false);
                    try { return Execute(request, cancellation); }
                    finally { VectorWorkerLimits.Slots.Release(); }
                });
            }
            return updated;
        }

        private Completed Execute(Request request, CancellationToken cancellation)
        {
            try { return ExecuteCore(request, cancellation); }
            catch
            {
                // A failed command can leave pooled buffers or JS globals partially changed.
                // A later request gets a fresh, isolated engine and resource pool.
                _engine = null; _compute = null;
                throw;
            }
        }

        private Completed ExecuteCore(Request request, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (_engine == null)
            {
                _engine = new Jint.Engine(options =>
                {
                    options.TimeoutInterval(TimeSpan.FromSeconds(2));
                    options.MaxStatements(200000); options.LimitRecursion(64);
                    options.LimitMemory(96L * 1024 * 1024); options.CancellationToken(cancellation);
                    options.Strict(); options.Interop.Enabled = false; options.Interop.AllowWrite = false;
                    options.Host.StringCompilationAllowed = false; options.Constraints.StackOverflowGuard = true;
                });
                // Large typed-array allocations can occur in a single JS statement before
                // a statement-based limit is checked. Bulk storage belongs to VectorCompute.
                _engine.Execute(@"globalThis.ArrayBuffer=undefined; globalThis.SharedArrayBuffer=undefined;
globalThis.DataView=undefined; globalThis.Int8Array=undefined; globalThis.Uint8Array=undefined;
globalThis.Uint8ClampedArray=undefined; globalThis.Int16Array=undefined; globalThis.Uint16Array=undefined;
globalThis.Int32Array=undefined; globalThis.Uint32Array=undefined; globalThis.Float32Array=undefined;
globalThis.Float64Array=undefined; globalThis.BigInt64Array=undefined; globalThis.BigUint64Array=undefined;");
                _engine.Execute(_source);
            }
            if (_compute == null || _computeWidth != request.Width || _computeHeight != request.Height || _computeAspect != request.Aspect)
            {
                _compute = new VectorCompute(request.Width, request.Height, request.Aspect, _resources);
                _computeWidth = request.Width; _computeHeight = request.Height; _computeAspect = request.Aspect;
                _engine.SetValue("__compute", new Func<string, string>(_compute.Apply));
            }
            string input = _serialize(request.Width, request.Height, request.Aspect, request.Data);
            if (input == null || input.Length > 131072) throw new ArgumentException("vector input is too large");
            _compute.Begin(cancellation);
            _engine.SetValue("__input", input);
            var value = _engine.Evaluate("JSON.stringify(computeVector(JSON.parse(__input)))");
            if (!value.IsString()) throw new ArgumentException("vector program must return output handles");
            string json = value.AsString();
            if (json.Length > 256) throw new ArgumentException("vector result is too large");
            var handles = JArray.Parse(json);
            if (handles.Count < 1 || handles.Count > 8) throw new ArgumentException("invalid vector result count");
            var buffers = new byte[handles.Count][];
            for (int i = 0; i < handles.Count; i++)
            {
                if (handles[i].Type != JTokenType.Integer) throw new ArgumentException("invalid vector result handle");
                buffers[i] = _compute.Result(handles[i].Value<int>());
            }
            return new Completed { Request = request, Buffers = buffers };
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true; _cancellation.Cancel();
            _desired = _running = _finished = _failed = null; Latest = null;
            if (_pending != null)
                _pending.ContinueWith(t => { var ignored = t.Exception; _cancellation.Dispose(); }, TaskContinuationOptions.ExecuteSynchronously);
            else _cancellation.Dispose();
            _pending = null;
        }
    }
}
