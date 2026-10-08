using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Open.Nat;

namespace FnafSelfHost
{
    /// <summary>Works out the address friends should use to reach this PC, and opens the port on the router with UPnP.</summary>
    internal static class HostNetwork
    {
        internal sealed class Result
        {
            public string PublicIp;        // what friends outside your network connect to (null if unknown)
            public string LanIp;           // your address on the local network
            public bool PortOpened;        // UPnP mapping succeeded
            public string Problem;         // human readable hint when something is wrong
        }

        private static Task<Result> _task;
        private static NatDevice _device;
        private static Mapping _mapping;
        private static readonly object Gate = new();

        /// <summary>Start (or reuse) the background lookup. Cheap to call repeatedly.</summary>
        public static Task<Result> Begin(ushort port, bool tryUpnp)
        {
            lock (Gate) return _task ??= Task.Run(() => Run(port, tryUpnp));
        }

        public static Result WaitForResult(ushort port, bool tryUpnp, int timeoutMs)
        {
            var t = Begin(port, tryUpnp);
            return t.Wait(timeoutMs) ? t.Result : new Result { LanIp = FindLanIp(), Problem = "Timed out while detecting the network." };
        }

        private static Result Run(ushort port, bool tryUpnp)
        {
            var r = new Result { LanIp = FindLanIp() };
            string upnpWan = null;

            if (tryUpnp)
            {
                try
                {
                    var discoverer = new NatDiscoverer();
                    using var cts = new CancellationTokenSource(5000);
                    _device = discoverer.DiscoverDeviceAsync(PortMapper.Upnp, cts).GetAwaiter().GetResult();
                    var wan = _device.GetExternalIPAsync().GetAwaiter().GetResult();
                    upnpWan = wan == null ? null : wan.ToString();
                    _mapping = new Mapping(Protocol.Udp, port, port, 0, "FNAF Online");
                    try { _device.DeletePortMapAsync(_mapping).GetAwaiter().GetResult(); } catch { /* none existed */ }
                    _device.CreatePortMapAsync(_mapping).GetAwaiter().GetResult();
                    r.PortOpened = true;
                    AppDomain.CurrentDomain.ProcessExit += (_, _) => Cleanup();
                }
                catch (Exception e)
                {
                    Plugin.Log.LogInfo($"UPnP unavailable ({e.GetType().Name}); the router will need UDP {port} forwarded by hand, or use a VPN.");
                }
            }

            string seen = null;
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                seen = http.GetStringAsync("https://api.ipify.org").GetAwaiter().GetResult().Trim();
                if (!IPAddress.TryParse(seen, out _)) seen = null;
            }
            catch { /* offline or blocked */ }

            r.PublicIp = seen ?? upnpWan;
            if (r.PublicIp == null)
                r.Problem = "Couldn't find your public IP (no internet?). Only people on your local network can join.";
            else if (upnpWan != null && seen != null && upnpWan != seen)
                r.Problem = "Your internet provider shares one public address between customers (CGNAT/double NAT), so friends outside your home can't connect directly. Use Tailscale/ZeroTier/playit.gg and set PublicAddress in the config.";
            else if (!r.PortOpened)
                r.Problem = $"Couldn't open the port automatically. Forward UDP {port} to this PC in your router, or use a VPN.";
            return r;
        }

        public static void Cleanup()
        {
            try { if (_device != null && _mapping != null) _device.DeletePortMapAsync(_mapping).GetAwaiter().GetResult(); } catch { }
        }

        private static string FindLanIp()
        {
            try
            {
                using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
                s.Connect("203.0.113.1", 9); // no packet is sent; just picks the outbound interface
                return ((IPEndPoint)s.LocalEndPoint).Address.ToString();
            }
            catch
            {
                return NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                    .Select(a => a.Address).Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                    .Select(a => a.ToString()).FirstOrDefault();
            }
        }
    }
}
