// iCloud panel: frameless WebView2 window (own profile), sized/positioned from
// config.json in the parent folder. Two hover buttons sit in the page's
// bottom-right corner: a lock (unlock to move/resize, lock to save) and a gear
// (always on top / pin to desktop). Setting a position on any monitor sticks.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace iCloudPanel
{
    public class PanelForm : Form
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr h, int index);
        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr h, int index, int value);
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr h, ref POINT p);
        [DllImport("user32.dll")]
        private static extern int SetWindowRgn(IntPtr h, IntPtr rgn, bool redraw);
        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int ht);
        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")]
        private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);

        private struct RECT { public int L, T, R, B; }
        private struct POINT { public int X, Y; }

        private const int GWL_STYLE = -16;
        private const int WS_CAPTION = 0x00C00000;
        private const int WS_THICKFRAME = 0x00040000;
        private const int WS_SYSMENU = 0x00080000;
        private const int WS_MINIMIZEBOX = 0x00020000;
        private const int WS_MAXIMIZEBOX = 0x00010000;

        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_FRAMECHANGED = 0x0020;

        private WebView2 web;
        private CoreWebView2Environment coreEnv;
        private string appDir;
        private string configPath;
        private string cookieFile;
        private int radius = 14;
        private double zoom = 1.0;
        private bool locked = true;
        private bool topMost;
        private bool pinned;
        private bool autoHide;
        // iCloud's account strip (Your iCloud / Storage / Devices) at the bottom of
        // the page. The corner button toggles it; remembered across restarts.
        private bool showInfo = true;
        private DateTime shownAt = DateTime.MinValue;

        // "New iCloud Note" arrives from outside the process: the Start-menu
        // shortcut sends a WM_COPYDATA to this window, which runs the page-side
        // command. No second browser, and it works while the panel is pinned.
        private const int WM_COPYDATA = 0x004A;
        private const int NewNoteMagic = 0x4E4F5445;   // 'NOTE'
        private const int AppPinMagic = 0x50494E31;    // 'PIN1'

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfo(string pszPath, uint attrs, ref SHFILEINFO psfi, uint cb, uint flags);
        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);
        private bool newNotePending;
        private bool pageReady;

        [StructLayout(LayoutKind.Sequential)]
        private struct COPYDATASTRUCT
        {
            public IntPtr dwData;
            public int cbData;
            public IntPtr lpData;
        }

        // Panel background: default (iCloud's own wallpaper), a solid colour, or a
        // local image, each with an opacity step. iCloud paints its wallpaper on
        // .homepage-gradient-background / .welcome-page-gradient-background.
        private string bgKind = "default";
        private string bgColor = "#0b2a52";
        private string bgExt = "";
        private string bgDataUri = "";
        private double bgOpacity = 1.0;

        // Dashboard: the widget row under iCloud's tiles. The page owns the
        // layout state and posts it back verbatim; the host stores it and feeds
        // widget data (weather, Outlook) into the page.
        private string dashJson = "";
        private double weatherLat = double.NaN;
        private double weatherLon = double.NaN;
        private string weatherPlace = "";
        private string weatherData = "";
        private string outlookData = "";
        // When set, widget data comes from another machine (gibson) running the
        // same helper behind a LAN endpoint, so this PC does no Outlook work.
        private string outlookUrl = "";
        private Timer weatherTimer;
        private Timer outlookTimer;
        private bool winLocTried;

        private static string logDir;

        private static void Log(string message)
        {
            try
            {
                string dir = logDir ?? Path.GetDirectoryName(Application.ExecutablePath);
                File.AppendAllText(Path.Combine(dir, "panel.log"),
                    DateTime.Now.ToString("HH:mm:ss") + "  " + message + "\r\n");
            }
            catch { }
        }

        public PanelForm()
        {
            appDir = ResolveAppDir();
            logDir = appDir;
            string root = Path.GetFullPath(Path.Combine(appDir, ".."));
            configPath = Path.Combine(root, "config.json");
            cookieFile = Path.Combine(appDir, "session-cookies.bin");

            int width = 594;
            int height = 851;
            int? px = null;
            int? py = null;

            if (File.Exists(configPath))
            {
                string text = File.ReadAllText(configPath);
                width  = ReadInt(text, "width", width);
                height = ReadInt(text, "height", height);
                radius = ReadInt(text, "radius", radius);
                zoom = ReadDouble(text, "zoom", zoom);
                topMost = ReadBool(text, "topmost");
                pinned = ReadBool(text, "pinned");
                autoHide = ReadBool(text, "autohide");
                Match im = Regex.Match(text, "\"showinfo\"\\s*:\\s*(true|false)");
                if (im.Success) { showInfo = im.Groups[1].Value == "true"; }
                Match nm = Regex.Match(text, "\"name\"\\s*:\\s*\"([^\"]*)\"");
                if (nm.Success) { userName = nm.Groups[1].Value; }
                nameLoaded = true;
                bgOpacity = ReadDouble(text, "bgopacity", bgOpacity);
                Match bm = Regex.Match(text, "\"bgkind\"\\s*:\\s*\"([a-z]*)\"");
                if (bm.Success) { bgKind = bm.Groups[1].Value; }
                bm = Regex.Match(text, "\"bgcolor\"\\s*:\\s*\"(#[0-9a-fA-F]{6})\"");
                if (bm.Success) { bgColor = bm.Groups[1].Value; }
                bm = Regex.Match(text, "\"bgimage\"\\s*:\\s*\"([a-z]*)\"");
                if (bm.Success) { bgExt = bm.Groups[1].Value; }
                bm = Regex.Match(text, "\"bgoffset\"\\s*:\\s*(-?[0-9]+)");
                if (bm.Success)
                {
                    int offsetValue;
                    if (int.TryParse(bm.Groups[1].Value, out offsetValue))
                    {
                        bgOffset = Math.Max(-300, Math.Min(300, offsetValue));
                    }
                }
                string dashBlock = ExtractObject(text, "dash");
                if (dashBlock.Length > 2) { dashJson = dashBlock; }
                string weatherBlock = ExtractObject(text, "weather");
                if (weatherBlock.Length > 2)
                {
                    weatherLat = ReadDouble(weatherBlock, "lat", double.NaN);
                    weatherLon = ReadDouble(weatherBlock, "lon", double.NaN);
                    Match pm = Regex.Match(weatherBlock, "\"place\"\\s*:\\s*\"([^\"]*)\"");
                    if (pm.Success) { weatherPlace = pm.Groups[1].Value; }
                }
                Match um = Regex.Match(text, "\"outlookUrl\"\\s*:\\s*\"([^\"]*)\"");
                if (um.Success) { outlookUrl = um.Groups[1].Value; }
                int v;
                if (TryReadInt(text, "x", out v)) { px = v; }
                if (TryReadInt(text, "y", out v)) { py = v; }
            }

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(20, 26, 33);
            Text = "iCloud";
            MinimizeBox = false;
            MaximizeBox = false;
            DoubleBuffered = true;

            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            int left;
            int top;
            if (px.HasValue && py.HasValue)
            {
                // Trust the saved spot wherever it is; only fall back when the
                // rectangle no longer lands on any monitor.
                left = px.Value;
                top = py.Value;
                if (!OnAnyScreen(new Rectangle(left, top, width, height)))
                {
                    left = area.Right - width - 12;
                    top = area.Bottom - height - 12;
                }
            }
            else
            {
                left = area.Right - width - 12;
                top  = area.Bottom - height - 12;
            }
            Bounds = new Rectangle(left, top, width, height);

            web = new WebView2();
            web.Dock = DockStyle.Fill;
            web.DefaultBackgroundColor = Color.FromArgb(20, 26, 33);
            Controls.Add(web);
            Load += OnPanelLoad;
            Shown += delegate(object s, EventArgs a)
            {
                Log("shown at " + Bounds);
                ApplyLayering();
            };
            VisibleChanged += delegate(object s, EventArgs a)
            {
                if (Visible)
                {
                    shownAt = DateTime.Now;
                    ApplyLayering();
                }
            };
            Activated += delegate(object s, EventArgs a)
            {
                // A window that was fully covered can come back without a frame
                // until it is asked to paint again.
                if (web != null) { web.Invalidate(); }
            };
            Deactivate += delegate(object s, EventArgs a)
            {
                if (autoHide)
                {
                    // Showing from the tray (a background process) can deliver
                    // Deactivate before the panel ever gets focus. Hiding then makes
                    // the tray click look like it did nothing.
                    if ((DateTime.Now - shownAt).TotalMilliseconds < 600)
                    {
                        Log("auto-hide: ignored focus loss right after showing");
                        return;
                    }
                    AutoHideNow();
                    return;
                }
                if (pinned && !topMost) { SendToBottom(); }
            };
        }

        // Works both as a compiled exe (app dir = its own folder) and hosted
        // inside powershell.exe, where the host supplies ICLOUD_PANEL_DIR.
        private static string ResolveAppDir()
        {
            string fromEnv = Environment.GetEnvironmentVariable("ICLOUD_PANEL_DIR");
            if (!string.IsNullOrEmpty(fromEnv) && Directory.Exists(fromEnv)) { return fromEnv; }
            return Path.GetDirectoryName(Application.ExecutablePath);
        }

        private static bool OnAnyScreen(Rectangle rect)
        {
            foreach (Screen screen in Screen.AllScreens)
            {
                if (screen.WorkingArea.IntersectsWith(rect)) { return true; }
            }
            return false;
        }

        private async void OnPanelLoad(object sender, EventArgs e)
        {
            ApplyRoundedCorners(2);
            ApplyRegion();
            ApplyLayering();
            try
            {
                Log("creating webview2 environment");
                string dataDir = Path.Combine(appDir, "profile");
                Directory.CreateDirectory(dataDir);
                CoreWebView2Environment env = await CoreWebView2Environment.CreateAsync(null, dataDir, null);
                coreEnv = env;
                Log("environment ready");
                await web.EnsureCoreWebView2Async(env);
                Log("webview2 ready");
                ApplySystemColorScheme();
                ApplyZoom();

                await web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(BuildInjectedScript());
                await web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(BuildNewNoteScript());
                Log("page css + corner buttons injected");

                web.CoreWebView2.WebMessageReceived += OnWebMessage;
                web.CoreWebView2.ContextMenuRequested += OnContextMenuRequested;
                Log("context menu hooked");
                // A new document starts out unaware of our state; resync it.
                web.CoreWebView2.NavigationCompleted += delegate(object s3, CoreWebView2NavigationCompletedEventArgs e3)
                {
                    pageReady = false;
                    NotifyPage();
                    ScheduleCookieSync();
                    ApplyZoom();
                    ScheduleDashPush();
                    Timer ready = new Timer();
                    ready.Interval = 4000;
                    ready.Tick += delegate(object rs, EventArgs ra)
                    {
                        ready.Stop();
                        ready.Dispose();
                        pageReady = true;
                        PollPinQueue();
                    };
                    ready.Start();
                    // A new-note request that arrived during a cold start waits
                    // for the first real iCloud document.
                    if (newNotePending) { RunNewNote(); }
                };

                // Keep popups (Apple ID sign-in) inside the panel.
                web.CoreWebView2.NewWindowRequested += delegate(object s, CoreWebView2NewWindowRequestedEventArgs args)
                {
                    args.Handled = true;
                    web.CoreWebView2.Navigate(args.Uri);
                };
                // Put back the Apple cookies captured while signed in, then load.
                await RestoreCookiesAsync();

                // A dead renderer leaves a blank surface where the page was, which
                // looks like the panel "turning into" an empty box. Log it.
                web.CoreWebView2.ProcessFailed += delegate(object pfSender, CoreWebView2ProcessFailedEventArgs pfArgs)
                {
                    Log("webview2 process failed: " + pfArgs.ProcessFailedKind);
                };

                web.CoreWebView2.Navigate("https://www.icloud.com/");
                Log("navigated");

                // Keep Apple's sign-in alive across restarts: WebView2 discards
                // cookies without an expiry when the process exits (Chrome only
                // keeps them with session restore on), and the iCloud web session
                // is built from exactly those. Give them an expiry instead.
                string probeFlag = Path.Combine(appDir, "cookie-selftest.flag");
                CookieProbe(File.Exists(probeFlag), probeFlag);
                ScheduleCookieSync();
                Timer cookieTimer = new Timer();
                cookieTimer.Interval = 60000;
                cookieTimer.Tick += SyncCookiesNow;
                cookieTimer.Start();

                // Remember the zoom the user dialled in (Ctrl +/- or Ctrl+wheel).
                Timer zoomTimer = new Timer();
                zoomTimer.Interval = 1200;
                zoomTimer.Tick += WatchZoom;
                zoomTimer.Start();

                Timer probe = new Timer();
                probe.Interval = 6000;
                probe.Tick += delegate(object s2, EventArgs a2)
                {
                    probe.Stop();
                    probe.Dispose();
                    ProbeOverlay();
                };
                probe.Start();

                StartWidgets();

                Timer pinTimer = new Timer();
                pinTimer.Interval = 1500;
                pinTimer.Tick += delegate(object ps, EventArgs pa) { PollPinQueue(); };
                pinTimer.Start();
            }
            catch (Exception ex)
            {
                Log("webview2 error: " + ex);
                MessageBox.Show("WebView2 could not start:\r\n\r\n" + ex.Message, "iCloud panel",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // Runs at document creation on every page: hides the page scrollbar and
        // iCloud's top bar, then adds the lock and settings buttons.
        private string BuildInjectedScript()
        {
            string css =
                "::-webkit-scrollbar{display:none !important;width:0 !important;height:0 !important}" +
                "html{scrollbar-width:none !important;-ms-overflow-style:none !important}" +
                "header,[role='banner']{display:none !important}" +
                // Locked panel: no page scrolling and no overscroll bounce.
                "html[data-ic-locked='1'],html[data-ic-locked='1'] body{overflow:hidden !important;" +
                "overscroll-behavior:none !important}" +
                // This chrome is UI, not page content: scale it by 1/zoom so the
                // buttons keep their physical size when the page is zoomed out.
                "#ic-corner{position:fixed;right:calc(10px * var(--ic-s,1));bottom:calc(10px * var(--ic-s,1));" +
                "display:flex;gap:8px;align-items:center;transform-origin:100% 100%;" +
                "z-index:2147483647;opacity:0;pointer-events:none;transform:scale(calc(0.9 * var(--ic-s,1)));" +
                "transition:opacity 0.16s ease,transform 0.16s ease}" +
                "#ic-corner.ic-on{opacity:1;pointer-events:auto;transform:scale(var(--ic-s,1))}" +
                ".ic-btn{width:30px;height:30px;border-radius:50%;display:flex;align-items:center;" +
                "justify-content:center;cursor:pointer;background:rgba(28,28,30,0.82);" +
                "border:1px solid rgba(255,255,255,0.18);color:#f2f2f7;" +
                "box-shadow:0 2px 8px rgba(0,0,0,0.35);transition:background 0.14s ease,color 0.14s ease}" +
                ".ic-btn:hover{background:rgba(58,58,62,0.95)}" +
                ".ic-btn svg{width:16px;height:16px;display:block}" +
                "html[data-ic-locked='0'] #ic-lock{border-color:#0a84ff;color:#0a84ff}" +
                "html[data-ic-topmost='1'] #ic-gear,html[data-ic-pinned='1'] #ic-gear," +
                "html[data-ic-autohide='1'] #ic-gear{border-color:#0a84ff;color:#0a84ff}" +
                "#ic-settings{position:fixed;right:calc(10px * var(--ic-s,1));" +
                "bottom:calc(10px + 40px * var(--ic-s,1));width:196px;box-sizing:border-box;" +
                "transform-origin:100% 100%;transform:scale(var(--ic-s,1));" +
                "visibility:hidden;opacity:0;pointer-events:none;transition:opacity 0.14s ease;" +
                "background:rgba(32,32,34,0.97);border:1px solid rgba(255,255,255,0.14);border-radius:12px;" +
                "padding:6px;z-index:2147483647;box-shadow:0 10px 30px rgba(0,0,0,0.45);" +
                "font:12px 'Segoe UI',system-ui,sans-serif;color:#f2f2f7}" +
                "#ic-settings.ic-open{visibility:visible;opacity:1;pointer-events:auto}" +
                ".ic-row{display:flex;align-items:center;gap:9px;padding:7px 9px;border-radius:8px;" +
                "cursor:pointer;white-space:nowrap}" +
                ".ic-row:hover{background:rgba(255,255,255,0.1)}" +
                ".ic-dot{width:14px;height:14px;border-radius:4px;box-sizing:border-box;" +
                "border:1px solid rgba(255,255,255,0.35);display:flex;align-items:center;justify-content:center}" +
                ".ic-dot::after{content:'';width:6px;height:6px;border-radius:2px;background:transparent}" +
                "html[data-ic-topmost='1'] .ic-row[data-key='topmost'] .ic-dot," +
                "html[data-ic-pinned='1'] .ic-row[data-key='pinned'] .ic-dot," +
                "html[data-ic-autohide='1'] .ic-row[data-key='autohide'] .ic-dot{background:#0a84ff;border-color:#0a84ff}" +
                "html[data-ic-topmost='1'] .ic-row[data-key='topmost'] .ic-dot::after," +
                "html[data-ic-pinned='1'] .ic-row[data-key='pinned'] .ic-dot::after," +
                "html[data-ic-autohide='1'] .ic-row[data-key='autohide'] .ic-dot::after{background:#ffffff}" +
                // Background sub-view: swatches, pickers, transparency, reset.
                "#ic-settings .ic-bg{display:none}" +
                "#ic-settings.ic-bgview .ic-bg{display:block}" +
                "#ic-settings.ic-bgview .ic-row[data-key='topmost']," +
                "#ic-settings.ic-bgview .ic-row[data-key='pinned']," +
                "#ic-settings.ic-bgview .ic-row[data-key='autohide']{display:none}" +
                ".ic-swatches{display:flex;gap:6px;padding:6px 9px 8px}" +
                ".ic-sw{width:20px;height:20px;border-radius:5px;cursor:pointer;display:block;" +
                "border:1px solid rgba(255,255,255,0.35)}" +
                ".ic-sw:hover{border-color:#0a84ff}" +
                // Account strip toggle: the page-side script tags iCloud's bottom
                // row as #ic-info-strip, this hides it and lights the button.
                "html[data-ic-info='0'] #ic-info-strip{display:none !important}" +
                "html[data-ic-info='0'] #ic-info{border-color:#0a84ff;color:#0a84ff}" +
                // The fly-out is a menu, so it follows the Windows app theme the
                // same way WebView2's own menus do (see ApplySystemColorScheme).
                "@media (prefers-color-scheme: light){" +
                "#ic-settings{background:rgba(249,249,249,0.97);border-color:rgba(0,0,0,0.12);" +
                "color:#1c1c1e;box-shadow:0 10px 30px rgba(0,0,0,0.18)}" +
                "#ic-settings .ic-row:hover{background:rgba(0,0,0,0.06)}" +
                ".ic-dot{border-color:rgba(0,0,0,0.3)}}";

            string lockSvg =
                "<svg viewBox='0 0 24 24' fill='none' stroke='currentColor' stroke-width='2.1' " +
                "stroke-linecap='round' stroke-linejoin='round'>" +
                "<rect x='4' y='10.5' width='16' height='10' rx='2.5'></rect>" +
                "<path d='M8 10.5V7a4 4 0 0 1 8 0v3.5'></path></svg>";

            string gearSvg =
                "<svg viewBox='0 0 24 24' fill='none' stroke='currentColor' stroke-width='1.9' " +
                "stroke-linecap='round' stroke-linejoin='round'>" +
                "<circle cx='12' cy='12' r='3.1'></circle>" +
                "<path d='M19.4 15a1.7 1.7 0 0 0 .33 1.85l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.7 1.7 0 0 0-1.85-.33" +
                " 1.7 1.7 0 0 0-1.03 1.56V21a2 2 0 1 1-4 0v-.09A1.7 1.7 0 0 0 8.99 19.4a1.7 1.7 0 0 0-1.85.33l-.06.06" +
                "a2 2 0 1 1-2.83-2.83l.06-.06a1.7 1.7 0 0 0 .33-1.85 1.7 1.7 0 0 0-1.56-1.03H3a2 2 0 1 1 0-4h.09" +
                "A1.7 1.7 0 0 0 4.6 9.01a1.7 1.7 0 0 0-.33-1.85l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06" +
                "A1.7 1.7 0 0 0 8.99 4.6a1.7 1.7 0 0 0 1.03-1.56V3a2 2 0 1 1 4 0v.09a1.7 1.7 0 0 0 1.03 1.56" +
                " 1.7 1.7 0 0 0 1.85-.33l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06a1.7 1.7 0 0 0-.33 1.85v.09" +
                "A1.7 1.7 0 0 0 20.91 12H21a2 2 0 1 1 0 4h-.09a1.7 1.7 0 0 0-1.51 1z'></path></svg>";

            string infoSvg =
                "<svg viewBox='0 0 24 24' fill='none' stroke='currentColor' stroke-width='1.9' " +
                "stroke-linecap='round' stroke-linejoin='round'>" +
                "<path d='M2.5 12s3.6-6.5 9.5-6.5S21.5 12 21.5 12s-3.6 6.5-9.5 6.5S2.5 12 2.5 12z'></path>" +
                "<circle cx='12' cy='12' r='2.9'></circle></svg>";

            return
                "(function(){" +
                "var css=" + JsString(css) + ";" +
                "var lockSvg=" + JsString(lockSvg) + ";" +
                "var gearSvg=" + JsString(gearSvg) + ";" +
                "var infoSvg=" + JsString(infoSvg) + ";" +
                "var bgCfg=JSON.parse(" + JsString(BgJson()) + ");" +
                // --ic-s is 1/pageZoom, pushed by the host, so the corner and
                // fly-out stay the same physical size at every zoom level.
                "var icS=1;" +
                "window.icSetChromeScale=function(s){s=parseFloat(s);if(!(s>0)){s=1;}" +
                "icS=s;document.documentElement.style.setProperty('--ic-s',String(s));};" +
                "function icRgba(h,a){h=String(h||'#7a1f4a').replace('#','');" +
                "if(h.length===3){h=h[0]+h[0]+h[1]+h[1]+h[2]+h[2];}" +
                "var r=parseInt(h.substr(0,2),16)||0,g=parseInt(h.substr(2,2),16)||0,b=parseInt(h.substr(4,2),16)||0;" +
                "return 'rgba('+r+','+g+','+b+','+a+')';}" +
                "function icSet(el,n,v){if(!el.__icb){el.__icb={};}" +
                "if(!(n in el.__icb)){el.__icb[n]={v:el.style.getPropertyValue(n),p:el.style.getPropertyPriority(n)};}" +
                "el.style.setProperty(n,v,'important');}" +
                "function icClr(el){if(!el.__icb){return;}" +
                "for(var n in el.__icb){var o=el.__icb[n];" +
                "if(o.v){el.style.setProperty(n,o.v,o.p||'');}else{el.style.removeProperty(n);}}}" +
                "function icTargets(){return document.querySelectorAll('[class*=\"gradient-background\"],[class*=\"homepage-background\"],[class*=\"wallpaper\"]');}" +
                "function applyBg(c){c=c||{};bgCfg=c;try{" +
                "var off=(typeof c.offset==='number')?c.offset:(parseInt(c.bgoffset)||0);" +
                "var oel=document.getElementById('ic-off');if(oel){oel.textContent='Offset: '+off+' px';}" +
                "var kind=c.kind||c.bgkind||'default';" +
                "var col=c.color||c.bgcolor||'#7a1f4a';" +
                "var img=c.img||c.bgimageData||'';" +
                "if(kind==='wallpaper'){img=c.wall||'';kind='image';}" +
                "var a=(typeof c.opacity==='number')?c.opacity:(parseFloat(c.bgopacity)||1);" +
                "var st=document.getElementById('ic-bgstyle');" +
                "if(!st){st=document.createElement('style');st.id='ic-bgstyle';" +
                "(document.head||document.documentElement).appendChild(st);}" +
                "var els=icTargets();" +
                "if(kind==='default'){st.textContent='';" +
                "for(var d1=0;d1<els.length;d1++){icClr(els[d1]);}" +
                "if(document.body){icClr(document.body);}if(document.documentElement){icClr(document.documentElement);}return;}" +
                "var css='html{background-color:#1c1c1e !important;';" +
                "var no=icRgba(kind==='image'?col:col,a);" +
                "if(kind==='image'&&img){var dv=icRgba('#1c1c1e',1-a);" +
                "css+='background-image:linear-gradient('+dv+','+dv+'),url(\"'+img+'\") !important;background-size:cover !important;background-position:center !important;';}" +
                "else{css+='background-image:linear-gradient('+no+','+no+') !important;';}" +
                "css+='}';" +
                "css+='html body [class*=\"gradient-background\"]::before,html body [class*=\"gradient-background\"]::after{content:none !important;background:none !important;}';" +
                "css+='html body [class*=\"gradient-background\"] img,html body [class*=\"gradient-background\"] canvas{display:none !important;}';" +
                "st.textContent=css;" +
                "var html=document.documentElement;" +
                "if(html){icSet(html,'background-color','#1c1c1e');" +
                "if(kind==='image'&&img){var dv2=icRgba('#1c1c1e',1-a);" +
                "icSet(html,'background-image','linear-gradient('+dv2+','+dv2+'),url(\"'+img+'\")');" +
                "icSet(html,'background-size','cover');icSet(html,'background-position','center');}" +
                "else{icSet(html,'background-image','linear-gradient('+no+','+no+')');}}" +
                "if(document.body){icSet(document.body,'background-color','transparent');}" +
                "for(var d2=0;d2<els.length;d2++){icSet(els[d2],'background-image','none');" +
                "icSet(els[d2],'background-color','transparent');icSet(els[d2],'opacity','1');}" +
                "}catch(e){try{window.chrome.webview.postMessage('bg-error:'+e.message);}catch(x){}}}" +
                "window.icApplyBg=applyBg;" +
                "var rows=\"" +
                "<div class='ic-row' data-key='topmost'><span class='ic-dot'></span>Always on top</div>" +
                "<div class='ic-row' data-key='pinned'><span class='ic-dot'></span>Pin to desktop</div>" +
                "<div class='ic-row' data-key='autohide'><span class='ic-dot'></span>Hide when not focused</div>" +
                "<div class='ic-row' data-key='dash-arrange'><span class='ic-dot'></span>Arrange dashboard</div>" +
                "<div class='ic-row' data-key='dash-reset'><span class='ic-dot'></span>Reset dashboard layout</div>" +
                "<div class='ic-row' data-key='dash-locate'><span class='ic-dot'></span>Weather location...</div>" +
                "<div class='ic-row' data-key='weather-windows'><span class='ic-dot'></span>Use Windows location</div>" +
                "<div class='ic-row' data-key='name-set'><span class='ic-dot'></span>Your name...</div>" +
                "<div class='ic-row' data-key='birthday-set'><span class='ic-dot'></span>Birthdays...</div>" +
                "<div class='ic-row' data-key='bg'><span class='ic-dot'></span>Background...</div>" +
                "<div class='ic-bg'>" +
                "<div class='ic-swatches'>" +
                "<span class='ic-sw' data-c='#0b2a52' style='background:#0b2a52'></span>" +
                "<span class='ic-sw' data-c='#101114' style='background:#101114'></span>" +
                "<span class='ic-sw' data-c='#22303f' style='background:#22303f'></span>" +
                "<span class='ic-sw' data-c='#3d2b4f' style='background:#3d2b4f'></span>" +
                "<span class='ic-sw' data-c='#12241c' style='background:#12241c'></span>" +
                "</div>" +
                "<div class='ic-row' data-key='bg-pick'>Colour...</div>" +
                "<div class='ic-row' data-key='bg-image'>Image...</div>" +
                "<div class='ic-row' data-key='bg-wall'>Desktop wallpaper</div>" +
                "<div class='ic-row' data-key='bg-up'>Shift up</div>" +
                "<div class='ic-row' data-key='bg-down'>Shift down</div>" +
                "<div class='ic-row' data-key='bg-off' id='ic-off'>Offset: 0 px</div>" +
                "<div class='ic-row' data-key='bg-op'>Transparency: 100%</div>" +
                "<div class='ic-row' data-key='bg-default'>Default</div>" +
                "</div>\";" +
                "function start(){" +
                "  if(document.getElementById('ic-corner')){return;}" +
                "  var st=document.createElement('style');st.textContent=css;" +
                "  (document.head||document.documentElement).appendChild(st);" +
                "  var root=(document.body||document.documentElement);" +
                "  var wrap=document.createElement('div');wrap.id='ic-corner';" +
                "  var gear=document.createElement('div');gear.id='ic-gear';gear.className='ic-btn';" +
                "    gear.title='Panel settings';gear.innerHTML=gearSvg;" +
                "  var lock=document.createElement('div');lock.id='ic-lock';lock.className='ic-btn';" +
                "    lock.title='Unlock to move or resize the panel';lock.innerHTML=lockSvg;" +
                "  var info=document.createElement('div');info.id='ic-info';info.className='ic-btn';" +
                "    info.title='Show or hide account details';info.innerHTML=infoSvg;" +
                "  wrap.appendChild(gear);wrap.appendChild(info);wrap.appendChild(lock);" +
                "  var pop=document.createElement('div');pop.id='ic-settings';pop.innerHTML=rows;" +
                "  root.appendChild(wrap);root.appendChild(pop);" +
                "  applyBg(bgCfg);" +
                "  setTimeout(function(){applyBg(bgCfg);},1200);" +
                "  setTimeout(function(){applyBg(bgCfg);},3500);" +
                "  setInterval(function(){var kib=(bgCfg.kind||bgCfg.bgkind||'default');" +
                "if(kib!=='default'){applyBg(bgCfg);}},5000);" +
                "  function post(m){try{window.chrome.webview.postMessage(m);}catch(e){}}" +
                "  lock.addEventListener('click',function(e){e.preventDefault();e.stopPropagation();post('toggle-lock');});" +
                "  info.addEventListener('click',function(e){e.preventDefault();e.stopPropagation();post('toggle-info');});" +
                "  gear.addEventListener('click',function(e){e.preventDefault();e.stopPropagation();" +
                "    pop.classList.toggle('ic-open');});" +
                "  var bgOps=[1,0.85,0.7,0.55],bgOpIdx=0;" +
                "  pop.addEventListener('click',function(e){" +
                "    var row=e.target.closest?e.target.closest('.ic-row'):null;if(!row){return;}" +
                "    e.preventDefault();e.stopPropagation();" +
                "    var k=row.getAttribute('data-key');" +
                "    if(k==='topmost'||k==='pinned'||k==='autohide'){post('toggle-'+k);return;}" +
                "    if(k==='dash-arrange'){if(window.icDash){window.icDash.arrange();}return;}" +
                "    if(k==='dash-reset'){if(window.icDash){window.icDash.resetLayout();}return;}" +
                "    if(k==='dash-locate'){post('weather-locate');return;}" +
                "    if(k==='weather-windows'){post('weather-windows');return;}" +
                "    if(k==='name-set'){post('name-set');return;}" +
                "    if(k==='birthday-set'){post('birthday-set');return;}" +
                "    if(k==='bg'){pop.classList.toggle('ic-bgview');return;}" +
                "    if(k==='bg-pick'){post('bg-pick-color');return;}" +
                "    if(k==='bg-image'){post('bg-pick-image');return;}" +
                "    if(k==='bg-wall'){post('bg-wallpaper');return;}" +
                "    if(k==='bg-up'){post('bg-nudge-up');return;}" +
                "    if(k==='bg-down'){post('bg-nudge-down');return;}" +
                "    if(k==='bg-off'){post('bg-offset-reset');return;}" +
                "    if(k==='bg-default'){post('bg-default');return;}" +
                "    if(k==='bg-op'){bgOpIdx=(bgOpIdx+1)%bgOps.length;" +
                "      row.textContent='Transparency: '+Math.round(bgOps[bgOpIdx]*100)+'%';" +
                "      post('bg-opacity:'+bgOps[bgOpIdx]);return;}});" +
                "  pop.addEventListener('click',function(e){" +
                "    var sw=e.target.closest?e.target.closest('.ic-sw'):null;if(!sw){return;}" +
                "    e.preventDefault();e.stopPropagation();" +
                "    post('bg-color:'+sw.getAttribute('data-c'));});" +
                "  document.addEventListener('click',function(e){" +
                "    if(pop.classList.contains('ic-open')&&!pop.contains(e.target)&&!gear.contains(e.target)){" +
                "      pop.classList.remove('ic-open');}},true);" +
                "  document.addEventListener('mousemove',function(e){" +
                "    var zone=80*icS;" +
                "    var near=(e.clientX>window.innerWidth-zone)&&(e.clientY>window.innerHeight-zone);" +
                "    var open=pop.classList.contains('ic-open');" +
                "    if(near||open||wrap.matches(':hover')){wrap.classList.add('ic-on');}" +
                "    else{wrap.classList.remove('ic-on');}},true);" +
                "  document.addEventListener('keydown',function(e){" +
                "    if(e.key!=='Escape'){return;}" +
                "    if(document.documentElement.hasAttribute('data-ic-edit')){" +
                "      if(window.icDash){window.icDash.arrange();}return;}" +
                "    if(pop.classList.contains('ic-open')){pop.classList.remove('ic-open');return;}" +
                "    if(document.documentElement.getAttribute('data-ic-locked')==='0'){post('lock-panel');}},true);" +
                // iCloud's account strip is the footer of the home page: the row of
                // "Your iCloud / Your Storage / Your Devices" cards. The words confirm
                // it is the right footer; Apple's class names change between builds,
                // the wording does not.
                "  function stripByText(){" +
                "    var labs=['your icloud','your storage','your devices'];" +
                "    var els=document.querySelectorAll('footer'),raw=[],i,j;" +
                "    for(i=0;i<els.length;i++){" +
                "      var t=(els[i].innerText||'').replace(/\\s+/g,' ').trim().toLowerCase();" +
                "      if(!t){continue;}" +
                "      for(j=0;j<labs.length;j++){if(t.indexOf(labs[j])>=0){raw.push(els[i]);break;}}}" +
                // A card and its heading both start with the same words, so keep only
                // the innermost node of each; the row is then the first ancestor that
                // holds all of them. Without this the tag landed on a card's wrapper
                // and hid one figure instead of the strip.
                "    var hits=[];" +
                "    for(i=0;i<raw.length;i++){var inner=true;" +
                "      for(j=0;j<raw.length;j++){if(raw[j]!==raw[i]&&raw[i].contains(raw[j])){inner=false;break;}}" +
                "      if(inner){hits.push(raw[i]);}}" +
                "    if(hits.length<1){return null;}" +
                "    var el=hits[0];" +
                "    for(var n=0;n<7&&el&&el!==document.body&&el!==document.documentElement;n++){" +
                "      var c=0;for(j=0;j<hits.length;j++){if(el.contains(hits[j])){c++;}}" +
                "      if(c>=hits.length){return el;}" +
                "      el=el.parentElement;}" +
                "    return null;}" +
                "  function tagStrip(){" +
                "    try{var s=document.getElementById('ic-info-strip');" +
                "      if(s&&document.contains(s)){return;}" +
                "      var el=stripByText();if(!el){return;}" +
                // Never let it swallow the dashboard or the app tile grid.
                "      if(el.querySelector('#ic-dash')){return;}" +
                "      el.id='ic-info-strip';}catch(x){}}" +
                "  tagStrip();setInterval(tagStrip,2500);" +
                "  function scrollLocked(){return document.documentElement.getAttribute('data-ic-locked')==='1';}" +
                "  document.addEventListener('wheel',function(e){" +
                "    if(e.ctrlKey||!scrollLocked()){return;}" +
                "    e.preventDefault();e.stopPropagation();},{passive:false,capture:true});" +
                "  document.addEventListener('touchmove',function(e){" +
                "    if(scrollLocked()){e.preventDefault();}},{passive:false,capture:true});" +
                "  document.addEventListener('keydown',function(e){" +
                "    if(!scrollLocked()){return;}" +
                "    var t=e.target;var tag=(t&&t.tagName)?t.tagName.toLowerCase():'';" +
                "    if(tag==='input'||tag==='textarea'||tag==='select'||(t&&t.isContentEditable)){return;}" +
                "    var keys=['PageDown','PageUp','Home','End'];" +
                "    if(keys.indexOf(e.key)>=0){e.preventDefault();return;}" +
                "    if((tag==='body'||tag==='html')&&(e.key==='ArrowDown'||e.key==='ArrowUp'||e.key===' ')){" +
                "      e.preventDefault();}" +
                "  },true);" +
                "}" +
                "if(document.readyState==='loading'){document.addEventListener('DOMContentLoaded',start);}" +
                "else{start();}" +
                "document.addEventListener('DOMContentLoaded',start);" +
                "})();" +
                "\n" +
                BuildDashScript(DashJson());
        }

        // ------------------------------------------------- windows prefs ---
        // The tiles follow Windows rather than Chromium's idea of a locale: the
        // measurement system decides °C/°F, LOCALE_ITIME decides 12 or 24 hour,
        // and the culture name drives date order and month names.
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetLocaleInfoEx(string localeName, uint lcType, StringBuilder data, int cch);

        private const uint LOCALE_IMEASURE = 0x0000000D;
        private const uint LOCALE_ITIME = 0x00000023;

        private static bool prefsDone;
        private static string prefsLocale = "en-US";
        private static bool prefsFahrenheit;
        private static bool prefsHour12 = true;
        private static string userName = "";
        private static bool nameLoaded;

        private static void LoadPrefs()
        {
            if (prefsDone) { return; }
            prefsDone = true;
            try { prefsLocale = CultureInfo.CurrentCulture.Name; } catch { }
            if (string.IsNullOrEmpty(prefsLocale)) { prefsLocale = "en-US"; }
            try { prefsHour12 = !CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern.Contains("H"); } catch { }
            try { prefsFahrenheit = !RegionInfo.CurrentRegion.IsMetric; } catch { }
            try
            {
                StringBuilder sb = new StringBuilder(16);
                if (GetLocaleInfoEx(prefsLocale, LOCALE_IMEASURE, sb, sb.Capacity) > 0)
                {
                    prefsFahrenheit = sb.ToString().Trim() == "1";
                }
                sb.Clear();
                if (GetLocaleInfoEx(prefsLocale, LOCALE_ITIME, sb, sb.Capacity) > 0)
                {
                    prefsHour12 = sb.ToString().Trim() == "0";
                }
            }
            catch (Exception ex) { Log("windows prefs: " + ex.Message); }
            if (!nameLoaded)
            {
                nameLoaded = true;
                try
                {
                    string cfg = Path.Combine(Path.GetFullPath(Path.Combine(ResolveAppDir(), "..")), "config.json");
                    if (File.Exists(cfg))
                    {
                        string text = File.ReadAllText(cfg);
                        Match nm = Regex.Match(text, "\"name\"\\s*:\\s*\"([^\"]*)\"");
                        if (nm.Success) { userName = nm.Groups[1].Value; }
                        string bd = ExtractArray(text, "birthdays");
                        if (bd.Length > 1) { birthdaysJson = bd; }
                        string ba = ExtractArray(text, "birthdayAccounts");
                        if (ba.Length > 1) { birthdayAccountsJson = ba; }
                    }
                }
                catch (Exception ex) { Log("name load: " + ex.Message); }
            }
            Log("windows prefs: " + prefsLocale + " " + (prefsFahrenheit ? "F" : "C") +
                " " + (prefsHour12 ? "12h" : "24h"));
        }

        // Birthday settings: manual name/month/day entries and which mail
        // accounts may supply birthdays from today's calendar. Stored as JSON
        // fragments so the page can read them from the prefs push untouched.
        private static string birthdaysJson = "[]";
        private static string birthdayAccountsJson = "[]";

        private static string PrefsJson()
        {
            LoadPrefs();
            return "{\"unit\":\"" + (prefsFahrenheit ? "F" : "C") +
                "\",\"hour12\":" + (prefsHour12 ? "true" : "false") +
                ",\"locale\":" + JsString(prefsLocale) +
                ",\"name\":" + JsString(userName) +
                ",\"birthdays\":" + birthdaysJson +
                ",\"birthdayAccounts\":" + birthdayAccountsJson + "}";
        }

        // The dashboard that sits under iCloud's tiles. The page owns the layout
        // (order, hidden, spans) and posts it back; the host only stores it and
        // feeds widget data in. Written without a single double quote so this
        // stays a readable C# verbatim literal.
        private static string BuildDashScript(string state)
        {
            return @"(function(){
if(window.icDash&&window.icDash.ready){return;}
var STATE=" + state + @";
var PREFS=" + PrefsJson() + @";
var IDS=['clock','shortcuts','weather','calendar','mail','notes','reminders'];

var TITLES={clock:'Clock',shortcuts:'Apps',weather:'Weather',calendar:'Calendar',mail:'Inbox',notes:'Notes',reminders:'Reminders'};
var ICONS={clock:'◷',shortcuts:'▦',weather:'☀',calendar:'▤',mail:'✉'};
var LINKS=[['Photos','photos'],['Drive','drive'],['Notes','notes'],['Calendar','calendar'],['Mail','mail'],['Reminders','reminders']];
var CODES={0:'Clear',1:'Mainly clear',2:'Partly cloudy',3:'Overcast',45:'Fog',48:'Rime fog',51:'Light drizzle',53:'Drizzle',55:'Heavy drizzle',56:'Freezing drizzle',57:'Freezing drizzle',61:'Light rain',63:'Rain',65:'Heavy rain',66:'Freezing rain',67:'Freezing rain',71:'Light snow',73:'Snow',75:'Heavy snow',77:'Snow grains',80:'Rain showers',81:'Rain showers',82:'Violent showers',85:'Snow showers',86:'Snow showers',95:'Thunderstorm',96:'Thunderstorm',99:'Thunderstorm'};
var data={weather:null,outlook:null};
var state=norm(STATE);
var prefs=(PREFS&&typeof PREFS==='object')?PREFS:{};
var edit=false,box=null,mountTimer=0,booted=false,diagged=false;
var CSS=[
'.profile-card{display:none!important}',
'#ic-dash .ic-card[data-native]{background:transparent!important;border:0!important;box-shadow:none!important;backdrop-filter:none!important;padding:0!important;pointer-events:none}',
'#ic-dash .ic-card[data-native]:hover{transform:none;box-shadow:none}',
'#ic-dash .ic-card[data-native] .ic-grab{opacity:.6;pointer-events:auto}',
'#ic-dash .ic-card[data-native] .ic-grab{right:40px}',
'#ic-dash .ic-notes{height:100%;overflow:auto}',
'#ic-dash .ic-notes .notes-home-tile{width:100%!important;height:100%!important;min-width:0!important;max-width:none!important}',
'#ic-dash{position:fixed;left:0;top:0;right:0;bottom:0;pointer-events:none;z-index:2147483640;font-family:Segoe UI,system-ui,sans-serif;color:#f2f2f7;font-size:12px;line-height:1.35;text-align:left}',
'#ic-dash *{box-sizing:border-box}',
'#ic-dash .ic-card:hover{transform:scale(1.03);box-shadow:0 16px 40px rgba(0,0,0,.38);z-index:3}',
'#ic-dash .ic-card{position:absolute;pointer-events:auto;background:rgba(28,28,30,.62);border:1px solid rgba(255,255,255,.14);border-radius:18px;padding:12px 14px;display:flex;flex-direction:column;min-width:0;overflow:hidden;box-shadow:0 10px 30px rgba(0,0,0,.28);backdrop-filter:blur(24px) saturate(1.5);-webkit-backdrop-filter:blur(24px) saturate(1.5);transition:transform .18s ease,box-shadow .18s ease;transform-origin:50% 50%;will-change:transform}',
'#ic-dash .ic-card[data-drag]{transform:none;z-index:9;opacity:.96}',
'#ic-dash .ic-grab{position:absolute;right:8px;bottom:8px;width:24px;height:24px;border-radius:50%;background:rgba(255,255,255,.18);color:#fff;font-size:13px;line-height:24px;text-align:center;opacity:0;pointer-events:none;cursor:grab;touch-action:none;transition:opacity .15s ease,background .15s ease}',
'html[data-ic-edit] #ic-dash .ic-tools{display:flex}',
'#ic-dash .ic-card:hover .ic-grab{opacity:1;pointer-events:auto}',
'html[data-ic-edit] #ic-dash .ic-card[data-off]{opacity:.45;border-style:dashed}',
'#ic-dash .ic-head{display:flex;align-items:center;justify-content:space-between;gap:6px;margin-bottom:6px;cursor:move}',
'#ic-dash .ic-h1{font-size:13px;font-weight:600;opacity:.92;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}',
'#ic-dash .ic-tools{display:none;gap:4px;flex:0 0 auto}',
'#ic-dash .ic-tool{width:20px;height:20px;border-radius:6px;display:flex;align-items:center;justify-content:center;background:rgba(255,255,255,.14);cursor:pointer;font-size:11px;line-height:1}',
'#ic-dash .ic-tool:hover{background:#0a84ff}',
'#ic-dash .ic-body{flex:1 1 auto;min-height:0;overflow:hidden}',
'#ic-dash .ic-big{font-size:30px;font-weight:600;line-height:1.05;letter-spacing:-1px;white-space:nowrap}',
'#ic-dash .ic-sub{font-size:12px;opacity:.9}',
'#ic-dash .ic-dim{font-size:11px;opacity:.62}',
'#ic-dash .ic-dim2{font-size:10px;opacity:.55;flex:0 0 auto}',
'#ic-dash .ic-num{font-size:13px;font-weight:600}',
'#ic-dash .ic-roww{display:flex;align-items:center;gap:10px}',
'#ic-dash .ic-col{display:flex;flex-direction:column;min-width:0}',
'#ic-dash .ic-links{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:6px}',
'#ic-dash .ic-link{display:block;padding:7px 0;text-align:center;border-radius:9px;background:rgba(255,255,255,.1);color:#f2f2f7;text-decoration:none;font-size:11px}',
'#ic-dash .ic-link:hover{background:rgba(10,132,255,.75)}',
'#ic-dash .ic-days{display:flex;gap:8px;margin-top:6px}',
'#ic-dash .ic-day{flex:1 1 0;text-align:center}',
'#ic-dash .ic-item{display:flex;align-items:center;gap:6px;padding:3px 0;border-top:1px solid rgba(255,255,255,.07)}',
'#ic-dash .ic-item[data-new] .ic-subject{font-weight:600}',
'#ic-dash .ic-acct{width:7px;height:7px;border-radius:50%;flex:0 0 auto}',
'#ic-dash .ic-from{font-size:11px;flex:0 0 34%;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;opacity:.85}',
'#ic-dash .ic-subject{font-size:11px;flex:1 1 auto;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}',
'#ic-dash .ic-when{font-size:10px;opacity:.7;flex:0 0 auto}',
'#ic-dash .ic-title2{font-size:11px;flex:1 1 auto;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}',
'#ic-dash .ic-grab:hover{background:rgba(10,132,255,.85)}',
'#ic-dash .ic-occ{font-size:11px;margin-top:6px;color:#ffd60a;opacity:.95}',
'#ic-dash .ic-hint{position:fixed;left:50%;top:12px;transform:translateX(-50%);background:rgba(16,16,18,.92);color:#f2f2f7;padding:6px 12px;border-radius:10px;font-size:11px;pointer-events:none}',
'@media (prefers-color-scheme: light){#ic-dash{color:#1c1c1e}#ic-dash .ic-card{background:rgba(255,255,255,.72);border-color:rgba(0,0,0,.08);box-shadow:0 10px 30px rgba(0,0,0,.14)}#ic-dash .ic-card:hover{box-shadow:0 16px 40px rgba(0,0,0,.2)}#ic-dash .ic-link{background:rgba(0,0,0,.06);color:#1c1c1e}#ic-dash .ic-tool{background:rgba(0,0,0,.08);color:#1c1c1e}#ic-dash .ic-grab{background:rgba(0,0,0,.12);color:#1c1c1e}#ic-dash .ic-occ{color:#9a6b00}}',
'html[data-ic-edit] #ic-dash .ic-card{outline:1px dashed rgba(255,255,255,.22);outline-offset:3px}',
'html[data-ic-edit] #ic-dash .ic-card[data-off]{opacity:.45;border-style:dashed}',
'#ic-dash .ic-grab:active{cursor:grabbing}',
'#ic-dash .ic-day[data-extra]{display:none}',
'#ic-dash .ic-card[data-shape=wide] .ic-day[data-extra]{display:block}',
'#ic-dash .ic-card[data-shape=wide] .ic-days{gap:12px;margin-top:0}',
'#ic-dash .ic-card[data-shape=wide][data-id=weather] .ic-body{display:flex;align-items:center;gap:18px}',
'#ic-dash .ic-card[data-shape=wide][data-id=weather] .ic-roww{flex:0 0 auto}',
'#ic-dash .ic-card[data-shape=wide][data-id=weather] .ic-days{flex:1 1 auto}',
'#ic-dash .ic-card[data-shape=wide][data-id=mail] .ic-body,#ic-dash .ic-card[data-shape=wide][data-id=calendar] .ic-body{column-count:2;column-gap:18px}',
'#ic-dash .ic-card[data-shape=wide] .ic-item{break-inside:avoid}',
'#ic-dash .ic-card[data-shape=tall] .ic-body{overflow:auto}',
'#ic-dash .ic-card[data-shape=wide] .ic-head{margin-bottom:4px}',

'#ic-dash .ic-card{background:linear-gradient(155deg,rgba(255,255,255,.16),rgba(255,255,255,.05) 46%,rgba(0,0,0,.12)),rgba(26,26,32,.58);border-color:rgba(255,255,255,.18)}',
'#ic-dash .ic-card{background:radial-gradient(120% 90% at 15% 0%,var(--accent,rgba(255,255,255,.16)),transparent 62%),linear-gradient(155deg,rgba(255,255,255,.16),rgba(255,255,255,.05) 46%,rgba(0,0,0,.12)),rgba(26,26,32,.58)}',
'#ic-dash .ic-card[data-id=clock]{--accent:rgba(92,112,255,.55)}',
'#ic-dash .ic-card[data-id=shortcuts]{--accent:rgba(255,120,90,.52)}',
'#ic-dash .ic-card[data-id=weather]{--accent:rgba(64,180,255,.52)}',
'#ic-dash .ic-card[data-id=calendar]{--accent:rgba(255,80,110,.46)}',
'#ic-dash .ic-card[data-id=mail]{--accent:rgba(80,140,255,.48)}',
'#ic-dash .ic-head{position:relative}',
'#ic-dash .ic-hleft{display:flex;align-items:center;gap:7px;min-width:0}',
'#ic-dash .ic-hright{display:flex;align-items:center;gap:4px;margin-left:auto}',
'#ic-dash .ic-ico{width:19px;height:19px;border-radius:6px;display:flex;align-items:center;justify-content:center;background:rgba(255,255,255,.16);font-size:11px;line-height:1;flex:0 0 auto}',
'#ic-dash .ic-more{width:23px;height:23px;border-radius:50%;display:flex;align-items:center;justify-content:center;background:rgba(255,255,255,.16);cursor:pointer;font-size:15px;line-height:1;color:#fff}',
'#ic-dash .ic-more:hover{background:#0a84ff}',
'#ic-dash .ic-apps{display:grid;grid-template-columns:repeat(auto-fill,minmax(52px,1fr));gap:9px 6px;align-content:start;padding:2px 0}',
'#ic-dash .ic-app{border:0;background:none;color:inherit;text-decoration:none;display:flex;flex-direction:column;align-items:center;gap:4px;cursor:pointer;min-width:0;padding:2px;font:inherit}',
'#ic-dash .ic-app-ico{width:42px;height:42px;border-radius:11px;display:flex;align-items:center;justify-content:center;font-size:21px;overflow:hidden;background:rgba(255,255,255,.16);box-shadow:0 3px 9px rgba(0,0,0,.2);transition:transform .14s ease,box-shadow .14s ease}',
'#ic-dash .ic-app:hover .ic-app-ico{transform:scale(1.06);box-shadow:0 6px 16px rgba(0,0,0,.32)}',
'#ic-dash .ic-app-ico img{width:30px;height:30px;object-fit:contain;display:block}',
'#ic-dash .ic-app-name{font-size:10px;line-height:1.15;max-width:100%;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;opacity:.9}',
'#ic-dash .ic-app-letter{font-size:18px;font-weight:700;color:#fff}',
'#ic-dash .ic-app-empty{grid-column:1/-1;font-size:11px;opacity:.65;text-align:center;padding:6px 0}',
'#ic-dash .ic-card[data-id=shortcuts] .ic-body{overflow:auto}',
'#ic-apps{position:fixed;inset:0;z-index:2147483645;display:none;align-items:center;justify-content:center;background:rgba(0,0,0,.32);backdrop-filter:blur(3px)}',
'#ic-apps.ic-open{display:flex}',
'#ic-apps .ic-pick{width:min(560px,calc(100vw - 36px));max-height:min(620px,calc(100vh - 60px));display:flex;flex-direction:column;overflow:hidden;border-radius:18px;border:1px solid rgba(255,255,255,.18);background:rgba(32,32,38,.97);box-shadow:0 24px 70px rgba(0,0,0,.5);color:#f2f2f7;font-family:Segoe UI,system-ui,sans-serif}',
'#ic-apps .ic-pick-head{display:flex;gap:8px;align-items:center;padding:12px 14px;border-bottom:1px solid rgba(255,255,255,.1)}',
'#ic-apps .ic-pick-title{font-size:14px;font-weight:600;margin-right:4px}',
'#ic-apps .ic-pick-search{flex:1 1 auto;min-width:70px;background:rgba(255,255,255,.1);border:1px solid rgba(255,255,255,.12);border-radius:8px;color:inherit;padding:6px 9px;font:inherit;font-size:12px;outline:none}',
'#ic-apps .ic-pick-btn{background:rgba(255,255,255,.12);border:0;border-radius:8px;color:inherit;padding:6px 9px;font:inherit;font-size:11px;cursor:pointer}',
'#ic-apps .ic-pick-btn:hover{background:#0a84ff}',
'#ic-apps .ic-app-list{overflow:auto;padding:6px 8px 10px}',
'#ic-apps .ic-pick-section{font-size:10px;font-weight:600;letter-spacing:.06em;text-transform:uppercase;opacity:.55;padding:9px 8px 4px}',
'#ic-apps .ic-pick-row{display:flex;align-items:center;gap:9px;padding:6px 7px;border-radius:9px}',
'#ic-apps .ic-pick-row:hover{background:rgba(255,255,255,.08)}',
'#ic-apps .ic-pick-ico{width:28px;height:28px;flex:0 0 auto;display:flex;align-items:center;justify-content:center;border-radius:7px;overflow:hidden;background:rgba(255,255,255,.1)}',
'#ic-apps .ic-pick-ico img{width:22px;height:22px;object-fit:contain}',
'#ic-apps .ic-pick-name{flex:1;font-size:12px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}',
'#ic-apps .ic-pick-open,#ic-apps .ic-pick-pin{border:0;border-radius:7px;background:rgba(255,255,255,.12);color:inherit;font:inherit;font-size:10px;padding:5px 8px;cursor:pointer}',
'#ic-apps .ic-pick-open:hover,#ic-apps .ic-pick-pin:hover{background:#0a84ff}',
'#ic-apps .ic-pick-pin[data-on]{color:#64d2ff}',
'#ic-apps .ic-pick-empty{font-size:12px;opacity:.6;padding:14px 8px}',
'#ic-apps .ic-pick-letter{font-size:14px;font-weight:700}'

].join('\n');

function post(m){try{window.chrome.webview.postMessage(m);}catch(e){}}
function esc(s){return String(s==null?'':s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/\x22/g,'&quot;');}
function norm(s){
s=s||{};
var given=(s.order&&s.order.length)?s.order:IDS,i,seen={},order=[];
for(i=0;i<given.length;i++){var g=given[i];if(IDS.indexOf(g)>=0&&!seen[g]){seen[g]=1;order.push(g);}}
for(i=0;i<IDS.length;i++){if(!seen[IDS[i]]){order.push(IDS[i]);}}
var hidden=[],gh=s.hidden||[];
for(i=0;i<gh.length;i++){if(IDS.indexOf(gh[i])>=0&&hidden.indexOf(gh[i])<0){hidden.push(gh[i]);}}
var free={},gf=(s.free&&typeof s.free==='object')?s.free:{};
for(i=0;i<IDS.length;i++){
var g=gf[IDS[i]];
if(!g||typeof g!=='object'){continue;}
var fx=parseFloat(g.x),fy=parseFloat(g.y),fw=parseFloat(g.w),fh=parseFloat(g.h);
if(!isFinite(fx)||!isFinite(fy)||!isFinite(fw)||!isFinite(fh)){continue;}
if(fw<80||fh<60){continue;}
free[IDS[i]]={x:Math.max(0,Math.round(fx)),y:Math.max(0,Math.round(fy)),w:Math.round(fw),h:Math.round(fh)};
}
var apps=[],ga=(s.apps&&s.apps.length)?s.apps:[];
for(i=0;i<ga.length&&apps.length<80;i++){
var ga1=ga[i];if(!ga1||!ga1.name){continue;}
var gk=String(ga1.id||ga1.path||ga1.url||ga1.name),dup=false;
for(var gj=0;gj<apps.length;gj++){if(appKey(apps[gj])===gk){dup=true;break;}}
if(!dup){apps.push({id:String(ga1.id||''),name:String(ga1.name),path:String(ga1.path||''),url:String(ga1.url||''),icon:String(ga1.icon||'')});}
}
return {order:order,hidden:hidden,free:free,apps:apps,ver:Number(s.ver)||0};
}
function hide1(id){return state.hidden.indexOf(id)>=0;}
function accColor(a){
a=String(a||'');
var h=0,i,pal=['#4a9eff','#30d158','#ff9f0a','#bf5af2','#ff375f'];
for(i=0;i<a.length;i++){h=(h*31+a.charCodeAt(i))>>>0;}
return pal[h%pal.length];
}
function loc(){return prefs.locale||undefined;}
function h12(){return prefs.hour12!==false;}
function shortTime(d){return d.toLocaleTimeString(loc(),{hour:'numeric',minute:'2-digit',hour12:h12()});}
function dayLabel(d){
var now=new Date();
var today=new Date(now.getFullYear(),now.getMonth(),now.getDate());
var other=new Date(d.getFullYear(),d.getMonth(),d.getDate());
var diff=Math.round((other-today)/86400000);
if(diff===0){return 'Today';}
if(diff===1){return 'Tomorrow';}
return d.toLocaleDateString(loc(),{weekday:'short'});
}

var HOLIDAYS={'1-1':'Happy New Year!','2-14':'Happy Valentine’s Day!','3-17':'Happy St Patrick’s Day!','7-4':'Happy Independence Day!','10-31':'Happy Halloween!','12-24':'Happy Christmas Eve!','12-25':'Merry Christmas!','12-31':'Happy New Year’s Eve!'};
function nthWeekday(y,m,w,d){var first=new Date(y,m,1).getDay();return 1+((7+d-first)%7)+7*(w-1);}
// One line under the clock: holidays, plus the user's own birthday only.
function birthdayMonthDay(v){
v=String(v||'');
var m=/^(\d{4})-(\d{1,2})-(\d{1,2})/.exec(v);
if(m&&m[1]!=='0001'){return {month:Number(m[2]),day:Number(m[3])};}
m=/^(\d{1,2})-(\d{1,2})$/.exec(v);
if(m){return {month:Number(m[1]),day:Number(m[2])};}
return null;
}
function ownBirthday(){
var o=data.outlook,b=null,i,e,nm=String(prefs.name||'').toLowerCase();
if(o&&o.ok&&o.me&&o.me.birthday){b=birthdayMonthDay(o.me.birthday);}
if(!b&&nm&&prefs.birthdays){
for(i=0;i<prefs.birthdays.length;i++){
e=prefs.birthdays[i];
if(e&&String(e.name||'').toLowerCase()===nm){b={month:Number(e.month),day:Number(e.day)};break;}
}
}
return b;
}
function occasion(){
var d=new Date(),m=d.getMonth()+1,day=d.getDate(),out=[],b=ownBirthday();
if(b&&b.month===m&&b.day===day){
var nm=String(prefs.name||'').replace(/^\s+|\s+$/g,'');
out.push(nm?('Happy birthday, '+nm+'!'):'Happy birthday!');
}
var hol=HOLIDAYS[m+'-'+day];
if(m===11&&day===nthWeekday(d.getFullYear(),10,4,4)){hol='Happy Thanksgiving!';}
if(hol&&out.indexOf(hol)<0){out.push(hol);}
return out;
}
function bodyClock(){
var d=new Date(),h=d.getHours();
var greet=h<5?'Good night':(h<12?'Good morning':(h<18?'Good afternoon':'Good evening'));
var nm=String(prefs.name||'').replace(/^\s+|\s+$/g,'');
return '<div class=ic-big>'+esc(d.toLocaleTimeString(loc(),{hour:'numeric',minute:'2-digit',hour12:h12()}))+'</div>'+
'<div class=ic-sub>'+esc(d.toLocaleDateString(loc(),{weekday:'long',day:'numeric',month:'long'}))+'</div>'+
'<div class=ic-dim>'+esc(nm?greet+', '+nm:greet)+'</div>'+
(function(){var o=occasion();return o.length?('<div class=ic-occ>'+esc(o.join(' · '))+'</div>'):'';})();
}
var DEFAULT_APPS=[['Photos','photos','#ff9f0a','\ud83d\udcf7'],['Drive','drive','#34c759','\u2601'],['Notes','notes','#ffd60a','\ud83d\udcdd'],['Calendar','calendar','#ff375f','\ud83d\udcc5'],['Mail','mail','#0a84ff','\u2709'],['Reminders','reminders','#ff9f0a','\u2713']];
var appCatalog=null;
function appKey(a){return String((a&&(a.id||a.path||a.url||a.name))||'');}
function appById(key){
var i,a;
for(i=0;i<((state.apps||[]).length);i++){a=state.apps[i];if(appKey(a)===key){return a;}}
if(appCatalog){for(i=0;i<appCatalog.length;i++){a=appCatalog[i];if(appKey(a)===key){return a;}}}
return null;
}
function appPinned(a){
var key=appKey(a),i;
for(i=0;i<((state.apps||[]).length);i++){if(appKey(state.apps[i])===key){return true;}}
return false;
}
function appGlyph(a){
if(a&&a.icon){return '<img src='+a.icon+'>';}
return '<span class=ic-app-letter>'+esc(String(((a&&a.name)||'?').charAt(0)).toUpperCase())+'</span>';
}
function bodyApps(){
var out='<div class=ic-apps>',i,a;
for(i=0;i<DEFAULT_APPS.length;i++){
a=DEFAULT_APPS[i];
out+='<a class=ic-app href=https://www.icloud.com/'+a[1]+'><span class=ic-app-ico style=background:'+a[2]+'>'+a[3]+'</span><span class=ic-app-name>'+a[0]+'</span></a>';
}
var pins=state.apps||[];
for(i=0;i<pins.length;i++){
a=pins[i];
var tag=a.url?'a':'button';
var where=a.url?('href='+a.url):('data-launch='+encodeURIComponent(appKey(a)));
out+='<'+tag+' class=ic-app '+where+'><span class=ic-app-ico>'+appGlyph(a)+'</span><span class=ic-app-name>'+esc(a.name)+'</span></'+tag+'>';
}
if(!pins.length){out+='<div class=ic-app-empty>Use ... to pin apps</div>';}
return out+'</div>';
}
function addPins(list){
if(!state.apps){state.apps=[];}
for(var i=0;i<list.length;i++){
var a=list[i];if(!a||!a.name){continue;}
var key=appKey(a);if(!key){continue;}
var found=false;
for(var j=0;j<state.apps.length;j++){if(appKey(state.apps[j])===key){found=true;break;}}
if(!found){state.apps.push({id:String(a.id||''),name:String(a.name),path:String(a.path||''),url:String(a.url||''),icon:String(a.icon||'')});}
}
persist();render();renderAppPicker();
}
function launchApp(key){
if(/^https?:/i.test(key)){location.href=key;return;}
post('app-open:'+encodeURIComponent(key));
}
function togglePin(key){
if(!state.apps){state.apps=[];}
var at=-1,i;
for(i=0;i<state.apps.length;i++){if(appKey(state.apps[i])===key){at=i;break;}}
if(at>=0){state.apps.splice(at,1);}
else{var a=appById(key);if(a){state.apps.push({id:String(a.id||''),name:String(a.name),path:String(a.path||''),url:String(a.url||''),icon:String(a.icon||'')});}}
persist();render();renderAppPicker();
}
function appRow(a,pinned){
var key=encodeURIComponent(appKey(a));
return '<div class=ic-pick-row><span class=ic-pick-ico>'+appGlyph(a)+'</span><span class=ic-pick-name>'+esc(a.name)+'</span><button class=ic-pick-open data-open='+key+'>Open</button><button class=ic-pick-pin data-pin='+key+(pinned?' data-on=1':'')+'>'+(pinned?'Unpin':'Pin')+'</button></div>';
}
function renderAppPicker(){
var list=document.getElementById('ic-app-list');if(!list){return;}
var q='';var s=document.getElementById('ic-app-search');if(s){q=String(s.value||'').toLowerCase();}
var rows='',i,a,pins=state.apps||[];
if(pins.length){rows+='<div class=ic-pick-section>Pinned</div>';for(i=0;i<pins.length;i++){rows+=appRow(pins[i],true);}}
rows+='<div class=ic-pick-section>All apps</div>';
if(appCatalog===null){rows+='<div class=ic-pick-empty>Reading the Start menu...</div>';}
else{
var n=0;
for(i=0;i<appCatalog.length;i++){
a=appCatalog[i];
if(q&&String(a.name||'').toLowerCase().indexOf(q)<0){continue;}
rows+=appRow(a,appPinned(a));n++;
if(n>=180){break;}
}
if(!n){rows+='<div class=ic-pick-empty>No matching apps</div>';}
}
list.innerHTML=rows;
}
function openApps(){
setupApps();
var p=document.getElementById('ic-apps');if(!p){return;}
p.classList.add('ic-open');renderAppPicker();
if(appCatalog===null){post('apps-list');}
}
function setupApps(){
if(document.getElementById('ic-apps')){return;}
var p=document.createElement('div');p.id='ic-apps';
p.innerHTML='<div class=ic-pick><div class=ic-pick-head><div class=ic-pick-title>Apps</div><input id=ic-app-search class=ic-pick-search><button id=ic-app-browse class=ic-pick-btn>Browse...</button><button id=ic-app-exp class=ic-pick-btn>Explorer</button><button id=ic-app-close class=ic-pick-btn>Done</button></div><div id=ic-app-list class=ic-app-list></div></div>';
document.documentElement.appendChild(p);
var s=p.querySelector('#ic-app-search');if(s){s.placeholder='Search apps';s.addEventListener('input',renderAppPicker);}
p.addEventListener('click',function(e){
var b=e.target.closest?e.target.closest('button'):null;if(!b){return;}
if(b.id==='ic-app-close'){p.classList.remove('ic-open');return;}
if(b.id==='ic-app-browse'){post('app-browse');return;}
if(b.id==='ic-app-exp'){post('explorer-menu');return;}
if(b.hasAttribute('data-open')){launchApp(decodeURIComponent(b.getAttribute('data-open')));return;}
if(b.hasAttribute('data-pin')){togglePin(decodeURIComponent(b.getAttribute('data-pin')));}
});
document.addEventListener('click',function(e){
if(p.classList.contains('ic-open')&&!p.contains(e.target)&&!e.target.closest('.ic-more')){p.classList.remove('ic-open');}
},true);
document.addEventListener('keydown',function(e){if(e.key==='Escape'){p.classList.remove('ic-open');}},true);
}
function bodyWeather(){
var w=data.weather;
if(!w){return '<div class=ic-dim>Loading weather&hellip;</div>';}
if(!w.ok){
var msg='Weather unavailable';
if(w.error==='no-location'){msg='Set a location in the gear menu';}
else if(w.error==='not-found'){msg='Location not found';}
return '<div class=ic-dim>'+esc(msg)+'</div>';
}
var u=(w.unit==='F'||w.unit==='C')?w.unit:(prefs.unit==='F'?'F':'C');
var t=(w.temp==null)?'--':Math.round(w.temp)+'&deg;'+u;
var out='<div class=ic-roww><div class=ic-big>'+t+'</div><div class=ic-col>'+
'<div class=ic-sub>'+esc(CODES[w.code]||'')+'</div>'+
'<div class=ic-dim>'+esc(w.place||'')+'</div></div></div>';
if(w.days&&w.days.length){
out+='<div class=ic-days>';
for(var i=0;i<w.days.length&&i<5;i++){
var d=w.days[i];
var mx=(d.max==null)?'--':Math.round(d.max);
var mn=(d.min==null)?'--':Math.round(d.min);
out+='<div class=ic-day'+(i===4?' data-extra=1':'')+'><div class=ic-dim>'+esc(d.day)+'</div>'+
'<div class=ic-num>'+mx+'&deg;</div><div class=ic-dim2>'+mn+'&deg;</div></div>';
}
out+='</div>';
}
return out;
}
var FEEDERR={'outlook-not-running':'Outlook is not running','no-config':'Mail is not set up yet','graph-auth':'Sign in to Microsoft','graph-failed':'Microsoft unreachable','gmail-config':'Gmail not set up yet','gmail-auth':'Gmail sign-in failed','gmail-failed':'Gmail unreachable','helper-missing':'Mail helper missing','timeout':'Mail fetch timed out'};
function feedMsg(o){var c=(o&&o.error)?String(o.error):'unknown';return FEEDERR[c]||('Mail unavailable ('+c+')');}
function bodyCalendar(){
var o=data.outlook;
if(!o){return '<div class=ic-dim>Loading mail&hellip;</div>';}
if(!o.ok){return '<div class=ic-dim>'+esc(feedMsg(o))+'</div>';}
var ev=o.calendar||[];
if(!ev.length){return '<div class=ic-dim>No events in the next 7 days</div>';}
var out='',i;
for(i=0;i<ev.length&&i<10;i++){
var e=ev[i],d=new Date(e.time);
if(isNaN(d.getTime())){continue;}
out+='<div class=ic-item><span class=ic-acct style=background:'+accColor(e.account||'')+'></span>'+
'<span class=ic-when>'+esc(dayLabel(d))+' '+esc(e.allday?'All day':shortTime(d))+'</span>'+
'<span class=ic-title2>'+esc(e.subject)+'</span></div>';
}
return out;
}
function bodyMail(){
var o=data.outlook;
if(!o){return '<div class=ic-dim>Loading mail&hellip;</div>';}
if(!o.ok){return '<div class=ic-dim>'+esc(feedMsg(o))+'</div>';}
var list=o.mail||[];
var out='';
if(o.unread!=null){out+='<div class=ic-dim>'+esc(o.unread)+' unread in '+esc((o.accounts||[]).length)+' accounts</div>';}
if(!list.length){return out+'<div class=ic-dim>No messages</div>';}
for(var i=0;i<list.length&&i<12;i++){
var m=list[i],t=m.time?new Date(m.time):null;
var when=(t&&!isNaN(t.getTime()))?shortTime(t):'';
out+='<div class=ic-item'+(m.unread?' data-new=1':'')+'>'+
'<span class=ic-acct style=background:'+accColor(m.account||'')+'></span>'+
'<span class=ic-from>'+esc(m.from)+'</span>'+
'<span class=ic-subject>'+esc(m.subject)+'</span>'+
'<span class=ic-dim2>'+esc(when)+'</span></div>';
}
return out;
}
function notesSource(){
var t=document.querySelector('.notes-home-tile')||document.querySelector('[class*=notes-home-tile]');
return t;
}
function notesMarkup(){
var t=notesSource();
if(!t){return '<div class=ic-dim style=padding:4px>Open iCloud Notes</div>';}
var c=t.cloneNode(true);
var hd=c.querySelector('header');
if(hd&&hd.parentNode){hd.parentNode.removeChild(hd);}
var all=c.querySelectorAll('*'),cand=null,best=1000;
for(var i=0;i<all.length;i++){
var tx=String(all[i].textContent||'');
if(tx.indexOf('All iCloud')>=0&&tx.length<best&&tx.length<120){best=tx.length;cand=all[i];}
}
if(cand&&cand.parentNode&&cand.parentNode!==c){
var hp=cand.parentNode;
if(String(hp.textContent||'').length<140&&hp.parentNode){hp.parentNode.removeChild(hp);}
else{hp.removeChild(cand);}
}
c.style.width='100%';
c.style.height='100%';
c.style.minWidth='0';
return '<div class=ic-notes>'+c.outerHTML+'</div>';
}
function bodyNotes(){return notesMarkup();}
var BODIES={clock:bodyClock,shortcuts:bodyApps,weather:bodyWeather,calendar:bodyCalendar,mail:bodyMail,notes:bodyNotes};
var NATIVE={notes:'.notes-home-tile',reminders:'.reminders-home-tile'},nativeBase={};
function syncNatives(){
for(var k in NATIVE){
var f=freeOf(k);
var tint=document.getElementById('ic-tint-'+k);
var t=document.querySelector(NATIVE[k]);
if(!f||!t){if(tint){tint.style.display='none';}continue;}
var c=t.closest('.tile-grid-tile-container')||t;
if(!nativeBase[k]){nativeBase[k]=[c.offsetWidth||660,c.offsetHeight||315];}
var bw=nativeBase[k][0],bh=nativeBase[k][1];
var sc=Math.min(f.w/bw,f.h/bh);
var tx=f.x+(f.w-bw*sc)/2,ty=f.y+(f.h-bh*sc)/2,tw=bw*sc,th=bh*sc;
c.style.position='fixed';
c.style.left=tx+'px';
c.style.top=ty+'px';
c.style.right='auto';
c.style.bottom='auto';
c.style.width=bw+'px';
c.style.height=bh+'px';
c.style.margin='0';
c.style.transformOrigin='0 0';
c.style.transform='scale('+sc+')';
c.style.zIndex='2147483630';
c.style.pointerEvents='auto';
c.style.background='transparent';
c.style.display='block';
if(!tint){
tint=document.createElement('div');
tint.id='ic-tint-'+k;
document.documentElement.appendChild(tint);
}
tint.style.position='fixed';
tint.style.left=tx+'px';
tint.style.top=ty+'px';
tint.style.width=tw+'px';
tint.style.height=th+'px';
tint.style.borderRadius='18px';
tint.style.pointerEvents='none';
tint.style.zIndex='2147483631';
tint.style.mixBlendMode='overlay';
tint.style.opacity='.85';
tint.style.boxShadow='inset 0 0 0 1px rgba(255,255,255,.16)';
tint.style.background=(k==='notes')
?'linear-gradient(145deg,rgba(96,120,255,.55),rgba(180,90,255,.18) 48%,rgba(255,255,255,0) 82%)'
:'linear-gradient(145deg,rgba(255,159,10,.55),rgba(255,80,110,.18) 48%,rgba(255,255,255,0) 82%)';
tint.style.display='block';
}
}

function shapeOf(f){
if(!f){return 'square';}
var r=f.w/f.h;
if(r>=1.55){return 'wide';}
if(f.h/f.w>=1.2){return 'tall';}
return 'square';
}
function cardHtml(id){
if(NATIVE[id]){
var nf=freeOf(id);
var npos=nf?(' style=left:'+nf.x+'px;top:'+nf.y+'px;width:'+nf.w+'px;height:'+nf.h+'px'):'';
return '<div class=ic-card data-native=1 data-id='+id+npos+'><span class=ic-grab title=Move>&#10303;</span></div>';
}
var off=hide1(id)?' data-off=1':'';
var f=freeOf(id);
var shape=shapeOf(f);
var pos=f?(' style=left:'+f.x+'px;top:'+f.y+'px;width:'+f.w+'px;height:'+f.h+'px'):'';
var tools=edit?'<span class=ic-tool data-act=hide title=Show>'+(off?'+':'&minus;')+'</span>':'';
return '<div class=ic-card data-id='+id+' data-shape='+shape+off+pos+'>'+
'<div class=ic-head><span class=ic-hleft><span class=ic-ico>'+(ICONS[id]||'')+'</span><span class=ic-h1>'+TITLES[id]+'</span></span>'+
'<span class=ic-hright>'+(id==='shortcuts'?'<span class=ic-more data-act=apps>&#8943;</span>':'')+'<span class=ic-tools>'+tools+'</span></span></div>'+
'<div class=ic-body>'+BODIES[id]()+'</div>'+
'<span class=ic-grab title=Move>&#10303;</span></div>';
}
function render(){
if(edit){document.documentElement.setAttribute('data-ic-edit','on');}
else{document.documentElement.removeAttribute('data-ic-edit');}
if(!box){return;}
var html='',i,vw=window.innerWidth||0,vh=window.innerHeight||0;
for(i=0;i<state.order.length;i++){
var id=state.order[i];
if(hide1(id)&&!edit){continue;}
var f=freeOf(id);
if(f&&vw>0){
if(f.x<0){f.x=0;}
if(f.x+f.w>vw-8){f.x=Math.max(0,vw-8-f.w);}
if(f.y<0){f.y=0;}
if(f.y+f.h>vh-8){f.y=Math.max(0,vh-8-f.h);}
}
html+=cardHtml(id);
}
if(edit){html+='<div class=ic-hint>Drag a tile by its corner grip. Shift-drag to resize. Esc when done.</div>';}
box.innerHTML=html;
syncNatives();
}
function persist(){post('dash-state:'+JSON.stringify(state));}
function freeOf(id){return (state.free&&state.free[id])||null;}
function hasFree(){for(var i=0;i<IDS.length;i++){if(freeOf(IDS[i])){return true;}}return false;}
function ensureFree(){
if(!state.free){state.free={};}
var vw=window.innerWidth||1200,vh=window.innerHeight||800;
var nh=vh>=1000?315:248,nw=Math.min(vh>=1000?660:520,Math.max(300,vw-48)),ny=Math.max(60,Math.min(120,Math.round(vh*0.07)));
if(!freeOf('notes')){state.free.notes={x:24,y:ny,w:nw,h:nh};}
if(!freeOf('reminders')){state.free.reminders={x:24,y:ny+nh+12,w:nw,h:nh};}
}
function allCramped(){
for(var i=0;i<IDS.length;i++){var f=freeOf(IDS[i]);if(!f||f.w>145){return false;}}
return true;
}
// First run: one row of separate tiles across the bottom, sized like widgets.
function defaultLayout(){
var vw=window.innerWidth||1200,vh=window.innerHeight||800,gap=14,i;
state.free={};state.ver=2;
var nh=vh>=1000?315:248,nw=Math.min(vh>=1000?660:520,Math.max(300,vw-48));
var ny=Math.max(60,Math.min(120,Math.round(vh*0.07)));
state.free.notes={x:24,y:ny,w:nw,h:nh};
state.free.reminders={x:24,y:ny+nh+12,w:nw,h:nh};
var wide={clock:190,shortcuts:250,weather:210,calendar:250,mail:230};
var custom=['clock','shortcuts','weather','calendar','mail'],sum=0;
for(i=0;i<custom.length;i++){sum+=wide[custom[i]];}
var total=sum+gap*(custom.length-1);
var x=Math.max(8,Math.round((vw-total)/2));
var ch=Math.max(150,Math.min(210,Math.round(vh*0.18)));
var cy=vh-ch-72;
var floorY=ny+nh*2+34;
if(cy<floorY){cy=floorY;}
if(cy+ch>vh-12){ch=Math.max(120,vh-cy-12);}
for(i=0;i<custom.length;i++){
state.free[custom[i]]={x:x,y:cy,w:wide[custom[i]],h:ch};
x+=wide[custom[i]]+gap;
}
}
var drag=null,skipClick=false;
function clampNum(v,a,b){return v<a?a:(v>b?b:v);}
function onPointerDown(e){
if(!box||e.button!==0){return;}
var t=e.target;
if(!t||!t.closest){return;}
if(t.closest('.ic-tool')){return;}
var grab=t.closest('.ic-grab');
if(!grab){grab=t.closest('.ic-head');}
if(!grab){return;}
var card=grab.closest('.ic-card');
if(!card){return;}
var id=card.getAttribute('data-id'),f=freeOf(id);
if(!f){return;}
e.preventDefault();
drag={card:card,id:id,resize:!!e.shiftKey,moved:false,
sx:e.clientX,sy:e.clientY,x:f.x,y:f.y,w:f.w,h:f.h};
card.setAttribute('data-drag','on');
try{card.setPointerCapture(e.pointerId);}catch(x){}
}
function onPointerMove(e){
if(!drag){return;}
var f=freeOf(drag.id);
if(!f){return;}
var dx=e.clientX-drag.sx,dy=e.clientY-drag.sy,vw=window.innerWidth||0,vh=window.innerHeight||0;
if(Math.abs(dx)+Math.abs(dy)>3){drag.moved=true;}
if(drag.resize){
f.w=clampNum(Math.round(drag.w+dx),160,Math.max(160,vw-16));
f.h=Math.max(90,Math.round(drag.h+dy));
}else{
f.x=clampNum(Math.round(drag.x+dx),0,Math.max(0,vw-f.w-8));
f.y=clampNum(Math.round(drag.y+dy),0,Math.max(0,vh-60));
}
drag.card.style.left=f.x+'px';
drag.card.style.top=f.y+'px';
drag.card.style.width=f.w+'px';
drag.card.style.height=f.h+'px';
drag.card.setAttribute('data-shape',shapeOf(f));
syncNatives();
e.preventDefault();
}
function onPointerUp(e){
if(!drag){return;}
var card=drag.card;
if(drag.moved){skipClick=true;}
card.removeAttribute('data-drag');
try{card.releasePointerCapture(e.pointerId);}catch(x){}
drag=null;
persist();
}

function injectCss(){
if(document.getElementById('ic-dash-style')){return;}
var st=document.createElement('style');
st.id='ic-dash-style';
st.textContent=CSS;
(document.head||document.documentElement).appendChild(st);
}
function diag(mode,grid){
if(diagged){return;}
diagged=true;
var d=document.getElementById('ic-dash'),info={mode:mode,vh:window.innerHeight};
if(d){var r=d.getBoundingClientRect();info.dash=[Math.round(r.left),Math.round(r.top),Math.round(r.width),Math.round(r.height)];info.cards=d.querySelectorAll('.ic-card').length;info.onScreen=r.top<window.innerHeight-20;}
if(grid){var g=grid.getBoundingClientRect();info.grid=[Math.round(g.left),Math.round(g.top),Math.round(g.width),Math.round(g.height)];info.gridClass=String(grid.className||'').slice(0,50);}
info.tags=document.querySelectorAll('a,button,[role=button]').length;
info.vp=[Math.round(window.innerWidth),Math.round(window.innerHeight)];
info.dir=getComputedStyle(document.documentElement).direction+'/'+getComputedStyle(document.body).direction;
info.cards=[];
var cc=document.querySelectorAll('#ic-dash .ic-card');
for(var ci=0;ci<cc.length;ci++){
var ce=cc[ci],cr=ce.getBoundingClientRect(),cs=getComputedStyle(ce);
info.cards.push(ce.getAttribute('data-id')+'='+[Math.round(cr.left),Math.round(cr.top),Math.round(cr.width),Math.round(cr.height)].join(',')+'|'+cs.position+'|'+cs.left+'|'+cs.top+'|'+cs.pointerEvents+'|'+String(ce.getAttribute('style')));
}
var pc=document.querySelector('#ic-dash .ic-card');
if(pc){
var pr=pc.getBoundingClientRect();
info.hit=(function(){var e=document.elementFromPoint(pr.left+pr.width/2,pr.top+pr.height/2);return e?(e.tagName+(e.id?'#'+e.id:'')+'.'+String(e.className||'').slice(0,40)):'none';})();
var gc=pc.querySelector('.ic-grab');
if(gc){var gr=gc.getBoundingClientRect();var ge=document.elementFromPoint(gr.left+gr.width/2,gr.top+gr.height/2);info.grip=Math.round(gr.left)+','+Math.round(gr.top)+','+Math.round(gr.width)+'|'+(ge?(ge.tagName+(ge.id?'#'+ge.id:'')+'.'+String(ge.className||'').slice(0,30)):'none');}
}
post('dash-diag:'+JSON.stringify(info));
}
function mount(){
if(box&&document.body&&document.contains(box)){return;}
if(box){box.remove();box=null;}
if(!document.body){return;}
box=document.createElement('div');
box.id='ic-dash';
(document.documentElement||document.body).appendChild(box);
if(!hasFree()){defaultLayout();}
else if(state.ver<2&&allCramped()){defaultLayout();}
ensureFree();
persist();
render();
diag('overlay');
}
function onToolClick(e){
if(!e.target.closest){return;}
var more=e.target.closest('.ic-more');
if(more){e.preventDefault();e.stopPropagation();openApps();return;}
var launcher=e.target.closest('.ic-app[data-launch]');
if(launcher){e.preventDefault();e.stopPropagation();launchApp(decodeURIComponent(launcher.getAttribute('data-launch')));return;}
if(!edit){return;}
var tool=e.target.closest('.ic-tool');
if(!tool){return;}
var card=tool.closest('.ic-card');
if(!card){return;}
e.preventDefault();
e.stopPropagation();
var id=card.getAttribute('data-id');
var at=state.hidden.indexOf(id);
if(at>=0){state.hidden.splice(at,1);}else{state.hidden.push(id);}
persist();
render();
}
function hideCustomize(){
var els=document.querySelectorAll('button,[role=button],a');
for(var i=0;i<els.length;i++){
var el=els[i];
var txt=String(el.innerText||el.textContent||'').replace(/\s+/g,' ').trim().toLowerCase();
var lab=String(el.getAttribute('aria-label')||el.getAttribute('title')||'').trim().toLowerCase();
if(txt==='customize'||lab==='customize'){el.style.setProperty('display','none','important');}
}
}
function boot(){
if(booted){return;}
booted=true;
injectCss();
mount();
setupApps();
hideCustomize();
setInterval(hideCustomize,1500);
try{
var mo=new MutationObserver(function(){
if(mountTimer){return;}
mountTimer=setTimeout(function(){mountTimer=0;mount();},500);
});
mo.observe(document.documentElement,{childList:true,subtree:true});
}catch(e){}
setInterval(function(){
if(!box){mount();return;}
if(!document.contains(box)||box.parentNode!==document.documentElement){(document.documentElement||document.body).appendChild(box);}
injectCss();
var fc=box.querySelector('.ic-card');
if(fc&&!fc.getAttribute('style')){defaultLayout();render();persist();}
},4000);
setInterval(function(){
if(!box||edit||hide1('clock')){return;}
var c=box.querySelector('.ic-card[data-id=clock] .ic-body');
if(c){c.innerHTML=bodyClock();}
},1000);
setInterval(syncNatives,1500);
document.addEventListener('click',onToolClick,true);
document.addEventListener('click',function(e){
if(edit&&skipClick){skipClick=false;e.preventDefault();e.stopPropagation();}
},true);
document.addEventListener('pointerdown',onPointerDown,true);
document.addEventListener('pointermove',onPointerMove,true);
document.addEventListener('pointerup',onPointerUp,true);
document.addEventListener('pointercancel',onPointerUp,true);
window.addEventListener('resize',function(){if(box){render();}},false);
document.addEventListener('click',function(e){
var t=e.target;
if(!t||!t.closest){return;}
if(t.closest('.ic-tool')||t.closest('a')){return;}
var card=t.closest('.ic-card[data-id=notes]');
if(!card||edit||skipClick){return;}
location.href='https://www.icloud.com/notes';
},true);
window.icDash={
ready:true,
arrange:function(){if(!box){mount();}edit=!edit;if(!edit){drag=null;}render();persist();},
resetLayout:function(){state.free={};defaultLayout();drag=null;render();persist();},
openApps:openApps,
addPins:addPins,
setConfig:function(s){state=norm(s);render();},
setData:function(k,v){data[k]=v;render();}
};
try{
window.chrome.webview.addEventListener('message',function(e){
var m=e.data;
if(!m||!m.ic){return;}
if(m.ic==='dash-config'){state=norm(m.state);if(!hasFree()){defaultLayout();persist();}render();}
else if(m.ic==='prefs'){prefs=(m.prefs&&typeof m.prefs==='object')?m.prefs:prefs;render();}
else if(m.ic==='widget'){data[m.kind]=m.data;render();}
else if(m.ic==='apps'){appCatalog=(m.apps&&m.apps.length)?m.apps:[];renderAppPicker();}
else if(m.ic==='apps-add'){addPins(m.apps||[]);}
});
}catch(x){}
document.addEventListener('keydown',function(e){
if(e.key==='Escape'&&edit){edit=false;render();e.stopPropagation();}
},true);
}
if(document.readyState==='loading'){document.addEventListener('DOMContentLoaded',boot);}
else{boot();}
document.addEventListener('DOMContentLoaded',boot);
})();";
        }

        // Runs in every document. The Start-menu "New iCloud Note" shortcut asks
        // the panel to call window.icNewNote(): the click has to happen inside the
        // page, because Notes builds its toolbar at runtime and Apple's class
        // names change, so the button is found by its wording.
        private string BuildNewNoteScript()
        {
            return @"(function(){
var FLAG='ic-new-note';
function post(m){try{window.chrome.webview.postMessage(m);}catch(e){}}
function host(){return /(^|\.)icloud\.com$/.test(location.hostname);}
function onNotes(){return host()&&/^\/notes(\/|$)/.test(location.pathname);}
function win(el){try{return (el.ownerDocument&&el.ownerDocument.defaultView)||window;}catch(e){return window;}}
// Apple renders the Notes app in a frame (the top document keeps only the
// dashboard's own links), so every lookup walks frames and shadow roots.
function docs(){
 var out=[],queue=[document];
 while(queue.length&&out.length<12){
  var node=queue.shift();
  out.push(node);
  var found;
  try{found=node.querySelectorAll('iframe');}catch(e){found=[];}
  for(var i=0;i<found.length;i++){
   var inner=null;
   try{inner=found[i].contentDocument;}catch(e){}
   if(inner&&inner.documentElement){queue.push(inner);}
  }
  try{found=node.querySelectorAll('*');}catch(e){found=[];}
  for(var j=0;j<found.length;j++){
   var sr=found[j].shadowRoot;
   if(sr){queue.push(sr);}
  }
 }
 return out;
}
function vis(el){
 if(!el||!el.getBoundingClientRect){return false;}
 var r=el.getBoundingClientRect();
 if(r.width<2||r.height<2){return false;}
 var cs=win(el).getComputedStyle(el);
 return cs.display!=='none'&&cs.visibility!=='hidden'&&parseFloat(cs.opacity||'1')>0.05;
}
function label(el){
 return String(el.getAttribute('aria-label')||'')+' '+String(el.getAttribute('title')||'')+' '+
  String(el.getAttribute('data-tooltip')||'')+' '+String((el.innerText||el.textContent||'').slice(0,60));
}
function findCompose(){
 var list=docs(),exact=null,rightmost=null,rightEdge=-1e9;
 for(var d=0;d<list.length;d++){
  var all;
  try{all=list[d].querySelectorAll('button,[role=button],a,[aria-label],[title]');}catch(e){continue;}
  for(var i=0;i<all.length;i++){
   var el=all[i],l=label(el);
   if(!l||!vis(el)){continue;}
   if(!/create[^a-z]{0,4}(a\s+)?note/i.test(l)&&!/\b(new note|create note|compose)\b/i.test(l)){continue;}
   var a=String(el.getAttribute('aria-label')||'').trim().toLowerCase();
   if(!exact&&(a==='create a note'||a==='create note')){exact=el;}
   var r=el.getBoundingClientRect();
   if(r.right>rightEdge){rightEdge=r.right;rightmost=el;}
  }
 }
 return exact||rightmost;
}
function editable(){
 var list=docs(),out=[];
 for(var d=0;d<list.length;d++){
  var l;
  try{l=list[d].querySelectorAll('[contenteditable=true],textarea');}catch(e){continue;}
  for(var i=0;i<l.length;i++){if(vis(l[i])){out.push(l[i]);}}
 }
 return out;
}
function pointer(el){
 var r=el.getBoundingClientRect();
 var x=Math.round(r.left+r.width/2),y=Math.round(r.top+r.height/2);
 var w=win(el);
 var base={bubbles:true,cancelable:true,composed:true,view:w,clientX:x,clientY:y,screenX:x,screenY:y,button:0,buttons:1,pointerId:1,pointerType:'mouse',isPrimary:true};
 function send(ctor,extra){
  var o={};for(var k in base){o[k]=base[k];}
  for(var k2 in extra){o[k2]=extra[k2];}
  try{el.dispatchEvent(new w[ctor](o));}catch(e){}
 }
 send(PointerEvent,{});
 send(MouseEvent,{});
 send(PointerEvent,{buttons:0});
 send(MouseEvent,{buttons:0});
}
function topRect(el){
 var r=el.getBoundingClientRect(),d=el.ownerDocument,guard=0;
 while(d&&d!==document&&guard++<4){
  var f=null;
  try{f=d.defaultView&&d.defaultView.frameElement;}catch(e){}
  if(!f){break;}
  var fr=f.getBoundingClientRect();
  r={left:r.left+fr.left,top:r.top+fr.top,width:r.width,height:r.height};
  d=f.ownerDocument;
 }
 return r;
}
// Ask the host for a real mouse click: Apple's compose button ignores every
// event a script can dispatch. The rect is the one measured when the button was
// found - Apple re-renders the toolbar, and a detached node reports 0,0.
function requestRealClick(r){
 if(!r||!(r.width>0)||!(r.height>0)){post('new-note:fail:no-rect');return;}
 post('new-note:input:'+Math.round(r.left)+','+Math.round(r.top)+','+Math.round(r.width)+','+Math.round(r.height)+','+Math.round(window.innerWidth));
}
function clearFlag(){try{sessionStorage.removeItem(FLAG);}catch(e){}}
function attemptOpen(btn){
 var r=null;
 try{r=btn.getBoundingClientRect();}catch(e){}
 post('new-note:click:'+btn.tagName+'|'+String(btn.getAttribute('aria-label')||btn.getAttribute('title')||'').slice(0,40)+
  '|'+String(btn.className||'').slice(0,40)+'|'+(r?Math.round(r.left)+','+Math.round(r.top)+','+Math.round(r.width)+'x'+Math.round(r.height):'-')+
  '|doc='+(btn.ownerDocument===document?'top':'frame'));
 // Measure while the element is still in the page: the fallback click needs it.
 var spot=topRect(btn);
 var before=editable(),stage=0,t0=Date.now(),activeBefore=null;
 try{activeBefore=btn.ownerDocument.activeElement;}catch(e){}
 var timer=setInterval(function(){
  var list=editable(),found=null;
  if(list.length>before.length){found=list[list.length-1];}
  else{
   var a=null;
   try{a=btn.ownerDocument.activeElement;}catch(e){}
   if(a&&a!==activeBefore&&vis(a)&&(a.tagName==='TEXTAREA'||String((a.getAttribute&&a.getAttribute('contenteditable'))||'')!=='')){found=a;}
  }
  if(found){
   clearInterval(timer);
   try{found.focus();}catch(e){}
   clearFlag();
   post('new-note:ok');
   return;
  }
  var waited=Date.now()-t0;
  if(stage===0&&waited>=1500){stage=1;pointer(btn);}
  else if(stage===1&&waited>=3000){stage=2;requestRealClick(spot);}
  else if(stage===2&&waited>=9000){
   stage=3;clearInterval(timer);clearFlag();
   var dump=[];
   for(var x=0;x<list.length&&dump.length<6;x++){
    var e=list[x];
    dump.push(e.tagName+'|ce='+String(e.getAttribute('contenteditable')||'-')+'|role='+String(e.getAttribute('role')||'-')+
     '|'+String(e.getAttribute('aria-label')||e.className||'').slice(0,30));
   }
   var act=null;
   try{act=btn.ownerDocument.activeElement;}catch(e){}
   post('new-note:fail:no-editor:editable='+list.length+'|'+dump.join(' ~ ')+
    '|active='+(act?act.tagName+'.'+String(act.className||'').slice(0,24):'-')+' @'+location.pathname);
  }
 },250);
 try{btn.click();}catch(e){}
}
function attempt(tries){
 tries=tries||0;
 if(!host()){clearFlag();post('new-note:fail:not-icloud:'+location.hostname);return;}
 if(!onNotes()){
  try{sessionStorage.setItem(FLAG,'1');}catch(e){}
  location.href='https://www.icloud.com/notes';
  return;
 }
 var btn=findCompose();
 if(!btn){
  if(tries>=60){
   clearFlag();
   // Nothing matched the wording: log what the toolbar actually offers, so the
   // selector can be fixed from panel.log instead of by guessing.
   var list=docs(),seen=[];
   for(var d=0;d<list.length&&seen.length<10;d++){
    var all;
    try{all=list[d].querySelectorAll('button,[role=button],a,[aria-label],[title]');}catch(e){continue;}
    for(var i=0;i<all.length&&seen.length<10;i++){
     if(!vis(all[i])){continue;}
     var l=label(all[i]).replace(/\s+/g,' ').trim().slice(0,32);
     if(l){seen.push(l);}
    }
   }
   post('new-note:fail:no-button:docs='+list.length+':'+seen.join('|')+' @'+location.pathname);
   return;
  }
  setTimeout(function(){attempt(tries+1);},300);
  return;
 }
 attemptOpen(btn);
}
window.icNewNote=function(){attempt(0);return 'started';};
try{if(sessionStorage.getItem(FLAG)==='1'){attempt(0);}}catch(e){}
})();";
        }

        // ------------------------------------------------------- dashboard ---
        private void StartWidgets()
        {
            ResolveWindowsPlaceName();
            StartWeather();
            StartOutlook();
        }

        private string DashJson()
        {
            if (string.IsNullOrEmpty(dashJson))
            {
                return "{\"order\":[],\"hidden\":[],\"spans\":{}}";
            }
            return dashJson;
        }

        private void PushDashConfig()
        {
            try
            {
                if (web == null || web.CoreWebView2 == null) { return; }
                web.CoreWebView2.PostWebMessageAsJson(
                    "{\"ic\":\"dash-config\",\"state\":" + DashJson() + "}");
            }
            catch (Exception ex) { Log("dash config push failed: " + ex.Message); }
        }

        // Host -> page as JSON, not as a script string: mail subjects and event
        // titles are arbitrary text and must never be pasted into JavaScript.
        private void PushWidget(string kind, string json)
        {
            if (string.IsNullOrEmpty(json)) { return; }
            try
            {
                if (web == null || web.CoreWebView2 == null) { return; }
                web.CoreWebView2.PostWebMessageAsJson(
                    "{\"ic\":\"widget\",\"kind\":\"" + kind + "\",\"data\":" + json + "}");
                Log("widget pushed: " + kind + " " + json.Length + " bytes");
            }
            catch (Exception ex) { Log("widget push failed (" + kind + "): " + ex.Message); }
        }

        // The greeting name can change while the page is open, so prefs are also
        // pushable rather than only baked into the injected script.
        private void PushPrefs()
        {
            try
            {
                if (web == null || web.CoreWebView2 == null) { return; }
                web.CoreWebView2.PostWebMessageAsJson("{\"ic\":\"prefs\",\"prefs\":" + PrefsJson() + "}");
            }
            catch (Exception ex) { Log("prefs push failed: " + ex.Message); }
        }

        private void PostToPage(MethodInvoker action)
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) { return; }
                BeginInvoke(action);
            }
            catch (Exception ex) { Log("post to page failed: " + ex.Message); }
        }

        // A navigation clears whatever the page held, and its message listener is
        // not registered the instant NavigationCompleted fires, so let it settle
        // before pushing the current dashboard state back in.
        private void ScheduleDashPush()
        {
            Timer once = new Timer();
            once.Interval = 1800;
            once.Tick += delegate(object s, EventArgs a)
            {
                once.Stop();
                once.Dispose();
                PushDashConfig();
                PushPrefs();
                if (weatherData.Length > 0) { PushWidget("weather", weatherData); }
                if (outlookData.Length > 0) { PushWidget("outlook", outlookData); }
            };
            once.Start();
        }

        // config.json is flat except for the dashboard and weather blocks, which
        // are nested objects and so need brace matching rather than a regex.
        private static string ExtractObject(string text, string key)
        {
            if (string.IsNullOrEmpty(text)) { return ""; }
            Match m = Regex.Match(text, "\"" + key + "\"\\s*:\\s*\\{");
            if (!m.Success) { return ""; }
            int start = text.IndexOf('{', m.Index + key.Length);
            if (start < 0) { return ""; }
            int depth = 0;
            bool inString = false;
            for (int i = start; i < text.Length; i++)
            {
                char c = text[i];
                if (inString)
                {
                    if (c == '\\') { i++; }
                    else if (c == '"') { inString = false; }
                    continue;
                }
                if (c == '"') { inString = true; }
                else if (c == '{') { depth++; }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0) { return text.Substring(start, i - start + 1); }
                }
            }
            return "";
        }

        private static string ExtractArray(string text, string key)
        {
            if (string.IsNullOrEmpty(text)) { return ""; }
            Match m = Regex.Match(text, "\"" + key + "\"\\s*:\\s*\\[");
            if (!m.Success) { return ""; }
            int start = text.IndexOf('[', m.Index + key.Length);
            if (start < 0) { return ""; }
            int depth = 0;
            bool inString = false;
            for (int i = start; i < text.Length; i++)
            {
                char c = text[i];
                if (inString)
                {
                    if (c == '\\') { i++; }
                    else if (c == '"') { inString = false; }
                    continue;
                }
                if (c == '"') { inString = true; }
                else if (c == '[') { depth++; }
                else if (c == ']')
                {
                    depth--;
                    if (depth == 0) { return text.Substring(start, i - start + 1); }
                }
            }
            return "";
        }

        private static double ReadNumber(string text, string key, double fallback)
        {
            Match m = Regex.Match(text, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(-?[0-9]+(?:\\.[0-9]+)?)");
            double value;
            if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out value))
            {
                return value;
            }
            return fallback;
        }

        private static List<string> JsonArray(string json, string key)
        {
            List<string> items = new List<string>();
            Match m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\\[([^\\]]*)\\]");
            if (!m.Success) { return items; }
            foreach (string raw in m.Groups[1].Value.Split(','))
            {
                string value = raw.Trim().Trim('"');
                if (value.Length > 0) { items.Add(value); }
            }
            return items;
        }

        private static string HttpGet(string url)
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; }
            catch { }
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = "iCloudPanel/1.0";
            request.Timeout = 12000;
            request.ReadWriteTimeout = 12000;
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (Stream stream = response.GetResponseStream())
            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }

        // ------------------------------------------------------- weather ---
        // Open-Meteo: keyless, no account, and fetched here rather than in the page
        // so iCloud's CSP never comes into it.
        private void StartWeather()
        {
            if (double.IsNaN(weatherLat) || double.IsNaN(weatherLon))
            {
                // No place configured: ask Windows once before giving up, so the
                // tiles follow the PC's own location setting rather than a guess.
                if (!winLocTried) { TryWindowsLocation(); }
                PushWidget("weather", "{\"ok\":false,\"error\":\"no-location\"}");
                return;
            }
            // Started once: setting the location after launch must not leave the
            // widget stuck on its first reading.
            if (weatherTimer == null)
            {
                weatherTimer = new Timer();
                weatherTimer.Interval = 15 * 60 * 1000;
                weatherTimer.Tick += delegate { FetchWeather(); };
                weatherTimer.Start();
            }
            FetchWeather();
        }

        private void FetchWeather()
        {
            if (double.IsNaN(weatherLat) || double.IsNaN(weatherLon)) { return; }
            LoadPrefs();
            string url = "https://api.open-meteo.com/v1/forecast?latitude=" +
                weatherLat.ToString("0.####", CultureInfo.InvariantCulture) +
                "&longitude=" + weatherLon.ToString("0.####", CultureInfo.InvariantCulture) +
                "&current=temperature_2m,weather_code" +
                "&daily=weather_code,temperature_2m_max,temperature_2m_min" +
                (prefsFahrenheit ? "&temperature_unit=fahrenheit" : "") +
                "&timezone=auto&forecast_days=5";
            string place = weatherPlace;
            Task.Run(delegate
            {
                string json;
                try
                {
                    json = ParseWeather(HttpGet(url), place);
                }
                catch (Exception ex)
                {
                    json = "{\"ok\":false,\"error\":" + JsString(ex.Message) + "}";
                }
                weatherData = json;
                PostToPage(delegate { PushWidget("weather", json); });
                Log("weather: " + (json.Length > 160 ? json.Substring(0, 160) : json));
            });
        }

        private static string ParseWeather(string json, string place)
        {
            double temp = ReadNumber(json, "temperature_2m", double.NaN);
            double code = ReadNumber(json, "weather_code", -1);
            List<string> times = JsonArray(json, "time");
            List<string> maxs = JsonArray(json, "temperature_2m_max");
            List<string> mins = JsonArray(json, "temperature_2m_min");
            List<string> codes = JsonArray(json, "weather_code");
            LoadPrefs();
            StringBuilder sb = new StringBuilder();
            sb.Append("{\"ok\":true,\"place\":").Append(JsString(place ?? ""));
            sb.Append(",\"unit\":\"").Append(prefsFahrenheit ? "F" : "C");
            sb.Append("\",\"temp\":");
            sb.Append(double.IsNaN(temp) ? "null" : temp.ToString("0.#", CultureInfo.InvariantCulture));
            sb.Append(",\"code\":").Append(code < 0 ? 0 : (int)code).Append(",\"days\":[");
            for (int i = 0; i < times.Count; i++)
            {
                if (i > 0) { sb.Append(','); }
                string day = times[i];
                DateTime parsed;
                if (DateTime.TryParse(times[i], CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
                {
                    day = parsed.ToString("ddd");
                }
                sb.Append("{\"day\":").Append(JsString(day));
                sb.Append(",\"max\":").Append(i < maxs.Count ? maxs[i] : "null");
                sb.Append(",\"min\":").Append(i < mins.Count ? mins[i] : "null");
                sb.Append(",\"code\":").Append(i < codes.Count ? codes[i] : "0").Append('}');
            }
            sb.Append("]}");
            return sb.ToString();
        }

        // One-time lookup instead of guessing from the IP, so the widget never
        // starts reporting a different town than the one asked for.
        private void PickLocation()
        {
            BeginInvoke((MethodInvoker)delegate
            {
                try
                {
                    using (Form dlg = new Form())
                    {
                        dlg.Text = "Weather location";
                        dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                        dlg.StartPosition = FormStartPosition.CenterParent;
                        dlg.MinimizeBox = false;
                        dlg.MaximizeBox = false;
                        dlg.ClientSize = new Size(300, 96);
                        Label label = new Label();
                        label.Text = "City name (for example Belfast)";
                        label.SetBounds(12, 10, 276, 18);
                        TextBox input = new TextBox();
                        input.SetBounds(12, 30, 276, 24);
                        input.Text = weatherPlace ?? "";
                        Button ok = new Button();
                        ok.Text = "OK";
                        ok.DialogResult = DialogResult.OK;
                        ok.SetBounds(150, 62, 66, 24);
                        Button cancel = new Button();
                        cancel.Text = "Cancel";
                        cancel.DialogResult = DialogResult.Cancel;
                        cancel.SetBounds(222, 62, 66, 24);
                        dlg.Controls.Add(label);
                        dlg.Controls.Add(input);
                        dlg.Controls.Add(ok);
                        dlg.Controls.Add(cancel);
                        dlg.AcceptButton = ok;
                        dlg.CancelButton = cancel;
                        if (dlg.ShowDialog(this) != DialogResult.OK) { return; }
                        ResolvePlace(input.Text.Trim());
                    }
                }
                catch (Exception ex) { Log("weather location dialog failed: " + ex.Message); }
            });
        }

        private void PickName()
        {
            BeginInvoke((MethodInvoker)delegate
            {
                try
                {
                    using (Form dlg = new Form())
                    {
                        dlg.Text = "Your name";
                        dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                        dlg.StartPosition = FormStartPosition.CenterParent;
                        dlg.MinimizeBox = false;
                        dlg.MaximizeBox = false;
                        dlg.ClientSize = new Size(300, 96);
                        Label label = new Label();
                        label.Text = "Name shown in the greeting (leave empty for none)";
                        label.SetBounds(12, 10, 276, 18);
                        TextBox input = new TextBox();
                        input.SetBounds(12, 30, 276, 24);
                        input.Text = userName ?? "";
                        Button ok = new Button();
                        ok.Text = "OK";
                        ok.DialogResult = DialogResult.OK;
                        ok.SetBounds(150, 62, 66, 24);
                        Button cancel = new Button();
                        cancel.Text = "Cancel";
                        cancel.DialogResult = DialogResult.Cancel;
                        cancel.SetBounds(222, 62, 66, 24);
                        dlg.Controls.Add(label);
                        dlg.Controls.Add(input);
                        dlg.Controls.Add(ok);
                        dlg.Controls.Add(cancel);
                        dlg.AcceptButton = ok;
                        dlg.CancelButton = cancel;
                        if (dlg.ShowDialog(this) != DialogResult.OK) { return; }
                        userName = input.Text.Trim();
                        nameLoaded = true;
                        SaveConfig(ClientOnScreen());
                        PushPrefs();
                        Log("name: " + (userName.Length > 0 ? userName : "(none)"));
                    }
                }
                catch (Exception ex) { Log("name dialog failed: " + ex.Message); }
            });
        }

        // Manual birthdays live in config as name/month/day. The account list
        // only chooses whose calendar we read for birthday events; credentials
        // stay in mailfeed.ps1 and are never touched here.
        private void PickBirthdays()
        {
            BeginInvoke((MethodInvoker)delegate
            {
                try
                {
                    List<string[]> items = new List<string[]>();
                    foreach (Match m in Regex.Matches(birthdaysJson,
                        @"\{\s*""name""\s*:\s*""([^""]*)""\s*,\s*""month""\s*:\s*(\d{1,2})\s*,\s*""day""\s*:\s*(\d{1,2})\s*\}"))
                    {
                        int mo, dy;
                        if (int.TryParse(m.Groups[2].Value, out mo) && int.TryParse(m.Groups[3].Value, out dy) &&
                            mo >= 1 && mo <= 12 && dy >= 1 && dy <= 31)
                        {
                            items.Add(new string[] { m.Groups[1].Value, mo.ToString(CultureInfo.InvariantCulture),
                                dy.ToString(CultureInfo.InvariantCulture) });
                        }
                    }
                    List<string> accounts = new List<string>();
                    foreach (string a in JsonArray(outlookData ?? "", "accounts"))
                    {
                        if (!string.IsNullOrEmpty(a) && !accounts.Contains(a)) { accounts.Add(a); }
                    }
                    using (Form dlg = new Form())
                    {
                        dlg.Text = "Birthdays";
                        dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                        dlg.StartPosition = FormStartPosition.CenterParent;
                        dlg.MinimizeBox = false;
                        dlg.MaximizeBox = false;
                        dlg.ClientSize = new Size(452, 284);
                        Label aLabel = new Label();
                        aLabel.Text = "Birthday accounts";
                        aLabel.SetBounds(12, 10, 180, 18);
                        CheckedListBox acct = new CheckedListBox();
                        acct.SetBounds(12, 30, 186, 150);
                        acct.CheckOnClick = true;
                        if (accounts.Count == 0)
                        {
                            acct.Items.Add("(no accounts yet)");
                            acct.Enabled = false;
                        }
                        foreach (string a in accounts)
                        {
                            bool on = birthdayAccountsJson.IndexOf("\"" + a + "\"", StringComparison.Ordinal) >= 0;
                            acct.Items.Add(a, on);
                        }
                        Label note = new Label();
                        note.Text = "Checked accounts may add birthdays from today's calendar events.";
                        note.SetBounds(12, 188, 186, 52);
                        Label bLabel = new Label();
                        bLabel.Text = "Birthdays";
                        bLabel.SetBounds(212, 10, 228, 18);
                        ListBox list = new ListBox();
                        list.SetBounds(212, 30, 228, 110);
                        Label hint = new Label();
                        hint.Text = "Add a name, month and day:";
                        hint.SetBounds(212, 142, 228, 16);
                        TextBox name = new TextBox();
                        name.SetBounds(212, 160, 116, 24);
                        NumericUpDown month = new NumericUpDown();
                        month.SetBounds(334, 160, 44, 24);
                        month.Minimum = 1;
                        month.Maximum = 12;
                        month.Value = 1;
                        NumericUpDown day = new NumericUpDown();
                        day.SetBounds(384, 160, 56, 24);
                        day.Minimum = 1;
                        day.Maximum = 31;
                        day.Value = 1;
                        Button add = new Button();
                        add.Text = "Add";
                        add.SetBounds(212, 192, 110, 26);
                        Button rem = new Button();
                        rem.Text = "Remove";
                        rem.SetBounds(330, 192, 110, 26);
                        Button ok = new Button();
                        ok.Text = "OK";
                        ok.DialogResult = DialogResult.OK;
                        ok.SetBounds(266, 240, 84, 28);
                        Button cancel = new Button();
                        cancel.Text = "Cancel";
                        cancel.DialogResult = DialogResult.Cancel;
                        cancel.SetBounds(356, 240, 84, 28);
                        Action refresh = delegate
                        {
                            list.BeginUpdate();
                            list.Items.Clear();
                            foreach (string[] it in items)
                            {
                                int mo = int.Parse(it[1], CultureInfo.InvariantCulture);
                                string mn = new DateTime(2000, mo, 1).ToString("MMM", CultureInfo.CurrentCulture);
                                list.Items.Add(it[0] + " - " + mn + " " + it[2]);
                            }
                            list.EndUpdate();
                        };
                        add.Click += delegate
                        {
                            string nm = name.Text.Trim();
                            if (nm.Length == 0) { name.Focus(); return; }
                            items.Add(new string[] { nm,
                                ((int)month.Value).ToString(CultureInfo.InvariantCulture),
                                ((int)day.Value).ToString(CultureInfo.InvariantCulture) });
                            name.Clear();
                            refresh();
                        };
                        rem.Click += delegate
                        {
                            int at = list.SelectedIndex;
                            if (at >= 0 && at < items.Count) { items.RemoveAt(at); refresh(); }
                        };
                        dlg.Controls.Add(aLabel);
                        dlg.Controls.Add(acct);
                        dlg.Controls.Add(note);
                        dlg.Controls.Add(bLabel);
                        dlg.Controls.Add(list);
                        dlg.Controls.Add(hint);
                        dlg.Controls.Add(name);
                        dlg.Controls.Add(month);
                        dlg.Controls.Add(day);
                        dlg.Controls.Add(add);
                        dlg.Controls.Add(rem);
                        dlg.Controls.Add(ok);
                        dlg.Controls.Add(cancel);
                        dlg.AcceptButton = ok;
                        dlg.CancelButton = cancel;
                        refresh();
                        if (dlg.ShowDialog(this) != DialogResult.OK) { return; }
                        StringBuilder bd = new StringBuilder("[");
                        for (int i = 0; i < items.Count; i++)
                        {
                            if (i > 0) { bd.Append(','); }
                            bd.Append("{\"name\":").Append(JsString(items[i][0]))
                              .Append(",\"month\":").Append(items[i][1])
                              .Append(",\"day\":").Append(items[i][2]).Append('}');
                        }
                        bd.Append(']');
                        birthdaysJson = bd.ToString();
                        StringBuilder ba = new StringBuilder("[");
                        bool first = true;
                        foreach (object o in acct.CheckedItems)
                        {
                            string a = Convert.ToString(o);
                            if (string.IsNullOrEmpty(a) || a.StartsWith("(", StringComparison.Ordinal)) { continue; }
                            if (!first) { ba.Append(','); }
                            first = false;
                            ba.Append(JsString(a));
                        }
                        ba.Append(']');
                        birthdayAccountsJson = ba.ToString();
                        SaveConfig(ClientOnScreen());
                        PushPrefs();
                        Log("birthdays: " + items.Count + " manual, " +
                            (first ? "0" : "some") + " accounts");
                    }
                }
                catch (Exception ex) { Log("birthday dialog failed: " + ex.Message); }
            });
        }

        private void ResolvePlace(string query)
        {
            if (string.IsNullOrEmpty(query)) { return; }
            string url = "https://geocoding-api.open-meteo.com/v1/search?count=1&language=en&format=json&name=" +
                         Uri.EscapeDataString(query);
            Task.Run(delegate
            {
                string json;
                try { json = HttpGet(url); }
                catch (Exception ex)
                {
                    Log("weather geocode failed: " + ex.Message);
                    PostToPage(delegate { PushWidget("weather", "{\"ok\":false,\"error\":\"geocode\"}"); });
                    return;
                }
                double lat = ReadNumber(json, "latitude", double.NaN);
                double lon = ReadNumber(json, "longitude", double.NaN);
                Match name = Regex.Match(json, "\"name\"\\s*:\\s*\"([^\"]*)\"");
                Match country = Regex.Match(json, "\"country\"\\s*:\\s*\"([^\"]*)\"");
                if (double.IsNaN(lat) || double.IsNaN(lon))
                {
                    PostToPage(delegate { PushWidget("weather", "{\"ok\":false,\"error\":\"not-found\"}"); });
                    return;
                }
                string place = name.Success ? name.Groups[1].Value : query;
                if (country.Success && country.Groups[1].Value.Length > 0)
                {
                    place = place + ", " + country.Groups[1].Value;
                }
                weatherLat = lat;
                weatherLon = lon;
                weatherPlace = place;
                Log("weather location: " + place + " " + lat.ToString("0.###", CultureInfo.InvariantCulture) +
                    "," + lon.ToString("0.###", CultureInfo.InvariantCulture));
                PostToPage(delegate
                {
                    SaveConfig(ClientOnScreen());
                    StartWeather();
                });
            });
        }

        // Coordinates on their own make a poor label. This keyless endpoint turns
        // them into a town, so a Windows fix reads the place name on the card.
        private static string ReversePlace(double lat, double lon)
        {
            try
            {
                string url = "https://api.bigdatacloud.net/data/reverse-geocode-client?latitude=" +
                    lat.ToString("0.####", CultureInfo.InvariantCulture) + "&longitude=" +
                    lon.ToString("0.####", CultureInfo.InvariantCulture) + "&localityLanguage=en";
                string json = HttpGet(url);
                string city = ReadString(json, "city");
                if (city.Length == 0) { city = ReadString(json, "locality"); }
                string region = ReadString(json, "principalSubdivision");
                string country = ReadString(json, "countryName");
                if (country == "United States of America") { country = "United States"; }
                if (city.Length == 0) { return region; }
                if (region.Length > 0) { return city + ", " + region; }
                if (country.Length > 0) { return city + ", " + country; }
                return city;
            }
            catch (Exception ex) { Log("reverse geocode failed: " + ex.Message); return ""; }
        }

        private static string ReadString(string text, string key)
        {
            Match m = Regex.Match(text, "\"" + key + "\"\\s*:\\s*\"([^\"]*)\"");
            return m.Success ? m.Groups[1].Value : "";
        }

        // Configs written before the lookup existed still say "Your location";
        // name the town once instead of leaving the placeholder forever.
        private void ResolveWindowsPlaceName()
        {
            if (double.IsNaN(weatherLat) || double.IsNaN(weatherLon)) { return; }
            string place = weatherPlace ?? "";
            if (place.Length > 0 && place != "Your location") { return; }
            double lat = weatherLat, lon = weatherLon;
            Task.Run(delegate
            {
                string name = ReversePlace(lat, lon);
                if (name.Length == 0) { return; }
                weatherPlace = name;
                Log("weather place: " + name);
                PostToPage(delegate
                {
                    SaveConfig(ClientOnScreen());
                    StartWeather();
                });
            });
        }

        // ------------------------------------------------------- outlook ---
        // Classic Outlook holds every configured account (Outlook.com and the Gmail
        // one) in its own store, so one COM read covers both mailboxes and both
        // calendars - no Graph app registration, no OAuth, no stored secret.
        private void StartOutlook()
        {
            FetchOutlook();
            outlookTimer = new Timer();
            outlookTimer.Interval = 5 * 60 * 1000;
            outlookTimer.Tick += delegate { FetchOutlook(); };
            outlookTimer.Start();
        }

        private void FetchOutlook()
        {
            // Configured URL wins: the Outlook work happens on a machine that has
            // Outlook, and this one only pays for a single HTTP GET.
            if (!string.IsNullOrEmpty(outlookUrl))
            {
                string url = outlookUrl;
                Task.Run(delegate
                {
                    string json;
                    try { json = HttpGet(url).Trim(); }
                    catch (Exception ex)
                    {
                        Log("outlook endpoint failed: " + ex.Message);
                        json = "{\"ok\":false,\"error\":\"unreachable\"}";
                    }
                    bool reachable = json.IndexOf("\"ok\":true", StringComparison.Ordinal) >= 0;
                    if (reachable) { outlookData = json; }
                    Log("outlook endpoint: " + (reachable ? "ok " : "failed ") + json.Length + " bytes");
                    PostToPage(delegate { PushWidget("outlook", json); });
                });
                return;
            }
            // mailfeed.ps1 (cloud: Graph + Gmail) wins when present; the classic
            // Outlook COM helper stays as the fallback so nothing breaks on a
            // machine that still has Outlook.
            string script = Path.Combine(Path.GetFullPath(Path.Combine(appDir, "..")), "mailfeed.ps1");
            if (!File.Exists(script))
            {
                script = Path.Combine(Path.GetFullPath(Path.Combine(appDir, "..")), "outlook-widgets.ps1");
            }
            if (!File.Exists(script))
            {
                PostToPage(delegate { PushWidget("outlook", "{\"ok\":false,\"error\":\"helper-missing\"}"); });
                return;
            }
            Task.Run(delegate
            {
                string json = RunOutlookHelper(script);
                bool good = json.IndexOf("\"ok\":true", StringComparison.Ordinal) >= 0;
                if (good) { outlookData = json; }
                Log("outlook helper: " + (good ? "ok " : "failed ") + json.Length + " bytes");
                PostToPage(delegate { PushWidget("outlook", json); });
            });
        }

        // A child process, not COM in this one: a hung or missing Outlook can then
        // never freeze the panel, and a failure just leaves the last data visible.
        private static string RunOutlookHelper(string script)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = "powershell.exe";
                psi.Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + script + "\"";
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;
                using (Process p = Process.Start(psi))
                {
                    if (p == null) { return "{\"ok\":false,\"error\":\"no-process\"}"; }
                    string output = p.StandardOutput.ReadToEnd();
                    string error = p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(25000))
                    {
                        try { p.Kill(); } catch { }
                        return "{\"ok\":false,\"error\":\"timeout\"}";
                    }
                    if (error.Length > 0) { Log("outlook helper stderr: " + error.Substring(0, Math.Min(300, error.Length))); }
                    output = output.Trim();
                    int brace = output.IndexOf('{');
                    if (brace < 0) { return "{\"ok\":false,\"error\":\"empty\"}"; }
                    return output.Substring(brace);
                }
            }
            catch (Exception ex)
            {
                return "{\"ok\":false,\"error\":" + JsString(ex.Message) + "}";
            }
        }

        // Same child-process shape as the mail helper: the panel must never hang
        // on a permission prompt or a slow location fix, so read both pipes while
        // the child runs and kill it if it outstays the timeout.
        private static string RunHelper(string script, int timeoutMs, string tag)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = "powershell.exe";
                psi.Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + script + "\"";
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;
                using (Process p = Process.Start(psi))
                {
                    if (p == null) { return "{\"ok\":false,\"error\":\"no-process\"}"; }
                    Task<string> outTask = p.StandardOutput.ReadToEndAsync();
                    Task<string> errTask = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { }
                        return "{\"ok\":false,\"error\":\"timeout\"}";
                    }
                    string output = outTask.Result.Trim();
                    string error = errTask.Result.Trim();
                    if (error.Length > 0) { Log(tag + " stderr: " + error.Substring(0, Math.Min(300, error.Length))); }
                    int brace = output.IndexOf('{');
                    if (brace < 0) { return "{\"ok\":false,\"error\":\"empty\"}"; }
                    return output.Substring(brace);
                }
            }
            catch (Exception ex)
            {
                return "{\"ok\":false,\"error\":" + JsString(ex.Message) + "}";
            }
        }

        // Windows knows where the PC is (Settings > Privacy > Location); this is
        // the same service the Weather app uses, just asked once and cached.
        private void TryWindowsLocation()
        {
            if (winLocTried) { return; }
            winLocTried = true;
            string script = Path.Combine(Path.GetFullPath(Path.Combine(appDir, "..")), "windows-location.ps1");
            if (!File.Exists(script)) { Log("windows location: helper missing"); return; }
            Task.Run(delegate
            {
                string json = RunHelper(script, 12000, "windows location");
                bool ok = json.IndexOf("\"ok\":true", StringComparison.Ordinal) >= 0;
                double lat = ReadNumber(json, "lat", double.NaN);
                double lon = ReadNumber(json, "lon", double.NaN);
                Log("windows location: " + (ok && !double.IsNaN(lat) ? "ok " : "failed ") + json);
                if (!ok || double.IsNaN(lat) || double.IsNaN(lon))
                {
                    PostToPage(delegate { PushWidget("weather", "{\"ok\":false,\"error\":\"no-location\"}"); });
                    return;
                }
                weatherLat = lat;
                weatherLon = lon;
                // Windows hands back coordinates, not a name: look the town up so
                // the card says the place rather than a generic label.
                string place = ReversePlace(lat, lon);
                weatherPlace = place.Length > 0 ? place : "Your location";
                PostToPage(delegate
                {
                    SaveConfig(ClientOnScreen());
                    StartWeather();
                });
            });
        }

        private static string JsString(string value)
        {
            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        private void ApplyZoom()
        {
            try
            {
                if (web == null || web.CoreWebView2 == null) { return; }
                double clamped = Math.Min(5.0, Math.Max(0.25, zoom));
                if (Math.Abs(web.ZoomFactor - clamped) > 0.001)
                {
                    web.ZoomFactor = clamped;
                }
                SyncChromeScale(clamped);
                Log("zoom applied: " + clamped.ToString("0.##"));
            }
            catch (Exception ex)
            {
                Log("zoom error: " + ex.Message);
            }
        }

        // The corner buttons and fly-out are laid out in CSS pixels, so the page
        // zoom shrinks them along with iCloud's content. Hand the injected UI the
        // inverse (1/zoom) and it keeps the same physical size at every level.
        private void SyncChromeScale(double factor)
        {
            try
            {
                if (web == null || web.CoreWebView2 == null) { return; }
                if (factor <= 0) { factor = 1.0; }
                string inv = (1.0 / factor).ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
                web.CoreWebView2.ExecuteScriptAsync(
                    "window.icSetChromeScale&&window.icSetChromeScale(" + inv + ");");
            }
            catch { }
        }

        private void WatchZoom(object sender, EventArgs args)
        {
            try
            {
                if (web == null || web.CoreWebView2 == null) { return; }
                double current = web.ZoomFactor;
                if (Math.Abs(current - zoom) > 0.004)
                {
                    zoom = current;
                    SyncChromeScale(current);
                    SaveConfig(ClientOnScreen());
                    Log("zoom saved: " + current.ToString("0.##"));
                }
            }
            catch { }
        }

        private static double ReadDouble(string text, string key, double fallback)
        {
            // The minus matters: every western longitude is negative, and losing
            // it turned a saved location back into "ask Windows again".
            Match m = Regex.Match(text, "\"" + key + "\"\\s*:\\s*(-?[0-9]+(?:\\.[0-9]+)?)");
            double value;
            if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out value))
            {
                return value;
            }
            return fallback;
        }

        private void ScheduleCookieSync()
        {
            Timer once = new Timer();
            once.Interval = 3000;
            once.Tick += delegate(object s, EventArgs a)
            {
                once.Stop();
                once.Dispose();
                SyncCookiesNow(null, EventArgs.Empty);
            };
            once.Start();
        }

        private async void SyncCookiesNow(object sender, EventArgs args)
        {
            await SyncSessionCookies("auto");
        }

        // WebView2 does not reliably write Apple's cookies into the profile's
        // cookie database, so the sign-in never survived. Instead of trusting
        // Chromium's flush, every Apple cookie is copied to our own store while
        // the panel runs (DPAPI-encrypted, current user only) and re-added before
        // the first navigation of the next session.
        private async Task SyncSessionCookies(string reason)
        {
            try
            {
                CoreWebView2 core = (web == null) ? null : web.CoreWebView2;
                if (core == null) { return; }
                List<CoreWebView2Cookie> cookies = await GatherAppleCookies(core.CookieManager);
                if (cookies.Count == 0) { return; }

                StringBuilder text = new StringBuilder();
                text.Append("v1\n");
                int saved = 0;
                foreach (CoreWebView2Cookie cookie in cookies)
                {
                    text.Append(cookie.Name).Append('\t')
                        .Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(cookie.Value ?? string.Empty))).Append('\t')
                        .Append(cookie.Domain).Append('\t')
                        .Append(cookie.Path).Append('\t')
                        .Append(cookie.IsSecure ? "1" : "0").Append('\t')
                        .Append(cookie.IsHttpOnly ? "1" : "0").Append('\t')
                        .Append((int)cookie.SameSite).Append('\t')
                        .Append(cookie.IsSession ? "-1" : cookie.Expires.Ticks.ToString()).Append('\n');
                    saved++;
                }

                byte[] plain = Encoding.UTF8.GetBytes(text.ToString());
                byte[] sealedBytes = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
                File.WriteAllBytes(cookieFile, sealedBytes);
                Log("cookie store(" + reason + "): " + saved + " Apple cookies saved");
            }
            catch (Exception ex)
            {
                Log("cookie store error: " + ex.Message);
            }
        }

        private static async Task<List<CoreWebView2Cookie>> GatherAppleCookies(CoreWebView2CookieManager manager)
        {
            string[] urls = new string[]
            {
                "https://www.icloud.com/",
                "https://idmsa.apple.com/",
                "https://appleid.apple.com/",
                "https://www.apple.com/"
            };
            List<CoreWebView2Cookie> found = new List<CoreWebView2Cookie>();
            HashSet<string> seen = new HashSet<string>();
            foreach (string url in urls)
            {
                List<CoreWebView2Cookie> list = await manager.GetCookiesAsync(url);
                foreach (CoreWebView2Cookie cookie in list)
                {
                    string domain = cookie.Domain ?? string.Empty;
                    if (domain.IndexOf("apple", StringComparison.OrdinalIgnoreCase) < 0 &&
                        domain.IndexOf("icloud", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }
                    string key = cookie.Name + "|" + domain + "|" + cookie.Path;
                    if (!seen.Add(key)) { continue; }
                    found.Add(cookie);
                }
            }
            return found;
        }

        private async Task RestoreCookiesAsync()
        {
            try
            {
                // The body below is synchronous; the yield keeps the compiler
                // happy (Add-Type promotes CS1998 to an error).
                await Task.Yield();
                CoreWebView2 core = (web == null) ? null : web.CoreWebView2;
                if (core == null || !File.Exists(cookieFile)) { return; }
                byte[] sealedBytes = File.ReadAllBytes(cookieFile);
                byte[] plain = ProtectedData.Unprotect(sealedBytes, null, DataProtectionScope.CurrentUser);
                string[] lines = Encoding.UTF8.GetString(plain).Split('\n');

                CoreWebView2CookieManager manager = core.CookieManager;
                int restored = 0;
                foreach (string rawLine in lines)
                {
                    string line = rawLine.TrimEnd('\r');
                    if (line.Length == 0 || line == "v1") { continue; }
                    string[] parts = line.Split('\t');
                    if (parts.Length < 8) { continue; }

                    string value = Encoding.UTF8.GetString(Convert.FromBase64String(parts[1]));
                    CoreWebView2Cookie cookie = manager.CreateCookie(parts[0], value, parts[2], parts[3]);
                    cookie.IsSecure = parts[4] == "1";
                    cookie.IsHttpOnly = parts[5] == "1";
                    int sameSite;
                    if (int.TryParse(parts[6], out sameSite))
                    {
                        cookie.SameSite = (CoreWebView2CookieSameSiteKind)sameSite;
                    }
                    long ticks;
                    if (long.TryParse(parts[7], out ticks) && ticks > 0)
                    {
                        cookie.Expires = new DateTime(ticks);
                    }
                    manager.AddOrUpdateCookie(cookie);
                    restored++;
                }
                Log("cookie restore: " + restored + " Apple cookies re-added");
            }
            catch (Exception ex)
            {
                Log("cookie restore error: " + ex.Message);
            }
        }

        // Names, domains and expiry only - never values.
        private async void LogCookieInventory(string reason)
        {
            try
            {
                CoreWebView2 core = (web == null) ? null : web.CoreWebView2;
                if (core == null) { return; }
                List<CoreWebView2Cookie> cookies = await GatherAppleCookies(core.CookieManager);
                StringBuilder text = new StringBuilder();
                text.Append("cookie inventory(").Append(reason).Append("): ").Append(cookies.Count).Append(" apple/icloud cookies");
                foreach (CoreWebView2Cookie cookie in cookies)
                {
                    string when = cookie.IsSession ? "session" : cookie.Expires.ToString("yyyy-MM-dd HH:mm");
                    text.Append(" | ").Append(cookie.Name).Append('@').Append(cookie.Domain).Append(' ').Append(when);
                }
                Log(text.ToString());
            }
            catch (Exception ex)
            {
                Log("cookie inventory error: " + ex.Message);
            }
        }

        // Two-phase self-test: create a session cookie for icloud.com (phase 1),
        // then report whether it is still there after a restart (phase 2).
        private async void CookieProbe(bool create, string flagPath)
        {
            try
            {
                CoreWebView2 core = (web == null) ? null : web.CoreWebView2;
                if (core == null) { return; }
                CoreWebView2CookieManager manager = core.CookieManager;

                if (create)
                {
                    CoreWebView2Cookie probe = manager.CreateCookie("ic_cookie_probe", "1", ".icloud.com", "/");
                    manager.AddOrUpdateCookie(probe);
                    Log("cookie probe: created a session cookie");
                    await SyncSessionCookies("probe");
                    List<CoreWebView2Cookie> after = await manager.GetCookiesAsync("https://www.icloud.com/");
                    foreach (CoreWebView2Cookie cookie in after)
                    {
                        if (cookie.Name == "ic_cookie_probe")
                        {
                            Log("cookie probe: isSession=" + cookie.IsSession +
                                " expires=" + cookie.Expires.ToString("yyyy-MM-dd"));
                        }
                    }
                    File.Delete(flagPath);
                    Log("cookie probe: phase 1 done, restart to check persistence");
                    return;
                }

                List<CoreWebView2Cookie> existing = await manager.GetCookiesAsync("https://www.icloud.com/");
                bool survived = false;
                foreach (CoreWebView2Cookie cookie in existing)
                {
                    if (cookie.Name == "ic_cookie_probe") { survived = true; }
                }
                if (survived)
                {
                    Log("cookie probe: SURVIVED restart, session cookie persisted");
                    manager.DeleteCookiesWithDomainAndPath("ic_cookie_probe", ".icloud.com", "/");
                    Log("cookie probe: cleaned up");
                }
            }
            catch (Exception ex)
            {
                Log("cookie probe error: " + ex.Message);
            }
        }

        private void ProbeOverlay()
        {
            try
            {
                CoreWebView2 core = (web == null) ? null : web.CoreWebView2;
                if (core == null) { Log("probe: no CoreWebView2"); return; }
                string script =
                    "(function(){" +
                    "var c=document.getElementById('ic-corner');" +
                    "var s=c?getComputedStyle(c).opacity:'MISSING';" +
                    "var h=document.querySelector('header');" +
                    "var hr=h?Math.round(h.getBoundingClientRect().height):-1;" +
                    "var g=document.getElementById('ic-gear');var gr=g?g.getBoundingClientRect():null;" +
                    "var grect=gr?(Math.round(gr.left)+','+Math.round(gr.top)+','+Math.round(gr.width)+'x'+Math.round(gr.height)):'MISSING';" +
                    "var rw=document.querySelectorAll('.ic-row');var rr=[];" +
                    "for(var i=0;i<rw.length;i++){var q=rw[i].getBoundingClientRect();" +
                    "rr.push(Math.round(q.left)+','+Math.round(q.top)+','+Math.round(q.width)+'x'+Math.round(q.height));}" +
                    "return 'corner op='+s+' lock='+!!document.getElementById('ic-lock')+" +
                    "' gear='+!!document.getElementById('ic-gear')+' info='+!!document.getElementById('ic-info')+" +
                    "' strip='+(function(){var z=document.getElementById('ic-info-strip');" +
                    "return z?getComputedStyle(z).display:'-';})()+' headerH='+hr+" +
                    "' topmost='+document.documentElement.getAttribute('data-ic-topmost')+" +
                    "' pinned='+document.documentElement.getAttribute('data-ic-pinned')+" +
                    "' autohide='+document.documentElement.getAttribute('data-ic-autohide')+" +
                    "' gearRect='+grect+' rows='+rr.join('|')+" +
                    "' scroll='+getComputedStyle(document.documentElement).overflow+" +
                    "' signin='+!!(document.body&&document.body.innerText.indexOf('Sign In')>=0)+" +
                    "' style='+!!document.getElementById('ic-bgstyle')+' rules='+(function(){var s=document.getElementById('ic-bgstyle');return s&&s.sheet?s.sheet.cssRules.length:'-'})()+" +
                    "' htm='+getComputedStyle(document.documentElement).backgroundColor+'|'+String(getComputedStyle(document.documentElement).backgroundImage).slice(0,32)+" +
                    "' body='+getComputedStyle(document.body).backgroundColor+'|'+String(getComputedStyle(document.body).backgroundImage).slice(0,24)+" +
                    "' bg='+(function(){var e=document.elementFromPoint(60,60);var o=[];" +
                    "for(var i=0;e&&i<6;i++){var cs=getComputedStyle(e);" +
                    "o.push(e.tagName+(e.id?'#'+e.id:'')+'.'+String(e.className||'').slice(0,20)+'|bi='+(cs.backgroundImage!=='none'?'Y':'n')+'|bc='+cs.backgroundColor+'|op='+cs.opacity);" +
                    "e=e.parentElement;}return o.join(' < ');})()+' wp='+(function(){var o=[];var es=document.querySelectorAll('[class*=\"gradient-background\"]');" +
                    "for(var i=0;i<es.length&&i<3;i++){var e=es[i],cs=getComputedStyle(e);" +
                    "var bf=getComputedStyle(e,'::before'),af=getComputedStyle(e,'::after');" +
                    "var r=e.getBoundingClientRect();" +
                    "o.push(String(e.className).slice(0,50)+'|bi='+(cs.backgroundImage!=='none'?'Y':'n')+'|bc='+cs.backgroundColor+" +
                    "'|bf='+(bf.backgroundImage!=='none'?'Y':'n')+bf.backgroundColor+'|af='+(af.backgroundImage!=='none'?'Y':'n')+af.backgroundColor+" +
                    "'|kids='+e.children.length+'|w='+Math.round(r.width));}" +
                    "return o.join(' ~ ');})()+' pop='+(function(){var p=document.getElementById('ic-settings');" +
                    "if(!p){return 'MISSING';}var r=p.getBoundingClientRect();" +
                    "return String(p.className||'none')+'@'+Math.round(r.left)+','+Math.round(r.top)+','+Math.round(r.width)+'x'+Math.round(r.height);})()+' '+location.pathname;})()";
                core.ExecuteScriptAsync(script).ContinueWith(delegate(Task<string> task)
                {
                    string result = task.IsFaulted ? ("failed " + task.Exception.Message) : (task.Result ?? "null");
                    Log("probe: " + result);
                });
                LogCookieInventory("probe");
            }
            catch (Exception ex)
            {
                Log("probe error: " + ex.Message);
            }
        }

        private void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            string message = null;
            try { message = args.TryGetWebMessageAsString(); }
            catch { }
            if (message == null || message.Length == 0) { return; }
            Log("page message: " + message);

            if (message == "toggle-lock") { ToggleLock(); return; }
            if (message == "lock-panel")
            {
                if (!locked) { Lock(); NotifyPage(); }
                return;
            }
            if (message == "toggle-topmost")
            {
                topMost = !topMost;
                if (topMost) { pinned = false; autoHide = false; }
                ApplyLayering();
                SaveConfig(ClientOnScreen());
                NotifyPage();
                return;
            }
            if (message == "toggle-pinned")
            {
                pinned = !pinned;
                if (pinned) { topMost = false; autoHide = false; }
                ApplyLayering();
                SaveConfig(ClientOnScreen());
                NotifyPage();
                return;
            }
            if (message == "toggle-autohide")
            {
                autoHide = !autoHide;
                if (autoHide) { pinned = false; topMost = false; }
                ApplyLayering();
                SaveConfig(ClientOnScreen());
                NotifyPage();
                return;
            }
            if (message == "toggle-info")
            {
                showInfo = !showInfo;
                SaveConfig(ClientOnScreen());
                NotifyPage();
                Log("account strip: " + (showInfo ? "shown" : "hidden"));
                return;
            }
            if (message.StartsWith("new-note:input:"))
            {
                RealClick(message.Substring(15));
                return;
            }
            if (message.StartsWith("new-note:"))
            {
                string outcome = message.Substring(9);
                if (outcome == "ok") { newNotePending = false; }
                bool verdict = outcome == "ok" || outcome.StartsWith("fail");
                Log((verdict ? "new-note result: " : "new-note: ") + outcome);
                return;
            }
            if (message.StartsWith("dash-state:"))
            {
                string raw = message.Substring(11).Trim();
                if (raw.Length > 1 && raw[0] == '{' && raw[raw.Length - 1] == '}')
                {
                    dashJson = raw;
                    SaveConfig(ClientOnScreen());
                    Log("dashboard state saved: " + raw.Length + " bytes");
                }
                return;
            }
            if (message == "apps-list") { RequestApps(); return; }
            if (message == "app-browse") { BrowseApps(); return; }
            if (message == "explorer-menu") { InstallExplorerPin(); return; }
            if (message.StartsWith("app-open:")) { OpenApp(message.Substring(9)); return; }
            if (message == "weather-locate") { PickLocation(); return; }
            if (message == "weather-windows") { winLocTried = false; TryWindowsLocation(); return; }
            if (message == "name-set") { PickName(); return; }
            if (message == "birthday-set") { PickBirthdays(); return; }
            if (message.StartsWith("bg-color:"))
            {
                bgKind = "color";
                bgColor = message.Substring(9);
                ApplyBackground();
                return;
            }
            if (message.StartsWith("bg-opacity:"))
            {
                double op;
                if (double.TryParse(message.Substring(11), NumberStyles.Float, CultureInfo.InvariantCulture, out op) &&
                    op > 0.15 && op <= 1.0)
                {
                    bgOpacity = op;
                }
                ApplyBackground();
                return;
            }
            if (message == "bg-wallpaper") { bgKind = "wallpaper"; wallKey = ""; ApplyBackground(); return; }
            if (message == "bg-nudge-up") { bgOffset = Math.Max(-300, bgOffset - 4); ApplyBackground(); return; }
            if (message == "bg-nudge-down") { bgOffset = Math.Min(300, bgOffset + 4); ApplyBackground(); return; }
            if (message == "bg-offset-reset") { bgOffset = 0; ApplyBackground(); return; }
            if (message == "bg-default") { bgKind = "default"; ApplyBackground(); return; }
            if (message == "bg-pick-color") { PickColor(); return; }
            if (message == "bg-pick-image") { PickImage(); return; }
        }

        // WebView2's own UI -- context menus, prompts, dialogs -- is themed by the
        // profile's colour scheme, and so is prefers-color-scheme for the page.
        // Auto nominally follows Windows, but the menu is known to keep a stale
        // scheme, so read the setting the user actually ticked and pin it.
        private void ApplySystemColorScheme()
        {
            try
            {
                if (web == null || web.CoreWebView2 == null) { return; }
                int light = 1;
                using (Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    "Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize"))
                {
                    if (key != null)
                    {
                        object value = key.GetValue("AppsUseLightTheme");
                        if (value != null) { light = Convert.ToInt32(value); }
                    }
                }
                web.CoreWebView2.Profile.PreferredColorScheme = (light == 0)
                    ? CoreWebView2PreferredColorScheme.Dark
                    : CoreWebView2PreferredColorScheme.Light;
                Log("color scheme: " + (light == 0 ? "dark" : "light"));
            }
            catch (Exception ex) { Log("color scheme failed: " + ex.Message); }
        }

        // ------------------------------------------------- apps launcher ---
        // The Start-menu list is produced by a child PowerShell process and cached
        // in panel\appcache.json, so opening the picker never blocks WebView2.
        private void RequestApps()
        {
            string script = Path.Combine(Path.GetFullPath(Path.Combine(appDir, "..")), "applist.ps1");
            if (!File.Exists(script)) { PushApps("[]"); return; }
            Task.Run(delegate
            {
                string json = RunJsonHelper(script, 60000, "apps");
                PostToPage(delegate { PushApps(json); });
            });
        }

        private void PushApps(string json)
        {
            try
            {
                if (web == null || web.CoreWebView2 == null) { return; }
                if (string.IsNullOrEmpty(json)) { json = "[]"; }
                web.CoreWebView2.PostWebMessageAsJson("{\"ic\":\"apps\",\"apps\":" + json + "}");
                Log("apps pushed: " + json.Length + " bytes");
            }
            catch (Exception ex) { Log("apps push failed: " + ex.Message); }
        }

        private void PushAppsAdd(string json)
        {
            try
            {
                if (web == null || web.CoreWebView2 == null) { return; }
                if (string.IsNullOrEmpty(json)) { return; }
                web.CoreWebView2.PostWebMessageAsJson("{\"ic\":\"apps-add\",\"apps\":" + json + "}");
                Log("apps add pushed: " + json.Length + " bytes");
            }
            catch (Exception ex) { Log("apps add failed: " + ex.Message); }
        }

        private static string RunJsonHelper(string script, int timeoutMs, string tag)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = "powershell.exe";
                psi.Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + script + "\"";
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;
                using (Process p = Process.Start(psi))
                {
                    if (p == null) { return "[]"; }
                    Task<string> outTask = p.StandardOutput.ReadToEndAsync();
                    Task<string> errTask = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { }
                        return "[]";
                    }
                    string output = outTask.Result.Trim();
                    string error = errTask.Result.Trim();
                    if (error.Length > 0) { Log(tag + " stderr: " + error.Substring(0, Math.Min(300, error.Length))); }
                    int at = output.IndexOfAny(new char[] { '[', '{' });
                    if (at < 0) { return "[]"; }
                    return output.Substring(at);
                }
            }
            catch (Exception ex)
            {
                Log(tag + " failed: " + ex.Message);
                return "[]";
            }
        }

        private void OpenApp(string encoded)
        {
            try
            {
                string key = Uri.UnescapeDataString(encoded);
                if (key.StartsWith("url:", StringComparison.OrdinalIgnoreCase))
                {
                    web.CoreWebView2.Navigate(key.Substring(4));
                    return;
                }
                if (key.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    web.CoreWebView2.Navigate(key);
                    return;
                }
                if (key.StartsWith("path:", StringComparison.OrdinalIgnoreCase))
                {
                    string path = key.Substring(5);
                    if (File.Exists(path) || Directory.Exists(path))
                    {
                        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
                        Log("launcher opened: " + path);
                        return;
                    }
                    Log("launcher missing path: " + path);
                    return;
                }
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = "shell:AppsFolder\\" + key,
                    UseShellExecute = true
                });
                Log("launcher opened app: " + key);
            }
            catch (Exception ex) { Log("launcher open failed: " + ex.Message); }
        }

        private void BrowseApps()
        {
            try
            {
                using (OpenFileDialog d = new OpenFileDialog())
                {
                    d.Title = "Pin apps or files to DashLauncher";
                    d.Multiselect = true;
                    d.CheckFileExists = true;
                    d.Filter = "Apps and files|*.exe;*.lnk;*.bat;*.cmd;*.ps1;*.url;*.*";
                    if (d.ShowDialog(this) != DialogResult.OK) { return; }
                    PushAppsAdd(AppsJsonForPaths(d.FileNames));
                }
            }
            catch (Exception ex) { Log("browse apps failed: " + ex.Message); }
        }

        private static string AppsJsonForPaths(string[] paths)
        {
            StringBuilder sb = new StringBuilder("[");
            for (int i = 0; i < paths.Length; i++)
            {
                string path = paths[i];
                string name = Directory.Exists(path)
                    ? new DirectoryInfo(path).Name
                    : Path.GetFileNameWithoutExtension(path);
                if (i > 0) { sb.Append(','); }
                sb.Append("{\"id\":").Append(JsString("path:" + path))
                  .Append(",\"name\":").Append(JsString(name))
                  .Append(",\"path\":").Append(JsString(path))
                  .Append(",\"icon\":").Append(JsString(IconDataUri(path))).Append('}');
            }
            return sb.Append(']').ToString();
        }

        private static string IconDataUri(string path)
        {
            IntPtr h = IntPtr.Zero;
            try
            {
                SHFILEINFO info = new SHFILEINFO();
                uint size = (uint)Marshal.SizeOf(typeof(SHFILEINFO));
                IntPtr ok = SHGetFileInfo(path, 0, ref info, size, 0x100); // SHGFI_ICON
                if (ok == IntPtr.Zero || info.hIcon == IntPtr.Zero) { return ""; }
                h = info.hIcon;
                using (Icon icon = (Icon)Icon.FromHandle(h).Clone())
                using (Bitmap bmp = icon.ToBitmap())
                using (MemoryStream ms = new MemoryStream())
                {
                    bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
                }
            }
            catch { return ""; }
            finally { if (h != IntPtr.Zero) { DestroyIcon(h); } }
        }

        private void PollPinQueue()
        {
            if (!pageReady) { return; }
            string file = Path.Combine(appDir, "pin-queue.txt");
            if (!File.Exists(file)) { return; }
            string[] lines;
            try
            {
                lines = File.ReadAllLines(file);
                File.Delete(file);
            }
            catch { return; }
            for (int i = 0; i < lines.Length; i++)
            {
                string line = (lines[i] ?? "").Trim();
                if (line.Length > 0) { AddPinPath(line); }
            }
        }

        private void AddPinPath(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) { return; }
                string json = AppsJsonForPaths(new string[] { path });
                PushAppsAdd(json);
                Log("pin received: " + path);
            }
            catch (Exception ex) { Log("pin failed: " + ex.Message); }
        }

        private void InstallExplorerPin()
        {
            try
            {
                string root = Path.GetFullPath(Path.Combine(appDir, ".."));
                string script = Path.Combine(root, "pin-to-dash.ps1");
                string icon = Path.Combine(root, "icloud.ico");
                string command = "\"powershell.exe\" -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File \"" + script + "\" \"%1\"";
                InstallPinKey(@"Software\Classes\*\shell\DashLauncher", "Pin to DashLauncher", icon, command);
                InstallPinKey(@"Software\Classes\Directory\shell\DashLauncher", "Pin to DashLauncher", icon, command);
                Log("explorer pin menu installed");
            }
            catch (Exception ex) { Log("explorer menu failed: " + ex.Message); }
        }

        private static void InstallPinKey(string keyPath, string label, string icon, string command)
        {
            using (Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(keyPath))
            {
                key.SetValue("", label);
                if (File.Exists(icon)) { key.SetValue("Icon", icon); }
                using (Microsoft.Win32.RegistryKey cmd = key.CreateSubKey("command"))
                {
                    cmd.SetValue("", command);
                }
            }
        }

        // Right-click: Chromium's own entries, plus the panel's at the top. The
        // reset exists because the window is frameless -- a panel left hanging off
        // the edge of a monitor (screen unplugged, resolution changed) has no
        // title bar to grab and cannot be dragged back into view.
        private void OnContextMenuRequested(object sender, CoreWebView2ContextMenuRequestedEventArgs e)
        {
            try
            {
                CoreWebView2Environment env = coreEnv;
                Log("context menu requested at " + e.Location + (env == null ? " (no environment)" : ""));
                if (env == null) { return; }
                CoreWebView2ContextMenuItem reset = env.CreateContextMenuItem(
                    "Reset panel position and size", null, CoreWebView2ContextMenuItemKind.Command);
                reset.CustomItemSelected += delegate(object s2, object a2) { ResetToScreen(); };
                e.MenuItems.Insert(0, reset);
                CoreWebView2ContextMenuItem lockItem = env.CreateContextMenuItem(
                    locked ? "Unlock panel" : "Lock panel", null, CoreWebView2ContextMenuItemKind.Command);
                lockItem.CustomItemSelected += delegate(object s2, object a2) { ToggleLock(); };
                e.MenuItems.Insert(1, lockItem);
                e.MenuItems.Insert(2, env.CreateContextMenuItem(
                    "", null, CoreWebView2ContextMenuItemKind.Separator));
                Log("context menu: reset item added at " + e.Location + " (" + e.MenuItems.Count + " items)");
            }
            catch (Exception ex) { Log("context menu failed: " + ex.Message); }
        }

        // Centres the panel on the monitor it mostly occupies, shrinking it only
        // when the current size does not fit that monitor's working area.
        private void ResetToScreen()
        {
            Rectangle b = Bounds;
            Screen screen = Screen.FromPoint(new Point(b.Left + (b.Width / 2), b.Top + (b.Height / 2)));
            Rectangle wa = screen.WorkingArea;
            int margin = 24;
            int w = Math.Min(b.Width, Math.Max(320, wa.Width - (margin * 2)));
            int h = Math.Min(b.Height, Math.Max(400, wa.Height - (margin * 2)));
            int left = wa.Left + ((wa.Width - w) / 2);
            int top = wa.Top + ((wa.Height - h) / 2);
            Bounds = new Rectangle(left, top, w, h);
            wallKey = "";            // the wallpaper crop is tied to position and size
            ApplyRegion();
            ApplyBackground();
            SaveConfig(ClientOnScreen());
            // A pinned panel lives behind other windows, so a reset that only moves
            // it can look like nothing happened at all: surface it as well.
            if (!Visible) { Show(); }
            SetForegroundWindow(Handle);
            Log("reset: " + w + "x" + h + " at " + left + "," + top +
                " (screen " + wa.Width + "x" + wa.Height + " at " + wa.Left + "," + wa.Top + ")");
        }

        private void ApplyLayering()
        {
            // "Pinned" is enforced on focus loss, not here: a tray click has to be
            // able to raise the panel, and only then does it drop back down.
            TopMost = topMost;
        }

        private void SendToBottom()
        {
            // Deliberately not HWND_BOTTOM: below the desktop window WebView2 stops
            // compositing, so the panel comes back as an unpainted blank surface.
            // Sitting directly under whichever window took the focus is still
            // "behind every app", but stays above the desktop and keeps painting.
            IntPtr above = GetForegroundWindow();
            if (above == IntPtr.Zero || above == Handle)
            {
                Log("pinned: left in place (no other window has focus)");
                return;
            }
            SetWindowPos(Handle, above, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            Log("pinned: dropped behind the focused window");
        }

        // Flyout mode: the tray click brings the panel up, losing focus takes it
        // away completely.
        private void AutoHideNow()
        {
            Hide();
            Log("auto-hide: hidden on focus loss");
        }

        // ------------------------------------------------------- background ---
        // ------------------------------------------------- wallpaper underlay ---
        // A windowed WebView2 has its own opaque surface, so "transparent" can
        // only ever show this panel's dark base, never the desktop. Instead the
        // panel paints the user's real wallpaper underneath: the slice of it
        // that sits behind the panel, so it reads as glass.
        private string wallUri = "";
        private string wallKey = "";
        private int bgOffset = 0;

        private static string WallpaperFile()
        {
            try
            {
                // What Settings itself writes for the current wallpaper.
                string themes = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Microsoft", "Windows", "Themes", "TranscodedWallpaper");
                if (File.Exists(themes)) { return themes; }
            }
            catch { }
            try
            {
                using (Microsoft.Win32.RegistryKey key =
                    Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Control Panel\\Desktop"))
                {
                    if (key != null)
                    {
                        object value = key.GetValue("WallPaper");
                        string path = (value == null) ? null : value.ToString();
                        if (!string.IsNullOrEmpty(path) && File.Exists(path)) { return path; }
                    }
                }
            }
            catch { }
            return null;
        }

        private string WallpaperDataUrl(int w, int h)
        {
            try
            {
                string path = WallpaperFile();
                if (path == null) { return ""; }
                DateTime stamp = File.GetLastWriteTimeUtc(path);
                string key = w + "x" + h + "@" + Bounds.Left + "," + Bounds.Top + "@" + stamp.Ticks + "@" + bgOffset;
                if (key == wallKey && wallUri.Length > 0) { return wallUri; }

                byte[] encoded;
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (Image src = Image.FromStream(fs))
                {
                    // Windows 11's default "Fill": the picture covers the monitor,
                    // centred, so the panel's slice can be computed exactly.
                    // Windows fits the wallpaper to the monitor's *working area*, so
                    // a taskbar at the top or bottom shifts the picture by half its
                    // height; using Bounds here left the underlay a taskbar off.
                    Rectangle monitor = Screen.FromPoint(new Point(
                        Bounds.Left + (Bounds.Width / 2),
                        Bounds.Top + (Bounds.Height / 2))).WorkingArea;
                    double scale = Math.Max(
                        (double)monitor.Width / src.Width,
                        (double)monitor.Height / src.Height);
                    // "Fill" centres the scaled picture on the monitor, so the visible
                    // part starts half the overflow in; that offset is negative when
                    // the picture is bigger than the monitor and has to be subtracted.
                    double offX = (monitor.Width - (src.Width * scale)) / 2;
                    double offY = (monitor.Height - (src.Height * scale)) / 2;
                    double sx = ((Bounds.Left - monitor.Left) - offX) / scale;
                    double sy = ((Bounds.Top - monitor.Top) - offY) / scale;
                    // Manual nudge: positive moves the picture down, which means
                    // starting the crop higher in the image.
                    sy -= bgOffset / scale;

                    int cw = Math.Min(src.Width, Math.Max(1, (int)Math.Round(w / scale)));
                    int ch = Math.Min(src.Height, Math.Max(1, (int)Math.Round(h / scale)));
                    int cx = Math.Max(0, Math.Min((int)Math.Round(sx), src.Width - cw));
                    int cy = Math.Max(0, Math.Min((int)Math.Round(sy), src.Height - ch));

                    using (Bitmap bmp = new Bitmap(w, h))
                    {
                        using (Graphics g = Graphics.FromImage(bmp))
                        {
                            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                            g.DrawImage(src, new Rectangle(0, 0, w, h),
                                new Rectangle(cx, cy, cw, ch), GraphicsUnit.Pixel);
                        }
                        using (MemoryStream ms = new MemoryStream())
                        {
                            System.Drawing.Imaging.ImageCodecInfo jpeg = null;
                            foreach (System.Drawing.Imaging.ImageCodecInfo codec in
                                     System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders())
                            {
                                if (codec.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid) { jpeg = codec; }
                            }
                            System.Drawing.Imaging.EncoderParameters ps =
                                new System.Drawing.Imaging.EncoderParameters(1);
                            ps.Param[0] = new System.Drawing.Imaging.EncoderParameter(
                                System.Drawing.Imaging.Encoder.Quality, 82L);
                            if (jpeg != null) { bmp.Save(ms, jpeg, ps); }
                            else { bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Jpeg); }
                            encoded = ms.ToArray();
                        }
                    }
                }
                wallUri = "data:image/jpeg;base64," + Convert.ToBase64String(encoded);
                wallKey = key;
                Log("wallpaper underlay: " + w + "x" + h + ", " + (encoded.Length / 1024) + " KB");
                return wallUri;
            }
            catch (Exception ex)
            {
                Log("wallpaper underlay failed: " + ex.Message);
                return "";
            }
        }

        private string BgJson()
        {
            if (bgExt.Length > 0 && bgDataUri.Length == 0)
            {
                try
                {
                    string imgPath = Path.Combine(appDir, "background.bin");
                    if (File.Exists(imgPath))
                    {
                        bgDataUri = "data:image/" + bgExt + ";base64," +
                                    Convert.ToBase64String(File.ReadAllBytes(imgPath));
                    }
                }
                catch (Exception ex) { Log("background image load failed: " + ex.Message); }
            }
            string wall = (bgKind == "wallpaper")
                ? WallpaperDataUrl(Math.Max(320, Bounds.Width), Math.Max(240, Bounds.Height))
                : "";
            return "{\"kind\":\"" + bgKind + "\",\"color\":\"" + bgColor + "\",\"img\":\"" +
                   bgDataUri + "\",\"wall\":\"" + wall + "\",\"opacity\":" +
                   bgOpacity.ToString("0.##", CultureInfo.InvariantCulture) +
                   ",\"offset\":" + bgOffset + "}";
        }

        private void ApplyBackground()
        {
            SaveConfig(ClientOnScreen());
            try
            {
                if (web != null && web.CoreWebView2 != null)
                {
                    web.CoreWebView2.ExecuteScriptAsync("window.icApplyBg&&window.icApplyBg(" + BgJson() + ")");
                }
            }
            catch (Exception ex) { Log("background apply failed: " + ex.Message); }
            NotifyPage();
            Log("background: " + bgKind + " " + bgColor + " image=" + bgExt +
                " opacity=" + bgOpacity.ToString("0.##", CultureInfo.InvariantCulture));
        }

        private void PickColor()
        {
            BeginInvoke((MethodInvoker)delegate
            {
                try
                {
                    using (ColorDialog dlg = new ColorDialog())
                    {
                        dlg.FullOpen = true;
                        try { dlg.Color = ColorTranslator.FromHtml(bgColor); } catch { }
                        if (dlg.ShowDialog(this) != DialogResult.OK) { return; }
                        bgColor = "#" + dlg.Color.R.ToString("x2") + dlg.Color.G.ToString("x2") +
                                  dlg.Color.B.ToString("x2");
                        bgKind = "color";
                        ApplyBackground();
                    }
                }
                catch (Exception ex) { Log("background colour pick failed: " + ex.Message); }
            });
        }

        private void PickImage()
        {
            BeginInvoke((MethodInvoker)delegate
            {
                try
                {
                    using (OpenFileDialog dlg = new OpenFileDialog())
                    {
                        dlg.Title = "Choose a background image";
                        dlg.Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif";
                        if (dlg.ShowDialog(this) != DialogResult.OK) { return; }
                        string ext = Path.GetExtension(dlg.FileName).TrimStart('.').ToLowerInvariant();
                        if (ext == "jpeg") { ext = "jpg"; }
                        File.WriteAllBytes(Path.Combine(appDir, "background.bin"),
                            File.ReadAllBytes(dlg.FileName));
                        bgExt = ext;
                        bgDataUri = "";
                        bgKind = "image";
                        ApplyBackground();
                    }
                }
                catch (Exception ex) { Log("background image pick failed: " + ex.Message); }
            });
        }

        private void ToggleLock()
        {
            if (locked) { Unlock(); } else { Lock(); }
            NotifyPage();
        }

        // Unlocked: native caption + resize border so the mouse can move and
        // resize it freely. The page keeps exactly the same on-screen box.
        private void Unlock()
        {
            int[] before = ClientOnScreen();
            locked = false;
            ApplyRoundedCorners(0);
            SetWindowRgn(Handle, IntPtr.Zero, true);
            SetFrame(true);

            int[] after = ClientOnScreen();
            RECT wr;
            GetWindowRect(Handle, out wr);
            int nx = wr.L + (before[0] - after[0]);
            int ny = wr.T + (before[1] - after[1]);
            int nw = (wr.R - wr.L) + (before[2] - after[2]);
            int nh = (wr.B - wr.T) + (before[3] - after[3]);
            SetWindowPos(Handle, IntPtr.Zero, nx, ny, nw, nh, SWP_NOZORDER | SWP_NOACTIVATE);

            Log("unlocked: page box " + before[2] + "x" + before[3] + " at " + before[0] + "," + before[1]);
        }

        // Locked: strip the frame, round the corners, save where the page sits
        // (including which monitor that is).
        private void Lock()
        {
            int[] box = ClientOnScreen();
            locked = true;
            SetFrame(false);
            if (box[2] > 0 && box[3] > 0)
            {
                Bounds = new Rectangle(box[0], box[1], box[2], box[3]);
            }
            ApplyRoundedCorners(2);
            ApplyRegion();

            Timer settle = new Timer();
            settle.Interval = 200;
            settle.Tick += delegate(object s, EventArgs a)
            {
                settle.Stop();
                settle.Dispose();
                ApplyRegion();
            };
            settle.Start();

            SaveConfig(box);
            Log("locked: " + box[2] + "x" + box[3] + " at " + box[0] + "," + box[1]);
        }

        private void SaveConfig(int[] box)
        {
            try
            {
                string json = "{\"width\":" + box[2] +
                              ",\"height\":" + box[3] +
                              ",\"x\":" + box[0] +
                              ",\"y\":" + box[1] +
                              ",\"radius\":" + radius +
                              ",\"topmost\":" + (topMost ? "true" : "false") +
                              ",\"pinned\":" + (pinned ? "true" : "false") +
                              ",\"autohide\":" + (autoHide ? "true" : "false") +
                              ",\"showinfo\":" + (showInfo ? "true" : "false") +
                              ",\"name\":" + JsString(userName) +
                              ",\"birthdays\":" + birthdaysJson +
                              ",\"birthdayAccounts\":" + birthdayAccountsJson +
                              ",\"bgkind\":\"" + bgKind + "\"" +
                              ",\"bgcolor\":\"" + bgColor + "\"" +
                              ",\"bgimage\":\"" + bgExt + "\"" +
                              ",\"bgopacity\":" + bgOpacity.ToString("0.##", CultureInfo.InvariantCulture) +
                              ",\"bgoffset\":" + bgOffset +
                              ",\"dash\":" + (string.IsNullOrEmpty(dashJson) ? "null" : dashJson) +
                              ",\"weather\":{\"lat\":" +
                              (double.IsNaN(weatherLat) ? "null" : weatherLat.ToString("0.####", CultureInfo.InvariantCulture)) +
                              ",\"lon\":" +
                              (double.IsNaN(weatherLon) ? "null" : weatherLon.ToString("0.####", CultureInfo.InvariantCulture)) +
                              ",\"place\":" + JsString(weatherPlace ?? "") +
                              ",\"windows\":" + (winLocTried ? "true" : "false") + "}" +
                              ",\"outlookUrl\":" + JsString(outlookUrl ?? "") +
                              ",\"zoom\":" + zoom.ToString("0.###", CultureInfo.InvariantCulture) + "}";
                File.WriteAllText(configPath, json);
                Log("config saved: " + json);
            }
            catch (Exception ex)
            {
                Log("config save failed: " + ex.Message);
            }
        }

        private void NotifyPage()
        {
            try
            {
                if (web != null && web.CoreWebView2 != null)
                {
                    web.CoreWebView2.ExecuteScriptAsync(
                        "var d=document.documentElement;" +
                        "d.setAttribute('data-ic-locked','" + (locked ? "1" : "0") + "');" +
                        "d.setAttribute('data-ic-topmost','" + (topMost ? "1" : "0") + "');" +
                        "d.setAttribute('data-ic-autohide','" + (autoHide ? "1" : "0") + "');" +
                        "d.setAttribute('data-ic-pinned','" + (pinned ? "1" : "0") + "');" +
                        "d.setAttribute('data-ic-info','" + (showInfo ? "1" : "0") + "');");
                }
            }
            catch { }
        }

        private void SetFrame(bool withCaption)
        {
            int style = GetWindowLong(Handle, GWL_STYLE);
            if (withCaption)
            {
                style |= WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX;
            }
            else
            {
                style &= ~(WS_CAPTION | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_SYSMENU);
            }
            SetWindowLong(Handle, GWL_STYLE, style);
            SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOSIZE | SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }

        private void ApplyRoundedCorners(int preference)
        {
            DwmSetWindowAttribute(Handle, 33, ref preference, 4);   // DWMWA_WINDOW_CORNER_PREFERENCE
            int none = -2;                                          // DWMWA_COLOR_NONE
            DwmSetWindowAttribute(Handle, 34, ref none, 4);
        }

        private int[] ClientOnScreen()
        {
            RECT c;
            GetClientRect(Handle, out c);
            POINT p = new POINT();
            p.X = c.L;
            p.Y = c.T;
            ClientToScreen(Handle, ref p);
            return new int[] { p.X, p.Y, c.R - c.L, c.B - c.T };
        }

        private void ApplyRegion()
        {
            if (radius <= 0)
            {
                SetWindowRgn(Handle, IntPtr.Zero, true);
                return;
            }
            RECT c;
            GetClientRect(Handle, out c);
            int w = c.R - c.L;
            int h = c.B - c.T;
            if (w <= 0 || h <= 0) { return; }
            int ellipse = Math.Max(2, Math.Min(2 * radius, Math.Min(w, h)));
            IntPtr region = CreateRoundRectRgn(0, 0, w + 1, h + 1, ellipse, ellipse);
            if (region != IntPtr.Zero) { SetWindowRgn(Handle, region, true); }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyRoundedCorners(locked ? 2 : 0);
        }

        // The tray (a different process) asks for a new note with WM_COPYDATA,
        // so the click happens in the page the panel already has open.
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_COPYDATA && m.LParam != IntPtr.Zero)
            {
                string command = "";
                string pinPath = null;
                try
                {
                    COPYDATASTRUCT data = (COPYDATASTRUCT)Marshal.PtrToStructure(m.LParam, typeof(COPYDATASTRUCT));
                    if (data.lpData != IntPtr.Zero && data.cbData > 0)
                    {
                        string payload = Marshal.PtrToStringUni(data.lpData, data.cbData / 2) ?? "";
                        if (data.dwData.ToInt32() == NewNoteMagic) { command = payload; }
                        else if (data.dwData.ToInt32() == AppPinMagic) { pinPath = payload; }
                    }
                }
                catch (Exception ex) { Log("copy-data error: " + ex.Message); }

                if (pinPath != null)
                {
                    Log("pin: command received");
                    m.Result = (IntPtr)1;
                    string pendingPath = pinPath;
                    BeginInvoke((Action)delegate { AddPinPath(pendingPath); });
                    return;
                }
                if (command == "new-note")
                {
                    Log("new-note: command received");
                    m.Result = (IntPtr)1;
                    // Never do page work inside SendMessage: the sender is blocked
                    // until this returns.
                    BeginInvoke((Action)RunNewNote);
                    return;
                }
            }
            base.WndProc(ref m);
        }

        // Some of Apple's controls ignore scripted DOM events, so the last step is
        // a real mouse click at the button's place on screen. The rect arrives in
        // the page's CSS pixels, so it is scaled by the client width and the page
        // zoom before it becomes a screen point.
        private void RealClick(string spec)
        {
            try
            {
                string[] parts = spec.Split(',');
                double l, t, w, h, innerW;
                if (parts.Length < 5 ||
                    !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out l) ||
                    !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out t) ||
                    !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out w) ||
                    !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out h) ||
                    !double.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out innerW) ||
                    innerW <= 0)
                {
                    Log("new-note: bad click spec " + spec);
                    return;
                }
                int[] client = ClientOnScreen();
                if (client[2] <= 0 || client[3] <= 0) { Log("new-note: no client area"); return; }
                double scale = client[2] / innerW;
                int sx = client[0] + (int)Math.Round((l + w / 2.0) * scale);
                int sy = client[1] + (int)Math.Round((t + h / 2.0) * scale);
                SetForegroundWindow(Handle);
                SetCursorPos(sx, sy);
                System.Threading.Thread.Sleep(80);
                mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);   // LEFTDOWN
                System.Threading.Thread.Sleep(40);
                mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);   // LEFTUP
                Log("new-note: real click at " + sx + "," + sy +
                    " (scale " + scale.ToString("0.###", CultureInfo.InvariantCulture) + ")");
            }
            catch (Exception ex) { Log("new-note: real click failed: " + ex.Message); }
        }

        private void RunNewNote()
        {
            try
            {
                if (web == null || web.CoreWebView2 == null)
                {
                    newNotePending = true;
                    Log("new-note: queued (webview not ready)");
                    return;
                }
                string url = "";
                try { url = web.CoreWebView2.Source ?? ""; }
                catch { }
                if (url.IndexOf("icloud.com", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    // Cold start still sits on about:blank; the navigation handler
                    // replays this once the page is iCloud.
                    newNotePending = true;
                    Log("new-note: queued until icloud.com loads (" + url + ")");
                    return;
                }
                newNotePending = false;
                web.CoreWebView2.ExecuteScriptAsync(
                    "try{window.icNewNote?window.icNewNote():'no-op'}catch(e){'err:'+e.message}")
                    .ContinueWith(delegate(Task<string> task)
                    {
                        string result = task.IsFaulted ? ("failed " + task.Exception.Message) : (task.Result ?? "null");
                        Log("new-note: page " + result);
                    });
            }
            catch (Exception ex) { Log("new-note error: " + ex.Message); }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape && !locked)
            {
                ToggleLock();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private static int ReadInt(string text, string key, int fallback)
        {
            int value;
            return TryReadInt(text, key, out value) ? value : fallback;
        }

        private static bool ReadBool(string text, string key)
        {
            Match m = Regex.Match(text, "\"" + key + "\"\\s*:\\s*(true|false)");
            return m.Success && m.Groups[1].Value == "true";
        }

        private static bool TryReadInt(string text, string key, out int value)
        {
            Match m = Regex.Match(text, "\"" + key + "\"\\s*:\\s*(-?\\d+)");
            if (m.Success && int.TryParse(m.Groups[1].Value, out value))
            {
                return true;
            }
            value = 0;
            return false;
        }

        [STAThread]
        private static void Main()
        {
            SetProcessDPIAware();
            Log("start");
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs a)
            {
                Log("unhandled: " + a.ExceptionObject);
            };
            Application.ThreadException += delegate(object s, System.Threading.ThreadExceptionEventArgs a)
            {
                Log("thread exception: " + a.Exception);
            };
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new PanelForm());
                Log("exit");
            }
            catch (Exception ex)
            {
                Log("fatal: " + ex);
                MessageBox.Show(ex.ToString(), "iCloud panel");
            }
        }
    }
}
