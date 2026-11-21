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

        /// <summary>
        /// Song time at which this instance's own tile is reached.
        ///
        /// Equal to EventTime for ordinary events. It differs for a package anchored at the
        /// level start (a video decoration), where the clock origin is 0 but "play from this
        /// tile" still has to name a specific moment.
        /// </summary>
        public double TileTime;
        public bool IsPlaying;
        public bool IsPaused;

        public Dictionary<string, object> Params;
        public Dictionary<string, object> Settings;
        internal int EventOrder;

        public string ToStateJson()
        {
            var state = new Dictionary<string, object>
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
                { "tileTime", TileTime },
                { "isPlaying", IsPlaying },
                { "isPaused", IsPaused }
            };
            if (Settings != null) state["settings"] = Settings;
            return JsonConvert.SerializeObject(state);
        }
    }

    /// <summary>
    /// The effect surface reachable from scripts. Everything created here hangs off a
    /// core-owned root so teardown can reclaim it unconditionally, even after a script
    /// throws or forgets to clean up.
    /// </summary>
    public static class GameBridge
    {
        internal static readonly TaggedValues Values = new TaggedValues();
        /// <summary>
        /// Everything a script can spawn lives on the effect canvas: its own layer, drawn
        /// by a camera above the game's stack. Parenting to the gameplay camera instead
        /// produced objects that existed at the right place but were painted over.
        /// </summary>
        public static Transform Root => EffectCanvas.Root;

        public static void DestroyAll()
        {
            Values.Clear();
            try { ScreenEffectPipeline.ClearAll(); } catch { }
            try { ContinuousEffects.DestroyAll(); } catch { }
            // Before the canvas: texture release walks our own bookkeeping, and doing it
            // first keeps the ordering explicit if a texture ever outlives its object.
            try { NoiseFieldRenderer.DestroyAll(); } catch { }
            try { VideoSurface.DestroyAll(); } catch { }
            try { EffectCanvas.DestroyAll(); } catch { }
            var cam = GameCamera.Current;
            if (cam != null) cam.GetComponent<ShakeHost>()?.Stop();
        }

        internal static string ScopedKey(string scope, string key) =>
            string.IsNullOrEmpty(scope) ? key : scope.Length + ":" + scope + "/" + key;

        public static void DestroyScope(string scope)
        {
            Values.Remove(scope);
            try { ScreenEffectPipeline.Clear(scope); } catch { }
            ContinuousEffects.DestroyScope(scope);
            NoiseFieldRenderer.DestroyScope(scope);
            try { VideoSurface.DestroyScope(scope); } catch { }
            if (!string.IsNullOrEmpty(scope))
            {
                foreach (var ticker in Root.GetComponentsInChildren<EffectTicker>())
                    if (ticker.Scope == scope) UnityEngine.Object.Destroy(ticker.gameObject);
                var cam = GameCamera.Current;
                var shake = cam != null ? cam.GetComponent<ShakeHost>() : null;
                if (shake != null && shake.Scope == scope) shake.Stop();
            }
        }

        /// <summary>Whitelist dispatch. Unknown names fail loudly instead of silently doing nothing.</summary>
        public static bool TryPlayNative(string effectName, Dictionary<string, object> args, out string error)
        {
            switch ((effectName ?? "").ToLowerInvariant())
            {
                case "flash": return NativeEffects.Flash(args, out error);
                case "shake":
                case "screenshake": return NativeEffects.Shake(args, out error);
                case "create": return ContinuousEffects.Create(EffectKey(args), args, out error);
                case "set": return ContinuousEffects.Set(EffectKey(args), args, out error);
                case "destroy": return ContinuousEffects.Destroy(EffectKey(args), out error);
                case "tagvalue": return SetTaggedValue(args, out error);

                // Noise fields are a host primitive, not part of the author-facing SDK; the
                // public contract is the .event package format, so this surface may change.
                case "field": return NoiseFieldRenderer.Set(Str(args, "key"), args, out error);
                case "fielddestroy": return NoiseFieldRenderer.Destroy(Str(args, "key"), args, out error);
                case "fieldmove": return FieldOverride(args, effectName, out error);

                // Video surfaces are the same kind of host primitive: decoding, texture
                // ownership and the decoration binding live in Core, while the package
                // decides what time to show. Not part of the author-facing SDK either.
                case "video": return VideoSurface.Set(Str(args, "key"), args, out error);
                case "videodestroy": return VideoSurface.Destroy(Str(args, "key"), args, out error);

                // A package-declared full-screen post-processing stage. Core owns the render
                // hook and uniform transport; the shader and every value in it belong to the
                // package, so this surface stays free of any particular effect.
                case "screen": return ScreenEffect(Str(args, "__instance"), args, out error);
                case "screenclear":
                {
                    ScreenEffectPipeline.Clear(Str(args, "__instance"));
                    error = null;
                    return true;
                }
                default:
                    error = $"unsupported effect '{effectName}'";
                    return false;
            }
        }

        private static string EffectKey(Dictionary<string, object> args)
        {
            string key = Str(args, "key");
            return string.IsNullOrEmpty(key) ? key : ScopedKey(Str(args, "__instance"), key);
        }

        /// <summary>
        /// Applies one full-screen stage. The manifest is stamped by the script host, so a
        /// package can only ever drive a shader it declared itself.
        /// </summary>
        private static bool ScreenEffect(string scope, Dictionary<string, object> args, out string error)
        {
            try
            {
                args.TryGetValue("__manifest", out var owner);
                args.TryGetValue("__payload", out var payload);
                var flattened = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "__payload", payload }
                };
                return ScreenEffectPipeline.Set(scope, owner as PEV.Model.EventManifest, flattened, out error);
            }
            catch (System.Exception e) { error = "screen: " + e.Message; return false; }
        }

        private static bool SetTaggedValue(Dictionary<string, object> args, out string error)
        {
            try
            {
                if (!args.TryGetValue("__manifest", out var owner) || !(owner is PEV.Model.EventManifest manifest))
                    throw new ArgumentException("A package owner is required");
                Values.Set(Str(args, "__instance"), manifest.SourcePath, Str(args, "key"), Str(args, "tag"),
                    Str(args, "value"), Convert.ToDouble(args["__eventTime"]), Convert.ToInt32(args["__eventOrder"]));
                error = null; return true;
            }
            catch (Exception e) { error = "tagvalue: " + e.Message; return false; }
        }

        /// <summary>
        /// Tag-scoped override of live fields. Only the event that owns the tag writes it,
        /// and it writes absolute values derived from the song clock, so pause and seeks
        /// cannot drift.
        /// </summary>
        private static bool FieldOverride(Dictionary<string, object> args, string name, out string error)
        {
            error = null;
            string tag = Str(args, "tag");
            if (string.IsNullOrEmpty(tag)) { error = name + " needs a tag"; return false; }

            bool clear = Bool(args, "clear", false);
            var offset = Vec2(args, "offset", Vector2.zero);
            float rotation = Flt(args, "rotation", 0f);
            float scale = Flt(args, "scale", 1f);
            float alpha = Flt(args, "alpha", 1f);
            int visibility = (int)Flt(args, "visibility", 0f);

            int hit = NoiseFieldRenderer.ApplyOverrides(tag, offset, rotation, scale, alpha, visibility, clear);
            if (hit == 0 && !clear)
            {
                // Not an error: a field with that tag may simply not have started yet.
                return true;
            }
            return true;
        }

        private static bool Bool(Dictionary<string, object> args, string key, bool fallback)
        {
            if (args == null || !args.TryGetValue(key, out var v) || v == null) return fallback;
            if (v is bool b) return b;
            try { return Convert.ToBoolean(v); } catch { return fallback; }
        }

        private static float Flt(Dictionary<string, object> args, string key, float fallback)
        {
            if (args == null || !args.TryGetValue(key, out var v) || v == null) return fallback;
            try { return Convert.ToSingle(v); } catch { return fallback; }
        }

        private static Vector2 Vec2(Dictionary<string, object> args, string key, Vector2 fallback)
        {
            if (args == null || !args.TryGetValue(key, out var v) || v == null) return fallback;
            try
            {
                if (v is System.Collections.IList list && list.Count >= 2)
                    return new Vector2(Convert.ToSingle(list[0]), Convert.ToSingle(list[1]));
                if (v is Vector2 vec) return vec;
            }
            catch { }
            return fallback;
        }

        private static string Str(Dictionary<string, object> args, string key)
        {
            if (args != null && args.TryGetValue(key, out var v) && v != null) return v.ToString();
            return null;
        }
    }
}
