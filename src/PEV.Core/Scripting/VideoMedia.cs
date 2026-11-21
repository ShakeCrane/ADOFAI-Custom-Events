using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace PEV.Scripting
{
    /// <summary>
    /// Resolves, identifies and — where the platform backend cannot open a file — converts
    /// video media for the surface primitive.
    ///
    /// Why this exists as its own layer: which containers this game build can decode is not
    /// knowable from the file extension. Measured on ADOFAI r150 / Unity 6000.3.25f1 / Windows,
    /// the shipped backend opens MP4/MOV/M4V (H.264, HEVC, AV1, MPEG-4), AVI (H.264, MJPEG),
    /// WMV and WebM-with-VP8 (alpha plane preserved), and refuses MKV, FLV, MPEG-TS, OGG/Theora,
    /// ProRes and WebM-with-VP9 no matter what the stream inside is. A chart author's MKV that
    /// holds H.264 is therefore an unsupported *container*, not an unsupported *codec* — the
    /// distinction decides whether a lossless remux is enough or a real transcode is needed.
    ///
    /// The compatibility tier is deliberately external and optional: an FFmpeg binary, found
    /// next to the mod, next to the chart or on PATH, converts the file once into a cached MP4
    /// (or a WebM when the source carries alpha). Nothing is downloaded, nothing is bundled,
    /// and a machine without FFmpeg still plays every natively supported file. When conversion
    /// is impossible the caller gets a diagnosis naming the container, the codec and the reason,
    /// which is what the editor shows instead of a silent black rectangle.
    /// </summary>
    public static class VideoMedia
    {
        public const string CacheFolderName = "video-cache";

        /// <summary>Extensions the backend is known to open, used only to pre-empt conversion.</summary>
        private static readonly HashSet<string> NativeExtensions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".mp4", ".m4v", ".mov", ".avi", ".wmv", ".webm", ".asf"
            };

        /// <summary>Extensions that are known not to open, so conversion can start immediately.</summary>
        private static readonly HashSet<string> ConvertExtensions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".mkv", ".flv", ".ts", ".m2ts", ".ogv", ".ogm", ".mpg", ".mpeg", ".webm.failed"
            };

        private static string _ffmpegPath;
        private static readonly object ConversionLock = new object();
        private static readonly Dictionary<string, ConversionJob> Jobs =
            new Dictionary<string, ConversionJob>(StringComparer.Ordinal);

        /// <summary>
        /// Builds an argument string with every value quoted.
        ///
        /// .NET Framework has no ProcessStartInfo.ArgumentList, and a path with spaces — the
        /// default for "Documents\A Dance of Fire and Ice\..." — breaks an unquoted command
        /// line silently. Quoting unconditionally is correct because FFmpeg accepts a quoted
        /// argument anywhere and this code never builds an option that must stay bare.
        /// </summary>
        private static string Quote(params string[] parts)
        {
            var sb = new StringBuilder();
            foreach (var part in parts)
            {
                if (sb.Length > 0) sb.Append(' ');
                if (string.IsNullOrEmpty(part)) { sb.Append("\"\""); continue; }
                bool needsQuotes = part.IndexOf(' ') >= 0 || part.IndexOf('"') >= 0 ||
                                   part.IndexOf('&') >= 0 || part.IndexOf('\t') >= 0;
                if (!needsQuotes) { sb.Append(part); continue; }
                sb.Append('"');
                foreach (char c in part)
                {
                    if (c == '"') sb.Append('\\');
                    sb.Append(c);
                }
                sb.Append('"');
            }
            return sb.ToString();
        }

        private sealed class ConversionJob
        {
            public string Target;
            public bool Done;
            public bool Failed;
            public string Message;
        }

        // ------------------------------------------------------------------ paths

        /// <summary>
        /// Resolves an author-supplied path to a readable file.
        ///
        /// A path relative to the chart directory is the portable form and the one the editor
        /// writes; an absolute path still resolves so an author can test with a file elsewhere,
        /// but it is flagged because copying the level to another machine will not carry it.
        /// </summary>
        public static bool TryResolve(string raw, out string fullPath, out string problem, bool chartOnly = false)
        {
            fullPath = null;
            problem = null;
            if (string.IsNullOrWhiteSpace(raw)) { problem = "no video file named"; return false; }

            string path = raw.Trim().Trim('"');
            try
            {
                if (chartOnly)
                {
                    if (!PEV.Loading.EventLoader.IsPackageRelativePath(path))
                    { problem = "video file must be relative to the chart: " + path; return false; }
                    path = PEV.Loading.ChartPackages.ResolveChartAsset(path);
                    if (path == null) { problem = "video file not found in the chart: " + raw; return false; }
                }
                else if (!Path.IsPathRooted(path))
                {
                    string chartDir = ChartDirectory();
                    if (string.IsNullOrEmpty(chartDir)) { problem = "no chart directory to resolve '" + path + "'"; return false; }
                    path = Path.Combine(chartDir, path.Replace('/', Path.DirectorySeparatorChar));
                }
                path = Path.GetFullPath(path);

                if (!File.Exists(path)) { problem = "video file not found: " + path; return false; }

                var info = new FileInfo(path);
                if (info.Length < 64) { problem = "video file is empty or truncated: " + path; return false; }
            }
            catch (Exception e) { problem = "cannot resolve video path '" + raw + "': " + e.Message; return false; }

            fullPath = path;
            return true;
        }

        /// <summary>The directory of the chart currently loaded, or null outside a chart.</summary>
        public static string ChartDirectory()
        {
            try
            {
                string levelPath = ADOBase.levelPath;
                if (string.IsNullOrEmpty(levelPath)) return null;
                string dir = Path.GetDirectoryName(levelPath);
                return string.IsNullOrEmpty(dir) ? null : dir;
            }
            catch { return null; }
        }

        // ------------------------------------------------------------ container

        /// <summary>
        /// Identifies the container from its first bytes.
        ///
        /// Extension is not trustworthy — a ".mp4" can be an MKV renamed, and the backend
        /// rejects it as one — so the answer comes from the file's own header.
        /// </summary>
        public static string ContainerOf(string path)
        {
            try
            {
                using (var stream = File.OpenRead(path))
                {
                    // 192 bytes, not 64: MPEG-TS is identified by a second sync byte at 188,
                    // and a 64-byte read made that check read past the buffer and fail.
                    var head = new byte[192];
                    int read = stream.Read(head, 0, head.Length);
                    if (read < 12) return "unknown";

                    if (head[0] == 0x1A && head[1] == 0x45 && head[2] == 0xDF && head[3] == 0xA3)
                    {
                        // EBML carries both WebM and Matroska; the DocType string distinguishes.
                        string doc = ReadDocType(path);
                        if (doc != null && doc.IndexOf("webm", StringComparison.OrdinalIgnoreCase) >= 0) return "webm";
                        return "matroska";
                    }

                    if (head[0] == 'R' && head[1] == 'I' && head[2] == 'F' && head[3] == 'F' &&
                        head[8] == 'A' && head[9] == 'V' && head[10] == 'I') return "avi";
                    if (head[0] == 'F' && head[1] == 'L' && head[2] == 'V') return "flv";
                    if (head[0] == 'O' && head[1] == 'g' && head[2] == 'g' && head[3] == 'S') return "ogg";
                    if (head[4] == 'f' && head[5] == 't' && head[6] == 'y' && head[7] == 'p') return "isobmff";
                    if (head[0] == 0x30 && head[1] == 0x26 && head[2] == 0xB2 && head[3] == 0x75) return "asf";
                    if (head[0] == 0x47 && head[188] == 0x47) return "mpeg-ts";
                }
            }
            catch { }
            return "unknown";
        }

        private static string ReadDocType(string path)
        {
            try
            {
                using (var stream = File.OpenRead(path))
                {
                    var buffer = new byte[4096];
                    int read = stream.Read(buffer, 0, buffer.Length);
                    string ascii = Encoding.ASCII.GetString(buffer, 0, read);
                    int at = ascii.IndexOf("webm", StringComparison.OrdinalIgnoreCase);
                    if (at >= 0) return "webm";
                    if (ascii.IndexOf("matroska", StringComparison.OrdinalIgnoreCase) >= 0) return "matroska";
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// A short, human-readable statement of what a file is and whether it will open.
        ///
        /// This is the text the log and the editor report carry when a file will not play, so
        /// it names the container and, when it is one the backend refuses, says which of the
        /// two remedies applies rather than only that something failed.
        /// </summary>
        public static string Diagnose(string path)
        {
            if (string.IsNullOrEmpty(path)) return "no file";
            string container = ContainerOf(path);
            string ext = Path.GetExtension(path).ToLowerInvariant();

            switch (container)
            {
                case "isobmff":
                    return "isobmff (" + ext + ")";
                case "matroska":
                    // Matroska is the container this backend refuses while accepting the very
                    // same codecs inside MP4, so remuxing is lossless and quick.
                    return "matroska/mkv — this backend does not open Matroska; needs remux to MP4";
                case "webm":
                    return "webm — opens only when the stream is VP8; VP9 is not decoded";
                case "flv":
                    return "flv — this backend does not open Flash Video; needs remux to MP4";
                case "mpeg-ts":
                    return "mpeg-ts — this backend does not open transport streams; needs remux to MP4";
                case "ogg":
                    return "ogg/theora — this backend does not decode Theora; needs transcoding";
                case "avi":
                    return "avi";
                case "asf":
                    return "asf/wmv";
                default:
                    return container + " (" + ext + ")";
            }
        }

        /// <summary>True when the extension is one the backend is known to open.</summary>
        public static bool LooksNative(string path)
        {
            string ext = Path.GetExtension(path);
            return !string.IsNullOrEmpty(ext) && NativeExtensions.Contains(ext) &&
                   !ConvertExtensions.Contains(ext);
        }

        // ----------------------------------------------------------- conversion

        /// <summary>The directory converted files are cached in.</summary>
        public static string CacheDirectory()
        {
            try
            {
                string modDir = PEV.Core.Plugin.Mod != null ? PEV.Core.Plugin.Mod.Path : null;
                if (string.IsNullOrEmpty(modDir)) return null;
                string dir = Path.Combine(modDir, CacheFolderName);
                Directory.CreateDirectory(dir);
                return dir;
            }
            catch { return null; }
        }

        /// <summary>
        /// The cached conversion of <paramref name="path"/>, if one is already complete.
        ///
        /// The key is the path, size and write time, not a content hash: hashing a multi-hundred
        /// megabyte file on the main thread to decide a cache hit would cost more than the
        /// conversion it avoids, and a file edited in place changes both size and timestamp.
        /// </summary>
        public static string CachedConversion(string path)
        {
            string dir = CacheDirectory();
            if (string.IsNullOrEmpty(dir)) return null;
            try
            {
                string key = CacheKey(path);
                foreach (var ext in new[] { ".mp4", ".webm" })
                {
                    string candidate = Path.Combine(dir, key + ext);
                    if (File.Exists(candidate) && new FileInfo(candidate).Length > 1024) return candidate;
                }
            }
            catch { }
            return null;
        }

        /// <summary>Target path a new conversion of this file would produce.</summary>
        public static string ConversionTarget(string path, bool alpha)
        {
            string dir = CacheDirectory();
            if (string.IsNullOrEmpty(dir)) return null;
            return Path.Combine(dir, CacheKey(path) + (alpha ? ".webm" : ".mp4"));
        }

        private static string CacheKey(string path)
        {
            var info = new FileInfo(path);
            string raw = "2|" + path.ToLowerInvariant() + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks;
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
                var sb = new StringBuilder(32);
                for (int i = 0; i < 16; i++) sb.Append(hash[i].ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>
        /// Starts converting <paramref name="path"/> in the background, if this machine can.
        ///
        /// Returns false when no converter is available, which is a diagnosis the caller can
        /// show; a conversion that is merely slow returns true and reports through the job.
        /// </summary>
        public static bool StartConversion(string path, bool alpha, out string note)
        {
            note = null;
            string ffmpeg = FfmpegPath();
            if (string.IsNullOrEmpty(ffmpeg))
            {
                note = "no FFmpeg found — install ffmpeg.exe next to PEV.Core or on PATH";
                return false;
            }

            string target = ConversionTarget(path, alpha);
            if (string.IsNullOrEmpty(target)) { note = "no cache directory"; return false; }

            lock (ConversionLock)
            {
                if (Jobs.TryGetValue(path, out var running) && running.Target == target && !running.Done)
                {
                    note = "converting";
                    return true;
                }

                var job = new ConversionJob { Target = target };
                Jobs[path] = job;
                System.Threading.Tasks.Task.Run(() => ConvertFile(ffmpeg, path, job, alpha));
                note = "converting";
                return true;
            }
        }

        /// <summary>State of an in-flight conversion, for the surface to poll.</summary>
        public static string ConversionState(string path, out bool failed)
        {
            failed = false;
            lock (ConversionLock)
            {
                if (!Jobs.TryGetValue(path, out var job)) return null;
                if (!job.Done) return "running";
                failed = job.Failed;
                return job.Message;
            }
        }

        private static void ConvertFile(string ffmpeg, string source, ConversionJob job, bool alpha)
        {
            string target = job.Target;
            string temporary = target + "." + Guid.NewGuid().ToString("N") + Path.GetExtension(target);
            string message = null;
            try
            {
                var input = new[]
                {
                    "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
                    "-protocol_whitelist", "file",
                    "-format_whitelist", "mov,matroska,webm,avi,asf,flv,mpegts,ogg,mpeg,mpegvideo",
                    "-i", source, "-map", "0:v:0", "-map", "0:a:0?"
                };
                bool success = !alpha && RunConversion(ffmpeg, temporary, input,
                    new[] { "-c", "copy", "-movflags", "+faststart" }, 120000, out message);
                if (!success)
                {
                    var codecs = alpha
                        ? new[] { "-c:v", "libvpx", "-pix_fmt", "yuva420p", "-auto-alt-ref", "0",
                                  "-b:v", "8M", "-c:a", "libvorbis" }
                        : new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", "20",
                                  "-pix_fmt", "yuv420p", "-movflags", "+faststart", "-c:a", "aac" };
                    success = RunConversion(ffmpeg, temporary, input, codecs, 600000, out message);
                }
                if (success)
                {
                    try { File.Move(temporary, target); }
                    catch (IOException) when (File.Exists(target)) { }
                    message = null;
                }
            }
            catch (Exception e) { message = e.Message; }
            finally
            {
                TryDelete(temporary);
                lock (ConversionLock)
                {
                    job.Failed = message != null;
                    job.Message = message ?? "converted";
                    job.Done = true;
                }
                PEV.Core.Plugin.Log("video convert " + Path.GetFileName(source) + ": " +
                    (message ?? "ok -> " + Path.GetFileName(target)));
            }
        }

        private static bool RunConversion(string ffmpeg, string target, string[] input, string[] codecs,
            int timeout, out string error)
        {
            var arguments = new List<string>(input);
            arguments.AddRange(codecs);
            arguments.Add(target);
            var info = new ProcessStartInfo(ffmpeg)
            {
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                Arguments = Quote(arguments.ToArray())
            };

            using (var process = new Process { StartInfo = info })
            {
                var stderr = new StringBuilder();
                process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data != null && stderr.Length < 400) stderr.AppendLine(Limit(e.Data, 400));
                };
                process.Start();
                process.BeginErrorReadLine();
                if (!process.WaitForExit(timeout))
                {
                    process.Kill();
                    process.WaitForExit();
                    error = "FFmpeg timed out";
                    return false;
                }
                process.WaitForExit();
                if (process.ExitCode == 0 && File.Exists(target) && new FileInfo(target).Length > 1024)
                {
                    error = null;
                    return true;
                }

                error = stderr.Length > 0 ? Limit(stderr.ToString().Trim(), 400)
                    : "FFmpeg produced no usable video (exit " + process.ExitCode + ")";
                return false;
            }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private static string Limit(string text, int max) =>
            string.IsNullOrEmpty(text) ? "" : (text.Length <= max ? text : text.Substring(0, max));

        /// <summary>
        /// Locates an FFmpeg binary without requiring configuration.
        ///
        /// The executable comes from the user's mod installation or an absolute PATH entry.
        /// </summary>
        public static string FfmpegPath()
        {
            if (_ffmpegPath != null && File.Exists(_ffmpegPath)) return _ffmpegPath;

            var candidates = new List<string>();
            try
            {
                string modDir = PEV.Core.Plugin.Mod != null ? PEV.Core.Plugin.Mod.Path : null;
                if (!string.IsNullOrEmpty(modDir))
                {
                    candidates.Add(Path.Combine(modDir, "ffmpeg.exe"));
                    candidates.Add(Path.Combine(modDir, "ffmpeg", "ffmpeg.exe"));
                    candidates.Add(Path.Combine(modDir, "tools", "ffmpeg.exe"));
                }
            }
            catch { }

            foreach (var candidate in candidates)
            {
                try { if (File.Exists(candidate)) return _ffmpegPath = candidate; } catch { }
            }

            try
            {
                string path = Environment.GetEnvironmentVariable("PATH");
                if (!string.IsNullOrEmpty(path))
                {
                    foreach (var dir in path.Split(new[] { Path.PathSeparator }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        string root = dir.Trim().Trim('"');
                        if (!Path.IsPathRooted(root) || Path.GetPathRoot(root).Length < 3) continue;
                        string candidate = Path.Combine(root, "ffmpeg.exe");
                        if (File.Exists(candidate)) return _ffmpegPath = candidate;
                    }
                }
            }
            catch { }

            return _ffmpegPath = null;
        }

        /// <summary>Diagnostics: converter availability and cache location.</summary>
        public static string Report()
        {
            string ffmpeg = FfmpegPath();
            int jobs;
            lock (ConversionLock) jobs = Jobs.Count;
            return "ffmpeg=" + (ffmpeg ?? "none") + " cache=" + (CacheDirectory() ?? "none") + " jobs=" + jobs;
        }
    }
}
