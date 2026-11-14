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

                // Property rows look up editor.{eventName}.{propertyName} first, then
                // editor.{propertyName}. Both miss for us, and what the game renders when
                // both miss is an unresolved-key string, not a friendly name.
                int dot = token.IndexOf('.');
                if (dot >= 0)
                {
                    if (int.TryParse(token.Substring(0, dot), out int ownerId) &&
                        EventRegistry.TryGetByType((LevelEventType)ownerId, out var owner))
                    {
                        string prop = token.Substring(dot + 1);
                        string label = Humanize(prop);
                        if (string.IsNullOrEmpty(label)) return;
                        __result = label;
                        exists = true;
                    }
                    return;
                }

                if (!int.TryParse(token, out int typeId)) return;

                if (!EventRegistry.TryGetByType((LevelEventType)typeId, out var manifest)) return;

                string name = manifest.DisplayName;
                if (string.IsNullOrEmpty(name)) name = manifest.Id;
                if (string.IsNullOrEmpty(name)) return;

                __result = name;
                exists = true;
            }

            /// <summary>"noiseSampleRate" -> "Noise Sample Rate"; existing camel words survive.</summary>
            private static string Humanize(string raw)
            {
                if (string.IsNullOrEmpty(raw)) return null;
                var sb = new System.Text.StringBuilder(raw.Length + 8);
                for (int i = 0; i < raw.Length; i++)
                {
                    char c = raw[i];
                    if (i > 0 && char.IsUpper(c) && !char.IsUpper(raw[i - 1])) sb.Append(' ');
                    sb.Append(i == 0 ? char.ToUpperInvariant(c) : c);
                }
                return sb.ToString();
            }
        }
    }
}
