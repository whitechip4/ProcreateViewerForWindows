using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ProcreateViewer
{
    public static class BlendModes
    {
        private static readonly Dictionary<int, string> names = new Dictionary<int, string>
        {
            { 0, "Normal" }, { 1, "Multiply" }, { 2, "Screen" }, { 3, "Add" }, { 4, "Lighten" }, { 5, "Exclusion" },
            { 6, "Difference" }, { 7, "Subtract" }, { 8, "Linear Burn" }, { 9, "Color Dodge" }, { 10, "Color Burn" },
            { 11, "Overlay" }, { 12, "Hard Light" }, { 13, "Color" }, { 14, "Luminosity" }, { 15, "Hue" },
            { 16, "Saturation" }, { 17, "Soft Light" }, { 19, "Darken" }, { 20, "Hard Mix" }, { 21, "Vivid Light" },
            { 22, "Linear Light" }, { 23, "Pin Light" }, { 24, "Lighter Color" }, { 25, "Darker Color" }, { 26, "Divide" },
        };
        public static string Name(int mode) { string n; return names.TryGetValue(mode, out n) ? n : "Blend " + mode; }
    }

    /// <summary>Premultiplied float RGBA working surface.</summary>
    public sealed class Canvas
    {
        public readonly int W, H;
        public readonly float[] Px;
        public Canvas(int w, int h) { W = w; H = h; Px = new float[(long)w * h * 4]; }

        public void Fill(float r, float g, float b, float a)
        {
            float pr = r * a, pg = g * a, pb = b * a;
            for (long i = 0; i < Px.Length; i += 4) { Px[i] = pr; Px[i + 1] = pg; Px[i + 2] = pb; Px[i + 3] = a; }
        }
    }

    public static unsafe class Compositor
    {
        /// <summary>Pixel loops use half the cores so the machine stays responsive while a file loads.</summary>
        public static readonly ParallelOptions Par = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) };

        static float Clamp01(float v) { return v < 0 ? 0 : (v > 1 ? 1 : v); }

        static float HardLight(float cb, float cs) { return cs <= 0.5f ? cb * 2 * cs : Sep(2, cb, 2 * cs - 1); }

        /// <summary>Separable blend function B(cb, cs) on straight colour channels.</summary>
        static float Sep(int mode, float cb, float cs)
        {
            switch (mode)
            {
                case 1: return cb * cs;
                case 2: return cb + cs - cb * cs;
                case 3: return Math.Min(1, cb + cs);
                case 4: return Math.Max(cb, cs);
                case 5: return cb + cs - 2 * cb * cs;
                case 6: return Math.Abs(cb - cs);
                case 7: return Math.Max(0, cb - cs);
                case 8: return Math.Max(0, cb + cs - 1);
                case 9: return cb <= 0 ? 0 : (cs >= 1 ? 1 : Math.Min(1, cb / Math.Max(1e-6f, 1 - cs)));
                case 10: return cb >= 1 ? 1 : (cs <= 0 ? 0 : 1 - Math.Min(1, (1 - cb) / Math.Max(1e-6f, cs)));
                case 11: return HardLight(cs, cb);
                case 12: return HardLight(cb, cs);
                case 17:
                    {
                        float d = cb <= 0.25f ? ((16 * cb - 12) * cb + 4) * cb : (float)Math.Sqrt(cb);
                        return cs <= 0.5f ? cb - (1 - 2 * cs) * cb * (1 - cb) : cb + (2 * cs - 1) * (d - cb);
                    }
                case 19: return Math.Min(cb, cs);
                case 20: return cb + cs >= 1 ? 1 : 0;
                case 21: return cs <= 0.5f ? Sep(10, cb, 2 * cs) : Sep(9, cb, 2 * (cs - 0.5f));
                case 22: return Clamp01(cb + 2 * cs - 1);
                case 23: return cs <= 0.5f ? Math.Min(cb, 2 * cs) : Math.Max(cb, 2 * cs - 1);
                case 26: return cs <= 0 ? 1 : Math.Min(1, cb / Math.Max(cs, 1e-6f));
                default: return cs;
            }
        }

        static float Lum(float r, float g, float b) { return 0.3f * r + 0.59f * g + 0.11f * b; }
        static float Sat(float r, float g, float b) { return Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)); }

        static void ClipColor(ref float r, ref float g, ref float b)
        {
            float l = Lum(r, g, b);
            float n = Math.Min(r, Math.Min(g, b)), x = Math.Max(r, Math.Max(g, b));
            if (n < 0)
            {
                float d = l - n;
                if (d != 0) { r = l + (r - l) * l / d; g = l + (g - l) * l / d; b = l + (b - l) * l / d; }
            }
            if (x > 1)
            {
                float d = x - l;
                if (d != 0) { r = l + (r - l) * (1 - l) / d; g = l + (g - l) * (1 - l) / d; b = l + (b - l) * (1 - l) / d; }
            }
        }

        static void SetLum(ref float r, ref float g, ref float b, float l)
        {
            float d = l - Lum(r, g, b);
            r += d; g += d; b += d;
            ClipColor(ref r, ref g, ref b);
        }

        static void SetSat(ref float r, ref float g, ref float b, float s)
        {
            float mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b)), rng = mx - mn;
            if (rng > 0) { r = (r - mn) * s / rng; g = (g - mn) * s / rng; b = (b - mn) * s / rng; }
            else { r = g = b = 0; }
        }

        static void BlendFn(int mode, float cbr, float cbg, float cbb, float csr, float csg, float csb, out float r, out float g, out float b)
        {
            switch (mode)
            {
                case 13: r = csr; g = csg; b = csb; SetLum(ref r, ref g, ref b, Lum(cbr, cbg, cbb)); return;
                case 14: r = cbr; g = cbg; b = cbb; SetLum(ref r, ref g, ref b, Lum(csr, csg, csb)); return;
                case 15: r = csr; g = csg; b = csb; SetSat(ref r, ref g, ref b, Sat(cbr, cbg, cbb)); SetLum(ref r, ref g, ref b, Lum(cbr, cbg, cbb)); return;
                case 16: r = cbr; g = cbg; b = cbb; SetSat(ref r, ref g, ref b, Sat(csr, csg, csb)); SetLum(ref r, ref g, ref b, Lum(cbr, cbg, cbb)); return;
                case 24:
                    if (Lum(csr, csg, csb) > Lum(cbr, cbg, cbb)) { r = csr; g = csg; b = csb; } else { r = cbr; g = cbg; b = cbb; }
                    return;
                case 25:
                    if (Lum(csr, csg, csb) < Lum(cbr, cbg, cbb)) { r = csr; g = csg; b = csb; } else { r = cbr; g = cbg; b = cbb; }
                    return;
                default:
                    r = Sep(mode, cbr, csr); g = Sep(mode, cbg, csg); b = Sep(mode, cbb, csb);
                    return;
            }
        }

        static float ClipAlpha(LayerPixels clip, int x, int y)
        {
            if (x < clip.X || y < clip.Y || x >= clip.X + clip.W || y >= clip.Y + clip.H) return 0;
            return clip.Bgra[(((long)(y - clip.Y) * clip.W) + (x - clip.X)) * 4 + 3] / 255f;
        }

        /// <summary>Blend a layer (premultiplied BGRA bytes) onto the canvas with opacity, blend mode and optional clipping base.</summary>
        public static void BlendLayer(Canvas dst, LayerPixels src, float opacity, int mode, LayerPixels clip)
        {
            if (src == null || src.IsEmpty || opacity <= 0) return;
            int x0 = Math.Max(0, src.X), y0 = Math.Max(0, src.Y);
            int x1 = Math.Min(dst.W, src.X + src.W), y1 = Math.Min(dst.H, src.Y + src.H);
            if (x1 <= x0 || y1 <= y0) return;
            float op = opacity / 255f;
            Parallel.For(y0, y1, Par, y =>
            {
                fixed (float* dp = dst.Px)
                fixed (byte* sp = src.Bgra)
                {
                    float* d = dp + ((long)y * dst.W + x0) * 4;
                    byte* s = sp + ((long)(y - src.Y) * src.W + (x0 - src.X)) * 4;
                    for (int x = x0; x < x1; x++, d += 4, s += 4)
                    {
                        float k = op;
                        if (clip != null) { k *= ClipAlpha(clip, x, y); if (k <= 0) continue; }
                        float sa = s[3] * k;
                        if (sa <= 0) continue;
                        float sr = s[2] * k, sg = s[1] * k, sb = s[0] * k;
                        float ca = d[3];
                        if (mode == 0 || ca <= 0)
                        {
                            float ia = 1 - sa;
                            d[0] = sr + d[0] * ia; d[1] = sg + d[1] * ia; d[2] = sb + d[2] * ia; d[3] = sa + ca * ia;
                            continue;
                        }
                        float csr = sr / sa, csg = sg / sa, csb = sb / sa;
                        float cbr = d[0] / ca, cbg = d[1] / ca, cbb = d[2] / ca;
                        float br, bg, bb;
                        BlendFn(mode, cbr, cbg, cbb, csr, csg, csb, out br, out bg, out bb);
                        float w1 = 1 - ca, w2 = 1 - sa, w3 = sa * ca;
                        d[0] = w1 * sr + w2 * d[0] + w3 * Clamp01(br);
                        d[1] = w1 * sg + w2 * d[1] + w3 * Clamp01(bg);
                        d[2] = w1 * sb + w2 * d[2] + w3 * Clamp01(bb);
                        d[3] = sa + ca - sa * ca;
                    }
                }
            });
        }

        /// <summary>Normal-mode blend of a whole canvas (a rendered group) onto another.</summary>
        public static void BlendCanvas(Canvas dst, Canvas src, float opacity)
        {
            if (opacity <= 0) return;
            Parallel.For(0, dst.H, Par, y =>
            {
                fixed (float* dp = dst.Px)
                fixed (float* sp = src.Px)
                {
                    float* d = dp + (long)y * dst.W * 4;
                    float* s = sp + (long)y * src.W * 4;
                    for (int x = 0; x < dst.W; x++, d += 4, s += 4)
                    {
                        float sa = s[3] * opacity;
                        if (sa <= 0) continue;
                        float ia = 1 - sa;
                        d[0] = s[0] * opacity + d[0] * ia; d[1] = s[1] * opacity + d[1] * ia; d[2] = s[2] * opacity + d[2] * ia; d[3] = sa + d[3] * ia;
                    }
                }
            });
        }

        /// <summary>Box-filter downscale of premultiplied layer pixels to a preview scale.</summary>
        public static LayerPixels Downscale(LayerPixels full, double s, int pw, int ph)
        {
            if (full.IsEmpty || s >= 1) return full;
            int dx0 = Math.Max(0, (int)Math.Floor(full.X * s)), dy0 = Math.Max(0, (int)Math.Floor(full.Y * s));
            int dx1 = Math.Min(pw, (int)Math.Ceiling((full.X + full.W) * s)), dy1 = Math.Min(ph, (int)Math.Ceiling((full.Y + full.H) * s));
            if (dx1 <= dx0 || dy1 <= dy0) return LayerPixels.Empty;
            var res = new LayerPixels { X = dx0, Y = dy0, W = dx1 - dx0, H = dy1 - dy0 };
            res.Bgra = new byte[res.W * res.H * 4];
            double inv = 1.0 / s;
            Parallel.For(dy0, dy1, Par, dy =>
            {
                int sy0 = Math.Max(full.Y, (int)Math.Floor(dy * inv)), sy1 = Math.Min(full.Y + full.H, (int)Math.Ceiling((dy + 1) * inv));
                if (sy1 <= sy0) return;
                fixed (byte* sp = full.Bgra)
                fixed (byte* dp = res.Bgra)
                {
                    byte* d = dp + ((long)(dy - dy0) * res.W) * 4;
                    for (int dx = dx0; dx < dx1; dx++, d += 4)
                    {
                        int sx0 = Math.Max(full.X, (int)Math.Floor(dx * inv)), sx1 = Math.Min(full.X + full.W, (int)Math.Ceiling((dx + 1) * inv));
                        if (sx1 <= sx0) continue;
                        long a0 = 0, a1 = 0, a2 = 0, a3 = 0;
                        for (int sy = sy0; sy < sy1; sy++)
                        {
                            byte* p = sp + (((long)(sy - full.Y) * full.W) + (sx0 - full.X)) * 4;
                            for (int sx = sx0; sx < sx1; sx++, p += 4) { a0 += p[0]; a1 += p[1]; a2 += p[2]; a3 += p[3]; }
                        }
                        long n = (long)(sy1 - sy0) * (sx1 - sx0), h = n / 2;
                        d[0] = (byte)((a0 + h) / n); d[1] = (byte)((a1 + h) / n); d[2] = (byte)((a2 + h) / n); d[3] = (byte)((a3 + h) / n);
                    }
                }
            });
            return res;
        }

        /// <summary>Canvas to GDI+ bitmap, optionally applying document flips/orientation and converting to straight alpha.</summary>
        public static Bitmap ToBitmap(Canvas c, ProcreateDocument doc, bool orient, bool straightAlpha)
        {
            int W = c.W, H = c.H;
            int o = orient ? doc.Orientation : 1;
            bool fh = orient && doc.FlipH, fv = orient && doc.FlipV;
            bool rot = o == 3 || o == 4;
            int DW = rot ? H : W, DH = rot ? W : H;
            var bmp = new Bitmap(DW, DH, straightAlpha ? PixelFormat.Format32bppArgb : PixelFormat.Format32bppPArgb);
            var bd = bmp.LockBits(new Rectangle(0, 0, DW, DH), ImageLockMode.WriteOnly, bmp.PixelFormat);
            try
            {
                IntPtr scan0 = bd.Scan0;
                int stride = bd.Stride;
                Parallel.For(0, DH, Par, y =>
                {
                    fixed (float* px = c.Px)
                    {
                        byte* row = (byte*)scan0 + (long)y * stride;
                        for (int x = 0; x < DW; x++, row += 4)
                        {
                            int sx, sy;
                            switch (o)
                            {
                                case 2: sx = W - 1 - x; sy = H - 1 - y; break;
                                case 3: sx = W - 1 - y; sy = x; break;          // 90 degrees counter-clockwise
                                case 4: sx = y; sy = H - 1 - x; break;          // 90 degrees clockwise
                                default: sx = x; sy = y; break;
                            }
                            if (fh) sx = W - 1 - sx;
                            if (fv) sy = H - 1 - sy;
                            float* p = px + ((long)sy * W + sx) * 4;
                            float a = p[3], r = p[0], g = p[1], b = p[2];
                            if (straightAlpha && a > 0) { r /= a; g /= a; b /= a; }
                            row[0] = ToByte(b); row[1] = ToByte(g); row[2] = ToByte(r); row[3] = ToByte(a);
                        }
                    }
                });
            }
            finally { bmp.UnlockBits(bd); }
            return bmp;
        }

        static byte ToByte(float v) { int i = (int)(v * 255f + 0.5f); return (byte)(i < 0 ? 0 : (i > 255 ? 255 : i)); }

        /// <summary>Layer pixels moved into display space (document flips then orientation), bounding box included.</summary>
        public static LayerPixels OrientLayer(LayerPixels px, ProcreateDocument doc)
        {
            if (px == null || px.IsEmpty) return px;
            int W = doc.Width, H = doc.Height, o = doc.Orientation;
            if (o == 1 && !doc.FlipH && !doc.FlipV) return px;
            // map the four corners to find the destination box
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            int[] cx = { px.X, px.X + px.W - 1 }, cy = { px.Y, px.Y + px.H - 1 };
            foreach (int x in cx) foreach (int y in cy)
                {
                    int dx, dy; Map(x, y, W, H, o, doc.FlipH, doc.FlipV, out dx, out dy);
                    if (dx < minX) minX = dx; if (dx > maxX) maxX = dx; if (dy < minY) minY = dy; if (dy > maxY) maxY = dy;
                }
            var res = new LayerPixels { X = minX, Y = minY, W = maxX - minX + 1, H = maxY - minY + 1 };
            res.Bgra = new byte[res.W * res.H * 4];
            Parallel.For(0, px.H, Par, y =>
            {
                fixed (byte* sp = px.Bgra)
                fixed (byte* dp = res.Bgra)
                {
                    byte* s = sp + (long)y * px.W * 4;
                    for (int x = 0; x < px.W; x++, s += 4)
                    {
                        int dx, dy; Map(px.X + x, px.Y + y, W, H, o, doc.FlipH, doc.FlipV, out dx, out dy);
                        byte* d = dp + ((long)(dy - res.Y) * res.W + (dx - res.X)) * 4;
                        d[0] = s[0]; d[1] = s[1]; d[2] = s[2]; d[3] = s[3];
                    }
                }
            });
            return res;
        }

        /// <summary>Storage-space pixel to display-space pixel (inverse of the mapping in ToBitmap).</summary>
        private static void Map(int x, int y, int W, int H, int o, bool fh, bool fv, out int dx, out int dy)
        {
            if (fh) x = W - 1 - x;
            if (fv) y = H - 1 - y;
            switch (o)
            {
                case 2: dx = W - 1 - x; dy = H - 1 - y; break;
                case 3: dx = y; dy = W - 1 - x; break;
                case 4: dx = H - 1 - y; dy = x; break;
                default: dx = x; dy = y; break;
            }
        }

        /// <summary>Bounding-box bitmap (premultiplied) of a layer, for thumbnails.</summary>
        public static Bitmap LayerBitmap(LayerPixels px)
        {
            var bmp = new Bitmap(px.W, px.H, PixelFormat.Format32bppPArgb);
            var bd = bmp.LockBits(new Rectangle(0, 0, px.W, px.H), ImageLockMode.WriteOnly, bmp.PixelFormat);
            try
            {
                for (int y = 0; y < px.H; y++)
                    System.Runtime.InteropServices.Marshal.Copy(px.Bgra, y * px.W * 4, new IntPtr(bd.Scan0.ToInt64() + (long)y * bd.Stride), px.W * 4);
            }
            finally { bmp.UnlockBits(bd); }
            return bmp;
        }
    }

    /// <summary>Holds a document plus a cache of preview-resolution layer pixels and composites them.</summary>
    public sealed class Renderer
    {
        public const int PreviewMax = 2048;
        public readonly ProcreateDocument Doc;
        public readonly double Scale;
        public readonly int PW, PH;
        private readonly Dictionary<string, LayerPixels> cache = new Dictionary<string, LayerPixels>(StringComparer.Ordinal);
        private readonly object cacheLock = new object();

        // full-resolution layers, kept while in full-size mode so that toggling layers only re-composites
        private readonly Dictionary<string, LayerPixels> fullCache = new Dictionary<string, LayerPixels>(StringComparer.Ordinal);
        private long fullCacheBytes;
        public static readonly long FullCacheBudget = ComputeBudget();

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength, dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

        /// <summary>30% of physical memory (at least 512 MB) may hold full-resolution layers.</summary>
        private static long ComputeBudget()
        {
            try
            {
                var ms = new MEMORYSTATUSEX();
                ms.dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(MEMORYSTATUSEX));
                if (GlobalMemoryStatusEx(ref ms)) return Math.Max(512L << 20, (long)(ms.ullTotalPhys * 0.3));
            }
            catch { }
            return 1L << 30;
        }

        public long FullCacheBytes { get { lock (cacheLock) return fullCacheBytes; } }

        public LayerPixels Full(Layer l)
        {
            if (l.IsGroup || l.Uuid == null) return LayerPixels.Empty;
            LayerPixels p;
            lock (cacheLock) if (fullCache.TryGetValue(l.Uuid, out p)) return p;
            p = Doc.DecodeLayer(l.Uuid);
            long size = p.IsEmpty ? 0 : p.Bgra.LongLength;
            lock (cacheLock)
            {
                if (fullCacheBytes + size <= FullCacheBudget) { fullCache[l.Uuid] = p; fullCacheBytes += size; }
                else FullCacheOverflow = true;
            }
            return p;
        }

        public void ClearFullCache()
        {
            lock (cacheLock) { fullCache.Clear(); fullCacheBytes = 0; FullCacheOverflow = false; }
        }

        public Renderer(ProcreateDocument doc, int previewMax)
        {
            Doc = doc;
            int longSide = Math.Max(1, Math.Max(doc.Width, doc.Height));
            Scale = Math.Min(1.0, (double)previewMax / longSide);
            PW = Math.Max(1, (int)Math.Round(doc.Width * Scale));
            PH = Math.Max(1, (int)Math.Round(doc.Height * Scale));
        }

        public LayerPixels Preview(Layer l)
        {
            if (l.IsGroup || l.Uuid == null) return LayerPixels.Empty;
            LayerPixels p, full;
            lock (cacheLock)
            {
                if (cache.TryGetValue(l.Uuid, out p)) return p;
                fullCache.TryGetValue(l.Uuid, out full);       // derive from the full-size cache when we have it
            }
            if (full == null) full = Doc.DecodeLayer(l.Uuid);
            p = Scale < 1 ? Compositor.Downscale(full, Scale, PW, PH) : full;
            lock (cacheLock) cache[l.Uuid] = p;
            return p;
        }

        /// <summary>True when a full-resolution layer did not fit the cache budget (full-size mode would re-decode).</summary>
        public bool FullCacheOverflow { get; private set; }

        /// <summary>Decode every layer; with fullSize the full-resolution pixels are cached and the previews derived from them.</summary>
        public void LoadAll(Action<int, int> progress, bool fullSize)
        {
            var layers = Doc.AllLayers().Where(l => !l.IsGroup).ToList();
            int done = 0;
            var opts = new ParallelOptions { MaxDegreeOfParallelism = 2 };
            Parallel.ForEach(layers, opts, l =>
            {
                if (fullSize) Full(l);
                Preview(l);
                int n = Interlocked.Increment(ref done);
                if (progress != null) progress(n, layers.Count);
            });
        }

        public Canvas Composite(Func<Layer, bool> visible, bool background, bool preview)
        {
            var c = new Canvas(preview ? PW : Doc.Width, preview ? PH : Doc.Height);
            if (background && !Doc.BackgroundHidden) c.Fill(Doc.BgR, Doc.BgG, Doc.BgB, Doc.BgA);
            CompositeInto(c, Doc.Layers, visible, preview);
            return c;
        }

        public Canvas Single(Layer l, bool preview)
        {
            var c = new Canvas(preview ? PW : Doc.Width, preview ? PH : Doc.Height);
            if (l.IsGroup) CompositeInto(c, l.Children, x => !x.Hidden, preview);
            else Compositor.BlendLayer(c, preview ? Preview(l) : Full(l), 1f, 0, null);
            return c;
        }

        private void CompositeInto(Canvas c, List<Layer> nodes, Func<Layer, bool> visible, bool preview)
        {
            LayerPixels clipBase = null;
            for (int i = nodes.Count - 1; i >= 0; i--)          // stored top-first, paint bottom-up
            {
                var n = nodes[i];
                if (!visible(n)) continue;
                if (n.IsGroup)
                {
                    var g = new Canvas(c.W, c.H);
                    CompositeInto(g, n.Children, visible, preview);
                    Compositor.BlendCanvas(c, g, n.Opacity);
                    clipBase = null;
                    continue;
                }
                var px = preview ? Preview(n) : Full(n);
                if (n.Clipped)
                {
                    if (clipBase != null) Compositor.BlendLayer(c, px, n.Opacity, n.Blend, clipBase);
                }
                else
                {
                    Compositor.BlendLayer(c, px, n.Opacity, n.Blend, null);
                    clipBase = px;
                }
            }
        }

        /// <summary>Full-resolution export: composite.png plus one PNG per layer (straight alpha, oriented).
        /// Groups become sub-folders; names are prefixed with their stack position (001 = top).</summary>
        public int ExportAll(string dir, Action<int, int> progress)
        {
            System.IO.Directory.CreateDirectory(dir);
            int total = Doc.LayerCount() + 1, done = 0;
            using (var bmp = Compositor.ToBitmap(Composite(l => !l.Hidden, true, false), Doc, true, true))
                bmp.Save(System.IO.Path.Combine(dir, "composite.png"), ImageFormat.Png);
            if (progress != null) progress(++done, total);
            ExportList(Doc.Layers, dir, progress, ref done, total);
            return total;
        }

        private static string SafeName(string name)
        {
            var s = new string((name ?? "").Select(ch => "\\/:*?\"<>|".IndexOf(ch) >= 0 || ch < ' ' ? '_' : ch).ToArray()).Trim();
            return s.Length == 0 ? "layer" : s;
        }

        private void ExportList(List<Layer> layers, string dir, Action<int, int> progress, ref int done, int total)
        {
            int idx = 0;
            foreach (var l in layers)
            {
                idx++;
                string prefix = string.Format("{0:D3}_", idx);
                if (l.IsGroup)
                {
                    string sub = System.IO.Path.Combine(dir, prefix + SafeName(l.Name) + (l.Hidden ? " (hidden)" : ""));
                    System.IO.Directory.CreateDirectory(sub);
                    ExportList(l.Children, sub, progress, ref done, total);
                    continue;
                }
                var c = new Canvas(Doc.Width, Doc.Height);
                Compositor.BlendLayer(c, Doc.DecodeLayer(l.Uuid), 1f, 0, null);
                using (var bmp = Compositor.ToBitmap(c, Doc, true, true))
                    bmp.Save(System.IO.Path.Combine(dir, prefix + SafeName(l.Name) + (l.Hidden ? " (hidden)" : "") + ".png"), ImageFormat.Png);
                if (progress != null) progress(++done, total);
            }
        }
    }
}
