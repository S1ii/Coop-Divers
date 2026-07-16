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
    private const float DiverCenterBelowWaterline = 1.4f;
    private readonly ManualLogSource _log;
    private uint _localRevision = 1;
    private uint _remoteRevision;
    private uint _stateRevision;
    private uint _lastStateRevision;
    private bool _hostReady;
    private bool _clientReady;
    private bool _starting;
    private bool _clientNativeStartRequested;
    private bool _allowNativeStart;
    private bool _allowNativeExit;
    private bool _hostDead;
    private bool _clientDead;
    private bool _localSpectating;
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

    internal DiveCoordinator(ManualLogSource log) => _log = log;

    internal static void SelfTest()
    {
        var revisions = new DiveCoordinator(null)
        {
            _localRevision = 42,
            _remoteRevision = 42,
            _stateRevision = 42,
            _lastStateRevision = 42,
            _lifeRevision = 42,
            _remoteLifeRevision = 42,
            _exitRevision = 42,
            _remoteExitRevision = 42
        };
        revisions.Reset();
        if (revisions._localRevision != 1 || revisions._remoteRevision != 0 ||
            revisions._stateRevision != 0 || revisions._lastStateRevision != 0 ||
            revisions._lifeRevision != 1 || revisions._remoteLifeRevision != 0 ||
            revisions._exitRevision != 1 || revisions._remoteExitRevision != 0 ||
            !IsNewer(1, revisions._remoteRevision) ||
            NextRevision(uint.MaxValue) != 1 || !IsNewer(1, uint.MaxValue))
            throw new InvalidOperationException("Dive revision reset/sequence failed");
        if (HasRequiredExitConfirmations(true, false, true, false) ||
            !HasRequiredExitConfirmations(true, false, false, true) ||
            !HasRequiredExitConfirmations(false, true, true, false) ||
            HasRequiredExitConfirmations(false, true, false, true) ||
            !ShouldStartClientDive(true, true, false) ||
            ShouldStartClientDive(false, true, false) ||
            ShouldStartClientDive(true, true, true) ||
            ShouldAllowEscapePodInteraction(SessionRole.Client, true) ||
            !ShouldAllowEscapePodInteraction(SessionRole.Host, true) ||
            !ShouldAllowEscapePodInteraction(SessionRole.Client, false) ||
            BuildLoadingText(0) != "Загрузка." ||
            BuildLoadingText(1) != "Загрузка.." ||
            BuildLoadingText(2) != "Загрузка..." ||
            IsPartyWipe(true, false) || IsPartyWipe(false, true) ||
            !IsPartyWipe(true, true))
            throw new InvalidOperationException("Dive exit death confirmation failed");
    }

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

    internal void Update(
        SessionRole role,
        UdpSession session,
        float now,
        PlayerCharacter player,
        Transform remoteAvatar)
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
                    TryStartHostExit(null);
                }
            }
            TryStartHostDive();
            if (_hostDead)
                UpdateSpectator(remoteAvatar, "host camera follows client after death");
            RefreshPrompt(session, now);
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
                FishSpawnSeedCoordinator.SetSeed(new SceneSeed(state.SceneId, state.Seed));
                _log.LogInfo($"Dive: host={_hostReady}; client={_clientReady}");
            }
            while (session.TryTakeDiverLifeState(out var life))
            {
                if (!IsNewer(life.Revision, _remoteLifeRevision))
                    continue;
                _remoteLifeRevision = life.Revision;
                _hostDead = life.IsDead;
            }
            while (session.TryTakeDiveExitRequest(out _))
            {
            }
            TryStartClientDive();
            if (_clientDead)
                UpdateSpectator(remoteAvatar, "client camera follows host after death");
            else
                EnsureClientExitPrompt(player);
        }
        RefreshPrompt(session, now);
    }

    internal void ObserveDivePanel(LobbyStartGamePanelUI panel)
    {
        if (panel != null)
            _panel = panel;
    }

    internal void Reset()
    {
        RestorePrompt();
        (_localRevision, _remoteRevision, _stateRevision, _lastStateRevision) = (1, 0, 0, 0);
        (_hostReady, _clientReady, _starting, _clientNativeStartRequested,
            _allowNativeStart, _allowNativeExit) =
            (false, false, false, false, false, false);
        (_hostDead, _clientDead) = (false, false);
        _localSpectating = false;
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

    private void TryStartClientDive()
    {
        if (!ShouldStartClientDive(_hostReady, _clientReady, _clientNativeStartRequested) ||
            _panel == null || _startParameter == null)
            return;

        _clientNativeStartRequested = true;
        _allowNativeStart = true;
        var probe = ProbeBehaviour.Instance;
        probe?.BeginClientNativeDiveTransition(_startParameter.StartSceneName);
        _log.LogInfo("Dive: both players ready; client starts native dive");
        try
        {
            _panel.StartGame(_startParameter);
        }
        catch
        {
            probe?.CancelClientNativeDiveTransition();
            throw;
        }
    }

    private void Publish(UdpSession session)
    {
        _stateRevision = NextRevision(_stateRevision);
        var targetScene = _startParameter?.StartSceneName;
        var seed = !string.IsNullOrWhiteSpace(targetScene)
            ? FishSpawnSeedCoordinator.GetOrCreate(Protocol.SceneId(targetScene))
            : new SceneSeed(1, 1);
        session.SendDiveState(new DiveState(
            _stateRevision, _hostReady, _clientReady, seed.SceneId, seed.Seed));
    }

    internal bool RequestExit(
        SessionRole role,
        UdpSession session,
        float now,
        PlayerCharacter player,
        SceneExitTrigger trigger)
    {
        if (_allowNativeExit || session == null || !session.Connected)
            return true;
        if (role == SessionRole.Client && _clientDead)
        {
            _log.LogInfo("Dive: dead client remains in spectator mode");
            return false;
        }
        if (player == null)
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
        TryStartHostExit(trigger);
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
        if (_allowNativeExit || session == null || !session.Connected)
            return true;
        if (role == SessionRole.Client && _clientDead)
        {
            _log.LogInfo("Dive: dead client blocked native lobby return");
            return false;
        }
        if (manager == null)
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
        if (_hostDead || playerDead)
        {
            _log.LogInfo("Dive: dead host remains in spectator mode; waiting for client exit");
            TryStartHostExit(null);
            return false;
        }
        _hostExitReady = true;
        TryStartHostExit(null);
        return false;
    }

    internal bool RequestEscapePod(SessionRole role, UdpSession session, PlayerCharacter player)
    {
        if (ShouldAllowEscapePodInteraction(role, session != null && session.Connected))
            return true;
        RequestExit(role, session, Time.realtimeSinceStartup, player, null);
        _log.LogInfo("Dive: client requested escape pod; native side effects deferred to host exit");
        return false;
    }

    internal void ReportLocalLife(SessionRole role, UdpSession session, bool dead)
    {
        if (session == null || !session.Connected)
            return;
        if (role is not (SessionRole.Host or SessionRole.Client))
            return;
        ref var localDead = ref (role == SessionRole.Host ? ref _hostDead : ref _clientDead);
        if (localDead == dead)
            return;
        localDead = dead;
        if (!dead)
            _localSpectating = false;
        _lifeRevision = NextRevision(_lifeRevision);
        session.SendDiverLifeState(new DiverLifeState(_lifeRevision, dead));
        _log.LogInfo($"Dive: {(role == SessionRole.Host ? "host" : "client")} " +
            (dead ? "reported death" : "reported revive"));
    }

    private void TryStartHostExit(SceneExitTrigger trigger)
    {
        if (!CanExit())
        {
            _log.LogInfo(
                $"Dive: exit waiting; host={_hostExitReady}; client={_clientExitReady}; " +
                "all living divers must confirm");
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
                ? "Dive: exit allowed; living diver confirmed"
                : "Dive: exit allowed; both divers confirmed");
            ProbeBehaviour.Instance?.PrepareDiveExitResult();
            if (_exitManager != null)
                _exitManager.GoToLobby(_exitColor, IsPartyWipe(_hostDead, _clientDead));
            else
                trigger.OnOK();
        }
        finally
        {
            _allowNativeExit = false;
        }
    }

    private bool CanExit()
    {
        if (!HasRequiredExitConfirmations(
                _hostDead, _clientDead, _hostExitReady, _clientExitReady))
            return false;
        return true;
    }

    private static bool HasRequiredExitConfirmations(
        bool hostDead,
        bool clientDead,
        bool hostExitReady,
        bool clientExitReady)
    {
        if (!hostDead && !clientDead)
            return hostExitReady && clientExitReady;
        if (hostDead && clientDead)
            return hostExitReady || clientExitReady;
        return hostDead ? clientExitReady : hostExitReady;
    }

    private static bool ShouldStartClientDive(bool hostReady, bool clientReady, bool alreadyStarted) =>
        hostReady && clientReady && !alreadyStarted;

    private static bool ShouldAllowEscapePodInteraction(SessionRole role, bool connected) =>
        role != SessionRole.Client || !connected;

    private static bool IsPartyWipe(bool hostDead, bool clientDead) => hostDead && clientDead;

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
        var trigger = FindLoadedExitTrigger();
        if (trigger != null)
            trigger.Exit();
        else
            _log.LogWarning("Dive: native return-to-lobby trigger is not loaded");
    }

    internal bool HostDead => _hostDead;
    internal bool ClientDead => _clientDead;
    internal bool AnyPlayerDead => _hostDead || _clientDead;

    private void UpdateSpectator(Transform remoteAvatar, string message)
    {
        if (_localSpectating || remoteAvatar == null)
            return;
        var camera = CameraManager.Instance;
        if (camera == null)
            return;
        camera.ChangeTarget(remoteAvatar);
        _localSpectating = true;
        _log.LogInfo($"Dive: {message}");
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

    private void RefreshPrompt(UdpSession session, float now)
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
            ? _starting || _clientNativeStartRequested
                ? BuildLoadingText((int)(now / 0.35f) % 3)
                : $"{_nativeButtonText}? {ReadyPlayers}/{RequiredPlayers}"
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

    private static string BuildLoadingText(int frame) => "Загрузка" + new string('.', frame % 3 + 1);

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

[HarmonyPatch(typeof(Interaction.Escape.EscapePodZone), nameof(Interaction.Escape.EscapePodZone.SuccessInteract))]
internal static class EscapePodExitPatch
{
    private static bool Prefix(BaseCharacter __0) =>
        ProbeBehaviour.Instance?.RequestEscapePod(__0 as PlayerCharacter) ?? true;
}

[HarmonyPatch(typeof(JDLC.EscapeBellZone), nameof(JDLC.EscapeBellZone.SuccessInteract))]
internal static class EscapeBellExitPatch
{
    private static bool Prefix(BaseCharacter __0) =>
        ProbeBehaviour.Instance?.RequestEscapePod(__0 as PlayerCharacter) ?? true;
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

    private static void Prefix(MethodBase __originalMethod)
    {
        if (__originalMethod.Name == nameof(PlayerCharacter.OnDie))
            ProbeBehaviour.Instance?.ReportDiveLife(true);
    }

    private static void Postfix(MethodBase __originalMethod)
    {
        if (__originalMethod.Name == nameof(PlayerCharacter.OnRevive))
            ProbeBehaviour.Instance?.ReportDiveLife(false);
    }
}
