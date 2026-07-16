using System;
using System.Collections.Generic;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace DaveTheDiverMP;

internal sealed class WorldStateReplicator
{
    private readonly ManualLogSource _log;
    private readonly Dictionary<string, WorldFlagState> _hostStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, uint> _clientRevisions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _pendingClientStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _clientOriginals = new(StringComparer.Ordinal);
    private float _nextScan;
    private bool _wasConnected;

    internal WorldStateReplicator(ManualLogSource log) => _log = log;

    internal void Update(SessionRole role, UdpSession session, float now)
    {
        var connected = session != null && session.Connected;
        if (!connected)
        {
            _wasConnected = false;
            return;
        }
        if (!_wasConnected)
        {
            _hostStates.Clear();
            _clientRevisions.Clear();
            _nextScan = 0f;
        }
        _wasConnected = true;

        if (role == SessionRole.Host)
        {
            while (session.TryTakeWorldFlagRequest(out var request))
                ApplyHostRequest(request);
            if (now >= _nextScan)
            {
                _nextScan = now + 1f;
                ScanHost(session);
            }
            return;
        }
        if (role != SessionRole.Client)
            return;

        while (session.TryTakeWorldFlagState(out var state))
        {
            _clientRevisions.TryGetValue(state.Key, out var previous);
            if (!IsNewer(state.Revision, previous))
                continue;
            _clientRevisions[state.Key] = state.Revision;
            _pendingClientStates[state.Key] = state.Value;
        }
        ApplyPendingClientStates();
    }

    internal bool AllowLocalSave(
        SessionRole role,
        UdpSession session,
        PuzzleStateSaveObject saveObject,
        bool value)
    {
        if (role != SessionRole.Client || session == null || !session.Connected)
            return true;
        var key = saveObject?._key;
        if (!string.IsNullOrWhiteSpace(key))
            session.SendWorldFlagRequest(new WorldFlagRequest(key, value));
        return false;
    }

    internal void ObserveHostSave(
        SessionRole role,
        UdpSession session,
        PuzzleStateSaveObject saveObject,
        bool value)
    {
        if (role != SessionRole.Host || session == null || !session.Connected)
            return;
        var key = saveObject?._key;
        if (!string.IsNullOrWhiteSpace(key))
            Publish(session, key, value);
    }

    internal void Clear()
    {
        RestoreClientStates();
        _hostStates.Clear();
        _clientRevisions.Clear();
        _pendingClientStates.Clear();
        _nextScan = 0f;
        _wasConnected = false;
    }

    private void ApplyHostRequest(WorldFlagRequest request)
    {
        foreach (var saveObject in UnityEngine.Object.FindObjectsByType<PuzzleStateSaveObject>(
                     FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (saveObject != null && string.Equals(saveObject._key, request.Key, StringComparison.Ordinal))
            {
                saveObject.Save(request.Value);
                return;
            }
        }
        _log.LogWarning($"World flag request has no active object: {request.Key}");
    }

    private void ScanHost(UdpSession session)
    {
        foreach (var saveObject in UnityEngine.Object.FindObjectsByType<PuzzleStateSaveObject>(
                     FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (saveObject == null || string.IsNullOrWhiteSpace(saveObject._key))
                continue;
            try
            {
                var data = saveObject.GetSaveData();
                if (data != null && data._keyToSolves.TryGetValue(saveObject._key, out var value))
                    Publish(session, saveObject._key, value);
            }
            catch (Exception exception)
            {
                _log.LogWarning($"World flag read failed for {saveObject._key}: {exception.Message}");
            }
        }
    }

    private void Publish(UdpSession session, string key, bool value)
    {
        _hostStates.TryGetValue(key, out var previous);
        if (previous.Revision != 0 && previous.Value == value)
            return;
        var state = new WorldFlagState(NextRevision(previous.Revision), key, value);
        if (session.SendWorldFlagState(state))
            _hostStates[key] = state;
    }

    private void ApplyPendingClientStates()
    {
        if (_pendingClientStates.Count == 0)
            return;
        var applied = new List<string>();
        foreach (var saveObject in UnityEngine.Object.FindObjectsByType<PuzzleStateSaveObject>(
                     FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (saveObject == null || !_pendingClientStates.TryGetValue(saveObject._key, out var value))
                continue;
            if (!_clientOriginals.ContainsKey(saveObject._key))
            {
                var data = saveObject.GetSaveData();
                _clientOriginals[saveObject._key] = data != null &&
                    data._keyToSolves.TryGetValue(saveObject._key, out var original) && original;
            }
            saveObject._onLoad?.Invoke(value);
            if (value)
                saveObject._onLoadSolved?.Invoke();
            else
                saveObject._onLoadNotSolved?.Invoke();
            applied.Add(saveObject._key);
        }
        foreach (var key in applied)
            _pendingClientStates.Remove(key);
    }

    private void RestoreClientStates()
    {
        if (_clientOriginals.Count == 0)
            return;
        foreach (var saveObject in UnityEngine.Object.FindObjectsByType<PuzzleStateSaveObject>(
                     FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (saveObject == null || !_clientOriginals.TryGetValue(saveObject._key, out var value))
                continue;
            saveObject._onLoad?.Invoke(value);
            if (value)
                saveObject._onLoadSolved?.Invoke();
            else
                saveObject._onLoadNotSolved?.Invoke();
        }
        _clientOriginals.Clear();
    }

    private static uint NextRevision(uint value) => value == uint.MaxValue ? 1 : value + 1;

    private static bool IsNewer(uint revision, uint previous) =>
        unchecked((int)(revision - previous)) > 0;
}

[HarmonyPatch(typeof(PuzzleStateSaveObject), nameof(PuzzleStateSaveObject.Save))]
internal static class PuzzleStateSavePatch
{
    private static bool Prefix(PuzzleStateSaveObject __instance, bool solve, out bool __state)
    {
        var behaviour = ProbeBehaviour.Instance;
        __state = behaviour?.AllowPuzzleSave(__instance, solve) ?? true;
        return __state;
    }

    private static void Postfix(PuzzleStateSaveObject __instance, bool solve, bool __state)
    {
        if (__state)
            ProbeBehaviour.Instance?.OnPuzzleSaved(__instance, solve);
    }
}
