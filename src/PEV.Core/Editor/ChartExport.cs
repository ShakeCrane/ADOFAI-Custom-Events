using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using PEV.Core;
using PEV.Loading;
using PEV.Storage;

namespace PEV.Editor
{
    /// <summary>Add chart-declared packages to the game's normal export and missing-file checks.</summary>
    [HarmonyPatch]
    internal static class ChartExport
    {
        private static System.Reflection.MethodBase TargetMethod() => AccessTools.Method(typeof(scnEditor), "GetExportLevelFiles",
            new[] { typeof(string), typeof(bool).MakeByRefType(), typeof(DLCManager[]).MakeByRefType() });
        private static void Prefix(out bool __state) { __state = ChartPackages.Exporting; ChartPackages.Exporting = true; }
        private static void Postfix(ref List<string> __result)
        {
            if (__result == null) return;
            try
            {
                string directory = scnGame.instance == null ? null : Path.GetDirectoryName(scnGame.instance.levelPath);
                if (string.IsNullOrEmpty(directory)) return;
                var known = new HashSet<string>(__result, StringComparer.OrdinalIgnoreCase);
                foreach (string level in __result.Where(path => path.EndsWith(".adofai", StringComparison.OrdinalIgnoreCase)).ToArray())
                {
                    if (!File.Exists(level)) continue;
                    var root = JObject.Parse(File.ReadAllText(level)).ToObject<Dictionary<string, object>>();
                    var state = ChartDependencies.Read(root);
                    foreach (var entry in state.Entries)
                    {
                        if (string.IsNullOrEmpty(entry.File)) continue;
                        string path = Path.Combine(directory, entry.File);
                        if (known.Add(path)) __result.Add(path);
                    }
                }
                // The field carrier is a virtual marker, not a missing image asset.
                string marker = Path.GetFullPath(Path.Combine(directory, DecorationCarrier.ImageMarker));
                if (!File.Exists(marker) && MarkerUsedOnlyByEvents(__result))
                    __result.RemoveAll(path => string.Equals(Path.GetFullPath(path), marker, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception e) { Plugin.Log("chart event export discovery failed: " + e.Message); }
        }
        private static bool MarkerUsedOnlyByEvents(IEnumerable<string> files)
        {
            bool found = false;
            foreach (string path in files.Where(p => p.EndsWith(".adofai", StringComparison.OrdinalIgnoreCase)))
            {
                if (!File.Exists(path)) continue;
                var root = JObject.Parse(File.ReadAllText(path));
                if (!(root["decorations"] is JArray rows)) continue;
                foreach (var row in rows.OfType<JObject>())
                {
                    if (!DecorationCarrier.IsOurs(row.Value<string>("decorationImage"))) continue;
                    found = true;
                    if (!DecorationCarrier.TryUnpack(row.Value<string>("maskingTarget"), out _)) return false;
                }
            }
            return found;
        }
        private static Exception Finalizer(Exception __exception, bool __state)
        { ChartPackages.Exporting = __state; return __exception; }
    }

    [HarmonyPatch(typeof(scnEditor), "UpdateImageLoadResult")]
    internal static class ChartCarrierImageNotice
    {
        private static bool Prefix(scnEditor __instance, string name)
        {
            if (!DecorationCarrier.IsOurs(name) || __instance?.levelData?.decorations == null) return true;
            bool seen = false;
            foreach (var row in __instance.levelData.decorations)
            {
                if (!DecorationCarrier.IsOurs(row.GetString("decorationImage"))) continue;
                seen = true;
                if (!DecorationCarrier.TryUnpack(row.GetString("maskingTarget"), out _)) return true;
            }
            return !seen;
        }
    }
}
