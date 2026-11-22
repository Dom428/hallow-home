// Hallow Home - the small themed dialogs: "Feed now?" (always asked before anything moves on a real device), a message, and the
// crash notice. Each is a Window built in code; --snap renders them without showing them.
using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace HallowHome
{
    public static class Dialogs
    {
        static Window Make(string title, double width)
        {
            var w = new Window(); Ui.Dress(w, title, width);
            w.ResizeMode = ResizeMode.NoResize; w.ShowInTaskbar = false; w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return w;
        }
        static StackPanel Body(Window w) { var sp = new StackPanel { Margin = new Thickness(22, 18, 22, 18) }; w.Content = sp; return sp; }
        static WrapPanel Buttons(StackPanel sp) { var row = new WrapPanel { Margin = new Thickness(0, 16, 0, 0), HorizontalAlignment = HorizontalAlignment.Right }; sp.Children.Add(row); return row; }

        public static Window ConfirmWindow(string title, string text, string okLabel, bool danger, Action<bool> done)
        {
            var w = Make("Hallow Home", 480); var sp = Body(w);
            sp.Children.Add(Ui.T(title, 15, true, null, 6)); sp.Children.Add(Ui.Muted(text, 12.5));
            var row = Buttons(sp); bool answer = false;
            row.Children.Add(Ui.Btn("Cancel", () => w.Close()));
            var ok = danger ? Ui.Danger(okLabel, () => { answer = true; w.Close(); }) : Ui.Btn(okLabel, () => { answer = true; w.Close(); }, true); ok.Margin = new Thickness(0); ok.MinWidth = 90;
            row.Children.Add(ok);
            w.Closed += (s, e) => { if (done != null) done(answer); };
            w.Loaded += (s, e) => ((Button)row.Children[0]).Focus();   // Cancel has the focus: Enter never changes anything by accident
            return w;
        }
        public static bool Confirm(Window owner, string title, string text, string okLabel, bool danger)
        {
            bool r = false; var w = ConfirmWindow(title, text, okLabel, danger, a => r = a); Own(w, owner); w.ShowDialog(); return r;
        }
        static void Own(Window w, Window owner) { if (owner != null && owner.IsVisible) { w.Owner = owner; w.WindowStartupLocation = WindowStartupLocation.CenterOwner; } }

        public static Window InfoWindow(string title, string text, Action done)
        {
            var w = Make("Hallow Home", 460); var sp = Body(w);
            sp.Children.Add(Ui.T(title, 15, true, null, 6)); sp.Children.Add(Ui.Muted(text, 12.5));
            var row = Buttons(sp); var ok = Ui.Btn("OK", () => w.Close(), true); ok.Margin = new Thickness(0); ok.MinWidth = 80; row.Children.Add(ok);
            w.Closed += (s, e) => { if (done != null) done(); };
            return w;
        }
        public static void Info(Window owner, string title, string text) { var w = InfoWindow(title, text, null); Own(w, owner); w.ShowDialog(); }

        // "Feed now?": pick the portions (1 if the feeder has no portion setting), see the grams, then confirm. Cancel has the focus.
        public static Window FeedWindow(Controller c, Action<int> done)
        {
            var w = Make("Hallow Home", 460); var sp = Body(w);
            int max = c.MaxFeedPortions, min = c.MinFeedPortions; int pick = min;   // the same range the pipe and status.json give
            sp.Children.Add(Ui.T("Feed now?", 15, true, null, 6));
            sp.Children.Add(Ui.Muted("The feeder dispenses right away. One portion is about " + Read.Num(c.S.GramsPerPortion) + " g (change it in Settings if your feeder is set differently)." + (c.Feeder.Next != null ? " Next scheduled meal: " + Fmt.Clock(c.Feeder.Next, c.Now()) + "." : ""), 12.5, 10));
            var chips = new WrapPanel(); var grams = Ui.Muted("", 12.5, 0);
            Button ok = null;
            Action refresh = () =>
            {
                foreach (System.Windows.Controls.Primitives.ToggleButton t in chips.Children) t.IsChecked = (int)t.Tag == pick;
                grams.Text = pick + " portion" + (pick == 1 ? "" : "s") + " ≈ " + Read.Num(pick * c.S.GramsPerPortion) + " g";
                if (ok != null) ok.Content = "Feed " + pick + " portion" + (pick == 1 ? "" : "s");
            };
            for (int i = min; i <= max; i++)
            {
                var n = i; var t = new System.Windows.Controls.Primitives.ToggleButton { Content = i.ToString(), Tag = i, Style = Ui.S("Chip"), Margin = new Thickness(0, 0, 6, 6), MinWidth = 38 };
                t.Click += (s, e) => { pick = n; refresh(); }; chips.Children.Add(t);
            }
            sp.Children.Add(chips); sp.Children.Add(grams);
            if (max == 1 && c.Map.Get("feeder.portion") == null) sp.Children.Add(Ui.Muted("Only one portion at a time: no portion setting was found for the feeder (Devices page).", 11.5, 0));
            var row = Buttons(sp); int answer = 0;
            row.Children.Add(Ui.Btn("Cancel", () => w.Close()));
            ok = Ui.Btn("Feed " + pick + " portion" + (pick == 1 ? "" : "s"), () => { answer = pick; w.Close(); }, true); ok.Margin = new Thickness(0); ok.MinWidth = 120; row.Children.Add(ok);
            refresh();
            w.Closed += (s, e) => { if (done != null) done(answer); };
            w.Loaded += (s, e) => ((Button)row.Children[0]).Focus();
            return w;
        }
        public static int AskFeed(Window owner, Controller c) { int r = 0; var w = FeedWindow(c, a => r = a); Own(w, owner); w.ShowDialog(); return r; }
    }

    public static class CrashNotice
    {
        public static Window Make(string file)
        {
            var w = new Window(); Ui.Dress(w, "Hallow Home", 440);
            w.ResizeMode = ResizeMode.NoResize; w.WindowStartupLocation = WindowStartupLocation.CenterScreen; w.Topmost = true;
            var sp = new StackPanel { Margin = new Thickness(22, 18, 22, 18) };
            sp.Children.Add(Ui.T("Hallow Home closed unexpectedly last time", 15, true, null, 6));
            DateTime when = DateTime.Now; try { if (File.Exists(file)) when = File.GetLastWriteTime(file); } catch { }
            sp.Children.Add(Ui.Muted("On " + when.ToString("d MMM yyyy, HH:mm") + ". A report was saved on this PC - nothing was sent anywhere, and it never contains your Home Assistant token or alert addresses.", 12.5, 14));
            var row = new WrapPanel();
            row.Children.Add(Ui.Btn("Show details", () => { try { System.Diagnostics.Process.Start("notepad.exe", "\"" + file + "\""); } catch { } }));
            row.Children.Add(Ui.Btn("Open folder", () => { try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + file + "\""); } catch { } }));
            var ok = Ui.Btn("OK", () => w.Close(), true); ok.MinWidth = 70; row.Children.Add(ok);
            sp.Children.Add(row); w.Content = sp;
            return w;
        }
    }
}
