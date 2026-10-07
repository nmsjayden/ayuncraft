using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using Il2CppSystem.Threading.Tasks;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Services.Authentication;
using UnityEngine;

namespace FnafSelfHost
{
    /// <summary>
    /// Replaces the game's Unity Relay transport with a plain direct UDP connection.
    /// One player runs "Host" as normal; everyone else joins that player's IP:port.
    /// Only RelayManager.CreateRelayAsync / JoinRelayAsync are replaced; everything else is untouched.
    /// </summary>
    [BepInPlugin("local.fnafonline.selfhost", "FNAF Online Self-Host", "0.1.0")]
    public class Plugin : BasePlugin
    {
        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<string> ServerAddress;
        internal static ConfigEntry<ushort> Port;
        internal static ConfigEntry<string> RoomCode;
        internal static ConfigEntry<bool> OfflineServices;

        public override void Load()
        {
            Log = base.Log;
            Enabled = Config.Bind("General", "Enabled", true,
                "Use direct connection instead of Unity Relay.");
            ServerAddress = Config.Bind("Client", "ServerAddress", "127.0.0.1",
                "IP address or hostname of the person hosting. Friends set this to the host's address. The host can leave it.");
            Port = Config.Bind("General", "Port", (ushort)7777,
                "UDP port. The host must allow inbound UDP on this port (port-forward, or use a VPN like Tailscale/ZeroTier).");
            RoomCode = Config.Bind("General", "RoomCode", "CHANGE",
                "6-character code shown as the lobby code. It is also used as the voice-chat channel name on the original game's " +
                "Vivox account, so pick something unique and make it the same for everyone in your group.");

            OfflineServices = Config.Bind("General", "OfflineServices", true,
                "Skip every Unity online service (login, cloud code, lobby list, voice chat). Fixes the 'no online connection' " +
                "screen. Voice chat will NOT work in this mode. Set false to keep the original online services.");

            ClassInjector.RegisterTypeInIl2Cpp<ServicesKicker>();
            var harmony = new Harmony("local.fnafonline.selfhost");
            harmony.PatchAll(typeof(Patches));
            if (OfflineServices.Value) harmony.PatchAll(typeof(OfflinePatches));
            Log.LogInfo($"Self-host patches applied (enabled={Enabled.Value}, port={Port.Value}).");
        }
    }

    internal static class Patches
    {
        // Host: original = allocate a Relay, SetRelayServerData, NetworkManager.StartHost.
        [HarmonyPatch(typeof(RelayManager), nameof(RelayManager.CreateRelayAsync))]
        [HarmonyPrefix]
        private static bool CreateRelayAsync_Prefix(RelayManager __instance, ref Task __result)
        {
            if (!Plugin.Enabled.Value) return true;
            try
            {
                var nm = NetworkManager.Singleton;
                var utp = nm.GetComponent<UnityTransport>();
                // Listen on all interfaces; clients connect to the host's public/VPN IP.
                utp.SetConnectionData("127.0.0.1", Plugin.Port.Value, "0.0.0.0");

                __instance.joinCode = Plugin.RoomCode.Value.ToUpperInvariant();
                Plugin.Log.LogInfo($"Hosting directly on UDP {Plugin.Port.Value}, room code {__instance.joinCode}");

                if (!nm.StartHost())
                    Plugin.Log.LogError("NetworkManager.StartHost() returned false");
                __result = Task.CompletedTask;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"CreateRelayAsync replacement failed: {e}");
                return true; // fall back to the original
            }
            return false;
        }

        // Client: original = join the Relay allocation, SetRelayServerData, build connection payload, StartClient.
        [HarmonyPatch(typeof(RelayManager), nameof(RelayManager.JoinRelayAsync))]
        [HarmonyPrefix]
        private static bool JoinRelayAsync_Prefix(RelayManager __instance, string codeToJoin, ref Task __result)
        {
            if (!Plugin.Enabled.Value) return true;
            try
            {
                var nm = NetworkManager.Singleton;
                var utp = nm.GetComponent<UnityTransport>();

                // Allow typing "ip:port" as the code if the UI accepts it; otherwise use the config file.
                string address = Plugin.ServerAddress.Value;
                ushort port = Plugin.Port.Value;
                if (!string.IsNullOrEmpty(codeToJoin) && codeToJoin.Contains(":"))
                {
                    var parts = codeToJoin.Split(':');
                    address = parts[0];
                    if (parts.Length > 1 && ushort.TryParse(parts[1], out var p)) port = p;
                }
                utp.SetConnectionData(address, port);

                // Same payload the original builds: version + player name + auth player id.
                var payload = new ConnectionPayload
                {
                    version = Application.version,
                    playerName = MultiplayerManager.Instance.playerName,
                    String2 = OfflinePatches.LocalPlayerId(),
                };
                string json = JsonUtility.ToJson(payload.BoxIl2CppObject());
                nm.NetworkConfig.ConnectionData = System.Text.Encoding.UTF8.GetBytes(json);

                Plugin.Log.LogInfo($"Joining directly: {address}:{port}");
                if (!nm.StartClient())
                    Plugin.Log.LogError("NetworkManager.StartClient() returned false");
                __result = Task.CompletedTask;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"JoinRelayAsync replacement failed: {e}");
                return true;
            }
            return false;
        }
    }

    /// <summary>Fires the "services ready" event the main menu is waiting for, a moment after the menu has subscribed to it.</summary>
    public class ServicesKicker : MonoBehaviour
    {
        public ServicesKicker(IntPtr ptr) : base(ptr) { }
        private float _t;
        private void Update()
        {
            _t += Time.unscaledDeltaTime;
            if (_t < 0.75f) return;
            var si = ServicesInitialiser.Instance;
            si.areServicesInitialised = true;
            si.OnConnectionToServicesCompleted?.Invoke();
            Plugin.Log.LogInfo("Offline mode: reported Unity services as ready.");
            Destroy(this);
        }
    }

    /// <summary>
    /// Offline mode. The game normally needs Unity Services (anonymous login, Cloud Code, Lobby, Vivox voice) to
    /// reach the menu and to let anyone join. These patches stub those calls out.
    /// </summary>
    internal static class OfflinePatches
    {
        private static string _id;
        internal static string LocalPlayerId()
        {
            if (_id != null) return _id;
            try
            {
                var online = AuthenticationService.Instance.PlayerId;
                if (!string.IsNullOrEmpty(online)) return _id = online;
            }
            catch { /* not signed in */ }
            // Stable per-install id so a player keeps the same identity between sessions.
            const string key = "FnafSelfHostPlayerId";
            var saved = PlayerPrefs.GetString(key, "");
            if (string.IsNullOrEmpty(saved)) { saved = System.Guid.NewGuid().ToString("N"); PlayerPrefs.SetString(key, saved); PlayerPrefs.Save(); }
            return _id = saved;
        }

        // --- startup: skip UnityServices.InitializeAsync + sign-in + update check -----------------------------------
        [HarmonyPatch(typeof(ServicesInitialiser), "Start")]
        [HarmonyPrefix]
        private static bool ServicesStart(ServicesInitialiser __instance)
        {
            __instance.gameObject.AddComponent<ServicesKicker>();
            return false;
        }

        // --- host asks Cloud Code "is this player a developer / moderator?" for every joiner -----------------------
        [HarmonyPatch(typeof(MultiplayerManager), "IsDeveloper")]
        [HarmonyPrefix]
        private static bool IsDeveloper(ref Task<bool> __result) { __result = Task.FromResult<bool>(false); return false; }

        [HarmonyPatch(typeof(MultiplayerManager), "IsModerator")]
        [HarmonyPrefix]
        private static bool IsModerator(ref Task<bool> __result) { __result = Task.FromResult<bool>(false); return false; }

        // --- Unity Lobby: don't publish or list lobbies --------------------------------------------------------------
        // Original = HostOnlineRoomAsync(...) THEN publish a Unity Lobby. Keep the first half, drop the second.
        [HarmonyPatch(typeof(LobbyManager), "CreateLobby")]
        [HarmonyPrefix]
        private static bool L1(LobbyManager __instance, bool isPrivate, GameMode gameMode, LobbyLanguage language)
        {
            try
            {
                __instance.OnCreateLobbyStarted?.Invoke();
                MultiplayerManager.Instance.HostOnlineRoomAsync(isPrivate, gameMode, language);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Offline CreateLobby failed: {e}");
                __instance.OnCreateLobbyFailed?.Invoke();
            }
            return false;
        }
        [HarmonyPatch(typeof(LobbyManager), "JoinLobby")] [HarmonyPrefix] private static bool L2() => false;
        [HarmonyPatch(typeof(LobbyManager), "DeleteLobby")] [HarmonyPrefix] private static bool L3() => false;
        [HarmonyPatch(typeof(LobbyManager), "LeaveLobby")] [HarmonyPrefix] private static bool L4() => false;
        [HarmonyPatch(typeof(LobbyManager), "UpdateLobbyData")] [HarmonyPrefix] private static bool L5() => false;
        [HarmonyPatch(typeof(LobbyManager), "HandleHeartBeat")] [HarmonyPrefix] private static bool L6() => false;
        [HarmonyPatch(typeof(LobbyManager), "RefreshLobbyList")]
        [HarmonyPrefix]
        private static bool L7(ref Task __result) { __result = Task.CompletedTask; return false; }

        // --- Vivox voice chat: disabled ------------------------------------------------------------------------------
        [HarmonyPatch(typeof(VivoxManager), "LogInAsync")]
        [HarmonyPrefix]
        private static bool V1(ref Task __result) { __result = Task.CompletedTask; return false; }
        [HarmonyPatch(typeof(VivoxManager), "LogOutAsync")]
        [HarmonyPrefix]
        private static bool V2(ref Task __result) { __result = Task.CompletedTask; return false; }
        [HarmonyPatch(typeof(VivoxManager), "SetActiveAudioChannel")]
        [HarmonyPrefix]
        private static bool V3(ref Task __result) { __result = Task.CompletedTask; return false; }
        [HarmonyPatch(typeof(VivoxManager), "LeaveAllCurrentChannels")]
        [HarmonyPrefix]
        private static bool V4(ref Task __result) { __result = Task.CompletedTask; return false; }
        [HarmonyPatch(typeof(VivoxManager), "JoinedRoom")] [HarmonyPrefix] private static bool V5() => false;
        [HarmonyPatch(typeof(VivoxManager), "SwitchToLobbyChat")] [HarmonyPrefix] private static bool V6() => false;
        [HarmonyPatch(typeof(VivoxManager), "SwitchToGameChat")] [HarmonyPrefix] private static bool V7() => false;
        [HarmonyPatch(typeof(VivoxManager), "SwitchToPrivateChat")] [HarmonyPrefix] private static bool V8() => false;
    }
}
