HALLOW HOME - your smart-home and pet devices through Home Assistant

One private app for a Petlibro feeder and fountain, Wyze cameras and an honest Wyze Lock Bolt card,
with smarter alerts. Home Assistant runs on your NAS and is NOT included - see NAS-SETUP.md.

WHAT'S NEW IN 1.2
- Setup page: a guided setup for Home Assistant (test the address, the exact token clicks, test again),
  the cameras (a ready-to-paste docker-wyze-bridge snippet for Synology Container Manager - your Wyze
  password and keys go in on the NAS only) and Petlibro (HACS). Each step can be skipped and shows what's
  still missing.
- Demo mode: explore with made-up devices before the NAS is ready. Nothing is sent anywhere.
- Cameras: 2x2 / 3x3 grid, full screen, snapshot, sound muted until you unmute it; WebRTC / HLS from the
  bridge or MJPEG from Home Assistant; pauses when the window is hidden or minimised; reconnects by itself.
- The feeder's own schedule: see it, and change it (with a confirm) when the Petlibro integration allows it.
  "Fed today" now shows what's planned too.
- Fountain filter / cleaning reminders (the fountain's own counters, or your own interval).

WHAT'S NEW IN 1.1.1
- Percentages read 45% instead of 45 %.

WHAT'S NEW IN 1.1
- Hallow Remote and Hub can now offer the right Feed now choice: the status other apps read includes the
  feeder's portion range (1 to the most the feeder and your Settings allow) and grams per portion.
- Feed now never asks the feeder for more portions than its own setting accepts.

WHAT'S NEW IN 1.0
- The first Hallow Home: Dashboard, Pets (Feed now with a confirm), Cameras (live grid, snapshot,
  docker-wyze-bridge live view, recordings folder), Lock (Bluetooth-only, explained), Alerts,
  Automations, Devices (which entity is what, with overrides), Settings.

HOW TO INSTALL
1. Right-click the zip > Properties > tick "Unblock" > OK
2. Right-click the zip > Extract All
3. Double-click INSTALL.bat (no admin needed)

FIRST TIME
1. Set up Home Assistant on your NAS (NAS-SETUP.md): Docker, docker-wyze-bridge, HACS, the community
   Wyze and Petlibro integrations (you sign in to those yourself, in Home Assistant).
2. In Home Assistant: your profile > Security > Long-lived access tokens > Create token.
3. In Hallow Home: the Setup page (or Settings) > paste the address (e.g. http://nas:8123) and the token.
   The token is kept encrypted for your Windows user only and never logged.
4. Devices page: check what Hallow Home matched; change anything it guessed wrong.
5. Alerts page: switch on Discord and/or ntfy if you want alerts on your phone.

HONEST NOTES
- The Petlibro and Wyze integrations are community projects (unofficial). If the vendors change their
  cloud, they can break for a while; Hallow Home can only show what they expose.
- The Wyze Lock Bolt is Bluetooth-only: Home Assistant and Wyze's cloud can't reach it. Use the Wyze
  app at the door.
- Alerts and automations only run while Hallow Home runs (it can start with Windows, in the tray).
- The water-intake hint is not medical advice.

WHILE YOU PLAY
Nothing pops up on the PC while Delta Force, Discovery or a game Hallow Hub sees is running, and the
camera streams pause. Your phone still gets alerts (Discord / ntfy) unless you choose to hold everything.

UNINSTALL
Windows Settings > Apps > Hallow Home > Uninstall (or Hallow Setup). Home Assistant isn't touched.

Icons: Font Awesome Free (CC BY 4.0). WebView2 SDK: Microsoft (WebView2-LICENSE.txt).
