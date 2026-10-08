using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace FnafSelfHost
{
    internal static class RelayLog
    {
        public static Action<string> Info = _ => { };
        public static Action<string> Warn = _ => { };
    }

    /// <summary>The free public MQTT brokers we can use as a pipe, in order of preference. The index travels inside the room code.</summary>
    internal static class RelayBrokers
    {
        public static readonly (string Host, int Port, bool Tls)[] Public =
        {
            ("broker.hivemq.com", 1883, false),
            ("broker.emqx.io", 1883, false),
            ("test.mosquitto.org", 1883, false),
            ("broker.hivemq.com", 8883, true),
            ("broker.emqx.io", 8883, true),
            ("test.mosquitto.org", 8883, true),
        };
        public const int CustomIndex = 31; // "use the broker from the config file"
        public static string CustomHost; public static int CustomPort = 1883; public static bool CustomTls;

        public static (string Host, int Port, bool Tls) Get(int index) =>
            index == CustomIndex ? (CustomHost, CustomPort, CustomTls) : Public[index];

        public static bool IsValid(int index) =>
            index == CustomIndex ? !string.IsNullOrEmpty(CustomHost) : index >= 0 && index < Public.Length;
    }

    /// <summary>
    /// A relay "room": which broker to meet on + a random secret. The secret never leaves the room code; the broker topic
    /// is a hash of it and every packet is AES-GCM encrypted with a key derived from it, so the broker (and anybody else
    /// watching it) can't read or inject traffic.
    /// </summary>
    internal sealed class RelayRoom
    {
        private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        public int BrokerIndex { get; private set; }
        public string SecretText { get; private set; } // 9 characters = 45 bits
        public string TopicId { get; private set; }
        public byte[] Key { get; private set; }

        public string Code => "RELAY-" + Alphabet[BrokerIndex] + SecretText.Substring(0, 4) + "-" + SecretText.Substring(4);

        public static RelayRoom Create(int brokerIndex)
        {
            var rnd = new byte[9]; RandomNumberGenerator.Fill(rnd);
            var sb = new StringBuilder();
            foreach (var b in rnd) sb.Append(Alphabet[b & 31]);
            return Build(brokerIndex, sb.ToString());
        }

        public static bool TryParse(string text, out RelayRoom room)
        {
            room = null;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var t = new string(text.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
            if (!t.StartsWith("RELAY")) return false;
            t = t.Substring(5).Replace('O', '0').Replace('I', '1').Replace('L', '1');
            if (t.Length != 10 || t.Any(c => Alphabet.IndexOf(c) < 0)) return false;
            int idx = Alphabet.IndexOf(t[0]);
            if (!RelayBrokers.IsValid(idx)) return false;
            room = Build(idx, t.Substring(1));
            return true;
        }

        private static RelayRoom Build(int idx, string secret)
        {
            using var sha = SHA256.Create();
            var key = sha.ComputeHash(Encoding.UTF8.GetBytes("fnaf-selfhost/key|" + secret));
            var topic = sha.ComputeHash(Encoding.UTF8.GetBytes("fnaf-selfhost/topic|" + secret));
            return new RelayRoom { BrokerIndex = idx, SecretText = secret, Key = key, TopicId = BitConverter.ToString(topic, 0, 10).Replace("-", "").ToLowerInvariant() };
        }

        public string ToHostTopic => $"fnafsh/{TopicId}/h"; // clients -> host
        public string ToClientTopic => $"fnafsh/{TopicId}/c"; // host -> clients
    }

    internal static class RelayCodec
    {
        // payload = [dir:1][clientId:4][nonce:12][tag:16][ciphertext]; dir+clientId are authenticated but not secret.
        public static byte[] Seal(byte[] key, byte dir, uint clientId, byte[] data, int len)
        {
            var hdr = new byte[5]; hdr[0] = dir; BitConverter.GetBytes(clientId).CopyTo(hdr, 1);
            var nonce = new byte[12]; RandomNumberGenerator.Fill(nonce);
            var ct = new byte[len]; var tag = new byte[16];
            using (var g = new AesGcm(key)) g.Encrypt(nonce, data.AsSpan(0, len), ct, tag, hdr);
            var o = new byte[5 + 12 + 16 + len];
            hdr.CopyTo(o, 0); nonce.CopyTo(o, 5); tag.CopyTo(o, 17); ct.CopyTo(o, 33);
            return o;
        }

        public static bool Open(byte[] key, byte[] p, out byte dir, out uint clientId, out byte[] data)
        {
            dir = 0; clientId = 0; data = null;
            if (p.Length < 33) return false;
            try
            {
                dir = p[0]; clientId = BitConverter.ToUInt32(p, 1);
                var hdr = p.AsSpan(0, 5).ToArray();
                data = new byte[p.Length - 33];
                using var g = new AesGcm(key);
                g.Decrypt(p.AsSpan(5, 12), p.AsSpan(33), p.AsSpan(17, 16), data, hdr);
                return true;
            }
            catch (CryptographicException) { return false; } // forged / corrupted packet: ignore
        }
    }

    /// <summary>
    /// Runs on the hosting PC. Packets that friends send to the room are handed to the game's own UDP port as if they came
    /// from a normal client (one local socket per friend), and the game's replies go back out the same pipe.
    /// </summary>
    internal sealed class RelayHost : IDisposable
    {
        private sealed class Session { public UdpClient Sock; public long Last; }
        private readonly RelayRoom _room;
        private readonly int _gamePort;
        private MqttLite _mqtt;
        private readonly ConcurrentDictionary<uint, Session> _sessions = new();
        private Timer _gc;
        private volatile bool _stop;

        public RelayRoom Room => _room;
        public RelayHost(RelayRoom room, int gamePort) { _room = room; _gamePort = gamePort; }

        public bool Start(int waitMs = 8000)
        {
            var (h, p, tls) = RelayBrokers.Get(_room.BrokerIndex);
            _mqtt = new MqttLite(h, p, tls);
            _mqtt.Message += OnMessage;
            _mqtt.Subscribe(_room.ToHostTopic);
            _mqtt.Start();
            if (!_mqtt.WaitReady(waitMs)) { Dispose(); return false; }
            _gc = new Timer(_ => Sweep(), null, 30000, 30000);
            return true;
        }

        private void OnMessage(string topic, byte[] payload)
        {
            if (topic != _room.ToHostTopic) return;
            if (!RelayCodec.Open(_room.Key, payload, out var dir, out var id, out var data) || dir != 0) return;
            var s = _sessions.GetOrAdd(id, NewSession);
            s.Last = Environment.TickCount64;
            try { s.Sock.Send(data, data.Length, new IPEndPoint(IPAddress.Loopback, _gamePort)); } catch { }
        }

        private Session NewSession(uint id)
        {
            var sock = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var s = new Session { Sock = sock, Last = Environment.TickCount64 };
            new Thread(() =>
            {
                var any = new IPEndPoint(IPAddress.Any, 0);
                while (!_stop)
                {
                    byte[] d;
                    try { d = sock.Receive(ref any); } catch { break; }
                    s.Last = Environment.TickCount64;
                    _mqtt.Publish(_room.ToClientTopic, RelayCodec.Seal(_room.Key, 1, id, d, d.Length));
                }
            }) { IsBackground = true, Name = "relay-session" }.Start();
            RelayLog.Info($"Relay: a player joined through the relay (#{_sessions.Count + 1}).");
            return s;
        }

        private void Sweep()
        {
            foreach (var kv in _sessions)
                if (Environment.TickCount64 - kv.Value.Last > 120000 && _sessions.TryRemove(kv.Key, out var s))
                    try { s.Sock.Close(); } catch { }
        }

        public void Dispose()
        {
            _stop = true;
            _gc?.Dispose();
            _mqtt?.Dispose();
            foreach (var s in _sessions.Values) try { s.Sock.Close(); } catch { }
            _sessions.Clear();
        }
    }

    /// <summary>
    /// Runs on a friend's PC. Opens a UDP port on 127.0.0.1; the game "connects" to that, and the packets travel through the
    /// broker to the host.
    /// </summary>
    internal sealed class RelayClient : IDisposable
    {
        private readonly RelayRoom _room;
        private readonly uint _id;
        private MqttLite _mqtt;
        private UdpClient _sock;
        private volatile IPEndPoint _game;
        private volatile bool _stop;

        public int LocalPort { get; private set; }
        public RelayClient(RelayRoom room) { _room = room; var b = new byte[4]; RandomNumberGenerator.Fill(b); _id = BitConverter.ToUInt32(b, 0); }

        public bool Start(int waitMs = 8000)
        {
            _sock = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            LocalPort = ((IPEndPoint)_sock.Client.LocalEndPoint).Port;

            var (h, p, tls) = RelayBrokers.Get(_room.BrokerIndex);
            _mqtt = new MqttLite(h, p, tls);
            _mqtt.Message += OnMessage;
            _mqtt.Subscribe(_room.ToClientTopic);
            _mqtt.Start();

            new Thread(() =>
            {
                var any = new IPEndPoint(IPAddress.Any, 0);
                while (!_stop)
                {
                    byte[] d;
                    try { d = _sock.Receive(ref any); } catch { break; }
                    _game = new IPEndPoint(any.Address, any.Port);
                    _mqtt.Publish(_room.ToHostTopic, RelayCodec.Seal(_room.Key, 0, _id, d, d.Length));
                }
            }) { IsBackground = true, Name = "relay-client" }.Start();

            if (!_mqtt.WaitReady(waitMs)) { Dispose(); return false; }
            return true;
        }

        private void OnMessage(string topic, byte[] payload)
        {
            if (topic != _room.ToClientTopic) return;
            if (!RelayCodec.Open(_room.Key, payload, out var dir, out var id, out var data) || dir != 1 || id != _id) return;
            var g = _game;
            if (g != null) try { _sock.Send(data, data.Length, g); } catch { }
        }

        public void Dispose()
        {
            _stop = true;
            _mqtt?.Dispose();
            try { _sock?.Close(); } catch { }
        }
    }
}
