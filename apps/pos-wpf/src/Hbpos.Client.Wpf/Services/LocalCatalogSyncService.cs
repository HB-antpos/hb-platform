using System.Diagnostics;
using System.Globalization;
using System.Net;
using Hbpos.Contracts.Catalog;

namespace Hbpos.Client.Wpf.Services;

public interface ILocalCatalogSyncService
{
    Task<LocalCatalogSyncResult> FullSyncAsync(
        string storeCode,
        CancellationToken cancellationToken = default,
        IProgress<CatalogSyncProgress>? progress = null,
        bool forceFullDownload = false);
}

/// <summary>
/// CatalogChanged/CodeConflictsChanged 为 false 时本地目录与冲突候选都没有写入，调用方可跳过扫码索引重建。
/// 旧协议无法判断是否变化，默认按"有变化"处理。
/// </summary>
public sealed record LocalCatalogSyncResult(
    string StoreCode,
    int ComparePages,
    int RemotePages,
    int UpsertedCount,
    int DeletedCount,
    string SyncMode = LocalCatalogSyncModes.Legacy,
    bool CatalogChanged = true,
    bool CodeConflictsChanged = true);

public static class LocalCatalogSyncModes
{
    public const string Legacy = "legacy";
    public const string NoChange = CatalogSyncModes.NoChange;
    public const string Delta = CatalogSyncModes.Delta;
    public const string Full = CatalogSyncModes.Full;
}

public enum CatalogSyncProgressStage
{
    Preparing,
    Comparing,
    Downloading,
    Completed,
    Failed
}

public sealed record CatalogSyncProgress(
    string StoreCode,
    CatalogSyncProgressStage Stage,
    int TotalCount,
    int DownloadedCount,
    int Percent,
    int ComparePages,
    int RemotePages,
    int UpsertedCount,
    int DeletedCount,
    long ElapsedMilliseconds,
    string? ErrorMessage = null)
{
    public int ComparedCount { get; init; }
}

public sealed class LocalCatalogSyncService(
    ILocalCatalogRepository localCatalogRepository,
    ICatalogApiClient catalogApiClient,
    IUiPriorityCoordinator? uiPriorityCoordinator = null,
    ILocalPromotionRepository? localPromotionRepository = null,
    IPromotionApiClient? promotionApiClient = null) : ILocalCatalogSyncService
{
    private const int ComparePageSize = 2000;
    // 与服务端 v2 标准页大小一致，才能命中服务端缓存的标准页与摘要。
    private const int DownloadPageSize = 5000;
    private const int ApplyBatchSize = 2000;
    private readonly IUiPriorityCoordinator _uiPriorityCoordinator = uiPriorityCoordinator ?? UiPriorityCoordinator.Noop;

    public async Task<LocalCatalogSyncResult> FullSyncAsync(
        string storeCode,
        CancellationToken cancellationToken = default,
        IProgress<CatalogSyncProgress>? progress = null,
        bool forceFullDownload = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeCode);
        var totalStopwatch = Stopwatch.StartNew();
        // 中文注释：与 iPad 同一协议——先问服务端要同步计划（无变化/增量/全量），只下载真正需要的数据；
        // 重置数据时不带基准版本，服务端必然给全量。
        var baseCatalogVersion = forceFullDownload
            ? null
            : await localCatalogRepository.GetCatalogVersionAsync(storeCode, cancellationToken);
        CatalogSyncPlanResponse plan;
        try
        {
            plan = await catalogApiClient.GetCatalogSyncPlanAsync(storeCode, baseCatalogVersion, cancellationToken);
        }
        catch (CatalogApiException ex) when (IsSyncPlanUnavailable(ex))
        {
            Log($"sync plan unavailable store={storeCode} status={FormatStatus(ex)} errorCode={ex.ErrorCode ?? "<none>"} fallback=legacy-compare");
            return await LegacyFullSyncAsync(storeCode, cancellationToken, progress, forceFullDownload);
        }

        return await PlannedSyncAsync(
            storeCode,
            plan,
            baseCatalogVersion,
            forceFullDownload,
            progress,
            totalStopwatch,
            cancellationToken);
    }

    private async Task<LocalCatalogSyncResult> PlannedSyncAsync(
        string storeCode,
        CatalogSyncPlanResponse plan,
        string? baseCatalogVersion,
        bool forceFullDownload,
        IProgress<CatalogSyncProgress>? progress,
        Stopwatch totalStopwatch,
        CancellationToken cancellationToken)
    {
        var counters = new PlannedSyncCounters();
        var syncMode = plan.Mode;
        try
        {
            Log($"planned sync start store={storeCode} mode={plan.Mode} base={baseCatalogVersion ?? "<none>"} target={plan.TargetCatalogVersion} total={plan.TargetTotal} deltaOperations={plan.DeltaOperationCount?.ToString(CultureInfo.InvariantCulture) ?? "<none>"} forceFullDownload={forceFullDownload}");
            ValidatePlan(storeCode, plan);
            switch (plan.Mode)
            {
                case CatalogSyncModes.NoChange:
                    if (baseCatalogVersion is null ||
                        !string.Equals(plan.TargetCatalogVersion, baseCatalogVersion, StringComparison.Ordinal))
                    {
                        throw new CatalogApiException(
                            "Catalog sync plan reported no change for a different catalog version.",
                            HttpStatusCode.OK,
                            "CATALOG_SYNC_PLAN_INVALID");
                    }

                    counters.TotalCount = plan.TargetTotal;
                    counters.DownloadedCount = plan.TargetTotal;
                    Log($"catalog unchanged store={storeCode} version={plan.TargetCatalogVersion} total={plan.TargetTotal}");
                    break;
                case CatalogSyncModes.Delta:
                    try
                    {
                        await DownloadAndApplyDeltaAsync(storeCode, plan, baseCatalogVersion, counters, progress, totalStopwatch, cancellationToken);
                    }
                    catch (Exception ex) when (IsDeltaFallback(ex))
                    {
                        // 中文注释：基准快照过期、本地版本被改或增量页不完整时，丢弃这次增量改走全量，本地数据保持不变。
                        Log($"delta sync fallback store={storeCode} reason={ex.GetType().Name} errorCode={(ex as CatalogApiException)?.ErrorCode ?? "<none>"} error={ex.Message}");
                        counters.Reset();
                        syncMode = CatalogSyncModes.Full;
                        var fullPlan = await RequestFullPlanAsync(storeCode, cancellationToken);
                        await DownloadFullWithRetryAsync(storeCode, fullPlan, counters, progress, totalStopwatch, cancellationToken);
                    }

                    break;
                case CatalogSyncModes.Full:
                    await DownloadFullWithRetryAsync(storeCode, plan, counters, progress, totalStopwatch, cancellationToken);
                    break;
            }

            var codeConflictsChanged = await SyncAuxiliaryDataAsync(storeCode, cancellationToken);
            var catalogChanged = syncMode != CatalogSyncModes.NoChange;
            totalStopwatch.Stop();
            Log($"planned sync completed store={storeCode} mode={syncMode} remotePages={counters.RemotePages} upserted={counters.UpsertedCount} deleted={counters.DeletedCount} catalogChanged={catalogChanged} codeConflictsChanged={codeConflictsChanged} elapsedMs={totalStopwatch.ElapsedMilliseconds}");
            ReportProgress(
                progress,
                storeCode,
                CatalogSyncProgressStage.Completed,
                counters.TotalCount,
                counters.TotalCount == 0 ? 0 : Math.Max(counters.DownloadedCount, counters.TotalCount),
                comparePages: 0,
                counters.RemotePages,
                counters.UpsertedCount,
                counters.DeletedCount,
                totalStopwatch,
                forceComplete: true);
            return new LocalCatalogSyncResult(
                storeCode,
                ComparePages: 0,
                counters.RemotePages,
                counters.UpsertedCount,
                counters.DeletedCount,
                syncMode,
                catalogChanged,
                codeConflictsChanged);
        }
        catch (OperationCanceledException)
        {
            totalStopwatch.Stop();
            Log($"planned sync canceled store={storeCode} mode={syncMode} remotePages={counters.RemotePages} elapsedMs={totalStopwatch.ElapsedMilliseconds}");
            throw;
        }
        catch (Exception ex)
        {
            totalStopwatch.Stop();
            Log($"planned sync failed store={storeCode} mode={syncMode} remotePages={counters.RemotePages} elapsedMs={totalStopwatch.ElapsedMilliseconds} error={ex.Message}");
            ReportProgress(
                progress,
                storeCode,
                CatalogSyncProgressStage.Failed,
                counters.TotalCount,
                counters.DownloadedCount,
                comparePages: 0,
                counters.RemotePages,
                counters.UpsertedCount,
                counters.DeletedCount,
                totalStopwatch,
                ex.Message);
            throw;
        }
    }

    private async Task DownloadAndApplyDeltaAsync(
        string storeCode,
        CatalogSyncPlanResponse plan,
        string? baseCatalogVersion,
        PlannedSyncCounters counters,
        IProgress<CatalogSyncProgress>? progress,
        Stopwatch totalStopwatch,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(baseCatalogVersion))
        {
            throw new LocalCatalogVersionConflictException("Catalog delta plan requires a local base catalog version.");
        }

        var upserts = new List<CatalogLookupItemDto>();
        var deletes = new List<DeletedLookupDto>();
        counters.TotalCount = plan.DeltaOperationCount ?? 0;
        string? cursor = null;
        while (true)
        {
            await _uiPriorityCoordinator.WaitForUiIdleAsync(cancellationToken);
            var page = await catalogApiClient.GetCatalogDeltaPageAsync(
                storeCode,
                baseCatalogVersion,
                plan.TargetCatalogVersion,
                plan.DownloadLeaseId,
                cursor,
                DownloadPageSize,
                cancellationToken);
            counters.RemotePages++;
            upserts.AddRange(page.Items);
            deletes.AddRange(page.DeletedLookups);
            counters.DownloadedCount = upserts.Count + deletes.Count;
            ReportPlannedDownloadProgress(progress, storeCode, counters, totalStopwatch);
            if (!page.HasMore)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(page.NextCursor))
            {
                throw new CatalogApiException("Catalog delta API indicated more pages but did not return a next cursor.");
            }

            cursor = page.NextCursor;
        }

        if (plan.DeltaOperationCount is { } expectedOperations &&
            upserts.Count + deletes.Count != expectedOperations)
        {
            throw new CatalogApiException(
                $"Catalog delta download is incomplete. expected={expectedOperations} received={upserts.Count + deletes.Count}",
                HttpStatusCode.OK,
                "CATALOG_DELTA_INCOMPLETE");
        }

        await _uiPriorityCoordinator.WaitForUiIdleAsync(cancellationToken);
        var applyStopwatch = Stopwatch.StartNew();
        var applied = await localCatalogRepository.ApplyCatalogDeltaAsync(
            storeCode,
            baseCatalogVersion,
            plan.TargetCatalogVersion,
            upserts.Select(item => item.ToSellableItemDto()).ToArray(),
            deletes.Select(GetDeleteLookupCode).ToArray(),
            cancellationToken);
        applyStopwatch.Stop();
        counters.UpsertedCount = applied.UpsertedCount;
        counters.DeletedCount = applied.DeletedCount;
        // 中文注释：扫码回查会把实时商品写进本地表，本地条数可能与版本总数略有出入，只记录不回退。
        Log($"delta applied store={storeCode} base={baseCatalogVersion} target={plan.TargetCatalogVersion} upserted={applied.UpsertedCount} deleted={applied.DeletedCount} localCount={applied.LocalItemCount} targetTotal={plan.TargetTotal} applyElapsedMs={applyStopwatch.ElapsedMilliseconds}");
    }

    private async Task DownloadFullWithRetryAsync(
        string storeCode,
        CatalogSyncPlanResponse plan,
        PlannedSyncCounters counters,
        IProgress<CatalogSyncProgress>? progress,
        Stopwatch totalStopwatch,
        CancellationToken cancellationToken)
    {
        try
        {
            await DownloadFullAsync(storeCode, plan, counters, progress, totalStopwatch, cancellationToken);
        }
        catch (CatalogApiException ex) when (IsSnapshotExpired(ex))
        {
            // 中文注释：下载途中锁定的版本被服务端回收（租约超时等），重新要计划后整体重下一次；暂存数据随会话丢弃。
            Log($"full download snapshot expired store={storeCode} version={plan.TargetCatalogVersion} retry=1");
            counters.Reset();
            var retryPlan = await RequestFullPlanAsync(storeCode, cancellationToken);
            await DownloadFullAsync(storeCode, retryPlan, counters, progress, totalStopwatch, cancellationToken);
        }
    }

    private async Task DownloadFullAsync(
        string storeCode,
        CatalogSyncPlanResponse plan,
        PlannedSyncCounters counters,
        IProgress<CatalogSyncProgress>? progress,
        Stopwatch totalStopwatch,
        CancellationToken cancellationToken)
    {
        counters.TotalCount = plan.TargetTotal;
        ReportPlannedDownloadProgress(progress, storeCode, counters, totalStopwatch);
        await using var replaceSession = await localCatalogRepository.BeginStoreReplaceSessionAsync(
            storeCode,
            cancellationToken);
        using var prefetchCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // 中文注释：写入当前页的同时预取下一页，网络与本地写库重叠；新目录在最后一个事务里整体替换，期间收银照常读旧目录。
        Task<CatalogSyncPageResponse>? pendingPage = FetchPinnedPageAsync(storeCode, plan, cursor: null, prefetchCts.Token);
        try
        {
            while (pendingPage is not null)
            {
                var page = await pendingPage;
                pendingPage = null;
                if (page.TotalCount != plan.TargetTotal)
                {
                    throw new CatalogApiException(
                        $"Catalog page total does not match the sync plan. plan={plan.TargetTotal} page={page.TotalCount}",
                        HttpStatusCode.OK,
                        "CATALOG_PAGE_TOTAL_MISMATCH");
                }

                if (page.HasMore)
                {
                    if (string.IsNullOrWhiteSpace(page.NextCursor))
                    {
                        throw new CatalogApiException("Catalog API indicated more pages but did not return a next cursor.");
                    }

                    pendingPage = FetchPinnedPageAsync(storeCode, plan, page.NextCursor, prefetchCts.Token);
                }

                var stageStopwatch = Stopwatch.StartNew();
                var stagedItems = page.Items
                    .Select(item => item.ToSellableItemDto())
                    .ToArray();
                foreach (var batch in stagedItems.Chunk(ApplyBatchSize))
                {
                    await _uiPriorityCoordinator.WaitForUiIdleAsync(cancellationToken);
                    await replaceSession.StageAsync(batch, cancellationToken);
                    counters.DownloadedCount += batch.Length;
                    counters.UpsertedCount += batch.Length;
                    ReportPlannedDownloadProgress(progress, storeCode, counters, totalStopwatch);
                }

                stageStopwatch.Stop();
                counters.RemotePages++;
                Log($"download page staged store={storeCode} page={counters.RemotePages} staged={stagedItems.Length} downloaded={counters.DownloadedCount}/{plan.TargetTotal} prefetching={pendingPage is not null} stageElapsedMs={stageStopwatch.ElapsedMilliseconds} mode=pinned");
            }

            var commitStopwatch = Stopwatch.StartNew();
            await _uiPriorityCoordinator.WaitForUiIdleAsync(cancellationToken);
            var commitResult = await replaceSession.CommitAsync(
                new LocalCatalogVersionStamp(plan.TargetCatalogVersion, plan.TargetTotal),
                cancellationToken);
            commitStopwatch.Stop();
            counters.UpsertedCount = commitResult.InsertedCount;
            counters.DeletedCount = commitResult.DeletedCount;
            Log($"snapshot committed store={storeCode} version={plan.TargetCatalogVersion} inserted={commitResult.InsertedCount} deleted={commitResult.DeletedCount} commitElapsedMs={commitStopwatch.ElapsedMilliseconds}");
        }
        finally
        {
            if (pendingPage is not null)
            {
                // 失败或取消时停止预取并观察其结果，避免未观察的任务异常。
                prefetchCts.Cancel();
                try
                {
                    await pendingPage;
                }
                catch
                {
                    // 预取结果已无意义，原始异常由外层继续抛出。
                }
            }
        }
    }

    private async Task<CatalogSyncPageResponse> FetchPinnedPageAsync(
        string storeCode,
        CatalogSyncPlanResponse plan,
        string? cursor,
        CancellationToken cancellationToken)
    {
        await _uiPriorityCoordinator.WaitForUiIdleAsync(cancellationToken);
        return await catalogApiClient.GetPinnedSellableItemsPageAsync(
            storeCode,
            plan.TargetCatalogVersion,
            plan.DownloadLeaseId,
            cursor,
            DownloadPageSize,
            cancellationToken);
    }

    private async Task<CatalogSyncPlanResponse> RequestFullPlanAsync(
        string storeCode,
        CancellationToken cancellationToken)
    {
        var plan = await catalogApiClient.GetCatalogSyncPlanAsync(storeCode, baseCatalogVersion: null, cancellationToken);
        ValidatePlan(storeCode, plan);
        if (!string.Equals(plan.Mode, CatalogSyncModes.Full, StringComparison.Ordinal))
        {
            throw new CatalogApiException(
                $"Catalog sync plan without a base version must be full. mode={plan.Mode}",
                HttpStatusCode.OK,
                "CATALOG_SYNC_PLAN_INVALID");
        }

        Log($"full plan store={storeCode} target={plan.TargetCatalogVersion} total={plan.TargetTotal} lease={!string.IsNullOrWhiteSpace(plan.DownloadLeaseId)}");
        return plan;
    }

    private static void ValidatePlan(string storeCode, CatalogSyncPlanResponse plan)
    {
        if (!string.Equals(plan.StoreCode?.Trim(), storeCode.Trim(), StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(plan.TargetCatalogVersion) ||
            plan.TargetTotal < 0 ||
            plan.Mode is not (CatalogSyncModes.NoChange or CatalogSyncModes.Delta or CatalogSyncModes.Full))
        {
            throw new CatalogApiException(
                $"Catalog sync plan is invalid. mode={plan.Mode} store={plan.StoreCode} target={plan.TargetCatalogVersion} total={plan.TargetTotal}",
                HttpStatusCode.OK,
                "CATALOG_SYNC_PLAN_INVALID");
        }
    }

    private static bool IsSyncPlanUnavailable(CatalogApiException exception)
    {
        // 旧版服务端没有 sync-plan：路由 404 不带业务错误码；门店不存在的 404 带 STORE_NOT_FOUND，不能当成旧版。
        return exception.StatusCode switch
        {
            HttpStatusCode.NotImplemented or HttpStatusCode.MethodNotAllowed => true,
            HttpStatusCode.NotFound => string.IsNullOrWhiteSpace(exception.ErrorCode),
            _ => false
        };
    }

    private static bool IsSnapshotExpired(CatalogApiException exception)
    {
        return string.Equals(exception.ErrorCode, "CATALOG_SNAPSHOT_EXPIRED", StringComparison.OrdinalIgnoreCase) ||
               (exception.StatusCode == HttpStatusCode.Conflict && string.IsNullOrWhiteSpace(exception.ErrorCode));
    }

    private static bool IsDeltaFallback(Exception exception)
    {
        return exception switch
        {
            LocalCatalogVersionConflictException => true,
            CatalogApiException apiException => IsSnapshotExpired(apiException) ||
                apiException.ErrorCode is "CATALOG_DELTA_BASE_CHANGED" or "CATALOG_DELTA_INCOMPLETE" or "CATALOG_PAGE_CHECKSUM_MISMATCH" or "CATALOG_PAGE_VERSION_MISMATCH",
            _ => false
        };
    }

    private static string FormatStatus(CatalogApiException exception)
    {
        return exception.StatusCode is null
            ? "<none>"
            : ((int)exception.StatusCode).ToString(CultureInfo.InvariantCulture);
    }

    private static void ReportPlannedDownloadProgress(
        IProgress<CatalogSyncProgress>? progress,
        string storeCode,
        PlannedSyncCounters counters,
        Stopwatch totalStopwatch)
    {
        ReportProgress(
            progress,
            storeCode,
            CatalogSyncProgressStage.Downloading,
            counters.TotalCount,
            counters.DownloadedCount,
            comparePages: 0,
            counters.RemotePages,
            counters.UpsertedCount,
            counters.DeletedCount,
            totalStopwatch);
    }

    /// <summary>
    /// 商品目录落地后的附属数据：促销、码冲突候选、新版促销规则。任何一项失败都只降级，不影响目录同步结果。
    /// 返回码冲突候选是否有变化。
    /// </summary>
    private async Task<bool> SyncAuxiliaryDataAsync(string storeCode, CancellationToken cancellationToken)
    {
        try
        {
            // 中文注释：旧 catalog 促销缓存只在商品同步成功后刷新；失败时保留旧缓存。
            await _uiPriorityCoordinator.WaitForUiIdleAsync(cancellationToken);
            var promotionStopwatch = Stopwatch.StartNew();
            var promotionResponse = await catalogApiClient.GetPromotionRulesAsync(storeCode, cancellationToken);
            await _uiPriorityCoordinator.WaitForUiIdleAsync(cancellationToken);
            await localCatalogRepository.ReplacePromotionRulesAsync(
                storeCode,
                promotionResponse.Promotions,
                cancellationToken);
            promotionStopwatch.Stop();
            Log($"promotions synced store={storeCode} rules={promotionResponse.Promotions.Count} elapsedMs={promotionStopwatch.ElapsedMilliseconds}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log($"promotion sync failed store={storeCode} error={ex.Message}");
        }

        var codeConflictsChanged = false;
        try
        {
            // 中文注释：码冲突候选只在商品同步成功后刷新；失败（含旧版服务端没有该接口）时保留本地旧数据，不阻断商品同步。
            // 服务端判断"目录无变化"只比较每个码的胜出商品，不含冲突候选，所以无变化时也必须拉取。
            await _uiPriorityCoordinator.WaitForUiIdleAsync(cancellationToken);
            var conflictStopwatch = Stopwatch.StartNew();
            var conflictResponse = await catalogApiClient.GetCodeConflictsAsync(storeCode, cancellationToken);
            if (conflictResponse.Available)
            {
                await _uiPriorityCoordinator.WaitForUiIdleAsync(cancellationToken);
                codeConflictsChanged = await localCatalogRepository.ReplaceCodeConflictItemsIfChangedAsync(
                    storeCode,
                    conflictResponse.Items.Select(item => item.ToSellableItemDto()).ToArray(),
                    cancellationToken);
            }

            conflictStopwatch.Stop();
            Log($"code conflicts synced store={storeCode} available={conflictResponse.Available} items={conflictResponse.Items.Count} changed={codeConflictsChanged} elapsedMs={conflictStopwatch.ElapsedMilliseconds}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log($"code conflict sync failed store={storeCode} error={ex.Message}");
        }

        if (localPromotionRepository is not null && promotionApiClient is not null)
        {
            try
            {
                // 中文注释：新 promotion rules 缓存供 POS 端重算使用，失败同样只降级不阻断商品同步。
                await _uiPriorityCoordinator.WaitForUiIdleAsync(cancellationToken);
                var promotionStopwatch = Stopwatch.StartNew();
                var promotionResponse = await promotionApiClient.GetRulesAsync(storeCode, asOf: null, cancellationToken);
                await _uiPriorityCoordinator.WaitForUiIdleAsync(cancellationToken);
                await localPromotionRepository.ReplaceStoreRulesAsync(storeCode, promotionResponse, cancellationToken);
                promotionStopwatch.Stop();
                Log($"promotion rules synced store={storeCode} rules={promotionResponse.Rules.Count} elapsedMs={promotionStopwatch.ElapsedMilliseconds}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log($"promotion sync failed store={storeCode} error={ex.Message}");
            }
        }

        return codeConflictsChanged;
    }

    /// <summary>
    /// 旧版服务端（没有 sync-plan）的兼容流程：逐页上传本地摘要核对，再按需分页下载。
    /// </summary>
    private async Task<LocalCatalogSyncResult> LegacyFullSyncAsync(
        string storeCode,
        CancellationToken cancellationToken,
        IProgress<CatalogSyncProgress>? progress,
        bool forceFullDownload)
    {
        var totalStopwatch = Stopwatch.StartNew();
        // 旧流程会逐条改写本地目录，之后的数据不再对应任何服务端版本。
        await localCatalogRepository.ClearCatalogVersionAsync(storeCode, cancellationToken);
        Log($"full sync start store={storeCode} comparePageSize={ComparePageSize} pageSize={DownloadPageSize} forceFullDownload={forceFullDownload}");

        var comparePages = 0;
        var remotePages = 0;
        var upsertedCount = 0;
        var deletedCount = 0;
        var totalCount = 0;
        var downloadedCount = 0;
        var comparedCount = 0;
        var localItemCount = 0;
        var hasCompareChanges = false;
        string? afterLookupCodeNormalized = null;

        try
        {
            ReportProgress(
                progress,
                storeCode,
                CatalogSyncProgressStage.Preparing,
                totalCount,
                downloadedCount,
                comparePages,
                remotePages,
                upsertedCount,
                deletedCount,
                totalStopwatch);

            if (forceFullDownload)
            {
                Log($"compare skipped store={storeCode} reason=force-full-download");
            }
            else
            {
                ReportProgress(
                    progress,
                    storeCode,
                    CatalogSyncProgressStage.Comparing,
                    totalCount,
                    downloadedCount,
                    comparePages,
                    remotePages,
                    upsertedCount,
                    deletedCount,
                    totalStopwatch,
                    comparedCount: comparedCount);

                while (true)
                {
                    await _uiPriorityCoordinator.WaitForUiIdleAsync(cancellationToken);
                    var localPage = await localCatalogRepository.LoadSellableItemComparePageAsync(
                        storeCode,
                        afterLookupCodeNormalized,
                        ComparePageSize,
                        cancellationToken);

                    if (localPage.Count == 0)
                    {
                        Log($"local compare finished store={storeCode} pages={comparePages}");
                        break;
                    }

                    localItemCount += localPage.Count;
                    afterLookupCodeNormalized = localPage[^1].LookupCodeNormalized;
                    Log($"local compare page store={storeCode} page={comparePages + 1} rows={localPage.Count} after={afterLookupCodeNormalized}");
                    var request = new CatalogCompareRequest(
                        storeCode,
                        localPage.Select(row => row.ToCompareVersion()).ToArray());
                    var compareStopwatch = Stopwatch.StartNew();
                    await _uiPriorityCoordinator.WaitForUiIdleAsync(cancellationToken);
                    var response = await catalogApiClient.CompareSellableItemsAsync(request, cancellationToken);
                    compareStopwatch.Stop();
                    Log($"compare response store={storeCode} page={comparePages + 1} upsertedLookups={response.UpsertedLookups.Count} deletedLookups={response.DeletedLookups.Count} apiElapsedMs={compareStopwatch.ElapsedMilliseconds}");
                    hasCompareChanges |= response.UpsertedLookups.Count > 0 || response.DeletedLookups.Count > 0;
                    comparedCount += localPage.Count;

                    var applied = await ApplyChangesAsync(
                        storeCode,
                        response.UpsertedLookups,
                        response.DeletedLookups,
                        cancellationToken,
                        (batchUpsertedCount, batchDeletedCount) => ReportProgress(
                            progress,
                            storeCode,
                            CatalogSyncProgressStage.Comparing,
                            totalCount,
                            downloadedCount,
                            comparePages + 1,
                            remotePages,
                            upsertedCount + batchUpsertedCount,
                            deletedCount + batchDeletedCount,
                            totalStopwatch,
                            comparedCount: comparedCount));

                    comparePages++;
                    upsertedCount += applied.UpsertedCount;
                    deletedCount += applied.DeletedCount;
                    Log($"compare applied store={storeCode} page={comparePages} upserted={applied.UpsertedCount} deleted={applied.DeletedCount} upsertElapsedMs={applied.UpsertElapsedMs} deleteElapsedMs={applied.DeleteElapsedMs} applyElapsedMs={applied.ApplyElapsedMs}");
                    ReportProgress(
                        progress,
                        storeCode,
                        CatalogSyncProgressStage.Comparing,
                        totalCount,
                        downloadedCount,
                        comparePages,
                        remotePages,
                        upsertedCount,
                        deletedCount,
                        totalStopwatch,
                        comparedCount: comparedCount);
                }
            }

            if (forceFullDownload)
            {
                await using var replaceSession = await localCatalogRepository.BeginStoreReplaceSessionAsync(
                    storeCode,
                    cancellationToken);
                string? cursor = null;
                while (true)
                {
                    Log($"download page request store={storeCode} page={remotePages + 1} cursor={cursor ?? "<start>"} mode=snapshot");
                    var downloadStopwatch = Stopwatch.StartNew();
                    await _uiPriorityCoordinator.WaitForUiIdleAsync(cancellationToken);
                    var response = await catalogApiClient.GetSellableItemsPageAsync(
                        storeCode,
                        cursor,
                        DownloadPageSize,
                        cancellationToken);
                    downloadStopwatch.Stop();
                    totalCount = Math.Max(totalCount, response.TotalCount);
                    Log($"download page response store={storeCode} page={remotePages + 1} items={response.Items.Count} total={response.TotalCount} deletedLookups={response.DeletedLookups.Count} hasMore={response.HasMore} next={response.NextCursor ?? "<end>"} apiElapsedMs={downloadStopwatch.ElapsedMilliseconds} mode=snapshot");
                    ReportProgress(
                        progress,
                        storeCode,
                        CatalogSyncProgressStage.Downloading,
                        totalCount,
                        downloadedCount,
                        comparePages,
                        remotePages,
                        upsertedCount,
                        deletedCount,
                        totalStopwatch,
                        comparedCount: comparedCount);

                    var stageStopwatch = Stopwatch.StartNew();
                    var stagedItems = response.Items
                        .Select(item => item.ToSellableItemDto())
                        .ToArray();
                    var stagedCount = 0;
                    foreach (var batch in stagedItems.Chunk(ApplyBatchSize))
                    {
                        await _uiPriorityCoordinator.WaitForUiIdleAsync(cancellationToken);
                        await replaceSession.StageAsync(batch, cancellationToken);
                        stagedCount += batch.Length;
                        ReportProgress(
                            progress,
                            storeCode,
                            CatalogSyncProgressStage.Downloading,
                            totalCount,
                            downloadedCount + stagedCount,
                            comparePages,
                            remotePages + 1,
                            upsertedCount + stagedCount,
                            deletedCount,
                            totalStopwatch,
                            comparedCount: comparedCount);
                    }

                    stageStopwatch.Stop();

                    remotePages++;
                    downloadedCount += response.Items.Count;
                    upsertedCount += stagedItems.Length;
                    Log($"download page staged store={storeCode} page={remotePages} staged={stagedItems.Length} stageElapsedMs={stageStopwatch.ElapsedMilliseconds}");
                    ReportProgress(
                        progress,
                        storeCode,
                        CatalogSyncProgressStage.Downloading,
                        totalCount,
                        downloadedCount,
                        comparePages,
                        remotePages,
                        upsertedCount,
                        deletedCount,
                        totalStopwatch,
                        forceComplete: false,
                        comparedCount: comparedCount);

                    if (!response.HasMore)
                    {
                        break;
                    }

                    if (string.IsNullOrWhiteSpace(response.NextCursor))
                    {
                        throw new CatalogApiException("Catalog API indicated more pages but did not return a next cursor.");
                    }

                    cursor = response.NextCursor;
                }

                var commitStopwatch = Stopwatch.StartNew();
                await _uiPriorityCoordinator.WaitForUiIdleAsync(cancellationToken);
                var commitResult = await replaceSession.CommitAsync(cancellationToken);
                commitStopwatch.Stop();
                upsertedCount = commitResult.InsertedCount;
                deletedCount = commitResult.DeletedCount;
                Log($"snapshot committed store={storeCode} inserted={commitResult.InsertedCount} deleted={commitResult.DeletedCount} commitElapsedMs={commitStopwatch.ElapsedMilliseconds}");
            }
            else
            {
                string? cursor = null;
                while (true)
                {
                    Log($"download page request store={storeCode} page={remotePages + 1} cursor={cursor ?? "<start>"}");
                    var downloadStopwatch = Stopwatch.StartNew();
                    await _uiPriorityCoordinator.WaitForUiIdleAsync(cancellationToken);
                    var response = await catalogApiClient.GetSellableItemsPageAsync(
                        storeCode,
                        cursor,
                        DownloadPageSize,
                        cancellationToken);
                    downloadStopwatch.Stop();
                    totalCount = Math.Max(totalCount, response.TotalCount);
                    Log($"download page response store={storeCode} page={remotePages + 1} items={response.Items.Count} total={response.TotalCount} deletedLookups={response.DeletedLookups.Count} hasMore={response.HasMore} next={response.NextCursor ?? "<end>"} apiElapsedMs={downloadStopwatch.ElapsedMilliseconds}");
                    ReportProgress(
                        progress,
                        storeCode,
                        CatalogSyncProgressStage.Downloading,
                        totalCount,
                        downloadedCount,
                        comparePages,
                        remotePages,
                        upsertedCount,
                        deletedCount,
                        totalStopwatch,
                        comparedCount: comparedCount);

                    if (remotePages == 0 && !hasCompareChanges && localItemCount == response.TotalCount)
                    {
                        remotePages++;
                        downloadedCount = response.TotalCount;
                        Log($"download skipped store={storeCode} reason=no-changes localCount={localItemCount} total={response.TotalCount} comparePages={comparePages} remotePages={remotePages} elapsedMs={totalStopwatch.ElapsedMilliseconds}");
                        ReportProgress(
                            progress,
                            storeCode,
                            CatalogSyncProgressStage.Downloading,
                            totalCount,
                            downloadedCount,
                            comparePages,
                            remotePages,
                            upsertedCount,
                            deletedCount,
                            totalStopwatch,
                            comparedCount: comparedCount);
                        break;
                    }

                    var applied = await ApplyChangesAsync(
                        storeCode,
                        response.Items,
                        response.DeletedLookups,
                        cancellationToken,
                        (batchUpsertedCount, batchDeletedCount) => ReportProgress(
                            progress,
                            storeCode,
                            CatalogSyncProgressStage.Downloading,
                            totalCount,
                            downloadedCount + batchUpsertedCount,
                            comparePages,
                            remotePages + 1,
                            upsertedCount + batchUpsertedCount,
                            deletedCount + batchDeletedCount,
                            totalStopwatch,
                            comparedCount: comparedCount));

                    remotePages++;
                    downloadedCount += response.Items.Count;
                    upsertedCount += applied.UpsertedCount;
                    deletedCount += applied.DeletedCount;
                    Log($"download page applied store={storeCode} page={remotePages} upserted={applied.UpsertedCount} deleted={applied.DeletedCount} upsertElapsedMs={applied.UpsertElapsedMs} deleteElapsedMs={applied.DeleteElapsedMs} applyElapsedMs={applied.ApplyElapsedMs}");
                    ReportProgress(
                        progress,
                        storeCode,
                        CatalogSyncProgressStage.Downloading,
                        totalCount,
                        downloadedCount,
                        comparePages,
                        remotePages,
                        upsertedCount,
                        deletedCount,
                        totalStopwatch,
                        forceComplete: false,
                        comparedCount: comparedCount);

                    if (!response.HasMore)
                    {
                        break;
                    }

                    if (string.IsNullOrWhiteSpace(response.NextCursor))
                    {
                        throw new CatalogApiException("Catalog API indicated more pages but did not return a next cursor.");
                    }

                    cursor = response.NextCursor;
                }
            }

            // 旧流程逐条改写目录，无法判断是否真的有变化，结果按默认的"有变化"处理。
            await SyncAuxiliaryDataAsync(storeCode, cancellationToken);

            totalStopwatch.Stop();
            Log($"full sync completed store={storeCode} comparePages={comparePages} remotePages={remotePages} upserted={upsertedCount} deleted={deletedCount} elapsedMs={totalStopwatch.ElapsedMilliseconds}");
            ReportProgress(
                progress,
                storeCode,
                CatalogSyncProgressStage.Completed,
                totalCount,
                totalCount == 0 ? 0 : Math.Max(downloadedCount, totalCount),
                comparePages,
                remotePages,
                upsertedCount,
                deletedCount,
                totalStopwatch,
                forceComplete: true,
                comparedCount: comparedCount);
            return new LocalCatalogSyncResult(
                storeCode,
                comparePages,
                remotePages,
                upsertedCount,
                deletedCount);
        }
        catch (OperationCanceledException)
        {
            totalStopwatch.Stop();
            Log($"full sync canceled store={storeCode} comparePages={comparePages} remotePages={remotePages} upserted={upsertedCount} deleted={deletedCount} elapsedMs={totalStopwatch.ElapsedMilliseconds}");
            throw;
        }
        catch (Exception ex)
        {
            totalStopwatch.Stop();
            ReportProgress(
                progress,
                storeCode,
                CatalogSyncProgressStage.Failed,
                totalCount,
                downloadedCount,
                comparePages,
                remotePages,
                upsertedCount,
                deletedCount,
                totalStopwatch,
                ex.Message,
                comparedCount: comparedCount);
            throw;
        }
    }

    private async Task<(int UpsertedCount, int DeletedCount, long UpsertElapsedMs, long DeleteElapsedMs, long ApplyElapsedMs)> ApplyChangesAsync(
        string storeCode,
        IReadOnlyList<CatalogLookupItemDto> upsertedLookups,
        IReadOnlyList<DeletedLookupDto> deletedLookups,
        CancellationToken cancellationToken,
        Action<int, int>? batchApplied = null)
    {
        var applyStopwatch = Stopwatch.StartNew();
        var upsertItems = upsertedLookups
            .Select(item => item.ToSellableItemDto())
            .ToArray();
        var upsertElapsedMs = 0L;
        var upsertedCount = 0;
        if (upsertItems.Length > 0)
        {
            var upsertStopwatch = Stopwatch.StartNew();
            foreach (var batch in upsertItems.Chunk(ApplyBatchSize))
            {
                await _uiPriorityCoordinator.WaitForUiIdleAsync(cancellationToken);
                await localCatalogRepository.UpsertSellableItemsAsync(batch, cancellationToken);
                upsertedCount += batch.Length;
                batchApplied?.Invoke(upsertedCount, 0);
            }

            upsertStopwatch.Stop();
            upsertElapsedMs = upsertStopwatch.ElapsedMilliseconds;
        }

        var deletedCodes = deletedLookups
            .Select(GetDeleteLookupCode)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var deleteElapsedMs = 0L;
        var deletedCount = deletedCodes.Length == 0
            ? 0
            : await DeleteByLookupCodesWithTimingAsync(storeCode, deletedCodes, cancellationToken);
        if (deletedCount > 0)
        {
            batchApplied?.Invoke(upsertedCount, deletedCount);
        }

        applyStopwatch.Stop();
        return (upsertedCount, deletedCount, upsertElapsedMs, deleteElapsedMs, applyStopwatch.ElapsedMilliseconds);

        async Task<int> DeleteByLookupCodesWithTimingAsync(
            string deleteStoreCode,
            IReadOnlyList<string> lookupCodes,
            CancellationToken deleteCancellationToken)
        {
            var deleteStopwatch = Stopwatch.StartNew();
            await _uiPriorityCoordinator.WaitForUiIdleAsync(deleteCancellationToken);
            var count = await localCatalogRepository.DeleteByLookupCodesAsync(
                deleteStoreCode,
                lookupCodes,
                deleteCancellationToken);
            deleteStopwatch.Stop();
            deleteElapsedMs = deleteStopwatch.ElapsedMilliseconds;
            return count;
        }
    }

    private static string GetDeleteLookupCode(DeletedLookupDto deletedLookup)
    {
        return string.IsNullOrWhiteSpace(deletedLookup.LookupCodeNormalized)
            ? deletedLookup.LookupCode
            : deletedLookup.LookupCodeNormalized;
    }

    private static void ReportProgress(
        IProgress<CatalogSyncProgress>? progress,
        string storeCode,
        CatalogSyncProgressStage stage,
        int totalCount,
        int downloadedCount,
        int comparePages,
        int remotePages,
        int upsertedCount,
        int deletedCount,
        Stopwatch stopwatch,
        string? errorMessage = null,
        bool forceComplete = false,
        int comparedCount = 0)
    {
        if (progress is null)
        {
            return;
        }

        var percent = CalculatePercent(totalCount, downloadedCount, forceComplete);
        progress.Report(new CatalogSyncProgress(
            storeCode,
            stage,
            totalCount,
            totalCount == 0 && forceComplete ? 0 : Math.Min(downloadedCount, totalCount),
            percent,
            comparePages,
            remotePages,
            upsertedCount,
            deletedCount,
            stopwatch.ElapsedMilliseconds,
            errorMessage)
        {
            ComparedCount = comparedCount
        });
    }

    private static int CalculatePercent(int totalCount, int downloadedCount, bool forceComplete)
    {
        if (forceComplete)
        {
            return 100;
        }

        if (totalCount <= 0)
        {
            return 0;
        }

        return Math.Clamp((int)Math.Round(downloadedCount * 100d / totalCount), 0, 99);
    }

    private static void Log(string message)
    {
        ConsoleLog.Write("CatalogSync", message);
    }

    private sealed class PlannedSyncCounters
    {
        public int TotalCount { get; set; }

        public int DownloadedCount { get; set; }

        public int RemotePages { get; set; }

        public int UpsertedCount { get; set; }

        public int DeletedCount { get; set; }

        public void Reset()
        {
            TotalCount = 0;
            DownloadedCount = 0;
            RemotePages = 0;
            UpsertedCount = 0;
            DeletedCount = 0;
        }
    }
}
