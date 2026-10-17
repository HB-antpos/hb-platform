using System.Buffers.Binary;
using Hbpos.Client.Wpf.Services.NetworkDiagnostics;

namespace Hbpos.Client.Tests;

public sealed class DnsWireClientTests
{
    [Fact]
    public void Query_is_a_standard_recursive_A_question_for_the_host()
    {
        var query = DnsWireClient.BuildQuery("hotbargain.vip", 0x1234);

        Assert.Equal(0x1234, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(0, 2)));
        Assert.Equal(0x0100, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(2, 2)));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(4, 2)));
        // 12 字节头 + 10 "hotbargain" + 3 "vip" + 各自的长度字节 + 结尾 0 + QTYPE/QCLASS。
        Assert.Equal(12 + 1 + 10 + 1 + 3 + 1 + 4, query.Length);
        Assert.Equal(10, query[12]);
        Assert.Equal((byte)'h', query[13]);
        Assert.Equal(3, query[23]);
        Assert.Equal(0, query[27]);
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(28, 2)));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(30, 2)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a..b")]
    public void Query_rejects_invalid_host_names(string host)
    {
        Assert.Throws<ArgumentException>(() => DnsWireClient.BuildQuery(host, 1));
    }

    [Fact]
    public void Response_addresses_are_read_through_compressed_names_and_other_record_types_are_skipped()
    {
        var response = BuildResponse(
            0x1234,
            flags: 0x8180,
            answers:
            [
                Answer(type: 5, data: [3, (byte)'a', (byte)'b', (byte)'c', 0]),
                Answer(type: 1, data: [203, 0, 113, 20]),
                Answer(type: 1, data: [203, 0, 113, 21])
            ]);

        var addresses = DnsWireClient.ParseAddresses(response, 0x1234);

        Assert.Equal(["203.0.113.20", "203.0.113.21"], addresses);
    }

    [Fact]
    public void Response_with_another_id_an_error_code_or_no_response_flag_is_rejected()
    {
        var good = new[] { Answer(type: 1, data: [1, 2, 3, 4]) };

        Assert.Null(DnsWireClient.ParseAddresses(BuildResponse(1, 0x8180, good), 2));
        Assert.Null(DnsWireClient.ParseAddresses(BuildResponse(1, 0x8183, good), 1));
        Assert.Null(DnsWireClient.ParseAddresses(BuildResponse(1, 0x0100, good), 1));
    }

    [Fact]
    public void Truncated_or_garbage_responses_return_null_instead_of_throwing()
    {
        var response = BuildResponse(1, 0x8180, [Answer(type: 1, data: [1, 2, 3, 4])]);

        Assert.Null(DnsWireClient.ParseAddresses(response.AsSpan(0, response.Length - 3), 1));
        Assert.Null(DnsWireClient.ParseAddresses([1, 2, 3], 1));
        Assert.Null(DnsWireClient.ParseAddresses([], 1));
    }

    private static byte[] Answer(ushort type, byte[] data)
    {
        var record = new List<byte> { 0xC0, 0x0C };
        record.AddRange(new byte[] { (byte)(type >> 8), (byte)type, 0, 1, 0, 0, 0, 60 });
        record.AddRange(new byte[] { (byte)(data.Length >> 8), (byte)data.Length });
        record.AddRange(data);
        return record.ToArray();
    }

    private static byte[] BuildResponse(ushort id, ushort flags, byte[][] answers)
    {
        var message = new List<byte>();
        var header = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(0, 2), id);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2, 2), flags);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(6, 2), (ushort)answers.Length);
        message.AddRange(header);
        message.AddRange(DnsWireClient.BuildQuery("hotbargain.vip", id).Skip(12));
        foreach (var answer in answers)
        {
            message.AddRange(answer);
        }

        return message.ToArray();
    }
}
