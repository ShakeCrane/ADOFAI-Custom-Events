using System;
using System.IO;
using System.IO.Compression;
using UnityEngine;

namespace PEV.Scripting
{
    /// <summary>
    /// PNG and texture-coordinate utilities; pixel assets come from event packages.
    /// </summary>
    public static class NoiseTextures
    {
        // --- minimal PNG decoder ------------------------------------------------------
        // Supports what these assets are: 8-bit, non-interlaced, colour type 2 (RGB) or 6
        // (RGBA), no palette, no tRNS. Anything else returns null rather than rendering noise
        // that looks like a decoding bug.

        internal static Color32[] DecodePng(byte[] data, out int width, out int height)
        {
            width = height = 0;
            if (data == null || data.Length < 8 || data.Length > 16 * 1024 * 1024) return null;
            byte[] signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
            for (int i = 0; i < signature.Length; i++) if (data[i] != signature[i]) return null;

            int pos = 8; // skip signature
            int bitDepth = 0, colorType = -1, interlace = 0;
            bool header = false, ended = false;
            var idat = new MemoryStream();

            while (pos + 8 <= data.Length)
            {
                int length = ReadInt32BE(data, pos);
                string type = System.Text.Encoding.ASCII.GetString(data, pos + 4, 4);
                int body = pos + 8;
                if (length < 0 || length > data.Length - body - 4) return null;

                if (type == "IHDR")
                {
                    if (header || length != 13) return null;
                    header = true;
                    width = ReadInt32BE(data, body);
                    height = ReadInt32BE(data, body + 4);
                    bitDepth = data[body + 8];
                    colorType = data[body + 9];
                    interlace = data[body + 12];
                }
                else if (type == "IDAT")
                {
                    if (!header || idat.Length + length > 16 * 1024 * 1024) return null;
                    idat.Write(data, body, length);
                }
                else if (type == "IEND") { ended = true; break; }

                pos = body + length + 4; // skip CRC
            }

            if (!header || !ended || width <= 0 || height <= 0 || width > 4096 || height > 4096 || bitDepth != 8 || interlace != 0) return null;
            if (colorType != 2 && colorType != 6) return null;

            int channels = colorType == 6 ? 4 : 3;
            int stride = width * channels;
            byte[] raw;
            try
            {
                raw = Inflate(idat.ToArray(), checked((stride + 1) * height));
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
                if (filter > 4) return null;
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

        private static byte[] Inflate(byte[] zlib, int expected)
        {
            if (zlib == null || zlib.Length < 2) return null;
            using (var input = new MemoryStream(zlib, 2, zlib.Length - 2)) // skip zlib header
            using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
            {
                var output = new byte[expected];
                int offset = 0;
                while (offset < expected)
                {
                    int count = deflate.Read(output, offset, expected - offset);
                    if (count == 0) return null;
                    offset += count;
                }
                return deflate.ReadByte() == -1 ? output : null;
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
