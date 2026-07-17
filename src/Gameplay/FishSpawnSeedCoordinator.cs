using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace DaveTheDiverMP;

internal static class FishSpawnSeedCoordinator
{
    private static readonly Dictionary<(uint SceneId, uint SceneEpoch), int> SceneSeeds = new();
    private static readonly Dictionary<uint, uint> StagedSceneEpochs = new();
    private static readonly Dictionary<uint, uint> ActiveSceneEpochs = new();
    private static readonly HashSet<(uint SceneId, uint SceneEpoch, string Scope)> LoggedScopes = new();

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
        Clear();
        var first = CombineSeed(123, "allocator-a");
        var expected = unchecked((17 * 31 + 123) * 31 +
            (int)Protocol.SceneId("allocator-a"));
        var fallback = BuildFallbackAllocatorUid(
            0x1234ABCD, "Game.FishAllocator", "/0:Root/2:Spawner");
        var scoped = BuildScopedUid("pickup", "A02/Pickup/4");
        if (first == 0 || first != expected || first == CombineSeed(123, "allocator-b") ||
            fallback != "1234ABCD|Game.FishAllocator|/0:Root/2:Spawner" ||
            scoped != "pickup|A02/Pickup/4" ||
            fallback == BuildFallbackAllocatorUid(
                0x1234ABCE, "Game.FishAllocator", "/0:Root/2:Spawner") ||
            fallback == BuildFallbackAllocatorUid(
                0x1234ABCD, "Game.OtherAllocator", "/0:Root/2:Spawner") ||
            fallback == BuildFallbackAllocatorUid(
                0x1234ABCD, "Game.FishAllocator", "/0:Root/3:Spawner"))
            throw new InvalidOperationException("Fish spawn seed mixing failed");

        var previous = new SceneSeed(7, 3, 11);
        var current = new SceneSeed(7, 4, 22);
        StageRemoteScene(previous);
        ActivateStagedScene(previous.SceneId);
        if (!HasActiveSeed(previous))
            throw new InvalidOperationException("Fish spawn seed activation failed");
        StageRemoteScene(current);
        if (!HasActiveSeed(previous))
            throw new InvalidOperationException("Future fish seed activated too early");
        ActivateStagedScene(current.SceneId);
        if (!HasActiveSeed(current) || HasActiveSeed(previous))
            throw new InvalidOperationException("Fish seed epoch replacement failed");
        ActivateStagedScene(current.SceneId);
        if (HasActiveSeed(current))
            throw new InvalidOperationException("Stale fish seed remained active");
        StageLocalScene(previous);
        if (!TryGetPreparedEpoch(previous.SceneId, out var preparedEpoch) ||
            preparedEpoch != previous.SceneEpoch)
            throw new InvalidOperationException("Preload fish seed staging failed");
        Clear();
    }

    internal static void Update(SessionRole role, UdpSession session)
    {
        if (role != SessionRole.Client || session == null)
            return;
        while (session.TryTakeSceneSeed(out var seed))
            StageRemoteScene(seed);
    }

    internal static SceneSeed GetOrCreate(uint sceneId, uint sceneEpoch)
    {
        if (sceneId == 0 || sceneEpoch == 0)
            return default;
        var key = (sceneId, sceneEpoch);
        if (!SceneSeeds.TryGetValue(key, out var seed))
        {
            seed = unchecked((int)(sceneId ^ sceneEpoch ^ (uint)Environment.TickCount ^ 0x9E3779B9u));
            if (seed == 0)
                seed = 1;
            SceneSeeds[key] = seed;
        }
        return new SceneSeed(sceneId, sceneEpoch, seed);
    }

    internal static void SetSeed(SceneSeed seed)
    {
        if (seed.SceneId == 0 || seed.SceneEpoch == 0 || seed.Seed == 0)
            return;
        var key = (seed.SceneId, seed.SceneEpoch);
        if (!SceneSeeds.TryGetValue(key, out var current) || current != seed.Seed)
            SceneSeeds[key] = seed.Seed;
    }

    internal static bool HasSeed(SceneSeed seed) =>
        SceneSeeds.TryGetValue((seed.SceneId, seed.SceneEpoch), out var value) &&
        value == seed.Seed;

    internal static void StageRemoteScene(SceneSeed seed)
    {
        StageScene(seed);
    }

    internal static void StageLocalScene(SceneSeed seed)
    {
        StageScene(seed);
    }

    private static void StageScene(SceneSeed seed)
    {
        SetSeed(seed);
        if (seed.SceneId == 0 || seed.SceneEpoch == 0 ||
            StagedSceneEpochs.TryGetValue(seed.SceneId, out var current) &&
            !IsNewer(seed.SceneEpoch, current))
            return;
        StagedSceneEpochs[seed.SceneId] = seed.SceneEpoch;
    }

    internal static void ActivateLocalScene(uint sceneId, uint sceneEpoch)
    {
        if (sceneId == 0 || sceneEpoch == 0)
            return;
        ActiveSceneEpochs[sceneId] = sceneEpoch;
        if (StagedSceneEpochs.TryGetValue(sceneId, out var stagedEpoch) && stagedEpoch == sceneEpoch)
            StagedSceneEpochs.Remove(sceneId);
    }

    internal static void ActivateStagedScene(uint sceneId)
    {
        ActiveSceneEpochs.Remove(sceneId);
        if (StagedSceneEpochs.Remove(sceneId, out var sceneEpoch))
            ActiveSceneEpochs[sceneId] = sceneEpoch;
    }

    internal static void Clear()
    {
        SceneSeeds.Clear();
        StagedSceneEpochs.Clear();
        ActiveSceneEpochs.Clear();
        LoggedScopes.Clear();
    }

    internal static Scope Begin(FishAllocator allocator)
    {
        if (allocator == null)
            return default;
        return Begin(allocator, "fish", GetAllocatorUid(Protocol.SceneId(allocator.gameObject.scene.name), allocator));
    }

    internal static Scope Begin(Component source, string subsystem, string nativeUid = null)
    {
        if (source == null)
            return default;
        var sceneId = Protocol.SceneId(source.gameObject.scene.name);
        if (!TryGetPreparedEpoch(sceneId, out var sceneEpoch) ||
            !SceneSeeds.TryGetValue((sceneId, sceneEpoch), out var sceneSeed))
            return default;

        var uid = string.IsNullOrWhiteSpace(nativeUid)
            ? BuildFallbackAllocatorUid(sceneId, source.GetType().FullName ?? source.GetType().Name,
                HierarchyPath(source.transform))
            : nativeUid;
        var scope = BuildScopedUid(subsystem, uid);
        var finalSeed = CombineSeed(sceneSeed, scope);
        if (LoggedScopes.Add((sceneId, sceneEpoch, scope)))
            ProbeBehaviour.Logger?.LogDebug(
                $"RNG scope: scene={sceneId:X8}; epoch={sceneEpoch}; subsystem={subsystem}; " +
                $"uid={uid}; seed={finalSeed}");
        var previous = UnityEngine.Random.state;
        UnityEngine.Random.InitState(finalSeed);
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
        var path = HierarchyPath(allocator.transform);
        return BuildFallbackAllocatorUid(
            sceneId, allocator.GetType().FullName ?? nameof(FishAllocator), path);
    }

    private static string BuildFallbackAllocatorUid(uint sceneId, string componentType, string path) =>
        $"{sceneId:X8}|{componentType}|{path}";

    private static string BuildScopedUid(string subsystem, string nativeUid) =>
        $"{subsystem ?? string.Empty}|{nativeUid ?? string.Empty}";

    private static string HierarchyPath(Transform transform)
    {
        var path = new StringBuilder();
        for (var current = transform; current != null; current = current.parent)
            path.Insert(0, $"/{current.GetSiblingIndex()}:{current.name}");
        return path.ToString();
    }

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

    private static bool HasActiveSeed(SceneSeed seed) =>
        ActiveSceneEpochs.TryGetValue(seed.SceneId, out var sceneEpoch) &&
        sceneEpoch == seed.SceneEpoch && HasSeed(seed);

    private static bool TryGetPreparedEpoch(uint sceneId, out uint sceneEpoch) =>
        ActiveSceneEpochs.TryGetValue(sceneId, out sceneEpoch) ||
        StagedSceneEpochs.TryGetValue(sceneId, out sceneEpoch);

    internal static bool AllowUnseededScope(FishSpawnSeedCoordinator.Scope scope) =>
        scope.Active || ProbeBehaviour.Instance?.IsOnlineSession != true;

    private static bool IsNewer(uint value, uint previous) =>
        value != previous && unchecked((int)(value - previous)) > 0;

    internal static IEnumerable<MethodBase> DeclaredFamilyRoots(
        Type root, string methodName, Type[] arguments)
    {
        foreach (var type in AccessTools.GetTypesFromAssembly(root.Assembly))
        {
            if (!root.IsAssignableFrom(type))
                continue;
            var method = AccessTools.DeclaredMethod(type, methodName, arguments);
            if (method != null)
                yield return method;
        }
    }
}

[HarmonyPatch]
internal static class FishAllocatorSpawnSeedPatch
{
    private static IEnumerable<MethodBase> TargetMethods() =>
        FishSpawnSeedCoordinator.DeclaredFamilyRoots(
            typeof(FishAllocator), nameof(FishAllocator.Spawn), Type.EmptyTypes);

    private static bool Prefix(FishAllocator __instance, out FishSpawnSeedCoordinator.Scope __state)
    {
        __state = FishSpawnSeedCoordinator.Begin(__instance);
        return FishSpawnSeedCoordinator.AllowUnseededScope(__state);
    }

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
    private static IEnumerable<MethodBase> TargetMethods() =>
        FishSpawnSeedCoordinator.DeclaredFamilyRoots(
            typeof(FishAllocator), nameof(FishAllocator.Spawn), new[] { typeof(bool) });

    private static bool Prefix(FishAllocator __instance, out FishSpawnSeedCoordinator.Scope __state)
    {
        __state = FishSpawnSeedCoordinator.Begin(__instance);
        return FishSpawnSeedCoordinator.AllowUnseededScope(__state);
    }

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

[HarmonyPatch]
internal static class FishBushSpawnSeedPatch
{
    private static IEnumerable<MethodBase> TargetMethods() =>
        FishSpawnSeedCoordinator.DeclaredFamilyRoots(
            typeof(FishBushAllocator), nameof(FishBushAllocator.TrySpawnFish), new[] { typeof(bool) });

    private static bool Prefix(FishBushAllocator __instance, out FishSpawnSeedCoordinator.Scope __state)
    {
        __state = FishSpawnSeedCoordinator.Begin(__instance?.fishAllocator, "fish-bush");
        return FishSpawnSeedCoordinator.AllowUnseededScope(__state);
    }

    private static Exception Finalizer(Exception __exception, FishSpawnSeedCoordinator.Scope __state)
    {
        FishSpawnSeedCoordinator.End(__state);
        return __exception;
    }
}

[HarmonyPatch]
internal static class PickupSpawnerSeedPatch
{
    private static IEnumerable<MethodBase> TargetMethods() =>
        FishSpawnSeedCoordinator.DeclaredFamilyRoots(
            typeof(SpawnerPickupItem), nameof(SpawnerPickupItem.Start), Type.EmptyTypes);

    private static bool Prefix(SpawnerPickupItem __instance, out FishSpawnSeedCoordinator.Scope __state)
    {
        __state = FishSpawnSeedCoordinator.Begin(__instance, "pickup", __instance?.UniqueID);
        return FishSpawnSeedCoordinator.AllowUnseededScope(__state);
    }

    private static Exception Finalizer(Exception __exception, FishSpawnSeedCoordinator.Scope __state)
    {
        FishSpawnSeedCoordinator.End(__state);
        return __exception;
    }
}

[HarmonyPatch]
internal static class ChestSpawnerSeedPatch
{
    private static IEnumerable<MethodBase> TargetMethods() =>
        FishSpawnSeedCoordinator.DeclaredFamilyRoots(
            typeof(SpawnerChestItem), nameof(SpawnerChestItem.Start), Type.EmptyTypes);

    private static bool Prefix(SpawnerChestItem __instance, out FishSpawnSeedCoordinator.Scope __state)
    {
        __state = FishSpawnSeedCoordinator.Begin(__instance, "chest", __instance?.UniqueID);
        return FishSpawnSeedCoordinator.AllowUnseededScope(__state);
    }

    private static Exception Finalizer(Exception __exception, FishSpawnSeedCoordinator.Scope __state)
    {
        FishSpawnSeedCoordinator.End(__state);
        return __exception;
    }
}

[HarmonyPatch(typeof(InstanceItemSpawnHandler), nameof(InstanceItemSpawnHandler.SelectRandomOne))]
internal static class ItemDropSelectionSeedPatch
{
    private static void Prefix(InstanceItemSpawnHandler __instance,
        out FishSpawnSeedCoordinator.Scope __state) =>
        __state = FishSpawnSeedCoordinator.Begin(__instance, "item-drop");

    private static Exception Finalizer(Exception __exception, FishSpawnSeedCoordinator.Scope __state)
    {
        FishSpawnSeedCoordinator.End(__state);
        return __exception;
    }
}

[HarmonyPatch(typeof(SavedRandomActivator), nameof(SavedRandomActivator.SelectRandomOne))]
internal static class SavedRandomActivatorSeedPatch
{
    private static bool Prefix(SavedRandomActivator __instance, out FishSpawnSeedCoordinator.Scope __state)
    {
        __state = FishSpawnSeedCoordinator.Begin(__instance, "saved-random", __instance?.UniqueID);
        return FishSpawnSeedCoordinator.AllowUnseededScope(__state);
    }

    private static Exception Finalizer(Exception __exception, FishSpawnSeedCoordinator.Scope __state)
    {
        FishSpawnSeedCoordinator.End(__state);
        return __exception;
    }
}

[HarmonyPatch(typeof(RandomActivator), nameof(RandomActivator.Awake))]
internal static class RandomActivatorSeedPatch
{
    private static bool Prefix(RandomActivator __instance, out FishSpawnSeedCoordinator.Scope __state)
    {
        __state = FishSpawnSeedCoordinator.Begin(__instance, "random-activator");
        return FishSpawnSeedCoordinator.AllowUnseededScope(__state);
    }

    private static Exception Finalizer(Exception __exception, FishSpawnSeedCoordinator.Scope __state)
    {
        FishSpawnSeedCoordinator.End(__state);
        return __exception;
    }
}

[HarmonyPatch(typeof(JungleProximitySavedRandomActivator),
    nameof(JungleProximitySavedRandomActivator.TryLockNearestCandidate))]
internal static class JungleProximityRandomAuthorityPatch
{
    private static bool Prefix() => HostAuthorityPolicy.CanOwnHostAction;
}

[HarmonyPatch(typeof(JungleCreatureNest), nameof(JungleCreatureNest.RandomSpawnCreature))]
internal static class JungleCreatureNestSeedPatch
{
    private static bool Prefix(JungleCreatureNest __instance,
        out FishSpawnSeedCoordinator.Scope __state)
    {
        __state = FishSpawnSeedCoordinator.Begin(__instance, "jungle-creature");
        return FishSpawnSeedCoordinator.AllowUnseededScope(__state);
    }

    private static Exception Finalizer(Exception __exception, FishSpawnSeedCoordinator.Scope __state)
    {
        FishSpawnSeedCoordinator.End(__state);
        return __exception;
    }
}
