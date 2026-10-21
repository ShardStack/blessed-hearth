// Fingerprints
//
// CurseForge recognizes an addon by a number per folder. It hashes the folder's files with
// MurmurHash2 (seed 1) after stripping every space, tab and line break. If we hash exactly the
// same files the same way, CurseForge can tell us "that's Questie, version 1.2.3".

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace BlessedHearth
{
    public static class Fingerprint
    {
        const uint M = 0x5BD1E995;

        public static uint Murmur2(byte[] data, uint seed = 1)
        {
            int length = data.Length;
            uint h = seed ^ (uint)length;
            int n4 = length / 4;
            for (int i = 0; i < n4; i++)
            {
                uint k = BitConverter.ToUInt32(data, i * 4);
                if (!BitConverter.IsLittleEndian) k = (k >> 24) | ((k >> 8) & 0xFF00) | ((k << 8) & 0xFF0000) | (k << 24);
                unchecked
                {
                    k *= M;
                    k ^= k >> 24;
                    k *= M;
                    h = (h * M) ^ k;
                }
            }
            int rem = length & 3, tail = n4 * 4;
            unchecked
            {
                if (rem == 3) h ^= (uint)data[tail + 2] << 16;
                if (rem >= 2) h ^= (uint)data[tail + 1] << 8;
                if (rem >= 1)
                {
                    h ^= data[tail];
                    h *= M;
                }
                h ^= h >> 13;
                h *= M;
                h ^= h >> 15;
            }
            return h;
        }

        public static uint OfFile(string path)
        {
            byte[] raw = File.ReadAllBytes(path);
            var kept = new byte[raw.Length];
            int n = 0;
            foreach (byte b in raw)
                if (b != 9 && b != 10 && b != 13 && b != 32) kept[n++] = b;
            Array.Resize(ref kept, n);
            return Murmur2(kept);
        }

        // Which files count, copied from how the CurseForge app scans WoW folders (WowUp worked this out):
        // the folder's own TOCs, every .lua and .xml they load, whatever those XML files include, and a
        // Bindings.xml in the root (hashed, but not followed).
        const string CfTocSuffixes = "mainline|bcc|tbc|classic|vanilla|wrath|wotlkc|cata|mists";
        // Newer suffixes the old rule didn't list. We can't know if CurseForge counts them yet,
        // so folders that have one get fingerprinted both ways.
        const string ExtraTocSuffixes = "forever|camelot|standard|plunderstorm|wowlabs|wowhack";
        static readonly Regex TocCommentRe = new Regex(@"\s*#.*$", RegexOptions.Multiline);
        static readonly Regex TocIncRe = new Regex(@"^\s*((?:(?<!\.\.).)+\.(?:xml|lua))\s*$", RegexOptions.IgnoreCase);
        static readonly Regex XmlCommentRe = new Regex("<!--.*?-->", RegexOptions.Singleline);
        static readonly Regex XmlIncRe = new Regex(@"<(?:Include|Script)\s+file=[""']((?:(?<!\.\.).)+)[""']\s*/>", RegexOptions.IgnoreCase | RegexOptions.Singleline);

        static bool BadPathChar(char c) => c == '|' || c < 32;

        static string Norm(string p) => Path.GetFullPath(p);

        // lowercase path -> real path, because addons reference files with whatever casing they like
        static Dictionary<string, string> FileIndex(string folder)
        {
            var idx = new Dictionary<string, string>();
            foreach (string f in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
            {
                string full = Norm(f);
                idx[full.ToLowerInvariant()] = full;
            }
            return idx;
        }

        public static List<string> FolderFiles(string folderPath, bool extraToc = false, bool anySuffix = false)
        {
            folderPath = Norm(folderPath).TrimEnd(Path.DirectorySeparatorChar);
            string name = Regex.Escape(Path.GetFileName(folderPath).ToLowerInvariant());
            string sufs = CfTocSuffixes + (extraToc ? "|" + ExtraTocSuffixes : "");
            var tocRe = new Regex(anySuffix ? "^" + name + @"([-_][a-z0-9]+)?\.toc$" : "^" + name + "([-|_](" + sufs + @"))?\.toc$",
                                  RegexOptions.IgnoreCase);
            var idx = FileIndex(folderPath);
            string rootLower = folderPath.ToLowerInvariant() + Path.DirectorySeparatorChar;
            var matching = new List<string>();
            var seen = new HashSet<string>();
            var tocs = new List<string>();
            foreach (var kv in idx)
            {
                if (!kv.Key.StartsWith(rootLower)) continue;
                string rel = kv.Key.Substring(rootLower.Length);
                if (rel.IndexOf(Path.DirectorySeparatorChar) >= 0) continue;
                if (tocRe.IsMatch(rel)) tocs.Add(kv.Value);
                else if (rel == "bindings.xml") { matching.Add(kv.Value); seen.Add(kv.Value); }
            }

            Action<string> visit = null;
            visit = path =>
            {
                string real;
                if (!idx.TryGetValue(Norm(path).ToLowerInvariant(), out real) || seen.Contains(real)) return;
                matching.Add(real);
                seen.Add(real);
                string ext = Path.GetExtension(real).ToLowerInvariant();
                if (ext != ".toc" && ext != ".xml") return;
                string text = Files.ReadText(real);
                Regex rx;
                if (ext == ".toc") { text = TocCommentRe.Replace(text, ""); rx = TocIncRe; }
                else { text = XmlCommentRe.Replace(text, ""); rx = XmlIncRe; }
                var includes = new List<string>();
                foreach (string line in text.Split('\n'))
                {
                    var m = rx.Match(line.Trim());
                    if (m.Success) includes.Add(m.Groups[1].Value.Trim());
                }
                string bas = Path.GetDirectoryName(real);
                foreach (string inc in includes)
                {
                    if (inc.Any(BadPathChar)) break;
                    string rel = inc.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
                    try { visit(Path.Combine(bas, rel)); }
                    catch (ArgumentException) { }   // a path Windows can't even represent
                    catch (NotSupportedException) { }
                }
            };
            foreach (string t in tocs) visit(t);
            return matching;
        }

        public static bool HasExtraToc(string folderPath)
        {
            string name = Path.GetFileName(Norm(folderPath).TrimEnd(Path.DirectorySeparatorChar)).ToLowerInvariant();
            string[] names;
            try { names = Directory.GetFileSystemEntries(folderPath).Select(p => Path.GetFileName(p).ToLowerInvariant()).ToArray(); }
            catch (Exception) { return false; }
            var sufs = ExtraTocSuffixes.Split('|');
            return names.Any(n => sufs.Any(s => n == name + "_" + s + ".toc" || n == name + "-" + s + ".toc"));
        }

        public static List<string> AllFiles(string folderPath)
        {
            var outList = new List<string>();
            foreach (string f in Directory.GetFiles(folderPath))
                if (!Path.GetFileName(f).StartsWith(".")) outList.Add(f);
            foreach (string d in Directory.GetDirectories(folderPath))
                if (!Path.GetFileName(d).StartsWith(".")) outList.AddRange(AllFiles(d));
            return outList;
        }

        // Same idea as the WoW rule, starting from another game's manifest: the manifest itself
        // plus every .lua and .xml it lists.
        public static List<string> ManifestFiles(string folderPath, string manifest)
        {
            var idx = FileIndex(folderPath);
            string real;
            if (!idx.TryGetValue(Norm(Path.Combine(folderPath, manifest)).ToLowerInvariant(), out real)) return new List<string>();
            var outList = new List<string> { real };
            foreach (string rawLine in Files.ReadText(real).Split('\n'))
            {
                string line = rawLine.Split('#')[0].Trim();
                foreach (Match m in Regex.Matches(line, @"[""']?([^\s""']+\.(?:lua|xml))[""']?", RegexOptions.IgnoreCase))
                {
                    string r = Regex.Replace(m.Groups[1].Value, @"\$\([^)]*\)", "en");   // ESO has $(language) placeholders in file names
                    string p;
                    try
                    {
                        if (idx.TryGetValue(Norm(Path.Combine(folderPath, r.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar))).ToLowerInvariant(), out p)
                            && !outList.Contains(p))
                            outList.Add(p);
                    }
                    catch (ArgumentException) { }
                }
            }
            return outList;
        }

        // The sets of files to fingerprint for one folder. For WoW (and Runes of Magic, which uses
        // WoW style .toc files) that's the CurseForge app's rule. For ESO and Rift nobody has published
        // the rule, so we send a few likely sets and keep whichever one CurseForge recognizes.
        public static List<List<string>> Variants(string game, string folderPath)
        {
            string kind = Games.GetOrWow(game).Manifest;
            if (kind == "toc")
            {
                var variants = new List<List<string>> { FolderFiles(folderPath) };
                if (HasExtraToc(folderPath)) variants.Add(FolderFiles(folderPath, extraToc: true));
                var every = FolderFiles(folderPath, anySuffix: true);   // every Foo_Something.toc, for suffixes nobody knows yet
                string key = string.Join("\n", every.OrderBy(x => x, StringComparer.Ordinal));
                if (!variants.Any(v => string.Join("\n", v.OrderBy(x => x, StringComparer.Ordinal)) == key)) variants.Add(every);
                return variants;
            }
            string name = Path.GetFileName(Norm(folderPath).TrimEnd(Path.DirectorySeparatorChar));
            var manifests = kind == "eso" ? new[] { name + ".addon", name + ".txt" } : new[] { "RiftAddon.toc" };
            var list = manifests.Select(m => ManifestFiles(folderPath, m)).ToList();
            list.Add(AllFiles(folderPath));
            return list.Where(v => v.Count > 0).ToList();
        }

        // A cheap "did anything change" check (file sizes and times) so we only re-hash folders that changed
        public static string Signature(IEnumerable<string> files)
        {
            using (var sha = SHA1.Create())
            {
                var all = new List<byte>();
                foreach (string f in files.Distinct().OrderBy(x => x, StringComparer.Ordinal))
                {
                    try
                    {
                        var fi = new FileInfo(f);
                        if (!fi.Exists) continue;
                        long mtime = (long)(fi.LastWriteTimeUtc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
                        all.AddRange(Encoding.UTF8.GetBytes(f + "|" + fi.Length + "|" + mtime));
                    }
                    catch (Exception) { }
                }
                return Files.Hex(sha.ComputeHash(all.ToArray()));
            }
        }

        // one number for the whole folder: hash each file, sort those, and hash the list
        static long Combine(List<string> files, Dictionary<string, long> cache)
        {
            var fps = new List<long>();
            foreach (string f in files)
            {
                long v;
                if (!cache.TryGetValue(f, out v))
                {
                    try { v = OfFile(f); }
                    catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { v = -1; }
                    cache[f] = v;
                }
                if (v >= 0) fps.Add(v);
            }
            fps.Sort();
            return Murmur2(Encoding.ASCII.GetBytes(string.Concat(fps.Select(x => x.ToString()))));
        }

        public static List<long> HashVariants(List<List<string>> variants)
        {
            var cache = new Dictionary<string, long>();
            var outList = new List<long>();
            foreach (var v in variants)
            {
                long fp = Combine(v, cache);
                if (!outList.Contains(fp)) outList.Add(fp);
            }
            return outList;
        }
    }
}
