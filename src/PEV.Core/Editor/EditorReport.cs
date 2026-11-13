using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ADOFAI;
using PEV.Registry;
using PEV.Storage;
using UnityEngine;

namespace PEV.Editor
{
    /// <summary>
    /// Surfaces problems to the person editing the chart.
    ///
    /// Everything that can go wrong here is silent by nature — a missing package, a
    /// malformed payload, a script that throws — and a chart author staring at an editor
    /// gets nothing from a log file they do not know exists. Problems are collected and
    /// shown once per level load rather than spamming a notification per event.
    /// </summary>
    public static class EditorReport
    {
        private sealed class Problem
        {
            public string Kind;
            public string Detail;
        }

        private static readonly List<Problem> Collected = new List<Problem>();
        private static bool _shown;

        public static void Reset()
        {
            Collected.Clear();
            _shown = false;
        }

        public static void Add(string kind, string detail)
        {
            Collected.Add(new Problem { Kind = kind, Detail = detail });
        }

        public static int Count => Collected.Count;

        public static void AddFrom(DecodeSummary summary)
        {
            if (summary == null) return;
            if (summary.MissingDefinition > 0)
                Add("MissingEvent", $"{summary.MissingDefinition} event(s) reference a package that is not installed");
            if (summary.Malformed > 0)
                Add("Malformed", $"{summary.Malformed} comment(s) could not be parsed and were left untouched");
            if (summary.DefinitionMismatch > 0)
                Add("VersionMismatch", $"{summary.DefinitionMismatch} event(s) were written by a newer version of their package");
        }

        /// <summary>
        /// Shows one notification summarizing the load. Called from the editor once it is
        /// ready, because ShowNotification needs a live scnEditor.
        /// </summary>
        public static void ShowIfNeeded()
        {
            if (_shown || Collected.Count == 0) return;
            if (ADOBase.editor == null) return;

            _shown = true;
            try
            {
                string text = BuildSummary();
                ADOBase.editor.ShowNotification(text, new Color(1f, 0.75f, 0.4f), 4f);
            }
            catch { /* a failed notification must never break loading */ }
        }

        private static string BuildSummary()
        {
            var sb = new StringBuilder("Programmable Events: ");
            var kinds = Collected.GroupBy(p => p.Kind)
                .Select(g => $"{g.Key} x{g.Count()}")
                .ToList();
            sb.Append(string.Join(", ", kinds));
            return sb.ToString();
        }

        /// <summary>Full detail lines, for the mod settings panel and for logging.</summary>
        public static IEnumerable<string> Details()
        {
            return Collected.Select(p => $"[{p.Kind}] {p.Detail}");
        }
    }
}
