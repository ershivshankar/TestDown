// JarvisType Native Messaging Host (Windows)
// Compile: csc.exe /target:winexe /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Net.Http.dll /out:WinSpeechHost.exe host.cs

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using System.Text;
using System.Net.WebSockets;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

// ── Win32 Structs ──────────────────────────────────────────────────────────────

[StructLayout(LayoutKind.Sequential)]
public struct MOUSEINPUT {
    public int dx, dy;
    public uint mouseData, dwFlags, time;
    public IntPtr dwExtraInfo;
}

[StructLayout(LayoutKind.Sequential)]
public struct KEYBDINPUT {
    public ushort wVk, wScan;
    public uint dwFlags, time;
    public IntPtr dwExtraInfo;
}

[StructLayout(LayoutKind.Explicit)]
public struct INPUT32 {
    [FieldOffset(0)] public uint type;
    [FieldOffset(4)] public MOUSEINPUT mi;
    [FieldOffset(4)] public KEYBDINPUT ki;
}

[StructLayout(LayoutKind.Explicit)]
public struct INPUT64 {
    [FieldOffset(0)] public uint type;
    [FieldOffset(8)] public MOUSEINPUT mi;
    [FieldOffset(8)] public KEYBDINPUT ki;
}

[StructLayout(LayoutKind.Sequential)]
public struct MSG {
    public IntPtr hwnd;
    public uint message;
    public IntPtr wParam;
    public IntPtr lParam;
    public uint time;
    public int ptX, ptY;
}

// ── Win32 P/Invoke ─────────────────────────────────────────────────────────────

[StructLayout(LayoutKind.Sequential)]
public struct KBDLLHOOKSTRUCT {
    public uint   vkCode;
    public uint   scanCode;
    public uint   flags;
    public uint   time;
    public IntPtr dwExtraInfo;
}

public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

public static class WinAPI {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool LockWorkStation();
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int nIndex);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, int flags, int extra);
    [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, int dwExtraInfo);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", EntryPoint = "SendInput")] public static extern uint SendInput32(uint nInputs, INPUT32[] pInputs, int cbSize);
    [DllImport("user32.dll", EntryPoint = "SendInput")] public static extern uint SendInput64(uint nInputs, INPUT64[] pInputs, int cbSize);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool SetThreadDesktop(IntPtr hDesktop);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool CloseDesktop(IntPtr hDesktop);
    [DllImport("gdi32.dll")]  public static extern bool BitBlt(IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight, IntPtr hdcSrc, int nXSrc, int nYSrc, uint dwRop);
    [DllImport("gdi32.dll")]  public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")]  public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);
    [DllImport("gdi32.dll")]  public static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);
    [DllImport("gdi32.dll")]  public static extern bool DeleteObject(IntPtr hObject);
    [DllImport("gdi32.dll")]  public static extern bool DeleteDC(IntPtr hdc);
    // Screen capture protection (Win10 2004+ / Win11)
    // WDA_NONE = 0x00, WDA_EXCLUDEFROMCAPTURE = 0x11
    [DllImport("user32.dll", SetLastError = true)] public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);
    [DllImport("user32.dll", SetLastError = true)] public static extern uint GetWindowDisplayAffinity(IntPtr hWnd, out uint pdwAffinity);
    [DllImport("user32.dll")] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr child, string className, string windowName);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);
    // Global hotkey + message pump
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] public static extern bool GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);
    [DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG lpMsg);
    [DllImport("user32.dll")] public static extern IntPtr DispatchMessage(ref MSG lpMsg);
    // Key state polling (always works, no registration needed)
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vKey);
    // Window enumeration for overlay discovery
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)] public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);
    // Window style manipulation (for taskbar/Alt-Tab hiding)
    [DllImport("user32.dll")] public static extern int  GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] public static extern int  SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
    // Global keyboard hook (WH_KEYBOARD_LL = 13) — system-wide, works in background
    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)] public static extern IntPtr GetModuleHandle(string lpModuleName);
    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool   UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern IntPtr FindWindow(string cls, string win);
    [DllImport("user32.dll")] public static extern bool   PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool   SetForegroundWindow(IntPtr hWnd);
}

public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);




// ── Silent Input & Keystroke Simulation ────────────────────────────────────────

public class SilentInput {
    public static void AttachInputDesktop() {
        try {
            IntPtr hDesk = WinAPI.OpenInputDesktop(0, false, 0x01FF);
            if (hDesk != IntPtr.Zero) {
                WinAPI.SetThreadDesktop(hDesk);
                WinAPI.CloseDesktop(hDesk);
            }
        } catch { }
    }

    public static void TypeText(string text) {
        if (string.IsNullOrEmpty(text)) return;
        AttachInputDesktop();
        bool is64 = (IntPtr.Size == 8);
        for (int i = 0; i < text.Length; i++) {
            char c = text[i];
            if (c == '\r' || c == '\n') {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                SendKey("enter");
                Thread.Sleep(30);
                SendKey("shift-home");
                SendKey("backspace");
                continue;
            }
            if (is64) {
                INPUT64[] inputs = new INPUT64[2];
                inputs[0].type = 1; inputs[0].ki.wScan = (ushort)c; inputs[0].ki.dwFlags = 0x0004u;
                inputs[1].type = 1; inputs[1].ki.wScan = (ushort)c; inputs[1].ki.dwFlags = 0x0004u | 0x0002u;
                WinAPI.SendInput64(2, inputs, Marshal.SizeOf(typeof(INPUT64)));
            } else {
                INPUT32[] inputs = new INPUT32[2];
                inputs[0].type = 1; inputs[0].ki.wScan = (ushort)c; inputs[0].ki.dwFlags = 0x0004u;
                inputs[1].type = 1; inputs[1].ki.wScan = (ushort)c; inputs[1].ki.dwFlags = 0x0004u | 0x0002u;
                WinAPI.SendInput32(2, inputs, Marshal.SizeOf(typeof(INPUT32)));
            }
        }
    }

    public static void SendKey(string key) {
        if (string.IsNullOrEmpty(key)) return;
        AttachInputDesktop();
        key = key.ToLower().Trim();
        byte vk = 0; bool ctrl = false, shift = false;
        if      (key == "enter" || key == "return")   vk = 0x0D;
        else if (key == "backspace" || key == "back") vk = 0x08;
        else if (key == "tab")    vk = 0x09;
        else if (key == "escape" || key == "esc") vk = 0x1B;
        else if (key == "space")  vk = 0x20;
        else if (key == "left")   vk = 0x25; else if (key == "up")   vk = 0x26;
        else if (key == "right")  vk = 0x27; else if (key == "down") vk = 0x28;
        else if (key == "delete" || key == "del") vk = 0x2E;
        else if (key == "capslock" || key == "caps") vk = 0x14;
        else if (key == "home")  vk = 0x24;
        else if (key == "shifthome" || key == "shift-home") { shift = true; vk = 0x24; }
        else if (key == "end")   vk = 0x23;
        else if (key == "shiftend" || key == "shift-end") { shift = true; vk = 0x23; }
        else if (key == "pageup")   vk = 0x21; else if (key == "pagedown") vk = 0x22;
        else if (key == "insert")   vk = 0x2D;
        else if (key == "copy")     { ctrl = true; vk = 0x43; }
        else if (key == "paste")    { ctrl = true; vk = 0x56; }
        else if (key == "cut")      { ctrl = true; vk = 0x58; }
        else if (key == "selectall"){ ctrl = true; vk = 0x41; }
        else if (key == "undo")     { ctrl = true; vk = 0x5A; }
        else if (key == "f1")  vk = 0x70; else if (key == "f2")  vk = 0x71;
        else if (key == "f3")  vk = 0x72; else if (key == "f4")  vk = 0x73;
        else if (key == "f5")  vk = 0x74; else if (key == "f6")  vk = 0x75;
        else if (key == "f7")  vk = 0x76; else if (key == "f8")  vk = 0x77;
        else if (key == "f9")  vk = 0x78; else if (key == "f10") vk = 0x79;
        else if (key == "f11") vk = 0x7A; else if (key == "f12") vk = 0x7B;
        if (vk != 0) {
            if (ctrl)  WinAPI.keybd_event(0x11, 0, 0, 0);
            if (shift) WinAPI.keybd_event(0x10, 0, 0, 0);
            WinAPI.keybd_event(vk, 0, 0, 0);
            WinAPI.keybd_event(vk, 0, 2, 0);
            if (shift) WinAPI.keybd_event(0x10, 0, 2, 0);
            if (ctrl)  WinAPI.keybd_event(0x11, 0, 2, 0);
        }
    }

    public static void SendMouseInput(uint dx, uint dy, uint dwFlags, uint mouseData) {
        try {
            bool is64 = (IntPtr.Size == 8);
            if (is64) {
                INPUT64[] inputs = new INPUT64[1];
                inputs[0].type = 0; // INPUT_MOUSE
                inputs[0].mi.dx = (int)dx;
                inputs[0].mi.dy = (int)dy;
                inputs[0].mi.dwFlags = dwFlags;
                inputs[0].mi.mouseData = mouseData;
                WinAPI.SendInput64(1, inputs, Marshal.SizeOf(typeof(INPUT64)));
            } else {
                INPUT32[] inputs = new INPUT32[1];
                inputs[0].type = 0; // INPUT_MOUSE
                inputs[0].mi.dx = (int)dx;
                inputs[0].mi.dy = (int)dy;
                inputs[0].mi.dwFlags = dwFlags;
                inputs[0].mi.mouseData = mouseData;
                WinAPI.SendInput32(1, inputs, Marshal.SizeOf(typeof(INPUT32)));
            }
        } catch {
            WinAPI.mouse_event(dwFlags, (int)dx, (int)dy, mouseData, 0);
        }
    }

    public static void MoveMouse(double rawX, double rawY) {
        if (rawX < 0 || rawY < 0) return;
        AttachInputDesktop();
        int sw = WinAPI.GetSystemMetrics(0); if (sw <= 0) sw = 1920;
        int sh = WinAPI.GetSystemMetrics(1); if (sh <= 0) sh = 1080;

        int x = (rawX > 1.0) ? (int)Math.Round(rawX) : (int)Math.Round(rawX * (sw - 1));
        int y = (rawY > 1.0) ? (int)Math.Round(rawY) : (int)Math.Round(rawY * (sh - 1));
        x = Math.Max(0, Math.Min(sw - 1, x));
        y = Math.Max(0, Math.Min(sh - 1, y));

        WinAPI.SetCursorPos(x, y);

        uint dxNorm = (uint)((x * 65535.0) / (sw > 1 ? sw - 1 : 1));
        uint dyNorm = (uint)((y * 65535.0) / (sh > 1 ? sh - 1 : 1));
        SendMouseInput(dxNorm, dyNorm, 0x8001u /* MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE */, 0);
    }

    public static void ClickMouse(string button, double rawX, double rawY, bool doubleClick = false) {
        AttachInputDesktop();
        if (rawX >= 0 && rawY >= 0) {
            MoveMouse(rawX, rawY);
            Thread.Sleep(20);
        }

        bool right = (button != null && button.ToLower() == "right");
        bool middle = (button != null && button.ToLower() == "middle");
        uint downFlag = right ? 0x0008u : (middle ? 0x0020u : 0x0002u);
        uint upFlag   = right ? 0x0010u : (middle ? 0x0040u : 0x0004u);

        SendMouseInput(0, 0, downFlag, 0);
        Thread.Sleep(30);
        SendMouseInput(0, 0, upFlag, 0);

        if (doubleClick) {
            Thread.Sleep(80);
            SendMouseInput(0, 0, downFlag, 0);
            Thread.Sleep(30);
            SendMouseInput(0, 0, upFlag, 0);
        }
    }

    public static void ScrollMouse(int delta) {
        AttachInputDesktop();
        SendMouseInput(0, 0, 0x0800u /* MOUSEEVENTF_WHEEL */, unchecked((uint)(delta * 120)));
    }

    public static string Screenshot(int quality = 38) {
        int sw = WinAPI.GetSystemMetrics(0); int sh = WinAPI.GetSystemMetrics(1);
        if (sw <= 0) sw = 1920; if (sh <= 0) sh = 1080;
        int targetW = Math.Min(960, sw);
        int targetH = (int)((long)sh * targetW / sw);

        IntPtr hInputDesk = IntPtr.Zero;
        try {
            try {
                hInputDesk = WinAPI.OpenInputDesktop(0, false, 0x01FF);
                if (hInputDesk != IntPtr.Zero) WinAPI.SetThreadDesktop(hInputDesk);
            } catch { }

            IntPtr hDeskDC = WinAPI.GetDC(IntPtr.Zero);
            if (hDeskDC == IntPtr.Zero) return "";

            IntPtr hMemDC  = WinAPI.CreateCompatibleDC(hDeskDC);
            IntPtr hBmp    = WinAPI.CreateCompatibleBitmap(hDeskDC, sw, sh);
            IntPtr hOldBmp = WinAPI.SelectObject(hMemDC, hBmp);
            uint rop = 0x00CC0020u; // SRCCOPY — does not blink or hide the mouse cursor
            WinAPI.BitBlt(hMemDC, 0, 0, sw, sh, hDeskDC, 0, 0, rop);

            string b64 = "";
            using (Bitmap rawBmp     = Bitmap.FromHbitmap(hBmp))
            using (Bitmap resizedBmp = new Bitmap(rawBmp, new Size(targetW, targetH)))
            using (MemoryStream ms   = new MemoryStream()) {
                ImageCodecInfo jpegCodec = null;
                foreach (var codec in ImageCodecInfo.GetImageEncoders())
                    if (codec.FormatID == ImageFormat.Jpeg.Guid) { jpegCodec = codec; break; }
                if (jpegCodec != null) {
                    var ep = new EncoderParameters(1);
                    ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
                    resizedBmp.Save(ms, jpegCodec, ep);
                } else { resizedBmp.Save(ms, ImageFormat.Jpeg); }
                b64 = "data:image/jpeg;base64," + Convert.ToBase64String(ms.ToArray());
            }
            WinAPI.SelectObject(hMemDC, hOldBmp);
            WinAPI.DeleteObject(hBmp); WinAPI.DeleteDC(hMemDC);
            WinAPI.ReleaseDC(IntPtr.Zero, hDeskDC);
            return b64;
        } catch { return ""; }
        finally {
            if (hInputDesk != IntPtr.Zero) try { WinAPI.CloseDesktop(hInputDesk); } catch { }
        }
    }
}



// ── Host Configuration ─────────────────────────────────────────────────────────

public static class HostConfig {
    public static volatile string RelayUrl   = "https://ex-5d5f.onrender.com";
    public static volatile string AuthToken  = "";
    public static volatile string GeminiKey  = "";
    public static volatile string ChatgptKey = "";
    public static volatile string ClaudeKey  = "";
    public static volatile string GrokKey    = "";
    public static volatile string GroqKey    = "";
    public static volatile string Provider   = "gemini";

    public static string CfgPath() {
        string dir = Path.GetDirectoryName(
            System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".";
        return Path.Combine(dir, "host_config.json");
    }

    public static void Load() {
        try {
            string path = CfgPath();
            if (!File.Exists(path)) return;
            string json = File.ReadAllText(path);
            string url  = Jget(json, "relayUrl");
            string tok  = Jget(json, "authToken");
            string gkey = Jget(json, "geminiKey");
            string ckey = Jget(json, "chatgptKey");
            string clk  = Jget(json, "claudeKey");
            string rkey = Jget(json, "grokKey");
            string qkey = Jget(json, "groqKey");
            string prov = Jget(json, "provider");
            if (tok != null) AuthToken = tok;
            if (!string.IsNullOrEmpty(url))  RelayUrl   = url;
            if (!string.IsNullOrEmpty(gkey)) GeminiKey  = gkey;
            if (!string.IsNullOrEmpty(ckey)) ChatgptKey = ckey;
            if (!string.IsNullOrEmpty(clk))  ClaudeKey  = clk;
            if (!string.IsNullOrEmpty(rkey)) GrokKey    = rkey;
            if (!string.IsNullOrEmpty(qkey)) GroqKey    = qkey;
            string lastVerStr = Jget(json, "lastVerifiedAt");
            long lv;
            if (long.TryParse(lastVerStr, out lv)) LastVerifiedAt = lv;
        } catch {}
    }

    public static long LastVerifiedAt = 0;
    static readonly object _verLock = new object();

    public static bool VerifyTokenGate() {
        if (string.IsNullOrEmpty(AuthToken)) return false;

        long now = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
        long lastVer;
        lock (_verLock) { lastVer = LastVerifiedAt; }
        long diff = now - lastVer;
        long twentyFourHours = 24L * 60L * 60L * 1000L;

        // If verified within 24 hours, permit immediately
        if (lastVer > 0 && diff < twentyFourHours && diff >= 0) {
            return true;
        }

        // Must verify against NeonDB via server
        try {
            using (var client = new System.Net.WebClient()) {
                client.Headers[System.Net.HttpRequestHeader.ContentType] = "application/json";
                string payload = "{\"token\":\"" + JsonEsc(AuthToken.Trim()) + "\"}";
                string resp = client.UploadString("https://ex-5d5f.onrender.com/api/auth/verify-token", payload);
                if (!string.IsNullOrEmpty(resp) && resp.Contains("\"success\":true")) {
                    lock (_verLock) { LastVerifiedAt = now; }
                    Save();
                    return true;
                }
            }
        } catch (System.Net.WebException wex) {
            var httpResp = wex.Response as System.Net.HttpWebResponse;
            if (httpResp != null && (httpResp.StatusCode == System.Net.HttpStatusCode.Forbidden || httpResp.StatusCode == System.Net.HttpStatusCode.Unauthorized || httpResp.StatusCode == System.Net.HttpStatusCode.BadRequest)) {
                // Explicit rejection from DB — strictly block and clear verification
                lock (_verLock) { LastVerifiedAt = 0; }
                Save();
                return false;
            }
            // Offline/timeout network error only — allow fallback if previously verified
            if (lastVer > 0 && diff < twentyFourHours) return true;
        } catch {
            if (lastVer > 0 && diff < twentyFourHours) return true;
        }
        return false;
    }

    public static void Save(string relayUrl = null, string authToken = null) {
        try {
            if (relayUrl != null) RelayUrl = relayUrl;
            if (authToken != null) {
                AuthToken = authToken;
                if (string.IsNullOrEmpty(authToken)) {
                    lock (_verLock) { LastVerifiedAt = 0; }
                }
            }
            File.WriteAllText(CfgPath(),
                "{\"relayUrl\":\""       + JsonEsc(RelayUrl)   + "\"," +
                 "\"authToken\":\""      + JsonEsc(AuthToken)  + "\"," +
                 "\"lastVerifiedAt\":"   + LastVerifiedAt      + "," +
                 "\"geminiKey\":\""      + JsonEsc(GeminiKey)  + "\"," +
                 "\"chatgptKey\":\""     + JsonEsc(ChatgptKey) + "\"," +
                 "\"claudeKey\":\""      + JsonEsc(ClaudeKey)  + "\"," +
                 "\"grokKey\":\""        + JsonEsc(GrokKey)    + "\"," +
                 "\"groqKey\":\""        + JsonEsc(GroqKey)    + "\"," +
                 "\"provider\":\""       + JsonEsc(Provider)   + "\"}");
        } catch {}
    }

    public static string Jget(string json, string key) {
        string sr = "\"" + key + "\"";
        int ki = json.IndexOf(sr);      if (ki < 0) return null;
        int ci = json.IndexOf(':', ki + sr.Length); if (ci < 0) return null;
        int qi = json.IndexOf('"', ci + 1);         if (qi < 0) return null;
        int qe = qi + 1;
        while (qe < json.Length) {
            if (json[qe] == '"') {
                int bs = 0, t = qe - 1; while (t >= qi && json[t] == '\\') { bs++; t--; }
                if (bs % 2 == 0) break;
            }
            qe++;
        }
        if (qe >= json.Length) return null;
        return json.Substring(qi + 1, qe - qi - 1);
    }

    // Extracts a numeric value — handles both quoted "123" and bare 123 JSON values.
    public static double JgetNum(string json, string key, double def = 0) {
        string sr = "\"" + key + "\"";
        int ki = json.IndexOf(sr); if (ki < 0) return def;
        int ci = json.IndexOf(':', ki + sr.Length); if (ci < 0) return def;
        int vi = ci + 1;
        while (vi < json.Length && (json[vi] == ' ' || json[vi] == '\t')) vi++;
        if (vi >= json.Length) return def;
        // Quoted string — strip quotes then parse
        if (json[vi] == '"') {
            string s = Jget(json, key);
            double r2;
            if (s != null && double.TryParse(s, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out r2)) return r2;
            return def;
        }
        // Bare number (int or float, possibly negative)
        int end = vi;
        if (end < json.Length && json[end] == '-') end++;
        while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '.')) end++;
        if (end == vi) return def;
        double val;
        if (double.TryParse(json.Substring(vi, end - vi), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out val)) return val;
        return def;
    }

    public static string JsonEsc(string s) {
        if (s == null) return "";
        return s.Replace("\\","\\\\").Replace("\"","\\\"")
                .Replace("\n","\\n").Replace("\r","\\r").Replace("\t","\\t");
    }
}

// ── Auto-Typing Manager ────────────────────────────────────────────────────────

public static class AutoTyper {
    static CancellationTokenSource _cts;
    static volatile int _speed = 3;

    public static void Start(string text, int speed, string id) {
        Stop();
        if (string.IsNullOrEmpty(text)) return;
        _speed = Math.Max(1, speed);
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        Task.Run(async () => {
            try {
                int i = 0;
                while (i < text.Length && !token.IsCancellationRequested) {
                    int delayMs = 1000 / _speed;
                    char c = text[i];
                    SilentInput.TypeText(c.ToString());
                    i++;
                    await Task.Delay(delayMs, token);
                }
            } catch (OperationCanceledException) {}
            catch {}
        });
    }

    public static void Stop() {
        if (_cts != null) {
            try { _cts.Cancel(); _cts.Dispose(); } catch {}
            _cts = null;
        }
    }

    public static void SetSpeed(int speed) {
        _speed = Math.Max(1, speed);
    }
}

// ── Standalone Socket.IO Relay Client ──────────────────────────────────────────
// Connects directly to the relay server — no browser required.
// Speaks Socket.IO v4 (EIO=4) over a raw ClientWebSocket.

public static class RelayClient {
    static volatile bool _active    = true;
    static volatile bool _streaming = false;
    static readonly object _wsLock  = new object();
    static ClientWebSocket _currentWs = null;

    public static void Start() {
        var t = new Thread(RunLoop) { IsBackground = false, Name = "RelayClient" };
        t.Start();
    }

    static void RunLoop() {
        while (_active) {
            StopStreaming();
            try { ConnectAndProcess().GetAwaiter().GetResult(); }
            catch { }
            if (_active) Thread.Sleep(2000); // reconnect delay — kept short to reclaim relay fast

        }
    }

    public static void Reconnect() {
        lock (_wsLock) {
            if (_currentWs != null) {
                try { _currentWs.Abort(); } catch {}
                _currentWs = null;
            }
        }
        StopStreaming();
    }

    // ── Screenshot streaming loop ──────────────────────────────────────────
    static void StartStreaming() {
        StopStreaming();
        _streaming = true;
        var t = new Thread(StreamLoop) { IsBackground = true, Name = "StreamLoop" };
        t.Start();
    }

    static void StopStreaming() {
        _streaming = false;
    }

    static void StreamLoop() {
        while (_streaming) {
            try {
                ClientWebSocket ws;
                lock (_wsLock) { ws = _currentWs; }
                if (ws == null || ws.State != WebSocketState.Open) {
                    Thread.Sleep(100);
                    continue; // ws dead — wait, RunLoop will reconnect
                }

                string dataUrl = SilentInput.Screenshot(38);
                if (!string.IsNullOrEmpty(dataUrl) && dataUrl.Length > 50) {
                    string pkt = "42[\"to-mobile\",{\"deviceId\":\"jarvistype-host\",\"event\":\"peek:data\",\"payload\":{\"image\":\"" + dataUrl + "\"}}]";
                    lock (_wsLock) {
                        if (ws.State == WebSocketState.Open)
                            WsSend(ws, pkt).GetAwaiter().GetResult();
                    }
                }
            } catch { Thread.Sleep(100); continue; } // transient error — keep streaming, don't die
            Thread.Sleep(150); // ~6 FPS
        }
    }

    static async Task ConnectAndProcess() {
        string tok = HostConfig.AuthToken;
        if (string.IsNullOrEmpty(tok) || !HostConfig.VerifyTokenGate()) {
            // Token is empty (logged out) or unverified in NeonDB — block connection
            await Task.Delay(2000).ConfigureAwait(false);
            return;
        }

        string relayUrl = HostConfig.RelayUrl;
        string wsBase = relayUrl.StartsWith("https://")
            ? "wss://" + relayUrl.Substring(8)
            : "ws://"  + relayUrl.Substring(7);
        string wsUrl = wsBase + "/socket.io/?EIO=4&transport=websocket";

        using (var ws = new ClientWebSocket()) {
            ws.Options.SetRequestHeader("Origin", relayUrl);
            await ws.ConnectAsync(new Uri(wsUrl), CancellationToken.None).ConfigureAwait(false);
            lock (_wsLock) { _currentWs = ws; }

            await WsReceive(ws).ConfigureAwait(false); // discard server hello (EIO open)

            // ── Socket.IO CONNECT with auth ────────────────────────────────
            string authField = tok.Length > 20
                ? "\"token\":\"" + HostConfig.JsonEsc(tok) + "\""
                : "\"password\":\"" + HostConfig.JsonEsc(tok) + "\"";
            await WsSend(ws, "40{\"auth\":{" + authField + ",\"deviceId\":\"jarvistype-host\"}}").ConfigureAwait(false);
            await WsReceive(ws).ConfigureAwait(false); // discard ack

            // ── Identify as desktop ────────────────────────────────────────
            await WsEmit(ws, "identify",
                "{\"type\":\"desktop\",\"pairingToken\":\"" + HostConfig.JsonEsc(tok) +
                "\",\"deviceId\":\"jarvistype-host\",\"hostname\":\"host-exe\",\"username\":\"user\"}")
                .ConfigureAwait(false);

            var cts = new CancellationTokenSource();
            Task ignore = HeartbeatAsync(ws, cts.Token);

            try {
                while (_active && ws.State == WebSocketState.Open) {
                    string pkt = await WsReceive(ws).ConfigureAwait(false);
                    if (pkt == null) break;
                    ProcessPacket(ws, pkt);
                }
            } finally {
                StopStreaming();
                cts.Cancel();
                lock (_wsLock) { _currentWs = null; }
            }
        }
    }

    static void ProcessPacket(ClientWebSocket ws, string pkt) {
        // EIO PING → PONG
        if (pkt == "2") { Task ignore = WsSend(ws, "3"); return; }
        if (!pkt.StartsWith("42")) return;

        // Parse: 42["event-name", {...}]
        string body = pkt.Substring(2);
        if (!body.StartsWith("[\"")) return;
        int q2 = body.IndexOf('"', 2); if (q2 < 0) return;
        string evtName = body.Substring(2, q2 - 2);

        // ── Stream control events ──────────────────────────────────────────
        if (evtName == "mobile-connected" || evtName == "relay:mobile-connected") {
            StartStreaming();
            return;
        }
        if (evtName == "mobile-disconnected" || evtName == "relay:mobile-disconnected"
            || evtName == "session-expired") {
            StopStreaming();
            return;
        }
        // Extension tried to take over — close this socket immediately so RunLoop
        // reconnects in 5s and reclaims the relay session for standalone.
        if (evtName == "relay:conflict") {
            StopStreaming();
            lock (_wsLock) { if (_currentWs != null) try { _currentWs.Abort(); } catch {} }
            return;
        }

        // peek requests: send one immediate frame
        if (evtName == "command:peek" || evtName == "command:camera") {
            Task ignore2 = Task.Run(() => {
                string dataUrl = SilentInput.Screenshot(38);
                if (!string.IsNullOrEmpty(dataUrl) && dataUrl.Length > 50) {
                    string pkt2 = "42[\"to-mobile\",{\"deviceId\":\"jarvistype-host\",\"event\":\"peek:data\",\"payload\":{\"image\":\"" + dataUrl + "\"}}]";
                    lock (_wsLock) {
                        if (ws.State == WebSocketState.Open)
                            WsSend(ws, pkt2).GetAwaiter().GetResult();
                    }
                }
            });
            return;
        }

        int comma = body.IndexOf(',', q2);
        string dataJson = comma >= 0
            ? body.Substring(comma + 1).TrimEnd(']', ' ')
            : "{}";

        string cmdJson = MapEvent(evtName, dataJson);
        if (cmdJson != null) {
            try { Program.HandleCommand(cmdJson); } catch { }
        } else {
            HandleMultiStep(evtName, dataJson);
        }
    }

    static string MapEvent(string evt, string data) {
        switch (evt) {
            case "command:move-mouse":    return "{\"cmd\":\"move\","           + data.TrimStart('{');
            case "command:click":         return "{\"cmd\":\"click\","          + data.TrimStart('{');
            case "command:dblclick":      return "{\"cmd\":\"dblclick\","       + data.TrimStart('{');
            case "command:scroll":        return "{\"cmd\":\"scroll\","         + data.TrimStart('{');
            case "command:type":          return "{\"cmd\":\"type\","           + data.TrimStart('{');
            case "command:key":
                return "{\"cmd\":\"key\"," + data.Replace("\"action\":", "\"key\":").TrimStart('{');
            case "command:clipboard-set": return "{\"cmd\":\"clipboard-set\","  + data.TrimStart('{');
            case "command:clipboard-get": return "{\"cmd\":\"clipboard-get\"}";
            case "command:lock":          return "{\"cmd\":\"lock\"}";
            case "command:media":         return "{\"cmd\":\"media\","          + data.TrimStart('{');
            case "command:mic-start":     return "{\"cmd\":\"mic-start\"}";
            case "command:mic-stop":      return "{\"cmd\":\"mic-stop\"}";
            default: return null;
        }
    }

    static void HandleMultiStep(string evt, string data) {
        string text;
        switch (evt) {
            case "command:pasteline":
                text = HostConfig.Jget(data, "text") ?? "";
                Program.HandleCommand("{\"cmd\":\"clipboard-set\",\"text\":\"" + HostConfig.JsonEsc(text) + "\"}");
                Thread.Sleep(150);
                Program.HandleCommand("{\"cmd\":\"key\",\"key\":\"paste\"}");
                Thread.Sleep(150);
                Program.HandleCommand("{\"cmd\":\"key\",\"key\":\"enter\"}");
                break;
            case "command:pasteline-noeol":
                text = HostConfig.Jget(data, "text") ?? "";
                Program.HandleCommand("{\"cmd\":\"clipboard-set\",\"text\":\"" + HostConfig.JsonEsc(text) + "\"}");
                Thread.Sleep(150);
                Program.HandleCommand("{\"cmd\":\"key\",\"key\":\"paste\"}");
                break;
        }
    }

    // EIO heartbeat: respond to server "2" (ping) with "3" (pong).
    static async Task HeartbeatAsync(ClientWebSocket ws, CancellationToken ct) {
        try {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open) {
                await Task.Delay(20000, ct).ConfigureAwait(false);
                if (!ct.IsCancellationRequested)
                    await WsSend(ws, "3").ConfigureAwait(false);
            }
        } catch { }
    }

    static async Task<string> WsReceive(ClientWebSocket ws) {
        var buf = new byte[524288]; // 512 KB — large enough for a screenshot frame
        var sb  = new System.Text.StringBuilder();
        WebSocketReceiveResult res;
        do {
            res = await ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None).ConfigureAwait(false);
            if (res.MessageType == WebSocketMessageType.Close) return null;
            sb.Append(Encoding.UTF8.GetString(buf, 0, res.Count));
        } while (!res.EndOfMessage);
        return sb.ToString();
    }

    static async Task WsSend(ClientWebSocket ws, string msg) {
        var b = Encoding.UTF8.GetBytes(msg);
        await ws.SendAsync(new ArraySegment<byte>(b),
            WebSocketMessageType.Text, true, CancellationToken.None)
            .ConfigureAwait(false);
    }

    static async Task WsEmit(ClientWebSocket ws, string evtName, string dataJson) {
        await WsSend(ws, "42[\"" + evtName + "\"," + dataJson + "]").ConfigureAwait(false);
    }
}

// ── Elevated Input IPC Service ─────────────────────────────────────────────────
// Allows un-elevated Chrome native host instances to forward UI actions (mouse,
// keyboard) to the elevated background host process, bypassing Windows UIPI on
// TaskManager, Antigravity, Taskbar, and Administrator windows.

public static class ElevatedInputService {
    public const string PIPE_NAME = "JarvisType_Elevated_Input";
    static volatile bool _serverRunning = false;

    public static void StartServer() {
        if (_serverRunning) return;
        _serverRunning = true;
        var t = new Thread(ServerLoop) { IsBackground = true, Name = "ElevatedPipeServer" };
        t.Start();
    }

    static void ServerLoop() {
        while (_serverRunning) {
            try {
                PipeSecurity ps = new PipeSecurity();
                ps.AddAccessRule(new PipeAccessRule(
                    new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                    PipeAccessRights.ReadWrite,
                    AccessControlType.Allow));

                using (var server = new NamedPipeServerStream(
                    PIPE_NAME,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.None,
                    4096,
                    4096,
                    ps)) {

                    while (_serverRunning) {
                        try {
                            server.WaitForConnection();
                            byte[] buffer = new byte[8192];
                            int read = server.Read(buffer, 0, buffer.Length);
                            if (read > 0) {
                                string line = Encoding.UTF8.GetString(buffer, 0, read).TrimEnd('\r', '\n', '\0');
                                if (!string.IsNullOrEmpty(line)) {
                                    string resp = Program.ExecuteCommandLocally(line) ?? "{\"ok\":true}";
                                    byte[] respBytes = Encoding.UTF8.GetBytes(resp + "\n");
                                    server.Write(respBytes, 0, respBytes.Length);
                                    server.Flush();
                                }
                            }
                            server.Disconnect();
                        } catch {
                            try { server.Disconnect(); } catch {}
                        }
                    }
                }
            } catch {
                Thread.Sleep(200);
            }
        }
    }

    public static string TryForwardToElevated(string json) {
        NamedPipeClientStream client = null;
        try {
            client = new NamedPipeClientStream(".", PIPE_NAME, PipeDirection.InOut);
            client.Connect(80);
            byte[] msgBytes = Encoding.UTF8.GetBytes(json + "\n");
            client.Write(msgBytes, 0, msgBytes.Length);
            client.Flush();

            byte[] respBuf = new byte[8192];
            int r = client.Read(respBuf, 0, respBuf.Length);
            if (r > 0) {
                return Encoding.UTF8.GetString(respBuf, 0, r).TrimEnd('\r', '\n', '\0');
            }
            return "{\"ok\":true}";
        } catch {
            return null; // pipe not available — run in current process
        } finally {
            if (client != null) {
                try { client.Close(); } catch {}
            }
        }
    }
}

// ── Native Messaging Protocol (Chrome Extension Bridge) & Entry Point ──────────

class Program {
    static Stream stdin, stdout;

    static void Main(string[] args) {
        try { WinAPI.SetProcessDPIAware(); } catch {}
        System.Net.ServicePointManager.SecurityProtocol =
            (System.Net.SecurityProtocolType)(3072 | 768 | 192);
        System.Net.ServicePointManager.ServerCertificateValidationCallback =
            (s, cert, chain, err) => true;

        bool standalone = args != null && Array.Exists(args, a =>
            a == "--standalone" || a == "-standalone");

        if (standalone) {
            // Standalone: relay-only mode. InterviewAssistant.exe manages its own
            // WH_KEYBOARD_LL hook, overlay, and hotkeys — WinSpeechHost just bridges Chrome.
            SelfRegisterNativeHost();
            HostConfig.Load();

            int myPid = Process.GetCurrentProcess().Id;
            foreach (var p in Process.GetProcessesByName("WinSpeechHost")) {
                if (p.Id != myPid) { try { p.Kill(); p.WaitForExit(2000); } catch {} }
            }

            // Start elevated IPC server to accept mouse/keyboard commands from un-elevated extension instances
            ElevatedInputService.StartServer();

            // 24-Hour Token Gate: Verify token against NeonDB before starting relay
            bool isTokenValid = HostConfig.VerifyTokenGate();
            if (isTokenValid) {
                RelayClient.Start();
            }

            // Launch RuntimeHost (overlay) if not already running
            try {
                string exeDir = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                string iaExe  = System.IO.Path.Combine(exeDir, "RuntimeHost.exe");
                if (System.IO.File.Exists(iaExe) && Process.GetProcessesByName("RuntimeHost").Length == 0) {
                    ProcessStartInfo psi2 = new ProcessStartInfo(iaExe) { WorkingDirectory = exeDir, UseShellExecute = false };
                    Process.Start(psi2);
                }
            } catch {}

            // Keep alive — relay client runs on its own thread
            Thread.Sleep(Timeout.Infinite);

        } else {
            // Native messaging mode: Chrome kills host if it takes >~2s to read stdin.
            // SelfRegisterNativeHost calls schtasks.exe (3s timeout) — MUST run in background.
            HostConfig.Load();
            var regThread = new Thread(SelfRegisterNativeHost) { IsBackground = true, Name = "Register" };
            regThread.Start();

            stdin  = Console.OpenStandardInput();
            stdout = Console.OpenStandardOutput();
            while (true) {
                try {
                    string json = ReadMessage();
                    if (json == null) break;
                    WriteMessage(HandleCommand(json));
                } catch (Exception ex) {
                    WriteMessage("{\"error\":\"" + ex.Message.Replace("\"","'") + "\"}");
                }
            }
            // Chrome closed — wait for registration to finish, then take over relay
            regThread.Join(5000);
            Thread.Sleep(500);
            ElevatedInputService.StartServer();
            RelayClient.Start();
            Thread.Sleep(Timeout.Infinite);
        }
    }


    // Writes HKCU native messaging host registration so Chrome finds WinSpeechHost
    // on any Windows user account without admin rights or separate setup scripts.
    // Also adds Windows startup Run key + Task Scheduler watchdog for exam browser bypass.
    static void SelfRegisterNativeHost() {
        try {
            string exePath  = System.Reflection.Assembly.GetExecutingAssembly().Location;
            string exeDir   = System.IO.Path.GetDirectoryName(exePath);
            string manifest = System.IO.Path.Combine(exeDir, "host-manifest.json");

            // Write/update manifest JSON pointing to this exe's actual location
            string json = "{\r\n" +
                "    \"name\": \"com.jarvistype.host\",\r\n" +
                "    \"description\": \"JarvisType Native Host - full Windows control\",\r\n" +
                "    \"path\": \"" + exePath.Replace("\\", "\\\\") + "\",\r\n" +
                "    \"type\": \"stdio\",\r\n" +
                "    \"allowed_origins\": [\r\n" +
                "        \"chrome-extension://hejbleljjhhjbfdfoemknnbiojjenbnf/\",\r\n" +
                "        \"chrome-extension://peijjbnbncfednpgnamjaggbmikgkdfe/\"\r\n" +
                "    ]\r\n" +
                "}\r\n";
            System.IO.File.WriteAllText(manifest, json, Encoding.UTF8);

            // Register Chrome native messaging in HKCU (no admin needed)
            string regKey = @"Software\Google\Chrome\NativeMessagingHosts\com.jarvistype.host";
            using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(regKey)) {
                if (key != null) key.SetValue("", manifest);
            }

            // ── Auto-start on Windows login (HKCU Run key — no admin needed) ──
            // Ensures standalone mode is already running before exam browser starts.
            string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
            using (var rk = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(runKey)) {
                if (rk != null)
                    rk.SetValue("WinSpeechHost", "\"" + exePath + "\" --standalone");
            }

            // ── Task Scheduler watchdog: restart if killed every 2 minutes ──
            // Simple command-line form — no XML, works on all Windows versions.
            // /F overwrites if task already exists, /RL HIGHEST runs with highest privileges,
            // /SC MINUTE /MO 2 = every 2 minutes.
            string taskCmd = "/Create /F /TN \"WinSpeechHostWatchdog\" " +
                "/SC MINUTE /MO 2 " +
                "/TR \"\\\"" + exePath.Replace("\"","\\\"") + "\\\" --standalone\" " +
                "/RL HIGHEST";
            var psi = new System.Diagnostics.ProcessStartInfo("schtasks.exe", taskCmd) {
                CreateNoWindow  = true,
                UseShellExecute = false,
                WindowStyle     = System.Diagnostics.ProcessWindowStyle.Hidden
            };
            using (var p = System.Diagnostics.Process.Start(psi)) {
                if (p != null) p.WaitForExit(3000);
            }
        } catch { /* Non-fatal — standalone relay still works without registration */ }
    }



    static string ReadMessage() {
        byte[] lenBuf = new byte[4];
        int read = 0;
        while (read < 4) {
            int r = stdin.Read(lenBuf, read, 4 - read);
            if (r <= 0) return null;
            read += r;
        }
        int len = BitConverter.ToInt32(lenBuf, 0);
        if (len <= 0 || len > 10485760) return null;
        byte[] msgBuf = new byte[len];
        read = 0;
        while (read < len) {
            int r = stdin.Read(msgBuf, read, len - read);
            if (r <= 0) return null;
            read += r;
        }
        return Encoding.UTF8.GetString(msgBuf);
    }

    static void WriteMessage(string json) {
        byte[] msg      = Encoding.UTF8.GetBytes(json);
        byte[] lenBytes = BitConverter.GetBytes(msg.Length);
        stdout.Write(lenBytes, 0, 4);
        stdout.Write(msg, 0, msg.Length);
        stdout.Flush();
    }

    public static string HandleCommand(string json) {
        string cmd = HostConfig.Jget(json, "cmd") ?? "";
        if (cmd == "click" || cmd == "dblclick" || cmd == "type" || cmd == "type-clipboard" ||
            cmd == "key" || cmd == "move" || cmd == "scroll" || cmd == "clipboard-set") {
            string forwarded = ElevatedInputService.TryForwardToElevated(json);
            if (forwarded != null) return forwarded;
        }
        return ExecuteCommandLocally(json);
    }

    public static string ExecuteCommandLocally(string json) {
        string cmd = HostConfig.Jget(json, "cmd") ?? "";

        if (cmd == "set-config") {
            string newUrl = HostConfig.Jget(json, "relayUrl");
            string newTok = HostConfig.Jget(json, "authToken");
            HostConfig.Save(newUrl, newTok);
            RelayClient.Reconnect();
            return "{\"ok\":true}";
        }

        if (cmd == "set-gemini-key" || cmd == "set-api-keys") {
            string gkey = HostConfig.Jget(json, "geminiKey");
            string ckey = HostConfig.Jget(json, "chatgptKey");
            string clk  = HostConfig.Jget(json, "claudeKey");
            string rkey = HostConfig.Jget(json, "grokKey");
            string qkey = HostConfig.Jget(json, "groqKey");
            if (!string.IsNullOrEmpty(gkey)) HostConfig.GeminiKey  = gkey;
            if (!string.IsNullOrEmpty(ckey)) HostConfig.ChatgptKey = ckey;
            if (!string.IsNullOrEmpty(clk))  HostConfig.ClaudeKey  = clk;
            if (!string.IsNullOrEmpty(rkey)) HostConfig.GrokKey    = rkey;
            if (!string.IsNullOrEmpty(qkey)) HostConfig.GroqKey    = qkey;
            HostConfig.Save();
            return "{\"ok\":true}";
        }

        int sw3 = WinAPI.GetSystemMetrics(0); if (sw3 <= 0) sw3 = 1920;
        int sh3 = WinAPI.GetSystemMetrics(1); if (sh3 <= 0) sh3 = 1080;

        switch (cmd) {
            case "screenshot":
            case "grab-screen": {
                string img = SilentInput.Screenshot(50);
                string grabId = HostConfig.Jget(json, "_grabId") ?? "";
                string grabIdField = grabId.Length > 0 ? ",\"_grabId\":\"" + HostConfig.JsonEsc(grabId) + "\"" : "";
                return "{\"image\":\"" + img + "\",\"dataUrl\":\"" + img + "\"" + grabIdField + "}";
            }
            case "type": {
                string text = HostConfig.Jget(json, "text") ?? "";
                SilentInput.TypeText(text);
                return "{\"ok\":true}";
            }
            case "type-clipboard": {
                // Clipboard-paste method — bypasses chrome-extension:// page restrictions
                // and works in any window that accepts Ctrl+V (Antigravity, exam browsers, etc.)
                string txt = HostConfig.Jget(json, "text") ?? "";
                if (txt.Length == 0) return "{\"ok\":true}";
                var ct = new Thread(() => { try { Clipboard.SetText(txt); } catch {} });
                ct.SetApartmentState(ApartmentState.STA); ct.Start(); ct.Join();
                Thread.Sleep(30);
                WinAPI.keybd_event(0x11, 0, 0, 0);           // VK_CONTROL down
                Thread.Sleep(15);
                WinAPI.keybd_event(0x56, 0, 0, 0);           // V down
                Thread.Sleep(15);
                WinAPI.keybd_event(0x56, 0, 0x0002, 0);      // V up
                Thread.Sleep(15);
                WinAPI.keybd_event(0x11, 0, 0x0002, 0);      // VK_CONTROL up
                return "{\"ok\":true}";
            }
            case "auto-type-start": {
                string text = HostConfig.Jget(json, "text") ?? "";
                string speedStr = HostConfig.Jget(json, "speed") ?? "3";
                string id = HostConfig.Jget(json, "id") ?? "";
                int speed = 3;
                int.TryParse(speedStr, out speed);
                AutoTyper.Start(text, speed, id);
                return "{\"ok\":true}";
            }
            case "auto-type-stop":
                AutoTyper.Stop();
                return "{\"ok\":true}";
            case "auto-type-speed": {
                string sstr = HostConfig.Jget(json, "speed") ?? "3";
                int spd = 3; int.TryParse(sstr, out spd);
                AutoTyper.SetSpeed(spd);
                return "{\"ok\":true}";
            }
            case "key": {
                string key = HostConfig.Jget(json, "key") ?? HostConfig.Jget(json, "action") ?? "";
                SilentInput.SendKey(key);
                return "{\"ok\":true}";
            }
            case "move": {
                double rawX = HostConfig.JgetNum(json, "x", -1);
                double rawY = HostConfig.JgetNum(json, "y", -1);
                SilentInput.MoveMouse(rawX, rawY);
                return "{\"ok\":true}";
            }
            case "click": {
                string cbtn = HostConfig.Jget(json, "button") ?? "left";
                double rawCx = HostConfig.JgetNum(json, "x", -1);
                double rawCy = HostConfig.JgetNum(json, "y", -1);
                SilentInput.ClickMouse(cbtn, rawCx, rawCy, false);
                return "{\"ok\":true}";
            }
            case "dblclick": {
                string dbtn = HostConfig.Jget(json, "button") ?? "left";
                double rawDx = HostConfig.JgetNum(json, "x", -1);
                double rawDy = HostConfig.JgetNum(json, "y", -1);
                SilentInput.ClickMouse(dbtn, rawDx, rawDy, true);
                return "{\"ok\":true}";
            }
            case "scroll": {
                int scrollDelta = (int)HostConfig.JgetNum(json, "delta");
                SilentInput.ScrollMouse(scrollDelta);
                return "{\"ok\":true}";
            }
            case "clipboard-set": {
                string clipText = HostConfig.Jget(json, "text") ?? "";
                var ct = new Thread(() => { try { Clipboard.SetText(clipText.Length > 0 ? clipText : " "); } catch {} });
                ct.SetApartmentState(ApartmentState.STA); ct.Start(); ct.Join();
                return "{\"ok\":true}";
            }
            case "media": {
                string action = HostConfig.Jget(json, "action") ?? "";
                byte vkm = 0;
                switch (action) {
                    case "play-pause": case "playpause": case "play": vkm = 0xB3; break;
                    case "next":       case "next-track":             vkm = 0xB0; break;
                    case "prev":       case "previous": case "prev-track": vkm = 0xB1; break;
                    case "stop":                                       vkm = 0xB2; break;
                    case "volume-up":  case "volumeup":                vkm = 0xAF; break;
                    case "volume-down":case "volumedown":              vkm = 0xAE; break;
                    case "mute":                                       vkm = 0xAD; break;
                }
                if (vkm != 0) { WinAPI.keybd_event(vkm, 0, 0, 0); WinAPI.keybd_event(vkm, 0, 2, 0); }
                return "{\"ok\":true}";
            }
            case "mic-start":
            case "mic-stop":
                return "{\"ok\":true}";
            case "lock":
                WinAPI.LockWorkStation();
                return "{\"ok\":true}";
            case "clipboard-get": {
                string clipContent = "";
                var cgt = new Thread(() => { try { clipContent = Clipboard.GetText(); } catch {} });
                cgt.SetApartmentState(ApartmentState.STA); cgt.Start(); cgt.Join();
                return "{\"clipboard\":\"" + HostConfig.JsonEsc(clipContent) + "\"}";
            }
            case "ping":
                return "{\"pong\":true}";
            case "protect-window": {
                // Make the foreground window (Chrome) invisible to screen capture
                // WDA_EXCLUDEFROMCAPTURE = 0x00000011 — works on Win10 2004+ and Win11
                // Zoom, OBS, Windows Snip, Print Screen all become blind to the window
                try {
                    IntPtr hWnd = WinAPI.GetForegroundWindow();
                    bool ok = hWnd != IntPtr.Zero && WinAPI.SetWindowDisplayAffinity(hWnd, 0x00000011u);
                    return "{\"ok\":" + (ok ? "true" : "false") + "}";
                } catch (Exception ex) {
                    return "{\"ok\":false,\"error\":\"" + HostConfig.JsonEsc(ex.Message) + "\"}";
                }
            }
            case "unprotect-window": {
                // Restore normal capture visibility (WDA_NONE = 0x00000000)
                try {
                    IntPtr hWnd = WinAPI.GetForegroundWindow();
                    bool ok = hWnd != IntPtr.Zero && WinAPI.SetWindowDisplayAffinity(hWnd, 0x00000000u);
                    return "{\"ok\":" + (ok ? "true" : "false") + "}";
                } catch (Exception ex) {
                    return "{\"ok\":false,\"error\":\"" + HostConfig.JsonEsc(ex.Message) + "\"}";
                }
            }
            case "terminate":
            case "kill":
            case "stop": {
                Task.Run(async () => {
                    await Task.Delay(200);
                    Environment.Exit(0);
                });
                return "{\"ok\":true}";
            }
            default:
                return "{\"error\":\"unknown command: " + cmd + "\"}";
        }
    }
}
