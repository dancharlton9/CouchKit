// Status: what CouchKit found on this PC and whether each feature is working, for the Guide's
// System → CouchKit status page. Each check is quick and local (no network calls), and says in
// plain words what's wrong and what to do.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

static class Status
{
    public enum State { Working, Off, NeedsSetup, Problem }

    public class Check
    {
        public string Name, Detail, Icon;
        public State State;
    }

    static readonly string AppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static bool DiscordInstalled { get { return File.Exists(Path.Combine(LocalAppData, @"Discord\Update.exe")); } }
    public static bool HowLongToBeatInstalled { get { return Directory.Exists(Path.Combine(AppData, @"Playnite\ExtensionsData\e08cd51f-9c9a-4ee3-a094-fde03b55492f\HowLongToBeat")); } }

    public static List<Check> All(Settings s)
    {
        var list = new List<Check>();
        Action<string, string, State, string> add = (name, icon, state, detail) => list.Add(new Check { Name = name, Icon = icon, State = state, Detail = detail });

        // Controller
        int pads = CouchKit.ControllersConnected();
        if (!s.Controller.Enabled) add("Controller", "game", State.Off, "Turned off in couchkit.json");
        else if (pads == 0) add("Controller", "game", State.Problem, "No controller connected");
        else add("Controller", "game", State.Working, pads + " connected  ·  " + Pad.Style + " buttons" + (Pad.Setting == "Auto" ? " (detected)" : ""));

        // Playnite's own Xbox-button handling fights CouchKit's
        try
        {
            string cfg = Path.Combine(AppData, @"Playnite\fullscreenConfig.json");
            if (s.Controller.GuideButton && File.Exists(cfg) && File.ReadAllText(cfg).Replace(" ", "").Contains("\"GuideButtonFocus\":true"))
                add("Xbox button", "game", State.NeedsSetup, "Turn off \"Guide button focuses Playnite\" in Playnite's fullscreen settings");
            else if (s.Controller.GuideButton) add("Xbox button", "game", State.Working, "Tap: game ↔ Playnite  ·  double-tap: the Guide");
        }
        catch { }

        // Lighting
        if (!s.Lighting.Enabled) add("PC lighting", "bulb", State.Off, "Turned off in couchkit.json");
        else if (Lighting.Devices > 0) add("PC lighting", "bulb", State.Working, "OpenRGB  ·  " + Lighting.Devices + " devices" + (Lighting.Off ? "  ·  switched off from the Guide" : ""));
        else if (Process.GetProcessesByName("OpenRGB").Length == 0) add("PC lighting", "bulb", State.NeedsSetup, "Install OpenRGB and start it with its SDK server on");
        else add("PC lighting", "bulb", State.Problem, "OpenRGB is running but its SDK server isn't reachable" + (Lighting.Problem.Length > 0 ? " (" + Lighting.Problem + ")" : ""));

        // Smart home
        if (!SmartHome.HasHomeAssistant && string.IsNullOrEmpty(s.SmartHome.WebhookUrl) && string.IsNullOrEmpty(s.SmartHome.NanoleafHost))
            add("Smart home", "bulb", State.Off, "Not set up (Home Assistant, webhook or Nanoleaf in couchkit.json)");
        else
        {
            var parts = new List<string>();
            if (SmartHome.HasHomeAssistant) parts.Add("Home Assistant (" + s.SmartHome.LightScenes.Length + " scenes)");
            if (!string.IsNullOrEmpty(s.SmartHome.WebhookUrl)) parts.Add("game-colour webhook");
            if (!string.IsNullOrEmpty(s.SmartHome.NanoleafHost)) parts.Add(string.IsNullOrEmpty(s.SmartHome.NanoleafToken) ? "Nanoleaf (needs pairing)" : "Nanoleaf");
            add("Smart home", "bulb", parts.Any(p => p.Contains("needs")) ? State.NeedsSetup : State.Working, string.Join("  ·  ", parts));
        }

        // Discord
        if (!s.Discord.Enabled) add("Discord", "chat", State.Off, DiscordInstalled ? "Turned off in couchkit.json" : "Not installed");
        else if (!DiscordInstalled) add("Discord", "chat", State.Off, "Not installed, so there's no Party tab");
        else if (!DiscordBridge.Running) add("Discord", "chat", State.NeedsSetup, "Installed but not running. Start it from the Party tab. English Discord only");
        else add("Discord", "chat", State.Working, (DiscordBridge.InCall ? "In a call" : "Running") + "  ·  " + s.Discord.Favourites.Length + " favourites");

        // Achievements and HowLongToBeat (Playnite add-ons)
        if (!s.Achievements.Enabled) add("Achievements", "trophy", State.Off, "Turned off in couchkit.json");
        else if (!Achievements.Available) add("Achievements", "trophy", State.Off, "Install the PlayniteAchievements add-on in Playnite to see them here");
        else add("Achievements", "trophy", State.Working, "From PlayniteAchievements" + (s.Achievements.Popups ? "  ·  TV pop-ups on" : ""));
        add("HowLongToBeat", "recent", HowLongToBeatInstalled ? State.Working : State.Off,
            HowLongToBeatInstalled ? "Estimates on the now-playing card" : "Optional: install the HowLongToBeat add-on in Playnite");

        // Temperatures
        var st = Pc.Latest;
        bool fanControl = Process.GetProcessesByName("FanControl").Length > 0;
        bool sensors = File.Exists(Path.Combine(Settings.DataDir, "sensors.json")) && (DateTime.Now - File.GetLastWriteTime(Path.Combine(Settings.DataDir, "sensors.json"))).TotalSeconds < 30;
        if (sensors) add("CPU temperature", "pc", State.Working, "From FanControl, through the CouchKit sensors helper");
        else if (fanControl) add("CPU temperature", "pc", State.NeedsSetup, "FanControl is running: set up the sensors helper (see the README) to show it");
        else add("CPU temperature", "pc", State.Off, "Needs FanControl. Everything else on the System tab still works");
        bool nvidia = File.Exists(Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe"));
        add("GPU stats", "pc", nvidia || sensors ? State.Working : State.Off, nvidia ? "Temperature, load and power from the NVIDIA driver" : sensors ? "Temperature from FanControl" : "Needs an NVIDIA card or FanControl");

        // Audio
        if (!string.IsNullOrEmpty(s.Audio.HeadsetDeviceName) || !string.IsNullOrEmpty(s.Audio.TvDeviceName))
        {
            try
            {
                var outputs = CouchKit.OutputDevices();
                Func<string, bool> found = n => !string.IsNullOrEmpty(n) && outputs.Any(o => o.Item1.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0);
                var parts = new List<string>();
                if (!string.IsNullOrEmpty(s.Audio.TvDeviceName)) parts.Add(found(s.Audio.TvDeviceName) ? "TV found" : "TV \"" + s.Audio.TvDeviceName + "\" not connected");
                if (!string.IsNullOrEmpty(s.Audio.HeadsetDeviceName)) parts.Add(found(s.Audio.HeadsetDeviceName) ? "headset on" : "headset off");
                add("Audio", "volume", State.Working, string.Join("  ·  ", parts));
            }
            catch (Exception ex) { add("Audio", "volume", State.Problem, ex.Message); }
        }
        else add("Audio", "volume", State.Off, "Optional: set your TV and headset names in couchkit.json to switch sound automatically");

        return list;
    }
}
