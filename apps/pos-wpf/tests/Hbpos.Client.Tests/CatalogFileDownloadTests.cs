using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.Common;

namespace Hbpos.Client.Tests;

/// <summary>
/// 整文件目录下载：API 客户端的 Range 请求、下载器的续传与整份校验、同步服务的文件模式与回退分页。
/// 文件格式与服务端 CatalogDownloadFileStore 相同（gzip NDJSON，头一行 + 每行一条）。
/// </summary>
public sealed class CatalogFileDownloadTests : IDisposable
{
    private const string Store = "1014";
    private static readonly DateTimeOffset Timestamp = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private readonly string _cacheDirectory = Path.Combine(Path.GetTempPath(), "hbpos-catalog-file-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_cacheDirectory))
        {
            Directory.Delete(_cacheDirectory, recursive: true);
        }
    }

    // ---------- API 客户端：Range 请求 ----------

    [Fact]
    public async Task Range_download_without_offset_writes_whole_body_and_sends_no_range()
    {
        var body = Enumerable.Range(0, 300_000).Select(index => (byte)index).ToArray();
        HttpRequestMessage? captured = null;
        var client = CreateApiClient(request =>
        {
            captured = request;
            return Binary(HttpStatusCode.OK, body);
        });
        using var destination = new MemoryStream();

        var result = await client.DownloadCatalogFileRangeAsync("api/v1/catalog/files/full?storeCode=1014&catalogVersion=v1", destination, 0, "\"abc\"", null);

        Assert.Equal("/api/v1/catalog/files/full?storeCode=1014&catalogVersion=v1", captured!.RequestUri!.PathAndQuery);
        Assert.Null(captured.Headers.Range);
        Assert.Null(captured.Headers.IfRange);
        Assert.Equal((true, body.LongLength), (result.Restarted, result.BytesWritten));
        Assert.Equal(body, destination.ToArray());
    }

    [Fact]
    public async Task Range_download_resumes_with_if_range_and_appends_partial_content()
    {
        var body = Enumerable.Range(0, 1000).Select(index => (byte)index).ToArray();
        HttpRequestMessage? captured = null;
        var client = CreateApiClient(request =>
        {
            captured = request;
            var response = Binary(HttpStatusCode.PartialContent, body[400..]);
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(400, 999, 1000);
            return response;
        });
        using var destination = new MemoryStream();
        destination.Write(body, 0, 400);

        var result = await client.DownloadCatalogFileRangeAsync("api/v1/catalog/files/full", destination, 400, "\"abc\"", null);

        Assert.Equal(400, captured!.Headers.Range!.Ranges.Single().From);
        Assert.Equal("\"abc\"", captured.Headers.IfRange!.EntityTag!.Tag);
        Assert.Equal((false, 600L), (result.Restarted, result.BytesWritten));
        Assert.Equal(body, destination.ToArray());
    }

    [Fact]
    public async Task Range_download_rewrites_from_zero_when_server_returns_whole_content()
    {
        var body = new byte[] { 9, 8, 7, 6 };
        var client = CreateApiClient(_ => Binary(HttpStatusCode.OK, body));
        using var destination = new MemoryStream();
        destination.Write([1, 1, 1, 1, 1, 1]);

        var result = await client.DownloadCatalogFileRangeAsync("api/v1/catalog/files/full", destination, 6, "\"old\"", null);

        Assert.True(result.Restarted);
        Assert.Equal(body, destination.ToArray());
    }

    [Fact]
    public async Task Range_download_rejects_partial_content_starting_elsewhere()
    {
        var client = CreateApiClient(_ =>
        {
            var response = Binary(HttpStatusCode.PartialContent, [1, 2]);
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 1, 2);
            return response;
        });
        using var destination = new MemoryStream(new byte[10]);

        var exception = await Assert.ThrowsAsync<CatalogApiException>(() =>
            client.DownloadCatalogFileRangeAsync("api/v1/catalog/files/full", destination, 10, "\"abc\"", null));

        Assert.Equal("CATALOG_FILE_RANGE_INVALID", exception.ErrorCode);
    }

    [Fact]
    public async Task Range_download_surfaces_business_error_code()
    {
        var client = CreateApiClient(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(ApiResult<object>.Fail("CATALOG_FILE_NOT_FOUND", "gone"), WebJson),
                Encoding.UTF8,
                "application/json")
        });

        var exception = await Assert.ThrowsAsync<CatalogApiException>(() =>
            client.DownloadCatalogFileRangeAsync("api/v1/catalog/files/full", new MemoryStream(), 0, null, null));

        Assert.Equal((HttpStatusCode.NotFound, "CATALOG_FILE_NOT_FOUND"), (exception.StatusCode, exception.ErrorCode));
    }

    [Fact]
    public async Task File_sync_plan_request_uses_files_endpoint()
    {
        var requests = new List<Uri>();
        var plan = new CatalogFileSyncPlanResponse(Store, Timestamp, CatalogSyncModes.Full, null, "v1", 3,
            new CatalogDownloadFileDto(CatalogFileKinds.Full, CatalogFileFormats.FullV1, "api/v1/catalog/files/full?x=1", 10, new string('a', 64)));
        var client = CreateApiClient(request =>
        {
            requests.Add(request.RequestUri!);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(ApiResult<CatalogFileSyncPlanResponse>.Ok(plan), WebJson), Encoding.UTF8, "application/json")
            };
        });

        var result = await client.GetCatalogFileSyncPlanAsync(Store, "v0");

        Assert.Equal("/api/v1/catalog/files/sync-plan?storeCode=1014&baseCatalogVersion=v0", Assert.Single(requests).PathAndQuery);
        Assert.Equal(plan.File, result.File);
    }

    // ---------- 下载器：续传与校验 ----------

    [Fact]
    public async Task Downloader_resumes_from_partial_bytes_after_a_dropped_connection()
    {
        var bytes = FullFile("v1", Items(2000));
        var api = new FileCatalogApiClient();
        api.AddFile("files/full", bytes);
        api.FailAfterBytes.Enqueue(bytes.Length / 3);
        var delays = new List<TimeSpan>();
        var downloader = CreateDownloader(api, delays);
        var reported = new List<long>();

        var path = await downloader.DownloadAsync(Descriptor("files/full", bytes), new ListProgress(reported), CancellationToken.None);

        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.Equal([0L, bytes.Length / 3], api.RangeRequests.Select(request => request.Offset));
        Assert.Equal($"\"{Sha256(bytes)}\"", api.RangeRequests[1].IfRange);
        Assert.Equal([CatalogFileDownloader.RetryDelays[0]], delays);
        Assert.Equal(bytes.Length, reported[^1]);
        Assert.Equal([Path.GetFileName(path)], Directory.EnumerateFiles(_cacheDirectory).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Downloader_continues_a_part_left_by_a_previous_run()
    {
        var bytes = FullFile("v1", Items(500));
        var api = new FileCatalogApiClient();
        api.AddFile("files/full", bytes);
        Directory.CreateDirectory(_cacheDirectory);
        await File.WriteAllBytesAsync(Path.Combine(_cacheDirectory, Sha256(bytes) + ".part"), bytes[..100]);

        var path = await CreateDownloader(api).DownloadAsync(Descriptor("files/full", bytes), null, CancellationToken.None);

        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.Equal(100, Assert.Single(api.RangeRequests).Offset);
    }

    [Fact]
    public async Task Downloader_restarts_once_when_whole_file_checksum_fails()
    {
        var bytes = FullFile("v1", Items(50));
        var api = new FileCatalogApiClient();
        api.AddFile("files/full", bytes);
        api.CorruptNextDownloads = 1;

        var path = await CreateDownloader(api).DownloadAsync(Descriptor("files/full", bytes), null, CancellationToken.None);

        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.Equal([0L, 0L], api.RangeRequests.Select(request => request.Offset));
    }

    [Fact]
    public async Task Downloader_gives_up_when_checksum_fails_twice_and_leaves_no_file()
    {
        var bytes = FullFile("v1", Items(50));
        var api = new FileCatalogApiClient();
        api.AddFile("files/full", bytes);
        api.CorruptNextDownloads = 2;

        var exception = await Assert.ThrowsAsync<CatalogApiException>(() =>
            CreateDownloader(api).DownloadAsync(Descriptor("files/full", bytes), null, CancellationToken.None));

        Assert.Equal("CATALOG_FILE_CHECKSUM_MISMATCH", exception.ErrorCode);
        Assert.Empty(Directory.EnumerateFiles(_cacheDirectory));
    }

    [Fact]
    public async Task Downloader_does_not_retry_business_errors()
    {
        var api = new FileCatalogApiClient
        {
            DownloadException = new CatalogApiException("gone", HttpStatusCode.NotFound, "CATALOG_FILE_NOT_FOUND")
        };
        var delays = new List<TimeSpan>();

        var exception = await Assert.ThrowsAsync<CatalogApiException>(() =>
            CreateDownloader(api, delays).DownloadAsync(Descriptor("files/full", [1, 2, 3]), null, CancellationToken.None));

        Assert.Equal("CATALOG_FILE_NOT_FOUND", exception.ErrorCode);
        Assert.Single(api.RangeRequests);
        Assert.Empty(delays);
    }

    [Fact]
    public async Task Downloader_stops_after_retry_budget_is_spent()
    {
        var bytes = FullFile("v1", Items(10));
        var api = new FileCatalogApiClient();
        api.AddFile("files/full", bytes);
        for (var attempt = 0; attempt < 10; attempt++)
        {
            api.FailAfterBytes.Enqueue(0);
        }

        var delays = new List<TimeSpan>();

        await Assert.ThrowsAsync<IOException>(() =>
            CreateDownloader(api, delays).DownloadAsync(Descriptor("files/full", bytes), null, CancellationToken.None));

        Assert.Equal(CatalogFileDownloader.RetryDelays, delays);
        Assert.Equal(CatalogFileDownloader.RetryDelays.Length + 1, api.RangeRequests.Count);
    }

    [Fact]
    public async Task Downloader_reuses_verified_file_and_clears_other_versions()
    {
        var bytes = FullFile("v1", Items(10));
        var api = new FileCatalogApiClient();
        api.AddFile("files/full", bytes);
        Directory.CreateDirectory(_cacheDirectory);
        var stale = Path.Combine(_cacheDirectory, new string('b', 64) + ".part");
        await File.WriteAllBytesAsync(stale, [1, 2, 3]);
        await File.WriteAllBytesAsync(Path.Combine(_cacheDirectory, Sha256(bytes) + ".ndjson.gz"), bytes);

        var path = await CreateDownloader(api).DownloadAsync(Descriptor("files/full", bytes), null, CancellationToken.None);

        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.Empty(api.RangeRequests);
        Assert.False(File.Exists(stale));
    }

    [Theory]
    [InlineData("not-a-hash")]
    [InlineData("../../../../etc/passwd/0000000000000000000000000000000000000000000000")]
    public async Task Downloader_rejects_invalid_hash_before_touching_disk(string sha256)
    {
        var api = new FileCatalogApiClient();
        var descriptor = new CatalogDownloadFileDto(CatalogFileKinds.Full, CatalogFileFormats.FullV1, "files/full", 3, sha256);

        var exception = await Assert.ThrowsAsync<CatalogApiException>(() =>
            CreateDownloader(api).DownloadAsync(descriptor, null, CancellationToken.None));

        Assert.Equal("CATALOG_FILE_INVALID", exception.ErrorCode);
        Assert.Empty(api.RangeRequests);
    }

    // ---------- 同步服务：文件模式与回退 ----------

    [Fact]
    public async Task Full_file_plan_imports_in_batches_and_commits_the_version_without_paging()
    {
        var items = Items(4500);
        var bytes = FullFile("v2", items);
        var api = new FileCatalogApiClient();
        api.AddFile("files/full", bytes);
        api.FilePlans.Enqueue(FilePlan(CatalogSyncModes.Full, null, "v2", items.Length, Descriptor("files/full", bytes)));
        var repository = new FileVersionedRepository();
        var progress = new List<CatalogSyncProgress>();

        var result = await CreateService(repository, api).FullSyncAsync(Store, progress: new CapturingProgress(progress));

        Assert.Equal([(Store, (string?)null)], api.FilePlanRequests);
        Assert.Empty(api.PagingPlanRequests);
        Assert.Equal([2000, 2000, 500], repository.StagedBatchSizes);
        Assert.Equal(("v2", 4500), repository.CommittedStamp);
        Assert.Equal(items.Select(item => item.LookupCode), repository.Items.Select(item => item.LookupCode));
        Assert.Equal((CatalogSyncModes.Full, true), (result.SyncMode, result.CatalogChanged));
        Assert.Equal(1, api.CodeConflictRequestCount);
        // 已写入本地库的文件随即删除。
        Assert.Empty(Directory.EnumerateFiles(_cacheDirectory));
        var downloaded = progress.Select(report => report.DownloadedCount).ToArray();
        Assert.Equal(downloaded.OrderBy(value => value), downloaded);
        Assert.Equal((CatalogSyncProgressStage.Completed, 100), (progress[^1].Stage, progress[^1].Percent));
    }

    [Fact]
    public async Task Delta_file_plan_applies_operations_in_one_versioned_call()
    {
        var bytes = DeltaFile("v1", "v2", 10, [Lookup("P-NEW", "9300000000001", 4.5m)], [Deleted("OLD-CODE")]);
        var api = new FileCatalogApiClient();
        api.AddFile("files/delta", bytes);
        api.FilePlans.Enqueue(FilePlan(CatalogSyncModes.Delta, "v1", "v2", 10, Descriptor("files/delta", bytes, CatalogFileKinds.Delta), deltaOperations: 2));
        var repository = new FileVersionedRepository { CatalogVersion = "v1" };

        var result = await CreateService(repository, api).FullSyncAsync(Store);

        var applied = Assert.Single(repository.DeltaApplyCalls);
        Assert.Equal(("v1", "v2"), (applied.Base, applied.Target));
        Assert.Equal(["P-NEW"], applied.Upserts.Select(item => item.ProductCode));
        Assert.Equal(["OLD-CODE"], applied.Deletes);
        Assert.Equal((CatalogSyncModes.Delta, 1, 1), (result.SyncMode, result.UpsertedCount, result.DeletedCount));
        Assert.Empty(api.PagingPlanRequests);
    }

    [Fact]
    public async Task No_change_file_plan_downloads_nothing()
    {
        var api = new FileCatalogApiClient();
        api.FilePlans.Enqueue(FilePlan(CatalogSyncModes.NoChange, "v1", "v1", 10, file: null));
        var repository = new FileVersionedRepository { CatalogVersion = "v1" };

        var result = await CreateService(repository, api).FullSyncAsync(Store);

        Assert.Equal((CatalogSyncModes.NoChange, false), (result.SyncMode, result.CatalogChanged));
        Assert.Empty(api.RangeRequests);
        Assert.Empty(api.PagingPlanRequests);
        Assert.Equal(1, api.CodeConflictRequestCount);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("old-server")]
    [InlineData("busy")]
    [InlineData("download-failed")]
    [InlineData("header-mismatch")]
    [InlineData("truncated-file")]
    [InlineData("delta-base-changed")]
    [InlineData("plan-without-file")]
    public async Task File_mode_problems_fall_back_to_paging_and_keep_local_catalog(string failure)
    {
        var items = Items(3);
        var api = new FileCatalogApiClient();
        var repository = new FileVersionedRepository { CatalogVersion = "v1" };
        repository.Seed(Lookup("P-OLD", "OLD", 1m).ToSellableItemDto());
        switch (failure)
        {
            case "disabled":
                api.FilePlanException = new CatalogApiException("disabled", HttpStatusCode.NotFound, "CATALOG_FILE_DOWNLOAD_DISABLED");
                break;
            case "old-server":
                api.FilePlanException = new CatalogApiException("route missing", HttpStatusCode.NotFound);
                break;
            case "busy":
                api.FilePlanException = new CatalogApiException("busy", HttpStatusCode.ServiceUnavailable, "CATALOG_FILE_UNAVAILABLE");
                break;
            case "download-failed":
                api.FilePlans.Enqueue(FilePlan(CatalogSyncModes.Full, "v1", "v2", 3, Descriptor("files/full", FullFile("v2", items))));
                api.DownloadException = new CatalogApiException("gone", HttpStatusCode.NotFound, "CATALOG_FILE_NOT_FOUND");
                break;
            case "header-mismatch":
                var otherVersion = FullFile("v-other", items);
                api.AddFile("files/full", otherVersion);
                api.FilePlans.Enqueue(FilePlan(CatalogSyncModes.Full, "v1", "v2", 3, Descriptor("files/full", otherVersion)));
                break;
            case "truncated-file":
                // 头声明 3 条但只有 2 行：哈希对得上（服务端就是这么写的）也不能当成完整版本。
                var truncated = FullFile("v2", items, headerTotal: 3, take: 2);
                api.AddFile("files/full", truncated);
                api.FilePlans.Enqueue(FilePlan(CatalogSyncModes.Full, "v1", "v2", 3, Descriptor("files/full", truncated)));
                break;
            case "delta-base-changed":
                var delta = DeltaFile("v1", "v2", 3, [Lookup("P-A", "A", 1m)], []);
                api.AddFile("files/delta", delta);
                api.FilePlans.Enqueue(FilePlan(CatalogSyncModes.Delta, "v1", "v2", 3, Descriptor("files/delta", delta, CatalogFileKinds.Delta), deltaOperations: 1));
                repository.DeltaApplyException = new LocalCatalogVersionConflictException("base changed");
                break;
            case "plan-without-file":
                api.FilePlans.Enqueue(FilePlan(CatalogSyncModes.Full, "v1", "v2", 3, file: null));
                break;
        }

        // 分页协议接手：无变化计划，本地目录与版本原样保留。
        api.PagingPlans.Enqueue(new CatalogSyncPlanResponse(Store, Timestamp, CatalogSyncModes.NoChange, "v1", "v1", 1));

        var result = await CreateService(repository, api).FullSyncAsync(Store);

        Assert.Equal([(Store, (string?)"v1")], api.PagingPlanRequests);
        Assert.Equal(CatalogSyncModes.NoChange, result.SyncMode);
        Assert.Null(repository.CommittedStamp);
        Assert.Equal("v1", repository.CatalogVersion);
        Assert.Equal(["OLD"], repository.Items.Select(item => item.LookupCode));
    }

    [Fact]
    public async Task Service_without_downloader_never_asks_for_a_file_plan()
    {
        var api = new FileCatalogApiClient();
        api.PagingPlans.Enqueue(new CatalogSyncPlanResponse(Store, Timestamp, CatalogSyncModes.NoChange, "v1", "v1", 1));
        var repository = new FileVersionedRepository { CatalogVersion = "v1" };

        await new LocalCatalogSyncService(repository, api).FullSyncAsync(Store);

        Assert.Empty(api.FilePlanRequests);
        Assert.Single(api.PagingPlanRequests);
    }

    [Fact]
    public async Task Cancellation_during_file_download_is_not_turned_into_a_paging_fallback()
    {
        var bytes = FullFile("v2", Items(3));
        var api = new FileCatalogApiClient();
        api.AddFile("files/full", bytes);
        api.FilePlans.Enqueue(FilePlan(CatalogSyncModes.Full, null, "v2", 3, Descriptor("files/full", bytes)));
        using var cts = new CancellationTokenSource();
        api.BeforeDownload = cts.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateService(new FileVersionedRepository(), api).FullSyncAsync(Store, cts.Token));

        Assert.Empty(api.PagingPlanRequests);
    }

    // ---------- 辅助 ----------

    private LocalCatalogSyncService CreateService(FileVersionedRepository repository, FileCatalogApiClient api) =>
        new(repository, api, catalogFileDownloader: CreateDownloader(api));

    private CatalogFileDownloader CreateDownloader(FileCatalogApiClient api, List<TimeSpan>? delays = null) =>
        new(api, _cacheDirectory, (delay, _) =>
        {
            delays?.Add(delay);
            return Task.CompletedTask;
        });

    private static CatalogApiClient CreateApiClient(Func<HttpRequestMessage, HttpResponseMessage> handler) =>
        new(new HttpClient(new StubHttpMessageHandler(handler)) { BaseAddress = new Uri("http://pos.test/") }, (_, _) => Task.CompletedTask);

    private static HttpResponseMessage Binary(HttpStatusCode status, byte[] body)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue(CatalogFileFormats.ContentType);
        return new HttpResponseMessage(status) { Content = content };
    }

    private static CatalogFileSyncPlanResponse FilePlan(
        string mode,
        string? baseVersion,
        string target,
        int total,
        CatalogDownloadFileDto? file,
        int? deltaOperations = null) =>
        new(Store, Timestamp, mode, baseVersion, target, total, file, deltaOperations);

    private static CatalogDownloadFileDto Descriptor(string path, byte[] bytes, string kind = CatalogFileKinds.Full) =>
        new(kind, kind == CatalogFileKinds.Delta ? CatalogFileFormats.DeltaV1 : CatalogFileFormats.FullV1, path, bytes.Length, Sha256(bytes));

    private static byte[] FullFile(string version, CatalogLookupItemDto[] items, int? headerTotal = null, int? take = null) =>
        Gzip([
            new CatalogFullFileHeader(CatalogFileFormats.FullV1, Store, version, Timestamp, headerTotal ?? items.Length),
            .. items.Take(take ?? items.Length)
        ]);

    private static byte[] DeltaFile(
        string baseVersion,
        string targetVersion,
        int targetTotal,
        CatalogLookupItemDto[] upserts,
        DeletedLookupDto[] deletes) =>
        Gzip([
            new CatalogDeltaFileHeader(CatalogFileFormats.DeltaV1, Store, baseVersion, targetVersion, Timestamp, targetTotal, upserts.Length + deletes.Length),
            .. upserts.Select(item => new CatalogDeltaFileLine(item, null)),
            .. deletes.Select(deleted => new CatalogDeltaFileLine(null, deleted))
        ]);

    private static byte[] Gzip(IEnumerable<object> lines)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            foreach (var line in lines)
            {
                gzip.Write(JsonSerializer.SerializeToUtf8Bytes(line, line.GetType(), WebJson));
                gzip.WriteByte((byte)'\n');
            }
        }

        return output.ToArray();
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static CatalogLookupItemDto[] Items(int count) =>
        Enumerable.Range(1, count).Select(index => Lookup($"P-{index:D6}", $"93{index:D10}", index / 100m)).ToArray();

    private static CatalogLookupItemDto Lookup(string productCode, string lookupCode, decimal price) =>
        new(Store, productCode, null, "商品 " + productCode, lookupCode, lookupCode.ToUpperInvariant(), productCode, lookupCode,
            price, PriceSourceKind.StoreRetailPrice, "store-retail", 1m, Timestamp, "row-" + lookupCode);

    private static DeletedLookupDto Deleted(string lookupCode) =>
        new(Store, lookupCode, lookupCode.ToUpperInvariant(), Timestamp);

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }

    private sealed class ListProgress(List<long> values) : IProgress<long>
    {
        public void Report(long value) => values.Add(value);
    }

    private sealed class CapturingProgress(List<CatalogSyncProgress> reports) : IProgress<CatalogSyncProgress>
    {
        public void Report(CatalogSyncProgress value) => reports.Add(value);
    }

    /// <summary>按 Range 语义从内存提供文件；可注入断流、内容损坏、业务错误。</summary>
    private sealed class FileCatalogApiClient : ICatalogApiClient
    {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);

        public Queue<CatalogFileSyncPlanResponse> FilePlans { get; } = new();

        public Exception? FilePlanException { get; set; }

        public List<(string StoreCode, string? Base)> FilePlanRequests { get; } = [];

        public Queue<CatalogSyncPlanResponse> PagingPlans { get; } = new();

        public List<(string StoreCode, string? Base)> PagingPlanRequests { get; } = [];

        public List<(string Path, long Offset, string? IfRange)> RangeRequests { get; } = [];

        public Queue<int> FailAfterBytes { get; } = new();

        public int CorruptNextDownloads { get; set; }

        public Exception? DownloadException { get; set; }

        public Action? BeforeDownload { get; set; }

        public int CodeConflictRequestCount { get; private set; }

        public void AddFile(string path, byte[] bytes) => _files[path] = bytes;

        public Task<CatalogFileSyncPlanResponse> GetCatalogFileSyncPlanAsync(
            string storeCode,
            string? baseCatalogVersion,
            CancellationToken cancellationToken = default)
        {
            FilePlanRequests.Add((storeCode, baseCatalogVersion));
            return FilePlanException is not null
                ? Task.FromException<CatalogFileSyncPlanResponse>(FilePlanException)
                : Task.FromResult(FilePlans.Dequeue());
        }

        public async Task<CatalogFileRangeResult> DownloadCatalogFileRangeAsync(
            string relativePath,
            Stream destination,
            long offset,
            string? ifRangeEntityTag,
            IProgress<long>? bytesWritten,
            CancellationToken cancellationToken = default)
        {
            RangeRequests.Add((relativePath, offset, ifRangeEntityTag));
            BeforeDownload?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            if (DownloadException is not null)
            {
                throw DownloadException;
            }

            var bytes = _files[relativePath];
            if (CorruptNextDownloads > 0)
            {
                CorruptNextDownloads--;
                bytes = bytes.ToArray();
                bytes[^1] ^= 0xFF;
            }

            var segment = bytes[(int)offset..];
            if (FailAfterBytes.TryDequeue(out var failAfter))
            {
                await destination.WriteAsync(segment.AsMemory(0, Math.Min(failAfter, segment.Length)), cancellationToken);
                throw new IOException("connection reset");
            }

            await destination.WriteAsync(segment, cancellationToken);
            bytesWritten?.Report(segment.Length);
            return new CatalogFileRangeResult(offset == 0, segment.Length);
        }

        public Task<CatalogSyncPlanResponse> GetCatalogSyncPlanAsync(
            string storeCode,
            string? baseCatalogVersion,
            CancellationToken cancellationToken = default)
        {
            PagingPlanRequests.Add((storeCode, baseCatalogVersion));
            return Task.FromResult(PagingPlans.Dequeue());
        }

        public Task<CatalogCodeConflictsResponse> GetCodeConflictsAsync(string storeCode, CancellationToken cancellationToken = default)
        {
            CodeConflictRequestCount++;
            return Task.FromResult(new CatalogCodeConflictsResponse(storeCode, Timestamp, false, []));
        }

        public Task<CatalogPromotionsResponse> GetPromotionRulesAsync(string storeCode, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CatalogPromotionsResponse(storeCode, Timestamp, []));

        public Task<CatalogSyncPageResponse> GetSellableItemsPageAsync(string storeCode, string? cursor, int pageSize, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CatalogCompareResponse> CompareSellableItemsAsync(CatalogCompareRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CatalogSpecialProductsPageResponse> GetSpecialProductsPageAsync(string storeCode, string? cursor, int pageSize, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CatalogLookupResponse?> LookupSellableItemAsync(string storeCode, string lookupCode, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CatalogSpecialProductMarkResponse> MarkSpecialProductAsync(CatalogSpecialProductMarkRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FileVersionedRepository : ILocalCatalogRepository
    {
        private readonly List<SellableItemDto> _items = [];

        public string? CatalogVersion { get; set; }

        public IReadOnlyList<SellableItemDto> Items => _items;

        public List<int> StagedBatchSizes { get; } = [];

        public (string Version, int ExpectedCount)? CommittedStamp { get; private set; }

        public Exception? DeltaApplyException { get; set; }

        public List<(string Base, string Target, IReadOnlyList<SellableItemDto> Upserts, IReadOnlyList<string> Deletes)> DeltaApplyCalls { get; } = [];

        public void Seed(params SellableItemDto[] items) => _items.AddRange(items);

        public Task<string?> GetCatalogVersionAsync(string storeCode, CancellationToken cancellationToken = default) =>
            Task.FromResult(CatalogVersion);

        public Task<LocalCatalogDeltaApplyResult> ApplyCatalogDeltaAsync(
            string storeCode,
            string baseCatalogVersion,
            string targetCatalogVersion,
            IReadOnlyList<SellableItemDto> upsertedItems,
            IReadOnlyList<string> deletedLookupCodes,
            CancellationToken cancellationToken = default)
        {
            DeltaApplyCalls.Add((baseCatalogVersion, targetCatalogVersion, upsertedItems, deletedLookupCodes));
            if (DeltaApplyException is not null)
            {
                return Task.FromException<LocalCatalogDeltaApplyResult>(DeltaApplyException);
            }

            CatalogVersion = targetCatalogVersion;
            return Task.FromResult(new LocalCatalogDeltaApplyResult(upsertedItems.Count, deletedLookupCodes.Count, _items.Count));
        }

        public Task<ILocalCatalogStoreReplaceSession> BeginStoreReplaceSessionAsync(string storeCode, CancellationToken cancellationToken = default) =>
            Task.FromResult<ILocalCatalogStoreReplaceSession>(new Session(this));

        public Task<bool> ReplaceCodeConflictItemsIfChangedAsync(string storeCode, IEnumerable<SellableItemDto> items, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task ReplacePromotionRulesAsync(string storeCode, IEnumerable<CatalogPromotionRuleDto> rules, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<LocalSellableItemCompareRow>> LoadSellableItemComparePageAsync(string storeCode, string? afterLookupCodeNormalized, int pageSize, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task ReplaceSellableItemsAsync(IEnumerable<SellableItemDto> items, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task UpsertSellableItemsAsync(IEnumerable<SellableItemDto> items, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<int> DeleteByLookupCodesAsync(string storeCode, IEnumerable<string> lookupCodes, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SellableItemDto?> FindByLookupCodeAsync(string storeCode, string lookupCode, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<SellableItemDto>> LoadSpecialProductItemsAsync(string storeCode, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task SaveSpecialProductOrderAsync(string storeCode, IEnumerable<string> productCodes, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<int> UpdateSpecialProductFlagAsync(string storeCode, string productCode, bool isSpecialProduct, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<int> ClearSpecialProductFlagsExceptAsync(string storeCode, IEnumerable<string> productCodesToKeep, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<SellableItemDto>> LoadSellableItemsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SellableItemDto>>(_items.ToArray());

        public Task<IReadOnlyList<SellableItemDto>> LoadSellableItemsAsync(string storeCode, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SellableItemDto>>(_items.ToArray());

        private sealed class Session(FileVersionedRepository repository) : ILocalCatalogStoreReplaceSession
        {
            private readonly List<SellableItemDto> _staged = [];

            public Task StageAsync(IEnumerable<SellableItemDto> items, CancellationToken cancellationToken = default)
            {
                var batch = items.ToArray();
                repository.StagedBatchSizes.Add(batch.Length);
                _staged.AddRange(batch);
                return Task.CompletedTask;
            }

            public Task<LocalCatalogStoreReplaceCommitResult> CommitAsync(CancellationToken cancellationToken = default) =>
                throw new InvalidOperationException("File sync must commit with a version stamp.");

            public Task<LocalCatalogStoreReplaceCommitResult> CommitAsync(LocalCatalogVersionStamp versionStamp, CancellationToken cancellationToken = default)
            {
                if (_staged.Count != versionStamp.ExpectedItemCount)
                {
                    throw new LocalCatalogVersionConflictException("count mismatch");
                }

                var deleted = repository._items.Count;
                repository._items.Clear();
                repository._items.AddRange(_staged);
                repository.CatalogVersion = versionStamp.CatalogVersion;
                repository.CommittedStamp = (versionStamp.CatalogVersion, versionStamp.ExpectedItemCount);
                return Task.FromResult(new LocalCatalogStoreReplaceCommitResult(_staged.Count, deleted));
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
