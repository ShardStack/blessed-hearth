// The CurseForge pane, and the little hand-drawn buttons the window uses.
//
// The pane works through a queue of CurseForge download pages, one at a time. Its top strip keeps
// the window's minimize, maximize and close buttons, because while it's open it covers the corner
// where the page normally draws them.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace BlessedHearth
{
    public class PaneView : Panel
    {
        readonly Panel strip, head;
        readonly Label step, title, sub;
        readonly Bar bar;
        readonly WebView2 view;
        readonly List<GlyphButton> buttons = new List<GlyphButton>();
        readonly List<GlyphButton> winButtons = new List<GlyphButton>();
        readonly List<Dictionary<string, object>> queue = new List<Dictionary<string, object>>();
        Dictionary<string, object> current;
        int dpi = 96;
        Theme theme;

        public PaneView(MainWindow win, CoreWebView2CreationProperties props)
        {

            theme = win.Theme;
            DoubleBuffered = true;

            view = new WebView2 { Dock = DockStyle.Fill, CreationProperties = props };
            bar = new Bar { Dock = DockStyle.Top, Height = 3, Visible = false };

            head = new Panel { Dock = DockStyle.Top };
            step = new Label { AutoSize = false, Font = new Font("Segoe UI Semibold", 9f), BackColor = Color.Transparent };
            title = new Label { AutoSize = false, Text = "CurseForge", Font = new Font("Segoe UI Semibold", 11.25f), BackColor = Color.Transparent, AutoEllipsis = true };
            sub = new Label { AutoSize = false, Font = new Font("Segoe UI", 9f), BackColor = Color.Transparent };
            head.Controls.AddRange(new Control[] { step, title, sub });
            foreach (var spec in new[] { Tuple.Create("back", "Back"), Tuple.Create("reload", "Reload"), Tuple.Create("close", "Close") })
            {
                var b = new GlyphButton(spec.Item1, () => theme) { Tip = spec.Item2 };
                string kind = spec.Item1;
                b.Click += (s, e) =>
                {
                    if (kind == "back") { if (view.CanGoBack) view.GoBack(); }
                    else if (kind == "reload") view.Reload();
                    else win.HidePane();
                };
                buttons.Add(b);
                head.Controls.Add(b);
            }
            head.Resize += (s, e) => LayoutHead();

            // the window buttons float over this strip, and the rest of it drags the window around
            strip = new Panel { Dock = DockStyle.Top };
            foreach (string kind in new[] { "min", "max", "close" })
            {
                var b = new GlyphButton("win-" + kind, () => theme) { Window = win };
                string k = kind;
                b.Click += (s, e) =>
                {
                    if (k == "min") win.WindowState = FormWindowState.Minimized;
                    else if (k == "max") win.ToggleMax();
                    else win.Close();
                };
                winButtons.Add(b);
                strip.Controls.Add(b);
            }
            strip.Resize += (s, e) => LayoutStrip();
            strip.MouseDown += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                if (e.Clicks == 2) { win.ToggleMax(); return; }
                // hand the drag to Windows as if the real title bar was grabbed, so Snap still works
                MainWindow.ReleaseCapture();
                MainWindow.SendMessage(win.Handle, 0xA1 /* WM_NCLBUTTONDOWN */, (IntPtr)2 /* HTCAPTION */, IntPtr.Zero);
            };

            Controls.Add(view);
            Controls.Add(bar);
            Controls.Add(head);
            Controls.Add(strip);
            SetDpi(DpiOf(win));
            ApplyTheme(theme);
            Init();
        }

        static int DpiOf(Control c) => MainWindow.DpiFor(c);

        int S(int px) => (int)Math.Round(px * dpi / 96.0);

        public void SetDpi(int newDpi)
        {
            dpi = newDpi;
            strip.Height = S(44);
            head.Height = S(96);
            bar.Height = Math.Max(2, S(3));
            LayoutStrip();
            LayoutHead();
        }

        void LayoutStrip()
        {
            int w = S(46), h = S(40), x = strip.Width;
            for (int i = winButtons.Count - 1; i >= 0; i--)
            {
                x -= w;
                winButtons[i].SetBounds(x, 0, w, h);
            }
        }

        void LayoutHead()
        {
            int pad = S(16), bw = S(30), right = head.Width - S(10);
            for (int i = buttons.Count - 1; i >= 0; i--)
            {
                right -= bw;
                buttons[i].SetBounds(right, S(10), bw, bw);
                right -= S(4);
            }
            int textW = Math.Max(10, right - pad - S(6));
            step.SetBounds(pad, S(10), textW, S(17));
            title.SetBounds(pad, S(27), textW, S(24));
            sub.SetBounds(pad, S(51), textW, head.Height - S(55));
        }

        public void ApplyTheme(Theme t)
        {
            theme = t;
            BackColor = MainWindow.Hex(t.Pane);
            strip.BackColor = MainWindow.Hex(t.Pane);
            head.BackColor = MainWindow.Hex(t.Head);
            step.ForeColor = MainWindow.Hex(t.Step);
            title.ForeColor = MainWindow.Hex(t.Text);
            sub.ForeColor = MainWindow.Hex(t.Muted);
            bar.Track = Color.FromArgb(30, t.Line[0], t.Line[1], t.Line[2]);
            bar.Fill = MainWindow.Hex(t.Accent);
            view.DefaultBackgroundColor = MainWindow.Hex(t.Pane);
            Invalidate(true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            // a faint line down the pane's left edge and under its header, in the theme's line color
            using (var p = new Pen(Color.FromArgb(40, theme.Line[0], theme.Line[1], theme.Line[2])))
                e.Graphics.DrawLine(p, 0, 0, 0, Height);
        }

        async void Init()
        {
            try
            {
                await view.EnsureCoreWebView2Async();
            }
            catch (Exception e)
            {
                Log.Write("the CurseForge pane's browser engine didn't start: " + e.Message);
                return;
            }
            var core = view.CoreWebView2;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            // it only goes to https pages (the test suite's pretend CurseForge runs on plain http)
            core.NavigationStarting += (s, e) =>
            {
                string u = e.Uri ?? "";
                string testBase = AppInfo.TestApiBase;
                bool ok = u.StartsWith("https://") || u.StartsWith("about:") || u.StartsWith("blob:") || u.StartsWith("data:")
                          || (testBase != "" && u.StartsWith(testBase));
                if (!ok) e.Cancel = true;
            };
            // links that want a new window load right here instead
            core.NewWindowRequested += (s, e) =>
            {
                e.Handled = true;
                if (!string.IsNullOrEmpty(e.Uri)) core.Navigate(e.Uri);
            };
            core.NavigationStarting += (s, e) => { bar.Busy = true; bar.Visible = true; };
            core.NavigationCompleted += (s, e) => { if (bar.Busy) bar.Visible = false; };
            core.DownloadStarting += OnDownload;
            if (current != null) core.Navigate(current.Str("url"));
        }

        public bool HasWork => current != null || queue.Count > 0;

        public void Add(List<Dictionary<string, object>> items)
        {
            var known = new HashSet<string>(queue.Select(i => i.Str("fileId")));
            if (current != null) known.Add(current.Str("fileId"));
            queue.AddRange(items.Where(i => !known.Contains(i.Str("fileId"))));
            if (current == null) Next();
            else UpdateStep();
        }

        void Next()
        {
            if (queue.Count == 0)
            {
                current = null;
                sub.Text = "All done.";
                return;
            }
            current = queue[0];
            queue.RemoveAt(0);
            title.Text = current.Str("name");
            sub.Text = "Opening the download page on curseforge.com. If it asks you to verify you're human, "
                       + "do that, and click Download if the file doesn't start on its own.";
            UpdateStep();
            if (view.CoreWebView2 != null) view.CoreWebView2.Navigate(current.Str("url"));
        }

        void UpdateStep()
        {
            int total = queue.Count + (current != null ? 1 : 0);
            step.Text = total > 1 ? "Website download, " + total + " to go" : "Website download";
        }

        public void Reset()
        {
            queue.Clear();
            current = null;
            if (view.CoreWebView2 != null) view.CoreWebView2.Navigate("about:blank");
        }

        void OnDownload(object sender, CoreWebView2DownloadStartingEventArgs e)
        {
            string name = Path.GetFileName(e.ResultFilePath ?? "");
            // The pane is only here to fetch addon zips we're waiting on. Anything else gets turned away.
            if (current == null || !name.ToLowerInvariant().EndsWith(".zip"))
            {
                e.Cancel = true;
                return;
            }
            e.ResultFilePath = Path.Combine(AppState.App.Manual.AppFolder, name);
            e.Handled = true;   // no browser download bubble, the pane shows its own progress
            sub.Text = "Downloading " + name;
            bar.Busy = false;
            bar.Value = -1;
            bar.Visible = true;
            var op = e.DownloadOperation;
            op.BytesReceivedChanged += (s, a) =>
            {
                ulong? total = op.TotalBytesToReceive;
                if (total.HasValue && total.Value > 0) bar.Value = (int)(op.BytesReceived * 100 / (long)total.Value);
            };
            op.StateChanged += (s, a) =>
            {
                if (op.State == CoreWebView2DownloadState.InProgress) return;
                bar.Visible = false;
                if (op.State == CoreWebView2DownloadState.Completed)
                {
                    sub.Text = "Downloaded " + name + ". Installing...";
                    var t = new Timer { Interval = 900 };
                    t.Tick += (s2, a2) => { t.Dispose(); Next(); };
                    t.Start();
                }
                else sub.Text = "The download of " + name + " didn't finish. Click Download on the page to try again.";
            };
        }
    }

    // A thin progress line. Value -1 (or Busy) shows a sliding stripe for "working on it".
    public class Bar : Control
    {
        public Color Track = Color.FromArgb(30, 255, 255, 255), Fill = Color.MediumPurple;
        int value = -1;
        bool busy;
        readonly Timer tick = new Timer { Interval = 30 };
        int phase;

        public Bar()
        {
            DoubleBuffered = true;
            tick.Tick += (s, e) => { phase = (phase + 3) % 200; Invalidate(); };
        }

        public bool Busy
        {
            get { return busy; }
            set { busy = value; Sync(); }
        }

        public int Value
        {
            get { return value; }
            set { this.value = value; Sync(); }
        }

        void Sync()
        {
            if (busy || value < 0) tick.Start(); else tick.Stop();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            using (var t = new SolidBrush(Track)) e.Graphics.FillRectangle(t, ClientRectangle);
            using (var f = new SolidBrush(Fill))
            {
                if (!busy && value >= 0) e.Graphics.FillRectangle(f, 0, 0, Width * Math.Min(100, value) / 100, Height);
                else
                {
                    int w = Width / 3, x = (int)((phase / 200.0) * (Width + w)) - w;
                    e.Graphics.FillRectangle(f, x, 0, w, Height);
                }
            }
        }
    }

    // The pane's line icons (back, reload, close) and the window's minimize, maximize and close,
    // drawn by hand so they match the interface
    public class GlyphButton : Control
    {
        readonly string kind;
        readonly Func<Theme> theme;
        bool hover;
        public string Tip;
        public MainWindow Window;
        readonly ToolTip tip = new ToolTip();

        public GlyphButton(string kind, Func<Theme> theme)
        {
            this.kind = kind;
            this.theme = theme;
            DoubleBuffered = true;
            Cursor = kind.StartsWith("win-") ? Cursors.Arrow : Cursors.Hand;
            SetStyle(ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
        }

        protected override void OnCreateControl()
        {
            base.OnCreateControl();
            string t = Tip ?? (kind == "win-min" ? "Minimize" : kind == "win-max" ? "Maximize" : kind == "win-close" ? "Close" : null);
            if (t != null) tip.SetToolTip(this, t);
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var t = theme();
            float s = Height / 40f;
            if (kind.StartsWith("win-"))
            {
                if (hover)
                    using (var b = new SolidBrush(kind == "win-close" ? Color.FromArgb(0xE8, 0x11, 0x23) : Color.FromArgb(22, 255, 255, 255)))
                        g.FillRectangle(b, ClientRectangle);
                var baseColor = MainWindow.Hex(t.Text);
                var c = hover && kind == "win-close" ? Color.White
                        : Color.FromArgb((int)(baseColor.R / 1.15), (int)(baseColor.G / 1.15), (int)(baseColor.B / 1.15));
                using (var pen = new Pen(c, 1.1f * s))
                {
                    float cx = Width / 2f + .5f, cy = Height / 2f + .5f, r = 5 * s;
                    if (kind == "win-min") g.DrawLine(pen, cx - r, cy, cx + r, cy);
                    else if (kind == "win-max")
                    {
                        if (Window != null && Window.WindowState == FormWindowState.Maximized)   // two overlapping squares means restore
                        {
                            g.DrawRectangle(pen, cx - r, cy - 3 * s, 8 * s, 8 * s);
                            g.DrawLine(pen, cx - 3 * s, cy - r, cx + r, cy - r);
                            g.DrawLine(pen, cx + r, cy - r, cx + r, cy + 3 * s);
                        }
                        else g.DrawRectangle(pen, cx - r, cy - r, 2 * r, 2 * r);
                    }
                    else
                    {
                        g.DrawLine(pen, cx - r, cy - r, cx + r, cy + r);
                        g.DrawLine(pen, cx + r, cy - r, cx - r, cy + r);
                    }
                }
                return;
            }
            if (hover)
                using (var b = new SolidBrush(Color.FromArgb(20, 255, 255, 255)))
                using (var path = Rounded(new RectangleF(0, 0, Width - 1, Height - 1), 8 * Height / 30f))
                    g.FillPath(b, path);
            // the same shapes as the interface's icons, on a 24 unit grid
            float u = Math.Min(Width, Height) * 0.6f / 24f, ox = (Width - 24 * u) / 2, oy = (Height - 24 * u) / 2;
            Func<float, float, PointF> P = (x, y) => new PointF(ox + x * u, oy + y * u);
            using (var pen = new Pen(MainWindow.Hex(t.Muted), 1.8f * u) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                if (kind == "back") g.DrawLines(pen, new[] { P(15, 5), P(8, 12), P(15, 19) });
                else if (kind == "close")
                {
                    g.DrawLine(pen, P(6, 6), P(18, 18));
                    g.DrawLine(pen, P(18, 6), P(6, 18));
                }
                else
                {
                    var box = new RectangleF(ox + 4 * u, oy + 4 * u, 16 * u, 16 * u);
                    g.DrawArc(pen, box, 200, 140);
                    g.DrawArc(pen, box, 20, 140);
                    g.DrawLines(pen, new[] { P(4, 4), P(4, 8), P(8, 8) });
                    g.DrawLines(pen, new[] { P(20, 20), P(20, 16), P(16, 16) });
                }
            }
        }

        static GraphicsPath Rounded(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    // Windows' modern folder picker (the same one File Explorer uses), through its COM interface.
    // WinForms only has the old tree style one built in, so that's the backup.
    public static class Pickers
    {
        public static string Folder(IWin32Window owner, string start, string title)
        {
            try
            {
                var dlg = (IFileOpenDialog)new FileOpenDialogRCW();
                dlg.SetOptions(0x20 /* FOS_PICKFOLDERS */ | 0x40 /* FOS_FORCEFILESYSTEM */);
                dlg.SetTitle(title);
                if (!string.IsNullOrEmpty(start) && Directory.Exists(start))
                {
                    IShellItem folder;
                    if (SHCreateItemFromParsingName(start, IntPtr.Zero, typeof(IShellItem).GUID, out folder) == 0) dlg.SetFolder(folder);
                }
                if (dlg.Show(owner.Handle) != 0) return null;   // cancelled
                IShellItem item;
                dlg.GetResult(out item);
                string path;
                item.GetDisplayName(0x80058000 /* SIGDN_FILESYSPATH */, out path);
                return path;
            }
            catch (Exception e) when (!(e is OperationCanceledException))
            {
                using (var d = new FolderBrowserDialog { Description = title, ShowNewFolderButton = false })
                {
                    if (!string.IsNullOrEmpty(start) && Directory.Exists(start)) d.SelectedPath = start;
                    return d.ShowDialog(owner) == DialogResult.OK ? d.SelectedPath : null;
                }
            }
        }

        public static string Zip(IWin32Window owner, string start)
        {
            using (var d = new OpenFileDialog { Title = "Choose the addon zip you downloaded", InitialDirectory = start ?? "",
                                               Filter = "Zip files (*.zip)|*.zip|All files (*.*)|*.*" })
                return d.ShowDialog(owner) == DialogResult.OK ? d.FileName : null;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        static extern int SHCreateItemFromParsingName(string path, IntPtr bindCtx, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItem item);

        [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
        class FileOpenDialogRCW { }

        [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IFileOpenDialog
        {
            [PreserveSig] int Show(IntPtr parent);
            void SetFileTypes(uint count, IntPtr specs);
            void SetFileTypeIndex(uint index);
            void GetFileTypeIndex(out uint index);
            void Advise(IntPtr events, out uint cookie);
            void Unadvise(uint cookie);
            void SetOptions(uint options);
            void GetOptions(out uint options);
            void SetDefaultFolder(IShellItem item);
            void SetFolder(IShellItem item);
            void GetFolder(out IShellItem item);
            void GetCurrentSelection(out IShellItem item);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
            void GetResult(out IShellItem item);
            void AddPlace(IShellItem item, int place);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string ext);
            void Close(int hr);
            void SetClientGuid(ref Guid guid);
            void ClearClientData();
            void SetFilter(IntPtr filter);
            void GetResults(out IntPtr items);
            void GetSelectedItems(out IntPtr items);
        }

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IShellItem
        {
            void BindToHandler(IntPtr bindCtx, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem parent);
            void GetDisplayName(uint sigdn, [MarshalAs(UnmanagedType.LPWStr)] out string name);
            void GetAttributes(uint mask, out uint attribs);
            void Compare(IShellItem other, uint hint, out int order);
        }
    }
}
