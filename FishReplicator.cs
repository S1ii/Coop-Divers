using System;
using System.Collections.Generic;
using BepInEx.Logging;
using DR.AI;
using UnityEngine;

namespace DaveTheDiverMP;

internal sealed class FishReplicator
{
    private sealed class Target
    {
        internal FishAISystem Fish;
        internal Vector3 Position;
        internal float Rotation;
        internal byte Flags;
        internal bool HasSnapshot;
    }

    private readonly ManualLogSource _log;
    private readonly List<FishAISystem> _hostFishes = new();
    private readonly Dictionary<int, Target> _targets = new();
    private readonly HashSet<int> _missedIds = new();
    private float _nextHostScan;
    private float _nextClientScan;
    private float _nextSend;
    private int _lastHostCount = -1;
    private int _lastHostDuplicates = -1;
    private int _lastClientCount = -1;
    private int _lastClientDuplicates = -1;
    private int _lastClientMissing = -1;

    internal FishReplicator(ManualLogSource log) => _log = log;

    internal void Update(
        SessionRole role,
        UdpSession session,
        uint sceneId,
        float now,
        float deltaTime)
    {
        if (role == SessionRole.Host)
        {
            while (session.TryTakeFishSnapshot(out _))
            {
            }
            UpdateHost(session, sceneId, now);
            return;
        }

        if (role != SessionRole.Client || !session.SceneMatches(sceneId))
        {
            while (session.TryTakeFishSnapshot(out _))
            {
            }
            _targets.Clear();
            return;
        }

        if (now >= _nextClientScan)
            RefreshClientFish(now);

        while (session.TryTakeFishSnapshot(out var snapshot))
        {
            if (snapshot.SceneId != sceneId)
                continue;
            if (!_targets.TryGetValue(snapshot.Id, out var target))
            {
                _missedIds.Add(snapshot.Id);
                continue;
            }

            target.Position = new Vector3(snapshot.X, snapshot.Y, snapshot.Z);
            target.Rotation = snapshot.Rotation;
            target.Flags = snapshot.Flags;
            target.HasSnapshot = true;
            ApplyFlags(target);
        }

        var blend = 1f - Mathf.Exp(-14f * deltaTime);
        foreach (var target in _targets.Values)
        {
            if (!target.HasSnapshot || target.Fish == null)
                continue;
            // Soft authority keeps client attacks/animations alive;
            // disable client AI once host-side combat events are replicated.
            target.Fish.transform.position = Vector3.Lerp(
                target.Fish.transform.position, target.Position, blend);
            target.Fish.Rotation = Mathf.LerpAngle(
                target.Fish.Rotation, target.Rotation, blend);
        }
    }

    internal void Clear()
    {
        _hostFishes.Clear();
        _targets.Clear();
        _missedIds.Clear();
        _nextHostScan = 0f;
        _nextClientScan = 0f;
        _nextSend = 0f;
        _lastHostCount = -1;
        _lastHostDuplicates = -1;
        _lastClientCount = -1;
        _lastClientDuplicates = -1;
        _lastClientMissing = -1;
    }

    private void UpdateHost(UdpSession session, uint sceneId, float now)
    {
        if (!session.SceneMatches(sceneId))
            return;
        if (now >= _nextHostScan)
            RefreshHostFish(now);
        if (now < _nextSend)
            return;

        _nextSend = now + 0.2f;
        foreach (var fish in _hostFishes)
        {
            if (fish == null || !fish.gameObject.activeInHierarchy)
                continue;
            var position = fish.transform.position;
            byte flags = 0;
            if (fish.IsCorpse)
                flags |= 1;
            if (fish.IsFishCaptured)
                flags |= 2;
            if (fish.IsFishEnable)
                flags |= 4;
            session.SendFishSnapshot(new FishSnapshot(
                sceneId, fish.ID, position.x, position.y, position.z, fish.Rotation, flags));
        }
    }

    private void RefreshHostFish(float now)
    {
        _nextHostScan = now + 1f;
        _hostFishes.Clear();
        var ids = new HashSet<int>();
        var duplicates = new HashSet<int>();
        foreach (var fish in UnityEngine.Object.FindObjectsByType<FishAISystem>(FindObjectsSortMode.None))
        {
            if (fish == null)
                continue;
            if (!ids.Add(fish.ID))
                duplicates.Add(fish.ID);
            _hostFishes.Add(fish);
        }
        if (duplicates.Count > 0)
            _hostFishes.RemoveAll(fish => fish == null || duplicates.Contains(fish.ID));
        if (_lastHostCount != _hostFishes.Count || _lastHostDuplicates != duplicates.Count)
        {
            _log.LogInfo(
                $"Network fish host: {_hostFishes.Count} unique; {duplicates.Count} duplicate IDs");
            _lastHostCount = _hostFishes.Count;
            _lastHostDuplicates = duplicates.Count;
        }
    }

    private void RefreshClientFish(float now)
    {
        _nextClientScan = now + 1f;
        var fishes = new Dictionary<int, FishAISystem>();
        var duplicates = new HashSet<int>();
        foreach (var fish in UnityEngine.Object.FindObjectsByType<FishAISystem>(FindObjectsSortMode.None))
        {
            if (fish == null)
                continue;
            if (!fishes.TryAdd(fish.ID, fish))
                duplicates.Add(fish.ID);
        }
        foreach (var duplicate in duplicates)
            fishes.Remove(duplicate);

        var nextTargets = new Dictionary<int, Target>(fishes.Count);
        foreach (var pair in fishes)
        {
            if (_targets.TryGetValue(pair.Key, out var target) && target.Fish == pair.Value)
                nextTargets.Add(pair.Key, target);
            else
                nextTargets.Add(pair.Key, new Target { Fish = pair.Value });
        }
        _targets.Clear();
        foreach (var pair in nextTargets)
            _targets.Add(pair.Key, pair.Value);

        if (_lastClientCount != _targets.Count ||
            _lastClientDuplicates != duplicates.Count || _lastClientMissing != _missedIds.Count)
        {
            _log.LogInfo(
                $"Network fish client: {_targets.Count} unique; {duplicates.Count} duplicate IDs; " +
                $"{_missedIds.Count} host IDs missing");
            _lastClientCount = _targets.Count;
            _lastClientDuplicates = duplicates.Count;
            _lastClientMissing = _missedIds.Count;
            _missedIds.Clear();
        }
    }

    private static void ApplyFlags(Target target)
    {
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
