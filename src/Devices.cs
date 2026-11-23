// Hallow Home - the mapping layer: which Home Assistant entity is "the feeder's food level", "the fountain's pump" and so on.
// The community Petlibro (HACS, unofficial) and Wyze integrations name their entities differently from version to version and
// people rename them, so nothing here relies on one exact name. Each role is recognised by domain, device class and name patterns
// (on the entity id and the friendly name), within the device's own entities (feeder / granary, fountain / dockstream). A manual
// override on the Devices page ("this entity is the feeder's food level", or "none") always wins.
// Then the pet views: the feeder and the fountain in plain words, from whatever the mapped entities say.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace HallowHome
{
    public class RoleDef
    {
        public string Id, Device, Label; public string[] Domains; public Regex Pattern; public string DeviceClass;
        public bool Matches(Entity e)
        {
            if (!Domains.Contains(e.Domain)) return false;
            if (DeviceClass != null && e.DeviceClass == DeviceClass) return true;
            return Pattern == null || Pattern.IsMatch(e.Words);
        }
    }

    public static class Roles
    {
        public static readonly Regex FeederWords = new Regex("feeder|granary", RegexOptions.Compiled);
        public static readonly Regex FountainWords = new Regex("fountain|dockstream|dock stream", RegexOptions.Compiled);
        static RoleDef D(string id, string label, string domains, string pattern, string dc = null)
        {
            return new RoleDef { Id = id, Device = id.Substring(0, id.IndexOf('.')), Label = label, Domains = domains.Split(','), Pattern = pattern == null ? null : new Regex(pattern, RegexOptions.Compiled), DeviceClass = dc };
        }
        // the order matters: the more specific roles pick first, and an entity is used by one role only
        public static readonly RoleDef[] All = {
            D("feeder.feed", "Feed now (button)", "button", "manual feed|feed now"),
            D("feeder.portion", "Portions for Feed now", "number,select", "(manual feed|feed now|feeding) (quantity|portion|portions|amount)|portions?\\b"),
            D("feeder.schedule", "Feeding schedule", "binary_sensor,sensor", "(?<!today\\W?s )(?<!today )feeding (schedule|plan)\\b"),
            D("feeder.next", "Next feeding", "sensor", "next (feed|feeding|meal)|upcoming (feed|meal)"),
            D("feeder.last", "Last feeding", "sensor", "last (feed|feeding|meal|dispens)"),
            D("feeder.grams", "Food dispensed today", "sensor", "today.*(quantity|amount|weight|grams|dispensed)|(feed|feeding) (quantity|amount) today|dispensed today"),
            D("feeder.meals", "Meals today", "sensor", "today.*(times|count|meals)|(feed|feeding) (times|count)"),
            D("feeder.jam", "Dispenser problem", "binary_sensor,sensor", "(dispens\\w*|outlet|motor|food|grain) (problem|error|blocked|block|jam|jammed|fault|stuck)|\\bjam"),
            D("feeder.food", "Food level", "sensor,binary_sensor", "food (level|status|state|remaining|low|shortage|insufficient)|grain (level|status)|remaining food|food low"),
            D("feeder.desiccant", "Desiccant", "sensor", "desiccant"),
            D("feeder.battery", "Battery", "sensor", "battery", "battery"),
            D("feeder.wifi", "Wi-Fi signal", "sensor", "wifi|wi fi|rssi|signal"),
            D("feeder.sound", "Sound", "switch", "sound|voice|mute|audio"),
            D("feeder.camera", "Feeder camera", "camera", null),
            D("fountain.intake", "Water intake today", "sensor", "intake|drink|drank|(today|daily).*water|water.*(today|daily)"),
            D("fountain.irregular", "Irregularities", "sensor", "irregular|abnormal|anomal"),
            D("fountain.water", "Water level", "sensor,binary_sensor", "water (level|state|shortage|low|status|remaining)|low water|lack of water"),
            D("fountain.filter", "Filter", "sensor,binary_sensor", "filter"),
            D("fountain.cleaning", "Cleaning", "sensor,binary_sensor", "clean"),
            D("fountain.pump", "Pump", "sensor,binary_sensor,switch", "pump|water flow|flowing|running|dispensing status"),
            D("lock.bolt", "Door lock", "lock", null),
        };
        public static RoleDef Find(string id) { return All.FirstOrDefault(r => r.Id == id); }
        static bool OnDevice(RoleDef r, Entity e)
        {
            if (r.Device == "feeder") return FeederWords.IsMatch(e.Words);
            if (r.Device == "fountain") return FountainWords.IsMatch(e.Words);
            return true;
        }
        // the entities that could be this role (for the override picker): its domains, on its device first
        public static List<Entity> Candidates(RoleDef r, IEnumerable<Entity> all)
        {
            return all.Where(e => r.Domains.Contains(e.Domain)).OrderBy(e => OnDevice(r, e) && r.Matches(e) ? 0 : OnDevice(r, e) ? 1 : 2).ThenBy(e => e.Id).ToList();
        }

        public static Mapping Build(IEnumerable<Entity> entities, HomeSettings s)
        {
            var all = entities.ToList(); var m = new Mapping();
            var byId = all.ToDictionary(e => e.Id);
            var used = new HashSet<string>();
            foreach (var r in All)
            {
                var ov = s == null ? null : s.Override(r.Id);
                if (ov != null)
                {
                    m.Manual.Add(r.Id);
                    Entity e; if (ov.Length > 0 && byId.TryGetValue(ov, out e)) { m.ByRole[r.Id] = e; used.Add(e.Id); }
                    else if (ov.Length > 0) m.Missing.Add(r.Id);
                }
            }
            foreach (var r in All)
            {
                if (m.Manual.Contains(r.Id)) continue;
                var e = all.Where(x => !used.Contains(x.Id) && OnDevice(r, x) && r.Matches(x)).OrderBy(x => Array.IndexOf(r.Domains, x.Domain)).ThenBy(x => x.Id.Length).ThenBy(x => x.Id).FirstOrDefault();
                if (e != null) { m.ByRole[r.Id] = e; used.Add(e.Id); }
            }
            var feederCam = m.Get("feeder.camera");
            m.Cameras = all.Where(e => e.Domain == "camera").OrderBy(e => feederCam != null && e.Id == feederCam.Id ? 1 : 0).ThenBy(e => e.Label).ToList();
            m.Motion = all.Where(e => e.Domain == "binary_sensor" && (e.DeviceClass == "motion" || e.DeviceClass == "occupancy" || (e.DeviceClass == null && e.Words.Contains("motion")))).OrderBy(e => e.Id).ToList();
            m.People = all.Where(e => e.Domain == "person").ToList();
            m.Count = all.Count;
            return m;
        }
    }

    public class Mapping
    {
        public readonly Dictionary<string, Entity> ByRole = new Dictionary<string, Entity>();
        public readonly HashSet<string> Manual = new HashSet<string>(), Missing = new HashSet<string>();
        public List<Entity> Cameras = new List<Entity>(), Motion = new List<Entity>(), People = new List<Entity>();
        public int Count;
        public Entity Get(string role) { Entity e; return ByRole.TryGetValue(role, out e) ? e : null; }
        public bool HasFeeder { get { return ByRole.Keys.Any(k => k.StartsWith("feeder.") && k != "feeder.camera") || Get("feeder.camera") != null; } }
        public bool HasFountain { get { return ByRole.Keys.Any(k => k.StartsWith("fountain.")); } }
        // everyone away: HA's person.* entities all not "home" (none = unknown)
        public bool? EveryoneAway { get { if (People.Count == 0) return null; return People.All(p => p.Available && p.State != "home"); } }
    }

    // ---------------- reading values the way people mean them
    public static class Read
    {
        static readonly Regex LowWords = new Regex("empty|low|insufficient|lack|shortage|not enough|out of|need|add", RegexOptions.Compiled);
        static readonly Regex DueWords = new Regex("replace|due|expired|change|overdue|needed|need", RegexOptions.Compiled);
        public static string Nice(string s) { if (string.IsNullOrEmpty(s)) return s; s = s.Replace('_', ' '); return char.ToUpperInvariant(s[0]) + s.Substring(1); }
        public static bool IsPercent(Entity e) { return e.Unit == "%" || (e.Unit == null && e.Num != null && e.Num >= 0 && e.Num <= 100 && e.Words.Contains("level")); }
        public static bool IsDays(Entity e) { var u = (e.Unit ?? "").ToLowerInvariant(); return u == "d" || u == "day" || u == "days" || (u.Length == 0 && e.Words.Contains("days")); }
        // a level (food / water): percent if it has one; low = under the threshold, or words like "empty" / "low", or a binary sensor on
        public static void Level(Entity e, int lowPct, out double? pct, out bool low, out string text)
        {
            pct = null; low = false; text = null;
            if (e == null || !e.Available) return;
            if (e.Domain == "binary_sensor") { low = e.On; text = e.On ? "Low" : "OK"; return; }
            var n = e.Num;
            if (n != null && IsPercent(e)) { pct = Math.Max(0, Math.Min(100, n.Value)); low = pct < lowPct; text = Math.Round(pct.Value) + "%"; return; }
            var s = e.State.ToLowerInvariant();
            if (n != null) { text = Num(n.Value) + (e.Unit != null ? " " + e.Unit : ""); return; }
            low = LowWords.IsMatch(s) && !s.Contains("not low"); text = Nice(e.State);
        }
        // filter / cleaning: due when the percent is nearly gone, the days left are few, a binary sensor is on, or the words say so
        public static void Due(Entity e, int days, out bool due, out string text)
        {
            due = false; text = null;
            if (e == null || !e.Available) return;
            if (e.Domain == "binary_sensor") { due = e.On; text = e.On ? "Due now" : "OK"; return; }
            var n = e.Num;
            if (n != null && IsDays(e)) { due = n.Value <= days; text = n.Value <= 0 ? "Due now" : Num(n.Value) + (n.Value == 1 ? " day left" : " days left"); return; }
            if (n != null && e.Unit == "%") { due = n.Value <= 5; text = Math.Round(n.Value) + "% left"; return; }
            if (n != null) { text = Num(n.Value) + (e.Unit != null ? " " + e.Unit : ""); return; }
            due = DueWords.IsMatch(e.State.ToLowerInvariant()); text = Nice(e.State);
        }
        // the pump: running / stopped / null (unknown, or an intermittent mode that pauses on purpose)
        public static bool? Running(Entity e)
        {
            if (e == null || !e.Available) return null;
            if (e.Domain == "switch" || e.Domain == "binary_sensor") return e.On;
            var s = e.State.ToLowerInvariant();
            if (Regex.IsMatch(s, "not running|stopped|stop|^off$|idle|abnormal|error|dry|no water")) return false;
            if (Regex.IsMatch(s, "running|^on$|working|normal|flowing|continuous|intermittent|smart|sleep")) return true;
            return null;
        }
        public static string Num(double v) { return Math.Abs(v - Math.Round(v)) < 0.005 ? Math.Round(v).ToString(CultureInfo.InvariantCulture) : v.ToString("0.##", CultureInfo.InvariantCulture); }
        // a time from a sensor: an ISO timestamp, or "07:00" / "7:00 AM" (today, or tomorrow if it has passed)
        public static DateTime? When(Entity e, DateTime now, bool future = false)
        {
            if (e == null || !e.Available) return null;
            var t = Entity.Time(e.State); if (t != null) return t;
            DateTime d;
            if (DateTime.TryParseExact(e.State.Trim(), new[] { "HH:mm", "H:mm", "h:mm tt", "hh:mm tt", "HH:mm:ss" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out d))
            { var r = now.Date + d.TimeOfDay; if (future && r < now.AddMinutes(-1)) r = r.AddDays(1); return r; }
            return null;
        }
    }

    public class FeederView
    {
        public bool Present, Online;
        public DateTime? Next, Last; public double? NextGrams; public double? TodayGrams; public int? TodayMeals;
        public double? FoodPct; public bool FoodLow; public string FoodText; public bool Jam;
        public string Desiccant, Battery, Wifi; public bool? Muted; public Entity Camera; public bool CanFeed; public bool PortionEntity;
        // 1.2: the feeder's own schedule, and "fed today" against what's planned for today
        public ScheduleView Schedule = new ScheduleView(); public double? PlannedGrams; public int PlannedMeals;
        public string FedTodayText(double perPortion)
        {
            var s = (TodayMeals != null ? TodayMeals + " meal" + (TodayMeals == 1 ? "" : "s") : "-") + (TodayGrams != null ? " · " + Read.Num(TodayGrams.Value) + " g" : "");
            if (PlannedGrams != null) s += " of " + Read.Num(PlannedGrams.Value) + " g planned";
            return s;
        }
        public static FeederView From(Mapping m, HomeSettings s, DateTime now)
        {
            var f = new FeederView { Present = m.HasFeeder };
            var feederEntities = m.ByRole.Where(kv => kv.Key.StartsWith("feeder.") && kv.Key != "feeder.camera").Select(kv => kv.Value).ToList();
            f.Online = feederEntities.Any(e => e.Available);
            var next = m.Get("feeder.next"); f.Next = Read.When(next, now, true);
            if (next != null) foreach (var k in next.Attr.Keys)
                {
                    var kl = k.ToLowerInvariant(); double v; var sv = next.AttrS(k);
                    if (!double.TryParse(sv, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) continue;
                    if (kl.Contains("gram") || kl == "amount" || kl.Contains("weight")) f.NextGrams = v;
                    else if (kl.Contains("portion") || kl.Contains("quantity")) f.NextGrams = v * s.GramsPerPortion;
                }
            f.Last = Read.When(m.Get("feeder.last"), now);
            var g = m.Get("feeder.grams");
            if (g != null && g.Num != null) { var u = (g.Unit ?? "").ToLowerInvariant(); f.TodayGrams = u == "g" || u == "grams" ? g.Num : u.Contains("portion") || u.Length == 0 ? g.Num * s.GramsPerPortion : g.Num; }
            var mc = m.Get("feeder.meals"); if (mc != null && mc.Num != null) f.TodayMeals = (int)Math.Round(mc.Num.Value);
            double? pct; bool low; string text; Read.Level(m.Get("feeder.food"), s.FoodLowPct, out pct, out low, out text); f.FoodPct = pct; f.FoodLow = low; f.FoodText = text;
            var jam = m.Get("feeder.jam"); f.Jam = jam != null && jam.Available && (jam.Domain == "binary_sensor" ? jam.On : Regex.IsMatch(jam.State.ToLowerInvariant(), "jam|block|stuck|error|problem|fault") && !Regex.IsMatch(jam.State.ToLowerInvariant(), "^(no|none|ok|normal)"));
            var de = m.Get("feeder.desiccant"); if (de != null && de.Available) { bool due; string dt; Read.Due(de, 3, out due, out dt); f.Desiccant = dt; }
            var b = m.Get("feeder.battery"); if (b != null && b.Available) f.Battery = b.Num != null ? Math.Round(b.Num.Value) + "%" : Read.Nice(b.State);
            var w = m.Get("feeder.wifi"); if (w != null && w.Available) f.Wifi = w.Num != null ? Read.Num(w.Num.Value) + (w.Unit != null ? " " + w.Unit : "") : Read.Nice(w.State);
            var snd = m.Get("feeder.sound"); if (snd != null && snd.Available) f.Muted = snd.Words.Contains("mute") ? snd.On : !snd.On;
            f.Camera = m.Get("feeder.camera");
            var btn = m.Get("feeder.feed"); f.CanFeed = btn != null && btn.Available; f.PortionEntity = m.Get("feeder.portion") != null;
            f.Schedule = ScheduleView.From(m.Get("feeder.schedule"));
            var today = f.Schedule.TodayPlans.ToList();
            if (f.Schedule.Present && f.Schedule.On && today.Count > 0) { f.PlannedMeals = today.Count; f.PlannedGrams = today.Sum(p => p.GramsOr(s.GramsPerPortion)); }
            return f;
        }
    }

    public class FountainView
    {
        public bool Present, Online;
        public double? WaterPct; public bool WaterLow; public string WaterText;
        public bool FilterDue; public string FilterText; public bool CleaningDue; public string CleaningText;
        public bool? PumpOn; public string PumpText; public DateTime? PumpSince;
        public double? IntakeToday, IntakeAvg; public string Unit; public int? IrregularCount;
        public Reminder Filter = new Reminder(), Cleaning = new Reminder();
        public static FountainView From(Mapping m, HomeSettings s, History h, DateTime now)
        {
            var f = new FountainView { Present = m.HasFountain };
            f.Online = m.ByRole.Where(kv => kv.Key.StartsWith("fountain.")).Any(kv => kv.Value.Available);
            double? pct; bool low; string text; Read.Level(m.Get("fountain.water"), s.WaterLowPct, out pct, out low, out text); f.WaterPct = pct; f.WaterLow = low; f.WaterText = text;
            bool due; string dt;
            Read.Due(m.Get("fountain.filter"), s.FilterDueDays, out due, out dt); f.FilterDue = due; f.FilterText = dt;
            Read.Due(m.Get("fountain.cleaning"), s.FilterDueDays, out due, out dt); f.CleaningDue = due; f.CleaningText = dt;
            // 1.2: your own interval when the integration has no filter / cleaning entity (or you chose your own)
            var fe = m.Get("fountain.filter"); var ce = m.Get("fountain.cleaning");
            if (f.Present && (s.OwnReminders || fe == null || !fe.Available))
            { f.Filter = Reminder.Interval("filter", s.FilterEveryDays, s.FilterChangedOn, now, s.FilterDueDays); f.FilterDue = f.Filter.Due; f.FilterText = f.Filter.Text; }
            else f.Filter = new Reminder { Kind = "filter", Due = f.FilterDue, Text = f.FilterText, Source = "the fountain (Petlibro integration)", Started = true };
            if (f.Present && (s.OwnReminders || ce == null || !ce.Available))
            { f.Cleaning = Reminder.Interval("cleaning", s.CleanEveryDays, s.CleanedOn, now, s.FilterDueDays); f.CleaningDue = f.Cleaning.Due; f.CleaningText = f.Cleaning.Text; }
            else f.Cleaning = new Reminder { Kind = "cleaning", Due = f.CleaningDue, Text = f.CleaningText, Source = "the fountain (Petlibro integration)", Started = true };
            var p = m.Get("fountain.pump"); f.PumpOn = Read.Running(p); if (p != null && p.Available) { f.PumpText = f.PumpOn == true ? "Running" : f.PumpOn == false ? "Stopped" : Read.Nice(p.State); f.PumpSince = p.Changed; }
            var i = m.Get("fountain.intake"); if (i != null && i.Num != null) { f.IntakeToday = i.Num; f.Unit = i.Unit ?? "ml"; }
            if (h != null) f.IntakeAvg = h.Avg7(now.Date);
            var ir = m.Get("fountain.irregular"); if (ir != null && ir.Num != null) f.IrregularCount = (int)Math.Round(ir.Num.Value);
            return f;
        }
        // what's not right with the fountain, in plain words (shown under the fountain card)
        public List<string> Explain(bool intakeLow)
        {
            var l = new List<string>();
            if (WaterLow) l.Add("The water is low" + (WaterPct != null ? " (" + Math.Round(WaterPct.Value) + "%)" : "") + " - top up the tank so the pump doesn't run dry.");
            if (PumpOn == false) l.Add("The pump isn't running - check the tank sits properly on the dock and has water; a dry or blocked pump stops on its own.");
            if (FilterDue) l.Add("The filter is due for a change (" + (FilterText ?? "now") + ") - an old filter lets hair and scale through and cats may drink less.");
            if (CleaningDue) l.Add("It's time to clean the fountain (" + (CleaningText ?? "now") + ") - rinse the tank and the pump; slime builds up in a week or two.");
            if (intakeLow) l.Add("Your cat has drunk less than usual today compared with the last 7 days. Often nothing (wet food, cooler weather, another bowl), but keep an eye on them. This is not medical advice - call your vet if you're worried.");
            if (IrregularCount != null && IrregularCount > 0) l.Add("Petlibro's own app counts " + IrregularCount + " irregularit" + (IrregularCount == 1 ? "y" : "ies") + " - its measure for unusual drinking visits or pump hiccups. The lines above are Hallow Home's own checks.");
            return l;
        }
    }
}
