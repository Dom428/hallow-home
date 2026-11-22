// Hallow Home - the Wyze Lock Bolt, honestly.
// The Lock Bolt is Bluetooth-only: no Wi-Fi, no hub, no Wyze cloud control. Wyze's API and the community Wyze integration for Home
// Assistant (which talks to Wyze's cloud) can't reach it, and there is no maintained Home Assistant integration for it that we could
// verify (late 2026: one forum report of someone controlling one through their own Bluetooth setup, nothing published or reviewed).
// So in 1.0 the Lock page says "Bluetooth-only - use the Wyze app nearby" and explains why.
//
// The extension point: ILockPath. Today there is one path, "Home Assistant lock entity": IF a lock.* entity for it ever shows up in
// Home Assistant - for example through an ESPHome Bluetooth proxy near the door plus a community integration that speaks the Bolt's
// Bluetooth protocol - the Devices page can map it to "Door lock" and this page shows its state with Lock / Unlock (unlock always
// asks first, and is never offered over the family pipe, to automations or to the phone). No promise that such an integration
// will exist; nothing in Hallow Home talks Bluetooth itself.
using System;
using System.Collections.Generic;

namespace HallowHome
{
    public class LockView
    {
        public string Kind;      // "ble-only" | "ha" | "ha-unavailable"
        public string Title, Text, State; public Entity Entity; public bool CanControl;
        public string Short { get { return Kind == "ble-only" ? "bluetooth-only" : Kind == "ha-unavailable" ? "unavailable" : State; } }
    }

    public interface ILockPath { string Name { get; } LockView View(Mapping m); }

    // the only path in 1.0: a Home Assistant lock entity, if one is mapped to "Door lock"
    public class HaLockPath : ILockPath
    {
        public string Name { get { return "Home Assistant lock entity"; } }
        public LockView View(Mapping m)
        {
            var e = m.Get("lock.bolt");
            if (e == null) return null;
            if (!e.Available) return new LockView { Kind = "ha-unavailable", Entity = e, Title = "Bluetooth-only — use the Wyze app nearby",
                Text = "Home Assistant has an entity for a lock (" + e.Label + ") but it's unavailable. A Lock Bolt can only be reached over Bluetooth from a few metres away, so a cloud integration can't control it. Use the Wyze app on your phone at the door." };
            return new LockView { Kind = "ha", Entity = e, State = e.State, CanControl = e.State == "locked" || e.State == "unlocked", Title = e.State == "locked" ? "Locked" : e.State == "unlocked" ? "Unlocked" : Read.Nice(e.State),
                Text = "Reported by Home Assistant (" + e.Label + "). Unlocking always asks first and is never offered over the family link or to automations." };
        }
    }

    public static class LockInfo
    {
        public static readonly List<ILockPath> Paths = new List<ILockPath> { new HaLockPath() };
        public const string BleTitle = "Bluetooth-only — use the Wyze app nearby";
        public const string BleText = "The Wyze Lock Bolt has no Wi-Fi and no hub: it only talks Bluetooth to a phone a few metres away. Wyze's cloud, Wyze's API and the Wyze integration for Home Assistant can't reach it, so Hallow Home can't show whether it's locked or lock it from here. Lock and unlock it with the Wyze app (or the fingerprint pad) at the door.";
        public const string Later = "Maybe later: if someone publishes a Home Assistant integration for the Bolt's Bluetooth protocol, an ESPHome Bluetooth proxy near the door could bring it into Home Assistant. Map that lock entity to \"Door lock\" on the Devices page and it shows up here. Nothing like that is verified today, so don't count on it.";
        public static LockView View(Mapping m)
        {
            foreach (var p in Paths) { var v = p.View(m); if (v != null) return v; }
            return new LockView { Kind = "ble-only", Title = BleTitle, Text = BleText };
        }
    }
}
