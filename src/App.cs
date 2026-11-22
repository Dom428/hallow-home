// Hallow Home - one private app for your smart-home and pet devices, with Home Assistant as the backend: a Petlibro feeder and
// fountain (the community Petlibro integration), Wyze cameras (docker-wyze-bridge / the Wyze integration) and an honest Lock Bolt
// card. Dashboard, smart alerts (Hub, Discord, ntfy; de-duplicated, quiet hours, quiet during games), simple automations, and the
// Hallow family link (pipe, status.json, Hub's tray). Home Assistant itself is not bundled - see NAS-SETUP.md.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using WF = System.Windows.Forms;

[assembly: AssemblyTitle("Hallow Home")]
[assembly: AssemblyProduct("Hallow Home")]
[assembly: AssemblyDescription("Your smart-home and pet devices through Home Assistant, the Hallow way")]
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]

namespace HallowHome
{
    public static class Program
    {
        public const string Version = "1.1";
        [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr v);

        [STAThread]
        public static void Main(string[] args)
        {
            int ci = Array.IndexOf(args, "--cmd");
            if (ci >= 0) { Environment.Exit(Family.RunCmd(ci + 1 < args.Length ? args[ci + 1] : "")); return; }
            try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }
            int si = Array.IndexOf(args, "--snap");
            if (si >= 0 && si + 1 < args.Length)
            {
                Snap.On = true; HomeSettings.Frozen = true; SecretStore.UseSnapValues();
                var sa = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                SnapAll(sa, args[si + 1]); return;
            }
            var leaf = HomeSettings.TestDir != null ? "Test" + Path.GetFileName(HomeSettings.TestDir.TrimEnd('\\')) : "";
            bool created;
            var mutex = new Mutex(true, @"Local\HallowHome" + leaf, out created);
            var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\HallowHomeShow" + leaf);
            if (!created) { showEvent.Set(); return; }
            Crash.Install();
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; LoadTheme(app);
            app.DispatcherUnhandledException += (s, e) => { Crash.Write(e.Exception); e.Handled = true; Environment.Exit(1); };
            var c = new Controller(args.Contains("--tray"));
            new Thread(() => { while (showEvent.WaitOne()) app.Dispatcher.BeginInvoke(new Action(() => c.ShowMain(null))); }) { IsBackground = true }.Start();
            app.Run();
            GC.KeepAlive(mutex);
        }
        public static string AccentHex = Accent.Default;
        public static void LoadTheme(Application app, bool? light = null)
        {
            var st = HomeSettings.Load();
            AccentHex = Accent.Current(st.UseHallowAccent);
            Mode.IsLight = light ?? Mode.Resolve(st.Look, Accent.ReadMode(), Mode.Sys);
            app.Resources.MergedDictionaries.Clear();
            app.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(ThemeXaml(Mode.IsLight, AccentHex)));
        }
        public static string ThemeXaml(bool light, string accent)
        {
            var x = Res("HallowHome.Theme.xaml");
            return light ? Accent.ApplyTo(Mode.LightTheme(x, accent), accent) : Accent.ApplyTo(x, Ui.Readable(accent));
        }
        static string Res(string name) { using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)) using (var r = new StreamReader(s)) return r.ReadToEnd(); }

        // --snap <folder>: every page off-screen, in dark and light, with made-up devices (never a real address, token or home)
        static void SnapAll(Application app, string dir)
        {
            foreach (var light in new[] { false, true })
            {
                LoadTheme(app, light);
                var sfx = light ? "-light" : "";
                var c = new Controller(true, true); c.SnapSetup(false);
                var w = new MainWindow(c);
                foreach (var p in MainWindow.Pages) { w.Show(p[0]); Snap.Save(w, Path.Combine(dir, p[0] + sfx + ".png"), 1200, 800); }
                w.ShowCamera("camera.front_door"); Snap.Save(w, Path.Combine(dir, "camera-single" + sfx + ".png"), 1200, 800);
                // trouble: a jam, low water, the pump stopped, low intake
                var c2 = new Controller(true, true); c2.SnapSetup(true);
                var w2 = new MainWindow(c2); w2.Show("dashboard"); Snap.Save(w2, Path.Combine(dir, "dashboard-trouble" + sfx + ".png"), 1200, 800);
                w2.Show("pets"); Snap.Save(w2, Path.Combine(dir, "pets-trouble" + sfx + ".png"), 1200, 800);
                w2.Show("alerts"); Snap.Save(w2, Path.Combine(dir, "alerts-trouble" + sfx + ".png"), 1200, 800);
                // not set up yet
                var c3 = new Controller(true, true); c3.SnapEmpty();
                var w3 = new MainWindow(c3); w3.Show("dashboard"); Snap.Save(w3, Path.Combine(dir, "dashboard-new" + sfx + ".png"), 1200, 800);
                Snap.Save(Dialogs.FeedWindow(c, null), Path.Combine(dir, "dialog-feed" + sfx + ".png"), 460, 0);
                Snap.Save(CrashNotice.Make(Path.Combine(dir, "example.txt")), Path.Combine(dir, "crash" + sfx + ".png"), 440, 0);
            }
        }
    }

    // the app: tray icon, family pipe, Home Assistant, alerts, automations, updates
    public class Controller
    {
        public readonly HomeSettings S = HomeSettings.Load();
        public readonly bool SnapMode;
        readonly Dispatcher ui;
        MainWindow main; WF.NotifyIcon tray; bool hubHosts, hubLists, quitting, balloonOwn;
        readonly List<string> log = new List<string>();
        public Func<DateTime> Now = () => DateTime.Now;
        public ISender Sender;

        // Home Assistant
        Ha ha; public Ha Client { get { return ha; } }
        public readonly Dictionary<string, Entity> Entities = new Dictionary<string, Entity>();
        public bool Connected, Connecting; public bool Live { get { return SnapLive || (ha != null && ha.IsLive); } }
        public string LastError, HaVersion; public DateTime? LastSync;
        // what it all means
        public Mapping Map = new Mapping(); public FeederView Feeder = new FeederView(); public FountainView Fountain = new FountainView(); public LockView Lock = LockInfo.View(new Mapping());
        public List<Alert> Active = new List<Alert>();
        public readonly AlertEngine Engine = new AlertEngine(); public AlertDesk Desk = new AlertDesk(); readonly AutomationEngine autos = new AutomationEngine();
        public History Hist;
        public readonly List<string> Sent = new List<string>();   // the recent deliveries (titles + channels), for the Alerts page
        public string Game; public int Notifications, NotificationsHeld;
        public event Action Changed;
        DispatcherTimer tick; FileSystemWatcher accentWatch; bool? lastLight; bool recomputeSoon;

        public Controller(bool trayOnly, bool snapMode = false)
        {
            SnapMode = snapMode; ui = Dispatcher.CurrentDispatcher;
            Sender = snapMode || HomeSettings.TestDir != null ? (ISender)new FakeSender() : new RealSender();
            Hist = snapMode ? new History() : History.Load(Path.Combine(HomeSettings.Dir, "history.json"));
            if (!snapMode) Desk = AlertDesk.Load(Path.Combine(HomeSettings.Dir, "alerts.json"));
            if (snapMode) return;
            SecretStore.Get(SecretStore.Token);   // loads the secrets, so a crash report can blank them out (Crash.Secrets)
            Family.Serve("Home", Command);
            tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) }; tick.Tick += (s, e) => Tick(); tick.Start();
            if (!HomeSettings.Headless)
            {
                SetupTray();
                var ht = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) }; ht.Tick += (s, e) => CheckHost(); ht.Start(); CheckHost();
                accentWatch = Accent.Watch(() => ui.BeginInvoke(new Action(ApplyAccent)));
                if (HomeSettings.TestDir == null || Environment.GetEnvironmentVariable("HALLOW_HOME_API") != null)
                {
                    var upd = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
                    upd.Tick += (s, e) => { upd.Interval = TimeSpan.FromHours(6); DateTime last; if (!DateTime.TryParse(S.LastUpdateCheck, out last) || (DateTime.Now - last).TotalHours >= 24) CheckForUpdate(false); };
                    upd.Start();
                }
                ui.BeginInvoke(new Action(ShowCrashNotice), DispatcherPriority.ApplicationIdle);
            }
            WriteStatus();
            Connect();
            if (!trayOnly && !HomeSettings.Headless) ShowMain(null);
        }
        public void Log(string s) { lock (log) { log.Add(DateTime.Now.ToString("HH:mm:ss") + " " + s); if (log.Count > 200) log.RemoveAt(0); } }
        public string[] LogLines() { lock (log) return log.ToArray(); }
        void Fire() { WriteStatus(); var h = Changed; if (h != null) h(); }
        bool snapUnset; public bool SnapLive;
        public bool Configured { get { return !snapUnset && S.HaUrl != null && SecretStore.Has(SecretStore.Token); } }
        public bool Away { get { return S.Away || Map.EveryoneAway == true; } }
        public DateTime? SnoozeUntil { get { DateTime d; return DateTime.TryParse(S.SnoozeUntil, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out d) && d > Now() ? d : (DateTime?)null; } }

        // ---------------- Home Assistant: connect, initial states, live updates
        public void Connect()
        {
            if (SnapMode) return;
            if (ha != null) { ha.StopEvents(); ha = null; }
            Connected = false; LastError = null; HaVersion = null;
            if (!Configured) { Connecting = false; Fire(); return; }
            var h = new Ha(S.HaUrl, SecretStore.Get(SecretStore.Token)); ha = h; Connecting = true; Fire();
            Log("connecting to Home Assistant");
            new Thread(() =>
            {
                string err = null; List<Entity> states = null; string ver = null;
                try { h.Check(); states = h.States(); ver = h.Version(); } catch (Exception e) { err = e.Message; }
                ui.BeginInvoke(new Action(() =>
                {
                    if (ha != h) return;
                    Connecting = false;
                    if (err != null) { LastError = err; Connected = false; Log("connect failed: " + err); retryAt = Now().AddSeconds(30); Fire(); return; }
                    Connected = true; HaVersion = ver; LastSync = Now(); lastRefresh = DateTime.Now; Log("connected: " + states.Count + " entities");
                    Entities.Clear(); foreach (var e in states) Entities[e.Id] = e;
                    h.StateChanged += (e, id) => ui.BeginInvoke(new Action(() => { if (ha == h) ApplyState(e, id); }));
                    h.Live += (on, why) => ui.BeginInvoke(new Action(() => { if (ha != h) return; Log(on ? "live updates on" : "live updates off" + (why != null ? ": " + why : "")); Fire(); }));
                    h.StartEvents();
                    Recompute();
                }));
            }) { IsBackground = true, Name = "ha connect" }.Start();
        }
        DateTime? retryAt; DateTime lastRefresh = DateTime.MinValue;
        void ApplyState(Entity e, string id)
        {
            if (e == null) Entities.Remove(id); else Entities[id] = e;
            LastSync = Now(); recomputeSoon = true;
        }
        // a full refresh (every 10 minutes, or every minute while live updates are down)
        void Refresh()
        {
            var h = ha; if (h == null || Connecting) return;
            lastRefresh = DateTime.Now;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                List<Entity> states = null; string err = null;
                try { states = h.States(); } catch (Exception e) { err = e.Message; }
                ui.BeginInvoke(new Action(() =>
                {
                    if (ha != h) return;
                    if (err != null) { if (Connected) Log("refresh failed: " + err); Connected = false; LastError = err; Fire(); return; }
                    Connected = true; LastError = null; LastSync = Now();
                    Entities.Clear(); foreach (var e in states) Entities[e.Id] = e;
                    Recompute();
                }));
            });
        }

        // ---------------- what it means: mapping, pet views, alerts, automations
        public void Recompute() { Recompute(true); }
        string lastPrint;
        void Recompute(bool force)
        {
            recomputeSoon = false;
            var now = Now();
            Map = Roles.Build(Entities.Values, S);
            Feeder = FeederView.From(Map, S, now);
            var intake = Map.Get("fountain.intake"); if (intake != null && intake.Num != null && !SnapMode) Hist.Record(now, intake.Num.Value);
            Fountain = FountainView.From(Map, S, Hist, now);
            Lock = LockInfo.View(Map);
            Active = Connected || SnapMode ? Engine.Evaluate(now, Map, Feeder, Fountain, S, Away) : new List<Alert>();
            if (!SnapMode)
            {
                foreach (var d in Desk.Process(Active, now, S, Game != null, SnoozeUntil)) Deliver(d.Alert.Title, d.Alert.Text, d.Pc, d.Phone, d.Alert.Bad);
                foreach (var a in autos.Evaluate(now, S.Automations, Active, Entities, Away)) RunAutomation(a);
            }
            var print = string.Join("|", Map.ByRole.Select(kv => kv.Key + "=" + kv.Value.State)) + "#" + string.Join("|", Map.Cameras.Select(x => x.Id + "=" + x.State)) + "#" + string.Join("|", Active.Select(x => x.Key)) + "#" + Connected + Live + Game + LastError + Away + Entities.Count;
            if (force || print != lastPrint) Fire();
            lastPrint = print;
        }
        public bool IntakeLow { get { return Active.Any(a => a.Rule == "intake-low"); } }
        void RunAutomation(AutoAction a)
        {
            Log("automation: " + a.Why);
            if (a.Kind == "notify")
            {
                var t = a.About != null ? "Reminder: " + a.About.Title : "Hallow Home reminder";
                var x = a.About != null ? a.About.Text + " (Still true - reminding you as set on the Automations page.)" : a.Why;
                if (S.QuietOn && Quiet.IsQuiet(Now(), S.QuietStart, S.QuietEnd)) { Log("reminder held (quiet hours)"); return; }
                if (Game != null && S.GameHoldAll) { Log("reminder held (game)"); return; }
                Deliver(t, x, Game == null, true, false);
            }
            else if (a.Kind == "service")
            {
                var h = ha; if (h == null) return;
                foreach (var ent in a.Entities) { var e2 = ent; ThreadPool.QueueUserWorkItem(_ => { try { h.Call(a.Domain, a.Service, new Dictionary<string, object> { { "entity_id", e2 } }); } catch (Exception e) { ui.BeginInvoke(new Action(() => Log("automation failed: " + e.Message))); } }); }
            }
        }
        // one alert out: the PC (Hub notify / own balloon) and the phone (Discord, ntfy) as switched on in Settings
        public void Deliver(string title, string text, bool pc, bool phone, bool urgent)
        {
            var ch = new List<string>();
            if (pc && S.UseHub) { Notify(title, text); ch.Add("PC"); }
            else if (!pc) NotificationsHeld++;
            if (phone && S.UseDiscord && (SecretStore.Has(SecretStore.Discord) || HomeSettings.TestDir != null)) { ch.Add("Discord"); var sd = Sender; ThreadPool.QueueUserWorkItem(_ => { var e = sd.Discord(title, text); if (e != null) ui.BeginInvoke(new Action(() => Log("send failed: " + e))); }); }
            if (phone && S.UseNtfy && (SecretStore.Has(SecretStore.Ntfy) || HomeSettings.TestDir != null)) { ch.Add("ntfy"); var sn = Sender; ThreadPool.QueueUserWorkItem(_ => { var e = sn.Ntfy(title, text, urgent); if (e != null) ui.BeginInvoke(new Action(() => Log("send failed: " + e))); }); }
            lock (Sent) { Sent.Insert(0, Now().ToString("ddd HH:mm") + "  " + title + "  → " + (ch.Count == 0 ? "nowhere (no channel on)" : string.Join(", ", ch))); if (Sent.Count > 50) Sent.RemoveAt(Sent.Count - 1); }
            Log("alert: " + title + " -> " + string.Join(",", ch));
        }

        // ---------------- Feed now: only ever after a confirm (the window's dialog, or the pipe's "confirm" word)
        public string FeedCheck(int portions)
        {
            if (!Connected && !SnapMode) return "Hallow Home isn't connected to Home Assistant.";
            var btn = Map.Get("feeder.feed");
            if (btn == null) return "No \"Feed now\" button was found for the feeder. Map it on the Devices page (the Petlibro integration's manual feed button).";
            if (!btn.Available) return "The feeder's Feed now button is unavailable right now (is the feeder online?).";
            if (portions < 1 || portions > S.MaxPortions) return "Pick 1 to " + S.MaxPortions + " portions.";
            if (portions > 1 && Map.Get("feeder.portion") == null) return "Only 1 portion: no portion setting was found for the feeder (map \"Portions for Feed now\" on the Devices page).";
            int lo = MinFeedPortions, hi = MaxFeedPortions;
            if (portions < lo || portions > hi) return "Pick " + lo + " to " + hi + " portions (the feeder's own limit).";
            return null;
        }
        // Feed now's range, for the dialog, the pipe and status.json (so Remote's phone page offers the same choice):
        // 1 .. Settings' "at most" limit, narrowed to what the feeder's own portion entity accepts (a number's min / max, a select's
        // numeric options). Without a portion entity it's 1 .. 1 - the feeder's button alone dispenses one portion.
        public int MaxFeedPortions { get { var r = PortionRange(Map.Get("feeder.portion"), S.MaxPortions); return r[1]; } }
        public int MinFeedPortions { get { var r = PortionRange(Map.Get("feeder.portion"), S.MaxPortions); return r[0]; } }
        public static int[] PortionRange(Entity por, int settingsMax)
        {
            int hi = Math.Max(1, settingsMax), lo = 1;
            if (por == null) return new[] { 1, 1 };
            double d;
            if (por.Domain == "number")
            {
                if (double.TryParse(por.AttrS("max"), NumberStyles.Float, CultureInfo.InvariantCulture, out d) && d >= 1) hi = Math.Min(hi, (int)Math.Floor(d));
                if (double.TryParse(por.AttrS("min"), NumberStyles.Float, CultureInfo.InvariantCulture, out d) && d > 1) lo = (int)Math.Ceiling(d);
            }
            else if (por.Domain == "select")
            {
                object o; var nums = new List<int>();
                if (por.Attr.TryGetValue("options", out o) && o is System.Collections.IEnumerable && !(o is string))
                    foreach (var x in (System.Collections.IEnumerable)o) { int n; if (x != null && int.TryParse(Convert.ToString(x, CultureInfo.InvariantCulture).Trim(), out n) && n >= 1) nums.Add(n); }
                if (nums.Count > 0) { lo = nums.Min(); var ok = nums.Where(n => n <= hi).ToList(); hi = ok.Count > 0 ? ok.Max() : lo; }
            }
            if (lo > hi) lo = hi;
            return new[] { lo, hi };
        }
        public string Feed(int portions, bool confirmed, Action<string> done)
        {
            var err = FeedCheck(portions); if (err != null) return err;
            if (!confirmed) return "Feeding needs a confirm.";
            var btn = Map.Get("feeder.feed"); var por = Map.Get("feeder.portion"); var h = ha;
            Log("feed now: " + portions + " portion(s), confirmed");
            new Thread(() =>
            {
                string e = null;
                try
                {
                    if (por != null)
                    {
                        if (por.Domain == "number") h.Call("number", "set_value", new Dictionary<string, object> { { "entity_id", por.Id }, { "value", portions } });
                        else h.Call("select", "select_option", new Dictionary<string, object> { { "entity_id", por.Id }, { "option", portions.ToString() } });
                    }
                    h.Call("button", "press", new Dictionary<string, object> { { "entity_id", btn.Id } });
                }
                catch (Exception x) { e = x.Message; }
                ui.BeginInvoke(new Action(() => { Log("feed now: " + (e ?? "sent")); if (done != null) done(e); }));
            }) { IsBackground = true, Name = "feed" }.Start();
            return null;
        }
        public string LockAction(bool lockIt)
        {
            if (Lock.Kind != "ha" || !Lock.CanControl || ha == null) return "The lock can't be controlled from here.";
            var id = Lock.Entity.Id; var h = ha;
            ThreadPool.QueueUserWorkItem(_ => { try { h.Call("lock", lockIt ? "lock" : "unlock", new Dictionary<string, object> { { "entity_id", id } }); } catch (Exception e) { ui.BeginInvoke(new Action(() => Say(e.Message))); } });
            return null;
        }

        // ---------------- once a second: recompute after changes, alerts every 15 s, refreshes, the game, the theme
        int ticks; bool gameChecking;
        void Tick()
        {
            if (quitting) return;
            ticks++;
            if (recomputeSoon) Recompute(false);
            else if (ticks % 15 == 0 && (Connected || Entities.Count > 0)) Recompute(false);
            if (retryAt != null && Now() >= retryAt.Value && !Connecting) { retryAt = null; Connect(); }
            if (Connected && !Connecting && (DateTime.Now - lastRefresh).TotalSeconds >= (Live ? 600 : 60)) Refresh();
            if (ticks % 5 == 0 && !gameChecking && HomeSettings.TestDir == null) CheckGame();
            if (ticks % 5 == 0 && !HomeSettings.Headless) { bool light = Mode.Resolve(S.Look, Accent.ReadMode(), Mode.Sys); if (lastLight != null && lastLight != light) ApplyAccent(); lastLight = light; }
            if (ticks % 60 == 0) WriteStatus();
        }
        void CheckGame()
        {
            gameChecking = true;
            ThreadPool.QueueUserWorkItem(_ => { string g = null; try { g = Games.Running(); } catch { } ui.BeginInvoke(new Action(() => { gameChecking = false; SetGame(g); })); });
        }
        public void SetGame(string g)
        {
            if (g == Game) return;
            Log(g != null ? "game: " + g : "game closed"); Game = g;
            if (main != null) main.GameChanged();
            Recompute();   // held alerts go out after the game (if still true)
        }

        // ---------------- window
        public void ShowMain(string page)
        {
            if (HomeSettings.Headless || SnapMode) return;
            if (main == null) main = new MainWindow(this);
            if (page != null) main.Show(page);
            main.Show(); if (main.WindowState == WindowState.Minimized) main.WindowState = WindowState.Normal; main.Activate();
        }
        public void MainClosed()
        {
            main = null;
            if (!S.TrayHintShown && !quitting) { S.TrayHintShown = true; S.Save(); Notify("Hallow Home is still here", "It keeps watching your pets' devices and cameras from the tray. Right-click the icon to quit."); }
        }
        public void Detach() { quitting = true; if (ha != null) ha.StopEvents(); if (tick != null) tick.Stop(); }   // tests: stop without ending the process
        public void Quit()
        {
            if (quitting) return; quitting = true;
            if (ha != null) ha.StopEvents();
            if (tray != null) { tray.Visible = false; tray.Dispose(); }
            WriteStatus(false);
            if (main != null) main.Exiting = true;
            Application.Current.Shutdown();
        }
        public void Say(string text) { if (main != null) main.Say(text); }

        // ---------------- settings
        const string RunName = "HallowHome";
        string RunFile { get { return Path.Combine(HomeSettings.Dir, "test-autostart.txt"); } }
        public bool StartsWithWindows()
        {
            if (HomeSettings.TestDir != null || SnapMode) return SnapMode || File.Exists(RunFile);
            try { using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")) return k != null && k.GetValue(RunName) != null; } catch { return false; }
        }
        public void SetStartWithWindows(bool on)
        {
            if (SnapMode) return;
            if (HomeSettings.TestDir != null) { if (on) File.WriteAllText(RunFile, "\"HallowHome.exe\" --tray"); else if (File.Exists(RunFile)) File.Delete(RunFile); return; }
            try { using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")) { if (on) k.SetValue(RunName, "\"" + Assembly.GetExecutingAssembly().Location + "\" --tray"); else k.DeleteValue(RunName, false); } } catch { }
        }
        // the connection settings from the Settings page; returns an error or null (and reconnects)
        public string SetConnection(string url, string token)
        {
            var u = Ha.NormalizeUrl(url);
            if (u == null) return "That isn't an address like http://nas:8123 or https://home.example.com.";
            if (!string.IsNullOrEmpty(token)) { var e = SecretStore.Set(SecretStore.Token, token); if (e != null) return e; }
            if (!SecretStore.Has(SecretStore.Token)) return "Paste a long-lived access token too (Home Assistant: your profile > Security > Long-lived access tokens).";
            S.HaUrl = u; S.Save(); Log("connection settings changed"); Connect(); return null;
        }
        public void SetOverride(string role, string entity) { S.SetOverride(role, entity); S.Save(); Log("mapping: " + role + " = " + (entity == null ? "automatic" : entity == "" ? "none" : entity)); Recompute(); }
        public void SetAway(bool on) { S.Away = on; S.Save(); Log(on ? "away" : "home"); Recompute(); }
        public void Snooze(bool on) { S.SnoozeUntil = on ? Now().AddHours(1).ToString("o") : null; S.Save(); Recompute(); }

        // ---------------- tray, Hub hosting, notifications, accent, updates, crash notice
        void SetupTray()
        {
            tray = new WF.NotifyIcon { Text = "Hallow Home", Visible = true };
            try { tray.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location); } catch { tray.Icon = System.Drawing.SystemIcons.Application; }
            var menu = new WF.ContextMenuStrip();
            menu.Renderer = new WF.ToolStripProfessionalRenderer(TrayColors.For(Mode.IsLight));
            menu.Opening += (s, e) => BuildTrayMenu(menu);
            BuildTrayMenu(menu);
            tray.ContextMenuStrip = menu;
            tray.MouseClick += (s, e) => { if (e.Button == WF.MouseButtons.Left) ShowMain(null); };
        }
        void BuildTrayMenu(WF.ContextMenuStrip menu)
        {
            menu.Items.Clear();
            var tc = TrayColors.For(Mode.IsLight); menu.Renderer = new WF.ToolStripProfessionalRenderer(tc); menu.BackColor = tc.Back; menu.ForeColor = tc.Fore;
            var open = new WF.ToolStripMenuItem("Open Hallow Home"); open.Font = new System.Drawing.Font(open.Font, System.Drawing.FontStyle.Bold); open.Click += (s, e) => ShowMain("dashboard"); menu.Items.Add(open);
            foreach (var it in MenuItems().Cast<Dictionary<string, object>>())
            {
                var id = (string)it["id"]; if (id == "-") { menu.Items.Add(new WF.ToolStripSeparator()); continue; }
                var mi = new WF.ToolStripMenuItem((string)it["label"]); object en; if (it.TryGetValue("enabled", out en) && en is bool) mi.Enabled = (bool)en;
                object ck; if (it.TryGetValue("checked", out ck) && ck is bool) mi.Checked = (bool)ck;
                mi.Click += (s, e) => Invoke(id); menu.Items.Add(mi);
            }
            menu.Items.Add(new WF.ToolStripSeparator());
            var quit = new WF.ToolStripMenuItem("Quit (alerts stop until it runs again)"); quit.Click += (s, e) => Quit(); menu.Items.Add(quit);
            foreach (WF.ToolStripItem i in menu.Items) i.ForeColor = tc.Fore;
        }
        public void CheckHost()
        {
            if (tray == null) return;
            if (!S.UseHallowIcon) { SetHosted(false, false); return; }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                bool on = false, lists = false;
                try { var r = Family.Send("Hub", "tray-host", 1500); if (r != null && r.StartsWith("ok {")) HubHosting(r.Substring(3), out on, out lists); } catch { }
                ui.BeginInvoke(new Action(() => SetHosted(on && S.UseHallowIcon, lists)));
            });
        }
        // Hub's "tray-host" answer: on = Hub's Hallow icon is showing; lists = Hub's menu has Hallow Home ("apps" has "Home", or
        // "generic":true - Hub 2.13+ lists any app answering "menu"). Hallow Home only hides its own icon when Hub lists it.
        public static void HubHosting(string json, out bool on, out bool lists)
        {
            on = false; lists = false;
            try
            {
                var d = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json); object v;
                on = d != null && d.TryGetValue("on", out v) && v is bool && (bool)v;
                if (!on) return;
                if (d.TryGetValue("generic", out v) && v is bool && (bool)v) { lists = true; return; }
                if (d.TryGetValue("apps", out v) && v is System.Collections.IEnumerable && !(v is string))
                    foreach (var a in (System.Collections.IEnumerable)v) if (a is string && string.Equals((string)a, "Home", StringComparison.OrdinalIgnoreCase)) lists = true;
            }
            catch { on = false; lists = false; }
        }
        void SetHosted(bool on, bool lists) { hubHosts = on; hubLists = on && lists; if (tray != null && !balloonOwn) tray.Visible = !hubLists; }
        // the PC notification: never on screen while a game runs (held ones are only logged)
        public void Notify(string title, string text)
        {
            if (Game != null) { NotificationsHeld++; Log("held (game running): " + title); return; }
            Notifications++; Log("notify: " + title);
            if (tray == null || HomeSettings.TestDir != null) return;
            if (!hubHosts) { tray.ShowBalloonTip(6000, title, text, WF.ToolTipIcon.None); return; }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                if (Family.Error(Family.Send("Hub", "notify Home\t" + title + "\t" + text, 2000), "no answer") == null) return;
                ui.BeginInvoke(new Action(() => { balloonOwn = true; tray.Visible = true; tray.ShowBalloonTip(6000, title, text, WF.ToolTipIcon.None); var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) }; t.Tick += (s, e) => { t.Stop(); balloonOwn = false; SetHosted(hubHosts, hubLists); }; t.Start(); }));
            });
        }
        public void ApplyAccent()
        {
            var a = Accent.Current(S.UseHallowAccent); bool light = Mode.Resolve(S.Look, Accent.ReadMode(), Mode.Sys);
            if (a == Program.AccentHex && light == Mode.IsLight) return;
            Program.LoadTheme(Application.Current);
            if (main != null) main.Rebuild();
        }
        GhRelease pending; public GhRelease PendingUpdate { get { return pending; } }
        public void CheckForUpdate(bool manual)
        {
            if (manual) Say("Checking GitHub for a newer Hallow Home...");
            new Thread(() =>
            {
                var r = Updater.Latest();
                ui.BeginInvoke(new Action(() =>
                {
                    S.LastUpdateCheck = DateTime.Now.ToString("o"); S.Save();
                    if (r == null) { if (manual) Say("Couldn't reach GitHub - try again later."); return; }
                    if (!Updater.IsNewer(r)) { if (manual) Say("You have the newest version (v" + Program.Version + ")."); return; }
                    pending = r; if (main != null) main.ShowUpdate("Update to v" + r.Version.ToString(2));
                    Say("Hallow Home v" + r.Version.ToString(2) + " is out - click Update at the bottom left.");
                }));
            }) { IsBackground = true }.Start();
        }
        public void InstallUpdate()
        {
            if (pending == null) return;
            if (!Dialogs.Confirm(main, "Install Hallow Home v" + pending.Version.ToString(2) + "?", "It downloads from GitHub, checks the file and puts the new version in place. Your settings, token and alert history stay.", "Update", false)) return;
            var r = pending; Say("Updating...");
            new Thread(() => { var err = Updater.Install(r); ui.BeginInvoke(new Action(() => Say(err ?? "Installing the update..."))); }) { IsBackground = true }.Start();
        }
        void ShowCrashNotice()
        {
            var f = Crash.Last(); if (f == null) return;
            var name = Path.GetFileName(f); if (name == S.LastCrashShown) return;
            if (Game != null) return;   // never during a game; it's shown next time
            S.LastCrashShown = name; S.Save();
            CrashNotice.Make(f).Show();
        }

        // ---------------- family link: status.json and the pipe. Counts and alert flags only - never camera URLs, addresses or tokens.
        public Dictionary<string, object> StatusData(bool running)
        {
            var d = new Dictionary<string, object> { { "app", "Home" }, { "version", Program.Version }, { "updated", Family.Time(DateTime.Now) }, { "running", running },
                { "configured", Configured }, { "connected", Connected }, { "live", Live }, { "entities", Entities.Count },
                { "cameras", Map.Cameras.Count }, { "camerasOnline", Map.Cameras.Count(c => c.Available) } };
            d["feeder"] = !Feeder.Present ? null : new Dictionary<string, object> { { "online", Feeder.Online }, { "nextFeed", Feeder.Next == null ? null : Family.Time(Feeder.Next.Value) },
                { "todayMeals", Feeder.TodayMeals }, { "todayGrams", Feeder.TodayGrams }, { "foodLow", Feeder.FoodLow }, { "canFeed", Feeder.CanFeed },
                // 1.1: the Feed now range, so the phone offers the same choice as the PC's dialog
                { "minPortions", MinFeedPortions }, { "maxPortions", MaxFeedPortions }, { "gramsPerPortion", S.GramsPerPortion }, { "portionSetting", Feeder.PortionEntity } };
            d["fountain"] = !Fountain.Present ? null : new Dictionary<string, object> { { "online", Fountain.Online }, { "waterLow", Fountain.WaterLow }, { "waterPct", Fountain.WaterPct },
                { "pumpOn", Fountain.PumpOn }, { "filterDue", Fountain.FilterDue }, { "cleaningDue", Fountain.CleaningDue }, { "intakeToday", Fountain.IntakeToday },
                { "intakeAvg7", Fountain.IntakeAvg == null ? null : (object)Math.Round(Fountain.IntakeAvg.Value, 2) }, { "unit", Fountain.Unit } };
            d["lock"] = Lock.Short;
            d["alerts"] = Active.Count; d["alertFlags"] = Active.Select(a => a.Rule).Distinct().ToArray();
            d["away"] = Away; d["quiet"] = S.QuietOn && Quiet.IsQuiet(Now(), S.QuietStart, S.QuietEnd); d["snoozed"] = SnoozeUntil != null; d["game"] = Game;
            d["lastCrash"] = Crash.LastTime() == null ? null : Family.Time(Crash.LastTime().Value);
            if (HomeSettings.TestDir != null) { lock (log) d["log"] = log.ToArray(); d["notifications"] = Notifications; d["held"] = NotificationsHeld; }
            return d;
        }
        public void WriteStatus(bool running = true) { if (!SnapMode && !(quitting && running)) Family.WriteStatus(Family.Json(StatusData(running))); }
        string Command(string line)
        {
            int sp = line.IndexOf(' '); var cmd = sp < 0 ? line : line.Substring(0, sp); var arg = sp < 0 ? "" : line.Substring(sp + 1).Trim();
            switch (cmd)
            {
                case "ping": return "ok " + Family.Json(new Dictionary<string, object> { { "app", "Home" }, { "version", Program.Version } });
                case "status": return OnUi(() => { var d = StatusData(true); Family.WriteStatus(Family.Json(d)); return "ok " + Family.Json(d); });
                case "feed":
                    return OnUi(() =>
                    {
                        // "feed <portions> confirm": the word confirm is required - without it nothing moves
                        var parts = arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries); int n;
                        if (parts.Length == 0 || !int.TryParse(parts[0], out n)) return "error use: feed <portions> confirm";
                        var err = FeedCheck(n); if (err != null) return "error " + err;
                        if (parts.Length < 2 || parts[1] != "confirm") return "error not fed: add \"confirm\" (feed " + n + " confirm) to dispense " + n + " portion" + (n == 1 ? "" : "s") + " (about " + Read.Num(n * S.GramsPerPortion) + " g)";
                        var e = Feed(n, true, null); return e == null ? "ok feeding " + n : "error " + e;
                    });
                case "away": return OnUi(() => { if (arg != "on" && arg != "off") return "error use: away on|off"; SetAway(arg == "on"); return "ok " + (Away ? "away" : "home"); });
                case "open": return OnUi(() => { var p = arg.Length > 0 ? MainWindow.PageId(arg) : null; if (arg.Length > 0 && p == null) return "error no page called " + arg; ShowMain(p); return "ok"; });
                case "menu": return OnUi(() => "ok " + Family.Json(new Dictionary<string, object> { { "app", "Home" }, { "items", MenuItems() } }));
                case "invoke": return OnUi(() => Invoke(arg));
                case "quit": new Thread(() => { Thread.Sleep(300); ui.BeginInvoke(new Action(Quit)); }) { IsBackground = true }.Start(); return "ok";
            }
            if (HomeSettings.TestDir != null && cmd.StartsWith("test-")) return OnUi(() => TestCommand(cmd, arg));
            return "error unknown command";
        }
        string OnUi(Func<string> f)
        {
            string r = "error no answer in time";
            var op = ui.BeginInvoke(new Action(() => { try { r = f(); } catch (Exception e) { r = "error " + Family.OneLine(e.Message); } }));
            return op.Wait(TimeSpan.FromSeconds(5)) == DispatcherOperationStatus.Completed ? r : "error no answer in time";
        }
        public List<object> MenuItems()
        {
            var l = new List<object> {
                new Dictionary<string, object> { { "id", "feed" }, { "label", "Feed now…" }, { "enabled", Feeder.CanFeed } },
                new Dictionary<string, object> { { "id", "pets" }, { "label", "Pets" } },
                new Dictionary<string, object> { { "id", "cameras" }, { "label", "Cameras" + (Map.Cameras.Count > 0 ? " (" + Map.Cameras.Count(c => c.Available) + "/" + Map.Cameras.Count + ")" : "") } },
                new Dictionary<string, object> { { "id", "alerts" }, { "label", Active.Count == 0 ? "Alerts: all fine" : "Alerts: " + Active.Count } },
                new Dictionary<string, object> { { "id", "-" } },
                new Dictionary<string, object> { { "id", "away" }, { "label", "I'm away" }, { "checked", S.Away } },
                new Dictionary<string, object> { { "id", "snooze" }, { "label", "Pause alerts for 1 hour" }, { "checked", SnoozeUntil != null } } };
            return l;
        }
        string Invoke(string id)
        {
            switch (id)
            {
                case "feed": ShowMain("pets"); if (main != null) main.AskFeed(); return "ok";   // always the confirm dialog - the menu never feeds by itself
                case "pets": ShowMain("pets"); return "ok";
                case "cameras": ShowMain("cameras"); return "ok";
                case "alerts": ShowMain("alerts"); return "ok";
                case "away": SetAway(!S.Away); return "ok";
                case "snooze": Snooze(SnoozeUntil == null); return "ok";
            }
            return "error unknown item";
        }
        // developer tests only (HALLOW_HOME_TESTDIR): what a person would click, without windows
        string TestCommand(string cmd, string arg)
        {
            switch (cmd)
            {
                case "test-autostart": SetStartWithWindows(arg == "on"); return "ok " + StartsWithWindows();
                case "test-connect": { var p = arg.Split(new[] { ' ' }, 2); var e = SetConnection(p[0], p.Length > 1 ? p[1] : null); return e == null ? "ok" : "error " + e; }
                case "test-secret": { var p = arg.Split(new[] { ' ' }, 2); var e = SecretStore.Set(p[0], p.Length > 1 ? p[1] : ""); return e == null ? "ok" : "error " + e; }
                case "test-channels": { var p = arg.Split(' '); S.UseHub = p.Contains("hub"); S.UseDiscord = p.Contains("discord"); S.UseNtfy = p.Contains("ntfy"); S.Save(); return "ok"; }
                case "test-override": { var p = arg.Split(' '); SetOverride(p[0], p.Length < 2 || p[1] == "auto" ? null : p[1] == "none" ? "" : p[1]); var e = Map.Get(p[0]); return "ok " + (e == null ? "none" : e.Id); }
                case "test-map": return "ok " + Family.Json(Map.ByRole.ToDictionary(kv => kv.Key, kv => kv.Value.Id));
                case "test-clock": { var add = double.Parse(arg, System.Globalization.CultureInfo.InvariantCulture); Now = () => DateTime.Now.AddMinutes(add); return "ok " + Now().ToString("HH:mm"); }
                case "test-recompute": Recompute(); return "ok " + Family.Json(Active.Select(a => a.Key).ToArray());
                case "test-sent": lock (((FakeSender)Sender).Sent) return "ok " + Family.Json(((FakeSender)Sender).Sent);
                case "test-desk": return "ok " + Family.Json(Desk.Sent.Keys.ToArray());
                case "test-quiet": { var p = arg.Split(' '); if (p[0] == "off") S.QuietOn = false; else { S.QuietOn = true; S.QuietStart = p[0]; S.QuietEnd = p[1]; } S.Save(); return "ok"; }
                case "test-game": SetGame(arg == "none" ? null : arg); return "ok";
                case "test-gamehold": S.GameHoldAll = arg == "all"; S.Save(); return "ok";
                case "test-automation": { var p = arg.Split(' '); var r = new AutoRule { Id = "t" + S.Automations.Count, On = true, When = p[0], Then = p[1], RepeatHours = p.Length > 2 ? double.Parse(p[2], System.Globalization.CultureInfo.InvariantCulture) : 0 }; var e = AutomationEngine.Check(r); if (e != null) return "error " + e; S.Automations.Add(r); S.Save(); return "ok"; }
                case "test-feeder": return "ok " + Family.Json(new Dictionary<string, object> { { "next", Feeder.Next == null ? null : Feeder.Next.Value.ToString("HH:mm") }, { "last", Feeder.Last == null ? null : Feeder.Last.Value.ToString("HH:mm") }, { "meals", Feeder.TodayMeals } });
            }
            return "error unknown test command";
        }

        // ---------------- --snap: made-up state
        public void SnapSetup(bool trouble)
        {
            var now = DateTime.Now.Date.AddHours(16).AddMinutes(20); Now = () => now;
            Connected = true; SnapLive = true; HaVersion = "2026.11.2"; LastSync = now.AddSeconds(-4); S.HaUrl = "http://nas:8123";
            S.UseDiscord = true; S.UseNtfy = true; S.RecordingsFolder = null; S.LiveBase = "http://nas:8889";
            Hist = FakeData.Week(now);
            foreach (var e in FakeData.Entities(now)) Entities[e.Id] = e;
            if (trouble)
            {
                Set("sensor.granary_smart_camera_feeder_next_feeding_time", now.Date.AddHours(16).ToUniversalTime().ToString("o"), now.AddHours(-4));
                Set("sensor.granary_smart_camera_feeder_last_feed_time", now.Date.AddHours(12).AddSeconds(12).ToUniversalTime().ToString("o"), now.AddHours(-4));
                Set("binary_sensor.granary_smart_camera_feeder_food_low", "on", now.AddHours(-1));
                Set("sensor.dockstream_2_smart_fountain_water_level", "12", now.AddMinutes(-50));
                Set("sensor.dockstream_2_smart_fountain_pump_status", "stopped", now.AddMinutes(-48));
                Set("sensor.dockstream_2_smart_fountain_today_s_water_intake", "1.10", now.AddMinutes(-60));
                Recompute();   // the 16:00 meal goes into the watch list...
                Set("sensor.granary_smart_camera_feeder_next_feeding_time", now.Date.AddHours(18).ToUniversalTime().ToString("o"), now.AddMinutes(-19));
                Engine.Evaluate(now.AddMinutes(-21), Map, new FeederView { Present = true, Next = now.Date.AddHours(16), TodayMeals = 2 }, Fountain, S, false);
                lock (Sent) { Sent.Add(now.AddMinutes(-9).ToString("ddd HH:mm") + "  No meal at 16:00  → PC, Discord, ntfy"); Sent.Add(now.AddMinutes(-30).ToString("ddd HH:mm") + "  Fountain water low  → PC, Discord, ntfy"); }
            }
            Recompute();
        }
        void Set(string id, string state, DateTime changed) { Entity e; if (Entities.TryGetValue(id, out e)) { e.State = state; e.Changed = changed; } }
        public void SnapEmpty() { var now = DateTime.Now.Date.AddHours(16).AddMinutes(20); Now = () => now; S.HaUrl = null; snapUnset = true; Recompute(); }
    }

    // the family game list (DeltaForceClient-Win64-Shipping.exe, Discovery-d.exe) plus Hallow Hub's status.json "playing"
    public static class Games
    {
        public static readonly string[][] Known = { new[] { "DeltaForceClient-Win64-Shipping", "Delta Force" }, new[] { "Discovery-d", "Discovery" } };
        public static string Running()
        {
            foreach (var k in Known)
            {
                Process[] ps = null;
                try { ps = Process.GetProcessesByName(k[0]); if (ps.Length > 0) return k[1]; }
                catch { }
                finally { if (ps != null) foreach (var p in ps) p.Dispose(); }
            }
            try
            {
                var f = Path.Combine(Family.DataDir("Hub"), "status.json"); if (!File.Exists(f)) return null;
                var d = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(f)); object v;
                if (d == null || !d.TryGetValue("running", out v) || !(v is bool) || !(bool)v) return null;
                if (d.TryGetValue("playing", out v) && v is System.Collections.IEnumerable && !(v is string))
                    foreach (var x in (System.Collections.IEnumerable)v)
                    {
                        if (x is string) return (string)x;
                        var o = x as Dictionary<string, object>; object n; if (o != null && o.TryGetValue("name", out n) && n is string) return (string)n;
                    }
            }
            catch { }
            return null;
        }
    }
}
