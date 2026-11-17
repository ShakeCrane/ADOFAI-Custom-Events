using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PEV.Scripting
{
    // Runs a package-provided per-group controller with plain snapshots only. Large inputs
    // are processed off-thread while the previous validated result remains available.
    internal sealed class GroupScriptRunner : IDisposable
    {
        private struct Input
        {
            public float Time, Aspect;
            public int Capacity, ViewWidth, ViewHeight;
            public string DefaultColor;
            public ViewportPoint[] Points;
        }
        private sealed class Completed
        {
            internal JObject Result, Plan;
            internal float Aspect;
            internal int Width, Height, Capacity;
            internal string DefaultColor;
            internal Dictionary<string, bool> Settings;
            internal ViewportPoint[] Points;
        }
        private readonly string _source;
        private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();
        private JsonController _controller;
        private Task<Completed> _pending;
        private Completed _last;
        private bool _disposed;
        private const int BackgroundThreshold = 32;

        internal GroupScriptRunner(string source)
        {
            if (string.IsNullOrWhiteSpace(source) || source.Length > 262144) throw new ArgumentException("Invalid group program");
            _source = source;
        }
        internal bool Update(JObject plan, float time, float aspect, int capacity, int width, int height, string defaultColor,
            IReadOnlyList<ViewportPoint> points, IReadOnlyDictionary<string, bool> settings, int workSize, out JObject result)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(GroupScriptRunner));
            if (plan == null || points == null || settings == null || capacity < 1 || capacity > 64 ||
                width < 1 || height < 1 || points.Count > 4096 || workSize < 0 || workSize > 1024 ||
                float.IsNaN(time) || float.IsInfinity(time) || aspect <= 0 || float.IsNaN(aspect) || float.IsInfinity(aspect))
                throw new ArgumentException("Invalid group program input");
            if (_pending != null && _pending.IsCompleted)
            {
                try { _last = _pending.GetAwaiter().GetResult(); }
                finally { _pending = null; }
            }
            bool continuous = _last != null && _last.Result.Value<bool>("continuous");
            bool needsUpdate = _last == null || !ReferenceEquals(_last.Plan, plan) || _last.Aspect != aspect ||
                _last.Width != width || _last.Height != height || _last.Capacity != capacity || _last.DefaultColor != defaultColor ||
                !SameSettings(_last.Settings, settings) || continuous || !SamePoints(_last.Points, points);
            if (!needsUpdate) { result = _last.Result; return true; }
            if (_pending == null)
            {
                bool includePlan = _last == null || !ReferenceEquals(_last.Plan, plan);
                var pointCopy = new ViewportPoint[points.Count];
                for (int i = 0; i < pointCopy.Length; i++) pointCopy[i] = points[i];
                var settingsCopy = new Dictionary<string, bool>(StringComparer.Ordinal);
                foreach (var setting in settings) settingsCopy.Add(setting.Key, setting.Value);
                var input = new Input { Time = time, Aspect = aspect, Capacity = capacity, ViewWidth = width,
                    ViewHeight = height, DefaultColor = defaultColor, Points = pointCopy };
                if (workSize >= BackgroundThreshold)
                {
                    var cancellation = _cancellation.Token;
                    _pending = Task.Run(async () =>
                    {
                        await VectorWorkerLimits.Slots.WaitAsync(cancellation).ConfigureAwait(false);
                        try { return Execute(plan, input, settingsCopy, includePlan, cancellation); }
                        finally { VectorWorkerLimits.Slots.Release(); }
                    });
                }
                else _last = Execute(plan, input, settingsCopy, includePlan, _cancellation.Token);
            }
            result = _last?.Result;
            return result != null;
        }
        private Completed Execute(JObject plan, Input input, Dictionary<string, bool> settings, bool includePlan, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (_controller == null) _controller = new JsonController(_source, 50, 524288, 262144, cancellation);
            var json = JObject.Parse(PublicFieldsSnapshot.Serialize(input));
            if (includePlan) json["Group"] = plan.DeepClone();
            json["Settings"] = JObject.FromObject(settings);
            var result = _controller.Update(json.ToString(Formatting.None));
            if (result["continuous"]?.Type != JTokenType.Boolean) throw new ArgumentException("Invalid group program scheduling hint");
            return new Completed { Result = result, Plan = plan, Aspect = input.Aspect, Width = input.ViewWidth, Height = input.ViewHeight,
                Capacity = input.Capacity, DefaultColor = input.DefaultColor, Settings = settings, Points = input.Points };
        }
        private static bool SameSettings(IReadOnlyDictionary<string, bool> left, IReadOnlyDictionary<string, bool> right)
        {
            if (left == null || left.Count != right.Count) return false;
            foreach (var item in right) if (!left.TryGetValue(item.Key, out var value) || value != item.Value) return false;
            return true;
        }
        private static bool SamePoints(IReadOnlyList<ViewportPoint> left, IReadOnlyList<ViewportPoint> right)
        {
            if (left == null || left.Count != right.Count) return false;
            for (int i = 0; i < left.Count; i++)
                if (left[i].Id != right[i].Id || left[i].Position.x != right[i].Position.x || left[i].Position.y != right[i].Position.y)
                    return false;
            return true;
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true; _cancellation.Cancel();
            var pending = _pending; _pending = null; _last = null;
            if (pending != null) pending.ContinueWith(t => { var ignored = t.Exception; _cancellation.Dispose(); }, TaskContinuationOptions.ExecuteSynchronously);
            else _cancellation.Dispose();
        }
    }
}
