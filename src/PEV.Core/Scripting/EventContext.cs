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
        private static Transform _root;

        public static Transform Root
        {
            get
            {
                if (_root == null)
                {
                    var go = new GameObject("PEV.Effects");
                    UnityEngine.Object.DontDestroyOnLoad(go);
                    _root = go.transform;
                }
                return _root;
            }
        }

        public static void DestroyAll()
        {
            try { ContinuousEffects.DestroyAll(); } catch { }
            if (_root == null) return;
            for (int i = _root.childCount - 1; i >= 0; i--)
                UnityEngine.Object.Destroy(_root.GetChild(i).gameObject);
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
