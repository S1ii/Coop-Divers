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
    private readonly RemoteCatchLedger _remoteCatch;
    private readonly Dictionary<int, FishAISystem> _hostFishById = new();
    private readonly Dictionary<FishAISystem, int> _hostIdsByFish = new();
    private readonly Dictionary<int, HostFish> _hostInfoById = new();
    private readonly Dictionary<int, Target> _targets = new();
    private readonly Dictionary<FishAISystem, int> _clientIdsByFish = new();
    private readonly HashSet<int> _removedIds = new();
    private readonly HashSet<int> _missingAllocatorIds = new();
    private readonly HashSet<int> _fallbackAllocatorIds = new();
    private readonly Dictionary<uint, HashSet<int>> _clientManifestIds = new();
    private readonly Dictionary<uint, ushort> _clientManifestCounts = new();
    private readonly Dictionary<uint, ushort> _finalizedClientManifestCounts = new();
    private readonly HashSet<FishAllocator> _stoppedClientAllocators = new();
    private readonly List<FishSnapshot> _snapshotBuffer = new();
    private float _nextHostScan;
    private float _nextSend;
    private float _nextClientAllocatorScan;
    private bool _manifestStateQueued;
    private int _nextHostId = 1;
    private int _lastHostCount = -1;
    private int _lastHostDuplicates = -1;
    private uint _manifestRevision;
    private uint _fishTick;
    private uint _latestClientManifestRevision;

    internal FishReplicator(ManualLogSource log, RemoteCatchLedger remoteCatch)
    {
        _log = log;
        _remoteCatch = remoteCatch;
    }

    internal void Update(
        SessionRole role,
        UdpSession session,
        uint sceneId,
        float now,
        float deltaTime,
        PlayerCharacter hostPlayer)
    {
        if (role == SessionRole.Host)
        {
            while (session.TryTakeFishSnapshot(out _))
            {
            }
            while (session.TryTakeFishRemoved(out _))
            {
            }
            while (session.TryTakeFishManifest(out _))
            {
            }
            while (session.TryTakeFishManifestState(out _))
            {
            }
            UpdateHost(session, sceneId, now, hostPlayer);
            return;
        }

        while (session.TryTakeFishDamageRequest(out _))
        {
        }
        while (session.TryTakeFishPickupRequest(out _))
        {
        }

        if (role == SessionRole.Client)
            StopClientAllocators(now);

        if (role != SessionRole.Client || !session.SceneMatches(sceneId))
        {
            while (session.TryTakeFishSnapshot(out _))
            {
            }
            while (session.TryTakeFishRemoved(out _))
            {
            }
            while (session.TryTakeFishManifest(out _))
            {
            }
            while (session.TryTakeFishManifestState(out _))
            {
            }
            DestroyClientTargets();
            ResetClientManifest();
            return;
        }

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
    }

    internal bool RequestDamage(
        UdpSession session,
        uint sceneId,
        FishAISystem fish,
        int damage,
        EElement element,
        AttackType attackType)
    {
        if (fish == null || damage <= 0 || !IsPlayerAttack(attackType) ||
            !_clientIdsByFish.TryGetValue(fish, out var id))
            return false;
        session.SendFishDamageRequest(new FishDamageRequest(
            sceneId, id, Mathf.Clamp(damage, 1, 10_000), (int)element, (int)attackType));
        return true;
    }

    internal void RequestPickup(UdpSession session, uint sceneId, FishAISystem fish)
    {
        if (fish != null && _clientIdsByFish.TryGetValue(fish, out var id))
            session.SendFishPickupRequest(new FishPickupRequest(sceneId, id));
    }

    internal bool IsClientProxy(FishAISystem fish) =>
        fish != null && _clientIdsByFish.ContainsKey(fish);

    internal void Clear()
    {
        DestroyClientTargets();
        ResumeClientAllocators();
        _hostFishById.Clear();
        _hostIdsByFish.Clear();
        _hostInfoById.Clear();
        ResetClientManifest();
        _nextHostScan = 0f;
        _nextSend = 0f;
        _nextClientAllocatorScan = 0f;
        _manifestStateQueued = false;
        _nextHostId = 1;
        _lastHostCount = -1;
        _lastHostDuplicates = -1;
        _manifestRevision = 0;
        _fishTick = 0;
        _latestClientManifestRevision = 0;
    }

    private void UpdateHost(
        UdpSession session,
        uint sceneId,
        float now,
        PlayerCharacter hostPlayer)
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

        while (session.TryTakeFishDamageRequest(out var damageRequest))
            ApplyHostDamage(session, sceneId, now, damageRequest);
        while (session.TryTakeFishPickupRequest(out var pickupRequest))
            ApplyHostPickup(session, sceneId, now, hostPlayer, pickupRequest);

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
        var allocatorUidCounts = new Dictionary<string, int>();
        var duplicateUids = new HashSet<string>();
        foreach (var allocator in UnityEngine.Object.FindObjectsByType<FishAllocator>(FindObjectsSortMode.None))
        {
            if (allocator == null)
                continue;
            var uid = GetAllocatorUid(allocator);
            if (string.IsNullOrEmpty(uid))
                continue;
            allocators.Add(allocator);
            allocatorUidCounts.TryGetValue(uid, out var count);
            allocatorUidCounts[uid] = ++count;
            if (count > 1)
                duplicateUids.Add(uid);
        }

        var allocatorByFish = new Dictionary<FishAISystem, FishAllocator>();
        foreach (var allocator in allocators)
        {
            var fishs = allocator.GetInstancedFishs;
            if (fishs == null)
                continue;
            foreach (var fish in fishs)
            {
                if (fish != null)
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

        if (_lastHostCount != _hostFishById.Count || _lastHostDuplicates != duplicateUids.Count)
        {
            _log.LogInfo(
                $"Network fish host manifest: {_hostFishById.Count} fish; " +
                $"{duplicateUids.Count} duplicate allocator UIDs");
            _lastHostCount = _hostFishById.Count;
            _lastHostDuplicates = duplicateUids.Count;
        }
        if (_manifestRevision == 0 || topologyChanged)
        {
            _manifestRevision = NextRevision(_manifestRevision);
            foreach (var info in _hostInfoById.Values)
                info.ManifestQueued = false;
            _manifestStateQueued = false;
        }
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
        FishDamageRequest request)
    {
        if (request.SceneId != sceneId || !TryGetRemotePlayer(session, sceneId, now, out var remotePlayer))
            return;
        if (!_hostFishById.TryGetValue(request.Id, out var fish))
        {
            RefreshHostFish(session, sceneId, now);
            _hostFishById.TryGetValue(request.Id, out fish);
        }
        if (fish == null || fish.IsCorpse || fish.IsFishCaptured ||
            !Enum.IsDefined(typeof(EElement), request.Element) ||
            !IsPlayerAttack((AttackType)request.AttackType) ||
            !InRange(fish.transform.position, remotePlayer, 1600f))
        {
            _log.LogWarning($"Network fish damage rejected: id={request.Id}");
            return;
        }

        if ((AttackType)request.AttackType == AttackType.QTE_Damage)
            fish.SetHPDamageQTE(request.Damage, (EElement)request.Element);
        else
            fish.SetHPDamage(request.Damage, (EElement)request.Element, (AttackType)request.AttackType);
        _log.LogDebug($"Network fish damage: id={request.Id}; damage={request.Damage}; hp={fish.HP:F1}");
    }

    private void ApplyHostPickup(
        UdpSession session,
        uint sceneId,
        float now,
        PlayerCharacter hostPlayer,
        FishPickupRequest request)
    {
        if (request.SceneId != sceneId || hostPlayer == null ||
            !TryGetRemotePlayer(session, sceneId, now, out var remotePlayer))
            return;
        if (!_hostFishById.TryGetValue(request.Id, out var fish))
        {
            RefreshHostFish(session, sceneId, now);
            _hostFishById.TryGetValue(request.Id, out fish);
        }
        var body = fish != null ? fish.GetInteractionBody : null;
        if (fish == null || body == null || !body.IsEnableInteraction ||
            body.InteractionType != FishInteractionBody.FishInteractionType.Pickup ||
            !InRange(fish.transform.position, remotePlayer, 16f))
        {
            _log.LogWarning($"Network fish pickup rejected: id={request.Id}");
            return;
        }

        var sourceId = RemoteCatchLedger.FishSource(sceneId, request.Id);
        var expectedItemId = fish.GetDropItemID();
        if (!_remoteCatch.CaptureFish(
                sourceId,
                expectedItemId,
                () => body.SuccessInteract(null),
                out var nativeCompleted))
        {
            if (nativeCompleted && (fish == null || !fish.gameObject.activeInHierarchy))
            {
                session.SendFishRemoved(new FishRemoved(sceneId, request.Id));
                RemoveHostFish(request.Id);
            }
            _log.LogWarning($"Network fish pickup could not enter remote carry: id={request.Id}");
            return;
        }
        if (!nativeCompleted && fish != null)
            fish.DestroySelf();

        session.SendFishRemoved(new FishRemoved(sceneId, request.Id));
        RemoveHostFish(request.Id);
        _log.LogInfo($"Network fish pickup accepted: id={request.Id}");
    }

    private void ApplyClientManifest(FishManifest manifest, bool trackManifest = true)
    {
        if (_removedIds.Contains(manifest.Id))
            return;
        if (_targets.TryGetValue(manifest.Id, out var existing) && existing.Fish != null)
        {
            if (trackManifest)
                MarkClientManifestEntry(manifest.Revision, manifest.Id);
            return;
        }
        if (existing != null)
            RemoveClientTarget(manifest.Id, false);

        // Host and client generate equivalent fish, but their hierarchy paths are
        // not stable. Reuse the client's native fish pool by type and position
        // before attempting an allocator lookup.
        var fish = TakeUnboundFish(manifest);
        if (fish == null)
        {
            var allocator = FindAllocator(manifest);
            if (allocator == null)
            {
                if (_missingAllocatorIds.Add(manifest.Id))
                    _log.LogWarning(
                        $"Network fish manifest waiting for native pool: " +
                        $"id={manifest.Id}; uid={manifest.AllocatorUid}");
                return;
            }
            var prefab = allocator.GetFirstFishPrefab;
            if (prefab == null)
            {
                if (_missingAllocatorIds.Add(manifest.Id))
                    _log.LogWarning($"Network fish manifest allocator has no prefab: id={manifest.Id}");
                return;
            }
            try
            {
                var instance = allocator.DoInstanceFishOrGroup(prefab, null, true);
                fish = instance != null
                    ? instance.GetComponent<FishAISystem>() ?? instance.GetComponentInChildren<FishAISystem>(true)
                    : null;
            }
            catch (Exception exception)
            {
                _log.LogWarning($"Network fish manifest spawn failed: id={manifest.Id}; {exception.Message}");
                return;
            }
        }
        if (fish == null)
            return;

        try
        {
            fish.OverwriteFishData(manifest.FishDataTID);
        }
        catch (Exception exception)
        {
            _log.LogWarning($"Network fish manifest apply failed: id={manifest.Id}; {exception.Message}");
            return;
        }
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
        _targets[manifest.Id] = target;
        _clientIdsByFish[fish] = manifest.Id;
        fish.enabled = false;
        _missingAllocatorIds.Remove(manifest.Id);
        if (trackManifest)
            MarkClientManifestEntry(manifest.Revision, manifest.Id);
        _log.LogDebug($"Network fish manifest bound: id={manifest.Id}; type={manifest.FishDataTID}");
    }

    private void ApplyClientManifestState(FishManifestState state)
    {
        if (_latestClientManifestRevision != 0 && state.Revision != _latestClientManifestRevision &&
            !IsNewer(state.Revision, _latestClientManifestRevision))
            return;
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
        _log.LogInfo(
            $"Network fish manifest applied: revision={revision}; fish={expected}; pruned={pruned}");
    }

    private void StopClientAllocators(float now)
    {
        if (now < _nextClientAllocatorScan)
            return;
        _nextClientAllocatorScan = now + 1f;
        foreach (var allocator in UnityEngine.Object.FindObjectsByType<FishAllocator>(
                     FindObjectsSortMode.None))
        {
            if (allocator == null || !_stoppedClientAllocators.Add(allocator))
                continue;
            try
            {
                allocator.StopAlloc(false);
                if (CurrentClientManifestFinalized(out var ids))
                    PruneAllocator(allocator, ids);
            }
            catch (Exception exception)
            {
                _stoppedClientAllocators.Remove(allocator);
                _log.LogWarning($"Network fish allocator stop failed: {exception.Message}");
            }
        }
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
        foreach (var allocator in new List<FishAllocator>(_stoppedClientAllocators))
            if (allocator != null)
                pruned += PruneAllocator(allocator, manifestIds);
        return pruned;
    }

    private int PruneAllocator(FishAllocator allocator, HashSet<int> manifestIds)
    {
        var fishToRemove = new List<FishAISystem>();
        try
        {
            var fishs = allocator.GetInstancedFishs;
            if (fishs == null)
                return 0;
            foreach (var fish in fishs)
                if (fish != null &&
                    (!_clientIdsByFish.TryGetValue(fish, out var id) || !manifestIds.Contains(id)))
                    fishToRemove.Add(fish);
            foreach (var fish in fishToRemove)
                fish.DestroySelf();
        }
        catch (Exception exception)
        {
            _log.LogWarning($"Network fish prune failed: {exception.Message}");
        }
        return fishToRemove.Count;
    }

    private bool CurrentClientManifestFinalized(out HashSet<int> ids)
    {
        ids = null;
        return _latestClientManifestRevision != 0 &&
            _clientManifestIds.TryGetValue(_latestClientManifestRevision, out ids) &&
            _clientManifestCounts.TryGetValue(_latestClientManifestRevision, out var expected) &&
            _finalizedClientManifestCounts.TryGetValue(
                _latestClientManifestRevision, out var finalized) &&
            finalized == expected && ids.Count >= expected;
    }

    private void DestroyClientTargets()
    {
        foreach (var target in new List<Target>(_targets.Values))
            if (target.Fish != null)
                target.Fish.DestroySelf();
        _targets.Clear();
        _clientIdsByFish.Clear();
    }

    private void ResumeClientAllocators()
    {
        foreach (var allocator in new List<FishAllocator>(_stoppedClientAllocators))
        {
            try
            {
                if (allocator != null && allocator.isActiveAndEnabled)
                    allocator.Spawn();
            }
            catch (Exception exception)
            {
                _log.LogWarning($"Network fish allocator resume failed: {exception.Message}");
            }
        }
        _stoppedClientAllocators.Clear();
    }

    private void ResetClientManifest()
    {
        _removedIds.Clear();
        _missingAllocatorIds.Clear();
        _fallbackAllocatorIds.Clear();
        _clientManifestIds.Clear();
        _clientManifestCounts.Clear();
        _finalizedClientManifestCounts.Clear();
        _latestClientManifestRevision = 0;
    }

    private FishAllocator FindAllocator(FishManifest manifest)
    {
        FishAllocator fallback = null;
        var fallbackScore = float.NegativeInfinity;
        var expectedKey = CanonicalAllocatorKey(manifest.AllocatorUid);
        var expectedPosition = new Vector3(manifest.X, manifest.Y, manifest.Z);
        foreach (var allocator in Resources.FindObjectsOfTypeAll<FishAllocator>())
        {
            if (allocator == null || allocator.gameObject == null ||
                !allocator.gameObject.scene.IsValid())
                continue;
            var uid = GetNetworkAllocatorUid(manifest.SceneId, allocator);
            var score = -Vector3.SqrMagnitude(allocator.transform.position - expectedPosition);
            if (uid == manifest.AllocatorUid)
                score += 1_000_000f;
            else if (CanonicalAllocatorKey(uid) == expectedKey)
                score += 500_000f;
            var prefab = allocator.GetFirstFishPrefab;
            var prefabFish = prefab != null
                ? prefab.GetComponent<FishAISystem>() ?? prefab.GetComponentInChildren<FishAISystem>(true)
                : null;
            if (prefabFish != null && prefabFish.FishDataTID == manifest.FishDataTID)
                score += 100_000f;
            if (score > fallbackScore && score > 50_000f)
            {
                fallbackScore = score;
                fallback = allocator;
            }
        }
        if (fallback != null && _fallbackAllocatorIds.Add(manifest.Id))
            _log.LogInfo(
                $"Network fish allocator fallback: id={manifest.Id}; key={expectedKey}");
        return fallback;
    }

    private static string CanonicalAllocatorKey(string uid)
    {
        if (string.IsNullOrEmpty(uid))
            return string.Empty;
        if (uid.Length > 9 && uid[^9] == '#')
            uid = uid.Substring(0, uid.Length - 9);
        var start = uid.IndexOf("Allocator_", StringComparison.Ordinal);
        if (start < 0)
            return uid;
        var end = uid.LastIndexOf(" (", StringComparison.Ordinal);
        if (end <= start || !uid.EndsWith(")", StringComparison.Ordinal))
            end = uid.Length;
        return uid.Substring(start, end - start);
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
        if (target.Fish == null)
            return;
        _clientIdsByFish.Remove(target.Fish);
        if (destroy)
            target.Fish.DestroySelf();
    }

    private void RemoveHostFish(int id)
    {
        if (_hostFishById.TryGetValue(id, out var fish))
            _hostIdsByFish.Remove(fish);
        _hostFishById.Remove(id);
        _hostInfoById.Remove(id);
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

    private static string GetAllocatorUid(FishAllocator allocator)
    {
        try
        {
            return allocator != null ? allocator.GetAllocatorUID() : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
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

[HarmonyPatch(typeof(FishInteractionBody), nameof(FishInteractionBody.SuccessInteract))]
internal static class FishPickupPatch
{
    private static bool Prefix(FishInteractionBody __instance) =>
        ProbeBehaviour.Instance?.AllowFishPickup(__instance) ?? true;
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
