// Pc: the PC's status for the Guide's System tab: CPU and GPU temperature and load, memory,
// uptime and drive space.
//  - Loads come from Windows' performance counters; GPU temperature, load and power from
//    nvidia-smi when there's an NVIDIA card.
//  - CPU temperature needs a hardware driver, which only admin programs can use. FanControl
//    already reads it, and shares its sensors over a named pipe that only admin programs may open.
//    So a small helper (CouchKit.exe --sensors, started at sign-in by a scheduled task with admin
//    rights) asks FanControl for its sensors every few seconds and writes them to sensors.json,
//    which the Guide reads. The helper only reads; it never touches the fans.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;

static class Pc
{
    public class Status
    {
        public double CpuLoad = -1, CpuTemp = -1, GpuLoad = -1, GpuTemp = -1, GpuPower = -1;
        public double MemUsedGb, MemTotalGb;
        public TimeSpan Uptime;
        public string GpuName = "";
    }

    public class Drive { public string Name, Label; public double FreeGb, TotalGb; }

    public class Sensor { public string Name { get; set; } public string Origin { get; set; } public string Type { get; set; } public double Value { get; set; } }
    public class SensorFile { public string Time { get; set; } public List<Sensor> Sensors { get; set; } }

    static readonly string SensorsPath = Path.Combine(Settings.DataDir, "sensors.json");
    static Status latest = new Status();
    static List<Drive> drives = new List<Drive>();
    static DateTime drivesAt = DateTime.MinValue;
    static Thread poller;
    public static volatile bool Watching;   // set while the System tab is showing

    public static Status Latest { get { return latest; } }

    // Starts the background poller (once). It only does work while the Guide shows the System tab.
    public static void Start()
    {
        if (poller != null) return;
        poller = new Thread(Poll) { IsBackground = true, Name = "PcStatus" };
        poller.Start();
    }

    static PerformanceCounter cpu;

    static void Poll()
    {
        while (true)
        {
            if (!Watching) { Thread.Sleep(500); continue; }
            Sample();
            Thread.Sleep(2000);
        }
    }

    // One reading of everything (the first CPU reading is only meaningful after a second one)
    public static void Sample()
    {
        if (cpu == null)
            try { cpu = new PerformanceCounter("Processor Information", "% Processor Utility", "_Total"); cpu.NextValue(); Thread.Sleep(500); }
            catch (Exception ex) { CouchKit.Log("PC status: CPU counter: " + ex.Message); }
        {
            var s = new Status();
            try { if (cpu != null) s.CpuLoad = Math.Min(100, cpu.NextValue()); } catch { }
            try
            {
                var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)) };
                if (GlobalMemoryStatusEx(ref m)) { s.MemTotalGb = m.ullTotalPhys / 1073741824.0; s.MemUsedGb = (m.ullTotalPhys - m.ullAvailPhys) / 1073741824.0; }
            }
            catch { }
            s.Uptime = TimeSpan.FromMilliseconds(GetTickCount64());
            Nvidia(s);
            FromSensors(s);
            latest = s;
        }
    }

    static void Nvidia(Status s)
    {
        try
        {
            string smi = Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe");
            if (!File.Exists(smi)) return;
            var psi = new ProcessStartInfo(smi, "--query-gpu=name,temperature.gpu,utilization.gpu,power.draw --format=csv,noheader,nounits")
                { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
            using (var p = Process.Start(psi))
            {
                string line = p.StandardOutput.ReadLine() ?? "";
                p.WaitForExit(3000);
                var parts = line.Split(',').Select(x => x.Trim()).ToArray();
                if (parts.Length < 4) return;
                s.GpuName = parts[0].Replace("NVIDIA ", "").Replace("GeForce ", "");
                double v;
                if (double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v)) s.GpuTemp = v;
                if (double.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v)) s.GpuLoad = v;
                if (double.TryParse(parts[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v)) s.GpuPower = v;
            }
        }
        catch { }
    }

    // CPU temperature (and GPU, if nvidia-smi isn't there) from the helper's sensors.json
    static void FromSensors(Status s)
    {
        try
        {
            if (!File.Exists(SensorsPath) || (DateTime.Now - File.GetLastWriteTime(SensorsPath)).TotalSeconds > 20) return;
            var file = new JavaScriptSerializer().Deserialize<SensorFile>(File.ReadAllText(SensorsPath));
            if (file == null || file.Sensors == null) return;
            var temps = file.Sensors.Where(x => x.Type == "Temperature" && x.Value > 0).ToList();
            // Ryzen: "Core (Tctl/Tdie)"; Intel: "CPU Package"; otherwise anything from the CPU
            var cpu = temps.FirstOrDefault(x => x.Name.Contains("Tctl")) ?? temps.FirstOrDefault(x => x.Name.Contains("Package") && !x.Origin.Contains("GPU"))
                      ?? temps.FirstOrDefault(x => x.Origin.Contains("Ryzen") || x.Origin.Contains("Intel") || x.Origin.Contains("CPU"));
            if (cpu != null) s.CpuTemp = cpu.Value;
            if (s.GpuTemp < 0)
            {
                var gpu = temps.FirstOrDefault(x => x.Name == "GPU Core" || (x.Origin.Contains("GPU") || x.Origin.Contains("Radeon") || x.Origin.Contains("GeForce")) && x.Name.Contains("Core"));
                if (gpu != null) s.GpuTemp = gpu.Value;
            }
        }
        catch { }
    }

    // Fixed drives with their free space, refreshed at most every 10 seconds
    public static List<Drive> Drives()
    {
        if ((DateTime.Now - drivesAt).TotalSeconds < 10) return drives;
        var list = new List<Drive>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                list.Add(new Drive { Name = d.Name.TrimEnd('\\'), Label = d.VolumeLabel, FreeGb = d.AvailableFreeSpace / 1073741824.0, TotalGb = d.TotalSize / 1073741824.0 });
            }
            catch { }
        }
        drives = list; drivesAt = DateTime.Now;
        return list;
    }

    public static string Size(double gb) { return gb >= 1000 ? (gb / 1024).ToString("0.#") + " TB" : ((int)Math.Round(gb)) + " GB"; }

    public static string Uptime(TimeSpan t)
    {
        if (t.TotalDays >= 1) return (int)t.TotalDays + " d " + t.Hours + " h";
        if (t.TotalHours >= 1) return (int)t.TotalHours + " h " + t.Minutes + " min";
        return t.Minutes + " min";
    }

    [DllImport("kernel32.dll")] static extern ulong GetTickCount64();
    [StructLayout(LayoutKind.Sequential)]
    struct MEMORYSTATUSEX { public uint dwLength, dwMemoryLoad; public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual; }
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

    // --- The sensors helper (runs with admin rights) ---

    // Registers "CouchKit sensors": at sign-in, with admin rights, no time limit; then starts it.
    // Run as admin (the settings page asks Windows to elevate CouchKit.exe --install-sensors).
    public static void InstallSensorsTask()
    {
        string exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CouchKit", "CouchKit.exe");
        string user = Environment.UserDomainName + "\\" + Environment.UserName;
        string xml = "<?xml version=\"1.0\" encoding=\"UTF-16\"?>" +
            "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">" +
            "<RegistrationInfo><Description>Reads CPU and GPU temperatures from FanControl for the CouchKit Guide (read-only)</Description></RegistrationInfo>" +
            "<Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + System.Security.SecurityElement.Escape(user) + "</UserId></LogonTrigger></Triggers>" +
            "<Principals><Principal id=\"Author\"><UserId>" + System.Security.SecurityElement.Escape(user) + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>" +
            "<Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>" +
            "<ExecutionTimeLimit>PT0S</ExecutionTimeLimit><StartWhenAvailable>true</StartWhenAvailable><Enabled>true</Enabled></Settings>" +
            "<Actions Context=\"Author\"><Exec><Command>" + System.Security.SecurityElement.Escape(exe) + "</Command><Arguments>--sensors</Arguments></Exec></Actions></Task>";
        string file = Path.Combine(Path.GetTempPath(), "couchkit-sensors-task.xml");
        File.WriteAllText(file, xml, System.Text.Encoding.Unicode);
        Func<string, int> schtasks = a =>
        {
            using (var p = Process.Start(new ProcessStartInfo("schtasks.exe", a) { UseShellExecute = false, CreateNoWindow = true })) { p.WaitForExit(15000); return p.ExitCode; }
        };
        int created = schtasks("/Create /TN \"CouchKit sensors\" /XML \"" + file + "\" /F");
        File.Delete(file);
        CouchKit.Log(created == 0 ? "Sensors helper task installed" : "Sensors helper task: schtasks failed (" + created + ")");
        if (created == 0) schtasks("/Run /TN \"CouchKit sensors\"");
    }

    // CouchKit.exe --sensors: copies itself aside first, so the Playnite add-on can still update
    // CouchKit.exe while the helper runs, then the copy loops reading FanControl.
    public static void SensorsHelper(bool isCopy)
    {
        bool created;
        using (var mutex = new Mutex(true, "CouchKitSensorsHelper", out created))
        {
            if (!created) return;   // already running
            if (!isCopy)
            {
                mutex.ReleaseMutex();
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CouchKit", "sensors");
                Directory.CreateDirectory(dir);
                string copy = Path.Combine(dir, "CouchKitSensors.exe");
                File.Copy(Assembly.GetExecutingAssembly().Location, copy, true);
                Process.Start(new ProcessStartInfo(copy, "--sensors-copy") { UseShellExecute = false, CreateNoWindow = true });
                return;
            }
            CouchKit.Log("Sensors helper started");
            object client = null;
            Func<object> read = null;
            while (true)
            {
                try
                {
                    if (read == null) read = ConnectFanControl(out client);
                    var reply = read();
                    var list = new List<Sensor>();
                    foreach (var sen in (IEnumerable)reply.GetType().GetProperty("Sensors").GetValue(reply, null))
                    {
                        var t = sen.GetType();
                        if (!(bool)t.GetProperty("HasValue").GetValue(sen, null)) continue;
                        list.Add(new Sensor
                        {
                            Name = (string)t.GetProperty("Name").GetValue(sen, null), Origin = (string)t.GetProperty("Origin").GetValue(sen, null),
                            Type = t.GetProperty("Type").GetValue(sen, null).ToString(), Value = Convert.ToDouble(t.GetProperty("Value").GetValue(sen, null)),
                        });
                    }
                    string json = new JavaScriptSerializer().Serialize(new SensorFile { Time = DateTime.Now.ToString("o"), Sensors = list });
                    string tmp = SensorsPath + ".tmp";
                    File.WriteAllText(tmp, json);
                    if (File.Exists(SensorsPath)) File.Replace(tmp, SensorsPath, null); else File.Move(tmp, SensorsPath);
                    Thread.Sleep(3000);
                }
                catch (Exception ex)
                {
                    read = null;   // FanControl not running yet, or restarted: reconnect
                    if (!(ex is TargetInvocationException) || DateTime.Now.Second < 5) CouchKit.Log("Sensors helper: " + (ex.InnerException ?? ex).Message);
                    Thread.Sleep(15000);
                }
            }
        }
    }

    // FanControl's own client library, loaded from its folder (found from the running process)
    static Func<object> ConnectFanControl(out object client)
    {
        var fc = Process.GetProcessesByName("FanControl").FirstOrDefault();
        if (fc == null) throw new InvalidOperationException("FanControl isn't running");
        string dir = Path.GetDirectoryName(fc.MainModule.FileName);
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            string p = Path.Combine(dir, new AssemblyName(e.Name).Name + ".dll");
            return File.Exists(p) ? Assembly.LoadFrom(p) : null;
        };
        var ipc = Assembly.LoadFrom(Path.Combine(dir, "FanControl.IPC.dll"));
        var grpc = Assembly.LoadFrom(Path.Combine(dir, "Grpc.Core.Api.dll"));
        var c = ipc.GetType("FanControl.IPC.IPCFactory").GetMethod("GetSensorClient").Invoke(null, null);
        client = c;
        var reqType = ipc.GetType("GetAllSensorsRequest");
        var optType = grpc.GetType("Grpc.Core.CallOptions");
        var call = c.GetType().GetMethod("GetAllSensors", new[] { reqType, optType });
        return () =>
        {
            var opts = Activator.CreateInstance(optType, new object[] { null, (DateTime?)DateTime.UtcNow.AddSeconds(3), CancellationToken.None, null, null, null });
            return call.Invoke(c, new[] { Activator.CreateInstance(reqType), opts });
        };
    }
}
