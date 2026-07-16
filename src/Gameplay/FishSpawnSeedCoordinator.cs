using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace DaveTheDiverMP;

internal static class FishSpawnSeedCoordinator
{
    private static readonly Dictionary<uint, int> SceneSeeds = new();

    internal readonly struct Scope
    {
        internal readonly bool Active;
        internal readonly UnityEngine.Random.State Previous;

        internal Scope(bool active, UnityEngine.Random.State previous)
        {
            Active = active;
            Previous = previous;
        }
    }

    internal static void SelfTest()
    {
        var first = CombineSeed(123, "allocator-a");
        var expected = unchecked((17 * 31 + 123) * 31 +
            (int)Protocol.SceneId("allocator-a"));
        var fallback = BuildFallbackAllocatorUid(
            0x1234ABCD, "Game.FishAllocator", "/0:Root/2:Spawner");
        if (first == 0 || first != expected || first == CombineSeed(123, "allocator-b") ||
            fallback != "1234ABCD|Game.FishAllocator|/0:Root/2:Spawner" ||
            fallback == BuildFallbackAllocatorUid(
                0x1234ABCE, "Game.FishAllocator", "/0:Root/2:Spawner") ||
            fallback == BuildFallbackAllocatorUid(
                0x1234ABCD, "Game.OtherAllocator", "/0:Root/2:Spawner") ||
            fallback == BuildFallbackAllocatorUid(
                0x1234ABCD, "Game.FishAllocator", "/0:Root/3:Spawner"))
            throw new InvalidOperationException("Fish spawn seed mixing failed");
    }

    internal static void Update(SessionRole role, UdpSession session)
    {
        if (role != SessionRole.Client || session == null)
            return;
        while (session.TryTakeSceneSeed(out var seed))
            SetSeed(seed);
    }

    internal static SceneSeed GetOrCreate(uint sceneId)
    {
        if (sceneId == 0)
            return default;
        if (!SceneSeeds.TryGetValue(sceneId, out var seed))
        {
            seed = unchecked((int)(sceneId ^ (uint)Environment.TickCount ^ 0x9E3779B9u));
            if (seed == 0)
                seed = 1;
            SceneSeeds[sceneId] = seed;
        }
        return new SceneSeed(sceneId, seed);
    }

    internal static void SetSeed(SceneSeed seed)
    {
        if (seed.SceneId == 0 || seed.Seed == 0)
            return;
        if (!SceneSeeds.TryGetValue(seed.SceneId, out var current) || current != seed.Seed)
            SceneSeeds[seed.SceneId] = seed.Seed;
    }

    internal static bool HasSeed(SceneSeed seed) =>
        SceneSeeds.TryGetValue(seed.SceneId, out var value) && value == seed.Seed;

    internal static Scope Begin(FishAllocator allocator)
    {
        if (allocator == null)
            return default;
        var sceneId = Protocol.SceneId(allocator.gameObject.scene.name);
        if (!SceneSeeds.TryGetValue(sceneId, out var sceneSeed))
            return default;

        var uid = GetAllocatorUid(sceneId, allocator);
        var previous = UnityEngine.Random.state;
        UnityEngine.Random.InitState(CombineSeed(sceneSeed, uid));
        return new Scope(true, previous);
    }

    internal static void End(Scope scope)
    {
        if (scope.Active)
            UnityEngine.Random.state = scope.Previous;
    }

    private static string GetAllocatorUid(uint sceneId, FishAllocator allocator)
    {
        try
        {
            var uid = allocator.GetAllocatorUID();
            if (!string.IsNullOrEmpty(uid))
                return uid;
        }
        catch
        {
        }
        var path = new StringBuilder();
        for (var current = allocator.transform; current != null; current = current.parent)
            path.Insert(0, $"/{current.GetSiblingIndex()}:{current.name}");
        return BuildFallbackAllocatorUid(
            sceneId, allocator.GetType().FullName ?? nameof(FishAllocator), path.ToString());
    }

    private static string BuildFallbackAllocatorUid(uint sceneId, string componentType, string path) =>
        $"{sceneId:X8}|{componentType}|{path}";

    private static int CombineSeed(int sceneSeed, string allocatorUid)
    {
        unchecked
        {
            var hash = 17;
            hash = hash * 31 + sceneSeed;
            hash = hash * 31 + unchecked((int)Protocol.SceneId(allocatorUid ?? string.Empty));
            return hash == 0 ? 1 : hash;
        }
    }
}

[HarmonyPatch]
internal static class FishAllocatorSpawnSeedPatch
{
    private static MethodBase TargetMethod() =>
        AccessTools.Method(typeof(FishAllocator), nameof(FishAllocator.Spawn), Type.EmptyTypes);

    private static void Prefix(FishAllocator __instance, out FishSpawnSeedCoordinator.Scope __state) =>
        __state = FishSpawnSeedCoordinator.Begin(__instance);

    private static Exception Finalizer(
        Exception __exception,
        FishSpawnSeedCoordinator.Scope __state)
    {
        FishSpawnSeedCoordinator.End(__state);
        return __exception;
    }
}

[HarmonyPatch]
internal static class FishAllocatorSpawnForceSeedPatch
{
    private static MethodBase TargetMethod() =>
        AccessTools.Method(typeof(FishAllocator), nameof(FishAllocator.Spawn), new[] { typeof(bool) });

    private static void Prefix(FishAllocator __instance, out FishSpawnSeedCoordinator.Scope __state) =>
        __state = FishSpawnSeedCoordinator.Begin(__instance);

    private static Exception Finalizer(
        Exception __exception,
        FishSpawnSeedCoordinator.Scope __state)
    {
        FishSpawnSeedCoordinator.End(__state);
        return __exception;
    }
}

[HarmonyPatch(typeof(FishAllocator), nameof(FishAllocator.GetRandomFishGroup))]
internal static class FishAllocatorRandomGroupSeedPatch
{
    private static void Prefix(FishAllocator __instance, out FishSpawnSeedCoordinator.Scope __state) =>
        __state = FishSpawnSeedCoordinator.Begin(__instance);

    private static Exception Finalizer(
        Exception __exception,
        FishSpawnSeedCoordinator.Scope __state)
    {
        FishSpawnSeedCoordinator.End(__state);
        return __exception;
    }
}

[HarmonyPatch(typeof(FishAllocator), nameof(FishAllocator.DoInstanceFishOrGroup))]
internal static class FishAllocatorInstanceSeedPatch
{
    private static void Prefix(FishAllocator __instance, out FishSpawnSeedCoordinator.Scope __state) =>
        __state = FishSpawnSeedCoordinator.Begin(__instance);

    private static Exception Finalizer(
        Exception __exception,
        FishSpawnSeedCoordinator.Scope __state)
    {
        FishSpawnSeedCoordinator.End(__state);
        return __exception;
    }
}
