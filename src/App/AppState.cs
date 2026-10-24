// Everything the local server's handlers share: the settings, the last scan of each game, the
// background job, and the JSON the interface reads. This is hearth_app.py's AppState, ported.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace BlessedHearth
{
    public static class Log
    {
        const long Limit = 1024 * 1024;
        static readonly object gate = new object();

        // A small log in the settings folder for troubleshooting. It never has the API key in it,
        // at most the key's last 4 characters. Once it passes 1 MB the old one gets set aside.
        public static void Write(string msg)
        {
            lock (gate)
            {
                try
                {
                    string path = Path.Combine(AppPaths.AppDataDir(), "app.log");
                    if (File.Exists(path) && new FileInfo(path).Length > Limit)
                    {
                        if (File.Exists(path + ".old")) File.Delete(path + ".old");
                        File.Move(path, path + ".old");
                    }
                    File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + msg + "\n");
                }
                catch (Exception) { }
            }
        }
    }

    public static class Clock
    {
        static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        // Seconds since 1970, same as Python's time.time(). The interface works in these.
        public static double Now => (DateTime.UtcNow - Epoch).TotalSeconds;
    }

    // The window around the interface plugs its own pieces in here: the CurseForge pane, the tray,
    // notifications and file pickers. The Edge fallback leaves these empty.
    public static class Shell
    {
        public static Func<string, string, string> PickFolder;   // (start folder, title) -> picked folder or null
        public static Func<string, string> PickZip;              // (start folder) -> picked zip or null
        public static Action<List<Dictionary<string, object>>> PaneOpen;
        public static Action PaneClose;
        public static Action<string> SetTheme;
        public static Action<bool> SetTray;
        public static Action<string, string, string> Notify;    // (title, body, game install id)
        public static Action<int> TrayStatus;
        public static Func<bool> WindowInFront;
    }

    // Scans, updates and installs run one at a time in the background. The interface polls the
    // current job to show its progress.
    public class Jobs
    {
        readonly object gate = new object();
        Dictionary<string, object> cur = J.D("id", 0L, "running", false);
        public Action OnDone;   // called after every job, keeps the tray's update count current

        public Dictionary<string, object> Snapshot()
        {
            lock (gate) return new Dictionary<string, object>(cur);
        }

        public bool Running
        {
            get { lock (gate) return J.Truthy(cur.Get("running")); }
        }

        public Dictionary<string, object> Start(string label, Func<Progress, object> fn)
        {
            long jid;
            lock (gate)
            {
                if (J.Truthy(cur.Get("running"))) return null;
                jid = (cur.Long("id") ?? 0) + 1;
                cur = J.D("id", jid, "running", true, "label", label, "message", label, "frac", null, "error", null, "result", null);
            }
            Progress progress = (msg, frac) =>
            {
                lock (gate)
                {
                    if (cur.Long("id") == jid) { cur["message"] = msg; cur["frac"] = frac; }
                }
            };
            var t = new Thread(() =>
            {
                object res = null;
                string err = null;
                try { res = fn(progress); }
                catch (Exception e)
                {
                    err = e is ApiError || e is DownloadBlocked || e is IOException || e is UnauthorizedAccessException
                        ? e.Message : e.GetType().Name + ": " + e.Message;
                    Log.Write(e.ToString());
                }
                lock (gate)
                {
                    if (cur.Long("id") == jid)
                    {
                        cur["running"] = false;
                        cur["result"] = res;
                        cur["error"] = err;
                        cur["frac"] = 1.0;
                    }
                }
                try { OnDone?.Invoke(); }
                catch (Exception e) { Log.Write(e.ToString()); }
            });
            t.IsBackground = true;
            t.Start();
            return Snapshot();
        }
    }

    public class ScanResult
    {
        public Dictionary<string, AddonEntry> Entries;   // key -> entry, in name order
        public double Last;
        public bool Online;
    }

    public class AppState
    {
        public static AppState App;

        public readonly Config Config;
        public readonly Jobs Jobs = new Jobs();
        public readonly ManualDownloads Manual;
        public readonly object Sync = new object();   // held while settings or scans change, so readers never see half a change
        readonly Dictionary<string, ScanResult> scans = new Dictionary<string, ScanResult>();   // game install id -> last scan
        public double LastPing = Clock.Now;
        public double? ByeAt;
        int rev;   // goes up whenever the addon list changes, so the interface knows to refresh
        // The key's real status, updated from every CurseForge reply so every screen agrees.
        // "unknown" (saved but not used yet), "ok" or "rejected".
        public Dictionary<string, object> KeyState = J.D("state", "unknown", "at", null, "message", "");
        readonly Dictionary<string, string> changelogs = new Dictionary<string, string>();
        List<Dictionary<string, object>> wowTypes;

        public AppState()
        {
            App = this;
            Config = new Config();
            CurseForgeApi.StatusListener = OnApiStatus;
            Manual = new ManualDownloads(this);
        }

        public int Rev => Volatile.Read(ref rev);
        public void Bump() => Interlocked.Increment(ref rev);

        void OnApiStatus(string key, bool ok, string message)
        {
            if (key == "" || key != Config.ApiKey) return;   // that was some other key being tested, it says nothing about the saved one
            string prev = KeyState.Str("state");
            KeyState = J.D("state", ok ? "ok" : "rejected", "at", Clock.Now, "message", ok ? "" : (message ?? ""));
            if (prev != KeyState.Str("state"))
                Log.Write((BuiltInKey.Present ? "built-in key" : "saved key (ends ..." + Tail(key) + ")") + " is now " + KeyState.Str("state"));
        }

        public static string Tail(string key) => key.Length >= 4 ? key.Substring(key.Length - 4) : key;

        public string ActiveId
        {
            get { lock (Sync) return Config.ActiveProfile()?.Str("id"); }
        }

        public ScanResult ScanOf(string pid)
        {
            lock (Sync)
            {
                ScanResult s;
                return pid != null && scans.TryGetValue(pid, out s) ? s : null;
            }
        }

        public void ForgetScan(string pid)
        {
            lock (Sync) if (pid != null) scans.Remove(pid);
        }

        public bool HasScan(string pid)
        {
            lock (Sync) return pid != null && scans.ContainsKey(pid);
        }

        public Dictionary<string, AddonEntry> EntriesFor(string pid) => ScanOf(pid)?.Entries ?? new Dictionary<string, AddonEntry>();
        public Dictionary<string, AddonEntry> Entries => EntriesFor(ActiveId);

        // Fill in CurseForge's number for this game. It only needs looking up once.
        void EnsureGameId(Dictionary<string, object> p)
        {
            if (p.Str("game") != "wow" && Config.ApiKey != "" && !p.Bool("_gameIdChecked"))
            {
                long? gid = Detect.ResolveGameId(new CurseForgeApi(Config.ApiKey), p.Str("game"));
                lock (Sync)
                {
                    if (gid.HasValue) p["gameId"] = gid.Value;
                    p["_gameIdChecked"] = true;
                    Config.Save();
                }
            }
        }

        public List<Dictionary<string, object>> WowTypes()
        {
            if (Config.ApiKey == "") return new List<Dictionary<string, object>>();
            var cached = wowTypes;
            if (cached == null || cached.Count == 0)
            {
                cached = new CurseForgeApi(Config.ApiKey).VersionTypes(AppInfo.WowGameId);
                wowTypes = cached;
            }
            return cached;
        }

        public void ForgetWowTypes() => wowTypes = null;

        public Dictionary<string, object> GuessFlavor(string addonsDir, string product = "")
        {
            if (product == "") product = Detect.GuessGameForFolder(addonsDir).Item2;
            try { return Detect.GuessWowFlavor(product, WowTypes()); }
            catch (ApiError) { return null; }
        }

        public Dictionary<string, object> ProfileJson(Dictionary<string, object> p)
        {
            var sc = ScanOf(p.Str("id"));
            var g = Games.Get(p.Str("game"));
            object updates = null, count = null;
            if (sc != null)
            {
                updates = (long)sc.Entries.Values.Count(e => e.CanUpdate && !e.Ignored);
                count = (long)sc.Entries.Count;
            }
            string dir = p.Str("addonsDir");
            return J.D("id", p.Str("id"), "game", p.Str("game"), "gameName", g?.Name ?? "",
                       "label", Games.Label(p), "short", Games.Short(p), "addonsDir", dir,
                       "folderOk", dir != "" && Directory.Exists(dir),
                       "flavorId", p.Get("flavorId"), "flavorName", p.Str("flavorName"),
                       "flavors", g != null && g.Flavors,
                       "color", g?.Logo ?? "#B067FF",
                       "updates", updates, "count", count);
        }

        public AddonManager Manager(string pid = null)
        {
            Dictionary<string, object> p;
            lock (Sync) p = pid != null ? Config.Profile(pid) : Config.ActiveProfile();
            if (p != null && p.Str("addonsDir") != "" && Directory.Exists(p.Str("addonsDir")))
            {
                EnsureGameId(p);
                return new AddonManager(Config, p.Str("id"));
            }
            return null;
        }

        // Game folders found on this PC, each one marked if it's already in the app
        public List<Dictionary<string, object>> FoundInstalls()
        {
            var have = new Dictionary<string, string>();
            lock (Sync)
                foreach (var p in Config.ProfileList)
                    if (p.Str("addonsDir") != "") have[AppPaths.NormCase(p.Str("addonsDir"))] = p.Str("id");
            var outList = new List<Dictionary<string, object>>();
            foreach (var i in Detect.Installs())
            {
                string pid;
                have.TryGetValue(AppPaths.NormCase(i.Str("addonsDir")), out pid);
                var item = new Dictionary<string, object>(i);
                item["gameName"] = Games.Get(i.Str("game")).Name;
                item["added"] = pid != null;
                item["pid"] = pid;
                if (i.Str("game") == "wow")
                {
                    var t = GuessFlavor(i.Str("addonsDir"), i.Str("product"));
                    item["flavorId"] = t?.Get("id");
                    item["flavorName"] = t != null ? t.Str("name") : "";
                    item["label"] = item.Str("flavorName") != "" ? item.Str("flavorName") : "World of Warcraft (" + i.Str("where") + ")";
                }
                else
                    item["label"] = Games.Get(i.Str("game")).Name
                                    + (i.Str("game") == "eso" && i.Str("where") != "live" ? " (" + i.Str("where") + ")" : "");
                outList.Add(item);
            }
            return outList;
        }

        // Everything the app supports for the "Add a game" menu, one entry per WoW version and one
        // per other game. The ones found on this PC come with their folder so they add in one click.
        // The rest ask for a folder when picked.
        public List<Dictionary<string, object>> AddOptions()
        {
            var found = FoundInstalls();
            List<Dictionary<string, object>> types;
            try { types = WowTypes(); }
            catch (ApiError) { types = new List<Dictionary<string, object>>(); }
            var byFlavor = new Dictionary<long, string>();
            var byGame = new Dictionary<string, string>();
            lock (Sync)
                foreach (var p in Config.ProfileList)
                {
                    if (p.Str("game") == "wow" && p.Long("flavorId").HasValue && p.Long("flavorId") != 0) byFlavor[p.Long("flavorId").Value] = p.Str("id");
                    byGame[p.Str("game")] = p.Str("id");
                }
            var opts = new List<Dictionary<string, object>>();
            var used = new HashSet<Dictionary<string, object>>();
            Func<Dictionary<string, object>, Dictionary<string, object>> foundOpt = i =>
            {
                used.Add(i);
                return J.D("game", i.Str("game"), "label", i.Str("label"), "flavorId", i.Get("flavorId"), "flavorName", i.Str("flavorName"),
                           "addonsDir", i.Str("addonsDir"), "added", i.Get("added"), "pid", i.Get("pid"));
            };
            Func<string, string, long?, string, Dictionary<string, object>> missingOpt = (game, label, flavorId, pid) =>
                J.D("game", game, "label", label, "flavorId", (object)flavorId, "flavorName", flavorId.HasValue ? label : "",
                    "addonsDir", null, "added", pid != null, "pid", pid);

            foreach (var t in types)
            {
                long tid = t.Long("id") ?? 0;
                var hits = found.Where(i => i.Str("game") == "wow" && J.ToLong(i.Get("flavorId")) == tid).ToList();
                if (hits.Count > 0) opts.AddRange(hits.Select(foundOpt).ToList());
                else
                {
                    string pid;
                    byFlavor.TryGetValue(tid, out pid);
                    opts.Add(missingOpt("wow", t.Str("name"), tid, pid));
                }
            }
            opts.AddRange(found.Where(i => i.Str("game") == "wow" && !used.Contains(i)).ToList().Select(foundOpt).ToList());
            if (!opts.Any(o => o.Str("game") == "wow")) opts.Add(missingOpt("wow", "World of Warcraft", null, null));
            foreach (var g in Games.All.Where(x => x.Id != "wow"))
            {
                var hits = found.Where(i => i.Str("game") == g.Id).ToList();
                if (hits.Count > 0) opts.AddRange(hits.Select(foundOpt).ToList());
                else
                {
                    string pid;
                    byGame.TryGetValue(g.Id, out pid);
                    opts.Add(missingOpt(g.Id, g.Name, null, pid));
                }
            }
            foreach (var o in opts)
            {
                o["gameName"] = Games.Get(o.Str("game")).Name;
                o["short"] = Games.Short(o);
            }
            return opts;
        }

        // Switch the window to this game. Used when you click an update notification.
        public void FocusProfile(string pid)
        {
            lock (Sync)
            {
                if (string.IsNullOrEmpty(pid) || Config.Profile(pid) == null || Config.Data.Get("active") as string == pid) return;
                Config.Data["active"] = pid;
                Config.Save();
            }
            Bump();
        }

        public Dictionary<string, object> Setup()
        {
            lock (Sync)
            {
                var c = Config;
                string d = c.Str("addons_dir");
                var ap = c.ActiveProfile();
                string key = c.ApiKey;
                var keyInfo = new Dictionary<string, object>(KeyState);
                keyInfo["tail"] = key != "" && !BuiltInKey.Present ? Tail(key) : "";   // no need to show any of the app's own key
                long channel = J.ToLong(c["channel"]) ?? 0, maxBackups = J.ToLong(c["max_backups"]) ?? 0;
                double last = Convert.ToDouble(c["last_auto_check"] ?? 0L);
                return J.D(
                    "profiles", c.ProfileList.Select(ProfileJson).ToList(),
                    "active", ap != null ? ProfileJson(ap) : null,
                    "hasKey", key != "",
                    "builtInKey", BuiltInKey.Present,   // the interface skips the paste-your-key steps when this is on
                    "releasesUrl", AppInfo.ReleasesUrl,
                    "key", keyInfo,
                    "addonsDir", d,
                    "folderOk", d != "" && Directory.Exists(d),
                    "flavorId", c["flavor_id"],
                    "flavorName", c.Str("flavor_name"),
                    "channel", channel > 0 ? channel : 1,
                    "keepBackups", J.Truthy(c["keep_backups"]),
                    "maxBackups", maxBackups > 0 ? maxBackups : 15,
                    "theme", Themes.Id(c["theme"]),
                    "tray", J.Truthy(c["tray"]),
                    "startMinimized", J.Truthy(c["start_minimized"]),
                    "autostart", Autostart.Instance.Enabled(),
                    "canTray", Shell.SetTray != null,   // only the app's own window can sit in the tray
                    "canAutostart", Autostart.Instance.Available,
                    "checkHours", (long)AutoCheck.Instance.Hours(),
                    "lastAutoCheck", last > 0 ? (object)last : null,
                    "nextAutoCheck", AutoCheck.Instance.NextAt,
                    "canNotify", Shell.Notify != null,
                    "version", AppInfo.Version);
            }
        }

        // The WoW versions a project has files for, like the Flavors box on its CurseForge page
        List<string> FlavorNames(Dictionary<string, object> mod)
        {
            var names = new Dictionary<long, string>();
            try
            {
                if (mod.Long("gameId") == AppInfo.WowGameId)
                    foreach (var t in WowTypes()) names[t.Long("id") ?? 0] = t.Str("name");
            }
            catch (ApiError) { }
            var seen = new List<string>();
            foreach (var idx in J.List(mod.Get("latestFilesIndexes")).OfType<Dictionary<string, object>>())
            {
                string n;
                if (names.TryGetValue(idx.Long("gameVersionTypeId") ?? -1, out n) && n != "" && !seen.Contains(n)) seen.Add(n);
            }
            return seen;
        }

        static string Label(Dictionary<string, object> f)
        {
            string n = f.Str("displayName");
            return n != "" ? n : f.Str("fileName");
        }

        public Dictionary<string, object> EntryJson(AddonEntry e)
        {
            var mod = e.Mod ?? new Dictionary<string, object>();
            var latest = e.LatestFile ?? new Dictionary<string, object>();
            var installed = e.InstalledFile ?? new Dictionary<string, object>();
            var shots = J.List(mod.Get("screenshots")).Take(6).OfType<Dictionary<string, object>>()
                         .Select(x => x.Str("thumbnailUrl") != "" ? x.Str("thumbnailUrl") : x.Str("url")).Where(u => u != "").ToList();
            string summary = mod.Str("summary");
            if (summary == "") summary = e.Toc != null ? e.Toc.Get("notes") : "";
            return J.D(
                "downloads", mod.Get("downloadCount"),
                "screens", shots,
                "flavorNames", e.Mod != null ? FlavorNames(mod) : new List<string>(),
                "latestId", latest.Get("id"), "installedId", e.InstalledFileId,
                "latestFile", latest.Str("fileName"), "installedFile", installed.Str("fileName"),
                "tocTitle", e.Toc != null ? e.Toc.Title : "", "tocVersion", e.Toc != null ? e.Toc.Version : "",
                "key", e.Key, "name", e.Name, "folders", e.Folders, "modId", e.ModId,
                "status", e.Status, "local", e.LocalVersion, "latest", e.LatestVersion,
                "author", e.Author, "interface", e.Interface, "website", e.Website,
                "logo", J.ObjOrEmpty(mod.Get("logo")).Str("thumbnailUrl"),
                "summary", summary,
                "ignored", e.Ignored, "canUpdate", e.CanUpdate && !e.Ignored,
                "source", e.Source,
                "latestDate", latest.Get("fileDate"),
                "latestChannel", AppInfo.ReleaseName(latest.Get("releaseType")),
                "downloadable", e.LatestFile != null ? (object)(latest.Str("downloadUrl") != "") : null,
                "installedDate", installed.Get("fileDate"),
                "held", e.HeldFile != null ? J.D("version", Label(e.HeldFile), "channel", AppInfo.ReleaseName(e.HeldFile.Get("releaseType"))) : null,
                "channel", e.Channel);
        }

        public Dictionary<string, object> SearchJson(Dictionary<string, object> m, AddonManager mgr)
        {
            long mid = m.Long("id") ?? 0;
            long? fid = AddonManager.LatestFlavorFileId(m, mgr.FlavorId, mgr.ChannelFor(mid));
            var idx = J.List(m.Get("latestFilesIndexes")).OfType<Dictionary<string, object>>().FirstOrDefault(i => fid.HasValue && i.Long("fileId") == fid);
            var info = mgr.State.Info(mid);
            string installed = null;
            if (info != null)
            {
                long? have = info.Long("fileId");
                installed = idx != null && (!have.HasValue || have == 0 || have < idx.Long("fileId")) ? "update" : "installed";
            }
            return J.D(
                "id", mid, "name", m.Str("name"), "summary", m.Str("summary"),
                "authors", J.List(m.Get("authors")).Select(a => J.ObjOrEmpty(a).Str("name")).ToList(),
                "downloads", m.Get("downloadCount") ?? 0L, "updated", m.Get("dateModified"),
                "logo", J.ObjOrEmpty(m.Get("logo")).Str("thumbnailUrl"),
                "website", J.ObjOrEmpty(m.Get("links")).Str("websiteUrl"),
                "file", idx != null ? J.D("name", idx.Get("filename"), "channel", AppInfo.ReleaseName(idx.Get("releaseType"))) : null,
                "installed", installed,
                "distributable", !(m.Get("allowModDistribution") is bool) || (bool)m.Get("allowModDistribution"));
        }

        public Dictionary<string, object> StateJson()
        {
            var sc = ScanOf(ActiveId);
            return J.D(
                "setup", Setup(),
                "addons", (sc?.Entries.Values ?? Enumerable.Empty<AddonEntry>()).OrderBy(x => x.Name.ToLowerInvariant(), StringComparer.Ordinal)
                          .Select(EntryJson).ToList(),
                "lastScan", sc != null ? (object)sc.Last : null,
                "rev", (long)Rev,
                "online", sc != null && sc.Online,
                "job", Jobs.Snapshot(),
                "manual", Manual.Snapshot());
        }

        public string Changelog(long modId, long fileId)
        {
            string k = modId + ":" + fileId, text;
            lock (changelogs) if (changelogs.TryGetValue(k, out text)) return text;
            var mgr = Manager();
            text = mgr != null ? Installer.HtmlToText(mgr.Api.Changelog(modId, fileId)) : "";
            lock (changelogs) changelogs[k] = text;
            return text;
        }

        // The jobs themselves (these run in the background through Jobs)

        public Action<AddonManager, Progress> ScanHook;   // the tests use this to pretend CurseForge is unreachable

        public void Scan(AddonManager mgr, Progress progress)
        {
            if (ScanHook != null) { ScanHook(mgr, progress); return; }
            bool online = Config.ApiKey != "";
            var entries = mgr.Scan(progress, online);
            var dict = new Dictionary<string, AddonEntry>();
            foreach (var e in entries) dict[e.Key] = e;
            lock (Sync) scans[mgr.ProfileId] = new ScanResult { Entries = dict, Last = Clock.Now, Online = online };
            Bump();
        }

        public void Prepare(AddonManager mgr, Progress progress)
        {
            // a WoW install we haven't matched to a CurseForge game version yet gets one now
            var p = mgr.Profile;
            if (p.Str("game") == "wow" && Config.ApiKey != "" && !(p.Long("flavorId").HasValue && p.Long("flavorId") != 0))
            {
                progress("Finding this WoW version on CurseForge...", null);
                var t = GuessFlavor(p.Str("addonsDir")) ?? mgr.DetectFlavor();
                if (t != null)
                {
                    lock (Sync)
                    {
                        p["flavorId"] = t.Get("id");
                        p["flavorName"] = t.Str("name");
                        p["label"] = Games.Label(p);
                        Config.Save();
                    }
                }
            }
        }

        public object JobScan(Progress progress)
        {
            var mgr = Manager();
            if (mgr == null) throw new ApiError("Choose your AddOns folder first.");
            Prepare(mgr, progress);
            Scan(mgr, progress);
            AutoCheck.Instance.AfterScan(notify: false);   // you saw these in the window, so they don't need a notification later
            return J.D("kind", "scan");
        }

        static Dictionary<string, object> Failure(string name, string error, string website, params object[] extra)
        {
            var d = J.D("name", name, "error", error, "website", website);
            for (int i = 0; i + 1 < extra.Length; i += 2) d[(string)extra[i]] = extra[i + 1];
            return d;
        }

        public object JobUpdate(List<string> keys, Progress progress)
        {
            var mgr = Manager();
            var entries = Entries;
            var todo = keys.Where(k => entries.ContainsKey(k) && entries[k].CanUpdate).Select(k => entries[k]).ToList();
            var ok = new List<object>();
            var failed = new List<object>();
            var deps = new HashSet<long>();
            for (int i = 0; i < todo.Count; i++)
            {
                var e = todo[i];
                int n = i;
                Progress p = (msg, frac) => progress(todo.Count == 1 ? msg : msg + "  (" + (n + 1) + " of " + todo.Count + ")", frac);
                try
                {
                    deps.UnionWith(mgr.Update(e, p));
                    ok.Add(e.Name);
                }
                catch (DownloadBlocked ex) { failed.Add(Failure(e.Name, ex.Message, e.Website, "blocked", true, "key", e.Key)); }
                catch (Exception ex) when (ex is ApiError || ex is IOException || ex is UnauthorizedAccessException)
                { failed.Add(Failure(e.Name, ex.Message, e.Website)); }
            }
            Scan(mgr, progress);
            return J.D("kind", "update", "ok", ok, "failed", failed, "deps", DepInfo(mgr, deps));
        }

        public object JobInstall(List<long> modIds, Progress progress)
        {
            var mgr = Manager();
            var mods = mgr.Api.GetMods(modIds);
            var ok = new List<object>();
            var failed = new List<object>();
            var deps = new HashSet<long>();
            foreach (var m in mods)
            {
                string site = J.ObjOrEmpty(m.Get("links")).Str("websiteUrl");
                try
                {
                    var f = mgr.LatestFileForMod(m);
                    if (f == null)
                    {
                        failed.Add(Failure(m.Str("name"), "No file for " + Games.Short(mgr.Profile) + " yet.", site));
                        continue;
                    }
                    deps.UnionWith(mgr.Install(m, f, progress));
                    ok.Add(m.Str("name"));
                }
                catch (DownloadBlocked ex) { failed.Add(Failure(m.Str("name"), ex.Message, site, "blocked", true, "modId", m.Get("id"))); }
                catch (Exception ex) when (ex is ApiError || ex is IOException || ex is UnauthorizedAccessException)
                { failed.Add(Failure(m.Str("name"), ex.Message, site)); }
            }
            Scan(mgr, progress);
            deps.ExceptWith(modIds);
            return J.D("kind", "install", "ok", ok, "failed", failed, "deps", DepInfo(mgr, deps));
        }

        public List<object> DepInfo(AddonManager mgr, IEnumerable<long> depIds)
        {
            var want = depIds.Where(d => !mgr.State.Addons.ContainsKey(d.ToString())).ToList();
            if (want.Count == 0) return new List<object>();
            try { return mgr.Api.GetMods(want).Select(m => (object)J.D("id", m.Get("id"), "name", m.Str("name"))).ToList(); }
            catch (ApiError) { return new List<object>(); }
        }

        public object JobUninstall(List<string> keys, Progress progress)
        {
            var mgr = Manager();
            var entries = Entries;
            var names = new List<object>();
            foreach (string k in keys)
            {
                AddonEntry e;
                if (!entries.TryGetValue(k, out e)) continue;
                progress("Removing " + e.Name, null);
                mgr.Uninstall(e);
                names.Add(e.Name);
            }
            Scan(mgr, progress);
            return J.D("kind", "uninstall", "ok", names);
        }

        public object JobSimple(string action, string key, object arg, Progress progress)
        {
            var mgr = Manager();
            AddonEntry e;
            if (key == null || !Entries.TryGetValue(key, out e)) throw new ApiError("That addon is no longer in the list. Refresh and try again.");
            string target = null;
            if (action == "link")
            {
                var mod = mgr.Api.GetMods(new[] { J.ToLong(arg) ?? 0 })[0];
                mgr.Link(e.Folders, mod);
                target = mod.Str("name");
            }
            else if (action == "unlink") mgr.Unlink(e);
            else if (action == "ignore") mgr.SetIgnored(e, J.Truthy(arg));
            else if (action == "channel") mgr.SetChannel(e, J.Truthy(arg) ? J.ToLong(arg) : null);
            Scan(mgr, progress);
            return J.D("kind", action, "name", e.Name, "target", target);
        }
    }
}
