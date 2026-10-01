using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hbpos.Api.Services;
using Hbpos.Contracts.Catalog;
using Microsoft.Extensions.Options;

namespace Hbpos.Api.Tests;

public sealed class CatalogDownloadFileStoreTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly DateTimeOffset BaseTime = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hbpos-catalog-files-" + Guid.NewGuid().ToString("N"));
    private readonly MutableTimeProvider _time = new(BaseTime);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Full_file_contains_header_and_every_index_item_in_paging_order()
    {
        var store = CreateStore();
        var target = BuildResult("v-target", Enumerable.Range(1, 12_345).Select(index => ($"93{index:D10}", 1m + index)));

        var file = await store.EnsureFullFileAsync(target, CancellationToken.None);

        var bytes = await File.ReadAllBytesAsync(file.FilePath);
        Assert.Equal(file.Bytes, bytes.Length);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), file.Sha256);
        var lines = ReadLines(bytes);
        var header = JsonSerializer.Deserialize<CatalogFullFileHeader>(lines[0], JsonOptions)!;
        Assert.Equal(CatalogFileFormats.FullV1, header.Format);
        Assert.Equal("v-target", header.CatalogVersion);
        Assert.Equal(target.CatalogIndex.Items.Count, header.TotalCount);
        var items = lines.Skip(1).Select(line => JsonSerializer.Deserialize<CatalogLookupItemDto>(line, JsonOptions)!).ToArray();

        // 与分页协议逐页拼接的结果逐条一致：同一版本两种下载方式写入本地的数据相同。
        var paged = ReadAllPages(target.CatalogIndex);
        Assert.Equal(paged.Count, items.Length);
        Assert.Equal(paged, items);
    }

    [Fact]
    public async Task Full_file_is_published_once_per_version()
    {
        var store = CreateStore();
        var target = BuildResult("v1", [("9300000000001", 1m)]);

        var first = await store.EnsureFullFileAsync(target, CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(5));
        var second = await store.EnsureFullFileAsync(target, CancellationToken.None);

        Assert.Equal(first, second);
        Assert.Equal(first, store.FindFullFile("s01", " v1 "));
        Assert.Null(store.FindFullFile("S01", "v-other"));
        Assert.Null(store.FindFullFile("S02", "v1"));
    }

    [Fact]
    public async Task Unpublished_leftover_version_directory_is_replaced()
    {
        var store = CreateStore();
        var target = BuildResult("v1", [("9300000000001", 1m)]);
        var published = await store.EnsureFullFileAsync(target, CancellationToken.None);
        var versionDirectory = Path.GetDirectoryName(published.FilePath)!;
        // 模拟进程在改名发布前被杀：目录在但没有发布标记。
        File.Delete(Path.Combine(versionDirectory, "version.json"));
        Assert.Null(store.FindFullFile("S01", "v1"));

        var republished = await CreateStore().EnsureFullFileAsync(target, CancellationToken.None);

        Assert.Equal(published.Sha256, republished.Sha256);
        Assert.NotNull(store.FindFullFile("S01", "v1"));
    }

    [Fact]
    public async Task Delta_file_matches_in_memory_delta_operations()
    {
        var store = CreateStore();
        var baseline = BuildResult("v1", [
            ("9300000000001", 1m),
            ("9300000000002", 2m),
            ("9300000000003", 3m),
            ("9300000000005", 5m)
        ]);
        var target = BuildResult("v2", [
            ("9300000000000", 0.5m), // 新增，排在最前
            ("9300000000001", 1m), // 不变
            ("9300000000002", 2.5m), // 改价
            ("9300000000004", 4m), // 新增，夹在中间
            ("9300000000005", 5m), // 不变
            ("9300000000006", 6m) // 新增，排在最后
        ]); // 9300000000003 被删除
        await store.EnsureFullFileAsync(baseline, CancellationToken.None);

        var result = await store.EnsureDeltaFileAsync(target, "v1", CancellationToken.None);

        Assert.Equal(CatalogDeltaFileStatus.Ready, result.Status);
        var expected = target.CatalogIndex.GetDeltaOperations(baseline.CatalogIndex);
        Assert.Equal(expected.Count, result.OperationCount);
        var lines = ReadLines(await File.ReadAllBytesAsync(result.File!.FilePath));
        var header = JsonSerializer.Deserialize<CatalogDeltaFileHeader>(lines[0], JsonOptions)!;
        Assert.Equal(("v1", "v2", 6, 5), (header.BaseCatalogVersion, header.TargetCatalogVersion, header.TargetTotal, header.OperationCount));
        var actual = lines.Skip(1).Select(line => JsonSerializer.Deserialize<CatalogDeltaFileLine>(line, JsonOptions)!).ToArray();
        Assert.Equal(expected.Count, actual.Length);
        for (var index = 0; index < expected.Count; index++)
        {
            Assert.Equal(expected[index].Item, actual[index].Item);
            Assert.Equal(expected[index].DeletedLookup, actual[index].Deleted);
        }

        Assert.Equal(result.File, store.FindDeltaFile("S01", "v1", "v2"));
        Assert.NotNull(store.FindFullFile("S01", "v2"));
    }

    [Fact]
    public async Task Delta_requires_baseline_row_versions_on_disk()
    {
        var store = CreateStore();
        var target = BuildResult("v2", [("9300000000001", 1m)]);

        var result = await store.EnsureDeltaFileAsync(target, "v1", CancellationToken.None);

        Assert.Equal(CatalogDeltaFileStatus.BaselineUnavailable, result.Status);
        Assert.Null(result.File);
    }

    [Fact]
    public async Task Delta_over_threshold_reports_count_without_writing_file()
    {
        var store = CreateStore(options => options.MaxDeltaOperations = 2);
        var baseline = BuildResult("v1", [("9300000000001", 1m)]);
        var target = BuildResult("v2", [("9300000000001", 9m), ("9300000000002", 2m), ("9300000000003", 3m)]);
        await store.EnsureFullFileAsync(baseline, CancellationToken.None);

        var result = await store.EnsureDeltaFileAsync(target, "v1", CancellationToken.None);

        Assert.Equal((CatalogDeltaFileStatus.TooLarge, 3), (result.Status, result.OperationCount));
        Assert.Null(store.FindDeltaFile("S01", "v1", "v2"));
    }

    [Fact]
    public async Task Superseded_versions_are_kept_for_retention_window_then_removed()
    {
        var store = CreateStore(options =>
        {
            options.RetainVersionsPerStore = 2;
            options.SupersededRetentionHours = 24;
        });
        await store.EnsureFullFileAsync(BuildResult("v1", [("9300000000001", 1m)]), CancellationToken.None);
        _time.Advance(TimeSpan.FromHours(1));
        await store.EnsureFullFileAsync(BuildResult("v2", [("9300000000001", 2m)]), CancellationToken.None);
        _time.Advance(TimeSpan.FromHours(1));
        await store.EnsureFullFileAsync(BuildResult("v3", [("9300000000001", 3m)]), CancellationToken.None);

        // v1 被 v2 取代才 2 小时，仍在保留期内。
        Assert.NotNull(store.FindFullFile("S01", "v1"));

        _time.Advance(TimeSpan.FromHours(23));
        await store.EnsureFullFileAsync(BuildResult("v4", [("9300000000001", 4m)]), CancellationToken.None);

        Assert.Null(store.FindFullFile("S01", "v1"));
        // v2 被 v3 取代 23 小时，仍保留；v3、v4 是最新两个版本。
        Assert.NotNull(store.FindFullFile("S01", "v2"));
        Assert.NotNull(store.FindFullFile("S01", "v3"));
        Assert.NotNull(store.FindFullFile("S01", "v4"));
    }

    [Fact]
    public async Task File_sync_plan_follows_paging_plan_rules()
    {
        var store = CreateStore(options => options.MaxDeltaOperations = 1);
        var baseline = BuildResult("v1", [("9300000000001", 1m), ("9300000000002", 2m)]);
        var source = new FakeTargetIndexSource(baseline);
        var service = CreateService(source, store);

        var first = await service.GetFileSyncPlanAsync("S01", baseCatalogVersion: null, CancellationToken.None);
        Assert.Equal((CatalogSyncModes.Full, CatalogFileKinds.Full), (first!.Mode, first.File!.Kind));
        Assert.Equal("api/v1/catalog/files/full?storeCode=S01&catalogVersion=v1", first.File.Path);

        var unchanged = await service.GetFileSyncPlanAsync("S01", "v1", CancellationToken.None);
        Assert.Equal(CatalogSyncModes.NoChange, unchanged!.Mode);
        Assert.Null(unchanged.File);

        source.Current = BuildResult("v2", [("9300000000001", 1m), ("9300000000002", 2.5m)]);
        var delta = await service.GetFileSyncPlanAsync("S01", "v1", CancellationToken.None);
        Assert.Equal((CatalogSyncModes.Delta, CatalogFileKinds.Delta, 1), (delta!.Mode, delta.File!.Kind, delta.DeltaOperationCount));
        Assert.Equal(CatalogFileFormats.DeltaV1, delta.File.Format);

        source.Current = BuildResult("v3", [("9300000000003", 3m)]);
        var tooLarge = await service.GetFileSyncPlanAsync("S01", "v2", CancellationToken.None);
        Assert.Equal((CatalogSyncModes.Full, CatalogFileKinds.Full, 3), (tooLarge!.Mode, tooLarge.File!.Kind, tooLarge.DeltaOperationCount));

        var unknownBase = await service.GetFileSyncPlanAsync("S01", "v-missing", CancellationToken.None);
        Assert.Equal((CatalogSyncModes.Full, (int?)null), (unknownBase!.Mode, unknownBase.DeltaOperationCount));
    }

    [Fact]
    public async Task File_sync_plan_skips_delta_when_delta_is_disabled()
    {
        var store = CreateStore();
        var source = new FakeTargetIndexSource(BuildResult("v1", [("9300000000001", 1m)]));
        var service = CreateService(source, store, deltaEnabled: false);
        await service.GetFileSyncPlanAsync("S01", null, CancellationToken.None);
        source.Current = BuildResult("v2", [("9300000000001", 2m)]);

        var plan = await service.GetFileSyncPlanAsync("S01", "v1", CancellationToken.None);

        Assert.Equal(CatalogSyncModes.Full, plan!.Mode);
        Assert.Null(store.FindDeltaFile("S01", "v1", "v2"));
    }

    private CatalogDownloadFileStore CreateStore(Action<CatalogDownloadFileOptions>? configure = null)
    {
        var options = new CatalogDownloadFileOptions { Enabled = true };
        configure?.Invoke(options);
        return new CatalogDownloadFileStore(_root, options, _time);
    }

    private static CatalogFileSyncService CreateService(
        ICatalogTargetIndexSource source,
        ICatalogDownloadFileStore store,
        bool deltaEnabled = true) => new(
        source,
        store,
        Options.Create(new CatalogDownloadFileOptions { Enabled = true }),
        Options.Create(new CatalogSyncOptions { DeltaEnabled = deltaEnabled }));

    internal static CatalogIndexBuildResult BuildResult(string version, IEnumerable<(string Code, decimal Price)> rows)
    {
        var items = rows.Select(row => new SellableItemDto(
                "S01",
                "P-" + row.Code,
                null,
                "商品 " + row.Code,
                row.Code,
                "I-" + row.Code,
                row.Code,
                row.Price,
                PriceSourceKind.StoreRetailPrice,
                "store",
                1m,
                BaseTime))
            .ToArray();
        var index = new CatalogSellableIndex("S01", BaseTime, items, version);
        return new CatalogIndexBuildResult("S01", BaseTime, items, index);
    }

    private static List<CatalogLookupItemDto> ReadAllPages(CatalogSellableIndex index)
    {
        var items = new List<CatalogLookupItemDto>();
        string? cursor = null;
        do
        {
            var page = index.GetPage(cursor, 5000, checksumVersion: 2);
            items.AddRange(page.Items);
            cursor = page.HasMore ? page.NextCursor : null;
        }
        while (cursor is not null);

        return items;
    }

    private static string[] ReadLines(byte[] gzipBytes)
    {
        using var gzip = new GZipStream(new MemoryStream(gzipBytes), CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    internal sealed class FakeTargetIndexSource(CatalogIndexBuildResult current) : ICatalogTargetIndexSource
    {
        public CatalogIndexBuildResult? Current { get; set; } = current;

        public Task<CatalogIndexBuildResult?> GetCurrentIndexAsync(string storeCode, CancellationToken cancellationToken) =>
            Task.FromResult(Current);
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public void Advance(TimeSpan by) => _utcNow += by;

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }
}
