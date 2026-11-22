// A fake Home Assistant for the tests (localhost only, a random port): the documented REST API (/api/, /api/config, /api/states,
// /api/services/<domain>/<service>, /api/camera_proxy[_stream]/<camera>), the WebSocket API (auth, subscribe_events, state_changed),
// an MJPEG camera stream made of generated frames, and a stand-in for docker-wyze-bridge's player page (/bridge/<name>/).
// Its entities are FakeData's: modelled on the community Petlibro and Wyze integrations, with made-up names and values.
// Service calls have the effects a real feeder would: button.press on the feeder dispenses (unless Jam is set).
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

namespace HallowHome.Tests
{
    public class FakeHa : IDisposable
    {
        public readonly int Port; public readonly string Url, Token;
        readonly HttpListener http = new HttpListener();
        readonly Dictionary<string, Dictionary<string, object>> states = new Dictionary<string, Dictionary<string, object>>();
        public readonly List<string> Calls = new List<string>();
        readonly List<WebSocket> sockets = new List<WebSocket>();
        public bool Jam; public int StreamsOpened, Requests, Unauthorized;
        readonly JavaScriptSerializer js = new JavaScriptSerializer();
        volatile bool stopped;

        public FakeHa()
        {
            var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); Port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();
            Url = "http://localhost:" + Port; Token = "fake-ha-token-" + Guid.NewGuid().ToString("N");
            foreach (var s in FakeData.States(DateTime.Now)) states[(string)s["entity_id"]] = s;
            http.Prefixes.Add(Url + "/"); http.Start();
            new Thread(Loop) { IsBackground = true, Name = "fake ha" }.Start();
        }
        public void Dispose() { stopped = true; try { http.Stop(); http.Close(); } catch { } lock (sockets) foreach (var s in sockets) try { s.Abort(); } catch { } }

        static string Iso(DateTime local) { return local.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.ffffff+00:00", CultureInfo.InvariantCulture); }
        public string State(string id) { lock (states) return states.ContainsKey(id) ? (string)states[id]["state"] : null; }
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
        public int Subscribers { get { lock (sockets) return sockets.Count(s => s.State == WebSocketState.Open); } }

        void Loop()
        {
            while (!stopped)
            {
                HttpListenerContext ctx; try { ctx = http.GetContext(); } catch { return; }
                new Thread(() => { try { Handle(ctx); } catch { try { ctx.Response.Abort(); } catch { } } }) { IsBackground = true }.Start();
            }
        }
        bool Authed(HttpListenerRequest r) { return r.Headers["Authorization"] == "Bearer " + Token; }
        void Send(HttpListenerResponse r, int code, string type, byte[] body) { r.StatusCode = code; r.ContentType = type; r.ContentLength64 = body.Length; r.OutputStream.Write(body, 0, body.Length); r.OutputStream.Close(); }
        void Json(HttpListenerResponse r, int code, object o) { Send(r, code, "application/json", Encoding.UTF8.GetBytes(js.Serialize(o))); }

        void Handle(HttpListenerContext ctx)
        {
            var req = ctx.Request; var res = ctx.Response; var path = req.Url.AbsolutePath; Interlocked.Increment(ref Requests);
            if (path == "/api/websocket" && req.IsWebSocketRequest) { Socket(ctx); return; }
            if (path.StartsWith("/bridge/")) { Bridge(ctx, path); return; }
            if (!Authed(req)) { Interlocked.Increment(ref Unauthorized); Json(res, 401, new Dictionary<string, object> { { "message", "401: Unauthorized" } }); return; }
            if (path == "/api/" && req.HttpMethod == "GET") { Json(res, 200, new Dictionary<string, object> { { "message", "API running." } }); return; }
            if (path == "/api/config") { Json(res, 200, new Dictionary<string, object> { { "version", "2026.11.0-fake" }, { "location_name", "Test home" } }); return; }
            if (path == "/api/states") { List<object> l; lock (states) l = states.Values.Cast<object>().ToList(); Json(res, 200, l); return; }
            if (path.StartsWith("/api/services/") && req.HttpMethod == "POST")
            {
                var parts = path.Substring(14).Split('/'); string body; using (var rd = new StreamReader(req.InputStream)) body = rd.ReadToEnd();
                var d = js.Deserialize<Dictionary<string, object>>(body) ?? new Dictionary<string, object>(); object eid; d.TryGetValue("entity_id", out eid);
                lock (Calls) Calls.Add(parts[0] + "." + parts[1] + " " + body);
                Effect(parts[0] + "." + parts[1], eid as string, d);
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
                var f = "granary_smart_camera_feeder"; int portions = int.Parse(State("number." + f + "_manual_feed_quantity") ?? "1");
                Set("sensor." + f + "_last_feed_time", Iso(DateTime.Now));
                Set("sensor." + f + "_today_feeding_times", (int.Parse(State("sensor." + f + "_today_feeding_times")) + 1).ToString());
                Set("sensor." + f + "_today_feeding_quantity", (int.Parse(State("sensor." + f + "_today_feeding_quantity")) + 10 * portions).ToString());
            }
        }
        // multipart MJPEG, like HA's camera proxy: up to 300 frames at about 10 a second, until the client goes away
        void Stream(HttpListenerContext ctx, string id)
        {
            var res = ctx.Response; var st = State(id);
            if (st == null || st == "unavailable") { Json(res, 404, new Dictionary<string, object> { { "message", "not found" } }); return; }
            Interlocked.Increment(ref StreamsOpened);
            res.StatusCode = 200; res.ContentType = "multipart/x-mixed-replace; boundary=frame"; res.SendChunked = true;
            try
            {
                for (int n = 0; n < 300 && !stopped; n++)
                {
                    var jpg = FakeFrames.Jpeg(id, n, 320, 180);
                    var head = Encoding.ASCII.GetBytes("--frame\r\nContent-Type: image/jpeg\r\nContent-Length: " + jpg.Length + "\r\n\r\n");
                    res.OutputStream.Write(head, 0, head.Length); res.OutputStream.Write(jpg, 0, jpg.Length); res.OutputStream.Write(new byte[] { 13, 10 }, 0, 2); res.OutputStream.Flush();
                    Thread.Sleep(100);
                }
                res.OutputStream.Close();
            }
            catch { try { res.Abort(); } catch { } }
        }
        // docker-wyze-bridge's player stand-in: a page whose <img> plays the MJPEG stream (no auth, like the bridge on the LAN)
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
                send("{\"type\":\"auth_required\",\"ha_version\":\"2026.11.0-fake\"}");
                var auth = js.Deserialize<Dictionary<string, object>>(recv() ?? "{}");
                object tok; if (auth == null || !auth.TryGetValue("access_token", out tok) || (tok as string) != Token) { send("{\"type\":\"auth_invalid\",\"message\":\"Invalid access token or password\"}"); ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).Wait(2000); return; }
                send("{\"type\":\"auth_ok\",\"ha_version\":\"2026.11.0-fake\"}");
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
