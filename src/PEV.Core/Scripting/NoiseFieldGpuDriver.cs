using System;
using System.Collections.Generic;
using PEV.Core;
using PEV.Model;
using UnityEngine;

namespace PEV.Scripting
{
    /// <summary>Field rendering isolated by package provider, with per-provider fallback.</summary>
    internal sealed class NoiseFieldGpuDriver : IDisposable
    {
#if PEV_DIAGNOSTICS
        internal static bool SuppressForFallbackTest;
        internal static readonly HashSet<EventManifest> TestFaults = new HashSet<EventManifest>();
#endif
        private readonly Dictionary<EventManifest, NoiseFieldGpuCoordinator> _coordinators =
            new Dictionary<EventManifest, NoiseFieldGpuCoordinator>();
        private readonly HashSet<EventManifest> _faulted = new HashSet<EventManifest>();
        private readonly List<NoiseFieldRenderer.Entry> _entries = new List<NoiseFieldRenderer.Entry>();
        private readonly Dictionary<EventManifest, List<NoiseFieldRenderer.Entry>> _groups =
            new Dictionary<EventManifest, List<NoiseFieldRenderer.Entry>>();
        private readonly HashSet<EventManifest> _ready = new HashSet<EventManifest>();

        internal void Tick(bool playing, double songTime)
        {
#if PEV_DIAGNOSTICS
            if (SuppressForFallbackTest) return;
#endif
            NoiseFieldRenderer.CopyEntriesTo(_entries);
            _groups.Clear();
            foreach (var entry in _entries)
            {
                var provider = entry.Provider;
                if (provider == null || provider.HostPrimitive != "fx.field" ||
                    string.IsNullOrEmpty(provider.ShaderBundlePath) || _faulted.Contains(provider)) continue;
                if (!_groups.TryGetValue(provider, out var group))
                    _groups.Add(provider, group = new List<NoiseFieldRenderer.Entry>());
                group.Add(entry);
            }

            var camera = GameCamera.Current;
            float time = playing ? (float)songTime : Time.unscaledTime;
            if (float.IsNaN(time) || float.IsInfinity(time)) return;
            _ready.Clear();
            if (camera != null)
            {
                foreach (var pair in _groups)
                {
                    var provider = pair.Key;
                    try
                    {
                        if (!_coordinators.TryGetValue(provider, out var coordinator))
                            _coordinators.Add(provider, coordinator = new NoiseFieldGpuCoordinator(provider));
                        bool capture = Plugin.EventSettings.GetBoundBool(provider, "fx.field.captureScene", true);
                        if (coordinator.Render(pair.Value, camera, time, capture, NoiseFieldRenderer.SceneBackground()))
                            _ready.Add(provider);
                    }
                    catch (Exception e)
                    {
#if PEV_DIAGNOSTICS
                        TestFaults.Add(provider);
#endif
                        _faulted.Add(provider);
                        Plugin.Log("experimental field GPU failed for " + provider.Id + "; restoring its legacy renderer: " + e);
                        DisposeCoordinator(provider);
                    }
                }
            }

            // Dispose unused or camera-less groups before restoring their CPU layers.
            var stale = new List<EventManifest>();
            foreach (var pair in _coordinators)
                if (camera == null || !_groups.ContainsKey(pair.Key)) stale.Add(pair.Key);
            foreach (var provider in stale) DisposeCoordinator(provider);
            NoiseFieldRenderer.SetGpuProviders(_ready);
        }

        private void DisposeCoordinator(EventManifest provider)
        {
            if (!_coordinators.TryGetValue(provider, out var coordinator)) return;
            _coordinators.Remove(provider);
            coordinator.Dispose();
        }

        public void Dispose()
        {
            foreach (var coordinator in _coordinators.Values) coordinator.Dispose();
            _coordinators.Clear();
            _faulted.Clear();
            _entries.Clear();
            _groups.Clear();
            _ready.Clear();
            // The owning host destroys all field entries immediately after disposal.
        }
    }
}
