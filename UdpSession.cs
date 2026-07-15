using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using BepInEx.Logging;

namespace DaveTheDiverMP;

internal enum SessionRole
{
    Offline,
    Host,
    Client
}

internal sealed class UdpSession : IDisposable
{
    private readonly ManualLogSource _log;
    private readonly ConcurrentQueue<UdpReceiveResult> _incoming = new();
    private readonly CancellationTokenSource _stop = new();
    private UdpClient _udp;
    private IPEndPoint _remote;
    private SessionRole _role;
    private uint _sequence;
    private float _lastReceive;
    private float _nextSend;
    private bool _connected;

    internal UdpSession(ManualLogSource log) => _log = log;

    internal bool Connected => _connected;
    internal int LocalPort => ((IPEndPoint)_udp.Client.LocalEndPoint).Port;

    internal void Start(SessionRole role, string address, int port)
    {
        _role = role;
        if (role == SessionRole.Offline)
        {
            _log.LogInfo("Network: Offline");
            return;
        }

        try
        {
            if (role == SessionRole.Host)
            {
                _udp = new UdpClient(new IPEndPoint(IPAddress.Any, port));
                _log.LogInfo($"Network: hosting UDP/{port}");
            }
            else
            {
                if (!IPAddress.TryParse(address, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
                    throw new ArgumentException($"Invalid IPv4 address: {address}");

                _udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
                _remote = new IPEndPoint(ip, port);
                _log.LogInfo($"Network: connecting to {_remote}");
            }

            _ = ReceiveLoop();
        }
        catch (Exception exception)
        {
            _log.LogError($"Network start failed: {exception.Message}");
            Dispose();
        }
    }

    internal void Update(float now)
    {
        if (_udp == null)
            return;

        while (_incoming.TryDequeue(out var received))
            Handle(received, now);

        if (_connected && now - _lastReceive > 5f)
        {
            _connected = false;
            _log.LogWarning("Network: peer timed out");
            if (_role == SessionRole.Host)
                _remote = null;
        }

        if (now < _nextSend)
            return;

        _nextSend = now + 1f;
        if (_role == SessionRole.Client && !_connected)
            Send(PacketType.Hello);
        else if (_remote != null)
            Send(PacketType.Heartbeat);
    }

    private async Task ReceiveLoop()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
                _incoming.Enqueue(await _udp.ReceiveAsync(_stop.Token));
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception exception)
        {
            _log.LogError($"Network receive failed: {exception.Message}");
        }
    }

    private void Handle(UdpReceiveResult received, float now)
    {
        if (!Protocol.TryDecode(received.Buffer, out var type, out _))
            return;

        if (_role == SessionRole.Host && _remote == null && type == PacketType.Hello)
            _remote = received.RemoteEndPoint;

        if (_remote == null || !received.RemoteEndPoint.Equals(_remote))
            return;

        _lastReceive = now;

        if (type == PacketType.Disconnect)
        {
            _connected = false;
            _log.LogInfo("Network: peer disconnected");
            if (_role == SessionRole.Host)
                _remote = null;
            return;
        }

        if (_role == SessionRole.Host && type == PacketType.Hello)
        {
            if (!_connected)
                _log.LogInfo($"Network: client connected from {_remote}");
            _connected = true;
            Send(PacketType.HelloAck);
        }
        else if (_role == SessionRole.Client && type == PacketType.HelloAck)
        {
            if (!_connected)
                _log.LogInfo("Network: connected to host");
            _connected = true;
        }
    }

    private void Send(PacketType type)
    {
        if (_udp == null || _remote == null)
            return;

        try
        {
            var packet = Protocol.Encode(type, ++_sequence);
            _udp.Send(packet, packet.Length, _remote);
        }
        catch (Exception exception)
        {
            _log.LogWarning($"Network send failed: {exception.Message}");
        }
    }

    public void Dispose()
    {
        if (_connected)
            Send(PacketType.Disconnect);
        _stop.Cancel();
        _udp?.Dispose();
        _udp = null;
        _connected = false;
    }
}
