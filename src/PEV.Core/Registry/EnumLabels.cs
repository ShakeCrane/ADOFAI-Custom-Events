using System;
using System.Collections.Generic;
using System.Globalization;
using ADOFAI;
using PEV.Model;
using SA.GoogleDoc;
using UnityEngine;

namespace PEV.Registry
{
    /// <summary>
    /// Puts a manifest's option captions into the game's own localization table.
    ///
    /// A parameter row is localized by writing customLabel before the PropertyInfo is built,
    /// but the dropdown *entries* have no such channel. PropertiesPanel hands the control to
    /// EnumSetup, which turns localizeEnumStrings on for every real enum, and its items then
    /// ask RDString.GetEnumValue for their text — customLabels is only consulted when that
    /// flag is off, which never happens here. So the only way an option reads in the author's
    /// language is for the key GetEnumValue looks up to exist.
    ///
    /// That key is "enum." + enumTypeString + "." + value, with enumTypeString being the
    /// manifest's type literal minus the "Enum:" prefix — assembly qualification included,
    /// because that is exactly what the panel passes through. Keying on our own reverse-domain
    /// type name keeps the entry from colliding with any of the game's enums; the shared
    /// "enum.common.<value>" fallback is deliberately not written.
    ///
    /// The table is filled once, at RDString.Setup, and never rebuilt afterwards, so an entry
    /// added here survives a language switch for the rest of the session.
    /// </summary>
    public static class EnumLabels
    {
        /// <summary>Keys already written this session, so re-publishing does no work.</summary>
        private static readonly HashSet<string> Written = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// Writes every option caption this manifest declares. Safe to call repeatedly and safe
        /// to call before the game has touched its localization table.
        /// </summary>
        public static int Register(ParamSpec spec, Action<string> log = null)
        {
            if (spec == null || spec.OptionLabels == null || spec.OptionLabels.Count == 0) return 0;
            if (string.IsNullOrEmpty(spec.EffectiveType) || !spec.EffectiveType.StartsWith("Enum:", StringComparison.Ordinal))
                return 0;

            string enumType = spec.EffectiveType.Substring("Enum:".Length).Trim();
            if (enumType.Length == 0) return 0;

            // Setup is what populates the section dictionaries. GetFullSectionLocalization
            // treats a missing section as empty, but the lookup below it would throw on a null
            // table, so this has to run first rather than be assumed already done.
            try { RDString.Setup(); }
            catch (Exception e) { log?.Invoke($"enum labels for '{spec.Name}': RDString.Setup failed: {e.Message}"); }

            int written = 0;
            foreach (var option in spec.OptionLabels)
            {
                if (string.IsNullOrEmpty(option.Key)) continue;
                string key = "enum." + enumType + "." + option.Key;

                foreach (var entry in option.Value)
                {
                    if (string.IsNullOrEmpty(entry.Value)) continue;
                    if (!TryParseLanguage(entry.Key, out var lang)) continue;

                    string stamp = key + "|" + lang;
                    Written.Add(stamp);

                    try
                    {
                        var section = Localization.Client.GetFullSectionLocalization(lang);
                        if (section == null) { Written.Remove(stamp); continue; }
                        section[key] = entry.Value;
                        written++;
                    }
                    catch (Exception e)
                    {
                        Written.Remove(stamp);
                        log?.Invoke($"enum label '{key}' ({lang}) not set: {e.Message}");
                    }
                }
            }

            Verify(spec, enumType, log);
            return written;
        }

        /// <summary>
        /// Reads the captions back the way the dropdown does, so a key that landed in the wrong
        /// dictionary shows up as a wrong label in the harness rather than as an English
        /// dropdown next session.
        /// </summary>
        public static string ReadCaptions(string enumType, IEnumerable<string> values)
        {
            if (string.IsNullOrEmpty(enumType) || values == null) return "";
            var parts = new List<string>();
            foreach (var v in values)
            {
                string caption = RDString.GetEnumValue(enumType, v);
                parts.Add(v + "=" + (string.IsNullOrEmpty(caption) ? "<none>" : caption));
            }
            return string.Join(" ", parts);
        }

        private static void Verify(ParamSpec spec, string enumType, Action<string> log)
        {
            if (log == null) return;
            foreach (var option in spec.OptionLabels)
            {
                string expect = FirstFor(RDString.language, option.Value);
                if (expect == null) continue;
                string got = RDString.GetEnumValue(enumType, option.Key);
                if (got != expect)
                    log($"enum label mismatch: {option.Key} expected '{expect}' got '{got}'");
            }
        }

        /// <summary>Same preference order as ParamSpec.ResolveLabel, for the self-check only.</summary>
        private static string FirstFor(SystemLanguage lang, Dictionary<string, string> labels)
        {
            foreach (var entry in labels)
            {
                if (TryParseLanguage(entry.Key, out var candidate) && candidate == lang &&
                    !string.IsNullOrEmpty(entry.Value))
                    return entry.Value;
            }
            if (labels.TryGetValue("English", out var en) && !string.IsNullOrEmpty(en)) return en;
            return null;
        }

        private static bool TryParseLanguage(string name, out SystemLanguage lang)
        {
            lang = SystemLanguage.English;
            if (string.IsNullOrEmpty(name)) return false;
            // Enum.TryParse would accept a bare number as a value; a language is always a name.
            if (char.IsDigit(name[0])) return false;
            return System.Enum.TryParse(name, false, out lang)
                   && System.Enum.IsDefined(typeof(SystemLanguage), lang);
        }
    }
}
