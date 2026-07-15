using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using DR;
using HarmonyLib;
using UnityEngine;

namespace DaveTheDiverMP;

internal sealed class RemoteCatchLedger
{
    private sealed class LootEntry
    {
        internal int ItemId;
        internal int Count;
        internal int BonusGrade;
        internal LootBox.AutoLiftedType LiftType;
        internal Il2CppSystem.Collections.Generic.List<string> GetTimes;
        internal bool UpdateMission;
    }

    private sealed class Capture
    {
        internal readonly List<LootEntry> Entries = new();
    }

    private const int MaxSources = 16_384;
    private const int MaxStacks = 4_096;
    private const int MaxCountPerAdd = 9_999;
    private readonly ManualLogSource _log;
    private readonly List<LootEntry> _entries = new();
    private readonly HashSet<ulong> _acceptedSources = new();
    private Capture _capture;
    private float _carriedWeight;
    private bool _materializing;
    private bool _hostResultSent;
    private bool _applyingClientResult;
    private ulong _clientResultTransfer;
    private ushort _clientResultTotal;
    private readonly Dictionary<ushort, DiveResultEntry> _clientResultEntries = new();
    private bool _clientResultApplied;
    private bool _clientResultShown;
    private float _clientResultReceivedAt;
    private bool _inDiveSession;
    private float _clientAcceptResultsAt;

    internal RemoteCatchLedger(ManualLogSource log) => _log = log;

    internal string Status => _entries.Count == 0
        ? "remote carry: empty"
        : $"remote carry: {_entries.Count} stacks / {_carriedWeight:F1} kg";

    internal bool ApplyingClientResult => _applyingClientResult;

    internal void Update(SessionRole role, UdpSession session, string sceneName, float now)
    {
        if (session == null)
            return;
        if (role == SessionRole.Host)
        {
            while (session.TryTakeDiveLootRequest(out var request))
                AcceptRemoteLoot(request);
            while (session.TryTakeDiveResultEntry(out _))
            {
            }
            while (session.TryTakeDiveResultState(out _))
            {
            }
            return;
        }
        while (session.TryTakeDiveLootRequest(out _))
        {
        }
        if (role != SessionRole.Client)
            return;
        while (session.TryTakeDiveResultEntry(out var entry))
        {
            if (now < _clientAcceptResultsAt)
                continue;
            BeginClientResult(entry.TransferId, entry.Total);
            if (entry.TransferId == _clientResultTransfer && entry.Total == _clientResultTotal)
                _clientResultEntries[entry.Index] = entry;
        }
        while (session.TryTakeDiveResultState(out var state))
            if (now >= _clientAcceptResultsAt)
                BeginClientResult(state.TransferId, state.Total);
        if (!_inDiveSession)
        {
            _clientResultEntries.Clear();
            _clientResultTransfer = 0;
            _clientResultTotal = 0;
            return;
        }
        if (_clientResultEntries.Count != _clientResultTotal)
            return;
        if (!_clientResultApplied)
            ApplyClientResult(now);
        if (!_clientResultShown && sceneName == "DR_Lobby" &&
            now >= _clientResultReceivedAt + 0.35f)
            OpenClientResultFallback();
    }

    internal void BeginDive(UdpSession session, float now)
    {
        _entries.Clear();
        _acceptedSources.Clear();
        _capture = null;
        _carriedWeight = 0f;
        _materializing = false;
        _hostResultSent = false;
        _applyingClientResult = false;
        _clientResultTransfer = 0;
        _clientResultTotal = 0;
        _clientResultEntries.Clear();
        _clientResultApplied = false;
        _clientResultShown = false;
        _clientResultReceivedAt = 0f;
        _inDiveSession = true;
        _clientAcceptResultsAt = now + 2f;
        if (session != null)
        {
            while (session.TryTakeDiveResultEntry(out _))
            {
            }
            while (session.TryTakeDiveResultState(out _))
            {
            }
        }
        _log.LogInfo("Dive result session started");
    }

    internal void ReportClientLoot(
        UdpSession session,
        int itemId,
        int count,
        int bonusGrade,
        LootBox.AutoLiftedType liftType,
        bool updateMission)
    {
        if (_applyingClientResult || session == null || !session.Connected ||
            itemId <= 0 || count <= 0)
            return;
        session.SendDiveLootRequest(new DiveLootRequest(
            itemId, count, Math.Max(0, bonusGrade), (int)liftType, updateMission));
    }

    internal void PrepareResult(UdpSession session, bool sendSnapshot)
    {
        FlushIntoResult();
        if (!sendSnapshot || !_inDiveSession || _hostResultSent ||
            session == null || !session.Connected)
            return;
        var lootBox = LootBox.Instance;
        if (lootBox == null)
            return;
        var slots = lootBox.GetAllLootBoxSlots();
        var entries = new List<DiveResultEntry>();
        if (slots != null)
        {
            foreach (var slot in slots)
            {
                if (slot == null)
                    continue;
                var itemId = (int)slot.ItemID;
                var count = (int)slot.TotalCount;
                var grade = Math.Max(0, (int)slot.Grade);
                if (itemId > 0 && count > 0)
                    entries.Add(new DiveResultEntry(
                        0, 0, 0, itemId, count, grade, (int)slot.AutoLiftedType));
            }
        }
        if (entries.Count > 200)
        {
            _log.LogError($"Dive result has {entries.Count} stacks; maximum is 200");
            return;
        }
        var transferId = unchecked((ulong)DateTime.UtcNow.Ticks);
        if (transferId == 0)
            transferId = 1;
        var total = (ushort)entries.Count;
        for (ushort index = 0; index < total; index++)
        {
            var entry = entries[index];
            session.SendDiveResultEntry(entry with
            {
                TransferId = transferId,
                Index = index,
                Total = total
            });
        }
        session.SendDiveResultState(new DiveResultState(transferId, total));
        _hostResultSent = true;
        _log.LogInfo($"Dive result snapshot sent: {total} stacks");
    }

    internal bool CaptureFish(
        ulong sourceId,
        int expectedItemId,
        Action nativePickup,
        out bool nativeCompleted)
    {
        nativeCompleted = false;
        if (_acceptedSources.Contains(sourceId))
        {
            nativeCompleted = true;
            return true;
        }
        if (_capture != null || _acceptedSources.Count >= MaxSources || nativePickup == null ||
            _entries.Count > MaxStacks - 32 || !HasCarryCapacity())
            return false;

        var capture = new Capture();
        Exception failure = null;
        _capture = capture;
        try
        {
            nativePickup();
            nativeCompleted = true;
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            _capture = null;
        }

        if (capture.Entries.Count == 0 && expectedItemId > 0)
        {
            _log.LogWarning($"Remote fish produced no captured loot for item {expectedItemId}");
            return false;
        }

        if (failure != null)
        {
            _log.LogWarning($"Remote fish capture failed: {failure.Message}");
            return false;
        }

        _entries.AddRange(capture.Entries);
        AddWeight(capture.Entries);
        _acceptedSources.Add(sourceId);
        return true;
    }

    internal bool CapturePickup(
        ulong sourceId,
        int expectedItemId,
        int expectedCount,
        Action nativePickup,
        out bool nativeCompleted)
    {
        nativeCompleted = false;
        if (_acceptedSources.Contains(sourceId))
        {
            nativeCompleted = true;
            return true;
        }
        if (_capture != null || _acceptedSources.Count >= MaxSources || nativePickup == null ||
            expectedItemId <= 0 || expectedCount is < 1 or > MaxCountPerAdd ||
            _entries.Count > MaxStacks - 8 ||
            !HasCarryCapacity())
            return false;

        var capture = new Capture();
        Exception failure = null;
        _capture = capture;
        try
        {
            nativePickup();
            nativeCompleted = true;
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            _capture = null;
        }

        if (failure != null && capture.Entries.Count == 0)
        {
            _log.LogWarning($"Remote pickup failed: {failure.Message}");
            return false;
        }
        if (capture.Entries.Count == 0)
        {
            _log.LogWarning($"Remote pickup produced no captured loot for {expectedItemId}");
            return false;
        }

        _entries.AddRange(capture.Entries);
        AddWeight(capture.Entries);
        _acceptedSources.Add(sourceId);
        if (failure != null)
            _log.LogWarning($"Remote pickup kept partial loot: {failure.Message}");
        return true;
    }

    internal bool TryIntercept(
        int itemId,
        int count,
        int bonusGrade,
        LootBox.AutoLiftedType liftType,
        Il2CppSystem.Collections.Generic.List<string> getTimes,
        bool updateMission)
    {
        if (_capture == null)
            return false;

        if (itemId > 0 && count is > 0 and <= MaxCountPerAdd)
        {
            _capture.Entries.Add(new LootEntry
            {
                ItemId = itemId,
                Count = count,
                BonusGrade = bonusGrade,
                LiftType = liftType,
                GetTimes = getTimes,
                UpdateMission = updateMission
            });
        }
        else
        {
            _log.LogWarning($"Remote loot ignored invalid add: item={itemId}; count={count}");
        }
        return true;
    }

    internal void FlushIntoResult()
    {
        if (_entries.Count == 0 || _materializing)
            return;

        _materializing = true;
        try
        {
            var lootBox = LootBox.Instance;
            if (lootBox == null)
                return;

            var flushed = 0;
            for (var index = 0; index < _entries.Count;)
            {
                var entry = _entries[index];
                var before = TryGetTotalCount(lootBox, entry.ItemId);
                try
                {
                    if (lootBox.AddIgnoreOverloaded(
                            entry.ItemId,
                            entry.Count,
                            entry.BonusGrade,
                            entry.LiftType,
                            entry.GetTimes,
                            entry.UpdateMission))
                    {
                        _entries.RemoveAt(index);
                        flushed++;
                    }
                    else
                        index++;
                }
                catch (Exception exception)
                {
                    var after = TryGetTotalCount(lootBox, entry.ItemId);
                    if (before >= 0 && after >= before + entry.Count)
                    {
                        _entries.RemoveAt(index);
                        flushed++;
                        _log.LogWarning(
                            $"Remote carry {entry.ItemId} committed with a late error: " +
                            exception.Message);
                    }
                    else
                    {
                        index++;
                        _log.LogError(
                            $"Remote carry {entry.ItemId} retained after merge error: " +
                            exception.Message);
                    }
                }
            }

            _log.LogInfo(
                $"Remote carry merged into dive result: {flushed} stacks; " +
                $"{_entries.Count} pending");
        }
        catch (Exception exception)
        {
            _log.LogWarning($"Remote carry result merge failed: {exception.Message}");
        }
        finally
        {
            _materializing = false;
        }
    }

    internal void Clear(string reason)
    {
        if (_entries.Count > 0)
            _log.LogInfo($"Remote carry cleared ({reason}): {_entries.Count} stacks");
        _entries.Clear();
        _acceptedSources.Clear();
        _capture = null;
        _carriedWeight = 0f;
        _materializing = false;
        _hostResultSent = false;
        _applyingClientResult = false;
        _clientResultTransfer = 0;
        _clientResultTotal = 0;
        _clientResultEntries.Clear();
        _clientResultApplied = false;
        _clientResultShown = false;
        _clientResultReceivedAt = 0f;
        _inDiveSession = false;
        _clientAcceptResultsAt = 0f;
    }

    internal void ClearCompleted()
    {
        if (_entries.Count > 0)
        {
            _log.LogError($"Remote carry retained after result clear: {_entries.Count} stacks");
            return;
        }
        Clear("result complete");
    }

    internal static ulong FishSource(uint sceneId, int networkId) =>
        0x4600000000000000UL ^ ((ulong)sceneId << 32) ^ (uint)networkId;

    internal static ulong PickupSource(uint sceneId, uint worldId) =>
        0x5000000000000000UL ^ ((ulong)sceneId << 32) ^ worldId;

    // Matching the native inventory, the pickup that crosses the limit is
    // accepted and makes the diver overloaded; later pickups stop.
    private bool HasCarryCapacity()
    {
        try
        {
            var lootBox = LootBox.Instance;
            return lootBox != null && lootBox.weightMax > 0f &&
                lootBox.weight + _carriedWeight < lootBox.weightMax;
        }
        catch
        {
            return false;
        }
    }

    private void AddWeight(List<LootEntry> entries)
    {
        foreach (var entry in entries)
            _carriedWeight += ItemWeight(entry.ItemId) * entry.Count;
    }

    private static float ItemWeight(int itemId)
    {
        try
        {
            var item = DataManager.Instance.GetItems(itemId);
            return item != null ? Math.Max(0f, item.ItemWeight) : 0f;
        }
        catch
        {
            return 0f;
        }
    }

    private static int TryGetTotalCount(LootBox lootBox, int itemId)
    {
        try
        {
            return lootBox.TotalItemCount(itemId);
        }
        catch
        {
            return -1;
        }
    }

    private void AcceptRemoteLoot(DiveLootRequest request)
    {
        if (_entries.Count >= MaxStacks || request.ItemId <= 0 ||
            request.Count is < 1 or > MaxCountPerAdd || !HasCarryCapacity())
        {
            _log.LogWarning(
                $"Remote loot rejected: item={request.ItemId}; count={request.Count}");
            return;
        }
        var entry = new LootEntry
        {
            ItemId = request.ItemId,
            Count = request.Count,
            BonusGrade = request.BonusGrade,
            LiftType = (LootBox.AutoLiftedType)request.LiftType,
            GetTimes = null,
            UpdateMission = request.UpdateMission
        };
        _entries.Add(entry);
        AddWeight(new List<LootEntry> { entry });
        _log.LogInfo($"Remote loot accepted: item={request.ItemId}; count={request.Count}");
    }

    private void BeginClientResult(ulong transferId, ushort total)
    {
        if (transferId == 0 || total > 200)
            return;
        if (_clientResultTransfer == transferId)
        {
            if (_clientResultTotal != total)
                _clientResultEntries.Clear();
        }
        else
        {
            _clientResultEntries.Clear();
            _clientResultApplied = false;
            _clientResultShown = false;
            _clientResultReceivedAt = 0f;
        }
        _clientResultTransfer = transferId;
        _clientResultTotal = total;
    }

    private void ApplyClientResult(float now)
    {
        var lootBox = LootBox.Instance;
        if (lootBox == null)
            return;
        _applyingClientResult = true;
        try
        {
            lootBox.Clear();
            for (ushort index = 0; index < _clientResultTotal; index++)
            {
                if (!_clientResultEntries.TryGetValue(index, out var entry))
                    return;
                lootBox.AddIgnoreOverloaded(
                    entry.ItemId,
                    entry.Count,
                    entry.BonusGrade,
                    (LootBox.AutoLiftedType)entry.LiftType,
                    null,
                    false);
            }
            _clientResultApplied = true;
            _clientResultReceivedAt = now;
            _log.LogInfo($"Client dive result applied: {_clientResultTotal} stacks");
        }
        catch (Exception exception)
        {
            _log.LogError($"Client dive result failed: {exception.Message}");
        }
        finally
        {
            _applyingClientResult = false;
        }
    }

    private void OpenClientResultFallback()
    {
        try
        {
            var routine = UnityEngine.Object.FindFirstObjectByType<LobbyPostRoutine>();
            if (routine == null)
                return;
            routine.Execute();
            _clientResultShown = true;
            _log.LogInfo("Client dive result restarted through the native lobby flow");
        }
        catch (Exception exception)
        {
            _log.LogError($"Client dive result restart failed: {exception.Message}");
        }
    }

}

internal static class RemoteCatchPatchBridge
{
    internal static bool TryCapture(
        int itemId,
        int count,
        int bonusGrade,
        LootBox.AutoLiftedType liftType,
        Il2CppSystem.Collections.Generic.List<string> getTimes,
        bool updateMission) =>
        ProbeBehaviour.Instance?.TryCaptureRemoteLoot(
            itemId, count, bonusGrade, liftType, getTimes, updateMission) ?? false;

    internal static void PrepareResult() => ProbeBehaviour.Instance?.PrepareRemoteCatchResult();

    internal static void Clear(string reason) => ProbeBehaviour.Instance?.ClearRemoteCatch(reason);

    internal static void ClearCompleted() => ProbeBehaviour.Instance?.ClearCompletedRemoteCatch();
}

[HarmonyPatch(typeof(LootBox), nameof(LootBox.Add_Impl))]
internal static class ClientDiveLootPatch
{
    private static void Prefix(
        DR.IItemBase __0,
        int __1,
        int __2,
        LootBox.AutoLiftedType __3,
        Il2CppSystem.Collections.Generic.List<string> __4,
        bool __5) =>
        ProbeBehaviour.Instance?.ReportClientLoot(
            __0?.TID ?? 0, __1, __2, __3, __5);
}

[HarmonyPatch(typeof(LootBox), nameof(LootBox.Add),
    new[]
    {
        typeof(int), typeof(int), typeof(int), typeof(LootBox.AutoLiftedType),
        typeof(Il2CppSystem.Collections.Generic.List<string>), typeof(bool)
    })]
internal static class RemoteCatchLootAddPatch
{
    private static bool Prefix(
        int id,
        int count,
        int bonusGrade,
        LootBox.AutoLiftedType liftType,
        Il2CppSystem.Collections.Generic.List<string> getTimes,
        bool bUpdateMissionCnt,
        ref bool __result)
    {
        if (!RemoteCatchPatchBridge.TryCapture(
                id, count, bonusGrade, liftType, getTimes, bUpdateMissionCnt))
            return true;
        __result = true;
        return false;
    }
}

[HarmonyPatch(typeof(LootBox), nameof(LootBox.AddIgnoreOverloaded),
    new[]
    {
        typeof(int), typeof(int), typeof(int), typeof(LootBox.AutoLiftedType),
        typeof(Il2CppSystem.Collections.Generic.List<string>), typeof(bool)
    })]
internal static class RemoteCatchLootAddIgnorePatch
{
    private static bool Prefix(
        int id,
        int count,
        int bonusGrade,
        LootBox.AutoLiftedType liftType,
        Il2CppSystem.Collections.Generic.List<string> getTimes,
        bool bUpdateMissionCnt,
        ref bool __result)
    {
        if (!RemoteCatchPatchBridge.TryCapture(
                id, count, bonusGrade, liftType, getTimes, bUpdateMissionCnt))
            return true;
        __result = true;
        return false;
    }
}

[HarmonyPatch(typeof(LobbyPostRoutine), "StartDiveResultProcess")]
internal static class RemoteCatchResultStartPatch
{
    private static void Prefix() => RemoteCatchPatchBridge.PrepareResult();
}

[HarmonyPatch(typeof(ItemSendIngredientsStoragePanel),
    nameof(ItemSendIngredientsStoragePanel.Init))]
internal static class RemoteCatchResultPanelPatch
{
    private static void Prefix() => RemoteCatchPatchBridge.PrepareResult();
}

[HarmonyPatch(typeof(PlayerDiePopupMenu), nameof(PlayerDiePopupMenu.OnPopup))]
internal static class RemoteCatchDeathPopupPatch
{
    private static bool Prefix()
    {
        RemoteCatchPatchBridge.PrepareResult();
        return !(ProbeBehaviour.Instance?.ShouldSuppressClientDeathPopup() ?? false);
    }
}

[HarmonyPatch(typeof(LootBox), nameof(LootBox.Clear))]
internal static class RemoteCatchResultClearPatch
{
    private static void Postfix() => RemoteCatchPatchBridge.ClearCompleted();
}

[HarmonyPatch]
internal static class RemoteCatchAbortPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var method in AccessTools.GetDeclaredMethods(typeof(LootBox)))
        {
            if (method.Name == nameof(LootBox.RemoveAll))
                yield return method;
        }
    }

    private static void Postfix() => RemoteCatchPatchBridge.Clear("dive aborted");
}
