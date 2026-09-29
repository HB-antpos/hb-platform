using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Cashiers;
using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.Common;
using Hbpos.Contracts.Health;
using Hbpos.Contracts.Devices;
using Hbpos.Contracts.Promotions;

namespace Hbpos.Client.Tests;

public sealed class CatalogSyncServiceTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public async Task FullSyncAsync_WhenCompareFails_DoesNotDeleteLocalCatalog()
    {
        var repository = new FakeLocalCatalogRepository();
        repository.ComparePages.Enqueue(
        [
            new LocalSellableItemCompareRow("S01", "LOCAL-CODE", "local-hash", Timestamp)
        ]);
        var apiClient = new FakeCatalogApiClient
        {
            CompareException = new CatalogApiException("remote compare failed")
        };
        var service = new LocalCatalogSyncService(repository, apiClient);
        var progressReports = new List<CatalogSyncProgress>();
        var progress = new CapturingProgress<CatalogSyncProgress>(progressReports);

        await Assert.ThrowsAsync<CatalogApiException>(() => service.FullSyncAsync("S01", progress: progress));

        Assert.Empty(repository.UpsertedBatches);
        Assert.Empty(repository.DeleteCalls);
        Assert.Contains(progressReports, report => report.Stage == CatalogSyncProgressStage.Failed);
        Assert.DoesNotContain(progressReports, report => report.Stage == CatalogSyncProgressStage.Completed);
    }

    [Fact]
    public async Task RefreshLookupAsync_WhenRemoteReturnsNotFound_DeletesOnlyThatLookup()
    {
        var repository = new FakeLocalCatalogRepository();
        var apiClient = new FakeCatalogApiClient
        {
            LookupResponse = new CatalogLookupResponse("S01", " abc ", "ABC", Found: false, Item: null)
        };
        var service = new RemoteLookupRefreshService(repository, apiClient);

        var result = await service.RefreshLookupAsync("S01", " abc ");

        Assert.False(result.Found);
        Assert.True(result.Deleted);
        Assert.Empty(repository.UpsertedBatches);
        var deleteCall = Assert.Single(repository.DeleteCalls);
        Assert.Equal("S01", deleteCall.StoreCode);
        Assert.Equal(["ABC"], deleteCall.LookupCodes);
    }

    [Fact]
    public async Task RefreshLookupAsync_WhenRemoteFails_DoesNotDeleteLocalCatalog()
    {
        var repository = new FakeLocalCatalogRepository();
        var apiClient = new FakeCatalogApiClient
        {
            LookupException = new CatalogApiException("store route missing", HttpStatusCode.NotFound, "STORE_NOT_FOUND")
        };
        var service = new RemoteLookupRefreshService(repository, apiClient);

        await Assert.ThrowsAsync<CatalogApiException>(() => service.RefreshLookupAsync("S01", "abc"));

        Assert.Empty(repository.UpsertedBatches);
        Assert.Empty(repository.DeleteCalls);
    }

    [Fact]
    public async Task RefreshLookupAsync_WhenRemoteLookupIsCanceled_DoesNotDeleteLocalCatalog()
    {
        var repository = new FakeLocalCatalogRepository();
        var apiClient = new FakeCatalogApiClient
        {
            LookupException = new OperationCanceledException("lookup timed out")
        };
        var service = new RemoteLookupRefreshService(repository, apiClient);

        await Assert.ThrowsAsync<OperationCanceledException>(() => service.RefreshLookupAsync("S01", "abc"));

        Assert.Empty(repository.UpsertedBatches);
        Assert.Empty(repository.DeleteCalls);
    }

    [Fact]
    public async Task FullSyncAsync_AppliesCompareAndRemotePageUpsertsAndDeletes()
    {
        var repository = new FakeLocalCatalogRepository();
        repository.ComparePages.Enqueue(
        [
            new LocalSellableItemCompareRow("S01", "LOCAL-CODE", "local-hash", Timestamp)
        ]);
        repository.ComparePages.Enqueue([]);

        var apiClient = new FakeCatalogApiClient();
        apiClient.CompareResponses.Enqueue(new CatalogCompareResponse(
            "S01",
            Timestamp,
            [CreateLookupItem("CMP-001", "cmp-code", "CMP-REF-001")],
            [CreateDeletedLookup("old-code")],
            NextCursor: null,
            HasMore: false));
        apiClient.PageResponses.Enqueue(new CatalogSyncPageResponse(
            "S01",
            Timestamp,
            Cursor: null,
            [CreateLookupItem("PAGE-001", "page-code", "PAGE-REF-001")],
            [CreateDeletedLookup("gone-code")],
            NextCursor: null,
            HasMore: false,
            TotalCount: 1));
        var service = new LocalCatalogSyncService(repository, apiClient);

        var result = await service.FullSyncAsync("S01");

        Assert.Equal(new LocalCatalogSyncResult("S01", ComparePages: 1, RemotePages: 1, UpsertedCount: 2, DeletedCount: 2), result);
        Assert.Equal(2, repository.UpsertedBatches.Count);
        var compareUpsert = Assert.Single(repository.UpsertedBatches[0]);
        var pageUpsert = Assert.Single(repository.UpsertedBatches[1]);
        Assert.Equal("CMP-001", compareUpsert.ProductCode);
        Assert.Equal("CMP-REF-001", compareUpsert.ReferenceCode);
        Assert.Equal("https://images.example/CMP-001.jpg", compareUpsert.ProductImage);
        Assert.Equal(0.2m, compareUpsert.DiscountRate);
        Assert.Equal("PAGE-001", pageUpsert.ProductCode);
        Assert.Equal("PAGE-REF-001", pageUpsert.ReferenceCode);
        Assert.Equal("https://images.example/PAGE-001.jpg", pageUpsert.ProductImage);
        Assert.Equal(0.2m, pageUpsert.DiscountRate);
        Assert.Equal(["OLD-CODE"], repository.DeleteCalls[0].LookupCodes);
        Assert.Equal(["GONE-CODE"], repository.DeleteCalls[1].LookupCodes);
        Assert.Equal(
            [("S01", null, 2000), ("S01", "LOCAL-CODE", 2000)],
            repository.ComparePageRequests);
        Assert.NotNull(apiClient.LastCompareRequest);
        var localVersion = Assert.Single(apiClient.LastCompareRequest.LocalLookups);
        Assert.Equal("LOCAL-CODE", localVersion.LookupCodeNormalized);
        Assert.Equal("local-hash", localVersion.RowVersion);
    }

    [Fact]
    public async Task FullSyncAsync_ReplacesPromotionRulesAfterCatalogDownload()
    {
        var repository = new FakeLocalCatalogRepository();
        repository.ComparePages.Enqueue([]);
        var apiClient = new FakeCatalogApiClient();
        apiClient.PageResponses.Enqueue(new CatalogSyncPageResponse(
            "S01",
            Timestamp,
            Cursor: null,
            [],
            [],
            NextCursor: null,
            HasMore: false,
            TotalCount: 0));
        apiClient.PromotionResponses.Enqueue(new CatalogPromotionsResponse(
            "S01",
            Timestamp,
            [CreatePromotionRule("PROMO-001", "SKU-001")]));
        var service = new LocalCatalogSyncService(repository, apiClient);

        await service.FullSyncAsync("S01");

        Assert.Equal(["S01"], apiClient.PromotionRequests);
        var replaceCall = Assert.Single(repository.PromotionReplaceCalls);
        Assert.Equal("S01", replaceCall.StoreCode);
        var rule = Assert.Single(replaceCall.Rules);
        Assert.Equal("PROMO-001", rule.PromotionId);
        Assert.Equal("SKU-001", Assert.Single(rule.Products).ProductCode);
    }

    [Fact]
    public async Task FullSyncAsync_ReplacesCodeConflictsAfterCatalogDownload()
    {
        var repository = new FakeLocalCatalogRepository();
        repository.ComparePages.Enqueue([]);
        var apiClient = new FakeCatalogApiClient();
        apiClient.PageResponses.Enqueue(EmptyPage("S01"));
        apiClient.CodeConflictResponses.Enqueue(new CatalogCodeConflictsResponse(
            "S01",
            Timestamp,
            Available: true,
            [
                CreateConflictLookupItem("P-FLY", "EXTENSION Fly Swatter", PriceSourceKind.ProductSetCode, 8.99m),
                CreateConflictLookupItem("P-FLOWER", "flower", PriceSourceKind.ProductBase, 2.99m)
            ]));
        var service = new LocalCatalogSyncService(repository, apiClient);

        await service.FullSyncAsync("S01");

        Assert.Equal(["S01"], apiClient.CodeConflictRequests);
        var replaceCall = Assert.Single(repository.CodeConflictReplaceCalls);
        Assert.Equal("S01", replaceCall.StoreCode);
        Assert.Equal(["P-FLY", "P-FLOWER"], replaceCall.Items.Select(item => item.ProductCode));
        Assert.Equal(2.99m, replaceCall.Items[1].RetailPrice);
        Assert.Equal("6405090401470", replaceCall.Items[1].LookupCode);
    }

    [Fact]
    public async Task FullSyncAsync_KeepsLocalCodeConflictsWhenServerHasNotComputedThem()
    {
        var repository = new FakeLocalCatalogRepository();
        repository.ComparePages.Enqueue([]);
        var apiClient = new FakeCatalogApiClient();
        apiClient.PageResponses.Enqueue(EmptyPage("S01"));
        apiClient.CodeConflictResponses.Enqueue(new CatalogCodeConflictsResponse("S01", Timestamp, Available: false, []));
        var service = new LocalCatalogSyncService(repository, apiClient);

        await service.FullSyncAsync("S01");

        // 服务端刚从旧快照恢复时 Available=false，不能用空列表清掉本地已有的冲突候选。
        Assert.Equal(["S01"], apiClient.CodeConflictRequests);
        Assert.Empty(repository.CodeConflictReplaceCalls);
    }

    [Fact]
    public async Task FullSyncAsync_WhenCodeConflictRequestFails_StillCompletesCatalogSync()
    {
        var repository = new FakeLocalCatalogRepository();
        repository.ComparePages.Enqueue([]);
        var apiClient = new FakeCatalogApiClient
        {
            // 旧版服务端没有该接口时同样表现为请求失败。
            CodeConflictsException = new CatalogApiException("404 Not Found")
        };
        apiClient.PageResponses.Enqueue(EmptyPage("S01"));
        var service = new LocalCatalogSyncService(repository, apiClient);

        var result = await service.FullSyncAsync("S01");

        Assert.NotNull(result);
        Assert.Equal(["S01"], apiClient.CodeConflictRequests);
        Assert.Empty(repository.CodeConflictReplaceCalls);
    }

    [Fact]
    public async Task FullSyncAsync_WhenLocalMatchesRemoteAndCompareHasNoChanges_SkipsFullDownload()
    {
        var repository = new FakeLocalCatalogRepository();
        repository.ComparePages.Enqueue(
        [
            new LocalSellableItemCompareRow("S01", "LOCAL-CODE-1", "hash-1", Timestamp),
            new LocalSellableItemCompareRow("S01", "LOCAL-CODE-2", "hash-2", Timestamp)
        ]);
        repository.ComparePages.Enqueue([]);

        var apiClient = new FakeCatalogApiClient();
        apiClient.CompareResponses.Enqueue(new CatalogCompareResponse(
            "S01",
            Timestamp,
            [],
            [],
            NextCursor: null,
            HasMore: false));
        apiClient.PageResponses.Enqueue(new CatalogSyncPageResponse(
            "S01",
            Timestamp,
            Cursor: null,
            [CreateLookupItem("PAGE-001", "page-code-1")],
            [],
            NextCursor: "PAGE-CODE-1",
            HasMore: true,
            TotalCount: 2));
        var logs = new ConcurrentQueue<string>();
        var service = new LocalCatalogSyncService(repository, apiClient);
        var progressReports = new List<CatalogSyncProgress>();
        var progress = new CapturingProgress<CatalogSyncProgress>(progressReports);

        using var logCapture = CaptureClientLog(logs);
        var result = await service.FullSyncAsync("S01", progress: progress);

        Assert.Equal(new LocalCatalogSyncResult("S01", ComparePages: 1, RemotePages: 1, UpsertedCount: 0, DeletedCount: 0), result);
        Assert.Empty(repository.UpsertedBatches);
        Assert.Empty(repository.DeleteCalls);
        var pageRequest = Assert.Single(apiClient.PageRequests);
        Assert.Equal(("S01", null, 5000), pageRequest);
        Assert.True(HasLog(logs, "download skipped store=S01 reason=no-changes"));
        Assert.Contains(progressReports, report =>
            report.Stage == CatalogSyncProgressStage.Downloading &&
            report.TotalCount == 2 &&
            report.DownloadedCount == 2 &&
            report.Percent == 99);
    }

    [Fact]
    public async Task FullSyncAsync_WhenForcedAndLocalMatchesRemote_DownloadsAllPages()
    {
        var repository = new FakeLocalCatalogRepository();

        var apiClient = new FakeCatalogApiClient();
        apiClient.PageResponses.Enqueue(new CatalogSyncPageResponse(
            "S01",
            Timestamp,
            Cursor: null,
            [CreateLookupItem("PAGE-001", "page-code-1")],
            [],
            NextCursor: "PAGE-CODE-1",
            HasMore: true,
            TotalCount: 2));
        apiClient.PageResponses.Enqueue(new CatalogSyncPageResponse(
            "S01",
            Timestamp,
            Cursor: "PAGE-CODE-1",
            [CreateLookupItem("PAGE-002", "page-code-2")],
            [],
            NextCursor: null,
            HasMore: false,
            TotalCount: 2));
        var logs = new ConcurrentQueue<string>();
        var service = new LocalCatalogSyncService(repository, apiClient);

        using var logCapture = CaptureClientLog(logs);
        var result = await service.FullSyncAsync("S01", forceFullDownload: true);

        Assert.Equal(new LocalCatalogSyncResult("S01", ComparePages: 0, RemotePages: 2, UpsertedCount: 2, DeletedCount: 0), result);
        Assert.Empty(repository.ComparePageRequests);
        Assert.Null(apiClient.LastCompareRequest);
        Assert.Empty(repository.UpsertedBatches);
        Assert.Equal("S01", Assert.Single(repository.StoreReplaceSessionRequests));
        Assert.Equal(2, repository.StagedBatches.Count);
        Assert.Equal(1, repository.StoreReplaceCommitCount);
        Assert.All(
            repository.StagedBatches.SelectMany(batch => batch),
            item => Assert.Equal("S01", item.StoreCode));
        Assert.Equal(
            [("S01", null, 5000), ("S01", "PAGE-CODE-1", 5000)],
            apiClient.PageRequests);
        Assert.True(HasLog(logs, "compare skipped store=S01 reason=force-full-download"));
        Assert.False(HasLog(logs, "download skipped store=S01 reason=no-changes"));
    }

    [Fact]
    public async Task FullSyncAsync_WhenForcedAndLaterPageFails_DoesNotCommitOrClearLocalState()
    {
        var repository = new FakeLocalCatalogRepository();
        repository.SeedStore(
            "S01",
            [
                CreateSellableItem("S01", "LOCAL-001", "local-code-1"),
                CreateSellableItem("S01", "LOCAL-002", "local-code-2")
            ]);
        var apiClient = new FakeCatalogApiClient();
        apiClient.PageResponses.Enqueue(new CatalogSyncPageResponse(
            "S01",
            Timestamp,
            Cursor: null,
            [CreateLookupItem("PAGE-001", "page-code-1")],
            [],
            NextCursor: "PAGE-CODE-1",
            HasMore: true,
            TotalCount: 2));
        apiClient.PageExceptionSequence.Enqueue(new CatalogApiException("page 2 failed"));
        var service = new LocalCatalogSyncService(repository, apiClient);

        await Assert.ThrowsAsync<CatalogApiException>(() => service.FullSyncAsync("S01", forceFullDownload: true));

        Assert.Equal("S01", Assert.Single(repository.StoreReplaceSessionRequests));
        Assert.Single(repository.StagedBatches);
        Assert.Equal(0, repository.StoreReplaceCommitCount);
        Assert.Equal(["LOCAL-001", "LOCAL-002"], repository.LoadSellableItems("S01").Select(item => item.ProductCode).ToArray());
        Assert.Equal(
            [("S01", null, 5000), ("S01", "PAGE-CODE-1", 5000)],
            apiClient.PageRequests);
    }

    [Fact]
    public async Task FullSyncAsync_WhenForced_SplitsLargeDownloadStageIntoUiFriendlyBatches()
    {
        var repository = new FakeLocalCatalogRepository();
        var events = new List<string>();
        repository.StageStarted = () => events.Add("stage");
        var apiClient = new FakeCatalogApiClient();
        apiClient.PageResponses.Enqueue(new CatalogSyncPageResponse(
            "S01",
            Timestamp,
            Cursor: null,
            Enumerable.Range(1, 2001)
                .Select(index => CreateLookupItem($"PAGE-{index:000}", $"page-code-{index:000}", $"PAGE-REF-{index:000}"))
                .ToArray(),
            [],
            NextCursor: null,
            HasMore: false,
            TotalCount: 2001));
        var uiPriority = new RecordingUiPriorityCoordinator(events);
        var service = new LocalCatalogSyncService(repository, apiClient, uiPriority);
        var progressReports = new List<CatalogSyncProgress>();
        var progress = new CapturingProgress<CatalogSyncProgress>(progressReports);

        var result = await service.FullSyncAsync("S01", progress: progress, forceFullDownload: true);

        Assert.Equal(2001, result.UpsertedCount);
        Assert.Empty(repository.UpsertedBatches);
        Assert.True(repository.StagedBatches.Count > 1);
        Assert.Equal(1, repository.StoreReplaceCommitCount);
        var stageIndexes = events
            .Select((name, index) => (name, index))
            .Where(entry => entry.name == "stage")
            .Select(entry => entry.index)
            .ToArray();
        Assert.Equal(repository.StagedBatches.Count, stageIndexes.Length);
        Assert.All(stageIndexes, index => Assert.True(index > 0 && events[index - 1] == "wait"));
        Assert.Contains(progressReports, report =>
            report.Stage == CatalogSyncProgressStage.Downloading &&
            report.DownloadedCount == 2000 &&
            report.UpsertedCount == 2000);
        Assert.All(
            progressReports.Where(report => report.Stage == CatalogSyncProgressStage.Downloading),
            report => Assert.InRange(report.Percent, 0, 99));
        Assert.Equal(100, Assert.Single(progressReports.Where(report => report.Stage == CatalogSyncProgressStage.Completed)).Percent);
    }

    [Fact]
    public async Task FullSyncAsync_WhenCanceled_DoesNotReportFailedProgress()
    {
        var repository = new FakeLocalCatalogRepository();
        repository.ComparePages.Enqueue([]);
        var apiClient = new FakeCatalogApiClient
        {
            PageException = new OperationCanceledException("sync canceled")
        };
        var service = new LocalCatalogSyncService(repository, apiClient);
        var progressReports = new List<CatalogSyncProgress>();
        var progress = new CapturingProgress<CatalogSyncProgress>(progressReports);

        await Assert.ThrowsAsync<OperationCanceledException>(() => service.FullSyncAsync("S01", progress: progress));

        Assert.DoesNotContain(progressReports, report => report.Stage == CatalogSyncProgressStage.Failed);
    }

    [Fact]
    public async Task FullSyncAsync_WaitsForUiIdleBeforeRemoteDownload()
    {
        var repository = new FakeLocalCatalogRepository();
        repository.ComparePages.Enqueue([]);
        var apiClient = new FakeCatalogApiClient();
        apiClient.PageResponses.Enqueue(new CatalogSyncPageResponse(
            "S01",
            Timestamp,
            Cursor: null,
            [CreateLookupItem("PAGE-001", "page-code", "PAGE-REF-001")],
            [],
            NextCursor: null,
            HasMore: false,
            TotalCount: 1));
        var uiPriority = new GatedUiPriorityCoordinator();
        var service = new LocalCatalogSyncService(repository, apiClient, uiPriority);

        var syncTask = service.FullSyncAsync("S01");
        await uiPriority.FirstWaitStarted.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);

        Assert.Empty(apiClient.PageRequests);

        uiPriority.ReleaseFirstWait();
        await syncTask.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);

        Assert.Single(apiClient.PageRequests);
    }

    [Fact]
    public async Task FullSyncAsync_SplitsLargeDownloadApplyIntoUiFriendlyBatches()
    {
        var repository = new FakeLocalCatalogRepository();
        repository.ComparePages.Enqueue([]);
        var apiClient = new FakeCatalogApiClient();
        apiClient.PageResponses.Enqueue(new CatalogSyncPageResponse(
            "S01",
            Timestamp,
            Cursor: null,
            Enumerable.Range(1, 2001)
                .Select(index => CreateLookupItem($"PAGE-{index:000}", $"page-code-{index:000}", $"PAGE-REF-{index:000}"))
                .ToArray(),
            [],
            NextCursor: null,
            HasMore: false,
            TotalCount: 2001));
        var uiPriority = new CountingUiPriorityCoordinator();
        var service = new LocalCatalogSyncService(repository, apiClient, uiPriority);
        var progressReports = new List<CatalogSyncProgress>();
        var progress = new CapturingProgress<CatalogSyncProgress>(progressReports);

        var result = await service.FullSyncAsync("S01", progress: progress);

        Assert.Equal(2001, result.UpsertedCount);
        Assert.True(repository.UpsertedBatches.Count > 1);
        Assert.True(uiPriority.WaitCount >= repository.UpsertedBatches.Count);
        Assert.Contains(progressReports, report =>
            report.Stage == CatalogSyncProgressStage.Downloading &&
            report.DownloadedCount == 2000 &&
            report.UpsertedCount == 2000);
        Assert.All(
            progressReports.Where(report => report.Stage == CatalogSyncProgressStage.Downloading),
            report => Assert.InRange(report.Percent, 0, 99));
    }

    [Fact]
    public async Task FullSyncAsync_ReportsComparedCountMonotonicallyAcrossLocalPages()
    {
        var repository = new FakeLocalCatalogRepository();
        repository.ComparePages.Enqueue(
        [
            new LocalSellableItemCompareRow("S01", "LOCAL-CODE-1", "hash-1", Timestamp),
            new LocalSellableItemCompareRow("S01", "LOCAL-CODE-2", "hash-2", Timestamp)
        ]);
        repository.ComparePages.Enqueue(
        [
            new LocalSellableItemCompareRow("S01", "LOCAL-CODE-3", "hash-3", Timestamp)
        ]);
        repository.ComparePages.Enqueue([]);
        var apiClient = new FakeCatalogApiClient();
        apiClient.CompareResponses.Enqueue(new CatalogCompareResponse("S01", Timestamp, [], [], null, false));
        apiClient.CompareResponses.Enqueue(new CatalogCompareResponse("S01", Timestamp, [], [], null, false));
        apiClient.PageResponses.Enqueue(new CatalogSyncPageResponse(
            "S01",
            Timestamp,
            Cursor: null,
            [],
            [],
            NextCursor: null,
            HasMore: false,
            TotalCount: 3));
        var service = new LocalCatalogSyncService(repository, apiClient);
        var progressReports = new List<CatalogSyncProgress>();
        var progress = new CapturingProgress<CatalogSyncProgress>(progressReports);

        await service.FullSyncAsync("S01", progress: progress);

        var comparedCounts = progressReports
            .Where(report => report.Stage == CatalogSyncProgressStage.Comparing)
            .Select(report => report.ComparedCount)
            .Distinct()
            .ToArray();
        Assert.Equal([0, 2, 3], comparedCounts);
        Assert.True(comparedCounts.SequenceEqual(comparedCounts.Order()));
    }

    [Fact]
    public async Task FullSyncAsync_ReportsDownloadProgressWithPercentAndTotals()
    {
        var repository = new FakeLocalCatalogRepository();
        repository.ComparePages.Enqueue([]);
        var apiClient = new FakeCatalogApiClient();
        apiClient.PageResponses.Enqueue(new CatalogSyncPageResponse(
            "S01",
            Timestamp,
            Cursor: null,
            [CreateLookupItem("PAGE-001", "page-code-1")],
            [],
            NextCursor: "PAGE-CODE-1",
            HasMore: true,
            TotalCount: 2));
        apiClient.PageResponses.Enqueue(new CatalogSyncPageResponse(
            "S01",
            Timestamp,
            Cursor: "PAGE-CODE-1",
            [CreateLookupItem("PAGE-002", "page-code-2")],
            [CreateDeletedLookup("gone-code")],
            NextCursor: null,
            HasMore: false,
            TotalCount: 2));
        var service = new LocalCatalogSyncService(repository, apiClient);
        var progressReports = new List<CatalogSyncProgress>();
        var progress = new CapturingProgress<CatalogSyncProgress>(progressReports);

        await service.FullSyncAsync("S01", progress: progress);

        Assert.Contains(progressReports, report =>
            report.Stage == CatalogSyncProgressStage.Downloading &&
            report.DownloadedCount == 1 &&
            report.TotalCount == 2 &&
            report.Percent == 50);
        Assert.All(
            progressReports.Where(report => report.Stage == CatalogSyncProgressStage.Downloading),
            report => Assert.InRange(report.Percent, 0, 99));
        var completed = Assert.Single(progressReports.Where(report => report.Stage == CatalogSyncProgressStage.Completed));
        Assert.Equal(100, completed.Percent);
        Assert.Equal(2, completed.DownloadedCount);
        Assert.Equal(2, completed.TotalCount);
        Assert.Equal(2, completed.RemotePages);
        Assert.Equal(2, completed.UpsertedCount);
        Assert.Equal(1, completed.DeletedCount);
    }

    [Fact]
    public async Task FullSyncAsync_RequestsRemotePagesWithMaxBatchSize()
    {
        var repository = new FakeLocalCatalogRepository();
        repository.ComparePages.Enqueue([]);
        var apiClient = new FakeCatalogApiClient();
        apiClient.PageResponses.Enqueue(new CatalogSyncPageResponse(
            "S01",
            Timestamp,
            Cursor: null,
            [],
            [],
            NextCursor: null,
            HasMore: false,
            TotalCount: 0));
        var service = new LocalCatalogSyncService(repository, apiClient);
        var progressReports = new List<CatalogSyncProgress>();
        var progress = new CapturingProgress<CatalogSyncProgress>(progressReports);

        await service.FullSyncAsync("S01", progress: progress);

        var pageRequest = Assert.Single(apiClient.PageRequests);
        Assert.Equal(("S01", null, 5000), pageRequest);
        var comparePageRequest = Assert.Single(repository.ComparePageRequests);
        Assert.Equal(("S01", null, 2000), comparePageRequest);
        Assert.All(
            progressReports.Where(report => report.Stage == CatalogSyncProgressStage.Downloading),
            report => Assert.NotEqual(100, report.Percent));
        Assert.Equal(100, Assert.Single(progressReports.Where(report => report.Stage == CatalogSyncProgressStage.Completed)).Percent);
    }

    [Fact]
    public async Task FullSyncAsync_refreshes_promotion_cache_after_catalog_sync_succeeds()
    {
        var repository = new FakeLocalCatalogRepository();
        repository.ComparePages.Enqueue([]);
        var apiClient = new FakeCatalogApiClient();
        apiClient.PageResponses.Enqueue(new CatalogSyncPageResponse(
            "S01",
            Timestamp,
            Cursor: null,
            [],
            [],
            NextCursor: null,
            HasMore: false,
            TotalCount: 0));
        var promotionRepository = new FakeLocalPromotionRepository();
        var promotionApiClient = new FakePromotionApiClient();
        promotionApiClient.RulesResponses.Enqueue(new PromotionRulesResponse(
            "S01",
            Timestamp,
            [
                new PromotionRuleDto(
                    "PROMO-001",
                    "Tea Bundle",
                    Timestamp.AddDays(-1).UtcDateTime,
                    Timestamp.AddDays(1).UtcDateTime,
                    true,
                    10,
                    2,
                    8.5m,
                    1,
                    [new PromotionRuleProductDto("SKU-001", 2)])
            ]));
        var service = new LocalCatalogSyncService(repository, apiClient, null, promotionRepository, promotionApiClient);

        await service.FullSyncAsync("S01");

        var replaceCall = Assert.Single(promotionRepository.ReplaceCalls);
        Assert.Equal("S01", replaceCall.StoreCode);
        Assert.Equal("PROMO-001", Assert.Single(replaceCall.Response.Rules).Id);
        var request = Assert.Single(promotionApiClient.Requests);
        Assert.Equal("S01", request.StoreCode);
        Assert.Null(request.AsOf);
    }

    [Fact]
    public async Task FullSyncAsync_when_promotion_sync_fails_keeps_existing_cached_rules()
    {
        var repository = new FakeLocalCatalogRepository();
        repository.ComparePages.Enqueue([]);
        var apiClient = new FakeCatalogApiClient();
        apiClient.PageResponses.Enqueue(new CatalogSyncPageResponse(
            "S01",
            Timestamp,
            Cursor: null,
            [],
            [],
            NextCursor: null,
            HasMore: false,
            TotalCount: 0));
        var promotionRepository = new FakeLocalPromotionRepository();
        promotionRepository.SeedStore(
            "S01",
            [
                new PromotionRuleDto(
                    "PROMO-OLD",
                    "Old Rule",
                    Timestamp.AddDays(-2).UtcDateTime,
                    Timestamp.AddDays(2).UtcDateTime,
                    false,
                    5,
                    2,
                    9.9m,
                    1,
                    [new PromotionRuleProductDto("SKU-OLD", 1)])
            ]);
        var promotionApiClient = new FakePromotionApiClient
        {
            Exception = new CatalogApiException("promotion sync failed")
        };
        var logs = new ConcurrentQueue<string>();
        var service = new LocalCatalogSyncService(repository, apiClient, null, promotionRepository, promotionApiClient);
        var progressReports = new List<CatalogSyncProgress>();
        var progress = new CapturingProgress<CatalogSyncProgress>(progressReports);

        using var logCapture = CaptureClientLog(logs);

        var result = await service.FullSyncAsync("S01", progress: progress);

        Assert.Equal(new LocalCatalogSyncResult("S01", ComparePages: 0, RemotePages: 1, UpsertedCount: 0, DeletedCount: 0), result);
        Assert.Equal(["PROMO-OLD"], promotionRepository.LoadStoreRuleIds("S01"));
        Assert.Empty(promotionRepository.ReplaceCalls);
        Assert.Contains(progressReports, report => report.Stage == CatalogSyncProgressStage.Completed);
        Assert.True(HasLog(logs, "promotion sync failed"));
    }

    [Fact]
    public async Task DownloadSpecialProductsAsync_RequestsRemotePagesWithMaxBatchSize()
    {
        var repository = new FakeLocalCatalogRepository();
        var apiClient = new FakeCatalogApiClient();
        apiClient.SpecialProductsPageResponses.Enqueue(new CatalogSpecialProductsPageResponse(
            "S01",
            Timestamp,
            Cursor: null,
            [],
            NextCursor: null,
            HasMore: false,
            TotalCount: 0));
        var service = new SpecialProductService(repository, apiClient);

        await service.DownloadSpecialProductsAsync("S01");

        var pageRequest = Assert.Single(apiClient.PageRequests);
        Assert.Equal(("S01", null, 5000), pageRequest);
    }

    [Fact]
    public async Task DownloadSpecialProductsAsync_WritesDiagnosticLogs()
    {
        var repository = new FakeLocalCatalogRepository();
        var apiClient = new FakeCatalogApiClient();
        var logs = new ConcurrentQueue<string>();
        apiClient.SpecialProductsPageResponses.Enqueue(new CatalogSpecialProductsPageResponse(
            "S01",
            Timestamp,
            Cursor: null,
            [CreateLookupItem("P01", "p01-code")],
            NextCursor: null,
            HasMore: false,
            TotalCount: 1));
        var service = new SpecialProductService(repository, apiClient);

        using var logCapture = CaptureClientLog(logs);
        await service.DownloadSpecialProductsAsync("S01");

        Assert.True(HasLog(logs, "[SpecialProducts]"));
        Assert.True(HasLog(logs, "download page response store=S01 page=1"));
        Assert.True(HasLog(logs, "apiElapsedMs="));
        Assert.True(HasLog(logs, "upsertElapsedMs="));
        Assert.True(HasLog(logs, "download completed store=S01"));
    }

    [Fact]
    public async Task CatalogApiClient_CompareSellableItemsAsync_PostsJsonAndUnwrapsApiResult()
    {
        HttpRequestMessage? capturedRequest = null;
        var expected = new CatalogCompareResponse(
            "S01",
            Timestamp,
            [CreateLookupItem("CMP-001", "cmp-code")],
            [],
            NextCursor: null,
            HasMore: false);
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            capturedRequest = request;
            return JsonResponse(ApiResult<CatalogCompareResponse>.Ok(expected));
        });
        var client = new CatalogApiClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:5000/")
        });

        var response = await client.CompareSellableItemsAsync(new CatalogCompareRequest("S01", []));

        Assert.Equal(HttpMethod.Post, capturedRequest?.Method);
        Assert.Equal("http://localhost:5000/api/v1/catalog/sellable-items/compare", capturedRequest?.RequestUri?.ToString());
        Assert.Equal("S01", response.StoreCode);
        Assert.Equal("CMP-001", Assert.Single(response.UpsertedLookups).ProductCode);
    }

    [Fact]
    public async Task CatalogApiClient_CompareSellableItemsAsync_RetriesInvalidGatewayJsonOnce()
    {
        var attempts = 0;
        var delays = new List<TimeSpan>();
        var logs = new ConcurrentQueue<string>();
        var expected = new CatalogCompareResponse(
            "S01",
            Timestamp,
            [CreateLookupItem("CMP-RETRY", "cmp-retry")],
            [],
            NextCursor: null,
            HasMore: false);
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            attempts++;
            return attempts == 1
                ? new HttpResponseMessage(HttpStatusCode.GatewayTimeout)
                {
                    Content = new StringContent("gateway-secret-html", Encoding.UTF8, "text/html")
                }
                : JsonResponse(ApiResult<CatalogCompareResponse>.Ok(expected));
        });
        var client = new CatalogApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000/") },
            (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        using var logCapture = CaptureClientLog(logs);
        var response = await client.CompareSellableItemsAsync(new CatalogCompareRequest("S01", []));

        Assert.Equal(2, attempts);
        Assert.Equal([CatalogApiClient.TransientRetryDelay], delays);
        Assert.Equal("CMP-RETRY", Assert.Single(response.UpsertedLookups).ProductCode);
        Assert.True(HasLog(logs, "attempt=2"));
        Assert.True(HasLog(logs, "reason=invalid-json"));
        Assert.True(HasLog(logs, "status=504"));
        Assert.True(HasLog(logs, "contentType=text/html"));
        Assert.True(HasLog(logs, "bodyLength="));
        Assert.DoesNotContain(logs, line => line.Contains("gateway-secret-html", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CatalogApiClient_GetSellableItemsPageAsync_RetriesGatewayStatusOnce()
    {
        var attempts = 0;
        var expected = new CatalogSyncPageResponse(
            "S01",
            Timestamp,
            Cursor: null,
            [CreateLookupItem("PAGE-RETRY", "page-retry")],
            [],
            NextCursor: null,
            HasMore: false,
            TotalCount: 1);
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            attempts++;
            return attempts == 1
                ? JsonResponse(
                    ApiResult<CatalogSyncPageResponse>.Fail("GATEWAY_TIMEOUT", "temporary gateway failure"),
                    HttpStatusCode.BadGateway)
                : JsonResponse(ApiResult<CatalogSyncPageResponse>.Ok(expected));
        });
        var client = new CatalogApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000/") },
            (_, _) => Task.CompletedTask);

        var response = await client.GetSellableItemsPageAsync("S01", cursor: null, pageSize: 100);

        Assert.Equal(2, attempts);
        Assert.Equal("PAGE-RETRY", Assert.Single(response.Items).ProductCode);
    }

    [Fact]
    public async Task CatalogApiClient_CompareSellableItemsAsync_DoesNotRetryUnauthorizedResponse()
    {
        var attempts = 0;
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            attempts++;
            return JsonResponse(
                ApiResult<CatalogCompareResponse>.Fail("UNAUTHORIZED", "authorization required"),
                HttpStatusCode.Unauthorized);
        });
        var client = new CatalogApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000/") },
            (_, _) => throw new InvalidOperationException("Retry delay must not run."));

        var exception = await Assert.ThrowsAsync<CatalogApiException>(() =>
            client.CompareSellableItemsAsync(new CatalogCompareRequest("S01", [])));

        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task CatalogApiClient_CompareSellableItemsAsync_DoesNotRetryCallerCancellation()
    {
        var attempts = 0;
        using var cancellation = new CancellationTokenSource();
        var handler = new StubHttpMessageHandler((_, cancellationToken) =>
        {
            attempts++;
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Cancellation should stop the request.");
        });
        var client = new CatalogApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000/") },
            (_, _) => throw new InvalidOperationException("Retry delay must not run."));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.CompareSellableItemsAsync(
                new CatalogCompareRequest("S01", []),
                cancellation.Token));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task CatalogApiClient_CompareSellableItemsAsync_StopsAfterOneTransientRetry()
    {
        var attempts = 0;
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            attempts++;
            return new HttpResponseMessage(HttpStatusCode.GatewayTimeout)
            {
                Content = new StringContent("gateway timeout", Encoding.UTF8, "text/html")
            };
        });
        var client = new CatalogApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000/") },
            (_, _) => Task.CompletedTask);

        await Assert.ThrowsAsync<CatalogApiException>(() =>
            client.CompareSellableItemsAsync(new CatalogCompareRequest("S01", [])));

        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task CatalogApiClient_MarkSpecialProductAsync_PostsJsonAndUnwrapsApiResult()
    {
        HttpRequestMessage? capturedRequest = null;
        var expected = new CatalogSpecialProductMarkResponse(
            "S01",
            "P01",
            true,
            Timestamp,
            [CreateLookupItem("P01", "p01-code")]);
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            capturedRequest = request;
            return JsonResponse(ApiResult<CatalogSpecialProductMarkResponse>.Ok(expected));
        });
        var client = new CatalogApiClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:5000/")
        });

        var response = await client.MarkSpecialProductAsync(new CatalogSpecialProductMarkRequest("S01", "P01", true));

        Assert.Equal(HttpMethod.Post, capturedRequest?.Method);
        Assert.Equal("http://localhost:5000/api/v1/catalog/special-products/mark", capturedRequest?.RequestUri?.ToString());
        Assert.Equal("P01", response.ProductCode);
        Assert.True(response.IsSpecialProduct);
        Assert.Equal("P01", Assert.Single(response.Items).ProductCode);
    }

    [Fact]
    public async Task CatalogApiClient_GetSpecialProductsPageAsync_GetsJsonAndUnwrapsApiResult()
    {
        HttpRequestMessage? capturedRequest = null;
        var expected = new CatalogSpecialProductsPageResponse(
            "S01",
            Timestamp,
            Cursor: null,
            [CreateLookupItem("P01", "p01-code")],
            NextCursor: null,
            HasMore: false,
            TotalCount: 1);
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            capturedRequest = request;
            return JsonResponse(ApiResult<CatalogSpecialProductsPageResponse>.Ok(expected));
        });
        var client = new CatalogApiClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:5000/")
        });

        var response = await client.GetSpecialProductsPageAsync("S01", cursor: null, pageSize: 100);

        Assert.Equal(HttpMethod.Get, capturedRequest?.Method);
        Assert.Equal("http://localhost:5000/api/v1/catalog/special-products/page?storeCode=S01&pageSize=100", capturedRequest?.RequestUri?.ToString());
        Assert.Equal("P01", Assert.Single(response.Items).ProductCode);
        Assert.Equal(1, response.TotalCount);
    }

    [Fact]
    public async Task CatalogApiClient_GetPromotionRulesAsync_GetsJsonAndUnwrapsApiResult()
    {
        HttpRequestMessage? capturedRequest = null;
        var expected = new CatalogPromotionsResponse(
            "S01",
            Timestamp,
            [CreatePromotionRule("PROMO-001", "SKU-001")]);
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            capturedRequest = request;
            return JsonResponse(ApiResult<CatalogPromotionsResponse>.Ok(expected));
        });
        var client = new CatalogApiClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:5000/")
        });

        var response = await client.GetPromotionRulesAsync("S01");

        Assert.Equal(HttpMethod.Get, capturedRequest?.Method);
        Assert.Equal("http://localhost:5000/api/v1/catalog/promotions?storeCode=S01", capturedRequest?.RequestUri?.ToString());
        Assert.Equal("PROMO-001", Assert.Single(response.Promotions).PromotionId);
    }

    [Fact]
    public async Task DeviceAuthorizationMessageHandler_AddsBearerAndDeviceHeaders()
    {
        HttpRequestMessage? capturedRequest = null;
        var state = new DeviceAuthorizationState();
        state.Set(new DeviceAuthorizationContext("POS-001", "S01", "HW-001", "AUTH-001"));
        var cashierContext = new CashierSessionContext();
        cashierContext.SetCurrent(new CashierSessionDto(
            "C001", "U001", "Alice", "S01", "POS-001", [], [], ["S01"],
            false, false, false, "CASHIER-TICKET", DateTimeOffset.UtcNow.AddHours(1)));
        var handler = new DeviceAuthorizationMessageHandler(state, cashierContext)
        {
            InnerHandler = new StubHttpMessageHandler((request, _) =>
            {
                capturedRequest = request;
                return JsonResponse(ApiResult<CatalogSyncPageResponse>.Ok(new CatalogSyncPageResponse(
                    "S01",
                    Timestamp,
                    Cursor: null,
                    [],
                    [],
                    NextCursor: null,
                    HasMore: false,
                    TotalCount: 0)));
            })
        };
        var client = new CatalogApiClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:5000/")
        });

        await client.GetSellableItemsPageAsync("S01", cursor: null, pageSize: 100);

        Assert.Equal("Bearer", capturedRequest?.Headers.Authorization?.Scheme);
        Assert.Equal("AUTH-001", capturedRequest?.Headers.Authorization?.Parameter);
        Assert.Equal("POS-001", capturedRequest?.Headers.GetValues(DeviceAuthConstants.DeviceCodeHeader).Single());
        Assert.Equal("S01", capturedRequest?.Headers.GetValues(DeviceAuthConstants.StoreCodeHeader).Single());
        Assert.Equal("HW-001", capturedRequest?.Headers.GetValues(DeviceAuthConstants.HardwareIdHeader).Single());
        Assert.Equal(
            "CASHIER-TICKET",
            capturedRequest?.Headers.GetValues(CashierAuthorizationConstants.HeaderName).Single());
    }

    [Fact]
    public async Task DeviceAuthorizationMessageHandler_clears_emergency_session_after_online_forbidden()
    {
        var state = new DeviceAuthorizationState();
        state.Set(new DeviceAuthorizationContext("POS-001", "S01", "HW-001", "AUTH-001"));
        var cashierContext = new CashierSessionContext();
        cashierContext.SetCurrent(CashierSessionContext.CreateEmergencyOverride(
            "S01", "POS-001", Guid.NewGuid(), DateTimeOffset.UtcNow.AddHours(1), "HBPOSE1-token"));
        var handler = new DeviceAuthorizationMessageHandler(state, cashierContext)
        {
            InnerHandler = new StubHttpMessageHandler((_, _) =>
                new HttpResponseMessage(HttpStatusCode.Forbidden))
        };
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };

        using var response = await client.GetAsync("api/v1/orders/history");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(cashierContext.CurrentSession);
    }

    [Fact]
    public async Task CatalogApiClient_LookupSellableItemAsync_ThrowsForStoreNotFound404()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            JsonResponse(
                ApiResult<CatalogLookupResponse>.Fail("STORE_NOT_FOUND", "store was not found or inactive"),
                HttpStatusCode.NotFound));
        var client = new CatalogApiClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:5000/")
        });

        var ex = await Assert.ThrowsAsync<CatalogApiException>(() =>
            client.LookupSellableItemAsync("S01", "abc"));

        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
        Assert.Equal("STORE_NOT_FOUND", ex.ErrorCode);
    }

    [Fact]
    public async Task CatalogApiClient_LookupSellableItemAsync_ReturnsNullOnlyForLookupNotFound404()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            JsonResponse(
                ApiResult<CatalogLookupResponse>.Fail("LOOKUP_NOT_FOUND", "lookup was not found"),
                HttpStatusCode.NotFound));
        var client = new CatalogApiClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:5000/")
        });

        var response = await client.LookupSellableItemAsync("S01", "abc");

        Assert.Null(response);
    }

    [Fact]
    public async Task CatalogApiClient_LookupSellableItemAsync_PropagatesCancellation()
    {
        var handler = new StubHttpMessageHandler((_, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return JsonResponse(ApiResult<CatalogLookupResponse>.Ok(new CatalogLookupResponse("S01", "abc", "ABC", true, null)));
        });
        var client = new CatalogApiClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:5000/")
        });
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.LookupSellableItemAsync("S01", "abc", cts.Token));
    }

    [Fact]
    public async Task ConnectivityApiClient_CheckOnlineAsync_ReturnsTrueWhenHealthEndpointSucceeds()
    {
        HttpRequestMessage? capturedRequest = null;
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            capturedRequest = request;
            return JsonResponse(ApiResult<HealthCheckResponse>.Ok(
                new HealthCheckResponse(true, DateTimeOffset.UnixEpoch, "ok")));
        });
        var client = new ConnectivityApiClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:5000/")
        });

        var isOnline = await client.CheckOnlineAsync();

        Assert.True(isOnline);
        Assert.Equal(HttpMethod.Get, capturedRequest?.Method);
        Assert.Equal("http://localhost:5000/api/v1/health", capturedRequest?.RequestUri?.ToString());
    }

    [Fact]
    public async Task ConnectivityApiClient_CheckOnlineAsync_ReturnsFalseWhenHealthEndpointFails()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var client = new ConnectivityApiClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:5000/")
        });

        var isOnline = await client.CheckOnlineAsync();

        Assert.False(isOnline);
    }

    private static CatalogLookupItemDto CreateLookupItem(string productCode, string lookupCode, string? referenceCode = null)
    {
        var normalizedLookupCode = lookupCode.Trim().ToUpperInvariant();
        return new CatalogLookupItemDto(
            "S01",
            productCode,
            ReferenceCode: referenceCode,
            $"{productCode} item",
            lookupCode,
            normalizedLookupCode,
            ItemNumber: productCode,
            Barcode: lookupCode,
            RetailPrice: 12.34m,
            PriceSourceKind.StoreRetailPrice,
            "store-retail",
            QuantityFactor: 1m,
            UpdatedAt: Timestamp,
            RowVersion: $"row-{productCode}",
            ProductImage: $"https://images.example/{productCode}.jpg",
            DiscountRate: 0.2m);
    }

    private static CatalogLookupItemDto CreateConflictLookupItem(
        string productCode,
        string displayName,
        PriceSourceKind priceSource,
        decimal retailPrice)
    {
        return CreateLookupItem(productCode, "6405090401470") with
        {
            DisplayName = displayName,
            PriceSource = priceSource,
            PriceSourceLabel = priceSource.ToString(),
            RetailPrice = retailPrice
        };
    }

    private static CatalogSyncPageResponse EmptyPage(string storeCode)
    {
        return new CatalogSyncPageResponse(
            storeCode,
            Timestamp,
            Cursor: null,
            [],
            [],
            NextCursor: null,
            HasMore: false,
            TotalCount: 0);
    }

    private static DeletedLookupDto CreateDeletedLookup(string lookupCode)
    {
        return new DeletedLookupDto(
            "S01",
            lookupCode,
            lookupCode.Trim().ToUpperInvariant(),
            Timestamp);
    }

    private static SellableItemDto CreateSellableItem(string storeCode, string productCode, string lookupCode)
    {
        return new SellableItemDto(
            storeCode,
            productCode,
            ReferenceCode: null,
            DisplayName: $"{productCode} item",
            LookupCode: lookupCode,
            ItemNumber: productCode,
            Barcode: lookupCode,
            RetailPrice: 12.34m,
            PriceSourceKind.StoreRetailPrice,
            "store-retail",
            QuantityFactor: 1m,
            UpdatedAt: Timestamp);
    }

    private static CatalogPromotionRuleDto CreatePromotionRule(string promotionId, string productCode)
    {
        return new CatalogPromotionRuleDto(
            promotionId,
            "Quantity discount",
            IsExclusive: true,
            Priority: 10,
            ApplyQuantity: 2,
            FixedPrice: 15m,
            MaxApplicationsPerOrder: null,
            Timestamp.AddDays(-1),
            Timestamp.AddDays(1),
            Timestamp,
            [new CatalogPromotionProductDto(productCode, UnitWeight: 1)]);
    }

    private static HttpResponseMessage JsonResponse<T>(T value, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                Encoding.UTF8,
                "application/json")
        };
    }

    private sealed class FakeLocalCatalogRepository : ILocalCatalogRepository
    {
        private readonly Dictionary<string, List<SellableItemDto>> _itemsByStore = new(StringComparer.OrdinalIgnoreCase);

        public Queue<IReadOnlyList<LocalSellableItemCompareRow>> ComparePages { get; } = new();

        public List<(string StoreCode, string? AfterLookupCodeNormalized, int PageSize)> ComparePageRequests { get; } = [];

        public List<IReadOnlyList<SellableItemDto>> UpsertedBatches { get; } = [];

        public List<(string StoreCode, string[] LookupCodes)> DeleteCalls { get; } = [];

        public List<string> StoreReplaceSessionRequests { get; } = [];

        public List<IReadOnlyList<SellableItemDto>> StagedBatches { get; } = [];

        public int StoreReplaceCommitCount { get; private set; }

        public List<(string StoreCode, IReadOnlyList<CatalogPromotionRuleDto> Rules)> PromotionReplaceCalls { get; } = [];

        public Action? StageStarted { get; set; }

        public Task ReplaceSellableItemsAsync(IEnumerable<SellableItemDto> items, CancellationToken cancellationToken = default)
        {
            return UpsertSellableItemsAsync(items, cancellationToken);
        }

        public Task UpsertSellableItemsAsync(IEnumerable<SellableItemDto> items, CancellationToken cancellationToken = default)
        {
            var batch = items.ToArray();
            UpsertedBatches.Add(batch);
            foreach (var item in batch)
            {
                var storeItems = GetStoreItems(item.StoreCode);
                var existingIndex = storeItems.FindIndex(existing =>
                    string.Equals(existing.LookupCode.Trim(), item.LookupCode.Trim(), StringComparison.OrdinalIgnoreCase));
                if (existingIndex >= 0)
                {
                    storeItems[existingIndex] = item;
                }
                else
                {
                    storeItems.Add(item);
                }
            }
            return Task.CompletedTask;
        }

        public Task<int> DeleteByLookupCodesAsync(
            string storeCode,
            IEnumerable<string> lookupCodes,
            CancellationToken cancellationToken = default)
        {
            var materializedCodes = lookupCodes.ToArray();
            DeleteCalls.Add((storeCode, materializedCodes));
            return Task.FromResult(materializedCodes.Length);
        }

        public Task<SellableItemDto?> FindByLookupCodeAsync(
            string storeCode,
            string lookupCode,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<SellableItemDto?>(null);
        }

        public Task<IReadOnlyList<SellableItemDto>> LoadSpecialProductItemsAsync(
            string storeCode,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<SellableItemDto>>([]);
        }

        public Task SaveSpecialProductOrderAsync(
            string storeCode,
            IEnumerable<string> productCodes,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<int> UpdateSpecialProductFlagAsync(
            string storeCode,
            string productCode,
            bool isSpecialProduct,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(0);
        }

        public Task<int> ClearSpecialProductFlagsExceptAsync(
            string storeCode,
            IEnumerable<string> productCodesToKeep,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(0);
        }

        public Task<IReadOnlyList<LocalSellableItemCompareRow>> LoadSellableItemComparePageAsync(
            string storeCode,
            string? afterLookupCodeNormalized,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            ComparePageRequests.Add((storeCode, afterLookupCodeNormalized, pageSize));
            return Task.FromResult(ComparePages.Count == 0 ? [] : ComparePages.Dequeue());
        }

        public Task<IReadOnlyList<SellableItemDto>> LoadSellableItemsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<SellableItemDto>>(
                _itemsByStore
                    .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
                    .SelectMany(entry => entry.Value)
                    .ToArray());
        }

        public Task<IReadOnlyList<SellableItemDto>> LoadSellableItemsAsync(string storeCode, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<SellableItemDto>>(LoadSellableItems(storeCode));
        }

        public Task ReplacePromotionRulesAsync(
            string storeCode,
            IEnumerable<CatalogPromotionRuleDto> rules,
            CancellationToken cancellationToken = default)
        {
            PromotionReplaceCalls.Add((storeCode, rules.ToArray()));
            return Task.CompletedTask;
        }

        public List<(string StoreCode, IReadOnlyList<SellableItemDto> Items)> CodeConflictReplaceCalls { get; } = [];

        public Task ReplaceCodeConflictItemsAsync(
            string storeCode,
            IEnumerable<SellableItemDto> items,
            CancellationToken cancellationToken = default)
        {
            CodeConflictReplaceCalls.Add((storeCode, items.ToArray()));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<CatalogPromotionRuleDto>> LoadPromotionRulesAsync(
            string storeCode,
            CancellationToken cancellationToken = default)
        {
            var call = PromotionReplaceCalls.LastOrDefault(entry =>
                string.Equals(entry.StoreCode, storeCode, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(call.Rules ?? []);
        }

        public Task<ILocalCatalogStoreReplaceSession> BeginStoreReplaceSessionAsync(
            string storeCode,
            CancellationToken cancellationToken = default)
        {
            StoreReplaceSessionRequests.Add(storeCode);
            var session = new FakeLocalCatalogStoreReplaceSession(this, storeCode);
            return Task.FromResult<ILocalCatalogStoreReplaceSession>(session);
        }

        public void SeedStore(string storeCode, IEnumerable<SellableItemDto> items)
        {
            _itemsByStore[storeCode] = items.ToList();
        }

        public IReadOnlyList<SellableItemDto> LoadSellableItems(string storeCode)
        {
            return _itemsByStore.TryGetValue(storeCode, out var items) ? items.ToArray() : [];
        }

        private List<SellableItemDto> GetStoreItems(string storeCode)
        {
            if (!_itemsByStore.TryGetValue(storeCode, out var storeItems))
            {
                storeItems = [];
                _itemsByStore[storeCode] = storeItems;
            }

            return storeItems;
        }

        private sealed class FakeLocalCatalogStoreReplaceSession(
            FakeLocalCatalogRepository repository,
            string storeCode) : ILocalCatalogStoreReplaceSession
        {
            private readonly List<SellableItemDto> _stagedItems = [];

            public List<IReadOnlyList<SellableItemDto>> StageItems { get; } = [];

            public Task StageAsync(IEnumerable<SellableItemDto> items, CancellationToken cancellationToken = default)
            {
                var batch = items.ToArray();
                repository.StageStarted?.Invoke();
                StageItems.Add(batch);
                repository.StagedBatches.Add(batch);
                _stagedItems.AddRange(batch);
                return Task.CompletedTask;
            }

            public Task<LocalCatalogStoreReplaceCommitResult> CommitAsync(CancellationToken cancellationToken = default)
            {
                var previousItems = repository.LoadSellableItems(storeCode);
                var stagedItems = _stagedItems
                    .GroupBy(item => NormalizeLookupCode(item.LookupCode), StringComparer.Ordinal)
                    .Select(group => group.Last())
                    .ToList();
                var deletedCount = previousItems.Count(previous =>
                    stagedItems.All(staged =>
                        !string.Equals(
                            NormalizeLookupCode(staged.LookupCode),
                            NormalizeLookupCode(previous.LookupCode),
                            StringComparison.Ordinal)));
                repository._itemsByStore[storeCode] = stagedItems;
                repository.StoreReplaceCommitCount++;
                return Task.FromResult(new LocalCatalogStoreReplaceCommitResult(stagedItems.Count, deletedCount));
            }

            public ValueTask DisposeAsync()
            {
                return ValueTask.CompletedTask;
            }

            private static string NormalizeLookupCode(string? value)
            {
                return (value ?? string.Empty).Trim().ToUpperInvariant();
            }
        }
    }

    private sealed class FakeCatalogApiClient : ICatalogApiClient
    {
        public Queue<CatalogSyncPageResponse> PageResponses { get; } = new();

        public Queue<CatalogCompareResponse> CompareResponses { get; } = new();

        public Queue<CatalogSpecialProductsPageResponse> SpecialProductsPageResponses { get; } = new();

        public Queue<CatalogPromotionsResponse> PromotionResponses { get; } = new();

        public List<(string StoreCode, string? Cursor, int PageSize)> PageRequests { get; } = [];

        public List<string> PromotionRequests { get; } = [];

        public Exception? CompareException { get; init; }

        public Exception? PageException { get; init; }

        public Queue<Exception> PageExceptionSequence { get; } = new();

        public Exception? LookupException { get; init; }

        public CatalogLookupResponse? LookupResponse { get; init; }

        public CatalogCompareRequest? LastCompareRequest { get; private set; }

        public Task<CatalogSyncPageResponse> GetSellableItemsPageAsync(
            string storeCode,
            string? cursor,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            PageRequests.Add((storeCode, cursor, pageSize));
            if (PageResponses.Count > 0)
            {
                return Task.FromResult(PageResponses.Dequeue());
            }

            if (PageExceptionSequence.Count > 0)
            {
                return Task.FromException<CatalogSyncPageResponse>(PageExceptionSequence.Dequeue());
            }

            return PageException is not null
                ? Task.FromException<CatalogSyncPageResponse>(PageException)
                : Task.FromException<CatalogSyncPageResponse>(new InvalidOperationException("No catalog page response was queued."));
        }

        public Task<CatalogCompareResponse> CompareSellableItemsAsync(
            CatalogCompareRequest request,
            CancellationToken cancellationToken = default)
        {
            LastCompareRequest = request;
            return CompareException is not null
                ? Task.FromException<CatalogCompareResponse>(CompareException)
                : Task.FromResult(CompareResponses.Dequeue());
        }

        public Task<CatalogSpecialProductsPageResponse> GetSpecialProductsPageAsync(
            string storeCode,
            string? cursor,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            PageRequests.Add((storeCode, cursor, pageSize));
            return Task.FromResult(SpecialProductsPageResponses.Dequeue());
        }

        public Task<CatalogLookupResponse?> LookupSellableItemAsync(
            string storeCode,
            string lookupCode,
            CancellationToken cancellationToken = default)
        {
            return LookupException is not null
                ? Task.FromException<CatalogLookupResponse?>(LookupException)
                : Task.FromResult(LookupResponse);
        }

        public Task<CatalogPromotionsResponse> GetPromotionRulesAsync(
            string storeCode,
            CancellationToken cancellationToken = default)
        {
            PromotionRequests.Add(storeCode);
            return PromotionResponses.Count > 0
                ? Task.FromResult(PromotionResponses.Dequeue())
                : Task.FromResult(new CatalogPromotionsResponse(storeCode, Timestamp, []));
        }

        public Queue<CatalogCodeConflictsResponse> CodeConflictResponses { get; } = new();

        public List<string> CodeConflictRequests { get; } = [];

        public Exception? CodeConflictsException { get; init; }

        public Task<CatalogCodeConflictsResponse> GetCodeConflictsAsync(
            string storeCode,
            CancellationToken cancellationToken = default)
        {
            CodeConflictRequests.Add(storeCode);
            if (CodeConflictsException is not null)
            {
                return Task.FromException<CatalogCodeConflictsResponse>(CodeConflictsException);
            }

            // 默认模拟"服务端尚未算出冲突"，既有同步用例不会触发本地冲突表写入。
            return CodeConflictResponses.Count > 0
                ? Task.FromResult(CodeConflictResponses.Dequeue())
                : Task.FromResult(new CatalogCodeConflictsResponse(storeCode, Timestamp, false, []));
        }

        public Task<CatalogSpecialProductMarkResponse> MarkSpecialProductAsync(
            CatalogSpecialProductMarkRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new CatalogSpecialProductMarkResponse(
                request.StoreCode,
                request.ProductCode,
                request.IsSpecialProduct,
                DateTimeOffset.UtcNow,
                []));
        }
    }

    private sealed class FakeLocalPromotionRepository : ILocalPromotionRepository
    {
        private readonly Dictionary<string, List<PromotionRuleDto>> _rulesByStore = new(StringComparer.OrdinalIgnoreCase);

        public List<(string StoreCode, PromotionRulesResponse Response)> ReplaceCalls { get; } = [];

        public Task ReplaceStoreRulesAsync(
            string storeCode,
            PromotionRulesResponse response,
            CancellationToken cancellationToken = default)
        {
            ReplaceCalls.Add((storeCode, response));
            _rulesByStore[storeCode] = response.Rules.ToList();
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<PromotionRuleDto>> GetActiveRulesAsync(
            string storeCode,
            DateTimeOffset asOf,
            CancellationToken cancellationToken = default)
        {
            var rules = _rulesByStore.TryGetValue(storeCode, out var storeRules)
                ? storeRules
                    .Where(rule => rule.EffectiveStart <= asOf.UtcDateTime && rule.EffectiveEnd >= asOf.UtcDateTime)
                    .ToArray()
                : [];
            return Task.FromResult<IReadOnlyList<PromotionRuleDto>>(rules);
        }

        public void SeedStore(string storeCode, IEnumerable<PromotionRuleDto> rules)
        {
            _rulesByStore[storeCode] = rules.ToList();
        }

        public string[] LoadStoreRuleIds(string storeCode)
        {
            return _rulesByStore.TryGetValue(storeCode, out var rules)
                ? rules.Select(rule => rule.Id).ToArray()
                : [];
        }
    }

    private sealed class FakePromotionApiClient : IPromotionApiClient
    {
        public Queue<PromotionRulesResponse> RulesResponses { get; } = new();

        public List<(string StoreCode, DateTimeOffset? AsOf)> Requests { get; } = [];

        public Exception? Exception { get; init; }

        public Task<PromotionRulesResponse> GetRulesAsync(
            string storeCode,
            DateTimeOffset? asOf = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add((storeCode, asOf));
            return Exception is not null
                ? Task.FromException<PromotionRulesResponse>(Exception)
                : Task.FromResult(RulesResponses.Dequeue());
        }
    }

    private sealed class CapturingProgress<T>(ICollection<T> reports) : IProgress<T>
    {
        public void Report(T value)
        {
            reports.Add(value);
        }
    }

    private sealed class CountingUiPriorityCoordinator : IUiPriorityCoordinator
    {
        public int WaitCount { get; private set; }

        public bool IsUiActive => false;

        public void NotifyUserInput()
        {
        }

        public IDisposable BeginUiOperation(string name)
        {
            return NoopDisposable.Instance;
        }

        public Task WaitForUiIdleAsync(CancellationToken cancellationToken = default)
        {
            WaitCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingUiPriorityCoordinator(List<string> events) : IUiPriorityCoordinator
    {
        public bool IsUiActive => false;

        public void NotifyUserInput()
        {
        }

        public IDisposable BeginUiOperation(string name)
        {
            return NoopDisposable.Instance;
        }

        public Task WaitForUiIdleAsync(CancellationToken cancellationToken = default)
        {
            events.Add("wait");
            return Task.CompletedTask;
        }
    }

    private sealed class GatedUiPriorityCoordinator : IUiPriorityCoordinator
    {
        private readonly TaskCompletionSource _releaseFirstWait = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _waitCount;

        public TaskCompletionSource FirstWaitStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsUiActive => false;

        public void NotifyUserInput()
        {
        }

        public IDisposable BeginUiOperation(string name)
        {
            return NoopDisposable.Instance;
        }

        public async Task WaitForUiIdleAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _waitCount) == 1)
            {
                FirstWaitStarted.SetResult();
                await _releaseFirstWait.Task.WaitAsync(cancellationToken);
            }
        }

        public void ReleaseFirstWait()
        {
            _releaseFirstWait.SetResult();
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();

        public void Dispose()
        {
        }
    }

    private static IDisposable CaptureClientLog(ConcurrentQueue<string> lines)
    {
        void Capture(string line)
        {
            lines.Enqueue(line);
        }

        ConsoleLog.LineWritten += Capture;
        return new DisposableAction(() => ConsoleLog.LineWritten -= Capture);
    }

    private static bool HasLog(ConcurrentQueue<string> lines, string text)
    {
        return lines.Any(line => line.Contains(text, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class DisposableAction(Action dispose) : IDisposable
    {
        public void Dispose()
        {
            dispose();
        }
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(handler(request, cancellationToken));
        }
    }
}
