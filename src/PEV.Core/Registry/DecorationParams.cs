using System;
using System.Collections.Generic;
using System.Linq;
using ADOFAI;
using PEV.Model;
using PEV.Storage;
using PropertyInfo = ADOFAI.PropertyInfo;

namespace PEV.Registry
{
    /// <summary>
    /// Gives a decoration-backed event a place in the properties panel.
    ///
    /// A noise field is stored as a real AddDecoration row, so the inspector that opens when
    /// the author selects it is AddDecoration's — the editor looks the type up in
    /// GCS.levelEventsInfo and builds the panel from that metadata, regardless of which event
    /// object is selected. There is no per-row panel.
    ///
    /// So the only place extra parameters can live is AddDecoration's own propertiesInfo.
    /// They are added there with `showIf decorationImage == our marker`, which is what keeps
    /// them off every native decoration: PropertyInfo.CheckIfShown evaluates that condition
    /// per selected event.
    ///
    /// Parameters whose names already exist on AddDecoration are deliberately skipped.
    /// scale, rotation, color, opacity, depth, parallax and tag are all native properties,
    /// and reusing them is the point: MoveDecorations tweens them, the transform gizmo writes
    /// them, and the effect follows with no extra wiring.
    /// </summary>
    public static class DecorationParams
    {
        /// <summary>Native AddDecoration type id; the panel and the rows are keyed by it.</summary>
        public static readonly LevelEventType NativeType = LevelEventType.AddDecoration;

        private static readonly HashSet<string> Injected = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// Names whose row also carries a condition of its own, on top of the marker.
        ///
        /// Kept so callers can tell "hidden because this row is native" from "hidden because
        /// the author picked the other shape" — the harness asserts both, and only the first
        /// is a bug.
        /// </summary>
        private static readonly HashSet<string> Conditional = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Names this mod added to AddDecoration.</summary>
        public static IEnumerable<string> InjectedNames => Injected;

        public static bool IsInjected(string name) => name != null && Injected.Contains(name);

        public static bool IsConditional(string name) => name != null && Conditional.Contains(name);

        /// <summary>
        /// The native AddDecoration metadata, or null if the table is not up yet.
        ///
        /// The lookup cannot assume a key format. GCS.levelEventsInfo is keyed by whatever
        /// GCS.levelEventTypeString holds for that type: a name for the game's own events, and
        /// a numeric string for the ones we publish (which is why LevelEvent's constructor
        /// indirection exists at all). Asking for the numeric form directly missed every
        /// native entry and silently published nothing.
        /// </summary>
        public static bool TryGetNativeInfo(out LevelEventInfo info)
        {
            info = null;
            if (GCS.levelEventsInfo == null) return false;

            try
            {
                if (GCS.levelEventTypeString != null
                    && GCS.levelEventTypeString.TryGetValue(NativeType, out var key)
                    && !string.IsNullOrEmpty(key)
                    && GCS.levelEventsInfo.TryGetValue(key, out info)
                    && info != null)
                    return true;
            }
            catch { }

            // Fall back to the literal name, which is what the game uses for native events and
            // what survives if levelEventTypeString is not populated yet.
            return GCS.levelEventsInfo.TryGetValue(NativeType.ToString(), out info) && info != null;
        }

        /// <summary>
        /// Adds every decoration-backed manifest's parameters to the native metadata.
        ///
        /// Must run before the editor builds its panels — PropertiesPanel.Init walks
        /// propertiesInfo once, at construction, so a parameter added afterwards exists in the
        /// metadata but never gets a row.
        /// </summary>
        public static void Publish(Action<string> log = null)
        {
            if (!TryGetNativeInfo(out var info))
            {
                log?.Invoke("AddDecoration metadata missing; decoration params not published");
                return;
            }

            // Our rows join whichever tab the native panel already opens on. Creating tabs of
            // our own would add empty tabs to the panel of every plain decoration in the game.
            string homeGroup = null;
            if (info.useGroups && info.groups != null && info.groups.Count > 0)
            {
                // Group is a struct, so the default one is not null and cannot be tested
                // that way; pick the default tab explicitly and fall back to the first.
                foreach (var g in info.groups)
                {
                    if (!string.IsNullOrEmpty(g.name) && homeGroup == null) homeGroup = g.name;
                    if (g.isDefault && !string.IsNullOrEmpty(g.name)) { homeGroup = g.name; break; }
                }
            }

            int added = 0;
            foreach (var m in EventRegistry.All)
            {
                if (!m.DecorationBacked) continue;

                foreach (var spec in m.Params)
                {
                    if (string.IsNullOrEmpty(spec.Name)) continue;
                    try { ChoiceEnumFactory.Prepare(m, spec); }
                    catch (Exception e) { log?.Invoke($"choice '{spec.Name}' failed: {e.Message}"); continue; }

                    // Before the early-outs below: the caption table is per parameter type, not
                    // per row, so an enum whose panel row is native still needs its options.
                    try { EnumLabels.Register(spec, log); }
                    catch (Exception e) { log?.Invoke($"enum labels for '{spec.Name}' failed: {e.Message}"); }

                    // Native wins. Our manifest still declares the parameter so scripts and
                    // Materialize see it, but the UI row is the game's own.
                    if (info.propertiesInfo.ContainsKey(spec.Name)) continue;

                    try
                    {
                        var dict = spec.ToPropertyDict();
                        // Only for rows that carry our marker. Without this every native
                        // decoration in the editor would grow an empty set of rows.
                        //
                        // The manifest's own conditions are kept and prepended, not replaced:
                        // feather exists only for the circle shape, and overwriting showIf with
                        // the marker alone would make it a permanent row on every field.
                        // CheckIfShown ANDs the pairs, so both conditions must hold.
                        var conditions = new List<string> { "decorationImage", DecorationCarrier.ImageMarker };
                        if (spec.ShowIf != null && spec.ShowIf.Count > 0)
                        {
                            conditions.AddRange(spec.ShowIf);
                            Conditional.Add(spec.Name);
                        }
                        // List<object>, because RDEditorUtils.DecodeStringArray is an
                        // `is List<object>` cast: a string[] comes back as null and the
                        // PropertyInfo constructor dereferences it immediately.
                        var conditionList = new List<object>(conditions.Count);
                        foreach (var c in conditions) conditionList.Add(c);
                        dict["showIf"] = conditionList;
                        // The payload on maskingTarget is the only persisted copy. Writing
                        // these as their own keys would add keys to every chart that vanilla
                        // drops on its next save — a diff with no benefit — so they stay out of
                        // the file and are re-read from the payload on decode.
                        dict["encode"] = false;
                        // Always editable. A disable checkbox would let an author switch a
                        // parameter off, at which point the panel shows the default — a state
                        // with no meaning for a parameter that is not written to the file.
                        dict["canBeDisabled"] = false;
                        if (!string.IsNullOrEmpty(homeGroup)) dict["group"] = homeGroup;

                        info.propertiesInfo[spec.Name] = new PropertyInfo(dict, info);
                        Injected.Add(spec.Name);
                        added++;
                    }
                    catch (Exception e)
                    {
                        // The message alone was not enough to diagnose this: every parameter
                        // failed with a bare NullReferenceException and the stack was lost.
                        log?.Invoke($"decoration param '{spec.Name}' rejected: {e}");
                    }
                }
            }

            if (added > 0)
                log?.Invoke($"decoration params: {added} added to {NativeType}");
        }
    }
}
