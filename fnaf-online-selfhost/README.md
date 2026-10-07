# FNAF Online – self-host mod (direct connect)

A small [BepInEx](https://github.com/BepInEx/BepInEx) plugin that replaces the game's **Unity Relay** transport with a
plain direct UDP connection, so one player's PC acts as the server and friends join its IP.
It ships **no game files** – everyone downloads the original build from GameJolt and drops this plugin in.

> **Status: written from a static analysis of the 0.9.6-alpha build, never compiled or run.**
> Expect to fix a compile error or two and to iterate on the first run (see *If it doesn't work*).
> It only supports build **0.9.6-alpha** (Unity 6000.0.24f1). Method names/signatures come from the IL2CPP metadata dump.

## What it changes (and what it doesn't)

| Piece | Behaviour |
|---|---|
| Game traffic (Netcode for GameObjects) | **Direct UDP** to the host (`RelayManager.CreateRelayAsync` / `JoinRelayAsync` replaced) |
| Unity Authentication, Lobby list, Vivox voice | **Unchanged** – still use the original developer's Unity project, so everyone needs internet |
| Lobby list | Host a **Private** lobby so you don't appear in the public list of the original game |
| Voice chat | Channel name = the room code, on the original Vivox account → pick a unique `RoomCode` |
| Dedicated server | **None exists.** The "server" is a player running Host. A headless build isn't possible without the source |

## 1. Install BepInEx (everyone, once)

1. Download **BepInEx 6 IL2CPP, Windows x64, a recent *bleeding edge* build** (Unity 6 needs a recent one) from <https://builds.bepinex.dev/projects/bepinex_be>.
2. Unzip it into the game folder (next to `FNAF Online Multiplayer.exe`).
3. Launch the game once and quit. BepInEx generates `BepInEx/interop/*.dll` (can take a few minutes the first time).

## 2. Build the plugin (one person, once)

Requires the [.NET SDK 6+](https://dotnet.microsoft.com/download).

```
cd SelfHostPlugin
dotnet build -c Release -p:GameDir="C:\path\to\fnaf-online-0.9.6-alpha"
```

Send `bin/Release/net6.0/FnafSelfHost.dll` to your friends. Everyone puts it in `<game folder>/BepInEx/plugins/`.

## 3. Configure (everyone)

Launch once; this creates `BepInEx/config/local.fnafonline.selfhost.cfg`.

| Setting | Host | Friends |
|---|---|---|
| `Port` | UDP port to listen on (default 7777) | same value |
| `RoomCode` | any unique 6 chars, e.g. `K7Q2ZP` | **same** value |
| `ServerAddress` | ignored | the host's public IP / VPN IP / hostname |

## 4. Make the host reachable

Pick one:
- **Port-forward** UDP `7777` on the host's router to the host PC (and allow it in Windows Firewall).
- **Tailscale / ZeroTier** – everyone joins the same virtual network; friends use the host's VPN IP. No port-forwarding needed.
- **playit.gg** (UDP tunnel) – use the tunnel address/port it gives you.

Host on a cloud VPS: the game is a graphical Windows client, so it needs a Windows VM (with a GPU or a software renderer); it will not run headless.

## 5. Play

- Host: normal flow – create a lobby (choose **Private**). The plugin logs `Hosting directly on UDP 7777` in `BepInEx/LogOutput.log`.
- Friends: choose join-by-code and enter the same `RoomCode`. The plugin ignores the code's value for routing and connects to `ServerAddress:Port`.
  (If the code box accepts it, you can type `ip:port` instead.)

## If it doesn't work

Send me `BepInEx/LogOutput.log`. Likely first-run issues:
- **Compile errors** – interop names differ slightly (private fields like `RelayManager.joinCode`, `ConnectionPayload` boxing). Easy to adjust.
- **Code box rejects `RoomCode`** – the game's `MultiplayerManager.IsCodeValid()` may check length/charset; I'd patch it.
- **"Version mismatch" / kicked on join** – the connection payload (`version`, `playerName`, `String2`=auth id) must match what the host's `JoinRoomApprovalAsync` expects; I'd compare against it.
- **Game updates** – a new GameJolt version changes the metadata; the patch targets must be re-checked.

## Legal / etiquette

This is an unofficial mod for a fan game. Don't redistribute the game itself or the developer's files; share only this plugin.
Ask the developer (Brian_mpz, <https://gamejolt.com/games/fnaf-online/963459>) if you plan to run anything public.
