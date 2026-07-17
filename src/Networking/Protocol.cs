using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace DaveTheDiverMP;

internal enum PacketType : byte
{
    Hello = 1,
    HelloAck = 2,
    Disconnect = 4,
    PlayerSnapshot = 5,
    SceneState = 6,
    FishSnapshotBatch = 7,
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
    MissionState = 36,
    WorldFlagRequest = 37,
    WorldFlagState = 38,
    BossDamageRequest = 39,
    BossState = 40,
    ManagerEvent = 41,
    SushiResultState = 42,
    MissionRoster = 43,
    FishPickupResult = 44,
    SceneSeed = 45,
    CargoState = 46,
    NpcInteraction = 47,
    FishLifecycle = 48,
    FishActionRequest = 49,
    FishActionAck = 50,
    FishLootGrant = 51,
    FishLootComplete = 52,
    FishHookPose = 53,
    SaveSnapshotChunk = 54,
    SaveSnapshotAck = 55,
    PickupResult = 56,
    DiverRuntimeState = 57,
    DiverVitalResult = 58,
    DiverWeaponIntent = 59,
    DiverWeaponResult = 60,
    DiverVitalIntent = 61
}

internal enum HandshakeRejectReason : byte
{
    IncompatibleProtocol = 1,
    IncompatibleBuild = 2
}

internal readonly record struct PlayerSnapshot(
    uint SceneId,
    uint SceneEpoch,
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

internal enum DiverOwner : byte
{
    Host = 1,
    Client = 2
}

[Flags]
internal enum DiverRuntimeFields : ushort
{
    None = 0,
    Health = 1 << 0,
    Oxygen = 1 << 1,
    Cargo = 1 << 2,
    Weapon = 1 << 3,
    Ammo = 1 << 4
}

[Flags]
internal enum DiverRuntimeFlags : ushort
{
    None = 0,
    Invulnerable = 1 << 0,
    OxygenDepleting = 1 << 1,
    Overweight = 1 << 2,
    Reviving = 1 << 3
}

internal readonly record struct DiverRuntimeState(
    uint SceneId,
    uint SceneEpoch,
    uint Revision,
    DiverOwner Owner,
    bool IsDead,
    DiverRuntimeFields Fields,
    DiverRuntimeFlags Flags,
    float Hp,
    float MaxHp,
    float Oxygen,
    float MaxOxygen,
    float CargoWeight,
    int WeaponId,
    int Ammo,
    int MaxAmmo = 0);

internal enum DiverVitalCause : byte
{
    Damage = 1,
    Heal = 2,
    OxygenRestore = 3,
    OxygenDepleted = 4,
    Revive = 5
}

[Flags]
internal enum DiverVitalEdges : byte
{
    None = 0,
    Damaged = 1 << 0,
    Healed = 1 << 1,
    Died = 1 << 2,
    Revived = 1 << 3
}

internal readonly record struct DiverVitalResult(
    uint CommitRevision,
    ulong EventId,
    DiverVitalCause Cause,
    DiverVitalEdges Edges,
    float AppliedAmount,
    DiverRuntimeState State);

internal enum DiverVitalIntentKind : byte
{
    OxygenCapsule = 1,
    Revive = 2
}

internal readonly record struct DiverVitalIntent(
    uint SceneId,
    uint SceneEpoch,
    ulong RequestId,
    DiverVitalIntentKind Kind,
    float Amount);

internal enum DiverWeaponAction : byte
{
    Equip = 1,
    Fire = 2,
    Reload = 3,
    Unequip = 4
}

internal enum DiverWeaponRejectReason : byte
{
    None = 0,
    SceneMismatch = 1,
    StaleRequest = 2,
    InvalidWeapon = 3,
    InvalidState = 4,
    NoAmmo = 5,
    ReloadNotNeeded = 6,
    Dead = 7,
    Unsupported = 8
}

internal readonly record struct DiverWeaponIntent(
    uint SceneId,
    uint SceneEpoch,
    ulong RequestId,
    DiverWeaponAction Action,
    int WeaponId);

internal readonly record struct DiverWeaponResult(
    uint CommitRevision,
    ulong RequestId,
    DiverWeaponAction Action,
    bool Accepted,
    DiverWeaponRejectReason RejectReason,
    int AppliedRounds,
    DiverRuntimeState State);

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

internal readonly record struct PlayerVisualState(
    uint SceneId,
    uint SceneEpoch,
    VisualSprite[] Sprites);

internal readonly record struct ProjectileVisualState(
    uint SceneId,
    uint SceneEpoch,
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
    uint SceneEpoch,
    uint Tick,
    int Id,
    uint Revision,
    int FishDataTID,
    float X,
    float Y,
    float Z,
    float Rotation,
    float VelocityX,
    float VelocityY,
    float Hp,
    byte Flags);

internal readonly record struct FishDamageRequest(
    uint SceneId,
    uint SceneEpoch,
    int Id,
    uint KnownRevision,
    int Damage,
    int Element,
    int AttackType);

internal readonly record struct FishPickupRequest(
    uint SceneId, uint SceneEpoch, int Id, uint KnownRevision);
internal readonly record struct FishPickupResult(
    uint SceneId, uint SceneEpoch, int Id, uint Revision, bool Accepted);
internal readonly record struct FishRemoved(
    uint SceneId, uint SceneEpoch, int Id, uint Revision);

internal readonly record struct FishManifest(
    uint SceneId,
    uint SceneEpoch,
    uint Revision,
    int Id,
    uint FishRevision,
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
    uint SceneEpoch,
    uint Revision,
    ushort EntryCount);

internal enum FishLifecycleKind : byte
{
    Spawn = 1,
    Despawn = 2,
    Phase = 3,
    InterestEnter = 4,
    InterestLeave = 5
}

internal enum FishPhase : byte
{
    None,
    Alive,
    Hooked,
    Qte,
    Captured,
    Corpse
}

internal enum FishAction : byte
{
    Damage = 1,
    Qte = 2,
    Hook = 3,
    Release = 4,
    Capture = 5,
    CorpsePickup = 6
}

internal enum FishActionResult : byte
{
    Accepted = 1,
    Rejected = 2
}

internal enum FishActionRejectReason : byte
{
    None,
    SceneMismatch,
    MissingFish,
    StaleRevision,
    InvalidAction,
    InvalidState,
    OutOfRange,
    Capacity,
    Duplicate,
    InternalError
}

internal readonly record struct FishLifecycle(
    uint SceneId,
    uint SceneEpoch,
    int Id,
    uint Revision,
    FishLifecycleKind Kind,
    int FishDataTID,
    FishPhase Phase,
    float Hp);

internal readonly record struct FishActionRequest(
    ulong RequestId,
    uint SceneId,
    uint SceneEpoch,
    int Id,
    uint KnownRevision,
    ulong LeaseId,
    FishAction Action,
    int Damage,
    int Element,
    int AttackType);

internal readonly record struct FishActionAck(
    ulong RequestId,
    uint SceneId,
    uint SceneEpoch,
    int Id,
    uint Revision,
    FishAction Action,
    FishActionResult Result,
    FishActionRejectReason RejectReason,
    float Hp,
    FishPhase Phase);

internal readonly record struct FishLootGrant(
    ulong TransactionId,
    uint SceneId,
    uint SceneEpoch,
    ulong RequestId,
    int FishId,
    uint Revision,
    FishAction Action,
    ushort Index,
    ushort EntryCount,
    int ItemId,
    int Count,
    int BonusGrade,
    int LiftType,
    float CarriedWeight);

internal readonly record struct FishLootComplete(
    ulong TransactionId,
    uint SceneId,
    uint SceneEpoch,
    ulong RequestId,
    int FishId,
    uint Revision,
    FishAction Action);

internal readonly record struct FishHookPose(
    uint SceneId,
    uint SceneEpoch,
    int FishId,
    ulong LeaseId,
    uint Tick,
    float X,
    float Y,
    float Z,
    float Rotation,
    float VelocityX,
    float VelocityY);

internal readonly record struct SaveSnapshotChunk(
    ulong TransferId,
    uint Fingerprint,
    int TotalBytes,
    ushort ChunkIndex,
    ushort ChunkCount,
    byte[] Data);

internal readonly record struct SaveSnapshotAck(
    ulong TransferId,
    uint Fingerprint,
    bool Loaded);

internal readonly record struct PickupRemoved(
    uint SceneId,
    uint SceneEpoch,
    uint WorldId,
    int ItemId);

internal readonly record struct PickupRequest(
    ulong RequestId,
    uint SceneId,
    uint SceneEpoch,
    uint WorldId,
    int ItemId,
    uint KnownRevision,
    float ExpectedX,
    float ExpectedY);

internal enum PickupRejectReason : byte
{
    None,
    SceneMismatch,
    StalePose,
    MissingEntity,
    ItemMismatch,
    StaleRevision,
    OutOfRange,
    AlreadyClaimed,
    InternalError
}

internal readonly record struct PickupResult(
    ulong RequestId,
    uint SceneId,
    uint SceneEpoch,
    uint WorldId,
    int ItemId,
    uint Revision,
    bool Accepted,
    PickupRejectReason RejectReason);

internal readonly record struct SceneTransitionCommand(
    string SceneName,
    int TransitionType,
    ushort Options,
    uint SceneId,
    uint SceneEpoch,
    int Seed);

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
internal readonly record struct DiveState(
    uint Revision,
    bool HostReady,
    bool ClientReady,
    uint SceneId,
    uint SceneEpoch,
    int Seed);
internal readonly record struct SceneSeed(uint SceneId, uint SceneEpoch, int Seed);
internal readonly record struct CargoState(
    uint SceneId,
    float WeightMax,
    float OverloadedThreshold,
    float WeightParameter,
    uint SceneEpoch = 0);
internal enum NpcInteractionAction : byte
{
    Request = 1,
    Granted = 2,
    Released = 3
}
internal enum NpcInteractionResult : byte
{
    Pending,
    Accepted,
    Busy,
    Invalid,
    MissionOwned,
    TooFar
}
internal readonly record struct NpcInteraction(
    ulong Token,
    uint SceneId,
    uint SceneEpoch,
    uint TargetId,
    uint Revision,
    NpcInteractionAction Action,
    int NpcId,
    int MissionId,
    int TaskId,
    NpcInteractionResult Result);
internal readonly record struct BoatDecoState(int Id);
internal readonly record struct DiverLifeState(uint Revision, bool IsDead);
internal readonly record struct DiveExitRequest(uint Revision);
internal readonly record struct TravelRoute(
    string SceneName,
    int SceneType,
    int Location,
    int TransitionType);
internal readonly record struct TravelReady(
    uint TargetId,
    uint Revision,
    bool Ready,
    bool NativeStarted,
    TravelRoute Route);
internal readonly record struct TravelState(
    uint TargetId,
    uint Revision,
    bool HostReady,
    bool ClientReady,
    bool SoloAllowed,
    bool HostDead);
internal readonly record struct DiveLootRequest(
    ulong SourceId,
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
internal readonly record struct MissionState(
    uint Revision,
    int MissionId,
    int Progress,
    byte State,
    int CurrentTaskId,
    MissionConditionState[] Conditions);
internal readonly record struct MissionRoster(uint Revision, int[] MissionIds);
internal readonly record struct WorldFlagRequest(string Key, bool Value);
internal readonly record struct WorldFlagState(uint Revision, string Key, bool Value);
internal readonly record struct BossDamageRequest(
    uint SceneId,
    uint SceneEpoch,
    uint RequestId,
    uint BossId,
    uint TargetId,
    int Damage,
    int Element,
    int AttackType,
    float HitX,
    float HitY,
    float HitZ);
internal readonly record struct BossState(
    uint SceneId,
    uint SceneEpoch,
    uint Tick,
    uint BossId,
    int FishId,
    int CurrentHp,
    int MaxHp,
    float X,
    float Y,
    float Z,
    int AnimationHash,
    float AnimationTime,
    int Phase,
    byte Flags);
internal enum ManagerInvocationKind : byte
{
    Scenario = 1,
    DialogueNormal = 2,
    DialogueArguments = 3,
    DialogueSmall = 4,
    TimelineByTid = 5
}
internal readonly record struct ManagerInvocationDescriptor(
    ManagerInvocationKind Kind,
    string BundleId,
    string[] Arguments,
    bool UseButton,
    bool ShowCurtain,
    bool IgnorePlaying,
    bool ApplyOffset,
    bool HasCustomPosition,
    float CustomX,
    float CustomY,
    float CustomZ);
internal readonly record struct ManagerEvent(
    uint Revision,
    uint SceneId,
    uint HostTick,
    byte Domain,
    byte Action,
    int Value,
    int Context,
    ManagerInvocationDescriptor? Invocation = null,
    uint SceneEpoch = 0);
internal readonly record struct SushiResultState(
    uint Revision,
    int SalesMenu,
    int SalesEtc,
    int StaffTips,
    int TotalVisits,
    int LikeCount,
    float Rating);
internal static class Protocol
{
    private const uint Magic = 0x504D5444; // DTMP
    private const byte Version = 48;
    internal const int HeaderSize = 18;
    private const int SnapshotSize = HeaderSize + 45;
    private const int DiverRuntimePayloadSize = 50;
    private const int DiverRuntimeStateSize = HeaderSize + DiverRuntimePayloadSize;
    private const int DiverVitalResultSize = HeaderSize + 18 + DiverRuntimePayloadSize;
    private const int DiverVitalIntentSize = HeaderSize + 21;
    private const int DiverWeaponIntentSize = HeaderSize + 21;
    private const int DiverWeaponResultSize = HeaderSize + 19 + DiverRuntimePayloadSize;
    private const int VisualStateFixedSize = HeaderSize + 9;
    private const int VisualSpriteSize = 38;
    private const int ProjectileVisualStateSize = HeaderSize + 74;
    internal const int MaxVisualSprites = 16;
    private const int FishSnapshotBatchFixedSize = HeaderSize + 13;
    private const int FishSnapshotCompactEntrySize = 26;
    private const int FishSnapshotFullEntrySize = 38;
    private const int FishDamageRequestSize = HeaderSize + 28;
    private const int BossDamageRequestSize = HeaderSize + 44;
    private const int BossStateSize = HeaderSize + 53;
    private const int ManagerEventSize = HeaderSize + 26;
    private const int SushiResultStateSize = HeaderSize + 28;
    private const int FishPickupRequestSize = HeaderSize + 16;
    private const int FishPickupResultSize = HeaderSize + 17;
    private const int FishRemovedSize = HeaderSize + 16;
    private const int FishManifestFixedSize = HeaderSize + 46;
    private const int FishManifestStateSize = HeaderSize + 14;
    private const int FishLifecycleSize = HeaderSize + 26;
    private const int FishActionRequestSize = HeaderSize + 45;
    private const int FishActionAckSize = HeaderSize + 32;
    private const int FishLootGrantSize = HeaderSize + 57;
    private const int FishLootCompleteSize = HeaderSize + 33;
    private const int FishHookPoseFixedSize = HeaderSize + 25;
    private const int FishHookPoseCompactSize = FishHookPoseFixedSize + 12;
    private const int FishHookPoseFullSize = FishHookPoseFixedSize + 24;
    private const int SaveSnapshotChunkFixedSize = HeaderSize + 22;
    private const int SaveSnapshotAckSize = HeaderSize + 13;
    private const int PickupRemovedSize = HeaderSize + 16;
    private const int PickupRequestSize = HeaderSize + 36;
    private const int PickupResultSize = HeaderSize + 30;
    private const int IngredientsSyncRequestSize = HeaderSize + 8;
    private const int IngredientsSnapshotChunkFixedSize = HeaderSize + 26;
    private const int IngredientsDeltaFixedSize = HeaderSize + 18;
    private const int IngredientCountSize = 12;
    private const int RoomPacketSize = HeaderSize + 9;
    private const int DiveReadyPacketSize = HeaderSize + 5;
    private const int DiveStatePacketSize = HeaderSize + 18;
    private const int SceneSeedPacketSize = HeaderSize + 12;
    private const int CargoStatePacketSize = HeaderSize + 20;
    private const int NpcInteractionPacketSize = HeaderSize + 38;
    private const int BoatDecoStatePacketSize = HeaderSize + 4;
    private const int DiverLifeStatePacketSize = HeaderSize + 5;
    private const int DiveExitRequestPacketSize = HeaderSize + 4;
    private const int TravelReadyFixedSize = HeaderSize + 24;
    private const int TravelStatePacketSize = HeaderSize + 12;
    private const int DiveLootRequestPacketSize = HeaderSize + 25;
    private const int DiveResultEntryPacketSize = HeaderSize + 28;
    private const int DiveResultStatePacketSize = HeaderSize + 10;
    private const int MissionStateFixedSize = HeaderSize + 18;
    private const int MissionConditionStateSize = 8;
    private const int MissionRosterFixedSize = HeaderSize + 6;
    internal const int MaxIngredientEntriesPerPacket =
        (1200 - IngredientsSnapshotChunkFixedSize) / IngredientCountSize;
    internal const int MaxIngredientSnapshotChunks = 256;
    internal const int MaxIngredientPlaces = 32;
    internal const int MaxMissionConditions = 64;
    internal const int MaxMissionRosterEntries =
        (1200 - MissionRosterFixedSize) / sizeof(int);
    internal const int MaxWorldFlagKeyBytes = 128;
    internal const int MaxTravelSceneNameBytes = 128;
    internal const int MaxDatagramSize = 1200;
    private const int MaxManagerInvocationArguments = 128;
    private const int MaxManagerInvocationStringBytes = 512;
    internal const int MaxSaveSnapshotChunkBytes = MaxDatagramSize - SaveSnapshotChunkFixedSize;
    internal const int MaxSaveSnapshotChunks = 16384;
    internal const int MaxFishSnapshotsPerPacket =
        (MaxDatagramSize - FishSnapshotBatchFixedSize) / FishSnapshotFullEntrySize;
    private const int MaxFishAllocatorUidBytes = byte.MaxValue;
    private const int MaxPlayerNameCharacters = 24;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static byte[] Encode(PacketType type, uint sequence)
    {
        var packet = new byte[HeaderSize];
        WriteHeader(packet, type, sequence);
        return packet;
    }

    private static void WriteHeader(Span<byte> packet, PacketType type, uint sequence)
        => WriteHeader(packet, type, sequence, Version);

    private static void WriteHeader(Span<byte> packet, PacketType type, uint sequence, byte version)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(packet, Magic);
        packet[4] = version;
        packet[5] = (byte)type;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.Slice(6), sequence);
    }

    internal static void SetSessionId(Span<byte> packet, ulong sessionId)
    {
        if (packet.Length < HeaderSize)
            throw new ArgumentException("Packet is shorter than the protocol header", nameof(packet));
        BinaryPrimitives.WriteUInt64LittleEndian(packet.Slice(10), sessionId);
    }

    internal static bool TryDecode(ReadOnlySpan<byte> packet, out PacketType type, out uint sequence)
        => TryDecode(packet, out type, out sequence, out _);

    internal static bool TryDecode(
        ReadOnlySpan<byte> packet,
        out PacketType type,
        out uint sequence,
        out ulong sessionId)
    {
        type = default;
        sequence = 0;
        sessionId = 0;

        if (packet.Length < HeaderSize ||
            BinaryPrimitives.ReadUInt32LittleEndian(packet) != Magic ||
            packet[4] != Version ||
            !Enum.IsDefined(typeof(PacketType), packet[5]))
            return false;

        type = (PacketType)packet[5];
        sequence = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(6));
        sessionId = BinaryPrimitives.ReadUInt64LittleEndian(packet.Slice(10));
        return true;
    }

    internal static byte[] EncodeHandshakeReject(
        uint sequence, HandshakeRejectReason reason, byte peerVersion)
    {
        if (peerVersion == 0)
            throw new ArgumentOutOfRangeException(nameof(peerVersion));
        if (!Enum.IsDefined(typeof(HandshakeRejectReason), reason))
            throw new ArgumentOutOfRangeException(nameof(reason));
        var packet = new byte[HeaderSize + 1];
        WriteHeader(packet, PacketType.Disconnect, sequence, peerVersion);
        packet[HeaderSize] = (byte)reason;
        return packet;
    }

    internal static bool TryDecodeHandshakeReject(
        ReadOnlySpan<byte> packet,
        out uint sequence,
        out ulong sessionId,
        out HandshakeRejectReason reason)
    {
        sequence = 0;
        sessionId = 0;
        reason = default;
        if (packet.Length != HeaderSize + 1 ||
            BinaryPrimitives.ReadUInt32LittleEndian(packet) != Magic ||
            packet[4] == 0 || packet[5] != (byte)PacketType.Disconnect ||
            !Enum.IsDefined(typeof(HandshakeRejectReason), packet[HeaderSize]))
            return false;
        sequence = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(6));
        sessionId = BinaryPrimitives.ReadUInt64LittleEndian(packet.Slice(10));
        reason = (HandshakeRejectReason)packet[HeaderSize];
        return true;
    }

    internal static bool TryDecodeHelloIdentityAnyVersion(
        ReadOnlySpan<byte> packet,
        out byte version,
        out ulong sessionId,
        out uint buildId)
    {
        version = 0;
        sessionId = 0;
        buildId = 0;
        if (packet.Length < HeaderSize + 5 ||
            BinaryPrimitives.ReadUInt32LittleEndian(packet) != Magic ||
            packet[4] == 0 || packet[5] != (byte)PacketType.Hello ||
            packet.Length != HeaderSize + 5 + packet[HeaderSize + 4])
            return false;
        try
        {
            StrictUtf8.GetString(packet.Slice(HeaderSize + 5));
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
        version = packet[4];
        sessionId = BinaryPrimitives.ReadUInt64LittleEndian(packet.Slice(10));
        buildId = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize));
        return sessionId != 0;
    }

    internal static byte[] EncodeSnapshot(uint sequence, PlayerSnapshot snapshot)
    {
        var packet = new byte[SnapshotSize];
        WriteHeader(packet, PacketType.PlayerSnapshot, sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize), snapshot.SceneId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 4), snapshot.SceneEpoch);
        WriteSingle(packet.AsSpan(HeaderSize + 8), snapshot.X);
        WriteSingle(packet.AsSpan(HeaderSize + 12), snapshot.Y);
        WriteSingle(packet.AsSpan(HeaderSize + 16), snapshot.Z);
        WriteSingle(packet.AsSpan(HeaderSize + 20), snapshot.Rotation);
        WriteSingle(packet.AsSpan(HeaderSize + 24), snapshot.VelocityX);
        WriteSingle(packet.AsSpan(HeaderSize + 28), snapshot.VelocityY);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 32), snapshot.SpriteId);
        WriteSingle(packet.AsSpan(HeaderSize + 36), snapshot.ScaleX);
        WriteSingle(packet.AsSpan(HeaderSize + 40), snapshot.ScaleY);
        packet[HeaderSize + 44] = snapshot.Flipped ? (byte)1 : (byte)0;
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

        var sceneEpoch = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 4));
        var x = ReadSingle(packet.Slice(HeaderSize + 8));
        var y = ReadSingle(packet.Slice(HeaderSize + 12));
        var z = ReadSingle(packet.Slice(HeaderSize + 16));
        var rotation = ReadSingle(packet.Slice(HeaderSize + 20));
        var velocityX = ReadSingle(packet.Slice(HeaderSize + 24));
        var velocityY = ReadSingle(packet.Slice(HeaderSize + 28));
        var scaleX = ReadSingle(packet.Slice(HeaderSize + 36));
        var scaleY = ReadSingle(packet.Slice(HeaderSize + 40));
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z) ||
            !float.IsFinite(rotation) || !float.IsFinite(velocityX) || !float.IsFinite(velocityY) ||
            !float.IsFinite(scaleX) || !float.IsFinite(scaleY) ||
            scaleX == 0f || scaleY == 0f ||
            MathF.Abs(x) > 1_000_000f || MathF.Abs(y) > 1_000_000f || MathF.Abs(z) > 1_000_000f ||
            MathF.Abs(velocityX) > 10_000f || MathF.Abs(velocityY) > 10_000f ||
            MathF.Abs(scaleX) > 100f || MathF.Abs(scaleY) > 100f ||
            sceneEpoch == 0 || packet[HeaderSize + 44] > 1)
            return false;

        snapshot = new PlayerSnapshot(
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize)),
            sceneEpoch,
            x, y, z, rotation, velocityX, velocityY,
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 32)),
            scaleX, scaleY, packet[HeaderSize + 44] == 1);
        return true;
    }

    internal static byte[] EncodeDiverRuntimeState(uint sequence, DiverRuntimeState state)
    {
        if (!IsValidDiverRuntimeState(state))
            throw new ArgumentOutOfRangeException(nameof(state));
        var packet = new byte[DiverRuntimeStateSize];
        WriteHeader(packet, PacketType.DiverRuntimeState, sequence);
        WriteDiverRuntimePayload(packet.AsSpan(HeaderSize), state);
        return packet;
    }

    internal static bool TryDecodeDiverRuntimeState(
        ReadOnlySpan<byte> packet,
        out uint sequence,
        out DiverRuntimeState state)
    {
        sequence = 0;
        state = default;
        if (packet.Length != DiverRuntimeStateSize ||
            !TryDecode(packet, out var type, out sequence) || type != PacketType.DiverRuntimeState)
            return false;
        return TryReadDiverRuntimePayload(packet.Slice(HeaderSize), out state);
    }

    internal static byte[] EncodeDiverVitalResult(uint sequence, DiverVitalResult result)
    {
        if (!IsValidDiverVitalResult(result))
            throw new ArgumentOutOfRangeException(nameof(result));
        var packet = new byte[DiverVitalResultSize];
        WriteHeader(packet, PacketType.DiverVitalResult, sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize), result.CommitRevision);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(HeaderSize + 4), result.EventId);
        packet[HeaderSize + 12] = (byte)result.Cause;
        packet[HeaderSize + 13] = (byte)result.Edges;
        WriteSingle(packet.AsSpan(HeaderSize + 14), result.AppliedAmount);
        WriteDiverRuntimePayload(packet.AsSpan(HeaderSize + 18), result.State);
        return packet;
    }

    internal static bool TryDecodeDiverVitalResult(
        ReadOnlySpan<byte> packet,
        out uint sequence,
        out DiverVitalResult result)
    {
        sequence = 0;
        result = default;
        if (packet.Length != DiverVitalResultSize ||
            !TryDecode(packet, out var type, out sequence) || type != PacketType.DiverVitalResult ||
            !TryReadDiverRuntimePayload(packet.Slice(HeaderSize + 18), out var state))
            return false;
        var candidate = new DiverVitalResult(
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize)),
            BinaryPrimitives.ReadUInt64LittleEndian(packet.Slice(HeaderSize + 4)),
            (DiverVitalCause)packet[HeaderSize + 12],
            (DiverVitalEdges)packet[HeaderSize + 13],
            ReadSingle(packet.Slice(HeaderSize + 14)),
            state);
        if (!IsValidDiverVitalResult(candidate))
            return false;
        result = candidate;
        return true;
    }

    internal static byte[] EncodeDiverVitalIntent(uint sequence, DiverVitalIntent intent)
    {
        if (!IsValidDiverVitalIntent(intent))
            throw new ArgumentOutOfRangeException(nameof(intent));
        var packet = new byte[DiverVitalIntentSize];
        WriteHeader(packet, PacketType.DiverVitalIntent, sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize), intent.SceneId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 4), intent.SceneEpoch);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(HeaderSize + 8), intent.RequestId);
        packet[HeaderSize + 16] = (byte)intent.Kind;
        WriteSingle(packet.AsSpan(HeaderSize + 17), intent.Amount);
        return packet;
    }

    internal static bool TryDecodeDiverVitalIntent(
        ReadOnlySpan<byte> packet,
        out uint sequence,
        out DiverVitalIntent intent)
    {
        sequence = 0;
        intent = default;
        if (packet.Length != DiverVitalIntentSize ||
            !TryDecode(packet, out var type, out sequence) || type != PacketType.DiverVitalIntent)
            return false;
        var candidate = new DiverVitalIntent(
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize)),
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 4)),
            BinaryPrimitives.ReadUInt64LittleEndian(packet.Slice(HeaderSize + 8)),
            (DiverVitalIntentKind)packet[HeaderSize + 16],
            ReadSingle(packet.Slice(HeaderSize + 17)));
        if (!IsValidDiverVitalIntent(candidate))
            return false;
        intent = candidate;
        return true;
    }

    internal static byte[] EncodeDiverWeaponIntent(uint sequence, DiverWeaponIntent intent)
    {
        if (!IsValidDiverWeaponIntent(intent))
            throw new ArgumentOutOfRangeException(nameof(intent));
        var packet = new byte[DiverWeaponIntentSize];
        WriteHeader(packet, PacketType.DiverWeaponIntent, sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize), intent.SceneId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 4), intent.SceneEpoch);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(HeaderSize + 8), intent.RequestId);
        packet[HeaderSize + 16] = (byte)intent.Action;
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 17), intent.WeaponId);
        return packet;
    }

    internal static bool TryDecodeDiverWeaponIntent(
        ReadOnlySpan<byte> packet,
        out uint sequence,
        out DiverWeaponIntent intent)
    {
        sequence = 0;
        intent = default;
        if (packet.Length != DiverWeaponIntentSize ||
            !TryDecode(packet, out var type, out sequence) ||
            type != PacketType.DiverWeaponIntent)
            return false;
        var candidate = new DiverWeaponIntent(
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize)),
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 4)),
            BinaryPrimitives.ReadUInt64LittleEndian(packet.Slice(HeaderSize + 8)),
            (DiverWeaponAction)packet[HeaderSize + 16],
            BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 17)));
        if (!IsValidDiverWeaponIntent(candidate))
            return false;
        intent = candidate;
        return true;
    }

    internal static byte[] EncodeDiverWeaponResult(uint sequence, DiverWeaponResult result)
    {
        if (!IsValidDiverWeaponResult(result))
            throw new ArgumentOutOfRangeException(nameof(result));
        var packet = new byte[DiverWeaponResultSize];
        WriteHeader(packet, PacketType.DiverWeaponResult, sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize), result.CommitRevision);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(HeaderSize + 4), result.RequestId);
        packet[HeaderSize + 12] = (byte)result.Action;
        packet[HeaderSize + 13] = result.Accepted ? (byte)1 : (byte)0;
        packet[HeaderSize + 14] = (byte)result.RejectReason;
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 15), result.AppliedRounds);
        WriteDiverRuntimePayload(packet.AsSpan(HeaderSize + 19), result.State);
        return packet;
    }

    internal static bool TryDecodeDiverWeaponResult(
        ReadOnlySpan<byte> packet,
        out uint sequence,
        out DiverWeaponResult result)
    {
        sequence = 0;
        result = default;
        if (packet.Length != DiverWeaponResultSize || packet[HeaderSize + 13] > 1 ||
            !TryDecode(packet, out var type, out sequence) ||
            type != PacketType.DiverWeaponResult ||
            !TryReadDiverRuntimePayload(packet.Slice(HeaderSize + 19), out var state))
            return false;
        var candidate = new DiverWeaponResult(
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize)),
            BinaryPrimitives.ReadUInt64LittleEndian(packet.Slice(HeaderSize + 4)),
            (DiverWeaponAction)packet[HeaderSize + 12],
            packet[HeaderSize + 13] != 0,
            (DiverWeaponRejectReason)packet[HeaderSize + 14],
            BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 15)),
            state);
        if (!IsValidDiverWeaponResult(candidate))
            return false;
        result = candidate;
        return true;
    }

    internal static byte[] EncodePlayerVisualState(uint sequence, PlayerVisualState state)
    {
        var sprites = state.Sprites ?? Array.Empty<VisualSprite>();
        if (state.SceneId == 0 || state.SceneEpoch == 0 || sprites.Length > MaxVisualSprites)
            throw new ArgumentOutOfRangeException(nameof(state));
        var packet = new byte[VisualStateFixedSize + sprites.Length * VisualSpriteSize];
        WriteHeader(packet, PacketType.PlayerVisualState, sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize), state.SceneId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 4), state.SceneEpoch);
        packet[HeaderSize + 8] = (byte)sprites.Length;
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
        var count = packet[HeaderSize + 8];
        if (count > MaxVisualSprites || packet.Length != VisualStateFixedSize + count * VisualSpriteSize)
            return false;
        var sceneId = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize));
        var sceneEpoch = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 4));
        if (sceneId == 0 || sceneEpoch == 0)
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
        state = new PlayerVisualState(sceneId, sceneEpoch, sprites);
        return true;
    }

    internal static byte[] EncodeProjectileVisualState(uint sequence, ProjectileVisualState state)
    {
        if (!IsValidProjectileVisualState(state))
            throw new ArgumentOutOfRangeException(nameof(state));
        var packet = new byte[ProjectileVisualStateSize];
        WriteHeader(packet, PacketType.ProjectileVisualState, sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize), state.SceneId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 4), state.SceneEpoch);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 8), state.Id);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 12), state.SpriteId);
        WriteSingle(packet.AsSpan(HeaderSize + 16), state.X);
        WriteSingle(packet.AsSpan(HeaderSize + 20), state.Y);
        WriteSingle(packet.AsSpan(HeaderSize + 24), state.Z);
        WriteSingle(packet.AsSpan(HeaderSize + 28), state.Rotation);
        WriteSingle(packet.AsSpan(HeaderSize + 32), state.ScaleX);
        WriteSingle(packet.AsSpan(HeaderSize + 36), state.ScaleY);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 40), state.SortingLayerId);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 44), state.SortingOrder);
        packet[HeaderSize + 48] = state.Flipped ? (byte)1 : (byte)0;
        packet[HeaderSize + 49] = state.HasRope ? (byte)1 : (byte)0;
        WriteSingle(packet.AsSpan(HeaderSize + 50), state.RopeStartX);
        WriteSingle(packet.AsSpan(HeaderSize + 54), state.RopeStartY);
        WriteSingle(packet.AsSpan(HeaderSize + 58), state.RopeStartZ);
        WriteSingle(packet.AsSpan(HeaderSize + 62), state.RopeEndX);
        WriteSingle(packet.AsSpan(HeaderSize + 66), state.RopeEndY);
        WriteSingle(packet.AsSpan(HeaderSize + 70), state.RopeEndZ);
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
            packet[HeaderSize + 48] > 1 || packet[HeaderSize + 49] > 1)
            return false;
        var candidate = new ProjectileVisualState(
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize)),
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 4)),
            BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 8)),
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 12)),
            ReadSingle(packet.Slice(HeaderSize + 16)),
            ReadSingle(packet.Slice(HeaderSize + 20)),
            ReadSingle(packet.Slice(HeaderSize + 24)),
            ReadSingle(packet.Slice(HeaderSize + 28)),
            ReadSingle(packet.Slice(HeaderSize + 32)),
            ReadSingle(packet.Slice(HeaderSize + 36)),
            BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 40)),
            BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 44)),
            packet[HeaderSize + 48] != 0,
            packet[HeaderSize + 49] != 0,
            ReadSingle(packet.Slice(HeaderSize + 50)),
            ReadSingle(packet.Slice(HeaderSize + 54)),
            ReadSingle(packet.Slice(HeaderSize + 58)),
            ReadSingle(packet.Slice(HeaderSize + 62)),
            ReadSingle(packet.Slice(HeaderSize + 66)),
            ReadSingle(packet.Slice(HeaderSize + 70)));
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
        WriteHeader(packet, type, sequence);
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

    internal static byte[] EncodeSceneState(uint sequence, uint sceneId, uint sceneEpoch)
    {
        if (sceneEpoch == 0)
            throw new ArgumentOutOfRangeException(nameof(sceneEpoch));
        var packet = new byte[HeaderSize + 8];
        WriteHeader(packet, PacketType.SceneState, sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize), sceneId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 4), sceneEpoch);
        return packet;
    }

    internal static bool TryDecodeSceneState(
        ReadOnlySpan<byte> packet, out uint sequence, out uint sceneId, out uint sceneEpoch)
    {
        sequence = 0;
        sceneId = 0;
        sceneEpoch = 0;
        if (packet.Length != HeaderSize + 8 ||
            !TryDecode(packet, out var type, out sequence) || type != PacketType.SceneState)
            return false;
        sceneId = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize));
        sceneEpoch = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 4));
        return sceneEpoch != 0;
    }

    internal static byte[] EncodeSceneSeed(uint sequence, SceneSeed seed)
    {
        if (seed.SceneId == 0 || seed.SceneEpoch == 0 || seed.Seed == 0)
            throw new ArgumentOutOfRangeException(nameof(seed));
        var packet = new byte[SceneSeedPacketSize];
        WriteHeader(packet, PacketType.SceneSeed, sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize), seed.SceneId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 4), seed.SceneEpoch);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 8), seed.Seed);
        return packet;
    }

    internal static bool TryDecodeSceneSeed(ReadOnlySpan<byte> packet, out uint sequence, out SceneSeed seed)
    {
        sequence = 0;
        seed = default;
        if (packet.Length != SceneSeedPacketSize ||
            !TryDecode(packet, out var type, out sequence) || type != PacketType.SceneSeed)
            return false;
        var sceneId = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize));
        var sceneEpoch = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 4));
        var value = BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 8));
        if (sceneId == 0 || sceneEpoch == 0 || value == 0)
            return false;
        seed = new SceneSeed(sceneId, sceneEpoch, value);
        return true;
    }

    internal static byte[] EncodeCargoState(uint sequence, CargoState state)
    {
        if (state.SceneId == 0 || state.SceneEpoch == 0 || state.WeightMax <= 0f ||
            state.OverloadedThreshold <= 0f ||
            !float.IsFinite(state.WeightMax) || !float.IsFinite(state.OverloadedThreshold) ||
            !float.IsFinite(state.WeightParameter))
            throw new ArgumentOutOfRangeException(nameof(state));
        var packet = new byte[CargoStatePacketSize];
        WriteHeader(packet, PacketType.CargoState, sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize), state.SceneId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 4), state.SceneEpoch);
        WriteSingle(packet.AsSpan(HeaderSize + 8), state.WeightMax);
        WriteSingle(packet.AsSpan(HeaderSize + 12), state.OverloadedThreshold);
        WriteSingle(packet.AsSpan(HeaderSize + 16), state.WeightParameter);
        return packet;
    }

    internal static bool TryDecodeCargoState(
        ReadOnlySpan<byte> packet,
        out uint sequence,
        out CargoState state)
    {
        sequence = 0;
        state = default;
        if (packet.Length != CargoStatePacketSize ||
            !TryDecode(packet, out var type, out sequence) || type != PacketType.CargoState)
            return false;
        state = new CargoState(
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize)),
            ReadSingle(packet.Slice(HeaderSize + 8)),
            ReadSingle(packet.Slice(HeaderSize + 12)),
            ReadSingle(packet.Slice(HeaderSize + 16)),
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 4)));
        return state.SceneId != 0 && state.SceneEpoch != 0 && state.WeightMax > 0f &&
               state.OverloadedThreshold > 0f &&
               float.IsFinite(state.WeightMax) && float.IsFinite(state.OverloadedThreshold) &&
               float.IsFinite(state.WeightParameter);
    }

    internal static byte[] EncodeNpcInteraction(uint sequence, NpcInteraction state)
    {
        if (!IsValidNpcInteraction(state))
            throw new ArgumentOutOfRangeException(nameof(state));
        var packet = new byte[NpcInteractionPacketSize];
        WriteHeader(packet, PacketType.NpcInteraction, sequence);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(HeaderSize), state.Token);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 8), state.SceneId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 12), state.SceneEpoch);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 16), state.TargetId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 20), state.Revision);
        packet[HeaderSize + 24] = (byte)state.Action;
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 25), state.NpcId);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 29), state.MissionId);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 33), state.TaskId);
        packet[HeaderSize + 37] = (byte)state.Result;
        return packet;
    }

    internal static bool TryDecodeNpcInteraction(
        ReadOnlySpan<byte> packet,
        out uint sequence,
        out NpcInteraction state)
    {
        sequence = 0;
        state = default;
        if (packet.Length != NpcInteractionPacketSize ||
            !TryDecode(packet, out var type, out sequence) || type != PacketType.NpcInteraction)
            return false;
        state = new NpcInteraction(
            BinaryPrimitives.ReadUInt64LittleEndian(packet.Slice(HeaderSize)),
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 8)),
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 12)),
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 16)),
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 20)),
            (NpcInteractionAction)packet[HeaderSize + 24],
            BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 25)),
            BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 29)),
            BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 33)),
            (NpcInteractionResult)packet[HeaderSize + 37]);
        if (IsValidNpcInteraction(state))
            return true;
        state = default;
        return false;
    }

    private static bool IsValidNpcInteraction(NpcInteraction state) =>
        state.Token != 0 && state.SceneId != 0 && state.SceneEpoch != 0 &&
        state.TargetId != 0 && state.NpcId > 0 &&
        state.MissionId >= 0 && state.TaskId >= 0 &&
        state.Action switch
        {
            NpcInteractionAction.Request =>
                state.Revision == 0 && state.Result == NpcInteractionResult.Pending,
            NpcInteractionAction.Granted =>
                state.Revision != 0 &&
                state.Result is >= NpcInteractionResult.Accepted and <= NpcInteractionResult.TooFar,
            NpcInteractionAction.Released =>
                state.Result == NpcInteractionResult.Accepted,
            _ => false
        };

    internal static byte[] EncodeFishSnapshotBatch(
        uint sequence,
        uint sceneId,
        uint sceneEpoch,
        uint tick,
        IReadOnlyList<FishSnapshot> snapshots,
        int offset,
        int count)
    {
        if (sceneId == 0 || sceneEpoch == 0 || tick == 0 || snapshots == null || offset < 0 || count < 1 ||
            count > MaxFishSnapshotsPerPacket || offset > snapshots.Count - count)
            throw new ArgumentOutOfRangeException(nameof(count));

        var packetSize = FishSnapshotBatchFixedSize;
        for (var index = 0; index < count; index++)
            packetSize += CanCompactFishSnapshot(snapshots[offset + index])
                ? FishSnapshotCompactEntrySize : FishSnapshotFullEntrySize;
        if (packetSize > MaxDatagramSize)
            throw new ArgumentOutOfRangeException(nameof(count));
        var packet = new byte[packetSize];
        WriteHeader(packet, PacketType.FishSnapshotBatch, sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize), sceneId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 4), sceneEpoch);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 8), tick);
        packet[HeaderSize + 12] = (byte)count;
        var entryOffset = FishSnapshotBatchFixedSize;
        for (var index = 0; index < count; index++)
        {
            var snapshot = snapshots[offset + index];
            if (snapshot.SceneId != sceneId || snapshot.SceneEpoch != sceneEpoch ||
                snapshot.Tick != tick || !IsValidFishSnapshot(snapshot))
                throw new ArgumentOutOfRangeException(nameof(snapshots));
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(entryOffset), snapshot.Id);
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(entryOffset + 4), snapshot.Revision);
            var compact = CanCompactFishSnapshot(snapshot);
            packet[entryOffset + 8] = compact ? (byte)0 : (byte)1;
            if (compact)
            {
                WriteQuantized(packet.AsSpan(entryOffset + 9), snapshot.X, 64f);
                WriteQuantized(packet.AsSpan(entryOffset + 11), snapshot.Y, 64f);
                WriteQuantized(packet.AsSpan(entryOffset + 13), snapshot.Z, 64f);
                BinaryPrimitives.WriteUInt16LittleEndian(
                    packet.AsSpan(entryOffset + 15), QuantizeRotation(snapshot.Rotation));
                WriteQuantized(packet.AsSpan(entryOffset + 17), snapshot.VelocityX, 16f);
                WriteQuantized(packet.AsSpan(entryOffset + 19), snapshot.VelocityY, 16f);
                WriteSingle(packet.AsSpan(entryOffset + 21), snapshot.Hp);
                packet[entryOffset + 25] = snapshot.Flags;
                entryOffset += FishSnapshotCompactEntrySize;
            }
            else
            {
                WriteSingle(packet.AsSpan(entryOffset + 9), snapshot.X);
                WriteSingle(packet.AsSpan(entryOffset + 13), snapshot.Y);
                WriteSingle(packet.AsSpan(entryOffset + 17), snapshot.Z);
                WriteSingle(packet.AsSpan(entryOffset + 21), snapshot.Rotation);
                WriteSingle(packet.AsSpan(entryOffset + 25), snapshot.VelocityX);
                WriteSingle(packet.AsSpan(entryOffset + 29), snapshot.VelocityY);
                WriteSingle(packet.AsSpan(entryOffset + 33), snapshot.Hp);
                packet[entryOffset + 37] = snapshot.Flags;
                entryOffset += FishSnapshotFullEntrySize;
            }
        }
        return packet;
    }

    internal static bool TryDecodeFishSnapshotBatch(
        ReadOnlySpan<byte> packet,
        out uint sequence,
        out FishSnapshot[] snapshots)
    {
        sequence = 0;
        snapshots = Array.Empty<FishSnapshot>();
        if (packet.Length < FishSnapshotBatchFixedSize ||
            !TryDecode(packet, out var type, out sequence) || type != PacketType.FishSnapshotBatch)
            return false;

        var sceneId = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize));
        var sceneEpoch = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 4));
        var tick = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 8));
        var count = packet[HeaderSize + 12];
        if (sceneId == 0 || sceneEpoch == 0 || tick == 0 || count is < 1 ||
            count > MaxFishSnapshotsPerPacket || packet.Length > MaxDatagramSize)
            return false;

        snapshots = new FishSnapshot[count];
        var entryOffset = FishSnapshotBatchFixedSize;
        for (var index = 0; index < count; index++)
        {
            if (entryOffset + 9 > packet.Length || packet[entryOffset + 8] > 1)
            {
                snapshots = Array.Empty<FishSnapshot>();
                return false;
            }
            var compact = packet[entryOffset + 8] == 0;
            var entrySize = compact ? FishSnapshotCompactEntrySize : FishSnapshotFullEntrySize;
            if (entryOffset + entrySize > packet.Length)
            {
                snapshots = Array.Empty<FishSnapshot>();
                return false;
            }
            var snapshot = new FishSnapshot(
                sceneId,
                sceneEpoch,
                tick,
                BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(entryOffset)),
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(entryOffset + 4)),
                0,
                compact ? ReadQuantized(packet.Slice(entryOffset + 9), 64f) : ReadSingle(packet.Slice(entryOffset + 9)),
                compact ? ReadQuantized(packet.Slice(entryOffset + 11), 64f) : ReadSingle(packet.Slice(entryOffset + 13)),
                compact ? ReadQuantized(packet.Slice(entryOffset + 13), 64f) : ReadSingle(packet.Slice(entryOffset + 17)),
                compact ? BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(entryOffset + 15)) *
                    (360f / ushort.MaxValue) : ReadSingle(packet.Slice(entryOffset + 21)),
                compact ? ReadQuantized(packet.Slice(entryOffset + 17), 16f) : ReadSingle(packet.Slice(entryOffset + 25)),
                compact ? ReadQuantized(packet.Slice(entryOffset + 19), 16f) : ReadSingle(packet.Slice(entryOffset + 29)),
                ReadSingle(packet.Slice(entryOffset + (compact ? 21 : 33))),
                packet[entryOffset + entrySize - 1]);
            if (!IsValidFishSnapshot(snapshot, false))
            {
                snapshots = Array.Empty<FishSnapshot>();
                return false;
            }
            snapshots[index] = snapshot;
            entryOffset += entrySize;
        }
        return entryOffset == packet.Length;
    }

    private static bool IsValidFishSnapshot(FishSnapshot snapshot, bool requireFishDataTid = true) =>
        snapshot.SceneId != 0 && snapshot.SceneEpoch != 0 && snapshot.Tick != 0 &&
        snapshot.Id > 0 && snapshot.Revision != 0 && (!requireFishDataTid || snapshot.FishDataTID > 0) &&
        float.IsFinite(snapshot.X) && float.IsFinite(snapshot.Y) && float.IsFinite(snapshot.Z) &&
        float.IsFinite(snapshot.Rotation) && float.IsFinite(snapshot.VelocityX) &&
        float.IsFinite(snapshot.VelocityY) && float.IsFinite(snapshot.Hp) &&
        snapshot.Hp is >= 0f and <= 1_000_000_000f &&
        MathF.Abs(snapshot.X) <= 1_000_000f && MathF.Abs(snapshot.Y) <= 1_000_000f &&
        MathF.Abs(snapshot.Z) <= 1_000_000f && MathF.Abs(snapshot.VelocityX) <= 10_000f &&
        MathF.Abs(snapshot.VelocityY) <= 10_000f && snapshot.Flags <= 7;

    private static bool CanCompactFishSnapshot(FishSnapshot snapshot) =>
        CanQuantize(snapshot.X, 64f) && CanQuantize(snapshot.Y, 64f) &&
        CanQuantize(snapshot.Z, 64f) && CanQuantize(snapshot.VelocityX, 16f) &&
        CanQuantize(snapshot.VelocityY, 16f);

    internal static int FishSnapshotRecordSize(FishSnapshot snapshot) =>
        CanCompactFishSnapshot(snapshot) ? FishSnapshotCompactEntrySize : FishSnapshotFullEntrySize;

    internal static int FishSnapshotBatchOverhead => FishSnapshotBatchFixedSize;

    private static bool CanQuantize(float value, float scale)
    {
        if (!float.IsFinite(value))
            return false;
        var scaled = MathF.Round(value * scale);
        return scaled is >= short.MinValue and <= short.MaxValue;
    }

    private static void WriteQuantized(Span<byte> destination, float value, float scale) =>
        BinaryPrimitives.WriteInt16LittleEndian(destination, (short)MathF.Round(value * scale));

    private static float ReadQuantized(ReadOnlySpan<byte> source, float scale) =>
        BinaryPrimitives.ReadInt16LittleEndian(source) / scale;

    private static ushort QuantizeRotation(float rotation)
    {
        var normalized = rotation % 360f;
        if (normalized < 0f)
            normalized += 360f;
        return (ushort)MathF.Round(normalized * (ushort.MaxValue / 360f));
    }

    internal static byte[] EncodeFishDamageRequest(uint sequence, FishDamageRequest request)
    {
        if (request.SceneId == 0 || request.SceneEpoch == 0 || request.Id <= 0 ||
            request.Damage is < 1 or > 10_000 || request.Element is < 0 or > 32 ||
            request.AttackType is < 0 or > 64)
            throw new ArgumentOutOfRangeException(nameof(request));
        var packet = new byte[FishDamageRequestSize];
        WriteHeader(packet, PacketType.FishDamageRequest, sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize), request.SceneId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 4), request.SceneEpoch);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 8), request.Id);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 12), request.KnownRevision);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 16), request.Damage);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 20), request.Element);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 24), request.AttackType);
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
        var sceneId = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize));
        var sceneEpoch = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 4));
        var id = BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 8));
        var damage = BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 16));
        var element = BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 20));
        var attackType = BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 24));
        if (sceneId == 0 || sceneEpoch == 0 || id <= 0 || damage is < 1 or > 10_000 ||
            element is < 0 or > 32 || attackType is < 0 or > 64)
            return false;
        request = new FishDamageRequest(
            sceneId, sceneEpoch, id,
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 12)),
            damage, element, attackType);
        return true;
    }

    internal static byte[] EncodeBossDamageRequest(uint sequence, BossDamageRequest request)
    {
        if (request.SceneId == 0 || request.SceneEpoch == 0 || request.RequestId == 0 ||
            request.BossId == 0 || request.TargetId == 0 ||
            request.Damage is < 1 or > 10_000 || request.Element is < 0 or > 32 ||
            request.AttackType is < 0 or > 64 || !float.IsFinite(request.HitX) ||
            !float.IsFinite(request.HitY) || !float.IsFinite(request.HitZ) ||
            MathF.Abs(request.HitX) > 1_000_000f || MathF.Abs(request.HitY) > 1_000_000f ||
            MathF.Abs(request.HitZ) > 1_000_000f)
            throw new ArgumentOutOfRangeException(nameof(request));
        var packet = new byte[BossDamageRequestSize];
        WriteHeader(packet, PacketType.BossDamageRequest, sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize), request.SceneId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 4), request.SceneEpoch);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 8), request.RequestId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 12), request.BossId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 16), request.TargetId);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 20), request.Damage);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 24), request.Element);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 28), request.AttackType);
        WriteSingle(packet.AsSpan(HeaderSize + 32), request.HitX);
        WriteSingle(packet.AsSpan(HeaderSize + 36), request.HitY);
        WriteSingle(packet.AsSpan(HeaderSize + 40), request.HitZ);
        return packet;
    }

    internal static bool TryDecodeBossDamageRequest(
        ReadOnlySpan<byte> packet,
        out uint sequence,
        out BossDamageRequest request)
    {
        sequence = 0;
        request = default;
        if (packet.Length != BossDamageRequestSize ||
            !TryDecode(packet, out var type, out sequence) || type != PacketType.BossDamageRequest)
            return false;
        var sceneId = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize));
        var sceneEpoch = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 4));
        var requestId = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 8));
        var bossId = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 12));
        var targetId = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 16));
        var damage = BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 20));
        var element = BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 24));
        var attackType = BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 28));
        var hitX = ReadSingle(packet.Slice(HeaderSize + 32));
        var hitY = ReadSingle(packet.Slice(HeaderSize + 36));
        var hitZ = ReadSingle(packet.Slice(HeaderSize + 40));
        if (sceneId == 0 || sceneEpoch == 0 || requestId == 0 || bossId == 0 || targetId == 0 ||
            damage is < 1 or > 10_000 || element is < 0 or > 32 || attackType is < 0 or > 64 ||
            !float.IsFinite(hitX) || !float.IsFinite(hitY) || !float.IsFinite(hitZ) ||
            MathF.Abs(hitX) > 1_000_000f || MathF.Abs(hitY) > 1_000_000f ||
            MathF.Abs(hitZ) > 1_000_000f)
            return false;
        request = new BossDamageRequest(
            sceneId, sceneEpoch, requestId, bossId, targetId, damage, element, attackType,
            hitX, hitY, hitZ);
        return true;
    }

    internal static byte[] EncodeBossState(uint sequence, BossState state)
    {
        if (!IsValidBossState(state))
            throw new ArgumentOutOfRangeException(nameof(state));
        var packet = new byte[BossStateSize];
        WriteHeader(packet, PacketType.BossState, sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize), state.SceneId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 4), state.SceneEpoch);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 8), state.Tick);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 12), state.BossId);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 16), state.FishId);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 20), state.CurrentHp);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 24), state.MaxHp);
        WriteSingle(packet.AsSpan(HeaderSize + 28), state.X);
        WriteSingle(packet.AsSpan(HeaderSize + 32), state.Y);
        WriteSingle(packet.AsSpan(HeaderSize + 36), state.Z);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 40), state.AnimationHash);
        WriteSingle(packet.AsSpan(HeaderSize + 44), state.AnimationTime);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 48), state.Phase);
        packet[HeaderSize + 52] = state.Flags;
        return packet;
    }

    internal static bool TryDecodeBossState(
        ReadOnlySpan<byte> packet,
        out uint sequence,
        out BossState state)
    {
        sequence = 0;
        state = default;
        if (packet.Length != BossStateSize ||
            !TryDecode(packet, out var type, out sequence) || type != PacketType.BossState)
            return false;
        state = new BossState(
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize)),
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 8)),
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 12)),
            BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 16)),
            BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 20)),
            BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 24)),
            ReadSingle(packet.Slice(HeaderSize + 28)),
            ReadSingle(packet.Slice(HeaderSize + 32)),
            ReadSingle(packet.Slice(HeaderSize + 36)),
            BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 40)),
            ReadSingle(packet.Slice(HeaderSize + 44)),
            BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 48)),
            packet[HeaderSize + 52]);
        if (!IsValidBossState(state))
        {
            state = default;
            return false;
        }
        return true;
    }

    private static bool IsValidBossState(BossState state) =>
        state.SceneId != 0 && state.SceneEpoch != 0 && state.Tick != 0 &&
        state.BossId != 0 && state.FishId >= 0 &&
        state.CurrentHp >= 0 && state.MaxHp is > 0 and <= 1_000_000_000 &&
        state.CurrentHp <= state.MaxHp && float.IsFinite(state.X) && float.IsFinite(state.Y) &&
        float.IsFinite(state.Z) && MathF.Abs(state.X) <= 1_000_000f &&
        MathF.Abs(state.Y) <= 1_000_000f && MathF.Abs(state.Z) <= 1_000_000f &&
        float.IsFinite(state.AnimationTime) && state.AnimationTime is >= 0f and <= 1f &&
        state.Phase >= 0 && state.Flags <= 1;

    internal static byte[] EncodeManagerEvent(uint sequence, ManagerEvent state)
    {
        if (state.Domain == 0 || state.Domain > 18 || state.Action == 0 || state.Action > 80)
            throw new ArgumentOutOfRangeException(nameof(state));
        var invocationSize = 0;
        if (state.Invocation is { } invocation &&
            !TryGetManagerInvocationSize(state, invocation, out invocationSize))
            throw new ArgumentOutOfRangeException(nameof(state));
        var packet = new byte[ManagerEventSize + invocationSize];
        WriteHeader(packet, PacketType.ManagerEvent, sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize), state.Revision);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 4), state.SceneId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 8), state.SceneEpoch);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 12), state.HostTick);
        packet[HeaderSize + 16] = state.Domain;
        packet[HeaderSize + 17] = state.Action;
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 18), state.Value);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 22), state.Context);
        if (state.Invocation is { } descriptor)
            WriteManagerInvocation(packet.AsSpan(ManagerEventSize), descriptor);
        return packet;
    }

    internal static bool TryDecodeManagerEvent(
        ReadOnlySpan<byte> packet,
        out uint sequence,
        out ManagerEvent state)
    {
        sequence = 0;
        state = default;
        if (packet.Length < ManagerEventSize || packet.Length > MaxDatagramSize ||
            !TryDecode(packet, out var type, out sequence) || type != PacketType.ManagerEvent)
            return false;
        var candidate = new ManagerEvent(
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize)),
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 12)),
            packet[HeaderSize + 16],
            packet[HeaderSize + 17],
            BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 18)),
            BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 22)),
            SceneEpoch: BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 8)));
        if (!IsValidManagerEvent(candidate))
            return false;
        if (packet.Length == ManagerEventSize)
        {
            state = candidate;
            return true;
        }
        if (!TryReadManagerInvocation(
                packet.Slice(ManagerEventSize), candidate, out var invocation))
            return false;
        state = candidate with { Invocation = invocation };
        return true;
    }

    private static bool TryGetManagerInvocationSize(
        ManagerEvent state,
        ManagerInvocationDescriptor invocation,
        out int size)
    {
        size = 2;
        if (state.Revision == 0)
            return false;
        var routeMatches = invocation.Kind switch
        {
            ManagerInvocationKind.Scenario => state.Domain == 16 && state.Action == 32,
            ManagerInvocationKind.DialogueNormal or ManagerInvocationKind.DialogueArguments or
                ManagerInvocationKind.DialogueSmall => state.Domain == 3 && state.Action == 35,
            ManagerInvocationKind.TimelineByTid => state.Domain == 13 && state.Action == 24,
            _ => false
        };
        if (!routeMatches)
            return false;
        if (invocation.Kind == ManagerInvocationKind.TimelineByTid)
        {
            if (invocation.BundleId != null || invocation.Arguments != null ||
                invocation.UseButton || invocation.ShowCurtain || invocation.IgnorePlaying ||
                (state.Context & 1) != 0 ||
                invocation.HasCustomPosition &&
                (!float.IsFinite(invocation.CustomX) || !float.IsFinite(invocation.CustomY) ||
                 !float.IsFinite(invocation.CustomZ) ||
                 MathF.Abs(invocation.CustomX) > 1_000_000f ||
                 MathF.Abs(invocation.CustomY) > 1_000_000f ||
                 MathF.Abs(invocation.CustomZ) > 1_000_000f) ||
                !invocation.HasCustomPosition &&
                (invocation.CustomX != 0f || invocation.CustomY != 0f || invocation.CustomZ != 0f))
                return false;
            if (invocation.HasCustomPosition)
                size += 12;
            return ManagerEventSize + size <= MaxDatagramSize;
        }

        if (string.IsNullOrEmpty(invocation.BundleId) || invocation.HasCustomPosition ||
            invocation.CustomX != 0f || invocation.CustomY != 0f || invocation.CustomZ != 0f ||
            !invocation.ApplyOffset ||
            unchecked((int)SceneId(invocation.BundleId)) != state.Value ||
            invocation.Kind != ManagerInvocationKind.Scenario && invocation.IgnorePlaying ||
            (invocation.Kind is ManagerInvocationKind.DialogueNormal or
                ManagerInvocationKind.DialogueSmall) && invocation.Arguments != null)
            return false;
        if (!TryGetManagerInvocationStringSize(invocation.BundleId, out var bundleSize))
            return false;
        size += 2 + bundleSize;
        if (invocation.Kind is ManagerInvocationKind.Scenario or
            ManagerInvocationKind.DialogueArguments)
        {
            size += 2;
            if (invocation.Arguments != null)
            {
                if (invocation.Arguments.Length > MaxManagerInvocationArguments)
                    return false;
                foreach (var argument in invocation.Arguments)
                {
                    if (argument == null)
                        size += 2;
                    else
                    {
                        if (!TryGetManagerInvocationStringSize(argument, out var argumentSize))
                            return false;
                        size += 2 + argumentSize;
                    }
                }
            }
        }
        return ManagerEventSize + size <= MaxDatagramSize;
    }

    internal static bool IsValidManagerInvocation(
        byte domain,
        byte action,
        int value,
        ManagerInvocationDescriptor invocation) =>
        TryGetManagerInvocationSize(
            new ManagerEvent(1, 0, 1, domain, action, value, 0), invocation, out _);

    private static bool TryGetManagerInvocationStringSize(string value, out int size)
    {
        try
        {
            size = StrictUtf8.GetByteCount(value);
            return value.IndexOf('\0') < 0 && size <= MaxManagerInvocationStringBytes;
        }
        catch (EncoderFallbackException)
        {
            size = 0;
            return false;
        }
    }

    private static void WriteManagerInvocation(
        Span<byte> destination, ManagerInvocationDescriptor invocation)
    {
        destination[0] = (byte)invocation.Kind;
        destination[1] = invocation.Kind switch
        {
            ManagerInvocationKind.Scenario =>
                (byte)((invocation.UseButton ? 1 : 0) |
                    (invocation.ShowCurtain ? 2 : 0) |
                    (invocation.IgnorePlaying ? 4 : 0)),
            ManagerInvocationKind.DialogueNormal or ManagerInvocationKind.DialogueArguments or
                ManagerInvocationKind.DialogueSmall =>
                (byte)((invocation.UseButton ? 1 : 0) |
                    (invocation.ShowCurtain ? 2 : 0)),
            _ => (byte)((invocation.ApplyOffset ? 1 : 0) |
                (invocation.HasCustomPosition ? 2 : 0))
        };
        var offset = 2;
        if (invocation.Kind == ManagerInvocationKind.TimelineByTid)
        {
            if (!invocation.HasCustomPosition)
                return;
            WriteSingle(destination.Slice(offset), invocation.CustomX);
            WriteSingle(destination.Slice(offset + 4), invocation.CustomY);
            WriteSingle(destination.Slice(offset + 8), invocation.CustomZ);
            return;
        }
        offset += WriteManagerInvocationString(destination.Slice(offset), invocation.BundleId);
        if (invocation.Kind is not (ManagerInvocationKind.Scenario or
            ManagerInvocationKind.DialogueArguments))
            return;
        if (invocation.Arguments == null)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(offset), ushort.MaxValue);
            return;
        }
        BinaryPrimitives.WriteUInt16LittleEndian(
            destination.Slice(offset), (ushort)invocation.Arguments.Length);
        offset += 2;
        foreach (var argument in invocation.Arguments)
        {
            if (argument == null)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(offset), ushort.MaxValue);
                offset += 2;
            }
            else
                offset += WriteManagerInvocationString(destination.Slice(offset), argument);
        }
    }

    private static int WriteManagerInvocationString(Span<byte> destination, string value)
    {
        var length = StrictUtf8.GetByteCount(value);
        BinaryPrimitives.WriteUInt16LittleEndian(destination, (ushort)length);
        StrictUtf8.GetBytes(value, destination.Slice(2, length));
        return length + 2;
    }

    private static bool TryReadManagerInvocation(
        ReadOnlySpan<byte> source,
        ManagerEvent state,
        out ManagerInvocationDescriptor invocation)
    {
        invocation = default;
        if (source.Length < 2)
            return false;
        var kind = (ManagerInvocationKind)source[0];
        var flags = source[1];
        var offset = 2;
        string bundleId = null;
        string[] arguments = null;
        var applyOffset = true;
        var hasCustomPosition = false;
        var customX = 0f;
        var customY = 0f;
        var customZ = 0f;
        if (kind == ManagerInvocationKind.TimelineByTid)
        {
            if ((flags & ~3) != 0)
                return false;
            applyOffset = (flags & 1) != 0;
            hasCustomPosition = (flags & 2) != 0;
            if (hasCustomPosition)
            {
                if (source.Length - offset != 12)
                    return false;
                customX = ReadSingle(source.Slice(offset));
                customY = ReadSingle(source.Slice(offset + 4));
                customZ = ReadSingle(source.Slice(offset + 8));
                offset += 12;
            }
        }
        else
        {
            var allowedFlags = kind == ManagerInvocationKind.Scenario ? 7 : 3;
            if ((flags & ~allowedFlags) != 0 ||
                !TryReadManagerInvocationString(source, ref offset, false, out bundleId))
                return false;
            if (kind is ManagerInvocationKind.Scenario or ManagerInvocationKind.DialogueArguments)
            {
                if (source.Length - offset < 2)
                    return false;
                var count = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(offset));
                offset += 2;
                if (count != ushort.MaxValue)
                {
                    if (count > MaxManagerInvocationArguments)
                        return false;
                    arguments = new string[count];
                    for (var index = 0; index < count; index++)
                        if (!TryReadManagerInvocationString(
                                source, ref offset, true, out arguments[index]))
                            return false;
                }
            }
        }
        if (offset != source.Length)
            return false;
        invocation = new ManagerInvocationDescriptor(
            kind, bundleId, arguments,
            kind != ManagerInvocationKind.TimelineByTid && (flags & 1) != 0,
            kind != ManagerInvocationKind.TimelineByTid && (flags & 2) != 0,
            kind == ManagerInvocationKind.Scenario && (flags & 4) != 0,
            applyOffset, hasCustomPosition, customX, customY, customZ);
        return TryGetManagerInvocationSize(state, invocation, out var expectedSize) &&
            expectedSize == source.Length;
    }

    private static bool TryReadManagerInvocationString(
        ReadOnlySpan<byte> source, ref int offset, bool allowNull, out string value)
    {
        value = null;
        if (source.Length - offset < 2)
            return false;
        var length = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(offset));
        offset += 2;
        if (length == ushort.MaxValue)
            return allowNull;
        if (length > MaxManagerInvocationStringBytes || source.Length - offset < length)
            return false;
        try
        {
            value = StrictUtf8.GetString(source.Slice(offset, length));
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
        offset += length;
        return true;
    }

    private static bool IsValidManagerEvent(ManagerEvent state)
    {
        var pairIsValid = state.Domain switch
        {
            1 or 2 => state.Action is 1 or 2 or 3,
            3 => state.Action is 4 or 5 or 6 or 7 or 35 or 36 or 37 or 38 or 39,
            4 or 5 or 6 or 7 => state.Action == 8,
            8 => state.Action is 9 or 10 or 11 or >= 71 and <= 74,
            9 => state.Action is 8 or 77,
            10 => state.Action is 12 or 75 or 76,
            11 => state.Action is >= 13 and <= 20 or >= 28 and <= 30,
            12 => state.Action is 21 or 22 or 23,
            13 => state.Action is 24 or 25 or 31,
            14 => state.Action == 26,
            15 => state.Action == 27,
            16 => state.Action is 32 or 33 or 34,
            17 => state.Action is >= 40 and <= 70,
            18 => state.Action is 78 or 79 or 80,
            _ => false
        };
        if (!pairIsValid)
            return false;
        if ((state.SceneId == 0) != (state.SceneEpoch == 0) ||
            state.Domain is 3 or 16 or 18 && state.SceneId != 0)
            return false;
        if (state.Action is 32 or 35 && (state.Value == 0 || state.Context != 0) ||
            state.Action is 38 or 39 && (state.Value <= 0 || state.Context != 0) ||
            state.Action == 33 && (state.Value == 0 || state.Context == 0) ||
            state.Action == 36 && (state.Value == 0 || state.Context < 0) ||
            (state.Action is 34 or 37) &&
                (state.Value == 0 || state.Context is not 0 and not 1))
            return false;
        if ((state.Domain is 4 or 5 || state.Domain == 10 && state.Action == 12) &&
            state.Value is not 0 and not 1)
            return false;
        if (state.Domain == 12 && state.Action == 22 && state.Value is < -1 or > 8)
            return false;
        if (state.Domain == 12 && state.Action == 23 &&
            state.Value is not (>= 0 and <= 3) and not 100)
            return false;
        if (state.Domain == 13)
        {
            if (state.Value is 0 or int.MinValue)
                return false;
            if (state.Action != 31 && state.Value < 0)
                return false;
            if (state.Action == 31 &&
                BitConverter.Int32BitsToSingle(state.Context) is not (>= 0f and <= 86_400f))
                return false;
        }
        if (state.Domain == 14 && state.Value is not (>= 0 and <= 4) and not 99)
            return false;
        if (state.Domain == 15 &&
            (state.Value is < 0 or > 64 ||
             BitConverter.Int32BitsToSingle(state.Context) is not (>= 0f and <= 3_600f)))
            return false;
        if (state.Domain == 17)
        {
            if (state.Action is >= 40 and <= 68 &&
                (state.Value < 0 ||
                 (state.Context & 0x3fffffff) is <= 0 or > 1_000_000))
                return false;
            if (state.Action == 69 &&
                (state.Value is < 1 or > 6 || state.Context < -1_000_000_000))
                return false;
            if (state.Action == 70 && (state.Value < 0 || state.Context is < 0 or > 3))
                return false;
        }
        if (state.Domain == 18)
        {
            if (state.Action == 78 &&
                (state.Value is < 1 or > 16 ||
                 BitConverter.Int32BitsToSingle(state.Context) is not (>= 0f and <= 4f)))
                return false;
            if (state.Action is 79 or 80 && (state.Value != 0 || state.Context != 0))
                return false;
        }
        if (state.Action is >= 71 and <= 77)
        {
            var request = state.Action is 71 or 73 or 75 or 77;
            if (request != (state.Revision == 0))
                return false;
            var validTarget = state.Context >= 0 && ((uint)state.Context >> 16) < 2;
            if (state.Action == 71 && (state.Value is < 0 or > 1 || state.Context != 0) ||
                state.Action == 72 && (state.Value < 0 || !validTarget) ||
                state.Action == 73 && (state.Value != 0 || !validTarget) ||
                state.Action == 74 && (state.Value is not 0 and not 1 || !validTarget) ||
                state.Action == 75 && (state.Value is < 0 or > 1_000_000 || !validTarget) ||
                state.Action == 76 && (state.Value is < -1 or > 1_000_000 || !validTarget) ||
                state.Action == 77 &&
                    (state.Value is < 0 or > 1 || state.Context is < 1 or > 1000))
                return false;
        }
        return true;
    }

    internal static byte[] EncodeSushiResultState(uint sequence, SushiResultState state)
    {
        if (!IsValidSushiResult(state))
            throw new ArgumentOutOfRangeException(nameof(state));
        var packet = new byte[SushiResultStateSize];
        WriteHeader(packet, PacketType.SushiResultState, sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize), state.Revision);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 4), state.SalesMenu);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 8), state.SalesEtc);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 12), state.StaffTips);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 16), state.TotalVisits);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 20), state.LikeCount);
        WriteSingle(packet.AsSpan(HeaderSize + 24), state.Rating);
        return packet;
    }

    internal static bool TryDecodeSushiResultState(
        ReadOnlySpan<byte> packet,
        out uint sequence,
        out SushiResultState state)
    {
        sequence = 0;
        state = default;
        if (packet.Length != SushiResultStateSize ||
            !TryDecode(packet, out var type, out sequence) || type != PacketType.SushiResultState)
            return false;
        state = new SushiResultState(
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize)),
            BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 4)),
            BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 8)),
            BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 12)),
            BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 16)),
            BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 20)),
            ReadSingle(packet.Slice(HeaderSize + 24)));
        if (IsValidSushiResult(state))
            return true;
        state = default;
        return false;
    }

    private static bool IsValidSushiResult(SushiResultState state) =>
        state.Revision != 0 && state.SalesMenu >= 0 && state.SalesEtc >= 0 &&
        state.StaffTips >= 0 && state.TotalVisits >= 0 && state.LikeCount >= 0 &&
        float.IsFinite(state.Rating) && state.Rating is >= 0f and <= 5f;

    internal static byte[] EncodeFishLifecycle(uint sequence, FishLifecycle state)
    {
        if (!IsValidFishLifecycle(state))
            throw new ArgumentOutOfRangeException(nameof(state));
        var packet = new byte[FishLifecycleSize];
        WriteHeader(packet, PacketType.FishLifecycle, sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize), state.SceneId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 4), state.SceneEpoch);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 8), state.Id);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 12), state.Revision);
        packet[HeaderSize + 16] = (byte)state.Kind;
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 17), state.FishDataTID);
        packet[HeaderSize + 21] = (byte)state.Phase;
        WriteSingle(packet.AsSpan(HeaderSize + 22), state.Hp);
        return packet;
    }

    internal static bool TryDecodeFishLifecycle(
        ReadOnlySpan<byte> packet, out uint sequence, out FishLifecycle state)
    {
        sequence = 0;
        state = default;
        if (packet.Length != FishLifecycleSize ||
            !TryDecode(packet, out var type, out sequence) || type != PacketType.FishLifecycle)
            return false;
        state = new FishLifecycle(
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize)),
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 4)),
            BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 8)),
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 12)),
            (FishLifecycleKind)packet[HeaderSize + 16],
            BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 17)),
            (FishPhase)packet[HeaderSize + 21],
            ReadSingle(packet.Slice(HeaderSize + 22)));
        if (IsValidFishLifecycle(state))
            return true;
        state = default;
        return false;
    }

    internal static byte[] EncodeFishActionRequest(uint sequence, FishActionRequest request)
    {
        if (!IsValidFishActionRequest(request))
            throw new ArgumentOutOfRangeException(nameof(request));
        var packet = new byte[FishActionRequestSize];
        WriteHeader(packet, PacketType.FishActionRequest, sequence);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(HeaderSize), request.RequestId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 8), request.SceneId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 12), request.SceneEpoch);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 16), request.Id);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 20), request.KnownRevision);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(HeaderSize + 24), request.LeaseId);
        packet[HeaderSize + 32] = (byte)request.Action;
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 33), request.Damage);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 37), request.Element);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 41), request.AttackType);
        return packet;
    }

    internal static bool TryDecodeFishActionRequest(
        ReadOnlySpan<byte> packet, out uint sequence, out FishActionRequest request)
    {
        sequence = 0;
        request = default;
        if (packet.Length != FishActionRequestSize ||
            !TryDecode(packet, out var type, out sequence) || type != PacketType.FishActionRequest)
            return false;
        request = new FishActionRequest(
            BinaryPrimitives.ReadUInt64LittleEndian(packet.Slice(HeaderSize)),
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 8)),
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 12)),
            BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 16)),
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 20)),
            BinaryPrimitives.ReadUInt64LittleEndian(packet.Slice(HeaderSize + 24)),
            (FishAction)packet[HeaderSize + 32],
            BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 33)),
            BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 37)),
            BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 41)));
        if (IsValidFishActionRequest(request))
            return true;
        request = default;
        return false;
    }

    internal static byte[] EncodeFishHookPose(uint sequence, FishHookPose pose)
    {
        if (!IsValidFishHookPose(pose))
            throw new ArgumentOutOfRangeException(nameof(pose));
        var compact = CanCompactFishHookPose(pose);
        var packet = new byte[compact ? FishHookPoseCompactSize : FishHookPoseFullSize];
        WriteHeader(packet, PacketType.FishHookPose, sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize), pose.SceneId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 4), pose.SceneEpoch);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 8), pose.FishId);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(HeaderSize + 12), pose.LeaseId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 20), pose.Tick);
        packet[HeaderSize + 24] = compact ? (byte)0 : (byte)1;
        var offset = FishHookPoseFixedSize;
        if (compact)
        {
            WriteQuantized(packet.AsSpan(offset), pose.X, 64f);
            WriteQuantized(packet.AsSpan(offset + 2), pose.Y, 64f);
            WriteQuantized(packet.AsSpan(offset + 4), pose.Z, 64f);
            BinaryPrimitives.WriteUInt16LittleEndian(
                packet.AsSpan(offset + 6), QuantizeRotation(pose.Rotation));
            WriteQuantized(packet.AsSpan(offset + 8), pose.VelocityX, 16f);
            WriteQuantized(packet.AsSpan(offset + 10), pose.VelocityY, 16f);
        }
        else
        {
            WriteSingle(packet.AsSpan(offset), pose.X);
            WriteSingle(packet.AsSpan(offset + 4), pose.Y);
            WriteSingle(packet.AsSpan(offset + 8), pose.Z);
            WriteSingle(packet.AsSpan(offset + 12), pose.Rotation);
            WriteSingle(packet.AsSpan(offset + 16), pose.VelocityX);
            WriteSingle(packet.AsSpan(offset + 20), pose.VelocityY);
        }
        return packet;
    }

    internal static bool TryDecodeFishHookPose(
        ReadOnlySpan<byte> packet, out uint sequence, out FishHookPose pose)
    {
        sequence = 0;
        pose = default;
        if (packet.Length < FishHookPoseFixedSize ||
            !TryDecode(packet, out var type, out sequence) || type != PacketType.FishHookPose)
            return false;
        var encoding = packet[HeaderSize + 24];
        if (encoding > 1 || packet.Length != (encoding == 0
                ? FishHookPoseCompactSize : FishHookPoseFullSize))
            return false;
        var offset = FishHookPoseFixedSize;
        var compact = encoding == 0;
        pose = new FishHookPose(
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize)),
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 4)),
            BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 8)),
            BinaryPrimitives.ReadUInt64LittleEndian(packet.Slice(HeaderSize + 12)),
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 20)),
            compact ? ReadQuantized(packet.Slice(offset), 64f) : ReadSingle(packet.Slice(offset)),
            compact ? ReadQuantized(packet.Slice(offset + 2), 64f) : ReadSingle(packet.Slice(offset + 4)),
            compact ? ReadQuantized(packet.Slice(offset + 4), 64f) : ReadSingle(packet.Slice(offset + 8)),
            compact ? BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(offset + 6)) *
                (360f / ushort.MaxValue) : ReadSingle(packet.Slice(offset + 12)),
            compact ? ReadQuantized(packet.Slice(offset + 8), 16f) : ReadSingle(packet.Slice(offset + 16)),
            compact ? ReadQuantized(packet.Slice(offset + 10), 16f) : ReadSingle(packet.Slice(offset + 20)));
        if (IsValidFishHookPose(pose))
            return true;
        pose = default;
        return false;
    }

    private static bool IsValidFishHookPose(FishHookPose pose) =>
        pose.SceneId != 0 && pose.SceneEpoch != 0 && pose.FishId > 0 &&
        pose.LeaseId != 0 && pose.Tick != 0 &&
        float.IsFinite(pose.X) && float.IsFinite(pose.Y) && float.IsFinite(pose.Z) &&
        float.IsFinite(pose.Rotation) && float.IsFinite(pose.VelocityX) &&
        float.IsFinite(pose.VelocityY) && MathF.Abs(pose.X) <= 1_000_000f &&
        MathF.Abs(pose.Y) <= 1_000_000f && MathF.Abs(pose.Z) <= 1_000_000f &&
        MathF.Abs(pose.VelocityX) <= 10_000f && MathF.Abs(pose.VelocityY) <= 10_000f;

    private static bool CanCompactFishHookPose(FishHookPose pose) =>
        CanQuantize(pose.X, 64f) && CanQuantize(pose.Y, 64f) &&
        CanQuantize(pose.Z, 64f) && CanQuantize(pose.VelocityX, 16f) &&
        CanQuantize(pose.VelocityY, 16f);

    internal static byte[] EncodeFishActionAck(uint sequence, FishActionAck ack)
    {
        if (!IsValidFishActionAck(ack))
            throw new ArgumentOutOfRangeException(nameof(ack));
        var packet = new byte[FishActionAckSize];
        WriteHeader(packet, PacketType.FishActionAck, sequence);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(HeaderSize), ack.RequestId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 8), ack.SceneId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 12), ack.SceneEpoch);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 16), ack.Id);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 20), ack.Revision);
        packet[HeaderSize + 24] = (byte)ack.Action;
        packet[HeaderSize + 25] = (byte)ack.Result;
        packet[HeaderSize + 26] = (byte)ack.RejectReason;
        WriteSingle(packet.AsSpan(HeaderSize + 27), ack.Hp);
        packet[HeaderSize + 31] = (byte)ack.Phase;
        return packet;
    }

    internal static bool TryDecodeFishActionAck(
        ReadOnlySpan<byte> packet, out uint sequence, out FishActionAck ack)
    {
        sequence = 0;
        ack = default;
        if (packet.Length != FishActionAckSize ||
            !TryDecode(packet, out var type, out sequence) || type != PacketType.FishActionAck)
            return false;
        ack = new FishActionAck(
            BinaryPrimitives.ReadUInt64LittleEndian(packet.Slice(HeaderSize)),
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 8)),
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 12)),
            BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 16)),
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 20)),
            (FishAction)packet[HeaderSize + 24],
            (FishActionResult)packet[HeaderSize + 25],
            (FishActionRejectReason)packet[HeaderSize + 26],
            ReadSingle(packet.Slice(HeaderSize + 27)),
            (FishPhase)packet[HeaderSize + 31]);
        if (IsValidFishActionAck(ack))
            return true;
        ack = default;
        return false;
    }

    internal static byte[] EncodeFishLootGrant(uint sequence, FishLootGrant grant)
    {
        if (!IsValidFishLootGrant(grant))
            throw new ArgumentOutOfRangeException(nameof(grant));
        var packet = new byte[FishLootGrantSize];
        WriteHeader(packet, PacketType.FishLootGrant, sequence);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(HeaderSize), grant.TransactionId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 8), grant.SceneId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 12), grant.SceneEpoch);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(HeaderSize + 16), grant.RequestId);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 24), grant.FishId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 28), grant.Revision);
        packet[HeaderSize + 32] = (byte)grant.Action;
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(HeaderSize + 33), grant.Index);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(HeaderSize + 35), grant.EntryCount);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 37), grant.ItemId);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 41), grant.Count);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 45), grant.BonusGrade);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 49), grant.LiftType);
        WriteSingle(packet.AsSpan(HeaderSize + 53), grant.CarriedWeight);
        return packet;
    }

    internal static bool TryDecodeFishLootGrant(
        ReadOnlySpan<byte> packet, out uint sequence, out FishLootGrant grant)
    {
        sequence = 0;
        grant = default;
        if (packet.Length != FishLootGrantSize ||
            !TryDecode(packet, out var type, out sequence) || type != PacketType.FishLootGrant)
            return false;
        grant = new FishLootGrant(
            BinaryPrimitives.ReadUInt64LittleEndian(packet.Slice(HeaderSize)),
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 8)),
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 12)),
            BinaryPrimitives.ReadUInt64LittleEndian(packet.Slice(HeaderSize + 16)),
            BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 24)),
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 28)),
            (FishAction)packet[HeaderSize + 32],
            BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(HeaderSize + 33)),
            BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(HeaderSize + 35)),
            BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 37)),
            BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 41)),
            BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 45)),
            BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 49)),
            ReadSingle(packet.Slice(HeaderSize + 53)));
        if (IsValidFishLootGrant(grant))
            return true;
        grant = default;
        return false;
    }

    private static bool IsValidFishLootGrant(FishLootGrant grant) =>
        grant.TransactionId != 0 && grant.SceneId != 0 && grant.SceneEpoch != 0 &&
        grant.RequestId != 0 && grant.FishId > 0 && grant.Revision != 0 &&
        grant.Action is FishAction.Capture or FishAction.CorpsePickup &&
        grant.EntryCount is > 0 and <= 64 && grant.Index < grant.EntryCount &&
        grant.ItemId > 0 && grant.Count is > 0 and <= 9_999 &&
        grant.BonusGrade is >= 0 and <= 100 && grant.LiftType is >= 0 and <= 3 &&
        float.IsFinite(grant.CarriedWeight) && grant.CarriedWeight is >= 0f and <= 1_000_000f;

    internal static byte[] EncodeFishLootComplete(uint sequence, FishLootComplete complete)
    {
        if (!IsValidFishLootComplete(complete))
            throw new ArgumentOutOfRangeException(nameof(complete));
        var packet = new byte[FishLootCompleteSize];
        WriteHeader(packet, PacketType.FishLootComplete, sequence);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(HeaderSize), complete.TransactionId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 8), complete.SceneId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 12), complete.SceneEpoch);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(HeaderSize + 16), complete.RequestId);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 24), complete.FishId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 28), complete.Revision);
        packet[HeaderSize + 32] = (byte)complete.Action;
        return packet;
    }

    internal static bool TryDecodeFishLootComplete(
        ReadOnlySpan<byte> packet, out uint sequence, out FishLootComplete complete)
    {
        sequence = 0;
        complete = default;
        if (packet.Length != FishLootCompleteSize ||
            !TryDecode(packet, out var type, out sequence) || type != PacketType.FishLootComplete)
            return false;
        complete = new FishLootComplete(
            BinaryPrimitives.ReadUInt64LittleEndian(packet.Slice(HeaderSize)),
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 8)),
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 12)),
            BinaryPrimitives.ReadUInt64LittleEndian(packet.Slice(HeaderSize + 16)),
            BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 24)),
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 28)),
            (FishAction)packet[HeaderSize + 32]);
        if (IsValidFishLootComplete(complete))
            return true;
        complete = default;
        return false;
    }

    private static bool IsValidFishLootComplete(FishLootComplete complete) =>
        complete.TransactionId != 0 && complete.SceneId != 0 && complete.SceneEpoch != 0 &&
        complete.RequestId != 0 && complete.FishId > 0 && complete.Revision != 0 &&
        complete.Action is FishAction.Capture or FishAction.CorpsePickup;

    private static bool IsValidFishLifecycle(FishLifecycle state)
    {
        if (state.SceneId == 0 || state.SceneEpoch == 0 || state.Id <= 0 || state.Revision == 0 ||
            !float.IsFinite(state.Hp) || state.Hp is < 0f or > 1_000_000_000f)
            return false;
        return state.Kind switch
        {
            FishLifecycleKind.Spawn => state.FishDataTID > 0 && state.Phase != FishPhase.None &&
                Enum.IsDefined(typeof(FishPhase), state.Phase),
            FishLifecycleKind.Despawn => state.FishDataTID == 0 && state.Phase == FishPhase.None &&
                state.Hp == 0f,
            FishLifecycleKind.Phase => state.FishDataTID == 0 && state.Phase != FishPhase.None &&
                Enum.IsDefined(typeof(FishPhase), state.Phase),
            FishLifecycleKind.InterestEnter or FishLifecycleKind.InterestLeave =>
                state.FishDataTID == 0 && state.Phase != FishPhase.None &&
                Enum.IsDefined(typeof(FishPhase), state.Phase),
            _ => false
        };
    }

    private static bool IsValidFishActionRequest(FishActionRequest request)
    {
        if (request.RequestId == 0 || request.SceneId == 0 || request.SceneEpoch == 0 || request.Id <= 0)
            return false;
        return request.Action switch
        {
            FishAction.Damage => request.LeaseId == 0 && request.Damage is >= 1 and <= 10_000 &&
                request.Element is >= 0 and <= 64 && request.AttackType is >= 0 and <= 1024,
            FishAction.Qte => request.LeaseId != 0 && request.Damage is >= 1 and <= 10_000 &&
                request.Element is >= 0 and <= 64 && request.AttackType == 0,
            FishAction.Hook or FishAction.CorpsePickup => request.LeaseId == 0 &&
                request.Damage == 0 && request.Element == 0 && request.AttackType == 0,
            FishAction.Release or FishAction.Capture => request.LeaseId != 0 &&
                request.Damage == 0 && request.Element == 0 && request.AttackType == 0,
            _ => false
        };
    }

    private static bool IsValidFishActionAck(FishActionAck ack)
    {
        if (ack.RequestId == 0 || ack.SceneId == 0 || ack.SceneEpoch == 0 || ack.Id <= 0 ||
            !Enum.IsDefined(typeof(FishAction), ack.Action) ||
            !Enum.IsDefined(typeof(FishPhase), ack.Phase) ||
            !float.IsFinite(ack.Hp) || ack.Hp is < 0f or > 1_000_000_000f)
            return false;
        return ack.Result switch
        {
            FishActionResult.Accepted => ack.Revision != 0 && ack.RejectReason == FishActionRejectReason.None &&
                ack.Phase != FishPhase.None,
            FishActionResult.Rejected when ack.RejectReason == FishActionRejectReason.MissingFish =>
                ack.Revision == 0 && ack.Hp == 0f && ack.Phase == FishPhase.None,
            FishActionResult.Rejected => ack.Revision != 0 &&
                ack.RejectReason is >= FishActionRejectReason.SceneMismatch and <= FishActionRejectReason.InternalError,
            _ => false
        };
    }

    internal static byte[] EncodeFishPickupRequest(uint sequence, FishPickupRequest request)
    {
        if (request.SceneId == 0 || request.SceneEpoch == 0 || request.Id <= 0)
            throw new ArgumentOutOfRangeException(nameof(request));
        var packet = new byte[FishPickupRequestSize];
        WriteHeader(packet, PacketType.FishPickupRequest, sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize), request.SceneId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 4), request.SceneEpoch);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 8), request.Id);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 12), request.KnownRevision);
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
        var sceneId = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize));
        var sceneEpoch = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 4));
        var id = BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 8));
        if (sceneId == 0 || sceneEpoch == 0 || id <= 0)
            return false;
        request = new FishPickupRequest(
            sceneId, sceneEpoch, id,
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 12)));
        return true;
    }

    internal static byte[] EncodeFishPickupResult(uint sequence, FishPickupResult result)
    {
        if (result.SceneId == 0 || result.SceneEpoch == 0 || result.Id <= 0 || result.Revision == 0)
            throw new ArgumentOutOfRangeException(nameof(result));
        var packet = new byte[FishPickupResultSize];
        WriteHeader(packet, PacketType.FishPickupResult, sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize), result.SceneId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 4), result.SceneEpoch);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 8), result.Id);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 12), result.Revision);
        packet[HeaderSize + 16] = result.Accepted ? (byte)1 : (byte)0;
        return packet;
    }

    internal static bool TryDecodeFishPickupResult(
        ReadOnlySpan<byte> packet,
        out uint sequence,
        out FishPickupResult result)
    {
        sequence = 0;
        result = default;
        if (packet.Length != FishPickupResultSize ||
            !TryDecode(packet, out var type, out sequence) || type != PacketType.FishPickupResult)
            return false;
        var sceneId = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize));
        var sceneEpoch = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 4));
        var id = BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 8));
        var revision = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 12));
        var accepted = packet[HeaderSize + 16];
        if (sceneId == 0 || sceneEpoch == 0 || id <= 0 || revision == 0 || accepted > 1)
            return false;
        result = new FishPickupResult(sceneId, sceneEpoch, id, revision, accepted != 0);
        return true;
    }

    internal static byte[] EncodeFishRemoved(uint sequence, FishRemoved removed)
    {
        if (removed.SceneId == 0 || removed.SceneEpoch == 0 || removed.Id <= 0 || removed.Revision == 0)
            throw new ArgumentOutOfRangeException(nameof(removed));
        var packet = new byte[FishRemovedSize];
        WriteHeader(packet, PacketType.FishRemoved, sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize), removed.SceneId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 4), removed.SceneEpoch);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 8), removed.Id);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 12), removed.Revision);
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
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 4)),
            BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 8)),
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 12)));
        return removed.SceneId != 0 && removed.SceneEpoch != 0 &&
            removed.Id > 0 && removed.Revision != 0;
    }

    internal static byte[] EncodeFishManifest(uint sequence, FishManifest manifest)
    {
        var uid = StrictUtf8.GetBytes(manifest.AllocatorUid ?? string.Empty);
        if (!IsValidFishManifest(manifest, uid.Length))
            throw new ArgumentOutOfRangeException(nameof(manifest));

        var packet = new byte[FishManifestFixedSize + uid.Length];
        WriteHeader(packet, PacketType.FishManifest, sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize), manifest.SceneId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 4), manifest.SceneEpoch);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 8), manifest.Revision);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 12), manifest.Id);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 16), manifest.FishRevision);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 20), manifest.FishDataTID);
        WriteSingle(packet.AsSpan(HeaderSize + 24), manifest.X);
        WriteSingle(packet.AsSpan(HeaderSize + 28), manifest.Y);
        WriteSingle(packet.AsSpan(HeaderSize + 32), manifest.Z);
        WriteSingle(packet.AsSpan(HeaderSize + 36), manifest.Rotation);
        WriteSingle(packet.AsSpan(HeaderSize + 40), manifest.Hp);
        packet[HeaderSize + 44] = manifest.Flags;
        packet[HeaderSize + 45] = (byte)uid.Length;
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

        var uidLength = packet[HeaderSize + 45];
        if (uidLength == 0 || uidLength > MaxFishAllocatorUidBytes ||
            packet.Length != FishManifestFixedSize + uidLength)
            return false;

        try
        {
            var candidate = new FishManifest(
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize)),
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 8)),
                BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 12)),
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 16)),
                StrictUtf8.GetString(packet.Slice(FishManifestFixedSize, uidLength)),
                BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 20)),
                ReadSingle(packet.Slice(HeaderSize + 24)),
                ReadSingle(packet.Slice(HeaderSize + 28)),
                ReadSingle(packet.Slice(HeaderSize + 32)),
                ReadSingle(packet.Slice(HeaderSize + 36)),
                ReadSingle(packet.Slice(HeaderSize + 40)),
                packet[HeaderSize + 44]);
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
        if (state.SceneId == 0 || state.SceneEpoch == 0 || state.Revision == 0)
            throw new ArgumentOutOfRangeException(nameof(state));
        var packet = new byte[FishManifestStateSize];
        WriteHeader(packet, PacketType.FishManifestState, sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize), state.SceneId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 4), state.SceneEpoch);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 8), state.Revision);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(HeaderSize + 12), state.EntryCount);
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
        var sceneEpoch = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 4));
        var revision = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 8));
        if (sceneId == 0 || sceneEpoch == 0 || revision == 0)
            return false;
        state = new FishManifestState(
            sceneId, sceneEpoch, revision,
            BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(HeaderSize + 12)));
        return true;
    }

    internal static byte[] EncodePickupRemoved(uint sequence, PickupRemoved removed)
    {
        if (removed.SceneId == 0 || removed.SceneEpoch == 0 ||
            removed.WorldId == 0 || removed.ItemId <= 0)
            throw new ArgumentOutOfRangeException(nameof(removed));
        var packet = new byte[PickupRemovedSize];
        WriteHeader(packet, PacketType.PickupRemoved, sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize), removed.SceneId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 4), removed.SceneEpoch);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 8), removed.WorldId);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 12), removed.ItemId);
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
        var sceneId = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize));
        var sceneEpoch = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 4));
        var worldId = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 8));
        var itemId = BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 12));
        if (sceneId == 0 || sceneEpoch == 0 || worldId == 0 || itemId <= 0)
            return false;
        removed = new PickupRemoved(sceneId, sceneEpoch, worldId, itemId);
        return true;
    }

    internal static byte[] EncodePickupRequest(uint sequence, PickupRequest request)
    {
        if (!IsValidPickupRequest(request))
            throw new ArgumentOutOfRangeException(nameof(request));
        var packet = new byte[PickupRequestSize];
        WriteHeader(packet, PacketType.PickupRequest, sequence);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(HeaderSize), request.RequestId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 8), request.SceneId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 12), request.SceneEpoch);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 16), request.WorldId);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 20), request.ItemId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 24), request.KnownRevision);
        WriteSingle(packet.AsSpan(HeaderSize + 28), request.ExpectedX);
        WriteSingle(packet.AsSpan(HeaderSize + 32), request.ExpectedY);
        return packet;
    }

    internal static bool TryDecodePickupRequest(
        ReadOnlySpan<byte> packet,
        out uint sequence,
        out PickupRequest request)
    {
        sequence = 0;
        request = default;
        if (packet.Length != PickupRequestSize ||
            !TryDecode(packet, out var type, out sequence) || type != PacketType.PickupRequest)
            return false;
        var candidate = new PickupRequest(
            BinaryPrimitives.ReadUInt64LittleEndian(packet.Slice(HeaderSize)),
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 8)),
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 12)),
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 16)),
            BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 20)),
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 24)),
            ReadSingle(packet.Slice(HeaderSize + 28)),
            ReadSingle(packet.Slice(HeaderSize + 32)));
        if (!IsValidPickupRequest(candidate))
            return false;
        request = candidate;
        return true;
    }

    internal static byte[] EncodePickupResult(uint sequence, PickupResult result)
    {
        if (!IsValidPickupResult(result))
            throw new ArgumentOutOfRangeException(nameof(result));
        var packet = new byte[PickupResultSize];
        WriteHeader(packet, PacketType.PickupResult, sequence);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(HeaderSize), result.RequestId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 8), result.SceneId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 12), result.SceneEpoch);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 16), result.WorldId);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 20), result.ItemId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 24), result.Revision);
        packet[HeaderSize + 28] = result.Accepted ? (byte)1 : (byte)0;
        packet[HeaderSize + 29] = (byte)result.RejectReason;
        return packet;
    }

    internal static bool TryDecodePickupResult(
        ReadOnlySpan<byte> packet,
        out uint sequence,
        out PickupResult result)
    {
        sequence = 0;
        result = default;
        if (packet.Length != PickupResultSize ||
            !TryDecode(packet, out var type, out sequence) || type != PacketType.PickupResult ||
            packet[HeaderSize + 28] > 1)
            return false;
        var candidate = new PickupResult(
            BinaryPrimitives.ReadUInt64LittleEndian(packet.Slice(HeaderSize)),
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 8)),
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 12)),
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 16)),
            BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 20)),
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 24)),
            packet[HeaderSize + 28] != 0,
            (PickupRejectReason)packet[HeaderSize + 29]);
        if (!IsValidPickupResult(candidate))
            return false;
        result = candidate;
        return true;
    }

    internal static byte[] EncodeSceneTransition(uint sequence, SceneTransitionCommand command)
    {
        var sceneName = command.SceneName ?? string.Empty;
        var encodedName = StrictUtf8.GetBytes(sceneName);
        if (encodedName.Length is < 1 or > 128 || (command.Options & 0x7e00) != 0 ||
            command.SceneId != SceneId(sceneName) || command.SceneEpoch == 0 || command.Seed == 0)
            throw new ArgumentOutOfRangeException(nameof(command));
        var packet = new byte[HeaderSize + 19 + encodedName.Length];
        WriteHeader(packet, PacketType.SceneTransition, sequence);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize), command.TransitionType);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(HeaderSize + 4), command.Options);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 6), command.SceneId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 10), command.SceneEpoch);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 14), command.Seed);
        packet[HeaderSize + 18] = (byte)encodedName.Length;
        encodedName.CopyTo(packet, HeaderSize + 19);
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
            packet.Length < HeaderSize + 20 || packet[HeaderSize + 18] is < 1 or > 128 ||
            packet.Length != HeaderSize + 19 + packet[HeaderSize + 18])
            return false;
        var options = BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(HeaderSize + 4));
        if ((options & 0x7e00) != 0)
            return false;
        try
        {
            var sceneName = StrictUtf8.GetString(packet.Slice(HeaderSize + 19));
            var sceneId = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 6));
            var sceneEpoch = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 10));
            var seed = BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 14));
            if (string.IsNullOrWhiteSpace(sceneName) || sceneId != SceneId(sceneName) ||
                sceneEpoch == 0 || seed == 0)
                return false;
            command = new SceneTransitionCommand(
                sceneName,
                BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize)),
                options,
                sceneId,
                sceneEpoch,
                seed);
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
        WriteHeader(packet, PacketType.IngredientsSyncRequest, sequence);
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

    internal static byte[] EncodeSaveSnapshotChunk(uint sequence, SaveSnapshotChunk chunk)
    {
        var data = chunk.Data ?? throw new ArgumentNullException(nameof(chunk.Data));
        if (!IsValidSaveSnapshotChunk(chunk, data.Length))
            throw new ArgumentOutOfRangeException(nameof(chunk));
        var packet = new byte[SaveSnapshotChunkFixedSize + data.Length];
        WriteHeader(packet, PacketType.SaveSnapshotChunk, sequence);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(HeaderSize), chunk.TransferId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 8), chunk.Fingerprint);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 12), chunk.TotalBytes);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(HeaderSize + 16), chunk.ChunkIndex);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(HeaderSize + 18), chunk.ChunkCount);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(HeaderSize + 20), (ushort)data.Length);
        data.CopyTo(packet.AsSpan(SaveSnapshotChunkFixedSize));
        return packet;
    }

    internal static bool TryDecodeSaveSnapshotChunk(
        ReadOnlySpan<byte> packet,
        out uint sequence,
        out SaveSnapshotChunk chunk)
    {
        sequence = 0;
        chunk = default;
        if (packet.Length < SaveSnapshotChunkFixedSize ||
            !TryDecode(packet, out var type, out sequence) || type != PacketType.SaveSnapshotChunk)
            return false;
        var dataLength = BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(HeaderSize + 20));
        if (dataLength > MaxSaveSnapshotChunkBytes ||
            packet.Length != SaveSnapshotChunkFixedSize + dataLength)
            return false;
        var candidate = new SaveSnapshotChunk(
            BinaryPrimitives.ReadUInt64LittleEndian(packet.Slice(HeaderSize)),
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 8)),
            BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 12)),
            BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(HeaderSize + 16)),
            BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(HeaderSize + 18)),
            packet.Slice(SaveSnapshotChunkFixedSize).ToArray());
        if (!IsValidSaveSnapshotChunk(candidate, dataLength))
            return false;
        chunk = candidate;
        return true;
    }

    internal static byte[] EncodeSaveSnapshotAck(uint sequence, SaveSnapshotAck ack)
    {
        if (ack.TransferId == 0 || ack.Fingerprint == 0)
            throw new ArgumentOutOfRangeException(nameof(ack));
        var packet = new byte[SaveSnapshotAckSize];
        WriteHeader(packet, PacketType.SaveSnapshotAck, sequence);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(HeaderSize), ack.TransferId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 8), ack.Fingerprint);
        packet[HeaderSize + 12] = ack.Loaded ? (byte)1 : (byte)0;
        return packet;
    }

    internal static bool TryDecodeSaveSnapshotAck(
        ReadOnlySpan<byte> packet,
        out uint sequence,
        out SaveSnapshotAck ack)
    {
        sequence = 0;
        ack = default;
        if (packet.Length != SaveSnapshotAckSize ||
            !TryDecode(packet, out var type, out sequence) || type != PacketType.SaveSnapshotAck ||
            packet[HeaderSize + 12] > 1)
            return false;
        ack = new SaveSnapshotAck(
            BinaryPrimitives.ReadUInt64LittleEndian(packet.Slice(HeaderSize)),
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 8)),
            packet[HeaderSize + 12] == 1);
        return ack.TransferId != 0 && ack.Fingerprint != 0;
    }

    private static bool IsValidSaveSnapshotChunk(SaveSnapshotChunk chunk, int dataLength)
    {
        if (chunk.TransferId == 0 || chunk.Fingerprint == 0 ||
            chunk.TotalBytes <= 0 || chunk.TotalBytes > MaxSaveSnapshotChunkBytes * MaxSaveSnapshotChunks ||
            chunk.ChunkCount is < 1 or > MaxSaveSnapshotChunks ||
            chunk.ChunkIndex >= chunk.ChunkCount || dataLength is < 1 or > MaxSaveSnapshotChunkBytes)
            return false;
        var expectedChunks = (chunk.TotalBytes + MaxSaveSnapshotChunkBytes - 1) /
            MaxSaveSnapshotChunkBytes;
        var start = (long)chunk.ChunkIndex * MaxSaveSnapshotChunkBytes;
        return chunk.ChunkCount == expectedChunks && start < chunk.TotalBytes &&
            start + dataLength <= chunk.TotalBytes &&
            (chunk.ChunkIndex + 1 == chunk.ChunkCount || dataLength == MaxSaveSnapshotChunkBytes);
    }

    internal static byte[] EncodeDiveReady(uint sequence, DiveReady ready)
    {
        if (ready.Revision == 0)
            throw new ArgumentOutOfRangeException(nameof(ready));
        var packet = new byte[DiveReadyPacketSize];
        WriteHeader(packet, PacketType.DiveReady, sequence);
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
        if (state.Revision == 0 || state.SceneId == 0 || state.SceneEpoch == 0 || state.Seed == 0)
            throw new ArgumentOutOfRangeException(nameof(state));
        var packet = new byte[DiveStatePacketSize];
        WriteHeader(packet, PacketType.DiveState, sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize), state.Revision);
        packet[HeaderSize + 4] = state.HostReady ? (byte)1 : (byte)0;
        packet[HeaderSize + 5] = state.ClientReady ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 6), state.SceneId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 10), state.SceneEpoch);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 14), state.Seed);
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
        var sceneId = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 6));
        var sceneEpoch = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 10));
        var seed = BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 14));
        if (revision == 0 || sceneId == 0 || sceneEpoch == 0 || seed == 0)
            return false;
        state = new DiveState(
            revision, packet[HeaderSize + 4] == 1, packet[HeaderSize + 5] == 1,
            sceneId, sceneEpoch, seed);
        return true;
    }

    internal static byte[] EncodeBoatDecoState(uint sequence, BoatDecoState state)
    {
        var packet = new byte[BoatDecoStatePacketSize];
        WriteHeader(packet, PacketType.BoatDecoState, sequence);
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
        WriteHeader(packet, PacketType.DiverLifeState, sequence);
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
        WriteHeader(packet, PacketType.DiveExitRequest, sequence);
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
        var sceneNameBytes = StrictUtf8.GetBytes(ready.Route.SceneName ?? string.Empty);
        if (ready.TargetId == 0 || ready.Revision == 0 ||
            (!ready.Ready && ready.NativeStarted) ||
            sceneNameBytes.Length > MaxTravelSceneNameBytes)
            throw new ArgumentOutOfRangeException(nameof(ready));
        var packet = new byte[TravelReadyFixedSize + sceneNameBytes.Length];
        WriteHeader(packet, PacketType.TravelReady, sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize), ready.TargetId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 4), ready.Revision);
        packet[HeaderSize + 8] = ready.Ready ? (byte)1 : (byte)0;
        packet[HeaderSize + 9] = ready.NativeStarted ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 10), ready.Route.SceneType);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 14), ready.Route.Location);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 18), ready.Route.TransitionType);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(HeaderSize + 22), (ushort)sceneNameBytes.Length);
        sceneNameBytes.CopyTo(packet.AsSpan(HeaderSize + 24));
        return packet;
    }

    internal static bool TryDecodeTravelReady(
        ReadOnlySpan<byte> packet,
        out uint sequence,
        out TravelReady ready)
    {
        sequence = 0;
        ready = default;
        if (packet.Length < TravelReadyFixedSize ||
            !TryDecode(packet, out var type, out sequence) || type != PacketType.TravelReady ||
            packet[HeaderSize + 8] > 1 || packet[HeaderSize + 9] > 1 ||
            (packet[HeaderSize + 8] == 0 && packet[HeaderSize + 9] == 1))
            return false;
        var targetId = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize));
        var revision = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 4));
        var sceneNameLength = BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(HeaderSize + 22));
        if (targetId == 0 || revision == 0 ||
            sceneNameLength > MaxTravelSceneNameBytes ||
            packet.Length != TravelReadyFixedSize + sceneNameLength)
            return false;
        string sceneName;
        try
        {
            sceneName = StrictUtf8.GetString(packet.Slice(HeaderSize + 24, sceneNameLength));
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
        ready = new TravelReady(
            targetId, revision,
            packet[HeaderSize + 8] == 1, packet[HeaderSize + 9] == 1,
            new TravelRoute(
                sceneName,
                BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 10)),
                BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 14)),
                BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 18))));
        return true;
    }

    internal static byte[] EncodeTravelState(uint sequence, TravelState state)
    {
        if (state.TargetId == 0 || state.Revision == 0 ||
            (state.HostDead && !state.SoloAllowed))
            throw new ArgumentOutOfRangeException(nameof(state));
        var packet = new byte[TravelStatePacketSize];
        WriteHeader(packet, PacketType.TravelState, sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize), state.TargetId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize + 4), state.Revision);
        packet[HeaderSize + 8] = state.HostReady ? (byte)1 : (byte)0;
        packet[HeaderSize + 9] = state.ClientReady ? (byte)1 : (byte)0;
        packet[HeaderSize + 10] = state.SoloAllowed ? (byte)1 : (byte)0;
        packet[HeaderSize + 11] = state.HostDead ? (byte)1 : (byte)0;
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
            packet[HeaderSize + 8] > 1 || packet[HeaderSize + 9] > 1 ||
            packet[HeaderSize + 10] > 1 || packet[HeaderSize + 11] > 1 ||
            (packet[HeaderSize + 11] == 1 && packet[HeaderSize + 10] == 0))
            return false;
        var targetId = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize));
        var revision = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 4));
        if (targetId == 0 || revision == 0)
            return false;
        state = new TravelState(
            targetId, revision,
            packet[HeaderSize + 8] == 1, packet[HeaderSize + 9] == 1,
            packet[HeaderSize + 10] == 1, packet[HeaderSize + 11] == 1);
        return true;
    }

    internal static byte[] EncodeDiveLootRequest(uint sequence, DiveLootRequest request)
    {
        if (!IsValidLoot(request.ItemId, request.Count, request.BonusGrade, request.LiftType))
            throw new ArgumentOutOfRangeException(nameof(request));
        var packet = new byte[DiveLootRequestPacketSize];
        WriteHeader(packet, PacketType.DiveLootRequest, sequence);
        if (request.SourceId == 0)
            throw new ArgumentOutOfRangeException(nameof(request));
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(HeaderSize), request.SourceId);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 8), request.ItemId);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 12), request.Count);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 16), request.BonusGrade);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 20), request.LiftType);
        packet[HeaderSize + 24] = request.UpdateMission ? (byte)1 : (byte)0;
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
            packet[HeaderSize + 24] > 1)
            return false;
        request = new DiveLootRequest(
            BinaryPrimitives.ReadUInt64LittleEndian(packet.Slice(HeaderSize)),
            BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 8)),
            BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 12)),
            BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 16)),
            BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 20)),
            packet[HeaderSize + 24] == 1);
        return request.SourceId != 0 &&
            IsValidLoot(request.ItemId, request.Count, request.BonusGrade, request.LiftType);
    }

    internal static byte[] EncodeDiveResultEntry(uint sequence, DiveResultEntry entry)
    {
        if (entry.TransferId == 0 || entry.Total == 0 || entry.Index >= entry.Total ||
            !IsValidLoot(entry.ItemId, entry.Count, entry.BonusGrade, entry.LiftType))
            throw new ArgumentOutOfRangeException(nameof(entry));
        var packet = new byte[DiveResultEntryPacketSize];
        WriteHeader(packet, PacketType.DiveResultEntry, sequence);
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
        WriteHeader(packet, PacketType.DiveResultState, sequence);
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
        if (state.Revision == 0 || state.MissionId <= 0 || state.Progress < 0 ||
            state.State > 6 || state.CurrentTaskId < 0 || conditions.Length > MaxMissionConditions ||
            !AreValidMissionConditions(conditions))
            throw new ArgumentOutOfRangeException(nameof(state));
        var packet = new byte[MissionStateFixedSize + conditions.Length * MissionConditionStateSize];
        WriteHeader(packet, PacketType.MissionState, sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize), state.Revision);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 4), state.MissionId);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 8), state.Progress);
        packet[HeaderSize + 12] = state.State;
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(HeaderSize + 13), state.CurrentTaskId);
        packet[HeaderSize + 17] = (byte)conditions.Length;
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
        var count = packet[HeaderSize + 17];
        if (count > MaxMissionConditions ||
            packet.Length != MissionStateFixedSize + count * MissionConditionStateSize)
            return false;
        var revision = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize));
        var missionId = BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 4));
        var progress = BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 8));
        var stateValue = packet[HeaderSize + 12];
        var currentTaskId = BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 13));
        if (revision == 0 || missionId <= 0 || progress < 0 || stateValue > 6 || currentTaskId < 0)
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
        state = new MissionState(revision, missionId, progress, stateValue, currentTaskId, conditions);
        return true;
    }

    internal static byte[] EncodeMissionRoster(uint sequence, MissionRoster roster)
    {
        var missionIds = roster.MissionIds ?? throw new ArgumentNullException(nameof(roster));
        if (roster.Revision == 0 || missionIds.Length > MaxMissionRosterEntries ||
            !AreValidMissionIds(missionIds))
            throw new ArgumentOutOfRangeException(nameof(roster));
        var packet = new byte[MissionRosterFixedSize + missionIds.Length * sizeof(int)];
        WriteHeader(packet, PacketType.MissionRoster, sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize), roster.Revision);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(HeaderSize + 4), (ushort)missionIds.Length);
        for (var index = 0; index < missionIds.Length; index++)
            BinaryPrimitives.WriteInt32LittleEndian(
                packet.AsSpan(MissionRosterFixedSize + index * sizeof(int)), missionIds[index]);
        return packet;
    }

    internal static bool TryDecodeMissionRoster(
        ReadOnlySpan<byte> packet,
        out uint sequence,
        out MissionRoster roster)
    {
        sequence = 0;
        roster = default;
        if (packet.Length < MissionRosterFixedSize ||
            !TryDecode(packet, out var type, out sequence) || type != PacketType.MissionRoster)
            return false;
        var revision = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize));
        var count = BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(HeaderSize + 4));
        if (revision == 0 || count > MaxMissionRosterEntries ||
            packet.Length != MissionRosterFixedSize + count * sizeof(int))
            return false;
        var missionIds = new int[count];
        for (var index = 0; index < count; index++)
            missionIds[index] = BinaryPrimitives.ReadInt32LittleEndian(
                packet.Slice(MissionRosterFixedSize + index * sizeof(int)));
        if (!AreValidMissionIds(missionIds))
            return false;
        roster = new MissionRoster(revision, missionIds);
        return true;
    }

    internal static byte[] EncodeWorldFlagRequest(uint sequence, WorldFlagRequest request) =>
        EncodeWorldFlag(PacketType.WorldFlagRequest, sequence, 0, request.Key, request.Value);

    internal static byte[] EncodeWorldFlagState(uint sequence, WorldFlagState state)
    {
        if (state.Revision == 0)
            throw new ArgumentOutOfRangeException(nameof(state));
        return EncodeWorldFlag(PacketType.WorldFlagState, sequence, state.Revision, state.Key, state.Value);
    }

    internal static bool TryDecodeWorldFlagRequest(
        ReadOnlySpan<byte> packet,
        out uint sequence,
        out WorldFlagRequest request)
    {
        request = default;
        if (!TryDecodeWorldFlag(packet, PacketType.WorldFlagRequest, out sequence,
                out var revision, out var key, out var value) || revision != 0)
            return false;
        request = new WorldFlagRequest(key, value);
        return true;
    }

    internal static bool TryDecodeWorldFlagState(
        ReadOnlySpan<byte> packet,
        out uint sequence,
        out WorldFlagState state)
    {
        state = default;
        if (!TryDecodeWorldFlag(packet, PacketType.WorldFlagState, out sequence,
                out var revision, out var key, out var value) || revision == 0)
            return false;
        state = new WorldFlagState(revision, key, value);
        return true;
    }

    private static byte[] EncodeWorldFlag(
        PacketType type,
        uint sequence,
        uint revision,
        string key,
        bool value)
    {
        var encodedKey = StrictUtf8.GetBytes(key ?? string.Empty);
        if (encodedKey.Length is < 1 or > MaxWorldFlagKeyBytes)
            throw new ArgumentOutOfRangeException(nameof(key));
        var packet = new byte[HeaderSize + 6 + encodedKey.Length];
        WriteHeader(packet, type, sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize), revision);
        packet[HeaderSize + 4] = value ? (byte)1 : (byte)0;
        packet[HeaderSize + 5] = (byte)encodedKey.Length;
        encodedKey.CopyTo(packet, HeaderSize + 6);
        return packet;
    }

    private static bool TryDecodeWorldFlag(
        ReadOnlySpan<byte> packet,
        PacketType expectedType,
        out uint sequence,
        out uint revision,
        out string key,
        out bool value)
    {
        sequence = 0;
        revision = 0;
        key = string.Empty;
        value = false;
        if (packet.Length < HeaderSize + 7 ||
            !TryDecode(packet, out var type, out sequence) || type != expectedType ||
            packet[HeaderSize + 4] > 1)
            return false;
        var keyLength = packet[HeaderSize + 5];
        if (keyLength is < 1 or > MaxWorldFlagKeyBytes ||
            packet.Length != HeaderSize + 6 + keyLength)
            return false;
        try
        {
            key = StrictUtf8.GetString(packet.Slice(HeaderSize + 6));
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
        revision = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize));
        value = packet[HeaderSize + 4] == 1;
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
        WriteHeader(packet, PacketType.IngredientsSnapshotChunk, sequence);
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
        WriteHeader(packet, PacketType.IngredientsDelta, sequence);
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
        manifest.SceneId != 0 && manifest.SceneEpoch != 0 && manifest.Revision > 0 &&
        manifest.Id > 0 && manifest.FishRevision > 0 && manifest.FishDataTID > 0 &&
        uidByteLength is > 0 and <= MaxFishAllocatorUidBytes &&
        !string.IsNullOrWhiteSpace(manifest.AllocatorUid) &&
        manifest.AllocatorUid.IndexOf('\0') < 0 &&
        float.IsFinite(manifest.X) && float.IsFinite(manifest.Y) &&
        float.IsFinite(manifest.Z) && float.IsFinite(manifest.Rotation) &&
        float.IsFinite(manifest.Hp) && manifest.Hp is >= 0f and <= 1_000_000_000f &&
        MathF.Abs(manifest.X) <= 1_000_000f && MathF.Abs(manifest.Y) <= 1_000_000f &&
        MathF.Abs(manifest.Z) <= 1_000_000f && manifest.Flags <= 15;

    private static bool IsValidVisualSprite(VisualSprite sprite) =>
        sprite.SpriteId != 0 && float.IsFinite(sprite.OffsetX) && float.IsFinite(sprite.OffsetY) &&
        float.IsFinite(sprite.OffsetZ) && float.IsFinite(sprite.Rotation) &&
        float.IsFinite(sprite.ScaleX) && float.IsFinite(sprite.ScaleY) &&
        MathF.Abs(sprite.OffsetX) <= 1_000_000f && MathF.Abs(sprite.OffsetY) <= 1_000_000f &&
        MathF.Abs(sprite.OffsetZ) <= 1_000_000f && sprite.ScaleX is > 0f and <= 100f &&
        sprite.ScaleY is > 0f and <= 100f;

    private static bool IsValidProjectileVisualState(ProjectileVisualState state) =>
        state.SceneId != 0 && state.SceneEpoch != 0 && state.Id != 0 && state.SpriteId != 0 &&
        float.IsFinite(state.X) && float.IsFinite(state.Y) && float.IsFinite(state.Z) &&
        float.IsFinite(state.Rotation) && float.IsFinite(state.ScaleX) &&
        float.IsFinite(state.ScaleY) && MathF.Abs(state.X) <= 1_000_000f &&
        MathF.Abs(state.Y) <= 1_000_000f && MathF.Abs(state.Z) <= 1_000_000f &&
        state.ScaleX is > 0f and <= 100f && MathF.Abs(state.ScaleY) is > 0f and <= 100f &&
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

    private static void WriteDiverRuntimePayload(Span<byte> payload, DiverRuntimeState state)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(payload, state.SceneId);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.Slice(4), state.SceneEpoch);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.Slice(8), state.Revision);
        payload[12] = (byte)state.Owner;
        payload[13] = state.IsDead ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.Slice(14), (ushort)state.Fields);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.Slice(16), (ushort)state.Flags);
        WriteSingle(payload.Slice(18), state.Hp);
        WriteSingle(payload.Slice(22), state.MaxHp);
        WriteSingle(payload.Slice(26), state.Oxygen);
        WriteSingle(payload.Slice(30), state.MaxOxygen);
        WriteSingle(payload.Slice(34), state.CargoWeight);
        BinaryPrimitives.WriteInt32LittleEndian(payload.Slice(38), state.WeaponId);
        BinaryPrimitives.WriteInt32LittleEndian(payload.Slice(42), state.Ammo);
        BinaryPrimitives.WriteInt32LittleEndian(payload.Slice(46), state.MaxAmmo);
    }

    private static bool TryReadDiverRuntimePayload(
        ReadOnlySpan<byte> payload,
        out DiverRuntimeState state)
    {
        state = default;
        if (payload.Length != DiverRuntimePayloadSize || payload[13] > 1)
            return false;
        var candidate = new DiverRuntimeState(
            BinaryPrimitives.ReadUInt32LittleEndian(payload),
            BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(4)),
            BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(8)),
            (DiverOwner)payload[12],
            payload[13] != 0,
            (DiverRuntimeFields)BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(14)),
            (DiverRuntimeFlags)BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(16)),
            ReadSingle(payload.Slice(18)),
            ReadSingle(payload.Slice(22)),
            ReadSingle(payload.Slice(26)),
            ReadSingle(payload.Slice(30)),
            ReadSingle(payload.Slice(34)),
            BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(38)),
            BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(42)),
            BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(46)));
        if (!IsValidDiverRuntimeState(candidate))
            return false;
        state = candidate;
        return true;
    }

    private static bool IsValidDiverRuntimeState(DiverRuntimeState state)
    {
        const DiverRuntimeFields knownFields = DiverRuntimeFields.Health |
            DiverRuntimeFields.Oxygen | DiverRuntimeFields.Cargo |
            DiverRuntimeFields.Weapon | DiverRuntimeFields.Ammo;
        const DiverRuntimeFlags knownFlags = DiverRuntimeFlags.Invulnerable |
            DiverRuntimeFlags.OxygenDepleting | DiverRuntimeFlags.Overweight |
            DiverRuntimeFlags.Reviving;
        if (state.SceneId == 0 || state.SceneEpoch == 0 || state.Revision == 0 ||
            state.Owner is not (DiverOwner.Host or DiverOwner.Client) ||
            (state.Fields & ~knownFields) != 0 || (state.Flags & ~knownFlags) != 0)
            return false;

        var hasHealth = (state.Fields & DiverRuntimeFields.Health) != 0;
        if (hasHealth
                ? !IsValidRuntimeValuePair(state.Hp, state.MaxHp)
                : state.Hp != 0f || state.MaxHp != 0f)
            return false;

        var hasOxygen = (state.Fields & DiverRuntimeFields.Oxygen) != 0;
        if (hasOxygen
                ? !IsValidRuntimeValuePair(state.Oxygen, state.MaxOxygen)
                : state.Oxygen != 0f || state.MaxOxygen != 0f)
            return false;
        if ((state.Flags & DiverRuntimeFlags.OxygenDepleting) != 0 && !hasOxygen)
            return false;

        var hasCargo = (state.Fields & DiverRuntimeFields.Cargo) != 0;
        if (hasCargo
                ? !float.IsFinite(state.CargoWeight) ||
                  state.CargoWeight is < 0f or > 1_000_000f
                : state.CargoWeight != 0f)
            return false;
        if ((state.Flags & DiverRuntimeFlags.Overweight) != 0 && !hasCargo)
            return false;

        var hasWeapon = (state.Fields & DiverRuntimeFields.Weapon) != 0;
        var hasAmmo = (state.Fields & DiverRuntimeFields.Ammo) != 0;
        if (hasWeapon != hasAmmo)
            return false;
        if (!hasWeapon)
            return state.WeaponId == 0 && state.Ammo == 0 && state.MaxAmmo == 0;
        return state.WeaponId == 0
            ? state.Ammo == 0 && state.MaxAmmo == 0
            : state.WeaponId > 0 && state.MaxAmmo is > 0 and <= 1_000_000 &&
              state.Ammo >= 0 && state.Ammo <= state.MaxAmmo;
    }

    private static bool IsValidRuntimeValuePair(float current, float maximum) =>
        float.IsFinite(current) && float.IsFinite(maximum) &&
        maximum is > 0f and <= 1_000_000f && current >= 0f && current <= maximum;

    private static bool IsValidDiverVitalResult(DiverVitalResult result)
    {
        const DiverVitalEdges knownEdges = DiverVitalEdges.Damaged | DiverVitalEdges.Healed |
            DiverVitalEdges.Died | DiverVitalEdges.Revived;
        var state = result.State;
        if (result.CommitRevision == 0 || result.EventId == 0 ||
            !Enum.IsDefined(typeof(DiverVitalCause), result.Cause) ||
            result.Edges == DiverVitalEdges.None || (result.Edges & ~knownEdges) != 0 ||
            !float.IsFinite(result.AppliedAmount) ||
            result.AppliedAmount is < 0f or > 1_000_000f ||
            !IsValidDiverRuntimeState(state) || state.Owner != DiverOwner.Client ||
            (state.Fields & (DiverRuntimeFields.Health | DiverRuntimeFields.Oxygen)) !=
                (DiverRuntimeFields.Health | DiverRuntimeFields.Oxygen) ||
            state.Hp != state.Oxygen || state.MaxHp != state.MaxOxygen ||
            state.IsDead != (state.Hp == 0f))
            return false;

        return result.Cause switch
        {
            DiverVitalCause.Damage =>
                result.AppliedAmount > 0f &&
                (result.Edges == DiverVitalEdges.Damaged ||
                 result.Edges == (DiverVitalEdges.Damaged | DiverVitalEdges.Died)) &&
                state.IsDead == ((result.Edges & DiverVitalEdges.Died) != 0),
            DiverVitalCause.Heal or DiverVitalCause.OxygenRestore =>
                result.AppliedAmount > 0f && result.Edges == DiverVitalEdges.Healed &&
                !state.IsDead,
            DiverVitalCause.OxygenDepleted =>
                result.Edges == DiverVitalEdges.Died && state.IsDead,
            DiverVitalCause.Revive =>
                result.AppliedAmount > 0f && result.Edges == DiverVitalEdges.Revived &&
                !state.IsDead,
            _ => false
        };
    }

    private static bool IsValidDiverWeaponIntent(DiverWeaponIntent intent) =>
        intent.SceneId != 0 && intent.SceneEpoch != 0 && intent.RequestId != 0 &&
        Enum.IsDefined(typeof(DiverWeaponAction), intent.Action) &&
        (intent.Action == DiverWeaponAction.Unequip
            ? intent.WeaponId == 0
            : intent.WeaponId > 0);

    private static bool IsValidDiverVitalIntent(DiverVitalIntent intent) =>
        intent.SceneId != 0 && intent.SceneEpoch != 0 && intent.RequestId != 0 &&
        Enum.IsDefined(typeof(DiverVitalIntentKind), intent.Kind) &&
        float.IsFinite(intent.Amount) && intent.Amount > 0f && intent.Amount <= 1_000_000f;

    private static bool IsValidDiverWeaponResult(DiverWeaponResult result)
    {
        if (result.CommitRevision == 0 || result.RequestId == 0 ||
            !Enum.IsDefined(typeof(DiverWeaponAction), result.Action) ||
            !Enum.IsDefined(typeof(DiverWeaponRejectReason), result.RejectReason) ||
            result.Accepted != (result.RejectReason == DiverWeaponRejectReason.None) ||
            !IsValidDiverRuntimeState(result.State) || result.State.Owner != DiverOwner.Client)
            return false;

        var weaponFields = result.State.Fields &
            (DiverRuntimeFields.Weapon | DiverRuntimeFields.Ammo);
        if (weaponFields != (DiverRuntimeFields.Weapon | DiverRuntimeFields.Ammo))
            return false;
        var equipped = result.State.WeaponId != 0;
        var unequipped = !equipped;

        if (result.Accepted &&
            (result.Action == DiverWeaponAction.Unequip ? !unequipped : !equipped))
            return false;
        if (!result.Accepted)
            return result.AppliedRounds == 0;

        return result.Action switch
        {
            DiverWeaponAction.Fire or DiverWeaponAction.Reload =>
                result.AppliedRounds > 0 &&
                result.AppliedRounds <= result.State.MaxAmmo,
            DiverWeaponAction.Equip or DiverWeaponAction.Unequip =>
                result.AppliedRounds == 0,
            _ => false
        };
    }

    private static bool IsValidPickupRequest(PickupRequest request) =>
        request.RequestId != 0 && request.SceneId != 0 && request.SceneEpoch != 0 &&
        request.WorldId != 0 && request.ItemId > 0 && request.KnownRevision != 0 &&
        float.IsFinite(request.ExpectedX) && float.IsFinite(request.ExpectedY) &&
        MathF.Abs(request.ExpectedX) <= 1_000_000f &&
        MathF.Abs(request.ExpectedY) <= 1_000_000f;

    private static bool IsValidPickupResult(PickupResult result) =>
        result.RequestId != 0 && result.SceneId != 0 && result.SceneEpoch != 0 &&
        result.WorldId != 0 && result.ItemId > 0 && result.Revision != 0 &&
        result.RejectReason is >= PickupRejectReason.None and <= PickupRejectReason.InternalError &&
        (result.Accepted
            ? result.RejectReason == PickupRejectReason.None
            : result.RejectReason != PickupRejectReason.None);

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
        WriteHeader(packet, type, sequence);
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

    private static bool AreValidMissionIds(int[] missionIds)
    {
        for (var index = 0; index < missionIds.Length; index++)
            if (missionIds[index] <= 0 || index > 0 && missionIds[index - 1] >= missionIds[index])
                return false;
        return true;
    }

    private static bool IsNewer(uint value, uint previous) =>
        unchecked((int)(value - previous)) > 0;

    internal static void SelfTest()
    {
        var packet = Encode(PacketType.Hello, 42);
        SetSessionId(packet, 0x123456789abcdef0);
        if (!TryDecode(packet, out var type, out var sequence, out var sessionId) ||
            type != PacketType.Hello || sequence != 42 || sessionId != 0x123456789abcdef0)
            throw new InvalidOperationException("Protocol round-trip failed");

        var ack = Encode(PacketType.Ack, 41);
        if (!TryDecode(ack, out type, out sequence) || type != PacketType.Ack || sequence != 41)
            throw new InvalidOperationException("Protocol acknowledgement round-trip failed");

        packet[0] ^= 0xff;
        if (TryDecode(packet, out _, out _))
            throw new InvalidOperationException("Protocol accepted invalid magic");
        packet = Encode(PacketType.Hello, 42);
        packet[4] = 33;
        if (TryDecode(packet, out _, out _))
            throw new InvalidOperationException("Protocol accepted version 33 packet");

        var identityPacket = EncodeIdentity(PacketType.Hello, 43, 0x12345678, "Дайвер");
        if (!TryDecodeIdentity(identityPacket, PacketType.Hello, out sequence, out var buildId, out var playerName) ||
            sequence != 43 || buildId != 0x12345678 || playerName != "Дайвер")
            throw new InvalidOperationException("Identity round-trip failed");
        identityPacket[^1] = 0xff;
        if (TryDecodeIdentity(identityPacket, PacketType.Hello, out _, out _, out _))
            throw new InvalidOperationException("Protocol accepted invalid UTF-8 identity");

        var rejectPacket = EncodeHandshakeReject(
            44, HandshakeRejectReason.IncompatibleBuild, 47);
        SetSessionId(rejectPacket, 0x123456789abcdef0);
        if (!TryDecodeHandshakeReject(
                rejectPacket, out sequence, out var rejectSessionId, out var rejectReason) ||
            sequence != 44 || rejectSessionId != 0x123456789abcdef0 ||
            rejectReason != HandshakeRejectReason.IncompatibleBuild)
            throw new InvalidOperationException("Handshake rejection round-trip failed");
        rejectPacket[HeaderSize] = 0;
        if (TryDecodeHandshakeReject(rejectPacket, out _, out _, out _))
            throw new InvalidOperationException("Protocol accepted an empty handshake rejection");

        var foreignHello = EncodeIdentity(PacketType.Hello, 45, 0x12345678, "Diver");
        SetSessionId(foreignHello, 11);
        foreignHello[4] = 47;
        if (!TryDecodeHelloIdentityAnyVersion(
                foreignHello, out var foreignVersion, out var foreignSessionId, out buildId) ||
            foreignVersion != 47 || foreignSessionId != 11 || buildId != 0x12345678)
            throw new InvalidOperationException("Foreign protocol hello decode failed");

        var scenePacket = EncodeSceneState(44, SceneId("A02_01_01"), 3);
        if (!TryDecodeSceneState(scenePacket, out sequence, out var sceneId, out var sceneEpoch) ||
            sequence != 44 || sceneId != SceneId("A02_01_01") || sceneEpoch != 3)
            throw new InvalidOperationException("Scene state round-trip failed");
        var sceneSeed = new SceneSeed(SceneId("A02_01_01"), 7, 1234567);
        var sceneSeedPacket = EncodeSceneSeed(45, sceneSeed);
        if (!TryDecodeSceneSeed(sceneSeedPacket, out sequence, out var actualSceneSeed) ||
            sequence != 45 || actualSceneSeed != sceneSeed)
            throw new InvalidOperationException("Scene seed round-trip failed");
        BinaryPrimitives.WriteUInt32LittleEndian(sceneSeedPacket.AsSpan(HeaderSize + 4), 0);
        if (TryDecodeSceneSeed(sceneSeedPacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted a scene seed without an epoch");
        BinaryPrimitives.WriteUInt32LittleEndian(sceneSeedPacket.AsSpan(HeaderSize + 4),
            sceneSeed.SceneEpoch);
        BinaryPrimitives.WriteInt32LittleEndian(sceneSeedPacket.AsSpan(HeaderSize + 8), 0);
        if (TryDecodeSceneSeed(sceneSeedPacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted a zero scene seed");
        var cargoState = new CargoState(SceneId("A02_01_01"), 9f, 13f, 0f, 7);
        var cargoPacket = EncodeCargoState(46, cargoState);
        if (!TryDecodeCargoState(cargoPacket, out sequence, out var actualCargoState) ||
            sequence != 46 || actualCargoState != cargoState)
            throw new InvalidOperationException("Cargo state round-trip failed");
        BinaryPrimitives.WriteUInt32LittleEndian(cargoPacket.AsSpan(HeaderSize + 4), 0);
        if (TryDecodeCargoState(cargoPacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted a cargo state without a scene epoch");
        BinaryPrimitives.WriteUInt32LittleEndian(cargoPacket.AsSpan(HeaderSize + 4), cargoState.SceneEpoch);
        WriteSingle(cargoPacket.AsSpan(HeaderSize + 8), 0f);
        if (TryDecodeCargoState(cargoPacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted invalid cargo state");

        var expectedDiverRuntime = new DiverRuntimeState(
            SceneId("A02_01_01"), 7, 3, DiverOwner.Client, false,
            DiverRuntimeFields.Health | DiverRuntimeFields.Oxygen |
            DiverRuntimeFields.Cargo | DiverRuntimeFields.Weapon | DiverRuntimeFields.Ammo,
            DiverRuntimeFlags.OxygenDepleting,
            80f, 100f, 45f, 120f, 17.5f, 2101, 8, 12);
        var diverRuntimePacket = EncodeDiverRuntimeState(47, expectedDiverRuntime);
        if (!TryDecodeDiverRuntimeState(
                diverRuntimePacket, out sequence, out var actualDiverRuntime) ||
            sequence != 47 || actualDiverRuntime != expectedDiverRuntime)
            throw new InvalidOperationException("Diver runtime state round-trip failed");
        BinaryPrimitives.WriteUInt16LittleEndian(
            diverRuntimePacket.AsSpan(HeaderSize + 14), 0x8000);
        if (TryDecodeDiverRuntimeState(diverRuntimePacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted invalid diver runtime field mask");
        diverRuntimePacket = EncodeDiverRuntimeState(47, expectedDiverRuntime);
        BinaryPrimitives.WriteUInt16LittleEndian(
            diverRuntimePacket.AsSpan(HeaderSize + 16), 0x8000);
        if (TryDecodeDiverRuntimeState(diverRuntimePacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted invalid diver runtime status mask");
        diverRuntimePacket = EncodeDiverRuntimeState(47, expectedDiverRuntime);
        WriteSingle(diverRuntimePacket.AsSpan(HeaderSize + 18), float.NaN);
        if (TryDecodeDiverRuntimeState(diverRuntimePacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted NaN diver runtime health");
        diverRuntimePacket = EncodeDiverRuntimeState(47, expectedDiverRuntime);
        BinaryPrimitives.WriteUInt16LittleEndian(
            diverRuntimePacket.AsSpan(HeaderSize + 14),
            (ushort)(expectedDiverRuntime.Fields & ~DiverRuntimeFields.Cargo));
        if (TryDecodeDiverRuntimeState(diverRuntimePacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted non-canonical absent diver field");
        diverRuntimePacket = EncodeDiverRuntimeState(47, expectedDiverRuntime);
        BinaryPrimitives.WriteUInt16LittleEndian(
            diverRuntimePacket.AsSpan(HeaderSize + 14),
            (ushort)(expectedDiverRuntime.Fields & ~DiverRuntimeFields.Oxygen));
        WriteSingle(diverRuntimePacket.AsSpan(HeaderSize + 26), 0f);
        WriteSingle(diverRuntimePacket.AsSpan(HeaderSize + 30), 0f);
        if (TryDecodeDiverRuntimeState(diverRuntimePacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted oxygen flag without oxygen state");
        diverRuntimePacket = EncodeDiverRuntimeState(47, expectedDiverRuntime);
        BinaryPrimitives.WriteUInt16LittleEndian(
            diverRuntimePacket.AsSpan(HeaderSize + 14),
            (ushort)(expectedDiverRuntime.Fields & ~DiverRuntimeFields.Cargo));
        BinaryPrimitives.WriteUInt16LittleEndian(
            diverRuntimePacket.AsSpan(HeaderSize + 16),
            (ushort)(expectedDiverRuntime.Flags | DiverRuntimeFlags.Overweight));
        WriteSingle(diverRuntimePacket.AsSpan(HeaderSize + 34), 0f);
        if (TryDecodeDiverRuntimeState(diverRuntimePacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted cargo flag without cargo state");
        diverRuntimePacket = EncodeDiverRuntimeState(47, expectedDiverRuntime);
        BinaryPrimitives.WriteInt32LittleEndian(
            diverRuntimePacket.AsSpan(HeaderSize + 46), expectedDiverRuntime.Ammo - 1);
        if (TryDecodeDiverRuntimeState(diverRuntimePacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted ammo above diver max ammo");
        diverRuntimePacket = EncodeDiverRuntimeState(47, expectedDiverRuntime);
        BinaryPrimitives.WriteUInt16LittleEndian(
            diverRuntimePacket.AsSpan(HeaderSize + 14),
            (ushort)(expectedDiverRuntime.Fields & ~DiverRuntimeFields.Ammo));
        BinaryPrimitives.WriteInt32LittleEndian(diverRuntimePacket.AsSpan(HeaderSize + 42), 0);
        if (TryDecodeDiverRuntimeState(diverRuntimePacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted max ammo without ammo state");

        var vitalState = expectedDiverRuntime with
        {
            Flags = DiverRuntimeFlags.None,
            Hp = 70f,
            MaxHp = 100f,
            Oxygen = 70f,
            MaxOxygen = 100f
        };
        var expectedVitalResult = new DiverVitalResult(
            5, 0x123456789abcdef0, DiverVitalCause.Damage,
            DiverVitalEdges.Damaged, 10f, vitalState);
        var vitalResultPacket = EncodeDiverVitalResult(48, expectedVitalResult);
        if (!TryDecodeDiverVitalResult(
                vitalResultPacket, out sequence, out var actualVitalResult) ||
            sequence != 48 || actualVitalResult != expectedVitalResult)
            throw new InvalidOperationException("Diver vital result round-trip failed");
        vitalResultPacket[HeaderSize + 12] = byte.MaxValue;
        if (TryDecodeDiverVitalResult(vitalResultPacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted invalid diver vital cause");
        vitalResultPacket = EncodeDiverVitalResult(48, expectedVitalResult);
        vitalResultPacket[HeaderSize + 13] = (byte)DiverVitalEdges.Healed;
        if (TryDecodeDiverVitalResult(vitalResultPacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted invalid diver vital edge");
        vitalResultPacket = EncodeDiverVitalResult(48, expectedVitalResult);
        vitalResultPacket[HeaderSize + 30] = (byte)DiverOwner.Host;
        if (TryDecodeDiverVitalResult(vitalResultPacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted host-owned diver vital result");
        vitalResultPacket = EncodeDiverVitalResult(48, expectedVitalResult);
        WriteSingle(vitalResultPacket.AsSpan(HeaderSize + 44), 69f);
        if (TryDecodeDiverVitalResult(vitalResultPacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted split HP and oxygen authority");
        vitalResultPacket = EncodeDiverVitalResult(48, expectedVitalResult);
        vitalResultPacket[HeaderSize + 31] = 1;
        if (TryDecodeDiverVitalResult(vitalResultPacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted nonzero dead diver vital state");

        var expectedVitalIntent = new DiverVitalIntent(
            expectedDiverRuntime.SceneId, expectedDiverRuntime.SceneEpoch,
            0x123456789abcdef0, DiverVitalIntentKind.OxygenCapsule, 5f);
        var vitalIntentPacket = EncodeDiverVitalIntent(49, expectedVitalIntent);
        if (!TryDecodeDiverVitalIntent(vitalIntentPacket, out sequence, out var actualVitalIntent) ||
            sequence != 49 || actualVitalIntent != expectedVitalIntent)
            throw new InvalidOperationException("Diver vital intent round-trip failed");
        vitalIntentPacket[HeaderSize + 16] = byte.MaxValue;
        if (TryDecodeDiverVitalIntent(vitalIntentPacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted invalid diver vital intent");

        var expectedWeaponIntent = new DiverWeaponIntent(
            expectedDiverRuntime.SceneId, expectedDiverRuntime.SceneEpoch,
            0x123456789abcdef0, DiverWeaponAction.Fire, expectedDiverRuntime.WeaponId);
        var weaponIntentPacket = EncodeDiverWeaponIntent(49, expectedWeaponIntent);
        if (!TryDecodeDiverWeaponIntent(
                weaponIntentPacket, out sequence, out var actualWeaponIntent) ||
            sequence != 49 || actualWeaponIntent != expectedWeaponIntent)
            throw new InvalidOperationException("Diver weapon intent round-trip failed");
        if (TryDecodeDiverWeaponIntent(
                weaponIntentPacket.AsSpan(0, weaponIntentPacket.Length - 1), out _, out _))
            throw new InvalidOperationException("Protocol accepted truncated diver weapon intent");
        weaponIntentPacket[HeaderSize + 16] = byte.MaxValue;
        if (TryDecodeDiverWeaponIntent(weaponIntentPacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted invalid diver weapon action");
        weaponIntentPacket = EncodeDiverWeaponIntent(49, expectedWeaponIntent);
        weaponIntentPacket[HeaderSize + 16] = (byte)DiverWeaponAction.Unequip;
        if (TryDecodeDiverWeaponIntent(weaponIntentPacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted weapon ID on unequip intent");

        var weaponState = expectedDiverRuntime with
        {
            Owner = DiverOwner.Client,
            Revision = 4,
            Ammo = 7
        };
        var expectedWeaponResult = new DiverWeaponResult(
            6, expectedWeaponIntent.RequestId, DiverWeaponAction.Fire, true,
            DiverWeaponRejectReason.None, 1, weaponState);
        var weaponResultPacket = EncodeDiverWeaponResult(50, expectedWeaponResult);
        if (!TryDecodeDiverWeaponResult(
                weaponResultPacket, out sequence, out var actualWeaponResult) ||
            sequence != 50 || actualWeaponResult != expectedWeaponResult)
            throw new InvalidOperationException("Diver weapon result round-trip failed");
        if (TryDecodeDiverWeaponResult(
                weaponResultPacket.AsSpan(0, weaponResultPacket.Length - 1), out _, out _))
            throw new InvalidOperationException("Protocol accepted truncated diver weapon result");
        weaponResultPacket[HeaderSize + 13] = 2;
        if (TryDecodeDiverWeaponResult(weaponResultPacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted invalid weapon acceptance flag");
        weaponResultPacket = EncodeDiverWeaponResult(50, expectedWeaponResult);
        weaponResultPacket[HeaderSize + 14] = (byte)DiverWeaponRejectReason.NoAmmo;
        if (TryDecodeDiverWeaponResult(weaponResultPacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted rejection reason on accepted weapon result");
        weaponResultPacket = EncodeDiverWeaponResult(50, expectedWeaponResult);
        weaponResultPacket[HeaderSize + 12] = (byte)DiverWeaponAction.Unequip;
        if (TryDecodeDiverWeaponResult(weaponResultPacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted equipped state after accepted unequip");
        weaponResultPacket = EncodeDiverWeaponResult(50, expectedWeaponResult);
        BinaryPrimitives.WriteInt32LittleEndian(
            weaponResultPacket.AsSpan(HeaderSize + 19 + 46), 0);
        if (TryDecodeDiverWeaponResult(weaponResultPacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted weapon result without max ammo");
        weaponResultPacket = EncodeDiverWeaponResult(50, expectedWeaponResult);
        weaponResultPacket[HeaderSize + 13] = 0;
        weaponResultPacket[HeaderSize + 14] = (byte)DiverWeaponRejectReason.NoAmmo;
        if (TryDecodeDiverWeaponResult(weaponResultPacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted applied rounds on rejected weapon result");

        var unequippedState = weaponState with
        {
            WeaponId = 0,
            Ammo = 0,
            MaxAmmo = 0
        };
        var unequipResult = new DiverWeaponResult(
            7, expectedWeaponIntent.RequestId + 1, DiverWeaponAction.Unequip, true,
            DiverWeaponRejectReason.None, 0, unequippedState);
        weaponResultPacket = EncodeDiverWeaponResult(51, unequipResult);
        if (!TryDecodeDiverWeaponResult(
                weaponResultPacket, out sequence, out actualWeaponResult) ||
            sequence != 51 || actualWeaponResult != unequipResult)
            throw new InvalidOperationException("Diver weapon unequip result round-trip failed");

        var expectedInteraction = new NpcInteraction(
            0x123456789abcdef0, SceneId("DR_Lobby"), 4, 0x10203040, 7,
            NpcInteractionAction.Granted, 101, 0, 0, NpcInteractionResult.Accepted);
        var interactionPacket = EncodeNpcInteraction(47, expectedInteraction);
        if (!TryDecodeNpcInteraction(
                interactionPacket, out sequence, out var actualInteraction) ||
            sequence != 47 || actualInteraction != expectedInteraction)
            throw new InvalidOperationException("NPC interaction round-trip failed");
        interactionPacket[HeaderSize + 24] = byte.MaxValue;
        if (TryDecodeNpcInteraction(interactionPacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted invalid NPC interaction action");
        var releasePacket = EncodeNpcInteraction(48, expectedInteraction with
        {
            Revision = 0,
            Action = NpcInteractionAction.Released
        });
        if (!TryDecodeNpcInteraction(releasePacket, out sequence, out var release) ||
            sequence != 48 || release.Action != NpcInteractionAction.Released)
            throw new InvalidOperationException("NPC interaction cancellation round-trip failed");

        var fishSceneId = SceneId("A02_01_01");
        var expectedFish = new[]
        {
            new FishSnapshot(fishSceneId, 7, 9, 17, 3, 2501, 1.25f, -2.5f, -0.1f, 183f, 2f, -1f, 42.5f, 5),
            new FishSnapshot(fishSceneId, 7, 9, 18, 4, 2502, 3f, 4f, 0f, 10f, 0f, 0f, 8f, 0)
        };
        var fishPacket = EncodeFishSnapshotBatch(45, fishSceneId, 7, 9, expectedFish, 0, expectedFish.Length);
        if (!TryDecodeFishSnapshotBatch(fishPacket, out sequence, out var actualFish) ||
            sequence != 45 || fishPacket.Length >= 113 || fishPacket.Length > 1200 ||
            actualFish.Length != 2 || actualFish[0].Id != expectedFish[0].Id ||
            actualFish[0].Revision != expectedFish[0].Revision || actualFish[0].FishDataTID != 0 ||
            MathF.Abs(actualFish[0].X - expectedFish[0].X) > 1f / 128f ||
            MathF.Abs(actualFish[0].Y - expectedFish[0].Y) > 1f / 128f ||
            MathF.Abs(actualFish[0].Z - expectedFish[0].Z) > 1f / 128f ||
            MathF.Abs(actualFish[0].VelocityX - expectedFish[0].VelocityX) > 1f / 32f ||
            MathF.Abs(actualFish[0].VelocityY - expectedFish[0].VelocityY) > 1f / 32f ||
            MathF.Abs(actualFish[0].Rotation - expectedFish[0].Rotation) > 360f / ushort.MaxValue ||
            actualFish[0].Hp != expectedFish[0].Hp || actualFish[0].Flags != expectedFish[0].Flags)
            throw new InvalidOperationException("Fish snapshot batch round-trip failed");
        var fallbackFish = new[]
        {
            expectedFish[0] with { X = 600f, VelocityY = 3000f, Rotation = -725.5f }
        };
        fishPacket = EncodeFishSnapshotBatch(45, fishSceneId, 7, 9, fallbackFish, 0, 1);
        if (!TryDecodeFishSnapshotBatch(fishPacket, out _, out actualFish) ||
            actualFish.Length != 1 || actualFish[0].X != fallbackFish[0].X ||
            actualFish[0].VelocityY != fallbackFish[0].VelocityY ||
            actualFish[0].Rotation != fallbackFish[0].Rotation)
            throw new InvalidOperationException("Fish snapshot full-precision fallback failed");
        var maximumFish = new FishSnapshot[MaxFishSnapshotsPerPacket];
        for (var index = 0; index < maximumFish.Length; index++)
            maximumFish[index] = fallbackFish[0] with { Id = index + 1 };
        fishPacket = EncodeFishSnapshotBatch(
            45, fishSceneId, 7, 9, maximumFish, 0, maximumFish.Length);
        if (fishPacket.Length > MaxDatagramSize ||
            !TryDecodeFishSnapshotBatch(fishPacket, out _, out actualFish) ||
            actualFish.Length != maximumFish.Length)
            throw new InvalidOperationException("Fish snapshot datagram budget failed");
        fishPacket = EncodeFishSnapshotBatch(45, fishSceneId, 7, 9, expectedFish, 0, expectedFish.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(fishPacket.AsSpan(HeaderSize + 4), 0);
        if (TryDecodeFishSnapshotBatch(fishPacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted fish snapshot without scene epoch");

        var expectedDamage = new FishDamageRequest(SceneId("A02_01_01"), 7, 17, 3, 23, 2, 4);
        var damagePacket = EncodeFishDamageRequest(46, expectedDamage);
        if (!TryDecodeFishDamageRequest(damagePacket, out sequence, out var actualDamage) ||
            sequence != 46 || actualDamage != expectedDamage)
            throw new InvalidOperationException("Fish damage request round-trip failed");
        BinaryPrimitives.WriteInt32LittleEndian(damagePacket.AsSpan(HeaderSize + 16), -1);
        if (TryDecodeFishDamageRequest(damagePacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted invalid fish damage");
        damagePacket = EncodeFishDamageRequest(46, expectedDamage);
        BinaryPrimitives.WriteUInt32LittleEndian(damagePacket.AsSpan(HeaderSize + 4), 0);
        if (TryDecodeFishDamageRequest(damagePacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted fish damage without scene epoch");

        var lifecycle = new FishLifecycle(
            fishSceneId, 7, 17, 4, FishLifecycleKind.Spawn, 2501, FishPhase.Alive, 42.5f);
        var lifecyclePacket = EncodeFishLifecycle(47, lifecycle);
        if (!TryDecodeFishLifecycle(lifecyclePacket, out sequence, out var actualLifecycle) ||
            sequence != 47 || lifecyclePacket.Length != 44 || actualLifecycle != lifecycle)
            throw new InvalidOperationException("Fish lifecycle round-trip failed");
        lifecyclePacket[HeaderSize + 16] = byte.MaxValue;
        if (TryDecodeFishLifecycle(lifecyclePacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted invalid fish lifecycle kind");
        lifecyclePacket = EncodeFishLifecycle(47, lifecycle);
        WriteSingle(lifecyclePacket.AsSpan(HeaderSize + 22), float.NaN);
        if (TryDecodeFishLifecycle(lifecyclePacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted invalid lifecycle HP");
        var interest = lifecycle with { Kind = FishLifecycleKind.InterestEnter, FishDataTID = 0 };
        lifecyclePacket = EncodeFishLifecycle(47, interest);
        if (!TryDecodeFishLifecycle(lifecyclePacket, out _, out actualLifecycle) ||
            actualLifecycle != interest)
            throw new InvalidOperationException("Fish interest lifecycle round-trip failed");

        var hookPose = new FishHookPose(
            fishSceneId, 7, 17, 0x0102030405060708, 19,
            1.25f, -2.5f, -0.1f, 183f, 2f, -1f);
        var hookPosePacket = EncodeFishHookPose(48, hookPose);
        if (!TryDecodeFishHookPose(hookPosePacket, out sequence, out var actualHookPose) ||
            sequence != 48 || actualHookPose.SceneId != hookPose.SceneId ||
            actualHookPose.SceneEpoch != hookPose.SceneEpoch ||
            actualHookPose.FishId != hookPose.FishId || actualHookPose.LeaseId != hookPose.LeaseId ||
            actualHookPose.Tick != hookPose.Tick ||
            MathF.Abs(actualHookPose.X - hookPose.X) > 1f / 64f ||
            MathF.Abs(actualHookPose.Y - hookPose.Y) > 1f / 64f ||
            MathF.Abs(actualHookPose.Z - hookPose.Z) > 1f / 64f ||
            MathF.Abs(actualHookPose.VelocityX - hookPose.VelocityX) > 1f / 16f ||
            MathF.Abs(actualHookPose.VelocityY - hookPose.VelocityY) > 1f / 16f)
            throw new InvalidOperationException("Fish hook pose round-trip failed");
        hookPosePacket = EncodeFishHookPose(48, hookPose with { X = 600f });
        BinaryPrimitives.WriteInt32LittleEndian(
            hookPosePacket.AsSpan(FishHookPoseFixedSize + 20), unchecked((int)0x7fc00000));
        if (TryDecodeFishHookPose(hookPosePacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted invalid fish hook pose");
        hookPosePacket = EncodeFishHookPose(48, hookPose);
        BinaryPrimitives.WriteUInt32LittleEndian(hookPosePacket.AsSpan(HeaderSize + 4), 0);
        if (TryDecodeFishHookPose(hookPosePacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted fish hook pose without scene epoch");
        hookPosePacket = EncodeFishHookPose(48, hookPose);
        hookPosePacket[5] = (byte)PacketType.FishActionAck;
        if (TryDecodeFishHookPose(hookPosePacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted wrong fish hook pose packet type");

        var actionRequest = new FishActionRequest(
            0x0102030405060708, fishSceneId, 7, 17, 4, 0,
            FishAction.Damage, 23, 2, 4);
        var actionRequestPacket = EncodeFishActionRequest(48, actionRequest);
        if (!TryDecodeFishActionRequest(actionRequestPacket, out sequence, out var actualActionRequest) ||
            sequence != 48 || actionRequestPacket.Length != 63 || actualActionRequest != actionRequest)
            throw new InvalidOperationException("Fish action request round-trip failed");
        actionRequestPacket[HeaderSize + 32] = (byte)FishAction.Release;
        if (TryDecodeFishActionRequest(actionRequestPacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted payload for payload-free fish action");
        var releaseRequest = actionRequest with
        {
            LeaseId = actionRequest.RequestId,
            Action = FishAction.Release,
            Damage = 0,
            Element = 0,
            AttackType = 0
        };
        var releaseRequestPacket = EncodeFishActionRequest(48, releaseRequest);
        if (!TryDecodeFishActionRequest(
                releaseRequestPacket, out _, out var actualReleaseRequest) ||
            actualReleaseRequest != releaseRequest)
            throw new InvalidOperationException("Fish lease identity round-trip failed");
        actionRequestPacket = EncodeFishActionRequest(48, actionRequest);
        BinaryPrimitives.WriteUInt64LittleEndian(actionRequestPacket.AsSpan(HeaderSize), 0);
        if (TryDecodeFishActionRequest(actionRequestPacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted zero fish action request ID");

        var actionAck = new FishActionAck(
            actionRequest.RequestId, fishSceneId, 7, 17, 5, FishAction.Damage,
            FishActionResult.Accepted, FishActionRejectReason.None, 19.5f, FishPhase.Alive);
        var actionAckPacket = EncodeFishActionAck(49, actionAck);
        if (!TryDecodeFishActionAck(actionAckPacket, out sequence, out var actualActionAck) ||
            sequence != 49 || actionAckPacket.Length != 50 || actualActionAck != actionAck)
            throw new InvalidOperationException("Fish action acknowledgement round-trip failed");
        actionAckPacket[HeaderSize + 26] = (byte)FishActionRejectReason.StaleRevision;
        if (TryDecodeFishActionAck(actionAckPacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted reject reason for accepted fish action");
        actionAckPacket = EncodeFishActionAck(49, actionAck);
        WriteSingle(actionAckPacket.AsSpan(HeaderSize + 27), float.NaN);
        if (TryDecodeFishActionAck(actionAckPacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted invalid authoritative fish HP");

        var lootGrant = new FishLootGrant(
            0x1112131415161718, fishSceneId, 7, actionRequest.RequestId, 17, 5,
            FishAction.Capture, 1, 2, 1_011_004, 2, 3, 2, 12.5f);
        var lootGrantPacket = EncodeFishLootGrant(50, lootGrant);
        if (!TryDecodeFishLootGrant(lootGrantPacket, out sequence, out var actualLootGrant) ||
            sequence != 50 || actualLootGrant != lootGrant)
            throw new InvalidOperationException("Fish loot grant round-trip failed");
        BinaryPrimitives.WriteUInt16LittleEndian(lootGrantPacket.AsSpan(HeaderSize + 33), 2);
        if (TryDecodeFishLootGrant(lootGrantPacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted invalid fish loot grant index");
        var lootComplete = new FishLootComplete(
            lootGrant.TransactionId, fishSceneId, 7, actionRequest.RequestId,
            17, 5, FishAction.Capture);
        var lootCompletePacket = EncodeFishLootComplete(51, lootComplete);
        if (!TryDecodeFishLootComplete(
                lootCompletePacket, out sequence, out var actualLootComplete) ||
            sequence != 51 || actualLootComplete != lootComplete)
            throw new InvalidOperationException("Fish loot completion round-trip failed");
        lootCompletePacket[HeaderSize + 32] = byte.MaxValue;
        if (TryDecodeFishLootComplete(lootCompletePacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted invalid fish loot completion");

        var expectedBossDamage = new BossDamageRequest(
            fishSceneId, 7, 9, 0x11223344, 0x11223345, 37, 2, 4, 2.5f, -4f, 0f);
        var bossDamagePacket = EncodeBossDamageRequest(46, expectedBossDamage);
        if (!TryDecodeBossDamageRequest(bossDamagePacket, out sequence, out var actualBossDamage) ||
            sequence != 46 || actualBossDamage != expectedBossDamage)
            throw new InvalidOperationException("Boss damage request round-trip failed");
        BinaryPrimitives.WriteUInt32LittleEndian(bossDamagePacket.AsSpan(HeaderSize + 12), 0);
        if (TryDecodeBossDamageRequest(bossDamagePacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted an invalid boss ID");
        bossDamagePacket = EncodeBossDamageRequest(46, expectedBossDamage);
        BinaryPrimitives.WriteUInt32LittleEndian(bossDamagePacket.AsSpan(HeaderSize + 4), 0);
        if (TryDecodeBossDamageRequest(bossDamagePacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted boss damage without scene epoch");

        var expectedBossState = new BossState(
            fishSceneId, 7, 9, 0x11223344, 2801, 740, 1000, 2.5f, -4f, 0f,
            0x12345678, 0.25f, 2, 0);
        var bossStatePacket = EncodeBossState(46, expectedBossState);
        if (!TryDecodeBossState(bossStatePacket, out sequence, out var actualBossState) ||
            sequence != 46 || actualBossState != expectedBossState)
            throw new InvalidOperationException("Boss state round-trip failed");
        BinaryPrimitives.WriteInt32LittleEndian(bossStatePacket.AsSpan(HeaderSize + 20), 1001);
        if (TryDecodeBossState(bossStatePacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted boss HP above maximum");
        bossStatePacket = EncodeBossState(46, expectedBossState);
        BinaryPrimitives.WriteInt32LittleEndian(
            bossStatePacket.AsSpan(HeaderSize + 44), unchecked((int)0x7fc00000));
        if (TryDecodeBossState(bossStatePacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted invalid boss animation time");
        bossStatePacket = EncodeBossState(46, expectedBossState);
        BinaryPrimitives.WriteUInt32LittleEndian(bossStatePacket.AsSpan(HeaderSize + 4), 0);
        if (TryDecodeBossState(bossStatePacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted boss state without scene epoch");

        var expectedManagerEvent = new ManagerEvent(
            3, 0, 1234, 3, 4, 1, 0x12345678);
        var managerEventPacket = EncodeManagerEvent(47, expectedManagerEvent);
        if (!TryDecodeManagerEvent(managerEventPacket, out sequence, out var managerEvent) ||
            sequence != 47 || managerEvent != expectedManagerEvent)
            throw new InvalidOperationException("Manager event round-trip failed");
        managerEventPacket[HeaderSize + 16] = 0;
        if (TryDecodeManagerEvent(managerEventPacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted invalid manager domain");
        managerEventPacket = EncodeManagerEvent(47, expectedManagerEvent);
        managerEventPacket[HeaderSize + 17] = 32;
        if (TryDecodeManagerEvent(managerEventPacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted invalid manager action");
        managerEventPacket = EncodeManagerEvent(47,
            new ManagerEvent(3, fishSceneId, 1234, 15, 27, 1,
                unchecked((int)0x7fc00000), SceneEpoch: 7));
        if (TryDecodeManagerEvent(managerEventPacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted invalid race time");
        var sessionEvents = new[]
        {
            new ManagerEvent(4, 0, 1235, 16, 32, 12345, 0),
            new ManagerEvent(5, 0, 1236, 16, 33, 12345, 67),
            new ManagerEvent(6, 0, 1237, 16, 34, 12345, 1),
            new ManagerEvent(7, 0, 1238, 3, 35, 23456, 0),
            new ManagerEvent(8, 0, 1239, 3, 36, 23456, 3),
            new ManagerEvent(9, 0, 1240, 3, 37, 23456, 1),
            new ManagerEvent(10, 0, 1241, 3, 38, 71, 0),
            new ManagerEvent(11, 0, 1242, 3, 39, 71, 0)
        };
        for (var index = 0; index < sessionEvents.Length; index++)
        {
            managerEventPacket = EncodeManagerEvent((uint)(48 + index), sessionEvents[index]);
            if (!TryDecodeManagerEvent(managerEventPacket, out sequence, out managerEvent) ||
                sequence != 48 + index || managerEvent != sessionEvents[index])
                throw new InvalidOperationException("Manager session event round-trip failed");
        }
        managerEventPacket[HeaderSize + 4] = 1;
        if (TryDecodeManagerEvent(managerEventPacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted scene-bound dialogue state");
        managerEventPacket = EncodeManagerEvent(60,
            new ManagerEvent(10, 0, 1241, 3, 37, 23456, 1));
        BinaryPrimitives.WriteInt32LittleEndian(managerEventPacket.AsSpan(HeaderSize + 18), 0);
        if (TryDecodeManagerEvent(managerEventPacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted identity-free dialogue finish");
        var progressionEvents = new[]
        {
            new ManagerEvent(12, 0, 1243, 17, 40, 100, 1),
            new ManagerEvent(13, 0, 1244, 17, 69, 1, 250),
            new ManagerEvent(0, 0, 1245, 17, 69, 1, -100),
            new ManagerEvent(14, 0, 1246, 17, 70, 10017, 3)
        };
        foreach (var progressionEvent in progressionEvents)
        {
            managerEventPacket = EncodeManagerEvent(61, progressionEvent);
            if (!TryDecodeManagerEvent(managerEventPacket, out _, out managerEvent) ||
                managerEvent != progressionEvent)
                throw new InvalidOperationException("Progression manager event round-trip failed");
        }
        var timeEvents = new[]
        {
            new ManagerEvent(15, 0, 1247, 18, 78, 3, BitConverter.SingleToInt32Bits(0.25f)),
            new ManagerEvent(16, 0, 1248, 18, 79, 0, 0),
            new ManagerEvent(17, 0, 1249, 18, 80, 0, 0)
        };
        foreach (var timeEvent in timeEvents)
        {
            managerEventPacket = EncodeManagerEvent(62, timeEvent);
            if (!TryDecodeManagerEvent(managerEventPacket, out _, out managerEvent) ||
                managerEvent != timeEvent)
                throw new InvalidOperationException("Time manager event round-trip failed");
        }
        var sushiActionEvents = new[]
        {
            new ManagerEvent(0, fishSceneId, 1247, 8, 71, 0, 0, SceneEpoch: 7),
            new ManagerEvent(15, fishSceneId, 1248, 8, 72, 1011001, (1 << 16) | 7,
                SceneEpoch: 7),
            new ManagerEvent(0, fishSceneId, 1249, 8, 73, 0, (1 << 16) | 7, SceneEpoch: 7),
            new ManagerEvent(16, fishSceneId, 1250, 8, 74, 1, (1 << 16) | 7, SceneEpoch: 7),
            new ManagerEvent(0, fishSceneId, 1251, 10, 75, 100, 7, SceneEpoch: 7),
            new ManagerEvent(16, fishSceneId, 1252, 10, 76, 100, 7, SceneEpoch: 7),
            new ManagerEvent(0, fishSceneId, 1253, 9, 77, 0, 5, SceneEpoch: 7)
        };
        foreach (var sushiActionEvent in sushiActionEvents)
        {
            managerEventPacket = EncodeManagerEvent(62, sushiActionEvent);
            if (!TryDecodeManagerEvent(managerEventPacket, out _, out managerEvent) ||
                managerEvent != sushiActionEvent)
                throw new InvalidOperationException("Sushi action event round-trip failed");
        }
        BinaryPrimitives.WriteUInt32LittleEndian(managerEventPacket.AsSpan(HeaderSize + 8), 0);
        if (TryDecodeManagerEvent(managerEventPacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted manager event without scene epoch");
        managerEventPacket = EncodeManagerEvent(47, expectedManagerEvent);
        BinaryPrimitives.WriteUInt32LittleEndian(managerEventPacket.AsSpan(HeaderSize + 8), 7);
        if (TryDecodeManagerEvent(managerEventPacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted global manager event with scene epoch");

        var scenarioBundle = "z";
        var scenarioKey = unchecked((int)SceneId(scenarioBundle));
        var scenarioInvocation = new ManagerInvocationDescriptor(
            ManagerInvocationKind.Scenario, scenarioBundle,
            new string[] { "alpha", null, string.Empty }, true, false, true,
            true, false, 0f, 0f, 0f);
        var describedScenario = new ManagerEvent(
            12, 0, 1243, 16, 32, scenarioKey, 0, scenarioInvocation);
        managerEventPacket = EncodeManagerEvent(61, describedScenario);
        if (scenarioKey >= 0 || managerEventPacket.Length > MaxDatagramSize ||
            !TryDecodeManagerEvent(managerEventPacket, out sequence, out managerEvent) ||
            sequence != 61 || managerEvent.Invocation is not { } decodedScenario ||
            decodedScenario.Kind != ManagerInvocationKind.Scenario ||
            decodedScenario.BundleId != scenarioBundle || decodedScenario.Arguments == null ||
            decodedScenario.Arguments.Length != 3 || decodedScenario.Arguments[0] != "alpha" ||
            decodedScenario.Arguments[1] != null || decodedScenario.Arguments[2] != string.Empty ||
            !decodedScenario.UseButton || decodedScenario.ShowCurtain ||
            !decodedScenario.IgnorePlaying)
            throw new InvalidOperationException("Scenario invocation round-trip failed");
        var trailingManagerEventPacket = new byte[managerEventPacket.Length + 1];
        managerEventPacket.CopyTo(trailingManagerEventPacket, 0);
        if (TryDecodeManagerEvent(trailingManagerEventPacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted trailing manager invocation data");
        managerEventPacket[HeaderSize + 17] = 35;
        if (TryDecodeManagerEvent(managerEventPacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted mismatched manager invocation action");
        managerEventPacket = EncodeManagerEvent(61, describedScenario);
        BinaryPrimitives.WriteInt32LittleEndian(
            managerEventPacket.AsSpan(HeaderSize + 18), scenarioKey + 1);
        if (TryDecodeManagerEvent(managerEventPacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted mismatched invocation content hash");
        managerEventPacket = EncodeManagerEvent(61, describedScenario);
        managerEventPacket[ManagerEventSize + 4] = 0xff;
        if (TryDecodeManagerEvent(managerEventPacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted invalid invocation UTF-8");
        var oversizedInvocationRejected = false;
        try
        {
            var oversizedBundle = new string('x', 513);
            EncodeManagerEvent(61, describedScenario with
            {
                Value = unchecked((int)SceneId(oversizedBundle)),
                Invocation = scenarioInvocation with { BundleId = oversizedBundle }
            });
        }
        catch (ArgumentOutOfRangeException)
        {
            oversizedInvocationRejected = true;
        }
        if (!oversizedInvocationRejected)
            throw new InvalidOperationException("Protocol accepted oversized manager invocation");

        var dialogueBundle = "dialogue/replay";
        var dialogueKey = unchecked((int)SceneId(dialogueBundle));
        var dialogueInvocations = new[]
        {
            new ManagerInvocationDescriptor(
                ManagerInvocationKind.DialogueNormal, dialogueBundle, null,
                true, false, false, true, false, 0f, 0f, 0f),
            new ManagerInvocationDescriptor(
                ManagerInvocationKind.DialogueArguments, dialogueBundle, null,
                false, true, false, true, false, 0f, 0f, 0f),
            new ManagerInvocationDescriptor(
                ManagerInvocationKind.DialogueArguments, dialogueBundle, Array.Empty<string>(),
                false, false, false, true, false, 0f, 0f, 0f),
            new ManagerInvocationDescriptor(
                ManagerInvocationKind.DialogueSmall, dialogueBundle, null,
                true, true, false, true, false, 0f, 0f, 0f)
        };
        for (var index = 0; index < dialogueInvocations.Length; index++)
        {
            var describedDialogue = new ManagerEvent(
                (uint)(13 + index), 0, (uint)(1244 + index), 3, 35,
                dialogueKey, 0, dialogueInvocations[index]);
            managerEventPacket = EncodeManagerEvent((uint)(62 + index), describedDialogue);
            if (dialogueKey >= 0 ||
                !TryDecodeManagerEvent(managerEventPacket, out _, out managerEvent) ||
                managerEvent.Invocation is not { } decodedDialogue ||
                decodedDialogue.Kind != dialogueInvocations[index].Kind ||
                (index == 1 && decodedDialogue.Arguments != null) ||
                (index == 2 && (decodedDialogue.Arguments == null ||
                    decodedDialogue.Arguments.Length != 0)))
                throw new InvalidOperationException("Dialogue invocation round-trip failed");
        }

        var timelineInvocation = new ManagerInvocationDescriptor(
            ManagerInvocationKind.TimelineByTid, null, null,
            false, false, false, false, true, 1.25f, -2.5f, 3.75f);
        managerEventPacket = EncodeManagerEvent(66,
            new ManagerEvent(17, fishSceneId, 1248, 13, 24, 71,
                2, timelineInvocation, 7));
        if (!TryDecodeManagerEvent(managerEventPacket, out _, out managerEvent) ||
            managerEvent.Invocation is not { } decodedTimeline ||
            decodedTimeline.Kind != ManagerInvocationKind.TimelineByTid ||
            decodedTimeline.ApplyOffset || !decodedTimeline.HasCustomPosition ||
            decodedTimeline.CustomX != 1.25f || decodedTimeline.CustomY != -2.5f ||
            decodedTimeline.CustomZ != 3.75f)
            throw new InvalidOperationException("Timeline invocation round-trip failed");
        WriteSingle(managerEventPacket.AsSpan(managerEventPacket.Length - 4), float.NaN);
        if (TryDecodeManagerEvent(managerEventPacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted non-finite timeline position");

        var expectedSushiResult = new SushiResultState(5, 1200, 300, 80, 21, 19, 4.5f);
        var sushiResultPacket = EncodeSushiResultState(48, expectedSushiResult);
        if (!TryDecodeSushiResultState(sushiResultPacket, out sequence, out var sushiResult) ||
            sequence != 48 || sushiResult != expectedSushiResult)
            throw new InvalidOperationException("Sushi result round-trip failed");
        WriteSingle(sushiResultPacket.AsSpan(HeaderSize + 24), 6f);
        if (TryDecodeSushiResultState(sushiResultPacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted invalid sushi rating");

        var expectedFishPickup = new FishPickupRequest(SceneId("A02_01_01"), 7, 17, 4);
        var fishPickupPacket = EncodeFishPickupRequest(47, expectedFishPickup);
        if (!TryDecodeFishPickupRequest(fishPickupPacket, out sequence, out var actualFishPickup) ||
            sequence != 47 || actualFishPickup != expectedFishPickup)
            throw new InvalidOperationException("Fish pickup request round-trip failed");
        BinaryPrimitives.WriteUInt32LittleEndian(fishPickupPacket.AsSpan(HeaderSize + 4), 0);
        if (TryDecodeFishPickupRequest(fishPickupPacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted fish pickup without scene epoch");

        var expectedFishPickupResult = new FishPickupResult(SceneId("A02_01_01"), 7, 17, 5, true);
        var fishPickupResultPacket = EncodeFishPickupResult(48, expectedFishPickupResult);
        if (!TryDecodeFishPickupResult(
                fishPickupResultPacket, out sequence, out var actualFishPickupResult) ||
            sequence != 48 || actualFishPickupResult != expectedFishPickupResult)
            throw new InvalidOperationException("Fish pickup result round-trip failed");
        fishPickupResultPacket[HeaderSize + 16] = 2;
        if (TryDecodeFishPickupResult(fishPickupResultPacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted invalid fish pickup result");

        var expectedFishRemoved = new FishRemoved(SceneId("A02_01_01"), 7, 17, 5);
        var fishRemovedPacket = EncodeFishRemoved(48, expectedFishRemoved);
        if (!TryDecodeFishRemoved(fishRemovedPacket, out sequence, out var actualFishRemoved) ||
            sequence != 48 || actualFishRemoved != expectedFishRemoved)
            throw new InvalidOperationException("Fish removal round-trip failed");
        BinaryPrimitives.WriteUInt32LittleEndian(fishRemovedPacket.AsSpan(HeaderSize + 12), 0);
        if (TryDecodeFishRemoved(fishRemovedPacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted fish removal without revision");

        var expectedFishManifest = new FishManifest(
            SceneId("A02_01_01"), 7, 3, 17, 4, "A02/FishAllocator/3", 2501,
            1.25f, -2.5f, -0.1f, 183f, 42.5f, 13);
        var fishManifestPacket = EncodeFishManifest(49, expectedFishManifest);
        if (!TryDecodeFishManifest(fishManifestPacket, out sequence, out var actualFishManifest) ||
            sequence != 49 || actualFishManifest != expectedFishManifest)
            throw new InvalidOperationException("Fish manifest round-trip failed");
        fishManifestPacket[HeaderSize + 45] = 0;
        if (TryDecodeFishManifest(fishManifestPacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted an empty fish allocator UID");

        var expectedManifestState = new FishManifestState(SceneId("A02_01_01"), 7, 3, 17);
        var manifestStatePacket = EncodeFishManifestState(50, expectedManifestState);
        if (!TryDecodeFishManifestState(manifestStatePacket, out sequence, out var actualManifestState) ||
            sequence != 50 || actualManifestState != expectedManifestState)
            throw new InvalidOperationException("Fish manifest state round-trip failed");
        BinaryPrimitives.WriteUInt32LittleEndian(manifestStatePacket.AsSpan(HeaderSize + 4), 0);
        if (TryDecodeFishManifestState(manifestStatePacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted fish manifest state without scene epoch");

        var expectedPickup = new PickupRemoved(SceneId("A02_01_01"), 7, 0xCAFEBABE, 1001);
        var pickupPacket = EncodePickupRemoved(50, expectedPickup);
        if (!TryDecodePickupRemoved(pickupPacket, out sequence, out var actualPickup) ||
            sequence != 50 || actualPickup != expectedPickup)
            throw new InvalidOperationException("Pickup removal round-trip failed");
        var expectedPickupRequest = new PickupRequest(
            0x0102030405060708, SceneId("A02_01_01"), 7, 0xCAFEBABE, 1001, 3,
            12.5f, -4.25f);
        var pickupRequest = EncodePickupRequest(51, expectedPickupRequest);
        if (!TryDecodePickupRequest(pickupRequest, out sequence, out var actualPickupRequest) ||
            sequence != 51 || actualPickupRequest != expectedPickupRequest)
            throw new InvalidOperationException("Pickup request round-trip failed");
        BinaryPrimitives.WriteUInt64LittleEndian(pickupRequest.AsSpan(HeaderSize), 0);
        if (TryDecodePickupRequest(pickupRequest, out _, out _))
            throw new InvalidOperationException("Protocol accepted pickup request without request ID");
        pickupRequest = EncodePickupRequest(51, expectedPickupRequest);
        BinaryPrimitives.WriteUInt32LittleEndian(pickupRequest.AsSpan(HeaderSize + 24), 0);
        if (TryDecodePickupRequest(pickupRequest, out _, out _))
            throw new InvalidOperationException("Protocol accepted pickup request without revision");
        pickupRequest = EncodePickupRequest(51, expectedPickupRequest);
        BinaryPrimitives.WriteUInt32LittleEndian(pickupRequest.AsSpan(HeaderSize + 16), 0);
        if (TryDecodePickupRequest(pickupRequest, out _, out _))
            throw new InvalidOperationException("Protocol accepted pickup request without world ID");
        pickupRequest = EncodePickupRequest(51, expectedPickupRequest);
        BinaryPrimitives.WriteInt32LittleEndian(pickupRequest.AsSpan(HeaderSize + 20), 0);
        if (TryDecodePickupRequest(pickupRequest, out _, out _))
            throw new InvalidOperationException("Protocol accepted pickup request without item ID");
        pickupRequest = EncodePickupRequest(51, expectedPickupRequest);
        WriteSingle(pickupRequest.AsSpan(HeaderSize + 28), float.NaN);
        if (TryDecodePickupRequest(pickupRequest, out _, out _))
            throw new InvalidOperationException("Protocol accepted non-finite pickup position");

        var acceptedPickup = new PickupResult(
            expectedPickupRequest.RequestId, expectedPickupRequest.SceneId,
            expectedPickupRequest.SceneEpoch, expectedPickupRequest.WorldId,
            expectedPickupRequest.ItemId, 4, true, PickupRejectReason.None);
        var pickupResult = EncodePickupResult(52, acceptedPickup);
        if (!TryDecodePickupResult(pickupResult, out sequence, out var actualPickupResult) ||
            sequence != 52 || actualPickupResult != acceptedPickup)
            throw new InvalidOperationException("Accepted pickup result round-trip failed");
        BinaryPrimitives.WriteUInt32LittleEndian(pickupResult.AsSpan(HeaderSize + 24), 0);
        if (TryDecodePickupResult(pickupResult, out _, out _))
            throw new InvalidOperationException("Protocol accepted pickup result without revision");

        var rejectedPickup = acceptedPickup with
        {
            Accepted = false,
            RejectReason = PickupRejectReason.OutOfRange
        };
        pickupResult = EncodePickupResult(53, rejectedPickup);
        if (!TryDecodePickupResult(pickupResult, out sequence, out actualPickupResult) ||
            sequence != 53 || actualPickupResult != rejectedPickup)
            throw new InvalidOperationException("Rejected pickup result round-trip failed");
        pickupResult[HeaderSize + 29] = (byte)PickupRejectReason.None;
        if (TryDecodePickupResult(pickupResult, out _, out _))
            throw new InvalidOperationException("Protocol accepted pickup rejection without reason");
        BinaryPrimitives.WriteUInt32LittleEndian(pickupPacket.AsSpan(HeaderSize + 4), 0);
        if (TryDecodePickupRemoved(pickupPacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted pickup without scene epoch");

        var expectedTransition = new SceneTransitionCommand(
            "A02_02_01", 3, 0x8155, SceneId("A02_02_01"), 8, 4567);
        var transitionPacket = EncodeSceneTransition(51, expectedTransition);
        if (!TryDecodeSceneTransition(transitionPacket, out sequence, out var actualTransition) ||
            sequence != 51 || actualTransition != expectedTransition)
            throw new InvalidOperationException("Scene transition round-trip failed");
        BinaryPrimitives.WriteUInt32LittleEndian(transitionPacket.AsSpan(HeaderSize + 10), 0);
        if (TryDecodeSceneTransition(transitionPacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted a scene transition without an epoch");

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

        var saveChunk = new SaveSnapshotChunk(
            0x0102030405060708, 0x11223344, 3, 0, 1, new byte[] { 1, 2, 3 });
        var saveChunkPacket = EncodeSaveSnapshotChunk(61, saveChunk);
        if (!TryDecodeSaveSnapshotChunk(saveChunkPacket, out sequence, out var actualSaveChunk) ||
            sequence != 61 || actualSaveChunk.TransferId != saveChunk.TransferId ||
            actualSaveChunk.Fingerprint != saveChunk.Fingerprint ||
            actualSaveChunk.TotalBytes != saveChunk.TotalBytes ||
            actualSaveChunk.ChunkIndex != saveChunk.ChunkIndex ||
            actualSaveChunk.ChunkCount != saveChunk.ChunkCount ||
            actualSaveChunk.Data.Length != saveChunk.Data.Length ||
            actualSaveChunk.Data[2] != saveChunk.Data[2])
            throw new InvalidOperationException("Save snapshot chunk round-trip failed");
        saveChunkPacket[HeaderSize + 16] = 1;
        if (TryDecodeSaveSnapshotChunk(saveChunkPacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted invalid save snapshot chunk index");
        var saveAckPacket = EncodeSaveSnapshotAck(
            62, new SaveSnapshotAck(saveChunk.TransferId, saveChunk.Fingerprint, true));
        if (!TryDecodeSaveSnapshotAck(saveAckPacket, out sequence, out var saveAck) ||
            sequence != 62 || saveAck.TransferId != saveChunk.TransferId ||
            saveAck.Fingerprint != saveChunk.Fingerprint || !saveAck.Loaded)
            throw new InvalidOperationException("Save snapshot ack round-trip failed");
        saveAckPacket[HeaderSize + 12] = 2;
        if (TryDecodeSaveSnapshotAck(saveAckPacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted invalid save snapshot ack flag");

        var diveReadyPacket = EncodeDiveReady(61, new DiveReady(4, true));
        if (!TryDecodeDiveReady(diveReadyPacket, out sequence, out var diveReady) ||
            sequence != 61 || diveReady != new DiveReady(4, true))
            throw new InvalidOperationException("Dive ready round-trip failed");
        diveReadyPacket[HeaderSize + 4] = 2;
        if (TryDecodeDiveReady(diveReadyPacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted invalid dive ready state");

        var diveStatePacket = EncodeDiveState(
            62, new DiveState(5, true, false, SceneId("A02_01_01"), 9, 3456));
        if (!TryDecodeDiveState(diveStatePacket, out sequence, out var diveState) ||
            sequence != 62 || diveState != new DiveState(
                5, true, false, SceneId("A02_01_01"), 9, 3456))
            throw new InvalidOperationException("Dive state round-trip failed");
        diveStatePacket[HeaderSize + 5] = 2;
        if (TryDecodeDiveState(diveStatePacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted invalid dive state");
        diveStatePacket[HeaderSize + 5] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(diveStatePacket.AsSpan(HeaderSize + 10), 0);
        if (TryDecodeDiveState(diveStatePacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted a dive state without a scene epoch");

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
            66, new TravelReady(
                0x102, 5, true, false,
                new TravelRoute("DR_Jungle_RPG_Forest", 1, 504, 2)));
        if (!TryDecodeTravelReady(travelReadyPacket, out sequence, out var travelReady) ||
            sequence != 66 || travelReady != new TravelReady(
                0x102, 5, true, false,
                new TravelRoute("DR_Jungle_RPG_Forest", 1, 504, 2)))
            throw new InvalidOperationException("Travel ready round-trip failed");
        travelReadyPacket[HeaderSize + 9] = 2;
        if (TryDecodeTravelReady(travelReadyPacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted invalid travel readiness");

        var travelStatePacket = EncodeTravelState(
            67, new TravelState(0x101, 6, true, false, true, true));
        if (!TryDecodeTravelState(travelStatePacket, out sequence, out var travelState) ||
            sequence != 67 || travelState != new TravelState(0x101, 6, true, false, true, true))
            throw new InvalidOperationException("Travel state round-trip failed");

        var lootRequestPacket = EncodeDiveLootRequest(
            68, new DiveLootRequest(0x6000000000000042, 101, 2, 1, 0, true));
        if (!TryDecodeDiveLootRequest(lootRequestPacket, out sequence, out var lootRequest) ||
            sequence != 68 ||
            lootRequest != new DiveLootRequest(0x6000000000000042, 101, 2, 1, 0, true))
            throw new InvalidOperationException("Dive loot request round-trip failed");
        BinaryPrimitives.WriteInt32LittleEndian(lootRequestPacket.AsSpan(HeaderSize + 12), -1);
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

        var expectedMissionState = new MissionState(8, 501, 2, 2, 7001, new[]
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

        var missionRosterPacket = EncodeMissionRoster(
            72, new MissionRoster(3, new[] { 101, 205, 999 }));
        if (!TryDecodeMissionRoster(missionRosterPacket, out sequence, out var missionRoster) ||
            sequence != 72 || missionRoster.Revision != 3 ||
            !missionRoster.MissionIds.AsSpan().SequenceEqual(new[] { 101, 205, 999 }))
            throw new InvalidOperationException("Mission roster round-trip failed");
        BinaryPrimitives.WriteInt32LittleEndian(
            missionRosterPacket.AsSpan(MissionRosterFixedSize + sizeof(int)), 101);
        if (TryDecodeMissionRoster(missionRosterPacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted duplicate mission IDs");

        var worldRequestPacket = EncodeWorldFlagRequest(
            72, new WorldFlagRequest("glacier/mirror-1", true));
        if (!TryDecodeWorldFlagRequest(worldRequestPacket, out sequence, out var worldRequest) ||
            sequence != 72 || worldRequest != new WorldFlagRequest("glacier/mirror-1", true))
            throw new InvalidOperationException("World flag request round-trip failed");
        var worldStatePacket = EncodeWorldFlagState(
            73, new WorldFlagState(4, "glacier/mirror-1", false));
        if (!TryDecodeWorldFlagState(worldStatePacket, out sequence, out var worldState) ||
            sequence != 73 || worldState != new WorldFlagState(4, "glacier/mirror-1", false))
            throw new InvalidOperationException("World flag state round-trip failed");
        worldStatePacket[HeaderSize + 4] = 2;
        if (TryDecodeWorldFlagState(worldStatePacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted invalid world flag value");

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
        if (ingredientChunkPacket.Length > 1200 ||
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
            SceneId("A02_01_01"), 3, -12.5f, 3.25f, -0.05f, 91.5f, 2.25f, -0.75f,
            SceneId("Dave_Swim_0042"), -1.25f, 1.25f, true);
        var snapshotPacket = EncodeSnapshot(43, expected);
        if (!TryDecodeSnapshot(snapshotPacket, out sequence, out var actual) ||
            sequence != 43 || actual != expected)
            throw new InvalidOperationException("Snapshot round-trip failed");
        BinaryPrimitives.WriteUInt32LittleEndian(snapshotPacket.AsSpan(HeaderSize + 4), 0);
        if (TryDecodeSnapshot(snapshotPacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted snapshot without scene epoch");

        var expectedVisualState = new PlayerVisualState(
            SceneId("A02_01_01"),
            3,
            new[] { new VisualSprite(SceneId("Dave_Swim_0042"), 0.25f, -0.5f, 0f, 90f,
                1f, 1.25f, 7, 12, true, false) });
        var visualStatePacket = EncodePlayerVisualState(44, expectedVisualState);
        if (!TryDecodePlayerVisualState(visualStatePacket, out sequence, out var actualVisualState) ||
            sequence != 44 || actualVisualState.SceneId != expectedVisualState.SceneId ||
            actualVisualState.SceneEpoch != expectedVisualState.SceneEpoch ||
            actualVisualState.Sprites.Length != 1 || actualVisualState.Sprites[0] != expectedVisualState.Sprites[0])
            throw new InvalidOperationException("Player visual state round-trip failed");
        visualStatePacket[^1] = 2;
        if (TryDecodePlayerVisualState(visualStatePacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted invalid visual sprite flags");
        visualStatePacket = EncodePlayerVisualState(44, expectedVisualState);
        BinaryPrimitives.WriteUInt32LittleEndian(visualStatePacket.AsSpan(HeaderSize + 4), 0);
        if (TryDecodePlayerVisualState(visualStatePacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted player visuals without scene epoch");

        var expectedProjectileVisual = new ProjectileVisualState(
            SceneId("A02_01_01"), 3, 31, SceneId("Harpoon"), 1.25f, -2.5f, 0f, 45f,
            1f, -1f, 7, 14, false, true, 0f, 0f, 0f, 1.25f, -2.5f, 0f);
        var projectileVisualPacket = EncodeProjectileVisualState(45, expectedProjectileVisual);
        if (!TryDecodeProjectileVisualState(projectileVisualPacket, out sequence, out var actualProjectileVisual) ||
            sequence != 45 || actualProjectileVisual != expectedProjectileVisual)
            throw new InvalidOperationException("Projectile visual round-trip failed");
        if (IsValidProjectileVisualState(expectedProjectileVisual with { ScaleY = 0f }))
            throw new InvalidOperationException("Protocol accepted zero projectile visual scale");
        BinaryPrimitives.WriteUInt32LittleEndian(projectileVisualPacket.AsSpan(HeaderSize + 4), 0);
        if (TryDecodeProjectileVisualState(projectileVisualPacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted projectile visuals without scene epoch");
        WriteSingle(snapshotPacket.AsSpan(HeaderSize + 16), float.NaN);
        if (TryDecodeSnapshot(snapshotPacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted invalid movement data");
        snapshotPacket = EncodeSnapshot(44, expected);
        WriteSingle(snapshotPacket.AsSpan(HeaderSize + 36), 0f);
        if (TryDecodeSnapshot(snapshotPacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted invalid visual scale");
        snapshotPacket = EncodeSnapshot(45, expected);
        WriteSingle(snapshotPacket.AsSpan(HeaderSize + 8), float.MaxValue);
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
        if (left.Revision != right.Revision || left.MissionId != right.MissionId ||
            left.Progress != right.Progress || left.State != right.State ||
            left.CurrentTaskId != right.CurrentTaskId ||
            left.Conditions == null || right.Conditions == null ||
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
