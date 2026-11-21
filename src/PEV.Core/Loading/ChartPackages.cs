using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ADOFAI;
using PEV.Core;
using PEV.Editor;
using PEV.Model;
using PEV.Registry;
using PEV.Scripting;
using PEV.Storage;

namespace PEV.Loading
{
    /// <summary>One package set per loaded chart; no global package can satisfy a dependency.</summary>
    internal static class ChartPackages
    {
        internal sealed class LoadContext
        {
            internal LevelData Level;
            internal string Path;
            internal LoadContext PreviousContext;
            internal List<EventManifest> Previous;
            internal ChartDependencies.State PreviousState;
            internal readonly List<string> Problems = new List<string>();
            internal bool Prepared, Succeeded;
        }
        [ThreadStatic] private static LoadContext _current;
        [ThreadStatic] internal static bool Exporting;

        /// <summary>
        /// Directory the chart was opened from, or null when it has no usable path.
        ///
        /// Screen-space effects resolve side files (a LUT, for instance) the same way the
        /// game resolves audio: next to the chart. Without this the only portable option
        /// would be shipping every asset inside the event package.
        /// </summary>
        internal static string CurrentChartDirectory
        {
            get
            {
                try
                {
                    var context = _current;
                    string path = context?.Path ?? ADOBase.levelPath;
                    return string.IsNullOrEmpty(path) ? null : Path.GetDirectoryName(Path.GetFullPath(path));
                }
                catch { return null; }
            }
        }

        internal static string ResolveChartAsset(string relative, string directory = null)
        {
            if (!EventLoader.IsPackageRelativePath(relative)) return null;
            directory = directory ?? CurrentChartDirectory;
            if (string.IsNullOrEmpty(directory)) return null;

            try
            {
                string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string full = Path.GetFullPath(Path.Combine(root, relative));
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) return null;
                for (string path = full; path.Length >= root.Length; path = Path.GetDirectoryName(path))
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return null;
                return full;
            }
            catch (Exception e) when (e is ArgumentException || e is IOException || e is UnauthorizedAccessException ||
                                      e is NotSupportedException)
            {
                return null;
            }
        }

        internal static LoadContext Begin(LevelData level, string path)
        {
            var context = new LoadContext { Level = level, Path = path, PreviousContext = _current,
                Previous = EventRegistry.All.ToList(), PreviousState = ChartDependencies.Get(level) };
            _current = context; return context;
        }
        internal static void End(LoadContext context, bool success)
        {
            if (context == null) return;
            try
            {
                if (!success && context.Prepared)
                {
                    ChartDependencies.Set(context.Level, context.PreviousState);
                    Replace(context.Previous);
                }
            }
            catch (Exception e) { Plugin.Log("chart registry rollback failed: " + e); }
            finally { if (ReferenceEquals(_current, context)) _current = context.PreviousContext; }
        }
        internal static void Prepare(LevelData level, Dictionary<string, object> root)
        {
            if (Exporting || _current == null || !ReferenceEquals(_current.Level, level)) return;
            var state = ChartDependencies.Read(root);
            ChartDependencies.Set(level, state);
            if (_current.Prepared) return;
            _current.Prepared = true;
            string directory = null;
            try { if (!string.IsNullOrEmpty(_current.Path) && File.Exists(_current.Path)) directory = Path.GetDirectoryName(Path.GetFullPath(_current.Path)); }
            catch (Exception e) { _current.Problems.Add("谱面路径无效：" + e.Message); }
            var report = EventLoader.ScanChartDirectory(directory);
            foreach (var error in report.Errors) _current.Problems.Add("事件包无法加载：" + error);
            if (state.Error != null) _current.Problems.Add("事件依赖声明无效：" + state.Error);
            var declared = state.Entries.ToDictionary(entry => entry.Id, StringComparer.Ordinal);
            var selected = new List<EventManifest>();
            foreach (var group in report.Loaded.GroupBy(m => m.Id, StringComparer.Ordinal))
            {
                if (group.Key == PlaceholderEvent.Id)
                { _current.Problems.Add("事件 ID 为内部保留名称：" + group.Key); continue; }
                var candidates = group.ToList();
                if (declared.TryGetValue(group.Key, out var dependency) && !string.IsNullOrEmpty(dependency.File))
                    candidates = candidates.Where(m => string.Equals(Path.GetFileName(m.SourcePath), dependency.File, StringComparison.OrdinalIgnoreCase)).ToList();
                if (candidates.Count == 1) selected.Add(candidates[0]);
                else if (candidates.Count > 1) _current.Problems.Add("谱面目录中事件 ID 重复：" + group.Key);
            }
            foreach (var dependency in state.Entries)
            {
                var match = selected.FirstOrDefault(m => m.Id == dependency.Id);
                if (match == null)
                    _current.Problems.Add("缺少事件包：" + (dependency.File ?? dependency.Id) + "（" + dependency.Id + "）");
                else if (Major(dependency.Version) > Major(match.Version))
                    _current.Problems.Add("事件包版本过低：" + (dependency.File ?? dependency.Id) + "（需要 " + dependency.Version + "）");
            }
            foreach (var data in ChartDependencies.UsedEvents(root).GroupBy(item => item.Id, StringComparer.Ordinal).Select(group => group.First()))
                if (!declared.ContainsKey(data.Id) && !selected.Any(manifest => manifest.Id == data.Id))
                    _current.Problems.Add("缺少事件包：" + data.Id);
            if (selected.Count > 0 || state.Entries.Count > 0 || HasPayload(root))
                selected.Insert(0, PlaceholderEvent.Build());
            Replace(selected);
            Plugin.Log("chart packages: " + (directory ?? "<none>") + "; " + report.Summary() + "; registered=" + selected.Count);
            foreach (var problem in _current.Problems) Plugin.Log("  " + problem);
        }
        internal static void AddProblems(LevelData level)
        {
            if (_current == null || !ReferenceEquals(_current.Level, level)) return;
            foreach (string problem in _current.Problems) EditorReport.Add("ChartPackage", problem);
        }
        private static void Replace(IEnumerable<EventManifest> manifests)
        {
            scnEditor editor = null;
            try { if (ADOBase.isLevelEditor) editor = ADOBase.editor; } catch { }
            if (editor != null) ChartEventPalette.Clear(editor);
            RuntimeHost.Shutdown();
            EventScriptCache.Clear();
            DecorationParams.ClearChart(); EventRegistry.ClearChart();
            EventRegistry.RegisterAll(manifests, message => Plugin.Log("registry: " + message));
            EventRegistry.PublishToGame(Plugin.Log);
            if (editor != null)
            {
                try { ChartEventPalette.Publish(editor); }
                catch (Exception e) { Plugin.Log("chart event palette failed: " + e); }
            }
        }
        private static int Major(string value)
        {
            if (string.IsNullOrEmpty(value)) return 0;
            return int.TryParse(value.Split('.')[0], out int major) ? major : 0;
        }
        private static bool HasPayload(IDictionary<string, object> root)
        {
            if (root == null) return false;
            foreach (string key in new[] { "actions", "decorations" })
            {
                if (!root.TryGetValue(key, out var rows) || !(rows is IEnumerable sequence) || rows is string) continue;
                foreach (var item in sequence)
                {
                    if (!(item is IDictionary<string, object> row)) continue;
                    if (row.TryGetValue("comment", out var text) && EventInstanceData.LooksLikeOurs(text as string)) return true;
                    if (row.TryGetValue("maskingTarget", out var target) && target is string payload &&
                        payload.StartsWith(DecorationCarrier.PayloadPrefix, StringComparison.Ordinal)) return true;
                }
            }
            return false;
        }
    }
}
