// Lighting: drives every OpenRGB device (via OpenRGB's SDK server) with a flowing gradient.
//   - Normally: the configured palette, saturated so it reads vivid on LEDs.
//   - While a Playnite game runs: colours picked from that game's art (Playnite's "after game
//     started" script writes the image path to current-game-art.txt), or the palette if the art
//     has no strong colours.
// Reconnects whenever OpenRGB is restarted; does nothing while it isn't running.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;

static class Lighting
{
    const int WheelSize = 1024;
    static LightingSettings L = new LightingSettings();

    [System.Runtime.InteropServices.DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);

    public static readonly string ArtPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "current-game-art.txt");
    public static Action<string> Log = _ => { };

    public static void Start(LightingSettings settings)
    {
        L = settings;
        var t = new Thread(Run) { IsBackground = true, Name = "Lighting" };
        t.Start();
    }

    static void Run()
    {
        timeBeginPeriod(1);   // 1 ms sleeps, so frames land evenly instead of on the default 15.6 ms ticks
        byte[][] house;
        try { house = Wheel(L.Palette.Select(Vivid).ToList()); }
        catch (Exception ex)
        {
            Log("Lighting: palette in couchkit.json is not a list of #rrggbb colours (" + ex.Message + "), using the default");
            house = Wheel(new LightingSettings().Palette.Select(Vivid).ToList());
        }
        bool loggedOffline = false;
        while (true)
        {
            try
            {
                using (var client = new TcpClient())
                {
                    client.Connect(L.OpenRgbHost, L.OpenRgbPort);
                    var ns = client.GetStream();
                    List<int[]> devices = Connect(ns);
                    Log("Lighting: connected to OpenRGB, " + devices.Count + " devices");
                    loggedOffline = false;
                    Animate(ns, devices, house);
                }
            }
            catch (Exception ex)
            {
                if (!loggedOffline) { Log("Lighting: OpenRGB not available (" + ex.Message + "), retrying"); loggedOffline = true; }
            }
            Thread.Sleep(5000);
        }
    }

    static void Animate(NetworkStream ns, List<int[]> devices, byte[][] house)
    {
        string artFile = null;
        byte[][] wheel = house;
        double cycle = L.BrowseCycleSeconds;
        double phase = 0;
        var clock = Stopwatch.StartNew();
        var artCheck = Stopwatch.StartNew();
        long last = 0;
        bool first = true;

        while (true)
        {
            if (first || artCheck.ElapsedMilliseconds >= 1000)
            {
                first = false;
                artCheck.Restart();
                string art = null;
                try { if (File.Exists(ArtPath)) art = File.ReadAllText(ArtPath).Trim(); } catch { }
                if (art != artFile)
                {
                    artFile = art;
                    List<int[]> picked = string.IsNullOrEmpty(art) || !L.GameColours ? null : PaletteFromImage(art);
                    wheel = picked != null ? Wheel(picked) : house;
                    cycle = string.IsNullOrEmpty(art) ? L.BrowseCycleSeconds : L.GameCycleSeconds;
                    Log(string.IsNullOrEmpty(art) ? "Lighting: house palette"
                        : !L.GameColours ? "Lighting: game running (game colours off)"
                        : picked != null ? "Lighting: game palette " + string.Join(" ", picked.Select(Hex)) + " from " + Path.GetFileName(art)
                        : "Lighting: no strong colours in " + Path.GetFileName(art) + ", using house palette");
                }
            }

            long now = clock.ElapsedMilliseconds;
            phase = (phase + (now - last) / 1000.0 / cycle) % 1;   // advance by elapsed time so speed changes don't jump
            last = now;

            for (int d = 0; d < devices.Count; d++)
            {
                int total = devices[d].Sum();
                var buf = new byte[6 + 4 * total];
                BitConverter.GetBytes((uint)buf.Length).CopyTo(buf, 0);
                BitConverter.GetBytes((ushort)total).CopyTo(buf, 4);
                int o = 6;
                foreach (int n in devices[d])
                    for (int i = 0; i < n; i++, o += 4)
                        wheel[(int)(((double)i / n * L.Span + phase) % 1 * (WheelSize - 1))].CopyTo(buf, o);
                Send(ns, d, 1050, buf);   // UpdateLEDs
            }
            Thread.Sleep(Math.Max(1, (int)(1000 / Math.Max(1, L.Fps) - (clock.ElapsedMilliseconds - now))));
        }
    }

    // --- OpenRGB SDK (protocol version 0) ---

    // Returns the LED count of each zone, per controller, and puts every controller in its direct mode.
    static List<int[]> Connect(NetworkStream ns)
    {
        Send(ns, 0, 50, Encoding.ASCII.GetBytes("CouchKit\0"));   // client name
        Send(ns, 0, 0, new byte[0]);
        int count = (int)BitConverter.ToUInt32(Receive(ns), 0);
        var devices = new List<int[]>();
        for (int d = 0; d < count; d++)
        {
            Send(ns, d, 1, new byte[0]);
            devices.Add(ZoneSizes(Receive(ns)));
            Send(ns, d, 1100, new byte[0]);   // SetCustomMode
        }
        return devices;
    }

    static int[] ZoneSizes(byte[] b)
    {
        int p = 4;
        Func<uint> u32 = () => { uint v = BitConverter.ToUInt32(b, p); p += 4; return v; };
        Func<ushort> u16 = () => { ushort v = BitConverter.ToUInt16(b, p); p += 2; return v; };
        Action str = () => { int l = u16(); p += l; };
        u32(); str(); str(); str(); str(); str();               // type, name, description, version, serial, location
        int modes = u16(); u32();                               // mode count, active mode
        for (int m = 0; m < modes; m++) { str(); p += 4 * 9; int colours = u16(); p += 4 * colours; }
        int zones = u16();
        var sizes = new int[zones];
        for (int z = 0; z < zones; z++) { str(); p += 4 * 3; sizes[z] = (int)u32(); int matrix = u16(); p += matrix; }
        return sizes;
    }

    static void Send(NetworkStream ns, int dev, int id, byte[] data)
    {
        var msg = new byte[16 + data.Length];
        Encoding.ASCII.GetBytes("ORGB").CopyTo(msg, 0);
        BitConverter.GetBytes((uint)dev).CopyTo(msg, 4);
        BitConverter.GetBytes((uint)id).CopyTo(msg, 8);
        BitConverter.GetBytes((uint)data.Length).CopyTo(msg, 12);
        data.CopyTo(msg, 16);
        ns.Write(msg, 0, msg.Length);
    }

    static byte[] Receive(NetworkStream ns)
    {
        byte[] header = ReadExactly(ns, 16);
        return ReadExactly(ns, (int)BitConverter.ToUInt32(header, 12));
    }

    static byte[] ReadExactly(NetworkStream ns, int n)
    {
        var b = new byte[n];
        for (int o = 0; o < n; )
        {
            int r = ns.Read(b, o, n - o);
            if (r <= 0) throw new IOException("connection closed");
            o += r;
        }
        return b;
    }

    // --- Colour ---

    // 1024-step colour wheel through the stops and back again, so the flow loops seamlessly.
    static byte[][] Wheel(List<int[]> stops)
    {
        var loop = new List<int[]>(stops);
        for (int i = stops.Count - 2; i >= 1; i--) loop.Add(stops[i]);
        loop.Add(stops[0]);
        var wheel = new byte[WheelSize][];
        for (int k = 0; k < WheelSize; k++)
        {
            double t = k / (double)WheelSize * (loop.Count - 1);
            int s = (int)t; double f = t - s;
            int[] a = loop[s], c = loop[s + 1];
            wheel[k] = new byte[] { (byte)(a[0] + (c[0] - a[0]) * f), (byte)(a[1] + (c[1] - a[1]) * f), (byte)(a[2] + (c[2] - a[2]) * f), 0 };
        }
        return wheel;
    }

    // Same hue at full saturation and brightness: pastels look washed out on LEDs.
    static int[] Vivid(string hex)
    {
        var c = ColorTranslator.FromHtml(hex);
        return FromHue(c.GetHue());
    }

    static int[] FromHue(double h)
    {
        h = (h % 360 + 360) % 360;
        double x = 1 - Math.Abs(h / 60 % 2 - 1);
        double[] c;
        switch ((int)(h / 60) % 6)
        {
            case 0: c = new[] { 1, x, 0 }; break;
            case 1: c = new[] { x, 1, 0 }; break;
            case 2: c = new[] { 0, 1, x }; break;
            case 3: c = new[] { 0, x, 1 }; break;
            case 4: c = new[] { x, 0, 1 }; break;
            default: c = new[] { 1, 0, x }; break;
        }
        return new[] { (int)(c[0] * 255), (int)(c[1] * 255), (int)(c[2] * 255) };
    }

    static string Hex(int[] c) { return string.Format("#{0:x2}{1:x2}{2:x2}", c[0], c[1], c[2]); }

    // Up to three dominant hues from the image's colourful pixels (greys, blacks and whites ignored),
    // ordered round the colour wheel so the gradient between them stays smooth. Null if the art is
    // too colourless to say anything.
    static List<int[]> PaletteFromImage(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var bins = new double[36];   // 10-degree hue buckets, weighted by how vivid each pixel is
            double colourful = 0, total = 0;
            using (var src = Image.FromFile(path))
            using (var small = new Bitmap(src, 64, 64))
            {
                for (int y = 0; y < small.Height; y++)
                    for (int x = 0; x < small.Width; x++)
                    {
                        Color px = small.GetPixel(x, y);
                        total++;
                        int max = Math.Max(px.R, Math.Max(px.G, px.B)), min = Math.Min(px.R, Math.Min(px.G, px.B));
                        double v = max / 255.0, s = max == 0 ? 0 : (max - min) / (double)max;
                        if (s < 0.35 || v < 0.25) continue;
                        bins[(int)(px.GetHue() / 10) % 36] += s * v;
                        colourful++;
                    }
            }
            if (colourful / total < 0.04) return null;

            // Smooth neighbouring buckets, then take peaks at least 40 degrees apart
            var smooth = new double[36];
            for (int i = 0; i < 36; i++) smooth[i] = bins[(i + 35) % 36] * 0.5 + bins[i] + bins[(i + 1) % 36] * 0.5;
            var peaks = new List<int>();
            foreach (int i in Enumerable.Range(0, 36).OrderByDescending(i => smooth[i]))
            {
                if (smooth[i] < smooth.Max() * 0.2 || peaks.Count == 3) break;
                if (peaks.All(p => Math.Min(Math.Abs(p - i), 36 - Math.Abs(p - i)) >= 4)) peaks.Add(i);
            }
            var hues = peaks.Select(i => i * 10 + 5.0).ToList();
            if (hues.Count == 1) hues = new List<double> { hues[0] - 20, hues[0], hues[0] + 20 };   // one strong colour: shade either side of it
            hues.Sort();
            return hues.Select(FromHue).ToList();
        }
        catch (Exception ex)
        {
            Log("Lighting: could not read " + path + ": " + ex.Message);
            return null;
        }
    }

    // For testing: CouchKit.exe --palette <image> prints what a game's art would produce.
    public static string Describe(string path)
    {
        var p = PaletteFromImage(path);
        return p == null ? "house palette (no strong colours)" : string.Join(" ", p.Select(Hex));
    }
}
