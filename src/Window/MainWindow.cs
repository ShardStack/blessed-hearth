// The app's own window.
//
// What it is under the hood:
//  - One WinForms window with Windows' built-in browser engine (WebView2) filling it. That shows
//    the interface (ui\index.html), served by the little local server in Server.cs.
//  - The window has no Windows title bar. The interface draws its own, and the CSS marks that
//    strip as "app-region: drag", which tells WebView2 to treat it like a real title bar (drag,
//    double click to maximize, Snap). The minimize, maximize and close buttons are part of the
//    page too, and send a message here when clicked.
//  - A pane that opens on the right with a second WebView2 for curseforge.com pages. It keeps its
//    own cookies, so a "verify you're human" check only has to be done once, and it saves downloads
//    straight into the app's drop folder where ManualDownloads checks and installs them.
//  - The tray icon, update notifications, and making sure only one copy runs at a time.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace BlessedHearth
{
    public class MainWindow : Form
    {
        const int PaneWidth = 500;
        const int TitleH = 44;   // height of the strip at the top that works as the title bar

        public readonly ApplicationContext Context;
        Theme theme;
        readonly string url, origin;
        readonly WebView2 ui;
        readonly PaneView pane;
        readonly NotifyIcon tray;
        readonly ToolStripMenuItem trayOpen;
        readonly System.Windows.Forms.Timer anim = new System.Windows.Forms.Timer { Interval = 15 };
        int animFrom, animTo;
        DateTime animStart;
        bool docked;          // the pane is sitting where the details panel normally is
        bool paneOpening;
        bool quitting;
        volatile bool front;  // is the window in front of you right now (the update checker reads this)
        string notifyPid = "";
        Icon appIcon, badgeIcon;
        int dpi = 96;

        // Asks the page where the details panel is: [distance from its left edge to the right side of
        // the window, gap between the addon list and the panel], or null if it isn't showing.
        const string DockJs = @"JSON.stringify((() => {
  const ins = document.querySelector('#view-mine.on .inspector'), main = document.querySelector('#view-mine.on .mine-main');
  if (!ins || !main || getComputedStyle(ins).display === 'none') return null;
  const r = ins.getBoundingClientRect(), m = main.getBoundingClientRect();
  return r.width > 50 ? [window.innerWidth - r.left, r.left - m.right] : null;
})())";

        // The engine's version, or an error if WebView2 isn't on this PC
        public static string EngineVersion()
        {
            PointAtLoader();
            return CoreWebView2Environment.GetAvailableBrowserVersionString();
        }

        // Tells WebView2 where its loader DLL is. Windows only accepts this once, and only before
        // anything else touches WebView2, so it's guarded to happen exactly one time.
        static bool loaderSet;
        static void PointAtLoader()
        {
            if (loaderSet) return;
            CoreWebView2Environment.SetLoaderDllFolderPath(LoaderFolder());
            loaderSet = true;
        }

        // WebView2Loader.dll is the small native piece that finds the engine. One per processor type.
        static string LoaderFolder()
        {
            string arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64"
                        : Environment.Is64BitProcess ? "win-x64" : "win-x86";
            return Path.Combine(Program.LibDir, arch);
        }

        public MainWindow(string url)
        {
            this.url = url;
            origin = url.Split(new[] { "/?" }, StringSplitOptions.None)[0] + "/";
            theme = Themes.Get(AppState.App.Config["theme"]);
            Context = new ApplicationContext();
            Text = AppInfo.Name;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;   // kept so Windows still does the resizing, the shadow and Snap
            dpi = DeviceDpiSafe();
            Size = new Size(S(1320), S(860));
            MinimumSize = new Size(S(900), S(600));
            Padding = new Padding(1);   // a 1px edge in the theme's color, gone when maximized
            BackColor = Hex(theme.Edge);
            try { appIcon = new Icon(Path.Combine(Program.UiDir, "icons", "hearth.ico")); Icon = appIcon; } catch (Exception) { }

            // the interface
            var userData = Path.Combine(AppPaths.LocalDataDir(), "webview");
            ui = new WebView2
            {
                Dock = DockStyle.Fill,
                DefaultBackgroundColor = Hex(theme.Bg),
                CreationProperties = new CoreWebView2CreationProperties { UserDataFolder = userData, ProfileName = "interface" },
            };
            // The CurseForge pane gets its own profile with cookies kept between runs
            pane = new PaneView(this, new CoreWebView2CreationProperties { UserDataFolder = userData, ProfileName = "curseforge" })
            {
                Dock = DockStyle.Right, Width = 0, Visible = false,
            };
            Controls.Add(ui);
            Controls.Add(pane);
            ui.BringToFront();   // the fill control has to be last in line so the pane keeps its spot on the right

            anim.Tick += (s, e) => AnimateStep();

            // the tray icon, so closing the window can tuck the app away instead of quitting
            var menu = new ContextMenuStrip();
            trayOpen = new ToolStripMenuItem("Open " + AppInfo.Name, null, (s, e) => BringUp());
            trayOpen.Font = new Font(trayOpen.Font, FontStyle.Bold);
            menu.Items.Add(trayOpen);
            menu.Items.Add(new ToolStripMenuItem("Check for updates now", null, (s, e) => AutoCheck.Instance.CheckNow(asked: true)));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Exit", null, (s, e) => ExitApp()));
            tray = new NotifyIcon { Icon = SmallIcon(false), Text = AppInfo.Name, ContextMenuStrip = menu };
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) BringUp(); };
            tray.BalloonTipClicked += (s, e) =>
            {
                if (notifyPid != "") AppState.App.FocusProfile(notifyPid);   // open on the game those updates are for
                BringUp();
            };
            SetTray(J.Truthy(AppState.App.Config["tray"]));

            Activated += (s, e) => TrackFront();
            Deactivate += (s, e) => TrackFront();
            Resize += (s, e) => { TrackFront(); FrameChanged(); };
            VisibleChanged += (s, e) => TrackFront();

            HookUpShell();
            ListenForKnocks();
            InitInterface();
        }

        // Sizes are written for a normal 100% screen and scaled up for bigger settings
        int S(int px) => (int)Math.Round(px * dpi / 96.0);

        int DeviceDpiSafe() => DpiFor(this);

        // The screen's scale setting as dots per inch (96 is 100%, 144 is 150% and so on)
        public static int DpiFor(Control c)
        {
            try
            {
                int d = (int)GetDpiForWindow(c.Handle);   // Windows 10 1607 and newer
                if (d > 0) return d;
            }
            catch (Exception) { }
            try { using (var g = c.CreateGraphics()) return (int)g.DpiX; }
            catch (Exception) { return 96; }
        }

        [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);

        public static Color Hex(string hex)
        {
            hex = hex.TrimStart('#');
            return Color.FromArgb(Convert.ToInt32(hex.Substring(0, 2), 16), Convert.ToInt32(hex.Substring(2, 2), 16), Convert.ToInt32(hex.Substring(4, 2), 16));
        }

        public Theme Theme => theme;

        // The server runs on its own threads, and only the window's own thread may touch the window.
        // So every hook hops over with BeginInvoke (or Invoke when it needs an answer back).
        void HookUpShell()
        {
            Shell.PickFolder = (start, title) => (string)Invoke(new Func<string>(() => Pickers.Folder(this, start, title)));
            Shell.PickZip = start => (string)Invoke(new Func<string>(() => Pickers.Zip(this, start)));
            Shell.PaneOpen = items => BeginInvoke(new Action(() => ShowPane(items)));
            Shell.PaneClose = () => BeginInvoke(new Action(() =>
            {
                var t = new System.Windows.Forms.Timer { Interval = 1200 };
                t.Tick += (s, e) => { t.Dispose(); CloseIfIdle(); };
                t.Start();
            }));
            Shell.SetTheme = name => BeginInvoke(new Action(() => ApplyTheme(name)));
            Shell.SetTray = on => BeginInvoke(new Action(() => SetTray(on)));
            Shell.Notify = (title, body, pid) => BeginInvoke(new Action(() => ShowNotification(title, body, pid)));
            Shell.TrayStatus = n => BeginInvoke(new Action(() => ShowTrayStatus(n)));
            Shell.WindowInFront = () => front;
        }

        async void InitInterface()
        {
            try
            {
                PointAtLoader();
                await ui.EnsureCoreWebView2Async();
            }
            catch (Exception e)
            {
                Log.Write("the window's browser engine didn't start: " + e);
                Program.Fail("The app's window couldn't start Windows' browser engine (WebView2).\n\n" + e.Message);
                ExitApp();
                return;
            }
            var core = ui.CoreWebView2;
            var st = core.Settings;
            st.AreDefaultContextMenusEnabled = false;
            st.AreDevToolsEnabled = false;
            st.IsStatusBarEnabled = false;
            st.IsZoomControlEnabled = false;
            st.AreBrowserAcceleratorKeysEnabled = false;   // no F5, Ctrl+F and so on inside the interface
            st.IsPasswordAutosaveEnabled = false;           // never offer to save the API key
            st.IsGeneralAutofillEnabled = false;
            // makes the page's "app-region: drag" strip work as a title bar (needs a WebView2 from 2024 or newer)
            try { st.IsNonClientRegionSupportEnabled = true; }
            catch (Exception e) { Log.Write("title bar dragging isn't supported by this WebView2: " + e.Message); }
            // The interface stays on the app's own local server. Any link that tries to go somewhere
            // else opens in your normal browser instead (CurseForge only), so an outside page never
            // ends up running inside the app's window.
            core.NavigationStarting += (s, e) =>
            {
                string u = e.Uri ?? "";
                if (u.StartsWith(origin) || u.StartsWith("about:")) return;
                e.Cancel = true;
                if (u.StartsWith("https://") && CurseForgeApi.IsCurseForge(u)) EdgeWindow.OpenInBrowser(u);
            };
            core.NewWindowRequested += (s, e) => e.Handled = true;   // no pop-up windows from the interface
            core.WebMessageReceived += (s, e) => OnPageMessage(e.WebMessageAsJson);
            core.NavigationCompleted += (s, e) => TellPage();
            core.Navigate(url + "&shell=native");
        }

        // The page's window buttons
        void OnPageMessage(string json)
        {
            Dictionary<string, object> m;
            try { m = J.Obj(Json.Parse(json)); }
            catch (JsonException) { return; }
            if (m == null) return;
            switch (m.Str("win"))
            {
                case "min": WindowState = FormWindowState.Minimized; break;
                case "max": ToggleMax(); break;
                case "close": Close(); break;
            }
        }

        // Lets the page know whether to draw maximize or restore, and whether the pane is covering its buttons
        void TellPage()
        {
            if (ui.CoreWebView2 == null) return;
            try
            {
                ui.CoreWebView2.PostWebMessageAsJson(Json.Write(J.D("maximized", WindowState == FormWindowState.Maximized,
                                                                    "paneOpen", pane.Visible && pane.Width > 0)));
            }
            catch (Exception) { }
        }

        public void ToggleMax() => WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;

        public void StartUp(bool hidden)
        {
            if (IsDisposed) return;   // the browser engine already failed to start and the window closed itself
            if (hidden)
            {
                if (TrayOn()) Log.Write("started with Windows, waiting in the tray");
                else
                {
                    WindowState = FormWindowState.Minimized;
                    Show();
                }
            }
            else Show();
        }

        // The tray

        bool TrayOn() => J.Truthy(AppState.App.Config["tray"]);

        void SetTray(bool on)
        {
            tray.Visible = on;
            if (!on && !Visible && !quitting) BringUp();   // never leave the window hidden with no tray icon to get it back
        }

        // Update notifications

        void TrackFront() => front = Visible && WindowState != FormWindowState.Minimized && ActiveForm == this;

        void ShowNotification(string title, string body, string pid)
        {
            notifyPid = pid ?? "";
            if (tray.Visible) tray.ShowBalloonTip(15000, title, body, ToolTipIcon.None);
            else Flash();   // no tray icon to show it from, so flash the taskbar button
        }

        void ShowTrayStatus(int count)
        {
            string text = count > 0 ? AppInfo.Name + "\n" + count + " update" + (count != 1 ? "s" : "") + " ready"
                                    : AppInfo.Name + "\nEverything's up to date";
            tray.Text = text.Length > 63 ? text.Substring(0, 63) : text;
            tray.Icon = SmallIcon(count > 0);   // a dot in the corner while updates are waiting
        }

        // The app icon at tray size, optionally with a dot in the corner, drawn at the size Windows
        // asks for so it stays crisp
        Icon SmallIcon(bool badge)
        {
            if (!badge && appIcon != null) return new Icon(appIcon, SystemInformation.SmallIconSize);
            int w = SystemInformation.SmallIconSize.Width;
            using (var bmp = new Bitmap(w, w))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    if (appIcon != null) using (var src = new Icon(appIcon, w, w)) g.DrawIcon(src, new Rectangle(0, 0, w, w));
                    if (badge)
                    {
                        float d = w * 0.42f;
                        using (var pen = new Pen(Hex(theme.Bg), w * 0.06f))
                        using (var brush = new SolidBrush(Hex(theme.Accent)))
                        {
                            g.FillEllipse(brush, w - d - 1, 1, d, d);
                            g.DrawEllipse(pen, w - d - 1, 1, d, d);
                        }
                    }
                }
                IntPtr h = bmp.GetHicon();
                badgeIcon?.Dispose();
                badgeIcon = (Icon)Icon.FromHandle(h).Clone();
                DestroyIcon(h);
                return badgeIcon;
            }
        }

        public void BringUp()
        {
            if (!Visible) Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
            SetForegroundWindow(Handle);
        }

        public void ExitApp()
        {
            quitting = true;
            tray.Visible = false;
            Close();
            Context.ExitThread();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!quitting && TrayOn() && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                var cfg = AppState.App.Config;
                if (!J.Truthy(cfg["tray_hint_shown"]))   // say where it went, just the first time
                {
                    tray.ShowBalloonTip(6000, AppInfo.Name, "Still running here. Right-click this icon and choose Exit to quit.", ToolTipIcon.None);
                    lock (AppState.App.Sync)
                    {
                        cfg["tray_hint_shown"] = true;
                        cfg.Save();
                    }
                }
                return;
            }
            quitting = true;
            tray.Visible = false;
            base.OnFormClosing(e);
            Context.ExitThread();
        }

        void ApplyTheme(string name)
        {
            // Repaint the bits the window draws itself. The page repaints its own.
            theme = Themes.Get(name);
            BackColor = Hex(theme.Edge);
            ui.DefaultBackgroundColor = Hex(theme.Bg);
            pane.ApplyTheme(theme);
            if (tray.Icon != null && tray.Visible) tray.Icon = SmallIcon(tray.Text.Contains("ready"));
        }

        // The window frame

        void FrameChanged()
        {
            int m = WindowState == FormWindowState.Maximized ? 0 : 1;
            if (Padding.All != m) Padding = new Padding(m);
            pane.Invalidate(true);
            TellPage();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                int on = 1;
                DwmSetWindowAttribute(Handle, 20, ref on, 4);        // dark mode for any frame bits Windows still draws
                int round = 2;
                DwmSetWindowAttribute(Handle, 33, ref round, 4);     // rounded corners on Windows 11
            }
            catch (Exception) { }
        }

        const int WM_NCCALCSIZE = 0x83, WM_NCHITTEST = 0x84, WM_DPICHANGED = 0x02E0;
        const int HTCLIENT = 1, HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_NCCALCSIZE && m.WParam != IntPtr.Zero)
            {
                // Let Windows work out the normal frame, then give the title bar's space back to the
                // window. The side and bottom frame stay, and on Windows 10 and 11 those are
                // invisible, so they're the resize handles you grab just outside the window's edge.
                var p = (NCCALCSIZE_PARAMS)Marshal.PtrToStructure(m.LParam, typeof(NCCALCSIZE_PARAMS));
                int top = p.rgrc0.top;
                base.WndProc(ref m);
                p = (NCCALCSIZE_PARAMS)Marshal.PtrToStructure(m.LParam, typeof(NCCALCSIZE_PARAMS));
                // maximized windows hang over the screen edge by the frame's thickness, so keep that much
                p.rgrc0.top = top + (WindowState == FormWindowState.Maximized ? FrameThickness() : 0);
                Marshal.StructureToPtr(p, m.LParam, false);
                m.Result = IntPtr.Zero;
                return;
            }
            if (m.Msg == WM_NCHITTEST)
            {
                base.WndProc(ref m);
                if ((int)m.Result == HTCLIENT && WindowState != FormWindowState.Maximized)
                {
                    // The top edge has no frame of its own, so the 1px border there works as the resize handle
                    var pt = PointToClient(new Point((short)((long)m.LParam & 0xFFFF), (short)(((long)m.LParam >> 16) & 0xFFFF)));
                    int corner = S(12);
                    if (pt.Y <= 1) m.Result = (IntPtr)(pt.X < corner ? HTTOPLEFT : pt.X > ClientSize.Width - corner ? HTTOPRIGHT : HTTOP);
                    else if (pt.X <= 0) m.Result = (IntPtr)HTLEFT;
                    else if (pt.X >= ClientSize.Width - 1) m.Result = (IntPtr)HTRIGHT;
                    else if (pt.Y >= ClientSize.Height - 1) m.Result = (IntPtr)HTBOTTOM;
                }
                return;
            }
            if (m.Msg == WM_DPICHANGED)
            {
                // moved to a screen with a different scale setting: take Windows' suggested size
                dpi = (int)((long)m.WParam & 0xFFFF);
                var r = (RECT)Marshal.PtrToStructure(m.LParam, typeof(RECT));
                Bounds = new Rectangle(r.left, r.top, r.right - r.left, r.bottom - r.top);
                MinimumSize = new Size(S(900), S(600));
                pane.SetDpi(dpi);
                if (pane.Width > 0 && !docked) pane.Width = S(PaneWidth);
                m.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
        }

        int FrameThickness() => GetSystemMetrics(33 /* SM_CYFRAME */) + GetSystemMetrics(92 /* SM_CXPADDEDBORDER */);

        // The CurseForge pane
        //
        // When the details panel is showing, the CurseForge page takes its spot: the panel hides and the
        // pane gets exactly its width, so the addon list doesn't move. Otherwise the pane slides in.

        async void ShowPane(List<Dictionary<string, object>> items)
        {
            pane.Add(items);
            if (pane.Width > 0 || paneOpening) return;
            paneOpening = true;
            string spot = null;
            try { spot = ui.CoreWebView2 != null ? await ui.CoreWebView2.ExecuteScriptAsync(DockJs) : null; }
            catch (Exception) { }
            paneOpening = false;
            if (!pane.HasWork) return;   // it already finished (or got closed) while we were asking
            List<object> box = null;
            try
            {
                // the script hands back a JSON string, and WebView2 wraps that in JSON once more
                string inner = spot != null ? Json.Parse(spot) as string : null;
                box = inner != null ? Json.Parse(inner) as List<object> : null;
            }
            catch (JsonException) { }
            anim.Stop();
            pane.Visible = true;
            if (box != null && box.Count == 2)
            {
                double w = Convert.ToDouble(box[0]), gap = Convert.ToDouble(box[1]);
                // the page measures in its own pixels, which are the screen's pixels divided by the scale
                double zoom = dpi / 96.0;
                if (w >= 300)
                {
                    docked = true;
                    await ui.CoreWebView2.ExecuteScriptAsync("document.body.style.setProperty('--dock-pad', '" + Math.Max(0, (int)Math.Round(gap))
                                                             + "px'); document.body.classList.add('pane-docked')");
                    pane.Width = (int)Math.Round(w * zoom);
                    Log.Write("website download page opened in the details panel's place (" + (int)Math.Round(w) + "px)");
                    TellPage();
                    return;
                }
            }
            AnimateTo(S(PaneWidth));
        }

        public void HidePane()
        {
            anim.Stop();
            if (docked)   // swap back: pane out, details panel back in
            {
                docked = false;
                if (ui.CoreWebView2 != null) ui.CoreWebView2.ExecuteScriptAsync("document.body.classList.remove('pane-docked')");
                pane.Width = 0;
                pane.Visible = false;
                TellPage();
            }
            else AnimateTo(0);
            pane.Reset();
        }

        void CloseIfIdle()
        {
            if (!pane.HasWork) HidePane();
        }

        // the pane slides by animating its actual width, easing out over about a quarter second
        void AnimateTo(int width)
        {
            animFrom = pane.Width;
            animTo = width;
            animStart = DateTime.UtcNow;
            pane.Visible = true;
            anim.Start();
        }

        void AnimateStep()
        {
            double t = Math.Min(1, (DateTime.UtcNow - animStart).TotalMilliseconds / 260.0);
            double eased = 1 - Math.Pow(1 - t, 3);
            pane.Width = (int)Math.Round(animFrom + (animTo - animFrom) * eased);
            if (t >= 1)
            {
                anim.Stop();
                if (pane.Width == 0) pane.Visible = false;
                TellPage();
            }
        }

        // One copy at a time. A second copy knocks on this pipe ("show" or "exit") and quits.
        // The pipe is locked to your Windows account so other accounts on the PC can't knock.
        void ListenForKnocks()
        {
            var t = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        var security = new PipeSecurity();
                        security.AddAccessRule(new PipeAccessRule(System.Security.Principal.WindowsIdentity.GetCurrent().User,
                                                                  PipeAccessRights.ReadWrite, System.Security.AccessControl.AccessControlType.Allow));
                        using (var pipe = new NamedPipeServerStream(Program.InstanceName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                                                                    PipeOptions.None, 256, 256, security))
                        {
                            pipe.WaitForConnection();
                            var buf = new byte[64];
                            int n = pipe.Read(buf, 0, buf.Length);
                            string msg = Encoding.UTF8.GetString(buf, 0, Math.Max(0, n)).Trim();
                            BeginInvoke(new Action(() => { if (msg == "exit") ExitApp(); else BringUp(); }));
                        }
                    }
                    catch (Exception e)
                    {
                        Log.Write("single copy check: " + e.Message);
                        Thread.Sleep(2000);
                    }
                }
            }) { IsBackground = true, Name = "knock" };
            t.Start();
        }

        void Flash()
        {
            var fi = new FLASHWINFO { cbSize = (uint)Marshal.SizeOf(typeof(FLASHWINFO)), hwnd = Handle, dwFlags = 3 | 12, uCount = 0, dwTimeout = 0 };
            FlashWindowEx(ref fi);
        }

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int left, top, right, bottom; }

        [StructLayout(LayoutKind.Sequential)]
        struct NCCALCSIZE_PARAMS
        {
            public RECT rgrc0, rgrc1, rgrc2;
            public IntPtr lppos;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct FLASHWINFO { public uint cbSize; public IntPtr hwnd; public uint dwFlags, uCount, dwTimeout; }

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool FlashWindowEx(ref FLASHWINFO info);
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);
        [DllImport("user32.dll")] public static extern bool ReleaseCapture();
        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    }
}
