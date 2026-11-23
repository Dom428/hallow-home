// Hallow Home - a stand-in for Home Assistant, on localhost only (a random port), with made-up devices. It is used for two things:
//   * Demo mode (1.2): explore the app before Home Assistant exists. Everything happens inside this PC - the "feeder", "fountain"
//     and "cameras" are FakeData's; nothing reaches a real device, account or Home Assistant, and no alerts are sent.
//   * the tests (tests\HomeTests.cs), which drive the real client against it.
// It speaks the documented APIs Hallow Home uses: REST (/api/, /api/config, /api/states, /api/services [GET and POST],
// /api/template, /api/camera_proxy[_stream]/<camera>), the WebSocket API (auth, subscribe_events, state_changed), and
// /manifest.json (what the wizard's "Test connection" looks for). Cameras are MJPEG streams of generated frames.
// docker-wyze-bridge stand-ins: two more localhost ports like the bridge's MediaMTX players - WebRTC (/<camera>/) and HLS
// (/<camera>/ and /<camera>/index.m3u8). Their pages play the generated frames (no audio). They stand in for the real players'
// pages; real WebRTC / HLS decoding is the browser engine's job and isn't simulated.
// Service calls have the effects a real feeder would have: button.press dispenses (unless Jam is set), and the Petlibro
// integration's schedule services (petlibro.edit_feeding_plan / add / toggle / delete) change the "Feeding schedule" sensor.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace HallowHome
{
    public class DemoHa : IDisposable
    {
        public const string DeviceId = "demo0feeder0device0id0000000001";
        public readonly int Port, HlsPort, WebRtcPort; public readonly string Url, Token, HlsBase, WebRtcBase;
        readonly HttpListener http = new HttpListener(), hls = new HttpListener(), webrtc = new HttpListener();
        readonly Dictionary<string, Dictionary<string, object>> states = new Dictionary<string, Dictionary<string, object>>();
        public readonly List<string> Calls = new List<string>();
        readonly List<WebSocket> sockets = new List<WebSocket>();
        public bool Jam; public int StreamsOpened, Requests, Unauthorized;
        public bool ScheduleServices = true, OfferHls = true, OfferWebRtc = true;   // what this "Home Assistant" / "bridge" offers
        public int FailStreams, FailPlayers;                                         // the next N camera streams / player pages fail (503)
        public int OpenStreams { get { return openStreams; } }
        int openStreams, nextPlan = 200;
        readonly JavaScriptSerializer js = new JavaScriptSerializer();
        volatile bool stopped;
        const string F = "granary_smart_camera_feeder";

        static int FreePort() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }
        public DemoHa()
        {
            Port = FreePort(); HlsPort = FreePort(); WebRtcPort = FreePort();
            Url = "http://localhost:" + Port; HlsBase = "http://localhost:" + HlsPort; WebRtcBase = "http://localhost:" + WebRtcPort;
            Token = "demo-ha-token-" + Guid.NewGuid().ToString("N");
            foreach (var s in FakeData.States(DateTime.Now)) states[(string)s["entity_id"]] = s;
            http.Prefixes.Add(Url + "/"); http.Start();
            hls.Prefixes.Add(HlsBase + "/"); hls.Start();
            webrtc.Prefixes.Add(WebRtcBase + "/"); webrtc.Start();
            new Thread(() => Loop(http, Handle)) { IsBackground = true, Name = "demo ha" }.Start();
            new Thread(() => Loop(hls, c => Player(c, "hls"))) { IsBackground = true, Name = "demo hls" }.Start();
            new Thread(() => Loop(webrtc, c => Player(c, "webrtc"))) { IsBackground = true, Name = "demo webrtc" }.Start();
        }
        public void Dispose()
        {
            stopped = true;
            foreach (var l in new[] { http, hls, webrtc }) try { l.Stop(); l.Close(); } catch { }
            lock (sockets) foreach (var s in sockets) try { s.Abort(); } catch { }
        }

        static string Iso(DateTime local) { return local.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.ffffff+00:00", CultureInfo.InvariantCulture); }
        public string State(string id) { lock (states) return states.ContainsKey(id) ? (string)states[id]["state"] : null; }
        public object Attr(string id, string attr) { lock (states) { Dictionary<string, object> s; object a, v; return states.TryGetValue(id, out s) && s.TryGetValue("attributes", out a) && ((Dictionary<string, object>)a).TryGetValue(attr, out v) ? v : null; } }
        // changes an entity and tells every WebSocket subscriber (state_changed), like HA does
        public void Set(string id, string state) { Set(id, state, null, null); }
        // ...and one attribute too (e.g. a number entity's "max"), like an integration update would
        public void Set(string id, string state, string attr, object value)
        {
            Dictionary<string, object> old, nu;
            lock (states)
            {
                states.TryGetValue(id, out old);
                nu = new Dictionary<string, object>(old ?? new Dictionary<string, object> { { "entity_id", id }, { "attributes", new Dictionary<string, object> { { "friendly_name", id } } } });
                nu["state"] = state; nu["last_changed"] = Iso(DateTime.Now); nu["last_updated"] = Iso(DateTime.Now);
                if (attr != null) { var a = new Dictionary<string, object>((Dictionary<string, object>)nu["attributes"]); a[attr] = value; nu["attributes"] = a; }
                states[id] = nu;
            }
            var ev = js.Serialize(new Dictionary<string, object> { { "id", 1 }, { "type", "event" }, { "event", new Dictionary<string, object> {
                { "event_type", "state_changed" }, { "data", new Dictionary<string, object> { { "entity_id", id }, { "old_state", old }, { "new_state", nu } } }, { "origin", "LOCAL" }, { "time_fired", Iso(DateTime.Now) } } } });
            var b = Encoding.UTF8.GetBytes(ev);
            lock (sockets) foreach (var s in sockets.ToList()) try { s.SendAsync(new ArraySegment<byte>(b), WebSocketMessageType.Text, true, CancellationToken.None).Wait(2000); } catch { sockets.Remove(s); }
        }
        public void Remove(string id) { lock (states) states.Remove(id); }
        public int Subscribers { get { lock (sockets) return sockets.Count(s => s.State == WebSocketState.Open); } }

        void Loop(HttpListener l, Action<HttpListenerContext> handle)
        {
            while (!stopped)
            {
                HttpListenerContext ctx; try { ctx = l.GetContext(); } catch { return; }
                new Thread(() => { try { handle(ctx); } catch { try { ctx.Response.Abort(); } catch { } } }) { IsBackground = true }.Start();
            }
        }
        bool Authed(HttpListenerRequest r) { return r.Headers["Authorization"] == "Bearer " + Token; }
        static void Send(HttpListenerResponse r, int code, string type, byte[] body) { r.StatusCode = code; r.ContentType = type; r.ContentLength64 = body.Length; r.OutputStream.Write(body, 0, body.Length); r.OutputStream.Close(); }
        void Json(HttpListenerResponse r, int code, object o) { Send(r, code, "application/json", Encoding.UTF8.GetBytes(js.Serialize(o))); }

        void Handle(HttpListenerContext ctx)
        {
            var req = ctx.Request; var res = ctx.Response; var path = req.Url.AbsolutePath; Interlocked.Increment(ref Requests);
            if (path == "/api/websocket" && req.IsWebSocketRequest) { Socket(ctx); return; }
            if (path == "/manifest.json") { Json(res, 200, new Dictionary<string, object> { { "name", "Home Assistant" }, { "short_name", "Assistant" }, { "start_url", "/?homescreen=1" } }); return; }
            if (path.StartsWith("/bridge/")) { Bridge(ctx, path); return; }
            if (!Authed(req)) { Interlocked.Increment(ref Unauthorized); Json(res, 401, new Dictionary<string, object> { { "message", "401: Unauthorized" } }); return; }
            if (path == "/api/" && req.HttpMethod == "GET") { Json(res, 200, new Dictionary<string, object> { { "message", "API running." } }); return; }
            if (path == "/api/config") { Json(res, 200, new Dictionary<string, object> { { "version", "2026.11.0-demo" }, { "location_name", "Demo home" } }); return; }
            if (path == "/api/states") { List<object> l; lock (states) l = states.Values.Cast<object>().ToList(); Json(res, 200, l); return; }
            if (path == "/api/services" && req.HttpMethod == "GET")
            {
                var l = new List<object> {
                    new Dictionary<string, object> { { "domain", "button" }, { "services", new Dictionary<string, object> { { "press", new Dictionary<string, object>() } } } },
                    new Dictionary<string, object> { { "domain", "number" }, { "services", new Dictionary<string, object> { { "set_value", new Dictionary<string, object>() } } } },
                    new Dictionary<string, object> { { "domain", "switch" }, { "services", new Dictionary<string, object> { { "turn_on", new Dictionary<string, object>() }, { "turn_off", new Dictionary<string, object>() } } } } };
                if (ScheduleServices) l.Add(new Dictionary<string, object> { { "domain", "petlibro" }, { "services", Schedule.Services.ToDictionary(s => s, s => (object)new Dictionary<string, object>()) } });
                Json(res, 200, l); return;
            }
            if (path == "/api/template" && req.HttpMethod == "POST")
            {
                string body; using (var rd = new StreamReader(req.InputStream)) body = rd.ReadToEnd();
                var d = js.Deserialize<Dictionary<string, object>>(body) ?? new Dictionary<string, object>(); object t; d.TryGetValue("template", out t);
                var tpl = t as string ?? ""; var m = System.Text.RegularExpressions.Regex.Match(tpl, "device_id\\('([a-z0-9_.]+)'\\)");
                Send(res, 200, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(m.Success && m.Groups[1].Value.Contains(F) && State(m.Groups[1].Value) != null ? DeviceId : "None")); return;
            }
            if (path.StartsWith("/api/services/") && req.HttpMethod == "POST")
            {
                var parts = path.Substring(14).Split('/'); string body; using (var rd = new StreamReader(req.InputStream)) body = rd.ReadToEnd();
                var d = js.Deserialize<Dictionary<string, object>>(body) ?? new Dictionary<string, object>(); object eid; d.TryGetValue("entity_id", out eid);
                lock (Calls) Calls.Add(parts[0] + "." + parts[1] + " " + body);
                if (parts[0] == "petlibro")
                {
                    var err = PlanService(parts[1], d);
                    if (err != null) { Json(res, 400, new Dictionary<string, object> { { "message", err } }); return; }
                }
                else Effect(parts[0] + "." + parts[1], eid as string, d);
                Json(res, 200, new object[0]); return;
            }
            if (path.StartsWith("/api/camera_proxy_stream/")) { Stream(ctx, path.Substring(25)); return; }
            if (path.StartsWith("/api/camera_proxy/"))
            {
                var id = path.Substring(18); var st = State(id);
                if (st == null || st == "unavailable") { Json(res, 404, new Dictionary<string, object> { { "message", "not found" } }); return; }
                Send(res, 200, "image/jpeg", FakeFrames.Jpeg(id, 1)); return;
            }
            Json(res, 404, new Dictionary<string, object> { { "message", "not found" } });
        }
        // what the devices would do
        void Effect(string service, string id, Dictionary<string, object> d)
        {
            if (id == null) return;
            if (service == "number.set_value") { Set(id, Convert.ToString(d["value"], CultureInfo.InvariantCulture)); return; }
            if (service == "switch.turn_on" || service == "light.turn_on") { Set(id, "on"); return; }
            if (service == "switch.turn_off" || service == "light.turn_off") { Set(id, "off"); return; }
            if (service == "button.press" && id.Contains("manual_feed"))
            {
                Set(id, Iso(DateTime.Now));
                if (Jam) return;
                int portions = int.Parse(State("number." + F + "_manual_feed_quantity") ?? "1");
                Set("sensor." + F + "_last_feed_time", Iso(DateTime.Now));
                Set("sensor." + F + "_today_feeding_times", (int.Parse(State("sensor." + F + "_today_feeding_times")) + 1).ToString());
                Set("sensor." + F + "_today_feeding_quantity", (int.Parse(State("sensor." + F + "_today_feeding_quantity")) + 10 * portions).ToString());
            }
        }
        // the Petlibro integration's schedule services, on the "Feeding schedule" sensor's plan list
        string PlanService(string service, Dictionary<string, object> d)
        {
            if (!ScheduleServices || !Schedule.Services.Contains(service)) return "Service not found.";
            object v; if (!d.TryGetValue("device_id", out v) || (v as string) != DeviceId) return "Unknown device.";
            var id = "binary_sensor." + F + "_feeding_schedule";
            var plans = ((object[])(Attr(id, "schedule") ?? new object[0])).Cast<Dictionary<string, object>>().Select(p => new Dictionary<string, object>(p)).ToList();
            long pid = d.TryGetValue("plan_id", out v) ? Convert.ToInt64(v, CultureInfo.InvariantCulture) : 0;
            var plan = plans.FirstOrDefault(p => Convert.ToInt64(p["id"], CultureInfo.InvariantCulture) == pid);
            if (service != "add_feeding_plan" && plan == null) return "Plan ID " + pid + " not found.";
            Func<object[]> days = () => d.TryGetValue("days", out v) && v is System.Collections.IEnumerable && !(v is string) ? ((System.Collections.IEnumerable)v).Cast<object>().Select(x => (object)Convert.ToInt32(x, CultureInfo.InvariantCulture)).ToArray() : null;
            switch (service)
            {
                case "edit_feeding_plan":
                    var nd = days() ?? (object[])plan["repeat_days"];
                    var np = FakeData.Plan(pid, (string)plan["label"], d.TryGetValue("time", out v) ? ScheduleView.NormTime((string)v) : (string)plan["time"], d.TryGetValue("portions", out v) ? Convert.ToInt32(v) : Convert.ToInt32(plan["amount_raw"]), (bool)plan["enabled"], nd, DateTime.Now);
                    plans[plans.IndexOf(plan)] = np; break;
                case "add_feeding_plan":
                    if (!d.TryGetValue("time", out v) || ScheduleView.NormTime(v as string) == null) return "time is required";
                    object po; if (!d.TryGetValue("portions", out po)) return "portions is required";
                    object lb; d.TryGetValue("label", out lb);
                    plans.Add(FakeData.Plan(nextPlan++, lb as string ?? "", ScheduleView.NormTime((string)v), Convert.ToInt32(po), true, days() ?? new object[0], DateTime.Now)); break;
                case "toggle_feeding_plan": plan["enabled"] = d.TryGetValue("enable", out v) && v is bool && (bool)v; break;
                case "delete_feeding_plan": plans.Remove(plan); break;
            }
            Set(id, State(id) ?? "on", "schedule", plans.OrderBy(p => (string)p["time"]).Cast<object>().ToArray());
            return null;
        }
        // multipart MJPEG, like HA's camera proxy: about 10 frames a second until the client goes away (at most 10 minutes)
        void Stream(HttpListenerContext ctx, string id)
        {
            var res = ctx.Response; var st = State(id);
            if (st == null || st == "unavailable") { Json(res, 404, new Dictionary<string, object> { { "message", "not found" } }); return; }
            if (FailStreams > 0) { Interlocked.Decrement(ref FailStreams); Json(res, 503, new Dictionary<string, object> { { "message", "camera busy" } }); return; }
            Interlocked.Increment(ref StreamsOpened); Interlocked.Increment(ref openStreams);
            res.StatusCode = 200; res.ContentType = "multipart/x-mixed-replace; boundary=frame"; res.SendChunked = true;
            try
            {
                for (int n = 0; n < 6000 && !stopped && State(id) != "unavailable"; n++)
                {
                    var jpg = FakeFrames.Jpeg(id, n, 320, 180);
                    var head = Encoding.ASCII.GetBytes("--frame\r\nContent-Type: image/jpeg\r\nContent-Length: " + jpg.Length + "\r\n\r\n");
                    res.OutputStream.Write(head, 0, head.Length); res.OutputStream.Write(jpg, 0, jpg.Length); res.OutputStream.Write(new byte[] { 13, 10 }, 0, 2); res.OutputStream.Flush();
                    Thread.Sleep(100);
                }
                res.OutputStream.Close();
            }
            catch { try { res.Abort(); } catch { } }
            finally { Interlocked.Decrement(ref openStreams); }
        }
        // the bridge's MediaMTX player stand-ins: a page per camera whose <img> plays generated frames (no auth, like the bridge on
        // the LAN), and for HLS an index.m3u8 that answers like a playlist (for "which players does the bridge offer?")
        void Player(HttpListenerContext ctx, string kind)
        {
            var path = ctx.Request.Url.AbsolutePath.Trim('/'); var parts = path.Split('/');
            if ((kind == "hls" && !OfferHls) || (kind == "webrtc" && !OfferWebRtc) || parts.Length == 0 || parts[0].Length == 0) { Send(ctx.Response, 404, "text/plain", Encoding.ASCII.GetBytes("404 page not found")); return; }
            var cam = "camera." + parts[0].Replace("-", "_");
            if (State(cam) == null || State(cam) == "unavailable") { Send(ctx.Response, 404, "text/plain", Encoding.ASCII.GetBytes("404 page not found")); return; }
            if (parts.Length > 1 && parts[1] == "stream") { Stream(ctx, cam); return; }
            if (kind == "hls" && parts.Length > 1 && parts[1] == "index.m3u8") { Send(ctx.Response, 200, "application/vnd.apple.mpegurl", Encoding.ASCII.GetBytes("#EXTM3U\n#EXT-X-VERSION:9\n#EXT-X-INDEPENDENT-SEGMENTS\n#EXT-X-STREAM-INF:BANDWIDTH=1000000,CODECS=\"avc1.64001f\"\nmain_stream.m3u8\n")); return; }
            if (parts.Length > 1) { Send(ctx.Response, 404, "text/plain", Encoding.ASCII.GetBytes("404 page not found")); return; }
            if (FailPlayers > 0) { Interlocked.Decrement(ref FailPlayers); Send(ctx.Response, 503, "text/plain", Encoding.ASCII.GetBytes("503 not ready")); return; }
            var html = "<!doctype html><html><head><title>" + kind + " " + parts[0] + "</title></head><body style='margin:0;background:#000'><img id='v' src='/" + parts[0] + "/stream' style='width:100%;height:100vh;object-fit:contain'>" +
                "<script>document.getElementById('v').onload=function(){document.title='playing';};</script></body></html>";
            Send(ctx.Response, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(html));
        }
        // 1.0's bridge stand-in on the main port (kept for the older live-view path)
        void Bridge(HttpListenerContext ctx, string path)
        {
            var name = path.Substring(8).Trim('/');
            if (name.EndsWith("/stream")) { Stream(ctx, "camera." + name.Substring(0, name.Length - 7).Replace("-", "_")); return; }
            var html = "<!doctype html><html><head><title>" + name + "</title></head><body style='margin:0;background:#000'><img id='v' src='/bridge/" + name + "/stream' style='width:100%'>" +
                "<script>document.getElementById('v').onload=function(){document.title='playing';};</script></body></html>";
            Send(ctx.Response, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(html));
        }
        void Socket(HttpListenerContext ctx)
        {
            var wsc = ctx.AcceptWebSocketAsync(null).Result; var ws = wsc.WebSocket;
            Func<string> recv = () =>
            {
                var buf = new byte[65536]; var ms = new MemoryStream();
                while (true) { var r = ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None).Result; if (r.MessageType == WebSocketMessageType.Close) return null; ms.Write(buf, 0, r.Count); if (r.EndOfMessage) return Encoding.UTF8.GetString(ms.ToArray()); }
            };
            Action<string> send = t => { var b = Encoding.UTF8.GetBytes(t); ws.SendAsync(new ArraySegment<byte>(b), WebSocketMessageType.Text, true, CancellationToken.None).Wait(2000); };
            try
            {
                send("{\"type\":\"auth_required\",\"ha_version\":\"2026.11.0-demo\"}");
                var auth = js.Deserialize<Dictionary<string, object>>(recv() ?? "{}");
                object tok; if (auth == null || !auth.TryGetValue("access_token", out tok) || (tok as string) != Token) { send("{\"type\":\"auth_invalid\",\"message\":\"Invalid access token or password\"}"); ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).Wait(2000); return; }
                send("{\"type\":\"auth_ok\",\"ha_version\":\"2026.11.0-demo\"}");
                while (!stopped)
                {
                    var m = recv(); if (m == null) break;
                    var d = js.Deserialize<Dictionary<string, object>>(m); if (d == null) continue;
                    if ((d["type"] as string) == "subscribe_events") { send("{\"id\":" + d["id"] + ",\"type\":\"result\",\"success\":true,\"result\":null}"); lock (sockets) sockets.Add(ws); }
                }
            }
            catch { }
            lock (sockets) sockets.Remove(ws);
        }
    }
}
