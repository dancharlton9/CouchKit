// Pad: which kind of controller is in use, so the Guide shows the right button faces and names.
// CouchKit reads controllers through XInput (the Xbox standard). PlayStation and Switch
// controllers reach it through a translator such as Steam Input or DS4Windows; the real
// controller is still a connected device, so its maker (USB/Bluetooth vendor ID) tells us the style:
// Sony 054C, Nintendo 057E. Controller.Style in couchkit.json overrides it.
using System;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;
using System.Threading;

static class Pad
{
    public enum Kind { Xbox, PlayStation, Switch, Generic }

    public static volatile Kind Style = Kind.Xbox;
    public static string Setting = "Auto";   // Auto, Xbox, PlayStation, Switch, Generic
    static DateTime checkedAt = DateTime.MinValue;

    // Re-checks every 30 seconds (controllers come and go) on a background thread
    public static void Refresh()
    {
        Kind fixedKind;
        if (Setting != null && !Setting.Equals("Auto", StringComparison.OrdinalIgnoreCase) && Enum.TryParse(Setting, true, out fixedKind)) { Style = fixedKind; return; }
        if ((DateTime.Now - checkedAt).TotalSeconds < 30) return;
        checkedAt = DateTime.Now;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                bool sony = false, nintendo = false, microsoft = false, any = CouchKit.ControllersConnected() > 0;
                using (var q = new ManagementObjectSearcher("SELECT DeviceID FROM Win32_PnPEntity WHERE PNPClass = 'HIDClass' OR PNPClass = 'XboxComposite' OR PNPClass = 'XnaComposite'"))
                    foreach (var d in q.Get())
                    {
                        string id = (d["DeviceID"] as string ?? "").ToUpperInvariant();
                        // USB: VID_054C. Bluetooth: VID&0002054C.
                        if (Regex.IsMatch(id, @"VID[_&](0002)?054C")) sony = true;
                        else if (Regex.IsMatch(id, @"VID[_&](0002)?057E")) nintendo = true;
                        else if (Regex.IsMatch(id, @"VID[_&](0002)?045E")) microsoft = true;
                    }
                var kind = sony ? Kind.PlayStation : nintendo ? Kind.Switch : microsoft || !any ? Kind.Xbox : Kind.Generic;
                if (kind != Style) CouchKit.Log("Controller style: " + kind);
                Style = kind;
            }
            catch (Exception ex) { CouchKit.Log("Controller style: " + ex.Message); }
        });
    }

    // What the controller calls its middle button
    public static string HomeButton
    {
        get
        {
            switch (Style)
            {
                case Kind.PlayStation: return "PS button";
                case Kind.Switch: return "Home button";
                case Kind.Generic: return "Guide button";
                default: return "Xbox button";
            }
        }
    }

    // The face the Guide draws for an Xbox-named button: a label (or "cross"/"circle" shapes for
    // PlayStation) and its colour. CouchKit receives Xbox A for "confirm", whatever it's printed as.
    public static string Face(string xbox)
    {
        switch (Style)
        {
            case Kind.PlayStation:
                switch (xbox) { case "A": return "cross"; case "B": return "circle"; case "LB": return "L1"; case "RB": return "R1"; }
                break;
            case Kind.Switch:
                switch (xbox) { case "LB": return "L"; case "RB": return "R"; }
                break;
        }
        return xbox;
    }
}
