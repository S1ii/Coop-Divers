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
    PlayerSnapshot = 5
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

internal static class Protocol
{
    private const uint Magic = 0x504D5444; // DTMP
    private const byte Version = 4;
    internal const int HeaderSize = 10;
    private const int SnapshotSize = HeaderSize + 41;
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
            packet[HeaderSize + 40] > 1)
            return false;

        snapshot = new PlayerSnapshot(
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize)),
            x, y, z, rotation, velocityX, velocityY,
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize + 28)),
            scaleX, scaleY, packet[HeaderSize + 40] == 1);
        return true;
    }

    internal static byte[] EncodeIdentity(PacketType type, uint sequence, string playerName)
    {
        if (type != PacketType.Hello && type != PacketType.HelloAck)
            throw new ArgumentOutOfRangeException(nameof(type));

        var name = NormalizePlayerName(playerName);
        var encodedName = StrictUtf8.GetBytes(name);
        var packet = new byte[HeaderSize + 1 + encodedName.Length];
        Encode(type, sequence).CopyTo(packet, 0);
        packet[HeaderSize] = (byte)encodedName.Length;
        encodedName.CopyTo(packet, HeaderSize + 1);
        return packet;
    }

    internal static bool TryDecodeIdentity(
        ReadOnlySpan<byte> packet,
        PacketType expectedType,
        out uint sequence,
        out string playerName)
    {
        sequence = 0;
        playerName = string.Empty;
        if (!TryDecode(packet, out var type, out sequence) || type != expectedType ||
            packet.Length < HeaderSize + 1 || packet.Length != HeaderSize + 1 + packet[HeaderSize])
            return false;

        try
        {
            playerName = NormalizePlayerName(StrictUtf8.GetString(packet.Slice(HeaderSize + 1)));
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

        var identityPacket = EncodeIdentity(PacketType.Hello, 43, "Дайвер");
        if (!TryDecodeIdentity(identityPacket, PacketType.Hello, out sequence, out var playerName) ||
            sequence != 43 || playerName != "Дайвер")
            throw new InvalidOperationException("Identity round-trip failed");
        identityPacket[^1] = 0xff;
        if (TryDecodeIdentity(identityPacket, PacketType.Hello, out _, out _))
            throw new InvalidOperationException("Protocol accepted invalid UTF-8 identity");

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
    }

    private static void WriteSingle(Span<byte> target, float value) =>
        BinaryPrimitives.WriteInt32LittleEndian(target, BitConverter.SingleToInt32Bits(value));

    private static float ReadSingle(ReadOnlySpan<byte> source) =>
        BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(source));
}
