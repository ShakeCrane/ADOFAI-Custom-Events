using System;
using System.Collections.Generic;
using System.Threading;
using Jint;
using Newtonsoft.Json.Linq;

namespace PEV.Scripting
{
    // One synchronous sandbox, owned by one background worker at a time.
    internal sealed class VectorProgram
    {
        private readonly string _source;
        private readonly IReadOnlyDictionary<string, PixelResource> _resources;
        private readonly CancellationToken _cancellation;
        private readonly int _timeoutSeconds;
        private Engine _engine;
        private VectorCompute _compute;
        private int _width, _height;
        private float _aspect;
        internal VectorProgram(string source, IReadOnlyDictionary<string, PixelResource> resources, CancellationToken cancellation, int timeoutSeconds = 2)
        {
            if (string.IsNullOrWhiteSpace(source) || source.Length > 262144 || timeoutSeconds < 1 || timeoutSeconds > 15)
                throw new ArgumentException("invalid vector program");
            _source = source; _resources = resources; _cancellation = cancellation; _timeoutSeconds = timeoutSeconds;
        }
        internal byte[][] Execute(int width, int height, float aspect, string input)
        {
            _cancellation.ThrowIfCancellationRequested();
            if (_engine == null)
            {
                _engine = new Engine(options =>
                {
                    options.TimeoutInterval(TimeSpan.FromSeconds(_timeoutSeconds));
                    options.MaxStatements(200000); options.LimitRecursion(64);
                    options.LimitMemory(96L * 1024 * 1024); options.CancellationToken(_cancellation);
                    options.Strict(); options.Interop.Enabled = false; options.Interop.AllowWrite = false;
                    options.Host.StringCompilationAllowed = false; options.Constraints.StackOverflowGuard = true;
                });
                _engine.Execute(@"globalThis.ArrayBuffer=undefined; globalThis.SharedArrayBuffer=undefined;
globalThis.DataView=undefined; globalThis.Int8Array=undefined; globalThis.Uint8Array=undefined;
globalThis.Uint8ClampedArray=undefined; globalThis.Int16Array=undefined; globalThis.Uint16Array=undefined;
globalThis.Int32Array=undefined; globalThis.Uint32Array=undefined; globalThis.Float32Array=undefined;
globalThis.Float64Array=undefined; globalThis.BigInt64Array=undefined; globalThis.BigUint64Array=undefined;");
                _engine.Execute(_source);
            }
            if (_compute == null || _width != width || _height != height || _aspect != aspect)
            {
                _compute = new VectorCompute(width, height, aspect, _resources, _timeoutSeconds);
                _width = width; _height = height; _aspect = aspect;
                _engine.SetValue("__compute", new Func<string, string>(_compute.Apply));
            }
            if (input == null || input.Length > 131072) throw new ArgumentException("vector input is too large");
            _compute.Begin(_cancellation);
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
            return buffers;
        }
    }
}
