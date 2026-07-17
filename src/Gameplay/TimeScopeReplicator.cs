using HarmonyLib;

namespace DaveTheDiverMP;

[HarmonyPatch(typeof(TimeManager), nameof(TimeManager.SetTimeScale),
    new[] { typeof(TimeScaleController.Type), typeof(float) })]
internal static class TimeScaleSyncPatch
{
    private static bool Prefix(TimeScaleController.Type __0, float __1) =>
        ProbeBehaviour.Instance?.InterceptTimeScale(__0, __1) ?? true;
}

[HarmonyPatch(typeof(TimeManager), nameof(TimeManager.SetTimeScale),
    new[] { typeof(TimeScaleController.Type), typeof(float), typeof(Il2CppSystem.Object) })]
internal static class TimeScaleLockedSyncPatch
{
    private static bool Prefix(TimeScaleController.Type __0, float __1) =>
        ProbeBehaviour.Instance?.InterceptTimeScale(__0, __1) ?? true;
}

[HarmonyPatch(typeof(TimeManager), nameof(TimeManager.SetTimeScale),
    new[] { typeof(TimeScaleController.Type), typeof(float), typeof(float), typeof(bool) })]
internal static class TimeScaleDurationSyncPatch
{
    private static bool Prefix(TimeScaleController.Type __0, float __1) =>
        ProbeBehaviour.Instance?.InterceptTimeScale(__0, __1) ?? true;
}

[HarmonyPatch(typeof(TimeManager), nameof(TimeManager.TimeStop),
    new[] { typeof(string), typeof(bool) })]
internal static class TimeStopSyncPatch
{
    private static bool Prefix(bool __1) =>
        ProbeBehaviour.Instance?.InterceptTimeStop(__1) ?? true;
}

[HarmonyPatch(typeof(TimeManager), nameof(TimeManager.TimeStop_EF),
    new[] { typeof(string), typeof(bool) })]
internal static class TimeStopEfSyncPatch
{
    private static bool Prefix(bool __1) =>
        ProbeBehaviour.Instance?.InterceptTimeStop(__1) ?? true;
}

[HarmonyPatch(typeof(TimeManager), nameof(TimeManager.ResetTimeScale))]
internal static class TimeResetSyncPatch
{
    private static bool Prefix() => ProbeBehaviour.Instance?.InterceptTimeReset() ?? true;
}

[HarmonyPatch(typeof(TimeManager), nameof(TimeManager.TimeScaleOn),
    new[] { typeof(string), typeof(bool) })]
internal static class TimeResumeSyncPatch
{
    private static bool Prefix() => ProbeBehaviour.Instance?.InterceptTimeReset() ?? true;
}

[HarmonyPatch(typeof(TimeManager), nameof(TimeManager.TimeScaleOn_EF),
    new[] { typeof(string), typeof(bool) })]
internal static class TimeResumeEfSyncPatch
{
    private static bool Prefix() => ProbeBehaviour.Instance?.InterceptTimeReset() ?? true;
}

[HarmonyPatch(typeof(TimeManager), nameof(TimeManager.TimeScaleOnEndingMiniGame),
    new[] { typeof(string) })]
internal static class TimeResumeMiniGameSyncPatch
{
    private static bool Prefix() => ProbeBehaviour.Instance?.InterceptTimeReset() ?? true;
}
