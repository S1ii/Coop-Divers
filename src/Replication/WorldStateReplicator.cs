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
    private readonly HashSet<string> _reportedAmbiguousKeys = new(StringComparer.Ordinal);
    private float _nextScan;
    private bool _wasConnected;

    internal WorldStateReplicator(ManualLogSource log) => _log = log;

    internal static void SelfTest()
    {
        var unique = new Dictionary<string, object>(StringComparer.Ordinal);
        var ambiguous = new HashSet<string>(StringComparer.Ordinal);
        var first = new object();
        var second = new object();
        AddUniqueKey(unique, ambiguous, "puzzle-a", first);
        AddUniqueKey(unique, ambiguous, "puzzle-b", second);
        AddUniqueKey(unique, ambiguous, "puzzle-a", new object());
        AddUniqueKey(unique, ambiguous, "puzzle-a", new object());
        AddUniqueKey(unique, ambiguous, " ", new object());
        if (unique.Count != 1 || !IsUniqueOwner(unique, "puzzle-b", second) ||
            IsUniqueOwner(unique, "puzzle-b", first) ||
            !ambiguous.SetEquals(new[] { "puzzle-a" }))
            throw new InvalidOperationException("World-state unique-key policy failed");
    }

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
        if (IsUniqueOwner(FindUniqueSaveObjects(), key, saveObject))
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
        if (IsUniqueOwner(FindUniqueSaveObjects(), key, saveObject))
            Publish(session, key, value);
    }

    internal void Clear()
    {
        RestoreClientStates();
        _hostStates.Clear();
        _clientRevisions.Clear();
        _pendingClientStates.Clear();
        _reportedAmbiguousKeys.Clear();
        _nextScan = 0f;
        _wasConnected = false;
    }

    private void ApplyHostRequest(WorldFlagRequest request)
    {
        var saveObjects = FindUniqueSaveObjects();
        if (saveObjects.TryGetValue(request.Key, out var saveObject))
        {
            saveObject.Save(request.Value);
            return;
        }
        _log.LogWarning($"World flag request has no active object: {request.Key}");
    }

    private void ScanHost(UdpSession session)
    {
        foreach (var pair in FindUniqueSaveObjects())
        {
            var key = pair.Key;
            var saveObject = pair.Value;
            try
            {
                var data = saveObject.GetSaveData();
                if (data != null && data._keyToSolves.TryGetValue(key, out var value))
                    Publish(session, key, value);
            }
            catch (Exception exception)
            {
                _log.LogWarning($"World flag read failed for {key}: {exception.Message}");
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
        foreach (var pair in FindUniqueSaveObjects())
        {
            var key = pair.Key;
            var saveObject = pair.Value;
            if (!_pendingClientStates.TryGetValue(key, out var value))
                continue;
            if (!_clientOriginals.ContainsKey(key))
            {
                var data = saveObject.GetSaveData();
                _clientOriginals[key] = data != null &&
                    data._keyToSolves.TryGetValue(key, out var original) && original;
            }
            saveObject._onLoad?.Invoke(value);
            if (value)
                saveObject._onLoadSolved?.Invoke();
            else
                saveObject._onLoadNotSolved?.Invoke();
            applied.Add(key);
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

    private Dictionary<string, PuzzleStateSaveObject> FindUniqueSaveObjects()
    {
        var unique = new Dictionary<string, PuzzleStateSaveObject>(StringComparer.Ordinal);
        var ambiguous = new HashSet<string>(StringComparer.Ordinal);
        foreach (var saveObject in UnityEngine.Object.FindObjectsByType<PuzzleStateSaveObject>(
                     FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (saveObject == null)
                continue;
            var key = saveObject._key;
            var wasUnique = !string.IsNullOrWhiteSpace(key) && unique.ContainsKey(key);
            AddUniqueKey(unique, ambiguous, key, saveObject);
            if (wasUnique && ambiguous.Contains(key) && _reportedAmbiguousKeys.Add(key))
                _log.LogWarning($"World flag replication disabled for duplicate key: {key}");
        }
        return unique;
    }

    private static void AddUniqueKey<T>(
        IDictionary<string, T> unique,
        ISet<string> ambiguous,
        string key,
        T value)
    {
        if (string.IsNullOrWhiteSpace(key) || ambiguous.Contains(key))
            return;
        if (unique.ContainsKey(key))
        {
            unique.Remove(key);
            ambiguous.Add(key);
            return;
        }
        unique.Add(key, value);
    }

    private static bool IsUniqueOwner<T>(
        IReadOnlyDictionary<string, T> unique,
        string key,
        T value)
        where T : class =>
        !string.IsNullOrWhiteSpace(key) && value != null &&
        unique.TryGetValue(key, out var owner) && ReferenceEquals(owner, value);

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
