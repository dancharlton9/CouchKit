// PlayniteLink: the Guide's view of Playnite, through files the CouchKit add-on keeps in
// %APPDATA%\CouchKit: recent-games.json (most recently played games) and current-game-name.txt
// (what's running). Starting a game is a request the add-on carries out inside Playnite
// (playnite-command.txt), so it's the same as starting it from Playnite's own UI.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Web.Script.Serialization;

static class PlayniteLink
{
    public class Game
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public long Playtime { get; set; }   // seconds
        public string LastActivity { get; set; }
        public bool Installed { get; set; }
        public string Platform { get; set; }
        public string Cover { get; set; }
        public string Background { get; set; }
    }

    static readonly string RecentPath = Path.Combine(Settings.DataDir, "recent-games.json");
    static readonly string CommandPath = Path.Combine(Settings.DataDir, "playnite-command.txt");
    static readonly string NamePath = Path.Combine(Settings.DataDir, "current-game-name.txt");
    static readonly string GamePath = Path.Combine(Settings.DataDir, "current-game.txt");

    public static List<Game> Recent()
    {
        try { return File.Exists(RecentPath) ? new JavaScriptSerializer().Deserialize<List<Game>>(File.ReadAllText(RecentPath)) ?? new List<Game>() : new List<Game>(); }
        catch { return new List<Game>(); }
    }

    public static bool Running { get { return File.Exists(GamePath); } }

    public static string NowPlaying
    {
        get { try { return Running && File.Exists(NamePath) ? File.ReadAllText(NamePath).Trim() : ""; } catch { return ""; } }
    }

    public static Game Current
    {
        get { string n = NowPlaying; return n.Length == 0 ? null : Recent().FirstOrDefault(g => g.Name == n); }
    }

    public static string Start(Game game)
    {
        File.WriteAllText(CommandPath, "start " + game.Id);
        CouchKit.Log("Guide: starting " + game.Name);
        return "Starting " + game.Name;
    }

    // Game swap: close what's running (politely), wait for Playnite to see it exit, then start the next
    public static string SwitchTo(Game game)
    {
        if (Running)
        {
            CouchKit.CloseRunningGame();
            for (int i = 0; i < 40 && Running; i++) Thread.Sleep(500);
            if (Running) return NowPlaying + " didn't close – close it first";
            Thread.Sleep(1500);   // let Playnite settle
        }
        return Start(game);
    }

    public static string Hours(long seconds)
    {
        if (seconds <= 0) return "not played yet";
        double h = seconds / 3600.0;
        return h < 1 ? Math.Max(1, (int)(seconds / 60)) + " min played" : (h < 10 ? h.ToString("0.#") : ((int)h).ToString()) + " h played";
    }
}
