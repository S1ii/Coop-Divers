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
    private sealed class ReliableReceiveWindow
    {
        private const int Size = 1024;
        private readonly ulong[] _seen = new ulong[Size / 64];
        private uint _newest;
        private bool _hasNewest;

        internal bool Accept(uint sequence)
        {
            if (!_hasNewest)
            {
                _newest = sequence;
                _hasNewest = true;
            }
            else
            {
                var delta = unchecked((int)(sequence - _newest));
                if (delta > 0)
                {
                    if (delta >= Size)
                        Array.Clear(_seen, 0, _seen.Length);
                    else
                        for (var offset = 1; offset <= delta; offset++)
                            Clear(_newest + (uint)offset);
                    _newest = sequence;
                }
                else if (delta == 0 || unchecked(_newest - sequence) >= Size)
                {
                    return false;
                }
            }

            var index = (int)(sequence & (Size - 1));
            var mask = 1UL << (index & 63);
            if ((_seen[index >> 6] & mask) != 0)
                return false;
            _seen[index >> 6] |= mask;
            return true;
        }

        internal void Clear()
        {
            Array.Clear(_seen, 0, _seen.Length);
            _hasNewest = false;
        }

        private void Clear(uint sequence)
        {
            var index = (int)(sequence & (Size - 1));
            _seen[index >> 6] &= ~(1UL << (index & 63));
        }
    }

    private sealed class PendingReliable
    {
        internal byte[] Packet;
        internal float NextSend;
        internal float RetryDelay;
    }

    private readonly ManualLogSource _log;
    private readonly ConcurrentQueue<UdpReceiveResult> _incoming = new();
    private readonly ConcurrentQueue<FishSnapshot> _fishSnapshots = new();
    private readonly ConcurrentQueue<FishDamageRequest> _fishDamageRequests = new();
    private readonly ConcurrentQueue<FishPickupRequest> _fishPickupRequests = new();
    private readonly ConcurrentQueue<FishPickupResult> _fishPickupResults = new();
    private readonly ConcurrentQueue<FishRemoved> _fishRemovals = new();
    private readonly ConcurrentQueue<FishManifest> _fishManifests = new();
    private readonly ConcurrentQueue<FishManifestState> _fishManifestStates = new();
    private readonly ConcurrentQueue<FishLifecycle> _fishLifecycles = new();
    private readonly ConcurrentQueue<FishActionRequest> _fishActionRequests = new();
    private readonly ConcurrentQueue<FishActionAck> _fishActionAcks = new();
    private readonly ConcurrentQueue<FishLootGrant> _fishLootGrants = new();
    private readonly ConcurrentQueue<FishLootComplete> _fishLootCompletions = new();
    private readonly ConcurrentQueue<FishHookPose> _fishHookPoses = new();
    private readonly ConcurrentQueue<PlayerVisualState> _playerVisualStates = new();
    private readonly Queue<int> _projectileVisualOrder = new();
    private readonly Dictionary<int, ProjectileVisualState> _projectileVisualStates = new();
    private readonly ConcurrentQueue<PickupRemoved> _pickupRemovals = new();
    private readonly ConcurrentQueue<PickupRemoved> _pickupRequests = new();
    private readonly ConcurrentQueue<SceneTransitionCommand> _sceneTransitions = new();
    private readonly ConcurrentQueue<SceneSeed> _sceneSeeds = new();
    private readonly ConcurrentQueue<CargoState> _cargoStates = new();
    private readonly ConcurrentQueue<IngredientsSyncRequest> _ingredientsSyncRequests = new();
    private readonly ConcurrentQueue<IngredientsSnapshotChunk> _ingredientsSnapshotChunks = new();
    private readonly ConcurrentQueue<IngredientsDelta> _ingredientsDeltas = new();
    private readonly ConcurrentQueue<RoomReady> _roomReady = new();
    private readonly ConcurrentQueue<RoomState> _roomStates = new();
    private readonly ConcurrentQueue<SaveSnapshotChunk> _saveSnapshotChunks = new();
    private readonly ConcurrentQueue<SaveSnapshotAck> _saveSnapshotAcks = new();
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
    private readonly ConcurrentQueue<MissionState> _missionStates = new();
    private readonly ConcurrentQueue<MissionRoster> _missionRosters = new();
    private readonly ConcurrentQueue<WorldFlagRequest> _worldFlagRequests = new();
    private readonly ConcurrentQueue<WorldFlagState> _worldFlagStates = new();
    private readonly ConcurrentQueue<BossDamageRequest> _bossDamageRequests = new();
    private readonly ConcurrentQueue<BossState> _bossStates = new();
    private readonly ConcurrentQueue<ManagerEvent> _managerEvents = new();
    private readonly ConcurrentQueue<SushiResultState> _sushiResultStates = new();
    private readonly ConcurrentQueue<NpcInteraction> _npcInteractions = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<uint, PendingReliable> _pendingReliable = new();
    private readonly Queue<byte[]> _reliableBacklog = new();
    private readonly ReliableReceiveWindow _receivedReliable = new();
    private UdpClient _udp;
    private IPEndPoint _remote;
    private SessionRole _role;
    private string _localName = "Diver";
    private string _remoteName = "Diver";
    private uint _buildId;
    private uint _localSceneId;
    private uint _localSceneEpoch;
    private uint _remoteSceneId;
    private uint _remoteSceneEpoch;
    private uint _lastRejectedBuildId;
    private uint _sequence;
    private ulong _sessionId;
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
    private uint _lastSnapshotSequence;
    private uint _lastVisualSequence;
    private uint _lastProjectileVisualSequence;
    private uint _lastTransitionSequence;
    private uint _lastBoatDecoSequence;
    private PlayerSnapshot _snapshot;
    private PlayerSnapshot _lastSentSnapshot;
    private PlayerVisualState _lastSentVisualState;
    private float _lastSnapshotSend;
    private float _lastVisualSend;
    private bool _hasSentSnapshot;
    private bool _hasSentVisualState;
    private long _sentBytes;
    private long _receivedBytes;
    private int _sentPackets;
    private int _receivedPackets;
    private float _nextTrafficLog;

    internal UdpSession(ManualLogSource log) => _log = log;

    internal static void SelfTest()
    {
        var window = new ReliableReceiveWindow();
        if (!window.Accept(uint.MaxValue - 1) || !window.Accept(0) ||
            !window.Accept(uint.MaxValue) || window.Accept(uint.MaxValue) ||
            !window.Accept(2) || !window.Accept(1))
            throw new InvalidOperationException("Reliable receive wrap/reorder self-test failed");

        window = new ReliableReceiveWindow();
        if (!window.Accept(1) || !window.Accept(1025) || window.Accept(1) ||
            !window.Accept(2) || window.Accept(2) || window.Accept(1025))
            throw new InvalidOperationException("Reliable receive replay self-test failed");

        if (!IsFishDirectionAllowed(SessionRole.Client, PacketType.FishLifecycle) ||
            !IsFishDirectionAllowed(SessionRole.Host, PacketType.FishActionRequest) ||
            !IsFishDirectionAllowed(SessionRole.Client, PacketType.FishActionAck) ||
            !IsFishDirectionAllowed(SessionRole.Client, PacketType.FishLootGrant) ||
            !IsFishDirectionAllowed(SessionRole.Host, PacketType.FishLootComplete) ||
            !IsFishDirectionAllowed(SessionRole.Host, PacketType.FishHookPose) ||
            IsFishDirectionAllowed(SessionRole.Host, PacketType.FishLifecycle) ||
            IsFishDirectionAllowed(SessionRole.Client, PacketType.FishActionRequest) ||
            IsFishDirectionAllowed(SessionRole.Host, PacketType.FishActionAck) ||
            IsFishDirectionAllowed(SessionRole.Client, PacketType.FishHookPose))
            throw new InvalidOperationException("Fish message direction self-test failed");

        TestProjectileVisualQueueLimit();
    }

    private static void TestProjectileVisualQueueLimit()
    {
        var session = new UdpSession(null);
        for (var id = 1; id <= 128; id++)
            session.EnqueueProjectileVisualState(default(ProjectileVisualState) with { Id = id });
        session.EnqueueProjectileVisualState(
            default(ProjectileVisualState) with { Id = 128, X = 42f });
        session.EnqueueProjectileVisualState(default(ProjectileVisualState) with { Id = 129 });
        if (session._projectileVisualStates.Count != 128 ||
            !session.TryTakeProjectileVisualState(out var first) || first.Id != 2)
            throw new InvalidOperationException("Projectile visual queue limit self-test failed");
        var foundCoalesced = false;
        while (session.TryTakeProjectileVisualState(out var state))
            if (state.Id == 128 && state.X != 42f)
                throw new InvalidOperationException("Projectile visual coalescing self-test failed");
            else if (state.Id == 128)
                foundCoalesced = true;
        if (!foundCoalesced)
            throw new InvalidOperationException("Projectile visual coalescing self-test failed");
    }

    internal bool Connected => _connected;
    internal bool IsRunning => _udp != null && !_receiveFailed;
    internal string RemoteName => _remoteName;
    internal uint LocalSceneEpoch => _localSceneEpoch;
    internal uint RemoteSceneEpoch => _remoteSceneEpoch;
    internal ulong ConnectionId => _connected ? _sessionId : 0;
    internal int ReliableCapacityRemaining => _reliableBacklog.Count == 0
        ? Math.Max(0, 256 - _pendingReliable.Count)
        : 0;
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
        _localSceneId = sceneId;
        _localSceneEpoch = _localSceneEpoch == uint.MaxValue ? 1 : _localSceneEpoch + 1;
        if (_connected)
            SendSceneState();
    }

    internal void SendSnapshot(PlayerSnapshot snapshot)
    {
        if (!_connected || (_hasSentSnapshot && _now < _lastSnapshotSend + 0.5f &&
            !PlayerSnapshotChanged(_lastSentSnapshot, snapshot)))
            return;
        Send(Protocol.EncodeSnapshot(++_sequence, snapshot));
        _lastSentSnapshot = snapshot;
        _lastSnapshotSend = _now;
        _hasSentSnapshot = true;
    }

    internal void SendPlayerVisualState(PlayerVisualState state)
    {
        if (!_connected || !SceneMatches(state.SceneId) ||
            (_hasSentVisualState && _now < _lastVisualSend + 1f &&
             SameVisualState(_lastSentVisualState, state)))
            return;
        Send(Protocol.EncodePlayerVisualState(++_sequence, state));
        _lastSentVisualState = state;
        _lastVisualSend = _now;
        _hasSentVisualState = true;
    }

    internal bool TryTakePlayerVisualState(out PlayerVisualState state) =>
        _playerVisualStates.TryDequeue(out state);

    internal void SendProjectileVisualState(ProjectileVisualState state)
    {
        if (_connected && SceneMatches(state.SceneId))
            Send(Protocol.EncodeProjectileVisualState(++_sequence, state));
    }

    internal bool TryTakeProjectileVisualState(out ProjectileVisualState state)
    {
        if (_projectileVisualOrder.Count > 0)
        {
            var id = _projectileVisualOrder.Dequeue();
            if (_projectileVisualStates.Remove(id, out state))
                return true;
        }
        state = default;
        return false;
    }

    private void EnqueueProjectileVisualState(ProjectileVisualState state)
    {
        if (_projectileVisualStates.ContainsKey(state.Id))
        {
            _projectileVisualStates[state.Id] = state;
            return;
        }
        if (_projectileVisualStates.Count >= 128)
            _projectileVisualStates.Remove(_projectileVisualOrder.Dequeue());
        _projectileVisualOrder.Enqueue(state.Id);
        _projectileVisualStates.Add(state.Id, state);
    }

    internal int SendFishSnapshots(
        uint sceneId,
        uint tick,
        IReadOnlyList<FishSnapshot> snapshots)
    {
        if (_role != SessionRole.Host || !SceneMatches(sceneId) || snapshots == null)
            return 0;
        var bytes = 0;
        for (var offset = 0; offset < snapshots.Count; offset += Protocol.MaxFishSnapshotsPerPacket)
        {
            var count = Math.Min(Protocol.MaxFishSnapshotsPerPacket, snapshots.Count - offset);
            var packet = Protocol.EncodeFishSnapshotBatch(
                ++_sequence, sceneId, _localSceneEpoch, tick, snapshots, offset, count);
            Send(packet);
            bytes += packet.Length;
        }
        return bytes;
    }

    internal bool TryTakeFishSnapshot(out FishSnapshot snapshot) =>
        _fishSnapshots.TryDequeue(out snapshot);

    internal void SendFishDamageRequest(FishDamageRequest request)
    {
        if (_role == SessionRole.Client && SceneMatches(request.SceneId) &&
            request.SceneEpoch == _remoteSceneEpoch)
            SendReliable(Protocol.EncodeFishDamageRequest(++_sequence, request));
    }

    internal bool TryTakeFishDamageRequest(out FishDamageRequest request) =>
        _fishDamageRequests.TryDequeue(out request);

    internal void SendFishPickupRequest(FishPickupRequest request)
    {
        if (_role == SessionRole.Client && SceneMatches(request.SceneId) &&
            request.SceneEpoch == _remoteSceneEpoch)
            SendReliable(Protocol.EncodeFishPickupRequest(++_sequence, request));
    }

    internal bool TryTakeFishPickupRequest(out FishPickupRequest request) =>
        _fishPickupRequests.TryDequeue(out request);

    internal void SendFishPickupResult(FishPickupResult result)
    {
        if (_role == SessionRole.Host && SceneMatches(result.SceneId) &&
            result.SceneEpoch == _localSceneEpoch)
            SendReliable(Protocol.EncodeFishPickupResult(++_sequence, result));
    }

    internal bool TryTakeFishPickupResult(out FishPickupResult result) =>
        _fishPickupResults.TryDequeue(out result);

    internal void SendFishRemoved(FishRemoved removed)
    {
        if (_role == SessionRole.Host && SceneMatches(removed.SceneId) &&
            removed.SceneEpoch == _localSceneEpoch)
            SendReliable(Protocol.EncodeFishRemoved(++_sequence, removed));
    }

    internal bool TryTakeFishRemoved(out FishRemoved removed) =>
        _fishRemovals.TryDequeue(out removed);

    internal bool SendFishManifest(FishManifest manifest)
    {
        if (_role != SessionRole.Host || !SceneMatches(manifest.SceneId) ||
            manifest.SceneEpoch != _localSceneEpoch ||
            ReliableCapacityRemaining == 0)
            return false;
        return SendReliable(Protocol.EncodeFishManifest(++_sequence, manifest));
    }

    internal bool TryTakeFishManifest(out FishManifest manifest) =>
        _fishManifests.TryDequeue(out manifest);

    internal bool SendFishManifestState(FishManifestState state)
    {
        if (_role != SessionRole.Host || !SceneMatches(state.SceneId) ||
            state.SceneEpoch != _localSceneEpoch ||
            ReliableCapacityRemaining == 0)
            return false;
        return SendReliable(Protocol.EncodeFishManifestState(++_sequence, state));
    }

    internal bool TryTakeFishManifestState(out FishManifestState state) =>
        _fishManifestStates.TryDequeue(out state);

    internal bool SendFishLifecycle(FishLifecycle state)
    {
        return _role == SessionRole.Host && SceneMatches(state.SceneId) &&
            state.SceneEpoch == _localSceneEpoch &&
            SendReliable(Protocol.EncodeFishLifecycle(++_sequence, state));
    }

    internal bool TryTakeFishLifecycle(out FishLifecycle state) =>
        _fishLifecycles.TryDequeue(out state);

    internal bool SendFishActionRequest(FishActionRequest request)
    {
        return _role == SessionRole.Client && SceneMatches(request.SceneId) &&
            request.SceneEpoch == _remoteSceneEpoch &&
            SendReliable(Protocol.EncodeFishActionRequest(++_sequence, request));
    }

    internal bool TryTakeFishActionRequest(out FishActionRequest request) =>
        _fishActionRequests.TryDequeue(out request);

    internal bool SendFishActionAck(FishActionAck ack)
    {
        return _role == SessionRole.Host && SceneMatches(ack.SceneId) &&
            ack.SceneEpoch == _localSceneEpoch &&
            SendReliable(Protocol.EncodeFishActionAck(++_sequence, ack));
    }

    internal bool TryTakeFishActionAck(out FishActionAck ack) =>
        _fishActionAcks.TryDequeue(out ack);

    internal bool SendFishLootGrant(FishLootGrant grant) =>
        _role == SessionRole.Host && SceneMatches(grant.SceneId) &&
        grant.SceneEpoch == _localSceneEpoch &&
        SendReliable(Protocol.EncodeFishLootGrant(++_sequence, grant));

    internal bool TryTakeFishLootGrant(out FishLootGrant grant) =>
        _fishLootGrants.TryDequeue(out grant);

    internal bool SendFishLootComplete(FishLootComplete complete) =>
        _role == SessionRole.Client && SceneMatches(complete.SceneId) &&
        complete.SceneEpoch == _remoteSceneEpoch &&
        SendReliable(Protocol.EncodeFishLootComplete(++_sequence, complete));

    internal bool TryTakeFishLootComplete(out FishLootComplete complete) =>
        _fishLootCompletions.TryDequeue(out complete);

    internal bool SendFishHookPose(FishHookPose pose)
    {
        if (_role != SessionRole.Client || !SceneMatches(pose.SceneId) ||
            pose.SceneEpoch != _remoteSceneEpoch)
            return false;
        Send(Protocol.EncodeFishHookPose(++_sequence, pose));
        return true;
    }

    internal bool TryTakeFishHookPose(out FishHookPose pose) =>
        _fishHookPoses.TryDequeue(out pose);

    internal bool SendPickupRemoved(PickupRemoved removed)
    {
        if (_role is (SessionRole.Host or SessionRole.Client) && SceneMatches(removed.SceneId))
            return SendReliable(Protocol.EncodePickupRemoved(++_sequence, removed));
        return false;
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

    internal void SendSceneSeed(SceneSeed seed)
    {
        if (_role == SessionRole.Host && _connected)
            SendReliable(Protocol.EncodeSceneSeed(++_sequence, seed));
    }

    internal bool TryTakeSceneSeed(out SceneSeed seed) => _sceneSeeds.TryDequeue(out seed);

    internal void SendCargoState(CargoState state)
    {
        if (_role == SessionRole.Host && _connected)
            SendReliable(Protocol.EncodeCargoState(++_sequence, state));
    }

    internal bool TryTakeCargoState(out CargoState state) => _cargoStates.TryDequeue(out state);

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

    internal bool SendSaveSnapshotChunk(SaveSnapshotChunk chunk)
    {
        if (_role == SessionRole.Host && _connected && ReliableCapacityRemaining > 0)
            return SendReliable(Protocol.EncodeSaveSnapshotChunk(++_sequence, chunk));
        return false;
    }

    internal bool TryTakeSaveSnapshotChunk(out SaveSnapshotChunk chunk) =>
        _saveSnapshotChunks.TryDequeue(out chunk);

    internal void SendSaveSnapshotAck(SaveSnapshotAck ack)
    {
        if (_role == SessionRole.Client && _connected)
            SendReliable(Protocol.EncodeSaveSnapshotAck(++_sequence, ack));
    }

    internal bool TryTakeSaveSnapshotAck(out SaveSnapshotAck ack) =>
        _saveSnapshotAcks.TryDequeue(out ack);

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
        if (_role is (SessionRole.Host or SessionRole.Client) && _connected)
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
        if (_role is (SessionRole.Host or SessionRole.Client) && _connected)
        {
            _log.LogInfo($"Network: send dive loot item={request.ItemId}; count={request.Count}");
            SendReliable(Protocol.EncodeDiveLootRequest(++_sequence, request));
        }
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

    internal void SendMissionState(MissionState state)
    {
        if (_role == SessionRole.Host && _connected)
            SendReliable(Protocol.EncodeMissionState(++_sequence, state));
    }

    internal bool TryTakeMissionState(out MissionState state) =>
        _missionStates.TryDequeue(out state);

    internal bool SendMissionRoster(MissionRoster roster)
    {
        if (_role == SessionRole.Host && _connected && ReliableCapacityRemaining > 0)
            return SendReliable(Protocol.EncodeMissionRoster(++_sequence, roster));
        return false;
    }

    internal bool TryTakeMissionRoster(out MissionRoster roster) =>
        _missionRosters.TryDequeue(out roster);

    internal void SendWorldFlagRequest(WorldFlagRequest request)
    {
        if (_role == SessionRole.Client && _connected)
            SendReliable(Protocol.EncodeWorldFlagRequest(++_sequence, request));
    }

    internal bool TryTakeWorldFlagRequest(out WorldFlagRequest request) =>
        _worldFlagRequests.TryDequeue(out request);

    internal bool SendWorldFlagState(WorldFlagState state)
    {
        if (_role == SessionRole.Host && _connected)
            return SendReliable(Protocol.EncodeWorldFlagState(++_sequence, state));
        return false;
    }

    internal bool TryTakeWorldFlagState(out WorldFlagState state) =>
        _worldFlagStates.TryDequeue(out state);

    internal void SendBossDamageRequest(BossDamageRequest request)
    {
        if (_role == SessionRole.Client && _connected)
            SendReliable(Protocol.EncodeBossDamageRequest(++_sequence, request));
    }

    internal bool TryTakeBossDamageRequest(out BossDamageRequest request) =>
        _bossDamageRequests.TryDequeue(out request);

    internal void SendBossState(BossState state)
    {
        if (_role == SessionRole.Host && _connected)
            Send(Protocol.EncodeBossState(++_sequence, state));
    }

    internal bool TryTakeBossState(out BossState state) =>
        _bossStates.TryDequeue(out state);

    internal bool SendManagerEvent(ManagerEvent state)
    {
        return _connected && SendReliable(Protocol.EncodeManagerEvent(++_sequence, state));
    }

    internal bool TryTakeManagerEvent(out ManagerEvent state) =>
        _managerEvents.TryDequeue(out state);

    internal void SendSushiResultState(SushiResultState state)
    {
        if (_role == SessionRole.Host && _connected)
            SendReliable(Protocol.EncodeSushiResultState(++_sequence, state));
    }

    internal bool TryTakeSushiResultState(out SushiResultState state) =>
        _sushiResultStates.TryDequeue(out state);

    internal bool SendNpcInteraction(NpcInteraction state)
    {
        if (!_connected || !SceneMatches(state.SceneId) ||
            (_role == SessionRole.Client && state.Action == NpcInteractionAction.Granted) ||
            (_role == SessionRole.Host && state.Action == NpcInteractionAction.Request))
            return false;
        return SendReliable(Protocol.EncodeNpcInteraction(++_sequence, state));
    }

    internal bool TryTakeNpcInteraction(out NpcInteraction state) =>
        _npcInteractions.TryDequeue(out state);

    internal bool TryTakeSnapshot(out PlayerSnapshot snapshot)
    {
        snapshot = _snapshot;
        if (!_hasSnapshot)
            return false;
        _hasSnapshot = false;
        return true;
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
        _sessionId = role == SessionRole.Client
            ? BitConverter.ToUInt64(Guid.NewGuid().ToByteArray(), 0)
            : 0;
        if (role == SessionRole.Client && _sessionId == 0)
            _sessionId = 1;
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

        FlushReliableBacklog();
        RetryReliable(now);

        if (_connected && _nextTrafficLog == 0f)
            _nextTrafficLog = now + 10f;
        else if (_connected && now >= _nextTrafficLog)
        {
            _nextTrafficLog = now + 10f;
            _log.LogDebug(
                $"Network traffic: tx={_sentBytes / 10f:F0} B/s ({_sentPackets / 10f:F1} pps), " +
                $"rx={_receivedBytes / 10f:F0} B/s ({_receivedPackets / 10f:F1} pps), " +
                $"reliable={_pendingReliable.Count}, backlog={_reliableBacklog.Count}");
            _sentBytes = 0;
            _receivedBytes = 0;
            _sentPackets = 0;
            _receivedPackets = 0;
        }

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
        if (!Protocol.TryDecode(received.Buffer, out var type, out var sequence, out var sessionId))
            return;

        if (_role == SessionRole.Host && _remote == null)
        {
            if (type != PacketType.Hello || sessionId == 0 ||
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
            _sessionId = sessionId;
        }

        if (_remote == null || !received.RemoteEndPoint.Equals(_remote))
            return;
        if (sessionId == 0 || sessionId != _sessionId)
            return;
        _receivedBytes += received.Buffer.Length;
        _receivedPackets++;

        if (type == PacketType.Ack)
        {
            if (_connected && _pendingReliable.Remove(sequence))
                _lastReceive = now;
            return;
        }

        if (type == PacketType.PlayerSnapshot)
        {
            if (_connected && Protocol.TryDecodeSnapshot(received.Buffer, out _, out var snapshot) &&
                _hasRemoteScene && snapshot.SceneId == _remoteSceneId &&
                snapshot.SceneEpoch == _remoteSceneEpoch)
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
                EnqueueProjectileVisualState(state);
            }
            return;
        }

        if (type == PacketType.SceneState)
        {
            if (_connected &&
                Protocol.TryDecodeSceneState(
                    received.Buffer, out var sceneSequence, out var sceneId, out var sceneEpoch))
            {
                _lastReceive = now;
                if (IsNewer(sceneSequence, _lastSceneSequence))
                {
                    _lastSceneSequence = sceneSequence;
                    if (!_hasRemoteScene || _remoteSceneId != sceneId)
                        _log.LogInfo($"Network: peer scene {sceneId:X8}");
                    _remoteSceneId = sceneId;
                    _remoteSceneEpoch = sceneEpoch;
                    _hasRemoteScene = true;
                }
            }
            return;
        }

        if (type == PacketType.SceneSeed)
        {
            if (_connected && _role == SessionRole.Client &&
                Protocol.TryDecodeSceneSeed(received.Buffer, out _, out var seed))
            {
                _lastReceive = now;
                if (AcceptReliable(sequence))
                    _sceneSeeds.Enqueue(seed);
            }
            return;
        }

        if (type == PacketType.CargoState)
        {
            if (_connected && _role == SessionRole.Client &&
                Protocol.TryDecodeCargoState(received.Buffer, out _, out var state))
            {
                _lastReceive = now;
                if (AcceptReliable(sequence))
                    _cargoStates.Enqueue(state);
            }
            return;
        }

        if (type == PacketType.FishSnapshotBatch)
        {
            if (_connected && _role == SessionRole.Client &&
                Protocol.TryDecodeFishSnapshotBatch(received.Buffer, out _, out var fishSnapshots) &&
                fishSnapshots[0].SceneId == _remoteSceneId &&
                fishSnapshots[0].SceneEpoch == _remoteSceneEpoch)
            {
                _lastReceive = now;
                foreach (var fishSnapshot in fishSnapshots)
                    _fishSnapshots.Enqueue(fishSnapshot);
            }
            return;
        }

        if (type == PacketType.FishLifecycle)
        {
            if (_connected && IsFishDirectionAllowed(_role, type) &&
                Protocol.TryDecodeFishLifecycle(received.Buffer, out _, out var state))
            {
                _lastReceive = now;
                if (AcceptReliable(sequence) && state.SceneId == _remoteSceneId &&
                    state.SceneEpoch == _remoteSceneEpoch)
                    _fishLifecycles.Enqueue(state);
            }
            return;
        }

        if (type == PacketType.FishActionRequest)
        {
            if (_connected && IsFishDirectionAllowed(_role, type) &&
                Protocol.TryDecodeFishActionRequest(received.Buffer, out _, out var request))
            {
                _lastReceive = now;
                if (AcceptReliable(sequence))
                    _fishActionRequests.Enqueue(request);
            }
            return;
        }

        if (type == PacketType.FishActionAck)
        {
            if (_connected && IsFishDirectionAllowed(_role, type) &&
                Protocol.TryDecodeFishActionAck(received.Buffer, out _, out var ack))
            {
                _lastReceive = now;
                if (AcceptReliable(sequence) && ack.SceneId == _remoteSceneId &&
                    ack.SceneEpoch == _remoteSceneEpoch)
                    _fishActionAcks.Enqueue(ack);
            }
            return;
        }

        if (type == PacketType.FishLootGrant)
        {
            if (_connected && IsFishDirectionAllowed(_role, type) &&
                Protocol.TryDecodeFishLootGrant(received.Buffer, out _, out var grant))
            {
                _lastReceive = now;
                if (AcceptReliable(sequence) && grant.SceneId == _remoteSceneId &&
                    grant.SceneEpoch == _remoteSceneEpoch)
                    _fishLootGrants.Enqueue(grant);
            }
            return;
        }

        if (type == PacketType.FishLootComplete)
        {
            if (_connected && IsFishDirectionAllowed(_role, type) &&
                Protocol.TryDecodeFishLootComplete(received.Buffer, out _, out var complete))
            {
                _lastReceive = now;
                if (AcceptReliable(sequence) && complete.SceneId == _localSceneId &&
                    complete.SceneEpoch == _localSceneEpoch)
                    _fishLootCompletions.Enqueue(complete);
            }
            return;
        }

        if (type == PacketType.FishHookPose)
        {
            if (_connected && IsFishDirectionAllowed(_role, type) &&
                Protocol.TryDecodeFishHookPose(received.Buffer, out _, out var pose) &&
                pose.SceneId == _localSceneId && pose.SceneEpoch == _localSceneEpoch)
            {
                _lastReceive = now;
                _fishHookPoses.Enqueue(pose);
            }
            return;
        }

        if (type == PacketType.FishDamageRequest)
        {
            if (_connected && _role == SessionRole.Host &&
                Protocol.TryDecodeFishDamageRequest(received.Buffer, out _, out var request))
            {
                _lastReceive = now;
                if (AcceptReliable(sequence) && request.SceneId == _localSceneId &&
                    request.SceneEpoch == _localSceneEpoch)
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
                if (AcceptReliable(sequence) && request.SceneId == _localSceneId &&
                    request.SceneEpoch == _localSceneEpoch)
                {
                    _fishPickupRequests.Enqueue(request);
                }
            }
            return;
        }

        if (type == PacketType.FishPickupResult)
        {
            if (_connected && _role == SessionRole.Client &&
                Protocol.TryDecodeFishPickupResult(received.Buffer, out _, out var result))
            {
                _lastReceive = now;
                if (AcceptReliable(sequence) && result.SceneId == _remoteSceneId &&
                    result.SceneEpoch == _remoteSceneEpoch)
                    _fishPickupResults.Enqueue(result);
            }
            return;
        }

        if (type == PacketType.FishRemoved)
        {
            if (_connected && _role == SessionRole.Client &&
                Protocol.TryDecodeFishRemoved(received.Buffer, out _, out var removed))
            {
                _lastReceive = now;
                if (AcceptReliable(sequence) && removed.SceneId == _remoteSceneId &&
                    removed.SceneEpoch == _remoteSceneEpoch)
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
                if (AcceptReliable(sequence) && manifest.SceneId == _remoteSceneId &&
                    manifest.SceneEpoch == _remoteSceneEpoch)
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
                if (AcceptReliable(sequence) && state.SceneId == _remoteSceneId &&
                    state.SceneEpoch == _remoteSceneEpoch)
                    _fishManifestStates.Enqueue(state);
            }
            return;
        }

        if (type == PacketType.PickupRemoved)
        {
            if (_connected && _role is (SessionRole.Host or SessionRole.Client) &&
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
                if (AcceptReliable(sequence) && IsNewer(sequence, _lastTransitionSequence))
                {
                    _lastTransitionSequence = sequence;
                    _sceneSeeds.Enqueue(new SceneSeed(command.SceneId, command.Seed));
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

        if (type == PacketType.SaveSnapshotChunk)
        {
            if (_connected && _role == SessionRole.Client &&
                Protocol.TryDecodeSaveSnapshotChunk(received.Buffer, out _, out var chunk))
            {
                _lastReceive = now;
                if (AcceptReliable(sequence))
                    _saveSnapshotChunks.Enqueue(chunk);
            }
            return;
        }

        if (type == PacketType.SaveSnapshotAck)
        {
            if (_connected && _role == SessionRole.Host &&
                Protocol.TryDecodeSaveSnapshotAck(received.Buffer, out _, out var ack))
            {
                _lastReceive = now;
                if (AcceptReliable(sequence))
                    _saveSnapshotAcks.Enqueue(ack);
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
            if (_connected && (_role is SessionRole.Host or SessionRole.Client) &&
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
                if (state.Id >= 0 && AcceptReliable(sequence) &&
                    IsNewer(sequence, _lastBoatDecoSequence))
                {
                    _lastBoatDecoSequence = sequence;
                    _boatDecoStates.Enqueue(state);
                }
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
            if (_connected && _role is (SessionRole.Host or SessionRole.Client) &&
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

        if (type == PacketType.MissionState)
        {
            if (_connected && _role == SessionRole.Client &&
                Protocol.TryDecodeMissionState(received.Buffer, out _, out var state))
            {
                _lastReceive = now;
                if (AcceptReliable(sequence))
                    _missionStates.Enqueue(state);
            }
            return;
        }

        if (type == PacketType.MissionRoster)
        {
            if (_connected && _role == SessionRole.Client &&
                Protocol.TryDecodeMissionRoster(received.Buffer, out _, out var roster))
            {
                _lastReceive = now;
                if (AcceptReliable(sequence))
                    _missionRosters.Enqueue(roster);
            }
            return;
        }

        if (type == PacketType.WorldFlagRequest)
        {
            if (_connected && _role == SessionRole.Host &&
                Protocol.TryDecodeWorldFlagRequest(received.Buffer, out _, out var request))
            {
                _lastReceive = now;
                if (AcceptReliable(sequence))
                    _worldFlagRequests.Enqueue(request);
            }
            return;
        }

        if (type == PacketType.WorldFlagState)
        {
            if (_connected && _role == SessionRole.Client &&
                Protocol.TryDecodeWorldFlagState(received.Buffer, out _, out var state))
            {
                _lastReceive = now;
                if (AcceptReliable(sequence))
                    _worldFlagStates.Enqueue(state);
            }
            return;
        }

        if (type == PacketType.BossDamageRequest)
        {
            if (_connected && _role == SessionRole.Host &&
                Protocol.TryDecodeBossDamageRequest(received.Buffer, out _, out var request))
            {
                _lastReceive = now;
                if (AcceptReliable(sequence))
                    _bossDamageRequests.Enqueue(request);
            }
            return;
        }

        if (type == PacketType.BossState)
        {
            if (_connected && _role == SessionRole.Client &&
                Protocol.TryDecodeBossState(received.Buffer, out _, out var state))
            {
                _lastReceive = now;
                _bossStates.Enqueue(state);
            }
            return;
        }

        if (type == PacketType.ManagerEvent)
        {
            if (_connected && Protocol.TryDecodeManagerEvent(received.Buffer, out _, out var state) &&
                (_role == SessionRole.Host && state.Revision == 0 ||
                 _role == SessionRole.Client && state.Revision != 0))
            {
                _lastReceive = now;
                if (AcceptReliable(sequence))
                    _managerEvents.Enqueue(state);
            }
            return;
        }

        if (type == PacketType.SushiResultState)
        {
            if (_connected && _role == SessionRole.Client &&
                Protocol.TryDecodeSushiResultState(received.Buffer, out _, out var state))
            {
                _lastReceive = now;
                if (AcceptReliable(sequence))
                    _sushiResultStates.Enqueue(state);
            }
            return;
        }

        if (type == PacketType.NpcInteraction)
        {
            if (_connected && Protocol.TryDecodeNpcInteraction(received.Buffer, out _, out var state) &&
                (_role == SessionRole.Host && state.Action is
                    NpcInteractionAction.Request or NpcInteractionAction.Released ||
                 _role == SessionRole.Client && state.Action is
                    NpcInteractionAction.Granted or NpcInteractionAction.Released))
            {
                _lastReceive = now;
                if (AcceptReliable(sequence))
                    _npcInteractions.Enqueue(state);
            }
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
            {
                _log.LogInfo($"Network: {_remoteName} connected from {_remote}");
                AdvanceLocalSceneEpoch();
            }
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
            {
                _log.LogInfo($"Network: connected to {_remoteName}");
                AdvanceLocalSceneEpoch();
            }
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

    private void SendSceneState()
    {
        if (_localSceneEpoch != 0)
            Send(Protocol.EncodeSceneState(++_sequence, _localSceneId, _localSceneEpoch));
    }

    private void AdvanceLocalSceneEpoch() =>
        _localSceneEpoch = _localSceneEpoch == uint.MaxValue ? 1 : _localSceneEpoch + 1;

    private void Send(byte[] packet)
    {
        if (_udp == null || _remote == null)
            return;
        try
        {
            Protocol.SetSessionId(packet, _sessionId);
            _udp.Send(packet, packet.Length, _remote);
            _sentBytes += packet.Length;
            _sentPackets++;
        }
        catch (Exception exception)
        {
            _log.LogWarning($"Network send failed: {exception.Message}");
        }
    }

    private bool SendReliable(byte[] packet)
    {
        if (!Protocol.TryDecode(packet, out _, out var sequence))
            return false;
        if (_reliableBacklog.Count > 0 || _pendingReliable.Count >= 256)
        {
            if (_reliableBacklog.Count >= 2048)
            {
                _log.LogWarning("Network reliable backlog is full");
                return false;
            }
            _reliableBacklog.Enqueue(packet);
            return true;
        }
        TrackReliable(packet, sequence);
        return true;
    }

    private void FlushReliableBacklog()
    {
        while (_connected && _pendingReliable.Count < 256 &&
               _reliableBacklog.Count > 0)
        {
            var packet = _reliableBacklog.Dequeue();
            if (Protocol.TryDecode(packet, out _, out var sequence))
                TrackReliable(packet, sequence);
        }
    }

    private void TrackReliable(byte[] packet, uint sequence)
    {
        _pendingReliable[sequence] = new PendingReliable
        {
            Packet = packet,
            NextSend = _now + 0.1f,
            RetryDelay = 0.1f
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
            pending.RetryDelay = Math.Min(1f, pending.RetryDelay * 2f);
            pending.NextSend = now + pending.RetryDelay;
        }
    }

    private bool AcceptReliable(uint sequence)
    {
        Send(Protocol.Encode(PacketType.Ack, sequence));
        return _receivedReliable.Accept(sequence);
    }

    private static bool IsNewer(uint sequence, uint previous) =>
        unchecked((int)(sequence - previous)) > 0;

    private static bool IsFishDirectionAllowed(SessionRole receiver, PacketType type) =>
        receiver == SessionRole.Client && type is PacketType.FishLifecycle or
            PacketType.FishActionAck or PacketType.FishLootGrant ||
        receiver == SessionRole.Host && type is PacketType.FishActionRequest or
            PacketType.FishLootComplete or PacketType.FishHookPose;

    private static bool PlayerSnapshotChanged(PlayerSnapshot previous, PlayerSnapshot current)
    {
        var dx = current.X - previous.X;
        var dy = current.Y - previous.Y;
        var dz = current.Z - previous.Z;
        var dvx = current.VelocityX - previous.VelocityX;
        var dvy = current.VelocityY - previous.VelocityY;
        return current.SceneId != previous.SceneId || current.SceneEpoch != previous.SceneEpoch ||
            dx * dx + dy * dy + dz * dz >= 0.0025f ||
            MathF.Abs(current.Rotation - previous.Rotation) >= 1f ||
            dvx * dvx + dvy * dvy >= 0.01f || current.SpriteId != previous.SpriteId ||
            current.ScaleX != previous.ScaleX || current.ScaleY != previous.ScaleY ||
            current.Flipped != previous.Flipped;
    }

    private static bool SameVisualState(PlayerVisualState left, PlayerVisualState right)
    {
        if (left.SceneId != right.SceneId)
            return false;
        var leftSprites = left.Sprites ?? Array.Empty<VisualSprite>();
        var rightSprites = right.Sprites ?? Array.Empty<VisualSprite>();
        if (leftSprites.Length != rightSprites.Length)
            return false;
        for (var index = 0; index < leftSprites.Length; index++)
            if (leftSprites[index] != rightSprites[index])
                return false;
        return true;
    }

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
        _remoteSceneEpoch = 0;
        _hasRemoteScene = false;
        _lastSceneSequence = 0;
        if (_role == SessionRole.Host)
            _sessionId = 0;
        while (_fishSnapshots.TryDequeue(out _))
        {
        }
        while (_playerVisualStates.TryDequeue(out _))
        {
        }
        _projectileVisualOrder.Clear();
        _projectileVisualStates.Clear();
        while (_fishDamageRequests.TryDequeue(out _))
        {
        }
        while (_fishPickupRequests.TryDequeue(out _))
        {
        }
        while (_fishPickupResults.TryDequeue(out _))
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
        while (_fishLifecycles.TryDequeue(out _))
        {
        }
        while (_fishActionRequests.TryDequeue(out _))
        {
        }
        while (_fishActionAcks.TryDequeue(out _))
        {
        }
        while (_fishLootGrants.TryDequeue(out _))
        {
        }
        while (_fishLootCompletions.TryDequeue(out _))
        {
        }
        while (_fishHookPoses.TryDequeue(out _))
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
        _lastTransitionSequence = 0;
        _lastBoatDecoSequence = 0;
        _lastSentSnapshot = default;
        _lastSentVisualState = default;
        _hasSentSnapshot = false;
        _hasSentVisualState = false;
        _sentBytes = 0;
        _receivedBytes = 0;
        _sentPackets = 0;
        _receivedPackets = 0;
        _nextTrafficLog = 0f;
        _pendingReliable.Clear();
        _reliableBacklog.Clear();
        _receivedReliable.Clear();
        while (_roomReady.TryDequeue(out _))
        {
        }
        while (_roomStates.TryDequeue(out _))
        {
        }
        while (_saveSnapshotChunks.TryDequeue(out _))
        {
        }
        while (_saveSnapshotAcks.TryDequeue(out _))
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
        while (_missionStates.TryDequeue(out _))
        {
        }
        while (_missionRosters.TryDequeue(out _))
        {
        }
        while (_worldFlagRequests.TryDequeue(out _))
        {
        }
        while (_worldFlagStates.TryDequeue(out _))
        {
        }
        while (_bossDamageRequests.TryDequeue(out _))
        {
        }
        while (_bossStates.TryDequeue(out _))
        {
        }
        while (_managerEvents.TryDequeue(out _))
        {
        }
        while (_sushiResultStates.TryDequeue(out _))
        {
        }
        while (_npcInteractions.TryDequeue(out _))
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
