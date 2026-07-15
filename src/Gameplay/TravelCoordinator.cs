using System;
using System.Collections.Generic;
using BepInEx.Logging;
using Common.Contents;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace DaveTheDiverMP;

internal sealed class TravelCoordinator
{
    private readonly ManualLogSource _log;
    private TravelTarget? _target;
    private uint _localRevision = 1;
    private uint _remoteRevision;
    private uint _stateRevision;
    private uint _lastStateRevision;
    private bool _hostReady;
    private bool _clientReady;
    private bool _clientNativeStarted;
    private bool _allowNative;
    private bool _allowClientTransition;
    private bool _starting;
    private Action _hostAction;
    private Action _clientAction;
    private MoveSceneElement _sushiElement;
    private string _sushiText;
    private string _sushiOverrideText;
    private readonly Dictionary<TMP_Text, string> _sushiLabels = new();
    private readonly Dictionary<UnityEngine.UI.Text, string> _legacySushiLabels = new();

    internal TravelCoordinator(ManualLogSource log) => _log = log;

    internal bool Request(
        SessionRole role,
        UdpSession session,
        TravelTarget target,
        Action hostAction,
        MoveSceneElement sushiElement = null)
    {
        if (_allowNative)
            return true;
        if (_starting)
            return false;
        if (session == null || !session.Connected)
            return true;

        Select(target);
        if (sushiElement != null)
            ObserveSushiElement(sushiElement);

        if (role == SessionRole.Host)
        {
            _hostReady = true;
            _hostAction ??= hostAction;
            Publish(session);
            TryStart();
        }
        else if (role == SessionRole.Client)
        {
            _clientReady = true;
            _clientAction ??= hostAction;
            _localRevision = NextRevision(_localRevision);
            session.SendTravelReady(new TravelReady(target, _localRevision, true, false));
        }
        RefreshText();
        _log.LogInfo($"Travel {target}: host={_hostReady}; client={_clientReady}");
        return false;
    }

    internal void Update(SessionRole role, UdpSession session)
    {
        if (session == null || !session.Connected)
        {
            Reset();
            return;
        }

        if (_sushiElement == null)
        {
            foreach (var element in UnityEngine.Object.FindObjectsByType<MoveSceneElement>(FindObjectsSortMode.None))
            {
                if (element != null && element.Scene == MoveSceneElement.SceneName.SuShi)
                {
                    ObserveSushiElement(element);
                    break;
                }
            }
        }

        if (role == SessionRole.Host)
        {
            var changed = false;
            while (session.TryTakeTravelReady(out var ready))
            {
                if (!IsNewer(ready.Revision, _remoteRevision))
                    continue;
                _remoteRevision = ready.Revision;
                if (_target != ready.Target)
                {
                    Select(ready.Target);
                    changed = true;
                }
                changed |= _clientReady != ready.Ready;
                _clientReady = ready.Ready;
                changed |= _clientNativeStarted != ready.NativeStarted;
                _clientNativeStarted = ready.NativeStarted;
            }
            if (changed)
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
                Select(state.Target, keepClientReady: true);
                _hostReady = state.HostReady;
                _clientReady = state.ClientReady;
            }
            TryStartClient(session);
        }
        RefreshText();
    }

    internal void LateUpdate() => RefreshText();

    internal void Reset()
    {
        RestoreText();
        _target = null;
        _remoteRevision = 0;
        _lastStateRevision = 0;
        _hostReady = false;
        _clientReady = false;
        _clientNativeStarted = false;
        _allowNative = false;
        _allowClientTransition = false;
        _starting = false;
        _hostAction = null;
        _clientAction = null;
        _sushiElement = null;
        _sushiText = null;
        _sushiOverrideText = null;
        _sushiLabels.Clear();
        _legacySushiLabels.Clear();
    }

    private void Select(TravelTarget target, bool keepClientReady = false)
    {
        if (_target == target)
            return;
        RestoreText();
        _target = target;
        _hostReady = false;
        if (!keepClientReady)
            _clientReady = false;
        _clientNativeStarted = false;
        _hostAction = null;
        _clientAction = null;
    }

    private void Publish(UdpSession session)
    {
        if (!_target.HasValue)
            return;
        _stateRevision = NextRevision(_stateRevision);
        session.SendTravelState(new TravelState(
            _target.Value, _stateRevision, _hostReady, _clientReady));
    }

    private void TryStart()
    {
        if (!_hostReady || !_clientReady || !_clientNativeStarted || _hostAction == null)
            return;
        var action = _hostAction;
        _hostAction = null;
        _starting = true;
        _allowNative = true;
        try
        {
            _log.LogInfo($"Travel {_target}: both players ready; host starts native transition");
            action();
        }
        finally
        {
            _allowNative = false;
        }
    }

    internal bool AllowClientSceneTransition()
    {
        if (!_allowClientTransition)
            return false;
        _allowClientTransition = false;
        return true;
    }

    private void TryStartClient(UdpSession session)
    {
        if (!_hostReady || !_clientReady || _clientNativeStarted)
            return;

        _clientNativeStarted = true;
        _starting = true;
        _localRevision = NextRevision(_localRevision);
        session.SendTravelReady(new TravelReady(_target!.Value, _localRevision, true, true));
        if (_clientAction == null)
        {
            _log.LogWarning($"Travel {_target}: client has no native transition action; using scene fallback");
            return;
        }

        _allowNative = true;
        _allowClientTransition = true;
        try
        {
            _log.LogInfo($"Travel {_target}: client starts native transition");
            _clientAction();
        }
        finally
        {
            _allowNative = false;
        }
    }

    private void ObserveSushiElement(MoveSceneElement element)
    {
        if (_sushiElement == element)
            return;
        RestoreText();
        _sushiElement = element;
        _sushiText = element?._text?.text?.textUGUI?.text;
        _sushiOverrideText = null;
        _sushiLabels.Clear();
        _legacySushiLabels.Clear();
        var direct = element?._text?.text?.textUGUI;
        if (direct != null && !string.IsNullOrWhiteSpace(direct.text))
            _legacySushiLabels[direct] = direct.text;
        if (element != null)
        {
            foreach (var label in element.GetComponentsInChildren<TMP_Text>(true))
            {
                if (label == null || string.IsNullOrWhiteSpace(label.text) ||
                    label.text.Trim().Length <= 1)
                    continue;
                _sushiLabels.TryAdd(label, label.text);
                if (string.IsNullOrEmpty(_sushiText))
                    _sushiText = label.text;
                break;
            }
        }
    }

    private void RefreshText()
    {
        if (_sushiElement == null || string.IsNullOrEmpty(_sushiText))
            return;
        var ready = (_hostReady ? 1 : 0) + (_clientReady ? 1 : 0);
        var text = $"{_sushiText}? {ready}/2";
        var textData = _sushiElement._text;
        if (_sushiOverrideText != text && textData != null)
        {
            textData.ReleaseAllOverrideDelegates();
            textData.SetOverride((Func<string, string>)(_ => text), true);
            _sushiOverrideText = text;
        }
        foreach (var pair in _sushiLabels)
            if (pair.Key != null && pair.Key.text != text)
                pair.Key.text = text;
        foreach (var pair in _legacySushiLabels)
            if (pair.Key != null && pair.Key.text != text)
                pair.Key.text = text;
    }

    private void RestoreText()
    {
        var textData = _sushiElement?._text;
        if (textData != null && !string.IsNullOrEmpty(_sushiText))
        {
            textData.ReleaseAllOverrideDelegates();
            textData.SetOverride((Func<string, string>)(_ => _sushiText), true);
        }
        foreach (var pair in _sushiLabels)
            if (pair.Key != null)
                pair.Key.text = pair.Value;
        foreach (var pair in _legacySushiLabels)
            if (pair.Key != null)
                pair.Key.text = pair.Value;
        _sushiOverrideText = null;
    }

    private static uint NextRevision(uint revision) =>
        revision == uint.MaxValue ? 1u : revision + 1u;

    private static bool IsNewer(uint revision, uint previous) =>
        unchecked((int)(revision - previous)) > 0;
}

[HarmonyPatch(typeof(MoveSceneElement), nameof(MoveSceneElement.OnClick))]
internal static class SushiBarTravelPatch
{
    private static bool Prefix(MoveSceneElement __instance)
    {
        if (__instance == null || __instance.Scene != MoveSceneElement.SceneName.SuShi)
            return true;
        return ProbeBehaviour.Instance?.RequestSushiBarTravel(__instance) ?? true;
    }
}

[HarmonyPatch(typeof(SushiBarExitPanel), nameof(SushiBarExitPanel.OnExecute))]
internal static class SushiBarReturnPatch
{
    private static bool Prefix(SushiBarExitPanel __instance) =>
        ProbeBehaviour.Instance?.RequestSushiBarReturn(__instance) ?? true;
}
