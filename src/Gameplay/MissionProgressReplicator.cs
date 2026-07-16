using System;
using System.Collections.Generic;
using BepInEx.Logging;

namespace DaveTheDiverMP;

internal sealed class MissionProgressReplicator
{
    private const float ScanInterval = 0.25f;
    private const float KeyframeInterval = 5f;
    private const float FullKeyframeInterval = 60f;
    private readonly ManualLogSource _log;
    private readonly SessionTrace _trace;
    private readonly Dictionary<int, MissionState> _hostStates = new();
    private readonly Dictionary<int, uint> _clientRevisions = new();
    private readonly Dictionary<int, MissionState> _pendingClientStates = new();
    private readonly Dictionary<int, MissionState> _clientOriginals = new();
    private int[] _hostRosterIds = Array.Empty<int>();
    private readonly HashSet<int> _clientRosterIds = new();
    private float _nextHostScan;
    private float _nextHostKeyframe;
    private float _nextHostFullKeyframe;
    private bool _wasConnected;
    private bool _hostRosterQueued;
    private bool _hasPendingClientRoster;
    private uint _hostRosterRevision;
    private uint _clientRosterRevision;
    private MissionRoster _pendingClientRoster;

    internal MissionProgressReplicator(ManualLogSource log, SessionTrace trace)
    {
        _log = log;
        _trace = trace;
    }

    internal static void SelfTest()
    {
        var conditions = new[] { new MissionConditionState(100, 3) };
        var local = new MissionState(1, 10, 2, 2, 20, conditions);
        var keyframe = new MissionState(2, 10, 2, 2, 20, conditions);
        var changed = new MissionState(3, 10, 2, 2, 20,
            new[] { new MissionConditionState(100, 4) });
        if (ShouldApplyClientState(local, keyframe) || !ShouldApplyClientState(local, changed))
            throw new InvalidOperationException("Mission client apply decision failed");
        if (ShouldApplyTerminalState(true, (byte)global::MissionState.Clear) ||
            !ShouldApplyTerminalState(false, (byte)global::MissionState.Clear) ||
            !ShouldApplyTerminalState(true, (byte)global::MissionState.InProgress))
            throw new InvalidOperationException("Mission terminal deferral failed");
        if (ResolveConditionCount(Array.Empty<MissionConditionState>(), 100) != 0 ||
            ResolveConditionCount(conditions, 100) != 3 ||
            ResolveConditionCount(conditions, 200) != 0)
            throw new InvalidOperationException("Mission condition snapshot merge failed");
        if (!ShouldRetainUnrestoredOriginal(true) || !ShouldRetainUnrestoredOriginal(false))
            throw new InvalidOperationException("Mission original restore retry policy failed");
    }

    internal void Update(SessionRole role, UdpSession session, float now, bool deferTerminalStates)
    {
        var connected = session != null && session.Connected;
        if (!connected)
        {
            RestoreClientOriginals();
            if (_wasConnected)
                ClearNetworkState();
            _wasConnected = false;
            return;
        }
        if (!_wasConnected)
        {
            RestoreClientOriginals();
            _hostStates.Clear();
            _clientRevisions.Clear();
            _pendingClientStates.Clear();
            _hostRosterIds = Array.Empty<int>();
            _clientRosterIds.Clear();
            _hostRosterQueued = false;
            _hasPendingClientRoster = false;
            _hostRosterRevision = 0;
            _clientRosterRevision = 0;
            _nextHostScan = 0f;
            _nextHostKeyframe = now + KeyframeInterval;
            _nextHostFullKeyframe = now + FullKeyframeInterval;
        }
        _wasConnected = true;

        if (role == SessionRole.Host)
        {
            while (session.TryTakeMissionRoster(out _))
            {
            }
            if (now >= _nextHostScan)
            {
                _nextHostScan = now + ScanInterval;
                PublishHostChanges(
                    session,
                    now >= _nextHostKeyframe,
                    now >= _nextHostFullKeyframe);
                if (now >= _nextHostKeyframe)
                    _nextHostKeyframe = now + KeyframeInterval;
                if (now >= _nextHostFullKeyframe)
                    _nextHostFullKeyframe = now + FullKeyframeInterval;
            }
            return;
        }
        if (role != SessionRole.Client)
            return;

        while (session.TryTakeMissionRoster(out var roster))
        {
            var previous = _hasPendingClientRoster
                ? _pendingClientRoster.Revision
                : _clientRosterRevision;
            if (IsNewer(roster.Revision, previous))
            {
                _pendingClientRoster = roster;
                _hasPendingClientRoster = true;
            }
        }
        if (_hasPendingClientRoster && ApplyClientRoster(_pendingClientRoster))
        {
            _clientRosterRevision = _pendingClientRoster.Revision;
            _hasPendingClientRoster = false;
        }

        while (session.TryTakeMissionState(out var state))
        {
            if (_clientRosterRevision != 0 && !_clientRosterIds.Contains(state.MissionId))
                continue;
            _clientRevisions.TryGetValue(state.MissionId, out var previous);
            if (_pendingClientStates.TryGetValue(state.MissionId, out var pending) &&
                IsNewer(pending.Revision, previous))
                previous = pending.Revision;
            if (!IsNewer(state.Revision, previous))
                continue;
            _pendingClientStates[state.MissionId] = state;
        }
        var changed = false;
        foreach (var pair in new List<KeyValuePair<int, MissionState>>(_pendingClientStates))
        {
            if (!ShouldApplyTerminalState(deferTerminalStates, pair.Value.State))
                continue;
            var mission = MissionManager.Instance?.GetMissionData(pair.Key);
            if (mission == null)
                continue;
            if (!ShouldApplyClientState(Capture(mission, pair.Value.Revision), pair.Value))
            {
                _clientRevisions[pair.Key] = pair.Value.Revision;
                _pendingClientStates.Remove(pair.Key);
                _trace?.Write("MISSION-SKIP",
                    $"mission={pair.Key} revision={pair.Value.Revision} reason=identical");
                continue;
            }
            if (!ApplyClientState(pair.Value, preserveOriginal: true))
                continue;
            _clientRevisions[pair.Key] = pair.Value.Revision;
            _pendingClientStates.Remove(pair.Key);
            changed = true;
        }
        if (changed)
            RefreshClientMissionUI("state");
    }

    internal void Clear()
    {
        RestoreClientOriginals();
        ClearNetworkState();
        _wasConnected = false;
    }

    internal void ForceHostKeyframe()
    {
        _hostRosterQueued = false;
        _nextHostScan = 0f;
        _nextHostKeyframe = 0f;
        _nextHostFullKeyframe = 0f;
    }

    private void PublishHostChanges(UdpSession session, bool keyframe, bool fullKeyframe)
    {
        var manager = MissionManager.Instance;
        if (manager == null)
            return;
        try
        {
            var roster = new List<int>();
            foreach (var mission in manager.MissionDictionary.Values)
            {
                if (mission == null || mission.TID <= 0)
                    continue;
                if (mission.State != global::MissionState.NotStarted)
                    roster.Add(mission.TID);
                _hostStates.TryGetValue(mission.TID, out var previous);
                if (mission.State == global::MissionState.NotStarted && previous.MissionId == 0)
                    continue;
                var current = Capture(mission, previous.Revision);
                var unchanged = SameContent(previous, current);
                if (unchanged && (!keyframe || IsTerminal(current.State) && !fullKeyframe))
                    continue;
                current = current with { Revision = NextRevision(previous.Revision) };
                _hostStates[mission.TID] = current;
                session.SendMissionState(current);
                _trace?.Write("MISSION-SEND",
                    $"mission={current.MissionId} revision={current.Revision} " +
                    $"state={current.State} progress={current.Progress} " +
                    $"conditions={FormatConditions(current.Conditions)}");
            }
            roster.Sort();
            PublishHostRoster(session, roster.ToArray());
        }
        catch (Exception exception)
        {
            _log.LogWarning($"Mission state read failed: {exception.Message}");
        }
    }

    private void PublishHostRoster(UdpSession session, int[] missionIds)
    {
        if (_hostRosterRevision == 0 || !SameIds(_hostRosterIds, missionIds))
        {
            _hostRosterIds = missionIds;
            _hostRosterRevision = NextRevision(_hostRosterRevision);
            _hostRosterQueued = false;
        }
        if (_hostRosterQueued)
            return;
        if (missionIds.Length > Protocol.MaxMissionRosterEntries)
        {
            _log.LogError($"Mission roster too large: {missionIds.Length}");
            return;
        }
        if (session.SendMissionRoster(new MissionRoster(_hostRosterRevision, missionIds)))
        {
            _hostRosterQueued = true;
            _log.LogInfo(
                $"Mission roster sent: revision={_hostRosterRevision}; missions={missionIds.Length}");
        }
    }

    private bool ApplyClientRoster(MissionRoster roster)
    {
        try
        {
            var manager = MissionManager.Instance;
            if (manager == null)
                return false;
            _clientRosterIds.Clear();
            foreach (var missionId in roster.MissionIds)
                _clientRosterIds.Add(missionId);

            var reset = 0;
            var resetIds = new List<int>();
            foreach (var mission in manager.MissionDictionary.Values)
            {
                if (mission == null || mission.TID <= 0 ||
                    mission.State == global::MissionState.NotStarted ||
                    _clientRosterIds.Contains(mission.TID))
                    continue;
                var state = new MissionState(
                    1, mission.TID, 0, (byte)global::MissionState.NotStarted, 0,
                    Array.Empty<MissionConditionState>());
                if (ApplyClientState(state, preserveOriginal: true))
                {
                    reset++;
                    resetIds.Add(mission.TID);
                }
            }
            foreach (var missionId in new List<int>(_pendingClientStates.Keys))
                if (!_clientRosterIds.Contains(missionId))
                    _pendingClientStates.Remove(missionId);
            _log.LogInfo(
                $"Mission roster applied: revision={roster.Revision}; " +
                $"missions={roster.MissionIds.Length}; reset={reset}");
            RefreshClientMissionUI("roster");
            _trace?.Write("MISSION-ROSTER",
                $"revision={roster.Revision} missions={roster.MissionIds.Length} " +
                $"reset={reset} ids={string.Join(',', resetIds)}");
            return true;
        }
        catch (Exception exception)
        {
            _log.LogWarning($"Mission roster apply failed: {exception.Message}");
            return false;
        }
    }

    private MissionState Capture(MissionData mission, uint revision)
    {
        var task = mission.CurrentTask;
        var conditions = new List<MissionConditionState>();
        if (task != null)
        {
            foreach (var condition in mission.GetInProgressMissionTaskConditionList(task.TID))
            {
                if (condition == null || condition.TID <= 0 || condition.NowCount < 0 ||
                    conditions.Count >= Protocol.MaxMissionConditions)
                    continue;
                conditions.Add(new MissionConditionState(condition.TID, condition.NowCount));
            }
        }
        conditions.Sort((left, right) => left.Id.CompareTo(right.Id));
        return new MissionState(
            revision,
            mission.TID,
            Math.Max(0, mission.Progress),
            (byte)mission.State,
            task?.TID ?? 0,
            conditions.ToArray());
    }

    private bool ApplyClientState(MissionState state, bool preserveOriginal)
    {
        try
        {
            var manager = MissionManager.Instance;
            var mission = manager?.GetMissionData(state.MissionId);
            if (mission == null)
                return false;
            if (preserveOriginal && !_clientOriginals.ContainsKey(state.MissionId))
                _clientOriginals[state.MissionId] = Capture(mission, 1);

            var probe = ProbeBehaviour.Instance;
            probe?.BeginRemoteMissionApply();
            try
            {
                mission.State = (global::MissionState)state.State;
                mission.Progress = state.Progress;
                mission.ForceUpdateCurrenTask();
                SelectTask(mission, state.CurrentTaskId);
                var task = mission.CurrentTask;
                if (task != null && (state.CurrentTaskId == 0 || task.TID == state.CurrentTaskId))
                {
                    foreach (var condition in mission.GetInProgressMissionTaskConditionList(task.TID))
                        if (condition != null)
                            condition.NowCount = ResolveConditionCount(state.Conditions, condition.TID);
                }
                SetMembership(manager.InProgressList, mission,
                    mission.State == global::MissionState.InProgress);
                SetMembership(manager.NewMissionList, mission,
                    mission.State == global::MissionState.Accept);
                if (mission.State is global::MissionState.Clear or global::MissionState.Done)
                    manager.ClearedSet.Add(mission.TID);
                else
                    manager.ClearedSet.Remove(mission.TID);
            }
            finally
            {
                probe?.EndRemoteMissionApply();
            }
            _trace?.Write("MISSION-APPLY",
                $"mission={state.MissionId} revision={state.Revision} " +
                $"state={state.State} progress={state.Progress} " +
                $"conditions={FormatConditions(state.Conditions)}");
            return true;
        }
        catch (Exception exception)
        {
            _log.LogWarning($"Mission state apply failed: {exception.Message}");
            return false;
        }
    }

    internal bool TryRestoreClientOriginals()
    {
        RestoreClientOriginals();
        return _clientOriginals.Count == 0;
    }

    private void RestoreClientOriginals()
    {
        if (_clientOriginals.Count == 0)
            return;
        var restored = new List<int>();
        if (MissionManager.Instance != null)
        {
            foreach (var pair in _clientOriginals)
                if (ApplyClientState(pair.Value, preserveOriginal: false))
                    restored.Add(pair.Key);
        }
        foreach (var missionId in restored)
            _clientOriginals.Remove(missionId);
        if (restored.Count > 0)
            RefreshClientMissionUI("restore");
    }

    private void RefreshClientMissionUI(string source)
    {
        try
        {
            var manager = MissionManager.Instance;
            if (manager == null)
                return;
            manager.OrderInProgressList();
            manager.RaiseInProgressChanged();
            manager.RefreshMissionEventListener();
            _trace?.Write("MISSION-HUD", $"refreshed source={source}");
        }
        catch (Exception exception)
        {
            _log.LogWarning($"Mission HUD refresh failed: {exception.Message}");
            _trace?.Write("MISSION-HUD-ERROR",
                $"source={source} error={exception.GetType().Name}:{exception.Message}");
        }
    }

    private void ClearNetworkState()
    {
        _hostStates.Clear();
        _clientRevisions.Clear();
        _pendingClientStates.Clear();
        _hostRosterIds = Array.Empty<int>();
        _clientRosterIds.Clear();
        _hostRosterQueued = false;
        _hasPendingClientRoster = false;
        _hostRosterRevision = 0;
        _clientRosterRevision = 0;
        _pendingClientRoster = default;
        _nextHostScan = 0f;
        _nextHostKeyframe = 0f;
        _nextHostFullKeyframe = 0f;
    }

    private static void SetMembership(
        Il2CppSystem.Collections.Generic.List<MissionData> list,
        MissionData mission,
        bool present)
    {
        if (present)
        {
            if (!list.Contains(mission))
                list.Add(mission);
        }
        else
            list.Remove(mission);
    }

    private static void SelectTask(MissionData mission, int taskId)
    {
        if (taskId == 0 || mission.CurrentTask?.TID == taskId)
            return;
        var node = mission.FirstTaskNode;
        while (node != null)
        {
            if (node.Value?.TID == taskId)
            {
                mission.m_CurrentTaskNode = node;
                return;
            }
            node = node.Next;
        }
    }

    private static bool SameContent(MissionState left, MissionState right)
    {
        if (left.MissionId != right.MissionId || left.Progress != right.Progress ||
            left.State != right.State || left.CurrentTaskId != right.CurrentTaskId ||
            left.Conditions == null || right.Conditions == null ||
            left.Conditions.Length != right.Conditions.Length)
            return false;
        for (var index = 0; index < left.Conditions.Length; index++)
            if (left.Conditions[index] != right.Conditions[index])
                return false;
        return true;
    }

    private static bool ShouldApplyClientState(MissionState local, MissionState remote) =>
        !SameContent(local, remote);

    private static bool ShouldApplyTerminalState(bool deferTerminalStates, byte state) =>
        !deferTerminalStates || !IsTerminal(state);

    private static bool ShouldRetainUnrestoredOriginal(bool _) => true;

    private static int ResolveConditionCount(MissionConditionState[] conditions, int id)
    {
        // Snapshots cap at 64 conditions; index if that limit grows.
        foreach (var condition in conditions)
            if (condition.Id == id)
                return condition.Count;
        return 0;
    }

    private static bool SameIds(int[] left, int[] right) =>
        left.AsSpan().SequenceEqual(right);

    private static uint NextRevision(uint value) => value == uint.MaxValue ? 1 : value + 1;

    private static bool IsTerminal(byte state) =>
        (global::MissionState)state is global::MissionState.Clear or global::MissionState.Done;

    private static bool IsNewer(uint revision, uint previous) =>
        unchecked((int)(revision - previous)) > 0;

    private static string FormatConditions(MissionConditionState[] conditions)
    {
        if (conditions == null || conditions.Length == 0)
            return "none";
        var parts = new string[Math.Min(conditions.Length, 8)];
        for (var index = 0; index < parts.Length; index++)
            parts[index] = $"{conditions[index].Id}:{conditions[index].Count}";
        return string.Join(',', parts);
    }
}
