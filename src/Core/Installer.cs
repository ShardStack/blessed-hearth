// Checking and unpacking addon zips, turning CurseForge's HTML changelogs into plain text, and
// finding the games on this PC.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace BlessedHearth
{
    public static class Installer
    {
        // Make sure a zip is exactly the file CurseForge describes: same size and, when CurseForge
        // gives one, the same SHA-1 checksum. Catches half-finished downloads and anything tampered with.
        public static void VerifyDownload(Dictionary<string, object> file, string path)
        {
            long? wantLen = file.Long("fileLength");
            long size = new FileInfo(path).Length;
            if (wantLen.HasValue && wantLen.Value != 0 && size != wantLen.Value)
                throw new ApiError(Path.GetFileName(path) + " is " + size.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " bytes but CurseForge lists " + wantLen.Value.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)
                                   + ". It may be a different version or an unfinished download.");
            string sha1 = J.List(file.Get("hashes")).OfType<Dictionary<string, object>>()
                           .Where(h => h.Long("algo") == 1 && h.Str("value") != "").Select(h => h.Str("value")).FirstOrDefault();
            if (sha1 != null)
            {
                string got;
                using (var sha = SHA1.Create())
                using (var fs = File.OpenRead(path)) got = Files.Hex(sha.ComputeHash(fs));
                if (!string.Equals(got, sha1, StringComparison.OrdinalIgnoreCase))
                    throw new ApiError(Path.GetFileName(path) + " doesn't match CurseForge's checksum for this version.");
            }
        }

        // The CurseForge web page that downloads this exact file
        public static string WebsiteDownloadUrl(Dictionary<string, object> mod, Dictionary<string, object> file)
        {
            string bas = J.ObjOrEmpty(mod.Get("links")).Str("websiteUrl").TrimEnd('/');
            return bas != "" ? bas + "/download/" + file.Long("id") : "";
        }

        // Changelogs come from CurseForge as HTML. We only ever show them as plain text, never as live HTML.
        public static string HtmlToText(string html, int limit = 6000)
        {
            string t = Regex.Replace(html ?? "", @"<(script|style)[^>]*>.*?</\1>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            t = Regex.Replace(t, @"<br\s*/?>|</p>|</div>|</h[1-6]>|</tr>|</li>|</ul>|</ol>|<p[^>]*>", "\n", RegexOptions.IgnoreCase);
            t = Regex.Replace(t, "<li[^>]*>", "\n- ", RegexOptions.IgnoreCase);
            t = Regex.Replace(t, "<h[1-6][^>]*>", "\n", RegexOptions.IgnoreCase);
            t = Regex.Replace(t, "<[^>]+>", "");
            t = WebUtility.HtmlDecode(t).Replace("\r", "");
            t = Regex.Replace(t, "[ \t]+\n", "\n");
            t = Regex.Replace(t, @"\n{3,}", "\n\n");
            t = Regex.Replace(t, @"\n\n(?=- )", "\n").Trim();   // keep list items together
            return t.Length > limit ? t.Substring(0, limit) + "..." : t;
        }

        // Unzip an addon. Refuses anything that tries to write outside `dest` or that would unpack to
        // something absurdly big. Returns the top level folder names, which are the addon folders.
        public static List<string> SafeExtract(string zipPath, string dest)
        {
            Directory.CreateDirectory(dest);
            string destAbs = Path.GetFullPath(dest).TrimEnd(Path.DirectorySeparatorChar);
            ZipArchive zf;
            try { zf = ZipFile.OpenRead(zipPath); }
            catch (InvalidDataException) { throw new ApiError("The downloaded file is not a valid zip archive"); }
            var tops = new Dictionary<string, string>();
            var topOrder = new List<string>();
            using (zf)
            {
                var entries = zf.Entries.ToList();
                if (entries.Count > AppInfo.MaxZipEntries || entries.Sum(e => e.Length) > AppInfo.MaxUnzippedBytes)
                    throw new ApiError("That zip unpacks to far more than any addon should, so it wasn't installed.");
                foreach (var entry in entries)
                {
                    string name = entry.FullName.Replace('\\', '/');
                    if (name.Trim('/') == "" || name.StartsWith("__MACOSX/")) continue;
                    var parts = name.Split('/').Where(p => p != "").ToList();
                    bool isDir = name.EndsWith("/");
                    if (name.StartsWith("/") || Regex.IsMatch(name, "^[A-Za-z]:") || parts.Contains(".."))
                        throw new ApiError("Unsafe path in zip: " + entry.FullName);
                    if (parts.Count == 1 && !isDir) continue;   // a stray file in the zip's root (readme and such), not an addon folder
                    string top = parts[0].ToLowerInvariant();
                    if (!tops.ContainsKey(top)) { tops[top] = parts[0]; topOrder.Add(top); }
                    string target;
                    try { target = Path.GetFullPath(Path.Combine(new[] { dest }.Concat(parts).ToArray())); }
                    catch (Exception e) when (e is ArgumentException || e is NotSupportedException || e is PathTooLongException)
                    { throw new ApiError("Unsafe path in zip: " + entry.FullName); }
                    if (!(target == destAbs || target.StartsWith(destAbs + Path.DirectorySeparatorChar)))
                        throw new ApiError("Unsafe path in zip: " + entry.FullName);
                    if (isDir) Directory.CreateDirectory(target);
                    else
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        using (var src = entry.Open())
                        using (var outFile = File.Create(target)) src.CopyTo(outFile);
                    }
                }
            }
            if (tops.Count == 0) throw new ApiError("The zip contained no addon folders");
            return tops.Values.OrderBy(v => v.ToLowerInvariant(), StringComparer.Ordinal).ToList();
        }
    }

    // Finding games on this PC
    public static class Detect
    {
        // Install folders Windows knows about from the Battle.net, Gameforge and Trion registry keys
        static List<string> RegistryInstallPaths()
        {
            var outList = new List<string>();
            if (!AppInfo.IsWindows) return outList;
            string[] keys =
            {
                @"SOFTWARE\WOW6432Node\Blizzard Entertainment\World of Warcraft",
                @"SOFTWARE\Blizzard Entertainment\World of Warcraft",
                @"SOFTWARE\WOW6432Node\Frogster Interactive Pictures\Runes of Magic",
                @"SOFTWARE\WOW6432Node\Gameforge4d\Runes of Magic",
                @"SOFTWARE\WOW6432Node\Trion Worlds\RIFT",
            };
            foreach (var hive in new[] { Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser })
                foreach (string k in keys)
                {
                    try
                    {
                        using (var h = hive.OpenSubKey(k))
                        {
                            if (h == null) continue;
                            foreach (string val in new[] { "InstallPath", "GamePath", "Path", "InstallLocation" })
                            {
                                string v = h.GetValue(val) as string;
                                if (!string.IsNullOrEmpty(v) && Directory.Exists(v)) outList.Add(v.TrimEnd('\\', '/'));
                            }
                        }
                    }
                    catch (Exception) { }
                }
            return outList;
        }

        // Battle.net writes each WoW version's product code (wow, wow_classic_era and so on) into a
        // tiny text file called .flavor.info in that version's folder. That's how we tell them apart.
        public static string WowProduct(string flavorDir)
        {
            string text = Files.ReadText(Path.Combine(flavorDir, ".flavor.info"));
            var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l != "" && !l.Contains("!")).ToList();
            if (lines.Count > 0) return lines[lines.Count - 1].ToLowerInvariant();
            return Path.GetFileName(flavorDir.TrimEnd('\\', '/')).Trim('_').ToLowerInvariant();
        }

        // product code or folder name -> words to look for in CurseForge's version names, best first
        static readonly List<Tuple<string[], string[]>> WowFlavorHints = new List<Tuple<string[], string[]>>
        {
            Tuple.Create(new[] { "forever" }, new[] { "forever" }),
            Tuple.Create(new[] { "classic_beta" }, new[] { "forever", "beta" }),
            Tuple.Create(new[] { "classic_era", "classic_era_ptr", "vanilla", "hardcore", "sod" }, new[] { "classic era", "era", "vanilla", "classic" }),
            Tuple.Create(new[] { "anniversary" }, new[] { "anniversary", "tbc", "burning crusade" }),
            Tuple.Create(new[] { "classic", "classic_ptr" }, new[] { "mists", "pandaria", "cataclysm", "cata", "wrath", "classic" }),
            Tuple.Create(new[] { "retail", "wow", "wow_beta", "beta", "ptr", "xptr", "wow_ptr" }, new[] { "retail", "mainline" }),
        };

        // Best guess at CurseForge's game version for a WoW install, from its product code
        public static Dictionary<string, object> GuessWowFlavor(string product, List<Dictionary<string, object>> types)
        {
            string prod = (product ?? "").ToLowerInvariant().Replace("wow_", "");
            if (prod == "") prod = "retail";
            foreach (var hint in WowFlavorHints)
            {
                if (!hint.Item1.Contains(prod)) continue;
                foreach (string w in hint.Item2)
                {
                    var hits = types.Where(t => (t.Str("name") + " " + t.Str("slug")).ToLowerInvariant().Contains(w)).ToList();
                    if (w == "classic")   // plain "classic" shouldn't grab Era, Forever or Anniversary by accident
                        hits = hits.Where(t => !Regex.IsMatch(t.Str("name"), "forever|anniversary|retail", RegexOptions.IgnoreCase)).ToList();
                    if (hits.Count > 0) return hits.OrderByDescending(t => t.Long("id") ?? 0).First();
                }
            }
            return null;
        }

        static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // Find the AddOns folders for every supported game on this PC, each WoW version on its own.
        // Each one comes back as {game, addonsDir, product, where}.
        public static List<Dictionary<string, object>> Installs()
        {
            var found = new List<Dictionary<string, object>>();
            var seen = new HashSet<string>();
            Action<string, string, string, string> add = (game, path, product, where) =>
            {
                string key = AppPaths.NormCase(path);
                if (Directory.Exists(path) && !seen.Contains(key))
                {
                    seen.Add(key);
                    found.Add(J.D("game", game, "addonsDir", path, "product", product ?? "", "where", where ?? ""));
                }
            };

            // World of Warcraft keeps each version in <install>\_flavor_\Interface\AddOns
            var wowRoots = new List<string>();
            if (AppInfo.IsWindows)
            {
                foreach (char drive in "CDEFGH")
                    foreach (string sub in new[] { @"Program Files (x86)\World of Warcraft", @"Program Files\World of Warcraft",
                                                   @"World of Warcraft", @"Games\World of Warcraft", @"Battle.net\World of Warcraft",
                                                   @"Blizzard\World of Warcraft", @"Program Files (x86)\Battle.net\World of Warcraft" })
                        wowRoots.Add(drive + @":\" + sub);
                wowRoots.AddRange(RegistryInstallPaths().Where(p => p.ToLowerInvariant().Contains("warcraft")));
                wowRoots = wowRoots.Select(p => Path.GetFileName(p).StartsWith("_") ? Path.GetDirectoryName(p) : p).ToList();
            }
            else wowRoots.Add(Path.Combine(Home, "Games", "World of Warcraft"));
            foreach (string root in wowRoots)
            {
                try
                {
                    foreach (string fdir in Directory.GetFileSystemEntries(root).OrderBy(x => x, StringComparer.Ordinal))
                    {
                        string flavor = Path.GetFileName(fdir);
                        if (flavor.StartsWith("_")) add("wow", Path.Combine(fdir, "Interface", "AddOns"), WowProduct(fdir), flavor);
                    }
                }
                catch (Exception) { }
            }

            string docs = AppPaths.DocumentsDir();
            // Elder Scrolls Online: Documents\Elder Scrolls Online\live\AddOns (pts is the test server)
            foreach (string server in new[] { "live", "liveeu", "pts" })
                add("eso", Path.Combine(docs, "Elder Scrolls Online", server, "AddOns"), server, server);
            // Rift: Documents\RIFT\Interface\AddOns
            foreach (string name in new[] { "AddOns", "Addons" })
                add("rift", Path.Combine(docs, "RIFT", "Interface", name), "", "");
            // Runes of Magic: <install>\Interface\AddOns
            var romRoots = RegistryInstallPaths().Where(p => p.ToLowerInvariant().Contains("runes")).ToList();
            if (AppInfo.IsWindows)
                foreach (char drive in "CDEF")
                    foreach (string sub in new[] { @"Program Files (x86)\Runes of Magic", @"Program Files\Runes of Magic",
                                                   @"Program Files (x86)\Gameforge\Runes of Magic", @"Gameforge\Runes of Magic",
                                                   @"Games\Runes of Magic", @"Runes of Magic" })
                        romRoots.Add(drive + @":\" + sub);
            foreach (string root in romRoots)
                foreach (string name in new[] { "AddOns", "Addons" })
                    add("rom", Path.Combine(root, "Interface", name), "", "");
            return found;
        }

        // For a folder you picked yourself: which game is it, and for WoW, its product code
        public static Tuple<string, string> GuessGameForFolder(string path)
        {
            string low = AppPaths.NormCase(path).ToLowerInvariant();
            char sep = Path.DirectorySeparatorChar;
            if (low.Contains("elder scrolls online")) return Tuple.Create("eso", "");
            if ((low + sep).Contains(sep + "rift" + sep)) return Tuple.Create("rift", "");
            if (low.Contains("runes of magic")) return Tuple.Create("rom", "");
            string full = Path.GetFullPath(path).TrimEnd('\\', '/');
            string flavorDir = Path.GetDirectoryName(Path.GetDirectoryName(full) ?? "") ?? "";   // ...\_retail_\Interface\AddOns
            return Tuple.Create("wow", Path.GetFileName(flavorDir).StartsWith("_") ? WowProduct(flavorDir) : "");
        }

        static readonly Dictionary<string, long> GameIdCache = new Dictionary<string, long>();

        // CurseForge's number for one of our games, looked up by its slug and remembered
        public static long? ResolveGameId(CurseForgeApi api, string game)
        {
            lock (GameIdCache)
            {
                long cached;
                if (GameIdCache.TryGetValue(game, out cached)) return cached;
            }
            var info = Games.Get(game);
            if (info == null) return null;
            try
            {
                foreach (var g in api.GamesList())
                {
                    if (info.Slugs.Contains(g.Str("slug")) || g.Str("name").ToLowerInvariant() == info.Name.ToLowerInvariant())
                    {
                        long id = g.Long("id") ?? 0;
                        lock (GameIdCache) GameIdCache[game] = id;
                        return id;
                    }
                }
            }
            catch (ApiError) { }
            return info.FallbackId;
        }
    }
}
