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
    private readonly RemoteCatchLedger _remoteCatch;
    private readonly SessionTrace _trace;
    private readonly Dictionary<uint, PickupInstanceItem> _items = new();
    private readonly Dictionary<uint, PickupRemoved> _pending = new();
    private readonly Dictionary<uint, PickupRemoved> _hostPending = new();
    private readonly HashSet<uint> _approvedClientPickups = new();
    private float _nextScan;
    private int _lastItemCount = -1;
    private int _lastDuplicateCount = -1;
    private PlayerCharacter _clientPlayer;

    internal PickupReplicator(
        ManualLogSource log,
        RemoteCatchLedger remoteCatch,
        SessionTrace trace)
    {
        _log = log;
        _remoteCatch = remoteCatch;
        _trace = trace;
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
            _approvedClientPickups.Clear();
            _clientPlayer = null;
            return;
        }
        if (role == SessionRole.Client)
            _clientPlayer = hostPlayer;

        if (now >= _nextScan)
            Refresh(sceneId, now);

        if (role == SessionRole.Host)
        {
            FlushHostPending(session);
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
            if (!TryApply(removed, _clientPlayer))
                _pending[removed.WorldId] = removed;
        }
    }

    internal bool RequestPickup(UdpSession session, uint sceneId, PickupInstanceItem item)
    {
        if (item == null)
            return false;
        var worldId = WorldId(sceneId, item);
        if (_approvedClientPickups.Contains(worldId))
            return true;
        session.SendPickupRequest(new PickupRemoved(sceneId, worldId, item.GetItemID()));
        _trace?.Write("ITEM-SEND", $"world={worldId:X8} item={item.GetItemID()}");
        return false;
    }

    internal void OnHostDestroyed(UdpSession session, uint sceneId, PickupInstanceItem item)
    {
        if (item == null)
            return;
        var worldId = WorldId(sceneId, item);
        _items.Remove(worldId);
        var removed = new PickupRemoved(sceneId, worldId, item.GetItemID());
        if (!session.SendPickupRemoved(removed))
            _hostPending[worldId] = removed;
    }

    internal void Clear()
    {
        _items.Clear();
        _pending.Clear();
        _hostPending.Clear();
        _approvedClientPickups.Clear();
        _nextScan = 0f;
        _lastItemCount = -1;
        _lastDuplicateCount = -1;
        _clientPlayer = null;
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
            if (TryApply(pair.Value, _clientPlayer))
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

    private bool TryApply(PickupRemoved removed, PlayerCharacter player)
    {
        if (!_items.TryGetValue(removed.WorldId, out var item) ||
            item == null || item.GetItemID() != removed.ItemId)
            return false;
        _items.Remove(removed.WorldId);
        if (IsPersonalEquipment(item))
        {
            if (player == null)
                return false;
            _approvedClientPickups.Add(removed.WorldId);
            try
            {
                item.SuccessInteract(player);
                return true;
            }
            catch (Exception exception)
            {
                _log.LogWarning($"Network equipment apply failed: {exception.Message}");
                return false;
            }
            finally
            {
                _approvedClientPickups.Remove(removed.WorldId);
            }
        }
        item.DestroyItem();
        player?.SuccessInteraction();
        _trace?.Write("ITEM-APPLY",
            $"world={removed.WorldId:X8} item={removed.ItemId} interaction=completed");
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
        {
            _trace?.Write("ITEM-REJECT", $"world={request.WorldId:X8} reason=player-or-scene");
            return;
        }

        if (!_items.TryGetValue(request.WorldId, out var item))
        {
            Refresh(sceneId, now);
            _items.TryGetValue(request.WorldId, out item);
        }
        if (item == null || item.GetItemID() != request.ItemId)
        {
            _trace?.Write("ITEM-REJECT", $"world={request.WorldId:X8} reason=missing-or-id");
            return;
        }
        var itemPosition = item.transform.position;
        var dx = itemPosition.x - remotePlayer.X;
        var dy = itemPosition.y - remotePlayer.Y;
        if (dx * dx + dy * dy > 16f)
        {
            _log.LogWarning($"Network pickup rejected: {request.WorldId:X8} is out of range");
            return;
        }

        var itemId = item.GetItemID();
        var integrated = DataManager.Instance.GetIntegratedItem(itemId);
        if (integrated == null)
        {
            _log.LogWarning($"Network pickup rejected: unknown item {itemId}");
            return;
        }
        var personalEquipment = integrated.IsEquipmentType() || item.IsUpgradeKit();
        if (personalEquipment)
        {
            item.DestroyItem();
            _items.Remove(request.WorldId);
            _log.LogInfo($"Network personal equipment accepted: {itemId}");
            return;
        }
        if (item.GetType() != typeof(PickupInstanceItem))
        {
            _log.LogWarning(
                $"Network pickup rejected: unsupported item type {item.GetType().Name}");
            return;
        }
        if (integrated.IntegratedType != (int)IntegratedItemType.Loot)
        {
            _log.LogWarning($"Network pickup rejected: unsupported integrated item {itemId}");
            return;
        }

        var raw = DataManager.Instance.GetItems(itemId);
        if (raw == null || raw.CategoryType is not (
                DR.ItemCategoryType.Material or
                DR.ItemCategoryType.Ingredients or
                DR.ItemCategoryType.IngredientFish))
        {
            _log.LogWarning($"Network pickup rejected: unsupported category for {itemId}");
            return;
        }
        var count = Math.Max(1, item.InstanceData.Count);
        var sourceId = RemoteCatchLedger.PickupSource(sceneId, request.WorldId);
        var onStored = item.OnStoredItem;
        if (!_remoteCatch.CapturePickup(
                sourceId, itemId, count,
                () => LootBox.Instance?.Add(
                    itemId, count, 0, LootBox.AutoLiftedType.None, null, true),
                out var nativeCompleted))
        {
            _log.LogWarning($"Network pickup rejected: remote carry is full for {itemId}");
            return;
        }
        if (item != null)
            item.DestroyItem();
        onStored?.Invoke();

        _items.Remove(request.WorldId);
        _log.LogInfo($"Network pickup accepted into remote carry: {itemId} x{count}");
        _trace?.Write("ITEM-ACCEPT",
            $"world={request.WorldId:X8} item={itemId} count={count}");
    }

    private static uint WorldId(uint sceneId, PickupInstanceItem item)
        => WorldObjectId.For(sceneId, item, item.GetItemID());

    private static bool IsPersonalEquipment(PickupInstanceItem item)
    {
        var integrated = DataManager.Instance?.GetIntegratedItem(item.GetItemID());
        return integrated != null && (integrated.IsEquipmentType() || item.IsUpgradeKit());
    }
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
            var method = AccessTools.DeclaredMethod(
                type, nameof(PickupInstanceItem.SuccessInteract), new[] { typeof(BaseCharacter) });
            if (method != null)
                yield return method;
        }
    }

    private static bool Prefix(PickupInstanceItem __instance) =>
        ProbeBehaviour.Instance?.OnPickupInteract(__instance) ?? true;
}
