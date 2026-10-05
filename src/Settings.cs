// Settings: read once at startup from %APPDATA%\CouchKit\couchkit.json. Anything missing from the
// file keeps the default below, so a file only needs the values that differ.
using System;
using System.IO;
using System.Web.Script.Serialization;

class Settings
{
    public ControllerSettings Controller { get; set; }
    public DisplaySettings Display { get; set; }
    public AudioSettings Audio { get; set; }
    public GameSettings Games { get; set; }
    public LightingSettings Lighting { get; set; }

    public Settings()
    {
        Controller = new ControllerSettings();
        Display = new DisplaySettings();
        Audio = new AudioSettings();
        Games = new GameSettings();
        Lighting = new LightingSettings();
    }

    // Settings, log and the running-game files the Playnite add-on writes all live here, outside
    // the install folder, so reinstalling or updating never touches them.
    public static readonly string DataDir = CreateDataDir();
    public static readonly string FilePath = Path.Combine(DataDir, "couchkit.json");

    static string CreateDataDir()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CouchKit");
        try { Directory.CreateDirectory(dir); } catch { }
        return dir;
    }

    // Returns the settings and a note for the log (which file was used, or why the defaults were).
    public static Settings Load(out string note)
    {
        if (!File.Exists(FilePath)) { note = "no couchkit.json, using defaults"; return new Settings(); }
        try
        {
            var s = new JavaScriptSerializer().Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
            // Sections left out of the file come back null; give them their defaults.
            if (s.Controller == null) s.Controller = new ControllerSettings();
            if (s.Display == null) s.Display = new DisplaySettings();
            if (s.Audio == null) s.Audio = new AudioSettings();
            if (s.Games == null) s.Games = new GameSettings();
            if (s.Lighting == null) s.Lighting = new LightingSettings();
            note = "settings from " + FilePath;
            return s;
        }
        catch (Exception ex)
        {
            note = "couchkit.json could not be read (" + ex.Message + "), using defaults";
            return new Settings();
        }
    }
}

class ControllerSettings
{
    public bool Enabled { get; set; }
    public int CloseHoldMs { get; set; }      // hold Back + Start this long: ask the game to close
    public int KillHoldMs { get; set; }       // ...this long: force it closed
    public bool GuideButton { get; set; }     // tap the Xbox button to switch between the game and Playnite
    public int GuideTapMaxMs { get; set; }    // longer holds are left to Windows
    public ControllerSettings() { Enabled = true; CloseHoldMs = 1500; KillHoldMs = 4000; GuideButton = true; GuideTapMaxMs = 500; }
}

class DisplaySettings
{
    public bool KeepPlayniteOnTv { get; set; }      // move Playnite off a streaming virtual display (or minimise it if the TV is off)
    public bool KeepGameOnTv { get; set; }          // same for the game Playnite started
    public bool MuteWhileStreaming { get; set; }    // mute Playnite and the game while a virtual display exists
    public bool FocusPlayniteAfterStream { get; set; }
    // Enforce this mode on the TV whenever the set of screens changes. 0 = leave the mode alone.
    public int TvWidth { get; set; }
    public int TvHeight { get; set; }
    public int TvRefreshRate { get; set; }
    // A display whose adapter name contains any of these counts as a streaming virtual display.
    public string[] VirtualDisplayNames { get; set; }
    public DisplaySettings()
    {
        KeepPlayniteOnTv = true; KeepGameOnTv = true; MuteWhileStreaming = true; FocusPlayniteAfterStream = true;
        VirtualDisplayNames = new[] { "Virtual", "SudoMaker", "IddSample", "Parsec" };
    }
}

class AudioSettings
{
    // Switch the default sound output to the device whose name contains this, when the TV is the
    // only screen (at startup, when the TV turns on, after a stream ends). Empty = off.
    public string TvDeviceName { get; set; }
    public AudioSettings() { TvDeviceName = ""; }
}

class GameSettings
{
    // Emulated games record their ROM folder as the install folder, so when nothing runs from
    // there, a process running from one of these folders is taken to be the game.
    public string[] EmulatorFolders { get; set; }
    // Never closed, moved or muted as "the game", in addition to the built-in list.
    public string[] ProtectedProcesses { get; set; }
    public GameSettings() { EmulatorFolders = new string[0]; ProtectedProcesses = new string[0]; }
}

class LightingSettings
{
    public bool Enabled { get; set; }
    public string OpenRgbHost { get; set; }
    public int OpenRgbPort { get; set; }
    public string[] Palette { get; set; }            // shown while browsing; saturated to full strength
    public bool GameColours { get; set; }            // in game, use colours from the game's art
    public double BrowseCycleSeconds { get; set; }   // one full sweep of the gradient
    public double GameCycleSeconds { get; set; }
    public int Fps { get; set; }
    public double Span { get; set; }                 // share of the colour loop across one device: lower = gentler
    public double ZoneOffset { get; set; }           // how far each zone of a device (e.g. each fan channel) lags the previous one, as a share of the loop
    public LightingSettings()
    {
        Enabled = true; OpenRgbHost = "127.0.0.1"; OpenRgbPort = 6742;
        Palette = new[] { "#75dddd", "#84c7d0", "#9297c4", "#9368b7", "#aa3e98" };
        GameColours = true; BrowseCycleSeconds = 9; GameCycleSeconds = 7; Fps = 30; Span = 0.5; ZoneOffset = 0.25;
    }
}
