using System.Net;
using System.Net.Http.Headers;
using System.Text;
using BlazorApp.FaceIngest.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using Xunit;

namespace BlazorApp.FaceIngest.Tests;

/// <summary>任何落盘文件与日志都不得出现 token 原文（含 Basic 凭据的 base64 形式）。</summary>
public sealed class TokenRedactionTests
{
    [Fact]
    public async Task 所有落盘文件与日志都不含token原文()
    {
        var basicCamera = TestCamera.Create("cam-basic-03", "S2002");
        var queryCamera = TestCamera.Create("cam-query-04", "S2002");
        using var factory = new FaceIngestAppFactory(options =>
        {
            options.Settings["FaceIngest:MaxRequestBodyBytes"] = "4096";
            foreach (var pair in basicCamera.ToSettings(2).Concat(queryCamera.ToSettings(3)))
            {
                options.Settings[pair.Key] = pair.Value;
            }
        });
        using var client = factory.CreateClient();
        var wrongTokens = new List<string>();
        var basicCredential = Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:" + basicCamera.Token));

        // 路径 token：额外在 Referer、自定义头、Cookie 里重复出现 token，验证兜底打码。
        using (var request = new HttpRequestMessage(HttpMethod.Post, $"/hik/v1/{factory.FrontCamera.Token}/sub?Token={factory.FrontCamera.Token}&a=1"))
        {
            request.Content = IngestEndpointTests.CreateContent(HikvisionSamples.IrregularFaceCaptureMultipart(), HikvisionSamples.MultipartContentType);
            request.Headers.TryAddWithoutValidation("Referer", $"https://hotbargain.vip/face-ingest/hik/v1/{factory.FrontCamera.Token}");
            request.Headers.TryAddWithoutValidation("X-Debug-Token", factory.FrontCamera.Token);
            request.Headers.TryAddWithoutValidation("Cookie", "session=" + factory.FrontCamera.Token);
            request.Headers.TryAddWithoutValidation("Proxy-Authorization", "Basic " + basicCredential);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // query token（参数名大小写不敏感，与 ASP.NET Core 的 Query 集合一致）。
        using (var response = await client.PostAsync(
                   $"/hik/v1?TOKEN={queryCamera.Token}&macAddress=44:19:b6:00:00:01",
                   IngestEndpointTests.CreateContent([1, 2, 3], "application/xml")))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // Basic password 作 token。
        using (var request = new HttpRequestMessage(HttpMethod.Put, "/hik/v1/"))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basicCredential);
            request.Content = IngestEndpointTests.CreateContent([4, 5, 6], "application/json");
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // 各种 401：错 token 出现在路径 / query / Basic 里，日志也不能记。
        for (var i = 0; i < 3; i++)
        {
            var wrong = TestCamera.NewToken();
            wrongTokens.Add(wrong);
            using var request = new HttpRequestMessage(HttpMethod.Post, i switch
            {
                0 => $"/hik/v1/{wrong}",
                1 => $"/hik/v1?token={wrong}",
                _ => "/hik/v1",
            });
            request.Headers.TryAddWithoutValidation("X-Real-IP", $"198.51.100.{i + 1}");
            if (i == 2)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue(
                    "Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:" + wrong)));
            }

            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        // 停用摄像机与超限请求也走一遍日志路径。
        using (var response = await client.PostAsync($"/hik/v1/{factory.DisabledCamera.Token}", IngestEndpointTests.CreateContent([7], "text/plain")))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        using (var response = await client.PostAsync($"/hik/v1/{factory.FrontCamera.Token}", IngestEndpointTests.CreateContent(new byte[5000], "image/jpeg")))
        {
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        }

        Assert.Equal(3, factory.StoredCaptures().Count);
        var secrets = new List<string>
        {
            factory.FrontCamera.Token,
            factory.DisabledCamera.Token,
            basicCamera.Token,
            queryCamera.Token,
            basicCredential,
        };
        secrets.AddRange(wrongTokens);

        foreach (var file in Directory.EnumerateFiles(factory.StorageRoot, "*", SearchOption.AllDirectories))
        {
            var bytes = await File.ReadAllBytesAsync(file);
            var latin1 = Encoding.Latin1.GetString(bytes);
            foreach (var secret in secrets)
            {
                Assert.DoesNotContain(secret, latin1, StringComparison.OrdinalIgnoreCase);
            }
        }

        Assert.NotEmpty(factory.LogEntries);
        foreach (var log in factory.LogEntries)
        {
            foreach (var secret in secrets)
            {
                Assert.DoesNotContain(secret, log.AllText, StringComparison.OrdinalIgnoreCase);
            }
        }

        var pathCapture = factory.StoredCaptures()
            .Select(capture => capture.ReadMetadata())
            .Single(metadata => metadata.GetProperty("cameraId").GetString() == factory.FrontCamera.CameraId);
        Assert.Equal("/hik/v1/***/sub", pathCapture.GetProperty("path").GetString());
        Assert.Equal("?Token=***&a=1", pathCapture.GetProperty("query").GetString());
        var headers = IngestEndpointTests.ReadHeaders(pathCapture);
        Assert.Equal("https://hotbargain.vip/face-ingest/hik/v1/***", Assert.Single(headers, header => header.Name == "Referer").Value);
        Assert.Equal("***", Assert.Single(headers, header => header.Name == "X-Debug-Token").Value);
        Assert.Equal("***", Assert.Single(headers, header => header.Name == "Cookie").Value);
        Assert.Equal("***", Assert.Single(headers, header => header.Name == "Proxy-Authorization").Value);

        var queryCapture = factory.StoredCaptures()
            .Select(capture => capture.ReadMetadata())
            .Single(metadata => metadata.GetProperty("cameraId").GetString() == queryCamera.CameraId);
        Assert.Equal("?TOKEN=***&macAddress=44:19:b6:00:00:01", queryCapture.GetProperty("query").GetString());
    }

    [Fact]
    public async Task 配置打开控制台IncludeScopes也会被强制关闭且请求日志不带RequestPath_scope()
    {
        using var factory = new FaceIngestAppFactory(options =>
            options.Settings["Logging:Console:FormatterOptions:IncludeScopes"] = "true");
        using var client = factory.CreateClient();

        using (var response = await client.PostAsync(
                   $"/hik/v1/{factory.FrontCamera.Token}",
                   IngestEndpointTests.CreateContent([1, 2, 3], "application/json")))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // 第一道：控制台格式化选项在配置绑定之后被代码强制改回 false。
        var consoleOptions = factory.GetService<IOptionsMonitor<SimpleConsoleFormatterOptions>>().CurrentValue;
        Assert.False(consoleOptions.IncludeScopes);

        // 第二道：请求 scope（RequestPath 含 token）从源头不再创建，任何提供程序都拿不到。
        var storedLog = Assert.Single(factory.LogEntries, log => log.Message.Contains("capture stored", StringComparison.Ordinal));
        Assert.DoesNotContain(storedLog.Scopes, scope => scope.Contains("RequestPath", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("?token=abc", "?token=***")]
    [InlineData("?TOKEN=abc&x=1&token=def", "?TOKEN=***&x=1&token=***")]
    [InlineData("?a=1&tok%65n=abc", "?a=1&tok%65n=***")]
    [InlineData("?tokenx=abc&token", "?tokenx=abc&token")]
    [InlineData("?a=b=c&&token=", "?a=b=c&&token=***")]
    public void RedactQuery只打码token参数值(string? rawQuery, string expected)
    {
        Assert.Equal(expected, RequestRedaction.RedactQuery(rawQuery));
    }
}
