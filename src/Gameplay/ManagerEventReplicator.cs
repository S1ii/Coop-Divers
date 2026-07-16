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
    Story = 11
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
    IntermissionUnmarked = 20
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

    internal ManagerEventReplicator(ManualLogSource log) => _log = log;

    internal void Update(SessionRole role, UdpSession session, float now)
    {
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
        }
        _wasConnected = true;

        if (role == SessionRole.Host && !_storySnapshotPublished)
            _storySnapshotPublished = PublishStorySnapshot(session);
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
                _pendingHostEvents[state.Revision] = state;
        }
        if (role == SessionRole.Client)
        {
            ApplyPendingHostEvents();
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
            session.SendManagerEvent(new ManagerEvent(0, (byte)domain, (byte)action, value, context));
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
        _pendingHostEvents.Clear();
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
        session.SendManagerEvent(new ManagerEvent(
            _hostRevision, (byte)domain, (byte)action, value, context));
    }

    private void ApplyClientRequest(UdpSession session, ManagerEvent state)
    {
        if ((ManagerDomain)state.Domain != ManagerDomain.Dialogue || state.Revision != 0 ||
            !DialogueContextMatches(state.Context))
            return;
        if (Apply(state))
            Publish(session, (ManagerDomain)state.Domain, (ManagerAction)state.Action,
                state.Value, state.Context);
    }

    private void ApplyPendingHostEvents()
    {
        while (_pendingHostEvents.TryGetValue(
                   NextRevision(_clientManagerRevision), out var state))
        {
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
        var chapter = SaveSystem.GetGameSave()?.ChapterData;
        if (chapter == null)
            return false;
        Publish(session, ManagerDomain.Story, ManagerAction.CurrentChapter,
            chapter.CurrentChapter, 0);
        Publish(session, ManagerDomain.Story, ManagerAction.ReservedChapter,
            chapter.ReservedChapter, 0);
        return true;
    }

    private static bool ApplyStory(ManagerAction action, int value)
    {
        var save = SaveSystem.GetGameSave();
        if (save == null)
            return false;
        switch (action)
        {
            case ManagerAction.CurrentChapter when save.ChapterData != null:
                save.ChapterData.CurrentChapter = value;
                return true;
            case ManagerAction.ReservedChapter when save.ChapterData != null:
                save.ChapterData.ReservedChapter = value;
                return true;
            case ManagerAction.EventCleared when save.EventData != null:
                save.EventData.AddCleared((Common.Contents.Event.Name)value);
                return true;
            case ManagerAction.EventRemoved when save.EventData != null:
                save.EventData.RemoveCleared((Common.Contents.Event.Name)value);
                return true;
            case ManagerAction.CutsceneMarked:
                save.MarkCutscene(value);
                return true;
            case ManagerAction.CutsceneUnmarked:
                save.UnMarkCutscene(value);
                return true;
            case ManagerAction.IntermissionMarked:
                save.MarkIntermission(value);
                return true;
            case ManagerAction.IntermissionUnmarked:
                save.UnMarkIntermission(value);
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

[HarmonyPatch]
internal static class StoryChapterSyncPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.PropertySetter(
            typeof(SaveData.SaveDataChapter), nameof(SaveData.SaveDataChapter.CurrentChapter));
        yield return AccessTools.PropertySetter(
            typeof(SaveData.SaveDataChapter), nameof(SaveData.SaveDataChapter.ReservedChapter));
    }

    private static bool Prefix(MethodBase __originalMethod, int __0) =>
        ManagerEventPatchHelper.Intercept(
            ManagerDomain.Story,
            __originalMethod.Name == "set_CurrentChapter"
                ? ManagerAction.CurrentChapter
                : ManagerAction.ReservedChapter,
            __0);
}

[HarmonyPatch]
internal static class StoryEventSyncPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.DeclaredMethod(
            typeof(SaveData.SaveDataEvent), nameof(SaveData.SaveDataEvent.AddCleared));
        yield return AccessTools.DeclaredMethod(
            typeof(SaveData.SaveDataEvent), nameof(SaveData.SaveDataEvent.RemoveCleared));
    }

    private static bool Prefix(MethodBase __originalMethod, Common.Contents.Event.Name __0) =>
        ManagerEventPatchHelper.Intercept(
            ManagerDomain.Story,
            __originalMethod.Name == nameof(SaveData.SaveDataEvent.AddCleared)
                ? ManagerAction.EventCleared
                : ManagerAction.EventRemoved,
            (int)__0);
}

[HarmonyPatch]
internal static class StoryMarkerSyncPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var name in new[]
                 {
                     nameof(SaveData.MarkCutscene), nameof(SaveData.UnMarkCutscene),
                     nameof(SaveData.MarkIntermission), nameof(SaveData.UnMarkIntermission)
                 })
            yield return AccessTools.DeclaredMethod(typeof(SaveData), name);
    }

    private static bool Prefix(MethodBase __originalMethod, int __0)
    {
        var action = __originalMethod.Name switch
        {
            nameof(SaveData.MarkCutscene) => ManagerAction.CutsceneMarked,
            nameof(SaveData.UnMarkCutscene) => ManagerAction.CutsceneUnmarked,
            nameof(SaveData.MarkIntermission) => ManagerAction.IntermissionMarked,
            _ => ManagerAction.IntermissionUnmarked
        };
        return ManagerEventPatchHelper.Intercept(ManagerDomain.Story, action, __0);
    }
}
