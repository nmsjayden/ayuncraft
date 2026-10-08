FNAF ONLINE - SELF-HOST MOD  (for game version 0.9.6-alpha)
============================================================

INSTALL (everyone, once)
  1. Unzip this into the game folder - the one that contains "FNAF Online Multiplayer.exe".
  2. Double-click  Install.bat  (needs internet; it fetches BepInEx, the mod loader).
  3. Start the game like normal.
     The FIRST start shows a black console window for 1-3 minutes while the mod gets ready. Wait for the game.
     If Windows Firewall asks, click "Allow".

HOW TO TELL IT'S WORKING
  - When you start the game a BLACK CONSOLE WINDOW opens first. If you never see it, the mod is not
    running (the game is the plain original) - the install didn't go into the right folder, or antivirus
    removed winhttp.dll. Run Install.bat again from inside the game folder and read what it prints.
  - After the game starts, the file  BepInEx\LogOutput.log  should contain the line
    "Self-host mod loaded". If that file doesn't exist, the mod never started.

HOST  (no setup)
  - Go to the online menu and create a lobby like normal.
  - The mod works out the best way for friends to reach you and puts the room code on your clipboard:
      * If your router lets the mod open the port automatically, the code looks like  6B01R-GJ7K1  (direct, fastest).
      * Otherwise (carrier/shared internet, no router access, strict firewall) it switches to the built-in RELAY
        and the code looks like  RELAY-K3F9A-7QZ2M.  Nothing to set up, nothing for your friends to install.
  - Send the code to your friends.

JOIN
  - Paste the room code into the room code box and join. You can also type an IP address
    (for example 203.0.113.9) or  ip:port  instead.

HOW THE RELAY WORKS
  Everyone's PC connects OUT to a free public message server (the same kind chat apps use) and the game's packets
  travel through it, end-to-end encrypted with a key that only exists inside your room code - the server can't read
  or change them. Only outgoing connections are used, so it works behind carrier NAT. It adds some delay (usually
  50-200 ms) and depends on those free servers being up. The mod tries several at once.

NOTES
  - Everybody needs the same game version and this mod.
  - Voice chat does not work (it needs the developer's servers). Use Discord.
  - Want the lowest delay? Use a direct connection: forward UDP 7777 on your router, or use Tailscale/ZeroTier/playit.gg
    and set  PublicAddress  in BepInEx\config\local.fnafonline.selfhost.cfg  (then ConnectionMode can stay Auto).
  - If the relay can't connect, your network may block it. Ask your friend to try, or use one of the direct options above.
  - Problems? Send the file  BepInEx\LogOutput.log

This mod contains none of the game's files. BepInEx is LGPL-2.1: https://github.com/BepInEx/BepInEx
