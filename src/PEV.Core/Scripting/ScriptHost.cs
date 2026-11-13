using System;
using System.Collections.Generic;
using System.Globalization;
using Jint;
using Jint.Native;
using Jint.Runtime;
using Newtonsoft.Json;
using PEV.Scripting;

namespace PEV.Scripting
{
    /// <summary>
    /// One isolated JS engine per event instance.
    ///
    /// Isolation is a property, not a word: a shared engine would let one script poison
    /// another through prototype mutation and global leakage. Engines are created once
    /// per instance at level load, never per frame.
    ///
    /// No CLR objects cross the boundary. Everything the script sees arrives as JSON
    /// parsed inside JS, and everything it asks for comes back as a function call with
    /// a JSON string. That keeps Interop.Enabled = false meaning what it says.
    /// </summary>
    public class ScriptHost : IDisposable
    {
        private const string Bootstrap = @"
var fx = {
  flash: function (o) { return JSON.parse(__hostFx('flash', JSON.stringify(o || {}))); },
  shake: function (o) { return JSON.parse(__hostFx('shake', JSON.stringify(o || {}))); },
  create: function (key, o) {
    var a = o || {}; a.key = key;
    return JSON.parse(__hostFx('create', JSON.stringify(a)));
  },
  set: function (key, o) {
    var a = o || {}; a.key = key;
    return JSON.parse(__hostFx('set', JSON.stringify(a)));
  },
  destroy: function (key) {
    return JSON.parse(__hostFx('destroy', JSON.stringify({ key: key })));
  }
};
var __pevReady = true;
";

        private Engine _engine;
        private JsValue _instance;
        private JsValue _updateFn;
        private JsValue _disposeFn;

        public bool Faulted { get; private set; }

        /// <summary>Set from the P4 harness to log what each instance actually receives.</summary>
        public static bool LogInit;

        private int _setLogs;
        public string LastError { get; private set; }
        public double LastUpdateMs { get; private set; }
        public long UpdateCalls { get; private set; }

        private string _debugName;

        public static ScriptHost Create(string source, string debugName, out string error)
        {
            error = null;
            var host = new ScriptHost { _debugName = debugName };
            try
            {
                host._engine = new Engine(options =>
                {
                    // MaxStatements is the primary guard: it is deterministic and does not
                    // punish the first call, which pays interpreter warm-up.
                    options.MaxStatements(200_000);

                    // A per-call wall clock is a backstop, not the main defence. 8 ms was
                    // measured to trip on the very first update() of a fresh engine on Mono
                    // purely from warm-up cost, so leave real headroom.
                    options.TimeoutInterval(TimeSpan.FromMilliseconds(50));

                    options.LimitRecursion(64);
                    options.Strict();

                    options.Interop.Enabled = false;
                    options.Interop.AllowWrite = false;
                    options.Host.StringCompilationAllowed = false;

                    // Stack overflow inside a script kills the process; this guard is not optional.
                    options.Constraints.StackOverflowGuard = true;
                });

                host._engine.SetValue("__hostFx", new Func<string, string, string>(host.OnHostFx));
                host._engine.Execute(Bootstrap);
                host._engine.Execute(source);
                return host;
            }
            catch (Exception e)
            {
                error = Describe(e, debugName);
                host.Dispose();
                return null;
            }
        }

        /// <summary>Host-side effect dispatch. Only whitelisted names reach the game.</summary>
        private string OnHostFx(string name, string argsJson)
        {
            try
            {
                if (LogInit && name == "set" && _setLogs < 2)
                {
                    _setLogs++;
                    PEV.Core.Plugin.Log($"fx.set args from {_debugName}: {argsJson}");
                }

                var args = string.IsNullOrEmpty(argsJson)
                    ? new Dictionary<string, object>()
                    : JsonConvert.DeserializeObject<Dictionary<string, object>>(argsJson)
                      ?? new Dictionary<string, object>();

                bool ok = GameBridge.TryPlayNative(name, args, out string error);
                return JsonConvert.SerializeObject(new Dictionary<string, object>
                {
                    { "ok", ok },
                    { "error", error }
                });
            }
            catch (Exception e)
            {
                return JsonConvert.SerializeObject(new Dictionary<string, object>
                {
                    { "ok", false },
                    { "error", e.Message }
                });
            }
        }

        public bool Initialize(EventContext context, IDictionary<string, object> parameters, out string error)
        {
            error = null;
            try
            {
                string paramsJson = JsonConvert.SerializeObject(parameters);
                string ctxJson = context.ToStateJson();

                if (LogInit) PEV.Core.Plugin.Log($"init {_debugName}: params={paramsJson}");

                _engine.SetValue("__ctx", ctxJson);
                _engine.SetValue("__params", paramsJson);

                var result = _engine.Evaluate(
                    "(function(){ return (typeof create === 'function') ? create(JSON.parse(__ctx), JSON.parse(__params)) : null; })()");

                _instance = result;
                if (_instance != null && _instance.IsObject())
                {
                    var obj = _instance.AsObject();
                    _updateFn = obj.Get("update");
                    _disposeFn = obj.Get("dispose");
                }
                return true;
            }
            catch (Exception e)
            {
                error = Describe(e, _debugName);
                Faulted = true;
                return false;
            }
        }

        public void Update(double t, string stateJson)
        {
            if (Faulted) return;
            if (_instance == null || !_instance.IsObject()) return;
            if (_updateFn == null || !_updateFn.IsObject()) return;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                _engine.SetValue("__state", stateJson);

                // Call through a wrapper so the argument list is unambiguous. Invoking the
                // function object directly let Jint bind the extra arguments as its own
                // overload parameters, and update() received undefined instead of t.
                var call = _engine.Evaluate(
                    "(function(fn, self, t){ return fn.call(self, t); })");
                _engine.Invoke(call, _updateFn, _instance, t);
                UpdateCalls++;
            }
            catch (Exception e)
            {
                Faulted = true;
                LastError = Describe(e, _debugName);
            }
            finally
            {
                sw.Stop();
                LastUpdateMs = sw.Elapsed.TotalMilliseconds;
            }
        }

        public void Dispose()
        {
            try
            {
                if (!Faulted && _instance != null && _instance.IsObject() &&
                    _disposeFn != null && _disposeFn.IsObject())
                {
                    _engine.Invoke(_disposeFn, _instance);
                }
            }
            catch { /* a throwing dispose must not block cleanup of the rest */ }
            finally
            {
                _instance = JsValue.Undefined;
                _updateFn = JsValue.Undefined;
                _disposeFn = JsValue.Undefined;
                _engine = null;
            }
        }

        private static string Describe(Exception e, string debugName)
        {
            if (e is JavaScriptException js) return $"[{debugName}] JS error: {js.Message}";
            if (e is StatementsCountOverflowException) return $"[{debugName}] statement budget exceeded";
            if (e is TimeoutException) return $"[{debugName}] time budget exceeded";
            if (e is RecursionDepthOverflowException) return $"[{debugName}] recursion limit hit";
            return $"[{debugName}] {e.GetType().Name}: {e.Message}";
        }
    }
}