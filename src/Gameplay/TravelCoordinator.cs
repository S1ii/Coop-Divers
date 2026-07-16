using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using Common.Contents;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace DaveTheDiverMP;

internal static class TravelTargets
{
    internal const uint Lobby = 0x101;
    internal const uint SushiBar = 0x102;
    internal const uint Farm = 0x103;
    internal const uint FishFarm = 0x104;
    internal const uint SushiBranch = 0x105;
    internal const uint Dredge = 0x106;
    internal const uint Jungle = 0x107;

    internal static uint FromMoveScene(MoveSceneElement.SceneName scene) => scene switch
    {
        MoveSceneElement.SceneName.Lobby => Lobby,
        MoveSceneElement.SceneName.SuShi => SushiBar,
        MoveSceneElement.SceneName.Farm => Farm,
        MoveSceneElement.SceneName.FishFarm => FishFarm,
        MoveSceneElement.SceneName.SushiBranch => SushiBranch,
        MoveSceneElement.SceneName.Dredge => Dredge,
        MoveSceneElement.SceneName.Jungle => Jungle,
        _ => 0
    };

    internal static uint JungleFastTravel(
        string sceneName,
        SceneType sceneType,
        SceneConnectLocationID location) =>
        Protocol.SceneId($"travel:jungle:{sceneName}:{(int)sceneType}:{(int)location}") | 0x80000000u;

    internal static string Name(uint targetId) => targetId switch
    {
        Lobby => "Lobby",
        SushiBar => "SushiBar",
        Farm => "Farm",
        FishFarm => "FishFarm",
        SushiBranch => "SushiBranch",
        Dredge => "Dredge",
        Jungle => "Jungle",
        _ => $"0x{targetId:X8}"
    };
}

internal sealed class TravelCoordinator
{
    private sealed class ElementText
    {
        internal readonly MoveSceneElement Element;
        internal string Original;
        internal readonly Dictionary<TMP_Text, string> TmpLabels = new();
        internal readonly Dictionary<UnityEngine.UI.Text, string> LegacyLabels = new();
        internal string Override;

        internal ElementText(MoveSceneElement element, string original)
        {
            Element = element;
            Original = original;
        }
    }

    private readonly ManualLogSource _log;
    private readonly Dictionary<MoveSceneElement, ElementText> _elementTexts = new();
    private uint _targetId;
    private uint _localRevision = 1;
    private uint _remoteRevision;
    private uint _stateRevision;
    private uint _lastStateRevision;
    private bool _hostReady;
    private bool _clientReady;
    private bool _clientNativeStarted;
    private bool _soloAllowed;
    private bool _hostDead;
    private bool _allowNative;
    private bool _allowClientTransition;
    private bool _starting;
    private bool _warnedMissingAction;
    private float _nextElementScan;
    private Action _hostAction;
    private Action _clientAction;
    private TravelRoute _route;

    internal TravelCoordinator(ManualLogSource log) => _log = log;

    internal bool Request(
        SessionRole role,
        UdpSession session,
        uint targetId,
        Action localAction,
        bool soloAllowed,
        bool hostDead,
        MoveSceneElement element = null,
        TravelRoute route = default)
    {
        if (_allowNative)
            return true;
        if (_starting)
            return false;
        if (session == null || !session.Connected || targetId == 0)
            return true;

        Select(targetId);
        _soloAllowed = soloAllowed;
        _hostDead = hostDead;
        _route = route;
        if (element != null)
            ObserveElement(element);

        if (role == SessionRole.Host)
        {
            _hostReady = true;
            _hostAction ??= localAction;
            Publish(session);
            TryStart();
        }
        else if (role == SessionRole.Client)
        {
            _clientReady = true;
            _clientAction ??= localAction;
            _localRevision = NextRevision(_localRevision);
            session.SendTravelReady(new TravelReady(
                targetId, _localRevision, true, false, _route));
        }
        RefreshText();
        _log.LogInfo(
            $"Travel {TravelTargets.Name(targetId)}: host={_hostReady}; " +
            $"client={_clientReady}; required={RequiredPlayers}");
        return false;
    }

    internal void Update(
        SessionRole role,
        UdpSession session,
        bool hostDead,
        bool clientDead)
    {
        if (session == null || !session.Connected)
        {
            Reset();
            return;
        }

        ScanElements();
        if (role == SessionRole.Host)
        {
            var soloAllowed = hostDead || clientDead;
            var changed = _soloAllowed != soloAllowed || _hostDead != hostDead;
            _soloAllowed = soloAllowed;
            _hostDead = hostDead;
            while (session.TryTakeTravelReady(out var ready))
            {
                if (!IsNewer(ready.Revision, _remoteRevision))
                    continue;
                _remoteRevision = ready.Revision;
                if (_targetId != ready.TargetId)
                {
                    Select(ready.TargetId);
                    changed = true;
                }
                _route = ready.Route;
                changed |= _clientReady != ready.Ready;
                _clientReady = ready.Ready;
                changed |= _clientNativeStarted != ready.NativeStarted;
                _clientNativeStarted = ready.NativeStarted;
            }

            if (_soloAllowed && _clientReady && !_hostReady)
            {
                _hostReady = true;
                _hostAction ??= ResolveHostAction(_targetId, _route);
                changed = true;
            }
            if (changed && _targetId != 0)
                Publish(session);
            TryStart();
        }
        else if (role == SessionRole.Client)
        {
            while (session.TryTakeTravelState(out var state))
            {
                if (!IsNewer(state.Revision, _lastStateRevision))
                    continue;
                _lastStateRevision = state.Revision;
                Select(state.TargetId, keepClientReady: true);
                _hostReady = state.HostReady;
                _clientReady = state.ClientReady;
                _soloAllowed = state.SoloAllowed;
                _hostDead = state.HostDead;
            }
            TryStartClient(session);
        }
        RefreshText();
    }

    internal void LateUpdate() => RefreshText();

    internal void Reset()
    {
        RestoreText();
        _targetId = 0;
        _remoteRevision = 0;
        _lastStateRevision = 0;
        _hostReady = false;
        _clientReady = false;
        _clientNativeStarted = false;
        _soloAllowed = false;
        _hostDead = false;
        _allowNative = false;
        _allowClientTransition = false;
        _starting = false;
        _warnedMissingAction = false;
        _nextElementScan = 0f;
        _hostAction = null;
        _clientAction = null;
        _route = default;
        _elementTexts.Clear();
    }

    internal bool AllowClientSceneTransition()
    {
        if (!_allowClientTransition)
            return false;
        _allowClientTransition = false;
        return true;
    }

    private int ReadyPlayers => _soloAllowed
        ? (_hostReady || _clientReady ? 1 : 0)
        : (_hostReady ? 1 : 0) + (_clientReady ? 1 : 0);

    private int RequiredPlayers => _soloAllowed ? 1 : 2;

    private void Select(uint targetId, bool keepClientReady = false)
    {
        if (_targetId == targetId)
            return;
        _targetId = targetId;
        _hostReady = false;
        if (!keepClientReady)
            _clientReady = false;
        _clientNativeStarted = false;
        _hostAction = null;
        _clientAction = null;
        _route = default;
        _warnedMissingAction = false;
    }

    private void Publish(UdpSession session)
    {
        if (_targetId == 0)
            return;
        _stateRevision = NextRevision(_stateRevision);
        session.SendTravelState(new TravelState(
            _targetId, _stateRevision, _hostReady, _clientReady, _soloAllowed, _hostDead));
    }

    private void TryStart()
    {
        var ready = _soloAllowed
            ? _hostDead
                ? _clientReady && _clientNativeStarted
                : _hostReady
            : _hostReady && _clientReady && _clientNativeStarted;
        if (!ready || _starting)
            return;
        if (_hostAction == null)
        {
            if (!_warnedMissingAction)
            {
                _warnedMissingAction = true;
                _log.LogWarning(
                    $"Travel {TravelTargets.Name(_targetId)}: no host transition route is loaded");
            }
            return;
        }

        var action = _hostAction;
        _hostAction = null;
        _starting = true;
        _allowNative = true;
        try
        {
            _log.LogInfo(
                $"Travel {TravelTargets.Name(_targetId)}: {RequiredPlayers}/{RequiredPlayers}; " +
                "host starts native transition");
            action();
        }
        finally
        {
            _allowNative = false;
        }
    }

    private void TryStartClient(UdpSession session)
    {
        if ((_soloAllowed && !_hostDead) ||
            !_hostReady || !_clientReady || _clientNativeStarted)
            return;

        _clientNativeStarted = true;
        _starting = true;
        _localRevision = NextRevision(_localRevision);
        session.SendTravelReady(new TravelReady(
            _targetId, _localRevision, true, true, _route));
        if (_clientAction == null)
        {
            _log.LogWarning(
                $"Travel {TravelTargets.Name(_targetId)}: client uses host scene fallback");
            return;
        }

        _allowNative = true;
        _allowClientTransition = true;
        try
        {
            _log.LogInfo($"Travel {TravelTargets.Name(_targetId)}: client starts native transition");
            _clientAction();
        }
        finally
        {
            _allowNative = false;
        }
    }

    private Action ResolveHostAction(uint targetId, TravelRoute route)
    {
        if (!string.IsNullOrEmpty(route.SceneName) &&
            TravelTargets.JungleFastTravel(
                route.SceneName,
                (SceneType)route.SceneType,
                (SceneConnectLocationID)route.Location) == targetId)
        {
            var fastTravel = FindLoaded<JDLC.FastTravelPanelController>();
            if (fastTravel != null)
                return () => fastTravel.ChangeScene(
                    route.SceneName,
                    (SceneType)route.SceneType,
                    (SceneConnectLocationID)route.Location,
                    (SceneTransitionType)route.TransitionType);
        }

        foreach (var element in UnityEngine.Object.FindObjectsByType<MoveSceneElement>(FindObjectsSortMode.None))
            if (element != null && TravelTargets.FromMoveScene(element.Scene) == targetId)
                return element.OnClick;
        foreach (var element in Resources.FindObjectsOfTypeAll<MoveSceneElement>())
            if (IsLoaded(element) && TravelTargets.FromMoveScene(element.Scene) == targetId)
                return element.OnClick;

        var fishFarm = FindLoaded<FishFarm.FishFarmManager>();
        if (fishFarm != null)
        {
            if (targetId == TravelTargets.Lobby)
                return fishFarm.GoToLobby;
            if (targetId == TravelTargets.SushiBar)
                return fishFarm.GoToSushiBar;
            if (targetId == TravelTargets.Farm)
                return fishFarm.GoToFarm;
            if (targetId == TravelTargets.SushiBranch)
                return fishFarm.GoToSushiBarBranch;
        }

        if (targetId != TravelTargets.Lobby)
            return null;
        var sushiExit = FindLoaded<SushiBarExitPanel>();
        if (sushiExit != null)
            return sushiExit.OnExecute;
        var dredge = FindLoaded<Dredge.DredgeManager>();
        if (dredge != null)
            return dredge.ReturnToLobby;
        var mirror = FindLoaded<Interaction.Escape.EscapeMirror>();
        var player = FindLoaded<PlayerCharacter>();
        if (mirror != null && player != null)
            return () => mirror.SuccessInteract(player);
        return null;
    }

    private static T FindLoaded<T>() where T : Component
    {
        var active = UnityEngine.Object.FindFirstObjectByType<T>();
        if (active != null)
            return active;
        foreach (var candidate in Resources.FindObjectsOfTypeAll<T>())
            if (IsLoaded(candidate))
                return candidate;
        return null;
    }

    private static bool IsLoaded(Component component) =>
        component != null && component.gameObject != null && component.gameObject.scene.IsValid();

    private void ScanElements()
    {
        if (Time.realtimeSinceStartup < _nextElementScan)
            return;
        _nextElementScan = Time.realtimeSinceStartup + 1f;
        foreach (var element in UnityEngine.Object.FindObjectsByType<MoveSceneElement>(FindObjectsSortMode.None))
            if (element != null && TravelTargets.FromMoveScene(element.Scene) != 0)
                ObserveElement(element);
    }

    private void ObserveElement(MoveSceneElement element)
    {
        if (_elementTexts.ContainsKey(element))
            return;

        var original = element?._text?.text?.textUGUI?.text;
        var snapshot = new ElementText(element, original);
        var direct = element?._text?.text?.textUGUI;
        if (direct != null && !string.IsNullOrWhiteSpace(direct.text))
        {
            snapshot.LegacyLabels[direct] = direct.text;
            if (string.IsNullOrEmpty(original))
                original = direct.text;
        }
        if (element != null)
        {
            foreach (var label in element.GetComponentsInChildren<TMP_Text>(true))
            {
                if (label == null || string.IsNullOrWhiteSpace(label.text) || label.text.Trim().Length <= 1)
                    continue;
                snapshot.TmpLabels.TryAdd(label, label.text);
                if (string.IsNullOrEmpty(original))
                    original = label.text;
                break;
            }
        }
        if (string.IsNullOrEmpty(snapshot.Original))
            snapshot.Original = original;
        if (!string.IsNullOrEmpty(snapshot.Original))
            _elementTexts[element] = snapshot;
    }

    private void RefreshText()
    {
        foreach (var snapshot in _elementTexts.Values)
        {
            var element = snapshot.Element;
            if (element == null)
                continue;
            var targetId = TravelTargets.FromMoveScene(element.Scene);
            var ready = _targetId == targetId ? ReadyPlayers : 0;
            var text = $"{snapshot.Original}? {ready}/{RequiredPlayers}";
            var textData = element._text;
            if (snapshot.Override != text && textData != null)
            {
                textData.ReleaseAllOverrideDelegates();
                textData.SetOverride((Func<string, string>)(_ => text), true);
                snapshot.Override = text;
            }
            foreach (var pair in snapshot.TmpLabels)
                if (pair.Key != null && pair.Key.text != text)
                    pair.Key.text = text;
            foreach (var pair in snapshot.LegacyLabels)
                if (pair.Key != null && pair.Key.text != text)
                    pair.Key.text = text;
        }
    }

    private void RestoreText()
    {
        foreach (var snapshot in _elementTexts.Values)
        {
            var element = snapshot.Element;
            var textData = element?._text;
            if (textData != null)
            {
                textData.ReleaseAllOverrideDelegates();
                textData.SetOverride((Func<string, string>)(_ => snapshot.Original), true);
            }
            foreach (var pair in snapshot.TmpLabels)
                if (pair.Key != null)
                    pair.Key.text = pair.Value;
            foreach (var pair in snapshot.LegacyLabels)
                if (pair.Key != null)
                    pair.Key.text = pair.Value;
        }
    }

    private static uint NextRevision(uint revision) =>
        revision == uint.MaxValue ? 1u : revision + 1u;

    private static bool IsNewer(uint revision, uint previous) =>
        unchecked((int)(revision - previous)) > 0;
}

[HarmonyPatch(typeof(MoveSceneElement), nameof(MoveSceneElement.OnClick))]
internal static class MoveSceneTravelPatch
{
    private static bool Prefix(MoveSceneElement __instance) =>
        __instance == null ||
        (ProbeBehaviour.Instance?.RequestMoveSceneTravel(__instance) ?? true);
}

[HarmonyPatch(typeof(SushiBarExitPanel), nameof(SushiBarExitPanel.OnExecute))]
internal static class SushiBarReturnPatch
{
    private static bool Prefix(SushiBarExitPanel __instance) =>
        ProbeBehaviour.Instance?.RequestSushiBarReturn(__instance) ?? true;
}

[HarmonyPatch]
internal static class FishFarmTravelPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        var type = typeof(FishFarm.FishFarmManager);
        yield return AccessTools.Method(type, nameof(FishFarm.FishFarmManager.GoToSushiBar));
        yield return AccessTools.Method(type, nameof(FishFarm.FishFarmManager.GoToFarm));
        yield return AccessTools.Method(type, nameof(FishFarm.FishFarmManager.GoToLobby));
        yield return AccessTools.Method(type, nameof(FishFarm.FishFarmManager.GoToSushiBarBranch));
    }

    private static bool Prefix(
        FishFarm.FishFarmManager __instance,
        MethodBase __originalMethod) =>
        ProbeBehaviour.Instance?.RequestFishFarmTravel(__instance, __originalMethod.Name) ?? true;
}

[HarmonyPatch(typeof(Dredge.DredgeManager), nameof(Dredge.DredgeManager.ReturnToLobby))]
internal static class DredgeReturnPatch
{
    private static bool Prefix(Dredge.DredgeManager __instance) =>
        ProbeBehaviour.Instance?.RequestDredgeReturn(__instance) ?? true;
}

[HarmonyPatch(
    typeof(Interaction.Escape.EscapeMirror),
    nameof(Interaction.Escape.EscapeMirror.SuccessInteract))]
internal static class EscapeMirrorTravelPatch
{
    private static bool Prefix(
        Interaction.Escape.EscapeMirror __instance,
        BaseCharacter __0) =>
        ProbeBehaviour.Instance?.RequestEscapeMirror(__instance, __0) ?? true;
}

[HarmonyPatch(typeof(JDLC.FastTravelPanelController), nameof(JDLC.FastTravelPanelController.ChangeScene))]
internal static class JungleFastTravelPatch
{
    private static bool Prefix(
        JDLC.FastTravelPanelController __instance,
        string __0,
        SceneType __1,
        SceneConnectLocationID __2,
        SceneTransitionType __3) =>
        ProbeBehaviour.Instance?.RequestJungleFastTravel(__instance, __0, __1, __2, __3) ?? true;
}
