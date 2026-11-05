// XM Panel - small Windows tray companion for Sony WH-1000X headphones
// (WH-1000XM4 tested; XM3 should work; XM5/XM6 experimental).
// Copyright (c) 2026 Holse18. MIT License, see LICENSE.
// Not affiliated with Sony. Protocol knowledge thanks to Gadgetbridge, Plutoberth's
// SonyHeadphonesClient and whitebeard10's SonyCompanionApp (see README).
//
// Talks Sony's control protocol over Bluetooth RFCOMM (same channel the phone app uses).
// Layout follows Apple's Human Interface Guidelines (grouped lists, segmented control,
// switches, Reduce Motion, focus rings); colours, acrylic and controls follow Windows 11.
// Build: build.cmd (uses the C# compiler that ships with Windows / .NET Framework 4).
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Management;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using System.Reflection;

// File properties shown in Explorer (Details tab) and checked by code signing: keep the product
// name fixed and bump the version together with MainForm.Version for each release.
[assembly: AssemblyTitle("XM Panel")]
[assembly: AssemblyProduct("XM Panel")]
[assembly: AssemblyDescription("Windows tray companion for Sony WH-1000X headphones")]
[assembly: AssemblyCompany("Holse18")]
[assembly: AssemblyCopyright("Copyright (c) 2026 Holse18. MIT License.")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
[assembly: AssemblyInformationalVersion("1.0.0")]

// ================================================================ Data folder
// Settings and logs live in %LOCALAPPDATA%\XMPanel, wherever the exe itself is kept (e.g. Downloads).
static class AppData
{
    static string dir;
    public static string Dir
    {
        get
        {
            if (dir != null) return dir;
            dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XMPanel");
            try
            {
                Directory.CreateDirectory(dir);
                // One-time carry-over from pre-release names ("XMate", then "XM4 Control").
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string newIni = Path.Combine(dir, "settings.ini");
                foreach (var old in new[] { "XMate", "XM4Control" })
                {
                    string oldIni = Path.Combine(local, old, "settings.ini");
                    if (!File.Exists(newIni) && File.Exists(oldIni)) File.Copy(oldIni, newIni);
                }
            }
            catch { }
            return dir;
        }
    }
}

// ================================================================ Bluetooth + Sony protocol
class BtEndPoint : EndPoint
{
    public const AddressFamily AF_BTH = (AddressFamily)32;
    readonly ulong addr; readonly Guid service;
    public BtEndPoint(ulong addr, Guid service) { this.addr = addr; this.service = service; }
    public override AddressFamily AddressFamily { get { return AF_BTH; } }
    public override SocketAddress Serialize()
    {
        // SOCKADDR_BTH (packed, 30 bytes): family(2) btAddr(8) serviceClassId(16) port(4)
        var sa = new SocketAddress(AF_BTH, 30);
        byte[] a = BitConverter.GetBytes(addr);
        for (int i = 0; i < 8; i++) sa[2 + i] = a[i];
        byte[] g = service.ToByteArray();
        for (int i = 0; i < 16; i++) sa[10 + i] = g[i];
        for (int i = 0; i < 4; i++) sa[26 + i] = 0;
        return sa;
    }
    public override EndPoint Create(SocketAddress sa) { return this; }
}

class SonyLink
{
    static readonly Guid SonyService = new Guid("96CC203E-5068-46ad-B32D-E316F5E069BA");
    const byte START = 0x3E, END = 0x3C, ESC = 0x3D;
    const byte T_ACK = 0x01, T_CMD = 0x0C;

    Socket sock;
    byte seq = 0;
    readonly object writeLock = new object();
    readonly AutoResetEvent ackEvt = new AutoResetEvent(false);
    BlockingCollection<byte[]> queue;
    volatile bool alive;

    public event Action<byte[]> Message;
    public event Action<string> Closed;
    public static Action<string> Log = delegate { };

    // XM5/XM6 (protocol v2) expose the control channel under a different service.
    public static readonly Guid SonyServiceV2 = new Guid("956C7B26-D49A-4BA8-B03F-B17D393CB6E2");

    public void Connect(ulong btAddr) { Connect(btAddr, SonyService); }
    public void Connect(ulong btAddr, Guid service)
    {
        var s = new Socket(BtEndPoint.AF_BTH, SocketType.Stream, (ProtocolType)3 /* RFCOMM */);
        s.Connect(new BtEndPoint(btAddr, service));
        sock = s; alive = true; seq = 0;
        queue = new BlockingCollection<byte[]>();
        new Thread(ReadLoop) { IsBackground = true }.Start();
        new Thread(SendLoop) { IsBackground = true }.Start();
    }

    public void Close()
    {
        alive = false;
        try { sock.Close(); } catch { }
        try { queue.CompleteAdding(); } catch { }
    }

    public void Send(params byte[] payload) { if (alive) queue.Add(payload); }

    // Blocking send for when the app is about to be killed (PC shutdown): returns once the
    // headset has acknowledged this command (or the timeout passes).
    readonly ConcurrentDictionary<byte[], ManualResetEvent> waiters = new ConcurrentDictionary<byte[], ManualResetEvent>();
    public bool SendAndWait(byte[] payload, int timeoutMs)
    {
        if (!alive) return false;
        var ev = new ManualResetEvent(false);
        waiters[payload] = ev;
        queue.Add(payload);
        return ev.WaitOne(timeoutMs);
    }

    void SendLoop()
    {
        try
        {
            foreach (var p in queue.GetConsumingEnumerable())
            {
                bool acked = false;
                for (int attempt = 0; attempt < 3 && alive; attempt++)
                {
                    ackEvt.Reset();
                    Write(T_CMD, seq, p);
                    if (ackEvt.WaitOne(700)) { acked = true; break; }
                    Log("no ACK, retry");
                }
                ManualResetEvent ev;
                if (acked && waiters.TryRemove(p, out ev)) ev.Set();
            }
        }
        catch (Exception e) { Fail(e.Message); }
    }

    void Write(byte type, byte sq, byte[] payload)
    {
        var raw = new List<byte> { type, sq,
            (byte)(payload.Length >> 24), (byte)(payload.Length >> 16), (byte)(payload.Length >> 8), (byte)payload.Length };
        raw.AddRange(payload);
        int sum = 0; foreach (var b in raw) sum += b;
        raw.Add((byte)sum);
        var frame = new List<byte> { START };
        foreach (var b in raw)
        {
            if (b == START || b == END || b == ESC) { frame.Add(ESC); frame.Add((byte)(b & 0xEF)); }
            else frame.Add(b);
        }
        frame.Add(END);
        lock (writeLock) sock.Send(frame.ToArray());
        if (type != T_ACK) Log("TX " + BitConverter.ToString(payload));
    }

    void ReadLoop()
    {
        var buf = new byte[1024];
        var cur = new List<byte>();
        bool inFrame = false, esc = false;
        try
        {
            while (alive)
            {
                int n = sock.Receive(buf);
                if (n <= 0) break;
                for (int i = 0; i < n; i++)
                {
                    byte b = buf[i];
                    if (b == START) { cur.Clear(); inFrame = true; esc = false; continue; }
                    if (!inFrame) continue;
                    if (b == END) { inFrame = false; Handle(cur.ToArray()); continue; }
                    if (b == ESC) { esc = true; continue; }
                    cur.Add(esc ? (byte)(b | 0x10) : b); esc = false;
                }
            }
            Fail("connection closed");
        }
        catch (Exception e) { Fail(e.Message); }
    }

    void Handle(byte[] f)
    {
        if (f.Length < 7) return;
        byte type = f[0], sq = f[1];
        int len = (f[2] << 24) | (f[3] << 16) | (f[4] << 8) | f[5];
        if (f.Length < 6 + len + 1) return;
        var payload = new byte[len];
        Array.Copy(f, 6, payload, 0, len);
        if (type == T_ACK) { seq = sq; ackEvt.Set(); return; }
        Write(T_ACK, (byte)(1 - sq), new byte[0]);
        Log("RX " + BitConverter.ToString(payload));
        var h = Message; if (h != null) h(payload);
    }

    void Fail(string why)
    {
        if (!alive) return;
        alive = false;
        try { sock.Close(); } catch { }
        var h = Closed; if (h != null) h(why);
    }

    // ---- Cheap "is the headset connected to Windows?" check (no radio activity, no WMI).
    [StructLayout(LayoutKind.Sequential)] struct FindRadioParams { public int dwSize; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DeviceInfo
    {
        public int dwSize; public ulong Address; public uint ClassOfDevice;
        public int fConnected, fRemembered, fAuthenticated;
        public long LastSeen1, LastSeen2, LastUsed1, LastUsed2; // two SYSTEMTIMEs
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)] public string Name;
    }
    [DllImport("bthprops.cpl")] static extern IntPtr BluetoothFindFirstRadio(ref FindRadioParams p, out IntPtr radio);
    [DllImport("bthprops.cpl")] static extern bool BluetoothFindRadioClose(IntPtr find);
    [DllImport("bthprops.cpl")] static extern int BluetoothGetDeviceInfo(IntPtr radio, ref DeviceInfo info);
    static IntPtr radio;

    // null = unknown (no radio / API failed): caller should just try to connect.
    public static bool? IsConnectedToWindows(ulong addr)
    {
        try
        {
            if (radio == IntPtr.Zero)
            {
                var fp = new FindRadioParams { dwSize = Marshal.SizeOf(typeof(FindRadioParams)) };
                IntPtr find = BluetoothFindFirstRadio(ref fp, out radio);
                if (find == IntPtr.Zero) return null;
                BluetoothFindRadioClose(find);
            }
            var info = new DeviceInfo { dwSize = Marshal.SizeOf(typeof(DeviceInfo)), Address = addr };
            if (BluetoothGetDeviceInfo(radio, ref info) != 0) return null;
            return info.fConnected != 0;
        }
        catch { return null; }
    }

    public static ulong FindHeadset(out string name)
    {
        using (var q = new ManagementObjectSearcher("SELECT Name, DeviceID FROM Win32_PnPEntity WHERE Name LIKE '%WH-1000XM%'"))
            foreach (ManagementObject mo in q.Get())
            {
                var m = Regex.Match((string)mo["DeviceID"] ?? "", @"DEV_([0-9A-F]{12})", RegexOptions.IgnoreCase);
                if (m.Success) { name = (string)mo["Name"]; return Convert.ToUInt64(m.Groups[1].Value, 16); }
            }
        name = null; return 0;
    }
}

// ================================================================ Command table: v1 (XM3/XM4) vs v2 (XM5/XM6)
// v1 bytes are verified on a WH-1000XM4. v2 bytes come from Gadgetbridge (SonyProtocolImplV2) and
// whitebeard10/SonyCompanionApp (MIT); only commands both agree on, or Gadgetbridge alone where the
// other has nothing, are used. v2 is UNTESTED on real hardware.
static class Cmd
{
    public static bool V2;
    static byte B(bool b) { return (byte)(b ? 1 : 0); }

    public static byte[] Init() { return new byte[] { 0x00, 0x00 }; }
    public static byte[] BatteryGet() { return V2 ? new byte[] { 0x22, 0x00 } : new byte[] { 0x10, 0x00 }; }
    public static byte[] NcGet() { return V2 ? new byte[] { 0x66, 0x17 } : new byte[] { 0x66, 0x02 }; }
    // mode: 0 = noise canceling, 1 = ambient, 2 = off
    public static byte[] NcSet(int mode, bool voice, int level)
    {
        bool on = mode != 2;
        if (V2) return new byte[] { 0x68, 0x17, 0x01, B(on), B(mode == 1), B(voice), (byte)level };
        return new byte[] { 0x68, 0x02, (byte)(on ? 0x11 : 0x00), 0x02, B(mode == 0), 0x01, B(voice), (byte)level };
    }
    public static byte[] EqGet() { return new byte[] { 0x56, (byte)(V2 ? 0x00 : 0x01) }; }
    public static byte[] EqPreset(byte code) { return new byte[] { 0x58, (byte)(V2 ? 0x00 : 0x01), code, 0x00 }; }
    // v1 needs the Manual slot selected first, then bands written to the current slot (verified).
    public static byte[][] EqCustom(int[] bands, bool selectSlot)
    {
        var b = new byte[] { 0x58, (byte)(V2 ? 0x00 : 0x01), (byte)(V2 ? 0xA0 : 0xFF), 0x06, 0, 0, 0, 0, 0, 0 };
        for (int i = 0; i < 6; i++) b[4 + i] = (byte)(bands[i] + 10);
        if (V2 || !selectSlot) return new[] { b };
        return new[] { new byte[] { 0x58, 0x01, 0xA0, 0x00 }, b };
    }
    public static byte[] DseeGet() { return V2 ? new byte[] { 0xE6, 0x01 } : new byte[] { 0xE6, 0x02 }; }
    public static byte[] DseeSet(bool on) { return V2 ? new byte[] { 0xE8, 0x01, B(on) } : new byte[] { 0xE8, 0x02, 0x00, B(on) }; }
    public static byte[] QualityGet() { return V2 ? new byte[] { 0xE6, 0x02 } : new byte[] { 0xE6, 0x01 }; }
    public static byte[] QualitySet(bool stable) { return V2 ? new byte[] { 0xE8, 0x02, B(stable) } : new byte[] { 0xE8, 0x01, 0x00, B(stable) }; }
    public static byte[] StcGet() { return V2 ? new byte[] { 0xF6, 0x0C } : new byte[] { 0xF6, 0x05 }; }
    public static byte[] StcSet(bool on) { return V2 ? new byte[] { 0xF8, 0x0C, B(!on), 0x01 } : new byte[] { 0xF8, 0x05, 0x01, B(on) }; }
    public static bool HasPause { get { return !V2; } } // v2 command unclear in both sources: not offered
    public static byte[] PauseGet() { return new byte[] { 0xF6, 0x03 }; }
    public static byte[] PauseSet(bool on) { return new byte[] { 0xF8, 0x03, 0x00, B(on) }; }
    public static byte[] TouchGet() { return V2 ? new byte[] { 0xD6, 0xD2 } : new byte[] { 0xD6, 0xD1 }; }
    public static byte[] TouchSet(bool on) { return V2 ? new byte[] { 0xD8, 0xD2, 0x00, B(!on) } : new byte[] { 0xD8, 0xD1, 0x01, B(on) }; }
    public static byte[] ApoGet() { return V2 ? new byte[] { 0x26, 0x05 } : new byte[] { 0xF6, 0x04 }; }
    public static byte[] ApoSet(bool on)
    {
        byte c = (byte)(on ? 0x10 : 0x11); // 0x10 = when taken off, 0x11 = never
        return V2 ? new byte[] { 0x28, 0x05, c, 0x00 } : new byte[] { 0xF8, 0x04, 0x01, c, 0x00 };
    }
    public static byte[] PowerOff() { return V2 ? new byte[] { 0x24, 0x03, 0x01 } : new byte[] { 0x22, 0x00, 0x01 }; }
}

// ================================================================ Theme (Apple system colors)
static class Theme
{
    public static bool Dark, ReduceMotion;
    // Glass: Windows 11 (22H2+) acrylic backdrop with "Transparency effects" on. Surfaces that would be
    // Bg are painted fully transparent; cards are translucent. CardSolid approximates the visible
    // card colour for colour mixing and for native controls that can't take alpha (TextBox).
    public static bool Glass;
    public static Color CardSolid, GlassTint;
    // Accent roles (Windows 11): fill for switches/slider/selection, text for links, OnAccent for text on fill.
    public static Color AccentFill, AccentText, OnAccent;

    // Windows accent palette (HKCU ...\Explorer\Accent\AccentPalette: light3, light2, light1, base,
    // dark1, dark2, dark3 as RGBA). Dark mode uses light2 for fills and light3 for text, light mode
    // dark1 and dark2 — the same shades Windows uses in quick settings.
    static void ReadWindowsAccent()
    {
        try
        {
            var pal = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Accent", "AccentPalette", null) as byte[];
            if (pal == null || pal.Length < 28) return;
            Func<int, Color> at = i => Color.FromArgb(255, pal[i * 4], pal[i * 4 + 1], pal[i * 4 + 2]);
            AccentFill = Dark ? at(1) : at(4);
            AccentText = Dark ? at(0) : at(5);
            OnAccent = Dark ? Color.Black : Color.White;
            Accent = AccentText;
        }
        catch { }
    }
    [ThreadStatic] public static bool PaintingOnGlass; // set per paint by AppleControl.Begin
    public static float S = 1f;
    public static Color Bg, Card, Label, Secondary, Separator, Fill, Pill, OffTrack, Accent, Green, Red, Menu;
    public static Font Title, Body, BodySemi, Small, SmallSemi, Section, Footnote;

    [DllImport("user32.dll")] static extern bool SystemParametersInfo(int a, int b, ref bool c, int d);

    public static int Px(float v) { return (int)Math.Round(v * S); }

    public static void Load()
    {
        Dark = false;
        try { object v = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1); Dark = v is int && (int)v == 0; } catch { }
        bool anim = true;
        try { SystemParametersInfo(0x1042 /* SPI_GETCLIENTAREAANIMATION */, 0, ref anim, 0); } catch { }
        ReduceMotion = !anim;

        if (Dark)
        {
            Bg = C(0x1C1C1E); Card = C(0x2C2C2E); Label = C(0xFFFFFF); Secondary = C(0x98989F);
            Separator = C(0x444446); Fill = C(0x3F3F43); Pill = C(0x636366); OffTrack = C(0x48484A);
            Accent = C(0x0A84FF); Green = C(0x30D158); Red = C(0xFF453A); Menu = C(0x323234);
        }
        else
        {
            Bg = C(0xF2F2F7); Card = C(0xFFFFFF); Label = C(0x000000); Secondary = C(0x8A8A8E);
            Separator = C(0xC6C6C8); Fill = C(0xEEEEEF); Pill = C(0xFFFFFF); OffTrack = C(0xE9E9EA);
            Accent = C(0x007AFF); Green = C(0x34C759); Red = C(0xFF3B30); Menu = C(0xFFFFFF);
        }
        CardSolid = Card;
        Glass = false;
        try
        {
            int build; int.TryParse(Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "CurrentBuild", "0") as string, out build);
            object tr = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 1);
            Glass = build >= 22621 && !(tr is int && (int)tr == 0) && !SystemInformation.HighContrast;
        }
        catch { }
        AccentFill = Accent; AccentText = Accent; OnAccent = Color.White;
        if (Glass)
        {
            // Windows 11 quick-settings palette: near-solid tinted background over acrylic, solid cards
            // (child windows replace pixels rather than blend, so cards must be opaque to match),
            // neutral text and the user's Windows accent colour.
            if (Dark)
            {
                GlassTint = Color.FromArgb(232, 0x20, 0x20, 0x20); Bg = C(0x202020);
                Card = C(0x2D2D2D); CardSolid = Card; Pill = Card;
                Fill = Color.FromArgb(18, 255, 255, 255); Separator = Color.FromArgb(22, 255, 255, 255);
                Label = C(0xFFFFFF); Secondary = C(0xC5C5C5); OffTrack = C(0x9A9A9A); Menu = C(0x2C2C2C);
            }
            else
            {
                GlassTint = Color.FromArgb(232, 0xF3, 0xF3, 0xF3); Bg = C(0xF3F3F3);
                Card = C(0xFBFBFB); CardSolid = Card; Pill = Card;
                Fill = Color.FromArgb(10, 0, 0, 0); Separator = Color.FromArgb(22, 0, 0, 0);
                Label = C(0x1B1B1B); Secondary = C(0x5F5F5F); OffTrack = C(0x8A8A8A); Menu = C(0xF9F9F9);
            }
            ReadWindowsAccent();
        }
        if (Title == null)
        {
            string reg = Pick("Segoe UI Variable Text", "Segoe UI");
            string semi = Pick("Segoe UI Variable Text Semibold", "Segoe UI Semibold");
            string disp = Pick("Segoe UI Variable Display Semib", "Segoe UI Semibold");
            Title = new Font(disp, 15f);       // ~20 px, title 3
            Body = new Font(reg, 10f);         // ~13 px, macOS body
            BodySemi = new Font(semi, 10f);
            Small = new Font(reg, 9.25f);      // segmented control labels
            SmallSemi = new Font(semi, 9.25f);
            Section = new Font(semi, 9f);      // ~12 px section header
            Footnote = new Font(reg, 8.5f);    // ~11 px footer
        }
    }

    static string Pick(params string[] names)
    {
        using (var fc = new InstalledFontCollection())
            foreach (var n in names)
                foreach (var f in fc.Families) if (f.Name == n) return n;
        return "Segoe UI";
    }

    static Color C(int rgb) { return Color.FromArgb(255, (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255); }

    public static Color Mix(Color a, Color b, float t)
    {
        t = Math.Max(0, Math.Min(1, t));
        return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
    }

    // Disabled controls are dimmed, not hidden.
    public static Color En(Color c, bool enabled) { return enabled ? c : Mix(CardSolid, c, 0.35f); }

    public static GraphicsPath Round(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static void Text(Graphics g, string s, Font f, Rectangle r, Color c, TextFormatFlags extra)
    {
        var flags = TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | extra;
        // Windows 11 shell surfaces (quick settings) use greyscale text smoothing; ClearType's colour
        // fringes don't survive the alpha pipeline. So on glass every text goes through GlassText.
        if (Glass && r.Width > 0 && r.Height > 0) { GlassText(g, s, f, r, c, flags); return; }
        // Native GDI ClearType text (crisp, native spacing). On opaque surfaces of a glass window the
        // alpha GDI leaves behind is repaired after painting (AppleControl.FixGdiAlpha).
        TextRenderer.DrawText(g, s, f, r, c, flags);
    }

    // Text directly on the translucent background: GDI renders white-on-black into a scratch image
    // (same glyphs and spacing as everywhere else); that becomes a coverage mask for the colour,
    // which GDI+ then blends in with correct alpha.
    static void GlassText(Graphics g, string s, Font f, Rectangle r, Color c, TextFormatFlags flags)
    {
        using (var mask = new Bitmap(r.Width, r.Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb))
        {
            // Render the coverage mask with the same polarity as the final text (dark-on-light or
            // light-on-dark): GDI's gamma makes light-on-dark glyphs heavier, so matching keeps weight right.
            // Polarity follows the surface, not a fixed midpoint: greyed-out text is mid-grey and
            // would flip to the wrong polarity (dark fringes). Only clearly dark text in dark mode
            // (e.g. black on the accent pill), or clearly light text in light mode, flips.
            double lum = c.R * 0.299 + c.G * 0.587 + c.B * 0.114;
            bool darkText = Dark ? lum < 60 : lum < 200;
            Color fore = darkText ? Color.Black : Color.White, back = darkText ? Color.White : Color.Black;
            using (var mg = Graphics.FromImage(mask))
            {
                mg.Clear(back);
                TextRenderer.DrawText(mg, s, f, new Rectangle(0, 0, r.Width, r.Height), fore, back, flags);
            }
            var rect = new Rectangle(0, 0, r.Width, r.Height);
            int n; int[] px;
            var d = mask.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            try { n = d.Stride / 4 * d.Height; px = new int[n]; Marshal.Copy(d.Scan0, px, 0, n); }
            finally { mask.UnlockBits(d); }
            for (int i = 0; i < n; i++)
            {
                int v = px[i];
                int cov = (((v >> 16) & 255) + ((v >> 8) & 255) + (v & 255)) / 3; // ClearType fringes -> grey coverage
                if (darkText) cov = 255 - cov;
                px[i] = ((cov * c.A / 255) << 24) | (c.R << 16) | (c.G << 8) | c.B;
            }
            using (var text = new Bitmap(r.Width, r.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                var td = text.LockBits(rect, System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                try { Marshal.Copy(px, 0, td.Scan0, Math.Min(n, td.Stride / 4 * td.Height)); }
                finally { text.UnlockBits(td); }
                g.DrawImageUnscaled(text, r.X, r.Y);
            }
        }
    }

    public static int Width(string s, Font f)
    {
        return TextRenderer.MeasureText(s, f, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
    }

    // Windows 11 acrylic backdrop for the whole window (Glass only).
    [StructLayout(LayoutKind.Sequential)] struct Margins { public int L, R, T, B; }
    [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr h, ref Margins m);
    public static void Backdrop(IntPtr h)
    {
        if (!Glass) return;
        try
        {
            var m = new Margins { L = -1, R = -1, T = -1, B = -1 };
            DwmExtendFrameIntoClientArea(h, ref m);
            int type = 3; // DWMSBT_TRANSIENTWINDOW: acrylic, as used by flyouts
            DwmSetWindowAttribute(h, 38 /* DWMWA_SYSTEMBACKDROP_TYPE */, ref type, 4);
        }
        catch { }
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);
    public static void WindowChrome(IntPtr h, bool roundCorners)
    {
        try
        {
            int d = Dark ? 1 : 0; DwmSetWindowAttribute(h, 20 /* immersive dark mode */, ref d, 4);
            if (roundCorners) { int r = 2; DwmSetWindowAttribute(h, 33 /* corner preference: round */, ref r, 4); }
        }
        catch { }
    }
}

// ================================================================ Headphone artwork
// Over-ear headphones in 3/4 view: thick headband, sliders, tilted ear cups; the left cup shows
// its pad opening. Drawn as vectors on a 64-unit grid so it stays crisp from 16 px tray size up.
static class HeadphoneArt
{
    public static void Draw(Graphics g, RectangleF box, Color c) { Draw(g, box, c, Color.Empty); }

    // bg: the colour behind the icon. When given and the icon is big enough, detail lines
    // (band highlight, slider break, pad ring, cup plate) are cut out of the silhouette.
    public static void Draw(Graphics g, RectangleF box, Color c, Color bg)
    {
        bool detail = !bg.IsEmpty && box.Width >= 30;
        if (detail) { DrawDetailed(g, box, c, bg); return; }
        var state = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TranslateTransform(box.X, box.Y);
        float k = box.Width / 64f; g.ScaleTransform(k, k);
        using (var br = new SolidBrush(c))
        using (var pen = new Pen(c, 6.5f))
        {
            g.DrawArc(pen, 12, 7, 40, 40, 180, 180);           // headband
            g.FillRectangle(br, 8.25f, 25.5f, 7.5f, 8f);        // sliders
            g.FillRectangle(br, 48.25f, 25.5f, 7.5f, 8f);
            using (var p = new GraphicsPath(FillMode.Alternate)) // left cup with pad opening
            {
                p.AddEllipse(-8.5f, -13.5f, 17, 27);
                p.AddEllipse(-1.5f, -7.5f, 7, 15);
                using (var m = new Matrix()) { m.Translate(18, 46); m.Rotate(-14); p.Transform(m); }
                g.FillPath(br, p);
            }
            using (var p = new GraphicsPath())                    // right cup, outer side
            {
                p.AddEllipse(-8.5f, -13.5f, 17, 27);
                using (var m = new Matrix()) { m.Translate(46, 46); m.Rotate(14); p.Transform(m); }
                g.FillPath(br, p);
            }
        }
        g.Restore(state);
    }

    // ---- Shaded "product shot" of the XM4 (front 3/4 view), drawn on a 200-unit grid.
    static readonly Dictionary<int, Bitmap> cache = new Dictionary<int, Bitmap>();

    // ---- User-supplied artwork (headphones.png, embedded at build time): black drawing on white.
    // Black becomes the theme colour, white becomes transparent, so detail lines show the background.
    static Bitmap source; static bool sourceTried;
    static readonly Dictionary<string, Bitmap> tintCache = new Dictionary<string, Bitmap>();

    public static bool HasArtwork
    {
        get
        {
            if (!sourceTried)
            {
                sourceTried = true;
                try
                {
                    var s = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("headphones.png");
                    if (s != null) source = new Bitmap(s);
                }
                catch { source = null; }
            }
            return source != null;
        }
    }

    public static Bitmap Artwork(int size, Color c)
    {
        string key = size + "/" + c.ToArgb();
        Bitmap b;
        if (tintCache.TryGetValue(key, out b)) return b;

        // Crop to the drawing (ignore near-white), pad to a square.
        int minX = source.Width, minY = source.Height, maxX = 0, maxY = 0;
        for (int y = 0; y < source.Height; y += 2)
            for (int x = 0; x < source.Width; x += 2)
            {
                var p = source.GetPixel(x, y);
                if (p.A > 128 && (p.R + p.G + p.B) / 3 < 160)
                { if (x < minX) minX = x; if (x > maxX) maxX = x; if (y < minY) minY = y; if (y > maxY) maxY = y; }
            }
        int side = (int)(Math.Max(maxX - minX, maxY - minY) * 1.04f) + 2;
        var crop = new Rectangle((minX + maxX - side) / 2, (minY + maxY - side) / 2, side, side);

        var tmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(tmp))
        {
            g.Clear(Color.White);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(source, new Rectangle(0, 0, size, size), crop, GraphicsUnit.Pixel);
        }
        b = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                var p = tmp.GetPixel(x, y);
                int alpha = 255 - (p.R + p.G + p.B) / 3;
                if (size <= 32) alpha = Math.Min(255, alpha * 3 / 2); // keep tiny icons solid and crisp
                b.SetPixel(x, y, Color.FromArgb(alpha * c.A / 255, c.R, c.G, c.B));
            }
        tmp.Dispose();
        tintCache[key] = b;
        return b;
    }

    public static Bitmap Hero(int size)
    {
        if (HasArtwork) return Artwork(size, Theme.Label);
        Bitmap b;
        if (cache.TryGetValue(size, out b)) return b;
        b = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(b))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.ScaleTransform(size / 200f, size / 200f);
            DrawHero(g);
        }
        cache[size] = b;
        return b;
    }

    static Color H(int rgb) { return Color.FromArgb(255, (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255); }
    static Color A(int a, Color c) { return Color.FromArgb(a, c); }

    static void Radial(Graphics g, GraphicsPath p, PointF center, Color mid, Color edge)
    {
        using (var br = new PathGradientBrush(p) { CenterPoint = center, CenterColor = mid, SurroundColors = new[] { edge } })
            g.FillPath(br, p);
    }

    static void DrawHero(Graphics g)
    {
        // Soft ground shadow.
        using (var p = new GraphicsPath())
        {
            p.AddEllipse(22, 178, 156, 16);
            Radial(g, p, new PointF(100, 186), Color.FromArgb(110, 0, 0, 0), Color.FromArgb(0, 0, 0, 0));
        }

        // Metal sliders (drawn first; the band and yokes overlap their ends).
        foreach (float x in new[] { 30.5f, 160.5f })
            using (var br = new LinearGradientBrush(new RectangleF(x, 0, 9, 1), H(0x9A9AA0), H(0x3C3C40), 0f))
            {
                var blend = new ColorBlend { Positions = new[] { 0f, 0.35f, 0.7f, 1f }, Colors = new[] { H(0x5A5A5F), H(0xB4B4BA), H(0x6E6E73), H(0x3A3A3E) } };
                br.InterpolationColors = blend;
                g.FillRectangle(br, x, 70, 9, 30);
            }

        // Headband: thick arc with vertical shading, cushion underside and top highlight.
        using (var band = new GraphicsPath())
        {
            band.AddArc(26, 18, 148, 148, 192, 156);
            band.AddArc(41, 33, 118, 118, 348, -156);
            band.CloseFigure();
            using (var br = new LinearGradientBrush(new RectangleF(0, 16, 1, 80), H(0x4E4E53), H(0x18181A), 90f)) g.FillPath(br, band);
        }
        using (var pen = new Pen(H(0x2E2E32), 4f)) g.DrawArc(pen, 43, 35, 114, 114, 196, 148);           // cushion
        using (var pen = new Pen(A(26, Color.Black), 1.2f)) g.DrawArc(pen, 46, 38, 108, 108, 196, 148);
        using (var pen = new Pen(A(90, Color.White), 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawArc(pen, 28, 20, 144, 144, 218, 70);                                                   // highlight
        using (var pen = new Pen(A(35, Color.White), 1.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawArc(pen, 28, 20, 144, 144, 292, 36);

        // Swivel yokes where the sliders enter the cups.
        foreach (float x in new[] { 25f, 155f })
            using (var p = Theme.Round(new RectangleF(x, 96, 20, 13), 5))
            {
                using (var br = new LinearGradientBrush(new RectangleF(x, 96, 20, 13), H(0x45454A), H(0x161618), 90f)) g.FillPath(br, p);
                using (var pen = new Pen(A(40, Color.White), 1f)) g.DrawLine(pen, x + 4, 97f, x + 16, 97f);
            }

        DrawCup(g, 44, 142, -11, true);
        DrawCup(g, 156, 142, 11, false);
    }

    static void DrawCup(Graphics g, float cx, float cy, float rot, bool innerSide)
    {
        var st = g.Save();
        g.TranslateTransform(cx, cy); g.RotateTransform(rot);
        float w = 60, h = 84, depth = innerSide ? -8 : 8; // housing depth shows on the outer side

        // Housing side (depth) + face.
        using (var p = new GraphicsPath())
        {
            p.AddEllipse(-w / 2 + depth, -h / 2 + 1, w, h);
            using (var br = new LinearGradientBrush(new RectangleF(-w / 2, -h / 2, w, h), H(0x26262A), H(0x0C0C0D), 90f)) g.FillPath(br, p);
        }
        var face = new RectangleF(-w / 2, -h / 2, w, h);
        using (var p = new GraphicsPath())
        {
            p.AddEllipse(face);
            if (innerSide) using (var br = new LinearGradientBrush(face, H(0x3A3A3F), H(0x161618), 70f)) g.FillPath(br, p);
            else Radial(g, p, new PointF(-9, -16), H(0x5C5C62), H(0x1C1C1F));
        }

        if (innerSide)
        {
            // Ear pad: puffy ring with a soft highlight, and the dark opening.
            var pad = new RectangleF(-26, -38, 52, 76);
            using (var p = new GraphicsPath())
            {
                p.AddEllipse(pad);
                Radial(g, p, new PointF(-3, -6), H(0x4A4A50), H(0x18181B));
            }
            using (var pen = new Pen(A(60, Color.White), 1.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawArc(pen, pad.X + 2, pad.Y + 2, pad.Width - 4, pad.Height - 4, 180, 90);
            var hole = new RectangleF(-9, -22, 22, 44);
            using (var p = new GraphicsPath())
            {
                p.AddEllipse(hole);
                using (var br = new LinearGradientBrush(hole, H(0x050506), H(0x232327), 80f)) g.FillPath(br, p);
            }
            using (var pen = new Pen(A(50, Color.White), 1f)) g.DrawArc(pen, hole, 20, 120);   // inner pad lip catching light
            using (var pen = new Pen(A(120, Color.Black), 2f)) g.DrawArc(pen, hole, 200, 120); // inner shadow
        }
        else
        {
            // Outer housing: rim highlight, subtle touch-panel bevel and the copper mic accent.
            using (var pen = new Pen(A(70, Color.White), 1.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawArc(pen, face.X + 1.5f, face.Y + 1.5f, face.Width - 3, face.Height - 3, 150, 110);
            var bevel = new RectangleF(-23, -34, 46, 68);
            using (var pen = new Pen(A(28, Color.White), 1f)) g.DrawArc(pen, bevel, 160, 120);
            using (var pen = new Pen(A(70, Color.Black), 1.2f)) g.DrawArc(pen, bevel, 340, 120);
            using (var br = new SolidBrush(H(0xB98A6A))) g.FillEllipse(br, 10f, -33f, 3.6f, 3.6f);
        }
        // Edge definition so the black body reads on dark backgrounds.
        using (var pen = new Pen(A(30, Color.White), 0.8f)) g.DrawEllipse(pen, face);
        g.Restore(st);
    }

    static GraphicsPath Cup(float cx, float cy, float w, float h, float rot)
    {
        var p = new GraphicsPath();
        p.AddEllipse(-w / 2, -h / 2, w, h);
        using (var m = new Matrix()) { m.Translate(cx, cy); m.Rotate(rot); p.Transform(m); }
        return p;
    }

    static void DrawDetailed(Graphics g, RectangleF box, Color c, Color bg)
    {
        var state = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TranslateTransform(box.X, box.Y);
        float k = box.Width / 64f; g.ScaleTransform(k, k);
        using (var br = new SolidBrush(c))
        using (var cut = new SolidBrush(bg))
        using (var band = new Pen(c, 7.5f))
        using (var line = new Pen(bg, 1.1f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            // Headband with a highlight line along its inner edge.
            g.DrawArc(band, 11.5f, 5.5f, 41, 41, 180, 180);
            g.DrawArc(line, 13.6f, 7.6f, 36.8f, 36.8f, 200, 140);
            // Sliders, separated from the band by a small break.
            g.FillRectangle(br, 7.5f, 24f, 8f, 10f);
            g.FillRectangle(br, 48.5f, 24f, 8f, 10f);
            g.FillRectangle(cut, 7f, 25.2f, 9f, 1.1f);
            g.FillRectangle(cut, 48f, 25.2f, 9f, 1.1f);

            // Left cup: seen from the inside - housing rim, then the pad with its opening.
            using (var p = Cup(18.5f, 46.5f, 20, 29, -18)) g.FillPath(br, p);
            using (var p = Cup(19.8f, 46.7f, 16, 25.5f, -18)) g.DrawPath(line, p);
            using (var p = Cup(21, 46.8f, 8.5f, 16.5f, -18)) g.FillPath(cut, p);
            // Right cup: seen from the outside - cup with its outer plate.
            using (var p = Cup(45.5f, 46.5f, 20, 29, 18)) g.FillPath(br, p);
            using (var p = Cup(47.8f, 47.2f, 13, 21, 18)) g.DrawPath(line, p);
        }
        g.Restore(state);
    }
}

// ================================================================ Controls
class AppleControl : Control
{
    protected float Anim; float animTarget;
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 15 };

    public AppleControl()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        timer.Tick += (s, e) =>
        {
            Anim += (animTarget - Anim) * 0.3f;
            if (Math.Abs(animTarget - Anim) < 0.01f) { Anim = animTarget; timer.Stop(); }
            Invalidate();
        };
    }

    protected void AnimateTo(float target, bool animate)
    {
        animTarget = target;
        if (!animate || Theme.ReduceMotion || !Visible) { Anim = target; timer.Stop(); Invalidate(); return; }
        timer.Start();
    }

    // Set by Card for rows that touch the next row: the row draws the hairline itself (like a table cell).
    public bool SeparatorBelow;

    // ---- Glass painting: WM_PAINT renders into a premultiplied-alpha bitmap and copies it to the
    // window, so DWM sees real per-pixel alpha (GDI would write alpha 0 and garble the backdrop).
    [StructLayout(LayoutKind.Sequential)]
    struct PAINTSTRUCT { public IntPtr hdc; public int fErase; public int l, t, r, b; public int fRestore, fIncUpdate; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] reserved; }
    [DllImport("user32.dll")] static extern IntPtr BeginPaint(IntPtr h, out PAINTSTRUCT ps);
    [DllImport("user32.dll")] static extern bool EndPaint(IntPtr h, ref PAINTSTRUCT ps);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr d, int x, int y, int w, int h, IntPtr s, int sx, int sy, int rop);

    protected override void WndProc(ref Message m)
    {
        if (Theme.Glass && m.Msg == 0x0014 /* WM_ERASEBKGND */) { m.Result = (IntPtr)1; return; }
        if (Theme.Glass && m.Msg == 0x000F /* WM_PAINT */ && Width > 0 && Height > 0)
        {
            PAINTSTRUCT ps;
            IntPtr hdc = BeginPaint(Handle, out ps);
            try
            {
                using (var bmp = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
                {
                    using (var g = Graphics.FromImage(bmp)) OnPaint(new PaintEventArgs(g, ClientRectangle));
                    FixGdiAlpha(bmp, Theme.PaintingOnGlass ? Theme.GlassTint.A : 255);
                    IntPtr hb = bmp.GetHbitmap(Color.FromArgb(0)), mem = CreateCompatibleDC(hdc), old = SelectObject(mem, hb);
                    BitBlt(hdc, 0, 0, Width, Height, mem, 0, 0, 0x00CC0020 /* SRCCOPY */);
                    SelectObject(mem, old); DeleteDC(mem); DeleteObject(hb);
                }
            }
            finally { EndPaint(Handle, ref ps); }
            return;
        }
        base.WndProc(ref m);
    }

    // GDI (ClearType text) writes alpha 0. Every surface here has a background of known alpha
    // (glass tint or opaque), so any alpha-0 pixel was drawn by GDI: give it that alpha and
    // premultiply its colour, which is what DWM expects.
    static void FixGdiAlpha(Bitmap bmp, int a)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var d = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadWrite, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        try
        {
            int n = d.Stride / 4 * d.Height;
            var px = new int[n];
            Marshal.Copy(d.Scan0, px, 0, n);
            for (int i = 0; i < n; i++)
            {
                int v = px[i];
                if ((v & unchecked((int)0xFF000000)) != 0) continue;
                int r = ((v >> 16) & 255) * a / 255, gg = ((v >> 8) & 255) * a / 255, b = (v & 255) * a / 255;
                px[i] = (a << 24) | (r << 16) | (gg << 8) | b;
            }
            Marshal.Copy(px, 0, d.Scan0, n);
        }
        finally { bmp.UnlockBits(d); }
    }

    protected Graphics Begin(PaintEventArgs e, Color bg)
    {
        var g = e.Graphics;
        Theme.PaintingOnGlass = Theme.Glass && bg == Theme.Bg;
        g.Clear(Theme.PaintingOnGlass ? Theme.GlassTint : bg);
        if (SeparatorBelow)
            using (var pen = new Pen(Theme.Separator)) g.DrawLine(pen, Theme.Px(12), Height - 1, Width, Height - 1);
        g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        return g;
    }

    // Keyboard focus ring, like macOS: accent-coloured halo, only when navigating by keyboard.
    protected void FocusRing(Graphics g, RectangleF r, float rad)
    {
        if (!Focused || !ShowFocusCues) return;
        using (var p = Theme.Round(r, rad))
        using (var pen = new Pen(Color.FromArgb(140, Theme.Accent), Theme.Px(2.5f))) g.DrawPath(pen, p);
    }

    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
}

// Grouped inset list ("card") with hairline separators inset from the leading edge.
class Card : AppleControl
{
    class Row { public Control C; public int H; public Padding P; public bool Sep; }
    readonly List<Row> rows = new List<Row>();
    readonly List<int> seps = new List<int>();
    int inset { get { return Theme.Px(4); } }

    public Card() { TabStop = false; }

    public void AddRow(Control c, int h, Padding p, bool separatorAfter)
    {
        rows.Add(new Row { C = c, H = h, P = p, Sep = separatorAfter });
        Controls.Add(c);
    }

    public int LayoutRows(int width)
    {
        int y = inset; seps.Clear();
        foreach (var r in rows)
        {
            bool own = r.Sep && r.P.Bottom == 0 && r.C is AppleControl; // row draws its own separator
            if (own) ((AppleControl)r.C).SeparatorBelow = true;
            int h = r.H - (r.Sep && !own ? 1 : 0);
            r.C.SetBounds(inset + r.P.Left, y + r.P.Top, width - inset * 2 - r.P.Horizontal, h - r.P.Vertical);
            y += r.H;
            if (r.Sep && !own) seps.Add(y - 1);
        }
        return y + inset;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = Begin(e, Theme.Bg);
        using (var p = Theme.Round(new RectangleF(0, 0, Width, Height), Theme.Px(10)))
        using (var b = new SolidBrush(Theme.Card)) g.FillPath(b, p);
        g.SmoothingMode = SmoothingMode.None;
        using (var pen = new Pen(Theme.Separator))
            foreach (int y in seps) g.DrawLine(pen, Theme.Px(16), y, Width, y);
    }
}

class Segmented : AppleControl
{
    readonly string[] items; int sel;
    public event Action<int> UserChanged;

    public Segmented(string[] items) { this.items = items; SetStyle(ControlStyles.Selectable, true); TabStop = true; }

    public int Selected { get { return sel; } set { if (sel == value) return; sel = value; AnimateTo(value, true); } }

    void Pick(int i)
    {
        if (i < 0 || i >= items.Length || i == sel) return;
        Selected = i;
        var h = UserChanged; if (h != null) h(i);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e); Focus();
        if (Enabled) Pick(Math.Min(items.Length - 1, e.X * items.Length / Math.Max(1, Width)));
    }

    protected override bool IsInputKey(Keys k) { return k == Keys.Left || k == Keys.Right || base.IsInputKey(k); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Left) Pick(sel - 1);
        if (e.KeyCode == Keys.Right) Pick(sel + 1);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = Begin(e, Theme.Card);
        float pad = Theme.Px(2), segW = (Width - pad * 2) / items.Length;
        var track = new RectangleF(0, 0, Width - 1, Height - 1);
        using (var p = Theme.Round(track, Theme.Px(8)))
        using (var b = new SolidBrush(Theme.Fill)) g.FillPath(b, p);

        // Hairline dividers between segments, hidden next to the selected pill.
        using (var pen = new Pen(Theme.Glass ? Theme.Mix(Theme.CardSolid, Theme.Secondary, 0.18f) : Theme.Mix(Theme.CardSolid, Theme.Secondary, 0.35f)))
            for (int i = 1; i < items.Length; i++)
            {
                if (i >= Anim - 0.01f && i <= Anim + 1.01f) continue;
                float x = pad + segW * i;
                g.DrawLine(pen, x, Height * 0.28f, x, Height * 0.72f);
            }

        var pill = new RectangleF(pad + Anim * segW, pad, segW, Height - pad * 2 - 1);
        if (!Theme.Dark && !Theme.Glass)
            using (var sp = Theme.Round(new RectangleF(pill.X, pill.Y + 1, pill.Width, pill.Height), Theme.Px(7)))
            using (var sb = new SolidBrush(Color.FromArgb(Enabled ? 30 : 10, 0, 0, 0))) g.FillPath(sb, sp);
        Color pillColor = Theme.Glass ? Theme.AccentFill : Theme.Pill; // Windows: selection uses the accent
        using (var p = Theme.Round(pill, Theme.Px(7)))
        using (var b = new SolidBrush(Enabled ? pillColor : Theme.Mix(Theme.CardSolid, pillColor, 0.4f))) g.FillPath(b, p);

        for (int i = 0; i < items.Length; i++)
        {
            var r = new Rectangle((int)(pad + segW * i), 0, (int)segW, Height);
            // Label colour follows how much of the sliding pill covers this segment, so text changes
            // as the highlight arrives instead of jumping before it gets there.
            float cover = Math.Max(0f, 1f - Math.Abs(Anim - i));
            Color tc = Theme.Glass ? Theme.Mix(Theme.En(Theme.Label, Enabled), Theme.OnAccent, cover) : Theme.En(Theme.Label, Enabled);
            Theme.Text(g, items[i], cover > 0.5f ? Theme.SmallSemi : Theme.Small, r, tc, TextFormatFlags.HorizontalCenter);
        }
        FocusRing(g, new RectangleF(1, 1, Width - 3, Height - 3), Theme.Px(8));
    }
}

// Row with a title and an iOS/macOS-style switch on the trailing edge.
class SwitchRow : AppleControl
{
    readonly string text; bool on;
    public event Action<bool> UserToggled;

    public SwitchRow(string text) { this.text = text; SetStyle(ControlStyles.Selectable, true); TabStop = true; }

    public bool On { get { return on; } set { if (on == value) return; on = value; AnimateTo(on ? 1 : 0, true); } }

    void Toggle() { if (!Enabled) return; On = !on; var h = UserToggled; if (h != null) h(on); }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); Focus(); }
    protected override void OnMouseClick(MouseEventArgs e) { base.OnMouseClick(e); Toggle(); }
    protected override void OnKeyDown(KeyEventArgs e) { base.OnKeyDown(e); if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) Toggle(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = Begin(e, Theme.Card);
        int sw = Theme.Px(40), sh = Theme.Px(24), x = Width - Theme.Px(12) - sw, y = (Height - sh) / 2;
        Theme.Text(g, text, Theme.Body, new Rectangle(Theme.Px(12), 0, x - Theme.Px(20), Height), Theme.En(Theme.Label, Enabled), 0);

        var track = new RectangleF(x, y, sw, sh);
        if (Theme.Glass)
        {
            // Windows 11 toggle: outlined when off, accent-filled when on, small knob that slides.
            sw = Theme.Px(40); sh = Theme.Px(20); x = Width - Theme.Px(12) - sw; y = (Height - sh) / 2;
            var tr = new RectangleF(x + 0.5f, y + 0.5f, sw - 1, sh - 1);
            using (var p = Theme.Round(tr, sh / 2f))
            {
                if (Anim > 0.01f)
                    using (var b = new SolidBrush(Color.FromArgb((int)(255 * Anim), Theme.En(Theme.AccentFill, Enabled)))) g.FillPath(b, p);
                if (Anim < 0.99f)
                    using (var pen = new Pen(Color.FromArgb((int)(255 * (1 - Anim)), Theme.En(Theme.Secondary, Enabled)), Math.Max(1f, Theme.Px(1)))) g.DrawPath(pen, p);
            }
            float kd = Theme.Px(12), kxx = x + Theme.Px(4) + (sw - kd - Theme.Px(8)) * Anim;
            using (var kb = new SolidBrush(Theme.En(Theme.Mix(Theme.Secondary, Theme.OnAccent, Anim), Enabled)))
                g.FillEllipse(kb, kxx, y + (sh - kd) / 2f, kd, kd);
            FocusRing(g, new RectangleF(x - 2, y - 2, sw + 4, sh + 4), sh / 2f + 2);
            return;
        }
        using (var p = Theme.Round(track, sh / 2f))
        using (var b = new SolidBrush(Theme.En(Theme.Mix(Theme.OffTrack, Theme.Green, Anim), Enabled))) g.FillPath(b, p);
        float d = sh - Theme.Px(4), kx = x + Theme.Px(2) + (sw - d - Theme.Px(4)) * Anim, ky = y + Theme.Px(2);
        using (var sb = new SolidBrush(Color.FromArgb(Enabled ? 45 : 15, 0, 0, 0))) g.FillEllipse(sb, kx, ky + 1, d, d);
        using (var kb = new SolidBrush(Enabled ? Color.White : Theme.Mix(Theme.CardSolid,Color.White, 0.6f))) g.FillEllipse(kb, kx, ky, d, d);
        FocusRing(g, new RectangleF(x - 2, y - 2, sw + 4, sh + 4), sh / 2f + 2);
    }
}

// Row with a title and a secondary value; optionally a pop-up button (up/down chevrons).
class ValueRow : AppleControl
{
    readonly string text; string value; readonly bool popup; bool pressed;
    public bool Disclosure; // navigation row: shows a › chevron instead of the pop-up chevrons
    public event Action<ValueRow> Clicked;

    public ValueRow(string text, string value, bool popup)
    {
        this.text = text; this.value = value; this.popup = popup;
        if (popup) { SetStyle(ControlStyles.Selectable, true); TabStop = true; } else TabStop = false;
    }

    public string Value { get { return value; } set { this.value = value; Invalidate(); } }

    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (popup && Enabled) { Focus(); pressed = true; Invalidate(); } }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!pressed) return; pressed = false; Invalidate();
        if (ClientRectangle.Contains(e.Location)) Fire();
    }
    protected override void OnKeyDown(KeyEventArgs e) { base.OnKeyDown(e); if (popup && (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter || e.KeyCode == Keys.Down)) Fire(); }
    void Fire() { var h = Clicked; if (h != null && Enabled) h(this); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = Begin(e, pressed ? Theme.Fill : Theme.Card);
        int right = Width - Theme.Px(12);
        if (popup && Disclosure)
        {
            float cx = right - Theme.Px(3), cy = Height / 2f, u = Theme.Px(4);
            using (var pen = new Pen(Theme.En(Theme.Secondary, Enabled), Theme.Px(1.6f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                g.DrawLines(pen, new[] { new PointF(cx - u / 2, cy - u), new PointF(cx + u / 2, cy), new PointF(cx - u / 2, cy + u) });
            right -= Theme.Px(16);
        }
        else if (popup)
        {
            // Pop-up button indicator: stacked up/down chevrons.
            float cx = right - Theme.Px(4), cy = Height / 2f, w = Theme.Px(3.5f), h = Theme.Px(2.5f), gap = Theme.Px(2);
            using (var pen = new Pen(Theme.En(Theme.Secondary, Enabled), Theme.Px(1.5f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                g.DrawLines(pen, new[] { new PointF(cx - w, cy - gap), new PointF(cx, cy - gap - h), new PointF(cx + w, cy - gap) });
                g.DrawLines(pen, new[] { new PointF(cx - w, cy + gap), new PointF(cx, cy + gap + h), new PointF(cx + w, cy + gap) });
            }
            right -= Theme.Px(16);
        }
        int vw = Theme.Width(value ?? "", Theme.Body);
        Theme.Text(g, value ?? "", Theme.Body, new Rectangle(right - vw, 0, vw + 1, Height), Theme.En(Theme.Secondary, Enabled), 0);
        Theme.Text(g, text, Theme.Body, new Rectangle(Theme.Px(12), 0, right - vw - Theme.Px(24), Height), Theme.En(Theme.Label, Enabled), 0);
        FocusRing(g, new RectangleF(2, 2, Width - 5, Height - 5), Theme.Px(6));
    }
}

class Slider : AppleControl
{
    public int Min = 0, Max = 20; int val; bool drag;
    public event Action<int> UserChanged;
    public Slider() { SetStyle(ControlStyles.Selectable, true); TabStop = true; }

    public int Value { get { return val; } set { val = Math.Max(Min, Math.Min(Max, value)); Invalidate(); } }

    float Knob { get { return Theme.Px(20); } }
    void SetFromX(int x)
    {
        float l = Knob / 2, r = Width - Knob / 2;
        int v = (int)Math.Round((x - l) / Math.Max(1, r - l) * (Max - Min)) + Min;
        Change(v);
    }
    void Change(int v)
    {
        v = Math.Max(Min, Math.Min(Max, v));
        if (v == val) return;
        val = v; Invalidate();
        var h = UserChanged; if (h != null) h(v);
    }

    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (!Enabled) return; Focus(); drag = true; Capture = true; SetFromX(e.X); }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (drag) SetFromX(e.X); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); drag = false; Capture = false; }
    protected override bool IsInputKey(Keys k) { return k == Keys.Left || k == Keys.Right || base.IsInputKey(k); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Down) Change(val - 1);
        if (e.KeyCode == Keys.Right || e.KeyCode == Keys.Up) Change(val + 1);
        if (e.KeyCode == Keys.Home) Change(Min);
        if (e.KeyCode == Keys.End) Change(Max);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = Begin(e, Theme.Card);
        float k = Knob, l = k / 2, r = Width - k / 2, cy = Height / 2f, th = Theme.Px(4);
        float x = l + (r - l) * (val - Min) / (float)(Max - Min);
        // With a negative minimum (EQ bands) the fill grows from the centre (0 dB) outwards.
        float x0 = Min < 0 ? l + (r - l) * (0 - Min) / (float)(Max - Min) : l;
        using (var p = Theme.Round(new RectangleF(l, cy - th / 2, r - l, th), th / 2))
        using (var b = new SolidBrush(Theme.En(Theme.OffTrack, Enabled))) g.FillPath(b, p);
        if (Min < 0)
            using (var b = new SolidBrush(Theme.En(Theme.Secondary, Enabled))) g.FillRectangle(b, x0 - 0.75f, cy - th * 1.25f, 1.5f, th * 2.5f);
        float fa = Math.Min(x0, x), fb = Math.Max(x0, x);
        if (fb - fa > 0.5f)
            using (var p = Theme.Round(new RectangleF(fa, cy - th / 2, fb - fa, th), th / 2))
            using (var b = new SolidBrush(Theme.En(Theme.AccentFill, Enabled))) g.FillPath(b, p);
        if (Theme.Glass)
        {
            // Windows 11 thumb: accent dot inside a neutral ring.
            float ko = Theme.Px(20), ki = Theme.Px(10);
            using (var rb = new SolidBrush(Theme.Dark ? Color.FromArgb(0x45, 0x45, 0x45) : Color.White)) g.FillEllipse(rb, x - ko / 2, cy - ko / 2, ko, ko);
            using (var pen = new Pen(Color.FromArgb(Theme.Dark ? 40 : 30, Theme.Dark ? Color.White : Color.Black))) g.DrawEllipse(pen, x - ko / 2, cy - ko / 2, ko, ko);
            using (var ib = new SolidBrush(Theme.En(Theme.AccentFill, Enabled))) g.FillEllipse(ib, x - ki / 2, cy - ki / 2, ki, ki);
            FocusRing(g, new RectangleF(x - ko / 2 - 2, cy - ko / 2 - 2, ko + 4, ko + 4), ko / 2 + 2);
            return;
        }
        using (var sb = new SolidBrush(Color.FromArgb(Enabled ? 50 : 15, 0, 0, 0))) g.FillEllipse(sb, x - k / 2, cy - k / 2 + 1, k, k);
        using (var kb = new SolidBrush(Enabled ? Color.White : Theme.Mix(Theme.CardSolid,Color.White, 0.6f))) g.FillEllipse(kb, x - k / 2, cy - k / 2, k, k);
        if (!Theme.Dark) using (var pen = new Pen(Color.FromArgb(30, 0, 0, 0))) g.DrawEllipse(pen, x - k / 2, cy - k / 2, k, k);
        FocusRing(g, new RectangleF(x - k / 2 - 2, cy - k / 2 - 2, k + 4, k + 4), k / 2 + 2);
    }
}

// Device header: product glyph, name, connection status, battery.
class Header : AppleControl
{
    public string DeviceName = "WH-1000XM4", Status = "Connecting…";
    public int Battery = -1; public bool Charging, ShowReconnect;
    public event Action Reconnect, OpenSettings;
    public event Action<Rectangle> OpenPower; // passes the button bounds so the menu can sit under it
    public bool PowerEnabled;
    Rectangle reconnectRect;
    bool gearHot, powerHot;

    public Header() { TabStop = false; }
    Rectangle GearRect { get { int s = Theme.Px(32); return new Rectangle(Width - s, 0, s, s); } }
    Rectangle PowerRect { get { int s = Theme.Px(32); return new Rectangle(Width - s * 2 - Theme.Px(4), 0, s, s); } }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (ShowReconnect && reconnectRect.Contains(e.Location)) { var h = Reconnect; if (h != null) h(); }
        if (GearRect.Contains(e.Location)) { var h = OpenSettings; if (h != null) h(); }
        if (PowerEnabled && PowerRect.Contains(e.Location)) { var h = OpenPower; if (h != null) h(PowerRect); }
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        bool hot = GearRect.Contains(e.Location), ph = PowerEnabled && PowerRect.Contains(e.Location);
        if (hot != gearHot || ph != powerHot) { gearHot = hot; powerHot = ph; Invalidate(); }
    }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (gearHot || powerHot) { gearHot = powerHot = false; Invalidate(); } }

    // Toolbar-style icon button: hover circle, Windows icon-font glyph.
    void DrawIconButton(Graphics g, Rectangle r, string glyph, bool hot, bool enabled)
    {
        if (hot)
            using (var p = Theme.Round(r, r.Width / 2f))
            using (var b = new SolidBrush(Theme.Card)) g.FillPath(b, p);
        Color c = !enabled ? Theme.Mix(Theme.Bg, Theme.Secondary, 0.4f) : hot ? Theme.Label : Theme.Secondary;
        Theme.Text(g, glyph, gearFont, r, c, TextFormatFlags.HorizontalCenter);
    }

    static Font gearFont;
    void DrawGear(Graphics g)
    {
        // Settings glyph from the Windows icon font, top-right like a toolbar button.
        if (gearFont == null)
        {
            string fam = "Segoe MDL2 Assets";
            using (var fc = new InstalledFontCollection())
                foreach (var f in fc.Families) if (f.Name == "Segoe Fluent Icons") fam = f.Name;
            gearFont = new Font(fam, 12f);
        }
        DrawIconButton(g, PowerRect, "", powerHot, PowerEnabled); // power, left of the gear
        var r = GearRect;
        if (gearHot)
            using (var p = Theme.Round(r, r.Width / 2f))
            using (var b = new SolidBrush(Theme.Card)) g.FillPath(b, p);
        Theme.Text(g, "", gearFont, r, gearHot ? Theme.Label : Theme.Secondary, TextFormatFlags.HorizontalCenter);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        // Large device image with name and status centred beneath (like the AirPods pop-up).
        var g = Begin(e, Theme.Bg);
        int s = Theme.Px(130);
        g.DrawImage(HeadphoneArt.Hero(s), (Width - s) / 2, 0, s, s);
        DrawGear(g);

        int ny = s + Theme.Px(4);
        Theme.Text(g, DeviceName, Theme.Title, new Rectangle(0, ny, Width, Theme.Px(26)), Theme.Label, TextFormatFlags.HorizontalCenter);
        var line = new Rectangle(0, ny + Theme.Px(28), Width, Theme.Px(20));

        // Status (and the Connect link) centred under the name; battery sits top-left, across from the gear.
        string dot = "  ·  ", pct = Battery + "%" + (Charging ? ", Charging" : ""), link = "Connect";
        int total = Theme.Width(Status, Theme.Body);
        if (ShowReconnect) total += Theme.Width(dot, Theme.Body) + Theme.Width(link, Theme.BodySemi);
        int x = (Width - total) / 2;

        Theme.Text(g, Status, Theme.Body, new Rectangle(x, line.Y, Width - x, line.Height), Theme.Secondary, 0);
        x += Theme.Width(Status, Theme.Body);

        reconnectRect = Rectangle.Empty;
        if (ShowReconnect)
        {
            Theme.Text(g, dot, Theme.Body, new Rectangle(x, line.Y, Theme.Width(dot, Theme.Body) + 1, line.Height), Theme.Secondary, 0);
            x += Theme.Width(dot, Theme.Body);
            int w = Theme.Width(link, Theme.BodySemi);
            reconnectRect = new Rectangle(x - Theme.Px(4), line.Y - Theme.Px(4), w + Theme.Px(8), line.Height + Theme.Px(8));
            Theme.Text(g, link, Theme.BodySemi, new Rectangle(x, line.Y, w + 1, line.Height), Theme.Accent, 0);
        }

        if (Battery >= 0)
        {
            // Battery glyph + percentage, vertically centred with the gear button (top row).
            line = new Rectangle(0, (Theme.Px(32) - Theme.Px(20)) / 2, Width / 2, Theme.Px(20));
            x = Theme.Px(2);
            float bw = Theme.Px(22), bh = Theme.Px(11), by = line.Y + (line.Height - bh) / 2f;
            using (var pen = new Pen(Theme.Secondary, Math.Max(1, Theme.Px(1))))
            using (var p = Theme.Round(new RectangleF(x, by, bw, bh), Theme.Px(3))) g.DrawPath(pen, p);
            using (var nb = new SolidBrush(Theme.Secondary)) g.FillRectangle(nb, x + bw + Theme.Px(1), by + bh * 0.3f, Theme.Px(1.5f), bh * 0.4f);
            Color fc = Charging ? Theme.Green : (Battery <= 20 ? Theme.Red : Theme.Label);
            float inner = (bw - Theme.Px(4)) * Battery / 100f;
            using (var fb = new SolidBrush(fc))
            using (var p = Theme.Round(new RectangleF(x + Theme.Px(2), by + Theme.Px(2), Math.Max(1, inner), bh - Theme.Px(4)), Theme.Px(1.5f))) g.FillPath(fb, p);
            x += (int)bw + Theme.Px(8);
            Theme.Text(g, pct, Theme.Body, new Rectangle(x, line.Y, Width / 2 - x, line.Height), Theme.Secondary, 0);
        }
    }
}

// Section header with an optional ⓘ: hovering (or clicking) it shows the explanation in a
// themed tooltip, so the page stays clean (like the help buttons in macOS settings).
class InfoHeader : AppleControl
{
    readonly string title; public string Info;
    bool hot;
    static ToolTip tip;
    public InfoHeader(string title, string info) { this.title = title; Info = info; TabStop = false; }

    Rectangle IconRect { get { int s = Theme.Px(15); return new Rectangle(Theme.Width(title, Theme.Section) + Theme.Px(6), (Height - s) / 2, s, s); } }

    static ToolTip Tip
    {
        get
        {
            if (tip != null) return tip;
            tip = new ToolTip { OwnerDraw = true, UseAnimation = false, UseFading = false };
            tip.Popup += (s, e) =>
            {
                var sz = TextRenderer.MeasureText(tip.GetToolTip(e.AssociatedControl) ?? CurrentText, Theme.Footnote, new Size(Theme.Px(260), 0), TextFormatFlags.WordBreak);
                e.ToolTipSize = new Size(sz.Width + Theme.Px(20), sz.Height + Theme.Px(14));
            };
            tip.Draw += (s, e) =>
            {
                e.Graphics.Clear(Theme.Menu);
                using (var pen = new Pen(Theme.Separator)) e.Graphics.DrawRectangle(pen, 0, 0, e.Bounds.Width - 1, e.Bounds.Height - 1);
                var r = new Rectangle(Theme.Px(10), Theme.Px(7), e.Bounds.Width - Theme.Px(20), e.Bounds.Height - Theme.Px(14));
                TextRenderer.DrawText(e.Graphics, e.ToolTipText, Theme.Footnote, r, Theme.Label, TextFormatFlags.WordBreak);
            };
            return tip;
        }
    }
    static string CurrentText;

    void ShowTip()
    {
        if (Info == null) return;
        if (shown) return;
        CurrentText = Info; shown = true;
        // Below the icon, clear of the pointer: a tip under the cursor steals the hover and flickers.
        var r = IconRect;
        Tip.Show(Info, this, r.Left - Theme.Px(4), r.Bottom + Theme.Px(8));
    }
    bool shown;
    void HideTip() { shown = false; if (tip != null) tip.Hide(this); }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        bool h = Info != null && IconRect.Contains(e.Location);
        if (h == hot) return;
        hot = h; Invalidate();
        if (h) ShowTip(); else HideTip();
    }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (hot) { hot = false; Invalidate(); HideTip(); } }
    protected override void OnMouseClick(MouseEventArgs e) { base.OnMouseClick(e); if (hot) ShowTip(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = Begin(e, Theme.Bg);
        Theme.Text(g, title, Theme.Section, new Rectangle(0, 0, Width, Height), Theme.Secondary, 0);
        if (Info == null) return;
        var r = IconRect; Color c = hot ? Theme.Accent : Theme.Secondary;
        using (var pen = new Pen(c, Math.Max(1f, Theme.Px(1.2f)))) g.DrawEllipse(pen, r.X + 0.5f, r.Y + 0.5f, r.Width - 1.5f, r.Height - 1.5f);
        using (var b = new SolidBrush(c))
        {
            float cx = r.X + r.Width / 2f, u = r.Width / 15f;
            g.FillEllipse(b, cx - 1.1f * u, r.Y + 3.2f * u, 2.2f * u, 2.2f * u);   // dot
            g.FillRectangle(b, cx - 0.9f * u, r.Y + 6.4f * u, 1.8f * u, 5.2f * u); // stem
        }
    }
}

// Static text: section headers and explanatory footers.
class TextBlock : AppleControl
{
    readonly Font font; readonly bool secondary;
    public bool Centered;
    public TextBlock(string text, Font font, bool secondary) { Text = text; this.font = font; this.secondary = secondary; TabStop = false; }
    const TextFormatFlags F = TextFormatFlags.WordBreak | TextFormatFlags.NoPadding;
    public int Measure(int width) { return TextRenderer.MeasureText(Text, font, new Size(width, 0), F).Height; }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = Begin(e, Theme.Bg);
        Color c = secondary ? Theme.Secondary : Theme.Label;
        if (Theme.Glass) // single line through the glass-safe text path
            Theme.Text(g, Text, font, ClientRectangle, c, Centered ? TextFormatFlags.HorizontalCenter : 0);
        else
            TextRenderer.DrawText(g, Text, font, ClientRectangle, c, F | (Centered ? TextFormatFlags.HorizontalCenter : 0));
    }
}

// macOS-style menus: rounded, accent highlight, checkmark for the current item.
class AppleMenuRenderer : ToolStripRenderer
{
    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) { e.Graphics.Clear(Theme.Menu); }
    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using (var pen = new Pen(Theme.Separator)) e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
    }
    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled) return;
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        // Windows 11 menus use a subtle grey highlight; macOS uses the accent.
        Color hl = Theme.Glass ? (Theme.Dark ? Color.FromArgb(0x3A, 0x3A, 0x3A) : Color.FromArgb(0xEA, 0xEA, 0xEA)) : Theme.Accent;
        using (var p = Theme.Round(new RectangleF(Theme.Px(5), 1, e.Item.Width - Theme.Px(10), e.Item.Height - 2), Theme.Px(5)))
        using (var b = new SolidBrush(hl)) g.FillPath(b, p);
    }
    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Selected && e.Item.Enabled && !Theme.Glass ? Color.White : (e.Item.Enabled ? Theme.Label : Theme.Secondary);
        base.OnRenderItemText(e);
    }
    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = e.ImageRectangle; float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, u = Theme.Px(3.5f);
        using (var pen = new Pen(e.Item.Selected ? Color.White : Theme.Label, Theme.Px(1.8f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            g.DrawLines(pen, new[] { new PointF(cx - u, cy), new PointF(cx - u * 0.3f, cy + u * 0.8f), new PointF(cx + u * 1.1f, cy - u) });
    }
    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        int y = e.Item.Height / 2;
        using (var pen = new Pen(Theme.Separator)) e.Graphics.DrawLine(pen, Theme.Px(10), y, e.Item.Width - Theme.Px(10), y);
    }
    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }
    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e) { }

    public static ContextMenuStrip Create()
    {
        var m = new ContextMenuStrip { Renderer = new AppleMenuRenderer(), ShowImageMargin = false, ShowCheckMargin = true, Font = Theme.Body, Padding = new Padding(0, Theme.Px(5), 0, Theme.Px(5)) };
        m.HandleCreated += (s, e) => Theme.WindowChrome(m.Handle, true);
        return m;
    }

    public static ToolStripMenuItem Item(string text, bool check, EventHandler click)
    {
        return new ToolStripMenuItem(text, null, click) { Checked = check, Padding = new Padding(0, Theme.Px(3), Theme.Px(12), Theme.Px(3)) };
    }
}

// ================================================================ Profiles + settings
class Profile
{
    public int Mode, Level, Eq; public bool Voice, Dsee;
    public string EqCustom; // name of a custom EQ profile, or null for a built-in preset
    public override string ToString() { return Mode + "," + Level + "," + (Voice ? 1 : 0) + "," + Eq + "," + (Dsee ? 1 : 0) + "," + (EqCustom ?? ""); }
    public static Profile Parse(string s)
    {
        try
        {
            var p = s.Split(',');
            return new Profile { Mode = int.Parse(p[0]), Level = int.Parse(p[1]), Voice = p[2] == "1", Eq = int.Parse(p[3]), Dsee = p[4] == "1",
                EqCustom = p.Length > 5 && p[5].Length > 0 ? p[5] : null };
        }
        catch { return null; }
    }
}

class Settings
{
    public string OnConnect = "last"; // last | profile | none
    public Profile Last, Saved;
    public List<EqProfile> Eqs = new List<EqProfile>();
    public bool Hotkeys, OffWithPc, BatteryAlerts = true;
    public EqProfile FindEq(string name) { return name == null ? null : Eqs.Find(e => e.Name == name); }
    static string PathName { get { return Path.Combine(AppData.Dir, "settings.ini"); } }

    public static Settings Load()
    {
        var s = new Settings();
        try
        {
            foreach (var line in File.ReadAllLines(PathName))
            {
                int i = line.IndexOf('='); if (i < 0) continue;
                string k = line.Substring(0, i).Trim(), v = line.Substring(i + 1).Trim();
                if (k == "OnConnect") s.OnConnect = v;
                if (k == "Last") s.Last = Profile.Parse(v);
                if (k == "Saved") s.Saved = Profile.Parse(v);
                if (k == "Hotkeys") s.Hotkeys = v == "1";
                if (k == "OffWithPc") s.OffWithPc = v == "1";
                if (k == "BatteryAlerts") s.BatteryAlerts = v == "1";
                if (k == "EqProfile") { var e = EqProfile.Parse(v); if (e != null) s.Eqs.Add(e); }
            }
        }
        catch { }
        return s;
    }

    public static bool ReadOnly; // design previews must never write the user's settings
    public void Save()
    {
        if (ReadOnly) return;
        try
        {
            var lines = new List<string> {
                "OnConnect=" + OnConnect,
                "Last=" + (Last == null ? "" : Last.ToString()),
                "Saved=" + (Saved == null ? "" : Saved.ToString()),
                "Hotkeys=" + (Hotkeys ? "1" : "0"),
                "OffWithPc=" + (OffWithPc ? "1" : "0"),
                "BatteryAlerts=" + (BatteryAlerts ? "1" : "0") };
            foreach (var e in Eqs) lines.Add("EqProfile=" + e);
            File.WriteAllLines(PathName, lines.ToArray());
        }
        catch { }
    }
}

// Plain button row: accent-coloured label, highlights while pressed.
class ButtonRow : AppleControl
{
    string text; bool pressed;
    public event Action Clicked;
    public ButtonRow(string text) { this.text = text; SetStyle(ControlStyles.Selectable, true); TabStop = true; }
    public string Label { get { return text; } set { text = value; Invalidate(); } }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (Enabled) { Focus(); pressed = true; Invalidate(); } }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!pressed) return; pressed = false; Invalidate();
        if (ClientRectangle.Contains(e.Location)) Fire();
    }
    protected override void OnKeyDown(KeyEventArgs e) { base.OnKeyDown(e); if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) Fire(); }
    void Fire() { var h = Clicked; if (h != null && Enabled) h(); }
    public bool Destructive; // red, for delete actions
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = Begin(e, pressed ? Theme.Fill : Theme.Card);
        Theme.Text(g, text, Theme.Body, new Rectangle(Theme.Px(12), 0, Width - Theme.Px(24), Height), Theme.En(Destructive ? Theme.Red : Theme.Accent, Enabled), 0);
        FocusRing(g, new RectangleF(2, 2, Width - 5, Height - 5), Theme.Px(6));
    }
}

// Navigation bar for pushed pages: "‹ Back" on the leading edge, centred title.
class NavBar : AppleControl
{
    public string Title, BackText, RightText; // RightText: optional trailing action such as "Done"
    public event Action Back, RightAction;
    Rectangle backRect, rightRect; bool hot, rightHot;
    public NavBar(string title, string back) { Title = title; BackText = back; TabStop = false; }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        bool h = backRect.Contains(e.Location), rh = rightRect.Contains(e.Location);
        if (h != hot || rh != rightHot) { hot = h; rightHot = rh; Invalidate(); }
    }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot = rightHot = false; Invalidate(); }
    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (backRect.Contains(e.Location)) { var h = Back; if (h != null) h(); }
        else if (rightRect.Contains(e.Location)) { var h = RightAction; if (h != null) h(); }
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = Begin(e, Theme.Bg);
        Color ac = hot ? Theme.Mix(Theme.Accent, Theme.Label, 0.25f) : Theme.Accent;
        float cy = Height / 2f, u = Theme.Px(6);
        using (var pen = new Pen(ac, Theme.Px(2f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            g.DrawLines(pen, new[] { new PointF(Theme.Px(6) + u * 0.6f, cy - u), new PointF(Theme.Px(6), cy), new PointF(Theme.Px(6) + u * 0.6f, cy + u) });
        int bw = Theme.Width(BackText, Theme.Body);
        Theme.Text(g, BackText, Theme.Body, new Rectangle(Theme.Px(18), 0, bw + 2, Height), ac, 0);
        backRect = new Rectangle(0, 0, Theme.Px(22) + bw, Height);
        rightRect = Rectangle.Empty;
        if (RightText != null)
        {
            int rw = Theme.Width(RightText, Theme.BodySemi);
            rightRect = new Rectangle(Width - rw - Theme.Px(12), 0, rw + Theme.Px(12), Height);
            Color rc = rightHot ? Theme.Mix(Theme.Accent, Theme.Label, 0.25f) : Theme.Accent;
            Theme.Text(g, RightText, Theme.BodySemi, new Rectangle(Width - rw - Theme.Px(2), 0, rw + 1, Height), rc, 0);
        }
        int side = Math.Max(backRect.Width, rightRect.Width) + Theme.Px(8);
        Theme.Text(g, Title, Theme.BodySemi, new Rectangle(side, 0, Width - side * 2, Height), Theme.Label, TextFormatFlags.HorizontalCenter);
    }
}

// EQ band: label, centre-zero slider, dB value.
class BandRow : AppleControl
{
    readonly string label; public readonly Slider Slider = new Slider { Min = -10, Max = 10 };
    public event Action<int> Changed;
    public BandRow(string label, int value)
    {
        this.label = label; TabStop = false;
        Slider.Value = value;
        Slider.UserChanged += v => { Invalidate(); var h = Changed; if (h != null) h(v); };
        Controls.Add(Slider);
    }
    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        int l = Theme.Px(92), r = Theme.Px(48);
        // One pixel short when this row draws a separator, so the slider doesn't cover the hairline.
        Slider.SetBounds(l, 0, Math.Max(10, Width - l - r), Height - (SeparatorBelow ? 1 : 0));
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = Begin(e, Theme.Card);
        Theme.Text(g, label, Theme.Body, new Rectangle(Theme.Px(12), 0, Theme.Px(80), Height), Theme.Label, 0);
        int v = Slider.Value;
        Theme.Text(g, (v > 0 ? "+" : "") + v, Theme.Body, new Rectangle(Width - Theme.Px(44), 0, Theme.Px(32), Height), Theme.Secondary, TextFormatFlags.Right);
    }
}

// Text field row, drawn by the app itself (a native TextBox can't render on the translucent window):
// label on the leading edge, value right-aligned. Click or Tab to edit; Enter or Esc finishes.
// Supports typing, Backspace/Delete, arrows, Home/End, Ctrl+A, Ctrl+C, Ctrl+V.
class NameRow : AppleControl
{
    readonly string label;
    string text; int caret; bool all; bool caretOn = true;
    public int MaxLength = 32;
    public event EventHandler NameChanged;
    readonly System.Windows.Forms.Timer blink = new System.Windows.Forms.Timer { Interval = 530 };

    public NameRow(string label, string value)
    {
        this.label = label; text = value ?? ""; caret = text.Length;
        SetStyle(ControlStyles.Selectable, true); TabStop = true;
        Cursor = Cursors.IBeam;
        blink.Tick += (s, e) => { caretOn = !caretOn; Invalidate(); };
    }

    public override string Text { get { return text; } set { text = value ?? ""; caret = Math.Min(caret, text.Length); Invalidate(); } }

    void Edit(string t, int c)
    {
        if (t.Length > MaxLength) return;
        text = t; caret = Math.Max(0, Math.Min(c, t.Length)); all = false; Touch();
        var h = NameChanged; if (h != null) h(this, EventArgs.Empty);
    }
    void Touch() { caretOn = true; blink.Stop(); blink.Start(); Invalidate(); }
    void Insert(string s)
    {
        s = s.Replace("\r", "").Replace("\n", "");
        if (all) Edit(s, s.Length); else Edit(text.Insert(caret, s), caret + s.Length);
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); all = false; caret = text.Length; Touch(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); blink.Stop(); all = false; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); Focus(); caret = text.Length; all = false; Touch(); }
    protected override bool IsInputKey(Keys k)
    {
        var kc = k & Keys.KeyCode;
        return kc == Keys.Left || kc == Keys.Right || kc == Keys.Home || kc == Keys.End || base.IsInputKey(k);
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.KeyCode)
        {
            case Keys.Left: caret = all ? 0 : Math.Max(0, caret - 1); all = false; Touch(); break;
            case Keys.Right: caret = all ? text.Length : Math.Min(text.Length, caret + 1); all = false; Touch(); break;
            case Keys.Home: caret = 0; all = false; Touch(); break;
            case Keys.End: caret = text.Length; all = false; Touch(); break;
            case Keys.Delete:
                if (all) Edit("", 0); else if (caret < text.Length) Edit(text.Remove(caret, 1), caret);
                break;
            case Keys.Enter: case Keys.Escape:
                { var f = FindForm(); if (f != null) f.ActiveControl = null; }
                e.Handled = true; e.SuppressKeyPress = true; break;
            case Keys.A: if (e.Control) { all = true; Touch(); e.SuppressKeyPress = true; } break;
            case Keys.C: if (e.Control) { try { Clipboard.SetText(text); } catch { } e.SuppressKeyPress = true; } break;
            case Keys.V: if (e.Control) { try { if (Clipboard.ContainsText()) Insert(Clipboard.GetText()); } catch { } e.SuppressKeyPress = true; } break;
        }
    }
    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        base.OnKeyPress(e);
        if (e.KeyChar == '\b')
        {
            if (all) Edit("", 0); else if (caret > 0) Edit(text.Remove(caret - 1, 1), caret - 1);
        }
        else if (!char.IsControl(e.KeyChar)) Insert(e.KeyChar.ToString());
        e.Handled = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = Begin(e, Theme.Card);
        Theme.Text(g, label, Theme.Body, new Rectangle(Theme.Px(12), 0, Theme.Px(70), Height), Theme.Label, 0);
        int right = Width - Theme.Px(12), tw = Theme.Width(text, Theme.Body), left = right - tw;
        if (Focused && all && tw > 0)
            using (var b = new SolidBrush(Color.FromArgb(110, Theme.AccentFill)))
                g.FillRectangle(b, left, (Height - Theme.Px(20)) / 2, tw, Theme.Px(20));
        Theme.Text(g, text, Theme.Body, new Rectangle(Theme.Px(84), 0, right - Theme.Px(84), Height), Focused ? Theme.Label : Theme.Secondary, TextFormatFlags.Right);
        if (Focused && caretOn && !all)
        {
            int cx = left + Theme.Width(text.Substring(0, caret), Theme.Body);
            using (var b = new SolidBrush(Theme.AccentFill)) g.FillRectangle(b, cx, (Height - Theme.Px(18)) / 2, Math.Max(1, Theme.Px(1.5f)), Theme.Px(18));
        }
    }
}

class EqProfile
{
    public string Name; public int[] Bands = new int[6]; // Clear Bass, 400 Hz, 1k, 2.5k, 6.3k, 16k (-10..+10)
    public override string ToString() { return Name + "|" + string.Join(",", Array.ConvertAll(Bands, b => b.ToString())); }
    public static EqProfile Parse(string s)
    {
        try
        {
            int i = s.LastIndexOf('|');
            var p = new EqProfile { Name = s.Substring(0, i) };
            var v = s.Substring(i + 1).Split(',');
            for (int k = 0; k < 6; k++) p.Bands[k] = Math.Max(-10, Math.Min(10, int.Parse(v[k])));
            return p;
        }
        catch { return null; }
    }
    public static string Clean(string name) { return (name ?? "").Replace("|", "").Replace(",", "").Replace("=", "").Trim(); }
}

// ================================================================ Window
class MainForm : Form
{
    enum Mode { NC, Ambient, Off }
    static readonly string[] ModeNames = { "Noise Canceling", "Ambient", "Off" };
    static readonly string[] EqNames = { "Off", "Bright", "Excited", "Mellow", "Relaxed", "Vocal", "Treble Boost", "Bass Boost", "Speech", "Manual", "Custom 1", "Custom 2" };
    static readonly byte[] EqCodes = { 0x00, 0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0xA0, 0xA1, 0xA2 };

    SonyLink link;
    Mode mode = Mode.NC;
    int eqIndex;
    bool connected;
    readonly bool testMode;
    readonly string logPath;

    Header header;
    ValueRow applyRow;
    ButtonRow saveRow;
    Settings settings = Settings.Load();
    System.Windows.Forms.Timer retry, saveLast;
    bool connecting;
    int connectedAt = -100000;
    volatile string headsetName; ulong headsetAddr; // cached after the first lookup
    Segmented seg;
    ValueRow levelRow, eqRow;
    Slider level;
    SwitchRow voice, dsee;
    NotifyIcon tray;
    ContextMenuStrip trayMenu;
    System.Windows.Forms.Timer debounce, batteryTimer;

    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

    static int Px(float v) { return Theme.Px(v); }

    public MainForm(bool testMode)
    {
        this.testMode = testMode;
        logPath = Path.Combine(AppData.Dir, "xmpanel.log");
        try { File.WriteAllText(logPath, ""); } catch { }
        SonyLink.Log = Log;

        Text = "XM Panel";
        // Flyout from the notification area, like the Windows quick settings / tray overflow.
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
        StartPosition = FormStartPosition.Manual; KeyPreview = true;
        KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) HideFlyout(); };
        Deactivate += (s, e) => HideFlyout();
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Theme.Bg;
        Icon = MakeIcon();
        DoubleBuffered = true;

        W = Px(400); M = Px(20); cw = W - M * 2;
        int y = Px(8);
        mainPage = NewPage(); target = mainPage;

        header = new Header();
        header.Reconnect += StartConnect;
        header.OpenSettings += () => { BuildSettingsPage(); ShowPage(settingsPage); };
        // Power button: a one-item menu acts as the confirmation (clicking elsewhere cancels).
        header.OpenPower += r =>
        {
            var m = AppleMenuRenderer.Create();
            m.ShowCheckMargin = false; // single action, no checkmark column
            var item = AppleMenuRenderer.Item("Turn Off " + header.DeviceName, false, (s, e) => PowerOff());
            item.Padding = new Padding(Px(4), Px(3), Px(4), Px(3));
            m.Items.Add(item);
            m.Closed += (s, e) => Ui(() => m.Dispose());
            // Right edge anchored to the power button, opening downward-left: stays inside the panel.
            m.Show(header, new Point(r.Right, r.Bottom + Px(4)), ToolStripDropDownDirection.BelowLeft);
        };
        Place(header, M, ref y, cw, Px(184), Px(20));

        // --- Noise Control
        InfoSection("Noise Control", "Ambient Sound lets you hear your surroundings. Focus on Voice keeps voices clear while reducing other noise.", ref y);
        var card1 = new Card();
        seg = new Segmented(ModeNames);
        seg.UserChanged += i => { SetMode((Mode)i, false); SendNc(); };
        card1.AddRow(seg, Px(48), new Padding(Px(8), Px(8), Px(8), Px(8)), true);
        levelRow = new ValueRow("Ambient Sound Level", "20", false);
        card1.AddRow(levelRow, Px(40), Padding.Empty, false);
        level = new Slider { Value = 20 };
        level.UserChanged += v => { levelRow.Value = v.ToString(); debounce.Stop(); debounce.Start(); };
        card1.AddRow(level, Px(36), new Padding(Px(12), 0, Px(12), Px(8)), true);
        voice = new SwitchRow("Focus on Voice");
        voice.UserToggled += on => SendNc();
        card1.AddRow(voice, Px(44), Padding.Empty, false);
        GroupEnd(card1, ref y);

        // --- Sound
        InfoSection("Sound", "DSEE Extreme restores detail lost in compressed music. It uses more battery.", ref y);
        var card2 = new Card();
        eqRow = new ValueRow("Equalizer", EqNames[0], true);
        eqRow.Clicked += ShowEqMenu;
        card2.AddRow(eqRow, Px(44), Padding.Empty, true);
        dsee = new SwitchRow("DSEE Extreme");
        dsee.UserToggled += on => { if (connected) { link.Send(Cmd.DseeSet(on)); Changed(); } };
        card2.AddRow(dsee, Px(44), Padding.Empty, false);
        GroupEnd(card2, ref y);

        // --- Smart Controls
        InfoSection("Smart Controls", "Music pauses when you take the headset off and resumes when you put it back on.", ref y);
        var card4 = new Card();
        pauseOff = new SwitchRow("Pause When Taken Off");
        pauseOff.UserToggled += on => { if (connected && Cmd.HasPause) link.Send(Cmd.PauseSet(on)); };
        card4.AddRow(pauseOff, Px(44), Padding.Empty, false);
        GroupEnd(card4, ref y);
        pageHeight[mainPage] = y - Px(24) + Px(16);

        confirmTimer = new System.Windows.Forms.Timer { Interval = 6000 };
        confirmTimer.Tick += (s, e) => { confirmTimer.Stop(); if (powerConfirm) { powerConfirm = false; RebuildSettings(); } };
        settingsPage = NewPage(); editorPage = NewPage();
        BuildSettingsPage();
        ShowPage(mainPage);

        var trim = new System.Windows.Forms.Timer { Interval = 10000 }; // after start-up work is done
        trim.Tick += (s, e) => { trim.Stop(); trim.Dispose(); if (!Visible) TrimMemory(); };
        trim.Start();

        retry = new System.Windows.Forms.Timer { Interval = 8000 };
        // While disconnected: every few seconds ask Windows (cheap, no radio use) whether the headset
        // is connected, and only then open our control channel. No paging while the headset is off.
        retry.Tick += (s, e) =>
        {
            retry.Stop();
            if (connected || connecting || quitting) return;
            if (headsetAddr != 0 && SonyLink.IsConnectedToWindows(headsetAddr) == false) { retry.Interval = 3000; retry.Start(); return; }
            StartConnect(true);
        };
        saveLast = new System.Windows.Forms.Timer { Interval = 1000 };
        saveLast.Tick += (s, e) => { saveLast.Stop(); if (connected) { settings.Last = CaptureProfile(); settings.Save(); } };

        debounce = new System.Windows.Forms.Timer { Interval = 150 };
        debounce.Tick += (s, e) => { debounce.Stop(); SendNc(); };
        eqSend = new System.Windows.Forms.Timer { Interval = 120 };
        eqSend.Tick += (s, e) => { eqSend.Stop(); if (eqSendProfile != null) { ApplyEq(eqSendProfile); settings.Save(); } };
        batteryTimer = new System.Windows.Forms.Timer { Interval = 60000 };
        batteryTimer.Tick += (s, e) => { if (connected) link.Send(Cmd.BatteryGet()); };
        batteryTimer.Start();

        // Menu bar extra equivalent: tray icon with quick mode switching.
        trayMenu = AppleMenuRenderer.Create();
        trayMenu.Opening += (s, e) => BuildTrayMenu();
        BuildTrayMenu();
        tray = new NotifyIcon { Icon = MakeTrayIcon(),Text = "XM Panel", ContextMenuStrip = trayMenu, Visible = true };
        tray.MouseClick += (s, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            if (Visible) HideFlyout();
            else if (Environment.TickCount - lastHide > 300) ShowFlyout(); // click that just closed it shouldn't reopen it
        };

        SystemEvents.UserPreferenceChanged += OnThemeChanged;

        // Keep the icon on the taskbar instead of the ^ overflow (Windows 11 per-app setting).
        int tries = 0;
        var promote = new System.Windows.Forms.Timer { Interval = 2000 };
        promote.Tick += (s, e) => { if (PromoteTrayIcon() || ++tries > 10) promote.Stop(); };
        promote.Start();

        SetMode(Mode.NC, false);
        ApplyEnabled();
    }

    int lastHide = -1000;
    System.Windows.Forms.Timer slide;

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ClassStyle |= 0x20000; /* CS_DROPSHADOW */ return cp; }
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);
    void Chrome()
    {
        Theme.WindowChrome(Handle, true);
        Theme.Backdrop(Handle);
        Color c = Theme.Dark ? Color.FromArgb(60, 60, 62) : Color.FromArgb(210, 210, 214);
        int colorref = c.R | (c.G << 8) | (c.B << 16);
        try { DwmSetWindowAttribute(Handle, 34 /* DWMWA_BORDER_COLOR */, ref colorref, 4); } catch { }
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Chrome(); RegisterHotkeys(); }

    public void ShowFlyout()
    {
        Log("show flyout");
        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        int x = wa.Right - Width - Px(12), y = wa.Bottom - Height - Px(12);
        if (slide != null) slide.Stop();
        if (Theme.ReduceMotion)
        {
            Location = new Point(x, y); Opacity = 1; Show(); Activate(); return;
        }
        int off = Px(24), frame = 0;
        Location = new Point(x, y + off); Opacity = 0; Show(); Activate();
        slide = new System.Windows.Forms.Timer { Interval = 12 };
        slide.Tick += (s, e) =>
        {
            float t = Math.Min(1f, ++frame / 12f), ease = 1 - (1 - t) * (1 - t) * (1 - t);
            Top = y + (int)(off * (1 - ease)); Opacity = Math.Min(1.0, t * 1.6);
            if (t >= 1) slide.Stop();
        };
        slide.Start();
    }

    void HideFlyout()
    {
        if (!Visible) return;
        Log("hide flyout");
        Hide(); lastHide = Environment.TickCount;
        if (current != mainPage) { settings.Save(); ShowPage(mainPage); } // reopen on the main page next time
        TrimMemory(); // give back memory used while the panel was open
    }

    // Run on the UI thread from background threads (headset link, single-instance signal).
    // Once the window is gone (app quitting, Windows ending the session) updates are dropped.
    public void Ui(Action a)
    {
        try { if (!IsDisposed && IsHandleCreated) BeginInvoke(a); }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    public void PreviewPage(string page)
    {
        if (page == "settings") { BuildSettingsPage(); ShowPage(settingsPage); }
        if (page == "power") { powerConfirm = true; BuildSettingsPage(); ShowPage(settingsPage); }
        if (page == "disconnected") { connected = false; ApplyEnabled(); header.Battery = -1; SetStatus("Not Connected", true); }
        if (page == "editor") OpenEditor(new EqProfile { Name = "My EQ 1", Bands = new[] { 4, 2, 0, -1, 1, 3 } });
    }

    public void PreviewState()
    {
        tray.Visible = false;
        connected = true; SetStatus("Connected", false);
        OnMessage(new byte[] { 0x11, 0x00, 40, 0 });
        OnMessage(new byte[] { 0x67, 0x02, 0x11, 0x02, 0x00, 0x01, 0x01, 12 });
        OnMessage(new byte[] { 0x57, 0x01, 0xA1 });
    }

    bool quitting;
    public void Quit()
    {
        if (quitting) return; // Application.Exit closes the form, which calls Quit again
        quitting = true;
        retry.Stop();
        SystemEvents.UserPreferenceChanged -= OnThemeChanged;
        tray.Visible = false;
        if (link != null) link.Close();
        Application.Exit();
    }

    bool PromoteTrayIcon()
    {
        try
        {
            using (var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings", true))
            {
                if (root == null) return true; // not Windows 11 - nothing to do
                foreach (var name in root.GetSubKeyNames())
                    using (var k = root.OpenSubKey(name, true))
                    {
                        var path = k == null ? null : k.GetValue("ExecutablePath") as string;
                        if (path == null || !string.Equals(path, Application.ExecutablePath, StringComparison.OrdinalIgnoreCase)) continue;
                        // Only the first time: if the user later hides it in Settings, respect that.
                        if (k.GetValue("IsPromoted") == null)
                        {
                            k.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
                            tray.Visible = false; tray.Visible = true; // re-add so Explorer picks it up
                            Log("tray icon promoted");
                        }
                        return true;
                    }
            }
        }
        catch (Exception e) { Log("promote failed: " + e.Message); return true; }
        return false;
    }

    void OnThemeChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General && e.Category != UserPreferenceCategory.VisualStyle) return;
        Ui((() =>
        {
            Theme.Load(); BackColor = Theme.Bg; Chrome(); tray.Icon = MakeTrayIcon();
            InvalidateAll(this);
        }));
    }
    static void InvalidateAll(Control c) { c.Invalidate(); foreach (Control k in c.Controls) InvalidateAll(k); }

    void Place(Control c, int x, ref int y, int w, int h, int gapAfter) { c.SetBounds(x, y, w, h); target.Controls.Add(c); y += h + gapAfter; }

    // ---------------------------------------------------------- pages (main / settings / EQ editor)
    int W, M, cw;
    Control mainPage, settingsPage, editorPage, current;
    Control target;
    readonly Dictionary<Control, int> pageHeight = new Dictionary<Control, int>();

    // Page background: transparent on glass (shows the acrylic), Bg otherwise.
    class Page : AppleControl
    {
        public Page() { TabStop = false; }
        protected override void OnPaint(PaintEventArgs e) { Begin(e, Theme.Bg); }
    }

    Control NewPage()
    {
        var p = new Page { BackColor = Theme.Bg, Visible = false, Location = Point.Empty };
        Controls.Add(p);
        return p;
    }

    static void ClearPage(Control p)
    {
        var old = new List<Control>(); foreach (Control c in p.Controls) old.Add(c);
        p.Controls.Clear(); foreach (var c in old) c.Dispose();
    }

    void ShowPage(Control p)
    {
        int h = pageHeight[p];
        int bottom = Bottom;
        p.SetBounds(0, 0, W, h);
        p.Visible = true; p.BringToFront();
        foreach (var other in new[] { mainPage, settingsPage, editorPage }) if (other != null && other != p) other.Visible = false;
        current = p;
        ClientSize = new Size(W, h);
        if (Visible) Top = bottom - Height; // grow/shrink upwards, staying anchored above the taskbar
        p.Focus();
    }

    void BuildSettingsPage()
    {
        ClearPage(settingsPage); target = settingsPage;
        int y = Px(8);
        var nav = new NavBar("Settings", "Back");
        nav.Back += () => ShowPage(mainPage);
        Place(nav, M - Px(6), ref y, cw + Px(6), Px(40), Px(12));

        // Grouped list rhythm (iOS Settings): header 6 px above its card, 24 px between groups.
        // Explanations live behind the ⓘ next to each header instead of footers.
        int i0 = Math.Max(0, Array.IndexOf(ApplyKeys, settings.OnConnect));

        // --- When Headset Connects
        InfoSection("When Headset Connects", FooterText(ApplyKeys[i0]), ref y);
        var c1 = new Card();
        applyRow = new ValueRow("Apply", "", true);
        applyRow.Clicked += ShowApplyMenu;
        c1.AddRow(applyRow, Px(44), Padding.Empty, true);
        saveRow = new ButtonRow("Save Current Settings as Profile");
        saveRow.Clicked += SaveProfile;
        c1.AddRow(saveRow, Px(44), Padding.Empty, false);
        GroupEnd(c1, ref y);

        // --- Equalizer Profiles
        InfoSection("Equalizer Profiles", "Profiles appear in the Equalizer menu on the main page. Your Custom 1 and Custom 2 presets aren't changed.", ref y);
        var c2 = new Card();
        foreach (var eq in settings.Eqs)
        {
            var p = eq;
            var row = new ValueRow(p.Name, p.Name == eqCustom ? "In Use" : "", true) { Disclosure = true };
            row.Clicked += r => OpenEditor(p);
            c2.AddRow(row, Px(44), Padding.Empty, true);
        }
        var add = new ButtonRow("New Equalizer Profile");
        add.Clicked += NewEqProfile;
        c2.AddRow(add, Px(44), Padding.Empty, false);
        GroupEnd(c2, ref y);

        // --- Headset (device settings)
        InfoSection("Headset",
            "Bluetooth Quality: Sound Quality uses LDAC when your device supports it; Stable Connection holds up better in crowded places.\n\n" +
            "Automatic Power Off: turns the headset off a while after you take it off.\n\n" +
            "Low Battery Alerts: a Windows notification at 20% and 10%.\n\n" +
            "Turn Off on Shutdown: turns the headset off when Windows shuts down or restarts.\n\n" +
            "Speak-to-Chat: pauses music and lets in outside sound when you start talking.\n\n" +
            "Touch Controls: turns the swipe and tap panel on the right cup on or off.", ref y);
        var c3 = new Card();
        btRow = new ValueRow("Bluetooth Quality", btStable ? "Stable Connection" : "Sound Quality", true);
        btRow.Clicked += ShowBtMenu;
        c3.AddRow(btRow, Px(44), Padding.Empty, true);
        autoOff = new SwitchRow("Automatic Power Off") { On = autoOffOn };
        autoOff.UserToggled += on => { autoOffOn = on; if (connected) link.Send(Cmd.ApoSet(on)); };
        c3.AddRow(autoOff, Px(44), Padding.Empty, true);
        var offPc = new SwitchRow("Turn Off on Shutdown") { On = settings.OffWithPc };
        offPc.UserToggled += on => { settings.OffWithPc = on; settings.Save(); };
        c3.AddRow(offPc, Px(44), Padding.Empty, true);
        var lowBat = new SwitchRow("Low Battery Alerts") { On = settings.BatteryAlerts };
        lowBat.UserToggled += on => { settings.BatteryAlerts = on; settings.Save(); };
        c3.AddRow(lowBat, Px(44), Padding.Empty, true);
        stc = new SwitchRow("Speak-to-Chat") { On = stcOn };
        stc.UserToggled += on => { stcOn = on; if (connected) link.Send(Cmd.StcSet(on)); };
        c3.AddRow(stc, Px(44), Padding.Empty, true);
        touch = new SwitchRow("Touch Controls") { On = touchOn };
        touch.UserToggled += on => { touchOn = on; if (connected) link.Send(Cmd.TouchSet(on)); };
        c3.AddRow(touch, Px(44), Padding.Empty, false);
        GroupEnd(c3, ref y);

        // --- App
        InfoSection("App", "Start with Windows opens XM Panel quietly in the tray when you sign in, so it can apply your settings as soon as the headset connects.", ref y);
        var ca = new Card();
        var autostart = new SwitchRow("Start with Windows") { On = Autostart };
        autostart.UserToggled += on => Autostart = on;
        ca.AddRow(autostart, Px(44), Padding.Empty, false);
        GroupEnd(ca, ref y);

        // --- Keyboard Shortcuts: the list only appears when they're switched on.
        InfoSection("Keyboard Shortcuts", "Change the sound mode from any app, even when this panel is closed.", ref y);
        var c4 = new Card();
        var hk = new SwitchRow("Keyboard Shortcuts") { On = settings.Hotkeys };
        hk.UserToggled += on => { settings.Hotkeys = on; settings.Save(); RegisterHotkeys(); BuildSettingsPage(); ShowPage(settingsPage); };
        c4.AddRow(hk, Px(44), Padding.Empty, settings.Hotkeys);
        if (settings.Hotkeys)
        {
            string[] what = { "Noise Canceling", "Ambient Sound", "Off", "Next Mode" };
            for (int i = 0; i < 4; i++)
                c4.AddRow(new ValueRow(what[i], "Ctrl+Alt+" + (i == 3 ? "0" : (i + 1).ToString()), false), Px(40), Padding.Empty, i < 3);
        }
        GroupEnd(c4, ref y);

        // About line, centred at the bottom of the page.
        var about = new TextBlock("XM Panel " + Version + "  ·  Not affiliated with Sony", Theme.Footnote, true) { Centered = true };
        int ah = about.Measure(cw);
        y -= Px(8);
        Place(about, M, ref y, cw, ah, Px(24));
        pageHeight[settingsPage] = y - Px(24) + Px(16);
        target = mainPage;
        UpdateProfileUi(); ApplyEnabled();
    }

    void NewEqProfile()
    {
        int n = 1; while (settings.FindEq("My EQ " + n) != null) n++;
        var p = new EqProfile { Name = "My EQ " + n };
        if (lastBands != null) Array.Copy(lastBands, p.Bands, 6); // start from what the headset plays now
        settings.Eqs.Add(p); settings.Save();
        OpenEditor(p);
    }

    static readonly string[] BandNames = { "Clear Bass", "400 Hz", "1 kHz", "2.5 kHz", "6.3 kHz", "16 kHz" };

    void OpenEditor(EqProfile p)
    {
        ClearPage(editorPage); target = editorPage;
        int y = Px(8);
        var nav = new NavBar(p.Name, "Settings") { RightText = "Done" };
        Action close = () => { settings.Save(); BuildSettingsPage(); ShowPage(settingsPage); };
        nav.Back += close;
        nav.RightAction += close;
        Place(nav, M - Px(6), ref y, cw + Px(6), Px(40), Px(12));

        var c1 = new Card();
        var name = new NameRow("Name", p.Name);
        name.NameChanged += (s, e) =>
        {
            string nn = EqProfile.Clean(name.Text);
            if (nn.Length == 0 || (nn != p.Name && settings.FindEq(nn) != null)) return; // keep names unique and non-empty
            RenameEq(p, nn); nav.Title = nn; nav.Invalidate();
        };
        c1.AddRow(name, Px(44), Padding.Empty, false);
        GroupEnd(c1, ref y);

        InfoSection("Bands", "Changes play on your headset as you make them and are saved automatically.", ref y);
        var c2 = new Card();
        var rows = new BandRow[6];
        for (int i = 0; i < 6; i++)
        {
            int idx = i;
            rows[i] = new BandRow(BandNames[i], p.Bands[i]);
            rows[i].Changed += v => { p.Bands[idx] = v; eqSendProfile = p; eqSend.Stop(); eqSend.Start(); };
            c2.AddRow(rows[i], Px(40), Padding.Empty, i == 0);
        }
        GroupEnd(c2, ref y);

        var c3 = new Card();
        var flat = new ButtonRow("Reset to Flat");
        flat.Clicked += () => { for (int i = 0; i < 6; i++) { p.Bands[i] = 0; rows[i].Slider.Value = 0; rows[i].Invalidate(); } ApplyEq(p); settings.Save(); };
        c3.AddRow(flat, Px(44), Padding.Empty, true);
        var del = new ButtonRow("Delete Profile") { Destructive = true };
        del.Clicked += () =>
        {
            settings.Eqs.Remove(p);
            if (eqCustom == p.Name) { eqCustom = null; eqRow.Value = EqNames[eqIndex]; }
            if (settings.Saved != null && settings.Saved.EqCustom == p.Name) settings.Saved.EqCustom = null;
            settings.Save(); BuildSettingsPage(); ShowPage(settingsPage);
        };
        c3.AddRow(del, Px(44), Padding.Empty, false);
        Place(c3, M, ref y, cw, c3.LayoutRows(cw), Px(16));

        pageHeight[editorPage] = y - Px(4);
        target = mainPage;
        ShowPage(editorPage);
        ActiveControl = null; // don't start in edit mode
        ApplyEq(p); // hear the profile while editing it
    }

    void RenameEq(EqProfile p, string nn)
    {
        if (eqCustom == p.Name) { eqCustom = nn; eqRow.Value = nn; }
        if (settings.Saved != null && settings.Saved.EqCustom == p.Name) settings.Saved.EqCustom = nn;
        if (settings.Last != null && settings.Last.EqCustom == p.Name) settings.Last.EqCustom = nn;
        p.Name = nn; settings.Save();
    }

    // Custom bands: select the headset's Manual preset slot (0xA0), then write the bands to the
    // current slot (0xFF): 58 01 FF 06 + Clear Bass and 5 bands, each offset by 10.
    void ApplyEq(EqProfile p)
    {
        eqCustom = p.Name; eqRow.Value = p.Name;
        if (!connected || link == null) return;
        eqAppliedAt = Environment.TickCount;
        foreach (var m in Cmd.EqCustom(p.Bands, eqIndex != 9)) link.Send(m);
        eqIndex = 9;
        Changed();
    }

    // ---------------------------------------------------------- device settings + shortcuts
    SwitchRow stc, pauseOff, touch, autoOff;
    bool btStable, autoOffOn = true, stcOn, touchOn = true;

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, int mods, int vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);
    const int MOD_ALT = 1, MOD_CONTROL = 2, MOD_NOREPEAT = 0x4000;
    static readonly Keys[] HotkeyKeys = { Keys.D1, Keys.D2, Keys.D3, Keys.D0 };

    void RegisterHotkeys()
    {
        for (int i = 0; i < 4; i++) UnregisterHotKey(Handle, 100 + i);
        if (!settings.Hotkeys) return;
        for (int i = 0; i < 4; i++)
            if (!RegisterHotKey(Handle, 100 + i, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, (int)HotkeyKeys[i]))
                Log("hotkey Ctrl+Alt+" + HotkeyKeys[i] + " is taken by another app");
    }

    protected override void WndProc(ref Message m)
    {
        // WM_ENDSESSION with wParam=TRUE: Windows is really ending the session. lParam has
        // ENDSESSION_LOGOFF set for a sign-out; without it this is a shutdown or restart.
        if (m.Msg == 0x0016 && m.WParam != IntPtr.Zero)
        {
            bool logoff = (m.LParam.ToInt64() & 0x80000000L) != 0;
            Log("session ending (" + (logoff ? "sign-out" : "shutdown/restart") + "), turn-off option " + (settings.OffWithPc ? "on" : "off"));
            if (!logoff && settings.OffWithPc && connected && link != null)
                Log("power off on shutdown, acknowledged: " + link.SendAndWait(Cmd.PowerOff(), 2500));
        }
        if (m.Msg == 0x0312 /* WM_HOTKEY */ && connected)
        {
            int id = m.WParam.ToInt32() - 100;
            Mode next = id == 3 ? (Mode)(((int)mode + 1) % 3) : (Mode)id;
            if (id >= 0 && id <= 3) { SetMode(next, false); SendNc(); Log("hotkey -> " + next); }
        }
        base.WndProc(ref m);
    }

    string eqCustom;          // custom EQ profile currently on the headset, if any
    int eqAppliedAt = -100000; // the slot-switch notification carries the old bands; ignore it briefly
    int[] lastBands;          // band values last reported by the headset
    EqProfile eqSendProfile;
    System.Windows.Forms.Timer eqSend;
    void Section(string t, int x, ref int y, int w)
    {
        var tb = new TextBlock(t, Theme.Section, true);
        Place(tb, x + Px(12), ref y, w - Px(12), tb.Measure(w), Px(6));
    }
    TextBlock Footer(string t, int x, ref int y, int w)
    {
        var tb = new TextBlock(t, Theme.Footnote, true);
        Place(tb, x + Px(12), ref y, w - Px(24), tb.Measure(w - Px(24)), Px(24));
        return tb;
    }

    // Low battery: one Windows notification at 20 % and one at 10 % per discharge.
    bool warned20, warned10;
    void CheckBattery(int level, bool charging)
    {
        if (charging || level > 25) { warned20 = warned10 = false; return; }
        if (!settings.BatteryAlerts) return;
        string title = null;
        if (level <= 10 && !warned10) { warned10 = warned20 = true; title = "Headset battery very low"; }
        else if (level <= 20 && !warned20) { warned20 = true; title = "Headset battery low"; }
        if (title == null) return;
        Log("battery alert " + level + "%");
        tray.ShowBalloonTip(6000, title, header.DeviceName + " has " + level + "% left. Charge it soon.", ToolTipIcon.None);
    }

    [DllImport("psapi.dll")] static extern bool EmptyWorkingSet(IntPtr process);
    static void TrimMemory()
    {
        try { GC.Collect(); EmptyWorkingSet(System.Diagnostics.Process.GetCurrentProcess().Handle); } catch { }
    }

    public const string Version = "1.0.0";

    // Start with Windows: the per-user Run key, read live so it reflects changes made elsewhere
    // (Task Manager's Startup apps toggles a separate "approved" flag, which Windows also honours).
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "XM Panel";

    // Pre-release builds registered "XM4 Control" / "XMate Headphones"; switch that entry over once.
    static void MigrateAutostart()
    {
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
            {
                if (k == null) return;
                bool had = false;
                foreach (var old in new[] { "XM4 Control", "XMate Headphones" })
                    if (k.GetValue(old) != null) { k.DeleteValue(old, false); had = true; }
                if (had) k.SetValue(RunValue, "\"" + Application.ExecutablePath + "\" --background");
            }
        }
        catch { }
    }

    static bool Autostart
    {
        get
        {
            try { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue(RunValue) != null; }
            catch { return false; }
        }
        set
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (value) k.SetValue(RunValue, "\"" + Application.ExecutablePath + "\" --background");
                    else k.DeleteValue(RunValue, false);
                }
            }
            catch { }
        }
    }

    bool powerConfirm;
    System.Windows.Forms.Timer confirmTimer;

    void RebuildSettings() { if (current == settingsPage || current == null) { BuildSettingsPage(); ShowPage(settingsPage); } else BuildSettingsPage(); }

    // Gadgetbridge POWER_OFF: 22 00 01. The link then drops and the app waits to reconnect.
    void PowerOff()
    {
        if (!connected) return;
        Log("power off");
        link.Send(Cmd.PowerOff());
    }

    void GroupEnd(Card c, ref int y) { Place(c, M, ref y, cw, c.LayoutRows(cw), Px(24)); }

    void InfoSection(string title, string info, ref int y)
    {
        var h = new InfoHeader(title, info);
        int th = Math.Max(TextRenderer.MeasureText(title, Theme.Section).Height, Px(16));
        Place(h, M + Px(12), ref y, cw - Px(12), th, Px(6));
    }

    ValueRow btRow;
    void ShowBtMenu(ValueRow row)
    {
        var m = AppleMenuRenderer.Create();
        string[] names = { "Sound Quality", "Stable Connection" };
        for (int i = 0; i < 2; i++)
        {
            int idx = i;
            m.Items.Add(AppleMenuRenderer.Item(names[i], (btStable ? 1 : 0) == i, (s, e) =>
            {
                btStable = idx == 1; btRow.Value = names[idx];
                if (connected) link.Send(Cmd.QualitySet(idx == 1));
            }));
        }
        m.Closed += (s, e) => Ui((() => m.Dispose()));
        var sz = m.GetPreferredSize(Size.Empty);
        m.Show(row, new Point(row.Width - sz.Width, row.Height));
    }

    void Log(string s)
    {
        try { lock (logPath) File.AppendAllText(logPath, DateTime.Now.ToString("HH:mm:ss.fff ") + s + Environment.NewLine); } catch { }
    }

    void SetStatus(string s, bool showConnect)
    {
        header.Status = s; header.ShowReconnect = showConnect; header.Invalidate();
        string t = "XM Panel – " + s; tray.Text = t.Length > 63 ? "XM Panel" : t;
        Log("STATUS " + s);
        UpdateTrayText();
    }

    // Tray tooltip: "WH-1000XM4 · Connected · 40%" (max 63 characters).
    void UpdateTrayText()
    {
        string t = header.DeviceName + " · " + header.Status + (header.Battery >= 0 ? " · " + header.Battery + "%" + (header.Charging ? " charging" : "") : "");
        tray.Text = t.Length > 63 ? t.Substring(0, 63) : t;
    }

    public void StartConnect() { StartConnect(false); }

    // quiet = background retry while the headset is off: keep showing "Not Connected".
    public void StartConnect(bool quiet)
    {
        if (connecting || quitting) return;
        retry.Stop();
        if (link != null) link.Close();
        connected = false; connecting = true; ApplyEnabled();
        if (!quiet) SetStatus("Connecting…", false);
        var t = new Thread(() =>
        {
            string err = null, name = null;
            var l = new SonyLink();
            try
            {
                ulong addr = headsetAddr; name = headsetName;
                if (addr == 0) { addr = SonyLink.FindHeadset(out name); headsetAddr = addr; headsetName = name; } // WMI only once
                if (addr == 0) throw new Exception("No paired headset found");
                l.Message += p => Ui((() => OnMessage(p)));
                l.Closed += why => Ui((() =>
                {
                    if (link != l) return;
                    connected = false; ApplyEnabled(); header.Battery = -1;
                    Log("closed: " + why);
                    // Right after power-on the headset accepts the link and drops it again while it boots.
                    // Treat a drop within 3 s as "still connecting" and retry quickly.
                    bool quickDrop = Environment.TickCount - connectedAt < 3000;
                    if (quickDrop) { SetStatus("Connecting…", false); retry.Interval = 2000; }
                    else { SetStatus("Not Connected", true); retry.Interval = 3000; }
                    if (!testMode) retry.Start(); // reconnect automatically when the headset comes back
                }));
                // XM5 and newer speak protocol v2 on a different service; try the other one if the guess fails.
                bool v2 = name != null && Regex.IsMatch(name, @"XM([5-9]|\d\d)", RegexOptions.IgnoreCase);
                try { l.Connect(addr, v2 ? SonyLink.SonyServiceV2 : new Guid("96CC203E-5068-46ad-B32D-E316F5E069BA")); }
                catch
                {
                    v2 = !v2;
                    l.Connect(addr, v2 ? SonyLink.SonyServiceV2 : new Guid("96CC203E-5068-46ad-B32D-E316F5E069BA"));
                }
                Cmd.V2 = v2;
                SonyLink.Log("protocol " + (v2 ? "v2 (XM5/XM6, untested)" : "v1"));
            }
            catch (Exception e) { err = e.Message; }
            Ui((() =>
            {
                connecting = false;
                if (err != null)
                {
                    SetStatus("Not Connected", true); Log("connect failed: " + err);
                    retry.Interval = 3000;
                    if (testMode) Quit(); else retry.Start();
                    return;
                }
                link = l; connected = true; connectedAt = Environment.TickCount; ApplyEnabled();
                if (name != null) header.DeviceName = name;
                SetStatus("Connected", false);
                link.Send(Cmd.Init());
                link.Send(Cmd.BatteryGet());
                link.Send(Cmd.StcGet());
                if (Cmd.HasPause) link.Send(Cmd.PauseGet());
                link.Send(Cmd.TouchGet());
                link.Send(Cmd.QualityGet());
                link.Send(Cmd.ApoGet());
                // The headset always powers on in NC or Ambient; switch it to the chosen profile.
                Profile target = settings.OnConnect == "last" ? settings.Last : settings.OnConnect == "profile" ? settings.Saved : null;
                if (target != null) ApplyProfile(target);
                else
                {
                    link.Send(Cmd.NcGet());
                    link.Send(Cmd.EqGet());
                    link.Send(Cmd.DseeGet());
                }
                if (testMode) { var tm = new System.Windows.Forms.Timer { Interval = 5000 }; tm.Tick += (s, e) => Quit(); tm.Start(); }
            }));
        }) { IsBackground = true };
        t.Start();
    }

    Profile CaptureProfile()
    {
        return new Profile { Mode = (int)mode, Level = level.Value, Voice = voice.On, Eq = eqIndex, Dsee = dsee.On, EqCustom = eqCustom };
    }

    static bool SameBands(int[] a, int[] b)
    {
        for (int i = 0; i < 6; i++) if (a[i] != b[i]) return false;
        return true;
    }

    void ApplyProfile(Profile p)
    {
        Log("applying profile " + p);
        SetMode((Mode)Math.Max(0, Math.Min(2, p.Mode)), false);
        level.Value = p.Level; levelRow.Value = level.Value.ToString();
        voice.On = p.Voice;
        eqIndex = Math.Max(0, Math.Min(EqNames.Length - 1, p.Eq)); eqRow.Value = EqNames[eqIndex];
        dsee.On = p.Dsee;
        var custom = settings.FindEq(p.EqCustom);
        eqCustom = custom == null ? null : custom.Name;
        if (custom != null) eqRow.Value = custom.Name;
        if (!connected) return;
        SendNc();
        if (custom != null) ApplyEq(custom);
        else link.Send(Cmd.EqPreset(EqCodes[eqIndex]));
        link.Send(Cmd.DseeSet(p.Dsee));
    }

    void Changed() { saveLast.Stop(); saveLast.Start(); } // remember "last used" shortly after any change

    static readonly string[] ApplyKeys = { "last", "profile", "none" };
    static readonly string[] ApplyNames = { "Last Used Settings", "Saved Profile", "Nothing" };

    string FooterText(string key)
    {
        if (key == "last") return "The headset always starts in Noise Canceling or Ambient Sound. XM Panel switches it back to your last used settings each time it connects.";
        if (key == "none") return "The headset keeps whatever mode it starts in.";
        return settings.Saved == null ? "No profile saved yet. Set things up the way you like, then save them as your profile."
            : "Each time the headset connects, XM Panel applies your saved profile: " + Describe(settings.Saved) + ".";
    }

    string Describe(Profile p)
    {
        string s = p.Mode == 1 ? "Ambient Sound " + p.Level + (p.Voice ? " with Focus on Voice" : "") : ModeNames[Math.Max(0, Math.Min(2, p.Mode))];
        s += ", " + (settings.FindEq(p.EqCustom) != null ? p.EqCustom : EqNames[Math.Max(0, Math.Min(EqNames.Length - 1, p.Eq))]) + " EQ";
        if (p.Dsee) s += ", DSEE Extreme";
        return s;
    }

    void UpdateProfileUi()
    {
        int i = Math.Max(0, Array.IndexOf(ApplyKeys, settings.OnConnect));
        applyRow.Value = ApplyNames[i] == "Last Used Settings" ? "Last Used" : ApplyNames[i];
    }

    void ShowApplyMenu(ValueRow row)
    {
        var m = AppleMenuRenderer.Create();
        for (int i = 0; i < ApplyKeys.Length; i++)
        {
            string key = ApplyKeys[i];
            m.Items.Add(AppleMenuRenderer.Item(ApplyNames[i], settings.OnConnect == key, (s, e) =>
            {
                settings.OnConnect = key; settings.Save();
                BuildSettingsPage(); ShowPage(settingsPage); // footer text (and its height) changes
            }));
        }
        m.Closed += (s, e) => Ui((() => m.Dispose()));
        var sz = m.GetPreferredSize(Size.Empty);
        m.Show(row, new Point(row.Width - sz.Width, row.Height));
    }

    void SaveProfile()
    {
        settings.Saved = CaptureProfile();
        settings.Save();
        if (current == settingsPage) { BuildSettingsPage(); ShowPage(settingsPage); } else UpdateProfileUi();
        saveRow.Label = "✓ Profile Saved";
        var t = new System.Windows.Forms.Timer { Interval = 1600 };
        t.Tick += (s, e) => { t.Stop(); t.Dispose(); saveRow.Label = "Save Current Settings as Profile"; };
        t.Start();
    }

    void ApplyEnabled()
    {
        bool amb = connected && mode == Mode.Ambient;
        seg.Enabled = connected; eqRow.Enabled = connected; dsee.Enabled = connected; saveRow.Enabled = connected;
        foreach (Control c in new Control[] { stc, pauseOff, touch, autoOff, btRow }) if (c != null) c.Enabled = connected;
        header.PowerEnabled = connected; header.Invalidate();
        if (pauseOff != null && !Cmd.HasPause) pauseOff.Enabled = false; // not offered on XM5/XM6 (protocol unclear)
        levelRow.Enabled = amb; level.Enabled = amb; voice.Enabled = amb;
    }

    void SetMode(Mode m, bool fromDevice)
    {
        mode = m; seg.Selected = (int)m; ApplyEnabled();
    }

    void SendNc()
    {
        if (!connected) return;
        link.Send(Cmd.NcSet((int)mode, voice.On, level.Value));
        Changed();
    }

    void ShowEqMenu(ValueRow row)
    {
        var m = AppleMenuRenderer.Create();
        for (int i = 0; i < EqNames.Length; i++)
        {
            int idx = i;
            if (i == 9) m.Items.Add(new ToolStripSeparator());
            m.Items.Add(AppleMenuRenderer.Item(EqNames[i], eqCustom == null && i == eqIndex, (s, e) =>
            {
                eqIndex = idx; eqCustom = null; eqRow.Value = EqNames[idx];
                if (connected) { link.Send(Cmd.EqPreset(EqCodes[idx])); Changed(); }
            }));
        }
        if (settings.Eqs.Count > 0)
        {
            m.Items.Add(new ToolStripSeparator());
            foreach (var eq in settings.Eqs)
            {
                var p = eq;
                m.Items.Add(AppleMenuRenderer.Item(p.Name, eqCustom == p.Name, (s, e) => ApplyEq(p)));
            }
        }
        m.Closed += (s, e) => Ui((() => m.Dispose()));
        var sz = m.GetPreferredSize(Size.Empty);
        m.Show(row, new Point(row.Width - sz.Width, row.Height));
    }

    void BuildTrayMenu()
    {
        trayMenu.Items.Clear();
        for (int i = 0; i < 3; i++)
        {
            int idx = i;
            var it = AppleMenuRenderer.Item(ModeNames[i], (int)mode == i, (s, e) => { SetMode((Mode)idx, false); SendNc(); });
            it.Enabled = connected;
            trayMenu.Items.Add(it);
        }
        trayMenu.Items.Add(new ToolStripSeparator());
        if (settings.Saved != null)
        {
            var ap = AppleMenuRenderer.Item("Apply Saved Profile", false, (s, e) => ApplyProfile(settings.Saved));
            ap.Enabled = connected;
            trayMenu.Items.Add(ap);
        }
        var po = AppleMenuRenderer.Item("Turn Off Headphones", false, (s, e) => PowerOff());
        po.Enabled = connected;
        trayMenu.Items.Add(po);
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add(AppleMenuRenderer.Item("Show Controls", false, (s, e) => ShowFlyout()));
        trayMenu.Items.Add(AppleMenuRenderer.Item("Quit XM Panel", false, (s, e) => Quit()));
    }

    void ApplyBattery(int level, bool charging)
    {
        header.Battery = level; header.Charging = charging; header.Invalidate();
        UpdateTrayText();
        CheckBattery(level, charging);
    }

    // Protocol v2 (XM5/XM6) replies, per Gadgetbridge SonyProtocolImplV2. Returns true when handled;
    // the equalizer reply has the same layout as v1 and falls through to the shared parser.
    bool OnMessageV2(byte[] p)
    {
        int n = p.Length;
        switch (p[0])
        {
            case 0x67: case 0x69: // 67 17 01 <on> <0=NC 1=ambient> ... <voice> <level>
                if (p[1] == 0x17 && n >= 7)
                {
                    SetMode(p[3] == 0 ? Mode.Off : (p[4] == 1 ? Mode.Ambient : Mode.NC), true);
                    voice.On = p[n - 2] != 0;
                    level.Value = Math.Min(20, (int)p[n - 1]); levelRow.Value = level.Value.ToString();
                    Changed();
                }
                return true;
            case 0x23: case 0x25: // battery
                if (n >= 4 && p[1] == 0x00) ApplyBattery(p[2], p[3] == 1);
                return true;
            case 0xE7: case 0xE9: // 01 = DSEE (1 = on), 02 = LDAC (0 = sound quality)
                if (n >= 3 && p[1] == 0x01) { dsee.On = p[2] != 0; Changed(); }
                if (n >= 3 && p[1] == 0x02) { btStable = p[2] != 0; if (btRow != null) btRow.Value = btStable ? "Stable Connection" : "Sound Quality"; }
                return true;
            case 0xF7: case 0xF9: // 0C = speak-to-chat (0 = on)
                if (n >= 3 && p[1] == 0x0C) { stcOn = p[2] == 0; if (stc != null) stc.On = stcOn; }
                return true;
            case 0xD7: case 0xD9: // D2 = touch (p[3] 0 = on)
                if (n >= 4 && p[1] == 0xD2) { touchOn = p[3] == 0; if (touch != null) touch.On = touchOn; }
                return true;
            case 0x27: case 0x29: // 05 = automatic power off (10 00 = when taken off, 11 00 = never)
                if (n >= 4 && p[1] == 0x05) { autoOffOn = p[2] == 0x10; if (autoOff != null) autoOff.On = autoOffOn; }
                return true;
        }
        return false;
    }

    void OnMessage(byte[] p)
    {
        if (p.Length < 2) return;
        if (Cmd.V2 && OnMessageV2(p)) return;
        switch (p[0])
        {
            case 0x67: case 0x69: // NC / ambient state
                if (p[1] == 0x02 && p.Length >= 8)
                {
                    SetMode(p[2] == 0 ? Mode.Off : (p[4] != 0 ? Mode.NC : Mode.Ambient), true);
                    voice.On = p[6] != 0;
                    level.Value = Math.Min(20, (int)p[7]); levelRow.Value = level.Value.ToString();
                }
                break;
            case 0x11: case 0x13: // battery
                if (p.Length >= 4 && p[1] == 0x00) ApplyBattery(p[2], p[3] == 1);
                break;
            case 0x57: case 0x59: // equalizer
                if (p.Length >= 3)
                {
                    if (p.Length >= 10 && p[3] == 6)
                    {
                        lastBands = new int[6];
                        for (int k = 0; k < 6; k++) lastBands[k] = p[4 + k] - 10;
                    }
                    if (p[2] == 0xA0 && lastBands != null)
                    {
                        // Manual slot: show which custom profile it is, if the bands match one.
                        var cur = settings.FindEq(eqCustom);
                        bool justApplied = Environment.TickCount - eqAppliedAt < 2000;
                        if (!justApplied && (cur == null || !SameBands(cur.Bands, lastBands)))
                        {
                            cur = settings.Eqs.Find(x => SameBands(x.Bands, lastBands));
                            eqCustom = cur == null ? null : cur.Name;
                        }
                    }
                    else if (p[2] != 0xA0) eqCustom = null;
                    int i = Array.IndexOf(EqCodes, p[2]);
                    if (i >= 0) eqIndex = i;
                    eqRow.Value = eqCustom ?? EqNames[eqIndex];
                }
                break;
            case 0xE7: case 0xE9: // DSEE
                if (p.Length >= 4 && p[1] == 0x02) dsee.On = p[3] != 0;
                if (p.Length >= 4 && p[1] == 0x01) { btStable = p[3] == 1; if (btRow != null) btRow.Value = btStable ? "Stable Connection" : "Sound Quality"; }
                break;
            case 0xF7: case 0xF9: // pause when taken off / automatic power off / speak-to-chat
                if (p.Length >= 4 && p[1] == 0x03) pauseOff.On = p[3] != 0;
                if (p.Length >= 4 && p[1] == 0x04) { autoOffOn = p[3] == 0x10; if (autoOff != null) autoOff.On = autoOffOn; }
                if (p.Length >= 4 && p[1] == 0x05 && p[2] == 0x00) { stcOn = p[3] != 0; if (stc != null) stc.On = stcOn; }
                break;
            case 0xD7: case 0xD9: // touch controls
                if (p.Length >= 4 && p[1] == 0xD1) { touchOn = p[3] != 0; if (touch != null) touch.On = touchOn; }
                break;
        }
        if (p[0] == 0x67 || p[0] == 0x69 || p[0] == 0x57 || p[0] == 0x59 || p[0] == 0xE7 || p[0] == 0xE9)
            Changed(); // includes changes made with the headset's own buttons
    }

    // Tray icon in the same style as the Windows system icons (volume, network...):
    // the headphone glyph from Segoe Fluent Icons, monochrome, matching the taskbar's light/dark mode.
    static Icon MakeTrayIcon()
    {
        int sz = SystemInformation.SmallIconSize.Width;
        bool light = false;
        try { object v = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0); light = v is int && (int)v == 1; } catch { }
        Color c = light ? Color.FromArgb(28, 28, 28) : Color.White;
        if (HeadphoneArt.HasArtwork) return Icon.FromHandle(HeadphoneArt.Artwork(sz, c).GetHicon());
        var bmp = new Bitmap(sz, sz, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            HeadphoneArt.Draw(g, new RectangleF(0, 0, sz, sz), c);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    // App icon: the headphone artwork in white on a blue rounded tile (also used for app.ico).
    static Bitmap RenderAppIcon(int size)
    {
        var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            float inset = size <= 20 ? 0 : size * 0.03f, rad = size * 0.22f;
            using (var p = Theme.Round(new RectangleF(inset, inset, size - inset * 2, size - inset * 2), rad))
            using (var b = new LinearGradientBrush(new PointF(0, 0), new PointF(0, size), Color.FromArgb(70, 160, 255), Color.FromArgb(0, 98, 225)))
                g.FillPath(b, p);
            int art = (int)Math.Round(size * (size <= 24 ? 0.86 : 0.74)), off = (size - art) / 2;
            if (HeadphoneArt.HasArtwork) g.DrawImage(HeadphoneArt.Artwork(art, Color.White), off, off, art, art);
            else HeadphoneArt.Draw(g, new RectangleF(off, off, art, art), Color.White);
        }
        return bmp;
    }

    static Icon MakeIcon() { return Icon.FromHandle(RenderAppIcon(32).GetHicon()); }

    // Writes a multi-size .ico (PNG-compressed entries, as Windows Vista+ expects for large sizes).
    static void WriteIco(string path)
    {
        int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
        var pngs = new List<byte[]>();
        foreach (int s in sizes)
            using (var bmp = RenderAppIcon(s))
            using (var ms = new MemoryStream()) { bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png); pngs.Add(ms.ToArray()); }
        using (var w = new BinaryWriter(File.Create(path)))
        {
            w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length); // ICONDIR
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)                               // ICONDIRENTRY
            {
                w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                w.Write((byte)0); w.Write((byte)0); w.Write((short)1); w.Write((short)32);
                w.Write(pngs[i].Length); w.Write(offset); offset += pngs[i].Length;
            }
            foreach (var png in pngs) w.Write(png);
        }
    }

    // Diagnostic: send read-only GET queries and log what the headset answers (to probe.log).
    // Without extra args it sweeps the x6 "get" codes; with hex args ("f6:04 d6:d2") it sends just those.
    static void Probe(string[] args)
    {
        string log = Path.Combine(AppData.Dir, "probe.log");
        File.WriteAllText(log, "");
        Action<string> w = s => { lock (log) File.AppendAllText(log, DateTime.Now.ToString("HH:mm:ss.fff ") + s + Environment.NewLine); };
        SonyLink.Log = w;
        string name; ulong addr = SonyLink.FindHeadset(out name);
        var l = new SonyLink();
        l.Message += p => { };
        l.Connect(addr);
        l.Send(0x00, 0x00);
        var list = new List<byte[]>();
        foreach (var a in args)
        {
            // "f6.04" or "f8.03.00.01": raw payload bytes in hex
            if (Regex.IsMatch(a, "^[0-9a-fA-F]{2}(\\.[0-9a-fA-F]{2})+$"))
                list.Add(Array.ConvertAll(a.Split('.'), h => Convert.ToByte(h, 16)));
        }
        if (list.Count == 0)
        {
            foreach (byte code in new byte[] { 0x26, 0x36, 0x46, 0x76, 0x86, 0x96, 0xA6, 0xB6, 0xC6, 0xD6, 0xE6, 0xF6 })
                for (int sub = 0; sub <= 0x0D; sub++) list.Add(new[] { code, (byte)sub });
            foreach (byte sub in new byte[] { 0xD0, 0xD1, 0xD2, 0xD3, 0xD4, 0xD5 }) list.Add(new[] { (byte)0xD6, sub });
        }
        foreach (var q in list) l.Send(q);
        Thread.Sleep(Math.Max(4000, list.Count * 120));
        l.Close();
    }

    [STAThread]
    static void Main(string[] args)
    {
        try { SetProcessDPIAware(); } catch { }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using (var g = Graphics.FromHwnd(IntPtr.Zero)) Theme.S = g.DpiX / 96f;
        Theme.Load();
        if (Array.IndexOf(args, "--probe") >= 0) { Probe(args); return; }
        int mi = Array.IndexOf(args, "--make-icon"); // build helper: writes app.ico for the exe
        if (mi >= 0 && mi + 1 < args.Length) { WriteIco(args[mi + 1]); return; }
        int pu = Array.IndexOf(args, "--preview-ui"); // render the whole panel to a PNG for design checks
        if (pu >= 0 && pu + 1 < args.Length)
        {
            Settings.ReadOnly = true;
            Theme.ReduceMotion = true; // switches snap to their final state for the snapshot
            var f = new MainForm(true);
            f.PreviewState();
            if (pu + 2 < args.Length) f.PreviewPage(args[pu + 2]);
            f.Location = new Point(-5000, -5000); f.Show(); Application.DoEvents();
            using (var bmp = new Bitmap(f.Width, f.Height))
            {
                f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                bmp.Save(args[pu + 1]);
            }
            f.Quit();
            return;
        }
        int pv = Array.IndexOf(args, "--preview"); // render the icon artwork to a PNG for design checks
        if (pv >= 0 && pv + 1 < args.Length)
        {
            using (var bmp = new Bitmap(420, 200))
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Theme.Card);
                g.DrawImage(HeadphoneArt.Hero(180), 10, 10);
                g.DrawImage(HeadphoneArt.Hero(Theme.Px(150)), 210, 10);
                HeadphoneArt.Draw(g, new RectangleF(270, 10, 16, 16), Theme.Label);
                using (var ic = MakeIcon()) g.DrawIcon(ic, new Rectangle(300, 10, 32, 32));
                bmp.Save(args[pv + 1]);
            }
            return;
        }
        bool test = Array.IndexOf(args, "--test") >= 0;
        bool background = Array.IndexOf(args, "--background") >= 0; // for starting with Windows

        // Single instance: launching again just opens the existing flyout.
        bool first;
        var mutex = new Mutex(true, "XMPanel.SingleInstance", out first);
        var showEvt = new EventWaitHandle(false, EventResetMode.AutoReset, "XMPanel.Show");
        if (!first && !test) { showEvt.Set(); return; }

        // Unexpected errors go to the log instead of a .NET error dialog.
        string crashLog = Path.Combine(AppData.Dir, "xmpanel.log");
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (s, e) => { try { File.AppendAllText(crashLog, DateTime.Now.ToString("HH:mm:ss.fff ") + "ERROR " + e.Exception + Environment.NewLine); } catch { } };
        AppDomain.CurrentDomain.UnhandledException += (s, e) => { try { File.AppendAllText(crashLog, DateTime.Now.ToString("HH:mm:ss.fff ") + "FATAL " + e.ExceptionObject + Environment.NewLine); } catch { } };

        MigrateAutostart();
        var form = new MainForm(test);
        IntPtr h = form.Handle; // create the window without showing it
        // If the window is closed for any reason (e.g. Windows ending the session), end the app
        // instead of leaving a process running without a window.
        form.FormClosed += (s, e) => form.Quit();
        new Thread(() => { while (true) { showEvt.WaitOne(); form.Ui(form.ShowFlyout); } }) { IsBackground = true }.Start();
        form.StartConnect();
        if (!background) form.ShowFlyout();
        Application.Run(new ApplicationContext());
        GC.KeepAlive(mutex);
    }
}
