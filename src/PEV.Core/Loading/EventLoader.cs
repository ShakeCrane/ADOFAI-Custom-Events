using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using PEV.Model;

namespace PEV.Loading
{
    public class LoadReport
    {
        public readonly List<EventManifest> Loaded = new List<EventManifest>();
        public readonly List<string> Errors = new List<string>();
        public readonly List<string> Conflicts = new List<string>();

        public string Summary()
        {
            return $"loaded={Loaded.Count}, errors={Errors.Count}, conflicts={Conflicts.Count}";
        }
    }

    /// <summary>
    /// Discovers .event packages and turns each into an EventManifest.
    /// A package is either a ZIP with a .event extension or an unpacked directory.
    /// </summary>
    public static class EventLoader
    {
        public const string ManifestName = "event.json";
        internal const int MaxManifestBytes = 2 * 1024 * 1024;
        internal const int MaxScriptBytes = 4 * 1024 * 1024;
        internal const int MaxIconBytes = 8 * 1024 * 1024;

        public static LoadReport Scan(IEnumerable<string> roots)
        {
            var report = new LoadReport();
            foreach (string root in roots)
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
                try
                {
                    foreach (string file in Directory.GetFiles(root, "*.event", SearchOption.AllDirectories))
                        AddFromPath(file, report);
                    foreach (string dir in Directory.GetDirectories(root, "*.event", SearchOption.AllDirectories))
                        AddFromPath(dir, report);
                }
                catch (Exception e)
                {
                    report.Errors.Add($"scan '{root}' failed: {e.Message}");
                }
            }
            return report;
        }

        /// <summary>Load packed event assets next to a chart, never from other charts or global directories.</summary>
        public static LoadReport ScanChartDirectory(string root)
        {
            var report = new LoadReport();
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return report;
            try
            {
                foreach (string path in Directory.GetFiles(root, "*.event", SearchOption.TopDirectoryOnly)
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    { report.Errors.Add($"{Path.GetFileName(path)}: symbolic links are not chart assets"); continue; }
                    AddFromPath(path, report);
                }
            }
            catch (Exception e) { report.Errors.Add($"scan '{root}' failed: {e.Message}"); }
            return report;
        }

        private static void AddFromPath(string path, LoadReport report)
        {
            try
            {
                byte[] main = ReadResource(path, ManifestName, MaxManifestBytes);
                if (main == null)
                {
                    report.Errors.Add($"{Path.GetFileName(path)}: {ManifestName} missing or larger than 2 MiB");
                    return;
                }
                var manifest = EventManifest.Parse(DecodeManifest(main), path, out string error);
                if (manifest == null)
                {
                    report.Errors.Add($"{Path.GetFileName(path)}: {error}");
                    return;
                }
                if (ReadResource(path, manifest.Entry, MaxScriptBytes) == null)
                    throw new InvalidDataException("event entry missing or larger than 4 MiB: " + manifest.Entry);
                // A compound package is registered atomically: a broken included event must
                // not silently leave half of a feature installed. All entries and resources
                // use the same package root, for both ZIPs and directory packages.
                var definitions = new List<EventManifest> { manifest };
                var ids = new HashSet<string>(StringComparer.Ordinal) { manifest.Id };
                foreach (string includedPath in manifest.IncludedEvents)
                {
                    byte[] bytes = ReadResource(path, includedPath, MaxManifestBytes);
                    if (bytes == null) throw new InvalidDataException("included event missing or larger than 2 MiB: " + includedPath);
                    var included = EventManifest.Parse(DecodeManifest(bytes), path, out string includedError);
                    if (included == null) throw new InvalidDataException("invalid included event " + includedPath + ": " + includedError);
                    if (included.IncludedEvents.Count != 0) throw new InvalidDataException("nested includedEvents are not supported");
                    if (!ids.Add(included.Id)) throw new InvalidDataException("duplicate event id inside package: " + included.Id);
                    if (ReadResource(path, included.Entry, MaxScriptBytes) == null)
                        throw new InvalidDataException("included event entry missing or larger than 4 MiB: " + included.Entry);
                    definitions.Add(included);
                }
                report.Loaded.AddRange(definitions);
            }
            catch (Exception e)
            {
                report.Errors.Add($"{Path.GetFileName(path)}: {e.Message}");
            }
        }

        private static string DecodeManifest(byte[] bytes) =>
            new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');

        /// <summary>Reads a side file (script, icon) out of a package.</summary>
        public static bool IsPackageRelativePath(string relative)
        {
            if (string.IsNullOrWhiteSpace(relative)) return false;
            relative = relative.Replace('\\', '/');
            return !relative.StartsWith("/", StringComparison.Ordinal) && !relative.Contains(":") &&
                   !relative.Split('/').Any(p => p == "..");
        }

        public static byte[] ReadResource(string packagePath, string relative) =>
            ReadResource(packagePath, relative, int.MaxValue);

        /// <summary>
        /// Whether a package contains a file, without reading it.
        ///
        /// A size probe through ReadResource cannot answer this: the helper refuses anything
        /// larger than the budget it is given, so a legitimate 130 kB LUT looked absent.
        /// </summary>
        public static bool HasResource(string packagePath, string relative)
        {
            if (string.IsNullOrEmpty(packagePath) || !IsPackageRelativePath(relative)) return false;
            relative = relative.Replace('\\', '/');
            try
            {
                if (Directory.Exists(packagePath))
                    return File.Exists(Path.Combine(packagePath, relative));
                if (!File.Exists(packagePath)) return false;
                using (var archive = ZipFile.OpenRead(packagePath))
                    return archive.Entries.Any(e => e.FullName.Equals(relative, StringComparison.OrdinalIgnoreCase));
            }
            catch { return false; }
        }

        /// <summary>Read at most maxBytes, including while decompressing a ZIP entry.</summary>
        public static byte[] ReadResource(string packagePath, string relative, int maxBytes)
        {
            if (!IsPackageRelativePath(relative) || maxBytes < 0) return null;
            // Both package forms use the same relative, slash-separated namespace.
            relative = relative.Replace('\\', '/');
            if (Directory.Exists(packagePath))
            {
                string p = Path.Combine(packagePath, relative);
                if (!File.Exists(p) || new FileInfo(p).Length > maxBytes) return null;
                using (var stream = File.OpenRead(p)) return ReadBounded(stream, maxBytes);
            }
            using (var archive = ZipFile.OpenRead(packagePath))
            {
                var entry = archive.Entries.FirstOrDefault(e =>
                    e.FullName.Equals(relative, StringComparison.OrdinalIgnoreCase));
                if (entry == null || entry.Length > maxBytes) return null;
                using (var stream = entry.Open()) return ReadBounded(stream, maxBytes);
            }
        }

        private static byte[] ReadBounded(Stream stream, int maxBytes)
        {
            using (var output = new MemoryStream())
            {
                var buffer = new byte[81920];
                int total = 0, read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) != 0)
                {
                    if (read > maxBytes - total) return null;
                    output.Write(buffer, 0, read); total += read;
                }
                return output.ToArray();
            }
        }
    }
}
