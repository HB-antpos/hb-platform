using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BlazorApp.FaceIngest.Tests.Infrastructure;
using Xunit;

namespace BlazorApp.FaceIngest.Tests;

/// <summary>接收端点：token 三种位置、原样落盘、元数据字段、401 不落盘、任意方法。</summary>
public sealed class IngestEndpointTests
{
    [Fact]
    public async Task 路径token正确_原样落盘且元数据齐全()
    {
        using var factory = new FaceIngestAppFactory();
        using var client = factory.CreateClient();
        var body = HikvisionSamples.IrregularFaceCaptureMultipart();

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/hik/v1/{factory.FrontCamera.Token}");
        request.Content = CreateContent(body, HikvisionSamples.MultipartContentType);
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (compatible; MSIE 9.0; Windows NT 6.1)");
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.9");
        request.Headers.TryAddWithoutValidation("X-Real-IP", "203.0.113.9");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());

        var capture = Assert.Single(factory.StoredCaptures());
        Assert.Equal(body, capture.ReadBody());
        Assert.Equal(2, factory.AllStoredFiles().Count);

        const string dateDirectory = "2026-10-12";
        var expectedDirectory = Path.Combine(factory.RawRoot, dateDirectory, "cam-front-01");
        Assert.Equal(expectedDirectory, Path.GetDirectoryName(capture.BodyPath));
        var fileName = Path.GetFileNameWithoutExtension(capture.BodyPath);
        Assert.StartsWith("20261012T010203456Z_", fileName, StringComparison.Ordinal);

        var metadata = capture.ReadMetadata();
        var captureId = fileName["20261012T010203456Z_".Length..];
        Assert.Equal(32, captureId.Length);
        Assert.Equal(1, metadata.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(captureId, metadata.GetProperty("captureId").GetString());
        Assert.Equal("cam-front-01", metadata.GetProperty("cameraId").GetString());
        Assert.Equal("S1001", metadata.GetProperty("storeCode").GetString());
        Assert.Equal("2026-10-12T01:02:03.4560000Z", metadata.GetProperty("receivedAtUtc").GetString());
        Assert.True(metadata.GetProperty("durationMs").GetDouble() >= 0);
        Assert.Equal("POST", metadata.GetProperty("method").GetString());
        Assert.Equal("/hik/v1/***", metadata.GetProperty("path").GetString());
        Assert.Equal(string.Empty, metadata.GetProperty("query").GetString());
        Assert.Equal("HTTP/1.1", metadata.GetProperty("protocol").GetString());
        Assert.Equal(JsonValueKind.Null, metadata.GetProperty("remoteIpAddress").ValueKind);
        Assert.Equal("203.0.113.9", metadata.GetProperty("forwardedFor").GetString());
        Assert.Equal("203.0.113.9", metadata.GetProperty("realIp").GetString());
        Assert.Equal(HikvisionSamples.MultipartContentType, metadata.GetProperty("contentType").GetString());
        Assert.Equal(body.Length, metadata.GetProperty("declaredContentLength").GetInt64());
        Assert.Equal(body.Length, metadata.GetProperty("bodyBytes").GetInt64());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(body)), metadata.GetProperty("bodySha256").GetString());
        Assert.Equal("path", metadata.GetProperty("tokenSource").GetString());

        var headers = ReadHeaders(metadata);
        Assert.Contains(headers, header => header.Name == "Content-Type" && header.Value == HikvisionSamples.MultipartContentType);
        Assert.Contains(headers, header => header.Name == "User-Agent" && header.Value.Contains("MSIE 9.0", StringComparison.Ordinal));
        Assert.Contains(headers, header => header.Name == "X-Real-IP" && header.Value == "203.0.113.9");
    }

    [Fact]
    public async Task Query_token可用_摄像机追加参数也接受()
    {
        using var factory = new FaceIngestAppFactory();
        using var client = factory.CreateClient();
        var body = Encoding.UTF8.GetBytes("<EventNotificationAlert>query</EventNotificationAlert>");

        var url = $"/hik/v1?token={factory.FrontCamera.Token}&macAddress=44:19:b6:00:00:01&channelID=1&eventType=faceCapture";
        using var response = await client.PostAsync(url, CreateContent(body, "application/xml; charset=\"UTF-8\""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var capture = Assert.Single(factory.StoredCaptures());
        Assert.Equal(body, capture.ReadBody());
        var metadata = capture.ReadMetadata();
        Assert.Equal("query", metadata.GetProperty("tokenSource").GetString());
        Assert.Equal("/hik/v1", metadata.GetProperty("path").GetString());
        Assert.Equal(
            "?token=***&macAddress=44:19:b6:00:00:01&channelID=1&eventType=faceCapture",
            metadata.GetProperty("query").GetString());
    }

    [Fact]
    public async Task Query_token带末尾斜杠也可用()
    {
        using var factory = new FaceIngestAppFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(
            $"/hik/v1/?token={factory.FrontCamera.Token}",
            CreateContent([1, 2, 3], "application/octet-stream"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var metadata = Assert.Single(factory.StoredCaptures()).ReadMetadata();
        Assert.Equal("query", metadata.GetProperty("tokenSource").GetString());
        Assert.Equal("?token=***", metadata.GetProperty("query").GetString());
    }

    [Fact]
    public async Task Basic认证的password作token可用_Authorization被打码()
    {
        using var factory = new FaceIngestAppFactory();
        using var client = factory.CreateClient();
        var body = Encoding.UTF8.GetBytes("basic-body");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/hik/v1");
        request.Content = CreateContent(body, "application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:" + factory.FrontCamera.Token)));
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var metadata = Assert.Single(factory.StoredCaptures()).ReadMetadata();
        Assert.Equal("basic", metadata.GetProperty("tokenSource").GetString());
        var authorization = Assert.Single(ReadHeaders(metadata), header => header.Name == "Authorization");
        Assert.Equal("***", authorization.Value);
    }

    [Fact]
    public async Task 路径token后追加子路径与额外query也接受()
    {
        using var factory = new FaceIngestAppFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(
            $"/hik/v1/{factory.FrontCamera.Token}/ISAPI/Event/notification?macAddress=44:19:b6:00:00:01&channelID=1",
            CreateContent([0x00, 0x0D, 0x0A], "application/octet-stream"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var metadata = Assert.Single(factory.StoredCaptures()).ReadMetadata();
        Assert.Equal("path", metadata.GetProperty("tokenSource").GetString());
        Assert.Equal("/hik/v1/***/ISAPI/Event/notification", metadata.GetProperty("path").GetString());
        Assert.Equal("?macAddress=44:19:b6:00:00:01&channelID=1", metadata.GetProperty("query").GetString());
    }

    [Fact]
    public async Task 固件把参数用and直接拼在路径token后也接受()
    {
        using var factory = new FaceIngestAppFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(
            $"/hik/v1/{factory.FrontCamera.Token}&macAddress=44:19:b6:00:00:01&channelID=1",
            CreateContent([9], "application/octet-stream"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var metadata = Assert.Single(factory.StoredCaptures()).ReadMetadata();
        Assert.Equal("/hik/v1/***&macAddress=44:19:b6:00:00:01&channelID=1", metadata.GetProperty("path").GetString());
    }

    [Theory]
    [InlineData("wrong-path-token")]
    [InlineData("no-token")]
    [InlineData("empty-query-token")]
    [InlineData("wrong-query-token")]
    [InlineData("wrong-basic-token")]
    [InlineData("disabled-camera")]
    public async Task 鉴权失败返回401且不落任何文件(string scenario)
    {
        using var factory = new FaceIngestAppFactory();
        using var client = factory.CreateClient();
        var wrongToken = TestCamera.NewToken();

        using var request = scenario switch
        {
            "wrong-path-token" => new HttpRequestMessage(HttpMethod.Post, $"/hik/v1/{wrongToken}"),
            "no-token" => new HttpRequestMessage(HttpMethod.Post, "/hik/v1"),
            "empty-query-token" => new HttpRequestMessage(HttpMethod.Post, "/hik/v1?token="),
            "wrong-query-token" => new HttpRequestMessage(HttpMethod.Post, $"/hik/v1?token={wrongToken}"),
            "wrong-basic-token" => new HttpRequestMessage(HttpMethod.Post, "/hik/v1"),
            "disabled-camera" => new HttpRequestMessage(HttpMethod.Post, $"/hik/v1/{factory.DisabledCamera.Token}"),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
        if (scenario == "wrong-basic-token")
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:" + wrongToken)));
        }

        request.Content = CreateContent(HikvisionSamples.IrregularFaceCaptureMultipart(), HikvisionSamples.MultipartContentType);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        Assert.Empty(factory.AllStoredFiles());
        Assert.Contains(factory.LogEntries, log => log.Level == Microsoft.Extensions.Logging.LogLevel.Warning
            && log.Message.Contains("status=401", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 同一来源60秒内多次401只记一条Warning_下一条带上被抑制次数()
    {
        using var factory = new FaceIngestAppFactory();
        using var client = factory.CreateClient();

        for (var i = 0; i < 5; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/hik/v1/{TestCamera.NewToken()}");
            request.Headers.TryAddWithoutValidation("X-Real-IP", "198.51.100.7");
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        Assert.Single(factory.LogEntries, IsRejection);

        factory.Time.Advance(TimeSpan.FromSeconds(61));
        using (var request = new HttpRequestMessage(HttpMethod.Post, $"/hik/v1/{TestCamera.NewToken()}"))
        {
            request.Headers.TryAddWithoutValidation("X-Real-IP", "198.51.100.7");
            using var response = await client.SendAsync(request);
        }

        var rejections = factory.LogEntries.Where(IsRejection).ToList();
        Assert.Equal(2, rejections.Count);
        Assert.Contains("suppressedSinceLastLog=4", rejections[1].Message, StringComparison.Ordinal);
        Assert.Contains("source=198.51.100.7", rejections[1].Message, StringComparison.Ordinal);

        static bool IsRejection(CapturedLog log) => log.Message.Contains("status=401", StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("172.18.0.1")]
    [InlineData("::ffff:172.18.0.1")]
    public async Task 容器内连接层地址是Docker网桥网关时_401按XRealIP分来源节流(string bridgeGateway)
    {
        // 中文注释：生产经 Nginx → 127.0.0.1:5004 → docker-proxy 进入容器，连接层地址是网桥网关而非回环；
        // Kestrel 监听双栈 [::]，生产里实际看到的是 IPv4 映射形式 ::ffff:172.18.0.1，两种形式都要按 X-Real-IP 分桶。
        using var factory = new FaceIngestAppFactory(options => options.RemoteIpAddress = IPAddress.Parse(bridgeGateway));
        using var client = factory.CreateClient();

        foreach (var realIp in new[] { "203.0.113.10", "198.51.100.20", "203.0.113.10" })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, await PostWrongTokenAsync(client, realIp));
        }

        var rejections = factory.LogEntries.Where(IsRejection).ToList();
        Assert.Equal(2, rejections.Count);
        Assert.Contains("source=203.0.113.10", rejections[0].Message, StringComparison.Ordinal);
        Assert.Contains("source=198.51.100.20", rejections[1].Message, StringComparison.Ordinal);
        Assert.DoesNotContain(rejections, log => log.Message.Contains("172.18.0.1", StringComparison.Ordinal));

        // 中文注释：没带 X-Real-IP（不经 Nginx 的本机直连）时退回连接层地址，映射地址还原成 IPv4 记录。
        Assert.Equal(HttpStatusCode.Unauthorized, await PostWrongTokenAsync(client, realIp: null));
        Assert.Contains(factory.LogEntries, log => IsRejection(log) && log.Message.Contains("source=172.18.0.1", StringComparison.Ordinal));

        static bool IsRejection(CapturedLog log) => log.Message.Contains("status=401", StringComparison.Ordinal);
    }

    [Fact]
    public async Task 连接层地址是公网地址时_不信任XRealIP()
    {
        using var factory = new FaceIngestAppFactory(options => options.RemoteIpAddress = IPAddress.Parse("198.51.100.99"));
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, await PostWrongTokenAsync(client, "203.0.113.10"));
        Assert.Equal(HttpStatusCode.Unauthorized, await PostWrongTokenAsync(client, "203.0.113.11"));

        var rejection = Assert.Single(factory.LogEntries, log => log.Message.Contains("status=401", StringComparison.Ordinal));
        Assert.Contains("source=198.51.100.99", rejection.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.254", true)]
    [InlineData("192.168.10.1", true)]
    [InlineData("169.254.1.1", true)]
    [InlineData("::1", true)]
    [InlineData("fd00::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("172.15.0.1", false)]
    [InlineData("172.32.0.1", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("::ffff:172.18.0.1", true)]
    [InlineData("::ffff:127.0.0.1", true)]
    [InlineData("::ffff:10.0.0.5", true)]
    [InlineData("::ffff:8.8.8.8", false)]
    [InlineData("::ffff:172.32.0.1", false)]
    public void 本机代理地址判定(string address, bool expected)
    {
        Assert.Equal(expected, FaceIngestRequestHandler.IsLocalProxyAddress(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("172.18.0.1")]
    [InlineData("::ffff:172.18.0.1")]
    public async Task 容器内落盘的元数据_remoteIpAddress记连接层地址_realIp记XRealIP(string bridgeGateway)
    {
        using var factory = new FaceIngestAppFactory(options => options.RemoteIpAddress = IPAddress.Parse(bridgeGateway));
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/hik/v1/{factory.FrontCamera.Token}");
        request.Content = CreateContent([1, 2, 3], "application/octet-stream");
        request.Headers.TryAddWithoutValidation("X-Real-IP", "203.0.113.10");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var metadata = Assert.Single(factory.StoredCaptures()).ReadMetadata();
        // 中文注释：元数据照实记录连接层地址，不做映射还原（runbook 第 8 节写的就是 ::ffff:172.18.0.1）。
        Assert.Equal(bridgeGateway, metadata.GetProperty("remoteIpAddress").GetString());
        Assert.Equal("203.0.113.10", metadata.GetProperty("realIp").GetString());
    }

    [Fact]
    public async Task 同名多值请求头逐条保留且保持先后_敏感头每条都打码()
    {
        using var factory = new FaceIngestAppFactory();
        using var client = factory.CreateClient();
        var token = factory.FrontCamera.Token;

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/hik/v1/{token}");
        request.Content = CreateContent([1, 2, 3], "application/octet-stream");
        request.Headers.TryAddWithoutValidation("X-Hik-Dup", new[] { "first", "second-" + token, "third" });
        request.Headers.TryAddWithoutValidation("Cookie", new[] { "c1=x", "c2=y" });
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var headers = ReadHeaders(Assert.Single(factory.StoredCaptures()).ReadMetadata());
        // 中文注释：不能合并成一条「first,second-***,third」：P1 分析固件行为要看到重复项本身；夹带的 token 逐条打码。
        Assert.Equal(
            ["first", "second-***", "third"],
            headers.Where(header => header.Name == "X-Hik-Dup").Select(header => header.Value).ToArray());
        Assert.Equal(
            ["***", "***"],
            headers.Where(header => header.Name == "Cookie").Select(header => header.Value).ToArray());
    }

    private static async Task<HttpStatusCode> PostWrongTokenAsync(HttpClient client, string? realIp)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/hik/v1/{TestCamera.NewToken()}");
        if (realIp is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Real-IP", realIp);
        }

        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("PUT")]
    [InlineData("HEAD")]
    public async Task 任意方法带正确token都记录_GET模拟Test按钮落0字节报文(string method)
    {
        using var factory = new FaceIngestAppFactory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(new HttpMethod(method), $"/hik/v1/{factory.FrontCamera.Token}");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var capture = Assert.Single(factory.StoredCaptures());
        Assert.Empty(capture.ReadBody());
        var metadata = capture.ReadMetadata();
        Assert.Equal(method, metadata.GetProperty("method").GetString());
        Assert.Equal(0, metadata.GetProperty("bodyBytes").GetInt64());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Array.Empty<byte>())), metadata.GetProperty("bodySha256").GetString());
    }

    [Fact]
    public async Task 关闭CaptureEnabled时鉴权通过回200但不落盘()
    {
        using var factory = new FaceIngestAppFactory(options => options.Settings["FaceIngest:CaptureEnabled"] = "false");
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(
            $"/hik/v1/{factory.FrontCamera.Token}",
            CreateContent(HikvisionSamples.IrregularFaceCaptureMultipart(), HikvisionSamples.MultipartContentType));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(factory.AllStoredFiles());
        Assert.Contains(factory.LogEntries, log => log.Message.Contains("capture disabled", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 并发20个请求生成20对互不相同的文件且统计正确()
    {
        using var factory = new FaceIngestAppFactory();
        using var client = factory.CreateClient();
        var bodies = Enumerable.Range(0, 20)
            .Select(i => Encoding.UTF8.GetBytes($"--boundary\r\npayload-{i}-" + new string('x', 1000 + i) + "\r\n--boundary--\r\n"))
            .ToArray();

        var responses = await Task.WhenAll(bodies.Select(async body =>
        {
            using var response = await client.PostAsync(
                $"/hik/v1/{factory.FrontCamera.Token}",
                CreateContent(body, HikvisionSamples.MultipartContentType));
            return response.StatusCode;
        }));

        Assert.All(responses, status => Assert.Equal(HttpStatusCode.OK, status));
        var captures = factory.StoredCaptures();
        Assert.Equal(20, captures.Count);
        Assert.Equal(40, factory.AllStoredFiles().Count);
        Assert.Equal(20, captures.Select(capture => capture.BodyPath).Distinct(StringComparer.Ordinal).Count());
        Assert.All(captures, capture => Assert.True(File.Exists(capture.BodyPath)));

        var storedBodies = captures.Select(capture => Convert.ToHexString(capture.ReadBody())).Order(StringComparer.Ordinal);
        var sentBodies = bodies.Select(Convert.ToHexString).Order(StringComparer.Ordinal);
        Assert.Equal(sentBodies, storedBodies);

        var store = factory.GetService<RawCaptureStore>();
        var expectedBytes = captures.Sum(capture => new FileInfo(capture.BodyPath).Length + new FileInfo(capture.MetadataPath).Length);
        Assert.Equal(40, store.StoredFileCount);
        Assert.Equal(expectedBytes, store.StoredBytes);
    }

    internal static HttpContent CreateContent(byte[] body, string contentType)
    {
        var content = new ByteArrayContent(body);
        content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        return content;
    }

    internal static IReadOnlyList<(string Name, string Value)> ReadHeaders(JsonElement metadata)
        => [.. metadata.GetProperty("headers").EnumerateArray()
            .Select(header => (header.GetProperty("name").GetString()!, header.GetProperty("value").GetString()!))];
}
