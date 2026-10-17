using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Hbpos.Client.Wpf.Services.NetworkDiagnostics;

/// <summary>
/// 最小的 UDP DNS 查询（只问 A 记录）：用来绕过本机配置的 DNS，直接问公共 DNS 同一个域名，
/// 和系统 DNS 的耗时、答案做对比。只读，不改任何设置。
/// </summary>
public static class DnsWireClient
{
    private const ushort TypeA = 1;
    private const ushort ClassIn = 1;

    public static byte[] BuildQuery(string host, ushort id)
    {
        var trimmed = host.TrimEnd('.');
        var labels = trimmed.Split('.');
        if (trimmed.Length == 0 || labels.Any(label => label.Length is 0 or > 63))
        {
            throw new ArgumentException("Invalid host name.", nameof(host));
        }

        using var stream = new MemoryStream();
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header[0..2], id);
        BinaryPrimitives.WriteUInt16BigEndian(header[2..4], 0x0100); // 标准查询，期望递归
        BinaryPrimitives.WriteUInt16BigEndian(header[4..6], 1);
        stream.Write(header);
        foreach (var label in labels)
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            stream.WriteByte((byte)bytes.Length);
            stream.Write(bytes);
        }

        stream.WriteByte(0);
        Span<byte> tail = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(tail[0..2], TypeA);
        BinaryPrimitives.WriteUInt16BigEndian(tail[2..4], ClassIn);
        stream.Write(tail);
        return stream.ToArray();
    }

    /// <summary>解析应答里的 IPv4 地址；ID 不符、RCODE 非 0 或报文损坏时返回 null。</summary>
    public static IReadOnlyList<string>? ParseAddresses(ReadOnlySpan<byte> response, ushort expectedId)
    {
        if (response.Length < 12 || BinaryPrimitives.ReadUInt16BigEndian(response[0..2]) != expectedId)
        {
            return null;
        }

        var flags = BinaryPrimitives.ReadUInt16BigEndian(response[2..4]);
        if ((flags & 0x8000) == 0 || (flags & 0x000F) != 0)
        {
            return null;
        }

        var questions = BinaryPrimitives.ReadUInt16BigEndian(response[4..6]);
        var answers = BinaryPrimitives.ReadUInt16BigEndian(response[6..8]);
        var offset = 12;
        for (var i = 0; i < questions; i++)
        {
            if (!TrySkipName(response, ref offset) || offset + 4 > response.Length)
            {
                return null;
            }

            offset += 4;
        }

        var addresses = new List<string>();
        for (var i = 0; i < answers; i++)
        {
            if (!TrySkipName(response, ref offset) || offset + 10 > response.Length)
            {
                return null;
            }

            var type = BinaryPrimitives.ReadUInt16BigEndian(response[offset..(offset + 2)]);
            var dataLength = BinaryPrimitives.ReadUInt16BigEndian(response[(offset + 8)..(offset + 10)]);
            offset += 10;
            if (offset + dataLength > response.Length)
            {
                return null;
            }

            if (type == TypeA && dataLength == 4)
            {
                addresses.Add(new IPAddress(response.Slice(offset, 4)).ToString());
            }

            offset += dataLength;
        }

        return addresses;
    }

    public static async Task<IReadOnlyList<string>?> QueryAsync(
        IPAddress server,
        string host,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var id = (ushort)Random.Shared.Next(0, ushort.MaxValue + 1);
        var query = BuildQuery(host, id);
        using var socket = new Socket(server.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        using var limited = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limited.CancelAfter(timeout);
        try
        {
            await socket.ConnectAsync(new IPEndPoint(server, 53), limited.Token);
            await socket.SendAsync(query, SocketFlags.None, limited.Token);
            var buffer = new byte[1500];
            var length = await socket.ReceiveAsync(buffer, SocketFlags.None, limited.Token);
            return ParseAddresses(buffer.AsSpan(0, length), id);
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return null;
        }
    }

    private static bool TrySkipName(ReadOnlySpan<byte> message, ref int offset)
    {
        while (offset < message.Length)
        {
            var length = message[offset];
            if (length == 0)
            {
                offset++;
                return true;
            }

            if ((length & 0xC0) == 0xC0)
            {
                // 压缩指针：两个字节后结束，不需要展开名字。
                offset += 2;
                return offset <= message.Length;
            }

            offset += 1 + length;
        }

        return false;
    }
}
