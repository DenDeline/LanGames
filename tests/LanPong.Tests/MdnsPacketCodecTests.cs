using System.Net;

namespace LanPong.Tests;

public sealed class MdnsPacketCodecTests
{
    [Test]
    public async Task TryParse_CompressedDnsSdResponse_ExtractsServicePortVersionAndAddress()
    {
        // This is a DNS wire packet assembled independently of Advertisement().
        // Its PTR, SRV, TXT, and A records use pointers into earlier names.
        var parsed = MdnsPacketCodec.TryParse(CompressedResponse(), out var message);

        await Assert.That(parsed).IsTrue();
        await Assert.That(message.IsResponse).IsTrue();
        await Assert.That(message.Questions.Count).IsEqualTo(0);
        await Assert.That(message.Records.Count).IsEqualTo(4);

        var ptr = message.Records.Single(record => record.Type == MdnsPacketCodec.Ptr);
        await Assert.That(ptr.Name).IsEqualTo("_lanpong._udp.local.");
        await Assert.That(ptr.Target).IsEqualTo("Game._lanpong._udp.local.");

        var srv = message.Records.Single(record => record.Type == MdnsPacketCodec.Srv);
        await Assert.That(srv.Name).IsEqualTo(ptr.Target);
        await Assert.That(srv.Target).IsEqualTo("host.local.");
        await Assert.That(srv.Port).IsEqualTo(47888);

        var txt = message.Records.Single(record => record.Type == MdnsPacketCodec.Txt);
        await Assert.That(txt.Name).IsEqualTo(ptr.Target);
        await Assert.That(txt.Version).IsEqualTo(5);
        await Assert.That(txt.Nickname).IsNull();

        var address = message.Records.Single(record => record.Type == MdnsPacketCodec.A);
        await Assert.That(address.Name).IsEqualTo(srv.Target);
        await Assert.That(address.Address).IsEqualTo(IPAddress.Parse("192.168.1.44"));
        await Assert.That(message.Records.All(record => record.Ttl == 120)).IsTrue();
    }

    [Test]
    public async Task Advertisement_WithZeroTtl_EncodesGoodbyeAndRequestedSrvPort()
    {
        var bytes = MdnsPacketCodec.Advertisement("_lanpong._udp.local.",
            "Game._lanpong._udp.local.", "host.local.", 47888,
            [IPAddress.Parse("192.0.2.44")], ttl: 0, nickname: "Хозяин");

        await Assert.That(MdnsPacketCodec.TryParse(bytes, out var message)).IsTrue();
        await Assert.That(message.IsResponse).IsTrue();
        await Assert.That(message.Records.Count).IsEqualTo(4);
        await Assert.That(message.Records.All(record => record.Ttl == 0)).IsTrue();
        await Assert.That(message.Records.Single(record => record.Type == MdnsPacketCodec.Srv).Port)
            .IsEqualTo(47888);
        await Assert.That(message.Records.Single(record => record.Type == MdnsPacketCodec.Txt).Version)
            .IsEqualTo(WirePacket.CurrentVersion);
        await Assert.That(message.Records.Single(record => record.Type == MdnsPacketCodec.Txt).Nickname)
            .IsEqualTo("Хозяин");
    }

    [Test]
    public async Task Advertisement_WithIpv4AndIpv6Addresses_UsesAAndAaaaRecords()
    {
        var ipv4 = IPAddress.Parse("192.0.2.44");
        var ipv6 = IPAddress.Parse("2001:db8::44");
        var bytes = MdnsPacketCodec.Advertisement("_lanpong._udp.local.",
            "Game._lanpong._udp.local.", "host.local.", 47888, [ipv4, ipv6], ttl: 120,
            nickname: "Игрок 😀");

        await Assert.That(MdnsPacketCodec.TryParse(bytes, out var message)).IsTrue();
        await Assert.That(message.Records.Count).IsEqualTo(5);
        await Assert.That(message.Records.Single(record => record.Type == MdnsPacketCodec.A).Address)
            .IsEqualTo(ipv4);
        await Assert.That(message.Records.Single(record => record.Type == MdnsPacketCodec.Aaaa).Address)
            .IsEqualTo(ipv6);
        await Assert.That(message.Records.Single(record => record.Type == MdnsPacketCodec.Txt).Nickname)
            .IsEqualTo("Игрок 😀");
        await Assert.That(message.Records.All(record => record.Ttl == 120)).IsTrue();
    }

    [Test]
    public async Task TryParse_CompressedAaaaResponse_DecodesAddressAndRejectsWrongLength()
    {
        // Replace the final A record in the independent compressed DNS fixture.
        var ipv6 = IPAddress.Parse("2001:db8::44");
        var bytes = CompressedResponse()[..^16]
            .Concat(new byte[] { 0xc0, 0x44, 0x00, 0x1c, 0x80, 0x01, 0x00, 0x00, 0x00, 0x78, 0x00, 0x10 })
            .Concat(ipv6.GetAddressBytes()).ToArray();

        await Assert.That(MdnsPacketCodec.TryParse(bytes, out var message)).IsTrue();
        await Assert.That(message.Records.Single(record => record.Type == MdnsPacketCodec.Aaaa).Address)
            .IsEqualTo(ipv6);

        var wrongLength = (byte[])bytes.Clone();
        wrongLength[^17] = 15;
        await Assert.That(MdnsPacketCodec.TryParse(wrongLength, out _)).IsFalse();
    }

    [Test]
    public async Task Query_Aaaa_EncodesDnsQuestionForIpv6Address()
    {
        var bytes = MdnsPacketCodec.Query("host.local.", MdnsPacketCodec.Aaaa);

        await Assert.That(MdnsPacketCodec.TryParse(bytes, out var message)).IsTrue();
        await Assert.That(message.Questions.Count).IsEqualTo(1);
        await Assert.That(message.Questions[0].Name).IsEqualTo("host.local.");
        await Assert.That(message.Questions[0].Type).IsEqualTo(MdnsPacketCodec.Aaaa);
    }

    [Test]
    public async Task TryParse_RejectsInvalidPointersLengthsAndRecordCounts()
    {
        var valid = CompressedResponse();
        var selfPointer = (byte[])valid.Clone();
        selfPointer[51] = 0x32; // SRV name at offset 50 points to itself.
        var outsidePointer = (byte[])valid.Clone();
        outsidePointer[51] = 0xff;
        var oversizedRdata = (byte[])valid.Clone();
        oversizedRdata[41] = 0x7f; // PTR RDLENGTH exceeds the datagram.
        oversizedRdata[42] = 0xff;
        var oversizedTxtItem = (byte[])valid.Clone();
        oversizedTxtItem[87] = 11; // Only nine text bytes follow this length.
        var tooManyRecords = (byte[])valid.Clone();
        tooManyRecords[7] = 65;
        var invalidLabel = (byte[])valid.Clone();
        invalidLabel[12] = 0x40; // Neither a valid label length nor a pointer.

        foreach (var malformed in new[]
                 {
                     selfPointer, outsidePointer, oversizedRdata, oversizedTxtItem,
                     tooManyRecords, invalidLabel, valid[..^1], new byte[9001]
                 })
            await Assert.That(MdnsPacketCodec.TryParse(malformed, out _)).IsFalse();
    }

    private static byte[] CompressedResponse()
    {
        var bytes = new List<byte>
        {
            // DNS header: response, four answers, no questions or other sections.
            0x00, 0x00, 0x84, 0x00, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0x00,
            0x08
        };
        bytes.AddRange("_lanpong"u8.ToArray());
        bytes.Add(0x04);
        bytes.AddRange("_udp"u8.ToArray());
        bytes.Add(0x05); // "local" begins at offset 26 (0x1a).
        bytes.AddRange("local"u8.ToArray());
        bytes.AddRange([0x00,
            // PTR _lanpong._udp.local. -> Game._lanpong._udp.local.
            0x00, 0x0c, 0x00, 0x01, 0x00, 0x00, 0x00, 0x78, 0x00, 0x07,
            0x04]); // "Game" begins at offset 43 (0x2b).
        bytes.AddRange("Game"u8.ToArray());
        bytes.AddRange([
            0xc0, 0x0c,
            // SRV owner points to "Game"; target is host.local. on port 47888.
            0xc0, 0x2b, 0x00, 0x21, 0x80, 0x01, 0x00, 0x00, 0x00, 0x78,
            0x00, 0x0d, 0x00, 0x00, 0x00, 0x00, 0xbb, 0x10,
            0x04]); // "host" begins at offset 68 (0x44).
        bytes.AddRange("host"u8.ToArray());
        bytes.AddRange([
            0xc0, 0x1a,
            // TXT version=5.
            0xc0, 0x2b, 0x00, 0x10, 0x80, 0x01, 0x00, 0x00, 0x00, 0x78,
            0x00, 0x0a, 0x09]);
        bytes.AddRange("version=5"u8.ToArray());
        bytes.AddRange([
            // A host.local. -> 192.168.1.44.
            0xc0, 0x44, 0x00, 0x01, 0x80, 0x01, 0x00, 0x00, 0x00, 0x78,
            0x00, 0x04, 192, 168, 1, 44]);
        return bytes.ToArray();
    }
}
