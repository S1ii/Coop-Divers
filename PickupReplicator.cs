using System.Collections.Generic;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace DaveTheDiverMP;

internal sealed class PickupReplicator
{
    private readonly ManualLogSource _log;
    private readonly Dictionary<uint, PickupInstanceItem> _items = new();
    private readonly Dictionary<uint, PickupRemoved> _pending = new();
    private float _nextScan;
    private int _lastItemCount = -1;
    private int _lastDuplicateCount = -1;

    internal PickupReplicator(ManualLogSource log) => _log = log;

    internal void Update(
        SessionRole role,
        UdpSession session,
        uint sceneId,
        float now,
        PlayerCharacter hostPlayer)
    {
        if (role == SessionRole.Offline || !session.SceneMatches(sceneId))
        {
            while (session.TryTakePickupRemoved(out _))
            {
            }
            while (session.TryTakePickupRequest(out _))
            {
            }
            _items.Clear();
            _pending.Clear();
            return;
        }

        if (now >= _nextScan)
            Refresh(sceneId, now);

        if (role == SessionRole.Host)
        {
            while (session.TryTakePickupRemoved(out _))
            {
            }
            while (session.TryTakePickupRequest(out var request))
                ApplyHostRequest(session, sceneId, now, hostPlayer, request);
            return;
        }

        while (session.TryTakePickupRequest(out _))
        {
        }

        while (session.TryTakePickupRemoved(out var removed))
        {
            if (removed.SceneId != sceneId)
                continue;
            if (!TryApply(removed))
                _pending[removed.WorldId] = removed;
        }
    }

    internal void RequestPickup(UdpSession session, uint sceneId, PickupInstanceItem item)
    {
        if (item == null)
            return;
        session.SendPickupRequest(new PickupRemoved(
            sceneId, WorldId(sceneId, item), item.GetItemID()));
    }

    internal void OnHostDestroyed(UdpSession session, uint sceneId, PickupInstanceItem item)
    {
        if (item == null)
            return;
        var worldId = WorldId(sceneId, item);
        _items.Remove(worldId);
        session.SendPickupRemoved(new PickupRemoved(sceneId, worldId, item.GetItemID()));
    }

    internal void Clear()
    {
        _items.Clear();
        _pending.Clear();
        _nextScan = 0f;
        _lastItemCount = -1;
        _lastDuplicateCount = -1;
    }

    private void Refresh(uint sceneId, float now)
    {
        _nextScan = now + 1f;
        _items.Clear();
        var duplicates = new HashSet<uint>();
        foreach (var item in UnityEngine.Object.FindObjectsByType<PickupInstanceItem>(FindObjectsSortMode.None))
        {
            if (item == null)
                continue;
            var worldId = WorldId(sceneId, item);
            if (!_items.TryAdd(worldId, item))
                duplicates.Add(worldId);
        }
        foreach (var duplicate in duplicates)
            _items.Remove(duplicate);

        if (_lastItemCount != _items.Count || _lastDuplicateCount != duplicates.Count)
        {
            _log.LogInfo($"Network pickups: {_items.Count} unique; {duplicates.Count} duplicate IDs");
            _lastItemCount = _items.Count;
            _lastDuplicateCount = duplicates.Count;
        }

        if (_pending.Count == 0)
            return;
        var applied = new List<uint>();
        foreach (var pair in _pending)
        {
            if (TryApply(pair.Value))
                applied.Add(pair.Key);
        }
        foreach (var worldId in applied)
            _pending.Remove(worldId);
    }

    private bool TryApply(PickupRemoved removed)
    {
        if (!_items.TryGetValue(removed.WorldId, out var item) ||
            item == null || item.GetItemID() != removed.ItemId)
            return false;
        _items.Remove(removed.WorldId);
        item.DestroyItem();
        return true;
    }

    private void ApplyHostRequest(
        UdpSession session,
        uint sceneId,
        float now,
        PlayerCharacter hostPlayer,
        PickupRemoved request)
    {
        if (request.SceneId != sceneId || hostPlayer == null ||
            !session.TryGetFreshRemotePlayerSnapshot(now, 0.75f, out var remotePlayer) ||
            remotePlayer.SceneId != sceneId)
            return;

        if (!_items.TryGetValue(request.WorldId, out var item))
        {
            Refresh(sceneId, now);
            _items.TryGetValue(request.WorldId, out item);
        }
        if (item == null || item.GetItemID() != request.ItemId)
            return;

        var itemPosition = item.transform.position;
        var dx = itemPosition.x - remotePlayer.X;
        var dy = itemPosition.y - remotePlayer.Y;
        if (dx * dx + dy * dy > 16f)
        {
            _log.LogWarning($"Network pickup rejected: {request.WorldId:X8} is out of range");
            return;
        }

        _items.Remove(request.WorldId);
        item.SuccessInteract(hostPlayer);
    }

    private static uint WorldId(uint sceneId, PickupInstanceItem item)
    {
        var hash = Mix(Mix(2166136261u, unchecked((int)sceneId)), item.GetItemID());
        for (var current = item.transform; current != null; current = current.parent)
        {
            hash = Mix(hash, unchecked((int)Protocol.SceneId(current.name)));
            if (current.parent != null)
                hash = Mix(hash, current.GetSiblingIndex());
        }
        // Hierarchy IDs cover deterministic scene pickups; dynamic
        // host spawns will receive explicit network IDs with spawn replication.
        return hash;
    }

    private static uint Mix(uint hash, int value)
    {
        unchecked
        {
            for (var shift = 0; shift < 32; shift += 8)
            {
                hash ^= (byte)(value >> shift);
                hash *= 16777619u;
            }
            return hash;
        }
    }
}

[HarmonyPatch(typeof(PickupInstanceItem), nameof(PickupInstanceItem.DestroyItem))]
internal static class PickupDestroyPatch
{
    private static void Prefix(PickupInstanceItem __instance) =>
        ProbeBehaviour.Instance?.OnPickupDestroyed(__instance);
}

[HarmonyPatch(typeof(PickupInstanceItem), nameof(PickupInstanceItem.SuccessInteract))]
internal static class PickupInteractPatch
{
    private static bool Prefix(PickupInstanceItem __instance) =>
        ProbeBehaviour.Instance?.OnPickupInteract(__instance) ?? true;
}
