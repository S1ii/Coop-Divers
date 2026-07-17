using System;
using HarmonyLib;

namespace DaveTheDiverMP;

// Unsupported client-only effects are blocked at their native entry points.
// This is deliberately a deny policy; add a packet only when an effect has a host reducer.
internal static class DiverEquipmentPolicy
{
    internal static void SelfTest()
    {
        if (!CanApplyRemoteHit(AttackType.Fish, 0, 0f, 0) ||
            CanApplyRemoteHit(AttackType.Player_Melee, 0, 0f, 0) ||
            CanApplyRemoteHit(AttackType.Fish, 1, 0f, 0) ||
            CanApplyRemoteHit(AttackType.Fish, 0, 0.1f, 0) ||
            CanApplyRemoteHit(AttackType.Fish, 0, 0f, 1) ||
            !AllowClientMutation(SessionRole.Host, true) ||
            AllowClientMutation(SessionRole.Client, true))
            throw new InvalidOperationException("Diver equipment policy self-test failed");
    }

    internal static bool CanApplyRemoteHit(
        AttackType attackType, int knockBackForce, float freezeTime, int buffCount) =>
        !FishReplicator.IsPlayerAttack(attackType) && knockBackForce == 0 &&
        freezeTime == 0f && buffCount == 0;

    internal static bool AllowClientMutation(SessionRole role, bool connected) =>
        role != SessionRole.Client || !connected;
}

[HarmonyPatch(typeof(GunWeaponHandler), nameof(GunWeaponHandler.FireWeapon))]
internal static class DiverGunPolicyPatch
{
    private static bool Prefix(GunWeaponHandler __instance) =>
        ProbeBehaviour.Instance?.AllowDiverGunFire(__instance) ?? true;
}

[HarmonyPatch(typeof(PlayerCharacter), nameof(PlayerCharacter.OnFireSubHelper_Performed))]
internal static class DiverSubHelperPolicyPatch
{
    private static bool Prefix(PlayerCharacter __instance) =>
        ProbeBehaviour.Instance?.AllowDiverDeviceUse(__instance) ?? true;
}

[HarmonyPatch(typeof(BuffHandler), nameof(BuffHandler.AddBuff),
    new[] { typeof(int), typeof(ICaster), typeof(bool) })]
internal static class DiverBuffIntPolicyPatch
{
    private static bool Prefix(BuffHandler __instance) =>
        ProbeBehaviour.Instance?.AllowDiverBuff(__instance) ?? true;
}

[HarmonyPatch(typeof(BuffHandler), nameof(BuffHandler.AddBuff),
    new[] { typeof(BuffDebuffEffectData), typeof(ICaster), typeof(bool),
        typeof(Il2CppSystem.Nullable<UnityEngine.Vector3>), typeof(bool) })]
internal static class DiverBuffDataPolicyPatch
{
    private static bool Prefix(BuffHandler __instance) =>
        ProbeBehaviour.Instance?.AllowDiverBuff(__instance) ?? true;
}

[HarmonyPatch(typeof(BuffHandler), nameof(BuffHandler.AddBuff),
    new[] { typeof(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int>), typeof(bool) })]
internal static class DiverBuffArrayPolicyPatch
{
    private static bool Prefix(BuffHandler __instance) =>
        ProbeBehaviour.Instance?.AllowDiverBuff(__instance) ?? true;
}

[HarmonyPatch(typeof(BuffHandler), nameof(BuffHandler.SafeAddBuff),
    new[] { typeof(int), typeof(ICaster), typeof(bool) })]
internal static class DiverBuffSafePolicyPatch
{
    private static bool Prefix(BuffHandler __instance) =>
        ProbeBehaviour.Instance?.AllowDiverBuff(__instance) ?? true;
}

[HarmonyPatch(typeof(BuffHandler), nameof(BuffHandler.AddBuff),
    new[] { typeof(string), typeof(ICaster), typeof(bool) })]
internal static class DiverBuffStringPolicyPatch
{
    private static bool Prefix(BuffHandler __instance) =>
        ProbeBehaviour.Instance?.AllowDiverBuff(__instance) ?? true;
}
