// Hallow Home - smart alerts, better than the vendor apps':
//   jam            a scheduled meal time passed but no meal was dispensed (the feeder's "last feeding" didn't move, today's meal
//                  count didn't go up, or its own dispenser-problem sensor is on)
//   food-low       the feeder's food level is low
//   water-low      the fountain's water is low
//   pump           the fountain's pump has been stopped for a while (an intermittent mode doesn't count)
//   filter         the filter (or cleaning) is due
//   intake-low     today's water intake is well under the 7-day average for this time of day (a health hint, not medical advice)
//   camera-offline a camera has been unavailable for a few minutes
//   motion-away    motion on a camera while you're away (off by default; Hallow Places can tell "away" later)
// De-duplicated (one message when it starts, not every check; it can come back only after it has been fixed for 10 minutes),
// quiet hours (held, then sent when they end if still true), and quiet during games (the PC stays quiet; the phone still gets it
// unless "hold everything during games" is on). Reminders ("every 12 hours until fixed") are automations (Automations.cs).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace HallowHome
{
    public class Alert
    {
        public string Key, Rule, Title, Text; public bool Bad; public DateTime Since;
        public override string ToString() { return Key; }
    }

    public static class Quiet
    {
        public static bool ValidTime(string s) { DateTime d; return s != null && DateTime.TryParseExact(s, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out d); }
        static TimeSpan T(string s) { return DateTime.ParseExact(s, "HH:mm", CultureInfo.InvariantCulture).TimeOfDay; }
        // inside start..end, across midnight when end < start; start == end = never quiet
        public static bool IsQuiet(DateTime now, string start, string end)
        {
            if (!ValidTime(start) || !ValidTime(end)) return false;
            var a = T(start); var b = T(end); var t = now.TimeOfDay;
            if (a == b) return false;
            return a < b ? t >= a && t < b : t >= a || t < b;
        }
    }

    // the fountain's daily water intake, one number per day (the most the day's counter reached), for the 7-day average
    public class History
    {
        public Dictionary<string, double> Intake = new Dictionary<string, double>();
        public string File;   // null = in memory (tests, --snap)
        public static string Day(DateTime d) { return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); }
        public void Record(DateTime now, double value)
        {
            var k = Day(now); double old;
            if (!Intake.TryGetValue(k, out old) || value > old) { Intake[k] = value; Save(); }
        }
        // the average of the 7 days before `today` that have a value; null with fewer than 3 days
        public double? Avg7(DateTime today)
        {
            var v = new List<double>();
            for (int i = 1; i <= 7; i++) { double x; if (Intake.TryGetValue(Day(today.AddDays(-i)), out x)) v.Add(x); }
            return v.Count >= 3 ? v.Average() : (double?)null;
        }
        public static History Load(string file)
        {
            var h = new History { File = file };
            try
            {
                if (file != null && System.IO.File.Exists(file))
                {
                    var d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(System.IO.File.ReadAllText(file)); object o;
                    if (d != null && d.TryGetValue("intake", out o) && o is Dictionary<string, object>)
                        foreach (var kv in (Dictionary<string, object>)o) { try { h.Intake[kv.Key] = Convert.ToDouble(kv.Value, CultureInfo.InvariantCulture); } catch { } }
                }
            }
            catch { }
            return h;
        }
        void Save()
        {
            if (File == null || HomeSettings.Frozen) return;
            foreach (var k in Intake.Keys.Where(k => string.CompareOrdinal(k, Day(DateTime.Now.AddDays(-60))) < 0).ToList()) Intake.Remove(k);
            try { Directory.CreateDirectory(Path.GetDirectoryName(File)); var tmp = File + ".tmp"; System.IO.File.WriteAllText(tmp, Family.Json(new Dictionary<string, object> { { "intake", Intake } })); if (System.IO.File.Exists(File)) System.IO.File.Replace(tmp, File, null); else System.IO.File.Move(tmp, File); } catch { }
        }
    }

    public class AlertEngine
    {
        public static readonly string[][] Rules = {
            new[] { "jam", "No meal dispensed when scheduled", "A meal time passed and the feeder didn't dispense (often a jam)." },
            new[] { "food-low", "Food low", "The feeder's food level is low." },
            new[] { "water-low", "Fountain water low", "The fountain's tank needs water." },
            new[] { "pump", "Pump stopped", "The fountain's pump has been stopped for a while." },
            new[] { "filter", "Filter or cleaning due", "The fountain's filter change or cleaning is due." },
            new[] { "intake-low", "Water intake unusually low", "Today's drinking is well under the 7-day average for this time of day (not medical advice)." },
            new[] { "camera-offline", "Camera offline", "A camera has been unavailable for a few minutes." },
            new[] { "motion-away", "Motion while away", "A camera sees motion while you're away (switch \"Motion alerts\" on)." } };
        public static string RuleTitle(string id) { var r = Rules.FirstOrDefault(x => x[0] == id); return r == null ? id : r[1]; }

        // meal times seen as "next feeding", waiting to be checked (time -> today's meal count when it was seen)
        readonly Dictionary<DateTime, int?> due = new Dictionary<DateTime, int?>();
        readonly Dictionary<string, DateTime> since = new Dictionary<string, DateTime>();

        public List<Alert> Evaluate(DateTime now, Mapping m, FeederView f, FountainView w, HomeSettings s, bool away)
        {
            var l = new List<Alert>();
            // ---- the feeder
            if (f.Present && s.RuleOn("jam"))
            {
                if (f.Next != null && !due.ContainsKey(f.Next.Value) && f.Next.Value > now.AddMinutes(-s.JamGraceMinutes)) due[f.Next.Value] = f.TodayMeals;
                foreach (var t in due.Keys.ToList())
                {
                    int? before = due[t];
                    bool fed = (f.Last != null && f.Last.Value >= t.AddMinutes(-5)) || (before != null && f.TodayMeals != null && f.TodayMeals > before);
                    if (fed || now > t.AddHours(12)) { due.Remove(t); continue; }
                    if (now >= t.AddMinutes(s.JamGraceMinutes))
                        l.Add(new Alert { Key = "jam:" + t.ToString("yyyyMMddHHmm"), Rule = "jam", Bad = true, Title = "No meal at " + t.ToString("HH:mm"),
                            Text = "The feeder should have dispensed at " + t.ToString("HH:mm") + " but no meal was recorded " + s.JamGraceMinutes + " minutes later. Check for a jam (kibble stuck in the outlet), that it has food and power. Your pet may be hungry - you can use Feed now once it's clear." });
                }
                if (f.Jam) l.Add(new Alert { Key = "jam:sensor", Rule = "jam", Bad = true, Title = "Feeder reports a dispenser problem", Text = "The feeder's own sensor says the dispenser is blocked or has a fault. Check the outlet for stuck kibble." });
            }
            if (f.Present && f.FoodLow && s.RuleOn("food-low"))
                l.Add(new Alert { Key = "food-low", Rule = "food-low", Title = "Feeder food low", Text = "The feeder's food is low" + (f.FoodText != null && f.FoodText != "Low" ? " (" + f.FoodText + ")" : "") + " - top it up so the next meals aren't missed." });
            // ---- the fountain
            if (w.Present && w.WaterLow && s.RuleOn("water-low"))
                l.Add(new Alert { Key = "water-low", Rule = "water-low", Bad = true, Title = "Fountain water low", Text = "The fountain's water is low" + (w.WaterText != null ? " (" + w.WaterText + ")" : "") + ". Top up the tank - the pump can run dry." });
            if (w.Present && w.PumpOn == false && w.PumpSince != null && now - w.PumpSince.Value >= TimeSpan.FromMinutes(s.PumpOffMinutes) && s.RuleOn("pump"))
                l.Add(new Alert { Key = "pump", Rule = "pump", Bad = true, Title = "Fountain pump stopped", Text = "The pump has been stopped for " + Fmt.Ago(now - w.PumpSince.Value) + ". Check the tank is on the dock and has water, and that the pump isn't clogged with hair." });
            if (w.Present && (w.FilterDue || w.CleaningDue) && s.RuleOn("filter"))
                l.Add(new Alert { Key = "filter", Rule = "filter", Title = w.FilterDue ? "Fountain filter due" : "Fountain cleaning due", Text = w.FilterDue ? "Time to change the fountain's filter (" + (w.FilterText ?? "now") + ")." : "Time to clean the fountain (" + (w.CleaningText ?? "now") + ")." });
            if (w.Present && s.RuleOn("intake-low"))
            {
                string why; if (IntakeLow(now, w.IntakeToday, w.IntakeAvg, s.IntakeLowPct, w.Unit, out why))
                    l.Add(new Alert { Key = "intake-low:" + History.Day(now), Rule = "intake-low", Title = "Your cat is drinking less than usual", Text = why + " Often nothing (wet food, cooler weather, another bowl), but keep an eye on them. This is not medical advice - if they also eat less, seem tired or you're worried, call your vet." });
            }
            // ---- cameras
            if (s.RuleOn("camera-offline"))
                foreach (var c in m.Cameras)
                    if (!c.Available && now - c.Changed >= TimeSpan.FromMinutes(s.CameraOfflineMinutes))
                        l.Add(new Alert { Key = "camera-offline:" + c.Id, Rule = "camera-offline", Title = c.Label + " is offline", Text = c.Label + " has been unavailable for " + Fmt.Ago(now - c.Changed) + ". Check its power and Wi-Fi, and that docker-wyze-bridge is running on the NAS." });
            if (away && s.MotionAlerts && s.RuleOn("motion-away"))
                foreach (var mo in m.Motion)
                    if (mo.Available && mo.On && now - mo.Changed < TimeSpan.FromMinutes(10))
                        l.Add(new Alert { Key = "motion:" + mo.Id + ":" + mo.Changed.ToString("yyyyMMddHHmm"), Rule = "motion-away", Bad = true, Title = "Motion while you're away", Text = mo.Label + " saw motion at " + mo.Changed.ToString("HH:mm") + "." });
            foreach (var a in l) { DateTime t; if (!since.TryGetValue(a.Key, out t)) since[a.Key] = t = now; a.Since = t; }
            foreach (var k in since.Keys.ToList()) if (!l.Any(a => a.Key == k)) since.Remove(k);
            return l;
        }

        // low intake: after 15:00, with an average of >= 3 days, under pct % of what the average says by this time of day
        public static bool IntakeLow(DateTime now, double? today, double? avg, int pct, string unit, out string why)
        {
            why = null;
            if (today == null || avg == null || avg.Value <= 0 || now.Hour < 15) return false;
            var expected = avg.Value * Math.Min(1.0, now.TimeOfDay.TotalMinutes / (24 * 60.0) * 1.15);   // cats drink a little more in the day
            if (today.Value >= expected * pct / 100.0) return false;
            why = "By " + now.ToString("HH:mm") + " your cat has drunk " + Read.Num(Math.Round(today.Value, 2)) + " " + (unit ?? "") + ", about " + Math.Round(100 * today.Value / expected) + "% of what's usual by this time (7-day average " + Read.Num(Math.Round(avg.Value, 2)) + " " + (unit ?? "") + " a day).";
            return true;
        }
    }

    // which alerts go out now, to which channels - and remembering what was already sent (de-duplication)
    public class AlertDesk
    {
        public readonly Dictionary<string, DateTime> Sent = new Dictionary<string, DateTime>();       // key -> when it was sent
        readonly Dictionary<string, DateTime> cleared = new Dictionary<string, DateTime>();            // key -> when it stopped being true
        public readonly List<string> Log = new List<string>();
        public static readonly TimeSpan Refire = TimeSpan.FromMinutes(10);
        public class Delivery { public Alert Alert; public bool Pc, Phone; }
        // what was already sent survives a restart (alerts.json: keys and times only), so starting with Windows doesn't repeat them
        public string File;
        public static AlertDesk Load(string file)
        {
            var d = new AlertDesk { File = file };
            try
            {
                if (file != null && System.IO.File.Exists(file))
                {
                    var o = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(System.IO.File.ReadAllText(file)); object s;
                    if (o != null && o.TryGetValue("sent", out s) && s is Dictionary<string, object>)
                        foreach (var kv in (Dictionary<string, object>)s) { DateTime t; if (kv.Value is string && DateTime.TryParse((string)kv.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out t) && DateTime.Now - t < TimeSpan.FromDays(7)) d.Sent[kv.Key] = t; }
                }
            }
            catch { }
            return d;
        }
        void Save()
        {
            if (File == null || HomeSettings.Frozen) return;
            try { var tmp = File + ".tmp"; System.IO.File.WriteAllText(tmp, Family.Json(new Dictionary<string, object> { { "sent", Sent.ToDictionary(kv => kv.Key, kv => (object)kv.Value.ToString("o")) } })); if (System.IO.File.Exists(File)) System.IO.File.Replace(tmp, File, null); else System.IO.File.Move(tmp, File); } catch { }
        }

        // active = what's true right now. Returns what to send now (and remembers it as sent).
        public List<Delivery> Process(List<Alert> active, DateTime now, HomeSettings s, bool game, DateTime? snoozeUntil)
        {
            var r = new List<Delivery>(); int before = Sent.Count; var removed = false;
            foreach (var k in Sent.Keys.ToList())
                if (!active.Any(a => a.Key == k)) { DateTime c; if (!cleared.TryGetValue(k, out c)) cleared[k] = now; else if (now - c >= Refire) { Sent.Remove(k); cleared.Remove(k); removed = true; } }
            foreach (var a in active) cleared.Remove(a.Key);
            bool quiet = s.QuietOn && Quiet.IsQuiet(now, s.QuietStart, s.QuietEnd);
            bool snoozed = snoozeUntil != null && now < snoozeUntil.Value;
            foreach (var a in active)
            {
                if (Sent.ContainsKey(a.Key)) continue;                       // already told - not again while it's still true
                if (quiet || snoozed) continue;                               // held: sent when quiet hours end, if still true
                if (game && s.GameHoldAll) continue;                          // held until the game closes
                var d = new Delivery { Alert = a, Pc = !game, Phone = true };
                Sent[a.Key] = now; r.Add(d);
                Log.Add(now.ToString("HH:mm") + " " + a.Key + (game ? " (game: phone only)" : ""));
                if (Log.Count > 200) Log.RemoveAt(0);
            }
            if (removed || Sent.Count != before || r.Count > 0) Save();
            return r;
        }
        public bool Held(List<Alert> active) { return active.Any(a => !Sent.ContainsKey(a.Key)); }
    }

    public static class Fmt
    {
        public static string Ago(TimeSpan t)
        {
            if (t.TotalMinutes < 1) return "less than a minute";
            if (t.TotalMinutes < 90) return (int)t.TotalMinutes + " minutes";
            if (t.TotalHours < 48) return (int)t.TotalHours + " hours";
            return (int)t.TotalDays + " days";
        }
        public static string Clock(DateTime? t, DateTime now)
        {
            if (t == null) return "-";
            var v = t.Value; var s = v.ToString("HH:mm");
            if (v.Date == now.Date) return s; if (v.Date == now.Date.AddDays(1)) return "tomorrow " + s; if (v.Date == now.Date.AddDays(-1)) return "yesterday " + s;
            return v.ToString("d MMM HH:mm");
        }
        public static string Size(long b) { return b >= 1L << 30 ? (b / (double)(1L << 30)).ToString("0.0") + " GB" : b >= 1 << 20 ? (b / (double)(1 << 20)).ToString("0.0") + " MB" : Math.Max(1, b / 1024) + " KB"; }
    }
}
