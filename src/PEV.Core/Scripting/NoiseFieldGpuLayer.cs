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

        internal static float TouchShine(float time) =>
            (0.63f + 0.37f * (Mathf.Sin(time * 43f) * 0.5f + 0.5f)) * 2f;

        private readonly GameObject _root;
        private readonly SortingGroup _sorting;
        private readonly SpriteRenderer _disabled, _active;
        private readonly Material[] _materials;
        private NoiseFieldActivationGate _activation, _disabledActivation, _activeActivation;

        internal NoiseFieldGpuLayer(EventManifest manifest, int sortingLayer, int sortingOrder, int unityLayer)
        {
            var materials = new Material[10];
            GameObject root = null;
            try
            {
                string[] names = { "PEVNoise_100_disabled", "PEVNoise_000_capture", "PEVNoise_010_capture",
                                   "PEVNoise_000_nocapture", "PEVNoise_010_nocapture",
                                   "PEVNoise_101_disabled", "PEVNoise_001_capture", "PEVNoise_011_capture",
                                   "PEVNoise_001_nocapture", "PEVNoise_011_nocapture" };
                for (int i = 0; i < names.Length; i++)
                {
                    string path = "assets/generated/" + names[i] + ".shader";
                    Shader shader = ShaderBundleCache.Load(manifest, path, out string error);
                    if (shader == null) throw new InvalidOperationException(error);
                    materials[i] = new Material(shader) { name = "PEV." + names[i] };
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
            }
            catch
            {
                if (root != null) UnityEngine.Object.Destroy(root);
                foreach (var material in materials) if (material != null) UnityEngine.Object.Destroy(material);
                throw;
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

        private static void Configure(Material m, Camera camera, NoiseFieldGpuMasks masks, Texture2D displacement,
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
            m.SetFloat("_EdgeOpacity", 0.8f); m.SetFloat("_FillStrength", 0.667f);
            m.SetFloat("_FillOpacity", 0.667f); m.SetFloat("_GlowIntensity", 0.8f);
            m.SetFloat("_SparkMapOpacity", 5.69f); m.SetFloat("_SparkHueShiftAmount", 0.2f);
            m.SetFloat("_SparkDisplaceIntensity", 2.39f); m.SetFloat("_DisplaceBlendIntensity", 0.411f);
            m.SetFloat("_DisplaceSpeed", 1.5f); m.SetFloat("_DisplaceStrength", 0.15f);
            m.SetFloat("_TouchPosShine", TouchShine(time)); m.SetFloat("_TouchPosRadius", 0.5f);
            m.SetFloat("_TouchPosSDFSmoothness", 0.47f); m.SetFloat("_TouchPosSDFFalloff", 0.41f);
            m.SetFloat("_BackgroundPixelScale", 6f); m.SetFloat("uDisabledFillOpacity", 0.4f);
            m.SetFloat("uDisabledSparkOpacity", 3.5f); m.SetFloat("uDisabledSparkIntensity", 2.29f);
            m.SetFloat("uDisabledSpeed", 0.3f); m.SetFloat("_ShineSpeed", 37.9f);
            m.SetFloat("_ShineBrightness", 0.12f); m.SetFloat("_TouchDisplaceSpeed", 2.9f);
            m.SetFloat("_TouchDisplaceStrength", 0.08f); m.SetFloat("_NoiseEvoSpeed", 0.03f);
            m.SetFloat("_NoiseDirChangeSpeed", 60f); m.SetFloat("_NoiseDisplaceStrength", 1f);
            m.SetFloat("_NoiseRadius", 0.48f); m.SetFloat("_NoiseSmoothness", 1f);
            m.SetFloat("_SDFCellSize", 0.11f); m.SetFloat("_SDFSmoothness", 0.63f);
            m.SetFloat("_SDFFalloff", 0.34f); m.SetFloat("_SDFMoveSpeed", 9.3f);
            m.SetFloat("_TouchBackgroundPixelScale", 8f); m.SetFloat("uReadyExtra", 0f);
            m.SetVector("_EdgeColor", new Vector4(1f, 0.33018857f, 0.33018857f, 1f));
            m.SetVector("_FillColor", new Vector4(0.7132075f, 0.23549296f, 0.23549296f, 1f));
            m.SetVector("_GlowColor", new Vector4(1f, 0.17924517f, 0.17924517f, 1f));
            m.SetVector("_DisplaceDirection", new Vector4(1f, 1f, 0f, 0f));
            m.SetVector("uDisabledFillColor", new Vector4(0.497f, 0.13766898f, 0.13766898f, 1f));
            m.SetVector("_ShineColor", Vector4.one); m.SetVector("_TouchDisplaceDirection", new Vector4(1f, 1f, 0f, 0f));
            m.SetVector("_NoiseTint", new Vector4(1f, 0f, 0f, 1f)); m.SetVector("_TouchGlowColor", new Vector4(1f, 0f, 0f, 1f));
            m.SetVector("_SparkTint", new Vector4(1f, 0.28490567f, 0.28490567f, 0f));
            m.SetVector("uDisabledSparkTint", new Vector4(0.31132078f, 0.077830195f, 0.077830195f, 0f));
        }

        public void Dispose()
        {
            if (_root != null) UnityEngine.Object.Destroy(_root);
            foreach (var material in _materials) if (material != null) UnityEngine.Object.Destroy(material);
        }
    }
}
