using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace LanPong;

/// <summary>Small bounded DNS packet codec for the DNS-SD record types used by LAN Pong.</summary>
internal static class MdnsPacketCodec
{
    public const ushort A = 1;
    public const ushort Ptr = 12;
    public const ushort Txt = 16;
    public const ushort Aaaa = 28;
    public const ushort Srv = 33;
    public const ushort Any = 255;
    private const int HeaderLength = 12;
    private const int MaxPacketBytes = 9000;
    private const int MaxRecords = 64;
    private const int MaxCompressionJumps = 32;
    private const int MaxNameBytes = 254;
    private const int MaxLabels = 127;
    private const int MaxLabelBytes = 63;
    private const int Ipv4AddressBytes = 4;
    private const int Ipv6AddressBytes = 16;
    private const ushort InternetClass = 1;
    private const ushort UnicastResponseFlag = 0x8000;
    private const ushort ClassMask = 0x7fff;
    private const ushort CacheFlushClass = UnicastResponseFlag | InternetClass;
    private const ushort AuthoritativeResponseFlags = 0x8400;
    private const ushort OpcodeMask = 0x7800;
    private const byte CompressionMarker = 0xc0;
    private const byte CompressionPointerOffsetMask = 0x3f;
    private const string VersionProperty = "version=";

    public sealed record Question(string Name, ushort Type, ushort Class = InternetClass)
    {
        public bool UnicastResponse => (Class & UnicastResponseFlag) != 0;
    }
    public sealed record Record(string Name, ushort Type, uint Ttl, string? Target = null,
        int Port = 0, int? Version = null, IPAddress? Address = null);
    public sealed record Message(ushort Id, bool IsResponse, IReadOnlyList<Question> Questions,
        IReadOnlyList<Record> Records);

    public static byte[] Query(string name, ushort type)
    {
        using var stream = new MemoryStream();
        WriteHeader(stream, 0, response: false, questionCount: 1, answerCount: 0);
        WriteName(stream, name);
        WriteU16(stream, type);
        WriteU16(stream, InternetClass);
        return stream.ToArray();
    }

    public static byte[] Advertisement(string serviceType, string instanceName, string hostName,
        int port, IReadOnlyList<IPAddress> addresses, uint ttl, ushort id = 0,
        IReadOnlyList<Question>? questions = null, bool legacy = false)
    {
        using var stream = new MemoryStream();
        WriteHeader(stream, id, response: true, questionCount: checked((ushort)(questions?.Count ?? 0)),
            answerCount: checked((ushort)(3 + addresses.Count)));
        if (questions is not null)
            foreach (var question in questions)
            {
                WriteName(stream, question.Name);
                WriteU16(stream, question.Type);
                WriteU16(stream, (ushort)(question.Class & ClassMask));
            }
        WriteRecord(stream, serviceType, Ptr, ttl, data => WriteName(data, instanceName), unique: false);
        WriteRecord(stream, instanceName, Srv, ttl, data =>
        {
            WriteU16(data, 0);
            WriteU16(data, 0);
            WriteU16(data, checked((ushort)port));
            WriteName(data, hostName);
        }, unique: !legacy);
        WriteRecord(stream, instanceName, Txt, ttl, data =>
        {
            var property = Encoding.ASCII.GetBytes($"{VersionProperty}{WirePacket.CurrentVersion}");
            data.WriteByte(checked((byte)property.Length));
            data.Write(property);
        }, unique: !legacy);
        foreach (var address in addresses)
        {
            var type = address.AddressFamily switch
            {
                AddressFamily.InterNetwork => A,
                AddressFamily.InterNetworkV6 => Aaaa,
                _ => throw new ArgumentException("Unsupported address family", nameof(addresses))
            };
            WriteRecord(stream, hostName, type, ttl, data => data.Write(address.GetAddressBytes()), unique: !legacy);
        }
        return stream.ToArray();
    }

    public static bool TryParse(byte[] bytes, out Message message)
    {
        message = null!;
        if (bytes.Length is < HeaderLength or > MaxPacketBytes) return false;
        var span = bytes.AsSpan();
        var id = BinaryPrimitives.ReadUInt16BigEndian(span);
        var flags = BinaryPrimitives.ReadUInt16BigEndian(span[2..]);
        if ((flags & OpcodeMask) != 0) return false; // Standard DNS opcode only.
        var questionsCount = BinaryPrimitives.ReadUInt16BigEndian(span[4..]);
        var answersCount = BinaryPrimitives.ReadUInt16BigEndian(span[6..]);
        var authorityCount = BinaryPrimitives.ReadUInt16BigEndian(span[8..]);
        var additionalCount = BinaryPrimitives.ReadUInt16BigEndian(span[10..]);
        if (questionsCount + answersCount + authorityCount + additionalCount > MaxRecords) return false;
        var offset = HeaderLength;
        var questions = new List<Question>(questionsCount);
        var records = new List<Record>(answersCount + authorityCount + additionalCount);

        for (var i = 0; i < questionsCount; i++)
        {
            if (!ReadName(bytes, ref offset, out var name) || offset + 4 > bytes.Length) return false;
            var type = BinaryPrimitives.ReadUInt16BigEndian(span[offset..]);
            var qclass = BinaryPrimitives.ReadUInt16BigEndian(span[(offset + 2)..]);
            offset += 4;
            questions.Add(new Question(name, type, qclass));
        }
        var count = answersCount + authorityCount + additionalCount;
        for (var i = 0; i < count; i++)
        {
            if (!ReadName(bytes, ref offset, out var name) || offset + 10 > bytes.Length) return false;
            var type = BinaryPrimitives.ReadUInt16BigEndian(span[offset..]);
            var ttl = BinaryPrimitives.ReadUInt32BigEndian(span[(offset + 4)..]);
            var length = BinaryPrimitives.ReadUInt16BigEndian(span[(offset + 8)..]);
            offset += 10;
            if (offset + length > bytes.Length) return false;
            var end = offset + length;
            string? target = null;
            var port = 0;
            int? version = null;
            IPAddress? address = null;
            switch (type)
            {
                case Ptr:
                    if (!ReadName(bytes, ref offset, out target) || offset != end) return false;
                    break;
                case Srv:
                    if (length < 7) return false;
                    port = BinaryPrimitives.ReadUInt16BigEndian(span[(offset + 4)..]);
                    offset += 6;
                    if (!ReadName(bytes, ref offset, out target) || offset != end) return false;
                    break;
                case Txt:
                    while (offset < end)
                    {
                        var itemLength = bytes[offset++];
                        if (offset + itemLength > end) return false;
                        var item = Encoding.UTF8.GetString(bytes, offset, itemLength);
                        if (item.StartsWith(VersionProperty, StringComparison.OrdinalIgnoreCase) &&
                            int.TryParse(item.AsSpan(VersionProperty.Length), out var parsed)) version = parsed;
                        offset += itemLength;
                    }
                    break;
                case A:
                    if (length != Ipv4AddressBytes) return false;
                    address = new IPAddress(span.Slice(offset, Ipv4AddressBytes));
                    break;
                case Aaaa:
                    if (length != Ipv6AddressBytes) return false;
                    address = new IPAddress(span.Slice(offset, Ipv6AddressBytes));
                    break;
            }
            offset = end;
            records.Add(new Record(name, type, ttl, target, port, version, address));
        }
        message = new Message(id, (flags & UnicastResponseFlag) != 0, questions, records);
        return true;
    }

    private static bool ReadName(byte[] bytes, ref int offset, out string name)
    {
        name = string.Empty;
        var labels = new List<string>();
        var cursor = offset;
        var jumped = false;
        var jumps = 0;
        var totalLength = 0;
        while (cursor < bytes.Length && jumps++ < MaxCompressionJumps)
        {
            var length = bytes[cursor++];
            if (length == 0)
            {
                if (!jumped) offset = cursor;
                name = string.Join('.', labels) + ".";
                return true;
            }
            if ((length & CompressionMarker) == CompressionMarker)
            {
                if (cursor >= bytes.Length) return false;
                var pointer = ((length & CompressionPointerOffsetMask) << 8) | bytes[cursor++];
                if (pointer >= bytes.Length || pointer >= cursor - 2) return false;
                if (!jumped) offset = cursor;
                cursor = pointer;
                jumped = true;
                continue;
            }
            if ((length & CompressionMarker) != 0 || length > MaxLabelBytes || cursor + length > bytes.Length) return false;
            totalLength += length + 1;
            if (totalLength > MaxNameBytes || labels.Count >= MaxLabels) return false;
            labels.Add(Encoding.UTF8.GetString(bytes, cursor, length));
            cursor += length;
        }
        return false;
    }

    private static void WriteHeader(Stream stream, ushort id, bool response, ushort questionCount,
        ushort answerCount)
    {
        WriteU16(stream, id);
        WriteU16(stream, response ? AuthoritativeResponseFlags : (ushort)0);
        WriteU16(stream, questionCount);
        WriteU16(stream, answerCount);
        WriteU16(stream, 0);
        WriteU16(stream, 0);
    }

    private static void WriteRecord(Stream stream, string name, ushort type, uint ttl,
        Action<Stream> writeData, bool unique)
    {
        using var data = new MemoryStream();
        writeData(data);
        WriteName(stream, name);
        WriteU16(stream, type);
        WriteU16(stream, unique ? CacheFlushClass : InternetClass);
        WriteU32(stream, ttl);
        WriteU16(stream, checked((ushort)data.Length));
        data.Position = 0;
        data.CopyTo(stream);
    }

    private static void WriteName(Stream stream, string name)
    {
        foreach (var label in name.TrimEnd('.').Split('.'))
        {
            var bytes = Encoding.UTF8.GetBytes(label);
            if (bytes.Length is < 1 or > MaxLabelBytes) throw new ArgumentException("Invalid DNS name", nameof(name));
            stream.WriteByte(checked((byte)bytes.Length));
            stream.Write(bytes);
        }
        stream.WriteByte(0);
    }

    private static void WriteU16(Stream stream, ushort value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteU32(Stream stream, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
        stream.Write(buffer);
    }
}
