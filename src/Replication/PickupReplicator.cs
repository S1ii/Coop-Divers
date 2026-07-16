using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace DaveTheDiverMP;

internal sealed class PickupReplicator
{
    private readonly ManualLogSource _log;
    private readonly SessionTrace _trace;
    private readonly RemoteCatchLedger _ledger;
    private readonly Dictionary<uint, PickupInstanceItem> _items = new();
    private readonly Dictionary<uint, PickupRemoved> _pending = new();
    private readonly Dictionary<uint, PickupRemoved> _hostPending = new();
    private readonly HashSet<uint> _applyingRemoteRemovals = new();
    private float _nextScan;
    private int _lastItemCount = -1;
    private int _lastDuplicateCount = -1;

    internal PickupReplicator(
        ManualLogSource log,
        SessionTrace trace,
        RemoteCatchLedger ledger)
    {
        _log = log;
        _trace = trace;
        _ledger = ledger;
    }

    internal static void SelfTest()
    {
        var patchFlags = BindingFlags.NonPublic | BindingFlags.Static;
        var finalizer = typeof(PickupInteractPatch).GetMethod("Finalizer", patchFlags);
        if (!MatchesRemotePickup(42, 42, 2f, 2f) ||
            MatchesRemotePickup(42, 43, 0f, 0f) ||
            MatchesRemotePickup(42, 42, 5f, 0f) ||
            finalizer == null || finalizer.ReturnType != typeof(Exception) ||
            finalizer.GetParameters().Length != 1 ||
            finalizer.GetParameters()[0].ParameterType != typeof(Exception) ||
            typeof(PickupInteractPatch).GetMethod("Postfix", patchFlags) != null)
            throw new InvalidOperationException("Remote pickup matching failed");
    }

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
            _hostPending.Clear();
            return;
        }

        if (now >= _nextScan)
            Refresh(sceneId, now);

        if (role == SessionRole.Host)
        {
            FlushHostPending(session);
            while (session.TryTakePickupRemoved(out var removed))
                ApplyHostRemoval(session, sceneId, now, hostPlayer, removed);
            while (session.TryTakePickupRequest(out _))
            {
            }
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

    internal bool RequestPickup(UdpSession session, uint sceneId, PickupInstanceItem item)
        => item != null;

    internal void OnDestroyed(UdpSession session, uint sceneId, PickupInstanceItem item)
    {
        if (item == null)
            return;
        var itemId = ItemId(item);
        if (itemId <= 0)
            return;
        var worldId = WorldId(sceneId, item);
        if (_applyingRemoteRemovals.Contains(worldId))
            return;
        _items.Remove(worldId);
        var removed = new PickupRemoved(sceneId, worldId, itemId);
        if (!session.SendPickupRemoved(removed))
            _hostPending[worldId] = removed;
    }

    internal void Clear()
    {
        _items.Clear();
        _pending.Clear();
        _hostPending.Clear();
        _applyingRemoteRemovals.Clear();
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
            if (ItemId(item) <= 0)
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

    private void FlushHostPending(UdpSession session)
    {
        if (_hostPending.Count == 0 || session.ReliableCapacityRemaining == 0)
            return;
        var sent = new List<uint>();
        foreach (var pair in _hostPending)
        {
            if (!session.SendPickupRemoved(pair.Value))
                break;
            sent.Add(pair.Key);
        }
        foreach (var worldId in sent)
            _hostPending.Remove(worldId);
    }

    private bool TryApply(PickupRemoved removed)
    {
        if (!_items.TryGetValue(removed.WorldId, out var item) ||
            item == null || ItemId(item) != removed.ItemId)
            return false;
        _items.Remove(removed.WorldId);
        _applyingRemoteRemovals.Add(removed.WorldId);
        try
        {
            item.DestroyItem();
        }
        finally
        {
            _applyingRemoteRemovals.Remove(removed.WorldId);
        }
        _trace?.Write("ITEM-APPLY",
            $"world={removed.WorldId:X8} item={removed.ItemId}");
        return true;
    }

    private void ApplyHostRemoval(
        UdpSession session,
        uint sceneId,
        float now,
        PlayerCharacter hostPlayer,
        PickupRemoved removed)
    {
        if (removed.SceneId != sceneId ||
            !session.TryGetFreshRemotePlayerSnapshot(now, 0.75f, out var remotePlayer) ||
            remotePlayer.SceneId != sceneId)
        {
            _trace?.Write("ITEM-REJECT", $"world={removed.WorldId:X8} reason=player-or-scene");
            return;
        }

        if (!_items.TryGetValue(removed.WorldId, out var item) || ItemId(item) != removed.ItemId)
        {
            Refresh(sceneId, now);
            _items.TryGetValue(removed.WorldId, out item);
        }
        if (item == null || ItemId(item) != removed.ItemId)
        {
            item = null;
            var nearestDistance = 16f;
            foreach (var candidate in UnityEngine.Object.FindObjectsByType<PickupInstanceItem>(
                         FindObjectsSortMode.None))
            {
                if (candidate == null || ItemId(candidate) != removed.ItemId)
                    continue;
                var position = candidate.transform.position;
                var candidateDx = position.x - remotePlayer.X;
                var candidateDy = position.y - remotePlayer.Y;
                var distance = candidateDx * candidateDx + candidateDy * candidateDy;
                if (distance >= nearestDistance)
                    continue;
                nearestDistance = distance;
                item = candidate;
            }
        }
        if (item == null)
        {
            _trace?.Write("ITEM-REJECT", $"world={removed.WorldId:X8} reason=missing-or-id");
            return;
        }

        var itemPosition = item.transform.position;
        var dx = itemPosition.x - remotePlayer.X;
        var dy = itemPosition.y - remotePlayer.Y;
        if (!MatchesRemotePickup(removed.ItemId, ItemId(item), dx, dy))
        {
            _log.LogWarning($"Network pickup rejected: {removed.WorldId:X8} is out of range");
            return;
        }

        var worldId = WorldId(sceneId, item);
        _applyingRemoteRemovals.Add(worldId);
        try
        {
            if (hostPlayer == null || !_ledger.CapturePickup(
                    RemoteCatchLedger.PickupSource(sceneId, worldId), removed.ItemId, 1,
                    () => item.SuccessInteract(hostPlayer), out _))
            {
                _trace?.Write("ITEM-REJECT", $"world={worldId:X8} reason=no-loot-evidence");
                return;
            }
        }
        finally
        {
            _applyingRemoteRemovals.Remove(worldId);
        }
        _items.Remove(worldId);
        _trace?.Write("ITEM-REMOTE-REMOVE",
            $"world={worldId:X8} requested={removed.WorldId:X8} item={removed.ItemId}");
    }

    internal static uint WorldId(uint sceneId, PickupInstanceItem item)
        => WorldObjectId.For(sceneId, item, ItemId(item));

    private static int ItemId(PickupInstanceItem item)
    {
        try
        {
            return item?.GetItemID() ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    private static bool MatchesRemotePickup(
        int expectedItemId,
        int actualItemId,
        float dx,
        float dy) =>
        expectedItemId > 0 && expectedItemId == actualItemId && dx * dx + dy * dy <= 16f;
}

[HarmonyPatch(typeof(PickupInstanceItem), nameof(PickupInstanceItem.DestroyItem))]
internal static class PickupDestroyPatch
{
    private static void Prefix(PickupInstanceItem __instance) =>
        ProbeBehaviour.Instance?.OnPickupDestroyed(__instance);
}

[HarmonyPatch]
internal static class PickupInteractPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var type in AccessTools.GetTypesFromAssembly(typeof(PickupInstanceItem).Assembly))
        {
            if (type != typeof(PickupInstanceItem) && !type.IsSubclassOf(typeof(PickupInstanceItem)))
                continue;
            var method = type.GetMethod(
                nameof(PickupInstanceItem.SuccessInteract),
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.DeclaredOnly,
                null, new[] { typeof(BaseCharacter) }, null);
            if (method != null)
                yield return method;
        }
    }

    private static bool Prefix(PickupInstanceItem __instance) =>
        ProbeBehaviour.Instance?.OnPickupInteract(__instance) ?? true;

    private static Exception Finalizer(Exception __exception)
    {
        ProbeBehaviour.Instance?.EndClientLootSource();
        return __exception;
    }
}
