// Hallow Home - talking to Home Assistant through its documented APIs only:
//   REST       GET /api/ (is it there, is the token good), GET /api/states, POST /api/services/<domain>/<service>,
//              GET /api/camera_proxy/<camera> (a still), GET /api/camera_proxy_stream/<camera> (MJPEG)
//   WebSocket  /api/websocket: auth with the long-lived token, then subscribe_events state_changed
// Every request carries "Authorization: Bearer <token>"; the token is never put in a URL, a log line or an error message.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace HallowHome
{
    public class Entity
    {
        public string Id, State, Name;
        public Dictionary<string, object> Attr = new Dictionary<string, object>();
        public DateTime Changed = DateTime.Now;   // local time
        public string Domain { get { var i = Id.IndexOf('.'); return i < 0 ? Id : Id.Substring(0, i); } }
        public string DeviceClass { get { return AttrS("device_class"); } }
        public string Unit { get { return AttrS("unit_of_measurement"); } }
        public bool Available { get { return State != null && State != "unavailable" && (State != "unknown" || Domain == "button" || Domain == "scene"); } }
        public string AttrS(string k) { object v; return Attr.TryGetValue(k, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : null; }
        public double? Num { get { double d; return State != null && double.TryParse(State, NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : (double?)null; } }
        public bool On { get { return State == "on" || State == "open" || State == "true" || State == "detected" || State == "problem"; } }
        public string Label { get { return string.IsNullOrEmpty(Name) ? Id : Name; } }
        // the words matched by the mapping layer: the entity id and the friendly name, lower case, with _ . - turned into spaces
        public string Words { get { return Regex.Replace((Id + " " + (Name ?? "")).ToLowerInvariant(), "[_.\\-]+", " "); } }
        public Entity Copy() { return new Entity { Id = Id, State = State, Name = Name, Attr = new Dictionary<string, object>(Attr), Changed = Changed }; }

        static readonly JavaScriptSerializer js = new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 };
        public static Entity From(Dictionary<string, object> d)
        {
            if (d == null) return null; object v;
            var id = d.TryGetValue("entity_id", out v) ? v as string : null; if (!Ha.ValidEntityId(id)) return null;
            var e = new Entity { Id = id, State = d.TryGetValue("state", out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : null };
            if (d.TryGetValue("attributes", out v) && v is Dictionary<string, object>) e.Attr = (Dictionary<string, object>)v;
            e.Name = e.AttrS("friendly_name");
            DateTime t; if (d.TryGetValue("last_changed", out v) && v is string && DateTime.TryParse((string)v, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out t)) e.Changed = t.ToLocalTime();
            return e;
        }
        public static List<Entity> ParseStates(string json)
        {
            var l = new List<Entity>();
            var arr = js.DeserializeObject(json) as object[];
            if (arr == null) throw new FormatException("Home Assistant didn't send a list of states.");
            foreach (var o in arr) { var e = From(o as Dictionary<string, object>); if (e != null) l.Add(e); }
            return l;
        }
        // HA's own timestamp format for a value; null when it isn't one
        public static DateTime? Time(string s)
        {
            DateTime t; if (string.IsNullOrEmpty(s)) return null;
            if (Regex.IsMatch(s, "^\\d{4}-\\d\\d-\\d\\d[T ]\\d\\d:\\d\\d") && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | (Regex.IsMatch(s, "(Z|[+\\-]\\d\\d:?\\d\\d)$") ? DateTimeStyles.AssumeUniversal : DateTimeStyles.AssumeLocal), out t)) return Regex.IsMatch(s, "(Z|[+\\-]\\d\\d:?\\d\\d)$") ? t.ToLocalTime() : t;
            return null;
        }
    }

    public class HaException : Exception { public int Status; public HaException(string m, int status) : base(m) { Status = status; } }

    public class Ha
    {
        public readonly string Url; readonly string token;
        public int TimeoutMs = 10000;
        public Ha(string url, string token) { Url = NormalizeUrl(url); this.token = token; }
        // several camera streams + the REST calls to one host at once: .NET allows only 2 connections per host by default
        static Ha() { if (ServicePointManager.DefaultConnectionLimit < 48) ServicePointManager.DefaultConnectionLimit = 48; }

        public static bool ValidEntityId(string id) { return id != null && id.Length <= 255 && Regex.IsMatch(id, "^[a-z0-9_]+\\.[a-z0-9_]+$"); }
        // "nas:8123" -> "http://nas:8123"; anything that isn't a plain http(s) base URL -> null
        public static string NormalizeUrl(string s)
        {
            s = (s ?? "").Trim(); if (s.Length == 0) return null;
            if (!Regex.IsMatch(s, "^https?://", RegexOptions.IgnoreCase)) { if (s.Contains("://")) return null; s = "http://" + s; }
            s = s.TrimEnd('/');
            Uri u; if (!Uri.TryCreate(s, UriKind.Absolute, out u) || (u.Scheme != "http" && u.Scheme != "https") || u.Host.Length == 0 || u.Query.Length > 0 || u.Fragment.Length > 0 || u.UserInfo.Length > 0) return null;
            return u.GetLeftPart(UriPartial.Path).TrimEnd('/');
        }
        // plain http to a host that isn't on this network = the token would cross the internet unencrypted
        public static bool InsecureRemote(string url)
        {
            Uri u; if (url == null || !Uri.TryCreate(url, UriKind.Absolute, out u) || u.Scheme == "https") return false;
            var h = u.Host.ToLowerInvariant();
            if (h == "localhost" || !h.Contains(".") || h.EndsWith(".local") || h.EndsWith(".lan") || h.EndsWith(".home.arpa")) return false;
            IPAddress ip; if (IPAddress.TryParse(h, out ip)) { var b = ip.GetAddressBytes(); return !(IPAddress.IsLoopback(ip) || b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] < 32) || (b[0] == 192 && b[1] == 168) || (b[0] == 100 && b[1] >= 64 && b[1] < 128)); }
            return true;
        }

        HttpWebRequest Req(string method, string path)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var r = (HttpWebRequest)WebRequest.Create(Url + path);
            r.Method = method; r.Timeout = TimeoutMs; r.ReadWriteTimeout = TimeoutMs; r.UserAgent = "HallowHome";
            r.Headers[HttpRequestHeader.Authorization] = "Bearer " + token;
            r.Proxy = null; r.AllowAutoRedirect = false;
            return r;
        }
        static string Read(WebResponse resp) { using (var s = resp.GetResponseStream()) using (var rd = new StreamReader(s, Encoding.UTF8)) return rd.ReadToEnd(); }
        // turns a failure into words a person can act on (never the token)
        static HaException Explain(Exception e)
        {
            var we = e as WebException;
            if (we != null && we.Response is HttpWebResponse)
            {
                var code = (int)((HttpWebResponse)we.Response).StatusCode; we.Response.Close();
                if (code == 401 || code == 403) return new HaException("Home Assistant refused the token (" + code + "). Make a new long-lived access token in your HA profile (Security tab) and paste it again.", code);
                if (code == 404) return new HaException("Home Assistant answered 404 - is that the right address (port 8123)?", code);
                if (code >= 300 && code < 400) return new HaException("The address redirects (" + code + ") - often a login page in front of Home Assistant (Cloudflare Access). Use the address Hallow Home can reach directly, or a service token.", code);
                return new HaException("Home Assistant answered " + code + ".", code);
            }
            if (we != null && we.Status == WebExceptionStatus.Timeout) return new HaException("Home Assistant didn't answer in time.", 0);
            if (we != null && (we.Status == WebExceptionStatus.NameResolutionFailure)) return new HaException("That address can't be found on this network.", 0);
            if (we != null && we.Status == WebExceptionStatus.ConnectFailure) return new HaException("Nothing answered at that address - is Home Assistant running?", 0);
            if (we != null && (we.Status == WebExceptionStatus.TrustFailure || we.Status == WebExceptionStatus.SecureChannelFailure)) return new HaException("The HTTPS certificate isn't trusted.", 0);
            return new HaException("Couldn't reach Home Assistant: " + Family.OneLine(e.Message), 0);
        }
        public string Get(string path)
        {
            try { using (var resp = Req("GET", path).GetResponse()) return Read(resp); }
            catch (Exception e) { throw Explain(e); }
        }
        public byte[] GetBytes(string path)
        {
            try { using (var resp = Req("GET", path).GetResponse()) using (var s = resp.GetResponseStream()) using (var ms = new MemoryStream()) { s.CopyTo(ms); return ms.ToArray(); } }
            catch (Exception e) { throw Explain(e); }
        }
        public string Post(string path, string json)
        {
            try
            {
                var r = Req("POST", path); r.ContentType = "application/json";
                var b = Encoding.UTF8.GetBytes(json ?? "{}"); r.ContentLength = b.Length;
                using (var s = r.GetRequestStream()) s.Write(b, 0, b.Length);
                using (var resp = r.GetResponse()) return Read(resp);
            }
            catch (Exception e) { throw Explain(e); }
        }
        // "API running." from GET /api/ - the token works
        public string Check()
        {
            var r = Get("/api/");
            if (!r.Contains("API running")) throw new HaException("That doesn't look like Home Assistant's API.", 0);
            return null;
        }
        public string Version() { try { var d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(Get("/api/config")); object v; return d != null && d.TryGetValue("version", out v) ? v as string : null; } catch { return null; } }
        public List<Entity> States() { return Entity.ParseStates(Get("/api/states")); }
        public void Call(string domain, string service, Dictionary<string, object> data)
        {
            if (!Regex.IsMatch(domain ?? "", "^[a-z_]+$") || !Regex.IsMatch(service ?? "", "^[a-z_]+$")) throw new HaException("Bad service name.", 0);
            Post("/api/services/" + domain + "/" + service, Family.Json(data ?? new Dictionary<string, object>()));
        }
        public byte[] Snapshot(string camera) { return GetBytes("/api/camera_proxy/" + camera); }
        // the MJPEG stream (Cameras.Mjpeg reads it); the caller disposes the response
        public WebResponse OpenStream(string camera)
        {
            var r = Req("GET", "/api/camera_proxy_stream/" + camera); r.Timeout = TimeoutMs; r.ReadWriteTimeout = 30000;
            try { return r.GetResponse(); } catch (Exception e) { throw Explain(e); }
        }

        // ---------------- the WebSocket: state_changed events, reconnecting with a growing pause
        public event Action<Entity, string> StateChanged;   // new state (null when removed), entity id
        public event Action<bool, string> Live;              // connected?, error
        volatile bool stop; Thread wsThread; ClientWebSocket ws;
        public bool IsLive { get; private set; }
        public void StartEvents()
        {
            stop = false;
            wsThread = new Thread(EventLoop) { IsBackground = true, Name = "ha websocket" }; wsThread.Start();
        }
        public void StopEvents() { stop = true; var w = ws; if (w != null) try { w.Abort(); } catch { } }
        public static string WsUrl(string url) { return (url.StartsWith("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws") + url.Substring(url.IndexOf(':')) + "/api/websocket"; }
        void EventLoop()
        {
            int fails = 0;
            while (!stop)
            {
                string err = null;
                try { RunSocket(); fails = 0; }
                catch (Exception e) { var he = e as HaException; err = he != null ? he.Message : "Live updates stopped: " + Family.OneLine(e.Message); }
                IsLive = false; var h = Live; if (h != null) h(false, err);
                if (stop) break;
                var wait = Math.Min(60000, 2000 * (1 << Math.Min(5, fails++)));
                for (int i = 0; i < wait / 200 && !stop; i++) Thread.Sleep(200);
            }
        }
        void RunSocket()
        {
            using (var w = new ClientWebSocket())
            {
                ws = w; w.Options.KeepAliveInterval = TimeSpan.FromSeconds(30); w.Options.Proxy = null;
                if (!w.ConnectAsync(new Uri(WsUrl(Url)), CancellationToken.None).Wait(TimeoutMs)) throw new HaException("Home Assistant's live updates didn't connect in time.", 0);
                var js = new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 };
                var first = js.Deserialize<Dictionary<string, object>>(Receive(w));
                if (first == null || (first["type"] as string) != "auth_required") throw new HaException("That doesn't look like Home Assistant's WebSocket.", 0);
                Send(w, Family.Json(new Dictionary<string, object> { { "type", "auth" }, { "access_token", token } }));
                var auth = js.Deserialize<Dictionary<string, object>>(Receive(w));
                if (auth == null || (auth["type"] as string) != "auth_ok") throw new HaException("Home Assistant refused the token for live updates.", 401);
                Send(w, "{\"id\":1,\"type\":\"subscribe_events\",\"event_type\":\"state_changed\"}");
                IsLive = true; var h = Live; if (h != null) h(true, null);
                while (!stop && w.State == WebSocketState.Open)
                {
                    var msg = Receive(w); if (msg == null) break;
                    var d = js.DeserializeObject(msg) as Dictionary<string, object>; if (d == null) continue;
                    object t; if (!d.TryGetValue("type", out t) || (t as string) != "event") continue;
                    var ev = d["event"] as Dictionary<string, object>; if (ev == null) continue;
                    var data = ev.ContainsKey("data") ? ev["data"] as Dictionary<string, object> : null; if (data == null) continue;
                    var id = data.ContainsKey("entity_id") ? data["entity_id"] as string : null; if (!ValidEntityId(id)) continue;
                    var ns = Entity.From(data.ContainsKey("new_state") ? data["new_state"] as Dictionary<string, object> : null);
                    var sc = StateChanged; if (sc != null) sc(ns, id);
                }
                ws = null;
            }
        }
        static void Send(ClientWebSocket w, string text) { var b = Encoding.UTF8.GetBytes(text); w.SendAsync(new ArraySegment<byte>(b), WebSocketMessageType.Text, true, CancellationToken.None).Wait(10000); }
        static string Receive(ClientWebSocket w)
        {
            var buf = new byte[16384]; var ms = new MemoryStream();
            while (true)
            {
                var r = w.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None).Result;
                if (r.MessageType == WebSocketMessageType.Close) return null;
                ms.Write(buf, 0, r.Count);
                if (ms.Length > 32 * 1024 * 1024) throw new HaException("A live update was too big.", 0);
                if (r.EndOfMessage) return Encoding.UTF8.GetString(ms.ToArray());
            }
        }
    }
}
