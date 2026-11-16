using System;
using Jint;
using Newtonsoft.Json.Linq;

namespace PEV.Scripting
{
    // One isolated, bounded stateful JSON program per consumer. No CLR objects are exposed.
    internal sealed class JsonController
    {
        private readonly Engine _engine;
        internal JsonController(string source)
        {
            if (string.IsNullOrWhiteSpace(source) || source.Length > 262144)
                throw new ArgumentException("Invalid controller program");
            _engine = new Engine(options =>
            {
                options.TimeoutInterval(TimeSpan.FromMilliseconds(50));
                options.MaxStatements(30000); options.LimitRecursion(64);
                options.LimitMemory(8L * 1024 * 1024); options.Strict();
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
            if (json == null || json.Length > 131072) throw new ArgumentException("Controller input is too large");
            _engine.SetValue("__controllerInput", json);
            var result = _engine.Evaluate("JSON.stringify(updateController(JSON.parse(__controllerInput)))");
            if (!result.IsString() || result.AsString().Length > 16384)
                throw new ArgumentException("Invalid controller result");
            return JObject.Parse(result.AsString());
        }
    }
}
