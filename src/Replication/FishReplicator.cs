using System;
using System.Collections.Generic;
using BepInEx.Logging;
using DR.AI;
using HarmonyLib;
using UnityEngine;

namespace DaveTheDiverMP;

internal static class FishBehaviorTreeState
{
    private static readonly Dictionary<FishAISystem, bool> States = new();

    internal static void ObserveActive(FishAISystem fish)
    {
        if (fish != null && !States.ContainsKey(fish) &&
            fish.gameObject.activeInHierarchy && fish.IsFishEnable)
            States[fish] = true;
    }

    internal static bool TryGet(FishAISystem fish, out bool enabled)
    {
        enabled = false;
        return fish != null && States.TryGetValue(fish, out enabled);
    }

    internal static void Set(FishAISystem fish, bool enabled)
    {
        fish.EnableBehaviorTree(enabled);
        States[fish] = enabled;
    }

    internal static void Forget(FishAISystem fish)
    {
        if (!ReferenceEquals(fish, null))
            States.Remove(fish);
    }
}

internal sealed class FishReplicator
{
    private const float SnapshotKeyframeSeconds = 1f;
    private const float RemoteStimulusInterval = 0.1f;
    private const int MaxFishControlPacketsPerFrame = 8;
    private const int MaxPendingHostLifecycles = 256;
    private const int ReliableCapacityReserve = 32;
    private const float ActionTimeoutSeconds = 2f;
    private const float HookLeaseSeconds = 12f;
    private const float MinHookActionIntervalSeconds = 0.05f;
    private const float MinDamageIntervalSeconds = 0.001f;
    private const float WeaponRangeSquared = 1600f;
    private const float CaptureRangeSquared = 100f;
    private const float CorpsePickupRangeSquared = 16f;
    private const int MaxQueuedActionsPerFish = 8;
    private const int ProcessedRequestWindow = 256;
    private const double SnapshotTicksPerSecond = 20d;
    private const double InterpolationTicks = 3d;
    private const double MaxExtrapolationTicks = 1.5d;
    private const int MaxSamples = 8;
    private const float InterestEnterDistance = 80f;
    private const float InterestLeaveDistance = 96f;
    private const int InterestLeaveTicks = 10;
    private const int SnapshotEntityBudget = 24;
    private const int SnapshotByteBudget = 1100;
    private const float NearSnapshotDistance = 40f;
    private const float NearSnapshotInterval = 0.05f;
    private const float MidSnapshotInterval = 0.2f;
    private const float RecentDamageSeconds = 2f;
    private const int MissingAllocatorScanGrace = 3;
    private const float LeaseCorrectionSeconds = 0.15f;
    private const float HookPoseIntervalSeconds = 0.05f;
    private const float HookPosePlayerRangeSquared = 64f;
    private const float HookPoseMaxSpeed = 40f;
    private const float RemoteThreatRefreshSeconds = 0.5f;
    private const float RemoteFearEnterRadius = 4f;
    private const float RemoteFearExitRadius = 5f;

    private readonly record struct InterestState(bool Interested, int OutsideTicks);
    private readonly record struct SnapshotCandidate(FishSnapshot Snapshot, HostFish Info, float Priority);

    private struct TimedSample
    {
        internal uint Tick;
        internal long TimeTick;
        internal float X;
        internal float Y;
        internal float Z;
        internal float VelocityX;
        internal float VelocityY;
        internal float Rotation;

        internal TimedSample(
            uint tick, float x, float y, float z,
            float velocityX, float velocityY, float rotation)
        {
            Tick = tick;
            TimeTick = tick;
            X = x;
            Y = y;
            Z = z;
            VelocityX = velocityX;
            VelocityY = velocityY;
            Rotation = rotation;
        }
    }

    private sealed class ManifestAssembly
    {
        internal readonly uint SceneId;
        internal readonly uint SceneEpoch;
        internal readonly uint Revision;
        internal readonly Dictionary<int, FishManifest> Entries = new();
        internal int Expected = -1;

        internal ManifestAssembly(uint sceneId, uint sceneEpoch, uint revision)
        {
            SceneId = sceneId;
            SceneEpoch = sceneEpoch;
            Revision = revision;
        }

        internal bool IsComplete => Expected >= 0 && Entries.Count == Expected;
    }

    private sealed class Target
    {
        internal FishAISystem Fish;
        internal SpriteRenderer Renderer;
        internal Vector3 Position;
        internal float Rotation;
        internal float Hp;
        internal byte Flags;
        internal bool HasSnapshot;
        internal string AllocatorUid;
        internal int FishDataTID;
        internal uint Revision;
        internal uint Tick;
        internal FishPhase Phase;
        internal bool Interested = true;
        internal bool NativeActive;
        internal bool NativeBehaviorEnabled;
        internal readonly List<TimedSample> Samples = new();
        internal bool AwaitingLeaseSnapshot;
        internal uint AwaitingLeaseSnapshotTick;
        internal Vector3 CorrectionFrom;
        internal float CorrectionRotation;
        internal float CorrectionStarted;
        internal bool CorrectingLeasePresentation;
        internal bool HasAppliedFlip;
        internal bool AppliedFlip;
    }

    private sealed class HostFish
    {
        internal FishAISystem Fish;
        internal SpriteRenderer Renderer;
        internal string AllocatorUid;
        internal int FishDataTID;
        internal FishSnapshot LastSnapshot;
        internal float LastSnapshotSend;
        internal bool HasSnapshot;
        internal bool ManifestQueued;
        internal uint Revision;
        internal FishPhase Phase;
        internal bool InterestKnown;
        internal bool Interested;
        internal int OutsideInterestTicks;
        internal float Priority;
        internal float LastHp;
        internal float RecentDamageUntil;
        internal int MissingScans;
        internal bool Active;
    }

    private sealed class GrantAssembly
    {
        internal readonly Dictionary<ushort, FishLootGrant> Entries = new();
        internal ushort Expected;
    }

    private sealed class ClientLootRequest
    {
        internal FishActionRequest Request;
        internal FishActionAck? Ack;
        internal float Started;
        internal float NextReplay;
        internal bool Released;
        internal bool Presented;
    }

    private sealed class ClientHookLease
    {
        internal ulong RequestId;
        internal bool HookQueued;
        internal bool Accepted;
        internal bool ReleaseQueued;
        internal bool CaptureQueued;
        internal bool CleanupDone;
        internal bool EndedPending;
        internal bool RecallResolved;
        internal bool RecallSuccess;
        internal float Expires;
        internal float NextPoseSend;
        internal uint PoseTick;

        internal ClientHookLease(ulong requestId) => RequestId = requestId;
    }

    private sealed class HostHookLease
    {
        internal readonly ulong RequestId;
        internal readonly bool BehaviorEnabled;
        internal float Expires;
        internal float LastPoseTime;
        internal FishHookPose LastPose;
        internal bool HasPose;

        internal HostHookLease(ulong requestId, float expires, bool behaviorEnabled = true)
        {
            RequestId = requestId;
            Expires = expires;
            BehaviorEnabled = behaviorEnabled;
        }
    }

    private readonly record struct ThreatDecision(bool Inside, bool Enter, bool Refresh);

    private readonly record struct PendingAction(
        int Id, FishAISystem Fish, FishAction Action, ulong LeaseId, float Expires,
        bool TimedOut = false);
    private readonly record struct QueuedAction(
        FishAction Action, int Damage, int Element, int AttackType, ulong LeaseId);

    private readonly ManualLogSource _log;
    private readonly SessionTrace _trace;
    private readonly RemoteCatchLedger _ledger;
    private readonly Dictionary<int, FishAISystem> _hostFishById = new();
    private readonly Dictionary<FishAISystem, int> _hostIdsByFish = new();
    private readonly Dictionary<int, HostFish> _hostInfoById = new();
    private readonly Dictionary<int, Target> _targets = new();
    private readonly Dictionary<FishAISystem, int> _clientIdsByFish = new();
    private readonly Dictionary<int, uint> _clientTombstones = new();
    private readonly HashSet<int> _pendingClientPickups = new();
    private readonly HashSet<FishAISystem> _hostRemovedFish = new();
    private readonly HashSet<FishAISystem> _suppressedClientFish = new();
    private readonly HashSet<int> _missingAllocatorIds = new();
    private readonly Dictionary<uint, ManifestAssembly> _clientManifestAssemblies = new();
    private readonly Dictionary<int, FishManifest> _activeClientManifest = new();
    private readonly Dictionary<int, FishManifest> _pendingClientManifests = new();
    private readonly Dictionary<int, FishLifecycle> _pendingClientLifecycles = new();
    private readonly List<(FishAISystem Fish, int Id, bool NativeActive, bool NativeBehaviorEnabled)>
        _deferredClientRemovals = new();
    private readonly Dictionary<int, float> _lastRemoteDamageById = new();
    private readonly Dictionary<int, float> _lastRemoteHookActionById = new();
    private readonly Dictionary<int, ClientHookLease> _clientHookLeases = new();
    private readonly Dictionary<int, HostHookLease> _hostHookLeases = new();
    private readonly HashSet<int> _scheduledClientHookCleanup = new();
    private readonly Dictionary<ulong, PendingAction> _pendingClientActions = new();
    private readonly Dictionary<int, ulong> _pendingClientActionByFish = new();
    private readonly Dictionary<int, Queue<QueuedAction>> _queuedClientActions = new();
    private readonly Dictionary<ulong, FishActionAck> _hostActionAcks = new();
    private readonly Dictionary<ulong, GrantAssembly> _clientLootGrants = new();
    private readonly Dictionary<ulong, FishAction> _clientGrantActions = new();
    private readonly Dictionary<ulong, int> _clientGrantFish = new();
    private readonly Dictionary<ulong, ClientLootRequest> _clientLootRequests = new();
    private readonly HashSet<ulong> _hostProcessedRequests = new();
    private readonly HashSet<int> _remoteStimulatedIds = new();
    private readonly Dictionary<int, float> _nextRemoteThreatById = new();
    private readonly Queue<FishLifecycle> _pendingHostLifecycles = new();
    private readonly List<FishSnapshot> _snapshotBuffer = new();
    private readonly List<SnapshotCandidate> _snapshotCandidates = new(SnapshotEntityBudget);
    private readonly List<SnapshotCandidate> _overdueSnapshotCandidates = new(SnapshotEntityBudget);
    private readonly List<FishAllocator> _allocatorScratch = new();
    private readonly Dictionary<FishAISystem, FishAllocator> _allocatorByFishScratch = new();
    private readonly Dictionary<string, FishAllocator> _clientAllocatorByUidScratch = new();
    private readonly Dictionary<FishAllocator, string> _allocatorUidsScratch = new();
    private readonly HashSet<string> _ambiguousAllocatorUidsScratch = new(StringComparer.Ordinal);
    private readonly HashSet<FishAISystem> _ambiguousHostFishScratch = new();
    private readonly HashSet<string> _reportedAmbiguousAllocatorUids = new(StringComparer.Ordinal);
    private readonly Dictionary<(string AllocatorUid, int FishDataTID), List<FishAISystem>>
        _availableClientFishScratch = new();
    private readonly List<FishAISystem> _staleFishScratch = new();
    private readonly List<int> _idScratch = new();
    private readonly List<uint> _revisionScratch = new();
    private readonly List<FishManifest> _manifestScratch = new();
    private readonly List<(FishAISystem Fish, int Id, bool NativeActive, bool NativeBehaviorEnabled)>
        _removalScratch = new();
    private float _nextHostScan;
    private float _nextRemoteStimulus;
    private float _nextSend;
    private bool _manifestStateQueued;
    private int _nextHostId = 1;
    private int _lastHostCount = -1;
    private bool _applyingClientPickup;
    private uint _manifestRevision;
    private uint _fishTick;
    private uint _latestClientManifestRevision;
    private float _nextTraceSummary;
    private float _nextClientBind;
    private long _latestClientTick;
    private double _clientLatestTick;
    private double _clientRenderTick;
    private bool _hasClientClock;
    private bool _applyingClientState;
    private bool _clientAuthorityActive;
    private int _lastClientHookId;
    private bool _suppressingNativeRecallOutcome;
    private int _clientSnapshotsAccepted;
    private int _clientSnapshotsRejected;
    private int _clientTeleports;
    private uint _appliedClientSceneId;
    private uint _appliedClientSceneEpoch;
    private ulong _nextClientRequestId;
    private ulong _highestHostRequestId;
    private ulong _nextHostTransactionId;
    private bool _hostTransactionalPickup;
    private int _pendingClientFishLootScopeDepth;
    private long _fishSnapshotBytes;
    private int _fishSnapshotsSent;
    private int _fishSnapshotPackets;
    private int _fishInterestEnters;
    private int _fishInterestLeaves;
    private int _fishOverdueSnapshots;
    private int _fishControlPacketsDeferred;
    private float _nextFishTrafficTrace;

    internal FishReplicator(ManualLogSource log, SessionTrace trace, RemoteCatchLedger ledger = null)
    {
        _log = log;
        _trace = trace;
        _ledger = ledger;
    }

    internal static void SelfTest()
    {
        TestUniqueAllocatorUids();
        var patchFlags = System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Static;
        var ordered = new List<TimedSample>();
        InsertSample(ordered, new TimedSample(uint.MaxValue, 0f, 0f, 0f, 0f, 0f, 0f));
        InsertSample(ordered, new TimedSample(1, 2f, 0f, 0f, 0f, 0f, 0f));
        InsertSample(ordered, new TimedSample(0, 1f, 0f, 0f, 0f, 0f, 0f));
        var bounded = new List<TimedSample>();
        for (uint tick = 1; tick <= 40; tick++)
            InsertSample(bounded, new TimedSample(tick, tick, 0f, 0f, 0f, 0f, 0f));
        var interpolation = SampleAt(
            new TimedSample(10, 0f, 0f, 0f, 10f, 0f, 350f),
            new TimedSample(20, 6f, 0f, 0f, 10f, 0f, 10f), 15d);
        var extrapolation = SampleAt(
            new TimedSample(20, 10f, 0f, 0f, 10f, 0f, 10f), default, 30d);
        var teleport = SampleAt(
            new TimedSample(10, 0f, 0f, 0f, 0f, 0f, 0f),
            new TimedSample(20, 9f, 0f, 0f, 0f, 0f, 0f), 15d);
        var assembly = new ManifestAssembly(7, 9, 11);
        assembly.Entries[1] = default;
        assembly.Expected = 2;
        var allocatorUids = new HashSet<string> { "allocator-a" };
        var fishIndex = new Dictionary<(string AllocatorUid, int FishDataTID), int>
        {
            [ManifestFishKey("allocator-a", 10)] = 1
        };
        var snappedClock = CorrectRenderClock(90d, 100d, 0.1f);
        var fastClock = CorrectRenderClock(95d, 100d, 0.1f);
        var slowClock = CorrectRenderClock(99d, 100d, 0.1f);
        var steadyClock = CorrectRenderClock(96.8d, 100d, 0.1f);
        var cappedClock = CorrectRenderClock(96d, 100d, 1f);
        var missingScans = 0;
        missingScans = NextMissingScans(missingScans, false);
        missingScans = NextMissingScans(missingScans, false);
        var retainedMissingFish = missingScans < MissingAllocatorScanGrace;
        missingScans = NextMissingScans(missingScans, false);
        var despawnedMissingFish = missingScans >= MissingAllocatorScanGrace;
        var resetMissingScans = NextMissingScans(missingScans, true);
        var liveMissingRetained = !ShouldRemoveMissingFish(false, 2);
        var liveMissingRemoved = ShouldRemoveMissingFish(false, 3);
        var destroyedMissingRemoved = ShouldRemoveMissingFish(true, 1);
        ulong requestId = 0;
        var firstRequestId = NextRequestId(ref requestId);
        requestId = ulong.MaxValue;
        var wrappedRequestId = NextRequestId(ref requestId);
        ulong highestRequestId = 0;
        var processedRequests = new HashSet<ulong>();
        var wrappedRequests = new HashSet<ulong>();
        ulong wrappedHighest = 0;
        var actionQueue = new Queue<int>();
        for (var value = 1; value <= MaxQueuedActionsPerFish + 1; value++)
            TryEnqueueBounded(actionQueue, value, MaxQueuedActionsPerFish);
        var lifecycleQueue = new Queue<FishLifecycle>();
        for (var value = 0; value < MaxPendingHostLifecycles; value++)
            lifecycleQueue.Enqueue(default);
        var lifecycleOverflowSignalled = false;
        var lifecycleQueued = TryEnqueueHostLifecycle(lifecycleQueue, default, () =>
        {
            lifecycleOverflowSignalled = true;
            lifecycleQueue.Clear();
        });
        var delayedQueue = new Queue<QueuedAction>();
        delayedQueue.Enqueue(new QueuedAction(FishAction.Damage, 1, 0, 2, 0));
        delayedQueue.Enqueue(new QueuedAction(FishAction.Damage, 2, 0, 2, 0));
        var timedActions = new Dictionary<ulong, PendingAction>
        {
            [1] = new PendingAction(7, null, FishAction.Damage, 0, 2f)
        };
        var timedActionByFish = new Dictionary<int, ulong> { [7] = 1 };
        var expiredInFlight = TryExpirePendingAction(
            timedActions, timedActionByFish, 1, 2f, out _);
        var retainedTimedOutIdentity = timedActions.Count == 1 && timedActionByFish.Count == 1 &&
            timedActions[1].TimedOut;
        var timedOutAction = timedActions[1];
        var acceptedLateAck = TryConsumeAck(timedActions, 1);
        ClearPendingFish(timedActionByFish, timedOutAction, 1);
        var pendingAcks = new HashSet<ulong> { firstRequestId };
        var clientLeases = new Dictionary<int, ClientHookLease>();
        BeginClientHookLease(clientLeases, 7, 41);
        var pendingLease = clientLeases[7];
        var pendingBeforeAck = !pendingLease.Accepted;
        var wrongHookAck = AcceptClientHookLease(clientLeases, 7, 40, 10f);
        var acceptedHookAck = AcceptClientHookLease(clientLeases, 7, 41, 10f);
        var renewedHookLease = RenewClientHookLease(clientLeases, 7, 41, 20f);
        var activeHookLease = HasActiveClientHookLease(clientLeases, 7, 21.9f);
        var expiredHookLease = HasActiveClientHookLease(clientLeases, 7, 32f);
        var hostLeases = new Dictionary<int, HostHookLease>
        {
            [7] = new HostHookLease(41, 22f)
        };
        var wrongHostRenewal = RenewHostHookLease(hostLeases, 7, 40, 20f);
        var hostRenewed = RenewHostHookLease(hostLeases, 7, 41, 20f);
        var firstCleanup = TryMarkClientHookCleanup(pendingLease);
        var duplicateCleanup = TryMarkClientHookCleanup(pendingLease);
        var releaseRejectedCleanup = new ClientHookLease(51);
        var releaseCleanup = TryMarkClientHookCleanup(releaseRejectedCleanup);
        var releaseCleanupAgain = TryMarkClientHookCleanup(releaseRejectedCleanup);
        var queueFlags = new ClientHookLease(61);
        var failedRecall = new ClientHookLease(62);
        var failedReleaseQueue = TryMarkLeaseActionQueued(
            queueFlags, FishAction.Release, false);
        var successfulReleaseQueue = TryMarkLeaseActionQueued(
            queueFlags, FishAction.Release, true);
        var failedCaptureQueue = TryMarkLeaseActionQueued(
            queueFlags, FishAction.Capture, false);
        var successfulCaptureQueue = TryMarkLeaseActionQueued(
            queueFlags, FishAction.Capture, true);
        var successfulHookQueue = TryMarkLeaseActionQueued(
            queueFlags, FishAction.Hook, true);
        var interest = UpdateInterest(false, 0, 79.9f, false);
        var heldInterest = UpdateInterest(true, 0, 96.1f, false);
        for (var tick = 1; tick < 9; tick++)
            heldInterest = UpdateInterest(heldInterest.Interested, heldInterest.OutsideTicks, 96.1f, false);
        var leftInterest = UpdateInterest(
            heldInterest.Interested, heldInterest.OutsideTicks, 96.1f, false);
        var forcedInterest = UpdateInterest(false, 9, float.PositiveInfinity, true);
        var frozenInterest = UpdateInterestForRemote(false, 7, 0f, false, false);
        var initialWithoutRemote = UpdateInterestForRemote(false, 0, 0f, false, true);
        var inactivePooled = CreateHostFish(null, "allocator", 7, false);
        var inactivePooledHidden = !inactivePooled.Active && !inactivePooled.InterestKnown &&
            !inactivePooled.Interested &&
            !ManifestInterested(BuildManifestFlags(0, inactivePooled.Interested));
        var poolActivation = UpdatePoolActive(inactivePooled, true);
        var duplicatePoolActivation = UpdatePoolActive(inactivePooled, true);
        var activatedPooledVisible = inactivePooled.Active && inactivePooled.InterestKnown &&
            inactivePooled.Interested;
        var poolDeactivation = UpdatePoolActive(inactivePooled, false);
        var duplicatePoolDeactivation = UpdatePoolActive(inactivePooled, false);
        var lateNativeReactivationHidden = ShouldHideClientTarget(false, true);
        var sampleBracket = new List<TimedSample>();
        for (uint tick = 1; tick <= 12; tick++)
            InsertSample(sampleBracket, new TimedSample(tick, tick, 0f, 0f, 0f, 0f, 0f));
        PruneSamples(sampleBracket, 9.5d);
        if (!renewedHookLease || !activeHookLease || expiredHookLease ||
            wrongHostRenewal || !hostRenewed || hostLeases[7].Expires != 32f)
            throw new InvalidOperationException("Fish hook lease renewal failed");
        if (ShouldAllowProxyWrite(true, false, true) ||
            !ShouldAllowProxyWrite(true, true, true) ||
            !ShouldAllowProxyWrite(true, false, false) ||
            ShouldAllowSimulation(true, false, true, false) ||
            !ShouldAllowSimulation(true, false, true, true) ||
            !ShouldAllowSimulation(true, true, true, false) ||
            !ShouldAllowSimulation(false, false, true, false))
            throw new InvalidOperationException("Fish client proxy authority gate failed");
        if (!AllocatorUidMatches("A02/FishAllocator/3", "A02/FishAllocator/3") ||
            AllocatorUidMatches("A02/FishAllocator/3", "A02/FishAllocator/4") ||
            FishTidFromItem(1_011_004) != 2_010_004 || FishTidFromItem(10) != 0 ||
            ordered.Count != 3 || ordered[0].Tick != uint.MaxValue || ordered[1].Tick != 0 ||
            ordered[2].Tick != 1 || bounded.Count != MaxSamples || bounded[0].Tick != 33 ||
            !IsNewer(1, uint.MaxValue) || IsNewer(uint.MaxValue, 1) ||
            MathF.Abs(interpolation.X - 3f) > 0.001f ||
            MathF.Abs(Mathf.DeltaAngle(interpolation.Rotation, 0f)) > 0.001f ||
            MathF.Abs(extrapolation.X -
                (10f + 10f * (float)(MaxExtrapolationTicks / SnapshotTicksPerSecond))) > 0.001f ||
            CanActivateManifest(assembly, 5) || AddManifestTestEntry(assembly, 2) != 2 ||
            !CanActivateManifest(assembly, 5) || CanActivateManifest(assembly, 12) ||
            !RemovalWins(20, 20) || !RemovalWins(21, 20) || RemovalWins(19, 20) ||
             !ManifestEntryEligible(false) || ManifestEntryEligible(true) ||
            !ShouldSuppressAllocator("allocator-a", allocatorUids) ||
            ShouldSuppressAllocator("allocator-b", allocatorUids) ||
            !fishIndex.ContainsKey(("allocator-a", 10)) ||
            fishIndex.ContainsKey(("allocator-a", 11)) ||
            !ManifestRetryDue(10f, 10f) || ManifestRetryDue(9.99f, 10f) ||
            !SceneScopeChanged(7, 9, 7, 10) || SceneScopeChanged(7, 9, 7, 9) ||
            Math.Abs(snappedClock - 97d) > 0.001d ||
             Math.Abs(fastClock - 97.2d) > 0.001d ||
             Math.Abs(slowClock - 100.8d) > 0.001d ||
             Math.Abs(steadyClock - 98.8d) > 0.001d ||
             Math.Abs(cappedClock - 101.5d) > 0.001d ||
             !retainedMissingFish || !despawnedMissingFish || resetMissingScans != 0 ||
             !liveMissingRetained || !liveMissingRemoved || !destroyedMissingRemoved)
            throw new InvalidOperationException(
                $"Fish snapshot interpolation failed: ordered={ordered.Count} bounded={bounded.Count} " +
                $"interp={interpolation.X} extrap={extrapolation.X} teleport={teleport.X} " +
                $"clock={snappedClock}/{fastClock}/{slowClock}");
        if (
            firstRequestId != 1 || wrappedRequestId != 1 ||
            !TryMarkProcessedRequest(processedRequests, ref highestRequestId, 10, 4) ||
            !TryMarkProcessedRequest(processedRequests, ref highestRequestId, 8, 4) ||
            TryMarkProcessedRequest(processedRequests, ref highestRequestId, 8, 4) ||
            TryMarkProcessedRequest(processedRequests, ref highestRequestId, 5, 4) ||
            !RequestIsNewer(1, ulong.MaxValue) ||
            !TryMarkProcessedRequest(wrappedRequests, ref wrappedHighest, ulong.MaxValue, 4) ||
            !TryMarkProcessedRequest(wrappedRequests, ref wrappedHighest, 1, 4) ||
            actionQueue.Count != MaxQueuedActionsPerFish || actionQueue.Peek() != 1 ||
            lifecycleQueued || !lifecycleOverflowSignalled || lifecycleQueue.Count != 0 ||
            delayedQueue.Count != 2 || delayedQueue.Peek().Damage != 1 ||
            !expiredInFlight || !retainedTimedOutIdentity || !acceptedLateAck ||
            timedActions.Count != 0 || timedActionByFish.Count != 0 ||
            BoundDamage(0) != 0 || BoundDamage(20_000) != 10_000 ||
            !RevisionMatches(9, 9) || RevisionMatches(8, 9) || RevisionMatches(10, 9))
            throw new InvalidOperationException("Fish request ordering failed");
        if (
            !TryConsumeAck(pendingAcks, firstRequestId) ||
            TryConsumeAck(pendingAcks, firstRequestId) ||
            !pendingBeforeAck || wrongHookAck || !acceptedHookAck ||
            !firstCleanup || duplicateCleanup ||
            !releaseCleanup || releaseCleanupAgain ||
            failedReleaseQueue || !successfulReleaseQueue || !queueFlags.ReleaseQueued ||
            failedCaptureQueue || !successfulCaptureQueue || !queueFlags.CaptureQueued ||
             !successfulHookQueue || !queueFlags.HookQueued ||
             !MarkHookEndedPending(queueFlags) || !queueFlags.EndedPending ||
             !ResolveHookRecall(queueFlags, true) || !queueFlags.RecallSuccess ||
             ResolveHookRecall(queueFlags, false) ||
             !ResolveHookRecall(failedRecall, false) || failedRecall.RecallSuccess ||
             ShouldApplyAfterPresentation(true, true, true, false) ||
             ShouldApplyAfterPresentation(true, true, false, true) ||
             !ShouldApplyAfterPresentation(true, true, false, false) ||
            !interest.Interested || interest.OutsideTicks != 0 ||
            !heldInterest.Interested || heldInterest.OutsideTicks != 9 ||
            leftInterest.Interested || leftInterest.OutsideTicks != 10 ||
            !forcedInterest.Interested || forcedInterest.OutsideTicks != 0 ||
            frozenInterest.Interested || frozenInterest.OutsideTicks != 7 ||
             !initialWithoutRemote.Interested || initialWithoutRemote.OutsideTicks != 0 ||
             !inactivePooledHidden || !activatedPooledVisible ||
             poolActivation != FishLifecycleKind.InterestEnter || duplicatePoolActivation != null ||
             poolDeactivation != FishLifecycleKind.InterestLeave ||
             duplicatePoolDeactivation != null || inactivePooled.Active ||
             inactivePooled.InterestKnown || inactivePooled.Interested ||
             !lateNativeReactivationHidden || ShouldHideClientTarget(true, true) ||
             ShouldHideClientTarget(true, false) || ShouldHideClientTarget(false, false) ||
             FishControlSendBudget(256) != 8 || FishControlSendBudget(39) != 7 ||
            FishControlSendBudget(32) != 0 ||
            !SnapshotDeadlineReached(10f, 11f) || SnapshotDeadlineReached(10f, 10.99f) ||
            !ManifestInterested(BuildManifestFlags(4, true)) ||
            ManifestInterested(BuildManifestFlags(4, false)) ||
            sampleBracket.Count != 4 || sampleBracket[0].Tick != 9 ||
            sampleBracket[^1].Tick != 12 ||
            !LeaseIdentityMatches(FishAction.Hook, 0, 0) ||
            !LeaseIdentityMatches(FishAction.Qte, 41, 41) ||
            LeaseIdentityMatches(FishAction.Qte, 41, 40) ||
            !ShouldCancelLeaseOnReject(FishAction.Qte, true) ||
            !ShouldCancelLeaseOnReject(FishAction.Release, true) ||
            !ShouldCancelLeaseOnReject(FishAction.Capture, true) ||
            ShouldCancelLeaseOnReject(FishAction.Damage, true) ||
            ActionRangeSquared(FishAction.Hook) != 1600f ||
            ActionRangeSquared(FishAction.Damage) != 1600f ||
            ActionRangeSquared(FishAction.Capture) >= 1600f ||
            ActionRangeSquared(FishAction.Qte).HasValue ||
            ActionRangeSquared(FishAction.Release).HasValue ||
            !HostActionStateValid(FishAction.Hook, FishPhase.Alive, false) ||
            HostActionStateValid(FishAction.Hook, FishPhase.Hooked, true) ||
            !HostActionStateValid(FishAction.Qte, FishPhase.Hooked, true) ||
            HostActionStateValid(FishAction.Qte, FishPhase.Hooked, false) ||
            !HostActionStateValid(FishAction.Capture, FishPhase.Qte, true) ||
            !HostActionStateValid(FishAction.CorpsePickup, FishPhase.Corpse, false) ||
            HostActionStateValid(FishAction.CorpsePickup, FishPhase.Alive, false) ||
            !HostActionStateValid(FishAction.Release, FishPhase.Hooked, true) ||
            HostActionStateValid(FishAction.Release, FishPhase.Alive, false) ||
            typeof(FishAddDropLootTracePatch).GetMethod("Postfix", patchFlags) != null)
            throw new InvalidOperationException("Fish action state self-test failed");

        var grantSet = new[]
        {
            new FishLootGrant(9, 7, 8, 10, 11, 12, FishAction.Capture, 0, 2, 101, 1, 2, 3, 4f),
            new FishLootGrant(9, 7, 8, 10, 11, 12, FishAction.Capture, 1, 2, 102, 2, 2, 3, 4f)
        };
        var manifestTarget = new Target();
        ApplyManifestBaseline(manifestTarget, new FishManifest(
            1, 2, 3, 4, 5, "allocator", 6, 7f, 8f, 9f, 10f, 11f, 4));
        var committed = new CommittedFishLoot
        {
            Ack = new FishActionAck(
                10, 7, 8, 11, 12, FishAction.Capture, FishActionResult.Accepted,
                FishActionRejectReason.None, 0f, FishPhase.Captured),
            Grants = new List<FishLootGrant>(grantSet)
        };
        var enqueueCount = 0;
        var partialSend = TrySendCommittedFishLoot(
            committed, 9, 9, _ => ++enqueueCount < 2, _ => true);
        var epochAfterPartial = committed.LastSentSessionId;
        var ackFailure = TrySendCommittedFishLoot(committed, 9, 9, _ => true, _ => false);
        var epochAfterAckFailure = committed.LastSentSessionId;
        var completeSend = TrySendCommittedFishLoot(committed, 9, 9, _ => true, _ => true);
        if (!RemoteCatchLedger.ExactGrantSet(grantSet) ||
            RemoteCatchLedger.ExactGrantSet(new[] { grantSet[1], grantSet[0] }) ||
            manifestTarget.Samples.Count != 0 || manifestTarget.Tick != 0 ||
            manifestTarget.Interested ||
            manifestTarget.HasSnapshot || manifestTarget.Revision != 5 ||
            partialSend || epochAfterPartial != 0 || ackFailure || epochAfterAckFailure != 0 ||
            !completeSend ||
            committed.LastSentSessionId != 9)
            throw new InvalidOperationException("Fish loot exact entry matching failed");
        var pose = new FishHookPose(1, 2, 7, 41, 10, 3f, 4f, 0f, 90f, 2f, 1f);
        if (!HookPoseIdentityValid(pose, 1, 2, 7, 41) ||
            HookPoseIdentityValid(pose, 1, 2, 7, 42) ||
            !HookPoseWithinPlayer(pose, new PlayerSnapshot(1, 2, 0f, 0f, 0f, 0f, 0f, 0f, 0, 1f, 1f, false)) ||
            HookPoseWithinPlayer(pose with { X = 8.01f, Y = 0f }, new PlayerSnapshot(1, 2, 0f, 0f, 0f, 0f, 0f, 0f, 0, 1f, 1f, false)) ||
            !HookPoseMotionPlausible(pose, pose with { Tick = 11, X = 5f }, 0.05f) ||
            HookPoseMotionPlausible(pose, pose with { Tick = 11, X = 5.1f }, 0.05f) ||
            HookPoseMotionPlausible(pose, pose with { Tick = 10 }, 1f) ||
            !HookPoseFromCurrentPlausible(new Vector3(1f, 4f, 0f), pose, 0.05f) ||
            HookPoseFromCurrentPlausible(new Vector3(0.9f, 4f, 0f), pose, 0.05f))
            throw new InvalidOperationException("Fish hook pose policy failed");
        var fearEnter = RemoteThreatPolicy(false, 0.7f, 3.9f, false, 1f, 0f);
        var fearHeld = RemoteThreatPolicy(false, 0.7f, 4.5f, true, 1.2f, 1.5f);
        var fearRefresh = RemoteThreatPolicy(false, 0.7f, 4.5f, true, 1.5f, 1.5f);
        var fearExit = RemoteThreatPolicy(false, 0.7f, 5.1f, true, 1.6f, 1.5f);
        var aggressiveOutside = RemoteThreatPolicy(true, 0.7f, 0.8f, false, 1f, 0f);
        if (!fearEnter.Inside || !fearEnter.Enter || !fearEnter.Refresh || fearHeld.Enter ||
            !fearHeld.Inside || fearHeld.Refresh ||
            fearRefresh.Enter || !fearRefresh.Refresh ||
            fearExit.Inside || aggressiveOutside.Inside)
            throw new InvalidOperationException("Remote fish threat policy failed");
        var ended = 0;
        var leaseTest = new Dictionary<int, HostHookLease>
        {
            [7] = new HostHookLease(41, 2f, true)
        };
        if (!TryEndHostHookLease(leaseTest, 7, _ => ended++) ||
            TryEndHostHookLease(leaseTest, 7, _ => ended++) || ended != 1)
            throw new InvalidOperationException("Fish hook lease restore-once policy failed");
        if (!ShouldSuppressPendingClientFishLootPolicy(false, true, true, true, false) ||
            !ShouldSuppressPendingClientFishLootPolicy(true, false, false, false, false) ||
            ShouldSuppressPendingClientFishLootPolicy(false, true, false, true, true) ||
            ShouldSuppressPendingClientFishLootPolicy(false, false, true, true, true))
            throw new InvalidOperationException("Pending client fish loot policy failed");
    }

    private static void TestUniqueAllocatorUids()
    {
        var unique = new Dictionary<string, object>(StringComparer.Ordinal);
        var duplicates = new HashSet<string>(StringComparer.Ordinal);
        var first = new object();
        var second = new object();
        AddUniqueAllocatorUid(unique, duplicates, "allocator-a", first);
        AddUniqueAllocatorUid(unique, duplicates, "allocator-b", second);
        AddUniqueAllocatorUid(unique, duplicates, "allocator-a", new object());
        AddUniqueAllocatorUid(unique, duplicates, "allocator-a", new object());
        AddUniqueAllocatorUid(unique, duplicates, " ", new object());
        if (unique.Count != 1 || !unique.TryGetValue("allocator-b", out var owner) ||
            !ReferenceEquals(owner, second) || !duplicates.SetEquals(new[] { "allocator-a" }))
            throw new InvalidOperationException("Fish allocator unique-ID policy failed");
    }

    internal void Update(
        SessionRole role,
        UdpSession session,
        uint sceneId,
        float now,
        float deltaTime,
        PlayerCharacter hostPlayer,
        Transform remotePlayerTransform)
    {
        if (role == SessionRole.Host)
        {
            _clientLootRequests.Clear();
            _clientLootGrants.Clear();
            _clientGrantActions.Clear();
            _clientGrantFish.Clear();
            if (_clientHookLeases.Count > 0 || _pendingClientActions.Count > 0 ||
                _queuedClientActions.Count > 0)
            {
                ReleaseClientTargets();
                ResetClientManifest();
            }
            _clientAuthorityActive = false;
            while (session.TryTakeFishSnapshot(out _))
            {
            }
            while (session.TryTakeFishRemoved(out _))
            {
            }
            while (session.TryTakeFishPickupResult(out _))
            {
            }
            while (session.TryTakeFishManifest(out _))
            {
            }
            while (session.TryTakeFishManifestState(out _))
            {
            }
            while (session.TryTakeFishLifecycle(out _))
            {
            }
            while (session.TryTakeFishActionAck(out _))
            {
            }
            while (session.TryTakeFishLootGrant(out _))
            {
            }
            UpdateHost(session, sceneId, now, hostPlayer, remotePlayerTransform);
            return;
        }

        while (session.TryTakeFishDamageRequest(out _))
        {
        }
        while (session.TryTakeFishPickupRequest(out _))
        {
        }
        if (_hostHookLeases.Count > 0 || _hostActionAcks.Count > 0 ||
            _hostProcessedRequests.Count > 0)
            ResetHostActions();

        if (role != SessionRole.Client || !session.SceneMatches(sceneId))
        {
            _clientAuthorityActive = false;
            while (session.TryTakeFishSnapshot(out _))
            {
            }
            while (session.TryTakeFishRemoved(out _))
            {
            }
            while (session.TryTakeFishPickupResult(out _))
            {
            }
            while (session.TryTakeFishManifest(out _))
            {
            }
            while (session.TryTakeFishManifestState(out _))
            {
            }
            while (session.TryTakeFishLifecycle(out _))
            {
            }
            while (session.TryTakeFishActionAck(out _))
            {
            }
            while (session.TryTakeFishLootGrant(out _))
            {
            }
            while (session.TryTakeFishActionRequest(out _))
            {
            }
            ReleaseClientTargets();
            ResetClientManifest();
            return;
        }

        _clientAuthorityActive = true;
        UpdateClient(session, sceneId, session.RemoteSceneEpoch, now, deltaTime, hostPlayer);
    }

    private void UpdateClient(
        UdpSession session,
        uint sceneId,
        uint sceneEpoch,
        float now,
        float deltaTime,
        PlayerCharacter player)
    {
        foreach (var requestId in new List<ulong>(_clientLootRequests.Keys))
            if (_clientLootRequests[requestId].Request.SceneId != sceneId)
            {
                _clientLootRequests.Remove(requestId);
                _clientLootGrants.Remove(requestId);
                _clientGrantActions.Remove(requestId);
                _clientGrantFish.Remove(requestId);
            }
        if (SceneScopeChanged(
                _appliedClientSceneId, _appliedClientSceneEpoch, sceneId, sceneEpoch))
        {
            ReleaseClientTargets();
            ResetClientManifest();
            _appliedClientSceneId = sceneId;
            _appliedClientSceneEpoch = sceneEpoch;
            _trace?.Write("FISH-EPOCH", $"scene={sceneId:X8} epoch={sceneEpoch}");
        }
        while (session.TryTakeFishManifest(out var manifest))
            ReceiveClientManifest(sceneId, sceneEpoch, manifest);
        while (session.TryTakeFishManifestState(out var state))
            ReceiveClientManifestState(sceneId, sceneEpoch, state);
        while (session.TryTakeFishLifecycle(out var lifecycle))
            ApplyClientLifecycle(sceneId, sceneEpoch, lifecycle);
        while (session.TryTakeFishRemoved(out var removed))
            ApplyClientRemoval(sceneId, sceneEpoch, removed.Id, removed.Revision, "removed");
        while (session.TryTakeFishSnapshot(out var snapshot))
            ApplyClientSnapshot(sceneId, sceneEpoch, snapshot);
        while (session.TryTakeFishPickupResult(out var result))
        {
            if (result.SceneId == sceneId && result.SceneEpoch == sceneEpoch)
                ApplyClientPickupResult(result, player);
        }
        while (session.TryTakeFishActionAck(out var ack))
            ApplyClientActionAck(session, sceneId, sceneEpoch, now, ack, player);
        while (session.TryTakeFishLootGrant(out var grant))
            ApplyClientLootGrant(session, sceneId, sceneEpoch, grant, player);
        while (session.TryTakeFishLootComplete(out _))
        {
        }
        UpdateClientLootRequests(session, sceneId, sceneEpoch, now, player);
        ExpireClientActions(session, sceneId, now);

        if (RetryPendingClientManifests(now) && _latestClientManifestRevision != 0)
            SuppressUnboundClientFish();
        AdvanceClientClock(deltaTime);
        EnforceClientVisibility();
        WriteClientSummary(now);
    }

    private void EnforceClientVisibility()
    {
        foreach (var pair in _targets)
        {
            var target = pair.Value;
            var fish = target.Fish;
            if (fish == null || !ShouldHideClientTarget(
                    target.Interested, fish.gameObject.activeInHierarchy))
                continue;
            fish.gameObject.SetActive(false);
            _trace?.Write("INTEREST-REHIDE", $"id={pair.Key} revision={target.Revision}");
        }
    }

    internal bool RequestDamage(
        UdpSession session,
        uint sceneId,
        FishAISystem fish,
        int damage,
        EElement element,
        AttackType attackType)
    {
        if (fish == null || damage <= 0 || !IsPlayerAttack(attackType) ||
            !Enum.IsDefined(typeof(EElement), element))
            return false;
        if (!_clientIdsByFish.TryGetValue(fish, out var id))
        {
            _trace?.Write("DAMAGE-BLOCK",
                $"unmapped type={fish.FishDataTID} instance={fish.GetInstanceID()} " +
                $"damage={damage} attack={attackType}");
            return false;
        }
        if (!_targets.TryGetValue(id, out var target) || target.Revision == 0)
            return false;
        var action = attackType == AttackType.QTE_Damage ? FishAction.Qte : FishAction.Damage;
        if (action == FishAction.Qte &&
            !HasActiveClientHookLease(_clientHookLeases, id, Time.realtimeSinceStartup))
            return false;
        var queued = EnqueueClientAction(
            session, sceneId, id, fish, action, BoundDamage(damage), (int)element,
            action == FishAction.Qte ? 0 : (int)attackType, Time.realtimeSinceStartup);
        if (!queued && action == FishAction.Qte)
            CancelClientHook(session, sceneId, id, fish, Time.realtimeSinceStartup);
        return queued;
    }

    internal void ObserveClientHook(
        UdpSession session, uint sceneId, FishAISystem fish)
    {
        if (fish == null || !_clientIdsByFish.TryGetValue(fish, out var id) ||
            _clientHookLeases.ContainsKey(id))
            return;
        BeginClientHookLease(_clientHookLeases, id, 0);
        _lastClientHookId = id;
        var queued = EnqueueClientAction(
                session, sceneId, id, fish, FishAction.Hook, 0, 0, 0,
                Time.realtimeSinceStartup);
        if (!TryMarkLeaseActionQueued(_clientHookLeases[id], FishAction.Hook, queued))
        {
            ScheduleClientHookCleanup(id);
        }
    }

    internal void ObserveClientRelease(
        UdpSession session, uint sceneId, FishAISystem fish)
    {
        if (_applyingClientState || fish == null ||
            !_clientIdsByFish.TryGetValue(fish, out var id) ||
            !_clientHookLeases.TryGetValue(id, out var lease) || lease.CleanupDone)
            return;
        if (MarkHookEndedPending(lease))
        {
            _suppressingNativeRecallOutcome = true;
            _trace?.Write("HOOK-END-PENDING", $"id={id} lease={lease.RequestId}");
        }
    }

    internal void BeginClientHarpoonRecall(
        UdpSession session, uint sceneId, bool isSuccess)
    {
        if (!_clientAuthorityActive)
            return;
        _suppressingNativeRecallOutcome = true;
        if (_lastClientHookId == 0 ||
            !_clientHookLeases.TryGetValue(_lastClientHookId, out var lease) ||
            !ResolveHookRecall(lease, isSuccess))
            return;
        var fish = FindClientFish(_lastClientHookId);
        if (fish == null)
            return;
        var action = isSuccess ? FishAction.Capture : FishAction.Release;
        var queued = EnqueueClientAction(
            session, sceneId, _lastClientHookId, fish, action, 0, 0, 0,
            Time.realtimeSinceStartup);
        TryMarkLeaseActionQueued(lease, action, queued);
        _trace?.Write("HARPOON-OUTCOME",
            $"id={_lastClientHookId} lease={lease.RequestId} success={isSuccess} queued={queued}");
        if (!queued)
            CancelClientHook(
                session, sceneId, _lastClientHookId, fish, Time.realtimeSinceStartup);
    }

    internal void EndClientHarpoonRecall() => _suppressingNativeRecallOutcome = false;

    internal void ResetClientHarpoon(
        UdpSession session, uint sceneId)
    {
        _suppressingNativeRecallOutcome = false;
        if (_lastClientHookId == 0 ||
            !_clientHookLeases.TryGetValue(_lastClientHookId, out var lease) ||
            lease.RecallResolved)
            return;
        var fish = FindClientFish(_lastClientHookId);
        if (fish != null)
            CancelClientHook(
                session, sceneId, _lastClientHookId, fish, Time.realtimeSinceStartup);
        _lastClientHookId = 0;
    }

    internal bool SuppressingNativeRecallOutcome => _suppressingNativeRecallOutcome;

    internal bool HasPendingClientCapture(FishAISystem fish)
    {
        if (fish == null || !_clientIdsByFish.TryGetValue(fish, out var id))
            return false;
        if (_clientHookLeases.TryGetValue(id, out var lease) && lease.CaptureQueued)
            return true;
        foreach (var request in _clientLootRequests.Values)
            if (!request.Released && request.Request.Id == id &&
                request.Request.Action == FishAction.Capture)
                return true;
        return false;
    }

    internal bool BeginPendingClientFishLootScope(FishAISystem fish)
    {
        if (fish == null || !_clientIdsByFish.TryGetValue(fish, out var id) ||
            !_clientHookLeases.TryGetValue(id, out var lease) ||
            !lease.EndedPending && !lease.CaptureQueued && !HasClientLootRequestForFish(id))
            return false;
        _pendingClientFishLootScopeDepth++;
        return true;
    }

    internal void EndPendingClientFishLootScope()
    {
        if (_pendingClientFishLootScopeDepth > 0)
            _pendingClientFishLootScopeDepth--;
    }

    internal bool ShouldSuppressPendingClientFishLoot(int itemId)
    {
        var matchingFish = false;
        var endedPending = false;
        var capturePending = false;
        if (_lastClientHookId != 0 &&
            _clientHookLeases.TryGetValue(_lastClientHookId, out var lease))
        {
            endedPending = lease.EndedPending;
            capturePending = lease.CaptureQueued || HasClientLootRequestForFish(_lastClientHookId);
            var fishTid = FishTidFromItem(itemId);
            matchingFish = fishTid > 0 &&
                (_targets.TryGetValue(_lastClientHookId, out var target)
                    ? target.FishDataTID == fishTid
                    : FindClientFish(_lastClientHookId)?.FishDataTID == fishTid);
        }
        return ShouldSuppressPendingClientFishLootPolicy(
            _pendingClientFishLootScopeDepth > 0, _suppressingNativeRecallOutcome,
            matchingFish, endedPending, capturePending);
    }

    internal bool RequestClientCapture(
        UdpSession session, uint sceneId, FishAISystem fish)
    {
        if (fish == null || !_clientIdsByFish.TryGetValue(fish, out var id))
            return true;
        if (!HasActiveClientHookLease(_clientHookLeases, id, Time.realtimeSinceStartup))
        {
            CleanupClientHook(id, fish);
            return false;
        }
        if (HasClientLootRequestForFish(id))
            return false;
        var lease = _clientHookLeases[id];
        if (lease.CaptureQueued)
            return false;
        var queued = EnqueueClientAction(
            session, sceneId, id, fish, FishAction.Capture, 0, 0, 0,
            Time.realtimeSinceStartup);
        if (!TryMarkLeaseActionQueued(lease, FishAction.Capture, queued))
            CancelClientHook(session, sceneId, id, fish, Time.realtimeSinceStartup);
        return false;
    }

    private bool EnqueueClientAction(
        UdpSession session,
        uint sceneId,
        int id,
        FishAISystem fish,
        FishAction action,
        int damage,
        int element,
        int attackType,
        float now)
    {
        if (!_queuedClientActions.TryGetValue(id, out var queue))
        {
            queue = new Queue<QueuedAction>();
            _queuedClientActions[id] = queue;
        }
        var leaseId = action is FishAction.Qte or FishAction.Release or FishAction.Capture &&
            _clientHookLeases.TryGetValue(id, out var lease) ? lease.RequestId : 0;
        if (!TryEnqueueBounded(queue, new QueuedAction(
                action, damage, element, attackType, leaseId),
                MaxQueuedActionsPerFish))
            return false;
        TrySendNextClientAction(session, sceneId, id, fish, now);
        return true;
    }

    private bool TrySendNextClientAction(
        UdpSession session, uint sceneId, int id, FishAISystem fish, float now)
    {
        if (_pendingClientActionByFish.ContainsKey(id) ||
            !_queuedClientActions.TryGetValue(id, out var queue))
            return false;
        if (queue.Count == 0 || !_targets.TryGetValue(id, out var target) || target.Revision == 0)
        {
            _queuedClientActions.Remove(id);
            return false;
        }
        var queued = queue.Peek();
        var requestId = NextRequestId(ref _nextClientRequestId);
        var request = new FishActionRequest(
            requestId, sceneId, session.RemoteSceneEpoch, id, target.Revision, queued.LeaseId,
            queued.Action, queued.Damage, queued.Element, queued.AttackType);
        if (!session.SendFishActionRequest(request))
            return false;
        if (queued.Action is FishAction.Capture or FishAction.CorpsePickup)
        {
            _clientLootRequests[requestId] = new ClientLootRequest
            {
                Request = request,
                Started = now,
                NextReplay = now + ActionTimeoutSeconds
            };
            _clientGrantActions[requestId] = queued.Action;
            _clientGrantFish[requestId] = id;
        }
        queue.Dequeue();
        if (queue.Count == 0)
            _queuedClientActions.Remove(id);
        _pendingClientActions[requestId] = new PendingAction(
            id, fish, queued.Action, queued.LeaseId, now + ActionTimeoutSeconds);
        _pendingClientActionByFish[id] = requestId;
        if (queued.Action == FishAction.Hook)
        {
            if (_clientHookLeases.TryGetValue(id, out var lease))
            {
                if (lease.RequestId == 0)
                    lease.RequestId = requestId;
            }
            else
                BeginClientHookLease(_clientHookLeases, id, requestId);
        }
        _trace?.Write("FISH-ACTION-SEND",
            $"request={requestId} id={id} revision={target.Revision} action={queued.Action} " +
            $"damage={queued.Damage} element={queued.Element} attack={queued.AttackType}");
        return true;
    }

    internal void ObserveClientDamage(
        UdpSession session,
        uint sceneId,
        FishAISystem fish,
        AttackData attackData)
    {
        if (fish == null || attackData == null || !IsPlayerAttack(attackData.attackType))
            return;
        if (!IsClientProxy(fish))
        {
            _trace?.Write("DAMAGE-BLOCK",
                $"unmapped OnTakeDamage type={fish.FishDataTID} instance={fish.GetInstanceID()}");
            return;
        }
        var sent = RequestDamage(
            session, sceneId, fish, attackData.damage,
            attackData.element, attackData.attackType);
        if (!sent)
            _trace?.Write("DAMAGE-BLOCK", $"request-send-failed type={fish.FishDataTID}");
    }

    internal bool ShouldAllowClientDamageWrite(FishAISystem fish) =>
        ShouldAllowProxyWrite(_clientAuthorityActive, _applyingClientState, IsClientProxy(fish));

    internal bool ShouldAllowDirectClientProxyWrite(FishAISystem fish) =>
        ShouldAllowProxyWrite(_clientAuthorityActive, _applyingClientState, IsClientProxy(fish));

    internal bool ShouldAllowClientSimulation(FishAISystem fish) =>
        ShouldAllowSimulation(
            _clientAuthorityActive, _applyingClientState, IsClientProxy(fish),
            fish != null && _clientIdsByFish.TryGetValue(fish, out var id) &&
            HasActiveClientHookLease(_clientHookLeases, id, Time.realtimeSinceStartup));

    internal bool ApplyingClientState => _applyingClientState;

    internal bool ClientAuthorityActive => _clientAuthorityActive;

    internal bool RequestPickup(UdpSession session, uint sceneId, FishAISystem fish)
    {
        if (fish == null || !_clientIdsByFish.TryGetValue(fish, out var id))
        {
            _trace?.Write("PICKUP-BLOCK",
                fish == null ? "fish=null" :
                $"unmapped type={fish.FishDataTID} instance={fish.GetInstanceID()}");
            return false;
        }
        if (_pendingClientActionByFish.ContainsKey(id))
            return true;
        if (HasClientLootRequestForFish(id))
            return true;
        if (!_targets.TryGetValue(id, out var target) || target.Revision == 0)
            return false;
        var queued = EnqueueClientAction(
            session, sceneId, id, fish, FishAction.CorpsePickup, 0, 0, 0,
            Time.realtimeSinceStartup);
        _trace?.Write(queued ? "PICKUP-SEND" : "PICKUP-BLOCK",
            $"id={id} type={fish.FishDataTID}");
        return queued;
    }

    internal bool ApplyingClientPickup => _applyingClientPickup;

    internal void ObserveHostPickup(UdpSession session, uint sceneId, FishAISystem fish)
    {
        if (fish == null || _hostTransactionalPickup)
            return;
        _hostRemovedFish.Add(fish);
        if (!_hostIdsByFish.TryGetValue(fish, out var id))
            return;
        var revision = SendHostDespawn(session, sceneId, id);
        RemoveHostFish(id);
        _log.LogInfo($"Network host fish pickup completed: id={id}");
        _trace?.Write("HOST-PICKUP", $"id={id} type={fish.FishDataTID}");
    }

    internal bool IsClientProxy(FishAISystem fish) =>
        fish != null && _clientIdsByFish.ContainsKey(fish);

    internal bool CanClientInteract(FishInteractionBody body, bool nativeAvailable)
    {
        var fish = body?.GetComponentInParent<FishAISystem>();
        if (!_clientIdsByFish.TryGetValue(fish, out var id) ||
            !_targets.TryGetValue(id, out var target))
            return nativeAvailable;
        return target.Phase is FishPhase.Captured or FishPhase.Corpse || target.Hp <= 0f;
    }

    internal bool CanClientPickupFish(FishInteractionBody body)
    {
        var fish = body?.GetComponentInParent<FishAISystem>();
        return IsClientPickupFish(fish, body);
    }

    internal bool TryClientPickupFish(FishInteractionBody body, BaseCharacter character)
    {
        var fish = body?.GetComponentInParent<FishAISystem>();
        return TryClientPickupFish(fish, character, "body");
    }

    internal bool TryCaptureRemoteLoot(
        UdpSession session,
        uint sceneId,
        DiveLootRequest request,
        RemoteCatchLedger ledger)
    {
        if ((request.SourceId & 0xf000000000000000UL) != 0x6000000000000000UL ||
            ledger == null ||
            request.ItemId <= 0 || request.Count <= 0 ||
            session == null || !session.TryGetFreshRemotePlayerSnapshot(
                Time.realtimeSinceStartup, 0.75f, out var remote) || remote.SceneId != sceneId)
            return false;
        var fishTid = FishTidFromItem(request.ItemId);
        FishAISystem nearest = null;
        var nearestDistance = CorpsePickupRangeSquared;
        foreach (var fish in UnityEngine.Object.FindObjectsByType<FishAISystem>(
                     FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            if (fish == null || fish.FishDataTID != fishTid ||
                !(fish.IsCorpse || fish.HP <= 0.01f || fish.IsFishCaptured ||
                  fish.GetInteractionBody?.InteractionType ==
                  FishInteractionBody.FishInteractionType.Pickup))
                continue;
            var dx = fish.transform.position.x - remote.X;
            var dy = fish.transform.position.y - remote.Y;
            var distance = dx * dx + dy * dy;
            if (distance >= nearestDistance)
                continue;
            nearestDistance = distance;
            nearest = fish;
        }
        if (nearest == null)
            return false;
        return ledger.CapturePickup(
            request.SourceId, request.ItemId, request.Count,
            () => nearest.SuccessNetPickupFish(true), out _);
    }

    internal bool TryClientFallbackPickup(PlayerCharacter player)
    {
        if (player == null)
            return false;
        if (player.CurrentInteractionObject != null)
            return false;

        var playerPosition = player.transform.position;
        FishAISystem nearest = null;
        var nearestDistance = CorpsePickupRangeSquared;
        foreach (var fish in UnityEngine.Object.FindObjectsByType<FishAISystem>(
                     FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            if (!IsClientPickupFish(fish, fish?.GetInteractionBody))
                continue;
            var distance = Vector3.SqrMagnitude(fish.transform.position - playerPosition);
            if (distance >= nearestDistance)
                continue;
            nearestDistance = distance;
            nearest = fish;
        }
        return TryClientPickupFish(nearest, player, "fallback");
    }

    private bool TryClientPickupFish(FishAISystem fish, BaseCharacter character, string source)
    {
        if (!IsClientPickupFish(fish, fish?.GetInteractionBody))
            return false;
        try
        {
            fish.SuccessNetPickupFish(true);
            character?.SuccessInteraction();
            _trace?.Write("CLIENT-FISH-PICKUP",
                $"source={source} type={fish.FishDataTID} hp={fish.HP:F1} corpse={fish.IsCorpse}");
            return true;
        }
        catch (Exception exception)
        {
            _trace?.Write("CLIENT-FISH-PICKUP-ERROR",
                $"source={source} type={fish.FishDataTID} " +
                $"error={exception.GetType().Name}:{exception.Message}");
            return false;
        }
    }

    internal void Clear()
    {
        _clientAuthorityActive = false;
        _lastClientHookId = 0;
        _suppressingNativeRecallOutcome = false;
        _pendingClientFishLootScopeDepth = 0;
        ResetHostActions();
        ReleaseClientTargets();
        foreach (var fish in _hostFishById.Values)
            FishBehaviorTreeState.Forget(fish);
        _hostFishById.Clear();
        _hostIdsByFish.Clear();
        _hostInfoById.Clear();
        _hostRemovedFish.Clear();
        _reportedAmbiguousAllocatorUids.Clear();
        _pendingClientActions.Clear();
        _pendingClientActionByFish.Clear();
        _queuedClientActions.Clear();
        _clientHookLeases.Clear();
        _hostActionAcks.Clear();
        _hostProcessedRequests.Clear();
        _lastRemoteDamageById.Clear();
        _lastRemoteHookActionById.Clear();
        _clientHookLeases.Clear();
        _scheduledClientHookCleanup.Clear();
        _remoteStimulatedIds.Clear();
        _nextRemoteThreatById.Clear();
        _pendingHostLifecycles.Clear();
        ResetClientManifest();
        _nextHostScan = 0f;
        _nextRemoteStimulus = 0f;
        _nextSend = 0f;
        _manifestStateQueued = false;
        _nextHostId = 1;
        _lastHostCount = -1;
        _manifestRevision = 0;
        _fishTick = 0;
        _latestClientManifestRevision = 0;
        _nextTraceSummary = 0f;
        _nextClientBind = 0f;
        _latestClientTick = 0;
        _clientRenderTick = 0d;
        _hasClientClock = false;
        _applyingClientState = false;
        _clientSnapshotsAccepted = 0;
        _clientSnapshotsRejected = 0;
        _clientTeleports = 0;
        _appliedClientSceneId = 0;
        _appliedClientSceneEpoch = 0;
        _highestHostRequestId = 0;
        _fishSnapshotBytes = 0;
        _fishSnapshotsSent = 0;
        _fishSnapshotPackets = 0;
        _fishInterestEnters = 0;
        _fishInterestLeaves = 0;
        _fishOverdueSnapshots = 0;
        _fishControlPacketsDeferred = 0;
        _nextFishTrafficTrace = 0f;
    }

    internal void LateUpdate()
    {
        if (_scheduledClientHookCleanup.Count > 0)
        {
            _idScratch.Clear();
            _idScratch.AddRange(_scheduledClientHookCleanup);
            foreach (var id in _idScratch)
            {
                _scheduledClientHookCleanup.Remove(id);
                var fish = FindClientFish(id);
                if (fish != null)
                    CleanupClientHook(id, fish);
                if (_clientHookLeases.TryGetValue(id, out var lease) &&
                    (lease.RequestId == 0 || lease.CleanupDone && lease.ReleaseQueued))
                    _clientHookLeases.Remove(id);
            }
        }
        if (_deferredClientRemovals.Count == 0)
            return;
        _removalScratch.Clear();
        _removalScratch.AddRange(_deferredClientRemovals);
        _deferredClientRemovals.Clear();
        _applyingClientState = true;
        try
        {
            foreach (var removal in _removalScratch)
            {
                if (removal.Fish == null)
                    continue;
                try
                {
                    ReleaseClientHook(removal.Fish, removal.Id);
                    removal.Fish.DestroySelf();
                }
                catch (Exception exception)
                {
                    removal.Fish.gameObject.SetActive(false);
                    _trace?.Write("REMOVE-ERROR",
                        $"id={removal.Id} error={exception.GetType().Name}:{exception.Message}");
                }
            }
        }
        finally
        {
            _applyingClientState = false;
        }
    }

    private void UpdateHost(
        UdpSession session,
        uint sceneId,
        float now,
        PlayerCharacter hostPlayer,
        Transform remotePlayerTransform)
    {
        if (!session.SceneMatches(sceneId))
        {
            while (session.TryTakeFishDamageRequest(out _))
            {
            }
            while (session.TryTakeFishPickupRequest(out _))
            {
            }
            while (session.TryTakeFishActionRequest(out _))
            {
            }
            while (session.TryTakeFishHookPose(out _))
            {
            }
            ResetHostActions();
            foreach (var info in _hostInfoById.Values)
                info.ManifestQueued = false;
            _manifestStateQueued = false;
            _pendingHostLifecycles.Clear();
            return;
        }
        if (now >= _nextHostScan)
            RefreshHostFish(session, sceneId, now);
        ExpireHostHookLeases(session, sceneId, now);

        if (now >= _nextRemoteStimulus)
        {
            _nextRemoteStimulus = now + RemoteStimulusInterval;
            StimulateHostFishForRemotePlayer(
                session, sceneId, now, hostPlayer?.transform, remotePlayerTransform);
        }

        while (session.TryTakeFishDamageRequest(out _))
        {
        }
        while (session.TryTakeFishActionRequest(out var actionRequest))
            ApplyHostAction(session, sceneId, now, remotePlayerTransform, actionRequest);
        while (session.TryTakeFishHookPose(out var pose))
            ApplyHostHookPose(session, sceneId, now, pose);
        while (session.TryTakeFishLootComplete(out var complete))
            if (_ledger?.CompleteFishTransaction(complete) == true)
                _trace?.Write("FISH-LOOT-COMPLETE",
                    $"transaction={complete.TransactionId} request={complete.RequestId}");
        while (session.TryTakeFishPickupRequest(out var pickupRequest))
            ApplyHostPickup(session, sceneId, now, hostPlayer, pickupRequest);

        if (_ledger != null)
            foreach (var transaction in _ledger.FishLootNeedingReplay(
                         sceneId, session.ConnectionId))
                SendCommittedFishLoot(session, transaction);

        WriteHostSummary(now);

        SendFishControl(session, sceneId);
        if (now < _nextSend)
            return;

        _nextSend = now + NearSnapshotInterval;
        _fishTick = NextRevision(_fishTick);
        _snapshotBuffer.Clear();
        _snapshotCandidates.Clear();
        _overdueSnapshotCandidates.Clear();
        var hasRemotePlayer = TryGetRemotePlayer(session, sceneId, now, out var remotePlayer);
        foreach (var pair in _hostFishById)
        {
            var fish = pair.Value;
            if (fish == null || !fish.gameObject.activeInHierarchy)
                continue;
            var position = fish.transform.position;
            var velocity = fish.Velocity;
            var info = _hostInfoById[pair.Key];
            var phase = ObservedHostPhase(pair.Key, info, fish, now);
            var phaseChanged = phase != info.Phase;
            if (phase != info.Phase)
            {
                info.Phase = phase;
                info.Revision = NextRevision(info.Revision);
                SendHostLifecycle(session, sceneId, pair.Key, info, FishLifecycleKind.Phase);
            }
            var hp = Mathf.Max(0f, fish.HP);
            if (info.LastHp != 0f && hp < info.LastHp)
                info.RecentDamageUntil = now + RecentDamageSeconds;
            info.LastHp = hp;
            var distance = hasRemotePlayer
                ? Vector2.Distance(new Vector2(position.x, position.y), new Vector2(remotePlayer.X, remotePlayer.Y))
                : float.PositiveInfinity;
            var forced = IsForcedRelevant(
                fish, info, now, hostPlayer?.transform, remotePlayerTransform);
            var interestChanged = !info.InterestKnown || !info.Interested;
            info.InterestKnown = true;
            info.Interested = true;
            info.OutsideInterestTicks = 0;
            if (interestChanged)
            {
                info.Revision = NextRevision(info.Revision);
                SendHostLifecycle(session, sceneId, pair.Key, info, FishLifecycleKind.InterestEnter);
                info.HasSnapshot = false;
                _fishInterestEnters++;
            }
            var snapshot = new FishSnapshot(
                sceneId, session.LocalSceneEpoch, _fishTick, pair.Key, info.Revision, fish.FishDataTID,
                position.x, position.y, position.z, fish.Rotation,
                velocity.x, velocity.y, hp, BuildFlags(fish, info.Phase, info.Renderer));
            if (phaseChanged || interestChanged)
            {
                SendImmediateHostSnapshot(session, sceneId, pair.Key, info, snapshot);
                continue;
            }
            var snapshotInterval = hasRemotePlayer && distance <= NearSnapshotDistance
                ? NearSnapshotInterval
                : MidSnapshotInterval;
            if (now < info.LastSnapshotSend + snapshotInterval)
                continue;
            if (!ShouldSendSnapshot(info, snapshot, now))
                continue;
            info.Priority = Math.Min(12f, info.Priority + 1f);
            if (SnapshotDeadlineReached(info.LastSnapshotSend, now))
                InsertSnapshotCandidate(_overdueSnapshotCandidates,
                    new SnapshotCandidate(snapshot, info, now - info.LastSnapshotSend));
            else
            {
                var bonus = Math.Min(8f,
                    Math.Max(0f, NearSnapshotDistance - distance) * 0.05f +
                    (forced ? 4f : 0f));
                InsertSnapshotCandidate(_snapshotCandidates,
                    new SnapshotCandidate(snapshot, info, info.Priority + bonus));
            }
        }
        _snapshotBuffer.Clear();
        var bytes = Protocol.FishSnapshotBatchOverhead;
        AddSnapshotCandidates(_overdueSnapshotCandidates, true, now, ref bytes);
        AddSnapshotCandidates(_snapshotCandidates, false, now, ref bytes);
        if (_snapshotBuffer.Count > 0)
        {
            _fishSnapshotBytes += session.SendFishSnapshots(sceneId, _fishTick, _snapshotBuffer);
            _fishSnapshotsSent += _snapshotBuffer.Count;
            _fishSnapshotPackets++;
        }
    }

    private void AddSnapshotCandidates(
        List<SnapshotCandidate> candidates, bool overdue, float now, ref int bytes)
    {
        foreach (var candidate in candidates)
        {
            var recordBytes = Protocol.FishSnapshotRecordSize(candidate.Snapshot);
            if (_snapshotBuffer.Count >= SnapshotEntityBudget ||
                bytes + recordBytes > SnapshotByteBudget)
                break;
            candidate.Info.LastSnapshot = candidate.Snapshot;
            candidate.Info.LastSnapshotSend = now;
            candidate.Info.HasSnapshot = true;
            candidate.Info.Priority = 0f;
            _snapshotBuffer.Add(candidate.Snapshot);
            bytes += recordBytes;
            if (overdue)
                _fishOverdueSnapshots++;
        }
    }

    private void RefreshHostFish(UdpSession session, uint sceneId, float now)
    {
        _nextHostScan = now + 1f;
        _allocatorScratch.Clear();
        foreach (var allocator in UnityEngine.Object.FindObjectsByType<FishAllocator>(
                     FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (allocator != null)
                _allocatorScratch.Add(allocator);
        }

        _allocatorUidsScratch.Clear();
        _ambiguousAllocatorUidsScratch.Clear();
        var uniqueAllocators = new Dictionary<string, FishAllocator>(StringComparer.Ordinal);
        foreach (var allocator in _allocatorScratch)
        {
            var uid = GetNetworkAllocatorUid(allocator);
            _allocatorUidsScratch.Add(allocator, uid);
            AddUniqueAllocatorUid(uniqueAllocators, _ambiguousAllocatorUidsScratch, uid, allocator);
        }
        foreach (var uid in _ambiguousAllocatorUidsScratch)
            ReportAmbiguousAllocatorUid(uid);

        _allocatorByFishScratch.Clear();
        _ambiguousHostFishScratch.Clear();
        foreach (var allocator in _allocatorScratch)
        {
            var uid = _allocatorUidsScratch[allocator];
            var fishs = allocator.GetInstancedFishs;
            if (fishs == null)
                continue;
            foreach (var fish in fishs)
                if (fish != null && ManifestEntryEligible(fish.IsFishCaptured) &&
                    !_hostRemovedFish.Contains(fish))
                    if (_ambiguousAllocatorUidsScratch.Contains(uid))
                        _ambiguousHostFishScratch.Add(fish);
                    else
                        _allocatorByFishScratch.TryAdd(fish, allocator);
        }

        var topologyChanged = false;
        _staleFishScratch.Clear();
        foreach (var pair in _hostIdsByFish)
        {
            var id = pair.Value;
            if (pair.Key == null || _ambiguousHostFishScratch.Contains(pair.Key))
                _staleFishScratch.Add(pair.Key);
            else if (_allocatorByFishScratch.ContainsKey(pair.Key))
            {
                if (_hostInfoById.TryGetValue(id, out var seenInfo))
                    seenInfo.MissingScans = 0;
            }
            else if (!_hostInfoById.TryGetValue(id, out var missingInfo))
                _staleFishScratch.Add(pair.Key);
            else
            {
                missingInfo.MissingScans = NextMissingScans(missingInfo.MissingScans, false);
                if (ShouldRemoveMissingFish(false, missingInfo.MissingScans))
                    _staleFishScratch.Add(pair.Key);
            }
        }
        foreach (var fish in _staleFishScratch)
        {
            if (fish != null && _ambiguousHostFishScratch.Contains(fish))
                _hostRemovedFish.Add(fish);
            var id = _hostIdsByFish[fish];
            SendHostDespawn(session, sceneId, id);
            RemoveHostFish(id);
        }

        foreach (var pair in _allocatorByFishScratch)
        {
            var fish = pair.Key;
            var allocator = pair.Value;
            var fishDataTID = fish.FishDataTID;
            if (fishDataTID <= 0)
                continue;
            var uid = GetNetworkAllocatorUid(allocator);
            var active = fish.gameObject.activeInHierarchy;

            if (!_hostIdsByFish.TryGetValue(fish, out var id))
            {
                id = TakeHostId();
                _hostIdsByFish.Add(fish, id);
            }
            _hostFishById[id] = fish;
            if (!_hostInfoById.TryGetValue(id, out var info))
            {
                info = CreateHostFish(fish, uid, fishDataTID, active);
                info.Revision = 1;
                info.Phase = HostPhase(fish);
                info.LastHp = Mathf.Max(0f, fish.HP);
                _hostInfoById.Add(id, info);
                topologyChanged = true;
            }
            else if (info.AllocatorUid != uid || info.FishDataTID != fishDataTID)
                topologyChanged = true;
            info.Fish = fish;
            info.Renderer ??= fish.GetComponentInChildren<SpriteRenderer>(true);
            info.AllocatorUid = uid;
            info.FishDataTID = fishDataTID;
            info.MissingScans = 0;
            FishBehaviorTreeState.ObserveActive(fish);
            var poolLifecycle = UpdatePoolActive(info, active);
            if (poolLifecycle.HasValue)
            {
                info.Revision = NextRevision(info.Revision);
                SendHostLifecycle(session, sceneId, id, info, poolLifecycle.Value);
                if (active)
                {
                    _fishInterestEnters++;
                    SendImmediateHostSnapshot(session, sceneId, id, info);
                }
                else
                    _fishInterestLeaves++;
                _trace?.Write("POOL-ACTIVE", $"id={id} active={active}");
            }
        }

        if (_lastHostCount != _hostFishById.Count)
        {
            _log.LogInfo($"Network fish host manifest: {_hostFishById.Count} fish");
            _trace?.Write("HOST-MANIFEST",
                $"revision={_manifestRevision} count={_hostFishById.Count}");
            _lastHostCount = _hostFishById.Count;
        }
        if (_manifestRevision == 0 || topologyChanged)
            MarkHostTopologyChanged();
    }

    private void StimulateHostFishForRemotePlayer(
        UdpSession session,
        uint sceneId,
        float now,
        Transform hostPlayerTransform,
        Transform remotePlayerTransform)
    {
        if (remotePlayerTransform == null ||
            !TryGetRemotePlayer(session, sceneId, now, out var remotePlayer))
        {
            _remoteStimulatedIds.Clear();
            _nextRemoteThreatById.Clear();
            return;
        }

        foreach (var pair in _hostFishById)
        {
            var id = pair.Key;
            var fish = pair.Value;
            if (fish == null || !fish.gameObject.activeInHierarchy || fish.IsCorpse ||
                fish.IsFishCaptured)
                continue;

            try
            {
                var center = fish.SensorCenterPoint;
                var distance = Vector2.Distance(
                    center, new Vector2(remotePlayer.X, remotePlayer.Y));
                var aggressive = fish.IsAggressive;
                var active = _remoteStimulatedIds.Contains(id);
                var nextRefresh = _nextRemoteThreatById.TryGetValue(id, out var due) ? due : 0f;
                var decision = RemoteThreatPolicy(
                    aggressive, NativeSensorRadius(fish), distance, active, now, nextRefresh);
                if (!decision.Inside || aggressive &&
                    !IsInsideEnemySensor(fish, remotePlayer, out distance))
                {
                    _remoteStimulatedIds.Remove(id);
                    _nextRemoteThreatById.Remove(id);
                    continue;
                }

                var current = fish.DetectedEnemyData;
                var currentTarget = current?.DetectedEnemy;
                if (currentTarget == hostPlayerTransform)
                {
                    var currentDistance = Vector2.Distance(
                        fish.SensorCenterPoint, currentTarget.position);
                    if (currentDistance <= distance)
                        continue;
                }

                WakeHostFish(fish, id);
                if (aggressive)
                {
                    if (currentTarget != remotePlayerTransform)
                        fish.OnEnemyDetected(new EnemyDetectSensorData(
                            remotePlayerTransform,
                            new Vector2(remotePlayer.X, remotePlayer.Y),
                            distance,
                            EnumDectectionType.Player));
                    else if (current != null)
                    {
                        current.Distance = distance;
                        current.HitPoint = new Vector2(remotePlayer.X, remotePlayer.Y);
                    }
                }
                else if (decision.Enter)
                {
                    fish.OnUnderAttack(remotePlayerTransform);
                    if (currentTarget == remotePlayerTransform && current != null)
                    {
                        current.Distance = distance;
                        current.HitPoint = new Vector2(remotePlayer.X, remotePlayer.Y);
                    }
                    else
                        fish.OnEnemyDetected(new EnemyDetectSensorData(
                            remotePlayerTransform,
                            new Vector2(remotePlayer.X, remotePlayer.Y),
                            distance,
                            EnumDectectionType.Player));
                    _nextRemoteThreatById[id] = now + RemoteThreatRefreshSeconds;
                    _trace?.Write("THREAT-REMOTE",
                        $"id={id} type={fish.FishDataTID} distance={distance:F2} " +
                        $"radius={Mathf.Max(NativeSensorRadius(fish), RemoteFearEnterRadius):F2} " +
                        "refresh=false");
                }
                else if (decision.Refresh)
                {
                    if (currentTarget == remotePlayerTransform && current != null)
                    {
                        current.Distance = distance;
                        current.HitPoint = new Vector2(remotePlayer.X, remotePlayer.Y);
                    }
                    else
                        fish.OnEnemyDetected(new EnemyDetectSensorData(
                            remotePlayerTransform,
                            new Vector2(remotePlayer.X, remotePlayer.Y),
                            distance,
                            EnumDectectionType.Player));
                    _nextRemoteThreatById[id] = now + RemoteThreatRefreshSeconds;
                    _trace?.Write("THREAT-REMOTE",
                        $"id={id} type={fish.FishDataTID} distance={distance:F2} " +
                        $"radius={Mathf.Max(NativeSensorRadius(fish), RemoteFearEnterRadius):F2} " +
                        "refresh=true");
                }

                if (_remoteStimulatedIds.Add(id))
                    _trace?.Write("REMOTE-DETECT",
                        $"id={id} type={fish.FishDataTID} distance={distance:F2} " +
                        $"aggressive={fish.IsAggressive}");
            }
            catch (Exception exception)
            {
                _trace?.Write("REMOTE-DETECT-ERROR",
                    $"id={id} error={exception.GetType().Name}:{exception.Message}");
            }
        }
    }

    private void WakeHostFish(FishAISystem fish, int id)
    {
        var changed = false;
        if (!fish.IsFishEnable)
        {
            fish.IsFishEnable = true;
            changed = true;
        }
        if (fish.IsFishSleeped)
        {
            fish.OnForceEndSleepMode();
            changed = true;
        }
        if (changed)
            _trace?.Write("WAKE",
                $"id={id} type={fish.FishDataTID} enabled={fish.IsFishEnable} " +
                $"sleeping={fish.IsFishSleeped}");
    }

    private static bool IsInsideEnemySensor(
        FishAISystem fish,
        PlayerSnapshot player,
        out float distance)
    {
        distance = 0f;
        if (!fish.IsEnableEnemyDetectSensor)
            return false;
        var spec = fish.GetFishSpecData;
        if (spec == null || spec.EnemyDetectType == EnumEnemyDetectType.None)
            return false;

        var center = fish.SensorCenterPoint;
        var dx = player.X - center.x;
        var dy = player.Y - center.y;
        distance = Mathf.Sqrt(dx * dx + dy * dy);
        if (spec.EnemyDetectType == EnumEnemyDetectType.CircleOverlap)
            return spec.EnemyCircleSensorRadius > 0f &&
                distance <= spec.EnemyCircleSensorRadius;

        var size = spec.EnemyBoxSensorSize;
        return size.x > 0f && size.y > 0f &&
            Mathf.Abs(dx) <= size.x * 0.5f && Mathf.Abs(dy) <= size.y * 0.5f;
    }

    private static float NativeSensorRadius(FishAISystem fish)
    {
        if (!fish.IsEnableEnemyDetectSensor)
            return 0f;
        var spec = fish.GetFishSpecData;
        if (spec == null || spec.EnemyDetectType == EnumEnemyDetectType.None)
            return 0f;
        return spec.EnemyDetectType == EnumEnemyDetectType.CircleOverlap
            ? Mathf.Max(0f, spec.EnemyCircleSensorRadius)
            : Mathf.Max(0f, Mathf.Max(spec.EnemyBoxSensorSize.x, spec.EnemyBoxSensorSize.y) * 0.5f);
    }

    private void WriteHostSummary(float now)
    {
        WriteFishTraffic(now);
        if (now < _nextTraceSummary)
            return;
        _nextTraceSummary = now + 2f;
        var active = 0;
        var enabled = 0;
        var sleeping = 0;
        foreach (var fish in _hostFishById.Values)
        {
            if (fish == null)
                continue;
            if (fish.gameObject.activeInHierarchy)
                active++;
            if (fish.IsFishEnable)
                enabled++;
            if (fish.IsFishSleeped)
                sleeping++;
        }
        _trace?.Write("FISH-SUMMARY",
            $"known={_hostFishById.Count} active={active} enabled={enabled} " +
            $"sleeping={sleeping} remoteTargets={_remoteStimulatedIds.Count} " +
            $"manifestRevision={_manifestRevision} queued={CountQueuedManifests()}");
    }

    private void WriteFishTraffic(float now)
    {
        if (now < _nextFishTrafficTrace)
            return;
        _nextFishTrafficTrace = now + 5f;
        _trace?.Write("FISH-TRAFFIC",
            $"bytes={_fishSnapshotBytes} packets={_fishSnapshotPackets} " +
            $"entities={_fishSnapshotsSent} overdue={_fishOverdueSnapshots} " +
            $"controlDeferred={_fishControlPacketsDeferred} " +
            $"enters={_fishInterestEnters} leaves={_fishInterestLeaves}");
        _fishSnapshotBytes = 0;
        _fishSnapshotPackets = 0;
        _fishSnapshotsSent = 0;
        _fishInterestEnters = 0;
        _fishInterestLeaves = 0;
        _fishOverdueSnapshots = 0;
        _fishControlPacketsDeferred = 0;
    }

    private void SendFishControl(UdpSession session, uint sceneId)
    {
        var budget = FishControlSendBudget(session.ReliableCapacityRemaining);
        while (budget > 0 && _pendingHostLifecycles.Count > 0)
        {
            if (!session.SendFishLifecycle(_pendingHostLifecycles.Peek()))
                break;
            _pendingHostLifecycles.Dequeue();
            budget--;
        }

        foreach (var pair in _hostInfoById)
        {
            if (budget == 0)
                break;
            var info = pair.Value;
            var fish = info.Fish;
            if (info.ManifestQueued || fish == null)
                continue;
            var position = fish.transform.position;
            var manifest = new FishManifest(
                sceneId, session.LocalSceneEpoch, _manifestRevision, pair.Key,
                info.Revision, info.AllocatorUid, info.FishDataTID,
                position.x, position.y, position.z, fish.Rotation,
                Mathf.Max(0f, fish.HP),
                BuildManifestFlags(BuildFlags(fish, info.Phase, info.Renderer), info.Interested));
            if (!session.SendFishManifest(manifest))
                break;
            info.ManifestQueued = true;
            budget--;
        }
        var queuedCount = CountQueuedManifests();
        if (budget > 0 && !_manifestStateQueued && queuedCount == _hostInfoById.Count &&
            queuedCount <= ushort.MaxValue &&
            session.SendFishManifestState(new FishManifestState(
                sceneId, session.LocalSceneEpoch, _manifestRevision,
                (ushort)queuedCount)))
        {
            _manifestStateQueued = true;
            budget--;
        }
        if (_pendingHostLifecycles.Count > 0 || !_manifestStateQueued)
            _fishControlPacketsDeferred++;
    }

    private void ApplyHostAction(
        UdpSession session,
        uint sceneId,
        float now,
        Transform remotePlayerTransform,
        FishActionRequest request)
    {
        if (_ledger?.TryGetCommittedFishLoot(request.RequestId, out var committed) == true)
        {
            SendCommittedFishLoot(session, committed);
            return;
        }
        if (_hostActionAcks.TryGetValue(request.RequestId, out var cached))
        {
            session.SendFishActionAck(cached);
            return;
        }
        if (!TryMarkProcessedRequest(
                _hostProcessedRequests, ref _highestHostRequestId,
                request.RequestId, ProcessedRequestWindow))
        {
            SendHostActionAck(session, now, BuildHostActionAck(
                session, sceneId, request, FishActionResult.Rejected,
                FishActionRejectReason.Duplicate));
            return;
        }
        foreach (var requestId in new List<ulong>(_hostActionAcks.Keys))
            if (!_hostProcessedRequests.Contains(requestId))
            {
                _hostActionAcks.Remove(requestId);
            }

        var scopeValid = request.SceneId == sceneId &&
            request.SceneEpoch == session.LocalSceneEpoch;
        if (!_hostFishById.TryGetValue(request.Id, out var fish))
        {
            RefreshHostFish(session, sceneId, now);
            _hostFishById.TryGetValue(request.Id, out fish);
        }
        _hostInfoById.TryGetValue(request.Id, out var info);
        if (info != null && fish != null)
            info.Phase = ObservedHostPhase(request.Id, info, fish, now);

        var leaseActive = _hostHookLeases.TryGetValue(request.Id, out var hookLease) &&
            now < hookLease.Expires;
        var leaseMatches = LeaseIdentityMatches(
            request.Action, leaseActive ? hookLease.RequestId : 0, request.LeaseId);
        var isHookAction = request.Action is FishAction.Hook or FishAction.Qte or
            FishAction.Release or FishAction.Capture;
        FishActionRejectReason rejectReason;
        if (!scopeValid)
            rejectReason = FishActionRejectReason.SceneMismatch;
        else if (fish == null || info == null)
            rejectReason = FishActionRejectReason.MissingFish;
        else if (!RevisionMatches(request.KnownRevision, info.Revision))
            rejectReason = FishActionRejectReason.StaleRevision;
        else if (request.Action is FishAction.Damage or FishAction.Qte
                      ? !ValidDamagePayload(request)
                      : request.Action is not (FishAction.Hook or FishAction.Release or
                          FishAction.Capture or FishAction.CorpsePickup))
            rejectReason = FishActionRejectReason.InvalidAction;
        else if (request.Action == FishAction.Damage && !IsDamageablePhase(info.Phase) ||
                 request.Action == FishAction.CorpsePickup &&
                     info.Phase is not (FishPhase.Corpse or FishPhase.Captured) ||
                 isHookAction && (!leaseMatches ||
                     !HostActionStateValid(request.Action, info.Phase, leaseActive)))
            rejectReason = FishActionRejectReason.InvalidState;
        else if (ActionRangeSquared(request.Action) is { } range &&
                 (!TryGetRemotePlayer(session, sceneId, now, out var remotePlayer) ||
                  !InRange(fish.transform.position, remotePlayer, range)) ||
                 (request.Action is FishAction.Damage or FishAction.Qte
                      ? _lastRemoteDamageById.TryGetValue(request.Id, out var lastDamage) &&
                        now - lastDamage < MinDamageIntervalSeconds
                      : _lastRemoteHookActionById.TryGetValue(request.Id, out var lastAction) &&
                        now - lastAction < MinHookActionIntervalSeconds))
            rejectReason = FishActionRejectReason.OutOfRange;
        else
            rejectReason = FishActionRejectReason.None;

        if (rejectReason != FishActionRejectReason.None)
        {
            if (info != null && ShouldCancelLeaseOnReject(request.Action, leaseMatches))
                CancelHostHookLease(session, sceneId, request.Id, info);
            var rejected = BuildHostActionAck(
                session, sceneId, request, FishActionResult.Rejected, rejectReason);
            SendHostActionAck(session, now, rejected);
            _trace?.Write("DAMAGE-REJECT",
                $"request={request.RequestId} id={request.Id} reason={rejectReason}");
            return;
        }

        if (request.Action is FishAction.Capture or FishAction.CorpsePickup)
        {
            ApplyHostLootAction(session, sceneId, request, fish, info);
            return;
        }

        var hpBefore = fish.HP;
        try
        {
            if (request.Action is FishAction.Damage or FishAction.Qte)
            {
                WakeHostFish(fish, request.Id);
                if (remotePlayerTransform != null)
                    fish.OnUnderAttack(remotePlayerTransform);
                if (request.Action == FishAction.Qte)
                    fish.SetHPDamageQTE(request.Damage, (EElement)request.Element);
                else
                    fish.SetHPDamage(
                        request.Damage, (EElement)request.Element, (AttackType)request.AttackType);
                _lastRemoteDamageById[request.Id] = now;
            }
            else
                _lastRemoteHookActionById[request.Id] = now;

            switch (request.Action)
            {
                case FishAction.Hook:
                    if (!BeginHostHookLease(request.Id, request.RequestId, fish, now))
                        throw new InvalidOperationException("fish behavior tree state is unavailable");
                    info.Phase = FishPhase.Hooked;
                    break;
                case FishAction.Qte:
                    RenewHostHookLease(
                        _hostHookLeases, request.Id, request.LeaseId, now);
                    info.Phase = fish.HP <= 0f ? FishPhase.Corpse : FishPhase.Qte;
                    break;
                case FishAction.Release:
                    EndHostHookLease(request.Id);
                    info.Phase = FishPhase.Alive;
                    break;
                default:
                    info.Phase = ObservedHostPhase(request.Id, info, fish, now);
                    break;
            }
            _nextSend = 0f;
            _fishTick = NextRevision(_fishTick);
            info.Revision = NextRevision(info.Revision);
            var accepted = BuildHostActionAck(
                session, sceneId, request, FishActionResult.Accepted,
                FishActionRejectReason.None);
            SendHostActionAck(session, now, accepted);
            SendHostLifecycle(session, sceneId, request.Id, info, FishLifecycleKind.Phase);
            SendImmediateHostSnapshot(session, sceneId, request.Id, info);
            _log.LogDebug(
                $"Network fish damage: id={request.Id}; damage={request.Damage}; hp={fish.HP:F1}");
            _trace?.Write("FISH-ACTION-APPLY",
                $"id={request.Id} type={fish.FishDataTID} damage={request.Damage} " +
                $"attack={(AttackType)request.AttackType} hp={hpBefore:F1}->{fish.HP:F1} " +
                $"corpse={fish.IsCorpse} enabled={fish.IsFishEnable}");
        }
        catch (Exception exception)
        {
            if (request.Action == FishAction.Hook)
            {
                EndHostHookLease(request.Id);
                if (info != null)
                    info.Phase = FishPhase.Alive;
            }
            else if (info != null && ShouldCancelLeaseOnReject(request.Action, leaseMatches))
                CancelHostHookLease(session, sceneId, request.Id, info);
            SendHostActionAck(session, now, BuildHostActionAck(
                session, sceneId, request, FishActionResult.Rejected,
                FishActionRejectReason.InternalError));
            _log.LogWarning($"Network fish damage failed: id={request.Id}; {exception.Message}");
            _trace?.Write("DAMAGE-ERROR",
                $"id={request.Id} hp={hpBefore:F1} error={exception.GetType().Name}:{exception.Message}");
        }
    }

    private void ApplyHostLootAction(
        UdpSession session,
        uint sceneId,
        FishActionRequest request,
        FishAISystem fish,
        HostFish info)
    {
        var revision = NextRevision(info.Revision);
        var hp = Math.Max(0f, fish.HP);
        var transactionId = NextRequestId(ref _nextHostTransactionId);
        if (_ledger == null ||
            session.ReliableCapacityRemaining < RemoteCatchLedger.MaxFishGrantEntries + 1 ||
            !_ledger.CanBeginFishTransaction())
        {
            if (request.Action == FishAction.Capture)
                CancelHostHookLease(session, sceneId, request.Id, info);
            SendHostActionAck(session, Time.realtimeSinceStartup, BuildHostActionAck(
                session, sceneId, request, FishActionResult.Rejected,
                FishActionRejectReason.Capacity));
            return;
        }
        CommittedFishLoot transaction;
        _hostTransactionalPickup = true;
        try
        {
            if (!_ledger.CaptureFishTransaction(
                    transactionId, sceneId, session.LocalSceneEpoch, request.RequestId,
                    request.Id, revision, request.Action, hp,
                    request.Action == FishAction.Capture ? FishPhase.Captured : FishPhase.Corpse,
                    fish, () => fish.SuccessNetPickupFish(true), out transaction))
            {
                if (request.Action == FishAction.Capture)
                    CancelHostHookLease(session, sceneId, request.Id, info);
                SendHostActionAck(session, Time.realtimeSinceStartup, BuildHostActionAck(
                    session, sceneId, request, FishActionResult.Rejected,
                    FishActionRejectReason.InternalError));
                return;
            }
        }
        finally
        {
            _hostTransactionalPickup = false;
        }

        EndHostHookLease(request.Id);
        info.Revision = revision;
        info.Phase = request.Action == FishAction.Capture
            ? FishPhase.Captured : FishPhase.Corpse;
        _hostRemovedFish.Add(fish);
        SendCommittedFishLoot(session, transaction);
        QueueHostLifecycle(session, new FishLifecycle(
            sceneId, session.LocalSceneEpoch, request.Id, revision,
            FishLifecycleKind.Despawn, 0, FishPhase.None, 0f));
        RemoveHostFish(request.Id);
        ProbeBehaviour.Instance?.RefreshMissionAfterNativeChange();
        _trace?.Write("FISH-LOOT-COMMIT",
            $"transaction={transactionId} request={request.RequestId} id={request.Id} " +
            $"action={request.Action} entries={transaction.Grants.Count}");
    }

    private static bool SendCommittedFishLoot(UdpSession session, CommittedFishLoot transaction) =>
        TrySendCommittedFishLoot(
            transaction, session.LocalSceneEpoch, session.ConnectionId,
            session.SendFishLootGrant, session.SendFishActionAck);

    private static bool TrySendCommittedFishLoot(
        CommittedFishLoot transaction,
        uint sceneEpoch,
        ulong sessionId,
        Func<FishLootGrant, bool> sendGrant,
        Func<FishActionAck, bool> sendAck)
    {
        if (sessionId == 0)
            return false;
        foreach (var grant in RemoteCatchLedger.RebaseGrants(transaction, sceneEpoch))
            if (!sendGrant(grant))
                return false;
        if (!sendAck(RemoteCatchLedger.RebaseAck(transaction, sceneEpoch)))
            return false;
        RemoteCatchLedger.MarkSent(transaction, sessionId);
        return true;
    }

    private FishActionAck BuildHostActionAck(
        UdpSession session,
        uint sceneId,
        FishActionRequest request,
        FishActionResult result,
        FishActionRejectReason reason)
    {
        if (!_hostInfoById.TryGetValue(request.Id, out var info) || info.Fish == null)
            return new FishActionAck(
                request.RequestId, sceneId, session.LocalSceneEpoch, request.Id, 0,
                request.Action, FishActionResult.Rejected, FishActionRejectReason.MissingFish,
                0f, FishPhase.None);
        return new FishActionAck(
            request.RequestId, sceneId, session.LocalSceneEpoch, request.Id, info.Revision,
                request.Action, result, reason, Mathf.Max(0f, info.Fish.HP), info.Phase);
    }

    private void SendHostActionAck(UdpSession session, float now, FishActionAck ack)
    {
        _hostActionAcks[ack.RequestId] = ack;
        session.SendFishActionAck(ack);
    }

    private static bool ValidDamagePayload(FishActionRequest request)
    {
        if (request.Damage is < 1 or > 10_000 ||
            !Enum.IsDefined(typeof(EElement), request.Element))
            return false;
        return request.Action == FishAction.Qte
            ? request.AttackType == 0
            : Enum.IsDefined(typeof(AttackType), request.AttackType) &&
              IsPlayerAttack((AttackType)request.AttackType) &&
              (AttackType)request.AttackType != AttackType.QTE_Damage;
    }

    private static bool IsDamageablePhase(FishPhase phase) =>
        phase is FishPhase.Alive or FishPhase.Hooked or FishPhase.Qte;

    private static FishPhase HostPhase(FishAISystem fish) =>
        fish.IsFishCaptured ? FishPhase.Captured :
        fish.IsCorpse || fish.HP <= 0f ? FishPhase.Corpse :
        fish.IsFishHooked || fish.IsFishHookedSequence ? FishPhase.Hooked : FishPhase.Alive;

    private FishPhase ObservedHostPhase(
        int id, HostFish info, FishAISystem fish, float now)
    {
        var native = HostPhase(fish);
        if (native is FishPhase.Corpse or FishPhase.Captured)
            return native;
        if (info.Phase == FishPhase.Captured)
            return info.Phase;
        return _hostHookLeases.TryGetValue(id, out var lease) && now < lease.Expires
            ? info.Phase
            : native;
    }

    private void ExpireHostHookLeases(UdpSession session, uint sceneId, float now)
    {
        foreach (var pair in new List<KeyValuePair<int, HostHookLease>>(_hostHookLeases))
        {
            if (now < pair.Value.Expires)
                continue;
            EndHostHookLease(pair.Key);
            if (!_hostInfoById.TryGetValue(pair.Key, out var info) ||
                info.Phase is not (FishPhase.Hooked or FishPhase.Qte))
                continue;
            info.Phase = FishPhase.Alive;
            info.Revision = NextRevision(info.Revision);
            SendHostLifecycle(session, sceneId, pair.Key, info, FishLifecycleKind.Phase);
        }
    }

    private void CancelHostHookLease(
        UdpSession session, uint sceneId, int id, HostFish info)
    {
        var removed = EndHostHookLease(id);
        if (!removed && info.Phase is not FishPhase.Captured)
            return;
        if (info.Phase is FishPhase.Hooked or FishPhase.Qte or FishPhase.Captured)
            info.Phase = FishPhase.Alive;
        info.Revision = NextRevision(info.Revision);
        _nextSend = 0f;
        SendHostLifecycle(session, sceneId, id, info, FishLifecycleKind.Phase);
    }

    private void ResetHostActions()
    {
        foreach (var id in new List<int>(_hostHookLeases.Keys))
        {
            if (_hostInfoById.TryGetValue(id, out var info) &&
                info.Phase is FishPhase.Hooked or FishPhase.Qte)
                info.Phase = FishPhase.Alive;
            EndHostHookLease(id);
        }
        _hostActionAcks.Clear();
        _hostProcessedRequests.Clear();
        _lastRemoteDamageById.Clear();
        _lastRemoteHookActionById.Clear();
        _highestHostRequestId = 0;
    }

    private bool BeginHostHookLease(
        int id, ulong requestId, FishAISystem fish, float now)
    {
        EndHostHookLease(id);
        FishBehaviorTreeState.ObserveActive(fish);
        if (!FishBehaviorTreeState.TryGet(fish, out var behaviorEnabled))
            return false;
        try
        {
            if (behaviorEnabled)
                FishBehaviorTreeState.Set(fish, false);
        }
        catch (Exception exception)
        {
            _trace?.Write("HOOK-POSE-SUSPEND-ERROR",
                $"id={id} error={exception.GetType().Name}:{exception.Message}");
            try
            {
                FishBehaviorTreeState.Set(fish, behaviorEnabled);
            }
            catch (Exception restoreException)
            {
                _trace?.Write("HOOK-POSE-RESTORE-ERROR",
                    $"id={id} error={restoreException.GetType().Name}:{restoreException.Message}");
            }
            return false;
        }
        var lease = new HostHookLease(requestId, now + HookLeaseSeconds, behaviorEnabled)
        {
            LastPoseTime = now
        };
        _hostHookLeases[id] = lease;
        return true;
    }

    private bool EndHostHookLease(int id)
    {
        return TryEndHostHookLease(_hostHookLeases, id, lease =>
        {
            if (!_hostFishById.TryGetValue(id, out var fish) || fish == null)
                return;
            try
            {
                FishBehaviorTreeState.Set(fish, lease.BehaviorEnabled);
            }
            catch (Exception exception)
            {
                _trace?.Write("HOOK-POSE-RESTORE-ERROR",
                    $"id={id} error={exception.GetType().Name}:{exception.Message}");
            }
        });
    }

    private static bool TryEndHostHookLease(
        Dictionary<int, HostHookLease> leases, int id, Action<HostHookLease> restore)
    {
        if (!leases.Remove(id, out var lease))
            return false;
        restore(lease);
        return true;
    }

    private void ApplyHostHookPose(
        UdpSession session, uint sceneId, float now, FishHookPose pose)
    {
        if (!_hostHookLeases.TryGetValue(pose.FishId, out var lease) ||
            !_hostFishById.TryGetValue(pose.FishId, out var fish) || fish == null ||
            !HookPoseIdentityValid(
                pose, sceneId, session.LocalSceneEpoch, pose.FishId, lease.RequestId) ||
            now >= lease.Expires ||
            !TryGetRemotePlayer(session, sceneId, now, out var remotePlayer) ||
            !HookPoseWithinPlayer(pose, remotePlayer) ||
            !HookPoseVelocityPlausible(pose) ||
            (lease.HasPose
                ? !HookPoseMotionPlausible(lease.LastPose, pose, now - lease.LastPoseTime)
                : !HookPoseFromCurrentPlausible(
                    fish.transform.position, pose, now - lease.LastPoseTime)))
            return;

        fish.transform.position = new Vector3(pose.X, pose.Y, pose.Z);
        fish.Rotation = pose.Rotation;
        var body = fish.GetComponent<Rigidbody2D>();
        if (body != null)
            body.velocity = new Vector2(pose.VelocityX, pose.VelocityY);
        lease.LastPose = pose;
        lease.LastPoseTime = now;
        lease.HasPose = true;
        _nextSend = 0f;
    }

    private void SendHostLifecycle(
        UdpSession session,
        uint sceneId,
        int id,
        HostFish info,
        FishLifecycleKind kind)
    {
        QueueHostLifecycle(session, new FishLifecycle(
            sceneId, session.LocalSceneEpoch, id, info.Revision, kind,
            0,
            info.Phase, Mathf.Max(0f, info.Fish.HP)));
    }

    private uint SendHostDespawn(UdpSession session, uint sceneId, int id)
    {
        var revision = NextHostRevision(id);
        QueueHostLifecycle(session, new FishLifecycle(
            sceneId, session.LocalSceneEpoch, id, revision, FishLifecycleKind.Despawn,
            0, FishPhase.None, 0f));
        return revision;
    }

    private uint NextHostRevision(int id)
    {
        if (!_hostInfoById.TryGetValue(id, out var info))
            return 1;
        info.Revision = NextRevision(info.Revision);
        return info.Revision;
    }

    private void SendImmediateHostSnapshot(
        UdpSession session, uint sceneId, int id, HostFish info)
    {
        var fish = info.Fish;
        var position = fish.transform.position;
        var velocity = fish.Velocity;
        var snapshot = new FishSnapshot(
            sceneId, session.LocalSceneEpoch, _fishTick, id, info.Revision, info.FishDataTID,
            position.x, position.y, position.z, fish.Rotation,
            velocity.x, velocity.y, Mathf.Max(0f, fish.HP),
            BuildFlags(fish, info.Phase, info.Renderer));
        SendImmediateHostSnapshot(session, sceneId, id, info, snapshot);
    }

    private void SendImmediateHostSnapshot(
        UdpSession session, uint sceneId, int id, HostFish info, FishSnapshot snapshot)
    {
        _snapshotBuffer.Clear();
        _snapshotBuffer.Add(snapshot);
        _fishSnapshotBytes += session.SendFishSnapshots(sceneId, _fishTick, _snapshotBuffer);
        _fishSnapshotsSent++;
        _fishSnapshotPackets++;
        info.LastSnapshot = snapshot;
        info.LastSnapshotSend = Time.realtimeSinceStartup;
        info.HasSnapshot = true;
        info.Priority = 0f;
    }

    private void ApplyHostPickup(
        UdpSession session,
        uint sceneId,
        float now,
        PlayerCharacter hostPlayer,
        FishPickupRequest request)
    {
        if (request.SceneId != sceneId)
        {
            _trace?.Write("PICKUP-REJECT", $"id={request.Id} reason=scene");
            return;
        }
        if (hostPlayer == null || !TryGetRemotePlayer(session, sceneId, now, out var remotePlayer))
        {
            _trace?.Write("PICKUP-REJECT", $"id={request.Id} reason=player-not-ready");
            RejectHostPickup(session, request);
            return;
        }
        if (!_hostFishById.TryGetValue(request.Id, out var fish))
        {
            RefreshHostFish(session, sceneId, now);
            _hostFishById.TryGetValue(request.Id, out fish);
        }
        var body = fish != null ? fish.GetInteractionBody : null;
        var normalPickup = fish != null && body != null && body.IsEnableInteraction &&
            body.InteractionType == FishInteractionBody.FishInteractionType.Pickup &&
            InRange(fish.transform.position, remotePlayer, CorpsePickupRangeSquared);
        if (!normalPickup)
        {
            _log.LogWarning($"Network fish pickup rejected: id={request.Id}");
            _trace?.Write("PICKUP-REJECT",
                $"id={request.Id} reason=state fish={fish != null} body={body != null} " +
                $"enabled={body?.IsEnableInteraction} type={body?.InteractionType}");
            RejectHostPickup(session, request);
            return;
        }

        try
        {
            _hostRemovedFish.Add(fish);
            fish.DestroySelf();
            var revision = SendHostDespawn(session, sceneId, request.Id);
            session.SendFishPickupResult(new FishPickupResult(
                sceneId, session.LocalSceneEpoch, request.Id, revision, true));
            RemoveHostFish(request.Id);
            _log.LogInfo($"Network client fish pickup approved: id={request.Id}");
            _trace?.Write("PICKUP-ACCEPT", $"id={request.Id} type={fish.FishDataTID}");
        }
        catch (Exception exception)
        {
            _hostRemovedFish.Remove(fish);
            RejectHostPickup(session, request);
            _log.LogWarning($"Network fish pickup failed: id={request.Id}; {exception.Message}");
            _trace?.Write("PICKUP-ERROR",
                $"id={request.Id} error={exception.GetType().Name}:{exception.Message}");
        }
    }

    private void RejectHostPickup(UdpSession session, FishPickupRequest request) =>
        session.SendFishPickupResult(new FishPickupResult(
            request.SceneId, session.LocalSceneEpoch, request.Id, NextRevision(_fishTick), false));

    private void ApplyClientPickupResult(FishPickupResult result, PlayerCharacter player)
    {
        _pendingClientPickups.Remove(result.Id);
        if (!result.Accepted)
        {
            player?.SuccessInteraction();
            _log.LogWarning($"Network fish pickup was rejected: id={result.Id}");
            _trace?.Write("PICKUP-RESULT", $"id={result.Id} accepted=false");
            return;
        }

        RecordTombstone(result.Id, result.Revision);
        if (!_targets.TryGetValue(result.Id, out var target) || target.Fish == null || player == null)
        {
            player?.SuccessInteraction();
            RemoveClientTarget(result.Id, true);
            _log.LogWarning($"Network fish pickup approval had no local target: id={result.Id}");
            _trace?.Write("PICKUP-RESULT", $"id={result.Id} accepted=true local=missing");
            return;
        }

        var fish = target.Fish;
        try
        {
            var body = fish.GetInteractionBody;
            if (body == null)
                throw new InvalidOperationException("fish interaction body is missing");
            _applyingClientPickup = true;
            body.SuccessInteract(player);
            _log.LogInfo($"Network client fish pickup completed: id={result.Id}");
            _trace?.Write("PICKUP-RESULT", $"id={result.Id} accepted=true local=completed");
        }
        catch (Exception exception)
        {
            player.SuccessInteraction();
            _log.LogWarning($"Network client fish pickup completion failed: id={result.Id}; {exception.Message}");
            _trace?.Write("PICKUP-ERROR",
                $"id={result.Id} local error={exception.GetType().Name}:{exception.Message}");
        }
        finally
        {
            _applyingClientPickup = false;
            RemoveClientTarget(result.Id, true);
        }
    }

    private void ApplyClientActionAck(
        UdpSession session, uint sceneId, uint sceneEpoch, float now, FishActionAck ack,
        PlayerCharacter player)
    {
        if (ack.Action is FishAction.Capture or FishAction.CorpsePickup &&
            _clientLootRequests.TryGetValue(ack.RequestId, out var lootRequest))
        {
            if (ack.SceneId != sceneId || ack.SceneEpoch != sceneEpoch ||
                lootRequest.Request.Id != ack.Id || lootRequest.Request.Action != ack.Action)
                return;
            if (_pendingClientActions.Remove(ack.RequestId, out var lootPending))
                ClearPendingFish(_pendingClientActionByFish, lootPending, ack.RequestId);
            if (ack.Result == FishActionResult.Rejected)
            {
                ReleaseClientLootInteraction(lootRequest, player);
                _clientLootRequests.Remove(ack.RequestId);
                _clientLootGrants.Remove(ack.RequestId);
                _clientGrantActions.Remove(ack.RequestId);
                _clientGrantFish.Remove(ack.RequestId);
                if (_targets.TryGetValue(ack.Id, out var rejectedTarget) &&
                    rejectedTarget.Fish != null && ack.Revision != 0)
                {
                    rejectedTarget.Revision = ack.Revision;
                    rejectedTarget.Hp = ack.Hp;
                    rejectedTarget.Phase = ack.Phase;
                    rejectedTarget.Flags = FlagsForPhase(rejectedTarget.Flags, ack.Phase);
                    ApplyClientState(rejectedTarget);
                }
                return;
            }
            lootRequest.Ack = ack;
            if (ack.Action == FishAction.Capture)
            {
                _suppressingNativeRecallOutcome = false;
                var fish = FindClientFish(ack.Id);
                if (fish != null)
                    CleanupClientHook(ack.Id, fish);
                _clientHookLeases.Remove(ack.Id);
                _queuedClientActions.Remove(ack.Id);
            }
            TryApplyClientLootGrant(session, ack.RequestId, player);
            return;
        }
        if (ack.SceneId != sceneId || ack.SceneEpoch != sceneEpoch ||
            !_pendingClientActions.TryGetValue(ack.RequestId, out var pending) ||
            pending.Id != ack.Id || pending.Action != ack.Action ||
            !TryConsumeAck(_pendingClientActions, ack.RequestId))
            return;
        ClearPendingFish(_pendingClientActionByFish, pending, ack.RequestId);
        if (ack.Result == FishActionResult.Rejected &&
            ack.RejectReason == FishActionRejectReason.MissingFish)
        {
            CleanupClientHook(ack.Id, pending.Fish);
            _clientHookLeases.Remove(ack.Id);
            _queuedClientActions.Remove(ack.Id);
            RemoveClientTarget(ack.Id, true);
            return;
        }
        if (ack.Result == FishActionResult.Accepted && ack.Action == FishAction.Hook)
        {
            if (pending.TimedOut)
                CancelClientHook(session, sceneId, ack.Id, pending.Fish, now);
            else
                AcceptClientHookLease(
                    _clientHookLeases, ack.Id, ack.RequestId, now);
        }
        else if (ack.Result == FishActionResult.Accepted && ack.Action == FishAction.Qte)
            RenewClientHookLease(_clientHookLeases, ack.Id, pending.LeaseId, now);
        var finishHook = ack.Action is FishAction.Release or FishAction.Capture ||
            ack.Result == FishActionResult.Rejected &&
            ack.Action is FishAction.Hook or FishAction.Qte;
        if (finishHook)
        {
            CleanupClientHook(ack.Id, pending.Fish);
            _clientHookLeases.Remove(ack.Id);
            _queuedClientActions.Remove(ack.Id);
        }
        if (!_targets.TryGetValue(ack.Id, out var target) || target.Fish == null ||
            target.Revision != 0 && ack.Revision != target.Revision &&
            !IsNewer(ack.Revision, target.Revision))
        {
            TrySendNextClientAction(session, sceneId, pending.Id, pending.Fish, now);
            return;
        }
        target.Revision = ack.Revision;
        target.Hp = ack.Hp;
        target.Phase = ack.Phase;
        target.Flags = FlagsForPhase(target.Flags, ack.Phase);
        if (!HasActiveClientHookLease(_clientHookLeases, ack.Id, now))
            ApplyClientState(target);
        _trace?.Write("FISH-ACTION-ACK",
            $"request={ack.RequestId} id={ack.Id} result={ack.Result} " +
            $"reason={ack.RejectReason} revision={ack.Revision} hp={ack.Hp:F1}");
        TrySendNextClientAction(session, sceneId, pending.Id, pending.Fish, now);
    }

    private void ApplyClientLootGrant(
        UdpSession session, uint sceneId, uint sceneEpoch, FishLootGrant grant,
        PlayerCharacter player)
    {
        if (grant.SceneId != sceneId || grant.SceneEpoch != sceneEpoch)
            return;
        if (!_clientLootRequests.TryGetValue(grant.RequestId, out var request))
        {
            if (_ledger?.HasAppliedFishTransaction(grant.TransactionId) != true)
                return;
            request = new ClientLootRequest
            {
                Request = new FishActionRequest(
                    grant.RequestId, grant.SceneId, grant.SceneEpoch, grant.FishId,
                    grant.Revision, grant.Action == FishAction.Capture ? grant.RequestId : 0,
                    grant.Action, 0, 0, 0),
                Started = Time.realtimeSinceStartup,
                NextReplay = Time.realtimeSinceStartup + ActionTimeoutSeconds,
                Released = true,
                Presented = true
            };
            _clientLootRequests[grant.RequestId] = request;
            _clientGrantActions[grant.RequestId] = grant.Action;
            _clientGrantFish[grant.RequestId] = grant.FishId;
        }
        if (request.Request.Id != grant.FishId || request.Request.Action != grant.Action)
            return;
        if (!_clientLootGrants.TryGetValue(grant.RequestId, out var assembly))
        {
            assembly = new GrantAssembly { Expected = grant.EntryCount };
            _clientLootGrants[grant.RequestId] = assembly;
        }
        if (assembly.Expected != grant.EntryCount)
            return;
        var isNew = !assembly.Entries.ContainsKey(grant.Index);
        assembly.Entries[grant.Index] = grant;
        if (isNew)
            request.NextReplay = Time.realtimeSinceStartup + ActionTimeoutSeconds;
        TryApplyClientLootGrant(session, grant.RequestId, player);
    }

    private void TryApplyClientLootGrant(
        UdpSession session, ulong requestId, PlayerCharacter player)
    {
        if (!_clientLootRequests.TryGetValue(requestId, out var request) ||
            request.Ack is not { } ack ||
            !_clientLootGrants.TryGetValue(requestId, out var assembly) ||
            assembly.Entries.Count != assembly.Expected)
            return;
        var grants = new List<FishLootGrant>(assembly.Expected);
        for (ushort index = 0; index < assembly.Expected; index++)
            if (!assembly.Entries.TryGetValue(index, out var grant))
                return;
            else
                grants.Add(grant);
        if (!RemoteCatchLedger.ExactGrantSet(grants) ||
            grants[0].RequestId != request.Request.RequestId ||
            grants[0].FishId != request.Request.Id ||
            grants[0].Action != request.Request.Action || grants[0].Revision != ack.Revision ||
            ack.Id != request.Request.Id || ack.Action != request.Request.Action)
            return;

        if (!request.Presented &&
            _targets.TryGetValue(grants[0].FishId, out var target) && target.Fish != null)
        {
            request.Presented = true;
            var fish = target.Fish;
            _ledger?.BeginFishPresentation();
            ProbeBehaviour.Instance?.BeginClientPresentationLifecycle();
            _applyingClientPickup = true;
            try
            {
                if (request.Request.Action == FishAction.Capture)
                    fish.WinFromProjectileinFight();
                else if (fish.GetInteractionBody != null && player != null)
                    fish.GetInteractionBody.SuccessInteract(player);
                else
                    fish.SuccessNetPickupFish(true);
            }
            catch (Exception exception)
            {
                _trace?.Write("FISH-PRESENTATION-ERROR",
                    $"transaction={grants[0].TransactionId} error={exception.Message}");
            }
            finally
            {
                _applyingClientPickup = false;
                ProbeBehaviour.Instance?.EndClientPresentationLifecycle();
                _ledger?.EndFishPresentation();
            }
        }
        if (_ledger?.ApplyFishTransaction(grants) != true)
            return;
        var complete = new FishLootComplete(
            grants[0].TransactionId, grants[0].SceneId, grants[0].SceneEpoch,
            grants[0].RequestId, grants[0].FishId, grants[0].Revision, grants[0].Action);
        if (!session.SendFishLootComplete(complete))
            return;
        _clientLootGrants.Remove(requestId);
        _clientGrantActions.Remove(requestId);
        _clientGrantFish.Remove(requestId);
        _clientLootRequests.Remove(requestId);
        RecordTombstone(grants[0].FishId, grants[0].Revision);
        RemoveClientTarget(grants[0].FishId, true);
        _trace?.Write("FISH-LOOT-APPLY",
            $"transaction={grants[0].TransactionId} request={requestId} " +
            $"id={grants[0].FishId} entries={grants.Count} weight={grants[0].CarriedWeight:F2}");
    }

    private void UpdateClientLootRequests(
        UdpSession session, uint sceneId, uint sceneEpoch, float now, PlayerCharacter player)
    {
        foreach (var pair in new List<KeyValuePair<ulong, ClientLootRequest>>(_clientLootRequests))
        {
            var request = pair.Value;
            TryApplyClientLootGrant(session, pair.Key, player);
            if (!_clientLootRequests.ContainsKey(pair.Key))
                continue;
            if (!request.Released && now >= request.Started + ActionTimeoutSeconds)
            {
                ReleaseClientLootInteraction(request, player);
                if (_clientTombstones.ContainsKey(request.Request.Id))
                    RemoveClientTarget(request.Request.Id, true);
            }
            if (now < request.NextReplay)
                continue;
            request.NextReplay = now + 1f;
            var replay = request.Request with { SceneEpoch = sceneEpoch };
            request.Request = replay;
            session.SendFishActionRequest(replay);
            _trace?.Write("FISH-LOOT-REPLAY",
                $"request={pair.Key} id={replay.Id} action={replay.Action}");
        }
    }

    private void ReleaseClientLootInteraction(
        ClientLootRequest request, PlayerCharacter player)
    {
        if (request.Released)
            return;
        request.Released = true;
        if (request.Request.Action == FishAction.Capture)
        {
            var fish = FindClientFish(request.Request.Id);
            if (fish != null)
                CleanupClientHook(request.Request.Id, fish);
            _clientHookLeases.Remove(request.Request.Id);
            _queuedClientActions.Remove(request.Request.Id);
        }
        player?.SuccessInteraction();
    }

    private void ExpireClientActions(UdpSession session, uint sceneId, float now)
    {
        foreach (var pair in new List<KeyValuePair<ulong, PendingAction>>(_pendingClientActions))
        {
            if (!TryExpirePendingAction(
                    _pendingClientActions, _pendingClientActionByFish,
                    pair.Key, now, out var expired))
                continue;
            _trace?.Write("FISH-ACTION-TIMEOUT", $"request={pair.Key} id={expired.Id}");
            if (expired.Action == FishAction.Hook)
            {
                _queuedClientActions.Remove(expired.Id);
                ScheduleClientHookCleanup(expired.Id);
            }
        }
        foreach (var pair in new List<KeyValuePair<int, ClientHookLease>>(_clientHookLeases))
            if (pair.Value.Accepted && now >= pair.Value.Expires && !pair.Value.ReleaseQueued)
            {
                if (!_targets.TryGetValue(pair.Key, out var target) || target.Fish == null)
                {
                    _clientHookLeases.Remove(pair.Key);
                    continue;
                }
                var queued = EnqueueClientAction(
                    session, sceneId, pair.Key, target.Fish, FishAction.Release,
                    0, 0, 0, now);
                if (TryMarkLeaseActionQueued(pair.Value, FishAction.Release, queued))
                    ScheduleClientHookCleanup(pair.Key);
                else
                    CancelClientHook(session, sceneId, pair.Key, target.Fish, now);
            }
        foreach (var pair in new List<KeyValuePair<int, Queue<QueuedAction>>>(_queuedClientActions))
            if (!_pendingClientActionByFish.ContainsKey(pair.Key) &&
                _targets.TryGetValue(pair.Key, out var target) && target.Fish != null)
                TrySendNextClientAction(session, sceneId, pair.Key, target.Fish, now);
    }

    private void ReceiveClientManifest(uint sceneId, uint sceneEpoch, FishManifest manifest)
    {
        if (manifest.SceneId != sceneId || manifest.SceneEpoch != sceneEpoch ||
            _latestClientManifestRevision != 0 &&
            manifest.Revision != _latestClientManifestRevision &&
            !IsNewer(manifest.Revision, _latestClientManifestRevision))
            return;
        var assembly = GetManifestAssembly(manifest.SceneId, manifest.SceneEpoch, manifest.Revision);
        assembly.Entries[manifest.Id] = manifest;
        TryActivateClientManifest(assembly);
    }

    private void ReceiveClientManifestState(uint sceneId, uint sceneEpoch, FishManifestState state)
    {
        if (state.SceneId != sceneId || state.SceneEpoch != sceneEpoch ||
            _latestClientManifestRevision != 0 &&
            state.Revision != _latestClientManifestRevision &&
            !IsNewer(state.Revision, _latestClientManifestRevision))
            return;
        var assembly = GetManifestAssembly(state.SceneId, state.SceneEpoch, state.Revision);
        assembly.Expected = state.EntryCount;
        TryActivateClientManifest(assembly);
    }

    private ManifestAssembly GetManifestAssembly(uint sceneId, uint sceneEpoch, uint revision)
    {
        if (!_clientManifestAssemblies.TryGetValue(revision, out var assembly) ||
            assembly.SceneId != sceneId || assembly.SceneEpoch != sceneEpoch)
        {
            assembly = new ManifestAssembly(sceneId, sceneEpoch, revision);
            _clientManifestAssemblies[revision] = assembly;
        }
        return assembly;
    }

    private void TryActivateClientManifest(ManifestAssembly assembly)
    {
        if (!CanActivateManifest(assembly, _latestClientManifestRevision))
            return;

        _latestClientManifestRevision = assembly.Revision;
        _activeClientManifest.Clear();
        _pendingClientManifests.Clear();
        foreach (var pair in assembly.Entries)
        {
            _activeClientManifest[pair.Key] = pair.Value;
            _pendingClientManifests[pair.Key] = pair.Value;
        }
        foreach (var id in new List<int>(_targets.Keys))
            if (!_activeClientManifest.ContainsKey(id))
                RemoveClientTarget(id, true);
        var suppressed = RetryPendingClientManifests(Time.realtimeSinceStartup)
            ? SuppressUnboundClientFish()
            : 0;
        foreach (var revision in new List<uint>(_clientManifestAssemblies.Keys))
            if (revision != assembly.Revision)
                _clientManifestAssemblies.Remove(revision);
        _log.LogInfo(
            $"Network fish manifest applied: scene={assembly.SceneId:X8}; epoch={assembly.SceneEpoch}; " +
            $"revision={assembly.Revision}; fish={assembly.Expected}; bound={_targets.Count}; " +
            $"suppressed={suppressed}");
        _trace?.Write("MANIFEST-COMPLETE",
            $"scene={assembly.SceneId:X8} epoch={assembly.SceneEpoch} revision={assembly.Revision} " +
            $"expected={assembly.Expected} bound={_targets.Count} suppressed={suppressed}");
    }

    private void ApplyClientManifest(
        FishManifest manifest,
        Dictionary<(string AllocatorUid, int FishDataTID), List<FishAISystem>> availableFish)
    {
        if (_clientTombstones.TryGetValue(manifest.Id, out var removalRevision))
        {
            if (RemovalWins(removalRevision, manifest.FishRevision))
            {
                _pendingClientManifests.Remove(manifest.Id);
                return;
            }
            _clientTombstones.Remove(manifest.Id);
        }
        if (_targets.TryGetValue(manifest.Id, out var existing) && existing.Fish != null &&
            existing.AllocatorUid == manifest.AllocatorUid &&
            existing.FishDataTID == manifest.FishDataTID)
        {
            if (existing.Revision != 0 && manifest.FishRevision != existing.Revision &&
                !IsNewer(manifest.FishRevision, existing.Revision))
            {
                _pendingClientManifests.Remove(manifest.Id);
                return;
            }
            var revisionAdvanced = existing.Revision == 0 ||
                IsNewer(manifest.FishRevision, existing.Revision);
            ApplyManifestBaseline(existing, manifest);
            ApplySnapshotPhase(existing, manifest.Flags, manifest.Hp, revisionAdvanced);
            if (ShouldHideClientTarget(
                    existing.Interested, existing.Fish.gameObject.activeInHierarchy))
                existing.Fish.gameObject.SetActive(false);
            if (!HasActiveClientHookLease(
                    _clientHookLeases, manifest.Id, Time.realtimeSinceStartup))
                ApplyClientState(existing);
            _pendingClientManifests.Remove(manifest.Id);
            return;
        }
        if (existing != null)
            RemoveClientTarget(manifest.Id, false);

        // Bind only fish created by the game's allocator. Creating or destroying
        // allocator entries while its async spawn routine is enumerating the pool
        // corrupts the native collection and causes a per-frame exception storm.
        var fish = TakeUnboundFish(manifest, availableFish);
        if (fish == null)
        {
            if (manifest.Revision != 0)
                _pendingClientManifests[manifest.Id] = manifest;
            if (_missingAllocatorIds.Add(manifest.Id))
            {
                _log.LogDebug($"Network fish manifest waiting for native fish: id={manifest.Id}");
                _trace?.Write("BIND-WAIT",
                    $"id={manifest.Id} uid={manifest.AllocatorUid} type={manifest.FishDataTID} " +
                    $"revision={manifest.Revision}");
            }
            return;
        }

        var localPosition = fish.transform.position;
        FishBehaviorTreeState.ObserveActive(fish);
        FishBehaviorTreeState.TryGet(fish, out var nativeBehaviorEnabled);
        var target = new Target
        {
            Fish = fish,
            Renderer = fish.GetComponentInChildren<SpriteRenderer>(true),
            AllocatorUid = manifest.AllocatorUid,
            FishDataTID = manifest.FishDataTID,
            Phase = PhaseFromFlags(manifest.Flags, manifest.Hp),
            NativeActive = fish.gameObject.activeInHierarchy,
            NativeBehaviorEnabled = nativeBehaviorEnabled
        };
        ApplyManifestBaseline(target, manifest);
        _applyingClientState = true;
        try
        {
            fish.transform.position = target.Position;
            fish.Rotation = target.Rotation;
            ApplyClientState(target);
        }
        finally
        {
            _applyingClientState = false;
        }
        SetClientSimulation(fish, false, manifest.Id);
        _targets[manifest.Id] = target;
        _clientIdsByFish[fish] = manifest.Id;
        fish.gameObject.SetActive(target.Interested);
        _pendingClientManifests.Remove(manifest.Id);
        _missingAllocatorIds.Remove(manifest.Id);
        _log.LogDebug($"Network fish manifest bound: id={manifest.Id}; type={manifest.FishDataTID}");
        _trace?.Write("BIND",
            $"id={manifest.Id} uid={manifest.AllocatorUid} type={manifest.FishDataTID} " +
            $"instance={fish.GetInstanceID()} " +
            $"offset={Vector3.Distance(localPosition, target.Position):F2} revision={manifest.Revision}");
        if (_pendingClientLifecycles.TryGetValue(manifest.Id, out var lifecycle))
            ApplyClientLifecycle(manifest.SceneId, manifest.SceneEpoch, lifecycle);
    }

    private bool RetryPendingClientManifests(float now)
    {
        if (_pendingClientManifests.Count == 0 || !ManifestRetryDue(now, _nextClientBind))
            return false;
        _nextClientBind = now + 0.1f;
        IndexAvailableClientFish(_appliedClientSceneId);
        _manifestScratch.Clear();
        _manifestScratch.AddRange(_pendingClientManifests.Values);
        foreach (var manifest in _manifestScratch)
            ApplyClientManifest(manifest, _availableClientFishScratch);
        return true;
    }

    private void ReleaseClientTargets()
    {
        foreach (var pair in new List<KeyValuePair<int, ClientHookLease>>(_clientHookLeases))
        {
            var fish = FindClientFish(pair.Key);
            if (fish != null)
                CleanupClientHook(pair.Key, fish);
        }
        _clientHookLeases.Clear();
        _scheduledClientHookCleanup.Clear();
        foreach (var pair in _targets)
            if (pair.Value.Fish != null)
                RestoreNativeTarget(pair.Value, pair.Key);
        _targets.Clear();
        _clientIdsByFish.Clear();
        foreach (var removal in _deferredClientRemovals)
            if (removal.Fish != null)
            {
                SetClientSimulation(
                    removal.Fish, removal.NativeBehaviorEnabled, removal.Id);
                removal.Fish.gameObject.SetActive(removal.NativeActive);
            }
        _deferredClientRemovals.Clear();
        RestoreSuppressedClientFish();
    }

    private FishAISystem FindClientFish(int id)
    {
        if (_targets.TryGetValue(id, out var target) && target.Fish != null)
            return target.Fish;
        foreach (var removal in _deferredClientRemovals)
            if (removal.Id == id && removal.Fish != null)
                return removal.Fish;
        if (_pendingClientActionByFish.TryGetValue(id, out var requestId) &&
            _pendingClientActions.TryGetValue(requestId, out var pending))
            return pending.Fish;
        return null;
    }

    private void ResetClientManifest()
    {
        _pendingClientActions.Clear();
        _pendingClientActionByFish.Clear();
        _queuedClientActions.Clear();
        _clientHookLeases.Clear();
        _scheduledClientHookCleanup.Clear();
        _clientTombstones.Clear();
        _pendingClientPickups.Clear();
        _missingAllocatorIds.Clear();
        _pendingClientManifests.Clear();
        _pendingClientLifecycles.Clear();
        _clientManifestAssemblies.Clear();
        _activeClientManifest.Clear();
        _latestClientManifestRevision = 0;
        _nextClientBind = 0f;
        _latestClientTick = 0;
        _clientLatestTick = 0d;
        _clientRenderTick = 0d;
        _hasClientClock = false;
        _appliedClientSceneId = 0;
        _appliedClientSceneEpoch = 0;
    }

    private void ApplyClientSnapshot(uint sceneId, uint sceneEpoch, FishSnapshot snapshot)
    {
        if (snapshot.SceneId != sceneId || snapshot.SceneEpoch != sceneEpoch ||
            !_targets.TryGetValue(snapshot.Id, out var target) || target.Fish == null ||
            target.Revision != 0 && snapshot.Revision != target.Revision &&
                !IsNewer(snapshot.Revision, target.Revision) ||
            target.Tick != 0 && !IsNewer(snapshot.Tick, target.Tick))
        {
            _clientSnapshotsRejected++;
            return;
        }

        var previous = target.Samples.Count == 0 ? default : target.Samples[^1];
        var sample = new TimedSample(
            snapshot.Tick, snapshot.X, snapshot.Y, snapshot.Z,
            snapshot.VelocityX, snapshot.VelocityY, snapshot.Rotation);
        var revisionAdvanced = target.Revision == 0 || IsNewer(snapshot.Revision, target.Revision);
        AddSnapshot(target, sample);
        target.Revision = snapshot.Revision;
        target.Tick = snapshot.Tick;
        target.Hp = snapshot.Hp;
        target.Flags = snapshot.Flags;
        if (!target.Interested)
        {
            target.Interested = true;
            target.Fish.gameObject.SetActive(true);
            SetClientSimulation(target.Fish, false, snapshot.Id);
        }
        ApplySnapshotPhase(target, snapshot.Flags, snapshot.Hp, revisionAdvanced);
        target.HasSnapshot = true;
        if (target.AwaitingLeaseSnapshot &&
            (target.AwaitingLeaseSnapshotTick == 0 ||
             IsNewer(snapshot.Tick, target.AwaitingLeaseSnapshotTick)))
        {
            target.AwaitingLeaseSnapshot = false;
            target.CorrectionFrom = target.Fish.transform.position;
            target.CorrectionRotation = target.Fish.Rotation;
            target.CorrectionStarted = Time.realtimeSinceStartup;
            var dx = snapshot.X - target.CorrectionFrom.x;
            var dy = snapshot.Y - target.CorrectionFrom.y;
            var dz = snapshot.Z - target.CorrectionFrom.z;
            target.CorrectingLeasePresentation = dx * dx + dy * dy + dz * dz <= 64f;
            _trace?.Write("LEASE-CORRECTION",
                $"id={snapshot.Id} tick={snapshot.Tick} blend={target.CorrectingLeasePresentation}");
        }
        if (!HasActiveClientHookLease(
                _clientHookLeases, snapshot.Id, Time.realtimeSinceStartup))
            ApplyClientState(target);
        _clientSnapshotsAccepted++;
        if (previous.Tick != 0 && DistanceSquared(previous, sample) >= 64f)
        {
            _clientTeleports++;
            _trace?.Write("TELEPORT",
                $"id={snapshot.Id} tick={snapshot.Tick} from=({previous.X:F2},{previous.Y:F2}) " +
                $"to=({sample.X:F2},{sample.Y:F2})");
        }
    }

    private void ApplyClientLifecycle(uint sceneId, uint sceneEpoch, FishLifecycle lifecycle)
    {
        if (lifecycle.SceneId != sceneId || lifecycle.SceneEpoch != sceneEpoch)
            return;
        if (lifecycle.Kind == FishLifecycleKind.Despawn)
        {
            ApplyClientRemoval(sceneId, sceneEpoch, lifecycle.Id, lifecycle.Revision, "lifecycle");
            return;
        }
        if (lifecycle.Kind == FishLifecycleKind.Spawn &&
            _clientTombstones.TryGetValue(lifecycle.Id, out var removalRevision) &&
            IsNewer(lifecycle.Revision, removalRevision))
            _clientTombstones.Remove(lifecycle.Id);
        if (!_targets.TryGetValue(lifecycle.Id, out var target) || target.Fish == null)
        {
            if (!_pendingClientLifecycles.TryGetValue(lifecycle.Id, out var pending) ||
                IsNewer(lifecycle.Revision, pending.Revision))
                _pendingClientLifecycles[lifecycle.Id] = lifecycle;
            return;
        }
        if (target.Revision != 0 && !IsNewer(lifecycle.Revision, target.Revision))
            return;
        if (lifecycle.Kind is FishLifecycleKind.InterestEnter or FishLifecycleKind.InterestLeave)
        {
            target.Revision = lifecycle.Revision;
            target.Hp = lifecycle.Hp;
            target.Phase = lifecycle.Phase;
            target.Interested = lifecycle.Kind == FishLifecycleKind.InterestEnter;
            target.HasSnapshot = false;
            if (!target.Interested)
                target.Samples.Clear();
            target.Fish.gameObject.SetActive(target.Interested);
            if (target.Interested)
            {
                SetClientSimulation(target.Fish, false, lifecycle.Id);
                ApplyClientState(target);
            }
            _pendingClientLifecycles.Remove(lifecycle.Id);
            _trace?.Write("INTEREST",
                $"id={lifecycle.Id} revision={lifecycle.Revision} interested={target.Interested}");
            return;
        }
        target.Revision = lifecycle.Revision;
        target.Hp = lifecycle.Hp;
        target.Phase = lifecycle.Phase;
        target.Flags = (byte)(target.Flags & ~3);
        if (lifecycle.Phase == FishPhase.Captured)
            target.Flags |= 2;
        else if (lifecycle.Phase == FishPhase.Corpse)
            target.Flags |= 1;
        if (!HasActiveClientHookLease(
                _clientHookLeases, lifecycle.Id, Time.realtimeSinceStartup))
            ApplyClientState(target);
        _pendingClientLifecycles.Remove(lifecycle.Id);
        _trace?.Write("LIFECYCLE",
            $"id={lifecycle.Id} revision={lifecycle.Revision} kind={lifecycle.Kind} " +
            $"phase={lifecycle.Phase} hp={lifecycle.Hp:F1}");
    }

    private void ApplyClientRemoval(
        uint sceneId, uint sceneEpoch, int id, uint revision, string source)
    {
        if (!_targets.TryGetValue(id, out var target))
        {
            if (_activeClientManifest.TryGetValue(id, out var manifest) &&
                manifest.FishRevision != 0 && !RemovalWins(revision, manifest.FishRevision) ||
                _pendingClientLifecycles.TryGetValue(id, out var lifecycle) &&
                lifecycle.Revision != 0 && !RemovalWins(revision, lifecycle.Revision))
                return;
            RecordTombstone(id, revision);
            return;
        }
        if (target.Revision != 0 && !RemovalWins(revision, target.Revision))
            return;
        target.Revision = revision;
        RecordTombstone(id, revision);
        if (HasPendingClientLoot(id))
            return;
        RemoveClientTarget(id, true);
        _trace?.Write("REMOVE-ACCEPT", $"id={id} revision={revision} source={source}");
    }

    private bool HasPendingClientLoot(int id)
    {
        foreach (var pending in _pendingClientActions.Values)
            if (pending.Id == id && pending.Action is FishAction.Capture or FishAction.CorpsePickup)
                return true;
        foreach (var pair in _clientGrantActions)
            if (_clientGrantFish.TryGetValue(pair.Key, out var fishId) && fishId == id &&
                _clientLootRequests.TryGetValue(pair.Key, out var request) && !request.Released)
                return true;
        return false;
    }

    private bool HasClientLootRequestForFish(int id)
    {
        foreach (var request in _clientLootRequests.Values)
            if (request.Request.Id == id)
                return true;
        return false;
    }

    private void AddSnapshot(Target target, TimedSample sample)
    {
        InsertSample(target.Samples, sample);
        if (_hasClientClock)
            PruneSamples(target.Samples, _clientRenderTick);
        var unwrapped = _latestClientTick == 0
            ? sample.Tick
            : UnwrapTick(sample.Tick, _latestClientTick);
        if (!_hasClientClock)
        {
            _latestClientTick = unwrapped;
            _clientLatestTick = unwrapped;
            _clientRenderTick = unwrapped - InterpolationTicks;
            _hasClientClock = true;
        }
        else if (unwrapped > _latestClientTick)
        {
            _latestClientTick = unwrapped;
            _clientLatestTick = unwrapped;
        }
    }

    private void AdvanceClientClock(float deltaTime)
    {
        if (!_hasClientClock)
            return;
        var elapsed = Math.Max(0f, deltaTime);
        _clientLatestTick += elapsed * SnapshotTicksPerSecond;
        _clientRenderTick = CorrectRenderClock(
            _clientRenderTick, _clientLatestTick, elapsed);
    }

    internal void ApplyClientAuthoritativeAfterPresentation(FishAISystem fish)
    {
        if (fish == null ||
            !_clientIdsByFish.TryGetValue(fish, out var id) ||
            !_targets.TryGetValue(id, out var target) ||
            !ShouldApplyAfterPresentation(
                _hasClientClock, target.Samples.Count > 0,
                HasActiveClientHookLease(
                    _clientHookLeases, id, Time.realtimeSinceStartup),
                target.AwaitingLeaseSnapshot))
            return;
        PruneSamples(target.Samples, _clientRenderTick);
        var rendered = SampleAt(target.Samples, _clientRenderTick);
        target.Position = new Vector3(rendered.X, rendered.Y, rendered.Z);
        target.Rotation = rendered.Rotation;
        if (target.CorrectingLeasePresentation)
        {
            var t = Mathf.Clamp01(
                (Time.realtimeSinceStartup - target.CorrectionStarted) / LeaseCorrectionSeconds);
            fish.transform.position = Vector3.Lerp(target.CorrectionFrom, target.Position, t);
            fish.Rotation = Mathf.LerpAngle(target.CorrectionRotation, target.Rotation, t);
            target.CorrectingLeasePresentation = t < 1f;
        }
        else
        {
            fish.transform.position = target.Position;
            fish.Rotation = target.Rotation;
        }
        ApplyClientState(target);
    }

    internal void PublishClientHookPose(
        UdpSession session, uint sceneId, FishAISystem fish, float now)
    {
        if (session == null || fish == null ||
            !_clientIdsByFish.TryGetValue(fish, out var id) ||
            !_targets.TryGetValue(id, out var target) ||
            target.Phase is not (FishPhase.Hooked or FishPhase.Qte) ||
            !_clientHookLeases.TryGetValue(id, out var lease) || !lease.Accepted ||
            lease.EndedPending || lease.ReleaseQueued || lease.CaptureQueued ||
            now >= lease.Expires || now < lease.NextPoseSend)
            return;
        var position = fish.transform.position;
        var velocity = fish.Velocity;
        var pose = new FishHookPose(
            sceneId, session.RemoteSceneEpoch, id, lease.RequestId,
            lease.PoseTick = NextRevision(lease.PoseTick),
            position.x, position.y, position.z, fish.Rotation, velocity.x, velocity.y);
        if (!HookPoseFinite(pose) || !session.SendFishHookPose(pose))
            return;
        lease.NextPoseSend = now + HookPoseIntervalSeconds;
    }

    private void ApplyClientState(Target target)
    {
        if (target.Fish == null)
            return;
        var wasApplying = _applyingClientState;
        _applyingClientState = true;
        try
        {
            if (Mathf.Abs(target.Fish.HP - target.Hp) >= 0.01f)
                target.Fish.SetHP(Mathf.Max(0f, target.Hp));
            var corpse = target.Phase == FishPhase.Corpse || (target.Flags & 1) != 0;
            var captured = target.Phase == FishPhase.Captured || (target.Flags & 2) != 0;
            if (target.Fish.IsCorpse != corpse)
                target.Fish.IsCorpse = corpse;
            if (target.Fish.IsFishCaptured != captured)
                target.Fish.IsFishCaptured = captured;
            var enabled = (target.Flags & 4) != 0;
            if (target.Fish.IsFishEnable != enabled)
                target.Fish.IsFishEnable = enabled;
            var flipped = (target.Flags & 16) != 0;
            if (!target.HasAppliedFlip || target.AppliedFlip != flipped)
            {
                ApplyFishFlip(target.Renderer, flipped);
                target.HasAppliedFlip = true;
                target.AppliedFlip = flipped;
            }
        }
        finally
        {
            _applyingClientState = wasApplying;
        }
    }

    private static FishPhase PhaseFromFlags(byte flags, float hp) =>
        (flags & 2) != 0 ? FishPhase.Captured :
        (flags & 1) != 0 || hp <= 0f ? FishPhase.Corpse : FishPhase.Alive;

    private static void ApplyManifestBaseline(Target target, FishManifest manifest)
    {
        target.Position = new Vector3(manifest.X, manifest.Y, manifest.Z);
        target.Rotation = manifest.Rotation;
        target.Hp = manifest.Hp;
        target.Flags = manifest.Flags;
        target.Revision = manifest.FishRevision;
        target.Interested = ManifestInterested(manifest.Flags);
    }

    private static byte FlagsForPhase(byte flags, FishPhase phase)
    {
        flags = (byte)(flags & ~3);
        if (phase == FishPhase.Captured)
            return (byte)(flags | 2);
        return phase == FishPhase.Corpse ? (byte)(flags | 1) : flags;
    }

    private static void ApplySnapshotPhase(
        Target target, byte flags, float hp, bool revisionAdvanced)
    {
        var phase = PhaseFromFlags(flags, hp);
        if (revisionAdvanced || target.Phase is FishPhase.None or FishPhase.Alive ||
            phase is FishPhase.Captured or FishPhase.Corpse)
            target.Phase = phase;
    }

    private static bool InsertSample(List<TimedSample> samples, TimedSample sample)
    {
        sample.TimeTick = samples.Count == 0
            ? sample.Tick
            : UnwrapTick(sample.Tick, samples[^1].TimeTick);
        var index = samples.BinarySearch(sample, TimedSampleComparer.Instance);
        if (index >= 0)
        {
            samples[index] = sample;
            return false;
        }
        samples.Insert(~index, sample);
        if (samples.Count > MaxSamples)
            samples.RemoveAt(0);
        return true;
    }

    private static void PruneSamples(List<TimedSample> samples, double renderTick)
    {
        while (samples.Count > 2 && samples[1].TimeTick <= renderTick)
            samples.RemoveAt(0);
    }

    private sealed class TimedSampleComparer : IComparer<TimedSample>
    {
        internal static readonly TimedSampleComparer Instance = new();
        public int Compare(TimedSample left, TimedSample right) =>
            left.TimeTick.CompareTo(right.TimeTick);
    }

    private static TimedSample SampleAt(List<TimedSample> samples, double renderTick)
    {
        if (samples.Count == 1)
            return SampleAt(samples[0], default, renderTick);
        var next = 0;
        while (next < samples.Count && samples[next].TimeTick < renderTick)
            next++;
        if (next == 0)
            return samples[0];
        if (next == samples.Count)
            return SampleAt(samples[^1], default, renderTick);
        return SampleAt(samples[next - 1], samples[next], renderTick);
    }

    private static TimedSample SampleAt(TimedSample previous, TimedSample next, double renderTick)
    {
        if (next.Tick == 0 && next.TimeTick == 0)
        {
            var extrapolationSeconds = (float)Math.Clamp(
                (renderTick - previous.TimeTick) / SnapshotTicksPerSecond,
                0d, MaxExtrapolationTicks / SnapshotTicksPerSecond);
            return new TimedSample(previous.Tick,
                previous.X + previous.VelocityX * extrapolationSeconds,
                previous.Y + previous.VelocityY * extrapolationSeconds,
                previous.Z, previous.VelocityX, previous.VelocityY, previous.Rotation);
        }
        if (DistanceSquared(previous, next) >= 64f)
            return renderTick < next.TimeTick ? previous : next;
        var tickSpan = next.TimeTick - previous.TimeTick;
        if (tickSpan <= 0)
            return next;
        var t = (float)Math.Clamp((renderTick - previous.TimeTick) / tickSpan, 0d, 1d);
        var seconds = (float)(tickSpan / SnapshotTicksPerSecond);
        var t2 = t * t;
        var t3 = t2 * t;
        var h00 = 2f * t3 - 3f * t2 + 1f;
        var h10 = t3 - 2f * t2 + t;
        var h01 = -2f * t3 + 3f * t2;
        var h11 = t3 - t2;
        return new TimedSample(previous.Tick,
            h00 * previous.X + h10 * previous.VelocityX * seconds +
                h01 * next.X + h11 * next.VelocityX * seconds,
            h00 * previous.Y + h10 * previous.VelocityY * seconds +
                h01 * next.Y + h11 * next.VelocityY * seconds,
            Mathf.Lerp(previous.Z, next.Z, t),
            Mathf.Lerp(previous.VelocityX, next.VelocityX, t),
            Mathf.Lerp(previous.VelocityY, next.VelocityY, t),
            Mathf.LerpAngle(previous.Rotation, next.Rotation, t));
    }

    private static float DistanceSquared(TimedSample left, TimedSample right)
    {
        var dx = right.X - left.X;
        var dy = right.Y - left.Y;
        var dz = right.Z - left.Z;
        return dx * dx + dy * dy + dz * dz;
    }

    private static long UnwrapTick(uint tick, long reference)
    {
        var candidate = (reference & ~0xffffffffL) | tick;
        if (candidate - reference > int.MaxValue)
            candidate -= 1L << 32;
        else if (reference - candidate > int.MaxValue)
            candidate += 1L << 32;
        return candidate;
    }

    private static int AddManifestTestEntry(ManifestAssembly assembly, int id)
    {
        assembly.Entries[id] = default;
        return assembly.Entries.Count;
    }

    private static bool CanActivateManifest(ManifestAssembly assembly, uint currentRevision) =>
        assembly.IsComplete && (currentRevision == 0 || assembly.Revision == currentRevision ||
            IsNewer(assembly.Revision, currentRevision));

    private static bool RemovalWins(uint removalRevision, uint fishRevision) =>
        removalRevision == fishRevision || IsNewer(removalRevision, fishRevision);

    private static bool ManifestEntryEligible(bool captured) => !captured;

    private static int NextMissingScans(int missingScans, bool seen) =>
        seen ? 0 : missingScans + 1;

    private static bool ShouldRemoveMissingFish(bool unityNull, int missingScans) =>
        unityNull || missingScans >= MissingAllocatorScanGrace;

    private static bool ShouldSuppressAllocator(string allocatorUid, HashSet<string> represented) =>
        !string.IsNullOrEmpty(allocatorUid) && represented.Contains(allocatorUid);

    private static (string AllocatorUid, int FishDataTID) ManifestFishKey(
        string allocatorUid, int fishDataTID) => (allocatorUid, fishDataTID);

    private static bool ManifestRetryDue(float now, float nextRetry) => now >= nextRetry;

    private static bool SceneScopeChanged(
        uint appliedSceneId, uint appliedSceneEpoch, uint sceneId, uint sceneEpoch) =>
        appliedSceneId != sceneId || appliedSceneEpoch != sceneEpoch;

    private static ulong NextRequestId(ref ulong requestId)
    {
        requestId++;
        if (requestId == 0)
            requestId = 1;
        return requestId;
    }

    private static bool RequestIsNewer(ulong requestId, ulong previous) =>
        unchecked((long)(requestId - previous)) > 0;

    private static bool TryMarkProcessedRequest(
        HashSet<ulong> processed, ref ulong highest, ulong requestId, int windowSize)
    {
        if (requestId == 0 || processed.Contains(requestId) || windowSize <= 0)
            return false;
        if (processed.Count == 0)
            highest = requestId;
        else if (RequestIsNewer(requestId, highest))
            highest = requestId;
        else if (unchecked(highest - requestId) >= (ulong)windowSize)
            return false;
        processed.Add(requestId);
        foreach (var processedId in new List<ulong>(processed))
            if (processedId != highest && !RequestIsNewer(processedId, highest) &&
                unchecked(highest - processedId) >= (ulong)windowSize)
                processed.Remove(processedId);
        return true;
    }

    private static bool TryEnqueueBounded<T>(Queue<T> queue, T value, int capacity)
    {
        if (queue.Count >= capacity)
            return false;
        queue.Enqueue(value);
        return true;
    }

    private static bool TryEnqueueHostLifecycle(
        Queue<FishLifecycle> queue, FishLifecycle lifecycle, System.Action onOverflow)
    {
        if (TryEnqueueBounded(queue, lifecycle, MaxPendingHostLifecycles))
            return true;
        onOverflow();
        return false;
    }

    private void QueueHostLifecycle(UdpSession session, FishLifecycle lifecycle)
    {
        TryEnqueueHostLifecycle(_pendingHostLifecycles, lifecycle, () =>
        {
            _pendingHostLifecycles.Clear();
            session.FailReliableDeliveryFromDomain("fish lifecycle publish queue overflow");
        });
    }

    private static int BoundDamage(int damage) => Math.Min(damage, 10_000);

    private static bool RevisionMatches(uint known, uint current) => known == current;

    private static void BeginClientHookLease(
        Dictionary<int, ClientHookLease> leases, int id, ulong requestId) =>
        leases[id] = new ClientHookLease(requestId);

    private static bool AcceptClientHookLease(
        Dictionary<int, ClientHookLease> leases,
        int id,
        ulong requestId,
        float now)
    {
        if (!leases.TryGetValue(id, out var lease) || lease.RequestId != requestId)
            return false;
        lease.Accepted = true;
        lease.Expires = now + HookLeaseSeconds;
        return true;
    }

    private static bool RenewClientHookLease(
        Dictionary<int, ClientHookLease> leases,
        int id,
        ulong requestId,
        float now)
    {
        if (!leases.TryGetValue(id, out var lease) || !lease.Accepted ||
            lease.RequestId != requestId)
            return false;
        lease.Expires = now + HookLeaseSeconds;
        return true;
    }

    private static bool RenewHostHookLease(
        Dictionary<int, HostHookLease> leases,
        int id,
        ulong requestId,
        float now)
    {
        if (!leases.TryGetValue(id, out var lease) || lease.RequestId != requestId)
            return false;
        lease.Expires = now + HookLeaseSeconds;
        return true;
    }

    private static bool HasActiveClientHookLease(
        Dictionary<int, ClientHookLease> leases, int id, float now) =>
        leases.TryGetValue(id, out var lease) && lease.Accepted && now < lease.Expires;

    private static bool TryMarkClientHookCleanup(ClientHookLease lease)
    {
        if (lease == null || lease.CleanupDone)
            return false;
        lease.CleanupDone = true;
        return true;
    }

    private static bool TryMarkLeaseActionQueued(
        ClientHookLease lease, FishAction action, bool enqueued)
    {
        if (!enqueued)
            return false;
        if (action == FishAction.Release)
            lease.ReleaseQueued = true;
        else if (action == FishAction.Capture)
            lease.CaptureQueued = true;
        else if (action == FishAction.Hook)
            lease.HookQueued = true;
        return true;
    }

    private static bool MarkHookEndedPending(ClientHookLease lease)
    {
        if (lease == null || lease.EndedPending)
            return false;
        lease.EndedPending = true;
        return true;
    }

    private static bool ResolveHookRecall(ClientHookLease lease, bool isSuccess)
    {
        if (lease == null || lease.RecallResolved)
            return false;
        lease.EndedPending = true;
        lease.RecallResolved = true;
        lease.RecallSuccess = isSuccess;
        return true;
    }

    private static bool LeaseIdentityMatches(
        FishAction action, ulong expectedLeaseId, ulong suppliedLeaseId) =>
        action is FishAction.Qte or FishAction.Release or FishAction.Capture
            ? expectedLeaseId != 0 && suppliedLeaseId == expectedLeaseId
            : suppliedLeaseId == 0;

    private static bool ShouldCancelLeaseOnReject(FishAction action, bool leaseMatches) =>
        leaseMatches && action is FishAction.Qte or FishAction.Release or FishAction.Capture;

    private static float? ActionRangeSquared(FishAction action) =>
        action switch
        {
            FishAction.Hook or FishAction.Damage => WeaponRangeSquared,
            FishAction.Capture => CaptureRangeSquared,
            FishAction.CorpsePickup => CorpsePickupRangeSquared,
            _ => null
        };

    private static bool HostActionStateValid(
        FishAction action, FishPhase phase, bool leaseActive) =>
        action switch
        {
            FishAction.Hook => phase == FishPhase.Alive && !leaseActive,
            FishAction.Qte or FishAction.Capture =>
                leaseActive && phase is FishPhase.Hooked or FishPhase.Qte,
            FishAction.Release =>
                leaseActive && phase is FishPhase.Hooked or FishPhase.Qte,
            FishAction.CorpsePickup =>
                !leaseActive && phase is FishPhase.Corpse or FishPhase.Captured,
            _ => true
        };

    private static bool ShouldAllowProxyWrite(
        bool clientAuthority, bool applyingState, bool clientProxy) =>
        !clientAuthority || applyingState || !clientProxy;

    private static bool ShouldAllowSimulation(
        bool clientAuthority, bool applyingState, bool clientProxy, bool presentationLease) =>
        !clientAuthority || applyingState || !clientProxy || presentationLease;

    private static bool ShouldApplyAfterPresentation(
        bool hasClock, bool hasSamples, bool activeLease, bool awaitingLeaseSnapshot) =>
        hasClock && hasSamples && !activeLease && !awaitingLeaseSnapshot;

    private static bool ShouldSuppressPendingClientFishLootPolicy(
        bool fishLootScope,
        bool recallScope,
        bool matchingFish,
        bool hookEndedPending,
        bool capturePending) =>
        fishLootScope || recallScope && matchingFish && (hookEndedPending || capturePending);

    private static bool HookPoseIdentityValid(
        FishHookPose pose, uint sceneId, uint sceneEpoch, int fishId, ulong leaseId) =>
        pose.SceneId == sceneId && pose.SceneEpoch == sceneEpoch &&
        pose.FishId == fishId && pose.LeaseId == leaseId;

    private static bool HookPoseFinite(FishHookPose pose) =>
        float.IsFinite(pose.X) && float.IsFinite(pose.Y) && float.IsFinite(pose.Z) &&
        float.IsFinite(pose.Rotation) && float.IsFinite(pose.VelocityX) &&
        float.IsFinite(pose.VelocityY);

    private static bool HookPoseWithinPlayer(FishHookPose pose, PlayerSnapshot player)
    {
        var dx = pose.X - player.X;
        var dy = pose.Y - player.Y;
        var dz = pose.Z - player.Z;
        return dx * dx + dy * dy + dz * dz <= HookPosePlayerRangeSquared;
    }

    private static bool HookPoseVelocityPlausible(FishHookPose pose) =>
        pose.VelocityX * pose.VelocityX + pose.VelocityY * pose.VelocityY <=
            HookPoseMaxSpeed * HookPoseMaxSpeed;

    private static bool HookPoseMotionPlausible(
        FishHookPose previous, FishHookPose current, float elapsed)
    {
        if (!IsNewer(current.Tick, previous.Tick) || elapsed <= 0f)
            return false;
        var dx = current.X - previous.X;
        var dy = current.Y - previous.Y;
        var dz = current.Z - previous.Z;
        var maxDistance = HookPoseMaxSpeed * elapsed + 0.001f;
        return dx * dx + dy * dy + dz * dz <= maxDistance * maxDistance;
    }

    private static bool HookPoseFromCurrentPlausible(
        Vector3 current, FishHookPose pose, float elapsed)
    {
        if (elapsed <= 0f)
            return false;
        var dx = pose.X - current.x;
        var dy = pose.Y - current.y;
        var dz = pose.Z - current.z;
        var maxDistance = HookPoseMaxSpeed * elapsed + 0.001f;
        return dx * dx + dy * dy + dz * dz <= maxDistance * maxDistance;
    }

    private static ThreatDecision RemoteThreatPolicy(
        bool aggressive,
        float nativeRadius,
        float distance,
        bool active,
        float now,
        float nextRefresh)
    {
        var enter = aggressive ? nativeRadius : Math.Max(nativeRadius, RemoteFearEnterRadius);
        var exit = aggressive ? nativeRadius : Math.Max(nativeRadius, RemoteFearExitRadius);
        var inside = distance <= (active ? exit : enter);
        var entering = inside && !active;
        return new ThreatDecision(
            inside, entering, inside && (entering || now >= nextRefresh));
    }

    private static bool TryConsumeAck(HashSet<ulong> pending, ulong requestId) =>
        pending.Remove(requestId);

    private static bool TryConsumeAck(
        Dictionary<ulong, PendingAction> pending, ulong requestId) =>
        pending.Remove(requestId);

    private static bool TryExpirePendingAction(
        Dictionary<ulong, PendingAction> pending,
        Dictionary<int, ulong> pendingByFish,
        ulong requestId,
        float now,
        out PendingAction action)
    {
        if (!pending.TryGetValue(requestId, out action) || action.TimedOut || now < action.Expires)
            return false;
        action = action with { TimedOut = true };
        pending[requestId] = action;
        return true;
    }

    private static void ClearPendingFish(
        Dictionary<int, ulong> pendingByFish, PendingAction action, ulong requestId)
    {
        if (pendingByFish.TryGetValue(action.Id, out var current) && current == requestId)
            pendingByFish.Remove(action.Id);
    }

    private static double CorrectRenderClock(
        double renderTick, double latestTick, float deltaTime)
    {
        var desired = latestTick - InterpolationTicks;
        var drift = desired - renderTick;
        if (Math.Abs(drift) > 2.5d)
            return desired;
        var scale = drift > 0.25d ? 1.10d : drift < -0.25d ? 0.90d : 1d;
        return Math.Min(
            renderTick + Math.Max(0f, deltaTime) * SnapshotTicksPerSecond * scale,
            latestTick + MaxExtrapolationTicks);
    }

    private void RecordTombstone(int id, uint revision)
    {
        if (!_clientTombstones.TryGetValue(id, out var previous) ||
            RemovalWins(revision, previous))
            _clientTombstones[id] = revision;
    }

    private void WriteClientSummary(float now)
    {
        if (now < _nextTraceSummary)
            return;
        _nextTraceSummary = now + 2f;
        var valid = 0;
        foreach (var target in _targets.Values)
            if (target.Fish != null && target.Fish.gameObject.activeInHierarchy)
                valid++;
        _trace?.Write("FISH-SUMMARY",
            $"bound={_targets.Count} active={valid} pendingBind={_pendingClientManifests.Count} " +
            $"missing={_missingAllocatorIds.Count} " +
            $"suppressed={_suppressedClientFish.Count} " +
            $"samples={_clientSnapshotsAccepted}/{_clientSnapshotsRejected} " +
            $"teleports={_clientTeleports} tombstones={_clientTombstones.Count} " +
            $"clock={_clientRenderTick:F1}/{_clientLatestTick:F1} " +
            $"pendingRemoval={_deferredClientRemovals.Count}");
    }

    private int SuppressUnboundClientFish()
    {
        var represented = new HashSet<string>();
        foreach (var manifest in _activeClientManifest.Values)
            if (!string.IsNullOrEmpty(manifest.AllocatorUid))
                represented.Add(manifest.AllocatorUid);

        var suppressed = 0;
        foreach (var allocator in UnityEngine.Object.FindObjectsByType<FishAllocator>(
                     FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (allocator == null ||
                !ShouldSuppressAllocator(GetNetworkAllocatorUid(allocator), represented))
                continue;
            var fishs = allocator.GetInstancedFishs;
            if (fishs == null)
                continue;
            foreach (var fish in fishs)
            {
                if (fish == null || !fish.gameObject.activeInHierarchy ||
                    _clientIdsByFish.ContainsKey(fish) || _suppressedClientFish.Contains(fish))
                    continue;
                fish.gameObject.SetActive(false);
                _suppressedClientFish.Add(fish);
                suppressed++;
            }
        }
        return suppressed;
    }

    private void RestoreSuppressedClientFish()
    {
        foreach (var fish in _suppressedClientFish)
            if (fish != null)
                fish.gameObject.SetActive(true);
        _suppressedClientFish.Clear();
    }

    private void IndexAvailableClientFish(uint sceneId)
    {
        _clientAllocatorByUidScratch.Clear();
        _allocatorUidsScratch.Clear();
        _ambiguousAllocatorUidsScratch.Clear();
        foreach (var fish in _availableClientFishScratch.Values)
            fish.Clear();
        foreach (var allocator in UnityEngine.Object.FindObjectsByType<FishAllocator>(
                     FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (allocator == null)
                continue;
            var uid = GetNetworkAllocatorUid(allocator);
            _allocatorUidsScratch[allocator] = uid;
            AddUniqueAllocatorUid(
                _clientAllocatorByUidScratch, _ambiguousAllocatorUidsScratch, uid, allocator);
        }
        foreach (var uid in _ambiguousAllocatorUidsScratch)
            ReportAmbiguousAllocatorUid(uid);
        _idScratch.Clear();
        foreach (var pair in _targets)
            if (_ambiguousAllocatorUidsScratch.Contains(pair.Value.AllocatorUid))
                _idScratch.Add(pair.Key);
        foreach (var id in _idScratch)
            RemoveClientTarget(id, false);
        foreach (var pair in _allocatorUidsScratch)
        {
            var allocator = pair.Key;
            var uid = pair.Value;
            if (_ambiguousAllocatorUidsScratch.Contains(uid))
                continue;
            var fishs = allocator.GetInstancedFishs;
            if (fishs == null)
                continue;
            foreach (var fish in fishs)
            {
                if (fish == null || _clientIdsByFish.ContainsKey(fish))
                    continue;
                var key = ManifestFishKey(uid, fish.FishDataTID);
                if (!_availableClientFishScratch.TryGetValue(key, out var available))
                {
                    available = new List<FishAISystem>();
                    _availableClientFishScratch[key] = available;
                }
                available.Add(fish);
            }
        }
    }

    private FishAISystem TakeUnboundFish(
        FishManifest manifest,
        Dictionary<(string AllocatorUid, int FishDataTID), List<FishAISystem>> availableFish)
    {
        if (!_clientAllocatorByUidScratch.ContainsKey(manifest.AllocatorUid) ||
            !availableFish.TryGetValue(
                ManifestFishKey(manifest.AllocatorUid, manifest.FishDataTID), out var candidates))
            return null;
        FishAISystem best = null;
        var bestScore = float.NegativeInfinity;
        var expected = new Vector3(manifest.X, manifest.Y, manifest.Z);
        foreach (var fish in candidates)
        {
            if (fish == null || _clientIdsByFish.ContainsKey(fish))
                continue;
            var score = -Vector3.SqrMagnitude(fish.transform.position - expected);
            if (score > bestScore)
            {
                best = fish;
                bestScore = score;
            }
        }
        return best;
    }

    private int CountQueuedManifests()
    {
        var count = 0;
        foreach (var info in _hostInfoById.Values)
            if (info.ManifestQueued)
                count++;
        return count;
    }

    private static bool ShouldSendSnapshot(HostFish info, FishSnapshot snapshot, float now)
    {
        if (!info.HasSnapshot || now >= info.LastSnapshotSend + SnapshotKeyframeSeconds)
            return true;
        var previous = info.LastSnapshot;
        var dx = snapshot.X - previous.X;
        var dy = snapshot.Y - previous.Y;
        var dz = snapshot.Z - previous.Z;
        var dvx = snapshot.VelocityX - previous.VelocityX;
        var dvy = snapshot.VelocityY - previous.VelocityY;
        return snapshot.VelocityX * snapshot.VelocityX +
                snapshot.VelocityY * snapshot.VelocityY >= 0.0001f ||
            dx * dx + dy * dy + dz * dz >= 0.0025f ||
            Mathf.Abs(Mathf.DeltaAngle(previous.Rotation, snapshot.Rotation)) >= 1f ||
            dvx * dvx + dvy * dvy >= 0.04f ||
            Mathf.Abs(snapshot.Hp - previous.Hp) >= 0.01f || snapshot.Flags != previous.Flags;
    }

    private static InterestState UpdateInterest(
        bool interested, int outsideTicks, float distance, bool forced)
    {
        if (forced || !interested && distance <= InterestEnterDistance)
            return new InterestState(true, 0);
        if (!interested)
            return new InterestState(false, outsideTicks);
        if (distance <= InterestLeaveDistance)
            return new InterestState(true, 0);
        outsideTicks++;
        return new InterestState(outsideTicks < InterestLeaveTicks, outsideTicks);
    }

    private static InterestState UpdateInterestForRemote(
        bool interested, int outsideTicks, float distance, bool forced, bool hasRemotePlayer) =>
        hasRemotePlayer
            ? UpdateInterest(interested, outsideTicks, distance, forced)
            : new InterestState(interested, outsideTicks);

    private static HostFish CreateHostFish(
        FishAISystem fish, string allocatorUid, int fishDataTID, bool active) =>
        new()
        {
            Fish = fish,
            Renderer = fish?.GetComponentInChildren<SpriteRenderer>(true),
            AllocatorUid = allocatorUid,
            FishDataTID = fishDataTID,
            Active = active,
            InterestKnown = active,
            Interested = active
        };

    private static FishLifecycleKind? UpdatePoolActive(HostFish info, bool active)
    {
        if (info.Active == active)
            return null;
        info.Active = active;
        info.InterestKnown = active;
        info.Interested = active;
        info.OutsideInterestTicks = 0;
        info.HasSnapshot = false;
        return active ? FishLifecycleKind.InterestEnter : FishLifecycleKind.InterestLeave;
    }

    private static int FishControlSendBudget(int reliableCapacityRemaining) =>
        Math.Min(MaxFishControlPacketsPerFrame,
            Math.Max(0, reliableCapacityRemaining - ReliableCapacityReserve));

    private static bool SnapshotDeadlineReached(float lastSend, float now) =>
        now >= lastSend + SnapshotKeyframeSeconds;

    private static byte BuildManifestFlags(byte flags, bool interested) =>
        (byte)(flags | (interested ? 8 : 0));

    private static bool ManifestInterested(byte flags) => (flags & 8) != 0;

    private static bool ShouldHideClientTarget(bool interested, bool active) =>
        !interested && active;

    private static bool IsForcedRelevant(
        FishAISystem fish, HostFish info, float now, Transform hostPlayer, Transform remotePlayer)
    {
        if (info.Phase is FishPhase.Hooked or FishPhase.Qte or FishPhase.Captured or FishPhase.Corpse ||
            now < info.RecentDamageUntil)
            return true;
        var target = fish.DetectedEnemyData?.DetectedEnemy;
        return target != null && (target == hostPlayer || target == remotePlayer);
    }

    private static void InsertSnapshotCandidate(
        List<SnapshotCandidate> candidates, SnapshotCandidate candidate)
    {
        var index = candidates.Count;
        while (index > 0 && candidates[index - 1].Priority < candidate.Priority)
            index--;
        if (index >= SnapshotEntityBudget)
            return;
        candidates.Insert(index, candidate);
        if (candidates.Count > SnapshotEntityBudget)
            candidates.RemoveAt(SnapshotEntityBudget);
    }

    private static uint NextRevision(uint value) => value == uint.MaxValue ? 1 : value + 1;

    private static bool IsNewer(uint candidate, uint previous) =>
        unchecked((int)(candidate - previous)) > 0;

    private void RemoveClientTarget(int id, bool destroy)
    {
        ClearClientActions(id);
        if (!_targets.TryGetValue(id, out var target))
            return;
        _targets.Remove(id);
        _pendingClientManifests.Remove(id);
        if (target.Fish == null)
            return;
        _clientIdsByFish.Remove(target.Fish);
        if (destroy)
        {
            target.Fish.gameObject.SetActive(false);
            _deferredClientRemovals.Add((
                target.Fish, id, target.NativeActive, target.NativeBehaviorEnabled));
        }
        else
            RestoreNativeTarget(target, id);
        _trace?.Write("REMOVE", $"id={id} destroy={destroy}");
    }

    private void RestoreNativeTarget(Target target, int id)
    {
        SetClientSimulation(target.Fish, target.NativeBehaviorEnabled, id);
        target.Fish.gameObject.SetActive(target.NativeActive);
        _trace?.Write("BIND-RESTORE",
            $"id={id} active={target.NativeActive} behavior={target.NativeBehaviorEnabled}");
    }

    private void ClearClientActions(int id)
    {
        _queuedClientActions.Remove(id);
        _pendingClientActionByFish.Remove(id);
    }

    private void CancelClientHook(
        UdpSession session, uint sceneId, int id, FishAISystem fish, float now)
    {
        if (!_clientHookLeases.TryGetValue(id, out var lease))
            return;
        _queuedClientActions.Remove(id);
        if (lease.RequestId != 0 && !lease.ReleaseQueued)
        {
            var queued = EnqueueClientAction(
                session, sceneId, id, fish, FishAction.Release, 0, 0, 0, now);
            TryMarkLeaseActionQueued(lease, FishAction.Release, queued);
        }
        ScheduleClientHookCleanup(id);
    }

    private void ScheduleClientHookCleanup(int id) =>
        _scheduledClientHookCleanup.Add(id);

    private void CleanupClientHook(int id, FishAISystem fish)
    {
        if (!_clientHookLeases.TryGetValue(id, out var lease) ||
            !TryMarkClientHookCleanup(lease))
            return;
        if (_targets.TryGetValue(id, out var target) && !lease.CaptureQueued)
        {
            target.AwaitingLeaseSnapshot = true;
            target.AwaitingLeaseSnapshotTick = target.Tick;
            target.CorrectingLeasePresentation = false;
        }
        if (_lastClientHookId == id)
            _lastClientHookId = 0;
        ReleaseClientHook(fish, id);
    }

    private void ReleaseClientHook(FishAISystem fish, int id)
    {
        if (!fish.IsFishHooked && !fish.IsFishHookedSequence)
            return;
        try
        {
            fish.lastHarpoonProjectile?.TryReleaseHarpoon();
            fish.OnForceLoseFromProjectile();
            fish.OnEndHookedMode();
            _trace?.Write("HOOK-RELEASE", $"id={id}");
        }
        catch (Exception exception)
        {
            _trace?.Write("HOOK-RELEASE-ERROR",
                $"id={id} error={exception.GetType().Name}:{exception.Message}");
        }
    }

    private void SetClientSimulation(FishAISystem fish, bool enabled, int id)
    {
        try
        {
            FishBehaviorTreeState.Set(fish, enabled);
        }
        catch (Exception exception)
        {
            _trace?.Write("SIMULATION-ERROR",
                $"id={id} enabled={enabled} error={exception.GetType().Name}:{exception.Message}");
        }
    }

    private void RemoveHostFish(int id)
    {
        var topologyChanged = _hostFishById.ContainsKey(id) || _hostInfoById.ContainsKey(id);
        EndHostHookLease(id);
        if (_hostFishById.TryGetValue(id, out var fish))
        {
            _hostIdsByFish.Remove(fish);
            FishBehaviorTreeState.Forget(fish);
        }
        _hostFishById.Remove(id);
        _hostInfoById.Remove(id);
        _lastRemoteDamageById.Remove(id);
        _lastRemoteHookActionById.Remove(id);
        _remoteStimulatedIds.Remove(id);
        _nextRemoteThreatById.Remove(id);
        if (topologyChanged)
            MarkHostTopologyChanged();
    }

    private void MarkHostTopologyChanged()
    {
        _manifestRevision = NextRevision(_manifestRevision);
        foreach (var info in _hostInfoById.Values)
            info.ManifestQueued = false;
        _manifestStateQueued = false;
    }

    private int TakeHostId()
    {
        while (_nextHostId <= 0 || _hostFishById.ContainsKey(_nextHostId))
        {
            _nextHostId++;
            if (_nextHostId <= 0)
                _nextHostId = 1;
        }
        return _nextHostId++;
    }

    private static string GetNetworkAllocatorUid(FishAllocator allocator)
    {
        try
        {
            var uid = allocator.GetAllocatorUID();
            if (!string.IsNullOrEmpty(uid))
                return uid;
        }
        catch
        {
        }
        return $"A{WorldObjectId.For(allocator):X8}";
    }

    private void ReportAmbiguousAllocatorUid(string uid)
    {
        if (!_reportedAmbiguousAllocatorUids.Add(uid))
            return;
        _log.LogWarning($"Network fish replication disabled for duplicate allocator UID: {uid}");
        _trace?.Write("FISH-ID-COLLISION", $"allocator={uid}");
    }

    private static void AddUniqueAllocatorUid<T>(
        IDictionary<string, T> unique,
        ISet<string> duplicates,
        string uid,
        T value)
    {
        if (string.IsNullOrWhiteSpace(uid) || duplicates.Contains(uid))
            return;
        if (unique.ContainsKey(uid))
        {
            unique.Remove(uid);
            duplicates.Add(uid);
            return;
        }
        unique.Add(uid, value);
    }

    private static bool AllocatorUidMatches(string expected, string actual) =>
        !string.IsNullOrEmpty(expected) && expected == actual;

    private static byte BuildFlags(
        FishAISystem fish,
        FishPhase phase = FishPhase.None,
        SpriteRenderer renderer = null)
    {
        byte flags = 0;
        if (fish.IsCorpse)
            flags |= 1;
        if (fish.IsFishCaptured || phase == FishPhase.Captured)
            flags |= 2;
        if (fish.IsFishEnable)
            flags |= 4;
        if (FishFlipped(renderer ?? fish?.GetComponentInChildren<SpriteRenderer>(true)))
            flags |= 16;
        return flags;
    }

    private static bool FishFlipped(SpriteRenderer renderer) =>
        renderer != null && RemoteAvatar.CaptureVisibleTransform(renderer).FlipX;

    private static void ApplyFishFlip(SpriteRenderer renderer, bool flipped)
    {
        if (renderer != null && RemoteAvatar.CaptureVisibleTransform(renderer).FlipX != flipped)
            renderer.flipX = !renderer.flipX;
    }

    private static bool TryGetRemotePlayer(
        UdpSession session,
        uint sceneId,
        float now,
        out PlayerSnapshot remotePlayer) =>
        session.TryGetFreshRemotePlayerSnapshot(now, 0.75f, out remotePlayer) &&
            remotePlayer.SceneId == sceneId;

    private static bool InRange(Vector3 position, PlayerSnapshot player, float maxSquaredDistance)
    {
        var dx = position.x - player.X;
        var dy = position.y - player.Y;
        return dx * dx + dy * dy <= maxSquaredDistance;
    }

    internal static bool IsPlayerAttack(AttackType attackType) =>
        attackType is AttackType.Player_All or AttackType.Player_Gun or
            AttackType.Player_Harpoon or AttackType.Player_Melee or
            AttackType.Player_Harpoon_Interaction or AttackType.QTE_Damage or
            AttackType.Player_SubHelper;

    private static bool IsClientPickupFish(FishAISystem fish, FishInteractionBody body) =>
        fish != null && fish.FishDataTID > 0 &&
        (fish.IsCorpse || fish.HP <= 0.01f || fish.IsFishCaptured ||
         body?.InteractionType == FishInteractionBody.FishInteractionType.Pickup);

    private static int FishTidFromItem(int itemId)
    {
        var fishTid = itemId + 999_000;
        return fishTid is >= 2_000_000 and <= 2_999_999 ? fishTid : 0;
    }

    private static void ApplyFlags(Target target)
    {
        if (target.Fish == null)
            return;
        var corpse = (target.Flags & 1) != 0;
        var captured = (target.Flags & 2) != 0;
        var enabled = (target.Flags & 4) != 0;
        if (target.Fish.IsCorpse != corpse)
            target.Fish.IsCorpse = corpse;
        if (target.Fish.IsFishCaptured != captured)
            target.Fish.IsFishCaptured = captured;
        if (target.Fish.IsFishEnable != enabled)
            target.Fish.IsFishEnable = enabled;
    }
}

[HarmonyPatch(typeof(FishAISystem), nameof(FishAISystem.SetHPDamage))]
internal static class FishDamagePatch
{
    private static bool Prefix(
        FishAISystem __instance,
        int damage,
        EElement element,
        AttackType attackType)
    {
        var behaviour = ProbeBehaviour.Instance;
        return behaviour?.AllowFishDamage(__instance, damage, element, attackType) ?? true;
    }
}

[HarmonyPatch(typeof(FishAISystem), nameof(FishAISystem.OnTakeDamage))]
internal static class FishTakeDamagePatch
{
    private static bool Prefix(
        FishAISystem __instance,
        AttackData __0)
    {
        var behaviour = ProbeBehaviour.Instance;
        behaviour?.ObserveFishDamage(__instance, __0);
        return true;
    }
}

[HarmonyPatch(typeof(FishAISystem), nameof(FishAISystem.SetTrueHPDamage))]
internal static class FishTrueDamagePatch
{
    private static bool Prefix(FishAISystem __instance) =>
        ProbeBehaviour.Instance?.AllowFishTrueDamage(__instance) ?? true;
}

[HarmonyPatch(typeof(FishAISystem), nameof(FishAISystem.WinFromProjectileinFight))]
internal static class FishHarpoonWinPatch
{
    private static bool Prefix(FishAISystem __instance)
    {
        ProbeBehaviour.Instance?.TraceFishPickup("harpoon-win-prefix", __instance, null);
        return ProbeBehaviour.Instance?.AllowFishCaptureWon(__instance) ?? true;
    }

    private static void Postfix(FishAISystem __instance) =>
        ProbeBehaviour.Instance?.TraceFishPickup("harpoon-win-postfix", __instance, null);
}

[HarmonyPatch(typeof(FishAISystem), nameof(FishAISystem.HookedByProjectile),
    new[] { typeof(ProjectileInfo) })]
internal static class FishHookedByProjectilePatch
{
    private static void Postfix(FishAISystem __instance) =>
        ProbeBehaviour.Instance?.OnFishHooked(__instance);
}

[HarmonyPatch(typeof(FishAISystem), nameof(FishAISystem.SetDeadForce))]
internal static class FishSetDeadForceTracePatch
{
    private static void Prefix(FishAISystem __instance) =>
        ProbeBehaviour.Instance?.TraceFishPickup("set-dead-prefix", __instance, null);

    private static void Postfix(FishAISystem __instance) =>
        ProbeBehaviour.Instance?.TraceFishPickup("set-dead-postfix", __instance, null);
}

[HarmonyPatch(typeof(FishAISystem), nameof(FishAISystem.OnEndHookedMode))]
internal static class FishEndHookedTracePatch
{
    private static void Prefix(FishAISystem __instance)
    {
        ProbeBehaviour.Instance?.TraceFishPickup("hook-end-prefix", __instance, null);
        ProbeBehaviour.Instance?.OnFishHookEnded(__instance);
    }

    private static void Postfix(FishAISystem __instance) =>
        ProbeBehaviour.Instance?.TraceFishPickup("hook-end-postfix", __instance, null);
}

[HarmonyPatch(typeof(FishInteractionBody), nameof(FishInteractionBody.SuccessInteract))]
internal static class FishPickupPatch
{
    private static bool Prefix(
        FishInteractionBody __instance,
        BaseCharacter __0,
        out FishAISystem __state)
    {
        __state = __instance?.GetComponentInParent<FishAISystem>();
        ProbeBehaviour.Instance?.TraceFishPickup("body-success-prefix", __state, __instance);
        return ProbeBehaviour.Instance?.AllowFishPickup(__instance, __0) ?? true;
    }

    private static void Postfix(FishInteractionBody __instance, FishAISystem __state)
    {
        ProbeBehaviour.Instance?.TraceFishPickup("body-success-postfix", __state, __instance);
        ProbeBehaviour.Instance?.OnFishPickupSucceeded(__state);
    }
}

[HarmonyPatch(typeof(FishInteractionBody), nameof(FishInteractionBody.CheckAvailableInteraction))]
internal static class FishInteractionAvailabilityPatch
{
    private static void Postfix(FishInteractionBody __instance, ref bool __result)
    {
        __result = ProbeBehaviour.Instance?.AllowFishInteraction(__instance, __result) ?? __result;
    }
}

[HarmonyPatch(typeof(FishAISystem), nameof(FishAISystem.LootDeadFishBody))]
internal static class FishLootDeadBodyTracePatch
{
    private static void Prefix(FishAISystem __instance) =>
        ProbeBehaviour.Instance?.TraceFishPickup("loot-dead-prefix", __instance, null);

    private static void Postfix(FishAISystem __instance) =>
        ProbeBehaviour.Instance?.TraceFishPickup("loot-dead-postfix", __instance, null);
}

[HarmonyPatch(typeof(FishAISystem), nameof(FishAISystem.OnSuccessPickUp))]
internal static class FishSuccessPickUpTracePatch
{
    private static void Prefix(FishAISystem __instance) =>
        ProbeBehaviour.Instance?.TraceFishPickup("success-pickup-event", __instance, null);
}

[HarmonyPatch(typeof(FishAISystem), nameof(FishAISystem.SuccessPickupFish))]
internal static class FishSuccessPickupFishTracePatch
{
    private static void Prefix(FishAISystem __instance, int grade, bool ignoreOverloaded) =>
        ProbeBehaviour.Instance?.TraceFishPickup(
            $"success-pickup-fish grade={grade} ignore={ignoreOverloaded}", __instance, null);
}

[HarmonyPatch(typeof(FishAISystem), nameof(FishAISystem.SuccessNetPickupFish))]
internal static class FishSuccessNetPickupFishTracePatch
{
    private static void Prefix(FishAISystem __instance, bool ignoreOverloaded) =>
        ProbeBehaviour.Instance?.TraceFishPickup(
            $"success-net-pickup ignore={ignoreOverloaded}", __instance, null);
}

[HarmonyPatch(typeof(FishAISystem), nameof(FishAISystem.DestroySelf))]
internal static class FishPresentationRemovalPatch
{
    private static bool Prefix(FishAISystem __instance) =>
        ProbeBehaviour.Instance?.AllowFishRemoval(__instance) ?? true;
}

[HarmonyPatch(typeof(FishAISystem), nameof(FishAISystem.AddDropItemLootBoxWithPlus))]
internal static class FishAddDropLootTracePatch
{
    private static void Prefix(
        FishAISystem __instance,
        int bonusGrade,
        LootBox.AutoLiftedType type,
        int tier,
        out bool __state)
    {
        __state = ProbeBehaviour.Instance?.BeginClientFishLootSource(__instance) == true;
        ProbeBehaviour.Instance?.TraceFishPickup(
            $"add-drop bonus={bonusGrade} lift={type} tier={tier}", __instance, null);
    }

    private static Exception Finalizer(Exception __exception, bool __state)
    {
        ProbeBehaviour.Instance?.EndClientFishLootSource(__state);
        return __exception;
    }
}

[HarmonyPatch(typeof(PlayerCharacter), nameof(PlayerCharacter.OnInteraction_Performed))]
internal static class PlayerInteractionPerformedTracePatch
{
    private static void Prefix(PlayerCharacter __instance) =>
        ProbeBehaviour.Instance?.TracePlayerInteraction("performed-prefix", __instance);

    private static void Postfix(PlayerCharacter __instance) =>
        ProbeBehaviour.Instance?.TracePlayerInteraction("performed-postfix", __instance);
}

[HarmonyPatch(typeof(PlayerCharacter), nameof(PlayerCharacter.OnInteractSuccess))]
internal static class PlayerInteractSuccessTracePatch
{
    private static void Prefix(PlayerCharacter __instance) =>
        ProbeBehaviour.Instance?.TracePlayerInteraction("success-prefix", __instance);

    private static void Postfix(PlayerCharacter __instance) =>
        ProbeBehaviour.Instance?.TracePlayerInteraction("success-postfix", __instance);
}

[HarmonyPatch(typeof(PlayerCharacter), nameof(PlayerCharacter.SuccessInteraction))]
internal static class PlayerSuccessInteractionTracePatch
{
    private static void Prefix(PlayerCharacter __instance) =>
        ProbeBehaviour.Instance?.TracePlayerInteraction("success-interaction-prefix", __instance);

    private static void Postfix(PlayerCharacter __instance) =>
        ProbeBehaviour.Instance?.TracePlayerInteraction("success-interaction-postfix", __instance);
}

[HarmonyPatch(typeof(FishAISystem), "Update")]
internal static class ClientFishUpdatePatch
{
    private static bool Prefix(FishAISystem __instance) =>
        ProbeBehaviour.Instance?.AllowFishSimulation(__instance) ?? true;
}

[HarmonyPatch(typeof(FishAISystem), "LateUpdate")]
internal static class ClientFishLateUpdatePatch
{
    private static bool Prefix(FishAISystem __instance) =>
        ProbeBehaviour.Instance?.AllowFishSimulation(__instance) ?? true;

    private static void Postfix(FishAISystem __instance) =>
        ProbeBehaviour.Instance?.ApplyFishAuthoritativeState(__instance);
}
