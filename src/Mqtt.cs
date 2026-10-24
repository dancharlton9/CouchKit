// Home Assistant over MQTT: CouchKit appears in Home Assistant as a "Gaming PC" device (MQTT
// discovery, no YAML): what's playing (with cover art), session time, CPU/GPU temperature and load,
// memory, Discord call and controller battery. Marks itself offline when CouchKit stops (MQTT
// last will). A minimal MQTT 3.1.1 client (connect, publish, ping) is built in: no libraries.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

static class HomeAssistantMqtt
{
    static SmartHomeSettings S;
    static string id, baseTopic, availability;
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    public static volatile bool Connected;
    public static string Problem = "";

    public static void Start(SmartHomeSettings settings)
    {
        S = settings;
        if (string.IsNullOrEmpty(S.MqttHost)) return;
        id = "couchkit_" + new string(Environment.MachineName.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        baseTopic = "couchkit/" + id;
        availability = baseTopic + "/status";
        new Thread(Run) { IsBackground = true, Name = "MQTT" }.Start();
    }

    static void Run()
    {
        bool logged = false;
        while (true)
        {
            MqttClient client = null;
            try
            {
                client = new MqttClient(S.MqttHost, S.MqttPort > 0 ? S.MqttPort : 1883);
                client.Connect(id, S.MqttUsername, S.MqttPassword, availability, "offline");
                Connected = true; Problem = ""; logged = false;
                CouchKit.Log("MQTT: connected to " + S.MqttHost + ", publishing to Home Assistant as \"" + DeviceName + "\"");
                Announce(client);
                client.Publish(availability, "online", true);
                Loop(client);
            }
            catch (Exception ex)
            {
                Connected = false; Problem = ex.Message;
                if (!logged) { CouchKit.Log("MQTT: " + ex.Message + " (retrying every 30 seconds)"); logged = true; }
            }
            finally { if (client != null) client.Dispose(); }
            Thread.Sleep(30000);
        }
    }

    static string DeviceName { get { return string.IsNullOrEmpty(S.MqttDeviceName) ? "Gaming PC" : S.MqttDeviceName; } }

    // --- Discovery: one retained config message per entity ---

    static void Announce(MqttClient c)
    {
        var device = new Dictionary<string, object>
        {
            { "identifiers", new[] { id } }, { "name", DeviceName }, { "manufacturer", "CouchKit" },
            { "model", "Windows PC" }, { "sw_version", System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString(3) },
        };
        string slug = new string(DeviceName.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray()).Trim('_');
        Action<string, string, string, Dictionary<string, object>> entity = (component, key, name, extra) =>
        {
            var config = new Dictionary<string, object>
            {
                { "name", name }, { "unique_id", id + "_" + key }, { "object_id", slug + "_" + key },
                { "availability_topic", availability }, { "device", device },
            };
            if (component != "image") config["state_topic"] = baseTopic + "/" + key;
            foreach (var kv in extra) config[kv.Key] = kv.Value;
            c.Publish("homeassistant/" + component + "/" + id + "/" + key + "/config", Json.Serialize(config), true);
        };
        var none = new Dictionary<string, object>();
        entity("sensor", "now_playing", "Now playing", new Dictionary<string, object> { { "icon", "mdi:controller" }, { "json_attributes_topic", baseTopic + "/now_playing_attributes" } });
        entity("binary_sensor", "playing", "Playing", new Dictionary<string, object> { { "icon", "mdi:gamepad-variant" }, { "payload_on", "on" }, { "payload_off", "off" } });
        entity("sensor", "session", "Session", new Dictionary<string, object> { { "icon", "mdi:timer-outline" }, { "unit_of_measurement", "min" }, { "device_class", "duration" } });
        entity("image", "game_art", "Game art", new Dictionary<string, object> { { "image_topic", baseTopic + "/game_art" }, { "content_type", "image/jpeg" } });
        entity("sensor", "cpu_temperature", "CPU temperature", new Dictionary<string, object> { { "unit_of_measurement", "°C" }, { "device_class", "temperature" }, { "state_class", "measurement" } });
        entity("sensor", "cpu_load", "CPU load", new Dictionary<string, object> { { "unit_of_measurement", "%" }, { "icon", "mdi:cpu-64-bit" }, { "state_class", "measurement" } });
        entity("sensor", "gpu_temperature", "GPU temperature", new Dictionary<string, object> { { "unit_of_measurement", "°C" }, { "device_class", "temperature" }, { "state_class", "measurement" } });
        entity("sensor", "gpu_load", "GPU load", new Dictionary<string, object> { { "unit_of_measurement", "%" }, { "icon", "mdi:expansion-card" }, { "state_class", "measurement" } });
        entity("sensor", "gpu_power", "GPU power", new Dictionary<string, object> { { "unit_of_measurement", "W" }, { "device_class", "power" }, { "state_class", "measurement" } });
        entity("sensor", "memory_used", "Memory used", new Dictionary<string, object> { { "unit_of_measurement", "%" }, { "icon", "mdi:memory" }, { "state_class", "measurement" } });
        entity("binary_sensor", "discord_call", "Discord call", new Dictionary<string, object> { { "icon", "mdi:headset" }, { "payload_on", "on" }, { "payload_off", "off" }, { "json_attributes_topic", baseTopic + "/discord_call_attributes" } });
        entity("sensor", "controller_battery", "Controller battery", new Dictionary<string, object> { { "icon", "mdi:microsoft-xbox-controller-battery-medium" } });
    }

    // --- State: published when it changes (temperatures every 30 s) ---

    static readonly Dictionary<string, string> sent = new Dictionary<string, string>();
    static string artFor;

    static void Loop(MqttClient c)
    {
        sent.Clear(); artFor = null;
        var lastStats = DateTime.MinValue;
        var lastPing = DateTime.Now;
        while (true)
        {
            string game = PlayniteLink.NowPlaying;
            bool playing = game.Length > 0;
            Send(c, "now_playing", playing ? game : "Nothing");
            Send(c, "playing", playing ? "on" : "off");
            var current = playing ? PlayniteLink.Current : null;
            var start = PlayniteLink.SessionStart;
            Send(c, "session", playing && start.HasValue ? ((int)(DateTime.Now - start.Value).TotalMinutes).ToString() : "0");
            Send(c, "now_playing_attributes", Json.Serialize(new Dictionary<string, object>
            {
                { "platform", current != null ? current.Platform ?? "" : "" },
                { "played", current != null ? PlayniteLink.Hours(current.Playtime) : "" },
                { "started", playing && start.HasValue ? start.Value.ToString("o") : "" },
            }));
            if ((playing ? game : "") != artFor) { artFor = playing ? game : ""; PublishArt(c, current); }

            Send(c, "discord_call", DiscordBridge.InCall ? "on" : "off");
            Send(c, "discord_call_attributes", Json.Serialize(new Dictionary<string, object> { { "call", DiscordBridge.InCall ? DiscordBridge.CallName : "" }, { "muted", DiscordBridge.Muted } }));
            string battery = "";
            try { battery = CouchKit.ControllerBatteryPercent(CouchKit.ControllerBattery()); } catch { }
            Send(c, "controller_battery", battery.Length > 0 ? battery : "Not connected");

            if ((DateTime.Now - lastStats).TotalSeconds >= 30)
            {
                lastStats = DateTime.Now;
                try { Pc.Sample(); } catch { }
                var st = Pc.Latest;
                Func<double, string> n = v => v >= 0 ? Math.Round(v).ToString() : "";
                if (st.CpuTemp >= 0) Send(c, "cpu_temperature", n(st.CpuTemp), true);
                if (st.CpuLoad >= 0) Send(c, "cpu_load", n(st.CpuLoad), true);
                if (st.GpuTemp >= 0) Send(c, "gpu_temperature", n(st.GpuTemp), true);
                if (st.GpuLoad >= 0) Send(c, "gpu_load", n(st.GpuLoad), true);
                if (st.GpuPower >= 0) Send(c, "gpu_power", n(st.GpuPower), true);
                if (st.MemTotalGb > 0) Send(c, "memory_used", n(st.MemUsedGb * 100 / st.MemTotalGb), true);
            }
            if ((DateTime.Now - lastPing).TotalSeconds >= 30) { c.Ping(); lastPing = DateTime.Now; }
            Thread.Sleep(5000);
        }
    }

    static void Send(MqttClient c, string key, string value, bool always = false)
    {
        string was;
        if (!always && sent.TryGetValue(key, out was) && was == value) return;
        sent[key] = value;
        c.Publish(baseTopic + "/" + key, value, true);
    }

    // The game's cover, shrunk to 400 px tall, as the image entity; a blank pixel when nothing's playing
    static void PublishArt(MqttClient c, PlayniteLink.Game game)
    {
        byte[] jpeg = null;
        try
        {
            string file = game != null ? (!string.IsNullOrEmpty(game.Cover) ? game.Cover : game.Background) : null;
            if (!string.IsNullOrEmpty(file) && File.Exists(file))
                using (var src = Image.FromFile(file))
                {
                    int h = Math.Min(400, src.Height), w = Math.Max(1, src.Width * h / src.Height);
                    using (var bmp = new Bitmap(w, h))
                    {
                        using (var g = Graphics.FromImage(bmp)) { g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic; g.DrawImage(src, 0, 0, w, h); }
                        using (var ms = new MemoryStream()) { bmp.Save(ms, ImageFormat.Jpeg); jpeg = ms.ToArray(); }
                    }
                }
        }
        catch (Exception ex) { CouchKit.Log("MQTT: game art: " + ex.Message); }
        if (jpeg == null)
            using (var bmp = new Bitmap(1, 1))
            using (var ms = new MemoryStream()) { bmp.SetPixel(0, 0, Color.FromArgb(17, 17, 27)); bmp.Save(ms, ImageFormat.Jpeg); jpeg = ms.ToArray(); }
        c.Publish(baseTopic + "/game_art", jpeg, true);
    }
}

// Just enough MQTT 3.1.1 for publishing: CONNECT (with login and last will), PUBLISH (QoS 0), PINGREQ.
sealed class MqttClient : IDisposable
{
    readonly TcpClient tcp;
    readonly NetworkStream stream;
    readonly object writeLock = new object();

    public MqttClient(string host, int port)
    {
        tcp = new TcpClient();
        if (!tcp.ConnectAsync(host, port).Wait(8000)) throw new IOException("can't reach " + host + ":" + port);
        stream = tcp.GetStream();
        stream.ReadTimeout = 10000;
    }

    public void Connect(string clientId, string user, string pass, string willTopic, string willMessage)
    {
        var vh = new List<byte> { 0, 4, (byte)'M', (byte)'Q', (byte)'T', (byte)'T', 4 };
        byte flags = 0x02;                          // clean session
        flags |= 0x04 | 0x20;                       // last will, retained
        if (!string.IsNullOrEmpty(user)) flags |= 0x80;
        if (!string.IsNullOrEmpty(pass)) flags |= 0x40;
        vh.Add(flags); vh.Add(0); vh.Add(60);       // keep-alive 60 s
        var payload = new List<byte>();
        payload.AddRange(Str(clientId)); payload.AddRange(Str(willTopic)); payload.AddRange(Str(willMessage));
        if (!string.IsNullOrEmpty(user)) payload.AddRange(Str(user));
        if (!string.IsNullOrEmpty(pass)) payload.AddRange(Str(pass));
        Write(0x10, vh.Concat(payload).ToArray());
        var ack = new byte[4];
        int got = 0;
        while (got < 4) { int n = stream.Read(ack, got, 4 - got); if (n <= 0) throw new IOException("broker closed the connection"); got += n; }
        if (ack[0] != 0x20) throw new IOException("unexpected reply from broker");
        if (ack[3] != 0)
            throw new IOException(ack[3] == 4 || ack[3] == 5 ? "broker refused the login: check the MQTT username and password" : "broker refused the connection (code " + ack[3] + ")");
    }

    public void Publish(string topic, string message, bool retain) { Publish(topic, Encoding.UTF8.GetBytes(message), retain); }

    public void Publish(string topic, byte[] message, bool retain)
    {
        var body = new List<byte>(Str(topic));
        body.AddRange(message);
        Write((byte)(0x30 | (retain ? 1 : 0)), body.ToArray());
    }

    public void Ping()
    {
        Write(0xC0, new byte[0]);
        while (tcp.Available > 0) stream.ReadByte();   // discard PINGRESP
    }

    static byte[] Str(string s)
    {
        var b = Encoding.UTF8.GetBytes(s ?? "");
        return new[] { (byte)(b.Length >> 8), (byte)(b.Length & 0xFF) }.Concat(b).ToArray();
    }

    void Write(byte header, byte[] body)
    {
        var packet = new List<byte> { header };
        int len = body.Length;
        do { byte d = (byte)(len % 128); len /= 128; if (len > 0) d |= 0x80; packet.Add(d); } while (len > 0);
        packet.AddRange(body);
        lock (writeLock) stream.Write(packet.ToArray(), 0, packet.Count);
    }

    // Closing without DISCONNECT makes the broker publish the last will ("offline")
    public void Dispose() { try { tcp.Close(); } catch { } }
}
