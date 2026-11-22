// Hallow Home tests. No real Home Assistant, devices or Wyze / Petlibro accounts: a fake Home Assistant (FakeHa.cs) runs on
// localhost with made-up entities modelled on the community integrations. Discord / ntfy are stand-ins on localhost too. Nothing
// leaves this PC. A test copy of the app runs headless with its own data folder and pipe (it never reaches the real Hallow apps).
// The WebView2 live-view check opens one window far off-screen (never on the visible desktop) and skips if WebView2 is missing.
//   tests\HomeTests.exe
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace HallowHome.Tests
{
    public static class Run
    {
        static int pass, fail;
        static void Ok(bool c, string name) { if (c) { pass++; Console.WriteLine("ok   " + name); } else { fail++; Console.WriteLine("FAIL " + name); } }
        static string Dir;
        static readonly DateTime Day = new DateTime(2026, 11, 20);

        [STAThread]
        public static int Main(string[] args)
        {
            Dir = Path.Combine(Path.GetTempPath(), "HallowHomeTest" + Process.GetCurrentProcess().Id);
            Environment.SetEnvironmentVariable("HALLOW_HOME_TESTDIR", Dir);
            Environment.SetEnvironmentVariable("HALLOW_HOME_HEADLESS", "1");   // the in-process app (camera grid test): no tray icon, no windows shown
            Directory.CreateDirectory(Dir);
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            try
            {
                Parsing(); MappingTests(); Overrides(); Readers(); JamTests(); RuleTests(); IntakeTests(); DeskTests(); AutomationTests(); SecretTests(); LockTests(); MjpegTests(); ModeTests(); Misc();
                using (var ha = new FakeHa()) { ClientTests(ha); SenderTests(); GridTest(app, ha); LiveViewTest(app, ha); PipeAndApp(ha); }
            }
            catch (Exception e) { fail++; Console.WriteLine("FAIL exception: " + e); }
            try { if (Directory.Exists(Dir)) Directory.Delete(Dir, true); } catch { }
            Console.WriteLine(pass + " passed, " + fail + " failed");
            return fail == 0 ? 0 : 1;
        }
        static HomeSettings NewSettings() { var s = new HomeSettings(); s.Fix(); return s; }
        static Dictionary<string, Entity> ById(IEnumerable<Entity> l) { return l.ToDictionary(e => e.Id); }
        static Entity E(string id, string state, string name, params object[] attr)
        {
            var e = new Entity { Id = id, State = state, Name = name };
            for (int i = 0; i + 1 < attr.Length; i += 2) e.Attr[(string)attr[i]] = attr[i + 1];
            return e;
        }

        // ---------------- Home Assistant's JSON
        static void Parsing()
        {
            var json = Family.Json(FakeData.States(DateTime.Now));
            var l = Entity.ParseStates(json);
            Ok(l.Count == FakeData.States(DateTime.Now).Count, "the states list parses: " + l.Count + " entities");
            var w = l.First(e => e.Id == "sensor.dockstream_2_smart_fountain_today_s_water_intake");
            Ok(w.Num == 3.52 && w.Unit == "oz" && w.Name == "Dockstream 2 Smart Fountain Today's water intake" && w.Domain == "sensor", "number, unit, friendly name, domain");
            Ok(Math.Abs((w.Changed - DateTime.Now.AddMinutes(-20)).TotalSeconds) < 5, "last_changed (UTC) becomes local time");
            Ok(!l.First(e => e.Id == "camera.garage").Available && l.First(e => e.Id == "button.granary_smart_camera_feeder_manual_feed").Available, "unavailable camera; a button's \"unknown\" still counts as usable");
            Ok(Entity.ParseStates("[{\"entity_id\":\"BAD id\",\"state\":\"x\"},{\"entity_id\":\"sensor.ok\",\"state\":5}]").Single().State == "5", "bad entity ids are skipped; numbers become text");
            try { Entity.ParseStates("{\"message\":\"no\"}"); Ok(false, "an object instead of a list throws"); } catch (FormatException) { Ok(true, "an object instead of a list throws"); }
            Ok(Ha.NormalizeUrl("nas:8123") == "http://nas:8123" && Ha.NormalizeUrl("https://home.example.com/") == "https://home.example.com" && Ha.NormalizeUrl("ftp://x") == null && Ha.NormalizeUrl("http://u:p@nas") == null && Ha.NormalizeUrl("http://nas/?a=1") == null, "base URLs");
            Ok(Ha.WsUrl("https://home.example.com") == "wss://home.example.com/api/websocket" && Ha.WsUrl("http://nas:8123") == "ws://nas:8123/api/websocket", "WebSocket URL");
            Ok(Ha.InsecureRemote("http://home.example.com") && !Ha.InsecureRemote("https://home.example.com") && !Ha.InsecureRemote("http://192.168.1.20:8123") && !Ha.InsecureRemote("http://nas:8123") && !Ha.InsecureRemote("http://100.101.1.2:8123"), "plain http over the internet is flagged; LAN / Tailscale / https aren't");
        }

        // ---------------- the mapping layer
        static void MappingTests()
        {
            var s = NewSettings(); var m = Roles.Build(FakeData.Entities(DateTime.Now), s);
            var want = new Dictionary<string, string> {
                { "feeder.feed", "button.granary_smart_camera_feeder_manual_feed" }, { "feeder.portion", "number.granary_smart_camera_feeder_manual_feed_quantity" },
                { "feeder.next", "sensor.granary_smart_camera_feeder_next_feeding_time" }, { "feeder.last", "sensor.granary_smart_camera_feeder_last_feed_time" },
                { "feeder.grams", "sensor.granary_smart_camera_feeder_today_feeding_quantity" }, { "feeder.meals", "sensor.granary_smart_camera_feeder_today_feeding_times" },
                { "feeder.jam", "binary_sensor.granary_smart_camera_feeder_dispenser_problem" }, { "feeder.food", "binary_sensor.granary_smart_camera_feeder_food_low" },
                { "feeder.desiccant", "sensor.granary_smart_camera_feeder_remaining_desiccant_days" }, { "feeder.battery", "sensor.granary_smart_camera_feeder_battery_level" },
                { "feeder.wifi", "sensor.granary_smart_camera_feeder_wifi_signal" }, { "feeder.sound", "switch.granary_smart_camera_feeder_sound" }, { "feeder.camera", "camera.granary_smart_camera_feeder_camera" },
                { "fountain.intake", "sensor.dockstream_2_smart_fountain_today_s_water_intake" }, { "fountain.irregular", "sensor.dockstream_2_smart_fountain_irregularities" },
                { "fountain.water", "sensor.dockstream_2_smart_fountain_water_level" }, { "fountain.filter", "sensor.dockstream_2_smart_fountain_remaining_filter_days" },
                { "fountain.cleaning", "sensor.dockstream_2_smart_fountain_remaining_cleaning_days" }, { "fountain.pump", "sensor.dockstream_2_smart_fountain_pump_status" },
                { "lock.bolt", "lock.front_door_lock_bolt" } };
            foreach (var kv in want) { var e = m.Get(kv.Key); Ok(e != null && e.Id == kv.Value, "mapped " + kv.Key + " -> " + (e == null ? "nothing" : e.Id)); }
            Ok(!m.ByRole.Values.Any(e => e.Id == "sensor.nas_cpu_temperature" || e.Id == "sun.sun"), "unrelated entities aren't mapped");
            Ok(m.ByRole.Values.Select(e => e.Id).Distinct().Count() == m.ByRole.Count, "no entity is used for two roles");
            Ok(m.Cameras.Count == 5 && m.Cameras.Last().Id == "camera.granary_smart_camera_feeder_camera" && m.Motion.Count == 2 && m.People.Count == 1 && m.EveryoneAway == false, "cameras (feeder camera last), motion sensors, people");
            // another way the integration (or a person) might name things
            var alt = new List<Entity> {
                E("sensor.cat_feeder_food_level", "15", "Cat Feeder Food level", "unit_of_measurement", "%"),
                E("button.cat_feeder_feed_now", "unknown", "Cat Feeder Feed now"),
                E("select.cat_feeder_feeding_portions", "1", "Cat Feeder Feeding portions"),
                E("sensor.cat_feeder_next_meal", "07:00", "Cat Feeder Next meal"),
                E("binary_sensor.water_fountain_water_shortage", "on", "Water Fountain Water shortage", "device_class", "problem"),
                E("switch.water_fountain_pump", "off", "Water Fountain Pump"),
                E("sensor.water_fountain_daily_water_intake", "120", "Water Fountain Daily water intake", "unit_of_measurement", "mL"),
                E("binary_sensor.water_fountain_filter_replace", "on", "Water Fountain Filter replace", "device_class", "problem"),
                E("sensor.kitchen_water_level", "50", "Kitchen kettle water level", "unit_of_measurement", "%") };
            var m2 = Roles.Build(alt, s);
            Ok(m2.Get("feeder.food").Id == "sensor.cat_feeder_food_level" && m2.Get("feeder.feed").Id == "button.cat_feeder_feed_now" && m2.Get("feeder.portion").Id == "select.cat_feeder_feeding_portions" && m2.Get("feeder.next").Id == "sensor.cat_feeder_next_meal", "other names: food level %, feed now, portions select, next meal");
            Ok(m2.Get("fountain.water").Id == "binary_sensor.water_fountain_water_shortage" && m2.Get("fountain.pump").Id == "switch.water_fountain_pump" && m2.Get("fountain.intake").Id == "sensor.water_fountain_daily_water_intake" && m2.Get("fountain.filter").Id == "binary_sensor.water_fountain_filter_replace", "other names: water shortage, pump switch, daily intake, filter replace");
            Ok(!m2.ByRole.Values.Any(e => e.Id == "sensor.kitchen_water_level"), "a kettle's water level isn't the fountain's (needs the fountain's own words)");
            var f2 = FeederView.From(m2, s, DateTime.Today.AddHours(8)); var w2 = FountainView.From(m2, s, null, DateTime.Now);
            Ok(f2.FoodLow && f2.FoodPct == 15 && f2.Next == DateTime.Today.AddDays(1).AddHours(7), "15 % food is low; \"07:00\" after 07:00 means tomorrow");
            Ok(w2.WaterLow && w2.PumpOn == false && w2.FilterDue && w2.IntakeToday == 120 && w2.Unit == "mL", "water shortage on, pump switch off, filter replace on, intake in mL");
        }
        static void Overrides()
        {
            var s = NewSettings(); var ents = FakeData.Entities(DateTime.Now);
            s.SetOverride("feeder.food", "sensor.granary_smart_camera_feeder_remaining_desiccant_days");
            var m = Roles.Build(ents, s);
            Ok(m.Get("feeder.food").Id == "sensor.granary_smart_camera_feeder_remaining_desiccant_days" && m.Manual.Contains("feeder.food"), "override: \"this entity is the feeder's food level\" wins");
            Ok(m.Get("feeder.desiccant") == null, "...and that entity isn't guessed for another role");
            s.SetOverride("feeder.food", ""); m = Roles.Build(ents, s);
            Ok(m.Get("feeder.food") == null && m.Manual.Contains("feeder.food"), "override \"none\": nothing, no guessing");
            s.SetOverride("feeder.food", "sensor.gone_away"); m = Roles.Build(ents, s);
            Ok(m.Get("feeder.food") == null && m.Missing.Contains("feeder.food"), "an override to an entity that's gone is reported missing");
            s.SetOverride("feeder.food", null); m = Roles.Build(ents, s);
            Ok(m.Get("feeder.food").Id == "binary_sensor.granary_smart_camera_feeder_food_low" && !m.Manual.Contains("feeder.food"), "back to automatic");
            s.Overrides.Add(new RoleOverride { Role = "nope.role", Entity = "sensor.x" }); s.Overrides.Add(new RoleOverride { Role = "feeder.food", Entity = "Bad Id" }); s.Fix();
            Ok(s.Overrides.Count == 0, "settings drop overrides for unknown roles or bad ids");
            var cands = Roles.Candidates(Roles.Find("feeder.food"), ents);
            Ok(cands.Count > 3 && cands[0].Id == "binary_sensor.granary_smart_camera_feeder_food_low" && cands.All(e => e.Domain == "sensor" || e.Domain == "binary_sensor"), "the picker lists the role's domains, best guess first");
        }
        static void Readers()
        {
            double? p; bool low; string t;
            Read.Level(E("sensor.a_level", "18", "x", "unit_of_measurement", "%"), 20, out p, out low, out t); Ok(p == 18 && low && t == "18 %", "18 % is low");
            Read.Level(E("sensor.a", "Insufficient", "x"), 20, out p, out low, out t); Ok(low && p == null && t == "Insufficient", "\"Insufficient\" is low");
            Read.Level(E("sensor.a", "normal", "x"), 20, out p, out low, out t); Ok(!low && t == "Normal", "\"normal\" isn't");
            Read.Level(E("sensor.a", "unavailable", "x"), 20, out p, out low, out t); Ok(!low && t == null, "unavailable says nothing");
            bool due; Read.Due(E("sensor.f", "1", "x", "unit_of_measurement", "days"), 2, out due, out t); Ok(due && t == "1 day left", "1 day left is due");
            Read.Due(E("sensor.f", "9", "x", "unit_of_measurement", "days"), 2, out due, out t); Ok(!due, "9 days isn't");
            Read.Due(E("sensor.f", "3", "x", "unit_of_measurement", "%"), 2, out due, out t); Ok(due, "3 % filter life is due");
            Ok(Read.Running(E("sensor.p", "running", "x")) == true && Read.Running(E("sensor.p", "Stopped", "x")) == false && Read.Running(E("sensor.p", "intermittent", "x")) == true && Read.Running(E("sensor.p", "weird", "x")) == null, "pump words");
        }

        // ---------------- smart alerts on synthetic data
        static Mapping FeederMap(DateTime next, DateTime last, int meals, bool jamSensor = false)
        {
            var f = "granary_smart_camera_feeder";
            var l = new List<Entity> {
                E("sensor." + f + "_next_feeding_time", next.ToUniversalTime().ToString("o"), "Granary Smart Camera Feeder Next feeding"),
                E("sensor." + f + "_last_feed_time", last.ToUniversalTime().ToString("o"), "Granary Smart Camera Feeder Last feed time"),
                E("sensor." + f + "_today_feeding_times", meals.ToString(), "Granary Smart Camera Feeder Today feeding times"),
                E("binary_sensor." + f + "_dispenser_problem", jamSensor ? "on" : "off", "Granary Smart Camera Feeder Dispenser problem", "device_class", "problem") };
            return Roles.Build(l, null);
        }
        static List<Alert> Eval(AlertEngine eng, DateTime now, Mapping m, HomeSettings s, History h = null, bool away = false)
        {
            return eng.Evaluate(now, m, FeederView.From(m, s, now), FountainView.From(m, s, h, now), s, away);
        }
        static void JamTests()
        {
            var s = NewSettings(); var eng = new AlertEngine();
            var seven = Day.AddHours(7); var yesterday = Day.AddDays(-1).AddHours(18);
            Ok(Eval(eng, Day.AddHours(6).AddMinutes(55), FeederMap(seven, yesterday, 0), s).Count == 0, "06:55, meal due at 07:00: nothing yet");
            // the sensor moves on to the next meal at 07:00 - the 07:00 meal stays watched
            Ok(Eval(eng, Day.AddHours(7).AddMinutes(5), FeederMap(Day.AddHours(12), yesterday, 0), s).Count == 0, "07:05: within the 10-minute grace");
            var a = Eval(eng, Day.AddHours(7).AddMinutes(11), FeederMap(Day.AddHours(12), yesterday, 0), s);
            Ok(a.Count == 1 && a[0].Key == "jam:202611200700" && a[0].Rule == "jam" && a[0].Text.Contains("07:00"), "07:11, nothing dispensed: \"No meal at 07:00\" (" + (a.Count > 0 ? a[0].Key : "none") + ")");
            Ok(Eval(eng, Day.AddHours(7).AddMinutes(20), FeederMap(Day.AddHours(12), Day.AddHours(7).AddMinutes(18), 1), s).Count == 0, "the meal comes out late (07:18): the alert clears");
            // fed on time: last feed time moved
            var e2 = new AlertEngine();
            Eval(e2, Day.AddHours(6).AddMinutes(59), FeederMap(seven, yesterday, 0), s);
            Ok(Eval(e2, Day.AddHours(7).AddMinutes(15), FeederMap(Day.AddHours(12), Day.AddHours(7).AddSeconds(12), 1), s).Count == 0, "dispensed at 07:00:12: no alert");
            // a feeder without a last-feed sensor: today's meal count went up
            var e3 = new AlertEngine(); var noLast = new Func<DateTime, int, Mapping>((next, meals) => { var mm = FeederMap(next, yesterday, meals); mm.ByRole.Remove("feeder.last"); return mm; });
            Eval(e3, Day.AddHours(6).AddMinutes(58), noLast(seven, 0), s);
            Ok(Eval(e3, Day.AddHours(7).AddMinutes(15), noLast(Day.AddHours(12), 1), s).Count == 0, "no last-feed sensor: the meal count going up counts as fed");
            Ok(Eval(new AlertEngine(), Day.AddHours(9), FeederMap(Day.AddHours(12), Day.AddHours(7), 1, true), s).Any(x => x.Key == "jam:sensor"), "the feeder's own dispenser-problem sensor raises it too");
            s.RulesOff.Add("jam"); Ok(Eval(new AlertEngine(), Day.AddHours(9), FeederMap(Day.AddHours(12), Day.AddHours(7), 1, true), s).Count == 0, "the rule can be switched off");
        }
        static void RuleTests()
        {
            var s = NewSettings(); var now = DateTime.Now; var ents = FakeData.Entities(now); var byId = ById(ents);
            var eng = new AlertEngine();
            var keys = Eval(eng, now, Roles.Build(ents, s), s).Select(a => a.Key).ToList();
            Ok(keys.Contains("camera-offline:camera.garage") && keys.Contains("filter") && keys.Count == 2, "made-up home: the garage camera (offline 12 min) and cleaning due (2 days): " + string.Join(", ", keys));
            byId["binary_sensor.granary_smart_camera_feeder_food_low"].State = "on";
            byId["sensor.dockstream_2_smart_fountain_water_level"].State = "12";
            var p = byId["sensor.dockstream_2_smart_fountain_pump_status"]; p.State = "stopped"; p.Changed = now.AddMinutes(-10);
            keys = Eval(eng, now, Roles.Build(ents, s), s).Select(a => a.Key).ToList();
            Ok(keys.Contains("food-low") && keys.Contains("water-low") && !keys.Contains("pump"), "food low, water low; the pump stopped only 10 min ago isn't an alert yet");
            p.Changed = now.AddMinutes(-31);
            Ok(Eval(eng, now, Roles.Build(ents, s), s).Any(a => a.Key == "pump"), "pump stopped 31 min: \"Pump stopped\"");
            var g = byId["camera.garage"]; g.Changed = now.AddMinutes(-2);
            Ok(!Eval(eng, now, Roles.Build(ents, s), s).Any(a => a.Rule == "camera-offline"), "a camera offline for 2 min isn't an alert yet");
            var mo = byId["binary_sensor.front_door_motion"]; mo.State = "on"; mo.Changed = now.AddMinutes(-1);
            Ok(!Eval(eng, now, Roles.Build(ents, s), s, null, true).Any(a => a.Rule == "motion-away"), "motion while away: off by default");
            s.MotionAlerts = true;
            Ok(Eval(eng, now, Roles.Build(ents, s), s, null, true).Any(a => a.Rule == "motion-away") && !Eval(eng, now, Roles.Build(ents, s), s, null, false).Any(a => a.Rule == "motion-away"), "motion alerts only while away");
            var a1 = Eval(eng, now, Roles.Build(ents, s), s).First(a => a.Key == "water-low"); Thread.Sleep(20);
            Ok(Eval(eng, now.AddMinutes(5), Roles.Build(ents, s), s).First(a => a.Key == "water-low").Since == a1.Since, "an alert keeps its start time while it's true");
        }
        static void IntakeTests()
        {
            string why;
            Ok(AlertEngine.IntakeLow(Day.AddHours(16), 1.0, 4.0, 50, "oz", out why) && why.Contains("1 oz") && why.Contains("4 oz"), "16:00, 1.0 oz vs a 4.0 oz average: low (" + why + ")");
            Ok(!AlertEngine.IntakeLow(Day.AddHours(16), 2.5, 4.0, 50, "oz", out why), "16:00, 2.5 oz: fine");
            Ok(!AlertEngine.IntakeLow(Day.AddHours(10), 0.2, 4.0, 50, "oz", out why), "before 15:00 it never says so (too early to tell)");
            Ok(!AlertEngine.IntakeLow(Day.AddHours(20), 1.0, null, 50, "oz", out why), "no average yet: nothing");
            var h = new History();
            for (int i = 1; i <= 2; i++) h.Intake[History.Day(Day.AddDays(-i))] = 4;
            Ok(h.Avg7(Day) == null, "fewer than 3 days: no average");
            h.Intake[History.Day(Day.AddDays(-3))] = 3.4; h.Intake[History.Day(Day.AddDays(-9))] = 100;
            Ok(Math.Abs(h.Avg7(Day).Value - 3.8) < 0.001, "average of the 7 days before today (older days ignored): " + h.Avg7(Day));
            h.Record(Day.AddHours(9), 1.2); h.Record(Day.AddHours(12), 2.0); h.Record(Day.AddHours(23), 0.1);
            Ok(h.Intake[History.Day(Day)] == 2.0, "a day keeps the most its counter reached (a reset at midnight doesn't lower it)");
            // through the engine with the fountain entities
            var s = NewSettings(); var ents = FakeData.Entities(Day.AddHours(16)); var byId = ById(ents);
            byId["sensor.dockstream_2_smart_fountain_today_s_water_intake"].State = "1.1";
            var a = Eval(new AlertEngine(), Day.AddHours(16), Roles.Build(ents, s), s, FakeData.Week(Day.AddHours(16)));
            var low = a.FirstOrDefault(x => x.Rule == "intake-low");
            Ok(low != null && low.Key == "intake-low:2026-11-20" && low.Text.Contains("not medical advice"), "the low-intake alert says it isn't medical advice");
            var w = FountainView.From(Roles.Build(ents, s), s, FakeData.Week(Day), Day.AddHours(16));
            Ok(w.Explain(true).Any(x => x.Contains("not medical advice")) && w.Explain(false).Any(x => x.Contains("clean")) && w.Explain(false).Any(x => x.Contains("3 irregularities")), "irregularities explained in plain words (cleaning, Petlibro's own count, intake)");
        }
        static List<Alert> A(params string[] keys) { return keys.Select(k => new Alert { Key = k, Rule = k.Split(':')[0], Title = k, Text = k }).ToList(); }
        static void DeskTests()
        {
            Ok(Quiet.IsQuiet(Day.AddHours(23), "22:00", "07:00") && Quiet.IsQuiet(Day.AddHours(3), "22:00", "07:00") && !Quiet.IsQuiet(Day.AddHours(7), "22:00", "07:00") && !Quiet.IsQuiet(Day.AddHours(12), "22:00", "07:00"), "quiet hours across midnight");
            Ok(Quiet.IsQuiet(Day.AddHours(13), "12:00", "14:00") && !Quiet.IsQuiet(Day.AddHours(15), "12:00", "14:00") && !Quiet.IsQuiet(Day.AddHours(1), "08:00", "08:00") && !Quiet.IsQuiet(Day, "25:00", "x"), "quiet hours in a day; equal or bad times = never");
            var s = NewSettings(); var d = new AlertDesk(); var t = Day.AddHours(12);
            Ok(d.Process(A("water-low"), t, s, false, null).Count == 1, "a new alert is sent");
            Ok(d.Process(A("water-low"), t.AddMinutes(1), s, false, null).Count == 0 && d.Process(A("water-low"), t.AddHours(5), s, false, null).Count == 0, "de-duplicated: not again while it's still true");
            d.Process(A(), t.AddHours(6), s, false, null);
            Ok(d.Process(A("water-low"), t.AddHours(6).AddMinutes(5), s, false, null).Count == 0, "it flapped back within 10 min: still not repeated");
            d.Process(A(), t.AddHours(7), s, false, null); d.Process(A(), t.AddHours(7).AddMinutes(11), s, false, null);
            Ok(d.Process(A("water-low"), t.AddHours(7).AddMinutes(12), s, false, null).Count == 1, "fixed for 10+ min, then low again: sent again");
            var q = new AlertDesk();
            Ok(q.Process(A("pump"), Day.AddHours(23), s, false, null).Count == 0 && q.Held(A("pump")), "quiet hours: held");
            Ok(q.Process(A("pump"), Day.AddDays(1).AddHours(6).AddMinutes(59), s, false, null).Count == 0, "still quiet at 06:59");
            Ok(q.Process(A("pump"), Day.AddDays(1).AddHours(7), s, false, null).Count == 1, "07:00: sent, since it's still true");
            var q2 = new AlertDesk(); q2.Process(A("pump"), Day.AddHours(23), s, false, null);
            Ok(q2.Process(A(), Day.AddDays(1).AddHours(7), s, false, null).Count == 0, "fixed during the night: nothing is sent in the morning");
            var g = new AlertDesk(); var r = g.Process(A("food-low"), t, s, true, null);
            Ok(r.Count == 1 && !r[0].Pc && r[0].Phone, "during a game: the phone gets it, the PC stays quiet");
            s.GameHoldAll = true; var g2 = new AlertDesk();
            Ok(g2.Process(A("food-low"), t, s, true, null).Count == 0 && g2.Process(A("food-low"), t.AddMinutes(30), s, false, null).Count == 1, "\"hold everything during games\": held, then sent after the game");
            s.GameHoldAll = false; var z = new AlertDesk();
            Ok(z.Process(A("filter"), t, s, false, t.AddHours(1)).Count == 0 && z.Process(A("filter"), t.AddHours(1).AddMinutes(1), s, false, t.AddHours(1)).Count == 1, "paused for an hour, then sent");
            s.QuietOn = false; Ok(new AlertDesk().Process(A("x"), Day.AddHours(23), s, false, null).Count == 1, "quiet hours off");
            var af = Path.Combine(Dir, "alerts.json"); var p1 = AlertDesk.Load(af); p1.Process(A("filter"), DateTime.Now, s, false, null);
            var p2 = AlertDesk.Load(af); Ok(p2.Sent.ContainsKey("filter") && p2.Process(A("filter"), DateTime.Now.AddMinutes(1), s, false, null).Count == 0, "what was sent survives a restart (no repeat when it starts with Windows)");
        }
        static void AutomationTests()
        {
            Ok(AutomationEngine.Check(new AutoRule { When = "alert:water-low", Then = "notify", RepeatHours = 12 }) == null, "fountain water low -> remind me twice a day: valid");
            Ok(AutomationEngine.Check(new AutoRule { When = "away", Then = "service:lock.unlock:lock.front_door_lock_bolt" }) != null, "an automation can never unlock");
            Ok(AutomationEngine.Check(new AutoRule { When = "away", Then = "service:button.press:button.granary_smart_camera_feeder_manual_feed" }) != null, "...or feed");
            Ok(AutomationEngine.Check(new AutoRule { When = "away", Then = "service:switch.turn_on:light.x" }) != null && AutomationEngine.Check(new AutoRule { When = "alert:nothing", Then = "notify" }) != null, "wrong entity domain / unknown alert refused");
            Ok(AutomationEngine.Describe(new AutoRule { When = "alert:water-low", Then = "notify", RepeatHours = 12 }) == "When \"Fountain water low\" is true → remind me, twice a day until it's fixed", "described in words");
            var eng = new AutomationEngine(); var rules = HomeSettings.DefaultAutomations(); var t = Day.AddHours(9); var none = new Dictionary<string, Entity>();
            var water = new List<Alert> { new Alert { Key = "water-low", Rule = "water-low", Title = "Fountain water low", Text = "x" } };
            Ok(eng.Evaluate(t, rules, water, none, false).Count == 0, "the first time: no reminder (the alert itself just went out)");
            Ok(eng.Evaluate(t.AddHours(11), rules, water, none, false).Count == 0, "11 hours later: nothing");
            var r = eng.Evaluate(t.AddHours(12), rules, water, none, false);
            Ok(r.Count == 1 && r[0].Kind == "notify" && r[0].About.Key == "water-low", "12 hours later, still low: a reminder");
            Ok(eng.Evaluate(t.AddHours(13), rules, new List<Alert>(), none, false).Count == 0 && eng.Evaluate(t.AddHours(30), rules, water, none, false).Count == 0, "fixed, then low again: starts over (no reminder straight away)");
            var cams = new List<AutoRule> { new AutoRule { Id = "c", On = true, When = "away", Then = "service:switch.turn_on:switch.front_door_power,switch.backyard_power" } };
            var c1 = eng.Evaluate(t, cams, new List<Alert>(), none, true);
            Ok(c1.Count == 1 && c1[0].Kind == "service" && c1[0].Domain == "switch" && c1[0].Service == "turn_on" && c1[0].Entities.Count == 2, "everyone away -> cameras on (switch.turn_on, two switches)");
            Ok(eng.Evaluate(t.AddMinutes(1), cams, new List<Alert>(), none, true).Count == 0, "once each time it becomes true");
            Ok(eng.Evaluate(t, new List<AutoRule> { new AutoRule { Id = "x", On = true, When = "state:sensor.a=on", Then = "notify" } }, new List<Alert>(), new Dictionary<string, Entity> { { "sensor.a", E("sensor.a", "on", "a") } }, false).Count == 1, "state:<entity>=<value>");
            Ok(eng.Evaluate(t, new List<AutoRule> { new AutoRule { Id = "y", On = false, When = "away", Then = "notify" } }, new List<Alert>(), none, true).Count == 0, "switched-off rules don't run");
        }
        static void SecretTests()
        {
            var tok = "eyJhbGciOiJIUzI1NiJ9.fake-long-lived-token-for-tests.0123456789";
            Ok(SecretStore.Set(SecretStore.Token, tok) == null && SecretStore.Get(SecretStore.Token) == tok, "the token is saved and read back");
            var raw = File.ReadAllBytes(SecretStore.FilePath);
            Ok(!Encoding.UTF8.GetString(raw).Contains("fake-long-lived") && !Encoding.Unicode.GetString(raw).Contains("fake-long-lived"), "secrets.dat is encrypted (DPAPI) - the token isn't in it in plain text");
            var d = SecretStore.Unprotect(raw); Ok(d != null && d[SecretStore.Token] == tok, "DPAPI round trip for this Windows user");
            Ok(SecretStore.Unprotect(new byte[] { 1, 2, 3 }) == null, "junk doesn't decrypt");
            Ok(Crash.Secrets.Contains(tok), "crash reports blank it out (Crash.Secrets)");
            Ok(SecretStore.Check(SecretStore.Token, "short") != null && SecretStore.Check(SecretStore.Token, "has space in it 0123456789012345") != null, "a too-short token or one with spaces is refused");
            Ok(SecretStore.Check(SecretStore.Discord, "https://discord.com/api/webhooks/123456/abcDEF-_x") == null && SecretStore.Check(SecretStore.Discord, "https://evil.example/api/webhooks/1/x") != null && SecretStore.Check(SecretStore.Discord, "http://discord.com/api/webhooks/1/x") != null, "Discord webhook URLs only (https)");
            Ok(SecretStore.Check(SecretStore.Ntfy, "https://ntfy.sh/hallow-home-x7f3k9") == null && SecretStore.Check(SecretStore.Ntfy, "http://ntfy.sh/topic") != null && SecretStore.Check(SecretStore.Ntfy, "https://ntfy.sh/a/b") != null, "ntfy topic URLs (https, one topic)");
            Ok(SecretStore.Mask(tok) == "eyJh••••••••789" && SecretStore.MaskUrl("https://discord.com/api/webhooks/1/secret") == "https://discord.com/••••••••", "shown masked");
            Ok(SecretStore.Set(SecretStore.Token, "") == null && !SecretStore.Has(SecretStore.Token), "removed");
            var st = new HomeSettings { HaUrl = "http://nas:8123" }; st.Fix(); st.Save();
            Ok(!File.ReadAllText(Path.Combine(HomeSettings.Dir, "settings.json")).Contains("token"), "settings.json has no token field at all");
        }
        static void LockTests()
        {
            var v = LockInfo.View(Roles.Build(FakeData.Entities(DateTime.Now), null));
            Ok(v.Kind == "ha-unavailable" && v.Title.StartsWith("Bluetooth-only") && v.Short == "unavailable" && !v.CanControl, "the Lock Bolt's entity is unavailable: \"Bluetooth-only - use the Wyze app nearby\"");
            v = LockInfo.View(Roles.Build(new List<Entity>(), null));
            Ok(v.Kind == "ble-only" && v.Text.Contains("Bluetooth") && v.Short == "bluetooth-only" && !v.CanControl, "no lock entity at all: the honest explanation");
            v = LockInfo.View(Roles.Build(new List<Entity> { E("lock.front_door", "locked", "Front door") }, null));
            Ok(v.Kind == "ha" && v.CanControl && v.Title == "Locked", "the extension point: if a lock entity ever works, it's shown (unlock asks first)");
            Ok(LockInfo.Later.Contains("ESPHome") && LockInfo.Later.Contains("don't count on it"), "the maybe-later note doesn't promise anything");
        }
        static void MjpegTests()
        {
            var ms = new MemoryStream(); var junk = Encoding.ASCII.GetBytes("--frame\r\nContent-Type: image/jpeg\r\n\r\n");
            for (int i = 0; i < 3; i++) { ms.Write(junk, 0, junk.Length); var j = FakeFrames.Jpeg("Test cam", i, 160, 90); ms.Write(j, 0, j.Length); ms.WriteByte(13); ms.WriteByte(10); }
            ms.Position = 0; var frames = new List<byte[]>();
            var n = Mjpeg.Read(ms, () => false, f => frames.Add(f));
            Ok(n == 3 && frames.All(f => f[0] == 0xFF && f[1] == 0xD8 && f[f.Length - 2] == 0xFF && f[f.Length - 1] == 0xD9), "MJPEG: 3 frames split from a multipart stream");
            var bmp = Mjpeg.Decode(frames[2]); Ok(bmp != null && bmp.PixelWidth == 160 && bmp.PixelHeight == 90, "a frame decodes (160x90)");
            Ok(Mjpeg.Decode(new byte[] { 0xFF, 0xD8, 1, 2, 0xFF, 0xD9 }) == null, "a broken frame is skipped, not a crash");
            var rd = Path.Combine(Dir, "rec"); Directory.CreateDirectory(Path.Combine(rd, "front-door", "2026-11-20"));
            File.WriteAllBytes(Path.Combine(rd, "front-door", "2026-11-20", "a.mp4"), new byte[10]); File.SetLastWriteTime(Path.Combine(rd, "front-door", "2026-11-20", "a.mp4"), Day.AddHours(1));
            File.WriteAllBytes(Path.Combine(rd, "front-door", "b.mp4"), new byte[20]); File.SetLastWriteTime(Path.Combine(rd, "front-door", "b.mp4"), Day.AddHours(2));
            File.WriteAllText(Path.Combine(rd, "notes.txt"), "x");
            string err; var recs = Cams.List(rd, 10, out err);
            Ok(err == null && recs.Count == 2 && recs[0].Name == "front-door\\b.mp4" && recs[1].Name.EndsWith("a.mp4"), "recordings: video files from the folder (and its sub-folders), newest first; other files ignored");
            Cams.List(Path.Combine(Dir, "nope"), 10, out err); Ok(err != null && err.Contains("can't be reached"), "a missing folder says so");
            Ok(Cams.BridgeName(E("camera.front_door", "idle", "Front Door")) == "front-door" && Cams.LiveUrl("http://nas:8889", E("camera.x", "idle", "Living Room")) == "http://nas:8889/living-room/", "wyze-bridge stream names and the live URL");
        }
        static Dictionary<string, string> Brushes(string xaml)
        {
            var d = new Dictionary<string, string>();
            foreach (Match m in Regex.Matches(xaml, "<SolidColorBrush x:Key=\"(\\w+)\" Color=\"(#[0-9A-Fa-f]{6,8})\"/>")) d[m.Groups[1].Value] = m.Groups[2].Value;
            return d;
        }
        static double Contrast(string fg, string bg) { return Mode.Ratio(fg.Length == 9 ? "#" + fg.Substring(3) : fg, bg); }
        static void ModeTests()
        {
            var sys = new FakeSystemTheme();
            Ok(!Mode.Resolve(null, null, sys) && !Mode.Resolve("follow", "dark", sys), "missing / dark -> dark");
            Ok(Mode.Resolve("follow", "light", sys) && !Mode.Resolve("dark", "light", sys) && Mode.Resolve("light", "dark", sys), "the app's own Light / Dark wins");
            sys.Light = true; Ok(Mode.Resolve("follow", "system", sys) && sys.Reads == 1, "system -> Windows' app mode (fake registry)");
            sys.Light = null; Ok(!Mode.Resolve("follow", "system", sys), "system, unknown -> dark");
            foreach (var light in new[] { false, true })
                foreach (var accent in new[] { "#F07820", "#8B5CF6", "#22C55E", "#FFE600" })
                {
                    var x = Program.ThemeXaml(light, accent).Replace("assembly=HallowHome", "assembly=" + typeof(Run).Assembly.GetName().Name);
                    try { XamlReader.Parse(x); } catch (Exception e) { Ok(false, "theme parses: " + e.Message); continue; }
                    var b = Brushes(x); var tag = (light ? "light" : "dark") + " " + accent;
                    var bgs = new[] { "Bg", "Panel", "PanelHi", "Side" };
                    Ok(bgs.All(bg => Contrast(b["Text"], b[bg]) >= 4.5 && Contrast(b["Muted"], b[bg]) >= 4.5 && Contrast(b["TextSoft"], b[bg]) >= 4.5), tag + ": text, soft and muted text >= 4.5:1 on every surface");
                    Ok(bgs.All(bg => Contrast(b["AccentBrush"], b[bg]) >= 4.5 && Contrast(b["Lilac"], b[bg]) >= 4.5), tag + ": accent and lilac text >= 4.5:1");
                    Ok(new[] { "Green", "Amber", "Red" }.All(k => Contrast(b[k], b["Panel"]) >= (light ? 4.5 : 3.0)), tag + ": status colours readable on panels");
                    if (light) Ok(!Regex.IsMatch(x, "#(14101B|1C1726|ECE8F4)\\b", RegexOptions.IgnoreCase), tag + ": no dark-only colours left");
                }
        }
        static void Misc()
        {
            bool on, lists;
            Controller.HubHosting("{\"on\":true,\"apps\":[\"Mixer\",\"Home\"]}", out on, out lists); Ok(on && lists, "Hub lists Home by name");
            Controller.HubHosting("{\"on\":true,\"generic\":true}", out on, out lists); Ok(on && lists, "Hub 2.13+ generic host");
            Controller.HubHosting("{\"on\":true,\"apps\":[\"Mixer\"]}", out on, out lists); Ok(on && !lists, "Hub on but not listing Home: keep our icon");
            Ok(Family.PipeName("Home").StartsWith("HallowFamily.Home." + Environment.UserName + ".test-"), "test pipes never reach the real app");
            Ok(MainWindow.PageId("fountain") == "pets" && MainWindow.PageId("Cameras") == "cameras" && MainWindow.PageId("nowhere") == null, "page names");
            Ok(Fmt.Ago(TimeSpan.FromMinutes(48)) == "48 minutes" && Fmt.Clock(Day.AddDays(1).AddHours(7), Day.AddHours(20)) == "tomorrow 07:00", "formats");
        }

        // ---------------- the real client against the fake Home Assistant
        static bool Wait(Func<bool> f, int ms) { var sw = Stopwatch.StartNew(); while (sw.ElapsedMilliseconds < ms) { if (f()) return true; Thread.Sleep(50); } return f(); }
        static void ClientTests(FakeHa ha)
        {
            var bad = new Ha(ha.Url, "wrong-token-1234567890");
            try { bad.Check(); Ok(false, "a wrong token is refused"); } catch (HaException e) { Ok(e.Status == 401 && e.Message.Contains("refused the token") && !e.Message.Contains("wrong-token"), "a wrong token: \"refused the token\" (and the message never contains it)"); }
            try { new Ha("http://localhost:1", ha.Token).Check(); Ok(false, "nothing there"); } catch (HaException e) { Ok(!e.Message.Contains(ha.Token), "nothing listening: " + e.Message); }
            var h = new Ha(ha.Url, ha.Token);
            Ok(h.Check() == null && h.Version() == "2026.11.0-fake", "GET /api/ and /api/config");
            var states = h.States(); Ok(states.Count == FakeData.States(DateTime.Now).Count, "GET /api/states: " + states.Count + " entities");
            h.Call("switch", "turn_on", new Dictionary<string, object> { { "entity_id", "switch.granary_smart_camera_feeder_sound" } });
            Ok(ha.State("switch.granary_smart_camera_feeder_sound") == "on" && ha.Calls.Last().StartsWith("switch.turn_on {\"entity_id\":\"switch.granary_smart_camera_feeder_sound\"}"), "POST /api/services/switch/turn_on");
            try { h.Call("lock; rm", "x", null); Ok(false, "bad service names refused"); } catch (HaException) { Ok(true, "bad service names are refused before anything is sent"); }
            var jpg = h.Snapshot("camera.front_door"); Ok(jpg.Length > 1000 && jpg[0] == 0xFF && jpg[1] == 0xD8, "camera snapshot is a JPEG (" + jpg.Length + " bytes)");
            // live updates
            var got = new List<string>(); bool live = false;
            h.StateChanged += (e, id) => { lock (got) got.Add(id + "=" + (e == null ? "gone" : e.State)); };
            h.Live += (on, wy) => live = on;
            h.StartEvents();
            Ok(Wait(() => live && ha.Subscribers > 0, 8000), "WebSocket: auth with the token, subscribed to state_changed");
            ha.Set("sensor.dockstream_2_smart_fountain_water_level", "41");
            Ok(Wait(() => { lock (got) return got.Contains("sensor.dockstream_2_smart_fountain_water_level=41"); }, 5000), "a state_changed event arrives");
            h.StopEvents();
            var badWs = new Ha(ha.Url, "wrong-token-1234567890"); string whyWs = null; badWs.Live += (on, w) => { if (!on) whyWs = w; }; badWs.StartEvents();
            Ok(Wait(() => whyWs != null, 8000) && whyWs.Contains("refused") && !whyWs.Contains("wrong-token"), "WebSocket with a wrong token: refused (" + whyWs + ")"); badWs.StopEvents();
            ha.Set("sensor.dockstream_2_smart_fountain_water_level", "64");
            // the stream the camera tiles use
            var feed = new CameraFeed("camera.living_room"); var frames = 0; BitmapSource last = null;
            feed.Frame = b => { Interlocked.Increment(ref frames); last = b; };
            feed.Start(h, 30);
            Ok(Wait(() => frames >= 3, 8000), "the camera feed reads frames from the fake MJPEG stream (" + frames + ")");
            feed.Stop();
            Ok(last != null && last.PixelWidth == 320, "decoded 320x180 frames");
        }
        // Discord / ntfy stand-ins on localhost: what the real senders post
        static void SenderTests()
        {
            var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();
            var http = new HttpListener(); http.Prefixes.Add("http://localhost:" + port + "/"); http.Start();
            var seen = new List<string>();
            new Thread(() => { try { while (true) { var c = http.GetContext(); string b; using (var r = new StreamReader(c.Request.InputStream)) b = r.ReadToEnd(); lock (seen) seen.Add(c.Request.Url.AbsolutePath + "|" + c.Request.Headers["Title"] + "|" + c.Request.Headers["Priority"] + "|" + c.Request.Headers["Authorization"] + "|" + b); c.Response.StatusCode = c.Request.Url.AbsolutePath.Contains("fail") ? 500 : 204; c.Response.Close(); } } catch { } }) { IsBackground = true }.Start();
            try
            {
                var rs = new RealSender();
                Ok(rs.Discord("t", "x") == "no Discord webhook set" && rs.Ntfy("t", "x", false) == "no ntfy topic set", "nothing set: nothing sent");
                Ok(SecretStore.Set(SecretStore.Discord, "http://localhost:" + port + "/api/webhooks/1/abc") == null && SecretStore.Set(SecretStore.Ntfy, "http://localhost:" + port + "/home-topic") == null && SecretStore.Set(SecretStore.NtfyToken, "tk_testtoken123") == null, "(test-only localhost stand-ins saved)");
                Ok(rs.Discord("Fountain water low", "Top up the tank.") == null, "Discord: posted");
                Ok(rs.Ntfy("Fountain water low — now", "Top up the tank.", true) == null, "ntfy: posted");
                lock (seen)
                {
                    var d = seen.FirstOrDefault(x => x.StartsWith("/api/webhooks/1/abc")); var n = seen.FirstOrDefault(x => x.StartsWith("/home-topic"));
                    Ok(d != null && d.Contains("\"content\":\"**Fountain water low**\\nTop up the tank.\"") && d.Contains("\"username\":\"Hallow Home\"") && d.Contains("\"parse\":[]"), "Discord gets the title and text as JSON, no @mentions");
                    Ok(n != null && n.Contains("|Fountain water low - now|high|Bearer tk_testtoken123|Top up the tank."), "ntfy gets the text, an ASCII Title, high priority and the access token");
                }
                SecretStore.Set(SecretStore.Discord, "http://localhost:" + port + "/api/webhooks/1/fail");
                var e = rs.Discord("t", "x"); Ok(e == "Discord answered 500" && !e.Contains("localhost"), "a failure says what answered, never the webhook address");
            }
            finally { http.Stop(); SecretStore.Set(SecretStore.Discord, ""); SecretStore.Set(SecretStore.Ntfy, ""); SecretStore.Set(SecretStore.NtfyToken, ""); }
        }

        // ---------------- the camera grid, in the real window (not shown), against the fake Home Assistant
        static void Pump(int ms) { var f = new DispatcherFrame(); var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) }; t.Tick += (s, e) => { t.Stop(); f.Continue = false; }; t.Start(); Dispatcher.PushFrame(f); }
        static bool PumpUntil(Func<bool> f, int ms) { var sw = Stopwatch.StartNew(); while (sw.ElapsedMilliseconds < ms) { if (f()) return true; Pump(100); } return f(); }
        static IEnumerable<T> All<T>(object root) where T : DependencyObject
        {
            var d = root as DependencyObject; if (d == null) yield break;
            foreach (var ch in LogicalTreeHelper.GetChildren(d)) { if (ch is T) yield return (T)ch; foreach (var x in All<T>(ch)) yield return x; }
        }
        static void GridTest(Application app, FakeHa ha)
        {
            Mode.IsLight = false; app.Resources.MergedDictionaries.Clear();
            app.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(Program.ThemeXaml(false, Accent.Default).Replace("assembly=HallowHome", "assembly=" + typeof(Run).Assembly.GetName().Name)));
            var st = HomeSettings.Load(); st.HaUrl = ha.Url; st.Save(); SecretStore.Set(SecretStore.Token, ha.Token);
            var c = new Controller(true);
            Ok(PumpUntil(() => c.Connected && c.Entities.Count > 20, 10000), "the app connects to the fake Home Assistant (" + c.Entities.Count + " entities)");
            Ok(c.Map.Cameras.Count == 5 && c.Feeder.CanFeed && c.Lock.Kind == "ha-unavailable", "cameras, the feeder's Feed now, the lock's honest state");
            var before0 = ha.StreamsOpened; var w = new MainWindow(c); w.Show("cameras");
            var before = ha.StreamsOpened;
            PumpUntil(() => All<Image>(w).Count(i => i.Source is BitmapSource && ((BitmapSource)i.Source).PixelWidth == 320) >= 4, 12000);
            var live = All<Image>(w).Count(i => i.Source is BitmapSource && ((BitmapSource)i.Source).PixelWidth == 320);
            Ok(live >= 4 && ha.StreamsOpened - before0 >= 4, "the camera grid shows frames from the fake streams: " + live + " live tiles, " + (ha.StreamsOpened - before0) + " streams (the garage is offline)");
            Ok(All<TextBlock>(w).Any(t => t.Text == "Offline"), "the offline camera says so");
            Snap.Save(w, Path.Combine(Dir, "grid.png"), 1200, 800);
            var snapDir = Environment.GetEnvironmentVariable("HALLOW_HOME_GRIDPNG"); if (snapDir != null) try { File.Copy(Path.Combine(Dir, "grid.png"), snapDir, true); } catch { }
            w.Show("dashboard"); w.Close();
            c.Detach();
        }
        // docker-wyze-bridge's player page in the WebView2 live view (one window far off-screen)
        static void LiveViewTest(Application app, FakeHa ha)
        {
            if (CameraWindow.RuntimeVersion() == null) { Console.WriteLine("     (WebView2 runtime not installed - live view check skipped)"); return; }
            var w = new CameraWindow("Front Door", ha.Url + "/bridge/front-door/") { WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000, ShowActivated = false, ShowInTaskbar = false };
            bool? ok = null; w.Loaded2 += r => ok = r;
            w.Show();
            PumpUntil(() => ok != null, 20000);
            string title = null;
            if (ok == true) PumpUntil(() => { try { title = w.View.CoreWebView2.DocumentTitle; } catch { } return title == "playing"; }, 10000);
            Ok(ok == true && title == "playing", "live view: the bridge's player page loads in WebView2 and its video starts (title \"" + title + "\")");
            w.Close(); Pump(300);
            var wv = Path.GetDirectoryName(CameraWindow.DataFolder);   // %LOCALAPPDATA%HallowHomeTest: WebView2 lets go of it a moment after the window closes
            for (int i = 0; i < 20 && Directory.Exists(wv); i++) { try { Directory.Delete(wv, true); } catch { Thread.Sleep(250); } }
            Ok(!Directory.Exists(wv), "the test WebView2 folder is cleaned up");
        }

        // ---------------- the family pipe of a test copy, talking to the fake Home Assistant
        static string Cmd(string exe, string dir, string line)
        {
            var psi = new ProcessStartInfo(exe, "--cmd \"" + line + "\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            psi.EnvironmentVariables["HALLOW_HOME_TESTDIR"] = dir;
            using (var p = Process.Start(psi)) { var o = p.StandardOutput.ReadToEnd().Trim(); p.WaitForExit(10000); return o; }
        }
        static string Until(string exe, string dir, string text, int ms = 8000)
        {
            string r = null; var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms) { r = Cmd(exe, dir, "status"); if (r.Contains(text)) return r; Thread.Sleep(200); }
            return r;
        }
        static void PipeAndApp(FakeHa ha)
        {
            var exe = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(typeof(Run).Assembly.Location)), "HallowHome.exe");
            if (!File.Exists(exe)) { Ok(false, "HallowHome.exe is built"); return; }
            var dir = Path.Combine(Dir, "app"); Directory.CreateDirectory(dir);
            var psi = new ProcessStartInfo(exe, "--tray") { UseShellExecute = false, CreateNoWindow = true };
            psi.EnvironmentVariables["HALLOW_HOME_TESTDIR"] = dir; psi.EnvironmentVariables["HALLOW_HOME_HEADLESS"] = "1";
            var app = Process.Start(psi);
            try
            {
                string r = null; for (int i = 0; i < 40 && (r == null || !r.StartsWith("ok")); i++) { Thread.Sleep(250); r = Cmd(exe, dir, "ping"); }
                Ok(r == "ok {\"app\":\"Home\",\"version\":\"" + Program.Version + "\"}", "ping: " + r);
                r = Cmd(exe, dir, "status");
                Ok(r.Contains("\"configured\":false") && r.Contains("\"connected\":false") && r.Contains("\"lock\":\"bluetooth-only\""), "not set up yet: not configured, the lock is bluetooth-only");
                Ok(Cmd(exe, dir, "feed 1 confirm").StartsWith("error Hallow Home isn't connected"), "feed without Home Assistant: refused");
                Cmd(exe, dir, "test-quiet off"); Cmd(exe, dir, "test-channels none");   // the alerts that are true at once (garage offline, cleaning due) go nowhere
                Ok(Cmd(exe, dir, "test-connect " + ha.Url + " short").StartsWith("error That's too short"), "a too-short token is refused");
                Ok(Cmd(exe, dir, "test-connect " + ha.Url + " " + ha.Token) == "ok", "address and token saved");
                r = Until(exe, dir, "\"live\":true", 12000);
                Ok(r.Contains("\"connected\":true") && r.Contains("\"live\":true") && r.Contains("\"cameras\":5") && r.Contains("\"camerasOnline\":4"), "connected, live updates on, 5 cameras (4 online)");
                foreach (var k in new[] { "feeder", "fountain", "lock", "alerts", "alertFlags", "away", "quiet", "game", "lastCrash", "nextFeed", "todayMeals", "waterLow", "pumpOn", "intakeToday" }) Ok(r.Contains("\"" + k + "\":"), "status has \"" + k + "\"");
                var sj = File.ReadAllText(Path.Combine(dir, "status.json"));
                Ok(!r.Contains(ha.Token) && !sj.Contains(ha.Token) && !r.Contains("localhost") && !sj.Contains("localhost") && !r.Contains("camera_proxy") && !sj.Contains("http"), "status / status.json carry no token, address or camera URL");
                Ok(!File.ReadAllText(Path.Combine(dir, "settings.json")).Contains(ha.Token) && !Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(dir, "secrets.dat"))).Contains("fake-ha-token"), "the token is only in secrets.dat, encrypted");
                // Feed now: only with confirm
                int calls; lock (ha.Calls) calls = ha.Calls.Count;
                r = Cmd(exe, dir, "feed 2");
                Ok(r.StartsWith("error not fed: add \"confirm\"") && r.Contains("about 20 g"), "feed 2 without confirm: not fed (" + r + ")");
                Thread.Sleep(600); lock (ha.Calls) Ok(ha.Calls.Count == calls, "...and no service was called");
                Ok(Cmd(exe, dir, "feed 9 confirm").StartsWith("error Pick 1 to 6") && Cmd(exe, dir, "feed lots confirm").StartsWith("error use:"), "too many portions / junk refused");
                var meals = ha.State("sensor.granary_smart_camera_feeder_today_feeding_times");
                Ok(Cmd(exe, dir, "feed 2 confirm") == "ok feeding 2", "feed 2 confirm");
                Ok(Wait(() => { lock (ha.Calls) return ha.Calls.Count >= calls + 2; }, 5000), "two service calls");
                lock (ha.Calls)
                {
                    var c1 = ha.Calls[calls]; var c2 = ha.Calls[calls + 1];
                    Ok(c1 == "number.set_value {\"entity_id\":\"number.granary_smart_camera_feeder_manual_feed_quantity\",\"value\":2}" && c2 == "button.press {\"entity_id\":\"button.granary_smart_camera_feeder_manual_feed\"}", "the portions are set, then the feeder's manual feed button is pressed: " + c1 + " / " + c2);
                }
                r = Until(exe, dir, "\"todayMeals\":" + (int.Parse(meals) + 1));
                Ok(r.Contains("\"todayMeals\":" + (int.Parse(meals) + 1)), "the feeder reports one more meal (live update)");
                // live alerts: the fountain runs low -> water-low within seconds, delivered to the channels that are on (stand-ins)
                Ok(Cmd(exe, dir, "test-quiet off") == "ok" && Cmd(exe, dir, "test-channels hub discord ntfy") == "ok", "quiet hours off for now; alert channels on (test stand-ins)");
                ha.Set("sensor.dockstream_2_smart_fountain_water_level", "9");
                r = Until(exe, dir, "\"waterLow\":true");
                Ok(r.Contains("\"waterLow\":true") && r.Contains("\"water-low\""), "the water drops to 9 %: water low flagged at once (WebSocket)");
                Ok(Wait(() => Cmd(exe, dir, "test-sent").Contains("ntfy|Fountain water low"), 4000), "sent: " + Cmd(exe, dir, "test-sent"));
                var sentOnce = Cmd(exe, dir, "test-sent");
                Ok(Regex.Matches(sentOnce, "discord\\|Fountain water low").Count == 1, "Discord got it once");
                Cmd(exe, dir, "test-recompute"); Cmd(exe, dir, "test-recompute"); Thread.Sleep(300);
                Ok(Cmd(exe, dir, "test-sent") == sentOnce, "de-duplicated: checking again sends nothing more");
                r = Cmd(exe, dir, "status"); Ok(r.Contains("\"notifications\":1"), "one PC notification");
                // quiet hours hold it
                Ok(Cmd(exe, dir, "test-quiet 00:00 23:59") == "ok", "quiet hours all day");
                ha.Set("sensor.dockstream_2_smart_fountain_pump_status", "stopped");
                Cmd(exe, dir, "test-clock 40"); Cmd(exe, dir, "test-recompute");
                r = Cmd(exe, dir, "test-recompute"); Ok(r.Contains("\"pump\""), "40 min later the pump alert is true: " + r);
                Thread.Sleep(300); Ok(!Cmd(exe, dir, "test-sent").Contains("Pump"), "...but held in quiet hours");
                Cmd(exe, dir, "test-quiet off"); Cmd(exe, dir, "test-recompute");
                Ok(Wait(() => Cmd(exe, dir, "test-sent").Contains("discord|Fountain pump stopped"), 4000), "quiet hours over: sent");
                // a game: the PC stays quiet, the phone still gets it
                Cmd(exe, dir, "test-game Delta Force");
                ha.Set("binary_sensor.granary_smart_camera_feeder_food_low", "on"); Thread.Sleep(1500); Cmd(exe, dir, "test-recompute");
                Ok(Wait(() => Cmd(exe, dir, "test-sent").Contains("ntfy|Feeder food low"), 4000), "during a game the phone gets \"Feeder food low\"");
                r = Cmd(exe, dir, "status"); Ok(r.Contains("\"game\":\"Delta Force\"") && r.Contains("\"notifications\":2") && Regex.IsMatch(r, "\"held\":[1-9]"), "...and nothing popped up on the PC");
                Cmd(exe, dir, "test-game none");
                // mapping override over the pipe's test hook
                Ok(Cmd(exe, dir, "test-override feeder.food sensor.granary_smart_camera_feeder_remaining_desiccant_days") == "ok sensor.granary_smart_camera_feeder_remaining_desiccant_days", "override saved and used");
                Ok(Cmd(exe, dir, "test-override feeder.food auto") == "ok binary_sensor.granary_smart_camera_feeder_food_low", "back to automatic");
                // the rest of the pipe
                r = Cmd(exe, dir, "menu"); Ok(r.StartsWith("ok {\"app\":\"Home\",\"items\":[") && r.Contains("\"id\":\"feed\"") && r.Contains("\"id\":\"cameras\"") && r.Contains("\"id\":\"snooze\""), "menu (Hub 2.13+ lists any app answering it)");
                Ok(Cmd(exe, dir, "away on") == "ok away" && Cmd(exe, dir, "status").Contains("\"away\":true") && Cmd(exe, dir, "away off") == "ok home", "away on / off");
                Ok(Cmd(exe, dir, "open pets") == "ok" && Cmd(exe, dir, "open nowhere").StartsWith("error"), "open <page>");
                Ok(Cmd(exe, dir, "invoke nothing") == "error unknown item" && Cmd(exe, dir, "frobnicate") == "error unknown command" && Cmd(exe, dir, "unlock").StartsWith("error unknown command"), "unknown item / command; there is no unlock over the pipe");
                Ok(Cmd(exe, dir, "test-autostart on") == "ok True" && Cmd(exe, dir, "test-autostart off") == "ok False", "Start with Windows (test file, never the real Run key)");
                r = Cmd(exe, dir, "status"); Ok(r.Contains("\"log\":") && !r.Contains(ha.Token), "the log never has the token");
                Ok(Cmd(exe, dir, "quit") == "ok", "quit");
                Ok(app.WaitForExit(8000), "the test copy exits");
                Ok(File.ReadAllText(Path.Combine(dir, "status.json")).Contains("\"running\":false"), "status.json says it isn't running");
                var cp = new ProcessStartInfo(exe, "--cmd ping") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true }; cp.EnvironmentVariables["HALLOW_HOME_TESTDIR"] = dir;
                using (var c = Process.Start(cp)) { c.StandardOutput.ReadToEnd(); c.WaitForExit(10000); Ok(c.ExitCode == 2, "--cmd when it isn't running -> exit 2"); }
            }
            finally { try { if (!app.HasExited) app.Kill(); } catch { } }
        }
    }
}
