using System;
using System.Collections.Generic;
using PEV.Core;
using PEV.Model;
using PEV.Registry;
using UnityEngine;

namespace PEV.Scripting
{
    /// <summary>
    /// Explicitly gated test integration. This is not enabled for players until the
    /// Unity bundle, native depth, capture and visual equivalence are verified.
    /// </summary>
    internal sealed class NoiseFieldGpuDriver : IDisposable
    {
        private readonly bool _enabled = Environment.GetEnvironmentVariable("PEV_EXPERIMENTAL_NOISE_GPU") == "1";
        private NoiseFieldGpuCoordinator _coordinator;
        private readonly List<NoiseFieldRenderer.Entry> _entries = new List<NoiseFieldRenderer.Entry>();
        private bool _faulted;

        internal void Tick(bool playing, double songTime)
        {
            if (!_enabled || _faulted) return;
            EventManifest manifest = null;
            foreach (var candidate in EventRegistry.All)
            {
                if (candidate.HostPrimitive != "fx.field" || string.IsNullOrEmpty(candidate.ShaderBundlePath)) continue;
                if (manifest != null)
                {
                    _faulted = true;
                    Plugin.Log("experimental field GPU disabled: multiple field shader providers are not yet supported");
                    DisposeCoordinator();
                    NoiseFieldRenderer.SetGpuRendering(false);
                    return;
                }
                manifest = candidate;
            }
            if (manifest == null)
            {
                DisposeCoordinator();
                NoiseFieldRenderer.SetGpuRendering(false);
                return;
            }
            bool captureScene = Plugin.EventSettings.GetBoundBool(manifest, "fx.field.captureScene", true);
            NoiseFieldRenderer.CopyEntriesTo(_entries);
            if (_entries.Count == 0)
            {
                DisposeCoordinator();
                NoiseFieldRenderer.SetGpuRendering(false);
                return;
            }
            var camera = GameCamera.Current;
            if (camera == null)
            {
                DisposeCoordinator();
                NoiseFieldRenderer.SetGpuRendering(false);
                return;
            }
            float time = playing ? (float)songTime : Time.unscaledTime;
            if (float.IsNaN(time) || float.IsInfinity(time)) return;
            try
            {
                if (_coordinator == null) _coordinator = new NoiseFieldGpuCoordinator(manifest);
                bool ready = _coordinator.Render(_entries, camera, time, captureScene, NoiseFieldRenderer.SceneBackground());
                NoiseFieldRenderer.SetGpuRendering(ready);
            }
            catch (Exception e)
            {
                _faulted = true;
                Plugin.Log("experimental noise GPU disabled; restoring legacy renderer: " + e);
                DisposeCoordinator();
                NoiseFieldRenderer.SetGpuRendering(false);
            }
        }

        private void DisposeCoordinator()
        {
            if (_coordinator == null) return;
            _coordinator.Dispose();
            _coordinator = null;
        }

        public void Dispose()
        {
            DisposeCoordinator();
            _entries.Clear();
            // Teardown destroys all entries immediately after disposing this driver.
        }
    }
}
