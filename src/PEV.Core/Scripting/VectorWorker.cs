using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PEV.Scripting
{
    internal static class VectorWorkerLimits
    {
        internal static readonly int Count = Math.Max(1, Environment.ProcessorCount / 2);
        internal static readonly SemaphoreSlim Slots = new SemaphoreSlim(Count);
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
        private VectorProgram _program;

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
                _program = null;
                throw;
            }
        }

        private Completed ExecuteCore(Request request, CancellationToken cancellation)
        {
            if (_program == null) _program = new VectorProgram(_source, _resources, cancellation);
            string input = _serialize(request.Width, request.Height, request.Aspect, request.Data);
            var buffers = _program.Execute(request.Width, request.Height, request.Aspect, input);
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
