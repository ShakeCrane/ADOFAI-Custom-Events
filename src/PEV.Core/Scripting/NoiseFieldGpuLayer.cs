using System;
using System.Collections.Generic;
using PEV.Model;
using UnityEngine;

namespace PEV.Scripting
{
    // Transitional adapter from field inputs to the package-declared compositor.
    internal sealed class NoiseFieldGpuLayer : IDisposable
    {
        internal static Vector4 TouchPosition(Vector2 uv, int width, int height) =>
            new Vector4(uv.x * ((float)width / height), uv.y, 0f, 0f);
        private readonly SpriteCompositor _compositor;
        private readonly Dictionary<string, bool> _conditions = new Dictionary<string, bool>();
        private readonly Dictionary<string, object> _inputs = new Dictionary<string, object>();
        internal NoiseFieldGpuLayer(EventManifest manifest, CompositorDefinition definition, int sortingLayer, int sortingOrder, int unityLayer)
        {
            _compositor = new SpriteCompositor(manifest, definition, sortingLayer, sortingOrder, unityLayer);
        }
        internal static void ApplyUniforms(Material material, IReadOnlyList<FieldUniformSpec> uniforms) =>
            SpriteCompositor.ApplyUniforms(material, uniforms);
        internal bool Draw(Camera camera, NoiseFieldGpuMasks masks, Texture2D displacement,
            Texture2D spark, Texture2D noise, float chartAspect, float songTime, bool capture,
            bool hover, bool showDisabled, bool showActive, Color fallbackBackground,
            Vector4[] touchPositions, int touchCount, Texture activeColors, Texture disabledColors)
        {
            if (camera == null || !camera.orthographic || camera.pixelWidth <= 0 || camera.pixelHeight <= 0 ||
                masks == null || masks.Effect == null || masks.Auxiliary == null || displacement == null || spark == null || noise == null)
                throw new ArgumentException("A valid camera and completed field resources are required");
            if (touchCount < 0 || touchCount > FieldContactController.Capacity ||
                (touchCount > 0 && (touchPositions == null || touchPositions.Length < touchCount)))
                throw new ArgumentOutOfRangeException(nameof(touchCount));
            float width = camera.pixelWidth, height = camera.pixelHeight;
            _conditions["showSecondary"] = showDisabled; _conditions["showPrimary"] = showActive;
            _conditions["capture"] = capture; _conditions["hover"] = hover;
            _conditions["colored"] = activeColors != null && disabledColors != null;
            _conditions["hasContacts"] = touchCount > 0;
            _inputs["view"] = new Vector4(width, height, chartAspect, 0);
            _inputs["time"] = new Vector4(songTime / 20f, songTime, songTime * 2f, songTime * 3f);
            _inputs["screen"] = new Vector4(width, height, 1f + 1f / width, 1f + 1f / height);
            _inputs["texelSize"] = new Vector4(1f / masks.Effect.width, 1f / masks.Effect.height, masks.Effect.width, masks.Effect.height);
            _inputs["mainMask"] = masks.Effect; _inputs["auxMask"] = masks.Auxiliary;
            _inputs["displacement"] = displacement; _inputs["spark"] = spark; _inputs["noise"] = noise;
            _inputs["background"] = fallbackBackground; _inputs["contactCount"] = touchCount;
            if (touchCount > 0) _inputs["contacts"] = touchPositions;
            if (_conditions["colored"]) { _inputs["primaryColors"] = activeColors; _inputs["secondaryColors"] = disabledColors; }
            return _compositor.Draw(camera, _conditions, _inputs, songTime);
        }
        internal void Hide() => _compositor.Hide();
        public void Dispose() => _compositor.Dispose();
    }
}
