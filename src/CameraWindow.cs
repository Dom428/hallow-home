// Hallow Home - the live view: docker-wyze-bridge's own WebRTC (or HLS) player page for one camera, in Microsoft Edge WebView2
// (the Evergreen runtime that comes with Windows 11; Hallow Home never downloads it). Only the bridge address you set in Settings
// is opened - links elsewhere open in your normal browser instead. WebView2's data goes to %LOCALAPPDATA%\HallowHome\WebView2.
using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace HallowHome
{
    public class CameraWindow : Window
    {
        public static string RuntimeVersion()
        {
            try { return CoreWebView2Environment.GetAvailableBrowserVersionString(); } catch { return null; }
        }
        public static string DataFolder { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), HomeSettings.TestDir != null ? "HallowHomeTest" : "HallowHome", "WebView2"); } }

        public readonly WebView2 View;
        readonly Uri start;
        public event Action<bool> Loaded2;   // navigation finished (ok?)
        public CameraWindow(string title, string url)
        {
            Ui.Dress(this, title + " - Hallow Home", 960); SizeToContent = SizeToContent.Manual; Height = 600;
            start = new Uri(url);
            var dock = new DockPanel();
            var bar = new DockPanel { Background = Ui.R("Side"), Height = 30 };
            var t = Ui.Muted("Live view from docker-wyze-bridge · " + start.Host, 11.5); t.VerticalAlignment = VerticalAlignment.Center; t.Margin = new Thickness(12, 0, 0, 0); bar.Children.Add(t);
            DockPanel.SetDock(bar, Dock.Top); dock.Children.Add(bar);
            View = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.Black };
            dock.Children.Add(View); Content = dock;
            Loaded += async (s, e) =>
            {
                try
                {
                    var env = await CoreWebView2Environment.CreateAsync(null, DataFolder);
                    await View.EnsureCoreWebView2Async(env);
                    View.CoreWebView2.Settings.AreDevToolsEnabled = false; View.CoreWebView2.Settings.IsStatusBarEnabled = false;
                    View.CoreWebView2.NewWindowRequested += (o, a) => { a.Handled = true; Outside(a.Uri); };
                    View.CoreWebView2.NavigationStarting += (o, a) => { Uri u; if (Uri.TryCreate(a.Uri, UriKind.Absolute, out u) && !SameOrigin(u)) { a.Cancel = true; Outside(a.Uri); } };
                    View.CoreWebView2.NavigationCompleted += (o, a) => { var h = Loaded2; if (h != null) h(a.IsSuccess); };
                    View.CoreWebView2.Navigate(start.ToString());
                }
                catch (Exception ex)
                {
                    dock.Children.Remove(View);
                    var msg = new StackPanel { Margin = new Thickness(24) };
                    msg.Children.Add(Ui.T("The live view needs Microsoft Edge WebView2", 15, true, null, 6));
                    msg.Children.Add(Ui.Muted("Windows 11 has it built in; on Windows 10 install \"Microsoft Edge WebView2 Runtime\" from Microsoft. The camera grid works without it. (" + Family.OneLine(ex.Message) + ")", 12.5));
                    dock.Children.Add(msg);
                    var h = Loaded2; if (h != null) h(false);
                }
            };
            Closed += (s, e) => { try { View.Dispose(); } catch { } };
        }
        bool SameOrigin(Uri u) { return u.Scheme == "about" || u.Scheme == "data" || (u.Host == start.Host && u.Port == start.Port && u.Scheme == start.Scheme); }
        static void Outside(string url) { Uri u; if (Uri.TryCreate(url, UriKind.Absolute, out u) && (u.Scheme == "http" || u.Scheme == "https")) try { Process.Start(new ProcessStartInfo(u.ToString()) { UseShellExecute = true }); } catch { } }
    }
}
