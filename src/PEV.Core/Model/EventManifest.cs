using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace PEV.Model
{
    /// <summary>A package-owned, mod-wide setting exposed by Core.</summary>
    public sealed class GlobalSettingSpec
    {
        public string Name;
        public bool Default;
        public string Binding;
        public readonly Dictionary<string, string> Labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string Label(string locale) =>
            !string.IsNullOrEmpty(locale) && Labels.TryGetValue(locale, out var text) ? text :
            Labels.TryGetValue("English", out var english) ? english : Name;
    }

    public sealed class ImageAssetSpec
    {
        public string Path, Wrap, Filter;
        public bool FlipY, Linear;
    }

    public sealed class DecorationMigrationBinding
    {
        public string Source, Target, Conversion;
    }
    /// <summary>One parameter declared by an event manifest.</summary>
    public class ParamSpec
    {
        public string Name;
        public string Type;          // game PropertyInfo type literal, e.g. "Float", "Enum:Ease"
        internal string ResolvedType;
        internal string EffectiveType => ResolvedType ?? Type;
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
        /// <summary>
        /// Localized captions for an enum-typed parameter's values, in declaration order.
        ///
        /// Separate from Labels because a panel renders them through a different path: a row
        /// label is read once from customLabel, while a dropdown entry asks the game's
        /// localization table for "enum.&lt;type&gt;.&lt;value&gt;" at draw time. See
        /// Registry.EnumLabels, which is the only thing that can put text there.
        /// </summary>
        public List<OptionSpec> OptionLabels = new List<OptionSpec>();
        public string Group;         // properties-panel tab; native events group into pages
        public int? Order;           // explicit sort position within the event's list
        /// <summary>Flat key/value pairs: show this row only when another property matches.</summary>
        public List<string> ShowIf;
        public List<string> HideIf;

        /// <summary>
        /// Picks the label for the language the game is running in.
        ///
        /// The manifest carries per-locale labels, but the panel reads exactly one string at
        /// PropertyInfo-construction time, so the choice has to happen here. RDString.language
        /// is the editor's own setting, not the OS locale — an author who switched the game to
        /// Chinese expects the panel to follow the game, not the system.
        /// </summary>
        private string ResolveLabel()
        {
            if (Labels == null || Labels.Count == 0) return CustomLabel;

            string lang = null;
            try { lang = RDString.language.ToString(); } catch { }

            if (!string.IsNullOrEmpty(lang) && Labels.TryGetValue(lang, out var exact) &&
                !string.IsNullOrEmpty(exact))
                return exact;

            // The game has three Chinese ids and packages write many spellings of them.
            if (lang != null && lang.StartsWith("Chinese", StringComparison.Ordinal))
            {
                foreach (var alt in new[] { "ChineseSimplified", "ChineseTraditional", "Chinese" })
                {
                    if (Labels.TryGetValue(alt, out var zh) && !string.IsNullOrEmpty(zh))
                        return zh;
                }
            }

            if (Labels.TryGetValue("English", out var en) && !string.IsNullOrEmpty(en)) return en;
            if (!string.IsNullOrEmpty(CustomLabel)) return CustomLabel;

            foreach (var pair in Labels)
                if (!string.IsNullOrEmpty(pair.Value)) return pair.Value;
            return null;
        }

        /// <summary>
        /// Convert to the dictionary shape the game's PropertyInfo constructor expects.
        /// Keys mirror LevelEditorProperties entries.
        /// </summary>
        public Dictionary<string, object> ToPropertyDict()
        {
            var d = new Dictionary<string, object>
            {
                { "name", Name },
                { "type", EffectiveType }
            };
            if (Default != null) d["default"] = Default;
            if (Min.HasValue) d["min"] = (float)Min.Value;
            if (Max.HasValue) d["max"] = (float)Max.Value;
            if (!string.IsNullOrEmpty(Unit)) d["unit"] = Unit;
            if (!string.IsNullOrEmpty(NoteKey)) d["noteKey"] = NoteKey;
            string label = ResolveLabel();
            if (!string.IsNullOrEmpty(label)) d["customLabel"] = label;
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

    /// <summary>One enum value's caption, per locale.</summary>
    public class OptionSpec
    {
        /// <summary>The value as the enum spells it, which is the key the dropdown asks for.</summary>
        public string Key;

        /// <summary>Locale name to caption, normalized the same way Labels are.</summary>
        public Dictionary<string, string> Value =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A parsed .event manifest. Immutable after load.</summary>
    public class EventManifest
    {
        public int SchemaVersion = 1;
        // Additional event definitions in the same package, relative to its root.
        public readonly List<string> IncludedEvents = new List<string>();
        public readonly Dictionary<string, string> VectorPrograms = new Dictionary<string, string>(StringComparer.Ordinal);
        public readonly Dictionary<string, string> ComputeGraphs = new Dictionary<string, string>(StringComparer.Ordinal);
        public readonly Dictionary<string, string> KeySpecs = new Dictionary<string, string>(StringComparer.Ordinal);
        public readonly Dictionary<string, string> RuntimeBindings = new Dictionary<string, string>(StringComparer.Ordinal);
        public readonly Dictionary<string, string> PixelResources = new Dictionary<string, string>(StringComparer.Ordinal);
        public string Id;
        public string Version = "1.0.0";
        public int ApiVersion = 1;
        public string Entry = "main.js";
        public string IconPath;
        /// <summary>Optional package-local Unity shader bundle (schema 2).</summary>
        public string ShaderBundlePath;
        public readonly List<GlobalSettingSpec> GlobalSettings = new List<GlobalSettingSpec>();
        public string DecorationDefaultColor;
        public string DecorationDefaultTag;
        public string HostPrimitive;
        public readonly Dictionary<string, ImageAssetSpec> ImageAssets = new Dictionary<string, ImageAssetSpec>(StringComparer.Ordinal);
        public readonly Dictionary<string, string> ShaderAssets = new Dictionary<string, string>(StringComparer.Ordinal);
        public string FieldPreviewKey;
        public readonly Dictionary<string, string> FieldParameterBindings = new Dictionary<string, string>(StringComparer.Ordinal);
        public readonly List<DecorationMigrationBinding> DecorationMigration = new List<DecorationMigrationBinding>();
        public string Category = "VisualFx";
        public string DisplayName = "";
        public string Description = "";
        public List<ParamSpec> Params = new List<ParamSpec>();

        /// <summary>Host-owned package path (ZIP or directory); never exposed directly to scripts.</summary>
        public string SourcePath;

        internal bool TryRuntimeResource(string binding, IReadOnlyDictionary<string, string> resources, out string path)
        {
            path = null;
            return RuntimeBindings.TryGetValue(binding, out var role) && resources.TryGetValue(role, out path);
        }

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

            try { return ParseFields(root, sourcePath, out error); }
            catch (Exception e) { error = "invalid manifest fields: " + e.Message; return null; }
        }

        private static EventManifest ParseFields(JObject root, string sourcePath, out string error)
        {
            error = null;

            var m = new EventManifest { SourcePath = sourcePath };
            m.SchemaVersion = root.Value<int?>("schemaVersion") ?? 1;
            m.Id = root.Value<string>("id");
            m.Version = root.Value<string>("version") ?? "1.0.0";
            m.ApiVersion = root.Value<int?>("apiVersion") ?? 1;
            m.Entry = root.Value<string>("entry") ?? "main.js";
            m.IconPath = root.Value<string>("icon");
            if (root["shaderBundle"] != null && root["shaderBundle"].Type != JTokenType.String)
            {
                error = "shaderBundle must be a package-relative path string";
                return null;
            }
            m.ShaderBundlePath = root.Value<string>("shaderBundle");
            m.Category = root.Value<string>("category") ?? "VisualFx";
            m.DecorationBacked = root.Value<bool?>("decoration") ?? false;
            if (root["hostPrimitive"] != null)
            {
                if (m.SchemaVersion < 3 || root["hostPrimitive"].Type != JTokenType.String ||
                    root.Value<string>("hostPrimitive") != "fx.field")
                { error = "invalid hostPrimitive (requires schemaVersion 3)"; return null; }
                m.HostPrimitive = root.Value<string>("hostPrimitive");
            }
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
            if ((m.SchemaVersion != 1 && m.SchemaVersion != 2 && m.SchemaVersion != 3 && m.SchemaVersion != 4) ||
                (m.ApiVersion != 1 && m.ApiVersion != 2))
            {
                error = $"unsupported schemaVersion {m.SchemaVersion} or apiVersion {m.ApiVersion} (supported schemas: 1, 2, 3, 4; API: 1, 2)";
                return null;
            }
            if (root["includedEvents"] != null)
            {
                if (m.SchemaVersion < 4 || !(root["includedEvents"] is JArray included) || included.Count < 1 || included.Count > 16)
                { error = "includedEvents requires schemaVersion 4 and 1-16 manifest paths"; return null; }
                var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in included)
                {
                    string path = item.Type == JTokenType.String ? item.Value<string>()?.Replace('\\', '/') : null;
                    if (!PEV.Loading.EventLoader.IsPackageRelativePath(path) ||
                        string.Equals(path, "event.json", StringComparison.OrdinalIgnoreCase) || !paths.Add(path))
                    { error = "includedEvents contains an invalid or duplicate package-relative path"; return null; }
                    m.IncludedEvents.Add(path);
                }
            }
            if (root["vectorPrograms"] != null)
            {
                if (m.SchemaVersion < 4 || !(root["vectorPrograms"] is JObject programs) || programs.Count < 1 || programs.Count > 16)
                { error = "vectorPrograms requires schemaVersion 4 and 1-16 package-relative scripts"; return null; }
                foreach (var entry in programs.Properties())
                {
                    if (!System.Text.RegularExpressions.Regex.IsMatch(entry.Name, "^[A-Za-z_][A-Za-z0-9_]{0,63}$") ||
                        entry.Name == "__proto__" || entry.Name == "constructor" || entry.Name == "prototype" ||
                        entry.Value.Type != JTokenType.String || !PEV.Loading.EventLoader.IsPackageRelativePath(entry.Value.Value<string>()))
                    { error = "invalid vector program declaration"; return null; }
                    m.VectorPrograms.Add(entry.Name, entry.Value.Value<string>().Replace('\\', '/'));
                }
            }
            if (root["computeGraphs"] != null)
            {
                if (m.SchemaVersion < 4 || !(root["computeGraphs"] is JObject graphs) || graphs.Count < 1 || graphs.Count > 16)
                { error = "computeGraphs requires schemaVersion 4 and 1-16 package-relative graph definitions"; return null; }
                foreach (var entry in graphs.Properties())
                {
                    if (!System.Text.RegularExpressions.Regex.IsMatch(entry.Name, "^[A-Za-z_][A-Za-z0-9_]{0,63}$") ||
                        entry.Name == "__proto__" || entry.Name == "constructor" || entry.Name == "prototype" ||
                        entry.Value.Type != JTokenType.String || !PEV.Loading.EventLoader.IsPackageRelativePath(entry.Value.Value<string>()))
                    { error = "invalid compute graph declaration"; return null; }
                    m.ComputeGraphs.Add(entry.Name, entry.Value.Value<string>().Replace('\\', '/'));
                }
            }
            if (root["keySpecs"] != null)
            {
                if (m.SchemaVersion < 4 || !(root["keySpecs"] is JObject specs) || specs.Count < 1 || specs.Count > 16)
                { error = "keySpecs requires schemaVersion 4 and 1-16 package-relative resources"; return null; }
                foreach (var entry in specs.Properties())
                {
                    if (!System.Text.RegularExpressions.Regex.IsMatch(entry.Name, "^[A-Za-z_][A-Za-z0-9_]{0,63}$") ||
                        entry.Name == "__proto__" || entry.Name == "constructor" || entry.Name == "prototype" ||
                        entry.Value.Type != JTokenType.String || !PEV.Loading.EventLoader.IsPackageRelativePath(entry.Value.Value<string>()))
                    { error = "invalid key specification declaration"; return null; }
                    m.KeySpecs.Add(entry.Name, entry.Value.Value<string>().Replace('\\', '/'));
                }
            }
            if (root["runtimeBindings"] != null)
            {
                if (m.SchemaVersion < 4 || !(root["runtimeBindings"] is JObject bindings) || bindings.Count < 1 || bindings.Count > 16)
                { error = "runtimeBindings requires schemaVersion 4 and 1-16 named resource roles"; return null; }
                foreach (var entry in bindings.Properties())
                {
                    string role = entry.Value.Type == JTokenType.String ? entry.Value.Value<string>() : null;
                    if (!System.Text.RegularExpressions.Regex.IsMatch(entry.Name, "^[A-Za-z_][A-Za-z0-9_]{0,63}$") ||
                        entry.Name == "__proto__" || entry.Name == "constructor" || entry.Name == "prototype" ||
                        role == null || (!m.VectorPrograms.ContainsKey(role) && !m.ComputeGraphs.ContainsKey(role) && !m.KeySpecs.ContainsKey(role)))
                    { error = "invalid runtime resource binding"; return null; }
                    m.RuntimeBindings.Add(entry.Name, role);
                }
            }
            if (root["pixelResources"] != null)
            {
                if (m.SchemaVersion < 4 || !(root["pixelResources"] is JObject resources) || resources.Count < 1 || resources.Count > 16)
                { error = "pixelResources requires schemaVersion 4 and 1-16 package-relative images"; return null; }
                foreach (var entry in resources.Properties())
                {
                    if (!System.Text.RegularExpressions.Regex.IsMatch(entry.Name, "^[A-Za-z_][A-Za-z0-9_]{0,63}$") ||
                        entry.Name == "__proto__" || entry.Name == "constructor" || entry.Name == "prototype" ||
                        entry.Value.Type != JTokenType.String || !PEV.Loading.EventLoader.IsPackageRelativePath(entry.Value.Value<string>()))
                    { error = "invalid pixel resource declaration"; return null; }
                    m.PixelResources.Add(entry.Name, entry.Value.Value<string>().Replace('\\', '/'));
                }
            }
            if (root["shaderAssets"] != null &&
                (m.SchemaVersion < 4 || string.IsNullOrEmpty(m.ShaderBundlePath) || !ReadNamedResourceMap(root["shaderAssets"], m.ShaderAssets)))
            { error = "shaderAssets requires schemaVersion 4, a shader bundle and package-relative asset names"; return null; }
            if (root["imageAssets"] != null)
            {
                if (m.SchemaVersion < 4 || !(root["imageAssets"] is JObject images) || images.Count < 1 || images.Count > 16)
                { error = "imageAssets requires schemaVersion 4 and 1-16 named images"; return null; }
                foreach (var item in images.Properties())
                {
                    if (!ValidFieldArgumentName(item.Name) || !(item.Value is JObject spec) || spec.Count != 5 ||
                        spec["path"]?.Type != JTokenType.String || !PEV.Loading.EventLoader.IsPackageRelativePath(spec.Value<string>("path")) ||
                        spec["wrap"]?.Type != JTokenType.String || spec["filter"]?.Type != JTokenType.String ||
                        spec["flipY"]?.Type != JTokenType.Boolean || spec["linear"]?.Type != JTokenType.Boolean)
                    { error = "invalid image asset declaration"; return null; }
                    string wrap = spec.Value<string>("wrap"), filter = spec.Value<string>("filter");
                    if ((wrap != "clamp" && wrap != "repeat" && wrap != "mirror") || (filter != "point" && filter != "linear"))
                    { error = "invalid image asset sampling"; return null; }
                    m.ImageAssets.Add(item.Name, new ImageAssetSpec { Path = spec.Value<string>("path").Replace('\\', '/'),
                        Wrap = wrap, Filter = filter, FlipY = spec.Value<bool>("flipY"), Linear = spec.Value<bool>("linear") });
                }
            }
            if (root["shaderBundle"] != null &&
                (m.SchemaVersion < 2 || !PEV.Loading.EventLoader.IsPackageRelativePath(m.ShaderBundlePath)))
            {
                error = "shaderBundle requires schemaVersion 2 or newer and a package-relative path";
                return null;
            }

            if (root["globalSettings"] != null)
            {
                if (m.SchemaVersion < 3 || m.ApiVersion < 2 || !(root["globalSettings"] is JArray settings))
                { error = "globalSettings requires schemaVersion 3, apiVersion 2 and an array"; return null; }
                foreach (var item in settings)
                {
                    if (!(item is JObject spec) || spec["name"]?.Type != JTokenType.String ||
                        spec["type"]?.Type != JTokenType.String || spec.Value<string>("type") != "Bool" ||
                        spec["default"]?.Type != JTokenType.Boolean)
                    { error = "globalSettings entries require a name, type Bool and boolean default"; return null; }
                    string name = spec.Value<string>("name");
                    if (string.IsNullOrWhiteSpace(name) || name == "__proto__" || name == "constructor" ||
                        name == "prototype" || m.GlobalSettings.Any(existing => existing.Name == name))
                    { error = "invalid or duplicate global setting name '" + name + "'"; return null; }
                    string binding = null;
                    if (spec["binding"] != null)
                    {
                        if (spec["binding"].Type != JTokenType.String ||
                            !System.Text.RegularExpressions.Regex.IsMatch(spec.Value<string>("binding"), "^[A-Za-z][A-Za-z0-9_.]{0,127}$") ||
                            m.GlobalSettings.Any(existing => existing.Binding == spec.Value<string>("binding")))
                        { error = "invalid or duplicate global setting binding"; return null; }
                        binding = spec.Value<string>("binding");
                    }
                    var setting = new GlobalSettingSpec { Name = name, Default = spec.Value<bool>("default"), Binding = binding };
                    ReadLocales(spec["label"], setting.Labels);
                    if (spec["label"]?.Type == JTokenType.String)
                        setting.Labels["English"] = spec.Value<string>("label");
                    m.GlobalSettings.Add(setting);
                }
            }

            if (root["decorationDefaults"] != null)
            {
                if (m.SchemaVersion < 3 || !m.DecorationBacked || !(root["decorationDefaults"] is JObject defaults) ||
                    defaults.Properties().Any(p => p.Name != "color" && p.Name != "tag") ||
                    (defaults["color"] != null && (defaults["color"].Type != JTokenType.String ||
                    !System.Text.RegularExpressions.Regex.IsMatch(defaults.Value<string>("color"), "^[0-9a-fA-F]{6}$"))) ||
                    (defaults["tag"] != null && (defaults["tag"].Type != JTokenType.String ||
                    string.IsNullOrWhiteSpace(defaults.Value<string>("tag")) || defaults.Value<string>("tag").Length > 128)))
                { error = "decorationDefaults requires a decoration-backed schema 3 event, optional six-digit color and nonempty tag"; return null; }
                m.DecorationDefaultColor = defaults.Value<string>("color");
                m.DecorationDefaultTag = defaults.Value<string>("tag");
            }

            if (root["decorationMigration"] != null)
            {
                if (m.SchemaVersion < 3 || !m.DecorationBacked || !(root["decorationMigration"] is JArray migrations) || migrations.Count > 16)
                { error = "decorationMigration requires a schema 3 decoration event and at most 16 bindings"; return null; }
                var targets = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in migrations)
                {
                    if (!(item is JObject binding) || binding.Count != 3 || binding["source"]?.Type != JTokenType.String ||
                        binding["target"]?.Type != JTokenType.String || binding["conversion"]?.Type != JTokenType.String)
                    { error = "invalid decorationMigration binding"; return null; }
                    string source = binding.Value<string>("source"), target = binding.Value<string>("target"), conversion = binding.Value<string>("conversion");
                    bool supported = (target == "scale" && conversion == "tilesToScale") ||
                        (target == "position" && conversion == "vector2") ||
                        (target == "rotation" && conversion == "float") ||
                        ((target == "tag" || target == "color") && conversion == "string") ||
                        (target == "depth" && conversion == "int") || (target == "opacity" && conversion == "percent");
                    if (!ValidFieldArgumentName(source) || !supported || !targets.Add(target))
                    { error = "invalid or duplicate decorationMigration binding"; return null; }
                    m.DecorationMigration.Add(new DecorationMigrationBinding { Source = source, Target = target, Conversion = conversion });
                }
            }

            if (root["fieldPreview"] != null)
            {
                if (m.SchemaVersion < 3 || m.HostPrimitive != "fx.field" || !(root["fieldPreview"] is JObject preview) ||
                    preview.Count != 2 || preview["key"]?.Type != JTokenType.String ||
                    string.IsNullOrWhiteSpace(preview.Value<string>("key")) || preview.Value<string>("key").Length > 128 ||
                    !(preview["parameterMap"] is JObject map) || map.Count > 64)
                { error = "fieldPreview requires a key and parameterMap on a schema 3 fx.field event"; return null; }
                var destinations = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in map.Properties())
                {
                    string target = property.Value.Type == JTokenType.String ? property.Value.Value<string>() : null;
                    if (!ValidFieldArgumentName(property.Name) || !ValidFieldArgumentName(target) || !destinations.Add(target))
                    { error = "invalid or duplicate fieldPreview parameter mapping"; return null; }
                    m.FieldParameterBindings.Add(property.Name, target);
                }
                m.FieldPreviewKey = preview.Value<string>("key");
            }

            if (root["params"] != null && !(root["params"] is JArray))
            {
                error = "params must be an array";
                return null;
            }

            if (root["params"] is JArray arr)
            {
                foreach (var token in arr)
                {
                    if (!(token is JObject p)) { error = "param entry must be an object"; return null; }
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
                    if (m.Params.Any(existing => existing.Name == spec.Name) || spec.Name == "__pevUid")
                    {
                        error = "duplicate or reserved parameter name '" + spec.Name + "'";
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
                    if (spec.Type == "Choice")
                    {
                        if (m.SchemaVersion < 3 || !(p["options"] is JArray choices) || choices.Count < 1 || choices.Count > 64 ||
                            choices.Any(o => o.Type != JTokenType.String ||
                                !System.Text.RegularExpressions.Regex.IsMatch(o.ToString(), "^[A-Za-z_][A-Za-z0-9_]{0,63}$") || o.ToString() == "value__") ||
                            choices.Select(o => o.ToString()).Distinct(StringComparer.Ordinal).Count() != choices.Count ||
                            p["default"]?.Type != JTokenType.String || !spec.Options.Contains(p.Value<string>("default")))
                        { error = "Choice requires schemaVersion 3, 1-64 unique identifier options and a matching default"; return null; }
                    }
                    ReadOptionLabels(p, spec);
                    m.Params.Add(spec);
                }
            }
            return m;
        }

        private static bool ReadNamedResourceMap(JToken token, Dictionary<string, string> target)
        {
            if (!(token is JObject map) || map.Count < 1 || map.Count > 32) return false;
            foreach (var item in map.Properties())
            {
                if (!ValidFieldArgumentName(item.Name) || item.Value.Type != JTokenType.String ||
                    !PEV.Loading.EventLoader.IsPackageRelativePath(item.Value.Value<string>())) return false;
                target.Add(item.Name, item.Value.Value<string>());
            }
            return true;
        }

        private static bool ValidFieldArgumentName(string value) =>
            !string.IsNullOrEmpty(value) && !value.StartsWith("__", StringComparison.Ordinal) &&
            System.Text.RegularExpressions.Regex.IsMatch(value, "^[A-Za-z_][A-Za-z0-9_]{0,63}$");

        /// <summary>
        /// Reads a param's enum captions. Accepted shapes:
        ///
        ///   "optionLabels": { "Rect": { "en": "Rectangle", "zh-CN": "矩形" } }
        ///   "optionLabels": [ { "value": "Rect", "label": { "en": "Rectangle" } } ]
        ///
        /// The object form is what a package with one language will write; the array form keeps
        /// declaration order explicit. Keys are matched case-sensitively, because they are enum
        /// member names and the dropdown looks them up by exactly that spelling.
        /// </summary>
        private static void ReadOptionLabels(JObject p, ParamSpec spec)
        {
            var token = p["optionLabels"] ?? p["option_labels"];
            if (token == null) return;

            if (token is JObject map)
            {
                foreach (var kv in map.Properties())
                {
                    var option = new OptionSpec { Key = kv.Name };
                    ReadLocales(kv.Value, option.Value);
                    if (option.Value.Count > 0) spec.OptionLabels.Add(option);
                }
            }
            else if (token is JArray arr)
            {
                foreach (var item in arr)
                {
                    if (!(item is JObject entry)) continue;
                    var key = entry.Value<string>("value") ?? entry.Value<string>("name");
                    if (string.IsNullOrEmpty(key)) continue;
                    var option = new OptionSpec { Key = key };
                    ReadLocales(entry["label"] ?? entry["name"], option.Value);
                    if (option.Value.Count > 0) spec.OptionLabels.Add(option);
                }
            }
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
            else throw new FormatException("parameter conditions must be an array or object");
            if (result.Count % 2 != 0) throw new FormatException("parameter conditions must contain key/value pairs");
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
