using System;
using System.IO;

namespace ProcreateViewer
{
    /// <summary>
    /// LZO1X decompressor (the format Procreate used for layer tiles up to version 5.2).
    ///
    /// C# port of the decompressor in lzokay by Jack Andersen (https://github.com/jackoalan/lzokay),
    /// Copyright (c) 2018 Jack Andersen, MIT License (full text in LICENSE).  Instruction comments follow
    /// the original.  Every read and write is bounds-checked against both buffers.
    /// </summary>
    public static unsafe class Lzo
    {
        public static int Decompress(byte[] src, byte[] dst)
        {
            if (src.Length < 3) throw new InvalidDataException("LZO input too short");
            fixed (byte* srcBase = src)
            fixed (byte* dstBase = dst)
            {
                byte* inp = srcBase, inpEnd = srcBase + src.Length;
                byte* outp = dstBase, outpEnd = dstBase + dst.Length;
                byte* lbcur;
                long lblen = 0;
                uint state = 0, nstate;

                // First byte encoding
                if (*inp >= 22)
                {
                    // 22..255 : copy literal string, length = byte - 17 (4..238), state = 4 (no extra literals)
                    long len = *inp++ - 17;
                    NeedsIn(inp, len, inpEnd);
                    NeedsOut(outp, len, outpEnd);
                    for (long i = 0; i < len; i++) *outp++ = *inp++;
                    state = 4;
                }
                else if (*inp >= 18)
                {
                    // 18..21 : copy 0..3 literals, state = byte - 17
                    nstate = (uint)(*inp++ - 17);
                    state = nstate;
                    NeedsIn(inp, nstate, inpEnd);
                    NeedsOut(outp, nstate, outpEnd);
                    for (uint i = 0; i < nstate; i++) *outp++ = *inp++;
                }
                // 0..17 : regular instruction encoding (see below)

                while (true)
                {
                    NeedsIn(inp, 1, inpEnd);
                    byte inst = *inp++;
                    if ((inst & 0xC0) != 0)
                    {
                        // [M2]  1 L L D D D S S (128..255): copy 5-8 bytes within 2 kB; 0 1 L D D D S S (64..127): copy 3-4 bytes
                        //       followed by one byte H: distance = (H << 3) + D + 1, state = S
                        NeedsIn(inp, 1, inpEnd);
                        lbcur = outp - ((*inp++ << 3) + ((inst >> 2) & 0x7) + 1);
                        lblen = (inst >> 5) + 1;
                        nstate = (uint)(inst & 0x3);
                    }
                    else if ((inst & 0x20) != 0)
                    {
                        // [M3]  0 0 1 L L L L L (32..63): copy within 16 kB, length = 2 + (L ?: 31 + zero_bytes * 255 + next)
                        //       followed by LE16: distance = D + 1, state = S
                        lblen = (inst & 0x1f) + 2;
                        if (lblen == 2)
                        {
                            long zeros = CountZeros(ref inp, inpEnd);
                            NeedsIn(inp, 1, inpEnd);
                            lblen += zeros * 255 + 31 + *inp++;
                        }
                        NeedsIn(inp, 2, inpEnd);
                        uint d = (uint)(inp[0] | (inp[1] << 8));
                        inp += 2;
                        lbcur = outp - ((d >> 2) + 1);
                        nstate = d & 0x3;
                    }
                    else if ((inst & 0x10) != 0)
                    {
                        // [M4]  0 0 0 1 H L L L (16..31): copy within 16..48 kB, length = 2 + (L ?: 7 + zero_bytes * 255 + next)
                        //       followed by LE16: distance = 16384 + (H << 14) + D, state = S; distance == 16384 ends the stream
                        lblen = (inst & 0x7) + 2;
                        if (lblen == 2)
                        {
                            long zeros = CountZeros(ref inp, inpEnd);
                            NeedsIn(inp, 1, inpEnd);
                            lblen += zeros * 255 + 7 + *inp++;
                        }
                        NeedsIn(inp, 2, inpEnd);
                        uint d = (uint)(inp[0] | (inp[1] << 8));
                        inp += 2;
                        lbcur = outp - (((inst & 0x8) << 11) + (d >> 2));
                        nstate = d & 0x3;
                        if (lbcur == outp) break;   // stream finished
                        lbcur -= 16384;
                    }
                    else
                    {
                        // [M1] depends on the number of literals copied by the previous instruction
                        if (state == 0)
                        {
                            // 0 0 0 0 L L L L (0..15): long literal run, length = 3 + (L ?: 15 + zero_bytes * 255 + next), state = 4
                            long len = inst + 3;
                            if (len == 3)
                            {
                                long zeros = CountZeros(ref inp, inpEnd);
                                NeedsIn(inp, 1, inpEnd);
                                len += zeros * 255 + 15 + *inp++;
                            }
                            NeedsIn(inp, len, inpEnd);
                            NeedsOut(outp, len, outpEnd);
                            for (long i = 0; i < len; i++) *outp++ = *inp++;
                            state = 4;
                            continue;
                        }
                        else if (state != 4)
                        {
                            // 0 0 0 0 D D S S (0..15) after 1-3 literals: copy 2 bytes, distance = (H << 2) + D + 1
                            NeedsIn(inp, 1, inpEnd);
                            nstate = (uint)(inst & 0x3);
                            lbcur = outp - ((inst >> 2) + (*inp++ << 2) + 1);
                            lblen = 2;
                        }
                        else
                        {
                            // 0 0 0 0 D D S S (0..15) after 4+ literals: copy 3 bytes, distance = (H << 2) + D + 2049
                            NeedsIn(inp, 1, inpEnd);
                            nstate = (uint)(inst & 0x3);
                            lbcur = outp - ((inst >> 2) + (*inp++ << 2) + 2049);
                            lblen = 3;
                        }
                    }
                    if (lbcur < dstBase) throw new InvalidDataException("LZO lookbehind overrun");
                    NeedsIn(inp, nstate, inpEnd);
                    NeedsOut(outp, lblen + nstate, outpEnd);
                    // copy lookbehind (may overlap the output being written, so byte by byte, forwards)
                    for (long i = 0; i < lblen; i++) *outp++ = *lbcur++;
                    state = nstate;
                    // copy literals
                    for (uint i = 0; i < nstate; i++) *outp++ = *inp++;
                }

                if (lblen != 3) throw new InvalidDataException("LZO stream not terminated");
                return (int)(outp - dstBase);
            }
        }

        private static void NeedsIn(byte* inp, long count, byte* inpEnd)
        {
            if (inp + count > inpEnd) throw new InvalidDataException("LZO input overrun");
        }

        private static void NeedsOut(byte* outp, long count, byte* outpEnd)
        {
            if (outp + count > outpEnd) throw new InvalidDataException("LZO output overrun");
        }

        /// <summary>Skips a run of zero bytes (each worth 255 in a length) and returns how many were skipped.</summary>
        private static long CountZeros(ref byte* inp, byte* inpEnd)
        {
            byte* start = inp;
            while (inp < inpEnd && *inp == 0) inp++;
            return inp - start;
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
