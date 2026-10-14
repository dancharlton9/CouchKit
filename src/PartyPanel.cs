// Party panel: an Xbox-guide-style Discord menu on the TV, driven by the controller.
//   Double-tap the Xbox button to open/close it; D-pad up/down moves, A selects, B goes back.
// Lists the Discord favourites from couchkit.json, then Chats (recent DMs and group chats, read
// live from Discord) and Voice channels (server, then channel), plus
// mute, deafen, leave and "Open Discord" (mouse mode). Also shows an incoming-call banner, without
// taking the focus, that says to double-tap the Xbox button to answer.
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
    // An entry runs an Action on the Discord thread, or opens a sub-menu whose entries are loaded
    // from Discord (chats, servers, voice channels).
    class Item { public string Text; public Func<string> Action; public bool CloseAfter; public Func<List<Item>> Submenu; public Color? Dot;
                 public Func<int, int> Adjust; public int Level = -2;
                 public bool Direct; }   // Direct: run on the panel's thread instead of the Discord thread   // volume items: D-pad left/right; Level -1 = nothing to control
    class Level { public string Title; public List<Item> Items; public int Selected; }

    static Form panel, banner;
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
        selected = firstVisible = 0;
        parents.Clear();
        title = "Party";
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
            if (item.Action == null) { if (parents.Count == 0) Close(); return; }
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
        list.Add(new Item { Text = "Chats ›", Submenu = ChatsMenu });
        list.Add(new Item { Text = "Voice channels ›", Submenu = ServersMenu });
        list.Add(new Item { Text = "Volume ›", Submenu = VolumeMenu });
        list.Add(new Item { Text = DiscordBridge.Muted ? "Unmute microphone" : "Mute microphone", Action = DiscordBridge.ToggleMute });
        list.Add(new Item { Text = DiscordBridge.Deafened ? "Undeafen" : "Deafen", Action = DiscordBridge.ToggleDeafen });
        if (DiscordBridge.InCall) list.Add(new Item { Text = "Leave call", Action = DiscordBridge.Leave });
        list.Add(new Item { Text = "Open Discord (mouse mode)", Direct = true, Action = () => { Close(); EnterMouseMode(); return ""; } });
        list.Add(new Item { Text = "Close", Action = null });
        string current = selected < items.Count ? items[selected].Text : null;
        items = list;
        int keep = current == null ? -1 : items.FindIndex(i => i.Text == current);
        selected = keep >= 0 ? keep : Math.Min(selected, items.Count - 1);
    }

    // Sub-menus, loaded live from Discord on its thread
    static List<Item> ChatsMenu()
    {
        return DiscordBridge.ListChats().Select(c =>
        {
            string name = c.Name;
string status = c.Status.Length > 0 ? "  ·  " + c.Status.ToLowerInvariant() : "";
            return new Item { Text = "Call " + name + (c.Group ? "  (group" + (c.Members > 0 ? ", " + c.Members : "") + ")" : status), Action = () => DiscordBridge.CallFriend(name), CloseAfter = true, Dot = StatusColour(c.Status) };
        }).ToList();
    }

    static List<Item> VolumeMenu()
    {
        var list = new List<Item>
        {
            new Item { Text = "Game", Adjust = CouchKit.GameVolume },
            new Item { Text = "Discord", Adjust = CouchKit.DiscordVolume },
            new Item { Text = "Everything", Adjust = CouchKit.MasterVolume },
        };
        foreach (var i in list) { try { i.Level = i.Adjust(0); } catch { i.Level = -1; } }
        return list;
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

    // Discord's own status colours
    static Color? StatusColour(string status)
    {
        switch (status)
        {
            case "Online": return Color.FromArgb(35, 165, 90);
            case "Streaming": return Color.FromArgb(89, 54, 149);
            case "Idle": return Color.FromArgb(240, 178, 50);
            case "Do Not Disturb": return Color.FromArgb(242, 63, 67);
            case "Offline": case "Invisible": return Color.FromArgb(128, 132, 142);
            default: return null;
        }
    }

    static List<Item> ServersMenu()
    {
        return DiscordBridge.ListServers().Select(s =>
        {
            string server = s;
            return new Item { Text = server + " ›", Submenu = () => ChannelsMenu(server) };
        }).ToList();
    }

    static List<Item> ChannelsMenu(string server)
    {
        return DiscordBridge.ListVoiceChannels(server).Select(ch =>
        {
            string channel = ch;
            return new Item { Text = "Join " + channel, Action = () => DiscordBridge.JoinVoice(server, channel), CloseAfter = true };
        }).ToList();
    }

    static bool lastIncoming, lastCalling;
    static void Tick()
    {
        if (IsOpen && parents.Count == 0) { Rebuild(); panel.Invalidate(); }
        bool incoming = DiscordBridge.Incoming;
        // "Calling …" shows for 8 seconds after starting a call, so there's visible feedback
        bool calling = DiscordBridge.CallingTo.Length > 0 && (DateTime.Now - DiscordBridge.CallingSince).TotalSeconds < 8;
        if (!calling && DiscordBridge.CallingTo.Length > 0) DiscordBridge.CallingTo = "";
        bool show = incoming || calling;
        if (calling && !lastCalling) for (int p = 0; p < 4; p++) CouchKit.Rumble(p, 120);
        lastCalling = calling;
        if (show && !banner.Visible)
        {
            Rectangle tv = CouchKit.TvBounds();
            int w = tv.Width * 42 / 100, h = tv.Height * 9 / 100;
            banner.Bounds = new Rectangle(tv.Left + (tv.Width - w) / 2, tv.Top + tv.Height * 4 / 100, w, h);
            banner.Show();
            banner.Invalidate();
        }
        else if (!show && banner.Visible) banner.Hide();
        if (banner.Visible) banner.Invalidate();
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
        using (var titleFont = new Font("Segoe UI Semibold", unit * 1.6f, GraphicsUnit.Pixel))
        using (var body = new Font("Segoe UI", unit * 1.05f, GraphicsUnit.Pixel))
        using (var small = new Font("Segoe UI", unit * 0.75f, GraphicsUnit.Pixel))
        using (var text = new SolidBrush(Text))
        using (var dim = new SolidBrush(Dim))
        {
            int x = unit * 2, y = unit * 2;
            var titleBox = new RectangleF(x, y, size.Width - x * 2, unit * 2.4f);
            g.DrawString(title, titleFont, text, titleBox, new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap });
            y += unit * 3;
            string status = DiscordBridge.Incoming ? "Incoming call" + (DiscordBridge.IncomingFrom.Length > 0 ? " from " + DiscordBridge.IncomingFrom : "")
                          : !DiscordBridge.Running ? "Discord isn't running"
                          : DiscordBridge.InCall ? "In a call" + (DiscordBridge.Muted ? " · mic muted" : " · mic live") + (DiscordBridge.Deafened ? " · deafened" : "")
                          : "Not in a call";
            g.DrawString(status, small, dim, x, y);
            y += unit * 2;
            int rowH = (int)(unit * 2.2), step = rowH + unit / 4;
            // Scroll so the selection stays visible; leave room for the message and button hints
            int visible = Math.Max(1, (size.Height - y - unit * 7) / step);
            if (selected < firstVisible) firstVisible = selected;
            if (selected >= firstVisible + visible) firstVisible = selected - visible + 1;
            if (firstVisible > 0) g.DrawString("▲", small, dim, size.Width - x * 2, y - unit * 1.5f);
            var noWrap = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            for (int i = firstVisible; i < items.Count && i < firstVisible + visible; i++)
            {
                var r = new Rectangle(x - unit / 2, y, size.Width - x * 2, rowH);
                if (i == selected)
                {
                    using (var card = new SolidBrush(Card)) g.FillRectangle(card, r);
                    using (var edge = new LinearGradientBrush(r, Aqua, Magenta, LinearGradientMode.Vertical)) g.FillRectangle(edge, r.Left, r.Top, unit / 4, r.Height);
                }
                int textX = x + unit / 2;
                if (items[i].Dot.HasValue)
                {
                    int d = (int)(unit * 0.7);
                    using (var dot = new SolidBrush(items[i].Dot.Value)) g.FillEllipse(dot, textX, y + (rowH - d) / 2, d, d);
                    textX += d + unit / 2;
                }
                int textRight = r.Right - unit / 2;
                if (items[i].Level >= -1)
                {
                    // Volume bar on the right: palette gradient for the level, or "not playing"
                    int barW = r.Width * 45 / 100, barH = unit / 2, barX = r.Right - barW - unit, barY = y + (rowH - barH) / 2;
                    textRight = barX - unit / 2;
                    if (items[i].Level < 0) g.DrawString("not playing", small, dim, barX, y + (rowH - small.Height) / 2);
                    else
                    {
                        using (var track = new SolidBrush(Color.FromArgb(60, 255, 255, 255))) g.FillRectangle(track, barX, barY, barW, barH);
                        int fill = barW * items[i].Level / 100;
                        if (fill > 0) using (var lvl = new LinearGradientBrush(new Rectangle(barX, barY, barW, barH), Aqua, Magenta, LinearGradientMode.Horizontal)) g.FillRectangle(lvl, barX, barY, fill, barH);
                        g.DrawString(items[i].Level + "%", small, i == selected ? text : dim, barX - unit * 2.6f, y + (rowH - small.Height) / 2);
                        textRight = (int)(barX - unit * 2.8f);
                    }
                }
                g.DrawString(items[i].Text, body, i == selected ? text : dim, new RectangleF(textX, y + (rowH - body.Height) / 2, textRight - textX, body.Height * 1.3f), noWrap);
                y += step;
            }
            if (firstVisible + visible < items.Count) g.DrawString("▼", small, dim, size.Width - x * 2, y);
            if (message.Length > 0) g.DrawString(message, small, dim, new RectangleF(x, size.Height - unit * 6, size.Width - x * 2, unit * 3));
            bool adjustable = selected < items.Count && items[selected].Adjust != null;
            g.DrawString(adjustable ? "◀ ▶  volume     B  back     D-pad  move" : "A  select     B  back     D-pad  move", small, dim, x, size.Height - unit * 2.5f);
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
            if (DiscordBridge.Incoming)
            {
                string who = DiscordBridge.IncomingFrom.Length > 0 ? DiscordBridge.IncomingFrom + " is calling" : "Incoming Discord call";
                g.DrawString(who, big, text, unit, unit * 0.4f);
                g.DrawString("Double-tap the Xbox button to answer", small, dim, unit, unit * 1.9f);
            }
            else
            {
                g.DrawString("Calling " + DiscordBridge.CallingTo + "…", big, text, unit, unit * 0.4f);
                g.DrawString("Waiting for them to join  ·  double-tap the Xbox button for call options", small, dim, unit, unit * 1.9f);
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
