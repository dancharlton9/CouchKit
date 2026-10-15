// Controller extras for Discord and desktop apps from the couch. No button combos: Playnite and
// games see every normal button, so anything held with them would leak through (Y opens Playnite's
// search keyboard, Back its menu). Instead:
//   Double-tap the Xbox button   open/close the Guide (CouchKit.cs detects the double tap).
//                                It takes the focus, so D-pad/A/B go to the panel and nothing else.
//                                Up/down/left/right also work on the left stick, and repeat when held.
//   Mouse mode (started from the panel): left stick moves the pointer, A clicks, B right-clicks,
//                                right stick scrolls; double-tap the Xbox button to leave it.
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

static class PadExtras
{
    const ushort Up = 0x0001, Down = 0x0002, Left = 0x0004, Right = 0x0008, LB = 0x0100, RB = 0x0200, A = 0x1000, B = 0x2000;
    static readonly ushort[] last = new ushort[4];
    static bool mouseMode;
    // Hold-to-repeat in the Guide, per pad and direction (up, down, left, right): when it was
    // first pressed and when it last fired
    static readonly DateTime[,] heldSince = new DateTime[4, 4], lastFire = new DateTime[4, 4];
    static readonly bool[,] held = new bool[4, 4];
    static double subX, subY;

    static PadExtras()
    {
        GuidePanel.EnterMouseMode = () => { ShowDiscord(); mouseMode = true; CouchKit.Log("Mouse mode on"); };
    }

    // Double tap of the Xbox button: leaves mouse mode, otherwise opens/closes the Guide
    public static void DoubleTap(int pad, ControllerSettings s)
    {
        if (mouseMode) { mouseMode = false; CouchKit.Log("Mouse mode off"); CouchKit.Rumble(pad, 60); return; }
        if (!s.DiscordButtons) return;
        GuidePanel.Toggle();
        CouchKit.Rumble(pad, 60);
    }

    [DllImport("user32.dll")] static extern void mouse_event(uint flags, int dx, int dy, int data, UIntPtr extra);
    const uint MOVE = 0x0001, LEFTDOWN = 0x0002, LEFTUP = 0x0004, RIGHTDOWN = 0x0008, RIGHTUP = 0x0010, WHEEL = 0x0800;

    public static void Handle(int pad, bool connected, ControllerSettings s, ushort buttons, short lx, short ly, short ry)
    {
        if (!connected) { last[pad] = 0; return; }
        ushort was = last[pad];
        last[pad] = buttons;
        Func<ushort, bool> pressed = b => (buttons & b) != 0 && (was & b) == 0;

        // The Guide has the focus while it's open: it takes the D-pad, A and B
        if (GuidePanel.IsOpen)
        {
            // D-pad or left stick; held, it repeats after a pause and speeds up the longer it's held
            double sx = lx / 32768.0, sy = ly / 32768.0;
            bool vertical = Math.Abs(sy) >= Math.Abs(sx);
            bool[] dir =
            {
                (buttons & Up) != 0 || (vertical && sy > 0.5),
                (buttons & Down) != 0 || (vertical && sy < -0.5),
                (buttons & Left) != 0 || (!vertical && sx < -0.5),
                (buttons & Right) != 0 || (!vertical && sx > 0.5),
            };
            for (int d = 0; d < 4; d++)
            {
                if (!dir[d]) { held[pad, d] = false; continue; }
                var now = DateTime.Now;
                bool fire = false;
                if (!held[pad, d]) { held[pad, d] = true; heldSince[pad, d] = now; fire = true; }
                else
                {
                    double heldMs = (now - heldSince[pad, d]).TotalMilliseconds;
                    double every = heldMs < 1500 ? 110 : heldMs < 3000 ? 70 : 45;
                    if (heldMs > 380 && (now - lastFire[pad, d]).TotalMilliseconds >= every) fire = true;
                }
                if (!fire) continue;
                lastFire[pad, d] = now;
                if (d == 0) GuidePanel.Move(-1);
                else if (d == 1) GuidePanel.Move(1);
                else GuidePanel.Adjust(d == 2 ? -1 : 1);
            }
            if (pressed(LB)) GuidePanel.Tab(-1);
            if (pressed(RB)) GuidePanel.Tab(1);
            if (pressed(A)) GuidePanel.Select();
            if (pressed(B)) GuidePanel.GoBack();
            return;
        }

        if (!mouseMode) return;
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
