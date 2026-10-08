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
  - The mod finds your public IP, opens the port on your router (UPnP), and copies your room code to the
    clipboard. The code looks like  6B01R-GJ7K1  and also shows in the lobby.
  - Send that code to your friends.

JOIN
  - Paste the room code into the room code box and join. You can also type an IP address
    (for example 203.0.113.9) or  ip:port  instead.

NOTES
  - Everybody needs the same game version and this mod.
  - Voice chat does not work (it needs the developer's servers). Use Discord.
  - If your router doesn't support UPnP, or your internet provider shares your address (CGNAT), friends can't
    reach you directly. Fix: use Tailscale/ZeroTier/playit.gg, then in
    BepInEx\config\local.fnafonline.selfhost.cfg  set  PublicAddress = <that address>
  - Problems? Send the file  BepInEx\LogOutput.log

This mod contains none of the game's files. BepInEx is LGPL-2.1: https://github.com/BepInEx/BepInEx
