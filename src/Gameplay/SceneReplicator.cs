using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DaveTheDiverMP;

internal readonly struct ResolvedSceneMetadata
{
    internal readonly SceneType SceneType;
    internal readonly bool CanDive;
    internal readonly bool IsSharedAction;
    internal readonly bool IsAdditive;
    internal readonly string RouteIdentity;
    internal readonly string Source;
    internal readonly bool HasNativeSceneType;

    internal ResolvedSceneMetadata(
        SceneType sceneType,
        bool canDive,
        bool isSharedAction,
        bool isAdditive,
        string routeIdentity,
        string source,
        bool hasNativeSceneType = false)
    {
        SceneType = sceneType;
        CanDive = canDive;
        IsSharedAction = isSharedAction;
        IsAdditive = isAdditive;
        RouteIdentity = routeIdentity;
        Source = source;
        HasNativeSceneType = hasNativeSceneType;
    }
}

internal static class SceneMetadataResolver
{
    private static readonly string[] DivePrefixes =
    {
        "A0", "B0", "C0", "Boss_", "ControlCenter_", "GlacialArea_",
        "GlacialPassage_", "MermanWarehouse", "SecretRoom_", "C00_",
        "Godzilla_Boss_", "Godzilla_underwater_", "DR_Jungle_Lake"
    };

    private static readonly string[] SharedActionPrefixes =
    {
        "DR_Jungle_HollowEarth", "DR_Jungle_RPG_", "DR_Jungle_MiniGames_",
        "DR_Jungle_DaiMuDaimu", "DR_Jungle_Basilo_Inside",
        "Godzilla_Lobby_Fight"
    };

    internal static void SelfTest()
    {
        var first = Fallback("A01_Test");
        var second = Fallback("A01_Test");
        var sharedOnly = Fallback("DR_Jungle_RPG_Test");
        var lobby = Fallback("DR_Lobby");
        if (!first.CanDive || !first.IsSharedAction || first.IsAdditive ||
            first.RouteIdentity != second.RouteIdentity || first.Source != second.Source ||
            sharedOnly.CanDive || !sharedOnly.IsSharedAction ||
            lobby.SceneType != SceneType.lobby || lobby.CanDive || lobby.IsSharedAction)
            throw new System.InvalidOperationException("Scene metadata fallback is not deterministic");

        var native = ResolveKnown(
            "FutureScene", SceneType.level_additive, true, true, false, true, 42, "context");
        var nativeNegative = ResolveKnown(
            "A01_Test", SceneType.special_event, true, true, false, false, 0, "loader");
        var nativeDiving = ResolveKnown(
            "FutureDive", SceneType.special_event, true, true, true, false, 0, "loader");
        var pendingType = ResolveKnown(
            "A01_Test", SceneType.none, false, true, false, false, 0, "type-pending");
        if (!native.CanDive || !native.IsAdditive || native.SceneType != SceneType.level_additive ||
            native.RouteIdentity != "tid:42" || native.Source != "native:data+context+tid" ||
            !native.HasNativeSceneType || nativeNegative.CanDive || nativeNegative.IsSharedAction ||
            !nativeDiving.CanDive || !nativeDiving.IsSharedAction ||
            !pendingType.CanDive || pendingType.HasNativeSceneType || pendingType.IsAdditive)
            throw new System.InvalidOperationException("Native scene metadata precedence failed");

        var mergedPartial = MergeRefresh(Fallback("A01_Test"), pendingType);
        var transientPartial = new ResolvedSceneMetadata(
            SceneType.none, false, false, false, "name:00000000", "native:type-pending");
        var retainedPartial = MergeRefresh(mergedPartial, transientPartial);
        var fullNative = ResolveKnown(
            "A01_Test", SceneType.special_event, true, true, false, false, 42, "context");
        var mergedNative = MergeRefresh(retainedPartial, fullNative);
        if (!mergedPartial.CanDive || mergedPartial.HasNativeSceneType ||
            !retainedPartial.CanDive || !retainedPartial.Source.Contains("retained:dive") ||
            !mergedNative.HasNativeSceneType || mergedNative.CanDive || mergedNative.IsSharedAction)
            throw new System.InvalidOperationException("Scene metadata refresh merge failed");
    }

    internal static ResolvedSceneMetadata Resolve(string sceneName)
    {
        if (string.IsNullOrEmpty(sceneName))
            return Fallback(sceneName);

        try
        {
            // Native scene services settle independently during transitions; consume whichever is ready.
            DR.GameScene sceneData = null;
            var routeTid = 0;
            try
            {
                var dataManager = DataManager.Instance;
                sceneData = dataManager?.GetScene(sceneName);
                routeTid = dataManager?.GetSceneTID(sceneName) ?? 0;
            }
            catch
            {
            }
            var sceneType = SceneType.none;
            var hasSceneType = false;
            var typeSource = string.Empty;
            try
            {
                var context = SceneContext.Instance;
                if (context != null && context.CurrentSceneName == sceneName &&
                    context.SceneType != SceneType.none)
                {
                    sceneType = context.SceneType;
                    routeTid = context.CurrentSceneTID > 0 ? context.CurrentSceneTID : routeTid;
                    hasSceneType = true;
                    typeSource = "context";
                }
            }
            catch
            {
            }
            if (!hasSceneType)
            {
                try
                {
                    if (SceneLoader.CurrentSceneName == sceneName)
                    {
                        sceneData ??= SceneLoader.CurrentSceneData;
                        var loader = UnityEngine.Object.FindFirstObjectByType<SceneLoader>();
                        if (loader != null && loader.CurrentSceneType != SceneType.none)
                        {
                            sceneType = loader.CurrentSceneType;
                            hasSceneType = true;
                            typeSource = "loader";
                        }
                    }
                }
                catch
                {
                }
            }

            var nativeInGame = false;
            var sceneWithDiving = false;
            try
            {
                if (sceneData != null)
                {
                    sceneWithDiving = sceneData.SceneWithDiving;
                    nativeInGame = GameSceneDataExtension.IsInGameScene(sceneData);
                }
            }
            catch
            {
            }
            if (!hasSceneType)
            {
                if (sceneData == null)
                    return Fallback(sceneName, routeTid);
                return ResolveKnown(
                    sceneName, Fallback(sceneName).SceneType, false, true,
                    sceneWithDiving, nativeInGame, routeTid, "type-pending");
            }
            return ResolveKnown(
                sceneName, sceneType, true, sceneData != null, sceneWithDiving,
                nativeInGame, routeTid, typeSource);
        }
        catch
        {
            return Fallback(sceneName);
        }
    }

    internal static ResolvedSceneMetadata MergeRefresh(
        ResolvedSceneMetadata current,
        ResolvedSceneMetadata candidate)
    {
        if (candidate.HasNativeSceneType)
            return candidate;
        if (current.HasNativeSceneType ||
            !candidate.Source.StartsWith("native:", System.StringComparison.Ordinal))
            return current;

        var retainedDive = current.CanDive && !candidate.CanDive;
        var retainedShared = current.IsSharedAction && !candidate.IsSharedAction;
        return new ResolvedSceneMetadata(
            candidate.SceneType != SceneType.none ? candidate.SceneType : current.SceneType,
            current.CanDive || candidate.CanDive,
            current.IsSharedAction || candidate.IsSharedAction,
            current.IsAdditive || candidate.IsAdditive,
            candidate.RouteIdentity,
            candidate.Source + (retainedDive ? ";retained:dive" : string.Empty) +
            (retainedShared ? ";retained:shared" : string.Empty));
    }

    internal static bool Matches(
        ResolvedSceneMetadata left,
        ResolvedSceneMetadata right) =>
        left.SceneType == right.SceneType &&
        left.CanDive == right.CanDive &&
        left.IsSharedAction == right.IsSharedAction &&
        left.IsAdditive == right.IsAdditive &&
        left.RouteIdentity == right.RouteIdentity &&
        left.Source == right.Source &&
        left.HasNativeSceneType == right.HasNativeSceneType;

    private static bool IsDiveType(SceneType sceneType) => sceneType is
        SceneType.level or SceneType.level_additive or SceneType.boss or
        SceneType.Glacial_area or SceneType.godzilla_intermission_level;

    private static bool IsSharedActionType(SceneType sceneType) => sceneType is
        SceneType.jungle_rpg_forest or SceneType.jungle_rpg_dungeon_1F or
        SceneType.jungle_rpg_dungeon_B1F or SceneType.jungle_rpg_dungeon_B2F or
        SceneType.jungle_rpg_dungeon_B3F or SceneType.jungle_rpg_dungeon_WildBoarRoom or
        SceneType.jungle_basilo_inside or SceneType.godzilla_intermission_lobbyFight;

    private static ResolvedSceneMetadata ResolveKnown(
        string sceneName,
        SceneType sceneType,
        bool hasSceneType,
        bool hasSceneData,
        bool sceneWithDiving,
        bool nativeInGame,
        int routeTid,
        string typeSource)
    {
        var fallback = Fallback(sceneName, routeTid);
        var nativeDive = sceneWithDiving || nativeInGame || IsDiveType(sceneType);
        var nativeShared = nativeDive || IsSharedActionType(sceneType);
        var canDive = hasSceneType ? nativeDive : nativeDive || fallback.CanDive;
        var isShared = hasSceneType ? nativeShared : nativeShared || fallback.IsSharedAction;
        var source = $"native:{(hasSceneData ? "data+" : string.Empty)}{typeSource}" +
            (routeTid > 0 ? "+tid" : "+name-hash");
        if (!hasSceneType && fallback.CanDive)
            source += ";fallback:dive-prefix";
        else if (!hasSceneType && fallback.IsSharedAction)
            source += ";fallback:shared-prefix";
        return new ResolvedSceneMetadata(
            sceneType,
            canDive,
            isShared,
            sceneType == SceneType.level_additive,
            RouteIdentity(sceneName, routeTid),
            source,
            hasSceneType);
    }

    private static ResolvedSceneMetadata Fallback(string sceneName, int routeTid = 0)
    {
        var canDive = HasPrefix(sceneName, DivePrefixes);
        var sharedPrefix = HasPrefix(sceneName, SharedActionPrefixes);
        var lobby = sceneName?.StartsWith(
            "DR_Lobby", System.StringComparison.Ordinal) == true;
        var classification = canDive ? "dive-prefix" :
            sharedPrefix ? "shared-prefix" : lobby ? "lobby-prefix" : "none";
        return new ResolvedSceneMetadata(
            lobby ? SceneType.lobby : SceneType.none,
            canDive,
            canDive || sharedPrefix,
            false,
            RouteIdentity(sceneName, routeTid),
            $"fallback:{classification}+{(routeTid > 0 ? "tid" : "name-hash")}");
    }

    private static string RouteIdentity(string sceneName, int routeTid) => routeTid > 0
        ? $"tid:{routeTid}"
        : $"name:{Protocol.SceneId(sceneName ?? string.Empty):X8}";

    private static bool HasPrefix(string value, string[] prefixes)
    {
        if (string.IsNullOrEmpty(value))
            return false;
        foreach (var prefix in prefixes)
            if (value.StartsWith(prefix, System.StringComparison.Ordinal))
                return true;
        return false;
    }
}

internal sealed class SceneReplicator
{
    private const ushort NativeDefaults = 1 << 15;
    private const string AnyClientTransition = "*";
    private readonly ManualLogSource _log;
    private SceneTransitionCommand? _pending;
    private bool _applyingHostTransition;
    private string _allowedClientTransitionScene;
    private float _allowedClientTransitionUntil;

    internal SceneReplicator(ManualLogSource log) => _log = log;

    internal static void SelfTest()
    {
        SceneMetadataResolver.SelfTest();
        if (!CanAllowClientTransition(false, "A01", "A01", 2f, 3f) ||
            CanAllowClientTransition(false, "A01", "B01", 2f, 3f) ||
            CanAllowClientTransition(false, "A01", "A01", 4f, 3f) ||
            !CanAllowClientTransition(true, null, "B01", 4f, 0f) ||
            !ShouldWaitForSceneSeed(true, false) ||
            ShouldWaitForSceneSeed(true, true) ||
            ShouldWaitForSceneSeed(false, false))
            throw new System.InvalidOperationException("Client native transition gate failed");
        var gate = new SceneReplicator(null);
        gate.BeginClientNativeDiveTransition("A01", 1f);
        if (gate.AllowTransition(SessionRole.Client, "B01", 2f) ||
            gate.AllowTransition(SessionRole.Client, "A01", 2f))
            throw new System.InvalidOperationException("Client transition mismatch did not clear gate");
        gate.BeginClientNativeDiveTransition("A01", 1f);
        gate.CancelClientNativeDiveTransition();
        if (gate.AllowTransition(SessionRole.Client, "A01", 2f))
            throw new System.InvalidOperationException("Client transition cancellation failed");
        gate.BeginClientNativeDiveTransition("A01", 1f);
        if (!gate.AllowTransition(SessionRole.Client, "A01", 2f) ||
            gate.AllowTransition(SessionRole.Client, "A01", 2f))
            throw new System.InvalidOperationException("Client transition gate was not one-shot");
        gate.BeginClientNativeDiveTransition(null, 1f);
        if (!gate.AllowTransition(SessionRole.Client, "A02", 2f))
            throw new System.InvalidOperationException("Client wildcard dive transition failed");
    }

    internal void OnHostTransition(
        UdpSession session,
        string sceneName,
        SceneTransitionType transitionType,
        bool throughEmptyScene,
        bool initLoading,
        bool useStartTransition,
        bool useFinishTransition,
        bool unloadActiveScene,
        bool ignoreSameSceneCheck,
        bool isRetry,
        bool skipEmptySceneOptionIsUnloadAssets,
        bool firstFindSceneManagerInActiveScene)
    {
        ushort options = 0;
        Set(ref options, 0, throughEmptyScene);
        Set(ref options, 1, initLoading);
        Set(ref options, 2, useStartTransition);
        Set(ref options, 3, useFinishTransition);
        Set(ref options, 4, unloadActiveScene);
        Set(ref options, 5, ignoreSameSceneCheck);
        Set(ref options, 6, isRetry);
        Set(ref options, 7, skipEmptySceneOptionIsUnloadAssets);
        Set(ref options, 8, firstFindSceneManagerInActiveScene);
        var seed = FishSpawnSeedCoordinator.GetOrCreate(
            Protocol.SceneId(sceneName), session.NextLocalSceneEpoch);
        FishSpawnSeedCoordinator.StageLocalScene(seed);
        session.SendSceneTransition(new SceneTransitionCommand(
            sceneName, (int)transitionType, options, seed.SceneId, seed.SceneEpoch, seed.Seed));
    }

    internal void OnHostObservedScene(UdpSession session, string sceneName)
    {
        if (session == null || string.IsNullOrWhiteSpace(sceneName) || sceneName == "Empty")
            return;
        var seed = FishSpawnSeedCoordinator.GetOrCreate(
            Protocol.SceneId(sceneName), session.LocalSceneEpoch);
        FishSpawnSeedCoordinator.StageLocalScene(seed);
        session.SendSceneTransition(new SceneTransitionCommand(
            sceneName, (int)SceneTransitionType.FadeOutIn, NativeDefaults,
            seed.SceneId, seed.SceneEpoch, seed.Seed));
    }

    internal void BeginClientNativeDiveTransition(string sceneName, float now)
    {
        _allowedClientTransitionScene = string.IsNullOrEmpty(sceneName)
            ? AnyClientTransition
            : sceneName;
        _allowedClientTransitionUntil = now + 10f;
    }

    internal void CancelClientNativeDiveTransition()
    {
        _allowedClientTransitionScene = null;
        _allowedClientTransitionUntil = 0f;
    }

    internal void Update(SessionRole role, UdpSession session)
    {
        if (role != SessionRole.Client || !session.Connected)
        {
            while (session.TryTakeSceneTransition(out _))
            {
            }
            _pending = null;
            CancelClientNativeDiveTransition();
            return;
        }

        if (_allowedClientTransitionScene != null &&
            Time.realtimeSinceStartup > _allowedClientTransitionUntil)
            CancelClientNativeDiveTransition();

        while (session.TryTakeSceneTransition(out var command))
        {
            FishSpawnSeedCoordinator.StageRemoteScene(
                new SceneSeed(command.SceneId, command.SceneEpoch, command.Seed));
            _pending = command;
        }
        if (!_pending.HasValue || SceneLoader.IsSceneLoading)
            return;

        var pending = _pending.Value;
        if (SceneManager.GetActiveScene().name == pending.SceneName)
        {
            _pending = null;
            return;
        }
        if (ShouldWaitForSceneSeed(
                SceneMetadataResolver.Resolve(pending.SceneName).CanDive,
                FishSpawnSeedCoordinator.HasSeed(
                    new SceneSeed(pending.SceneId, pending.SceneEpoch, pending.Seed))))
            return;

        var loader = UnityEngine.Object.FindFirstObjectByType<SceneLoader>();
        if (loader == null)
            return;

        _pending = null;
        _log.LogInfo($"Network: following host to scene {pending.SceneName}");
        _applyingHostTransition = true;
        try
        {
            if ((pending.Options & NativeDefaults) != 0)
            {
                loader.ChangeSceneAsync(pending.SceneName, (SceneTransitionType)pending.TransitionType);
                return;
            }
            loader.ChangeSceneAsync(
                pending.SceneName,
                (SceneTransitionType)pending.TransitionType,
                Get(pending.Options, 0),
                Get(pending.Options, 1),
                Get(pending.Options, 2),
                Get(pending.Options, 3),
                Get(pending.Options, 4),
                null,
                null,
                Get(pending.Options, 5),
                Get(pending.Options, 6),
                Get(pending.Options, 7),
                Get(pending.Options, 8));
        }
        finally
        {
            _applyingHostTransition = false;
        }
    }

    internal bool AllowTransition(SessionRole role, string sceneName, float now)
    {
        if (role != SessionRole.Client || _applyingHostTransition)
            return true;
        if (!CanAllowClientTransition(
                _applyingHostTransition, _allowedClientTransitionScene,
                sceneName, now, _allowedClientTransitionUntil))
        {
            if (_allowedClientTransitionScene != null &&
                (now > _allowedClientTransitionUntil ||
                 _allowedClientTransitionScene != AnyClientTransition &&
                 !string.Equals(_allowedClientTransitionScene, sceneName,
                     System.StringComparison.Ordinal)))
                CancelClientNativeDiveTransition();
            return false;
        }
        CancelClientNativeDiveTransition();
        return true;
    }

    internal void Clear()
    {
        _pending = null;
        _applyingHostTransition = false;
        CancelClientNativeDiveTransition();
    }

    private static void Set(ref ushort options, int bit, bool enabled)
    {
        if (enabled)
            options |= (ushort)(1 << bit);
    }

    private static bool Get(ushort options, int bit) =>
        (options & (1 << bit)) != 0;

    private static bool CanAllowClientTransition(
        bool applyingHostTransition,
        string expectedScene,
        string requestedScene,
        float now,
        float expiresAt) =>
        applyingHostTransition ||
        (now <= expiresAt && (expectedScene == AnyClientTransition || string.Equals(
            expectedScene, requestedScene, System.StringComparison.Ordinal)));

    private static bool ShouldWaitForSceneSeed(bool canDive, bool hasSeed) =>
        canDive && !hasSeed;
}

[HarmonyPatch(typeof(SceneLoader), nameof(SceneLoader.ChangeSceneAsync))]
internal static class SceneTransitionPatch
{
    private static bool Prefix(
        string sceneName,
        SceneTransitionType sceneTranstionType,
        bool throughEmptyScene,
        bool initLoading,
        bool useStartTransition,
        bool useFinishTransition,
        bool unloadActiveScene,
        bool ignoreSameSceneCheck,
        bool isRetry,
        bool skipEmptySceneOption_isUnloadAssets,
        bool firstFindSceneManagerInActiveScene)
    {
        var behaviour = ProbeBehaviour.Instance;
        if (behaviour != null && !behaviour.AllowSceneTransition(sceneName))
            return false;
        behaviour?.OnSceneTransition(
            sceneName,
            sceneTranstionType,
            throughEmptyScene,
            initLoading,
            useStartTransition,
            useFinishTransition,
            unloadActiveScene,
            ignoreSameSceneCheck,
            isRetry,
            skipEmptySceneOption_isUnloadAssets,
            firstFindSceneManagerInActiveScene);
        return true;
    }
}
