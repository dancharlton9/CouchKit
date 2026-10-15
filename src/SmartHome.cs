// SmartHome: tells the room about the running game.
//   - Webhook (e.g. Home Assistant): POSTs the game's colours when a game starts and "game_stop"
//     when it ends, so an automation can theme the room's lights and restore them afterwards.
//   - Nanoleaf: plays a flowing animation in the game's colours on the panels (via Nanoleaf's local
//     API, which can show an effect without saving it), and puts back whatever they were showing.
// The game's colours are picked from its artwork the same way as for the PC lighting; art with no
// strong colours uses the lighting palette instead.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

static class SmartHome
{
    static SmartHomeSettings S = new SmartHomeSettings();
    static LightingSettings L = new LightingSettings();
    public static Action<string> Log = _ => { };

    static readonly string GamePath = Path.Combine(Settings.DataDir, "current-game.txt");
    static readonly string ArtPath = Path.Combine(Settings.DataDir, "current-game-art.txt");
    static readonly string NamePath = Path.Combine(Settings.DataDir, "current-game-name.txt");
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

    public static void Start(SmartHomeSettings settings, LightingSettings lighting)
    {
        S = settings;
        L = lighting;
        if (string.IsNullOrEmpty(S.WebhookUrl) && string.IsNullOrEmpty(S.NanoleafHost)) return;   // (Home Assistant calls still work)
        new Thread(Run) { IsBackground = true, Name = "SmartHome" }.Start();
    }

    static void Run()
    {
        bool inGame = false;
        string lastArt = null;
        Dictionary<string, object> nanoleafBefore = null;
        while (true)
        {
            try
            {
                bool running = File.Exists(GamePath);
                if (running)
                {
                    Thread.Sleep(500);   // the add-on writes the art and name files just after the game file
                    string art = ReadOrEmpty(ArtPath);
                    if (!inGame || art != lastArt)
                    {
                        List<int[]> colours = (art.Length > 0 ? Lighting.PaletteFromImage(art) : null) ?? L.Palette.Select(Lighting.Vivid).ToList();
                        string name = ReadOrEmpty(NamePath);
                        Log("SmartHome: game start (" + (name.Length > 0 ? name : "unknown game") + ")");
                        if (!inGame) nanoleafBefore = NanoleafState();
                        Webhook(new Dictionary<string, object> { { "event", "game_start" }, { "game", name }, { "colors", colours } });
                        NanoleafFlow(colours);
                        inGame = true;
                        lastArt = art;
                    }
                }
                else if (inGame)
                {
                    Log("SmartHome: game stop");
                    Webhook(new Dictionary<string, object> { { "event", "game_stop" } });
                    NanoleafRestore(nanoleafBefore);
                    inGame = false;
                    lastArt = null;
                    nanoleafBefore = null;
                }
            }
            catch (Exception ex) { Log("SmartHome: " + ex.Message); }
            Thread.Sleep(1000);
        }
    }

    static string ReadOrEmpty(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : ""; } catch { return ""; }
    }

    static void Webhook(Dictionary<string, object> body)
    {
        if (string.IsNullOrEmpty(S.WebhookUrl)) return;
        try { Send("POST", S.WebhookUrl, Json.Serialize(body)); }
        catch (Exception ex) { Log("SmartHome: webhook failed: " + ex.Message); }
    }

    // --- Nanoleaf (local API on port 16021) ---

    static string NanoleafUrl(string path)
    {
        return "http://" + S.NanoleafHost + ":16021/api/v1/" + S.NanoleafToken + path;
    }

    static void NanoleafFlow(List<int[]> colours)
    {
        if (string.IsNullOrEmpty(S.NanoleafHost)) return;
        var palette = colours.Select(c => new Dictionary<string, object> {
            { "hue", (int)Color.FromArgb(c[0], c[1], c[2]).GetHue() }, { "saturation", 100 }, { "brightness", 100 }, { "probability", 0 } }).ToList();
        var write = new Dictionary<string, object> {
            { "command", "display" }, { "version", "2.0" }, { "animType", "plugin" }, { "colorType", "HSB" },
            { "palette", palette }, { "pluginType", "color" },
            { "pluginUuid", "027842e4-e1d6-4a4c-a731-be74a1ebd4cf" },   // Nanoleaf's built-in "Flow" animation
            { "pluginOptions", new object[] {
                new Dictionary<string, object> { { "name", "delayTime" }, { "value", 0 } },
                new Dictionary<string, object> { { "name", "linDirection" }, { "value", "right" } },
                new Dictionary<string, object> { { "name", "loop" }, { "value", true } },
                new Dictionary<string, object> { { "name", "transTime" }, { "value", S.NanoleafTransitionTime } } } } };
        try { Send("PUT", NanoleafUrl("/effects"), Json.Serialize(new Dictionary<string, object> { { "write", write } })); }
        catch (Exception ex) { Log("SmartHome: Nanoleaf animation failed: " + ex.Message); }
    }

    // What the panels show now: a named effect, or a plain colour / white.
    static Dictionary<string, object> NanoleafState()
    {
        if (string.IsNullOrEmpty(S.NanoleafHost)) return null;
        try { return Json.Deserialize<Dictionary<string, object>>(Send("GET", NanoleafUrl(""), null)); }
        catch (Exception ex) { Log("SmartHome: could not read Nanoleaf state: " + ex.Message); return null; }
    }

    static void NanoleafRestore(Dictionary<string, object> before)
    {
        if (before == null) return;
        try
        {
            var state = (Dictionary<string, object>)before["state"];
            string effect = (string)((Dictionary<string, object>)before["effects"])["select"];
            if (!effect.StartsWith("*"))   // a saved effect such as "90s Vaporwave"
                Send("PUT", NanoleafUrl("/effects"), Json.Serialize(new Dictionary<string, object> { { "select", effect } }));
            else
            {
                var body = new Dictionary<string, object> { { "brightness", new Dictionary<string, object> { { "value", Value(state, "brightness") }, { "duration", 2 } } } };
                if ((string)state["colorMode"] == "ct") body["ct"] = new Dictionary<string, object> { { "value", Value(state, "ct") } };
                else { body["hue"] = new Dictionary<string, object> { { "value", Value(state, "hue") } }; body["sat"] = new Dictionary<string, object> { { "value", Value(state, "sat") } }; }
                Send("PUT", NanoleafUrl("/state"), Json.Serialize(body));
            }
            if (!(bool)((Dictionary<string, object>)state["on"])["value"])
                Send("PUT", NanoleafUrl("/state"), Json.Serialize(new Dictionary<string, object> { { "on", new Dictionary<string, object> { { "value", false } } } }));
        }
        catch (Exception ex) { Log("SmartHome: Nanoleaf restore failed: " + ex.Message); }
    }

    static object Value(Dictionary<string, object> state, string key)
    {
        return ((Dictionary<string, object>)state[key])["value"];
    }

    static string Send(string method, string url, string json)
    {
        var req = (HttpWebRequest)WebRequest.Create(url);
        req.Method = method;
        req.Timeout = 5000;
        if (json != null)
        {
            byte[] data = Encoding.UTF8.GetBytes(json);
            req.ContentType = "application/json";
            req.ContentLength = data.Length;
            using (var s = req.GetRequestStream()) s.Write(data, 0, data.Length);
        }
        using (var resp = (HttpWebResponse)req.GetResponse())
        using (var r = new StreamReader(resp.GetResponseStream()))
            return r.ReadToEnd();
    }

    // --- Home Assistant (the Guide's Lights tab) ---

    public static bool HasHomeAssistant { get { return !string.IsNullOrEmpty(S.HomeAssistantUrl) && !string.IsNullOrEmpty(S.HomeAssistantToken); } }
    public static string[] Scenes { get { return S.LightScenes ?? new string[0]; } }
    static readonly Dictionary<string, string> friendly = new Dictionary<string, string>();

    // An entity's friendly name ("Office Gaming"), looked up once
    // --showcase: made-up scene names without asking Home Assistant
    public static void PretendName(string entity, string name) { lock (friendly) friendly[entity] = name; }

    public static string NameOf(string entity)
    {
        lock (friendly) { string n; if (friendly.TryGetValue(entity, out n)) return n; }
        string name = entity.Substring(entity.IndexOf('.') + 1).Replace('_', ' ');
        try
        {
            var state = Json.Deserialize<Dictionary<string, object>>(HomeAssistant("GET", "/api/states/" + entity, null));
            var attrs = (Dictionary<string, object>)state["attributes"];
            object fn;
            if (attrs.TryGetValue("friendly_name", out fn) && fn != null) name = fn.ToString();
        }
        catch { }
        lock (friendly) friendly[entity] = name;
        return name;
    }

    public static string Activate(string entity)
    {
        string domain = entity.Substring(0, entity.IndexOf('.'));
        string service = domain == "scene" || domain == "script" ? "turn_on" : "toggle";
        HomeAssistant("POST", "/api/services/" + domain + "/" + service, Json.Serialize(new Dictionary<string, object> { { "entity_id", entity } }));
        return NameOf(entity) + " on";
    }

    public static bool RoomLightsOn()
    {
        foreach (var e in S.RoomLights ?? new string[0])
        {
            try { if ((string)Json.Deserialize<Dictionary<string, object>>(HomeAssistant("GET", "/api/states/" + e, null))["state"] == "on") return true; } catch { }
        }
        return false;
    }

    public static string SetRoomLights(bool on)
    {
        if (S.RoomLights == null || S.RoomLights.Length == 0) return "No room lights set";
        HomeAssistant("POST", "/api/services/light/" + (on ? "turn_on" : "turn_off"), Json.Serialize(new Dictionary<string, object> { { "entity_id", S.RoomLights } }));
        return on ? "Room lights on" : "Room lights off";
    }

    static string HomeAssistant(string method, string path, string json)
    {
        var req = (HttpWebRequest)WebRequest.Create(S.HomeAssistantUrl.TrimEnd('/') + path);
        req.Method = method;
        req.Timeout = 5000;
        req.Headers["Authorization"] = "Bearer " + S.HomeAssistantToken;
        if (json != null)
        {
            byte[] data = Encoding.UTF8.GetBytes(json);
            req.ContentType = "application/json";
            req.ContentLength = data.Length;
            using (var s = req.GetRequestStream()) s.Write(data, 0, data.Length);
        }
        using (var resp = (HttpWebResponse)req.GetResponse())
        using (var r = new StreamReader(resp.GetResponseStream()))
            return r.ReadToEnd();
    }
}