using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using DR.GameData;
using DR.Save;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using TMPro;
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
    Scenario = 16,
    Progression = 17,
    Time = 18
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
    PhoneAnswered = 39,
    RewardFirst = 40,
    RewardLast = 68,
    Wallet = 69,
    Unlock = 70,
    SushiPickupRequest = 71,
    SushiPickupResult = 72,
    SushiServeRequest = 73,
    SushiServeResult = 74,
    SushiCleanRequest = 75,
    SushiCleanResult = 76,
    SushiWasabiRequest = 77,
    TimeScale = 78,
    TimeStop = 79,
    TimeReset = 80,
    TutorialStep = 81,
    DialogueVote = 82,
    DialogueChoice = 83
}

internal sealed class ManagerEventReplicator
{
    private const int MaxTimelineGeneration = 0x3fffffff;
    private const int RewardActionBase = (int)ManagerAction.RewardFirst;
    private const int MaxPendingManagerEvents = 256;
    private const int DialogueVoteContinue = 1;
    private const int DialogueVoteSkip = 2;
    private const int DialogueVoteFinish = 3;
    private const int DialogueVoteChoiceBase = 4;

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
    private enum InvocationReplayDecision { Local, Synthesize, Spectate }
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
    private readonly Dictionary<ManagerDomain, uint> _lastBlockedTraceRevisions = new();
    private readonly Dictionary<int, MenuSlotState> _hostMenuSlots = new();
    private readonly int[] _hostWasabi = new int[(int)SushiBar.Place.Max];
    private uint _sushiRevision;
    private uint _clientSushiRevision;
    private SushiResultState? _pendingSushiResult;
    private bool _applying;
    private bool _applyingScenarioAuthority;
    private bool _applyingDialogueAuthority;
    private bool _applyingTimeAuthority;
    private bool _remoteTimeScopeActive;
    private bool _waitingForManagementPanelClose;
    private int _suppressPublish;
    private float _nextSushiScan;
    private float _nextProgressionScan;
    private readonly int[] _hostWallet = new int[6];
    private readonly Dictionary<int, byte> _hostUnlocks = new();
    private int _hostTutorialStep = -1;
    private int _pendingClientTutorialStep = -1;
    private int _clientTutorialAppliedStep = -1;
    private uint _clientTutorialAppliedSceneId;
    private bool _allowClientTutorialActivation;
    private SushiBarOrderQueue.ProgressData _remoteSushiPlate;
    private bool _clientRemoteSushiPlate;
    private bool _wasConnected;
    private bool _storySnapshotPublished;
    private float _nextStoryScan;
    private float _nextStorySafetyKeyframe;
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
    private int _localDialogueBundleKey;
    private int _dialogueVoteBundleKey;
    private int _dialogueVoteScope = -1;
    private int _committedDialogueBundleKey;
    private int _committedDialogueScope = -1;
    private int _hostDialogueVote;
    private int _clientDialogueVote;
    private TextMeshProUGUI _dialogueVoteLabel;
    private Transform _dialogueVoteLabelParent;
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
    private ManagerInvocationDescriptor? _pendingHostScenarioInvocation;
    private ManagerInvocationDescriptor? _hostScenarioInvocation;
    private ManagerInvocationDescriptor? _pendingHostDialogueInvocation;
    private ManagerInvocationDescriptor? _hostDialogueInvocation;
    private ManagerInvocationDescriptor? _pendingHostTimelineInvocation;
    private ManagerInvocationDescriptor? _hostTimelineInvocation;

    internal ManagerEventReplicator(ManualLogSource log)
    {
        _log = log;
    }

    internal static void SelfTest()
    {
        if (!IsLocalTimeScope(TimeScaleController.Type.PauseMenumAuto) ||
            IsLocalTimeScope(TimeScaleController.Type.InGameQTE) ||
            !IsActivityTerminal(ManagerDomain.Karaoke, ManagerAction.Result) ||
            IsActivityTerminal(ManagerDomain.Dialogue, ManagerAction.Result))
            throw new InvalidOperationException("Manager time/activity policy self-test failed");
        if (!IsLocalDialogueBundle("LobbyTalkCobra_001") ||
            IsLocalDialogueBundle("Tutorial_Mission06") ||
            IsLocalDialogueBundle(null))
            throw new InvalidOperationException("Local dialogue policy self-test failed");
        if (!HasManagerEventCapacity(MaxPendingManagerEvents - 1) ||
            HasManagerEventCapacity(MaxPendingManagerEvents))
            throw new InvalidOperationException("Manager event queue capacity self-test failed");
        if (!ShouldForceSceneEntryKeyframe(SessionRole.Host, true, 2f, 2f) ||
            ShouldForceSceneEntryKeyframe(SessionRole.Client, true, 2f, 2f) ||
            ShouldForceSceneEntryKeyframe(SessionRole.Host, false, 2f, 3f) ||
            ShouldForceSceneEntryKeyframe(SessionRole.Host, true, 0f, 3f) ||
            ShouldForceSceneEntryKeyframe(SessionRole.Host, true, 3f, 2f))
            throw new InvalidOperationException("Manager scene keyframe gate self-test failed");
        if (!ShouldForceStorySafetyKeyframe(true, true, 5f, 5f) ||
            ShouldForceStorySafetyKeyframe(false, true, 5f, 6f) ||
            ShouldForceStorySafetyKeyframe(true, false, 5f, 6f) ||
            ShouldForceStorySafetyKeyframe(true, true, 0f, 6f) ||
            ShouldForceStorySafetyKeyframe(true, true, 6f, 5f))
            throw new InvalidOperationException("Manager story safety keyframe gate self-test failed");
        if (!ShouldScheduleReconnectKeyframe(SessionRole.Host, false, true) ||
            ShouldScheduleReconnectKeyframe(SessionRole.Host, true, true) ||
            ShouldScheduleReconnectKeyframe(SessionRole.Host, false, false) ||
            ShouldScheduleReconnectKeyframe(SessionRole.Client, false, true))
            throw new InvalidOperationException("Manager reconnect keyframe gate self-test failed");
        if (!ShouldForceScenarioMilestoneKeyframe(
                false, SessionRole.Host, true, 17, 17) ||
            ShouldForceScenarioMilestoneKeyframe(
                true, SessionRole.Host, true, 17, 17) ||
            ShouldForceScenarioMilestoneKeyframe(
                false, SessionRole.Client, true, 17, 17) ||
            ShouldForceScenarioMilestoneKeyframe(
                false, SessionRole.Host, false, 17, 17) ||
            ShouldForceScenarioMilestoneKeyframe(
                false, SessionRole.Host, true, 0, 0) ||
            ShouldForceScenarioMilestoneKeyframe(
                false, SessionRole.Host, true, 17, 18))
            throw new InvalidOperationException("Manager scenario keyframe gate self-test failed");
        if (ShouldAllowNativeTutorialActivation(SessionRole.Client, true, false) ||
            !ShouldAllowNativeTutorialActivation(SessionRole.Client, true, true) ||
            !ShouldAllowNativeTutorialActivation(SessionRole.Client, false, false) ||
            !ShouldAllowNativeTutorialActivation(SessionRole.Host, true, false))
            throw new InvalidOperationException("Tutorial activation authority self-test failed");
        if (!ShouldCompleteTutorialDialogue(17, 17, true) ||
            ShouldCompleteTutorialDialogue(17, 18, true) ||
            ShouldCompleteTutorialDialogue(17, 17, false))
            throw new InvalidOperationException("Tutorial dialogue completion self-test failed");
        if (!ShouldReleaseTutorialPresentation(17, 17, "other") ||
            !ShouldReleaseTutorialPresentation(17, 18, "Tutorial_Test") ||
            ShouldReleaseTutorialPresentation(17, 18, "Story_Test"))
            throw new InvalidOperationException("Tutorial presentation release self-test failed");
        if (!ShouldDeferTutorialReplay(17) || ShouldDeferTutorialReplay(0))
            throw new InvalidOperationException("Tutorial dialogue ordering self-test failed");
        if (!ShouldWaitForManagementPanelClose(true, false) ||
            !ShouldWaitForManagementPanelClose(false, true) ||
            ShouldWaitForManagementPanelClose(false, false))
            throw new InvalidOperationException("Management panel close gate self-test failed");
        if (!CanReleaseClosedManagementPanel(true, false, false) ||
            CanReleaseClosedManagementPanel(false, false, false) ||
            CanReleaseClosedManagementPanel(true, true, false) ||
            CanReleaseClosedManagementPanel(true, false, true))
            throw new InvalidOperationException("Management panel release self-test failed");
        if (!ShouldConsumeUnavailableProgression(ManagerAction.Unlock) ||
            !ShouldConsumeUnavailableProgression(ManagerAction.TutorialStep) ||
            ShouldConsumeUnavailableProgression(ManagerAction.RewardFirst))
            throw new InvalidOperationException(
                "Unavailable progression consumption self-test failed");
        var outboundOverflow = new ManagerEventReplicator(null);
        var outboundSession = new UdpSession(null);
        for (var index = 0; index < MaxPendingManagerEvents; index++)
            outboundOverflow._outboundEvents.Enqueue(default);
        if (outboundOverflow.TryQueueOutbound(outboundSession, default) ||
            outboundOverflow._outboundEvents.Count != 0)
            throw new InvalidOperationException("Manager outbound queue overflow self-test failed");

        var pendingOverflow = new ManagerEventReplicator(null);
        var pendingSession = new UdpSession(null);
        for (var revision = 1; revision <= MaxPendingManagerEvents; revision++)
            if (!pendingOverflow.TryQueuePendingHostEvent(
                    pendingSession, ManagerDomain.Story,
                    new ManagerEvent((uint)revision, 0, 0, (byte)ManagerDomain.Story, 0, 0, 0)))
                throw new InvalidOperationException("Manager pending queue setup self-test failed");
        if (pendingOverflow.TryQueuePendingHostEvent(
                pendingSession, ManagerDomain.Story,
                new ManagerEvent(unchecked((uint)MaxPendingManagerEvents + 1u), 0, 0,
                    (byte)ManagerDomain.Story, 0, 0, 0)) ||
            pendingOverflow._pendingHostEvents.Count != 0)
            throw new InvalidOperationException("Manager pending queue overflow self-test failed");

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
        var synthesizedScenario = CreateScenarioStart(new ManagerInvocationDescriptor(
            ManagerInvocationKind.Scenario, "z", new string[] { "a", null, string.Empty },
            true, false, true, true, false, 0f, 0f, 0f));
        var synthesizedNullDialogue = CreateDialogueStart(new ManagerInvocationDescriptor(
            ManagerInvocationKind.DialogueArguments, "dialogue/replay", null,
            false, true, false, true, false, 0f, 0f, 0f));
        var synthesizedEmptyDialogue = CreateDialogueStart(new ManagerInvocationDescriptor(
            ManagerInvocationKind.DialogueArguments, "dialogue/replay", Array.Empty<string>(),
            true, false, false, true, false, 0f, 0f, 0f));
        var rewardContext = PackRewardContext(17, RewardShowType.SilentReward);
        var sushiTarget = PackSushiTarget(SushiBar.Place.Branch, 7);
        var dialogueScope = PackDialogueScope(17, 3);
        var dialogueVotes = PackDialogueVotes(
            dialogueScope, DialogueVoteChoiceBase + 2, DialogueVoteSkip);
        var dialogueChoice = PackDialogueChoice(dialogueScope, 2);
        var dialogueVoteText = FormatDialogueVoteText("Choice", 1);
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
            !ShouldPrimeSushiRuntimeSnapshot(SessionRole.Host, true, false) ||
            ShouldPrimeSushiRuntimeSnapshot(SessionRole.Host, true, true) ||
            ShouldPrimeSushiRuntimeSnapshot(SessionRole.Client, true, false) ||
            ShouldPrimeSushiRuntimeSnapshot(SessionRole.Host, false, false) ||
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
            ScenarioPreviousKey(false, () => throw new NullReferenceException()) != 0 ||
            ScenarioPreviousKey(true, () => throw new NullReferenceException()) != 0 ||
            ScenarioPreviousKey(true, () => "scenario-a") != ContentKey("scenario-a") ||
             synthesizedScenario.Kind != ScenarioStartKind.Normal ||
             synthesizedScenario.Arguments == null || synthesizedScenario.Arguments.Count != 3 ||
             synthesizedScenario.Arguments[0] != "a" ||
             synthesizedScenario.Arguments[1] != null ||
             synthesizedScenario.Arguments[2] != string.Empty ||
             !synthesizedScenario.UseButton || !synthesizedScenario.IgnorePlaying ||
             synthesizedNullDialogue.Kind != DialogueStartKind.Arguments ||
             synthesizedNullDialogue.Arguments != null ||
             synthesizedEmptyDialogue.Arguments == null ||
             synthesizedEmptyDialogue.Arguments.Count != 0 ||
             DecideInvocationReplay(true, true) != InvocationReplayDecision.Local ||
             DecideInvocationReplay(false, true) != InvocationReplayDecision.Synthesize ||
             DecideInvocationReplay(false, false) != InvocationReplayDecision.Spectate ||
             ProgressionRewardAction(CommonRewardType.Gold) != 40 ||
             !TryUnpackRewardContext(
                 rewardContext, out var rewardCount, out var rewardShowType) ||
             rewardCount != 17 || rewardShowType != RewardShowType.SilentReward ||
             !IsWalletDebitValid(GoodsType.gold, -100, 100) ||
             IsWalletDebitValid(GoodsType.none, -100, 100) ||
             IsWalletDebitValid(GoodsType.gold, 1, 100) ||
             IsWalletDebitValid(GoodsType.gold, -101, 100) ||
             !IsValidTutorialStep((int)TutorialStep.Open_Sushi_Ingredient) ||
             IsValidTutorialStep(-1) ||
             IsValidTutorialStep((int)TutorialStep.AllDone + 1) ||
             !ShouldQueueTutorialReplay(12, -1, 0, 7) ||
             ShouldQueueTutorialReplay(12, 12, 7, 7) ||
             !TryUnpackSushiTarget(
                 sushiTarget, out var sushiPlace, out var sushiTable) ||
             sushiPlace != SushiBar.Place.Branch || sushiTable != 7 ||
             TryUnpackSushiTarget(-1, out _, out _) ||
             !TryUnpackDialogueVotes(
                 dialogueVotes, out var voteScope, out var hostVote, out var clientVote) ||
             DialogueNodeFromScope(voteScope) != 17 ||
             DialogueMessageFromScope(voteScope) != 3 ||
             hostVote != DialogueVoteChoiceBase + 2 ||
             clientVote != DialogueVoteSkip ||
             !TryUnpackDialogueChoice(
                 dialogueChoice, out var choiceScope, out var choiceIndex) ||
             choiceScope != dialogueScope || choiceIndex != 2 ||
             !DialogueVotesMatch(DialogueVoteContinue, DialogueVoteContinue) ||
             DialogueVotesMatch(DialogueVoteContinue, DialogueVoteSkip) ||
             DialogueVotesMatch(0, 0) ||
             StripDialogueVoteText(dialogueVoteText) != "Choice" ||
             FormatDialogueVoteText(dialogueVoteText, 2) != "Choice  2/2" ||
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
            if (ShouldPrimeSushiRuntimeSnapshot(role, connected, _wasConnected))
            {
                _hostMenuSlots.Clear();
                Array.Fill(_hostWasabi, -1);
                _nextSushiScan = 0f;
            }
            Array.Fill(_hostWallet, -1);
            _hostUnlocks.Clear();
            _hostTutorialStep = -1;
            _nextProgressionScan = 0f;
            _nextDayScan = 0f;
            _nextStoryScan = 0f;
            _nextStorySafetyKeyframe = 0f;
            _activeSessionSnapshotPublished = false;
        }
        _wasConnected = true;
        FlushOutbound(session);

        if (role == SessionRole.Host)
        {
            TryAcceptScenarioStart(ScenarioManager.Instance, session);
            ObserveLiveHostDialogue(session);
        }

        if (role == SessionRole.Host && !_activeSessionSnapshotPublished)
        {
            PublishActiveSessionSnapshot(session);
            _activeSessionSnapshotPublished = true;
        }

        if (role == SessionRole.Host && !_storySnapshotPublished)
        {
            _storySnapshotPublished = PublishStorySnapshot(session);
            if (_storySnapshotPublished)
            {
                _nextStoryScan = now + 1f;
                _nextStorySafetyKeyframe = now + 5f;
            }
        }
        if (role == SessionRole.Host && _storySnapshotPublished && now >= _nextStoryScan)
        {
            _nextStoryScan = now + 1f;
            PublishStoryChanges(session);
        }
        if (ShouldForceStorySafetyKeyframe(
                role == SessionRole.Host && session.Connected,
                _storySnapshotPublished,
                _nextStorySafetyKeyframe,
                now))
        {
            ForceHostStoryKeyframe();
            _nextStorySafetyKeyframe = now + 5f;
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
        if (role == SessionRole.Host && now >= _nextProgressionScan)
        {
            _nextProgressionScan = now + 0.5f;
            PublishProgressionChanges(session);
        }
        while (session.TryTakeManagerEvent(out var state))
        {
            if (role == SessionRole.Host)
                ApplyClientRequest(session, state);
            else if (role == SessionRole.Client)
            {
                var lane = LaneOf((ManagerDomain)state.Domain);
                var revision = GetRevision(_clientManagerRevisions, lane);
                TraceManagerEvent(
                    "receive", state, lane, revision,
                    _pendingHostEvents.TryGetValue(lane, out var receivedPending)
                        ? receivedPending.Count
                        : 0);
                if (!IsNewer(state.Revision, revision))
                {
                    TraceManagerEvent("drop-stale", state, lane, revision, 0);
                    continue;
                }
                if (!_hasHostClockOffset)
                {
                    _hostClockOffset = unchecked(CurrentTick() - state.HostTick);
                    _hasHostClockOffset = true;
                }
                if (!TryQueuePendingHostEvent(session, lane, state))
                    return;
                if ((ManagerDomain)state.Domain == ManagerDomain.Progression &&
                    (ManagerAction)state.Action == ManagerAction.TutorialStep &&
                    IsValidTutorialStep(state.Value) &&
                    (TutorialManager.Instance == null || ShouldQueueTutorialReplay(
                        state.Value, _clientTutorialAppliedStep,
                        _clientTutorialAppliedSceneId, sceneId)))
                    _pendingClientTutorialStep = state.Value;
            }
        }
        if (role == SessionRole.Client)
        {
            TryApplyPendingTutorial(sceneId);
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

    private static bool ShouldPrimeSushiRuntimeSnapshot(
        SessionRole role, bool connected, bool wasConnected) =>
        role == SessionRole.Host && connected && !wasConnected;

    internal bool InterceptTimeScale(
        SessionRole role,
        UdpSession session,
        TimeScaleController.Type type,
        float scale)
    {
        if (_applyingTimeAuthority || _localDialogueBundleKey != 0 || IsLocalTimeScope(type) ||
            session == null || !session.Connected)
            return true;
        if (role != SessionRole.Host)
        {
            _log?.LogInfo(
                $"Time authority intercept: role={role}; action=Scale; type={type}; " +
                $"scale={scale}; allowed={role != SessionRole.Client}; remote={_remoteTimeScopeActive}");
            return role != SessionRole.Client;
        }
        _log?.LogInfo(
            $"Time authority publish: action=Scale; type={type}; scale={scale}; " +
            $"unityScale={Time.timeScale}");
        Publish(session, ManagerDomain.Time, ManagerAction.TimeScale,
            (int)type, BitConverter.SingleToInt32Bits(scale));
        return true;
    }

    internal bool InterceptTimeStop(
        SessionRole role,
        UdpSession session,
        bool isGamePause)
    {
        if (_applyingTimeAuthority || _localDialogueBundleKey != 0 || isGamePause ||
            session == null || !session.Connected)
            return true;
        if (role != SessionRole.Host)
        {
            _log?.LogInfo(
                $"Time authority intercept: role={role}; action=Stop; " +
                $"allowed={role != SessionRole.Client}; remote={_remoteTimeScopeActive}");
            return role != SessionRole.Client;
        }
        _log?.LogInfo($"Time authority publish: action=Stop; unityScale={Time.timeScale}");
        Publish(session, ManagerDomain.Time, ManagerAction.TimeStop, 0, 0);
        return true;
    }

    internal bool InterceptTimeReset(SessionRole role, UdpSession session)
    {
        if (_applyingTimeAuthority || _localDialogueBundleKey != 0 ||
            session == null || !session.Connected)
            return true;
        if (role == SessionRole.Client && !_remoteTimeScopeActive)
            return true;
        if (role != SessionRole.Host)
        {
            _log?.LogInfo(
                $"Time authority intercept: role={role}; action=Reset; " +
                $"allowed={role != SessionRole.Client}; remote={_remoteTimeScopeActive}");
            return role != SessionRole.Client;
        }
        _log?.LogInfo($"Time authority publish: action=Reset; unityScale={Time.timeScale}");
        Publish(session, ManagerDomain.Time, ManagerAction.TimeReset, 0, 0);
        return true;
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
        if (domain == ManagerDomain.Dialogue &&
            IsLocalDialogueBundle(DialogueManager.Instance?.CurrentBundleID))
            return true;
        if (domain == ManagerDomain.Dialogue &&
            action is ManagerAction.Continue or ManagerAction.Skip or
                ManagerAction.FirstChoice or ManagerAction.SecondChoice or
                ManagerAction.DialogueChoice)
            return InterceptDialogueAction(role, session, action, value, context);
        if (AllowsLeasedDialogueControl(
                role, session.Connected,
                ProbeBehaviour.Instance?.HasActiveNpcLease == true, action))
            return true;
        if (role == SessionRole.Host)
        {
            Publish(session, domain, action, value, context);
            if (IsActivityTerminal(domain, action))
                ForceHostKeyframe();
            return true;
        }
        if (role == SessionRole.Client && domain == ManagerDomain.Dialogue)
        {
            TryQueueOutbound(session, new ManagerEvent(
                0, 0, CurrentTick(), (byte)domain, (byte)action, value, context));
            FlushOutbound(session);
        }
        return role != SessionRole.Client;
    }

    private bool InterceptDialogueAction(
        SessionRole role,
        UdpSession session,
        ManagerAction action,
        int bundleKey,
        int context)
    {
        var manager = DialogueManager.Instance;
        var liveBundleKey = ContentKey(manager?.CurrentBundleID);
        var nodeIndex = manager?.m_CurrentDialogueIndex ?? -1;
        var voteScope = DialogueVoteScope(manager);
        if (manager?.IsPlaying != true || bundleKey == 0 || bundleKey != liveBundleKey ||
            nodeIndex < 0 || voteScope < 0 ||
            IsCommittedDialogueScope(
                bundleKey, voteScope, _committedDialogueBundleKey, _committedDialogueScope))
        {
            _log?.LogWarning(
                $"Dialogue vote rejected: role={role}; action={action}; " +
                $"requested={bundleKey:X8}/{context}; live={liveBundleKey:X8}/{voteScope}; " +
                $"playing={manager?.IsPlaying == true}");
            return false;
        }
        if (action == ManagerAction.Continue && !IsDialoguePanelReady(manager))
            return true;

        var vote = action switch
        {
            ManagerAction.Continue => IsFinalDialogueNode(manager)
                ? DialogueVoteFinish
                : DialogueVoteContinue,
            ManagerAction.Skip => DialogueVoteSkip,
            ManagerAction.FirstChoice => DialogueVoteChoiceBase,
            ManagerAction.SecondChoice => DialogueVoteChoiceBase + 1,
            ManagerAction.DialogueChoice when TryUnpackDialogueChoice(
                context, out var requestedScope, out var choiceIndex) &&
                requestedScope == voteScope =>
                DialogueVoteChoiceBase + choiceIndex,
            _ => 0
        };
        if (!IsDialogueVoteValid(manager, vote))
        {
            _log?.LogWarning(
                $"Dialogue vote invalid: role={role}; bundle={bundleKey:X8}; " +
                $"scope={voteScope}; vote={vote}");
            return false;
        }

        SetDialogueVoteScope(bundleKey, voteScope);
        if (role == SessionRole.Host)
        {
            if (_hostDialogueVote == vote)
                return false;
            _hostDialogueVote = vote;
            _log?.LogInfo(
                $"Dialogue vote cast: role={role}; bundle={bundleKey:X8}; node={nodeIndex}; " +
                $"msg={DialogueMessageFromScope(voteScope)}; vote={vote}; ready={DialogueVoteCount(vote)}/2");
            PublishDialogueVotes(session);
            TryCommitDialogueVotes(session, manager);
        }
        else if (role == SessionRole.Client)
        {
            if (_clientDialogueVote == vote)
                return false;
            _clientDialogueVote = vote;
            _log?.LogInfo(
                $"Dialogue vote cast: role={role}; bundle={bundleKey:X8}; node={nodeIndex}; " +
                $"msg={DialogueMessageFromScope(voteScope)}; vote={vote}; ready={DialogueVoteCount(vote)}/2");
            TryQueueOutbound(session, new ManagerEvent(
                0, 0, CurrentTick(), (byte)ManagerDomain.Dialogue,
                (byte)ManagerAction.DialogueVote, bundleKey,
                PackDialogueVotes(voteScope, 0, vote)));
            FlushOutbound(session);
        }
        return false;
    }

    internal void ObserveReward(SessionRole role, UdpSession session, Reward reward)
    {
        if (_applying || role != SessionRole.Host || session?.Connected != true || reward == null)
            return;
        var action = ProgressionRewardAction(reward.Type);
        var context = PackRewardContext(reward.Count, reward.ShowType);
        if (action is < (int)ManagerAction.RewardFirst or > (int)ManagerAction.RewardLast ||
            reward.Value < 0 || reward.Count is <= 0 or > 1_000_000 ||
            !TryUnpackRewardContext(context, out _, out _))
            return;
        Publish(session, ManagerDomain.Progression, (ManagerAction)action, reward.Value, context);
    }

    internal bool InterceptPlayerGoods(
        SessionRole role, UdpSession session, GoodsType type, int value)
    {
        if (_applying || role != SessionRole.Client || session?.Connected != true)
            return true;
        if (value < 0 && type is >= GoodsType.gold and <= GoodsType.fakePoint)
        {
            TryQueueOutbound(session, new ManagerEvent(
                0, 0, CurrentTick(), (byte)ManagerDomain.Progression,
                (byte)ManagerAction.Wallet, (int)type, value));
            FlushOutbound(session);
            return true;
        }
        return false;
    }

    internal bool InterceptSushiInteraction(
        SessionRole role, UdpSession session, StaffDave staff, SushiBarInteraction interaction)
    {
        if (_applying || role != SessionRole.Client || session?.Connected != true ||
            interaction != SushiBarInteraction.Serve)
            return true;
        if (!_clientRemoteSushiPlate)
        {
            QueueClientRequest(session, ManagerDomain.SushiMenu,
                ManagerAction.SushiPickupRequest, (int)SushiBar.Place.Main, 0);
            return false;
        }
        var customer = FindNearestCustomer(staff?.transform.position ?? default);
        if (customer != null)
            QueueClientRequest(session, ManagerDomain.SushiMenu,
                ManagerAction.SushiServeRequest, 0,
                PackSushiTarget(customer.PlaceTag, customer.SeatNumber));
        return false;
    }

    internal bool InterceptSushiClean(
        SessionRole role, UdpSession session, SushiBarTrashTrigger trigger, int gold)
    {
        if (_applying || role != SessionRole.Client || session?.Connected != true)
            return true;
        if (trigger?.Target != null && TryFindTablePlace(trigger.Target, out var place))
            QueueClientRequest(session, ManagerDomain.SushiTable,
                ManagerAction.SushiCleanRequest, gold,
                PackSushiTarget(place, trigger.TableNumber));
        return false;
    }

    internal bool InterceptSushiWasabi(
        SessionRole role, UdpSession session, SushiBar.Place place, int count)
    {
        if (_applying || role != SessionRole.Client || session?.Connected != true)
            return true;
        if (count > 0)
            QueueClientRequest(session, ManagerDomain.SushiWasabi,
                ManagerAction.SushiWasabiRequest, (int)place, count);
        return false;
    }

    private void QueueClientRequest(
        UdpSession session, ManagerDomain domain, ManagerAction action, int value, int context)
    {
        TryQueueOutbound(session, new ManagerEvent(
            0, _sceneId, CurrentTick(), (byte)domain, (byte)action, value, context));
        FlushOutbound(session);
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
            var hasCustomPosition = customPos.HasValue;
            var position = hasCustomPosition ? customPos.Value : default;
            var invocation = new ManagerInvocationDescriptor(
                ManagerInvocationKind.TimelineByTid, null, null,
                false, false, false, applyOffset, hasCustomPosition,
                position.x, position.y, position.z);
            _pendingHostTimelineInvocation = Protocol.IsValidManagerInvocation(
                (byte)ManagerDomain.Timeline, (byte)ManagerAction.TimelineStart,
                tid, invocation) ? invocation : null;
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
            _pendingHostTimelineInvocation = null;
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
            {
                _hostTimelineRouteTid = 0;
                _pendingHostTimelineInvocation = null;
            }
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
            _hostTimelineInvocation = _hostTimelineActiveRoute == TimelineStartRoute.ByTid &&
                _hostTimelineRouteTid == tid
                ? _pendingHostTimelineInvocation
                : null;
            _pendingHostTimelineInvocation = null;
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
                tid, context, start ? _hostTimelineInvocation : null);
        if (!start)
            _hostTimelineInvocation = null;
    }

    internal bool AllowTimelineTerminalControl(SessionRole role, UdpSession session) =>
        _applying || role != SessionRole.Client || session?.Connected != true;

    internal bool AllowScenarioControl(SessionRole role, UdpSession session) =>
        _applying || ProbeBehaviour.Instance?.HasActiveNpcLease == true ||
        role != SessionRole.Client || session == null || !session.Connected;

    internal bool AllowDialogueAdvance(SessionRole role, UdpSession session) =>
        _applying || IsLocalDialogueBundle(DialogueManager.Instance?.CurrentBundleID) ||
        ProbeBehaviour.Instance?.HasActiveNpcLease == true ||
        role != SessionRole.Client || session == null || !session.Connected;

    internal bool AllowDialogueChoice(SessionRole role, UdpSession session) =>
        _applying || IsLocalDialogueBundle(DialogueManager.Instance?.CurrentBundleID) ||
        role != SessionRole.Client || session == null || !session.Connected;

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
            TryQueueOutbound(session, new ManagerEvent(
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
        IsLocalDialogueBundle(DialogueManager.Instance?.CurrentBundleID) ||
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
        if (!_applying && role == SessionRole.Host)
        {
            _pendingHostScenarioInvocation = null;
            if (!isBranch)
            {
                var invocation = new ManagerInvocationDescriptor(
                    ManagerInvocationKind.Scenario, bundleId, SnapshotArguments(arguments),
                    useButton, showCurtain, ignorePlaying, true, false, 0f, 0f, 0f);
                var invocationKey = ContentKey(bundleId);
                if (Protocol.IsValidManagerInvocation(
                        (byte)ManagerDomain.Scenario,
                        (byte)ManagerAction.ScenarioStarted, invocationKey, invocation))
                    _pendingHostScenarioInvocation = invocation;
            }
        }
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
        if (IsLocalDialogueBundle(bundleId))
        {
            _localDialogueBundleKey = ContentKey(bundleId);
            _pendingHostDialogueInvocation = null;
            _log?.LogInfo(
                $"Dialogue kept local: role={role}; bundle={_localDialogueBundleKey:X8}; " +
                $"id={bundleId}");
            return true;
        }
        _localDialogueBundleKey = 0;
        if (!_applying && role == SessionRole.Host)
        {
            _pendingHostDialogueInvocation = null;
            var invocationKind = kind switch
            {
                DialogueStartKind.Normal => ManagerInvocationKind.DialogueNormal,
                DialogueStartKind.Arguments => ManagerInvocationKind.DialogueArguments,
                DialogueStartKind.Small => ManagerInvocationKind.DialogueSmall,
                _ => (ManagerInvocationKind)0
            };
            if (invocationKind != 0)
            {
                var invocation = new ManagerInvocationDescriptor(
                    invocationKind, bundleId,
                    kind == DialogueStartKind.Arguments ? SnapshotArguments(arguments) : null,
                    useButton, showCurtain, false, true, false, 0f, 0f, 0f);
                var invocationKey = ContentKey(bundleId);
                if (Protocol.IsValidManagerInvocation(
                        (byte)ManagerDomain.Dialogue,
                        (byte)ManagerAction.DialogueStarted, invocationKey, invocation))
                    _pendingHostDialogueInvocation = invocation;
            }
        }
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
        _hostScenarioInvocation = _pendingHostScenarioInvocation is { } invocation &&
            ContentKey(invocation.BundleId) == key ? invocation : null;
        _pendingHostScenarioInvocation = null;
        if (session?.Connected == true)
            Publish(session, ManagerDomain.Scenario, ManagerAction.ScenarioStarted,
                _hostScenarioBundleKey, 0, _hostScenarioInvocation);
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
        if (!ShouldForceScenarioMilestoneKeyframe(
                _applying, role, session?.Connected == true,
                bundleKey, _hostScenarioBundleKey))
            return;
        Publish(session, ManagerDomain.Scenario, ManagerAction.ScenarioFinished,
            bundleKey, result ? 1 : 0);
        _hostScenarioBundleId = string.Empty;
        _hostScenarioBundleKey = 0;
        _hostScenarioNodeId = -1;
        _hostScenarioInvocation = null;
        ForceHostKeyframe();
    }

    internal void ObserveScenarioFinished(SessionRole role, UdpSession session) =>
        ObserveScenarioFinished(role, session, _hostScenarioBundleKey, true);

    internal void ObserveScenarioFinished(
        SessionRole role, UdpSession session, bool result) =>
        ObserveScenarioFinished(role, session, _hostScenarioBundleKey, result);

    internal void ObserveDialogueStarted(
        SessionRole role, UdpSession session, string bundleId)
    {
        if (_applying || string.IsNullOrEmpty(bundleId))
            return;
        var key = ContentKey(bundleId);
        if (IsLocalDialogueBundle(bundleId))
        {
            _localDialogueBundleKey = key;
            _pendingHostDialogueInvocation = null;
            return;
        }
        if (role != SessionRole.Host)
            return;
        if (_hostDialogueBundleKey == key)
            return;
        _hostDialogueBundleKey = key;
        _hostDialogueIndex = -1;
        _hostDialogueInvocation = _pendingHostDialogueInvocation is { } invocation &&
            ContentKey(invocation.BundleId) == key ? invocation : null;
        _pendingHostDialogueInvocation = null;
        _hostDialogueInvocation ??= CreateLiveDialogueInvocation(
            DialogueManager.Instance, bundleId);
        ResetDialogueVotes();
        ResetDialogueCommit();
        _log?.LogInfo(
            $"Dialogue host start: bundle={key:X8}; id={bundleId}; " +
            $"invocation={_hostDialogueInvocation?.Kind.ToString() ?? "none"}");
        if (session?.Connected == true)
            Publish(session, ManagerDomain.Dialogue, ManagerAction.DialogueStarted,
                key, 0, _hostDialogueInvocation);
    }

    internal void ObserveDialogueNode(
        SessionRole role, UdpSession session, DialogueManager manager, DialogueInfo info)
    {
        if (_applying || role != SessionRole.Host || manager == null || info == null)
            return;
        if (IsLocalDialogueBundle(manager.CurrentBundleID))
            return;
        var key = ContentKey(manager.CurrentBundleID);
        var index = manager.m_CurrentDialogueIndex;
        if (key == 0 || index < 0)
            return;
        if (_hostDialogueBundleKey != key)
            ObserveDialogueStarted(role, session, manager.CurrentBundleID);
        if (_hostDialogueBundleKey != key || _hostDialogueIndex == index)
            return;
        _hostDialogueBundleKey = key;
        _hostDialogueIndex = index;
        ResetDialogueVotes();
        _log?.LogInfo($"Dialogue host node: bundle={key:X8}; node={index}");
        if (session?.Connected == true)
            Publish(session, ManagerDomain.Dialogue, ManagerAction.DialogueNode, key, index);
    }

    internal void ObserveDialogueFinished(
        SessionRole role, UdpSession session, int bundleKey, bool result)
    {
        if (_localDialogueBundleKey != 0 && bundleKey == _localDialogueBundleKey)
        {
            _log?.LogInfo(
                $"Local dialogue finished: role={role}; bundle={bundleKey:X8}; result={result}");
            _localDialogueBundleKey = 0;
            return;
        }
        if (_applying || role != SessionRole.Host || bundleKey == 0 ||
            bundleKey != _hostDialogueBundleKey)
            return;
        if (session?.Connected == true)
            Publish(session, ManagerDomain.Dialogue, ManagerAction.DialogueFinished,
                bundleKey, result ? 1 : 0);
        _hostDialogueBundleKey = 0;
        _hostDialogueIndex = -1;
        _hostDialogueInvocation = null;
        ResetDialogueVotes();
        ResetDialogueCommit();
        _log?.LogInfo($"Dialogue host finish: bundle={bundleKey:X8}; result={result}");
    }

    internal void ObserveDialogueFinished(SessionRole role, UdpSession session)
    {
        if (_localDialogueBundleKey != 0)
        {
            _log?.LogInfo(
                $"Local dialogue finished: role={role}; bundle={_localDialogueBundleKey:X8}");
            _localDialogueBundleKey = 0;
            return;
        }
        ObserveDialogueFinished(role, session, _hostDialogueBundleKey, true);
    }

    private void ObserveLiveHostDialogue(UdpSession session)
    {
        var manager = DialogueManager.Instance;
        var bundleId = manager?.CurrentBundleID;
        var bundleKey = ContentKey(bundleId);
        if (bundleKey != 0)
        {
            if (IsLocalDialogueBundle(bundleId))
                return;
            if (_hostDialogueBundleKey != bundleKey)
                ObserveDialogueStarted(SessionRole.Host, session, bundleId);
            var index = manager?.m_CurrentDialogueIndex ?? -1;
            if (manager?.IsPlaying == true && index >= 0 && index != _hostDialogueIndex)
            {
                _hostDialogueIndex = index;
                ResetDialogueVotes();
                _log?.LogInfo($"Dialogue host poll node: bundle={bundleKey:X8}; node={index}");
                Publish(session, ManagerDomain.Dialogue, ManagerAction.DialogueNode,
                    bundleKey, index);
            }
            return;
        }
        if (_hostDialogueBundleKey != 0)
            ObserveDialogueFinished(
                SessionRole.Host, session, _hostDialogueBundleKey, true);
    }

    private static ManagerInvocationDescriptor? CreateLiveDialogueInvocation(
        DialogueManager manager, string bundleId)
    {
        if (manager == null || string.IsNullOrEmpty(bundleId))
            return null;
        var descriptor = new ManagerInvocationDescriptor(
            manager.IsPlayingSmall
                ? ManagerInvocationKind.DialogueSmall
                : ManagerInvocationKind.DialogueNormal,
            bundleId, null, manager.m_UseButton,
            manager.backCurtain?.activeSelf == true,
            false, true, false, 0f, 0f, 0f);
        return Protocol.IsValidManagerInvocation(
            (byte)ManagerDomain.Dialogue, (byte)ManagerAction.DialogueStarted,
            ContentKey(bundleId), descriptor) ? descriptor : null;
    }

    private void PublishDialogueVotes(UdpSession session) =>
        Publish(session, ManagerDomain.Dialogue, ManagerAction.DialogueVote,
            _dialogueVoteBundleKey,
            PackDialogueVotes(
                _dialogueVoteScope, _hostDialogueVote, _clientDialogueVote));

    private void TryCommitDialogueVotes(UdpSession session, DialogueManager manager)
    {
        if (!DialogueVotesMatch(_hostDialogueVote, _clientDialogueVote))
            return;
        var bundleKey = _dialogueVoteBundleKey;
        var voteScope = _dialogueVoteScope;
        var nodeIndex = DialogueNodeFromScope(voteScope);
        var vote = _hostDialogueVote;
        _committedDialogueBundleKey = bundleKey;
        _committedDialogueScope = voteScope;
        _log?.LogInfo(
            $"Dialogue vote committed: bundle={bundleKey:X8}; node={nodeIndex}; " +
            $"msg={DialogueMessageFromScope(voteScope)}; vote={vote}; ready=2/2");
        PublishDialogueVotes(session);
        if (vote >= DialogueVoteChoiceBase)
            Publish(session, ManagerDomain.Dialogue, ManagerAction.DialogueChoice,
                bundleKey, PackDialogueChoice(voteScope, vote - DialogueVoteChoiceBase));
        else
            Publish(session, ManagerDomain.Dialogue,
                vote == DialogueVoteSkip ? ManagerAction.Skip : ManagerAction.Continue,
                bundleKey, voteScope);
        ResetDialogueVotes();

        var previousApplying = _applying;
        var previousDialogueAuthority = _applyingDialogueAuthority;
        _applying = true;
        _applyingDialogueAuthority = true;
        try
        {
            ExecuteDialogueVote(manager, bundleKey, voteScope, vote);
        }
        finally
        {
            _applying = previousApplying;
            _applyingDialogueAuthority = previousDialogueAuthority;
        }
        ObserveLiveHostDialogue(session);
    }

    private static bool ExecuteDialogueVote(
        DialogueManager manager, int bundleKey, int voteScope, int vote)
    {
        if (!CanApplyDialogueVoteScope(manager, bundleKey, voteScope))
            return false;
        if (vote == DialogueVoteSkip)
        {
            manager.OnSkip();
            return true;
        }
        if (vote is DialogueVoteContinue or DialogueVoteFinish)
        {
            manager.ContinueDialogueManual();
            return true;
        }
        var panel = FindActiveChoicePanel(manager);
        var choice = vote - DialogueVoteChoiceBase;
        if (panel == null || choice < 0 || choice >= panel.CheckChoiceCount())
            return false;
        panel.m_CurButtonIndex = choice;
        panel.ExcuteFocusDialogue();
        return true;
    }

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
        _lastBlockedTraceRevisions.Clear();
        _hostMenuSlots.Clear();
        Array.Fill(_hostWasabi, -1);
        Array.Fill(_hostWallet, -1);
        _hostUnlocks.Clear();
        _hostTutorialStep = -1;
        _pendingClientTutorialStep = -1;
        _clientTutorialAppliedStep = -1;
        _clientTutorialAppliedSceneId = 0;
        _allowClientTutorialActivation = false;
        _remoteSushiPlate = null;
        _clientRemoteSushiPlate = false;
        _sushiRevision = 0;
        _clientSushiRevision = 0;
        _pendingSushiResult = null;
        _applying = false;
        _applyingScenarioAuthority = false;
        _applyingDialogueAuthority = false;
        _applyingTimeAuthority = false;
        if (_remoteTimeScopeActive)
        {
            try
            {
                _applyingTimeAuthority = true;
                TimeManager.Instance?.ResetTimeScale();
            }
            catch (Exception exception)
            {
                _log.LogWarning($"Remote time scope cleanup failed: {exception.Message}");
            }
            finally
            {
                _applyingTimeAuthority = false;
            }
        }
        _remoteTimeScopeActive = false;
        _waitingForManagementPanelClose = false;
        _suppressPublish = 0;
        _nextSushiScan = 0f;
        _nextProgressionScan = 0f;
        _nextDayScan = 0f;
        _nextStoryScan = 0f;
        _nextStorySafetyKeyframe = 0f;
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
            _pendingHostTimelineInvocation = null;
            _hostTimelineInvocation = null;
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
            _pendingHostScenarioInvocation = null;
            _hostScenarioInvocation = null;
            _hostDialogueBundleKey = 0;
            _hostDialogueIndex = -1;
            _localDialogueBundleKey = 0;
            _pendingHostDialogueInvocation = null;
            _hostDialogueInvocation = null;
        }
        _clientScenarioBundleId = string.Empty;
        _clientScenarioBundleKey = 0;
        _clientScenarioNodeId = -1;
        _clientDialogueBundleKey = 0;
        _clientDialogueIndex = -1;
        ResetDialogueVotes();
        ResetDialogueCommit();
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
        ForceHostStoryKeyframe();
        _hostDayTicks = long.MinValue;
        _hostDayTime = int.MinValue;
        _hostWeather = int.MinValue;
        _nextDayScan = 0f;
        Array.Fill(_hostWallet, -1);
        _hostUnlocks.Clear();
        _hostTutorialStep = -1;
        _nextProgressionScan = 0f;
        _activeSessionSnapshotPublished = false;
    }

    private void ForceHostStoryKeyframe()
    {
        _storySnapshotPublished = false;
        _nextStoryScan = 0f;
        _nextStorySafetyKeyframe = _wasConnected
            ? Time.realtimeSinceStartup + 5f
            : 0f;
    }

    internal static bool ShouldForceSceneEntryKeyframe(
        SessionRole role,
        bool scenesMatch,
        float requestedAt,
        float now) =>
        role == SessionRole.Host && scenesMatch && requestedAt > 0f && now >= requestedAt;

    private static bool ShouldForceStorySafetyKeyframe(
        bool hostConnected,
        bool storySnapshotPublished,
        float requestedAt,
        float now) =>
        hostConnected && storySnapshotPublished && requestedAt > 0f && now >= requestedAt;

    internal static bool ShouldScheduleReconnectKeyframe(
        SessionRole role,
        bool wasConnected,
        bool connected) =>
        role == SessionRole.Host && !wasConnected && connected;

    private static bool ShouldForceScenarioMilestoneKeyframe(
        bool applying,
        SessionRole role,
        bool connected,
        int bundleKey,
        int activeBundleKey) =>
        !applying && role == SessionRole.Host && connected && bundleKey != 0 &&
        bundleKey == activeBundleKey;

    internal void OnSceneChanged(SessionRole role)
    {
        if (role == SessionRole.Client)
            ClearClientTutorialPresentation();
        ResetDialogueVotes();
        ResetDialogueCommit();
        _remoteSushiPlate = null;
        _clientRemoteSushiPlate = false;
        _pendingHostTimelineInvocation = null;
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

    private void ClearClientTutorialPresentation()
    {
        try
        {
            var tutorial = TutorialManager.Instance;
            ReleaseTutorialPresentation(tutorial, tutorial?.GetHandler());
            _log?.LogInfo(
                $"Client tutorial presentation cleared: step={_clientTutorialAppliedStep}; " +
                $"scene={_clientTutorialAppliedSceneId}");
        }
        catch (Exception exception)
        {
            _log?.LogWarning(
                $"Client tutorial presentation cleanup failed: {exception.Message}");
        }
        _clientTutorialAppliedStep = -1;
        _clientTutorialAppliedSceneId = 0;
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
        int context,
        ManagerInvocationDescriptor? invocation = null)
    {
        var lane = LaneOf(domain);
        var revision = NextRevision(GetRevision(_hostRevisions, lane));
        _hostRevisions[lane] = revision;
        var state = new ManagerEvent(
            revision,
            IsGlobal(domain) ? 0 : _sceneId,
            CurrentTick(),
            (byte)domain, (byte)action, value, context, invocation);
        TraceManagerEvent("publish", state, lane, revision, _outboundEvents.Count);
        TryQueueOutbound(session, state);
        FlushOutbound(session);
    }

    private bool TryQueueOutbound(UdpSession session, ManagerEvent state)
    {
        if (_outboundEvents.Count >= MaxPendingManagerEvents)
        {
            _log?.LogError(
                $"Manager outbound overflow: count={_outboundEvents.Count}; " +
                $"incoming={(ManagerDomain)state.Domain}/{(ManagerAction)state.Action}; " +
                $"revision={state.Revision}; scene={state.SceneId}/{state.SceneEpoch}");
            _outboundEvents.Clear();
            session?.FailReliableDeliveryFromDomain("manager event outbound queue overflow");
            return false;
        }
        _outboundEvents.Enqueue(state);
        return true;
    }

    private bool TryQueuePendingHostEvent(
        UdpSession session,
        ManagerDomain lane,
        ManagerEvent state)
    {
        if (!_pendingHostEvents.TryGetValue(lane, out var pending))
            _pendingHostEvents[lane] = pending = new SortedDictionary<uint, ManagerEvent>();
        if (pending.ContainsKey(state.Revision))
        {
            pending[state.Revision] = state;
            TraceManagerEvent(
                "queue-replace", state, lane,
                GetRevision(_clientManagerRevisions, lane), pending.Count);
            return true;
        }
        if (pending.Count >= MaxPendingManagerEvents)
        {
            _log?.LogError(
                $"Manager pending overflow: lane={lane}; expected=" +
                $"{NextRevision(GetRevision(_clientManagerRevisions, lane))}; " +
                $"incomingRevision={state.Revision}; incoming=" +
                $"{(ManagerDomain)state.Domain}/{(ManagerAction)state.Action}; " +
                $"laneCount={pending.Count}; all={PendingLaneSummary()}; " +
                $"runtime={DescribeApplyState(state)}");
            _pendingHostEvents.Clear();
            session?.FailReliableDeliveryFromDomain("manager event pending queue overflow");
            return false;
        }
        pending[state.Revision] = state;
        TraceManagerEvent(
            "queue", state, lane,
            GetRevision(_clientManagerRevisions, lane), pending.Count);
        return true;
    }

    private static bool HasManagerEventCapacity(int queued) =>
        queued < MaxPendingManagerEvents;

    private void FlushOutbound(UdpSession session)
    {
        while (_outboundEvents.Count > 0 && session.ReliableCapacityRemaining > 0 &&
               session.SendManagerEvent(_outboundEvents.Peek()))
            _outboundEvents.Dequeue();
    }

    private void ApplyClientRequest(UdpSession session, ManagerEvent state)
    {
        if (state.Revision != 0)
            return;
        if (state.SceneId == 0 ? state.SceneEpoch != 0 :
            state.SceneEpoch != session.LocalSceneEpoch)
            return;
        var domain = (ManagerDomain)state.Domain;
        if (domain == ManagerDomain.Dialogue &&
            (ManagerAction)state.Action == ManagerAction.DialogueVote)
        {
            ApplyClientDialogueVote(session, state);
            return;
        }
        if (domain == ManagerDomain.Progression &&
            (ManagerAction)state.Action == ManagerAction.Wallet)
        {
            if (state.SceneId != 0)
                return;
            var type = (GoodsType)state.Value;
            var balance = GetWalletBalance(type);
            if (!IsWalletDebitValid(type, state.Context, balance))
                return;
            CommonDefine.Instance?.AddPlayerGoods(type, state.Context);
            PublishWallet(session, type);
            return;
        }
        if ((domain is ManagerDomain.SushiMenu or ManagerDomain.SushiTable or
                ManagerDomain.SushiWasabi) &&
            state.SceneId == _sceneId && state.SceneId != 0 &&
            state.SceneEpoch == session.LocalSceneEpoch && session.SceneMatches(_sceneId))
        {
            ApplySushiClientRequest(session, state);
            return;
        }
        if (state.SceneId != 0 || domain != ManagerDomain.Dialogue || !ValidateDialogueIntent(state))
            return;
        if (Apply(state))
            Publish(session, (ManagerDomain)state.Domain, (ManagerAction)state.Action,
                state.Value, state.Context);
    }

    private void ApplyClientDialogueVote(UdpSession session, ManagerEvent state)
    {
        if (!TryUnpackDialogueVotes(
                state.Context, out var voteScope, out var hostVote, out var clientVote) ||
            hostVote != 0 || clientVote == 0 || state.Value != _hostDialogueBundleKey)
        {
            _log?.LogWarning(
                $"Client dialogue vote rejected: bundle={state.Value:X8}; " +
                $"hostBundle={_hostDialogueBundleKey:X8}; context={state.Context}");
            return;
        }
        var manager = DialogueManager.Instance;
        if (!CanApplyDialogueVoteScope(manager, state.Value, voteScope) ||
            IsCommittedDialogueScope(
                state.Value, voteScope,
                _committedDialogueBundleKey, _committedDialogueScope) ||
            !IsDialogueVoteValid(manager, clientVote))
        {
            _log?.LogWarning(
                $"Client dialogue vote stale: bundle={state.Value:X8}; " +
                $"scope={voteScope}; vote={clientVote}; live={DialogueVoteScope(manager)}");
            return;
        }
        SetDialogueVoteScope(state.Value, voteScope);
        _clientDialogueVote = clientVote;
        _log?.LogInfo(
            $"Client dialogue vote accepted: bundle={state.Value:X8}; " +
            $"scope={voteScope}; vote={clientVote}; ready={DialogueVoteCount(clientVote)}/2");
        PublishDialogueVotes(session);
        TryCommitDialogueVotes(session, manager);
    }

    private void ApplyPendingHostEvents(UdpSession session, uint sceneId)
    {
        foreach (var pair in _pendingHostEvents)
        {
            var revision = GetRevision(_clientManagerRevisions, pair.Key);
            ApplyPendingLane(pair.Value, ref revision, state =>
            {
                var applied = false;
                if (state.SceneId == 0)
                    applied = state.SceneEpoch == 0 ? Apply(state) : true;
                else if (state.SceneId == sceneId && state.SceneEpoch == session.RemoteSceneEpoch)
                    applied = Apply(state);
                else
                    applied = state.SceneEpoch != session.RemoteSceneEpoch ||
                        !session.SceneMatches(state.SceneId);
                if (applied)
                {
                    _lastBlockedTraceRevisions.Remove(pair.Key);
                    TraceManagerEvent(
                        "apply-ok", state, pair.Key, revision, pair.Value.Count);
                }
                else if (ShouldTraceManagerEvent(state) &&
                         (!_lastBlockedTraceRevisions.TryGetValue(
                              pair.Key, out var blockedRevision) ||
                          blockedRevision != state.Revision))
                {
                    _lastBlockedTraceRevisions[pair.Key] = state.Revision;
                    TraceManagerEvent(
                        "apply-blocked", state, pair.Key, revision, pair.Value.Count);
                    _log?.LogWarning(
                        $"Manager lane blocked: lane={pair.Key}; expected={NextRevision(revision)}; " +
                        $"event={(ManagerDomain)state.Domain}/{(ManagerAction)state.Action}; " +
                        $"revision={state.Revision}; runtime={DescribeApplyState(state)}");
                }
                return applied;
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
                        (ManagerAction)state.Action, state.Value, state.Context,
                        state.Invocation);
                case ManagerDomain.Scenario:
                    return ApplyScenarioState(
                        (ManagerAction)state.Action, state.Value, state.Context,
                        state.Invocation);
                case ManagerDomain.Progression:
                    return ApplyProgression(
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
                    if ((ManagerAction)state.Action is ManagerAction.SushiPickupResult or
                        ManagerAction.SushiServeResult)
                        return ApplySushiFoodResult(
                            (ManagerAction)state.Action, state.Value, state.Context);
                    return ApplyMenuState((ManagerAction)state.Action, state.Value, state.Context);
                case ManagerDomain.SushiWasabi:
                    return ApplyWasabiState(state.Value, state.Context);
                case ManagerDomain.SushiTable:
                    if ((ManagerAction)state.Action == ManagerAction.SushiCleanResult)
                        return ApplySushiCleanResult(state.Value, state.Context);
                    return ApplyTableState(state.Value != 0, state.Context);
                case ManagerDomain.Story:
                    return ApplyStory((ManagerAction)state.Action, state.Value);
                case ManagerDomain.Day:
                    return ApplyDay((ManagerAction)state.Action, state.Value, state.Context);
                case ManagerDomain.Timeline:
                    return ApplyTimeline(
                        (ManagerAction)state.Action, state.Value, state.Context,
                        state.HostTick, state.Invocation);
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
                case ManagerDomain.Time:
                    return ApplyTimeScope((ManagerAction)state.Action, state.Value, state.Context);
                default:
                    return false;
            }
        }
        catch (Exception exception)
        {
            _log?.LogWarning(
                $"Manager event apply failed: domain={domain}; action={(ManagerAction)state.Action}; " +
                $"revision={state.Revision}; value={state.Value}; context={state.Context}; " +
                $"runtime={DescribeApplyState(state)}; error={exception}");
            return false;
        }
        finally
        {
            _applyingScenarioAuthority = previousScenarioAuthority;
            _applyingDialogueAuthority = previousDialogueAuthority;
            _applying = previousApplying;
        }
    }

    private bool ApplyTimeScope(ManagerAction action, int value, int context)
    {
        try
        {
            _applyingTimeAuthority = true;
            switch (action)
            {
                case ManagerAction.TimeScale:
                    var scale = BitConverter.Int32BitsToSingle(context);
                    if (float.IsNaN(scale) || float.IsInfinity(scale) || scale < 0f || scale > 4f ||
                        !Enum.IsDefined(typeof(TimeScaleController.Type), value))
                        return false;
                    TimeManager.SetTimeScale((TimeScaleController.Type)value, scale);
                    _remoteTimeScopeActive = true;
                    _log?.LogInfo(
                        $"Time authority applied: action=Scale; type={(TimeScaleController.Type)value}; " +
                        $"scale={scale}; unityScale={Time.timeScale}");
                    return true;
                case ManagerAction.TimeStop:
                    if (WaitForManagementPanelClose())
                        return false;
                    TimeManager.Instance?.TimeStop("DaveTheDiverMP", false);
                    _remoteTimeScopeActive = true;
                    _log?.LogInfo(
                        $"Time authority applied: action=Stop; unityScale={Time.timeScale}");
                    return true;
                case ManagerAction.TimeReset:
                    if (_remoteTimeScopeActive)
                        TimeManager.Instance?.ResetTimeScale();
                    _remoteTimeScopeActive = false;
                    _log?.LogInfo(
                        $"Time authority applied: action=Reset; unityScale={Time.timeScale}");
                    return true;
                default:
                    return false;
            }
        }
        finally
        {
            _applyingTimeAuthority = false;
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

    private bool ApplyDialogueState(
        ManagerAction action,
        int bundleKey,
        int index,
        ManagerInvocationDescriptor? invocation)
    {
        var manager = DialogueManager.Instance;
        switch (action)
        {
            case ManagerAction.DialogueStarted:
                if (WaitForManagementPanelClose())
                    return false;
                if (manager?.IsPlaying == true &&
                    IsLocalDialogueBundle(manager.CurrentBundleID))
                {
                    var localBundleId = manager.CurrentBundleID;
                    manager.TotalFinishDialogue();
                    _localDialogueBundleKey = 0;
                    _log?.LogInfo(
                        $"Local dialogue closed for shared start: id={localBundleId}; " +
                        $"shared={bundleKey:X8}");
                }
                var tutorial = TutorialManager.Instance;
                var tutorialHandler = tutorial?.GetHandler();
                if (ShouldReleaseTutorialPresentation(
                        bundleKey,
                        ContentKey(tutorialHandler?.startDialogueID),
                        invocation?.BundleId))
                {
                    try
                    {
                        ReleaseTutorialPresentation(tutorial, tutorialHandler);
                        _log?.LogInfo(
                            $"Shared tutorial presentation released: " +
                            $"bundle={bundleKey:X8}; step={tutorial?.CurrentStep.ToString() ?? "none"}");
                    }
                    catch (Exception exception)
                    {
                        _log?.LogWarning(
                            $"Shared tutorial presentation cleanup failed: " +
                            $"bundle={bundleKey:X8}; error={exception.Message}");
                    }
                }
                ResetDialogueVotes();
                if (_clientDialogueBundleKey != bundleKey)
                    ResetDialogueCommit();
                _clientDialogueBundleKey = bundleKey;
                _clientDialogueIndex = -1;
                if (bundleKey == 0)
                    return false;
                if (manager?.IsPlaying == true && ContentKey(manager.CurrentBundleID) == bundleKey)
                {
                    _clientDialogueIndex = manager.m_CurrentDialogueIndex;
                    _clientDialogueSpectating = false;
                    _log?.LogInfo(
                        $"Dialogue client start matched native: bundle={bundleKey:X8}; " +
                        $"node={_clientDialogueIndex}");
                    return true;
                }
                if (CallbackIdentity(_pendingClientDialogueStart?.BundleKey ?? 0, bundleKey) ==
                    CallbackIdentityDecision.Clear)
                    CancelPendingDialogueStart();
                var dialogueDecision = DecideInvocationReplay(
                    _pendingClientDialogueStart?.BundleKey == bundleKey,
                    IsDialogueInvocation(invocation));
                if (dialogueDecision == InvocationReplayDecision.Synthesize)
                {
                    _pendingClientDialogueStart = CreateDialogueStart(invocation.Value);
                    _log.LogInfo($"Dialogue presentation synthesized: bundle={bundleKey:X8}");
                }
                if (dialogueDecision == InvocationReplayDecision.Spectate || manager == null)
                {
                    _clientDialogueSpectating = true;
                    _log.LogInfo(
                        $"Dialogue presentation spectator fallback: bundle={bundleKey:X8}; " +
                        "unsupported or unavailable invocation");
                    return true;
                }
                _clientDialogueSpectating = false;
                ReplayDialogueStart(manager, _pendingClientDialogueStart);
                _log?.LogInfo($"Dialogue client replay started: bundle={bundleKey:X8}");
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
                ResetDialogueVotes();
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
                    manager.TotalFinishDialogue();
                if (!CompleteDialogueCallback(bundleKey, index != 0))
                {
                    CompleteSynthesizedTutorialDialogue(bundleKey, index != 0);
                    var ui = GlobalUI.Instance;
                    var wasHudOn = ui?.CurMainCanvas?.IsHudOn == true;
                    ui?.ShowHUD(HUDLockType.Dialogue);
                    _log?.LogInfo(
                        $"Shared dialogue HUD lock released: bundle={bundleKey:X8}; " +
                        $"wasOn={wasHudOn}; nowOn={ui?.CurMainCanvas?.IsHudOn == true}");
                }
                _clientDialogueBundleKey = 0;
                _clientDialogueIndex = -1;
                _clientDialogueSpectating = false;
                _pendingClientDialogueStart = null;
                ResetDialogueVotes();
                ResetDialogueCommit();
                _log?.LogInfo(
                    $"Dialogue client finish: bundle={bundleKey:X8}; result={index != 0}");
                return true;
            case ManagerAction.DialogueVote:
                if (bundleKey == 0 || bundleKey != _clientDialogueBundleKey ||
                    !TryUnpackDialogueVotes(
                        index, out var voteScope, out var hostVote, out var clientVote))
                    return false;
                if (!CanApplyDialogueVoteScope(manager, bundleKey, voteScope))
                {
                    _log?.LogInfo(
                        $"Dialogue vote consumed before panel ready: bundle={bundleKey:X8}; " +
                        $"scope={voteScope}; live={DialogueVoteScope(manager)}");
                    return true;
                }
                if (hostVote != 0 && !IsDialogueVoteValid(manager, hostVote) ||
                    clientVote != 0 && !IsDialogueVoteValid(manager, clientVote))
                    return true;
                SetDialogueVoteScope(bundleKey, voteScope);
                _hostDialogueVote = hostVote;
                _clientDialogueVote = clientVote;
                _log?.LogInfo(
                    $"Dialogue votes received: bundle={bundleKey:X8}; scope={voteScope}; " +
                    $"host={hostVote}; client={clientVote}");
                return true;
            case ManagerAction.Continue:
                if (_clientDialogueSpectating)
                    return true;
                if (IsCommittedDialogueScope(
                        bundleKey, index,
                        _committedDialogueBundleKey, _committedDialogueScope) ||
                    IsLiveDialogueScopePast(manager, bundleKey, index))
                    return true;
                if (!CanApplyDialogueVoteScope(manager, bundleKey, index))
                    return false;
                _committedDialogueBundleKey = bundleKey;
                _committedDialogueScope = index;
                ResetDialogueVotes();
                _log?.LogInfo(
                    $"Dialogue commit applied: action=Continue; bundle={bundleKey:X8}; scope={index}");
                manager.ContinueDialogueManual();
                return true;
            case ManagerAction.Skip:
                if (_clientDialogueSpectating)
                    return true;
                if (IsCommittedDialogueScope(
                        bundleKey, index,
                        _committedDialogueBundleKey, _committedDialogueScope) ||
                    IsLiveDialogueScopePast(manager, bundleKey, index))
                    return true;
                if (!CanApplyDialogueVoteScope(manager, bundleKey, index))
                    return false;
                _committedDialogueBundleKey = bundleKey;
                _committedDialogueScope = index;
                ResetDialogueVotes();
                _log?.LogInfo(
                    $"Dialogue commit applied: action=Skip; bundle={bundleKey:X8}; scope={index}");
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
            case ManagerAction.DialogueChoice:
                if (_clientDialogueSpectating)
                    return true;
                if (!TryUnpackDialogueChoice(
                        index, out var choiceScope, out var choiceIndex))
                    return false;
                if (IsCommittedDialogueScope(
                        bundleKey, choiceScope,
                        _committedDialogueBundleKey, _committedDialogueScope) ||
                    IsLiveDialogueScopePast(manager, bundleKey, choiceScope))
                    return true;
                if (!CanApplyDialogueVoteScope(manager, bundleKey, choiceScope))
                    return false;
                var choicePanel = FindActiveChoicePanel(manager);
                if (choicePanel == null || choiceIndex < 0 ||
                    choiceIndex >= choicePanel.CheckChoiceCount())
                    return false;
                _committedDialogueBundleKey = bundleKey;
                _committedDialogueScope = choiceScope;
                ResetDialogueVotes();
                _log?.LogInfo(
                    $"Dialogue commit applied: action=Choice; bundle={bundleKey:X8}; " +
                    $"scope={choiceScope}; choice={choiceIndex}");
                choicePanel.m_CurButtonIndex = choiceIndex;
                choicePanel.ExcuteFocusDialogue();
                CompleteDialogueChoiceCallback(bundleKey, choiceIndex);
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

    private bool ApplyScenarioState(
        ManagerAction action,
        int bundleKey,
        int nodeId,
        ManagerInvocationDescriptor? invocation)
    {
        var manager = ScenarioManager.Instance;
        switch (action)
        {
            case ManagerAction.ScenarioStarted:
                if (bundleKey == 0)
                    return false;
                _clientScenarioBundleKey = bundleKey;
                _clientScenarioNodeId = -1;
                if (ScenarioPreviousKey(
                        manager?.IsPlaying == true, () => manager.CurrentSequenceID) == bundleKey)
                {
                    _clientScenarioSpectating = false;
                    return true;
                }
                if (CallbackIdentity(_pendingClientScenarioStart?.BundleKey ?? 0, bundleKey) ==
                    CallbackIdentityDecision.Clear)
                {
                    CancelPendingScenarioStart();
                    _blockedClientScenarioNode = null;
                    _blockedClientScenarioNodeKey = 0;
                }
                var scenarioDecision = DecideInvocationReplay(
                    _pendingClientScenarioStart?.BundleKey == bundleKey,
                    invocation?.Kind == ManagerInvocationKind.Scenario);
                if (scenarioDecision == InvocationReplayDecision.Synthesize)
                {
                    _pendingClientScenarioStart = CreateScenarioStart(invocation.Value);
                    _log.LogInfo($"Scenario presentation synthesized: bundle={bundleKey:X8}");
                }
                if (scenarioDecision == InvocationReplayDecision.Spectate || manager == null)
                {
                    _clientScenarioSpectating = true;
                    _log.LogInfo(
                        $"Scenario presentation spectator fallback: bundle={bundleKey:X8}; " +
                        "unsupported or unavailable invocation");
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

    private static string[] SnapshotArguments(
        Il2CppSystem.Collections.Generic.List<string> arguments)
    {
        if (arguments == null)
            return null;
        var snapshot = new string[arguments.Count];
        for (var index = 0; index < snapshot.Length; index++)
            snapshot[index] = arguments[index];
        return snapshot;
    }

    private static Il2CppSystem.Collections.Generic.List<string> RestoreArguments(
        string[] arguments)
    {
        if (arguments == null)
            return null;
        var restored = new Il2CppSystem.Collections.Generic.List<string>();
        foreach (var argument in arguments)
            restored.Add(argument);
        return restored;
    }

    private static PendingScenarioStart CreateScenarioStart(
        ManagerInvocationDescriptor invocation) => new()
    {
        Kind = ScenarioStartKind.Normal,
        BundleId = invocation.BundleId,
        BundleKey = ContentKey(invocation.BundleId),
        Arguments = RestoreArguments(invocation.Arguments),
        UseButton = invocation.UseButton,
        ShowCurtain = invocation.ShowCurtain,
        IgnorePlaying = invocation.IgnorePlaying
    };

    private static PendingDialogueStart CreateDialogueStart(
        ManagerInvocationDescriptor invocation) => new()
    {
        Kind = invocation.Kind switch
        {
            ManagerInvocationKind.DialogueArguments => DialogueStartKind.Arguments,
            ManagerInvocationKind.DialogueSmall => DialogueStartKind.Small,
            _ => DialogueStartKind.Normal
        },
        BundleId = invocation.BundleId,
        BundleKey = ContentKey(invocation.BundleId),
        Arguments = invocation.Kind == ManagerInvocationKind.DialogueArguments
            ? RestoreArguments(invocation.Arguments)
            : null,
        UseButton = invocation.UseButton,
        ShowCurtain = invocation.ShowCurtain
    };

    private static bool IsDialogueInvocation(ManagerInvocationDescriptor? invocation) =>
        invocation?.Kind is ManagerInvocationKind.DialogueNormal or
            ManagerInvocationKind.DialogueArguments or ManagerInvocationKind.DialogueSmall;

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

    private bool CompleteDialogueCallback(int bundleKey, bool result)
    {
        var decision = CallbackIdentity(
            _pendingClientDialogueStart?.BundleKey ?? 0, bundleKey);
        if (decision != CallbackIdentityDecision.Invoke)
        {
            if (decision == CallbackIdentityDecision.Clear)
                CancelPendingDialogueStart();
            return false;
        }
        var pending = _pendingClientDialogueStart;
        _pendingClientDialogueStart = null;
        var callback = pending?.Callback;
        if (pending != null)
            pending.Callback = null;
        InvokeClientPresentationCallback(() => callback?.Invoke(result), "dialogue");
        return callback != null;
    }

    private void CompleteSynthesizedTutorialDialogue(int bundleKey, bool result)
    {
        var tutorial = TutorialManager.Instance;
        var handler = tutorial?.GetHandler();
        var tutorialBundleKey = ContentKey(handler?.startDialogueID);
        if (!ShouldCompleteTutorialDialogue(bundleKey, tutorialBundleKey, result))
            return;
        InvokeClientPresentationCallback(() => handler.FinishDialogue(result), "tutorial dialogue");
        _log?.LogInfo(
            $"Shared tutorial dialogue callback completed: bundle={bundleKey:X8}; " +
            $"step={tutorial.CurrentStep}");
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
        var previousApplying = _applying;
        _applying = false;
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
            _applying = previousApplying;
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

    private void ApplySushiClientRequest(UdpSession session, ManagerEvent state)
    {
        var action = (ManagerAction)state.Action;
        if (action == ManagerAction.SushiPickupRequest)
        {
            var place = (SushiBar.Place)state.Value;
            var queue = place is >= SushiBar.Place.Main and < SushiBar.Place.Max
                ? SushiBarStaffManager.GetInstanceOrderQueue(place)
                : null;
            if (_remoteSushiPlate == null && queue != null)
                _remoteSushiPlate = queue.GetNowServingData(CustomerTypeFlag.ALL, null, false);
            var recipe = _remoteSushiPlate == null
                ? 0
                : _remoteSushiPlate.PickUpID != 0
                    ? _remoteSushiPlate.PickUpID
                    : _remoteSushiPlate.OrderID;
            var target = _remoteSushiPlate?.ReciveCustomer;
            Publish(session, ManagerDomain.SushiMenu, ManagerAction.SushiPickupResult,
                recipe, target == null ? PackSushiTarget(place, 0) :
                PackSushiTarget(target.PlaceTag, target.SeatNumber));
            return;
        }
        if (action == ManagerAction.SushiServeRequest)
        {
            var success = false;
            if (_remoteSushiPlate != null &&
                TryUnpackSushiTarget(state.Context, out var place, out var table))
            {
                var customer = SushiBar.Customer.SushiBarCustomerManager.Instance?
                    .GetVisitCustomer(table, place);
                var expected = _remoteSushiPlate.ReciveCustomer;
                var staff = SushiBarManager.Instance?.dave;
                if (customer != null && customer.CanServed() &&
                    staff != null &&
                    (expected == null || expected.GetInstanceID() == customer.GetInstanceID()))
                    success = customer.Served(staff);
            }
            if (success)
                _remoteSushiPlate = null;
            Publish(session, ManagerDomain.SushiMenu, ManagerAction.SushiServeResult,
                success ? 1 : 0, state.Context);
            return;
        }
        if (action == ManagerAction.SushiCleanRequest && state.Value is >= 0 and <= 1_000_000 &&
            TryUnpackSushiTarget(state.Context, out var cleanPlace, out var cleanTable))
        {
            var trigger = FindTrashTrigger(cleanPlace, cleanTable);
            var success = trigger?.Target?.IsTrash == true;
            if (success)
                trigger.CleanFinished(state.Value);
            Publish(session, ManagerDomain.SushiTable, ManagerAction.SushiCleanResult,
                success ? state.Value : -1, state.Context);
            return;
        }
        if (action == ManagerAction.SushiWasabiRequest &&
            state.Value is >= (int)SushiBar.Place.Main and < (int)SushiBar.Place.Max &&
            state.Context is >= 1 and <= 1000)
            SushiBarContext.Operation?.UpdateWasabiCount(
                (SushiBar.Place)state.Value, state.Context);
    }

    private bool ApplySushiFoodResult(ManagerAction action, int value, int context)
    {
        var dave = SushiBarManager.Instance?.dave;
        if (dave == null)
            return false;
        if (action == ManagerAction.SushiPickupResult)
        {
            if (value <= 0)
                return true;
            var place = TryUnpackSushiTarget(context, out var targetPlace, out _)
                ? targetPlace
                : SushiBar.Place.Main;
            var local = SushiBarStaffManager.GetInstanceOrderQueue(place)?
                .GetNowServingData(CustomerTypeFlag.ALL, null, false);
            dave.ProgressData = local;
            dave.PickupRecipeID.Value = value;
            _clientRemoteSushiPlate = true;
            return true;
        }
        if (action != ManagerAction.SushiServeResult || value == 0)
            return true;
        if (TryUnpackSushiTarget(context, out var placeTag, out var table))
        {
            var customer = SushiBar.Customer.SushiBarCustomerManager.Instance?
                .GetVisitCustomer(table, placeTag);
            if (customer?.CanServed() == true)
                customer.Served(dave);
        }
        dave.ProgressData = null;
        dave.PickupRecipeID.Value = 0;
        _clientRemoteSushiPlate = false;
        return true;
    }

    private bool ApplySushiCleanResult(int gold, int context)
    {
        if (gold < 0 || !TryUnpackSushiTarget(context, out var place, out var table))
            return true;
        var trigger = FindTrashTrigger(place, table);
        if (trigger?.Target?.IsTrash == true)
            trigger.CleanFinished(gold);
        return true;
    }

    private static SushiBar.Customer.SushiBarCustomer FindNearestCustomer(Vector3 position)
    {
        SushiBar.Customer.SushiBarCustomer nearest = null;
        var distance = 16f;
        foreach (var customer in UnityEngine.Object.FindObjectsByType<
                     SushiBar.Customer.SushiBarCustomer>(FindObjectsSortMode.None))
        {
            if (customer == null || !customer.CanServed())
                continue;
            var current = (customer.transform.position - position).sqrMagnitude;
            if (current >= distance)
                continue;
            nearest = customer;
            distance = current;
        }
        return nearest;
    }

    private static SushiBarTrashTrigger FindTrashTrigger(SushiBar.Place place, int table)
    {
        foreach (var trigger in UnityEngine.Object.FindObjectsByType<SushiBarTrashTrigger>(
                     FindObjectsSortMode.None))
            if (trigger != null && trigger.TableNumber == table && trigger.Target != null &&
                TryFindTablePlace(trigger.Target, out var current) && current == place)
                return trigger;
        return null;
    }

    private static bool TryFindTablePlace(SushiBarTable table, out SushiBar.Place place)
    {
        var manager = SushibarTableManager.Instance;
        if (manager != null)
            for (var value = (int)SushiBar.Place.Main; value < (int)SushiBar.Place.Max; value++)
                if (manager.GetTable(table.tableNumber, (SushiBar.Place)value) == table)
                {
                    place = (SushiBar.Place)value;
                    return true;
                }
        place = SushiBar.Place.Unknown;
        return false;
    }

    private static int PackSushiTarget(SushiBar.Place place, int table) =>
        place is >= SushiBar.Place.Main and < SushiBar.Place.Max && table is >= 0 and <= ushort.MaxValue
            ? ((int)place << 16) | table
            : -1;

    private static bool TryUnpackSushiTarget(
        int value, out SushiBar.Place place, out int table)
    {
        place = (SushiBar.Place)((uint)value >> 16);
        table = value & 0xffff;
        return value >= 0 && place is >= SushiBar.Place.Main and < SushiBar.Place.Max;
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

    private bool ApplyTimeline(
        ManagerAction action,
        int tid,
        int context,
        uint hostTick,
        ManagerInvocationDescriptor? invocation)
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
            var invocationDecision = DecideInvocationReplay(
                intent != null,
                route == TimelineStartRoute.ByTid &&
                    invocation?.Kind == ManagerInvocationKind.TimelineByTid);
            if (invocationDecision == InvocationReplayDecision.Synthesize)
            {
                var descriptor = invocation.Value;
                intent = new TimelineLease
                {
                    Tid = timelineTid,
                    Route = TimelineStartRoute.ByTid,
                    State = ClientTimelineState.AwaitingHostStart,
                    ApplyOffset = descriptor.ApplyOffset,
                    CustomPos = descriptor.HasCustomPosition
                        ? new Il2CppSystem.Nullable<Vector3>(new Vector3(
                            descriptor.CustomX, descriptor.CustomY, descriptor.CustomZ))
                        : default
                };
                _log.LogInfo($"Timeline presentation synthesized: tid={timelineTid}");
            }
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
                _log.LogInfo(
                    $"Timeline presentation spectator fallback: tid={timelineTid}; " +
                    "controller invocation unavailable");
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

    private bool ApplyProgression(ManagerAction action, int value, int context)
    {
        var probe = ProbeBehaviour.Instance;
        probe?.BeginRemoteMissionApply();
        try
        {
            var actionValue = (int)action;
            if (actionValue is >= (int)ManagerAction.RewardFirst and <= (int)ManagerAction.RewardLast)
            {
                if (!TryUnpackRewardContext(context, out var count, out var showType))
                    return false;
                var type = (CommonRewardType)(actionValue - RewardActionBase);
                var reward = Reward.CreateInstance(type, value, count, showType);
                if (reward == null || RewardManager.Instance == null)
                    return false;
                RewardManager.Instance.Reward(reward);
                _log.LogInfo($"Shared reward applied: type={type}; value={value}; count={count}");
                return true;
            }
            if (action == ManagerAction.Wallet)
            {
                var type = (GoodsType)value;
                if (context < 0 || type is < GoodsType.gold or > GoodsType.fakePoint)
                    return false;
                var current = GetWalletBalance(type);
                if (current < 0)
                    return false;
                if (current != context)
                    CommonDefine.Instance?.AddPlayerGoods(type, context - current);
                return true;
            }
            if (action == ManagerAction.TutorialStep)
            {
                if (!IsValidTutorialStep(value))
                    return false;
                if (TutorialManager.Instance == null)
                    return ShouldConsumeUnavailableProgression(action);
                if (ShouldQueueTutorialReplay(
                        value, _clientTutorialAppliedStep,
                        _clientTutorialAppliedSceneId, _sceneId))
                    _pendingClientTutorialStep = value;
                return true;
            }
            if (action != ManagerAction.Unlock || context is < 0 or > 3)
                return false;
            var unlockManager = ContentsUnlockManager.Instance;
            var contents = (ContentsList)value;
            var data = unlockManager?.GetUnlockData(contents);
            var created = false;
            if (data == null && unlockManager != null && (context & 1) != 0)
            {
                unlockManager.ForceUnlock(contents);
                data = unlockManager.GetUnlockData(contents);
                created = data != null;
                _log?.LogInfo(
                    $"Shared unlock native force: id={value}; created={created}");
            }
            if (data == null)
            {
                _log?.LogWarning(
                    $"Shared unlock deferred: id={value}; flags={context}; " +
                    "native unlock data unavailable; event consumed for lane progress");
                return ShouldConsumeUnavailableProgression(action);
            }
            var isUnlock = (context & 1) != 0;
            var isNew = (context & 2) != 0;
            var changed = data.isUnlock != isUnlock || data.isNew != isNew;
            if (changed)
            {
                data.isUnlock = isUnlock;
                data.isNew = isNew;
            }
            if (created || changed)
            {
                data.Save();
                unlockManager.RefreshUI();
                _log?.LogInfo(
                    $"Shared unlock committed: id={value}; created={created}; " +
                    $"changed={changed}; unlock={data.isUnlock}; new={data.isNew}");
            }
            return true;
        }
        finally
        {
            probe?.EndRemoteMissionApply();
        }
    }

    private void PublishProgressionChanges(UdpSession session)
    {
        for (var value = (int)GoodsType.gold; value <= (int)GoodsType.fakePoint; value++)
            PublishWalletIfChanged(session, (GoodsType)value);

        var unlocks = ContentsUnlockManager.Instance?.m_UnlockDatas;
        if (unlocks != null)
        {
            foreach (var pair in unlocks)
            {
                var data = pair.Value;
                if (data == null)
                    continue;
                var id = (int)pair.Key;
                var flags = (byte)((data.isUnlock ? 1 : 0) | (data.isNew ? 2 : 0));
                if (_hostUnlocks.TryGetValue(id, out var previous) && previous == flags)
                    continue;
                _hostUnlocks[id] = flags;
                if (flags != 0 || previous != 0)
                    Publish(session, ManagerDomain.Progression, ManagerAction.Unlock, id, flags);
            }
        }

        var tutorial = TutorialManager.Instance;
        var tutorialStep = tutorial == null ? -1 : (int)tutorial.CurrentStep;
        if (IsValidTutorialStep(tutorialStep) && _hostTutorialStep != tutorialStep)
        {
            _hostTutorialStep = tutorialStep;
            Publish(session, ManagerDomain.Progression,
                ManagerAction.TutorialStep, tutorialStep, 0);
        }
    }

    private static bool IsValidTutorialStep(int value) =>
        value is >= (int)TutorialStep.None and <= (int)TutorialStep.AllDone;

    private static bool ShouldConsumeUnavailableProgression(ManagerAction action) =>
        action is ManagerAction.Unlock or ManagerAction.TutorialStep;

    private static bool ShouldAllowNativeTutorialActivation(
        SessionRole role, bool connected, bool controlledActivation) =>
        role != SessionRole.Client || !connected || controlledActivation;

    internal bool AllowNativeTutorialActivation(SessionRole role, UdpSession session)
    {
        var allowed = ShouldAllowNativeTutorialActivation(
            role, session?.Connected == true, _allowClientTutorialActivation);
        if (!allowed)
            _log?.LogInfo(
                $"Duplicate tutorial activation suppressed: " +
                $"step={TutorialManager.Instance?.CurrentStep.ToString() ?? "none"}");
        return allowed;
    }

    private static bool ShouldQueueTutorialReplay(
        int step, int appliedStep, uint appliedScene, uint scene) =>
        step != appliedStep || appliedScene != scene;

    private static bool ShouldCompleteTutorialDialogue(
        int bundleKey, int tutorialBundleKey, bool result) =>
        result && bundleKey != 0 && bundleKey == tutorialBundleKey;

    private static bool ShouldReleaseTutorialPresentation(
        int bundleKey, int tutorialBundleKey, string bundleId) =>
        bundleKey != 0 && (bundleKey == tutorialBundleKey ||
            bundleId?.StartsWith("Tutorial_", StringComparison.Ordinal) == true);

    private void ReleaseTutorialPresentation(
        TutorialManager tutorial, TutorialHandler handler)
    {
        var handlers = new List<TutorialHandler>();
        CollectTutorialHandlers(handler, handlers, new HashSet<int>());
        foreach (var item in handlers)
        {
            try
            {
                ReleaseTutorialHandler(item);
            }
            catch (Exception exception)
            {
                _log?.LogWarning(
                    $"Tutorial handler release failed: name={item?.name}; " +
                    $"error={exception.Message}");
            }
        }
        tutorial?.HideTutorialUI();
        var guide = UnityEngine.Object.FindFirstObjectByType<GuideHelperPanel>();
        guide?.StopAllCoroutines();
        guide?.HideGuideTextUI();
        guide?.HideGuidePointerUI();
        ReleaseSushiManagementSheet();
        _log?.LogInfo($"Tutorial presentation owners released: handlers={handlers.Count}");
    }

    private static void CollectTutorialHandlers(
        TutorialHandler handler,
        List<TutorialHandler> handlers,
        HashSet<int> seen)
    {
        if (handler == null || !seen.Add(handler.GetInstanceID()))
            return;
        handlers.Add(handler);
        CollectTutorialHandlers(handler.m_CurrentBranchTutorial, handlers, seen);
        var branches = handler.m_BranchList;
        if (branches == null)
            return;
        for (var index = 0; index < branches.Count; index++)
            CollectTutorialHandlers(branches[index], handlers, seen);
    }

    private void ReleaseTutorialHandler(TutorialHandler handler)
    {
        var blockers = handler.m_CustomBlockerList;
        _log?.LogInfo(
            $"Tutorial handler release begin: name={handler.name}; " +
            $"step={handler.tutorialStep}; activated={handler.IsActivated}; " +
            $"actionLock={handler.actionLock?.IsEnable == true}; " +
            $"inputLock={handler.inputLocker?.IsEnable == true}; " +
            $"blockers={blockers?.Count ?? 0}");
        try
        {
            handler.StopAllCoroutines();
            handler.DeactivateTutorial();
        }
        catch (Exception exception)
        {
            _log?.LogWarning(
                $"Native tutorial deactivation failed: name={handler.name}; " +
                $"error={exception.Message}");
        }
        DisableTutorialLocks(handler);
        if (blockers != null)
        {
            for (var index = 0; index < blockers.Count; index++)
            {
                var blocker = blockers[index];
                if (blocker == null)
                    continue;
                var wasActive = blocker.activeInHierarchy;
                blocker.SetActive(false);
                _log?.LogInfo(
                    $"Tutorial custom blocker released: handler={handler.name}; " +
                    $"name={blocker.name}; wasActive={wasActive}; " +
                    $"activeNow={blocker.activeInHierarchy}");
            }
        }
        _log?.LogInfo(
            $"Tutorial handler release end: name={handler.name}; " +
            $"activated={handler.IsActivated}; " +
            $"actionLock={handler.actionLock?.IsEnable == true}; " +
            $"inputLock={handler.inputLocker?.IsEnable == true}");
    }

    private static void DisableTutorialLocks(TutorialHandler handler)
    {
        handler?.actionLock?.SetEnable(false);
        handler?.inputLocker?.SetEnable(false);
        handler?.m_Pointerlocker?.SetEnable(false);
    }

    private static bool ShouldDeferTutorialReplay(int activeDialogueBundleKey) =>
        activeDialogueBundleKey != 0;

    private static bool ShouldWaitForManagementPanelClose(
        bool isShown, bool isTransitioning) =>
        isShown || isTransitioning;

    private static bool CanReleaseClosedManagementPanel(
        bool exists, bool isShown, bool isTransitioning) =>
        exists && !isShown && !isTransitioning;

    private void ReleaseSushiManagementSheet()
    {
        var helper = UnityEngine.Object.FindFirstObjectByType<SushiBarTutorialHelper>();
        if (helper != null)
        {
            if (helper.m_Coroutine != null)
                helper.StopCoroutine(helper.m_Coroutine);
            helper.SetBlockCurrentManagementSheet(false);
        }
        var panel = UnityEngine.Object.FindFirstObjectByType<ManagementPanel>();
        if (CanReleaseClosedManagementPanel(
                panel != null, panel?.IsShow == true, panel?.IsTransitioning == true))
            panel.EnaableMenuScroller(true);
        _log?.LogInfo(
            $"Sushi tutorial sheet released: helper={helper != null}; " +
            $"panel={panel != null}; shown={panel?.IsShow == true}; " +
            $"transitioning={panel?.IsTransitioning == true}");
    }

    private bool WaitForManagementPanelClose()
    {
        var panel = UnityEngine.Object.FindFirstObjectByType<ManagementPanel>();
        if (!ShouldWaitForManagementPanelClose(
                panel?.IsShow == true, panel?.IsTransitioning == true))
        {
            if (_waitingForManagementPanelClose)
                _log?.LogInfo("Management panel closed; shared pause may continue");
            _waitingForManagementPanelClose = false;
            return false;
        }

        if (panel.IsShow && !panel.IsTransitioning)
            panel.OnClickClose();
        if (!_waitingForManagementPanelClose)
            _log?.LogInfo(
                $"Management panel close deferred shared pause: " +
                $"shown={panel.IsShow}; transitioning={panel.IsTransitioning}; " +
                $"unityScale={Time.timeScale}");
        _waitingForManagementPanelClose = true;
        return true;
    }

    private void TryApplyPendingTutorial(uint sceneId)
    {
        if (!IsValidTutorialStep(_pendingClientTutorialStep) || sceneId == 0)
            return;
        var tutorial = TutorialManager.Instance;
        if (tutorial == null)
            return;
        if (ShouldDeferTutorialReplay(_clientDialogueBundleKey))
            return;

        var step = (TutorialStep)_pendingClientTutorialStep;
        var probe = ProbeBehaviour.Instance;
        var previousApplying = _applying;
        var initialized = false;
        probe?.BeginRemoteMissionApply();
        _applying = true;
        try
        {
            try
            {
                tutorial.GetHandler()?.DeactivateTutorial();
            }
            catch (Exception exception)
            {
                _log.LogWarning(
                    $"Passive tutorial stale handler cleanup failed: step={step}; " +
                    $"error={exception.Message}");
            }
            tutorial.ApplyStep(step);
            tutorial.RefreshTutorialHandler();
            if (tutorial.CurrentStep != step)
            {
                tutorial.CurrentStep = step;
                tutorial.ApplyStep(step);
                tutorial.RefreshTutorialHandler();
            }
            tutorial.InitActivateTutorial();
            initialized = true;
        }
        catch (Exception exception)
        {
            _log.LogWarning(
                $"Passive tutorial step not ready: step={step}; scene={sceneId}; " +
                $"error={exception.Message}");
        }
        finally
        {
            _applying = previousApplying;
            probe?.EndRemoteMissionApply();
        }
        if (!initialized)
            return;
        try
        {
            _allowClientTutorialActivation = true;
            tutorial.ActivateTutorial();
            _pendingClientTutorialStep = -1;
            _clientTutorialAppliedStep = (int)step;
            _clientTutorialAppliedSceneId = sceneId;
            _log.LogInfo(
                $"Shared tutorial runtime activated: step={step}; scene={sceneId}; " +
                "dialogueAuthority=host");
        }
        catch (Exception exception)
        {
            _log.LogWarning(
                $"Shared tutorial activation not ready: step={step}; scene={sceneId}; " +
                $"error={exception.Message}");
        }
        finally
        {
            _allowClientTutorialActivation = false;
        }
    }

    private void PublishWalletIfChanged(UdpSession session, GoodsType type)
    {
        var balance = GetWalletBalance(type);
        var index = (int)type - (int)GoodsType.gold;
        if (balance < 0 || index < 0 || index >= _hostWallet.Length || _hostWallet[index] == balance)
            return;
        _hostWallet[index] = balance;
        Publish(session, ManagerDomain.Progression, ManagerAction.Wallet, (int)type, balance);
    }

    private void PublishWallet(UdpSession session, GoodsType type)
    {
        var index = (int)type - (int)GoodsType.gold;
        var balance = GetWalletBalance(type);
        if (index < 0 || index >= _hostWallet.Length || balance < 0)
            return;
        _hostWallet[index] = balance;
        Publish(session, ManagerDomain.Progression, ManagerAction.Wallet, (int)type, balance);
    }

    private static int GetWalletBalance(GoodsType type)
    {
        var player = SaveSystem.GetGameSave()?.playerInfo;
        if (player == null)
            return -1;
        return type switch
        {
            GoodsType.gold => player.m_Gold,
            GoodsType.researchPoint => player.m_researchPoint,
            GoodsType.Bei => player.m_Bei,
            GoodsType.trustPoint => player.m_TrustPoint,
            GoodsType.chefFlame => player.m_ChefFlame,
            GoodsType.fakePoint => player.m_FakePoint,
            _ => -1
        };
    }

    private static int ProgressionRewardAction(CommonRewardType type) =>
        RewardActionBase + (int)type;

    private static int PackRewardContext(int count, RewardShowType showType) =>
        count is > 0 and <= 1_000_000 &&
        showType is >= RewardShowType.AlwaysShow and <= RewardShowType.SilentReward
            ? (count & 0x3fffffff) | ((int)showType << 30)
            : -1;

    private static bool TryUnpackRewardContext(
        int context, out int count, out RewardShowType showType)
    {
        count = context & 0x3fffffff;
        showType = (RewardShowType)((uint)context >> 30);
        return count is > 0 and <= 1_000_000 &&
            showType is >= RewardShowType.AlwaysShow and <= RewardShowType.SilentReward;
    }

    private static bool IsWalletDebitValid(GoodsType type, int delta, int balance) =>
        type is >= GoodsType.gold and <= GoodsType.fakePoint &&
        delta is < 0 and >= -1_000_000_000 && balance >= 0 && (long)balance + delta >= 0;

    private static int PackUShorts(int low, int high) =>
        (Math.Clamp(low, 0, ushort.MaxValue) & 0xffff) |
        (Math.Clamp(high, 0, ushort.MaxValue) << 16);

    private static int PackDialogueVotes(int voteScope, int hostVote, int clientVote) =>
        (Math.Clamp(voteScope, 0, ushort.MaxValue) & 0xffff) |
        (Math.Clamp(hostVote, 0, 0xff) << 16) |
        (Math.Clamp(clientVote, 0, 0x7f) << 24);

    private static bool TryUnpackDialogueVotes(
        int context, out int voteScope, out int hostVote, out int clientVote)
    {
        voteScope = context & 0xffff;
        hostVote = context >> 16 & 0xff;
        clientVote = context >> 24 & 0x7f;
        return context >= 0 && hostVote <= 0x7f;
    }

    private static int PackDialogueChoice(int voteScope, int choiceIndex) =>
        PackUShorts(voteScope, choiceIndex);

    private static bool TryUnpackDialogueChoice(
        int context, out int voteScope, out int choiceIndex)
    {
        voteScope = context & 0xffff;
        choiceIndex = (int)((uint)context >> 16);
        return context >= 0;
    }

    private static int PackDialogueScope(int nodeIndex, int messageIndex) =>
        nodeIndex is >= 0 and <= byte.MaxValue &&
        messageIndex is >= 0 and <= byte.MaxValue
            ? nodeIndex | messageIndex << 8
            : -1;

    private static int DialogueNodeFromScope(int voteScope) => voteScope & 0xff;

    private static int DialogueMessageFromScope(int voteScope) => voteScope >> 8 & 0xff;

    internal static int DialogueVoteScope(DialogueManager manager)
    {
        var nodeIndex = manager?.m_CurrentDialogueIndex ?? -1;
        var messageIndex = CurrentDialoguePanel(manager)?.m_MsgsIndex ?? 0;
        return PackDialogueScope(nodeIndex, messageIndex);
    }

    private static DialoguePanel CurrentDialoguePanel(DialogueManager manager) =>
        manager?.current?.TryCast<DialoguePanel>();

    private static bool IsFinalDialogueNode(DialogueManager manager) =>
        manager?.m_CurrentDialogues != null &&
        IsFinalDialogueNode(manager.m_CurrentDialogueIndex, manager.m_CurrentDialogues.Count);

    private static bool IsFinalDialogueNode(int index, int count) =>
        count > 0 && index >= count - 1;

    private static bool DialogueVotesMatch(int hostVote, int clientVote) =>
        hostVote != 0 && hostVote == clientVote;

    private static bool IsDialogueVoteValid(DialogueManager manager, int vote)
    {
        if (manager == null || vote <= 0 || vote > 0x7f)
            return false;
        if (vote == DialogueVoteContinue)
            return IsDialoguePanelReady(manager) && !manager.CanChoiceButtonAciton &&
                !IsFinalDialogueNode(manager);
        if (vote == DialogueVoteFinish)
            return IsDialoguePanelReady(manager) && !manager.CanChoiceButtonAciton &&
                IsFinalDialogueNode(manager);
        if (vote == DialogueVoteSkip)
            return manager.IsShowSkipButton;
        var panel = FindActiveChoicePanel(manager);
        var choice = vote - DialogueVoteChoiceBase;
        return manager.CanChoiceButtonAciton && panel != null && choice >= 0 &&
            choice < panel.CheckChoiceCount();
    }

    private static bool IsDialoguePanelReady(DialogueManager manager) =>
        CurrentDialoguePanel(manager)?.textingDone != false;

    private static bool CanApplyDialogueNode(
        DialogueManager manager, int bundleKey, int nodeIndex) =>
        manager?.IsPlaying == true && bundleKey != 0 &&
        ContentKey(manager.CurrentBundleID) == bundleKey &&
        manager.m_CurrentDialogueIndex == nodeIndex;

    private static bool CanApplyDialogueVoteScope(
        DialogueManager manager, int bundleKey, int voteScope) =>
        voteScope >= 0 && CanApplyDialogueNode(
            manager, bundleKey, DialogueNodeFromScope(voteScope)) &&
        DialogueVoteScope(manager) == voteScope;

    private static bool IsCommittedDialogueScope(
        int bundleKey, int voteScope, int committedBundleKey, int committedScope) =>
        bundleKey != 0 && bundleKey == committedBundleKey && voteScope == committedScope;

    private static bool IsDialogueScopePast(int liveScope, int receivedScope)
    {
        var liveNode = DialogueNodeFromScope(liveScope);
        var receivedNode = DialogueNodeFromScope(receivedScope);
        return liveNode > receivedNode || liveNode == receivedNode &&
            DialogueMessageFromScope(liveScope) > DialogueMessageFromScope(receivedScope);
    }

    private static bool IsLiveDialogueScopePast(
        DialogueManager manager, int bundleKey, int receivedScope)
    {
        var liveScope = DialogueVoteScope(manager);
        return liveScope >= 0 && manager?.IsPlaying == true &&
            ContentKey(manager.CurrentBundleID) == bundleKey &&
            IsDialogueScopePast(liveScope, receivedScope);
    }

    private static ChoiceDialoguePanel FindActiveChoicePanel(DialogueManager manager)
    {
        if (manager?.imageChoiceDialoguePanel?.gameObject.activeInHierarchy == true)
            return manager.imageChoiceDialoguePanel;
        if (manager?.deliveryDialoguePanel?.gameObject.activeInHierarchy == true)
            return manager.deliveryDialoguePanel;
        if (manager?.requestMissionDialoguePanel?.gameObject.activeInHierarchy == true)
            return manager.requestMissionDialoguePanel;
        if (manager?.normalChoiceDialoguePanel?.gameObject.activeInHierarchy == true)
            return manager.normalChoiceDialoguePanel;
        return manager?.choiceDialoguePanel?.gameObject.activeInHierarchy == true
            ? manager.choiceDialoguePanel
            : null;
    }

    private void SetDialogueVoteScope(int bundleKey, int voteScope)
    {
        if (_dialogueVoteBundleKey == bundleKey && _dialogueVoteScope == voteScope)
            return;
        ResetDialogueVotes();
        _dialogueVoteBundleKey = bundleKey;
        _dialogueVoteScope = voteScope;
        _log?.LogInfo(
            $"Dialogue vote scope: bundle={bundleKey:X8}; node={DialogueNodeFromScope(voteScope)}; " +
            $"msg={DialogueMessageFromScope(voteScope)}; ready=0/2");
    }

    private void ResetDialogueVotes()
    {
        RestoreDialogueVoteLabels();
        _dialogueVoteBundleKey = 0;
        _dialogueVoteScope = -1;
        _hostDialogueVote = 0;
        _clientDialogueVote = 0;
    }

    private void ResetDialogueCommit()
    {
        _committedDialogueBundleKey = 0;
        _committedDialogueScope = -1;
    }

    private int DialogueVoteCount(int vote) =>
        (_hostDialogueVote == vote ? 1 : 0) + (_clientDialogueVote == vote ? 1 : 0);

    internal void UpdateDialogueVoteLabels()
    {
        var manager = DialogueManager.Instance;
        if (manager?.IsPlaying != true || IsLocalDialogueBundle(manager.CurrentBundleID))
        {
            RestoreDialogueVoteLabels();
            return;
        }
        var bundleKey = ContentKey(manager.CurrentBundleID);
        var voteScope = DialogueVoteScope(manager);
        if (bundleKey != 0 && voteScope >= 0 &&
            !IsCommittedDialogueScope(
                bundleKey, voteScope,
                _committedDialogueBundleKey, _committedDialogueScope))
            SetDialogueVoteScope(bundleKey, voteScope);
        var panel = CurrentDialoguePanel(manager);
        if (panel != null && manager.m_UseButton && panel.text != null)
        {
            var vote = IsFinalDialogueNode(manager)
                ? DialogueVoteFinish
                : DialogueVoteContinue;
            var ready = Math.Max(DialogueVoteCount(vote), DialogueVoteCount(DialogueVoteSkip));
            var label = GetDialogueVoteLabel(panel);
            if (label != null)
            {
                label.text = $"{Math.Clamp(ready, 0, 2)}/2";
                label.gameObject.SetActive(panel.textingDone);
            }
        }
        else if (_dialogueVoteLabel != null)
            _dialogueVoteLabel.gameObject.SetActive(false);
        if (manager.CanChoiceButtonAciton)
        {
            UpdateChoiceVoteLabels(manager.imageChoiceDialoguePanel);
            UpdateChoiceVoteLabels(manager.deliveryDialoguePanel);
            UpdateChoiceVoteLabels(manager.requestMissionDialoguePanel);
            UpdateChoiceVoteLabels(manager.normalChoiceDialoguePanel);
            UpdateChoiceVoteLabels(manager.choiceDialoguePanel);
        }
    }

    private void UpdateChoiceVoteLabels(ChoiceDialoguePanel panel)
    {
        if (panel?.gameObject.activeInHierarchy != true || panel.chocieButtonPanel == null)
            return;
        for (var index = 0; index < panel.chocieButtonPanel.Count; index++)
        {
            var button = panel.chocieButtonPanel[index];
            var text = button?.focusText?.text;
            if (text != null && button.gameObject.activeInHierarchy)
                text.text = FormatDialogueVoteText(
                    text.text, DialogueVoteCount(DialogueVoteChoiceBase + index));
        }
    }

    private static string FormatDialogueVoteText(string text, int ready)
    {
        ready = Math.Clamp(ready, 0, 2);
        if (HasDialogueVoteSuffix(text) && text[^3] == '0' + ready)
            return text;
        var clean = StripDialogueVoteText(text);
        return $"{clean}  {ready}/2";
    }

    private static string StripDialogueVoteText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? string.Empty;
        return HasDialogueVoteSuffix(text) ? text[..^5] : text;
    }

    private static bool HasDialogueVoteSuffix(string text) =>
        text?.Length >= 5 && text[^5] == ' ' && text[^4] == ' ' &&
        text[^3] is >= '0' and <= '2' && text[^2] == '/' && text[^1] == '2';

    private void RestoreDialogueVoteLabels()
    {
        if (_dialogueVoteLabel != null)
            _dialogueVoteLabel.gameObject.SetActive(false);
        var manager = DialogueManager.Instance;
        RestoreChoiceVoteLabels(manager?.imageChoiceDialoguePanel);
        RestoreChoiceVoteLabels(manager?.deliveryDialoguePanel);
        RestoreChoiceVoteLabels(manager?.requestMissionDialoguePanel);
        RestoreChoiceVoteLabels(manager?.normalChoiceDialoguePanel);
        RestoreChoiceVoteLabels(manager?.choiceDialoguePanel);
    }

    private TextMeshProUGUI GetDialogueVoteLabel(DialoguePanel panel)
    {
        var parent = panel?.passIcon?.transform;
        if (parent == null)
            return null;
        if (_dialogueVoteLabelParent == parent)
            return _dialogueVoteLabel;
        if (_dialogueVoteLabel != null)
            UnityEngine.Object.Destroy(_dialogueVoteLabel.gameObject);
        _dialogueVoteLabel = null;
        _dialogueVoteLabelParent = parent;

        var source = panel.text?.textTMProUGUI;
        if (source == null)
            return null;
        GameObject gameObject = null;
        try
        {
            gameObject = new GameObject("DTMP Dialogue Vote");
            gameObject.transform.SetParent(parent, false);
            var label = gameObject.AddComponent<TextMeshProUGUI>();
            label.font = source.font;
            label.fontSharedMaterial = source.fontSharedMaterial;
            label.fontSize = Mathf.Max(18f, source.fontSize * 0.7f);
            label.fontStyle = FontStyles.Bold;
            label.color = source.color;
            label.alignment = TextAlignmentOptions.MidlineLeft;
            label.raycastTarget = false;
            label.enableWordWrapping = false;
            var rect = label.rectTransform;
            rect.anchorMin = new Vector2(1f, 0.5f);
            rect.anchorMax = new Vector2(1f, 0.5f);
            rect.pivot = new Vector2(0f, 0.5f);
            rect.anchoredPosition = new Vector2(16f, 2f);
            rect.sizeDelta = new Vector2(96f, 48f);
            _dialogueVoteLabel = label;
            _log?.LogInfo("Dialogue vote label attached to native pass icon");
            return label;
        }
        catch (Exception exception)
        {
            if (gameObject != null)
                UnityEngine.Object.Destroy(gameObject);
            _log?.LogWarning($"Dialogue vote label unavailable: {exception.Message}");
            return null;
        }
    }

    private static void RestoreChoiceVoteLabels(ChoiceDialoguePanel panel)
    {
        if (panel?.chocieButtonPanel == null)
            return;
        for (var index = 0; index < panel.chocieButtonPanel.Count; index++)
        {
            var text = panel.chocieButtonPanel[index]?.focusText?.text;
            if (text != null)
                text.text = StripDialogueVoteText(text.text);
        }
    }

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
                    _hostTimelineActiveRoute == TimelineStartRoute.ByController),
                _hostTimelineInvocation);
            PublishTimelineProgress(session);
        }
        if (_hostScenarioBundleKey != 0)
        {
            Publish(session, ManagerDomain.Scenario, ManagerAction.ScenarioStarted,
                _hostScenarioBundleKey, 0, _hostScenarioInvocation);
            if (_hostScenarioNodeId >= 0)
                Publish(session, ManagerDomain.Scenario, ManagerAction.ScenarioNode,
                    _hostScenarioBundleKey, _hostScenarioNodeId);
        }
        if (_hostDialogueBundleKey != 0)
        {
            Publish(session, ManagerDomain.Dialogue, ManagerAction.DialogueStarted,
                _hostDialogueBundleKey, 0, _hostDialogueInvocation);
            if (_hostDialogueIndex >= 0)
                Publish(session, ManagerDomain.Dialogue, ManagerAction.DialogueNode,
                    _hostDialogueBundleKey, _hostDialogueIndex);
            if (_dialogueVoteBundleKey == _hostDialogueBundleKey && _dialogueVoteScope >= 0)
                PublishDialogueVotes(session);
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

    private static bool IsLocalDialogueBundle(string bundleId) =>
        bundleId?.StartsWith("LobbyTalk", StringComparison.Ordinal) == true;

    internal static int ScenarioPreviousKey(bool wasPlaying, Func<string> readSequenceId)
    {
        if (!wasPlaying)
            return 0;
        try
        {
            return ContentKey(readSequenceId());
        }
        catch
        {
            return 0;
        }
    }

    private static TimelineStartDecision DecideTimelineStart(
        bool hasIntent, TimelineStartRoute route, bool hasController)
    {
        if (route == TimelineStartRoute.ByController && (!hasIntent || !hasController))
            return TimelineStartDecision.ConsumeSpectator;
        return route == TimelineStartRoute.ByController
            ? TimelineStartDecision.ReplayByController
            : TimelineStartDecision.ReplayByTid;
    }

    private static InvocationReplayDecision DecideInvocationReplay(
        bool localMatches, bool hasDescriptor) => localMatches
        ? InvocationReplayDecision.Local
        : hasDescriptor
            ? InvocationReplayDecision.Synthesize
            : InvocationReplayDecision.Spectate;

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

    private static bool ShouldTraceManagerEvent(ManagerEvent state) => state.Domain != 0;

    private void TraceManagerEvent(
        string stage,
        ManagerEvent state,
        ManagerDomain lane,
        uint appliedRevision,
        int queued)
    {
        if (_log == null || !ShouldTraceManagerEvent(state))
            return;
        _log.LogInfo(
            $"Manager trace: stage={stage}; lane={lane}; " +
            $"event={(ManagerDomain)state.Domain}/{(ManagerAction)state.Action}; " +
            $"revision={state.Revision}; applied={appliedRevision}; next={NextRevision(appliedRevision)}; " +
            $"queued={queued}; value={state.Value}; context={state.Context}; " +
            $"scene={state.SceneId}/{state.SceneEpoch}; hostTick={state.HostTick}; " +
            $"invocation={state.Invocation?.Kind.ToString() ?? "none"}");
    }

    private string PendingLaneSummary()
    {
        if (_pendingHostEvents.Count == 0)
            return "empty";
        var lanes = new List<string>(_pendingHostEvents.Count);
        foreach (var pair in _pendingHostEvents)
        {
            uint first = 0;
            uint last = 0;
            foreach (var revision in pair.Value.Keys)
            {
                if (first == 0)
                    first = revision;
                last = revision;
            }
            lanes.Add($"{pair.Key}:{pair.Value.Count}[{first}..{last}]");
        }
        return string.Join(",", lanes);
    }

    private string DescribeApplyState(ManagerEvent state)
    {
        try
        {
            var dialogue = DialogueManager.Instance;
            var scenario = ScenarioManager.Instance;
            var tutorial = TutorialManager.Instance;
            return
                $"dialoguePlaying={dialogue?.IsPlaying == true}; " +
                $"dialogueBundle={ContentKey(dialogue?.CurrentBundleID):X8}; " +
                $"dialogueNode={dialogue?.m_CurrentDialogueIndex ?? -1}; " +
                $"dialogueScope={DialogueVoteScope(dialogue)}; " +
                $"dialogueChoice={dialogue?.CanChoiceButtonAciton == true}; " +
                $"scenarioPlaying={scenario?.IsPlaying == true}; " +
                $"scenarioKey={ScenarioPreviousKey(
                    scenario?.IsPlaying == true, () => scenario.CurrentSequenceID):X8}; " +
                $"tutorial={tutorial?.CurrentStep.ToString() ?? "none"}; " +
                $"tutorialBundle={ContentKey(tutorial?.GetHandler()?.startDialogueID):X8}; " +
                $"pendingDialogue={_pendingClientDialogueStart?.BundleKey ?? 0:X8}; " +
                $"unityScale={Time.timeScale}; eventValue={state.Value}; eventContext={state.Context}";
        }
        catch (Exception exception)
        {
            return $"state-read-failed:{exception.Message}";
        }
    }

    private static bool IsGlobal(ManagerDomain domain) =>
        domain is ManagerDomain.Story or ManagerDomain.Day or
            ManagerDomain.Dialogue or ManagerDomain.Scenario or ManagerDomain.Progression or
            ManagerDomain.Time;

    private static bool IsLocalTimeScope(TimeScaleController.Type type) =>
        type == TimeScaleController.Type.PauseMenumAuto;

    private static bool IsActivityTerminal(ManagerDomain domain, ManagerAction action) =>
        (action is ManagerAction.Result or ManagerAction.Goal or ManagerAction.Finish) &&
        domain is ManagerDomain.MainSushi or ManagerDomain.JungleSushi or
            ManagerDomain.Betting or ManagerDomain.VipCooking or ManagerDomain.JungleMiniGame or
            ManagerDomain.Karaoke or ManagerDomain.InsectBattle or ManagerDomain.SeahorseRace;

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

    internal static bool BeginChoice(int choiceIndex, out bool suppressNested)
    {
        var behaviour = ProbeBehaviour.Instance;
        var dialogue = DialogueManager.Instance;
        if (behaviour != null && dialogue != null && choiceIndex >= 0)
        {
            var bundleKey = string.IsNullOrEmpty(dialogue.CurrentBundleID)
                ? 0
                : unchecked((int)Protocol.SceneId(dialogue.CurrentBundleID));
            var context = (Math.Clamp(
                    ManagerEventReplicator.DialogueVoteScope(dialogue),
                    0, ushort.MaxValue) & 0xffff) |
                (Math.Clamp(choiceIndex, 0, ushort.MaxValue) << 16);
            return behaviour.BeginManagerEvent(
                ManagerDomain.Dialogue, ManagerAction.DialogueChoice,
                bundleKey, context, out suppressNested);
        }
        suppressNested = false;
        return true;
    }

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

[HarmonyPatch(typeof(TutorialManager), nameof(TutorialManager.ActivateTutorial))]
internal static class PassiveClientTutorialActivationPatch
{
    private static bool Prefix() =>
        ProbeBehaviour.Instance?.AllowNativeTutorialActivation() ?? true;
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

[HarmonyPatch(typeof(DialogueManager), "ContinueDialogue")]
internal static class DialogueNativeContinueSyncPatch
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
    private static bool Prefix(out bool __state) =>
        ManagerEventPatchHelper.Begin(
            ManagerDomain.Dialogue, ManagerAction.FirstChoice, out __state);

    private static Exception Finalizer(Exception __exception, bool __state)
    {
        ManagerEventPatchHelper.End(__state);
        return __exception;
    }
}

[HarmonyPatch(typeof(DialogueManager), nameof(DialogueManager.ExcuteSecondDialogue))]
internal static class DialogueSecondChoiceSyncPatch
{
    private static bool Prefix(out bool __state) =>
        ManagerEventPatchHelper.Begin(
            ManagerDomain.Dialogue, ManagerAction.SecondChoice, out __state);

    private static Exception Finalizer(Exception __exception, bool __state)
    {
        ManagerEventPatchHelper.End(__state);
        return __exception;
    }
}

[HarmonyPatch(typeof(ChoiceDialoguePanel), nameof(ChoiceDialoguePanel.ExcuteFocusDialogue))]
internal static class DialogueFocusedChoiceSyncPatch
{
    private static bool Prefix(ChoiceDialoguePanel __instance, out bool __state) =>
        ManagerEventPatchHelper.BeginChoice(
            __instance?.m_CurButtonIndex ?? -1, out __state);

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
        var wasPlaying = __instance.IsPlaying;
        __state = new ManagerEventReplicator.ScenarioStartObservation(
            (string)__args[0], wasPlaying,
            ManagerEventReplicator.ScenarioPreviousKey(
                wasPlaying, () => __instance.CurrentSequenceID),
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
        var hasArguments = !visual && !small &&
            __originalMethod.GetParameters().Length == 5;
        var arguments = hasArguments
            ? __args[1] as Il2CppSystem.Collections.Generic.List<string>
            : null;
        var kind = visual
            ? ManagerEventReplicator.DialogueStartKind.VisualNovel
            : small
                ? ManagerEventReplicator.DialogueStartKind.Small
                : hasArguments
                    ? ManagerEventReplicator.DialogueStartKind.Arguments
                    : ManagerEventReplicator.DialogueStartKind.Normal;
        var callbackIndex = hasArguments ? 2 : 1;
        var allowed = behaviour?.InterceptDialogueStart(
            (string)__args[0],
            kind,
            arguments,
            visual ? __args[1] as Il2CppSystem.Collections.Generic.List<DialogueEntry> : null,
            visual ? __args[2] as ScenarioButtonInfo : null,
            !visual ? __args[callbackIndex] as Il2CppSystem.Action<bool> : null,
            visual ? __args[3] as Il2CppSystem.Action<int> : null,
            visual || (bool)__args[hasArguments ? 3 : 2],
            visual || (bool)__args[hasArguments ? 4 : 3]) ?? true;
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
