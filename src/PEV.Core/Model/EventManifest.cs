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
        /// Per-locale labels, keyed by SystemLanguage name. CustomLabel is the English/plain
        /// fallback; this carries the rest.
        ///
        /// Without this the Chinese labels in every shipped manifest were parsed and then
        /// dropped: LocalizedValue returns the "en" entry by design, so a Chinese client showed
        /// English rows. The panel reads customLabel at build time, so the value has to be
        /// chosen before the PropertyInfo is constructed — see ResolveLabel.
        /// </summary>
        public Dictionary<string, string> Labels =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string Group;         // properties-panel tab; native events group into pages
        public int? Order;           // explicit sort position within the event's list
        /// <summary>Flat key/value pairs: show this row only when another property matches.</summary>
        public List<string> ShowIf;
        public List<string> HideIf;

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
            // Grouping is what makes a 25-parameter event usable: without it the panel is
            // one flat scroll list, while every native event organizes its panel into tabs.
            if (!string.IsNullOrEmpty(Group)) d["group"] = Group;
            if (Order.HasValue) d["order"] = Order.Value;
            // Conditions must be a List<object>, not a string[].
            //
            // PropertyInfo's constructor hands these to RDEditorUtils.DecodeStringArray, which
            // is an `is List<object>` cast returning null for anything else — and the caller
            // dereferences the result immediately. A string[] therefore threw
            // NullReferenceException inside the constructor, and the parameter was dropped.
            if (ShowIf != null && ShowIf.Count > 0) d["showIf"] = ToObjectList(ShowIf);
            if (HideIf != null && HideIf.Count > 0) d["hideIf"] = ToObjectList(HideIf);
            return d;
        }

        private static List<object> ToObjectList(List<string> values)
        {
            var list = new List<object>(values.Count);
            foreach (var v in values) list.Add(v);
            return list;
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

        /// <summary>Per-locale display name and description, keyed by SystemLanguage name.</summary>
        public Dictionary<string, string> Names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Descriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// True when an instance of this event is stored as a real native AddDecoration row
        /// rather than as an EditorComment.
        ///
        /// The difference is not cosmetic. A comment is invisible to the game, so the effect
        /// can never be selected, dragged, or swept up by MoveDecorations' tag lookup. A
        /// decoration row is exactly what scrDecorationManager indexes, so the same code
        /// that moves native decorations moves ours — which is the whole point of the
        /// event being "like a decoration".
        /// </summary>
        public bool DecorationBacked;

        /// <summary>
        /// The name to show for <paramref name="locale"/>, which is a SystemLanguage name
        /// ("ChineseSimplified", "Korean", …). Falls back to English, then to the id, so a
        /// package that ships only one translation still renders.
        ///
        /// Earlier this ignored the argument entirely: the manifest's zh-CN was parsed and
        /// then thrown away, which is why a Chinese game still showed English names.
        /// </summary>
        public string LocaleName(string locale)
        {
            if (!string.IsNullOrEmpty(locale) && Names.TryGetValue(locale, out var localized)
                && !string.IsNullOrEmpty(localized))
                return localized;
            if (Names.TryGetValue("English", out var en) && !string.IsNullOrEmpty(en))
                return en;
            if (!string.IsNullOrEmpty(DisplayName)) return DisplayName;
            return Id;
        }

        public string LocaleDescription(string locale)
        {
            if (!string.IsNullOrEmpty(locale) && Descriptions.TryGetValue(locale, out var localized)
                && !string.IsNullOrEmpty(localized))
                return localized;
            if (Descriptions.TryGetValue("English", out var en) && !string.IsNullOrEmpty(en))
                return en;
            return Description;
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
            m.DecorationBacked = root.Value<bool?>("decoration") ?? false;
            m.Description = LocalizedValue(root["description"]) ?? "";
            m.DisplayName = LocalizedValue(root["name"]) ?? m.Id;

            // Keep every locale the package ships, keyed by SystemLanguage name. The game
            // only knows 14 language identifiers and none of them is "zh-CN", so the old
            // parse kept "en" and silently dropped the rest — a Chinese client showed
            // English names even though the package had a translation.
            ReadLocales(root["name"], m.Names);
            ReadLocales(root["description"], m.Descriptions);

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
                        Unit = p.Value<string>("unit"),
                        Group = p.Value<string>("group"),
                        // Labels may be a plain string or a per-locale object like name.
                        CustomLabel = LocalizedValue(p["label"]) ?? LocalizedValue(p["customLabel"]),
                        NoteKey = p.Value<string>("noteKey")
                    };
                    ReadLocales(p["label"] ?? p["customLabel"], spec.Labels);
                    if (p["order"] != null) spec.Order = p.Value<int>("order");
                    spec.ShowIf = ReadPairs(p["showIf"]);
                    spec.HideIf = ReadPairs(p["hideIf"]);
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

        /// <summary>
        /// Reads a flat key/value condition array ("shape", "Rect", ...) or an object form.
        /// The game decodes it as alternating key and value strings.
        /// </summary>
        private static List<string> ReadPairs(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            var result = new List<string>();
            if (token is JArray arr)
            {
                foreach (var item in arr) result.Add(item.ToString());
            }
            else if (token is JObject obj)
            {
                foreach (var kv in obj)
                {
                    result.Add(kv.Key);
                    result.Add(kv.Value.ToString());
                }
            }
            return result.Count > 0 ? result : null;
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

        /// <summary>
        /// Collects a manifest's per-locale map into <paramref name="into"/>, translating the
        /// author-friendly keys into the game's SystemLanguage names.
        ///
        /// Authors write "zh-CN" or "en"; the game looks up "ChineseSimplified" or "English".
        /// Accepting both is the difference between a package that localizes and one that
        /// silently shows English everywhere.
        /// </summary>
        private static void ReadLocales(JToken token, Dictionary<string, string> into)
        {
            if (!(token is JObject obj)) return;
            foreach (var prop in obj.Properties())
            {
                string value = prop.Value?.ToString();
                if (string.IsNullOrEmpty(value)) continue;
                string key = NormalizeLocale(prop.Name);
                if (key != null) into[key] = value;
            }
        }

        private static string NormalizeLocale(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            string s = raw.Replace("_", "-").Trim();

            // Exact SystemLanguage names first: a package may already use them.
            switch (s)
            {
                case "English": case "Korean": case "Japanese":
                case "ChineseSimplified": case "ChineseTraditional":
                case "Spanish": case "Portuguese": case "French": case "Polish":
                case "Romanian": case "Russian": case "Vietnamese":
                case "Czech": case "German":
                    return s;
            }

            // Then the BCP-47 tags an author is most likely to write.
            switch (s.ToLowerInvariant())
            {
                case "en": return "English";
                case "ko": return "Korean";
                case "ja": return "Japanese";
                case "zh": case "zh-cn": case "zh-hans": return "ChineseSimplified";
                case "zh-tw": case "zh-hk": case "zh-hant": return "ChineseTraditional";
                case "es": return "Spanish";
                case "pt": return "Portuguese";
                case "fr": return "French";
                case "pl": return "Polish";
                case "ro": return "Romanian";
                case "ru": return "Russian";
                case "vi": return "Vietnamese";
                case "cs": return "Czech";
                case "de": return "German";
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
