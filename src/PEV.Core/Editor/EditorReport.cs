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
        private static ReportPump _pump;

        public static void Reset()
        {
            Collected.Clear();
            _shown = false;
            if (_pump != null) UnityEngine.Object.Destroy(_pump.gameObject);
            _pump = null;
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
            try
            {
                string text = BuildSummary();
                var notification = Notification.instance;
                notification.ShowNoSpace();
                notification.text.text = text;
                _shown = true;
            }
            catch
            {
                try { if (ADOBase.editor != null) { ADOBase.editor.ShowNotification(BuildSummary(), new Color(1f, .75f, .4f), 6f); _shown = true; } }
                catch { /* a failed notification must never break loading */ }
            }
        }

        internal static void Schedule()
        {
            if (_shown || Collected.Count == 0 || _pump != null) return;
            var go = new GameObject("PEV.ChartNotification");
            UnityEngine.Object.DontDestroyOnLoad(go);
            _pump = go.AddComponent<ReportPump>();
        }

        public sealed class ReportPump : MonoBehaviour
        {
            private float _deadline;
            private void Awake() { _deadline = Time.unscaledTime + .5f; }
            private void Update()
            {
                if (Time.unscaledTime < _deadline || (ADOBase.editor != null && ADOBase.editor.isLoading)) return;
                ShowIfNeeded();
                if (_shown || Time.unscaledTime > _deadline + 10f) { _pump = null; Destroy(gameObject); }
            }
        }

        private static string BuildSummary()
        {
            var chartProblems = Collected.Where(p => p.Kind == "ChartPackage").Select(p => p.Detail).Distinct().ToList();
            if (chartProblems.Count > 0)
            {
                string text = string.Join("\n", chartProblems.Take(4));
                return chartProblems.Count > 4 ? text + "\n还有 " + (chartProblems.Count - 4) + " 项" : text;
            }
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
