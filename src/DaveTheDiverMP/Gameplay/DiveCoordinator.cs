using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using Common;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace DaveTheDiverMP;

internal sealed class DiveCoordinator
{
    private const float ExitDistance = 16f;
    private const float DiverCenterBelowWaterline = 1.4f;
    private readonly ManualLogSource _log;
    private uint _localRevision = 1;
    private uint _remoteRevision;
    private uint _stateRevision;
    private uint _lastStateRevision;
    private bool _hostReady;
    private bool _clientReady;
    private bool _starting;
    private bool _allowNativeStart;
    private bool _allowNativeExit;
    private bool _hostDead;
    private bool _clientDead;
    private uint _lifeRevision = 1;
    private uint _remoteLifeRevision;
    private uint _exitRevision = 1;
    private uint _remoteExitRevision;
    private bool _hostExitReady;
    private bool _clientExitReady;
    private LobbyStartGamePanelUI _panel;
    private StartParameter _startParameter;
    private TMP_Text _buttonLabel;
    private string _nativeButtonText;
    private bool _surfaceKnown;
    private float _surfaceY;
    private bool _clientWentBelowSurface;
    private bool _clientExitPromptShown;
    private InGameManager _exitManager;
    private SceneTransitionColorType _exitColor;
    private bool _exitPlayerDead;

    internal DiveCoordinator(ManualLogSource log) => _log = log;

    // The transport currently has one host and one client. Keeping the count
    // separate from the display lets the UI become 1/3 later without changing
    // its contract.
    internal int ReadyPlayers => (_hostReady ? 1 : 0) + (_clientReady ? 1 : 0);
    internal int RequiredPlayers => 2;

    internal bool RequestDive(
        SessionRole role,
        UdpSession session,
        LobbyStartGamePanelUI panel,
        StartParameter startParameter)
    {
        if (_allowNativeStart)
        {
            _allowNativeStart = false;
            return true;
        }
        if (session == null || !session.Connected)
            return true;

        _panel = panel;
        _startParameter = startParameter;

        if (role == SessionRole.Host)
        {
            if (!_hostReady)
            {
                _hostReady = true;
                Publish(session);
                _log.LogInfo("Dive: host is ready; waiting for client");
            }
            TryStartHostDive();
            return false;
        }
        if (role == SessionRole.Client && !_clientReady)
        {
            _clientReady = true;
            _localRevision = NextRevision(_localRevision);
            session.SendDiveReady(new DiveReady(_localRevision, true));
            _log.LogInfo("Dive: client is ready; waiting for host");
            return false;
        }
        return false;
    }

    internal void Update(SessionRole role, UdpSession session, float now, PlayerCharacter player)
    {
        if (session == null || !session.Connected)
        {
            Reset();
            return;
        }

        if (role == SessionRole.Host)
        {
            var changed = false;
            while (session.TryTakeDiveReady(out var ready))
            {
                if (!IsNewer(ready.Revision, _remoteRevision))
                    continue;
                _remoteRevision = ready.Revision;
                changed |= _clientReady != ready.Ready;
                _clientReady = ready.Ready;
            }
            if (changed)
            {
                Publish(session);
                _log.LogInfo(_clientReady ? "Dive: client is ready" : "Dive: client cancelled");
            }
            while (session.TryTakeDiverLifeState(out var life))
            {
                if (!IsNewer(life.Revision, _remoteLifeRevision))
                    continue;
                _remoteLifeRevision = life.Revision;
                _clientDead = life.IsDead;
                _log.LogInfo(_clientDead ? "Dive: client died" : "Dive: client revived");
            }
            while (session.TryTakeDiveExitRequest(out var request))
            {
                if (IsNewer(request.Revision, _remoteExitRevision))
                {
                    _remoteExitRevision = request.Revision;
                    _clientExitReady = true;
                    TryStartHostExit(session, now, player, null);
                }
            }
            TryStartHostDive();
            RefreshPrompt(session);
            return;
        }

        if (role == SessionRole.Client)
        {
            while (session.TryTakeDiveState(out var state))
            {
                if (!IsNewer(state.Revision, _lastStateRevision))
                    continue;
                _lastStateRevision = state.Revision;
                _hostReady = state.HostReady;
                _clientReady = state.ClientReady;
                _log.LogInfo($"Dive: host={_hostReady}; client={_clientReady}");
            }
            while (session.TryTakeDiverLifeState(out _))
            {
            }
            while (session.TryTakeDiveExitRequest(out _))
            {
            }
            EnsureClientExitPrompt(player);
        }
        RefreshPrompt(session);
    }

    internal void ObserveDivePanel(LobbyStartGamePanelUI panel)
    {
        if (panel != null)
            _panel = panel;
    }

    internal void Reset()
    {
        RestorePrompt();
        (_hostReady, _clientReady, _starting, _allowNativeStart, _allowNativeExit) =
            (false, false, false, false, false);
        (_hostDead, _clientDead) = (false, false);
        (_hostExitReady, _clientExitReady) = (false, false);
        (_lifeRevision, _remoteLifeRevision, _exitRevision, _remoteExitRevision) = (1, 0, 1, 0);
        _panel = null;
        _startParameter = null;
        _buttonLabel = null;
        _nativeButtonText = null;
        _surfaceKnown = false;
        _surfaceY = 0f;
        _clientWentBelowSurface = false;
        _clientExitPromptShown = false;
        _exitManager = null;
        _exitPlayerDead = false;
    }

    private void TryStartHostDive()
    {
        if (!_hostReady || !_clientReady || _starting || _panel == null)
            return;
        _starting = true;
        _allowNativeStart = true;
        _log.LogInfo("Dive: both players ready; host starts native dive");
        _panel.StartGame(_startParameter);
    }

    private void Publish(UdpSession session)
    {
        _stateRevision = NextRevision(_stateRevision);
        session.SendDiveState(new DiveState(_stateRevision, _hostReady, _clientReady));
    }

    internal bool RequestExit(
        SessionRole role,
        UdpSession session,
        float now,
        PlayerCharacter player,
        SceneExitTrigger trigger)
    {
        if (_allowNativeExit || session == null || !session.Connected || player == null)
            return true;
        if (role == SessionRole.Client)
        {
            _clientExitReady = true;
            _exitRevision = NextRevision(_exitRevision);
            session.SendDiveExitRequest(new DiveExitRequest(_exitRevision));
            _log.LogInfo("Dive: client confirmed exit; waiting for host");
            return false;
        }
        if (role != SessionRole.Host)
            return true;

        _hostExitReady = true;
        TryStartHostExit(session, now, player, trigger);
        return false;
    }

    internal bool RequestLobbyExit(
        SessionRole role,
        UdpSession session,
        float now,
        PlayerCharacter player,
        InGameManager manager,
        SceneTransitionColorType color,
        bool playerDead)
    {
        if (_allowNativeExit || session == null || !session.Connected || manager == null)
            return true;
        if (role == SessionRole.Client)
        {
            _clientExitReady = true;
            _exitRevision = NextRevision(_exitRevision);
            session.SendDiveExitRequest(new DiveExitRequest(_exitRevision));
            _log.LogInfo("Dive: client confirmed native lobby exit; waiting for host");
            return false;
        }
        if (role != SessionRole.Host)
            return true;

        _exitManager = manager;
        _exitColor = color;
        _exitPlayerDead = playerDead;
        _hostExitReady = true;
        TryStartHostExit(session, now, player, null);
        return false;
    }

    internal void ReportLocalLife(SessionRole role, UdpSession session, bool dead)
    {
        if (session == null || !session.Connected)
            return;
        if (role == SessionRole.Host)
        {
            _hostDead = dead;
            _log.LogInfo(dead ? "Dive: host died" : "Dive: host revived");
            return;
        }
        if (role != SessionRole.Client || _clientDead == dead)
            return;

        _clientDead = dead;
        _lifeRevision = NextRevision(_lifeRevision);
        session.SendDiverLifeState(new DiverLifeState(_lifeRevision, dead));
        _log.LogInfo(dead ? "Dive: client reported death" : "Dive: client reported revive");
    }

    private void TryStartHostExit(
        UdpSession session,
        float now,
        PlayerCharacter player,
        SceneExitTrigger trigger)
    {
        if (!CanExit(session, now, player))
        {
            _log.LogInfo(
                $"Dive: exit waiting; host={_hostExitReady}; client={_clientExitReady}; " +
                "living divers must both confirm and stay together");
            return;
        }
        trigger ??= FindLoadedExitTrigger();
        if (_exitManager == null && trigger == null)
        {
            _log.LogWarning("Dive: exit requested but no native exit route is loaded");
            return;
        }

        _allowNativeExit = true;
        try
        {
            _log.LogInfo(_hostDead || _clientDead
                ? "Dive: exit allowed; a diver is dead"
                : "Dive: exit allowed; divers are together");
            ProbeBehaviour.Instance?.PrepareDiveExitResult();
            if (_exitManager != null)
                _exitManager.GoToLobby(_exitColor, _exitPlayerDead);
            else
                trigger.OnOK();
        }
        finally
        {
            _allowNativeExit = false;
        }
    }

    private bool CanExit(UdpSession session, float now, PlayerCharacter player)
    {
        if (_hostDead || _clientDead)
            return _hostExitReady || _clientExitReady;
        if (!_hostExitReady || !_clientExitReady)
            return false;
        if (player == null || !session.TryGetFreshRemotePlayerSnapshot(now, 0.5f, out var remote))
            return false;
        var offset = player.transform.position - new Vector3(remote.X, remote.Y, remote.Z);
        return offset.sqrMagnitude <= ExitDistance * ExitDistance;
    }

    private void EnsureClientExitPrompt(PlayerCharacter player)
    {
        if (player == null)
            return;
        if (!_surfaceKnown)
        {
            var position = player.transform.position;
            if (!float.IsFinite(position.x) || !float.IsFinite(position.y) ||
                Mathf.Abs(position.x) >= 500f || Mathf.Abs(position.y) >= 500f)
                return;
            var waterline = float.NaN;
            try
            {
                var manager = InGameManager.Instance;
                if (manager != null)
                    waterline = manager.surfaceHeight;
            }
            catch
            {
            }
            var nativeSurface = waterline - DiverCenterBelowWaterline;
            _surfaceY = float.IsFinite(nativeSurface) &&
                Mathf.Abs(nativeSurface - position.y) < 10f
                ? nativeSurface
                : position.y - DiverCenterBelowWaterline;
            _surfaceKnown = true;
            _log.LogInfo(
                $"Dive: client surface boundary at y={_surfaceY:F2}; " +
                $"waterline={waterline:F2}");
        }

        var y = player.transform.position.y;
        if (y < _surfaceY - 2f)
        {
            _clientWentBelowSurface = true;
            _clientExitPromptShown = false;
            return;
        }
        if (!_clientWentBelowSurface || y < _surfaceY)
            return;

        var clamped = player.transform.position;
        clamped.y = _surfaceY;
        player.transform.position = clamped;
        if (_clientExitPromptShown)
            return;
        _clientExitPromptShown = true;
        _log.LogInfo("Dive: client reached the native return-to-lobby boundary");
    }

    private static SceneExitTrigger FindLoadedExitTrigger()
    {
        var active = UnityEngine.Object.FindFirstObjectByType<SceneExitTrigger>();
        if (active != null)
            return active;
        foreach (var candidate in Resources.FindObjectsOfTypeAll<SceneExitTrigger>())
            if (candidate != null && candidate.gameObject != null && candidate.gameObject.scene.IsValid())
                return candidate;
        return null;
    }

    private void RefreshPrompt(UdpSession session)
    {
        if (_panel == null || _panel.normalButton == null)
            return;
        if (_buttonLabel == null)
        {
            _buttonLabel = _panel.normalButton.GetComponentInChildren<TMP_Text>(true);
            if (_buttonLabel == null)
                return;
            _nativeButtonText = _buttonLabel.text;
        }
        if (string.IsNullOrEmpty(_nativeButtonText))
            return;
        var prompt = session != null && session.Connected
            ? $"{_nativeButtonText}? {ReadyPlayers}/{RequiredPlayers}"
            : _nativeButtonText;
        if (_buttonLabel.text != prompt)
            _buttonLabel.text = prompt;
    }

    private void RestorePrompt()
    {
        if (_buttonLabel != null && !string.IsNullOrEmpty(_nativeButtonText))
            _buttonLabel.text = _nativeButtonText;
    }

    private static uint NextRevision(uint revision) => revision == uint.MaxValue ? 1u : revision + 1u;

    private static bool IsNewer(uint revision, uint previous) =>
        unchecked((int)(revision - previous)) > 0;
}

[HarmonyPatch(typeof(LobbyStartGamePanelUI), "StartGame")]
internal static class DiveConsentStartPatch
{
    private static bool Prefix(LobbyStartGamePanelUI __instance, StartParameter __0) =>
        ProbeBehaviour.Instance?.RequestDive(__instance, __0) ?? true;
}

[HarmonyPatch(typeof(DiveTrigger), nameof(DiveTrigger.OnPlayerEnter))]
internal static class DiveConsentTriggerPatch
{
    private static void Postfix(DiveTrigger __instance, bool __0)
    {
        if (__0)
            ProbeBehaviour.Instance?.ObserveDivePanel(__instance.m_Panel);
    }
}

[HarmonyPatch(typeof(SceneExitTrigger), nameof(SceneExitTrigger.OnOK))]
internal static class DiveExitPatch
{
    private static bool Prefix(SceneExitTrigger __instance) =>
        ProbeBehaviour.Instance?.RequestDiveExit(__instance) ?? true;
}

[HarmonyPatch(typeof(InGameManager), nameof(InGameManager.GoToLobby))]
internal static class NativeDiveLobbyExitPatch
{
    private static bool Prefix(
        InGameManager __instance,
        SceneTransitionColorType __0,
        bool __1) =>
        ProbeBehaviour.Instance?.RequestDiveLobbyExit(__instance, __0, __1) ?? true;
}

[HarmonyPatch]
internal static class DiveLifePatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var method in AccessTools.GetDeclaredMethods(typeof(PlayerCharacter)))
            if (method.Name is nameof(PlayerCharacter.OnDie) or nameof(PlayerCharacter.OnRevive))
                yield return method;
    }

    private static void Postfix(MethodBase __originalMethod)
    {
        ProbeBehaviour.Instance?.ReportDiveLife(
            __originalMethod.Name == nameof(PlayerCharacter.OnDie));
    }
}
