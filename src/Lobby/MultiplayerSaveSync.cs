using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BepInEx;
using BepInEx.Logging;
using DR.Save;
using UnityEngine;

namespace DaveTheDiverMP;

internal static class MultiplayerSaveSync
{
    private enum SaveFailurePoint
    {
        None,
        HostWrite,
        GameWrite,
        PhotoWrite,
        PlayerWrite,
        Verification,
        MarkerUpdate,
        Load
    }

    private const int MpSlotIndex = 6;
    private const SaveSlotType MpSlotType = SaveSlotType.Manual;
    private const uint BundleMagic = 0x42534D44; // DMSB
    private const int BundleHeaderSize = 16;
    private const int MarkerSchema = 3;
    private const int LegacyMarkerSchema = 2;
    private const int MaxBundleBytes = 16 * 1024 * 1024;
    private const int MaxMetadataBytes = 64 * 1024;
    private static readonly string MpSlotMarkerPath =
        Path.Combine(Paths.ConfigPath, "DaveTheDiverMP.mp-slot");
    private static readonly string MpSlotPendingPath = MpSlotMarkerPath + ".pending";
    private static readonly string MpSlotBackupPath = MpSlotMarkerPath + ".backup";

    private sealed class SlotMarker
    {
        public int Schema { get; set; }
        public int Slot { get; set; }
        public string MultiplayerFingerprint { get; set; } = string.Empty;
        public string PreviousFingerprint { get; set; } = string.Empty;
        public string ProfileIdentity { get; set; } = string.Empty;
        public string GameBuild { get; set; } = string.Empty;
        public string CreatedAt { get; set; } = string.Empty;

        [JsonPropertyName("Fingerprint")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string LegacyFingerprint { get; set; }
    }

    private sealed class PendingWrite
    {
        public int Schema { get; set; }
        public int Slot { get; set; }
        public bool HadSlot { get; set; }
        public string PreviousFingerprint { get; set; } = string.Empty;
        public string TargetFingerprint { get; set; } = string.Empty;
    }

    private static readonly Dictionary<ushort, byte[]> ClientChunks = new();
    private static byte[] _hostSnapshot = Array.Empty<byte>();
    private static ulong _hostConnectionId;
    private static ulong _hostTransferId;
    private static uint _hostFingerprint;
    private static int _hostNextChunk;
    private static bool _hostRemoteLoaded;
    private static ulong _clientTransferId;
    private static uint _clientFingerprint;
    private static int _clientTotalBytes;
    private static ushort _clientChunkCount;
    private static bool _clientLoaded;
    private static bool _clientRejectedTransfer;
    private static string _originalGameJson = string.Empty;
    private static string _originalPhotoJson = string.Empty;
    private static string _originalPlayerJson = string.Empty;
    private static bool _originalProfileRestoreRequired;
    private static bool _normalSaveRestartRequired;
    [ThreadStatic]
    private static int _remoteSnapshotApplyDepth;
    // Test-only; SelfTest always clears this before returning.
    private static SaveFailurePoint _selfTestFailurePoint;
    private static string _status = string.Empty;

    internal static bool HostRemoteLoaded => _hostRemoteLoaded;
    internal static bool ClientLoaded => _clientLoaded;
    internal static bool IsApplyingRemoteSnapshot => _remoteSnapshotApplyDepth > 0;
    internal static bool OriginalProfileRestoreRequired => _originalProfileRestoreRequired;
    internal static bool NormalSaveRestartRequired => _normalSaveRestartRequired;
    internal static string Status => _status;

    internal static void SelfTest()
    {
        if (!IsJsonObject("{}") || !IsJsonObject(" \r\n\uFEFF{\"SaveVersion\":\"0.0.31\"}\t") ||
            IsJsonObject(string.Empty) || IsJsonObject("<Error />") || IsJsonObject("[]"))
            throw new InvalidOperationException("MP save JSON boundary self-test failed");
        var bundle = PackBundle("{\"g\":1}", "{\"p\":2}", "{\"d\":3}");
        if (!TryUnpackBundle(bundle, out var game, out var photo, out var player) ||
            game != "{\"g\":1}" || photo != "{\"p\":2}" || player != "{\"d\":3}")
            throw new InvalidOperationException("MP save bundle round-trip self-test failed");
        bundle[4] = byte.MaxValue;
        if (TryUnpackBundle(bundle, out _, out _, out _))
            throw new InvalidOperationException("MP save bundle accepted invalid lengths");
        if (AreBundleLengthsValid(MaxBundleBytes, 1, 1) ||
            AreBundleLengthsValid(int.MaxValue, int.MaxValue, int.MaxValue))
            throw new InvalidOperationException("MP save bundle accepted oversized lengths");
        var fingerprint = FullFingerprint(PackBundle("{\"g\":1}", "{\"p\":2}", "{\"d\":3}"));
        var markerJson = JsonSerializer.Serialize(new SlotMarker
        {
            Schema = MarkerSchema,
            Slot = MpSlotIndex,
            MultiplayerFingerprint = fingerprint,
            PreviousFingerprint = new string('A', 64),
            ProfileIdentity = string.Empty,
            GameBuild = "self-test",
            CreatedAt = "2026-07-18T00:00:00.0000000+00:00"
        });
        if (!TryParseMarker(markerJson, out var marker) ||
            !MarkerOwns(marker, fingerprint) || MarkerOwns(marker, new string('0', 64)) ||
            marker.PreviousFingerprint != new string('A', 64) ||
            marker.GameBuild.Length == 0 || marker.CreatedAt.Length == 0 ||
            TryParseMarker("{\"Schema\":3,\"Slot\":6,\"MultiplayerFingerprint\":\"bad\"}", out _) ||
            !TryParseMarker($"{{\"Schema\":2,\"Slot\":6,\"Fingerprint\":\"{fingerprint}\"}}", out var legacy) ||
            !MarkerOwns(legacy, fingerprint))
            throw new InvalidOperationException("MP save marker self-test failed");
        var pendingJson = JsonSerializer.Serialize(new PendingWrite
        {
            Schema = MarkerSchema,
            Slot = MpSlotIndex,
            HadSlot = true,
            PreviousFingerprint = fingerprint,
            TargetFingerprint = new string('A', 64)
        });
        if (!TryParsePending(pendingJson, out var pending) || !pending.HadSlot ||
            pending.PreviousFingerprint != fingerprint ||
            !TryParsePending($"{{\"Schema\":2,\"Slot\":6,\"HadSlot\":true," +
                $"\"PreviousFingerprint\":\"{fingerprint}\",\"TargetFingerprint\":\"\"}}", out _))
            throw new InvalidOperationException("MP save pending journal self-test failed");
        if (IsApplyingRemoteSnapshot)
            throw new InvalidOperationException("MP save apply scope leaked before self-test");
        BeginRemoteSnapshotApply();
        BeginRemoteSnapshotApply();
        if (!IsApplyingRemoteSnapshot)
            throw new InvalidOperationException("MP save apply scope did not activate");
        EndRemoteSnapshotApply();
        if (!IsApplyingRemoteSnapshot)
            throw new InvalidOperationException("MP save nested apply scope ended early");
        EndRemoteSnapshotApply();
        if (IsApplyingRemoteSnapshot)
            throw new InvalidOperationException("MP save apply scope leaked after self-test");
        SelfTestFailureInjection();
        if (!ShouldRestoreBeforeRoleChange(
                SessionRole.Client, SessionRole.Offline, true) ||
            !ShouldRestoreBeforeRoleChange(
                SessionRole.Client, SessionRole.Host, true) ||
            ShouldRestoreBeforeRoleChange(
                SessionRole.Client, SessionRole.Client, true) ||
            ShouldRestoreBeforeRoleChange(
                SessionRole.Host, SessionRole.Offline, true) ||
            ShouldRestoreBeforeRoleChange(
                SessionRole.Client, SessionRole.Offline, false))
            throw new InvalidOperationException("MP original-profile restore policy failed");
    }

    internal static void Reset()
    {
        ResetHost();
        ResetClient();
        if (!_originalProfileRestoreRequired && !_normalSaveRestartRequired)
            _status = string.Empty;
    }

    internal static bool RequiresOriginalProfileRestore(
        SessionRole currentRole,
        SessionRole nextRole) =>
        ShouldRestoreBeforeRoleChange(
            currentRole, nextRole, _originalProfileRestoreRequired);

    private static bool ShouldRestoreBeforeRoleChange(
        SessionRole currentRole,
        SessionRole nextRole,
        bool restoreRequired) =>
        restoreRequired && currentRole == SessionRole.Client && nextRole != SessionRole.Client;

    private static void BeginRemoteSnapshotApply() => _remoteSnapshotApplyDepth++;

    private static void EndRemoteSnapshotApply()
    {
        if (_remoteSnapshotApplyDepth > 0)
            _remoteSnapshotApplyDepth--;
    }

    internal static void UpdateHost(UdpSession session, ManualLogSource log)
    {
        if (session == null || !session.Connected)
        {
            ResetHost();
            return;
        }

        if (_hostConnectionId != session.ConnectionId)
        {
            ResetHost();
            _hostConnectionId = session.ConnectionId;
        }

        while (session.TryTakeSaveSnapshotAck(out var ack))
        {
            if (ack.TransferId == _hostTransferId && ack.Fingerprint == _hostFingerprint)
            {
                _hostRemoteLoaded = ack.Loaded;
                _status = ack.Loaded ? "Host save synced" : "Host save rejected by client";
            }
        }

        if (_hostSnapshot.Length == 0 && !PrepareHostSnapshot(session, log))
            return;

        while (!_hostRemoteLoaded && _hostNextChunk < ChunkCount(_hostSnapshot.Length) &&
               session.ReliableCapacityRemaining > 0)
        {
            var offset = _hostNextChunk * Protocol.MaxSaveSnapshotChunkBytes;
            var count = Math.Min(Protocol.MaxSaveSnapshotChunkBytes, _hostSnapshot.Length - offset);
            var data = new byte[count];
            Array.Copy(_hostSnapshot, offset, data, 0, count);
            if (!session.SendSaveSnapshotChunk(new SaveSnapshotChunk(
                    _hostTransferId, _hostFingerprint, _hostSnapshot.Length,
                    (ushort)_hostNextChunk, (ushort)ChunkCount(_hostSnapshot.Length), data)))
                break;
            _hostNextChunk++;
        }
    }

    internal static void UpdateClient(UdpSession session, ManualLogSource log)
    {
        if (session == null || !session.Connected)
        {
            ResetClient();
            return;
        }

        while (session.TryTakeSaveSnapshotChunk(out var chunk))
        {
            if (chunk.TransferId != _clientTransferId || chunk.Fingerprint != _clientFingerprint)
                StartClientTransfer(chunk);
            if (_clientRejectedTransfer)
            {
                session.SendSaveSnapshotAck(new SaveSnapshotAck(
                    _clientTransferId, _clientFingerprint, false));
                continue;
            }
            if (_clientLoaded)
            {
                session.SendSaveSnapshotAck(new SaveSnapshotAck(
                    _clientTransferId, _clientFingerprint, true));
                continue;
            }
            if (chunk.TotalBytes != _clientTotalBytes || chunk.ChunkCount != _clientChunkCount)
            {
                RejectClientSave(session, "Host save chunk metadata changed during transfer");
                _clientRejectedTransfer = true;
                continue;
            }
            ClientChunks.TryAdd(chunk.ChunkIndex, chunk.Data);
            if (ClientChunks.Count == _clientChunkCount)
                FinishClientTransfer(session, log);
        }
    }

    internal static bool LoadHostSlotForCampaign(ManualLogSource log)
    {
        var save = FindSaveSystem(log);
        if (save == null)
            return false;
        try
        {
            ThrowIfSelfTestFailure(SaveFailurePoint.Load);
            if (save.LoadGameDataFromSlot(MpSlotIndex, MpSlotType))
                return true;
            _status = "Could not load MP save slot";
            log?.LogWarning("MP save sync: host MP slot load failed");
        }
        catch (Exception exception)
        {
            _status = "Could not load MP save slot";
            log?.LogWarning($"MP save sync: host MP slot load failed: {exception.Message}");
        }
        return false;
    }

    private static bool PrepareHostSnapshot(UdpSession session, ManualLogSource log)
    {
        var save = FindSaveSystem(log);
        if (save?.GameDataManager == null || save.PhotoDataManager == null ||
            save.PlayerDataManager == null)
            return false;
        try
        {
            if (!TryBeginSlotWrite(save, string.Empty, log, out var pending))
                return false;
            var hostWritten = false;
            var slotVerified = false;
            var details = string.Empty;
            var fullFingerprint = string.Empty;
            if (!TryCompleteSlotWrite(
                    () =>
                    {
                        ThrowIfSelfTestFailure(SaveFailurePoint.HostWrite);
                        return hostWritten = save.SaveGameDataInSlot(MpSlotIndex, true, MpSlotType);
                    },
                    () => true,
                    () => true,
                    () =>
                    {
                        ThrowIfSelfTestFailure(SaveFailurePoint.Verification);
                        slotVerified = TryReadSlotBundle(save, out _hostSnapshot, out details);
                        if (slotVerified)
                        {
                            fullFingerprint = FullFingerprint(_hostSnapshot);
                            pending.TargetFingerprint = fullFingerprint;
                            WriteAtomicText(MpSlotPendingPath, JsonSerializer.Serialize(pending));
                        }
                        return slotVerified;
                    },
                    () => true,
                    () => CommitSlotWrite(pending, fullFingerprint, log),
                    () => RollBackSlotWrite(save, pending, log)))
            {
                if (!hostWritten)
                    _status = "Could not write MP save slot";
                else if (!slotVerified)
                {
                    _status = "MP save slot did not contain complete data";
                    log?.LogWarning($"MP save sync: invalid host slot after write; {details}");
                }
                _hostSnapshot = Array.Empty<byte>();
                return false;
            }
            _hostFingerprint = Fingerprint(_hostSnapshot);
            _hostTransferId = session.ConnectionId ^ ((ulong)_hostFingerprint << 32) ^ (uint)_hostSnapshot.Length;
            if (_hostTransferId == 0)
                _hostTransferId = _hostFingerprint;
            _hostNextChunk = 0;
            _hostRemoteLoaded = false;
            _status = $"Syncing host save ({_hostSnapshot.Length / 1024} KB)";
            log?.LogInfo($"MP save sync: prepared {_hostSnapshot.Length} bytes; fp={_hostFingerprint:X8}");
            return true;
        }
        catch (Exception exception)
        {
            _status = "Could not prepare MP save";
            log?.LogWarning($"MP save sync: host prepare failed: {exception.Message}");
            TryRecoverPendingWrite(save, log);
            return false;
        }
    }

    private static void StartClientTransfer(SaveSnapshotChunk chunk)
    {
        ResetClient();
        _clientTransferId = chunk.TransferId;
        _clientFingerprint = chunk.Fingerprint;
        _clientTotalBytes = chunk.TotalBytes;
        _clientChunkCount = chunk.ChunkCount;
        _clientRejectedTransfer = chunk.TotalBytes is < BundleHeaderSize or > MaxBundleBytes;
        if (_clientRejectedTransfer)
        {
            _status = "Host save bundle is too large";
            return;
        }
        _status = $"Receiving host save ({chunk.TotalBytes / 1024} KB)";
    }

    private static void FinishClientTransfer(UdpSession session, ManualLogSource log)
    {
        if (_clientTotalBytes is < BundleHeaderSize or > MaxBundleBytes)
        {
            RejectClientSave(session, "Host save bundle is too large");
            return;
        }
        var bytes = new byte[_clientTotalBytes];
        for (ushort index = 0; index < _clientChunkCount; index++)
        {
            if (!ClientChunks.TryGetValue(index, out var chunk))
                return;
            var offset = (long)index * Protocol.MaxSaveSnapshotChunkBytes;
            if (chunk == null || chunk.Length == 0 || offset + chunk.Length > bytes.Length)
            {
                RejectClientSave(session, "Host save chunks are invalid");
                return;
            }
            Array.Copy(chunk, 0, bytes, (int)offset, chunk.Length);
        }
        if (Fingerprint(bytes) != _clientFingerprint)
        {
            RejectClientSave(session, "Host save checksum mismatch");
            return;
        }
        var save = FindSaveSystem(log);
        if (save?.GameDataManager == null || save.PhotoDataManager == null ||
            save.PlayerDataManager == null)
            return;
        BeginRemoteSnapshotApply();
        try
        {
            if (!TryUnpackBundle(bytes, out var gameJson, out var photoJson, out var playerJson))
            {
                RejectClientSave(session, "Host save bundle is invalid");
                return;
            }
            if (!TryCaptureOriginalProfile(save, log))
            {
                RejectClientSave(session, _status);
                return;
            }
            var fullFingerprint = FullFingerprint(bytes);
            if (!TryBeginSlotWrite(save, fullFingerprint, log, out var pending))
            {
                session.SendSaveSnapshotAck(new SaveSnapshotAck(
                    _clientTransferId, _clientFingerprint, false));
                return;
            }
            var gameSaved = false;
            var photoSaved = false;
            var playerSaved = false;
            var verified = false;
            var loaded = false;
            var completed = TryCompleteSlotWrite(
                () =>
                {
                    ThrowIfSelfTestFailure(SaveFailurePoint.GameWrite);
                    return gameSaved = save.GameDataManager.SaveSlotWithJson(
                        gameJson, MpSlotIndex, MpSlotType);
                },
                () =>
                {
                    ThrowIfSelfTestFailure(SaveFailurePoint.PhotoWrite);
                    return photoSaved = save.PhotoDataManager.SaveSlotWithJson(
                        photoJson, MpSlotIndex, MpSlotType);
                },
                () =>
                {
                    ThrowIfSelfTestFailure(SaveFailurePoint.PlayerWrite);
                    return playerSaved = save.PlayerDataManager.SaveSlotWithJson(
                        playerJson, MpSlotIndex, MpSlotType);
                },
                () =>
                {
                    ThrowIfSelfTestFailure(SaveFailurePoint.Verification);
                    verified = TryReadSlotBundle(save, out var written, out _) &&
                        FullFingerprint(written) == fullFingerprint;
                    if (verified)
                        _normalSaveRestartRequired = true;
                    return verified;
                },
                () =>
                {
                    ThrowIfSelfTestFailure(SaveFailurePoint.Load);
                    return loaded = save.LoadGameDataFromSlot(MpSlotIndex, MpSlotType);
                },
                () => CommitSlotWrite(pending, fullFingerprint, log),
                () => RollBackSlotWrite(save, pending, log));
            if (!completed)
            {
                RejectClientSave(session, "Could not safely load host save");
                log?.LogWarning(
                    $"MP save sync: client transaction failed; gd={gameSaved}; " +
                    $"pz={photoSaved}; pd={playerSaved}; verified={verified}; loaded={loaded}");
                return;
            }
            _clientLoaded = loaded;
            _status = _clientLoaded ? "Host save synced" : "Could not load host save";
            session.SendSaveSnapshotAck(new SaveSnapshotAck(
                _clientTransferId, _clientFingerprint, _clientLoaded));
            log?.LogInfo(
                $"MP save sync: client gd={gameSaved}; pz={photoSaved}; pd={playerSaved}; " +
                $"loaded={loaded}; fp={_clientFingerprint:X8}");
        }
        catch (Exception exception)
        {
            _status = "Could not load host save";
            TryRecoverPendingWrite(save, log);
            session.SendSaveSnapshotAck(new SaveSnapshotAck(_clientTransferId, _clientFingerprint, false));
            log?.LogWarning($"MP save sync: client load failed: {exception.Message}");
        }
        finally
        {
            EndRemoteSnapshotApply();
        }
    }

    private static bool TryCaptureOriginalProfile(SaveSystem save, ManualLogSource log)
    {
        if (_originalProfileRestoreRequired)
            return true;
        try
        {
            var game = SaveDataBase.Serialize(save.GameDataManager.Data);
            var photo = SaveDataBase.Serialize(save.PhotoDataManager.Data);
            var player = SaveDataBase.Serialize(save.PlayerDataManager.Data);
            if (!IsJsonObject(game) || !IsJsonObject(photo) || !IsJsonObject(player))
                throw new InvalidDataException("current profile serialization returned invalid JSON");

            _originalGameJson = game;
            _originalPhotoJson = photo;
            _originalPlayerJson = player;
            _originalProfileRestoreRequired = true;
            log?.LogInfo("MP save sync: captured original client profile in memory");
            return true;
        }
        catch (Exception exception)
        {
            _status = "Could not preserve original client profile";
            log?.LogWarning($"MP save sync: original profile capture failed: {exception.Message}");
            return false;
        }
    }

    internal static bool TryRestoreOriginalProfile(ManualLogSource log)
    {
        if (!_originalProfileRestoreRequired)
            return true;
        var save = FindSaveSystem(log);
        if (save?.GameDataManager == null || save.PhotoDataManager == null ||
            save.PlayerDataManager == null)
        {
            _status = "Save system not ready to restore original client profile";
            log?.LogWarning("MP save sync: original profile restore managers are unavailable");
            return false;
        }

        try
        {
            if (!save.GameDataManager.TryLoadFromJson(_originalGameJson, out var game) ||
                !save.PhotoDataManager.TryLoadFromJson(_originalPhotoJson, out var photo) ||
                !save.PlayerDataManager.TryLoadFromJson(_originalPlayerJson, out var player) ||
                game == null || photo == null || player == null)
                throw new InvalidDataException("original profile JSON could not be parsed");

            save.GameDataManager.SetLoadedData(game);
            save.PhotoDataManager.SetLoadedData(photo);
            save.PlayerDataManager.SetLoadedData(player);

            _originalGameJson = string.Empty;
            _originalPhotoJson = string.Empty;
            _originalPlayerJson = string.Empty;
            _originalProfileRestoreRequired = false;
            _status = _normalSaveRestartRequired
                ? "Original profile restored; restart before using normal saves"
                : "Original client profile restored";
            log?.LogInfo(
                "MP save sync: restored original client profile from memory; " +
                $"restartRequired={_normalSaveRestartRequired}");
            return true;
        }
        catch (Exception exception)
        {
            _status = "Could not restore original client profile; remaining online";
            log?.LogWarning($"MP save sync: original profile restore failed: {exception.Message}");
            return false;
        }
    }

    private static bool TryBeginSlotWrite(
        SaveSystem save,
        string targetFingerprint,
        ManualLogSource log,
        out PendingWrite pending)
    {
        pending = null;
        if (!TryRecoverPendingWrite(save, log))
            return false;

        var hadSlot = AnySlotFileExists(save);
        var previous = Array.Empty<byte>();
        var previousFingerprint = string.Empty;
        if (hadSlot)
        {
            if (!TryReadSlotBundle(save, out previous, out var details))
            {
                RefuseOccupiedSlot(log, $"slot data is incomplete; {details}");
                return false;
            }
            previousFingerprint = FullFingerprint(previous);
            if (!TryValidateOwnedMarker(previous, previousFingerprint, log))
                return false;
        }
        else if (File.Exists(MpSlotMarkerPath))
        {
            RefuseOccupiedSlot(log, "marker exists but slot data is missing");
            return false;
        }

        pending = new PendingWrite
        {
            Schema = MarkerSchema,
            Slot = MpSlotIndex,
            HadSlot = hadSlot,
            PreviousFingerprint = previousFingerprint,
            TargetFingerprint = targetFingerprint ?? string.Empty
        };
        try
        {
            WriteAtomicBytes(MpSlotBackupPath, previous);
            WriteAtomicText(MpSlotPendingPath, JsonSerializer.Serialize(pending));
            return true;
        }
        catch (Exception exception)
        {
            _status = "Could not back up MP save slot";
            log?.LogWarning($"MP save sync: transaction backup failed: {exception.Message}");
            return false;
        }
    }

    private static bool CommitSlotWrite(
        PendingWrite pending,
        string fingerprint,
        ManualLogSource log)
    {
        try
        {
            if (pending == null || pending.Schema != MarkerSchema || pending.Slot != MpSlotIndex ||
                !string.Equals(pending.TargetFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("pending journal does not match marker commit");
            WriteMarker(CreateMarker(fingerprint, pending.PreviousFingerprint));
            if (!TryDeleteFile(MpSlotPendingPath))
                throw new IOException("could not remove pending journal");
            if (!TryDeleteFile(MpSlotBackupPath))
                log?.LogWarning("MP save sync: committed, but stale backup could not be removed");
            return true;
        }
        catch (Exception exception)
        {
            _status = "Could not commit MP save slot";
            log?.LogWarning($"MP save sync: marker commit failed: {exception.Message}");
            return false;
        }
    }

    private static bool TryRecoverPendingWrite(SaveSystem save, ManualLogSource log)
    {
        if (!File.Exists(MpSlotPendingPath))
            return true;
        try
        {
            if (!TryReadMetadata(MpSlotPendingPath, out var json) ||
                !TryParsePending(json, out var pending))
            {
                _status = "MP save recovery metadata is invalid";
                log?.LogWarning("MP save sync: refusing slot write because pending journal is invalid");
                return false;
            }
            return RollBackSlotWrite(save, pending, log);
        }
        catch (Exception exception)
        {
            _status = "Could not recover MP save slot";
            log?.LogWarning($"MP save sync: pending recovery failed: {exception.Message}");
            return false;
        }
    }

    private static bool RollBackSlotWrite(
        SaveSystem save,
        PendingWrite pending,
        ManualLogSource log)
    {
        try
        {
            if (pending == null ||
                (pending.Schema != MarkerSchema && pending.Schema != LegacyMarkerSchema) ||
                pending.Slot != MpSlotIndex)
                throw new InvalidDataException("invalid pending journal");
            if (pending.HadSlot)
            {
                var backup = ReadBoundedBytes(MpSlotBackupPath, MaxBundleBytes);
                if (!TryUnpackBundle(backup, out var game, out var photo, out var player) ||
                    FullFingerprint(backup) != pending.PreviousFingerprint)
                    throw new InvalidDataException("backup fingerprint mismatch");
                if (!WriteSlot(save, game, photo, player) ||
                    !TryReadSlotBundle(save, out var restored, out _) ||
                    FullFingerprint(restored) != pending.PreviousFingerprint)
                    throw new IOException("restored slot verification failed");
                WriteMarker(CreateMarker(pending.PreviousFingerprint, string.Empty));
            }
            else
            {
                DeleteSlotFiles(save);
                if (AnySlotFileExists(save))
                    throw new IOException("new partial slot files could not be removed");
                TryDeleteFile(MpSlotMarkerPath);
            }
            if (!TryDeleteFile(MpSlotPendingPath))
                throw new IOException("pending journal could not be removed after rollback");
            TryDeleteFile(MpSlotBackupPath);
            log?.LogInfo("MP save sync: restored slot after interrupted write");
            return true;
        }
        catch (Exception exception)
        {
            _status = "Could not recover MP save slot";
            log?.LogWarning($"MP save sync: rollback failed: {exception.Message}");
            return false;
        }
    }

    private static bool TryValidateOwnedMarker(
        byte[] current,
        string currentFingerprint,
        ManualLogSource log)
    {
        if (!TryReadMetadata(MpSlotMarkerPath, out var json))
        {
            RefuseOccupiedSlot(log, "valid ownership marker is missing");
            return false;
        }
        if (TryParseMarker(json, out var marker))
        {
            if (MarkerOwns(marker, currentFingerprint))
            {
                if (marker.Schema == MarkerSchema)
                    return true;
                try
                {
                    WriteMarker(CreateMarker(currentFingerprint, string.Empty));
                }
                catch (Exception exception)
                {
                    RefuseOccupiedSlot(log, "legacy marker could not be migrated");
                    log?.LogWarning($"MP save sync: legacy marker migration failed: {exception.Message}");
                    return false;
                }
                log?.LogInfo("MP save sync: migrated schema-2 slot marker");
                return true;
            }
            RefuseOccupiedSlot(log, "slot contents differ from the last verified DTMP write");
            return false;
        }

        if (uint.TryParse(json.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture,
                out var legacy) && legacy == Fingerprint(current))
        {
            WriteMarker(CreateMarker(currentFingerprint, string.Empty));
            log?.LogInfo("MP save sync: migrated legacy slot marker");
            return true;
        }
        RefuseOccupiedSlot(log, "ownership marker is invalid or stale");
        return false;
    }

    private static bool TryReadSlotBundle(
        SaveSystem save,
        out byte[] bundle,
        out string details)
    {
        bundle = Array.Empty<byte>();
        details = string.Empty;
        try
        {
            var game = save.GameDataManager.GetDataStringFromFile(MpSlotIndex, MpSlotType);
            var photo = save.PhotoDataManager.GetDataStringFromFile(MpSlotIndex, MpSlotType);
            var player = save.PlayerDataManager.GetDataStringFromFile(MpSlotIndex, MpSlotType);
            if (!IsJsonObject(game) || !IsJsonObject(photo) || !IsJsonObject(player))
            {
                details = $"gd={FirstContentCharacter(game)}; pz={FirstContentCharacter(photo)}; " +
                    $"pd={FirstContentCharacter(player)}";
                return false;
            }
            bundle = PackBundle(game, photo, player);
            return true;
        }
        catch (Exception exception)
        {
            details = exception.Message;
            return false;
        }
    }

    private static bool WriteSlot(
        SaveSystem save,
        string game,
        string photo,
        string player)
    {
        var gameSaved = save.GameDataManager.SaveSlotWithJson(game, MpSlotIndex, MpSlotType);
        var photoSaved = save.PhotoDataManager.SaveSlotWithJson(photo, MpSlotIndex, MpSlotType);
        var playerSaved = save.PlayerDataManager.SaveSlotWithJson(player, MpSlotIndex, MpSlotType);
        return gameSaved && photoSaved && playerSaved;
    }

    private static bool TryCompleteSlotWrite(
        Func<bool> writeGame,
        Func<bool> writePhoto,
        Func<bool> writePlayer,
        Func<bool> verify,
        Func<bool> load,
        Func<bool> updateMarker,
        Action rollBack)
    {
        var completed = false;
        try
        {
            completed = writeGame() && writePhoto() && writePlayer() && verify() && load() &&
                updateMarker();
            return completed;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (!completed)
                rollBack();
        }
    }

    private static void ThrowIfSelfTestFailure(SaveFailurePoint point)
    {
        if (_selfTestFailurePoint == point)
        {
            _selfTestFailurePoint = SaveFailurePoint.None;
            throw new IOException($"self-test failure at {point}");
        }
    }

    private static void SelfTestFailureInjection()
    {
        foreach (SaveFailurePoint point in Enum.GetValues(typeof(SaveFailurePoint)))
        {
            if (point == SaveFailurePoint.None)
                continue;
            var rolledBack = false;
            _selfTestFailurePoint = point;
            try
            {
                if (TryCompleteSlotWrite(
                        () => SelfTestWriteStep(point == SaveFailurePoint.HostWrite
                            ? SaveFailurePoint.HostWrite
                            : SaveFailurePoint.GameWrite),
                        () => SelfTestWriteStep(SaveFailurePoint.PhotoWrite),
                        () => SelfTestWriteStep(SaveFailurePoint.PlayerWrite),
                        () => SelfTestWriteStep(SaveFailurePoint.Verification),
                        () => SelfTestWriteStep(SaveFailurePoint.Load),
                        () => SelfTestWriteStep(SaveFailurePoint.MarkerUpdate),
                        () => rolledBack = true) || !rolledBack)
                    throw new InvalidOperationException($"MP save failure injection missed {point}");
            }
            finally
            {
                _selfTestFailurePoint = SaveFailurePoint.None;
            }
        }
    }

    private static bool SelfTestWriteStep(SaveFailurePoint point)
    {
        ThrowIfSelfTestFailure(point);
        return true;
    }

    private static bool AnySlotFileExists(SaveSystem save)
    {
        if (save.CheckGameSaveExist(SaveDataType.GameData, MpSlotIndex, MpSlotType))
            return true;
        foreach (var type in new[] { SaveDataType.GameData, SaveDataType.PhotoData, SaveDataType.PlayerData })
            if (File.Exists(save.GetSaveFilePath(type, MpSlotIndex, MpSlotType)))
                return true;
        return false;
    }

    private static void DeleteSlotFiles(SaveSystem save)
    {
        foreach (var type in new[] { SaveDataType.GameData, SaveDataType.PhotoData, SaveDataType.PlayerData })
        {
            var path = save.GetSaveFilePath(type, MpSlotIndex, MpSlotType);
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private static void RefuseOccupiedSlot(ManualLogSource log, string reason)
    {
        _status = "Manual save slot 7 changed outside DTMP; recovery required";
        log?.LogWarning($"MP save sync: refusing manual slot 7: {reason}");
    }

    private static void RejectClientSave(UdpSession session, string status)
    {
        _status = status;
        session.SendSaveSnapshotAck(new SaveSnapshotAck(
            _clientTransferId, _clientFingerprint, false));
    }

    private static SaveSystem FindSaveSystem(ManualLogSource log)
    {
        var save = UnityEngine.Object.FindFirstObjectByType<SaveSystem>();
        if (save == null)
        {
            _status = "Save system not ready";
            log?.LogWarning("MP save sync: SaveSystem not found");
        }
        return save;
    }

    private static void ResetHost()
    {
        _hostSnapshot = Array.Empty<byte>();
        _hostConnectionId = 0;
        _hostTransferId = 0;
        _hostFingerprint = 0;
        _hostNextChunk = 0;
        _hostRemoteLoaded = false;
    }

    private static void ResetClient()
    {
        ClientChunks.Clear();
        _clientTransferId = 0;
        _clientFingerprint = 0;
        _clientTotalBytes = 0;
        _clientChunkCount = 0;
        _clientLoaded = false;
        _clientRejectedTransfer = false;
    }

    private static int ChunkCount(int totalBytes) =>
        (totalBytes + Protocol.MaxSaveSnapshotChunkBytes - 1) / Protocol.MaxSaveSnapshotChunkBytes;

    private static byte[] PackBundle(string gameJson, string photoJson, string playerJson)
    {
        if (!IsJsonObject(gameJson) || !IsJsonObject(photoJson) || !IsJsonObject(playerJson))
            throw new ArgumentException("Save bundle contains invalid JSON");
        var gameLength = Encoding.UTF8.GetByteCount(gameJson);
        var photoLength = Encoding.UTF8.GetByteCount(photoJson);
        var playerLength = Encoding.UTF8.GetByteCount(playerJson);
        if (!AreBundleLengthsValid(gameLength, photoLength, playerLength))
            throw new ArgumentException("Save bundle is too large");
        var game = Encoding.UTF8.GetBytes(gameJson);
        var photo = Encoding.UTF8.GetBytes(photoJson);
        var player = Encoding.UTF8.GetBytes(playerJson);
        var bundle = new byte[BundleHeaderSize + game.Length + photo.Length + player.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bundle, BundleMagic);
        BinaryPrimitives.WriteInt32LittleEndian(bundle.AsSpan(4), game.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bundle.AsSpan(8), photo.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bundle.AsSpan(12), player.Length);
        game.CopyTo(bundle, BundleHeaderSize);
        photo.CopyTo(bundle, BundleHeaderSize + game.Length);
        player.CopyTo(bundle, BundleHeaderSize + game.Length + photo.Length);
        return bundle;
    }

    private static bool TryUnpackBundle(
        byte[] bundle,
        out string gameJson,
        out string photoJson,
        out string playerJson)
    {
        gameJson = photoJson = playerJson = string.Empty;
        if (bundle == null || bundle.Length < BundleHeaderSize ||
            BinaryPrimitives.ReadUInt32LittleEndian(bundle) != BundleMagic)
            return false;
        var gameLength = BinaryPrimitives.ReadInt32LittleEndian(bundle.AsSpan(4));
        var photoLength = BinaryPrimitives.ReadInt32LittleEndian(bundle.AsSpan(8));
        var playerLength = BinaryPrimitives.ReadInt32LittleEndian(bundle.AsSpan(12));
        if (!AreBundleLengthsValid(gameLength, photoLength, playerLength) ||
            (long)BundleHeaderSize + gameLength + photoLength + playerLength != bundle.Length)
            return false;
        gameJson = Encoding.UTF8.GetString(bundle, BundleHeaderSize, gameLength);
        photoJson = Encoding.UTF8.GetString(bundle, BundleHeaderSize + gameLength, photoLength);
        playerJson = Encoding.UTF8.GetString(
            bundle, BundleHeaderSize + gameLength + photoLength, playerLength);
        return IsJsonObject(gameJson) && IsJsonObject(photoJson) && IsJsonObject(playerJson);
    }

    private static bool AreBundleLengthsValid(int gameLength, int photoLength, int playerLength) =>
        gameLength > 0 && photoLength > 0 && playerLength > 0 &&
        (long)BundleHeaderSize + gameLength + photoLength + playerLength <= MaxBundleBytes;

    private static bool IsJsonObject(string value)
    {
        if (string.IsNullOrEmpty(value))
            return false;
        var start = 0;
        while (start < value.Length && (char.IsWhiteSpace(value[start]) || value[start] == '\uFEFF'))
            start++;
        var end = value.Length - 1;
        while (end >= start && char.IsWhiteSpace(value[end]))
            end--;
        return end > start && value[start] == '{' && value[end] == '}';
    }

    private static string FirstContentCharacter(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "empty";
        for (var index = 0; index < value.Length; index++)
            if (!char.IsWhiteSpace(value[index]) && value[index] != '\uFEFF')
                return $"U+{(int)value[index]:X4}";
        return "whitespace";
    }

    private static bool TryParseMarker(string json, out SlotMarker marker)
    {
        marker = null;
        try
        {
            marker = JsonSerializer.Deserialize<SlotMarker>(json);
            if (marker == null || marker.Slot != MpSlotIndex)
                return false;
            if (marker.Schema == MarkerSchema)
                return IsFullFingerprint(marker.MultiplayerFingerprint) &&
                    (string.IsNullOrEmpty(marker.PreviousFingerprint) ||
                        IsFullFingerprint(marker.PreviousFingerprint)) &&
                    !string.IsNullOrWhiteSpace(marker.GameBuild) &&
                    DateTimeOffset.TryParseExact(
                        marker.CreatedAt, "O", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out _);
            return marker.Schema == LegacyMarkerSchema && IsFullFingerprint(marker.LegacyFingerprint);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryParsePending(string json, out PendingWrite pending)
    {
        pending = null;
        try
        {
            pending = JsonSerializer.Deserialize<PendingWrite>(json);
            return pending != null &&
                (pending.Schema == MarkerSchema || pending.Schema == LegacyMarkerSchema) &&
                pending.Slot == MpSlotIndex &&
                (!pending.HadSlot || IsFullFingerprint(pending.PreviousFingerprint)) &&
                (string.IsNullOrEmpty(pending.TargetFingerprint) ||
                    IsFullFingerprint(pending.TargetFingerprint));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool MarkerOwns(SlotMarker marker, string fingerprint) =>
        marker != null && string.Equals(
            marker.Schema == MarkerSchema
                ? marker.MultiplayerFingerprint
                : marker.LegacyFingerprint,
            fingerprint, StringComparison.OrdinalIgnoreCase);

    private static SlotMarker CreateMarker(string multiplayerFingerprint, string previousFingerprint) =>
        new()
        {
            Schema = MarkerSchema,
            Slot = MpSlotIndex,
            MultiplayerFingerprint = multiplayerFingerprint,
            PreviousFingerprint = previousFingerprint ?? string.Empty,
            // No stable local profile/Steam identity is proven safe for LAN/offline use.
            ProfileIdentity = string.Empty,
            GameBuild = string.IsNullOrWhiteSpace(Application.version) ? "unknown" : Application.version,
            CreatedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
        };

    private static bool IsFullFingerprint(string value)
    {
        if (value == null || value.Length != 64)
            return false;
        for (var index = 0; index < value.Length; index++)
            if (!Uri.IsHexDigit(value[index]))
                return false;
        return true;
    }

    private static bool TryReadMetadata(string path, out string value)
    {
        value = string.Empty;
        try
        {
            if (!File.Exists(path))
                return false;
            var bytes = ReadBoundedBytes(path, MaxMetadataBytes);
            value = Encoding.UTF8.GetString(bytes);
            return !string.IsNullOrWhiteSpace(value);
        }
        catch
        {
            return false;
        }
    }

    private static byte[] ReadBoundedBytes(string path, int maximum)
    {
        var length = new FileInfo(path).Length;
        if (length < 0 || length > maximum)
            throw new InvalidDataException($"file exceeds {maximum} bytes");
        return File.ReadAllBytes(path);
    }

    private static void WriteAtomicText(string path, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        if (bytes.Length == 0 || bytes.Length > MaxMetadataBytes)
            throw new InvalidDataException("metadata size is invalid");
        WriteAtomicBytes(path, bytes);
    }

    private static void WriteMarker(SlotMarker marker)
    {
        ThrowIfSelfTestFailure(SaveFailurePoint.MarkerUpdate);
        WriteAtomicText(MpSlotMarkerPath, JsonSerializer.Serialize(marker));
    }

    private static void WriteAtomicBytes(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        using (var stream = new FileStream(
                   temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(true);
        }
        File.Move(temporary, path, true);
    }

    private static bool TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
            return !File.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    private static string FullFingerprint(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));

    private static uint Fingerprint(byte[] bytes)
    {
        var hash = SHA256.HashData(bytes);
        var value = BitConverter.ToUInt32(hash, 0);
        return value == 0 ? 1u : value;
    }
}
