using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;

namespace DaveTheDiverMP;

internal static class HarmonyCompatibilityReporter
{
    private const string OwnerId = "dev.davethedivermp";
    private const int MaxReports = 64;

    internal static void SelfTest()
    {
        var owners = FilterForeignOwners(
            new[] { OwnerId, "z.mod", "a.mod", "z.mod", string.Empty }, OwnerId);
        if (owners.Length != 2 || owners[0] != "a.mod" || owners[1] != "z.mod")
            throw new InvalidOperationException("Harmony compatibility owner filtering is not deterministic");
    }

    internal static void Report(ManualLogSource log)
    {
        if (log == null)
            return;

        try
        {
            var reports = new List<string>();
            foreach (var method in Harmony.GetAllPatchedMethods())
            {
                var patchInfo = Harmony.GetPatchInfo(method);
                var owners = ReadOwners(patchInfo);
                var foreign = FilterForeignOwners(owners, OwnerId);
                if (foreign.Length == 0)
                    continue;

                var typeName = method.DeclaringType?.FullName ?? "<global>";
                reports.Add($"{typeName}.{method.Name}: {string.Join(", ", foreign)}");
            }

            reports.Sort(StringComparer.Ordinal);
            var count = Math.Min(reports.Count, MaxReports);
            for (var index = 0; index < count; index++)
                log.LogWarning($"Harmony compatibility target overlap: {reports[index]}");
            if (reports.Count > count)
                log.LogWarning($"Harmony compatibility target overlap report capped at {MaxReports} entries (total={reports.Count})");
        }
        catch (Exception exception)
        {
            log.LogWarning($"Harmony compatibility inventory unavailable: {exception.Message}");
        }
    }

    internal static string[] FilterForeignOwners(IEnumerable<string> owners, string ownOwner)
    {
        if (owners == null)
            return Array.Empty<string>();

        return owners
            .Where(owner => !string.IsNullOrWhiteSpace(owner) &&
                !string.Equals(owner, ownOwner, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(owner => owner, StringComparer.Ordinal)
            .ToArray();
    }

    private static IEnumerable<string> ReadOwners(object patchInfo)
    {
        if (patchInfo == null)
            return Array.Empty<string>();

        // HarmonyX exposes Owners on PatchInfo, but reflection keeps this
        // diagnostic compatible with the bundled Harmony minor version.
        var property = patchInfo.GetType().GetProperty("Owners",
            BindingFlags.Instance | BindingFlags.Public);
        return property?.GetValue(patchInfo) as IEnumerable<string> ??
            Array.Empty<string>();
    }
}
