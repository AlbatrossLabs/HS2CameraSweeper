using System;
using System.IO;
using System.IO.Compression;

namespace HS2CameraSweep.Capture
{
    /// <summary>
    /// PNG writer that runs off the main thread (Unity's EncodeToPNG can't), so a sweep doesn't stall
    /// while a 4K image compresses. 8-bit RGB or RGBA, per-row adaptive filtering (the usual
    /// "smallest sum of differences" choice), zlib through System.IO.Compression.
    /// </summary>
    internal static class PngEncoder
    {
        private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
        private static readonly uint[] CrcTable = BuildCrcTable();

        /// <param name="pixels">Top row first, tightly packed, 3 or 4 bytes per pixel.</param>
        public static void Write(string path, byte[] pixels, int width, int height, int channels)
        {
            var stride = width * channels;

            var ihdr = new byte[13];
            PutBE(ihdr, 0, (uint)width);
            PutBE(ihdr, 4, (uint)height);
            ihdr[8] = 8;                                // bit depth
            ihdr[9] = (byte)(channels == 4 ? 6 : 2);    // RGBA / RGB
            // compression, filter, interlace = 0

            var idat = new MemoryStream(stride * height / 2 + 1024);
            idat.WriteByte(0x78); // zlib header: deflate, 32K window
            idat.WriteByte(0x9C); // default compression
            uint adler = 1;

            using (var deflate = OpenDeflate(idat))
            {
                var prev = new byte[stride];
                var line = new byte[stride + 1];
                var best = new byte[stride + 1];
                var trial = new byte[stride + 1];

                for (var y = 0; y < height; y++)
                {
                    var row = y * stride;
                    long bestScore = long.MaxValue;
                    for (byte filter = 0; filter <= 4; filter++)
                    {
                        var score = Filter(filter, pixels, row, prev, channels, stride, trial);
                        if (score >= bestScore) continue;
                        bestScore = score;
                        var swap = best; best = trial; trial = swap;
                    }

                    deflate.Write(best, 0, stride + 1);
                    adler = Adler32(adler, best, stride + 1);
                    Buffer.BlockCopy(pixels, row, prev, 0, stride);
                }
            }

            var a = new byte[4];
            PutBE(a, 0, adler);
            idat.Write(a, 0, 4);

            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                fs.Write(Signature, 0, Signature.Length);
                WriteChunk(fs, "IHDR", ihdr, 0, ihdr.Length);
                WriteChunk(fs, "IDAT", idat.GetBuffer(), 0, (int)idat.Length);
                WriteChunk(fs, "IEND", new byte[0], 0, 0);
            }
        }

        private static Stream OpenDeflate(Stream target)
        {
            try
            {
                return new DeflateStream(target, CompressionLevel.Optimal, true);
            }
            catch (Exception)
            {
                // Older runtimes without compression levels.
                return new DeflateStream(target, CompressionMode.Compress, true);
            }
        }

        /// <summary>Filters one row into dst (dst[0] = filter type). Returns the sum of |signed bytes| for choosing.</summary>
        private static long Filter(byte type, byte[] px, int row, byte[] prev, int bpp, int stride, byte[] dst)
        {
            dst[0] = type;
            long sum = 0;
            for (var i = 0; i < stride; i++)
            {
                int x = px[row + i];
                int a = i >= bpp ? px[row + i - bpp] : 0;
                int b = prev[i];
                int v;
                switch (type)
                {
                    case 0: v = x; break;
                    case 1: v = x - a; break;
                    case 2: v = x - b; break;
                    case 3: v = x - ((a + b) >> 1); break;
                    default:
                    {
                        int c = i >= bpp ? prev[i - bpp] : 0;
                        int p = a + b - c;
                        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                        v = x - (pa <= pb && pa <= pc ? a : pb <= pc ? b : c);
                        break;
                    }
                }

                var f = (byte)v;
                dst[i + 1] = f;
                sum += f < 128 ? f : 256 - f;
            }

            return sum;
        }

        private static uint Adler32(uint adler, byte[] data, int count)
        {
            uint s1 = adler & 0xFFFF, s2 = adler >> 16;
            var i = 0;
            while (i < count)
            {
                var n = Math.Min(count - i, 5552);
                for (var end = i + n; i < end; i++)
                {
                    s1 += data[i];
                    s2 += s1;
                }
                s1 %= 65521;
                s2 %= 65521;
            }
            return (s2 << 16) | s1;
        }

        private static void WriteChunk(Stream s, string type, byte[] data, int offset, int length)
        {
            var head = new byte[8];
            PutBE(head, 0, (uint)length);
            for (var i = 0; i < 4; i++) head[4 + i] = (byte)type[i];
            s.Write(head, 0, 8);
            if (length > 0) s.Write(data, offset, length);

            var crc = UpdateCrc(0xFFFFFFFFu, head, 4, 4);
            crc = UpdateCrc(crc, data, offset, length) ^ 0xFFFFFFFFu;
            var tail = new byte[4];
            PutBE(tail, 0, crc);
            s.Write(tail, 0, 4);
        }

        private static uint UpdateCrc(uint crc, byte[] data, int offset, int length)
        {
            for (var i = offset; i < offset + length; i++)
                crc = CrcTable[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
            return crc;
        }

        private static uint[] BuildCrcTable()
        {
            var t = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                var c = n;
                for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                t[n] = c;
            }
            return t;
        }

        private static void PutBE(byte[] b, int at, uint v)
        {
            b[at] = (byte)(v >> 24);
            b[at + 1] = (byte)(v >> 16);
            b[at + 2] = (byte)(v >> 8);
            b[at + 3] = (byte)v;
        }
    }
}
