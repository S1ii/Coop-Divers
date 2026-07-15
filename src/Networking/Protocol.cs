using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace DaveTheDiverMP;

internal enum PacketType : byte
{
    Hello = 1,
    HelloAck = 2,
    Heartbeat = 3,
    Disconnect = 4,
    PlayerSnapshot = 5,
    SceneState = 6,
    FishSnapshot = 7,
    PickupRemoved = 8,
    PickupRequest = 9,
    SceneTransition = 10,
    FishDamageRequest = 11,
    FishPickupRequest = 12,
    Ack = 13,
    FishRemoved = 14,
    IngredientsSyncRequest = 15,
    IngredientsSnapshotChunk = 16,
    IngredientsDelta = 17,
    RoomReady = 18,
    RoomState = 19,
    CampaignSnapshotChunk = 20,
    CampaignSnapshotAck = 21,
    DiveReady = 22,
    DiveState = 23,
    BoatDecoState = 24,
    FishManifest = 25,
    FishManifestState = 26,
    PlayerVisualState = 27,
    ProjectileVisualState = 28,
    DiverLifeState = 29,
    DiveExitRequest = 30,
    TravelReady = 31,
    TravelState = 32,
    DiveLootRequest = 33,
    DiveResultEntry = 34,
    DiveResultState = 35,
    MissionState = 36
}

internal enum TravelTarget : byte
{
    SushiBar = 1,
    Lobby = 2
}

internal readonly record struct PlayerSnapshot(
    uint SceneId,
    float X,
    float Y,
    float Z,
    float Rotation,
    float VelocityX,
    float VelocityY,
    uint SpriteId,
    float ScaleX,
    float ScaleY,
    bool Flipped);

internal readonly record struct VisualSprite(
    uint SpriteId,
    float OffsetX,
    float OffsetY,
    float OffsetZ,
    float Rotation,
    float ScaleX,
    float ScaleY,
    int SortingLayerId,
    int SortingOrder,
    bool FlipX,
    bool FlipY);

internal readonly record struct PlayerVisualState(uint SceneId, VisualSprite[] Sprites);

internal readonly record struct ProjectileVisualState(
    uint SceneId,
    int Id,
    uint SpriteId,
    float X,
    float Y,
    float Z,
    float Rotation,
    float ScaleX,
    float ScaleY,
    int SortingLayerId,
    int SortingOrder,
    bool Flipped,
    bool HasRope,
    float RopeStartX,
    float RopeStartY,
    float RopeStartZ,
    float RopeEndX,
    float RopeEndY,
    float RopeEndZ);

internal readonly record struct FishSnapshot(
    uint SceneId,
    int Id,
    int FishDataTID,
    float X,
    float Y,
    float Z,
    float Rotation,
    float Hp,
    byte Flags);

internal readonly record struct FishDamageRequest(
    uint SceneId,
    int Id,
    int Damage,
    int Element,
    int AttackType);

internal readonly record struct FishPickupRequest(uint SceneId, int Id);
internal readonly record struct FishRemoved(uint SceneId, int Id);

internal readonly record struct FishManifest(
    uint SceneId,
    uint Revision,
    int Id,
    string AllocatorUid,
    int FishDataTID,
    float X,
    float Y,
    float Z,
    float Rotation,
    float Hp,
    byte Flags);

internal readonly record struct FishManifestState(
    uint SceneId,
    uint Revision,
    ushort EntryCount);

internal readonly record struct PickupRemoved(
    uint SceneId,
    uint WorldId,
    int ItemId);

internal readonly record struct SceneTransitionCommand(
    string SceneName,
    int TransitionType,
    ushort Options);

internal readonly record struct IngredientCount(
    int IngredientId,
    int Place,
    int Count);

internal readonly record struct IngredientsSyncRequest(ulong RequestId);

internal readonly record struct IngredientsSnapshotChunk(
    ulong RequestId,
    ulong HostEpoch,
    uint Revision,
    ushort ChunkIndex,
    ushort ChunkCount,
    IngredientCount[] Entries);

internal readonly record struct IngredientsDelta(
    ulong HostEpoch,
    uint BaseRevision,
    uint Revision,
    IngredientCount[] Entries);

internal readonly record struct RoomReady(uint RoomId, uint Revision, bool Ready);
internal readonly record struct RoomState(uint RoomId, uint Revision, bool ClientReady);
internal readonly record struct DiveReady(uint Revision, bool Ready);
internal readonly record struct DiveState(uint Revision, bool HostReady, bool ClientReady);
internal readonly record struct BoatDecoState(int Id);
internal readonly record struct DiverLifeState(uint Revision, bool IsDead);
internal readonly record struct DiveExitRequest(uint Revision);
internal readonly record struct TravelReady(
    TravelTarget Target,
    uint Revision,
    bool Ready,
    bool NativeStarted);
internal readonly record struct TravelState(
    TravelTarget Target,
    uint Revision,
    bool HostReady,
    bool ClientReady);
internal readonly record struct DiveLootRequest(
    int ItemId,
    int Count,
    int BonusGrade,
    int LiftType,
    bool UpdateMission);
internal readonly record struct DiveResultEntry(
    ulong TransferId,
    ushort Index,
    ushort Total,
    int ItemId,
    int Count,
    int BonusGrade,
    int LiftType);
internal readonly record struct DiveResultState(ulong TransferId, ushort Total);
internal readonly record struct MissionConditionState(int Id, int Count);
internal readonly record struct MissionState(uint Revision, MissionConditionState[] Conditions);
internal readonly record struct CampaignSnapshotChunk(
    ulong TransferId,
    uint Revision,
    ushort ChunkIndex,
    ushort ChunkCount,
    byte[] Payload);
internal readonly record struct CampaignSnapshotAck(ulong TransferId, uint Revision);

internal static class Protocol
{
    private const uint Magic = 0x504D5444; // DTMP
    private const byte Version = 22;
    internal const int HeaderSize = 10;
    private const int SnapshotSize = HeaderSize + 41;
    private const int VisualStateFixedSize = HeaderSize + 5;
    private const int VisualSpriteSize = 38;
    private const int ProjectileVisualStateSize = HeaderSize + 70;
    internal const int MaxVisualSprites = 16;
    private const int FishSnapshotSize = HeaderSize + 33;
    private const int FishDamageRequestSize = HeaderSize + 20;
    private const int FishPickupRequestSize = HeaderSize + 8;
    private const int FishRemovedSize = HeaderSize + 8;
    private const int FishManifestFixedSize = HeaderSize + 38;
    private const int FishManifestStateSize = HeaderSize + 10;
    private const int PickupRemovedSize = HeaderSize + 12;
    private const int IngredientsSyncRequestSize = HeaderSize + 8;
    private const int IngredientsSnapshotChunkFixedSize = HeaderSize + 26;
    private const int IngredientsDeltaFixedSize = HeaderSize + 18;
    private const int IngredientCountSize = 12;
    private const int RoomPacketSize = HeaderSize + 9;
    private const int DiveReadyPacketSize = HeaderSize + 5;
    private const int DiveStatePacketSize = HeaderSize + 6;
    private const int BoatDecoStatePacketSize = HeaderSize + 4;
    private const int DiverLifeStatePacketSize = HeaderSize + 5;
    private const int DiveExitRequestPacketSize = HeaderSize + 4;
    private const int TravelReadyPacketSize = HeaderSize + 7;
    private const int TravelStatePacketSize = HeaderSize + 7;
    private const int DiveLootRequestPacketSize = HeaderSize + 17;
    private const int DiveResultEntryPacketSize = HeaderSize + 28;
    private const int DiveResultStatePacketSize = HeaderSize + 10;
    private const int MissionStateFixedSize = HeaderSize + 5;
    private const int MissionConditionStateSize = 8;
    private const int CampaignSnapshotChunkFixedSize = HeaderSize + 18;
    private const int CampaignSnapshotAckSize = HeaderSize + 12;
    internal const int MaxIngredientEntriesPerPacket = 97;
    internal const int MaxIngredientSnapshotChunks = 256;
    internal const int MaxIngredientPlaces = 32;
    internal const int MaxCampaignSnapshotChunks = 2048;
    internal const int MaxCampaignSnapshotPayloadBytes = 1172;
    internal const int MaxMissionConditions = 32;
    private const int MaxFishAllocatorUidBytes = byte.MaxValue;
    private const int MaxPlayerNameCharacters = 24;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static byte[] Encode(PacketType type, uint sequence)
    {
        var packet = new byte[HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, Magic);
        packet[4] = Version;
        packet[5] = (byte)type;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(6), sequence);
        return packet;
    }

    internal static bool TryDecode(ReadOnlySpan<byte> packet, out PacketType type, out uint sequence)
    {
        type = default;
        sequence = 0;

        if (packet.Length < HeaderSize ||
            BinaryPrimitives.ReadUInt32LittleEndian(packet) != Magic ||
            packet[4] != Version ||
            !Enum.IsDefined(typeof(PacketType), packet[5]))
            return false;

        type = (PacketType)packet[5];
        sequence = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(6));
        return true;
    }

    internal static byte[] EncodeSnapshot(uint sequence, PlayerSnapshot snapshot)
    {
        var packet = new byte[SnapshotSize];
        Encode(PacketType.PlayerSnapshot, sequence).CopyTo(packet, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize), snapshot.SceneId);
        WriteSingle(packet.AsSpan(HeaderSize + 4), snapshot.X);
        WriteSingle(packet.AsSpan(HeaderSize + 8), snapshot.Y);
        WriteSingle(packet.AsSpan(HeaderSize + 12), snapshot.Z);
        WriteSingle(packet.AsSpan(HeaderSize + 16), snapshot.Rotation);
        WriteSingle(packet.AsSpan(HeaderSize + 20), snapshot.VelocityX);
        WriteSingle(packet.AsSpan(HeaderSize + 24), snapshot.VelocityY);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 28), snapshot.SpriteId);
        WriteSingle(packet.AsSpan(HeaderSize + 32), snapshot.ScaleX);
        WriteSingle(packet.AsSpan(HeaderSize + 36), snapshot.ScaleY);
        packet[HeaderSize + 40] = snapshot.Flipped ? (byte)1 : (byte)0;
        return packet;
    }

    internal static bool TryDecodeSnapshot(ReadOnlySpan<byte> packet, out uint sequence, out PlayerSnapshot snapshot)
    {
        sequence = 0;
        snapshot = default;
        if (packet.Length != SnapshotSize ||
            !TryDecode(packet, out var type, out sequence) ||
            type != PacketType.PlayerSnapshot)
            return false;

        var x = ReadSingle(packet.Slice(HeaderSize + 4));
        var y = ReadSingle(packet.Slice(HeaderSize + 8));
        var z = ReadSingle(packet.Slice(HeaderSize + 12));
        var rotation = ReadSingle(packet.Slice(HeaderSize + 16));
        var velocityX = ReadSingle(packet.Slice(HeaderSize + 20));
        var velocityY = ReadSingle(packet.Slice(HeaderSize + 24));
        var scaleX = ReadSingle(packet.Slice(HeaderSize + 32));
        var scaleY = ReadSingle(packet.Slice(HeaderSize + 36));
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z) ||
            !float.IsFinite(rotation) || !float.IsFinite(velocityX) || !float.IsFinite(velocityY) ||
            !float.IsFinite(scaleX) || !float.IsFinite(scaleY) ||
            scaleX == 0f || scaleY == 0f ||
            MathF.Abs(x) > 1_000_000f || MathF.Abs(y) > 1_000_000f || MathF.Abs(z) > 1_000_000f ||
            MathF.Abs(velocityX) > 10_000f || MathF.Abs(velocityY) > 10_000f ||
            MathF.Abs(scaleX) > 100f || MathF.Abs(scaleY) > 100f ||
            packet[HeaderSize + 40] > 1)
            return false;

        snapshot = new PlayerSnapshot(
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize)),
            x, y, z, rotation, velocityX, velocityY,
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 28)),
            scaleX, scaleY, packet[HeaderSize + 40] == 1);
        return true;
    }

    internal static byte[] EncodePlayerVisualState(uint sequence, PlayerVisualState state)
    {
        var sprites = state.Sprites ?? Array.Empty<VisualSprite>();
        if (state.SceneId == 0 || sprites.Length > MaxVisualSprites)
            throw new ArgumentOutOfRangeException(nameof(state));
        var packet = new byte[VisualStateFixedSize + sprites.Length * VisualSpriteSize];
        Encode(PacketType.PlayerVisualState, sequence).CopyTo(packet, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize), state.SceneId);
        packet[HeaderSize + 4] = (byte)sprites.Length;
        for (var index = 0; index < sprites.Length; index++)
        {
            var sprite = sprites[index];
            if (!IsValidVisualSprite(sprite))
                throw new ArgumentOutOfRangeException(nameof(state));
            var offset = VisualStateFixedSize + index * VisualSpriteSize;
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(offset), sprite.SpriteId);
            WriteSingle(packet.AsSpan(offset + 4), sprite.OffsetX);
            WriteSingle(packet.AsSpan(offset + 8), sprite.OffsetY);
            WriteSingle(packet.AsSpan(offset + 12), sprite.OffsetZ);
            WriteSingle(packet.AsSpan(offset + 16), sprite.Rotation);
            WriteSingle(packet.AsSpan(offset + 20), sprite.ScaleX);
            WriteSingle(packet.AsSpan(offset + 24), sprite.ScaleY);
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(offset + 28), sprite.SortingLayerId);
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(offset + 32), sprite.SortingOrder);
            packet[offset + 36] = sprite.FlipX ? (byte)1 : (byte)0;
            packet[offset + 37] = sprite.FlipY ? (byte)1 : (byte)0;
        }
        return packet;
    }

    internal static bool TryDecodePlayerVisualState(
        ReadOnlySpan<byte> packet,
        out uint sequence,
        out PlayerVisualState state)
    {
        sequence = 0;
        state = default;
        if (packet.Length < VisualStateFixedSize ||
            !TryDecode(packet, out var type, out sequence) || type != PacketType.PlayerVisualState)
            return false;
        var count = packet[HeaderSize + 4];
        if (count > MaxVisualSprites || packet.Length != VisualStateFixedSize + count * VisualSpriteSize)
            return false;
        var sceneId = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize));
        if (sceneId == 0)
            return false;
        var sprites = new VisualSprite[count];
        for (var index = 0; index < count; index++)
        {
            var offset = VisualStateFixedSize + index * VisualSpriteSize;
            if (packet[offset + 36] > 1 || packet[offset + 37] > 1)
                return false;
            var sprite = new VisualSprite(
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(offset)),
                ReadSingle(packet.Slice(offset + 4)),
                ReadSingle(packet.Slice(offset + 8)),
                ReadSingle(packet.Slice(offset + 12)),
                ReadSingle(packet.Slice(offset + 16)),
                ReadSingle(packet.Slice(offset + 20)),
                ReadSingle(packet.Slice(offset + 24)),
                BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(offset + 28)),
                BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(offset + 32)),
                packet[offset + 36] != 0,
                packet[offset + 37] != 0);
            if (!IsValidVisualSprite(sprite))
                return false;
            sprites[index] = sprite;
        }
        state = new PlayerVisualState(sceneId, sprites);
        return true;
    }

    internal static byte[] EncodeProjectileVisualState(uint sequence, ProjectileVisualState state)
    {
        if (!IsValidProjectileVisualState(state))
            throw new ArgumentOutOfRangeException(nameof(state));
        var packet = new byte[ProjectileVisualStateSize];
        Encode(PacketType.ProjectileVisualState, sequence).CopyTo(packet, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize), state.SceneId);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 4), state.Id);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 8), state.SpriteId);
        WriteSingle(packet.AsSpan(HeaderSize + 12), state.X);
        WriteSingle(packet.AsSpan(HeaderSize + 16), state.Y);
        WriteSingle(packet.AsSpan(HeaderSize + 20), state.Z);
        WriteSingle(packet.AsSpan(HeaderSize + 24), state.Rotation);
        WriteSingle(packet.AsSpan(HeaderSize + 28), state.ScaleX);
        WriteSingle(packet.AsSpan(HeaderSize + 32), state.ScaleY);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 36), state.SortingLayerId);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 40), state.SortingOrder);
        packet[HeaderSize + 44] = state.Flipped ? (byte)1 : (byte)0;
        packet[HeaderSize + 45] = state.HasRope ? (byte)1 : (byte)0;
        WriteSingle(packet.AsSpan(HeaderSize + 46), state.RopeStartX);
        WriteSingle(packet.AsSpan(HeaderSize + 50), state.RopeStartY);
        WriteSingle(packet.AsSpan(HeaderSize + 54), state.RopeStartZ);
        WriteSingle(packet.AsSpan(HeaderSize + 58), state.RopeEndX);
        WriteSingle(packet.AsSpan(HeaderSize + 62), state.RopeEndY);
        WriteSingle(packet.AsSpan(HeaderSize + 66), state.RopeEndZ);
        return packet;
    }

    internal static bool TryDecodeProjectileVisualState(
        ReadOnlySpan<byte> packet,
        out uint sequence,
        out ProjectileVisualState state)
    {
        sequence = 0;
        state = default;
        if (packet.Length != ProjectileVisualStateSize ||
            !TryDecode(packet, out var type, out sequence) || type != PacketType.ProjectileVisualState ||
            packet[HeaderSize + 44] > 1 || packet[HeaderSize + 45] > 1)
            return false;
        var candidate = new ProjectileVisualState(
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize)),
            BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 8)),
            ReadSingle(packet.Slice(HeaderSize + 12)),
            ReadSingle(packet.Slice(HeaderSize + 16)),
            ReadSingle(packet.Slice(HeaderSize + 20)),
            ReadSingle(packet.Slice(HeaderSize + 24)),
            ReadSingle(packet.Slice(HeaderSize + 28)),
            ReadSingle(packet.Slice(HeaderSize + 32)),
            BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 36)),
            BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 40)),
            packet[HeaderSize + 44] != 0,
            packet[HeaderSize + 45] != 0,
            ReadSingle(packet.Slice(HeaderSize + 46)),
            ReadSingle(packet.Slice(HeaderSize + 50)),
            ReadSingle(packet.Slice(HeaderSize + 54)),
            ReadSingle(packet.Slice(HeaderSize + 58)),
            ReadSingle(packet.Slice(HeaderSize + 62)),
            ReadSingle(packet.Slice(HeaderSize + 66)));
        if (!IsValidProjectileVisualState(candidate))
            return false;
        state = candidate;
        return true;
    }

    internal static byte[] EncodeIdentity(PacketType type, uint sequence, uint buildId, string playerName)
    {
        if (type != PacketType.Hello && type != PacketType.HelloAck)
            throw new ArgumentOutOfRangeException(nameof(type));

        var name = NormalizePlayerName(playerName);
        var encodedName = StrictUtf8.GetBytes(name);
        var packet = new byte[HeaderSize + 5 + encodedName.Length];
        Encode(type, sequence).CopyTo(packet, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize), buildId);
        packet[HeaderSize + 4] = (byte)encodedName.Length;
        encodedName.CopyTo(packet, HeaderSize + 5);
        return packet;
    }

    internal static bool TryDecodeIdentity(
        ReadOnlySpan<byte> packet,
        PacketType expectedType,
        out uint sequence,
        out uint buildId,
        out string playerName)
    {
        sequence = 0;
        buildId = 0;
        playerName = string.Empty;
        if (!TryDecode(packet, out var type, out sequence) || type != expectedType ||
            packet.Length < HeaderSize + 5 ||
            packet.Length != HeaderSize + 5 + packet[HeaderSize + 4])
            return false;

        try
        {
            buildId = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize));
            playerName = NormalizePlayerName(StrictUtf8.GetString(packet.Slice(HeaderSize + 5)));
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    internal static byte[] EncodeSceneState(uint sequence, uint sceneId)
    {
        var packet = new byte[HeaderSize + 4];
        Encode(PacketType.SceneState, sequence).CopyTo(packet, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize), sceneId);
        return packet;
    }

    internal static bool TryDecodeSceneState(ReadOnlySpan<byte> packet, out uint sequence, out uint sceneId)
    {
        sequence = 0;
        sceneId = 0;
        if (packet.Length != HeaderSize + 4 ||
            !TryDecode(packet, out var type, out sequence) || type != PacketType.SceneState)
            return false;
        sceneId = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize));
        return true;
    }

    internal static byte[] EncodeFishSnapshot(uint sequence, FishSnapshot snapshot)
    {
        var packet = new byte[FishSnapshotSize];
        Encode(PacketType.FishSnapshot, sequence).CopyTo(packet, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize), snapshot.SceneId);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 4), snapshot.Id);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 8), snapshot.FishDataTID);
        WriteSingle(packet.AsSpan(HeaderSize + 12), snapshot.X);
        WriteSingle(packet.AsSpan(HeaderSize + 16), snapshot.Y);
        WriteSingle(packet.AsSpan(HeaderSize + 20), snapshot.Z);
        WriteSingle(packet.AsSpan(HeaderSize + 24), snapshot.Rotation);
        WriteSingle(packet.AsSpan(HeaderSize + 28), snapshot.Hp);
        packet[HeaderSize + 32] = snapshot.Flags;
        return packet;
    }

    internal static bool TryDecodeFishSnapshot(
        ReadOnlySpan<byte> packet,
        out uint sequence,
        out FishSnapshot snapshot)
    {
        sequence = 0;
        snapshot = default;
        if (packet.Length != FishSnapshotSize ||
            !TryDecode(packet, out var type, out sequence) || type != PacketType.FishSnapshot)
            return false;

        var sceneId = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize));
        var id = BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 4));
        var fishDataTID = BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 8));
        var x = ReadSingle(packet.Slice(HeaderSize + 12));
        var y = ReadSingle(packet.Slice(HeaderSize + 16));
        var z = ReadSingle(packet.Slice(HeaderSize + 20));
        var rotation = ReadSingle(packet.Slice(HeaderSize + 24));
        var hp = ReadSingle(packet.Slice(HeaderSize + 28));
        var flags = packet[HeaderSize + 32];
        if (sceneId == 0 || id <= 0 || fishDataTID <= 0 ||
            !float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z) ||
            !float.IsFinite(rotation) || !float.IsFinite(hp) || hp < 0f || hp > 1_000_000_000f ||
            MathF.Abs(x) > 1_000_000f ||
            MathF.Abs(y) > 1_000_000f || MathF.Abs(z) > 1_000_000f || flags > 7)
            return false;

        snapshot = new FishSnapshot(sceneId, id, fishDataTID, x, y, z, rotation, hp, flags);
        return true;
    }

    internal static byte[] EncodeFishDamageRequest(uint sequence, FishDamageRequest request)
    {
        if (request.Damage is < 1 or > 10_000 || request.Element is < 0 or > 32 ||
            request.AttackType is < 0 or > 64)
            throw new ArgumentOutOfRangeException(nameof(request));
        var packet = new byte[FishDamageRequestSize];
        Encode(PacketType.FishDamageRequest, sequence).CopyTo(packet, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize), request.SceneId);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 4), request.Id);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 8), request.Damage);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 12), request.Element);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 16), request.AttackType);
        return packet;
    }

    internal static bool TryDecodeFishDamageRequest(
        ReadOnlySpan<byte> packet,
        out uint sequence,
        out FishDamageRequest request)
    {
        sequence = 0;
        request = default;
        if (packet.Length != FishDamageRequestSize ||
            !TryDecode(packet, out var type, out sequence) || type != PacketType.FishDamageRequest)
            return false;
        var damage = BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 8));
        var element = BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 12));
        var attackType = BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 16));
        if (damage is < 1 or > 10_000 || element is < 0 or > 32 || attackType is < 0 or > 64)
            return false;
        request = new FishDamageRequest(
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize)),
            BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 4)),
            damage, element, attackType);
        return true;
    }

    internal static byte[] EncodeFishPickupRequest(uint sequence, FishPickupRequest request)
    {
        var packet = new byte[FishPickupRequestSize];
        Encode(PacketType.FishPickupRequest, sequence).CopyTo(packet, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize), request.SceneId);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 4), request.Id);
        return packet;
    }

    internal static bool TryDecodeFishPickupRequest(
        ReadOnlySpan<byte> packet,
        out uint sequence,
        out FishPickupRequest request)
    {
        sequence = 0;
        request = default;
        if (packet.Length != FishPickupRequestSize ||
            !TryDecode(packet, out var type, out sequence) || type != PacketType.FishPickupRequest)
            return false;
        request = new FishPickupRequest(
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize)),
            BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 4)));
        return true;
    }

    internal static byte[] EncodeFishRemoved(uint sequence, FishRemoved removed)
    {
        var packet = new byte[FishRemovedSize];
        Encode(PacketType.FishRemoved, sequence).CopyTo(packet, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize), removed.SceneId);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 4), removed.Id);
        return packet;
    }

    internal static bool TryDecodeFishRemoved(
        ReadOnlySpan<byte> packet,
        out uint sequence,
        out FishRemoved removed)
    {
        sequence = 0;
        removed = default;
        if (packet.Length != FishRemovedSize ||
            !TryDecode(packet, out var type, out sequence) || type != PacketType.FishRemoved)
            return false;
        removed = new FishRemoved(
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize)),
            BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 4)));
        return true;
    }

    internal static byte[] EncodeFishManifest(uint sequence, FishManifest manifest)
    {
        var uid = StrictUtf8.GetBytes(manifest.AllocatorUid ?? string.Empty);
        if (!IsValidFishManifest(manifest, uid.Length))
            throw new ArgumentOutOfRangeException(nameof(manifest));

        var packet = new byte[FishManifestFixedSize + uid.Length];
        Encode(PacketType.FishManifest, sequence).CopyTo(packet, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize), manifest.SceneId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 4), manifest.Revision);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 8), manifest.Id);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 12), manifest.FishDataTID);
        WriteSingle(packet.AsSpan(HeaderSize + 16), manifest.X);
        WriteSingle(packet.AsSpan(HeaderSize + 20), manifest.Y);
        WriteSingle(packet.AsSpan(HeaderSize + 24), manifest.Z);
        WriteSingle(packet.AsSpan(HeaderSize + 28), manifest.Rotation);
        WriteSingle(packet.AsSpan(HeaderSize + 32), manifest.Hp);
        packet[HeaderSize + 36] = manifest.Flags;
        packet[HeaderSize + 37] = (byte)uid.Length;
        uid.CopyTo(packet, FishManifestFixedSize);
        return packet;
    }

    internal static bool TryDecodeFishManifest(
        ReadOnlySpan<byte> packet,
        out uint sequence,
        out FishManifest manifest)
    {
        sequence = 0;
        manifest = default;
        if (packet.Length < FishManifestFixedSize ||
            !TryDecode(packet, out var type, out sequence) || type != PacketType.FishManifest)
            return false;

        var uidLength = packet[HeaderSize + 37];
        if (uidLength == 0 || uidLength > MaxFishAllocatorUidBytes ||
            packet.Length != FishManifestFixedSize + uidLength)
            return false;

        try
        {
            var candidate = new FishManifest(
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize)),
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 4)),
                BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 8)),
                StrictUtf8.GetString(packet.Slice(FishManifestFixedSize, uidLength)),
                BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 12)),
                ReadSingle(packet.Slice(HeaderSize + 16)),
                ReadSingle(packet.Slice(HeaderSize + 20)),
                ReadSingle(packet.Slice(HeaderSize + 24)),
                ReadSingle(packet.Slice(HeaderSize + 28)),
                ReadSingle(packet.Slice(HeaderSize + 32)),
                packet[HeaderSize + 36]);
            if (!IsValidFishManifest(candidate, uidLength))
                return false;
            manifest = candidate;
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    internal static byte[] EncodeFishManifestState(uint sequence, FishManifestState state)
    {
        if (state.SceneId == 0 || state.Revision == 0)
            throw new ArgumentOutOfRangeException(nameof(state));
        var packet = new byte[FishManifestStateSize];
        Encode(PacketType.FishManifestState, sequence).CopyTo(packet, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize), state.SceneId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 4), state.Revision);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(HeaderSize + 8), state.EntryCount);
        return packet;
    }

    internal static bool TryDecodeFishManifestState(
        ReadOnlySpan<byte> packet,
        out uint sequence,
        out FishManifestState state)
    {
        sequence = 0;
        state = default;
        if (packet.Length != FishManifestStateSize ||
            !TryDecode(packet, out var type, out sequence) || type != PacketType.FishManifestState)
            return false;
        var sceneId = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize));
        var revision = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 4));
        if (sceneId == 0 || revision == 0)
            return false;
        state = new FishManifestState(
            sceneId, revision,
            BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(HeaderSize + 8)));
        return true;
    }

    internal static byte[] EncodePickupRemoved(uint sequence, PickupRemoved removed)
    {
        var packet = new byte[PickupRemovedSize];
        Encode(PacketType.PickupRemoved, sequence).CopyTo(packet, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize), removed.SceneId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 4), removed.WorldId);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 8), removed.ItemId);
        return packet;
    }

    internal static bool TryDecodePickupRemoved(
        ReadOnlySpan<byte> packet,
        out uint sequence,
        out PickupRemoved removed)
    {
        sequence = 0;
        removed = default;
        if (packet.Length != PickupRemovedSize ||
            !TryDecode(packet, out var type, out sequence) || type != PacketType.PickupRemoved)
            return false;
        removed = new PickupRemoved(
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize)),
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 4)),
            BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 8)));
        return true;
    }

    internal static byte[] EncodePickupRequest(uint sequence, PickupRemoved request)
    {
        var packet = EncodePickupRemoved(sequence, request);
        packet[5] = (byte)PacketType.PickupRequest;
        return packet;
    }

    internal static bool TryDecodePickupRequest(
        ReadOnlySpan<byte> packet,
        out uint sequence,
        out PickupRemoved request)
    {
        sequence = 0;
        request = default;
        if (packet.Length != PickupRemovedSize ||
            !TryDecode(packet, out var type, out sequence) || type != PacketType.PickupRequest)
            return false;
        request = new PickupRemoved(
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize)),
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 4)),
            BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 8)));
        return true;
    }

    internal static byte[] EncodeSceneTransition(uint sequence, SceneTransitionCommand command)
    {
        var sceneName = command.SceneName ?? string.Empty;
        var encodedName = StrictUtf8.GetBytes(sceneName);
        if (encodedName.Length is < 1 or > 128 || (command.Options & 0x7e00) != 0)
            throw new ArgumentOutOfRangeException(nameof(command));
        var packet = new byte[HeaderSize + 7 + encodedName.Length];
        Encode(PacketType.SceneTransition, sequence).CopyTo(packet, 0);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize), command.TransitionType);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(HeaderSize + 4), command.Options);
        packet[HeaderSize + 6] = (byte)encodedName.Length;
        encodedName.CopyTo(packet, HeaderSize + 7);
        return packet;
    }

    internal static bool TryDecodeSceneTransition(
        ReadOnlySpan<byte> packet,
        out uint sequence,
        out SceneTransitionCommand command)
    {
        sequence = 0;
        command = default;
        if (!TryDecode(packet, out var type, out sequence) || type != PacketType.SceneTransition ||
            packet.Length < HeaderSize + 8 || packet[HeaderSize + 6] is < 1 or > 128 ||
            packet.Length != HeaderSize + 7 + packet[HeaderSize + 6])
            return false;
        var options = BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(HeaderSize + 4));
        if ((options & 0x7e00) != 0)
            return false;
        try
        {
            var sceneName = StrictUtf8.GetString(packet.Slice(HeaderSize + 7));
            if (string.IsNullOrWhiteSpace(sceneName))
                return false;
            command = new SceneTransitionCommand(
                sceneName,
                BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize)),
                options);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    internal static byte[] EncodeIngredientsSyncRequest(
        uint sequence,
        IngredientsSyncRequest request)
    {
        if (request.RequestId == 0)
            throw new ArgumentOutOfRangeException(nameof(request));
        var packet = new byte[IngredientsSyncRequestSize];
        Encode(PacketType.IngredientsSyncRequest, sequence).CopyTo(packet, 0);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(HeaderSize), request.RequestId);
        return packet;
    }

    internal static byte[] EncodeRoomReady(uint sequence, RoomReady ready) =>
        EncodeRoomPacket(PacketType.RoomReady, sequence, ready.RoomId, ready.Revision, ready.Ready);

    internal static bool TryDecodeRoomReady(
        ReadOnlySpan<byte> packet,
        out uint sequence,
        out RoomReady ready)
    {
        ready = default;
        if (!TryDecodeRoomPacket(packet, PacketType.RoomReady, out sequence, out var roomId, out var revision, out var value))
            return false;
        ready = new RoomReady(roomId, revision, value);
        return true;
    }

    internal static byte[] EncodeRoomState(uint sequence, RoomState state) =>
        EncodeRoomPacket(PacketType.RoomState, sequence, state.RoomId, state.Revision, state.ClientReady);

    internal static bool TryDecodeRoomState(
        ReadOnlySpan<byte> packet,
        out uint sequence,
        out RoomState state)
    {
        state = default;
        if (!TryDecodeRoomPacket(packet, PacketType.RoomState, out sequence, out var roomId, out var revision, out var value))
            return false;
        state = new RoomState(roomId, revision, value);
        return true;
    }

    internal static byte[] EncodeDiveReady(uint sequence, DiveReady ready)
    {
        if (ready.Revision == 0)
            throw new ArgumentOutOfRangeException(nameof(ready));
        var packet = new byte[DiveReadyPacketSize];
        Encode(PacketType.DiveReady, sequence).CopyTo(packet, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize), ready.Revision);
        packet[HeaderSize + 4] = ready.Ready ? (byte)1 : (byte)0;
        return packet;
    }

    internal static bool TryDecodeDiveReady(ReadOnlySpan<byte> packet, out uint sequence, out DiveReady ready)
    {
        sequence = 0;
        ready = default;
        if (packet.Length != DiveReadyPacketSize ||
            !TryDecode(packet, out var type, out sequence) || type != PacketType.DiveReady ||
            packet[HeaderSize + 4] > 1)
            return false;
        var revision = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize));
        if (revision == 0)
            return false;
        ready = new DiveReady(revision, packet[HeaderSize + 4] == 1);
        return true;
    }

    internal static byte[] EncodeDiveState(uint sequence, DiveState state)
    {
        if (state.Revision == 0)
            throw new ArgumentOutOfRangeException(nameof(state));
        var packet = new byte[DiveStatePacketSize];
        Encode(PacketType.DiveState, sequence).CopyTo(packet, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize), state.Revision);
        packet[HeaderSize + 4] = state.HostReady ? (byte)1 : (byte)0;
        packet[HeaderSize + 5] = state.ClientReady ? (byte)1 : (byte)0;
        return packet;
    }

    internal static bool TryDecodeDiveState(ReadOnlySpan<byte> packet, out uint sequence, out DiveState state)
    {
        sequence = 0;
        state = default;
        if (packet.Length != DiveStatePacketSize ||
            !TryDecode(packet, out var type, out sequence) || type != PacketType.DiveState ||
            packet[HeaderSize + 4] > 1 || packet[HeaderSize + 5] > 1)
            return false;
        var revision = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize));
        if (revision == 0)
            return false;
        state = new DiveState(revision, packet[HeaderSize + 4] == 1, packet[HeaderSize + 5] == 1);
        return true;
    }

    internal static byte[] EncodeBoatDecoState(uint sequence, BoatDecoState state)
    {
        var packet = new byte[BoatDecoStatePacketSize];
        Encode(PacketType.BoatDecoState, sequence).CopyTo(packet, 0);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize), state.Id);
        return packet;
    }

    internal static bool TryDecodeBoatDecoState(
        ReadOnlySpan<byte> packet,
        out uint sequence,
        out BoatDecoState state)
    {
        sequence = 0;
        state = default;
        if (packet.Length != BoatDecoStatePacketSize ||
            !TryDecode(packet, out var type, out sequence) || type != PacketType.BoatDecoState)
            return false;
        state = new BoatDecoState(BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize)));
        return true;
    }

    internal static byte[] EncodeDiverLifeState(uint sequence, DiverLifeState state)
    {
        if (state.Revision == 0)
            throw new ArgumentOutOfRangeException(nameof(state));
        var packet = new byte[DiverLifeStatePacketSize];
        Encode(PacketType.DiverLifeState, sequence).CopyTo(packet, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize), state.Revision);
        packet[HeaderSize + 4] = state.IsDead ? (byte)1 : (byte)0;
        return packet;
    }

    internal static bool TryDecodeDiverLifeState(
        ReadOnlySpan<byte> packet,
        out uint sequence,
        out DiverLifeState state)
    {
        sequence = 0;
        state = default;
        if (packet.Length != DiverLifeStatePacketSize ||
            !TryDecode(packet, out var type, out sequence) || type != PacketType.DiverLifeState ||
            packet[HeaderSize + 4] > 1)
            return false;
        var revision = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize));
        if (revision == 0)
            return false;
        state = new DiverLifeState(revision, packet[HeaderSize + 4] == 1);
        return true;
    }

    internal static byte[] EncodeDiveExitRequest(uint sequence, DiveExitRequest request)
    {
        if (request.Revision == 0)
            throw new ArgumentOutOfRangeException(nameof(request));
        var packet = new byte[DiveExitRequestPacketSize];
        Encode(PacketType.DiveExitRequest, sequence).CopyTo(packet, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize), request.Revision);
        return packet;
    }

    internal static bool TryDecodeDiveExitRequest(
        ReadOnlySpan<byte> packet,
        out uint sequence,
        out DiveExitRequest request)
    {
        sequence = 0;
        request = default;
        if (packet.Length != DiveExitRequestPacketSize ||
            !TryDecode(packet, out var type, out sequence) || type != PacketType.DiveExitRequest)
            return false;
        var revision = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize));
        if (revision == 0)
            return false;
        request = new DiveExitRequest(revision);
        return true;
    }

    internal static byte[] EncodeTravelReady(uint sequence, TravelReady ready)
    {
        if (!Enum.IsDefined(typeof(TravelTarget), ready.Target) || ready.Revision == 0 ||
            (!ready.Ready && ready.NativeStarted))
            throw new ArgumentOutOfRangeException(nameof(ready));
        var packet = new byte[TravelReadyPacketSize];
        Encode(PacketType.TravelReady, sequence).CopyTo(packet, 0);
        packet[HeaderSize] = (byte)ready.Target;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 1), ready.Revision);
        packet[HeaderSize + 5] = ready.Ready ? (byte)1 : (byte)0;
        packet[HeaderSize + 6] = ready.NativeStarted ? (byte)1 : (byte)0;
        return packet;
    }

    internal static bool TryDecodeTravelReady(
        ReadOnlySpan<byte> packet,
        out uint sequence,
        out TravelReady ready)
    {
        sequence = 0;
        ready = default;
        if (packet.Length != TravelReadyPacketSize ||
            !TryDecode(packet, out var type, out sequence) || type != PacketType.TravelReady ||
            !Enum.IsDefined(typeof(TravelTarget), packet[HeaderSize]) ||
            packet[HeaderSize + 5] > 1 || packet[HeaderSize + 6] > 1 ||
            (packet[HeaderSize + 5] == 0 && packet[HeaderSize + 6] == 1))
            return false;
        var revision = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 1));
        if (revision == 0)
            return false;
        ready = new TravelReady(
            (TravelTarget)packet[HeaderSize], revision,
            packet[HeaderSize + 5] == 1, packet[HeaderSize + 6] == 1);
        return true;
    }

    internal static byte[] EncodeTravelState(uint sequence, TravelState state)
    {
        if (!Enum.IsDefined(typeof(TravelTarget), state.Target) || state.Revision == 0)
            throw new ArgumentOutOfRangeException(nameof(state));
        var packet = new byte[TravelStatePacketSize];
        Encode(PacketType.TravelState, sequence).CopyTo(packet, 0);
        packet[HeaderSize] = (byte)state.Target;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 1), state.Revision);
        packet[HeaderSize + 5] = state.HostReady ? (byte)1 : (byte)0;
        packet[HeaderSize + 6] = state.ClientReady ? (byte)1 : (byte)0;
        return packet;
    }

    internal static bool TryDecodeTravelState(
        ReadOnlySpan<byte> packet,
        out uint sequence,
        out TravelState state)
    {
        sequence = 0;
        state = default;
        if (packet.Length != TravelStatePacketSize ||
            !TryDecode(packet, out var type, out sequence) || type != PacketType.TravelState ||
            !Enum.IsDefined(typeof(TravelTarget), packet[HeaderSize]) ||
            packet[HeaderSize + 5] > 1 || packet[HeaderSize + 6] > 1)
            return false;
        var revision = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 1));
        if (revision == 0)
            return false;
        state = new TravelState(
            (TravelTarget)packet[HeaderSize], revision,
            packet[HeaderSize + 5] == 1, packet[HeaderSize + 6] == 1);
        return true;
    }

    internal static byte[] EncodeDiveLootRequest(uint sequence, DiveLootRequest request)
    {
        if (!IsValidLoot(request.ItemId, request.Count, request.BonusGrade, request.LiftType))
            throw new ArgumentOutOfRangeException(nameof(request));
        var packet = new byte[DiveLootRequestPacketSize];
        Encode(PacketType.DiveLootRequest, sequence).CopyTo(packet, 0);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize), request.ItemId);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 4), request.Count);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 8), request.BonusGrade);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 12), request.LiftType);
        packet[HeaderSize + 16] = request.UpdateMission ? (byte)1 : (byte)0;
        return packet;
    }

    internal static bool TryDecodeDiveLootRequest(
        ReadOnlySpan<byte> packet,
        out uint sequence,
        out DiveLootRequest request)
    {
        sequence = 0;
        request = default;
        if (packet.Length != DiveLootRequestPacketSize ||
            !TryDecode(packet, out var type, out sequence) || type != PacketType.DiveLootRequest ||
            packet[HeaderSize + 16] > 1)
            return false;
        request = new DiveLootRequest(
            BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize)),
            BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 4)),
            BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 8)),
            BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 12)),
            packet[HeaderSize + 16] == 1);
        return IsValidLoot(request.ItemId, request.Count, request.BonusGrade, request.LiftType);
    }

    internal static byte[] EncodeDiveResultEntry(uint sequence, DiveResultEntry entry)
    {
        if (entry.TransferId == 0 || entry.Total == 0 || entry.Index >= entry.Total ||
            !IsValidLoot(entry.ItemId, entry.Count, entry.BonusGrade, entry.LiftType))
            throw new ArgumentOutOfRangeException(nameof(entry));
        var packet = new byte[DiveResultEntryPacketSize];
        Encode(PacketType.DiveResultEntry, sequence).CopyTo(packet, 0);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(HeaderSize), entry.TransferId);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(HeaderSize + 8), entry.Index);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(HeaderSize + 10), entry.Total);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 12), entry.ItemId);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 16), entry.Count);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 20), entry.BonusGrade);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 24), entry.LiftType);
        return packet;
    }

    internal static bool TryDecodeDiveResultEntry(
        ReadOnlySpan<byte> packet,
        out uint sequence,
        out DiveResultEntry entry)
    {
        sequence = 0;
        entry = default;
        if (packet.Length != DiveResultEntryPacketSize ||
            !TryDecode(packet, out var type, out sequence) || type != PacketType.DiveResultEntry)
            return false;
        entry = new DiveResultEntry(
            BinaryPrimitives.ReadUInt64LittleEndian(packet.Slice(HeaderSize)),
            BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(HeaderSize + 8)),
            BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(HeaderSize + 10)),
            BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 12)),
            BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 16)),
            BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 20)),
            BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 24)));
        return entry.TransferId != 0 && entry.Total > 0 && entry.Index < entry.Total &&
            IsValidLoot(entry.ItemId, entry.Count, entry.BonusGrade, entry.LiftType);
    }

    internal static byte[] EncodeDiveResultState(uint sequence, DiveResultState state)
    {
        if (state.TransferId == 0 || state.Total > 200)
            throw new ArgumentOutOfRangeException(nameof(state));
        var packet = new byte[DiveResultStatePacketSize];
        Encode(PacketType.DiveResultState, sequence).CopyTo(packet, 0);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(HeaderSize), state.TransferId);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(HeaderSize + 8), state.Total);
        return packet;
    }

    internal static bool TryDecodeDiveResultState(
        ReadOnlySpan<byte> packet,
        out uint sequence,
        out DiveResultState state)
    {
        sequence = 0;
        state = default;
        if (packet.Length != DiveResultStatePacketSize ||
            !TryDecode(packet, out var type, out sequence) || type != PacketType.DiveResultState)
            return false;
        state = new DiveResultState(
            BinaryPrimitives.ReadUInt64LittleEndian(packet.Slice(HeaderSize)),
            BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(HeaderSize + 8)));
        return state.TransferId != 0 && state.Total <= 200;
    }

    internal static byte[] EncodeMissionState(uint sequence, MissionState state)
    {
        var conditions = state.Conditions ?? throw new ArgumentNullException(nameof(state));
        if (state.Revision == 0 || conditions.Length > MaxMissionConditions ||
            !AreValidMissionConditions(conditions))
            throw new ArgumentOutOfRangeException(nameof(state));
        var packet = new byte[MissionStateFixedSize + conditions.Length * MissionConditionStateSize];
        Encode(PacketType.MissionState, sequence).CopyTo(packet, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize), state.Revision);
        packet[HeaderSize + 4] = (byte)conditions.Length;
        for (var index = 0; index < conditions.Length; index++)
        {
            var offset = MissionStateFixedSize + index * MissionConditionStateSize;
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(offset), conditions[index].Id);
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(offset + 4), conditions[index].Count);
        }
        return packet;
    }

    internal static bool TryDecodeMissionState(
        ReadOnlySpan<byte> packet,
        out uint sequence,
        out MissionState state)
    {
        sequence = 0;
        state = default;
        if (packet.Length < MissionStateFixedSize ||
            !TryDecode(packet, out var type, out sequence) || type != PacketType.MissionState)
            return false;
        var count = packet[HeaderSize + 4];
        if (count > MaxMissionConditions ||
            packet.Length != MissionStateFixedSize + count * MissionConditionStateSize)
            return false;
        var revision = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize));
        if (revision == 0)
            return false;
        var conditions = new MissionConditionState[count];
        for (var index = 0; index < count; index++)
        {
            var offset = MissionStateFixedSize + index * MissionConditionStateSize;
            conditions[index] = new MissionConditionState(
                BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(offset)),
                BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(offset + 4)));
        }
        if (!AreValidMissionConditions(conditions))
            return false;
        state = new MissionState(revision, conditions);
        return true;
    }

    internal static byte[] EncodeCampaignSnapshotChunk(uint sequence, CampaignSnapshotChunk chunk)
    {
        var payload = chunk.Payload ?? throw new ArgumentNullException(nameof(chunk.Payload));
        if (chunk.TransferId == 0 || chunk.Revision == 0 ||
            chunk.ChunkCount is < 1 or > MaxCampaignSnapshotChunks ||
            chunk.ChunkIndex >= chunk.ChunkCount ||
            payload.Length is < 1 or > MaxCampaignSnapshotPayloadBytes)
            throw new ArgumentOutOfRangeException(nameof(chunk));
        var packet = new byte[CampaignSnapshotChunkFixedSize + payload.Length];
        Encode(PacketType.CampaignSnapshotChunk, sequence).CopyTo(packet, 0);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(HeaderSize), chunk.TransferId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 8), chunk.Revision);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(HeaderSize + 12), chunk.ChunkIndex);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(HeaderSize + 14), chunk.ChunkCount);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(HeaderSize + 16), (ushort)payload.Length);
        payload.CopyTo(packet.AsSpan(CampaignSnapshotChunkFixedSize));
        return packet;
    }

    internal static bool TryDecodeCampaignSnapshotChunk(
        ReadOnlySpan<byte> packet,
        out uint sequence,
        out CampaignSnapshotChunk chunk)
    {
        sequence = 0;
        chunk = default;
        if (packet.Length < CampaignSnapshotChunkFixedSize ||
            !TryDecode(packet, out var type, out sequence) || type != PacketType.CampaignSnapshotChunk)
            return false;
        var payloadLength = BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(HeaderSize + 16));
        if (payloadLength is < 1 or > MaxCampaignSnapshotPayloadBytes ||
            packet.Length != CampaignSnapshotChunkFixedSize + payloadLength)
            return false;
        var transferId = BinaryPrimitives.ReadUInt64LittleEndian(packet.Slice(HeaderSize));
        var revision = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 8));
        var chunkIndex = BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(HeaderSize + 12));
        var chunkCount = BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(HeaderSize + 14));
        if (transferId == 0 || revision == 0 ||
            chunkCount is < 1 or > MaxCampaignSnapshotChunks || chunkIndex >= chunkCount)
            return false;
        chunk = new CampaignSnapshotChunk(
            transferId, revision, chunkIndex, chunkCount,
            packet.Slice(CampaignSnapshotChunkFixedSize, payloadLength).ToArray());
        return true;
    }

    internal static byte[] EncodeCampaignSnapshotAck(uint sequence, CampaignSnapshotAck acknowledgement)
    {
        if (acknowledgement.TransferId == 0 || acknowledgement.Revision == 0)
            throw new ArgumentOutOfRangeException(nameof(acknowledgement));
        var packet = new byte[CampaignSnapshotAckSize];
        Encode(PacketType.CampaignSnapshotAck, sequence).CopyTo(packet, 0);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(HeaderSize), acknowledgement.TransferId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 8), acknowledgement.Revision);
        return packet;
    }

    internal static bool TryDecodeCampaignSnapshotAck(
        ReadOnlySpan<byte> packet,
        out uint sequence,
        out CampaignSnapshotAck acknowledgement)
    {
        sequence = 0;
        acknowledgement = default;
        if (packet.Length != CampaignSnapshotAckSize ||
            !TryDecode(packet, out var type, out sequence) || type != PacketType.CampaignSnapshotAck)
            return false;
        var transferId = BinaryPrimitives.ReadUInt64LittleEndian(packet.Slice(HeaderSize));
        var revision = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 8));
        if (transferId == 0 || revision == 0)
            return false;
        acknowledgement = new CampaignSnapshotAck(transferId, revision);
        return true;
    }

    internal static bool TryDecodeIngredientsSyncRequest(
        ReadOnlySpan<byte> packet,
        out uint sequence,
        out IngredientsSyncRequest request)
    {
        sequence = 0;
        request = default;
        if (packet.Length != IngredientsSyncRequestSize ||
            !TryDecode(packet, out var type, out sequence) ||
            type != PacketType.IngredientsSyncRequest)
            return false;
        var requestId = BinaryPrimitives.ReadUInt64LittleEndian(packet.Slice(HeaderSize));
        if (requestId == 0)
            return false;
        request = new IngredientsSyncRequest(requestId);
        return true;
    }

    internal static byte[] EncodeIngredientsSnapshotChunk(
        uint sequence,
        IngredientsSnapshotChunk chunk)
    {
        var entries = chunk.Entries ?? throw new ArgumentNullException(nameof(chunk.Entries));
        if (chunk.RequestId == 0 || chunk.HostEpoch == 0 ||
            chunk.ChunkCount is < 1 or > MaxIngredientSnapshotChunks ||
            chunk.ChunkIndex >= chunk.ChunkCount ||
            !AreIngredientEntriesValid(entries, allowEmpty: chunk.ChunkCount == 1))
            throw new ArgumentOutOfRangeException(nameof(chunk));

        var packet = new byte[IngredientsSnapshotChunkFixedSize + entries.Length * IngredientCountSize];
        Encode(PacketType.IngredientsSnapshotChunk, sequence).CopyTo(packet, 0);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(HeaderSize), chunk.RequestId);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(HeaderSize + 8), chunk.HostEpoch);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 16), chunk.Revision);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(HeaderSize + 20), chunk.ChunkIndex);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(HeaderSize + 22), chunk.ChunkCount);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(HeaderSize + 24), (ushort)entries.Length);
        WriteIngredientEntries(packet.AsSpan(IngredientsSnapshotChunkFixedSize), entries);
        return packet;
    }

    internal static bool TryDecodeIngredientsSnapshotChunk(
        ReadOnlySpan<byte> packet,
        out uint sequence,
        out IngredientsSnapshotChunk chunk)
    {
        sequence = 0;
        chunk = default;
        if (packet.Length < IngredientsSnapshotChunkFixedSize ||
            !TryDecode(packet, out var type, out sequence) ||
            type != PacketType.IngredientsSnapshotChunk)
            return false;

        var entryCount = BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(HeaderSize + 24));
        if (entryCount > MaxIngredientEntriesPerPacket ||
            packet.Length != IngredientsSnapshotChunkFixedSize + entryCount * IngredientCountSize)
            return false;
        var requestId = BinaryPrimitives.ReadUInt64LittleEndian(packet.Slice(HeaderSize));
        var hostEpoch = BinaryPrimitives.ReadUInt64LittleEndian(packet.Slice(HeaderSize + 8));
        var revision = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 16));
        var chunkIndex = BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(HeaderSize + 20));
        var chunkCount = BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(HeaderSize + 22));
        if (requestId == 0 || hostEpoch == 0 ||
            chunkCount is < 1 or > MaxIngredientSnapshotChunks || chunkIndex >= chunkCount ||
            (entryCount == 0 && (chunkIndex != 0 || chunkCount != 1)) ||
            !TryReadIngredientEntries(
                packet.Slice(IngredientsSnapshotChunkFixedSize), entryCount, out var entries))
            return false;

        chunk = new IngredientsSnapshotChunk(
            requestId, hostEpoch, revision, chunkIndex, chunkCount, entries);
        return true;
    }

    internal static byte[] EncodeIngredientsDelta(uint sequence, IngredientsDelta delta)
    {
        var entries = delta.Entries ?? throw new ArgumentNullException(nameof(delta.Entries));
        if (delta.HostEpoch == 0 || !IsNewer(delta.Revision, delta.BaseRevision) ||
            !AreIngredientEntriesValid(entries, allowEmpty: false))
            throw new ArgumentOutOfRangeException(nameof(delta));

        var packet = new byte[IngredientsDeltaFixedSize + entries.Length * IngredientCountSize];
        Encode(PacketType.IngredientsDelta, sequence).CopyTo(packet, 0);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(HeaderSize), delta.HostEpoch);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 8), delta.BaseRevision);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 12), delta.Revision);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(HeaderSize + 16), (ushort)entries.Length);
        WriteIngredientEntries(packet.AsSpan(IngredientsDeltaFixedSize), entries);
        return packet;
    }

    internal static bool TryDecodeIngredientsDelta(
        ReadOnlySpan<byte> packet,
        out uint sequence,
        out IngredientsDelta delta)
    {
        sequence = 0;
        delta = default;
        if (packet.Length < IngredientsDeltaFixedSize ||
            !TryDecode(packet, out var type, out sequence) || type != PacketType.IngredientsDelta)
            return false;

        var entryCount = BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(HeaderSize + 16));
        if (entryCount is < 1 or > MaxIngredientEntriesPerPacket ||
            packet.Length != IngredientsDeltaFixedSize + entryCount * IngredientCountSize)
            return false;
        var hostEpoch = BinaryPrimitives.ReadUInt64LittleEndian(packet.Slice(HeaderSize));
        var baseRevision = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 8));
        var revision = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 12));
        if (hostEpoch == 0 || !IsNewer(revision, baseRevision) ||
            !TryReadIngredientEntries(packet.Slice(IngredientsDeltaFixedSize), entryCount, out var entries))
            return false;
        delta = new IngredientsDelta(hostEpoch, baseRevision, revision, entries);
        return true;
    }

    internal static string NormalizePlayerName(string playerName)
    {
        var normalized = (playerName ?? string.Empty).Trim();
        if (normalized.Length == 0)
            return "Diver";
        return normalized.Length <= MaxPlayerNameCharacters
            ? normalized
            : normalized.Substring(0, MaxPlayerNameCharacters);
    }

    internal static uint SceneId(string scene)
    {
        var hash = 2166136261u;
        foreach (var character in scene)
        {
            hash ^= character;
            hash *= 16777619;
        }
        return hash;
    }

    private static bool AreIngredientEntriesValid(IngredientCount[] entries, bool allowEmpty)
    {
        if (entries.Length > MaxIngredientEntriesPerPacket || (!allowEmpty && entries.Length == 0))
            return false;
        var keys = new HashSet<long>();
        foreach (var entry in entries)
        {
            if (entry.IngredientId <= 0 || entry.Place is < 0 or >= MaxIngredientPlaces ||
                entry.Count < 0 || !keys.Add(((long)entry.IngredientId << 32) | (uint)entry.Place))
                return false;
        }
        return true;
    }

    private static bool IsValidFishManifest(FishManifest manifest, int uidByteLength) =>
        manifest.SceneId != 0 && manifest.Revision > 0 && manifest.Id > 0 && manifest.FishDataTID > 0 &&
        uidByteLength is > 0 and <= MaxFishAllocatorUidBytes &&
        !string.IsNullOrWhiteSpace(manifest.AllocatorUid) &&
        manifest.AllocatorUid.IndexOf('\0') < 0 &&
        float.IsFinite(manifest.X) && float.IsFinite(manifest.Y) &&
        float.IsFinite(manifest.Z) && float.IsFinite(manifest.Rotation) &&
        float.IsFinite(manifest.Hp) && manifest.Hp is >= 0f and <= 1_000_000_000f &&
        MathF.Abs(manifest.X) <= 1_000_000f && MathF.Abs(manifest.Y) <= 1_000_000f &&
        MathF.Abs(manifest.Z) <= 1_000_000f && manifest.Flags <= 7;

    private static bool IsValidVisualSprite(VisualSprite sprite) =>
        sprite.SpriteId != 0 && float.IsFinite(sprite.OffsetX) && float.IsFinite(sprite.OffsetY) &&
        float.IsFinite(sprite.OffsetZ) && float.IsFinite(sprite.Rotation) &&
        float.IsFinite(sprite.ScaleX) && float.IsFinite(sprite.ScaleY) &&
        MathF.Abs(sprite.OffsetX) <= 1_000_000f && MathF.Abs(sprite.OffsetY) <= 1_000_000f &&
        MathF.Abs(sprite.OffsetZ) <= 1_000_000f && sprite.ScaleX is > 0f and <= 100f &&
        sprite.ScaleY is > 0f and <= 100f;

    private static bool IsValidProjectileVisualState(ProjectileVisualState state) =>
        state.SceneId != 0 && state.Id != 0 && IsValidVisualSprite(new VisualSprite(
            state.SpriteId, state.X, state.Y, state.Z, state.Rotation, state.ScaleX, state.ScaleY,
            state.SortingLayerId, state.SortingOrder, state.Flipped, false)) &&
        (!state.HasRope ||
            float.IsFinite(state.RopeStartX) && float.IsFinite(state.RopeStartY) &&
            float.IsFinite(state.RopeStartZ) && float.IsFinite(state.RopeEndX) &&
            float.IsFinite(state.RopeEndY) && float.IsFinite(state.RopeEndZ) &&
            MathF.Abs(state.RopeStartX) <= 1_000_000f &&
            MathF.Abs(state.RopeStartY) <= 1_000_000f &&
            MathF.Abs(state.RopeStartZ) <= 1_000_000f &&
            MathF.Abs(state.RopeEndX) <= 1_000_000f &&
            MathF.Abs(state.RopeEndY) <= 1_000_000f &&
            MathF.Abs(state.RopeEndZ) <= 1_000_000f);

    private static bool IsValidLoot(int itemId, int count, int bonusGrade, int liftType) =>
        itemId > 0 && count is > 0 and <= 9_999 && bonusGrade is >= 0 and <= 100 &&
        liftType is >= 0 and <= 3;

    private static byte[] EncodeRoomPacket(
        PacketType type,
        uint sequence,
        uint roomId,
        uint revision,
        bool value)
    {
        if (roomId == 0 || revision == 0)
            throw new ArgumentOutOfRangeException(nameof(revision));
        var packet = new byte[RoomPacketSize];
        Encode(type, sequence).CopyTo(packet, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize), roomId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 4), revision);
        packet[HeaderSize + 8] = value ? (byte)1 : (byte)0;
        return packet;
    }

    private static bool TryDecodeRoomPacket(
        ReadOnlySpan<byte> packet,
        PacketType expectedType,
        out uint sequence,
        out uint roomId,
        out uint revision,
        out bool value)
    {
        sequence = 0;
        roomId = 0;
        revision = 0;
        value = false;
        if (packet.Length != RoomPacketSize ||
            !TryDecode(packet, out var type, out sequence) || type != expectedType ||
            packet[HeaderSize + 8] > 1)
            return false;
        roomId = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize));
        revision = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 4));
        if (roomId == 0 || revision == 0)
            return false;
        value = packet[HeaderSize + 8] == 1;
        return true;
    }

    private static void WriteIngredientEntries(Span<byte> target, IngredientCount[] entries)
    {
        for (var index = 0; index < entries.Length; index++)
        {
            var offset = index * IngredientCountSize;
            BinaryPrimitives.WriteInt32LittleEndian(target.Slice(offset), entries[index].IngredientId);
            BinaryPrimitives.WriteInt32LittleEndian(target.Slice(offset + 4), entries[index].Place);
            BinaryPrimitives.WriteInt32LittleEndian(target.Slice(offset + 8), entries[index].Count);
        }
    }

    private static bool TryReadIngredientEntries(
        ReadOnlySpan<byte> source,
        int count,
        out IngredientCount[] entries)
    {
        entries = new IngredientCount[count];
        var keys = new HashSet<long>();
        for (var index = 0; index < count; index++)
        {
            var offset = index * IngredientCountSize;
            var ingredientId = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(offset));
            var place = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(offset + 4));
            var value = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(offset + 8));
            if (ingredientId <= 0 || place is < 0 or >= MaxIngredientPlaces || value < 0 ||
                !keys.Add(((long)ingredientId << 32) | (uint)place))
            {
                entries = Array.Empty<IngredientCount>();
                return false;
            }
            entries[index] = new IngredientCount(ingredientId, place, value);
        }
        return true;
    }

    private static bool AreValidMissionConditions(MissionConditionState[] conditions)
    {
        var ids = new HashSet<int>();
        foreach (var condition in conditions)
            if (condition.Id <= 0 || condition.Count < 0 || !ids.Add(condition.Id))
                return false;
        return true;
    }

    private static bool IsNewer(uint value, uint previous) =>
        unchecked((int)(value - previous)) > 0;

    internal static void SelfTest()
    {
        var packet = Encode(PacketType.Hello, 42);
        if (!TryDecode(packet, out var type, out var sequence) ||
            type != PacketType.Hello || sequence != 42)
            throw new InvalidOperationException("Protocol round-trip failed");

        var ack = Encode(PacketType.Ack, 41);
        if (!TryDecode(ack, out type, out sequence) || type != PacketType.Ack || sequence != 41)
            throw new InvalidOperationException("Protocol acknowledgement round-trip failed");

        packet[0] ^= 0xff;
        if (TryDecode(packet, out _, out _))
            throw new InvalidOperationException("Protocol accepted invalid magic");

        var identityPacket = EncodeIdentity(PacketType.Hello, 43, 0x12345678, "Дайвер");
        if (!TryDecodeIdentity(identityPacket, PacketType.Hello, out sequence, out var buildId, out var playerName) ||
            sequence != 43 || buildId != 0x12345678 || playerName != "Дайвер")
            throw new InvalidOperationException("Identity round-trip failed");
        identityPacket[^1] = 0xff;
        if (TryDecodeIdentity(identityPacket, PacketType.Hello, out _, out _, out _))
            throw new InvalidOperationException("Protocol accepted invalid UTF-8 identity");

        var scenePacket = EncodeSceneState(44, SceneId("A02_01_01"));
        if (!TryDecodeSceneState(scenePacket, out sequence, out var sceneId) ||
            sequence != 44 || sceneId != SceneId("A02_01_01"))
            throw new InvalidOperationException("Scene state round-trip failed");

        var expectedFish = new FishSnapshot(
            SceneId("A02_01_01"), 17, 2501, 1.25f, -2.5f, -0.1f, 183f, 42.5f, 5);
        var fishPacket = EncodeFishSnapshot(45, expectedFish);
        if (!TryDecodeFishSnapshot(fishPacket, out sequence, out var actualFish) ||
            sequence != 45 || actualFish != expectedFish)
            throw new InvalidOperationException("Fish snapshot round-trip failed");
        fishPacket[HeaderSize + 32] = 8;
        if (TryDecodeFishSnapshot(fishPacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted invalid fish flags");

        var expectedDamage = new FishDamageRequest(SceneId("A02_01_01"), 17, 23, 2, 4);
        var damagePacket = EncodeFishDamageRequest(46, expectedDamage);
        if (!TryDecodeFishDamageRequest(damagePacket, out sequence, out var actualDamage) ||
            sequence != 46 || actualDamage != expectedDamage)
            throw new InvalidOperationException("Fish damage request round-trip failed");
        BinaryPrimitives.WriteInt32LittleEndian(damagePacket.AsSpan(HeaderSize + 8), -1);
        if (TryDecodeFishDamageRequest(damagePacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted invalid fish damage");

        var expectedFishPickup = new FishPickupRequest(SceneId("A02_01_01"), 17);
        var fishPickupPacket = EncodeFishPickupRequest(47, expectedFishPickup);
        if (!TryDecodeFishPickupRequest(fishPickupPacket, out sequence, out var actualFishPickup) ||
            sequence != 47 || actualFishPickup != expectedFishPickup)
            throw new InvalidOperationException("Fish pickup request round-trip failed");

        var expectedFishRemoved = new FishRemoved(SceneId("A02_01_01"), 17);
        var fishRemovedPacket = EncodeFishRemoved(48, expectedFishRemoved);
        if (!TryDecodeFishRemoved(fishRemovedPacket, out sequence, out var actualFishRemoved) ||
            sequence != 48 || actualFishRemoved != expectedFishRemoved)
            throw new InvalidOperationException("Fish removal round-trip failed");

        var expectedFishManifest = new FishManifest(
            SceneId("A02_01_01"), 3, 17, "A02/FishAllocator/3", 2501,
            1.25f, -2.5f, -0.1f, 183f, 42.5f, 5);
        var fishManifestPacket = EncodeFishManifest(49, expectedFishManifest);
        if (!TryDecodeFishManifest(fishManifestPacket, out sequence, out var actualFishManifest) ||
            sequence != 49 || actualFishManifest != expectedFishManifest)
            throw new InvalidOperationException("Fish manifest round-trip failed");
        fishManifestPacket[HeaderSize + 37] = 0;
        if (TryDecodeFishManifest(fishManifestPacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted an empty fish allocator UID");

        var expectedManifestState = new FishManifestState(SceneId("A02_01_01"), 3, 17);
        var manifestStatePacket = EncodeFishManifestState(50, expectedManifestState);
        if (!TryDecodeFishManifestState(manifestStatePacket, out sequence, out var actualManifestState) ||
            sequence != 50 || actualManifestState != expectedManifestState)
            throw new InvalidOperationException("Fish manifest state round-trip failed");

        var expectedPickup = new PickupRemoved(SceneId("A02_01_01"), 0xCAFEBABE, 1001);
        var pickupPacket = EncodePickupRemoved(50, expectedPickup);
        if (!TryDecodePickupRemoved(pickupPacket, out sequence, out var actualPickup) ||
            sequence != 50 || actualPickup != expectedPickup)
            throw new InvalidOperationException("Pickup removal round-trip failed");
        var pickupRequest = EncodePickupRequest(50, expectedPickup);
        if (!TryDecodePickupRequest(pickupRequest, out sequence, out actualPickup) ||
            sequence != 50 || actualPickup != expectedPickup)
            throw new InvalidOperationException("Pickup request round-trip failed");

        var expectedTransition = new SceneTransitionCommand("A02_02_01", 3, 0x8155);
        var transitionPacket = EncodeSceneTransition(51, expectedTransition);
        if (!TryDecodeSceneTransition(transitionPacket, out sequence, out var actualTransition) ||
            sequence != 51 || actualTransition != expectedTransition)
            throw new InvalidOperationException("Scene transition round-trip failed");

        var syncRequest = new IngredientsSyncRequest(0x0102030405060708UL);
        var syncPacket = EncodeIngredientsSyncRequest(52, syncRequest);
        if (!TryDecodeIngredientsSyncRequest(syncPacket, out sequence, out var actualSyncRequest) ||
            sequence != 52 || actualSyncRequest != syncRequest)
            throw new InvalidOperationException("Ingredients sync request round-trip failed");
        BinaryPrimitives.WriteUInt64LittleEndian(syncPacket.AsSpan(HeaderSize), 0);
        if (TryDecodeIngredientsSyncRequest(syncPacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted a zero ingredients request ID");

        var roomReadyPacket = EncodeRoomReady(59, new RoomReady(7, 1, true));
        if (!TryDecodeRoomReady(roomReadyPacket, out sequence, out var roomReady) ||
            sequence != 59 || roomReady != new RoomReady(7, 1, true))
            throw new InvalidOperationException("Room ready round-trip failed");
        roomReadyPacket[HeaderSize + 8] = 2;
        if (TryDecodeRoomReady(roomReadyPacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted invalid room ready state");

        var roomStatePacket = EncodeRoomState(60, new RoomState(7, 1, false));
        if (!TryDecodeRoomState(roomStatePacket, out sequence, out var roomState) ||
            sequence != 60 || roomState != new RoomState(7, 1, false))
            throw new InvalidOperationException("Room state round-trip failed");

        var diveReadyPacket = EncodeDiveReady(61, new DiveReady(4, true));
        if (!TryDecodeDiveReady(diveReadyPacket, out sequence, out var diveReady) ||
            sequence != 61 || diveReady != new DiveReady(4, true))
            throw new InvalidOperationException("Dive ready round-trip failed");
        diveReadyPacket[HeaderSize + 4] = 2;
        if (TryDecodeDiveReady(diveReadyPacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted invalid dive ready state");

        var diveStatePacket = EncodeDiveState(62, new DiveState(5, true, false));
        if (!TryDecodeDiveState(diveStatePacket, out sequence, out var diveState) ||
            sequence != 62 || diveState != new DiveState(5, true, false))
            throw new InvalidOperationException("Dive state round-trip failed");
        diveStatePacket[HeaderSize + 5] = 2;
        if (TryDecodeDiveState(diveStatePacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted invalid dive state");

        var boatDecoPacket = EncodeBoatDecoState(63, new BoatDecoState(0));
        if (!TryDecodeBoatDecoState(boatDecoPacket, out sequence, out var boatDeco) ||
            sequence != 63 || boatDeco != new BoatDecoState(0))
            throw new InvalidOperationException("Boat decoration round-trip failed");

        var lifePacket = EncodeDiverLifeState(64, new DiverLifeState(3, true));
        if (!TryDecodeDiverLifeState(lifePacket, out sequence, out var lifeState) ||
            sequence != 64 || lifeState != new DiverLifeState(3, true))
            throw new InvalidOperationException("Diver life state round-trip failed");
        lifePacket[HeaderSize + 4] = 2;
        if (TryDecodeDiverLifeState(lifePacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted invalid diver life state");

        var exitPacket = EncodeDiveExitRequest(65, new DiveExitRequest(4));
        if (!TryDecodeDiveExitRequest(exitPacket, out sequence, out var exitRequest) ||
            sequence != 65 || exitRequest != new DiveExitRequest(4))
            throw new InvalidOperationException("Dive exit request round-trip failed");
        BinaryPrimitives.WriteUInt32LittleEndian(exitPacket.AsSpan(HeaderSize), 0);
        if (TryDecodeDiveExitRequest(exitPacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted a zero dive exit revision");

        var travelReadyPacket = EncodeTravelReady(
            66, new TravelReady(TravelTarget.SushiBar, 5, true, false));
        if (!TryDecodeTravelReady(travelReadyPacket, out sequence, out var travelReady) ||
            sequence != 66 || travelReady != new TravelReady(TravelTarget.SushiBar, 5, true, false))
            throw new InvalidOperationException("Travel ready round-trip failed");
        travelReadyPacket[HeaderSize + 6] = 2;
        if (TryDecodeTravelReady(travelReadyPacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted invalid travel readiness");

        var travelStatePacket = EncodeTravelState(
            67, new TravelState(TravelTarget.Lobby, 6, true, false));
        if (!TryDecodeTravelState(travelStatePacket, out sequence, out var travelState) ||
            sequence != 67 || travelState != new TravelState(TravelTarget.Lobby, 6, true, false))
            throw new InvalidOperationException("Travel state round-trip failed");

        var lootRequestPacket = EncodeDiveLootRequest(
            68, new DiveLootRequest(101, 2, 1, 0, true));
        if (!TryDecodeDiveLootRequest(lootRequestPacket, out sequence, out var lootRequest) ||
            sequence != 68 || lootRequest != new DiveLootRequest(101, 2, 1, 0, true))
            throw new InvalidOperationException("Dive loot request round-trip failed");
        BinaryPrimitives.WriteInt32LittleEndian(lootRequestPacket.AsSpan(HeaderSize + 4), -1);
        if (TryDecodeDiveLootRequest(lootRequestPacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted invalid dive loot count");

        var resultEntryPacket = EncodeDiveResultEntry(
            69, new DiveResultEntry(7, 0, 1, 101, 2, 1, 0));
        if (!TryDecodeDiveResultEntry(resultEntryPacket, out sequence, out var resultEntry) ||
            sequence != 69 || resultEntry != new DiveResultEntry(7, 0, 1, 101, 2, 1, 0))
            throw new InvalidOperationException("Dive result entry round-trip failed");

        var resultStatePacket = EncodeDiveResultState(70, new DiveResultState(7, 1));
        if (!TryDecodeDiveResultState(resultStatePacket, out sequence, out var resultState) ||
            sequence != 70 || resultState != new DiveResultState(7, 1))
            throw new InvalidOperationException("Dive result state round-trip failed");

        var expectedMissionState = new MissionState(8, new[]
        {
            new MissionConditionState(101, 6), new MissionConditionState(102, 7)
        });
        var missionStatePacket = EncodeMissionState(71, expectedMissionState);
        if (!TryDecodeMissionState(missionStatePacket, out sequence, out var missionState) ||
            sequence != 71 || !SameMissionState(missionState, expectedMissionState))
            throw new InvalidOperationException("Mission state round-trip failed");
        BinaryPrimitives.WriteInt32LittleEndian(missionStatePacket.AsSpan(MissionStateFixedSize), 0);
        if (TryDecodeMissionState(missionStatePacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted invalid mission state");

        var ingredientEntries = new[]
        {
            new IngredientCount(101, 0, 3),
            new IngredientCount(101, 1, 7),
            new IngredientCount(202, 0, 0)
        };
        var expectedIngredientChunk = new IngredientsSnapshotChunk(
            0x0102030405060708UL, 0x1112131415161718UL, 9, 0, 1, ingredientEntries);
        var ingredientChunkPacket = EncodeIngredientsSnapshotChunk(53, expectedIngredientChunk);
        if (!TryDecodeIngredientsSnapshotChunk(
                ingredientChunkPacket, out sequence, out var actualIngredientChunk) ||
            sequence != 53 || !SameIngredientChunk(actualIngredientChunk, expectedIngredientChunk))
            throw new InvalidOperationException("Ingredients snapshot chunk round-trip failed");
        BinaryPrimitives.WriteUInt16LittleEndian(
            ingredientChunkPacket.AsSpan(HeaderSize + 22), 0);
        if (TryDecodeIngredientsSnapshotChunk(ingredientChunkPacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted invalid ingredients chunk bounds");
        ingredientChunkPacket = EncodeIngredientsSnapshotChunk(54, expectedIngredientChunk);
        BinaryPrimitives.WriteInt32LittleEndian(
            ingredientChunkPacket.AsSpan(IngredientsSnapshotChunkFixedSize + IngredientCountSize), 101);
        BinaryPrimitives.WriteInt32LittleEndian(
            ingredientChunkPacket.AsSpan(IngredientsSnapshotChunkFixedSize + IngredientCountSize + 4), 0);
        if (TryDecodeIngredientsSnapshotChunk(ingredientChunkPacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted duplicate ingredient entries");

        var maximumEntries = new IngredientCount[MaxIngredientEntriesPerPacket];
        for (var index = 0; index < maximumEntries.Length; index++)
            maximumEntries[index] = new IngredientCount(index + 1, 0, index);
        ingredientChunkPacket = EncodeIngredientsSnapshotChunk(
            55,
            new IngredientsSnapshotChunk(1, 2, 10, 0, 1, maximumEntries));
        if (ingredientChunkPacket.Length != 1200 ||
            !TryDecodeIngredientsSnapshotChunk(ingredientChunkPacket, out _, out _))
            throw new InvalidOperationException("Ingredients snapshot datagram boundary failed");

        var expectedIngredientDelta = new IngredientsDelta(
            0x1112131415161718UL, 9, 10, ingredientEntries);
        var ingredientDeltaPacket = EncodeIngredientsDelta(56, expectedIngredientDelta);
        if (!TryDecodeIngredientsDelta(
                ingredientDeltaPacket, out sequence, out var actualIngredientDelta) ||
            sequence != 56 || !SameIngredientDelta(actualIngredientDelta, expectedIngredientDelta))
            throw new InvalidOperationException("Ingredients delta round-trip failed");
        BinaryPrimitives.WriteUInt32LittleEndian(
            ingredientDeltaPacket.AsSpan(HeaderSize + 12), expectedIngredientDelta.BaseRevision);
        if (TryDecodeIngredientsDelta(ingredientDeltaPacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted a non-advancing ingredients revision");
        ingredientDeltaPacket = EncodeIngredientsDelta(57, expectedIngredientDelta);
        BinaryPrimitives.WriteInt32LittleEndian(
            ingredientDeltaPacket.AsSpan(IngredientsDeltaFixedSize + 8), -1);
        if (TryDecodeIngredientsDelta(ingredientDeltaPacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted a negative ingredient count");

        var wrappedDelta = new IngredientsDelta(
            5, uint.MaxValue, 0, new[] { new IngredientCount(1, 0, 1) });
        if (!TryDecodeIngredientsDelta(
                EncodeIngredientsDelta(58, wrappedDelta), out _, out actualIngredientDelta) ||
            !SameIngredientDelta(actualIngredientDelta, wrappedDelta))
            throw new InvalidOperationException("Ingredients revision wrap-around failed");

        var expected = new PlayerSnapshot(
            SceneId("A02_01_01"), -12.5f, 3.25f, -0.05f, 91.5f, 2.25f, -0.75f,
            SceneId("Dave_Swim_0042"), -1.25f, 1.25f, true);
        var snapshotPacket = EncodeSnapshot(43, expected);
        if (!TryDecodeSnapshot(snapshotPacket, out sequence, out var actual) ||
            sequence != 43 || actual != expected)
            throw new InvalidOperationException("Snapshot round-trip failed");

        var expectedVisualState = new PlayerVisualState(
            SceneId("A02_01_01"),
            new[] { new VisualSprite(SceneId("Dave_Swim_0042"), 0.25f, -0.5f, 0f, 90f,
                1f, 1.25f, 7, 12, true, false) });
        var visualStatePacket = EncodePlayerVisualState(44, expectedVisualState);
        if (!TryDecodePlayerVisualState(visualStatePacket, out sequence, out var actualVisualState) ||
            sequence != 44 || actualVisualState.SceneId != expectedVisualState.SceneId ||
            actualVisualState.Sprites.Length != 1 || actualVisualState.Sprites[0] != expectedVisualState.Sprites[0])
            throw new InvalidOperationException("Player visual state round-trip failed");
        visualStatePacket[^1] = 2;
        if (TryDecodePlayerVisualState(visualStatePacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted invalid visual sprite flags");

        var expectedProjectileVisual = new ProjectileVisualState(
            SceneId("A02_01_01"), 31, SceneId("Harpoon"), 1.25f, -2.5f, 0f, 45f,
            1f, 1f, 7, 14, false, true, 0f, 0f, 0f, 1.25f, -2.5f, 0f);
        var projectileVisualPacket = EncodeProjectileVisualState(45, expectedProjectileVisual);
        if (!TryDecodeProjectileVisualState(projectileVisualPacket, out sequence, out var actualProjectileVisual) ||
            sequence != 45 || actualProjectileVisual != expectedProjectileVisual)
            throw new InvalidOperationException("Projectile visual round-trip failed");
        WriteSingle(snapshotPacket.AsSpan(HeaderSize + 16), float.NaN);
        if (TryDecodeSnapshot(snapshotPacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted invalid movement data");
        snapshotPacket = EncodeSnapshot(44, expected);
        WriteSingle(snapshotPacket.AsSpan(HeaderSize + 32), 0f);
        if (TryDecodeSnapshot(snapshotPacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted invalid visual scale");
        snapshotPacket = EncodeSnapshot(45, expected);
        WriteSingle(snapshotPacket.AsSpan(HeaderSize + 4), float.MaxValue);
        if (TryDecodeSnapshot(snapshotPacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted unsafe transform data");
    }

    private static bool SameIngredientChunk(
        IngredientsSnapshotChunk left,
        IngredientsSnapshotChunk right) =>
        left.RequestId == right.RequestId && left.HostEpoch == right.HostEpoch &&
        left.Revision == right.Revision && left.ChunkIndex == right.ChunkIndex &&
        left.ChunkCount == right.ChunkCount && SameIngredientEntries(left.Entries, right.Entries);

    private static bool SameIngredientDelta(IngredientsDelta left, IngredientsDelta right) =>
        left.HostEpoch == right.HostEpoch && left.BaseRevision == right.BaseRevision &&
        left.Revision == right.Revision && SameIngredientEntries(left.Entries, right.Entries);

    private static bool SameMissionState(MissionState left, MissionState right)
    {
        if (left.Revision != right.Revision || left.Conditions == null || right.Conditions == null ||
            left.Conditions.Length != right.Conditions.Length)
            return false;
        for (var index = 0; index < left.Conditions.Length; index++)
            if (left.Conditions[index] != right.Conditions[index])
                return false;
        return true;
    }

    private static bool SameIngredientEntries(IngredientCount[] left, IngredientCount[] right)
    {
        if (left == null || right == null || left.Length != right.Length)
            return false;
        for (var index = 0; index < left.Length; index++)
        {
            if (left[index] != right[index])
                return false;
        }
        return true;
    }

    private static void WriteSingle(Span<byte> target, float value) =>
        BinaryPrimitives.WriteInt32LittleEndian(target, BitConverter.SingleToInt32Bits(value));

    private static float ReadSingle(ReadOnlySpan<byte> source) =>
        BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(source));
}
