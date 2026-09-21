using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ProcreateViewer
{
    public sealed class UpdateInfo
    {
        public Version Version;
        public string Tag, SetupUrl, ZipUrl, PageUrl;
    }

    /// <summary>
    /// Update check against GitHub Releases (public API, no token).  On start the viewer asks for the latest
    /// release at most once every few hours (result cached in Prefs) and, when it is newer than the running
    /// build, shows a link in the status bar.  Applying an update:
    ///   - installed with the Inno Setup installer: download the Setup exe and run it silently (UAC prompt);
    ///   - portable folder: download the portable zip, then a small cmd script waits for this process to exit,
    ///     copies the new files over the old ones and restarts the viewer.
    /// Development builds (version 0.0.0) never check automatically; "--update" on the command line always does.
    /// </summary>
    public static class Updater
    {
        public const string Repo = "whitechip4/ProcreateViewerForWindows";
        private const string ApiLatest = "https://api.github.com/repos/" + Repo + "/releases/latest";
        private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{B7F3D6A2-5C41-4E0B-9A7D-2F6C1E8D9A10}_is1";
        private const int CacheHours = 6;

        public static Version Local { get { return Assembly.GetExecutingAssembly().GetName().Version; } }
        public static bool IsDevBuild { get { var v = Local; return v.Major == 0 && v.Minor == 0 && v.Build <= 0; } }
        public static string LocalText { get { var v = Local; return string.Format("{0}.{1}.{2}", v.Major, v.Minor, Math.Max(0, v.Build)); } }

        public static bool IsNewer(Version remote)
        {
            var l = Local;
            var a = new Version(remote.Major, remote.Minor, Math.Max(0, remote.Build));
            var b = new Version(l.Major, l.Minor, Math.Max(0, l.Build));
            return a > b;
        }

        /// <summary>Latest release from the cache if it is fresh, otherwise from GitHub (and cached).</summary>
        public static UpdateInfo Latest(bool force)
        {
            if (!force)
            {
                long ticks;
                if (long.TryParse(Prefs.Get("UpdateCheckedAt", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out ticks)
                    && DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc) < TimeSpan.FromHours(CacheHours))
                {
                    string tag = Prefs.Get("UpdateLatestTag", "");
                    Version v;
                    if (tag.Length > 0 && Version.TryParse(tag.TrimStart('v', 'V'), out v))
                        return new UpdateInfo
                        {
                            Version = v, Tag = tag,
                            SetupUrl = Empty(Prefs.Get("UpdateSetupUrl", "")),
                            ZipUrl = Empty(Prefs.Get("UpdateZipUrl", "")),
                            PageUrl = Empty(Prefs.Get("UpdatePageUrl", "")) ?? "https://github.com/" + Repo + "/releases",
                        };
                }
            }
            var info = FetchLatest();
            Prefs.Set("UpdateCheckedAt", DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture));
            Prefs.Set("UpdateLatestTag", info.Tag);
            Prefs.Set("UpdateSetupUrl", info.SetupUrl ?? "");
            Prefs.Set("UpdateZipUrl", info.ZipUrl ?? "");
            Prefs.Set("UpdatePageUrl", info.PageUrl ?? "");
            return info;
        }

        private static string Empty(string s) { return string.IsNullOrEmpty(s) ? null : s; }

        public static UpdateInfo FetchLatest()
        {
            string json = Http(ApiLatest, "application/vnd.github+json");
            var info = new UpdateInfo();
            info.Tag = Match(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"");
            info.PageUrl = Match(json, "\"html_url\"\\s*:\\s*\"([^\"]+/releases/tag/[^\"]+)\"") ?? "https://github.com/" + Repo + "/releases";
            info.SetupUrl = Match(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]*ProcreateViewer-Setup-[^\"]+\\.exe)\"");
            info.ZipUrl = Match(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]*ProcreateViewer-[^\"]+-portable\\.zip)\"");
            if (info.Tag == null) throw new InvalidDataException("no tag_name in the release data");
            Version v;
            if (!Version.TryParse(info.Tag.TrimStart('v', 'V'), out v)) throw new InvalidDataException("unexpected tag " + info.Tag);
            info.Version = v;
            return info;
        }

        private static string Match(string s, string pattern)
        {
            var m = Regex.Match(s, pattern);
            return m.Success ? m.Groups[1].Value : null;
        }

        private static HttpWebRequest Request(string url, string accept)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "ProcreateViewer/" + LocalText + " (Windows)";
            if (accept != null) req.Accept = accept;
            req.AllowAutoRedirect = true;
            req.Timeout = 20000;
            req.ReadWriteTimeout = 60000;
            return req;
        }

        private static string Http(string url, string accept)
        {
            using (var resp = (HttpWebResponse)Request(url, accept).GetResponse())
            using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return r.ReadToEnd();
        }

        private static void Download(string url, string dest, Action<string> progress)
        {
            using (var resp = (HttpWebResponse)Request(url, null).GetResponse())
            using (var s = resp.GetResponseStream())
            using (var f = File.Create(dest))
            {
                var buf = new byte[64 << 10];
                long total = resp.ContentLength, done = 0, lastReport = -1;
                int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0)
                {
                    f.Write(buf, 0, n);
                    done += n;
                    long pct = total > 0 ? done * 100 / total : -1;
                    if (progress != null && pct != lastReport) { lastReport = pct; progress(pct >= 0 ? pct + "%" : (done >> 10) + " KB"); }
                }
            }
            if (new FileInfo(dest).Length == 0) throw new InvalidDataException("empty download");
        }

        /// <summary>Background check for the main window; onNewer runs on a worker thread.</summary>
        public static void CheckInBackground(Action<UpdateInfo> onNewer)
        {
            if (IsDevBuild) { Trace.Log("update check skipped (development build)"); return; }
            Task.Run(() =>
            {
                try
                {
                    var info = Latest(false);
                    Trace.Log("update check: running " + LocalText + ", latest " + info.Tag);
                    if (IsNewer(info.Version)) onNewer(info);
                }
                catch (Exception ex) { Trace.Log("update check failed: " + ex.Message); }
            });
        }

        /// <summary>True when this exe sits in the folder the Inno Setup installer registered.</summary>
        public static bool InstalledWithSetup()
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(UninstallKey))
                {
                    if (k == null) return false;
                    string dir = k.GetValue("InstallLocation") as string;
                    if (string.IsNullOrEmpty(dir)) return false;
                    string here = Path.GetDirectoryName(Application.ExecutablePath);
                    return string.Equals(Path.GetFullPath(dir).TrimEnd('\\'), Path.GetFullPath(here).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { return false; }
        }

        /// <summary>Downloads the release and launches whatever replaces the running program.  The caller exits
        /// right after this returns.  reopenFile (may be null) is passed to the restarted viewer (portable path).</summary>
        public static void Apply(UpdateInfo info, string reopenFile, Action<string> progress)
        {
            string tmp = Path.Combine(Path.GetTempPath(), "ProcreateViewer-update-" + info.Tag);
            Directory.CreateDirectory(tmp);
            if (InstalledWithSetup())
            {
                if (info.SetupUrl == null) throw new InvalidOperationException("this release has no installer");
                string setup = Path.Combine(tmp, Path.GetFileName(info.SetupUrl));
                Download(info.SetupUrl, setup, progress);
                Trace.Log("update: starting installer " + setup);
                Process.Start(new ProcessStartInfo(setup, "/SILENT /NORESTART") { UseShellExecute = true });
                return;
            }

            if (info.ZipUrl == null) throw new InvalidOperationException("this release has no portable zip");
            string zip = Path.Combine(tmp, "portable.zip");
            Download(info.ZipUrl, zip, progress);
            string files = Path.Combine(tmp, "files");
            if (Directory.Exists(files)) Directory.Delete(files, true);
            ZipFile.ExtractToDirectory(zip, files);
            if (!File.Exists(Path.Combine(files, "ProcreateViewer.exe"))) throw new InvalidDataException("the zip has no ProcreateViewer.exe");

            string app = Path.GetDirectoryName(Application.ExecutablePath);
            int pid = Process.GetCurrentProcess().Id;
            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine("rem ProcreateViewer self-update " + info.Tag + ": wait for the viewer to exit, copy the new files, restart it.");
            sb.AppendLine(":wait");
            sb.AppendLine("tasklist /FI \"PID eq " + pid + "\" 2>nul | find \"" + pid + "\" >nul");
            sb.AppendLine("if not errorlevel 1 (ping -n 2 127.0.0.1 >nul & goto wait)");
            sb.AppendLine("copy /y \"" + files + "\\ProcreateViewer.exe\" \"" + app + "\\ProcreateViewer.exe\" >nul");
            sb.AppendLine("if errorlevel 1 (ping -n 3 127.0.0.1 >nul & copy /y \"" + files + "\\ProcreateViewer.exe\" \"" + app + "\\ProcreateViewer.exe\" >nul)");
            sb.AppendLine("copy /y \"" + files + "\\ProcreateThumb.dll\" \"" + app + "\\ProcreateThumb.dll\" >nul 2>&1");   // may be locked by Explorer; harmless to skip
            sb.AppendLine("copy /y \"" + files + "\\README.md\" \"" + app + "\\\" >nul 2>&1");
            sb.AppendLine("copy /y \"" + files + "\\LICENSE\" \"" + app + "\\\" >nul 2>&1");
            sb.AppendLine("start \"\" \"" + app + "\\ProcreateViewer.exe\"" + (reopenFile != null ? " \"" + reopenFile + "\"" : ""));
            sb.AppendLine("rmdir /s /q \"" + files + "\" >nul 2>&1");
            sb.AppendLine("del \"" + zip + "\" >nul 2>&1");
            sb.AppendLine("(goto) 2>nul & del \"%~f0\"");
            string script = Path.Combine(tmp, "apply.cmd");
            Encoding oem;
            try { oem = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage); } catch { oem = Encoding.Default; }
            File.WriteAllText(script, sb.ToString(), oem);   // cmd.exe reads batch files in the OEM code page
            Trace.Log("update: starting " + script);
            Process.Start(new ProcessStartInfo("cmd.exe", "/c \"" + script + "\"") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = tmp });
        }
    }
}
