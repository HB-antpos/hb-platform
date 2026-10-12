using System.Net;
using System.Text;
using System.Text.Json;
using BlazorApp.FaceIngest.Tests.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;
using static BlazorApp.FaceIngest.Tests.Infrastructure.P1TestSupport;

namespace BlazorApp.FaceIngest.Tests;

/// <summary>
/// 摄像机来源：配置了数据库连接串 → FaceCamera 表（首次加载失败回 503、刷新失败保留旧快照、非法行跳过）；
/// 没配 → cameras.json（P0 行为，原有测试覆盖）。另含 P1 新增配置项的启动校验。
/// </summary>
public sealed class CameraSourceTests
{
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("camera-source-test");

    [Fact]
    public async Task 数据库来源首次加载失败_所有请求回503且不落盘_health显示未就绪()
    {
        var repository = new FakeFaceCameraRepository();
        using var factory = CreateDatabaseFactory(repository);
        using var client = factory.CreateClient();

        Assert.False(await factory.GetService<DatabaseCameraSource>().RefreshAsync(CancellationToken.None));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, await PostAsync(client, factory.FrontCamera.Token, Body, "application/octet-stream"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, await PostAsync(client, TestCamera.NewToken(), Body, "application/octet-stream"));
        using (var noToken = await client.PostAsync("/hik/v1", IngestEndpointTests.CreateContent(Body, "text/plain")))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, noToken.StatusCode);
        }

        Assert.Empty(factory.AllStoredFiles());
        using (var health = JsonDocument.Parse(await client.GetStringAsync("/health")))
        {
            Assert.Equal("healthy", health.RootElement.GetProperty("status").GetString());
            Assert.Equal("database", health.RootElement.GetProperty("cameraSource").GetString());
            Assert.False(health.RootElement.GetProperty("cameraSourceReady").GetBoolean());
        }

        var refreshError = Assert.Single(factory.LogEntries, log => log.Message.Contains("refresh from database failed", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Error, refreshError.Level);
        Assert.Contains("ingest endpoint answers 503", refreshError.Message, StringComparison.Ordinal);
        Assert.Contains(factory.LogEntries, log => log.Message.Contains("camera source not ready", StringComparison.Ordinal));
        Assert.All(factory.LogEntries, log => Assert.DoesNotContain(factory.FrontCamera.Token, log.AllText, StringComparison.Ordinal));
        Assert.All(factory.LogEntries, log => Assert.DoesNotContain("db.invalid", log.AllText, StringComparison.Ordinal));
    }

    [Fact]
    public async Task 数据库来源加载成功后正常鉴权_刷新失败保留旧快照_恢复后按新表生效()
    {
        var repository = new FakeFaceCameraRepository();
        using var factory = CreateDatabaseFactory(repository);
        using var client = factory.CreateClient();
        var source = factory.GetService<DatabaseCameraSource>();
        var newCamera = TestCamera.Create("cam-new-05", "S1002");

        await LoadCamerasAsync(factory, repository, factory.FrontCamera, factory.DisabledCamera);
        Assert.Equal(HttpStatusCode.OK, await PostAsync(client, factory.FrontCamera.Token, Body, "application/octet-stream"));
        Assert.Equal(HttpStatusCode.Unauthorized, await PostAsync(client, factory.DisabledCamera.Token, Body, "application/octet-stream"));
        Assert.Equal(HttpStatusCode.Unauthorized, await PostAsync(client, TestCamera.NewToken(), Body, "application/octet-stream"));
        var metadata = Assert.Single(factory.StoredCaptures()).ReadMetadata();
        Assert.Equal("cam-front-01", metadata.GetProperty("cameraId").GetString());

        // 刷新失败：保留旧快照继续服务。
        repository.Fail();
        Assert.False(await source.RefreshAsync(CancellationToken.None));
        Assert.Equal(HttpStatusCode.OK, await PostAsync(client, factory.FrontCamera.Token, Body, "application/octet-stream"));
        Assert.Contains(factory.LogEntries, log => log.Level == LogLevel.Error && log.Message.Contains("keeping previous snapshot", StringComparison.Ordinal));

        // 恢复：按新表生效（前门摄像机被删掉 → 401；新摄像机 → 200）。
        await LoadCamerasAsync(factory, repository, newCamera);
        Assert.Equal(HttpStatusCode.Unauthorized, await PostAsync(client, factory.FrontCamera.Token, Body, "application/octet-stream"));
        Assert.Equal(HttpStatusCode.OK, await PostAsync(client, newCamera.Token, Body, "application/octet-stream"));

        using var health = JsonDocument.Parse(await client.GetStringAsync("/health"));
        Assert.True(health.RootElement.GetProperty("cameraSourceReady").GetBoolean());
        Assert.Equal(2, factory.LogEntries.Count(log => log.Message.Contains("camera registry loaded source=database", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task 非法行只跳过该行并记Error_哈希重复的行全部跳过_其余摄像机照常()
    {
        var repository = new FakeFaceCameraRepository();
        using var factory = CreateDatabaseFactory(repository);
        using var client = factory.CreateClient();
        var traversal = TestCamera.Create("cam-x", "S1001");
        var badStore = TestCamera.Create("cam-bad-store", "S1001");
        var dupA = TestCamera.Create("cam-dup-a", "S1001");
        var dupB = TestCamera.Create("cam-dup-b", "S1001");
        repository.Return(
            FakeFaceCameraRepository.Row(factory.FrontCamera),
            new FaceCameraRow("../evil", "S1001", traversal.TokenSha256, true),
            new FaceCameraRow(badStore.CameraId, "S/1001", badStore.TokenSha256, true),
            new FaceCameraRow("cam-bad-hash", "S1001", "not-a-hash", true),
            new FaceCameraRow(null, null, null, true),
            FakeFaceCameraRepository.Row(dupA),
            new FaceCameraRow(dupB.CameraId, dupB.StoreCode, dupA.TokenSha256.ToUpperInvariant(), true));

        Assert.True(await factory.GetService<DatabaseCameraSource>().RefreshAsync(CancellationToken.None));

        Assert.Equal(HttpStatusCode.OK, await PostAsync(client, factory.FrontCamera.Token, Body, "application/octet-stream"));
        Assert.Equal(HttpStatusCode.Unauthorized, await PostAsync(client, traversal.Token, Body, "application/octet-stream"));
        Assert.Equal(HttpStatusCode.Unauthorized, await PostAsync(client, badStore.Token, Body, "application/octet-stream"));
        Assert.Equal(HttpStatusCode.Unauthorized, await PostAsync(client, dupA.Token, Body, "application/octet-stream"));
        Assert.Single(factory.GetService<DatabaseCameraSource>().Current!.Cameras);

        var skipped = factory.LogEntries.Where(log => log.Message.Contains("FaceCamera row skipped", StringComparison.Ordinal)).ToList();
        Assert.Equal(6, skipped.Count);
        Assert.All(skipped, log => Assert.Equal(LogLevel.Error, log.Level));
        Assert.Contains(skipped, log => log.Message.Contains("row #2: CameraId must match", StringComparison.Ordinal));
        Assert.Contains(skipped, log => log.Message.Contains("camera 'cam-bad-store': StoreCode", StringComparison.Ordinal));
        Assert.Contains(skipped, log => log.Message.Contains("camera 'cam-dup-b': TokenSha256 duplicates another camera", StringComparison.Ordinal));
        Assert.All(skipped, log => Assert.DoesNotContain(dupA.TokenSha256, log.AllText, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(factory.LogEntries, log => log.Message.Contains("skippedRows=6", StringComparison.Ordinal));

        // 同样的非法行在节流窗口内不重复刷日志。
        Assert.True(await factory.GetService<DatabaseCameraSource>().RefreshAsync(CancellationToken.None));
        Assert.Equal(6, factory.LogEntries.Count(log => log.Message.Contains("FaceCamera row skipped", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task 后台刷新服务启动后自动加载数据库摄像机()
    {
        var repository = new FakeFaceCameraRepository();
        using var factory = CreateDatabaseFactory(repository, configure: options => options.EnableCameraRefreshService = true);
        repository.Return(FakeFaceCameraRepository.Row(factory.FrontCamera));
        using var client = factory.CreateClient();

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && factory.GetService<DatabaseCameraSource>().Current is null)
        {
            await Task.Delay(50);
        }

        Assert.Equal(HttpStatusCode.OK, await PostAsync(client, factory.FrontCamera.Token, Body, "application/octet-stream"));
    }

    [Fact]
    public async Task json来源_health显示json且就绪_DB假仓储不会被调用()
    {
        var repository = new FakeFaceCameraRepository();
        using var factory = new FaceIngestAppFactory(options => options.EnableCameraRefreshService = true);
        using var client = factory.CreateClient();

        using var health = JsonDocument.Parse(await client.GetStringAsync("/health"));

        Assert.Equal("json", health.RootElement.GetProperty("cameraSource").GetString());
        Assert.True(health.RootElement.GetProperty("cameraSourceReady").GetBoolean());
        Assert.IsType<JsonFileCameraSource>(factory.GetService<ICameraRegistrySource>());
        Assert.Equal(0, repository.LoadCalls);
    }

    // ------------------------------------------------------------------ P1 配置校验

    [Fact]
    public void Cos开启但缺键_启动失败且只报键名不报值()
    {
        using var factory = new FaceIngestAppFactory(options =>
        {
            options.Settings["FaceIngest:Cos:Enabled"] = "true";
            options.Settings["FaceIngest:Cos:Bucket"] = "fake-bucket-1250000000";
            options.Settings["FaceIngest:Cos:SecretId"] = "fake-secret-id-value";
        });

        var validation = FindValidationException(Assert.ThrowsAny<Exception>(() => factory.CreateClient()));

        Assert.Contains("FaceIngest:Cos:Region is required", validation.Message, StringComparison.Ordinal);
        Assert.Contains("FaceIngest:Cos:SecretKey is required", validation.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("FaceIngest:Cos:Bucket is required", validation.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("fake-secret-id-value", validation.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("this is not a connection string")]
    [InlineData("Data Source=db.invalid")]
    [InlineData("Unknown Keyword=1;Password=fake-password-value")]
    public void 数据库连接串格式非法_启动失败且不回显连接串(string connectionString)
    {
        using var factory = new FaceIngestAppFactory(options => options.Settings["FaceIngest:Database:ConnectionString"] = connectionString);

        var validation = FindValidationException(Assert.ThrowsAny<Exception>(() => factory.CreateClient()));

        Assert.Contains("FaceIngest:Database:ConnectionString", validation.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(connectionString, validation.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("fake-password-value", validation.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("FaceIngest:ProcessingIntervalSeconds", "0")]
    [InlineData("FaceIngest:ProcessingBatchSize", "0")]
    [InlineData("FaceIngest:ProcessingMaxAttempts", "-1")]
    [InlineData("FaceIngest:Database:CameraRefreshSeconds", "0")]
    public void P1数值配置非法_启动失败(string key, string value)
    {
        using var factory = new FaceIngestAppFactory(options => options.Settings[key] = value);

        var validation = FindValidationException(Assert.ThrowsAny<Exception>(() => factory.CreateClient()));

        Assert.Contains(key, validation.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 连接串合法但连不上与COS配置齐全_启动不失败_图片存储选COS()
    {
        var repository = new FakeFaceCameraRepository();
        using var factory = CreateDatabaseFactory(repository, configure: options =>
        {
            options.Settings["FaceIngest:Cos:Enabled"] = "true";
            options.Settings["FaceIngest:Cos:Bucket"] = "fake-bucket-1250000000";
            options.Settings["FaceIngest:Cos:Region"] = "ap-singapore";
            options.Settings["FaceIngest:Cos:SecretId"] = "fake-secret-id";
            options.Settings["FaceIngest:Cos:SecretKey"] = "fake-secret-key";
        });

        using var client = factory.CreateClient();

        Assert.Equal("cos", factory.GetService<IImageStore>().StorageName);
        Assert.True(factory.GetService<CaptureProcessor>().DatabaseEnabled);
        var started = Assert.Single(factory.LogEntries, log => log.Message.Contains("Face ingest started", StringComparison.Ordinal));
        Assert.Contains("cameraSource=database", started.Message, StringComparison.Ordinal);
        Assert.Contains("imageStorage=cos", started.Message, StringComparison.Ordinal);
        Assert.All(factory.LogEntries, log => Assert.DoesNotContain("fake-secret", log.AllText, StringComparison.Ordinal));
    }

    private static OptionsValidationException FindValidationException(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is OptionsValidationException validation)
            {
                return validation;
            }
        }

        throw new Xunit.Sdk.XunitException($"Expected OptionsValidationException but got: {exception}");
    }
}
