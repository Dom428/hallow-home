HALLOW HOME - your smart-home and pet devices through Home Assistant

One private app for a Petlibro feeder and fountain, Wyze cameras and an honest Wyze Lock Bolt card,
with smarter alerts. Home Assistant runs on your NAS and is NOT included - see NAS-SETUP.md.

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
3. In Hallow Home: Settings > paste the address (e.g. http://nas:8123) and the token > Save.
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
