using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

// Procreate thumbnail provider for Windows Explorer.
//  * registered as the IThumbnailProvider shell extension for .procreate (reads QuickLook/Thumbnail.png
//    out of the zip container), and
//  * optionally registered as the ThumbnailProvider of the iCloud Drive sync root, because Explorer only
//    asks the sync root's provider for cloud placeholder files.  In that role it handles .procreate itself
//    and delegates every other extension to the regular thumbnail handler registered for that extension.
namespace ProcreateThumb
{
    public enum WTS_ALPHATYPE { WTSAT_UNKNOWN = 0, WTSAT_RGB = 1, WTSAT_ARGB = 2 }

    [ComImport, Guid("e357fccd-a995-4576-b01f-234630154e96"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IThumbnailProvider
    {
        void GetThumbnail(uint cx, out IntPtr phbmp, out WTS_ALPHATYPE pdwAlpha);
    }

    [ComImport, Guid("b824b49d-22ac-4161-ac8a-9916e8fa3f7f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IInitializeWithStream
    {
        void Initialize(IStream pstream, uint grfMode);
    }

    [ComImport, Guid("b7d14566-0509-4cce-a71f-0a554233bd9b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IInitializeWithFile
    {
        void Initialize([MarshalAs(UnmanagedType.LPWStr)] string pszFilePath, uint grfMode);
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdnName, out IntPtr ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [ComImport, Guid("7f73be3f-fb79-493c-a6c7-7ee14e245841"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IInitializeWithItem
    {
        void Initialize(IShellItem psi, uint grfMode);
    }

    // Read-only seekable Stream over a COM IStream (Explorer hands the file as an IStream).
    internal class ComStream : Stream
    {
        private readonly IStream s;
        public ComStream(IStream s) { this.s = s; }
        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return true; } }
        public override bool CanWrite { get { return false; } }
        public override long Length
        {
            get { System.Runtime.InteropServices.ComTypes.STATSTG st; s.Stat(out st, 1); return st.cbSize; }
        }
        public override long Position
        {
            get { return Seek(0, SeekOrigin.Current); }
            set { Seek(value, SeekOrigin.Begin); }
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            byte[] tmp = offset == 0 ? buffer : new byte[count];
            IntPtr pRead = Marshal.AllocHGlobal(4);
            try
            {
                s.Read(tmp, count, pRead);
                int n = Marshal.ReadInt32(pRead);
                if (offset != 0) Array.Copy(tmp, 0, buffer, offset, n);
                return n;
            }
            finally { Marshal.FreeHGlobal(pRead); }
        }
        public override long Seek(long offset, SeekOrigin origin)
        {
            IntPtr pPos = Marshal.AllocHGlobal(8);
            try { s.Seek(offset, (int)origin, pPos); return Marshal.ReadInt64(pPos); }
            finally { Marshal.FreeHGlobal(pPos); }
        }
        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
    }

    [ComVisible(true), Guid("7C1E7A6B-2F0D-4B39-9C55-5B1A2D3E4F60"), ClassInterface(ClassInterfaceType.None)]
    [ProgId("ProcreateThumb.Provider")]
    public class Provider : IThumbnailProvider, IInitializeWithStream, IInitializeWithFile, IInitializeWithItem
    {
        public const string ClsidString = "{7C1E7A6B-2F0D-4B39-9C55-5B1A2D3E4F60}";
        private const int E_FAIL = unchecked((int)0x80004005);
        private const uint SIGDN_FILESYSPATH = 0x80058000;
        private const uint STGM_READ = 0, STGM_SHARE_DENY_NONE = 0x40;

        private Stream source;      // set by IInitializeWithStream / IInitializeWithFile
        private string path;        // set by IInitializeWithFile / IInitializeWithItem

        private static readonly string LogPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow", "procreate_thumb_log.txt");

        static void Log(string s)
        {
            try { if (File.Exists(LogPath)) File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss ") + s + Environment.NewLine); }
            catch { }
        }

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        static extern int AssocQueryString(int flags, int str, string assoc, string extra, StringBuilder outbuf, ref int outlen);
        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        static extern void SHCreateStreamOnFileEx(string file, uint grfMode, uint attrs, bool create, IStream template, out IStream s);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, out IShellItem item);

        // ---- initialisation --------------------------------------------------------
        public void Initialize(IStream pstream, uint grfMode)
        {
            Log("init stream");
            source = new ComStream(pstream);
        }

        public void Initialize(string pszFilePath, uint grfMode)
        {
            Log("init file " + pszFilePath);
            path = pszFilePath;
        }

        public void Initialize(IShellItem psi, uint grfMode)
        {
            IntPtr p;
            psi.GetDisplayName(SIGDN_FILESYSPATH, out p);
            try { path = Marshal.PtrToStringUni(p); }
            finally { Marshal.FreeCoTaskMem(p); }
            Log("init item " + path);
        }

        // ---- IThumbnailProvider ----------------------------------------------------
        public void GetThumbnail(uint cx, out IntPtr phbmp, out WTS_ALPHATYPE pdwAlpha)
        {
            try { GetThumbnailCore(cx, out phbmp, out pdwAlpha); Log("ok cx=" + cx + " " + path); }
            catch (Exception ex) { Log("FAIL " + path + " " + ex); throw; }
        }

        void GetThumbnailCore(uint cx, out IntPtr phbmp, out WTS_ALPHATYPE pdwAlpha)
        {
            if (source == null && path == null) throw new COMException("not initialized", E_FAIL);
            bool isProcreate = source != null || path.EndsWith(".procreate", StringComparison.OrdinalIgnoreCase);
            if (!isProcreate)
            {
                // sync-root role: hand every other file type to its own registered thumbnail handler
                if (!Delegate(path, cx, out phbmp, out pdwAlpha)) throw new COMException("no handler for " + Path.GetExtension(path), E_FAIL);
                return;
            }
            Stream s = source;
            bool own = false;
            if (s == null)
            {
                s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                own = true;
            }
            try { ProcreateThumbnail(s, cx, out phbmp, out pdwAlpha); }
            finally { if (own) s.Dispose(); }
        }

        static void ProcreateThumbnail(Stream s, uint cx, out IntPtr phbmp, out WTS_ALPHATYPE pdwAlpha)
        {
            phbmp = IntPtr.Zero;
            pdwAlpha = WTS_ALPHATYPE.WTSAT_ARGB;
            using (ZipArchive zip = new ZipArchive(s, ZipArchiveMode.Read, true))
            {
                ZipArchiveEntry e = zip.GetEntry("QuickLook/Thumbnail.png");
                if (e == null)
                    foreach (ZipArchiveEntry x in zip.Entries)
                        if (x.FullName.EndsWith("Thumbnail.png", StringComparison.OrdinalIgnoreCase)) { e = x; break; }
                if (e == null) throw new COMException("no thumbnail", E_FAIL);
                byte[] png;
                using (Stream es = e.Open()) using (MemoryStream ms = new MemoryStream()) { es.CopyTo(ms); png = ms.ToArray(); }
                using (MemoryStream ms = new MemoryStream(png))
                using (Bitmap src = new Bitmap(ms))
                {
                    int w = src.Width, h = src.Height;
                    int size = (int)cx;
                    double scale = Math.Min((double)size / w, (double)size / h);
                    if (scale > 1) scale = 1;
                    int tw = Math.Max(1, (int)Math.Round(w * scale));
                    int th = Math.Max(1, (int)Math.Round(h * scale));
                    using (Bitmap dst = new Bitmap(tw, th, PixelFormat.Format32bppArgb))
                    {
                        using (Graphics g = Graphics.FromImage(dst))
                        {
                            g.Clear(Color.Transparent);
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                            g.CompositingMode = CompositingMode.SourceOver;
                            g.DrawImage(src, new Rectangle(0, 0, tw, th), new Rectangle(0, 0, w, h), GraphicsUnit.Pixel);
                        }
                        phbmp = dst.GetHbitmap();
                    }
                }
            }
        }

        // Find the thumbnail handler registered for the file's extension and run it on the file ourselves
        // (Explorer refuses to do that for cloud placeholder files, which is why we are here).
        static bool Delegate(string file, uint cx, out IntPtr phbmp, out WTS_ALPHATYPE alpha)
        {
            phbmp = IntPtr.Zero;
            alpha = WTS_ALPHATYPE.WTSAT_UNKNOWN;
            if (Directory.Exists(file)) return false;
            string ext = Path.GetExtension(file);
            if (string.IsNullOrEmpty(ext)) return false;
            StringBuilder sb = new StringBuilder(64);
            int len = 64;
            int hr = AssocQueryString(0, 16 /*ASSOCSTR_SHELLEXTENSION*/, ext, "{E357FCCD-A995-4576-B01F-234630154E96}", sb, ref len);
            if (hr != 0) { Log("no thumbnail handler for " + ext); return false; }
            Guid clsid = new Guid(sb.ToString());
            if (clsid == new Guid(ClsidString)) return false;
            object handler = Activator.CreateInstance(Type.GetTypeFromCLSID(clsid, true));
            IInitializeWithStream iws = handler as IInitializeWithStream;
            if (iws != null)
            {
                IStream stm;
                SHCreateStreamOnFileEx(file, STGM_READ | STGM_SHARE_DENY_NONE, 0, false, null, out stm);
                iws.Initialize(stm, STGM_READ);
            }
            else
            {
                IInitializeWithFile iwf = handler as IInitializeWithFile;
                if (iwf != null) iwf.Initialize(file, STGM_READ);
                else
                {
                    IInitializeWithItem iwi = handler as IInitializeWithItem;
                    if (iwi == null) { Log("handler " + clsid + " has no init interface"); return false; }
                    Guid iid = typeof(IShellItem).GUID;
                    IShellItem item;
                    SHCreateItemFromParsingName(file, IntPtr.Zero, ref iid, out item);
                    iwi.Initialize(item, STGM_READ);
                }
            }
            ((IThumbnailProvider)handler).GetThumbnail(cx, out phbmp, out alpha);
            Log("delegated " + ext + " -> " + clsid);
            return true;
        }
    }
}
