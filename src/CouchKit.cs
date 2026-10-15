// CouchKit: background helper that makes a PC behave like a TV console around Playnite
// fullscreen, while Sunshine/Apollo + Moonlight remote sessions use their own virtual display.
//
// Controller:
//   Hold Back + Start   close the running game: the one Playnite reports it started (via
//                       current-game.txt; for emulated games, the running emulator),
//                       otherwise whatever is in the foreground.
//                       1.5s -> polite close, short rumble; 4s -> force-kill, long rumble.
//                       Playnite, Explorer, Steam and Apollo are never touched.
//   Tap Xbox button     game -> Playnite library; anything else -> the running game, or Playnite.
//   Double-tap Xbox button   The Guide: games, Discord party, audio, lights, system (GuidePanel.cs).
//
// TV vs virtual display (a stream makes the host's virtual display primary):
//   - Playnite is kept on the TV, or minimised while no physical screen is active.
//   - The running Playnite game is kept on the TV the same way.
//   - Playnite and the game are muted while a stream is connected; afterwards the game (or
//     Playnite) is brought to the front.
//   - Sound output is switched back to the TV when it is the only screen.
//   - The TV is put back to its configured mode whenever the set of screens changes.
//
// Lighting (Lighting.cs): a flowing palette on every OpenRGB device, themed to the game's art in game.
// Smart home (SmartHome.cs): the game's colours sent to a webhook (e.g. Home Assistant) and animated
// on Nanoleaf panels.
//
// Every feature can be switched off or tuned in %APPDATA%\CouchKit\couchkit.json (Settings.cs).
// The Playnite add-on (playnite\) starts CouchKit with Playnite and reports the running game.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

static class CouchKit
{
    [StructLayout(LayoutKind.Sequential)]
    struct XGamepad { public ushort Buttons; public byte LT, RT; public short LX, LY, RX, RY; }
    [StructLayout(LayoutKind.Sequential)]
    struct XState { public uint Packet; public XGamepad Pad; }
    [StructLayout(LayoutKind.Sequential)]
    struct XVibration { public ushort Left, Right; }

    // Ordinal 100 is XInputGetStateEx: same as XInputGetState but also reports the Xbox (Guide) button.
    [DllImport("xinput1_4.dll", EntryPoint = "#100")] static extern int XInputGetState(int index, out XState state);
    [DllImport("xinput1_4.dll")] static extern int XInputSetState(int index, ref XVibration vib);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MONITORINFOEX
    {
        public int Size; public RECT Monitor; public RECT Work; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DISPLAY_DEVICE
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }
    delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref RECT rect, IntPtr data);

    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFOEX info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplayDevices(string device, uint index, ref DISPLAY_DEVICE dd, uint flags);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);

    const uint MONITOR_DEFAULTTONEAREST = 2;
    const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
    const int SW_SHOWNOACTIVATE = 4, SW_SHOWMINNOACTIVE = 7;
    static readonly IntPtr DPI_AWARENESS_PER_MONITOR_V2 = new IntPtr(-4);
    static bool minimisedByUs;
    static bool? playniteMuted;
    static bool hadVirtualDisplay;
    static DateTime? focusPlayniteAt;

    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    const byte VK_MENU = 0x12;
    const uint KEYEVENTF_KEYUP = 0x0002;

    // Core Audio, used to mute Playnite's own sound while it is hidden.
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumerator { }
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDeviceWithProps device);
    }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceWithProps
    {
        void _Activate();
        int OpenPropertyStore(int access, out IPropertyStore store);
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }
    // Undocumented but long-stable interface Windows' own Sound settings use to change the default device.
    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")] class PolicyConfigClient { }
    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        void _GetMixFormat(); void _GetDeviceFormat(); void _ResetDeviceFormat(); void _SetDeviceFormat();
        void _GetProcessingPeriod(); void _SetProcessingPeriod(); void _GetShareMode(); void _SetShareMode();
        void _GetPropertyValue(); void _SetPropertyValue();
        int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public short SpecVersion, DriverVersion, Size, DriverExtra; public int Fields;
        public int PositionX, PositionY, Orientation, FixedOutput;
        public short Color, Duplex, YResolution, TTOption, Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public short LogPixels; public int BitsPerPel, PelsWidth, PelsHeight, DisplayFlags, DisplayFrequency;
        public int ICMMethod, ICMIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettings(string device, int mode, ref DEVMODE dm);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int ChangeDisplaySettingsEx(string device, ref DEVMODE dm, IntPtr hwnd, uint flags, IntPtr param);
    const int ENUM_CURRENT_SETTINGS = -1;
    const int DM_PELSWIDTH = 0x80000, DM_PELSHEIGHT = 0x100000, DM_DISPLAYFREQUENCY = 0x400000;
    const uint CDS_UPDATEREGISTRY = 0x1;

    static Settings S = new Settings();
    static string lastScreenSet;
    static DateTime? checkTvModeAt;

    // Windows saves display modes per combination of connected screens, so a TV can come up at
    // 4096x2160 on its own but 3840x2160 alongside a virtual display. Whenever the set of
    // screens changes, put the TV back to the configured mode (saved for that combination too).
    // Not checked continuously, so games that switch resolution themselves are left alone.
    static void KeepTvMode()
    {
        int tvWidth = S.Display.TvWidth, tvHeight = S.Display.TvHeight, tvHz = S.Display.TvRefreshRate;
        if (tvWidth <= 0 || tvHeight <= 0) return;
        var screens = new System.Collections.Generic.List<string>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr mon, IntPtr hdc, ref RECT r, IntPtr d) =>
        {
            var info = new MONITORINFOEX { Size = Marshal.SizeOf(typeof(MONITORINFOEX)) };
            if (GetMonitorInfo(mon, ref info)) screens.Add(info.Device);
            return true;
        }, IntPtr.Zero);
        screens.Sort();
        string screenSet = string.Join(",", screens);
        if (screenSet != lastScreenSet) { lastScreenSet = screenSet; checkTvModeAt = DateTime.Now.AddSeconds(4); }

        if (!checkTvModeAt.HasValue || DateTime.Now < checkTvModeAt.Value) return;
        checkTvModeAt = null;
        string tv = screens.FirstOrDefault(s => !IsVirtualDisplay(s));
        if (tv == null) return;

        var dm = new DEVMODE { Size = (short)Marshal.SizeOf(typeof(DEVMODE)) };
        if (!EnumDisplaySettings(tv, ENUM_CURRENT_SETTINGS, ref dm)) return;
        if (dm.PelsWidth == tvWidth && dm.PelsHeight == tvHeight && (tvHz <= 0 || dm.DisplayFrequency == tvHz)) return;
        string before = dm.PelsWidth + "x" + dm.PelsHeight + "@" + dm.DisplayFrequency;
        dm.PelsWidth = tvWidth; dm.PelsHeight = tvHeight;
        dm.Fields = DM_PELSWIDTH | DM_PELSHEIGHT;
        if (tvHz > 0) { dm.DisplayFrequency = tvHz; dm.Fields |= DM_DISPLAYFREQUENCY; }
        int result = ChangeDisplaySettingsEx(tv, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero);
        Log("TV " + tv + " mode " + before + " -> " + tvWidth + "x" + tvHeight + (tvHz > 0 ? "@" + tvHz : "") + (result == 0 ? "" : " FAILED (" + result + ")"));
    }

    static bool? tvAudioWasActive, headsetWasActive;
    static bool hadVirtualForAudio;
    static DateTime? switchAudioAt;

    // Streaming hosts restore the pre-stream default output when a session ends, and Windows
    // falls back to another device when the TV is off. Put sound on the right device: the headset
    // while it's switched on (if one is configured), otherwise the TV. Checked at startup, when
    // the TV or headset comes or goes, and a few seconds after a stream ends; never during one.
    static void KeepAudioOnTv()
    {
        string tvAudioName = S.Audio.TvDeviceName, headsetName = S.Audio.HeadsetDeviceName;
        if (string.IsNullOrEmpty(tvAudioName) && string.IsNullOrEmpty(headsetName)) return;
        bool hasVirtual = AnyVirtualDisplay();
        string tvId = string.IsNullOrEmpty(tvAudioName) ? null : FindRenderDevice(tvAudioName);
        string headsetId = string.IsNullOrEmpty(headsetName) ? null : FindRenderDevice(headsetName);
        bool tvActive = tvId != null, headsetActive = headsetId != null;
        if (!hasVirtual && ((tvActive && (tvAudioWasActive != true || hadVirtualForAudio))
                            || (headsetWasActive.HasValue && headsetActive != headsetWasActive.Value)
                            || (headsetActive && hadVirtualForAudio)))
            switchAudioAt = DateTime.Now.AddSeconds(headsetActive != (headsetWasActive ?? false) ? 1 : 3);
        tvAudioWasActive = tvActive;
        headsetWasActive = headsetActive;
        hadVirtualForAudio = hasVirtual;

        if (!switchAudioAt.HasValue || DateTime.Now < switchAudioAt.Value) return;
        switchAudioAt = null;
        if (hasVirtual) return;
        string targetId = headsetActive ? headsetId : tvId, targetName = headsetActive ? headsetName : tvAudioName;
        if (targetId == null) return;
        string current = DefaultAudioDeviceName();
        if (current.IndexOf(targetName, StringComparison.OrdinalIgnoreCase) < 0)
        {
            var policy = (IPolicyConfig)new PolicyConfigClient();
            for (int role = 0; role < 3; role++) policy.SetDefaultEndpoint(targetId, role);
            Log("Audio output switched from " + current + " to " + DefaultAudioDeviceName());
        }
        // The headset's microphone becomes the default too, so voice chat picks it up
        if (headsetActive)
        {
            string micId = FindDevice(headsetName, 1 /* capture */);
            if (micId != null)
            {
                var policy = (IPolicyConfig)new PolicyConfigClient();
                for (int role = 0; role < 3; role++) policy.SetDefaultEndpoint(micId, role);
            }
        }
    }

    static string FindRenderDevice(string nameContains)
    {
        return FindDevice(nameContains, 0 /* render */);
    }

    static string FindDevice(string nameContains, int dataFlow)
    {
        IMMDeviceCollection devices;
        ((IMMDeviceEnumerator)new MMDeviceEnumerator()).EnumAudioEndpoints(dataFlow, 1 /* active */, out devices);
        uint count;
        devices.GetCount(out count);
        for (uint i = 0; i < count; i++)
        {
            IMMDevice device;
            devices.Item(i, out device);
            var props = (IMMDeviceWithProps)device;
            if (DeviceName(props).IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) < 0) continue;
            string id;
            props.GetId(out id);
            return id;
        }
        return null;
    }

    static string DeviceName(IMMDeviceWithProps device)
    {
        IPropertyStore store;
        PropVariant value;
        device.OpenPropertyStore(0, out store);
        var friendlyName = new PropertyKey { Format = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), Id = 14 };
        store.GetValue(ref friendlyName, out value);
        return Marshal.PtrToStringUni(value.Pointer) ?? "";
    }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore { void _GetCount(); void _GetAt(); int GetValue(ref PropertyKey key, out PropVariant value); }
    [StructLayout(LayoutKind.Sequential)] struct PropertyKey { public Guid Format; public int Id; }
    [StructLayout(LayoutKind.Explicit)] struct PropVariant { [FieldOffset(0)] public short Type; [FieldOffset(8)] public IntPtr Pointer; [FieldOffset(16)] public IntPtr Pad; }

    static string DefaultAudioDeviceName()
    {
        try
        {
            IMMDeviceWithProps device;
            ((IMMDeviceEnumerator)new MMDeviceEnumerator()).GetDefaultAudioEndpoint(0 /* render */, 1 /* multimedia */, out device);
            return DeviceName(device);
        }
        catch (Exception ex) { return "unknown (" + ex.Message + ")"; }
    }
    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection { int GetCount(out uint count); int Item(uint index, out IMMDevice device); }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice { int Activate(ref Guid iid, int clsCtx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object obj); }
    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionManager2
    {
        int GetAudioSessionControl(IntPtr guid, uint flags, out IntPtr control);
        int GetSimpleAudioVolume(IntPtr guid, uint flags, out IntPtr volume);
        int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
    }
    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionEnumerator { int GetCount(out int count); int GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object session); }
    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl2
    {
        void _GetState(); void _GetDisplayName(); void _SetDisplayName(); void _GetIconPath(); void _SetIconPath();
        void _GetGroupingParam(); void _SetGroupingParam(); void _RegisterNotification(); void _UnregisterNotification();
        void _GetSessionIdentifier(); void _GetSessionInstanceIdentifier();
        int GetProcessId(out uint pid);
    }
    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISimpleAudioVolume
    {
        int SetMasterVolume(float level, ref Guid context);
        int GetMasterVolume(out float level);
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
    }
    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        void _RegisterControlChangeNotify(); void _UnregisterControlChangeNotify(); void _GetChannelCount(); void _SetMasterVolumeLevel();
        int SetMasterVolumeLevelScalar(float level, ref Guid context);
        void _GetMasterVolumeLevel();
        int GetMasterVolumeLevelScalar(out float level);
    }

    // --- Volume mixer (Guide → Audio) ---
    // Each returns the volume as a percentage after changing it by delta (0 = just read it), or -1
    // if there's nothing to control (no game running, Discord silent or closed).

    internal static int GameVolume(int delta) { return AppVolume(PlayniteGameProcesses(), delta); }

    internal static int DiscordVolume(int delta)
    {
        return AppVolume(Process.GetProcessesByName("Discord").Select(p => (uint)p.Id).ToArray(), delta);
    }

    // Master volume of the current sound output (headset or TV)
    internal static int MasterVolume(int delta)
    {
        IMMDeviceWithProps device;
        ((IMMDeviceEnumerator)new MMDeviceEnumerator()).GetDefaultAudioEndpoint(0 /* render */, 1 /* multimedia */, out device);
        var iid = typeof(IAudioEndpointVolume).GUID;
        object obj;
        ((IMMDevice)(object)device).Activate(ref iid, 1, IntPtr.Zero, out obj);
        var volume = (IAudioEndpointVolume)obj;
        float level;
        volume.GetMasterVolumeLevelScalar(out level);
        if (delta != 0)
        {
            level = Math.Max(0f, Math.Min(1f, level + delta / 100f));
            var context = Guid.Empty;
            volume.SetMasterVolumeLevelScalar(level, ref context);
        }
        return (int)Math.Round(level * 100);
    }

    // Per-app volume, as in Windows' volume mixer: every audio session the processes own, on every output
    static int AppVolume(uint[] pids, int delta)
    {
        if (pids.Length == 0) return -1;
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        IMMDeviceCollection devices;
        enumerator.EnumAudioEndpoints(0 /* render */, 1 /* active */, out devices);
        uint deviceCount;
        devices.GetCount(out deviceCount);
        var managerIid = typeof(IAudioSessionManager2).GUID;
        var context = Guid.Empty;
        float? result = null;
        for (uint d = 0; d < deviceCount; d++)
        {
            IMMDevice device;
            object managerObj;
            devices.Item(d, out device);
            if (device.Activate(ref managerIid, 1, IntPtr.Zero, out managerObj) != 0) continue;
            IAudioSessionEnumerator sessions;
            ((IAudioSessionManager2)managerObj).GetSessionEnumerator(out sessions);
            int sessionCount;
            sessions.GetCount(out sessionCount);
            for (int s = 0; s < sessionCount; s++)
            {
                object session;
                sessions.GetSession(s, out session);
                uint pid;
                ((IAudioSessionControl2)session).GetProcessId(out pid);
                if (!pids.Contains(pid)) continue;
                var vol = (ISimpleAudioVolume)session;
                float level;
                vol.GetMasterVolume(out level);
                // All of an app's sessions move together, to the level of the first one found
                if (!result.HasValue) result = Math.Max(0f, Math.Min(1f, level + delta / 100f));
                if (delta != 0) vol.SetMasterVolume(result.Value, ref context);
            }
        }
        return result.HasValue ? (int)Math.Round(result.Value * 100) : -1;
    }

    // --- Guide helpers (Audio and System tabs) ---

    // Sound outputs that are plugged in, with the current default marked: (name, id, isDefault)
    internal static List<Tuple<string, string, bool>> OutputDevices()
    {
        var list = new List<Tuple<string, string, bool>>();
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        IMMDeviceWithProps def;
        string defId = "";
        try { enumerator.GetDefaultAudioEndpoint(0, 1, out def); def.GetId(out defId); } catch { }
        IMMDeviceCollection devices;
        enumerator.EnumAudioEndpoints(0 /* render */, 1 /* active */, out devices);
        uint count;
        devices.GetCount(out count);
        for (uint i = 0; i < count; i++)
        {
            IMMDevice device;
            devices.Item(i, out device);
            var props = (IMMDeviceWithProps)device;
            string id;
            props.GetId(out id);
            string name = DeviceName(props);
            if (name.IndexOf("Steam Streaming", StringComparison.OrdinalIgnoreCase) >= 0) continue;   // Steam's virtual speakers
            list.Add(Tuple.Create(name, id, id == defId));
        }
        return list;
    }

    internal static string SetOutput(string id, string name)
    {
        var policy = (IPolicyConfig)new PolicyConfigClient();
        for (int role = 0; role < 3; role++) policy.SetDefaultEndpoint(id, role);
        Log("Audio output set to " + name + " from the Guide");
        return "Sound now on " + name;
    }

    [StructLayout(LayoutKind.Sequential)] struct XBattery { public byte Type, Level; }
    [DllImport("xinput1_4.dll")] static extern int XInputGetBatteryInformation(int index, byte devType, out XBattery info);

    // The first connected controller's battery: "Wired", "Full", "Medium", "Low", "Empty", or "" if none
    internal static string ControllerBattery()
    {
        for (int i = 0; i < 4; i++)
        {
            XState s;
            if (XInputGetState(i, out s) != 0) continue;
            XBattery b;
            if (XInputGetBatteryInformation(i, 0 /* gamepad */, out b) != 0) return "";
            if (b.Type == 1 /* wired */) return "Wired";
            if (b.Type == 0 || b.Type == 0xFF) return "";
            switch (b.Level) { case 0: return "Empty"; case 1: return "Low"; case 2: return "Medium"; default: return "Full"; }
        }
        return "";
    }

    // Battery as a percentage for the Guide: "73%" from Windows.Gaming.Input's battery report when
    // the controller gives one, otherwise "~60%" estimated from XInput's four levels. "" if there's
    // no wireless controller.
    internal static string ControllerBatteryPercent(string level)
    {
        if (level == "" || level == "Wired") return "";
        try { int exact = GamingInputBattery(); if (exact >= 0) return exact + "%"; }
        catch (Exception) { }   // Windows.Gaming.Input not available
        switch (level) { case "Empty": return "~5%"; case "Low": return "~25%"; case "Medium": return "~60%"; default: return "~100%"; }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static int GamingInputBattery()
    {
        foreach (var pad in Windows.Gaming.Input.Gamepad.Gamepads)
        {
            if (!pad.IsWireless) continue;
            var r = pad.TryGetBatteryReport();
            if (r == null || !r.RemainingCapacityInMilliwattHours.HasValue || !r.FullChargeCapacityInMilliwattHours.HasValue) continue;
            int full = r.FullChargeCapacityInMilliwattHours.Value;
            if (full > 0) return Math.Max(0, Math.Min(100, r.RemainingCapacityInMilliwattHours.Value * 100 / full));
        }
        return -1;
    }

    // Saves what's on the TV to Pictures\CouchKit
    internal static string Screenshot()
    {
        var tv = TvBounds();
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "CouchKit");
        Directory.CreateDirectory(dir);
        string name = "Screenshot " + DateTime.Now.ToString("yyyy-MM-dd HHmmss") + ".png";
        using (var bmp = new System.Drawing.Bitmap(tv.Width, tv.Height))
        {
            using (var g = System.Drawing.Graphics.FromImage(bmp)) g.CopyFromScreen(tv.Location, System.Drawing.Point.Empty, tv.Size);
            bmp.Save(Path.Combine(dir, name), System.Drawing.Imaging.ImageFormat.Png);
        }
        Log("Screenshot saved: " + name);
        return name;
    }

    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);

    // Turns the screen signal off (the TV goes to standby on most sets); any input brings it back
    internal static void ScreenOff()
    {
        Log("Screen off from the Guide");
        SendMessage(new IntPtr(0xFFFF) /* HWND_BROADCAST */, 0x0112 /* WM_SYSCOMMAND */, new IntPtr(0xF170) /* SC_MONITORPOWER */, new IntPtr(2));
    }

    internal static string CloseRunningGame()
    {
        uint[] game = PlayniteGameProcesses();
        if (game.Length == 0) return "No game is running";
        CloseGame();
        return "Closing the game";
    }

    internal static void FocusGame()
    {
        IntPtr w = MainWindowOf(PlayniteGameProcesses());
        if (w != IntPtr.Zero) BringToFront(w);
    }

    internal static void FocusPlaynite()
    {
        var p = Process.GetProcessesByName("Playnite.FullscreenApp").FirstOrDefault();
        if (p != null && p.MainWindowHandle != IntPtr.Zero) BringToFront(p.MainWindowHandle);
    }

    internal static void RestartPlaynite()
    {
        var p = Process.GetProcessesByName("Playnite.FullscreenApp").FirstOrDefault();
        if (p == null) return;
        string exe = ImagePath((uint)p.Id);
        Log("Restarting Playnite from the Guide");
        p.CloseMainWindow();
        if (!p.WaitForExit(20000)) p.Kill();
        if (exe != null) Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = Path.GetDirectoryName(exe) });
    }

    internal static void PowerAction(bool restart)
    {
        Log((restart ? "Restart" : "Shut down") + " from the Guide");
        Process.Start(new ProcessStartInfo("shutdown.exe", (restart ? "/r" : "/s") + " /t 0") { CreateNoWindow = true, UseShellExecute = false });
    }

    // The TV's screen area (the first physical screen), for windows CouchKit shows itself
    internal static System.Drawing.Rectangle TvBounds()
    {
        RECT? r = FirstPhysicalScreen();
        if (!r.HasValue) return System.Windows.Forms.Screen.PrimaryScreen.Bounds;
        return System.Drawing.Rectangle.FromLTRB(r.Value.Left, r.Value.Top, r.Value.Right, r.Value.Bottom);
    }

    const ushort Start = 0x0010, Back = 0x0020, Combo = Start | Back, Guide = 0x0400;
    const int DoubleTapMs = 350;
    const uint WM_CLOSE = 0x0010;

    static readonly string[] BuiltInProtected = {
        "explorer", "playnite.fullscreenapp", "playnite.desktopapp", "steam", "steamwebhelper",
        "sunshine", "sunshinesvc", "apollo", "dwm", "searchhost", "startmenuexperiencehost",
        "shellexperiencehost", "applicationframehost", "textinputhost", "lockapp", "couchkit", "openrgb"
    };

    static bool IsProtected(string processName)
    {
        string name = processName.ToLowerInvariant();
        return BuiltInProtected.Contains(name)
            || S.Games.ProtectedProcesses.Any(p => string.Equals(p.Replace(".exe", ""), name, StringComparison.OrdinalIgnoreCase));
    }

    static readonly string LogPath = Path.Combine(Settings.DataDir, "CouchKit.log");

    static void Main(string[] args)
    {
        if (args.Length > 0 && (args[0] == "--sensors" || args[0] == "--sensors-copy"))   // admin helper: FanControl's sensors to sensors.json
        {
            Pc.SensorsHelper(args[0] == "--sensors-copy");
            return;
        }
        if (args.Length > 1 && args[0] == "--demo")   // demo video frames: --demo <folder> [background image]
        {
            string note;
            var ds = Settings.Load(out note);
            SmartHome.Start(ds.SmartHome, ds.Lighting);
            GuidePanel.Demo(ds.Discord, args[1], args.Length > 2 ? args[2] : null);
            return;
        }
        if (args.Length > 1 && args[0] == "--preview")   // design check: the Guide, banner and HUD as PNGs
        {
            string note;
            var ps = Settings.Load(out note);
            SmartHome.Start(ps.SmartHome, ps.Lighting);
            GuidePanel.Preview(ps.Discord, args[1]);
            return;
        }        if (args.Length > 1 && args[0] == "--palette")   // test: palettes picked from the given images
        {
            File.WriteAllLines(Path.Combine(Settings.DataDir, "palette-test.txt"),
                args.Skip(1).Select(a => Lighting.Describe(a) + "  " + Path.GetFileName(a)));
            return;
        }
        bool isNew;
        using (new Mutex(true, "Local\\CouchKit_Singleton", out isNew))
        {
            if (!isNew) return;
            SetProcessDpiAwarenessContext(DPI_AWARENESS_PER_MONITOR_V2);
            string note;
            S = Settings.Load(out note);
            Log("Started: " + note);
            if (S.Lighting.Enabled)
            {
                Lighting.Log = Log;
                Lighting.Start(S.Lighting);
            }
            DiscordBridge.Start(S.Discord);
            UiSound.Configure(S.Guide.Sounds, S.Guide.SoundVolume);
            GuidePanel.Animations = S.Guide.Animations;
            GuidePanel.Start(S.Discord);
            if (S.Achievements.Enabled && Achievements.Available)
            {
                GuidePanel.AchievementPopups = S.Achievements.Popups;
                Achievements.Watch(GuidePanel.Unlocked);
            }
            Achievements.Enabled = S.Achievements.Enabled;
            SmartHome.Log = Log;
            SmartHome.Start(S.SmartHome, S.Lighting);

            var held = new Stopwatch[4];
            var stage = new int[4];
            var target = new uint[4][];
            var guideHeld = new Stopwatch[4];
            var pendingTap = new Stopwatch[4];
            var displayCheck = Stopwatch.StartNew();

            while (true)
            {
                if (displayCheck.ElapsedMilliseconds >= 1000)
                {
                    displayCheck.Restart();
                    try { KeepPlayniteOffVirtualDisplay(); }
                    catch (Exception ex) { Log("Display check failed: " + ex.Message); }
                    try { KeepGameOffVirtualDisplay(); }
                    catch (Exception ex) { Log("Game display check failed: " + ex.Message); }
                    try { KeepAudioOnTv(); }
                    catch (Exception ex) { Log("Audio check failed: " + ex.Message); }
                    try { KeepTvMode(); }
                    catch (Exception ex) { Log("TV mode check failed: " + ex.Message); }
                }

                for (int i = 0; i < 4 && S.Controller.Enabled; i++)
                {
                    XState s;
                    bool connected = XInputGetState(i, out s) == 0;

                    // Xbox button: a tap switches game/Playnite (or closes the Guide); a double tap
                    // opens the Guide (or leaves mouse mode). A single tap waits briefly to be sure.
                    bool guide = connected && (s.Pad.Buttons & Guide) != 0;
                    if (guide && guideHeld[i] == null) guideHeld[i] = Stopwatch.StartNew();
                    else if (!guide && guideHeld[i] != null)
                    {
                        bool tap = guideHeld[i].ElapsedMilliseconds <= S.Controller.GuideTapMaxMs;
                        guideHeld[i] = null;
                        if (tap && pendingTap[i] != null && pendingTap[i].ElapsedMilliseconds <= DoubleTapMs)
                        {
                            pendingTap[i] = null;
                            try { PadExtras.DoubleTap(i, S.Controller); }
                            catch (Exception ex) { Log("Xbox button double tap failed: " + ex.Message); }
                        }
                        else if (tap) pendingTap[i] = Stopwatch.StartNew();
                    }
                    if (pendingTap[i] != null && pendingTap[i].ElapsedMilliseconds > DoubleTapMs && guideHeld[i] == null)
                    {
                        pendingTap[i] = null;
                        try
                        {
                            if (GuidePanel.IsOpen) GuidePanel.Close();
                            else if (DiscordBridge.Incoming && S.Controller.DiscordButtons) GuidePanel.Open();   // ringing: tap, then A answers
                            else if (S.Controller.GuideButton) ToggleGameAndPlaynite();
                        }
                        catch (Exception ex) { Log("Xbox button action failed: " + ex.Message); }
                    }

                    try { PadExtras.Handle(i, connected, S.Controller, s.Pad.Buttons, s.Pad.LX, s.Pad.LY, s.Pad.RY); }
                    catch (Exception ex) { Log("Controller extras failed: " + ex.Message); }

                    bool down = connected && (s.Pad.Buttons & Combo) == Combo;
                    if (!down) { held[i] = null; stage[i] = 0; continue; }
                    if (held[i] == null) { held[i] = Stopwatch.StartNew(); continue; }

                    long ms = held[i].ElapsedMilliseconds;
                    if (stage[i] == 0 && ms >= S.Controller.CloseHoldMs)
                    {
                        stage[i] = 1;
                        target[i] = CloseGame();
                        if (target[i].Length > 0) Rumble(i, 200);
                    }
                    else if (stage[i] == 1 && ms >= S.Controller.KillHoldMs && target[i].Length > 0)
                    {
                        stage[i] = 2;
                        bool killed = false;
                        foreach (uint pid in target[i]) killed |= Kill(pid);
                        if (killed) Rumble(i, 600);
                    }
                }
                Thread.Sleep(30);
            }
        }
    }

    // The CouchKit Playnite add-on writes the game's install folder here when a game starts and
    // deletes it when the game exits, so the game can be found wherever focus is.
    static readonly string CurrentGamePath = Path.Combine(Settings.DataDir, "current-game.txt");

    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr process, uint flags, System.Text.StringBuilder name, ref int size);
    delegate bool WindowEnumProc(IntPtr hwnd, IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumWindows(WindowEnumProc proc, IntPtr data);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    static uint[] CloseGame()
    {
        uint[] game = PlayniteGameProcesses();
        if (game.Length == 0)
        {
            uint pid = CloseForeground();
            return pid == 0 ? new uint[0] : new[] { pid };
        }
        EnumWindows((hwnd, d) =>
        {
            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            if (IsWindowVisible(hwnd) && game.Contains(pid)) PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            return true;
        }, IntPtr.Zero);
        Log("Close requested for Playnite game: " + string.Join(", ", game.Select(p => ProcessLabel(p))));
        return game;
    }

    // Emulated games record their ROM folder (or nothing) as the install folder, so when no
    // process runs from it the game is whichever emulator is running from the configured folders.
    static uint[] PlayniteGameProcesses()
    {
        try
        {
            if (!File.Exists(CurrentGamePath)) return new uint[0];
            string dir = File.ReadAllText(CurrentGamePath).Trim().TrimEnd('\\') + "\\";
            // A drive root or other too-broad folder would match everything, so skip it.
            uint[] game = dir.Count(c => c == '\\') >= 3 ? ProcessesUnder(dir) : new uint[0];
            foreach (string emulators in S.Games.EmulatorFolders)
            {
                if (game.Length > 0) break;
                if (!string.IsNullOrEmpty(emulators)) game = ProcessesUnder(emulators.TrimEnd('\\') + "\\");
            }
            return game;
        }
        catch (Exception ex) { Log("Game lookup failed: " + ex.Message); return new uint[0]; }
    }

    static uint[] ProcessesUnder(string dir)
    {
        return Process.GetProcesses()
            .Where(p => !IsProtected(p.ProcessName))
            .Where(p => { string exe = ImagePath((uint)p.Id); return exe != null && exe.StartsWith(dir, StringComparison.OrdinalIgnoreCase); })
            .Select(p => (uint)p.Id).ToArray();
    }

    // Xbox button tap, console "Home" style: game -> Playnite; anything else -> the game if one is
    // running, otherwise Playnite.
    static void ToggleGameAndPlaynite()
    {
        var playnite = Process.GetProcessesByName("Playnite.FullscreenApp").FirstOrDefault();
        if (playnite == null || playnite.MainWindowHandle == IntPtr.Zero) return;
        IntPtr playniteWnd = playnite.MainWindowHandle;
        if (minimisedByUs) return; // TV is off; Playnite is deliberately hidden

        uint fgPid;
        GetWindowThreadProcessId(GetForegroundWindow(), out fgPid);
        uint[] game = PlayniteGameProcesses();

        IntPtr gameWnd = game.Length > 0 ? MainWindowOf(game) : IntPtr.Zero;
        if (gameWnd != IntPtr.Zero && !game.Contains(fgPid))
        {
            // From Playnite, or from anything else that took focus (e.g. a remote session's window): to the game
            BringToFront(gameWnd);
            Log("Xbox button: back to game");
        }
        else if (fgPid != (uint)playnite.Id)
        {
            BringToFront(playniteWnd);
            Log("Xbox button: to Playnite");
        }
    }

    // Largest visible top-level window belonging to the given processes.
    static IntPtr MainWindowOf(uint[] pids)
    {
        IntPtr best = IntPtr.Zero;
        long bestArea = 0;
        EnumWindows((hwnd, d) =>
        {
            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            RECT r;
            if (IsWindowVisible(hwnd) && pids.Contains(pid) && GetWindowRect(hwnd, out r))
            {
                long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
                if (area > bestArea) { bestArea = area; best = hwnd; }
            }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    const int SW_RESTORE = 9;

    internal static void BringToFront(IntPtr hwnd)
    {
        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
        // Windows only lets the process with the latest input change the foreground window;
        // a synthetic Alt press satisfies that.
        keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
        SetForegroundWindow(hwnd);
        keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    static string ImagePath(uint pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new System.Text.StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    static string ProcessLabel(uint pid)
    {
        try { return Process.GetProcessById((int)pid).ProcessName + " (" + pid + ")"; }
        catch { return pid.ToString(); }
    }

    static uint CloseForeground()
    {
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return 0;
        uint pid;
        GetWindowThreadProcessId(hwnd, out pid);
        try
        {
            string name = Process.GetProcessById((int)pid).ProcessName.ToLowerInvariant();
            if (IsProtected(name)) { Log("Combo held but no Playnite game running and " + name + " is in front: nothing closed"); return 0; }
            PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            Log("Close requested: " + name + " (" + pid + ")");
            return pid;
        }
        catch (Exception ex) { Log("Close failed: " + ex.Message); return 0; }
    }

    static bool Kill(uint pid)
    {
        try
        {
            var p = Process.GetProcessById((int)pid);
            if (p.HasExited) return false;
            Log("Force kill: " + p.ProcessName + " (" + pid + ")");
            p.Kill();
            return true;
        }
        catch (ArgumentException) { return false; } // already gone
        catch (Exception ex) { Log("Kill failed: " + ex.Message); return false; }
    }

    // The game Playnite started belongs on the TV too. Games open on the primary display, which
    // is the virtual display while a stream is connected, and a game left running with the TV off
    // fills the remote session. Move it to the TV, or minimise it until the TV is back.
    static IntPtr gameMinimisedByUs;
    static IntPtr lastMovedGameWnd;
    static int gameMoveAttempts;
    const int MaxGameMoveAttempts = 3;

    static void KeepGameOffVirtualDisplay()
    {
        if (!File.Exists(CurrentGamePath)) { gameMinimisedByUs = IntPtr.Zero; lastMovedGameWnd = IntPtr.Zero; gameMoveAttempts = 0; gameMuted = null; return; }
        uint[] game = PlayniteGameProcesses();
        if (game.Length == 0) return;
        if (S.Display.MuteWhileStreaming) SetGameMuted(game, AnyVirtualDisplay());
        if (!S.Display.KeepGameOnTv) return;
        IntPtr hwnd = MainWindowOf(game);
        if (hwnd == IntPtr.Zero) return;
        RECT? physical = FirstPhysicalScreen();

        if (IsIconic(hwnd))
        {
            if (hwnd == gameMinimisedByUs && physical.HasValue)
            {
                ShowWindow(hwnd, SW_SHOWNOACTIVATE);
                gameMinimisedByUs = IntPtr.Zero;
                Log("Physical screen back: restored game " + ProcessLabel(WindowPid(hwnd)));
            }
            return;
        }

        IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFOEX { Size = Marshal.SizeOf(typeof(MONITORINFOEX)) };
        if (!GetMonitorInfo(monitor, ref info)) return;
        if (!IsVirtualDisplay(info.Device)) { gameMoveAttempts = 0; return; } // on the TV: settled

        if (!physical.HasValue)
        {
            ShowWindow(hwnd, SW_SHOWMINNOACTIVE);
            gameMinimisedByUs = hwnd;
            Log("Game on virtual display with no physical screen: minimised " + ProcessLabel(WindowPid(hwnd)));
            return;
        }

        if (hwnd != lastMovedGameWnd) { lastMovedGameWnd = hwnd; gameMoveAttempts = 0; }
        if (gameMoveAttempts >= MaxGameMoveAttempts) return; // the game keeps moving itself back; leave it
        gameMoveAttempts++;

        RECT w;
        GetWindowRect(hwnd, out w);
        RECT tv = physical.Value, vd = info.Monitor;
        bool fillsScreen = w.Left <= vd.Left && w.Top <= vd.Top && w.Right >= vd.Right && w.Bottom >= vd.Bottom;
        if (fillsScreen) MoveTo(hwnd, tv);
        else
        {
            int width = Math.Min(w.Right - w.Left, tv.Right - tv.Left), height = Math.Min(w.Bottom - w.Top, tv.Bottom - tv.Top);
            int x = tv.Left + ((tv.Right - tv.Left) - width) / 2, y = tv.Top + ((tv.Bottom - tv.Top) - height) / 2;
            SetWindowPos(hwnd, IntPtr.Zero, x, y, width, height, SWP_NOZORDER | SWP_NOACTIVATE);
        }
        Log("Game on virtual display: moved to physical screen (" + (fillsScreen ? "full screen" : "windowed") + ", attempt " + gameMoveAttempts + ") " + ProcessLabel(WindowPid(hwnd)));
    }

    static uint WindowPid(IntPtr hwnd)
    {
        uint pid;
        GetWindowThreadProcessId(hwnd, out pid);
        return pid;
    }

    static void KeepPlayniteOffVirtualDisplay()
    {
        var playnite = Process.GetProcessesByName("Playnite.FullscreenApp").FirstOrDefault();
        if (playnite == null) { minimisedByUs = false; playniteMuted = null; return; }
        IntPtr hwnd = playnite.MainWindowHandle;
        if (hwnd == IntPtr.Zero) return;

        RECT? physical = FirstPhysicalScreen();

        // Stream ended: windows from the virtual display land on the TV above Playnite, and
        // Playnite stays silent while unfocused. Bring it to the front once things settle.
        bool hasVirtual = AnyVirtualDisplay();
        bool streamMute = S.Display.MuteWhileStreaming && hasVirtual;
        if (hadVirtualDisplay && !hasVirtual && physical.HasValue && S.Display.FocusPlayniteAfterStream)
        {
            focusPlayniteAt = DateTime.Now.AddSeconds(2);
            Log("Virtual display gone");
        }
        hadVirtualDisplay = hasVirtual;
        if (focusPlayniteAt.HasValue && DateTime.Now >= focusPlayniteAt.Value)
        {
            focusPlayniteAt = null;
            // A running game gets the focus back instead, so the controller keeps working in it.
            IntPtr gameWnd = MainWindowOf(PlayniteGameProcesses());
            if (!hasVirtual && gameWnd != IntPtr.Zero)
            {
                BringToFront(gameWnd);
                Log("Focused the running game " + ProcessLabel(WindowPid(gameWnd)) + ", audio output: " + DefaultAudioDeviceName());
            }
            else if (!hasVirtual && !IsIconic(hwnd))
            {
                // Windows only lets the process with the latest input change the foreground
                // window; a synthetic Alt press satisfies that.
                keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
                bool ok = SetForegroundWindow(hwnd);
                keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                Log("Focused Playnite: " + ok + ", audio output: " + DefaultAudioDeviceName());
            }
        }

        if (IsIconic(hwnd))
        {
            // Adopt a Playnite left minimised with no TV (e.g. after CouchKit restarts).
            if (!physical.HasValue && S.Display.KeepPlayniteOnTv) minimisedByUs = true;
            if (minimisedByUs && physical.HasValue)
            {
                // Let Playnite position itself (it is set to Monitor 1, the TV while a stream is
                // connected); an external move leaves its layout sized for the old screen.
                // If it is still on the virtual display, the next check moves it.
                ShowWindow(hwnd, SW_SHOWNOACTIVATE);
                minimisedByUs = false;
                Log("Physical screen back: restored Playnite to it");
            }
            SetPlayniteMuted(minimisedByUs || streamMute);
            return;
        }
        // While a stream is connected all audio goes to it, so Playnite on the TV stays silent.
        SetPlayniteMuted(streamMute);
        if (!S.Display.KeepPlayniteOnTv) return;

        var info = new MONITORINFOEX { Size = Marshal.SizeOf(typeof(MONITORINFOEX)) };
        if (!GetMonitorInfo(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), ref info)) return;
        if (!IsVirtualDisplay(info.Device)) return;

        if (physical.HasValue)
        {
            MoveTo(hwnd, physical.Value);
            Log("Playnite was on virtual display " + info.Device + ": moved back to physical screen");
        }
        else
        {
            ShowWindow(hwnd, SW_SHOWMINNOACTIVE);
            minimisedByUs = true;
            SetPlayniteMuted(true);
            Log("Playnite was on virtual display " + info.Device + " with no physical screen: minimised and muted");
        }
    }

    // Mutes or unmutes every audio session owned by a Playnite process, on every output device.
    // Re-applied each check (sessions can appear later, e.g. when music starts) but only logged on change.
    static void SetPlayniteMuted(bool mute)
    {
        if (!mute && playniteMuted == false) return;
        var pids = Process.GetProcesses().Where(p => p.ProcessName.StartsWith("Playnite", StringComparison.OrdinalIgnoreCase))
                                         .Select(p => (uint)p.Id).ToArray();
        SetProcessesMuted(pids, mute);
        if (playniteMuted != mute) Log(mute ? "Playnite muted" : "Playnite unmuted");
        playniteMuted = mute;
    }

    // While a stream is connected all sound goes to Moonlight, so the TV game is muted too
    // (it couldn't be heard on the TV anyway); unmuted once when the stream ends.
    static bool? gameMuted;

    static void SetGameMuted(uint[] game, bool mute)
    {
        if (!mute && gameMuted == false) return;
        SetProcessesMuted(game, mute);
        if (gameMuted != mute) Log((mute ? "Game muted (stream connected): " : "Game unmuted: ") + string.Join(", ", game.Select(p => ProcessLabel(p))));
        gameMuted = mute;
    }

    static void SetProcessesMuted(uint[] pids, bool mute)
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        IMMDeviceCollection devices;
        enumerator.EnumAudioEndpoints(0 /* render */, 1 /* active */, out devices);
        uint deviceCount;
        devices.GetCount(out deviceCount);
        var managerIid = typeof(IAudioSessionManager2).GUID;
        var context = Guid.Empty;
        for (uint d = 0; d < deviceCount; d++)
        {
            IMMDevice device;
            object managerObj;
            devices.Item(d, out device);
            if (device.Activate(ref managerIid, 1 /* CLSCTX_INPROC_SERVER */, IntPtr.Zero, out managerObj) != 0) continue;
            IAudioSessionEnumerator sessions;
            ((IAudioSessionManager2)managerObj).GetSessionEnumerator(out sessions);
            int sessionCount;
            sessions.GetCount(out sessionCount);
            for (int s = 0; s < sessionCount; s++)
            {
                object session;
                sessions.GetSession(s, out session);
                uint pid;
                ((IAudioSessionControl2)session).GetProcessId(out pid);
                if (pids.Contains(pid)) ((ISimpleAudioVolume)session).SetMute(mute, ref context);
            }
        }
    }

    static RECT? FirstPhysicalScreen()
    {
        RECT? found = null;
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr mon, IntPtr hdc, ref RECT r, IntPtr d) =>
        {
            var info = new MONITORINFOEX { Size = Marshal.SizeOf(typeof(MONITORINFOEX)) };
            if (GetMonitorInfo(mon, ref info) && !IsVirtualDisplay(info.Device)) { found = info.Monitor; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    static bool AnyVirtualDisplay()
    {
        bool found = false;
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr mon, IntPtr hdc, ref RECT r, IntPtr d) =>
        {
            var info = new MONITORINFOEX { Size = Marshal.SizeOf(typeof(MONITORINFOEX)) };
            if (GetMonitorInfo(mon, ref info) && IsVirtualDisplay(info.Device)) { found = true; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    static bool IsVirtualDisplay(string deviceName)
    {
        var dd = new DISPLAY_DEVICE { Size = Marshal.SizeOf(typeof(DISPLAY_DEVICE)) };
        for (uint i = 0; EnumDisplayDevices(null, i, ref dd, 0); i++)
        {
            if (string.Equals(dd.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
            {
                string adapter = dd.DeviceString;
                return S.Display.VirtualDisplayNames.Any(n => !string.IsNullOrEmpty(n) && adapter.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0);
            }
            dd.Size = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
        }
        return false;
    }

    static void MoveTo(IntPtr hwnd, RECT r)
    {
        SetWindowPos(hwnd, IntPtr.Zero, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, SWP_NOZORDER | SWP_NOACTIVATE);
    }

    internal static void Rumble(int index, int ms)
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            var on = new XVibration { Left = 40000, Right = 40000 };
            var off = new XVibration();
            XInputSetState(index, ref on);
            Thread.Sleep(ms);
            XInputSetState(index, ref off);
        });
    }

    internal static void Log(string msg)
    {
        try { File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine); }
        catch { }
    }
}
