// Hallow Home 1.2 - where a camera's picture comes from, and the players.
//   mjpeg   Home Assistant's camera proxy (/api/camera_proxy_stream/<camera>): decoded here, no browser, the token in a header.
//   webrtc  docker-wyze-bridge's WebRTC player (MediaMTX, http://nas:8889/<camera>/) in a WebView2 control.
//   hls     docker-wyze-bridge's HLS player (MediaMTX, http://nas:8888/<camera>/) in a WebView2 control.
// "Auto" uses what the bridge offers: it asks the bridge once (index.m3u8 for HLS, the player page for WebRTC) and picks
// WebRTC, then HLS, then Home Assistant's MJPEG - except that the grid prefers MJPEG when Home Assistant is there (lighter: one
// small picture per tile, no browser per tile). Every player starts MUTED (CoreWebView2.IsMuted is set before it navigates);
// the mute toggle is per view and never saved. Players and streams reconnect with a growing pause (Backoff) and stop when the
// window is hidden or minimised.
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace HallowHome
{
    public static class Streams
    {
        public static double BackoffScale = 1;   // the tests shrink the pauses
        // 2, 4, 8, 16, then 30 seconds between tries
        public static TimeSpan Backoff(int fails) { int s = Math.Min(30, 2 << Math.Min(4, Math.Max(0, fails))); return TimeSpan.FromMilliseconds(s * 1000.0 * BackoffScale); }
        public static string Label(string kind) { return kind == "webrtc" ? "WebRTC (docker-wyze-bridge)" : kind == "hls" ? "HLS (docker-wyze-bridge)" : kind == "mjpeg" ? "MJPEG (Home Assistant)" : "none"; }

        // which source to use; null = nothing can play this camera now
        public static string Pick(string mode, bool forGrid, bool haOk, bool? hlsOffered, bool? webrtcOffered, bool hasHls, bool hasWebRtc)
        {
            bool hls = hasHls && hlsOffered != false, rtc = hasWebRtc && webrtcOffered != false;
            switch (mode)
            {
                case "mjpeg": return haOk ? "mjpeg" : null;
                case "hls": return hls ? "hls" : haOk ? "mjpeg" : null;
                case "webrtc": return rtc ? "webrtc" : haOk ? "mjpeg" : null;
            }
            if (forGrid && haOk) return "mjpeg";
            if (hasWebRtc && webrtcOffered == true) return "webrtc";
            if (hasHls && hlsOffered == true) return "hls";
            if (haOk) return "mjpeg";
            return rtc ? "webrtc" : hls ? "hls" : null;
        }
        public static string PlayerUrl(string kind, string hlsBase, string webrtcBase, Entity cam)
        {
            var b = kind == "hls" ? hlsBase : kind == "webrtc" ? webrtcBase : null;
            return b == null || cam == null ? null : b.TrimEnd('/') + "/" + Cams.BridgeName(cam) + "/";
        }
        static string GetText(string url, int ms, out int code)
        {
            code = 0;
            try
            {
                var r = (HttpWebRequest)WebRequest.Create(url); r.Timeout = ms; r.ReadWriteTimeout = ms; r.Proxy = null; r.AllowAutoRedirect = false; r.UserAgent = "HallowHome";
                using (var resp = (HttpWebResponse)r.GetResponse()) using (var s = new StreamReader(resp.GetResponseStream())) { code = (int)resp.StatusCode; var buf = new char[4096]; int n = s.Read(buf, 0, buf.Length); return new string(buf, 0, Math.Max(0, n)); }
            }
            catch (WebException e) { var hr = e.Response as HttpWebResponse; if (hr != null) { code = (int)hr.StatusCode; hr.Close(); } return null; }
            catch { return null; }
        }
        // does the bridge offer HLS / WebRTC for this camera? (null = nothing answered at all)
        public static bool? ProbeHls(string hlsBase, string camName, int ms = 4000)
        {
            if (hlsBase == null || camName == null) return null; int code;
            var t = GetText(hlsBase.TrimEnd('/') + "/" + camName + "/index.m3u8", ms, out code);
            if (code == 0) return null; return code == 200 && t != null && t.TrimStart().StartsWith("#EXTM3U");
        }
        public static bool? ProbeWebRtc(string webrtcBase, string camName, int ms = 4000)
        {
            if (webrtcBase == null || camName == null) return null; int code;
            var t = GetText(webrtcBase.TrimEnd('/') + "/" + camName + "/", ms, out code);
            if (code == 0) return null; return code == 200 && t != null && t.IndexOf("<html", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // ---- WebView2 (the Evergreen runtime that comes with Windows 11; Hallow Home never downloads it)
        public static string RuntimeVersion() { try { return CoreWebView2Environment.GetAvailableBrowserVersionString(); } catch { return null; } }
        public static string DataFolder { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), HomeSettings.TestDir != null ? "HallowHomeTest" : "HallowHome", "WebView2"); } }
        static Task<CoreWebView2Environment> env;
        // one browser environment for every player; the tests' players are muted at the engine too (--mute-audio)
        public static Task<CoreWebView2Environment> Env()
        {
            if (env == null) env = CoreWebView2Environment.CreateAsync(null, DataFolder, HomeSettings.TestDir != null ? new CoreWebView2EnvironmentOptions("--mute-audio") : null);
            return env;
        }
    }

    // a bridge player (WebRTC or HLS page) in WebView2: muted until you unmute it, same-origin only, reconnects with back-off
    public class PlayerView : Grid
    {
        public readonly WebView2 View; public readonly string Url, Kind; readonly Uri start;
        public bool Muted = true, MutedBeforeNavigate, Playing; public int Attempts, Fails; public string Status = "Connecting…";
        public event Action<string> StatusChanged;
        readonly TextBlock overlay; DispatcherTimer retry; bool stopped, inited;
        public PlayerView(string url, string kind)
        {
            Url = url; Kind = kind; start = new Uri(url); Background = Brushes.Black; ClipToBounds = true;
            View = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.Black };
            Children.Add(View);
            overlay = new TextBlock { Foreground = Brushes.White, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(12), IsHitTestVisible = false };
            Children.Add(overlay); Say(Status);
            Loaded += (s, e) => Init();
        }
        void Say(string s) { Status = s; overlay.Text = s == "Live" ? "" : s; overlay.Visibility = s == "Live" ? Visibility.Collapsed : Visibility.Visible; var h = StatusChanged; if (h != null) h(s); }
        async void Init()
        {
            if (inited || stopped) return; inited = true;
            try
            {
                var e = await Streams.Env(); if (stopped) return;
                await View.EnsureCoreWebView2Async(e); if (stopped) return;
                var core = View.CoreWebView2;
                core.IsMuted = Muted; MutedBeforeNavigate = core.IsMuted;   // muted BEFORE anything plays
                core.Settings.AreDevToolsEnabled = false; core.Settings.IsStatusBarEnabled = false; core.Settings.AreDefaultContextMenusEnabled = false;
                core.NewWindowRequested += (o, a) => { a.Handled = true; };
                core.NavigationStarting += (o, a) => { Uri u; if (Uri.TryCreate(a.Uri, UriKind.Absolute, out u) && !SameOrigin(u)) a.Cancel = true; };
                core.NavigationCompleted += (o, a) => { if (stopped) return; if (a.IsSuccess) { Fails = 0; Playing = true; Say("Live"); } else { Playing = false; Retry(); } };
                Go();
            }
            catch (Exception ex) { Say("The player needs Microsoft Edge WebView2 (built into Windows 11). " + Family.OneLine(ex.Message)); }
        }
        bool SameOrigin(Uri u) { return u.Scheme == "about" || u.Scheme == "data" || (u.Host == start.Host && u.Port == start.Port && u.Scheme == start.Scheme); }
        void Go() { if (stopped || View.CoreWebView2 == null) return; Attempts++; Say(Fails == 0 ? "Connecting…" : "Reconnecting…"); try { View.CoreWebView2.Navigate(Url); } catch { Retry(); } }
        void Retry()
        {
            var w = Streams.Backoff(Fails++);
            Say("Can't reach the bridge's player - reconnecting in " + Math.Max(1, (int)Math.Round(w.TotalSeconds)) + " s");
            if (retry != null) retry.Stop();
            retry = new DispatcherTimer { Interval = w }; retry.Tick += (s, e) => { retry.Stop(); Go(); }; retry.Start();
        }
        public void SetMuted(bool m) { Muted = m; try { if (View.CoreWebView2 != null) View.CoreWebView2.IsMuted = m; } catch { } }
        public bool EngineMuted { get { try { return View.CoreWebView2 != null && View.CoreWebView2.IsMuted; } catch { return false; } } }
        public void Stop() { stopped = true; if (retry != null) retry.Stop(); try { View.Dispose(); } catch { } }
        public bool Stopped { get { return stopped; } }
        public async Task<byte[]> Capture()
        {
            if (View.CoreWebView2 == null) return null;
            using (var ms = new MemoryStream()) { await View.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms); return ms.ToArray(); }
        }
    }
}
