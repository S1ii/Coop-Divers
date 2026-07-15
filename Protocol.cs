using System;
using System.Buffers.Binary;
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
    SceneTransition = 10
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

internal readonly record struct FishSnapshot(
    uint SceneId,
    int Id,
    float X,
    float Y,
    float Z,
    float Rotation,
    byte Flags);

internal readonly record struct PickupRemoved(
    uint SceneId,
    uint WorldId,
    int ItemId);

internal readonly record struct SceneTransitionCommand(
    string SceneName,
    int TransitionType,
    ushort Options);

internal static class Protocol
{
    private const uint Magic = 0x504D5444; // DTMP
    private const byte Version = 9;
    internal const int HeaderSize = 10;
    private const int SnapshotSize = HeaderSize + 41;
    private const int FishSnapshotSize = HeaderSize + 25;
    private const int PickupRemovedSize = HeaderSize + 12;
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
            !float.IsFinite(scaleX) || !float.IsFinite(scaleY) || scaleX == 0f || scaleY == 0f ||
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
        WriteSingle(packet.AsSpan(HeaderSize + 8), snapshot.X);
        WriteSingle(packet.AsSpan(HeaderSize + 12), snapshot.Y);
        WriteSingle(packet.AsSpan(HeaderSize + 16), snapshot.Z);
        WriteSingle(packet.AsSpan(HeaderSize + 20), snapshot.Rotation);
        packet[HeaderSize + 24] = snapshot.Flags;
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

        var x = ReadSingle(packet.Slice(HeaderSize + 8));
        var y = ReadSingle(packet.Slice(HeaderSize + 12));
        var z = ReadSingle(packet.Slice(HeaderSize + 16));
        var rotation = ReadSingle(packet.Slice(HeaderSize + 20));
        var flags = packet[HeaderSize + 24];
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z) ||
            !float.IsFinite(rotation) || MathF.Abs(x) > 1_000_000f ||
            MathF.Abs(y) > 1_000_000f || MathF.Abs(z) > 1_000_000f || flags > 7)
            return false;

        snapshot = new FishSnapshot(
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize)),
            BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(HeaderSize + 4)),
            x, y, z, rotation, flags);
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
        if (encodedName.Length is < 1 or > 128 || command.Options > 0x1ff)
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
        if (options > 0x1ff)
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

    internal static void SelfTest()
    {
        var packet = Encode(PacketType.Hello, 42);
        if (!TryDecode(packet, out var type, out var sequence) ||
            type != PacketType.Hello || sequence != 42)
            throw new InvalidOperationException("Protocol round-trip failed");

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
            SceneId("A02_01_01"), 17, 1.25f, -2.5f, -0.1f, 183f, 5);
        var fishPacket = EncodeFishSnapshot(45, expectedFish);
        if (!TryDecodeFishSnapshot(fishPacket, out sequence, out var actualFish) ||
            sequence != 45 || actualFish != expectedFish)
            throw new InvalidOperationException("Fish snapshot round-trip failed");
        fishPacket[HeaderSize + 24] = 8;
        if (TryDecodeFishSnapshot(fishPacket, out _, out _))
            throw new InvalidOperationException("Protocol accepted invalid fish flags");

        var expectedPickup = new PickupRemoved(SceneId("A02_01_01"), 0xCAFEBABE, 1001);
        var pickupPacket = EncodePickupRemoved(46, expectedPickup);
        if (!TryDecodePickupRemoved(pickupPacket, out sequence, out var actualPickup) ||
            sequence != 46 || actualPickup != expectedPickup)
            throw new InvalidOperationException("Pickup removal round-trip failed");
        var pickupRequest = EncodePickupRequest(47, expectedPickup);
        if (!TryDecodePickupRequest(pickupRequest, out sequence, out actualPickup) ||
            sequence != 47 || actualPickup != expectedPickup)
            throw new InvalidOperationException("Pickup request round-trip failed");

        var expectedTransition = new SceneTransitionCommand("A02_02_01", 3, 0x155);
        var transitionPacket = EncodeSceneTransition(48, expectedTransition);
        if (!TryDecodeSceneTransition(transitionPacket, out sequence, out var actualTransition) ||
            sequence != 48 || actualTransition != expectedTransition)
            throw new InvalidOperationException("Scene transition round-trip failed");

        var expected = new PlayerSnapshot(
            SceneId("A02_01_01"), -12.5f, 3.25f, -0.05f, 91.5f, 2.25f, -0.75f,
            SceneId("Dave_Swim_0042"), -1.25f, 1.25f, true);
        var snapshotPacket = EncodeSnapshot(43, expected);
        if (!TryDecodeSnapshot(snapshotPacket, out sequence, out var actual) ||
            sequence != 43 || actual != expected)
            throw new InvalidOperationException("Snapshot round-trip failed");
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

    private static void WriteSingle(Span<byte> target, float value) =>
        BinaryPrimitives.WriteInt32LittleEndian(target, BitConverter.SingleToInt32Bits(value));

    private static float ReadSingle(ReadOnlySpan<byte> source) =>
        BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(source));
}
