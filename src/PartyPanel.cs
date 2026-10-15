// Party panel: an Xbox-guide-style Discord menu on the TV, driven by the controller.
//   Double-tap the Xbox button to open/close it (a single tap opens it while a call is ringing);
//   D-pad up/down moves, left/right changes volume, A selects, B goes back.
// Shows the current call, an incoming call (Answer / Decline), the Discord favourites from
// couchkit.json, Chats (recent DMs and group chats with their status, read live from Discord),
// Voice channels (server, then channel), Volume (game / Discord / everything), mute, deafen, leave
// and "Open Discord" (mouse mode).
// Also: an incoming/outgoing call banner and a corner HUD for call, microphone and message events,
// both shown without taking the focus from the game.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

static class PartyPanel
{
    // An entry runs an Action on the Discord thread (or directly, if Direct), opens a sub-menu whose
    // entries are loaded from Discord, or (volume) is changed with D-pad left/right.
    class Item
    {
        public string Text, Icon;
        public Func<string> Action;
        public bool CloseAfter, Direct;
        public Func<List<Item>> Submenu;
        public Color? Dot;
        public Func<int, int> Adjust;
        public int Level = -2;   // volume: -2 not a volume item, -1 nothing to control, else percent
    }
    class Level { public string Title; public List<Item> Items; public int Selected; }

    static Form panel, banner, hud;
    static Control ui;   // for marshalling onto the UI thread
    static List<Item> items = new List<Item>();
    static int selected, firstVisible;
    static string title = "Party";
    static readonly Stack<Level> parents = new Stack<Level>();   // empty = main menu
    static string message = "";
    static IntPtr returnTo;
    static DiscordSettings S = new DiscordSettings();
    public static volatile bool IsOpen;
    public static Action EnterMouseMode = () => { };

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();

    static readonly Color Back = Color.FromArgb(18, 14, 32), Card = Color.FromArgb(40, 30, 72), CardLight = Color.FromArgb(56, 42, 98),
                          Text = Color.FromArgb(235, 235, 245), Dim = Color.FromArgb(150, 150, 175),
                          Aqua = ColorTranslator.FromHtml("#00e1ff"), Magenta = ColorTranslator.FromHtml("#ff00d4"),
                          Red = Color.FromArgb(242, 63, 67), Green = Color.FromArgb(35, 165, 90);

    // Icons from Windows' own icon font (Segoe Fluent Icons on Windows 11, MDL2 on 10)
    static readonly Dictionary<string, string> Glyphs = new Dictionary<string, string>
    {
        { "call", "" }, { "hangup", "" }, { "chat", "" }, { "people", "" }, { "channel", "" },
        { "volume", "" }, { "mic", "" }, { "micoff", "" }, { "headset", "" }, { "deafened", "" },
        { "mouse", "" }, { "close", "" }, { "message", "" }, { "accept", "" }, { "decline", "" },
        { "power", "" }, { "game", "" }, { "server", "" }, { "star", "" },
    };
    static string iconFont = "Segoe MDL2 Assets";

    public static void Start(DiscordSettings settings)
    {
        S = settings;
        if (!S.Enabled) return;
        DiscordBridge.Notify = (icon, t, d) => OnUi(() => ShowHud(icon, t, d));
        var th = new Thread(() =>
        {
            using (var fonts = new InstalledFontCollection())
                if (fonts.Families.Any(f => f.Name == "Segoe Fluent Icons")) iconFont = "Segoe Fluent Icons";
            ui = new Control(); ui.CreateControl();
            panel = new Form { FormBorderStyle = FormBorderStyle.None, TopMost = true, ShowInTaskbar = false, BackColor = Back, StartPosition = FormStartPosition.Manual };
            panel.Paint += (s, e) => PaintPanel(e.Graphics, panel.ClientSize);
            banner = new NoActivateForm { FormBorderStyle = FormBorderStyle.None, TopMost = true, ShowInTaskbar = false, BackColor = Card, StartPosition = FormStartPosition.Manual };
            banner.Paint += (s, e) => PaintBanner(e.Graphics, banner.ClientSize);
            hud = new NoActivateForm { FormBorderStyle = FormBorderStyle.None, TopMost = true, ShowInTaskbar = false, BackColor = Card, StartPosition = FormStartPosition.Manual };
            hud.Paint += (s, e) => PaintHud(e.Graphics, hud.ClientSize);
            foreach (var f in new[] { panel, banner, hud })
                typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(f, true, null);
            var timer = new System.Windows.Forms.Timer { Interval = 500 };
            timer.Tick += (s, e) => Tick();
            timer.Start();
            Application.Run();
        }) { IsBackground = true, Name = "PartyPanel" };
        th.SetApartmentState(ApartmentState.STA);
        th.Start();
    }

    static void OnUi(Action a) { if (ui != null && ui.IsHandleCreated) ui.BeginInvoke(a); }

    // --- Controller input (called from the main loop) ---

    public static void Toggle() { if (IsOpen) Close(); else Open(); }

    public static void Open()
    {
        returnTo = GetForegroundWindow();
        message = "";
        selected = firstVisible = 0;
        parents.Clear();
        title = "Party";
        IsOpen = true;
        OnUi(() =>
        {
            hud.Hide();
            Rectangle tv = CouchKit.TvBounds();
            int width = tv.Width * 34 / 100;
            Rebuild();
            panel.Bounds = new Rectangle(tv.Left - width, tv.Top, width, tv.Height);
            panel.Show();
            CouchKit.BringToFront(panel.Handle);
            SlideIn(tv.Left);
        });
        // Fresh friend statuses for the favourites (reads Discord's DM list in the background)
        DiscordBridge.Do(() => { if (DiscordBridge.Running) DiscordBridge.ListChats(); OnUi(() => { if (IsOpen && parents.Count == 0) { Rebuild(); panel.Invalidate(); } }); return ""; }, null);
    }

    // Quick ease-out slide from the left edge
    static void SlideIn(int targetLeft)
    {
        int start = panel.Left, frame = 0;
        var anim = new System.Windows.Forms.Timer { Interval = 15 };
        anim.Tick += (s, e) =>
        {
            frame++;
            double t = Math.Min(1, frame / 10.0), ease = 1 - Math.Pow(1 - t, 3);
            panel.Left = (int)(start + (targetLeft - start) * ease);
            if (t >= 1) { anim.Stop(); anim.Dispose(); }
        };
        anim.Start();
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

    // B: back out of a sub-menu, or close from the main menu
    public static void GoBack()
    {
        OnUi(() =>
        {
            if (parents.Count == 0) { Close(); return; }
            var up = parents.Pop();
            title = up.Title; items = up.Items; selected = up.Selected; firstVisible = 0;
            message = "";
            if (parents.Count == 0) Rebuild();
            panel.Invalidate();
        });
    }

    static void OpenSubmenu(Item item)
    {
        parents.Push(new Level { Title = title, Items = items, Selected = selected });
        title = item.Text.TrimEnd(' ', '›');
        items = new List<Item> { new Item { Text = "Loading…" } };
        selected = firstVisible = 0;
        message = "";
        panel.Invalidate();
        var loader = item.Submenu;
        var level = items;
        DiscordBridge.Do(() =>
        {
            var loaded = loader();
            OnUi(() =>
            {
                if (items != level) return;   // backed out while loading
                items = loaded.Count > 0 ? loaded : new List<Item> { new Item { Text = "Nothing found" } };
                selected = 0;
                panel.Invalidate();
            });
            return "";
        }, null);
    }

    public static void Select()
    {
        OnUi(() =>
        {
            if (selected >= items.Count) return;
            var item = items[selected];
            if (item.Submenu != null) { OpenSubmenu(item); return; }
            if (item.Direct && item.Action != null) { item.Action(); return; }
            if (item.Action == null) { if (parents.Count == 0 && item.Icon == "close") Close(); return; }
            message = "…";
            panel.Invalidate();
            bool close = item.CloseAfter;
            DiscordBridge.Do(item.Action, msg => OnUi(() =>
            {
                message = msg != null ? msg.Replace("Discord: ", "") : "";
                if (parents.Count == 0) Rebuild();
                panel.Invalidate();
                if (close && msg != null && !msg.Contains("not found") && !msg.Contains("isn't") && !msg.Contains("couldn't")) Close();
            }));
        });
    }

    // D-pad left/right on a volume item
    public static void Adjust(int direction)
    {
        OnUi(() =>
        {
            if (selected >= items.Count || items[selected].Adjust == null) return;
            var item = items[selected];
            try { item.Level = item.Adjust(direction * 5); } catch (Exception ex) { CouchKit.Log("Volume change failed: " + ex.Message); }
            panel.Invalidate();
        });
    }

    // --- Content ---

    static void Rebuild()
    {
        var list = new List<Item>();
        if (!DiscordBridge.Running)
            list.Add(new Item { Text = "Start Discord", Icon = "power", Action = DiscordBridge.StartDiscord });
        if (DiscordBridge.Incoming)
        {
            list.Add(new Item { Text = "Answer", Icon = "accept", Action = DiscordBridge.Answer, CloseAfter = true });
            list.Add(new Item { Text = "Decline", Icon = "decline", Action = DiscordBridge.Decline });
        }
        if (DiscordBridge.InCall) list.Add(new Item { Text = "Leave call", Icon = "hangup", Action = DiscordBridge.Leave });
        list.Add(new Item { Text = DiscordBridge.Muted ? "Unmute microphone" : "Mute microphone", Icon = DiscordBridge.Muted ? "micoff" : "mic", Action = DiscordBridge.ToggleMute });
        foreach (var f in S.Favourites)
        {
            var fav = f;
            if (!string.IsNullOrEmpty(fav.Friend))
            {
                string status;
                lock (DiscordBridge.Statuses) DiscordBridge.Statuses.TryGetValue(fav.Friend, out status);
                list.Add(new Item { Text = "Call " + (fav.Label ?? fav.Friend), Icon = "call", Dot = StatusColour(status ?? ""), Action = () => DiscordBridge.CallFriend(fav.Friend), CloseAfter = true });
            }
            else if (!string.IsNullOrEmpty(fav.Server) && !string.IsNullOrEmpty(fav.Channel))
                list.Add(new Item { Text = "Join " + (fav.Label ?? fav.Channel), Icon = "channel", Action = () => DiscordBridge.JoinVoice(fav.Server, fav.Channel), CloseAfter = true });
        }
        list.Add(new Item { Text = "Chats ›", Icon = "chat", Submenu = ChatsMenu });
        list.Add(new Item { Text = "Voice channels ›", Icon = "server", Submenu = ServersMenu });
        list.Add(new Item { Text = "Volume ›", Icon = "volume", Submenu = VolumeMenu });
        list.Add(new Item { Text = DiscordBridge.Deafened ? "Undeafen" : "Deafen", Icon = DiscordBridge.Deafened ? "deafened" : "headset", Action = DiscordBridge.ToggleDeafen });
        list.Add(new Item { Text = "Open Discord (mouse mode)", Icon = "mouse", Direct = true, Action = () => { Close(); EnterMouseMode(); return ""; } });
        list.Add(new Item { Text = "Close", Icon = "close" });
        string current = selected < items.Count ? items[selected].Text : null;
        items = list;
        int keep = current == null ? -1 : items.FindIndex(i => i.Text == current);
        selected = DiscordBridge.Incoming && current == null ? 0 : keep >= 0 ? keep : Math.Min(selected, items.Count - 1);
    }

    // Sub-menus, loaded live from Discord on its thread
    static List<Item> ChatsMenu()
    {
        return DiscordBridge.ListChats().Select(c =>
        {
            string name = c.Name;
            string extra = c.Group ? "  ·  group" + (c.Members > 0 ? " of " + c.Members : "") : c.Status.Length > 0 ? "  ·  " + c.Status.ToLowerInvariant() : "";
            int fresh = DiscordBridge.Unread(name);
            if (fresh > 0) extra += "  ·  " + fresh + " new";
            return new Item { Text = name + extra, Icon = c.Group ? "people" : "call", Action = () => DiscordBridge.CallFriend(name), CloseAfter = true, Dot = StatusColour(c.Status) };
        }).ToList();
    }

    static List<Item> ServersMenu()
    {
        return DiscordBridge.ListServers().Select(s =>
        {
            string server = s;
            return new Item { Text = server + " ›", Icon = "server", Submenu = () => ChannelsMenu(server) };
        }).ToList();
    }

    static List<Item> ChannelsMenu(string server)
    {
        return DiscordBridge.ListVoiceChannels(server).Select(ch =>
        {
            string channel = ch;
            return new Item { Text = channel, Icon = "channel", Action = () => DiscordBridge.JoinVoice(server, channel), CloseAfter = true };
        }).ToList();
    }

    static List<Item> VolumeMenu()
    {
        var list = new List<Item>
        {
            new Item { Text = "Game", Icon = "game", Adjust = CouchKit.GameVolume },
            new Item { Text = "Discord", Icon = "chat", Adjust = CouchKit.DiscordVolume },
            new Item { Text = "Everything", Icon = "volume", Adjust = CouchKit.MasterVolume },
        };
        foreach (var i in list) { try { i.Level = i.Adjust(0); } catch { i.Level = -1; } }
        return list;
    }

    // Discord's own status colours
    static Color? StatusColour(string status)
    {
        switch (status)
        {
            case "Online": return Green;
            case "Streaming": return Color.FromArgb(89, 54, 149);
            case "Idle": return Color.FromArgb(240, 178, 50);
            case "Do Not Disturb": return Red;
            case "Offline": case "Invisible": return Color.FromArgb(128, 132, 142);
            default: return null;
        }
    }

    // --- Banner and HUD ---

    static bool lastIncoming, lastCalling;
    static void Tick()
    {
        if (IsOpen && parents.Count == 0) { Rebuild(); panel.Invalidate(); }
        bool incoming = DiscordBridge.Incoming;
        // "Calling …" shows for 8 seconds after starting a call, so there's visible feedback
        bool calling = DiscordBridge.CallingTo.Length > 0 && (DateTime.Now - DiscordBridge.CallingSince).TotalSeconds < 8;
        if (!calling && DiscordBridge.CallingTo.Length > 0) DiscordBridge.CallingTo = "";
        bool show = (incoming || calling) && !IsOpen;
        if (calling && !lastCalling) for (int p = 0; p < 4; p++) CouchKit.Rumble(p, 120);
        lastCalling = calling;
        if (show && !banner.Visible)
        {
            Rectangle tv = CouchKit.TvBounds();
            int w = tv.Width * 46 / 100, h = tv.Height * 12 / 100;
            banner.Bounds = new Rectangle(tv.Left + (tv.Width - w) / 2, tv.Top + tv.Height * 4 / 100, w, h);
            banner.Region = Rounded(new Rectangle(0, 0, w, h), h / 5);
            banner.Show();
        }
        else if (!show && banner.Visible) banner.Hide();
        if (banner.Visible) banner.Invalidate();
        if (incoming && !lastIncoming)
            ThreadPool.QueueUserWorkItem(_ => { for (int r = 0; r < 3; r++) { for (int p = 0; p < 4; p++) CouchKit.Rumble(p, 150); Thread.Sleep(350); } });
        lastIncoming = incoming;
    }

    static string hudIcon = "", hudTitle = "", hudDetail = "";
    static System.Windows.Forms.Timer hudTimer;

    // Corner HUD: a short message top-right for a few seconds, then fades
    static void ShowHud(string icon, string t, string detail)
    {
        if (IsOpen) return;   // the panel already shows it
        hudIcon = icon; hudTitle = t; hudDetail = detail ?? "";
        Rectangle tv = CouchKit.TvBounds();
        int w = tv.Width * 24 / 100, h = tv.Height * 8 / 100;
        hud.Bounds = new Rectangle(tv.Right - w - tv.Width * 2 / 100, tv.Top + tv.Height * 3 / 100, w, h);
        hud.Region = Rounded(new Rectangle(0, 0, w, h), h / 4);
        hud.Opacity = 0.97;
        hud.Show();
        hud.Invalidate();
        if (hudTimer != null) { hudTimer.Stop(); hudTimer.Dispose(); }
        int ticks = 0;
        hudTimer = new System.Windows.Forms.Timer { Interval = 50 };
        hudTimer.Tick += (s, e) =>
        {
            ticks++;
            if (ticks > 70) hud.Opacity = Math.Max(0, hud.Opacity - 0.1);   // hold ~3.5 s, then fade
            if (hud.Opacity <= 0.05) { hud.Hide(); hudTimer.Stop(); }
        };
        hudTimer.Start();
    }

    // --- Drawing ---

    static GraphicsPath RoundedPath(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.Left, r.Top, d, d, 180, 90); p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    static Region Rounded(Rectangle r, int radius) { using (var p = RoundedPath(r, radius)) return new Region(p); }

    static void Line(Graphics g, string s, Font font, Color colour, RectangleF r)
    {
        TextRenderer.DrawText(g, s, font, Rectangle.Round(r), colour,
            TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPrefix);
    }

    // A hint like Xbox's: a coloured round button badge followed by its label. Returns the next x.
    static float Hint(Graphics g, string button, Color colour, string label, Font font, float x, float y, float size)
    {
        using (var fill = new SolidBrush(Color.FromArgb(50, colour)))
        using (var ring = new Pen(colour, size / 12f))
        {
            var r = new RectangleF(x, y, size, size);
            g.FillEllipse(fill, r);
            g.DrawEllipse(ring, r);
        }
        bool glyph = Glyphs.ContainsKey(button);
        using (var bf = glyph ? new Font(iconFont, size * 0.5f, GraphicsUnit.Pixel) : new Font("Segoe UI Semibold", size * (button.Length > 1 ? 0.38f : 0.58f), GraphicsUnit.Pixel))
        {
            string s = glyph ? Glyphs[button] : button;
            var sz = g.MeasureString(s, bf);
            using (var b = new SolidBrush(colour)) g.DrawString(s, bf, b, x + (size - sz.Width) / 2 + size * 0.02f, y + (size - sz.Height) / 2);
        }
        float lx = x + size * 1.25f;
        Line(g, label, font, Dim, new RectangleF(lx, y + (size - font.Height) / 2, 1000, font.Height * 1.3f));
        return lx + TextRenderer.MeasureText(label, font).Width + size * 0.9f;
    }

    static void DrawIcon(Graphics g, string key, float x, float y, float size, Brush brush)
    {
        string glyph;
        if (key == null || !Glyphs.TryGetValue(key, out glyph)) return;
        using (var f = new Font(iconFont, size, GraphicsUnit.Pixel)) g.DrawString(glyph, f, brush, x, y);
    }

    static void PaintPanel(Graphics g, Size size)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        int unit = size.Height / 40;   // scales with the TV's resolution
        using (var accent = new LinearGradientBrush(new Point(0, 0), new Point(0, size.Height), Aqua, Magenta))
            g.FillRectangle(accent, size.Width - unit / 3, 0, unit / 3, size.Height);
        using (var titleFont = new Font("Segoe UI Semibold", unit * 1.6f, GraphicsUnit.Pixel))
        using (var body = new Font("Segoe UI", unit * 1.05f, GraphicsUnit.Pixel))
        using (var bold = new Font("Segoe UI Semibold", unit * 1.05f, GraphicsUnit.Pixel))
        using (var small = new Font("Segoe UI", unit * 0.75f, GraphicsUnit.Pixel))
        using (var text = new SolidBrush(Text))
        using (var dim = new SolidBrush(Dim))
        {
            var noWrap = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            int x = unit * 2, y = unit * 2, w = size.Width - x * 2;
            Line(g, title, titleFont, Text, new RectangleF(x, y, w, unit * 2.4f));
            y += unit * 3;

            // Status card: incoming call, current call, or not in a call
            if (parents.Count == 0)
            {
                var card = new RectangleF(x - unit / 2, y, w, unit * 3.2f);
                using (var path = RoundedPath(card, unit * 0.6f))
                {
                    using (var fill = new SolidBrush(DiscordBridge.Incoming ? CardLight : Card)) g.FillPath(fill, path);
                    if (DiscordBridge.Incoming || DiscordBridge.InCall)
                        using (var edge = new LinearGradientBrush(card, Aqua, Magenta, LinearGradientMode.Horizontal))
                        using (var pen = new Pen(edge, unit / 8f)) g.DrawPath(pen, path);
                }
                string icon = DiscordBridge.Incoming ? "call" : DiscordBridge.InCall ? "people" : "chat";
                string line1 = DiscordBridge.Incoming ? (DiscordBridge.IncomingFrom.Length > 0 ? DiscordBridge.IncomingFrom + " is calling" : "Incoming call")
                             : !DiscordBridge.Running ? "Discord isn't running"
                             : DiscordBridge.InCall ? "In a call" + (DiscordBridge.CallName.Length > 0 ? ": " + DiscordBridge.CallName : "")
                             : "Not in a call";
                string line2 = DiscordBridge.InCall ? (DiscordBridge.Muted ? "Microphone muted" : "Microphone live") + (DiscordBridge.Deafened ? "  ·  deafened" : "")
                             : DiscordBridge.Incoming ? "Press A to answer" : "Pick a friend or channel below";
                using (var iconBrush = new LinearGradientBrush(new RectangleF(card.Left, card.Top, unit * 3, unit * 3), Aqua, Magenta, 45f))
                    DrawIcon(g, icon, card.Left + unit * 0.6f, card.Top + unit * 0.7f, unit * 1.6f, iconBrush);
                Line(g, line1, bold, Text, new RectangleF(card.Left + unit * 3, card.Top + unit * 0.45f, card.Width - unit * 3.5f, bold.Height * 1.2f));
                Line(g, line2, small, DiscordBridge.InCall && DiscordBridge.Muted ? Red : Dim, new RectangleF(card.Left + unit * 3, card.Top + unit * 1.8f, card.Width - unit * 3.5f, small.Height * 1.2f));
                y += (int)(unit * 4);
            }

            int rowH = (int)(unit * 2.2), step = rowH + unit / 4;
            // Scroll so the selection stays visible; leave room for the message and button hints
            int visible = Math.Max(1, (size.Height - y - unit * 5) / step);
            if (selected < firstVisible) firstVisible = selected;
            if (selected >= firstVisible + visible) firstVisible = selected - visible + 1;
            if (firstVisible > 0) g.DrawString("▲", small, dim, size.Width - x * 2, y - unit * 1.2f);
            for (int i = firstVisible; i < items.Count && i < firstVisible + visible; i++)
            {
                var item = items[i];
                bool sel = i == selected;
                var r = new Rectangle(x - unit / 2, y, w, rowH);
                if (sel)
                {
                    using (var path = RoundedPath(r, unit * 0.5f))
                    using (var card = new SolidBrush(Card)) g.FillPath(card, path);
                    using (var edge = new LinearGradientBrush(r, Aqua, Magenta, LinearGradientMode.Vertical)) g.FillRectangle(edge, r.Left, r.Top + unit / 3, unit / 5, r.Height - unit * 2 / 3);
                }
                int textX = x + unit / 2;
                using (var iconBrush = new SolidBrush(sel ? Text : Dim))
                    DrawIcon(g, item.Icon, textX, y + (rowH - unit * 1.1f) / 2, unit * 1.1f, item.Icon == "decline" || item.Icon == "hangup" ? new SolidBrush(Red) : item.Icon == "accept" ? new SolidBrush(Green) : (Brush)iconBrush);
                if (item.Icon != null) textX += (int)(unit * 1.9f);
                if (item.Dot.HasValue)
                {
                    int d = (int)(unit * 0.6);
                    using (var dot = new SolidBrush(item.Dot.Value)) g.FillEllipse(dot, textX, y + (rowH - d) / 2, d, d);
                    textX += d + unit / 2;
                }
                int textRight = r.Right - unit / 2;
                if (item.Level >= -1)
                {
                    // Volume bar on the right: palette gradient for the level, or "not playing"
                    int barW = r.Width * 40 / 100, barH = unit / 2, barX = r.Right - barW - unit, barY = y + (rowH - barH) / 2;
                    textRight = barX - unit / 2;
                    if (item.Level < 0) g.DrawString("not playing", small, dim, barX, y + (rowH - small.Height) / 2);
                    else
                    {
                        using (var track = new SolidBrush(Color.FromArgb(60, 255, 255, 255))) g.FillRectangle(track, barX, barY, barW, barH);
                        int fill = barW * item.Level / 100;
                        if (fill > 0) using (var lvl = new LinearGradientBrush(new Rectangle(barX, barY, barW, barH), Aqua, Magenta, LinearGradientMode.Horizontal)) g.FillRectangle(lvl, barX, barY, fill, barH);
                        g.DrawString(item.Level + "%", small, sel ? text : dim, barX - unit * 2.6f, y + (rowH - small.Height) / 2);
                        textRight = (int)(barX - unit * 2.8f);
                    }
                }
                Line(g, item.Text, body, sel ? Text : Dim, new RectangleF(textX, y + (rowH - body.Height) / 2, textRight - textX, body.Height * 1.3f));
                y += step;
            }
            if (firstVisible + visible < items.Count) g.DrawString("▼", small, dim, size.Width - x * 2, y);
            if (message.Length > 0) Line(g, message, small, Dim, new RectangleF(x, size.Height - unit * 4.2f, w, unit * 1.4f));
            bool adjustable = selected < items.Count && items[selected].Adjust != null;
            float hy = size.Height - unit * 2.9f, hs = unit * 1.3f, hx = x;
            if (adjustable) hx = Hint(g, "◀▶", Text, "volume", small, hx, hy, hs);
            else hx = Hint(g, "A", Green, "select", small, hx, hy, hs);
            hx = Hint(g, "B", Red, "back", small, hx, hy, hs);
            Hint(g, "game", Aqua, "Xbox twice: close", small, hx, hy, hs);
        }
    }

    static void PaintBanner(Graphics g, Size size)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        int unit = size.Height / 5;
        using (var accent = new LinearGradientBrush(new Point(0, 0), new Point(size.Width, 0), Aqua, Magenta))
            g.FillRectangle(accent, 0, size.Height - unit / 3, size.Width, unit / 3);
        using (var big = new Font("Segoe UI Semibold", unit * 1.35f, GraphicsUnit.Pixel))
        using (var small = new Font("Segoe UI", unit * 0.8f, GraphicsUnit.Pixel))
        using (var text = new SolidBrush(Text))
        using (var dim = new SolidBrush(Dim))
        using (var iconBrush = new LinearGradientBrush(new Rectangle(0, 0, unit * 4, unit * 4), Aqua, Magenta, 45f))
        {
            DrawIcon(g, "call", unit * 0.9f, unit * 1.1f, unit * 2.2f, iconBrush);
            float tx = unit * 4;
            if (DiscordBridge.Incoming)
            {
                string who = DiscordBridge.IncomingFrom.Length > 0 ? DiscordBridge.IncomingFrom + " is calling" : "Incoming Discord call";
                g.DrawString(who, big, text, tx, unit * 0.7f);
                g.DrawString("Press the Xbox button, then A to answer", small, dim, tx, unit * 2.6f);
            }
            else
            {
                g.DrawString("Calling " + DiscordBridge.CallingTo + "…", big, text, tx, unit * 0.7f);
                g.DrawString("Waiting for them to join  ·  Xbox ×2 for call options", small, dim, tx, unit * 2.6f);
            }
        }
    }

    static void PaintHud(Graphics g, Size size)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        int unit = size.Height / 4;
        using (var accent = new LinearGradientBrush(new Point(0, 0), new Point(0, size.Height), Aqua, Magenta))
            g.FillRectangle(accent, 0, 0, unit / 3, size.Height);
        using (var big = new Font("Segoe UI Semibold", unit * 1.05f, GraphicsUnit.Pixel))
        using (var small = new Font("Segoe UI", unit * 0.75f, GraphicsUnit.Pixel))
        using (var text = new SolidBrush(Text))
        using (var dim = new SolidBrush(Dim))
        {
            var noWrap = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            Brush iconBrush = hudIcon == "micoff" || hudIcon == "hangup" || hudIcon == "deafened" ? (Brush)new SolidBrush(Red) : new LinearGradientBrush(new Rectangle(0, 0, unit * 3, unit * 3), Aqua, Magenta, 45f);
            DrawIcon(g, hudIcon, unit * 0.9f, unit * 0.9f, unit * 1.9f, iconBrush);
            iconBrush.Dispose();
            float tx = unit * 3.4f, tw = size.Width - tx - unit * 0.6f;
            bool two = hudDetail.Length > 0;
            Line(g, hudTitle, big, Text, new RectangleF(tx, two ? unit * 0.55f : unit * 1.25f, tw, big.Height * 1.2f));
            if (two) Line(g, hudDetail, small, Dim, new RectangleF(tx, unit * 2.2f, tw, small.Height * 1.2f));
        }
    }

    // For checking the design: CouchKit.exe --preview <folder> draws the panel, banner and HUD at 4K
    // with made-up call details into PNGs.
    public static void Preview(DiscordSettings settings, string folder)
    {
        S = settings;
        using (var fonts = new InstalledFontCollection())
            if (fonts.Families.Any(f => f.Name == "Segoe Fluent Icons")) iconFont = "Segoe Fluent Icons";
        DiscordBridge.Running = true; DiscordBridge.InCall = true; DiscordBridge.CallName = "General / Charlton Home";
        lock (DiscordBridge.Statuses) foreach (var f in S.Favourites) if (!string.IsNullOrEmpty(f.Friend)) DiscordBridge.Statuses[f.Friend] = "Online";
        Rebuild();
        selected = 1;
        Action<string, int, int, Action<Graphics, Size>> render = (name, w, h, paint) =>
        {
            using (var bmp = new Bitmap(w, h))
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Back);
                paint(g, new Size(w, h));
                bmp.Save(System.IO.Path.Combine(folder, name), System.Drawing.Imaging.ImageFormat.Png);
            }
        };
        render("panel.png", 1305, 2160, PaintPanel);
        hudIcon = "micoff"; hudTitle = "Microphone muted"; hudDetail = "";
        render("hud-muted.png", 921, 172, PaintHud);
        hudIcon = "message"; hudTitle = "Matt"; hudDetail = "2 new messages";
        render("hud-message.png", 921, 172, PaintHud);
        DiscordBridge.InCall = false; DiscordBridge.Incoming = true; DiscordBridge.IncomingFrom = "Matt";
        render("banner.png", 1766, 259, PaintBanner);
        items.Clear(); Rebuild(); selected = 0;
        render("panel-incoming.png", 1305, 2160, PaintPanel);
    }

    // For showing how it looks: CouchKit.exe --demo <folder> <background image> renders a scripted
    // 1920x1080 30 fps sequence (HUD, panel, volume, incoming call) as numbered PNG frames, using
    // the real drawing code over a game's artwork. Encode with ffmpeg afterwards.
    public static void Demo(DiscordSettings settings, string folder, string background)
    {
        S = settings;
        using (var fonts = new InstalledFontCollection())
            if (fonts.Families.Any(f => f.Name == "Segoe Fluent Icons")) iconFont = "Segoe Fluent Icons";
        const int W = 1920, H = 1080, Fps = 30;
        int panelW = W * 34 / 100, hudW = W * 24 / 100, hudH = H * 8 / 100, banW = W * 46 / 100, banH = H * 12 / 100;
        lock (DiscordBridge.Statuses) foreach (var f in S.Favourites) if (!string.IsNullOrEmpty(f.Friend)) DiscordBridge.Statuses[f.Friend] = "Online";

        Bitmap bg = new Bitmap(W, H);
        using (var src = Image.FromFile(background))
        using (var g = Graphics.FromImage(bg))
        {
            float scale = Math.Max((float)W / src.Width, (float)H / src.Height);
            float sw = src.Width * scale, sh = src.Height * scale;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(src, (W - sw) / 2, (H - sh) / 2, sw, sh);
        }

        Func<double, double> ease = t => 1 - Math.Pow(1 - Math.Max(0, Math.Min(1, t)), 3);
        // Fade in over 0.25 s, hold, fade out over the last 0.5 s
        Func<double, double, double, double> window = (t, start, end) =>
            t < start || t > end ? 0 : Math.Min(Math.Min(1, (t - start) / 0.25), Math.Min(1, (end - t) / 0.5));

        var volume = new List<Item>
        {
            new Item { Text = "Game", Icon = "game", Level = 80, Adjust = d => 0 },
            new Item { Text = "Discord", Icon = "chat", Level = 100, Adjust = d => 0 },
            new Item { Text = "Everything", Icon = "volume", Level = 60, Adjust = d => 0 },
        };

        int frames = (int)(21.5 * Fps);
        for (int n = 0; n < frames; n++)
        {
            double t = (double)n / Fps;
            // --- Script ---
            DiscordBridge.Running = true;
            DiscordBridge.Incoming = t >= 10.8 && t < 14.6;
            DiscordBridge.IncomingFrom = "Matt";
            DiscordBridge.InCall = t < 10.6 || t >= 14.6;
            DiscordBridge.CallName = t < 10.6 ? "General / Charlton Home" : "Matt";
            DiscordBridge.Muted = t >= 17.6;
            double panelX = -1;   // fraction shown: 0 hidden .. 1 fully in
            bool inVolume = false;
            if (t >= 4.4 && t < 10.4) panelX = t < 4.7 ? ease((t - 4.4) / 0.3) : t > 10.1 ? 1 - ease((t - 10.1) / 0.3) : 1;
            if (t >= 13.4 && t < 14.9) panelX = t < 13.7 ? ease((t - 13.4) / 0.3) : t > 14.6 ? 1 - ease((t - 14.6) / 0.3) : 1;
            if (panelX >= 0)
            {
                items.Clear(); parents.Clear(); title = "Party"; selected = 0; Rebuild();
                if (t < 10.4)
                {
                    int volumeRow = items.FindIndex(i => i.Text.StartsWith("Volume"));
                    selected = Math.Min(volumeRow, 1 + (int)Math.Max(0, (t - 5.0) / 0.35));
                    if (t >= 7.5 && t < 9.9)
                    {
                        inVolume = true;
                        parents.Push(new Level { Title = "Party", Items = items, Selected = selected });
                        title = "Volume"; items = volume; selected = 0;
                        volume[0].Level = t < 8.0 ? 80 : Math.Max(45, 80 - 5 * (int)((t - 8.0) / 0.2));
                    }
                }
                else selected = 0;   // incoming: Answer
            }
            hudIcon = t < 4.2 ? "people" : t < 17.5 ? "people" : "micoff";
            hudTitle = t < 4.2 ? "Connected" : t < 17.5 ? "Connected" : "Microphone muted";
            hudDetail = t < 4.2 ? "General / Charlton Home" : t < 17.5 ? "Matt" : "";
            double hudAlpha = Math.Max(window(t, 1.0, 4.2), Math.Max(window(t, 14.9, 17.4), window(t, 17.6, 20.6)));
            double banAlpha = DiscordBridge.Incoming && panelX < 0 ? window(t, 10.8, 13.6) : 0;

            // --- Compose ---
            using (var frame = new Bitmap(W, H))
            using (var g = Graphics.FromImage(frame))
            {
                g.DrawImage(bg, 0, 0);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                if (banAlpha > 0) Composite(g, new Rectangle((W - banW) / 2, H * 4 / 100, banW, banH), banH / 5, banAlpha, PaintBanner, Card);
                if (hudAlpha > 0) Composite(g, new Rectangle(W - hudW - W * 2 / 100, H * 3 / 100, hudW, hudH), hudH / 4, hudAlpha, PaintHud, Card);
                if (panelX >= 0)
                {
                    using (var shade = new SolidBrush(Color.FromArgb((int)(90 * panelX), 0, 0, 0))) g.FillRectangle(shade, 0, 0, W, H);
                    Composite(g, new Rectangle((int)(-panelW + panelW * panelX), 0, panelW, H), 0, 1, PaintPanel, Back);
                }
                frame.Save(System.IO.Path.Combine(folder, string.Format("f{0:D4}.png", n)), System.Drawing.Imaging.ImageFormat.Png);
            }
            if (inVolume) { items = parents.Pop().Items; }
        }
        bg.Dispose();
    }

    // Draws one of the windows (panel, banner, HUD) onto a frame: rounded, at an opacity
    static void Composite(Graphics g, Rectangle r, int radius, double alpha, Action<Graphics, Size> paint, Color backColour)
    {
        using (var layer = new Bitmap(r.Width, r.Height))
        {
            using (var lg = Graphics.FromImage(layer)) { lg.Clear(backColour); paint(lg, r.Size); }
            var matrix = new System.Drawing.Imaging.ColorMatrix { Matrix33 = (float)alpha };
            using (var attrs = new System.Drawing.Imaging.ImageAttributes())
            {
                attrs.SetColorMatrix(matrix);
                var state = g.Save();
                if (radius > 0) using (var path = RoundedPath(r, radius)) g.SetClip(path);
                g.DrawImage(layer, r, 0, 0, r.Width, r.Height, GraphicsUnit.Pixel, attrs);
                g.Restore(state);
            }
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
