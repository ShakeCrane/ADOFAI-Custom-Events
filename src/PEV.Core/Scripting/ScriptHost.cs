using System;
using System.Collections.Generic;
using System.Globalization;
using Jint;
using Jint.Native;
using Jint.Runtime;
using Newtonsoft.Json;
using PEV.Scripting;
using UnityEngine;

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
  },
  field: function (key, o) {
    var a = o || {}; a.key = key;
    return JSON.parse(__hostFx('field', JSON.stringify(a)));
  },
  fieldDestroy: function (key) {
    return JSON.parse(__hostFx('fielddestroy', JSON.stringify({ key: key })));
  },
  fieldMove: function (o) {
    return JSON.parse(__hostFx('fieldmove', JSON.stringify(o || {})));
  },
  fieldState: function (o) {
    return JSON.parse(__hostFx('fieldstate', JSON.stringify(o || {})));
  }
};
var __pevReady = true;
";

        private Engine _engine;
        private JsValue _instance;
        private JsValue _updateFn;
        private JsValue _disposeFn;
        private JsValue _context;

        public bool Faulted { get; private set; }

        /// <summary>Set from the P4 harness to log what each instance actually receives.</summary>
        public static bool LogInit;

        private int _setLogs;
        public string LastError { get; private set; }
        public double LastUpdateMs { get; private set; }
        public long UpdateCalls { get; private set; }

        private string _debugName;

        /// <summary>
        /// Tile this instance belongs to, injected into every host call so the field
        /// renderer can resolve relativeTo=Tile without the script repeating it.
        /// </summary>
        private int _floorSeq = -1;
        private double _eventTime;
        private int _eventOrder;

        public static ScriptHost Create(string source, string debugName, out string error)
        {
            error = null;
            var host = new ScriptHost { _debugName = debugName };
            try
            {
                host._engine = new Engine(options =>
                {
                    // MaxStatements is the real guard on runaway scripts, and it is exact.
                    //
                    // The wall-clock backstop must be generous, because Jint measures it
                    // across the whole Evaluate call including our own host delegates: a
                    // field texture build inside fx.field counts as script time. At 50ms a
                    // single first-frame texture build faulted two of three instances.
                    options.TimeoutInterval(TimeSpan.FromMilliseconds(2000));
                    options.MaxStatements(200000);

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

                // A script's key is only unique within its own instance. Stamping the
                // instance identity here means two fields on the same floor cannot collide,
                // and no script has to invent globally unique keys.
                args["__instance"] = _debugName ?? "";
                args["__floor"] = _floorSeq;
                args["__eventTime"] = _eventTime;
                args["__eventOrder"] = _eventOrder;

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

        /// <summary>
        /// Params cross into JS as JSON, and Unity structs (Vector2 and friends) expose
        /// self-referencing properties like normalized, which makes JsonConvert fail with a
        /// loop error before the script ever runs. Flatten them at the boundary.
        /// </summary>
        private static Dictionary<string, object> SanitizeParams(IDictionary<string, object> parameters)
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            if (parameters == null) return result;

            foreach (var kv in parameters)
                result[kv.Key] = SanitizeValue(kv.Value);
            return result;
        }

        private static object SanitizeValue(object value)
        {
            if (value == null) return null;
            if (value is Vector2 v2) return new[] { v2.x, v2.y };
            if (value is Vector3 v3) return new[] { v3.x, v3.y, v3.z };
            if (value is Color c) return new[] { c.r, c.g, c.b, c.a };
            if (value is Tuple<int, TileRelativeTo> tile)
                return new object[] { tile.Item1, tile.Item2.ToString() };
            if (value is Tuple<float, float> pair) return new[] { pair.Item1, pair.Item2 };

            // Enums cross as their name, not their ordinal.
            //
            // JsonConvert serialises a boxed enum as its numeric value, so a script saw
            // shape=0 and had to know the declaration order. That made reordering an enum a
            // silent behaviour change for every chart, and it is why a string comparison like
            // `params.shape === "Circle"` never matched. The file already stores names, so
            // this is the same vocabulary everywhere.
            if (value is Enum) return value.ToString();

            if (value is IDictionary<string, object> map)
            {
                var copy = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (var kv in map) copy[kv.Key] = SanitizeValue(kv.Value);
                return copy;
            }

            if (value is System.Collections.IEnumerable list && !(value is string))
            {
                var copy = new List<object>();
                foreach (var item in list) copy.Add(SanitizeValue(item));
                return copy;
            }

            return value;
        }

        public bool Initialize(EventContext context, IDictionary<string, object> parameters, out string error)
        {
            error = null;
            try
            {
                string paramsJson = JsonConvert.SerializeObject(SanitizeParams(parameters));
                string ctxJson = context.ToStateJson();

                if (LogInit) PEV.Core.Plugin.Log($"init {_debugName}: params={paramsJson}");

                // Must be set before create() runs: a script may call fx.field on its very
                // first line, and the host stamps this value into that call.
                _floorSeq = context != null ? context.FloorSeq : -1;
                _eventTime = context != null ? context.EventTime : 0;
                _eventOrder = context != null ? context.EventOrder : 0;

                _engine.SetValue("__ctx", ctxJson);
                _engine.SetValue("__params", paramsJson);

                _context = _engine.Evaluate("(function(c){if(c.settings)Object.freeze(c.settings);return c;})(JSON.parse(__ctx))");
                var createFn = _engine.Evaluate(
                    "(function(ctx){ return (typeof create === 'function') ? create(ctx, JSON.parse(__params)) : null; })");
                var result = _engine.Invoke(createFn, _context);

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
                LastError = error;
                Faulted = true;
                Dispose();
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
                    "(function(fn, self, ctx, t){ var state = JSON.parse(__state); " +
                    "if(state.settings)Object.freeze(state.settings); Object.keys(state).forEach(function(k){ctx[k] = state[k];}); return fn.call(self, t); })");
                _engine.Invoke(call, _updateFn, _instance, _context, t);
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
                    var call = _engine.Evaluate("(function(fn, self){ return fn.call(self); })");
                    _engine.Invoke(call, _disposeFn, _instance);
                }
            }
            catch { /* a throwing dispose must not block cleanup of the rest */ }
            finally
            {
                _instance = JsValue.Undefined;
                _updateFn = JsValue.Undefined;
                _disposeFn = JsValue.Undefined;
                _context = JsValue.Undefined;
                _engine = null;
                GameBridge.DestroyScope(_debugName);
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
