using System;
using System.Collections.Generic;
using UnityEngine;

namespace PEV.Scripting
{
    /// <summary>
    /// Continuously driven visual objects, addressed by a script-chosen key.
    ///
    /// The other effects (flash, shake) are fire-and-forget: the host owns their timing.
    /// A continuous effect is the opposite — the script decides what it looks like on every
    /// frame, and the host only stores and applies. That is what makes update(t) useful for
    /// anything more complex than "trigger once".
    ///
    /// Everything lives under GameBridge.Root, so teardown reclaims it unconditionally even
    /// if a script forgets to destroy its object.
    /// </summary>
    public static class ContinuousEffects
    {
        private sealed class Entry
        {
            public GameObject GameObject;
            public SpriteRenderer Renderer;
            public int SetCalls;
            public float LastScale = float.NaN;
            public float LastAlpha = float.NaN;
            public Vector3 LastPosition;
            public Dictionary<string, object> State = new Dictionary<string, object>();
        }

        private static readonly Dictionary<string, Entry> Live =
            new Dictionary<string, Entry>(StringComparer.Ordinal);

        public static int LiveCount => Live.Count;

        /// <summary>Set by the P4 harness to log the resolved camera and placement once.</summary>
        public static bool DiagnoseFirstApply;

        public static bool Exists(string key) => !string.IsNullOrEmpty(key) && Live.ContainsKey(key);

        /// <summary>Diagnostics for the harness: per-key counters and last applied values.</summary>
        public static bool TryGetStats(string key, out int setCalls, out float scale, out float alpha, out Vector3 pos)
        {
            setCalls = 0; scale = 0; alpha = 0; pos = Vector3.zero;
            if (string.IsNullOrEmpty(key) || !Live.TryGetValue(key, out var e)) return false;
            setCalls = e.SetCalls;
            scale = e.LastScale;
            alpha = e.LastAlpha;
            pos = e.LastPosition;
            return true;
        }

        /// <summary>Creates the object if it does not exist yet. Idempotent.</summary>
        public static bool Create(string key, Dictionary<string, object> args, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(key)) { error = "fx.create needs a key"; return false; }

            try
            {
                if (Live.TryGetValue(key, out var existing) && existing.GameObject != null)
                    return true;

                // Must go through the canvas: it assigns the effect layer and parents the
                // object under the root that owns the dedicated camera. Creating a bare
                // GameObject leaves it on layer 0, where the game's own cameras draw over it.
                var go = EffectCanvas.CreateObject("PEV.Fx:" + key);

                var sr = go.AddComponent<SpriteRenderer>();
                sr.sprite = SolidSprite.White;
                sr.sortingOrder = ParseInt(args, "sortingOrder", 1000);

                var entry = new Entry { GameObject = go, Renderer = sr };
                Live[key] = entry;

                Apply(entry, args);
                return true;
            }
            catch (Exception e)
            {
                error = "fx.create failed: " + e.Message;
                return false;
            }
        }

        /// <summary>Applies a new state to an existing object. Called once per script update.</summary>
        public static bool Set(string key, Dictionary<string, object> args, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(key)) { error = "fx.set needs a key"; return false; }

            if (!Live.TryGetValue(key, out var entry) || entry.GameObject == null)
            {
                // Recreate silently: an object removed by teardown should not be an error
                // the script has to guard against on every frame.
                return Create(key, args, out error);
            }

            try
            {
                Apply(entry, args);
                entry.SetCalls++;
                return true;
            }
            catch (Exception e)
            {
                error = "fx.set failed: " + e.Message;
                return false;
            }
        }

        public static bool Destroy(string key, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(key)) return true;
            if (!Live.TryGetValue(key, out var entry)) return true;

            try
            {
                if (entry.GameObject != null) UnityEngine.Object.Destroy(entry.GameObject);
            }
            catch { }
            Live.Remove(key);
            return true;
        }

        public static void DestroyAll()
        {
            foreach (var kv in Live)
            {
                try { if (kv.Value.GameObject != null) UnityEngine.Object.Destroy(kv.Value.GameObject); }
                catch { }
            }
            Live.Clear();
        }

        public static void DestroyScope(string scope)
        {
            if (string.IsNullOrEmpty(scope)) return;
            string prefix = GameBridge.ScopedKey(scope, "");
            var keys = new List<string>();
            foreach (var key in Live.Keys)
                if (key.StartsWith(prefix, StringComparison.Ordinal)) keys.Add(key);
            foreach (var key in keys) Destroy(key, out _);
        }

        private static void Apply(Entry entry, Dictionary<string, object> args)
        {
            if (args == null) return;
            foreach (var kv in args) entry.State[kv.Key] = kv.Value;
            args = entry.State;

            // Values are normalized screen-ish coordinates around the camera centre:
            // x/y in [-1,1] spans half the viewport, which is easier for authors to reason
            // about than world units that change with zoom.
            float x = ParseFloat(args, "x", 0f);
            float y = ParseFloat(args, "y", 0f);
            float scale = ParseFloat(args, "scale", 1f);
            float alpha = ParseFloat(args, "alpha", 1f);
            float angle = ParseFloat(args, "angle", 0f);

            var cam = GameCamera.Current;
            Vector3 pos;
            if (cam != null)
            {
                float halfH = cam.orthographic
                    ? cam.orthographicSize
                    : Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
                float halfW = halfH * cam.aspect;

                pos = cam.transform.position
                      + cam.transform.right * (x * halfW)
                      + cam.transform.up * (y * halfH)
                      + cam.transform.forward * 0.5f;
            }
            else
            {
                pos = new Vector3(x, y, 0f);
            }

            // One-shot diagnostics: proves which camera was chosen and where the sprite
            // landed. Without this a placement bug looks exactly like "no effect".
            if (DiagnoseFirstApply)
            {
                DiagnoseFirstApply = false;
                var c = GameCamera.Current;
                string camInfo = c == null
                    ? "NO CAMERA"
                    : $"{c.name} ortho={c.orthographic} size={(c.orthographic ? c.orthographicSize : c.fieldOfView)} " +
                      $"pos={c.transform.position} fwd={c.transform.forward} aspect={c.aspect:F2} " +
                      $"mask={c.cullingMask} depth={c.depth} near={c.nearClipPlane} far={c.farClipPlane}";

                var rend = entry.Renderer;
                string rendInfo = rend == null ? "NO RENDERER" :
                    $"enabled={rend.enabled} sprite={(rend.sprite != null)} " +
                    $"visible={(c != null && rend.isVisible)} " +
                    $"layer={entry.GameObject.layer} sortingLayer={rend.sortingLayerName}/{rend.sortingOrder} " +
                    $"shader={rend.sharedMaterial?.shader?.name ?? "none"} " +
                    $"color={rend.color} bounds={rend.bounds}";

                PEV.Core.Plugin.Log($"fx apply: cam=[{camInfo}]");
                PEV.Core.Plugin.Log("fx apply: canvas " + EffectCanvas.Describe());
                PEV.Core.Plugin.Log($"fx apply: obj pos={pos} scale={entry.GameObject.transform.localScale} [{rendInfo}]");
            }

            entry.GameObject.transform.position = pos;
            entry.GameObject.transform.localScale = new Vector3(scale, scale, 1f);
            entry.GameObject.transform.rotation = Quaternion.Euler(0f, 0f, angle);
            entry.Renderer.color = ParseColor(args, "color", Color.white).WithAlpha(alpha);

            entry.LastScale = scale;
            entry.LastAlpha = alpha;
            entry.LastPosition = pos;
        }

        private static float ParseFloat(Dictionary<string, object> args, string key, float fallback)
        {
            if (args != null && args.TryGetValue(key, out var v) && v != null)
            {
                try { return Convert.ToSingle(v); } catch { }
            }
            return fallback;
        }

        private static int ParseInt(Dictionary<string, object> args, string key, int fallback)
        {
            if (args != null && args.TryGetValue(key, out var v) && v != null)
            {
                try { return Convert.ToInt32(v); } catch { }
            }
            return fallback;
        }

        private static Color ParseColor(Dictionary<string, object> args, string key, Color fallback)
        {
            if (args == null || !args.TryGetValue(key, out var v) || v == null) return fallback;
            string s = v.ToString();
            if (ColorUtility.TryParseHtmlString(s.StartsWith("#") ? s : "#" + s, out var c)) return c;
            return fallback;
        }

        private static Color WithAlpha(this Color c, float a)
        {
            c.a = Mathf.Clamp01(a);
            return c;
        }
    }
}
