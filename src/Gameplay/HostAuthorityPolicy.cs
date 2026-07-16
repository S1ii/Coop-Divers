using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace DaveTheDiverMP;

internal static class HostAuthorityPolicy
{
    internal static bool CanMutatePersistentProgress =>
        ProbeBehaviour.Role != SessionRole.Client ||
        ProbeBehaviour.Instance?._session == null ||
        !ProbeBehaviour.Instance._session.Connected;
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
internal static class MiniGameSaveAuthorityPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var pair in new[]
                 {
                     (typeof(SaveData.SaveDataMiniGame), new[]
                     {
                         "AddCount", "SetCount", "SetGuideDone", "ResetAllCounts"
                     }),
                     (typeof(SaveData.ArcadeSave), new[]
                     {
                         "AddChecked", "UpdateConcertscore", "UpdateFlappyBirdBest",
                         "OwnFlappyBirdCharacter", "CheckFlappyBirdCharacter",
                         "UpdateKaraokeScore", "UpdateBeatemUpScore"
                     }),
                     (typeof(SaveData.SaveDataBalatro), new[] { "UpdateScore" })
                 })
        {
            foreach (var name in pair.Item2)
            foreach (var method in AccessTools.GetDeclaredMethods(pair.Item1))
                if (method.Name == name)
                    yield return method;
        }
    }

    private static bool Prefix() => HostAuthorityPolicy.CanMutatePersistentProgress;
}

[HarmonyPatch]
internal static class StorySaveAuthorityPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var name in new[]
                 {
                     nameof(SaveData.UpdateChapterSave),
                     nameof(SaveData.UpdateEventDataSave),
                     nameof(SaveData.UpdatePlayedCutsceneSave),
                     nameof(SaveData.UpdatePlayedIntermissionSave)
                 })
            yield return AccessTools.DeclaredMethod(typeof(SaveData), name);
    }

    private static bool Prefix() => HostAuthorityPolicy.CanMutatePersistentProgress;
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
