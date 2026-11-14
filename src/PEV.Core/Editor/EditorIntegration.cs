using System;
using ADOFAI;
using HarmonyLib;
using PEV.Registry;
using PEV.Storage;
using PEV.Core;

namespace PEV.Editor
{
    /// <summary>
    /// Keeps instance data in step with what the editor does to events.
    ///
    /// The editor creates, copies and deletes LevelEvent objects without knowing about
    /// our storage. Each of those actions needs a matching change on the data side:
    ///
    ///   place  -> attach fresh data with a new uid
    ///   copy   -> attach a clone with a *different* uid, or two instances would share one
    ///             comment and overwrite each other on save
    ///   delete -> detach, so the comment is not written back
    ///
    /// Events that already existed at load time are attached by LevelStorage.Restore and
    /// must not be touched here.
    /// </summary>
    public static class EditorIntegration
    {
        [HarmonyPatch]
        public static class AddEventPatch
        {
            public static System.Reflection.MethodBase TargetMethod()
            {
                return AccessTools.Method(typeof(scnEditor), "AddEvent",
                    new[] { typeof(int), typeof(LevelEventType) });
            }

            public static void Postfix(int floorID, LevelEventType eventType)
            {
                try
                {
                    var manifest = FindManifest(eventType);
                    if (manifest == null) return;

                    // The editor just appended the new event; it is the last one on this floor.
                    var data = new EventInstanceData
                    {
                        Id = manifest.Id,
                        Version = manifest.Version,
                        Uid = StableUid()
                    };
                    data.Materialize(manifest.Params);

                    var ev = FindNewest(floorID, eventType);
                    if (ev == null) return;

                    LevelStorage.Attach(ev, data);

                    // Seed the editor's value store so the inspector shows the defaults
                    // and Bake() has something concrete to read back.
                    //
                    // Values must be coerced to the type the declared PropertyType implies:
                    // a manifest default of 2 parses as an int, and LevelEvent.Get<float>
                    // would then fail its cast and silently report 0.
                    var info = ev.info;
                    foreach (var kv in data.Effective)
                    {
                        try
                        {
                            ADOFAI.PropertyInfo pi = null;
                            info?.propertiesInfo.TryGetValue(kv.Key, out pi);
                            ev[kv.Key] = ParamCoercion.Coerce(pi, kv.Value);
                        }
                        catch { }
                    }

                    // Without angleOffset in the event data the editor never draws a badge
                    // on the tile: ShowEventIndicators skips any event whose data lacks that
                    // key. Native events all carry one, so authors expect the same.
                    EnsureAngleOffset(ev);

                    // AddEvent has already drawn this tile's indicators by the time our
                    // postfix runs, so that pass saw an event without angleOffset and drew
                    // nothing. Redraw now that the data is complete.
                    RedrawIndicators(floorID);

                    Plugin.Log($"placed '{manifest.Id}' uid={data.Uid} at floor {floorID}");
                }
                catch (Exception e)
                {
                    Plugin.Log("AddEvent hook failed: " + e);
                }
            }
        }

        [HarmonyPatch]
        public static class CopyEventPatch
        {
            public static System.Reflection.MethodBase TargetMethod()
            {
                return AccessTools.Method(typeof(scnEditor), "CopyEvent",
                    new[] { typeof(LevelEvent), typeof(int) });
            }

            /// <summary>
            /// CopyShallow duplicates the LevelEvent but our data map is keyed by object
            /// reference, so the copy starts unattached. Give it its own uid: sharing one
            /// would make both instances write the same comment.
            /// </summary>
            public static void Postfix(LevelEvent eventToCopy, int floor, LevelEvent __result)
            {
                try
                {
                    if (__result == null) return;
                    if (!LevelStorage.TryGetData(eventToCopy, out var source)) return;

                    var clone = source.Clone(StableUid());
                    LevelStorage.Attach(__result, clone);
                    // The clone copies the source's data, angleOffset included, but guard
                    // anyway: a legacy event copied from an old chart may lack it.
                    EnsureAngleOffset(__result);
                    Plugin.Log($"copied '{clone.Id}' -> uid={clone.Uid} at floor {__result.floor}");
                }
                catch (Exception e)
                {
                    Plugin.Log("CopyEvent hook failed: " + e);
                }
            }
        }

        [HarmonyPatch]
        public static class RemoveEventPatch
        {
            public static System.Reflection.MethodBase TargetMethod()
            {
                return AccessTools.Method(typeof(scnEditor), "RemoveEvent",
                    new[] { typeof(LevelEvent), typeof(bool) });
            }

            public static void Prefix(LevelEvent evnt)
            {
                try
                {
                    // Dropping the attachment is what makes the delete stick: Bake only
                    // writes events that are still in levelEvents.
                    if (LevelStorage.TryGetData(evnt, out var data))
                    {
                        LevelStorage.Detach(evnt);
                        Plugin.Log($"removed '{data.Id}' uid={data.Uid}");
                    }
                }
                catch (Exception e)
                {
                    Plugin.Log("RemoveEvent hook failed: " + e);
                }
            }
        }

        private static Model.EventManifest FindManifest(LevelEventType type)
        {
            return EventRegistry.TryGetByType(type, out var m) ? m : null;
        }

        private static LevelEvent FindNewest(int floor, LevelEventType type)
        {
            var list = ADOBase.editor != null ? ADOBase.editor.events : null;
            if (list == null) return null;
            LevelEvent found = null;
            foreach (var ev in list)
                if (ev.floor == floor && ev.eventType == type) found = ev;
            return found;
        }

        private static string StableUid() => PEV.Util.StableId.NewUid();

        /// <summary>
        /// The editor's tile badge (EventIndicator) is only created for events whose data
        /// contains angleOffset; every native event carries one. Custom events do not, so
        /// without this they place and edit fine but show nothing on the tile.
        ///
        /// The key is not declared as a PEV param, so it never surfaces in the properties
        /// panel, but it still encodes into the file — where vanilla simply ignores it.
        /// </summary>
        /// <summary>
        /// Redraws one tile's event badges. The editor only keeps indicators alive for the
        /// selected floor, so any code that mutates an event must refresh it explicitly.
        /// </summary>
        internal static void RedrawIndicators(int floor)
        {
            try
            {
                var editor = ADOBase.editor;
                if (editor == null) return;
                if (editor.floors == null || floor < 0 || floor >= editor.floors.Count) return;
                editor.ShowEventIndicators(editor.floors[floor]);
            }
            catch (Exception e)
            {
                Plugin.Log("indicator refresh failed: " + e.Message);
            }
        }

        internal static void EnsureAngleOffset(LevelEvent ev)
        {
            if (ev == null) return;
            try
            {
                object existing = ev.Get<object>("angleOffset");
                if (existing == null) ev["angleOffset"] = 0f;
            }
            catch { }
        }
    }
}
