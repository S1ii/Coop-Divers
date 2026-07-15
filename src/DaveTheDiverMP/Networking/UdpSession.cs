using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
    private sealed class PendingReliable
    {
        internal byte[] Packet;
        internal float NextSend;
    }

    private readonly ManualLogSource _log;
    private readonly ConcurrentQueue<UdpReceiveResult> _incoming = new();
    private readonly ConcurrentQueue<FishSnapshot> _fishSnapshots = new();
    private readonly ConcurrentQueue<FishDamageRequest> _fishDamageRequests = new();
    private readonly ConcurrentQueue<FishPickupRequest> _fishPickupRequests = new();
    private readonly ConcurrentQueue<FishRemoved> _fishRemovals = new();
    private readonly ConcurrentQueue<FishManifest> _fishManifests = new();
    private readonly ConcurrentQueue<FishManifestState> _fishManifestStates = new();
    private readonly ConcurrentQueue<PlayerVisualState> _playerVisualStates = new();
    private readonly ConcurrentQueue<ProjectileVisualState> _projectileVisualStates = new();
    private readonly ConcurrentQueue<PickupRemoved> _pickupRemovals = new();
    private readonly ConcurrentQueue<PickupRemoved> _pickupRequests = new();
    private readonly ConcurrentQueue<SceneTransitionCommand> _sceneTransitions = new();
    private readonly ConcurrentQueue<IngredientsSyncRequest> _ingredientsSyncRequests = new();
    private readonly ConcurrentQueue<IngredientsSnapshotChunk> _ingredientsSnapshotChunks = new();
    private readonly ConcurrentQueue<IngredientsDelta> _ingredientsDeltas = new();
    private readonly ConcurrentQueue<RoomReady> _roomReady = new();
    private readonly ConcurrentQueue<RoomState> _roomStates = new();
    private readonly ConcurrentQueue<DiveReady> _diveReady = new();
    private readonly ConcurrentQueue<DiveState> _diveStates = new();
    private readonly ConcurrentQueue<DiverLifeState> _diverLifeStates = new();
    private readonly ConcurrentQueue<DiveExitRequest> _diveExitRequests = new();
    private readonly ConcurrentQueue<BoatDecoState> _boatDecoStates = new();
    private readonly ConcurrentQueue<TravelReady> _travelReady = new();
    private readonly ConcurrentQueue<TravelState> _travelStates = new();
    private readonly ConcurrentQueue<DiveLootRequest> _diveLootRequests = new();
    private readonly ConcurrentQueue<DiveResultEntry> _diveResultEntries = new();
    private readonly ConcurrentQueue<DiveResultState> _diveResultStates = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<uint, PendingReliable> _pendingReliable = new();
    private readonly HashSet<uint> _receivedReliable = new();
    private readonly Queue<uint> _receivedReliableOrder = new();
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
    private float _lastSnapshotReceive;
    private float _nextSend;
    private float _now;
    private bool _connected;
    private bool _hasRemoteScene;
    private bool _hasSnapshot;
    private bool _hasRemotePlayerState;
    private volatile bool _receiveFailed;
    private string _peerLostReason = string.Empty;
    private uint _lastSceneSequence;
    private uint _lastFishSequence;
    private uint _lastSnapshotSequence;
    private uint _lastVisualSequence;
    private uint _lastProjectileVisualSequence;
    private PlayerSnapshot _snapshot;

    internal UdpSession(ManualLogSource log) => _log = log;

    internal bool Connected => _connected;
    internal bool IsRunning => _udp != null && !_receiveFailed;
    internal string RemoteName => _remoteName;
    internal int LocalPort => ((IPEndPoint)_udp.Client.LocalEndPoint).Port;
    internal int PendingReliableCount => _pendingReliable.Count;
    internal int ReliableCapacityRemaining => Math.Max(0, 256 - _pendingReliable.Count);
    internal bool SceneMatches(uint sceneId) =>
        _connected && _hasRemoteScene && _remoteSceneId == sceneId;

    internal bool TryTakePeerLoss(out string reason)
    {
        reason = _peerLostReason;
        _peerLostReason = string.Empty;
        return !string.IsNullOrEmpty(reason);
    }

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

    internal void SendPlayerVisualState(PlayerVisualState state)
    {
        if (_connected && SceneMatches(state.SceneId))
            Send(Protocol.EncodePlayerVisualState(++_sequence, state));
    }

    internal bool TryTakePlayerVisualState(out PlayerVisualState state) =>
        _playerVisualStates.TryDequeue(out state);

    internal void SendProjectileVisualState(ProjectileVisualState state)
    {
        if (_connected && SceneMatches(state.SceneId))
            Send(Protocol.EncodeProjectileVisualState(++_sequence, state));
    }

    internal bool TryTakeProjectileVisualState(out ProjectileVisualState state) =>
        _projectileVisualStates.TryDequeue(out state);

    internal void SendFishSnapshot(FishSnapshot snapshot)
    {
        if (_role == SessionRole.Host && SceneMatches(snapshot.SceneId))
            Send(Protocol.EncodeFishSnapshot(++_sequence, snapshot));
    }

    internal bool TryTakeFishSnapshot(out FishSnapshot snapshot) =>
        _fishSnapshots.TryDequeue(out snapshot);

    internal void SendFishDamageRequest(FishDamageRequest request)
    {
        if (_role == SessionRole.Client && SceneMatches(request.SceneId))
            SendReliable(Protocol.EncodeFishDamageRequest(++_sequence, request));
    }

    internal bool TryTakeFishDamageRequest(out FishDamageRequest request) =>
        _fishDamageRequests.TryDequeue(out request);

    internal void SendFishPickupRequest(FishPickupRequest request)
    {
        if (_role == SessionRole.Client && SceneMatches(request.SceneId))
            SendReliable(Protocol.EncodeFishPickupRequest(++_sequence, request));
    }

    internal bool TryTakeFishPickupRequest(out FishPickupRequest request) =>
        _fishPickupRequests.TryDequeue(out request);

    internal void SendFishRemoved(FishRemoved removed)
    {
        if (_role == SessionRole.Host && SceneMatches(removed.SceneId))
            SendReliable(Protocol.EncodeFishRemoved(++_sequence, removed));
    }

    internal bool TryTakeFishRemoved(out FishRemoved removed) =>
        _fishRemovals.TryDequeue(out removed);

    internal bool SendFishManifest(FishManifest manifest)
    {
        if (_role != SessionRole.Host || !SceneMatches(manifest.SceneId) ||
            ReliableCapacityRemaining == 0)
            return false;
        SendReliable(Protocol.EncodeFishManifest(++_sequence, manifest));
        return true;
    }

    internal bool TryTakeFishManifest(out FishManifest manifest) =>
        _fishManifests.TryDequeue(out manifest);

    internal bool SendFishManifestState(FishManifestState state)
    {
        if (_role != SessionRole.Host || !SceneMatches(state.SceneId) ||
            ReliableCapacityRemaining == 0)
            return false;
        SendReliable(Protocol.EncodeFishManifestState(++_sequence, state));
        return true;
    }

    internal bool TryTakeFishManifestState(out FishManifestState state) =>
        _fishManifestStates.TryDequeue(out state);

    internal void SendPickupRemoved(PickupRemoved removed)
    {
        if (_role == SessionRole.Host && SceneMatches(removed.SceneId))
            SendReliable(Protocol.EncodePickupRemoved(++_sequence, removed));
    }

    internal bool TryTakePickupRemoved(out PickupRemoved removed) =>
        _pickupRemovals.TryDequeue(out removed);

    internal void SendPickupRequest(PickupRemoved request)
    {
        if (_role == SessionRole.Client && SceneMatches(request.SceneId))
            SendReliable(Protocol.EncodePickupRequest(++_sequence, request));
    }

    internal bool TryTakePickupRequest(out PickupRemoved request) =>
        _pickupRequests.TryDequeue(out request);

    internal void SendSceneTransition(SceneTransitionCommand command)
    {
        if (_role == SessionRole.Host && _connected)
            SendReliable(Protocol.EncodeSceneTransition(++_sequence, command));
    }

    internal bool TryTakeSceneTransition(out SceneTransitionCommand command) =>
        _sceneTransitions.TryDequeue(out command);

    internal void SendIngredientsSyncRequest(IngredientsSyncRequest request)
    {
        if (_role == SessionRole.Client && _connected)
            SendReliable(Protocol.EncodeIngredientsSyncRequest(++_sequence, request));
    }

    internal bool TryTakeIngredientsSyncRequest(out IngredientsSyncRequest request) =>
        _ingredientsSyncRequests.TryDequeue(out request);

    internal void SendIngredientsSnapshotChunk(IngredientsSnapshotChunk chunk)
    {
        if (_role == SessionRole.Host && _connected)
            SendReliable(Protocol.EncodeIngredientsSnapshotChunk(++_sequence, chunk));
    }

    internal bool TryTakeIngredientsSnapshotChunk(out IngredientsSnapshotChunk chunk) =>
        _ingredientsSnapshotChunks.TryDequeue(out chunk);

    internal void SendIngredientsDelta(IngredientsDelta delta)
    {
        if (_role == SessionRole.Host && _connected)
            SendReliable(Protocol.EncodeIngredientsDelta(++_sequence, delta));
    }

    internal bool TryTakeIngredientsDelta(out IngredientsDelta delta) =>
        _ingredientsDeltas.TryDequeue(out delta);

    internal void SendRoomReady(RoomReady ready)
    {
        if (_role == SessionRole.Client && _connected)
            SendReliable(Protocol.EncodeRoomReady(++_sequence, ready));
    }

    internal bool TryTakeRoomReady(out RoomReady ready) => _roomReady.TryDequeue(out ready);

    internal void SendRoomState(RoomState state)
    {
        if (_role == SessionRole.Host && _connected)
            SendReliable(Protocol.EncodeRoomState(++_sequence, state));
    }

    internal bool TryTakeRoomState(out RoomState state) => _roomStates.TryDequeue(out state);

    internal void SendDiveReady(DiveReady ready)
    {
        if (_role == SessionRole.Client && _connected)
            SendReliable(Protocol.EncodeDiveReady(++_sequence, ready));
    }

    internal bool TryTakeDiveReady(out DiveReady ready) => _diveReady.TryDequeue(out ready);

    internal void SendDiveState(DiveState state)
    {
        if (_role == SessionRole.Host && _connected)
            SendReliable(Protocol.EncodeDiveState(++_sequence, state));
    }

    internal bool TryTakeDiveState(out DiveState state) => _diveStates.TryDequeue(out state);

    internal void SendDiverLifeState(DiverLifeState state)
    {
        if (_role == SessionRole.Client && _connected)
            SendReliable(Protocol.EncodeDiverLifeState(++_sequence, state));
    }

    internal bool TryTakeDiverLifeState(out DiverLifeState state) =>
        _diverLifeStates.TryDequeue(out state);

    internal void SendDiveExitRequest(DiveExitRequest request)
    {
        if (_role == SessionRole.Client && _connected)
            SendReliable(Protocol.EncodeDiveExitRequest(++_sequence, request));
    }

    internal bool TryTakeDiveExitRequest(out DiveExitRequest request) =>
        _diveExitRequests.TryDequeue(out request);

    internal void SendBoatDecoState(BoatDecoState state)
    {
        if ((_role is SessionRole.Host or SessionRole.Client) && _connected)
            SendReliable(Protocol.EncodeBoatDecoState(++_sequence, state));
    }

    internal bool TryTakeBoatDecoState(out BoatDecoState state) =>
        _boatDecoStates.TryDequeue(out state);

    internal void SendTravelReady(TravelReady ready)
    {
        if (_role == SessionRole.Client && _connected)
            SendReliable(Protocol.EncodeTravelReady(++_sequence, ready));
    }

    internal bool TryTakeTravelReady(out TravelReady ready) =>
        _travelReady.TryDequeue(out ready);

    internal void SendTravelState(TravelState state)
    {
        if (_role == SessionRole.Host && _connected)
            SendReliable(Protocol.EncodeTravelState(++_sequence, state));
    }

    internal bool TryTakeTravelState(out TravelState state) =>
        _travelStates.TryDequeue(out state);

    internal void SendDiveLootRequest(DiveLootRequest request)
    {
        if (_role == SessionRole.Client && _connected)
            SendReliable(Protocol.EncodeDiveLootRequest(++_sequence, request));
    }

    internal bool TryTakeDiveLootRequest(out DiveLootRequest request) =>
        _diveLootRequests.TryDequeue(out request);

    internal void SendDiveResultEntry(DiveResultEntry entry)
    {
        if (_role == SessionRole.Host && _connected)
            SendReliable(Protocol.EncodeDiveResultEntry(++_sequence, entry));
    }

    internal bool TryTakeDiveResultEntry(out DiveResultEntry entry) =>
        _diveResultEntries.TryDequeue(out entry);

    internal void SendDiveResultState(DiveResultState state)
    {
        if (_role == SessionRole.Host && _connected)
            SendReliable(Protocol.EncodeDiveResultState(++_sequence, state));
    }

    internal bool TryTakeDiveResultState(out DiveResultState state) =>
        _diveResultStates.TryDequeue(out state);

    internal bool TryTakeSnapshot(out PlayerSnapshot snapshot)
    {
        snapshot = _snapshot;
        if (!_hasSnapshot)
            return false;
        _hasSnapshot = false;
        return true;
    }

    internal bool TryGetRemotePlayerSnapshot(out PlayerSnapshot snapshot)
    {
        snapshot = _snapshot;
        return _connected && _hasRemotePlayerState;
    }

    internal bool TryGetFreshRemotePlayerSnapshot(
        float now,
        float maxAge,
        out PlayerSnapshot snapshot)
    {
        snapshot = _snapshot;
        return _connected && _hasRemotePlayerState &&
            now - _lastSnapshotReceive >= 0f && now - _lastSnapshotReceive <= maxAge;
    }

    internal void Start(SessionRole role, string address, int port, string localName, uint buildId)
    {
        _receiveFailed = false;
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
        _now = now;
        if (_receiveFailed)
        {
            SignalPeerLoss("connection failed");
            _udp?.Dispose();
            _udp = null;
            _receiveFailed = false;
            ResetPeerState();
            _log.LogWarning("Network: session stopped after a receive failure");
            return;
        }
        if (_udp == null)
            return;

        while (_incoming.TryDequeue(out var received))
            Handle(received, now);

        RetryReliable(now);

        if (_connected && now - _lastReceive > 5f)
        {
            SignalPeerLoss("host timed out");
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
            {
                try
                {
                    _incoming.Enqueue(await _udp.ReceiveAsync(_stop.Token));
                }
                catch (SocketException exception) when (
                    exception.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionRefused)
                {
                    // UDP can surface an ICMP "port unreachable" while the peer is starting.
                }
            }
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
        finally
        {
            if (!_stop.IsCancellationRequested)
                _receiveFailed = true;
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

        if (type == PacketType.Ack)
        {
            if (_connected && _pendingReliable.Remove(sequence))
                _lastReceive = now;
            return;
        }

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
                    _hasRemotePlayerState = true;
                    _lastSnapshotReceive = now;
                }
            }
            return;
        }

        if (type == PacketType.PlayerVisualState)
        {
            if (_connected && Protocol.TryDecodePlayerVisualState(received.Buffer, out _, out var state) &&
                IsNewer(sequence, _lastVisualSequence))
            {
                _lastVisualSequence = sequence;
                _lastReceive = now;
                _playerVisualStates.Enqueue(state);
            }
            return;
        }

        if (type == PacketType.ProjectileVisualState)
        {
            if (_connected && Protocol.TryDecodeProjectileVisualState(received.Buffer, out _, out var state) &&
                IsNewer(sequence, _lastProjectileVisualSequence))
            {
                _lastProjectileVisualSequence = sequence;
                _lastReceive = now;
                _projectileVisualStates.Enqueue(state);
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

        if (type == PacketType.FishSnapshot)
        {
            if (_connected && _role == SessionRole.Client &&
                Protocol.TryDecodeFishSnapshot(received.Buffer, out _, out var fishSnapshot))
            {
                _lastReceive = now;
                if (IsNewer(sequence, _lastFishSequence))
                {
                    _lastFishSequence = sequence;
                    _fishSnapshots.Enqueue(fishSnapshot);
                }
            }
            return;
        }

        if (type == PacketType.FishDamageRequest)
        {
            if (_connected && _role == SessionRole.Host &&
                Protocol.TryDecodeFishDamageRequest(received.Buffer, out _, out var request))
            {
                _lastReceive = now;
                if (AcceptReliable(sequence))
                {
                    _fishDamageRequests.Enqueue(request);
                }
            }
            return;
        }

        if (type == PacketType.FishPickupRequest)
        {
            if (_connected && _role == SessionRole.Host &&
                Protocol.TryDecodeFishPickupRequest(received.Buffer, out _, out var request))
            {
                _lastReceive = now;
                if (AcceptReliable(sequence))
                {
                    _fishPickupRequests.Enqueue(request);
                }
            }
            return;
        }

        if (type == PacketType.FishRemoved)
        {
            if (_connected && _role == SessionRole.Client &&
                Protocol.TryDecodeFishRemoved(received.Buffer, out _, out var removed))
            {
                _lastReceive = now;
                if (AcceptReliable(sequence))
                    _fishRemovals.Enqueue(removed);
            }
            return;
        }

        if (type == PacketType.FishManifest)
        {
            if (_connected && _role == SessionRole.Client &&
                Protocol.TryDecodeFishManifest(received.Buffer, out _, out var manifest))
            {
                _lastReceive = now;
                if (AcceptReliable(sequence))
                    _fishManifests.Enqueue(manifest);
            }
            return;
        }

        if (type == PacketType.FishManifestState)
        {
            if (_connected && _role == SessionRole.Client &&
                Protocol.TryDecodeFishManifestState(received.Buffer, out _, out var state))
            {
                _lastReceive = now;
                if (AcceptReliable(sequence))
                    _fishManifestStates.Enqueue(state);
            }
            return;
        }

        if (type == PacketType.PickupRemoved)
        {
            if (_connected && _role == SessionRole.Client &&
                Protocol.TryDecodePickupRemoved(received.Buffer, out _, out var removed))
            {
                _lastReceive = now;
                if (AcceptReliable(sequence))
                {
                    _pickupRemovals.Enqueue(removed);
                }
            }
            return;
        }

        if (type == PacketType.PickupRequest)
        {
            if (_connected && _role == SessionRole.Host &&
                Protocol.TryDecodePickupRequest(received.Buffer, out _, out var request))
            {
                _lastReceive = now;
                if (AcceptReliable(sequence))
                {
                    _pickupRequests.Enqueue(request);
                }
            }
            return;
        }

        if (type == PacketType.SceneTransition)
        {
            if (_connected && _role == SessionRole.Client &&
                Protocol.TryDecodeSceneTransition(received.Buffer, out _, out var command))
            {
                _lastReceive = now;
                if (AcceptReliable(sequence))
                {
                    _sceneTransitions.Enqueue(command);
                }
            }
            return;
        }

        if (type == PacketType.IngredientsSyncRequest)
        {
            if (_connected && _role == SessionRole.Host &&
                Protocol.TryDecodeIngredientsSyncRequest(received.Buffer, out _, out var request))
            {
                _lastReceive = now;
                if (AcceptReliable(sequence))
                    _ingredientsSyncRequests.Enqueue(request);
            }
            return;
        }

        if (type == PacketType.IngredientsSnapshotChunk)
        {
            if (_connected && _role == SessionRole.Client &&
                Protocol.TryDecodeIngredientsSnapshotChunk(received.Buffer, out _, out var chunk))
            {
                _lastReceive = now;
                if (AcceptReliable(sequence))
                    _ingredientsSnapshotChunks.Enqueue(chunk);
            }
            return;
        }

        if (type == PacketType.IngredientsDelta)
        {
            if (_connected && _role == SessionRole.Client &&
                Protocol.TryDecodeIngredientsDelta(received.Buffer, out _, out var delta))
            {
                _lastReceive = now;
                if (AcceptReliable(sequence))
                    _ingredientsDeltas.Enqueue(delta);
            }
            return;
        }

        if (type == PacketType.RoomReady)
        {
            if (_connected && _role == SessionRole.Host &&
                Protocol.TryDecodeRoomReady(received.Buffer, out _, out var ready))
            {
                _lastReceive = now;
                if (AcceptReliable(sequence))
                    _roomReady.Enqueue(ready);
            }
            return;
        }

        if (type == PacketType.RoomState)
        {
            if (_connected && _role == SessionRole.Client &&
                Protocol.TryDecodeRoomState(received.Buffer, out _, out var state))
            {
                _lastReceive = now;
                if (AcceptReliable(sequence))
                    _roomStates.Enqueue(state);
            }
            return;
        }

        if (type == PacketType.DiveReady)
        {
            if (_connected && _role == SessionRole.Host &&
                Protocol.TryDecodeDiveReady(received.Buffer, out _, out var ready))
            {
                _lastReceive = now;
                if (AcceptReliable(sequence))
                    _diveReady.Enqueue(ready);
            }
            return;
        }

        if (type == PacketType.DiveState)
        {
            if (_connected && _role == SessionRole.Client &&
                Protocol.TryDecodeDiveState(received.Buffer, out _, out var state))
            {
                _lastReceive = now;
                if (AcceptReliable(sequence))
                    _diveStates.Enqueue(state);
            }
            return;
        }

        if (type == PacketType.DiverLifeState)
        {
            if (_connected && _role == SessionRole.Host &&
                Protocol.TryDecodeDiverLifeState(received.Buffer, out _, out var state))
            {
                _lastReceive = now;
                if (AcceptReliable(sequence))
                    _diverLifeStates.Enqueue(state);
            }
            return;
        }

        if (type == PacketType.DiveExitRequest)
        {
            if (_connected && _role == SessionRole.Host &&
                Protocol.TryDecodeDiveExitRequest(received.Buffer, out _, out var request))
            {
                _lastReceive = now;
                if (AcceptReliable(sequence))
                    _diveExitRequests.Enqueue(request);
            }
            return;
        }

        if (type == PacketType.BoatDecoState)
        {
            if (_connected && (_role is SessionRole.Host or SessionRole.Client) &&
                Protocol.TryDecodeBoatDecoState(received.Buffer, out _, out var state))
            {
                _lastReceive = now;
                if (state.Id >= 0 && AcceptReliable(sequence))
                    _boatDecoStates.Enqueue(state);
            }
            return;
        }

        if (type == PacketType.TravelReady)
        {
            if (_connected && _role == SessionRole.Host &&
                Protocol.TryDecodeTravelReady(received.Buffer, out _, out var ready))
            {
                _lastReceive = now;
                if (AcceptReliable(sequence))
                    _travelReady.Enqueue(ready);
            }
            return;
        }

        if (type == PacketType.TravelState)
        {
            if (_connected && _role == SessionRole.Client &&
                Protocol.TryDecodeTravelState(received.Buffer, out _, out var state))
            {
                _lastReceive = now;
                if (AcceptReliable(sequence))
                    _travelStates.Enqueue(state);
            }
            return;
        }

        if (type == PacketType.DiveLootRequest)
        {
            if (_connected && _role == SessionRole.Host &&
                Protocol.TryDecodeDiveLootRequest(received.Buffer, out _, out var request))
            {
                _lastReceive = now;
                if (AcceptReliable(sequence))
                    _diveLootRequests.Enqueue(request);
            }
            return;
        }

        if (type == PacketType.DiveResultEntry)
        {
            if (_connected && _role == SessionRole.Client &&
                Protocol.TryDecodeDiveResultEntry(received.Buffer, out _, out var entry))
            {
                _lastReceive = now;
                if (AcceptReliable(sequence))
                    _diveResultEntries.Enqueue(entry);
            }
            return;
        }

        if (type == PacketType.DiveResultState)
        {
            if (_connected && _role == SessionRole.Client &&
                Protocol.TryDecodeDiveResultState(received.Buffer, out _, out var state))
            {
                _lastReceive = now;
                if (AcceptReliable(sequence))
                    _diveResultStates.Enqueue(state);
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
            SignalPeerLoss("host disconnected");
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

    private void SendReliable(byte[] packet)
    {
        if (!Protocol.TryDecode(packet, out _, out var sequence) || _pendingReliable.Count >= 256)
        {
            _log.LogWarning("Network reliable queue is full");
            return;
        }
        _pendingReliable[sequence] = new PendingReliable
        {
            Packet = packet,
            NextSend = _now + 0.1f
        };
        Send(packet);
    }

    private void RetryReliable(float now)
    {
        if (!_connected)
            return;
        foreach (var pending in _pendingReliable.Values)
        {
            if (now < pending.NextSend)
                continue;
            Send(pending.Packet);
            pending.NextSend = now + 0.1f;
        }
    }

    private bool AcceptReliable(uint sequence)
    {
        Send(Protocol.Encode(PacketType.Ack, sequence));
        if (!_receivedReliable.Add(sequence))
            return false;
        _receivedReliableOrder.Enqueue(sequence);
        if (_receivedReliableOrder.Count > 1024)
            _receivedReliable.Remove(_receivedReliableOrder.Dequeue());
        return true;
    }

    private static bool IsNewer(uint sequence, uint previous) =>
        unchecked((int)(sequence - previous)) > 0;

    private void SignalPeerLoss(string reason)
    {
        if (_role == SessionRole.Client && _connected && string.IsNullOrEmpty(_peerLostReason))
            _peerLostReason = reason;
    }

    private void ResetPeerState()
    {
        _connected = false;
        _remoteName = "Diver";
        _remoteSceneId = 0;
        _hasRemoteScene = false;
        _lastSceneSequence = 0;
        _lastFishSequence = 0;
        while (_fishSnapshots.TryDequeue(out _))
        {
        }
        while (_playerVisualStates.TryDequeue(out _))
        {
        }
        while (_projectileVisualStates.TryDequeue(out _))
        {
        }
        while (_fishDamageRequests.TryDequeue(out _))
        {
        }
        while (_fishPickupRequests.TryDequeue(out _))
        {
        }
        while (_fishRemovals.TryDequeue(out _))
        {
        }
        while (_fishManifests.TryDequeue(out _))
        {
        }
        while (_fishManifestStates.TryDequeue(out _))
        {
        }
        while (_pickupRemovals.TryDequeue(out _))
        {
        }
        while (_pickupRequests.TryDequeue(out _))
        {
        }
        while (_sceneTransitions.TryDequeue(out _))
        {
        }
        while (_ingredientsSyncRequests.TryDequeue(out _))
        {
        }
        while (_ingredientsSnapshotChunks.TryDequeue(out _))
        {
        }
        while (_ingredientsDeltas.TryDequeue(out _))
        {
        }
        _snapshot = default;
        _lastSnapshotReceive = 0f;
        _hasSnapshot = false;
        _hasRemotePlayerState = false;
        _lastSnapshotSequence = 0;
        _lastVisualSequence = 0;
        _lastProjectileVisualSequence = 0;
        _pendingReliable.Clear();
        _receivedReliable.Clear();
        _receivedReliableOrder.Clear();
        while (_roomReady.TryDequeue(out _))
        {
        }
        while (_roomStates.TryDequeue(out _))
        {
        }
        while (_diveReady.TryDequeue(out _))
        {
        }
        while (_diveStates.TryDequeue(out _))
        {
        }
        while (_diverLifeStates.TryDequeue(out _))
        {
        }
        while (_diveExitRequests.TryDequeue(out _))
        {
        }
        while (_travelReady.TryDequeue(out _))
        {
        }
        while (_travelStates.TryDequeue(out _))
        {
        }
        while (_boatDecoStates.TryDequeue(out _))
        {
        }
        while (_diveLootRequests.TryDequeue(out _))
        {
        }
        while (_diveResultEntries.TryDequeue(out _))
        {
        }
        while (_diveResultStates.TryDequeue(out _))
        {
        }
    }

    public void Dispose()
    {
        if (_connected)
            Send(PacketType.Disconnect);
        _stop.Cancel();
        _udp?.Dispose();
        _udp = null;
        _receiveFailed = false;
        ResetPeerState();
    }
}
