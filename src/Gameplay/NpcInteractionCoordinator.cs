using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using BepInEx.Logging;
using Common.Contents;
using HarmonyLib;
using UnityEngine;

namespace DaveTheDiverMP;

internal sealed class NpcInteractionCoordinator
{
    private const float PendingSeconds = 10f;
    private const float ActiveSeconds = 300f;
    private const float SnapshotMaxAge = 1f;
    private const float MaxDistance = 4f;

    private enum PendingKind { None, TalkMenu, TalkTarget, MissionNpc }

    private readonly ManualLogSource _log;
    private readonly HashSet<ulong> _canceledTokens = new();
    private readonly Queue<ulong> _canceledTokenOrder = new();
    private ulong _token;
    private uint _revision;
    private uint _sceneId;
    private uint _sceneEpoch;
    private uint _targetId;
    private int _npcId;
    private float _expiresAt;
    private bool _wasConnected;
    private bool _hostBusy;
    private bool _applyingGrant;
    private bool _active;
    private PendingKind _pendingKind;
    private TalkNPCMenu _pendingTalkMenu;
    private TalkTrigger _pendingTalkTrigger;
    private TalkTarget _pendingTalkTarget;
    private MissionTargetNPCController _pendingMissionNpc;

    internal NpcInteractionCoordinator(ManualLogSource log) => _log = log;

    internal bool IsApplyingGrant => _applyingGrant;
    internal bool HasActiveLease => _active;

    internal static void SelfTest()
    {
        if (DecideRequest(true, false, true, true, false) != NpcInteractionResult.Accepted ||
            DecideRequest(false, false, true, true, false) != NpcInteractionResult.Invalid ||
            DecideRequest(true, true, true, true, false) != NpcInteractionResult.Busy ||
            DecideRequest(true, false, false, true, false) != NpcInteractionResult.Invalid ||
            DecideRequest(true, false, true, false, false) != NpcInteractionResult.TooFar ||
            DecideRequest(true, false, true, true, true) != NpcInteractionResult.MissionOwned ||
            !IsCurrentEpoch(4, 4) || IsCurrentEpoch(3, 4) ||
            !ShouldExpire(true, 310f, 311f) || ShouldExpire(true, 310f, 309f) ||
            ShouldExpire(false, 10f, 100f) ||
            !ShouldCancelDelayedRequest(true, true) ||
            ShouldCancelDelayedRequest(false, true) ||
            !ShouldApplyGrant(
                NpcInteractionAction.Granted, NpcInteractionResult.Accepted, true) ||
            ShouldApplyGrant(
                NpcInteractionAction.Granted, NpcInteractionResult.Accepted, false) ||
            ShouldApplyGrant(
                NpcInteractionAction.Released, NpcInteractionResult.Accepted, true) ||
            StableTargetId("talk", 101, "0:Root/2:Npc") !=
                StableTargetId("talk", 101, "0:Root/2:Npc") ||
            StableTargetId("talk", 101, "0:Root/2:Npc") ==
                StableTargetId("mission", 101, "0:Root/2:Npc") ||
            StableTargetId("talk", 101, "0:Root/2:Npc") ==
                StableTargetId("talk", 102, "0:Root/2:Npc") ||
            StableTargetId("talk", 101, "0:Root/2:Npc") ==
                StableTargetId("talk", 101, "0:Root/3:Npc"))
            throw new InvalidOperationException("NPC interaction policy self-test failed");
        if (!TargetsMatch(GetPatchTargets()))
            throw new InvalidOperationException("NPC interaction patch target self-test failed");
    }

    internal void Update(SessionRole role, UdpSession session, uint sceneId, float now)
    {
        var connected = session?.Connected == true;
        if (_wasConnected && !connected)
        {
            LocalClear();
            _canceledTokens.Clear();
            _canceledTokenOrder.Clear();
        }
        _wasConnected = connected;
        if (!connected)
            return;

        while (session.TryTakeNpcInteraction(out var state))
        {
            try
            {
                if (role == SessionRole.Host)
                    ApplyHostPacket(session, state, sceneId, now);
                else if (role == SessionRole.Client)
                    ApplyHostResult(role, session, state, sceneId, now);
            }
            catch (Exception exception)
            {
                _log.LogWarning($"NPC interaction packet rejected: {exception.Message}");
            }
        }

        if (ShouldExpire(_token != 0 || _hostBusy, _expiresAt, now))
            Release(role, session);
    }

    internal bool RequestTalk(
        SessionRole role, UdpSession session, uint sceneId, TalkTarget target)
    {
        if (_applyingGrant || session?.Connected != true)
            return true;
        if (role == SessionRole.Host)
            return BeginHostInteraction();
        var trigger = ResolveTalkTrigger(target);
        return role != SessionRole.Client || Request(
            session, sceneId, target?.npcTid ?? 0, TargetId(trigger),
            PendingKind.TalkTarget, target, null);
    }

    internal bool RequestTalkMenu(
        SessionRole role, UdpSession session, uint sceneId, TalkNPCMenu menu)
    {
        if (_applyingGrant || session?.Connected != true)
            return true;
        if (role == SessionRole.Host)
            return BeginHostInteraction();
        var trigger = TalkTrigger.talkTriggerCurrent;
        return role != SessionRole.Client || Request(
            session, sceneId, trigger?._npcTid ?? 0, TargetId(trigger),
            PendingKind.TalkMenu, null, null, menu, trigger);
    }

    internal bool RequestMissionTalk(
        SessionRole role,
        UdpSession session,
        uint sceneId,
        MissionTargetNPCController npc)
    {
        if (_applyingGrant || session?.Connected != true)
            return true;
        if (role == SessionRole.Host)
            return BeginHostInteraction();
        return role != SessionRole.Client || Request(
            session, sceneId, npc?.GetNpcId() ?? 0, TargetId(npc),
            PendingKind.MissionNpc, null, npc, null, null);
    }

    internal void Release(SessionRole role, UdpSession session)
    {
        if (_token == 0 && !_hostBusy)
            return;
        if (_token != 0 && session?.Connected == true)
            session.SendNpcInteraction(new NpcInteraction(
                _token, _sceneId, _sceneEpoch, _targetId, _revision,
                NpcInteractionAction.Released, _npcId, 0, 0,
                NpcInteractionResult.Accepted));
        LocalClear();
    }

    internal void Clear(SessionRole role, UdpSession session) => Release(role, session);

    private bool BeginHostInteraction()
    {
        if (_token != 0)
            return false;
        if (_hostBusy)
            return true;
        _hostBusy = true;
        _active = true;
        _expiresAt = Time.realtimeSinceStartup + ActiveSeconds;
        return true;
    }

    private bool Request(
        UdpSession session,
        uint sceneId,
        int npcId,
        uint targetId,
        PendingKind kind,
        TalkTarget talkTarget,
        MissionTargetNPCController missionNpc,
        TalkNPCMenu talkMenu = null,
        TalkTrigger talkTrigger = null)
    {
        if (npcId <= 0 || targetId == 0 || _token != 0)
            return false;
        _token = BitConverter.ToUInt64(Guid.NewGuid().ToByteArray(), 0);
        if (_token == 0)
            _token = 1;
        _sceneId = sceneId;
        _sceneEpoch = session.LocalSceneEpoch;
        _targetId = targetId;
        _npcId = npcId;
        _pendingKind = kind;
        _pendingTalkMenu = talkMenu;
        _pendingTalkTrigger = talkTrigger;
        _pendingTalkTarget = talkTarget;
        _pendingMissionNpc = missionNpc;
        _expiresAt = Time.realtimeSinceStartup + PendingSeconds;
        if (_sceneEpoch == 0 || !session.SendNpcInteraction(new NpcInteraction(
                _token, sceneId, _sceneEpoch, targetId, 0,
                NpcInteractionAction.Request, npcId, 0, 0,
                NpcInteractionResult.Pending)))
            LocalClear();
        return false;
    }

    private void ApplyHostPacket(
        UdpSession session, NpcInteraction state, uint sceneId, float now)
    {
        if (state.Action == NpcInteractionAction.Released)
        {
            if (MatchesLease(state))
                LocalClear();
            else
                RememberCancellation(state.Token);
            return;
        }
        if (state.Action != NpcInteractionAction.Request)
            return;

        var canceled = _canceledTokens.Remove(state.Token);
        var targets = ResolveTargets(state.NpcId, state.TargetId);
        var target = targets.Count == 1 ? targets[0] : null;
        var missionOwned = ClassifyMission(state.NpcId, target, out var mission);
        var fresh = session.TryGetFreshRemotePlayerSnapshot(
            now, SnapshotMaxAge, out var snapshot) && snapshot.SceneId == state.SceneId &&
            snapshot.SceneEpoch == state.SceneEpoch;
        var near = fresh && target != null &&
            (target.transform.position - new Vector3(snapshot.X, snapshot.Y, snapshot.Z))
                .sqrMagnitude <= MaxDistance * MaxDistance;
        var result = canceled
            ? NpcInteractionResult.Invalid
            : DecideRequest(
                state.SceneId == sceneId && session.SceneMatches(sceneId) &&
                    IsCurrentEpoch(state.SceneEpoch, session.RemoteSceneEpoch) &&
                    targets.Count == 1,
                _token != 0 || _hostBusy, fresh, near, missionOwned);
        var revision = NextRevision(_revision);
        if (!session.SendNpcInteraction(state with
        {
            Revision = revision,
            Action = NpcInteractionAction.Granted,
            MissionId = mission?.TID ?? 0,
            TaskId = mission?.CurrentTask?.TID ?? 0,
            Result = result
        }))
            return;
        _revision = revision;
        if (result != NpcInteractionResult.Accepted)
            return;
        _token = state.Token;
        _sceneId = state.SceneId;
        _sceneEpoch = state.SceneEpoch;
        _targetId = state.TargetId;
        _npcId = state.NpcId;
        _active = true;
        _expiresAt = now + ActiveSeconds;
    }

    private void ApplyHostResult(
        SessionRole role,
        UdpSession session,
        NpcInteraction state,
        uint sceneId,
        float now)
    {
        if (!MatchesLease(state) || state.SceneId != sceneId ||
            !IsCurrentEpoch(state.SceneEpoch, session.LocalSceneEpoch))
            return;
        if (state.Action == NpcInteractionAction.Released ||
            state.Result != NpcInteractionResult.Accepted)
        {
            LocalClear();
            return;
        }
        if (!ShouldApplyGrant(state.Action, state.Result, _pendingKind != PendingKind.None))
            return;
        _revision = state.Revision;
        _active = true;
        _expiresAt = now + ActiveSeconds;
        try
        {
            _applyingGrant = true;
            switch (_pendingKind)
            {
                case PendingKind.TalkMenu when _pendingTalkMenu != null &&
                                               _pendingTalkTrigger != null:
                    var previousTrigger = TalkTrigger.talkTriggerCurrent;
                    try
                    {
                        TalkTrigger.talkTriggerCurrent = _pendingTalkTrigger;
                        _pendingTalkMenu.OnOK_Impl();
                    }
                    finally
                    {
                        TalkTrigger.talkTriggerCurrent = previousTrigger;
                    }
                    break;
                case PendingKind.TalkTarget when _pendingTalkTarget != null:
                    _pendingTalkTarget.Open();
                    break;
                case PendingKind.MissionNpc when _pendingMissionNpc != null:
                    _pendingMissionNpc.OnTalkRequested();
                    break;
                default:
                    throw new InvalidOperationException("original NPC interaction is unavailable");
            }
            _pendingKind = PendingKind.None;
            _pendingTalkMenu = null;
            _pendingTalkTrigger = null;
            _pendingTalkTarget = null;
            _pendingMissionNpc = null;
        }
        catch (Exception exception)
        {
            _log.LogWarning($"NPC interaction grant failed: {exception.Message}");
            Release(role, session);
        }
        finally
        {
            _applyingGrant = false;
        }
    }

    private bool MatchesLease(NpcInteraction state) =>
        state.Token == _token && state.SceneId == _sceneId &&
        state.SceneEpoch == _sceneEpoch && state.TargetId == _targetId &&
        state.NpcId == _npcId;

    private static bool ClassifyMission(
        int npcId, Component target, out MissionData mission)
    {
        mission = null;
        var owned = false;
        foreach (var npc in UnityEngine.Object.FindObjectsByType<MissionTargetNPCController>(
                     FindObjectsSortMode.None))
        {
            if (npc == null || target == null || npc.GetNpcId() != npcId ||
                (target is MissionTargetNPCController && npc != target ||
                 target is not MissionTargetNPCController &&
                 (npc.transform.position - target.transform.position).sqrMagnitude > 0.25f))
                continue;
            var ready = npc.GetReadyTaskDataIfAvailable();
            mission ??= ready;
            owned |= npc.CurAlertState != MissionTargetNPCController.EAlertState.None ||
                npc.HaveTask() || npc.IsReadyClearTask() ||
                npc.IsNPCTargetForTalkWithExclamation() ||
                npc.IsNPCTargetForCarryWithExclamation() ||
                npc.IsNPCTargetForClearTask() || ready != null;
        }
        return owned;
    }

    private static TalkTrigger ResolveTalkTrigger(TalkTarget target)
    {
        if (target == null)
            return null;
        if (target._talkTrigger != null)
            return target._talkTrigger;
        var current = TalkTrigger.talkTriggerCurrent;
        if (current?._talkTarget == target)
            return current;
        TalkTrigger match = null;
        foreach (var trigger in UnityEngine.Object.FindObjectsByType<TalkTrigger>(
                     FindObjectsSortMode.None))
        {
            if (trigger?._talkTarget != target)
                continue;
            if (match != null)
                return null;
            match = trigger;
        }
        return match;
    }

    private static List<Component> ResolveTargets(int npcId, uint targetId)
    {
        var matches = new List<Component>();
        foreach (var trigger in UnityEngine.Object.FindObjectsByType<TalkTrigger>(
                     FindObjectsSortMode.None))
            if (trigger != null && trigger._npcTid == npcId && TargetId(trigger) == targetId)
                matches.Add(trigger);
        foreach (var npc in UnityEngine.Object.FindObjectsByType<MissionTargetNPCController>(
                     FindObjectsSortMode.None))
            if (npc != null && npc.GetNpcId() == npcId && TargetId(npc) == targetId)
                matches.Add(npc);
        return matches;
    }

    private static uint TargetId(Component component)
    {
        if (component == null)
            return 0;
        var path = new StringBuilder();
        for (var current = component.transform; current != null; current = current.parent)
            path.Insert(0, $"/{current.GetSiblingIndex()}:{current.name}");
        return StableTargetId(
            component is MissionTargetNPCController ? "mission" : "talk",
            component is MissionTargetNPCController npc ? npc.GetNpcId() :
                ((TalkTrigger)component)._npcTid,
            path.ToString());
    }

    private static uint StableTargetId(string kind, int npcId, string path) =>
        Protocol.SceneId($"{kind}|{npcId}|{path}");

    private static NpcInteractionResult DecideRequest(
        bool identityMatches, bool busy, bool fresh, bool near, bool missionOwned)
    {
        if (!identityMatches)
            return NpcInteractionResult.Invalid;
        if (busy)
            return NpcInteractionResult.Busy;
        if (!fresh)
            return NpcInteractionResult.Invalid;
        if (!near)
            return NpcInteractionResult.TooFar;
        return missionOwned ? NpcInteractionResult.MissionOwned : NpcInteractionResult.Accepted;
    }

    private static bool IsCurrentEpoch(uint packetEpoch, uint currentEpoch) =>
        packetEpoch != 0 && packetEpoch == currentEpoch;

    private static bool ShouldExpire(bool leased, float expiresAt, float now) =>
        leased && now >= expiresAt;

    private static bool ShouldCancelDelayedRequest(bool canceled, bool isRequest) =>
        canceled && isRequest;

    private static bool ShouldApplyGrant(
        NpcInteractionAction action, NpcInteractionResult result, bool hasPendingInvocation) =>
        action == NpcInteractionAction.Granted && result == NpcInteractionResult.Accepted &&
        hasPendingInvocation;

    private void RememberCancellation(ulong token)
    {
        if (!_canceledTokens.Add(token))
            return;
        _canceledTokenOrder.Enqueue(token);
        if (_canceledTokenOrder.Count > 64)
            _canceledTokens.Remove(_canceledTokenOrder.Dequeue());
    }

    private void LocalClear()
    {
        _token = 0;
        _revision = 0;
        _sceneId = 0;
        _sceneEpoch = 0;
        _targetId = 0;
        _npcId = 0;
        _expiresAt = 0f;
        _hostBusy = false;
        _applyingGrant = false;
        _active = false;
        _pendingKind = PendingKind.None;
        _pendingTalkMenu = null;
        _pendingTalkTrigger = null;
        _pendingTalkTarget = null;
        _pendingMissionNpc = null;
    }

    private static MethodBase[] GetPatchTargets() => new[]
    {
        AccessTools.DeclaredMethod(
            typeof(TalkNPCMenu), nameof(TalkNPCMenu.OnOK_Impl), Type.EmptyTypes),
        AccessTools.DeclaredMethod(typeof(TalkTarget), nameof(TalkTarget.Open), Type.EmptyTypes),
        AccessTools.DeclaredMethod(
            typeof(MissionTargetNPCController),
            nameof(MissionTargetNPCController.OnTalkRequested), Type.EmptyTypes)
    };

    private static bool TargetsMatch(MethodBase[] targets) =>
        targets.Length == 3 && targets[0] != null && targets[1] != null && targets[2] != null;

    private static uint NextRevision(uint revision) => revision == uint.MaxValue ? 1 : revision + 1;
}

[HarmonyPatch]
internal static class NpcTalkMenuLeasePatch
{
    private static MethodBase TargetMethod() => AccessTools.DeclaredMethod(
        typeof(TalkNPCMenu), nameof(TalkNPCMenu.OnOK_Impl), Type.EmptyTypes);

    private static bool Prefix(TalkNPCMenu __instance) =>
        ProbeBehaviour.Instance?.RequestNpcTalkMenu(__instance) ?? true;
}

[HarmonyPatch]
internal static class NpcTalkLeasePatch
{
    private static MethodBase TargetMethod() => AccessTools.DeclaredMethod(
        typeof(TalkTarget), nameof(TalkTarget.Open), Type.EmptyTypes);

    private static bool Prefix(TalkTarget __instance) =>
        ProbeBehaviour.Instance?.RequestNpcTalk(__instance) ?? true;
}

[HarmonyPatch]
internal static class MissionNpcTalkAuthorityPatch
{
    private static MethodBase TargetMethod() => AccessTools.DeclaredMethod(
        typeof(MissionTargetNPCController),
        nameof(MissionTargetNPCController.OnTalkRequested), Type.EmptyTypes);

    private static bool Prefix(MissionTargetNPCController __instance) =>
        ProbeBehaviour.Instance?.RequestMissionNpcTalk(__instance) ?? true;
}
