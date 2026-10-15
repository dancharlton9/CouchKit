// CouchKit Playnite add-on: starts CouchKit (bundled with this DLL) when Playnite starts, and
// tells it which game is running by writing two files to %APPDATA%\CouchKit:
//   current-game.txt      the game's install folder (CouchKit treats processes run from it as the game)
//   current-game-art.txt  the full path of the game's background image, or its cover if it has none
//   current-game-name.txt the game's name
// They are deleted when the game exits. For the Guide's Home tab it also keeps:
//   recent-games.json     the most recently played games (name, playtime, cover...), refreshed as games come and go
// and watches playnite-command.txt for requests from CouchKit ("start <game id>", "library"), which it
// carries out through Playnite itself, so playtime tracking and game-start events work as usual.
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;

public class CouchKitPlugin : GenericPlugin
{
    static readonly ILogger logger = LogManager.GetLogger();

    static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CouchKit");
    static readonly string GamePath = Path.Combine(DataDir, "current-game.txt");
    static readonly string ArtPath = Path.Combine(DataDir, "current-game-art.txt");
    static readonly string NamePath = Path.Combine(DataDir, "current-game-name.txt");
    static readonly string RecentPath = Path.Combine(DataDir, "recent-games.json");
    static readonly string CommandPath = Path.Combine(DataDir, "playnite-command.txt");
    FileSystemWatcher commands;

    static readonly Guid PluginId = Guid.Parse("6f0c6a51-8d0b-4c41-9a38-2b1f4f6d7c11");
    public override Guid Id { get { return PluginId; } }

    readonly CouchKitSettings settings = new CouchKitSettings();

    public CouchKitPlugin(IPlayniteAPI api) : base(api)
    {
        Properties = new GenericPluginProperties { HasSettings = true };
        settings.Saved = RestartCouchKit;
    }

    // Add-ons → Extension settings → CouchKit
    public override ISettings GetSettings(bool firstRunSettings) { return settings; }
    public override System.Windows.Controls.UserControl GetSettingsView(bool firstRunView) { return new SettingsPage(settings); }

    // After saving settings: CouchKit reads couchkit.json when it starts
    static void RestartCouchKit()
    {
        try
        {
            string exe = Path.Combine(RunDir, "CouchKit.exe");
            foreach (var p in Process.GetProcessesByName("CouchKit").Where(p => string.Equals(ImagePath(p), exe, StringComparison.OrdinalIgnoreCase)))
            { p.Kill(); p.WaitForExit(5000); }
            if (File.Exists(exe)) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = RunDir });
            logger.Info("CouchKit: restarted with the new settings");
        }
        catch (Exception ex) { logger.Error(ex, "CouchKit: could not restart CouchKit.exe"); }
    }

    public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
    {
        // Left over from a crash or a game still running from before Playnite restarted
        DeleteGameFiles();
        StartCouchKit();
        WriteRecentGames();
        WatchCommands();
    }

    public override void OnLibraryUpdated(OnLibraryUpdatedEventArgs args) { WriteRecentGames(); }

    // The 12 most recently played games, for the Guide's Home tab
    void WriteRecentGames()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            var games = PlayniteApi.Database.Games.Where(g => g.LastActivity.HasValue && !g.Hidden)
                .OrderByDescending(g => g.LastActivity.Value).Take(12)
                .Select(g => new
                {
                    Id = g.Id.ToString(),
                    Name = g.Name ?? "",
                    Playtime = g.Playtime,
                    LastActivity = g.LastActivity.Value.ToString("o"),
                    Installed = g.IsInstalled,
                    Platform = g.Platforms != null && g.Platforms.Count > 0 ? g.Platforms[0].Name : "",
                    Cover = string.IsNullOrEmpty(g.CoverImage) ? "" : PlayniteApi.Database.GetFullFilePath(g.CoverImage) ?? "",
                    Background = string.IsNullOrEmpty(g.BackgroundImage) ? "" : PlayniteApi.Database.GetFullFilePath(g.BackgroundImage) ?? "",
                }).ToList();
            File.WriteAllText(RecentPath, Playnite.SDK.Data.Serialization.ToJson(games));
        }
        catch (Exception ex) { logger.Error(ex, "CouchKit: could not write recent games"); }
    }

    // Requests from CouchKit: "start <game id>" or "library"
    void WatchCommands()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            DeleteFile(CommandPath);
            commands = new FileSystemWatcher(DataDir, "playnite-command.txt") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName };
            FileSystemEventHandler handler = (s, e) => System.Threading.ThreadPool.QueueUserWorkItem(_ => RunCommand());
            commands.Created += handler;
            commands.Changed += handler;
            commands.EnableRaisingEvents = true;
        }
        catch (Exception ex) { logger.Error(ex, "CouchKit: could not watch for commands"); }
    }

    readonly object commandLock = new object();
    void RunCommand()
    {
        lock (commandLock)
        {
            string text = null;
            for (int i = 0; i < 5 && text == null; i++)
            {
                try { if (File.Exists(CommandPath)) { text = File.ReadAllText(CommandPath).Trim(); File.Delete(CommandPath); } else return; }
                catch { System.Threading.Thread.Sleep(100); }
            }
            if (string.IsNullOrEmpty(text)) return;
            logger.Info("CouchKit: command " + text);
            PlayniteApi.MainView.UIDispatcher.Invoke(new Action(() =>
            {
                try
                {
                    Guid id;
                    if (text.StartsWith("start ") && Guid.TryParse(text.Substring(6).Trim(), out id)) PlayniteApi.StartGame(id);
                }
                catch (Exception ex) { logger.Error(ex, "CouchKit: command failed: " + text); }
            }));
        }
    }

    public override void OnGameStarted(OnGameStartedEventArgs args)
    {
        Game game = args.Game;
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(NamePath, game.Name ?? "");
            File.WriteAllText(GamePath, game.InstallDirectory ?? "");
            string art = new[] { game.BackgroundImage, game.CoverImage }.FirstOrDefault(a => !string.IsNullOrEmpty(a));
            string artPath = art == null ? null : PlayniteApi.Database.GetFullFilePath(art);
            if (!string.IsNullOrEmpty(artPath) && File.Exists(artPath)) File.WriteAllText(ArtPath, artPath);
            else DeleteFile(ArtPath);
        }
        catch (Exception ex) { logger.Error(ex, "CouchKit: could not record the running game"); }
        WriteRecentGames();
    }

    public override void OnGameStopped(OnGameStoppedEventArgs args)
    {
        DeleteGameFiles();
        WriteRecentGames();
    }

    public override void OnGameStartupCancelled(OnGameStartupCancelledEventArgs args)
    {
        DeleteGameFiles();
    }

    static void DeleteGameFiles()
    {
        DeleteFile(GamePath);
        DeleteFile(ArtPath);
        DeleteFile(NamePath);
    }

    static void DeleteFile(string path)
    {
        try { File.Delete(path); } catch (Exception ex) { logger.Warn(ex, "CouchKit: could not delete " + path); }
    }

    // CouchKit runs from a copy in %LOCALAPPDATA%\CouchKit rather than from the add-on's folder, so
    // it never holds the add-on's files open while Playnite installs an update. The copy is
    // refreshed (and CouchKit restarted) whenever the bundled version changes.
    static readonly string RunDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CouchKit");

    static void StartCouchKit()
    {
        try
        {
            string bundled = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "CouchKit.exe");
            string exe = Path.Combine(RunDir, "CouchKit.exe");
            if (!File.Exists(bundled)) { logger.Warn("CouchKit: " + bundled + " not found"); return; }

            var running = Process.GetProcessesByName("CouchKit");
            var ours = running.Where(p => string.Equals(ImagePath(p), exe, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (running.Length > ours.Length) { logger.Info("CouchKit: already running from elsewhere, leaving it"); return; }

            bool stale = !File.Exists(exe) || FileVersionInfo.GetVersionInfo(exe).FileVersion != FileVersionInfo.GetVersionInfo(bundled).FileVersion;
            if (!stale && ours.Length > 0) return;
            if (stale)
            {
                foreach (var p in ours) { p.Kill(); p.WaitForExit(5000); }
                Directory.CreateDirectory(RunDir);
                File.Copy(bundled, exe, true);
                logger.Info("CouchKit: installed " + FileVersionInfo.GetVersionInfo(exe).FileVersion + " to " + exe);
            }
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = RunDir });
            logger.Info("CouchKit: started " + exe);
        }
        catch (Exception ex) { logger.Error(ex, "CouchKit: could not start CouchKit.exe"); }
    }

    // Playnite is 32-bit and CouchKit 64-bit, so Process.MainModule can't read CouchKit's path;
    // QueryFullProcessImageName works across that boundary.
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern bool QueryFullProcessImageName(IntPtr process, uint flags, System.Text.StringBuilder name, ref int size);

    static string ImagePath(Process p)
    {
        IntPtr h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, p.Id);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new System.Text.StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }
}
