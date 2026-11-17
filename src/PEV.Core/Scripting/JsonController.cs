using System;
using System.Threading;
using Jint;
using Newtonsoft.Json.Linq;

namespace PEV.Scripting
{
    // One isolated, bounded stateful JSON program per consumer. No CLR objects are exposed.
    internal sealed class JsonController
    {
        private readonly Engine _engine;
        private readonly int _maxInput, _maxOutput;
        internal JsonController(string source, int timeoutMilliseconds = 50, int maxInput = 131072, int maxOutput = 16384,
            CancellationToken cancellation = default)
        {
            if (string.IsNullOrWhiteSpace(source) || source.Length > 262144 || timeoutMilliseconds < 1 || timeoutMilliseconds > 500 ||
                maxInput < 1 || maxInput > 524288 || maxOutput < 1 || maxOutput > 524288)
                throw new ArgumentException("Invalid controller program");
            _maxInput = maxInput; _maxOutput = maxOutput;
            _engine = new Engine(options =>
            {
                options.TimeoutInterval(TimeSpan.FromMilliseconds(timeoutMilliseconds));
                options.MaxStatements(30000); options.LimitRecursion(64);
                options.LimitMemory(8L * 1024 * 1024); options.Strict();
                options.CancellationToken(cancellation);
                options.Interop.Enabled = false; options.Interop.AllowWrite = false;
                options.Host.StringCompilationAllowed = false; options.Constraints.StackOverflowGuard = true;
            });
            _engine.Execute(@"globalThis.ArrayBuffer=undefined; globalThis.SharedArrayBuffer=undefined;
globalThis.DataView=undefined; globalThis.Int8Array=undefined; globalThis.Uint8Array=undefined;
globalThis.Uint8ClampedArray=undefined; globalThis.Int16Array=undefined; globalThis.Uint16Array=undefined;
globalThis.Int32Array=undefined; globalThis.Uint32Array=undefined; globalThis.Float32Array=undefined;
globalThis.Float64Array=undefined; globalThis.BigInt64Array=undefined; globalThis.BigUint64Array=undefined;");
            _engine.Execute(source);
        }
        internal JObject Update(string json)
        {
            if (json == null || json.Length > _maxInput) throw new ArgumentException("Controller input is too large");
            _engine.SetValue("__controllerInput", json);
            var result = _engine.Evaluate("JSON.stringify(updateController(JSON.parse(__controllerInput)))");
            if (!result.IsString() || result.AsString().Length > _maxOutput)
                throw new ArgumentException("Invalid controller result");
            return JObject.Parse(result.AsString());
        }
    }
}
