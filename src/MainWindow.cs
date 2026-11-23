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
            new[] { "dashboard", "Dashboard", "house" }, new[] { "setup", "Setup", "wand-magic-sparkles" }, new[] { "pets", "Pets", "paw" }, new[] { "cameras", "Cameras", "video" }, new[] { "lock", "Lock", "lock" },
            new[] { "alerts", "Alerts", "bell" }, new[] { "automations", "Automations", "bolt" }, new[] { "devices", "Devices", "list-check" }, new[] { "settings", "Settings", "gear" } };
        public static string PageId(string s)
        {
            var v = (s ?? "").Trim().ToLowerInvariant().Replace(" ", "");
            if (v == "home" || v == "status") v = "dashboard"; if (v == "feeder" || v == "fountain" || v == "pet") v = "pets"; if (v == "camera") v = "cameras";
            if (v == "alert") v = "alerts"; if (v == "automation" || v == "rules") v = "automations"; if (v == "mapping" || v == "entities") v = "devices"; if (v == "door") v = "lock"; if (v == "wizard" || v == "start" || v == "demo") v = "setup";
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
            // 1.2: hidden (to the tray) or minimised = every camera stream and player stops, to save bandwidth; back = they restart
            StateChanged += (s, e) => SetPaused(WindowState == WindowState.Minimized || !IsVisible);
            IsVisibleChanged += (s, e) => SetPaused(WindowState == WindowState.Minimized || !IsVisible);
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
                case "setup": el = SetupPage(); break;
                case "cameras": el = camera != null ? CameraPage(keep) : CamerasPage(keep); break;
                case "lock": el = LockPage(); break;
                case "alerts": el = AlertsPage(); break;
                case "automations": el = AutomationsPage(); break;
                case "devices": el = DevicesPage(); break;
                case "settings": el = SettingsPage(); break;
                default: el = DashboardPage(keep); break;
            }
            foreach (var k in feeds.Keys.Where(k => !keep.Contains(k)).ToList()) { feeds[k].Stop(); feeds.Remove(k); }
            foreach (var k in players.Keys.Where(k => !keep.Contains(k)).ToList()) { players[k].Stop(); players.Remove(k); }
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
            if (c.S.Demo) sp.Children.Add(Ui.Note("Demo mode: made-up devices running inside Hallow Home. Nothing reaches your Home Assistant, cameras or feeder, and no alerts are sent. Leave it on the Setup page.", "Lilac", "LilacBg"));
            if (!c.Configured) { sp.Children.Add(Ui.Note("Hallow Home isn't connected to Home Assistant yet. The Setup page walks you through it (Home Assistant, cameras, Petlibro) and shows what's still missing - or try Demo mode to explore with made-up devices.", "Amber", "AmberBg")); sp.Children.Add(Wrap(Ui.Btn("Open setup", () => Show("setup"), true), Ui.Btn("Try Demo mode", () => { c.SetDemo(true); Show("dashboard"); }))); return sp; }
            if (!c.Connected) sp.Children.Add(Ui.Note(c.Connecting ? "Connecting to Home Assistant…" : "Can't reach Home Assistant: " + (c.LastError ?? "no answer") + " Hallow Home keeps trying.", c.Connecting ? "Lilac" : "Red", c.Connecting ? "LilacBg" : "RedBg"));
            return sp;
        }

        // ---- camera tiles: Home Assistant's MJPEG proxy (snap: a made-up frame)
        readonly Dictionary<string, CameraFeed> feeds = new Dictionary<string, CameraFeed>();
        readonly Dictionary<string, BitmapSource> lastFrame = new Dictionary<string, BitmapSource>();
        readonly Dictionary<string, PlayerView> players = new Dictionary<string, PlayerView>();
        void StopFeeds() { foreach (var f in feeds.Values) f.Stop(); feeds.Clear(); foreach (var p in players.Values) p.Stop(); players.Clear(); }
        public int RunningFeeds { get { return feeds.Count(f => !f.Value.Stopped) + players.Count(p => !p.Value.Stopped); } }
        public IEnumerable<PlayerView> Players { get { return players.Values; } }
        public IEnumerable<CameraFeed> Feeds { get { return feeds.Values; } }
        public bool Paused { get; private set; }
        public void SetPaused(bool p)
        {
            if (p == Paused) return; Paused = p;
            if (p) StopFeeds(); else BuildPage();
        }
        static TextBlock Overlay(string text) { return new TextBlock { Text = text, Foreground = Brushes.White, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(10), IsHitTestVisible = false }; }
        public PlayerView BigPlayer { get { return players.Where(kv => kv.Key.EndsWith("|big")).Select(kv => kv.Value).FirstOrDefault(); } }
        FrameworkElement Tile(Entity cam, HashSet<string> keep, double height, bool click)
        {
            var g = new Grid { Height = height, Background = Brushes.Black, ClipToBounds = true }; bool playerTile = false;
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
            else if (c.Game != null) g.Children.Add(Overlay("Paused while you play"));
            else if (Paused) g.Children.Add(Overlay("Paused - the window is hidden"));
            else
            {
                var kind = c.StreamFor(cam, camera == null);
                var url = kind == "hls" || kind == "webrtc" ? c.PlayerUrl(kind, cam) : null;
                if (url != null && Streams.RuntimeVersion() != null)
                {
                    // a bridge player (muted until you unmute it on the big view)
                    var key = cam.Id + "|" + kind + "|" + (camera != null ? "big" : "tile"); keep.Add(key);
                    PlayerView pv; if (!players.TryGetValue(key, out pv)) { pv = new PlayerView(url, kind); players[key] = pv; }
                    var old = pv.Parent as Panel; if (old != null) old.Children.Remove(pv);
                    g.Children.Insert(1, pv); playerTile = true;
                }
                else if (kind == "mjpeg" && c.Client != null && c.Connected)
                {
                    BitmapSource last; if (lastFrame.TryGetValue(cam.Id, out last)) img.Source = last;
                    keep.Add(cam.Id);
                    CameraFeed f;
                    if (!feeds.TryGetValue(cam.Id, out f)) { f = new CameraFeed(cam.Id); feeds[cam.Id] = f; f.Start(c.Client, camera != null ? 8 : 3); }
                    var id = cam.Id; var status = Overlay(f.Status == "Live" ? "" : f.Status); status.Visibility = f.Status == "Live" ? Visibility.Collapsed : Visibility.Visible; g.Children.Add(status);
                    f.Frame = bmp => Dispatcher.BeginInvoke(new Action(() => { lastFrame[id] = bmp; img.Source = bmp; }));
                    f.StatusChanged += st => Dispatcher.BeginInvoke(new Action(() => { status.Text = st == "Live" ? "" : st; status.Visibility = st == "Live" ? Visibility.Collapsed : Visibility.Visible; }));
                }
                else g.Children.Add(Overlay(kind == null ? "No picture source yet - connect Home Assistant or the bridge (Setup)" : "The bridge's players need Microsoft Edge WebView2"));
            }
            UIElement inner = g;
            if (playerTile)
            {
                // a browser control always draws on top (WPF's airspace rule): the name and the click target go in a bar under it
                g.Children.Remove(label); g.Height = Math.Max(60, height - 26);
                var dp = new DockPanel();
                var cap = new DockPanel { Background = new SolidColorBrush(Color.FromRgb(16, 16, 20)), Height = 26 };
                var src = new TextBlock { Text = (Streams.Label(c.StreamFor(cam, camera == null)) + (camera == null ? " · muted" : "")), Foreground = new SolidColorBrush(Color.FromRgb(190, 190, 200)), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
                DockPanel.SetDock(src, Dock.Right); cap.Children.Add(src);
                cap.Children.Add(new TextBlock { Text = cam.Label, Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) });
                DockPanel.SetDock(cap, Dock.Bottom); dp.Children.Add(cap); dp.Children.Add(g); inner = dp;
            }
            var b = new Border { Child = inner, BorderBrush = Ui.R("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3) };
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
            sp.Children.Add(KV("Fed today", f.FedTodayText(c.S.GramsPerPortion), null, null, 110));
            if (f.PlannedGrams != null && f.TodayGrams != null) sp.Children.Add(KV("", Bar(100.0 * f.TodayGrams.Value / Math.Max(1, f.PlannedGrams.Value), "Green"), 110));
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
            if (c.Feeder.Present) { var sc = ScheduleCard(); sc.Margin = new Thickness(0, 12, 0, 0); feederCol.Children.Add(sc); }
            if (c.Fountain.Present) { var rc = RemindersCard(); rc.Margin = new Thickness(0, 12, 0, 0); fountainCol.Children.Add(rc); var ch = IntakeChart(); ch.Margin = new Thickness(0, 12, 0, 0); fountainCol.Children.Add(ch); }
            sp.Children.Add(Columns(2, feederCol, fountainCol));
            sp.Children.Add(Ui.Muted("The water-intake hint compares today with your cat's own last 7 days. It's a nudge to keep an eye on them, not medical advice - call your vet if you're worried.", 11.5, 0));
            return Scroll(sp);
        }
        Border IntakeChart()
        {
            var sp = new StackPanel(); sp.Children.Add(Ui.Caps("Water intake, last 8 days", 10.5));
            var now = c.Now(); var days = Enumerable.Range(0, 8).Select(i => now.Date.AddDays(i - 7)).ToList();
            var vals = days.Select(d => { double v; if (d == now.Date && c.Fountain.IntakeToday != null) return c.Fountain.IntakeToday; return c.ShownHist.Intake.TryGetValue(History.Day(d), out v) ? v : (double?)null; }).ToList();
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

        // ---- 1.2: the feeder's own schedule (read from Home Assistant; changed through the integration's services, after a confirm)
        Border ScheduleCard()
        {
            var v = c.Feeder.Schedule; var sp = new StackPanel();
            sp.Children.Add(Head("calendar-days", "Feeding schedule", "The feeder's own plan, read from Home Assistant", null));
            if (!v.Present) { sp.Children.Add(Ui.Muted("No feeding schedule found. Newer versions of the community Petlibro integration list the feeder's plan on a \"Feeding schedule\" sensor - update it in HACS, or map the sensor on the Devices page.", 12)); return Ui.Card(sp); }
            if (!v.On) sp.Children.Add(Ui.Note("The feeder's schedule is switched off: no scheduled meals dispense.", "Amber", "AmberBg"));
            bool edit = c.ScheduleEditable == true && c.Connected;
            if (!edit) sp.Children.Add(Ui.Muted(c.ScheduleEditable == false ? "Read-only: the Petlibro integration in your Home Assistant doesn't offer schedule changes (newer versions do - update it in HACS). Change meals in the Petlibro app meanwhile." : "Read-only for now: Hallow Home couldn't ask Home Assistant which services it has.", 11.5, 6));
            if (v.Plans.Count == 0) sp.Children.Add(Ui.Muted("No meals scheduled.", 12));
            foreach (var p in v.Plans)
            {
                var pp = p; var d = new DockPanel { Margin = new Thickness(0, 5, 0, 1) };
                StackPanel btns = null;
                if (edit)
                {
                    btns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(54, 2, 0, 4) };
                    btns.Children.Add(Ui.Btn("Edit", () => AskPlan(pp)));
                    btns.Children.Add(Ui.Btn(pp.Enabled ? "Turn off" : "Turn on", () => ConfirmPlan(new PlanEdit { Kind = "toggle", PlanId = pp.Id, Enable = !pp.Enabled })));
                    btns.Children.Add(Ui.Btn("Delete", () => ConfirmPlan(new PlanEdit { Kind = "delete", PlanId = pp.Id })));
                }
                if (p.Today && p.Enabled && p.State != null)
                {
                    var st = p.State == "dispensed" ? Ui.Pill("Fed", "Green", "GreenBg") : p.State == "skipped" || p.State == "to_be_skipped" ? Ui.Pill("Skipped", "Amber", "AmberBg") : Ui.Pill("Today", "Lilac", "LilacBg");
                    st.Margin = new Thickness(0, 0, 8, 0); DockPanel.SetDock(st, Dock.Right); d.Children.Add(st);
                }
                var t = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
                t.Inlines.Add(new System.Windows.Documents.Run(p.Time + "  ") { FontFamily = Ui.Mono, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Ui.R(p.Enabled ? "Text" : "Faint") });
                t.Inlines.Add(new System.Windows.Documents.Run((p.Label.Length > 0 ? p.Label + " · " : "") + p.Portions + " portion" + (p.Portions == 1 ? "" : "s") + " (" + Read.Num(p.GramsOr(c.S.GramsPerPortion)) + " g) · " + p.DaysText + (p.Enabled ? "" : " · off")) { FontSize = 12, Foreground = Ui.R(p.Enabled ? "TextSoft" : "Faint") });
                d.Children.Add(t); sp.Children.Add(d); if (btns != null) sp.Children.Add(btns);
            }
            if (edit) { var add = Ui.Btn("Add a meal", () => AskPlan(null)); add.HorizontalAlignment = HorizontalAlignment.Left; add.Margin = new Thickness(0, 8, 0, 0); sp.Children.Add(add); }
            sp.Children.Add(Ui.Muted(edit ? "Changes go to the feeder through the Petlibro integration in Home Assistant - each one only after you confirm it." : "Hallow Home shows the plan the feeder follows; it can't change it here.", 11.5, 0));
            return Ui.Card(sp);
        }
        void AskPlan(FeedPlan p)
        {
            var e = Dialogs.AskPlan(this, c, p); if (e == null) return;
            ConfirmPlan(e);
        }
        public void ConfirmPlan(PlanEdit e)
        {
            var err = Schedule.Check(e, c.Feeder.Schedule, 48); if (err != null) { Say(err); return; }
            string title; var text = Schedule.Describe(e, c.Feeder.Schedule, c.S.GramsPerPortion, out title);
            if (!Dialogs.Confirm(this, title, text, e.Kind == "delete" ? "Delete" : e.Kind == "add" ? "Add" : "Change", e.Kind == "delete")) { Say("Nothing changed."); return; }
            var r = c.ScheduleChange(e, true, x => Say(x == null ? "Sent to the feeder - the schedule updates in a moment." : "Couldn't change it: " + x));
            if (r != null) Say(r);
        }
        // ---- 1.2: the fountain's reminders (the integration's days left, or your own interval) and the low-water alert
        Border RemindersCard()
        {
            var w = c.Fountain; var sp = new StackPanel();
            sp.Children.Add(Head("filter", "Reminders", "Filter, cleaning and water", null));
            foreach (var r in new[] { w.Filter, w.Cleaning })
            {
                var rr = r; var d = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
                if (r.Own) { var b = Ui.Btn(r.Kind == "filter" ? "Changed the filter today" : "Cleaned it today", () => { c.MarkDone(rr.Kind); Say(rr.Kind == "filter" ? "Filter reminder restarted - next in " + c.S.FilterEveryDays + " days." : "Cleaning reminder restarted - next in " + c.S.CleanEveryDays + " days."); }); DockPanel.SetDock(b, Dock.Right); d.Children.Add(b); }
                var t = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
                t.Inlines.Add(new System.Windows.Documents.Run((r.Kind == "filter" ? "Filter" : "Cleaning") + "  ") { FontSize = 12.5, Foreground = Ui.R("Muted") });
                t.Inlines.Add(new System.Windows.Documents.Run(r.Text ?? "-") { FontFamily = Ui.Mono, FontSize = 12.5, Foreground = Ui.R(r.Due ? "Amber" : "Text") });
                t.Inlines.Add(new System.Windows.Documents.Run("   " + (r.Source ?? "")) { FontSize = 11.5, Foreground = Ui.R("Muted") });
                d.Children.Add(t); sp.Children.Add(d);
            }
            var on = c.S.RuleOn("water-low");
            sp.Children.Add(KV("Low water", on ? "alert under " + c.S.WaterLowPct + "%" : "alert off", w.WaterLow ? "low now" : w.WaterText != null ? "now " + w.WaterText : null, w.WaterLow ? "Red" : null, 110));
            sp.Children.Add(Ui.Muted(w.Filter.Own || w.Cleaning.Own ? "Your own intervals (Settings > Pets) count from the day you press the button." : "From the fountain's own counters: reset them in the Petlibro app (or Home Assistant's reset buttons) after a change.", 11.5, 0));
            return Ui.Card(sp);
        }

        // ================= Cameras (1.2: 2x2 / 3x3 grid with pages, the source the bridge offers, full screen, mute)
        int gridPage;
        FrameworkElement CamerasPage(HashSet<string> keep)
        {
            bigFor = null;   // the big view starts muted again next time
            var sp = PageBody("Cameras", "Live while this page is open; paused while you play, and when the window is hidden or minimised. Click one for the big view, full screen, a snapshot and sound.");
            SetupNote(sp);
            var cams = c.Map.Cameras;
            if (cams.Count == 0) sp.Children.Add(Ui.Muted("No camera entities in Home Assistant yet. docker-wyze-bridge serves your Wyze cameras as RTSP / WebRTC / HLS; add each to HA (the Setup page, step 2).", 12.5));
            else
            {
                int n = c.S.GridSize == 3 ? 3 : 2, per = n * n, pages = (cams.Count + per - 1) / per;
                if (gridPage >= pages) gridPage = pages - 1; if (gridPage < 0) gridPage = 0;
                var online = cams.Count(x => x.Available);
                var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 8), LastChildFill = false };
                var chips = new StackPanel { Orientation = Orientation.Horizontal };
                chips.Children.Add(Chip("2×2", n == 2, () => { c.S.GridSize = 2; c.S.Save(); gridPage = 0; BuildPage(); }));
                chips.Children.Add(Chip("3×3", n == 3, () => { c.S.GridSize = 3; c.S.Save(); gridPage = 0; BuildPage(); }));
                if (pages > 1)
                {
                    var prev = Ui.Btn("Previous", () => { gridPage--; BuildPage(); }); prev.IsEnabled = gridPage > 0;
                    var next = Ui.Btn("Next", () => { gridPage++; BuildPage(); }); next.IsEnabled = gridPage < pages - 1;
                    chips.Children.Add(new Border { Width = 10 }); chips.Children.Add(prev); chips.Children.Add(Ui.Muted((gridPage + 1) + " / " + pages + "  ", 12)); chips.Children.Add(next);
                }
                DockPanel.SetDock(chips, Dock.Left); bar.Children.Add(chips);
                var info = Ui.Muted(online + " of " + cams.Count + " online · " + SourceSummary(), 12); info.VerticalAlignment = VerticalAlignment.Center; DockPanel.SetDock(info, Dock.Right); bar.Children.Add(info);
                sp.Children.Add(bar);
                var shown = cams.Skip(gridPage * per).Take(per).ToList();
                double h = n == 2 ? 250 : 165;
                sp.Children.Add(Columns(n, shown.Select(cam => (UIElement)Tile(cam, keep, h, true)).ToArray()));
            }
            sp.Children.Add(Section("Recordings"));
            sp.Children.Add(RecordingsBlock());
            return Scroll(sp);
        }
        string SourceSummary()
        {
            var g = c.StreamFor(null, true); var big = c.StreamFor(null, false);
            return "grid: " + Streams.Label(g) + (big != g ? ", big view: " + Streams.Label(big) : "") + (c.S.StreamMode == "auto" ? " (auto)" : "");
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
        bool bigMuted = true;   // the big view's sound: muted each time it opens
        string bigFor;
        FrameworkElement CameraPage(HashSet<string> keep)
        {
            Entity cam; c.Entities.TryGetValue(camera, out cam);
            if (cam == null) { camera = null; return CamerasPage(keep); }
            if (bigFor != camera) { bigFor = camera; bigMuted = true; }
            var kind = c.StreamFor(cam, false);
            var sp = PageBody(cam.Label, cam.Available ? "Live: " + Streams.Label(kind) + "." : "Offline since " + Fmt.Clock(cam.Changed, c.Now()) + ".");
            sp.Children.Add(Tile(cam, keep, 470, false));
            var pv = BigPlayer; if (pv != null) pv.SetMuted(bigMuted);
            var full = Ui.Btn("Full screen", () => OpenFull(cam), true); full.IsEnabled = cam.Available && kind != null;
            var snap = Ui.Btn("Snapshot", () => SaveSnapshot(cam)); snap.IsEnabled = cam.Available && (c.Connected || pv != null);
            var mute = Ui.Btn(bigMuted ? "Unmute" : "Mute", () => { bigMuted = !bigMuted; var p = BigPlayer; if (p != null) p.SetMuted(bigMuted); BuildPage(); });
            mute.IsEnabled = pv != null; mute.ToolTip = pv != null ? (bigMuted ? "Muted - click for the camera's sound" : "Sound on") : "MJPEG pictures have no sound - the bridge's WebRTC / HLS players do (Settings > Cameras)";
            var row = Wrap(Ui.Btn("All cameras", () => { camera = null; BuildPage(); }), full, snap, mute); row.Margin = new Thickness(0, 12, 0, 0); sp.Children.Add(row);
            sp.Children.Add(Ui.Muted("Snapshots go to Pictures\\Hallow Home. Sound starts muted every time. " + (c.BridgeWebRtc == null && c.BridgeHls == null ? "With docker-wyze-bridge's address (Setup, step 2) you get its WebRTC / HLS players - smoother, and with sound." : "Stream: " + SourceSummary() + "."), 12, 0));
            return Scroll(sp);
        }
        public FullView OpenFull(Entity cam, bool show = true)
        {
            var kind = c.StreamFor(cam, false); if (kind == null) { Say("Nothing can play this camera right now."); return null; }
            StopFeeds();   // the big view's stream gives way to the full-screen one
            var w = new FullView(c, cam, kind, c.PlayerUrl(kind, cam), show);
            if (show) { w.Owner = this; w.Closed += (s, e) => BuildPage(); w.Show(); }
            return w;
        }
        void SaveSnapshot(Entity cam) { Say("Taking a snapshot…"); c.Snapshot(cam, BigPlayer, msg => Say(msg)); }

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
                Ui.Btn("Send a test alert", () => { c.Deliver("Hallow Home test", "This is a test alert from Hallow Home. If you see it, alerts reach you here.", true, true, false, true); Say("Test alert sent to the channels that are on."); }));
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
            sp.Children.Add(Ui.Muted("Thresholds: food and water low under " + c.S.FoodLowPct + " / " + c.S.WaterLowPct + "%, pump stopped for " + c.S.PumpOffMinutes + " min, filter / cleaning with " + c.S.FilterDueDays + " days left, a camera offline for " + c.S.CameraOfflineMinutes + " min, a missed meal checked " + c.S.JamGraceMinutes + " min after its time, intake under " + c.S.IntakeLowPct + "% of usual (after 15:00, once 3 days are known). \"Away\" = everyone's person entity in Home Assistant is away, or \"I'm away\" below. Hallow Places can tell Hallow Home later.", 11.5, 4));
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

        // ================= Setup (1.2): the guided setup - three skippable steps that show what's still missing, and Demo mode
        string wizUrl, wizUrlResult, wizTokenResult; bool wizUrlOk, wizBusy; string wizTz; bool? wizTokenOk;
        FrameworkElement SetupPage()
        {
            var sp = PageBody("Setup", "Three steps that get Hallow Home talking to Home Assistant on your NAS. Each can be skipped and comes back later; the list shows what's still missing. Nothing here changes the NAS - you do that part, with the exact clicks below.");
            var demo = new StackPanel();
            demo.Children.Add(Head("flask", "Demo mode", "Explore Hallow Home now, with made-up devices", c.S.Demo ? "Lilac|On" : null));
            demo.Children.Add(Ui.Muted("A pretend feeder, fountain and four cameras run inside Hallow Home on this PC (localhost only). Nothing reaches your Home Assistant, cameras, feeder or accounts, and no alerts are sent. Your settings and token stay as they are.", 12, 8));
            demo.Children.Add(Wrap(c.S.Demo ? Ui.Btn("Leave Demo mode", () => { c.SetDemo(false); Say("Demo mode off."); BuildPage(); }, true) : Ui.Btn("Try Demo mode", () => { c.SetDemo(true); Say("Demo mode on - made-up devices."); BuildPage(); }, true)));
            var dc = Ui.Card(demo); dc.Margin = new Thickness(0, 0, 0, 14); sp.Children.Add(dc);

            var steps = Wizard.Steps(c);
            var list = new StackPanel();
            foreach (var s in steps)
            {
                var d = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                var ic = Icons.Make(s.Done ? "circle-check" : "circle", 14, Ui.R(s.Done ? "Green" : s.Skipped ? "Faint" : "Amber")); ic.Margin = new Thickness(0, 2, 10, 0); ic.VerticalAlignment = VerticalAlignment.Top; d.Children.Add(ic);
                d.Children.Add(Ui.T(s.Title + ": " + (s.Done ? "done" : (s.Skipped ? "skipped - " : "") + "still missing " + string.Join("; ", s.Missing)), 12.5, false, Ui.R(s.Done ? "Text" : "TextSoft")));
                list.Children.Add(d);
            }
            sp.Children.Add(Ui.Card(list));
            for (int i = 0; i < steps.Count; i++)
            {
                var s = steps[i]; sp.Children.Add(Section("Step " + (i + 1) + " · " + s.Title));
                if (s.Skipped)
                {
                    var id = s.Id; sp.Children.Add(Ui.Muted("Skipped. Still missing: " + string.Join("; ", s.Missing) + ".", 12.5, 4));
                    sp.Children.Add(Wrap(Ui.Btn("Show this step", () => { c.S.WizardSkipped.Remove(id); c.S.Save(); BuildPage(); })));
                    continue;
                }
                sp.Children.Add(s.Id == "ha" ? StepHa(s) : s.Id == "cameras" ? StepCameras(s) : StepPetlibro(s));
                if (!s.Done) { var id = s.Id; var sk = Ui.Btn("Skip this step", () => { if (!c.S.WizardSkipped.Contains(id)) c.S.WizardSkipped.Add(id); c.S.Save(); BuildPage(); }); sk.HorizontalAlignment = HorizontalAlignment.Left; sk.Margin = new Thickness(0, 8, 0, 0); sp.Children.Add(sk); }
            }
            return Scroll(sp);
        }
        static UIElement Numbered(IEnumerable<string> lines)
        {
            var sp = new StackPanel(); int n = 0;
            foreach (var l in lines)
            {
                var sub = l.StartsWith("    "); if (!sub) n++;
                var d = new DockPanel { Margin = new Thickness(sub ? 22 : 0, 2, 0, 2) };
                var num = Ui.T(sub ? "" : n + ".", 12.5, true, Ui.R("Lilac")); num.Width = 22; d.Children.Add(num);
                var t = Ui.T(l.Trim(), 12.5, false, Ui.R("TextSoft")); if (sub) t.FontFamily = Ui.Mono; d.Children.Add(t);
                sp.Children.Add(d);
            }
            return sp;
        }
        UIElement StepHa(WizStep s)
        {
            var sp = new StackPanel();
            if (c.S.Demo) sp.Children.Add(Ui.Muted("Demo mode is on: this step talks to the real Home Assistant you type in - leave Demo mode afterwards to use it.", 11.5, 6));
            sp.Children.Add(Ui.Muted("a) Paste Home Assistant's address and test it - no token needed yet.", 12.5, 4));
            var url = Ui.Field(wizUrl ?? c.S.HaUrl ?? "http://nas:8123", 320);
            var test = Ui.Btn("Test connection", null);
            test.Click += (o, e) =>
            {
                wizUrl = url.Text; var u = url.Text; wizBusy = true; wizUrlResult = "Testing…"; BuildPage();
                ThreadPool.QueueUserWorkItem(_ => { string r; bool ok; try { r = Ha.Probe(u); ok = true; } catch (Exception x) { r = x.Message; ok = false; } Dispatcher.BeginInvoke(new Action(() => { wizBusy = false; wizUrlOk = ok; wizUrlResult = r; BuildPage(); })); });
            };
            test.IsEnabled = !wizBusy;
            sp.Children.Add(Wrap(url, new Border { Width = 8 }, test));
            if (wizUrlResult != null) sp.Children.Add(Ui.T(wizUrlResult, 12.5, false, Ui.R(wizBusy ? "Muted" : wizUrlOk ? "Green" : "Red"), 4));
            if (Ha.InsecureRemote(Ha.NormalizeUrl(url.Text))) sp.Children.Add(Ui.Note("That address is plain http over the internet: the token would travel unencrypted. Use your home network's address (or Tailscale), or https.", "Red", "RedBg"));
            sp.Children.Add(Ui.Muted("b) Make a long-lived access token in Home Assistant - the exact clicks:", 12.5, 4));
            sp.Children.Add(Numbered(Wizard.TokenSteps));
            var hu = Ha.NormalizeUrl(url.Text);
            if (hu != null) { var open = Ui.Btn("Open Home Assistant's profile page", () => { try { Process.Start(new ProcessStartInfo(hu + "/profile/security") { UseShellExecute = true }); } catch { } }); open.HorizontalAlignment = HorizontalAlignment.Left; open.Margin = new Thickness(22, 4, 0, 6); sp.Children.Add(open); }
            sp.Children.Add(Ui.Muted("c) Paste the token and test again. It's kept encrypted for your Windows user only, never shown or logged.", 12.5, 4));
            var tok = new PasswordBox { Width = 320, MinHeight = 30, Padding = new Thickness(8, 5, 8, 5), Background = Ui.R("Well"), Foreground = Ui.R("Text"), BorderBrush = Ui.R("Line"), CaretBrush = Ui.R("AccentBrush"), FontFamily = Ui.Mono };
            var again = Ui.Btn("Test again", null);
            again.Click += (o, e) =>
            {
                var u = url.Text; var t = tok.Password; tok.Clear();
                if (t.Length == 0 && !SecretStore.Has(SecretStore.Token)) { wizTokenOk = false; wizTokenResult = "Paste the token first."; BuildPage(); return; }
                var tokenToTest = t.Length > 0 ? t : SecretStore.Get(SecretStore.Token);
                wizBusy = true; wizTokenResult = "Testing…"; wizTokenOk = null; BuildPage();
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    string r; bool ok; WizardResult(u, tokenToTest, out r, out ok);
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        wizBusy = false; wizTokenOk = ok; wizTokenResult = r;
                        if (ok) { var err = c.SetConnection(u, t.Length > 0 ? t : null); if (err != null) { wizTokenOk = false; wizTokenResult = err; } else wizTokenResult += c.S.Demo ? " Saved - leave Demo mode to use it." : " Saved."; }
                        BuildPage();
                    }));
                });
            };
            again.IsEnabled = !wizBusy;
            sp.Children.Add(Wrap(tok, new Border { Width = 8 }, again));
            if (wizTokenResult != null) sp.Children.Add(Ui.T(wizTokenResult, 12.5, false, Ui.R(wizTokenOk == true ? "Green" : wizTokenOk == false ? "Red" : "Muted"), 4));
            else if (s.Done) sp.Children.Add(Ui.T("Connected" + (c.HaVersion != null ? " to Home Assistant " + c.HaVersion : "") + ".", 12.5, false, Ui.R("Green"), 4));
            return Ui.Card(sp);
        }
        // the wizard's second test: the token works, and what Home Assistant has (feeder, fountain, cameras)
        public static void WizardResult(string url, string token, out string text, out bool ok)
        {
            try
            {
                var h = new Ha(url, token); h.Check(); var ver = h.Version(); var n = h.States(); var m = Roles.Build(n, null);
                text = "Connected to Home Assistant" + (ver != null ? " " + ver : "") + ": " + n.Count + " entities - " + (m.HasFeeder ? "feeder found" : "no feeder yet") + ", " + (m.HasFountain ? "fountain found" : "no fountain yet") + ", " + m.Cameras.Count + " camera" + (m.Cameras.Count == 1 ? "" : "s") + ".";
                ok = true;
            }
            catch (Exception x) { text = x.Message; ok = false; }
        }
        UIElement StepCameras(WizStep s)
        {
            var sp = new StackPanel(); var host = Wizard.NasHost(c.S.HaUrl);
            sp.Children.Add(Ui.Muted("docker-wyze-bridge turns each Wyze camera into streams on the NAS. This makes its docker-compose snippet for Synology Container Manager. The <REPLACE...> lines are for the NAS only: Hallow Home never asks for your Wyze password or API keys.", 12.5, 8));
            var folder = Ui.Field(c.S.NasFolder, 300);
            if (wizTz == null) wizTz = Wizard.IanaZone(TimeZoneInfo.Local.Id) ?? "";
            var tz = Ui.Field(wizTz, 200);
            sp.Children.Add(KV("Folder on the NAS", folder, 150)); sp.Children.Add(KV("Time zone", tz, 150));
            sp.Children.Add(Ui.Switch("Record every camera on the NAS (RECORD_ALL - uses disk)", c.S.BridgeRecord, on => { c.S.BridgeRecord = on; c.S.Save(); BuildPage(); }));
            var o = new Wizard.ComposeOptions { Folder = c.S.NasFolder, Tz = wizTz.Length > 0 ? wizTz : null, Record = c.S.BridgeRecord, NasHost = host };
            var yaml = Wizard.Compose(o);
            var box = new TextBox { Text = yaml, IsReadOnly = true, FontFamily = Ui.Mono, FontSize = 11.5, TextWrapping = TextWrapping.NoWrap, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Background = Ui.R("Well"), Foreground = Ui.R("Text"), BorderBrush = Ui.R("Line"), Padding = new Thickness(10), Margin = new Thickness(0, 8, 0, 6), MaxHeight = 380, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            sp.Children.Add(box);
            sp.Children.Add(Wrap(Ui.Btn("Copy", () => { try { Clipboard.SetText(yaml.Replace("\n", "\r\n")); Say("Copied - paste it in Container Manager (Project > Create)."); } catch { Say("Couldn't reach the clipboard - select the text and copy it."); } }, true),
                Ui.Btn("Update the snippet", () => { var f = folder.Text.Trim(); c.S.NasFolder = f; c.S.Fix(); c.S.Save(); wizTz = tz.Text.Trim(); if (c.S.NasFolder != f.TrimEnd('/')) Say("The folder should look like /volume1/docker/wyze-bridge."); BuildPage(); })));
            sp.Children.Add(Ui.Muted("Then:", 12.5, 2)); sp.Children.Add(Numbered(Wizard.CameraSteps(host)));
            sp.Children.Add(Ui.Muted("The bridge's players for Hallow Home (filled in from the NAS's name; change them if yours differ):", 12.5, 4));
            var rtc = Ui.Field(c.S.BridgeWebRtc ?? "http://" + host + ":8889", 260); var hls = Ui.Field(c.S.BridgeHls ?? "http://" + host + ":8888", 260);
            sp.Children.Add(KV("WebRTC", rtc, 150)); sp.Children.Add(KV("HLS", hls, 150));
            sp.Children.Add(Wrap(Ui.Btn("Test the bridge", () => { c.S.BridgeWebRtc = rtc.Text; c.S.BridgeHls = hls.Text; c.S.Fix(); c.S.Save(); Say("Asking the bridge…"); c.ProbeBridge(() => { Say(c.BridgeWebRtcOk == true || c.BridgeHlsOk == true ? "The bridge answered." : "No answer from the bridge yet (is it running, and is there a camera in Home Assistant?)."); BuildPage(); }); BuildPage(); })));
            if (c.BridgeProbing) sp.Children.Add(Ui.Muted("Asking the bridge…", 12, 0));
            else if (c.BridgeWebRtcOk != null || c.BridgeHlsOk != null) sp.Children.Add(Ui.T("WebRTC: " + (c.BridgeWebRtcOk == true ? "yes" : "no") + "   HLS: " + (c.BridgeHlsOk == true ? "yes" : "no") + (c.S.Demo ? "   (Demo mode: the made-up bridge)" : ""), 12.5, false, Ui.R(c.BridgeWebRtcOk == true || c.BridgeHlsOk == true ? "Green" : "Red"), 4));
            return Ui.Card(sp);
        }
        UIElement StepPetlibro(WizStep s)
        {
            var sp = new StackPanel();
            sp.Children.Add(Ui.Muted("The feeder and the fountain come into Home Assistant through the community Petlibro integration (HACS - unofficial, not made by Petlibro):", 12.5, 6));
            sp.Children.Add(Numbered(Wizard.PetlibroSteps));
            sp.Children.Add(Wrap(Ui.Btn("Copy the repository address", () => { try { Clipboard.SetText(Wizard.PetlibroRepo); Say("Copied " + Wizard.PetlibroRepo); } catch { } })));
            var found = new List<string>();
            found.Add(c.Feeder.Present ? "feeder found" : "no feeder yet"); found.Add(c.Fountain.Present ? "fountain found" : "no fountain yet");
            if (c.Feeder.Present) found.Add(c.Feeder.Schedule.Present ? "feeding schedule found (" + c.Feeder.Schedule.Plans.Count + " meals" + (c.ScheduleEditable == true ? ", editable" : c.ScheduleEditable == false ? ", read-only" : "") + ")" : "no feeding schedule sensor");
            sp.Children.Add(Ui.T(c.Connected ? "In Home Assistant now: " + string.Join(", ", found) + "." : "Connect Home Assistant (step 1) to see what it found.", 12.5, false, Ui.R(s.Done ? "Green" : "Muted"), 6));
            return Ui.Card(sp);
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
            var rtc = Ui.Field(c.S.BridgeWebRtc ?? "", 360); var hlsF = Ui.Field(c.S.BridgeHls ?? "", 360);
            sp.Children.Add(KV("Bridge WebRTC", rtc)); sp.Children.Add(KV("Bridge HLS", hlsF));
            sp.Children.Add(KV("", Ui.Muted("Optional: docker-wyze-bridge's players on your network - WebRTC http://nas:8889, HLS http://nas:8888. Keep them off the internet.", 11.5)));
            var rec = Ui.Field(c.S.RecordingsFolder ?? "", 360);
            sp.Children.Add(KV("Recordings folder", rec));
            sp.Children.Add(Wrap(Ui.Btn("Save", () =>
            {
                var a = rtc.Text.Trim(); var b = hlsF.Text.Trim(); c.S.BridgeWebRtc = a; c.S.BridgeHls = b; c.S.RecordingsFolder = rec.Text.Trim().Length == 0 ? null : rec.Text.Trim(); c.S.Fix(); c.S.Save(); c.ProbeBridge(() => BuildPage());
                Say((a.Length > 0 && c.S.BridgeWebRtc == null) || (b.Length > 0 && c.S.BridgeHls == null) ? "A bridge address should look like http://nas:8889." : "Saved."); BuildPage();
            })));
            var modes = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) }; modes.Children.Add(Ui.Muted("Picture source   ", 12.5));
            foreach (var m in new[] { new[] { "auto", "Auto (what the bridge offers)" }, new[] { "webrtc", "WebRTC" }, new[] { "hls", "HLS" }, new[] { "mjpeg", "Home Assistant (MJPEG)" } }) { var mm = m[0]; modes.Children.Add(Chip(m[1], c.S.StreamMode == mm, () => { c.S.StreamMode = mm; c.S.Save(); BuildPage(); })); }
            sp.Children.Add(modes);
            sp.Children.Add(Ui.Muted("Auto: the grid uses Home Assistant's light MJPEG pictures; the big view and full screen use the bridge's WebRTC, else HLS, else MJPEG. Players always start muted. Now: " + SourceSummary() + ".", 11.5, 4));

            sp.Children.Add(Section("Pets"));
            var gpp = Ui.Field(Read.Num(c.S.GramsPerPortion), 70); var maxp = Ui.Field(c.S.MaxPortions.ToString(), 70);
            sp.Children.Add(Wrap(Ui.Muted("One portion is ", 12.5), gpp, Ui.Muted(" g   ·   Feed now offers at most ", 12.5), maxp, Ui.Muted(" portions  ", 12.5), Ui.Btn("Save", () =>
            {
                double g; int m;
                if (!double.TryParse(gpp.Text.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out g) || g < 1 || g > 100 || !int.TryParse(maxp.Text.Trim(), out m) || m < 1 || m > 12) { Say("Grams 1-100, portions 1-12."); return; }
                c.S.GramsPerPortion = g; c.S.MaxPortions = m; c.S.Save(); c.Recompute(); Say("Saved.");
            })));
            var fe = Ui.Field(c.S.FilterEveryDays.ToString(), 60); var ce = Ui.Field(c.S.CleanEveryDays.ToString(), 60);
            sp.Children.Add(Wrap(Ui.Muted("Fountain: change the filter every ", 12.5), fe, Ui.Muted(" days, clean it every ", 12.5), ce, Ui.Muted(" days  ", 12.5), Ui.Btn("Save", () =>
            {
                int a, b; if (!int.TryParse(fe.Text.Trim(), out a) || a < 1 || a > 180 || !int.TryParse(ce.Text.Trim(), out b) || b < 1 || b > 90) { Say("Filter 1-180 days, cleaning 1-90 days."); return; }
                c.S.FilterEveryDays = a; c.S.CleanEveryDays = b; c.S.Save(); c.Recompute(); Say("Saved.");
            })));
            sp.Children.Add(Ui.Switch("Use my own intervals even when the fountain has its own filter / cleaning counters", c.S.OwnReminders, on => { c.S.OwnReminders = on; c.S.Save(); c.Recompute(); }));
            sp.Children.Add(Ui.Muted("Without the integration's counters Hallow Home uses these intervals, counted from the day you press \"Changed the filter today\" / \"Cleaned it today\" on the Pets page.", 11.5, 4));

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
