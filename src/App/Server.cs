// The tiny web server the interface talks to.
//
// Under the hood it's a TCP socket on 127.0.0.1 (only this PC can reach that address) that speaks
// just enough HTTP for one page and its JSON calls. Every API call has to carry a random password
// that's made fresh each time the app starts. The window gets it in its address, and nothing else
// on the PC knows it, so other programs and web pages can't use the server.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace BlessedHearth
{
    class Request
    {
        public string Method, Path, Query;
        public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public byte[] Body = new byte[0];
        public bool BodyTooBig;
    }

    class Response
    {
        public int Code = 200;
        public string Type = "application/json";
        public byte[] Body = new byte[0];
        public Dictionary<string, string> Extra = new Dictionary<string, string>();
    }

    public class Server
    {
        const int MaxBody = 1024 * 1024;
        public static readonly string Token = RandomToken(24);

        readonly TcpListener listener;
        readonly string uiDir;
        public readonly string Url;
        AppState App => AppState.App;

        public static string RandomToken(int bytes)
        {
            var b = new byte[bytes];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            // url-safe base64, same shape as Python's secrets.token_urlsafe
            return Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        public Server(string uiDir, int port = 0)
        {
            this.uiDir = Path.GetFullPath(uiDir);
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            int actual = ((IPEndPoint)listener.LocalEndpoint).Port;
            Url = "http://127.0.0.1:" + actual + "/?t=" + Token;
            var t = new Thread(AcceptLoop) { IsBackground = true, Name = "server" };
            t.Start();
            App.Jobs.OnDone = () => AutoCheck.Instance.RefreshTray();
            Log.Write("version " + AppInfo.Version + " started on " + Url.Split('?')[0]);
        }

        public void Stop()
        {
            try { listener.Stop(); } catch (Exception) { }
        }

        void AcceptLoop()
        {
            while (true)
            {
                TcpClient client;
                try { client = listener.AcceptTcpClient(); }
                catch (Exception) { return; }   // stopped
                ThreadPool.QueueUserWorkItem(_ => Serve(client));
            }
        }

        void Serve(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.ReceiveTimeout = 30000;
                    var stream = client.GetStream();
                    var req = ReadRequest(stream);
                    if (req == null) return;
                    var resp = Handle(req);
                    WriteResponse(stream, resp);
                }
                catch (Exception e) when (e is IOException || e is SocketException || e is ObjectDisposedException) { }
                catch (Exception e) { Log.Write(e.ToString()); }
            }
        }

        static Request ReadRequest(NetworkStream stream)
        {
            // the request line and headers end at the first blank line
            var head = new List<byte>();
            int b, lastFour = 0;
            while ((b = stream.ReadByte()) >= 0)
            {
                head.Add((byte)b);
                lastFour = (lastFour << 8) | b;
                if (lastFour == 0x0D0A0D0A) break;
                if (head.Count > 65536) return null;
            }
            if (b < 0) return null;
            var lines = Encoding.ASCII.GetString(head.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.None);
            var first = lines[0].Split(' ');
            if (first.Length < 2) return null;
            var req = new Request { Method = first[0].ToUpperInvariant() };
            string target = first[1];
            int q = target.IndexOf('?');
            req.Path = q >= 0 ? target.Substring(0, q) : target;
            req.Query = q >= 0 ? target.Substring(q + 1) : "";
            foreach (string line in lines.Skip(1))
            {
                int c = line.IndexOf(':');
                if (c > 0) req.Headers[line.Substring(0, c).Trim()] = line.Substring(c + 1).Trim();
            }
            string lenText;
            int len = 0;
            if (req.Headers.TryGetValue("Content-Length", out lenText)) int.TryParse(lenText, out len);
            if (len > MaxBody) req.BodyTooBig = true;
            else if (len > 0)
            {
                var body = new byte[len];
                int got = 0;
                while (got < len)
                {
                    int n = stream.Read(body, got, len - got);
                    if (n <= 0) break;
                    got += n;
                }
                req.Body = body;
            }
            return req;
        }

        static void WriteResponse(NetworkStream stream, Response r)
        {
            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(r.Code).Append(' ').Append(Reason(r.Code)).Append("\r\n");
            sb.Append("Server: BlessedHearth\r\n");
            sb.Append("Content-Type: ").Append(r.Type).Append("\r\n");
            sb.Append("Content-Length: ").Append(r.Body.Length).Append("\r\n");
            sb.Append("Cache-Control: no-store\r\n");
            sb.Append("X-Content-Type-Options: nosniff\r\n");
            sb.Append("Referrer-Policy: no-referrer\r\n");
            foreach (var kv in r.Extra) sb.Append(kv.Key).Append(": ").Append(kv.Value).Append("\r\n");
            sb.Append("Connection: close\r\n\r\n");
            var head = Encoding.UTF8.GetBytes(sb.ToString());
            stream.Write(head, 0, head.Length);
            stream.Write(r.Body, 0, r.Body.Length);
            stream.Flush();
        }

        static string Reason(int code)
        {
            switch (code)
            {
                case 200: return "OK";
                case 400: return "Bad Request";
                case 401: return "Unauthorized";
                case 403: return "Forbidden";
                case 404: return "Not Found";
                case 409: return "Conflict";
                case 500: return "Internal Server Error";
                case 502: return "Bad Gateway";
                default: return "OK";
            }
        }

        static Response Text(int code, string text) => new Response { Code = code, Type = "text/plain", Body = Encoding.UTF8.GetBytes(text) };
        static Response JsonOut(object obj, int code = 200) => new Response { Code = code, Body = Encoding.UTF8.GetBytes(Json.Write(obj)) };
        static Response Error(string msg, int code) => JsonOut(J.D("error", msg), code);

        // Compares without leaking how much of the password matched through timing
        static bool Same(string a, string b)
        {
            var x = Encoding.UTF8.GetBytes(a ?? "");
            var y = Encoding.UTF8.GetBytes(b ?? "");
            int diff = x.Length ^ y.Length;
            for (int i = 0; i < Math.Min(x.Length, y.Length); i++) diff |= x[i] ^ y[i];
            return diff == 0;
        }

        static Dictionary<string, string> ParseQuery(string query)
        {
            var d = new Dictionary<string, string>();
            foreach (string part in (query ?? "").Split('&'))
            {
                if (part == "") continue;
                int eq = part.IndexOf('=');
                string k = Uri.UnescapeDataString((eq >= 0 ? part.Substring(0, eq) : part).Replace('+', ' '));
                string v = eq >= 0 ? Uri.UnescapeDataString(part.Substring(eq + 1).Replace('+', ' ')) : "";
                if (!d.ContainsKey(k)) d[k] = v;
            }
            return d;
        }

        static string Q(Dictionary<string, string> q, string key, string fallback)
        {
            string v;
            return q.TryGetValue(key, out v) ? v : fallback;
        }

        // Turns away web pages that point some other domain name at 127.0.0.1 (DNS rebinding)
        static bool HostOk(Request r)
        {
            string host;
            r.Headers.TryGetValue("Host", out host);
            host = (host ?? "").Split(':')[0];
            return host == "127.0.0.1" || host == "localhost";
        }

        static bool Authed(Request r)
        {
            string tok;
            r.Headers.TryGetValue("X-Hearth-Token", out tok);
            return HostOk(r) && Same(tok, Token);
        }

        Response Handle(Request r)
        {
            var q = ParseQuery(r.Query);
            if (r.Method == "GET" && !r.Path.StartsWith("/api/")) return Static(r, q);
            if (r.Method == "POST" && r.Path == "/api/bye" && HostOk(r) && Same(Q(q, "t", ""), Token))
            {
                // sendBeacon can't add headers, so this one call carries the password in the address
                App.ByeAt = Clock.Now;
                return JsonOut(J.D("ok", true));
            }
            if (!Authed(r)) return Error("forbidden", 403);
            try
            {
                if (r.Method == "GET") return Get(r.Path, q);
                if (r.Method == "POST")
                {
                    Dictionary<string, object> body = new Dictionary<string, object>();
                    if (!r.BodyTooBig && r.Body.Length > 0)
                    {
                        try { body = J.ObjOrEmpty(Json.Parse(Encoding.UTF8.GetString(r.Body))); }
                        catch (JsonException) { }
                    }
                    return Post(r.Path, body);
                }
                return Error("unknown", 404);
            }
            catch (ApiError e)
            {
                return JsonOut(J.D("error", e.Message, "auth", e.IsAuth), e.IsAuth ? 401 : 502);
            }
            catch (Exception e)
            {
                Log.Write(e.ToString());
                return Error(e.GetType().Name + ": " + e.Message, 500);
            }
        }

        static readonly Dictionary<string, string> Mime = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { ".html", "text/html" }, { ".js", "text/javascript" }, { ".css", "text/css" }, { ".svg", "image/svg+xml" },
            { ".woff2", "font/woff2" }, { ".png", "image/png" }, { ".ico", "image/vnd.microsoft.icon" }, { ".txt", "text/plain" },
            { ".json", "application/json" },
        };

        Response Static(Request r, Dictionary<string, string> q)
        {
            if (!HostOk(r)) return Text(403, "forbidden");
            string path = r.Path;
            if (path == "/" || path == "/index.html")
            {
                if (!Same(Q(q, "t", ""), Token)) return Text(403, "Open Blessed Hearth from its launcher.");
                path = "/index.html";
            }
            // Only files inside the ui folder, ever. GetFullPath sorts out any ../ tricks and drive
            // letters before we compare.
            string full;
            try { full = Path.GetFullPath(Path.Combine(uiDir, path.TrimStart('/', '\\'))); }
            catch (Exception) { return Text(404, "not found"); }
            if (!full.StartsWith(uiDir + Path.DirectorySeparatorChar) || !File.Exists(full)) return Text(404, "not found");
            string type;
            if (!Mime.TryGetValue(Path.GetExtension(full), out type)) type = "application/octet-stream";
            byte[] data = File.ReadAllBytes(full);
            var resp = new Response { Type = type, Body = data };
            if (full.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            {
                // A fresh one-time code for the page's own script. The browser runs only scripts that
                // carry it, so even if some text from CurseForge ever slipped into the page as code,
                // it wouldn't run. Pictures can come from any https address (addon logos live on
                // CurseForge's servers), but the page can only talk to this app.
                string nonce = RandomToken(16);
                string html = Encoding.UTF8.GetString(data)
                    .Replace("__HEARTH_TOKEN__", Token).Replace("__HEARTH_NONCE__", nonce)
                    // paint the saved theme from the very first frame, no flash of the default colors
                    .Replace("__HEARTH_THEME__", Themes.Id(App.Config["theme"]));
                resp.Body = Encoding.UTF8.GetBytes(html);
                // the test suite's pretend CurseForge serves its logos over plain http on this PC
                string testBase = AppInfo.TestApiBase;
                string testImages = testBase.StartsWith("http://127.0.0.1") ? testBase : "";
                resp.Extra["Content-Security-Policy"] =
                    "default-src 'self'; script-src 'nonce-" + nonce + "'; style-src 'self' 'unsafe-inline'; " +
                    "img-src 'self' https: data: " + testImages + "; font-src 'self'; connect-src 'self'; " +
                    "object-src 'none'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
                resp.Extra["X-Frame-Options"] = "DENY";
            }
            return resp;
        }

        Response Get(string path, Dictionary<string, string> q)
        {
            switch (path)
            {
                case "/api/state": return JsonOut(App.StateJson());
                case "/api/job": return JsonOut(App.Jobs.Snapshot());
                case "/api/rev":
                    return JsonOut(J.D("rev", (long)App.Rev, "job", App.Jobs.Running, "manualActive", App.Manual.Busy));
                case "/api/manual": return JsonOut(App.Manual.Snapshot());
                case "/api/search":
                {
                    var mgr = App.Manager();
                    if (mgr == null || App.Config.ApiKey == "") return Error("Add your API key and AddOns folder in Settings first.", 400);
                    long? flavor = Q(q, "forever", "1") == "1" ? mgr.FlavorId : null;
                    long total;
                    var mods = mgr.Api.Search(Q(q, "q", ""), flavor, out total, sortField: int.Parse(Q(q, "sort", "2")),
                                              index: int.Parse(Q(q, "index", "0")), pageSize: 30, gameId: mgr.GameId);
                    return JsonOut(J.D("results", mods.Select(m => App.SearchJson(m, mgr)).ToList(), "total", total));
                }
                case "/api/changelog":
                    return JsonOut(J.D("text", App.Changelog(long.Parse(Q(q, "modId", "0")), long.Parse(Q(q, "fileId", "0")))));
                case "/api/version-types":
                    App.ForgetWowTypes();
                    return JsonOut(J.D("types", App.WowTypes().Select(t => J.D("id", t.Get("id"), "name", t.Str("name"))).ToList()));
                case "/api/detect-installs":
                {
                    var types = App.Config.ApiKey != "" ? App.WowTypes().Select(t => (object)J.D("id", t.Get("id"), "name", t.Str("name"))).ToList()
                                                        : new List<object>();
                    return JsonOut(J.D("installs", App.FoundInstalls(), "wowTypes", types,
                                       "games", Games.All.Select(g => J.D("id", g.Id, "name", g.Name)).ToList()));
                }
                case "/api/add-options": return JsonOut(J.D("options", App.AddOptions()));
            }
            return Error("unknown", 404);
        }

        Response Start(string label, Func<Progress, object> fn)
        {
            var snap = App.Jobs.Start(label, fn);
            if (snap == null) return Error("Something else is still running. Try again in a moment.", 409);
            return JsonOut(snap);
        }

        static List<string> Strings(object v) => J.List(v).Select(x => Convert.ToString(x)).ToList();

        Response Post(string path, Dictionary<string, object> b)
        {
            switch (path)
            {
                case "/api/ping":
                    App.LastPing = Clock.Now;
                    App.ByeAt = null;
                    return JsonOut(J.D("ok", true));
                case "/api/bye":
                    App.ByeAt = Clock.Now;
                    return JsonOut(J.D("ok", true));
                case "/api/scan": return Start("Checking for updates", App.JobScan);
                case "/api/check-all":
                    AutoCheck.Instance.CheckNow();
                    return JsonOut(J.D("ok", true));
                case "/api/update":
                {
                    var keys = Strings(b.Get("keys"));
                    if (b.Bool("all"))
                        keys = App.Entries.Where(kv => kv.Value.CanUpdate && !kv.Value.Ignored && kv.Value.LatestFile.Str("downloadUrl") != "")
                                          .Select(kv => kv.Key).ToList();
                    if (keys.Count == 0) return Error("Nothing to update.", 400);
                    return Start("Updating", p => App.JobUpdate(keys, p));
                }
                case "/api/install":
                {
                    var ids = J.List(b.Get("modIds")).Select(x => J.ToLong(x) ?? 0).ToList();
                    return Start("Installing", p => App.JobInstall(ids, p));
                }
                case "/api/uninstall":
                {
                    var keys = Strings(b.Get("keys"));
                    return Start("Removing", p => App.JobUninstall(keys, p));
                }
                case "/api/link":
                case "/api/unlink":
                case "/api/ignore":
                case "/api/channel":
                {
                    string action = path.Substring(path.LastIndexOf('/') + 1);
                    object arg = action == "link" ? b.Get("modId") : action == "ignore" ? b.Get("ignored") : action == "channel" ? b.Get("channel") : null;
                    string key = b.Get("key") as string;
                    return Start("Saving", p => App.JobSimple(action, key, arg, p));
                }
                case "/api/manual/start":
                {
                    var mgr = App.Manager();
                    var started = new List<Dictionary<string, object>>();
                    var entries = App.Entries;
                    foreach (string k in Strings(b.Get("keys")))
                    {
                        AddonEntry e;
                        if (entries.TryGetValue(k, out e) && e.Mod != null && e.LatestFile != null)
                            started.Add(App.Manual.Add(e.Mod, e.LatestFile, k, mgr.ProfileId));
                    }
                    foreach (object mid in J.List(b.Get("modIds")))
                    {
                        var mod = mgr.Api.GetMods(new[] { J.ToLong(mid) ?? 0 })[0];
                        var f = mgr.LatestFileForMod(mod);
                        if (f != null) started.Add(App.Manual.Add(mod, f, null, mgr.ProfileId));
                    }
                    new Thread(() => App.Manual.OpenPages(started)) { IsBackground = true }.Start();
                    return JsonOut(App.Manual.Snapshot());
                }
                case "/api/manual/reopen":
                {
                    string fid = Convert.ToString(J.ToLong(b.Get("fileId")) ?? (object)b.Str("fileId"));
                    Dictionary<string, object> item, copy = null;
                    lock (App.Manual.Gate)
                    {
                        if (App.Manual.Items.TryGetValue(fid, out item))
                        {
                            item["status"] = "waiting";
                            item["error"] = "";
                            item["since"] = Clock.Now;
                            item.Remove("picked");
                            copy = new Dictionary<string, object>(item);
                        }
                    }
                    if (copy != null) new Thread(() => App.Manual.OpenPages(new List<Dictionary<string, object>> { copy })) { IsBackground = true }.Start();
                    return JsonOut(App.Manual.Snapshot());
                }
                case "/api/manual/pick":
                {
                    string fid = Convert.ToString(J.ToLong(b.Get("fileId")) ?? (object)b.Str("fileId"));
                    string p = Dialogs.PickZip();
                    if (!string.IsNullOrEmpty(p)) App.Manual.UseFile(fid, p);
                    return JsonOut(App.Manual.Snapshot());
                }
                case "/api/manual/clear":
                    App.Manual.Clear(doneOnly: !b.Bool("all"));
                    return JsonOut(App.Manual.Snapshot());
                case "/api/test-key": return TestKey();
                case "/api/settings": return Settings(b);
                case "/api/set-folder":
                {
                    string p = NormalizeAddonsDir(b.Str("path"));
                    if (p == "") return Error("That folder doesn't exist.", 400);
                    lock (App.Sync)
                    {
                        if (App.Config.ActiveProfile() == null)
                        {
                            var gp = Detect.GuessGameForFolder(p);
                            var prof = Games.NewProfile(gp.Item1, p);
                            if (gp.Item1 == "wow")
                            {
                                var t = App.GuessFlavor(p, gp.Item2);
                                if (t != null)
                                {
                                    prof["flavorId"] = t.Get("id");
                                    prof["flavorName"] = t.Str("name");
                                    prof["label"] = Games.Label(prof);
                                }
                            }
                            App.Config.AddProfile(prof);
                        }
                        else App.Config["addons_dir"] = p;
                        App.Config.Save();
                    }
                    App.ForgetScan(App.ActiveId);
                    return JsonOut(J.D("setup", App.Setup()));
                }
                case "/api/profiles/select":
                {
                    string id = b.Get("id") as string;
                    lock (App.Sync)
                    {
                        if (App.Config.Profile(id) == null) return Error("That game isn't set up.", 400);
                        App.Config.Data["active"] = id;
                        App.Config.Save();
                    }
                    App.Bump();
                    if (!App.HasScan(id) && App.Manager() != null) App.Jobs.Start("Checking for updates", App.JobScan);
                    return JsonOut(App.StateJson());
                }
                case "/api/profiles/add":
                {
                    var added = new List<Dictionary<string, object>>();
                    foreach (var item in J.List(b.Get("items")).OfType<Dictionary<string, object>>())
                    {
                        string d = NormalizeAddonsDir(item.Str("addonsDir"));
                        string game = item.Str("game") != "" ? item.Str("game") : "wow";
                        if (d == "" || Games.Get(game) == null) continue;
                        long? fl = J.ToLong(item.Get("flavorId"));
                        var prof = Games.NewProfile(game, d, fl.HasValue && fl != 0 ? fl : null, item.Str("flavorName"));
                        if (game == "wow" && prof.Get("flavorId") == null)
                        {
                            var t = App.GuessFlavor(d);
                            if (t != null)
                            {
                                prof["flavorId"] = t.Get("id");
                                prof["flavorName"] = t.Str("name");
                                prof["label"] = Games.Label(prof);
                            }
                        }
                        lock (App.Sync) added.Add(App.Config.AddProfile(prof, activate: added.Count == 0));
                    }
                    if (added.Count == 0) return Error("Pick at least one AddOns folder that exists.", 400);
                    lock (App.Sync) App.Config.Save();
                    App.Bump();
                    if (App.Manager() != null) App.Jobs.Start("Checking for updates", App.JobScan);
                    return JsonOut(App.StateJson());
                }
                case "/api/profiles/remove":
                {
                    string id = b.Get("id") as string;
                    lock (App.Sync)
                    {
                        App.Config.RemoveProfile(id);
                        App.Config.Save();
                    }
                    App.ForgetScan(id);
                    App.Bump();
                    if (App.Config.ActiveProfile() != null && !App.HasScan(App.ActiveId) && App.Manager() != null)
                        App.Jobs.Start("Checking for updates", App.JobScan);
                    return JsonOut(App.StateJson());
                }
                case "/api/guess-folder":
                {
                    string d = NormalizeAddonsDir(b.Str("path"));
                    if (d == "") return Error("That folder doesn't exist.", 400);
                    var gp = Detect.GuessGameForFolder(d);
                    var t = gp.Item1 == "wow" ? App.GuessFlavor(d, gp.Item2) : null;
                    return JsonOut(J.D("addonsDir", d, "game", gp.Item1, "product", gp.Item2,
                                       "flavorId", t?.Get("id"), "flavorName", t != null ? t.Str("name") : ""));
                }
                case "/api/pick-folder":
                {
                    string start = b.Str("start");
                    if (start == "") start = App.Config.Str("addons_dir");
                    return JsonOut(J.D("path", Dialogs.PickFolder(start, b.Str("title"))));
                }
                case "/api/open": return Open(b);
            }
            return Error("unknown", 404);
        }

        Response TestKey()
        {
            string key = App.Config.ApiKey;
            if (key == "") return Error("No API key saved yet.", 400);
            var api = new CurseForgeApi(key);
            var outD = new Dictionary<string, object>();
            var tests = new List<Tuple<string, Action>>
            {
                Tuple.Create("lookup", (Action)(() => api.VersionTypes())),
                Tuple.Create("search", (Action)(() => { long t; api.Search("", null, out t, pageSize: 1); })),
            };
            foreach (var t in tests)
            {
                try
                {
                    t.Item2();
                    outD[t.Item1] = J.D("ok", true);
                }
                catch (ApiError e) { outD[t.Item1] = J.D("ok", false, "error", e.Message, "status", e.Status); }
            }
            Log.Write("key test (" + (BuiltInKey.Present ? "built-in key" : "ends ..." + AppState.Tail(key)) + "): " + string.Join(", ", outD.Select(kv =>
                kv.Key + "=" + (J.Obj(kv.Value).Bool("ok") ? "ok" : (J.Obj(kv.Value).Get("status")?.ToString() ?? "None")))));
            return JsonOut(J.D("results", outD, "setup", App.Setup()));
        }

        Response Settings(Dictionary<string, object> b)
        {
            var c = App.Config;
            // A build with the app's own key has nothing to paste, so a pasted key is just ignored
            if (b.Get("apiKey") != null && !BuiltInKey.Present)
            {
                string raw = b.Str("apiKey");
                string fresh = CurseForgeApi.CleanKey(raw);
                if (fresh != "")
                {
                    // Always test the key, even if it's the one already saved. The log only gets its
                    // length and last 4 characters, never the key itself.
                    string shape = CurseForgeApi.LooksLikeKey(fresh) ? "CurseForge format" : "unexpected format";
                    string note = "key check: pasted " + raw.Length + " chars, using " + fresh.Length + " chars (" + shape + ", ends ..." + AppState.Tail(fresh) + ")";
                    try { new CurseForgeApi(fresh).VersionTypes(); }   // throws with a clear message if the key is bad
                    catch (ApiError e)
                    {
                        Log.Write(note + " -> rejected (" + (e.Status?.ToString() ?? "None") + ")");
                        if (shape == "unexpected format")
                            throw new ApiError("That doesn't look like a CurseForge API key (" + fresh.Length + " characters; they're 60 and "
                                               + "start with $2a$10$). Make sure you copied the key from console.curseforge.com > API keys.", e.Status);
                        throw;
                    }
                    Log.Write(note + " -> accepted");
                    lock (App.Sync) c["api_key"] = fresh;
                    App.KeyState = J.D("state", "ok", "at", Clock.Now, "message", "");
                }
            }
            lock (App.Sync)
            {
                if (J.Truthy(b.Get("flavorId")))
                {
                    c["flavor_id"] = J.ToLong(b.Get("flavorId"));
                    c["flavor_name"] = b.Str("flavorName") != "" ? b.Str("flavorName") : c.Str("flavor_name");
                    App.ForgetScan(App.ActiveId);
                }
                if (b.ContainsKey("channel")) c["channel"] = J.ToLong(b.Get("channel")) ?? 1;
                if (b.ContainsKey("keepBackups")) c["keep_backups"] = J.Truthy(b.Get("keepBackups"));
                if (b.ContainsKey("maxBackups")) c["max_backups"] = Math.Max(1, J.ToLong(b.Get("maxBackups")) ?? 1);
                if (b.ContainsKey("tray"))
                {
                    c["tray"] = J.Truthy(b.Get("tray"));
                    Shell.SetTray?.Invoke(J.Truthy(c["tray"]));
                }
            }
            if (b.ContainsKey("checkHours"))
            {
                long h = J.ToLong(b.Get("checkHours")) ?? -1;
                if (!AutoCheck.Choices.Contains((int)h) || h < 0) return Error("Pick one of the listed intervals.", 400);
                lock (App.Sync)
                {
                    c["check_hours"] = h;
                    c.Save();
                }
                AutoCheck.Instance.Schedule();
            }
            if (b.ContainsKey("startMinimized")) lock (App.Sync) c["start_minimized"] = J.Truthy(b.Get("startMinimized"));
            if (b.ContainsKey("autostart")) Autostart.Instance.Set(J.Truthy(b.Get("autostart")));
            if (b.ContainsKey("theme"))
            {
                lock (App.Sync) c["theme"] = Themes.Id(b.Get("theme"));
                Shell.SetTheme?.Invoke(c.Str("theme"));
            }
            lock (App.Sync) c.Save();
            return JsonOut(J.D("setup", App.Setup()));
        }

        Response Open(Dictionary<string, object> b)
        {
            // Links only go to CurseForge (or the app's own download page on GitHub), and folders
            // only to the AddOns folder, its backups, or one addon's folder inside it
            string url = b.Str("url");
            if (url != "")
            {
                if ((url.StartsWith("https://") && CurseForgeApi.IsCurseForge(url)) || url == AppInfo.ReleasesUrl)
                {
                    EdgeWindow.OpenInBrowser(url);
                    return JsonOut(J.D("ok", true));
                }
                return Error("That link isn't a CurseForge page.", 400);
            }
            string which = b.Str("folder");
            if (which != "")
            {
                string bas = App.Config.Str("addons_dir");
                string p;
                if (which == "addons") p = bas;
                else if (which == "backups") p = bas != "" ? Path.Combine(Path.GetDirectoryName(bas.TrimEnd('\\', '/')) ?? "", "AddOns_Backups") : "";
                else p = bas != "" ? Path.Combine(bas, Path.GetFileName(which.TrimEnd('\\', '/'))) : "";
                if (p == "" || !Directory.Exists(p)) return Error("That folder doesn't exist yet.", 400);
                OpenPath(p);
            }
            return JsonOut(J.D("ok", true));
        }

        // People pick the game folder or the Interface folder as often as AddOns itself, so find AddOns
        public static string NormalizeAddonsDir(string path)
        {
            string p = Environment.ExpandEnvironmentVariables((path ?? "").Trim().Trim('"'));
            if (p.StartsWith("~")) p = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + p.Substring(1);
            if (p == "") return "";
            try { p = Path.GetFullPath(p); }
            catch (Exception) { return ""; }
            string root = Path.GetPathRoot(p) ?? "";
            if (p.Length > root.Length) p = p.TrimEnd('\\', '/');
            if (!Directory.Exists(p)) return "";
            if (Path.GetFileName(p).ToLowerInvariant() != "addons")
            {
                foreach (string cand in new[] { Path.Combine(p, "AddOns"), Path.Combine(p, "Interface", "AddOns") })
                    if (Directory.Exists(cand)) return cand;
            }
            return p;
        }

        public static void OpenPath(string path)
        {
            try
            {
                if (AppInfo.IsWindows) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                else Process.Start("xdg-open", "\"" + path + "\"");
            }
            catch (Exception e) { Log.Write("couldn't open " + path + ": " + e.Message); }
        }
    }
}
