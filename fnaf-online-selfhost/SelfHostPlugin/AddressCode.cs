using System;
using System.Linq;
using System.Net;

namespace FnafSelfHost
{
    /// <summary>
    /// Turns "IPv4 + port" into a short room code and back, so a normal room code is enough to join:
    /// 203.0.113.9:7777  ->  "0S6RD-7QV1G"  (Crockford base32, 48 bits in 10 characters).
    /// </summary>
    internal static class AddressCode
    {
        private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

        public static string Encode(IPAddress ip, ushort port)
        {
            var b = ip.GetAddressBytes();
            ulong v = ((ulong)b[0] << 40) | ((ulong)b[1] << 32) | ((ulong)b[2] << 24) | ((ulong)b[3] << 16) | port;
            var c = new char[10];
            for (int i = 9; i >= 0; i--) { c[i] = Alphabet[(int)(v & 31)]; v >>= 5; }
            return new string(c, 0, 5) + "-" + new string(c, 5, 5);
        }

        private static bool TryDecode(string text, out IPAddress ip, out ushort port)
        {
            ip = null; port = 0;
            var t = new string(text.ToUpperInvariant().Where(ch => ch != '-' && ch != ' ').ToArray())
                .Replace('O', '0').Replace('I', '1').Replace('L', '1');
            if (t.Length != 10 || t.Any(ch => Alphabet.IndexOf(ch) < 0)) return false;
            ulong v = 0;
            foreach (var ch in t) v = (v << 5) | (uint)Alphabet.IndexOf(ch);
            if (v >> 48 != 0) return false;
            port = (ushort)(v & 0xFFFF);
            ip = new IPAddress(new[] { (byte)(v >> 40), (byte)(v >> 32), (byte)(v >> 24), (byte)(v >> 16) });
            return port != 0;
        }

        /// <summary>Accepts a generated room code, an IPv4 address, "host", "host:port" or "ip:port".</summary>
        public static bool TryResolve(string text, ushort defaultPort, out string host, out ushort port)
        {
            host = null; port = defaultPort;
            if (string.IsNullOrWhiteSpace(text)) return false;
            text = text.Trim();

            if (text.IndexOf('.') < 0 && text.IndexOf(':') < 0)
            {
                if (!TryDecode(text, out var ip, out var p)) return false;
                host = ip.ToString(); port = p; return true;
            }

            var h = text;
            int colon = text.LastIndexOf(':');
            if (colon > 0 && ushort.TryParse(text.Substring(colon + 1), out var parsed)) { h = text.Substring(0, colon); port = parsed; }
            h = h.Trim().TrimEnd('/');
            if (h.Length == 0) return false;
            host = h;
            return true;
        }

        /// <summary>Unity Transport wants an IP literal, so resolve host names here.</summary>
        public static string ToIPv4(string host)
        {
            if (IPAddress.TryParse(host, out var lit)) return lit.ToString();
            var addrs = Dns.GetHostAddresses(host);
            var v4 = addrs.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
            return (v4 ?? addrs.First()).ToString();
        }
    }
}
