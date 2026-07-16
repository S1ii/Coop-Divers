using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
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
    SeahorseRace = 15
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
    TimelineProgress = 31
}

internal sealed class ManagerEventReplicator
{
    private readonly record struct MenuSlotState(
        int RecipeId, int NowCount, int MaxCount, int Flags);

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
    private readonly SortedDictionary<uint, ManagerEvent> _pendingHostEvents = new();
    private readonly Queue<ManagerEvent> _outboundEvents = new();
    private readonly Dictionary<int, MenuSlotState> _hostMenuSlots = new();
    private readonly int[] _hostWasabi = new int[(int)SushiBar.Place.Max];
    private uint _hostRevision;
    private uint _clientManagerRevision;
    private uint _sushiRevision;
    private uint _clientSushiRevision;
    private SushiResultState? _pendingSushiResult;
    private bool _applying;
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

    internal ManagerEventReplicator(ManualLogSource log) => _log = log;

    internal void Update(SessionRole role, UdpSession session, uint sceneId, float now)
    {
        _sceneId = sceneId;
        var connected = session != null && session.Connected;
        if (!connected)
        {
            if (_wasConnected)
                Clear();
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
        }
        _wasConnected = true;
        FlushOutbound(session);

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
            else if (role == SessionRole.Client && IsNewer(state.Revision, _clientManagerRevision))
            {
                if (!_hasHostClockOffset)
                {
                    _hostClockOffset = unchecked(CurrentTick() - state.HostTick);
                    _hasHostClockOffset = true;
                }
                _pendingHostEvents[state.Revision] = state;
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
        if (role == SessionRole.Host)
        {
            Publish(session, domain, action, value, context);
            return true;
        }
        if (role == SessionRole.Client && domain == ManagerDomain.Dialogue)
        {
            _outboundEvents.Enqueue(new ManagerEvent(
                0, _sceneId, CurrentTick(), (byte)domain, (byte)action, value, context));
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

    internal void ObserveTimeline(
        SessionRole role,
        UdpSession session,
        TimelineManager.TPlayState state,
        int tid)
    {
        if (role == SessionRole.Client)
            _clientTimelineTid = state == TimelineManager.TPlayState.Start ? tid : 0;
        if (_applying || role != SessionRole.Host || session == null || !session.Connected)
            return;
        _hostTimelineTid = state == TimelineManager.TPlayState.Start ? tid : 0;
        _nextTimelineSync = 0f;
        Publish(session, ManagerDomain.Timeline,
            state == TimelineManager.TPlayState.Start
                ? ManagerAction.TimelineStart
                : ManagerAction.TimelineFinish,
            tid, 0);
    }

    internal bool AllowTimelineControl(SessionRole role, UdpSession session) =>
        _applying || role != SessionRole.Client || session == null || !session.Connected;

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

    internal void Clear()
    {
        RestoreClientState();
        _pendingHostEvents.Clear();
        _outboundEvents.Clear();
        _hostMenuSlots.Clear();
        Array.Fill(_hostWasabi, -1);
        _hostRevision = 0;
        _clientManagerRevision = 0;
        _sushiRevision = 0;
        _clientSushiRevision = 0;
        _pendingSushiResult = null;
        _applying = false;
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
        _clientTimelineTid = 0;
        _hostTimelineTid = 0;
        _timelineSyncTid = 0;
        _clientTimelineHostTick = 0;
        _clientTimelineTime = 0;
        _clientTimelinePlaying = false;
        _nextTimelineSync = 0f;
        _hasHostClockOffset = false;
        _hostClockOffset = 0;
        _wasConnected = false;
        _storySnapshotPublished = false;
    }

    private void Publish(
        UdpSession session,
        ManagerDomain domain,
        ManagerAction action,
        int value,
        int context)
    {
        _hostRevision = NextRevision(_hostRevision);
        _outboundEvents.Enqueue(new ManagerEvent(
            _hostRevision,
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
            state.SceneId != _sceneId || !DialogueContextMatches(state.Context))
            return;
        if (Apply(state))
            Publish(session, (ManagerDomain)state.Domain, (ManagerAction)state.Action,
                state.Value, state.Context);
    }

    private void ApplyPendingHostEvents(UdpSession session, uint sceneId)
    {
        while (_pendingHostEvents.TryGetValue(
                   NextRevision(_clientManagerRevision), out var state))
        {
            if (state.SceneId != 0 && state.SceneId != sceneId)
            {
                if (session.SceneMatches(state.SceneId))
                    return;
                _pendingHostEvents.Remove(state.Revision);
                _clientManagerRevision = state.Revision;
                continue;
            }
            if (!Apply(state))
                return;
            _pendingHostEvents.Remove(state.Revision);
            _clientManagerRevision = state.Revision;
        }
    }

    private bool Apply(ManagerEvent state)
    {
        _applying = true;
        try
        {
            switch ((ManagerDomain)state.Domain)
            {
                case ManagerDomain.MainSushi:
                    return ApplyMainSushi((ManagerAction)state.Action);
                case ManagerDomain.JungleSushi:
                    return ApplyJungleSushi((ManagerAction)state.Action, state.Value != 0);
                case ManagerDomain.Dialogue:
                    return ApplyDialogue((ManagerAction)state.Action);
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
            _applying = false;
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

    private static bool ApplyDialogue(ManagerAction action)
    {
        var manager = DialogueManager.Instance;
        if (manager == null)
            return false;
        switch (action)
        {
            case ManagerAction.Continue:
                manager.ContinueDialogueManual();
                return true;
            case ManagerAction.Skip:
                manager.OnSkip();
                return true;
            case ManagerAction.FirstChoice:
                manager.ExcuteFirstDialogue();
                return true;
            case ManagerAction.SecondChoice:
                manager.ExcuteSecondDialogue();
                return true;
            default:
                return false;
        }
    }

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
            _timelineSyncTid = timelineTid;
            _clientTimelineHostTick = LocalizeHostTick(hostTick);
            _clientTimelineTime = 0;
            _clientTimelinePlaying = true;
            if (_clientTimelineTid != timelineTid)
                manager.Play(timelineTid, null, null);
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
            if (_clientTimelineTid != timelineTid)
                manager.Play(timelineTid, null, null);
            return true;
        }
        if (action != ManagerAction.TimelineFinish)
            return false;
        if (_clientTimelineTid == timelineTid)
            manager.Finish(timelineTid);
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

    private static bool DialogueContextMatches(int context)
    {
        var manager = DialogueManager.Instance;
        return context == 0 || manager != null &&
            unchecked((int)Protocol.SceneId(manager.CurrentBundleID ?? string.Empty)) == context;
    }

    private static uint NextRevision(uint value) => value == uint.MaxValue ? 1 : value + 1;

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

    private void RestoreClientState()
    {
        if (_originalCurrentChapter == null && _originalReservedChapter == null &&
            _originalDayTicks == null && _originalDayTime == null && _originalWeather == null &&
            _originalEvents.Count == 0 && _originalCutscenes.Count == 0 &&
            _originalIntermissions.Count == 0)
            return;
        _applying = true;
        try
        {
            var save = SaveSystem.GetGameSave();
            if (save?.ChapterData != null)
            {
                if (_originalCurrentChapter.HasValue)
                    save.ChapterData.CurrentChapter = _originalCurrentChapter.Value;
                if (_originalReservedChapter.HasValue)
                    save.ChapterData.ReservedChapter = _originalReservedChapter.Value;
            }
            if (save?.EventData != null)
                foreach (var pair in _originalEvents)
                    if (pair.Value)
                        save.EventData.AddCleared((Common.Contents.Event.Name)pair.Key);
                    else
                        save.EventData.RemoveCleared((Common.Contents.Event.Name)pair.Key);
            if (save != null)
            {
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
            }
            var day = DayManager.Instance;
            if (day != null)
            {
                if (_originalDayTicks.HasValue)
                    day.m_TodayDate = new Il2CppSystem.DateTime(_originalDayTicks.Value);
                if (_originalDayTime.HasValue)
                    day.m_CurrentTimeState = (DayTimeState)_originalDayTime.Value;
            }
            if (_originalWeather.HasValue && WeatherManager.Instance != null)
                WeatherManager.Instance.ChangeCurrentWeather((WeatherType)_originalWeather.Value);
        }
        catch (Exception exception)
        {
            _log.LogWarning($"Client state restore failed: {exception.Message}");
        }
        finally
        {
            _applying = false;
            _originalCurrentChapter = null;
            _originalReservedChapter = null;
            _originalDayTicks = null;
            _originalDayTime = null;
            _originalWeather = null;
            _originalEvents.Clear();
            _originalCutscenes.Clear();
            _originalIntermissions.Clear();
        }
    }

    private static bool IsGlobal(ManagerDomain domain) =>
        domain is ManagerDomain.Story or ManagerDomain.Day;

    private static bool IsNewer(uint revision, uint previous) =>
        unchecked((int)(revision - previous)) > 0;
}

internal static class ManagerEventPatchHelper
{
    internal static bool Intercept(ManagerDomain domain, ManagerAction action, int value = 0)
    {
        var behaviour = ProbeBehaviour.Instance;
        var context = domain == ManagerDomain.Dialogue
            ? unchecked((int)Protocol.SceneId(DialogueManager.Instance?.CurrentBundleID ?? string.Empty))
            : 0;
        return behaviour?.InterceptManagerEvent(domain, action, value, context) ?? true;
    }

    internal static bool Begin(
        ManagerDomain domain,
        ManagerAction action,
        out bool suppressNested)
    {
        var behaviour = ProbeBehaviour.Instance;
        var context = unchecked((int)Protocol.SceneId(
            DialogueManager.Instance?.CurrentBundleID ?? string.Empty));
        if (behaviour != null)
            return behaviour.BeginManagerEvent(
                domain, action, 0, context, out suppressNested);
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

[HarmonyPatch(typeof(DialogueManager), nameof(DialogueManager.ExcuteFirstDialogue))]
internal static class DialogueFirstChoiceSyncPatch
{
    private static bool Prefix(out bool __state) =>
        ManagerEventPatchHelper.Begin(
            ManagerDomain.Dialogue, ManagerAction.FirstChoice, out __state);

    private static void Postfix(bool __state) => ManagerEventPatchHelper.End(__state);
}

[HarmonyPatch(typeof(DialogueManager), nameof(DialogueManager.ExcuteSecondDialogue))]
internal static class DialogueSecondChoiceSyncPatch
{
    private static bool Prefix(out bool __state) =>
        ManagerEventPatchHelper.Begin(
            ManagerDomain.Dialogue, ManagerAction.SecondChoice, out __state);

    private static void Postfix(bool __state) => ManagerEventPatchHelper.End(__state);
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
internal static class TimelineAuthorityPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.DeclaredMethod(typeof(TimelineManager), nameof(TimelineManager.Play));
        yield return AccessTools.DeclaredMethod(typeof(TimelineManager), nameof(TimelineManager.Finish));
    }

    private static bool Prefix() => ProbeBehaviour.Instance?.AllowTimelineControl() ?? true;
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
