using System;
using System.Collections.Generic;
using ADOFAI;
using HarmonyLib;
using PEV.Core;
using PEV.Registry;
using PEV.Storage;
using PEV.Util;
using UnityEngine;
using PropertyInfo = ADOFAI.PropertyInfo;

namespace PEV.Editor
{
    /// <summary>
    /// Places a decoration-backed event the way the editor places a decoration.
    ///
    /// The palette button for a decoration-backed manifest must not create a custom event
    /// type: it has to produce a real AddDecoration row, because that row is what the engine
    /// indexes, draws a gizmo for, and finds by tag in MoveDecorations.
    ///
    /// So the click is intercepted and handed to scnEditor.AddDecoration, the game's own
    /// placement entry. That call already knows where a new decoration belongs — on the
    /// selected tile with zero offset, or at the camera centre when nothing is selected —
    /// and it already registers the object. Reimplementing that would be both longer and
    /// subtly wrong.
    /// </summary>
    public static class DecorationPlacement
    {
        [HarmonyPatch]
        public static class AddEventAtSelectedPatch
        {
            public static System.Reflection.MethodBase TargetMethod()
            {
                return AccessTools.Method(typeof(scnEditor), "AddEventAtSelected",
                    new[] { typeof(LevelEventType) });
            }

            /// <summary>
            /// Returns false for decoration-backed types: the original would have built a
            /// custom event object that nothing renders, and the decoration path below
            /// replaces it entirely.
            /// </summary>
            public static bool Prefix(LevelEventType eventType)
            {
                try
                {
                    if (!EventRegistry.TryGetByType(eventType, out var manifest)) return true;
                    if (!manifest.DecorationBacked) return true;

                    Place(manifest);
                    return false;
                }
                catch (Exception e)
                {
                    Plugin.Log("decoration placement failed: " + e);
                    return false;
                }
            }
        }

        /// <summary>
        /// Duplicating a field must give the copy its own identity. The editor clones the
        /// LevelEvent, and our uid travels inside its data dictionary, so without this the
        /// copy would share the original's renderer scope and the two would render as one.
        /// </summary>
        [HarmonyPatch]
        public static class PasteDecorationsPatch
        {
            public static System.Reflection.MethodBase TargetMethod()
            {
                return AccessTools.Method(typeof(scnEditor), "PasteDecorations",
                    new[] { typeof(bool) });
            }

            public static void Postfix()
            {
                try { EnsureUniqueUids(); }
                catch (Exception e) { Plugin.Log("decoration paste fixup failed: " + e.Message); }
            }
        }

        /// <summary>
        /// Keeps the payload in step with what the panel shows.
        ///
        /// Property edits land in the row's data dictionary, but the payload — the only
        /// place our parameters survive a save — is written by LevelStorage.SyncDecorations.
        /// Running it as the row updates means a save immediately after an edit already
        /// carries the new values, with no "did you touch anything else" dependency.
        /// </summary>
        [HarmonyPatch]
        public static class DecorationChangedPatch
        {
            public static System.Reflection.MethodBase TargetMethod()
            {
                // UpdateDecorationObject(LevelEvent) — one argument. Finding it by signature
                // matters: a miss here makes PatchAll throw and the whole mod fails to load.
                return AccessTools.Method(typeof(scnEditor), "UpdateDecorationObject",
                    new[] { typeof(LevelEvent) });
            }

            public static void Postfix(LevelEvent e)
            {
                try
                {
                    if (e == null) return;
                    if (!LevelStorage.TryGetData(e, out _)) return;

                    var level = ADOBase.editor != null ? ADOBase.editor.levelData : null;
                    if (level != null) LevelStorage.SyncDecorations(level);
                }
                catch (Exception ex)
                {
                    Plugin.Log("decoration sync after edit failed: " + ex.Message);
                }
            }
        }

        private static void Place(PEV.Model.EventManifest manifest)
        {
            var editor = ADOBase.editor;
            if (editor == null) return;

            var level = editor.levelData;
            if (level == null) return;

            // Snapshot by identity, not by count. AddDecoration inserts at an index derived
            // from the current selection, so "the row after the old last one" can be an
            // existing native decoration — which would then be given our marker and start
            // rendering a field the author never placed.
            var existing = new HashSet<LevelEvent>();
            try
            {
                var decorations = level.decorations;
                if (decorations != null)
                    foreach (var d in decorations) if (d != null) existing.Add(d);
            }
            catch { }

            // Let the game decide where the decoration goes and build its object.
            editor.AddDecoration(LevelEventType.AddDecoration, -1);

            var row = FindAdded(level.decorations, existing);
            if (row == null)
            {
                Plugin.Log("AddDecoration produced no row; placement aborted");
                return;
            }

            var data = new EventInstanceData
            {
                Id = manifest.Id,
                Version = manifest.Version,
                Uid = StableId.NewUid()
            };
            data.Materialize(manifest.Params);

            // The parameters need their PropertyInfo before they can be coerced; publishing is
            // idempotent and this is the one place a row can be created before the editor has
            // ever loaded its property tables.
            try { DecorationParams.Publish(); } catch { }

            // The marker is what every other subsystem keys on: Restore recognises the row by
            // it, the injected parameter group shows itself by matching it, and the effect
            // renderer only mirrors rows that carry it.
            row["decorationImage"] = DecorationCarrier.ImageMarker;

            // Seed the panel with the manifest defaults and enable them. Decode marks absent
            // properties disabled; a freshly created row has no such history, but doing this
            // explicitly keeps placement and reload producing identical rows.
            foreach (var kv in data.Effective)
            {
                try
                {
                    PropertyInfo pi = null;
                    row.info?.propertiesInfo.TryGetValue(kv.Key, out pi);
                    row[kv.Key] = ParamCoercion.Coerce(pi, kv.Value);
                    if (row.disabled != null) row.disabled[kv.Key] = false;
                }
                catch { }
            }

            LevelStorage.Attach(row, data);
            LevelStorage.SyncDecorations(level);

            // The image changed after the object was built, so the engine has to rebuild it.
            // Without this the marker takes effect only on the next level load, and a fresh
            // placement shows a completely blank decoration.
            Refresh(editor, level);

            Plugin.Log($"placed '{manifest.Id}' at floor {row.floor} uid={data.Uid}");
        }

        /// <summary>
        /// The row AddDecoration just created, found by identity rather than position.
        ///
        /// The game inserts at an index derived from the current selection, so the new row is
        /// not necessarily last and "count went up" does not say which row is new. Everything
        /// present before the call is remembered, and the one object that was not is ours.
        /// Picking wrong would hand our marker to a native decoration, which would then draw
        /// a field the author never asked for.
        /// </summary>
        private static LevelEvent FindAdded(List<LevelEvent> rows, HashSet<LevelEvent> existing)
        {
            if (rows == null) return null;
            foreach (var row in rows)
            {
                if (row == null) continue;
                if (!existing.Contains(row)) return row;
            }
            return null;
        }

        private static void Refresh(scnEditor editor, LevelData level)
        {
            // Rebuild the list, not one object: a row that just gained its marker did not
            // have a decoration object before, and UpdateDecorationObject(LevelEvent) can
            // only refresh one that already exists.
            try { editor.UpdateDecorationObjects(); }
            catch (Exception e) { Plugin.Log("decoration refresh failed: " + e.Message); }

            try { LevelStorage.SyncDecorations(level); }
            catch { }
        }

        /// <summary>
        /// Gives every decoration-backed row a uid no other row claims.
        ///
        /// LevelData.Copy() — which undo uses — duplicates rows by value, and our uid lives
        /// inside the row's payload, so a copy carries the original's identity. Two rows with
        /// one uid would resolve to one data object after the next decode, and editing one
        /// would silently rewrite the other.
        /// </summary>
        private static void EnsureUniqueUids()
        {
            var level = ADOBase.editor != null ? ADOBase.editor.levelData : null;
            if (level == null) return;

            var rows = level.decorations;
            if (rows == null) return;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                if (row == null) continue;
                if (!LevelStorage.TryGetData(row, out var data)) continue;

                if (string.IsNullOrEmpty(data.Uid) || !seen.Add(data.Uid))
                {
                    data.Uid = StableId.NewUid();
                    seen.Add(data.Uid);
                    LevelStorage.Attach(row, data);
                }
            }

            LevelStorage.SyncDecorations(level);
        }
    }
}
