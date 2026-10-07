using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
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

            new Harmony("local.fnafonline.selfhost").PatchAll(typeof(Patches));
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
                    String2 = AuthenticationService.Instance.PlayerId,
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
}
