using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;

namespace ProcreateViewer
{
    /// <summary>
    /// Writes a layered Photoshop file (PSD version 1, 8-bit RGB): every layer with its pixels, blend mode,
    /// opacity, visibility and clipping; groups as folders; the merged image with transparency; DPI.
    /// Layer data is RLE (PackBits) compressed and cropped to each layer's bounding box.
    /// </summary>
    public static class PsdWriter
    {
        private static readonly Dictionary<int, string> BlendKeys = new Dictionary<int, string>
        {
            { 0, "norm" }, { 1, "mul " }, { 2, "scrn" }, { 3, "lddg" }, { 4, "lite" }, { 5, "smud" }, { 6, "diff" },
            { 7, "fsub" }, { 8, "lbrn" }, { 9, "div " }, { 10, "idiv" }, { 11, "over" }, { 12, "hLit" }, { 13, "colr" },
            { 14, "lum " }, { 15, "hue " }, { 16, "sat " }, { 17, "sLit" }, { 19, "dark" }, { 20, "hMix" }, { 21, "vLit" },
            { 22, "lLit" }, { 23, "pLit" }, { 24, "lgCl" }, { 25, "dkCl" }, { 26, "fdiv" },
        };

        private enum Kind { Layer, GroupStart, GroupDivider }

        private sealed class Entry
        {
            public Kind Kind;
            public string Name;
            public string Blend = "norm";
            public byte Opacity = 255;
            public bool Hidden, Clipped;
            public Layer Source;
        }

        public static void Write(Renderer r, string path, Action<int, int> progress)
        {
            var doc = r.Doc;
            int W = doc.DisplayWidth, H = doc.DisplayHeight;
            if (W > 30000 || H > 30000) throw new InvalidOperationException(L.T("PSD is limited to 30000 px (PSB would be required)"));

            // Photoshop stores layers bottom-up; a group is "</Layer group>" divider ... children ... folder record
            var entries = new List<Entry>();
            Flatten(doc.Layers, entries);
            int total = entries.Count + 1, done = 0;

            string tmp = Path.GetTempFileName();
            try
            {
                using (var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite))
                using (var chan = new FileStream(tmp, FileMode.Create, FileAccess.ReadWrite))
                {
                    var w = new BE(fs);
                    // ---- header
                    w.Ascii("8BPS"); w.U16(1); w.Zero(6); w.U16(4); w.U32((uint)H); w.U32((uint)W); w.U16(8); w.U16(3);
                    // ---- colour mode data
                    w.U32(0);
                    // ---- image resources: resolution
                    w.U32(28);
                    w.Ascii("8BIM"); w.U16(0x03ED); w.U16(0); w.U32(16);
                    uint dpi = (uint)Math.Round((doc.Dpi > 0 ? doc.Dpi : 72) * 65536);
                    w.U32(dpi); w.U16(1); w.U16(1); w.U32(dpi); w.U16(1); w.U16(1);
                    // ---- layer and mask information
                    long sectionLenPos = fs.Position; w.U32(0);
                    long layerInfoLenPos = fs.Position; w.U32(0);
                    w.I16((short)-entries.Count);            // negative: first alpha channel of the merged image is transparency
                    var cw = new BE(chan);
                    foreach (var e in entries)
                    {
                        WriteLayerRecord(w, cw, r, e, W, H);
                        if (progress != null) progress(++done, total);
                    }
                    // channel image data follows all records
                    chan.Position = 0;
                    chan.CopyTo(fs);
                    if ((fs.Position - layerInfoLenPos - 4) % 2 == 1) w.Zero(1);
                    long layerInfoEnd = fs.Position;
                    w.U32(0);                                  // global layer mask info: none
                    long sectionEnd = fs.Position;
                    fs.Position = layerInfoLenPos; w.U32((uint)(layerInfoEnd - layerInfoLenPos - 4));
                    fs.Position = sectionLenPos; w.U32((uint)(sectionEnd - sectionLenPos - 4));
                    fs.Position = sectionEnd;
                    // ---- merged image (RGBA, RLE)
                    WriteMerged(w, r, W, H);
                    if (progress != null) progress(++done, total);
                }
            }
            finally { try { File.Delete(tmp); } catch { } }
        }

        private static void Flatten(List<Layer> layers, List<Entry> outList)
        {
            for (int i = layers.Count - 1; i >= 0; i--)          // stored top-first, PSD wants bottom-first
            {
                var l = layers[i];
                if (l.IsGroup)
                {
                    outList.Add(new Entry { Kind = Kind.GroupDivider, Name = "</Layer group>" });
                    Flatten(l.Children, outList);
                    outList.Add(new Entry { Kind = Kind.GroupStart, Name = l.Name, Opacity = ToByte(l.Opacity), Hidden = l.Hidden, Clipped = l.Clipped, Source = l });
                }
                else
                {
                    string key;
                    if (!BlendKeys.TryGetValue(l.Blend, out key)) key = "norm";
                    outList.Add(new Entry { Kind = Kind.Layer, Name = l.Name, Blend = key, Opacity = ToByte(l.Opacity), Hidden = l.Hidden, Clipped = l.Clipped, Source = l });
                }
            }
        }

        private static byte ToByte(float opacity) { int v = (int)Math.Round(opacity * 255); return (byte)(v < 0 ? 0 : (v > 255 ? 255 : v)); }

        private static void WriteLayerRecord(BE w, BE cw, Renderer r, Entry e, int W, int H)
        {
            LayerPixels px = null;
            if (e.Kind == Kind.Layer) px = Compositor.OrientLayer(r.Full(e.Source), r.Doc);
            bool hasPixels = px != null && !px.IsEmpty;
            int left = 0, top = 0, right = 0, bottom = 0;
            if (hasPixels)
            {
                left = Math.Max(0, px.X); top = Math.Max(0, px.Y);
                right = Math.Min(W, px.X + px.W); bottom = Math.Min(H, px.Y + px.H);
                if (right <= left || bottom <= top) hasPixels = false;
            }
            w.I32(top); w.I32(left); w.I32(bottom); w.I32(right);
            w.U16(4);
            int[] ids = { -1, 0, 1, 2 };
            if (hasPixels)
            {
                int cwid = right - left, chgt = bottom - top;
                var planes = StraightPlanes(px, left, top, cwid, chgt);
                for (int c = 0; c < 4; c++)
                {
                    long start = cw.Stream.Position;
                    WriteRlePlane(cw, planes[ids[c] < 0 ? 3 : ids[c]], cwid, chgt);
                    w.I16((short)ids[c]); w.U32((uint)(cw.Stream.Position - start));
                }
            }
            else
            {
                for (int c = 0; c < 4; c++) { w.I16((short)ids[c]); w.U32(2); cw.U16(0); }
            }
            w.Ascii("8BIM"); w.Ascii(e.Blend);
            w.U8(e.Opacity);
            w.U8((byte)(e.Clipped ? 1 : 0));
            w.U8((byte)(e.Hidden ? 2 : 0));
            w.U8(0);
            long extraLenPos = w.Stream.Position; w.U32(0);
            w.U32(0);                                          // layer mask data
            w.U32(0);                                          // blending ranges
            w.Pascal(e.Name);
            // unicode name
            byte[] uni = Encoding.BigEndianUnicode.GetBytes(e.Name ?? "");
            int uniLen = 4 + uni.Length, uniPad = (4 - uniLen % 4) % 4;
            w.Ascii("8BIM"); w.Ascii("luni"); w.U32((uint)(uniLen + uniPad)); w.U32((uint)(uni.Length / 2)); w.Bytes(uni); w.Zero(uniPad);
            if (e.Kind != Kind.Layer)
            {
                w.Ascii("8BIM"); w.Ascii("lsct"); w.U32(12);
                w.U32(e.Kind == Kind.GroupStart ? 1u : 3u);   // 1 = open folder, 3 = section divider
                w.Ascii("8BIM"); w.Ascii("norm");
            }
            long end = w.Stream.Position;
            w.Stream.Position = extraLenPos; w.U32((uint)(end - extraLenPos - 4)); w.Stream.Position = end;
        }

        /// <summary>Straight-alpha planes R,G,B,A for a window of premultiplied BGRA layer pixels.</summary>
        private static byte[][] StraightPlanes(LayerPixels px, int left, int top, int cw, int ch)
        {
            var planes = new byte[4][];
            for (int c = 0; c < 4; c++) planes[c] = new byte[cw * ch];
            for (int y = 0; y < ch; y++)
            {
                long srcRow = ((long)(top + y - px.Y) * px.W + (left - px.X)) * 4;
                int o = y * cw;
                for (int x = 0; x < cw; x++, srcRow += 4, o++)
                {
                    byte b = px.Bgra[srcRow], g = px.Bgra[srcRow + 1], rr = px.Bgra[srcRow + 2], a = px.Bgra[srcRow + 3];
                    if (a == 0) continue;
                    if (a < 255)
                    {
                        rr = (byte)Math.Min(255, (rr * 255 + a / 2) / a);
                        g = (byte)Math.Min(255, (g * 255 + a / 2) / a);
                        b = (byte)Math.Min(255, (b * 255 + a / 2) / a);
                    }
                    planes[0][o] = rr; planes[1][o] = g; planes[2][o] = b; planes[3][o] = a;
                }
            }
            return planes;
        }

        private static void WriteRlePlane(BE cw, byte[] plane, int width, int height)
        {
            cw.U16(1);
            var rows = new byte[height][];
            var buf = new byte[width + width / 128 + 2];
            for (int y = 0; y < height; y++)
            {
                int n = PackBits(plane, y * width, width, buf);
                rows[y] = new byte[n];
                Array.Copy(buf, rows[y], n);
                cw.U16((ushort)n);
            }
            for (int y = 0; y < height; y++) cw.Bytes(rows[y]);
        }

        private static void WriteMerged(BE w, Renderer r, int W, int H)
        {
            var canvas = r.Composite(l => !l.Hidden, true, false);
            using (var bmp = Compositor.ToBitmap(canvas, r.Doc, true, true))
            {
                var bd = bmp.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                var planes = new byte[4][];
                try
                {
                    for (int c = 0; c < 4; c++) planes[c] = new byte[W * H];
                    var row = new byte[W * 4];
                    for (int y = 0; y < H; y++)
                    {
                        System.Runtime.InteropServices.Marshal.Copy(new IntPtr(bd.Scan0.ToInt64() + (long)y * bd.Stride), row, 0, W * 4);
                        int o = y * W;
                        for (int x = 0; x < W; x++)
                        {
                            planes[0][o + x] = row[x * 4 + 2]; planes[1][o + x] = row[x * 4 + 1]; planes[2][o + x] = row[x * 4]; planes[3][o + x] = row[x * 4 + 3];
                        }
                    }
                }
                finally { bmp.UnlockBits(bd); }
                w.U16(1);
                var packed = new byte[4][][];
                var buf = new byte[W + W / 128 + 2];
                for (int c = 0; c < 4; c++)
                {
                    packed[c] = new byte[H][];
                    for (int y = 0; y < H; y++)
                    {
                        int n = PackBits(planes[c], y * W, W, buf);
                        packed[c][y] = new byte[n];
                        Array.Copy(buf, packed[c][y], n);
                        w.U16((ushort)n);
                    }
                }
                for (int c = 0; c < 4; c++) for (int y = 0; y < H; y++) w.Bytes(packed[c][y]);
            }
        }

        /// <summary>PackBits run-length encoding of one scan line.</summary>
        private static int PackBits(byte[] src, int off, int len, byte[] dst)
        {
            int i = off, end = off + len, o = 0;
            while (i < end)
            {
                byte b = src[i];
                int j = i + 1;
                while (j < end && src[j] == b && j - i < 128) j++;
                int run = j - i;
                if (run >= 2) { dst[o++] = (byte)(257 - run); dst[o++] = b; i = j; continue; }
                j = i + 1;
                while (j < end && j - i < 128)
                {
                    if (j + 2 < end && src[j] == src[j + 1] && src[j] == src[j + 2]) break;
                    j++;
                }
                int lit = j - i;
                dst[o++] = (byte)(lit - 1);
                Array.Copy(src, i, dst, o, lit);
                o += lit;
                i = j;
            }
            return o;
        }

        /// <summary>Big-endian primitive writer over a stream.</summary>
        private sealed class BE
        {
            public readonly Stream Stream;
            public BE(Stream s) { Stream = s; }
            public void U8(byte v) { Stream.WriteByte(v); }
            public void U16(ushort v) { Stream.WriteByte((byte)(v >> 8)); Stream.WriteByte((byte)v); }
            public void I16(short v) { U16((ushort)v); }
            public void U32(uint v) { Stream.WriteByte((byte)(v >> 24)); Stream.WriteByte((byte)(v >> 16)); Stream.WriteByte((byte)(v >> 8)); Stream.WriteByte((byte)v); }
            public void I32(int v) { U32((uint)v); }
            public void Bytes(byte[] b) { Stream.Write(b, 0, b.Length); }
            public void Ascii(string s) { Bytes(Encoding.ASCII.GetBytes(s)); }
            public void Zero(int n) { for (int i = 0; i < n; i++) Stream.WriteByte(0); }
            public void Pascal(string s)
            {
                byte[] b = Encoding.Default.GetBytes(s ?? "");
                if (b.Length > 255) Array.Resize(ref b, 255);
                U8((byte)b.Length); Bytes(b);
                int pad = (4 - (b.Length + 1) % 4) % 4;
                Zero(pad);
            }
        }
    }
}
