using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ProcreateViewer
{
    /// <summary>Dark, flat look: black canvas, near-black panels, light text, one accent colour.</summary>
    public static class Theme
    {
        public static readonly Color Window = Color.FromArgb(0x16, 0x16, 0x16);      // form / bars
        public static readonly Color Panel = Color.FromArgb(0x1c, 0x1c, 0x1e);       // layer list
        public static readonly Color Canvas = Color.Black;                            // around the picture
        public static readonly Color Border = Color.FromArgb(0x30, 0x30, 0x33);
        public static readonly Color Hover = Color.FromArgb(0x2c, 0x2c, 0x30);
        public static readonly Color Pressed = Color.FromArgb(0x3a, 0x3a, 0x40);
        public static readonly Color Accent = Color.FromArgb(0x4c, 0xc2, 0xff);
        public static readonly Color AccentFill = Color.FromArgb(0x12, 0x3a, 0x52);
        public static readonly Color Text = Color.FromArgb(0xe8, 0xe8, 0xe8);
        public static readonly Color TextDim = Color.FromArgb(0x9a, 0x9a, 0xa0);
        public static readonly Color CheckerLight = Color.FromArgb(0x3a, 0x3a, 0x3c);
        public static readonly Color CheckerDark = Color.FromArgb(0x2a, 0x2a, 0x2c);
        public static readonly Color Folder = Color.FromArgb(0xe0, 0xb8, 0x5c);

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr hWnd, string app, string idList);

        /// <summary>Dark title bar (Windows 10 1809+ / 11).  Call once the handle exists.</summary>
        public static void DarkTitleBar(Form f)
        {
            int on = 1;
            try
            {
                if (DwmSetWindowAttribute(f.Handle, 20, ref on, 4) != 0)     // DWMWA_USE_IMMERSIVE_DARK_MODE
                    DwmSetWindowAttribute(f.Handle, 19, ref on, 4);          // pre-20H1 value
            }
            catch { }
        }

        /// <summary>Explorer's dark theme for a native control (dark scroll bars, expander arrows).</summary>
        public static void DarkNative(Control c)
        {
            try { SetWindowTheme(c.Handle, "DarkMode_Explorer", null); } catch { }
        }

        public static Font UiFont()
        {
            try { return new Font(SystemFonts.MessageBoxFont.FontFamily, 9.75f); }
            catch { return SystemFonts.MessageBoxFont; }
        }

        public static GraphicsPath Rounded(Rectangle r, int radius)
        {
            int d = radius * 2;
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    /// <summary>Flat dark renderer for the tool bar and status bar.</summary>
    public sealed class DarkRenderer : ToolStripProfessionalRenderer
    {
        public DarkRenderer() : base(new ProfessionalColorTable { UseSystemColors = false }) { RoundedEdges = false; }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using (var b = new SolidBrush(Theme.Window)) e.Graphics.FillRectangle(b, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using (var p = new Pen(Theme.Border))
            {
                if (e.ToolStrip is StatusStrip) e.Graphics.DrawLine(p, 0, 0, e.ToolStrip.Width, 0);
                else e.Graphics.DrawLine(p, 0, e.ToolStrip.Height - 1, e.ToolStrip.Width, e.ToolStrip.Height - 1);
            }
        }

        protected override void OnRenderGrip(ToolStripGripRenderEventArgs e) { }

        protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs e)
        {
            var btn = e.Item as ToolStripButton;
            var r = new Rectangle(1, 2, e.Item.Width - 2, e.Item.Height - 4);
            Color fill;
            if (btn != null && btn.Checked) fill = Theme.AccentFill;
            else if (e.Item.Pressed) fill = Theme.Pressed;
            else if (e.Item.Selected) fill = Theme.Hover;
            else return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Theme.Rounded(r, 5))
            using (var b = new SolidBrush(fill))
                e.Graphics.FillPath(b, path);
            if (btn != null && btn.Checked)
                using (var path = Theme.Rounded(r, 5))
                using (var p = new Pen(Theme.Accent))
                    e.Graphics.DrawPath(p, path);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            var btn = e.Item as ToolStripButton;
            var lbl = e.Item as ToolStripStatusLabel;
            e.TextColor = !e.Item.Enabled ? Theme.TextDim : (btn != null && btn.Checked ? Theme.Accent : (lbl != null ? (lbl.IsLink ? Theme.Accent : Theme.TextDim) : Theme.Text));
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using (var p = new Pen(Theme.Border))
            {
                int x = e.Item.Width / 2;
                e.Graphics.DrawLine(p, x, 5, x, e.Item.Height - 5);
            }
        }
    }
}
