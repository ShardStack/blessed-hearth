// Where the app starts.
//
// Blessed Hearth is one exe. Inside it, besides the code, are the interface files (ui\...) and the
// two small WebView2 helper DLLs that let a WinForms window host Windows' built-in browser engine.
// Nothing gets installed: the first time a version runs it copies those few files into
// %LOCALAPPDATA%\BlessedHearth and uses them from there.

using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// Tells .NET this was built for 4.8, so it uses modern defaults (like letting Windows pick the
// newest TLS version) instead of acting like a 2010 app
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]
[assembly: AssemblyTitle("Blessed Hearth - A Not So Cursed Client")]
[assembly: AssemblyProduct("Blessed Hearth")]
[assembly: AssemblyVersion(BlessedHearth.AppInfo.Version + ".0")]
[assembly: AssemblyFileVersion(BlessedHearth.AppInfo.Version + ".0")]

namespace BlessedHearth
{
    public static class Program
    {
        public static string UiDir;
        public static string LibDir;
        public static string[] Args = new string[0];

        [STAThread]
        static int Main(string[] args)
        {
            Args = args;
            try
            {
                // TLS 1.2 and 1.3 for talking to CurseForge (1.3 only where Windows has it)
                try { ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | (SecurityProtocolType)12288; }
                catch (NotSupportedException) { ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12; }

                AppDomain.CurrentDomain.AssemblyResolve += LoadEmbeddedDll;
                Unpack();
                AppPaths.LocalDataDir();   // brings things over from the app's old folder, if there is one
                new AppState();

                bool noWindow = args.Contains("--no-window");
                int port = 0;
                int pi = Array.IndexOf(args, "--port");
                if (pi >= 0 && pi + 1 < args.Length) int.TryParse(args[pi + 1], out port);
                else int.TryParse(Environment.GetEnvironmentVariable("HEARTH_PORT"), out port);

                if (noWindow)
                {
                    EdgeWindow.Run(false, port);
                    return 0;
                }
                if (!AppInfo.IsWindows || !WebView2Ready())
                {
                    Log.Write("WebView2 isn't available, using an Edge window instead");
                    EdgeWindow.Run(true, port);
                    return 0;
                }
                return RunWindow(port);
            }
            catch (Exception e)
            {
                string local = Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? Path.GetTempPath(), AppPaths.DataFolder);
                try
                {
                    Directory.CreateDirectory(local);
                    File.WriteAllText(Path.Combine(local, "crash.log"), e.ToString());
                }
                catch (Exception) { }
                Fail("Something went wrong and the app closed.\n\nDetails were saved to:\n" + Path.Combine(local, "crash.log"));
                return 1;
            }
        }

        public static void Fail(string msg)
        {
            if (AppInfo.IsWindows)
            {
                try { MessageBox.Show(msg, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
                catch (Exception) { }
            }
            Console.Error.WriteLine(msg);
        }

        // The interface files and helper DLLs ride inside the exe. They get copied out once per
        // version, into a folder named after a fingerprint of the exe so a rebuilt exe never uses
        // stale files. When the app runs from its source folder (the tests do), ui\ next to it wins.
        static void Unpack()
        {
            string exe = Assembly.GetEntryAssembly().Location;
            string here = Path.GetDirectoryName(exe);
            var asm = Assembly.GetExecutingAssembly();
            var names = asm.GetManifestResourceNames();
            if (Directory.Exists(Path.Combine(here, "ui")) || names.Length == 0)
            {
                UiDir = Path.Combine(here, "ui");
                LibDir = Path.Combine(here, "lib");
                return;
            }
            string stamp;
            using (var fs = File.OpenRead(exe))
            using (var sha = System.Security.Cryptography.SHA1.Create())
                stamp = Files.Hex(sha.ComputeHash(fs)).Substring(0, 12);
            string root = Path.Combine(AppPaths.LocalDataDir(), "files", AppInfo.Version + "-" + stamp);
            UiDir = Path.Combine(root, "ui");
            LibDir = Path.Combine(root, "lib");
            if (!File.Exists(Path.Combine(root, ".done")))
            {
                using (var gate = new Mutex(false, @"Local\BlessedHearth-unpack"))
                {
                    gate.WaitOne(TimeSpan.FromMinutes(2));
                    try
                    {
                        if (!File.Exists(Path.Combine(root, ".done")))
                        {
                            foreach (string n in names)
                            {
                                if (!n.StartsWith("ui/") && !n.StartsWith("lib/")) continue;
                                string dest = Path.Combine(root, n.Replace('/', Path.DirectorySeparatorChar));
                                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                                using (var src = asm.GetManifestResourceStream(n))
                                using (var dst = File.Create(dest)) src.CopyTo(dst);
                            }
                            File.WriteAllText(Path.Combine(root, ".done"), AppInfo.Version);
                        }
                    }
                    finally { gate.ReleaseMutex(); }
                }
                // older versions' copies aren't needed anymore
                foreach (string old in Directory.GetDirectories(Path.GetDirectoryName(root)))
                    if (!AppPaths.SamePath(old, root)) Files.DeleteTree(old);
            }
        }

        static Assembly LoadEmbeddedDll(object sender, ResolveEventArgs e)
        {
            string name = new AssemblyName(e.Name).Name + ".dll";
            string onDisk = LibDir != null ? Path.Combine(LibDir, name) : null;
            if (onDisk != null && File.Exists(onDisk)) return Assembly.LoadFrom(onDisk);
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("lib/" + name))
            {
                if (s == null) return null;
                var bytes = new byte[s.Length];
                s.Read(bytes, 0, bytes.Length);
                return Assembly.Load(bytes);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool WebView2Ready()
        {
            try { return MainWindow.EngineVersion() != null; }
            catch (Exception e)
            {
                Log.Write("WebView2 check failed: " + e.Message);
                return false;
            }
        }

        // One copy at a time, per Windows account

        public static string InstanceName =>
            "BlessedHearth-" + Files.Sha1Hex(Encoding.UTF8.GetBytes(AppPaths.AppDataDir())).Substring(0, 10);

        // If a copy is already running, poke it so it comes forward (or quits, for "exit") and say so
        public static bool Knock(string message)
        {
            try
            {
                using (var pipe = new NamedPipeClientStream(".", InstanceName, PipeDirection.Out))
                {
                    pipe.Connect(500);
                    var b = Encoding.UTF8.GetBytes(message);
                    pipe.Write(b, 0, b.Length);
                    pipe.Flush();
                    return true;
                }
            }
            catch (Exception) { return false; }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        [MethodImpl(MethodImplOptions.NoInlining)]
        static int RunWindow(int port)
        {
            if (Knock(Args.Contains("--exit") ? "exit" : "show")) return 0;
            if (Args.Contains("--exit")) return 0;
            // Sharp on every screen, even with different scale settings (Windows 10 1703 and newer)
            try { if (!SetProcessDpiAwarenessContext((IntPtr)(-4))) SetProcessDPIAware(); }
            catch (Exception) { try { SetProcessDPIAware(); } catch (Exception) { } }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var srv = new Server(UiDir, port);
            if (Args.Contains("--print-url")) Console.WriteLine(srv.Url);
            var win = new MainWindow(srv.Url);
            Autostart.Instance.Refresh();
            bool hidden = Args.Contains("--autostart") && J.Truthy(AppState.App.Config["start_minimized"]);
            win.StartUp(hidden);
            if (win.IsDisposed)   // it couldn't start (and already said why), so there's nothing to run
            {
                srv.Stop();
                return 1;
            }
            AutoCheck.Instance.Start(hidden ? 60 : 600);   // started out of sight, so check every game soon
            Application.Run(win.Context);
            srv.Stop();
            Log.Write("stopped");
            return 0;
        }
    }
}
