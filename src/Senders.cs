// Hallow Home - where alerts go besides the PC (Hallow Hub's notify / its own tray balloon): a Discord webhook and an ntfy topic,
// both set by you and kept encrypted (SecretStore). Only the alert's title and text are sent - no camera pictures, URLs or tokens.
// Errors never include the webhook / topic address.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;

namespace HallowHome
{
    public interface ISender { string Discord(string title, string text); string Ntfy(string title, string text, bool urgent); }

    public class RealSender : ISender
    {
        static string Post(string url, byte[] body, string contentType, Dictionary<string, string> headers)
        {
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                var r = (HttpWebRequest)WebRequest.Create(url);
                r.Method = "POST"; r.ContentType = contentType; r.Timeout = 10000; r.UserAgent = "HallowHome"; r.AllowAutoRedirect = false;
                if (headers != null) foreach (var kv in headers) r.Headers[kv.Key] = kv.Value;
                r.ContentLength = body.Length;
                using (var s = r.GetRequestStream()) s.Write(body, 0, body.Length);
                using (var resp = (HttpWebResponse)r.GetResponse()) return (int)resp.StatusCode < 300 ? null : "answered " + (int)resp.StatusCode;
            }
            catch (WebException e) { var h = e.Response as HttpWebResponse; return h != null ? "answered " + (int)h.StatusCode : "couldn't be reached"; }
            catch { return "couldn't be reached"; }
        }
        // HTTP headers are ASCII: ntfy's Title header gets the plain-ASCII version
        static string Ascii(string s) { var sb = new StringBuilder(); foreach (var c in s ?? "") sb.Append(c >= 32 && c < 127 ? c : c == '’' ? '\'' : c == '–' || c == '—' ? '-' : '?'); return sb.ToString(); }
        public string Discord(string title, string text)
        {
            var url = SecretStore.Get(SecretStore.Discord); if (url == null) return "no Discord webhook set";
            var json = Family.Json(new Dictionary<string, object> { { "username", "Hallow Home" }, { "content", "**" + title + "**\n" + text }, { "allowed_mentions", new Dictionary<string, object> { { "parse", new string[0] } } } });
            var e = Post(url, Encoding.UTF8.GetBytes(json), "application/json", null);
            return e == null ? null : "Discord " + e;
        }
        public string Ntfy(string title, string text, bool urgent)
        {
            var url = SecretStore.Get(SecretStore.Ntfy); if (url == null) return "no ntfy topic set";
            var h = new Dictionary<string, string> { { "Title", Ascii(title) }, { "Tags", urgent ? "warning" : "house" }, { "Priority", urgent ? "high" : "default" } };
            var tok = SecretStore.Get(SecretStore.NtfyToken); if (tok != null) h["Authorization"] = "Bearer " + tok;
            var e = Post(url, Encoding.UTF8.GetBytes(text), "text/plain; charset=utf-8", h);
            return e == null ? null : "ntfy " + e;
        }
    }

    // tests and --snap: remembers what would have been sent
    public class FakeSender : ISender
    {
        public readonly List<string> Sent = new List<string>();
        public string Discord(string title, string text) { lock (Sent) Sent.Add("discord|" + title); return null; }
        public string Ntfy(string title, string text, bool urgent) { lock (Sent) Sent.Add("ntfy|" + title); return null; }
    }
}
