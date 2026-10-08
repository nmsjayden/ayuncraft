using System;
using System.Net;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using TMPro;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Networking.Transport.Relay;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppList = Il2CppSystem.Collections.Generic.List<Unity.Services.Relay.Models.RelayServerEndpoint>;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Vivox;
using UnityEngine;
using Il2Task = Il2CppSystem.Threading.Tasks.Task;
using Il2BoolTask = Il2CppSystem.Threading.Tasks.Task<bool>;

namespace FnafSelfHost
{
    /// <summary>
    /// FNAF Online self-host mod.
    ///  * Replaces Unity Relay with a direct UDP connection (the host's PC is the server).
    ///  * The room code is the host's address encoded as 10 characters, so "normal room code" flow works.
    ///  * The room-code box also accepts a plain IP / host name (optionally ":port").
    ///  * Optionally switches off every Unity online service so the game works while the developer's servers are down.
    /// </summary>
    [BepInPlugin("local.fnafonline.selfhost", "FNAF Online Self-Host", "0.3.1")]
    public class Plugin : BasePlugin
    {
        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<ushort> Port;
        internal static ConfigEntry<string> PublicAddress;
        internal static ConfigEntry<bool> TryUpnp;
        internal static ConfigEntry<bool> OfflineServices;

        public override void Load()
        {
            Log = base.Log;
            Enabled = Config.Bind("General", "Enabled", true, "Use direct connection instead of Unity Relay.");
            Port = Config.Bind("General", "Port", (ushort)7777, "UDP port the host listens on.");
            PublicAddress = Config.Bind("Host", "PublicAddress", "",
                "Leave EMPTY to detect it automatically. Only set this if you host through a VPN or tunnel " +
                "(e.g. your Tailscale IP, or a playit.gg address like name.joinmc.link:12345).");
            TryUpnp = Config.Bind("Host", "TryUpnp", true,
                "Automatically open the port on your router with UPnP when you host.");
            OfflineServices = Config.Bind("General", "OfflineServices", true,
                "Skip every Unity online service (login, cloud code, lobby list, voice chat). Fixes the 'no online connection' " +
                "screen when the developer's servers are down. Voice chat does not work in this mode.");

            ClassInjector.RegisterTypeInIl2Cpp<ServicesKicker>();
            var harmony = new Harmony("local.fnafonline.selfhost");
            harmony.PatchAll(typeof(Patches));
            harmony.PatchAll(typeof(RelayPatches));
            if (OfflineServices.Value) harmony.PatchAll(typeof(OfflinePatches));
            Log.LogInfo($"Self-host mod loaded (direct={Enabled.Value}, offline services={OfflineServices.Value}, port={Port.Value}).");
        }

        /// <summary>The room code this PC hands out when hosting.</summary>
        internal static string BuildHostCode(ushort listenPort)
        {
            var port = listenPort;
            string address = null;

            var over = PublicAddress.Value?.Trim();
            if (!string.IsNullOrEmpty(over))
            {
                if (AddressCode.TryResolve(over, listenPort, out var h, out var p))
                {
                    port = p;
                    if (IPAddress.TryParse(h, out var lit) && lit.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        address = lit.ToString();
                    else
                    {
                        // A host name can't be packed into a short code; hand out "NAME:PORT" instead.
                        var raw = $"{h}:{p}".ToUpperInvariant();
                        Log.LogInfo($"Room code (host name, share as-is): {raw}");
                        return raw;
                    }
                }
                else Log.LogWarning($"PublicAddress '{over}' isn't valid; detecting automatically instead.");
            }

            if (address == null)
            {
                var r = HostNetwork.WaitForResult(listenPort, TryUpnp.Value, 9000);
                address = r.PublicIp ?? r.LanIp ?? "127.0.0.1";
                Log.LogInfo($"Detected address {address} (LAN {r.LanIp}, port opened automatically: {r.PortOpened}).");
                if (r.Problem != null) Log.LogWarning(r.Problem);
            }

            var code = AddressCode.Encode(IPAddress.Parse(address), port);
            Log.LogInfo($"Room code for {address}:{port} = {code}");
            return code;
        }
    }

    internal static class Patches
    {
        // The game insists on a 6-character code before it even tries to join. We hand it this stand-in
        // and remember the real destination ourselves.
        private const string StandIn = "DIRECT";
        internal static string _pendingHost;
        internal static ushort _pendingPort;

        // ---- JOIN --------------------------------------------------------------------------------------------------
        // Runs first: decode whatever was typed (room code / IP / host name) and swap in a code the game accepts.
        [HarmonyPatch(typeof(MultiplayerManager), nameof(MultiplayerManager.JoinOnlineRoomAsync))]
        [HarmonyPrefix]
        private static void JoinOnlineRoom_Prefix(ref string joinCode)
        {
            if (!Plugin.Enabled.Value) return;
            if (AddressCode.TryResolve(joinCode, Plugin.Port.Value, out var host, out var port))
            {
                Plugin.Log.LogInfo($"Joining '{joinCode}' -> {host}:{port}");
                _pendingHost = host; _pendingPort = port;
                joinCode = StandIn;
            }
            else
            {
                Plugin.Log.LogWarning($"'{joinCode}' isn't a room code or an address; the game will report it as invalid.");
                _pendingHost = null;
            }
        }

        // The game also requires the displayed code to be exactly 6 characters long. Ours is longer.
        [HarmonyPatch(typeof(MultiplayerManager), nameof(MultiplayerManager.IsCodeValid))]
        [HarmonyPrefix]
        private static bool IsCodeValid_Prefix(MultiplayerManager __instance, ref bool __result)
        {
            if (!Plugin.Enabled.Value) return true;
            __result = !string.IsNullOrEmpty(__instance.JoinCode);
            return false;
        }

        // ---- UI ----------------------------------------------------------------------------------------------------
        // Let the room-code box take long codes, dots and colons; start looking up the network in the background.
        [HarmonyPatch(typeof(MatchmakingUI), "Start")]
        [HarmonyPostfix]
        private static void MatchmakingStart_Postfix(MatchmakingUI __instance)
        {
            if (!Plugin.Enabled.Value) return;
            try
            {
                var f = __instance.roomCodeInputField;
                f.characterLimit = 0;
                f.contentType = TMP_InputField.ContentType.Standard;
                f.characterValidation = TMP_InputField.CharacterValidation.None;
                var ph = f.placeholder?.TryCast<TMP_Text>();
                if (ph != null) ph.text = "Room code or IP...";
            }
            catch (Exception e) { Plugin.Log.LogWarning($"Couldn't relax the room-code box: {e.Message}"); }

            HostNetwork.Begin(Plugin.Port.Value, Plugin.TryUpnp.Value);
        }
    }

    /// <summary>
    /// The game's host/join code calls Unity's Relay service three times (create allocation, get join code, join allocation)
    /// and feeds the answers to UnityTransport.SetRelayServerData. The game compiles its relay helpers straight into the
    /// callers, so we can't replace them - instead we answer those three calls ourselves with harmless placeholder data and
    /// redirect SetRelayServerData to a direct connection. Everything else the game does (connection payload, StartHost,
    /// StartClient, lobby scene, ...) then runs untouched.
    /// </summary>
    internal static class RelayPatches
    {
        private enum Mode { None, Host, Client }
        private static Mode _mode;
        private static string _hostCode;

        private static Il2CppStructArray<byte> Bytes(int n)
        {
            var a = new Il2CppStructArray<byte>(n);
            for (int i = 0; i < n; i++) a[i] = (byte)(i + 1);
            return a;
        }

        private static Il2CppList Endpoints(string host, int port)
        {
            var l = new Il2CppList();
            // The game asks for one of these connection types; offer all so whichever it uses is found.
            foreach (var t in new[] { "dtls", "udp", "wss", "ws" })
                l.Add(new RelayServerEndpoint(t, RelayServerEndpoint.NetworkOptions.Udp, true, t == "dtls" || t == "wss", host, port));
            return l;
        }

        // ---- HOST ----
        [HarmonyPatch(typeof(WrappedRelayService), nameof(WrappedRelayService.CreateAllocationAsync))]
        [HarmonyPrefix]
        private static bool CreateAllocation(ref Il2CppSystem.Threading.Tasks.Task<Allocation> __result)
        {
            if (!Plugin.Enabled.Value) return true;
            try
            {
                var port = Plugin.Port.Value;
                _hostCode = Plugin.BuildHostCode(port); // finds the public IP + opens the port; the code is just that address
                _mode = Mode.Host;
                var alloc = new Allocation(Il2CppSystem.Guid.NewGuid(), Endpoints("127.0.0.1", port),
                    new RelayServer("127.0.0.1", port), Bytes(64), Bytes(255), Bytes(16), "local");
                __result = Il2Task.FromResult<Allocation>(alloc);
                return false;
            }
            catch (Exception e) { Plugin.Log.LogError($"Fake CreateAllocation failed: {e}"); return true; }
        }

        [HarmonyPatch(typeof(WrappedRelayService), nameof(WrappedRelayService.GetJoinCodeAsync))]
        [HarmonyPrefix]
        private static bool GetJoinCode(ref Il2CppSystem.Threading.Tasks.Task<string> __result)
        {
            if (!Plugin.Enabled.Value || _mode != Mode.Host || _hostCode == null) return true;
            try { GUIUtility.systemCopyBuffer = _hostCode; } catch { /* clipboard unavailable */ }
            Plugin.Log.LogInfo($"Your room code is {_hostCode} (copied to the clipboard).");
            __result = Il2Task.FromResult<string>(_hostCode);
            return false;
        }

        // ---- JOIN ----
        [HarmonyPatch(typeof(WrappedRelayService), nameof(WrappedRelayService.JoinAllocationAsync))]
        [HarmonyPrefix]
        private static bool JoinAllocation(ref Il2CppSystem.Threading.Tasks.Task<JoinAllocation> __result)
        {
            if (!Plugin.Enabled.Value || Patches._pendingHost == null) return true;
            try
            {
                var alloc = new JoinAllocation(Il2CppSystem.Guid.NewGuid(), Endpoints("127.0.0.1", Patches._pendingPort),
                    new RelayServer("127.0.0.1", Patches._pendingPort), Bytes(64), Bytes(255), Bytes(16), "local", Bytes(255));
                _mode = Mode.Client;
                __result = Il2Task.FromResult<JoinAllocation>(alloc);
                return false;
            }
            catch (Exception e) { Plugin.Log.LogError($"Fake JoinAllocation failed: {e}"); return true; }
        }

        // ---- the actual redirect ----
        [HarmonyPatch(typeof(UnityTransport), nameof(UnityTransport.SetRelayServerData), new[] { typeof(RelayServerData) })]
        [HarmonyPrefix]
        private static bool SetRelayServerData(UnityTransport __instance)
        {
            if (!Plugin.Enabled.Value || _mode == Mode.None) return true;
            try
            {
                if (_mode == Mode.Host)
                {
                    __instance.SetConnectionData("127.0.0.1", Plugin.Port.Value, "0.0.0.0"); // listen on every interface
                    Plugin.Log.LogInfo($"Hosting directly: listening on UDP {Plugin.Port.Value}.");
                }
                else
                {
                    var ip = AddressCode.ToIPv4(Patches._pendingHost);
                    __instance.SetConnectionData(ip, Patches._pendingPort);
                    Plugin.Log.LogInfo($"Connecting directly to {ip}:{Patches._pendingPort} ...");
                    Patches._pendingHost = null;
                }
                _mode = Mode.None;
                return false;
            }
            catch (Exception e) { Plugin.Log.LogError($"Direct transport setup failed: {e}"); _mode = Mode.None; return true; }
        }
    }

    /// <summary>
    /// Waits until Unity Services have initialised locally (no internet needed), then fires the "services ready" event
    /// the main menu is waiting for. Gives up waiting after 8 seconds and fires anyway.
    /// </summary>
    public class ServicesKicker : MonoBehaviour
    {
        public ServicesKicker(IntPtr ptr) : base(ptr) { }
        private float _t;
        private void Update()
        {
            _t += Time.unscaledDeltaTime;
            bool ready = UnityServices.State == ServicesInitializationState.Initialized;
            if (_t < 0.75f || (!ready && _t < 8f)) return;
            var si = ServicesInitialiser.Instance;
            si.areServicesInitialised = true;
            si.OnConnectionToServicesCompleted?.Invoke();
            Plugin.Log.LogInfo($"Offline mode: Unity services state = {UnityServices.State}; reported ready after {_t:0.0}s.");
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
        /// <summary>A stable per-install player id (used instead of a Unity login id).</summary>
        internal static string LocalPlayerId()
        {
            if (_id != null) return _id;
            const string key = "FnafSelfHostPlayerId";
            var saved = PlayerPrefs.GetString(key, "");
            if (string.IsNullOrEmpty(saved)) { saved = Guid.NewGuid().ToString("N"); PlayerPrefs.SetString(key, saved); PlayerPrefs.Save(); }
            return _id = saved;
        }

        // --- startup: skip UnityServices.InitializeAsync + sign-in + update check -----------------------------------
        [HarmonyPatch(typeof(ServicesInitialiser), "Start")]
        [HarmonyPrefix]
        private static bool ServicesStart(ServicesInitialiser __instance)
        {
            // Initialise Unity Services *locally* (needs no internet). Without this every `XxxService.Instance` throws
            // "Singleton is not initialized" each time the game touches it. We just never sign in.
            try { UnityServices.InitializeAsync(); }
            catch (Exception e) { Plugin.Log.LogWarning($"UnityServices.InitializeAsync failed: {e.Message}"); }
            __instance.gameObject.AddComponent<ServicesKicker>();
            return false;
        }

        // Not signed in, so the player id is empty - and the game turns it into a string without a null check.
        [HarmonyPatch(typeof(AuthenticationServiceInternal), "get_PlayerId")]
        [HarmonyPostfix]
        private static void PlayerId_Postfix(ref string __result)
        {
            if (string.IsNullOrEmpty(__result)) __result = LocalPlayerId();
        }

        // --- host asks Cloud Code "is this player a developer / moderator?" for every joiner -----------------------
        [HarmonyPatch(typeof(MultiplayerManager), "IsDeveloper")]
        [HarmonyPrefix]
        private static bool IsDeveloper(ref Il2BoolTask __result) { __result = Il2Task.FromResult<bool>(false); return false; }

        [HarmonyPatch(typeof(MultiplayerManager), "IsModerator")]
        [HarmonyPrefix]
        private static bool IsModerator(ref Il2BoolTask __result) { __result = Il2Task.FromResult<bool>(false); return false; }

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
        [HarmonyPatch(typeof(LobbyManager), "Update")] [HarmonyPrefix] private static bool L8() => false;
        [HarmonyPatch(typeof(LobbyManager), "IsLobbyHost")] [HarmonyPrefix] private static bool L9(ref bool __result) { __result = false; return false; }
        [HarmonyPatch(typeof(MatchmakingUI), "HandlePeriodicListLobbies")] [HarmonyPrefix] private static bool L10() => false;
        [HarmonyPatch(typeof(LobbyManager), "RefreshLobbyList")]
        [HarmonyPrefix]
        private static bool L7(ref Il2Task __result) { __result = Il2Task.CompletedTask; return false; }

        // --- Vivox voice chat: disabled ------------------------------------------------------------------------------
        [HarmonyPatch(typeof(VivoxManager), "LogInAsync")]
        [HarmonyPrefix]
        private static bool V1(ref Il2Task __result) { __result = Il2Task.CompletedTask; return false; }
        [HarmonyPatch(typeof(VivoxManager), "LogOutAsync")]
        [HarmonyPrefix]
        private static bool V2(ref Il2Task __result) { __result = Il2Task.CompletedTask; return false; }
        [HarmonyPatch(typeof(VivoxManager), "SetActiveAudioChannel")]
        [HarmonyPrefix]
        private static bool V3(ref Il2Task __result) { __result = Il2Task.CompletedTask; return false; }
        [HarmonyPatch(typeof(VivoxManager), "LeaveAllCurrentChannels")]
        [HarmonyPrefix]
        private static bool V4(ref Il2Task __result) { __result = Il2Task.CompletedTask; return false; }
        [HarmonyPatch(typeof(VivoxManager), "JoinedRoom")] [HarmonyPrefix] private static bool V5() => false;
        [HarmonyPatch(typeof(VivoxManager), "SwitchToLobbyChat")] [HarmonyPrefix] private static bool V6() => false;
        [HarmonyPatch(typeof(VivoxManager), "SwitchToGameChat")] [HarmonyPrefix] private static bool V7() => false;
        [HarmonyPatch(typeof(VivoxManager), "SwitchToPrivateChat")] [HarmonyPrefix] private static bool V8() => false;

        // Same thing one level down, in Unity's Vivox SDK, in case the game calls it from somewhere it inlined its own wrappers.
        [HarmonyPatch(typeof(VivoxServiceInternal), "InitializeAsync")] [HarmonyPrefix] private static bool X1(ref Il2Task __result) { __result = Il2Task.CompletedTask; return false; }
        [HarmonyPatch(typeof(VivoxServiceInternal), "LoginAsync")] [HarmonyPrefix] private static bool X2(ref Il2Task __result) { __result = Il2Task.CompletedTask; return false; }
        [HarmonyPatch(typeof(VivoxServiceInternal), "LogoutAsync")] [HarmonyPrefix] private static bool X3(ref Il2Task __result) { __result = Il2Task.CompletedTask; return false; }
        [HarmonyPatch(typeof(VivoxServiceInternal), "JoinGroupChannelAsync")] [HarmonyPrefix] private static bool X4(ref Il2Task __result) { __result = Il2Task.CompletedTask; return false; }
        [HarmonyPatch(typeof(VivoxServiceInternal), "JoinEchoChannelAsync")] [HarmonyPrefix] private static bool X5(ref Il2Task __result) { __result = Il2Task.CompletedTask; return false; }
        [HarmonyPatch(typeof(VivoxServiceInternal), "LeaveAllChannelsAsync")] [HarmonyPrefix] private static bool X6(ref Il2Task __result) { __result = Il2Task.CompletedTask; return false; }
        [HarmonyPatch(typeof(VivoxServiceInternal), "LeaveChannelAsync")] [HarmonyPrefix] private static bool X7(ref Il2Task __result) { __result = Il2Task.CompletedTask; return false; }
        [HarmonyPatch(typeof(VivoxServiceInternal), "SetChannelTransmissionModeAsync")] [HarmonyPrefix] private static bool X8(ref Il2Task __result) { __result = Il2Task.CompletedTask; return false; }
    }
}
