# Hallow Home roadmap

## Done

| Version | What |
|---|---|
| 1.0 | Home Assistant as the backend (REST: /api/, /api/states, /api/services, camera proxy; WebSocket: auth + subscribe_events state_changed, reconnect with back-off, a full refresh every 10 min or every minute while live updates are down); long-lived token in DPAPI (secrets.dat), plain-http-over-the-internet warning. Mapping layer: roles for the Petlibro feeder (next / last feeding, today's grams and meals, food level, dispenser problem, Feed now button and portions, desiccant, battery, Wi-Fi, sound, camera), the fountain (water level, intake, filter, cleaning, pump, irregularities) and the lock, recognised by domain, device class and name patterns within the device's own entities; per-role override ("this entity", "none", automatic) on the Devices page. Dashboard, Pets (Feed now with portion picker and confirm; intake vs 7-day average chart; irregularities in plain words), Cameras (live MJPEG grid from HA's camera proxy while the page is open and no game runs, single view, snapshot, docker-wyze-bridge WebRTC / HLS live view in WebView2, recordings folder listing), Lock (honest Bluetooth-only card, ILockPath extension point for a future HA lock entity). Smart alerts: jam, food low, water low, pump stopped, filter / cleaning due, low intake vs 7-day average (not medical advice), camera offline, motion while away; de-duplication with a 10-minute re-fire guard, quiet hours, pause 1 h, quiet during games (PC quiet, phone still gets it, or hold all); Hub notify / tray, Discord webhook, ntfy (+ token), secrets encrypted. Simple automations (alert / away / home / state -> reminder or switch / light / camera on-off; never unlock or feed). Family link (ping, status, feed <n> confirm, away, open, menu, invoke, quit, --cmd, status.json), Hub tray hosting, light / dark, accent, crash reports, updater. NAS-SETUP.md (HA + docker-wyze-bridge compose, HACS, Wyze and Petlibro integrations, token, Cloudflare warning) |
| 1.1 | Feed now range in status.json / `status` (asked for by Hallow Remote 1.18 and Hub 2.17): `minPortions`, `maxPortions`, `gramsPerPortion`, `portionSetting` per feeder. The range is Settings' limit narrowed by the feeder's own portion entity (number min / max, select options); the dialog and `feed <n> confirm` use the same range and refuse anything outside it before any service call. Tests: the range rules, a live max change on the fake Home Assistant, a 5-minute kill timeout on the suite. |
| 1.1.1 | **Percentages read 45% instead of 45 %** (family-wide text fix): food / water / battery levels, filter life, the intake alert, the water-low hint and the Settings thresholds line. status.json keeps numbers. |

## Later

| Idea | Why it isn't in 1.0 |
|---|---|
| Phone view through Hallow Remote | Remote serves the phone page; Hallow Home's pipe and status.json are ready (counts and flags, `feed <n> confirm`, and since **1.1** the feeder's portion range). Remote isn't changed here. |
| "Away" from Hallow Places | Places knows who's home. 1.0 uses HA's person entities or the "I'm away" switch; Places can send `away on|off` over the pipe later. |
| Cloudflare Access service tokens | Sending CF-Access-Client-Id / Secret headers would let Hallow Home reach HA behind Access from outside. 1.0: LAN or Tailscale address. |
| Lock Bolt control | Bluetooth-only, nothing verified in HA. If a community integration appears (e.g. via an ESPHome Bluetooth proxy), the Lock page already shows a mapped `lock.*` entity. |
| Automations that run with the PC off | They'd have to be written into Home Assistant (its automation API); 1.0 keeps its few rules in the app. |
| Recording / clips in the app | docker-wyze-bridge or HA record on the NAS; 1.0 lists the folder. Hallow Clips-style trimming could come later. |
| Camera event timeline | Needs HA's history API per motion sensor; 1.0 shows the state now. |
