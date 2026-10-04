using System.Buffers;
using MessagePack;

namespace LanPong;

/// <summary>The local browser WebSocket protocol, independent of the UDP wire protocol.</summary>
internal static class BrowserWebSocketProtocol
{
    internal const int Version = 1;
    internal const int SnapshotFieldCount = 20;

    // [version, role, connection, message, udpPort, localAddresses, peerAddress,
    //  leftY, rightY, ballX, ballY, ballVx, ballVy, leftScore, rightScore,
    //  phase, countdown, tick, roundId, pingMs]
    internal static void WriteSnapshot(PongSnapshot snapshot, IBufferWriter<byte> buffer)
    {
        var writer = new MessagePackWriter(buffer);
        writer.WriteArrayHeader(SnapshotFieldCount);
        writer.Write(Version);
        writer.Write((int)snapshot.Role);
        writer.Write((int)snapshot.Connection);
        writer.Write(snapshot.Message);
        writer.Write(snapshot.UdpPort);
        writer.WriteArrayHeader(snapshot.LocalAddresses.Length);
        foreach (var address in snapshot.LocalAddresses) writer.Write(address);
        if (snapshot.PeerAddress is { } peerAddress) writer.Write(peerAddress);
        else writer.WriteNil();
        writer.Write(snapshot.LeftY);
        writer.Write(snapshot.RightY);
        writer.Write(snapshot.BallX);
        writer.Write(snapshot.BallY);
        writer.Write(snapshot.BallVx);
        writer.Write(snapshot.BallVy);
        writer.Write(snapshot.LeftScore);
        writer.Write(snapshot.RightScore);
        writer.Write((int)snapshot.Phase);
        writer.Write(snapshot.Countdown);
        writer.Write(snapshot.Tick);
        writer.Write(snapshot.RoundId);
        if (snapshot.PingMs is { } pingMs) writer.Write(pingMs);
        else writer.WriteNil();
        writer.Flush();
    }

    // [version, axis], where axis is -1, 0, or 1. Each frame contains one value.
    internal static bool TryReadAxis(ReadOnlyMemory<byte> data, out int axis)
    {
        axis = 0;
        if (data.IsEmpty) return false;

        try
        {
            var reader = new MessagePackReader(new ReadOnlySequence<byte>(data));
            if (reader.ReadArrayHeader() != 2 ||
                reader.NextMessagePackType != MessagePackType.Integer ||
                reader.ReadInt32() != Version ||
                reader.NextMessagePackType != MessagePackType.Integer)
                return false;

            var value = reader.ReadInt32();
            if (value is < -1 or > 1 || !reader.End) return false;
            axis = value;
            return true;
        }
        catch (Exception error) when (error is MessagePackSerializationException or InvalidOperationException
                                        or EndOfStreamException or OverflowException)
        {
            return false;
        }
    }
}
