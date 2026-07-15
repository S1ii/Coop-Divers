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
    private string _localName = "Diver";
    private string _remoteName = "Diver";
    private uint _buildId;
    private uint _localSceneId;
    private uint _remoteSceneId;
    private uint _lastRejectedBuildId;
    private uint _sequence;
    private float _lastReceive;
    private float _nextSend;
    private bool _connected;
    private bool _hasRemoteScene;
    private bool _hasSnapshot;
    private uint _lastSceneSequence;
    private uint _lastSnapshotSequence;
    private PlayerSnapshot _snapshot;

    internal UdpSession(ManualLogSource log) => _log = log;

    internal bool Connected => _connected;
    internal string RemoteName => _remoteName;
    internal int LocalPort => ((IPEndPoint)_udp.Client.LocalEndPoint).Port;
    internal bool SceneMatches(uint sceneId) =>
        _connected && _hasRemoteScene && _remoteSceneId == sceneId;

    internal void SetLocalScene(uint sceneId)
    {
        if (_localSceneId == sceneId)
            return;
        _localSceneId = sceneId;
        if (_connected)
            SendSceneState();
    }

    internal void SendSnapshot(PlayerSnapshot snapshot)
    {
        if (_connected)
            Send(Protocol.EncodeSnapshot(++_sequence, snapshot));
    }

    internal bool TryTakeSnapshot(out PlayerSnapshot snapshot)
    {
        snapshot = _snapshot;
        if (!_hasSnapshot)
            return false;
        _hasSnapshot = false;
        return true;
    }

    internal void Start(SessionRole role, string address, int port, string localName, uint buildId)
    {
        _role = role;
        _localName = Protocol.NormalizePlayerName(localName);
        _buildId = buildId;
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
            ResetPeerState();
            _log.LogWarning("Network: peer timed out");
            if (_role == SessionRole.Host)
                _remote = null;
        }

        if (now < _nextSend)
            return;

        _nextSend = now + 1f;
        if (_role == SessionRole.Client && !_connected)
            SendIdentity(PacketType.Hello);
        else if (_remote != null)
            SendSceneState();
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
        if (!Protocol.TryDecode(received.Buffer, out var type, out var sequence))
            return;

        if (_role == SessionRole.Host && _remote == null)
        {
            if (type != PacketType.Hello ||
                !Protocol.TryDecodeIdentity(
                    received.Buffer, PacketType.Hello, out _, out var remoteBuildId, out var remoteName))
                return;
            if (remoteBuildId != _buildId)
            {
                if (_lastRejectedBuildId != remoteBuildId)
                    _log.LogWarning($"Network: rejected incompatible build {remoteBuildId:X8}; expected {_buildId:X8}");
                _lastRejectedBuildId = remoteBuildId;
                return;
            }
            _remoteName = remoteName;
            _remote = received.RemoteEndPoint;
        }

        if (_remote == null || !received.RemoteEndPoint.Equals(_remote))
            return;

        if (type == PacketType.PlayerSnapshot)
        {
            if (_connected && Protocol.TryDecodeSnapshot(received.Buffer, out _, out var snapshot))
            {
                _lastReceive = now;
                if (IsNewer(sequence, _lastSnapshotSequence))
                {
                    _lastSnapshotSequence = sequence;
                    _snapshot = snapshot;
                    _hasSnapshot = true;
                }
            }
            return;
        }

        if (type == PacketType.SceneState)
        {
            if (_connected &&
                Protocol.TryDecodeSceneState(received.Buffer, out var sceneSequence, out var sceneId))
            {
                _lastReceive = now;
                if (IsNewer(sceneSequence, _lastSceneSequence))
                {
                    _lastSceneSequence = sceneSequence;
                    if (!_hasRemoteScene || _remoteSceneId != sceneId)
                        _log.LogInfo($"Network: peer scene {sceneId:X8}");
                    _remoteSceneId = sceneId;
                    _hasRemoteScene = true;
                }
            }
            return;
        }

        if (type == PacketType.Heartbeat)
        {
            if (_connected)
                _lastReceive = now;
            return;
        }

        if (type == PacketType.Disconnect)
        {
            ResetPeerState();
            _log.LogInfo("Network: peer disconnected");
            if (_role == SessionRole.Host)
                _remote = null;
            return;
        }

        if (_role == SessionRole.Host && type == PacketType.Hello)
        {
            if (!Protocol.TryDecodeIdentity(
                    received.Buffer, PacketType.Hello, out _, out var remoteBuildId, out _remoteName) ||
                remoteBuildId != _buildId)
                return;
            if (!_connected)
                _log.LogInfo($"Network: {_remoteName} connected from {_remote}");
            _connected = true;
            _lastReceive = now;
            SendIdentity(PacketType.HelloAck);
            SendSceneState();
        }
        else if (_role == SessionRole.Client && type == PacketType.HelloAck)
        {
            if (!Protocol.TryDecodeIdentity(
                    received.Buffer, PacketType.HelloAck, out _, out var remoteBuildId, out _remoteName) ||
                remoteBuildId != _buildId)
                return;
            if (!_connected)
                _log.LogInfo($"Network: connected to {_remoteName}");
            _connected = true;
            _lastReceive = now;
            SendSceneState();
        }
    }

    private void Send(PacketType type)
    {
        Send(Protocol.Encode(type, ++_sequence));
    }

    private void SendIdentity(PacketType type)
    {
        Send(Protocol.EncodeIdentity(type, ++_sequence, _buildId, _localName));
    }

    private void SendSceneState() =>
        Send(Protocol.EncodeSceneState(++_sequence, _localSceneId));

    private void Send(byte[] packet)
    {
        if (_udp == null || _remote == null)
            return;
        try
        {
            _udp.Send(packet, packet.Length, _remote);
        }
        catch (Exception exception)
        {
            _log.LogWarning($"Network send failed: {exception.Message}");
        }
    }

    private static bool IsNewer(uint sequence, uint previous) =>
        unchecked((int)(sequence - previous)) > 0;

    private void ResetPeerState()
    {
        _connected = false;
        _remoteName = "Diver";
        _remoteSceneId = 0;
        _hasRemoteScene = false;
        _lastSceneSequence = 0;
        _snapshot = default;
        _hasSnapshot = false;
        _lastSnapshotSequence = 0;
    }

    public void Dispose()
    {
        if (_connected)
            Send(PacketType.Disconnect);
        _stop.Cancel();
        _udp?.Dispose();
        _udp = null;
        ResetPeerState();
    }
}
