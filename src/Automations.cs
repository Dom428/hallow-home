// Hallow Home - simple automations: "when X then Y", kept in the app and evaluated only while Hallow Home runs (they are NOT
// Home Assistant automations - for anything that must run when the PC is off, make it in HA itself).
//   When:  alert:<rule>          one of the smart alerts is true (e.g. alert:water-low)
//          away / home           everyone is away (HA's person.* entities, or "I'm away" in Hallow Home) / someone is home
//          state:<entity>=<val>  an entity has that state
//   Then:  notify                send the alert again through the alert channels (a reminder)
//          service:<domain>.<service>:<entity>[,<entity>]   only switch / light / camera turn_on / turn_off - never a lock, never feeding
//   Repeat every N hours while it stays true (12 = "twice a day until it's fixed"); 0 = once each time it becomes true.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace HallowHome
{
    public class AutoAction { public AutoRule Rule; public string Kind; public string Domain, Service; public List<string> Entities = new List<string>(); public Alert About; public string Why; }

    public class AutomationEngine
    {
        public static readonly string[] AllowedServices = { "switch.turn_on", "switch.turn_off", "light.turn_on", "light.turn_off", "camera.turn_on", "camera.turn_off" };
        readonly Dictionary<string, DateTime> lastRun = new Dictionary<string, DateTime>();

        // null = fine; otherwise what's wrong with the rule
        public static string Check(AutoRule r)
        {
            if (r == null) return "No rule.";
            if (!Regex.IsMatch(r.When ?? "", "^(alert:[a-z\\-]+|away|home|state:[a-z0-9_]+\\.[a-z0-9_]+=.{1,64})$")) return "\"When\" isn't one of: alert:<rule>, away, home, state:<entity>=<value>.";
            if (r.When.StartsWith("alert:") && !AlertEngine.Rules.Any(x => x[0] == r.When.Substring(6))) return "There's no alert called " + r.When.Substring(6) + ".";
            if (r.Then == "notify") return null;
            var m = Regex.Match(r.Then ?? "", "^service:([a-z_]+)\\.([a-z_]+):(.*)$");
            if (!m.Success) return "\"Then\" is notify or service:<domain>.<service>:<entity>.";
            if (!AllowedServices.Contains(m.Groups[1].Value + "." + m.Groups[2].Value)) return "Automations may only turn switches, lights and cameras on or off - never a lock, never feeding.";
            var ents = Targets(r.Then);
            if (ents.Any(e => !Ha.ValidEntityId(e) || !e.StartsWith(m.Groups[1].Value + "."))) return "The entities must be " + m.Groups[1].Value + ".* entities.";
            if (r.RepeatHours < 0 || r.RepeatHours > 168) return "Repeat between 0 and 168 hours.";
            return null;
        }
        public static List<string> Targets(string then)
        {
            var m = Regex.Match(then ?? "", "^service:[a-z_]+\\.[a-z_]+:(.*)$");
            return m.Success ? m.Groups[1].Value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0).ToList() : new List<string>();
        }
        public static string Describe(AutoRule r)
        {
            string w = r.When == "away" ? "everyone is away" : r.When == "home" ? "someone is home" : r.When.StartsWith("alert:") ? "\"" + AlertEngine.RuleTitle(r.When.Substring(6)) + "\" is true" : r.When.StartsWith("state:") ? r.When.Substring(6).Replace("=", " is ") : r.When;
            string t; if (r.Then == "notify") t = "remind me";
            else { var m = Regex.Match(r.Then, "^service:([a-z_]+)\\.([a-z_]+):"); var tg = Targets(r.Then); t = (m.Groups[2].Value == "turn_on" ? "turn on " : "turn off ") + (tg.Count == 0 ? "(pick " + m.Groups[1].Value + " entities)" : string.Join(", ", tg)); }
            var rep = r.RepeatHours > 0 ? (Math.Abs(r.RepeatHours - 12) < 0.01 ? ", twice a day until it's fixed" : ", every " + Read.Num(r.RepeatHours) + " h while it's true") : "";
            return "When " + w + " → " + t + rep;
        }

        static bool Holds(AutoRule r, List<Alert> active, Dictionary<string, Entity> byId, bool away, out Alert about)
        {
            about = null;
            if (r.When.StartsWith("alert:")) { about = active.FirstOrDefault(a => a.Rule == r.When.Substring(6)); return about != null; }
            if (r.When == "away") return away;
            if (r.When == "home") return !away;
            if (r.When.StartsWith("state:"))
            {
                var kv = r.When.Substring(6); var i = kv.IndexOf('='); Entity e;
                return i > 0 && byId.TryGetValue(kv.Substring(0, i), out e) && string.Equals(e.State, kv.Substring(i + 1), StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }
        // what to do now
        public List<AutoAction> Evaluate(DateTime now, IEnumerable<AutoRule> rules, List<Alert> active, Dictionary<string, Entity> byId, bool away)
        {
            var l = new List<AutoAction>();
            foreach (var r in rules)
            {
                if (!r.On || Check(r) != null) { lastRun.Remove(r.Id ?? ""); continue; }
                Alert about; var key = r.Id ?? r.When + r.Then;
                if (!Holds(r, active, byId, away, out about)) { lastRun.Remove(key); continue; }
                DateTime last;
                if (lastRun.TryGetValue(key, out last) && (r.RepeatHours <= 0 || now - last < TimeSpan.FromHours(r.RepeatHours))) continue;
                bool first = !lastRun.ContainsKey(key);
                lastRun[key] = now;
                var a = new AutoAction { Rule = r, About = about, Why = Describe(r) };
                if (r.Then == "notify")
                {
                    if (first && about != null) continue;   // the alert itself just went out - the reminder starts with the next repeat
                    a.Kind = "notify";
                }
                else { var m = Regex.Match(r.Then, "^service:([a-z_]+)\\.([a-z_]+):"); a.Kind = "service"; a.Domain = m.Groups[1].Value; a.Service = m.Groups[2].Value; a.Entities = Targets(r.Then); if (a.Entities.Count == 0) continue; }
                l.Add(a);
            }
            return l;
        }
    }
}
