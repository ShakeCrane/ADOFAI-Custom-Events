using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using ADOFAI;
using PEV.Model;
using PEV.Util;
using UnityEngine;
using PropertyInfo = ADOFAI.PropertyInfo;

namespace PEV.Registry
{
    /// <summary>
    /// Turns manifests into game event metadata and keeps the id -> definition map.
    /// Stage 0 established the constraints encoded here:
    ///   - custom types are addressed by numeric string names (no ParseEnum patch needed)
    ///   - every registered type MUST get an icon or the palette build throws
    /// </summary>
    public static class EventRegistry
    {
        private static readonly Dictionary<string, EventManifest> ById =
            new Dictionary<string, EventManifest>(StringComparer.Ordinal);

        private static readonly Dictionary<LevelEventType, EventManifest> ByType =
            new Dictionary<LevelEventType, EventManifest>();

        private static readonly Dictionary<LevelEventType, LevelEventInfo> InfoByType =
            new Dictionary<LevelEventType, LevelEventInfo>();

        public static IReadOnlyCollection<EventManifest> All => ById.Values;

        public static bool TryGetById(string id, out EventManifest manifest) =>
            ById.TryGetValue(id ?? "", out manifest);

        public static bool TryGetByType(LevelEventType type, out EventManifest manifest) =>
            ByType.TryGetValue(type, out manifest);

        public static bool TryGetInfo(LevelEventType type, out LevelEventInfo info) =>
            InfoByType.TryGetValue(type, out info);

        /// <summary>
        /// Adds manifests to the registry and, if the game is already running,
        /// into the dictionaries the editor reads.
        /// </summary>
        public static void RegisterAll(IEnumerable<EventManifest> manifests, Action<string> warn = null)
        {
            foreach (var m in manifests)
            {
                if (ById.TryGetValue(m.Id, out var existing))
                {
                    int cmp = CompareVersions(m.Version, existing.Version);
                    if (cmp == 0)
                    {
                        warn?.Invoke($"duplicate id '{m.Id}' version {m.Version}; keeping {existing.SourcePath}");
                        continue;
                    }
                    if (cmp < 0)
                    {
                        warn?.Invoke($"'{m.Id}' version {m.Version} is older than {existing.Version}; ignored");
                        continue;
                    }
                    warn?.Invoke($"'{m.Id}' upgraded {existing.Version} -> {m.Version}");
                    ById[m.Id] = m;
                    continue;
                }
                ById[m.Id] = m;
            }
        }

        /// <summary>
        /// Publishes every manifest into the game's metadata tables.
        /// Call after ADOStartup.SetupLevelEventsInfo, before InspectorPanel.Init.
        /// </summary>
        public static void PublishToGame(Action<string> log = null)
        {
            if (GCS.levelEventsInfo == null || GCS.levelEventTypeString == null)
            {
                log?.Invoke("game metadata tables not ready; publish skipped");
                return;
            }

            foreach (var m in ById.Values)
            {
                LevelEventType type = (LevelEventType)StableId.FromEventId(m.Id);
                if (ByType.ContainsKey(type) && ByType[type].Id != m.Id)
                {
                    log?.Invoke($"type id collision: '{m.Id}' vs '{ByType[type].Id}'");
                    continue;
                }

                LevelEventInfo info = BuildInfo(m, type);
                string key = ((int)type).ToString();

                GCS.levelEventsInfo[key] = info;
                GCS.levelEventTypeString[type] = key;
                EnsureIcon(type, m);

                ByType[type] = m;
                InfoByType[type] = info;
            }

            log?.Invoke($"published {ByType.Count} event types to the editor");
        }

        private static LevelEventInfo BuildInfo(EventManifest m, LevelEventType type)
        {
            // Grouped panels are what make a 25-parameter event navigable: the inspector
            // renders one tab per groupName, exactly as native events do. Only switch it on
            // when the manifest actually declares groups, since the flag also changes the
            // panel layout.
            // Panel grouping is only relevant for events that declare their own properties.
            // A decoration-backed type has none here — they live on AddDecoration's panel — so
            // switching grouping on would create an empty tab set behind a palette button.
            bool grouped = !m.DecorationBacked && m.Params.Any(p => !string.IsNullOrEmpty(p.Group));

            var info = new LevelEventInfo
            {
                name = ((int)type).ToString(),
                type = type,
                categories = new List<LevelEventCategory> { ParseCategory(m.Category) },
                propertiesInfo = new Dictionary<string, PropertyInfo>(),
                executionTime = LevelEventExecutionTime.OnBar,
                allowFirstFloor = true,
                isDecoration = false,
                useGroups = grouped,
                groups = BuildGroups(m),
                stretchViewport = false,
                taroDLC = false,
                pro = false
            };

            foreach (var spec in m.Params)
            {
                try
                {
                    // A decoration-backed manifest keeps its palette entry but does not declare
                    // its own properties: they were injected into AddDecoration instead, and a
                    // second copy here would build a parallel panel nobody can open. Leaving
                    // them out is also what keeps the type from looking like a normal event.
                    if (m.DecorationBacked) continue;

                    info.propertiesInfo[spec.Name] = new PropertyInfo(spec.ToPropertyDict(), info);
                }
                catch (Exception)
                {
                    // A bad type literal silently yields no control; record it as a string instead
                    // so the author at least sees the field.
                    var fallback = new Dictionary<string, object>
                    {
                        { "name", spec.Name }, { "type", "String" }
                    };
                    if (spec.Default != null) fallback["default"] = spec.Default.ToString();
                    info.propertiesInfo[spec.Name] = new PropertyInfo(fallback, info);
                }
            }
            return info;
        }

        /// <summary>
        /// Sidebar sub-tabs, in first-declaration order. isDefault marks the tab the panel
        /// opens on, so the geometry group is picked deliberately rather than by luck.
        /// </summary>
        private static List<LevelEventInfo.Group> BuildGroups(EventManifest m)
        {
            var groups = new List<LevelEventInfo.Group>();
            var names = new List<string>();
            foreach (var p in m.Params)
            {
                if (string.IsNullOrEmpty(p.Group) || names.Contains(p.Group)) continue;
                names.Add(p.Group);
            }
            for (int i = 0; i < names.Count; i++)
            {
                groups.Add(new LevelEventInfo.Group
                {
                    name = names[i],
                    icon = "",
                    isDefault = i == 0
                });
            }
            return groups;
        }

        private static LevelEventCategory ParseCategory(string name)
        {
            if (Enum.TryParse(name, true, out LevelEventCategory parsed) &&
                Enum.IsDefined(typeof(LevelEventCategory), parsed))
                return parsed;
            return LevelEventCategory.VisualFx;
        }

        /// <summary>
        /// Stage 0 finding: GCS.levelEventIcons[type] is indexed unconditionally, so a
        /// missing key aborts LoadEditorProperties and leaves the editor half-built.
        ///
        /// Ordering matters: the icon table does not exist yet at SetupLevelEventsInfo
        /// time, so EnsureIcon is a no-op there. Call this again from a patch that runs
        /// inside scnEditor.Start, after the sprites are loaded.
        /// </summary>
        public static void EnsureAllIcons(Action<string> log = null)
        {
            if (GCS.levelEventIcons == null) return;
            foreach (var kv in ByType)
                EnsureIcon(kv.Key, kv.Value);
        }

        private static void EnsureIcon(LevelEventType type, EventManifest m)
        {
            if (GCS.levelEventIcons == null) return;
            if (GCS.levelEventIcons.ContainsKey(type)) return;

            Sprite custom = TryLoadIcon(m);
            if (custom != null)
            {
                GCS.levelEventIcons[type] = custom;
                return;
            }

            // Borrow any built-in sprite so the palette stays constructible.
            foreach (var kv in GCS.levelEventIcons)
            {
                if (kv.Value != null)
                {
                    GCS.levelEventIcons[type] = kv.Value;
                    return;
                }
            }
        }

        private static Sprite TryLoadIcon(EventManifest m)
        {
            if (string.IsNullOrEmpty(m.IconPath)) return null;
            Texture2D tex = null;
            try
            {
                byte[] bytes = PEV.Loading.EventLoader.ReadResource(m.SourcePath, m.IconPath);
                if (bytes == null || bytes.Length == 0) return null;
                tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!DecodeImage(tex, bytes)) { UnityEngine.Object.Destroy(tex); return null; }
                return Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
            }
            catch
            {
                if (tex != null) UnityEngine.Object.Destroy(tex);
                return null;
            }
        }

        /// <summary>
        /// Texture2D.LoadImage lives in UnityEngine.ImageConversionModule, which targets
        /// netstandard2.1 and cannot be referenced from a net48 assembly. Call it via
        /// reflection instead of dragging in that dependency.
        /// </summary>
        private static bool DecodeImage(Texture2D tex, byte[] bytes)
        {
            try
            {
                var t = Type.GetType("UnityEngine.ImageConversion, UnityEngine.ImageConversionModule");
                var method = t?.GetMethod("LoadImage",
                    new[] { typeof(Texture2D), typeof(byte[]), typeof(bool) });
                if (method == null) return false;
                object result = method.Invoke(null, new object[] { tex, bytes, false });
                return result is bool ok && ok;
            }
            catch
            {
                return false;
            }
        }

        private static int CompareVersions(string a, string b)
        {
            if (Version.TryParse(Normalize(a), out var va) && Version.TryParse(Normalize(b), out var vb))
                return va.CompareTo(vb);
            return string.CompareOrdinal(a, b);
        }

        private static string Normalize(string v)
        {
            if (string.IsNullOrEmpty(v)) return "0.0.0";
            int dots = v.Count(c => c == '.');
            while (dots < 2) { v += ".0"; dots++; }
            return v;
        }
    }
}
