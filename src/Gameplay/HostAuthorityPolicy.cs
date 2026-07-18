using System;
using System.Collections.Generic;
using System.Reflection;
using DR.Save;
using HarmonyLib;

namespace DaveTheDiverMP;

internal static class HostAuthorityPolicy
{
    private static readonly HashSet<string> PersistentMissionMutationRoots = new()
    {
        nameof(MissionManager.AcceptMission),
        nameof(MissionManager.ResetMission),
        nameof(MissionManager.ClearMission),
        nameof(MissionManager.ClearMissionFromStart),
        nameof(MissionManager.ClearCurMissionTask),
        nameof(MissionManager.ClearMissionTask),
        nameof(MissionManager.SetMissionFailedV2),
        nameof(MissionManager.SetMissionFailed),
        nameof(MissionManager.SetMissoinInProgress),
        nameof(MissionManager.SetMissoinStateFail),
        nameof(MissionManager.RevertToStartVIPMission),
        nameof(MissionManager.ApplyMissionClear),
        nameof(MissionManager.ProcessFailByType),
        nameof(MissionManager.ProcessMissionFailWeatherChanged),
        nameof(MissionManager.ProcessMissionFailJungleTimeSectionChanged),
        nameof(MissionManager.FailMission),
        nameof(MissionManager.FailMissionByStartType)
    };

    internal static void SelfTest()
    {
        var matrix = new[]
        {
            (SessionRole.Offline, false, false, false, true),
            (SessionRole.Host, true, false, false, true),
            (SessionRole.Client, false, false, false, true),
            (SessionRole.Client, true, false, false, false),
            (SessionRole.Client, true, true, false, true),
            (SessionRole.Client, true, true, true, false)
        };
        foreach (var (role, connected, remoteApply, presentation, expected) in matrix)
            if (AllowsPersistentMutation(role, connected, remoteApply, presentation) != expected)
                throw new InvalidOperationException(
                    $"Host authority policy failed: role={role} connected={connected} " +
                    $"remoteApply={remoteApply} presentation={presentation}");
        if (!IsPersistentMissionMutationRoot(nameof(MissionManager.ClearMission), typeof(void)) ||
            !IsPersistentMissionMutationRoot(nameof(MissionManager.SetMissionFailed), typeof(void)) ||
            !IsPersistentMissionMutationRoot(nameof(MissionManager.ApplyMissionClear), typeof(void)) ||
            !IsPersistentMissionMutationRoot(nameof(MissionManager.FailMission), typeof(void)) ||
            IsPersistentMissionMutationRoot(nameof(MissionManager.GetReward), typeof(Il2CppSystem.Collections.IEnumerator)) ||
            !IsMissionRewardRoot(nameof(MissionManager.GetReward), typeof(Il2CppSystem.Collections.IEnumerator)))
            throw new InvalidOperationException("Mission authority target policy failed");
        if (!AllowsHostOwnedAction(SessionRole.Host, true) ||
            AllowsHostOwnedAction(SessionRole.Client, true) ||
            !AllowsHostOwnedAction(SessionRole.Client, false))
            throw new InvalidOperationException("Host-owned action policy failed");
        if (!AllowsSaveWrite(SessionRole.Offline, false, false) ||
            !AllowsSaveWrite(SessionRole.Host, true, false) ||
            !AllowsSaveWrite(SessionRole.Client, false, false) ||
            AllowsSaveWrite(SessionRole.Client, true, false) ||
            !AllowsSaveWrite(SessionRole.Client, true, true) ||
            AllowsSaveWrite(SessionRole.Offline, false, false, true) ||
            !AllowsSaveWrite(SessionRole.Offline, false, true, true) ||
            AllowsSaveWrite(SessionRole.Offline, false, false, false, true) ||
            !AllowsSaveWrite(SessionRole.Offline, false, true, false, true))
            throw new InvalidOperationException("Host-owned save policy failed");
        if (SaveOwnerCode(SessionRole.Client, true) != "host" ||
            SaveOwnerCode(SessionRole.Host, true) != "host" ||
            SaveOwnerCode(SessionRole.Offline, false) != "local" ||
            DescribeCurrentOwnership(SessionRole.Client, true) !=
                "campaign=host;save=host;mission=host;reward=host;economy=host" ||
            DescribeCurrentOwnership(SessionRole.Offline, false) !=
                "campaign=local;save=local;mission=local;reward=local;economy=local" ||
            SaveWriteRejectReason(SessionRole.Client, true, false, false, false) != "client_not_owner" ||
            SaveWriteRejectReason(SessionRole.Client, true, false, true, false) != "original_restore_required" ||
            SaveWriteRejectReason(SessionRole.Client, true, false, false, true) != "restart_required")
            throw new InvalidOperationException("Host-owned save diagnostic codes failed");
    }

    internal static bool CanMutatePersistentProgress
    {
        get
        {
            var probe = ProbeBehaviour.Instance;
            var remoteApply = probe?.IsApplyingRemoteMission == true;
            var allowed = AllowsPersistentMutation(
                ProbeBehaviour.Role,
                probe?._session?.Connected == true,
                remoteApply,
                probe?.IsCompletingClientPresentation == true);
            return allowed && (remoteApply || probe?.TryRestoreClientOriginals() != false);
        }
    }

    internal static bool CanOwnHostAction =>
        AllowsHostOwnedAction(
            ProbeBehaviour.Role,
            ProbeBehaviour.Instance?._session?.Connected == true);

    internal static bool CanWriteHostOwnedSave =>
        AllowsSaveWrite(
            ProbeBehaviour.Role,
            ProbeBehaviour.Instance?._session?.Connected == true,
            MultiplayerSaveSync.IsApplyingRemoteSnapshot,
            MultiplayerSaveSync.OriginalProfileRestoreRequired,
            MultiplayerSaveSync.NormalSaveRestartRequired);

    internal static string DescribeCurrentOwnership(SessionRole role, bool connected)
    {
        var owner = SaveOwnerCode(role, connected);
        return $"campaign={owner};save={owner};mission={owner};reward={owner};economy={owner}";
    }

    internal static bool AllowHostOwnedSave(string method)
    {
        var probe = ProbeBehaviour.Instance;
        var role = ProbeBehaviour.Role;
        var connected = probe?._session?.Connected == true;
        var remoteSnapshot = MultiplayerSaveSync.IsApplyingRemoteSnapshot;
        var restoreRequired = MultiplayerSaveSync.OriginalProfileRestoreRequired;
        var restartRequired = MultiplayerSaveSync.NormalSaveRestartRequired;
        if (AllowsSaveWrite(role, connected, remoteSnapshot, restoreRequired, restartRequired))
            return true;
        probe?.TraceAuthorityRejected(
            "save", method, SaveOwnerCode(role, connected),
            SaveWriteRejectReason(role, connected, remoteSnapshot, restoreRequired, restartRequired));
        return false;
    }

    private static bool AllowsPersistentMutation(
        SessionRole role,
        bool connected,
        bool remoteApply,
        bool presentation) =>
        !presentation && (role != SessionRole.Client || !connected || remoteApply);

    private static bool AllowsHostOwnedAction(SessionRole role, bool connected) =>
        role != SessionRole.Client || !connected;

    private static bool AllowsSaveWrite(
        SessionRole role,
        bool connected,
        bool remoteSnapshot,
        bool restoreRequired = false,
        bool restartRequired = false) =>
        remoteSnapshot || !restoreRequired && !restartRequired &&
            (role != SessionRole.Client || !connected);

    private static string SaveOwnerCode(SessionRole role, bool connected) =>
        role == SessionRole.Offline ? "local" : connected ? "host" : "local";

    private static string SaveWriteRejectReason(
        SessionRole role,
        bool connected,
        bool remoteSnapshot,
        bool restoreRequired,
        bool restartRequired)
    {
        if (remoteSnapshot)
            return "allowed";
        if (restartRequired)
            return "restart_required";
        if (restoreRequired)
            return "original_restore_required";
        return role == SessionRole.Client && connected ? "client_not_owner" : "policy_denied";
    }

    internal static bool IsPersistentMissionMutationRoot(string name, Type returnType) =>
        returnType == typeof(void) && PersistentMissionMutationRoots.Contains(name);

    internal static bool IsMissionRewardRoot(string name, Type returnType) =>
        name == nameof(MissionManager.GetReward) &&
        returnType == typeof(Il2CppSystem.Collections.IEnumerator);
}

[HarmonyPatch]
internal static class MissionLifecycleAuthorityPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var method in AccessTools.GetDeclaredMethods(typeof(MissionManager)))
            if (HostAuthorityPolicy.IsPersistentMissionMutationRoot(method.Name, method.ReturnType))
                yield return method;
    }

    private static bool Prefix() => HostAuthorityPolicy.CanMutatePersistentProgress;
}

[HarmonyPatch]
internal static class MissionPresentationCountPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var method in AccessTools.GetDeclaredMethods(typeof(MissionManager)))
            if (method.Name == nameof(MissionManager.UpdateMissionIntCondition))
                yield return method;
    }

    private static bool Prefix() =>
        ProbeBehaviour.Instance?.IsCompletingClientPresentation != true;
}

[HarmonyPatch]
internal static class MissionRewardAuthorityPatch
{
    private static MethodBase TargetMethod() => AccessTools.DeclaredMethod(
        typeof(MissionManager), nameof(MissionManager.GetReward), new[] { typeof(int) });

    private static bool Prefix() => HostAuthorityPolicy.CanOwnHostAction;
}

[HarmonyPatch(typeof(FishFarm.FishFarmManager), nameof(FishFarm.FishFarmManager.Save))]
internal static class FishFarmSaveAuthorityPatch
{
    private static bool Prefix() => HostAuthorityPolicy.CanMutatePersistentProgress;
}

[HarmonyPatch(typeof(Farm.FarmCore), nameof(Farm.FarmCore.Save))]
internal static class FarmSaveAuthorityPatch
{
    private static bool Prefix() => HostAuthorityPolicy.CanMutatePersistentProgress;
}

[HarmonyPatch]
internal static class MermanFarmAuthorityPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var name in new[]
                 {
                     nameof(VillageFarmSystem.RequestSowSeed),
                     nameof(VillageFarmSystem.DoHarvest),
                     nameof(VillageFarmSystem.RequestBuyLane)
                 })
        {
            var method = AccessTools.DeclaredMethod(typeof(VillageFarmSystem), name);
            if (method != null)
                yield return method;
        }
    }

    private static bool Prefix() => HostAuthorityPolicy.CanMutatePersistentProgress;
}

[HarmonyPatch(typeof(RewardManager), nameof(RewardManager.Reward))]
internal static class RewardAuthorityPatch
{
    private static bool Prefix() => HostAuthorityPolicy.CanMutatePersistentProgress;

    private static void Postfix(Reward __0) =>
        ProbeBehaviour.Instance?.ObserveReward(__0);
}

[HarmonyPatch(typeof(CommonDefine), nameof(CommonDefine.AddPlayerGoods))]
internal static class SharedWalletAuthorityPatch
{
    private static bool Prefix(GoodsType __0, int __1) =>
        ProbeBehaviour.Instance?.InterceptPlayerGoods(__0, __1) ?? true;
}

[HarmonyPatch(typeof(ScenarioManager), nameof(ScenarioManager.SetDoneSequenceEvent))]
internal static class ScenarioDoneAuthorityPatch
{
    private static bool Prefix() => HostAuthorityPolicy.CanMutatePersistentProgress;
}

[HarmonyPatch(typeof(UpdateMissionInScenarioData), nameof(UpdateMissionInScenarioData.OnChoice))]
internal static class ScenarioMissionChoiceAuthorityPatch
{
    private static bool Prefix() => HostAuthorityPolicy.CanMutatePersistentProgress;
}

[HarmonyPatch]
internal static class MissionPhoneCallAuthorityPatch
{
    private static MethodBase TargetMethod() =>
        ManagerEventReplicator.GetPhoneTargets()[2];

    private static bool Prefix() => HostAuthorityPolicy.CanOwnHostAction;
}

[HarmonyPatch(typeof(SushiBarAnalyticsManager), nameof(SushiBarAnalyticsManager.Save))]
internal static class SushiAnalyticsSaveAuthorityPatch
{
    private static bool Prefix()
    {
        ProbeBehaviour.Instance?.PublishSushiResult();
        return HostAuthorityPolicy.CanMutatePersistentProgress;
    }
}

[HarmonyPatch(typeof(SushiBarAnalyticsTodayData), nameof(SushiBarAnalyticsTodayData.Record))]
internal static class SushiAnalyticsRecordAuthorityPatch
{
    private static bool Prefix() => HostAuthorityPolicy.CanMutatePersistentProgress;
}

[HarmonyPatch(typeof(SushiBarMenuManager), nameof(SushiBarMenuManager.SaveMenuAll))]
internal static class SushiMenuSaveAuthorityPatch
{
    private static bool Prefix() => HostAuthorityPolicy.CanMutatePersistentProgress;
}

[HarmonyPatch(typeof(SpecialCustomerDataManager), nameof(SpecialCustomerDataManager.Save))]
internal static class SpecialCustomerSaveAuthorityPatch
{
    private static bool Prefix() => HostAuthorityPolicy.CanMutatePersistentProgress;
}

[HarmonyPatch(typeof(JDLC.JungleSushiBarAnalytics), nameof(JDLC.JungleSushiBarAnalytics.ApplyDailyResult))]
internal static class JungleSushiResultAuthorityPatch
{
    private static bool Prefix() => HostAuthorityPolicy.CanMutatePersistentProgress;
}

[HarmonyPatch(typeof(MiniGame.SeahorseRace.SeahorseRaceSaveValue),
    nameof(MiniGame.SeahorseRace.SeahorseRaceSaveValue.Save))]
internal static class SeahorseSaveAuthorityPatch
{
    private static bool Prefix() => HostAuthorityPolicy.CanMutatePersistentProgress;
}

[HarmonyPatch]
internal static class GameSaveAuthorityPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var name in new[]
                 {
                     nameof(SaveSystem.SaveGameData),
                     nameof(SaveSystem.TrySaveGameData),
                     nameof(SaveSystem.SaveGameDataInSlot)
                 })
            foreach (var method in AccessTools.GetDeclaredMethods(typeof(SaveSystem)))
                if (method.Name == name && method.ReturnType == typeof(bool))
                    yield return method;
    }

    private static bool Prefix(ref bool __result, MethodBase __originalMethod)
    {
        if (HostAuthorityPolicy.AllowHostOwnedSave(__originalMethod?.Name ?? "SaveGameData"))
            return true;
        __result = true;
        return false;
    }
}

[HarmonyPatch]
internal static class CentralSaveAuthorityPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        var seen = new HashSet<MethodBase>();
        var saveAll = AccessTools.DeclaredMethod(
            typeof(SaveSystem), nameof(SaveSystem.SaveAllData), Type.EmptyTypes);
        if (saveAll != null && seen.Add(saveAll))
            yield return saveAll;

        var gameSave = AccessTools.DeclaredMethod(
            typeof(SaveSystemGameDataManager), nameof(SaveSystemGameDataManager.SaveData),
            new[] { typeof(bool) });
        if (gameSave != null && seen.Add(gameSave))
            yield return gameSave;

        var playerSave = AccessTools.DeclaredMethod(
            typeof(SaveSystemPlayerDataManager), nameof(SaveSystemPlayerDataManager.SaveData),
            new[] { typeof(bool) });
        if (playerSave != null && seen.Add(playerSave))
            yield return playerSave;

        var playerUpdate = AccessTools.DeclaredMethod(
            typeof(SaveSystemPlayerDataManager), nameof(SaveSystemPlayerDataManager.UpdateData),
            Type.EmptyTypes);
        if (playerUpdate != null && seen.Add(playerUpdate))
            yield return playerUpdate;
    }

    private static bool Prefix(MethodBase __originalMethod) =>
        HostAuthorityPolicy.AllowHostOwnedSave(__originalMethod?.Name ?? "SaveData");
}

[HarmonyPatch(typeof(SteamAchievements), nameof(SteamAchievements.UnlockProgressSyncFromSave))]
internal static class RemoteSaveAchievementAuthorityPatch
{
    private static bool Prefix() => !MultiplayerSaveSync.IsApplyingRemoteSnapshot;
}

[HarmonyPatch]
internal static class InsectBattleRewardAuthorityPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var name in new[]
                 {
                     nameof(InsectBattle.InsectBattleMinigameController.AddRewardBattleInsect),
                     nameof(InsectBattle.InsectBattleMinigameController.FaintBattleInsect)
                 })
        {
            var method = AccessTools.DeclaredMethod(
                typeof(InsectBattle.InsectBattleMinigameController), name);
            if (method != null)
                yield return method;
        }
    }

    private static bool Prefix() => HostAuthorityPolicy.CanMutatePersistentProgress;
}
