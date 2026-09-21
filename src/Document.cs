using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;

namespace ProcreateViewer
{
    public sealed class Layer
    {
        public string Name = "";
        public string Uuid;
        public bool IsGroup, Hidden, Clipped;
        public float Opacity = 1f;
        public int Blend;
        public readonly List<Layer> Children = new List<Layer>();
        public string BlendName { get { return BlendModes.Name(Blend); } }
    }

    /// <summary>Premultiplied BGRA pixels of one layer inside its bounding box (upright canvas space).</summary>
    public sealed class LayerPixels
    {
        public int X, Y, W, H;
        public byte[] Bgra;
        public static readonly LayerPixels Empty = new LayerPixels();
        public bool IsEmpty { get { return Bgra == null || W <= 0 || H <= 0; } }
    }

    /// <summary>
    /// A .procreate document: zip container with Document.archive (NSKeyedArchiver plist) and one folder
    /// of LZO-compressed 256px tiles per layer.  Tiles are premultiplied RGBA stored bottom-up.
    /// </summary>
    public sealed class ProcreateDocument : IDisposable
    {
        public readonly string Path;
        public int Width, Height, TileSize = 256, Orientation = 1;
        public bool FlipH, FlipV, BackgroundHidden;
        public float BgR = 1, BgG = 1, BgB = 1, BgA = 1;
        public string CompositeUuid, Name;
        public double Dpi;
        public readonly List<Layer> Layers = new List<Layer>();     // top of the stack first, as stored

        private struct TileRef { public int Col, Row; public ZipArchiveEntry Entry; }

        private readonly ZipArchive zip;
        private readonly object zipLock = new object();
        private readonly Dictionary<string, ZipArchiveEntry> entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<TileRef>> tiles = new Dictionary<string, List<TileRef>>(StringComparer.Ordinal);

        public ProcreateDocument(string path)
        {
            Path = path;
            var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            zip = new ZipArchive(fs, ZipArchiveMode.Read, false);
            foreach (var e in zip.Entries)
            {
                entries[e.FullName] = e;
                // tiles: "<uuid>/<col>~<row>.chunk" (LZO, up to Procreate 5.2) or ".lz4" (Apple LZ4 framing, 5.3+)
                int ext;
                if (e.FullName.EndsWith(".chunk", StringComparison.Ordinal)) ext = 6;
                else if (e.FullName.EndsWith(".lz4", StringComparison.Ordinal)) ext = 4;
                else continue;
                int slash = e.FullName.IndexOf('/');
                if (slash < 0) continue;
                string uuid = e.FullName.Substring(0, slash);
                string rest = e.FullName.Substring(slash + 1, e.FullName.Length - slash - 1 - ext);
                int tilde = rest.IndexOf('~');
                int c, r;
                if (tilde < 0 || !int.TryParse(rest.Substring(0, tilde), out c) || !int.TryParse(rest.Substring(tilde + 1), out r)) continue;
                List<TileRef> list;
                if (!tiles.TryGetValue(uuid, out list)) tiles[uuid] = list = new List<TileRef>();
                list.Add(new TileRef { Col = c, Row = r, Entry = e });
            }
            ZipArchiveEntry arch;
            if (!entries.TryGetValue("Document.archive", out arch)) throw new InvalidDataException("Document.archive not found (not a Procreate file?)");
            ParseRoot(new NsArchive(ReadEntry(arch)));
        }

        public void Dispose() { zip.Dispose(); }

        public int DisplayWidth { get { return Orientation == 3 || Orientation == 4 ? Height : Width; } }
        public int DisplayHeight { get { return Orientation == 3 || Orientation == 4 ? Width : Height; } }

        private void ParseRoot(NsArchive ns)
        {
            var root = ns.Root;
            string size = ns.StrKey(root, "size") ?? "{0, 0}";
            var parts = size.Trim('{', '}', ' ').Split(',');
            Width = (int)double.Parse(parts[0].Trim(), CultureInfo.InvariantCulture);
            Height = (int)double.Parse(parts[1].Trim(), CultureInfo.InvariantCulture);
            TileSize = (int)NsArchive.Num(root, "tileSize", 256);
            Orientation = (int)NsArchive.Num(root, "orientation", 1);
            FlipH = NsArchive.Bool(root, "flippedHorizontally", false);
            FlipV = NsArchive.Bool(root, "flippedVertically", false);
            BackgroundHidden = NsArchive.Bool(root, "backgroundHidden", false);
            var bg = ns.Get(root, "backgroundColor") as byte[];
            if (bg != null && bg.Length >= 16)
            {
                BgR = BitConverter.ToSingle(bg, 0); BgG = BitConverter.ToSingle(bg, 4);
                BgB = BitConverter.ToSingle(bg, 8); BgA = BitConverter.ToSingle(bg, 12);
            }
            var comp = ns.Get(root, "composite") as Dictionary<string, object>;
            if (comp != null) CompositeUuid = ns.StrKey(comp, "UUID");
            Dpi = NsArchive.Num(root, "SilicaDocumentArchiveDPIKey", 0);
            Name = ns.StrKey(root, "name");
            // Two lists describe the stack: "layers" is flat (every SilicaLayer, top first) and "unwrappedLayers"
            // is the group hierarchy (SilicaGroup nodes with "children"); without groups both are identical.
            // Take whichever list actually contains SilicaGroup objects rather than trusting the key names.
            var tree = ParseList(ns, ns.NsObjects(root, "layers"));
            var alt = ParseList(ns, ns.NsObjects(root, "unwrappedLayers"));
            if (!HasGroup(tree) && HasGroup(alt)) tree = alt;
            if (tree.Count == 0) tree = alt;
            Layers.AddRange(tree);
        }

        private List<Layer> ParseList(NsArchive ns, List<object> list)
        {
            var res = new List<Layer>();
            if (list == null) return res;
            foreach (var l in list)
            {
                var node = ParseNode(ns, l);
                if (node != null) res.Add(node);
            }
            return res;
        }

        private static bool HasGroup(List<Layer> list)
        {
            foreach (var l in list) if (l.IsGroup) return true;
            return false;
        }

        private Layer ParseNode(NsArchive ns, object v)
        {
            var d = ns.Dict(v);
            if (d == null) return null;
            string cn = ns.ClassName(d);
            var layer = new Layer();
            if (cn == "SilicaGroup")
            {
                layer.IsGroup = true;
                layer.Name = ns.StrKey(d, "name") ?? L.T("Group");
                layer.Hidden = NsArchive.Bool(d, "isHidden", false);
                layer.Opacity = (float)NsArchive.Num(d, "opacity", 1);
                layer.Clipped = NsArchive.Bool(d, "isClipped", false);
                var ch = ns.NsObjects(d, "children");
                if (ch != null)
                    foreach (var c in ch)
                    {
                        var n = ParseNode(ns, c);
                        if (n != null) layer.Children.Add(n);
                    }
                return layer;
            }
            if (cn != "SilicaLayer" && !d.ContainsKey("UUID")) return null;
            layer.Name = ns.StrKey(d, "name") ?? L.T("Layer");
            layer.Uuid = ns.StrKey(d, "UUID");
            layer.Hidden = NsArchive.Bool(d, "hidden", false);
            layer.Opacity = (float)NsArchive.Num(d, "opacity", 1);
            layer.Clipped = NsArchive.Bool(d, "clipped", false);
            object eb;
            if (d.TryGetValue("extendedBlend", out eb) && eb is long) layer.Blend = (int)(long)eb;
            else layer.Blend = (int)NsArchive.Num(d, "blend", 0);
            return layer;
        }

        public IEnumerable<Layer> AllLayers() { return Walk(Layers); }

        private static IEnumerable<Layer> Walk(List<Layer> list)
        {
            foreach (var l in list)
            {
                yield return l;
                if (l.IsGroup) foreach (var c in Walk(l.Children)) yield return c;
            }
        }

        public int LayerCount()
        {
            int n = 0;
            foreach (var l in AllLayers()) if (!l.IsGroup) n++;
            return n;
        }

        private byte[] ReadEntry(ZipArchiveEntry e)
        {
            lock (zipLock)
            {
                var buf = new byte[e.Length];
                using (var s = e.Open())
                {
                    int off = 0;
                    while (off < buf.Length)
                    {
                        int n = s.Read(buf, off, buf.Length - off);
                        if (n <= 0) break;
                        off += n;
                    }
                }
                return buf;
            }
        }

        public byte[] ThumbnailPng()
        {
            ZipArchiveEntry e;
            if (entries.TryGetValue("QuickLook/Thumbnail.png", out e)) return ReadEntry(e);
            foreach (var kv in entries)
                if (kv.Key.EndsWith("Thumbnail.png", StringComparison.OrdinalIgnoreCase)) return ReadEntry(kv.Value);
            return null;
        }

        private static int DecodeChunk(byte[] comp, byte[] buf)
        {
            if (Lz4.IsAppleFrame(comp)) return Lz4.DecompressApple(comp, buf);
            if (Lz4.IsFrame(comp)) return Lz4.DecompressFrame(comp, buf);
            return Lzo.Decompress(comp, buf);
        }

        /// <summary>Decode all tiles of a layer into premultiplied BGRA inside the bounding box of the present tiles.</summary>
        public unsafe LayerPixels DecodeLayer(string uuid)
        {
            List<TileRef> list;
            if (uuid == null || !tiles.TryGetValue(uuid, out list) || list.Count == 0) return LayerPixels.Empty;
            int ts = TileSize, W = Width, H = Height;
            var refs = list.ToArray();
            var comp = new byte[refs.Length][];
            var rects = new Rectangle[refs.Length];
            int x0 = int.MaxValue, y0 = int.MaxValue, x1 = 0, y1 = 0;
            for (int i = 0; i < refs.Length; i++)
            {
                int c = refs[i].Col, r = refs[i].Row;
                int tw = Math.Min(ts, W - c * ts), th = Math.Min(ts, H - r * ts);
                if (tw <= 0 || th <= 0) continue;
                int uy = H - (r * ts + th);                 // tiles are stored bottom-up
                rects[i] = new Rectangle(c * ts, uy, tw, th);
                x0 = Math.Min(x0, c * ts); y0 = Math.Min(y0, uy);
                x1 = Math.Max(x1, c * ts + tw); y1 = Math.Max(y1, uy + th);
                comp[i] = ReadEntry(refs[i].Entry);
            }
            if (x1 <= x0 || y1 <= y0) return LayerPixels.Empty;
            var lp = new LayerPixels { X = x0, Y = y0, W = x1 - x0, H = y1 - y0 };
            lp.Bgra = new byte[lp.W * lp.H * 4];
            using (var scratch = new ThreadLocal<byte[]>(() => new byte[ts * ts * 4]))
            {
                Parallel.For(0, refs.Length, Compositor.Par, i =>
                {
                    if (rects[i].IsEmpty || comp[i] == null) return;
                    byte[] buf = scratch.Value;
                    int n = DecodeChunk(comp[i], buf);
                    var rc = rects[i];
                    int stride = (n == rc.Width * rc.Height * 4) ? rc.Width * 4 : ts * 4;
                    if (n < (rc.Height - 1) * stride + rc.Width * 4) return;    // grayscale/mask tiles are not colour data
                    fixed (byte* src = buf)
                    fixed (byte* dst = lp.Bgra)
                    {
                        for (int j = 0; j < rc.Height; j++)
                        {
                            byte* s = src + j * stride;
                            int dy = rc.Y + rc.Height - 1 - j - y0;            // decoded row 0 is the bottom row of the upright tile
                            byte* d = dst + ((long)dy * lp.W + (rc.X - x0)) * 4;
                            for (int x = 0; x < rc.Width; x++)
                            {
                                d[0] = s[2]; d[1] = s[1]; d[2] = s[0]; d[3] = s[3];
                                d += 4; s += 4;
                            }
                        }
                    }
                });
            }
            return lp;
        }
    }
}
