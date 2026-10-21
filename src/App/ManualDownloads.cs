// Addons whose authors only allow downloads from the CurseForge website. The app opens that
// file's download page, then watches for exactly that file to show up and installs it. It checks
// the size and checksum against CurseForge first, so a random file with the same name won't do.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace BlessedHearth
{
    public class ManualDownloads
    {
        static readonly string[] Partial = { ".crdownload", ".part", ".partial", ".tmp", ".download" };
        const double ExpireAfter = 45 * 60;

        readonly AppState app;
        public readonly object Gate = new object();
        // file id -> status the interface shows
        public readonly Dictionary<string, Dictionary<string, object>> Items = new Dictionary<string, Dictionary<string, object>>();
        // file id -> (mod, file, entry key, game install id)
        readonly Dictionary<string, Tuple<Dictionary<string, object>, Dictionary<string, object>, string, string>> objs =
            new Dictionary<string, Tuple<Dictionary<string, object>, Dictionary<string, object>, string, string>>();
        readonly Dictionary<string, Tuple<long, int>> sizes = new Dictionary<string, Tuple<long, int>>();   // path -> (size, how many checks in a row it stayed that size)
        // The in-app pane and the download window save into the app's own drop folder. If you
        // download in your normal browser it lands in Downloads instead, so we watch both.
        public readonly string AppFolder;
        public readonly string Folder;
        Process window;
        string mode = "window";

        public ManualDownloads(AppState app)
        {
            this.app = app;
            AppFolder = Path.Combine(AppPaths.AppDataDir(), "website-downloads");
            Directory.CreateDirectory(AppFolder);
            Folder = AppPaths.DownloadsDir();
            var t = new Thread(Loop) { IsBackground = true, Name = "website-downloads" };
            t.Start();
        }

        public Dictionary<string, object> Snapshot()
        {
            lock (Gate)
                return J.D("folder", Folder, "mode", mode,
                           "items", Items.Values.Select(v => (object)new Dictionary<string, object>(v)).ToList());
        }

        public bool Busy
        {
            get { lock (Gate) return Items.Values.Any(v => v.Str("status") == "waiting" || v.Str("status") == "installing"); }
        }

        // Show the CurseForge download pages. In the app's own window they open in its pane.
        // Otherwise they open in a separate little Edge window, and if there's no Edge, in the
        // default browser.
        public void OpenPages(List<Dictionary<string, object>> items)
        {
            items = items.Where(it => it.Str("url") != "").ToList();
            if (items.Count == 0) return;
            if (Shell.PaneOpen != null)
            {
                Shell.PaneOpen(items.Select(it => J.D("url", it.Str("url"), "name", it.Str("name"), "fileName", it.Str("fileName"),
                                                      "fileId", it.Get("fileId"))).ToList());
                mode = "pane";
                return;
            }
            var urls = items.Select(it => it.Str("url")).ToList();
            if (window != null && !HasExited(window))
            {
                // the window's already open, and launching it again with the same profile just adds tabs
                EdgeWindow.OpenDownloadWindow(urls, AppFolder);
                mode = "window";
                return;
            }
            var proc = EdgeWindow.OpenDownloadWindow(urls, AppFolder);
            if (proc != null)
            {
                window = proc;
                mode = "window";
                return;
            }
            mode = "browser";
            foreach (string u in urls)
            {
                EdgeWindow.OpenInBrowser(u);
                Thread.Sleep(800);
            }
        }

        static bool HasExited(Process p)
        {
            try { return p.HasExited; }
            catch (Exception) { return true; }
        }

        void CloseWindowIfDone()
        {
            bool busy = Busy;
            if (!busy) Shell.PaneClose?.Invoke();
            var w = window;
            Log.Write("download window check: busy=" + (busy ? "True" : "False") + " window=" + (w == null ? "none" : (HasExited(w) ? "exited" : "open")));
            if (!busy && w != null && !HasExited(w))
            {
                Thread.Sleep(1500);
                try
                {
                    // ask it to close like clicking its X first, then make it
                    w.CloseMainWindow();
                    if (!w.WaitForExit(5000)) w.Kill();
                }
                catch (Exception)
                {
                    try { w.Kill(); } catch (Exception) { }
                }
                Log.Write("download window closed");
                window = null;
            }
        }

        public Dictionary<string, object> Add(Dictionary<string, object> mod, Dictionary<string, object> file, string entryKey, string pid = null)
        {
            string fid = Convert.ToString(file.Long("id"));
            var item = J.D("fileId", file.Get("id"), "modId", mod.Get("id"), "name", mod.Str("name"), "fileName", file.Str("fileName"),
                           "version", file.Str("displayName"), "url", Installer.WebsiteDownloadUrl(mod, file),
                           "status", "waiting", "error", "", "since", Clock.Now, "key", entryKey, "profile", pid);
            lock (Gate)
            {
                Items[fid] = item;
                objs[fid] = Tuple.Create(mod, file, entryKey, pid);
            }
            Log.Write("website download requested: " + item.Str("name") + " -> " + item.Str("fileName"));
            return new Dictionary<string, object>(item);
        }

        public void UseFile(string fileId, string path)
        {
            lock (Gate)
            {
                Dictionary<string, object> item;
                if (!Items.TryGetValue(fileId, out item)) return;
                item["status"] = "waiting";
                item["error"] = "";
                item["picked"] = path;
            }
        }

        public void Clear(bool doneOnly = true)
        {
            lock (Gate)
                foreach (string k in Items.Where(kv => !doneOnly || kv.Value.Str("status") == "installed" || kv.Value.Str("status") == "expired")
                                          .Select(kv => kv.Key).ToList())
                {
                    Items.Remove(k);
                    objs.Remove(k);
                }
        }

        string Find(Dictionary<string, object> item)
        {
            if (item.Str("picked") != "") return File.Exists(item.Str("picked")) ? item.Str("picked") : null;
            string want = item.Str("fileName");
            if (want == "") return null;
            string stem = Path.GetFileNameWithoutExtension(want), ext = Path.GetExtension(want);
            // browsers tack on " (1)", " (2)" when a file with that name is already there
            var pat = new Regex("^" + Regex.Escape(stem) + @"( \(\d+\))?" + Regex.Escape(ext) + "$", RegexOptions.IgnoreCase);
            var found = new List<string>();
            foreach (string folder in new[] { AppFolder, Folder })
            {
                try
                {
                    found.AddRange(Directory.GetFiles(folder).Where(p =>
                    {
                        string n = Path.GetFileName(p);
                        return pat.IsMatch(n) && !Partial.Any(s => n.ToLowerInvariant().EndsWith(s));
                    }));
                }
                catch (Exception) { }
            }
            return found.Count > 0 ? found.OrderByDescending(File.GetLastWriteTimeUtc).First() : null;
        }

        // only call a download finished once its size holds still for two checks in a row
        bool Stable(string path)
        {
            long size;
            try { size = new FileInfo(path).Length; }
            catch (Exception) { return false; }
            Tuple<long, int> prev;
            sizes.TryGetValue(path, out prev);
            var now = Tuple.Create(size, prev != null && prev.Item1 == size ? prev.Item2 + 1 : 0);
            sizes[path] = now;
            return size > 0 && now.Item2 >= 1;
        }

        void Loop()
        {
            while (true)
            {
                Thread.Sleep(1000);
                try
                {
                    List<KeyValuePair<string, Dictionary<string, object>>> waiting;
                    lock (Gate)
                        waiting = Items.Where(kv => kv.Value.Str("status") == "waiting")
                                       .Select(kv => new KeyValuePair<string, Dictionary<string, object>>(kv.Key, new Dictionary<string, object>(kv.Value))).ToList();
                    foreach (var kv in waiting)
                    {
                        string fid = kv.Key;
                        var item = kv.Value;
                        if (Clock.Now - Convert.ToDouble(item.Get("since") ?? 0.0) > ExpireAfter)
                        {
                            lock (Gate) if (Items.ContainsKey(fid)) Items[fid]["status"] = "expired";
                            continue;
                        }
                        string path = Find(item);
                        if (path == null || !Stable(path)) continue;
                        Tuple<Dictionary<string, object>, Dictionary<string, object>, string, string> o;
                        lock (Gate)
                        {
                            if (!objs.TryGetValue(fid, out o)) continue;
                            Items[fid]["status"] = "installing";
                        }
                        var snap = app.Jobs.Start("Installing " + item.Str("name"), p => InstallFrom(fid, o.Item1, o.Item2, o.Item3, path, p, o.Item4));
                        if (snap == null)   // something else is running, so try again next time around
                            lock (Gate) if (Items.ContainsKey(fid)) Items[fid]["status"] = "waiting";
                    }
                }
                catch (Exception e) { Log.Write(e.ToString()); }
            }
        }

        object InstallFrom(string fid, Dictionary<string, object> mod, Dictionary<string, object> file, string key, string zipPath, Progress progress, string pid)
        {
            try
            {
                var mgr = app.Manager(pid);
                if (mgr == null) throw new ApiError("That game's AddOns folder isn't there anymore.");
                AddonEntry entry = null;
                if (key != null) app.EntriesFor(mgr.ProfileId).TryGetValue(key, out entry);
                progress("Installing " + mod.Str("name"), null);
                List<long> deps;
                if (entry != null && entry.ModId == mod.Long("id")) deps = mgr.Update(entry, progress, zipPath);
                else deps = mgr.Install(mod, file, progress, zipPath);
                lock (Gate)
                {
                    Items[fid]["status"] = "installed";
                    Items[fid]["error"] = "";
                }
                Log.Write("installed from website download: " + mod.Str("name") + " (" + Path.GetFileName(zipPath) + ")");
                // the app's own drop folder gets cleaned up, but your Downloads folder is left alone
                if (AppPaths.SamePath(Path.GetDirectoryName(Path.GetFullPath(zipPath)), AppFolder))
                {
                    try { File.Delete(zipPath); } catch (Exception) { }
                }
                app.Scan(mgr, progress);
                new Thread(CloseWindowIfDone) { IsBackground = true }.Start();
                return J.D("kind", "manual", "ok", new List<object> { mod.Str("name") }, "failed", new List<object>(), "deps", app.DepInfo(mgr, deps));
            }
            catch (Exception e) when (e is ApiError || e is IOException || e is UnauthorizedAccessException)
            {
                lock (Gate)
                {
                    Items[fid]["status"] = "failed";
                    Items[fid]["error"] = e.Message;
                }
                throw;
            }
        }
    }
}
