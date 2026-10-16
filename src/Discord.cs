// Discord: drives the Discord desktop app through Windows accessibility (UI Automation), the same
// way a screen reader does, so calls can be joined, muted and left from the controller while
// Discord stays minimised behind Playnite or the game. Discord offers no supported API for this.
// Everything runs on one background thread; the controller and the Guide queue requests and
// read the cached state (in a call, muted, deafened, incoming call).
// Relies on Discord's accessible names ("Mute", "Start Voice Call", "<name> (direct message)",
// "<channel> (voice channel)"); a Discord redesign may need these updating.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;

static class DiscordBridge
{
    public static volatile bool Running, InCall, Muted, Deafened, Incoming;
    public static volatile string IncomingFrom = "";
    public static volatile string CallingTo = "";          // outgoing call just started (for the "Calling…" banner)
    public static DateTime CallingSince = DateTime.MinValue;
    public static volatile string CallName = "";           // the call or voice channel you're connected to
    // Things worth a corner HUD message: (icon, title, detail). Set by the Guide.
    public static Action<string, string, string> Notify = (i, t, d) => { };
    // Last known status of people in your DM list (filled whenever the chat list is read)
    public static readonly Dictionary<string, string> Statuses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, int> unread = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    // Unread DM count per person, as last seen in the server bar (0 if none)
    public static int Unread(string name) { lock (unread) { int n; return unread.TryGetValue(name, out n) ? n : 0; } }
    static readonly Regex UnreadItem = new Regex(@"^(?<name>.+?) , (?<n>\d+) unread messages?$", RegexOptions.IgnoreCase);
    static bool callLayoutRecorded, unreadPrimed;
    static readonly BlockingCollection<Action> work = new BlockingCollection<Action>();
    static DiscordSettings S = new DiscordSettings();

    static readonly Regex LeaveNames = new Regex(@"^(Disconnect|Leave Call|Hang Up|End Call)$", RegexOptions.IgnoreCase);
    // Not "Join Voice": that also appears when merely viewing a voice channel
    static readonly Regex AcceptNames = new Regex(@"^(Accept|Accept Call|Join Call|Answer)$", RegexOptions.IgnoreCase);
    static readonly Regex DeclineNames = new Regex(@"^(Decline|Ignore|Dismiss)$", RegexOptions.IgnoreCase);

    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    delegate bool EnumProc(IntPtr hwnd, IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc proc, IntPtr data);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    public static void Start(DiscordSettings settings)
    {
        S = settings;
        if (!S.Enabled) return;
        new Thread(Run) { IsBackground = true, Name = "Discord" }.Start();
    }

    // Queue an action for the Discord thread; result (a short message for the panel/log) via callback
    public static void Do(Func<string> action, Action<string> done) { Do(action, done, true); }

    // keepScreen: if Discord comes to the front during the action, put back whatever was in front
    // (the game or Playnite) and minimise Discord. Off only for deliberately opening Discord.
    // The window that must end up in front after Discord work (the Guide while it's open), and
    // whether a Discord action is running (the Guide leaves the focus alone meanwhile)
    public static Func<IntPtr> KeepInFront = () => IntPtr.Zero;
    public static volatile bool Busy;

    public static void Do(Func<string> action, Action<string> done, bool keepScreen)
    {
        if (!S.Enabled) return;
        work.Add(() =>
        {
            string msg;
            IntPtr before = GetForegroundWindow();
            Busy = true;
            try { msg = action(); } catch (Exception ex) { msg = "Discord: " + ex.Message; }
            finally { Busy = false; }
            if (keepScreen) PutScreenBack(before);
            if (!string.IsNullOrEmpty(msg)) CouchKit.Log(msg);
            if (done != null) done(msg);
        });
    }

    static void Run()
    {
        bool wasIncoming = false, first = true, wasInCall = false, wasMuted = false, wasDeafened = false;
        while (true)
        {
            Action job;
            if (work.TryTake(out job, 1500)) { job(); continue; }
            try
            {
                Refresh();
                if (Incoming && !wasIncoming) CouchKit.Log("Discord: incoming call" + (IncomingFrom.Length > 0 ? " from " + IncomingFrom : ""));
                // Corner HUD messages for changes (not on the very first look)
                if (!first && Running)
                {
                    if (InCall && !wasInCall) Notify("call", "Connected", CallName);
                    if (!InCall && wasInCall) Notify("hangup", "Call ended", "");
                    if (Muted != wasMuted) Notify(Muted ? "micoff" : "mic", Muted ? "Microphone muted" : "Microphone live", "");
                    if (Deafened != wasDeafened) Notify(Deafened ? "deafened" : "headset", Deafened ? "Deafened" : "Sound back on", "");
                }
                if (InCall && !callLayoutRecorded) { callLayoutRecorded = true; RecordCallLayout(); }
                wasIncoming = Incoming; wasInCall = InCall; wasMuted = Muted; wasDeafened = Deafened; first = false;
            }
            catch { }
        }
    }

    // New direct messages show in Discord's server bar as "<name> , N unread messages"
    static void CheckUnread(AutomationElement main, bool announce)
    {
        var now = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (AutomationElement e in main.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TreeItem)))
        {
            var m = UnreadItem.Match(e.Current.Name);
            if (m.Success) now[m.Groups["name"].Value.Trim()] = int.Parse(m.Groups["n"].Value);
        }
        foreach (var kv in now)
        {
            int before;
            unread.TryGetValue(kv.Key, out before);
            if (announce && kv.Value > before) Notify("message", kv.Key, kv.Value == 1 ? "1 new message" : kv.Value + " new messages");
        }
        lock (unread)
        {
            unread.Clear();
            foreach (var kv in now) unread[kv.Key] = kv.Value;
        }
    }

    // Once per run, when first in a call: write which controls Discord shows during a call
    // (types and names only; no message text), so "who's in the call" can be matched to it.
    static void RecordCallLayout()
    {
        try
        {
            var main = Windows().FirstOrDefault();
            if (main == null) return;
            var lines = new List<string> { "Discord call layout " + DateTime.Now.ToString("yyyy-MM-dd HH:mm") };
            var types = new[] { ControlType.Button, ControlType.Image, ControlType.Hyperlink, ControlType.TreeItem, ControlType.ListItem, ControlType.Group };
            foreach (AutomationElement e in main.FindAll(TreeScope.Descendants, Condition.TrueCondition))
            {
                var c = e.Current;
                if (!types.Contains(c.ControlType) || string.IsNullOrWhiteSpace(c.Name)) continue;
                if ((c.AutomationId ?? "").StartsWith("chat-messages")) continue;   // message contents stay private
                if (c.ControlType == ControlType.ListItem && c.Name.Length > 60) continue;
                lines.Add(c.ControlType.ProgrammaticName.Replace("ControlType.", "") + " \"" + c.Name + "\" id=" + c.AutomationId);
            }
            File.WriteAllLines(Path.Combine(Settings.DataDir, "discord-call-layout.txt"), lines);
            CouchKit.Log("Discord: recorded the call layout for who's-in-the-call support");
        }
        catch (Exception ex) { CouchKit.Log("Discord: call layout not recorded: " + ex.Message); }
    }

    // The call or channel name from Discord's "Voice Connected" panel
    static string FindCallName(AutomationElement main)
    {
        try
        {
            var vc = main.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "Voice Connected"));
            if (vc == null) return "";
            var parent = TreeWalker.ControlViewWalker.GetParent(vc);
            for (int i = 0; i < 3 && parent != null; i++)
            {
                foreach (AutomationElement e in parent.FindAll(TreeScope.Descendants, Condition.TrueCondition))
                {
                    string n = e.Current.Name;
                    if (string.IsNullOrWhiteSpace(n) || n == "Voice Connected" || LeaveNames.IsMatch(n) || e.Current.ControlType == ControlType.Button && !n.Contains("/")) continue;
                    if (n.Length > 2 && n.Length < 80) return n;
                }
                parent = TreeWalker.ControlViewWalker.GetParent(parent);
            }
        }
        catch { }
        return "";
    }

    // --- State ---

    static void Refresh()
    {
        var windows = Windows();
        Running = windows.Count > 0;
        if (!Running) { InCall = Muted = Deafened = Incoming = false; return; }
        var main = windows[0];
        bool inCall = Find(main, ControlType.Button, LeaveNames) != null;
        if (inCall && (!InCall || CallName.Length == 0)) CallName = FindCallName(main);
        if (!inCall) CallName = "";
        InCall = inCall;
        CheckUnread(main, Running && unreadPrimed);
        unreadPrimed = true;
        Muted = IsOn(Find(main, ControlType.Button, "Mute"));
        Deafened = IsOn(Find(main, ControlType.Button, "Deafen"));
        // A real ring offers Decline/Ignore as well. "Join Call" on its own is just a call still going
        // on in a chat you left, which isn't an incoming call.
        AutomationElement accept = null;
        foreach (var w in windows)
        {
            var a = Find(w, ControlType.Button, AcceptNames);
            if (a == null) continue;
            bool ringing = Find(w, ControlType.Button, DeclineNames) != null || !a.Current.Name.Equals("Join Call", StringComparison.OrdinalIgnoreCase);
            if (ringing) { accept = a; break; }
        }
        Incoming = accept != null && !InCall;
        IncomingFrom = Incoming ? CallerName(accept) : "";
    }

    static string CallerName(AutomationElement accept)
    {
        try
        {
            var parent = TreeWalker.ControlViewWalker.GetParent(accept);
            for (int i = 0; i < 3 && parent != null; i++)
            {
                var text = parent.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text))
                    .Cast<AutomationElement>().Select(t => t.Current.Name).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n) && !AcceptNames.IsMatch(n) && !DeclineNames.IsMatch(n));
                if (text != null && !Regex.IsMatch(text, "^(Notifications|Direct Messages|Friends|Inbox|Discord)$", RegexOptions.IgnoreCase)) return text;
                parent = TreeWalker.ControlViewWalker.GetParent(parent);
            }
        }
        catch { }
        return "";
    }

    // --- Actions (run on the Discord thread via Do) ---

    public static string ToggleMute() { return Toggle("Mute", "Microphone"); }
    public static string ToggleDeafen() { return Toggle("Deafen", "Sound"); }

    static string Toggle(string button, string label)
    {
        var main = MainWindow(true);
        if (main == null) return "Discord isn't running";
        var b = Find(main, ControlType.Button, button);
        if (b == null) return "Discord: " + button + " button not found";
        ((TogglePattern)b.GetCurrentPattern(TogglePattern.Pattern)).Toggle();
        Thread.Sleep(1000);   // a minimised Discord updates its state about a second later
        b = Find(main, ControlType.Button, button);
        bool on = IsOn(b);
        if (button == "Mute") Muted = on; else Deafened = on;
        return "Discord: " + (button == "Mute" ? (on ? "microphone muted" : "microphone live") : (on ? "deafened" : "undeafened"));
    }

    // Recent chats from Discord's direct messages list: friends and group chats, newest first.
    public class Chat { public string Name; public bool Group; public int Members; public string Status = "", Activity = ""; public bool InCall; }
    public class Message { public string Author, Text; }

    // A friend's avatar is labelled "<username>, Online" (or Idle, Do Not Disturb, Offline, ...)
    static readonly Regex StatusLabel = new Regex(@", (?<status>Online|Idle|Do Not Disturb|Offline|Streaming|Invisible)$", RegexOptions.IgnoreCase);

    // A friend's activity line in the DM list ("Playing Valorant", "Listening to Spotify"...)
    static readonly Regex ActivityText = new Regex(@"^(Playing|Listening to|Watching|Streaming|Competing in|In a call)\b", RegexOptions.IgnoreCase);
    static string ActivityNear(AutomationElement link, string name)
    {
        try
        {
            var item = TreeWalker.ControlViewWalker.GetParent(TreeWalker.ControlViewWalker.GetParent(link) ?? link);
            if (item == null) return "";
            foreach (AutomationElement e in item.FindAll(TreeScope.Descendants, Condition.TrueCondition))
            {
                string n = e.Current.Name;
                if (string.IsNullOrEmpty(n) || n == name) continue;
                var m = Regex.Match(n, @"^(In a call) \(");
                if (m.Success) return "in a call";
                if (ActivityText.IsMatch(n) && n.Length < 80) return n;
            }
        }
        catch { }
        return "";
    }

    static string StatusNear(AutomationElement link)
    {
        try
        {
            var parent = TreeWalker.ControlViewWalker.GetParent(link);
            for (int i = 0; i < 2 && parent != null; i++)
            {
                foreach (AutomationElement img in parent.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Image)))
                {
                    var m = StatusLabel.Match(img.Current.Name);
                    if (m.Success) return m.Groups["status"].Value;
                }
                parent = TreeWalker.ControlViewWalker.GetParent(parent);
            }
        }
        catch { }
        return "";
    }

    // e.g. "unread, Matt (direct message), Ongoing Call, Online" or "Spod, Liam (group message), 3 Members"
    static readonly Regex ChatLink = new Regex(@"^(unread, )?(?<name>.+?) \((?<kind>direct|group) message\)(?<rest>.*)$", RegexOptions.IgnoreCase);
    static readonly Regex Members = new Regex(@", (?<n>\d+) Members", RegexOptions.IgnoreCase);
    static readonly Regex StatusSuffix = new Regex(@", (?<status>Online|Idle|Do Not Disturb|Offline|Streaming)\b", RegexOptions.IgnoreCase);

    public static List<Chat> ListChats()
    {
        var main = MainWindow(true);
        var chats = new List<Chat>();
        if (main == null) return chats;
        GoToDirectMessages(main);
        WaitFor(main, ControlType.Hyperlink, ChatLink, 3000);
        foreach (AutomationElement e in main.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Hyperlink)))
        {
            var m = ChatLink.Match(e.Current.Name);
            if (!m.Success) continue;
            string rest = m.Groups["rest"].Value;
            int members; int.TryParse(Members.Match(rest).Groups["n"].Value, out members);
            string name = m.Groups["name"].Value;
            if (chats.Any(c => c.Name == name)) continue;
            bool group = m.Groups["kind"].Value.Equals("group", StringComparison.OrdinalIgnoreCase);
            string status = StatusSuffix.Match(rest).Groups["status"].Value;
            var chat = new Chat { Name = name, Group = group, Members = members, Status = group ? "" : status.Length > 0 ? status : StatusNear(e),
                                  InCall = rest.IndexOf("Ongoing Call", StringComparison.OrdinalIgnoreCase) >= 0, Activity = ActivityNear(e, name) };
            chats.Add(chat);
            if (!group) lock (Statuses) Statuses[name] = chat.Status;
        }
        // People who are around first (keeping Discord's most-recent order within each group)
        Func<Chat, int> rank = c => c.Group ? 1 : c.Status == "Online" || c.Status == "Streaming" ? 0 : c.Status == "Idle" || c.Status == "Do Not Disturb" ? 1 : 2;
        return chats.Select((c, i) => new { c, i }).OrderBy(x => rank(x.c)).ThenBy(x => x.i).Select(x => x.c).ToList();
    }

    // Servers in Discord's sidebar, with the notification prefixes and suffixes removed.
    public static List<string> ListServers()
    {
        var main = MainWindow(true);
        var servers = new List<string>();
        if (main == null) return servers;
        foreach (AutomationElement e in main.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TreeItem)))
        {
            string n = e.Current.Name;
            if (n.StartsWith("Direct Messages") || n == "Add a Server" || n == "Discover" || n.Contains(", folder ")) continue;
            n = Regex.Replace(n, @"^((\d+ mentions?|Unread messages), )+", "");
            n = Regex.Replace(n, @"(, (Scheduled event|Live|Live stage|Voice channel active|\d+ unread))+$", "");
            if (n.Length > 0 && !servers.Contains(n)) servers.Add(n);
        }
        return servers;
    }

    public class Channel { public string Name; public List<string> People = new List<string>(); }

    // A server's voice channels and who's in each (people are listed inside the channel's entry)
    public static List<Channel> ListVoiceChannels(string server)
    {
        var main = MainWindow(true);
        var channels = new List<Channel>();
        if (main == null || !SelectServer(main, server)) return channels;
        var voice = new Regex(@"^(?<name>.+?) \(voice channel\)", RegexOptions.IgnoreCase);
        var ignore = new Regex(@"^(Voice icon|Open Chat|Invite to Channel|Edit Channel|Voice|.*\(voice channel\).*|Muted|Deafened|Server Muted|Server Deafened|LIVE|Live|Video|Streaming)$", RegexOptions.IgnoreCase);
        WaitFor(main, ControlType.Button, voice, 3000);
        foreach (AutomationElement e in main.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)))
        {
            var m = voice.Match(e.Current.Name);
            if (!m.Success || channels.Any(c => c.Name == m.Groups["name"].Value)) continue;
            var ch = new Channel { Name = m.Groups["name"].Value };
            try
            {
                var item = TreeWalker.ControlViewWalker.GetParent(e);
                if (item != null)
                    foreach (AutomationElement p in item.FindAll(TreeScope.Descendants, Condition.TrueCondition))
                    {
                        string n = p.Current.Name;
                        if (string.IsNullOrWhiteSpace(n) || ignore.IsMatch(n) || p.Current.ControlType == ControlType.Image) continue;
                        if ((p.Current.ControlType == ControlType.Hyperlink || p.Current.ControlType == ControlType.Button || p.Current.ControlType == ControlType.Text || p.Current.ControlType == ControlType.ListItem) && !ch.People.Contains(n) && n.Length < 40)
                            ch.People.Add(n);
                    }
            }
            catch { }
            channels.Add(ch);
        }
        return channels;
    }

    static bool SelectServer(AutomationElement main, string server)
    {
        var srv = main.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TreeItem))
            .Cast<AutomationElement>().FirstOrDefault(e => Regex.IsMatch(e.Current.Name, @"(^|, )" + Regex.Escape(server) + @"($|,)", RegexOptions.IgnoreCase));
        if (srv == null) return false;
        ((SelectionItemPattern)srv.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
        return true;
    }

    static readonly Regex Timestamp = new Regex(@"^(\d{1,2}:\d{2}( [AP]M)?|\d{1,2}/\d{1,2}/\d{2,4},? \d{1,2}:\d{2}( [AP]M)?|(Today|Yesterday) at .+|(Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday), .+|\(edited\)|edited)$", RegexOptions.IgnoreCase);

    // The last few messages in a DM or group chat (opening it marks it read, as in Discord itself)
    public static List<Message> ReadMessages(string chat, int count)
    {
        var list = new List<Message>();
        var main = MainWindow(true);
        if (main == null || !OpenChat(main, chat)) return list;
        WaitFor(main, ControlType.ListItem, new Regex("."), 1500);
        var items = main.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem))
            .Cast<AutomationElement>().Where(e => (e.Current.AutomationId ?? "").StartsWith("chat-messages-")).ToList();
        string author = "";
        foreach (var item in items.Skip(Math.Max(0, items.Count - count * 2)))
        {
            var parts = item.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>().ToList();
            int authorAt = parts.FindIndex(p => p.Current.ControlType == ControlType.Hyperlink && p.GetSupportedPatterns().Contains(ExpandCollapsePattern.Pattern));
            if (authorAt >= 0) author = parts[authorAt].Current.Name;
            var text = parts.Skip(authorAt + 1).Where(p => p.Current.ControlType == ControlType.Text).Select(p => p.Current.Name.Trim())
                .Where(t => t.Length > 0 && !Timestamp.IsMatch(t) && t != author).ToList();
            if (text.Count == 0) continue;
            list.Add(new Message { Author = author, Text = string.Join(" ", text) });
        }
        return list.Skip(Math.Max(0, list.Count - count)).ToList();
    }

    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    // Sends a message: Discord briefly comes forward to type it (pasted, then Enter), then the
    // screen is put back by Do(). Discord has no other way in for typing.
    public static string SendMessage(string chat, string text)
    {
        var main = MainWindow(true);
        if (main == null || !OpenChat(main, chat)) return "Discord: couldn't open the chat with " + chat;
        var box = WaitFor(main, ControlType.Edit, new Regex("^Message "), 3000);
        if (box == null) return "Discord: couldn't find the message box";
        IntPtr hwnd = Process.GetProcessesByName("Discord").Select(p => p.MainWindowHandle).FirstOrDefault(h => h != IntPtr.Zero);
        ShowWindow(hwnd, 9 /* SW_RESTORE */);
        CouchKit.BringToFront(hwnd);
        Thread.Sleep(250);
        box.SetFocus();
        Thread.Sleep(100);
        string previous = null;
        RunSta(() => { try { if (System.Windows.Forms.Clipboard.ContainsText()) previous = System.Windows.Forms.Clipboard.GetText(); System.Windows.Forms.Clipboard.SetText(text); } catch { } });
        keybd_event(0x11, 0, 0, UIntPtr.Zero); keybd_event(0x56, 0, 0, UIntPtr.Zero);   // Ctrl+V
        keybd_event(0x56, 0, 2, UIntPtr.Zero); keybd_event(0x11, 0, 2, UIntPtr.Zero);
        Thread.Sleep(150);
        keybd_event(0x0D, 0, 0, UIntPtr.Zero); keybd_event(0x0D, 0, 2, UIntPtr.Zero);    // Enter
        Thread.Sleep(200);
        RunSta(() => { try { if (previous != null) System.Windows.Forms.Clipboard.SetText(previous); else System.Windows.Forms.Clipboard.Clear(); } catch { } });
        return "Discord: sent \"" + text + "\" to " + chat;
    }

    static void RunSta(Action a)
    {
        var t = new Thread(() => a());
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join(2000);
    }

    static bool OpenChat(AutomationElement main, string chat)
    {
        GoToDirectMessages(main);
        var link = WaitFor(main, ControlType.Hyperlink, new Regex("^(unread, )?" + Regex.Escape(chat) + @" \((direct|group) message\)", RegexOptions.IgnoreCase), 3000);
        if (link == null) return false;
        ((InvokePattern)link.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        Thread.Sleep(600);
        return true;
    }

    // Starts a voice call in a DM or group chat (everyone in a group chat is rung).
    public static string CallFriend(string friend)
    {
        var main = MainWindow(true);
        if (main == null) return "Discord isn't running";
        if (InCall) Leave();
        GoToDirectMessages(main);
        var link = WaitFor(main, ControlType.Hyperlink, new Regex("^(unread, )?" + Regex.Escape(friend) + @" \((direct|group) message\)", RegexOptions.IgnoreCase), 3000);
        if (link == null) return "Discord: no recent chat with " + friend + " (open a chat with them once in Discord)";
        ((InvokePattern)link.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        // "Join Call" instead when a call is still going in that chat (e.g. after leaving it)
        var call = WaitFor(main, ControlType.Button, new Regex("^(Start Voice Call|Join Call)$"), 4000);
        if (call == null) return "Discord: couldn't find a call button for " + friend;
        bool rejoin = call.Current.Name == "Join Call";
        ((InvokePattern)call.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        if (rejoin) return "Discord: rejoined the call with " + friend;
        CallingTo = friend;
        CallingSince = DateTime.Now;
        return "Discord: calling " + friend;
    }

    public static string JoinVoice(string server, string channel)
    {
        var main = MainWindow(true);
        if (main == null) return "Discord isn't running";
        if (!SelectServer(main, server)) return "Discord: server " + server + " not found";
        var ch = WaitFor(main, ControlType.Button, new Regex("^" + Regex.Escape(channel) + @" \(voice channel\)", RegexOptions.IgnoreCase), 4000);
        if (ch == null) return "Discord: voice channel " + channel + " not found in " + server;
        ((InvokePattern)ch.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        return "Discord: joined " + server + " / " + channel;
    }

    public static string Leave()
    {
        var main = MainWindow(false);
        if (main == null) return "Discord isn't running";
        var b = Find(main, ControlType.Button, LeaveNames);
        if (b == null) return "Discord: not in a call";
        ((InvokePattern)b.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        InCall = false;
        return "Discord: left the call";
    }

    public static string Answer() { return PressInAnyWindow(AcceptNames, "answered the call", "no incoming call"); }

    // Starts Discord (minimised) if it isn't running or is only in the tray
    public static string StartDiscord() { return MainWindow(true) != null ? "Discord: ready" : "Discord: couldn't start it"; }
    public static string Decline() { return PressInAnyWindow(DeclineNames, "declined the call", "no incoming call"); }

    static string PressInAnyWindow(Regex names, string ok, string none)
    {
        foreach (var w in Windows())
        {
            var b = Find(w, ControlType.Button, names);
            if (b == null) continue;
            ((InvokePattern)b.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            Incoming = false;
            return "Discord: " + ok;
        }
        return "Discord: " + none;
    }

    static void PutScreenBack(IntPtr before)
    {
        try
        {
            var discord = new HashSet<uint>(Process.GetProcessesByName("Discord").Select(p => (uint)p.Id));
            uint fgPid, beforePid;
            GetWindowThreadProcessId(GetForegroundWindow(), out fgPid);
            GetWindowThreadProcessId(before, out beforePid);
            // While the Guide is open it's what belongs in front, not whatever was there when the work
            // was queued (that can be Playnite, if the work started before the Guide appeared)
            IntPtr guide = KeepInFront();
            if (guide != IntPtr.Zero) { before = guide; beforePid = 0; }
            if (!discord.Contains(fgPid) || discord.Contains(beforePid) || before == IntPtr.Zero) return;
            IntPtr main = Process.GetProcessesByName("Discord").Select(p => p.MainWindowHandle).FirstOrDefault(h => h != IntPtr.Zero);
            if (main != IntPtr.Zero) ShowWindow(main, 6 /* SW_MINIMIZE */);
            CouchKit.BringToFront(before);
        }
        catch { }
    }

    static void GoToDirectMessages(AutomationElement main)
    {
        var dms = main.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TreeItem))
            .Cast<AutomationElement>().FirstOrDefault(e => e.Current.Name.StartsWith("Direct Messages", StringComparison.OrdinalIgnoreCase));
        if (dms != null) { ((SelectionItemPattern)dms.GetCurrentPattern(SelectionItemPattern.Pattern)).Select(); Thread.Sleep(500); }
    }

    // --- Windows and element helpers ---

    // Discord's top-level windows (main window first). Empty if Discord isn't running or only in the tray.
    static List<AutomationElement> Windows()
    {
        var pids = new HashSet<uint>(Process.GetProcessesByName("Discord").Select(p => (uint)p.Id));
        var list = new List<AutomationElement>();
        if (pids.Count == 0) return list;
        IntPtr mainHwnd = Process.GetProcessesByName("Discord").Select(p => p.MainWindowHandle).FirstOrDefault(h => h != IntPtr.Zero);
        var handles = new List<IntPtr>();
        EnumWindows((h, d) => { uint pid; GetWindowThreadProcessId(h, out pid); if (pids.Contains(pid) && (IsWindowVisible(h) || h == mainHwnd)) handles.Add(h); return true; }, IntPtr.Zero);
        if (mainHwnd != IntPtr.Zero) { handles.Remove(mainHwnd); handles.Insert(0, mainHwnd); }
        foreach (var h in handles) { try { list.Add(AutomationElement.FromHandle(h)); } catch { } }
        return list;
    }

    // The main window; if Discord is only in the tray (or not running), open it, then minimise it
    // and give the focus back to whatever had it.
    static AutomationElement MainWindow(bool openIfNeeded)
    {
        var w = Windows();
        if (w.Count > 0 || !openIfNeeded) return w.FirstOrDefault();
        string updater = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Discord", "Update.exe");
        if (!File.Exists(updater)) return null;
        IntPtr before = GetForegroundWindow();
        Process.Start(updater, "--processStart Discord.exe");
        for (int i = 0; i < 60; i++)
        {
            Thread.Sleep(500);
            IntPtr h = Process.GetProcessesByName("Discord").Select(p => p.MainWindowHandle).FirstOrDefault(x => x != IntPtr.Zero);
            if (h == IntPtr.Zero) continue;
            Thread.Sleep(2000);   // let it finish loading
            ShowWindow(h, 6 /* SW_MINIMIZE */);
            if (KeepInFront() != IntPtr.Zero) before = KeepInFront();
            if (before != IntPtr.Zero) CouchKit.BringToFront(before);
            return AutomationElement.FromHandle(h);
        }
        return null;
    }

    static AutomationElement Find(AutomationElement root, ControlType type, string name)
    {
        return root.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, type),
            new PropertyCondition(AutomationElement.NameProperty, name)));
    }

    static AutomationElement Find(AutomationElement root, ControlType type, Regex name)
    {
        return root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, type))
            .Cast<AutomationElement>().FirstOrDefault(e => name.IsMatch(e.Current.Name));
    }

    static AutomationElement WaitFor(AutomationElement root, ControlType type, Regex name, int ms)
    {
        var sw = Stopwatch.StartNew();
        do
        {
            var e = Find(root, type, name);
            if (e != null) return e;
            Thread.Sleep(250);
        } while (sw.ElapsedMilliseconds < ms);
        return null;
    }

    static bool IsOn(AutomationElement toggle)
    {
        if (toggle == null) return false;
        try { return ((TogglePattern)toggle.GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState == ToggleState.On; }
        catch { return false; }
    }
}
