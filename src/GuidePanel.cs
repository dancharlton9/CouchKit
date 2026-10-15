// The Guide: an Xbox-guide-style menu on the TV, driven by the controller.
//   Double-tap the Xbox button to open/close it (a single tap opens it while a call is ringing);
//   LB/RB switch tabs, D-pad up/down moves, left/right changes volume, A selects, B goes back.
// Tabs: Home (the running game, recently played games from Playnite: start or swap, screenshot,
// close game), Party (Discord: the current or incoming call, favourites from couchkit.json, chats
// with messages and quick replies, voice channels, mute, deafen), Audio (output device and volume
// mixer), Lights (Home Assistant scenes, room lights, PC lighting) and System (screen off,
// restart, shut down).
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

static class GuidePanel
{
    // An entry runs an Action on the Discord thread (or directly, if Direct), opens a sub-menu whose
    // entries are loaded from Discord, or (volume) is changed with D-pad left/right.
    class Item
    {
        public string Text, Icon;
        public string Sub = "";      // second, smaller line under the text
        public string Badge = "";    // pill on the right, e.g. an unread count
        public bool Header;          // section heading: not selectable
        public bool Wrap;            // Sub wraps over up to three lines (messages)
        public Func<string> Action;
        public bool CloseAfter, Direct, Quiet;   // Quiet: information row (e.g. a message)
        public PlayniteLink.Game Game;   // Home tab: the game this row starts (its cover is shown)
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
        { "tv", "\uE7F4" }, { "bulb", "\uEA80" }, { "bulboff", "\uE708" }, { "camera", "\uE722" }, { "refresh", "\uE72C" },
        { "closegame", "\uE7FC" }, { "battery", "\uE83F" }, { "batterylow", "\uE850" }, { "wired", "\uE839" },
        { "settings", "\uE713" }, { "chevron", "\uE76C" }, { "up", "\uE70E" }, { "down", "\uE70D" }, { "info", "\uE946" }, { "voice", "\uE902" },
        { "send", "\uE724" }, { "quote", "\uE8BD" }, { "home", "\uE80F" }, { "library", "\uE8F1" }, { "play", "\uE768" }, { "swap", "\uE8AB" },
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
        }) { IsBackground = true, Name = "Guide" };
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
        if (DiscordBridge.Incoming) tab = PartyTab;   // ringing: straight to Answer
        title = Tabs[tab];
        items = new List<Item>();
        if (tab == LightsTab) WarmSceneNames();
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
        OnUi(() =>
        {
            if (!items.Any(i => !i.Header)) return;
            do selected = (selected + delta + items.Count) % items.Count; while (items[selected].Header);
            panel.Invalidate();
        });
    }

    // First selectable row at or after i
    static int Selectable(int i)
    {
        i = Math.Max(0, Math.Min(i, items.Count - 1));
        for (int k = i; k < items.Count; k++) if (!items[k].Header) return k;
        for (int k = i; k >= 0; k--) if (!items[k].Header) return k;
        return 0;
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
                selected = Selectable(0);
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

    static readonly string[] Tabs = { "Home", "Party", "Audio", "Lights", "System" };
    const int HomeTab = 0, PartyTab = 1, AudioTab = 2, LightsTab = 3, SystemTab = 4;
    static int tab;
    static string battery = "";

    // LB/RB: previous/next tab (from any level; leaves sub-menus)
    public static void Tab(int direction)
    {
        OnUi(() =>
        {
            tab = (tab + direction + Tabs.Length) % Tabs.Length;
            parents.Clear(); title = Tabs[tab]; message = ""; selected = firstVisible = 0; items = new List<Item>();
            if (tab == LightsTab) WarmSceneNames();
            Rebuild();
            panel.Invalidate();
        });
    }

    static void Rebuild()
    {
        var list = new List<Item>();
        if (tab == HomeTab) HomeItems(list);
        else if (tab == AudioTab) AudioItems(list);
        else if (tab == LightsTab) LightsItems(list);
        else if (tab == SystemTab) SystemItems(list);
        else PartyItems(list);
        string current = selected < items.Count ? items[selected].Text : null;
        items = list;
        int keep = current == null ? -1 : items.FindIndex(i => i.Text == current && !i.Header);
        selected = Selectable(tab == PartyTab && DiscordBridge.Incoming && current == null ? 0 : keep >= 0 ? keep : selected);
    }

    // --- Audio tab: where the sound goes, and how loud ---
    static void AudioItems(List<Item> list)
    {
        list.Add(Heading("Sound plays on"));
        try
        {
            foreach (var d in CouchKit.OutputDevices())
            {
                string id = d.Item2, name = d.Item1;
                bool headset = name.IndexOf("Headset", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Headphone", StringComparison.OrdinalIgnoreCase) >= 0;
                list.Add(new Item { Text = ShortDevice(name), Sub = d.Item3 ? "Playing here now" : "Switch the sound here", Icon = headset ? "headset" : "tv", Dot = d.Item3 ? (Color?)Green : null, Action = () => CouchKit.SetOutput(id, ShortDevice(name)) });
            }
        }
        catch (Exception ex) { CouchKit.Log("Guide: audio devices: " + ex.Message); }
        list.Add(Heading("Volume"));
        foreach (var v in VolumeMenu()) list.Add(v);
    }

    static Item Heading(string text) { return new Item { Text = text, Header = true }; }

    // "Headset Earphone (CORSAIR VIRTUOSO SE Wireless Gaming Headset)" -> "CORSAIR VIRTUOSO SE Wireless Gaming Headset"
    static string ShortDevice(string name)
    {
        int open = name.IndexOf('('), close = name.LastIndexOf(')');
        return open > 0 && close > open ? name.Substring(open + 1, close - open - 1) : name;
    }

    // --- Lights tab: Home Assistant scenes, room lights, PC lighting ---
    static readonly Dictionary<string, string> sceneNames = new Dictionary<string, string>();
    static void WarmSceneNames()
    {
        if (!SmartHome.HasHomeAssistant) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            foreach (var e in SmartHome.Scenes) { string n = SmartHome.NameOf(e); lock (sceneNames) sceneNames[e] = n; }
            OnUi(() => { if (IsOpen && tab == LightsTab && parents.Count == 0) { Rebuild(); panel.Invalidate(); } });
        });
    }

    static void LightsItems(List<Item> list)
    {
        if (SmartHome.HasHomeAssistant)
        {
            list.Add(Heading("Scenes"));
            foreach (var s in SmartHome.Scenes)
            {
                string entity = s, name;
                lock (sceneNames) if (!sceneNames.TryGetValue(entity, out name)) name = entity.Substring(entity.IndexOf('.') + 1).Replace('_', ' ');
                list.Add(new Item { Text = name, Icon = "bulb", Action = () => SmartHome.Activate(entity) });
            }
            list.Add(Heading("Room"));
            list.Add(new Item { Text = "Room lights on", Icon = "bulb", Action = () => SmartHome.SetRoomLights(true) });
            list.Add(new Item { Text = "Room lights off", Icon = "bulboff", Action = () => SmartHome.SetRoomLights(false) });
        }
        else list.Add(new Item { Text = "Home Assistant not set up", Sub = "Add HomeAssistantUrl and a token to couchkit.json", Icon = "bulb", Quiet = true });
        list.Add(Heading("PC"));
        list.Add(new Item { Text = Lighting.Off ? "PC lighting on" : "PC lighting off", Sub = Lighting.Off ? "The PC's lights are off" : "Follows the game's colours", Icon = Lighting.Off ? "bulb" : "bulboff", Direct = true,
                            Action = () => { Lighting.Off = !Lighting.Off; ShowMessage(Lighting.Off ? "PC lighting off" : "PC lighting on"); return ""; } });
    }

    // --- Home tab: the running game, recently played (start or swap), the library ---
    static void HomeItems(List<Item> list)
    {
        var current = PlayniteLink.NowPlaying;
        if (current.Length > 0)
            list.Add(new Item { Text = "Resume game", Sub = current, Icon = "play", Direct = true, Action = () => { Close(); ThreadPool.QueueUserWorkItem(_ => { Thread.Sleep(300); CouchKit.FocusGame(); }); return ""; } });
        list.Add(new Item { Text = "Take a screenshot", Sub = "Saved to Pictures\\CouchKit", Icon = "camera", Direct = true, Action = () =>
        {
            Close();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Thread.Sleep(450);   // let the Guide get out of the picture
                try { CouchKit.Screenshot(); OnUi(() => ShowHud("camera", "Screenshot saved", "Pictures\\CouchKit")); }
                catch (Exception ex) { CouchKit.Log("Screenshot failed: " + ex.Message); }
            });
            return "";
        } });
        if (current.Length > 0) list.Add(new Item { Text = "Close game", Sub = "Quits " + current, Icon = "closegame", Action = CouchKit.CloseRunningGame, CloseAfter = true });
        var recent = PlayniteLink.Recent().Where(r => r.Name != current).Take(8).ToList();
        if (recent.Count > 0) list.Add(Heading(current.Length > 0 ? "Switch to" : "Jump back in"));
        foreach (var gm in recent)
        {
            var game = gm;
            string sub = (string.IsNullOrEmpty(game.Platform) ? "" : game.Platform + "  ·  ") + PlayniteLink.Hours(game.Playtime);
            if (current.Length > 0)
                list.Add(new Item { Text = game.Name, Sub = sub, Icon = "swap", Game = game, Submenu = () => Confirm("Switch to " + game.Name, () => PlayniteLink.SwitchTo(game)) });
            else
                list.Add(new Item { Text = game.Name, Sub = sub, Icon = "play", Game = game, Action = () => PlayniteLink.Start(game), CloseAfter = true });
        }
        list.Add(new Item { Text = "Playnite library", Sub = "Everything else", Icon = "library", Direct = true, Action = () => { Close(); ThreadPool.QueueUserWorkItem(_ => { Thread.Sleep(300); CouchKit.FocusPlaynite(); }); return ""; } });
    }

    // --- System tab ---
    static void SystemItems(List<Item> list)
    {
        list.Add(new Item { Text = "Turn screen off", Sub = "Any input wakes it", Icon = "tv", Direct = true, Action = () => { Close(); ThreadPool.QueueUserWorkItem(_ => { Thread.Sleep(400); CouchKit.ScreenOff(); }); return ""; } });
        list.Add(new Item { Text = "Restart Playnite", Sub = "If it gets stuck", Icon = "refresh", Submenu = () => Confirm("Restart Playnite", () => { CouchKit.RestartPlaynite(); return ""; }) });
        list.Add(new Item { Text = "Restart PC", Icon = "refresh", Submenu = () => Confirm("Restart the PC", () => { CouchKit.PowerAction(true); return ""; }) });
        list.Add(new Item { Text = "Shut down", Icon = "power", Submenu = () => Confirm("Shut down", () => { CouchKit.PowerAction(false); return ""; }) });
    }

    static List<Item> Confirm(string what, Func<string> action)
    {
        return new List<Item>
        {
            new Item { Text = "Yes, " + what.ToLowerInvariant(), Icon = "accept", Action = action, CloseAfter = true },
            new Item { Text = "Cancel", Icon = "decline", Direct = true, Action = () => { GoBack(); return ""; } },
        };
    }

    static void ShowMessage(string m) { message = m; if (panel != null) panel.Invalidate(); }

    // --- Party tab ---
    static void PartyItems(List<Item> list)
    {
        if (!DiscordBridge.Running)
            list.Add(new Item { Text = "Start Discord", Icon = "power", Action = DiscordBridge.StartDiscord });
        if (DiscordBridge.Incoming)
        {
            list.Add(new Item { Text = "Answer", Icon = "accept", Action = DiscordBridge.Answer, CloseAfter = true });
            list.Add(new Item { Text = "Decline", Icon = "decline", Action = DiscordBridge.Decline });
        }
        if (DiscordBridge.InCall) list.Add(new Item { Text = "Leave call", Icon = "hangup", Action = DiscordBridge.Leave });
        list.Add(new Item { Text = DiscordBridge.Muted ? "Unmute microphone" : "Mute microphone", Icon = DiscordBridge.Muted ? "micoff" : "mic", Action = DiscordBridge.ToggleMute });
        if (S.Favourites.Length > 0) list.Add(Heading("Favourites"));
        foreach (var f in S.Favourites)
        {
            var fav = f;
            if (!string.IsNullOrEmpty(fav.Friend))
            {
                string status;
                lock (DiscordBridge.Statuses) DiscordBridge.Statuses.TryGetValue(fav.Friend, out status);
                list.Add(new Item { Text = "Call " + (fav.Label ?? fav.Friend), Sub = Describe(status ?? "", "", DiscordBridge.Unread(fav.Friend), false), Icon = "call", Dot = StatusColour(status ?? ""), Action = () => DiscordBridge.CallFriend(fav.Friend), CloseAfter = true });
            }
            else if (!string.IsNullOrEmpty(fav.Server) && !string.IsNullOrEmpty(fav.Channel))
                list.Add(new Item { Text = "Join " + (fav.Label ?? fav.Channel), Sub = fav.Channel + "  ·  " + fav.Server, Icon = "channel", Action = () => DiscordBridge.JoinVoice(fav.Server, fav.Channel), CloseAfter = true });
        }
        list.Add(Heading("Discord"));
        list.Add(new Item { Text = "Chats", Sub = "Messages, calls and quick replies", Icon = "chat", Submenu = ChatsMenu });
        list.Add(new Item { Text = "Voice channels", Sub = "See who's in, and join", Icon = "voice", Submenu = ServersMenu });
        list.Add(new Item { Text = DiscordBridge.Deafened ? "Undeafen" : "Deafen", Icon = DiscordBridge.Deafened ? "deafened" : "headset", Action = DiscordBridge.ToggleDeafen });
        list.Add(new Item { Text = "Open Discord", Sub = "The controller works as a mouse", Icon = "mouse", Direct = true, Action = () => { Close(); EnterMouseMode(); return ""; } });
    }

    // Sub-menus, loaded live from Discord on its thread
    static List<Item> ChatsMenu()
    {
        return DiscordBridge.ListChats().Select(c =>
        {
            string name = c.Name;
            int fresh = DiscordBridge.Unread(name);
            string sub = c.Group ? "Group" + (c.Members > 0 ? " of " + c.Members : "") + (c.InCall ? "  ·  in a call" : "") : Describe(c.Status, c.Activity, 0, c.InCall);
            var chat = c;
            return new Item { Text = name, Sub = sub, Badge = fresh > 0 ? fresh.ToString() : "", Icon = c.Group ? "people" : "chat", Dot = StatusColour(c.Status), Submenu = () => ChatPage(chat) };
        }).ToList();
    }

    // A chat's page: its last few messages, call (or join its call), and quick replies
    static List<Item> ChatPage(DiscordBridge.Chat chat)
    {
        var list = new List<Item>();
        string name = chat.Name;
        list.Add(new Item { Text = chat.InCall ? "Join the call" : "Call " + name, Icon = "call", Action = () => DiscordBridge.CallFriend(name), CloseAfter = true });
        list.Add(new Item { Text = "Quick reply", Icon = "message", Submenu = () => S.QuickReplies.Select(r =>
        {
            string reply = r;
            return new Item { Text = reply, Icon = "send", Action = () => DiscordBridge.SendMessage(name, reply), CloseAfter = false };
        }).ToList() });
        var messages = DiscordBridge.ReadMessages(name, 6);
        if (messages.Count > 0) list.Add(Heading("Latest messages"));
        foreach (var m in messages)
            list.Add(new Item { Text = m.Author.Length > 0 ? m.Author : "…", Sub = m.Text, Wrap = true, Icon = "quote", Quiet = true });
        return list;
    }

    static List<Item> ServersMenu()
    {
        return DiscordBridge.ListServers().Select(s =>
        {
            string server = s;
            return new Item { Text = server, Icon = "server", Submenu = () => ChannelsMenu(server) };
        }).ToList();
    }

    static List<Item> ChannelsMenu(string server)
    {
        return DiscordBridge.ListVoiceChannels(server).Select(ch =>
        {
            string channel = ch.Name;
            string who = ch.People.Count == 0 ? "Empty" : string.Join(", ", ch.People.Take(4)) + (ch.People.Count > 4 ? " +" + (ch.People.Count - 4) : "");
            return new Item { Text = channel, Sub = who, Badge = ch.People.Count > 0 ? ch.People.Count.ToString() : "", Icon = ch.People.Count > 0 ? "people" : "channel", Action = () => DiscordBridge.JoinVoice(server, channel), CloseAfter = true };
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

    // "Online  ·  Playing Halo" / "Idle  ·  3 new" / "In a call"
    static string Describe(string status, string activity, int unread, bool inCall)
    {
        var parts = new List<string>();
        if (status.Length > 0) parts.Add(status);
        if (inCall) parts.Add("in a call");
        else if (activity.Length > 0) parts.Add(activity);
        if (unread > 0) parts.Add(unread + " new");
        return string.Join("  ·  ", parts);
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
    static string lastBattery = "";
    static void Tick()
    {
        try { battery = CouchKit.ControllerBattery(); } catch { battery = ""; }
        if ((battery == "Low" || battery == "Empty") && battery != lastBattery) ShowHud("batterylow", "Controller battery " + battery.ToLowerInvariant(), "Time to charge it soon");
        lastBattery = battery;
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
        using (var b = new SolidBrush(colour))
            Centred(g, glyph ? Glyphs[button] : button, glyph ? iconFont : "Segoe UI Semibold", glyph ? FontStyle.Regular : FontStyle.Bold,
                    size * (glyph ? 0.5f : button.Length > 1 ? 0.36f : 0.5f), b, x + size / 2, y + size / 2);
        float lx = x + size * 1.25f;
        Line(g, label, font, Dim, new RectangleF(lx, y + (size - font.Height) / 2, 1000, font.Height * 1.3f));
        return lx + TextRenderer.MeasureText(label, font).Width + size * 0.9f;
    }

    static void DrawIcon(Graphics g, string key, float x, float y, float size, Brush brush)
    {
        string glyph;
        if (key == null || !Glyphs.TryGetValue(key, out glyph)) return;
        Centred(g, glyph, iconFont, FontStyle.Regular, size, brush, x + size / 2, y + size / 2);
    }

    // Draws text so its visible shape (not its font box) is centred on (cx, cy). Icon glyphs and
    // single letters have uneven space around them in the font, which puts them off-centre otherwise.
    static void Centred(Graphics g, string s, string family, FontStyle style, float size, Brush brush, float cx, float cy)
    {
        using (var ff = new FontFamily(family))
        using (var path = new GraphicsPath())
        {
            path.AddString(s, ff, (int)style, size, PointF.Empty, StringFormat.GenericTypographic);
            var b = path.GetBounds();
            if (b.Width <= 0) return;
            using (var m = new Matrix()) { m.Translate(cx - (b.Left + b.Width / 2), cy - (b.Top + b.Height / 2)); path.Transform(m); }
            g.FillPath(brush, path);
        }
    }

    // Game covers, loaded once and kept small
    static readonly Dictionary<string, Image> covers = new Dictionary<string, Image>();
    static Image Cover(PlayniteLink.Game game)
    {
        if (game == null || string.IsNullOrEmpty(game.Cover)) return null;
        Image img;
        if (covers.TryGetValue(game.Cover, out img)) return img;
        try
        {
            using (var src = Image.FromFile(game.Cover))
            {
                int h = 360, w = Math.Max(1, src.Width * h / Math.Max(1, src.Height));
                var small = new Bitmap(w, h);
                using (var g = Graphics.FromImage(small)) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(src, 0, 0, w, h); }
                img = small;
            }
        }
        catch (Exception ex) { CouchKit.Log("Guide: cover " + game.Name + ": " + ex.Message); img = null; }
        covers[game.Cover] = img;
        return img;
    }

    // Cover art cropped to a 3:4 tile with rounded corners
    static void DrawCover(Graphics g, Image img, RectangleF r, float radius)
    {
        var state = g.Save();
        using (var path = RoundedPath(r, radius)) g.SetClip(path, CombineMode.Intersect);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        float scale = Math.Max(r.Width / img.Width, r.Height / img.Height);
        float w = img.Width * scale, h = img.Height * scale;
        g.DrawImage(img, r.Left + (r.Width - w) / 2, r.Top + (r.Height - h) / 2, w, h);
        g.Restore(state);
    }

    static readonly string[] TabIcons = { "home", "people", "volume", "bulb", "settings" };

    // Row heights: headings are short, rows with a second line taller, game rows tall enough for
    // their cover, and messages grow to fit up to three lines
    static int RowHeight(Item item, int unit, Font small, Font heading, int width)
    {
        if (item.Header) return (int)(unit * 1.7f);
        if (item.Game != null) return (int)(unit * 3.3f);
        if (item.Wrap && item.Sub.Length > 0)
        {
            int h = TextRenderer.MeasureText(item.Sub, small, new Size(width, 10000), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding).Height;
            int lines = Math.Max(1, Math.Min(3, (int)Math.Round(h / (double)small.Height)));
            return (int)(unit * 0.5f + heading.Height * 1.35f + lines * small.Height + unit * 0.6f);
        }
        return item.Sub.Length > 0 ? (int)(unit * 2.8f) : (int)(unit * 2.2f);
    }

    static void PaintPanel(Graphics g, Size size)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        int unit = size.Height / 40;   // scales with the TV's resolution
        using (var accent = new LinearGradientBrush(new Point(0, 0), new Point(0, size.Height), Aqua, Magenta))
            g.FillRectangle(accent, size.Width - unit / 3, 0, unit / 3, size.Height);
        using (var titleFont = new Font("Segoe UI Semibold", unit * 1.5f, GraphicsUnit.Pixel))
        using (var body = new Font("Segoe UI", unit * 1.0f, GraphicsUnit.Pixel))
        using (var bold = new Font("Segoe UI Semibold", unit * 1.0f, GraphicsUnit.Pixel))
        using (var small = new Font("Segoe UI", unit * 0.72f, GraphicsUnit.Pixel))
        using (var heading = new Font("Segoe UI Semibold", unit * 0.66f, GraphicsUnit.Pixel))
        using (var text = new SolidBrush(Text))
        using (var dim = new SolidBrush(Dim))
        {
            int x = unit * 2, y = unit * 2, w = size.Width - x * 2;

            // Top bar: tabs as icons, the current one as a pill with its name; the clock on the right
            string clock = DateTime.Now.ToString("HH:mm");
            int clockW = TextRenderer.MeasureText(clock, bold).Width;
            Line(g, clock, bold, Dim, new RectangleF(x + w - clockW, y + (unit * 2.0f - bold.Height) / 2, clockW + 4, bold.Height * 1.3f));
            float tx = x - unit * 0.5f, tabH = unit * 2.0f, iconS = unit * 1.05f;
            for (int t = 0; t < Tabs.Length; t++)
            {
                bool on = t == tab;
                int labelW = on ? TextRenderer.MeasureText(Tabs[t], bold).Width : 0;
                float tw = on ? unit * 2.6f + labelW + unit * 0.4f : unit * 2.4f;
                var pill = new RectangleF(tx, y, tw, tabH);
                if (on)
                    using (var path = RoundedPath(pill, tabH / 2))
                    {
                        using (var fill = new SolidBrush(Card)) g.FillPath(fill, path);
                        using (var edge = new LinearGradientBrush(pill, Aqua, Magenta, LinearGradientMode.Horizontal))
                        using (var pen = new Pen(edge, unit / 10f)) g.DrawPath(pen, path);
                    }
                using (Brush ib = on ? (Brush)new LinearGradientBrush(new RectangleF(tx, y, unit * 2, unit * 2), Aqua, Magenta, 45f) : new SolidBrush(Dim))
                    DrawIcon(g, TabIcons[t], tx + (on ? unit * 0.75f : (tw - iconS) / 2), y + (tabH - iconS) / 2, iconS, ib);
                if (on) Line(g, Tabs[t], bold, Text, new RectangleF(tx + unit * 2.25f, y + (tabH - bold.Height) / 2, labelW + 4, bold.Height * 1.3f));
                tx += tw + unit * 0.15f;
            }
            y += (int)(tabH + unit * 1.0f);

            // In a sub-menu: where you are, then its title
            if (parents.Count > 0)
            {
                string trail = string.Join("  ›  ", parents.Reverse().Select(l => l.Title).ToArray());
                Line(g, trail.ToUpperInvariant(), heading, Dim, new RectangleF(x + unit / 2, y, w, heading.Height * 1.3f));
                y += (int)(heading.Height * 1.4f);
                Line(g, title, titleFont, Text, new RectangleF(x + unit * 0.4f, y, w, titleFont.Height * 1.25f));
                y += (int)(titleFont.Height * 1.5f);
            }

            // Status card: Home shows what's playing, Party the call (or the incoming call)
            if (parents.Count == 0 && (tab == PartyTab || tab == HomeTab))
            {
                bool home = tab == HomeTab, playing = PlayniteLink.Running;
                var currentGame = home && playing ? PlayniteLink.Current : null;
                var cover = Cover(currentGame);
                var card = new RectangleF(x - unit / 2, y, w + unit, unit * 3.6f);
                using (var path = RoundedPath(card, unit * 0.6f))
                {
                    using (var fill = new SolidBrush(!home && DiscordBridge.Incoming ? CardLight : Card)) g.FillPath(fill, path);
                    if (home ? playing : DiscordBridge.Incoming || DiscordBridge.InCall)
                        using (var edge = new LinearGradientBrush(card, Aqua, Magenta, LinearGradientMode.Horizontal))
                        using (var pen = new Pen(edge, unit / 8f)) g.DrawPath(pen, path);
                }
                string icon = home ? "game" : DiscordBridge.Incoming ? "call" : DiscordBridge.InCall ? "people" : "chat";
                string over = home ? (playing ? "NOW PLAYING" : "PLAYNITE") : DiscordBridge.Incoming ? "INCOMING CALL" : DiscordBridge.InCall ? "IN A CALL" : "DISCORD";
                string line1 = DiscordBridge.Incoming ? (DiscordBridge.IncomingFrom.Length > 0 ? DiscordBridge.IncomingFrom + " is calling" : "Someone's calling")
                             : !DiscordBridge.Running ? "Discord isn't running"
                             : DiscordBridge.InCall ? (DiscordBridge.CallName.Length > 0 ? DiscordBridge.CallName : "Voice call")
                             : "Not in a call";
                string line2 = DiscordBridge.InCall ? (DiscordBridge.Muted ? "Microphone muted" : "Microphone live") + (DiscordBridge.Deafened ? "  ·  deafened" : "")
                             : DiscordBridge.Incoming ? "Press A to answer" : "Call a friend or join a channel";
                if (home)
                {
                    line1 = playing ? PlayniteLink.NowPlaying : "Nothing running";
                    line2 = currentGame != null ? (string.IsNullOrEmpty(currentGame.Platform) ? "" : currentGame.Platform + "  ·  ") + PlayniteLink.Hours(currentGame.Playtime) : playing ? "" : "Pick something to play";
                }
                float cx = card.Left + unit * 3.1f;
                if (cover != null)
                {
                    float ch = card.Height - unit * 0.8f;
                    DrawCover(g, cover, new RectangleF(card.Left + unit * 0.4f, card.Top + unit * 0.4f, ch * 0.75f, ch), unit * 0.35f);
                    cx = card.Left + unit * 0.4f + ch * 0.75f + unit * 0.7f;
                }
                else
                    using (var iconBrush = new LinearGradientBrush(new RectangleF(card.Left, card.Top, unit * 3, unit * 3), Aqua, Magenta, 45f))
                        DrawIcon(g, icon, card.Left + unit * 0.75f, card.Top + (card.Height - unit * 1.6f) / 2, unit * 1.6f, iconBrush);
                float cw = card.Right - cx - unit * 0.6f;
                Line(g, over, heading, Aqua, new RectangleF(cx, card.Top + unit * 0.45f, cw, heading.Height * 1.3f));
                Line(g, line1, bold, Text, new RectangleF(cx, card.Top + unit * 1.15f, cw, bold.Height * 1.25f));
                Line(g, line2, small, !home && DiscordBridge.InCall && DiscordBridge.Muted ? Red : Dim, new RectangleF(cx, card.Top + unit * 2.45f, cw, small.Height * 1.25f));
                y += (int)(card.Height + unit * 0.7f);
            }

            // Rows. Scroll so the selection (and the heading just above it) stays in view, leaving
            // room for the message and the button hints.
            int bottom = size.Height - unit * 5, gap = unit / 5;
            int textW = w - (int)(unit * 2.9f);
            var heights = items.Select(i => RowHeight(i, unit, small, heading, textW)).ToList();
            if (selected < firstVisible) firstVisible = selected;
            if (firstVisible == selected && selected > 0 && items[selected - 1].Header) firstVisible = selected - 1;
            Func<int, int, int> span = (from, to) => { int sum = 0; for (int k = from; k <= to && k < heights.Count; k++) sum += heights[k] + gap; return sum; };
            while (firstVisible < selected && span(firstVisible, selected) > bottom - y) firstVisible++;
            if (firstVisible > 0) DrawIcon(g, "up", x + w - unit * 0.6f, y - unit * 0.9f, unit * 0.7f, dim);
            int last = firstVisible - 1;
            for (int i = firstVisible; i < items.Count && y + heights[i] <= bottom; i++)
            {
                last = i;
                var item = items[i];
                int rowH = heights[i];
                if (item.Header)
                {
                    Line(g, item.Text.ToUpperInvariant(), heading, Dim, new RectangleF(x + unit / 2, y + rowH - heading.Height * 1.35f, w, heading.Height * 1.3f));
                    y += rowH + gap;
                    continue;
                }
                bool sel = i == selected;
                var r = new Rectangle(x - unit / 2, y, w + unit, rowH);
                if (sel)
                {
                    using (var path = RoundedPath(r, unit * 0.5f))
                    {
                        using (var card = new SolidBrush(Card)) g.FillPath(card, path);
                        using (var edge = new Pen(Color.FromArgb(70, Aqua), unit / 14f)) g.DrawPath(edge, path);
                    }
                    using (var edge = new LinearGradientBrush(r, Aqua, Magenta, LinearGradientMode.Vertical)) g.FillRectangle(edge, r.Left, r.Top + unit / 3, unit / 5, r.Height - unit * 2 / 3);
                }
                int textX = x + unit / 2;
                var cover = Cover(item.Game);
                if (cover != null)
                {
                    float ch = rowH - unit * 0.6f;
                    DrawCover(g, cover, new RectangleF(textX, y + unit * 0.3f, ch * 0.75f, ch), unit * 0.25f);
                    textX += (int)(ch * 0.75f + unit * 0.7f);
                }
                else if (item.Icon != null)
                {
                    float iy = item.Wrap ? y + unit * 0.5f + heading.Height * 0.62f - unit * 0.55f : y + (rowH - unit * 1.1f) / 2;
                    Brush ib = item.Icon == "decline" || item.Icon == "hangup" ? new SolidBrush(Red) : item.Icon == "accept" ? new SolidBrush(Green)
                             : sel ? (Brush)new LinearGradientBrush(new RectangleF(textX, y, unit * 1.2f, rowH), Aqua, Magenta, 45f) : new SolidBrush(Dim);
                    DrawIcon(g, item.Icon, textX, iy, unit * 1.1f, ib);
                    ib.Dispose();
                    textX += (int)(unit * 1.9f);
                }
                int textRight = r.Right - unit;
                if (item.Submenu != null)
                {
                    using (var cb = new SolidBrush(sel ? Text : Dim)) DrawIcon(g, "chevron", textRight - unit * 0.6f, y + (rowH - unit * 0.75f) / 2, unit * 0.75f, cb);
                    textRight -= (int)(unit * 1.2f);
                }
                if (item.Badge.Length > 0)
                {
                    int bw = Math.Max(TextRenderer.MeasureText(item.Badge, heading).Width + unit / 2, (int)(unit * 1.15f)), bh = (int)(unit * 1.15f);
                    var pill = new RectangleF(textRight - bw, y + (rowH - bh) / 2f, bw, bh);
                    using (var path = RoundedPath(pill, bh / 2f))
                    using (var fill = new LinearGradientBrush(pill, Magenta, Color.FromArgb(170, 62, 152), LinearGradientMode.Horizontal)) g.FillPath(fill, path);
                    var bs = TextRenderer.MeasureText(item.Badge, heading);
                    Line(g, item.Badge, heading, Color.White, new RectangleF(pill.Left + (bw - bs.Width) / 2f, pill.Top + (bh - heading.Height) / 2f, bs.Width + 4, heading.Height * 1.3f));
                    textRight -= bw + unit / 2;
                }
                if (item.Level >= -1)
                {
                    // Volume bar on the right: palette gradient for the level, or "not playing"
                    int barW = r.Width * 36 / 100, barH = unit / 2, barX = r.Right - barW - unit, barY = y + (rowH - barH) / 2;
                    textRight = barX - unit / 2;
                    if (item.Level < 0) Line(g, "not playing", small, Dim, new RectangleF(barX, y + (rowH - small.Height) / 2, barW, small.Height * 1.3f));
                    else
                    {
                        using (var track = new SolidBrush(Color.FromArgb(60, 255, 255, 255)))
                        using (var tp = RoundedPath(new RectangleF(barX, barY, barW, barH), barH / 2f)) g.FillPath(track, tp);
                        int fill = barW * item.Level / 100;
                        if (fill > barH)
                            using (var lvl = new LinearGradientBrush(new Rectangle(barX, barY, barW, barH), Aqua, Magenta, LinearGradientMode.Horizontal))
                            using (var lp = RoundedPath(new RectangleF(barX, barY, fill, barH), barH / 2f)) g.FillPath(lvl, lp);
                        string pct = item.Level + "%";
                        int pw = TextRenderer.MeasureText(pct, small).Width;
                        Line(g, pct, small, sel ? Text : Dim, new RectangleF(barX - unit * 0.5f - pw, y + (rowH - small.Height) / 2, pw + 4, small.Height * 1.3f));
                        textRight = (int)(barX - unit * 3f);
                    }
                }
                int nameRight = textRight;   // the name leaves room for its status dot
                if (item.Dot.HasValue)
                {
                    // Status dot just after the name
                    float d = unit * 0.45f;
                    nameRight = textRight - (int)(d + unit * 0.6f);
                    float ty = item.Sub.Length > 0 ? y + rowH / 2f - body.Height * 0.95f : y + (rowH - body.Height) / 2f;
                    int nameW = Math.Min(TextRenderer.MeasureText(g, item.Text, sel ? bold : body, new Size(10000, 1000), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width, nameRight - textX);
                    using (var dot = new SolidBrush(item.Dot.Value)) g.FillEllipse(dot, textX + nameW + unit * 0.35f, ty + body.Height * 0.6f - d / 2, d, d);
                }
                Color main = item.Quiet ? Dim : sel ? Text : Color.FromArgb(205, 205, 222);
                if (item.Wrap)
                {
                    Line(g, item.Text, heading, Aqua, new RectangleF(textX, y + unit * 0.5f, textRight - textX, heading.Height * 1.3f));
                    var box = new Rectangle(textX, (int)(y + unit * 0.5f + heading.Height * 1.35f), textRight - textX, rowH - (int)(unit * 0.5f + heading.Height * 1.35f));
                    TextRenderer.DrawText(g, item.Sub, small, box, sel ? Text : Color.FromArgb(205, 205, 222),
                        TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.Left | TextFormatFlags.Top);
                }
                else if (item.Sub.Length > 0)
                {
                    Line(g, item.Text, sel ? bold : body, main, new RectangleF(textX, y + rowH / 2f - body.Height * 0.95f, nameRight - textX, body.Height * 1.3f));
                    Line(g, item.Sub, small, Dim, new RectangleF(textX, y + rowH / 2f + unit * 0.12f, textRight - textX, small.Height * 1.3f));
                }
                else Line(g, item.Text, sel ? bold : body, main, new RectangleF(textX, y + (rowH - body.Height) / 2, nameRight - textX, body.Height * 1.3f));
                y += rowH + gap;
            }
            if (last < items.Count - 1) DrawIcon(g, "down", x + w - unit * 0.6f, y - unit * 0.1f, unit * 0.7f, dim);

            // Footer: the result of the last action, button hints, controller battery
            if (message.Length > 0)
            {
                using (var mb = new LinearGradientBrush(new RectangleF(x, 0, unit * 1.2f, 10), Aqua, Magenta, LinearGradientMode.Horizontal))
                    DrawIcon(g, "info", x + unit * 0.1f, size.Height - unit * 4.1f + (small.Height - unit * 0.8f) / 2, unit * 0.8f, mb);
                Line(g, message, small, Text, new RectangleF(x + unit * 1.2f, size.Height - unit * 4.1f, w - unit * 1.2f, unit * 1.4f));
            }
            using (var sep = new Pen(Color.FromArgb(40, 255, 255, 255), Math.Max(1, unit / 20f))) g.DrawLine(sep, x, size.Height - unit * 3.4f, x + w, size.Height - unit * 3.4f);
            bool adjustable = selected < items.Count && items[selected].Adjust != null;
            float hy = size.Height - unit * 2.6f, hs = unit * 1.25f, hx = x;
            if (adjustable) hx = Hint(g, "◀▶", Text, "volume", small, hx, hy, hs);
            else if (selected < items.Count && !items[selected].Quiet) hx = Hint(g, "A", Green, "select", small, hx, hy, hs);
            hx = Hint(g, "B", Red, parents.Count > 0 ? "back" : "close", small, hx, hy, hs);
            hx = Hint(g, "LB", Dim, "", small, hx, hy, hs) - hs * 0.7f;
            hx = Hint(g, "RB", Dim, "tabs", small, hx, hy, hs);
            if (battery.Length > 0)
            {
                bool low = battery == "Low" || battery == "Empty";
                string key = battery == "Wired" ? "wired" : low ? "batterylow" : "battery";
                string label = battery == "Wired" ? "wired" : battery.ToLowerInvariant();
                int lw = TextRenderer.MeasureText(label, small).Width;
                float bx = size.Width - x - lw;
                Line(g, label, small, low ? Red : Dim, new RectangleF(bx, hy + (hs - small.Height) / 2, lw + 4, small.Height * 1.3f));
                using (var bb = new SolidBrush(low ? Red : Dim)) DrawIcon(g, key, bx - unit * 1.6f, hy + (hs - unit * 1.1f) / 2, unit * 1.1f, bb);
            }
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
            DrawIcon(g, "call", unit * 0.9f, (size.Height - unit / 3f - unit * 2.2f) / 2, unit * 2.2f, iconBrush);
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
            DrawIcon(g, hudIcon, unit * 0.9f, (size.Height - unit * 1.9f) / 2, unit * 1.9f, iconBrush);
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
        selected = Selectable(1);
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
        battery = "Medium";
        for (int t = 1; t < Tabs.Length; t++)
        {
            tab = t; items.Clear(); parents.Clear(); title = Tabs[t]; selected = 1;
            if (t == LightsTab) foreach (var e in SmartHome.Scenes) { string n = SmartHome.NameOf(e); lock (sceneNames) sceneNames[e] = n; }
            Rebuild();
            render("panel-" + Tabs[t].ToLowerInvariant() + ".png", 1305, 2160, PaintPanel);
        }
        tab = 0;
        hudIcon = "micoff"; hudTitle = "Microphone muted"; hudDetail = "";
        render("hud-muted.png", 921, 172, PaintHud);
        hudIcon = "message"; hudTitle = "Matt"; hudDetail = "2 new messages";
        render("hud-message.png", 921, 172, PaintHud);
        DiscordBridge.InCall = false; DiscordBridge.Incoming = true; DiscordBridge.IncomingFrom = "Matt";
        render("banner.png", 1766, 259, PaintBanner);
        items.Clear(); Rebuild(); selected = 0;
        render("panel-incoming.png", 1305, 2160, PaintPanel);
        // Sub-menus with made-up chats and messages
        DiscordBridge.Incoming = false; tab = PartyTab;
        parents.Clear(); parents.Push(new Level { Title = "Party", Items = items, Selected = 0 });
        title = "Chats"; items = MockChats(); selected = 0;
        render("panel-chats.png", 1305, 2160, PaintPanel);
        parents.Push(new Level { Title = "Chats", Items = items, Selected = 0 });
        title = "Matt"; items = MockChat(); selected = 3;
        render("panel-chat.png", 1305, 2160, PaintPanel);
        parents.Clear();
    }

    // Made-up chats and messages for the preview and demo (never real ones)
    static List<Item> MockChats()
    {
        return new List<Item>
        {
            new Item { Text = "Matt", Sub = "Online  ·  Playing Call of Duty", Badge = "2", Icon = "chat", Dot = Green, Submenu = () => null },
            new Item { Text = "Sam", Sub = "Idle", Icon = "chat", Dot = StatusColour("Idle"), Submenu = () => null },
            new Item { Text = "Saturday squad", Sub = "Group of 5  ·  in a call", Icon = "people", Submenu = () => null },
            new Item { Text = "Alex", Sub = "Offline", Icon = "chat", Dot = StatusColour("Offline"), Submenu = () => null },
        };
    }

    static List<Item> MockChat()
    {
        return new List<Item>
        {
            new Item { Text = "Call Matt", Icon = "call", Action = () => "" },
            new Item { Text = "Quick reply", Icon = "message", Submenu = () => null },
            Heading("Latest messages"),
            new Item { Text = "Matt", Sub = "You on tonight? Thinking a few rounds of the new map then maybe some Gears if we've still got the energy for it", Wrap = true, Icon = "quote", Quiet = true },
            new Item { Text = "Dan", Sub = "Yeah, give me 20 mins", Wrap = true, Icon = "quote", Quiet = true },
            new Item { Text = "Matt", Sub = "👍", Wrap = true, Icon = "quote", Quiet = true },
        };
    }

    // Index of the k-th selectable row (headings don't count)
    static int Nth(int k)
    {
        int count = -1, last = 0;
        for (int i = 0; i < items.Count; i++)
            if (!items[i].Header) { last = i; if (++count == k) return i; }
        return last;
    }

    // For showing how it looks: CouchKit.exe --demo <folder> [background image] renders a scripted
    // 1920x1080 30 fps tour of the Guide (Home, Party, a chat, Audio, Lights, System), then an
    // incoming call, as numbered PNG frames, using the real drawing code over a game's artwork.
    // The most recently played game stands in as the running one. Encode with ffmpeg afterwards.
    public static void Demo(DiscordSettings settings, string folder, string background)
    {
        S = settings;
        using (var fonts = new InstalledFontCollection())
            if (fonts.Families.Any(f => f.Name == "Segoe Fluent Icons")) iconFont = "Segoe Fluent Icons";
        const int W = 1920, H = 1080, Fps = 30;
        int panelW = W * 34 / 100, hudW = W * 24 / 100, hudH = H * 8 / 100, banW = W * 46 / 100, banH = H * 12 / 100;
        lock (DiscordBridge.Statuses) foreach (var f in S.Favourites) if (!string.IsNullOrEmpty(f.Friend)) DiscordBridge.Statuses[f.Friend] = "Online";
        foreach (var e in SmartHome.Scenes) { string sn = SmartHome.NameOf(e); lock (sceneNames) sceneNames[e] = sn; }   // scene names for the Lights tab
        var playing = PlayniteLink.Recent().FirstOrDefault();
        if (playing != null) PlayniteLink.Pretend = playing.Name;
        if (string.IsNullOrEmpty(background) && playing != null) background = playing.Background;

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
        // D-pad presses: k-th row, one press every `every` seconds from `from`, up to `max`
        Func<double, double, double, int, int> presses = (t, from, every, max) => Math.Min(max, (int)Math.Max(0, (t - from) / every + 1));

        // Audio tab with example outputs (the real one lists whatever is plugged in)
        var volume = new List<Item>
        {
            new Item { Text = "Game", Icon = "game", Level = 80, Adjust = d => 0 },
            new Item { Text = "Discord", Icon = "chat", Level = 100, Adjust = d => 0 },
            new Item { Text = "Everything", Icon = "volume", Level = 60, Adjust = d => 0 },
        };
        var audio = new List<Item>
        {
            Heading("Sound plays on"),
            new Item { Text = "CORSAIR VIRTUOSO SE Wireless Gaming Headset", Sub = "Playing here now", Icon = "headset", Dot = Green },
            new Item { Text = "LG TV SSCR2", Sub = "Switch the sound here", Icon = "tv" },
            Heading("Volume"),
        };
        audio.AddRange(volume);
        battery = "Full";

        int frames = (int)(30.5 * Fps);
        for (int n = 0; n < frames; n++)
        {
            double t = (double)n / Fps;
            // --- Script ---
            // 1.0 HUD connected · 4.3 Guide in (Home, browse games) · 7.6 RB Party, down to Chats
            // 9.8 A Chats · 11.0 A Matt's chat, down to the messages · 13.4 B, 13.8 B
            // 14.4 RB Audio, volume down · 17.2 RB Lights · 19.0 RB System · 20.6 Guide out
            // 21.4 Matt calling · 23.6 tap: Answer · 24.9 HUD connected · 27.6 HUD muted
            DiscordBridge.Running = true;
            DiscordBridge.Incoming = t >= 21.4 && t < 24.6;
            DiscordBridge.IncomingFrom = "Matt";
            DiscordBridge.InCall = t < 21.2 || t >= 24.6;
            DiscordBridge.CallName = t < 21.2 ? "General / Charlton Home" : "Matt";
            DiscordBridge.Muted = t >= 27.6;
            double panelX = -1;   // fraction shown: 0 hidden .. 1 fully in
            if (t >= 4.3 && t < 20.9) panelX = t < 4.6 ? ease((t - 4.3) / 0.3) : t > 20.6 ? 1 - ease((t - 20.6) / 0.3) : 1;
            if (t >= 23.6 && t < 24.9) panelX = t < 23.9 ? ease((t - 23.6) / 0.3) : t > 24.6 ? 1 - ease((t - 24.6) / 0.3) : 1;
            if (panelX >= 0)
            {
                items = new List<Item>(); parents.Clear(); selected = firstVisible = 0; message = "";
                tab = t < 7.6 ? HomeTab : t < 14.4 || t >= 23.6 ? PartyTab : t < 17.2 ? AudioTab : t < 19.0 ? LightsTab : SystemTab;
                title = Tabs[tab];
                if (tab == AudioTab)
                {
                    items = audio;
                    selected = Nth(presses(t, 14.9, 0.35, 2));   // down to Game
                    volume[0].Level = t < 15.8 ? 80 : Math.Max(50, 80 - 5 * (int)((t - 15.8) / 0.2));
                }
                else
                {
                    Rebuild();
                    if (tab == HomeTab) selected = Nth(presses(t, 5.0, 0.4, 5));
                    else if (tab == LightsTab) selected = Nth(presses(t, 17.6, 0.35, 3));
                    else if (tab == SystemTab) selected = Nth(presses(t, 19.4, 0.35, 2));
                    else if (t >= 23.6) selected = 0;   // incoming: Answer
                    else
                    {
                        int chats = items.FindIndex(i => i.Text == "Chats");
                        selected = t < 9.8 || t >= 13.8 ? (t >= 13.8 ? chats : Nth(presses(t, 8.0, 0.35, 4))) : chats;
                        if (t >= 9.8 && t < 13.8)
                        {
                            parents.Push(new Level { Title = "Party", Items = items, Selected = selected });
                            title = "Chats"; items = MockChats(); selected = 0;
                            if (t >= 11.0 && t < 13.4)
                            {
                                parents.Push(new Level { Title = "Chats", Items = items, Selected = 0 });
                                title = "Matt"; items = MockChat(); selected = Nth(presses(t, 11.6, 0.4, 4));
                            }
                        }
                    }
                }
            }
            hudIcon = t < 27.4 ? "people" : "micoff";
            hudTitle = t < 27.4 ? "Connected" : "Microphone muted";
            hudDetail = t < 4.2 ? "General / Charlton Home" : t < 27.4 ? "Matt" : "";
            double hudAlpha = Math.Max(window(t, 1.0, 4.0), Math.Max(window(t, 24.9, 27.4), window(t, 27.6, 30.2)));
            double banAlpha = DiscordBridge.Incoming && panelX < 0 ? window(t, 21.4, 23.6) : 0;
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
        }
        bg.Dispose();
        PlayniteLink.Pretend = null;
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
