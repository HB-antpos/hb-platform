using BlazorApp.FaceIngest.Tests.Infrastructure;
using Xunit;
using static BlazorApp.FaceIngest.Tests.Infrastructure.P1TestSupport;

namespace BlazorApp.FaceIngest.Tests;

/// <summary>清理任务对 P1 产物的口径：处理标记与报文同一保留期删除且不计入统计；本地图片按保留期删除。</summary>
public sealed class ProcessingCleanupTests
{
    private static readonly DateTimeOffset Now = FaceIngestAppFactory.DefaultUtcNow;

    [Fact]
    public async Task 过期报文的标记与本地图片一起删除_未过期的保留_统计只算报文()
    {
        using var factory = new FaceIngestAppFactory();
        using var client = factory.CreateClient();
        await PostAtAsync(factory, client, Now.AddDays(-10), HikvisionSamples.OfficialFaceCaptureMultipart(), HikvisionSamples.MultipartContentType);
        await PostAtAsync(factory, client, Now, HikvisionSamples.OfficialFaceCaptureMultipart(), HikvisionSamples.MultipartContentType);
        factory.Time.SetUtcNow(Now);
        Assert.Equal(2, (await factory.GetService<CaptureProcessor>().RunOnceAsync(CancellationToken.None)).Processed);

        var bases = CaptureBasePaths(factory);
        var (expiredBase, keptBase) = (bases[0], bases[1]);
        var imagesRoot = Path.Combine(factory.StorageRoot, "images");
        var expiredImages = Path.Combine(imagesRoot, "2026-10-02");
        var keptImages = Path.Combine(imagesRoot, "2026-10-12", "cam-front-01");
        Assert.Equal(2, Directory.GetFiles(expiredImages, "*", SearchOption.AllDirectories).Length);
        // 中文注释：图片的写入时间是真实时钟；固定成假时钟的「现在」，避免测试结果依赖运行日期。
        foreach (var file in Directory.GetFiles(keptImages))
        {
            File.SetLastWriteTimeUtc(file, Now.UtcDateTime);
        }

        var staleTemp = Path.Combine(keptImages, "leftover.jpg.tmp");
        await File.WriteAllTextAsync(staleTemp, "partial");
        File.SetLastWriteTimeUtc(staleTemp, Now.UtcDateTime.AddHours(-2));

        var summary = await factory.GetService<CaptureStorageCleaner>().RunOnceAsync(CancellationToken.None);

        Assert.Equal(2, summary.DeletedCaptureFiles);
        Assert.Equal(2, summary.DeletedMarkerFiles);
        Assert.Equal(2, summary.DeletedImageFiles);
        Assert.Equal(1, summary.DeletedTempFiles);
        Assert.Equal(0, summary.FailedDeletes);
        Assert.False(File.Exists(expiredBase + ".processed"));
        Assert.False(File.Exists(expiredBase + ".parsed.json"));
        Assert.False(Directory.Exists(expiredImages));
        Assert.True(File.Exists(keptBase + ".processed"));
        Assert.True(File.Exists(keptBase + ".parsed.json"));
        Assert.Equal(2, Directory.GetFiles(keptImages).Length);
        Assert.False(File.Exists(staleTemp));
        Assert.Equal(2, summary.StoredFileCount);
        Assert.Equal(2, factory.GetService<RawCaptureStore>().StoredFileCount);
    }

    [Fact]
    public async Task 日期目录未整天过期但图片写入时间早于截止_也删除()
    {
        using var factory = new FaceIngestAppFactory();
        _ = factory.Services;
        var directory = Path.Combine(factory.StorageRoot, "images", "2026-10-05", "cam-front-01");
        Directory.CreateDirectory(directory);
        var early = Path.Combine(directory, new string('a', 32) + "_1.jpg");
        var late = Path.Combine(directory, new string('b', 32) + "_1.jpg");
        await File.WriteAllBytesAsync(early, [1]);
        await File.WriteAllBytesAsync(late, [2]);
        // 截止时间 = 2026-10-05T01:02:03.456Z。
        File.SetLastWriteTimeUtc(early, new DateTime(2026, 10, 5, 0, 30, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(late, new DateTime(2026, 10, 5, 2, 0, 0, DateTimeKind.Utc));

        var summary = await factory.GetService<CaptureStorageCleaner>().RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, summary.DeletedImageFiles);
        Assert.False(File.Exists(early));
        Assert.True(File.Exists(late));
    }

    [Fact]
    public void 文件分类_标记与摘要不当作报文元数据()
    {
        Assert.Equal(CaptureFileKind.Marker, CaptureFileNames.Classify("20261012T010203456Z_" + new string('a', 32) + ".parsed.json"));
        Assert.Equal(CaptureFileKind.Marker, CaptureFileNames.Classify("x.processed"));
        Assert.Equal(CaptureFileKind.Marker, CaptureFileNames.Classify("x.failed"));
        Assert.Equal(CaptureFileKind.Marker, CaptureFileNames.Classify("x.retry"));
        Assert.Equal(CaptureFileKind.Temp, CaptureFileNames.Classify("x.processed.tmp"));
        Assert.Equal(CaptureFileKind.Metadata, CaptureFileNames.Classify("x.json"));
        Assert.Equal(CaptureFileKind.Body, CaptureFileNames.Classify("x.bin"));
    }
}
