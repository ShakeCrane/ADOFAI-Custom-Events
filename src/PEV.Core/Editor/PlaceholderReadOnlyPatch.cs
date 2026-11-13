using System;
using System.Collections.Generic;
using ADOFAI;
using HarmonyLib;
using PEV.Registry;
using PEV.Storage;

namespace PEV.Editor
{
    /// <summary>
    /// Makes placeholder instances read-only.
    ///
    /// A placeholder exists to tell the author which package is missing. Its fields are
    /// descriptive, not editable: if they were editable the author would "fix" a value that
    /// is never written back anyway, because Bake returns the original comment verbatim.
    /// Editing them would be a lie about what is stored.
    /// </summary>
    [HarmonyPatch]
    public static class PlaceholderReadOnlyPatch
    {
        public static System.Reflection.MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(PropertiesPanel), "SetProperties",
                new[] { typeof(LevelEvent), typeof(bool) });
        }

        public static void Postfix(PropertiesPanel __instance, LevelEvent levelEvent)
        {
            try
            {
                if (levelEvent == null) return;
                if (!LevelStorage.TryGetData(levelEvent, out var data)) return;
                if (!data.IsPlaceholder) return;

                foreach (var kv in __instance.properties)
                {
                    var control = kv.Value != null ? kv.Value.control : null;
                    if (control == null) continue;
                    try { control.SetEnabled(false); } catch { }
                }
            }
            catch (Exception e)
            {
                PEV.Core.Plugin.Log("placeholder read-only pass failed: " + e.Message);
            }
        }
    }
}
