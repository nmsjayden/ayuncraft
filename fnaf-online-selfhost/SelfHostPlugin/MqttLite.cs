using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace FnafSelfHost
{
    /// <summary>
    /// A tiny MQTT 3.1.1 client (connect / subscribe / publish at QoS 0 / keep-alive / auto-reconnect), just enough to
    /// use a free public broker as a pipe. It only ever makes OUTGOING connections, so it works behind carrier NAT.
    /// </summary>
    internal sealed class MqttLite : IDisposable
    {
        public event Action<string, byte[]> Message;

        private readonly string _host;
        private readonly int _port;
        private readonly bool _tls;
        private readonly object _writeLock = new();
        private readonly List<string> _subs = new();
        private readonly ManualResetEventSlim _ready = new(false);
        private TcpClient _tcp;
        private Stream _stream;
        private Thread _thread;
        private Timer _ping;
        private volatile bool _stop;
        private ushort _packetId;

        public MqttLite(string host, int port, bool tls) { _host = host; _port = port; _tls = tls; }

        public bool IsReady => _ready.IsSet;
        public bool WaitReady(int ms) => _ready.Wait(ms);

        public void Start()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "mqtt-" + _host };
            _thread.Start();
        }

        public void Subscribe(string topic)
        {
            lock (_subs) _subs.Add(topic);
            if (IsReady) { try { SendSubscribe(topic); } catch { /* reconnect will resubscribe */ } }
        }

        public void Publish(string topic, byte[] payload)
        {
            if (!IsReady) return;
            var t = Encoding.UTF8.GetBytes(topic);
            var body = new byte[2 + t.Length + payload.Length];
            body[0] = (byte)(t.Length >> 8); body[1] = (byte)t.Length;
            Buffer.BlockCopy(t, 0, body, 2, t.Length);
            Buffer.BlockCopy(payload, 0, body, 2 + t.Length, payload.Length);
            try { Send(0x30, body); } catch (Exception e) { RelayLog.Warn("mqtt publish failed: " + e.Message); }
        }

        private void Run()
        {
            while (!_stop)
            {
                try
                {
                    Connect();
                    string[] subs; lock (_subs) subs = _subs.ToArray();
                    foreach (var s in subs) SendSubscribe(s);
                    _ready.Set();
                    RelayLog.Info($"Relay connected to {_host}:{_port}{(_tls ? " (TLS)" : "")}.");
                    _ping = new Timer(_ => { try { Send(0xC0, Array.Empty<byte>()); } catch { } }, null, 15000, 15000);
                    ReadLoop();
                }
                catch (Exception e)
                {
                    if (!_stop) RelayLog.Warn($"Relay link to {_host}:{_port} dropped: {e.Message}");
                }
                finally
                {
                    _ready.Reset();
                    _ping?.Dispose(); _ping = null;
                    try { _tcp?.Close(); } catch { }
                }
                if (!_stop) Thread.Sleep(2000);
            }
        }

        private void Connect()
        {
            _tcp = new TcpClient { NoDelay = true };
            var c = _tcp.ConnectAsync(_host, _port);
            if (!c.Wait(6000)) throw new TimeoutException("connect timed out");
            Stream s = _tcp.GetStream();
            if (_tls)
            {
                // The broker is only a pipe; the payload is already end-to-end encrypted, so we don't need to trust its certificate.
                var ssl = new SslStream(s, false, (_, _, _, _) => true);
                ssl.AuthenticateAsClient(_host);
                s = ssl;
            }
            _stream = s;
            _stream.ReadTimeout = 45000;

            var id = Encoding.UTF8.GetBytes("fnaf" + Guid.NewGuid().ToString("N").Substring(0, 12));
            var body = new List<byte> { 0, 4, (byte)'M', (byte)'Q', (byte)'T', (byte)'T', 4, 0x02, 0, 60 };
            body.Add((byte)(id.Length >> 8)); body.Add((byte)id.Length); body.AddRange(id);
            Send(0x10, body.ToArray());

            var (type, _, data) = ReadPacket();
            if (type != 2 || data.Length < 2 || data[1] != 0) throw new IOException("broker refused the connection");
        }

        private void ReadLoop()
        {
            while (!_stop)
            {
                var (type, flags, data) = ReadPacket();
                if (type != 3) continue; // ignore SUBACK / PINGRESP
                int tl = (data[0] << 8) | data[1];
                var topic = Encoding.UTF8.GetString(data, 2, tl);
                int off = 2 + tl + (((flags >> 1) & 3) != 0 ? 2 : 0);
                var payload = new byte[data.Length - off];
                Buffer.BlockCopy(data, off, payload, 0, payload.Length);
                try { Message?.Invoke(topic, payload); } catch (Exception e) { RelayLog.Warn("relay handler error: " + e.Message); }
            }
        }

        private (int type, int flags, byte[] data) ReadPacket()
        {
            int b = _stream.ReadByte();
            if (b < 0) throw new EndOfStreamException("broker closed the connection");
            int len = 0, shift = 0, x;
            do
            {
                x = _stream.ReadByte();
                if (x < 0) throw new EndOfStreamException();
                len |= (x & 0x7F) << shift; shift += 7;
            } while ((x & 0x80) != 0 && shift < 28);
            var data = new byte[len];
            int read = 0;
            while (read < len)
            {
                int n = _stream.Read(data, read, len - read);
                if (n <= 0) throw new EndOfStreamException();
                read += n;
            }
            return (b >> 4, b & 0x0F, data);
        }

        private void SendSubscribe(string topic)
        {
            var t = Encoding.UTF8.GetBytes(topic);
            ushort id = ++_packetId;
            var body = new byte[2 + 2 + t.Length + 1];
            body[0] = (byte)(id >> 8); body[1] = (byte)id;
            body[2] = (byte)(t.Length >> 8); body[3] = (byte)t.Length;
            Buffer.BlockCopy(t, 0, body, 4, t.Length);
            Send(0x82, body);
        }

        private void Send(byte header, byte[] body)
        {
            var frame = new List<byte>(body.Length + 5) { header };
            int n = body.Length;
            do { byte d = (byte)(n & 0x7F); n >>= 7; if (n > 0) d |= 0x80; frame.Add(d); } while (n > 0);
            frame.AddRange(body);
            var buf = frame.ToArray();
            lock (_writeLock) { _stream.Write(buf, 0, buf.Length); _stream.Flush(); }
        }

        public void Dispose()
        {
            _stop = true;
            try { if (_stream != null) lock (_writeLock) _stream.Write(new byte[] { 0xE0, 0 }, 0, 2); } catch { }
            try { _tcp?.Close(); } catch { }
            _ping?.Dispose();
        }
    }
}
