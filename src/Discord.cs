// Discord: drives the Discord desktop app through Windows accessibility (UI Automation), the same
// way a screen reader does, so calls can be joined, muted and left from the controller while
// Discord stays minimised behind Playnite or the game. Discord offers no supported API for this.
// Everything runs on one background thread; the controller and the Party panel queue requests and
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
    public static void Do(Func<string> action, Action<string> done)
    {
        if (!S.Enabled) return;
        work.Add(() =>
        {
            string msg;
            try { msg = action(); } catch (Exception ex) { msg = "Discord: " + ex.Message; }
            if (!string.IsNullOrEmpty(msg)) CouchKit.Log(msg);
            if (done != null) done(msg);
        });
    }

    static void Run()
    {
        bool wasIncoming = false;
        while (true)
        {
            Action job;
            if (work.TryTake(out job, 1500)) { job(); continue; }
            try
            {
                Refresh();
                if (Incoming && !wasIncoming) CouchKit.Log("Discord: incoming call" + (IncomingFrom.Length > 0 ? " from " + IncomingFrom : ""));
                wasIncoming = Incoming;
            }
            catch { }
        }
    }

    // --- State ---

    static void Refresh()
    {
        var windows = Windows();
        Running = windows.Count > 0;
        if (!Running) { InCall = Muted = Deafened = Incoming = false; return; }
        var main = windows[0];
        InCall = Find(main, ControlType.Button, LeaveNames) != null;
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
    public class Chat { public string Name; public bool Group; public int Members; public string Status = ""; }

    // A friend's avatar is labelled "<username>, Online" (or Idle, Do Not Disturb, Offline, ...)
    static readonly Regex StatusLabel = new Regex(@", (?<status>Online|Idle|Do Not Disturb|Offline|Streaming|Invisible)$", RegexOptions.IgnoreCase);

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

    static readonly Regex ChatLink = new Regex(@"^(unread, )?(?<name>.+?) \((?<kind>direct|group) message\)(, (?<members>\d+) Members)?(, (?<status>Online|Idle|Do Not Disturb|Offline|Streaming))?", RegexOptions.IgnoreCase);

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
            int members; int.TryParse(m.Groups["members"].Value, out members);
            string name = m.Groups["name"].Value;
            if (chats.Any(c => c.Name == name)) continue;
            bool group = m.Groups["kind"].Value.Equals("group", StringComparison.OrdinalIgnoreCase);
            string status = m.Groups["status"].Value;
            chats.Add(new Chat { Name = name, Group = group, Members = members, Status = group ? "" : status.Length > 0 ? status : StatusNear(e) });
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

    public static List<string> ListVoiceChannels(string server)
    {
        var main = MainWindow(true);
        var channels = new List<string>();
        if (main == null || !SelectServer(main, server)) return channels;
        var voice = new Regex(@"^(?<name>.+?) \(voice channel\)", RegexOptions.IgnoreCase);
        WaitFor(main, ControlType.Button, voice, 3000);
        foreach (AutomationElement e in main.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)))
        {
            var m = voice.Match(e.Current.Name);
            if (m.Success && !channels.Contains(m.Groups["name"].Value)) channels.Add(m.Groups["name"].Value);
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
        var call = WaitFor(main, ControlType.Button, new Regex("^Start Voice Call$"), 4000);
        if (call == null) return "Discord: couldn't find Start Voice Call for " + friend;
        ((InvokePattern)call.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
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
