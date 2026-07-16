using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using DR.GameData;
using DR.Save;
using HarmonyLib;
using UnityEngine;

namespace DaveTheDiverMP;

internal enum ManagerDomain : byte
{
    MainSushi = 1,
    JungleSushi = 2,
    Dialogue = 3,
    Betting = 4,
    VipCooking = 5,
    JungleMiniGame = 6,
    Karaoke = 7,
    SushiMenu = 8,
    SushiWasabi = 9,
    SushiTable = 10,
    Story = 11,
    Day = 12,
    Timeline = 13,
    InsectBattle = 14,
    SeahorseRace = 15,
    Scenario = 16
}

internal enum ManagerAction : byte
{
    Start = 1,
    LastSpurt = 2,
    Finish = 3,
    Continue = 4,
    Skip = 5,
    FirstChoice = 6,
    SecondChoice = 7,
    Result = 8,
    MenuRecipe = 9,
    MenuCount = 10,
    MenuFlags = 11,
    TableState = 12,
    CurrentChapter = 13,
    ReservedChapter = 14,
    EventCleared = 15,
    EventRemoved = 16,
    CutsceneMarked = 17,
    CutsceneUnmarked = 18,
    IntermissionMarked = 19,
    IntermissionUnmarked = 20,
    DayDate = 21,
    DayTime = 22,
    Weather = 23,
    TimelineStart = 24,
    TimelineFinish = 25,
    State = 26,
    Goal = 27,
    ResetEvents = 28,
    ResetCutscenes = 29,
    ResetIntermissions = 30,
    TimelineProgress = 31,
    ScenarioStarted = 32,
    ScenarioNode = 33,
    ScenarioFinished = 34,
    DialogueStarted = 35,
    DialogueNode = 36,
    DialogueFinished = 37,
    PhoneCall = 38,
    PhoneAnswered = 39
}

internal sealed class ManagerEventReplicator
{
    private const int MaxTimelineGeneration = 0x3fffffff;

    private enum RestoreDecision
    {
        None,
        Retry
    }

    private readonly record struct MenuSlotState(
        int RecipeId, int NowCount, int MaxCount, int Flags);

    private enum ScenarioStartKind { Normal, Branch }
    internal enum DialogueStartKind { Normal, Arguments, Small, VisualNovel }
    private enum CallbackIdentityDecision { None, Invoke, Clear }
    private enum TimelineStartRoute { ByTid, ByController }
    private enum TimelineStartDecision { ReplayByTid, ReplayByController, ConsumeSpectator }
    private enum TimelineCallbackDecision { None, Wait, Invoke }
    private enum TimelineFinishDecision { WaitForStart, Wait, Invoke }
    private enum ClientTimelineState
    {
        AwaitingHostStart,
        AwaitingClientIntent,
        Starting,
        Playing,
        Spectating,
        AwaitingHostFinish,
        Finished
    }
    internal readonly record struct ScenarioStartObservation(
        string BundleId, bool WasPlaying, int PreviousKey, bool Allowed);

    private sealed class PendingScenarioStart
    {
        internal ScenarioStartKind Kind;
        internal string BundleId;
        internal int BundleKey;
        internal ScenarioBranchData BranchData;
        internal Il2CppSystem.Collections.Generic.List<string> Arguments;
        internal Il2CppSystem.Action<bool> Callback;
        internal bool UseButton;
        internal bool ShowCurtain;
        internal bool IgnorePlaying;
    }

    private sealed class PendingDialogueStart
    {
        internal DialogueStartKind Kind;
        internal string BundleId;
        internal int BundleKey;
        internal Il2CppSystem.Collections.Generic.List<string> Arguments;
        internal Il2CppSystem.Collections.Generic.List<DialogueEntry> Entries;
        internal ScenarioButtonInfo ButtonInfo;
        internal Il2CppSystem.Action<bool> Callback;
        internal Il2CppSystem.Action<int> ChoiceCallback;
        internal bool UseButton;
        internal bool ShowCurtain;
    }

    private sealed class TimelineLease
    {
        internal int Tid { get; init; }
        internal int Generation { get; init; }
        internal TimelineStartRoute Route { get; init; }
        internal ClientTimelineState State;
        internal bool HasIntent;
        internal TimelineController Controller;
        internal int ControllerId;
        internal Il2CppSystem.Action OnStart;
        internal Il2CppSystem.Action<bool> OnFinished;
        internal bool ApplyOffset = true;
        internal Il2CppSystem.Nullable<Vector3> CustomPos;
        internal bool StartObserved;
        internal bool StartDelivered;
        internal bool HostFinishObserved;
        internal bool RelayFinishObserved;
        internal bool FinishDelivered;
        internal bool Skipped;
    }

    private static readonly MethodInfo MainSushiFinish =
        AccessTools.Method(typeof(SushiBarManager), "OnFinishEveningTime");
    private static readonly MethodInfo JungleSushiFinish =
        AccessTools.Method(typeof(JDLC.JungleSushiBarSceneManager), "OnCloseSushiBar");
    private static readonly MethodInfo VipJudgeWinner =
        AccessTools.Method(typeof(MiniGame.BattleVIP.VIPCookingManager), "JudgeWinner");
    private static readonly MethodInfo SetSalesMenu =
        AccessTools.PropertySetter(typeof(SushiBarAnalyticsTodayData), nameof(SushiBarAnalyticsTodayData.SalesMenu));
    private static readonly MethodInfo SetSalesEtc =
        AccessTools.PropertySetter(typeof(SushiBarAnalyticsTodayData), nameof(SushiBarAnalyticsTodayData.SalesEtc));
    private static readonly MethodInfo SetStaffTips =
        AccessTools.PropertySetter(typeof(SushiBarAnalyticsTodayData), nameof(SushiBarAnalyticsTodayData.StaffTips));
    private static readonly MethodInfo SetLikeCount =
        AccessTools.PropertySetter(typeof(SushiBarAnalyticsData), nameof(SushiBarAnalyticsData.LikeCount));

    private readonly ManualLogSource _log;
    private readonly Dictionary<ManagerDomain, SortedDictionary<uint, ManagerEvent>>
        _pendingHostEvents = new();
    private readonly Queue<ManagerEvent> _outboundEvents = new();
    private readonly Dictionary<ManagerDomain, uint> _hostRevisions = new();
    private readonly Dictionary<ManagerDomain, uint> _clientManagerRevisions = new();
    private readonly Dictionary<int, MenuSlotState> _hostMenuSlots = new();
    private readonly int[] _hostWasabi = new int[(int)SushiBar.Place.Max];
    private uint _sushiRevision;
    private uint _clientSushiRevision;
    private SushiResultState? _pendingSushiResult;
    private bool _applying;
    private bool _applyingScenarioAuthority;
    private bool _applyingDialogueAuthority;
    private int _suppressPublish;
    private float _nextSushiScan;
    private bool _wasConnected;
    private bool _storySnapshotPublished;
    private float _nextStoryScan;
    private int _hostCurrentChapter = int.MinValue;
    private int _hostReservedChapter = int.MinValue;
    private readonly HashSet<int> _hostEvents = new();
    private readonly HashSet<int> _hostCutscenes = new();
    private readonly HashSet<int> _hostIntermissions = new();
    private long _hostDayTicks = long.MinValue;
    private int _hostDayTime = int.MinValue;
    private int _hostWeather = int.MinValue;
    private float _nextDayScan;
    private uint _sceneId;
    private int _clientTimelineTid;
    private int _hostTimelineTid;
    private int _timelineSyncTid;
    private uint _clientTimelineHostTick;
    private double _clientTimelineTime;
    private bool _clientTimelinePlaying;
    private float _nextTimelineSync;
    private TimelineLease _clientTimelineLease;
    private int _hostTimelineRouteTid;
    private TimelineStartRoute _hostTimelineRoute;
    private int _hostTimelineGeneration;
    private TimelineStartRoute _hostTimelineActiveRoute;
    private bool _hasHostClockOffset;
    private uint _hostClockOffset;
    private int? _originalCurrentChapter;
    private int? _originalReservedChapter;
    private long? _originalDayTicks;
    private int? _originalDayTime;
    private int? _originalWeather;
    private readonly Dictionary<int, bool> _originalEvents = new();
    private readonly Dictionary<int, bool> _originalCutscenes = new();
    private readonly Dictionary<int, bool> _originalIntermissions = new();
    private bool _restorePending;
    private bool _activeSessionSnapshotPublished;
    private string _hostScenarioBundleId = string.Empty;
    private int _hostScenarioBundleKey;
    private int _hostScenarioNodeId = -1;
    private int _hostDialogueBundleKey;
    private int _hostDialogueIndex = -1;
    private string _clientScenarioBundleId = string.Empty;
    private int _clientScenarioBundleKey;
    private int _clientScenarioNodeId = -1;
    private int _clientDialogueBundleKey;
    private int _clientDialogueIndex = -1;
    private int _blockedClientScenarioNodeKey;
    private DRSequence _blockedClientScenarioNode;
    private PendingScenarioStart _pendingClientScenarioStart;
    private PendingDialogueStart _pendingClientDialogueStart;
    private bool _clientScenarioSpectating;
    private bool _clientDialogueSpectating;
    private int _hostPhoneTid;
    private int _clientPhoneTid;
    private int _pendingClientPhoneTid;
    private Il2CppSystem.Action<bool> _pendingClientPhoneCallback;
    private string _pendingHostScenarioStartBundleId = string.Empty;

    internal ManagerEventReplicator(ManualLogSource log)
    {
        _log = log;
    }

    internal static void SelfTest()
    {
        var callbackReplicator = new ManagerEventReplicator(null);
        var scenarioCancels = 0;
        callbackReplicator._pendingClientScenarioStart = new PendingScenarioStart
        {
            Callback = (Il2CppSystem.Action<bool>)(Action<bool>)(result =>
                scenarioCancels += result ? 100 : 1)
        };
        callbackReplicator.CancelPendingScenarioStart();
        callbackReplicator.CancelPendingScenarioStart();
        var dialogueCancels = 0;
        callbackReplicator._pendingClientDialogueStart = new PendingDialogueStart
        {
            ChoiceCallback = (Il2CppSystem.Action<int>)(Action<int>)(choice =>
                dialogueCancels += choice == -1 ? 1 : 100)
        };
        callbackReplicator.CancelPendingDialogueStart();
        callbackReplicator.CancelPendingDialogueStart();
        var timelineStarts = 0;
        var timelineCancels = 0;
        var timelineLease = new TimelineLease
        {
            OnStart = (Il2CppSystem.Action)(Action)(() => timelineStarts++),
            OnFinished = (Il2CppSystem.Action<bool>)(Action<bool>)(skipped =>
                timelineCancels += skipped ? 1 : 100)
        };
        callbackReplicator.CancelTimelineLease(timelineLease);
        callbackReplicator.CancelTimelineLease(timelineLease);
        var dialoguePending = new SortedDictionary<uint, ManagerEvent>
        {
            [1] = new ManagerEvent(1, 0, 0, (byte)ManagerDomain.Dialogue, 0, 0, 0)
        };
        var storyPending = new SortedDictionary<uint, ManagerEvent>
        {
            [2] = new ManagerEvent(2, 0, 0, (byte)ManagerDomain.Story, 0, 0, 0)
        };
        uint dialogueRevision = 0;
        uint storyRevision = 0;
        var applied = new List<uint>();
        ApplyPendingLane(dialoguePending, ref dialogueRevision, _ => false);
        ApplyPendingLane(storyPending, ref storyRevision, state =>
        {
            applied.Add(state.Revision);
            return true;
        });
        storyPending[1] = new ManagerEvent(1, 0, 0, (byte)ManagerDomain.Story, 0, 0, 0);
        ApplyPendingLane(storyPending, ref storyRevision, state =>
        {
            applied.Add(state.Revision);
            return true;
        });
        var wrappedPending = new SortedDictionary<uint, ManagerEvent>
        {
            [1] = new ManagerEvent(1, 0, 0, (byte)ManagerDomain.Day, 0, 0, 0)
        };
        var wrappedRevision = uint.MaxValue;
        ApplyPendingLane(wrappedPending, ref wrappedRevision, _ => true);
        var pendingLanes = new Dictionary<ManagerDomain, SortedDictionary<uint, ManagerEvent>>
        {
            [ManagerDomain.Story] = storyPending
        };
        var hostRevisions = new Dictionary<ManagerDomain, uint> { [ManagerDomain.Story] = 4 };
        var clientRevisions = new Dictionary<ManagerDomain, uint> { [ManagerDomain.Day] = 3 };
        ResetLaneState(pendingLanes, hostRevisions, clientRevisions);
        if (scenarioCancels != 1 || dialogueCancels != 1 ||
            timelineStarts != 1 || timelineCancels != 1 ||
            dialogueRevision != 0 || dialoguePending.Count != 1 ||
            storyRevision != 2 || applied.Count != 2 || applied[0] != 1 || applied[1] != 2 ||
            wrappedRevision != 1 || wrappedPending.Count != 0 ||
            pendingLanes.Count != 0 || hostRevisions.Count != 0 || clientRevisions.Count != 0 ||
            LaneOf(ManagerDomain.MainSushi) == LaneOf(ManagerDomain.JungleSushi) ||
            LaneOf(ManagerDomain.MainSushi) == LaneOf(ManagerDomain.SushiMenu) ||
            LaneOf(ManagerDomain.SushiMenu) == LaneOf(ManagerDomain.SushiWasabi) ||
            LaneOf(ManagerDomain.SushiWasabi) == LaneOf(ManagerDomain.SushiTable) ||
            LaneOf(ManagerDomain.Story) == LaneOf(ManagerDomain.Day) ||
            DecideRestore(false, SessionRole.Offline, false, 0) != RestoreDecision.None ||
            DecideRestore(true, SessionRole.Offline, false, 1) != RestoreDecision.Retry ||
            DecideRestore(true, SessionRole.Offline, false, 3) != RestoreDecision.Retry ||
            DecideRestore(true, SessionRole.Host, false, 1) != RestoreDecision.Retry ||
            DecideRestore(true, SessionRole.Client, true, 1) != RestoreDecision.None ||
            !IsDialogueIntentValid(ManagerAction.Continue, 11, 2, true, true, 11, 2, false, false, false) ||
            IsDialogueIntentValid(ManagerAction.Continue, 11, 1, true, true, 11, 2, false, false, false) ||
            IsDialogueIntentValid(ManagerAction.FirstChoice, 11, 2, true, true, 11, 2, false, false, false) ||
            IsDialogueIntentValid(ManagerAction.SecondChoice, 11, 2, true, true, 11, 2, true, false, false) ||
            IsDialogueIntentValid(ManagerAction.Skip, 11, 2, true, true, 11, 2, false, false, false) ||
            !IsDialogueIntentValid(ManagerAction.Skip, 11, 2, true, true, 11, 2, false, true, false) ||
             IsDialogueIntentValid(ManagerAction.Skip, 11, 2, true, true, 11, 2, false, true, true) ||
             !IsPhoneIntentValid(ManagerAction.PhoneAnswered, 71, 71, true) ||
             IsPhoneIntentValid(ManagerAction.PhoneAnswered, 70, 71, true) ||
             IsPhoneIntentValid(ManagerAction.PhoneCall, 71, 71, true) ||
             !PhoneTargetsMatch(GetPhoneTargets()) ||
             !AllowsLeasedDialogueControl(
                 SessionRole.Client, true, true, ManagerAction.Continue) ||
             AllowsLeasedDialogueControl(
                 SessionRole.Client, true, false, ManagerAction.Continue) ||
              AllowsLeasedDialogueControl(
                  SessionRole.Client, true, true, ManagerAction.PhoneAnswered) ||
              AllowsLeasedDialogueControl(
                  SessionRole.Client, true, true, ManagerAction.FirstChoice) ||
              AllowsLeasedDialogueControl(
                  SessionRole.Client, true, true, ManagerAction.SecondChoice) ||
             HostPhoneAfterClear(true, 71) != 71 || HostPhoneAfterClear(false, 71) != 0 ||
            ScenarioNodeKey("scenario-a", "node", 7) ==
                ScenarioNodeKey("scenario-b", "node", 7) ||
            GetScenarioStartTargets().Count != 2 ||
            GetDialogueStartTargets().Count != 4 ||
            GetScenarioFinishTargets().Count != 1 ||
            GetDialogueNodeTargets().Count != 2 ||
            !TimelineTargetsMatch(GetTimelineTargets()) ||
            GetDialogueTerminalTargets().Count != 6 ||
            !IsTerminalAllowed(SessionRole.Client, true, true) ||
            IsTerminalAllowed(SessionRole.Client, true, false) ||
            !IsTerminalAllowed(SessionRole.Host, true, false) ||
            CallbackIdentity(11, 11) != CallbackIdentityDecision.Invoke ||
            CallbackIdentity(11, 12) != CallbackIdentityDecision.Clear ||
            CallbackIdentity(0, 12) != CallbackIdentityDecision.None ||
            DecideTimelineStart(false, TimelineStartRoute.ByTid, false) !=
                TimelineStartDecision.ReplayByTid ||
            DecideTimelineStart(true, TimelineStartRoute.ByTid, false) !=
                TimelineStartDecision.ReplayByTid ||
            DecideTimelineStart(true, TimelineStartRoute.ByController, false) !=
                TimelineStartDecision.ConsumeSpectator ||
            DecideTimelineStart(true, TimelineStartRoute.ByController, true) !=
                TimelineStartDecision.ReplayByController ||
            DecideTimelineStart(false, TimelineStartRoute.ByController, false) !=
                TimelineStartDecision.ConsumeSpectator ||
            TimelineCallbackIdentity(41, 41) != CallbackIdentityDecision.Invoke ||
            TimelineCallbackIdentity(41, 42) != CallbackIdentityDecision.Clear ||
            TimelineCallbackIdentity(0, 42) != CallbackIdentityDecision.None ||
            TimelineGenerationIdentity(41, 7, 41, 7) != CallbackIdentityDecision.Invoke ||
            TimelineGenerationIdentity(41, 7, 41, 8) != CallbackIdentityDecision.Clear ||
            NextTimelineGeneration(7) != 8 ||
            NextTimelineGeneration(MaxTimelineGeneration) != 1 ||
            UnpackTimelineGeneration(PackTimelineContext(123, true)) != 123 ||
            !UnpackTimelineFlag(PackTimelineContext(123, true)) ||
            DecideTimelineCallbackDelivery(true, false, true) !=
                TimelineCallbackDecision.Invoke ||
            DecideTimelineCallbackDelivery(true, true, true) !=
                TimelineCallbackDecision.None ||
            DecideTimelineCallbackDelivery(true, false, false) !=
                TimelineCallbackDecision.Wait ||
            DecideTimelineFinishDelivery(false, true, true, true) !=
                TimelineFinishDecision.WaitForStart ||
            DecideTimelineFinishDelivery(true, true, true, true) !=
                TimelineFinishDecision.Invoke ||
            !ShouldDropTimelineLease(true, false) ||
            ShouldDropTimelineLease(true, true) ||
            ShouldDropTimelineLease(false, false) ||
            GetTimelineTargets()[0] == null || GetTimelineTargets()[1] == null ||
            GetTimelineTargets()[2] == null || GetTimelineTargets()[3] == null)
            throw new InvalidOperationException("Manager event self-test failed");
    }

    internal void Update(SessionRole role, UdpSession session, uint sceneId, float now)
    {
        _sceneId = sceneId;
        var connected = session != null && session.Connected;
        var disconnecting = _wasConnected && !connected;
        if (!disconnecting)
            ResolveClientRestore(DecideRestore(
                _restorePending || (role != SessionRole.Client && HasClientOriginals()),
                role, connected, 0));
        if (!connected)
        {
            if (_wasConnected)
                Clear(role == SessionRole.Host);
            _wasConnected = false;
            return;
        }
        if (!_wasConnected)
        {
            _hostMenuSlots.Clear();
            Array.Fill(_hostWasabi, -1);
            _nextSushiScan = 0f;
            _nextDayScan = 0f;
            _nextStoryScan = 0f;
            _activeSessionSnapshotPublished = false;
        }
        _wasConnected = true;
        FlushOutbound(session);

        if (role == SessionRole.Host)
            TryAcceptScenarioStart(ScenarioManager.Instance, session);

        if (role == SessionRole.Host && !_activeSessionSnapshotPublished)
        {
            PublishActiveSessionSnapshot(session);
            _activeSessionSnapshotPublished = true;
        }

        if (role == SessionRole.Host && !_storySnapshotPublished)
        {
            _storySnapshotPublished = PublishStorySnapshot(session);
            if (_storySnapshotPublished)
                _nextStoryScan = now + 1f;
        }
        if (role == SessionRole.Host && _storySnapshotPublished && now >= _nextStoryScan)
        {
            _nextStoryScan = now + 1f;
            PublishStoryChanges(session);
        }
        if (role == SessionRole.Host && now >= _nextDayScan)
        {
            _nextDayScan = now + 1f;
            PublishDayChanges(session);
        }
        if (role == SessionRole.Host && _hostTimelineTid != 0 && now >= _nextTimelineSync)
        {
            _nextTimelineSync = now + 1f;
            PublishTimelineProgress(session);
        }
        if (role == SessionRole.Host && now >= _nextSushiScan)
        {
            _nextSushiScan = now + 0.5f;
            PublishSushiRuntimeChanges(session);
        }
        while (session.TryTakeManagerEvent(out var state))
        {
            if (role == SessionRole.Host)
                ApplyClientRequest(session, state);
            else if (role == SessionRole.Client)
            {
                var lane = LaneOf((ManagerDomain)state.Domain);
                var revision = GetRevision(_clientManagerRevisions, lane);
                if (!IsNewer(state.Revision, revision))
                    continue;
                if (!_hasHostClockOffset)
                {
                    _hostClockOffset = unchecked(CurrentTick() - state.HostTick);
                    _hasHostClockOffset = true;
                }
                if (!_pendingHostEvents.TryGetValue(lane, out var pending))
                    _pendingHostEvents[lane] = pending = new SortedDictionary<uint, ManagerEvent>();
                pending[state.Revision] = state;
            }
        }
        if (role == SessionRole.Client)
        {
            ApplyPendingHostEvents(session, sceneId);
            AlignTimeline();
            while (session.TryTakeSushiResultState(out var state))
                if (IsNewer(state.Revision, _clientSushiRevision))
                    _pendingSushiResult = state;
            if (_pendingSushiResult.HasValue && ApplySushiResult(_pendingSushiResult.Value))
            {
                _clientSushiRevision = _pendingSushiResult.Value.Revision;
                _pendingSushiResult = null;
            }
        }
    }

    internal bool Intercept(
        SessionRole role,
        UdpSession session,
        ManagerDomain domain,
        ManagerAction action,
        int value = 0,
        int context = 0)
    {
        if (_applying || _suppressPublish > 0 || session == null || !session.Connected)
            return true;
        if (AllowsLeasedDialogueControl(
                role, session.Connected,
                ProbeBehaviour.Instance?.HasActiveNpcLease == true, action))
            return true;
        if (role == SessionRole.Host)
        {
            Publish(session, domain, action, value, context);
            return true;
        }
        if (role == SessionRole.Client && domain == ManagerDomain.Dialogue)
        {
            _outboundEvents.Enqueue(new ManagerEvent(
                0, 0, CurrentTick(), (byte)domain, (byte)action, value, context));
            FlushOutbound(session);
        }
        return role != SessionRole.Client;
    }

    internal bool BeginIntercept(
        SessionRole role,
        UdpSession session,
        ManagerDomain domain,
        ManagerAction action,
        int value,
        int context,
        out bool suppressNested)
    {
        suppressNested = !_applying && _suppressPublish == 0 &&
            role == SessionRole.Host && session != null && session.Connected;
        var allowed = Intercept(role, session, domain, action, value, context);
        if (allowed && suppressNested)
            _suppressPublish++;
        else
            suppressNested = false;
        return allowed;
    }

    internal void EndIntercept(bool suppressNested)
    {
        if (suppressNested && _suppressPublish > 0)
            _suppressPublish--;
    }

    internal bool InterceptTimelinePlay(
        SessionRole role,
        UdpSession session,
        int tid,
        Il2CppSystem.Action onStart,
        Il2CppSystem.Action<bool> onFinished,
        bool applyOffset,
        Il2CppSystem.Nullable<Vector3> customPos)
    {
        if (_applying)
            return true;
        if (role == SessionRole.Host)
        {
            RecordHostTimelineRoute(tid, TimelineStartRoute.ByTid);
            return true;
        }
        if (role != SessionRole.Client || session?.Connected != true)
            return true;
        var captured = new TimelineLease
        {
            Tid = tid == int.MinValue ? 0 : Math.Abs(tid),
            Route = TimelineStartRoute.ByTid,
            State = ClientTimelineState.AwaitingHostStart,
            HasIntent = true,
            OnStart = onStart,
            OnFinished = onFinished,
            ApplyOffset = applyOffset,
            CustomPos = customPos
        };
        if (captured.Tid != 0)
            CaptureTimelineIntent(captured);
        else
            CancelTimelineLease(captured);
        return false;
    }

    internal bool InterceptTimelineController(
        SessionRole role,
        UdpSession session,
        TimelineController controller,
        Il2CppSystem.Action onStart,
        Il2CppSystem.Action<bool> onFinished,
        int id)
    {
        if (_applying)
            return true;
        var tid = id != 0 ? id : controller?.TimelineID ?? 0;
        if (role == SessionRole.Host)
        {
            RecordHostTimelineRoute(tid, TimelineStartRoute.ByController);
            return true;
        }
        if (role != SessionRole.Client || session?.Connected != true)
            return true;
        if (controller != null && tid != 0 && tid != int.MinValue &&
            TimelineCallbackIdentity(_clientTimelineLease?.Tid ?? 0, Math.Abs(tid)) ==
                CallbackIdentityDecision.Invoke &&
            _clientTimelineLease.Route == TimelineStartRoute.ByTid &&
            _clientTimelineLease.State == ClientTimelineState.Starting)
            return true;
        var captured = new TimelineLease
        {
            Tid = tid == int.MinValue ? 0 : Math.Abs(tid),
            Route = TimelineStartRoute.ByController,
            State = ClientTimelineState.AwaitingHostStart,
            HasIntent = true,
            Controller = controller,
            ControllerId = id,
            OnStart = onStart,
            OnFinished = onFinished
        };
        if (controller != null && captured.Tid != 0)
            CaptureTimelineIntent(captured);
        else
            CancelTimelineLease(captured);
        return false;
    }

    internal void ObserveTimeline(
        SessionRole role,
        UdpSession session,
        TimelineManager.TPlayState state,
        int tid,
        bool success)
    {
        if (!success)
        {
            if (role == SessionRole.Client && tid != 0 && tid != int.MinValue &&
                _clientTimelineLease?.Tid == Math.Abs(tid))
            {
                _clientTimelineLease.State = ClientTimelineState.Spectating;
                _clientTimelineLease.StartObserved = true;
                _clientTimelineLease.HostFinishObserved = true;
                _clientTimelineLease.RelayFinishObserved = true;
                _clientTimelineLease.Skipped = true;
                DeliverTimelineStart(_clientTimelineLease);
                DeliverTimelineFinish(_clientTimelineLease);
            }
            if (role == SessionRole.Host && tid != int.MinValue &&
                _hostTimelineRouteTid == Math.Abs(tid))
                _hostTimelineRouteTid = 0;
            return;
        }
        if (role == SessionRole.Client)
        {
            _clientTimelineTid = state == TimelineManager.TPlayState.Start ? tid : 0;
            return;
        }
        if (_applying || role != SessionRole.Host || tid == 0 || tid == int.MinValue)
            return;
        tid = Math.Abs(tid);
        var start = state == TimelineManager.TPlayState.Start;
        var manager = TimelineManager.Instance;
        var current = manager?.currentTimeline;
        if (start)
        {
            _hostTimelineGeneration = NextTimelineGeneration(_hostTimelineGeneration);
            _hostTimelineActiveRoute = _hostTimelineRouteTid == tid
                ? _hostTimelineRoute
                : TimelineStartRoute.ByTid;
        }
        var flag = start
            ? _hostTimelineActiveRoute == TimelineStartRoute.ByController
            : current != null
                ? current.IsSkipped
                : manager?.IsPrevTimelineSkipped == true;
        var context = PackTimelineContext(_hostTimelineGeneration, flag);
        _hostTimelineTid = start ? tid : 0;
        _hostTimelineRouteTid = 0;
        _nextTimelineSync = 0f;
        if (session?.Connected == true)
            Publish(session, ManagerDomain.Timeline,
                start ? ManagerAction.TimelineStart : ManagerAction.TimelineFinish,
                tid, context);
    }

    internal bool AllowTimelineTerminalControl(SessionRole role, UdpSession session) =>
        _applying || role != SessionRole.Client || session?.Connected != true;

    internal bool AllowScenarioControl(SessionRole role, UdpSession session) =>
        _applying || ProbeBehaviour.Instance?.HasActiveNpcLease == true ||
        role != SessionRole.Client || session == null || !session.Connected;

    internal bool AllowDialogueAdvance(SessionRole role, UdpSession session) =>
        _applying || ProbeBehaviour.Instance?.HasActiveNpcLease == true ||
        role != SessionRole.Client || session == null || !session.Connected;

    internal bool AllowDialogueChoice(SessionRole role, UdpSession session) =>
        _applying || role != SessionRole.Client || session == null || !session.Connected;

    internal bool InterceptPhoneCall(
        SessionRole role, UdpSession session, int tid, Il2CppSystem.Action<bool> callback)
    {
        if (_applying || session?.Connected != true)
            return true;
        if (tid <= 0)
            return false;
        if (role == SessionRole.Host)
        {
            _hostPhoneTid = tid;
            Publish(session, ManagerDomain.Dialogue, ManagerAction.PhoneCall, tid, 0);
            return true;
        }
        if (role == SessionRole.Client)
        {
            CompletePendingPhoneCallback(false);
            _pendingClientPhoneTid = tid;
            _pendingClientPhoneCallback = callback;
            return false;
        }
        return true;
    }

    internal bool InterceptPhoneAnswer(SessionRole role, UdpSession session)
    {
        if (_applying || session?.Connected != true)
            return true;
        var tid = role == SessionRole.Host ? _hostPhoneTid : _clientPhoneTid;
        if (tid <= 0)
            return false;
        if (role == SessionRole.Host)
        {
            Publish(session, ManagerDomain.Dialogue, ManagerAction.PhoneAnswered, tid, 0);
            _hostPhoneTid = 0;
            return true;
        }
        if (role == SessionRole.Client)
        {
            _outboundEvents.Enqueue(new ManagerEvent(
                0, 0, CurrentTick(), (byte)ManagerDomain.Dialogue,
                (byte)ManagerAction.PhoneAnswered, tid, 0));
            FlushOutbound(session);
            return false;
        }
        return true;
    }

    internal bool AllowScenarioTerminal(SessionRole role, UdpSession session) =>
        ProbeBehaviour.Instance?.HasActiveNpcLease == true ||
        IsTerminalAllowed(role, session?.Connected == true, _applyingScenarioAuthority);

    internal bool AllowDialogueTerminal(SessionRole role, UdpSession session) =>
        ProbeBehaviour.Instance?.HasActiveNpcLease == true ||
        IsTerminalAllowed(role, session?.Connected == true, _applyingDialogueAuthority);

    private static bool IsTerminalAllowed(SessionRole role, bool connected, bool applying) =>
        role != SessionRole.Client || !connected || applying;

    internal bool InterceptScenarioStart(
        SessionRole role,
        UdpSession session,
        string bundleId,
        bool isBranch,
        ScenarioBranchData branchData,
        Il2CppSystem.Collections.Generic.List<string> arguments,
        Il2CppSystem.Action<bool> callback,
        bool useButton,
        bool showCurtain,
        bool ignorePlaying)
    {
        if (_applying || ProbeBehaviour.Instance?.IsApplyingNpcGrant == true ||
            role != SessionRole.Client || session?.Connected != true)
            return true;
        var key = ContentKey(bundleId);
        if (key == 0)
        {
            InvokeClientPresentationCallback(() => callback?.Invoke(false), "scenario");
            return false;
        }
        CancelPendingScenarioStart();
        _pendingClientScenarioStart = new PendingScenarioStart
        {
            Kind = isBranch ? ScenarioStartKind.Branch : ScenarioStartKind.Normal,
            BundleId = bundleId,
            BundleKey = key,
            BranchData = branchData,
            Arguments = arguments,
            Callback = callback,
            UseButton = useButton,
            ShowCurtain = showCurtain,
            IgnorePlaying = ignorePlaying
        };
        return false;
    }

    internal bool InterceptDialogueStart(
        SessionRole role,
        UdpSession session,
        string bundleId,
        DialogueStartKind kind,
        Il2CppSystem.Collections.Generic.List<string> arguments,
        Il2CppSystem.Collections.Generic.List<DialogueEntry> entries,
        ScenarioButtonInfo buttonInfo,
        Il2CppSystem.Action<bool> callback,
        Il2CppSystem.Action<int> choiceCallback,
        bool useButton,
        bool showCurtain)
    {
        if (_applying || ProbeBehaviour.Instance?.IsApplyingNpcGrant == true ||
            role != SessionRole.Client || session?.Connected != true)
            return true;
        var key = ContentKey(bundleId);
        if (key == 0)
        {
            InvokeClientPresentationCallback(() => callback?.Invoke(false), "dialogue");
            InvokeClientPresentationCallback(() => choiceCallback?.Invoke(-1), "dialogue choice");
            return false;
        }
        CancelPendingDialogueStart();
        _pendingClientDialogueStart = new PendingDialogueStart
        {
            Kind = kind,
            BundleId = bundleId,
            BundleKey = key,
            Arguments = arguments,
            Entries = entries,
            ButtonInfo = buttonInfo,
            Callback = callback,
            ChoiceCallback = choiceCallback,
            UseButton = useButton,
            ShowCurtain = showCurtain
        };
        return false;
    }

    internal bool InterceptScenarioNode(
        SessionRole role, UdpSession session, ScenarioManager manager, DRSequence sequence)
    {
        if (role == SessionRole.Client && session?.Connected == true &&
            ProbeBehaviour.Instance?.HasActiveNpcLease == true)
            return true;
        if (_applying || role != SessionRole.Client || session?.Connected != true)
        {
            ObserveScenarioNode(role, session, manager, sequence);
            return true;
        }
        _blockedClientScenarioNode = sequence;
        _blockedClientScenarioNodeKey = ScenarioNodeKey(manager, sequence);
        return false;
    }

    internal void ObserveScenarioStarted(
        SessionRole role, UdpSession session, ScenarioManager manager, string bundleId)
    {
        var key = ContentKey(bundleId);
        if (_applying || role != SessionRole.Host || key == 0 ||
            _hostScenarioBundleKey == key)
            return;
        _pendingHostScenarioStartBundleId = bundleId;
        TryAcceptScenarioStart(manager, session);
    }

    private void TryAcceptScenarioStart(ScenarioManager manager, UdpSession session)
    {
        if (manager?.IsPlaying != true || string.IsNullOrEmpty(_pendingHostScenarioStartBundleId))
            return;
        var key = ContentKey(_pendingHostScenarioStartBundleId);
        var currentKey = ContentKey(manager.m_CurrentConversation?.GetSequenceID());
        if (key == 0 || currentKey != key)
            return;
        _hostScenarioBundleId = _pendingHostScenarioStartBundleId;
        _pendingHostScenarioStartBundleId = string.Empty;
        _hostScenarioBundleKey = key;
        _hostScenarioNodeId = -1;
        if (session?.Connected == true)
            Publish(session, ManagerDomain.Scenario, ManagerAction.ScenarioStarted,
                _hostScenarioBundleKey, 0);
    }

    internal void ObserveScenarioNode(
        SessionRole role, UdpSession session, ScenarioManager manager, DRSequence sequence)
    {
        if (role == SessionRole.Host && _hostScenarioBundleKey == 0)
            TryAcceptScenarioStart(manager, session);
        var nodeKey = ScenarioNodeKey(manager, sequence);
        if (_applying || role != SessionRole.Host || _hostScenarioBundleKey == 0 || nodeKey == 0)
            return;
        _hostScenarioNodeId = nodeKey;
        if (session?.Connected == true)
            Publish(session, ManagerDomain.Scenario, ManagerAction.ScenarioNode,
                _hostScenarioBundleKey, nodeKey);
    }

    internal Il2CppSystem.Action<bool> WrapScenarioFinish(
        SessionRole role,
        UdpSession session,
        string bundleId,
        Il2CppSystem.Action<bool> callback)
    {
        if (role != SessionRole.Host)
            return callback;
        var bundleKey = ContentKey(bundleId);
        var invoked = false;
        return (Il2CppSystem.Action<bool>)(Action<bool>)(result =>
        {
            if (invoked)
                return;
            invoked = true;
            ObserveScenarioFinished(role, session, bundleKey, result);
            callback?.Invoke(result);
        });
    }

    internal Il2CppSystem.Action<bool> WrapDialogueFinish(
        SessionRole role,
        UdpSession session,
        string bundleId,
        Il2CppSystem.Action<bool> callback)
    {
        if (role != SessionRole.Host)
            return callback;
        var bundleKey = ContentKey(bundleId);
        var invoked = false;
        return (Il2CppSystem.Action<bool>)(Action<bool>)(result =>
        {
            if (invoked)
                return;
            invoked = true;
            ObserveDialogueFinished(role, session, bundleKey, result);
            callback?.Invoke(result);
        });
    }

    internal void ObserveScenarioFinished(
        SessionRole role, UdpSession session, int bundleKey, bool result)
    {
        if (_applying || role != SessionRole.Host || bundleKey == 0 ||
            bundleKey != _hostScenarioBundleKey)
            return;
        if (session?.Connected == true)
            Publish(session, ManagerDomain.Scenario, ManagerAction.ScenarioFinished,
                bundleKey, result ? 1 : 0);
        _hostScenarioBundleId = string.Empty;
        _hostScenarioBundleKey = 0;
        _hostScenarioNodeId = -1;
    }

    internal void ObserveScenarioFinished(SessionRole role, UdpSession session) =>
        ObserveScenarioFinished(role, session, _hostScenarioBundleKey, true);

    internal void ObserveScenarioFinished(
        SessionRole role, UdpSession session, bool result) =>
        ObserveScenarioFinished(role, session, _hostScenarioBundleKey, result);

    internal void ObserveDialogueStarted(
        SessionRole role, UdpSession session, string bundleId)
    {
        if (_applying || role != SessionRole.Host || string.IsNullOrEmpty(bundleId))
            return;
        var key = ContentKey(bundleId);
        if (_hostDialogueBundleKey == key)
            return;
        _hostDialogueBundleKey = key;
        _hostDialogueIndex = -1;
        if (session?.Connected == true)
            Publish(session, ManagerDomain.Dialogue, ManagerAction.DialogueStarted, key, 0);
    }

    internal void ObserveDialogueNode(
        SessionRole role, UdpSession session, DialogueManager manager, DialogueInfo info)
    {
        if (_applying || role != SessionRole.Host || manager == null || info == null)
            return;
        var key = ContentKey(manager.CurrentBundleID);
        var index = manager.m_CurrentDialogueIndex;
        if (key == 0 || index < 0)
            return;
        _hostDialogueBundleKey = key;
        _hostDialogueIndex = index;
        if (session?.Connected == true)
            Publish(session, ManagerDomain.Dialogue, ManagerAction.DialogueNode, key, index);
    }

    internal void ObserveDialogueFinished(
        SessionRole role, UdpSession session, int bundleKey, bool result)
    {
        if (_applying || role != SessionRole.Host || bundleKey == 0 ||
            bundleKey != _hostDialogueBundleKey)
            return;
        if (session?.Connected == true)
            Publish(session, ManagerDomain.Dialogue, ManagerAction.DialogueFinished,
                bundleKey, result ? 1 : 0);
        _hostDialogueBundleKey = 0;
        _hostDialogueIndex = -1;
    }

    internal void ObserveDialogueFinished(SessionRole role, UdpSession session) =>
        ObserveDialogueFinished(role, session, _hostDialogueBundleKey, true);

    private void RecordHostTimelineRoute(int tid, TimelineStartRoute route)
    {
        if (tid == 0 || tid == int.MinValue || _hostTimelineRouteTid == Math.Abs(tid))
            return;
        _hostTimelineRouteTid = Math.Abs(tid);
        _hostTimelineRoute = route;
    }

    private void CaptureTimelineIntent(TimelineLease captured)
    {
        var lease = _clientTimelineLease;
        if (lease == null || lease.State == ClientTimelineState.Finished ||
            TimelineCallbackIdentity(lease.Tid, captured.Tid) != CallbackIdentityDecision.Invoke)
        {
            CancelTimelineLease(lease);
            _clientTimelineLease = captured;
            lease = captured;
        }
        else if (lease.Generation == 0)
        {
            CancelTimelineLease(lease);
            _clientTimelineLease = captured;
            lease = captured;
        }
        else
        {
            if (lease.Route != captured.Route)
            {
                CancelTimelineLease(captured);
                return;
            }
            lease.HasIntent = true;
            lease.Controller = captured.Controller;
            lease.ControllerId = captured.ControllerId;
            lease.OnStart = captured.OnStart;
            lease.OnFinished = captured.OnFinished;
            lease.ApplyOffset = captured.ApplyOffset;
            lease.CustomPos = captured.CustomPos;
            if (lease.State == ClientTimelineState.Spectating)
                lease.StartObserved = true;
        }
        DeliverTimelineStart(lease);
        DeliverTimelineFinish(lease);
    }

    private Il2CppSystem.Action CreateTimelineStartRelay(int tid, int generation) =>
        (Il2CppSystem.Action)(Action)(() =>
        {
            var lease = _clientTimelineLease;
            if (TimelineGenerationIdentity(
                    lease?.Tid ?? 0, lease?.Generation ?? 0, tid, generation) !=
                CallbackIdentityDecision.Invoke)
                return;
            lease.StartObserved = true;
            lease.State = ClientTimelineState.Playing;
            DeliverTimelineStart(lease);
            DeliverTimelineFinish(lease);
        });

    private Il2CppSystem.Action<bool> CreateTimelineFinishRelay(int tid, int generation) =>
        (Il2CppSystem.Action<bool>)(Action<bool>)(skipped =>
        {
            var lease = _clientTimelineLease;
            if (TimelineGenerationIdentity(
                    lease?.Tid ?? 0, lease?.Generation ?? 0, tid, generation) !=
                CallbackIdentityDecision.Invoke)
                return;
            lease.RelayFinishObserved = true;
            lease.Skipped |= skipped;
            DeliverTimelineFinish(lease);
        });

    private void DeliverTimelineStart(TimelineLease lease)
    {
        if (lease == null || DecideTimelineCallbackDelivery(
                lease.StartObserved, lease.StartDelivered, lease.OnStart != null) !=
            TimelineCallbackDecision.Invoke)
            return;
        lease.StartDelivered = true;
        var callback = lease.OnStart;
        lease.OnStart = null;
        InvokeClientPresentationCallback(() => callback.Invoke(), "timeline start");
    }

    private void DeliverTimelineFinish(TimelineLease lease)
    {
        if (lease == null)
            return;
        DeliverTimelineStart(lease);
        var startReady = lease.StartObserved &&
            (lease.OnStart == null || lease.StartDelivered);
        var decision = DecideTimelineFinishDelivery(
            startReady, lease.HostFinishObserved, lease.RelayFinishObserved,
            lease.OnFinished != null && !lease.FinishDelivered);
        if (decision != TimelineFinishDecision.Invoke)
        {
            RetireTimelineLeaseIfComplete(lease);
            return;
        }
        lease.FinishDelivered = true;
        lease.State = ClientTimelineState.Finished;
        var callback = lease.OnFinished;
        lease.OnFinished = null;
        InvokeClientPresentationCallback(
            () => callback.Invoke(lease.Skipped), "timeline finish");
        RetireTimelineLeaseIfComplete(lease);
    }

    private void RetireTimelineLeaseIfComplete(TimelineLease lease)
    {
        if (lease.StartObserved && lease.HostFinishObserved && lease.RelayFinishObserved &&
            (lease.OnStart == null || lease.StartDelivered) &&
            (lease.OnFinished == null || lease.FinishDelivered))
        {
            lease.State = ClientTimelineState.Finished;
            if (ReferenceEquals(_clientTimelineLease, lease))
                _clientTimelineLease = null;
        }
    }

    internal void PublishSushiResult(SessionRole role, UdpSession session)
    {
        if (_applying || role != SessionRole.Host || session == null || !session.Connected)
            return;
        try
        {
            var today = SushiBarAnalyticsManager.Instance?.TodayData;
            if (today == null)
                return;
            _sushiRevision = NextRevision(_sushiRevision);
            session.SendSushiResultState(new SushiResultState(
                _sushiRevision,
                today.SalesMenu,
                today.SalesEtc,
                today.StaffTips,
                today.TotalVisitCount,
                today.LikeCount,
                today.CalcRating()));
        }
        catch (Exception exception)
        {
            _log.LogWarning($"Sushi result read failed: {exception.Message}");
        }
    }

    internal void Clear(bool preserveHostSessions = false)
    {
        if (HasClientOriginals())
        {
            _restorePending = true;
            ResolveClientRestore(DecideRestore(
                true, SessionRole.Offline, false, 0));
        }
        _outboundEvents.Clear();
        ResetLaneState(_pendingHostEvents, _hostRevisions, _clientManagerRevisions);
        _hostMenuSlots.Clear();
        Array.Fill(_hostWasabi, -1);
        _sushiRevision = 0;
        _clientSushiRevision = 0;
        _pendingSushiResult = null;
        _applying = false;
        _applyingScenarioAuthority = false;
        _applyingDialogueAuthority = false;
        _suppressPublish = 0;
        _nextSushiScan = 0f;
        _nextDayScan = 0f;
        _nextStoryScan = 0f;
        _hostCurrentChapter = int.MinValue;
        _hostReservedChapter = int.MinValue;
        _hostEvents.Clear();
        _hostCutscenes.Clear();
        _hostIntermissions.Clear();
        _hostDayTicks = long.MinValue;
        _hostDayTime = int.MinValue;
        _hostWeather = int.MinValue;
        _sceneId = 0;
        ResetClientTimelineState();
        _nextTimelineSync = 0f;
        if (!preserveHostSessions)
        {
            _hostTimelineTid = 0;
            _hostTimelineRouteTid = 0;
            _hostTimelineRoute = TimelineStartRoute.ByTid;
            _hostTimelineGeneration = 0;
            _hostTimelineActiveRoute = TimelineStartRoute.ByTid;
        }
        _hasHostClockOffset = false;
        _hostClockOffset = 0;
        _wasConnected = false;
        _storySnapshotPublished = false;
        _activeSessionSnapshotPublished = false;
        if (!preserveHostSessions)
        {
            _pendingHostScenarioStartBundleId = string.Empty;
            _hostScenarioBundleId = string.Empty;
            _hostScenarioBundleKey = 0;
            _hostScenarioNodeId = -1;
            _hostDialogueBundleKey = 0;
            _hostDialogueIndex = -1;
        }
        _clientScenarioBundleId = string.Empty;
        _clientScenarioBundleKey = 0;
        _clientScenarioNodeId = -1;
        _clientDialogueBundleKey = 0;
        _clientDialogueIndex = -1;
        _blockedClientScenarioNodeKey = 0;
        _blockedClientScenarioNode = null;
        CancelPendingScenarioStart();
        CancelPendingDialogueStart();
        _clientScenarioSpectating = false;
        _clientDialogueSpectating = false;
        _hostPhoneTid = HostPhoneAfterClear(preserveHostSessions, _hostPhoneTid);
        _clientPhoneTid = 0;
        CompletePendingPhoneCallback(false);
    }

    internal void ForceHostKeyframe()
    {
        _storySnapshotPublished = false;
        _nextStoryScan = 0f;
        _hostDayTicks = long.MinValue;
        _hostDayTime = int.MinValue;
        _hostWeather = int.MinValue;
        _nextDayScan = 0f;
        _activeSessionSnapshotPublished = false;
    }

    internal void OnSceneChanged()
    {
        CancelPendingScenarioStart();
        CancelPendingDialogueStart();
        CompletePendingPhoneCallback(false);
        _clientPhoneTid = 0;
        var timeline = TimelineManager.Instance?.currentTimeline;
        var active = _clientTimelineLease?.State is ClientTimelineState.Starting or
                ClientTimelineState.Playing ||
            _hostTimelineRouteTid != 0 ||
            timeline?.Director?.state == UnityEngine.Playables.PlayState.Playing &&
            (_clientTimelineTid != 0 || _hostTimelineTid != 0);
        if (!ShouldDropTimelineLease(true, active))
            return;
        ResetClientTimelineState();
        _hostTimelineTid = 0;
        _hostTimelineRouteTid = 0;
    }

    private void ResetClientTimelineState()
    {
        CancelTimelineLease(_clientTimelineLease);
        _clientTimelineTid = 0;
        _timelineSyncTid = 0;
        _clientTimelineHostTick = 0;
        _clientTimelineTime = 0;
        _clientTimelinePlaying = false;
        _clientTimelineLease = null;
    }

    private void Publish(
        UdpSession session,
        ManagerDomain domain,
        ManagerAction action,
        int value,
        int context)
    {
        var lane = LaneOf(domain);
        var revision = NextRevision(GetRevision(_hostRevisions, lane));
        _hostRevisions[lane] = revision;
        _outboundEvents.Enqueue(new ManagerEvent(
            revision,
            IsGlobal(domain) ? 0 : _sceneId,
            CurrentTick(),
            (byte)domain, (byte)action, value, context));
        FlushOutbound(session);
    }

    private void FlushOutbound(UdpSession session)
    {
        while (_outboundEvents.Count > 0 && session.ReliableCapacityRemaining > 0 &&
               session.SendManagerEvent(_outboundEvents.Peek()))
            _outboundEvents.Dequeue();
    }

    private void ApplyClientRequest(UdpSession session, ManagerEvent state)
    {
        if ((ManagerDomain)state.Domain != ManagerDomain.Dialogue || state.Revision != 0 ||
            state.SceneId != 0 || !ValidateDialogueIntent(state))
            return;
        if (Apply(state))
            Publish(session, (ManagerDomain)state.Domain, (ManagerAction)state.Action,
                state.Value, state.Context);
    }

    private void ApplyPendingHostEvents(UdpSession session, uint sceneId)
    {
        foreach (var pair in _pendingHostEvents)
        {
            var revision = GetRevision(_clientManagerRevisions, pair.Key);
            ApplyPendingLane(pair.Value, ref revision, state =>
            {
                if (state.SceneId == 0 || state.SceneId == sceneId)
                    return Apply(state);
                return !session.SceneMatches(state.SceneId);
            });
            _clientManagerRevisions[pair.Key] = revision;
        }
    }

    private static void ApplyPendingLane(
        SortedDictionary<uint, ManagerEvent> pending,
        ref uint revision,
        Func<ManagerEvent, bool> apply)
    {
        while (pending.TryGetValue(NextRevision(revision), out var state) && apply(state))
        {
            pending.Remove(state.Revision);
            revision = state.Revision;
        }
    }

    private bool Apply(ManagerEvent state)
    {
        var previousApplying = _applying;
        _applying = true;
        var domain = (ManagerDomain)state.Domain;
        var previousScenarioAuthority = _applyingScenarioAuthority;
        var previousDialogueAuthority = _applyingDialogueAuthority;
        _applyingScenarioAuthority |= domain == ManagerDomain.Scenario;
        _applyingDialogueAuthority |= domain == ManagerDomain.Dialogue;
        try
        {
            switch (domain)
            {
                case ManagerDomain.MainSushi:
                    return ApplyMainSushi((ManagerAction)state.Action);
                case ManagerDomain.JungleSushi:
                    return ApplyJungleSushi((ManagerAction)state.Action, state.Value != 0);
                case ManagerDomain.Dialogue:
                    return ApplyDialogueState(
                        (ManagerAction)state.Action, state.Value, state.Context);
                case ManagerDomain.Scenario:
                    return ApplyScenarioState(
                        (ManagerAction)state.Action, state.Value, state.Context);
                case ManagerDomain.Betting:
                    var betting = UnityEngine.Object.FindFirstObjectByType<MiniGame.BettingGameUI_ResultPage>();
                    if (betting == null)
                        return false;
                    betting.Show(state.Value != 0);
                    return true;
                case ManagerDomain.VipCooking:
                    var vip = UnityEngine.Object.FindFirstObjectByType<MiniGame.BattleVIP.VIPCookingManager>();
                    if (vip == null || VipJudgeWinner == null)
                        return false;
                    VipJudgeWinner.Invoke(vip, new object[] { state.Value != 0 });
                    return true;
                case ManagerDomain.JungleMiniGame:
                    var jungle = UnityEngine.Object.FindFirstObjectByType<JDLC.JungleMiniGameSceneManager>();
                    if (jungle == null)
                        return false;
                    jungle.OnMiniGameEnd(new JDLC.JungleMiniGameResultInfo(
                        (JDLC.JungleMiniGameResult)state.Value));
                    return true;
                case ManagerDomain.Karaoke:
                    var result = UnityEngine.Object.FindFirstObjectByType<Karaoke.KaraokeTrackResult>();
                    var track = UnityEngine.Object.FindFirstObjectByType<Karaoke.KaraokeManager>()
                        ?.trackPlayer?.currentTrack;
                    if (result == null || track == null)
                        return false;
                    result.Show(state.Value, track);
                    return true;
                case ManagerDomain.SushiMenu:
                    return ApplyMenuState((ManagerAction)state.Action, state.Value, state.Context);
                case ManagerDomain.SushiWasabi:
                    return ApplyWasabiState(state.Value, state.Context);
                case ManagerDomain.SushiTable:
                    return ApplyTableState(state.Value != 0, state.Context);
                case ManagerDomain.Story:
                    return ApplyStory((ManagerAction)state.Action, state.Value);
                case ManagerDomain.Day:
                    return ApplyDay((ManagerAction)state.Action, state.Value, state.Context);
                case ManagerDomain.Timeline:
                    return ApplyTimeline(
                        (ManagerAction)state.Action, state.Value, state.Context, state.HostTick);
                case ManagerDomain.InsectBattle:
                    var battle = UnityEngine.Object.FindFirstObjectByType<InsectBattle.InsectBattleStateManager>();
                    if (battle == null)
                        return false;
                    battle.ChangeState((InsectBattle.InsectBattleStateManager.InsectBattleState)state.Value);
                    return true;
                case ManagerDomain.SeahorseRace:
                    var race = UnityEngine.Object.FindFirstObjectByType<MiniGame.SeahorseRace.SeahorseRace>();
                    if (race?.session == null)
                        return false;
                    race.session.OnGoal(state.Value, BitConverter.Int32BitsToSingle(state.Context));
                    return true;
                default:
                    return false;
            }
        }
        catch (Exception exception)
        {
            _log.LogWarning($"Manager event apply failed: {exception.Message}");
            return false;
        }
        finally
        {
            _applyingScenarioAuthority = previousScenarioAuthority;
            _applyingDialogueAuthority = previousDialogueAuthority;
            _applying = previousApplying;
        }
    }

    private static bool ApplyMainSushi(ManagerAction action)
    {
        var manager = UnityEngine.Object.FindFirstObjectByType<SushiBarManager>();
        if (manager == null)
            return false;
        switch (action)
        {
            case ManagerAction.Start:
                manager.OnEventSushiBarOpened();
                return true;
            case ManagerAction.LastSpurt:
                manager.OnEnterEveningLastSpurt();
                return true;
            case ManagerAction.Finish when MainSushiFinish != null:
                MainSushiFinish.Invoke(manager, null);
                return true;
            default:
                return false;
        }
    }

    private static bool ApplyJungleSushi(ManagerAction action, bool skipped)
    {
        var manager = UnityEngine.Object.FindFirstObjectByType<JDLC.JungleSushiBarSceneManager>();
        if (manager == null)
            return false;
        switch (action)
        {
            case ManagerAction.Start:
                manager.OnOpenSushiBar();
                return true;
            case ManagerAction.LastSpurt:
                manager.OnEnterEveningLastSpurt();
                return true;
            case ManagerAction.Finish when JungleSushiFinish != null:
                JungleSushiFinish.Invoke(manager, new object[] { skipped });
                return true;
            default:
                return false;
        }
    }

    private bool ApplyDialogueState(ManagerAction action, int bundleKey, int index)
    {
        var manager = DialogueManager.Instance;
        switch (action)
        {
            case ManagerAction.DialogueStarted:
                _clientDialogueBundleKey = bundleKey;
                _clientDialogueIndex = -1;
                if (bundleKey == 0)
                    return false;
                if (CallbackIdentity(_pendingClientDialogueStart?.BundleKey ?? 0, bundleKey) ==
                    CallbackIdentityDecision.Clear)
                    CancelPendingDialogueStart();
                if (_pendingClientDialogueStart?.BundleKey != bundleKey || manager == null)
                {
                    _clientDialogueSpectating = true;
                    _log.LogInfo(
                        $"Dialogue presentation spectating: bundle={bundleKey:X8}; local invocation unavailable");
                    return true;
                }
                _clientDialogueSpectating = false;
                ReplayDialogueStart(manager, _pendingClientDialogueStart);
                return true;
            case ManagerAction.DialogueNode:
                if (bundleKey == 0 || bundleKey != _clientDialogueBundleKey)
                    return false;
                if (_clientDialogueSpectating)
                {
                    _clientDialogueIndex = index;
                    return true;
                }
                if (manager == null || ContentKey(manager.CurrentBundleID) != bundleKey ||
                    manager.m_CurrentDialogues == null || index < 0 ||
                    index >= manager.m_CurrentDialogues.Count)
                    return false;
                _clientDialogueBundleKey = bundleKey;
                _clientDialogueIndex = index;
                if (manager.m_CurrentDialogueIndex != index)
                {
                    manager.m_CurrentDialogueIndex = index;
                    manager.UpdateCurrentDialogue(manager.m_CurrentDialogues[index]);
                }
                return true;
            case ManagerAction.DialogueFinished:
                if (bundleKey == 0)
                    return false;
                if (bundleKey != _clientDialogueBundleKey)
                {
                    if (CallbackIdentity(_pendingClientDialogueStart?.BundleKey ?? 0, bundleKey) ==
                        CallbackIdentityDecision.Clear)
                        CancelPendingDialogueStart();
                    return true;
                }
                if (manager?.IsPlaying == true)
                    manager.ForceFinishDialogue(false);
                CompleteDialogueCallback(bundleKey, index != 0);
                _clientDialogueBundleKey = 0;
                _clientDialogueIndex = -1;
                _clientDialogueSpectating = false;
                _pendingClientDialogueStart = null;
                return true;
            case ManagerAction.Continue:
                if (_clientDialogueSpectating)
                    return true;
                if (manager == null)
                    return false;
                manager.ContinueDialogueManual();
                return true;
            case ManagerAction.Skip:
                if (_clientDialogueSpectating)
                    return true;
                if (manager == null)
                    return false;
                manager.OnSkip();
                return true;
            case ManagerAction.FirstChoice:
                if (_clientDialogueSpectating)
                    return true;
                if (manager == null)
                    return false;
                manager.ExcuteFirstDialogue();
                CompleteDialogueChoiceCallback(bundleKey, 0);
                return true;
            case ManagerAction.SecondChoice:
                if (_clientDialogueSpectating)
                    return true;
                if (manager == null)
                    return false;
                manager.ExcuteSecondDialogue();
                CompleteDialogueChoiceCallback(bundleKey, 1);
                return true;
            case ManagerAction.PhoneCall:
                if (manager == null || bundleKey <= 0)
                    return false;
                _clientPhoneTid = bundleKey;
                manager.ShowPhoneCall(bundleKey, CreatePhoneCallback(bundleKey));
                return true;
            case ManagerAction.PhoneAnswered:
                if (bundleKey <= 0 || bundleKey != (_hostPhoneTid != 0
                        ? _hostPhoneTid
                        : _clientPhoneTid))
                    return false;
                var panel = UnityEngine.Object.FindFirstObjectByType<SideMissionPhonePanel>();
                if (panel == null)
                    return false;
                panel.OnAnswerPhone();
                _hostPhoneTid = 0;
                _clientPhoneTid = 0;
                return true;
            default:
                return false;
        }
    }

    private Il2CppSystem.Action<bool> CreatePhoneCallback(int tid) =>
        (Il2CppSystem.Action<bool>)(Action<bool>)(result =>
        {
            if (_pendingClientPhoneTid == tid)
                CompletePendingPhoneCallback(result);
        });

    private void CompletePendingPhoneCallback(bool result)
    {
        var callback = _pendingClientPhoneCallback;
        _pendingClientPhoneTid = 0;
        _pendingClientPhoneCallback = null;
        if (callback != null)
            InvokeClientPresentationCallback(() => callback.Invoke(result), "phone call");
    }

    private bool ApplyScenarioState(ManagerAction action, int bundleKey, int nodeId)
    {
        var manager = ScenarioManager.Instance;
        switch (action)
        {
            case ManagerAction.ScenarioStarted:
                if (bundleKey == 0)
                    return false;
                _clientScenarioBundleKey = bundleKey;
                _clientScenarioNodeId = -1;
                if (CallbackIdentity(_pendingClientScenarioStart?.BundleKey ?? 0, bundleKey) ==
                    CallbackIdentityDecision.Clear)
                {
                    CancelPendingScenarioStart();
                    _blockedClientScenarioNode = null;
                    _blockedClientScenarioNodeKey = 0;
                }
                if (_pendingClientScenarioStart?.BundleKey != bundleKey || manager == null)
                {
                    _clientScenarioSpectating = true;
                    _log.LogInfo(
                        $"Scenario presentation spectating: bundle={bundleKey:X8}; local invocation unavailable");
                    return true;
                }
                _clientScenarioSpectating = false;
                _clientScenarioBundleId = _pendingClientScenarioStart.BundleId;
                ReplayScenarioStart(manager, _pendingClientScenarioStart);
                return true;
            case ManagerAction.ScenarioNode:
                if (bundleKey == 0 || bundleKey != _clientScenarioBundleKey || nodeId == 0)
                    return false;
                _clientScenarioNodeId = nodeId;
                if (_clientScenarioSpectating)
                    return true;
                if (manager == null || _blockedClientScenarioNode == null ||
                    _blockedClientScenarioNodeKey != nodeId)
                    return false;
                var sequence = _blockedClientScenarioNode;
                _blockedClientScenarioNode = null;
                _blockedClientScenarioNodeKey = 0;
                manager.ProceedScenario(sequence);
                return true;
            case ManagerAction.ScenarioFinished:
                if (bundleKey == 0)
                    return false;
                if (bundleKey != _clientScenarioBundleKey)
                {
                    if (CallbackIdentity(_pendingClientScenarioStart?.BundleKey ?? 0, bundleKey) ==
                        CallbackIdentityDecision.Clear)
                    {
                        CancelPendingScenarioStart();
                        _blockedClientScenarioNode = null;
                        _blockedClientScenarioNodeKey = 0;
                    }
                    return true;
                }
                if (manager?.IsPlaying == true)
                    manager.ForceStopScenario();
                CompleteScenarioCallback(bundleKey, nodeId != 0);
                _clientScenarioBundleId = string.Empty;
                _clientScenarioBundleKey = 0;
                _clientScenarioNodeId = -1;
                _clientScenarioSpectating = false;
                _pendingClientScenarioStart = null;
                _blockedClientScenarioNode = null;
                _blockedClientScenarioNodeKey = 0;
                return true;
            default:
                return false;
        }
    }

    private static void ReplayScenarioStart(
        ScenarioManager manager, PendingScenarioStart pending)
    {
        manager.m_ScenarioArgumentList = pending.Arguments;
        if (pending.Kind == ScenarioStartKind.Branch)
            manager.StartScenarioInternal(pending.BundleId, pending.BranchData, null);
        else
            manager.StartScenarioInternal(
                pending.BundleId, null, pending.UseButton, pending.ShowCurtain, pending.IgnorePlaying);
    }

    private static void ReplayDialogueStart(
        DialogueManager manager, PendingDialogueStart pending)
    {
        switch (pending.Kind)
        {
            case DialogueStartKind.Arguments:
                manager.StartDialogueV2(
                    pending.BundleId, pending.Arguments, null, pending.UseButton, pending.ShowCurtain);
                break;
            case DialogueStartKind.Small:
                manager.StartSmallDialogue(
                    pending.BundleId, null, pending.UseButton, pending.ShowCurtain);
                break;
            case DialogueStartKind.VisualNovel:
                manager.StartVisualNovel(
                    pending.BundleId, pending.Entries, pending.ButtonInfo, null);
                break;
            default:
                manager.StartDialogueV2(
                    pending.BundleId, null, pending.UseButton, pending.ShowCurtain);
                break;
        }
    }

    private void CompleteScenarioCallback(int bundleKey, bool result)
    {
        var decision = CallbackIdentity(
            _pendingClientScenarioStart?.BundleKey ?? 0, bundleKey);
        if (decision != CallbackIdentityDecision.Invoke)
        {
            if (decision == CallbackIdentityDecision.Clear)
                CancelPendingScenarioStart();
            return;
        }
        var callback = _pendingClientScenarioStart?.Callback;
        if (_pendingClientScenarioStart != null)
            _pendingClientScenarioStart.Callback = null;
        InvokeClientPresentationCallback(() => callback?.Invoke(result), "scenario");
    }

    private void CompleteDialogueCallback(int bundleKey, bool result)
    {
        var decision = CallbackIdentity(
            _pendingClientDialogueStart?.BundleKey ?? 0, bundleKey);
        if (decision != CallbackIdentityDecision.Invoke)
        {
            if (decision == CallbackIdentityDecision.Clear)
                CancelPendingDialogueStart();
            return;
        }
        var callback = _pendingClientDialogueStart?.Callback;
        if (_pendingClientDialogueStart != null)
            _pendingClientDialogueStart.Callback = null;
        InvokeClientPresentationCallback(() => callback?.Invoke(result), "dialogue");
    }

    private void CompleteDialogueChoiceCallback(int bundleKey, int choice)
    {
        var decision = CallbackIdentity(
            _pendingClientDialogueStart?.BundleKey ?? 0, bundleKey);
        if (decision != CallbackIdentityDecision.Invoke)
        {
            if (decision == CallbackIdentityDecision.Clear)
                CancelPendingDialogueStart();
            return;
        }
        var callback = _pendingClientDialogueStart?.ChoiceCallback;
        if (_pendingClientDialogueStart != null)
            _pendingClientDialogueStart.ChoiceCallback = null;
        InvokeClientPresentationCallback(() => callback?.Invoke(choice), "dialogue choice");
    }

    private void InvokeClientPresentationCallback(Action callback, string name)
    {
        if (callback == null)
            return;
        var probe = ProbeBehaviour.Instance;
        probe?.BeginClientPresentationLifecycle();
        try
        {
            callback();
        }
        catch (Exception exception)
        {
            _log.LogWarning($"Client {name} callback failed: {exception.Message}");
        }
        finally
        {
            probe?.EndClientPresentationLifecycle();
        }
    }

    private void CancelPendingScenarioStart()
    {
        var pending = _pendingClientScenarioStart;
        _pendingClientScenarioStart = null;
        _blockedClientScenarioNode = null;
        _blockedClientScenarioNodeKey = 0;
        var callback = pending?.Callback;
        if (pending != null)
            pending.Callback = null;
        InvokeClientPresentationCallback(() => callback?.Invoke(false), "scenario cancel");
    }

    private void CancelPendingDialogueStart()
    {
        var pending = _pendingClientDialogueStart;
        _pendingClientDialogueStart = null;
        var callback = pending?.Callback;
        var choiceCallback = pending?.ChoiceCallback;
        if (pending != null)
        {
            pending.Callback = null;
            pending.ChoiceCallback = null;
        }
        InvokeClientPresentationCallback(() => callback?.Invoke(false), "dialogue cancel");
        InvokeClientPresentationCallback(() => choiceCallback?.Invoke(-1), "dialogue choice cancel");
    }

    private void CancelTimelineLease(TimelineLease lease)
    {
        if (lease == null || lease.State == ClientTimelineState.Finished)
            return;
        lease.State = ClientTimelineState.Finished;
        lease.Skipped = true;
        var onStart = lease.OnStart;
        var onFinished = lease.OnFinished;
        lease.OnStart = null;
        lease.OnFinished = null;
        lease.StartDelivered = true;
        lease.FinishDelivered = true;
        InvokeClientPresentationCallback(() => onStart?.Invoke(), "timeline start cancel");
        InvokeClientPresentationCallback(() => onFinished?.Invoke(true), "timeline finish cancel");
    }

    private static CallbackIdentityDecision CallbackIdentity(int pendingKey, int bundleKey) =>
        pendingKey == 0
            ? CallbackIdentityDecision.None
            : pendingKey == bundleKey && bundleKey != 0
                ? CallbackIdentityDecision.Invoke
                : CallbackIdentityDecision.Clear;

    private bool ApplySushiResult(SushiResultState state)
    {
        try
        {
            var today = SushiBarAnalyticsManager.Instance?.TodayData;
            if (today == null)
                return false;
            SetSalesMenu?.Invoke(today, new object[] { state.SalesMenu });
            SetSalesEtc?.Invoke(today, new object[] { state.SalesEtc });
            SetStaffTips?.Invoke(today, new object[] { state.StaffTips });
            SetLikeCount?.Invoke(today, new object[] { state.LikeCount });
            today.TotalVisitCount = state.TotalVisits;
            today.GiveRating(state.Rating);
            return true;
        }
        catch (Exception exception)
        {
            _log.LogWarning($"Sushi result apply failed: {exception.Message}");
            return false;
        }
    }

    private void PublishSushiRuntimeChanges(UdpSession session)
    {
        if (UnityEngine.Object.FindFirstObjectByType<SushiBarManager>() == null)
            return;
        try
        {
            var menu = SushiBarMenuManager.Instance;
            if (menu != null)
            {
                for (var place = 0; place < (int)SushiBar.Place.Max; place++)
                {
                    var slots = menu.GetMenuSlotEnumerator((SushiBar.Place)place);
                    if (slots == null)
                        continue;
                    var iterator = slots.Cast<Il2CppSystem.Collections.IEnumerator>();
                    while (iterator.MoveNext())
                    {
                        var slot = slots.Current;
                        if (slot == null || slot.slotID < 0)
                            continue;
                        var flags = (slot.isSoldOut ? 1 : 0) | (slot.isAutoCooking ? 2 : 0);
                        var current = new MenuSlotState(
                            slot.recipeID, slot.nowPlateCount, slot.maxPlateCount, flags);
                        var known = _hostMenuSlots.TryGetValue(slot.slotID, out var previous);
                        if (!known || previous.RecipeId != current.RecipeId)
                            Publish(session, ManagerDomain.SushiMenu,
                                ManagerAction.MenuRecipe, current.RecipeId, slot.slotID);
                        if (!known || previous.NowCount != current.NowCount ||
                            previous.MaxCount != current.MaxCount)
                            Publish(session, ManagerDomain.SushiMenu,
                                ManagerAction.MenuCount,
                                PackUShorts(current.NowCount, current.MaxCount), slot.slotID);
                        if (!known || previous.Flags != current.Flags)
                            Publish(session, ManagerDomain.SushiMenu,
                                ManagerAction.MenuFlags, current.Flags, slot.slotID);
                        _hostMenuSlots[slot.slotID] = current;
                    }
                }
            }

            var operation = SushiBarContext.Operation;
            if (operation == null)
                return;
            for (var place = 0; place < _hostWasabi.Length; place++)
            {
                var count = operation.NowWasabiCount((SushiBar.Place)place);
                if (_hostWasabi[place] == count)
                    continue;
                _hostWasabi[place] = count;
                Publish(session, ManagerDomain.SushiWasabi,
                    ManagerAction.Result, count, place);
            }
        }
        catch (Exception exception)
        {
            _log.LogWarning($"Sushi runtime read failed: {exception.Message}");
        }
    }

    private static bool ApplyMenuState(ManagerAction action, int value, int slotId)
    {
        var slot = SushiBarMenuManager.Instance?.GetMenuSlot(slotId);
        if (slot == null)
            return false;
        switch (action)
        {
            case ManagerAction.MenuRecipe:
                slot.recipeID = value;
                return true;
            case ManagerAction.MenuCount:
                slot.nowPlateCount = value & 0xffff;
                slot.maxPlateCount = (int)((uint)value >> 16);
                return true;
            case ManagerAction.MenuFlags:
                slot.isSoldOut = (value & 1) != 0;
                slot.isAutoCooking = (value & 2) != 0;
                return true;
            default:
                return false;
        }
    }

    private static bool ApplyWasabiState(int target, int place)
    {
        if (place < 0 || place >= (int)SushiBar.Place.Max || target < 0)
            return false;
        var operation = SushiBarContext.Operation;
        if (operation == null)
            return false;
        var tag = (SushiBar.Place)place;
        var current = operation.NowWasabiCount(tag);
        return current == target || operation.UpdateWasabiCount(tag, target - current);
    }

    private static bool ApplyTableState(bool dirty, int context)
    {
        var place = (SushiBar.Place)((uint)context >> 16);
        var tableNumber = context & 0xffff;
        var table = SushibarTableManager.Instance?.GetTable(tableNumber, place);
        if (table == null)
            return false;
        table.SetTrash(dirty);
        return true;
    }

    private bool PublishStorySnapshot(UdpSession session)
    {
        var save = SaveSystem.GetGameSave();
        var chapter = save?.ChapterData;
        if (chapter == null)
            return false;
        Publish(session, ManagerDomain.Story, ManagerAction.CurrentChapter,
            chapter.CurrentChapter, 0);
        _hostCurrentChapter = chapter.CurrentChapter;
        Publish(session, ManagerDomain.Story, ManagerAction.ReservedChapter,
            chapter.ReservedChapter, 0);
        _hostReservedChapter = chapter.ReservedChapter;
        Publish(session, ManagerDomain.Story, ManagerAction.ResetEvents, 0, 0);
        _hostEvents.Clear();
        if (save.EventData?.clearedEvents != null)
            foreach (var id in save.EventData.clearedEvents)
            {
                Publish(session, ManagerDomain.Story, ManagerAction.EventCleared, (int)id, 0);
                _hostEvents.Add((int)id);
            }
        Publish(session, ManagerDomain.Story, ManagerAction.ResetCutscenes, 0, 0);
        _hostCutscenes.Clear();
        if (save.m_PlayedCutsceneData != null)
            foreach (var id in save.m_PlayedCutsceneData)
            {
                Publish(session, ManagerDomain.Story, ManagerAction.CutsceneMarked, id, 0);
                _hostCutscenes.Add(id);
            }
        Publish(session, ManagerDomain.Story, ManagerAction.ResetIntermissions, 0, 0);
        _hostIntermissions.Clear();
        if (save.m_MarkedIntermissionData != null)
            foreach (var id in save.m_MarkedIntermissionData)
            {
                Publish(session, ManagerDomain.Story, ManagerAction.IntermissionMarked, id, 0);
                _hostIntermissions.Add(id);
            }
        return true;
    }

    private void PublishStoryChanges(UdpSession session)
    {
        var save = SaveSystem.GetGameSave();
        var chapter = save?.ChapterData;
        if (chapter == null)
            return;
        if (chapter.CurrentChapter != _hostCurrentChapter)
        {
            _hostCurrentChapter = chapter.CurrentChapter;
            Publish(session, ManagerDomain.Story, ManagerAction.CurrentChapter,
                _hostCurrentChapter, 0);
        }
        if (chapter.ReservedChapter != _hostReservedChapter)
        {
            _hostReservedChapter = chapter.ReservedChapter;
            Publish(session, ManagerDomain.Story, ManagerAction.ReservedChapter,
                _hostReservedChapter, 0);
        }

        var events = new HashSet<int>();
        if (save.EventData?.clearedEvents != null)
            foreach (var id in save.EventData.clearedEvents)
                events.Add((int)id);
        PublishSetChanges(session, _hostEvents, events,
            ManagerAction.EventCleared, ManagerAction.EventRemoved);

        var cutscenes = new HashSet<int>();
        if (save.m_PlayedCutsceneData != null)
            foreach (var id in save.m_PlayedCutsceneData)
                cutscenes.Add(id);
        PublishSetChanges(session, _hostCutscenes, cutscenes,
            ManagerAction.CutsceneMarked, ManagerAction.CutsceneUnmarked);

        var intermissions = new HashSet<int>();
        if (save.m_MarkedIntermissionData != null)
            foreach (var id in save.m_MarkedIntermissionData)
                intermissions.Add(id);
        PublishSetChanges(session, _hostIntermissions, intermissions,
            ManagerAction.IntermissionMarked, ManagerAction.IntermissionUnmarked);
    }

    private void PublishSetChanges(
        UdpSession session,
        HashSet<int> previous,
        HashSet<int> current,
        ManagerAction added,
        ManagerAction removed)
    {
        foreach (var id in current)
            if (!previous.Contains(id))
                Publish(session, ManagerDomain.Story, added, id, 0);
        foreach (var id in previous)
            if (!current.Contains(id))
                Publish(session, ManagerDomain.Story, removed, id, 0);
        previous.Clear();
        previous.UnionWith(current);
    }

    private void PublishDayChanges(UdpSession session)
    {
        try
        {
            var day = DayManager.Instance;
            if (day != null)
            {
                var ticks = day.TodayDate.Ticks;
                if (ticks != _hostDayTicks)
                {
                    _hostDayTicks = ticks;
                    Publish(session, ManagerDomain.Day, ManagerAction.DayDate,
                        unchecked((int)ticks), unchecked((int)(ticks >> 32)));
                }
                var time = (int)day.CurrentTimeState;
                if (time != _hostDayTime)
                {
                    _hostDayTime = time;
                    Publish(session, ManagerDomain.Day, ManagerAction.DayTime, time, 0);
                }
            }
            var weather = WeatherManager.Instance;
            if (weather != null && (int)weather.CurrentWeatherType != _hostWeather)
            {
                _hostWeather = (int)weather.CurrentWeatherType;
                Publish(session, ManagerDomain.Day, ManagerAction.Weather, _hostWeather, 0);
            }
        }
        catch (Exception exception)
        {
            _log.LogWarning($"Day state read failed: {exception.Message}");
        }
    }

    private bool ApplyDay(ManagerAction action, int value, int context)
    {
        switch (action)
        {
            case ManagerAction.DayDate:
                var day = DayManager.Instance;
                if (day == null)
                    return false;
                var ticks = unchecked((long)(uint)value | (long)context << 32);
                if (ticks <= 0)
                    return true;
                _originalDayTicks ??= day.TodayDate.Ticks;
                day.m_TodayDate = new Il2CppSystem.DateTime(ticks);
                return true;
            case ManagerAction.DayTime:
                if (DayManager.Instance == null)
                    return false;
                _originalDayTime ??= (int)DayManager.Instance.CurrentTimeState;
                DayManager.Instance.SetTimeState((DayTimeState)value);
                return true;
            case ManagerAction.Weather:
                if (WeatherManager.Instance == null)
                    return false;
                _originalWeather ??= (int)WeatherManager.Instance.CurrentWeatherType;
                WeatherManager.Instance.ChangeCurrentWeather((WeatherType)value);
                return true;
            default:
                return false;
        }
    }

    private void PublishTimelineProgress(UdpSession session)
    {
        var director = TimelineManager.Instance?.currentTimeline?.Director;
        if (director == null)
            return;
        var playing = director.state == UnityEngine.Playables.PlayState.Playing;
        Publish(session, ManagerDomain.Timeline, ManagerAction.TimelineProgress,
            playing ? _hostTimelineTid : -_hostTimelineTid,
            BitConverter.SingleToInt32Bits((float)director.time));
    }

    private bool ApplyTimeline(ManagerAction action, int tid, int context, uint hostTick)
    {
        var manager = TimelineManager.Instance;
        if (manager == null || tid == 0 || tid == int.MinValue)
            return false;
        var timelineTid = Math.Abs(tid);
        if (action == ManagerAction.TimelineStart)
        {
            var generation = UnpackTimelineGeneration(context);
            if (generation == 0)
                return true;
            var route = UnpackTimelineFlag(context)
                ? TimelineStartRoute.ByController
                : TimelineStartRoute.ByTid;
            var lease = _clientTimelineLease;
            if (TimelineGenerationIdentity(
                    lease?.Tid ?? 0, lease?.Generation ?? 0,
                    timelineTid, generation) == CallbackIdentityDecision.Invoke)
            {
                _timelineSyncTid = timelineTid;
                _clientTimelineHostTick = LocalizeHostTick(hostTick);
                return true;
            }
            var intent = lease?.Generation == 0 && lease.Tid == timelineTid &&
                lease.Route == route ? lease : null;
            if (intent == null)
                CancelTimelineLease(lease);
            _clientTimelineLease = lease = new TimelineLease
            {
                Tid = timelineTid,
                Generation = generation,
                Route = route,
                State = ClientTimelineState.AwaitingClientIntent,
                HasIntent = intent?.HasIntent == true,
                Controller = intent?.Controller,
                ControllerId = intent?.ControllerId ?? 0,
                OnStart = intent?.OnStart,
                OnFinished = intent?.OnFinished,
                ApplyOffset = intent?.ApplyOffset ?? true,
                CustomPos = intent?.CustomPos ?? default
            };
            var decision = DecideTimelineStart(
                lease.HasIntent, route, lease.Controller != null);
            _timelineSyncTid = timelineTid;
            _clientTimelineHostTick = LocalizeHostTick(hostTick);
            _clientTimelineTime = 0;
            _clientTimelinePlaying = true;
            if (decision == TimelineStartDecision.ConsumeSpectator)
            {
                lease.State = ClientTimelineState.Spectating;
                lease.StartObserved = true;
                DeliverTimelineStart(lease);
                return true;
            }
            if (_clientTimelineTid != timelineTid && lease.State is not
                (ClientTimelineState.Starting or ClientTimelineState.Playing))
            {
                lease.State = ClientTimelineState.Starting;
                var startRelay = CreateTimelineStartRelay(timelineTid, generation);
                var finishRelay = CreateTimelineFinishRelay(timelineTid, generation);
                if (decision == TimelineStartDecision.ReplayByController)
                    manager.PlayController(
                        lease.Controller, startRelay, finishRelay, lease.ControllerId);
                else
                    manager.Play(timelineTid, startRelay, finishRelay,
                        lease.ApplyOffset, lease.CustomPos);
            }
            return true;
        }
        if (action == ManagerAction.TimelineProgress)
        {
            var time = BitConverter.Int32BitsToSingle(context);
            if (!float.IsFinite(time) || time < 0f)
                return false;
            _timelineSyncTid = timelineTid;
            _clientTimelineHostTick = LocalizeHostTick(hostTick);
            _clientTimelineTime = time;
            _clientTimelinePlaying = tid > 0;
            var lease = _clientTimelineLease;
            if (lease == null || lease.State == ClientTimelineState.Spectating)
                return true;
            if (_clientTimelineTid != timelineTid)
            {
                if (lease.Tid != timelineTid)
                    return true;
                if (lease.State is not
                    (ClientTimelineState.Starting or ClientTimelineState.Playing))
                    return true;
            }
            return true;
        }
        if (action != ManagerAction.TimelineFinish)
            return false;
        var finishGeneration = UnpackTimelineGeneration(context);
        if (finishGeneration == 0)
            return true;
        var finishLease = _clientTimelineLease;
        if (TimelineGenerationIdentity(
                finishLease?.Tid ?? 0, finishLease?.Generation ?? 0,
                timelineTid, finishGeneration) !=
            CallbackIdentityDecision.Invoke)
            return true;
        var spectating = finishLease.State == ClientTimelineState.Spectating;
        finishLease.HostFinishObserved = true;
        finishLease.Skipped = UnpackTimelineFlag(context);
        finishLease.State = ClientTimelineState.AwaitingHostFinish;
        if (spectating)
        {
            finishLease.StartObserved = true;
            finishLease.RelayFinishObserved = true;
            DeliverTimelineFinish(finishLease);
        }
        else
        if (_clientTimelineTid == timelineTid)
        {
            if (finishLease.Skipped)
            {
                var controller = manager.currentTimeline;
                if (controller == null)
                    return false;
                controller.Skip();
            }
            else
                manager.Finish(timelineTid);
        }
        else if (!finishLease.RelayFinishObserved)
            return false;
        DeliverTimelineFinish(finishLease);
        _clientTimelineTid = 0;
        _timelineSyncTid = 0;
        _clientTimelineHostTick = 0;
        _clientTimelineTime = 0;
        _clientTimelinePlaying = false;
        return true;
    }

    private void AlignTimeline()
    {
        if (_timelineSyncTid == 0 || _clientTimelineTid != _timelineSyncTid)
            return;
        var director = TimelineManager.Instance?.currentTimeline?.Director;
        if (director == null)
            return;
        var elapsed = _clientTimelinePlaying
            ? TickElapsedSeconds(_clientTimelineHostTick, CurrentTick())
            : 0;
        var expected = Math.Min(director.duration, _clientTimelineTime + elapsed);
        if (Math.Abs(director.time - expected) < 0.25)
            return;
        director.time = expected;
        director.Evaluate();
    }

    private bool ApplyStory(ManagerAction action, int value)
    {
        var save = SaveSystem.GetGameSave();
        if (save == null)
            return false;
        switch (action)
        {
            case ManagerAction.CurrentChapter when save.ChapterData != null:
                _originalCurrentChapter ??= save.ChapterData.CurrentChapter;
                save.ChapterData.CurrentChapter = value;
                return true;
            case ManagerAction.ReservedChapter when save.ChapterData != null:
                _originalReservedChapter ??= save.ChapterData.ReservedChapter;
                save.ChapterData.ReservedChapter = value;
                return true;
            case ManagerAction.EventCleared when save.EventData != null:
                Remember(_originalEvents, value,
                    save.EventData.IsCleared((Common.Contents.Event.Name)value));
                save.EventData.AddCleared((Common.Contents.Event.Name)value);
                return true;
            case ManagerAction.EventRemoved when save.EventData != null:
                Remember(_originalEvents, value,
                    save.EventData.IsCleared((Common.Contents.Event.Name)value));
                save.EventData.RemoveCleared((Common.Contents.Event.Name)value);
                return true;
            case ManagerAction.CutsceneMarked:
                Remember(_originalCutscenes, value, save.IsPlayed(value));
                save.MarkCutscene(value);
                return true;
            case ManagerAction.CutsceneUnmarked:
                Remember(_originalCutscenes, value, save.IsPlayed(value));
                save.UnMarkCutscene(value);
                return true;
            case ManagerAction.IntermissionMarked:
                Remember(_originalIntermissions, value, save.IsMarkedIntermission(value));
                save.MarkIntermission(value);
                return true;
            case ManagerAction.IntermissionUnmarked:
                Remember(_originalIntermissions, value, save.IsMarkedIntermission(value));
                save.UnMarkIntermission(value);
                return true;
            case ManagerAction.ResetEvents:
                if (save.EventData?.clearedEvents != null)
                {
                    foreach (var id in save.EventData.clearedEvents)
                        Remember(_originalEvents, (int)id, true);
                    save.EventData.clearedEvents.Clear();
                }
                return true;
            case ManagerAction.ResetCutscenes:
                if (save.m_PlayedCutsceneData != null)
                {
                    foreach (var id in save.m_PlayedCutsceneData)
                        Remember(_originalCutscenes, id, true);
                    save.m_PlayedCutsceneData.Clear();
                }
                return true;
            case ManagerAction.ResetIntermissions:
                if (save.m_MarkedIntermissionData != null)
                {
                    foreach (var id in save.m_MarkedIntermissionData)
                        Remember(_originalIntermissions, id, true);
                    save.m_MarkedIntermissionData.Clear();
                }
                return true;
            default:
                return false;
        }
    }

    private static int PackUShorts(int low, int high) =>
        (Math.Clamp(low, 0, ushort.MaxValue) & 0xffff) |
        (Math.Clamp(high, 0, ushort.MaxValue) << 16);

    private bool ValidateDialogueIntent(ManagerEvent state)
    {
        if ((ManagerAction)state.Action == ManagerAction.PhoneAnswered)
            return IsPhoneIntentValid(
                ManagerAction.PhoneAnswered, state.Value, _hostPhoneTid, _hostPhoneTid != 0);
        var manager = DialogueManager.Instance;
        return manager != null && state.Value == _hostDialogueBundleKey &&
            state.Context == _hostDialogueIndex && IsDialogueIntentValid(
            (ManagerAction)state.Action,
            state.Value,
            state.Context,
            _hostDialogueBundleKey != 0,
            manager.IsPlaying,
            ContentKey(manager.CurrentBundleID),
            manager.m_CurrentDialogueIndex,
            manager.CanChoiceButtonAciton && manager.m_ScenarioButtonInfo != null,
            manager.IsShowSkipButton,
            false);
    }

    private static bool IsPhoneIntentValid(
        ManagerAction action, int requestedTid, int activeTid, bool active) =>
        action == ManagerAction.PhoneAnswered && active && requestedTid > 0 &&
        requestedTid == activeTid;

    private static bool AllowsLeasedDialogueControl(
        SessionRole role, bool connected, bool leased, ManagerAction action) =>
        role == SessionRole.Client && connected && leased &&
        action is ManagerAction.Continue or ManagerAction.Skip;

    private static int HostPhoneAfterClear(bool preserveHostSessions, int activeTid) =>
        preserveHostSessions ? activeTid : 0;

    private static bool IsDialogueIntentValid(
        ManagerAction action,
        int requestBundleKey,
        int requestIndex,
        bool active,
        bool playing,
        int liveBundleKey,
        int liveIndex,
        bool canChoose,
        bool canSkip,
        bool pending)
    {
        if (!active || !playing || pending || requestBundleKey == 0 ||
            requestBundleKey != liveBundleKey || requestIndex < 0 || requestIndex != liveIndex)
            return false;
        return action switch
        {
            ManagerAction.Continue => !canChoose,
            ManagerAction.Skip => canSkip,
            ManagerAction.FirstChoice or ManagerAction.SecondChoice => false,
            _ => false
        };
    }

    private void PublishActiveSessionSnapshot(UdpSession session)
    {
        if (_hostTimelineTid != 0 && _hostTimelineGeneration != 0)
        {
            Publish(session, ManagerDomain.Timeline, ManagerAction.TimelineStart,
                _hostTimelineTid,
                PackTimelineContext(_hostTimelineGeneration,
                    _hostTimelineActiveRoute == TimelineStartRoute.ByController));
            PublishTimelineProgress(session);
        }
        if (_hostScenarioBundleKey != 0)
        {
            Publish(session, ManagerDomain.Scenario, ManagerAction.ScenarioStarted,
                _hostScenarioBundleKey, 0);
            if (_hostScenarioNodeId >= 0)
                Publish(session, ManagerDomain.Scenario, ManagerAction.ScenarioNode,
                    _hostScenarioBundleKey, _hostScenarioNodeId);
        }
        if (_hostDialogueBundleKey != 0)
        {
            Publish(session, ManagerDomain.Dialogue, ManagerAction.DialogueStarted,
                _hostDialogueBundleKey, 0);
            if (_hostDialogueIndex >= 0)
                Publish(session, ManagerDomain.Dialogue, ManagerAction.DialogueNode,
                    _hostDialogueBundleKey, _hostDialogueIndex);
        }
        if (_hostPhoneTid != 0)
            Publish(session, ManagerDomain.Dialogue, ManagerAction.PhoneCall, _hostPhoneTid, 0);
    }

    private static int ScenarioNodeKey(ScenarioManager manager, DRSequence sequence) =>
        ScenarioNodeKey(
            manager?.m_CurrentConversation?.GetSequenceID(),
            sequence?.dialogueEntry?.GetSequenceID(),
            sequence?.dialogueEntry?.id ?? 0);

    private static int ScenarioNodeKey(string scenarioId, string sequenceId, int entryId)
    {
        if (string.IsNullOrEmpty(scenarioId) || string.IsNullOrEmpty(sequenceId))
            return 0;
        return ContentKey($"{scenarioId}\u001f{sequenceId}\u001f{entryId}");
    }

    internal static List<MethodBase> GetScenarioStartTargets() => ExistingMethods(
        AccessTools.DeclaredMethod(typeof(ScenarioManager), "StartScenarioInternal",
            new[] { typeof(string), typeof(ScenarioBranchData), typeof(Il2CppSystem.Action<bool>) }),
        AccessTools.DeclaredMethod(typeof(ScenarioManager), "StartScenarioInternal",
            new[] { typeof(string), typeof(Il2CppSystem.Action<bool>), typeof(bool), typeof(bool), typeof(bool) }));

    internal static List<MethodBase> GetDialogueStartTargets() => ExistingMethods(
        AccessTools.DeclaredMethod(typeof(DialogueManager), nameof(DialogueManager.StartDialogueV2),
            new[] { typeof(string), typeof(Il2CppSystem.Collections.Generic.List<string>),
                typeof(Il2CppSystem.Action<bool>), typeof(bool), typeof(bool) }),
        AccessTools.DeclaredMethod(typeof(DialogueManager), nameof(DialogueManager.StartDialogueV2),
            new[] { typeof(string), typeof(Il2CppSystem.Action<bool>), typeof(bool), typeof(bool) }),
        AccessTools.DeclaredMethod(typeof(DialogueManager), nameof(DialogueManager.StartSmallDialogue),
            new[] { typeof(string), typeof(Il2CppSystem.Action<bool>), typeof(bool), typeof(bool) }),
        AccessTools.DeclaredMethod(typeof(DialogueManager), nameof(DialogueManager.StartVisualNovel),
            new[] { typeof(string), typeof(Il2CppSystem.Collections.Generic.List<DialogueEntry>),
                typeof(ScenarioButtonInfo), typeof(Il2CppSystem.Action<int>) }));

    internal static List<MethodBase> GetScenarioFinishTargets() => ExistingMethods(
        AccessTools.DeclaredMethod(typeof(ScenarioManager), nameof(ScenarioManager.ForceStopScenario)));

    internal static List<MethodBase> GetDialogueNodeTargets() => ExistingMethods(
        AccessTools.DeclaredMethod(typeof(DialogueManager), "UpdateCurrentDialogue",
            new[] { typeof(DialogueInfo) }),
        AccessTools.DeclaredMethod(typeof(DialogueManager), "UpdateCurrentBranchDialogue",
            new[] { typeof(DialogueInfo) }));

    internal static List<MethodBase> GetTimelineTargets() => ExistingMethods(
        AccessTools.DeclaredMethod(typeof(TimelineManager), nameof(TimelineManager.Play),
            new[] { typeof(int), typeof(Il2CppSystem.Action),
                typeof(Il2CppSystem.Action<bool>), typeof(bool),
                typeof(Il2CppSystem.Nullable<Vector3>) }),
        AccessTools.DeclaredMethod(typeof(TimelineManager), nameof(TimelineManager.PlayController),
            new[] { typeof(TimelineController), typeof(Il2CppSystem.Action),
                typeof(Il2CppSystem.Action<bool>), typeof(int) }),
        AccessTools.DeclaredMethod(typeof(TimelineManager), nameof(TimelineManager.Finish),
            new[] { typeof(int) }),
        AccessTools.DeclaredMethod(typeof(TimelineController), nameof(TimelineController.Skip),
            Type.EmptyTypes));

    internal static List<MethodBase> GetPhoneTargets() => ExistingMethods(
        AccessTools.DeclaredMethod(typeof(DialogueManager), nameof(DialogueManager.ShowPhoneCall),
            new[] { typeof(int), typeof(Il2CppSystem.Action<bool>) }),
        AccessTools.DeclaredMethod(typeof(SideMissionPhonePanel),
            nameof(SideMissionPhonePanel.OnAnswerPhone), Type.EmptyTypes),
        AccessTools.DeclaredMethod(typeof(MissionManager),
            nameof(MissionManager.UpdateMissionNPCPhoneCall), new[] { typeof(int) }));

    private static bool PhoneTargetsMatch(IReadOnlyList<MethodBase> targets) =>
        targets.Count == 3 &&
        targets[0].DeclaringType == typeof(DialogueManager) &&
        targets[0].GetParameters().Length == 2 &&
        targets[1].DeclaringType == typeof(SideMissionPhonePanel) &&
        targets[1].GetParameters().Length == 0 &&
        targets[2].DeclaringType == typeof(MissionManager) &&
        targets[2].GetParameters().Length == 1;

    private static bool TimelineTargetsMatch(IReadOnlyList<MethodBase> targets) =>
        targets.Count == 4 &&
        targets[0].DeclaringType == typeof(TimelineManager) &&
        targets[0].Name == nameof(TimelineManager.Play) &&
        targets[0].GetParameters().Length == 5 &&
        targets[1].DeclaringType == typeof(TimelineManager) &&
        targets[1].Name == nameof(TimelineManager.PlayController) &&
        targets[1].GetParameters().Length == 4 &&
        targets[2].DeclaringType == typeof(TimelineManager) &&
        targets[2].Name == nameof(TimelineManager.Finish) &&
        targets[2].GetParameters().Length == 1 &&
        targets[3].DeclaringType == typeof(TimelineController) &&
        targets[3].Name == nameof(TimelineController.Skip) &&
        targets[3].GetParameters().Length == 0;

    internal static List<MethodBase> GetDialogueTerminalTargets() => ExistingMethods(
        AccessTools.DeclaredMethod(typeof(DialogueManager), "ScenarioFinish",
            new[] { typeof(int) }),
        AccessTools.DeclaredMethod(typeof(DialogueManager), "ImageChoiceDialoguePanelFinish",
            new[] { typeof(int) }),
        AccessTools.DeclaredMethod(typeof(DialogueManager), "OnDialogueFinished"),
        AccessTools.DeclaredMethod(typeof(DialogueManager), "TotalFinishDialogue"),
        AccessTools.DeclaredMethod(typeof(DialogueManager), nameof(DialogueManager.ForceFinishSmallDialogue)),
        AccessTools.DeclaredMethod(typeof(DialogueManager), nameof(DialogueManager.ForceFinishDialogue),
            new[] { typeof(bool) }));

    private static List<MethodBase> ExistingMethods(params MethodBase[] methods)
    {
        var result = new List<MethodBase>(methods.Length);
        foreach (var method in methods)
            if (method != null)
                result.Add(method);
        return result;
    }

    private static int ContentKey(string value) => string.IsNullOrEmpty(value)
        ? 0
        : unchecked((int)Protocol.SceneId(value));

    private static TimelineStartDecision DecideTimelineStart(
        bool hasIntent, TimelineStartRoute route, bool hasController)
    {
        if (route == TimelineStartRoute.ByController && (!hasIntent || !hasController))
            return TimelineStartDecision.ConsumeSpectator;
        return route == TimelineStartRoute.ByController
            ? TimelineStartDecision.ReplayByController
            : TimelineStartDecision.ReplayByTid;
    }

    private static CallbackIdentityDecision TimelineCallbackIdentity(
        int leaseTid, int eventTid) => CallbackIdentity(leaseTid, eventTid);

    private static CallbackIdentityDecision TimelineGenerationIdentity(
        int leaseTid, int leaseGeneration, int eventTid, int eventGeneration) =>
        leaseTid == 0 || leaseGeneration == 0
            ? CallbackIdentityDecision.None
            : leaseTid == eventTid && leaseGeneration == eventGeneration
                ? CallbackIdentityDecision.Invoke
                : CallbackIdentityDecision.Clear;

    private static int NextTimelineGeneration(int generation) =>
        generation >= MaxTimelineGeneration ? 1 : generation + 1;

    private static int PackTimelineContext(int generation, bool flag) =>
        Math.Clamp(generation, 0, MaxTimelineGeneration) << 1 | (flag ? 1 : 0);

    private static int UnpackTimelineGeneration(int context) =>
        (int)((uint)context >> 1);

    private static bool UnpackTimelineFlag(int context) => (context & 1) != 0;

    private static TimelineCallbackDecision DecideTimelineCallbackDelivery(
        bool observed, bool delivered, bool hasCallback) =>
        !observed || delivered
            ? TimelineCallbackDecision.None
            : hasCallback
                ? TimelineCallbackDecision.Invoke
                : TimelineCallbackDecision.Wait;

    private static TimelineFinishDecision DecideTimelineFinishDelivery(
        bool startReady, bool hostFinished, bool relayFinished, bool hasCallback)
    {
        if (!startReady)
            return TimelineFinishDecision.WaitForStart;
        if (!hostFinished || !relayFinished || !hasCallback)
            return TimelineFinishDecision.Wait;
        return TimelineFinishDecision.Invoke;
    }

    private static bool ShouldDropTimelineLease(bool sceneChanged, bool timelineActive) =>
        sceneChanged && !timelineActive;

    private static uint NextRevision(uint value) => value == uint.MaxValue ? 1 : value + 1;

    private static uint GetRevision(
        Dictionary<ManagerDomain, uint> revisions,
        ManagerDomain lane) => revisions.TryGetValue(lane, out var revision) ? revision : 0;

    private static void ResetLaneState(
        Dictionary<ManagerDomain, SortedDictionary<uint, ManagerEvent>> pending,
        Dictionary<ManagerDomain, uint> hostRevisions,
        Dictionary<ManagerDomain, uint> clientRevisions)
    {
        pending.Clear();
        hostRevisions.Clear();
        clientRevisions.Clear();
    }

    private static RestoreDecision DecideRestore(
        bool pending,
        SessionRole role,
        bool connected,
        int attempts)
    {
        if (!pending)
            return RestoreDecision.None;
        return role == SessionRole.Client && connected
            ? RestoreDecision.None
            : RestoreDecision.Retry;
    }

    private static ManagerDomain LaneOf(ManagerDomain domain) => domain;

    private static uint CurrentTick() =>
        unchecked((uint)(Time.realtimeSinceStartupAsDouble * 1000.0));

    private static double TickElapsedSeconds(uint start, uint end) =>
        unchecked(end - start) / 1000.0;

    private uint LocalizeHostTick(uint hostTick) =>
        _hasHostClockOffset ? unchecked(hostTick + _hostClockOffset) : CurrentTick();

    private static void Remember(Dictionary<int, bool> originals, int id, bool value)
    {
        if (!originals.ContainsKey(id))
            originals[id] = value;
    }

    private bool HasClientOriginals() =>
        _originalCurrentChapter.HasValue || _originalReservedChapter.HasValue ||
        _originalDayTicks.HasValue || _originalDayTime.HasValue || _originalWeather.HasValue ||
        _originalEvents.Count > 0 || _originalCutscenes.Count > 0 ||
        _originalIntermissions.Count > 0;

    private void ResolveClientRestore(RestoreDecision decision)
    {
        if (decision == RestoreDecision.None)
            return;
        if (RestoreClientState())
            _restorePending = false;
    }

    internal bool TryRestoreClientOriginals()
    {
        if (!HasClientOriginals())
            return true;
        _restorePending = true;
        if (!RestoreClientState())
            return false;
        _restorePending = false;
        return true;
    }

    private bool RestoreClientState()
    {
        _applying = true;
        try
        {
            RestoreClientStory();
            RestoreClientDay();
            return !HasClientOriginals();
        }
        finally
        {
            _applying = false;
        }
    }

    private void RestoreClientStory()
    {
        if (_originalCurrentChapter == null && _originalReservedChapter == null &&
            _originalEvents.Count == 0 && _originalCutscenes.Count == 0 &&
            _originalIntermissions.Count == 0)
            return;
        try
        {
            var save = SaveSystem.GetGameSave();
            if (save == null ||
                ((_originalCurrentChapter.HasValue || _originalReservedChapter.HasValue) &&
                 save.ChapterData == null) ||
                (_originalEvents.Count > 0 && save.EventData == null))
                throw new InvalidOperationException("client save is unavailable");
            if (_originalCurrentChapter.HasValue)
                save.ChapterData.CurrentChapter = _originalCurrentChapter.Value;
            if (_originalReservedChapter.HasValue)
                save.ChapterData.ReservedChapter = _originalReservedChapter.Value;
            foreach (var pair in _originalEvents)
                if (pair.Value)
                    save.EventData.AddCleared((Common.Contents.Event.Name)pair.Key);
                else
                    save.EventData.RemoveCleared((Common.Contents.Event.Name)pair.Key);
            foreach (var pair in _originalCutscenes)
                if (pair.Value)
                    save.MarkCutscene(pair.Key);
                else
                    save.UnMarkCutscene(pair.Key);
            foreach (var pair in _originalIntermissions)
                if (pair.Value)
                    save.MarkIntermission(pair.Key);
                else
                    save.UnMarkIntermission(pair.Key);
            _originalCurrentChapter = null;
            _originalReservedChapter = null;
            _originalEvents.Clear();
            _originalCutscenes.Clear();
            _originalIntermissions.Clear();
        }
        catch (Exception exception)
        {
            _log.LogWarning($"Client story restore failed: {exception.Message}");
        }
    }

    private void RestoreClientDay()
    {
        if (_originalDayTicks == null && _originalDayTime == null && _originalWeather == null)
            return;
        try
        {
            var day = DayManager.Instance;
            var weather = WeatherManager.Instance;
            if ((_originalDayTicks.HasValue || _originalDayTime.HasValue) && day == null ||
                _originalWeather.HasValue && weather == null)
                throw new InvalidOperationException("client day manager is unavailable");
            if (_originalDayTicks.HasValue)
                day.m_TodayDate = new Il2CppSystem.DateTime(_originalDayTicks.Value);
            if (_originalDayTime.HasValue)
                day.m_CurrentTimeState = (DayTimeState)_originalDayTime.Value;
            if (_originalWeather.HasValue)
                weather.ChangeCurrentWeather((WeatherType)_originalWeather.Value);
            _originalDayTicks = null;
            _originalDayTime = null;
            _originalWeather = null;
        }
        catch (Exception exception)
        {
            _log.LogWarning($"Client day restore failed: {exception.Message}");
        }
    }

    private static bool IsGlobal(ManagerDomain domain) =>
        domain is ManagerDomain.Story or ManagerDomain.Day or
            ManagerDomain.Dialogue or ManagerDomain.Scenario;

    private static bool IsNewer(uint revision, uint previous) =>
        unchecked((int)(revision - previous)) > 0;
}

internal static class ManagerEventPatchHelper
{
    internal static bool Intercept(ManagerDomain domain, ManagerAction action, int value = 0)
    {
        var behaviour = ProbeBehaviour.Instance;
        var dialogue = domain == ManagerDomain.Dialogue ? DialogueManager.Instance : null;
        var context = dialogue?.m_CurrentDialogueIndex ?? 0;
        if (dialogue != null)
            value = string.IsNullOrEmpty(dialogue.CurrentBundleID)
                ? 0
                : unchecked((int)Protocol.SceneId(dialogue.CurrentBundleID));
        return behaviour?.InterceptManagerEvent(domain, action, value, context) ?? true;
    }

    internal static bool Begin(
        ManagerDomain domain,
        ManagerAction action,
        out bool suppressNested)
    {
        var behaviour = ProbeBehaviour.Instance;
        var dialogue = DialogueManager.Instance;
        var value = string.IsNullOrEmpty(dialogue?.CurrentBundleID)
            ? 0
            : unchecked((int)Protocol.SceneId(dialogue.CurrentBundleID));
        var context = dialogue?.m_CurrentDialogueIndex ?? 0;
        if (behaviour != null)
            return behaviour.BeginManagerEvent(
                domain, action, value, context, out suppressNested);
        suppressNested = false;
        return true;
    }

    internal static void End(bool suppressNested) =>
        ProbeBehaviour.Instance?.EndManagerEvent(suppressNested);

    internal static int TableContext(SushiBarTable table)
    {
        if (table == null)
            return 0;
        for (var place = 0; place < (int)SushiBar.Place.Max; place++)
            if (SushibarTableManager.Instance?.GetTable(
                    table.tableNumber, (SushiBar.Place)place) == table)
                return place << 16 | table.tableNumber & 0xffff;
        return table.tableNumber & 0xffff;
    }
}

[HarmonyPatch(typeof(SushiBarManager), nameof(SushiBarManager.OnEventSushiBarOpened))]
internal static class MainSushiOpenSyncPatch
{
    private static bool Prefix() =>
        ManagerEventPatchHelper.Intercept(ManagerDomain.MainSushi, ManagerAction.Start);
}

[HarmonyPatch(typeof(SushiBarManager), nameof(SushiBarManager.OnEnterEveningLastSpurt))]
internal static class MainSushiLastSpurtSyncPatch
{
    private static bool Prefix() =>
        ManagerEventPatchHelper.Intercept(ManagerDomain.MainSushi, ManagerAction.LastSpurt);
}

[HarmonyPatch(typeof(SushiBarManager), "OnFinishEveningTime")]
internal static class MainSushiFinishSyncPatch
{
    private static bool Prefix() =>
        ManagerEventPatchHelper.Intercept(ManagerDomain.MainSushi, ManagerAction.Finish);
}

[HarmonyPatch(typeof(JDLC.JungleSushiBarSceneManager), nameof(JDLC.JungleSushiBarSceneManager.OnOpenSushiBar))]
internal static class JungleSushiOpenSyncPatch
{
    private static bool Prefix() =>
        ManagerEventPatchHelper.Intercept(ManagerDomain.JungleSushi, ManagerAction.Start);
}

[HarmonyPatch(typeof(JDLC.JungleSushiBarSceneManager), nameof(JDLC.JungleSushiBarSceneManager.OnEnterEveningLastSpurt))]
internal static class JungleSushiLastSpurtSyncPatch
{
    private static bool Prefix() =>
        ManagerEventPatchHelper.Intercept(ManagerDomain.JungleSushi, ManagerAction.LastSpurt);
}

[HarmonyPatch(typeof(JDLC.JungleSushiBarSceneManager), "OnCloseSushiBar")]
internal static class JungleSushiFinishSyncPatch
{
    private static bool Prefix(bool __0) =>
        ManagerEventPatchHelper.Intercept(
            ManagerDomain.JungleSushi, ManagerAction.Finish, __0 ? 1 : 0);
}

[HarmonyPatch(typeof(DialogueManager), nameof(DialogueManager.ContinueDialogueManual))]
internal static class DialogueContinueSyncPatch
{
    private static bool Prefix() =>
        ManagerEventPatchHelper.Intercept(ManagerDomain.Dialogue, ManagerAction.Continue);
}

[HarmonyPatch(typeof(DialogueManager), nameof(DialogueManager.OnSkip))]
internal static class DialogueSkipSyncPatch
{
    private static bool Prefix() =>
        ManagerEventPatchHelper.Intercept(ManagerDomain.Dialogue, ManagerAction.Skip);
}

[HarmonyPatch]
internal static class DialoguePhoneCallAuthorityPatch
{
    private static MethodBase TargetMethod() => ManagerEventReplicator.GetPhoneTargets()[0];

    private static bool Prefix(int __0, Il2CppSystem.Action<bool> __1) =>
        ProbeBehaviour.Instance?.InterceptPhoneCall(__0, __1) ?? true;
}

[HarmonyPatch]
internal static class DialoguePhoneAnswerAuthorityPatch
{
    private static MethodBase TargetMethod() => ManagerEventReplicator.GetPhoneTargets()[1];

    private static bool Prefix() => ProbeBehaviour.Instance?.InterceptPhoneAnswer() ?? true;
}

[HarmonyPatch(typeof(DialogueManager), nameof(DialogueManager.ExcuteFirstDialogue))]
internal static class DialogueFirstChoiceSyncPatch
{
    private static bool Prefix(out bool __state)
    {
        if (!(ProbeBehaviour.Instance?.AllowDialogueChoice() ?? true))
        {
            __state = false;
            return false;
        }
        return ManagerEventPatchHelper.Begin(
            ManagerDomain.Dialogue, ManagerAction.FirstChoice, out __state);
    }

    private static Exception Finalizer(Exception __exception, bool __state)
    {
        ManagerEventPatchHelper.End(__state);
        return __exception;
    }
}

[HarmonyPatch(typeof(DialogueManager), nameof(DialogueManager.ExcuteSecondDialogue))]
internal static class DialogueSecondChoiceSyncPatch
{
    private static bool Prefix(out bool __state)
    {
        if (!(ProbeBehaviour.Instance?.AllowDialogueChoice() ?? true))
        {
            __state = false;
            return false;
        }
        return ManagerEventPatchHelper.Begin(
            ManagerDomain.Dialogue, ManagerAction.SecondChoice, out __state);
    }

    private static Exception Finalizer(Exception __exception, bool __state)
    {
        ManagerEventPatchHelper.End(__state);
        return __exception;
    }
}

[HarmonyPatch]
internal static class ScenarioStartAuthorityPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var method in ManagerEventReplicator.GetScenarioStartTargets())
            yield return method;
    }

    private static bool Prefix(
        ScenarioManager __instance, MethodBase __originalMethod, object[] __args,
        out ManagerEventReplicator.ScenarioStartObservation __state)
    {
        var behaviour = ProbeBehaviour.Instance;
        var isBranch = __originalMethod.GetParameters().Length == 3;
        var branch = isBranch ? __args[1] as ScenarioBranchData : null;
        var callbackIndex = isBranch ? 2 : 1;
        var allowed = behaviour?.InterceptScenarioStart(
            (string)__args[0],
            isBranch,
            branch,
            __instance.m_ScenarioArgumentList,
            __args[callbackIndex] as Il2CppSystem.Action<bool>,
            !isBranch && (bool)__args[2],
            !isBranch && (bool)__args[3],
            !isBranch && (bool)__args[4]) ?? true;
        if (allowed && behaviour != null)
            __args[callbackIndex] = behaviour.WrapScenarioFinish(
                (string)__args[0], __args[callbackIndex] as Il2CppSystem.Action<bool>);
        __state = new ManagerEventReplicator.ScenarioStartObservation(
            (string)__args[0],
            __instance.IsPlaying,
            string.IsNullOrEmpty(__instance.CurrentSequenceID)
                ? 0
                : unchecked((int)Protocol.SceneId(__instance.CurrentSequenceID)),
            allowed);
        var requestedKey = string.IsNullOrEmpty(__state.BundleId)
            ? 0
            : unchecked((int)Protocol.SceneId(__state.BundleId));
        if (allowed && requestedKey != 0 &&
            (!__state.WasPlaying || __state.PreviousKey != requestedKey))
            behaviour?.ObserveScenarioStarted(__instance, __state.BundleId);
        return allowed;
    }

    private static void Postfix(
        ScenarioManager __instance, ManagerEventReplicator.ScenarioStartObservation __state)
    {
        var requestedKey = string.IsNullOrEmpty(__state.BundleId)
            ? 0
            : unchecked((int)Protocol.SceneId(__state.BundleId));
        if (__state.Allowed && requestedKey != 0 &&
            (!__state.WasPlaying || __state.PreviousKey != requestedKey))
            ProbeBehaviour.Instance?.ObserveScenarioStarted(__instance, __state.BundleId);
    }
}

[HarmonyPatch(typeof(ScenarioManager), "ProceedScenario", new[] { typeof(DRSequence) })]
internal static class ScenarioNodeAuthorityPatch
{
    private static bool Prefix(ScenarioManager __instance, DRSequence __0)
    {
        return ProbeBehaviour.Instance?.InterceptScenarioNode(__instance, __0) ?? true;
    }
}

[HarmonyPatch]
internal static class ScenarioFinishSyncPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var method in ManagerEventReplicator.GetScenarioFinishTargets())
            yield return method;
    }

    private static bool Prefix(out bool __state)
    {
        __state = ProbeBehaviour.Instance?.AllowScenarioTerminal() ?? true;
        return __state;
    }

    private static void Postfix(MethodBase __originalMethod, bool __state)
    {
        if (__state && __originalMethod.Name == nameof(ScenarioManager.ForceStopScenario))
            ProbeBehaviour.Instance?.ObserveScenarioFinished(false);
    }
}

[HarmonyPatch(typeof(DialogueManager), "set_CurrentBundleID")]
internal static class DialogueStartStatePatch
{
    private static void Postfix(string __0) =>
        ProbeBehaviour.Instance?.ObserveDialogueStarted(__0);
}

[HarmonyPatch]
internal static class DialogueStartAuthorityPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var method in ManagerEventReplicator.GetDialogueStartTargets())
            yield return method;
    }

    private static bool Prefix(MethodBase __originalMethod, object[] __args)
    {
        var behaviour = ProbeBehaviour.Instance;
        var visual = __originalMethod.Name == nameof(DialogueManager.StartVisualNovel);
        var small = __originalMethod.Name == nameof(DialogueManager.StartSmallDialogue);
        var arguments = !visual && !small && __args.Length == 5
            ? __args[1] as Il2CppSystem.Collections.Generic.List<string>
            : null;
        var kind = visual
            ? ManagerEventReplicator.DialogueStartKind.VisualNovel
            : small
                ? ManagerEventReplicator.DialogueStartKind.Small
                : arguments == null
                    ? ManagerEventReplicator.DialogueStartKind.Normal
                    : ManagerEventReplicator.DialogueStartKind.Arguments;
        var callbackIndex = arguments == null ? 1 : 2;
        var allowed = behaviour?.InterceptDialogueStart(
            (string)__args[0],
            kind,
            arguments,
            visual ? __args[1] as Il2CppSystem.Collections.Generic.List<DialogueEntry> : null,
            visual ? __args[2] as ScenarioButtonInfo : null,
            !visual ? __args[callbackIndex] as Il2CppSystem.Action<bool> : null,
            visual ? __args[3] as Il2CppSystem.Action<int> : null,
            visual || (bool)__args[arguments == null ? 2 : 3],
            visual || (bool)__args[arguments == null ? 3 : 4]) ?? true;
        if (allowed && !visual && behaviour != null)
            __args[callbackIndex] = behaviour.WrapDialogueFinish(
                (string)__args[0], __args[callbackIndex] as Il2CppSystem.Action<bool>);
        return allowed;
    }
}

[HarmonyPatch]
internal static class DialogueNodeStatePatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var method in ManagerEventReplicator.GetDialogueNodeTargets())
            yield return method;
    }

    private static void Postfix(DialogueManager __instance, DialogueInfo __0) =>
        ProbeBehaviour.Instance?.ObserveDialogueNode(__instance, __0);
}

[HarmonyPatch(typeof(DialogueManager), "TotalFinishDialogue")]
internal static class DialogueFinishStatePatch
{
    private static void Postfix() => ProbeBehaviour.Instance?.ObserveDialogueFinished();
}

[HarmonyPatch]
internal static class DialogueTerminalAuthorityPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var method in ManagerEventReplicator.GetDialogueTerminalTargets())
            yield return method;
    }

    private static bool Prefix() => ProbeBehaviour.Instance?.AllowDialogueTerminal() ?? true;
}

[HarmonyPatch]
internal static class DialogueForceFinishStatePatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var method in ExistingForceTargets())
            yield return method;
    }

    private static IEnumerable<MethodBase> ExistingForceTargets()
    {
        foreach (var method in ManagerEventReplicator.GetDialogueTerminalTargets())
            if (method.Name is nameof(DialogueManager.ForceFinishDialogue) or
                nameof(DialogueManager.ForceFinishSmallDialogue))
                yield return method;
    }

    private static void Postfix() => ProbeBehaviour.Instance?.ObserveDialogueFinished();
}

[HarmonyPatch(typeof(DialogueManager), "ContinueDialogue")]
internal static class DialogueAutomaticAdvanceAuthorityPatch
{
    private static bool Prefix() => ProbeBehaviour.Instance?.AllowDialogueAdvance() ?? true;
}

[HarmonyPatch(typeof(MiniGame.BettingGameUI_ResultPage), nameof(MiniGame.BettingGameUI_ResultPage.Show),
    new[] { typeof(bool) })]
internal static class BettingResultSyncPatch
{
    private static bool Prefix(bool __0) =>
        ManagerEventPatchHelper.Intercept(
            ManagerDomain.Betting, ManagerAction.Result, __0 ? 1 : 0);
}

[HarmonyPatch(typeof(MiniGame.BattleVIP.VIPCookingManager), "JudgeWinner")]
internal static class VipCookingResultSyncPatch
{
    private static bool Prefix(bool __0) =>
        ManagerEventPatchHelper.Intercept(
            ManagerDomain.VipCooking, ManagerAction.Result, __0 ? 1 : 0);
}

[HarmonyPatch(typeof(JDLC.JungleMiniGameSceneManager), nameof(JDLC.JungleMiniGameSceneManager.OnMiniGameEnd))]
internal static class JungleMiniGameResultSyncPatch
{
    private static bool Prefix(JDLC.JungleMiniGameResultInfo __0) =>
        ManagerEventPatchHelper.Intercept(
            ManagerDomain.JungleMiniGame, ManagerAction.Result, (int)__0.ResultType);
}

[HarmonyPatch(typeof(Karaoke.KaraokeTrackResult), nameof(Karaoke.KaraokeTrackResult.Show))]
internal static class KaraokeResultSyncPatch
{
    private static bool Prefix(int __0) =>
        ManagerEventPatchHelper.Intercept(ManagerDomain.Karaoke, ManagerAction.Result, __0);
}

[HarmonyPatch(typeof(SushiBarTable), nameof(SushiBarTable.SetTrash))]
internal static class SushiTableStateSyncPatch
{
    private static bool Prefix(SushiBarTable __instance, bool __0) =>
        ProbeBehaviour.Instance?.InterceptManagerEvent(
            ManagerDomain.SushiTable,
            ManagerAction.TableState,
            __0 ? 1 : 0,
            ManagerEventPatchHelper.TableContext(__instance)) ?? true;
}

[HarmonyPatch(typeof(TimelineManager), "OnChangePlayState")]
internal static class TimelineStateSyncPatch
{
    private static void Prefix(TimelineManager.TPlayState __0, int __1, bool __2) =>
        ProbeBehaviour.Instance?.ObserveTimeline(__0, __1, __2);
}

[HarmonyPatch]
internal static class TimelinePlayAuthorityPatch
{
    private static MethodBase TargetMethod() => ManagerEventReplicator.GetTimelineTargets()[0];

    private static bool Prefix(
        int __0,
        Il2CppSystem.Action __1,
        Il2CppSystem.Action<bool> __2,
        bool __3,
        Il2CppSystem.Nullable<Vector3> __4) =>
        ProbeBehaviour.Instance?.InterceptTimelinePlay(__0, __1, __2, __3, __4) ?? true;
}

[HarmonyPatch]
internal static class TimelineControllerAuthorityPatch
{
    private static MethodBase TargetMethod() => ManagerEventReplicator.GetTimelineTargets()[1];

    private static bool Prefix(
        TimelineController __0,
        Il2CppSystem.Action __1,
        Il2CppSystem.Action<bool> __2,
        int __3) =>
        ProbeBehaviour.Instance?.InterceptTimelineController(__0, __1, __2, __3) ?? true;
}

[HarmonyPatch]
internal static class TimelineFinishAuthorityPatch
{
    private static MethodBase TargetMethod() => ManagerEventReplicator.GetTimelineTargets()[2];

    private static bool Prefix() =>
        ProbeBehaviour.Instance?.AllowTimelineTerminalControl() ?? true;
}

[HarmonyPatch]
internal static class TimelineSkipAuthorityPatch
{
    private static MethodBase TargetMethod() => ManagerEventReplicator.GetTimelineTargets()[3];

    private static bool Prefix() =>
        ProbeBehaviour.Instance?.AllowTimelineTerminalControl() ?? true;
}

[HarmonyPatch(typeof(InsectBattle.InsectBattleStateManager),
    nameof(InsectBattle.InsectBattleStateManager.ChangeState))]
internal static class InsectBattleStateSyncPatch
{
    private static bool Prefix(InsectBattle.InsectBattleStateManager.InsectBattleState __0) =>
        ManagerEventPatchHelper.Intercept(
            ManagerDomain.InsectBattle, ManagerAction.State, (int)__0);
}

[HarmonyPatch(typeof(MiniGame.SeahorseRace.SeahorseRaceSession),
    nameof(MiniGame.SeahorseRace.SeahorseRaceSession.OnGoal))]
internal static class SeahorseRaceGoalSyncPatch
{
    private static bool Prefix(int __0, float __1) =>
        ProbeBehaviour.Instance?.InterceptManagerEvent(
            ManagerDomain.SeahorseRace,
            ManagerAction.Goal,
            __0,
            BitConverter.SingleToInt32Bits(__1)) ?? true;
}
