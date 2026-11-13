using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace PEV.Scripting
{
    /// <summary>
    /// Small self-contained effects. These are deliberately not the game's ffx components:
    /// those expect the LevelEvent pipeline to hand them a scrPlanet and are tied to
    /// vanilla event scheduling. P1 needs effects that an arbitrary script can drive.
    /// </summary>
    public static class NativeEffects
    {
        public static bool Flash(Dictionary<string, object> args, out string error)
        {
            error = null;
            try
            {
                Color color = ParseColor(args, "color", Color.white);
                float duration = ParseFloat(args, "duration", 0.3f);
                float alpha = ParseFloat(args, "alpha", 0.6f);

                var go = EffectCanvas.CreateObject("PEV.Flash");
                var sr = go.AddComponent<SpriteRenderer>();
                sr.sprite = SolidSprite.White;
                sr.sortingOrder = short.MaxValue;

                var cam = GameCamera.Current;
                if (cam != null)
                {
                    // Put it just in front of the camera and size it to the viewport.
                    go.transform.position = cam.transform.position + cam.transform.forward * 0.5f;

                    // Match the effect camera's framing so the quad covers the screen.
                    float halfH = cam.orthographic
                        ? cam.orthographicSize
                        : Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
                    float halfW = halfH * cam.aspect;
                    go.transform.localScale = new Vector3(halfW * 2f + 1f, halfH * 2f + 1f, 1f);
                }

                var ticker = go.AddComponent<EffectTicker>();
                ticker.Begin(duration, p =>
                {
                    var c = color;
                    c.a = Mathf.Lerp(alpha, 0f, p);
                    sr.color = c;
                }, () => UnityEngine.Object.Destroy(go));
                return true;
            }
            catch (Exception e)
            {
                error = "flash failed: " + e.Message;
                return false;
            }
        }

        public static bool Shake(Dictionary<string, object> args, out string error)
        {
            error = null;
            try
            {
                float strength = ParseFloat(args, "strength", 0.2f);
                float duration = ParseFloat(args, "duration", 0.4f);

                var cam = GameCamera.Current;
                if (cam == null) { error = "no main camera"; return false; }

                var host = cam.gameObject.GetComponent<ShakeHost>();
                if (host == null) host = cam.gameObject.AddComponent<ShakeHost>();
                host.Play(strength, duration);
                return true;
            }
            catch (Exception e)
            {
                error = "shake failed: " + e.Message;
                return false;
            }
        }

        private static float ParseFloat(Dictionary<string, object> args, string key, float fallback)
        {
            if (args != null && args.TryGetValue(key, out var v) && v != null)
            {
                try { return Convert.ToSingle(v); } catch { }
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
    }

    /// <summary>Drives a per-frame callback with a normalized 0..1 progress.</summary>
    public class EffectTicker : MonoBehaviour
    {
        private float _duration;
        private float _elapsed;
        private Action<float> _onTick;
        private Action _onDone;
        private bool _running;

        public void Begin(float duration, Action<float> onTick, Action onDone)
        {
            _duration = Mathf.Max(0.0001f, duration);
            _elapsed = 0f;
            _onTick = onTick;
            _onDone = onDone;
            _running = true;
        }

        private void Update()
        {
            if (!_running) return;
            _elapsed += Time.unscaledDeltaTime;
            float p = Mathf.Clamp01(_elapsed / _duration);
            try { _onTick?.Invoke(p); } catch { }
            if (p >= 1f)
            {
                _running = false;
                try { _onDone?.Invoke(); } catch { }
            }
        }
    }

    /// <summary>Camera shake that borrows the camera transform and always restores it.</summary>
    public class ShakeHost : MonoBehaviour
    {
        private Vector3 _basePos;
        private bool _captured;
        private float _remaining;
        private float _strength;

        public void Play(float strength, float duration)
        {
            if (!_captured)
            {
                _basePos = transform.localPosition;
                _captured = true;
            }
            _strength = strength;
            _remaining = duration;
        }

        private void LateUpdate()
        {
            if (!_captured) return;
            if (_remaining > 0f)
            {
                _remaining -= Time.unscaledDeltaTime;
                float falloff = Mathf.Clamp01(_remaining);
                var offset = UnityEngine.Random.insideUnitCircle * (_strength * falloff);
                transform.localPosition = _basePos + new Vector3(offset.x, offset.y, 0f);
                if (_remaining <= 0f) transform.localPosition = _basePos;
            }
        }
    }

    /// <summary>A 1x1 white sprite, created once on demand.</summary>
    public static class SolidSprite
    {
        private static Sprite _white;

        public static Sprite White
        {
            get
            {
                if (_white == null)
                {
                    var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                    tex.SetPixel(0, 0, Color.white);
                    tex.Apply();
                    _white = Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 1f);
                    _white.name = "PEV.Solid";
                }
                return _white;
            }
        }
    }
}
