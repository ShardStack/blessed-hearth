// The app's name, where it keeps its files, the games it knows, the themes, and the two files it
// remembers things in: config.json (your settings) and one state file per AddOns folder.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace BlessedHearth
{
    public static class AppInfo
    {
        public const string Name = "Blessed Hearth";   // in full, "Blessed Hearth - A Not So Cursed Client"
        public const string Version = "2.0.1";
        public const long WowGameId = 1;
        public static readonly string UserAgent = "BlessedHearth/" + Version + " (personal addon updater)";

        public static bool IsWindows => Environment.OSVersion.Platform == PlatformID.Win32NT;

        // The test suite points the app at a pretend CurseForge running on this PC
        public static string TestApiBase => Environment.GetEnvironmentVariable("HEARTH_API_BASE") ?? "";
        public static string ApiBase => TestApiBase != "" ? TestApiBase : "https://api.curseforge.com";

        // CurseForge numbers its release types 1, 2 and 3
        public const int Release = 1, Beta = 2, Alpha = 3;
        public static string ReleaseName(object type)
        {
            switch (J.ToLong(type) ?? 0)
            {
                case 1: return "Release";
                case 2: return "Beta";
                case 3: return "Alpha";
                default: return "";
            }
        }
        public const int RequiredDependency = 3;   // in a file's dependency list, relationType 3 means "required"

        // Hard limits so a broken or nasty file can't fill up the drive
        public const long MaxDownloadBytes = 512L * 1024 * 1024;
        public static long MaxUnzippedBytes = 1024L * 1024 * 1024;   // not a constant only so the tests can shrink it
        public const int MaxZipEntries = 20000;
    }

    // Where the app keeps its stuff

    public static class AppPaths
    {
        public const string DataFolder = "BlessedHearth";
        public const string OldDataFolder = "NotSoCursedForge";   // what the folders were called back when the app was Not So Cursed Forge

        static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // Settings, the saved state of each AddOns folder and the log. %APPDATA%\BlessedHearth on Windows.
        // The Python version of the app used the very same folder, so switching over keeps everything.
        public static string AppDataDir()
        {
            string bas;
            if (AppInfo.IsWindows)
                bas = Environment.GetEnvironmentVariable("APPDATA") ?? Home;
            else
                bas = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? Path.Combine(Home, ".config");
            string path = Path.Combine(bas, DataFolder), old = Path.Combine(bas, OldDataFolder);
            if (!Directory.Exists(path) && !File.Exists(path) && Directory.Exists(old))
            {
                // Coming from before the rename. Just rename the folder so every setting (and the saved
                // API key) comes along. If Windows won't let go of it right now, keep using the old one.
                try { Directory.Move(old, path); }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return old; }
            }
            Directory.CreateDirectory(path);
            return path;
        }

        // The bigger stuff that doesn't need to follow you between PCs: the window's browser data
        // and the CurseForge pane's cookies. %LOCALAPPDATA%\BlessedHearth on Windows.
        public static string LocalDataDir()
        {
            string bas = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? Path.Combine(Home, ".local", "share");
            string path = Path.Combine(bas, DataFolder), old = Path.Combine(bas, OldDataFolder);
            Directory.CreateDirectory(path);
            if (Directory.Exists(old))
            {
                // Things move over one at a time instead of renaming the whole folder, because the new
                // folder already exists by now. The old unpacked app gets thrown away, and so does
                // anything that already has a newer copy over here.
                foreach (string src in Directory.GetFileSystemEntries(old))
                {
                    string name = Path.GetFileName(src), dst = Path.Combine(path, name);
                    try
                    {
                        if (name == "app" || Directory.Exists(dst) || File.Exists(dst))
                        {
                            if (Directory.Exists(src)) Files.DeleteTree(src);
                            else File.Delete(src);
                        }
                        else if (Directory.Exists(src)) Directory.Move(src, dst);
                        else File.Move(src, dst);
                    }
                    catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
                }
                try { Directory.Delete(old); } catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }   // only goes if it's empty now
            }
            // The Python version's window engine (Qt) kept caches of its own in folders named after
            // the app's old display name. Nothing in them is worth keeping, so they just go.
            foreach (string qtBase in new[] { bas, Environment.GetEnvironmentVariable("APPDATA") })
            {
                if (!string.IsNullOrEmpty(qtBase) && Directory.Exists(Path.Combine(qtBase, "Not So Cursed Forge")))
                    Files.DeleteTree(Path.Combine(qtBase, "Not So Cursed Forge"));
            }
            return path;
        }

        // Windows special folders like Documents and Downloads. Asking Windows (instead of guessing
        // C:\Users\you\Documents) follows them when OneDrive or a custom location has moved them.
        public static string DocumentsDir() => KnownFolder("FDD39AD0-238F-46AF-ADB4-6C85480369C7", "Documents");
        public static string DownloadsDir() => KnownFolder("374DE290-123F-4565-9164-39C4925E467B", "Downloads");

        [System.Runtime.InteropServices.DllImport("shell32.dll")]
        static extern int SHGetKnownFolderPath([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPStruct)] Guid id,
                                               uint flags, IntPtr token, out IntPtr path);

        static string KnownFolder(string guid, string fallback)
        {
            if (AppInfo.IsWindows)
            {
                try
                {
                    IntPtr p;
                    if (SHGetKnownFolderPath(new Guid(guid), 0, IntPtr.Zero, out p) == 0)
                    {
                        string path = System.Runtime.InteropServices.Marshal.PtrToStringUni(p);
                        System.Runtime.InteropServices.Marshal.FreeCoTaskMem(p);
                        if (!string.IsNullOrEmpty(path) && Directory.Exists(path)) return path;
                    }
                }
                catch (Exception) { }
            }
            return Path.Combine(Home, fallback);
        }

        // The same thing Python's os.path.normcase(os.path.abspath(p)) gives. The state files are
        // named after this, so it has to match exactly for the Python version's files to carry over.
        public static string NormCase(string path)
        {
            string full;
            try { full = Path.GetFullPath(string.IsNullOrEmpty(path) ? "." : path); }
            catch (Exception) { full = path ?? ""; }
            string root = Path.GetPathRoot(full) ?? "";
            if (full.Length > root.Length) full = full.TrimEnd('\\', '/');
            return AppInfo.IsWindows ? full.Replace('/', '\\').ToLowerInvariant() : full;
        }

        public static bool SamePath(string a, string b) => NormCase(a) == NormCase(b);
    }

    public static class Files
    {
        public static object ReadJson(string path, object fallback)
        {
            try { return Json.Parse(File.ReadAllText(path, Encoding.UTF8)); }
            catch (Exception) { return fallback; }
        }

        public static void WriteJson(string path, object data)
        {
            // write to a temp file first and swap it in, so a crash mid-save can't leave half a file
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, Json.Write(data, indent: true), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }

        public static string ReadText(string path)
        {
            try
            {
                // utf-8, a byte order mark is skipped, and broken bytes become a placeholder instead of an error
                return new UTF8Encoding(false, false).GetString(File.ReadAllBytes(path)).TrimStart('\uFEFF');
            }
            catch (Exception) { return ""; }
        }

        public static void DeleteTree(string path)
        {
            // like rm -rf, ignoring anything that won't go. Read-only files get unlocked first.
            try
            {
                if (!Directory.Exists(path)) return;
                foreach (string f in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(f, FileAttributes.Normal); } catch (Exception) { }
                }
                Directory.Delete(path, true);
            }
            catch (Exception) { }
        }

        // Moves a folder, even onto another drive (Directory.Move can't do that one)
        public static void MoveDir(string src, string dst)
        {
            try
            {
                Directory.Move(src, dst);
                return;
            }
            catch (IOException) when (!SameVolume(src, dst)) { }
            CopyDir(src, dst);
            DeleteTree(src);
        }

        static bool SameVolume(string a, string b) =>
            string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

        static void CopyDir(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (string f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)));
            foreach (string d in Directory.GetDirectories(src)) CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
        }

        public static string Sha1Hex(byte[] data)
        {
            using (var sha = SHA1.Create()) return Hex(sha.ComputeHash(data));
        }

        public static string Hex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }

    // Windows' built-in per-user encryption (DPAPI). It's the same thing browsers use for saved
    // passwords: only this Windows account on this PC can turn the result back into the original.
    // Returns null anywhere that isn't Windows or if Windows says no. The Python version used the
    // exact same call, so a key it saved still opens here.
    public static class Dpapi
    {
        public static Func<byte[], bool, byte[]> Override;   // the tests stand in a fake for Windows' encryption

        public static byte[] Run(byte[] data, bool protect)
        {
            if (Override != null) return Override(data, protect);
            if (!AppInfo.IsWindows) return null;
            try
            {
                return protect ? ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser)
                               : ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser);
            }
            catch (Exception) { return null; }
        }
    }

    // The games the app handles. These are the MMOs CurseForge carries addons for. CurseForge's
    // numbers for them get looked up by slug when the app runs; FallbackId is only for offline.
    public class GameInfo
    {
        public string Id, Name, Short, Manifest, Logo;
        public string[] Slugs;
        public long? FallbackId;
        public bool Flavors;
    }

    public static class Games
    {
        public static readonly List<GameInfo> All = new List<GameInfo>
        {
            new GameInfo { Id = "wow", Name = "World of Warcraft", Short = "WoW", Slugs = new[] { "wow" }, FallbackId = 1,
                           Manifest = "toc", Flavors = true, Logo = "#B067FF" },
            new GameInfo { Id = "eso", Name = "The Elder Scrolls Online", Short = "ESO", Slugs = new[] { "teso", "elder-scrolls-online", "eso" },
                           FallbackId = 455, Manifest = "eso", Flavors = false, Logo = "#C9A86A" },
            new GameInfo { Id = "rift", Name = "Rift", Short = "Rift", Slugs = new[] { "rift" }, FallbackId = null,
                           Manifest = "rift", Flavors = false, Logo = "#4FC3D8" },
            new GameInfo { Id = "rom", Name = "Runes of Magic", Short = "Runes of Magic", Slugs = new[] { "rom", "runes-of-magic" }, FallbackId = null,
                           Manifest = "toc", Flavors = false, Logo = "#E25B5B" },
        };

        public static GameInfo Get(string id) => All.FirstOrDefault(g => g.Id == id);
        public static GameInfo GetOrWow(string id) => Get(id) ?? All[0];

        // What a game install is called in the app, like "WoW Forever" or "Rift"
        public static string Label(Dictionary<string, object> p)
        {
            if (p.Str("game") == "wow")
            {
                string n = p.Str("flavorName");
                return n != "" ? n : "World of Warcraft";
            }
            var g = Get(p.Str("game"));
            return g != null ? g.Name : "Game";
        }

        // The short name for sentences like "No Forever version yet"
        public static string Short(Dictionary<string, object> p)
        {
            if (p.Str("game") == "wow")
            {
                string n = Regex.Replace(p.Str("flavorName"), @"^(world of warcraft|wow)[:\s-]*", "", RegexOptions.IgnoreCase).Trim();
                return n != "" ? n : "WoW";
            }
            var g = Get(p.Str("game"));
            return g != null ? g.Short : "this game";
        }

        public static Dictionary<string, object> NewProfile(string game, string addonsDir, long? flavorId = null, string flavorName = "", long? gameId = null)
        {
            string seed = game + "|" + addonsDir + "|" + DateTime.UtcNow.Ticks + "|" + Guid.NewGuid();
            var g = Get(game);
            var p = J.D("id", "p" + Files.Sha1Hex(Encoding.UTF8.GetBytes(seed)).Substring(0, 8),
                        "game", game, "gameId", (object)(gameId ?? g?.FallbackId),
                        "flavorId", (object)flavorId, "flavorName", flavorName ?? "", "addonsDir", addonsDir);
            p["label"] = Label(p);
            return p;
        }
    }

    // The colors the native window paints itself (its edge, the CurseForge pane, the window buttons).
    // The interface's own palettes are in ui/index.html. These just make the window match.
    public class Theme
    {
        public string Id, Name, Bg, Edge, Pane, Head, Text, Muted, Accent, Step;
        public int[] Line;
    }

    public static class Themes
    {
        public static readonly List<Theme> All = new List<Theme>
        {
            new Theme { Id = "arcane", Name = "Arcane", Bg = "#130E1B", Edge = "#3A2C52", Pane = "#1A1326", Head = "#211830",
                        Line = new[] { 196, 164, 255 }, Text = "#F2EDF8", Muted = "#A99FBA", Accent = "#B067FF", Step = "#C99BFF" },
            new Theme { Id = "fel", Name = "Fel", Bg = "#0B120E", Edge = "#24402E", Pane = "#101A14", Head = "#16241B",
                        Line = new[] { 176, 240, 170 }, Text = "#EEF6EF", Muted = "#9DB2A3", Accent = "#76E84C", Step = "#A9F38E" },
            new Theme { Id = "crimson", Name = "Crimson", Bg = "#140B0E", Edge = "#4A2430", Pane = "#1B1014", Head = "#261419",
                        Line = new[] { 255, 176, 186 }, Text = "#F8EDEF", Muted = "#BBA0A6", Accent = "#EC485C", Step = "#FF8FA0" },
            new Theme { Id = "frost", Name = "Frost", Bg = "#0A1017", Edge = "#24384E", Pane = "#0F1720", Head = "#16212E",
                        Line = new[] { 170, 215, 255 }, Text = "#EEF5FB", Muted = "#9DB0C2", Accent = "#54C4FF", Step = "#8FD8FF" },
            new Theme { Id = "dark", Name = "Dark", Bg = "#0A0A0B", Edge = "#2A2A2E", Pane = "#111113", Head = "#18181B",
                        Line = new[] { 255, 255, 255 }, Text = "#EDEDEF", Muted = "#A1A1AA", Accent = "#A78BFA", Step = "#C4B5FD" },
            new Theme { Id = "forever", Name = "Forever", Bg = "#0B0A0E", Edge = "#4A3A26", Pane = "#121014", Head = "#1C1717",
                        Line = new[] { 176, 138, 74 }, Text = "#F3EBDD", Muted = "#B5A796", Accent = "#C42A1E", Step = "#8FD4F2" },
        };

        public static string Id(object value)
        {
            var s = value as string;
            return s != null && All.Any(t => t.Id == s) ? s : "arcane";
        }

        public static Theme Get(object value) => All.First(t => t.Id == Id(value));
    }

    public class Config
    {
        // Version 1.0 only knew about one AddOns folder and kept these at the top level.
        // They live on the selected game install now, but the old names still work.
        static readonly Dictionary<string, string> Legacy = new Dictionary<string, string>
        {
            { "addons_dir", "addonsDir" }, { "flavor_id", "flavorId" }, { "flavor_name", "flavorName" },
        };

        public static Dictionary<string, object> Defaults() => J.D(
            "api_key", "",
            "profiles", new List<object>(),   // one per game install (WoW Forever, WoW Retail, ESO and so on)
            "active", null,                   // id of the one that's selected
            "channel", (long)AppInfo.Release, // newest release type to accept: 1 release, 2 beta, 3 alpha
            "keep_backups", true,
            "max_backups", 15L,
            "theme", "arcane",
            "tray", false,                    // closing the window keeps the app running in the system tray
            "start_minimized", false,         // when Windows starts it at sign-in, don't pop the window up
            "tray_hint_shown", false,
            "check_hours", 6L,                // background update check while the app runs, 0 means off
            "last_auto_check", 0L,
            "notified", new List<object>());  // updates already announced ("profile|addon|file") so each one only pops up once

        public readonly string Path;
        public Dictionary<string, object> Data;
        string key = "";
        readonly object saveLock = new object();

        public Config(string path = null)
        {
            Path = path ?? System.IO.Path.Combine(AppPaths.AppDataDir(), "config.json");
            Data = Defaults();
            var saved = Files.ReadJson(Path, null) as Dictionary<string, object>;
            if (saved != null) foreach (var kv in saved) Data[kv.Key] = kv.Value;
            Migrate();
        }

        void Migrate()
        {
            if (!J.Truthy(Data.Get("profiles")) && Data.Str("addons_dir") != "")
            {
                var p = Games.NewProfile("wow", Data.Str("addons_dir"), Data.Long("flavor_id"), Data.Str("flavor_name"));
                Data["profiles"] = new List<object> { p };
                Data["active"] = p["id"];
            }
            foreach (string k in Legacy.Keys) Data.Remove(k);
            // The API key is stored encrypted on Windows (see Dpapi). Older versions saved it as
            // plain text, so encrypt that one the next time the config gets saved.
            string locked = Data.Str("api_key_protected");
            if (locked != "")
            {
                byte[] raw;
                try { raw = Dpapi.Run(Convert.FromBase64String(locked), protect: false); }
                catch (FormatException) { raw = null; }   // the file got mangled somehow, so it's like having no key saved
                key = raw != null ? new UTF8Encoding(false, false).GetString(raw) : "";
            }
            else
            {
                key = Data.Str("api_key").Trim();
                if (key != "") StoreKey(key);
            }
        }

        void StoreKey(string k)
        {
            key = k;
            byte[] locked = k != "" ? Dpapi.Run(Encoding.UTF8.GetBytes(k), protect: true) : null;
            if (locked != null)
            {
                Data["api_key_protected"] = Convert.ToBase64String(locked);
                Data["api_key"] = "";
            }
            else   // not on Windows (or Windows refused), so it's plain text like before
            {
                Data.Remove("api_key_protected");
                Data["api_key"] = k;
            }
        }

        public object this[string k]
        {
            get
            {
                if (k == "api_key") return ApiKey;
                string lk;
                if (Legacy.TryGetValue(k, out lk))
                {
                    var p = ActiveProfile();
                    return p?.Get(lk);
                }
                return Data.Get(k);
            }
            set
            {
                if (k == "api_key")
                {
                    StoreKey(((value as string) ?? "").Trim());
                    return;
                }
                string lk;
                if (Legacy.TryGetValue(k, out lk))
                {
                    var p = ActiveProfile();
                    if (p == null)
                    {
                        p = Games.NewProfile("wow", "");
                        Profiles.Add(p);
                        Data["active"] = p["id"];
                    }
                    p[lk] = value;
                    p["label"] = Games.Label(p);
                    return;
                }
                Data[k] = value;
            }
        }

        public string Str(string k) => (this[k] as string) ?? "";

        public List<object> Profiles
        {
            get
            {
                var list = Data.Get("profiles") as List<object>;
                if (list == null) { list = new List<object>(); Data["profiles"] = list; }
                return list;
            }
        }

        public IEnumerable<Dictionary<string, object>> ProfileList => Profiles.OfType<Dictionary<string, object>>();

        public Dictionary<string, object> Profile(string pid) => pid == null ? null : ProfileList.FirstOrDefault(p => p.Str("id") == pid);

        public Dictionary<string, object> ActiveProfile() => Profile(Data.Get("active") as string) ?? ProfileList.FirstOrDefault();

        public Dictionary<string, object> AddProfile(Dictionary<string, object> p, bool activate = true)
        {
            // adding a folder that's already in the list just updates that entry
            var same = ProfileList.FirstOrDefault(q => AppPaths.SamePath(q.Str("addonsDir", "."), p.Str("addonsDir", ".")));
            if (same != null && p.Str("addonsDir") != "")
            {
                foreach (var kv in p) if (kv.Key != "id") same[kv.Key] = kv.Value;
                p = same;
            }
            else Profiles.Add(p);
            if (activate) Data["active"] = p["id"];
            return p;
        }

        public void RemoveProfile(string pid)
        {
            Data["profiles"] = Profiles.Where(p => (p as Dictionary<string, object>).Str("id") != pid).ToList();
            if (Data.Get("active") as string == pid)
                Data["active"] = ProfileList.FirstOrDefault()?.Get("id");
        }

        public string ApiKey
        {
            get
            {
                if (key != "") return key;
                string plain = Data.Str("api_key").Trim();
                if (plain != "") return plain;
                return (Environment.GetEnvironmentVariable("CURSEFORGE_API_KEY") ?? "").Trim();
            }
        }

        public void Save()
        {
            lock (saveLock) Files.WriteJson(Path, Data);
        }
    }

    // What the app remembers about one AddOns folder: which folders belong to which CurseForge
    // project, which file it installed, skip flags, and a cache so unchanged folders aren't re-hashed.
    public class State
    {
        public readonly string Path;
        readonly object saveLock = new object();
        public Dictionary<string, object> Addons;    // project id (as text) -> what we know about it
        public Dictionary<string, object> FpCache;   // folder name -> [change signature, fingerprints]

        public State(string addonsDir)
        {
            string key = Files.Sha1Hex(Encoding.UTF8.GetBytes(AppPaths.NormCase(addonsDir))).Substring(0, 12);
            Path = System.IO.Path.Combine(AppPaths.AppDataDir(), "state_" + key + ".json");
            var d = J.ObjOrEmpty(Files.ReadJson(Path, null));
            Addons = J.ObjOrEmpty(d.Get("addons"));
            FpCache = J.ObjOrEmpty(d.Get("fp_cache"));
        }

        public Dictionary<string, object> Info(object modId) => J.Obj(Addons.Get(Convert.ToString(modId)));

        public Dictionary<string, object> InfoOrNew(object modId, Func<Dictionary<string, object>> make = null)
        {
            string k = Convert.ToString(modId);
            var info = J.Obj(Addons.Get(k));
            if (info == null) { info = make != null ? make() : new Dictionary<string, object>(); Addons[k] = info; }
            return info;
        }

        public void Save()
        {
            lock (saveLock) Files.WriteJson(Path, J.D("addons", Addons, "fp_cache", FpCache));
        }

        public Dictionary<string, string> FolderOwner()
        {
            var owner = new Dictionary<string, string>();
            foreach (var kv in Addons)
                foreach (object f in J.List(J.Obj(kv.Value).Get("folders")))
                    owner[Convert.ToString(f).ToLowerInvariant()] = kv.Key;
            return owner;
        }
    }
}
