using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;

namespace PEV.Scripting
{
    // Transitional renderer adapter: the package defines what each tag value means.
    internal sealed class RegionModes
    {
        internal struct Mode
        {
            internal uint ShaderFlags;
            internal bool MainLayer, AuxiliaryLayer, Palette, Contacts;
        }
        private readonly Dictionary<string, Mode> _values = new Dictionary<string, Mode>(StringComparer.OrdinalIgnoreCase);
        internal string TagKey { get; private set; }
        internal string Default { get; private set; }
        internal uint InvertFlag { get; private set; }
        internal Mode Resolve(string value) => value != null && _values.TryGetValue(value, out var mode) ? mode : _values[Default];

        internal static RegionModes Parse(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > 131072) throw new ArgumentException("Invalid mode resource");
            var root = JObject.Parse(new UTF8Encoding(false, true).GetString(bytes));
            if (!(root["regionModes"] is JObject modes) || !(modes["values"] is JObject values) || values.Count < 1 || values.Count > 64)
                throw new ArgumentException("Invalid region modes");
            var result = new RegionModes { TagKey = Text(modes, "tagKey"), Default = Text(modes, "default"), InvertFlag = Flags(modes, "invertFlag") };
            foreach (var property in values.Properties())
            {
                if (string.IsNullOrWhiteSpace(property.Name) || property.Name.Length > 128 || !(property.Value is JObject spec))
                    throw new ArgumentException("Invalid region mode");
                var mode = new Mode { ShaderFlags = Flags(spec, "shaderFlags"), MainLayer = Bool(spec, "mainLayer"),
                    AuxiliaryLayer = Bool(spec, "auxLayer"), Palette = Bool(spec, "palette"), Contacts = Bool(spec, "contacts") };
                if ((mode.ShaderFlags & result.InvertFlag) != 0) throw new ArgumentException("Mode and inversion flags overlap");
                result._values.Add(property.Name, mode);
            }
            if (!result._values.ContainsKey(result.Default)) throw new ArgumentException("Unknown default region mode");
            return result;
        }
        private static string Text(JObject obj, string name)
        {
            if (obj[name]?.Type != JTokenType.String) throw new ArgumentException("Invalid region mode text");
            string value = obj.Value<string>(name);
            if (string.IsNullOrWhiteSpace(value) || value.Length > 128) throw new ArgumentException("Invalid region mode text");
            return value;
        }
        private static uint Flags(JObject obj, string name)
        {
            if (obj[name]?.Type != JTokenType.Integer) throw new ArgumentException("Invalid region mode flags");
            long value = obj.Value<long>(name);
            if (value < 0 || value > 65535) throw new ArgumentOutOfRangeException(name);
            return (uint)value;
        }
        private static bool Bool(JObject obj, string name)
        {
            if (obj[name]?.Type != JTokenType.Boolean) throw new ArgumentException("Invalid region mode layer");
            return obj.Value<bool>(name);
        }
    }
}
