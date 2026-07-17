using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using DR.Save;
using UnityEngine;

namespace DaveTheDiverMP;

internal static class MultiplayerSaveSync
{
    private const int MpSlotIndex = 6;
    private const SaveSlotType MpSlotType = SaveSlotType.Manual;
    private const uint BundleMagic = 0x42534D44; // DMSB
    private const int BundleHeaderSize = 16;
    private static readonly string MpSlotMarkerPath =
        Path.Combine(Paths.ConfigPath, "DaveTheDiverMP.mp-slot");

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
    private static string _status = string.Empty;

    internal static bool HostRemoteLoaded => _hostRemoteLoaded;
    internal static bool ClientLoaded => _clientLoaded;
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
    }

    internal static void Reset()
    {
        ResetHost();
        ResetClient();
        _status = string.Empty;
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
            if (_clientLoaded)
            {
                session.SendSaveSnapshotAck(new SaveSnapshotAck(
                    _clientTransferId, _clientFingerprint, true));
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
        if (save?.GameDataManager == null)
            return false;
        try
        {
            if (save.CheckGameSaveExist(SaveDataType.GameData, MpSlotIndex, MpSlotType) &&
                !File.Exists(MpSlotMarkerPath))
            {
                _status = "Manual save slot 7 is occupied";
                log?.LogWarning("MP save sync: manual slot 7 is occupied and not owned by DTMP");
                return false;
            }
            if (!save.SaveGameDataInSlot(MpSlotIndex, true, MpSlotType))
            {
                _status = "Could not write MP save slot";
                return false;
            }
            var gameJson = save.GameDataManager.GetDataStringFromFile(MpSlotIndex, MpSlotType);
            var photoJson = save.PhotoDataManager.GetDataStringFromFile(MpSlotIndex, MpSlotType);
            var playerJson = save.PlayerDataManager.GetDataStringFromFile(MpSlotIndex, MpSlotType);
            if (!IsJsonObject(gameJson) || !IsJsonObject(photoJson) || !IsJsonObject(playerJson))
            {
                _status = "MP save slot did not contain complete data";
                log?.LogWarning(
                    $"MP save sync: invalid slot JSON; gd={FirstContentCharacter(gameJson)}; " +
                    $"pz={FirstContentCharacter(photoJson)}; pd={FirstContentCharacter(playerJson)}");
                return false;
            }
            _hostSnapshot = PackBundle(gameJson, photoJson, playerJson);
            _hostFingerprint = Fingerprint(_hostSnapshot);
            File.WriteAllText(MpSlotMarkerPath, _hostFingerprint.ToString("X8"));
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
        _status = $"Receiving host save ({chunk.TotalBytes / 1024} KB)";
    }

    private static void FinishClientTransfer(UdpSession session, ManualLogSource log)
    {
        var bytes = new byte[_clientTotalBytes];
        for (ushort index = 0; index < _clientChunkCount; index++)
        {
            if (!ClientChunks.TryGetValue(index, out var chunk))
                return;
            Array.Copy(chunk, 0, bytes, index * Protocol.MaxSaveSnapshotChunkBytes, chunk.Length);
        }
        if (Fingerprint(bytes) != _clientFingerprint)
        {
            _status = "Host save checksum mismatch";
            session.SendSaveSnapshotAck(new SaveSnapshotAck(_clientTransferId, _clientFingerprint, false));
            return;
        }
        var save = FindSaveSystem(log);
        if (save?.GameDataManager == null || save.PhotoDataManager == null ||
            save.PlayerDataManager == null)
            return;
        try
        {
            if (!TryUnpackBundle(bytes, out var gameJson, out var photoJson, out var playerJson))
            {
                _status = "Host save bundle is invalid";
                session.SendSaveSnapshotAck(new SaveSnapshotAck(
                    _clientTransferId, _clientFingerprint, false));
                return;
            }
            if (save.CheckGameSaveExist(SaveDataType.GameData, MpSlotIndex, MpSlotType) &&
                !File.Exists(MpSlotMarkerPath))
            {
                _status = "Manual save slot 7 is occupied";
                log?.LogWarning("MP save sync: client manual slot 7 is occupied and not owned by DTMP");
                session.SendSaveSnapshotAck(new SaveSnapshotAck(
                    _clientTransferId, _clientFingerprint, false));
                return;
            }
            File.WriteAllText(MpSlotMarkerPath, _clientFingerprint.ToString("X8"));
            var gameSaved = save.GameDataManager.SaveSlotWithJson(
                gameJson, MpSlotIndex, MpSlotType);
            var photoSaved = save.PhotoDataManager.SaveSlotWithJson(
                photoJson, MpSlotIndex, MpSlotType);
            var playerSaved = save.PlayerDataManager.SaveSlotWithJson(
                playerJson, MpSlotIndex, MpSlotType);
            var loaded = gameSaved && photoSaved && playerSaved &&
                save.LoadGameDataFromSlot(MpSlotIndex, MpSlotType);
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
            session.SendSaveSnapshotAck(new SaveSnapshotAck(_clientTransferId, _clientFingerprint, false));
            log?.LogWarning($"MP save sync: client load failed: {exception.Message}");
        }
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
    }

    private static int ChunkCount(int totalBytes) =>
        (totalBytes + Protocol.MaxSaveSnapshotChunkBytes - 1) / Protocol.MaxSaveSnapshotChunkBytes;

    private static byte[] PackBundle(string gameJson, string photoJson, string playerJson)
    {
        if (!IsJsonObject(gameJson) || !IsJsonObject(photoJson) || !IsJsonObject(playerJson))
            throw new ArgumentException("Save bundle contains invalid JSON");
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
        if (gameLength <= 0 || photoLength <= 0 || playerLength <= 0 ||
            (long)BundleHeaderSize + gameLength + photoLength + playerLength != bundle.Length)
            return false;
        gameJson = Encoding.UTF8.GetString(bundle, BundleHeaderSize, gameLength);
        photoJson = Encoding.UTF8.GetString(bundle, BundleHeaderSize + gameLength, photoLength);
        playerJson = Encoding.UTF8.GetString(
            bundle, BundleHeaderSize + gameLength + photoLength, playerLength);
        return IsJsonObject(gameJson) && IsJsonObject(photoJson) && IsJsonObject(playerJson);
    }

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

    private static uint Fingerprint(byte[] bytes)
    {
        var hash = SHA256.HashData(bytes);
        var value = BitConverter.ToUInt32(hash, 0);
        return value == 0 ? 1u : value;
    }
}
