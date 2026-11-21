using System;
using PEV.Scripting;
using UnityEngine;

namespace PEV.Editor
{
    /// <summary>
    /// Histogram, RGB parade, waveform and vectorscope for the frame the grade is producing.
    ///
    /// These read the same downscaled copy of the pipeline's input that the driver keeps while
    /// the panel is open, so they show what is actually being graded rather than a second
    /// render of the game. The analysis runs at a few hertz into small textures: a scope that
    /// costs frame time would be measuring the wrong thing.
    ///
    /// Everything here is display-referred, matching how the grade itself is authored.
    /// </summary>
    internal sealed class ScreenScopeView : IDisposable
    {
        private const int Bins = 256;
        private const int ScopeHeight = 128;
        private const float RefreshSeconds = 0.25f;

        private readonly Texture2D _histogram;
        private readonly Texture2D _parade;
        private readonly Texture2D _waveform;
        private readonly Texture2D _vectorscope;
        private readonly Color32[] _histogramPixels = new Color32[Bins * ScopeHeight];
        private readonly Color32[] _paradePixels = new Color32[Bins * ScopeHeight * 3];
        private readonly Color32[] _waveformPixels = new Color32[Bins * Bins];
        private readonly Color32[] _vectorPixels = new Color32[Bins * Bins];
        private readonly int[] _red = new int[Bins];
        private readonly int[] _green = new int[Bins];
        private readonly int[] _blue = new int[Bins];
        private readonly int[] _luma = new int[Bins];
        private readonly int[] _wave = new int[Bins * Bins];
        private readonly int[] _vector = new int[Bins * Bins];

        private Texture2D _readback;
        private float _lastCapture = -1f;
        private bool _valid;

        internal ScreenScopeView()
        {
            _histogram = MakeTexture("PEV.Scope.Histogram", Bins, ScopeHeight);
            _parade = MakeTexture("PEV.Scope.Parade", Bins * 3, ScopeHeight);
            _waveform = MakeTexture("PEV.Scope.Waveform", Bins, Bins);
            _vectorscope = MakeTexture("PEV.Scope.Vector", Bins, Bins);
        }

        private static Texture2D MakeTexture(string name, int width, int height)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false)
            {
                name = name,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.DontSave
            };
            var clear = new Color32[width * height];
            for (int i = 0; i < clear.Length; i++) clear[i] = new Color32(8, 9, 12, 255);
            texture.SetPixels32(clear);
            texture.Apply(false, false);
            return texture;
        }

        internal bool Ready => _valid;

        /// <summary>
        /// Refreshes the scopes no faster than four times a second. Reading a render target
        /// stalls the GPU, so doing it per frame would make the panel the most expensive part
        /// of the editor.
        /// </summary>
        internal void Refresh()
        {
            var source = ScreenEffectPipeline.ScopeTarget;
            if (source == null) { _valid = false; return; }
            if (Time.realtimeSinceStartup - _lastCapture < RefreshSeconds) return;
            _lastCapture = Time.realtimeSinceStartup;

            try
            {
                int width = source.width, height = source.height;
                if (_readback == null || _readback.width != width || _readback.height != height)
                {
                    if (_readback != null) UnityEngine.Object.Destroy(_readback);
                    _readback = new Texture2D(width, height, TextureFormat.RGBA32, false)
                    {
                        hideFlags = HideFlags.DontSave
                    };
                }

                var previous = RenderTexture.active;
                try
                {
                    RenderTexture.active = source;
                    _readback.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                    _readback.Apply(false);
                }
                finally { RenderTexture.active = previous; }

                Analyse(_readback.GetPixels32(), width, height);
                _valid = true;
            }
            catch (Exception e)
            {
                PEV.Core.Plugin.Log("scope refresh failed: " + e.Message);
                _valid = false;
            }
        }

        private void Analyse(Color32[] pixels, int width, int height)
        {
            Array.Clear(_red, 0, _red.Length);
            Array.Clear(_green, 0, _green.Length);
            Array.Clear(_blue, 0, _blue.Length);
            Array.Clear(_luma, 0, _luma.Length);
            Array.Clear(_wave, 0, _wave.Length);
            Array.Clear(_vector, 0, _vector.Length);

            int count = Mathf.Min(pixels.Length, width * height);
            for (int i = 0; i < count; i++)
            {
                var pixel = pixels[i];
                _red[pixel.r]++;
                _green[pixel.g]++;
                _blue[pixel.b]++;
                int luma = (int)(0.2126f * pixel.r + 0.7152f * pixel.g + 0.0722f * pixel.b);
                _luma[Mathf.Clamp(luma, 0, 255)]++;

                // Waveform: x is the horizontal position in the frame, y the luminance.
                int x = (i % width) * Bins / Mathf.Max(width, 1);
                int y = Mathf.Clamp(luma, 0, Bins - 1);
                _wave[y * Bins + Mathf.Clamp(x, 0, Bins - 1)]++;

                // Vectorscope: chroma plane, scaled so saturated primaries sit near the rim.
                float r = pixel.r / 255f, g = pixel.g / 255f, b = pixel.b / 255f;
                float cb = -0.1146f * r - 0.3854f * g + 0.5f * b;
                float cr = 0.5f * r - 0.4542f * g - 0.0458f * b;
                int vx = Mathf.Clamp((int)((cb * 2f + 0.5f) * Bins), 0, Bins - 1);
                // Red sits at the top of a vectorscope, blue at the bottom.
                int vy = Mathf.Clamp((int)((cr * 2f + 0.5f) * Bins), 0, Bins - 1);
                _vector[vy * Bins + vx]++;
            }

            FillHistogram();
            FillParade();
            FillWaveform();
            FillVectorscope();
        }

        private static float Normalise(int value, int peak)
        {
            if (peak <= 0 || value <= 0) return 0f;
            // Logarithmic: a chart background is usually one huge spike at or near black, and
            // a linear or square-root scale hides everything the grade is doing elsewhere.
            return Mathf.Log(1f + value) / Mathf.Log(1f + peak);
        }

        private void FillHistogram()
        {
            int peak = 1;
            for (int i = 0; i < Bins; i++)
                peak = Mathf.Max(peak, Mathf.Max(_red[i], Mathf.Max(_green[i], _blue[i])));

            Array.Clear(_histogramPixels, 0, _histogramPixels.Length);
            for (int x = 0; x < Bins; x++)
            {
                int hr = Mathf.Clamp(Mathf.RoundToInt(Normalise(_red[x], peak) * ScopeHeight), 0, ScopeHeight);
                int hg = Mathf.Clamp(Mathf.RoundToInt(Normalise(_green[x], peak) * ScopeHeight), 0, ScopeHeight);
                int hb = Mathf.Clamp(Mathf.RoundToInt(Normalise(_blue[x], peak) * ScopeHeight), 0, ScopeHeight);
                for (int y = 0; y < ScopeHeight; y++)
                {
                    // Additive so overlapping channels read as white, which is how a colour
                    // histogram is normally displayed.
                    int r = y < hr ? 200 : 0;
                    int g = y < hg ? 200 : 0;
                    int b = y < hb ? 200 : 0;
                    // Row 0 is the bottom of the texture in Unity's convention, so a bar's
                    // base sits at row 0 and grows upward.
                    int index = y * Bins + x;
                    _histogramPixels[index] = new Color32(
                        (byte)Mathf.Min(255, 10 + r),
                        (byte)Mathf.Min(255, 10 + g),
                        (byte)Mathf.Min(255, 10 + b), 255);
                }
            }
            _histogram.SetPixels32(_histogramPixels);
            _histogram.Apply(false, false);
        }

        private void FillParade()
        {
            int peak = 1;
            for (int i = 0; i < Bins; i++)
                peak = Mathf.Max(peak, Mathf.Max(_red[i], Mathf.Max(_green[i], _blue[i])));

            var channels = new[] { _red, _green, _blue };
            var colours = new[] { new Color32(255, 70, 70, 255), new Color32(80, 255, 90, 255),
                new Color32(80, 120, 255, 255) };
            for (int y = 0; y < ScopeHeight; y++)
                for (int x = 0; x < Bins * 3; x++)
                    _paradePixels[y * Bins * 3 + x] = new Color32(8, 9, 12, 255);

            for (int channel = 0; channel < 3; channel++)
            {
                var bins = channels[channel];
                for (int x = 0; x < Bins; x++)
                {
                    int height = Mathf.Clamp(Mathf.RoundToInt(Normalise(bins[x], peak) * ScopeHeight), 0, ScopeHeight);
                    for (int y = 0; y < height; y++)
                    {
                        int index = y * Bins * 3 + channel * Bins + x;
                        _paradePixels[index] = colours[channel];
                    }
                }
            }
            _parade.SetPixels32(_paradePixels);
            _parade.Apply(false, false);
        }

        private void FillWaveform()
        {
            int peak = 1;
            foreach (var value in _wave) peak = Mathf.Max(peak, value);

            for (int i = 0; i < _waveformPixels.Length; i++)
            {
                float intensity = Normalise(_wave[i], peak);
                byte level = (byte)Mathf.Clamp(10 + intensity * 235f, 0, 255);
                _waveformPixels[i] = new Color32(level, level, level, 255);
            }
            _waveform.SetPixels32(_waveformPixels);
            _waveform.Apply(false, false);
        }

        private void FillVectorscope()
        {
            // A crosshair and the neutral axis make the plot readable; without them a blob in
            // the middle says nothing about which way the grade pushed the chroma.
            for (int i = 0; i < _vectorPixels.Length; i++) _vectorPixels[i] = new Color32(8, 9, 12, 255);
            for (int i = 0; i < Bins; i++)
            {
                _vectorPixels[(Bins / 2) * Bins + i] = new Color32(34, 36, 42, 255);
                _vectorPixels[i * Bins + Bins / 2] = new Color32(34, 36, 42, 255);
            }

            int peak = 1;
            foreach (var value in _vector) peak = Mathf.Max(peak, value);
            for (int i = 0; i < _vector.Length; i++)
            {
                if (_vector[i] == 0) continue;
                float intensity = Normalise(_vector[i], peak);
                byte level = (byte)Mathf.Clamp(20 + intensity * 235f, 0, 255);
                _vectorPixels[i] = new Color32(level, level, level, 255);
            }
            _vectorscope.SetPixels32(_vectorPixels);
            _vectorscope.Apply(false, false);
        }

        internal void Draw(float width)
        {
            var half = (width - 8f) * 0.5f;
            GUILayout.BeginHorizontal();
            DrawScope(_histogram, "Histogram", half);
            DrawScope(_parade, "RGB parade", half);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            DrawScope(_waveform, "Waveform", half);
            DrawScope(_vectorscope, "Vectorscope", half);
            GUILayout.EndHorizontal();
        }

        /// <summary>Diagnostics: writes each scope at 1:1 so the drawing can be inspected.</summary>
#if PEV_DIAGNOSTICS
        internal void Dump(string directory)
        {
            Dump(_histogram, System.IO.Path.Combine(directory, "scope-histogram.png"));
            Dump(_parade, System.IO.Path.Combine(directory, "scope-parade.png"));
            Dump(_waveform, System.IO.Path.Combine(directory, "scope-waveform.png"));
            Dump(_vectorscope, System.IO.Path.Combine(directory, "scope-vector.png"));
        }

        private static void Dump(Texture2D texture, string path)
        {
            var png = Scripting.NoiseTextures.EncodePng(texture.GetPixels32(), texture.width, texture.height);
            if (png != null && png.Length > 0) System.IO.File.WriteAllBytes(path, png);
        }
#endif

        private static void DrawScope(Texture texture, string label, float width)
        {
            GUILayout.BeginVertical(GUILayout.Width(width));
            GUILayout.Label(label, new GUIStyle(GUI.skin.label) { fontSize = 10 });
            float height = width * 0.5f;
            var rect = GUILayoutUtility.GetRect(width, height);
            GUI.DrawTexture(rect, texture, ScaleMode.StretchToFill, false);
            GUILayout.EndVertical();
        }

        public void Dispose()
        {
            UnityEngine.Object.Destroy(_histogram);
            UnityEngine.Object.Destroy(_parade);
            UnityEngine.Object.Destroy(_waveform);
            UnityEngine.Object.Destroy(_vectorscope);
            if (_readback != null) UnityEngine.Object.Destroy(_readback);
            _readback = null;
        }
    }
}
