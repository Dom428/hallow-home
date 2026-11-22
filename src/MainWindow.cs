// Hallow Home main window: the page list on the left (Dashboard, Pets, Cameras, Lock, Alerts, Automations, Devices, Settings), the
// open page, and a status line. Pages are rebuilt when the Controller says something changed (camera tiles keep streaming).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace HallowHome
{
    public partial class MainWindow : Window
    {
        readonly Controller c;
        string page = "dashboard", camera;
        readonly Dictionary<string, Button> nav = new Dictionary<string, Button>();
        Border content; TextBlock sayText, meter; Button updateBtn; DispatcherTimer sayTimer, rebuildTimer;
        public bool Exiting;
        public static readonly string[][] Pages = {
            new[] { "dashboard", "Dashboard", "house" }, new[] { "pets", "Pets", "paw" }, new[] { "cameras", "Cameras", "video" }, new[] { "lock", "Lock", "lock" },
            new[] { "alerts", "Alerts", "bell" }, new[] { "automations", "Automations", "bolt" }, new[] { "devices", "Devices", "list-check" }, new[] { "settings", "Settings", "gear" } };
        public static string PageId(string s)
        {
            var v = (s ?? "").Trim().ToLowerInvariant().Replace(" ", "");
            if (v == "home" || v == "status") v = "dashboard"; if (v == "feeder" || v == "fountain" || v == "pet") v = "pets"; if (v == "camera") v = "cameras";
            if (v == "alert") v = "alerts"; if (v == "automation" || v == "rules") v = "automations"; if (v == "mapping" || v == "entities") v = "devices"; if (v == "door") v = "lock";
            return Pages.Any(p => p[0] == v) ? v : null;
        }

        public MainWindow(Controller controller)
        {
            c = controller;
            Ui.Dress(this, "Hallow Home", 1200);
            SizeToContent = SizeToContent.Manual; Height = 800; MinWidth = 980; MinHeight = 620;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Rebuild();
            Action changed = () => Dispatcher.BeginInvoke(new Action(SoonRebuild));
            c.Changed += changed;
            Closed += (s, e) => { c.Changed -= changed; StopFeeds(); c.MainClosed(); };
            StateChanged += (s, e) => { if (WindowState == WindowState.Minimized) StopFeeds(); else BuildPage(); };
        }
        void SoonRebuild()
        {
            if (Snap.On) { BuildPage(); return; }
            if (rebuildTimer == null) { rebuildTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) }; rebuildTimer.Tick += (s, e) => { rebuildTimer.Stop(); if (!Typing()) BuildPage(); else UpdateMeter(); }; }
            rebuildTimer.Stop(); rebuildTimer.Start();
        }
        bool Typing() { var f = System.Windows.Input.Keyboard.FocusedElement as DependencyObject; return (f is TextBox || f is PasswordBox) && IsKeyboardFocusWithin; }

        public void Rebuild()
        {
            Ui.Restyle(this); Ui.TitleBar(this);
            var root = new Grid();
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(212) });
            root.ColumnDefinitions.Add(new ColumnDefinition());
            var side = new DockPanel { Background = Ui.R("Side"), LastChildFill = false };
            var head = new StackPanel { Margin = new Thickness(16, 18, 16, 18) };
            head.Children.Add(Ui.Header("HOME", 18));
            head.Children.Add(Ui.Muted("Pets, cameras and more, through Home Assistant", 11.5, 0));
            DockPanel.SetDock(head, Dock.Top); side.Children.Add(head);
            nav.Clear();
            foreach (var p in Pages)
            {
                var id = p[0];
                var b = new Button { Style = Ui.S("Nav"), Content = Icons.WithText(p[2], p[1], 14, Ui.R("Text")), Margin = new Thickness(8, 0, 8, 0) };
                b.Click += (s, e) => Show(id);
                DockPanel.SetDock(b, Dock.Top); side.Children.Add(b); nav[id] = b;
            }
            var foot = new StackPanel { Margin = new Thickness(16, 0, 16, 14) };
            updateBtn = Ui.Btn("Update", () => c.InstallUpdate(), true); updateBtn.Visibility = c.PendingUpdate == null ? Visibility.Collapsed : Visibility.Visible; updateBtn.HorizontalAlignment = HorizontalAlignment.Left; updateBtn.Margin = new Thickness(0, 0, 0, 10);
            if (c.PendingUpdate != null) updateBtn.Content = "Update to v" + c.PendingUpdate.Version.ToString(2);
            foot.Children.Add(updateBtn);
            foot.Children.Add(Ui.Muted("v" + Program.Version + " · Home Assistant backend", 11, 0));
            DockPanel.SetDock(foot, Dock.Bottom); side.Children.Add(foot);
            root.Children.Add(side);
            root.Children.Add(new Border { Width = 1, Background = Ui.R("Line"), HorizontalAlignment = HorizontalAlignment.Right });
            var right = new DockPanel(); Grid.SetColumn(right, 1);
            var bar = new DockPanel { Background = Ui.R("Side"), Height = 28 };
            meter = new TextBlock { FontFamily = Ui.Mono, FontSize = 11, Foreground = Ui.R("Muted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
            DockPanel.SetDock(meter, Dock.Right); bar.Children.Add(meter);
            sayText = new TextBlock { FontSize = 12, Foreground = Ui.R("TextSoft"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            bar.Children.Add(sayText);
            var barWrap = new Border { BorderBrush = Ui.R("Line"), BorderThickness = new Thickness(0, 1, 0, 0), Child = bar };
            DockPanel.SetDock(barWrap, Dock.Bottom); right.Children.Add(barWrap);
            content = new Border(); right.Children.Add(content);
            root.Children.Add(right);
            Content = root;
            Show(page);
        }
        public void ShowUpdate(string label) { if (updateBtn == null) return; updateBtn.Content = label; updateBtn.Visibility = Visibility.Visible; }
        public void Say(string text)
        {
            if (sayText == null) return; sayText.Text = text ?? "";
            if (sayTimer != null) sayTimer.Stop();
            sayTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) }; sayTimer.Tick += (s, e) => { sayTimer.Stop(); sayText.Text = ""; }; sayTimer.Start();
        }
        public void Show(string id)
        {
            var p = PageId(id) ?? "dashboard"; if (p != "cameras" || page != "cameras") camera = null; page = p;
            foreach (var kv in nav) kv.Value.Tag = kv.Key == page ? "on" : null;
            BuildPage();
        }
        public void ShowCamera(string id) { camera = id; page = "cameras"; foreach (var kv in nav) kv.Value.Tag = kv.Key == page ? "on" : null; BuildPage(); }
        public void GameChanged() { if (c.Game != null) StopFeeds(); BuildPage(); }
        public void AskFeed()
        {
            var err = c.FeedCheck(1); if (err != null) { Say(err); return; }
            int n = Dialogs.AskFeed(this, c); if (n <= 0) { Say("Not fed."); return; }
            var e = c.Feed(n, true, r => Say(r == null ? "Feed now sent to the feeder (" + n + " portion" + (n == 1 ? "" : "s") + ")." : "Feeding failed: " + r));
            if (e != null) Say(e);
        }
        string builtPage;
        void BuildPage()
        {
            if (content == null) return;
            FrameworkElement el;
            var keep = new HashSet<string>();
            switch (page)
            {
                case "pets": el = PetsPage(keep); break;
                case "cameras": el = camera != null ? CameraPage(keep) : CamerasPage(keep); break;
                case "lock": el = LockPage(); break;
                case "alerts": el = AlertsPage(); break;
                case "automations": el = AutomationsPage(); break;
                case "devices": el = DevicesPage(); break;
                case "settings": el = SettingsPage(); break;
                default: el = DashboardPage(keep); break;
            }
            foreach (var k in feeds.Keys.Where(k => !keep.Contains(k)).ToList()) { feeds[k].Stop(); feeds.Remove(k); }
            var old = content.Child as ScrollViewer; double off = old != null && builtPage == page + camera ? old.VerticalOffset : 0; builtPage = page + camera;
            content.Child = el;
            var sv = el as ScrollViewer; if (sv != null && off > 0) Dispatcher.BeginInvoke(new Action(() => sv.ScrollToVerticalOffset(off)), DispatcherPriority.Loaded);   // a refresh keeps your place
            UpdateMeter();
        }
        void UpdateMeter()
        {
            if (meter == null) return;
            var st = c.Connected ? (c.Live ? "HOME ASSISTANT · LIVE" : "HOME ASSISTANT · POLLING") : c.Connecting ? "CONNECTING" : c.Configured ? "NOT CONNECTED" : "NOT SET UP";
            meter.Text = st + "   " + c.Entities.Count + " entities   " + (c.Active.Count == 0 ? "all fine" : c.Active.Count + " alert" + (c.Active.Count == 1 ? "" : "s")) + (c.Game != null ? "   game: quiet" : "");
        }

        // ---- page building blocks
        static ScrollViewer Scroll(UIElement child)
        {
            UIElement inner = child;
            if (child is StackPanel && ((StackPanel)child).Tag as string == "page")
            {
                var g = new Grid(); g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MaxWidth = 1000 }); g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.0001, GridUnitType.Star) });
                g.Children.Add(child); inner = g;
            }
            return new ScrollViewer { Content = inner, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        }
        static StackPanel PageBody(string title, string sub)
        {
            var sp = new StackPanel { Margin = new Thickness(26, 20, 26, 24), Tag = "page" };
            sp.Children.Add(Ui.T(title, 22, true, null, 2));
            if (sub != null) sp.Children.Add(Ui.Muted(sub, 12.5, 16));
            return sp;
        }
        static TextBlock Section(string text) { var t = Ui.Caps(text, 11); t.Margin = new Thickness(0, 16, 0, 8); return t; }
        static FrameworkElement KV(string k, FrameworkElement v, double kw = 150)
        {
            var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(kw) }); g.ColumnDefinitions.Add(new ColumnDefinition());
            g.Children.Add(Ui.Muted(k, 12.5)); Grid.SetColumn(v, 1); g.Children.Add(v); return g;
        }
        static FrameworkElement KV(string k, string v, string note = null, string color = null, double kw = 150)
        {
            var t = new TextBlock { TextWrapping = TextWrapping.Wrap };
            t.Inlines.Add(new System.Windows.Documents.Run(v ?? "-") { FontFamily = Ui.Mono, FontSize = 12.5, Foreground = color != null ? Ui.R(color) : Ui.R("Text") });
            if (note != null) t.Inlines.Add(new System.Windows.Documents.Run("   " + note) { FontSize = 11.5, Foreground = Ui.R("Muted") });
            return KV(k, t, kw);
        }
        static UIElement Bullets(IEnumerable<string> lines, string dotColor = null)
        {
            var sp = new StackPanel();
            foreach (var l in lines) { var d = new DockPanel { Margin = new Thickness(0, 2, 0, 2) }; var dot = Ui.T("•", 12.5, true, Ui.R(dotColor ?? "Muted")); dot.Width = 14; d.Children.Add(dot); d.Children.Add(Ui.T(l, 12.5, false, Ui.R("TextSoft"))); sp.Children.Add(d); }
            return sp;
        }
        ToggleButton Chip(string label, bool on, Action click)
        {
            var t = new ToggleButton { Content = label, Style = Ui.S("Chip"), Margin = new Thickness(0, 0, 6, 6), IsChecked = on };
            t.Click += (s, e) => click(); return t;
        }
        static WrapPanel Wrap(params UIElement[] items) { var w = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) }; foreach (var i in items) w.Children.Add(i); return w; }
        static FrameworkElement Bar(double? pct, string color, double width = 160)
        {
            var g = new Grid { Width = width, Height = 8, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
            g.Children.Add(new Border { Background = Ui.R("LineDim"), CornerRadius = new CornerRadius(4) });
            if (pct != null) g.Children.Add(new Border { Background = Ui.R(color), CornerRadius = new CornerRadius(4), Width = Math.Max(4, width * Math.Min(100, Math.Max(0, pct.Value)) / 100.0), HorizontalAlignment = HorizontalAlignment.Left });
            return g;
        }
        static FrameworkElement Head(string icon, string title, string sub, string dotColor)
        {
            var d = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            var ic = Icons.Make(icon, 20, Ui.R("AccentBrush")); ic.Margin = new Thickness(0, 0, 12, 0); ic.VerticalAlignment = VerticalAlignment.Top; d.Children.Add(ic);
            if (dotColor != null) { var pc = dotColor.Split('|'); var p = Ui.Pill(pc.Length > 1 ? pc[1] : pc[0] == "Green" ? "Online" : "Offline", pc[0], pc[0] + "Bg"); DockPanel.SetDock(p, Dock.Right); d.Children.Add(p); }
            var sp = new StackPanel(); sp.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Head });
            if (sub != null) sp.Children.Add(Ui.Muted(sub, 11.5)); d.Children.Add(sp);
            return d;
        }
        static Grid Columns(int n, params UIElement[] items)
        {
            var g = new Grid();
            for (int i = 0; i < n; i++) { g.ColumnDefinitions.Add(new ColumnDefinition()); }
            for (int i = 0; i < items.Length; i++)
            {
                if (i % n == 0) g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var el = items[i] as FrameworkElement; if (el != null) el.Margin = new Thickness(i % n == 0 ? 0 : 6, 0, i % n == n - 1 ? 0 : 6, 12);
                Grid.SetColumn(items[i], i % n); Grid.SetRow(items[i], i / n); g.Children.Add(items[i]);
            }
            return g;
        }
        FrameworkElement SetupNote(StackPanel sp)
        {
            if (!c.Configured) { sp.Children.Add(Ui.Note("Hallow Home isn't connected to Home Assistant yet. Set up Home Assistant on your NAS first (NAS-SETUP.md in the download), then paste its address and a long-lived access token on the Settings page.", "Amber", "AmberBg")); sp.Children.Add(Wrap(Ui.Btn("Open Settings", () => Show("settings"), true))); return sp; }
            if (!c.Connected) sp.Children.Add(Ui.Note(c.Connecting ? "Connecting to Home Assistant…" : "Can't reach Home Assistant: " + (c.LastError ?? "no answer") + " Hallow Home keeps trying.", c.Connecting ? "Lilac" : "Red", c.Connecting ? "LilacBg" : "RedBg"));
            return sp;
        }

        // ---- camera tiles: Home Assistant's MJPEG proxy (snap: a made-up frame)
        readonly Dictionary<string, CameraFeed> feeds = new Dictionary<string, CameraFeed>();
        readonly Dictionary<string, BitmapSource> lastFrame = new Dictionary<string, BitmapSource>();
        void StopFeeds() { foreach (var f in feeds.Values) f.Stop(); feeds.Clear(); }
        FrameworkElement Tile(Entity cam, HashSet<string> keep, double height, bool click)
        {
            var g = new Grid { Height = height, Background = Brushes.Black, ClipToBounds = true };
            var img = new Image { Stretch = Stretch.UniformToFill };
            g.Children.Add(img);
            var label = new Border { Background = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), Padding = new Thickness(8, 3, 8, 3), VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Left,
                Child = new TextBlock { Text = cam.Label, Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeights.SemiBold } };
            g.Children.Add(label);
            if (!cam.Available)
            {
                g.Children.Add(new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Children = {
                    new TextBlock { Text = "Offline", Foreground = Brushes.White, FontSize = 15, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
                    new TextBlock { Text = "since " + Fmt.Clock(cam.Changed, c.Now()), Foreground = new SolidColorBrush(Color.FromRgb(190, 190, 200)), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center } } });
            }
            else if (Snap.On)
            {
                int n = 0; foreach (var ch in cam.Id) n += ch;
                img.Source = Mjpeg.Decode(FakeFrames.Jpeg(cam.Label, n % 40));
            }
            else if (c.Game != null) g.Children.Add(new TextBlock { Text = "Paused while you play", Foreground = Brushes.White, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center });
            else if (c.Client != null && c.Connected)
            {
                BitmapSource last; if (lastFrame.TryGetValue(cam.Id, out last)) img.Source = last;
                keep.Add(cam.Id);
                CameraFeed f;
                if (!feeds.TryGetValue(cam.Id, out f)) { f = new CameraFeed(cam.Id); feeds[cam.Id] = f; f.Start(c.Client, camera != null ? 8 : 3); }
                var id = cam.Id;
                f.Frame = bmp => Dispatcher.BeginInvoke(new Action(() => { lastFrame[id] = bmp; img.Source = bmp; }));
            }
            var b = new Border { Child = g, BorderBrush = Ui.R("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3) };
            if (click) { b.Cursor = System.Windows.Input.Cursors.Hand; b.ToolTip = "Open " + cam.Label; var id2 = cam.Id; b.MouseLeftButtonUp += (s, e) => ShowCamera(id2); }
            return b;
        }

        // ================= Dashboard
        FrameworkElement DashboardPage(HashSet<string> keep)
        {
            var sp = PageBody("Dashboard", null);
            SetupNote(sp);
            if (!c.Configured) return Scroll(sp);
            // alerts
            if (c.Active.Count > 0)
            {
                var al = new StackPanel();
                al.Children.Add(Head("triangle-exclamation", c.Active.Count == 1 ? "1 thing needs a look" : c.Active.Count + " things need a look", null, null));
                foreach (var a in c.Active.Take(4)) al.Children.Add(AlertRow(a));
                var go = Ui.Btn("Open", () => Show("alerts")); go.HorizontalAlignment = HorizontalAlignment.Left; al.Children.Add(go);
                var card = Ui.Card(al); card.BorderBrush = Ui.R(c.Active.Any(a => a.Bad) ? "Red" : "Amber"); card.Margin = new Thickness(0, 0, 0, 12); sp.Children.Add(card);
            }
            else if (c.Connected) sp.Children.Add(Ui.Note("All fine: the feeder, the fountain and the cameras look OK.", "Green", "GreenBg"));
            sp.Children.Add(Columns(3, FeederCard(false), FountainCard(false), LockCard(false)));
            sp.Children.Add(Section("Cameras"));
            if (c.Map.Cameras.Count == 0) sp.Children.Add(Ui.Muted("No cameras in Home Assistant yet. Add your Wyze cameras through docker-wyze-bridge (NAS-SETUP.md).", 12.5));
            else sp.Children.Add(Columns(4, c.Map.Cameras.Take(4).Select(cam => (UIElement)Tile(cam, keep, 130, true)).ToArray()));
            return Scroll(sp);
        }
        FrameworkElement AlertRow(Alert a)
        {
            var d = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var ic = Icons.Make(a.Bad ? "triangle-exclamation" : "circle-info", 14, Ui.R(a.Bad ? "Red" : "Amber")); ic.Margin = new Thickness(0, 2, 10, 0); ic.VerticalAlignment = VerticalAlignment.Top; d.Children.Add(ic);
            var sp = new StackPanel();
            var t = new TextBlock { TextWrapping = TextWrapping.Wrap };
            t.Inlines.Add(new System.Windows.Documents.Run(a.Title) { FontWeight = FontWeights.SemiBold, FontSize = 13 });
            t.Inlines.Add(new System.Windows.Documents.Run("   since " + Fmt.Clock(a.Since, c.Now())) { FontSize = 11.5, Foreground = Ui.R("Muted") });
            sp.Children.Add(t); sp.Children.Add(Ui.Muted(a.Text, 12)); d.Children.Add(sp);
            return d;
        }

        // ================= Pets
        Border FeederCard(bool full)
        {
            var f = c.Feeder; var sp = new StackPanel();
            if (!f.Present) { sp.Children.Add(Head("bowl-food", "Feeder", null, null)); sp.Children.Add(Ui.Muted("No feeder found in Home Assistant. Install the community Petlibro integration (HACS) and sign in there; or map the entities on the Devices page.", 12)); return Ui.Card(sp); }
            sp.Children.Add(Head("bowl-food", "Feeder", "Petlibro Granary (community integration)", !f.Online ? "Red" : f.Jam || c.Active.Any(a => a.Rule == "jam") ? "Red|Needs a look" : f.FoodLow ? "Amber|Needs a look" : "Green"));
            var nx = f.Next == null ? "-" : Fmt.Clock(f.Next, c.Now()) + (f.NextGrams != null ? " (" + Read.Num(f.NextGrams.Value) + " g)" : "");
            sp.Children.Add(KV("Next meal", nx, null, null, 110));
            sp.Children.Add(KV("Today", (f.TodayMeals != null ? f.TodayMeals + " meal" + (f.TodayMeals == 1 ? "" : "s") : "-") + (f.TodayGrams != null ? " · " + Read.Num(f.TodayGrams.Value) + " g" : ""), null, null, 110));
            sp.Children.Add(KV("Food", f.FoodText ?? "-", f.FoodLow ? "low - top it up" : null, f.FoodLow ? "Red" : null, 110));
            if (f.FoodPct != null) sp.Children.Add(KV("", Bar(f.FoodPct, f.FoodLow ? "Red" : "Green"), 110));
            if (full)
            {
                sp.Children.Add(KV("Last meal", Fmt.Clock(f.Last, c.Now()), null, null, 110));
                if (f.Desiccant != null) sp.Children.Add(KV("Desiccant", f.Desiccant, null, null, 110));
                if (f.Battery != null) sp.Children.Add(KV("Battery", f.Battery, "backup batteries", null, 110));
                var icons = new StackPanel { Orientation = Orientation.Horizontal };
                if (f.Wifi != null) { icons.Children.Add(Icons.WithText("wifi", f.Wifi, 13, Ui.R("Muted"))); }
                if (f.Muted == true) { var m = Icons.WithText("volume-xmark", "muted", 13, Ui.R("Muted")); m.Margin = new Thickness(16, 0, 0, 0); icons.Children.Add(m); }
                if (icons.Children.Count > 0) sp.Children.Add(KV("Device", icons, 110));
            }
            var fb = Ui.Btn("Feed now…", () => AskFeed(), true); fb.IsEnabled = f.CanFeed && c.Connected; fb.HorizontalAlignment = HorizontalAlignment.Left; fb.Margin = new Thickness(0, 10, 0, 0);
            if (!f.CanFeed) fb.ToolTip = "No Feed now button was found - map it on the Devices page.";
            sp.Children.Add(fb);
            return Ui.Card(sp);
        }
        Border FountainCard(bool full)
        {
            var w = c.Fountain; var sp = new StackPanel();
            if (!w.Present) { sp.Children.Add(Head("droplet", "Fountain", null, null)); sp.Children.Add(Ui.Muted("No fountain found in Home Assistant. The community Petlibro integration (HACS) adds the Dockstream; or map the entities on the Devices page.", 12)); return Ui.Card(sp); }
            var problems = w.Explain(c.IntakeLow);
            sp.Children.Add(Head("droplet", "Fountain", "Petlibro Dockstream 2 (community integration)", !w.Online ? "Red" : w.WaterLow || w.PumpOn == false ? "Red|Needs a look" : problems.Count > 0 ? "Amber|Check" : "Green"));
            sp.Children.Add(KV("Water", w.WaterText ?? "-", w.WaterLow ? "low" : null, w.WaterLow ? "Red" : null, 110));
            if (w.WaterPct != null) sp.Children.Add(KV("", Bar(w.WaterPct, w.WaterLow ? "Red" : "Blue"), 110));
            var intake = w.IntakeToday == null ? "-" : Read.Num(w.IntakeToday.Value) + " " + w.Unit;
            sp.Children.Add(KV("Drank today", intake, w.IntakeAvg != null ? "7-day avg " + Read.Num(Math.Round(w.IntakeAvg.Value, 2)) + " " + w.Unit : "average after 3 days", c.IntakeLow ? "Amber" : null, 110));
            sp.Children.Add(KV("Pump", w.PumpText ?? "-", null, w.PumpOn == false ? "Red" : null, 110));
            sp.Children.Add(KV("Filter", w.FilterText ?? "-", w.FilterDue ? "due" : null, w.FilterDue ? "Amber" : null, 110));
            if (full) sp.Children.Add(KV("Cleaning", w.CleaningText ?? "-", w.CleaningDue ? "due" : null, w.CleaningDue ? "Amber" : null, 110));
            if (problems.Count > 0)
            {
                var t = Ui.T(problems.Count == 1 ? "1 irregularity" : problems.Count + " irregularities", 12.5, true, Ui.R("Amber")); t.Margin = new Thickness(0, 10, 0, 4); sp.Children.Add(t);
                sp.Children.Add(full ? Bullets(problems, "Amber") : (UIElement)Ui.Muted(problems[0] + (problems.Count > 1 ? " (+" + (problems.Count - 1) + " more on the Pets page)" : ""), 12));
            }
            return Ui.Card(sp);
        }
        Border LockCard(bool full)
        {
            var l = c.Lock; var sp = new StackPanel();
            sp.Children.Add(Head(l.Kind == "ha" ? "lock" : "bluetooth-b", "Door lock", "Wyze Lock Bolt", null));
            sp.Children.Add(Ui.T(l.Title, 13.5, true, l.Kind == "ha" ? Ui.R(l.State == "locked" ? "Green" : "Amber") : Ui.R("Lilac"), 6));
            sp.Children.Add(Ui.Muted(full ? l.Text : "Bluetooth only: Home Assistant and Wyze's cloud can't reach it. Lock and unlock it with the Wyze app at the door.", 12));
            if (!full) { var b = Ui.Btn("Why?", () => Show("lock")); b.HorizontalAlignment = HorizontalAlignment.Left; b.Margin = new Thickness(0, 10, 0, 0); if (l.Kind != "ha") sp.Children.Add(b); }
            return Ui.Card(sp);
        }
        FrameworkElement PetsPage(HashSet<string> keep)
        {
            var sp = PageBody("Pets", "The feeder and the fountain, from the community Petlibro integration in Home Assistant.");
            SetupNote(sp);
            var feederCol = new StackPanel(); feederCol.Children.Add(FeederCard(true));
            if (c.Feeder.Camera != null) { var t = Tile(c.Feeder.Camera, keep, 220, true); t.Margin = new Thickness(0, 12, 0, 0); feederCol.Children.Add(t); }
            var fountainCol = new StackPanel(); fountainCol.Children.Add(FountainCard(true));
            if (c.Fountain.Present) { var ch = IntakeChart(); ch.Margin = new Thickness(0, 12, 0, 0); fountainCol.Children.Add(ch); }
            sp.Children.Add(Columns(2, feederCol, fountainCol));
            sp.Children.Add(Ui.Muted("The water-intake hint compares today with your cat's own last 7 days. It's a nudge to keep an eye on them, not medical advice - call your vet if you're worried.", 11.5, 0));
            return Scroll(sp);
        }
        Border IntakeChart()
        {
            var sp = new StackPanel(); sp.Children.Add(Ui.Caps("Water intake, last 8 days", 10.5));
            var now = c.Now(); var days = Enumerable.Range(0, 8).Select(i => now.Date.AddDays(i - 7)).ToList();
            var vals = days.Select(d => { double v; if (d == now.Date && c.Fountain.IntakeToday != null) return c.Fountain.IntakeToday; return c.Hist.Intake.TryGetValue(History.Day(d), out v) ? v : (double?)null; }).ToList();
            var max = Math.Max(0.1, vals.Where(v => v != null).Select(v => v.Value).DefaultIfEmpty(1).Max());
            var g = new Grid { Height = 110, Margin = new Thickness(0, 10, 0, 0) };
            for (int i = 0; i < 8; i++)
            {
                g.ColumnDefinitions.Add(new ColumnDefinition());
                var col = new DockPanel { Margin = new Thickness(4, 0, 4, 0) };
                var lab = Ui.Muted(i == 7 ? "today" : days[i].ToString("ddd"), 10.5); lab.HorizontalAlignment = HorizontalAlignment.Center; DockPanel.SetDock(lab, Dock.Bottom); col.Children.Add(lab);
                var num = Ui.Muted(vals[i] == null ? "" : Read.Num(Math.Round(vals[i].Value, 1)), 10.5); num.HorizontalAlignment = HorizontalAlignment.Center; DockPanel.SetDock(num, Dock.Top); col.Children.Add(num);
                var barH = vals[i] == null ? 0 : Math.Max(3, 64 * vals[i].Value / max);
                col.Children.Add(new Border { Height = barH, VerticalAlignment = VerticalAlignment.Bottom, CornerRadius = new CornerRadius(2, 2, 0, 0), Background = Ui.R(i == 7 ? (c.IntakeLow ? "Amber" : "AccentBrush") : "Lilac") });
                Grid.SetColumn(col, i); g.Children.Add(col);
            }
            sp.Children.Add(g);
            return Ui.Card(sp);
        }

        // ================= Cameras
        FrameworkElement CamerasPage(HashSet<string> keep)
        {
            var sp = PageBody("Cameras", "Live from Home Assistant's camera proxy while this page is open (paused during games). Click one for the big view, a snapshot and the live player.");
            SetupNote(sp);
            if (c.Map.Cameras.Count == 0) sp.Children.Add(Ui.Muted("No camera entities in Home Assistant yet. docker-wyze-bridge serves your Wyze cameras as RTSP / WebRTC / HLS; add each to HA (NAS-SETUP.md).", 12.5));
            else
            {
                var online = c.Map.Cameras.Count(x => x.Available);
                sp.Children.Add(Ui.Muted(online + " of " + c.Map.Cameras.Count + " online", 12, 8));
                sp.Children.Add(Columns(c.Map.Cameras.Count <= 4 ? 2 : 3, c.Map.Cameras.Select(cam => (UIElement)Tile(cam, keep, c.Map.Cameras.Count <= 4 ? 250 : 190, true)).ToArray()));
            }
            sp.Children.Add(Section("Recordings"));
            sp.Children.Add(RecordingsBlock());
            return Scroll(sp);
        }
        FrameworkElement RecordingsBlock()
        {
            var sp = new StackPanel();
            if (string.IsNullOrWhiteSpace(c.S.RecordingsFolder))
            {
                sp.Children.Add(Ui.Muted("Hallow Home doesn't record - docker-wyze-bridge or Home Assistant can, on the NAS. Set \"Recordings folder\" in Settings (e.g. a share like \\\\nas\\docker\\wyze-bridge\\recordings) and the newest files show here.", 12.5));
                return sp;
            }
            string err = null; var list = Snap.On ? new List<Recording>() : Cams.List(c.S.RecordingsFolder, 60, out err);
            sp.Children.Add(Wrap(Ui.Btn("Open the recordings folder", () => { try { Process.Start("explorer.exe", "\"" + c.S.RecordingsFolder + "\""); } catch { } }), Ui.Btn("Refresh", () => BuildPage())));
            if (err != null) { sp.Children.Add(Ui.Muted(err, 12.5)); return sp; }
            if (list.Count == 0) { sp.Children.Add(Ui.Muted("No video or picture files there yet.", 12.5)); return sp; }
            foreach (var r in list)
            {
                var d = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                var open = Ui.Btn("Open", () => { try { Process.Start(new ProcessStartInfo(r.Path) { UseShellExecute = true }); } catch { } }); DockPanel.SetDock(open, Dock.Right); d.Children.Add(open);
                var t = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                t.Inlines.Add(new System.Windows.Documents.Run(r.When.ToString("ddd d MMM HH:mm") + "   ") { FontFamily = Ui.Mono, FontSize = 12 });
                t.Inlines.Add(new System.Windows.Documents.Run(r.Name) { FontSize = 12.5 });
                t.Inlines.Add(new System.Windows.Documents.Run("   " + Fmt.Size(r.Size)) { FontSize = 11.5, Foreground = Ui.R("Muted") });
                d.Children.Add(t); sp.Children.Add(d);
            }
            return sp;
        }
        FrameworkElement CameraPage(HashSet<string> keep)
        {
            Entity cam; c.Entities.TryGetValue(camera, out cam);
            if (cam == null) { camera = null; return CamerasPage(keep); }
            var sp = PageBody(cam.Label, cam.Available ? "Live from Home Assistant's camera proxy." : "Offline since " + Fmt.Clock(cam.Changed, c.Now()) + ".");
            sp.Children.Add(Tile(cam, keep, 470, false));
            var live = Ui.Btn("Live view", () => OpenLive(cam), true); live.IsEnabled = cam.Available && c.S.LiveBase != null;
            live.ToolTip = c.S.LiveBase == null ? "Set the docker-wyze-bridge WebRTC / HLS address in Settings first." : "Opens " + Cams.LiveUrl(c.S.LiveBase, cam);
            var snap = Ui.Btn("Snapshot", () => SaveSnapshot(cam)); snap.IsEnabled = cam.Available && c.Connected;
            var row = Wrap(Ui.Btn("All cameras", () => { camera = null; BuildPage(); }), snap, live); row.Margin = new Thickness(0, 12, 0, 0); sp.Children.Add(row);
            sp.Children.Add(Ui.Muted(c.S.LiveBase == null ? "Live view (WebRTC / HLS, smoother and with sound) uses docker-wyze-bridge's own player - set its address in Settings." : "Live view opens docker-wyze-bridge's player for \"" + Cams.BridgeName(cam) + "\" in a WebView2 window. Snapshots go to Pictures\\Hallow Home.", 12, 0));
            return Scroll(sp);
        }
        void OpenLive(Entity cam)
        {
            var url = Cams.LiveUrl(c.S.LiveBase, cam); if (url == null) return;
            if (CameraWindow.RuntimeVersion() == null) { Dialogs.Info(this, "The live view needs Microsoft Edge WebView2", "Windows 11 has it built in; on Windows 10 install \"Microsoft Edge WebView2 Runtime\" from Microsoft. The camera grid works without it."); return; }
            var w = new CameraWindow(cam.Label, url) { Owner = this }; w.Show();
        }
        void SaveSnapshot(Entity cam)
        {
            var h = c.Client; if (h == null) return; Say("Taking a snapshot…");
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string msg; try { var f = Cams.SaveSnapshot(cam.Label, h.Snapshot(cam.Id)); msg = "Saved " + Path.GetFileName(f) + " in Pictures\\Hallow Home."; } catch (Exception e) { msg = "Snapshot failed: " + e.Message; }
                Dispatcher.BeginInvoke(new Action(() => Say(msg)));
            });
        }

        // ================= Lock
        FrameworkElement LockPage()
        {
            var sp = PageBody("Lock", "The Wyze Lock Bolt, honestly.");
            var l = c.Lock;
            var card = new StackPanel();
            card.Children.Add(Head(l.Kind == "ha" ? "lock" : "bluetooth-b", l.Title, "Wyze Lock Bolt", null));
            card.Children.Add(Ui.T(l.Text, 12.5, false, Ui.R("TextSoft"), 8));
            if (l.Kind == "ha" && l.CanControl)
            {
                var lk = Ui.Btn("Lock", () => Say(c.LockAction(true) ?? "Asked Home Assistant to lock it."));
                var ul = Ui.Danger("Unlock", () => { if (Dialogs.Confirm(this, "Unlock the door?", "Home Assistant will ask the lock to unlock now. Only do this if you can see who's at the door.", "Unlock", true)) Say(c.LockAction(false) ?? "Asked Home Assistant to unlock it."); });
                card.Children.Add(Wrap(lk, ul));
            }
            sp.Children.Add(Ui.Card(card));
            sp.Children.Add(Section("Why it can't be controlled from here"));
            sp.Children.Add(Bullets(new[] {
                "The Lock Bolt has no Wi-Fi and doesn't use a Wyze hub or gateway. It only talks Bluetooth Low Energy, to a phone within a few metres.",
                "Wyze's cloud never hears from it, so Wyze's API - and the community Wyze integration for Home Assistant, which uses that API - can't see or move it.",
                "No maintained Home Assistant integration for it could be verified (late 2026). One person reported controlling theirs with their own Bluetooth setup; nothing published or reviewed.",
                "So: use the Wyze app (or the fingerprint pad) at the door. Hallow Home won't pretend to know whether it's locked." }));
            sp.Children.Add(Section("Maybe later"));
            sp.Children.Add(Ui.Muted(LockInfo.Later, 12.5));
            return Scroll(sp);
        }

        // ================= Alerts
        FrameworkElement AlertsPage()
        {
            var sp = PageBody("Alerts", "Smarter than the vendor apps: each alert once (not every check), quiet hours, quiet during games, and reminders only if you want them (Automations).");
            SetupNote(sp);
            sp.Children.Add(Section("Right now"));
            if (c.Active.Count == 0) sp.Children.Add(Ui.Muted(c.Connected ? "Nothing - all fine." : "Not connected.", 12.5));
            else { var l = new StackPanel(); foreach (var a in c.Active) l.Children.Add(AlertRow(a)); sp.Children.Add(Ui.Card(l)); }
            var snoozed = c.SnoozeUntil;
            var tools = Wrap(snoozed == null ? Ui.Btn("Pause alerts for 1 hour", () => c.Snooze(true)) : Ui.Btn("Resume alerts", () => c.Snooze(false)),
                Ui.Btn("Send a test alert", () => { c.Deliver("Hallow Home test", "This is a test alert from Hallow Home. If you see it, alerts reach you here.", true, true, false); Say("Test alert sent to the channels that are on."); }));
            sp.Children.Add(tools);
            if (snoozed != null) sp.Children.Add(Ui.Muted("Paused until " + snoozed.Value.ToString("HH:mm") + " - alerts that are still true then go out.", 12, 0));
            sp.Children.Add(Section("Sent recently"));
            List<string> sent; lock (c.Sent) sent = c.Sent.Take(8).ToList();
            sp.Children.Add(sent.Count == 0 ? (UIElement)Ui.Muted("Nothing yet.", 12.5) : Bullets(sent));

            sp.Children.Add(Section("Where alerts go"));
            sp.Children.Add(Ui.Switch("This PC (Hallow Hub's notification, or Hallow Home's own tray balloon)", c.S.UseHub, on => { c.S.UseHub = on; c.S.Save(); }));
            sp.Children.Add(SecretRow("Discord webhook", SecretStore.Discord, c.S.UseDiscord, on => { c.S.UseDiscord = on; c.S.Save(); }, "https://discord.com/api/webhooks/..."));
            sp.Children.Add(SecretRow("ntfy topic", SecretStore.Ntfy, c.S.UseNtfy, on => { c.S.UseNtfy = on; c.S.Save(); }, "https://ntfy.sh/a-long-random-topic"));
            sp.Children.Add(Ui.Muted("Only the alert's title and text are sent - never camera pictures, addresses or tokens. The webhook and topic are kept encrypted for your Windows user and never logged. Use a long random ntfy topic (anyone who knows it can read it), or your own ntfy server with an access token.", 11.5, 4));

            sp.Children.Add(Section("Quiet"));
            sp.Children.Add(Ui.Switch("Quiet hours: hold alerts from " + c.S.QuietStart + " to " + c.S.QuietEnd + " (sent when they end, if still true)", c.S.QuietOn, on => { c.S.QuietOn = on; c.S.Save(); BuildPage(); }));
            var qs = Ui.Field(c.S.QuietStart, 70); var qe = Ui.Field(c.S.QuietEnd, 70);
            sp.Children.Add(Wrap(Ui.Muted("From ", 12.5), qs, Ui.Muted("  to ", 12.5), qe, new Border { Width = 8 }, Ui.Btn("Save", () => { if (!Quiet.ValidTime(qs.Text.Trim()) || !Quiet.ValidTime(qe.Text.Trim())) { Say("Use 24-hour times like 22:00."); return; } c.S.QuietStart = qs.Text.Trim(); c.S.QuietEnd = qe.Text.Trim(); c.S.Save(); Say("Quiet hours saved."); BuildPage(); })));
            sp.Children.Add(Ui.Switch("During games, hold everything (otherwise only this PC stays quiet - your phone still gets alerts)", c.S.GameHoldAll, on => { c.S.GameHoldAll = on; c.S.Save(); }));

            sp.Children.Add(Section("Which alerts"));
            foreach (var r in AlertEngine.Rules)
            {
                var id = r[0];
                var sw = Ui.Switch(r[1] + " - " + r[2], c.S.RuleOn(id) && (id != "motion-away" || c.S.MotionAlerts), on => { if (on) c.S.RulesOff.Remove(id); else if (!c.S.RulesOff.Contains(id)) c.S.RulesOff.Add(id); if (id == "motion-away") c.S.MotionAlerts = on; c.S.Save(); c.Recompute(); });
                sp.Children.Add(sw);
            }
            sp.Children.Add(Ui.Muted("Thresholds: food and water low under " + c.S.FoodLowPct + " / " + c.S.WaterLowPct + " %, pump stopped for " + c.S.PumpOffMinutes + " min, filter / cleaning with " + c.S.FilterDueDays + " days left, a camera offline for " + c.S.CameraOfflineMinutes + " min, a missed meal checked " + c.S.JamGraceMinutes + " min after its time, intake under " + c.S.IntakeLowPct + " % of usual (after 15:00, once 3 days are known). \"Away\" = everyone's person entity in Home Assistant is away, or \"I'm away\" below. Hallow Places can tell Hallow Home later.", 11.5, 4));
            sp.Children.Add(Ui.Switch("I'm away (for motion alerts and \"everyone's away\" automations)", c.S.Away, on => c.SetAway(on)));
            return Scroll(sp);
        }
        FrameworkElement SecretRow(string label, string name, bool on, Action<bool> toggled, string hint)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
            var has = SecretStore.Has(name) && !Snap.On || (Snap.On && on);
            sp.Children.Add(Ui.Switch(label + (has ? "  (saved: " + (Snap.On ? "https://••••••••" : SecretStore.MaskUrl(SecretStore.Get(name))) + ")" : "  (not set)"), on, toggled));
            var box = new PasswordBox { Width = 420, MinHeight = 30, Padding = new Thickness(8, 5, 8, 5), Background = Ui.R("Well"), Foreground = Ui.R("Text"), BorderBrush = Ui.R("Line"), CaretBrush = Ui.R("AccentBrush"), FontFamily = Ui.Mono, ToolTip = hint };
            var row = Wrap(box, new Border { Width = 8 }, Ui.Btn("Save", () => { var e = SecretStore.Set(name, box.Password); box.Clear(); Say(e ?? (label + " saved (encrypted).")); BuildPage(); }), Ui.Btn("Remove", () => { SecretStore.Set(name, ""); Say(label + " removed."); BuildPage(); }));
            row.Margin = new Thickness(46, 2, 0, 0); sp.Children.Add(row);
            return sp;
        }

        // ================= Automations
        string newWhen = "alert:water-low", newThen = "notify"; double newRepeat = 12;
        FrameworkElement AutomationsPage()
        {
            var sp = PageBody("Automations", "Simple \"when this, then that\" rules. They run inside Hallow Home, only while it's running - for anything that must happen when the PC is off, make an automation in Home Assistant itself.");
            foreach (var r in c.S.Automations.ToList())
            {
                var rr = r; var d = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
                var rm = Ui.Btn("Remove", () => { c.S.Automations.Remove(rr); c.S.Save(); BuildPage(); }); DockPanel.SetDock(rm, Dock.Right); d.Children.Add(rm);
                var bad = AutomationEngine.Check(r);
                var sw = Ui.Switch(AutomationEngine.Describe(r), r.On, on => { rr.On = on; c.S.Save(); BuildPage(); }); d.Children.Add(sw);
                var card = Ui.Card(d, 12); card.Margin = new Thickness(0, 0, 0, 8); sp.Children.Add(card);
                if (bad != null && r.On) sp.Children.Add(Ui.Muted("Not running: " + bad, 11.5, 6));
                else if (bad != null) sp.Children.Add(Ui.Muted(r.Then.StartsWith("service:") ? "Pick the camera power switches below (the Wyze integration's switch.* entities) to use this one." : bad, 11.5, 6));
            }
            sp.Children.Add(Section("Add a rule"));
            sp.Children.Add(Ui.Muted("When", 12, 4));
            var w = new WrapPanel();
            foreach (var r in AlertEngine.Rules) { var id = "alert:" + r[0]; w.Children.Add(Chip(r[1], newWhen == id, () => { newWhen = id; BuildPage(); })); }
            w.Children.Add(Chip("Everyone's away", newWhen == "away", () => { newWhen = "away"; BuildPage(); }));
            w.Children.Add(Chip("Someone's home", newWhen == "home", () => { newWhen = "home"; BuildPage(); }));
            sp.Children.Add(w);
            sp.Children.Add(Ui.Muted("Then", 12, 4));
            var t = new WrapPanel();
            t.Children.Add(Chip("Remind me", newThen == "notify", () => { newThen = "notify"; BuildPage(); }));
            foreach (var e in c.Entities.Values.Where(x => x.Domain == "switch" || x.Domain == "light").OrderBy(x => x.Label).Take(16))
            {
                var on = "service:" + e.Domain + ".turn_on:" + e.Id; var off = "service:" + e.Domain + ".turn_off:" + e.Id;
                t.Children.Add(Chip("Turn on " + e.Label, newThen == on, () => { newThen = on; BuildPage(); }));
                t.Children.Add(Chip("Turn off " + e.Label, newThen == off, () => { newThen = off; BuildPage(); }));
            }
            sp.Children.Add(t);
            sp.Children.Add(Ui.Muted("Repeat", 12, 4));
            var rp = new WrapPanel();
            foreach (var h in new[] { 0.0, 2, 12, 24 }) { var hh = h; rp.Children.Add(Chip(h == 0 ? "Once each time" : h == 12 ? "Twice a day until fixed" : "Every " + h + " h", Math.Abs(newRepeat - h) < 0.01, () => { newRepeat = hh; BuildPage(); })); }
            sp.Children.Add(rp);
            var rule = new AutoRule { Id = "u" + DateTime.Now.Ticks, On = true, When = newWhen, Then = newThen, RepeatHours = newRepeat };
            sp.Children.Add(Ui.T(AutomationEngine.Describe(rule), 12.5, true, null, 6));
            sp.Children.Add(Wrap(Ui.Btn("Add", () => { var e = AutomationEngine.Check(rule); if (e != null) { Say(e); return; } c.S.Automations.Add(rule); c.S.Save(); Say("Added."); BuildPage(); }, true)));
            sp.Children.Add(Ui.Muted("Automations can turn switches, lights and cameras on or off and send reminders. They never unlock a door or feed - those always need you.", 11.5, 0));
            return Scroll(sp);
        }

        // ================= Devices (the mapping layer)
        FrameworkElement DevicesPage()
        {
            var sp = PageBody("Devices", "Which Home Assistant entity is what. Hallow Home guesses from the entity's domain, device class and name (the community integrations name things differently between versions); pick another one, or \"none\", wherever it guessed wrong.");
            SetupNote(sp);
            string dev = null;
            foreach (var r in Roles.All)
            {
                if (r.Device != dev) { dev = r.Device; sp.Children.Add(Section(dev == "feeder" ? "Feeder" : dev == "fountain" ? "Fountain" : "Lock")); }
                var e = c.Map.Get(r.Id); var manual = c.Map.Manual.Contains(r.Id);
                var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) }); g.ColumnDefinitions.Add(new ColumnDefinition()); g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.Children.Add(Ui.T(r.Label, 12.5));
                var v = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
                if (e != null)
                {
                    v.Inlines.Add(new System.Windows.Documents.Run(e.Id) { FontFamily = Ui.Mono, FontSize = 12 });
                    v.Inlines.Add(new System.Windows.Documents.Run("   " + (e.State ?? "") + (e.Unit != null ? " " + e.Unit : "")) { FontSize = 11.5, Foreground = Ui.R(e.Available ? "Muted" : "Red") });
                }
                else v.Inlines.Add(new System.Windows.Documents.Run(c.Map.Missing.Contains(r.Id) ? "the picked entity isn't in Home Assistant any more" : manual ? "none (you said so)" : "not found") { FontSize = 12, Foreground = Ui.R(c.Map.Missing.Contains(r.Id) ? "Red" : "Faint") });
                if (manual) v.Inlines.Add(new System.Windows.Documents.Run("   picked by you") { FontSize = 11.5, Foreground = Ui.R("Lilac") });
                Grid.SetColumn(v, 1); g.Children.Add(v);
                var role = r; var pick = Ui.Btn("Change…", null); pick.Click += (s, ev) => PickMenu(pick, role);
                Grid.SetColumn(pick, 2); g.Children.Add(pick);
                sp.Children.Add(g);
            }
            sp.Children.Add(Section("Also found"));
            sp.Children.Add(KV("Cameras", c.Map.Cameras.Count == 0 ? "none" : string.Join(", ", c.Map.Cameras.Select(x => x.Label))));
            sp.Children.Add(KV("Motion sensors", c.Map.Motion.Count == 0 ? "none" : string.Join(", ", c.Map.Motion.Select(x => x.Label))));
            sp.Children.Add(KV("People (for away)", c.Map.People.Count == 0 ? "none" : c.Map.People.Count + " person entit" + (c.Map.People.Count == 1 ? "y" : "ies")));
            sp.Children.Add(KV("All entities", c.Entities.Count.ToString()));
            return Scroll(sp);
        }
        void PickMenu(Button anchor, RoleDef r)
        {
            var m = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
            Action<string, string> add = (label, val) => { var mi = new MenuItem { Header = label }; mi.Click += (s, e) => c.SetOverride(r.Id, val); m.Items.Add(mi); };
            add("Automatic (let Hallow Home guess)", null);
            add("None - this device doesn't have it", "");
            m.Items.Add(new Separator());
            foreach (var e in Roles.Candidates(r, c.Entities.Values).Take(24)) add(e.Label + "   (" + e.Id + ")", e.Id);
            m.IsOpen = true;
        }

        // ================= Settings
        FrameworkElement SettingsPage()
        {
            var sp = PageBody("Settings", null);
            sp.Children.Add(Section("Home Assistant"));
            sp.Children.Add(Ui.Muted("Home Assistant runs on your NAS (it isn't part of Hallow Home - NAS-SETUP.md explains the Docker setup). Hallow Home uses its REST and WebSocket APIs with a long-lived access token: in Home Assistant open your profile > Security > Long-lived access tokens > Create token.", 12, 8));
            var url = Ui.Field(c.S.HaUrl ?? "", 360);
            sp.Children.Add(KV("Address", url));
            var tok = new PasswordBox { Width = 360, MinHeight = 30, Padding = new Thickness(8, 5, 8, 5), Background = Ui.R("Well"), Foreground = Ui.R("Text"), BorderBrush = Ui.R("Line"), CaretBrush = Ui.R("AccentBrush"), FontFamily = Ui.Mono, HorizontalAlignment = HorizontalAlignment.Left };
            sp.Children.Add(KV("Access token", tok));
            sp.Children.Add(KV("", Ui.Muted(SecretStore.Has(SecretStore.Token) ? "A token is saved (encrypted for your Windows user, never shown or logged). Paste a new one to replace it." : "No token saved yet.", 11.5)));
            sp.Children.Add(Wrap(Ui.Btn("Save", () => { var e = c.SetConnection(url.Text, tok.Password); tok.Clear(); Say(e ?? "Saved - connecting…"); BuildPage(); }, true), Ui.Btn("Check again", () => { c.Connect(); Say("Connecting…"); }),
                Ui.Btn("Remove", () => { if (!Dialogs.Confirm(this, "Forget the token?", "Hallow Home disconnects from Home Assistant until you paste a token again. (Revoke it in Home Assistant too if you won't use it any more.)", "Remove", true)) return; SecretStore.Set(SecretStore.Token, ""); c.Connect(); BuildPage(); })));
            var stText = c.Connected ? "Connected" + (c.HaVersion != null ? " to Home Assistant " + c.HaVersion : "") + (c.Live ? ", live updates on" : ", checking every minute") + " · " + c.Entities.Count + " entities" : c.Connecting ? "Connecting…" : c.LastError ?? (c.Configured ? "Not connected." : "Not set up.");
            sp.Children.Add(Ui.T(stText, 12.5, false, Ui.R(c.Connected ? "Green" : c.Connecting ? "Muted" : c.Configured ? "Red" : "Muted"), 4));
            if (Ha.InsecureRemote(c.S.HaUrl)) sp.Children.Add(Ui.Note("That address is plain http over the internet: the token would travel unencrypted. Use https (e.g. a Cloudflare Tunnel with Cloudflare Access in front) or your home network's address.", "Red", "RedBg"));

            sp.Children.Add(Section("Cameras"));
            var live = Ui.Field(c.S.LiveBase ?? "", 360);
            sp.Children.Add(KV("Live view address", live));
            sp.Children.Add(KV("", Ui.Muted("Optional: docker-wyze-bridge's WebRTC (http://nas:8889) or HLS (http://nas:8888) address on your network. Keep it off the internet.", 11.5)));
            var rec = Ui.Field(c.S.RecordingsFolder ?? "", 360);
            sp.Children.Add(KV("Recordings folder", rec));
            sp.Children.Add(Wrap(Ui.Btn("Save", () =>
            {
                var lv = live.Text.Trim(); c.S.LiveBase = lv.Length == 0 ? null : lv; c.S.RecordingsFolder = rec.Text.Trim().Length == 0 ? null : rec.Text.Trim(); c.S.Fix(); c.S.Save();
                Say(lv.Length > 0 && c.S.LiveBase == null ? "The live view address should look like http://nas:8889." : "Saved."); BuildPage();
            })));

            sp.Children.Add(Section("Pets"));
            var gpp = Ui.Field(Read.Num(c.S.GramsPerPortion), 70); var maxp = Ui.Field(c.S.MaxPortions.ToString(), 70);
            sp.Children.Add(Wrap(Ui.Muted("One portion is ", 12.5), gpp, Ui.Muted(" g   ·   Feed now offers at most ", 12.5), maxp, Ui.Muted(" portions  ", 12.5), Ui.Btn("Save", () =>
            {
                double g; int m;
                if (!double.TryParse(gpp.Text.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out g) || g < 1 || g > 100 || !int.TryParse(maxp.Text.Trim(), out m) || m < 1 || m > 12) { Say("Grams 1-100, portions 1-12."); return; }
                c.S.GramsPerPortion = g; c.S.MaxPortions = m; c.S.Save(); c.Recompute(); Say("Saved.");
            })));

            sp.Children.Add(Section("Look"));
            var looks = new WrapPanel();
            foreach (var l in new[] { Mode.Follow, Mode.Light, Mode.Dark }) { var ll = l; looks.Children.Add(Chip(Mode.OwnLabel(l), Mode.Own(c.S.Look) == l, () => { c.S.Look = ll; c.S.Save(); c.ApplyAccent(); BuildPage(); })); }
            sp.Children.Add(looks);
            sp.Children.Add(Ui.Muted("Now " + (Mode.IsLight ? "light" : "dark") + " - " + Mode.Why(c.S.Look, Accent.ReadMode()) + ". \"Follow Hallow\" uses the switch in Hallow Toolkit (Light / Dark / Same as Windows).", 12, 4));
            sp.Children.Add(Ui.Switch("Use the Hallow accent colour (from Hallow Toolkit)", c.S.UseHallowAccent, on => { c.S.UseHallowAccent = on; c.S.Save(); c.ApplyAccent(); }));
            sp.Children.Add(Ui.Switch("Use the one Hallow icon (Hallow Hub's tray menu lists Hallow Home)", c.S.UseHallowIcon, on => { c.S.UseHallowIcon = on; c.S.Save(); c.CheckHost(); }));
            sp.Children.Add(Section("App"));
            sp.Children.Add(Ui.Switch("Start Hallow Home with Windows (in the tray) - alerts and automations only run while it runs", c.StartsWithWindows(), on => c.SetStartWithWindows(on)));
            sp.Children.Add(Wrap(Ui.Btn("Check for updates", () => c.CheckForUpdate(true)), Ui.Btn("Open data folder", () => { try { Directory.CreateDirectory(HomeSettings.Dir); Process.Start("explorer.exe", "\"" + HomeSettings.Dir + "\""); } catch { } })));
            sp.Children.Add(Ui.Muted("Family link: other Hallow apps (Hub's tray, later Remote's phone page) see counts and alert flags only - never camera addresses or the token. Feeding over the link needs an explicit confirm.", 11.5, 0));
            return Scroll(sp);
        }
    }
}
