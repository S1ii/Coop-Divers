using System;
using System.Collections.Generic;
using BepInEx.Logging;
using HarmonyLib;

namespace DaveTheDiverMP;

internal sealed class IngredientsReplicator
{
    private const int PlaceCount = (int)SushiBar.Place.Max;
    private const int MaxMirrorEntries = 20_000;
    private const int MaxPendingHostDeltas = 256;
    private readonly ManualLogSource _log;
    private readonly Dictionary<long, int> _hostCounts = new();
    private readonly Dictionary<long, int> _clientCounts = new();
    private readonly Dictionary<ushort, IngredientCount[]> _snapshotChunks = new();
    private readonly List<IngredientsDelta> _deferredDeltas = new();
    private readonly Queue<IngredientsDelta> _pendingHostDeltas = new();
    private SessionRole _role;
    private bool _wasConnected;
    private bool _hostReady;
    private bool _dirty = true;
    private bool _clientSynchronized;
    private bool _storageErrorLogged;
    private ulong _hostEpoch;
    private uint _hostRevision;
    private ulong _clientRequestId;
    private ulong _pendingHostRequestId;
    private ulong _snapshotEpoch;
    private uint _snapshotRevision;
    private ushort _snapshotChunkCount;
    private int _snapshotEntryCount;
    private float _nextHostScan;
    private float _clientSyncDeadline;

    internal IngredientsReplicator(ManualLogSource log) => _log = log;

    internal string Status
    {
        get
        {
            if (_role == SessionRole.Host)
                return _hostReady ? $"shared catch: {_hostCounts.Count} stacks" : "shared catch: loading";
            if (_role == SessionRole.Client)
                return _clientSynchronized
                    ? $"catch mirror: synced ({_clientCounts.Count} stacks)"
                    : "catch mirror: syncing";
            return "shared catch: offline";
        }
    }

    internal void MarkDirty() => _dirty = true;

    internal bool TryGetClientCount(int ingredientId, int place, out int count)
    {
        count = 0;
        if (!_clientSynchronized)
            return false;
        _clientCounts.TryGetValue(Key(ingredientId, place), out count);
        return true;
    }

    internal void Update(SessionRole role, UdpSession session, float now)
    {
        if (_role != role)
            ResetForRole(role);

        var connected = session != null && session.Connected;
        if (!connected)
        {
            if (_wasConnected && role == SessionRole.Client)
                ResetClientConnection();
            if (_wasConnected && role == SessionRole.Host)
                ResetHostConnection();
            _wasConnected = false;
            return;
        }

        if (ShouldRequestInitialClientSnapshot(role, connected, _wasConnected))
            RequestClientSnapshot(session, now);
        _wasConnected = true;

        if (role == SessionRole.Host)
            UpdateHost(session, now);
        else if (role == SessionRole.Client)
            UpdateClient(session, now);
    }

    internal void Clear() => ResetForRole(SessionRole.Offline);

    internal static void SelfTest()
    {
        if (!ShouldRequestInitialClientSnapshot(SessionRole.Client, true, false) ||
            ShouldRequestInitialClientSnapshot(SessionRole.Client, true, true) ||
            ShouldRequestInitialClientSnapshot(SessionRole.Client, false, false) ||
            ShouldRequestInitialClientSnapshot(SessionRole.Host, true, false))
            throw new InvalidOperationException("Ingredients initial snapshot self-test failed");
    }

    private static bool ShouldRequestInitialClientSnapshot(
        SessionRole role, bool connected, bool wasConnected) =>
        role == SessionRole.Client && connected && !wasConnected;

    private void ResetForRole(SessionRole role)
    {
        _role = role;
        _wasConnected = false;
        _hostReady = false;
        _dirty = true;
        _clientSynchronized = false;
        _storageErrorLogged = false;
        _hostEpoch = role == SessionRole.Host ? NewToken() : 0;
        _hostRevision = 1;
        _clientRequestId = 0;
        _nextHostScan = 0f;
        _clientSyncDeadline = 0f;
        _hostCounts.Clear();
        _clientCounts.Clear();
        _deferredDeltas.Clear();
        ResetHostConnection();
        ResetSnapshotAssembly();
    }

    private void UpdateHost(UdpSession session, float now)
    {
        while (session.TryTakeIngredientsSyncRequest(out var request))
            _pendingHostRequestId = request.RequestId;

        List<IngredientsDelta> deltas = null;
        if (_dirty || now >= _nextHostScan)
        {
            _nextHostScan = now + 1f;
            deltas = ScanHostStorage();
        }
        if (deltas != null)
        {
            foreach (var delta in deltas)
            {
                if (_pendingHostDeltas.Count >= MaxPendingHostDeltas)
                {
                    _pendingHostDeltas.Clear();
                    _log.LogWarning("Ingredients delta backlog reset; client will resnapshot");
                }
                _pendingHostDeltas.Enqueue(delta);
            }
        }

        if (_pendingHostRequestId != 0)
        {
            if (!_hostReady)
                return;
            var chunkCount = GetSnapshotChunkCount();
            if (session.ReliableCapacityRemaining >= chunkCount)
            {
                var requestId = _pendingHostRequestId;
                _pendingHostRequestId = 0;
                SendSnapshot(session, requestId, chunkCount);
            }
            else
            {
                // Let acknowledgements drain the reliable queue instead of
                // filling each newly freed slot with another delta.
                return;
            }
        }

        while (_pendingHostDeltas.Count > 0 && session.ReliableCapacityRemaining > 0)
            session.SendIngredientsDelta(_pendingHostDeltas.Dequeue());
    }

    private List<IngredientsDelta> ScanHostStorage()
    {
        if (!TryReadStorage(out var current))
            return null;

        _dirty = false;
        if (!_hostReady)
        {
            Replace(_hostCounts, current);
            _hostReady = true;
            return null;
        }

        var changes = new List<IngredientCount>();
        foreach (var pair in current)
        {
            if (!_hostCounts.TryGetValue(pair.Key, out var oldCount) || oldCount != pair.Value)
                changes.Add(Entry(pair.Key, pair.Value));
        }
        foreach (var pair in _hostCounts)
        {
            if (!current.ContainsKey(pair.Key))
                changes.Add(Entry(pair.Key, 0));
        }
        if (changes.Count == 0)
            return null;

        changes.Sort(CompareEntries);
        Replace(_hostCounts, current);
        var deltas = new List<IngredientsDelta>();
        for (var offset = 0; offset < changes.Count; offset += Protocol.MaxIngredientEntriesPerPacket)
        {
            var count = Math.Min(Protocol.MaxIngredientEntriesPerPacket, changes.Count - offset);
            var entries = new IngredientCount[count];
            changes.CopyTo(offset, entries, 0, count);
            var baseRevision = _hostRevision;
            _hostRevision++;
            deltas.Add(new IngredientsDelta(_hostEpoch, baseRevision, _hostRevision, entries));
        }
        return deltas;
    }

    private bool TryReadStorage(out Dictionary<long, int> current)
    {
        current = new Dictionary<long, int>();
        try
        {
            if (!IngredientsStorage.hasInstance)
                return false;
            var storage = IngredientsStorage.Instance;
            if (storage == null || !storage.m_IsLoaded || storage.m_Storage == null)
                return false;

            foreach (var pair in storage.m_Storage)
            {
                if (pair.Key <= 0 || pair.Value == null || pair.Value.counts == null)
                    continue;
                var places = Math.Min(
                    Math.Min(PlaceCount, Protocol.MaxIngredientPlaces),
                    pair.Value.counts.Length);
                for (var place = 0; place < places; place++)
                {
                    var count = pair.Value.counts[place];
                    if (count > 0)
                        current[Key(pair.Key, place)] = count;
                }
            }
            if (current.Count > MaxMirrorEntries)
            {
                _log.LogError($"Ingredients mirror rejected {current.Count} entries");
                return false;
            }
            _storageErrorLogged = false;
            return true;
        }
        catch (Exception exception)
        {
            if (!_storageErrorLogged)
            {
                _storageErrorLogged = true;
                _log.LogWarning($"Ingredients scan failed: {exception.Message}");
            }
            return false;
        }
    }

    private int GetSnapshotChunkCount() => Math.Max(1,
        (_hostCounts.Count + Protocol.MaxIngredientEntriesPerPacket - 1) /
        Protocol.MaxIngredientEntriesPerPacket);

    private void SendSnapshot(UdpSession session, ulong requestId, int chunkCount)
    {
        var entries = new List<IngredientCount>(_hostCounts.Count);
        foreach (var pair in _hostCounts)
            entries.Add(Entry(pair.Key, pair.Value));
        entries.Sort(CompareEntries);
        if (chunkCount > Protocol.MaxIngredientSnapshotChunks)
        {
            _log.LogError($"Ingredients snapshot rejected {entries.Count} entries");
            return;
        }

        for (var chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
        {
            var offset = chunkIndex * Protocol.MaxIngredientEntriesPerPacket;
            var count = Math.Min(Protocol.MaxIngredientEntriesPerPacket, entries.Count - offset);
            var chunkEntries = new IngredientCount[count];
            if (count > 0)
                entries.CopyTo(offset, chunkEntries, 0, count);
            session.SendIngredientsSnapshotChunk(new IngredientsSnapshotChunk(
                requestId,
                _hostEpoch,
                _hostRevision,
                (ushort)chunkIndex,
                (ushort)chunkCount,
                chunkEntries));
        }
        _log.LogInfo($"Ingredients snapshot r{_hostRevision}: {entries.Count} entries");
    }

    private void UpdateClient(UdpSession session, float now)
    {
        if (session.TryConsumeIngredientsDeltaResync())
            RequestClientSnapshot(session, now);
        if (_clientRequestId != 0 && now >= _clientSyncDeadline)
            RequestClientSnapshot(session, now);

        while (session.TryTakeIngredientsSnapshotChunk(out var chunk))
            AcceptSnapshotChunk(session, chunk, now);

        while (session.TryTakeIngredientsDelta(out var delta))
        {
            if (!_clientSynchronized)
            {
                EnsureClientSnapshotRequested(session, now);
                if (_deferredDeltas.Count < 256)
                    _deferredDeltas.Add(delta);
                else
                    RequestClientSnapshot(session, now);
                continue;
            }
            if (delta.HostEpoch != _snapshotEpoch)
            {
                RequestClientSnapshot(session, now);
                continue;
            }
            if (delta.BaseRevision != _snapshotRevision)
            {
                // A snapshot can overtake deltas that were already queued on the host.
                // They are harmless once their revision is included in that snapshot.
                if (!IsNewer(delta.Revision, _snapshotRevision))
                    continue;
                RequestClientSnapshot(session, now);
                continue;
            }
            ApplyDelta(delta);
        }
    }

    private void AcceptSnapshotChunk(
        UdpSession session,
        IngredientsSnapshotChunk chunk,
        float now)
    {
        if (chunk.RequestId != _clientRequestId || _clientRequestId == 0)
            return;
        if (_snapshotChunkCount == 0)
        {
            _snapshotEpoch = chunk.HostEpoch;
            _snapshotRevision = chunk.Revision;
            _snapshotChunkCount = chunk.ChunkCount;
        }
        else if (_snapshotEpoch != chunk.HostEpoch || _snapshotRevision != chunk.Revision ||
            _snapshotChunkCount != chunk.ChunkCount)
        {
            RequestClientSnapshot(session, now);
            return;
        }

        if (_snapshotChunks.ContainsKey(chunk.ChunkIndex))
            return;
        _clientSyncDeadline = now + 10f;
        _snapshotEntryCount += chunk.Entries.Length;
        if (_snapshotEntryCount > MaxMirrorEntries)
        {
            RequestClientSnapshot(session, now);
            return;
        }
        _snapshotChunks.Add(chunk.ChunkIndex, chunk.Entries);
        if (_snapshotChunks.Count != _snapshotChunkCount)
            return;

        var replacement = new Dictionary<long, int>(_snapshotEntryCount);
        for (ushort index = 0; index < _snapshotChunkCount; index++)
        {
            if (!_snapshotChunks.TryGetValue(index, out var entries))
                return;
            foreach (var entry in entries)
            {
                var key = Key(entry.IngredientId, entry.Place);
                if (!replacement.TryAdd(key, entry.Count))
                {
                    RequestClientSnapshot(session, now);
                    return;
                }
            }
        }

        _clientCounts.Clear();
        foreach (var pair in replacement)
        {
            if (pair.Value > 0)
                _clientCounts.Add(pair.Key, pair.Value);
        }
        _clientSynchronized = true;
        _clientRequestId = 0;
        _clientSyncDeadline = 0f;
        ResetSnapshotAssembly(false);
        if (!ApplyDeferredDeltas())
        {
            RequestClientSnapshot(session, now);
            return;
        }
        _log.LogInfo($"Ingredients synchronized r{_snapshotRevision}: {_clientCounts.Count} entries");
    }

    private bool ApplyDeferredDeltas()
    {
        while (_deferredDeltas.Count > 0)
        {
            var applied = false;
            for (var index = _deferredDeltas.Count - 1; index >= 0; index--)
            {
                var delta = _deferredDeltas[index];
                if (delta.HostEpoch != _snapshotEpoch)
                    return false;
                if (delta.BaseRevision == _snapshotRevision)
                {
                    ApplyDelta(delta);
                    _deferredDeltas.RemoveAt(index);
                    applied = true;
                    break;
                }
                if (!IsNewer(delta.Revision, _snapshotRevision))
                    _deferredDeltas.RemoveAt(index);
            }
            if (!applied && _deferredDeltas.Count > 0)
                return false;
        }
        return true;
    }

    private void ApplyDelta(IngredientsDelta delta)
    {
        foreach (var entry in delta.Entries)
        {
            var key = Key(entry.IngredientId, entry.Place);
            if (entry.Count == 0)
                _clientCounts.Remove(key);
            else
                _clientCounts[key] = entry.Count;
        }
        _snapshotRevision = delta.Revision;
    }

    private void EnsureClientSnapshotRequested(UdpSession session, float now)
    {
        if (_clientRequestId == 0)
            RequestClientSnapshot(session, now);
    }

    private void RequestClientSnapshot(UdpSession session, float now)
    {
        _clientSynchronized = false;
        _clientRequestId = NewToken();
        _clientSyncDeadline = now + 10f;
        _deferredDeltas.Clear();
        ResetSnapshotAssembly();
        session.SendIngredientsSyncRequest(new IngredientsSyncRequest(_clientRequestId));
    }

    private void ResetClientConnection()
    {
        _clientSynchronized = false;
        _clientRequestId = 0;
        _clientSyncDeadline = 0f;
        _deferredDeltas.Clear();
        ResetSnapshotAssembly();
    }

    private void ResetHostConnection()
    {
        _pendingHostRequestId = 0;
        _pendingHostDeltas.Clear();
    }

    private void ResetSnapshotAssembly(bool resetVersion = true)
    {
        _snapshotChunks.Clear();
        _snapshotChunkCount = 0;
        _snapshotEntryCount = 0;
        if (resetVersion)
        {
            _snapshotEpoch = 0;
            _snapshotRevision = 0;
        }
    }

    private static void Replace(Dictionary<long, int> target, Dictionary<long, int> source)
    {
        target.Clear();
        foreach (var pair in source)
            target.Add(pair.Key, pair.Value);
    }

    private static long Key(int ingredientId, int place) =>
        ((long)ingredientId << 32) | (uint)place;

    private static IngredientCount Entry(long key, int count) =>
        new((int)(key >> 32), (int)(uint)key, count);

    private static int CompareEntries(IngredientCount left, IngredientCount right)
    {
        var ingredient = left.IngredientId.CompareTo(right.IngredientId);
        return ingredient != 0 ? ingredient : left.Place.CompareTo(right.Place);
    }

    private static bool IsNewer(uint value, uint previous) =>
        unchecked((int)(value - previous)) > 0;

    private static ulong NewToken()
    {
        var token = BitConverter.ToUInt64(Guid.NewGuid().ToByteArray(), 0);
        return token == 0 ? 1UL : token;
    }
}

internal static class IngredientsPatchBridge
{
    internal static void Changed() => ProbeBehaviour.Instance?.OnIngredientsChanged();
}

[HarmonyPatch(typeof(IngredientsStorage), nameof(IngredientsStorage.Reset))]
internal static class IngredientsResetPatch
{
    private static void Postfix() => IngredientsPatchBridge.Changed();
}

[HarmonyPatch(typeof(IngredientsStorage), nameof(IngredientsStorage.Clear))]
internal static class IngredientsClearPatch
{
    private static void Postfix() => IngredientsPatchBridge.Changed();
}

[HarmonyPatch(typeof(IngredientsStorage), nameof(IngredientsStorage.AddIngredients),
    new[] { typeof(int), typeof(int), typeof(SushiBar.Place) })]
internal static class IngredientsAddPatch
{
    private static void Postfix() => IngredientsPatchBridge.Changed();
}

[HarmonyPatch(typeof(IngredientsStorage), nameof(IngredientsStorage.MoveBranch),
    new[] { typeof(int), typeof(int), typeof(SushiBar.Place), typeof(SushiBar.Place) })]
internal static class IngredientsMovePatch
{
    private static void Postfix() => IngredientsPatchBridge.Changed();
}

[HarmonyPatch(typeof(IngredientsStorage), nameof(IngredientsStorage.Decrease),
    new[] { typeof(int), typeof(SushiBar.Place), typeof(int), typeof(bool) })]
internal static class IngredientsDecreasePlacePatch
{
    private static void Postfix() => IngredientsPatchBridge.Changed();
}

[HarmonyPatch(typeof(IngredientsStorage), nameof(IngredientsStorage.Decrease),
    new[] { typeof(int), typeof(int), typeof(bool) })]
internal static class IngredientsDecreasePatch
{
    private static void Postfix() => IngredientsPatchBridge.Changed();
}
