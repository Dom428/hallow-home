// Hallow Home 1.2 - one camera full screen: a borderless window over the whole screen with the camera's name, the source, a
// snapshot button and a mute toggle (muted when it opens). Esc or "Exit full screen" closes it. Minimised = the stream stops.
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HallowHome
{
    public class FullView : Window
    {
        readonly Controller c; public readonly Entity Cam; public readonly string Kind, Url;
        public PlayerView Player; public CameraFeed Feed; public bool Muted = true;
        readonly Grid stage; readonly Image img; readonly TextBlock status; Button muteBtn;
        public FullView(Controller controller, Entity cam, string kind, string url, bool fullScreen = true)
        {
            c = controller; Cam = cam; Kind = kind; Url = url;
            Title = cam.Label + " - Hallow Home"; Background = Brushes.Black; Foreground = Brushes.White; FontFamily = (FontFamily)Application.Current.FindResource("BodyFont");
            try { Icon = Ui.AppIcon(); } catch { }
            if (fullScreen) { WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; WindowState = WindowState.Maximized; }
            else { Width = 960; Height = 600; }
            var root = new DockPanel();
            var bar = new DockPanel { Background = new SolidColorBrush(Color.FromRgb(16, 16, 20)), Height = 44 };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            buttons.Children.Add(Ui.Btn("Snapshot", () => c.Snapshot(Cam, Player, msg => status.Text = msg)));
            muteBtn = Ui.Btn("Unmute", ToggleMute); buttons.Children.Add(muteBtn);
            buttons.Children.Add(Ui.Btn("Exit full screen", Close, true));
            DockPanel.SetDock(buttons, Dock.Right); bar.Children.Add(buttons);
            var name = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
            name.Inlines.Add(new System.Windows.Documents.Run(cam.Label) { FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White });
            name.Inlines.Add(new System.Windows.Documents.Run("   " + Streams.Label(kind)) { FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(190, 190, 200)) });
            status = new TextBlock { FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(190, 190, 200)), Margin = new Thickness(20, 0, 0, 0) };
            name.Inlines.Add(new System.Windows.Documents.LineBreak()); name.Inlines.Add(new System.Windows.Documents.InlineUIContainer(status));
            bar.Children.Add(name);
            DockPanel.SetDock(bar, Dock.Top); root.Children.Add(bar);
            stage = new Grid { Background = Brushes.Black }; img = new Image { Stretch = Stretch.Uniform }; stage.Children.Add(img);
            root.Children.Add(stage); Content = root;
            KeyDown += (s, e) => { if (e.Key == Key.Escape) Close(); };
            StateChanged += (s, e) => { if (WindowState == WindowState.Minimized) StopStream(); else StartStream(); };
            Closed += (s, e) => StopStream();
            StartStream();
            UpdateMute();
        }
        public void StartStream()
        {
            if (Player != null || Feed != null) return;
            if ((Kind == "hls" || Kind == "webrtc") && Url != null)
            {
                Player = new PlayerView(Url, Kind); Player.Muted = Muted; stage.Children.Add(Player);
                Player.StatusChanged += st => status.Text = st == "Live" ? "" : st;
            }
            else if (c.Client != null)
            {
                Feed = new CameraFeed(Cam.Id);
                Feed.Frame = bmp => Dispatcher.BeginInvoke(new Action(() => img.Source = bmp));
                Feed.StatusChanged += st => Dispatcher.BeginInvoke(new Action(() => status.Text = st == "Live" ? "" : st));
                Feed.Start(c.Client, 12);
            }
            UpdateMute();
        }
        public void StopStream()
        {
            if (Player != null) { Player.Stop(); stage.Children.Remove(Player); Player = null; }
            if (Feed != null) { Feed.Stop(); Feed = null; }
        }
        public void ToggleMute() { Muted = !Muted; if (Player != null) Player.SetMuted(Muted); UpdateMute(); }
        void UpdateMute()
        {
            if (muteBtn == null) return;
            muteBtn.Content = Muted ? "Unmute" : "Mute"; muteBtn.IsEnabled = Kind == "hls" || Kind == "webrtc";
            muteBtn.ToolTip = muteBtn.IsEnabled ? (Muted ? "Muted - click for the camera's sound" : "Sound on") : "MJPEG pictures have no sound";
        }
    }
}
