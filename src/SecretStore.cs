// Hallow Home - the secrets: the Home Assistant long-lived access token, the Discord webhook URL and the ntfy topic URL (+ optional
// ntfy access token). Kept in %APPDATA%\HallowHome\secrets.dat, encrypted with Windows DPAPI for this Windows user only
// (DataProtectionScope.CurrentUser). Never logged, never in status, the pipe, settings.json, crash reports (Crash.Secrets) or on a
// command line. The UI only ever shows them masked.
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace HallowHome
{
    public static class SecretStore
    {
        public const string Token = "token", Discord = "discord", Ntfy = "ntfy", NtfyToken = "ntfyToken";
        static readonly byte[] Entropy = Encoding.UTF8.GetBytes("HallowHome.secrets.v1");
        public static string FilePath { get { return Path.Combine(HomeSettings.Dir, "secrets.dat"); } }
        static Dictionary<string, string> snap;   // --snap: made-up values in memory only
        static readonly object gate = new object();

        // null = fine; otherwise why it can't be that secret
        public static string Check(string name, string value)
        {
            value = (value ?? "").Trim();
            if (value.Length == 0) return null;   // empty = remove
            if (value.Length > 2048) return "That's too long.";
            if (Regex.IsMatch(value, "\\s")) return "It can't contain spaces or line breaks - check that you copied it whole.";
            switch (name)
            {
                case Token: return value.Length < 20 ? "That's too short for a Home Assistant long-lived access token (Profile > Security > Long-lived access tokens)." : null;
                case Discord: return Regex.IsMatch(value, "^https://(discord\\.com|discordapp\\.com|canary\\.discord\\.com|ptb\\.discord\\.com)/api/webhooks/\\d+/[A-Za-z0-9_\\-]+$") || TestUrl(value) ? null : "That isn't a Discord webhook URL (https://discord.com/api/webhooks/...). Make one in your server: Server settings > Integrations > Webhooks.";
                case Ntfy: return Regex.IsMatch(value, "^https://[A-Za-z0-9.\\-]+(:\\d+)?/[A-Za-z0-9_\\-]{1,64}$") || TestUrl(value) ? null : "That isn't an ntfy topic URL, e.g. https://ntfy.sh/a-long-random-topic-name (https only).";
                case NtfyToken: return null;
            }
            return "Unknown secret.";
        }
        // developer tests only: a local stand-in on http://localhost
        static bool TestUrl(string v) { return HomeSettings.TestDir != null && Regex.IsMatch(v, "^http://(localhost|127\\.0\\.0\\.1):\\d+/[A-Za-z0-9_/\\-]+$"); }

        public static byte[] Protect(Dictionary<string, string> d)
        {
            var plain = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(d));
            try { return ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser); }
            finally { Array.Clear(plain, 0, plain.Length); }
        }
        public static Dictionary<string, string> Unprotect(byte[] data)
        {
            byte[] plain = null;
            try
            {
                plain = ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);
                var d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(plain));
                var r = new Dictionary<string, string>();
                if (d != null) foreach (var kv in d) if (kv.Value is string) r[kv.Key] = (string)kv.Value;
                return r;
            }
            catch { return null; }
            finally { if (plain != null) Array.Clear(plain, 0, plain.Length); }
        }

        static Dictionary<string, string> LoadAll()
        {
            if (snap != null) return snap;
            try { if (File.Exists(FilePath)) { var d = Unprotect(File.ReadAllBytes(FilePath)); if (d != null) { Remember(d); return d; } } } catch { }
            return new Dictionary<string, string>();
        }
        static void Remember(Dictionary<string, string> d) { var l = new List<string>(); foreach (var v in d.Values) if (!string.IsNullOrEmpty(v)) l.Add(v); Crash.Secrets = l.ToArray(); }

        public static string Get(string name) { lock (gate) { string v; return LoadAll().TryGetValue(name, out v) && v.Length > 0 ? v : null; } }
        public static bool Has(string name) { return Get(name) != null; }
        // saves (or removes, when empty) one secret; returns an error message or null
        public static string Set(string name, string value)
        {
            var err = Check(name, value); if (err != null) return err;
            value = (value ?? "").Trim();
            lock (gate)
            {
                var d = LoadAll();
                if (value.Length == 0) d.Remove(name); else d[name] = value;
                if (snap != null || HomeSettings.Frozen) { Remember(d); return null; }
                try
                {
                    Directory.CreateDirectory(HomeSettings.Dir);
                    var tmp = FilePath + ".tmp";
                    File.WriteAllBytes(tmp, Protect(d));
                    if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null); else File.Move(tmp, FilePath);
                    Remember(d);
                    return null;
                }
                catch (Exception e) { return "Couldn't save it: " + e.Message; }
            }
        }
        public static void UseSnapValues() { snap = new Dictionary<string, string> { { Token, "made-up-token-for-pictures-0000000000" } }; }
        // what the UI shows: the first and last characters only
        public static string Mask(string v)
        {
            if (string.IsNullOrEmpty(v)) return "";
            if (v.Length <= 12) return new string('•', 8);
            return v.Substring(0, 4) + new string('•', 8) + v.Substring(v.Length - 3);
        }
        // a URL with its secret path hidden: https://discord.com/api/webhooks/•••• (for "Saved: ..." lines)
        public static string MaskUrl(string v)
        {
            try { var u = new Uri(v); return u.Scheme + "://" + u.Host + "/" + new string('•', 8); } catch { return Mask(v); }
        }
    }
}
