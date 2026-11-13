using System;
using System.Collections.Generic;
using ADOFAI;
using HarmonyLib;
using PEV.Registry;

namespace PEV.Editor
{
    /// <summary>
    /// Supplies display names for custom event types.
    ///
    /// The editor asks RDString.Get($"editor.{type}") for the tooltip shown when a palette
    /// button is hovered. Our type names are numeric, so that resolves to "editor.100001",
    /// which does not exist in the game's string table and would be rendered verbatim.
    ///
    /// The game's table lives in a Rewired LocalizedStringProvider loaded from a TextAsset;
    /// writing into it is fragile and version dependent. Intercepting the lookup is the
    /// smaller, safer seam.
    /// </summary>
    public static class LocalizationPatch
    {
        private const string Prefix = "editor.";

        [HarmonyPatch(typeof(RDString), "GetWithCheck")]
        public static class GetWithCheckPatch
        {
            public static void Postfix(string key, ref bool exists, ref string __result)
            {
                if (exists || string.IsNullOrEmpty(key)) return;
                if (!key.StartsWith(Prefix, StringComparison.Ordinal)) return;

                string token = key.Substring(Prefix.Length);
                if (!int.TryParse(token, out int typeId)) return;

                if (!EventRegistry.TryGetByType((LevelEventType)typeId, out var manifest)) return;

                string label = manifest.DisplayName;
                if (string.IsNullOrEmpty(label)) label = manifest.Id;
                if (string.IsNullOrEmpty(label)) return;

                __result = label;
                exists = true;
            }
        }
    }
}
