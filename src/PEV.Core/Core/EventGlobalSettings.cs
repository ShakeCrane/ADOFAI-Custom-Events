using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PEV.Model;

namespace PEV.Core
{
    /// <summary>Package-scoped settings; undeclared/uninstalled values survive round trips.</summary>
    internal sealed class EventGlobalSettings
    {
        private JObject _values = new JObject();
        private bool _loadFailed;
        internal int Revision { get; private set; }

        internal void Load(string path)
        {
            _values = new JObject();
            _loadFailed = false;
            if (!File.Exists(path)) return;
            try { _values = JObject.Parse(File.ReadAllText(path)); }
            catch { _loadFailed = true; throw; }
        }

        internal bool Get(EventManifest manifest, GlobalSettingSpec setting)
        {
            if (manifest == null || setting == null) throw new ArgumentNullException();
            var value = (_values[manifest.Id] as JObject)?[setting.Name];
            return value?.Type == JTokenType.Boolean ? value.Value<bool>() : setting.Default;
        }

        internal bool GetBoundBool(EventManifest manifest, string binding, bool fallback)
        {
            if (manifest == null) return fallback;
            foreach (var setting in manifest.GlobalSettings)
                if (setting.Binding == binding) return Get(manifest, setting);
            return fallback;
        }

        internal void Set(EventManifest manifest, GlobalSettingSpec setting, bool value)
        {
            if (manifest == null || setting == null || !manifest.GlobalSettings.Contains(setting))
                throw new ArgumentException("Setting is not declared by this event");
            if (_loadFailed) throw new InvalidOperationException("Settings file could not be read; refusing to overwrite it");
            if (_values[manifest.Id] != null && !(_values[manifest.Id] is JObject))
                throw new InvalidOperationException("Stored event settings are invalid; refusing to overwrite them");
            if (Get(manifest, setting) == value) return;
            var package = _values[manifest.Id] as JObject;
            if (package == null) _values[manifest.Id] = package = new JObject();
            package[setting.Name] = value;
            Revision++;
        }

        internal Dictionary<string, object> Snapshot(EventManifest manifest)
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            if (manifest != null)
                foreach (var setting in manifest.GlobalSettings) result[setting.Name] = Get(manifest, setting);
            return result;
        }

        internal void CopyBooleans(EventManifest manifest, Dictionary<string, bool> destination)
        {
            destination.Clear();
            if (manifest != null)
                foreach (var setting in manifest.GlobalSettings) destination[setting.Name] = Get(manifest, setting);
        }

        internal void Save(string path)
        {
            if (_loadFailed) throw new InvalidOperationException("Settings file could not be read; refusing to overwrite it");
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, _values.ToString(Formatting.Indented));
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
