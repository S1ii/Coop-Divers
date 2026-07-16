using System;
using System.Collections.Generic;
using BepInEx.Logging;
using DR.AI;
using HarmonyLib;
using UnityEngine;

namespace DaveTheDiverMP;

internal sealed class FishReplicator
{
    private const float SnapshotKeyframeSeconds = 1f;
    private const float RemoteStimulusInterval = 0.2f;

    private sealed class Target
    {
        internal FishAISystem Fish;
        internal Vector3 Position;
        internal float Rotation;
        internal float Hp;
        internal byte Flags;
        internal uint LastTick;
        internal Vector3 Velocity;
        internal float SinceSnapshot;
        internal bool HasSnapshot;
    }

    private sealed class HostFish
    {
        internal FishAISystem Fish;
        internal string AllocatorUid;
        internal int FishDataTID;
        internal FishSnapshot LastSnapshot;
        internal float LastSnapshotSend;
        internal bool HasSnapshot;
        internal bool ManifestQueued;
    }

    private readonly ManualLogSource _log;
    private readonly SessionTrace _trace;
    private readonly Dictionary<int, FishAISystem> _hostFishById = new();
    private readonly Dictionary<FishAISystem, int> _hostIdsByFish = new();
    private readonly Dictionary<int, HostFish> _hostInfoById = new();
    private readonly Dictionary<int, Target> _targets = new();
    private readonly Dictionary<FishAISystem, int> _clientIdsByFish = new();
    private readonly HashSet<int> _removedIds = new();
    private readonly HashSet<int> _pendingClientPickups = new();
    private readonly HashSet<int> _pendingClientCaptures = new();
    private readonly HashSet<FishAISystem> _clientDamageScopes = new();
    private readonly HashSet<FishAISystem> _hostRemovedFish = new();
    private readonly HashSet<FishAISystem> _suppressedClientFish = new();
    private readonly HashSet<int> _missingAllocatorIds = new();
    private readonly Dictionary<uint, HashSet<int>> _clientManifestIds = new();
    private readonly Dictionary<uint, ushort> _clientManifestCounts = new();
    private readonly Dictionary<uint, ushort> _finalizedClientManifestCounts = new();
    private readonly Dictionary<int, FishManifest> _pendingClientManifests = new();
    private readonly Dictionary<int, float> _nextRemoteStimulusById = new();
    private readonly Dictionary<int, float> _lastRemoteDamageById = new();
    private readonly HashSet<int> _remoteStimulatedIds = new();
    private readonly List<FishSnapshot> _snapshotBuffer = new();
    private float _nextHostScan;
    private float _nextSend;
    private bool _manifestStateQueued;
    private int _nextHostId = 1;
    private int _lastHostCount = -1;
    private bool _applyingClientPickup;
    private uint _manifestRevision;
    private uint _fishTick;
    private uint _latestClientManifestRevision;
    private float _nextTraceSummary;
    private float _nextClientBindRetry;

    internal FishReplicator(ManualLogSource log, SessionTrace trace)
    {
        _log = log;
        _trace = trace;
    }

    internal void Update(
        SessionRole role,
        UdpSession session,
        uint sceneId,
        float now,
        float deltaTime,
        PlayerCharacter hostPlayer,
        Transform remotePlayerTransform)
    {
        if (role == SessionRole.Host)
        {
            while (session.TryTakeFishSnapshot(out _))
            {
            }
            while (session.TryTakeFishRemoved(out _))
            {
            }
            while (session.TryTakeFishPickupResult(out _))
            {
            }
            while (session.TryTakeFishManifest(out _))
            {
            }
            while (session.TryTakeFishManifestState(out _))
            {
            }
            UpdateHost(session, sceneId, now, hostPlayer, remotePlayerTransform);
            return;
        }

        while (session.TryTakeFishDamageRequest(out _))
        {
        }
        while (session.TryTakeFishPickupRequest(out _))
        {
        }

        if (role != SessionRole.Client || !session.SceneMatches(sceneId))
        {
            while (session.TryTakeFishSnapshot(out _))
            {
            }
            while (session.TryTakeFishRemoved(out _))
            {
            }
            while (session.TryTakeFishPickupResult(out _))
            {
            }
            while (session.TryTakeFishManifest(out _))
            {
            }
            while (session.TryTakeFishManifestState(out _))
            {
            }
            ReleaseClientTargets();
            ResetClientManifest();
            return;
        }
        if (hostPlayer == null)
            return;

        while (session.TryTakeFishManifest(out var manifest))
        {
            if (manifest.SceneId == sceneId)
                ApplyClientManifest(manifest);
        }
        while (session.TryTakeFishManifestState(out var state))
        {
            if (state.SceneId == sceneId)
                ApplyClientManifestState(state);
        }
        if (now >= _nextClientBindRetry)
        {
            _nextClientBindRetry = now + 0.2f;
            RetryPendingClientManifests();
        }

        while (session.TryTakeFishPickupResult(out var result))
        {
            if (result.SceneId == sceneId)
                ApplyClientPickupResult(result, hostPlayer);
        }

        while (session.TryTakeFishRemoved(out var removed))
        {
            if (removed.SceneId != sceneId)
                continue;
            _removedIds.Add(removed.Id);
            RemoveClientTarget(removed.Id, true);
        }

        while (session.TryTakeFishSnapshot(out var snapshot))
        {
            if (snapshot.SceneId != sceneId || _removedIds.Contains(snapshot.Id))
                continue;

            if (!_targets.TryGetValue(snapshot.Id, out var target))
            {
                // Snapshots are frequent and carry enough identity to recover even
                // when a reliable startup manifest was delayed or lost.
                ApplyClientManifest(new FishManifest(
                    snapshot.SceneId, 0, snapshot.Id, "*", snapshot.FishDataTID,
                    snapshot.X, snapshot.Y, snapshot.Z, snapshot.Rotation,
                    snapshot.Hp, snapshot.Flags), false);
                if (!_targets.TryGetValue(snapshot.Id, out target))
                    continue;
            }

            if (target.HasSnapshot && !IsNewer(snapshot.Tick, target.LastTick))
                continue;
            target.Position = new Vector3(snapshot.X, snapshot.Y, snapshot.Z);
            target.LastTick = snapshot.Tick;
            target.Velocity = new Vector3(snapshot.VelocityX, snapshot.VelocityY, 0f);
            target.SinceSnapshot = 0f;
            target.Rotation = snapshot.Rotation;
            target.Hp = snapshot.Hp;
            target.Flags = snapshot.Flags;
            target.HasSnapshot = true;
            ApplyFlags(target);
            SetClientSimulation(target.Fish, false, snapshot.Id);
        }

        var blend = 1f - Mathf.Exp(-14f * deltaTime);
        foreach (var target in _targets.Values)
        {
            if (!target.HasSnapshot || target.Fish == null)
                continue;
            target.SinceSnapshot += deltaTime;
            var predicted = target.Position + target.Velocity * Mathf.Min(target.SinceSnapshot, 0.2f);
            target.Fish.transform.position = Vector3.Lerp(
                target.Fish.transform.position, predicted, blend);
            target.Fish.Rotation = Mathf.LerpAngle(
                target.Fish.Rotation, target.Rotation, blend);
            if (Mathf.Abs(target.Fish.HP - target.Hp) > 0.01f)
                target.Fish.SetHP(target.Hp);
        }
        WriteClientSummary(now);
    }

    internal bool RequestDamage(
        UdpSession session,
        uint sceneId,
        FishAISystem fish,
        int damage,
        EElement element,
        AttackType attackType)
    {
        if (fish == null || damage <= 0 || !IsPlayerAttack(attackType))
            return false;
        if (!_clientIdsByFish.TryGetValue(fish, out var id))
        {
            _trace?.Write("DAMAGE-BLOCK",
                $"unmapped type={fish.FishDataTID} instance={fish.GetInstanceID()} " +
                $"damage={damage} attack={attackType}");
            return false;
        }
        session.SendFishDamageRequest(new FishDamageRequest(
            sceneId, id, Mathf.Clamp(damage, 1, 10_000), (int)element, (int)attackType));
        _trace?.Write("DAMAGE-SEND",
            $"id={id} type={fish.FishDataTID} damage={damage} element={element} attack={attackType}");
        return true;
    }

    internal bool BeginClientDamage(
        UdpSession session,
        uint sceneId,
        FishAISystem fish,
        AttackData attackData)
    {
        if (fish == null || attackData == null || !IsPlayerAttack(attackData.attackType))
            return false;
        if (!IsClientProxy(fish))
        {
            _trace?.Write("DAMAGE-BLOCK",
                $"unmapped OnTakeDamage type={fish.FishDataTID} instance={fish.GetInstanceID()}");
            return false;
        }
        RequestDamage(
            session, sceneId, fish, Math.Max(1, attackData.damage),
            attackData.element, attackData.attackType);
        _clientDamageScopes.Add(fish);
        return true;
    }

    internal void EndClientDamage(FishAISystem fish) => _clientDamageScopes.Remove(fish);

    internal bool IsClientDamageScoped(FishAISystem fish) =>
        fish != null && _clientDamageScopes.Contains(fish);

    internal bool RequestPickup(UdpSession session, uint sceneId, FishAISystem fish)
        => RequestPickup(session, sceneId, fish, false);

    internal bool RequestCapture(UdpSession session, uint sceneId, FishAISystem fish)
        => RequestPickup(session, sceneId, fish, true);

    private bool RequestPickup(
        UdpSession session,
        uint sceneId,
        FishAISystem fish,
        bool capture)
    {
        if (fish == null || !_clientIdsByFish.TryGetValue(fish, out var id))
        {
            _trace?.Write("PICKUP-BLOCK",
                fish == null ? "fish=null" :
                $"unmapped type={fish.FishDataTID} instance={fish.GetInstanceID()}");
            return false;
        }
        if (capture)
            _pendingClientCaptures.Add(id);
        if (!_pendingClientPickups.Add(id))
            return true;
        session.SendFishPickupRequest(new FishPickupRequest(sceneId, id));
        _trace?.Write(capture ? "CAPTURE-SEND" : "PICKUP-SEND",
            $"id={id} type={fish.FishDataTID}");
        return true;
    }

    internal bool ApplyingClientPickup => _applyingClientPickup;

    internal void ObserveHostPickup(UdpSession session, uint sceneId, FishAISystem fish)
    {
        if (fish == null)
            return;
        _hostRemovedFish.Add(fish);
        if (!_hostIdsByFish.TryGetValue(fish, out var id))
            return;
        session.SendFishRemoved(new FishRemoved(sceneId, id));
        RemoveHostFish(id);
        _log.LogInfo($"Network host fish pickup completed: id={id}");
        _trace?.Write("HOST-PICKUP", $"id={id} type={fish.FishDataTID}");
    }

    internal bool IsClientProxy(FishAISystem fish) =>
        fish != null && _clientIdsByFish.ContainsKey(fish);

    internal void Clear()
    {
        ReleaseClientTargets();
        _hostFishById.Clear();
        _hostIdsByFish.Clear();
        _hostInfoById.Clear();
        _hostRemovedFish.Clear();
        _clientDamageScopes.Clear();
        _lastRemoteDamageById.Clear();
        _nextRemoteStimulusById.Clear();
        _remoteStimulatedIds.Clear();
        ResetClientManifest();
        _nextHostScan = 0f;
        _nextSend = 0f;
        _manifestStateQueued = false;
        _nextHostId = 1;
        _lastHostCount = -1;
        _manifestRevision = 0;
        _fishTick = 0;
        _latestClientManifestRevision = 0;
        _nextTraceSummary = 0f;
        _nextClientBindRetry = 0f;
    }

    private void UpdateHost(
        UdpSession session,
        uint sceneId,
        float now,
        PlayerCharacter hostPlayer,
        Transform remotePlayerTransform)
    {
        if (!session.SceneMatches(sceneId))
        {
            while (session.TryTakeFishDamageRequest(out _))
            {
            }
            while (session.TryTakeFishPickupRequest(out _))
            {
            }
            foreach (var info in _hostInfoById.Values)
                info.ManifestQueued = false;
            _manifestStateQueued = false;
            return;
        }
        if (now >= _nextHostScan)
            RefreshHostFish(session, sceneId, now);

        StimulateHostFishForRemotePlayer(
            session, sceneId, now, remotePlayerTransform);

        while (session.TryTakeFishDamageRequest(out var damageRequest))
            ApplyHostDamage(session, sceneId, now, remotePlayerTransform, damageRequest);
        while (session.TryTakeFishPickupRequest(out var pickupRequest))
            ApplyHostPickup(session, sceneId, now, hostPlayer, pickupRequest);

        WriteHostSummary(now);

        SendFishManifests(session, sceneId);
        if (now < _nextSend)
            return;

        _nextSend = now + 0.1f;
        _fishTick = NextRevision(_fishTick);
        _snapshotBuffer.Clear();
        foreach (var pair in _hostFishById)
        {
            var fish = pair.Value;
            if (fish == null || !fish.gameObject.activeInHierarchy)
                continue;
            var position = fish.transform.position;
            var velocity = fish.Velocity;
            var snapshot = new FishSnapshot(
                sceneId, _fishTick, pair.Key, fish.FishDataTID,
                position.x, position.y, position.z, fish.Rotation,
                velocity.x, velocity.y, Mathf.Max(0f, fish.HP), BuildFlags(fish));
            var info = _hostInfoById[pair.Key];
            if (!ShouldSendSnapshot(info, snapshot, now))
                continue;
            info.LastSnapshot = snapshot;
            info.LastSnapshotSend = now;
            info.HasSnapshot = true;
            _snapshotBuffer.Add(snapshot);
        }
        if (_snapshotBuffer.Count > 0)
            session.SendFishSnapshots(sceneId, _fishTick, _snapshotBuffer);
    }

    private void RefreshHostFish(UdpSession session, uint sceneId, float now)
    {
        _nextHostScan = now + 1f;
        var allocators = new List<FishAllocator>();
        foreach (var allocator in UnityEngine.Object.FindObjectsByType<FishAllocator>(FindObjectsSortMode.None))
        {
            if (allocator != null)
                allocators.Add(allocator);
        }

        var allocatorByFish = new Dictionary<FishAISystem, FishAllocator>();
        foreach (var allocator in allocators)
        {
            var fishs = allocator.GetInstancedFishs;
            if (fishs == null)
                continue;
            foreach (var fish in fishs)
            {
                if (fish != null && !fish.IsFishCaptured && !_hostRemovedFish.Contains(fish))
                    allocatorByFish.TryAdd(fish, allocator);
            }
        }

        var topologyChanged = false;
        var staleFish = new List<FishAISystem>();
        foreach (var pair in _hostIdsByFish)
        {
            if (!allocatorByFish.ContainsKey(pair.Key))
                staleFish.Add(pair.Key);
        }
        foreach (var fish in staleFish)
        {
            var id = _hostIdsByFish[fish];
            session.SendFishRemoved(new FishRemoved(sceneId, id));
            RemoveHostFish(id);
            topologyChanged = true;
        }

        foreach (var pair in allocatorByFish)
        {
            var fish = pair.Key;
            var allocator = pair.Value;
            var fishDataTID = fish.FishDataTID;
            if (fishDataTID <= 0)
                continue;
            var uid = GetNetworkAllocatorUid(sceneId, allocator);

            if (!_hostIdsByFish.TryGetValue(fish, out var id))
            {
                id = TakeHostId();
                _hostIdsByFish.Add(fish, id);
            }
            _hostFishById[id] = fish;
            if (!_hostInfoById.TryGetValue(id, out var info))
            {
                info = new HostFish();
                _hostInfoById.Add(id, info);
                topologyChanged = true;
            }
            else if (info.AllocatorUid != uid || info.FishDataTID != fishDataTID)
                topologyChanged = true;
            info.Fish = fish;
            info.AllocatorUid = uid;
            info.FishDataTID = fishDataTID;
        }

        if (_lastHostCount != _hostFishById.Count)
        {
            _log.LogInfo($"Network fish host manifest: {_hostFishById.Count} fish");
            _trace?.Write("HOST-MANIFEST",
                $"revision={_manifestRevision} count={_hostFishById.Count}");
            _lastHostCount = _hostFishById.Count;
        }
        if (_manifestRevision == 0 || topologyChanged)
        {
            _manifestRevision = NextRevision(_manifestRevision);
            foreach (var info in _hostInfoById.Values)
                info.ManifestQueued = false;
            _manifestStateQueued = false;
        }
    }

    private void StimulateHostFishForRemotePlayer(
        UdpSession session,
        uint sceneId,
        float now,
        Transform remotePlayerTransform)
    {
        if (remotePlayerTransform == null ||
            !TryGetRemotePlayer(session, sceneId, now, out var remotePlayer))
            return;

        foreach (var pair in _hostFishById)
        {
            var id = pair.Key;
            var fish = pair.Value;
            if (fish == null || !fish.gameObject.activeInHierarchy || fish.IsCorpse ||
                fish.IsFishCaptured ||
                _nextRemoteStimulusById.TryGetValue(id, out var next) && now < next)
                continue;
            _nextRemoteStimulusById[id] = now + RemoteStimulusInterval;

            try
            {
                if (!IsInsideEnemySensor(fish, remotePlayer, out var distance))
                {
                    _remoteStimulatedIds.Remove(id);
                    continue;
                }

                var current = fish.DetectedEnemyData;
                var currentTarget = current?.DetectedEnemy;
                if (currentTarget != null && currentTarget != remotePlayerTransform)
                {
                    var currentDistance = Vector2.Distance(
                        fish.SensorCenterPoint, currentTarget.position);
                    if (currentDistance <= distance)
                        continue;
                }

                WakeHostFish(fish, id);
                if (currentTarget != remotePlayerTransform)
                {
                    fish.OnEnemyDetected(new EnemyDetectSensorData(
                        remotePlayerTransform,
                        new Vector2(remotePlayer.X, remotePlayer.Y),
                        distance,
                        EnumDectectionType.Player));
                }
                else if (current != null)
                {
                    current.Distance = distance;
                    current.HitPoint = new Vector2(remotePlayer.X, remotePlayer.Y);
                }

                if (_remoteStimulatedIds.Add(id))
                    _trace?.Write("REMOTE-DETECT",
                        $"id={id} type={fish.FishDataTID} distance={distance:F2} " +
                        $"aggressive={fish.IsAggressive}");
            }
            catch (Exception exception)
            {
                _trace?.Write("REMOTE-DETECT-ERROR",
                    $"id={id} error={exception.GetType().Name}:{exception.Message}");
            }
        }
    }

    private void WakeHostFish(FishAISystem fish, int id)
    {
        var changed = false;
        if (!fish.IsFishEnable)
        {
            fish.IsFishEnable = true;
            changed = true;
        }
        if (fish.IsFishSleeped)
        {
            fish.OnForceEndSleepMode();
            changed = true;
        }
        if (changed)
            _trace?.Write("WAKE",
                $"id={id} type={fish.FishDataTID} enabled={fish.IsFishEnable} " +
                $"sleeping={fish.IsFishSleeped}");
    }

    private static bool IsInsideEnemySensor(
        FishAISystem fish,
        PlayerSnapshot player,
        out float distance)
    {
        distance = 0f;
        if (!fish.IsEnableEnemyDetectSensor)
            return false;
        var spec = fish.GetFishSpecData;
        if (spec == null || spec.EnemyDetectType == EnumEnemyDetectType.None)
            return false;

        var center = fish.SensorCenterPoint;
        var dx = player.X - center.x;
        var dy = player.Y - center.y;
        distance = Mathf.Sqrt(dx * dx + dy * dy);
        if (spec.EnemyDetectType == EnumEnemyDetectType.CircleOverlap)
            return spec.EnemyCircleSensorRadius > 0f &&
                distance <= spec.EnemyCircleSensorRadius;

        var size = spec.EnemyBoxSensorSize;
        return size.x > 0f && size.y > 0f &&
            Mathf.Abs(dx) <= size.x * 0.5f && Mathf.Abs(dy) <= size.y * 0.5f;
    }

    private void WriteHostSummary(float now)
    {
        if (now < _nextTraceSummary)
            return;
        _nextTraceSummary = now + 2f;
        var active = 0;
        var enabled = 0;
        var sleeping = 0;
        foreach (var fish in _hostFishById.Values)
        {
            if (fish == null)
                continue;
            if (fish.gameObject.activeInHierarchy)
                active++;
            if (fish.IsFishEnable)
                enabled++;
            if (fish.IsFishSleeped)
                sleeping++;
        }
        _trace?.Write("FISH-SUMMARY",
            $"known={_hostFishById.Count} active={active} enabled={enabled} " +
            $"sleeping={sleeping} remoteTargets={_remoteStimulatedIds.Count}");
    }

    private void SendFishManifests(UdpSession session, uint sceneId)
    {
        foreach (var pair in _hostInfoById)
        {
            var info = pair.Value;
            var fish = info.Fish;
            if (fish == null || !fish.gameObject.activeInHierarchy || info.ManifestQueued)
                continue;
            var position = fish.transform.position;
            var manifest = new FishManifest(
                sceneId, _manifestRevision, pair.Key, info.AllocatorUid, info.FishDataTID,
                position.x, position.y, position.z, fish.Rotation,
                Mathf.Max(0f, fish.HP), BuildFlags(fish));
            if (!session.SendFishManifest(manifest))
                return;
            info.ManifestQueued = true;
        }
        if (!_manifestStateQueued && AllCurrentManifestsQueued() &&
            session.SendFishManifestState(new FishManifestState(
                sceneId, _manifestRevision, (ushort)Math.Min(ushort.MaxValue, _hostInfoById.Count))))
            _manifestStateQueued = true;
    }

    private void ApplyHostDamage(
        UdpSession session,
        uint sceneId,
        float now,
        Transform remotePlayerTransform,
        FishDamageRequest request)
    {
        if (request.SceneId != sceneId)
        {
            _trace?.Write("DAMAGE-REJECT",
                $"id={request.Id} reason=scene request={request.SceneId:X8} local={sceneId:X8}");
            return;
        }
        if (!TryGetRemotePlayer(session, sceneId, now, out var remotePlayer))
        {
            _trace?.Write("DAMAGE-REJECT", $"id={request.Id} reason=stale-player");
            return;
        }
        if (!_hostFishById.TryGetValue(request.Id, out var fish))
        {
            RefreshHostFish(session, sceneId, now);
            _hostFishById.TryGetValue(request.Id, out fish);
        }
        var rejectReason = fish == null ? "missing" :
            fish.IsCorpse ? "corpse" :
            fish.IsFishCaptured ? "captured" :
            !Enum.IsDefined(typeof(EElement), request.Element) ? "element" :
            !Enum.IsDefined(typeof(AttackType), request.AttackType) ||
            !IsPlayerAttack((AttackType)request.AttackType) ? "attack" :
            !InRange(fish.transform.position, remotePlayer, 1600f) ? "range" : null;
        if (rejectReason != null)
        {
            _log.LogWarning($"Network fish damage rejected: id={request.Id}");
            _trace?.Write("DAMAGE-REJECT", $"id={request.Id} reason={rejectReason}");
            return;
        }

        var hpBefore = fish.HP;
        try
        {
            WakeHostFish(fish, request.Id);
            if (remotePlayerTransform != null)
                fish.OnUnderAttack(remotePlayerTransform);
            if ((AttackType)request.AttackType == AttackType.QTE_Damage)
                fish.SetHPDamageQTE(request.Damage, (EElement)request.Element);
            else
                fish.SetHPDamage(
                    request.Damage, (EElement)request.Element, (AttackType)request.AttackType);
            _nextSend = 0f;
            _log.LogDebug(
                $"Network fish damage: id={request.Id}; damage={request.Damage}; hp={fish.HP:F1}");
            _trace?.Write("DAMAGE-APPLY",
                $"id={request.Id} type={fish.FishDataTID} damage={request.Damage} " +
                $"attack={(AttackType)request.AttackType} hp={hpBefore:F1}->{fish.HP:F1} " +
                $"corpse={fish.IsCorpse} enabled={fish.IsFishEnable}");
            _lastRemoteDamageById[request.Id] = now;
        }
        catch (Exception exception)
        {
            _log.LogWarning($"Network fish damage failed: id={request.Id}; {exception.Message}");
            _trace?.Write("DAMAGE-ERROR",
                $"id={request.Id} hp={hpBefore:F1} error={exception.GetType().Name}:{exception.Message}");
        }
    }

    private void ApplyHostPickup(
        UdpSession session,
        uint sceneId,
        float now,
        PlayerCharacter hostPlayer,
        FishPickupRequest request)
    {
        if (request.SceneId != sceneId)
        {
            _trace?.Write("PICKUP-REJECT", $"id={request.Id} reason=scene");
            return;
        }
        if (hostPlayer == null || !TryGetRemotePlayer(session, sceneId, now, out var remotePlayer))
        {
            _trace?.Write("PICKUP-REJECT", $"id={request.Id} reason=player-not-ready");
            RejectHostPickup(session, request);
            return;
        }
        if (!_hostFishById.TryGetValue(request.Id, out var fish))
        {
            RefreshHostFish(session, sceneId, now);
            _hostFishById.TryGetValue(request.Id, out fish);
        }
        var body = fish != null ? fish.GetInteractionBody : null;
        var normalPickup = fish != null && body != null && body.IsEnableInteraction &&
            body.InteractionType == FishInteractionBody.FishInteractionType.Pickup &&
            InRange(fish.transform.position, remotePlayer, 16f);
        var recentCapture = fish != null &&
            _lastRemoteDamageById.TryGetValue(request.Id, out var lastDamage) &&
            now - lastDamage is >= 0f and <= 12f &&
            InRange(fish.transform.position, remotePlayer, 1600f);
        if (!normalPickup && !recentCapture)
        {
            _log.LogWarning($"Network fish pickup rejected: id={request.Id}");
            _trace?.Write("PICKUP-REJECT",
                $"id={request.Id} reason=state fish={fish != null} body={body != null} " +
                $"enabled={body?.IsEnableInteraction} type={body?.InteractionType} " +
                $"recentDamage={recentCapture}");
            RejectHostPickup(session, request);
            return;
        }

        try
        {
            _hostRemovedFish.Add(fish);
            fish.DestroySelf();
            session.SendFishPickupResult(new FishPickupResult(sceneId, request.Id, true));
            RemoveHostFish(request.Id);
            _log.LogInfo($"Network client fish pickup approved: id={request.Id}");
            _trace?.Write(recentCapture && !normalPickup ? "CAPTURE-ACCEPT" : "PICKUP-ACCEPT",
                $"id={request.Id} type={fish.FishDataTID}");
        }
        catch (Exception exception)
        {
            _hostRemovedFish.Remove(fish);
            RejectHostPickup(session, request);
            _log.LogWarning($"Network fish pickup failed: id={request.Id}; {exception.Message}");
            _trace?.Write("PICKUP-ERROR",
                $"id={request.Id} error={exception.GetType().Name}:{exception.Message}");
        }
    }

    private void RejectHostPickup(UdpSession session, FishPickupRequest request) =>
        session.SendFishPickupResult(new FishPickupResult(request.SceneId, request.Id, false));

    private void ApplyClientPickupResult(FishPickupResult result, PlayerCharacter player)
    {
        _pendingClientPickups.Remove(result.Id);
        if (_pendingClientCaptures.Remove(result.Id))
        {
            if (result.Accepted)
            {
                _removedIds.Add(result.Id);
                RemoveClientTarget(result.Id, true);
            }
            _trace?.Write("CAPTURE-RESULT",
                $"id={result.Id} accepted={result.Accepted}");
            return;
        }
        if (!result.Accepted)
        {
            player?.SuccessInteraction();
            _log.LogWarning($"Network fish pickup was rejected: id={result.Id}");
            _trace?.Write("PICKUP-RESULT", $"id={result.Id} accepted=false");
            return;
        }

        _removedIds.Add(result.Id);
        if (!_targets.TryGetValue(result.Id, out var target) || target.Fish == null || player == null)
        {
            player?.SuccessInteraction();
            RemoveClientTarget(result.Id, true);
            _log.LogWarning($"Network fish pickup approval had no local target: id={result.Id}");
            _trace?.Write("PICKUP-RESULT", $"id={result.Id} accepted=true local=missing");
            return;
        }

        var fish = target.Fish;
        try
        {
            var body = fish.GetInteractionBody;
            if (body == null)
                throw new InvalidOperationException("fish interaction body is missing");
            _applyingClientPickup = true;
            body.SuccessInteract(player);
            _log.LogInfo($"Network client fish pickup completed: id={result.Id}");
            _trace?.Write("PICKUP-RESULT", $"id={result.Id} accepted=true local=completed");
        }
        catch (Exception exception)
        {
            player.SuccessInteraction();
            _log.LogWarning($"Network client fish pickup completion failed: id={result.Id}; {exception.Message}");
            _trace?.Write("PICKUP-ERROR",
                $"id={result.Id} local error={exception.GetType().Name}:{exception.Message}");
        }
        finally
        {
            _applyingClientPickup = false;
            RemoveClientTarget(result.Id, true);
        }
    }

    private void ApplyClientManifest(FishManifest manifest, bool trackManifest = true)
    {
        if (_removedIds.Contains(manifest.Id))
        {
            _pendingClientManifests.Remove(manifest.Id);
            return;
        }
        if (_targets.TryGetValue(manifest.Id, out var existing) && existing.Fish != null)
        {
            if (trackManifest)
                MarkClientManifestEntry(manifest.Revision, manifest.Id);
            _pendingClientManifests.Remove(manifest.Id);
            return;
        }
        if (existing != null)
            RemoveClientTarget(manifest.Id, false);

        // Bind only fish created by the game's allocator. Creating or destroying
        // allocator entries while its async spawn routine is enumerating the pool
        // corrupts the native collection and causes a per-frame exception storm.
        var fish = TakeUnboundFish(manifest);
        if (fish == null)
        {
            if (trackManifest && manifest.Revision != 0)
                _pendingClientManifests[manifest.Id] = manifest;
            if (_missingAllocatorIds.Add(manifest.Id))
            {
                _log.LogDebug($"Network fish manifest waiting for native fish: id={manifest.Id}");
                _trace?.Write("BIND-WAIT",
                    $"id={manifest.Id} type={manifest.FishDataTID} revision={manifest.Revision}");
            }
            return;
        }

        var localPosition = fish.transform.position;
        var target = new Target
        {
            Fish = fish,
            Position = new Vector3(manifest.X, manifest.Y, manifest.Z),
            Rotation = manifest.Rotation,
            Hp = manifest.Hp,
            Flags = manifest.Flags,
            HasSnapshot = true
        };
        fish.transform.position = target.Position;
        fish.Rotation = target.Rotation;
        fish.SetHP(target.Hp);
        ApplyFlags(target);
        SetClientSimulation(fish, false, manifest.Id);
        _targets[manifest.Id] = target;
        _clientIdsByFish[fish] = manifest.Id;
        _pendingClientManifests.Remove(manifest.Id);
        _missingAllocatorIds.Remove(manifest.Id);
        if (trackManifest)
            MarkClientManifestEntry(manifest.Revision, manifest.Id);
        _log.LogDebug($"Network fish manifest bound: id={manifest.Id}; type={manifest.FishDataTID}");
        _trace?.Write("BIND",
            $"id={manifest.Id} type={manifest.FishDataTID} instance={fish.GetInstanceID()} " +
            $"offset={Vector3.Distance(localPosition, target.Position):F2} revision={manifest.Revision}");
    }

    private void RetryPendingClientManifests()
    {
        if (_pendingClientManifests.Count == 0)
            return;
        foreach (var manifest in new List<FishManifest>(_pendingClientManifests.Values))
            ApplyClientManifest(manifest);
    }

    private void ApplyClientManifestState(FishManifestState state)
    {
        if (_latestClientManifestRevision != 0 && state.Revision != _latestClientManifestRevision &&
            !IsNewer(state.Revision, _latestClientManifestRevision))
            return;
        if (_latestClientManifestRevision != state.Revision)
            RestoreSuppressedClientFish();
        _latestClientManifestRevision = state.Revision;
        _clientManifestCounts[state.Revision] = state.EntryCount;
        if (!_clientManifestIds.ContainsKey(state.Revision))
            _clientManifestIds.Add(state.Revision, new HashSet<int>());
        TryFinalizeClientManifest(state.Revision);
    }

    private void MarkClientManifestEntry(uint revision, int id)
    {
        if (!_clientManifestIds.TryGetValue(revision, out var ids))
        {
            ids = new HashSet<int>();
            _clientManifestIds.Add(revision, ids);
        }
        ids.Add(id);
        TryFinalizeClientManifest(revision);
    }

    private void TryFinalizeClientManifest(uint revision)
    {
        if (revision != _latestClientManifestRevision ||
            !_clientManifestCounts.TryGetValue(revision, out var expected) ||
            !_clientManifestIds.TryGetValue(revision, out var received) || received.Count < expected ||
            (_finalizedClientManifestCounts.TryGetValue(revision, out var finalized) && finalized == expected))
            return;

        _finalizedClientManifestCounts[revision] = expected;
        var pruned = PruneClientFish(received);
        var suppressed = SuppressUnboundClientFish();
        _log.LogInfo(
            $"Network fish manifest applied: revision={revision}; fish={expected}; " +
            $"pruned={pruned}; suppressed={suppressed}");
        _trace?.Write("MANIFEST-COMPLETE",
            $"revision={revision} expected={expected} bound={received.Count} " +
            $"pruned={pruned} suppressed={suppressed}");
    }

    private int PruneClientFish(HashSet<int> manifestIds)
    {
        var pruned = 0;
        foreach (var id in new List<int>(_targets.Keys))
        {
            if (manifestIds.Contains(id))
                continue;
            RemoveClientTarget(id, true);
            pruned++;
        }
        return pruned;
    }

    private void ReleaseClientTargets()
    {
        foreach (var pair in _targets)
            if (pair.Value.Fish != null)
                SetClientSimulation(pair.Value.Fish, true, pair.Key);
        _targets.Clear();
        _clientIdsByFish.Clear();
        RestoreSuppressedClientFish();
    }

    private void ResetClientManifest()
    {
        _removedIds.Clear();
        _pendingClientPickups.Clear();
        _pendingClientCaptures.Clear();
        _missingAllocatorIds.Clear();
        _pendingClientManifests.Clear();
        _clientManifestIds.Clear();
        _clientManifestCounts.Clear();
        _finalizedClientManifestCounts.Clear();
        _latestClientManifestRevision = 0;
    }

    private void WriteClientSummary(float now)
    {
        if (now < _nextTraceSummary)
            return;
        _nextTraceSummary = now + 2f;
        var valid = 0;
        foreach (var target in _targets.Values)
            if (target.Fish != null && target.Fish.gameObject.activeInHierarchy)
                valid++;
        _trace?.Write("FISH-SUMMARY",
            $"bound={_targets.Count} active={valid} pendingBind={_pendingClientManifests.Count} " +
            $"missing={_missingAllocatorIds.Count} suppressed={_suppressedClientFish.Count} " +
            $"pendingPickup={_pendingClientPickups.Count}");
    }

    private int SuppressUnboundClientFish()
    {
        var count = 0;
        foreach (var fish in UnityEngine.Object.FindObjectsByType<FishAISystem>(
                     FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            if (fish == null || _clientIdsByFish.ContainsKey(fish) ||
                _suppressedClientFish.Contains(fish))
                continue;
            try
            {
                fish.gameObject.SetActive(false);
                _suppressedClientFish.Add(fish);
                count++;
            }
            catch (Exception exception)
            {
                _trace?.Write("SUPPRESS-ERROR",
                    $"type={fish.FishDataTID} error={exception.GetType().Name}:{exception.Message}");
            }
        }
        return count;
    }

    private void RestoreSuppressedClientFish()
    {
        foreach (var fish in _suppressedClientFish)
        {
            try
            {
                if (fish != null)
                    fish.gameObject.SetActive(true);
            }
            catch
            {
            }
        }
        _suppressedClientFish.Clear();
    }

    private FishAISystem TakeUnboundFish(FishManifest manifest)
    {
        FishAISystem best = null;
        var bestScore = float.NegativeInfinity;
        var expected = new Vector3(manifest.X, manifest.Y, manifest.Z);
        foreach (var fish in UnityEngine.Object.FindObjectsByType<FishAISystem>(FindObjectsSortMode.None))
        {
            if (fish == null || !fish.gameObject.activeInHierarchy || _clientIdsByFish.ContainsKey(fish))
                continue;
            if (fish.FishDataTID != manifest.FishDataTID)
                continue;
            var score = -Vector3.SqrMagnitude(fish.transform.position - expected);
            if (score > bestScore)
            {
                best = fish;
                bestScore = score;
            }
        }
        return best;
    }

    private bool AllCurrentManifestsQueued()
    {
        if (_manifestRevision == 0)
            return false;
        foreach (var info in _hostInfoById.Values)
            if (!info.ManifestQueued)
                return false;
        return true;
    }

    private static bool ShouldSendSnapshot(HostFish info, FishSnapshot snapshot, float now)
    {
        if (!info.HasSnapshot || now >= info.LastSnapshotSend + SnapshotKeyframeSeconds)
            return true;
        var previous = info.LastSnapshot;
        var dx = snapshot.X - previous.X;
        var dy = snapshot.Y - previous.Y;
        var dz = snapshot.Z - previous.Z;
        var dvx = snapshot.VelocityX - previous.VelocityX;
        var dvy = snapshot.VelocityY - previous.VelocityY;
        return dx * dx + dy * dy + dz * dz >= 0.0025f ||
            Mathf.Abs(Mathf.DeltaAngle(previous.Rotation, snapshot.Rotation)) >= 1f ||
            dvx * dvx + dvy * dvy >= 0.04f ||
            Mathf.Abs(snapshot.Hp - previous.Hp) >= 0.01f || snapshot.Flags != previous.Flags;
    }

    private static uint NextRevision(uint value) => value == uint.MaxValue ? 1 : value + 1;

    private static bool IsNewer(uint candidate, uint previous) =>
        unchecked((int)(candidate - previous)) > 0;

    private void RemoveClientTarget(int id, bool destroy)
    {
        if (!_targets.TryGetValue(id, out var target))
            return;
        _targets.Remove(id);
        _pendingClientManifests.Remove(id);
        if (target.Fish == null)
            return;
        _clientIdsByFish.Remove(target.Fish);
        if (destroy)
        {
            try
            {
                ReleaseClientHook(target.Fish, id);
                target.Fish.DestroySelf();
            }
            catch (Exception exception)
            {
                _log.LogWarning($"Network fish proxy removal failed: id={id}; {exception.Message}");
                _trace?.Write("REMOVE-ERROR",
                    $"id={id} error={exception.GetType().Name}:{exception.Message}");
            }
        }
        else
        {
            SetClientSimulation(target.Fish, true, id);
        }
        _trace?.Write("REMOVE", $"id={id} destroy={destroy}");
    }

    private void ReleaseClientHook(FishAISystem fish, int id)
    {
        if (!fish.IsFishHooked && !fish.IsFishHookedSequence)
            return;
        try
        {
            fish.lastHarpoonProjectile?.TryReleaseHarpoon();
            fish.OnForceLoseFromProjectile();
            fish.OnEndHookedMode();
            _trace?.Write("HOOK-RELEASE", $"id={id}");
        }
        catch (Exception exception)
        {
            _trace?.Write("HOOK-RELEASE-ERROR",
                $"id={id} error={exception.GetType().Name}:{exception.Message}");
        }
    }

    private void SetClientSimulation(FishAISystem fish, bool enabled, int id)
    {
        try
        {
            fish.EnableBehaviorTree(enabled);
        }
        catch (Exception exception)
        {
            _trace?.Write("SIMULATION-ERROR",
                $"id={id} enabled={enabled} error={exception.GetType().Name}:{exception.Message}");
        }
    }

    private void RemoveHostFish(int id)
    {
        if (_hostFishById.TryGetValue(id, out var fish))
            _hostIdsByFish.Remove(fish);
        _hostFishById.Remove(id);
        _hostInfoById.Remove(id);
        _lastRemoteDamageById.Remove(id);
        _nextRemoteStimulusById.Remove(id);
        _remoteStimulatedIds.Remove(id);
    }

    private int TakeHostId()
    {
        while (_nextHostId <= 0 || _hostFishById.ContainsKey(_nextHostId))
        {
            _nextHostId++;
            if (_nextHostId <= 0)
                _nextHostId = 1;
        }
        return _nextHostId++;
    }

    private static string GetNetworkAllocatorUid(uint sceneId, FishAllocator allocator) =>
        $"A{WorldObjectId.For(sceneId, allocator):X8}";

    private static byte BuildFlags(FishAISystem fish)
    {
        byte flags = 0;
        if (fish.IsCorpse)
            flags |= 1;
        if (fish.IsFishCaptured)
            flags |= 2;
        if (fish.IsFishEnable)
            flags |= 4;
        return flags;
    }

    private static bool TryGetRemotePlayer(
        UdpSession session,
        uint sceneId,
        float now,
        out PlayerSnapshot remotePlayer) =>
        session.TryGetFreshRemotePlayerSnapshot(now, 0.75f, out remotePlayer) &&
            remotePlayer.SceneId == sceneId;

    private static bool InRange(Vector3 position, PlayerSnapshot player, float maxSquaredDistance)
    {
        var dx = position.x - player.X;
        var dy = position.y - player.Y;
        return dx * dx + dy * dy <= maxSquaredDistance;
    }

    internal static bool IsPlayerAttack(AttackType attackType) =>
        attackType is AttackType.Player_All or AttackType.Player_Gun or
            AttackType.Player_Harpoon or AttackType.Player_Melee or
            AttackType.Player_Harpoon_Interaction or AttackType.QTE_Damage or
            AttackType.Player_SubHelper;

    private static void ApplyFlags(Target target)
    {
        if (target.Fish == null)
            return;
        var corpse = (target.Flags & 1) != 0;
        var captured = (target.Flags & 2) != 0;
        var enabled = (target.Flags & 4) != 0;
        if (target.Fish.IsCorpse != corpse)
            target.Fish.IsCorpse = corpse;
        if (target.Fish.IsFishCaptured != captured)
            target.Fish.IsFishCaptured = captured;
        if (target.Fish.IsFishEnable != enabled)
            target.Fish.IsFishEnable = enabled;
    }
}

[HarmonyPatch(typeof(FishAISystem), nameof(FishAISystem.SetHPDamage))]
internal static class FishDamagePatch
{
    private static bool Prefix(
        FishAISystem __instance,
        int damage,
        EElement element,
        AttackType attackType)
    {
        var behaviour = ProbeBehaviour.Instance;
        return behaviour?.AllowFishDamage(__instance, damage, element, attackType) ?? true;
    }
}

[HarmonyPatch(typeof(FishAISystem), nameof(FishAISystem.OnTakeDamage))]
internal static class FishTakeDamagePatch
{
    private static bool Prefix(
        FishAISystem __instance,
        AttackData __0,
        ref bool __result,
        out bool __state)
    {
        var behaviour = ProbeBehaviour.Instance;
        if (behaviour == null)
        {
            __state = false;
            return true;
        }
        var allow = behaviour.BeginFishDamage(__instance, __0, out __state);
        if (!allow)
            __result = false;
        return allow;
    }

    private static void Postfix(FishAISystem __instance, bool __state) =>
        ProbeBehaviour.Instance?.EndFishDamage(__instance, __state);
}

[HarmonyPatch(typeof(FishAISystem), nameof(FishAISystem.SetTrueHPDamage))]
internal static class FishTrueDamagePatch
{
    private static bool Prefix(FishAISystem __instance) =>
        ProbeBehaviour.Instance?.AllowFishTrueDamage(__instance) ?? true;
}

[HarmonyPatch(typeof(FishAISystem), nameof(FishAISystem.SetHPDamageQTE))]
internal static class FishQteDamagePatch
{
    private static bool Prefix(FishAISystem __instance, int damage, EElement element)
    {
        var behaviour = ProbeBehaviour.Instance;
        return behaviour?.AllowFishDamage(
            __instance, damage, element, AttackType.QTE_Damage) ?? true;
    }
}

[HarmonyPatch(typeof(FishAISystem), nameof(FishAISystem.WinFromProjectileinFight))]
internal static class FishHarpoonWinPatch
{
    private static void Prefix(FishAISystem __instance) =>
        ProbeBehaviour.Instance?.OnFishCaptureWon(__instance);
}

[HarmonyPatch(typeof(FishInteractionBody), nameof(FishInteractionBody.SuccessInteract))]
internal static class FishPickupPatch
{
    private static bool Prefix(
        FishInteractionBody __instance,
        BaseCharacter __0,
        out FishAISystem __state)
    {
        __state = __instance?.GetComponentInParent<FishAISystem>();
        return ProbeBehaviour.Instance?.AllowFishPickup(__instance, __0) ?? true;
    }

    private static void Postfix(FishAISystem __state) =>
        ProbeBehaviour.Instance?.OnFishPickupSucceeded(__state);
}

[HarmonyPatch(typeof(FishInteractionBody), nameof(FishInteractionBody.CheckAvailableInteraction))]
internal static class FishInteractionAvailabilityPatch
{
    private static void Postfix(FishInteractionBody __instance, ref bool __result)
    {
        if (__result)
            __result = ProbeBehaviour.Instance?.AllowFishInteraction(__instance) ?? true;
    }
}

[HarmonyPatch(typeof(FishAISystem), "Update")]
internal static class ClientFishUpdatePatch
{
    private static bool Prefix(FishAISystem __instance) =>
        ProbeBehaviour.Instance?.AllowFishSimulation(__instance) ?? true;
}

[HarmonyPatch(typeof(FishAISystem), "LateUpdate")]
internal static class ClientFishLateUpdatePatch
{
    private static bool Prefix(FishAISystem __instance) =>
        ProbeBehaviour.Instance?.AllowFishSimulation(__instance) ?? true;
}
