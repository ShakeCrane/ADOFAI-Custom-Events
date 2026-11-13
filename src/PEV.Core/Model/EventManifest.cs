using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace PEV.Model
{
    /// <summary>One parameter declared by an event manifest.</summary>
    public class ParamSpec
    {
        public string Name;
        public string Type;          // game PropertyInfo type literal, e.g. "Float", "Enum:Ease"
        public object Default;
        public double? Min;
        public double? Max;
        public string Unit;
        public List<string> Options; // advisory; only used when we downgrade to String
        public string NoteKey;       // for type "Note": localization key holding the text
        public string CustomLabel;   // overrides the field's display name

        /// <summary>
        /// Convert to the dictionary shape the game's PropertyInfo constructor expects.
        /// Keys mirror LevelEditorProperties entries.
        /// </summary>
        public Dictionary<string, object> ToPropertyDict()
        {
            var d = new Dictionary<string, object>
            {
                { "name", Name },
                { "type", Type }
            };
            if (Default != null) d["default"] = Default;
            if (Min.HasValue) d["min"] = (float)Min.Value;
            if (Max.HasValue) d["max"] = (float)Max.Value;
            if (!string.IsNullOrEmpty(Unit)) d["unit"] = Unit;
            if (!string.IsNullOrEmpty(NoteKey)) d["noteKey"] = NoteKey;
            if (!string.IsNullOrEmpty(CustomLabel)) d["customLabel"] = CustomLabel;
            return d;
        }
    }

    /// <summary>A parsed .event manifest. Immutable after load.</summary>
    public class EventManifest
    {
        public int SchemaVersion = 1;
        public string Id;
        public string Version = "1.0.0";
        public int ApiVersion = 1;
        public string Entry = "main.js";
        public string IconPath;
        public string Category = "VisualFx";
        public string DisplayName = "";
        public string Description = "";
        public List<ParamSpec> Params = new List<ParamSpec>();

        /// <summary>Where the package came from (zip path or directory). Diagnostics only.</summary>
        public string SourcePath;

        public string LocaleName(string locale)
        {
            return DisplayName;
        }

        public static EventManifest Parse(string json, string sourcePath, out string error)
        {
            error = null;
            JObject root;
            try { root = JObject.Parse(json); }
            catch (Exception e) { error = "manifest is not valid JSON: " + e.Message; return null; }

            var m = new EventManifest { SourcePath = sourcePath };
            m.SchemaVersion = root.Value<int?>("schemaVersion") ?? 1;
            m.Id = root.Value<string>("id");
            m.Version = root.Value<string>("version") ?? "1.0.0";
            m.ApiVersion = root.Value<int?>("apiVersion") ?? 1;
            m.Entry = root.Value<string>("entry") ?? "main.js";
            m.IconPath = root.Value<string>("icon");
            m.Category = root.Value<string>("category") ?? "VisualFx";
            m.Description = LocalizedValue(root["description"]) ?? "";
            m.DisplayName = LocalizedValue(root["name"]) ?? m.Id;

            if (string.IsNullOrWhiteSpace(m.Id))
            {
                error = "manifest is missing required field 'id'";
                return null;
            }
            if (m.SchemaVersion > 1)
            {
                error = $"schemaVersion {m.SchemaVersion} is newer than this core supports (1)";
                return null;
            }

            if (root["params"] is JArray arr)
            {
                foreach (var token in arr)
                {
                    if (!(token is JObject p)) continue;
                    var spec = new ParamSpec
                    {
                        Name = p.Value<string>("name"),
                        Type = p.Value<string>("type"),
                        Unit = p.Value<string>("unit")
                    };
                    if (string.IsNullOrWhiteSpace(spec.Name) || string.IsNullOrWhiteSpace(spec.Type))
                    {
                        error = "a param entry is missing 'name' or 'type'";
                        return null;
                    }
                    var def = p["default"];
                    if (def != null) spec.Default = ToPlain(def);
                    if (p["min"] != null) spec.Min = p.Value<double>("min");
                    if (p["max"] != null) spec.Max = p.Value<double>("max");
                    if (p["options"] is JArray opts)
                    {
                        spec.Options = new List<string>();
                        foreach (var o in opts) spec.Options.Add(o.ToString());
                    }
                    m.Params.Add(spec);
                }
            }
            return m;
        }

        private static string LocalizedValue(JToken token)
        {
            if (token == null) return null;
            if (token.Type == JTokenType.String) return token.ToString();
            if (token is JObject obj)
            {
                return obj.Value<string>("en") ?? obj.Properties().FirstOrDefault()?.Value.ToString();
            }
            return null;
        }

        private static object ToPlain(JToken t)
        {
            switch (t.Type)
            {
                case JTokenType.Float: return t.Value<float>();
                case JTokenType.Integer: return t.Value<int>();
                case JTokenType.Boolean: return t.Value<bool>();
                case JTokenType.String: return t.ToString();
                case JTokenType.Array:
                    // The game's PropertyInfo ctor casts these to List<object> for
                    // Tile / Vector2 / FloatPair defaults. A raw JArray fails that cast.
                    var list = new List<object>();
                    foreach (var item in (JArray)t) list.Add(ToPlain(item));
                    return list;
                case JTokenType.Object:
                    return t.ToObject<Dictionary<string, object>>();
                default:
                    return t.ToString();
            }
        }
    }
}
