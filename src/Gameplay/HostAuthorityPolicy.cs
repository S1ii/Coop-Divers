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
        nameof(MissionManager.RevertToStartVIPMission)
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
            IsPersistentMissionMutationRoot(nameof(MissionManager.ApplyMissionClear), typeof(void)) ||
            IsPersistentMissionMutationRoot(nameof(MissionManager.FailMission), typeof(void)) ||
            IsPersistentMissionMutationRoot(nameof(MissionManager.GetReward), typeof(Il2CppSystem.Collections.IEnumerator)))
            throw new InvalidOperationException("Mission authority target policy failed");
        if (!AllowsHostOwnedAction(SessionRole.Host, true) ||
            AllowsHostOwnedAction(SessionRole.Client, true) ||
            !AllowsHostOwnedAction(SessionRole.Client, false))
            throw new InvalidOperationException("Host-owned action policy failed");
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

    private static bool AllowsPersistentMutation(
        SessionRole role,
        bool connected,
        bool remoteApply,
        bool presentation) =>
        !presentation && (role != SessionRole.Client || !connected || remoteApply);

    private static bool AllowsHostOwnedAction(SessionRole role, bool connected) =>
        role != SessionRole.Client || !connected;

    internal static bool IsPersistentMissionMutationRoot(string name, Type returnType) =>
        returnType == typeof(void) && PersistentMissionMutationRoots.Contains(name);
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

    private static bool Prefix(ref bool __result)
    {
        if (HostAuthorityPolicy.CanMutatePersistentProgress)
            return true;
        __result = true;
        return false;
    }
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
