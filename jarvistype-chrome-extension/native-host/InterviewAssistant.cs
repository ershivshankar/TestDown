// language: C#  file: InterviewAssistant.cs  target: Windows 10/11, .NET 4.6+
// Compile:
// csc /target:winexe /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Net.Http.dll /r:Microsoft.Web.WebView2.WinForms.dll /r:Microsoft.Web.WebView2.Core.dll /out:InterviewAssistant.exe InterviewAssistant.cs

using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

// ── Native Win32 ──────────────────────────────────────────────────────────────
[StructLayout(LayoutKind.Sequential)]
struct MARGINS { public int Left, Right, Top, Bottom; }

[StructLayout(LayoutKind.Sequential)]
struct ACCENT_POLICY {
    public int AccentState;
    public int AccentFlags;
    public int GradientColor;
    public int AnimationId;
}

[StructLayout(LayoutKind.Sequential)]
struct WINCOMPATTRDATA {
    public int    Attribute;
    public IntPtr Data;
    public int    SizeOfData;
}

static class WinAPI {
    [DllImport("dwmapi.dll")] public static extern int  DwmSetWindowAttribute(IntPtr h, int a, ref int v, int s);
    [DllImport("dwmapi.dll")] public static extern int  DwmExtendFrameIntoClientArea(IntPtr h, ref MARGINS m);
    [DllImport("user32.dll")] public static extern bool SetWindowDisplayAffinity(IntPtr h, uint a);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr FindWindow(string cls, string win);
    [DllImport("user32.dll")] public static extern int  SendMessage(IntPtr h, int msg, int wp, int lp);
    [DllImport("user32.dll")] public static extern bool ReleaseCapture();
    [DllImport("user32.dll")] public static extern uint SendInput(uint n, [MarshalAs(UnmanagedType.LPArray), In] INPUT[] inputs, int sz);
    [DllImport("user32.dll")] public static extern int  SetWindowCompositionAttribute(IntPtr h, ref WINCOMPATTRDATA d);
    [DllImport("gdi32.dll")]  public static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int cx, int cy);

    // WH_KEYBOARD_LL global hook
    [DllImport("user32.dll", SetLastError=true)] public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError=true)] public static extern bool   UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern short  GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] public static extern bool   PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool   EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint   GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] public static extern bool   RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] public static extern bool   UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("kernel32.dll", CharSet=CharSet.Auto, SetLastError=true)] public static extern IntPtr GetModuleHandle(string lpModuleName);

    public const int WM_HOTKEY        = 0x0312;
    public const int WM_NCHITTEST     = 0x84;
    public const int WM_NCLBUTTONDOWN = 0xA1;
    public const int WM_USER          = 0x0400;
    public const int WM_SHOW_OVERLAY  = WM_USER + 777;
    public const int HT_CAPTION       = 2;
    public const int HTTOP            = 12;
    public const int HTTOPLEFT        = 13;
    public const int HTTOPRIGHT       = 14;
    public const int HTLEFT           = 10;
    public const int HTRIGHT          = 11;
    public const int HTBOTTOM         = 15;
    public const int HTBOTTOMLEFT     = 16;
    public const int HTBOTTOMRIGHT    = 17;
}

public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
public delegate bool   EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

[StructLayout(LayoutKind.Sequential)]
public struct KBDLLHOOKSTRUCT {
    public uint   vkCode;
    public uint   scanCode;
    public uint   flags;
    public uint   time;
    public IntPtr dwExtraInfo;
}

[StructLayout(LayoutKind.Explicit)]
public struct INPUT { [FieldOffset(0)] public uint type; [FieldOffset(8)] public KEYBDINPUT ki; }
[StructLayout(LayoutKind.Sequential)]
public struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

// ── Silent auto-typer ─────────────────────────────────────────────────────────
static class SilentTyper {
    static CancellationTokenSource _cts;
    static int _cpm = 250;

    public static void SetSpeed(int cpm) { _cpm = cpm > 0 ? cpm : 250; }

    public static void Start(string text, Action<int,int> onProgress) {
        Stop();
        if (string.IsNullOrEmpty(text)) return;
        _cts = new CancellationTokenSource();
        var tok = _cts.Token;
        Task.Run(async () => {
            for (int i = 0; i < text.Length; i++) {
                if (tok.IsCancellationRequested) break;
                TypeChar(text[i]);
                if (onProgress != null) onProgress(i + 1, text.Length);
                int delay = Math.Max(10, 60000 / _cpm + new Random().Next(-15, 25));
                try { await Task.Delay(delay, tok); } catch { break; }
            }
        }, tok);
    }

    public static void Stop() {
        if (_cts != null) { try { _cts.Cancel(); } catch {} try { _cts.Dispose(); } catch {} _cts = null; }
    }

    static void TypeChar(char c) {
        INPUT[] inp = new INPUT[2];
        inp[0].type = 1; inp[0].ki.wScan = (ushort)c; inp[0].ki.dwFlags = 0x0004;
        inp[1].type = 1; inp[1].ki.wScan = (ushort)c; inp[1].ki.dwFlags = 0x0004 | 0x0002;
        WinAPI.SendInput(2, inp, Marshal.SizeOf(typeof(INPUT)));
    }
}

// ── Overlay Form ──────────────────────────────────────────────────────────────
class OverlayForm : Form {
    WebView2 wv;
    string   htmlPath;
    const int RESIZE_GRIP = 6;

    // WH_KEYBOARD_LL hook — keep static so GC never collects the delegate
    static LowLevelKeyboardProc _hookProc   = null;
    static IntPtr               _hookHandle = IntPtr.Zero;
    static DateTime             _lastToggle = DateTime.MinValue;

    public OverlayForm() {
        Text            = "";              // blank title — nothing to identify in task manager title bar
        Size            = new Size(400, 620);
        MinimumSize     = new Size(280, 360);
        FormBorderStyle = FormBorderStyle.None;
        BackColor       = Color.FromArgb(18, 18, 28);
        StartPosition   = FormStartPosition.Manual;
        ShowInTaskbar   = false;           // not in taskbar
        TopMost         = true;

        Rectangle scr = Screen.PrimaryScreen.WorkingArea;
        Location = new Point(scr.Width - Width - 16, 30);

        // Start hidden — only shows on hotkey
        Load += (s, e) => Hide();

        InitWebView();
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override CreateParams CreateParams {
        get {
            CreateParams cp = base.CreateParams;
            cp.Style   |= 0x00040000; // WS_THICKFRAME
            cp.ExStyle |= 0x00000008; // WS_EX_TOPMOST
            cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW — hides from Alt+Tab
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e) {
        base.OnHandleCreated(e);
        ApplyWindowEffects();
        try {
            // Screen-capture exclusion — invisible to OBS, Teams, Discord share, Snipping Tool
            WinAPI.SetWindowDisplayAffinity(Handle, 0x00000011u); // WDA_EXCLUDEFROMCAPTURE

            // Install WH_KEYBOARD_LL — system-wide keyboard hook, works even in lockdown browsers
            var self = this;
            _hookProc = delegate(int nCode, IntPtr wParam, IntPtr lParam) {
                try {
                    if (nCode >= 0 && (wParam == (IntPtr)0x100 || wParam == (IntPtr)0x104)) {
                        KBDLLHOOKSTRUCT kbd = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                        bool ctrl  = (WinAPI.GetAsyncKeyState(0x11) & 0x8000) != 0;
                        bool shift = (WinAPI.GetAsyncKeyState(0x10) & 0x8000) != 0;
                        // Ctrl+Shift+S or Ctrl+Shift+I
                        if (ctrl && shift && (kbd.vkCode == 0x53u || kbd.vkCode == 0x49u)) {
                            if ((DateTime.UtcNow - _lastToggle).TotalMilliseconds >= 500) {
                                _lastToggle = DateTime.UtcNow;
                                self.BeginInvoke(new Action(self.ToggleOverlay));
                            }
                        }
                    }
                } catch {}
                return WinAPI.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
            };
            IntPtr hMod = WinAPI.GetModuleHandle(Process.GetCurrentProcess().MainModule.ModuleName);
            _hookHandle = WinAPI.SetWindowsHookEx(13 /* WH_KEYBOARD_LL */, _hookProc, hMod, 0);
        } catch {}
    }

    protected override void OnHandleDestroyed(EventArgs e) {
        try { if (_hookHandle != IntPtr.Zero) WinAPI.UnhookWindowsHookEx(_hookHandle); } catch {}
        base.OnHandleDestroyed(e);
    }

    double _targetOpacity = 0.95;

    public void ToggleOverlay() {
        if (InvokeRequired) { BeginInvoke(new Action(ToggleOverlay)); return; }
        try {
            if (Visible && WindowState != FormWindowState.Minimized) {
                // Fade out then hide
                FadeOut();
            } else {
                // Set transparent first so DWM acrylic initialises before anything is visible
                Opacity = 0;
                Show();
                WindowState = FormWindowState.Normal;
                TopMost     = true;
                BringToFront();
                WinAPI.ShowWindow(Handle, 9); // SW_RESTORE
                WinAPI.SetForegroundWindow(Handle);
                // Wait one paint cycle for acrylic to settle, then fade in
                var settle = new System.Windows.Forms.Timer();
                settle.Interval = 30;
                settle.Tick += delegate(object ts, EventArgs te) {
                    settle.Stop(); settle.Dispose();
                    FadeIn();
                };
                settle.Start();
            }
        } catch {}
    }

    void FadeIn() {
        var t = new System.Windows.Forms.Timer();
        t.Interval = 16; // ~60 fps steps
        t.Tick += delegate(object s, EventArgs e) {
            Opacity += 0.10;
            if (Opacity >= _targetOpacity) { Opacity = _targetOpacity; t.Stop(); t.Dispose(); }
        };
        t.Start();
    }

    void FadeOut() {
        var t = new System.Windows.Forms.Timer();
        t.Interval = 16;
        t.Tick += delegate(object s, EventArgs e) {
            Opacity -= 0.15;
            if (Opacity <= 0) { Opacity = 0; t.Stop(); t.Dispose(); Hide(); Opacity = _targetOpacity; }
        };
        t.Start();
    }

    void ApplyWindowEffects() {
        try {
            int backdrop = 3; // DWMSBT_TRANSIENTWINDOW (Acrylic)
            WinAPI.DwmSetWindowAttribute(Handle, 38, ref backdrop, 4);
            var mg = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            WinAPI.DwmExtendFrameIntoClientArea(Handle, ref mg);
            int dark = 1;
            WinAPI.DwmSetWindowAttribute(Handle, 20, ref dark, 4);
            int rc = 2;
            WinAPI.DwmSetWindowAttribute(Handle, 33, ref rc, 4);
        } catch {}
        try {
            var accent = new ACCENT_POLICY {
                AccentState   = 4,
                AccentFlags   = 0x20,
                GradientColor = unchecked((int)0x7012121C),
                AnimationId   = 0
            };
            var sz  = Marshal.SizeOf(accent);
            var ptr = Marshal.AllocHGlobal(sz);
            Marshal.StructureToPtr(accent, ptr, false);
            var data = new WINCOMPATTRDATA { Attribute = 19, Data = ptr, SizeOfData = sz };
            WinAPI.SetWindowCompositionAttribute(Handle, ref data);
            Marshal.FreeHGlobal(ptr);
        } catch {}
    }

    // ── Resize (borderless) ───────────────────────────────────────────────────
    protected override void WndProc(ref Message m) {
        if (m.Msg == WinAPI.WM_SHOW_OVERLAY) { ToggleOverlay(); return; }
        if (m.Msg == WinAPI.WM_NCHITTEST) {
            base.WndProc(ref m);
            int lp = m.LParam.ToInt32();
            int px = (lp & 0xFFFF); if (px > 32767) px -= 65536;
            int py = (lp >> 16);    if (py > 32767) py -= 65536;
            Point p = PointToClient(new Point(px, py));
            int  g  = RESIZE_GRIP;
            bool top = p.Y < g, bot = p.Y >= ClientSize.Height - g;
            bool lft = p.X < g, rgt = p.X >= ClientSize.Width  - g;
            if (top && lft) { m.Result = (IntPtr)WinAPI.HTTOPLEFT;    return; }
            if (top && rgt) { m.Result = (IntPtr)WinAPI.HTTOPRIGHT;   return; }
            if (bot && lft) { m.Result = (IntPtr)WinAPI.HTBOTTOMLEFT; return; }
            if (bot && rgt) { m.Result = (IntPtr)WinAPI.HTBOTTOMRIGHT;return; }
            if (top)        { m.Result = (IntPtr)WinAPI.HTTOP;        return; }
            if (bot)        { m.Result = (IntPtr)WinAPI.HTBOTTOM;     return; }
            if (lft)        { m.Result = (IntPtr)WinAPI.HTLEFT;       return; }
            if (rgt)        { m.Result = (IntPtr)WinAPI.HTRIGHT;      return; }
            return;
        }
        base.WndProc(ref m);
    }

    // ── WebView2 ──────────────────────────────────────────────────────────────
    async void InitWebView() {
        wv = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.Transparent };
        Controls.Add(wv);

        string exeDir   = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
        htmlPath        = Path.Combine(exeDir, "ia-overlay.html");
        string userData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JarvisType", "ia_wv2");

        try {
            var env = await CoreWebView2Environment.CreateAsync(null, userData);
            await wv.EnsureCoreWebView2Async(env);

            wv.CoreWebView2.Settings.IsStatusBarEnabled              = false;
            wv.CoreWebView2.Settings.IsZoomControlEnabled            = false;
            wv.CoreWebView2.Settings.AreDevToolsEnabled              = false; // fully stealthy
            wv.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;

            wv.CoreWebView2.PermissionRequested += (s, e) => {
                if (e.PermissionKind == CoreWebView2PermissionKind.Microphone)
                    e.State = CoreWebView2PermissionState.Allow;
            };

            wv.CoreWebView2.WebMessageReceived += OnMessage;

            string url = File.Exists(htmlPath)
                ? "file:///" + htmlPath.Replace('\\', '/')
                : null;

            if (url != null) wv.CoreWebView2.Navigate(url);
            else wv.CoreWebView2.NavigateToString("<body style='background:#12121C;color:white;padding:24px'><h2>ia-overlay.html not found</h2></body>");
        } catch {}
    }

    // ── Messages from overlay page ────────────────────────────────────────────
    void OnMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e) {
        string raw  = e.TryGetWebMessageAsString();
        if (string.IsNullOrEmpty(raw)) return;
        string type = JsonField(raw, "type");

        if (type == "close") {
            if (InvokeRequired) Invoke(new Action(Hide)); else Hide();
        } else if (type == "drag") {
            if (InvokeRequired) Invoke(new Action(StartDrag)); else StartDrag();
        } else if (type == "grab-screen") {
            Task.Run(() => GrabScreen());
        } else if (type == "autotype-start") {
            string text = JsonField(raw, "text");
            SilentTyper.Start(text, (pos, tot) =>
                PostMessage(string.Format("{{\"type\":\"autotype-progress\",\"position\":{0},\"total\":{1}}}", pos, tot)));
        } else if (type == "autotype-stop") {
            SilentTyper.Stop();
        } else if (type == "autotype-speed") {
            int sp; if (int.TryParse(JsonField(raw, "speed"), out sp)) SilentTyper.SetSpeed(sp);
        } else if (type == "set-opacity") {
            double op = 0.95;
            string opStr = JsonField(raw, "opacity");
            if (double.TryParse(opStr, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out op)) {
                op = Math.Max(0.15, Math.Min(1.0, op));
                if (InvokeRequired) Invoke(new Action(() => { _targetOpacity = op; Opacity = op; }));
                else { _targetOpacity = op; Opacity = op; }
            }
        } else if (type == "resize-start") {
            int dir = 17;
            int.TryParse(JsonField(raw, "direction"), out dir);
            if (InvokeRequired) Invoke(new Action(() => { WinAPI.ReleaseCapture(); WinAPI.SendMessage(Handle, WinAPI.WM_NCLBUTTONDOWN, dir, 0); }));
            else { WinAPI.ReleaseCapture(); WinAPI.SendMessage(Handle, WinAPI.WM_NCLBUTTONDOWN, dir, 0); }
        }
    }

    void StartDrag() {
        WinAPI.ReleaseCapture();
        WinAPI.SendMessage(Handle, WinAPI.WM_NCLBUTTONDOWN, WinAPI.HT_CAPTION, 0);
    }

    void GrabScreen() {
        double prevOp = 0.95;
        if (InvokeRequired) Invoke(new Action(() => { prevOp = Opacity; Opacity = 0; }));
        Thread.Sleep(200);
        try {
            Rectangle bounds = Screen.PrimaryScreen.Bounds;
            Bitmap bmp = new Bitmap(bounds.Width, bounds.Height);
            using (Graphics g = Graphics.FromImage(bmp)) g.CopyFromScreen(Point.Empty, Point.Empty, bounds.Size);
            Bitmap output = bmp;
            if (bmp.Width > 1280) {
                int h = (int)(bmp.Height * (1280.0 / bmp.Width));
                output = new Bitmap(bmp, new Size(1280, h));
            }
            string b64;
            using (var ms = new MemoryStream()) {
                output.Save(ms, ImageFormat.Jpeg);
                b64 = "data:image/jpeg;base64," + Convert.ToBase64String(ms.ToArray());
            }
            if (output != bmp) output.Dispose();
            bmp.Dispose();
            PostMessage(string.Format("{{\"type\":\"grab-screen-result\",\"image\":\"{0}\"}}", b64));
        } catch (Exception ex) {
            PostMessage(string.Format("{{\"type\":\"grab-screen-result\",\"error\":\"{0}\"}}", ex.Message.Replace("\"","'")));
        } finally {
            if (InvokeRequired) Invoke(new Action(() => Opacity = prevOp));
            else Opacity = prevOp;
        }
    }

    void PostMessage(string json) {
        if (InvokeRequired) { Invoke(new Action<string>(PostMessage), json); return; }
        try { if (wv != null && wv.CoreWebView2 != null) wv.CoreWebView2.PostWebMessageAsString(json); } catch {}
    }

    static string JsonField(string json, string field) {
        var m = System.Text.RegularExpressions.Regex.Match(
            json, "\"" + System.Text.RegularExpressions.Regex.Escape(field) + "\"\\s*:\\s*\"?([^\",\\}\\]]+)\"?");
        return m.Success ? m.Groups[1].Value : "";
    }

    protected override void OnFormClosing(FormClosingEventArgs e) {
        if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
        else { SilentTyper.Stop(); }
    }

    protected override void Dispose(bool disposing) {
        try { if (_hookHandle != IntPtr.Zero) WinAPI.UnhookWindowsHookEx(_hookHandle); } catch {}
        if (disposing && wv != null) wv.Dispose();
        base.Dispose(disposing);
    }
}

// ── Entry point ───────────────────────────────────────────────────────────────
static class Program {
    [STAThread]
    static void Main() {
        // Single instance check — uses actual process name so renaming the exe still works
        string myName = Process.GetCurrentProcess().ProcessName;
        try {
            int myPid = Process.GetCurrentProcess().Id;
            foreach (var p in Process.GetProcessesByName(myName)) {
                if (p.Id != myPid) {
                    uint targetPid = (uint)p.Id;
                    WinAPI.EnumWindows(delegate(IntPtr hWnd, IntPtr lParam) {
                        uint pid;
                        WinAPI.GetWindowThreadProcessId(hWnd, out pid);
                        if (pid == targetPid) {
                            WinAPI.PostMessage(hWnd, (uint)WinAPI.WM_SHOW_OVERLAY, IntPtr.Zero, IntPtr.Zero);
                            WinAPI.ShowWindow(hWnd, 9);
                            WinAPI.SetForegroundWindow(hWnd);
                        }
                        return true;
                    }, IntPtr.Zero);
                    return; // already running
                }
            }
        } catch {}

        System.Net.ServicePointManager.SecurityProtocol =
            (System.Net.SecurityProtocolType)(3072 | 768 | 192);
        System.Net.ServicePointManager.ServerCertificateValidationCallback = (s, c, ch, e) => true;

        // Auto-start: register under a generic key name pointing to this exe's actual path
        try {
            string exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
            using (var rk = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", true)) {
                if (rk != null)
                    rk.SetValue("DesktopWindowManager", "\"" + exePath + "\"");
            }
        } catch {}

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var form = new OverlayForm();
        form.Opacity = 0.95;
        Application.Run(form);
    }
}
