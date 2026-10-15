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

    // --demo: a game to show as running
    public static string Pretend;

    public static bool Running { get { return Pretend != null || File.Exists(GamePath); } }

    public static string NowPlaying
    {
        get { if (Pretend != null) return Pretend; try { return Running && File.Exists(NamePath) ? File.ReadAllText(NamePath).Trim() : ""; } catch { return ""; } }
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

    // When the running game started: the add-on writes current-game.txt as it launches
    public static DateTime? SessionStart
    {
        get
        {
            if (Pretend != null) return DateTime.Now.AddMinutes(-72);
            try { return File.Exists(GamePath) ? (DateTime?)File.GetLastWriteTime(GamePath) : null; } catch { return null; }
        }
    }

    // "1 h 12 min" / "8 min"
    public static string Duration(TimeSpan t)
    {
        if (t.TotalHours >= 1) return (int)t.TotalHours + " h " + t.Minutes + " min";
        return Math.Max(1, (int)t.TotalMinutes) + " min";
    }

    // HowLongToBeat times (seconds) from the HowLongToBeat add-on's data, if it has the game:
    // main story, main + extras, completionist. Zero where unknown.
    static readonly string HltbDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                                  @"Playnite\ExtensionsData\e08cd51f-9c9a-4ee3-a094-fde03b55492f\HowLongToBeat");
    public static long[] HowLongToBeat(string gameId)
    {
        try
        {
            string file = Path.Combine(HltbDir, gameId + ".json");
            if (string.IsNullOrEmpty(gameId) || !File.Exists(file)) return null;
            var root = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(file)) as Dictionary<string, object>;
            var items = root != null && root.ContainsKey("Items") ? root["Items"] as object[] : null;
            var first = items != null && items.Length > 0 ? items[0] as Dictionary<string, object> : null;
            var data = first != null && first.ContainsKey("GameHltbData") ? first["GameHltbData"] as Dictionary<string, object> : null;
            if (data == null) return null;
            Func<string, long> get = k => data.ContainsKey(k) && data[k] != null ? Convert.ToInt64(data[k]) : 0;
            var times = new[] { get("MainStoryClassic"), get("MainExtraClassic"), get("CompletionistClassic") };
            return times.Any(t => t > 0) ? times : null;
        }
        catch { return null; }
    }

    public static string Hours(long seconds)
    {
        if (seconds <= 0) return "not played yet";
        double h = seconds / 3600.0;
        return h < 1 ? Math.Max(1, (int)(seconds / 60)) + " min played" : (h < 10 ? h.ToString("0.#") : ((int)h).ToString()) + " h played";
    }
}
