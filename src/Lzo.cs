using System;
using System.IO;

namespace ProcreateViewer
{
    /// <summary>
    /// LZO1X decompressor (the format Procreate uses for layer tiles).  Port of the classic
    /// lzo1x_decompress_safe state machine; bounds-checked against both buffers.
    /// </summary>
    public static unsafe class Lzo
    {
        public static int Decompress(byte[] src, byte[] dst)
        {
            if (src.Length < 3) throw new InvalidDataException("LZO input too short");
            fixed (byte* inBase = src)
            fixed (byte* outBase = dst)
            {
                byte* ip = inBase, ipEnd = inBase + src.Length;
                byte* op = outBase, opEnd = outBase + dst.Length;
                byte* mpos;
                uint t, next, state = 0;

                if (*ip > 17)
                {
                    t = (uint)(*ip++ - 17);
                    CopyLiterals(ref ip, ref op, t, ipEnd, opEnd);
                    state = t < 4 ? t : 4;
                }

                for (;;)
                {
                    if (ip >= ipEnd) throw new InvalidDataException("LZO input overrun");
                    t = *ip++;
                    if (t < 16)
                    {
                        if (state == 0)
                        {
                            if (t == 0)
                            {
                                while (*ip == 0) { t += 255; ip++; if (ip >= ipEnd) throw new InvalidDataException("LZO input overrun"); }
                                t += (uint)(15 + *ip++);
                            }
                            t += 3;
                            CopyLiterals(ref ip, ref op, t, ipEnd, opEnd);
                            state = 4;
                            continue;
                        }
                        else if (state != 4)
                        {
                            next = t & 3;
                            mpos = op - 1 - (t >> 2) - ((uint)*ip++ << 2);
                            if (mpos < outBase || op + 2 > opEnd) throw new InvalidDataException("LZO lookbehind overrun");
                            *op++ = mpos[0];
                            *op++ = mpos[1];
                            goto match_next;
                        }
                        else
                        {
                            next = t & 3;
                            mpos = op - (1 + 0x800) - (t >> 2) - ((uint)*ip++ << 2);
                            t = 3;
                        }
                    }
                    else if (t >= 64)
                    {
                        next = t & 3;
                        mpos = op - 1 - ((t >> 2) & 7) - ((uint)*ip++ << 3);
                        t = (t >> 5) + 1;
                    }
                    else if (t >= 32)
                    {
                        t = (t & 31) + 2;
                        if (t == 2)
                        {
                            while (*ip == 0) { t += 255; ip++; if (ip >= ipEnd) throw new InvalidDataException("LZO input overrun"); }
                            t += (uint)(31 + *ip++);
                        }
                        mpos = op - 1;
                        next = (uint)(ip[0] | (ip[1] << 8));
                        ip += 2;
                        mpos -= next >> 2;
                        next &= 3;
                    }
                    else
                    {
                        mpos = op - ((t & 8) << 11);
                        t = (t & 7) + 2;
                        if (t == 2)
                        {
                            while (*ip == 0) { t += 255; ip++; if (ip >= ipEnd) throw new InvalidDataException("LZO input overrun"); }
                            t += (uint)(7 + *ip++);
                        }
                        next = (uint)(ip[0] | (ip[1] << 8));
                        ip += 2;
                        mpos -= next >> 2;
                        next &= 3;
                        if (mpos == op) goto eof_found;
                        mpos -= 0x4000;
                    }

                    if (mpos < outBase || op + t > opEnd) throw new InvalidDataException("LZO match overrun");
                    {
                        byte* oe = op + t;
                        do { *op++ = *mpos++; } while (op < oe);
                    }

                match_next:
                    state = next;
                    CopyLiterals(ref ip, ref op, next, ipEnd, opEnd);
                }

            eof_found:
                return (int)(op - outBase);
            }
        }

        private static void CopyLiterals(ref byte* ip, ref byte* op, uint t, byte* ipEnd, byte* opEnd)
        {
            if (t == 0) return;
            if (ip + t > ipEnd || op + t > opEnd) throw new InvalidDataException("LZO literal overrun");
            byte* oe = op + t;
            while (op < oe) *op++ = *ip++;
        }
    }

    /// <summary>
    /// LZ4 decompressor.  Procreate 5.3+ (2023) stores tiles as "&lt;col&gt;~&lt;row&gt;.lz4" in Apple's framing
    /// (Compression.framework): a sequence of blocks, each "bv41" + LE32 decompressed size + LE32 compressed size
    /// + LZ4 block, or "bv4-" + LE32 size + raw bytes, terminated by "bv4$".  Standard LZ4 frames are handled too.
    /// </summary>
    public static class Lz4
    {
        public static bool IsFrame(byte[] src)
        {
            return src.Length >= 7 && src[0] == 0x04 && src[1] == 0x22 && src[2] == 0x4D && src[3] == 0x18;
        }

        public static bool IsAppleFrame(byte[] src)
        {
            return src.Length >= 8 && src[0] == (byte)'b' && src[1] == (byte)'v' && src[2] == (byte)'4' && (src[3] == (byte)'1' || src[3] == (byte)'-' || src[3] == (byte)'$');
        }

        static uint LE32(byte[] b, int i) { return (uint)(b[i] | (b[i + 1] << 8) | (b[i + 2] << 16) | (b[i + 3] << 24)); }

        public static int DecompressApple(byte[] src, byte[] dst)
        {
            int ip = 0, op = 0;
            while (ip + 4 <= src.Length)
            {
                if (src[ip] != (byte)'b' || src[ip + 1] != (byte)'v' || src[ip + 2] != (byte)'4') throw new InvalidDataException("bad Apple LZ4 block magic");
                byte kind = src[ip + 3];
                ip += 4;
                if (kind == (byte)'$') break;
                if (kind == (byte)'1')
                {
                    int usize = (int)LE32(src, ip), csize = (int)LE32(src, ip + 4);
                    ip += 8;
                    if (op + usize > dst.Length) throw new InvalidDataException("Apple LZ4 output overrun");
                    int n = DecompressBlock(src, ip, csize, dst, op);
                    if (n != usize) throw new InvalidDataException("Apple LZ4 block size mismatch");
                    op += n;
                    ip += csize;
                }
                else if (kind == (byte)'-')
                {
                    int size = (int)LE32(src, ip);
                    ip += 4;
                    if (op + size > dst.Length) throw new InvalidDataException("Apple LZ4 output overrun");
                    Array.Copy(src, ip, dst, op, size);
                    op += size;
                    ip += size;
                }
                else throw new InvalidDataException("unknown Apple LZ4 block kind");
            }
            return op;
        }

        public static int DecompressFrame(byte[] src, byte[] dst)
        {
            int ip = 4;
            byte flg = src[ip++];
            ip++;                                   // BD byte
            bool blockChecksum = (flg & 0x10) != 0;
            if ((flg & 0x08) != 0) ip += 8;         // content size
            if ((flg & 0x01) != 0) ip += 4;         // dictionary id
            ip++;                                   // header checksum
            int op = 0;
            while (ip + 4 <= src.Length)
            {
                uint bs = (uint)(src[ip] | (src[ip + 1] << 8) | (src[ip + 2] << 16) | (src[ip + 3] << 24));
                ip += 4;
                if (bs == 0) break;
                bool stored = (bs & 0x80000000u) != 0;
                int len = (int)(bs & 0x7FFFFFFF);
                if (stored) { Array.Copy(src, ip, dst, op, len); op += len; }
                else op += DecompressBlock(src, ip, len, dst, op);
                ip += len;
                if (blockChecksum) ip += 4;
            }
            return op;
        }

        /// <summary>One LZ4 block into dst at op.  Matches may reach back to the start of dst: in Apple's framing
        /// (and in LZ4 frames with linked blocks) later blocks reference earlier decoded output.</summary>
        public static int DecompressBlock(byte[] src, int ip, int len, byte[] dst, int op)
        {
            int ipEnd = ip + len, opStart = op;
            while (ip < ipEnd)
            {
                int token = src[ip++];
                int lit = token >> 4;
                if (lit == 15) { int s; do { s = src[ip++]; lit += s; } while (s == 255); }
                Array.Copy(src, ip, dst, op, lit);
                ip += lit; op += lit;
                if (ip >= ipEnd) break;
                int offset = src[ip] | (src[ip + 1] << 8);
                ip += 2;
                int ml = (token & 15) + 4;
                if ((token & 15) == 15) { int s; do { s = src[ip++]; ml += s; } while (s == 255); }
                int mp = op - offset;
                if (mp < 0 || offset == 0) throw new InvalidDataException("LZ4 offset out of range");
                if (op + ml > dst.Length) throw new InvalidDataException("LZ4 output overrun");
                for (int i = 0; i < ml; i++) dst[op++] = dst[mp++];
            }
            return op - opStart;
        }
    }
}
