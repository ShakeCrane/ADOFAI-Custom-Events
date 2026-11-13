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

        private static void AddFromPath(string path, LoadReport report)
        {
            try
            {
                string json = Directory.Exists(path) ? ReadFromDirectory(path) : ReadFromZip(path);
                if (json == null)
                {
                    report.Errors.Add($"{Path.GetFileName(path)}: no {ManifestName} found");
                    return;
                }
                var manifest = EventManifest.Parse(json, path, out string error);
                if (manifest == null)
                {
                    report.Errors.Add($"{Path.GetFileName(path)}: {error}");
                    return;
                }
                report.Loaded.Add(manifest);
            }
            catch (Exception e)
            {
                report.Errors.Add($"{Path.GetFileName(path)}: {e.Message}");
            }
        }

        private static string ReadFromDirectory(string dir)
        {
            string p = Path.Combine(dir, ManifestName);
            return File.Exists(p) ? File.ReadAllText(p, Encoding.UTF8) : null;
        }

        private static string ReadFromZip(string zipPath)
        {
            using (var archive = ZipFile.OpenRead(zipPath))
            {
                var entry = archive.Entries.FirstOrDefault(e =>
                    e.FullName.Equals(ManifestName, StringComparison.OrdinalIgnoreCase));
                if (entry == null) return null;
                using (var reader = new StreamReader(entry.Open(), Encoding.UTF8))
                    return reader.ReadToEnd();
            }
        }

        /// <summary>Reads a side file (script, icon) out of a package.</summary>
        public static byte[] ReadResource(string packagePath, string relative)
        {
            if (string.IsNullOrEmpty(relative)) return null;
            if (Directory.Exists(packagePath))
            {
                string p = Path.Combine(packagePath, relative);
                return File.Exists(p) ? File.ReadAllBytes(p) : null;
            }
            using (var archive = ZipFile.OpenRead(packagePath))
            {
                var entry = archive.Entries.FirstOrDefault(e =>
                    e.FullName.Equals(relative, StringComparison.OrdinalIgnoreCase));
                if (entry == null) return null;
                using (var stream = entry.Open())
                using (var ms = new MemoryStream())
                {
                    stream.CopyTo(ms);
                    return ms.ToArray();
                }
            }
        }
    }
}
