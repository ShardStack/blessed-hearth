// What the app knows about each installed addon, and everything you can do to one game install:
// scan it, update, install, remove, link.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace BlessedHearth
{
    public static class Status
    {
        public const string UpToDate = "Up to date";
        public const string Update = "Update available";
        public const string Unknown = "Installed version unknown";
        public const string NotLinked = "Not linked";
        public const string NoFlavor = "No Forever file";
        public const string Ignored = "Ignored";
        public const string Offline = "Not checked";
    }

    public class AddonEntry
    {
        public string Key;                    // "mod:<project id>" once we know the project, otherwise "folder:<name>"
        public string Name;
        public List<string> Folders;
        public TocInfo Toc;
        public long? ModId;
        public Dictionary<string, object> Mod;
        public long? InstalledFileId;
        public Dictionary<string, object> InstalledFile;
        public Dictionary<string, object> LatestFile;
        public string Source = "";            // how we matched it: fingerprint, tracked, toc or manual
        public string Status = BlessedHearth.Status.Offline;
        public bool Ignored;
        public Dictionary<string, object> HeldFile;   // a newer file that's only beta or alpha, held back by the update channel
        public long? Channel;                 // this addon's own update channel, if it has one

        static string FileLabel(Dictionary<string, object> f)
        {
            string n = f.Str("displayName");
            return n != "" ? n : f.Str("fileName");
        }

        public string LocalVersion => InstalledFile != null ? FileLabel(InstalledFile) : (Toc != null ? Toc.Version : "");
        public string LatestVersion => LatestFile != null ? FileLabel(LatestFile) : "";

        public string Author
        {
            get
            {
                var authors = J.List(Mod.Get("authors"));
                if (Mod != null && authors.Count > 0)
                    return string.Join(", ", authors.Take(2).Select(a => J.ObjOrEmpty(a).Str("name")));
                return Toc != null ? Toc.Get("author") : "";
            }
        }

        public string Interface => Toc != null ? Toc.Get("interface") : "";
        public string Website => J.ObjOrEmpty(Mod.Get("links")).Str("websiteUrl");
        public bool CanUpdate => (Status == BlessedHearth.Status.Update || Status == BlessedHearth.Status.Unknown) && LatestFile != null;
    }

    public class AddonManager
    {
        public readonly Config Config;
        public readonly string ProfileId;
        public readonly State State;
        public readonly CurseForgeApi Api;

        public AddonManager(Config config, string profileId = null)
        {
            Config = config;
            var p = profileId != null ? config.Profile(profileId) : config.ActiveProfile();
            if (p == null) throw new ApiError("No game is set up yet. Add one in Settings.");
            ProfileId = p.Str("id");
            State = new State(p.Str("addonsDir"));
            Api = new CurseForgeApi(config.ApiKey);
        }

        public Dictionary<string, object> Profile => Config.Profile(ProfileId) ?? new Dictionary<string, object>();
        public string AddonsDir => Profile.Str("addonsDir");
        public string Game => Profile.Str("game") != "" ? Profile.Str("game") : "wow";
        public long GameId => Profile.Long("gameId") ?? Games.GetOrWow(Game).FallbackId ?? AppInfo.WowGameId;
        public long? FlavorId => Game == "wow" ? Profile.Long("flavorId") : null;
        public string FlavorName => Profile.Str("flavorName");
        public int Channel
        {
            get
            {
                long c = J.ToLong(Config["channel"]) ?? 0;
                return c > 0 ? (int)c : AppInfo.Release;
            }
        }

        public int ChannelFor(object modId)
        {
            long? own = State.Info(modId).Long("channel");
            return own.HasValue && own.Value > 0 ? Math.Max((int)own.Value, Channel) : Channel;
        }

        // Find CurseForge's game version whose name has `keyword` in it
        public Dictionary<string, object> DetectFlavor(string keyword = "forever")
        {
            foreach (var t in Api.VersionTypes(GameId))
                if ((t.Str("name") + " " + t.Str("slug")).ToLowerInvariant().Contains(keyword)) return t;
            return null;
        }

        public List<string> ListFolders()
        {
            var outList = new List<string>();
            foreach (string path in Directory.GetDirectories(AddonsDir).OrderBy(p => Path.GetFileName(p).ToLowerInvariant(), StringComparer.Ordinal))
            {
                string name = Path.GetFileName(path);
                if (name.StartsWith(".") || (Game == "wow" && name.ToLowerInvariant().StartsWith("blizzard_"))) continue;
                outList.Add(name);
            }
            return outList;
        }

        Dictionary<string, List<long>> Fingerprints(List<string> folders, Progress progress)
        {
            var result = new Dictionary<string, List<long>>();
            for (int i = 0; i < folders.Count; i++)
            {
                string name = folders[i];
                progress("Reading " + name, (double)i / Math.Max(1, folders.Count));
                string path = Path.Combine(AddonsDir, name);
                try
                {
                    var variants = Fingerprint.Variants(Game, path);
                    string sig = Fingerprint.Signature(variants.SelectMany(v => v));
                    var cached = State.FpCache.Get(name) as List<object>;
                    if (cached != null && cached.Count == 2 && cached[0] as string == sig && cached[1] is List<object>)
                    {
                        result[name] = ((List<object>)cached[1]).Select(x => J.ToLong(x) ?? 0).ToList();
                        continue;
                    }
                    var fps = Fingerprint.HashVariants(variants);
                    State.FpCache[name] = new List<object> { sig, fps.Cast<object>().ToList() };
                    result[name] = fps;
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
            }
            return result;
        }

        class Group
        {
            public List<string> Folders = new List<string>();
            public Dictionary<string, object> File;
            public string Source;
        }

        public List<AddonEntry> Scan(Progress progress, bool online = true)
        {
            var folders = ListFolders();
            var tocs = new Dictionary<string, TocInfo>();
            foreach (string name in folders)
            {
                var info = Toc.ParseManifest(Game, Path.Combine(AddonsDir, name), name, FlavorName);
                if (info != null) tocs[name] = info;
            }
            if (Game == "wow" && tocs.Count > 0)
            {
                // Most addons in a folder list the game's own interface number (16001 for Forever, say).
                // If an addon's chosen TOC doesn't, it probably has another TOC that does. Use that one.
                var counts = new Dictionary<string, int>();
                var order = new List<string>();
                foreach (var t in folders.Where(tocs.ContainsKey).Select(n => tocs[n]))
                    foreach (string i in t.Interfaces)
                    {
                        if (!counts.ContainsKey(i)) { counts[i] = 0; order.Add(i); }
                        counts[i]++;
                    }
                if (counts.Count > 0)
                {
                    string hint = order.OrderByDescending(i => counts[i]).First();   // stable, so ties go to the first one seen
                    foreach (string name in tocs.Keys.ToList())
                    {
                        if (tocs[name].Interfaces.Contains(hint)) continue;
                        var better = Toc.ParseManifest(Game, Path.Combine(AddonsDir, name), name, FlavorName, hint);
                        if (better != null) tocs[name] = better;
                    }
                }
            }
            folders = folders.Where(tocs.ContainsKey).ToList();   // no manifest means it isn't an addon

            var fps = Fingerprints(folders, progress);
            online = online && Api.ApiKey != "";

            // Step 1: ask CurseForge about the fingerprints. A match tells us the exact file installed.
            var folderMatch = new Dictionary<string, Tuple<long, Dictionary<string, object>>>();
            if (online && fps.Count > 0)
            {
                progress("Identifying addons with CurseForge...", null);
                var byFp = new Dictionary<long, List<string>>();
                var fpOrder = new List<long>();
                foreach (string name in folders.Where(fps.ContainsKey))
                    foreach (long fp in fps[name])
                    {
                        if (!byFp.ContainsKey(fp)) { byFp[fp] = new List<string>(); fpOrder.Add(fp); }
                        byFp[fp].Add(name);
                    }
                foreach (var m in Api.FingerprintMatches(fpOrder, GameId))
                {
                    var f = J.ObjOrEmpty(m.Get("file"));
                    long modId = m.Long("id") ?? f.Long("modId") ?? 0;
                    foreach (var mod in J.List(f.Get("modules")).OfType<Dictionary<string, object>>())
                    {
                        List<string> names;
                        if (!byFp.TryGetValue(mod.Long("fingerprint") ?? -1, out names)) continue;
                        foreach (string name in names)
                            if (string.Equals(name, mod.Str("name"), StringComparison.OrdinalIgnoreCase) && !folderMatch.ContainsKey(name))
                                folderMatch[name] = Tuple.Create(modId, f);
                    }
                }
            }

            // Step 2: anything that didn't match falls back to what we installed before,
            // then to an X-Curse-Project-ID line in the TOC, and otherwise it's left unlinked.
            var owner = State.FolderOwner();
            var groups = new Dictionary<long, Group>();
            var groupOrder = new List<long>();
            var unlinked = new List<string>();
            Func<long, string, Group> groupFor = (mid, source) =>
            {
                Group g;
                if (!groups.TryGetValue(mid, out g)) { g = new Group { Source = source }; groups[mid] = g; groupOrder.Add(mid); }
                return g;
            };
            foreach (string name in folders)
            {
                Tuple<long, Dictionary<string, object>> hit;
                if (folderMatch.TryGetValue(name, out hit))
                {
                    var g = groupFor(hit.Item1, "fingerprint");
                    g.Folders.Add(name);
                    if (g.File == null) g.File = hit.Item2;
                    continue;
                }
                string mids;
                long mid;
                if (owner.TryGetValue(name.ToLowerInvariant(), out mids) && long.TryParse(mids, out mid))
                {
                    groupFor(mid, "tracked").Folders.Add(name);
                    continue;
                }
                long? cid = tocs[name].CurseId;
                if (cid.HasValue)
                {
                    groupFor(cid.Value, "toc").Folders.Add(name);
                    continue;
                }
                unlinked.Add(name);
            }

            var entries = new List<AddonEntry>();
            var mods = new Dictionary<long, Dictionary<string, object>>();
            var files = new Dictionary<long, Dictionary<string, object>>();
            if (online && groups.Count > 0)
            {
                progress("Checking CurseForge for updates...", null);
                foreach (var m in Api.GetMods(groupOrder)) mods[m.Long("id") ?? 0] = m;
            }

            foreach (long mid in groupOrder)
            {
                var g = groups[mid];
                var info = State.Info(mid) ?? new Dictionary<string, object>();
                string main = MainFolder(g.Folders, g.File);
                long? fid = (g.File != null ? g.File.Long("id") : null) ?? info.Long("fileId");
                if (g.File != null && g.File.Long("id").HasValue) files[g.File.Long("id").Value] = g.File;
                Dictionary<string, object> mod;
                mods.TryGetValue(mid, out mod);
                string name = mod.Str("name");
                if (name == "") name = info.Str("name");
                if (name == "") name = tocs[main].Title;
                if (name == "") name = main;
                var e = new AddonEntry
                {
                    Key = "mod:" + mid,
                    Name = name,
                    Folders = g.Folders.OrderBy(f => f.ToLowerInvariant(), StringComparer.Ordinal).ToList(),
                    Toc = tocs[main], ModId = mid, Mod = mod,
                    InstalledFileId = fid,
                    Source = g.Source != "tracked" ? g.Source : info.Str("source", "tracked"),
                    Ignored = info.Bool("ignored"),
                };
                entries.Add(e);
                // remember what we learned so the next scan is quicker and works offline too
                var newInfo = new Dictionary<string, object>(info);
                newInfo["name"] = e.Name;
                newInfo["folders"] = e.Folders.Cast<object>().ToList();
                newInfo["fileId"] = fid;
                newInfo["source"] = e.Source;
                if (e.Mod != null) newInfo["websiteUrl"] = e.Website;
                State.Addons[mid.ToString()] = newInfo;
            }

            if (online && entries.Count > 0) ResolveFiles(entries, files, progress);

            entries.AddRange(GroupUnlinked(unlinked, tocs));
            foreach (var e in entries) SetStatus(e, online);
            State.Save();
            entries = entries.OrderBy(x => x.Name.ToLowerInvariant(), StringComparer.Ordinal).ToList();
            progress("Found " + entries.Count + " addons", 1.0);
            return entries;
        }

        static string MainFolder(List<string> folders, Dictionary<string, object> f)
        {
            if (f != null)
                foreach (var mod in J.List(f.Get("modules")).OfType<Dictionary<string, object>>())
                    if (folders.Contains(mod.Str("name"))) return mod.Str("name");
            return folders.OrderBy(n => n.Length).ThenBy(n => n.ToLowerInvariant(), StringComparer.Ordinal).First();
        }

        static long? FileId(Dictionary<string, object> f) => f?.Long("id");

        public static long? LatestFlavorFileId(Dictionary<string, object> mod, long? flavorId, int channel)
        {
            long? best = null;
            foreach (var idx in J.List(mod.Get("latestFilesIndexes")).OfType<Dictionary<string, object>>())
            {
                if (flavorId.HasValue && idx.Long("gameVersionTypeId") != flavorId) continue;
                if ((idx.Long("releaseType") ?? AppInfo.Release) > channel) continue;
                long? fid = idx.Long("fileId");
                if (fid.HasValue && fid.Value != 0 && (best == null || fid > best)) best = fid;
            }
            return best;
        }

        public static bool FileMatchesFlavor(Dictionary<string, object> f, long? flavorId)
        {
            if (!flavorId.HasValue) return true;
            return J.List(f.Get("sortableGameVersions")).OfType<Dictionary<string, object>>().Any(v => v.Long("gameVersionTypeId") == flavorId);
        }

        static bool Available(Dictionary<string, object> f) => !f.ContainsKey("isAvailable") || J.Truthy(f.Get("isAvailable"));

        void ResolveFiles(List<AddonEntry> entries, Dictionary<long, Dictionary<string, object>> known, Progress progress)
        {
            var want = new HashSet<long>();
            var latestId = new Dictionary<long, long?>();
            var heldId = new Dictionary<long, long>();
            foreach (var e in entries)
            {
                if (e.Mod == null) continue;
                long mid = e.ModId.Value;
                int ch = ChannelFor(mid);
                e.Channel = State.Info(mid).Long("channel");
                long? lid = LatestFlavorFileId(e.Mod, FlavorId, ch);
                latestId[mid] = lid;
                if (lid.HasValue) want.Add(lid.Value);
                // a newer file that only exists as beta or alpha, held back by the update channel
                long? anyId = LatestFlavorFileId(e.Mod, FlavorId, AppInfo.Alpha);
                if (anyId.HasValue && anyId != lid)
                {
                    heldId[mid] = anyId.Value;
                    want.Add(anyId.Value);
                }
                if (e.InstalledFileId.HasValue && e.InstalledFileId != 0 && !known.ContainsKey(e.InstalledFileId.Value)) want.Add(e.InstalledFileId.Value);
            }
            if (want.Count > 0)
                foreach (var f in Api.GetFiles(want))
                    if (f.Long("id").HasValue) known[f.Long("id").Value] = f;
            foreach (var e in entries)
            {
                if (e.Mod == null) continue;
                long mid = e.ModId.Value;
                long? lid;
                latestId.TryGetValue(mid, out lid);
                Dictionary<string, object> f;
                if (heldId.ContainsKey(mid) && known.TryGetValue(heldId[mid], out f)) e.HeldFile = f;
                if (lid.HasValue)
                {
                    known.TryGetValue(lid.Value, out f);
                    e.LatestFile = f;
                }
                else if (!heldId.ContainsKey(mid))
                {
                    // CurseForge's quick index had nothing for this game version, so ask for the file list
                    try
                    {
                        progress("Looking up files for " + e.Name, null);
                        var cands = Api.GetModFiles(mid, FlavorId)
                                       .Where(c => (c.Long("releaseType") ?? AppInfo.Release) <= ChannelFor(mid) && FileMatchesFlavor(c, FlavorId) && Available(c))
                                       .ToList();
                        if (cands.Count > 0) e.LatestFile = cands.OrderByDescending(c => c.Long("id") ?? 0).First();
                    }
                    catch (ApiError) { }
                }
                if (e.InstalledFileId.HasValue && e.InstalledFileId != 0)
                {
                    known.TryGetValue(e.InstalledFileId.Value, out f);
                    e.InstalledFile = f;
                }
            }
        }

        // Tuck helper folders (Foo_Options, which depends on Foo) under their parent addon
        static List<AddonEntry> GroupUnlinked(List<string> names, Dictionary<string, TocInfo> tocs)
        {
            var lower = new Dictionary<string, string>();
            foreach (string n in names) lower[n.ToLowerInvariant()] = n;
            var parent = new Dictionary<string, string>();
            foreach (string n in names)
            {
                foreach (string dep in tocs[n].Deps)
                {
                    string d;
                    if (lower.TryGetValue(dep.ToLowerInvariant(), out d) && d != n && n.ToLowerInvariant().StartsWith(d.ToLowerInvariant()))
                    {
                        parent[n] = d;
                        break;
                    }
                }
            }
            Func<string, string> root = n =>
            {
                var seen = new HashSet<string>();
                while (parent.ContainsKey(n) && !seen.Contains(n)) { seen.Add(n); n = parent[n]; }
                return n;
            };
            var groups = new Dictionary<string, List<string>>();
            var order = new List<string>();
            foreach (string n in names)
            {
                string r = root(n);
                if (!groups.ContainsKey(r)) { groups[r] = new List<string>(); order.Add(r); }
                groups[r].Add(n);
            }
            return order.Select(r => new AddonEntry
            {
                Key = "folder:" + r,
                Name = tocs[r].Title != "" ? tocs[r].Title : r,
                Folders = groups[r].OrderBy(f => f.ToLowerInvariant(), StringComparer.Ordinal).ToList(),
                Toc = tocs[r], Source = "",
            }).ToList();
        }

        void SetStatus(AddonEntry e, bool online)
        {
            if (e.ModId == null) e.Status = Status.NotLinked;
            else if (e.Ignored) e.Status = Status.Ignored;
            else if (!online || e.Mod == null) e.Status = Status.Offline;
            else if (e.LatestFile == null) e.Status = Status.NoFlavor;
            else if (!e.InstalledFileId.HasValue || e.InstalledFileId == 0)
            {
                // Installed by hand and edited, so the fingerprint didn't match. If the TOC's version
                // shows up in the newest file's name, it's already current.
                string v = e.Toc != null ? e.Toc.Version.Trim() : "";
                string names = e.LatestFile.Str("displayName") + " " + e.LatestFile.Str("fileName");
                if (v.Length >= 3 && Regex.IsMatch(names, @"(?<![\w.])" + Regex.Escape(v) + @"(?![\w]|\.\d)"))
                {
                    e.InstalledFileId = FileId(e.LatestFile);
                    e.InstalledFile = e.LatestFile;
                    State.InfoOrNew(e.ModId.Value)["fileId"] = e.InstalledFileId;
                    e.Status = Status.UpToDate;
                }
                else e.Status = Status.Unknown;
            }
            else if ((FileId(e.LatestFile) ?? 0) > e.InstalledFileId) e.Status = Status.Update;
            else e.Status = Status.UpToDate;
        }

        public Dictionary<string, object> LatestFileForMod(Dictionary<string, object> mod)
        {
            long mid = mod.Long("id") ?? 0;
            long? lid = LatestFlavorFileId(mod, FlavorId, ChannelFor(mid));
            if (lid.HasValue)
            {
                foreach (var f in J.List(mod.Get("latestFiles")).OfType<Dictionary<string, object>>())
                    if (f.Long("id") == lid) return f;
                var got = Api.GetFiles(new[] { lid.Value });
                if (got.Count > 0) return got[0];
            }
            var cands = Api.GetModFiles(mid, FlavorId)
                           .Where(f => (f.Long("releaseType") ?? AppInfo.Release) <= ChannelFor(mid) && FileMatchesFlavor(f, FlavorId))
                           .ToList();
            return cands.Count > 0 ? cands.OrderByDescending(f => f.Long("id") ?? 0).First() : null;
        }

        // Download and install `file`, or install it from `zipPath` (a copy you downloaded from the
        // CurseForge website). Returns the required addons that aren't installed yet.
        public List<long> Install(Dictionary<string, object> mod, Dictionary<string, object> file, Progress progress, string zipPath = null)
        {
            string url = file.Str("downloadUrl");
            if (url == "" && zipPath == null)
                throw new DownloadBlocked("The author of " + mod.Str("name") + " only allows downloads from the CurseForge website.", mod, file);
            string mid = Convert.ToString(mod.Long("id"));
            var oldFolders = J.List(State.Info(mid).Get("folders")).Select(x => Convert.ToString(x)).ToList();
            string work = Path.Combine(Path.GetTempPath(), "hearth_" + Guid.NewGuid().ToString("N").Substring(0, 10));
            Directory.CreateDirectory(work);
            List<string> newFolders;
            try
            {
                string zpath;
                if (zipPath != null) zpath = zipPath;
                else
                {
                    zpath = Path.Combine(work, "addon.zip");
                    progress("Downloading " + mod.Str("name"), 0.0);
                    Api.Download(url, zpath, progress);
                }
                // either way, it has to be byte for byte the file CurseForge describes
                Installer.VerifyDownload(file, zpath);
                progress("Unpacking " + mod.Str("name"), null);
                string extractDir = Path.Combine(work, "x");
                newFolders = Installer.SafeExtract(zpath, extractDir);
                SwapIn(mod.Str("name", mid), extractDir, newFolders, oldFolders);
            }
            finally { Files.DeleteTree(work); }

            var info = State.Info(mid) ?? new Dictionary<string, object>();
            info["name"] = mod.Get("name");
            info["folders"] = newFolders.Cast<object>().ToList();
            info["fileId"] = file.Long("id");
            info["source"] = "installed";
            info["websiteUrl"] = J.ObjOrEmpty(mod.Get("links")).Str("websiteUrl");
            State.Addons[mid] = info;
            foreach (string f in newFolders) State.FpCache.Remove(f);
            State.Save();
            progress("Installed " + mod.Str("name") + " " + file.Str("displayName"), 1.0);

            var installedIds = new HashSet<long>(State.Addons.Keys.Select(k => { long v; return long.TryParse(k, out v) ? v : -1; }));
            return J.List(file.Get("dependencies")).OfType<Dictionary<string, object>>()
                    .Where(d => d.Long("relationType") == AppInfo.RequiredDependency && d.Long("modId").HasValue && !installedIds.Contains(d.Long("modId").Value))
                    .Select(d => d.Long("modId").Value).ToList();
        }

        public List<long> Update(AddonEntry entry, Progress progress, string zipPath = null)
        {
            if (entry.Mod == null || entry.LatestFile == null)
                throw new ApiError("No " + Games.Short(Profile) + " file to install for " + entry.Name);
            // folders we found by fingerprint get replaced too, not just the ones we installed
            var info = State.InfoOrNew(entry.ModId.Value);
            var all = J.List(info.Get("folders")).Select(x => Convert.ToString(x)).Union(entry.Folders)
                       .Distinct().OrderBy(f => f.ToLowerInvariant(), StringComparer.Ordinal);
            info["folders"] = all.Cast<object>().ToList();
            return Install(entry.Mod, entry.LatestFile, progress, zipPath);
        }

        // next to AddOns, not inside it, so the game never tries to load the backups
        public string BackupRoot() => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(AddonsDir).TrimEnd('\\', '/')), "AddOns_Backups");

        static string Stamp() => DateTime.Now.ToString("yyyyMMdd-HHmmss");

        static string SafeLabel(string label, int max)
        {
            string s = Regex.Replace(label ?? "", "[^A-Za-z0-9._-]+", "_");
            return s.Length > max ? s.Substring(0, max) : s;
        }

        void SwapIn(string label, string extractDir, List<string> newFolders, List<string> oldFolders)
        {
            var targets = new Dictionary<string, string>();
            foreach (string f in oldFolders.Concat(newFolders)) targets[f.ToLowerInvariant()] = f;
            var existing = targets.Values.Select(f => Path.Combine(AddonsDir, f)).Where(Directory.Exists).ToList();
            string backup = Path.Combine(BackupRoot(), Stamp() + "_" + SafeLabel(label, 60));
            var moved = new List<Tuple<string, string>>();
            var placed = new List<string>();
            try
            {
                if (existing.Count > 0)
                {
                    Directory.CreateDirectory(backup);
                    foreach (string src in existing)
                    {
                        string dst = Path.Combine(backup, Path.GetFileName(src));
                        Files.MoveDir(src, dst);
                        moved.Add(Tuple.Create(dst, src));
                    }
                }
                foreach (string f in newFolders)
                {
                    string target = Path.Combine(AddonsDir, f);
                    Files.MoveDir(Path.Combine(extractDir, f), target);
                    placed.Add(target);
                }
            }
            catch (Exception)
            {
                // something went wrong halfway, so put everything back the way it was
                foreach (string p in placed) Files.DeleteTree(p);
                foreach (var m in moved)
                    if (Directory.Exists(m.Item1) && !Directory.Exists(m.Item2)) Files.MoveDir(m.Item1, m.Item2);
                throw;
            }
            if (existing.Count > 0 && !J.Truthy(Config["keep_backups"])) Files.DeleteTree(backup);
            PruneBackups();
        }

        void PruneBackups()
        {
            string root = BackupRoot();
            if (!Directory.Exists(root)) return;
            long keep = J.ToLong(Config["max_backups"]) ?? 15;
            if (keep <= 0) keep = 15;
            var dirs = Directory.GetDirectories(root).Select(Path.GetFileName).OrderByDescending(d => d, StringComparer.Ordinal).ToList();
            foreach (string d in dirs.Skip((int)keep)) Files.DeleteTree(Path.Combine(root, d));
            try
            {
                if (!Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
        }

        public void Uninstall(AddonEntry entry)
        {
            var existing = entry.Folders.Where(f => Directory.Exists(Path.Combine(AddonsDir, f))).ToList();
            if (existing.Count > 0)
            {
                string backup = Path.Combine(BackupRoot(), Stamp() + "_removed_" + SafeLabel(entry.Name, 50));
                Directory.CreateDirectory(backup);
                foreach (string f in existing) Files.MoveDir(Path.Combine(AddonsDir, f), Path.Combine(backup, f));
                if (!J.Truthy(Config["keep_backups"])) Files.DeleteTree(backup);
                PruneBackups();
            }
            if (entry.ModId.HasValue) State.Addons.Remove(entry.ModId.Value.ToString());
            foreach (string f in entry.Folders) State.FpCache.Remove(f);
            State.Save();
        }

        public void Link(List<string> folders, Dictionary<string, object> mod)
        {
            string target = Convert.ToString(mod.Long("id"));
            foreach (string mid in State.Addons.Keys.ToList())   // a folder can only belong to one project
            {
                var info = J.Obj(State.Addons[mid]);
                if (info == null) continue;
                var left = J.List(info.Get("folders")).Select(x => Convert.ToString(x)).Where(f => !folders.Contains(f)).ToList();
                info["folders"] = left.Cast<object>().ToList();
                if (left.Count == 0 && mid != target) State.Addons.Remove(mid);
            }
            var mine = State.InfoOrNew(target);
            var merged = J.List(mine.Get("folders")).Select(x => Convert.ToString(x)).Union(folders).Distinct().OrderBy(f => f, StringComparer.Ordinal);
            mine["name"] = mod.Get("name");
            mine["folders"] = merged.Cast<object>().ToList();
            mine["fileId"] = null;
            mine["source"] = "manual";
            mine["websiteUrl"] = J.ObjOrEmpty(mod.Get("links")).Str("websiteUrl");
            State.Save();
        }

        public void Unlink(AddonEntry entry)
        {
            if (!entry.ModId.HasValue) return;
            State.Addons.Remove(entry.ModId.Value.ToString());
            State.Save();
        }

        // Let one addon follow beta or alpha builds without changing the setting for everything
        public void SetChannel(AddonEntry entry, long? channel)
        {
            if (!entry.ModId.HasValue) return;
            var info = State.InfoOrNew(entry.ModId.Value, () => J.D("folders", entry.Folders.Cast<object>().ToList()));
            if (channel.HasValue && channel.Value > AppInfo.Release) info["channel"] = channel.Value;
            else info.Remove("channel");
            State.Save();
        }

        public void SetIgnored(AddonEntry entry, bool ignored)
        {
            if (!entry.ModId.HasValue) return;
            var info = State.InfoOrNew(entry.ModId.Value, () => J.D("folders", entry.Folders.Cast<object>().ToList()));
            info["ignored"] = ignored;
            State.Save();
        }
    }
}
