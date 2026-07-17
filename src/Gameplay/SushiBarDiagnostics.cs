using HarmonyLib;

namespace DaveTheDiverMP;

internal static class SushiBarDiagnostics
{
    internal static void Trace(string message) =>
        ProbeBehaviour.Logger?.LogInfo($"Sushi trace: role={ProbeBehaviour.Role}; {message}");
}

[HarmonyPatch(typeof(SushiBarTrashTrigger), nameof(SushiBarTrashTrigger.CleanFinished))]
internal static class SushiTrashCleanDiagnosticPatch
{
    private static bool Prefix(SushiBarTrashTrigger __instance, int gold)
    {
        SushiBarDiagnostics.Trace(
            $"clean-finished prefix table={__instance.TableNumber} gold={gold} " +
            $"dirty={__instance.Target?.IsTrash}");
        return ProbeBehaviour.Instance?.InterceptSushiClean(__instance, gold) ?? true;
    }

    private static void Postfix(SushiBarTrashTrigger __instance, int gold) =>
        SushiBarDiagnostics.Trace(
            $"clean-finished postfix table={__instance.TableNumber} gold={gold} " +
            $"dirty={__instance.Target?.IsTrash}");
}

[HarmonyPatch(typeof(SushiBarTable), nameof(SushiBarTable.Clean))]
internal static class SushiTableCleanDiagnosticPatch
{
    private static void Prefix(SushiBarTable __instance) =>
        SushiBarDiagnostics.Trace(
            $"table-clean prefix table={__instance.tableNumber} dirty={__instance.IsTrash}");

    private static void Postfix(SushiBarTable __instance) =>
        SushiBarDiagnostics.Trace(
            $"table-clean postfix table={__instance.tableNumber} dirty={__instance.IsTrash}");
}

[HarmonyPatch(typeof(SushiBarContext.OperationData), nameof(SushiBarContext.OperationData.UpdateWasabiCount),
    new[] { typeof(SushiBar.Place), typeof(int) })]
internal static class SushiWasabiDiagnosticPatch
{
    private static bool Prefix(
        SushiBarContext.OperationData __instance,
        SushiBar.Place place,
        int count,
        ref bool __result)
    {
        var allowed = ProbeBehaviour.Instance?.InterceptSushiWasabi(place, count) ?? true;
        if (!allowed)
            __result = true;
        return allowed;
    }
}

[HarmonyPatch(typeof(StaffDave), nameof(StaffDave.OnInteraction_Start))]
internal static class SushiDaveInteractionDiagnosticPatch
{
    private static bool Prefix(
        StaffDave __instance, SushiBarInteraction interaction, ref bool __result)
    {
        SushiBarDiagnostics.Trace(
            $"dave-interaction prefix action={interaction} staff={__instance.GetInstanceID()}");
        var allowed = ProbeBehaviour.Instance?.InterceptSushiInteraction(__instance, interaction) ?? true;
        if (!allowed)
            __result = true;
        return allowed;
    }

    private static void Postfix(
        StaffDave __instance, SushiBarInteraction interaction, bool __result) =>
        SushiBarDiagnostics.Trace(
            $"dave-interaction postfix action={interaction} staff={__instance.GetInstanceID()} " +
            $"result={__result}");
}

[HarmonyPatch(typeof(SushiBar.Customer.SushiBarCustomer),
    nameof(SushiBar.Customer.SushiBarCustomer.Served))]
internal static class SushiCustomerServedDiagnosticPatch
{
    private static void Prefix(
        SushiBar.Customer.SushiBarCustomer __instance, SushiBarStaff servingStaff) =>
        SushiBarDiagnostics.Trace(
            $"customer-served prefix customer={__instance.GetInstanceID()} " +
            $"seat={__instance.SeatNumber} recipe={__instance.LastOrderedRecipeID} " +
            $"state={__instance.CurrentState} staff={servingStaff?.GetInstanceID()}");

    private static void Postfix(
        SushiBar.Customer.SushiBarCustomer __instance,
        SushiBarStaff servingStaff,
        bool __result) =>
        SushiBarDiagnostics.Trace(
            $"customer-served postfix customer={__instance.GetInstanceID()} " +
            $"seat={__instance.SeatNumber} recipe={__instance.LastOrderedRecipeID} " +
            $"state={__instance.CurrentState} staff={servingStaff?.GetInstanceID()} " +
            $"result={__result}");
}

[HarmonyPatch(typeof(SushiBar.Customer.SushiBarCustomer),
    nameof(SushiBar.Customer.SushiBarCustomer.ServedDrink))]
internal static class SushiCustomerDrinkDiagnosticPatch
{
    private static void Prefix(
        SushiBar.Customer.SushiBarCustomer __instance, SushiBarStaff servingStaff) =>
        SushiBarDiagnostics.Trace(
            $"customer-drink prefix customer={__instance.GetInstanceID()} " +
            $"seat={__instance.SeatNumber} state={__instance.CurrentState} " +
            $"staff={servingStaff?.GetInstanceID()}");

    private static void Postfix(
        SushiBar.Customer.SushiBarCustomer __instance,
        SushiBarStaff servingStaff,
        bool __result) =>
        SushiBarDiagnostics.Trace(
            $"customer-drink postfix customer={__instance.GetInstanceID()} " +
            $"seat={__instance.SeatNumber} state={__instance.CurrentState} " +
            $"staff={servingStaff?.GetInstanceID()} result={__result}");
}
