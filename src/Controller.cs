// Controller extras for Discord and desktop apps from the couch:
//   Back + Y    open/close the Party panel (Discord favourites, mute, deafen, leave)
//   Back + LB   Discord mute/unmute (Discord's own mute, so in-game voice chat is untouched).
//               One long buzz = muted, two short = live.
//   Back + A    answer an incoming Discord call
//   Back + B    leave the Discord call (or decline an incoming one)
//   Back + RB   mouse mode on/off: left stick moves the pointer, A clicks, B right-clicks,
//               right stick scrolls
// Each fires once per press. Back + Start stays the quit-game combo.
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

static class PadExtras
{
    const ushort Up = 0x0001, Down = 0x0002, Back = 0x0020, LB = 0x0100, RB = 0x0200, A = 0x1000, B = 0x2000, Y = 0x8000;
    static readonly ushort[] last = new ushort[4];
    static bool mouseMode;
    static double subX, subY;

    static PadExtras()
    {
        PartyPanel.EnterMouseMode = () => { ShowDiscord(); mouseMode = true; CouchKit.Log("Mouse mode on"); };
    }

    static void Buzz(int pad, bool longBuzz)
    {
        if (longBuzz) CouchKit.Rumble(pad, 350);
        else ThreadPool.QueueUserWorkItem(_ => { CouchKit.Rumble(pad, 70); Thread.Sleep(200); CouchKit.Rumble(pad, 70); });
    }

    [DllImport("user32.dll")] static extern void mouse_event(uint flags, int dx, int dy, int data, UIntPtr extra);
    const uint MOVE = 0x0001, LEFTDOWN = 0x0002, LEFTUP = 0x0004, RIGHTDOWN = 0x0008, RIGHTUP = 0x0010, WHEEL = 0x0800;

    public static void Handle(int pad, bool connected, ControllerSettings s, ushort buttons, short lx, short ly, short ry)
    {
        if (!connected) { last[pad] = 0; return; }
        ushort was = last[pad];
        last[pad] = buttons;
        Func<ushort, bool> pressed = combo => (buttons & combo) == combo && (was & combo) != combo;

        if (s.MouseMode && pressed((ushort)(Back | RB)))
        {
            mouseMode = !mouseMode;
            CouchKit.Rumble(pad, mouseMode ? 120 : 60);
            CouchKit.Log("Mouse mode " + (mouseMode ? "on" : "off"));
        }
        if (s.DiscordButtons)
        {
            int p = pad;
            if (pressed((ushort)(Back | Y))) { PartyPanel.Toggle(); CouchKit.Rumble(p, 60); return; }
            if (pressed((ushort)(Back | LB)))
                DiscordBridge.Do(DiscordBridge.ToggleMute, msg => Buzz(p, DiscordBridge.Muted));
            if (pressed((ushort)(Back | A)))
                DiscordBridge.Do(DiscordBridge.Answer, msg => CouchKit.Rumble(p, 120));
            if (pressed((ushort)(Back | B)))
                DiscordBridge.Do(() => DiscordBridge.Incoming ? DiscordBridge.Decline() : DiscordBridge.Leave(), msg => CouchKit.Rumble(p, 120));
        }

        // The Party panel takes the D-pad, A and B while it's open
        if (PartyPanel.IsOpen)
        {
            if ((buttons & Back) != 0) return;
            if (pressed(Up)) PartyPanel.Move(-1);
            if (pressed(Down)) PartyPanel.Move(1);
            if (pressed(A)) PartyPanel.Select();
            if (pressed(B)) PartyPanel.Close();
            return;
        }

        if (!mouseMode || (buttons & Back) != 0) return;   // Back is held for a combo: don't click

        // Pointer: deadzone, then a curve so small movements are precise and full tilt is fast
        double x = Axis(lx), y = Axis(ly);
        double speed = s.MouseSpeed;
        subX += Math.Sign(x) * x * x * speed;
        subY -= Math.Sign(y) * y * y * speed;
        int dx = (int)subX, dy = (int)subY;
        subX -= dx; subY -= dy;
        if (dx != 0 || dy != 0) mouse_event(MOVE, dx, dy, 0, UIntPtr.Zero);

        if ((buttons & A) != 0 && (was & A) == 0) mouse_event(LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
        if ((buttons & A) == 0 && (was & A) != 0) mouse_event(LEFTUP, 0, 0, 0, UIntPtr.Zero);
        if ((buttons & B) != 0 && (was & B) == 0) mouse_event(RIGHTDOWN, 0, 0, 0, UIntPtr.Zero);
        if ((buttons & B) == 0 && (was & B) != 0) mouse_event(RIGHTUP, 0, 0, 0, UIntPtr.Zero);

        double scroll = Axis(ry);
        if (scroll != 0) mouse_event(WHEEL, 0, 0, (int)(scroll * Math.Abs(scroll) * 60), UIntPtr.Zero);
    }

    // Stick value to -1..1 with a 25% deadzone (Xbox sticks rest slightly off-centre)
    static double Axis(short v)
    {
        double f = v / 32768.0, dead = 0.25;
        if (Math.Abs(f) < dead) return 0;
        return Math.Sign(f) * (Math.Abs(f) - dead) / (1 - dead);
    }

    static void ShowDiscord()
    {
        try
        {
            var discord = Process.GetProcessesByName("Discord").FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero);
            if (discord != null) { CouchKit.BringToFront(discord.MainWindowHandle); CouchKit.Log("Discord brought to front"); return; }
            // Not running, or only in the tray: Discord's launcher shows (or starts) the main window
            string updater = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Discord", "Update.exe");
            if (!File.Exists(updater)) { CouchKit.Log("Discord not found"); return; }
            Process.Start(updater, "--processStart Discord.exe");
            ThreadPool.QueueUserWorkItem(_ =>
            {
                for (int i = 0; i < 20; i++)
                {
                    Thread.Sleep(500);
                    var p = Process.GetProcessesByName("Discord").FirstOrDefault(q => q.MainWindowHandle != IntPtr.Zero);
                    if (p != null) { CouchKit.BringToFront(p.MainWindowHandle); break; }
                }
            });
            CouchKit.Log("Discord opened");
        }
        catch (Exception ex) { CouchKit.Log("Could not show Discord: " + ex.Message); }
    }
}
