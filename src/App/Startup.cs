// Start with Windows, and the background update checks.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace BlessedHearth
{
    // The few registry calls the app makes. The tests swap in a pretend registry kept in a JSON file
    // (HEARTH_TEST_WINREG), in the same format the Python version's tests used.
    public interface IRegistry
    {
        object Get(string key, string name);            // string, byte[] or null
        void SetString(string key, string name, string value);
        void SetBinary(string key, string name, byte[] value);
        void Delete(string key, string name);           // quietly does nothing if it isn't there
    }

    class WindowsRegistry : IRegistry
    {
        // Everything here lives under HKEY_CURRENT_USER, which every program can change for its own user
        public object Get(string key, string name)
        {
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(key)) return k?.GetValue(name);
        }

        public void SetString(string key, string name, string value)
        {
            using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(key)) k.SetValue(name, value, Microsoft.Win32.RegistryValueKind.String);
        }

        public void SetBinary(string key, string name, byte[] value)
        {
            using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(key)) k.SetValue(name, value, Microsoft.Win32.RegistryValueKind.Binary);
        }

        public void Delete(string key, string name)
        {
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(key, true)) k?.DeleteValue(name, false);
        }
    }

    class JsonRegistry : IRegistry
    {
        readonly string path;
        public JsonRegistry(string path) { this.path = path; }

        Dictionary<string, object> Load() => J.ObjOrEmpty(Files.ReadJson(path, null));
        void Save(Dictionary<string, object> d) => File.WriteAllText(path, Json.Write(d, indent: true));

        public object Get(string key, string name)
        {
            var v = J.Obj(J.Obj(Load().Get("HKCU\\" + key)).Get(name));
            if (v == null) return null;
            if (v.ContainsKey("b")) return J.List(v.Get("b")).Select(x => (byte)(J.ToLong(x) ?? 0)).ToArray();
            return v.Str("s");
        }

        void Set(string key, string name, Dictionary<string, object> val)
        {
            var d = Load();
            var k = J.Obj(d.Get("HKCU\\" + key));
            if (k == null) { k = new Dictionary<string, object>(); d["HKCU\\" + key] = k; }
            k[name] = val;
            Save(d);
        }

        public void SetString(string key, string name, string value) => Set(key, name, J.D("s", value));
        public void SetBinary(string key, string name, byte[] value) => Set(key, name, J.D("b", value.Select(b => (object)(long)b).ToList()));

        public void Delete(string key, string name)
        {
            var d = Load();
            var k = J.Obj(d.Get("HKCU\\" + key));
            if (k != null && k.Remove(name)) Save(d);
        }
    }

    // Start with Windows. Under the hood it's one line in the registry: HKCU\...\CurrentVersion\Run
    // is the list of programs Windows starts when you sign in (the same list Task Manager's Startup
    // apps tab shows). Task Manager's on/off switch for each one lives next door in StartupApproved\Run.
    public class Autostart
    {
        public static Autostart Instance = new Autostart();

        public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        public const string RunName = "BlessedHearth";            // the Python version used this same entry
        public const string OldRunName = "NotSoCursedForge";      // the entry's name from before the app was renamed

        IRegistry reg;
        bool looked;

        public IRegistry Reg
        {
            get
            {
                if (!looked)
                {
                    looked = true;
                    string test = Environment.GetEnvironmentVariable("HEARTH_TEST_WINREG");
                    if (!string.IsNullOrEmpty(test)) reg = new JsonRegistry(test);
                    else if (AppInfo.IsWindows) reg = new WindowsRegistry();
                }
                return reg;
            }
        }

        public bool Available => Reg != null;

        // The exe runs itself, with a flag so it knows Windows started it
        public static string Command() => "\"" + Assembly.GetEntryAssembly().Location + "\" --autostart";

        public string Current()
        {
            var r = Reg;
            if (r == null) return null;
            try { return r.Get(RunKey, RunName) as string; }
            catch (Exception) { return null; }
        }

        public bool TurnedOffInTaskManager(string name = RunName)
        {
            try
            {
                var data = Reg.Get(ApprovedKey, name) as byte[];
                return data != null && data.Length > 0 && data[0] % 2 == 1;   // first byte 02 means on, 03 means switched off
            }
            catch (Exception) { return false; }
        }

        public bool Enabled() => !string.IsNullOrEmpty(Current()) && !TurnedOffInTaskManager();

        public void Set(bool on)
        {
            var r = Reg;
            if (r == null) throw new ApiError("Starting with Windows only works on Windows.", 400);
            if (on) r.SetString(RunKey, RunName, Command());
            else r.Delete(RunKey, RunName);
            // clear an old "off" from Task Manager so turning it on here actually sticks
            try { r.Delete(ApprovedKey, RunName); } catch (Exception) { }
            Log.Write("start with Windows: " + (on ? "on" : "off"));
        }

        // Before the rename the entry was called NotSoCursedForge and pointed at the old exe. Swap
        // it for one under the new name, keeping it on or off the way it was.
        void MoveOldEntry()
        {
            var r = Reg;
            if (r.Get(RunKey, OldRunName) == null) return;   // nothing from the old days
            bool wasOn = !TurnedOffInTaskManager(OldRunName);
            foreach (string key in new[] { RunKey, ApprovedKey })
            {
                try { r.Delete(key, OldRunName); } catch (Exception) { }
            }
            Log.Write("start with Windows: moved the entry over from the old name");
            if (wasOn) Set(true);
        }

        // If the app moved (or you switched from the Python version), point the startup entry at
        // where it lives now
        public void Refresh()
        {
            try
            {
                if (Reg == null) return;
                MoveOldEntry();
                string cur = Current();
                if (!string.IsNullOrEmpty(cur) && cur != Command()) Set(true);
            }
            catch (Exception e) when (!(e is ThreadAbortException)) { }
        }
    }

    // Every few hours, while the app is running (window open or tucked in the tray), check every
    // game for updates. When new ones show up and you're not looking at the window, the window pops
    // a Windows notification. Each update only gets announced once.
    public class AutoCheck
    {
        public static AutoCheck Instance = new AutoCheck();
        public static readonly int[] Choices = { 0, 1, 3, 6, 12, 24 };

        readonly AutoResetEvent wake = new AutoResetEvent(false);
        public double? NextAt;
        bool started;
        public bool Asked;     // "Check for updates now" from the tray always answers, even with "nothing new"
        public double? FirstAt;   // when the first check of this run is planned

        static AppState App => AppState.App;

        public int Hours()
        {
            long h = J.ToLong(App.Config["check_hours"]) ?? -1;
            return Choices.Contains((int)h) && h >= 0 ? (int)h : 6;
        }

        public void Schedule(double firstDelay = 0)
        {
            int h = Hours();
            double last = Convert.ToDouble(App.Config["last_auto_check"] ?? 0L);
            double now = Clock.Now;
            if (h == 0) NextAt = null;
            else if (last <= 0) NextAt = Math.Max(now + firstDelay, FirstAt ?? now + 600);   // never checked before, so leave the first check where it was planned
            else NextAt = Math.Max(now + firstDelay, last + h * 3600);
            wake.Set();
        }

        // The window checks the selected game by itself when it opens, so the first check of every
        // game can wait a bit. When the app starts hidden in the tray it goes sooner.
        public void Start(double firstDelay = 600)
        {
            if (started) return;
            started = true;
            double delay;
            if (!double.TryParse(Environment.GetEnvironmentVariable("HEARTH_FIRST_CHECK"), System.Globalization.NumberStyles.Float,
                                 System.Globalization.CultureInfo.InvariantCulture, out delay))
                delay = firstDelay;
            FirstAt = Clock.Now + delay;
            Schedule(delay);
            new Thread(Loop) { IsBackground = true, Name = "auto-check" }.Start();
        }

        public void CheckNow(bool asked = false)
        {
            Asked = asked;
            NextAt = Clock.Now;
            wake.Set();
        }

        void Loop()
        {
            while (true)
            {
                wake.WaitOne(20000);
                if (NextAt == null || Clock.Now < NextAt) continue;
                if (App.Config.ApiKey == "" || App.Config.Profiles.Count == 0)
                {
                    NextAt = Clock.Now + 1800;
                    continue;
                }
                NextAt = null;
                if (App.Jobs.Start("Checking your games for updates", RunOnce) == null)
                    NextAt = Clock.Now + 120;     // you're in the middle of something, try again shortly
            }
        }

        public object RunOnce(Progress progress)
        {
            var failed = new List<object>();
            int checkedCount = 0;
            try
            {
                List<Dictionary<string, object>> profiles;
                lock (App.Sync) profiles = App.Config.ProfileList.ToList();
                foreach (var p in profiles)
                {
                    var mgr = App.Manager(p.Str("id"));
                    if (mgr == null) continue;
                    string label = Games.Label(p);
                    progress("Checking " + label + " for updates...", null);
                    try
                    {
                        App.Prepare(mgr, progress);
                        App.Scan(mgr, (msg, frac) => progress(label + ": " + msg, frac));
                        checkedCount++;
                    }
                    catch (Exception e) when (e is ApiError || e is IOException || e is UnauthorizedAccessException)
                    {
                        failed.Add(label);
                        Log.Write("background check: " + label + ": " + e.Message);
                    }
                }
                if (checkedCount > 0)
                {
                    lock (App.Sync)
                    {
                        App.Config["last_auto_check"] = Clock.Now;
                        App.Config.Save();
                    }
                    AfterScan(notify: true, asked: Asked);
                }
                Asked = false;
                Log.Write("background check: " + checkedCount + " checked" + (failed.Count > 0 ? ", failed: " + string.Join(", ", failed) : ""));
            }
            finally
            {
                Schedule();
                if (failed.Count > 0 && checkedCount == 0 && NextAt.HasValue)
                    NextAt = Math.Min(NextAt.Value, Clock.Now + 1800);   // offline? try again in half an hour
            }
            return J.D("background", true, "checked", (long)checkedCount, "failed", failed);
        }

        public List<Tuple<Dictionary<string, object>, AddonEntry, string>> Pending()
        {
            var outList = new List<Tuple<Dictionary<string, object>, AddonEntry, string>>();
            List<Dictionary<string, object>> profiles;
            lock (App.Sync) profiles = App.Config.ProfileList.ToList();
            foreach (var p in profiles)
                foreach (var e in App.EntriesFor(p.Str("id")).Values)
                    if (e.CanUpdate && !e.Ignored)
                        outList.Add(Tuple.Create(p, e, p.Str("id") + "|" + e.Key + "|" + (e.LatestFile?.Long("id")?.ToString() ?? "None")));
            return outList;
        }

        bool InFront() => Shell.WindowInFront == null || Shell.WindowInFront();   // the Edge window can't tell us, so assume you've seen it

        public void AfterScan(bool notify, bool asked = false)
        {
            var pend = Pending();
            var sigs = new HashSet<string>(pend.Select(x => x.Item3));
            HashSet<string> seen;
            lock (App.Sync) seen = new HashSet<string>(J.List(App.Config["notified"]).Select(x => Convert.ToString(x)));
            var fresh = pend.Where(x => !seen.Contains(x.Item3)).ToList();
            HashSet<string> remember;
            if (notify)
            {
                if (fresh.Count > 0 && !InFront() && Shell.Notify != null)
                {
                    var msg = Message(fresh, pend.Count);
                    Shell.Notify(msg.Item1, msg.Item2, fresh[0].Item1.Str("id"));
                    Log.Write("notified: " + msg.Item1);
                }
                else if (asked && Shell.Notify != null && !InFront())
                    Shell.Notify("No new updates", pend.Count > 0 ? pend.Count + " still waiting." : "Everything's up to date.", "");
                remember = sigs;                   // this also forgets ones that have been updated since
            }
            else if (InFront()) remember = sigs;
            else remember = new HashSet<string>(seen.Intersect(sigs));   // checked while hidden, so leave the new ones for a notification
            var sorted = remember.OrderBy(x => x, StringComparer.Ordinal).ToList();
            if (!sorted.SequenceEqual(seen.OrderBy(x => x, StringComparer.Ordinal)))
            {
                lock (App.Sync)
                {
                    App.Config["notified"] = sorted.Cast<object>().ToList();
                    App.Config.Save();
                }
            }
            RefreshTray(pend.Count);
        }

        public void RefreshTray(int? count = null)
        {
            Shell.TrayStatus?.Invoke(count ?? Pending().Count);
        }

        public static Tuple<string, string> Message(List<Tuple<Dictionary<string, object>, AddonEntry, string>> fresh, int total)
        {
            int n = fresh.Count;
            string title = n + " addon update" + (n != 1 ? "s" : "") + " ready";
            var groups = new Dictionary<string, List<string>>();
            var order = new List<string>();
            foreach (var x in fresh)
            {
                string label = Games.Label(x.Item1);
                if (!groups.ContainsKey(label)) { groups[label] = new List<string>(); order.Add(label); }
                groups[label].Add(x.Item2.Name);
            }
            var lines = new List<string>();
            int profileCount;
            lock (App.Sync) profileCount = App.Config.Profiles.Count;
            foreach (string label in order)
            {
                var names = groups[label];
                string shown = string.Join(", ", names.Take(3)) + (names.Count > 3 ? " and " + (names.Count - 3) + " more" : "");
                lines.Add(groups.Count > 1 || profileCount > 1 ? label + ": " + shown : shown);
            }
            if (total > n) lines.Add(total + " waiting in all.");
            return Tuple.Create(title, string.Join("\n", lines));
        }
    }
}
