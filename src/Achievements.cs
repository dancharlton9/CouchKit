// Achievements: read from the PlayniteAchievements add-on's database (achievement_cache.db in its
// ExtensionsData folder), read-only, through the SQLite that ships with Windows (winsqlite3.dll).
// The Guide shows the running game's progress and achievements and recent unlocks, and a watcher
// notices new unlocks (the add-on polls Steam/Xbox while a game runs) for a pop-up on the TV.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

static class Achievements
{
    public class Achievement
    {
        public long Id;
        public string Name = "", Description = "", Icon = "", Rarity = "", Game = "", GameId = "";
        public bool Unlocked, Hidden;
        public DateTime? UnlockTime;   // local time
        public int ProgressNum, ProgressDenom, Points;
        public double Percent = -1;    // share of players who have it, if known
    }

    public class Summary { public int Unlocked, Total; }

    static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                                 @"Playnite\ExtensionsData\e6aad2c9-6e06-4d8d-ac55-ac3b252b5f7b");
    static readonly string DbPath = Path.Combine(Folder, "achievement_cache.db");

    public static bool Enabled = true;   // Achievements.Enabled in couchkit.json
    public static bool Available { get { return Enabled && File.Exists(DbPath); } }

    // --- Queries ---

    const string Columns = @"select ua.Id, d.DisplayName, d.Description, d.UnlockedIconPath, d.LockedIconPath, ua.Unlocked, ua.UnlockTimeUtc,
               ua.ProgressNum, ua.ProgressDenom, d.GlobalPercentUnlocked, d.Rarity, d.Hidden, d.Points, g.GameName, g.PlayniteGameId
        from UserAchievements ua
        join AchievementDefinitions d on d.Id = ua.AchievementDefinitionId
        join UserGameProgress p on p.Id = ua.UserGameProgressId
        join Games g on g.Id = p.GameId
        join Users u on u.Id = p.UserId and u.IsCurrentUser = 1 ";

    static readonly Dictionary<string, Tuple<DateTime, Summary>> summaries = new Dictionary<string, Tuple<DateTime, Summary>>();

    // Unlocked/total for a Playnite game, cached for a few seconds (the Guide repaints often)
    public static Summary ForGame(string playniteId)
    {
        if (string.IsNullOrEmpty(playniteId) || !Available) return null;
        lock (summaries)
        {
            Tuple<DateTime, Summary> hit;
            if (summaries.TryGetValue(playniteId, out hit) && (DateTime.Now - hit.Item1).TotalSeconds < 10) return hit.Item2;
        }
        Summary s = null;
        try
        {
            Query(@"select sum(p.AchievementsUnlocked), sum(p.TotalAchievements) from UserGameProgress p
                    join Games g on g.Id = p.GameId join Users u on u.Id = p.UserId and u.IsCurrentUser = 1
                    where g.PlayniteGameId = ?1 collate nocase", new[] { playniteId },
                  r => { if (!r.IsNull(1) && r.Int(1) > 0) s = new Summary { Unlocked = r.Int(0), Total = r.Int(1) }; });
        }
        catch (Exception ex) { CouchKit.Log("Achievements: " + ex.Message); }
        lock (summaries) summaries[playniteId] = Tuple.Create(DateTime.Now, s);
        return s;
    }

    // All of a game's achievements: unlocked (newest first), then locked (closest to done, then most common)
    public static List<Achievement> ForGameList(string playniteId)
    {
        var list = new List<Achievement>();
        try { Query(Columns + "where g.PlayniteGameId = ?1 collate nocase", new[] { playniteId }, r => list.Add(Read(r))); }
        catch (Exception ex) { CouchKit.Log("Achievements: " + ex.Message); }
        return list.Where(a => a.Unlocked).OrderByDescending(a => a.UnlockTime ?? DateTime.MinValue)
                   .Concat(list.Where(a => !a.Unlocked).OrderBy(a => a.Hidden).ThenByDescending(Fraction).ThenByDescending(a => a.Percent))
                   .ToList();
    }

    public static double Fraction(Achievement a) { return a.ProgressDenom > 0 ? Math.Min(1.0, (double)a.ProgressNum / a.ProgressDenom) : 0; }

    // Latest unlocks across every game
    public static List<Achievement> Recent(int count)
    {
        var list = new List<Achievement>();
        try { Query(Columns + "where ua.Unlocked = 1 and ua.UnlockTimeUtc is not null order by ua.UnlockTimeUtc desc limit " + count, null, r => list.Add(Read(r))); }
        catch (Exception ex) { CouchKit.Log("Achievements: " + ex.Message); }
        return list;
    }

    static Achievement Read(Row r)
    {
        var a = new Achievement
        {
            Id = r.Long(0), Name = r.Text(1), Description = r.Text(2), Unlocked = r.Int(5) != 0,
            ProgressNum = r.Int(7), ProgressDenom = r.Int(8), Percent = r.IsNull(9) ? -1 : r.Double(9), Rarity = r.Text(10),
            Hidden = r.Int(11) != 0, Points = r.Int(12), Game = r.Text(13), GameId = r.Text(14),
        };
        string icon = a.Unlocked || r.IsNull(4) ? r.Text(3) : r.Text(4);
        if (icon.Length > 0) a.Icon = Path.IsPathRooted(icon) ? icon : Path.Combine(Folder, icon);
        DateTime t;
        if (!r.IsNull(6) && DateTime.TryParse(r.Text(6), null, System.Globalization.DateTimeStyles.RoundtripKind, out t)) a.UnlockTime = t.ToLocalTime();
        return a;
    }

    // "Rare · 4.2% of players" / "Ultra rare"
    public static string RarityText(Achievement a)
    {
        string tier = a.Rarity == "UltraRare" ? "Ultra rare" : a.Rarity;
        if (a.Percent >= 0) return tier + "  ·  " + (a.Percent < 10 ? a.Percent.ToString("0.#") : ((int)Math.Round(a.Percent)).ToString()) + "% of players";
        return tier;
    }

    // "Just now" / "3 h ago" / "Yesterday" / "3 Oct"
    public static string When(DateTime? t)
    {
        if (!t.HasValue) return "";
        var age = DateTime.Now - t.Value;
        if (age.TotalMinutes < 2) return "Just now";
        if (age.TotalHours < 1) return (int)age.TotalMinutes + " min ago";
        if (age.TotalHours < 24 && t.Value.Date == DateTime.Today) return (int)age.TotalHours + " h ago";
        if (t.Value.Date == DateTime.Today.AddDays(-1)) return "Yesterday";
        return t.Value.ToString(t.Value.Year == DateTime.Now.Year ? "d MMM" : "d MMM yyyy");
    }

    // --- Watching for new unlocks ---

    public static void Watch(Action<Achievement> unlocked)
    {
        var th = new Thread(() =>
        {
            var seen = new HashSet<long>();
            bool first = true;
            while (true)
            {
                try
                {
                    if (Available)
                    {
                        string since = DateTime.UtcNow.AddHours(-2).ToString("yyyy-MM-ddTHH:mm:ss");
                        var fresh = new List<Achievement>();
                        Query(Columns + "where ua.Unlocked = 1 and ua.UnlockTimeUtc > ?1 order by ua.UnlockTimeUtc", new[] { since }, r => fresh.Add(Read(r)));
                        foreach (var a in fresh)
                        {
                            if (!seen.Add(a.Id) || first) continue;
                            // Only pop up for things unlocked just now, not a library refresh catching up
                            if (a.UnlockTime.HasValue && (DateTime.Now - a.UnlockTime.Value).TotalMinutes < 30)
                            {
                                CouchKit.Log("Achievement unlocked: " + a.Game + " – " + a.Name);
                                lock (summaries) summaries.Clear();
                                unlocked(a);
                            }
                        }
                        first = false;
                    }
                }
                catch (Exception ex) { CouchKit.Log("Achievements watch: " + ex.Message); Thread.Sleep(30000); }
                Thread.Sleep(3000);
            }
        }) { IsBackground = true, Name = "Achievements" };
        th.Start();
    }

    // --- SQLite (winsqlite3.dll, part of Windows) ---

    const int SQLITE_OK = 0, SQLITE_ROW = 100, SQLITE_OPEN_READONLY = 1, SQLITE_NULL = 5;
    [DllImport("winsqlite3.dll")] static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_close(IntPtr db);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_busy_timeout(IntPtr db, int ms);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int bytes, out IntPtr stmt, IntPtr tail);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_bind_text(IntPtr stmt, int index, byte[] text, int bytes, IntPtr destructor);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_step(IntPtr stmt);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_finalize(IntPtr stmt);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_column_type(IntPtr stmt, int col);
    [DllImport("winsqlite3.dll")] static extern long sqlite3_column_int64(IntPtr stmt, int col);
    [DllImport("winsqlite3.dll")] static extern double sqlite3_column_double(IntPtr stmt, int col);
    [DllImport("winsqlite3.dll")] static extern IntPtr sqlite3_column_text(IntPtr stmt, int col);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_column_bytes(IntPtr stmt, int col);
    [DllImport("winsqlite3.dll")] static extern IntPtr sqlite3_errmsg(IntPtr db);

    class Row
    {
        public IntPtr Stmt;
        public bool IsNull(int c) { return sqlite3_column_type(Stmt, c) == SQLITE_NULL; }
        public long Long(int c) { return sqlite3_column_int64(Stmt, c); }
        public int Int(int c) { return (int)sqlite3_column_int64(Stmt, c); }
        public double Double(int c) { return sqlite3_column_double(Stmt, c); }
        public string Text(int c)
        {
            IntPtr p = sqlite3_column_text(Stmt, c);
            if (p == IntPtr.Zero) return "";
            var bytes = new byte[sqlite3_column_bytes(Stmt, c)];
            Marshal.Copy(p, bytes, 0, bytes.Length);
            return Encoding.UTF8.GetString(bytes);
        }
    }

    static byte[] Utf8(string s) { return Encoding.UTF8.GetBytes(s + "\0"); }

    // Opens the database read-only for one query; the add-on keeps writing to it meanwhile
    static void Query(string sql, string[] args, Action<Row> each)
    {
        IntPtr db;
        if (sqlite3_open_v2(Utf8(DbPath), out db, SQLITE_OPEN_READONLY, IntPtr.Zero) != SQLITE_OK)
        {
            string err = db != IntPtr.Zero ? Marshal.PtrToStringAnsi(sqlite3_errmsg(db)) : "can't open";
            if (db != IntPtr.Zero) sqlite3_close(db);
            throw new IOException(err);
        }
        try
        {
            sqlite3_busy_timeout(db, 2000);
            IntPtr stmt;
            if (sqlite3_prepare_v2(db, Utf8(sql), -1, out stmt, IntPtr.Zero) != SQLITE_OK) throw new IOException(Marshal.PtrToStringAnsi(sqlite3_errmsg(db)));
            try
            {
                if (args != null)
                    for (int i = 0; i < args.Length; i++)
                    {
                        var b = Encoding.UTF8.GetBytes(args[i]);
                        sqlite3_bind_text(stmt, i + 1, b, b.Length, new IntPtr(-1) /* SQLITE_TRANSIENT */);
                    }
                var row = new Row { Stmt = stmt };
                while (sqlite3_step(stmt) == SQLITE_ROW) each(row);
            }
            finally { sqlite3_finalize(stmt); }
        }
        finally { sqlite3_close(db); }
    }
}
