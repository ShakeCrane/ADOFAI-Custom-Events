using System;
using System.Collections.Generic;
using System.Linq;
using ADOFAI;
using PEV.Model;
using PEV.Registry;
using PEV.Util;

namespace PEV.Storage
{
    /// <summary>
    /// Bridges between EditorComment rows on disk and virtual LevelEvents in memory.
    ///
    /// Invariants enforced here (see ARCHITECTURE-v1 §6.3):
    ///   1. only @PEV1 comments are touched
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

        public static int AttachedCount => ByUid.Count;

        public static bool TryGetData(LevelEvent ev, out EventInstanceData data)
        {
            data = null;
            if (ev == null) return false;

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

            ByUid[data.Uid] = data;
            ByRef[ev] = data;

            // Stamp the identity onto the event so it survives Copy().
            try { ev[UidKey] = data.Uid; } catch { }
        }

        /// <summary>Unbinds without touching the file. Bake only writes still-attached events.</summary>
        public static void Detach(LevelEvent ev)
        {
            if (ev == null) return;

            if (ByRef.TryGetValue(ev, out var data))
            {
                ByRef.Remove(ev);
                ByUid.Remove(data.Uid);
            }
            else
            {
                string uid = ev.ContainsKey(UidKey) ? ev.GetString(UidKey) : null;
                if (uid != null) ByUid.Remove(uid);
            }
        }

        public static void Clear()
        {
            ByUid.Clear();
            ByRef.Clear();
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
                    consumed.Add(ev);
                    AddPlaceholder(level, ev, comment, null, error, summary);
                    continue;
                }

                if (!EventRegistry.TryGetById(data.Id, out var manifest))
                {
                    summary.MissingDefinition++;
                    string reason = $"package '{data.Id}' is not installed";
                    summary.Problems.Add($"floor {ev.floor}: no definition for '{data.Id}'");
                    consumed.Add(ev);
                    AddPlaceholder(level, ev, comment, data, reason, summary);
                    continue;
                }

                int majorLocal = Major(manifest.Version);
                int majorStored = Major(data.Version);
                if (majorStored > majorLocal)
                {
                    summary.DefinitionMismatch++;
                    string reason = $"written by v{data.Version}, local is v{manifest.Version}";
                    summary.Problems.Add($"floor {ev.floor}: '{data.Id}' {reason}");
                    consumed.Add(ev);
                    AddPlaceholder(level, ev, comment, data, reason, summary);
                    continue;
                }

                LevelEventType type = (LevelEventType)StableId.FromEventId(data.Id);
                if (!EventRegistry.TryGetInfo(type, out var info))
                {
                    summary.MissingDefinition++;
                    string reason = $"'{data.Id}' registered but has no metadata";
                    summary.Problems.Add($"floor {ev.floor}: {reason}");
                    consumed.Add(ev);
                    AddPlaceholder(level, ev, comment, data, reason, summary);
                    continue;
                }

                data.Materialize(manifest.Params);

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
        /// Stands in for a payload we could not resolve. The original comment is kept so
        /// Bake can write it back unchanged; the fields only describe the problem.
        /// </summary>
        private static void AddPlaceholder(LevelData level, LevelEvent source, string comment,
            EventInstanceData parsed, string reason, DecodeSummary summary)
        {
            try
            {
                LevelEventType type = (LevelEventType)StableId.FromEventId(PlaceholderEvent.Id);
                if (!EventRegistry.TryGetInfo(type, out var info))
                {
                    summary.Problems.Add("placeholder metadata unavailable; comment left as-is");
                    return;
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
            }
            catch (Exception e)
            {
                summary.Problems.Add("placeholder creation failed: " + e.Message);
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
                .Where(e => TryGetData(e, out _))
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

            foreach (var ev in virtuals)
            {
                if (!TryGetData(ev, out var data)) continue;
                EventRegistry.TryGetInfo(ev.eventType, out var info);

                // Pull the live editor values back into the persisted payload, in the
                // shape a human would have written (enum names, arrays for compounds).
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
                    catch { /* keep the previously stored value */ }
                }

                actions.Add(new Dictionary<string, object>
                {
                    { "floor", ev.floor },
                    { "eventType", "EditorComment" },
                    { "comment", data.ToComment() }
                });
                summary.Written++;
            }

            return summary;
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
        public readonly List<string> Problems = new List<string>();

        public override string ToString() =>
            $"found={Found}, restored={Restored}, malformed={Malformed}, " +
            $"missing={MissingDefinition}, mismatch={DefinitionMismatch}, placeholders={Placeholders}";
    }

    public class EncodeSummary
    {
        public int Written;
        public int RemovedStale;

        public override string ToString() => $"written={Written}, removedStale={RemovedStale}";
    }
}
