using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;

namespace PEV.Scripting
{
    /// <summary>
    /// The state a script sees. Plain data only — no game types cross the boundary,
    /// so renaming ADOFAI internals never changes this contract.
    /// </summary>
    public class EventContext
    {
        public double Time;
        public double EventTime;
        public double T;
        public double Beat;
        public double Bpm = 120;
        public int FloorSeq;
        public bool IsPlaying;
        public bool IsPaused;

        public Dictionary<string, object> Params;

        public string ToStateJson()
        {
            return JsonConvert.SerializeObject(new Dictionary<string, object>
            {
                { "time", Time },
                { "eventTime", EventTime },
                { "t", T },
                { "beat", Beat },
                { "bpm", Bpm },
                // Seconds per beat, so a script can turn a "beats" parameter into seconds
                // without assuming a tempo.
                { "secondsPerBeat", Bpm > 0 ? 60.0 / Bpm : 0.5 },
                { "floorSeq", FloorSeq },
                { "isPlaying", IsPlaying },
                { "isPaused", IsPaused }
            });
        }
    }

    /// <summary>
    /// The effect surface reachable from scripts. Everything created here hangs off a
    /// core-owned root so teardown can reclaim it unconditionally, even after a script
    /// throws or forgets to clean up.
    /// </summary>
    public static class GameBridge
    {
        /// <summary>
        /// Everything a script can spawn lives on the effect canvas: its own layer, drawn
        /// by a camera above the game's stack. Parenting to the gameplay camera instead
        /// produced objects that existed at the right place but were painted over.
        /// </summary>
        public static Transform Root => EffectCanvas.Root;

        public static void DestroyAll()
        {
            try { ContinuousEffects.DestroyAll(); } catch { }
            try { EffectCanvas.DestroyAll(); } catch { }
        }

        /// <summary>Whitelist dispatch. Unknown names fail loudly instead of silently doing nothing.</summary>
        public static bool TryPlayNative(string effectName, Dictionary<string, object> args, out string error)
        {
            switch ((effectName ?? "").ToLowerInvariant())
            {
                case "flash": return NativeEffects.Flash(args, out error);
                case "shake":
                case "screenshake": return NativeEffects.Shake(args, out error);
                case "create": return ContinuousEffects.Create(Str(args, "key"), args, out error);
                case "set": return ContinuousEffects.Set(Str(args, "key"), args, out error);
                case "destroy": return ContinuousEffects.Destroy(Str(args, "key"), out error);
                default:
                    error = $"unsupported effect '{effectName}'";
                    return false;
            }
        }

        private static string Str(Dictionary<string, object> args, string key)
        {
            if (args != null && args.TryGetValue(key, out var v) && v != null) return v.ToString();
            return null;
        }
    }
}
