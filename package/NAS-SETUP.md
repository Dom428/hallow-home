# Setting up Home Assistant on the NAS (for Hallow Home)

Hallow Home is only the PC app (its **Setup** page walks through this page step by step and makes the docker-wyze-bridge snippet for you). Your devices live in **Home Assistant (HA)**, which runs in Docker on your NAS. Hallow Home does
**not** install or change anything on the NAS. This page shows what to set up yourself, once.

What you end up with:

| Part | What it does | Where |
| --- | --- | --- |
| Home Assistant | the hub: devices, history, the API Hallow Home talks to | NAS, port **8123** |
| docker-wyze-bridge | turns each Wyze camera into RTSP / WebRTC / HLS streams (and can record) | NAS, ports 8554 / 8888 / 8889 / 5000 |
| HACS | the community store inside HA | inside HA |
| Wyze integration (community, HACS) | other Wyze devices (plugs, bulbs, camera power switches, motion) | inside HA |
| Petlibro integration (community, HACS, unofficial) | the Granary feeder and the Dockstream fountain | inside HA |

> **Honest notes.** The Petlibro and Wyze integrations are community projects, not made or supported by Petlibro or Wyze. They
> use the vendors' cloud APIs, which can change and break them for a while. Hallow Home recognises their entities by pattern and lets
> you fix any guess on its Devices page, but it can only show what the integrations expose.
> **The Wyze Lock Bolt cannot be added**: it's Bluetooth-only (no Wi-Fi, no hub, no cloud control). See the end of this page.

---

## 1. Folders

On the NAS (Synology paths shown; adjust for yours), make:

```
/volume1/docker/home-assistant/
    docker-compose.yml
    .env                      <- secrets, readable by you only (chmod 600)
    config/                   <- Home Assistant's own files
    wyze-bridge/record/       <- camera recordings (optional)
```

## 2. docker-compose.yml

```yaml
services:
  homeassistant:
    container_name: homeassistant
    image: ghcr.io/home-assistant/home-assistant:stable
    network_mode: host            # HA on port 8123; host networking lets it find devices on your LAN
    restart: unless-stopped
    environment:
      - TZ=${TZ}
    volumes:
      - /volume1/docker/home-assistant/config:/config
      - /etc/localtime:/etc/localtime:ro

  wyze-bridge:
    container_name: wyze-bridge
    image: mrlt8/wyze-bridge:latest   # check the project's README for the current image name - forks carry it on
    restart: unless-stopped
    ports:                            # LAN only - never forward these on your router or through a tunnel
      - 8554:8554                     # RTSP  (Home Assistant reads the cameras here)
      - 8888:8888                     # HLS   (Hallow Home's live view, optional)
      - 8889:8889                     # WebRTC (Hallow Home's live view, optional)
      - 8189:8189/udp                 # WebRTC media
      - 5000:5000                     # the bridge's own web page
    environment:
      - WYZE_EMAIL=${WYZE_EMAIL}
      - WYZE_PASSWORD=${WYZE_PASSWORD}
      - API_ID=${WYZE_API_ID}
      - API_KEY=${WYZE_API_KEY}
      - WB_AUTH=True                  # the bridge's web page and streams need a login (see its README)
      - RECORD_ALL=False              # True to record every camera to /record (uses disk!)
    volumes:
      - /volume1/docker/home-assistant/wyze-bridge/record:/record
```

`.env` next to it (never commit or share this file):

```
TZ=America/New_York
WYZE_EMAIL=you@example.com
WYZE_PASSWORD=your-wyze-password
WYZE_API_ID=...from the Wyze developer console...
WYZE_API_KEY=...from the Wyze developer console...
```

The **Wyze API ID and key** come from Wyze's developer console (sign in at developer-api-console.wyze.com, "Create an API key").
Wyze requires them for third-party tools since 2024. Start it with `docker compose up -d` in that folder.

## 3. Home Assistant's first start

1. Open `http://<nas>:8123`, create the owner account (a strong password; turn on multi-factor authentication in your profile).
2. Set your home's location and time zone.

## 4. HACS (the community store)

1. `docker exec -it homeassistant bash -c "wget -O - https://get.hacs.xyz | bash -"`
2. Restart Home Assistant (Settings > System > Restart).
3. Settings > Devices & services > Add integration > **HACS**, and sign in with GitHub when it asks (a device code).

## 5. Wyze

- **Cameras** come from docker-wyze-bridge. For each camera add HA's built-in **Generic Camera** integration
  (Settings > Devices & services > Add integration > Generic Camera) with the stream `rtsp://<nas>:8554/<camera-name>`
  (the name is the camera's name in the Wyze app, lower case with dashes, e.g. `front-door`; the bridge's page on port 5000 lists them).
  Name each camera like in the Wyze app - Hallow Home uses that name for the live view.
- **Other Wyze devices**: HACS > search **Wyze** (the community "Wyze API" integration) > Download > restart > Add integration
  "Wyze" with your Wyze email, password, and the same API ID / key. It adds plugs, bulbs, camera power switches and motion
  sensors - useful for "cameras on when everyone's away" automations.

## 6. Petlibro (feeder and fountain)

HACS > the three dots > **Custom repositories** > `https://github.com/jjjonesjr33/petlibro`, type Integration > Add; then
search **PETLIBRO** > Download (the newest version also has feeding-schedule services) > restart > Add integration **PETLIBRO**
and sign in with your Petlibro account
**yourself** (Hallow Home never sees it). Read the integration's README first: Petlibro may allow only one signed-in session per
account, so some people make a second Petlibro account, share the devices to it from the main app, and use that one in HA.

You should then see entities like the feeder's next feeding, last feed time, today's feeding quantity, food low, a manual feed
button and portions, desiccant days; and the fountain's water level, today's water intake, filter and cleaning days, pump status.
Names differ between versions - that's fine, Hallow Home's Devices page shows what it matched and lets you change it.

## 7. A token for Hallow Home

In Home Assistant: click your name (bottom left) > **Security** > **Long-lived access tokens** > **Create token**, name it
"Hallow Home", and copy it (it's shown once). In Hallow Home: the **Setup** page (step 1) > paste `http://<nas>:8123`, Test connection, paste the token, Test again.
The token is stored encrypted for your Windows user (DPAPI) and never logged. Revoke it in HA any time.

## 8. Optional: reaching it from outside (home.hallowservices.com)

You don't need this at home - use the LAN address. If you want Home Assistant from outside:

- **Easiest and safest: don't publish it at all.** Use Tailscale (Hallow VPN's Home mode) and keep using `http://<nas>:8123`.
- **Cloudflare Tunnel** (`cloudflared`, no open router ports): point `home.hallowservices.com` at `http://localhost:8123` and add to
  HA's `configuration.yaml`:
  ```yaml
  http:
    use_x_forwarded_for: true
    trusted_proxies:
      - 127.0.0.1
      - 172.16.0.0/12
  ```

> ### Warning - read before publishing anything
> - **Put Cloudflare Access (or at the very least Home Assistant's own login with multi-factor authentication) in front.** Anyone who
>   gets into Home Assistant can see your cameras and control your devices.
> - **Never expose the cameras unauthenticated.** Do not publish docker-wyze-bridge's ports (RTSP 8554, HLS 8888, WebRTC 8889,
>   web 5000) through the tunnel or your router. The cameras should only ever be reachable through Home Assistant (which needs a
>   login) or on your LAN / Tailscale.
> - Keep HA updated; use a long random password; don't reuse it.
> - Hallow Home 1.0 sends only HA's token (`Authorization: Bearer`). With Cloudflare Access in front, use the LAN or Tailscale
>   address in Hallow Home - 1.0 doesn't send Cloudflare Access service-token headers (on the roadmap). Hallow Home warns if you enter
>   a plain-`http` address on the internet.

## The Wyze Lock Bolt

It is **Bluetooth-only**: no Wi-Fi, no hub, no Wyze cloud control. Neither Wyze's API nor the Wyze integration can reach it, and no
maintained Home Assistant integration for it could be verified (late 2026; there's a forum report of someone controlling one with
their own Bluetooth setup, nothing published). Hallow Home shows it as "Bluetooth-only - use the Wyze app nearby".
If a community integration for its Bluetooth protocol ever appears, an **ESPHome Bluetooth proxy** (a small ESP32 near the door)
could bring it into HA as a `lock.*` entity, and Hallow Home would show it (map it to "Door lock" on the Devices page). No promises.

## Recordings

Hallow Home doesn't record. With `RECORD_ALL=True` docker-wyze-bridge records to `/volume1/docker/home-assistant/wyze-bridge/record`.
Share that folder read-only on your LAN (e.g. `\\nas\docker\home-assistant\wyze-bridge\record`) and set it as Hallow Home's
"Recordings folder" - it lists the newest files and opens them in your video player. Watch the disk space.
