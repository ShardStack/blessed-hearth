// Reading .toc files (and ESO and Rift's versions of them).
//
// Every addon is just a folder with a description file in it. For WoW that's Foo.toc: a text file
// whose "## Title: ...", "## Version: ..." lines describe the addon, followed by the list of .lua
// and .xml files the game should load.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace BlessedHearth
{
    public class TocInfo
    {
        public string Path;
        public Dictionary<string, string> Fields;   // keys are lowercase

        public TocInfo(string path, Dictionary<string, string> fields)
        {
            Path = path;
            Fields = fields;
        }

        public string Get(string key, string fallback = "")
        {
            string v;
            return Fields.TryGetValue(key.ToLowerInvariant(), out v) ? v : fallback;
        }

        // WoW color codes and inline textures in addon titles, like |cff00ff00Green|r
        static readonly Regex ColorRe = new Regex(@"\|c[0-9a-fA-F]{8}|\|r|\|T[^|]*\|t|\|A[^|]*\|a");

        public string Title => Regex.Replace(ColorRe.Replace(Get("title"), "").Trim(), @"\s+", " ");

        // @project-version@ means the packager never filled it in
        public string Version => Get("version").Contains("@") ? "" : Get("version");

        public long? CurseId
        {
            get
            {
                string v = Get("x-curse-project-id");
                long id;
                return v != "" && v.All(char.IsDigit) && long.TryParse(v, out id) ? id : (long?)null;
            }
        }

        public List<string> Deps
        {
            get
            {
                string raw = string.Join(",", new[] { "dependencies", "requireddeps", "dep" }.Select(k => Get(k)));
                return raw.Split(',').Select(d => d.Trim()).Where(d => d != "").ToList();
            }
        }

        public HashSet<string> Interfaces
        {
            get
            {
                var set = new HashSet<string>();
                foreach (string x in Get("interface").Split(','))
                {
                    string t = x.Trim();
                    long n;
                    if (t != "" && t.All(char.IsDigit) && long.TryParse(t, out n) && n > 0) set.Add(t);
                }
                return set;
            }
        }
    }

    public static class Toc
    {
        // One addon can ship several TOCs (Foo_Mainline.toc, Foo_Vanilla.toc and so on).
        // This is the order to try them in when we don't know which WoW version it is.
        public static readonly string[] SuffixPref = { "_forever", "_camelot", "_mainline", "", "_classic", "_vanilla" };

        // Which TOC to read for the selected WoW version, best first. This follows Blizzard's own rule
        // (warcraft.wiki.gg "TOC format"): the game looks for its own suffix first, then its family
        // (_Mainline or _Classic), then plain Foo.toc.
        public static string[] Preferences(string flavorName)
        {
            string n = (flavorName ?? "").ToLowerInvariant();
            string[] order;
            if (n.Contains("forever")) order = new[] { "_forever", "_camelot", "_mainline" };   // Forever's TOCs also go by _Camelot (its codename)
            else if (n.Contains("retail") || n.Contains("mainline") || n.Contains("midnight")) order = new[] { "_standard", "_mainline" };
            else if (n.Contains("mists") || n.Contains("pandaria")) order = new[] { "_mists", "_classic" };
            else if (n.Contains("cata")) order = new[] { "_cata", "_classic" };
            else if (n.Contains("wrath") || n.Contains("wotlk") || n.Contains("titan")) order = new[] { "_wrath", "_wotlkc", "_classic" };
            else if (n.Contains("tbc") || n.Contains("burning") || n.Contains("anniversary")) order = new[] { "_tbc", "_bcc", "_classic", "_vanilla" };
            else if (n.Contains("classic") || n.Contains("era") || n.Contains("vanilla") || n.Contains("hardcore")) order = new[] { "_vanilla", "_classic" };
            else return SuffixPref;
            var outList = new List<string>();
            foreach (string suf in order) { outList.Add(suf); outList.Add(suf.Replace("_", "-")); }
            outList.Add("");
            return outList.ToArray();
        }

        public static TocInfo Parse(string path)
        {
            var fields = new Dictionary<string, string>();
            string text = File.Exists(path) ? Files.ReadText(path) : "";
            foreach (string rawLine in text.Split('\n'))
            {
                string line = rawLine.TrimEnd('\r');
                if (!line.StartsWith("##")) continue;
                int colon = line.IndexOf(':', 2);
                if (colon < 0) continue;
                string k = line.Substring(2, colon - 2).Trim().ToLowerInvariant(), v = line.Substring(colon + 1).Trim();
                if (!fields.ContainsKey(k)) fields[k] = v;
            }
            return new TocInfo(path, fields);
        }

        static Dictionary<string, string> NamesIn(string folder)
        {
            // lowercase name -> real name of everything directly inside a folder
            var d = new Dictionary<string, string>();
            foreach (string p in Directory.GetFileSystemEntries(folder))
            {
                string n = Path.GetFileName(p);
                d[n.ToLowerInvariant()] = n;
            }
            return d;
        }

        public static string Pick(string folderPath, string folderName, string[] prefs = null)
        {
            Dictionary<string, string> names;
            try { names = NamesIn(folderPath); }
            catch (Exception) { return null; }
            var tocs = names.Where(kv => kv.Key.EndsWith(".toc")).ToDictionary(kv => kv.Key, kv => kv.Value);
            if (tocs.Count == 0) return null;
            string bas = folderName.ToLowerInvariant();
            foreach (string suf in prefs ?? SuffixPref)
            {
                string cand;
                if (tocs.TryGetValue(bas + suf + ".toc", out cand)) return Path.Combine(folderPath, cand);
            }
            return Path.Combine(folderPath, tocs.Values.OrderBy(v => v, StringComparer.Ordinal).First());
        }

        // Elder Scrolls Online uses Foo/Foo.addon (or Foo.txt) with the same "## Key: value" lines as WoW
        public static TocInfo ParseEso(string path)
        {
            var info = Parse(path);
            var f = info.Fields;
            if (info.Get("version") == "" && info.Get("addonversion") != "") f["version"] = f["addonversion"];
            if (!f.ContainsKey("interface")) f["interface"] = info.Get("apiversion");
            if (info.Get("title") != "")   // ESO color codes are |cRRGGBB, 6 digits instead of WoW's 8
                f["title"] = Regex.Replace(f["title"], @"\|c[0-9a-fA-F]{6}|\|r", "");
            var deps = info.Get("dependson").Split((char[])null, StringSplitOptions.RemoveEmptyEntries)
                           .Select(d => Regex.Split(d, "[<>=]")[0]).ToList();
            if (deps.Count > 0) f["dependencies"] = string.Join(",", deps.Where(d => d != ""));
            return info;
        }

        // Rift's Foo/RiftAddon.toc is a little Lua table (Identifier = "Foo", Name = "..." and so on)
        public static TocInfo ParseRift(string path)
        {
            string text = Regex.Replace(Files.ReadText(path), @"--[^\n]*", "");
            Func<string, string> field = name =>
            {
                var m = Regex.Match(text, @"\b" + name + @"\s*=\s*(?:\{[^}]*?)?[""']([^""']*)[""']");
                return m.Success ? m.Groups[1].Value.Trim() : "";
            };
            var fields = new Dictionary<string, string>
            {
                { "title", field("Name") != "" ? field("Name") : field("Identifier") },
                { "version", field("Version") }, { "author", field("Author") },
                { "identifier", field("Identifier") }, { "notes", field("Description") },
            };
            var dm = Regex.Match(text, @"\bDependencies\s*=\s*\{");
            if (dm.Success)
            {
                int depth = 1, i = dm.Index + dm.Length;
                while (i < text.Length && depth > 0)   // walk to the matching closing brace (they nest)
                {
                    if (text[i] == '{') depth++;
                    else if (text[i] == '}') depth--;
                    i++;
                }
                string body = text.Substring(dm.Index + dm.Length, Math.Max(0, i - 1 - (dm.Index + dm.Length)));
                var names = Regex.Matches(body, @"(?:^|[,{\s])\[?[""']?([A-Za-z0-9_.-]+)[""']?\]?\s*=\s*\{")
                                 .Cast<Match>().Select(m => m.Groups[1].Value);
                fields["dependencies"] = string.Join(",", names);
            }
            return new TocInfo(path, fields.Where(kv => kv.Value != "").ToDictionary(kv => kv.Key, kv => kv.Value));
        }

        // Read an addon folder's description file for the given game. null means it isn't an addon.
        public static TocInfo ParseManifest(string game, string folderPath, string folderName, string flavorName = "", string interfaceHint = "")
        {
            string kind = Games.GetOrWow(game).Manifest;
            Dictionary<string, string> names;
            try { names = NamesIn(folderPath); }
            catch (Exception) { return null; }
            if (kind == "eso")
            {
                foreach (string cand in new[] { folderName + ".addon", folderName + ".txt" })
                {
                    string real;
                    if (names.TryGetValue(cand.ToLowerInvariant(), out real)) return ParseEso(Path.Combine(folderPath, real));
                }
                return null;
            }
            if (kind == "rift")
            {
                string real;
                return names.TryGetValue("riftaddon.toc", out real) ? ParseRift(Path.Combine(folderPath, real)) : null;
            }
            string tp = Pick(folderPath, folderName, game == "wow" ? Preferences(flavorName) : null);
            if (tp == null) return null;
            var info = Parse(tp);
            var others = names.Where(kv => kv.Key.EndsWith(".toc") && kv.Key.StartsWith(folderName.ToLowerInvariant())
                                           && Path.Combine(folderPath, kv.Value) != tp)
                              .Select(kv => Parse(Path.Combine(folderPath, kv.Value))).ToList();
            if (interfaceHint != "" && !info.Interfaces.Contains(interfaceHint))
            {
                // The game's interface number is in a different TOC (a suffix we don't know about yet).
                // That's the one the game actually loads, so read that one instead.
                var match = others.FirstOrDefault(o => o.Interfaces.Contains(interfaceHint));
                if (match != null) info = match;
            }
            if (info.CurseId == null)
            {
                // Questie is the classic example: its plain Questie.toc is a stub with no project ID,
                // but its _Camelot and _Vanilla TOCs have one.
                var withId = others.FirstOrDefault(o => o.CurseId != null);
                if (withId != null) info.Fields["x-curse-project-id"] = withId.Get("x-curse-project-id");
            }
            return info;
        }
    }
}
