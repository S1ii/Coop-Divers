using System;
using System.Buffers.Binary;

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
    bool Flipped);

internal static class Protocol
{
    private const uint Magic = 0x504D5444; // DTMP
    private const byte Version = 1;
    internal const int HeaderSize = 10;
    private const int SnapshotSize = HeaderSize + 17;

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
        packet[HeaderSize + 16] = snapshot.Flipped ? (byte)1 : (byte)0;
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
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z) || packet[HeaderSize + 16] > 1)
            return false;

        snapshot = new PlayerSnapshot(
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(HeaderSize)),
            x, y, z, packet[HeaderSize + 16] == 1);
        return true;
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

        var expected = new PlayerSnapshot(SceneId("A02_01_01"), -12.5f, 3.25f, -0.05f, true);
        var snapshotPacket = EncodeSnapshot(43, expected);
        if (!TryDecodeSnapshot(snapshotPacket, out sequence, out var actual) ||
            sequence != 43 || actual != expected)
            throw new InvalidOperationException("Snapshot round-trip failed");
    }

    private static void WriteSingle(Span<byte> target, float value) =>
        BinaryPrimitives.WriteInt32LittleEndian(target, BitConverter.SingleToInt32Bits(value));

    private static float ReadSingle(ReadOnlySpan<byte> source) =>
        BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(source));
}
