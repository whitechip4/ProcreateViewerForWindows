using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ProcreateViewer
{
    /// <summary>TreeView whose state image (the eye icon) acts as a large visibility toggle.
    /// The built-in CheckBoxes mode forces 16px glyphs, so the toggle is handled here instead.</summary>
    public sealed class LayerTree : TreeView
    {
        public event Action<TreeNode> EyeClicked;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref TVITEM item);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private struct TVITEM
        {
            public uint mask; public IntPtr hItem; public uint state; public uint stateMask; public IntPtr pszText;
            public int cchTextMax; public int iImage; public int iSelectedImage; public int cChildren; public IntPtr lParam;
        }

        /// <summary>State image list handed straight to the native control: WinForms' StateImageList property
        /// copies the images into a 16x16 list, which would shrink the eye icons.  Visibility is tracked here
        /// and written with TVM_SETITEM (native state index 1 = list image 1 = hidden, 2 = list image 2 = visible;
        /// index 0 means no image, so list image 0 is a blank placeholder).</summary>
        public ImageList Eyes;
        private readonly HashSet<TreeNode> hidden = new HashSet<TreeNode>();

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (Eyes != null) SendMessage(Handle, 0x1109 /* TVM_SETIMAGELIST */, (IntPtr)2 /* TVSIL_STATE */, Eyes.Handle);
            ApplyAll();
        }

        public bool IsOn(TreeNode n) { return !hidden.Contains(n); }

        public void SetOn(TreeNode n, bool on)
        {
            if (on) hidden.Remove(n); else hidden.Add(n);
            Apply(n);
        }

        public void ResetStates() { hidden.Clear(); }

        private void Apply(TreeNode n)
        {
            if (!IsHandleCreated || n.TreeView != this) return;
            var it = new TVITEM { mask = 0x10 | 0x8 /* TVIF_HANDLE | TVIF_STATE */, hItem = n.Handle, state = (uint)((IsOn(n) ? 2 : 1) << 12), stateMask = 0xF000 };
            SendMessage(Handle, 0x113F /* TVM_SETITEMW */, IntPtr.Zero, ref it);
        }

        public void ApplyAll() { ApplyAll(Nodes); }

        private void ApplyAll(TreeNodeCollection col)
        {
            foreach (TreeNode n in col) { Apply(n); ApplyAll(n.Nodes); }
        }

        private bool HandleEye(Point p)
        {
            var ht = HitTest(p);
            if (ht.Node == null || ht.Location != TreeViewHitTestLocations.StateImage) return false;
            SetOn(ht.Node, !IsOn(ht.Node));
            if (EyeClicked != null) EyeClicked(ht.Node);
            return true;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && HandleEye(e.Location)) return;   // no selection change for an eye click
            base.OnMouseDown(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x203 && HandleEye(PointToClient(Cursor.Position))) return;   // WM_LBUTTONDBLCLK: second toggle, no expand
            base.WndProc(ref m);
        }
    }

    /// <summary>Zoomable, pannable image surface with a checkerboard behind transparent pixels.</summary>
    public sealed class CanvasView : Control
    {
        private Bitmap image;
        private Bitmap[] mips;          // [0] = image, then successive half-size copies for fast zoomed-out drawing
        private float zoom;             // 0 = fit
        private PointF pan;
        private Point dragStart;
        private PointF panStart;
        private bool dragging;
        private readonly TextureBrush checker;

        public CanvasView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Canvas;
            var cb = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(cb))
            {
                g.Clear(Theme.CheckerLight);
                using (var br = new SolidBrush(Theme.CheckerDark))
                {
                    g.FillRectangle(br, 0, 0, 16, 16);
                    g.FillRectangle(br, 16, 16, 16, 16);
                }
            }
            checker = new TextureBrush(cb);
        }

        public Bitmap Image { get { return image; } }

        /// <summary>Raised whenever the zoom factor changes (wheel, fit, explicit zoom, image swap).</summary>
        public event Action ZoomChanged;

        private void OnZoomChanged() { if (ZoomChanged != null) ZoomChanged(); }

        public void SetImage(Bitmap bmp)
        {
            var old = image;
            // keep the on-screen size when a preview image is replaced by the full-resolution one (or back)
            if (old != null && zoom != 0 && old.Width != bmp.Width) zoom *= old.Width / (float)bmp.Width;
            if (bmp.PixelFormat != PixelFormat.Format32bppPArgb)
            {
                var conv = new Bitmap(bmp.Width, bmp.Height, PixelFormat.Format32bppPArgb);
                using (var g = Graphics.FromImage(conv)) g.DrawImageUnscaled(bmp, 0, 0);
                bmp.Dispose();
                bmp = conv;
            }
            image = bmp;
            DisposeMips();
            if (old != null && old != bmp) old.Dispose();
            var sw = Stopwatch.StartNew();
            BuildMips();
            paintsLogged = 0;
            Trace.Log(string.Format("view.SetImage {0}x{1} mips={2} ({3} ms) client={4}x{5} visible={6}", bmp.Width, bmp.Height, mips.Length, sw.ElapsedMilliseconds, ClientSize.Width, ClientSize.Height, Visible));
            Invalidate();
            OnZoomChanged();
        }

        private void DisposeMips()
        {
            if (mips == null) return;
            for (int i = 1; i < mips.Length; i++) mips[i].Dispose();
            mips = null;
        }

        /// <summary>Half-size pyramid so a zoomed-out view never has to filter the full-resolution bitmap.</summary>
        private void BuildMips()
        {
            var list = new System.Collections.Generic.List<Bitmap> { image };
            var cur = image;
            while (cur.Width > 400 && cur.Height > 400 && list.Count < 6)
            {
                cur = HalfSize(cur);
                list.Add(cur);
            }
            mips = list.ToArray();
        }

        private static unsafe Bitmap HalfSize(Bitmap src)
        {
            int w = Math.Max(1, src.Width / 2), h = Math.Max(1, src.Height / 2);
            var dst = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            var sd = src.LockBits(new Rectangle(0, 0, src.Width, src.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            var dd = dst.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            try
            {
                byte* sp = (byte*)sd.Scan0; byte* dp = (byte*)dd.Scan0;
                int ss = sd.Stride, ds = dd.Stride;
                System.Threading.Tasks.Parallel.For(0, h, Compositor.Par, y =>
                {
                    byte* r0 = sp + (long)(2 * y) * ss;
                    byte* r1 = r0 + ss;
                    byte* d = dp + (long)y * ds;
                    for (int x = 0; x < w; x++, d += 4, r0 += 8, r1 += 8)
                    {
                        d[0] = (byte)((r0[0] + r0[4] + r1[0] + r1[4] + 2) >> 2);
                        d[1] = (byte)((r0[1] + r0[5] + r1[1] + r1[5] + 2) >> 2);
                        d[2] = (byte)((r0[2] + r0[6] + r1[2] + r1[6] + 2) >> 2);
                        d[3] = (byte)((r0[3] + r0[7] + r1[3] + r1[7] + 2) >> 2);
                    }
                });
            }
            finally { src.UnlockBits(sd); dst.UnlockBits(dd); }
            return dst;
        }

        private int paintsLogged;

        public float Zoom { get { return zoom == 0 ? FitScale() : zoom; } }
        public bool IsFit { get { return zoom == 0; } }

        private float FitScale()
        {
            if (image == null) return 1;
            float s = Math.Min((ClientSize.Width - 8f) / image.Width, (ClientSize.Height - 8f) / image.Height);
            return Math.Min(1f, Math.Max(0.01f, s));
        }

        public void Fit() { zoom = 0; pan = PointF.Empty; Invalidate(); OnZoomChanged(); }
        public void SetZoom(float z) { zoom = z; pan = PointF.Empty; Invalidate(); OnZoomChanged(); }

        private RectangleF DestRect()
        {
            float s = Zoom;
            float w = image.Width * s, h = image.Height * s;
            return new RectangleF((ClientSize.Width - w) / 2 + pan.X, (ClientSize.Height - h) / 2 + pan.Y, w, h);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            if (image == null) return;
            var r = DestRect();
            float s = Zoom;
            var ri = Rectangle.Round(r);
            if (paintsLogged < 2) { paintsLogged++; Trace.Log(string.Format("paint zoom={0:0.000} dest={1} clip={2} dpi={3}", s, ri, e.ClipRectangle, g.DpiX)); }
            g.FillRectangle(checker, ri);
            g.CompositingMode = CompositingMode.SourceOver;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            // only the part of the picture that is on screen
            var vis = RectangleF.Intersect(r, new RectangleF(0, 0, ClientSize.Width, ClientSize.Height));
            if (vis.Width <= 0 || vis.Height <= 0) return;
            // pick the pyramid level closest above the zoom factor: the filter then works on at most 2x the pixels
            int lvl = 0;
            if (mips != null)
                while (lvl + 1 < mips.Length && s <= 0.5f * (mips[lvl].Width / (float)image.Width)) lvl++;
            var src = mips != null ? mips[lvl] : image;
            float ms = src.Width / (float)image.Width;
            var srcRect = new RectangleF((vis.X - r.X) / s * ms, (vis.Y - r.Y) / s * ms, vis.Width / s * ms, vis.Height / s * ms);
            g.InterpolationMode = s >= 1 ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBilinear;
            g.DrawImage(src, vis, srcRect, GraphicsUnit.Pixel);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (image == null) return;
            float oldS = Zoom;
            float f = e.Delta > 0 ? 1.25f : 0.8f;
            float newS = Math.Max(0.02f, Math.Min(16f, oldS * f));
            var r = DestRect();
            float ix = (e.X - r.X) / oldS, iy = (e.Y - r.Y) / oldS;     // image point under the cursor stays put
            zoom = newS;
            pan = new PointF(e.X - ix * newS - (ClientSize.Width - image.Width * newS) / 2,
                             e.Y - iy * newS - (ClientSize.Height - image.Height * newS) / 2);
            Invalidate();
            OnZoomChanged();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (zoom == 0) OnZoomChanged();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (e.Button == MouseButtons.Left) { dragging = true; dragStart = e.Location; panStart = pan; Capture = true; }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging) { pan = new PointF(panStart.X + e.X - dragStart.X, panStart.Y + e.Y - dragStart.Y); Invalidate(); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            dragging = false;
            Capture = false;
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e) { Fit(); }
    }

    public sealed class MainForm : Form
    {
        private readonly ToolStrip tool = new ToolStrip();
        private readonly ToolStripButton btnComposite, btnSingle, btnFull;
        private bool zoomToActualAfterRender;
        private readonly SplitContainer split = new SplitContainer();
        private readonly LayerTree tree = new LayerTree();
        private readonly ImageList thumbs = new ImageList();
        private readonly ImageList eyes = new ImageList();      // state images: 0 = hidden, 1 = visible
        private const int EyeSize = 30;
        private readonly CanvasView view = new CanvasView();
        private readonly StatusStrip status = new StatusStrip();
        private readonly ToolStripStatusLabel statusLabel = new ToolStripStatusLabel();
        private readonly ToolStripProgressBar progress = new ToolStripProgressBar();
        private const int ThumbSize = 64;

        private ProcreateDocument doc;
        private Renderer renderer;
        private readonly Dictionary<Layer, TreeNode> nodes = new Dictionary<Layer, TreeNode>();
        private bool suppressCheck, loaded, rendering, pending;
        private int loadSeq;
        private long lastRenderMs, loadMs;
        private Canvas lastCanvas;

        /// <summary>Invoked on the UI thread after each finished render (used by the --gui-test self check).</summary>
        public Action AfterRender;

        public MainForm(string path)
        {
            Text = "Procreate Viewer";
            Size = new Size(1280, 840);
            StartPosition = FormStartPosition.CenterScreen;
            AllowDrop = true;
            KeyPreview = true;
            Font = Theme.UiFont();
            BackColor = Theme.Window;
            ForeColor = Theme.Text;
            HandleCreated += (s, e) => Theme.DarkTitleBar(this);

            thumbs.ColorDepth = ColorDepth.Depth32Bit;
            thumbs.ImageSize = new Size(ThumbSize, ThumbSize);
            thumbs.Images.Add("blank", new Bitmap(ThumbSize, ThumbSize));
            thumbs.Images.Add("group", GroupIcon());

            // visibility toggles drawn as big eye icons (Photoshop style) instead of tiny check boxes
            eyes.ColorDepth = ColorDepth.Depth32Bit;
            eyes.ImageSize = new Size(EyeSize, EyeSize);
            eyes.Images.Add(new Bitmap(EyeSize, EyeSize));   // native state index 0 means "no image", so index 0 is a placeholder
            eyes.Images.Add(EyeIcon(false));                  // state 1 = hidden
            eyes.Images.Add(EyeIcon(true));                   // state 2 = visible
            tree.Eyes = eyes;
            tree.ImageList = thumbs;
            tree.CheckBoxes = false;
            tree.HideSelection = false;
            tree.ShowLines = false;
            tree.ItemHeight = ThumbSize + 8;
            tree.Dock = DockStyle.Fill;
            tree.FullRowSelect = true;
            tree.BorderStyle = BorderStyle.None;
            tree.BackColor = Theme.Panel;
            tree.ForeColor = Theme.Text;
            tree.LineColor = Theme.Border;
            tree.HandleCreated += (s, e) => Theme.DarkNative(tree);
            tree.EyeClicked += n => { if (!suppressCheck && btnComposite.Checked) RequestRender(); };
            tree.AfterSelect += (s, e) => { if (btnSingle.Checked) RequestRender(); };

            view.Dock = DockStyle.Fill;
            split.Dock = DockStyle.Fill;
            split.BackColor = Theme.Border;          // the splitter bar
            split.Panel1.BackColor = Theme.Panel;
            split.Panel2.BackColor = Theme.Canvas;
            split.SplitterWidth = 3;
            split.Panel1.Controls.Add(tree);
            split.Panel2.Controls.Add(view);

            var barRenderer = new DarkRenderer();
            tool.Renderer = barRenderer;
            status.Renderer = barRenderer;
            tool.BackColor = status.BackColor = Theme.Window;
            tool.ForeColor = status.ForeColor = Theme.Text;
            tool.Padding = new Padding(6, 3, 6, 3);
            status.SizingGrip = false;
            tool.GripStyle = ToolStripGripStyle.Hidden;
            tool.Items.Add(new ToolStripButton("開く…", null, (s, e) => OpenDialog()));
            tool.Items.Add(new ToolStripSeparator());
            btnComposite = new ToolStripButton("合成表示") { CheckOnClick = false, Checked = true };
            btnSingle = new ToolStripButton("選択レイヤーのみ") { CheckOnClick = false };
            btnComposite.Click += (s, e) => { btnComposite.Checked = true; btnSingle.Checked = false; RequestRender(); };
            btnSingle.Click += (s, e) => { btnSingle.Checked = true; btnComposite.Checked = false; RequestRender(); };
            tool.Items.Add(btnComposite);
            tool.Items.Add(btnSingle);
            tool.Items.Add(new ToolStripSeparator());
            tool.Items.Add(new ToolStripButton("全て表示", null, (s, e) => SetAll(true)));
            tool.Items.Add(new ToolStripButton("全て非表示", null, (s, e) => SetAll(false)));
            tool.Items.Add(new ToolStripButton("保存時の状態", null, (s, e) => ResetVisibility()));
            tool.Items.Add(new ToolStripSeparator());
            tool.Items.Add(new ToolStripButton("フィット", null, (s, e) => view.Fit()));
            tool.Items.Add(new ToolStripButton("100%", null, (s, e) => ZoomActual()));
            btnFull = new ToolStripButton("フルサイズ") { CheckOnClick = true, Checked = Prefs.Get("FullSize", true), ToolTipText = "原寸で合成して表示する（オフにすると長辺 2048px のプレビューで軽く表示）" };
            btnFull.Click += (s, e) =>
            {
                Prefs.Set("FullSize", btnFull.Checked);
                if (!btnFull.Checked && renderer != null) { renderer.ClearFullCache(); GC.Collect(); }
                RequestRender();
            };
            tool.Items.Add(btnFull);
            tool.Items.Add(new ToolStripSeparator());
            tool.Items.Add(new ToolStripButton("表示をPNG保存…", null, (s, e) => SaveView()));
            tool.Items.Add(new ToolStripButton("全レイヤーをPNG書き出し…", null, (s, e) => ExportAll()));
            tool.Items.Add(new ToolStripButton("PSD書き出し…", null, (s, e) => ExportPsd()));

            statusLabel.Spring = true;
            statusLabel.ForeColor = Theme.TextDim;
            statusLabel.TextAlign = ContentAlignment.MiddleLeft;
            statusLabel.Text = "ファイルを開くか、ここに .procreate をドロップしてください";
            progress.Visible = false;
            status.Items.Add(statusLabel);
            status.Items.Add(progress);

            Controls.Add(split);
            Controls.Add(tool);
            Controls.Add(status);
            split.SplitterDistance = 360;

            view.ZoomChanged += UpdateStatus;
            DragEnter += (s, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
            DragDrop += (s, e) => { var files = e.Data.GetData(DataFormats.FileDrop) as string[]; if (files != null && files.Length > 0) OpenFile(files[0]); };
            KeyDown += OnKey;

            // the handle does not exist yet in the constructor, so defer opening until the window is shown
            if (path != null) Shown += (s, e) => OpenFile(path);

            // lifecycle trace: explains "the window vanished" reports
            Shown += (s, e) => Trace.Log("form shown bounds=" + Bounds + " state=" + WindowState);
            FormClosing += (s, e) => Trace.Log("form closing reason=" + e.CloseReason + " cancel=" + e.Cancel);
            FormClosed += (s, e) => Trace.Log("form closed reason=" + e.CloseReason);
            Deactivate += (s, e) => Trace.Log("form deactivated");
            VisibleChanged += (s, e) => Trace.Log("form visible=" + Visible);
            SizeChanged += (s, e) => Trace.Log("form size=" + Size + " state=" + WindowState);
            Application.ApplicationExit += (s, e) => Trace.Log("application exit");
            AppDomain.CurrentDomain.ProcessExit += (s, e) => Trace.Log("process exit");
        }

        /// <summary>Eye icon on a rounded dark tile: open and bright when visible, closed and dim when hidden.</summary>
        private static Bitmap EyeIcon(bool visible)
        {
            var b = new Bitmap(EyeSize, EyeSize, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (var tile = Theme.Rounded(new Rectangle(1, 1, EyeSize - 3, EyeSize - 3), 6))
                using (var bg = new SolidBrush(visible ? Theme.Hover : Theme.Panel))
                using (var edge = new Pen(Theme.Border))
                {
                    g.FillPath(bg, tile);
                    g.DrawPath(edge, tile);
                }
                float cx = EyeSize / 2f, cy = EyeSize / 2f, w = EyeSize * 0.66f, h = EyeSize * 0.40f;
                Color ink = visible ? Theme.Text : Color.FromArgb(0x6a, 0x6a, 0x70);
                using (var pen = new Pen(ink, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                using (var fill = new SolidBrush(ink))
                {
                    g.DrawEllipse(pen, cx - w / 2, cy - h / 2, w, h);                        // eye outline
                    g.FillEllipse(fill, cx - h * 0.36f, cy - h * 0.36f, h * 0.72f, h * 0.72f); // iris
                    if (!visible)
                        using (var slash = new Pen(Theme.Panel, 5f)) using (var slash2 = new Pen(ink, 2f))
                        {
                            g.DrawLine(slash, cx - w * 0.42f, cy + h * 0.75f, cx + w * 0.42f, cy - h * 0.75f);   // gap
                            g.DrawLine(slash2, cx - w * 0.42f, cy + h * 0.75f, cx + w * 0.42f, cy - h * 0.75f);  // the slash itself
                        }
                }
            }
            return b;
        }

        private static Bitmap GroupIcon()
        {
            var b = new Bitmap(ThumbSize, ThumbSize);
            using (var g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var br = new SolidBrush(Theme.Folder))
                using (var tab = Theme.Rounded(new Rectangle(10, 16, 22, 10), 3))
                using (var body = Theme.Rounded(new Rectangle(10, 22, 44, 28), 4))
                {
                    g.FillPath(br, tab);
                    g.FillPath(br, body);
                }
            }
            return b;
        }

        private void OnKey(object sender, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.O) { OpenDialog(); e.Handled = true; }
            else if (e.KeyCode == Keys.F && !tree.Focused) { view.Fit(); e.Handled = true; }
            else if (e.KeyCode == Keys.D1 && !tree.Focused) { ZoomActual(); e.Handled = true; }
            else if (e.KeyCode == Keys.Space && tree.Focused && tree.SelectedNode != null)
            {
                tree.SetOn(tree.SelectedNode, !tree.IsOn(tree.SelectedNode));
                if (btnComposite.Checked) RequestRender();
                e.Handled = true; e.SuppressKeyPress = true;
            }
        }

        /// <summary>True 1:1 pixels: switch to full-size rendering if needed, then zoom to 100%.</summary>
        private void ZoomActual()
        {
            if (doc == null) return;
            if (!btnFull.Checked)
            {
                btnFull.Checked = true;
                zoomToActualAfterRender = true;
                RequestRender();
            }
            else view.SetZoom(1f);
        }

        /// <summary>Test hook: full-size composite view.</summary>
        public void ShowFullSize()
        {
            btnComposite.PerformClick();
            btnFull.Checked = true;
            RequestRender();
        }

        private void UI(Action a)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(a); }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        // ---------------- document -----------------
        private void OpenDialog()
        {
            using (var d = new OpenFileDialog { Filter = "Procreate (*.procreate)|*.procreate|All files (*.*)|*.*" })
                if (d.ShowDialog(this) == DialogResult.OK) OpenFile(d.FileName);
        }

        public void OpenFile(string path)
        {
            int seq = ++loadSeq;
            loaded = false;
            var sw = Stopwatch.StartNew();
            Text = Path.GetFileName(path) + " - Procreate Viewer";
            statusLabel.Text = "開いています… " + path;
            try { Trace.Log("open " + path + " attrs=" + File.GetAttributes(path)); } catch (Exception ex) { Trace.Log("open " + path + " (attrs failed: " + ex.Message + ")"); }
            // parse off the UI thread: reading a cloud placeholder can block for a long time
            Task.Run(() =>
            {
                ProcreateDocument d;
                byte[] png;
                try
                {
                    d = new ProcreateDocument(path);
                    png = d.ThumbnailPng();
                }
                catch (Exception ex)
                {
                    Trace.Log("open failed: " + ex);
                    UI(() =>
                    {
                        if (seq != loadSeq) return;
                        statusLabel.Text = "開けません: " + ex.Message;
                        MessageBox.Show(this, path + "\n\n" + ex.Message, "開けません", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    });
                    return;
                }
                Trace.Log(string.Format("parsed {0}x{1} layers={2} orient={3} in {4} ms", d.Width, d.Height, d.LayerCount(), d.Orientation, sw.ElapsedMilliseconds));
                UI(() =>
                {
                    if (seq != loadSeq) { d.Dispose(); return; }
                    var old = doc;
                    doc = d;
                    renderer = new Renderer(doc, Renderer.PreviewMax);
                    lastCanvas = null;
                    BuildTree();
                    if (png != null)
                    {
                        try
                        {
                            using (var ms = new MemoryStream(png))
                            using (var img = Image.FromStream(ms))
                                view.SetImage(new Bitmap(img));
                            view.Fit();
                        }
                        catch (Exception ex) { Trace.Log("thumbnail png failed: " + ex.Message); }
                    }
                    progress.Visible = true;
                    progress.Maximum = Math.Max(1, doc.LayerCount());
                    progress.Value = 0;
                    statusLabel.Text = "レイヤー展開中…";
                    if (old != null) old.Dispose();
                    LoadLayers(seq, sw);
                });
            });
        }

        private void LoadLayers(int seq, Stopwatch sw)
        {
            var r = renderer;
            bool full = btnFull.Checked;
            Task.Run(() =>
            {
                try
                {
                    r.LoadAll((n, total) => UI(() =>
                    {
                        if (seq != loadSeq) return;
                        progress.Value = Math.Min(progress.Maximum, n);
                        statusLabel.Text = string.Format("レイヤー展開中 {0}/{1}", n, total);
                    }), full);
                    long ms = sw.ElapsedMilliseconds;
                    Trace.Log("layers decoded in " + ms + " ms" + (full ? " (full cache " + (r.FullCacheBytes >> 20) + " MB, overflow=" + r.FullCacheOverflow + ")" : ""));
                    UI(() =>
                    {
                        if (seq != loadSeq) return;
                        loaded = true;
                        loadMs = ms;
                        progress.Visible = false;
                        if (full && r.FullCacheOverflow)
                        {
                            // too big for the memory budget: fall back to the light preview mode for this file
                            btnFull.Checked = false;
                            r.ClearFullCache();
                            GC.Collect();
                        }
                        UpdateStatus();
                        RequestRender();
                    });
                    foreach (var l in r.Doc.AllLayers())
                    {
                        if (seq != loadSeq) break;
                        if (l.IsGroup) continue;
                        Bitmap t = MakeThumb(r, l);
                        var layer = l;
                        UI(() =>
                        {
                            TreeNode n;
                            if (seq != loadSeq || !nodes.TryGetValue(layer, out n)) { t.Dispose(); return; }
                            string key = layer.Uuid;
                            thumbs.Images.Add(key, t);
                            n.ImageKey = key;
                            n.SelectedImageKey = key;
                        });
                    }
                }
                catch (Exception ex)
                {
                    Trace.Log("load failed: " + ex);
                    UI(() => { statusLabel.Text = "読み込みエラー: " + ex.Message; progress.Visible = false; });
                }
            });
        }

        private void BuildTree()
        {
            tree.BeginUpdate();
            suppressCheck = true;
            tree.Nodes.Clear();
            tree.ResetStates();
            nodes.Clear();
            while (thumbs.Images.Count > 2) thumbs.Images.RemoveAt(thumbs.Images.Count - 1);
            AddNodes(tree.Nodes, doc.Layers);
            tree.ExpandAll();
            if (tree.Nodes.Count > 0) tree.Nodes[0].EnsureVisible();
            suppressCheck = false;
            tree.EndUpdate();
            tree.ApplyAll();
        }

        private void AddNodes(TreeNodeCollection col, List<Layer> layers)
        {
            foreach (var l in layers)
            {
                var n = new TreeNode(NodeText(l)) { Tag = l };
                tree.SetOn(n, !l.Hidden);          // recorded now, written to the control by ApplyAll
                n.ImageKey = n.SelectedImageKey = l.IsGroup ? "group" : "blank";
                col.Add(n);
                nodes[l] = n;
                if (l.IsGroup) AddNodes(n.Nodes, l.Children);
            }
        }

        private static string NodeText(Layer l)
        {
            int pct = (int)Math.Round(l.Opacity * 100);
            return l.IsGroup ? string.Format("{0}   [グループ {1}%]", l.Name, pct)
                             : string.Format("{0}   [{1} {2}%]", l.Name, l.BlendName, pct);
        }

        /// <summary>Layer thumbnail: the layer's own content (bounding box of painted tiles) fitted into the cell,
        /// oriented like the document, over a checkerboard.  A whole-canvas miniature would make small layers invisible.</summary>
        private static Bitmap MakeThumb(Renderer r, Layer l)
        {
            var px = r.Preview(l);
            var doc = r.Doc;
            var thumb = new Bitmap(ThumbSize, ThumbSize, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(thumb))
            {
                g.Clear(Color.Transparent);
                using (var light = new SolidBrush(Theme.CheckerLight))
                using (var dark = new SolidBrush(Theme.CheckerDark))
                    for (int y = 0; y < ThumbSize; y += 8)
                        for (int x = 0; x < ThumbSize; x += 8)
                            g.FillRectangle(((x + y) / 8) % 2 == 0 ? light : dark, x, y, 8, 8);
                if (!px.IsEmpty)
                {
                    using (var lb = Compositor.LayerBitmap(px))
                    {
                        if (doc.FlipH && doc.FlipV) lb.RotateFlip(RotateFlipType.RotateNoneFlipXY);
                        else if (doc.FlipH) lb.RotateFlip(RotateFlipType.RotateNoneFlipX);
                        else if (doc.FlipV) lb.RotateFlip(RotateFlipType.RotateNoneFlipY);
                        if (doc.Orientation == 2) lb.RotateFlip(RotateFlipType.Rotate180FlipNone);
                        else if (doc.Orientation == 3) lb.RotateFlip(RotateFlipType.Rotate270FlipNone);
                        else if (doc.Orientation == 4) lb.RotateFlip(RotateFlipType.Rotate90FlipNone);
                        float s = Math.Min((float)(ThumbSize - 2) / lb.Width, (float)(ThumbSize - 2) / lb.Height);
                        if (s > 1) s = 1;
                        int w = Math.Max(1, (int)Math.Round(lb.Width * s)), h = Math.Max(1, (int)Math.Round(lb.Height * s));
                        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                        g.PixelOffsetMode = PixelOffsetMode.Half;
                        g.DrawImage(lb, new Rectangle((ThumbSize - w) / 2, (ThumbSize - h) / 2, w, h), new Rectangle(0, 0, lb.Width, lb.Height), GraphicsUnit.Pixel);
                    }
                }
                using (var pen = new Pen(Theme.Border)) g.DrawRectangle(pen, 0, 0, ThumbSize - 1, ThumbSize - 1);
            }
            return thumb;
        }

        private void UpdateStatus()
        {
            if (doc == null) return;
            string mode = btnFull.Checked ? string.Format("フルサイズ (キャッシュ {0} MB)", renderer.FullCacheBytes >> 20) : string.Format("プレビュー {0}%", (int)Math.Round(renderer.Scale * 100));
            string zoomText = "";
            if (view.Image != null && doc.DisplayWidth > 0)
                zoomText = string.Format("   表示 {0:0.#}%", view.Zoom * view.Image.Width / doc.DisplayWidth * 100);
            statusLabel.Text = string.Format("{0}   {1}x{2} px   {3} dpi   レイヤー {4}   向き {5}{6}{7}   {8}{9}   読込 {10} ms / 描画 {11} ms",
                Path.GetFileName(doc.Path), doc.DisplayWidth, doc.DisplayHeight, doc.Dpi > 0 ? doc.Dpi.ToString("0") : "?", doc.LayerCount(),
                doc.Orientation, doc.FlipH ? " 左右反転" : "", doc.FlipV ? " 上下反転" : "", mode, zoomText, loadMs, lastRenderMs);
        }

        // ---------------- visibility -----------------
        /// <summary>Test hook: toggle the first visible layer off (what a click on its checkbox does).</summary>
        public void ToggleFirstLayer()
        {
            foreach (var kv in nodes)
                if (!kv.Key.IsGroup && tree.IsOn(kv.Value)) { tree.SetOn(kv.Value, false); RequestRender(); return; }
        }

        /// <summary>Test hook: select the first layer and switch to single-layer view.</summary>
        public void ShowFirstLayerAlone()
        {
            foreach (var kv in nodes)
                if (!kv.Key.IsGroup) { tree.SelectedNode = kv.Value; break; }
            btnSingle.PerformClick();
        }

        private HashSet<Layer> VisibleSet()
        {
            var set = new HashSet<Layer>();
            foreach (var kv in nodes) if (tree.IsOn(kv.Value)) set.Add(kv.Key);
            return set;
        }

        private void SetAll(bool on)
        {
            if (doc == null) return;
            foreach (var kv in nodes) tree.SetOn(kv.Value, on);
            RequestRender();
        }

        private void ResetVisibility()
        {
            if (doc == null) return;
            foreach (var kv in nodes) tree.SetOn(kv.Value, !kv.Key.Hidden);
            RequestRender();
        }

        // ---------------- rendering -----------------
        private void RequestRender()
        {
            if (doc == null || !loaded) return;
            if (rendering) { pending = true; return; }
            StartRender();
        }

        private void StartRender()
        {
            rendering = true;
            pending = false;
            bool single = btnSingle.Checked;
            bool preview = !btnFull.Checked;
            Layer sel = tree.SelectedNode != null ? tree.SelectedNode.Tag as Layer : null;
            var vis = VisibleSet();
            var r = renderer;
            int seq = loadSeq;
            Cursor = Cursors.AppStarting;
            if (!preview) statusLabel.Text = "フルサイズで合成中…";
            Task.Run(() =>
            {
                Bitmap bmp = null;
                Canvas c = null;
                string err = null;
                long ms = 0;
                try
                {
                    var sw = Stopwatch.StartNew();
                    c = (single && sel != null) ? r.Single(sel, preview) : r.Composite(l => vis.Contains(l), true, preview);
                    bmp = Compositor.ToBitmap(c, r.Doc, true, false);
                    ms = sw.ElapsedMilliseconds;
                    Trace.Log(string.Format("rendered {0}{1} {2}x{3} in {4} ms", single ? "single" : "composite", preview ? "" : " full", bmp.Width, bmp.Height, ms));
                }
                catch (Exception ex) { err = ex.Message; Trace.Log("render failed: " + ex); }
                UI(() =>
                {
                    rendering = false;
                    Cursor = Cursors.Default;
                    if (seq == loadSeq && bmp != null)
                    {
                        view.SetImage(bmp);
                        lastCanvas = c;
                        lastRenderMs = ms;
                        if (zoomToActualAfterRender) { zoomToActualAfterRender = false; view.SetZoom(1f); }
                        UpdateStatus();
                    }
                    else if (bmp != null) bmp.Dispose();
                    if (err != null) statusLabel.Text = "描画エラー: " + err;
                    if (pending) StartRender();
                    else if (AfterRender != null) AfterRender();
                });
            });
        }

        // ---------------- export -----------------
        private void SaveView()
        {
            if (lastCanvas == null || doc == null) return;
            using (var d = new SaveFileDialog { Filter = "PNG|*.png", InitialDirectory = Path.GetDirectoryName(doc.Path), FileName = Path.GetFileNameWithoutExtension(doc.Path) + ".png" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                using (var bmp = Compositor.ToBitmap(lastCanvas, doc, true, true)) bmp.Save(d.FileName, ImageFormat.Png);
                statusLabel.Text = "保存しました: " + d.FileName;
            }
        }

        private void ExportAll()
        {
            if (doc == null) return;
            // a save dialog pre-filled with "<document>_layers": the chosen name becomes the output folder
            using (var d = new SaveFileDialog
            {
                Title = "書き出し先フォルダ（この名前のフォルダに composite.png と各レイヤーの PNG を作ります）",
                Filter = "フォルダ|*.",
                InitialDirectory = Path.GetDirectoryName(doc.Path),
                FileName = Path.GetFileNameWithoutExtension(doc.Path) + "_layers",
                CheckFileExists = false,
                OverwritePrompt = false,
                AddExtension = false,
            })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                string dir = d.FileName;
                if (dir.EndsWith(".")) dir = dir.TrimEnd('.');
                var r = renderer;
                progress.Visible = true;
                progress.Value = 0;
                Task.Run(() =>
                {
                    try
                    {
                        int n = r.ExportAll(dir, (done, total) => UI(() => { progress.Maximum = total; progress.Value = Math.Min(total, done); statusLabel.Text = string.Format("書き出し中 {0}/{1}", done, total); }));
                        UI(() => { progress.Visible = false; statusLabel.Text = string.Format("{0} 枚を書き出しました: {1}", n, dir); });
                    }
                    catch (Exception ex) { UI(() => { progress.Visible = false; statusLabel.Text = "書き出しエラー: " + ex.Message; }); }
                });
            }
        }

        private void ExportPsd()
        {
            if (doc == null) return;
            using (var d = new SaveFileDialog { Filter = "Photoshop|*.psd", InitialDirectory = Path.GetDirectoryName(doc.Path), FileName = Path.GetFileNameWithoutExtension(doc.Path) + ".psd" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                string file = d.FileName;
                var r = renderer;
                progress.Visible = true;
                progress.Value = 0;
                Task.Run(() =>
                {
                    try
                    {
                        var sw = Stopwatch.StartNew();
                        PsdWriter.Write(r, file, (done, total) => UI(() => { progress.Maximum = total; progress.Value = Math.Min(total, done); statusLabel.Text = string.Format("PSD 書き出し中 {0}/{1}", done, total); }));
                        long size = new FileInfo(file).Length;
                        UI(() => { progress.Visible = false; statusLabel.Text = string.Format("PSD を書き出しました: {0} ({1} MB, {2:0.0} 秒)", file, size >> 20, sw.ElapsedMilliseconds / 1000.0); });
                    }
                    catch (Exception ex) { Trace.Log("psd failed: " + ex); UI(() => { progress.Visible = false; statusLabel.Text = "PSD 書き出しエラー: " + ex.Message; }); }
                });
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            loadSeq++;
            if (doc != null) doc.Dispose();
        }
    }
}
