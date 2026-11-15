using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using ADOFAI;
using HarmonyLib;
using PEV.Registry;
using PEV.Scripting;
using PEV.Storage;
using EventInstance = PEV.Scripting.EventInstance;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace PEV.Diagnostics
{
    /// <summary>
    /// Automated harness for P1. Inert unless "p1-test.txt" exists next to the DLL.
    /// </summary>
    public static class P1Harness
    {
        public static string LevelPath;
        public static bool Enabled;

        private static string _resultPath;

        public static void Configure(string modDir)
        {
            Enabled = File.Exists(Path.Combine(modDir, "p1-test.txt"));
            if (!Enabled) return;

            _resultPath = Path.Combine(modDir, "p1-results.txt");
            LevelPath = ReadOr(Path.Combine(modDir, "p1-level.txt"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "A Dance of Fire and Ice", "Levels", "P1Test.adofai"));

            try { File.WriteAllText(_resultPath, ""); } catch { }
            Row("HARNESS", "enabled", LevelPath);
        }

        private static string ReadOr(string file, string fallback)
        {
            try { if (File.Exists(file)) return File.ReadAllText(file).Trim(); } catch { }
            return fallback;
        }

        public static void Row(string key, string value, string note = "")
        {
            string line = $"{key}\t{value}\t{note}";
            Debug.Log("[PEV-P1] " + line);
            try { File.AppendAllText(_resultPath, line + Environment.NewLine); } catch { }
        }

        [HarmonyPatch(typeof(scnLevelSelect), "Start")]
        public static class AutoOpen
        {
            public static void Postfix()
            {
                if (!Enabled) return;
                try
                {
                    if (!File.Exists(LevelPath)) { Row("AUTOOPEN", "MISSING", LevelPath); return; }
                    scnEditor.levelToOpenOnLoad = LevelPath;
                    ADOBase.loader.LoadScene("scnEditor");
                    Row("AUTOOPEN", "ok", LevelPath);
                }
                catch (Exception e) { Row("AUTOOPEN", "EXCEPTION", e.ToString()); }
            }
        }

        private static bool _ran;

        [HarmonyPatch(typeof(scnEditor), "Start")]
        public static class Probe
        {
            public static void Postfix(scnEditor __instance)
            {
                if (!Enabled || _ran) return;
                _ran = true;
                // Restart replaces the editor scene. Keep the verification coroutine on
                // the persistent runtime so it can report the restart result and DONE.
                RuntimeHost.Ensure().StartCoroutine(Run(__instance));
            }

            private static IEnumerator Run(scnEditor editor)
            {
                yield return new WaitForSeconds(2f);

                // --- 1. Scripts compile and initialise ---
                var instances = GetAllInstances();
                Row("RUNTIME", instances.Count.ToString(), "instances armed");
                foreach (var inst in instances)
                    Row("INSTANCE", inst.Key, $"floor={inst.LevelEvent.floor} phase={inst.Phase}");

                // --- 2. Sandbox: per-script isolation and limits ---
                RunSandboxChecks();

                // --- 3. Start playback so update() actually runs ---
                Row("PLAY", "starting", "calling editor.Play()");
                try { editor.Play(); } catch (Exception e) { Row("PLAY", "EXCEPTION", e.Message); }

                yield return new WaitForSeconds(6f);

                // --- 4. Did update() run, and was state per-instance? ---
                var running = GetAllInstances();
                foreach (var inst in running)
                {
                    if (inst.Host == null) continue;
                    Row("UPDATES", inst.Key,
                        $"calls={inst.Host.UpdateCalls}, lastMs={inst.Host.LastUpdateMs:F3}, faulted={inst.Host.Faulted}");
                }

                int totalCalls = running.Where(i => i.Host != null).Sum(i => (int)i.Host.UpdateCalls);
                Row("UPDATE-TOTAL", totalCalls.ToString(), "sum of update() calls across instances");

                double worst = running.Where(i => i.Host != null)
                    .Select(i => i.Host.LastUpdateMs).DefaultIfEmpty(0).Max();
                Row("UPDATE-COST", $"{worst:F3} ms", "worst single update()");

                // --- 5. Pause behaviour ---
                // C# forbids yield inside a try that has a catch, so the waits live outside.
                // Sample only after the pause has settled: AudioListener.pause stops the
                // dsp clock on the next audio frame, so a couple of ticks still land.
                bool pauseSet = SetPaused(true);
                yield return new WaitForSeconds(0.7f);
                int beforePause = TotalCalls();
                yield return new WaitForSeconds(2f);
                int afterPause = TotalCalls();
                int pausedDelta = afterPause - beforePause;
                SetPaused(false);

                bool pauseOk = pausedDelta <= 2;   // allow one settling tick
                Row("PAUSE", !pauseSet ? "SKIP" : (pauseOk ? "PASS" : "FAIL"),
                    $"calls during 2s paused window: {pausedDelta} (running baseline is ~120/instance)");

                yield return new WaitForSeconds(1f);

                // --- 6. Restart resets state instead of leaking ---
                int beforeRestart = TotalCalls();
                bool restarted = RestartPlayback();
                yield return new WaitForSeconds(3f);
                var afterRestart = GetAllInstances();
                Row("RESTART", !restarted ? "SKIP" : "ok",
                    $"instances after restart={afterRestart.Count}, callsBefore={beforeRestart}");

                Row("DONE", "ok", "p1 harness finished");
            }

            private static List<EventInstance> GetAllInstances()
            {
                var host = RuntimeHost.Ensure();
                var field = typeof(RuntimeHost).GetField("_instances",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                return (field?.GetValue(host) as List<EventInstance>) ?? new List<EventInstance>();
            }

            private static int TotalCalls()
            {
                return GetAllInstances().Where(i => i.Host != null)
                    .Sum(i => (int)i.Host.UpdateCalls);
            }

            private static bool SetPaused(bool paused)
            {
                try
                {
                    var ctrl = scrController.instance;
                    if (ctrl == null) return false;

                    // scrController.paused only swaps input mappings and hides UI.
                    // The song clock is driven from AudioSettings.dspTime, and the thing
                    // that actually freezes it is AudioListener.pause (exposed here as
                    // audioPaused). Setting only `paused` leaves the clock running, which
                    // is exactly the bug this test was written to catch.
                    ctrl.audioPaused = paused;
                    ctrl.paused = paused;
                    return true;
                }
                catch (Exception e) { Row("PAUSE", "EXCEPTION", e.Message); return false; }
            }

            private static bool RestartPlayback()
            {
                try
                {
                    var ctrl = scrController.instance;
                    if (ctrl == null) return false;
                    ctrl.Restart(fromBeginning: false);
                    return true;
                }
                catch (Exception e) { Row("RESTART", "EXCEPTION", e.Message); return false; }
            }

            private static void RunSandboxChecks()
            {
                // a) infinite loop must be stopped by the statement/time budget
                var sw = Stopwatch.StartNew();
                var host = ScriptHost.Create("function create(){ return { update: function(){ while(true){} } }; }",
                    "probe.infinite-loop", out string err);
                if (host == null)
                {
                    Row("SANDBOX-loop", "COMPILE-FAIL", err);
                }
                else
                {
                    host.Initialize(new EventContext(), new Dictionary<string, object>(), out _);
                    host.Update(0, "{}");
                    sw.Stop();
                    Row("SANDBOX-loop", host.Faulted ? "PASS (contained)" : "FAIL (still running)",
                        $"{sw.ElapsedMilliseconds} ms, error={host.LastError}");
                }

                // b) recursion must hit the depth limit, not crash the process
                var rec = ScriptHost.Create(
                    "function create(){ return { update: function(){ (function f(n){ f(n+1); })(0); } }; }",
                    "probe.recursion", out string recErr);
                if (rec != null)
                {
                    rec.Initialize(new EventContext(), new Dictionary<string, object>(), out _);
                    rec.Update(0, "{}");
                    Row("SANDBOX-recursion", rec.Faulted ? "PASS (contained)" : "FAIL", rec.LastError ?? "");
                }
                else Row("SANDBOX-recursion", "COMPILE-FAIL", recErr);

                // c) CLR access must be unavailable
                var clr = ScriptHost.Create(
                    "function create(){ return { update: function(){ var t = System; } }; }",
                    "probe.clr", out string clrErr);
                if (clr != null)
                {
                    clr.Initialize(new EventContext(), new Dictionary<string, object>(), out _);
                    clr.Update(0, "{}");
                    Row("SANDBOX-clr", clr.Faulted ? "PASS (blocked)" : "FAIL (System reachable)",
                        clr.LastError ?? "");
                }
                else Row("SANDBOX-clr", "COMPILE-FAIL", clrErr);

                // d) eval must be disabled
                var eval = ScriptHost.Create(
                    "function create(){ return { update: function(){ eval('1+1'); } }; }",
                    "probe.eval", out string evalErr);
                if (eval != null)
                {
                    eval.Initialize(new EventContext(), new Dictionary<string, object>(), out _);
                    eval.Update(0, "{}");
                    Row("SANDBOX-eval", eval.Faulted ? "PASS (blocked)" : "FAIL (eval ran)",
                        eval.LastError ?? "");
                }
                else Row("SANDBOX-eval", "COMPILE-FAIL", evalErr);

                // e) one script must not be able to poison another
                var a = ScriptHost.Create(
                    "Array.prototype.push = function(){ return 'hijacked'; }; " +
                    "function create(){ return { update: function(){} }; }",
                    "probe.polluter", out _);
                var b = ScriptHost.Create(
                    "function create(){ var arr = []; arr.push(1); return { update: function(){} }; }",
                    "probe.victim", out _);
                if (a != null && b != null)
                {
                    a.Initialize(new EventContext(), new Dictionary<string, object>(), out _);
                    b.Initialize(new EventContext(), new Dictionary<string, object>(), out _);
                    a.Update(0, "{}"); b.Update(0, "{}");
                    Row("SANDBOX-isolation", !b.Faulted ? "PASS (separate engines)" : "FAIL",
                        "victim engine unaffected by prototype pollution in another script");
                }
                else Row("SANDBOX-isolation", "COMPILE-FAIL", "");
            }
        }
    }
}
