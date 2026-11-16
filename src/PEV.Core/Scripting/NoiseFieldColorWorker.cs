using System;
using System.Threading;
using System.Threading.Tasks;

namespace PEV.Scripting
{
    /// <summary>
    /// One in-flight immutable palette snapshot per group. A changed request replaces
    /// the desired snapshot rather than queuing work for every animation frame.
    /// Results contain no Unity objects; callers upload them on the main thread.
    /// </summary>
    internal sealed class NoiseFieldColorWorker : IDisposable
    {
        private static readonly SemaphoreSlim Slots = new SemaphoreSlim(Math.Max(1, Environment.ProcessorCount / 2));
        private readonly NoiseFieldColorOwnership.Workspace _workspace = new NoiseFieldColorOwnership.Workspace();
        private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();

        private sealed class Request
        {
            internal int Width, Height;
            internal float Aspect;
            internal NoiseFieldColorOwnership.Zone[] Zones;
        }

        private sealed class Completed
        {
            internal Request Request;
            internal NoiseFieldColorOwnership.Result Result;
        }

        private Request _desired, _failed;
        private Task<Completed> _pending;
        private bool _disposed;
        // Consume synchronously after Update. Older result arrays are reused by later jobs.
        internal NoiseFieldColorOwnership.Result Latest { get; private set; }
        internal int LatestWidth { get; private set; }
        internal int LatestHeight { get; private set; }
        internal float LatestAspect { get; private set; }
        internal Exception LastError { get; private set; }

        // Called from the owning thread, including while paused so a worker can complete.
        // Returns true only when the caller should upload a new result.
        internal bool Update(int width, int height, float aspect, NoiseFieldColorOwnership.Zone[] zones)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(NoiseFieldColorWorker));
            if (zones == null) throw new ArgumentNullException(nameof(zones));
            if (!Matches(_desired, width, height, aspect, zones))
            {
                _desired = new Request { Width = width, Height = height, Aspect = aspect,
                    Zones = (NoiseFieldColorOwnership.Zone[])zones.Clone() };
                _failed = null;
                LastError = null;
            }
            bool updated = false;
            if (_pending != null && _pending.IsCompleted)
            {
                try
                {
                    var done = _pending.GetAwaiter().GetResult();
                    // Never upload a previous viewport size; the old snapshot may still be
                    // shown while geometry animates at the same resolution.
                    // Even a superseded result becomes Latest so the next worker uses
                    // the other output slot; callers only upload matching dimensions.
                    Latest = done.Result;
                    LatestWidth = done.Request.Width;
                    LatestHeight = done.Request.Height;
                    LatestAspect = done.Request.Aspect;
                    updated = done.Request.Width == width && done.Request.Height == height && done.Request.Aspect == aspect;
                    _finished = done.Request;
                    _failed = null;
                }
                catch (Exception e) { LastError = e; _failed = _running; }
                _pending = null;
            }
            if (_pending == null && !ReferenceEquals(_desired, _finished) && !ReferenceEquals(_desired, _failed))
            {
                var request = _desired;
                var cancellation = _cancellation.Token;
                _running = request;
                _pending = Task.Run(async () =>
                {
                    await Slots.WaitAsync(cancellation).ConfigureAwait(false);
                    try
                    {
                        cancellation.ThrowIfCancellationRequested();
                        return new Completed { Request = request,
                            Result = NoiseFieldColorOwnership.Build(request.Width, request.Height, request.Aspect, request.Zones, _workspace) };
                    }
                    finally { Slots.Release(); }
                });
            }
            return updated;
        }

        private Request _running;
        private Request _finished;

        private static bool Matches(Request request, int width, int height, float aspect,
            NoiseFieldColorOwnership.Zone[] zones)
        {
            if (request == null || request.Width != width || request.Height != height ||
                request.Aspect != aspect || request.Zones.Length != zones.Length) return false;
            for (int i = 0; i < zones.Length; i++)
            {
                var a = request.Zones[i]; var b = zones[i];
                if (a.CenterX != b.CenterX || a.CenterY != b.CenterY || a.HalfX != b.HalfX || a.HalfY != b.HalfY ||
                    a.Angle != b.Angle || a.YScale != b.YScale || a.Opacity != b.Opacity || a.Red != b.Red ||
                    a.Green != b.Green || a.Blue != b.Blue || a.Active != b.Active || a.Inverted != b.Inverted) return false;
            }
            return true;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _cancellation.Cancel();
            _desired = _running = _finished = _failed = null;
            Latest = null;
            // In-flight work owns its inputs and does not touch this object or Unity.
            if (_pending != null)
                _pending.ContinueWith(t => { var ignored = t.Exception; _cancellation.Dispose(); },
                    TaskContinuationOptions.ExecuteSynchronously);
            else _cancellation.Dispose();
            _pending = null;
        }
    }
}
