using System;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ProcreateViewer
{
    /// <summary>Lightweight run trace in %LOCALAPPDATA%\ProcreateViewer\trace.log (rewritten on every start).</summary>
    public static class Trace
    {
        static readonly object lk = new object();
        static string path;
        static readonly Stopwatch clock = Stopwatch.StartNew();

        public static void Start()
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProcreateViewer");
                Directory.CreateDirectory(dir);
                path = Path.Combine(dir, "trace.log");
                File.WriteAllText(path, "");
                Log("start " + Environment.CommandLine + " | session=" + Process.GetCurrentProcess().SessionId + " 64bit=" + Environment.Is64BitProcess + " os=" + Environment.OSVersion + " clr=" + Environment.Version);
            }
            catch { path = null; }
        }

        public static void Log(string s)
        {
            if (path == null) return;
            try { lock (lk) File.AppendAllText(path, clock.ElapsedMilliseconds.ToString().PadLeft(7) + " ms  " + s + Environment.NewLine); }
            catch { }
        }
    }

    /// <summary>Tiny per-user preferences (HKCU\Software\ProcreateViewer).</summary>
    public static class Prefs
    {
        private const string Key = @"Software\ProcreateViewer";

        public static bool Get(string name, bool def)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(Key))
                {
                    object v = k == null ? null : k.GetValue(name);
                    return v is int ? (int)v != 0 : def;
                }
            }
            catch { return def; }
        }

        public static void Set(string name, bool value)
        {
            try { using (var k = Registry.CurrentUser.CreateSubKey(Key)) k.SetValue(name, value ? 1 : 0, RegistryValueKind.DWord); }
            catch { }
        }

        public static string Get(string name, string def)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(Key))
                {
                    object v = k == null ? null : k.GetValue(name);
                    return v is string ? (string)v : def;
                }
            }
            catch { return def; }
        }

        public static void Set(string name, string value)
        {
            try { using (var k = Registry.CurrentUser.CreateSubKey(Key)) k.SetValue(name, value, RegistryValueKind.String); }
            catch { }
        }
    }

    static class Program
    {
        [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);
        [DllImport("shcore.dll")] static extern int SetProcessDpiAwareness(int value);
        [DllImport("shell32.dll")] static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);

        [STAThread]
        static void Main(string[] args)
        {
            Trace.Start();
            try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }   // never fight the foreground app for CPU
            if (args.Length > 0 && args[0] == "--gui-test")
            {
                GuiTest(args);
                return;
            }
            if (args.Length > 0 && args[0].StartsWith("--", StringComparison.Ordinal))
            {
                AttachConsole(-1);
                Console.WriteLine();
                Environment.ExitCode = Cli(args);
                return;
            }
            try { SetProcessDpiAwareness(1); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += (s, e) => ReportCrash(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => ReportCrash(e.ExceptionObject as Exception);
            try
            {
                Application.Run(new MainForm(args.Length > 0 ? args[0] : null));
                Trace.Log("Application.Run returned normally");
            }
            catch (Exception ex) { ReportCrash(ex); }
        }

        /// <summary>Never die silently: log to %LOCALAPPDATA%\ProcreateViewer\error.log and tell the user.</summary>
        static void ReportCrash(Exception ex)
        {
            Trace.Log("CRASH " + ex);
            string text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + Environment.CommandLine + "\r\n" + ex + "\r\n\r\n";
            string log = null;
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProcreateViewer");
                Directory.CreateDirectory(dir);
                log = Path.Combine(dir, "error.log");
                File.AppendAllText(log, text);
            }
            catch { }
            try
            {
                MessageBox.Show((ex == null ? "unknown error" : ex.GetType().Name + ": " + ex.Message) + (log != null ? "\n\n" + log : ""),
                    L.T("Procreate Viewer - error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { }
        }

        /// <summary>--gui-test file outdir: opens the window off-screen, screenshots composite / layer-off / single-layer views, exits.</summary>
        static void GuiTest(string[] args)
        {
            string file = args[1], outDir = args[2];
            Directory.CreateDirectory(outDir);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var form = new MainForm(file);      // same code path as a double-click in Explorer
            int step = 0;
            form.AfterRender = () =>
            {
                step++;
                using (var bmp = new System.Drawing.Bitmap(form.Width, form.Height))
                {
                    form.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, form.Width, form.Height));
                    bmp.Save(Path.Combine(outDir, "gui_" + step + ".png"), ImageFormat.Png);
                }
                try
                {
                    // real screen pixels (only meaningful in an interactive session)
                    Application.DoEvents();
                    var b = form.Bounds;
                    using (var shot = new System.Drawing.Bitmap(b.Width, b.Height))
                    {
                        using (var g = System.Drawing.Graphics.FromImage(shot)) g.CopyFromScreen(b.Location, System.Drawing.Point.Empty, b.Size);
                        shot.Save(Path.Combine(outDir, "screen_" + step + ".png"), ImageFormat.Png);
                    }
                }
                catch (Exception ex) { Trace.Log("screen capture failed: " + ex.Message); }
                if (step == 1) form.ToggleFirstLayer();           // (full-size is the default, so this is a cached full render)
                else if (step == 2) form.ShowFirstLayerAlone();
                else if (step == 3) form.ShowFullSize();
                else if (step == 4) form.ToggleFirstLayer();
                else if (step == 5) form.ToggleLanguage();
                else form.Close();
            };
            Application.Run(form);
            File.WriteAllText(Path.Combine(outDir, "done.txt"), "steps=" + step);
        }

        static int Cli(string[] args)
        {
            try
            {
                switch (args[0])
                {
                    case "--export":
                        {
                            if (args.Length < 3) { Console.WriteLine("usage: --export <file.procreate> <outdir>"); return 2; }
                            var sw = Stopwatch.StartNew();
                            using (var doc = new ProcreateDocument(args[1]))
                            {
                                var r = new Renderer(doc, int.MaxValue);
                                int n = r.ExportAll(args[2], null);
                                Console.WriteLine("exported {0} files to {1} in {2} ms", n, args[2], sw.ElapsedMilliseconds);
                            }
                            return 0;
                        }
                    case "--bench":
                        {
                            if (args.Length < 2) { Console.WriteLine("usage: --bench <file.procreate> [preview.png]"); return 2; }
                            var sw = Stopwatch.StartNew();
                            using (var doc = new ProcreateDocument(args[1]))
                            {
                                long tParse = sw.ElapsedMilliseconds;
                                var r = new Renderer(doc, Renderer.PreviewMax);
                                r.LoadAll(null, false);
                                long tLoad = sw.ElapsedMilliseconds;
                                var c = r.Composite(l => !l.Hidden, true, true);
                                long tComp = sw.ElapsedMilliseconds;
                                using (var bmp = Compositor.ToBitmap(c, doc, true, true))
                                {
                                    long tBmp = sw.ElapsedMilliseconds;
                                    if (args.Length > 2) bmp.Save(args[2], ImageFormat.Png);
                                    Console.WriteLine("{0}: {1}x{2} layers={3} orient={4}{5}{6} preview={7}x{8}", Path.GetFileName(args[1]), doc.Width, doc.Height, doc.LayerCount(),
                                        doc.Orientation, doc.FlipH ? "H" : "", doc.FlipV ? "V" : "", r.PW, r.PH);
                                    Console.WriteLine("  parse {0} ms, decode+downscale {1} ms, composite {2} ms, bitmap {3} ms, total {4} ms",
                                        tParse, tLoad - tParse, tComp - tLoad, tBmp - tComp, sw.ElapsedMilliseconds);
                                }
                            }
                            return 0;
                        }
                    case "--psd":
                        {
                            if (args.Length < 3) { Console.WriteLine("usage: --psd <file.procreate> <out.psd>"); return 2; }
                            var sw = Stopwatch.StartNew();
                            using (var doc = new ProcreateDocument(args[1]))
                            {
                                var r = new Renderer(doc, Renderer.PreviewMax);
                                PsdWriter.Write(r, args[2], null);
                                Console.WriteLine("wrote {0} ({1} MB) in {2} ms", args[2], new FileInfo(args[2]).Length >> 20, sw.ElapsedMilliseconds);
                            }
                            return 0;
                        }
                    case "--tree":
                        {
                            if (args.Length < 2) { Console.WriteLine("usage: --tree <file.procreate>"); return 2; }
                            using (var doc = new ProcreateDocument(args[1]))
                            {
                                Console.WriteLine("{0}  {1}x{2}  orientation={3}{4}{5}  layers={6}", Path.GetFileName(args[1]), doc.Width, doc.Height,
                                    doc.Orientation, doc.FlipH ? " flipH" : "", doc.FlipV ? " flipV" : "", doc.LayerCount());
                                PrintTree(doc.Layers, 0);
                            }
                            return 0;
                        }
                    case "--register":
                        Register(true);
                        Console.WriteLine("registered .procreate -> " + Application.ExecutablePath);
                        return 0;
                    case "--unregister":
                        Register(false);
                        Console.WriteLine("unregistered");
                        return 0;
                    case "--lang":
                        if (args.Length < 2 || (args[1] != "ja" && args[1] != "en")) { Console.WriteLine("usage: --lang ja|en"); return 2; }
                        L.Set(args[1]);
                        Console.WriteLine("language = " + L.Current);
                        return 0;
                    default:
                        Console.WriteLine("ProcreateViewer [file.procreate]\n  --export <file> <outdir>   write composite.png and every layer as PNG (full resolution, group folders)\n  --psd <file> <out.psd>     write a layered Photoshop file (groups, blend modes, opacity, clipping)\n  --tree <file>              print the layer / group hierarchy\n  --bench <file> [out.png]   time the pipeline\n  --register | --unregister  file association for the current user\n  --lang ja|en               UI language (default: follows the Windows display language)");
                        return 2;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("error: " + ex);
                return 1;
            }
        }

        static void PrintTree(System.Collections.Generic.List<Layer> layers, int depth)
        {
            foreach (var l in layers)
            {
                string pad = new string(' ', depth * 2);
                if (l.IsGroup)
                {
                    Console.WriteLine("{0}[{1}]  {2}%{3}", pad, l.Name, Math.Round(l.Opacity * 100), l.Hidden ? "  hidden" : "");
                    PrintTree(l.Children, depth + 1);
                }
                else Console.WriteLine("{0}{1}  {2} {3}%{4}{5}", pad, l.Name, l.BlendName, Math.Round(l.Opacity * 100), l.Hidden ? "  hidden" : "", l.Clipped ? "  clipped" : "");
            }
        }

        /// <summary>Per-user file association (HKCU\Software\Classes), no admin rights needed.</summary>
        static void Register(bool on)
        {
            string exe = Application.ExecutablePath;
            using (var classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes"))
            {
                if (on)
                {
                    using (var ext = classes.CreateSubKey(".procreate")) ext.SetValue("", "Procreate.Document");
                    using (var pid = classes.CreateSubKey("Procreate.Document"))
                    {
                        pid.SetValue("", "Procreate Document");
                        using (var k = pid.CreateSubKey("DefaultIcon")) k.SetValue("", "\"" + exe + "\",0");
                        using (var k = pid.CreateSubKey(@"shell\open")) k.SetValue("MUIVerb", L.T("Open with Procreate Viewer"));
                        using (var k = pid.CreateSubKey(@"shell\open\command")) k.SetValue("", "\"" + exe + "\" \"%1\"");
                        using (var k = pid.CreateSubKey(@"shell\exportpng")) k.SetValue("MUIVerb", L.T("Export layers as PNG"));
                        using (var k = pid.CreateSubKey(@"shell\exportpng\command")) k.SetValue("", "\"" + exe + "\" --export \"%1\" \"%1_layers\"");
                    }
                }
                else
                {
                    classes.DeleteSubKeyTree("Procreate.Document", false);
                    classes.DeleteSubKeyTree(".procreate", false);
                    Registry.CurrentUser.DeleteSubKeyTree(@"Software\ProcreateViewer", false);
                }
            }
            SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);   // SHCNE_ASSOCCHANGED
        }
    }
}
