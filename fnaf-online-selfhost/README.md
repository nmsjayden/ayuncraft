# FNAF Online – self-host mod

Lets you host FNAF Online Multiplayer **from your own PC with no setup**, hand friends a normal room code (or just your IP),
and keeps working while the developer's servers are down. Built for game version **0.9.6-alpha** (Unity 6000.0.24f1).

The ready-to-use pack for players is a single zip (`FNAF-Online-SelfHost-Pack.zip`: BepInEx + this plugin). Players copy it
into the game folder and run the game; the steps are in the pack's `README-FIRST.txt`. This repo folder is the source.

> **Status:** the plugin compiles against the game's real generated interop assemblies and every patch target was checked to exist
> with matching parameter names. It has **not been run inside the game** (no Windows/GPU where it was written), so expect
> to send back `BepInEx/LogOutput.log` after a first try.

## What it does

| Piece | Behaviour |
|---|---|
| Game traffic | Direct UDP to the host (replaces `RelayManager.CreateRelayAsync` / `JoinRelayAsync`) |
| Hosting | Click host as normal. The mod finds your public IP, opens UDP 7777 with **UPnP**, and copies your **room code** to the clipboard |
| Room code | Your address packed into 10 characters, e.g. `6B01R-GJ7K1` (`AddressCode.cs`) |
| Joining | Room-code box accepts that code, or an IP / `ip:port` / host name (the 6-character limit is lifted) |
| Online services | `OfflineServices = true` (default) skips Unity login, Cloud Code, Lobby and Vivox so the game works while the developer's servers are down. No voice chat, no public lobby list |

## Files

- `SelfHostPlugin/Plugin.cs` – config, host/join/UI patches, offline-services patches
- `SelfHostPlugin/AddressCode.cs` – room code ⇄ IP:port
- `SelfHostPlugin/HostNetwork.cs` – public-IP lookup and UPnP port mapping
- `tools/InteropGen` + `tools/build-pack.md` – regenerate interop and rebuild the pack for a new game version

## Config (`BepInEx/config/local.fnafonline.selfhost.cfg`)

| Key | Default | Meaning |
|---|---|---|
| `General.Port` | 7777 | UDP port (same for everyone) |
| `General.OfflineServices` | true | Skip Unity online services |
| `Host.PublicAddress` | empty | Only for VPN/tunnel hosting (Tailscale IP, `name.joinmc.link:12345`) |
| `Host.TryUpnp` | true | Auto-open the router port |

## If it doesn't work

Send `BepInEx/LogOutput.log`. Things most likely to need a tweak:
- Stuck on the online-connection screen → look for `Offline mode: reported Unity services as ready.`
- Join says the code is invalid → the room-code box patch or the stand-in code (`DIRECT`) didn't take.
- Friends time out connecting → UPnP failed or the host is behind CGNAT; the log says which.
- Host creates a lobby and nothing happens → `LobbyManager.CreateLobby` replacement (the original also did Unity Lobby work).

## Legal / etiquette

Unofficial mod for a fan game. It contains none of the game's files; players need the original from
<https://gamejolt.com/games/fnaf-online/963459>. Ask the developer (Brian_mpz) before running anything public.
