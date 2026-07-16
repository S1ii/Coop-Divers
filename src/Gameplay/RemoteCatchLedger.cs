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
    private readonly SessionTrace _trace;
    private readonly List<LootEntry> _entries = new();
    private readonly HashSet<ulong> _acceptedSources = new();
    private Capture _capture;
    private float _carriedWeight;
    private bool _materializing;
    private bool _hostResultSent;
    private bool _applyingClientResult;
    private bool _applyingRemoteLoot;
    private ulong _clientResultTransfer;
    private ushort _clientResultTotal;
    private readonly Dictionary<ushort, DiveResultEntry> _clientResultEntries = new();
    private bool _clientResultApplied;
    private bool _clientResultShown;
    private float _clientResultReceivedAt;
    private bool _inDiveSession;
    private float _clientAcceptResultsAt;
    private int _lastClientLootFrame = -1;
    private LootEntry _lastClientLoot;
    private Func<DiveLootRequest, bool> _remoteLootEvidence;
    private ulong _clientLootSource;
    private ulong _pendingClientLootSource;
    private float _pendingClientLootSourceUntil;
    private ulong _nextClientLootSource;
    private CargoState _clientCargoState;
    private CargoState _lastHostCargoState;
    private bool _hasHostCargoState;
    private float _nextHostCargoSend;

    internal RemoteCatchLedger(ManualLogSource log, SessionTrace trace)
    {
        _log = log;
        _trace = trace;
    }

    internal static void SelfTest()
    {
        var state = new CargoState(123, 9f, 13f, 0f);
        if (!ShouldApplyCargoState(SessionRole.Client, true, 123, state) ||
            ShouldApplyCargoState(SessionRole.Host, true, 123, state) ||
            ShouldApplyCargoState(SessionRole.Client, false, 123, state) ||
            ShouldApplyCargoState(SessionRole.Client, true, 124, state))
            throw new InvalidOperationException("Cargo state apply decision failed");

        var accepted = new HashSet<ulong>();
        if (TryAcceptEvidence(accepted, 7, false) ||
            !TryAcceptEvidence(accepted, 7, true) ||
            TryAcceptEvidence(accepted, 7, true))
            throw new InvalidOperationException("Remote loot evidence must be present and exactly once");
        if (SelectClientLootSource(0, 7, 1f, 2f) != 7 ||
            SelectClientLootSource(0, 7, 3f, 2f) != 0 ||
            SelectClientLootSource(8, 7, 3f, 2f) != 8)
            throw new InvalidOperationException("Delayed client loot evidence selection failed");
    }

    internal string Status => _entries.Count == 0
        ? "remote carry: empty"
        : $"remote carry: {_entries.Count} stacks / {_carriedWeight:F1} kg";

    internal bool ApplyingClientResult => _applyingClientResult;

    internal bool ApplyingRemoteLoot => _applyingRemoteLoot;

    internal void SetRemoteLootEvidence(Func<DiveLootRequest, bool> evidence) =>
        _remoteLootEvidence = evidence;

    internal void BeginClientPickupSource(uint sceneId, uint worldId)
    {
        _pendingClientLootSource = 0;
        _clientLootSource = PickupSource(sceneId, worldId);
    }

    internal void BeginClientFishSource()
    {
        _pendingClientLootSource = 0;
        _nextClientLootSource = (_nextClientLootSource + 1) & 0x0fffffffffffffffUL;
        if (_nextClientLootSource == 0)
            _nextClientLootSource = 1;
        _clientLootSource = 0x6000000000000000UL | _nextClientLootSource;
    }

    internal void EndClientLootSource()
    {
        if (_clientLootSource != 0)
        {
            _pendingClientLootSource = _clientLootSource;
            _pendingClientLootSourceUntil = Time.realtimeSinceStartup + 0.5f;
        }
        _clientLootSource = 0;
    }

    internal void Update(
        SessionRole role,
        UdpSession session,
        string sceneName,
        bool isLobby,
        float now)
    {
        if (session == null)
            return;
        if (role == SessionRole.Host)
        {
            UpdateHostCargoState(session, sceneName, now);
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
        if (role != SessionRole.Client)
        {
            while (session.TryTakeDiveLootRequest(out _))
            {
            }
            return;
        }
        while (session.TryTakeCargoState(out var cargoState))
            if (ShouldApplyCargoState(
                    role, _inDiveSession, Protocol.SceneId(sceneName), cargoState))
            {
                _clientCargoState = cargoState;
                ApplyClientCargoState(LootBox.Instance);
                _trace?.Write("CARGO-APPLY",
                    $"max={cargoState.WeightMax:F2} threshold={cargoState.OverloadedThreshold:F2} " +
                    $"parameter={cargoState.WeightParameter:F2}");
            }
        while (session.TryTakeDiveLootRequest(out var request))
            ApplyRemoteLoot(request);
        while (session.TryTakeDiveResultEntry(out var entry))
        {
            if (now < _clientAcceptResultsAt)
                continue;
            _log.LogInfo(
                $"Client result entry received: transfer={entry.TransferId}; " +
                $"index={entry.Index}/{entry.Total}; item={entry.ItemId}; count={entry.Count}");
            _trace?.Write("RESULT-ENTRY",
                $"transfer={entry.TransferId} index={entry.Index}/{entry.Total} " +
                $"item={entry.ItemId} count={entry.Count}");
            BeginClientResult(entry.TransferId, entry.Total);
            if (entry.TransferId == _clientResultTransfer && entry.Total == _clientResultTotal)
                _clientResultEntries[entry.Index] = entry;
        }
        while (session.TryTakeDiveResultState(out var state))
            if (now >= _clientAcceptResultsAt)
            {
                _log.LogInfo(
                    $"Client result state received: transfer={state.TransferId}; total={state.Total}");
                _trace?.Write("RESULT-STATE", $"transfer={state.TransferId} total={state.Total}");
                BeginClientResult(state.TransferId, state.Total);
            }
        if (!_inDiveSession)
        {
            _clientResultEntries.Clear();
            _clientResultTransfer = 0;
            _clientResultTotal = 0;
            return;
        }
        if (_clientResultTransfer == 0 || _clientResultEntries.Count != _clientResultTotal)
            return;
        if (!_clientResultApplied)
            ApplyClientResult(now);
        if (!_clientResultShown && isLobby &&
            now >= _clientResultReceivedAt + 0.35f)
            OpenClientResultFallback();
    }

    internal void BeginDive(UdpSession session, float now)
    {
        _entries.Clear();
        _acceptedSources.Clear();
        _clientLootSource = 0;
        _pendingClientLootSource = 0;
        _pendingClientLootSourceUntil = 0f;
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
        _clientCargoState = default;
        _lastHostCargoState = default;
        _hasHostCargoState = false;
        _nextHostCargoSend = 0f;
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
        var sourceId = SelectClientLootSource(
            _clientLootSource, _pendingClientLootSource,
            Time.realtimeSinceStartup, _pendingClientLootSourceUntil);
        if (_materializing || _capture != null || _applyingClientResult || _applyingRemoteLoot ||
            session == null || !session.Connected ||
            sourceId == 0 || itemId <= 0 || count <= 0)
        {
            _log.LogInfo(
                $"Client loot ignored: item={itemId}; count={count}; " +
                $"connected={session?.Connected ?? false}; applying={_applyingClientResult}; " +
                $"remote={_applyingRemoteLoot}");
            _trace?.Write("LOOT-IGNORE",
                $"item={itemId} count={count} connected={session?.Connected ?? false} " +
                $"applying={_applyingClientResult} remote={_applyingRemoteLoot}");
            return;
        }
        var frame = Time.frameCount;
        if (frame == _lastClientLootFrame && _lastClientLoot != null &&
            _lastClientLoot.ItemId == itemId && _lastClientLoot.Count == count &&
            _lastClientLoot.BonusGrade == bonusGrade && _lastClientLoot.LiftType == liftType &&
            _lastClientLoot.UpdateMission == updateMission)
        {
            _trace?.Write("LOOT-DUPE", $"item={itemId} count={count} frame={frame}");
            return;
        }
        _lastClientLootFrame = frame;
        _lastClientLoot = new LootEntry
        {
            ItemId = itemId,
            Count = count,
            BonusGrade = bonusGrade,
            LiftType = liftType,
            UpdateMission = updateMission
        };
        _log.LogInfo($"Client loot send: source={sourceId:X16}; item={itemId}; count={count}");
        _trace?.Write("LOOT-SEND", $"source={sourceId:X16} item={itemId} count={count}");
        session.SendDiveLootRequest(new DiveLootRequest(
            sourceId, itemId, count, Math.Max(0, bonusGrade), (int)liftType,
            updateMission));
        if (_clientLootSource == sourceId)
            _clientLootSource = 0;
        if (_pendingClientLootSource == sourceId)
            _pendingClientLootSource = 0;
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
        if (sourceId == 0 || _capture != null || _acceptedSources.Count >= MaxSources ||
            nativePickup == null ||
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

        if (!TryAcceptEvidence(_acceptedSources, sourceId, true))
            return false;
        UpdateAcceptedMissions(capture.Entries);
        _entries.AddRange(capture.Entries);
        AddWeight(capture.Entries);
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
        _applyingRemoteLoot = false;
        _clientResultTransfer = 0;
        _clientResultTotal = 0;
        _clientResultEntries.Clear();
        _clientResultApplied = false;
        _clientResultShown = false;
        _clientResultReceivedAt = 0f;
        _inDiveSession = false;
        _clientAcceptResultsAt = 0f;
        _lastClientLootFrame = -1;
        _lastClientLoot = null;
        _clientCargoState = default;
        _lastHostCargoState = default;
        _hasHostCargoState = false;
        _nextHostCargoSend = 0f;
        _clientLootSource = 0;
        _pendingClientLootSource = 0;
        _pendingClientLootSourceUntil = 0f;
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

    internal static ulong PickupSource(uint sceneId, uint worldId) =>
        0x5000000000000000UL ^ ((ulong)sceneId << 32) ^ worldId;

    internal void ApplyClientCargoState(LootBox lootBox)
    {
        if (lootBox == null || _clientCargoState.SceneId == 0)
            return;
        lootBox.WeightParameter = _clientCargoState.WeightParameter;
        lootBox.m_WeightMax = _clientCargoState.WeightMax;
        lootBox.overloadedThreshold = _clientCargoState.OverloadedThreshold;
    }

    private void UpdateHostCargoState(UdpSession session, string sceneName, float now)
    {
        if (!_inDiveSession)
            return;
        var lootBox = LootBox.Instance;
        var sceneId = Protocol.SceneId(sceneName);
        if (lootBox == null || sceneId == 0 || lootBox.weightMax <= 0f ||
            lootBox.overloadedThreshold <= 0f)
            return;
        var state = new CargoState(
            sceneId, lootBox.weightMax, lootBox.overloadedThreshold, lootBox.WeightParameter);
        if (_hasHostCargoState && state == _lastHostCargoState && now < _nextHostCargoSend)
            return;
        session.SendCargoState(state);
        _lastHostCargoState = state;
        _hasHostCargoState = true;
        _nextHostCargoSend = now + 5f;
        _trace?.Write("CARGO-SEND",
            $"max={state.WeightMax:F2} threshold={state.OverloadedThreshold:F2} " +
            $"parameter={state.WeightParameter:F2}");
    }

    private static bool ShouldApplyCargoState(
        SessionRole role,
        bool inDive,
        uint sceneId,
        CargoState state) =>
        role == SessionRole.Client && inDive && sceneId != 0 && sceneId == state.SceneId;

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
        if (_entries.Count >= MaxStacks || request.SourceId == 0 ||
            _acceptedSources.Contains(request.SourceId) ||
            _remoteLootEvidence == null || !_remoteLootEvidence(request))
        {
            _log.LogWarning(
                $"Remote loot rejected: source={request.SourceId:X16}; " +
                $"item={request.ItemId}; count={request.Count}");
            return;
        }
        _log.LogInfo($"Remote loot evidence accepted: source={request.SourceId:X16}");
        _trace?.Write("LOOT-ACCEPT", $"source={request.SourceId:X16}");
    }

    private static bool TryAcceptEvidence(HashSet<ulong> accepted, ulong sourceId, bool hasEvidence) =>
        sourceId != 0 && hasEvidence && accepted.Add(sourceId);

    private static ulong SelectClientLootSource(
        ulong active,
        ulong pending,
        float now,
        float pendingUntil) => active != 0 ? active : now <= pendingUntil ? pending : 0;

    private void ApplyRemoteLoot(DiveLootRequest request)
    {
        if (request.ItemId <= 0 || request.Count is < 1 or > MaxCountPerAdd)
            return;
        var lootBox = LootBox.Instance;
        if (lootBox == null)
            return;
        _applyingRemoteLoot = true;
        try
        {
            if (lootBox.AddIgnoreOverloaded(
                    request.ItemId,
                    request.Count,
                    request.BonusGrade,
                    (LootBox.AutoLiftedType)request.LiftType,
                    null,
                    request.UpdateMission))
            {
                _log.LogInfo($"Remote loot applied: item={request.ItemId}; count={request.Count}");
                _trace?.Write("LOOT-APPLY", $"item={request.ItemId} count={request.Count}");
            }
            else
                _trace?.Write("LOOT-APPLY-REJECT", $"item={request.ItemId} count={request.Count}");
        }
        catch (Exception exception)
        {
            _log.LogWarning(
                $"Remote loot apply failed: item={request.ItemId}; {exception.Message}");
            _trace?.Write("LOOT-APPLY-ERROR",
                $"item={request.ItemId} error={exception.GetType().Name}:{exception.Message}");
        }
        finally
        {
            _applyingRemoteLoot = false;
        }
    }

    private void UpdateAcceptedMissions(IEnumerable<LootEntry> entries)
    {
        var manager = MissionManager.Instance;
        var data = DataManager.Instance;
        if (manager == null || data == null)
            return;
        foreach (var entry in entries)
        {
            if (!entry.UpdateMission)
                continue;
            try
            {
                var item = data.GetItems(entry.ItemId);
                if (item == null)
                    continue;
                manager.UpdateMissionIntCondition(item, entry.BonusGrade, entry.Count);
                entry.UpdateMission = false;
                _log.LogDebug(
                    $"Remote catch mission updated: item={entry.ItemId}; count={entry.Count}");
            }
            catch (Exception exception)
            {
                _log.LogWarning(
                    $"Remote catch mission update deferred: item={entry.ItemId}; " +
                    exception.Message);
            }
        }
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
        bool __5)
    {
        ProbeBehaviour.Instance?.TraceLootEntry("add-impl", __0?.TID ?? 0, __1, __3, true);
        ProbeBehaviour.Instance?.ReportClientLoot(
            __0?.TID ?? 0, __1, __2, __3, __5);
    }
}

[HarmonyPatch(typeof(LootBox), nameof(LootBox.CheckOverloadedState))]
internal static class LootBoxOverloadTracePatch
{
    private static void Postfix(int tid, ref bool __result)
    {
        var nativeResult = __result;
        ProbeBehaviour.Instance?.TraceOverload(tid, nativeResult, __result);
    }
}

[HarmonyPatch(
    typeof(LootBox),
    nameof(LootBox.RefreshOverweight),
    new[] { typeof(float) })]
internal static class LootBoxCargoRefreshPatch
{
    private static void Postfix(LootBox __instance) =>
        ProbeBehaviour.Instance?.ApplyClientCargoState(__instance);
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
        ProbeBehaviour.Instance?.TraceLootEntry("add-prefix", id, count, liftType, false);
        if (!RemoteCatchPatchBridge.TryCapture(
                id, count, bonusGrade, liftType, getTimes, bUpdateMissionCnt))
            return true;
        __result = true;
        return false;
    }

    private static void Postfix(
        int id,
        int count,
        int bonusGrade,
        LootBox.AutoLiftedType liftType,
        Il2CppSystem.Collections.Generic.List<string> getTimes,
        bool bUpdateMissionCnt,
        bool __result)
    {
        ProbeBehaviour.Instance?.TraceLootEntry("add-postfix", id, count, liftType, __result);
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
        ProbeBehaviour.Instance?.TraceLootEntry("add-ignore-prefix", id, count, liftType, false);
        if (!RemoteCatchPatchBridge.TryCapture(
                id, count, bonusGrade, liftType, getTimes, bUpdateMissionCnt))
            return true;
        __result = true;
        return false;
    }

    private static void Postfix(
        int id,
        int count,
        int bonusGrade,
        LootBox.AutoLiftedType liftType,
        Il2CppSystem.Collections.Generic.List<string> getTimes,
        bool bUpdateMissionCnt,
        bool __result)
    {
        ProbeBehaviour.Instance?.TraceLootEntry("add-ignore-postfix", id, count, liftType, __result);
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
