using System;
using System.Collections.Generic;
using BepInEx.Logging;

namespace DaveTheDiverMP;

internal sealed class MissionProgressReplicator
{
    private const float SendInterval = 0.25f;
    private readonly ManualLogSource _log;
    private float _nextHostSend;
    private uint _revision;
    private uint _lastRevision;

    internal MissionProgressReplicator(ManualLogSource log) => _log = log;

    internal void Update(SessionRole role, UdpSession session, float now)
    {
        if (session == null || !session.Connected)
            return;
        if (role == SessionRole.Host)
        {
            if (now >= _nextHostSend)
            {
                _nextHostSend = now + SendInterval;
                session.SendMissionState(new MissionState(NextRevision(), ReadActiveConditions()));
            }
            return;
        }
        if (role != SessionRole.Client)
            return;
        while (session.TryTakeMissionState(out var state))
        {
            if (!IsNewer(state.Revision, _lastRevision))
                continue;
            _lastRevision = state.Revision;
            Apply(state);
        }
    }

    internal void Clear()
    {
        _nextHostSend = 0f;
        _revision = 0;
        _lastRevision = 0;
    }

    private MissionConditionState[] ReadActiveConditions()
    {
        var values = new Dictionary<int, int>();
        try
        {
            var manager = MissionManager.Instance;
            if (manager == null)
                return Array.Empty<MissionConditionState>();
            foreach (var mission in manager.InProgressList)
            {
                var task = mission?.CurrentTask;
                if (task == null)
                    continue;
                foreach (var condition in mission.GetInProgressMissionTaskConditionList(task.TID))
                {
                    if (condition != null && condition.IsShowCount && condition.NowCount >= 0 &&
                        (values.ContainsKey(condition.TID) ||
                         values.Count < Protocol.MaxMissionConditions))
                        values[condition.TID] = condition.NowCount;
                }
            }
        }
        catch (Exception exception)
        {
            _log.LogWarning($"Mission state read failed: {exception.Message}");
            return Array.Empty<MissionConditionState>();
        }

        var conditions = new List<MissionConditionState>(values.Count);
        foreach (var pair in values)
            conditions.Add(new MissionConditionState(pair.Key, pair.Value));
        conditions.Sort((left, right) => left.Id.CompareTo(right.Id));
        return conditions.ToArray();
    }

    private static void Apply(MissionState state)
    {
        if (state.Conditions.Length == 0)
            return;
        var values = new Dictionary<int, int>(state.Conditions.Length);
        foreach (var condition in state.Conditions)
            values[condition.Id] = condition.Count;
        var manager = MissionManager.Instance;
        if (manager == null)
            return;
        foreach (var mission in manager.InProgressList)
        {
            var task = mission?.CurrentTask;
            if (task == null)
                continue;
            foreach (var condition in mission.GetInProgressMissionTaskConditionList(task.TID))
                if (condition != null && values.TryGetValue(condition.TID, out var count))
                    condition.NowCount = count;
        }
    }

    private uint NextRevision() => _revision = _revision == uint.MaxValue ? 1u : _revision + 1u;

    private static bool IsNewer(uint revision, uint previous) =>
        unchecked((int)(revision - previous)) > 0;
}
