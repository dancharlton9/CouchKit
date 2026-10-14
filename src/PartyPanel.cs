// Party panel: an Xbox-guide-style Discord menu on the TV, driven by the controller.
//   Back + Y opens/closes it; D-pad up/down moves, A selects, B closes.
// Lists the Discord favourites from couchkit.json (call a friend / join a voice channel), plus
// mute, deafen, leave and "Open Discord" (mouse mode). Also shows an incoming-call banner, without
// taking the focus, that says to hold Back + A to answer.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

static class PartyPanel
{
    class Item { public string Text; public Func<string> Action; public bool CloseAfter; }

    static Form panel, banner;
    static Control ui;   // for marshalling onto the UI thread
    static List<Item> items = new List<Item>();
    static int selected;
    static string message = "";
    static IntPtr returnTo;
    static DiscordSettings S = new DiscordSettings();
    public static volatile bool IsOpen;
    public static Action EnterMouseMode = () => { };

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();

    static readonly Color Back = Color.FromArgb(18, 14, 32), Card = Color.FromArgb(40, 30, 72), Text = Color.FromArgb(235, 235, 245),
                          Dim = Color.FromArgb(150, 150, 175), Aqua = ColorTranslator.FromHtml("#00e1ff"), Magenta = ColorTranslator.FromHtml("#ff00d4");

    public static void Start(DiscordSettings settings)
    {
        S = settings;
        if (!S.Enabled) return;
        var t = new Thread(() =>
        {
            ui = new Control(); ui.CreateControl();
            panel = new Form { FormBorderStyle = FormBorderStyle.None, TopMost = true, ShowInTaskbar = false, BackColor = Back, StartPosition = FormStartPosition.Manual };
            panel.Paint += (s, e) => PaintPanel(e.Graphics, panel.ClientSize);
            typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(panel, true, null);
            banner = new NoActivateForm { FormBorderStyle = FormBorderStyle.None, TopMost = true, ShowInTaskbar = false, BackColor = Card, StartPosition = FormStartPosition.Manual };
            banner.Paint += (s, e) => PaintBanner(e.Graphics, banner.ClientSize);
            var timer = new System.Windows.Forms.Timer { Interval = 500 };
            timer.Tick += (s, e) => Tick();
            timer.Start();
            Application.Run();
        }) { IsBackground = true, Name = "PartyPanel" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    static void OnUi(Action a) { if (ui != null && ui.IsHandleCreated) ui.BeginInvoke(a); }

    // --- Controller input (called from the main loop) ---

    public static void Toggle() { if (IsOpen) Close(); else Open(); }

    public static void Open()
    {
        returnTo = GetForegroundWindow();
        message = "";
        selected = 0;
        IsOpen = true;
        OnUi(() =>
        {
            Rectangle tv = CouchKit.TvBounds();
            panel.Bounds = new Rectangle(tv.Left, tv.Top, tv.Width * 30 / 100, tv.Height);
            Rebuild();
            panel.Show();
            panel.Invalidate();
            CouchKit.BringToFront(panel.Handle);
        });
    }

    public static void Close()
    {
        IsOpen = false;
        OnUi(() => { panel.Hide(); if (returnTo != IntPtr.Zero) CouchKit.BringToFront(returnTo); });
    }

    public static void Move(int delta)
    {
        OnUi(() => { if (items.Count == 0) return; selected = (selected + delta + items.Count) % items.Count; panel.Invalidate(); });
    }

    public static void Select()
    {
        OnUi(() =>
        {
            if (selected >= items.Count) return;
            var item = items[selected];
            if (item.Action == null) { Close(); return; }
            message = "…";
            panel.Invalidate();
            bool close = item.CloseAfter;
            DiscordBridge.Do(item.Action, msg => OnUi(() =>
            {
                message = msg != null ? msg.Replace("Discord: ", "") : "";
                Rebuild();
                panel.Invalidate();
                if (close && msg != null && !msg.Contains("not found") && !msg.Contains("isn't") && !msg.Contains("couldn't")) Close();
            }));
        });
    }

    // --- Content ---

    static void Rebuild()
    {
        var list = new List<Item>();
        foreach (var f in S.Favourites)
        {
            var fav = f;
            if (!string.IsNullOrEmpty(fav.Friend))
                list.Add(new Item { Text = "Call " + (fav.Label ?? fav.Friend), Action = () => DiscordBridge.CallFriend(fav.Friend), CloseAfter = true });
            else if (!string.IsNullOrEmpty(fav.Server) && !string.IsNullOrEmpty(fav.Channel))
                list.Add(new Item { Text = "Join " + (fav.Label ?? fav.Channel), Action = () => DiscordBridge.JoinVoice(fav.Server, fav.Channel), CloseAfter = true });
        }
        if (DiscordBridge.Incoming) list.Insert(0, new Item { Text = "Answer call" + (DiscordBridge.IncomingFrom.Length > 0 ? " from " + DiscordBridge.IncomingFrom : ""), Action = DiscordBridge.Answer, CloseAfter = true });
        list.Add(new Item { Text = DiscordBridge.Muted ? "Unmute microphone" : "Mute microphone", Action = DiscordBridge.ToggleMute });
        list.Add(new Item { Text = DiscordBridge.Deafened ? "Undeafen" : "Deafen", Action = DiscordBridge.ToggleDeafen });
        if (DiscordBridge.InCall) list.Add(new Item { Text = "Leave call", Action = DiscordBridge.Leave });
        list.Add(new Item { Text = "Open Discord (mouse mode)", Action = () => { OnUi(() => { Close(); EnterMouseMode(); }); return ""; } });
        list.Add(new Item { Text = "Close", Action = null });
        string current = selected < items.Count ? items[selected].Text : null;
        items = list;
        int keep = current == null ? -1 : items.FindIndex(i => i.Text == current);
        selected = keep >= 0 ? keep : Math.Min(selected, items.Count - 1);
    }

    static bool lastIncoming;
    static void Tick()
    {
        if (IsOpen) { Rebuild(); panel.Invalidate(); }
        bool incoming = DiscordBridge.Incoming;
        if (incoming && !banner.Visible)
        {
            Rectangle tv = CouchKit.TvBounds();
            int w = tv.Width * 42 / 100, h = tv.Height * 9 / 100;
            banner.Bounds = new Rectangle(tv.Left + (tv.Width - w) / 2, tv.Top + tv.Height * 4 / 100, w, h);
            banner.Show();
            banner.Invalidate();
        }
        else if (!incoming && banner.Visible) banner.Hide();
        if (incoming && !lastIncoming)
            ThreadPool.QueueUserWorkItem(_ => { for (int r = 0; r < 3; r++) { for (int p = 0; p < 4; p++) CouchKit.Rumble(p, 150); Thread.Sleep(350); } });
        lastIncoming = incoming;
    }

    // --- Drawing ---

    static void PaintPanel(Graphics g, Size size)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        int unit = size.Height / 40;   // scales with the TV's resolution
        using (var accent = new LinearGradientBrush(new Point(0, 0), new Point(0, size.Height), Aqua, Magenta))
            g.FillRectangle(accent, size.Width - unit / 3, 0, unit / 3, size.Height);
        using (var title = new Font("Segoe UI Semibold", unit * 1.6f, GraphicsUnit.Pixel))
        using (var body = new Font("Segoe UI", unit * 1.05f, GraphicsUnit.Pixel))
        using (var small = new Font("Segoe UI", unit * 0.75f, GraphicsUnit.Pixel))
        using (var text = new SolidBrush(Text))
        using (var dim = new SolidBrush(Dim))
        {
            int x = unit * 2, y = unit * 2;
            g.DrawString("Party", title, text, x, y);
            y += unit * 3;
            string status = DiscordBridge.Incoming ? "Incoming call" + (DiscordBridge.IncomingFrom.Length > 0 ? " from " + DiscordBridge.IncomingFrom : "")
                          : !DiscordBridge.Running ? "Discord isn't running"
                          : DiscordBridge.InCall ? "In a call" + (DiscordBridge.Muted ? " · mic muted" : " · mic live") + (DiscordBridge.Deafened ? " · deafened" : "")
                          : "Not in a call";
            g.DrawString(status, small, dim, x, y);
            y += unit * 2;
            int rowH = (int)(unit * 2.2);
            for (int i = 0; i < items.Count; i++)
            {
                var r = new Rectangle(x - unit / 2, y, size.Width - x * 2, rowH);
                if (i == selected)
                {
                    using (var card = new SolidBrush(Card)) g.FillRectangle(card, r);
                    using (var edge = new LinearGradientBrush(r, Aqua, Magenta, LinearGradientMode.Vertical)) g.FillRectangle(edge, r.Left, r.Top, unit / 4, r.Height);
                }
                g.DrawString(items[i].Text, body, i == selected ? text : dim, x + unit / 2, y + (rowH - body.Height) / 2);
                y += rowH + unit / 4;
            }
            if (message.Length > 0) g.DrawString(message, small, dim, new RectangleF(x, size.Height - unit * 6, size.Width - x * 2, unit * 3));
            g.DrawString("A  select     B  close     D-pad  move", small, dim, x, size.Height - unit * 2.5f);
        }
    }

    static void PaintBanner(Graphics g, Size size)
    {
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        int unit = size.Height / 4;
        using (var accent = new LinearGradientBrush(new Point(0, 0), new Point(size.Width, 0), Aqua, Magenta))
            g.FillRectangle(accent, 0, size.Height - unit / 4, size.Width, unit / 4);
        using (var big = new Font("Segoe UI Semibold", unit * 1.1f, GraphicsUnit.Pixel))
        using (var small = new Font("Segoe UI", unit * 0.75f, GraphicsUnit.Pixel))
        using (var text = new SolidBrush(Text))
        using (var dim = new SolidBrush(Dim))
        {
            string who = DiscordBridge.IncomingFrom.Length > 0 ? DiscordBridge.IncomingFrom + " is calling" : "Incoming Discord call";
            g.DrawString(who, big, text, unit, unit * 0.4f);
            g.DrawString("Hold Back + A to answer  ·  Back + B to decline", small, dim, unit, unit * 1.9f);
        }
    }

    // Shown on top without taking the focus from the game
    class NoActivateForm : Form
    {
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get { var p = base.CreateParams; p.ExStyle |= 0x08000000 /* WS_EX_NOACTIVATE */ | 0x00000080 /* WS_EX_TOOLWINDOW */ | 0x00000008 /* WS_EX_TOPMOST */; return p; }
        }
    }
}
