using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace DaveTheDiverMP;

internal sealed class PickupReplicator
{
    private const uint PickupRevision = 1;
    private const int MaxCachedResults = 512;
    private const float ClientRequestLifetime = 15f;

    private sealed class PendingClientRequest
    {
        internal PickupRequest Request;
        internal float ExpiresAt;
    }

    private readonly ManualLogSource _log;
    private readonly SessionTrace _trace;
    private readonly RemoteCatchLedger _ledger;
    private readonly Dictionary<uint, PickupInstanceItem> _items = new();
    private readonly Dictionary<uint, PickupRemoved> _pending = new();
    private readonly Dictionary<uint, PickupRemoved> _hostPending = new();
    private readonly Dictionary<ulong, PendingClientRequest> _clientPending = new();
    private readonly Dictionary<uint, ulong> _clientPendingByWorld = new();
    private readonly Dictionary<ulong, PickupResult> _hostResultCache = new();
    private readonly Dictionary<ulong, PickupResult> _hostPendingResults = new();
    private readonly Queue<ulong> _hostResultOrder = new();
    private readonly HashSet<uint> _applyingRemoteRemovals = new();
    private float _nextScan;
    private int _lastItemCount = -1;
    private int _lastDuplicateCount = -1;
    private uint _activeSceneId;
    private uint _activeSceneEpoch;
    private SessionRole _activeRole;
    private ulong _nextRequestId;

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
        var prefix = typeof(PickupInteractPatch).GetMethod("Prefix", patchFlags);
        var request = new PickupRequest(9, 7, 3, 11, 42, PickupRevision, 1f, 2f);
        var accepted = new PickupResult(
            9, 7, 3, 11, 42, PickupRevision, true, PickupRejectReason.None);
        if (!MatchesRemotePickup(42, 42, 2f, 2f) ||
            MatchesRemotePickup(42, 43, 0f, 0f) ||
            MatchesRemotePickup(42, 42, 5f, 0f) ||
            !SameTransaction(accepted, request) ||
            SameTransaction(accepted, request with { WorldId = 12 }) ||
            Reject(request, PickupRejectReason.OutOfRange).Accepted ||
            prefix == null || prefix.GetParameters().Length != 2 ||
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
            while (session.TryTakePickupResult(out _))
            {
            }
            _items.Clear();
            _pending.Clear();
            _hostPending.Clear();
            _clientPending.Clear();
            _clientPendingByWorld.Clear();
            _hostResultCache.Clear();
            _hostPendingResults.Clear();
            _hostResultOrder.Clear();
            return;
        }

        var activeEpoch = role == SessionRole.Host
            ? session.LocalSceneEpoch
            : session.RemoteSceneEpoch;
        if (_activeSceneId != sceneId || _activeSceneEpoch != activeEpoch || _activeRole != role)
        {
            Clear();
            _activeSceneId = sceneId;
            _activeSceneEpoch = activeEpoch;
            _activeRole = role;
        }

        if (now >= _nextScan)
            Refresh(sceneId, now);

        if (role == SessionRole.Host)
        {
            FlushHostPending(session);
            FlushHostResults(session);
            while (session.TryTakePickupRemoved(out _))
            {
            }
            while (session.TryTakePickupResult(out _))
            {
            }
            while (session.TryTakePickupRequest(out var request))
                ApplyHostRequest(session, sceneId, now, hostPlayer, request);
            return;
        }

        while (session.TryTakePickupRequest(out _))
        {
        }

        while (session.TryTakePickupResult(out var result))
            ApplyClientResult(result);

        while (session.TryTakePickupRemoved(out var removed))
        {
            if (removed.SceneId != sceneId ||
                removed.SceneEpoch != session.RemoteSceneEpoch)
                continue;
            if (!TryApply(removed))
                _pending[removed.WorldId] = removed;
        }
        ExpireClientRequests(now);
    }

    internal bool RequestPickup(UdpSession session, uint sceneId, PickupInstanceItem item)
    {
        if (item == null || session == null || !session.SceneMatches(sceneId))
            return false;
        var itemId = ItemId(item);
        var worldId = WorldId(item);
        if (itemId <= 0 || worldId == 0 || _clientPendingByWorld.ContainsKey(worldId))
            return false;
        _nextRequestId = _nextRequestId == ulong.MaxValue ? 1 : _nextRequestId + 1;
        var position = item.transform.position;
        var request = new PickupRequest(
            _nextRequestId, sceneId, session.RemoteSceneEpoch, worldId, itemId,
            PickupRevision, position.x, position.y);
        if (!session.SendPickupRequest(request))
            return false;
        _clientPending[request.RequestId] = new PendingClientRequest
        {
            Request = request,
            ExpiresAt = Time.realtimeSinceStartup + ClientRequestLifetime
        };
        _clientPendingByWorld[worldId] = request.RequestId;
        _trace?.Write("ITEM-REQUEST", $"request={request.RequestId} world={worldId:X8} item={itemId}");
        return false;
    }

    internal void OnDestroyed(
        SessionRole role, UdpSession session, uint sceneId, PickupInstanceItem item)
    {
        if (item == null)
            return;
        var itemId = ItemId(item);
        if (itemId <= 0)
            return;
        var worldId = WorldId(item);
        if (_applyingRemoteRemovals.Contains(worldId))
            return;
        _items.Remove(worldId);
        if (role != SessionRole.Host)
            return;
        var removed = new PickupRemoved(
            sceneId, session.LocalSceneEpoch, worldId, itemId);
        if (!session.SendPickupRemoved(removed))
            _hostPending[worldId] = removed;
    }

    internal void Clear()
    {
        _items.Clear();
        _pending.Clear();
        _hostPending.Clear();
        _clientPending.Clear();
        _clientPendingByWorld.Clear();
        _hostResultCache.Clear();
        _hostPendingResults.Clear();
        _hostResultOrder.Clear();
        _applyingRemoteRemovals.Clear();
        _nextScan = 0f;
        _lastItemCount = -1;
        _lastDuplicateCount = -1;
        _activeSceneId = 0;
        _activeSceneEpoch = 0;
        _activeRole = SessionRole.Offline;
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
            var worldId = WorldId(item);
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

    private void ApplyHostRequest(
        UdpSession session,
        uint sceneId,
        float now,
        PlayerCharacter hostPlayer,
        PickupRequest request)
    {
        if (_hostResultCache.TryGetValue(request.RequestId, out var cached))
        {
            QueueHostResult(session, SameTransaction(cached, request)
                ? cached
                : Reject(request, PickupRejectReason.InternalError));
            return;
        }

        var result = ValidateAndCommitHostRequest(
            session, sceneId, now, hostPlayer, request);
        CacheHostResult(result);
        QueueHostResult(session, result);
        _trace?.Write(result.Accepted ? "ITEM-COMMIT" : "ITEM-REJECT",
            $"request={request.RequestId} world={request.WorldId:X8} reason={result.RejectReason}");
    }

    private PickupResult ValidateAndCommitHostRequest(
        UdpSession session,
        uint sceneId,
        float now,
        PlayerCharacter hostPlayer,
        PickupRequest request)
    {
        if (request.SceneId != sceneId || request.SceneEpoch != session.LocalSceneEpoch)
            return Reject(request, PickupRejectReason.SceneMismatch);
        if (request.KnownRevision != PickupRevision)
            return Reject(request, PickupRejectReason.StaleRevision);
        if (!_items.TryGetValue(request.WorldId, out var item) || item == null)
        {
            Refresh(sceneId, now);
            _items.TryGetValue(request.WorldId, out item);
        }
        if (item == null)
            return Reject(request, PickupRejectReason.MissingEntity);
        if (ItemId(item) != request.ItemId)
            return Reject(request, PickupRejectReason.ItemMismatch);
        if (!IsAvailable(item))
            return Reject(request, PickupRejectReason.AlreadyClaimed);

        var itemPosition = item.transform.position;
        var expectedDx = itemPosition.x - request.ExpectedX;
        var expectedDy = itemPosition.y - request.ExpectedY;
        if (expectedDx * expectedDx + expectedDy * expectedDy > 0.25f)
            return Reject(request, PickupRejectReason.StaleRevision);
        if (!session.TryGetFreshRemotePlayerSnapshot(now, 0.75f, out var remotePlayer) ||
            remotePlayer.SceneId != sceneId || remotePlayer.SceneEpoch != session.RemoteSceneEpoch)
            return Reject(request, PickupRejectReason.StalePose);
        var dx = itemPosition.x - remotePlayer.X;
        var dy = itemPosition.y - remotePlayer.Y;
        if (!MatchesRemotePickup(request.ItemId, ItemId(item), dx, dy))
            return Reject(request, PickupRejectReason.OutOfRange);

        var worldId = request.WorldId;
        _applyingRemoteRemovals.Add(worldId);
        try
        {
            if (hostPlayer == null || !_ledger.CapturePickup(
                    RemoteCatchLedger.PickupSource(sceneId, worldId), request.ItemId, 1,
                    () => item.SuccessInteract(hostPlayer), out _))
                return Reject(request, PickupRejectReason.InternalError);
        }
        finally
        {
            _applyingRemoteRemovals.Remove(worldId);
        }
        _items.Remove(worldId);
        return new PickupResult(
            request.RequestId, request.SceneId, request.SceneEpoch, request.WorldId,
            request.ItemId, PickupRevision, true, PickupRejectReason.None);
    }

    private void ApplyClientResult(PickupResult result)
    {
        if (!_clientPending.TryGetValue(result.RequestId, out var pending) ||
            pending.Request.SceneId != result.SceneId ||
            pending.Request.SceneEpoch != result.SceneEpoch ||
            pending.Request.WorldId != result.WorldId ||
            pending.Request.ItemId != result.ItemId ||
            pending.Request.KnownRevision != result.Revision)
            return;
        _clientPending.Remove(result.RequestId);
        _clientPendingByWorld.Remove(result.WorldId);
        if (result.Accepted)
        {
            var removed = new PickupRemoved(
                result.SceneId, result.SceneEpoch, result.WorldId, result.ItemId);
            if (!TryApply(removed))
                _pending[result.WorldId] = removed;
        }
        _trace?.Write(result.Accepted ? "ITEM-RESULT" : "ITEM-REJECT",
            $"request={result.RequestId} world={result.WorldId:X8} reason={result.RejectReason}");
    }

    private void ExpireClientRequests(float now)
    {
        if (_clientPending.Count == 0)
            return;
        var expired = new List<ulong>();
        foreach (var pair in _clientPending)
            if (now >= pair.Value.ExpiresAt)
                expired.Add(pair.Key);
        foreach (var requestId in expired)
        {
            var pending = _clientPending[requestId];
            _clientPending.Remove(requestId);
            _clientPendingByWorld.Remove(pending.Request.WorldId);
            _trace?.Write("ITEM-EXPIRE",
                $"request={requestId} world={pending.Request.WorldId:X8}");
        }
    }

    private void CacheHostResult(PickupResult result)
    {
        while (_hostResultCache.Count >= MaxCachedResults && _hostResultOrder.Count > 0)
        {
            var oldest = _hostResultOrder.Dequeue();
            _hostResultCache.Remove(oldest);
            _hostPendingResults.Remove(oldest);
        }
        _hostResultCache[result.RequestId] = result;
        _hostResultOrder.Enqueue(result.RequestId);
    }

    private void QueueHostResult(UdpSession session, PickupResult result)
    {
        if (!session.SendPickupResult(result))
            _hostPendingResults[result.RequestId] = result;
    }

    private void FlushHostResults(UdpSession session)
    {
        if (_hostPendingResults.Count == 0 || session.ReliableCapacityRemaining == 0)
            return;
        var sent = new List<ulong>();
        foreach (var pair in _hostPendingResults)
        {
            if (!session.SendPickupResult(pair.Value))
                break;
            sent.Add(pair.Key);
        }
        foreach (var requestId in sent)
            _hostPendingResults.Remove(requestId);
    }

    private static PickupResult Reject(PickupRequest request, PickupRejectReason reason) =>
        new(request.RequestId, request.SceneId, request.SceneEpoch, request.WorldId,
            request.ItemId, PickupRevision, false, reason);

    private static bool SameTransaction(PickupResult result, PickupRequest request) =>
        result.RequestId == request.RequestId && result.SceneId == request.SceneId &&
        result.SceneEpoch == request.SceneEpoch && result.WorldId == request.WorldId &&
        result.ItemId == request.ItemId && result.Revision == request.KnownRevision;

    internal static uint WorldId(PickupInstanceItem item) =>
        WorldObjectId.For(item, ItemId(item), SpawnerUniqueId(item));

    private static string SpawnerUniqueId(PickupInstanceItem item)
    {
        try
        {
            return item?.GetComponentInParent<SpawnerBase>()?.UniqueID;
        }
        catch
        {
            return null;
        }
    }

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

    private static bool IsAvailable(PickupInstanceItem item)
    {
        try
        {
            return item != null && item.isActiveAndEnabled && item.GetIsEnableInteraction;
        }
        catch
        {
            return false;
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

    private static bool Prefix(PickupInstanceItem __instance, BaseCharacter __0) =>
        ProbeBehaviour.Instance?.OnPickupInteract(__instance, __0) ?? true;

    private static Exception Finalizer(Exception __exception)
    {
        ProbeBehaviour.Instance?.EndClientLootSource();
        return __exception;
    }
}
