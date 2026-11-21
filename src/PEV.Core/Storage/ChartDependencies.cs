using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using ADOFAI;
using Newtonsoft.Json.Linq;
using PEV.Registry;

namespace PEV.Storage
{
    /// <summary>Chart-owned declarations. The vanilla decoder ignores this root key.</summary>
    internal static class ChartDependencies
    {
        internal const string Key = "pevEvents";
        internal sealed class Entry
        {
            internal string Id, Version, File;
            internal Dictionary<string, object> Raw;
        }
        internal sealed class State
        {
            internal readonly List<Entry> Entries = new List<Entry>();
            internal object InvalidRaw;
            internal string Error;
        }
        private static readonly ConditionalWeakTable<LevelData, State> States = new ConditionalWeakTable<LevelData, State>();

        internal static State Read(IDictionary<string, object> root)
        {
            var state = new State();
            if (root == null || !root.TryGetValue(Key, out var raw) || raw == null) return state;
            try
            {
                var token = raw as JToken ?? JToken.FromObject(raw);
                if (!(token is JArray entries) || entries.Count > 256) throw new ArgumentException("事件依赖声明必须是最多 256 项的数组");
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var value in entries)
                {
                    if (!(value is JObject item) || item["id"]?.Type != JTokenType.String)
                        throw new ArgumentException("事件依赖缺少 ID");
                    string id = item.Value<string>("id"), file = item.Value<string>("file"), version = item.Value<string>("version");
                    if (string.IsNullOrWhiteSpace(id) || id.Length > 256 || !seen.Add(id) ||
                        (item["file"] != null && item["file"].Type != JTokenType.String) ||
                        (item["version"] != null && item["version"].Type != JTokenType.String) ||
                        (file != null && !IsFileName(file)))
                        throw new ArgumentException("事件依赖的 ID 或文件名无效");
                    state.Entries.Add(new Entry { Id = id, File = file, Version = version, Raw = (Dictionary<string, object>)Plain(item) });
                }
            }
            catch (Exception e)
            {
                state.Entries.Clear(); state.InvalidRaw = raw is JToken token ? Plain(token) : raw; state.Error = e.Message;
            }
            return state;
        }
        internal static bool IsFileName(string file) => !string.IsNullOrWhiteSpace(file) && file.Length <= 255 &&
            file.EndsWith(".event", StringComparison.OrdinalIgnoreCase) && file.IndexOfAny(new[] { '/', '\\', ':' }) < 0 &&
            file != "." && file != ".." && file.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

        internal static void Set(LevelData level, State state)
        {
            if (level == null) return;
            States.Remove(level); States.Add(level, state ?? new State());
        }
        internal static State Get(LevelData level) => level != null && States.TryGetValue(level, out var state) ? state : null;
        internal static void Copy(LevelData from, LevelData to)
        {
            var state = Get(from);
            if (state == null || to == null) return;
            // Existing declarations are not mutated; Encode makes a fresh copy.
            Set(to, state);
        }
        internal static void Write(LevelData level, IDictionary<string, object> encoded)
        {
            if (level == null || encoded == null) return;
            var state = Get(level) ?? new State();
            if (state.InvalidRaw != null) { encoded[Key] = state.InvalidRaw; return; }
            var entries = state.Entries.Select(x => new Dictionary<string, object>(x.Raw, StringComparer.Ordinal)).ToList();
            var byId = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
            foreach (var entry in entries) byId[(string)entry["id"]] = entry;
            foreach (var data in UsedEvents(encoded))
            {
                if (!byId.TryGetValue(data.Id, out var entry))
                {
                    entry = new Dictionary<string, object>(StringComparer.Ordinal) { ["id"] = data.Id };
                    entries.Add(entry); byId.Add(data.Id, entry);
                }
                if (!entry.ContainsKey("version") && !string.IsNullOrEmpty(data.Version)) entry["version"] = data.Version;
                if (!entry.ContainsKey("file") && EventRegistry.TryGetById(data.Id, out var manifest) &&
                    !string.IsNullOrEmpty(manifest.SourcePath))
                {
                    string file = Path.GetFileName(manifest.SourcePath);
                    if (IsFileName(file)) entry["file"] = file;
                }
            }
            foreach (var entry in entries)
            {
                if (entry.ContainsKey("file") || !EventRegistry.TryGetById((string)entry["id"], out var manifest) ||
                    string.IsNullOrEmpty(manifest.SourcePath)) continue;
                string file = Path.GetFileName(manifest.SourcePath);
                if (IsFileName(file)) entry["file"] = file;
            }
            if (entries.Count > 0) encoded[Key] = entries; else encoded.Remove(Key);
        }
        internal static IEnumerable<EventInstanceData> UsedEvents(IDictionary<string, object> encoded)
        {
            foreach (string name in new[] { "actions", "decorations" })
            {
                if (!encoded.TryGetValue(name, out var rows) || !(rows is IEnumerable sequence) || rows is string) continue;
                foreach (var raw in sequence)
                {
                    string text = ReadString(raw, "comment");
                    if (EventInstanceData.LooksLikeOurs(text))
                    {
                        var parsed = EventInstanceData.Parse(text, out _);
                        if (parsed != null) yield return parsed;
                    }
                    if (DecorationCarrier.TryUnpack(ReadString(raw, "maskingTarget"), out var decoration))
                        yield return decoration;
                }
            }
        }
        private static string ReadString(object row, string name)
        {
            if (row is JObject json) return json[name]?.Type == JTokenType.String ? json.Value<string>(name) : null;
            return row is IDictionary<string, object> data && data.TryGetValue(name, out var value) ? value as string : null;
        }
        private static object Plain(JToken token)
        {
            if (token is JObject obj) return obj.Properties().ToDictionary(p => p.Name, p => Plain(p.Value), StringComparer.Ordinal);
            if (token is JArray array) return array.Select(Plain).ToList();
            return token is JValue scalar ? scalar.Value : null;
        }
    }
}
