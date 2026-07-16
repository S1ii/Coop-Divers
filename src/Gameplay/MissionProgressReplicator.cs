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

    internal MissionProgressReplicator(ManualLogSource log) => _log = log;

    internal void Update(SessionRole role, UdpSession session, float now)
    {
        var connected = session != null && session.Connected;
        if (!connected)
        {
            if (_wasConnected)
                ClearNetworkState();
            _wasConnected = false;
            return;
        }
        if (!_wasConnected)
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
        foreach (var pair in new List<KeyValuePair<int, MissionState>>(_pendingClientStates))
            if (ApplyClientState(pair.Value, preserveOriginal: true))
            {
                _clientRevisions[pair.Key] = pair.Value.Revision;
                _pendingClientStates.Remove(pair.Key);
            }
    }

    internal void Clear()
    {
        ClearNetworkState();
        _wasConnected = false;
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
                    reset++;
            }
            foreach (var missionId in new List<int>(_pendingClientStates.Keys))
                if (!_clientRosterIds.Contains(missionId))
                    _pendingClientStates.Remove(missionId);
            _log.LogInfo(
                $"Mission roster applied: revision={roster.Revision}; " +
                $"missions={roster.MissionIds.Length}; reset={reset}");
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

            mission.State = (global::MissionState)state.State;
            mission.Progress = state.Progress;
            mission.ForceUpdateCurrenTask();
            SelectTask(mission, state.CurrentTaskId);
            var task = mission.CurrentTask;
            if (task != null && (state.CurrentTaskId == 0 || task.TID == state.CurrentTaskId))
            {
                var values = new Dictionary<int, int>(state.Conditions.Length);
                foreach (var condition in state.Conditions)
                    values[condition.Id] = condition.Count;
                foreach (var condition in mission.GetInProgressMissionTaskConditionList(task.TID))
                    if (condition != null)
                        condition.NowCount = values.TryGetValue(condition.TID, out var count)
                            ? count
                            : 0;
            }
            SetMembership(manager.InProgressList, mission,
                mission.State == global::MissionState.InProgress);
            SetMembership(manager.NewMissionList, mission,
                mission.State == global::MissionState.Accept);
            if (mission.State is global::MissionState.Clear or global::MissionState.Done)
                manager.ClearedSet.Add(mission.TID);
            else
                manager.ClearedSet.Remove(mission.TID);
            return true;
        }
        catch (Exception exception)
        {
            _log.LogWarning($"Mission state apply failed: {exception.Message}");
            return false;
        }
    }

    private void RestoreClientState()
    {
        foreach (var state in _clientOriginals.Values)
            ApplyClientState(state, preserveOriginal: false);
        _clientOriginals.Clear();
    }

    private void ClearNetworkState()
    {
        RestoreClientState();
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

    private static bool SameIds(int[] left, int[] right) =>
        left.AsSpan().SequenceEqual(right);

    private static uint NextRevision(uint value) => value == uint.MaxValue ? 1 : value + 1;

    private static bool IsTerminal(byte state) =>
        (global::MissionState)state is global::MissionState.Clear or global::MissionState.Done;

    private static bool IsNewer(uint revision, uint previous) =>
        unchecked((int)(revision - previous)) > 0;
}
