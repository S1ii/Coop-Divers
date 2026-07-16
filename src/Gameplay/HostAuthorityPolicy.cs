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

[HarmonyPatch]
internal static class BettingGameAuthorityPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var method in AccessTools.GetDeclaredMethods(typeof(MiniGame.BettingGame)))
            if (method.Name == nameof(MiniGame.BettingGame.Show))
                yield return method;
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
