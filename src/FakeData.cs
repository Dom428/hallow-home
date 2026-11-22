// Hallow Home - made-up devices for --snap pictures and for the fake Home Assistant in the tests. They are modelled on what the
// community Petlibro integration (HACS) and the Wyze integration / docker-wyze-bridge cameras expose, with invented names and
// values - nothing here comes from a real home, account or address.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace HallowHome
{
    public static class FakeData
    {
        static string Iso(DateTime local) { return local.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.ffffff+00:00", CultureInfo.InvariantCulture); }
        static Dictionary<string, object> E(string id, string state, string name, DateTime changed, params object[] attr)
        {
            var a = new Dictionary<string, object> { { "friendly_name", name } };
            for (int i = 0; i + 1 < attr.Length; i += 2) a[(string)attr[i]] = attr[i + 1];
            return new Dictionary<string, object> { { "entity_id", id }, { "state", state }, { "attributes", a }, { "last_changed", Iso(changed) }, { "last_updated", Iso(changed) } };
        }
        // the states as Home Assistant's /api/states would send them
        public static List<Dictionary<string, object>> States(DateTime now)
        {
            var f = "granary_smart_camera_feeder"; var F = "Granary Smart Camera Feeder";
            var w = "dockstream_2_smart_fountain"; var W = "Dockstream 2 Smart Fountain";
            var next = now.Date.AddHours(now.Hour < 7 ? 7 : now.Hour < 12 ? 12 : now.Hour < 18 ? 18 : 31);
            var last = now.Date.AddHours(now.Hour >= 18 ? 18 : now.Hour >= 12 ? 12 : 7).AddSeconds(12); if (last > now) last = last.AddDays(-1);
            int meals = now.Hour >= 18 ? 3 : now.Hour >= 12 ? 2 : now.Hour >= 7 ? 1 : 0;
            var h1 = now.AddHours(-1);
            return new List<Dictionary<string, object>> {
                E("sensor." + f + "_next_feeding_time", Iso(next), F + " Next feeding", h1, "device_class", "timestamp", "grams", 10),
                E("sensor." + f + "_last_feed_time", Iso(last), F + " Last feed time", last, "device_class", "timestamp"),
                E("sensor." + f + "_today_feeding_quantity", (meals * 10).ToString(), F + " Today feeding quantity", last, "unit_of_measurement", "g", "state_class", "total_increasing"),
                E("sensor." + f + "_today_feeding_times", meals.ToString(), F + " Today feeding times", last),
                E("binary_sensor." + f + "_food_low", "off", F + " Food low", now.AddDays(-2), "device_class", "problem"),
                E("binary_sensor." + f + "_dispenser_problem", "off", F + " Dispenser problem", now.AddDays(-5), "device_class", "problem"),
                E("sensor." + f + "_remaining_desiccant_days", "18", F + " Remaining desiccant days", now.AddDays(-1), "unit_of_measurement", "days"),
                E("sensor." + f + "_battery_level", "100", F + " Battery level", now.AddDays(-1), "unit_of_measurement", "%", "device_class", "battery"),
                E("sensor." + f + "_wifi_signal", "-52", F + " Wi-Fi signal", h1, "unit_of_measurement", "dBm", "device_class", "signal_strength"),
                E("switch." + f + "_sound", "off", F + " Sound", now.AddDays(-9)),
                E("button." + f + "_manual_feed", "unknown", F + " Manual feed", now.AddDays(-9)),
                E("number." + f + "_manual_feed_quantity", "1", F + " Manual feed quantity", now.AddDays(-9), "min", 1, "max", 12, "step", 1),
                E("camera." + f + "_camera", "idle", F + " Camera", now.AddDays(-1), "access_token", "fake", "entity_picture", "/api/camera_proxy/camera." + f + "_camera?token=fake"),
                E("sensor." + w + "_today_s_water_intake", "3.52", W + " Today's water intake", now.AddMinutes(-20), "unit_of_measurement", "oz", "state_class", "total_increasing"),
                E("sensor." + w + "_water_level", "64", W + " Water level", now.AddMinutes(-20), "unit_of_measurement", "%"),
                E("sensor." + w + "_remaining_filter_days", "9", W + " Remaining filter days", now.AddDays(-1), "unit_of_measurement", "days"),
                E("sensor." + w + "_remaining_cleaning_days", "2", W + " Remaining cleaning days", now.AddDays(-1), "unit_of_measurement", "days"),
                E("sensor." + w + "_pump_status", "running", W + " Pump status", now.AddHours(-3)),
                E("sensor." + w + "_irregularities", "3", W + " Irregularities", now.AddHours(-2)),
                E("camera.front_door", "streaming", "Front Door", now.AddDays(-3), "access_token", "fake"),
                E("camera.living_room", "streaming", "Living Room", now.AddDays(-3), "access_token", "fake"),
                E("camera.backyard", "streaming", "Backyard", now.AddDays(-3), "access_token", "fake"),
                E("camera.garage", "unavailable", "Garage", now.AddMinutes(-12), "restored", true),
                E("binary_sensor.front_door_motion", "off", "Front Door Motion", now.AddMinutes(-35), "device_class", "motion"),
                E("binary_sensor.living_room_motion", "off", "Living Room Motion", now.AddMinutes(-50), "device_class", "motion"),
                E("lock.front_door_lock_bolt", "unavailable", "Front Door Lock Bolt", now.AddDays(-30), "restored", true),
                E("person.sam", "home", "Sam", now.AddHours(-5)),
                E("sun.sun", "below_horizon", "Sun", now.AddHours(-4)),
                E("sensor.nas_cpu_temperature", "41", "NAS CPU temperature", now.AddMinutes(-1), "unit_of_measurement", "°C", "device_class", "temperature") };
        }
        public static List<Entity> Entities(DateTime now) { var l = new List<Entity>(); foreach (var d in States(now)) l.Add(Entity.From(d)); return l; }
        // a week of water intake (oz) for the 7-day average
        public static History Week(DateTime now)
        {
            var h = new History(); var v = new[] { 3.9, 4.1, 3.7, 4.0, 3.6, 4.2, 3.8 };
            for (int i = 1; i <= 7; i++) h.Intake[History.Day(now.AddDays(-i))] = v[i - 1];
            return h;
        }
    }
}
