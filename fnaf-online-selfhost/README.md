# FNAF Online – self-host mod (direct connect)

A small [BepInEx](https://github.com/BepInEx/BepInEx) plugin that replaces the game's **Unity Relay** transport with a
plain direct UDP connection, so one player's PC acts as the server and friends join its IP.
It ships **no game files** – everyone downloads the original build from GameJolt and drops this plugin in.

> **Status: written from a static analysis of the 0.9.6-alpha build, never compiled or run.** Offline mode in particular stubs about 20 methods I could only read signatures and call lists for.
> Expect to fix a compile error or two and to iterate on the first run (see *If it doesn't work*).
> It only supports build **0.9.6-alpha** (Unity 6000.0.24f1). Method names/signatures come from the IL2CPP metadata dump.

## What it changes (and what it doesn't)

| Piece | Behaviour |
|---|---|
| Game traffic (Netcode for GameObjects) | **Direct UDP** to the host (`RelayManager.CreateRelayAsync` / `JoinRelayAsync` replaced) |
| Unity login, Cloud Code, Lobby list, Vivox voice | **Switched off** by default (`OfflineServices = true`). This is what removes the *"no online connection"* screen. Nobody needs the developer's Unity services or even internet access to Unity |
| In-game voice chat | **Does not work** in offline mode (it is Vivox). Use Discord for voice |
| Lobby browser | Doesn't list your game; friends join by code (see below) |
| Developer / moderator checks | Always "no" (they were Cloud Code calls) |
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
| `RoomCode` | any 6 chars, e.g. `K7Q2ZP` | **same** value |
| `OfflineServices` | `true` (default) | `true` (default) |
| `ServerAddress` | ignored | the host's public IP / VPN IP / hostname |

## 4. Make the host reachable

Pick one:
- **Port-forward** UDP `7777` on the host's router to the host PC (and allow it in Windows Firewall).
- **Tailscale / ZeroTier** – everyone joins the same virtual network; friends use the host's VPN IP. No port-forwarding needed.
- **playit.gg** (UDP tunnel) – use the tunnel address/port it gives you.

Host on a cloud VPS: the game is a graphical Windows client, so it needs a Windows VM (with a GPU or a software renderer); it will not run headless.

## 5. Play

- Host: normal flow – create a lobby. The plugin logs `Hosting directly on UDP 7777` in `BepInEx/LogOutput.log`.
- Friends: choose join-by-code and enter the same `RoomCode`. The plugin ignores the code's value for routing and connects to `ServerAddress:Port`.
  (If the code box accepts it, you can type `ip:port` instead.)

## If it doesn't work

Send me `BepInEx/LogOutput.log`. Likely first-run issues:
- **Still stuck on the "no online connection" screen** – look for `Offline mode: reported Unity services as ready.` in the log. If missing, the `ServicesInitialiser.Start` patch didn't apply or the menu subscribed after the event fired (I'd change the 0.75s delay).
- **Host creates a lobby but nothing happens / UI hangs** – `LobbyManager.CreateLobby` was replaced by a version that only calls `HostOnlineRoomAsync`; the original also set some state I can't see. The log will show the exception.
- **Joining fails with the voice or ID fields** – the client sends a voice-chat id and auth id to the host; offline these are empty or generated and the game may not tolerate it.
- **Compile errors** – interop names differ slightly (private fields like `RelayManager.joinCode`, `ConnectionPayload` boxing). Easy to adjust.
- **Code box rejects `RoomCode`** – the game's `MultiplayerManager.IsCodeValid()` may check length/charset; I'd patch it.
- **"Version mismatch" / kicked on join** – the connection payload (`version`, `playerName`, `String2`=auth id) must match what the host's `JoinRoomApprovalAsync` expects; I'd compare against it.
- **Game updates** – a new GameJolt version changes the metadata; the patch targets must be re-checked.

## Legal / etiquette

This is an unofficial mod for a fan game. Don't redistribute the game itself or the developer's files; share only this plugin.
Ask the developer (Brian_mpz, <https://gamejolt.com/games/fnaf-online/963459>) if you plan to run anything public.
