// Hallow Home - cameras.
//   The grid and the single view read Home Assistant's camera proxy: GET /api/camera_proxy_stream/<camera> is an MJPEG stream
//   (multipart JPEG frames) that this file splits into frames - no browser needed, the token stays in the Authorization header.
//   Streams run only while the Cameras page (or a single view) is open and no game is running.
//   Live view (optional): docker-wyze-bridge's own WebRTC / HLS player page in a WebView2 window (CameraWindow.cs).
//   Snapshot: one still from /api/camera_proxy/<camera>, saved to Pictures\Hallow Home.
//   Recordings: Hallow Home doesn't record. Point "Recordings folder" at wherever wyze-bridge or HA records on the NAS and it
//   lists the files there (newest first); opening one uses your normal video player. It never deletes anything.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Media.Imaging;

namespace HallowHome
{
    public static class Mjpeg
    {
        // reads JPEG frames (FF D8 ... FF D9) from a multipart stream until stop() or the stream ends; frame gets each one
        public static int Read(Stream s, Func<bool> stop, Action<byte[]> frame, int maxFrames = int.MaxValue)
        {
            var buf = new byte[65536]; var cur = new MemoryStream(); bool inJpeg = false; byte prev = 0; int frames = 0;
            while (!stop() && frames < maxFrames)
            {
                int n; try { n = s.Read(buf, 0, buf.Length); } catch { break; }
                if (n <= 0) break;
                for (int i = 0; i < n; i++)
                {
                    byte b = buf[i];
                    if (!inJpeg) { if (prev == 0xFF && b == 0xD8) { inJpeg = true; cur.SetLength(0); cur.WriteByte(0xFF); cur.WriteByte(0xD8); } }
                    else
                    {
                        cur.WriteByte(b);
                        if (prev == 0xFF && b == 0xD9) { inJpeg = false; frames++; frame(cur.ToArray()); if (frames >= maxFrames || stop()) return frames; }
                        else if (cur.Length > 8 * 1024 * 1024) { inJpeg = false; }   // not a sane frame
                    }
                    prev = b;
                }
            }
            return frames;
        }
        public static BitmapSource Decode(byte[] jpeg)
        {
            try
            {
                var bi = new BitmapImage(); bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.StreamSource = new MemoryStream(jpeg); bi.EndInit(); bi.Freeze();
                return bi;
            }
            catch { return null; }
        }
    }

    // one running stream (a camera tile): frames come in on a background thread, Frame is called with each decoded picture
    public class CameraFeed
    {
        public readonly string Camera; volatile bool stopped; public int Frames; public string Error;
        public Action<BitmapSource> Frame; public event Action<string> Failed;
        public CameraFeed(string camera) { Camera = camera; }
        public void Start(Ha ha, int maxFps)
        {
            new Thread(() =>
            {
                int fails = 0; var minGap = TimeSpan.FromMilliseconds(1000.0 / Math.Max(1, maxFps)); var last = DateTime.MinValue;
                while (!stopped)
                {
                    try
                    {
                        using (var resp = ha.OpenStream(Camera)) using (var s = resp.GetResponseStream())
                            Mjpeg.Read(s, () => stopped, jpg =>
                            {
                                fails = 0; Frames++;
                                if (DateTime.Now - last < minGap) return; last = DateTime.Now;
                                var bmp = Mjpeg.Decode(jpg); var h = Frame; if (bmp != null && h != null) h(bmp);
                            });
                    }
                    catch (Exception e) { Error = e.Message; var f = Failed; if (f != null) f(e.Message); }
                    if (stopped) break;
                    for (int i = 0; i < Math.Min(30, 2 << Math.Min(4, fails++)) * 5 && !stopped; i++) Thread.Sleep(200);
                }
            }) { IsBackground = true, Name = "camera " + Camera }.Start();
        }
        public void Stop() { stopped = true; }
    }

    public class Recording { public string Path, Name; public long Size; public DateTime When; }

    public static class Cams
    {
        static readonly string[] Exts = { ".mp4", ".mkv", ".ts", ".m4v", ".mov", ".avi", ".jpg", ".jpeg" };
        // the newest files under the folder (two levels deep: wyze-bridge keeps a folder per camera / day), at most max
        public static List<Recording> List(string folder, int max, out string err)
        {
            err = null; var l = new List<Recording>();
            if (string.IsNullOrWhiteSpace(folder)) { err = "No recordings folder set."; return l; }
            try
            {
                if (!Directory.Exists(folder)) { err = "That folder can't be reached right now (is the NAS on?)."; return l; }
                var files = new List<FileInfo>(); var root = new DirectoryInfo(folder);
                Action<DirectoryInfo, int> walk = null;
                walk = (d, depth) =>
                {
                    try { files.AddRange(d.GetFiles().Where(f => Exts.Contains(f.Extension.ToLowerInvariant()))); } catch { }
                    if (depth < 3 && files.Count < 20000) try { foreach (var sub in d.GetDirectories()) walk(sub, depth + 1); } catch { }
                };
                walk(root, 0);
                foreach (var f in files.OrderByDescending(f => f.LastWriteTime).Take(max))
                    l.Add(new Recording { Path = f.FullName, Name = f.FullName.Substring(root.FullName.Length).TrimStart('\\', '/'), Size = f.Length, When = f.LastWriteTime });
            }
            catch (Exception e) { err = "Couldn't list it: " + e.Message; }
            return l;
        }
        // docker-wyze-bridge names a stream after the camera's name in the Wyze app: lower case, spaces -> dashes
        public static string BridgeName(Entity cam)
        {
            var n = (cam.Name ?? cam.Id.Substring(cam.Id.IndexOf('.') + 1)).ToLowerInvariant();
            return Regex.Replace(Regex.Replace(n, "[^a-z0-9]+", "-"), "^-+|-+$", "");
        }
        public static string LiveUrl(string liveBase, Entity cam) { return liveBase == null || cam == null ? null : liveBase.TrimEnd('/') + "/" + BridgeName(cam) + "/"; }
        public static string SnapshotFolder { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Hallow Home"); } }
        public static string SaveSnapshot(string camLabel, byte[] jpeg, string folder = null)
        {
            folder = folder ?? SnapshotFolder; Directory.CreateDirectory(folder);
            var safe = Regex.Replace(camLabel ?? "camera", "[^A-Za-z0-9 \\-]+", "").Trim(); if (safe.Length == 0) safe = "camera";
            var f = Path.Combine(folder, safe + " " + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss") + ".jpg");
            File.WriteAllBytes(f, jpeg); return f;
        }
    }

    // made-up camera pictures: --snap and the fake Home Assistant in the tests (a room-ish scene, the camera's name and a clock)
    public static class FakeFrames
    {
        public static byte[] Jpeg(string label, int n, int w = 640, int h = 360)
        {
            using (var b = new Bitmap(w, h))
            {
                using (var g = Graphics.FromImage(b))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    int seed = 0; foreach (var c in label ?? "") seed = seed * 31 + c;
                    var rnd = new Random(seed);
                    var top = Color.FromArgb(40 + rnd.Next(40), 44 + rnd.Next(40), 52 + rnd.Next(50));
                    using (var bg = new LinearGradientBrush(new Rectangle(0, 0, w, h), top, Color.FromArgb(18, 18, 22), 90f)) g.FillRectangle(bg, 0, 0, w, h);
                    using (var floor = new SolidBrush(Color.FromArgb(70, 62, 54))) g.FillPolygon(floor, new[] { new PointF(0, h * 0.72f), new PointF(w, h * 0.66f), new PointF(w, h), new PointF(0, h) });
                    using (var win = new SolidBrush(Color.FromArgb(120, 150, 170, 190))) g.FillRectangle(win, w * 0.62f, h * 0.14f, w * 0.22f, h * 0.30f);
                    using (var box = new SolidBrush(Color.FromArgb(92, 84, 110))) g.FillRectangle(box, w * 0.10f, h * 0.48f, w * 0.26f, h * 0.26f);
                    // a "cat" that walks across, one step a frame
                    float cx = (n * 9 % (w + 120)) - 60, cy = h * 0.70f;
                    using (var cat = new SolidBrush(Color.FromArgb(230, 150, 90))) { g.FillEllipse(cat, cx, cy - 26, 70, 30); g.FillEllipse(cat, cx + 56, cy - 40, 26, 24); }
                    using (var f = new Font("Consolas", 13f, FontStyle.Bold)) using (var tb = new SolidBrush(Color.FromArgb(235, 255, 255, 255)))
                    {
                        g.DrawString((label ?? "Camera").ToUpperInvariant(), f, tb, 12, 10);
                        g.DrawString(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  #" + n, f, tb, 12, h - 30);
                    }
                }
                using (var ms = new MemoryStream()) { b.Save(ms, ImageFormat.Jpeg); return ms.ToArray(); }
            }
        }
    }
}
