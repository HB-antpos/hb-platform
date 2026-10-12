using System.Net;
using System.Text;
using BlazorApp.FaceIngest.Tests.Infrastructure;
using Xunit;

namespace BlazorApp.FaceIngest.Tests;

/// <summary>后台清理：按保留天数删报文、删 1 小时前的 .tmp 残片、删空目录、重扫校正统计。</summary>
public sealed class CleanupTests
{
    [Fact]
    public async Task RunOnce只保留未过期报文_删过期tmp与空目录_统计被校正()
    {
        using var factory = new FaceIngestAppFactory();
        using var client = factory.CreateClient();
        var now = FaceIngestAppFactory.DefaultUtcNow;

        // 过期：10 天前与 7 天零 1 分钟前；未过期：6 天 23 小时前、昨天、今天。
        await PostAtAsync(factory, client, now.AddDays(-10), "expired-10d");
        await PostAtAsync(factory, client, now.AddDays(-7).AddMinutes(-1), "expired-7d");
        await PostAtAsync(factory, client, now.AddDays(-7).AddHours(1), "kept-6d23h");
        await PostAtAsync(factory, client, now.AddDays(-1), "kept-yesterday");
        await PostAtAsync(factory, client, now, "kept-today");
        Assert.Equal(5, factory.StoredCaptures().Count);

        var todayCameraDirectory = Path.Combine(factory.RawRoot, "2026-10-12", factory.FrontCamera.CameraId);
        var staleTemp = Path.Combine(todayCameraDirectory, CaptureFileNames.FormatBaseName(now.AddHours(-2), Guid.NewGuid().ToString("N")) + ".bin.tmp");
        var freshTemp = Path.Combine(todayCameraDirectory, CaptureFileNames.FormatBaseName(now.AddMinutes(-10), Guid.NewGuid().ToString("N")) + ".json.tmp");
        await File.WriteAllTextAsync(staleTemp, "partial");
        await File.WriteAllTextAsync(freshTemp, "in-flight");

        // 无法从文件名解析时间的 .tmp 回退 LastWriteTimeUtc。
        var unnamedTemp = Path.Combine(todayCameraDirectory, "leftover.bin.tmp");
        await File.WriteAllTextAsync(unnamedTemp, "partial");
        File.SetLastWriteTimeUtc(unnamedTemp, now.UtcDateTime.AddHours(-3));

        var emptyOldDirectory = Path.Combine(factory.RawRoot, "2026-09-01", "cam-gone-09");
        Directory.CreateDirectory(emptyOldDirectory);

        // 故意把计数器弄错，验证清理结束会重扫校正。
        var store = factory.GetService<RawCaptureStore>();
        store.OnFilesDeleted(-1000, -1_000_000);

        var cleaner = factory.GetService<CaptureStorageCleaner>();
        var summary = await cleaner.RunOnceAsync(CancellationToken.None);

        var remaining = factory.StoredCaptures();
        Assert.Equal(3, remaining.Count);
        var remainingBodies = remaining.Select(capture => Encoding.UTF8.GetString(capture.ReadBody())).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["body-kept-6d23h", "body-kept-today", "body-kept-yesterday"], remainingBodies);

        Assert.False(File.Exists(staleTemp));
        Assert.False(File.Exists(unnamedTemp));
        Assert.True(File.Exists(freshTemp));

        // 2026-10-02 整天过期、2026-09-01 本来就空 → 目录被删；2026-10-05 还剩未过期的那条 → 目录保留。
        Assert.Equal(
            ["2026-10-05", "2026-10-11", "2026-10-12"],
            Directory.EnumerateDirectories(factory.RawRoot).Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal).ToArray());

        var expectedBytes = remaining.Sum(capture => new FileInfo(capture.BodyPath).Length + new FileInfo(capture.MetadataPath).Length);
        Assert.Equal(6, store.StoredFileCount);
        Assert.Equal(expectedBytes, store.StoredBytes);
        Assert.Equal(4, summary.DeletedCaptureFiles);
        Assert.Equal(2, summary.DeletedTempFiles);
        Assert.Equal(6, summary.StoredFileCount);
        Assert.Equal(0, summary.FailedDeletes);
    }

    [Fact]
    public async Task 今天与昨天的空目录不删_避免与正在建目录的写入竞争()
    {
        using var factory = new FaceIngestAppFactory();
        _ = factory.Services;
        var todayEmpty = Path.Combine(factory.RawRoot, "2026-10-12", "cam-idle-05");
        var yesterdayEmpty = Path.Combine(factory.RawRoot, "2026-10-11", "cam-idle-05");
        Directory.CreateDirectory(todayEmpty);
        Directory.CreateDirectory(yesterdayEmpty);

        await factory.GetService<CaptureStorageCleaner>().RunOnceAsync(CancellationToken.None);

        Assert.True(Directory.Exists(todayEmpty));
        Assert.True(Directory.Exists(yesterdayEmpty));
    }

    [Fact]
    public async Task 非本服务命名的文件既不统计也不清理()
    {
        using var factory = new FaceIngestAppFactory();
        _ = factory.Services;
        var foreign = Path.Combine(factory.RawRoot, "2026-01-01", "README.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(foreign)!);
        await File.WriteAllTextAsync(foreign, "keep me");

        var summary = await factory.GetService<CaptureStorageCleaner>().RunOnceAsync(CancellationToken.None);

        Assert.True(File.Exists(foreign));
        Assert.Equal(0, summary.StoredFileCount);
    }

    [Fact]
    public async Task 启动时扫描已有报文得到统计_忽略tmp()
    {
        using var seed = new FaceIngestAppFactory();
        var seedDirectory = Path.Combine(seed.RawRoot, "2026-10-12", "cam-front-01");
        Directory.CreateDirectory(seedDirectory);
        await File.WriteAllBytesAsync(Path.Combine(seedDirectory, "20261012T000000000Z_" + new string('a', 32) + ".bin"), new byte[100]);
        await File.WriteAllBytesAsync(Path.Combine(seedDirectory, "20261012T000000000Z_" + new string('a', 32) + ".json"), new byte[20]);
        await File.WriteAllBytesAsync(Path.Combine(seedDirectory, "20261012T000000000Z_" + new string('b', 32) + ".bin.tmp"), new byte[999]);

        var store = seed.GetService<RawCaptureStore>();

        Assert.Equal(2, store.StoredFileCount);
        Assert.Equal(120, store.StoredBytes);
    }

    [Fact]
    public async Task 后台清理服务启动后先跑一轮()
    {
        using var factory = new FaceIngestAppFactory(options => options.EnableCleanupService = true);
        var staleDirectory = Path.Combine(factory.RawRoot, "2026-09-01", "cam-front-01");
        Directory.CreateDirectory(staleDirectory);
        var staleBody = Path.Combine(staleDirectory, "20260901T000000000Z_" + new string('c', 32) + ".bin");
        await File.WriteAllBytesAsync(staleBody, new byte[10]);

        _ = factory.Services;

        // 中文注释：以「本轮结束」日志为准等待（文件删除先于日志，只等文件消失会与日志写入竞争）。
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline
               && !factory.LogEntries.Any(log => log.Message.Contains("cleanup finished", StringComparison.Ordinal)))
        {
            await Task.Delay(50);
        }

        Assert.Contains(factory.LogEntries, log => log.Message.Contains("cleanup finished", StringComparison.Ordinal));
        Assert.False(File.Exists(staleBody));
    }

    private static async Task PostAtAsync(FaceIngestAppFactory factory, HttpClient client, DateTimeOffset at, string marker)
    {
        factory.Time.SetUtcNow(at);
        using var response = await client.PostAsync(
            $"/hik/v1/{factory.FrontCamera.Token}",
            IngestEndpointTests.CreateContent(Encoding.UTF8.GetBytes("body-" + marker), "application/octet-stream"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        factory.Time.SetUtcNow(FaceIngestAppFactory.DefaultUtcNow);
    }
}
