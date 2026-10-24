// Talking to CurseForge's official API (api.curseforge.com).
//
// Every request is a plain https call with the API key in an "x-api-key" header. The answers are
// JSON. The key only ever goes to CurseForge's own servers.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace BlessedHearth
{
    public class ApiError : Exception
    {
        public readonly int? Status;
        public ApiError(string msg, int? status = null) : base(msg) { Status = status; }
        public bool IsAuth => Status == 401 || Status == 403;
    }

    // The addon's author turned off downloads for apps other than CurseForge's own. You can still
    // grab the file from the CurseForge website and the app installs it from there.
    public class DownloadBlocked : Exception
    {
        public readonly Dictionary<string, object> Mod, File;
        public DownloadBlocked(string msg, Dictionary<string, object> mod, Dictionary<string, object> file) : base(msg)
        {
            Mod = mod;
            File = file;
        }
    }

    public delegate void Progress(string message, double? fraction);

    public class CurseForgeApi
    {
        // The app hooks in here so it always knows if CurseForge is taking the key right now.
        // Called after every reply as listener(api key, ok, message).
        public static Action<string, bool, string> StatusListener;

        public readonly string ApiKey, Base;

        // true when this is the key that came built into the app, so messages don't ask you to paste a new one
        bool Builtin => BuiltInKey.Present && ApiKey == BuiltInKey.Value;

        public CurseForgeApi(string apiKey, string baseUrl = null)
        {
            ApiKey = apiKey ?? "";
            Base = (baseUrl ?? AppInfo.ApiBase).TrimEnd('/');
        }

        static readonly Regex CfKeyRe = new Regex(@"\$2[aby]\$\d{2}\$[./A-Za-z0-9]{53}");
        static readonly Regex InvisibleRe = new Regex(@"[\s\u200b-\u200f\u2028-\u202f\u2060-\u206f\ufeff\u00a0]+");

        // Tidy up a pasted API key. Copying from a web page sometimes drags along spaces, line
        // breaks or invisible characters. CurseForge keys always look the same ($2a$10$ plus 53
        // characters), so if one is hiding inside the pasted text, use just that.
        public static string CleanKey(string raw)
        {
            string s = InvisibleRe.Replace(raw ?? "", "");
            var found = CfKeyRe.Matches(s);
            return found.Count > 0 ? found[found.Count - 1].Value : s;
        }

        public static bool LooksLikeKey(string key) => CfKeyRe.Match(key).Success && CfKeyRe.Match(key).Length == key.Length;

        public static bool IsCurseForge(string url)
        {
            Uri u;
            if (Uri.TryCreate(url, UriKind.Absolute, out u))
            {
                string host = u.Host.ToLowerInvariant();
                if (host == "curseforge.com" || host == "forgecdn.net" || host.EndsWith(".curseforge.com") || host.EndsWith(".forgecdn.net"))
                    return true;
            }
            string testBase = AppInfo.TestApiBase;   // the test suite's pretend CurseForge
            return testBase != "" && url.StartsWith(testBase);
        }

        void Notify(bool ok, string message = null)
        {
            var fn = StatusListener;
            if (fn == null) return;
            try { fn(ApiKey, ok, message); } catch (Exception) { }
        }

        static string Encode(Dictionary<string, object> ps)
        {
            var parts = new List<string>();
            foreach (var kv in ps)
            {
                if (kv.Value == null) continue;
                string v = kv.Value is bool ? ((bool)kv.Value ? "True" : "False") : Convert.ToString(kv.Value, System.Globalization.CultureInfo.InvariantCulture);
                parts.Add(Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(v));
            }
            return string.Join("&", parts);
        }

        // Python copies every header onto a redirect and so would we. If a redirect ever points
        // somewhere that isn't CurseForge, the key gets left off so it never leaves CurseForge.
        // That's why redirects are followed by hand here instead of letting .NET do it.
        static HttpWebResponse Send(string method, string url, Dictionary<string, string> headers, byte[] body, int timeoutMs)
        {
            for (int hop = 0; hop < 10; hop++)
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = method;
                req.AllowAutoRedirect = false;
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                foreach (var h in headers)
                {
                    if (h.Key == "Accept") req.Accept = h.Value;
                    else if (h.Key == "User-Agent") req.UserAgent = h.Value;
                    else if (h.Key == "Content-Type") req.ContentType = h.Value;
                    else req.Headers[h.Key] = h.Value;
                }
                if (body != null)
                {
                    req.ContentLength = body.Length;
                    using (var s = req.GetRequestStream()) s.Write(body, 0, body.Length);
                }
                HttpWebResponse resp;
                try { resp = (HttpWebResponse)req.GetResponse(); }
                catch (WebException e) when (e.Response is HttpWebResponse) { resp = (HttpWebResponse)e.Response; }
                int code = (int)resp.StatusCode;
                if (code != 301 && code != 302 && code != 303 && code != 307 && code != 308) return resp;
                string loc = resp.Headers["Location"];
                resp.Close();
                if (string.IsNullOrEmpty(loc)) throw new WebException("Redirect without a location");
                url = new Uri(new Uri(url), loc).ToString();
                if (code == 303 || ((code == 301 || code == 302) && method == "POST"))
                {
                    method = "GET";
                    body = null;
                    headers = headers.Where(h => h.Key != "Content-Type").ToDictionary(h => h.Key, h => h.Value);
                }
                if (!IsCurseForge(url))
                    headers = headers.Where(h => !string.Equals(h.Key, "x-api-key", StringComparison.OrdinalIgnoreCase)).ToDictionary(h => h.Key, h => h.Value);
            }
            throw new WebException("Too many redirects");
        }

        static byte[] ReadAll(HttpWebResponse resp, int max = int.MaxValue)
        {
            using (var s = resp.GetResponseStream())
            using (var ms = new MemoryStream())
            {
                var buf = new byte[65536];
                int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0 && ms.Length < max) ms.Write(buf, 0, n);
                return ms.ToArray();
            }
        }

        // Turn a 401 or 403 into something useful by saying what CurseForge itself replied
        string AuthErrorText(int code, byte[] raw)
        {
            string detail = "";
            try
            {
                string body = Encoding.UTF8.GetString(raw, 0, Math.Min(raw.Length, 600));
                try
                {
                    var j = J.ObjOrEmpty(Json.Parse(body));
                    detail = j.Str("error");
                    if (detail == "") detail = j.Str("message");
                    if (detail == "") detail = j.Str("errorMessage");
                    if (detail.Length > 200) detail = detail.Substring(0, 200);
                }
                catch (JsonException)
                {
                    if (body.ToLowerInvariant().Contains("cloudfront") || body.ToLowerInvariant().Contains("<html"))
                        detail = "The request was blocked by CurseForge's network firewall before it reached the API.";
                    else
                    {
                        detail = Regex.Replace(body, @"\s+", " ");
                        if (detail.Length > 200) detail = detail.Substring(0, 200);
                    }
                }
            }
            catch (Exception) { }
            string msg = Builtin
                ? "CurseForge rejected the app's key (HTTP " + code + "). This usually clears up on its own, and if it doesn't, a newer version of the app will fix it."
                : "CurseForge rejected the API key (HTTP " + code + "). If you just created it, regenerate it once in "
                  + "console.curseforge.com > API keys and paste the new one.";
            return msg + (detail != "" ? " CurseForge said: " + detail : "");
        }

        object Request(string method, string path, Dictionary<string, object> ps = null, object body = null)
        {
            if (ApiKey == "") throw new ApiError("No CurseForge API key set. Add one in Settings.");
            string url = Base + path;
            if (ps != null)
            {
                string q = Encode(ps);
                if (q != "") url += "?" + q;
            }
            byte[] data = body != null ? Encoding.UTF8.GetBytes(Json.Write(body)) : null;
            var headers = new Dictionary<string, string>
            {
                { "x-api-key", ApiKey }, { "Accept", "application/json" }, { "User-Agent", AppInfo.UserAgent },
            };
            if (data != null) headers["Content-Type"] = "application/json";
            ApiError last = null;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                try
                {
                    using (var resp = Send(method, url, headers, data, 30000))
                    {
                        int code = (int)resp.StatusCode;
                        byte[] raw = ReadAll(resp);
                        if (code >= 200 && code < 300)
                        {
                            Notify(true);
                            return raw.Length > 0 ? Json.Parse(Encoding.UTF8.GetString(raw)) : null;
                        }
                        if (code == 401 || code == 403)
                        {
                            if (attempt == 0)
                            {
                                // CurseForge sometimes turns down a good key once and takes it a second
                                // later. One quick retry saves a false alarm.
                                Thread.Sleep(1000);
                                continue;
                            }
                            string msg = AuthErrorText(code, raw);
                            Notify(false, msg);
                            throw new ApiError(msg, code);
                        }
                        if (code == 404) throw new ApiError("Not found on CurseForge (HTTP 404): " + path, 404);
                        if (code == 429 || code == 500 || code == 502 || code == 503 || code == 504)
                        {
                            last = new ApiError("CurseForge returned HTTP " + code, code);
                            Thread.Sleep((int)(1500 * (attempt + 1)));
                            continue;
                        }
                        throw new ApiError("CurseForge returned HTTP " + code + ": " + resp.StatusDescription, code);
                    }
                }
                catch (WebException e)
                {
                    last = new ApiError("Could not reach CurseForge: " + e.Message);
                    Thread.Sleep((int)(1500 * (attempt + 1)));
                }
                catch (IOException e)
                {
                    last = new ApiError("Could not reach CurseForge: " + e.Message);
                    Thread.Sleep((int)(1500 * (attempt + 1)));
                }
            }
            throw last ?? new ApiError("Request failed");
        }

        static List<object> Data(object res) => J.List(J.ObjOrEmpty(res).Get("data"));

        static long Total(object res) => J.ObjOrEmpty(J.ObjOrEmpty(res).Get("pagination")).Long("totalCount") ?? 0;

        public List<Dictionary<string, object>> GamesList()
        {
            var outList = new List<Dictionary<string, object>>();
            int index = 0;
            while (true)
            {
                var res = Request("GET", "/v1/games", J.D("index", index, "pageSize", 50));
                var page = Data(res).OfType<Dictionary<string, object>>().ToList();
                outList.AddRange(page);
                index += page.Count;
                if (page.Count == 0 || index >= Total(res)) return outList;
            }
        }

        public List<Dictionary<string, object>> VersionTypes(long gameId = AppInfo.WowGameId)
        {
            var res = J.Obj(Request("GET", "/v1/games/" + gameId + "/version-types"));
            if (res == null || !res.ContainsKey("data")) throw new ApiError("CurseForge sent an empty answer");
            return Data(res).OfType<Dictionary<string, object>>().ToList();
        }

        public List<Dictionary<string, object>> FingerprintMatches(List<long> fingerprints, long gameId = AppInfo.WowGameId)
        {
            var outList = new List<Dictionary<string, object>>();
            for (int i = 0; i < fingerprints.Count; i += 500)
            {
                var chunk = fingerprints.Skip(i).Take(500).Cast<object>().ToList();
                var res = J.ObjOrEmpty(Request("POST", "/v1/fingerprints/" + gameId, body: J.D("fingerprints", chunk)));
                outList.AddRange(J.List(J.ObjOrEmpty(res.Get("data")).Get("exactMatches")).OfType<Dictionary<string, object>>());
            }
            return outList;
        }

        public List<Dictionary<string, object>> GetMods(IEnumerable<long> modIds)
        {
            var ids = modIds.Distinct().OrderBy(x => x).ToList();
            var outList = new List<Dictionary<string, object>>();
            for (int i = 0; i < ids.Count; i += 200)
                outList.AddRange(Data(Request("POST", "/v1/mods", body: J.D("modIds", ids.Skip(i).Take(200).Cast<object>().ToList())))
                                 .OfType<Dictionary<string, object>>());
            return outList;
        }

        public List<Dictionary<string, object>> GetFiles(IEnumerable<long> fileIds)
        {
            var ids = fileIds.Distinct().OrderBy(x => x).ToList();
            var outList = new List<Dictionary<string, object>>();
            for (int i = 0; i < ids.Count; i += 200)
                outList.AddRange(Data(Request("POST", "/v1/mods/files", body: J.D("fileIds", ids.Skip(i).Take(200).Cast<object>().ToList())))
                                 .OfType<Dictionary<string, object>>());
            return outList;
        }

        // The author's notes for one file. CurseForge sends these as HTML.
        public string Changelog(long modId, long fileId) =>
            J.ObjOrEmpty(Request("GET", "/v1/mods/" + modId + "/files/" + fileId + "/changelog")).Str("data");

        public List<Dictionary<string, object>> GetModFiles(long modId, long? flavorId, int pageSize = 50) =>
            Data(Request("GET", "/v1/mods/" + modId + "/files", J.D("gameVersionTypeId", (object)flavorId, "pageSize", pageSize, "index", 0)))
                .OfType<Dictionary<string, object>>().ToList();

        public List<Dictionary<string, object>> Search(string text, long? flavorId, out long total, int sortField = 2,
                                                       int index = 0, int pageSize = 50, long gameId = AppInfo.WowGameId)
        {
            var res = Request("GET", "/v1/mods/search", J.D(
                "gameId", gameId,
                "searchFilter", string.IsNullOrEmpty(text) ? null : text,
                "gameVersionTypeId", (object)flavorId,
                "sortField", sortField,
                "sortOrder", "desc",
                "index", index,
                "pageSize", pageSize));
            total = Total(res);
            return Data(res).OfType<Dictionary<string, object>>().ToList();
        }

        public void Download(string url, string dest, Progress progress)
        {
            Uri u;
            if (!Uri.TryCreate(url, UriKind.Absolute, out u) || (u.Scheme != "https" && !url.StartsWith(Base)))
                throw new ApiError("CurseForge sent a download link that isn't https, so it wasn't used.");
            // file names with spaces and such get encoded properly, without double encoding ones that already are
            string safePath = string.Join("/", u.AbsolutePath.Split('/').Select(seg => Uri.EscapeDataString(Uri.UnescapeDataString(seg))));
            string safeUrl = u.Scheme + "://" + u.Authority + safePath + u.Query;
            var headers = new Dictionary<string, string> { { "User-Agent", AppInfo.UserAgent } };
            // Since July 2026 CurseForge's file server (edge.forgecdn.net) wants the API key too.
            // Only ever hand it to CurseForge's own servers.
            if (ApiKey != "" && IsCurseForge(url)) headers["x-api-key"] = ApiKey;
            HttpWebResponse resp;
            try { resp = Send("GET", safeUrl, headers, null, 60000); }
            catch (WebException e) { throw new ApiError("Download failed: " + e.Message); }
            using (resp)
            {
                int code = (int)resp.StatusCode;
                if (code == 401 || code == 403)
                {
                    string msg = "CurseForge's file server refused the download (HTTP " + code + "). It needs a valid API key"
                                 + (Builtin ? ", and it isn't accepting the app's key right now." : ", so check yours in Settings.");
                    Notify(false, msg);
                    throw new ApiError(msg, code);
                }
                if (code < 200 || code >= 300) throw new ApiError("Download failed (HTTP " + code + ")", code);
                long total = resp.ContentLength > 0 ? resp.ContentLength : 0, got = 0;
                try
                {
                    using (var s = resp.GetResponseStream())
                    using (var outFile = System.IO.File.Create(dest))
                    {
                        var buf = new byte[65536];
                        int n;
                        while ((n = s.Read(buf, 0, buf.Length)) > 0)
                        {
                            got += n;
                            if (got > AppInfo.MaxDownloadBytes)
                                throw new ApiError("That download is far bigger than any addon should be, so it was stopped.");
                            outFile.Write(buf, 0, n);
                            if (total > 0) progress("Downloading... " + got / 1024 + " / " + total / 1024 + " KB", (double)got / total);
                        }
                    }
                }
                catch (WebException e) { throw new ApiError("Download failed: " + e.Message); }
                catch (IOException e) when (!(e is FileNotFoundException)) { throw new ApiError("Download failed: " + e.Message); }
            }
        }
    }
}
