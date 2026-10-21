// The Edge fallback, and the file pickers.
//
// If Windows' built-in browser engine (WebView2) is missing, the app still works: the interface
// opens in Microsoft Edge in "app mode", which is just Edge with no tabs and no address bar, and
// website downloads open in a small separate Edge window. Both use their own Edge profile, which
// is just a folder in the app's settings folder, so your normal Edge is never touched.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace BlessedHearth
{
    public static class EdgeWindow
    {
        public static string FindBrowser()
        {
            string forced = Environment.GetEnvironmentVariable("HEARTH_BROWSER");
            if (!string.IsNullOrEmpty(forced) && File.Exists(forced)) return forced;
            if (AppInfo.IsWindows)
            {
                foreach (string env in new[] { "ProgramFiles(x86)", "ProgramFiles", "LOCALAPPDATA" })
                {
                    string bas = Environment.GetEnvironmentVariable(env);
                    if (string.IsNullOrEmpty(bas)) continue;
                    foreach (string c in new[] { Path.Combine(bas, "Microsoft", "Edge", "Application", "msedge.exe"),
                                                 Path.Combine(bas, "Google", "Chrome", "Application", "chrome.exe") })
                        if (File.Exists(c)) return c;
                }
                return null;
            }
            foreach (string name in new[] { "microsoft-edge", "google-chrome", "chromium", "chromium-browser" })
                foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':'))
                    if (dir != "" && File.Exists(Path.Combine(dir, name))) return Path.Combine(dir, name);
            return null;
        }

        // Turn off the password manager and autofill in the app's Edge profile so Edge never offers
        // to save the API key
        static void PrepWindowProfile(string profile)
        {
            try
            {
                string d = Path.Combine(profile, "Default");
                Directory.CreateDirectory(d);
                string path = Path.Combine(d, "Preferences");
                var data = J.Obj(Files.ReadJson(path, new Dictionary<string, object>()));
                if (data == null) return;
                data["credentials_enable_service"] = false;
                data["credentials_enable_autosignin"] = false;
                Sub(data, "profile")["password_manager_enabled"] = false;
                var af = Sub(data, "autofill");
                af["enabled"] = false;
                af["profile_enabled"] = false;
                af["credit_card_enabled"] = false;
                File.WriteAllText(path, Json.Write(data));
            }
            catch (Exception) { }
        }

        static Dictionary<string, object> Sub(Dictionary<string, object> d, string key)
        {
            var s = J.Obj(d.Get(key));
            if (s == null) { s = new Dictionary<string, object>(); d[key] = s; }
            return s;
        }

        // Profile for the CurseForge download window. It saves straight into the app's drop folder
        // without asking, and doesn't offer to restore pages after the app closes it.
        static void PrepDownloadProfile(string profile, string folder)
        {
            PrepWindowProfile(profile);
            try
            {
                string path = Path.Combine(profile, "Default", "Preferences");
                var data = J.Obj(Files.ReadJson(path, new Dictionary<string, object>()));
                if (data == null) return;
                var dl = Sub(data, "download");
                dl["default_directory"] = folder;
                dl["prompt_for_download"] = false;
                dl["directory_upgrade"] = true;
                Sub(data, "savefile")["default_directory"] = folder;
                var p = Sub(data, "profile");
                p["exit_type"] = "Normal";
                p["exited_cleanly"] = true;
                File.WriteAllText(path, Json.Write(data));
            }
            catch (Exception) { }
        }

        static string Quote(string a) => a.Contains(" ") || a.Contains("\"") ? "\"" + a.Replace("\"", "\\\"") + "\"" : a;

        static Process Launch(string exe, IEnumerable<string> args)
        {
            var psi = new ProcessStartInfo(exe, string.Join(" ", args.Select(Quote))) { UseShellExecute = false, CreateNoWindow = true };
            try { return Process.Start(psi); }
            catch (Exception) { return null; }
        }

        // Open CurseForge download pages in a small separate Edge or Chrome window with its own
        // profile. null means there's no Edge or Chrome, and the caller falls back to the default browser.
        public static Process OpenDownloadWindow(List<string> urls, string folder)
        {
            string exe = FindBrowser();
            if (exe == null || urls.Count == 0) return null;
            string profile = Path.Combine(AppPaths.AppDataDir(), "download-window");
            PrepDownloadProfile(profile, folder);
            var args = new List<string>
            {
                "--user-data-dir=" + profile, "--new-window", "--window-size=1060,780",
                "--no-first-run", "--no-default-browser-check", "--hide-crash-restore-bubble",
                "--disable-features=Translate,PasswordManagerOnboarding,AutofillServerCommunication",
                "--disable-sync", "--disable-extensions",
            };
            args.AddRange((Environment.GetEnvironmentVariable("HEARTH_BROWSER_ARGS") ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
            args.AddRange(urls);
            return Launch(exe, args);
        }

        public static Process LaunchWindow(string url)
        {
            string exe = FindBrowser();
            if (exe == null)
            {
                OpenInBrowser(url);
                return null;
            }
            string profile = Path.Combine(AppPaths.AppDataDir(), "window");
            PrepWindowProfile(profile);
            var proc = Launch(exe, new[]
            {
                "--app=" + url, "--user-data-dir=" + profile, "--window-size=1280,840",
                "--no-first-run", "--no-default-browser-check",
                "--disable-features=Translate,PasswordManagerOnboarding,AutofillServerCommunication",
                "--disable-sync", "--disable-extensions",
            });
            if (proc == null) OpenInBrowser(url);
            return proc;
        }

        // Your normal browser, for CurseForge pages
        public static void OpenInBrowser(string url)
        {
            try
            {
                if (AppInfo.IsWindows) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                else Process.Start("xdg-open", url);
            }
            catch (Exception e) { Log.Write("couldn't open a browser: " + e.Message); }
        }

        // The Edge window version, used when WebView2 isn't available. Also how the tests run the
        // app (--no-window just prints the address instead of opening anything).
        public static void Run(bool openWindow, int port)
        {
            string ui = Program.UiDir;
            var srv = new Server(ui, port);
            Autostart.Instance.Refresh();
            AutoCheck.Instance.Start();
            var proc = openWindow ? LaunchWindow(srv.Url) : null;
            if (!openWindow) Console.WriteLine(srv.Url);
            Console.Out.Flush();
            double started = Clock.Now;
            bool handedOff = proc == null;   // no Edge process of our own to keep an eye on
            var app = AppState.App;

            // How we know the window's gone:
            //  - the Edge process we started exits (the normal case)
            //  - the page says goodbye as it closes (covers Edge hanging around in the background)
            //  - no heartbeat for 3 minutes (covers everything else, like a plain browser tab)
            while (true)
            {
                double now = Clock.Now;
                bool exited;
                try { exited = proc != null && proc.HasExited; }
                catch (Exception) { exited = true; }
                if (!handedOff && exited)
                {
                    if (now - started < 8) handedOff = true;   // Edge handed the window to a copy of itself that was already running
                    else break;
                }
                if (app.ByeAt.HasValue && now - app.ByeAt.Value > 4) break;
                if (now - app.LastPing > 180 && now - started > 60) break;
                Thread.Sleep(50);
            }
            srv.Stop();
            Log.Write("stopped");
        }
    }

    // Folder and zip pickers. The app's own window brings its own (so they sit on top of it).
    // Otherwise they open on a little thread of their own.
    public static class Dialogs
    {
        public static string PickFolder(string start, string title)
        {
            if (string.IsNullOrEmpty(title)) title = "Choose the game's Interface\\AddOns folder";
            string test = Environment.GetEnvironmentVariable("HEARTH_TEST_PICK");
            if (!string.IsNullOrEmpty(test))   // the tests pretend you picked this folder
            {
                Log.Write("folder picker: " + title);
                return NullIfEmpty(Server.NormalizeAddonsDir(test));
            }
            string picked = Shell.PickFolder != null ? Shell.PickFolder(start, title) : OnOwnThread(() =>
            {
                using (var d = new FolderBrowserDialog { Description = title, ShowNewFolderButton = false })
                {
                    if (!string.IsNullOrEmpty(start) && Directory.Exists(start)) d.SelectedPath = start;
                    return d.ShowDialog(TopForm()) == DialogResult.OK ? d.SelectedPath : null;
                }
            });
            return string.IsNullOrEmpty(picked) ? null : NullIfEmpty(Server.NormalizeAddonsDir(picked));
        }

        public static string PickZip()
        {
            string start = AppPaths.DownloadsDir();
            if (Shell.PickZip != null) return Shell.PickZip(start);
            return OnOwnThread(() =>
            {
                using (var d = new OpenFileDialog { Title = "Choose the addon zip you downloaded", InitialDirectory = start,
                                                   Filter = "Zip files (*.zip)|*.zip|All files (*.*)|*.*" })
                    return d.ShowDialog(TopForm()) == DialogResult.OK ? d.FileName : null;
            });
        }

        static string NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;

        // an invisible always-on-top owner so the picker doesn't open behind Edge
        static Form TopForm() => new Form { TopMost = true, ShowInTaskbar = false, Opacity = 0, StartPosition = FormStartPosition.CenterScreen };

        static string OnOwnThread(Func<string> fn)
        {
            string result = null;
            var t = new Thread(() =>
            {
                try { result = fn(); }
                catch (Exception e) { Log.Write("picker: " + e.Message); }
            });
            t.SetApartmentState(ApartmentState.STA);   // Windows' file pickers insist on this
            t.Start();
            t.Join(TimeSpan.FromMinutes(10));
            return result;
        }
    }
}
