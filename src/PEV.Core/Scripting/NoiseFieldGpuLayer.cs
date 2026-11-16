using System;
using PEV.Model;
using UnityEngine;
using UnityEngine.Rendering;

namespace PEV.Scripting
{
    /// <summary>
    /// Draft fullscreen composition at one native sorting position. Not used by the
    /// production renderer until the Unity shader bundle and depth behavior are tested.
    /// </summary>
    internal sealed class NoiseFieldGpuLayer : IDisposable
    {
        // The reference evaluates touch distances in screen-aspect-corrected UV.
        internal static Vector4 TouchPosition(Vector2 uv, int width, int height) =>
            new Vector4(uv.x * ((float)width / height), uv.y, 0f, 0f);

        private readonly GameObject _root;
        private readonly SortingGroup _sorting;
        private readonly SpriteRenderer _disabled, _active;
        private readonly Material[] _materials;
        private readonly System.Collections.Generic.IReadOnlyList<FieldWaveSpec> _waves;
        private NoiseFieldActivationGate _activation, _disabledActivation, _activeActivation;

        internal NoiseFieldGpuLayer(EventManifest manifest, int sortingLayer, int sortingOrder, int unityLayer)
        {
            var materials = new Material[10];
            GameObject root = null;
            try
            {
                string[] names = { "disabled", "activeCapture", "hoverCapture", "activeNoCapture", "hoverNoCapture",
                                   "disabledColored", "activeCaptureColored", "hoverCaptureColored",
                                   "activeNoCaptureColored", "hoverNoCaptureColored" };
                for (int i = 0; i < names.Length; i++)
                {
                    if (!manifest.FieldShaders.TryGetValue(names[i], out var path))
                        throw new InvalidOperationException("Field shader role is not declared: " + names[i]);
                    Shader shader = ShaderBundleCache.Load(manifest, path, out string error);
                    if (shader == null) throw new InvalidOperationException(error);
                    materials[i] = new Material(shader) { name = "PEV." + names[i] };
                    ApplyUniforms(materials[i], manifest.FieldUniforms);
                }
                root = new GameObject("PEV.NoiseGpuLayer");
                root.layer = unityLayer;
                root.SetActive(false);
                _sorting = root.AddComponent<SortingGroup>();
                _sorting.sortingLayerID = sortingLayer;
                _sorting.sortingOrder = sortingOrder;
                _disabled = CreateRenderer(root.transform, "Disabled", unityLayer, 0);
                _active = CreateRenderer(root.transform, "Active", unityLayer, 1);
                _root = root;
                _materials = materials;
                _waves = manifest.FieldWaves;
            }
            catch
            {
                if (root != null) UnityEngine.Object.Destroy(root);
                foreach (var material in materials) if (material != null) UnityEngine.Object.Destroy(material);
                throw;
            }
        }

        internal static void ApplyUniforms(Material material, System.Collections.Generic.IReadOnlyList<FieldUniformSpec> uniforms)
        {
            foreach (var uniform in uniforms)
            {
                var values = uniform.Values;
                if (values.Length == 1) material.SetFloat(uniform.Name, values[0]);
                else material.SetVector(uniform.Name, new Vector4(values[0], values[1], values[2], values[3]));
            }
        }

        private static SpriteRenderer CreateRenderer(Transform parent, string name, int unityLayer, int order)
        {
            var go = new GameObject(name);
            go.layer = unityLayer;
            go.transform.SetParent(parent, false);
            var renderer = go.AddComponent<SpriteRenderer>();
            renderer.sprite = SolidSprite.White;
            renderer.sortingOrder = order;
            renderer.color = Color.white;
            return renderer;
        }

        internal bool Draw(Camera camera, NoiseFieldGpuMasks masks, Texture2D displacement,
            Texture2D spark, Texture2D noise, float chartAspect, float songTime, bool capture,
            bool hover, bool showDisabled, bool showActive, Color fallbackBackground,
            Vector4[] touchPositions, int touchCount, Texture activeColors, Texture disabledColors)
        {
            if (camera == null || !camera.orthographic || camera.pixelWidth <= 0 || camera.pixelHeight <= 0 ||
                masks == null || masks.Effect == null ||
                masks.Auxiliary == null || displacement == null || spark == null || noise == null)
                throw new ArgumentException("A valid orthographic camera, completed masks and reference textures are required");
            if (touchCount < 0 || touchCount > NoiseFieldTouchHistory.Capacity ||
                (touchCount > 0 && (touchPositions == null || touchPositions.Length < touchCount)))
                throw new ArgumentOutOfRangeException(nameof(touchCount));
            float distance = Mathf.Max(camera.nearClipPlane + 0.1f, 1f);
            if (distance >= camera.farClipPlane) throw new ArgumentException("Camera clipping range cannot contain field quad");
            _root.transform.position = camera.transform.position + camera.transform.forward * distance;
            _root.transform.rotation = camera.transform.rotation;
            float height = camera.orthographicSize * 2f;
            _root.transform.localScale = new Vector3(height * camera.aspect, height, 1f);
            _disabled.enabled = showDisabled;
            _active.enabled = showActive;
            _disabled.color = Color.white;
            _active.color = Color.white;
            _root.SetActive(showDisabled || showActive);
            bool settled = _activation.Observe(showDisabled || showActive, Time.frameCount);
            settled &= _disabledActivation.Observe(showDisabled, Time.frameCount);
            settled &= _activeActivation.Observe(showActive, Time.frameCount);
            if (!showDisabled && !showActive) return true;
            bool colored = activeColors != null && disabledColors != null;
            int offset = colored ? 5 : 0;
            var disabled = _materials[offset];
            var active = _materials[offset + (capture ? (hover ? 2 : 1) : (hover ? 4 : 3))];
            _disabled.sharedMaterial = disabled;
            _active.sharedMaterial = active;
            if (colored)
            {
                disabled.SetTexture("uDisabledColors", disabledColors);
                active.SetTexture("uActiveColors", activeColors);
                active.SetTexture("uDisabledColors", disabledColors);
            }
            if (showDisabled) Configure(disabled, camera, masks, displacement, spark, noise,
                chartAspect, songTime, fallbackBackground, touchPositions, touchCount);
            if (showActive) Configure(active, camera, masks, displacement, spark, noise,
                chartAspect, songTime, fallbackBackground, touchPositions, touchCount);
            return settled;
        }

        // Keep the group and its selected renderers enabled so Unity can register them
        // before the next Draw. Transparent vertex color suppresses their visible output.
        internal void Hide() { _disabled.color = Color.clear; _active.color = Color.clear; }

        private void Configure(Material m, Camera camera, NoiseFieldGpuMasks masks, Texture2D displacement,
            Texture2D spark, Texture2D noise, float aspect, float time, Color fallback, Vector4[] positions, int count)
        {
            float width = camera.pixelWidth, height = camera.pixelHeight;
            m.SetVector("uView", new Vector4(width, height, aspect, 0));
            m.SetVector("uUnityTime", new Vector4(time / 20f, time, time * 2f, time * 3f));
            m.SetVector("_PEVScreenParams", new Vector4(width, height, 1f + 1f / width, 1f + 1f / height));
            m.SetVector("_EffectRT_TexelSize", new Vector4(1f / masks.Effect.width, 1f / masks.Effect.height,
                masks.Effect.width, masks.Effect.height));
            m.SetTexture("uMasks", masks.Effect);
            m.SetTexture("uAuxMasks", masks.Auxiliary);
            m.SetTexture("uDisplaceTex", displacement);
            m.SetTexture("uSparkTex", spark);
            m.SetTexture("uNoiseTex", noise);
            m.SetColor("_PEVBackground", fallback);
            m.SetInt("_TouchPosCount", count);
            if (count > 0) m.SetVectorArray("_TouchPos", positions);
            foreach (var wave in _waves) m.SetFloat(wave.Name, wave.At(time));
        }

        public void Dispose()
        {
            if (_root != null) UnityEngine.Object.Destroy(_root);
            foreach (var material in _materials) if (material != null) UnityEngine.Object.Destroy(material);
        }
    }
}
