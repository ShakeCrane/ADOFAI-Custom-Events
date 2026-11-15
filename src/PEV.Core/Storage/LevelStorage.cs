using System;
using System.Collections.Generic;
using System.Linq;
using ADOFAI;
using PEV.Model;
using PEV.Registry;
using PEV.Scripting;
using PEV.Util;
using UnityEngine;
using PropertyInfo = ADOFAI.PropertyInfo;

namespace PEV.Storage
{
    /// <summary>
    /// Bridges between the on-disk representation and virtual events in memory.
    ///
    /// Two carriers exist:
    ///
    ///   EditorComment with an @PEV1 body  — the original form. Invisible to the game, so
    ///                                       the event can never be selected or moved.
    ///   AddDecoration row with our marker  — what a decoration-backed manifest uses. The
    ///                                       row is a real decoration: scrDecorationManager
    ///                                       indexes it, so dragging, tags and
    ///                                       MoveDecorations all work with no code of ours.
    ///
    /// Invariants enforced here (see ARCHITECTURE-v1 §6.3):
    ///   1. only ours are touched
    ///   2. unparseable payloads are left byte-for-byte intact
    ///   3. nothing is deleted silently
    ///   4. a failed encode leaves the originals in place
    ///   5. version mismatches never rebind to the wrong definition
    /// </summary>
    public static class LevelStorage
    {
        /// <summary>
        /// Hidden key carried inside each virtual event's data dictionary.
        ///
        /// Binding by object reference does not survive undo: the editor snapshots the
        /// level with LevelData.Copy(), which rebuilds every LevelEvent as a new object.
        /// A reference-keyed map would silently lose every custom event on the next save.
        ///
        /// LevelEvent.Copy() copies the data dictionary, so a key stored here travels with
        /// the event. Neither the inspector nor Encode looks at keys outside
        /// info.propertiesInfo, so this never reaches the file or the UI.
        /// </summary>
        private const string UidKey = "__pevUid";

        /// <summary>uid -> data. Keyed by identity that survives object replacement.</summary>
        private static readonly Dictionary<string, EventInstanceData> ByUid =
            new Dictionary<string, EventInstanceData>(StringComparer.Ordinal);

        /// <summary>Fast path for the common case; rebuilt transparently when stale.</summary>
        private static readonly Dictionary<LevelEvent, EventInstanceData> ByRef =
            new Dictionary<LevelEvent, EventInstanceData>();
        private static readonly HashSet<LevelEvent> Detached = new HashSet<LevelEvent>();

        public static int AttachedCount => ByUid.Count;

        public static bool TryGetData(LevelEvent ev, out EventInstanceData data)
        {
            data = null;
            if (ev == null || Detached.Contains(ev)) return false;

            if (ByRef.TryGetValue(ev, out data)) return true;

            // Reference miss means the event was replaced (undo/redo, or a copy we have
            // not seen yet). Recover through the uid the event carries.
            string uid = ev.ContainsKey(UidKey) ? ev.GetString(UidKey) : null;
            if (uid != null && ByUid.TryGetValue(uid, out data))
            {
                ByRef[ev] = data;
                return true;
            }
            return false;
        }

        /// <summary>Binds an editor event to its persisted data (place, copy, restore).</summary>
        public static void Attach(LevelEvent ev, EventInstanceData data)
        {
            if (ev == null || data == null) return;

            if (string.IsNullOrEmpty(data.Uid)) data.Uid = PEV.Util.StableId.NewUid();
            if (ByUid.TryGetValue(data.Uid, out var existing) && !ReferenceEquals(existing, data))
                data.Uid = StableId.NewUid();

            ByUid[data.Uid] = data;
            ByRef[ev] = data;
            Detached.Remove(ev);

            // Stamp the identity onto the event so it survives Copy().
            try { ev[UidKey] = data.Uid; } catch { }
        }

        /// <summary>Unbinds without touching the file. Bake only writes still-attached events.</summary>
        public static void Detach(LevelEvent ev)
        {
            if (ev == null) return;
            // Undo snapshots retain the uid. Keep its data until the level is cleared;
            // Bake walks the current event list, so deleted events are still excluded.
            ByRef.Remove(ev);
            Detached.Add(ev);
        }

        public static void Clear()
        {
            ByUid.Clear();
            ByRef.Clear();
            Detached.Clear();
        }

        /// <summary>
        /// Replaces @PEV1 comments with virtual events.
        ///
        /// Payloads that cannot be resolved (missing package, newer major version, broken
        /// body) still become virtual events, but placeholders: visible on the track so the
        /// author does not think their events vanished, never executed, and written back
        /// byte-for-byte on save.
        /// </summary>
        public static DecodeSummary Restore(LevelData level)
        {
            var summary = new DecodeSummary();

            // Decorations first. A decoration-backed event IS the decoration row, so it is
            // adopted in place rather than replaced by a new virtual event: replacing it
            // would leave the row in level.decorations with nothing attached to it, and the
            // engine would keep rendering a decoration that our bake would then duplicate.
            RestoreDecorations(level, summary);

            // Snapshot: adding to levelEvents while enumerating it throws.
            var snapshot = level.levelEvents.ToList();
            var consumed = new List<LevelEvent>();

            foreach (var ev in snapshot)
            {
                if (ev.eventType != LevelEventType.EditorComment) continue;
                string comment = ev.GetString("comment") ?? "";
                if (!EventInstanceData.LooksLikeOurs(comment)) continue;

                summary.Found++;

                var data = EventInstanceData.Parse(comment, out string error);
                if (data == null)
                {
                    summary.Malformed++;
                    summary.Problems.Add($"floor {ev.floor}: {error}");
                    if (AddPlaceholder(level, ev, comment, null, error, summary)) consumed.Add(ev);
                    continue;
                }

                if (!EventRegistry.TryGetById(data.Id, out var manifest))
                {
                    summary.MissingDefinition++;
                    string reason = $"package '{data.Id}' is not installed";
                    summary.Problems.Add($"floor {ev.floor}: no definition for '{data.Id}'");
                    if (AddPlaceholder(level, ev, comment, data, reason, summary)) consumed.Add(ev);
                    continue;
                }

                int majorLocal = Major(manifest.Version);
                int majorStored = Major(data.Version);
                if (majorStored > majorLocal)
                {
                    summary.DefinitionMismatch++;
                    string reason = $"written by v{data.Version}, local is v{manifest.Version}";
                    summary.Problems.Add($"floor {ev.floor}: '{data.Id}' {reason}");
                    if (AddPlaceholder(level, ev, comment, data, reason, summary)) consumed.Add(ev);
                    continue;
                }

                LevelEventType type = (LevelEventType)StableId.FromEventId(data.Id);
                if (!EventRegistry.TryGetInfo(type, out var info) ||
                    !EventRegistry.TryGetByType(type, out var registered) || registered.Id != data.Id)
                {
                    summary.MissingDefinition++;
                    string reason = $"'{data.Id}' registered but has no metadata";
                    summary.Problems.Add($"floor {ev.floor}: {reason}");
                    if (AddPlaceholder(level, ev, comment, data, reason, summary)) consumed.Add(ev);
                    continue;
                }

                data.Materialize(manifest.Params);

                // A decoration-backed package moved from comments to decoration rows. Charts
                // written before that change still hold comments, and leaving them alone would
                // keep those events unselectable — the exact problem the move was made to fix.
                if (manifest.DecorationBacked)
                {
                    if (MigrateToDecoration(level, ev, data, manifest, summary))
                    {
                        consumed.Add(ev);
                        summary.Restored++;
                        summary.Migrated++;
                    }
                    else
                    {
                        summary.Problems.Add($"floor {ev.floor}: '{data.Id}' could not be migrated to a decoration");
                    }
                    continue;
                }

                var instance = new LevelEvent(ev.floor, type, info)
                {
                    active = ev.active,
                    visible = ev.visible,
                    locked = ev.locked
                };
                foreach (var kv in data.Effective)
                {
                    try
                    {
                        // The editor's containers expect the concrete type its Encode
                        // path casts to; a raw JSON value would throw at save time.
                        info.propertiesInfo.TryGetValue(kv.Key, out var propInfo);
                        instance[kv.Key] = ParamCoercion.Coerce(propInfo, kv.Value);
                    }
                    catch (Exception e)
                    {
                        summary.Problems.Add($"floor {ev.floor}: param '{kv.Key}' rejected ({e.Message})");
                    }
                }

                level.levelEvents.Add(instance);
                Attach(instance, data);
                consumed.Add(ev);
                summary.Restored++;
            }

            foreach (var ev in consumed) level.levelEvents.Remove(ev);
            return summary;
        }

        /// <summary>
        /// Turns a legacy comment into a decoration row, keeping its parameters.
        ///
        /// The comment itself is dropped afterwards: its payload now lives on the row, and
        /// keeping both would render the event twice — once as an invisible comment and once
        /// as a real decoration.
        ///
        /// Geometry the old comment carried (size, position, rotation, tag, depth) is
        /// transferred onto the row as decoration properties. A comment had none of these
        /// natively, so this is where a chart authored before the move keeps its placement.
        /// </summary>
        private static bool MigrateToDecoration(LevelData level, LevelEvent source,
            EventInstanceData data, EventManifest manifest, DecodeSummary summary)
        {
            try
            {
                var rows = level.decorations;
                if (rows == null) return false;

                LevelEventType nativeType = LevelEventType.AddDecoration;
                if (!DecorationParams.TryGetNativeInfo(out var info)) return false;

                var row = new LevelEvent(source.floor, nativeType, info)
                {
                    active = source.active,
                    visible = source.visible,
                    locked = source.locked
                };

                row["decorationImage"] = DecorationCarrier.ImageMarker;
                info.propertiesInfo.TryGetValue("relativeTo", out var relativeToInfo);
                row["relativeTo"] = ParamCoercion.Coerce(relativeToInfo, source.floor >= 0 ? "Tile" : "Camera");
                row["depth"] = 0;
                row["tag"] = DecorationCarrier.DefaultTag;
                row["visible"] = true;
                row["opacity"] = 100f;
                row["color"] = "ff5454";

                // Old comment parameters map onto the decoration's own fields where a
                // counterpart exists, so a chart that used the previous geometry keeps it.
                //
                // Units do not carry over directly: a comment measured size in tiles, while a
                // decoration stores scale as a percentage of its own base. Converting through
                // the documented base size is what keeps a migrated field the size it was —
                // copying the number across would make every migrated field 100× too small.
                Copy(data, row, "size", v =>
                {
                    Vector2 tiles = ToVector2(v);
                    row["scale"] = new Vector2(
                        tiles.x / DecorationBridge.BaseSizeTiles.x * 100f,
                        tiles.y / DecorationBridge.BaseSizeTiles.y * 100f);
                });
                Copy(data, row, "positionOffset", v => row["position"] = ToVector2(v));
                Copy(data, row, "rotation", v => row["rotation"] = Convert.ToSingle(v));
                Copy(data, row, "tag", v => row["tag"] = v.ToString());
                Copy(data, row, "depth", v => row["depth"] = Convert.ToInt32(v));
                Copy(data, row, "opacity", v =>
                {
                    float pct = Convert.ToSingle(v);
                    // Zero is an explicitly transparent field. Missing opacity keeps 100.
                    row["opacity"] = Mathf.Clamp(pct, 0f, 100f);
                });
                Copy(data, row, "color", v => row["color"] = v.ToString());

                Adopt(row, data, summary);
                if (string.IsNullOrWhiteSpace(row.GetString("tag")))
                    row["tag"] = DecorationCarrier.DefaultTag;
                rows.Add(row);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Applies one raw value through <paramref name="apply"/>, if the payload has it.
        /// Separated out because every legacy key needs the same "present, usable, non-null"
        /// dance and repeating it inline made the migration unreadable.
        /// </summary>
        private static void Copy(EventInstanceData data, LevelEvent row, string name,
            Action<object> apply)
        {
            if (!data.Effective.TryGetValue(name, out var raw) || raw == null) return;
            try { apply(raw); } catch { }
        }

        /// <summary>Vector2 from any of the shapes our payloads can hold (list, JArray, Vector2).</summary>
        private static Vector2 ToVector2(object value)
        {
            if (value is Vector2 v) return v;
            try
            {
                if (value is Newtonsoft.Json.Linq.JArray arr && arr.Count >= 2)
                    return new Vector2(arr[0].ToObject<float>(), arr[1].ToObject<float>());
                if (value is System.Collections.IEnumerable seq && !(value is string))
                {
                    var parts = new List<float>();
                    foreach (var item in seq) parts.Add(Convert.ToSingle(item));
                    if (parts.Count >= 2) return new Vector2(parts[0], parts[1]);
                }
            }
            catch { }
            return Vector2.one;
        }

        /// <summary>
        /// Stands in for a payload we could not resolve. The original comment is kept so
        /// Bake can write it back unchanged; the fields only describe the problem.
        /// </summary>
        private static bool AddPlaceholder(LevelData level, LevelEvent source, string comment,
            EventInstanceData parsed, string reason, DecodeSummary summary)
        {
            try
            {
                LevelEventType type = (LevelEventType)StableId.FromEventId(PlaceholderEvent.Id);
                if (!EventRegistry.TryGetInfo(type, out var info))
                {
                    summary.Problems.Add("placeholder metadata unavailable; comment left as-is");
                    return false;
                }

                var data = parsed ?? new EventInstanceData();
                data.IsPlaceholder = true;
                data.PlaceholderReason = reason;
                data.OriginalComment = comment;

                string shownId = string.IsNullOrEmpty(data.Id)
                    ? (comment.Length > 40 ? comment.Substring(0, 40) + "…" : comment)
                    : data.Id;

                var instance = new LevelEvent(source.floor, type, info)
                {
                    active = source.active,
                    visible = source.visible,
                    locked = source.locked
                };
                instance["missingId"] = shownId;
                instance["missingVersion"] = data.Version ?? "";
                instance["missingUid"] = data.Uid ?? "";

                level.levelEvents.Add(instance);
                Attach(instance, data);
                summary.Placeholders++;
                return true;
            }
            catch (Exception e)
            {
                summary.Problems.Add("placeholder creation failed: " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// Bakes attached virtual events back into comments. Called during encode so the
        /// written file never contains a custom eventType.
        /// </summary>
        public static EncodeSummary Bake(LevelData level, List<Dictionary<string, object>> actions)
        {
            var summary = new EncodeSummary();
            if (actions == null) return summary;

            // TryGetData also recovers events whose object identity changed under us
            // (undo/redo rebuilds the list), so querying it here is what keeps those
            // instances visible to the encoder.
            var virtuals = level.levelEvents
                .Where(e => e.active && TryGetData(e, out _))
                .ToList();

            if (virtuals.Count == 0) return summary;

            // Strip only what this encode supersedes:
            //   a) custom types the vanilla encoder emitted (should not happen, cheap guard)
            //   b) payload comments whose uid matches an instance we are about to rewrite
            // Everything else — malformed payloads, comments with no installed definition,
            // comments written for a newer major version — must survive untouched.
            var liveUids = new HashSet<string>();
            foreach (var e in virtuals)
                if (TryGetData(e, out var d) && !string.IsNullOrEmpty(d.Uid)) liveUids.Add(d.Uid);

            // Build replacements before removing any original output. A failed serialization
            // must leave the vanilla encoder's result intact.
            var replacements = new List<Dictionary<string, object>>();
            foreach (var ev in virtuals)
            {
                if (!TryGetData(ev, out var data)) continue;
                EventRegistry.TryGetInfo(ev.eventType, out var info);
                foreach (var prop in data.Effective.Keys.ToList())
                {
                    try
                    {
                        object live = ev.Get<object>(prop);
                        if (live == null) continue;
                        PropertyInfo pi = null;
                        info?.propertiesInfo.TryGetValue(prop, out pi);
                        data.Raw[prop] = StorageValue.Canonical(pi, live);
                        data.Effective[prop] = live;
                    }
                    catch { }
                }
                replacements.Add(new Dictionary<string, object>
                {
                    { "floor", ev.floor },
                    { "eventType", "EditorComment" },
                    { "comment", data.ToComment() },
                    { "visible", ev.visible },
                    { "locked", ev.locked }
                });
            }

            summary.RemovedStale = actions.RemoveAll(a =>
            {
                if (!a.TryGetValue("eventType", out var t)) return false;
                string typeName = t as string;
                if (typeName == null) return false;

                if (EventRegistry.All.Any(m => ((int)StableId.FromEventId(m.Id)).ToString() == typeName))
                    return true;

                if (typeName != "EditorComment") return false;
                if (!a.TryGetValue("comment", out var c)) return false;
                string comment = c as string;
                if (!EventInstanceData.LooksLikeOurs(comment)) return false;

                var existing = EventInstanceData.Parse(comment, out _);
                return existing != null && liveUids.Contains(existing.Uid ?? "");
            });

            actions.AddRange(replacements);
            summary.Written = replacements.Count;

            return summary;
        }

        /// <summary>
        /// Adopts decoration rows that carry our marker.
        ///
        /// The row is kept in place and attached, never rebuilt: it is a real decoration, and
        /// replacing it with a virtual event would leave the engine owning an object that
        /// nothing writes back to. Adoption is what makes the effect "just a decoration" —
        /// the engine keeps moving it, and we only read its geometry.
        ///
        /// A row whose payload will not resolve is left completely alone. It is still a valid
        /// decoration that vanilla opens without complaint; reinstalling the package brings
        /// the effect back with its settings intact, which is a better outcome than deleting
        /// the author's placement.
        /// </summary>
        private static void RestoreDecorations(LevelData level, DecodeSummary summary)
        {
            List<LevelEvent> rows;
            try { rows = level.decorations; }
            catch { return; }
            if (rows == null) return;

            foreach (var ev in rows)
            {
                if (ev == null) continue;

                string image = null;
                try { image = ev.GetString("decorationImage"); } catch { }
                if (!DecorationCarrier.IsOurs(image)) continue;

                summary.Found++;

                string payload = null;
                try { payload = ev.GetString("maskingTarget"); } catch { }

                if (!DecorationCarrier.TryUnpack(payload, out var data))
                {
                    summary.Malformed++;
                    summary.Problems.Add($"decoration {Describe(ev)}: payload unreadable");
                    continue;
                }

                if (!EventRegistry.TryGetById(data.Id, out var manifest))
                {
                    summary.MissingDefinition++;
                    summary.Problems.Add($"decoration {Describe(ev)}: package '{data.Id}' is not installed");
                    continue;
                }

                int majorLocal = Major(manifest.Version);
                int majorStored = Major(data.Version);
                if (majorStored > majorLocal)
                {
                    summary.DefinitionMismatch++;
                    summary.Problems.Add(
                        $"decoration {Describe(ev)}: '{data.Id}' written by v{data.Version}, local is v{manifest.Version}");
                    continue;
                }

                data.Materialize(manifest.Params);
                Adopt(ev, data, summary);
                summary.Restored++;
            }
        }

        /// <summary>
        /// Binds payload values onto the decoration row.
        ///
        /// The values must land in the event's data dictionary, because that is what the
        /// panel reads and what the script sees. They also have to be re-enabled: Decode
        /// marks every property absent from the file as disabled, and ours are absent by
        /// design — they live in the payload. Without this every row would open greyed out
        /// and read as if it were locked.
        /// </summary>
        private static void Adopt(LevelEvent ev, EventInstanceData data, DecodeSummary summary)
        {
            // Parameter injection normally runs before any level is decoded, but decode can
            // beat the editor's first LoadEditorProperties on a cold start. Publishing here is
            // idempotent and makes the coercion below independent of that ordering: without a
            // PropertyInfo the values would land as raw strings and the panel would render
            // them in whichever control the type guess produced.
            try { Registry.DecorationParams.Publish(); } catch { }

            foreach (var kv in data.Effective)
            {
                PropertyInfo pi = null;
                try { ev.info?.propertiesInfo.TryGetValue(kv.Key, out pi); } catch { }

                object coerced;
                try { coerced = ParamCoercion.Coerce(pi, kv.Value); }
                catch (Exception e)
                {
                    summary.Problems.Add($"decoration {Describe(ev)}: param '{kv.Key}' rejected ({e.Message})");
                    continue;
                }

                try
                {
                    ev[kv.Key] = coerced;
                    if (ev.disabled != null) ev.disabled[kv.Key] = false;
                }
                catch { /* a property the row cannot hold is not worth failing the load */ }
            }

            Attach(ev, data);
        }

        /// <summary>
        /// Writes the payload back onto every decoration row we own, before the encoder runs.
        ///
        /// This has to happen in a Prefix: LevelData's own encoder serialises the decorations
        /// list inside EncodeToDictionary, so a postfix would find the string already built.
        /// */
        public static void SyncDecorations(LevelData level)
        {
            if (level == null) return;

            List<LevelEvent> rows;
            try { rows = level.decorations; }
            catch { return; }
            if (rows == null) return;

            foreach (var ev in rows)
            {
                if (ev == null) continue;
                if (!TryGetData(ev, out var data)) continue;
                if (data.IsPlaceholder) continue;

                string image = null;
                try { image = ev.GetString("decorationImage"); } catch { }
                if (!DecorationCarrier.IsOurs(image)) continue;

                // Pull live panel values into the payload, in the shape a human would write.
                foreach (var prop in data.Effective.Keys.ToList())
                {
                    try
                    {
                        object live = ev.Get<object>(prop);
                        if (live == null) continue;
                        PropertyInfo pi = null;
                        ev.info?.propertiesInfo.TryGetValue(prop, out pi);
                        data.Raw[prop] = StorageValue.Canonical(pi, live);
                        data.Effective[prop] = live;
                    }
                    catch { /* keep the previously stored value */ }
                }

                try
                {
                    ev["maskingTarget"] = DecorationCarrier.Pack(data);

                    // LevelEvent.Encode skips a property marked disabled, and Decode marks
                    // every property absent from the file as disabled. maskingTarget is hidden
                    // from the panel and is usually absent at that point, so without this the
                    // payload is written to the live object and then silently dropped by the
                    // encoder — the chart would reopen with defaults and no error anywhere.
                    if (ev.disabled != null) ev.disabled["maskingTarget"] = false;
                }
                catch (Exception e)
                {
                    PEV.Core.Plugin.Log($"decoration sync failed on floor {ev.floor}: {e.Message}");
                }
            }
        }

        private static string Describe(LevelEvent ev)
        {
            string tag = null;
            try { tag = ev.GetString("tag"); } catch { }
            return string.IsNullOrEmpty(tag)
                ? $"on floor {ev.floor}"
                : $"'{tag}' on floor {ev.floor}";
        }

        private static int Major(string version)
        {
            if (string.IsNullOrEmpty(version)) return 0;
            string head = version.Split('.')[0];
            return int.TryParse(head, out int n) ? n : 0;
        }
    }

    public class DecodeSummary
    {
        public int Found;
        public int Restored;
        public int Malformed;
        public int MissingDefinition;
        public int DefinitionMismatch;
        public int Placeholders;

        /// <summary>Events that moved from an EditorComment to a decoration row.</summary>
        public int Migrated;

        public readonly List<string> Problems = new List<string>();

        public override string ToString() =>
            $"found={Found}, restored={Restored}, malformed={Malformed}, " +
            $"missing={MissingDefinition}, mismatch={DefinitionMismatch}, " +
            $"placeholders={Placeholders}, migrated={Migrated}";
    }

    public class EncodeSummary
    {
        public int Written;
        public int RemovedStale;

        public override string ToString() => $"written={Written}, removedStale={RemovedStale}";
    }
}
