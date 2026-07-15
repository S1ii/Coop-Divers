using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DaveTheDiverMP;

internal sealed class SceneReplicator
{
    private const ushort NativeDefaults = 1 << 15;
    private readonly ManualLogSource _log;
    private SceneTransitionCommand? _pending;
    private bool _applyingHostTransition;

    internal SceneReplicator(ManualLogSource log) => _log = log;

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
        session.SendSceneTransition(new SceneTransitionCommand(
            sceneName, (int)transitionType, options));
    }

    internal void OnHostObservedScene(UdpSession session, string sceneName)
    {
        if (session == null || string.IsNullOrWhiteSpace(sceneName) || sceneName == "Empty")
            return;
        session.SendSceneTransition(new SceneTransitionCommand(
            sceneName, (int)SceneTransitionType.FadeOutIn, NativeDefaults));
    }

    internal void Update(SessionRole role, UdpSession session)
    {
        if (role != SessionRole.Client || !session.Connected)
        {
            while (session.TryTakeSceneTransition(out _))
            {
            }
            _pending = null;
            return;
        }

        while (session.TryTakeSceneTransition(out var command))
            _pending = command;
        if (!_pending.HasValue || SceneLoader.IsSceneLoading)
            return;

        var pending = _pending.Value;
        if (SceneManager.GetActiveScene().name == pending.SceneName)
        {
            _pending = null;
            return;
        }

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

    internal bool AllowTransition(SessionRole role) =>
        role != SessionRole.Client || _applyingHostTransition;

    internal void Clear()
    {
        _pending = null;
        _applyingHostTransition = false;
    }

    private static void Set(ref ushort options, int bit, bool enabled)
    {
        if (enabled)
            options |= (ushort)(1 << bit);
    }

    private static bool Get(ushort options, int bit) =>
        (options & (1 << bit)) != 0;
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
