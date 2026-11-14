using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using UnityEngine;

namespace PEV.Scripting
{
    /// <summary>
    /// Loads the noise maps the effect samples, verbatim from Phira-Pro's
    /// noise-skins/NoiseField/tex (GPL-3.0-or-later).
    ///
    /// These are not decorative assets: the shader's entire character — where the coast bites,
    /// how the grains scatter — comes from the exact pixel values in these three images. They
    /// are mirror-wrapped value noise, sparse points, and an RG flow field, none of which an
    /// analytic function reproduces closely enough. Every earlier attempt to synthesise a
    /// substitute looked like a different effect, because it was one.
    ///
    /// Decoded here rather than through UnityEngine.ImageConversionModule: that assembly is
    /// built against netstandard 2.1 and this project targets 2.0, so referencing it fails to
    /// compile. The images are plain non-interlaced 8-bit PNGs, which is a small enough subset
    /// to decode directly.
    /// </summary>
    public static class NoiseTextures
    {
        private static Texture2D _block, _point, _flow;

        public static Texture2D Block => _block != null ? _block : (_block = Load("BlockNoise1.png", true));
        public static Texture2D Point => _point != null ? _point : (_point = Load("PointNoise.png", false));
        public static Texture2D Flow => _flow != null ? _flow : (_flow = Load("FD_Noise.png", true));

        /// <summary>True when every map decoded. Diagnostics read this; the renderer does not gate on it.</summary>
        public static bool AllLoaded => Block != null && Point != null && Flow != null;

        public static void Clear()
        {
            foreach (var t in new[] { _block, _point, _flow })
                if (t != null) UnityEngine.Object.Destroy(t);
            _block = _point = _flow = null;
        }

        private static Texture2D Load(string name, bool mirror)
        {
            try
            {
                byte[] png = ReadResource(name);
                if (png == null)
                {
                    PEV.Core.Plugin.Log("noise map '" + name + "' is missing from the assembly");
                    return null;
                }

                int w, h;
                Color32[] pixels = DecodePng(png, out w, out h);
                if (pixels == null)
                {
                    PEV.Core.Plugin.Log("noise map '" + name + "' did not decode");
                    return null;
                }

                var tex = new Texture2D(w, h, TextureFormat.RGBA32, false)
                {
                    // The skin's own sampler states: mirror for the block and flow maps,
                    // repeat for the points. Getting these wrong changes where the coast
                    // wraps, which is visible at the quad edge.
                    wrapMode = mirror ? TextureWrapMode.Mirror : TextureWrapMode.Repeat,
                    filterMode = FilterMode.Point,
                    name = "PEV.Tex." + name
                };
                tex.SetPixels32(pixels);
                tex.Apply(false);
                return tex;
            }
            catch (Exception e)
            {
                PEV.Core.Plugin.Log("noise map '" + name + "' failed: " + e.Message);
                return null;
            }
        }

        private static byte[] ReadResource(string name)
        {
            var asm = Assembly.GetExecutingAssembly();
            string resource = null;
            foreach (var candidate in asm.GetManifestResourceNames())
            {
                if (candidate.EndsWith(".Textures." + name, StringComparison.Ordinal)
                    || candidate == name)
                {
                    resource = candidate;
                    break;
                }
            }
            if (resource == null) return null;

            using (var stream = asm.GetManifestResourceStream(resource))
            {
                if (stream == null) return null;
                using (var ms = new MemoryStream())
                {
                    stream.CopyTo(ms);
                    return ms.ToArray();
                }
            }
        }

        // --- minimal PNG decoder ------------------------------------------------------
        // Supports what these assets are: 8-bit, non-interlaced, colour type 2 (RGB) or 6
        // (RGBA), no palette, no tRNS. Anything else returns null rather than rendering noise
        // that looks like a decoding bug.

        private static Color32[] DecodePng(byte[] data, out int width, out int height)
        {
            width = height = 0;
            if (data == null || data.Length < 8) return null;

            int pos = 8; // skip signature
            int bitDepth = 0, colorType = -1, interlace = 0;
            var idat = new MemoryStream();

            while (pos + 8 <= data.Length)
            {
                int length = ReadInt32BE(data, pos);
                string type = System.Text.Encoding.ASCII.GetString(data, pos + 4, 4);
                int body = pos + 8;

                if (type == "IHDR")
                {
                    width = ReadInt32BE(data, body);
                    height = ReadInt32BE(data, body + 4);
                    bitDepth = data[body + 8];
                    colorType = data[body + 9];
                    interlace = data[body + 12];
                }
                else if (type == "IDAT")
                {
                    idat.Write(data, body, length);
                }
                else if (type == "IEND") break;

                pos = body + length + 4; // skip CRC
            }

            if (bitDepth != 8 || interlace != 0) return null;
            if (colorType != 2 && colorType != 6) return null;

            int channels = colorType == 6 ? 4 : 3;
            int stride = width * channels;
            byte[] raw;
            try
            {
                raw = Inflate(idat.ToArray());
            }
            catch { return null; }
            if (raw == null || raw.Length < (stride + 1) * height) return null;

            var pixels = new Color32[width * height];
            var prev = new byte[stride];
            var line = new byte[stride];

            int src = 0;
            for (int y = 0; y < height; y++)
            {
                int filter = raw[src++];
                Buffer.BlockCopy(raw, src, line, 0, stride);
                src += stride;

                Unfilter(filter, line, prev, channels);

                for (int x = 0; x < width; x++)
                {
                    int i = x * channels;
                    pixels[y * width + x] = new Color32(
                        line[i],
                        channels > 1 ? line[i + 1] : line[i],
                        channels > 2 ? line[i + 2] : line[i],
                        channels > 3 ? line[i + 3] : (byte)255);
                }
                Buffer.BlockCopy(line, 0, prev, 0, stride);
            }

            return pixels;
        }

        private static void Unfilter(int filter, byte[] line, byte[] prev, int bpp)
        {
            switch (filter)
            {
                case 0: break;
                case 1:
                    for (int i = bpp; i < line.Length; i++)
                        line[i] = (byte)(line[i] + line[i - bpp]);
                    break;
                case 2:
                    for (int i = 0; i < line.Length; i++)
                        line[i] = (byte)(line[i] + prev[i]);
                    break;
                case 3:
                    for (int i = 0; i < line.Length; i++)
                    {
                        int left = i >= bpp ? line[i - bpp] : 0;
                        line[i] = (byte)(line[i] + ((left + prev[i]) >> 1));
                    }
                    break;
                case 4:
                    for (int i = 0; i < line.Length; i++)
                    {
                        int a = i >= bpp ? line[i - bpp] : 0;
                        int b = prev[i];
                        int c = i >= bpp ? prev[i - bpp] : 0;
                        int p = a + b - c;
                        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                        int pr = (pa <= pb && pa <= pc) ? a : (pb <= pc ? b : c);
                        line[i] = (byte)(line[i] + pr);
                    }
                    break;
                default: break;
            }
        }

        private static byte[] Inflate(byte[] zlib)
        {
            if (zlib == null || zlib.Length < 2) return null;
            using (var input = new MemoryStream(zlib, 2, zlib.Length - 2)) // skip zlib header
            using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                deflate.CopyTo(output);
                return output.ToArray();
            }
        }

        private static int ReadInt32BE(byte[] d, int i)
        {
            return (d[i] << 24) | (d[i + 1] << 16) | (d[i + 2] << 8) | d[i + 3];
        }

        /// <summary>GL_REPEAT for normalised coordinates.</summary>
        public static float Repeat(float t) => t - Mathf.Floor(t);

        /// <summary>GL_MIRRORED_REPEAT for normalised coordinates.</summary>
        public static float Mirror(float t)
        {
            float f = t - Mathf.Floor(t);
            int period = Mathf.FloorToInt(Mathf.Abs(t)) % 2;
            if (t < 0f && f > 0f) period = (period + 1) % 2;
            return period == 0 ? f : 1f - f;
        }

        /// <summary>
        /// Minimal PNG writer for diagnostics.
        ///
        /// UnityEngine.ImageConversionModule cannot be referenced (netstandard 2.1 against this
        /// project's 2.0), so the texture dump encodes its own PNG. Uncompressed deflate blocks
        /// keep it a dozen lines and avoid needing a compressor at all.
        /// </summary>
        public static byte[] EncodePng(Color32[] pixels, int width, int height)
        {
            int stride = width * 4;
            var raw = new byte[(stride + 1) * height];
            int o = 0;
            for (int y = height - 1; y >= 0; y--)   // PNG rows run top-down; Unity's are bottom-up
            {
                raw[o++] = 0;                        // filter: none
                int row = y * width;
                for (int x = 0; x < width; x++)
                {
                    var c = pixels[row + x];
                    raw[o++] = c.r;
                    raw[o++] = c.g;
                    raw[o++] = c.b;
                    raw[o++] = c.a;
                }
            }

            using (var ms = new MemoryStream())
            {
                ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);

                var ihdr = new byte[13];
                WriteInt32BE(ihdr, 0, width);
                WriteInt32BE(ihdr, 4, height);
                ihdr[8] = 8;   // bit depth
                ihdr[9] = 6;   // colour type: RGBA
                ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
                WriteChunk(ms, "IHDR", ihdr);

                WriteChunk(ms, "IDAT", DeflateStored(raw));
                WriteChunk(ms, "IEND", new byte[0]);
                return ms.ToArray();
            }
        }

        private static void WriteInt32BE(byte[] d, int i, int v)
        {
            d[i] = (byte)(v >> 24); d[i + 1] = (byte)(v >> 16);
            d[i + 2] = (byte)(v >> 8); d[i + 3] = (byte)v;
        }

        private static void WriteChunk(Stream s, string type, byte[] body)
        {
            var head = new byte[4];
            WriteInt32BE(head, 0, body.Length);
            s.Write(head, 0, 4);

            var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
            s.Write(typeBytes, 0, 4);
            s.Write(body, 0, body.Length);

            // CRC over type + body.
            uint crc = Crc32(typeBytes, 0, 4, 0xFFFFFFFFu);
            crc = Crc32(body, 0, body.Length, crc) ^ 0xFFFFFFFFu;
            var tail = new byte[4];
            WriteInt32BE(tail, 0, unchecked((int)crc));
            s.Write(tail, 0, 4);
        }

        private static uint Crc32(byte[] data, int offset, int count, uint crc)
        {
            for (int i = 0; i < count; i++)
            {
                crc ^= data[offset + i];
                for (int k = 0; k < 8; k++)
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
            return crc;
        }

        /// <summary>zlib stream made of uncompressed deflate blocks.</summary>
        private static byte[] DeflateStored(byte[] data)
        {
            using (var ms = new MemoryStream())
            {
                ms.WriteByte(0x78);   // zlib header
                ms.WriteByte(0x01);

                int pos = 0;
                while (pos < data.Length)
                {
                    int len = Math.Min(65535, data.Length - pos);
                    bool last = pos + len >= data.Length;
                    ms.WriteByte((byte)(last ? 1 : 0));   // BFINAL, stored block
                    ms.WriteByte((byte)(len & 0xFF));
                    ms.WriteByte((byte)(len >> 8));
                    ms.WriteByte((byte)(~len & 0xFF));
                    ms.WriteByte((byte)((~len >> 8) & 0xFF));
                    ms.Write(data, pos, len);
                    pos += len;
                }

                uint s1 = 1, s2 = 0;
                foreach (byte b in data) { s1 = (s1 + b) % 65521; s2 = (s2 + s1) % 65521; }
                uint adler = (s2 << 16) | s1;
                ms.WriteByte((byte)(adler >> 24));
                ms.WriteByte((byte)(adler >> 16));
                ms.WriteByte((byte)(adler >> 8));
                ms.WriteByte((byte)adler);
                return ms.ToArray();
            }
        }
    }
}
