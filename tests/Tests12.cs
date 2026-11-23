// Hallow Home 1.2 tests: the setup wizard against the stand-in Home Assistant, the docker-compose snippet, the camera grid /
// reconnect / pause with the stand-in's streams and bridge players (all muted), the feeder's schedule through the stand-in's
// Petlibro services (with the confirm), the fountain reminders, Demo mode, and "45%" in every text.
// Every browser player here is muted twice over: the engine runs with --mute-audio in tests, and CoreWebView2.IsMuted is set
// before it navigates. Windows are shown far off-screen only (never on the visible desktop), and all of them are closed.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using FakeHa = HallowHome.DemoHa;

namespace HallowHome.Tests
{
    public static partial class Run
    {
        static Controller shared;

        static void SettingsMigration()
        {
            var s = new HomeSettings { LiveBase = "http://nas:8889" }; s.Fix();
            Ok(s.BridgeWebRtc == "http://nas:8889" && s.BridgeHls == "http://nas:8888" && s.LiveBase == null, "1.1's live view address becomes the bridge's WebRTC + HLS addresses");
            s = new HomeSettings { LiveBase = "http://nas:8888" }; s.Fix();
            Ok(s.BridgeHls == "http://nas:8888" && s.BridgeWebRtc == "http://nas:8889", "...from the HLS port too");
            Ok(HomeSettings.BaseUrl("nas:8889") == "http://nas:8889" && HomeSettings.BaseUrl("http://u:p@nas:8889") == null && HomeSettings.BaseUrl("http://nas:8889/x?y") == null && HomeSettings.BaseUrl("ftp://nas") == null, "bridge addresses: plain base URLs only");
            s = new HomeSettings { NasFolder = "/volume1/x; rm -rf /", GridSize = 7, StreamMode = "weird", FilterEveryDays = 0, FilterChangedOn = "yesterday" }; s.Fix();
            Ok(s.NasFolder == "/volume1/docker/wyze-bridge" && s.GridSize == 2 && s.StreamMode == "auto" && s.FilterEveryDays == 30 && s.FilterChangedOn == null, "bad values fall back to the defaults");
            var d = NewSettings(); Ok(!d.Demo && d.StreamMode == "auto" && d.GridSize == 2 && d.WizardSkipped.Count == 0, "defaults: no Demo mode, auto source, 2x2 grid");
        }

        // ---------------- step 2's docker-compose snippet
        static void ComposeTests()
        {
            var y = Wizard.Compose(new Wizard.ComposeOptions { Folder = "/volume1/docker/wyze-bridge", Tz = "Europe/London" });
            Ok(y.Contains("      - API_ID=<REPLACE with your Wyze API ID>") && y.Contains("      - API_KEY=<REPLACE with your Wyze API key>"), "compose: the Wyze API ID and key are clearly marked placeholders");
            Ok(Regex.IsMatch(y, "WYZE_PASSWORD=<REPLACE[^\n]*never in Hallow Home") && y.Contains("WYZE_EMAIL=<REPLACE"), "the Wyze e-mail / password are placeholders for the NAS only");
            Ok(new[] { "8554:8554", "8888:8888", "8889:8889", "8189:8189/udp", "5000:5000" }.All(p => y.Contains("      - " + p)), "the bridge's ports (RTSP, HLS, WebRTC, WebRTC media, its web page)");
            Ok(y.Contains("TZ=Europe/London") && y.Contains("RECORD_ALL=False") && !y.Contains("/record:/record") && y.Contains("      - /volume1/docker/wyze-bridge/tokens:/tokens"), "time zone, no recording, the sign-in volume");
            Ok(!y.Contains("\t") && y.Split('\n').All(l => !l.EndsWith(" ")) && y.StartsWith("# docker-wyze-bridge") && y.Contains("\nservices:\n  wyze-bridge:\n    container_name: wyze-bridge\n    image: mrlt8/wyze-bridge:latest"), "compose layout: spaces only, services > wyze-bridge > image");
            var ph = y.Split('\n').Where(l => l.Contains("<REPLACE") && !l.TrimStart().StartsWith("#")).ToList();
            Ok(ph.Count == 4 && ph.All(l => Regex.IsMatch(l, "^      - [A-Z_]+=<REPLACE")), "4 placeholders (e-mail, password, API ID, API key), each an environment value (" + ph.Count + ")");
            var r = Wizard.Compose(new Wizard.ComposeOptions { Folder = "/volume2/cams/", Record = true, Tz = "bad zone; x" });
            Ok(r.Contains("RECORD_ALL=True") && r.Contains("      - /volume2/cams/record:/record") && r.Contains("TZ=<REPLACE with your time zone"), "recording on: RECORD_ALL and the record folder; a bad time zone stays a placeholder");
            Ok(Wizard.Compose(new Wizard.ComposeOptions { Folder = "/volume1/../etc" }).Contains("/volume1/docker/wyze-bridge/tokens") && Wizard.Compose(new Wizard.ComposeOptions { Folder = "C:\\x" }).Contains("/volume1/docker/wyze-bridge/tokens"), "odd folders fall back to /volume1/docker/wyze-bridge");
            Ok(Wizard.IanaZone("Eastern Standard Time") == "America/New_York" && Wizard.IanaZone("Nowhere Time") == null, "Windows time zone -> Docker's name");
            Ok(Wizard.NasHost("http://192.168.1.20:8123") == "192.168.1.20" && Wizard.NasHost(null) == "nas", "the NAS's name from Home Assistant's address");
            Ok(Wizard.CameraSteps("nas").Any(l => l.Contains("rtsp://nas:8554/<camera-name>")) && Wizard.TokenSteps.Any(l => l.Contains("Security")) && Wizard.TokenSteps.Any(l => l.Contains("Long-lived access tokens")) && Wizard.PetlibroSteps.Any(l => l.Contains(Wizard.PetlibroRepo)), "the exact clicks: token, Generic Camera, HACS repository");
        }

        // ---------------- the feeder's schedule (unit level)
        static void ScheduleUnitTests()
        {
            var now = Day.AddHours(13);
            var states = Entity.ParseStates(Family.Json(FakeData.States(now)));
            var v = ScheduleView.From(states.First(x => x.Id == "binary_sensor.granary_smart_camera_feeder_feeding_schedule"));
            Ok(v.Present && v.On && v.Plans.Count == 3 && v.Plans[0].Time == "07:00" && v.Plans[0].Label == "Breakfast" && v.Plans[0].Portions == 1 && v.Plans[0].Grams == 10 && v.Plans[0].DaysText == "every day", "the schedule sensor's plans parse (time, name, portions, grams, days)");
            Ok(v.Plans[0].State == "dispensed" && v.Plans[2].State == "pending" && v.TodayPlans.Count() == 3, "today's state per meal (13:00: breakfast and lunch fed)");
            var f = FeederView.From(Roles.Build(states, null), NewSettings(), now);
            Ok(f.PlannedGrams == 30 && f.PlannedMeals == 3 && f.FedTodayText(10) == "2 meals · 20 g of 30 g planned", "fed today: " + f.FedTodayText(10));
            Ok(ScheduleView.NormTime("7:5") == null && ScheduleView.NormTime("7:05") == "07:05" && ScheduleView.NormTime("07:30:00") == "07:30" && ScheduleView.NormTime("24:00") == null, "times");
            Ok(new FeedPlan { Days = new[] { 1, 2, 3, 4, 5 } }.DaysText == "weekdays" && new FeedPlan { Days = new[] { 6, 7 } }.DaysText == "weekends" && new FeedPlan { Days = new[] { 1, 3 } }.DaysText == "Mon Wed" && new FeedPlan().DaysText == "once", "days in words");
            Ok(Schedule.Check(new PlanEdit { Kind = "edit", PlanId = 101, Time = "7.30", Portions = 1 }, v, 48) != null && Schedule.Check(new PlanEdit { Kind = "edit", PlanId = 999, Time = "07:30", Portions = 1 }, v, 48) != null
                && Schedule.Check(new PlanEdit { Kind = "add", Time = "07:30", Portions = 0 }, v, 48) != null && Schedule.Check(new PlanEdit { Kind = "add", Time = "07:30", Portions = 2, Label = "Second breakfast" }, v, 48) != null
                && Schedule.Check(new PlanEdit { Kind = "add", Time = "07:30", Portions = 2, Days = new[] { 1, 7 } }, v, 48) == null, "bad changes are refused before anything is sent (time, unknown meal, portions, a name with spaces)");
            string svc; var d = Schedule.Data(new PlanEdit { Kind = "edit", PlanId = 101, Time = "7:30", Portions = 2, Days = new[] { 1, 2 } }, "dev1", out svc);
            Ok(svc == "edit_feeding_plan" && Family.Json(d) == "{\"device_id\":\"dev1\",\"plan_id\":101,\"time\":\"07:30\",\"portions\":2,\"days\":[\"1\",\"2\"]}", "petlibro.edit_feeding_plan data: " + Family.Json(d));
            d = Schedule.Data(new PlanEdit { Kind = "toggle", PlanId = 102, Enable = false }, "dev1", out svc);
            Ok(svc == "toggle_feeding_plan" && Family.Json(d) == "{\"device_id\":\"dev1\",\"plan_id\":102,\"enable\":false}", "toggle_feeding_plan data");
            Schedule.Data(new PlanEdit { Kind = "delete", PlanId = 103 }, "dev1", out svc); Ok(svc == "delete_feeding_plan", "delete_feeding_plan");
            d = Schedule.Data(new PlanEdit { Kind = "add", Time = "21:00", Portions = 1 }, "dev1", out svc); Ok(svc == "add_feeding_plan" && !d.ContainsKey("plan_id") && !d.ContainsKey("days"), "add_feeding_plan (no days = once)");
            string title; var text = Schedule.Describe(new PlanEdit { Kind = "edit", PlanId = 101, Time = "07:30", Portions = 2 }, v, 10, out title);
            Ok(title == "Change the 07:00 (Breakfast) meal?" && text.Contains("New: 07:30, 2 portions (about 20 g), every day") && text.Contains("Was: 07:00, 1 portion (about 10 g)"), "the confirm says exactly what changes: " + title);
            Schedule.Describe(new PlanEdit { Kind = "delete", PlanId = 103 }, v, 10, out title); Ok(title == "Delete the 18:00 (Dinner) meal?", "the delete confirm");
            Ok(Schedule.Editable("[{\"domain\":\"petlibro\",\"services\":{\"edit_feeding_plan\":{},\"add_feeding_plan\":{}}}]") == true && Schedule.Editable("[{\"domain\":\"petlibro\",\"services\":{}}]") == false
                && Schedule.Editable("[{\"domain\":\"button\",\"services\":{\"press\":{}}}]") == false && Schedule.Editable("nope") == null, "GET /api/services: editable / read-only / unknown");
            var m = Roles.Build(new List<Entity> { E("binary_sensor.cat_feeder_today_s_feeding_schedule", "on", "Cat Feeder Today's feeding schedule"), E("binary_sensor.cat_feeder_feeding_schedule", "on", "Cat Feeder Feeding schedule") }, null);
            Ok(m.Get("feeder.schedule") != null && m.Get("feeder.schedule").Id == "binary_sensor.cat_feeder_feeding_schedule", "the whole schedule is mapped, not today's one");
        }

        // ---------------- fountain reminders
        static void ReminderTests()
        {
            var now = Day.AddHours(10);
            var r = Reminder.Interval("filter", 30, null, now, 2); Ok(!r.Due && !r.Started && r.Text.StartsWith("Not started"), "own interval not started yet: says so, not due");
            r = Reminder.Interval("filter", 30, History.Day(Day.AddDays(-10)), now, 2); Ok(!r.Due && r.DaysLeft == 20 && r.Text == "20 days left", "changed 10 days ago, every 30: 20 days left");
            r = Reminder.Interval("filter", 30, History.Day(Day.AddDays(-29)), now, 2); Ok(r.Due && r.Text == "1 day left", "1 day left: due");
            r = Reminder.Interval("cleaning", 14, History.Day(Day.AddDays(-17)), now, 2); Ok(r.Due && r.Text == "Due now (3 days late)", "3 days late: " + r.Text);
            var s = NewSettings(); s.FilterChangedOn = History.Day(Day.AddDays(-31)); s.CleanedOn = History.Day(Day.AddDays(-1));
            var ents = FakeData.Entities(now).Where(e => !e.Id.Contains("remaining_filter") && !e.Id.Contains("remaining_cleaning")).ToList();
            var m = Roles.Build(ents, s); var w = FountainView.From(m, s, null, now);
            Ok(w.Filter.Own && w.FilterDue && w.Cleaning.Own && !w.CleaningDue && w.Filter.Source.Contains("your interval"), "no filter / cleaning entity: your own intervals");
            Ok(new AlertEngine().Evaluate(now, m, FeederView.From(m, s, now), w, s, false).Any(a => a.Rule == "filter" && a.Title == "Fountain filter due"), "...and the filter alert comes from them");
            var w2 = FountainView.From(Roles.Build(FakeData.Entities(now), s), s, null, now);
            Ok(!w2.Filter.Own && w2.Filter.Text == "9 days left" && w2.Filter.Source.Contains("Petlibro"), "with the integration's filter days, those count");
            s.OwnReminders = true; w2 = FountainView.From(Roles.Build(FakeData.Entities(now), s), s, null, now);
            Ok(w2.Filter.Own && w2.FilterDue, "\"use my own intervals\" overrides them");
            var low = FakeData.Entities(now); low.First(e => e.Id.EndsWith("_water_level")).State = "15";
            var s2 = NewSettings(); var m2 = Roles.Build(low, s2); var w3 = FountainView.From(m2, s2, null, now);
            Ok(new AlertEngine().Evaluate(now, m2, FeederView.From(m2, s2, now), w3, s2, false).Any(a => a.Rule == "water-low" && a.Text.Contains("15%")), "low-water alert under 20% (15%)");
        }

        // ---------------- where a picture comes from
        static void StreamUnitTests()
        {
            Streams.BackoffScale = 1;
            Ok(string.Join(",", Enumerable.Range(0, 7).Select(i => Streams.Backoff(i).TotalSeconds)) == "2,4,8,16,30,30,30", "reconnect back-off: 2, 4, 8, 16, then 30 s");
            Ok(Streams.Pick("auto", true, true, true, true, true, true) == "mjpeg" && Streams.Pick("auto", false, true, true, true, true, true) == "webrtc" && Streams.Pick("auto", false, true, true, false, true, true) == "hls" && Streams.Pick("auto", false, true, false, false, true, true) == "mjpeg", "auto: the grid uses MJPEG; the big view WebRTC, else HLS, else MJPEG");
            Ok(Streams.Pick("auto", true, false, true, true, true, true) == "webrtc" && Streams.Pick("auto", true, false, null, null, false, false) == null, "no Home Assistant: the bridge's player, or nothing");
            Ok(Streams.Pick("hls", true, true, true, true, true, true) == "hls" && Streams.Pick("hls", true, true, false, true, true, true) == "mjpeg" && Streams.Pick("webrtc", true, true, null, null, false, true) == "webrtc" && Streams.Pick("mjpeg", false, false, true, true, true, true) == null, "forced sources, with a fallback");
            Ok(Streams.PlayerUrl("webrtc", "http://nas:8888", "http://nas:8889", E("camera.x", "idle", "Front Door")) == "http://nas:8889/front-door/" && Streams.PlayerUrl("hls", "http://nas:8888", null, E("camera.x", "idle", "Back Yard")) == "http://nas:8888/back-yard/", "player addresses");
        }

        // ---------------- 45%, not 45 % - in every text of the app
        static void PercentTexts()
        {
            var src = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(typeof(Run).Assembly.Location)), "src");
            if (!Directory.Exists(src)) { Ok(false, "the src folder is next to tests"); return; }
            var bad = new List<string>();
            foreach (var f in Directory.GetFiles(src, "*.cs"))
                foreach (var l in File.ReadAllLines(f))
                    if (!l.TrimStart().StartsWith("//") && Regex.IsMatch(l, "\"[^\"]*(\\d|\\)) %[^\"]*\"|\" %")) bad.Add(Path.GetFileName(f) + ": " + l.Trim());
            Ok(bad.Count == 0, "no space before % in any text (45%, not 45 %)" + (bad.Count > 0 ? ": " + bad[0] : ""));
        }

        // ---------------- wizard step 1 against the stand-in
        static void WizardTests(FakeHa ha)
        {
            string r = null; try { r = Ha.Probe(ha.Url); } catch (Exception e) { r = e.Message; }
            Ok(r.StartsWith("Home Assistant answered at " + ha.Url), "wizard step 1: \"Test connection\" finds Home Assistant without a token (" + r + ")");
            try { Ha.Probe("http://127.0.0.1:1", 3000); Ok(false, "nothing at that address"); } catch (HaException e) { Ok(e.Message.Contains("Nothing answered"), "nothing at that address: " + e.Message); }
            try { Ha.Probe(ha.HlsBase, 3000); Ok(false, "something else answering"); } catch (HaException e) { Ok(e.Message.Contains("doesn't look like Home Assistant"), "something else answering: " + e.Message); }
            try { Ha.Probe("ftp://x"); Ok(false, "not an address"); } catch (HaException e) { Ok(e.Message.Contains("isn't an address"), "not an address: refused"); }
            string t; bool ok; MainWindow.WizardResult(ha.Url, "wrong-token-1234567890", out t, out ok);
            Ok(!ok && t.Contains("refused the token") && !t.Contains("wrong-token"), "\"Test again\" with a wrong token: " + t);
            MainWindow.WizardResult(ha.Url, ha.Token, out t, out ok);
            Ok(ok && t.Contains("feeder found") && t.Contains("fountain found") && t.Contains("5 cameras") && t.Contains("2026.11.0-demo"), "...with the right token: " + t);
        }

        static string TextOf(TextBlock t) { var s = t.Text ?? ""; foreach (var r in t.Inlines.OfType<System.Windows.Documents.Run>()) s += r.Text; return s; }
        static void OffScreen(Window w) { w.WindowStartupLocation = WindowStartupLocation.Manual; w.Left = -32000; w.Top = -32000; w.ShowActivated = false; w.ShowInTaskbar = false; }
        static void Click(Window w, string label) { var b = All<Button>(w).FirstOrDefault(x => (x.Content as string) == label); if (b != null) b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
        static int TileCount(Window w) { return All<Border>(w).Count(b => b.ToolTip is string && ((string)b.ToolTip).StartsWith("Open ")); }

        // ---------------- the camera grid, paging, pause when hidden, reconnect, snapshot (MJPEG from the stand-in)
        static void CameraTests(Application app, FakeHa ha)
        {
            Streams.BackoffScale = 0.02;
            var st = HomeSettings.Load(); st.BridgeHls = ha.HlsBase; st.BridgeWebRtc = ha.WebRtcBase; st.StreamMode = "auto"; st.GridSize = 2; st.Save();
            var c = new Controller(true); shared = c;
            Ok(PumpUntil(() => c.Connected && c.BridgeWebRtcOk == true && c.BridgeHlsOk == true, 12000), "the bridge stand-in answers: WebRTC and HLS offered");
            var steps = Wizard.Steps(c);
            Ok(steps.All(s => s.Done), "wizard: all three steps done against the stand-in (" + string.Join(" / ", steps.Select(s => s.Id + ":" + string.Join(",", s.Missing))) + ")");
            ha.OfferWebRtc = false; ha.OfferHls = false; bool done = false; c.ProbeBridge(() => done = true); PumpUntil(() => done, 8000);
            steps = Wizard.Steps(c);
            Ok(!steps[1].Done && steps[1].Missing.Any(m => m.Contains("no player found")), "the bridge offers nothing: step 2 says what's missing (" + string.Join(",", steps[1].Missing) + ")");
            c.S.WizardSkipped.Add("cameras"); Ok(Wizard.Steps(c)[1].Skipped, "a skipped step is listed as skipped"); c.S.WizardSkipped.Clear();
            ha.OfferWebRtc = true; ha.OfferHls = true; done = false; c.ProbeBridge(() => done = true); PumpUntil(() => done, 8000);

            var w = new MainWindow(c); OffScreen(w); w.Show(); w.Show("cameras");
            Ok(TileCount(w) == 4 && All<TextBlock>(w).Any(t => t.Text == "1 / 2  "), "2\u00D72: four tiles, page 1 of 2 (" + TileCount(w) + ")");
            PumpUntil(() => All<Image>(w).Count(i => i.Source is BitmapSource && ((BitmapSource)i.Source).PixelWidth == 320) >= 3, 12000);
            Ok(w.Feeds.Count() == 3 && w.Players.Count() == 0, "auto in the grid: Home Assistant's MJPEG for the 3 online tiles (" + w.Feeds.Count() + "), no browser players");
            Click(w, "Next"); Ok(TileCount(w) == 1, "Next: the fifth camera on page 2");
            c.S.GridSize = 3; w.Show("cameras"); Ok(TileCount(w) == 5 && !All<Button>(w).Any(b => (b.Content as string) == "Next"), "3\u00D73: all five, no pages");
            PumpUntil(() => ha.OpenStreams >= 4, 8000);
            // pause: hidden (to the tray) or minimised -> every stream stops; back -> they restart
            w.Hide();
            Ok(w.Paused && w.RunningFeeds == 0, "hidden: paused, no stream running in the app");
            Ok(Wait(() => ha.OpenStreams == 0, 8000), "...and the stand-in sees every stream closed (bandwidth saved): " + ha.OpenStreams);
            w.Show();
            Ok(!w.Paused && PumpUntil(() => ha.OpenStreams >= 4, 10000), "shown again: the streams restart (" + ha.OpenStreams + ")");
            w.SetPaused(true); Ok(w.RunningFeeds == 0 && Wait(() => ha.OpenStreams == 0, 8000), "minimised (the same switch as hidden): streams stop");
            w.SetPaused(false);
            // reconnect with back-off: three failed tries (503), then live; and a camera that drops and comes back
            w.SetPaused(true); Wait(() => ha.OpenStreams == 0, 8000);   // only this feed asks for streams now
            ha.FailStreams = 3; var feed = new CameraFeed("camera.backyard"); int frames = 0; var says = new List<string>();
            feed.StatusChanged += x => { lock (says) says.Add(x); }; feed.Frame = b => Interlocked.Increment(ref frames);
            feed.Start(c.Client, 10);
            Ok(Wait(() => frames > 0, 10000) && feed.Attempts >= 4, "reconnect: 3 failed tries, then live (" + feed.Attempts + " tries)");
            lock (says) Ok(says.Count(x => x.StartsWith("Reconnecting in")) >= 3 && says.Contains("Live"), "it says \"Reconnecting in N s\" between tries: " + string.Join(" | ", says.Distinct()));
            ha.Set("camera.backyard", "unavailable"); Thread.Sleep(600); var f0 = frames;
            ha.Set("camera.backyard", "streaming");
            Ok(Wait(() => frames > f0 + 3, 10000), "the camera dropped and came back: the feed reconnected by itself");
            feed.Stop(); w.SetPaused(false);
            // snapshot -> Pictures\Hallow Home (the tests' own folder)
            string msg = null; c.Snapshot(c.Entities["camera.front_door"], null, m => msg = m);
            PumpUntil(() => msg != null, 8000);
            var pics = Directory.Exists(Cams.SnapshotFolder) ? Directory.GetFiles(Cams.SnapshotFolder, "Front Door *.jpg") : new string[0];
            Ok(msg != null && msg.StartsWith("Saved Front Door") && pics.Length == 1 && Cams.SnapshotFolder.StartsWith(Dir) && File.ReadAllBytes(pics[0])[0] == 0xFF, "snapshot saved as a JPEG in (the tests' own) Pictures\\Hallow Home: " + msg);
            w.Close(); c.S.GridSize = 2;
        }

        // ---------------- the feeder's schedule through the stand-in's Petlibro services, always after a confirm
        static void ScheduleTests(Application app, FakeHa ha)
        {
            var c = shared;
            Ok(c.Feeder.Schedule.Plans.Count == 3 && c.ScheduleEditable == true, "the feeder's own schedule is read from Home Assistant (3 meals); the integration offers changes");
            int calls; lock (ha.Calls) calls = ha.Calls.Count;
            var edit = new PlanEdit { Kind = "edit", PlanId = 101, Time = "07:30", Portions = 2 };
            Ok(c.ScheduleChange(edit, false, null) == "Schedule changes need a confirm.", "without the confirm: refused");
            Ok(c.ScheduleChange(new PlanEdit { Kind = "edit", PlanId = 101, Time = "7h30", Portions = 2 }, true, null) == "Use a 24-hour time like 07:30.", "a bad time: refused");
            Thread.Sleep(300); lock (ha.Calls) Ok(ha.Calls.Count == calls, "...and nothing was sent");
            string res = "x"; Ok(c.ScheduleChange(edit, true, x => res = x) == null, "confirmed: sent");
            PumpUntil(() => res != "x", 8000);
            lock (ha.Calls)
            {
                var call = ha.Calls.Skip(calls).FirstOrDefault(x => x.StartsWith("petlibro."));
                Ok(res == null && call == "petlibro.edit_feeding_plan {\"device_id\":\"" + FakeHa.DeviceId + "\",\"plan_id\":101,\"time\":\"07:30\",\"portions\":2}", "petlibro.edit_feeding_plan with the feeder's device id (from /api/template): " + call);
            }
            Ok(PumpUntil(() => c.Feeder.Schedule.Plans.Any(p => p.Id == 101 && p.Time == "07:30" && p.Portions == 2), 8000), "the schedule updates live (07:30, 2 portions)");
            res = "x"; var addErr = c.ScheduleChange(new PlanEdit { Kind = "add", Time = "21:00", Portions = 1, Days = new[] { 6, 7 } }, true, x => res = x);
            PumpUntil(() => c.Feeder.Schedule.Plans.Count == 4, 8000);
            var added = c.Feeder.Schedule.Plans.FirstOrDefault(p => p.Time == "21:00");
            Ok(added != null && added.DaysText == "weekends", "add a meal: 21:00 at weekends (" + (addErr ?? res ?? "sent") + "; " + string.Join(",", c.Feeder.Schedule.Plans.Select(p => p.Time + "/" + p.DaysText)) + ")");
            c.ScheduleChange(new PlanEdit { Kind = "toggle", PlanId = 102, Enable = false }, true, null);
            Ok(PumpUntil(() => c.Feeder.Schedule.Plans.Any(p => p.Id == 102 && !p.Enabled), 8000), "turn the 12:00 meal off");
            c.ScheduleChange(new PlanEdit { Kind = "toggle", PlanId = 102, Enable = true }, true, null); PumpUntil(() => c.Feeder.Schedule.Plans.Any(p => p.Id == 102 && p.Enabled), 8000);
            if (added != null) { c.ScheduleChange(new PlanEdit { Kind = "delete", PlanId = added.Id }, true, null); Ok(PumpUntil(() => c.Feeder.Schedule.Plans.Count == 3, 8000), "delete it again"); }
            var w = new MainWindow(c); w.Show("pets");
            Ok(All<Button>(w).Count(b => (b.Content as string) == "Edit") == 3 && All<Button>(w).Any(b => (b.Content as string) == "Add a meal"), "the Pets page: Edit on each meal, Add a meal");
            Ok(All<TextBlock>(w).Any(t => Regex.IsMatch(TextOf(t), "\\d+ g of \\d+ g planned")), "the Pets page shows \"fed today\" against what's planned");
            var pw = Dialogs.PlanWindow(c, c.Feeder.Schedule.Plans.First(p => p.Id == 101), null);
            Ok(All<TextBox>(pw).Any(t => t.Text == "07:30"), "the meal dialog starts from the meal's own time");
            // an integration without the services: read-only, and it says so
            ha.ScheduleServices = false; c.Connect(); PumpUntil(() => c.Connected && c.ScheduleEditable == false, 10000);
            Ok(c.ScheduleEditable == false, "an integration without the schedule services: read-only");
            lock (ha.Calls) calls = ha.Calls.Count;
            var why = c.ScheduleChange(edit, true, null);
            Ok(why != null && why.Contains("read-only"), "a change is refused: " + why);
            Thread.Sleep(300); lock (ha.Calls) Ok(ha.Calls.Count == calls, "...and nothing was sent");
            w.Show("pets");
            Ok(!All<Button>(w).Any(b => (b.Content as string) == "Edit") && All<TextBlock>(w).Any(t => t.Text != null && t.Text.StartsWith("Read-only: the Petlibro integration")), "the Pets page shows the schedule read-only and says why");
            ha.ScheduleServices = true; c.Connect(); PumpUntil(() => c.Connected && c.ScheduleEditable == true, 10000);
            w.Close();
        }

        // ---------------- the bridge's players (WebView2, muted, off-screen): reconnect, grid, big view mute, full screen
        static void PlayerTests(Application app, FakeHa ha)
        {
            var c = shared;
            try
            {
                if (Streams.RuntimeVersion() == null) { Console.WriteLine("     (WebView2 runtime not installed - player checks skipped)"); return; }
                ha.FailPlayers = 2;
                var pv = new PlayerView(c.PlayerUrl("webrtc", c.Entities["camera.front_door"]), "webrtc");
                var hw = new Window { Content = pv, Width = 640, Height = 400, WindowStyle = WindowStyle.None }; OffScreen(hw); hw.Show();
                PumpUntil(() => pv.Playing, 20000);
                string title = null; PumpUntil(() => { try { title = pv.View.CoreWebView2.DocumentTitle; } catch { } return title == "playing"; }, 10000);
                Ok(pv.MutedBeforeNavigate && pv.EngineMuted, "the WebRTC player is muted before it navigates");
                Ok(pv.Playing && title == "playing" && pv.Attempts >= 3, "...two failed loads (503), then it plays (title \"" + title + "\") after " + pv.Attempts + " tries");
                pv.SetMuted(false); Ok(!pv.EngineMuted, "unmute works"); pv.SetMuted(true); Ok(pv.EngineMuted, "...and mute again");
                hw.Close(); pv.Stop();

                c.S.StreamMode = "hls";
                var w = new MainWindow(c); OffScreen(w); w.Show(); w.Show("cameras");
                PumpUntil(() => w.Players.Count() >= 3 && w.Players.All(p => p.Playing), 25000);
                Ok(w.Players.Count() == 3 && w.Players.All(p => p.Kind == "hls" && p.Playing && p.EngineMuted) && w.Feeds.Count() == 0, "grid on HLS: 3 muted players playing (the 4th tile is the offline camera), no MJPEG");
                w.Hide(); Ok(w.Paused && w.Players.Count() == 0, "hidden: the players stop");
                w.Show(); Ok(PumpUntil(() => w.Players.Count() == 3 && w.Players.All(p => p.Playing), 20000), "shown again: they restart");

                c.S.StreamMode = "webrtc"; w.ShowCamera("camera.front_door");
                PumpUntil(() => w.BigPlayer != null && w.BigPlayer.Playing, 15000);
                Ok(w.BigPlayer != null && w.BigPlayer.Kind == "webrtc" && w.BigPlayer.EngineMuted && All<Button>(w).Any(b => (b.Content as string) == "Unmute"), "big view: the WebRTC player, muted (button says Unmute)");
                Click(w, "Unmute"); Pump(200);
                Ok(w.BigPlayer != null && !w.BigPlayer.EngineMuted && All<Button>(w).Any(b => (b.Content as string) == "Mute"), "Unmute: sound on for the big view");
                Click(w, "All cameras"); w.ShowCamera("camera.front_door"); PumpUntil(() => w.BigPlayer != null && w.BigPlayer.Playing, 15000);
                Ok(w.BigPlayer != null && w.BigPlayer.EngineMuted, "back to the camera later: muted again");
                var fv = w.OpenFull(c.Entities["camera.living_room"], false); OffScreen(fv); fv.Show();
                PumpUntil(() => fv.Player != null && fv.Player.Playing, 15000);
                Ok(fv.Muted && fv.Player != null && fv.Player.EngineMuted && fv.Player.Kind == "webrtc", "full screen: the player, muted");
                fv.ToggleMute(); Ok(fv.Player != null && !fv.Player.EngineMuted, "full screen: Unmute");
                fv.StopStream(); Ok(fv.Player == null, "full screen minimised: the stream stops");
                fv.StartStream(); Ok(PumpUntil(() => fv.Player != null && fv.Player.Playing, 15000), "restored: it plays again");
                fv.Close(); Ok(fv.Player == null, "closed: nothing left playing");
                var fm = new FullView(c, c.Entities["camera.backyard"], "mjpeg", null, false);
                Ok(PumpUntil(() => fm.Feed != null && fm.Feed.Frames > 0, 10000) && fm.Player == null, "full screen with MJPEG (Home Assistant's pictures)");
                fm.StopStream();
                w.Close(); Pump(300);
            }
            finally
            {
                c.S.StreamMode = "auto"; c.Detach(); Pump(300);
                var wv = Path.GetDirectoryName(Streams.DataFolder);   // %LOCALAPPDATA%\HallowHomeTest: WebView2 lets go of it a moment after the windows close
                for (int i = 0; i < 40 && Directory.Exists(wv); i++) { try { Directory.Delete(wv, true); } catch { Pump(250); } }
                Ok(!Directory.Exists(wv), "the test WebView2 folder is cleaned up");
            }
        }

        // ---------------- Demo mode: made-up devices inside the app; nothing is sent
        static void DemoModeTests(Application app)
        {
            var st = HomeSettings.Load(); st.HaUrl = null; st.Demo = false; st.UseDiscord = true; st.UseNtfy = true; st.QuietOn = false; st.BridgeHls = null; st.BridgeWebRtc = null; st.Save();
            SecretStore.Set(SecretStore.Token, "");
            var c = new Controller(true);
            Ok(!c.Configured && !c.Connected, "not set up: not configured");
            var steps = Wizard.Steps(c);
            Ok(steps[0].Missing.Any(m => m.Contains("address")) && steps[0].Missing.Any(m => m.Contains("token")) && steps.All(s => !s.Done) && steps[1].Missing.Count >= 2 && steps[2].Missing.Count >= 2, "the wizard lists what's missing in each step: " + string.Join(" / ", steps.Select(s => string.Join(", ", s.Missing))));
            c.SetDemo(true);
            Ok(PumpUntil(() => c.Connected && c.Entities.Count > 20 && c.Feeder.Schedule.Plans.Count == 3, 10000) && c.Demo != null && c.Map.Cameras.Count == 5, "Demo mode: the made-up home runs inside the app");
            Ok(c.Demo.Url.StartsWith("http://localhost:") && c.S.HaUrl == null && !SecretStore.Has(SecretStore.Token), "...on localhost; your address and token are untouched");
            c.Recompute(); Pump(500);
            lock (((FakeSender)c.Sender).Sent) Ok(((FakeSender)c.Sender).Sent.Count == 0 && c.Active.Count > 0, "alerts are true in the demo (" + c.Active.Count + ") but nothing is sent");
            lock (c.Sent) Ok(c.Sent.Any(x => x.Contains("not sent (Demo mode)")), "...the Alerts page lists them as \"not sent (Demo mode)\"");
            Ok((bool)c.StatusData(true)["demo"], "status says demo");
            PumpUntil(() => c.BridgeWebRtcOk == true, 8000); Ok(c.StreamFor(null, false) == "webrtc", "the demo's bridge offers WebRTC for the big view");
            var demo = c.Demo; int n; lock (demo.Calls) n = demo.Calls.Count;
            c.Feed(1, true, null); Ok(Wait(() => { lock (demo.Calls) return demo.Calls.Count > n; }, 5000), "Feed now in the demo presses the made-up feeder's button only");
            var w = new MainWindow(c); w.Show("dashboard");
            Ok(All<TextBlock>(w).Any(t => t.Text != null && t.Text.StartsWith("Demo mode: made-up devices")), "every page says it's the demo");
            w.Show("setup"); Ok(All<Button>(w).Any(b => (b.Content as string) == "Leave Demo mode") && All<TextBox>(w).Any(t => t.Text.Contains("API_KEY=<REPLACE")), "the Setup page: Leave Demo mode, and the compose snippet");
            w.Close();
            c.SetDemo(false);
            Ok(c.Demo == null && !c.Configured && !c.Connected, "Demo mode off: back to not set up");
            c.Detach();
            st = HomeSettings.Load(); st.UseDiscord = false; st.UseNtfy = false; st.Save();
        }
    }
}
