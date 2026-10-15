// The CouchKit settings page in Playnite (Add-ons → Extension settings → CouchKit). It edits
// %APPDATA%\CouchKit\couchkit.json in place, so anything it doesn't show (and any comments-free
// hand edits) is kept, and restarts CouchKit when you save so the changes apply straight away.
// The page is built in code from Playnite's own control styles, so it follows the Playnite theme.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Playnite.SDK;

public class CouchKitSettings : ISettings
{
    static readonly ILogger logger = LogManager.GetLogger();
    public static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CouchKit");
    public static readonly string FilePath = Path.Combine(DataDir, "couchkit.json");

    public JObject Working;          // what the page edits
    public Action Saved = () => { };

    public static JObject Load()
    {
        try { if (File.Exists(FilePath)) return JObject.Parse(File.ReadAllText(FilePath)); }
        catch (Exception ex) { logger.Error(ex, "CouchKit: couchkit.json could not be read"); }
        // First time: start from the example settings bundled with the add-on
        try
        {
            string example = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "couchkit.example.json");
            if (File.Exists(example)) return JObject.Parse(File.ReadAllText(example));
        }
        catch { }
        return new JObject();
    }

    public void BeginEdit() { Working = Load(); }
    public void CancelEdit() { Working = null; }

    public void EndEdit()
    {
        if (Working == null) return;
        Directory.CreateDirectory(DataDir);
        if (File.Exists(FilePath)) File.Copy(FilePath, FilePath + ".bak", true);   // one step of undo
        File.WriteAllText(FilePath, Working.ToString(Formatting.Indented));
        logger.Info("CouchKit: settings saved");
        Saved();
    }

    public bool VerifySettings(out List<string> errors)
    {
        errors = new List<string>();
        if (Working == null) return true;
        var palette = Working.SelectToken("Lighting.Palette") as JArray;
        if (palette != null)
            foreach (var c in palette)
                if (!Regex.IsMatch((string)c ?? "", "^#[0-9a-fA-F]{6}$")) errors.Add("Lighting colours must look like #75dddd (\"" + c + "\" doesn't).");
        foreach (var key in new[] { "SmartHome.HomeAssistantUrl", "SmartHome.WebhookUrl" })
        {
            string url = (string)Working.SelectToken(key);
            if (!string.IsNullOrEmpty(url) && !url.StartsWith("http://") && !url.StartsWith("https://")) errors.Add(key.Split('.')[1] + " should start with http:// or https://");
        }
        return errors.Count == 0;
    }

    // --- Reading and writing "Section.Key" paths, creating sections as needed ---
    public JToken Get(string path) { return Working == null ? null : Working.SelectToken(path); }
    public void Set(string path, JToken value)
    {
        var parts = path.Split('.');
        JObject node = Working;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            var next = node[parts[i]] as JObject;
            if (next == null) { next = new JObject(); node[parts[i]] = next; }
            node = next;
        }
        node[parts[parts.Length - 1]] = value;
    }
}

public class SettingsPage : UserControl
{
    readonly CouchKitSettings S;
    readonly StackPanel page = new StackPanel { Margin = new Thickness(20, 10, 20, 20) };
    List<string> audioDevices = new List<string>();

    public SettingsPage(CouchKitSettings settings)
    {
        S = settings;
        Content = new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        // Build when shown: Playnite loads the settings (BeginEdit) around the time it shows the page
        Loaded += (s, e) => { if (S.Working == null) S.BeginEdit(); Build(); };
        ListDevices();
    }

    // Audio outputs come from CouchKit.exe itself (64-bit, with the Windows audio code)
    void ListDevices()
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                string exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CouchKit", "CouchKit.exe");
                string file = Path.Combine(Path.GetTempPath(), "couchkit-list.json");
                if (!File.Exists(exe)) return;
                using (var p = Process.Start(new ProcessStartInfo(exe, "--list \"" + file + "\"") { UseShellExecute = false, CreateNoWindow = true })) p.WaitForExit(8000);
                var list = JObject.Parse(File.ReadAllText(file))["Audio"] as JArray;
                if (list != null) audioDevices = list.Select(t => (string)t).ToList();
                Dispatcher.BeginInvoke(new Action(() => { if (IsLoaded) Build(); }));
            }
            catch { }
        });
    }

    // --- Page ---

    void Build()
    {
        page.Children.Clear();
        Intro("CouchKit makes this PC behave like a games console on the TV. Changes apply when you save: CouchKit restarts with them. " +
              "Everything here is stored in " + CouchKitSettings.FilePath + ".");
        Buttons(Button("Open settings file", () => Process.Start("notepad.exe", "\"" + CouchKitSettings.FilePath + "\"")),
                Button("Open log", () => { string log = Path.Combine(CouchKitSettings.DataDir, "CouchKit.log"); if (File.Exists(log)) Process.Start("notepad.exe", "\"" + log + "\""); }),
                Button("Project page", () => Process.Start("https://github.com/dancharlton9/CouchKit")));

        Section("Controller");
        Check("Controller.Enabled", "Use the controller for CouchKit (Xbox button, close game)");
        Check("Controller.GuideButton", "Tap the Xbox button to switch between the game and Playnite");
        Check("Controller.DiscordButtons", "Double-tap the Xbox button to open the Guide");
        Choice("Controller.Style", "Button style in the Guide", new[] { "Auto", "Xbox", "PlayStation", "Switch", "Generic" },
               "Auto spots PlayStation and Switch controllers (used through Steam Input or DS4Windows).");
        Check("Controller.MouseMode", "Allow mouse mode (left stick moves the pointer) from the Guide");
        Note("In Playnite's fullscreen settings, turn off \"Guide button focuses Playnite\": CouchKit handles the Xbox button.");

        Section("The Guide");
        Check("Guide.Sounds", "Sounds (clicks and chimes)");
        Slider("Guide.SoundVolume", "Sound volume", 0, 100, "%", 60);
        Check("Guide.Animations", "Animations (sliding highlight, fades)");

        Section("TV and sound");
        Check("Display.KeepPlayniteOnTv", "Keep Playnite on the TV (off streaming screens)");
        Check("Display.KeepGameOnTv", "Keep games on the TV too");
        Check("Display.MuteWhileStreaming", "Mute the PC while streaming to another device");
        Device("Audio.TvDeviceName", "TV sound output", "Sound goes back here when the TV is the only screen. Part of the name is enough.");
        Device("Audio.HeadsetDeviceName", "Headset", "While it's switched on, sound and the microphone go to it.");

        Section("Lighting (OpenRGB)");
        Check("Lighting.Enabled", "Animate the PC's RGB lighting");
        Check("Lighting.GameColours", "Use colours from the game's artwork while it runs");
        Lines("Lighting.Palette", "Colours while browsing", "One per line, like #75dddd.", new[] { "#75dddd", "#84c7d0", "#9297c4", "#9368b7", "#aa3e98" });
        Slider("Lighting.BrowseCycleSeconds", "Speed (seconds per sweep)", 2, 30, " s", 9);
        Note("Needs OpenRGB running with its SDK server on (OpenRGB → SDK Server → Start Server; tick it to start automatically).");

        Section("Home Assistant");
        Text("SmartHome.HomeAssistantUrl", "Address", "For example http://homeassistant.local:8123");
        Secret("SmartHome.HomeAssistantToken", "Access token", "In Home Assistant: your profile → Security → Long-lived access tokens → Create token.");
        Lines("SmartHome.LightScenes", "Scenes for the Lights tab", "One per line: scene.movie_night, script.party…");
        Lines("SmartHome.RoomLights", "Room lights", "Switched by \"Room lights on/off\": light.ceiling, light.lamp…");
        Secret("SmartHome.WebhookUrl", "Game-colours webhook", "Optional: an automation's webhook URL, sent the game's colours when it starts.");
        Buttons(Button("Test Home Assistant", () => TestHomeAssistant()));

        Section("Nanoleaf");
        Text("SmartHome.NanoleafHost", "Panels' IP address", "Find it in your router, or in the Nanoleaf app → device → Info.");
        Secret("SmartHome.NanoleafToken", "Token", "Use Pair below to get one.");
        Buttons(Button("Pair Nanoleaf", () => PairNanoleaf()));
        Note("To pair: hold the panels' power button for 5–7 seconds until the lights flash, then press Pair within 30 seconds.");

        Section("Discord");
        Check("Discord.Enabled", "Discord in the Guide's Party tab (English Discord only)");
        Favourites();
        Lines("Discord.QuickReplies", "Quick replies", "One per line.", new[] { "On my way", "Give me 5 mins", "In a game – call me?", "Can't talk right now", "👍" });

        Section("Achievements");
        Check("Achievements.Enabled", "Show achievements in the Guide (needs the PlayniteAchievements add-on)");
        Check("Achievements.Popups", "Pop up on the TV when you unlock one");

        Section("Games");
        Lines("Games.EmulatorFolders", "Emulator folders", "Where your emulators run from, one per line, so CouchKit knows emulated games are games.");

        Section("CPU temperature");
        Note("The System tab can show the CPU temperature from FanControl. That needs a small helper that starts with Windows with admin rights; it only reads FanControl's sensors.");
        Buttons(Button("Set up the temperature helper", () => SetUpSensors()));
    }

    // --- Building blocks ---

    void Intro(string text) { page.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10), Opacity = 0.85 }); }

    void Section(string title)
    {
        page.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 22, 0, 8) });
    }

    void Note(string text) { page.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.7, Margin = new Thickness(0, 4, 0, 6) }); }

    // A row: label on the left, control on the right, an optional hint under the control
    void Row(string label, FrameworkElement control, string hint)
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var l = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 12, 0), TextWrapping = TextWrapping.Wrap };
        var right = new StackPanel();
        right.Children.Add(control);
        if (!string.IsNullOrEmpty(hint)) right.Children.Add(new TextBlock { Text = hint, Opacity = 0.65, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0), FontSize = 12 });
        Grid.SetColumn(right, 1);
        grid.Children.Add(l); grid.Children.Add(right);
        page.Children.Add(grid);
    }

    void Check(string path, string label)
    {
        var t = S.Get(path);
        var box = new CheckBox { Content = label, IsChecked = t == null || t.Type != JTokenType.Boolean || (bool)t, Margin = new Thickness(0, 4, 0, 4) };
        box.Checked += (s, e) => S.Set(path, true);
        box.Unchecked += (s, e) => S.Set(path, false);
        page.Children.Add(box);
    }

    void Choice(string path, string label, string[] options, string hint)
    {
        var combo = new ComboBox { ItemsSource = options, MaxWidth = 300, HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 200 };
        string current = (string)S.Get(path) ?? options[0];
        combo.SelectedItem = options.FirstOrDefault(o => o.Equals(current, StringComparison.OrdinalIgnoreCase)) ?? options[0];
        combo.SelectionChanged += (s, e) => S.Set(path, (string)combo.SelectedItem);
        Row(label, combo, hint);
    }

    void Device(string path, string label, string hint)
    {
        string current = (string)S.Get(path) ?? "";
        var items = new List<string> { "" };
        items.AddRange(audioDevices);
        if (current.Length > 0 && !audioDevices.Any(d => d.IndexOf(current, StringComparison.OrdinalIgnoreCase) >= 0)) items.Add(current);
        var combo = new ComboBox { ItemsSource = items, IsEditable = true, Text = current, MinWidth = 320, HorizontalAlignment = HorizontalAlignment.Left };
        // Store just the distinctive part: "Speakers (LG TV SSCR2)" → "LG TV SSCR2"
        combo.SelectionChanged += (s, e) =>
        {
            string pick = combo.SelectedItem as string ?? "";
            var m = Regex.Match(pick, @"\(([^)]+)\)\s*$");
            S.Set(path, m.Success ? m.Groups[1].Value : pick);
        };
        combo.LostFocus += (s, e) => { if (combo.SelectedItem == null) S.Set(path, combo.Text.Trim()); };
        Row(label, combo, hint + (audioDevices.Count == 0 ? " (Looking for sound devices…)" : ""));
    }

    void Text(string path, string label, string hint)
    {
        var box = new TextBox { Text = (string)S.Get(path) ?? "", MinWidth = 320 };
        box.TextChanged += (s, e) => S.Set(path, box.Text.Trim());
        Row(label, box, hint);
    }

    void Secret(string path, string label, string hint)
    {
        var box = new PasswordBox { Password = (string)S.Get(path) ?? "", MinWidth = 320, HorizontalAlignment = HorizontalAlignment.Left, Width = 420 };
        box.PasswordChanged += (s, e) => S.Set(path, box.Password.Trim());
        Row(label, box, hint);
    }

    void Lines(string path, string label, string hint, string[] defaults = null)
    {
        var arr = S.Get(path) as JArray;
        var shown = arr != null ? arr.Select(t => (string)t).ToArray() : defaults ?? new string[0];
        var box = new TextBox { Text = string.Join(Environment.NewLine, shown), AcceptsReturn = true, MinHeight = 70, MinWidth = 320, TextWrapping = TextWrapping.NoWrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        box.TextChanged += (s, e) => S.Set(path, new JArray(box.Text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Cast<object>().ToArray()));
        Row(label, box, hint);
    }

    void Slider(string path, string label, double min, double max, string unit, double fallback)
    {
        var t = S.Get(path);
        double value = t != null && (t.Type == JTokenType.Integer || t.Type == JTokenType.Float) ? Math.Round((double)t) : fallback;
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var slider = new Slider { Minimum = min, Maximum = max, Value = value, Width = 260, IsSnapToTickEnabled = true, TickFrequency = 1, VerticalAlignment = VerticalAlignment.Center };
        var shown = new TextBlock { Text = value + unit, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, MinWidth = 50 };
        slider.ValueChanged += (s, e) => { shown.Text = slider.Value + unit; S.Set(path, (int)slider.Value); };
        panel.Children.Add(slider); panel.Children.Add(shown);
        Row(label, panel, null);
    }

    // Discord favourites as lines: "Label = friend" or "Label = Server / Channel"
    void Favourites()
    {
        var arr = S.Get("Discord.Favourites") as JArray;
        var lines = arr == null ? new List<string>() : arr.OfType<JObject>().Select(f =>
            ((string)f["Label"] ?? "") + " = " + (!string.IsNullOrEmpty((string)f["Friend"]) ? (string)f["Friend"] : (string)f["Server"] + " / " + (string)f["Channel"])).ToList();
        var box = new TextBox { Text = string.Join(Environment.NewLine, lines), AcceptsReturn = true, MinHeight = 60, MinWidth = 320 };
        box.TextChanged += (s, e) =>
        {
            var favs = new JArray();
            foreach (var line in box.Text.Split('\n').Select(l => l.Trim()).Where(l => l.Contains("=")))
            {
                string name = line.Substring(0, line.IndexOf('=')).Trim(), target = line.Substring(line.IndexOf('=') + 1).Trim();
                if (target.Contains("/"))
                    favs.Add(new JObject { { "Label", name }, { "Server", target.Substring(0, target.IndexOf('/')).Trim() }, { "Channel", target.Substring(target.IndexOf('/') + 1).Trim() } });
                else if (target.Length > 0) favs.Add(new JObject { { "Label", name }, { "Friend", target } });
            }
            S.Set("Discord.Favourites", favs);
        };
        Row("Favourites", box, "One per line. A friend: \"My brother = their name in Discord's DM list\". A voice channel: \"Gaming = Server name / Channel name\".");
    }

    // --- Buttons and actions ---

    void Buttons(params FrameworkElement[] buttons)
    {
        var panel = new WrapPanel { Margin = new Thickness(0, 6, 0, 6) };
        foreach (var b in buttons) panel.Children.Add(b);
        page.Children.Add(panel);
    }

    // A button with a result line beside it
    FrameworkElement Button(string label, Func<string> action)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 4) };
        var button = new Button { Content = label, Padding = new Thickness(12, 4, 12, 4) };
        var result = new TextBlock { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Opacity = 0.85 };
        button.Click += (s, e) =>
        {
            button.IsEnabled = false; result.Text = "…";
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string msg;
                try { msg = action(); } catch (Exception ex) { msg = ex.Message; }
                Dispatcher.BeginInvoke(new Action(() => { result.Text = msg; button.IsEnabled = true; }));
            });
        };
        panel.Children.Add(button); panel.Children.Add(result);
        return panel;
    }

    FrameworkElement Button(string label, Action action) { return Button(label, () => { action(); return ""; }); }

    string TestHomeAssistant()
    {
        string url = ((string)S.Get("SmartHome.HomeAssistantUrl") ?? "").TrimEnd('/'), token = (string)S.Get("SmartHome.HomeAssistantToken") ?? "";
        if (url.Length == 0 || token.Length == 0) return "Fill in the address and token first.";
        using (var web = new WebClient())
        {
            web.Headers[HttpRequestHeader.Authorization] = "Bearer " + token;
            try { web.DownloadString(url + "/api/"); return "✓ Connected to Home Assistant"; }
            catch (WebException ex)
            {
                var r = ex.Response as HttpWebResponse;
                if (r != null && r.StatusCode == HttpStatusCode.Unauthorized) return "Home Assistant rejected the token";
                return "Couldn't reach it: " + ex.Message;
            }
        }
    }

    string PairNanoleaf()
    {
        string host = (string)S.Get("SmartHome.NanoleafHost") ?? "";
        if (host.Length == 0) return "Fill in the panels' IP address first.";
        using (var web = new WebClient())
        {
            try
            {
                string reply = web.UploadString("http://" + host + ":16021/api/v1/new", "POST", "");
                string token = (string)JObject.Parse(reply)["auth_token"];
                if (string.IsNullOrEmpty(token)) return "No token came back";
                Dispatcher.Invoke(new Action(() => { S.Set("SmartHome.NanoleafToken", token); Build(); }));
                return "✓ Paired. Save to keep it.";
            }
            catch (WebException ex)
            {
                var r = ex.Response as HttpWebResponse;
                if (r != null && (int)r.StatusCode == 403) return "Not in pairing mode: hold the power button 5–7 seconds, then try again";
                return "Couldn't reach the panels: " + ex.Message;
            }
        }
    }

    string SetUpSensors()
    {
        if (Process.GetProcessesByName("FanControl").Length == 0) return "FanControl isn't running. Install and start it first.";
        string exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CouchKit", "CouchKit.exe");
        try
        {
            using (var p = Process.Start(new ProcessStartInfo(exe, "--install-sensors") { UseShellExecute = true, Verb = "runas" })) p.WaitForExit(30000);
        }
        catch (System.ComponentModel.Win32Exception) { return "Cancelled: Windows needs admin permission to set it up."; }
        for (int i = 0; i < 10; i++)
        {
            Thread.Sleep(1000);
            string sensors = Path.Combine(CouchKitSettings.DataDir, "sensors.json");
            if (File.Exists(sensors) && (DateTime.Now - File.GetLastWriteTime(sensors)).TotalSeconds < 15) return "✓ Working: the System tab now shows the CPU temperature";
        }
        return "Set up, but no readings yet. Check FanControl is running.";
    }
}
