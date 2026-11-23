// Hallow Home 1.2 - the guided setup: three steps that make the NAS session short, each skippable, each showing what's missing.
//   1 Home Assistant  paste the address, test it, make a long-lived token (the exact clicks), test again
//   2 Cameras         a docker-compose snippet for docker-wyze-bridge, ready for Synology Container Manager, with clearly marked
//                     placeholders - Hallow Home never asks for your Wyze password or keys; you type them on the NAS only
//   3 Petlibro        the community integration through HACS
// Plus Demo mode (made-up devices) to explore the app before any of it exists.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace HallowHome
{
    public class WizStep { public string Id, Title; public bool Done, Skipped; public List<string> Missing = new List<string>(); }

    public static class Wizard
    {
        public const string PetlibroRepo = "https://github.com/jjjonesjr33/petlibro";
        public const string Placeholder = "<REPLACE";   // every placeholder in the snippet starts with this

        // where each step stands, from what Hallow Home knows now
        public static List<WizStep> Steps(Controller c)
        {
            var l = new List<WizStep>();
            var ha = new WizStep { Id = "ha", Title = "Home Assistant" };
            if (c.S.HaUrl == null) ha.Missing.Add("Home Assistant's address (like http://nas:8123)");
            if (!SecretStore.Has(SecretStore.Token)) ha.Missing.Add("a long-lived access token from Home Assistant");
            if (c.S.HaUrl != null && SecretStore.Has(SecretStore.Token) && !c.S.Demo && !c.Connected) ha.Missing.Add("a working connection (" + (c.LastError ?? "not connected yet") + ")");
            ha.Done = ha.Missing.Count == 0 && !c.S.Demo && c.Connected;
            if (c.S.Demo && ha.Missing.Count == 0 && !c.Connected) ha.Missing.Add("a working connection (Demo mode is on)");
            l.Add(ha);

            var cam = new WizStep { Id = "cameras", Title = "Cameras (docker-wyze-bridge)" };
            if (c.S.BridgeWebRtc == null && c.S.BridgeHls == null) cam.Missing.Add("the bridge's address in Hallow Home (filled in from step 2)");
            else if (c.BridgeWebRtcOk != true && c.BridgeHlsOk != true) cam.Missing.Add("an answer from the bridge (" + (c.BridgeWebRtcOk == null && c.BridgeHlsOk == null ? "not tested yet" : "no player found") + ")");
            if (!c.Connected || c.Map.Cameras.Count(x => c.Feeder.Camera == null || x.Id != c.Feeder.Camera.Id) == 0) cam.Missing.Add("the Wyze cameras in Home Assistant (Generic Camera, one per camera)");
            cam.Done = cam.Missing.Count == 0 && !c.S.Demo;
            l.Add(cam);

            var pet = new WizStep { Id = "petlibro", Title = "Petlibro (feeder and fountain)" };
            if (!c.Connected || !c.Feeder.Present) pet.Missing.Add("the feeder in Home Assistant (the Petlibro integration)");
            if (!c.Connected || !c.Fountain.Present) pet.Missing.Add("the fountain in Home Assistant (the same integration)");
            if (c.Connected && c.Feeder.Present && !c.Feeder.Schedule.Present) pet.Missing.Add("the feeder's Feeding schedule sensor (a newer integration version has it)");
            pet.Done = pet.Missing.Count == 0 && !c.S.Demo;
            l.Add(pet);
            foreach (var s in l) s.Skipped = !s.Done && c.S.WizardSkipped.Contains(s.Id);
            return l;
        }

        // ---- step 2: the docker-compose snippet (Synology Container Manager > Project > Create > paste)
        public class ComposeOptions { public string Folder = "/volume1/docker/wyze-bridge", Tz, NasHost; public bool Record; }
        public static string Compose(ComposeOptions o)
        {
            var folder = (o.Folder ?? "").Trim().TrimEnd('/');
            if (!Regex.IsMatch(folder, "^/[A-Za-z0-9_./\\-]+$") || folder.Contains("..")) folder = "/volume1/docker/wyze-bridge";
            var tz = o.Tz != null && Regex.IsMatch(o.Tz, "^[A-Za-z_]+(/[A-Za-z0-9_+\\-]+){0,2}$") ? o.Tz : null;
            var sb = new StringBuilder();
            Action<string> L = s => sb.Append(s).Append('\n');
            L("# docker-wyze-bridge for Hallow Home - Synology: Container Manager > Project > Create,");
            L("# path " + folder + ", \"Create docker-compose.yml\", paste this, then Next > Done.");
            L("# Replace every <REPLACE...> line ON THE NAS ONLY. Hallow Home never asks for your Wyze password or keys.");
            L("# API ID / key: https://developer-api-console.wyze.com (sign in > Create an API key).");
            L("services:");
            L("  wyze-bridge:");
            L("    container_name: wyze-bridge");
            L("    image: mrlt8/wyze-bridge:latest      # check the project's README for the current image (forks carry it on)");
            L("    restart: unless-stopped");
            L("    ports:                               # LAN only - never forward these on your router or through a tunnel");
            L("      - 8554:8554                        # RTSP   (Home Assistant reads the cameras here)");
            L("      - 8888:8888                        # HLS    (Hallow Home's players)");
            L("      - 8889:8889                        # WebRTC (Hallow Home's players)");
            L("      - 8189:8189/udp                    # WebRTC media");
            L("      - 5000:5000                        # the bridge's own web page (lists your camera names)");
            L("    environment:");
            L("      - WYZE_EMAIL=<REPLACE with your Wyze e-mail>");
            L("      - WYZE_PASSWORD=<REPLACE with your Wyze password - type it here on the NAS, never in Hallow Home>");
            L("      - API_ID=<REPLACE with your Wyze API ID>");
            L("      - API_KEY=<REPLACE with your Wyze API key>");
            L("      - TZ=" + (tz ?? "<REPLACE with your time zone, e.g. America/New_York>"));
            L("      - WB_AUTH=True                     # the bridge's web page needs a login (see its README)");
            L("      - RECORD_ALL=" + (o.Record ? "True                  # records every camera to " + folder + "/record (watch the disk)" : "False                 # True records every camera (uses disk)"));
            L("    volumes:");
            L("      - " + folder + "/tokens:/tokens       # keeps the bridge signed in across restarts");
            if (o.Record) L("      - " + folder + "/record:/record");
            return sb.ToString();
        }
        // the Windows time zone -> the name Docker wants (the common ones; anything else stays a placeholder)
        public static string IanaZone(string windowsId)
        {
            var m = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
                { "Eastern Standard Time", "America/New_York" }, { "Central Standard Time", "America/Chicago" }, { "Mountain Standard Time", "America/Denver" },
                { "US Mountain Standard Time", "America/Phoenix" }, { "Pacific Standard Time", "America/Los_Angeles" }, { "Alaskan Standard Time", "America/Anchorage" },
                { "Hawaiian Standard Time", "Pacific/Honolulu" }, { "Atlantic Standard Time", "America/Halifax" }, { "Newfoundland Standard Time", "America/St_Johns" },
                { "GMT Standard Time", "Europe/London" }, { "W. Europe Standard Time", "Europe/Berlin" }, { "Romance Standard Time", "Europe/Paris" },
                { "Central Europe Standard Time", "Europe/Budapest" }, { "Central European Standard Time", "Europe/Warsaw" }, { "E. Europe Standard Time", "Europe/Chisinau" },
                { "FLE Standard Time", "Europe/Kiev" }, { "GTB Standard Time", "Europe/Bucharest" }, { "AUS Eastern Standard Time", "Australia/Sydney" },
                { "E. Australia Standard Time", "Australia/Brisbane" }, { "W. Australia Standard Time", "Australia/Perth" }, { "New Zealand Standard Time", "Pacific/Auckland" },
                { "Tokyo Standard Time", "Asia/Tokyo" }, { "India Standard Time", "Asia/Kolkata" }, { "China Standard Time", "Asia/Shanghai" }, { "UTC", "Etc/UTC" } };
            string v; return windowsId != null && m.TryGetValue(windowsId, out v) ? v : null;
        }
        public static string NasHost(string haUrl)
        {
            Uri u; return haUrl != null && Uri.TryCreate(haUrl, UriKind.Absolute, out u) && u.Host.Length > 0 && u.Host != "localhost" ? u.Host : "nas";
        }
        // the clicks in Home Assistant, step by step (the token is shown once)
        public static readonly string[] TokenSteps = {
            "Open Home Assistant in your browser and sign in.",
            "Click your name at the bottom of the left sidebar (your profile).",
            "Open the Security tab at the top.",
            "Scroll down to \"Long-lived access tokens\" and click Create token.",
            "Name it Hallow Home and click OK.",
            "Copy the token now - Home Assistant shows it only once - and paste it below." };
        public static string[] CameraSteps(string host)
        {
            return new[] {
                "On the NAS: Container Manager > Project > Create, paste the snippet, replace the <REPLACE...> lines, Done.",
                "Open http://" + host + ":5000 - the bridge's page lists each camera's name (like front-door).",
                "In Home Assistant: Settings > Devices & services > Add integration > Generic Camera, once per camera:",
                "    Stream source: rtsp://" + host + ":8554/<camera-name>     Still image: http://" + host + ":5000/snapshot/<camera-name>.jpg",
                "Name each camera like in the Wyze app, then come back here and press Test the bridge." };
        }
        public static readonly string[] PetlibroSteps = {
            "Petlibro allows one signed-in session per account: make a second Petlibro account and, in the Petlibro app, share the feeder and fountain to it (your phone stays signed in).",
            "In Home Assistant: HACS > the three dots (top right) > Custom repositories.",
            "Repository: " + PetlibroRepo + "   Type: Integration > Add.",
            "Search HACS for PETLIBRO > Download (the newest version has the feeding-schedule services).",
            "Settings > System > Restart Home Assistant.",
            "Settings > Devices & services > Add integration > PETLIBRO, and sign in there with the second account (Hallow Home never sees it)." };
    }
}
