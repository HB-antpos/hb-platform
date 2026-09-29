using System.Net;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Catalog;

namespace Hbpos.Client.Tests;

/// <summary>
/// WPF 目录同步走 iPad 同一套 sync-plan 协议：无变化只刷新附属数据，增量一次事务落库，全量锁定版本、预取下一页。
/// </summary>
public sealed class CatalogSyncPlanTests
{
    private const string Store = "1042";
    private static readonly DateTimeOffset Timestamp = new(2026, 9, 26, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task No_change_plan_downloads_nothing_but_still_refreshes_code_conflicts()
    {
        var repository = new VersionedCatalogRepository { CatalogVersion = "catalog-v1:a" };
        var api = new PlanCatalogApiClient();
        api.Plans.Enqueue(Plan(CatalogSyncModes.NoChange, baseVersion: "catalog-v1:a", target: "catalog-v1:a", total: 351076));
        api.CodeConflicts = Conflicts(available: true, Lookup("P-FLY", "6405090401470", 8.99m), Lookup("P-FLOWER", "6405090401470", 2.99m));
        var progress = new List<CatalogSyncProgress>();
        var service = new LocalCatalogSyncService(repository, api);

        var result = await service.FullSyncAsync(Store, progress: new CapturingProgress(progress));

        Assert.Equal([(Store, "catalog-v1:a")], api.PlanRequests);
        Assert.Empty(api.PinnedPageRequests);
        Assert.Empty(api.DeltaPageRequests);
        Assert.Equal(0, api.CompareRequestCount);
        Assert.Equal(0, repository.ComparePageRequestCount);
        Assert.Equal(1, api.CodeConflictRequestCount);
        Assert.Equal(1, api.PromotionRequestCount);
        Assert.Equal(CatalogSyncModes.NoChange, result.SyncMode);
        Assert.False(result.CatalogChanged);
        Assert.True(result.CodeConflictsChanged);
        Assert.Equal(["P-FLY", "P-FLOWER"], repository.CodeConflictItems.Select(item => item.ProductCode));
        var completed = Assert.Single(progress);
        Assert.Equal(CatalogSyncProgressStage.Completed, completed.Stage);
        Assert.Equal(100, completed.Percent);
        Assert.Equal(351076, completed.TotalCount);
    }

    [Fact]
    public async Task No_change_with_identical_code_conflicts_reports_nothing_changed()
    {
        var fly = Lookup("P-FLY", "6405090401470", 8.99m);
        var repository = new VersionedCatalogRepository { CatalogVersion = "catalog-v1:a" };
        repository.CodeConflictItems.Add(fly.ToSellableItemDto());
        var api = new PlanCatalogApiClient { CodeConflicts = Conflicts(available: true, fly) };
        api.Plans.Enqueue(Plan(CatalogSyncModes.NoChange, baseVersion: "catalog-v1:a", target: "catalog-v1:a", total: 10));

        var result = await new LocalCatalogSyncService(repository, api).FullSyncAsync(Store);

        Assert.False(result.CatalogChanged);
        Assert.False(result.CodeConflictsChanged);
    }

    [Fact]
    public async Task Delta_plan_applies_all_operations_in_one_versioned_call()
    {
        var repository = new VersionedCatalogRepository { CatalogVersion = "catalog-v1:a" };
        var api = new PlanCatalogApiClient();
        api.Plans.Enqueue(Plan(CatalogSyncModes.Delta, "catalog-v1:a", "catalog-v1:b", total: 351077, lease: "lease-1", deltaOperations: 2));
        api.DeltaPages.Enqueue(DeltaPage("catalog-v1:a", "catalog-v1:b", [Lookup("P-NEW", "9300000000001", 4.5m)], [Deleted("OLD-CODE")]));

        var result = await new LocalCatalogSyncService(repository, api).FullSyncAsync(Store);

        var request = Assert.Single(api.DeltaPageRequests);
        Assert.Equal(("catalog-v1:a", "catalog-v1:b", "lease-1", (string?)null, 5000), request);
        var applied = Assert.Single(repository.DeltaApplyCalls);
        Assert.Equal("catalog-v1:a", applied.Base);
        Assert.Equal("catalog-v1:b", applied.Target);
        Assert.Equal(["P-NEW"], applied.Upserts.Select(item => item.ProductCode));
        Assert.Equal(["OLD-CODE"], applied.Deletes);
        Assert.Equal("catalog-v1:b", repository.CatalogVersion);
        Assert.Empty(api.PinnedPageRequests);
        Assert.Equal(CatalogSyncModes.Delta, result.SyncMode);
        Assert.True(result.CatalogChanged);
        Assert.Equal((1, 1), (result.UpsertedCount, result.DeletedCount));
    }

    [Theory]
    [InlineData("incomplete")]
    [InlineData("snapshot-expired")]
    [InlineData("local-version-changed")]
    public async Task Delta_problems_fall_back_to_a_pinned_full_download(string failure)
    {
        var repository = new VersionedCatalogRepository { CatalogVersion = "catalog-v1:a" };
        repository.Seed(Lookup("P-OLD", "OLD", 1m).ToSellableItemDto());
        var api = new PlanCatalogApiClient();
        api.Plans.Enqueue(Plan(CatalogSyncModes.Delta, "catalog-v1:a", "catalog-v1:b", total: 2, lease: "lease-delta", deltaOperations: 2));
        api.Plans.Enqueue(Plan(CatalogSyncModes.Full, baseVersion: null, target: "catalog-v1:b", total: 2, lease: "lease-full"));
        switch (failure)
        {
            case "incomplete":
                // 计划说 2 个操作，实际只收到 1 个：不能把残缺增量当成完整版本。
                api.DeltaPages.Enqueue(DeltaPage("catalog-v1:a", "catalog-v1:b", [Lookup("P-A", "A", 1m)], []));
                break;
            case "snapshot-expired":
                api.DeltaException = new CatalogApiException("expired", HttpStatusCode.Conflict, "CATALOG_SNAPSHOT_EXPIRED");
                break;
            case "local-version-changed":
                api.DeltaPages.Enqueue(DeltaPage("catalog-v1:a", "catalog-v1:b", [Lookup("P-A", "A", 1m)], [Deleted("OLD")]));
                repository.DeltaApplyException = new LocalCatalogVersionConflictException("base changed");
                break;
        }

        api.PinnedPages[""] = PinnedPage("catalog-v1:b", "lease-full", 2, next: null, Lookup("P-A", "A", 1m), Lookup("P-B", "B", 2m));

        var result = await new LocalCatalogSyncService(repository, api).FullSyncAsync(Store);

        Assert.Equal([(Store, "catalog-v1:a"), (Store, null)], api.PlanRequests);
        Assert.Equal(CatalogSyncModes.Full, result.SyncMode);
        Assert.Equal(("catalog-v1:b", 2), repository.CommittedStamp);
        Assert.Equal("catalog-v1:b", repository.CatalogVersion);
        Assert.Equal(["A", "B"], repository.Items.Select(item => item.LookupCode));
    }

    [Fact]
    public async Task Full_plan_pins_version_and_lease_and_prefetches_the_next_page_before_staging()
    {
        var events = new List<string>();
        var repository = new VersionedCatalogRepository(events);
        var api = new PlanCatalogApiClient(events);
        api.Plans.Enqueue(Plan(CatalogSyncModes.Full, baseVersion: null, target: "catalog-v1:b", total: 3, lease: "lease-1"));
        api.PinnedPages[""] = PinnedPage("catalog-v1:b", "lease-1", 3, next: "B", Lookup("P-A", "A", 1m), Lookup("P-B", "B", 2m));
        api.PinnedPages["B"] = PinnedPage("catalog-v1:b", "lease-1", 3, next: null, Lookup("P-C", "C", 3m));
        var progress = new List<CatalogSyncProgress>();

        var result = await new LocalCatalogSyncService(repository, api).FullSyncAsync(Store, progress: new CapturingProgress(progress));

        Assert.Equal(
            [("catalog-v1:b", "lease-1", (string?)null, 5000), ("catalog-v1:b", "lease-1", "B", 5000)],
            api.PinnedPageRequests);
        // 预取：第一页写入本地之前，第二页的请求已经发出。
        Assert.Equal(["page:<start>", "page:B", "stage:2", "stage:1", "commit:catalog-v1:b/3"], events);
        Assert.Equal(("catalog-v1:b", 3), repository.CommittedStamp);
        Assert.Equal(CatalogSyncModes.Full, result.SyncMode);
        Assert.True(result.CatalogChanged);
        Assert.Equal(2, result.RemotePages);
        Assert.All(
            progress.Where(report => report.Stage == CatalogSyncProgressStage.Downloading),
            report => Assert.Equal(3, report.TotalCount));
        Assert.Equal(100, progress[^1].Percent);
    }

    [Fact]
    public async Task Reset_ignores_the_local_version_and_asks_for_a_full_plan()
    {
        var repository = new VersionedCatalogRepository { CatalogVersion = "catalog-v1:a" };
        var api = new PlanCatalogApiClient();
        api.Plans.Enqueue(Plan(CatalogSyncModes.Full, baseVersion: null, target: "catalog-v1:a", total: 1, lease: "lease-1"));
        api.PinnedPages[""] = PinnedPage("catalog-v1:a", "lease-1", 1, next: null, Lookup("P-A", "A", 1m));

        await new LocalCatalogSyncService(repository, api).FullSyncAsync(Store, forceFullDownload: true);

        Assert.Equal([(Store, (string?)null)], api.PlanRequests);
        Assert.Equal(("catalog-v1:a", 1), repository.CommittedStamp);
    }

    [Fact]
    public async Task Full_download_restarts_once_when_the_pinned_snapshot_expires_midway()
    {
        var repository = new VersionedCatalogRepository();
        var api = new PlanCatalogApiClient();
        api.Plans.Enqueue(Plan(CatalogSyncModes.Full, baseVersion: null, target: "catalog-v1:a", total: 2, lease: "lease-1"));
        api.Plans.Enqueue(Plan(CatalogSyncModes.Full, baseVersion: null, target: "catalog-v1:b", total: 1, lease: "lease-2"));
        api.PinnedPages[""] = PinnedPage("catalog-v1:a", "lease-1", 2, next: "A", Lookup("P-A", "A", 1m));
        api.PinnedPageExceptions["A"] = new CatalogApiException("expired", HttpStatusCode.Conflict, "CATALOG_SNAPSHOT_EXPIRED");
        api.PinnedPagesByVersion[("catalog-v1:b", "")] = PinnedPage("catalog-v1:b", "lease-2", 1, next: null, Lookup("P-Z", "Z", 9m));

        await new LocalCatalogSyncService(repository, api).FullSyncAsync(Store);

        Assert.Equal(2, api.PlanRequests.Count);
        Assert.Equal(2, repository.ReplaceSessionCount);
        Assert.Equal(("catalog-v1:b", 1), repository.CommittedStamp);
        Assert.Equal(["Z"], repository.Items.Select(item => item.LookupCode));
    }

    [Fact]
    public async Task Page_total_that_disagrees_with_the_plan_fails_without_replacing_the_catalog()
    {
        var repository = new VersionedCatalogRepository { CatalogVersion = "catalog-v1:old" };
        repository.Seed(Lookup("P-OLD", "OLD", 1m).ToSellableItemDto());
        var api = new PlanCatalogApiClient();
        api.Plans.Enqueue(Plan(CatalogSyncModes.Full, baseVersion: "catalog-v1:old", target: "catalog-v1:b", total: 5, lease: "lease-1"));
        api.PinnedPages[""] = PinnedPage("catalog-v1:b", "lease-1", 4, next: null, Lookup("P-A", "A", 1m));
        var progress = new List<CatalogSyncProgress>();

        var exception = await Assert.ThrowsAsync<CatalogApiException>(() =>
            new LocalCatalogSyncService(repository, api).FullSyncAsync(Store, progress: new CapturingProgress(progress)));

        Assert.Equal("CATALOG_PAGE_TOTAL_MISMATCH", exception.ErrorCode);
        Assert.Null(repository.CommittedStamp);
        Assert.Equal("catalog-v1:old", repository.CatalogVersion);
        Assert.Equal(["OLD"], repository.Items.Select(item => item.LookupCode));
        Assert.Equal(CatalogSyncProgressStage.Failed, progress[^1].Stage);
        Assert.Equal(0, api.CodeConflictRequestCount);
    }

    [Fact]
    public async Task Server_without_sync_plan_falls_back_to_the_legacy_compare_flow_and_clears_the_version()
    {
        var repository = new VersionedCatalogRepository { CatalogVersion = "catalog-v1:stale" };
        var api = new PlanCatalogApiClient
        {
            PlanException = new CatalogApiException("Catalog API request failed with HTTP 404.", HttpStatusCode.NotFound)
        };
        api.LegacyPages.Enqueue(new CatalogSyncPageResponse(Store, Timestamp, null, [], [], null, false, 0));

        var result = await new LocalCatalogSyncService(repository, api).FullSyncAsync(Store);

        Assert.Equal(LocalCatalogSyncModes.Legacy, result.SyncMode);
        Assert.True(result.CatalogChanged);
        Assert.Equal(1, repository.ComparePageRequestCount);
        Assert.Null(repository.CatalogVersion);
        Assert.Empty(api.PinnedPageRequests);
    }

    [Fact]
    public async Task Store_not_found_is_an_error_not_a_legacy_server()
    {
        var repository = new VersionedCatalogRepository();
        var api = new PlanCatalogApiClient
        {
            PlanException = new CatalogApiException("store was not found or inactive", HttpStatusCode.NotFound, "STORE_NOT_FOUND")
        };

        var exception = await Assert.ThrowsAsync<CatalogApiException>(() => new LocalCatalogSyncService(repository, api).FullSyncAsync(Store));

        Assert.Equal("STORE_NOT_FOUND", exception.ErrorCode);
        Assert.Equal(0, repository.ComparePageRequestCount);
    }

    private static CatalogSyncPlanResponse Plan(
        string mode,
        string? baseVersion,
        string target,
        int total,
        string? lease = null,
        int? deltaOperations = null)
    {
        return new CatalogSyncPlanResponse(Store, Timestamp, mode, baseVersion, target, total, lease, deltaOperations);
    }

    private static CatalogSyncPageResponse PinnedPage(
        string version,
        string lease,
        int total,
        string? next,
        params CatalogLookupItemDto[] items)
    {
        return new CatalogSyncPageResponse(
            Store,
            Timestamp,
            null,
            items,
            [],
            next,
            next is not null,
            total,
            version,
            CatalogPageChecksums.ComputeSellablePageV2(items),
            lease);
    }

    private static CatalogDeltaPageResponse DeltaPage(
        string baseVersion,
        string targetVersion,
        CatalogLookupItemDto[] upserts,
        DeletedLookupDto[] deletes)
    {
        return new CatalogDeltaPageResponse(
            Store,
            Timestamp,
            baseVersion,
            targetVersion,
            null,
            upserts,
            deletes,
            null,
            false,
            0,
            CatalogPageChecksums.ComputeDeltaPageV1(baseVersion, targetVersion, upserts, deletes));
    }

    private static CatalogCodeConflictsResponse Conflicts(bool available, params CatalogLookupItemDto[] items)
    {
        return new CatalogCodeConflictsResponse(Store, Timestamp, available, items);
    }

    private static CatalogLookupItemDto Lookup(string productCode, string lookupCode, decimal price)
    {
        return new CatalogLookupItemDto(
            Store,
            productCode,
            ReferenceCode: null,
            productCode,
            lookupCode,
            lookupCode.Trim().ToUpperInvariant(),
            ItemNumber: productCode,
            Barcode: lookupCode,
            RetailPrice: price,
            PriceSourceKind.StoreRetailPrice,
            "store-retail",
            QuantityFactor: 1m,
            UpdatedAt: Timestamp,
            RowVersion: null);
    }

    private static DeletedLookupDto Deleted(string lookupCode)
    {
        return new DeletedLookupDto(Store, lookupCode, lookupCode.ToUpperInvariant(), Timestamp);
    }

    private sealed class CapturingProgress(List<CatalogSyncProgress> reports) : IProgress<CatalogSyncProgress>
    {
        public void Report(CatalogSyncProgress value)
        {
            reports.Add(value);
        }
    }

    private sealed class PlanCatalogApiClient(List<string>? events = null) : ICatalogApiClient
    {
        public Queue<CatalogSyncPlanResponse> Plans { get; } = new();

        public Exception? PlanException { get; init; }

        public List<(string StoreCode, string? BaseCatalogVersion)> PlanRequests { get; } = [];

        public Dictionary<string, CatalogSyncPageResponse> PinnedPages { get; } = new(StringComparer.Ordinal);

        public Dictionary<(string Version, string Cursor), CatalogSyncPageResponse> PinnedPagesByVersion { get; } = [];

        public Dictionary<string, Exception> PinnedPageExceptions { get; } = new(StringComparer.Ordinal);

        public List<(string Version, string? Lease, string? Cursor, int PageSize)> PinnedPageRequests { get; } = [];

        public Queue<CatalogDeltaPageResponse> DeltaPages { get; } = new();

        public Exception? DeltaException { get; set; }

        public List<(string Base, string Target, string? Lease, string? Cursor, int PageSize)> DeltaPageRequests { get; } = [];

        public Queue<CatalogSyncPageResponse> LegacyPages { get; } = new();

        public CatalogCodeConflictsResponse? CodeConflicts { get; set; }

        public int CodeConflictRequestCount { get; private set; }

        public int PromotionRequestCount { get; private set; }

        public int CompareRequestCount { get; private set; }

        public Task<CatalogSyncPlanResponse> GetCatalogSyncPlanAsync(
            string storeCode,
            string? baseCatalogVersion,
            CancellationToken cancellationToken = default)
        {
            PlanRequests.Add((storeCode, baseCatalogVersion));
            return PlanException is not null
                ? Task.FromException<CatalogSyncPlanResponse>(PlanException)
                : Task.FromResult(Plans.Dequeue());
        }

        public Task<CatalogSyncPageResponse> GetPinnedSellableItemsPageAsync(
            string storeCode,
            string catalogVersion,
            string? downloadLeaseId,
            string? cursor,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            PinnedPageRequests.Add((catalogVersion, downloadLeaseId, cursor, pageSize));
            events?.Add($"page:{cursor ?? "<start>"}");
            var key = cursor ?? string.Empty;
            if (PinnedPageExceptions.Remove(key, out var exception))
            {
                return Task.FromException<CatalogSyncPageResponse>(exception);
            }

            if (PinnedPagesByVersion.TryGetValue((catalogVersion, key), out var versionedPage))
            {
                return Task.FromResult(versionedPage);
            }

            return Task.FromResult(PinnedPages[key]);
        }

        public Task<CatalogDeltaPageResponse> GetCatalogDeltaPageAsync(
            string storeCode,
            string baseCatalogVersion,
            string targetCatalogVersion,
            string? downloadLeaseId,
            string? cursor,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            DeltaPageRequests.Add((baseCatalogVersion, targetCatalogVersion, downloadLeaseId, cursor, pageSize));
            return DeltaException is not null
                ? Task.FromException<CatalogDeltaPageResponse>(DeltaException)
                : Task.FromResult(DeltaPages.Dequeue());
        }

        public Task<CatalogSyncPageResponse> GetSellableItemsPageAsync(
            string storeCode,
            string? cursor,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(LegacyPages.Dequeue());
        }

        public Task<CatalogCompareResponse> CompareSellableItemsAsync(
            CatalogCompareRequest request,
            CancellationToken cancellationToken = default)
        {
            CompareRequestCount++;
            return Task.FromResult(new CatalogCompareResponse(request.StoreCode, Timestamp, [], [], null, false));
        }

        public Task<CatalogPromotionsResponse> GetPromotionRulesAsync(
            string storeCode,
            CancellationToken cancellationToken = default)
        {
            PromotionRequestCount++;
            return Task.FromResult(new CatalogPromotionsResponse(storeCode, Timestamp, []));
        }

        public Task<CatalogSpecialProductsPageResponse> GetSpecialProductsPageAsync(
            string storeCode,
            string? cursor,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<CatalogCodeConflictsResponse> GetCodeConflictsAsync(
            string storeCode,
            CancellationToken cancellationToken = default)
        {
            CodeConflictRequestCount++;
            return Task.FromResult(CodeConflicts ?? Conflicts(available: false));
        }

        public Task<CatalogLookupResponse?> LookupSellableItemAsync(
            string storeCode,
            string lookupCode,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<CatalogSpecialProductMarkResponse> MarkSpecialProductAsync(
            CatalogSpecialProductMarkRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class VersionedCatalogRepository(List<string>? events = null) : ILocalCatalogRepository
    {
        private readonly List<SellableItemDto> _items = [];

        public string? CatalogVersion { get; set; }

        public IReadOnlyList<SellableItemDto> Items => _items;

        public List<SellableItemDto> CodeConflictItems { get; } = [];

        public int ComparePageRequestCount { get; private set; }

        public int ReplaceSessionCount { get; private set; }

        public (string Version, int ExpectedCount)? CommittedStamp { get; private set; }

        public Exception? DeltaApplyException { get; set; }

        public List<(string Base, string Target, IReadOnlyList<SellableItemDto> Upserts, IReadOnlyList<string> Deletes)> DeltaApplyCalls { get; } = [];

        public void Seed(params SellableItemDto[] items)
        {
            _items.AddRange(items);
        }

        public Task<string?> GetCatalogVersionAsync(string storeCode, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CatalogVersion);
        }

        public Task ClearCatalogVersionAsync(string storeCode, CancellationToken cancellationToken = default)
        {
            CatalogVersion = null;
            return Task.CompletedTask;
        }

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

        public Task<ILocalCatalogStoreReplaceSession> BeginStoreReplaceSessionAsync(
            string storeCode,
            CancellationToken cancellationToken = default)
        {
            ReplaceSessionCount++;
            return Task.FromResult<ILocalCatalogStoreReplaceSession>(new Session(this));
        }

        public Task<bool> ReplaceCodeConflictItemsIfChangedAsync(
            string storeCode,
            IEnumerable<SellableItemDto> items,
            CancellationToken cancellationToken = default)
        {
            var incoming = items.ToArray();
            if (incoming.SequenceEqual(CodeConflictItems))
            {
                return Task.FromResult(false);
            }

            CodeConflictItems.Clear();
            CodeConflictItems.AddRange(incoming);
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<LocalSellableItemCompareRow>> LoadSellableItemComparePageAsync(
            string storeCode,
            string? afterLookupCodeNormalized,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            ComparePageRequestCount++;
            return Task.FromResult<IReadOnlyList<LocalSellableItemCompareRow>>([]);
        }

        public Task ReplacePromotionRulesAsync(
            string storeCode,
            IEnumerable<CatalogPromotionRuleDto> rules,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

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

        private sealed class Session(VersionedCatalogRepository repository) : ILocalCatalogStoreReplaceSession
        {
            private readonly List<SellableItemDto> _staged = [];

            public Task StageAsync(IEnumerable<SellableItemDto> items, CancellationToken cancellationToken = default)
            {
                var batch = items.ToArray();
                repository.RecordEvent($"stage:{batch.Length}");
                _staged.AddRange(batch);
                return Task.CompletedTask;
            }

            public Task<LocalCatalogStoreReplaceCommitResult> CommitAsync(CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException("Planned sync must commit with a version stamp.");
            }

            public Task<LocalCatalogStoreReplaceCommitResult> CommitAsync(
                LocalCatalogVersionStamp versionStamp,
                CancellationToken cancellationToken = default)
            {
                repository.RecordEvent($"commit:{versionStamp.CatalogVersion}/{versionStamp.ExpectedItemCount}");
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

            public ValueTask DisposeAsync()
            {
                return ValueTask.CompletedTask;
            }
        }

        private void RecordEvent(string value)
        {
            events?.Add(value);
        }
    }
}
