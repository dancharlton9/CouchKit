// UiSound: the Guide's sounds, synthesised at start-up (no sound files to ship). Short, soft and
// tonal, like a console menu: a tick for moving, rising/falling pairs for select/back, a swoosh
// for opening and closing, and chimes for notifications and achievements. Played asynchronously
// through winmm, so a new sound replaces one still playing.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

static class UiSound
{
    public static bool Enabled = true;
    public static double Volume = 0.6;   // 0..1, on top of Windows' volume

    [DllImport("winmm.dll")] static extern bool PlaySound(byte[] sound, IntPtr module, uint flags);
    const uint SND_ASYNC = 0x0001, SND_NODEFAULT = 0x0002, SND_MEMORY = 0x0004;

    static readonly Dictionary<string, byte[]> sounds = new Dictionary<string, byte[]>();
    const int Rate = 44100;

    // A note: frequency (Hz), start and length (s), loudness, and an optional slide to another frequency
    struct Note { public double Freq, To, Start, Length, Gain; }
    static Note N(double freq, double start, double length, double gain, double to = 0) { return new Note { Freq = freq, To = to > 0 ? to : freq, Start = start, Length = length, Gain = gain }; }

    static UiSound() { Build(); }

    // From couchkit.json: Guide.Sounds and Guide.SoundVolume (percent)
    public static void Configure(bool enabled, int volumePercent)
    {
        Enabled = enabled;
        Volume = Math.Max(0, Math.Min(100, volumePercent)) / 100.0;
        Build();
    }

    static void Build()
    {
        sounds["move"] = Render(0.05, N(2200, 0, 0.04, 0.22));
        sounds["select"] = Render(0.16, N(880, 0, 0.07, 0.35), N(1320, 0.055, 0.1, 0.35));
        sounds["back"] = Render(0.16, N(1175, 0, 0.07, 0.3), N(784, 0.055, 0.1, 0.3));
        sounds["tab"] = Render(0.07, N(1600, 0, 0.06, 0.25, 1900));
        sounds["open"] = Render(0.22, N(520, 0, 0.2, 0.3, 1040), N(1560, 0.08, 0.12, 0.12));
        sounds["close"] = Render(0.2, N(1040, 0, 0.18, 0.28, 520));
        sounds["notify"] = Render(0.5, N(1318.5, 0, 0.35, 0.35), N(1975.5, 0.12, 0.38, 0.32));
        sounds["achievement"] = Render(0.9, N(1046.5, 0, 0.45, 0.32), N(1318.5, 0.1, 0.45, 0.32), N(1568, 0.2, 0.5, 0.32),
                                       N(2093, 0.32, 0.55, 0.3), N(4186, 0.34, 0.3, 0.06));
    }

    public static void Play(string name)
    {
        byte[] wav;
        if (!Enabled || !sounds.TryGetValue(name, out wav)) return;
        try { PlaySound(wav, IntPtr.Zero, SND_ASYNC | SND_MEMORY | SND_NODEFAULT); } catch { }
    }

    // Sine tones with a quick attack and exponential decay, plus a little second harmonic so they
    // sound like a soft bell rather than a beep. 16-bit mono WAV in memory.
    static byte[] Render(double seconds, params Note[] notes)
    {
        int n = (int)(seconds * Rate);
        var mix = new double[n];
        foreach (var note in notes)
        {
            int start = (int)(note.Start * Rate), len = (int)(note.Length * Rate);
            double phase = 0;
            for (int i = 0; i < len && start + i < n; i++)
            {
                double t = (double)i / len, f = note.Freq + (note.To - note.Freq) * t;
                phase += 2 * Math.PI * f / Rate;
                double env = Math.Min(1, i / (Rate * 0.004)) * Math.Exp(-5.5 * t);
                mix[start + i] += note.Gain * env * (Math.Sin(phase) + 0.18 * Math.Sin(2 * phase));
            }
        }
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms))
        {
            w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + n * 2); w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write(Encoding.ASCII.GetBytes("data")); w.Write(n * 2);
            for (int i = 0; i < n; i++) w.Write((short)Math.Max(-32767, Math.Min(32767, mix[i] * Volume * 32767 * 0.8)));
            return ms.ToArray();
        }
    }
}
