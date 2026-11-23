// Hallow Home settings: %APPDATA%\HallowHome\settings.json. Nothing secret is kept here - the Home Assistant token, the Discord
// webhook and the ntfy topic are in secrets.dat, encrypted with Windows DPAPI for this Windows user (SecretStore.cs).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace HallowHome
{
    // "this entity is the feeder's food level": a role (Roles.All) pinned to an entity id; Entity = "" means "none, don't guess"
    [DataContract] public class RoleOverride { [DataMember] public string Role; [DataMember] public string Entity; }

    // a simple "when X then Y" rule, evaluated only while Hallow Home runs (Automations.cs)
    [DataContract] public class AutoRule
    {
        [DataMember] public string Id;
        [DataMember] public bool On;
        [DataMember] public string When;        // alert:<rule> / away / home / state:<entity>=<value>
        [DataMember] public string Then;        // notify / service:<domain>.<service>:<entity>
        [DataMember] public double RepeatHours; // 0 = once each time it becomes true; 12 = "twice a day until fixed"
    }

    [DataContract]
    public class HomeSettings
    {
        // developer tests: HALLOW_HOME_TESTDIR = data folder (and a separate pipe); HALLOW_HOME_HEADLESS=1 = no windows or tray.
        // Tests point Hallow Home at the fake Home Assistant (tests\FakeHa.cs) on localhost - there is no other fake mode.
        public static readonly string TestDir = Env("HALLOW_HOME_TESTDIR");
        public static readonly bool Headless = TestDir != null && Env("HALLOW_HOME_HEADLESS") == "1";
        public static bool Frozen;   // --snap: never saved
        static string Env(string n) { var v = Environment.GetEnvironmentVariable(n); return string.IsNullOrWhiteSpace(v) ? null : v; }
        public static string Dir { get { return TestDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HallowHome"); } }
        static string FilePath { get { return Path.Combine(Dir, "settings.json"); } }

        [DataMember] public bool UseHallowIcon = true;
        [DataMember] public bool UseHallowAccent = true;
        [DataMember] public string Look = "follow";            // follow / light / dark
        // Home Assistant
        [DataMember] public string HaUrl;                      // http://nas:8123 or https://home.example.com (the token is in secrets.dat)
        [DataMember] public List<RoleOverride> Overrides;
        // pets
        [DataMember] public double GramsPerPortion = 10;       // Petlibro: one portion is about 10 g of dry food
        [DataMember] public int MaxPortions = 6;               // "Feed now" never asks for more than this at once
        // cameras
        [DataMember] public string LiveBase;                   // docker-wyze-bridge WebRTC (http://nas:8889) or HLS (http://nas:8888) base - optional
        [DataMember] public string RecordingsFolder;           // where wyze-bridge / HA record, e.g. \\nas\docker\wyze-bridge\recordings
        // 1.2: docker-wyze-bridge's two players (MediaMTX): WebRTC http://nas:8889 and HLS http://nas:8888 (LiveBase is migrated into them)
        [DataMember] public string BridgeWebRtc, BridgeHls;
        [DataMember] public string StreamMode = "auto";        // auto / mjpeg (Home Assistant's camera proxy) / hls / webrtc
        [DataMember] public int GridSize = 2;                  // the camera grid: 2 = 2x2, 3 = 3x3
        // 1.2: the setup wizard and Demo mode
        [DataMember] public List<string> WizardSkipped;        // "ha" / "cameras" / "petlibro"
        [DataMember] public bool Demo;                         // made-up devices (an in-app stand-in for Home Assistant on localhost)
        [DataMember] public string NasFolder = "/volume1/docker/wyze-bridge";
        [DataMember] public bool BridgeRecord;
        // 1.2: fountain reminders - the integration's filter / cleaning days, or your own interval when it has none (or you prefer it)
        [DataMember] public bool OwnReminders;
        [DataMember] public int FilterEveryDays = 30, CleanEveryDays = 14;
        [DataMember] public string FilterChangedOn, CleanedOn;   // yyyy-MM-dd
        // alerts
        [DataMember] public List<string> RulesOff;            // alert rules switched off (Alerts.Rules ids)
        [DataMember] public bool UseHub = true, UseDiscord, UseNtfy;
        [DataMember] public bool QuietOn = true;
        [DataMember] public string QuietStart = "22:00", QuietEnd = "07:00";
        [DataMember] public bool GameHoldAll;                  // false: during a game only the PC stays quiet (the phone still gets alerts)
        [DataMember] public int FoodLowPct = 20, WaterLowPct = 20, IntakeLowPct = 50, PumpOffMinutes = 30, CameraOfflineMinutes = 5, JamGraceMinutes = 10, FilterDueDays = 2;
        [DataMember] public bool Away;                         // "I'm away" (manual; person.* entities in HA count too)
        [DataMember] public bool MotionAlerts;                 // motion while away (off by default)
        [DataMember] public string SnoozeUntil;
        [DataMember] public List<AutoRule> Automations;
        [DataMember] public string LastUpdateCheck;
        [DataMember] public string LastCrashShown;
        [DataMember] public bool TrayHintShown;

        [OnDeserializing] void Defaults(StreamingContext c) { UseHallowIcon = true; UseHallowAccent = true; Look = "follow"; GramsPerPortion = 10; MaxPortions = 6; UseHub = true; QuietOn = true; QuietStart = "22:00"; QuietEnd = "07:00"; FoodLowPct = 20; WaterLowPct = 20; IntakeLowPct = 50; PumpOffMinutes = 30; CameraOfflineMinutes = 5; JamGraceMinutes = 10; FilterDueDays = 2; StreamMode = "auto"; GridSize = 2; NasFolder = "/volume1/docker/wyze-bridge"; FilterEveryDays = 30; CleanEveryDays = 14; }

        public static HomeSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    using (var f = File.OpenRead(FilePath))
                    {
                        var s = (HomeSettings)new DataContractJsonSerializer(typeof(HomeSettings)).ReadObject(f);
                        if (s != null) { s.Fix(); return s; }
                    }
            }
            catch { }
            var d = new HomeSettings(); d.Fix(); return d;
        }
        public static List<AutoRule> DefaultAutomations()
        {
            return new List<AutoRule> {
                new AutoRule { Id = "a1", On = true, When = "alert:water-low", Then = "notify", RepeatHours = 12 },
                new AutoRule { Id = "a2", On = false, When = "away", Then = "service:switch.turn_on:", RepeatHours = 0 } };
        }
        public void Fix()
        {
            if (Overrides == null) Overrides = new List<RoleOverride>();
            Overrides.RemoveAll(o => o == null || Roles.Find(o.Role) == null || o.Entity == null || (o.Entity != "" && !Ha.ValidEntityId(o.Entity)));
            if (RulesOff == null) RulesOff = new List<string>();
            if (Automations == null) Automations = DefaultAutomations();
            Automations.RemoveAll(a => a == null || string.IsNullOrEmpty(a.When) || string.IsNullOrEmpty(a.Then));
            Look = Mode.Own(Look);
            HaUrl = Ha.NormalizeUrl(HaUrl);
            if (GramsPerPortion < 1 || GramsPerPortion > 100) GramsPerPortion = 10;
            if (MaxPortions < 1 || MaxPortions > 12) MaxPortions = 6;
            if (!Quiet.ValidTime(QuietStart)) QuietStart = "22:00";
            if (!Quiet.ValidTime(QuietEnd)) QuietEnd = "07:00";
            FoodLowPct = Clamp(FoodLowPct, 5, 60, 20); WaterLowPct = Clamp(WaterLowPct, 5, 60, 20); IntakeLowPct = Clamp(IntakeLowPct, 10, 90, 50);
            PumpOffMinutes = Clamp(PumpOffMinutes, 5, 720, 30); CameraOfflineMinutes = Clamp(CameraOfflineMinutes, 1, 120, 5); JamGraceMinutes = Clamp(JamGraceMinutes, 3, 120, 10); FilterDueDays = Clamp(FilterDueDays, 0, 14, 2);
            if (LiveBase != null) { LiveBase = LiveBase.Trim().TrimEnd('/'); if (!Regex.IsMatch(LiveBase, "^https?://[^\\s/?#]+$", RegexOptions.IgnoreCase)) LiveBase = null; }
            BridgeWebRtc = BaseUrl(BridgeWebRtc); BridgeHls = BaseUrl(BridgeHls);
            if (LiveBase != null && BridgeWebRtc == null && BridgeHls == null)
            {
                // 1.1's one "live view address": the same host gives both players on their usual ports
                Uri u; if (Uri.TryCreate(LiveBase, UriKind.Absolute, out u))
                {
                    var host = u.Scheme + "://" + u.Host;
                    if (u.Port == 8888) { BridgeHls = LiveBase; BridgeWebRtc = host + ":8889"; }
                    else if (u.Port == 8889) { BridgeWebRtc = LiveBase; BridgeHls = host + ":8888"; }
                    else BridgeWebRtc = LiveBase;
                }
            }
            LiveBase = null;
            if (StreamMode != "mjpeg" && StreamMode != "hls" && StreamMode != "webrtc") StreamMode = "auto";
            if (GridSize != 3) GridSize = 2;
            if (WizardSkipped == null) WizardSkipped = new List<string>();
            WizardSkipped.RemoveAll(x => x != "ha" && x != "cameras" && x != "petlibro");
            if (string.IsNullOrWhiteSpace(NasFolder) || !Regex.IsMatch(NasFolder.Trim(), "^/[A-Za-z0-9_./\\-]+$") || NasFolder.Contains("..")) NasFolder = "/volume1/docker/wyze-bridge";
            NasFolder = NasFolder.Trim().TrimEnd('/');
            FilterEveryDays = Clamp(FilterEveryDays, 1, 180, 30); CleanEveryDays = Clamp(CleanEveryDays, 1, 90, 14);
            FilterChangedOn = Day(FilterChangedOn); CleanedOn = Day(CleanedOn);
        }
        // a bridge player's base address (http://nas:8889) or null
        public static string BaseUrl(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null; s = s.Trim().TrimEnd('/');
            if (!Regex.IsMatch(s, "^https?://", RegexOptions.IgnoreCase)) { if (s.Contains("://")) return null; s = "http://" + s; }
            return Regex.IsMatch(s, "^https?://[^\\s/?#@]+$", RegexOptions.IgnoreCase) ? s : null;
        }
        public static string Day(string s) { DateTime d; return s != null && DateTime.TryParseExact(s, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out d) ? s : null; }
        static int Clamp(int v, int lo, int hi, int def) { return v < lo || v > hi ? def : v; }
        public string Override(string role) { var o = Overrides.FirstOrDefault(x => x.Role == role); return o == null ? null : o.Entity; }
        public void SetOverride(string role, string entity)
        {
            Overrides.RemoveAll(x => x.Role == role);
            if (entity != null) Overrides.Add(new RoleOverride { Role = role, Entity = entity });
        }
        public bool RuleOn(string id) { return !RulesOff.Contains(id); }

        static readonly object saveLock = new object();
        public void Save()
        {
            if (Frozen) return;
            lock (saveLock)
            {
                try
                {
                    Directory.CreateDirectory(Dir);
                    var tmp = FilePath + ".tmp";
                    using (var f = File.Create(tmp)) new DataContractJsonSerializer(typeof(HomeSettings)).WriteObject(f, this);
                    for (int i = 0; ; i++) { try { if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null); else File.Move(tmp, FilePath); break; } catch { if (i >= 5) throw; Thread.Sleep(50); } }
                }
                catch { }
            }
        }
    }
}
