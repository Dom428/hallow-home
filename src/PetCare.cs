// Hallow Home 1.2 - the feeder's own schedule and the fountain's reminders.
//   Schedule: the community Petlibro integration (HACS) puts the feeder's whole feeding plan on its "Feeding Schedule" binary
//   sensor, attribute "schedule": a list of plans { id, label, time "HH:MM", amount_g / amount_raw (portions), enabled,
//   repeat_days [1 = Monday .. 7 = Sunday], today, state }. Recent versions also register services to change it:
//   petlibro.edit_feeding_plan / add_feeding_plan / toggle_feeding_plan / delete_feeding_plan, each with the feeder's HA device_id
//   (asked from Home Assistant's template API: {{ device_id('<entity>') }}). Hallow Home reads GET /api/services to see whether
//   they exist; without them the schedule is shown read-only, and it says so. Every change needs a confirm first.
//   Reminders: the fountain's filter and cleaning, from the integration's "remaining ... days" entities, or your own interval
//   ("change the filter every 30 days", counted from the day you tell Hallow Home you changed it).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace HallowHome
{
    public class FeedPlan
    {
        public long Id; public string Label, Time; public int Portions; public double? Grams; public bool Enabled, Today, Sound; public int[] Days = new int[0]; public string State;
        public static readonly string[] DayNames = { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };
        public string DaysText
        {
            get
            {
                if (Days == null || Days.Length == 0) return "once";
                if (Days.Length == 7) return "every day";
                var d = Days.OrderBy(x => x).ToArray();
                if (d.SequenceEqual(new[] { 1, 2, 3, 4, 5 })) return "weekdays";
                if (d.SequenceEqual(new[] { 6, 7 })) return "weekends";
                return string.Join(" ", d.Where(x => x >= 1 && x <= 7).Select(x => DayNames[x - 1]));
            }
        }
        public double GramsOr(double perPortion) { return Grams ?? Portions * perPortion; }
    }

    public class ScheduleView
    {
        public Entity Entity; public List<FeedPlan> Plans = new List<FeedPlan>(); public bool On = true;
        public bool Present { get { return Entity != null; } }
        // the feeder's plans for today (enabled), and how much of it is planned / already dispensed
        public IEnumerable<FeedPlan> TodayPlans { get { return Plans.Where(p => p.Enabled && p.Today).OrderBy(p => p.Time); } }

        public static ScheduleView From(Entity e)
        {
            var v = new ScheduleView { Entity = e };
            if (e == null) return v;
            v.On = e.State != "off";
            object o; if (!e.Attr.TryGetValue("schedule", out o) || !(o is System.Collections.IEnumerable) || o is string) return v;
            foreach (var x in (System.Collections.IEnumerable)o)
            {
                var d = x as Dictionary<string, object>; if (d == null) continue;
                var p = new FeedPlan();
                long id; if (!long.TryParse(S(d, "id"), NumberStyles.Integer, CultureInfo.InvariantCulture, out id)) continue; p.Id = id;
                p.Label = S(d, "label") ?? ""; p.Time = NormTime(S(d, "time")); if (p.Time == null) continue;
                double n; if (double.TryParse(S(d, "amount_raw"), NumberStyles.Float, CultureInfo.InvariantCulture, out n)) p.Portions = (int)Math.Round(n);
                if (double.TryParse(S(d, "amount_g"), NumberStyles.Float, CultureInfo.InvariantCulture, out n)) p.Grams = n;
                p.Enabled = B(d, "enabled", true); p.Today = B(d, "today", false); p.Sound = B(d, "sound", false); p.State = S(d, "state");
                object rd; var days = new List<int>();
                if (d.TryGetValue("repeat_days", out rd) && rd is System.Collections.IEnumerable && !(rd is string))
                    foreach (var y in (System.Collections.IEnumerable)rd) { int k; if (y != null && int.TryParse(Convert.ToString(y, CultureInfo.InvariantCulture), out k) && k >= 1 && k <= 7) days.Add(k); }
                p.Days = days.Distinct().OrderBy(k => k).ToArray();
                v.Plans.Add(p);
            }
            v.Plans = v.Plans.OrderBy(p => p.Time).ThenBy(p => p.Id).ToList();
            return v;
        }
        static string S(Dictionary<string, object> d, string k) { object v; return d.TryGetValue(k, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : null; }
        static bool B(Dictionary<string, object> d, string k, bool def) { object v; if (!d.TryGetValue(k, out v) || v == null) return def; if (v is bool) return (bool)v; var s = Convert.ToString(v, CultureInfo.InvariantCulture).ToLowerInvariant(); return s == "true" || s == "1" || s == "on"; }
        // "7:00", "07:00:00" -> "07:00"; anything else -> null
        public static string NormTime(string s)
        {
            if (s == null) return null; var m = Regex.Match(s.Trim(), "^(\\d{1,2}):(\\d{2})(:\\d{2})?$"); if (!m.Success) return null;
            int h = int.Parse(m.Groups[1].Value), mi = int.Parse(m.Groups[2].Value); if (h > 23 || mi > 59) return null;
            return h.ToString("00") + ":" + mi.ToString("00");
        }
    }

    // one change to the feeder's schedule, as the person picked it (and as the confirm dialog describes it)
    public class PlanEdit
    {
        public string Kind;          // edit / add / toggle / delete
        public long PlanId; public string Time; public int Portions; public int[] Days; public bool Enable; public string Label;
    }

    public static class Schedule
    {
        public static readonly string[] Services = { "edit_feeding_plan", "add_feeding_plan", "toggle_feeding_plan", "delete_feeding_plan" };
        // GET /api/services: does the Petlibro integration offer the schedule services? (null = couldn't tell)
        public static bool? Editable(string servicesJson)
        {
            try
            {
                var arr = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 }.DeserializeObject(servicesJson) as object[];
                if (arr == null) return null;
                foreach (var o in arr)
                {
                    var d = o as Dictionary<string, object>; object dom, sv; if (d == null || !d.TryGetValue("domain", out dom) || (dom as string) != "petlibro") continue;
                    var s = d.TryGetValue("services", out sv) ? sv as Dictionary<string, object> : null;
                    return s != null && s.ContainsKey("edit_feeding_plan") && s.ContainsKey("add_feeding_plan");
                }
                return false;
            }
            catch { return null; }
        }
        // what's wrong with a change, before anything is sent (null = fine)
        public static string Check(PlanEdit e, ScheduleView v, int maxPortions)
        {
            if (e == null) return "Nothing to change.";
            if (e.Kind != "edit" && e.Kind != "add" && e.Kind != "toggle" && e.Kind != "delete") return "Unknown change.";
            if (e.Kind != "add" && (v == null || !v.Plans.Any(p => p.Id == e.PlanId))) return "That meal isn't in the feeder's schedule any more.";
            if (e.Kind == "edit" || e.Kind == "add")
            {
                if (ScheduleView.NormTime(e.Time) == null) return "Use a 24-hour time like 07:30.";
                if (e.Portions < 1 || e.Portions > Math.Min(48, Math.Max(1, maxPortions))) return "Pick 1 to " + Math.Min(48, Math.Max(1, maxPortions)) + " portions.";
                if (e.Days != null && e.Days.Any(d => d < 1 || d > 7)) return "Days are Monday to Sunday.";
                if (e.Label != null && Regex.IsMatch(e.Label, "\\s")) return "The meal's name can't have spaces (the integration's rule) - e.g. Breakfast.";
            }
            return null;
        }
        // the service call for a change: petlibro.<service> with the feeder's device_id
        public static Dictionary<string, object> Data(PlanEdit e, string deviceId, out string service)
        {
            var d = new Dictionary<string, object> { { "device_id", deviceId } };
            switch (e.Kind)
            {
                case "edit": service = "edit_feeding_plan"; d["plan_id"] = e.PlanId; d["time"] = ScheduleView.NormTime(e.Time); d["portions"] = e.Portions; if (e.Days != null && e.Days.Length > 0) d["days"] = e.Days.Select(x => x.ToString(CultureInfo.InvariantCulture)).ToArray(); break;
                case "add": service = "add_feeding_plan"; d["time"] = ScheduleView.NormTime(e.Time); d["portions"] = e.Portions; if (e.Days != null && e.Days.Length > 0) d["days"] = e.Days.Select(x => x.ToString(CultureInfo.InvariantCulture)).ToArray(); if (!string.IsNullOrEmpty(e.Label)) d["label"] = e.Label; break;
                case "toggle": service = "toggle_feeding_plan"; d["plan_id"] = e.PlanId; d["enable"] = e.Enable; break;
                default: service = "delete_feeding_plan"; d["plan_id"] = e.PlanId; break;
            }
            return d;
        }
        static string DaysWords(int[] days) { return new FeedPlan { Days = days ?? new int[0] }.DaysText; }
        // the confirm dialog's words: exactly what changes
        public static string Describe(PlanEdit e, ScheduleView v, double gramsPerPortion, out string title)
        {
            var p = v == null ? null : v.Plans.FirstOrDefault(x => x.Id == e.PlanId);
            var was = p == null ? "" : p.Time + (p.Label.Length > 0 ? " (" + p.Label + ")" : "");
            Func<int, string> amount = n => n + " portion" + (n == 1 ? "" : "s") + " (about " + Read.Num(n * gramsPerPortion) + " g)";
            switch (e.Kind)
            {
                case "edit":
                    title = "Change the " + was + " meal?";
                    return "New: " + ScheduleView.NormTime(e.Time) + ", " + amount(e.Portions) + ", " + DaysWords(e.Days != null && e.Days.Length > 0 ? e.Days : p != null ? p.Days : null) + ". Was: " + p.Time + ", " + amount(p.Portions) + ", " + p.DaysText + ". Home Assistant sends this to the feeder through the Petlibro integration; the feeder then follows the new time.";
                case "add":
                    title = "Add a meal at " + ScheduleView.NormTime(e.Time) + "?";
                    return amount(e.Portions) + ", " + DaysWords(e.Days) + ". Home Assistant adds it to the feeder's own schedule through the Petlibro integration.";
                case "toggle":
                    title = (e.Enable ? "Turn on" : "Turn off") + " the " + was + " meal?";
                    return e.Enable ? "The feeder dispenses it again at " + p.Time + " (" + p.DaysText + ")." : "The feeder skips it until you turn it on again. The other meals stay as they are.";
                default:
                    title = "Delete the " + was + " meal?";
                    return "It's removed from the feeder's schedule for good (the integration can't undo it - you'd add it again). The other meals stay as they are.";
            }
        }
    }

    // a filter / cleaning reminder: from the integration's entity, or your own interval
    public class Reminder
    {
        public string Kind, Text, Source; public bool Due, Own, Started; public double? DaysLeft;
        // own interval: every `every` days counted from `since` (yyyy-MM-dd); not started = never told
        public static Reminder Interval(string kind, int every, string since, DateTime now, int dueDays)
        {
            var r = new Reminder { Kind = kind, Own = true, Source = "every " + every + " days (your interval)" };
            DateTime d;
            if (since == null || !DateTime.TryParseExact(since, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) { r.Text = "Not started - tell Hallow Home when you last did it"; return r; }
            r.Started = true; r.DaysLeft = every - (now.Date - d.Date).TotalDays;
            r.Due = r.DaysLeft <= dueDays;
            r.Text = r.DaysLeft <= 0 ? (r.DaysLeft < 0 ? "Due now (" + Read.Num(-r.DaysLeft.Value) + (r.DaysLeft == -1 ? " day late)" : " days late)") : "Due now") : Read.Num(r.DaysLeft.Value) + (r.DaysLeft == 1 ? " day left" : " days left");
            return r;
        }
    }
}
