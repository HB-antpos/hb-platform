using System.Net;
using System.Text;
using BlazorApp.FaceIngest.Tests.Infrastructure;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BlazorApp.FaceIngest.Tests;

/// <summary>存储护栏与失败路径：超限丢弃回 200、请求体超限 413、写盘失败 503、客户端断开，均不留残片。</summary>
public sealed class StorageGuardTests
{
    [Fact]
    public async Task 已存总字节达到MaxTotalBytes后_回200且不再落盘()
    {
        using var factory = new FaceIngestAppFactory(options => options.Settings["FaceIngest:MaxTotalBytes"] = "1");
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, await PostAsync(client, factory, "first"));
        Assert.Single(factory.StoredCaptures());

        Assert.Equal(HttpStatusCode.OK, await PostAsync(client, factory, "second"));
        Assert.Single(factory.StoredCaptures());
        Assert.Equal(2, factory.AllStoredFiles().Count);
        var warning = Assert.Single(factory.LogEntries, log => log.Message.Contains("storage guard tripped", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("reason=MaxTotalBytes", warning.Message, StringComparison.Ordinal);
        Assert.Contains("droppedTotal=1", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 已存文件数达到MaxFileCount后_回200且不再落盘_丢弃日志节流并累计()
    {
        using var factory = new FaceIngestAppFactory(options => options.Settings["FaceIngest:MaxFileCount"] = "2");
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, await PostAsync(client, factory, "first"));
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.OK, await PostAsync(client, factory, "dropped-" + i));
        }

        Assert.Single(factory.StoredCaptures());
        Assert.Equal(2, factory.AllStoredFiles().Count);
        Assert.Single(factory.LogEntries, log => log.Message.Contains("reason=MaxFileCount", StringComparison.Ordinal));

        factory.Time.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(HttpStatusCode.OK, await PostAsync(client, factory, "dropped-after-window"));
        var warnings = factory.LogEntries.Where(log => log.Message.Contains("reason=MaxFileCount", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, warnings.Count);
        Assert.Contains("droppedTotal=4", warnings[1].Message, StringComparison.Ordinal);
        Assert.Contains("suppressedSinceLastLog=2", warnings[1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 盘剩余空间低于MinFreeDiskBytes时_回200且不落盘()
    {
        using var factory = new FaceIngestAppFactory();
        factory.Disk.FreeBytes = 1024;
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, await PostAsync(client, factory, "low-disk"));

        Assert.Empty(factory.AllStoredFiles());
        Assert.Contains(factory.LogEntries, log => log.Message.Contains("reason=MinFreeDiskBytes", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 剩余空间检查结果会缓存_不是每个请求都查盘()
    {
        using var factory = new FaceIngestAppFactory();
        using var client = factory.CreateClient();

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.OK, await PostAsync(client, factory, "cached-" + i));
        }

        Assert.Equal(1, factory.Disk.CallCount);
        factory.Time.Advance(TimeSpan.FromSeconds(6));
        Assert.Equal(HttpStatusCode.OK, await PostAsync(client, factory, "after-cache"));
        Assert.Equal(2, factory.Disk.CallCount);
    }

    [Fact]
    public async Task MinFreeDiskBytes为0时不检查剩余空间()
    {
        using var factory = new FaceIngestAppFactory(options => options.Settings["FaceIngest:MinFreeDiskBytes"] = "0");
        factory.Disk.FreeBytes = 0;
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, await PostAsync(client, factory, "no-disk-check"));

        Assert.Single(factory.StoredCaptures());
        Assert.Equal(0, factory.Disk.CallCount);
    }

    [Fact]
    public async Task 声明的ContentLength超过上限_回413且无任何残留()
    {
        using var factory = new FaceIngestAppFactory(options => options.Settings["FaceIngest:MaxRequestBodyBytes"] = "1024");
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(
            $"/hik/v1/{factory.FrontCamera.Token}",
            IngestEndpointTests.CreateContent(new byte[1025], "application/octet-stream"));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Empty(factory.AllStoredFiles());
    }

    [Fact]
    public async Task 无ContentLength的请求体读到超过上限_回413并删除临时文件()
    {
        using var factory = new FaceIngestAppFactory(options => options.Settings["FaceIngest:MaxRequestBodyBytes"] = "100000");
        using var client = factory.Server.CreateClient();
        var body = new byte[250_000];
        new Random(42).NextBytes(body);

        using var content = new StreamContent(new NonSeekableReadStream(body));
        content.Headers.TryAddWithoutValidation("Content-Type", HikvisionSamples.MultipartContentType);
        using var response = await client.PostAsync($"/hik/v1/{factory.FrontCamera.Token}", content);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Empty(factory.AllStoredFiles());
        Assert.Equal(0, factory.GetService<RawCaptureStore>().StoredFileCount);
    }

    [Fact]
    public async Task 请求体恰好等于上限_正常落盘()
    {
        using var factory = new FaceIngestAppFactory(options => options.Settings["FaceIngest:MaxRequestBodyBytes"] = "1024");
        using var client = factory.Server.CreateClient();
        var body = new byte[1024];
        new Random(7).NextBytes(body);

        using var content = new StreamContent(new NonSeekableReadStream(body));
        using var response = await client.PostAsync($"/hik/v1/{factory.FrontCamera.Token}", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var capture = Assert.Single(factory.StoredCaptures());
        Assert.Equal(body, capture.ReadBody());
        // 中文注释：确认走的是「无 Content-Length、边读边计数」的路径，而不是声明长度预检。
        Assert.Equal(System.Text.Json.JsonValueKind.Null, capture.ReadMetadata().GetProperty("declaredContentLength").ValueKind);
    }

    [Fact]
    public async Task 关闭落盘时请求体超限也回413()
    {
        using var factory = new FaceIngestAppFactory(options =>
        {
            options.Settings["FaceIngest:CaptureEnabled"] = "false";
            options.Settings["FaceIngest:MaxRequestBodyBytes"] = "1024";
        });
        using var client = factory.Server.CreateClient();

        using var content = new StreamContent(new NonSeekableReadStream(new byte[5000]));
        using var response = await client.PostAsync($"/hik/v1/{factory.FrontCamera.Token}", content);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Empty(factory.AllStoredFiles());
    }

    [Fact]
    public async Task 摄像机目录无法创建_回503且不留任何文件()
    {
        using var factory = new FaceIngestAppFactory();
        using var client = factory.CreateClient();
        // 中文注释：在摄像机目录的位置放一个同名文件，让 CreateDirectory 抛 IOException，模拟磁盘 / 权限故障。
        // 这一步发生在创建 .bin.tmp 之前；「.bin 已改名后 .json 写失败」由下一个测试覆盖。
        var dateDirectory = Path.Combine(factory.RawRoot, "2026-10-12");
        Directory.CreateDirectory(dateDirectory);
        var blocker = Path.Combine(dateDirectory, factory.FrontCamera.CameraId);
        await File.WriteAllTextAsync(blocker, "not a directory");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, await PostAsync(client, factory, "io-failure"));

        Assert.Equal(["raw/2026-10-12/cam-front-01"], factory.AllStoredFiles());
        Assert.Equal(0, factory.GetService<RawCaptureStore>().StoredFileCount);
        Assert.Contains(factory.LogEntries, log => log.Level == LogLevel.Error
            && log.Message.Contains("storage write failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 元数据写失败_回503并删除已改名的bin与全部临时文件()
    {
        using var factory = new FaceIngestAppFactory();
        // 中文注释：直接用 TestServer 的客户端，请求体按需流式读取（默认客户端会先整体缓冲请求体）。
        using var client = factory.Server.CreateClient();
        var cameraDirectory = Path.Combine(factory.RawRoot, "2026-10-12", factory.FrontCamera.CameraId);
        var body = Encoding.ASCII.GetBytes("--boundary\r\nContent-Disposition: form-data; name=\"faceCapture\"\r\n\r\n{}\r\n--boundary--\r\n");

        // 中文注释：请求体第一次被读时 .bin.tmp 已建好；此时在 {base}.json.tmp 的位置建一个同名目录，
        // 于是 .bin 正常写完并改名，写元数据临时文件时 CreateNew 抛 IOException——走「.bin 已改名、.json 写失败」分支。
        using var content = new StreamContent(new BlockMetadataTempOnFirstReadStream(body, cameraDirectory));
        content.Headers.TryAddWithoutValidation("Content-Type", HikvisionSamples.MultipartContentType);
        using var response = await client.PostAsync($"/hik/v1/{factory.FrontCamera.Token}", content);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Single(Directory.GetDirectories(cameraDirectory), path => path.EndsWith(".json.tmp", StringComparison.Ordinal));
        // 中文注释：不留 .bin（否则会成为没有 .json 的半条数据被统计计入）、.bin.tmp、.json、.json.tmp 文件。
        Assert.Empty(factory.AllStoredFiles());
        Assert.Empty(factory.StoredCaptures());
        Assert.Equal(0, factory.GetService<RawCaptureStore>().StoredFileCount);
        Assert.Equal(0, factory.GetService<RawCaptureStore>().RescanStatistics().FileCount);
        Assert.Contains(factory.LogEntries, log => log.Level == LogLevel.Error
            && log.Message.Contains("storage write failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 客户端发送到一半断开_删除已写的临时文件并记Warning()
    {
        using var factory = new FaceIngestAppFactory();
        // 中文注释：直接用 TestServer 的客户端——工厂默认客户端带重定向处理器，会先把请求体整个缓冲，
        // 服务端就看不到「发送到一半」的状态了。
        using var client = factory.Server.CreateClient();
        var prefix = Encoding.ASCII.GetBytes("--boundary\r\nContent-Disposition: form-data; name=\"faceCapture\";\r\n\r\n{\"partial\":");
        // 中文注释：兜底超时，任何断言失败都不会让发送任务永久挂起拖住整个测试进程。
        using var disconnect = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var clientAbort = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        using var content = new StreamContent(new StallingAfterPrefixStream(prefix, disconnect.Token));
        content.Headers.TryAddWithoutValidation("Content-Type", HikvisionSamples.MultipartContentType);
        var sending = client.PostAsync($"/hik/v1/{factory.FrontCamera.Token}", content, clientAbort.Token);

        // 中文注释：先等服务端把前半段写进 .bin.tmp，证明「残片」确实产生过，再模拟摄像机断网。
        // TestServer 里客户端取消会触发服务端 RequestAborted（等同 Kestrel 连接中断），读请求体随即抛 OperationCanceledException。
        Assert.True(
            await WaitUntilAsync(() => factory.AllStoredFiles().Any(file => file.EndsWith(".bin.tmp", StringComparison.Ordinal))),
            "服务端应已开始写临时文件。");
        await clientAbort.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
        await disconnect.CancelAsync();

        Assert.True(
            await WaitUntilAsync(() => factory.LogEntries.Any(log => log.Message.Contains("client aborted", StringComparison.Ordinal))),
            "服务端应记录客户端中途断开。");
        var warning = Assert.Single(factory.LogEntries, log => log.Message.Contains("client aborted", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Empty(factory.AllStoredFiles());
        Assert.Equal(0, factory.GetService<RawCaptureStore>().StoredFileCount);
    }

    [Fact]
    public async Task 存储层直接写入_读请求体抛IOException时归为客户端断开且删除临时文件()
    {
        using var factory = new FaceIngestAppFactory();
        _ = factory.Services;
        var store = factory.GetService<RawCaptureStore>();
        var settings = factory.GetService<FaceIngestSettingsMonitor>().Current;
        var camera = settings.Cameras.Single(camera => camera.CameraId == factory.FrontCamera.CameraId);

        var result = await store.SaveAsync(
            new CaptureWriteRequest(camera, factory.Time.GetUtcNow(), factory.Time.GetTimestamp(), 1024 * 1024, MinimalRequestInfo()),
            new FailingAfterPrefixStream(new byte[300_000]),
            CancellationToken.None);

        Assert.Equal(CaptureSaveStatus.ClientAborted, result.Status);
        Assert.IsType<IOException>(result.Error);
        Assert.Empty(factory.AllStoredFiles());
        Assert.Equal(0, store.StoredFileCount);
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(20);
        }

        return condition();
    }

    private static CapturedRequestInfo MinimalRequestInfo() => new()
    {
        Method = "POST",
        Path = "/hik/v1/***",
        Query = string.Empty,
        Protocol = "HTTP/1.1",
        Headers = [],
        TokenSource = "path",
    };

    private static async Task<HttpStatusCode> PostAsync(HttpClient client, FaceIngestAppFactory factory, string marker)
    {
        using var response = await client.PostAsync(
            $"/hik/v1/{factory.FrontCamera.Token}",
            IngestEndpointTests.CreateContent(Encoding.UTF8.GetBytes("body-" + marker), "application/octet-stream"));
        return response.StatusCode;
    }
}
