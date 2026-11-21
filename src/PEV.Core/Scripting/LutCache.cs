using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using PEV.Loading;
using UnityEngine;

namespace PEV.Scripting
{
    /// <summary>
    /// Loads Adobe/Resolve `.cube` LUT files into GPU textures and keeps them alive for as
    /// long as a grading layer references them.
    ///
    /// Three details decide whether a LUT renders correctly:
    ///
    ///   • orientation. `.cube` stores red fastest, then green, then blue. Unity's
    ///     Texture3D.SetPixels32 wants x fastest too, so the file order maps onto
    ///     (x, y, z) = (red, green, blue) without a transpose. Getting this wrong swaps
    ///     the axes and looks plausible but is wrong.
    ///
    ///   • sampling. Trilinear filtering on the raw 0..1 coordinate samples the edge
    ///     texels' centres incorrectly at the extremes, so the shader is given the grid
    ///     size and applies the half-texel inset. Stretching by (n-1)/n alone is the
    ///     classic bug and shows as the darkest and brightest values never being reached.
    ///
    ///   • lifetime. A LUT is a texture the event does not own; two events using the same
    ///     file share one upload, and the last release destroys it. Re-parsing a 33³ LUT
    ///     every time a layer is rebuilt is a visible hitch.
    /// </summary>
    internal static class LutCache
    {
        internal sealed class Entry
        {
            internal string Key;
            internal Texture3D Volume;
            internal Texture2D Strip;
            internal int Size;
            internal int References;
            internal Vector4 DomainMin = Vector4.zero;
            internal Vector4 DomainMax = Vector4.one;
            internal bool IsThreeDimensional;
            internal long Bytes;
        }

        private const int MaxTextBytes = 16 * 1024 * 1024;
        private const int MinThreeDimensionalSize = 2;
        private const int MaxThreeDimensionalSize = 64;
        private const int MinOneDimensionalSize = 2;
        private const int MaxOneDimensionalSize = 4096;
        private const long MaxTotalBytes = 96L * 1024 * 1024;

        private static readonly Dictionary<string, Entry> Entries =
            new Dictionary<string, Entry>(StringComparer.Ordinal);

        /// <summary>
        /// Resolves a LUT path for a package. A package may ship LUTs, and an author may
        /// also drop a `.cube` beside the chart and reference its relative path.
        /// </summary>
        internal static string Resolve(string path, string packagePath, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(path)) { error = "no LUT path"; return null; }
            string candidate = path.Trim().Replace('\\', '/');

            if (EventLoader.IsPackageRelativePath(candidate) && !string.IsNullOrEmpty(packagePath) &&
                EventLoader.HasResource(packagePath, candidate))
                return packagePath + "|" + candidate;

            string beside = ChartPackages.ResolveChartAsset(candidate);
            if (beside != null) return beside;

            error = $"LUT '{path}' was not found in the package or next to the chart";
            return null;
        }

        internal static Entry Acquire(string resolvedKey, string sourcePackage, string relative, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(resolvedKey)) { error = "unresolved LUT"; return null; }

            string stamp = Stamp(resolvedKey);
            string cacheKey = resolvedKey + "|" + stamp;
            if (Entries.TryGetValue(cacheKey, out var cached))
            {
                cached.References++;
                return cached;
            }

            ValidateBudget(out error);
            if (error != null) return null;

            byte[] bytes = ReadBytes(resolvedKey, sourcePackage, relative, out error);
            if (bytes == null) return null;

            var entry = Parse(bytes, out error);
            if (entry == null) return null;

            entry.Key = cacheKey;
            entry.References = 1;
            Entries[cacheKey] = entry;
            return entry;
        }

        internal static void Release(string cacheKey)
        {
            if (string.IsNullOrEmpty(cacheKey)) return;
            if (!Entries.TryGetValue(cacheKey, out var entry)) return;
            entry.References--;
            if (entry.References > 0) return;
            Entries.Remove(cacheKey);
            Destroy(entry);
        }

        internal static void Clear()
        {
            var entries = new List<Entry>(Entries.Values);
            Entries.Clear();
            foreach (var entry in entries) Destroy(entry);
        }

        internal static string Report()
        {
            long bytes = 0;
            int volumes = 0, strips = 0;
            foreach (var entry in Entries.Values)
            {
                bytes += entry.Bytes;
                if (entry.Volume != null) volumes++;
                else strips++;
            }
            return $"loaded={Entries.Count} 3d={volumes} 1d={strips} bytes={bytes}";
        }

        private static void Destroy(Entry entry)
        {
            if (entry == null) return;
            if (entry.Volume != null) UnityEngine.Object.Destroy(entry.Volume);
            if (entry.Strip != null) UnityEngine.Object.Destroy(entry.Strip);
            entry.Volume = null;
            entry.Strip = null;
        }

        private static void ValidateBudget(out string error)
        {
            long bytes = 0;
            foreach (var entry in Entries.Values) bytes += entry.Bytes;
            error = bytes > MaxTotalBytes
                ? $"LUT memory budget exceeded ({bytes} bytes); too many LUTs are loaded at once"
                : null;
        }

        private static string Stamp(string resolvedKey)
        {
            try
            {
                int separator = resolvedKey.IndexOf('|');
                if (separator > 0)
                {
                    string package = resolvedKey.Substring(0, separator);
                    var info = new FileInfo(package);
                    return info.Exists ? info.Length + ":" + info.LastWriteTimeUtc.Ticks : "package";
                }
                var file = new FileInfo(resolvedKey);
                return file.Exists ? file.Length + ":" + file.LastWriteTimeUtc.Ticks : "missing";
            }
            catch { return "unknown"; }
        }

        private static byte[] ReadBytes(string resolvedKey, string sourcePackage, string relative, out string error)
        {
            error = null;
            try
            {
                int separator = resolvedKey.IndexOf('|');
                if (separator > 0)
                {
                    byte[] packaged = EventLoader.ReadResource(resolvedKey.Substring(0, separator),
                        resolvedKey.Substring(separator + 1), MaxTextBytes);
                    if (packaged == null) error = "LUT is missing or larger than 16 MB";
                    return packaged;
                }
                var info = new FileInfo(resolvedKey);
                if (!info.Exists) { error = "LUT file is missing"; return null; }
                if (info.Length > MaxTextBytes) { error = "LUT is larger than 16 MB"; return null; }
                return File.ReadAllBytes(resolvedKey);
            }
            catch (Exception e) { error = "LUT read failed: " + e.Message; return null; }
        }

        /// <summary>
        /// Parses the `.cube` text into a texture. Supports both `LUT_3D_SIZE` and
        /// `LUT_1D_SIZE`; Resolve exports 1D LUTs for exposure-like transforms and they are
        /// cheap enough to accept rather than reject.
        /// </summary>
        internal static Entry Parse(byte[] bytes, out string error)
        {
            error = null;
            string text;
            try { text = new UTF8Encoding(false, true).GetString(bytes); }
            catch (Exception e) { error = "LUT is not valid UTF-8: " + e.Message; return null; }

            int size3D = 0, size1D = 0;
            var min = new Vector3(0f, 0f, 0f);
            var max = new Vector3(1f, 1f, 1f);
            var values = new List<float>(1 << 18);
            var parts = new float[3];

            using (var reader = new StringReader(text))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    line = line.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    if (line.StartsWith("LUT_3D_SIZE", StringComparison.Ordinal))
                    {
                        if (!int.TryParse(line.Substring(11).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out size3D))
                        { error = "invalid LUT_3D_SIZE"; return null; }
                        continue;
                    }
                    if (line.StartsWith("LUT_1D_SIZE", StringComparison.Ordinal))
                    {
                        if (!int.TryParse(line.Substring(11).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out size1D))
                        { error = "invalid LUT_1D_SIZE"; return null; }
                        continue;
                    }
                    if (line.StartsWith("DOMAIN_MIN", StringComparison.Ordinal))
                    {
                        if (!Triple(line.Substring(10), out min)) { error = "invalid DOMAIN_MIN"; return null; }
                        continue;
                    }
                    if (line.StartsWith("DOMAIN_MAX", StringComparison.Ordinal))
                    {
                        if (!Triple(line.Substring(10), out max)) { error = "invalid DOMAIN_MAX"; return null; }
                        continue;
                    }
                    if (line.StartsWith("TITLE", StringComparison.Ordinal)) continue;
                    if (line[0] < '0' && line[0] != '-' && line[0] != '.' && line[0] != '+') continue;

                    var tokens = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (tokens.Length < 3) continue;
                    if (values.Count > (1 << 18)) { error = "LUT has too many entries"; return null; }
                    for (int i = 0; i < 3; i++)
                        if (!float.TryParse(tokens[i], NumberStyles.Float, CultureInfo.InvariantCulture, out parts[i]))
                        { error = "LUT data line is not numeric"; return null; }
                    values.Add(parts[0]);
                    values.Add(parts[1]);
                    values.Add(parts[2]);
                }
            }

            if (size3D > 0 && size1D > 0) { error = "a LUT cannot be both 1D and 3D"; return null; }
            if (size3D <= 0 && size1D <= 0) { error = "LUT declares neither LUT_3D_SIZE nor LUT_1D_SIZE"; return null; }

            if (size3D > 0)
            {
                if (size3D < MinThreeDimensionalSize || size3D > MaxThreeDimensionalSize)
                { error = $"LUT_3D_SIZE {size3D} is outside {MinThreeDimensionalSize}..{MaxThreeDimensionalSize}"; return null; }
                long expected = (long)size3D * size3D * size3D * 3;
                if (values.Count != expected)
                { error = $"LUT_3D_SIZE {size3D} needs {expected / 3} entries but the file has {values.Count / 3}"; return null; }

                var pixels = new Color32[size3D * size3D * size3D];
                for (int i = 0; i < pixels.Length; i++)
                {
                    int source = i * 3;
                    pixels[i] = new Color32(
                        ToByte(values[source]), ToByte(values[source + 1]), ToByte(values[source + 2]), 255);
                }
                // Texture3D indices are x-fastest, which matches the .cube red-fastest order.
                var volume = new Texture3D(size3D, size3D, size3D, TextureFormat.RGBA32, false)
                {
                    name = "PEV.Lut3D",
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                    anisoLevel = 0,
                    hideFlags = HideFlags.DontSave
                };
                volume.SetPixels32(pixels);
                volume.Apply(false, false);
                return new Entry
                {
                    Volume = volume, Size = size3D, IsThreeDimensional = true,
                    DomainMin = new Vector4(min.x, min.y, min.z, 0f),
                    DomainMax = new Vector4(max.x, max.y, max.z, 0f),
                    Bytes = (long)size3D * size3D * size3D * 4
                };
            }

            if (size1D < MinOneDimensionalSize || size1D > MaxOneDimensionalSize)
            { error = $"LUT_1D_SIZE {size1D} is outside {MinOneDimensionalSize}..{MaxOneDimensionalSize}"; return null; }
            if (values.Count != size1D * 3)
            { error = $"LUT_1D_SIZE {size1D} needs {size1D} entries but the file has {values.Count / 3}"; return null; }

            var row = new Color32[size1D];
            for (int i = 0; i < size1D; i++)
            {
                int source = i * 3;
                row[i] = new Color32(ToByte(values[source]), ToByte(values[source + 1]), ToByte(values[source + 2]), 255);
            }
            var strip = new Texture2D(size1D, 1, TextureFormat.RGBA32, false)
            {
                name = "PEV.Lut1D",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                anisoLevel = 0,
                hideFlags = HideFlags.DontSave
            };
            strip.SetPixels32(row);
            strip.Apply(false, false);
            return new Entry
            {
                Strip = strip, Size = size1D, IsThreeDimensional = false,
                DomainMin = new Vector4(min.x, min.y, min.z, 0f),
                DomainMax = new Vector4(max.x, max.y, max.z, 0f),
                Bytes = (long)size1D * 4
            };
        }

        private static bool Triple(string text, out Vector3 value)
        {
            value = Vector3.zero;
            var tokens = text.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 3) return false;
            if (!float.TryParse(tokens[0], NumberStyles.Float, CultureInfo.InvariantCulture, out value.x)) return false;
            if (!float.TryParse(tokens[1], NumberStyles.Float, CultureInfo.InvariantCulture, out value.y)) return false;
            if (!float.TryParse(tokens[2], NumberStyles.Float, CultureInfo.InvariantCulture, out value.z)) return false;
            return true;
        }

        private static byte ToByte(float value)
        {
            if (float.IsNaN(value)) return 0;
            return (byte)Mathf.Clamp(Mathf.RoundToInt(value * 255f), 0, 255);
        }
    }
}
