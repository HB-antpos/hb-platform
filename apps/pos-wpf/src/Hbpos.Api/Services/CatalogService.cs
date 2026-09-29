using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BlazorApp.Shared.Helper;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HBweb;
using Hbpos.Api.Data;
using Hbpos.Contracts.Catalog;
using Microsoft.Extensions.Options;
using SqlSugar;

namespace Hbpos.Api.Services;

public interface ICatalogService
{
    Task<IReadOnlyList<StoreDto>> GetStoresAsync(CancellationToken cancellationToken);

    Task<SellableItemsResponse?> GetSellableItemsAsync(
        string storeCode,
        DateTimeOffset? since,
        CancellationToken cancellationToken);

    Task<CatalogSyncPageResponse?> GetSellableItemsPageAsync(
        string storeCode,
        DateTimeOffset? since,
        string? cursor,
        int pageSize,
        CancellationToken cancellationToken);

    Task<CatalogSyncPageResponse?> GetSellableItemsPageAsync(
        string storeCode,
        DateTimeOffset? since,
        string? cursor,
        int pageSize,
        CancellationToken cancellationToken,
        string? catalogVersion,
        int checksumVersion);

    Task<CatalogSyncPlanResponse?> GetCatalogSyncPlanAsync(
        string storeCode,
        string? baseCatalogVersion,
        CancellationToken cancellationToken);

    Task<CatalogSyncPlanResponse?> GetCatalogSyncPlanWithLeaseAsync(
        string storeCode,
        string? baseCatalogVersion,
        CancellationToken cancellationToken);

    Task<CatalogSyncPageResponse?> GetSellableItemsPageWithLeaseAsync(
        string storeCode,
        DateTimeOffset? since,
        string? cursor,
        int pageSize,
        CancellationToken cancellationToken,
        string? catalogVersion,
        int checksumVersion,
        string? downloadLeaseId);

    Task<CatalogDeltaPageResponse> GetCatalogDeltaPageAsync(
        string storeCode,
        string baseCatalogVersion,
        string targetCatalogVersion,
        string? cursor,
        int pageSize,
        CancellationToken cancellationToken);

    Task<CatalogDeltaPageResponse> GetCatalogDeltaPageWithLeaseAsync(
        string storeCode,
        string baseCatalogVersion,
        string targetCatalogVersion,
        string? cursor,
        int pageSize,
        CancellationToken cancellationToken,
        string? downloadLeaseId);

    Task<CatalogCompareResponse?> CompareSellableItemsAsync(
        CatalogCompareRequest request,
        CancellationToken cancellationToken);

    Task<CatalogPromotionsResponse?> GetPromotionRulesAsync(
        string storeCode,
        CancellationToken cancellationToken);

    Task<CatalogLookupResponse?> LookupSellableItemAsync(
        string storeCode,
        string? lookupCode,
        string? lookupCodeNormalized,
        CancellationToken cancellationToken);

    Task<CatalogSpecialProductsPageResponse?> GetSpecialProductsPageAsync(
        string storeCode,
        string? cursor,
        int pageSize,
        CancellationToken cancellationToken);

    Task<CatalogCodeConflictsResponse?> GetCodeConflictsAsync(
        string storeCode,
        CancellationToken cancellationToken);

    Task<CatalogSpecialProductMarkServiceResult> MarkSpecialProductAsync(
        CatalogSpecialProductMarkRequest request,
        string updatedBy,
        CancellationToken cancellationToken);
}

public sealed record CatalogSpecialProductMarkServiceResult(
    bool Success,
    CatalogSpecialProductMarkResponse? Response,
    string? ErrorCode,
    string? Message)
{
    public static CatalogSpecialProductMarkServiceResult Ok(CatalogSpecialProductMarkResponse response) =>
        new(true, response, null, null);

    public static CatalogSpecialProductMarkServiceResult Fail(string errorCode, string message) =>
        new(false, null, errorCode, message);
}

public sealed class CatalogSnapshotExpiredException(
    string storeCode,
    string catalogVersion)
    : Exception("The requested catalog snapshot is no longer available.")
{
    public string StoreCode { get; } = storeCode;

    public string CatalogVersion { get; } = catalogVersion;
}

/// <summary>SQL Server 未启用 SNAPSHOT 时拒绝混合时点的目录构建，不能静默降级为脏读。</summary>
public sealed class CatalogSnapshotIsolationUnavailableException(Exception innerException)
    : Exception("SQL Server SNAPSHOT isolation is required for catalog reads.", innerException);

public sealed class CatalogSyncOptions
{
    public bool DeltaEnabled { get; init; } = true;
}

public sealed class CatalogService(
    HbposSqlSugarContext dbContext,
    IPriceIndexBuilder priceIndexBuilder,
    ICatalogIndexCache catalogIndexCache,
    ICatalogBaseDataCache catalogBaseDataCache,
    IOptions<CatalogSyncOptions>? catalogSyncOptions = null)
    : ICatalogService, ICatalogIndexRefreshWorker
{
    private const int CatalogSourceBatchSize = 100_000;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> StoreRetailPriceEnsureLocks = new(StringComparer.Ordinal);

    public CatalogService(
        HbposSqlSugarContext dbContext,
        IPriceIndexBuilder priceIndexBuilder,
        ICatalogIndexCache catalogIndexCache)
        : this(
            dbContext,
            priceIndexBuilder,
            catalogIndexCache,
            new CatalogBaseDataCache(),
            catalogSyncOptions: null)
    {
    }

    public async Task<IReadOnlyList<StoreDto>> GetStoresAsync(CancellationToken cancellationToken)
    {
        var stores = await dbContext.MainDb.Queryable<Store>()
            .Where(x => x.IsActive && !x.IsDeleted)
            .OrderBy(x => x.StoreName)
            .OrderBy(x => x.StoreCode)
            .ToListAsync(cancellationToken);

        return stores
            .Select(x => new StoreDto(x.StoreCode, x.StoreName, x.IsActive))
            .ToArray();
    }

    public async Task<SellableItemsResponse?> GetSellableItemsAsync(
        string storeCode,
        DateTimeOffset? since,
        CancellationToken cancellationToken)
    {
        var index = await BuildSellableIndexAsync(storeCode, since, cancellationToken);
        return index is null
            ? null
            : new SellableItemsResponse(index.StoreCode, index.GeneratedAt, index.SellableItems);
    }

    public async Task<CatalogSyncPageResponse?> GetSellableItemsPageAsync(
        string storeCode,
        DateTimeOffset? since,
        string? cursor,
        int pageSize,
        CancellationToken cancellationToken)
    {
        return await GetSellableItemsPageAsync(
            storeCode,
            since,
            cursor,
            pageSize,
            cancellationToken,
            catalogVersion: null,
            checksumVersion: 1);
    }

    public async Task<CatalogSyncPageResponse?> GetSellableItemsPageAsync(
        string storeCode,
        DateTimeOffset? since,
        string? cursor,
        int pageSize,
        CancellationToken cancellationToken,
        string? catalogVersion,
        int checksumVersion)
    {
        if (checksumVersion is not 1 and not 2)
        {
            throw new ArgumentOutOfRangeException(nameof(checksumVersion));
        }

        CatalogIndexBuildResult? index;
        if (string.IsNullOrWhiteSpace(catalogVersion))
        {
            index = await BuildSellableIndexAsync(storeCode, since, cancellationToken);
        }
        else
        {
            index = catalogIndexCache.GetByVersion(storeCode, since, catalogVersion);
            if (index is null)
            {
                throw new CatalogSnapshotExpiredException(storeCode, catalogVersion);
            }
        }

        return index?.CatalogIndex.GetPage(cursor, pageSize, checksumVersion);
    }

    public async Task<CatalogSyncPlanResponse?> GetCatalogSyncPlanAsync(
        string storeCode,
        string? baseCatalogVersion,
        CancellationToken cancellationToken)
    {
        // 目标版本始终从当前完整目录取得；增量仅使用已固定、仍可读取的旧版本。
        var target = await BuildSellableIndexAsync(storeCode, since: null, cancellationToken);
        if (target is null)
        {
            return null;
        }

        var targetVersion = target.CatalogIndex.CatalogVersion;
        var normalizedBaseVersion = baseCatalogVersion?.Trim();
        if (!IsDeltaEnabled)
        {
            return new CatalogSyncPlanResponse(
                target.StoreCode,
                target.GeneratedAt,
                CatalogSyncModes.Full,
                normalizedBaseVersion,
                targetVersion,
                target.CatalogIndex.Items.Count);
        }
        if (string.IsNullOrEmpty(normalizedBaseVersion))
        {
            return new CatalogSyncPlanResponse(
                target.StoreCode,
                target.GeneratedAt,
                CatalogSyncModes.Full,
                null,
                targetVersion,
                target.CatalogIndex.Items.Count);
        }

        if (string.Equals(normalizedBaseVersion, targetVersion, StringComparison.Ordinal))
        {
            return new CatalogSyncPlanResponse(
                target.StoreCode,
                target.GeneratedAt,
                CatalogSyncModes.NoChange,
                normalizedBaseVersion,
                targetVersion,
                target.CatalogIndex.Items.Count);
        }

        var baseline = catalogIndexCache.GetByVersion(storeCode, since: null, normalizedBaseVersion);
        // 基准快照过期时不能猜测删除项，明确要求客户端安全地退回全量下载。
        return new CatalogSyncPlanResponse(
            target.StoreCode,
            target.GeneratedAt,
            baseline is null ? CatalogSyncModes.Full : CatalogSyncModes.Delta,
            normalizedBaseVersion,
            targetVersion,
            target.CatalogIndex.Items.Count);
    }

    public async Task<CatalogSyncPlanResponse?> GetCatalogSyncPlanWithLeaseAsync(
        string storeCode,
        string? baseCatalogVersion,
        CancellationToken cancellationToken)
    {
        var target = await BuildSellableIndexAsync(storeCode, since: null, cancellationToken);
        if (target is null)
        {
            return null;
        }

        var normalizedBase = baseCatalogVersion?.Trim();
        if (!IsDeltaEnabled)
        {
            var lease = catalogIndexCache.CreateFullLease(target);
            return new CatalogSyncPlanResponse(target.StoreCode, target.GeneratedAt, CatalogSyncModes.Full,
                normalizedBase, target.CatalogIndex.CatalogVersion, target.CatalogIndex.Items.Count, lease.LeaseId);
        }
        if (string.IsNullOrEmpty(normalizedBase))
        {
            var lease = catalogIndexCache.CreateFullLease(target);
            return new CatalogSyncPlanResponse(target.StoreCode, target.GeneratedAt, CatalogSyncModes.Full, null,
                target.CatalogIndex.CatalogVersion, target.CatalogIndex.Items.Count, lease.LeaseId);
        }

        if (string.Equals(normalizedBase, target.CatalogIndex.CatalogVersion, StringComparison.Ordinal))
        {
            return new CatalogSyncPlanResponse(target.StoreCode, target.GeneratedAt, CatalogSyncModes.NoChange,
                normalizedBase, target.CatalogIndex.CatalogVersion, target.CatalogIndex.Items.Count);
        }

        var baseline = catalogIndexCache.GetByVersion(storeCode, since: null, normalizedBase);
        if (baseline is null)
        {
            var lease = catalogIndexCache.CreateFullLease(target);
            return new CatalogSyncPlanResponse(target.StoreCode, target.GeneratedAt, CatalogSyncModes.Full,
                normalizedBase, target.CatalogIndex.CatalogVersion, target.CatalogIndex.Items.Count, lease.LeaseId);
        }

        var operations = target.CatalogIndex.GetDeltaOperations(baseline.CatalogIndex);
        if (operations.Count > 5_000)
        {
            var lease = catalogIndexCache.CreateFullLease(target);
            return new CatalogSyncPlanResponse(target.StoreCode, target.GeneratedAt, CatalogSyncModes.Full,
                normalizedBase, target.CatalogIndex.CatalogVersion, target.CatalogIndex.Items.Count, lease.LeaseId,
                operations.Count);
        }

        var deltaLease = catalogIndexCache.CreateDeltaLease(baseline, target, operations);
        return new CatalogSyncPlanResponse(target.StoreCode, target.GeneratedAt, CatalogSyncModes.Delta,
            normalizedBase, target.CatalogIndex.CatalogVersion, target.CatalogIndex.Items.Count,
            deltaLease.LeaseId, operations.Count);
    }

    public async Task<CatalogSyncPageResponse?> GetSellableItemsPageWithLeaseAsync(
        string storeCode,
        DateTimeOffset? since,
        string? cursor,
        int pageSize,
        CancellationToken cancellationToken,
        string? catalogVersion,
        int checksumVersion,
        string? downloadLeaseId)
    {
        if (string.IsNullOrWhiteSpace(downloadLeaseId))
        {
            return await GetSellableItemsPageAsync(storeCode, since, cursor, pageSize, cancellationToken, catalogVersion, checksumVersion);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var lease = catalogIndexCache.DownloadLeases.GetAndTouch(
            downloadLeaseId,
            storeCode,
            baseCatalogVersion: null,
            catalogVersion ?? throw new CatalogSnapshotExpiredException(storeCode, string.Empty));
        var page = lease.Target.CatalogIndex.GetPage(cursor, pageSize, checksumVersion);
        cancellationToken.ThrowIfCancellationRequested();
        catalogIndexCache.DownloadLeases.Touch(
            downloadLeaseId,
            storeCode,
            baseCatalogVersion: null,
            catalogVersion!);
        return page with { DownloadLeaseId = lease.LeaseId };
    }

    public Task<CatalogDeltaPageResponse> GetCatalogDeltaPageWithLeaseAsync(
        string storeCode,
        string baseCatalogVersion,
        string targetCatalogVersion,
        string? cursor,
        int pageSize,
        CancellationToken cancellationToken,
        string? downloadLeaseId)
    {
        if (string.IsNullOrWhiteSpace(downloadLeaseId))
        {
            return GetCatalogDeltaPageAsync(storeCode, baseCatalogVersion, targetCatalogVersion, cursor, pageSize, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var lease = catalogIndexCache.DownloadLeases.GetAndTouch(downloadLeaseId, storeCode, baseCatalogVersion, targetCatalogVersion);
        var page = CreateLeaseDeltaPage(lease, cursor, pageSize);
        cancellationToken.ThrowIfCancellationRequested();
        catalogIndexCache.DownloadLeases.Touch(downloadLeaseId, storeCode, baseCatalogVersion, targetCatalogVersion);
        return Task.FromResult(page);
    }

    private static CatalogDeltaPageResponse CreateLeaseDeltaPage(
        CatalogDownloadLease lease,
        string? cursor,
        int pageSize)
    {
        // DeltaOperations 已在 plan 阶段固定；当前索引对象同样由租约固定，
        // 因而分页即使发生后续发布也只会读取这一对版本。
        var page = lease.Target.CatalogIndex.GetDeltaPageFromOperations(
            lease.Baseline!.CatalogIndex,
            lease.DeltaOperations,
            cursor,
            pageSize);
        return page with { DownloadLeaseId = lease.LeaseId };
    }

    public Task<CatalogDeltaPageResponse> GetCatalogDeltaPageAsync(
        string storeCode,
        string baseCatalogVersion,
        string targetCatalogVersion,
        string? cursor,
        int pageSize,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(baseCatalogVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetCatalogVersion);

        var baseline = catalogIndexCache.GetByVersion(storeCode, since: null, baseCatalogVersion);
        if (baseline is null)
        {
            throw new CatalogSnapshotExpiredException(storeCode, baseCatalogVersion);
        }

        var target = catalogIndexCache.GetByVersion(storeCode, since: null, targetCatalogVersion);
        if (target is null)
        {
            throw new CatalogSnapshotExpiredException(storeCode, targetCatalogVersion);
        }

        return Task.FromResult(target.CatalogIndex.GetDeltaPage(
            baseline.CatalogIndex,
            cursor,
            pageSize));
    }

    public async Task<CatalogCompareResponse?> CompareSellableItemsAsync(
        CatalogCompareRequest request,
        CancellationToken cancellationToken)
    {
        var index = await BuildSellableIndexAsync(request.StoreCode, since: null, cancellationToken);
        return index?.CatalogIndex.Compare(request);
    }

    public async Task<CatalogPromotionsResponse?> GetPromotionRulesAsync(
        string storeCode,
        CancellationToken cancellationToken)
    {
        var normalizedStoreCode = NormalizeStoreCode(storeCode);
        if (string.IsNullOrEmpty(normalizedStoreCode))
        {
            return null;
        }

        var store = await dbContext.MainDb.Queryable<Store>()
            .FirstAsync(x => x.StoreCode == normalizedStoreCode && x.IsActive && !x.IsDeleted, cancellationToken);
        if (store is null)
        {
            return null;
        }

        var now = DateTime.UtcNow;
        var promotions = await dbContext.MainDb.Queryable<Promotion>()
            .Where(p =>
                !p.IsDeleted &&
                p.IsEnabled &&
                p.ApplyQuantity > 0 &&
                p.FixedPrice >= 0m &&
                p.EffectiveStart <= now &&
                p.EffectiveEnd >= now)
            .Where(p =>
                SqlFunc
                    .Subqueryable<PromotionStore>()
                    .Where(ps =>
                        !ps.IsDeleted &&
                        ps.PromotionId == p.Id &&
                        ps.StoreCode == normalizedStoreCode)
                    .Any())
            .OrderByDescending(p => p.IsExclusive)
            .OrderByDescending(p => p.Priority)
            .ToListAsync(cancellationToken);

        var promotionIds = promotions.Select(p => p.Id).ToArray();
        var products = promotionIds.Length == 0
            ? new List<PromotionProduct>()
            : await dbContext.MainDb.Queryable<PromotionProduct>()
                .Where(product => !product.IsDeleted && promotionIds.Contains(product.PromotionId))
                .ToListAsync(cancellationToken);
        var productsByPromotion = products
            .GroupBy(product => product.PromotionId)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

        // 只同步有商品明细的有效规则；收银端离线计算不再访问后端评估接口。
        var rules = promotions
            .Select(promotion => new CatalogPromotionRuleDto(
                promotion.Id,
                promotion.Name,
                promotion.IsExclusive,
                promotion.Priority,
                promotion.ApplyQuantity,
                promotion.FixedPrice,
                promotion.MaxApplicationsPerOrder,
                ToOffset(promotion.EffectiveStart) ?? DateTimeOffset.MinValue,
                ToOffset(promotion.EffectiveEnd) ?? DateTimeOffset.MinValue,
                promotion.UpdatedAt.HasValue ? ToOffset(promotion.UpdatedAt.Value) : null,
                productsByPromotion.TryGetValue(promotion.Id, out var promotionProducts)
                    ? promotionProducts
                        .Select(product => new CatalogPromotionProductDto(
                            product.ProductCode,
                            Math.Max(1, product.UnitWeight)))
                        .ToArray()
                    : []))
            .Where(rule => rule.Products.Count > 0)
            .ToArray();

        return new CatalogPromotionsResponse(store.StoreCode, DateTimeOffset.UtcNow, rules);
    }

    public async Task<CatalogLookupResponse?> LookupSellableItemAsync(
        string storeCode,
        string? lookupCode,
        string? lookupCodeNormalized,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        Log($"lookup service start store={storeCode} lookupCode={lookupCode ?? "<null>"} lookupCodeNormalized={lookupCodeNormalized ?? "<null>"}");
        var directResult = await LookupSellableItemDirectAsync(
            storeCode,
            lookupCode,
            lookupCodeNormalized,
            cancellationToken);
        if (directResult is null)
        {
            stopwatch.Stop();
            Log($"lookup service completed store={storeCode} status=store-not-found elapsedMs={stopwatch.ElapsedMilliseconds}");
            return null;
        }

        var response = directResult.Response;
        if (response is { Found: true, Item.PriceSource: PriceSourceKind.ProductBase })
        {
            response = await EnsureStoreRetailPriceAndLookupAsync(
                response.StoreCode,
                lookupCode,
                lookupCodeNormalized,
                response,
                directResult.Product,
                cancellationToken);
        }

        stopwatch.Stop();
        Log($"lookup service completed store={storeCode} status=ok found={response.Found} lookupCodeNormalized={response.LookupCodeNormalized} productCode={response.Item?.ProductCode ?? "<null>"} elapsedMs={stopwatch.ElapsedMilliseconds}");
        return response;
    }

    private async Task<CatalogDirectLookupResult?> LookupSellableItemDirectAsync(
        string storeCode,
        string? lookupCode,
        string? lookupCodeNormalized,
        CancellationToken cancellationToken)
    {
        var totalStopwatch = Stopwatch.StartNew();
        var normalizedStoreCode = NormalizeStoreCode(storeCode);
        var lookupCandidates = BuildLookupCandidates(lookupCode, lookupCodeNormalized);

        var store = await dbContext.MainDb.Queryable<Store>()
            .FirstAsync(x => x.StoreCode == normalizedStoreCode && x.IsActive && !x.IsDeleted, cancellationToken);
        if (store is null)
        {
            totalStopwatch.Stop();
            Log($"lookup direct store not found store={normalizedStoreCode} elapsedMs={totalStopwatch.ElapsedMilliseconds}");
            return null;
        }

        var candidates = new List<CatalogLookupCandidate>();
        if (lookupCandidates.Count > 0)
        {
            // 四类扫码来源投影为同一形状，一次 UNION 查询即可保留全部价格候选。
            var clearanceQuery = dbContext.MainDb.Queryable<StoreClearancePrice>()
                .Where(x =>
                    x.StoreCode == normalizedStoreCode &&
                    !x.IsDeleted &&
                    x.ClearanceBarcode != null &&
                    lookupCandidates.Contains(x.ClearanceBarcode))
                .Select(x => new CatalogLookupCandidate
                {
                    SourceKind = (int)PriceSourceKind.StoreClearancePrice,
                    ProductCode = x.ProductCode,
                    RelatedCode = null,
                    LookupCode = x.ClearanceBarcode,
                    RetailPrice = x.ClearancePrice,
                    DiscountRate = null,
                    ReferenceCode = x.UUID,
                    CreatedAt = x.CreatedAt,
                    UpdatedAt = x.UpdatedAt
                });
            var multiQuery = dbContext.MainDb.Queryable<StoreMultiCodeProduct>()
                .Where(x =>
                    x.StoreCode == normalizedStoreCode &&
                    x.IsActive &&
                    !x.IsDeleted &&
                    x.MultiBarcode != null &&
                    lookupCandidates.Contains(x.MultiBarcode))
                .Select(x => new CatalogLookupCandidate
                {
                    SourceKind = (int)PriceSourceKind.StoreMultiCodeProduct,
                    ProductCode = x.ProductCode,
                    RelatedCode = x.MultiCodeProductCode,
                    LookupCode = x.MultiBarcode,
                    RetailPrice = x.MultiCodeRetailPrice,
                    DiscountRate = x.DiscountRate,
                    ReferenceCode = x.UUID,
                    CreatedAt = x.CreatedAt,
                    UpdatedAt = x.UpdatedAt
                });
            var setQuery = dbContext.MainDb.Queryable<ProductSetCode>()
                .Where(x =>
                    x.IsActive &&
                    !x.IsDeleted &&
                    x.SetBarcode != null &&
                    lookupCandidates.Contains(x.SetBarcode))
                .Select(x => new CatalogLookupCandidate
                {
                    SourceKind = (int)PriceSourceKind.ProductSetCode,
                    ProductCode = x.ProductCode,
                    RelatedCode = x.SetProductCode,
                    LookupCode = x.SetBarcode,
                    RetailPrice = x.SetRetailPrice,
                    DiscountRate = null,
                    ReferenceCode = x.SetCodeId,
                    CreatedAt = x.CreatedAt,
                    UpdatedAt = x.UpdatedAt
                });
            // 商品停用只影响采购订货，门店仍可销售；收银目录只排除已删除商品。
            var productQuery = dbContext.MainDb.Queryable<Product>()
                .Where(x =>
                    !x.IsDeleted &&
                    ((x.Barcode != null && lookupCandidates.Contains(x.Barcode)) ||
                     (x.ItemNumber != null && lookupCandidates.Contains(x.ItemNumber))))
                .Select(x => new CatalogLookupCandidate
                {
                    SourceKind = (int)PriceSourceKind.ProductBase,
                    ProductCode = x.ProductCode,
                    RelatedCode = null,
                    LookupCode = x.Barcode,
                    RetailPrice = x.RetailPrice,
                    DiscountRate = null,
                    ReferenceCode = x.UUID,
                    CreatedAt = x.CreatedAt,
                    UpdatedAt = x.UpdatedAt
                });

            candidates = await dbContext.MainDb
                .UnionAll(clearanceQuery, multiQuery, setQuery, productQuery)
                .ToListAsync(cancellationToken);
        }

        var setProductCodes = candidates
            .Where(x => x.SourceKind == (int)PriceSourceKind.ProductSetCode)
            .Select(x => x.RelatedCode)
            .Where(HasText)
            .Select(x => x!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var setMultiCodeProductEntities = setProductCodes.Length == 0
            ? []
            : await dbContext.MainDb.Queryable<StoreMultiCodeProduct>()
                .Where(x =>
                    x.StoreCode == normalizedStoreCode &&
                    x.IsActive &&
                    !x.IsDeleted &&
                    x.MultiCodeProductCode != null &&
                    setProductCodes.Contains(x.MultiCodeProductCode))
                .ToListAsync(cancellationToken);

        var relatedProductCodes = candidates
            .Select(x => x.ProductCode)
            .Concat(setMultiCodeProductEntities.Select(x => x.ProductCode))
            .Where(HasText)
            .Select(x => x!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var productEntities = relatedProductCodes.Length == 0
            ? []
            : await dbContext.MainDb.Queryable<Product>()
                .Where(x =>
                    !x.IsDeleted &&
                    x.ProductCode != null &&
                    relatedProductCodes.Contains(x.ProductCode))
                .ToListAsync(cancellationToken);

        var productCodes = productEntities
            .Select(x => x.ProductCode)
            .Where(HasText)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var storeRetailPriceEntities = productCodes.Length == 0
            ? []
            : await dbContext.MainDb.Queryable<StoreRetailPrice>()
                .Where(x =>
                    x.StoreCode == normalizedStoreCode &&
                    x.IsActive &&
                    !x.IsDeleted &&
                    x.ProductCode != null &&
                    productCodes.Contains(x.ProductCode))
                .ToListAsync(cancellationToken);

        var multiCodeRecords = candidates
            .Where(x => x.SourceKind == (int)PriceSourceKind.StoreMultiCodeProduct)
            .Select(x => new StoreMultiCodeProductRecord(
                x.ProductCode,
                x.RelatedCode,
                x.LookupCode,
                x.RetailPrice,
                ToOffset(x.UpdatedAt ?? x.CreatedAt),
                x.ReferenceCode,
                x.DiscountRate))
            .Concat(setMultiCodeProductEntities.Select(x => new StoreMultiCodeProductRecord(
                x.ProductCode,
                x.MultiCodeProductCode,
                x.MultiBarcode,
                x.MultiCodeRetailPrice,
                ToOffset(x.UpdatedAt ?? x.CreatedAt),
                x.UUID,
                x.DiscountRate)))
            .GroupBy(x => x.ReferenceCode ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .ToList();

        var input = new PriceIndexInput(
            Since: null,
            productEntities
                .Select(x => new ProductPriceRecord(
                    x.ProductCode,
                    x.ProductName,
                    x.ItemNumber,
                    x.Barcode,
                    x.RetailPrice,
                    ToOffset(x.UpdatedAt ?? x.CreatedAt),
                    x.ProductImage,
                    x.UUID))
                .ToList(),
            storeRetailPriceEntities
                .Select(x => new StoreRetailPriceRecord(
                    x.ProductCode,
                    x.StoreRetailPriceValue,
                    ToOffset(x.UpdatedAt ?? x.CreatedAt),
                    x.UUID,
                    x.DiscountRate,
                    x.IsSpecialProduct))
                .ToList(),
            multiCodeRecords,
            candidates
                .Where(x => x.SourceKind == (int)PriceSourceKind.StoreClearancePrice)
                .Select(x => new StoreClearancePriceRecord(
                    x.ProductCode,
                    x.LookupCode,
                    x.RetailPrice,
                    ToOffset(x.UpdatedAt ?? x.CreatedAt),
                    x.ReferenceCode))
                .ToList(),
            candidates
                .Where(x => x.SourceKind == (int)PriceSourceKind.ProductSetCode)
                .Select(x => new ProductSetCodeRecord(
                    x.ProductCode ?? string.Empty,
                    x.RelatedCode ?? string.Empty,
                    x.LookupCode,
                    x.RetailPrice,
                    ToOffset(x.UpdatedAt ?? x.CreatedAt),
                    x.ReferenceCode))
                .ToList());

        var generatedAt = DateTimeOffset.UtcNow;
        var items = priceIndexBuilder.Build(store.StoreCode, input);
        var response = new CatalogSellableIndex(store.StoreCode, generatedAt, items)
            .Lookup(lookupCode, lookupCodeNormalized);

        totalStopwatch.Stop();
        Log($"lookup direct completed store={store.StoreCode} found={response.Found} candidates={candidates.Count} products={productEntities.Count} storePrices={storeRetailPriceEntities.Count} elapsedMs={totalStopwatch.ElapsedMilliseconds}");
        var matchedProduct = response.Item is null
            ? null
            : productEntities.FirstOrDefault(x =>
                StringComparer.OrdinalIgnoreCase.Equals(x.ProductCode, response.Item.ProductCode));
        return new CatalogDirectLookupResult(response, matchedProduct);
    }

    public async Task<CatalogSpecialProductsPageResponse?> GetSpecialProductsPageAsync(
        string storeCode,
        string? cursor,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var index = await BuildSellableIndexAsync(storeCode, since: null, cancellationToken);
        return index?.CatalogIndex.GetSpecialProductsPage(cursor, pageSize);
    }

    public async Task<CatalogCodeConflictsResponse?> GetCodeConflictsAsync(
        string storeCode,
        CancellationToken cancellationToken)
    {
        // 与特殊商品分页一样读取共享缓存的完整工件，不单独查库；门店不存在时返回 null。
        var index = await BuildSellableIndexAsync(storeCode, since: null, cancellationToken);
        if (index is null)
        {
            return null;
        }

        var codeConflicts = index.CodeConflicts;
        return new CatalogCodeConflictsResponse(
            index.StoreCode,
            index.GeneratedAt,
            codeConflicts is not null,
            codeConflicts is null
                ? []
                : codeConflicts.Select(CatalogSellableIndex.ToLookupItem).ToArray());
    }

    public async Task<CatalogSpecialProductMarkServiceResult> MarkSpecialProductAsync(
        CatalogSpecialProductMarkRequest request,
        string updatedBy,
        CancellationToken cancellationToken)
    {
        var totalStopwatch = Stopwatch.StartNew();
        var normalizedStoreCode = NormalizeStoreCode(request.StoreCode);
        var normalizedProductCode = NormalizeProductCode(request.ProductCode);
        Log($"mark special product start store={normalizedStoreCode} product={normalizedProductCode} isSpecialProduct={request.IsSpecialProduct}");
        if (string.IsNullOrEmpty(normalizedStoreCode))
        {
            totalStopwatch.Stop();
            Log($"mark special product failed store={normalizedStoreCode} product={normalizedProductCode} reason=store-code-required totalElapsedMs={totalStopwatch.ElapsedMilliseconds}");
            return CatalogSpecialProductMarkServiceResult.Fail("STORE_CODE_REQUIRED", "storeCode is required");
        }

        if (string.IsNullOrEmpty(normalizedProductCode))
        {
            totalStopwatch.Stop();
            Log($"mark special product failed store={normalizedStoreCode} product={normalizedProductCode} reason=product-code-required totalElapsedMs={totalStopwatch.ElapsedMilliseconds}");
            return CatalogSpecialProductMarkServiceResult.Fail("PRODUCT_CODE_REQUIRED", "productCode is required");
        }

        var storeStopwatch = Stopwatch.StartNew();
        var store = await dbContext.MainDb.Queryable<Store>()
            .FirstAsync(x => x.StoreCode == normalizedStoreCode && x.IsActive && !x.IsDeleted, cancellationToken);
        storeStopwatch.Stop();
        Log($"mark special product store query store={normalizedStoreCode} found={store is not null} elapsedMs={storeStopwatch.ElapsedMilliseconds}");
        if (store is null)
        {
            totalStopwatch.Stop();
            Log($"mark special product failed store={normalizedStoreCode} product={normalizedProductCode} reason=store-not-found totalElapsedMs={totalStopwatch.ElapsedMilliseconds}");
            return CatalogSpecialProductMarkServiceResult.Fail("STORE_NOT_FOUND", "store was not found or inactive");
        }

        var productStopwatch = Stopwatch.StartNew();
        var product = await dbContext.MainDb.Queryable<Product>()
            .FirstAsync(x => x.ProductCode == normalizedProductCode && !x.IsDeleted, cancellationToken);
        productStopwatch.Stop();
        Log($"mark special product product query store={normalizedStoreCode} product={normalizedProductCode} found={product is not null} elapsedMs={productStopwatch.ElapsedMilliseconds}");
        if (product is null)
        {
            totalStopwatch.Stop();
            Log($"mark special product failed store={normalizedStoreCode} product={normalizedProductCode} reason=product-not-found totalElapsedMs={totalStopwatch.ElapsedMilliseconds}");
            return CatalogSpecialProductMarkServiceResult.Fail("PRODUCT_NOT_FOUND", "product was not found or deleted");
        }

        var now = DateTime.UtcNow;
        var actor = string.IsNullOrWhiteSpace(updatedBy) ? "pos-device" : updatedBy.Trim();

        var transactionStopwatch = Stopwatch.StartNew();
        var retailQueryElapsedMs = 0L;
        var writeElapsedMs = 0L;
        var writeAction = "unknown";
        await dbContext.MainDb.Ado.BeginTranAsync();
        try
        {
            var retailQueryStopwatch = Stopwatch.StartNew();
            var storeRetailPrices = await dbContext.MainDb.Queryable<StoreRetailPrice>()
                .Where(x =>
                    x.StoreCode == normalizedStoreCode &&
                    x.ProductCode == normalizedProductCode &&
                    !x.IsDeleted)
                .ToListAsync(cancellationToken);
            retailQueryStopwatch.Stop();
            retailQueryElapsedMs = retailQueryStopwatch.ElapsedMilliseconds;

            if (storeRetailPrices.Count == 0)
            {
                writeAction = "insert";
                var storeRetailPrice = new StoreRetailPrice
                {
                    UUID = UuidHelper.GenerateUuid7(),
                    StoreCode = normalizedStoreCode,
                    ProductCode = normalizedProductCode,
                    StoreProductCode = UuidHelper.GenerateUuid7(),
                    SupplierCode = product.LocalSupplierCode,
                    PurchasePrice = product.PurchasePrice,
                    StoreRetailPriceValue = product.RetailPrice ?? 0m,
                    IsActive = true,
                    IsAutoPricing = product.IsAutoPricing,
                    IsSpecialProduct = request.IsSpecialProduct,
                    CreatedAt = now,
                    UpdatedAt = now,
                    CreatedBy = actor,
                    UpdatedBy = actor,
                    IsDeleted = false
                };

                var writeStopwatch = Stopwatch.StartNew();
                await dbContext.MainDb.Insertable(storeRetailPrice).ExecuteCommandAsync();
                writeStopwatch.Stop();
                writeElapsedMs = writeStopwatch.ElapsedMilliseconds;
            }
            else
            {
                var primaryStoreRetailPrice = storeRetailPrices
                    .Where(x => x.IsActive)
                    .OrderByDescending(x => x.UpdatedAt ?? x.CreatedAt)
                    .FirstOrDefault()
                    ?? storeRetailPrices
                        .OrderByDescending(x => x.UpdatedAt ?? x.CreatedAt)
                        .First();

                if (request.IsSpecialProduct &&
                    (!primaryStoreRetailPrice.IsActive || primaryStoreRetailPrice.StoreRetailPriceValue is null))
                {
                    // 添加时确保至少一条门店价能参与后续目录构建；删除不会改变历史失效行的启用状态。
                    writeAction = "restore-and-update";
                    primaryStoreRetailPrice.SupplierCode ??= product.LocalSupplierCode;
                    primaryStoreRetailPrice.PurchasePrice ??= product.PurchasePrice;
                    primaryStoreRetailPrice.StoreRetailPriceValue = product.RetailPrice ?? 0m;
                    primaryStoreRetailPrice.IsActive = true;
                    primaryStoreRetailPrice.IsAutoPricing = product.IsAutoPricing;
                }
                else
                {
                    writeAction = "update";
                }

                foreach (var storeRetailPrice in storeRetailPrices)
                {
                    storeRetailPrice.IsSpecialProduct = request.IsSpecialProduct;
                    if (ReferenceEquals(storeRetailPrice, primaryStoreRetailPrice))
                    {
                        // 主行唯一写入最新审计时间，保证 PriceIndexBuilder 选价稳定且可预测。
                        storeRetailPrice.UpdatedAt = now;
                        storeRetailPrice.UpdatedBy = actor;
                    }
                }

                var writeStopwatch = Stopwatch.StartNew();
                await dbContext.MainDb.Updateable(storeRetailPrices).ExecuteCommandAsync();
                writeStopwatch.Stop();
                writeElapsedMs = writeStopwatch.ElapsedMilliseconds;
            }

            await dbContext.MainDb.Ado.CommitTranAsync();
            transactionStopwatch.Stop();
            Log($"mark special product transaction store={normalizedStoreCode} product={normalizedProductCode} action={writeAction} retailQueryElapsedMs={retailQueryElapsedMs} writeElapsedMs={writeElapsedMs} transactionElapsedMs={transactionStopwatch.ElapsedMilliseconds}");
        }
        catch (Exception ex)
        {
            transactionStopwatch.Stop();
            await dbContext.MainDb.Ado.RollbackTranAsync();
            Log($"mark special product transaction failed store={normalizedStoreCode} product={normalizedProductCode} action={writeAction} retailQueryElapsedMs={retailQueryElapsedMs} writeElapsedMs={writeElapsedMs} transactionElapsedMs={transactionStopwatch.ElapsedMilliseconds} error={ex.Message}");
            throw;
        }

        var invalidateStopwatch = Stopwatch.StartNew();
        catalogIndexCache.InvalidateStore(normalizedStoreCode);
        invalidateStopwatch.Stop();

        var itemBuildStopwatch = Stopwatch.StartNew();
        // 写入已经提交，不能再为了本次响应同步重建整店目录；只构建当前商品即可让 POS 立即更新本地货架。
        var items = await BuildMarkedSpecialProductItemsAsync(
            store.StoreCode,
            product,
            request.IsSpecialProduct,
            cancellationToken);
        itemBuildStopwatch.Stop();
        totalStopwatch.Stop();
        Log($"mark special product completed store={normalizedStoreCode} product={normalizedProductCode} isSpecialProduct={request.IsSpecialProduct} items={items.Count} storeQueryElapsedMs={storeStopwatch.ElapsedMilliseconds} productQueryElapsedMs={productStopwatch.ElapsedMilliseconds} retailQueryElapsedMs={retailQueryElapsedMs} writeElapsedMs={writeElapsedMs} transactionElapsedMs={transactionStopwatch.ElapsedMilliseconds} cacheInvalidateElapsedMs={invalidateStopwatch.ElapsedMilliseconds} itemBuildElapsedMs={itemBuildStopwatch.ElapsedMilliseconds} totalElapsedMs={totalStopwatch.ElapsedMilliseconds}");

        return CatalogSpecialProductMarkServiceResult.Ok(new CatalogSpecialProductMarkResponse(
            normalizedStoreCode,
            normalizedProductCode,
            request.IsSpecialProduct,
            DateTimeOffset.UtcNow,
            items));
    }

    private Task<IReadOnlyList<CatalogLookupItemDto>> BuildMarkedSpecialProductItemsAsync(
        string storeCode,
        Product product,
        bool isSpecialProduct,
        CancellationToken cancellationToken)
    {
        var normalizedStoreCode = NormalizeStoreCode(storeCode);
        var normalizedProductCode = NormalizeProductCode(product.ProductCode);
        return RunCatalogSnapshotReadAsync(
            token => BuildMarkedSpecialProductItemsCoreAsync(
                normalizedStoreCode,
                normalizedProductCode,
                isSpecialProduct,
                token),
            cancellationToken);
    }

    private async Task<IReadOnlyList<CatalogLookupItemDto>> BuildMarkedSpecialProductItemsCoreAsync(
        string normalizedStoreCode,
        string normalizedProductCode,
        bool isSpecialProduct,
        CancellationToken cancellationToken)
    {
        var totalStopwatch = Stopwatch.StartNew();

        var product = await dbContext.MainDb.Queryable<Product>()
            .With(SqlWith.Null)
            .FirstAsync(x =>
                x.ProductCode == normalizedProductCode &&
                !x.IsDeleted,
                cancellationToken);
        if (product is null)
        {
            return [];
        }

        var multiCodeProductEntities = await dbContext.MainDb.Queryable<StoreMultiCodeProduct>()
            .With(SqlWith.Null)
            .Where(x =>
                x.StoreCode == normalizedStoreCode &&
                x.ProductCode == normalizedProductCode &&
                x.IsActive &&
                !x.IsDeleted)
            .Select(x => new StoreMultiCodeProduct
            {
                ProductCode = x.ProductCode,
                MultiCodeProductCode = x.MultiCodeProductCode,
                MultiBarcode = x.MultiBarcode,
                MultiCodeRetailPrice = x.MultiCodeRetailPrice,
                UpdatedAt = x.UpdatedAt,
                CreatedAt = x.CreatedAt,
                UUID = x.UUID,
                DiscountRate = x.DiscountRate
            })
            .ToListAsync(cancellationToken);

        var clearancePriceEntities = await dbContext.MainDb.Queryable<StoreClearancePrice>()
            .With(SqlWith.Null)
            .Where(x =>
                x.StoreCode == normalizedStoreCode &&
                x.ProductCode == normalizedProductCode &&
                !x.IsDeleted)
            .Select(x => new StoreClearancePrice
            {
                ProductCode = x.ProductCode,
                ClearanceBarcode = x.ClearanceBarcode,
                ClearancePrice = x.ClearancePrice,
                UpdatedAt = x.UpdatedAt,
                CreatedAt = x.CreatedAt,
                UUID = x.UUID
            })
            .ToListAsync(cancellationToken);

        var productSetCodeEntities = await dbContext.MainDb.Queryable<ProductSetCode>()
            .With(SqlWith.Null)
            .Where(x =>
                x.ProductCode == normalizedProductCode &&
                x.IsActive &&
                !x.IsDeleted)
            .Select(x => new ProductSetCode
            {
                ProductCode = x.ProductCode,
                SetProductCode = x.SetProductCode,
                SetBarcode = x.SetBarcode,
                SetRetailPrice = x.SetRetailPrice,
                UpdatedAt = x.UpdatedAt,
                CreatedAt = x.CreatedAt,
                SetCodeId = x.SetCodeId
            })
            .ToListAsync(cancellationToken);

        var candidateLookupCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddCandidateLookupCode(candidateLookupCodes, product.Barcode);
        AddCandidateLookupCode(candidateLookupCodes, product.ItemNumber);
        foreach (var multiCodeProduct in multiCodeProductEntities)
        {
            AddCandidateLookupCode(candidateLookupCodes, multiCodeProduct.MultiBarcode);
        }

        foreach (var clearancePrice in clearancePriceEntities)
        {
            AddCandidateLookupCode(candidateLookupCodes, clearancePrice.ClearanceBarcode);
        }

        foreach (var productSetCode in productSetCodeEntities)
        {
            AddCandidateLookupCode(candidateLookupCodes, productSetCode.SetBarcode);
        }

        var items = new List<CatalogLookupItemDto>();
        foreach (var lookupCode in candidateLookupCodes.OrderBy(x => x, StringComparer.Ordinal))
        {
            // 对每个目标编码仍使用全局候选集裁决，避免跨商品重复条码时错误返回已被覆盖的目标商品行。
            var directResult = await LookupSellableItemDirectAsync(
                normalizedStoreCode,
                lookupCode,
                lookupCode.ToUpperInvariant(),
                cancellationToken);
            var winner = directResult?.Response.Item;
            if (winner is null ||
                !string.Equals(winner.ProductCode, normalizedProductCode, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // 多码、清仓码和套装码的特殊标记属于当前门店商品，随本次写入统一覆盖。
            items.Add(winner with { IsSpecialProduct = isSpecialProduct });
        }

        var canonicalItems = items
            .GroupBy(item => item.LookupCodeNormalized, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(item => item.LookupCodeNormalized, StringComparer.Ordinal)
            .ToArray();
        totalStopwatch.Stop();
        Log($"mark special product item build store={normalizedStoreCode} product={normalizedProductCode} multiCodes={multiCodeProductEntities.Count} clearanceCodes={clearancePriceEntities.Count} setCodes={productSetCodeEntities.Count} candidates={candidateLookupCodes.Count} items={canonicalItems.Length} elapsedMs={totalStopwatch.ElapsedMilliseconds}");
        return canonicalItems;
    }

    private async Task<CatalogIndexBuildResult?> BuildSellableIndexAsync(
        string storeCode,
        DateTimeOffset? since,
        CancellationToken cancellationToken,
        bool requireFresh = false)
    {
        var normalizedStoreCode = NormalizeStoreCode(storeCode);
        var buildAsync = new Func<CancellationToken, Task<CatalogIndexBuildResult?>>(
            // 共享缓存只构建完整门店目录；legacy since 在缓存内从该工件派生。
            token => BuildSellableIndexCoreAsync(normalizedStoreCode, since: null, token));
        return requireFresh
            ? await catalogIndexCache.GetOrBuildFreshAsync(
                normalizedStoreCode,
                since,
                buildAsync,
                cancellationToken)
            : await catalogIndexCache.GetOrBuildAsync(
                normalizedStoreCode,
                since,
                buildAsync,
                cancellationToken);
    }

    public async Task RefreshCatalogIndexAsync(
        string storeCode,
        CancellationToken cancellationToken)
    {
        var normalizedStoreCode = NormalizeStoreCode(storeCode);
        _ = await catalogIndexCache.ForceRefreshAndPublishAsync(
            normalizedStoreCode,
            since: null,
            token => BuildSellableIndexCoreAsync(normalizedStoreCode, since: null, token),
            cancellationToken);
    }

    /// <summary>
    /// 仅由 singleton 缓存协调器在自有 DI scope 中调用，绝不再捕获 HTTP scope 的 DbContext。
    /// </summary>
    public Task<CatalogIndexBuildResult?> BuildCatalogArtifactAsync(
        string storeCode,
        CancellationToken cancellationToken)
    {
        return BuildSellableIndexCoreAsync(NormalizeStoreCode(storeCode), since: null, cancellationToken);
    }

    private async Task<CatalogIndexBuildResult?> BuildSellableIndexCoreAsync(
        string normalizedStoreCode,
        DateTimeOffset? since,
        CancellationToken cancellationToken)
    {
        var totalStopwatch = Stopwatch.StartNew();
        Log($"build index start store={normalizedStoreCode} since={since?.ToString("O") ?? "<null>"}");

        var stepStopwatch = Stopwatch.StartNew();
        var store = await dbContext.MainDb.Queryable<Store>()
            .FirstAsync(x => x.StoreCode == normalizedStoreCode && x.IsActive && !x.IsDeleted, cancellationToken);
        stepStopwatch.Stop();
        Log($"store query store={normalizedStoreCode} found={store is not null} elapsedMs={stepStopwatch.ElapsedMilliseconds}");

        if (store is null)
        {
            totalStopwatch.Stop();
            Log($"build index store not found store={normalizedStoreCode} elapsedMs={totalStopwatch.ElapsedMilliseconds}");
            return null;
        }

        stepStopwatch.Restart();
        var baseData = await catalogBaseDataCache.GetOrCreateAsync(
            BuildCatalogBaseDataAsync,
            waiterCancellationToken: cancellationToken,
            buildCancellationToken: cancellationToken);
        stepStopwatch.Stop();
        Log($"base data ready store={normalizedStoreCode} products={baseData.Products.Count} setCodes={baseData.SetCodes.Count} generatedAt={baseData.GeneratedAt:O} elapsedMs={stepStopwatch.ElapsedMilliseconds}");

        // 门店价、多码、清仓价必须在同一个 SNAPSHOT 事务中读取，避免跨批次拼出不存在的目录。
        List<StoreRetailPriceRecord> storeRetailPrices;
        List<StoreMultiCodeProductRecord> multiCodeProducts;
        List<StoreClearancePriceRecord> clearancePrices;
        var storeReadTransactionStarted = await BeginCatalogSnapshotReadTransactionAsync(cancellationToken);
        try
        {
        stepStopwatch.Restart();
        storeRetailPrices = new List<StoreRetailPriceRecord>();
        string? lastProductCode = null;
        string? lastUuid = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batchStopwatch = Stopwatch.StartNew();
            var storeRetailPriceQuery = dbContext.MainDb.Queryable<StoreRetailPrice>()
                .With(SqlWith.Null)
                .Where(x => x.StoreCode == normalizedStoreCode
                    && x.IsActive
                    && !x.IsDeleted
                    && x.ProductCode != null);

            if (lastProductCode is not null && lastUuid is not null)
            {
                // 沿用现有复合索引顺序做键集分页，避免越往后的 OFFSET 扫描越慢。
                storeRetailPriceQuery = storeRetailPriceQuery.Where(
                    "(([ProductCode] > @lastProductCode) OR ([ProductCode] = @lastProductCode AND [UUID] > @lastUuid))",
                    new { lastProductCode, lastUuid });
            }

            // 只从数据库读取目录构建实际使用的字段，减少大门店的数据传输和实体映射成本。
            var storeRetailPriceBatch = await storeRetailPriceQuery
                .OrderBy(x => x.ProductCode)
                .OrderBy(x => x.UUID)
                .Select(x => new StoreRetailPrice
                {
                    ProductCode = x.ProductCode,
                    StoreRetailPriceValue = x.StoreRetailPriceValue,
                    UpdatedAt = x.UpdatedAt,
                    CreatedAt = x.CreatedAt,
                    UUID = x.UUID,
                    DiscountRate = x.DiscountRate,
                    IsSpecialProduct = x.IsSpecialProduct
                })
                .Take(CatalogSourceBatchSize)
                .ToListAsync(cancellationToken);

            storeRetailPrices.AddRange(storeRetailPriceBatch.Select(x => new StoreRetailPriceRecord(
                x.ProductCode,
                x.StoreRetailPriceValue,
                ToOffset(x.UpdatedAt ?? x.CreatedAt),
                x.UUID,
                x.DiscountRate,
                x.IsSpecialProduct)));
            batchStopwatch.Stop();
            Log($"store retail prices batch query store={normalizedStoreCode} rows={storeRetailPriceBatch.Count} total={storeRetailPrices.Count} elapsedMs={batchStopwatch.ElapsedMilliseconds}");

            if (storeRetailPriceBatch.Count < CatalogSourceBatchSize)
            {
                break;
            }

            var lastStoreRetailPrice = storeRetailPriceBatch[^1];
            lastProductCode = lastStoreRetailPrice.ProductCode;
            lastUuid = lastStoreRetailPrice.UUID;
        }

        stepStopwatch.Stop();
        Log($"store retail prices query store={normalizedStoreCode} count={storeRetailPrices.Count} elapsedMs={stepStopwatch.ElapsedMilliseconds}");

        stepStopwatch.Restart();
        var multiCodeProductEntities = await dbContext.MainDb.Queryable<StoreMultiCodeProduct>()
            .With(SqlWith.Null)
            .Where(x => x.StoreCode == normalizedStoreCode && x.IsActive && !x.IsDeleted)
            .Select(x => new StoreMultiCodeProduct
            {
                ProductCode = x.ProductCode,
                MultiCodeProductCode = x.MultiCodeProductCode,
                MultiBarcode = x.MultiBarcode,
                MultiCodeRetailPrice = x.MultiCodeRetailPrice,
                UpdatedAt = x.UpdatedAt,
                CreatedAt = x.CreatedAt,
                UUID = x.UUID,
                DiscountRate = x.DiscountRate
            })
            .ToListAsync(cancellationToken);
        stepStopwatch.Stop();
        Log($"multi code products query store={normalizedStoreCode} count={multiCodeProductEntities.Count} elapsedMs={stepStopwatch.ElapsedMilliseconds}");
        multiCodeProducts = multiCodeProductEntities
            .Select(x => new StoreMultiCodeProductRecord(
                x.ProductCode,
                x.MultiCodeProductCode,
                x.MultiBarcode,
                x.MultiCodeRetailPrice,
                ToOffset(x.UpdatedAt ?? x.CreatedAt),
                x.UUID,
                x.DiscountRate))
            .ToList();

        stepStopwatch.Restart();
        var clearancePriceEntities = await dbContext.MainDb.Queryable<StoreClearancePrice>()
            .With(SqlWith.Null)
            .Where(x => x.StoreCode == normalizedStoreCode && !x.IsDeleted)
            .Select(x => new StoreClearancePrice
            {
                ProductCode = x.ProductCode,
                ClearanceBarcode = x.ClearanceBarcode,
                ClearancePrice = x.ClearancePrice,
                UpdatedAt = x.UpdatedAt,
                CreatedAt = x.CreatedAt,
                UUID = x.UUID
            })
            .ToListAsync(cancellationToken);
        stepStopwatch.Stop();
        Log($"clearance prices query store={normalizedStoreCode} count={clearancePriceEntities.Count} elapsedMs={stepStopwatch.ElapsedMilliseconds}");
        clearancePrices = clearancePriceEntities
            .Select(x => new StoreClearancePriceRecord(
                x.ProductCode,
                x.ClearanceBarcode,
                x.ClearancePrice,
                ToOffset(x.UpdatedAt ?? x.CreatedAt),
                x.UUID))
            .ToList();

        if (storeReadTransactionStarted)
        {
            await dbContext.MainDb.Ado.CommitTranAsync();
        }
        }
        catch
        {
            if (storeReadTransactionStarted)
            {
                await dbContext.MainDb.Ado.RollbackTranAsync();
            }

            throw;
        }

        var input = new PriceIndexInput(
            since,
            baseData.Products,
            storeRetailPrices,
            multiCodeProducts,
            clearancePrices,
            baseData.SetCodes);

        var generatedAt = DateTimeOffset.UtcNow;
        stepStopwatch.Restart();
        IReadOnlyList<SellableItemDto> items;
        IReadOnlyList<SellableItemDto>? codeConflicts = null;
        if (since is null)
        {
            // 完整目录在同一次遍历中收集码冲突候选，供新版收银端在扫码时让收银员选择。
            var buildOutput = priceIndexBuilder.BuildWithCodeConflicts(store.StoreCode, input);
            items = buildOutput.Items;
            codeConflicts = buildOutput.CodeConflicts;
        }
        else
        {
            items = priceIndexBuilder.Build(store.StoreCode, input);
        }

        stepStopwatch.Stop();
        totalStopwatch.Stop();
        Log($"build index completed store={store.StoreCode} items={items.Count} codeConflictItems={codeConflicts?.Count.ToString(CultureInfo.InvariantCulture) ?? "<null>"} buildElapsedMs={stepStopwatch.ElapsedMilliseconds} totalElapsedMs={totalStopwatch.ElapsedMilliseconds}");
        return new CatalogIndexBuildResult(
            store.StoreCode,
            generatedAt,
            items,
            new CatalogSellableIndex(store.StoreCode, generatedAt, items),
            baseData.ValidUntil,
            // 完整工件保留只读候选，供 legacy since 在内存中重新投影；不会二次读数据库。
            input,
            codeConflicts);
    }

    private Task<CatalogBaseData> BuildCatalogBaseDataAsync(
        CancellationToken cancellationToken)
    {
        return RunCatalogSnapshotReadAsync(BuildCatalogBaseDataCoreAsync, cancellationToken);
    }

    private async Task<CatalogBaseData> BuildCatalogBaseDataCoreAsync(
        CancellationToken cancellationToken)
    {
        var totalStopwatch = Stopwatch.StartNew();
        var products = new List<ProductPriceRecord>();
        string? lastProductCode = null;
        string? lastProductUuid = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batchStopwatch = Stopwatch.StartNew();
            // 停用商品门店仍可销售，全量目录与扫码查询口径一致，只排除已删除商品。
            var productQuery = dbContext.MainDb.Queryable<Product>()
                .With(SqlWith.Null)
                .Where(x => !x.IsDeleted && x.ProductCode != null && x.UUID != null);

            if (lastProductCode is not null && lastProductUuid is not null)
            {
                // 全局商品基础按覆盖索引顺序键集读取；10 万批次减少重复 SQL 往返和范围扫描。
                productQuery = productQuery.Where(
                    "(([ProductCode] > @lastProductCode) OR ([ProductCode] = @lastProductCode AND [UUID] > @lastUuid))",
                    new { lastProductCode, lastUuid = lastProductUuid });
            }

            var productBatch = await productQuery
                .OrderBy(x => x.ProductCode)
                .OrderBy(x => x.UUID)
                .Select(x => new Product
                {
                    ProductCode = x.ProductCode,
                    ProductName = x.ProductName,
                    ItemNumber = x.ItemNumber,
                    Barcode = x.Barcode,
                    RetailPrice = x.RetailPrice,
                    UpdatedAt = x.UpdatedAt,
                    CreatedAt = x.CreatedAt,
                    ProductImage = x.ProductImage,
                    UUID = x.UUID
                })
                .Take(CatalogSourceBatchSize)
                .ToListAsync(cancellationToken);

            products.AddRange(productBatch.Select(x => new ProductPriceRecord(
                x.ProductCode,
                x.ProductName,
                x.ItemNumber,
                x.Barcode,
                x.RetailPrice,
                ToOffset(x.UpdatedAt ?? x.CreatedAt),
                x.ProductImage,
                x.UUID)));
            batchStopwatch.Stop();
            Log($"base products batch rows={productBatch.Count} total={products.Count} elapsedMs={batchStopwatch.ElapsedMilliseconds}");

            if (productBatch.Count < CatalogSourceBatchSize)
            {
                break;
            }

            var lastProduct = productBatch[^1];
            lastProductCode = lastProduct.ProductCode;
            lastProductUuid = lastProduct.UUID;
        }

        var setCodeStopwatch = Stopwatch.StartNew();
        var setCodeEntities = await dbContext.MainDb.Queryable<ProductSetCode>()
            .With(SqlWith.Null)
            .Where(x => x.IsActive && !x.IsDeleted)
            .Select(x => new ProductSetCode
            {
                ProductCode = x.ProductCode,
                SetProductCode = x.SetProductCode,
                SetBarcode = x.SetBarcode,
                SetRetailPrice = x.SetRetailPrice,
                UpdatedAt = x.UpdatedAt,
                CreatedAt = x.CreatedAt,
                SetCodeId = x.SetCodeId
            })
            .ToListAsync(cancellationToken);
        setCodeStopwatch.Stop();
        var setCodes = setCodeEntities
            .Select(x => new ProductSetCodeRecord(
                x.ProductCode,
                x.SetProductCode,
                x.SetBarcode,
                x.SetRetailPrice,
                ToOffset(x.UpdatedAt ?? x.CreatedAt),
                x.SetCodeId))
            .ToArray();
        totalStopwatch.Stop();
        var generatedAt = DateTimeOffset.UtcNow;
        Log($"base data build completed products={products.Count} setCodes={setCodes.Length} setCodeElapsedMs={setCodeStopwatch.ElapsedMilliseconds} totalElapsedMs={totalStopwatch.ElapsedMilliseconds}");
        return new CatalogBaseData(generatedAt, products.ToArray(), setCodes);
    }

    private async Task<CatalogLookupResponse> EnsureStoreRetailPriceAndLookupAsync(
        string normalizedStoreCode,
        string? lookupCode,
        string? lookupCodeNormalized,
        CatalogLookupResponse currentResponse,
        Product? product,
        CancellationToken cancellationToken)
    {
        var productCode = NormalizeProductCode(currentResponse.Item?.ProductCode);
        if (string.IsNullOrEmpty(productCode))
        {
            return currentResponse;
        }

        if (product is null)
        {
            Log($"lookup store retail ensure skipped store={normalizedStoreCode} product={productCode} reason=product-not-loaded");
            return currentResponse;
        }

        var ensureLock = StoreRetailPriceEnsureLocks.GetOrAdd(
            StoreRetailPriceEnsureLockKey(normalizedStoreCode, productCode),
            _ => new SemaphoreSlim(1, 1));
        await ensureLock.WaitAsync(cancellationToken);
        try
        {
            var now = DateTime.UtcNow;
            var writeAction = "none";
            StoreRetailPrice? storeRetailPrice = null;
            await dbContext.MainDb.Ado.BeginTranAsync();
            try
            {
                storeRetailPrice = await dbContext.MainDb.Queryable<StoreRetailPrice>()
                    .FirstAsync(x =>
                        x.StoreCode == normalizedStoreCode &&
                        x.ProductCode == productCode &&
                        !x.IsDeleted,
                        cancellationToken);

                if (storeRetailPrice is null)
                {
                    writeAction = "insert";
                    // 本地 lookup 命中商品主档但没有分店价时，立即复制主档价格创建分店价，供 POS 本次扫码使用。
                    storeRetailPrice = new StoreRetailPrice
                    {
                        UUID = UuidHelper.GenerateUuid7(),
                        StoreCode = normalizedStoreCode,
                        ProductCode = productCode,
                        StoreProductCode = UuidHelper.GenerateUuid7(),
                        SupplierCode = product.LocalSupplierCode,
                        PurchasePrice = product.PurchasePrice,
                        StoreRetailPriceValue = product.RetailPrice ?? 0m,
                        IsActive = true,
                        IsAutoPricing = product.IsAutoPricing,
                        IsSpecialProduct = product.IsSpecialProduct,
                        CreatedAt = now,
                        UpdatedAt = now,
                        CreatedBy = "pos-device",
                        UpdatedBy = "pos-device",
                        IsDeleted = false
                    };

                    await dbContext.MainDb.Insertable(storeRetailPrice).ExecuteCommandAsync();
                }
                else if (!storeRetailPrice.IsActive || storeRetailPrice.StoreRetailPriceValue is null)
                {
                    writeAction = "update";
                    // 已有分店价记录但不可用于索引时，补齐主档价格并重新启用，避免继续返回 ProductBase。
                    storeRetailPrice.SupplierCode ??= product.LocalSupplierCode;
                    storeRetailPrice.PurchasePrice ??= product.PurchasePrice;
                    storeRetailPrice.StoreRetailPriceValue = product.RetailPrice ?? 0m;
                    storeRetailPrice.IsActive = true;
                    storeRetailPrice.IsAutoPricing = product.IsAutoPricing;
                    storeRetailPrice.IsSpecialProduct = product.IsSpecialProduct;
                    storeRetailPrice.UpdatedAt = now;
                    storeRetailPrice.UpdatedBy = "pos-device";

                    await dbContext.MainDb.Updateable(storeRetailPrice).ExecuteCommandAsync();
                }
                else
                {
                    // 缓存可能仍停在 ProductBase；即使数据库已有可用分店价，也要重建该店索引后再返回。
                    writeAction = "refresh";
                }

                await dbContext.MainDb.Ado.CommitTranAsync();
            }
            catch
            {
                await dbContext.MainDb.Ado.RollbackTranAsync();
                throw;
            }

            if (writeAction == "none")
            {
                return currentResponse;
            }

            catalogIndexCache.InvalidateStore(normalizedStoreCode);
            if (storeRetailPrice is null)
            {
                return currentResponse;
            }

            // 复用本次 lookup 已读取的商品和刚写入的门店价，避免再次执行候选 UNION。
            var input = new PriceIndexInput(
                Since: null,
                [new ProductPriceRecord(
                    product.ProductCode,
                    product.ProductName,
                    product.ItemNumber,
                    product.Barcode,
                    product.RetailPrice,
                    ToOffset(product.UpdatedAt ?? product.CreatedAt),
                    product.ProductImage,
                    product.UUID)],
                [new StoreRetailPriceRecord(
                    storeRetailPrice.ProductCode,
                    storeRetailPrice.StoreRetailPriceValue,
                    ToOffset(storeRetailPrice.UpdatedAt ?? storeRetailPrice.CreatedAt),
                    storeRetailPrice.UUID,
                    storeRetailPrice.DiscountRate,
                    storeRetailPrice.IsSpecialProduct)],
                [],
                [],
                []);
            var generatedAt = DateTimeOffset.UtcNow;
            var items = priceIndexBuilder.Build(normalizedStoreCode, input);
            var refreshedResponse = new CatalogSellableIndex(normalizedStoreCode, generatedAt, items)
                .Lookup(lookupCode, lookupCodeNormalized);
            Log($"lookup store retail ensured store={normalizedStoreCode} product={productCode} action={writeAction} refreshed={refreshedResponse.Found}");
            return refreshedResponse;
        }
        finally
        {
            ensureLock.Release();
        }
    }

    private static DateTimeOffset? ToOffset(DateTime? value)
    {
        return value is null
            ? null
            : new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc));
    }

    private bool IsDeltaEnabled => catalogSyncOptions?.Value.DeltaEnabled ?? true;

    private static string NormalizeStoreCode(string? value)
    {
        return (value ?? string.Empty).Trim();
    }

    private static string NormalizeProductCode(string? value)
    {
        return (value ?? string.Empty).Trim();
    }

    private static bool HasText(string? value)
    {
        return !string.IsNullOrWhiteSpace(value);
    }

    private static IReadOnlyList<string> BuildLookupCandidates(string? lookupCode, string? lookupCodeNormalized)
    {
        var candidates = new HashSet<string>(StringComparer.Ordinal);
        AddLookupCandidate(candidates, lookupCode);
        AddLookupCandidate(candidates, lookupCodeNormalized);
        return candidates.ToArray();
    }

    private static void AddLookupCandidate(HashSet<string> candidates, string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return;
        }

        candidates.Add(trimmed);
        candidates.Add(trimmed.ToUpperInvariant());
        candidates.Add(trimmed.ToLowerInvariant());
    }

    private static void AddCandidateLookupCode(HashSet<string> candidates, string? value)
    {
        var trimmed = value?.Trim();
        if (!string.IsNullOrEmpty(trimmed))
        {
            candidates.Add(trimmed);
        }
    }

    private sealed record CatalogDirectLookupResult(
        CatalogLookupResponse Response,
        Product? Product);

    private sealed class CatalogLookupCandidate
    {
        public int SourceKind { get; set; }

        public string? ProductCode { get; set; }

        public string? RelatedCode { get; set; }

        public string? LookupCode { get; set; }

        public decimal? RetailPrice { get; set; }

        public decimal? DiscountRate { get; set; }

        public string? ReferenceCode { get; set; }

        public DateTime CreatedAt { get; set; }

        public DateTime? UpdatedAt { get; set; }
    }

    private static string StoreRetailPriceEnsureLockKey(string storeCode, string productCode)
    {
        return string.Concat(
            NormalizeStoreCode(storeCode).ToUpperInvariant(),
            "|",
            NormalizeProductCode(productCode).ToUpperInvariant());
    }

    private async Task<T> RunCatalogSnapshotReadAsync<T>(
        Func<CancellationToken, Task<T>> readAsync,
        CancellationToken cancellationToken)
    {
        var transactionStarted = await BeginCatalogSnapshotReadTransactionAsync(cancellationToken);
        try
        {
            var result = await readAsync(cancellationToken);
            if (transactionStarted)
            {
                await dbContext.MainDb.Ado.CommitTranAsync();
            }

            return result;
        }
        catch
        {
            if (transactionStarted)
            {
                await dbContext.MainDb.Ado.RollbackTranAsync();
            }

            throw;
        }
    }

    private async Task<bool> BeginCatalogSnapshotReadTransactionAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (dbContext.MainDb.CurrentConnectionConfig.DbType != SqlSugar.DbType.SqlServer)
        {
            // SQLite 单元测试不支持 SNAPSHOT；生产 MainDb 固定为 SQL Server，绝不在生产静默降级。
            return false;
        }

        try
        {
            await dbContext.MainDb.Ado.BeginTranAsync(IsolationLevel.Snapshot);
            return true;
        }
        catch (Exception exception)
        {
            throw new CatalogSnapshotIsolationUnavailableException(exception);
        }
    }

    private static void Log(string message)
    {
        Console.WriteLine($"[HBPOS][Api][CatalogService] {DateTimeOffset.Now:O} {message}");
    }

}

public sealed class CatalogSellableIndex
{
    private const int MaxPageSize = 5000;
    // 中文注释：v2 标准页定长；客户端默认 pageSize 恰为该值，可命中标准页缓存。
    private const int StandardPageSize = 5000;
    private const string CatalogVersionPrefix = "catalog-v1:";
    private const string PageChecksumV1AlgorithmMarker = "HBPOS-CATALOG-PAGE-CHECKSUM-V1";
    private const string PageChecksumV1Prefix = "sha256-catalog-page-v1:";
    private const string PageChecksumV2AlgorithmMarker = "HBPOS-CATALOG-PAGE-CHECKSUM-V2";
    private const string PageChecksumV2Prefix = "sha256-catalog-page-v2:";
    private const string DeltaPageChecksumV1AlgorithmMarker = "HBPOS-CATALOG-DELTA-PAGE-CHECKSUM-V1";
    private const string DeltaPageChecksumV1Prefix = "sha256-catalog-delta-page-v1:";
    private readonly IReadOnlyDictionary<string, CatalogLookupItemDto> _itemsByNormalizedLookup;
    // 中文注释：v2 标准页（定长 5000 条）缓存，键为页序号；Lazy 保证并发只计算一次。
    // 条目只缓存 checksum/nextCursor/hasMore（商品数组仍从不可变索引切窗），内存开销极小。
    private readonly ConcurrentDictionary<int, Lazy<StandardV2PageEntry>> _standardV2PageCache = new();
    // 中文注释：进程级最近一次 v1 分页请求时间戳（TickCount64），供 v2 预热避让 WPF 下载。
    private static long _lastV1PageRequestTicks;

    internal static long LastV1PageRequestTicks => Interlocked.Read(ref _lastV1PageRequestTicks);

    private sealed record StandardV2PageEntry(
        string PageChecksum,
        string? NextCursor,
        bool HasMore);

    public CatalogSellableIndex(
        string storeCode,
        DateTimeOffset generatedAt,
        IEnumerable<SellableItemDto> items,
        string? catalogVersion = null)
    {
        StoreCode = NormalizeStoreCode(storeCode);
        GeneratedAt = generatedAt;
        CatalogVersion = string.IsNullOrWhiteSpace(catalogVersion)
            ? string.Concat(CatalogVersionPrefix, Guid.NewGuid().ToString("N"))
            : catalogVersion.Trim();

        Items = items
            .Select(ToLookupItem)
            .Where(x => HasText(x.StoreCode) && HasText(x.LookupCodeNormalized))
            .GroupBy(x => x.LookupCodeNormalized, StringComparer.Ordinal)
            .Select(x => x
                .OrderByDescending(item => item.PriceSource)
                .ThenByDescending(item => item.UpdatedAt ?? DateTimeOffset.MinValue)
                .ThenBy(item => item.LookupCode, StringComparer.Ordinal)
                .First())
            .OrderBy(x => x.LookupCodeNormalized, StringComparer.Ordinal)
            .ToArray();

        _itemsByNormalizedLookup = Items.ToDictionary(
            x => x.LookupCodeNormalized,
            StringComparer.Ordinal);
    }

    public string StoreCode { get; }

    public DateTimeOffset GeneratedAt { get; }

    public string CatalogVersion { get; }

    public IReadOnlyList<CatalogLookupItemDto> Items { get; }

    public CatalogSyncPageResponse GetPage(
        string? cursor,
        int pageSize,
        int checksumVersion = 1)
    {
        if (checksumVersion is not 1 and not 2)
        {
            throw new ArgumentOutOfRangeException(nameof(checksumVersion));
        }

        if (checksumVersion == 1)
        {
            // 中文注释：v1 打点供 v2 预热避让；不改变 v1 任何响应行为。
            Interlocked.Exchange(ref _lastV1PageRequestTicks, Environment.TickCount64);
        }

        var normalizedCursor = NormalizeLookupCode(cursor);
        if (checksumVersion == 2 &&
            pageSize == StandardPageSize &&
            TryGetStandardV2Page(normalizedCursor, out var standardPage))
        {
            return standardPage;
        }

        var take = Math.Clamp(pageSize, 1, MaxPageSize);
        var start = FindFirstAfter(normalizedCursor);
        // 游标已通过二分定位；直接按索引拷贝窗口，不能用 Skip 从数组头线性重扫。
        var remaining = Items.Count - start;
        var pageLength = Math.Min(take, Math.Max(remaining, 0));
        var pageItems = new CatalogLookupItemDto[pageLength];
        for (var index = 0; index < pageLength; index++)
        {
            pageItems[index] = Items[start + index];
        }

        var hasMore = remaining > take;
        var nextCursor = hasMore && pageItems.Length > 0
            ? pageItems[^1].LookupCodeNormalized
            : null;

        return new CatalogSyncPageResponse(
            StoreCode,
            GeneratedAt,
            string.IsNullOrEmpty(normalizedCursor) ? null : normalizedCursor,
            pageItems,
            [],
            nextCursor,
            hasMore,
            Items.Count,
            CatalogVersion,
            CreatePageChecksum(pageItems, checksumVersion));
    }

    /// <summary>
    /// 两个不可变版本均按 lookup key 排序。此处只做有序归并，避免把客户端全量行版本上传后再比较；
    /// 每个 key 最多生成一个操作，cursor 因而能稳定覆盖 upsert 与 delete 的混合序列。
    /// </summary>
    public CatalogDeltaPageResponse GetDeltaPage(
        CatalogSellableIndex baseline,
        string? cursor,
        int pageSize)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        if (!string.Equals(StoreCode, baseline.StoreCode, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Catalog versions must belong to the same store.", nameof(baseline));
        }

        var normalizedCursor = NormalizeLookupCode(cursor);
        var take = Math.Clamp(pageSize, 1, MaxPageSize);
        var operations = new List<CatalogDeltaOperation>(take + 1);
        var baselinePosition = 0;
        var targetPosition = 0;

        while (baselinePosition < baseline.Items.Count || targetPosition < Items.Count)
        {
            CatalogDeltaOperation? operation;
            if (baselinePosition >= baseline.Items.Count)
            {
                var targetItem = Items[targetPosition++];
                operation = CatalogDeltaOperation.Upsert(targetItem);
            }
            else if (targetPosition >= Items.Count)
            {
                var baselineItem = baseline.Items[baselinePosition++];
                operation = CatalogDeltaOperation.Delete(new DeletedLookupDto(
                    StoreCode,
                    baselineItem.LookupCode,
                    baselineItem.LookupCodeNormalized,
                    GeneratedAt));
            }
            else
            {
                var baselineItem = baseline.Items[baselinePosition];
                var targetItem = Items[targetPosition];
                var comparison = string.Compare(
                    baselineItem.LookupCodeNormalized,
                    targetItem.LookupCodeNormalized,
                    StringComparison.Ordinal);
                if (comparison < 0)
                {
                    baselinePosition++;
                    operation = CatalogDeltaOperation.Delete(new DeletedLookupDto(
                        StoreCode,
                        baselineItem.LookupCode,
                        baselineItem.LookupCodeNormalized,
                        GeneratedAt));
                }
                else if (comparison > 0)
                {
                    targetPosition++;
                    operation = CatalogDeltaOperation.Upsert(targetItem);
                }
                else
                {
                    baselinePosition++;
                    targetPosition++;
                    operation = string.Equals(
                        baselineItem.RowVersion,
                        targetItem.RowVersion,
                        StringComparison.OrdinalIgnoreCase)
                        ? null
                        : CatalogDeltaOperation.Upsert(targetItem);
                }
            }

            if (operation is null ||
                (!string.IsNullOrEmpty(normalizedCursor) &&
                 string.Compare(operation.LookupCodeNormalized, normalizedCursor, StringComparison.Ordinal) <= 0))
            {
                continue;
            }

            operations.Add(operation);
            if (operations.Count > take)
            {
                break;
            }
        }

        var pageOperations = operations.Take(take).ToArray();
        var hasMore = operations.Count > take;
        var nextCursor = hasMore && pageOperations.Length > 0
            ? pageOperations[^1].LookupCodeNormalized
            : null;
        var upserts = pageOperations
            .Where(operation => operation.Item is not null)
            .Select(operation => operation.Item!)
            .ToArray();
        var deletes = pageOperations
            .Where(operation => operation.DeletedLookup is not null)
            .Select(operation => operation.DeletedLookup!)
            .ToArray();

        return new CatalogDeltaPageResponse(
            StoreCode,
            GeneratedAt,
            baseline.CatalogVersion,
            CatalogVersion,
            string.IsNullOrEmpty(normalizedCursor) ? null : normalizedCursor,
            upserts,
            deletes,
            nextCursor,
            hasMore,
            Items.Count,
            CreateDeltaPageChecksum(
                baseline.CatalogVersion,
                CatalogVersion,
                pageOperations));
    }

    public IReadOnlyList<global::Hbpos.Api.Services.CatalogDeltaOperation> GetDeltaOperations(
        CatalogSellableIndex baseline)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        var operations = new List<global::Hbpos.Api.Services.CatalogDeltaOperation>();
        var baselinePosition = 0;
        var targetPosition = 0;
        while (baselinePosition < baseline.Items.Count || targetPosition < Items.Count)
        {
            if (baselinePosition >= baseline.Items.Count)
            {
                operations.Add(global::Hbpos.Api.Services.CatalogDeltaOperation.Upsert(Items[targetPosition++]));
                continue;
            }

            if (targetPosition >= Items.Count)
            {
                var item = baseline.Items[baselinePosition++];
                operations.Add(global::Hbpos.Api.Services.CatalogDeltaOperation.Delete(
                    new DeletedLookupDto(StoreCode, item.LookupCode, item.LookupCodeNormalized, GeneratedAt)));
                continue;
            }

            var left = baseline.Items[baselinePosition];
            var right = Items[targetPosition];
            var comparison = string.Compare(left.LookupCodeNormalized, right.LookupCodeNormalized, StringComparison.Ordinal);
            if (comparison < 0)
            {
                baselinePosition++;
                operations.Add(global::Hbpos.Api.Services.CatalogDeltaOperation.Delete(
                    new DeletedLookupDto(StoreCode, left.LookupCode, left.LookupCodeNormalized, GeneratedAt)));
            }
            else if (comparison > 0)
            {
                targetPosition++;
                operations.Add(global::Hbpos.Api.Services.CatalogDeltaOperation.Upsert(right));
            }
            else
            {
                baselinePosition++;
                targetPosition++;
                if (!string.Equals(left.RowVersion, right.RowVersion, StringComparison.OrdinalIgnoreCase))
                {
                    operations.Add(global::Hbpos.Api.Services.CatalogDeltaOperation.Upsert(right));
                }
            }
        }

        return operations;
    }

    /// <summary>
    /// 下载租约已在 plan 阶段固定完整差异；续页只在该不可变数组上二分切片，
    /// 不再从两个目录版本重新归并。
    /// </summary>
    public CatalogDeltaPageResponse GetDeltaPageFromOperations(
        CatalogSellableIndex baseline,
        IReadOnlyList<global::Hbpos.Api.Services.CatalogDeltaOperation> operations,
        string? cursor,
        int pageSize)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(operations);
        var normalizedCursor = NormalizeLookupCode(cursor);
        var take = Math.Clamp(pageSize, 1, MaxPageSize);
        var low = 0;
        var high = operations.Count;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (string.Compare(operations[middle].LookupCodeNormalized, normalizedCursor, StringComparison.Ordinal) <= 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        // 下载租约的差异数组已固定且有序；二分后的页仅复制当前窗口。
        var remaining = operations.Count - low;
        var pageLength = Math.Min(take, Math.Max(remaining, 0));
        var pageOperations = new global::Hbpos.Api.Services.CatalogDeltaOperation[pageLength];
        for (var index = 0; index < pageLength; index++)
        {
            pageOperations[index] = operations[low + index];
        }

        var hasMore = remaining > take;
        var nextCursor = hasMore && pageOperations.Length > 0
            ? pageOperations[^1].LookupCodeNormalized
            : null;
        var upserts = pageOperations.Where(operation => operation.Item is not null)
            .Select(operation => operation.Item!).ToArray();
        var deletes = pageOperations.Where(operation => operation.DeletedLookup is not null)
            .Select(operation => operation.DeletedLookup!).ToArray();
        var checksumOperations = pageOperations.Select(operation => operation.Item is { } item
            ? CatalogDeltaOperation.Upsert(item)
            : CatalogDeltaOperation.Delete(operation.DeletedLookup!)).ToArray();

        return new CatalogDeltaPageResponse(
            StoreCode,
            GeneratedAt,
            baseline.CatalogVersion,
            CatalogVersion,
            string.IsNullOrEmpty(normalizedCursor) ? null : normalizedCursor,
            upserts,
            deletes,
            nextCursor,
            hasMore,
            Items.Count,
            CreateDeltaPageChecksum(baseline.CatalogVersion, CatalogVersion, checksumOperations));
    }

    public CatalogCompareResponse Compare(CatalogCompareRequest request)
    {
        var localByLookup = new Dictionary<string, CatalogLocalLookupVersionDto>(StringComparer.Ordinal);

        foreach (var local in request.LocalLookups ?? [])
        {
            var normalizedLookup = NormalizeLookupCode(
                HasText(local.LookupCodeNormalized) ? local.LookupCodeNormalized : local.LookupCode);
            if (string.IsNullOrEmpty(normalizedLookup))
            {
                continue;
            }

            localByLookup.TryAdd(normalizedLookup, local);
        }

        var upserts = new List<CatalogLookupItemDto>();
        var deletes = new List<DeletedLookupDto>();

        foreach (var (normalizedLookup, local) in localByLookup)
        {
            if (!_itemsByNormalizedLookup.TryGetValue(normalizedLookup, out var current))
            {
                deletes.Add(new DeletedLookupDto(
                    StoreCode,
                    GetDeleteLookupCode(local, normalizedLookup),
                    normalizedLookup,
                    GeneratedAt));
                continue;
            }

            if (!HasMatchingVersion(local, current))
            {
                upserts.Add(current);
            }
        }

        return new CatalogCompareResponse(
            StoreCode,
            GeneratedAt,
            upserts,
            deletes,
            NextCursor: null,
            HasMore: false);
    }

    public CatalogLookupResponse Lookup(string? lookupCode, string? lookupCodeNormalized)
    {
        var normalizedLookup = NormalizeLookupCode(
            HasText(lookupCodeNormalized) ? lookupCodeNormalized : lookupCode);
        _itemsByNormalizedLookup.TryGetValue(normalizedLookup, out var item);

        return new CatalogLookupResponse(
            StoreCode,
            GetRequestedLookupCode(lookupCode, lookupCodeNormalized, normalizedLookup),
            normalizedLookup,
            item is not null,
            item);
    }

    public CatalogSpecialProductsPageResponse GetSpecialProductsPage(string? cursor, int pageSize)
    {
        var normalizedCursor = NormalizeLookupCode(cursor);
        var take = Math.Clamp(pageSize, 1, MaxPageSize);
        // 一个商品可能对应多个 lookup_code（一商品多码），Items 按 lookup_code
        // 去重会导致特殊商品列表出现重复 productCode；这里按商品去重并保留
        // 更新时间最新、价格来源最高的条目，避免客户端下载校验误判非法页。
        var specialItems = Items
            .Where(x => x.IsSpecialProduct)
            .GroupBy(x => x.ProductCode, StringComparer.Ordinal)
            .Select(x => x
                .OrderByDescending(item => item.UpdatedAt ?? DateTimeOffset.MinValue)
                .ThenByDescending(item => item.PriceSource)
                .First())
            .OrderBy(x => x.LookupCodeNormalized, StringComparer.Ordinal)
            .ToArray();
        var pageCandidates = specialItems
            .Where(x => string.IsNullOrEmpty(normalizedCursor)
                || string.Compare(x.LookupCodeNormalized, normalizedCursor, StringComparison.Ordinal) > 0)
            .Take(take + 1)
            .ToArray();

        var pageItems = pageCandidates.Take(take).ToArray();
        var hasMore = pageCandidates.Length > take;
        var nextCursor = hasMore && pageItems.Length > 0
            ? pageItems[^1].LookupCodeNormalized
            : null;

        return new CatalogSpecialProductsPageResponse(
            StoreCode,
            GeneratedAt,
            string.IsNullOrEmpty(normalizedCursor) ? null : normalizedCursor,
            pageItems,
            nextCursor,
            hasMore,
            specialItems.Length);
    }

    public static string NormalizeLookupCode(string? value)
    {
        return (value ?? string.Empty).Trim().ToUpperInvariant();
    }

    private int FindFirstAfter(string normalizedCursor)
    {
        if (string.IsNullOrEmpty(normalizedCursor))
        {
            return 0;
        }

        var low = 0;
        var high = Items.Count;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (string.Compare(Items[middle].LookupCodeNormalized, normalizedCursor, StringComparison.Ordinal) <= 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    private int StandardPageCount => Math.Max(1, (Items.Count + StandardPageSize - 1) / StandardPageSize);

    /// <summary>
    /// 标准 v2 页命中判定：请求游标必须恰好是标准页边界（首页为空、后续页等于上一页末项商品码），
    /// 且 pageSize 必须为定长 5000。非标准 pageSize 或任意游标一律回退原二分分页路径，
    /// 保证 v2 响应内容与语义不变，仅共享可复用的 checksum 计算。
    /// </summary>
    private bool TryGetStandardV2Page(string normalizedCursor, out CatalogSyncPageResponse page)
    {
        var pageNumber = 0;
        if (!string.IsNullOrEmpty(normalizedCursor))
        {
            var startIndex = FindFirstAfter(normalizedCursor);
            // 中文注释：标准页边界 = 上一页末项商品码；FindFirstAfter 落在 5000 的整数倍上
            // 且该位置前一商品码与游标一致，才视为标准页续页。
            if (startIndex % StandardPageSize != 0 ||
                startIndex == 0 ||
                startIndex >= Items.Count ||
                !string.Equals(
                    Items[startIndex - 1].LookupCodeNormalized,
                    normalizedCursor,
                    StringComparison.Ordinal))
            {
                page = null!;
                return false;
            }

            pageNumber = startIndex / StandardPageSize;
        }

        if (pageNumber >= StandardPageCount)
        {
            page = null!;
            return false;
        }

        var start = pageNumber * StandardPageSize;
        var pageLength = Math.Min(StandardPageSize, Math.Max(Items.Count - start, 0));
        var pageItems = new CatalogLookupItemDto[pageLength];
        for (var index = 0; index < pageLength; index++)
        {
            pageItems[index] = Items[start + index];
        }

        // 中文注释：single-flight——并发请求同一页时只有一次计算，其余等待同一 Lazy；
        // 计算失败时移除毒化条目，避免后续请求持续命中同一异常。
        var entry = GetOrComputeStandardV2Page(pageNumber, pageItems);

        page = new CatalogSyncPageResponse(
            StoreCode,
            GeneratedAt,
            pageNumber == 0 ? null : Items[start - 1].LookupCodeNormalized,
            pageItems,
            [],
            entry.NextCursor,
            entry.HasMore,
            Items.Count,
            CatalogVersion,
            entry.PageChecksum);
        return true;
    }

    private StandardV2PageEntry ComputeStandardV2Page(
        int pageNumber,
        IReadOnlyList<CatalogLookupItemDto> pageItems)
    {
        var start = pageNumber * StandardPageSize;
        var hasMore = Items.Count - start > StandardPageSize;
        var nextCursor = hasMore && pageItems.Count > 0
            ? pageItems[^1].LookupCodeNormalized
            : null;
        return new StandardV2PageEntry(
            CreatePageChecksumV2Streaming(pageItems),
            nextCursor,
            hasMore);
    }

    /// <summary>
    /// 发布后预热入口：强制求值标准页缓存条目，真正完成 checksum 计算；
    /// 不触碰活跃下载，v1 活跃时由调用方暂停。
    /// </summary>
    internal void WarmUpStandardV2Page(int pageNumber)
    {
        if (pageNumber < 0 || pageNumber >= StandardPageCount)
        {
            return;
        }

        var start = pageNumber * StandardPageSize;
        var pageLength = Math.Min(StandardPageSize, Math.Max(Items.Count - start, 0));
        var pageItems = new CatalogLookupItemDto[pageLength];
        for (var index = 0; index < pageLength; index++)
        {
            pageItems[index] = Items[start + index];
        }

        // 中文注释：访问 .Value 强制同步完成流式 checksum，否则预热只是预登记 Lazy 而计算仍推迟。
        _ = GetOrComputeStandardV2Page(pageNumber, pageItems);
    }

    /// <summary>
    /// 取标准页缓存条目并强制求值；计算失败时移除毒化条目后重抛，
    /// 保证下一次请求可重新计算而非持续命中同一异常。
    /// </summary>
    private StandardV2PageEntry GetOrComputeStandardV2Page(
        int pageNumber,
        IReadOnlyList<CatalogLookupItemDto> pageItems)
    {
        var lazy = _standardV2PageCache.GetOrAdd(
            pageNumber,
            _ => new Lazy<StandardV2PageEntry>(
                () => ComputeStandardV2Page(pageNumber, pageItems),
                LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return lazy.Value;
        }
        catch
        {
            _standardV2PageCache.TryRemove(
                new KeyValuePair<int, Lazy<StandardV2PageEntry>>(pageNumber, lazy));
            throw;
        }
    }

    internal static CatalogLookupItemDto ToLookupItem(SellableItemDto item)
    {
        var storeCode = NormalizeStoreCode(item.StoreCode);
        var lookupCode = (item.LookupCode ?? string.Empty).Trim();
        var lookupCodeNormalized = NormalizeLookupCode(lookupCode);

        return new CatalogLookupItemDto(
            storeCode,
            item.ProductCode.Trim(),
            item.ReferenceCode?.Trim(),
            item.DisplayName.Trim(),
            lookupCode,
            lookupCodeNormalized,
            item.ItemNumber?.Trim(),
            item.Barcode?.Trim(),
            item.RetailPrice,
            item.PriceSource,
            item.PriceSourceLabel.Trim(),
            item.QuantityFactor,
            item.UpdatedAt,
            CreateRowVersion(
                storeCode,
                item.ProductCode.Trim(),
                item.ReferenceCode?.Trim() ?? string.Empty,
                item.DisplayName.Trim(),
                lookupCodeNormalized,
                item.ItemNumber?.Trim() ?? string.Empty,
                item.Barcode?.Trim() ?? string.Empty,
                item.RetailPrice,
                item.PriceSource,
                item.PriceSourceLabel.Trim(),
                item.QuantityFactor,
                item.ProductImage ?? string.Empty,
                item.DiscountRate,
                item.IsSpecialProduct),
            item.ProductImage,
            item.DiscountRate,
            item.IsSpecialProduct);
    }

    private static string CreateRowVersion(
        string storeCode,
        string productCode,
        string referenceCode,
        string displayName,
        string lookupCodeNormalized,
        string itemNumber,
        string barcode,
        decimal retailPrice,
        PriceSourceKind priceSource,
        string priceSourceLabel,
        decimal quantityFactor,
        string productImage,
        decimal? discountRate,
        bool isSpecialProduct)
    {
        var builder = new StringBuilder();
        AppendCanonical(builder, storeCode);
        AppendCanonical(builder, productCode);
        AppendCanonical(builder, referenceCode);
        AppendCanonical(builder, displayName);
        AppendCanonical(builder, lookupCodeNormalized);
        AppendCanonical(builder, itemNumber);
        AppendCanonical(builder, barcode);
        AppendCanonical(builder, retailPrice.ToString("0.#############################", CultureInfo.InvariantCulture));
        AppendCanonical(builder, ((int)priceSource).ToString(CultureInfo.InvariantCulture));
        AppendCanonical(builder, priceSourceLabel);
        AppendCanonical(builder, quantityFactor.ToString("0.#############################", CultureInfo.InvariantCulture));
        AppendCanonical(builder, productImage);
        AppendCanonical(builder, FormatNullableDecimal(discountRate));
        AppendCanonical(builder, isSpecialProduct ? "1" : "0");

        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(hashBytes);
    }

    /// <summary>
    /// 校验覆盖分页响应中每个 lookup 行的稳定业务字段；字段顺序和长度前缀是跨平台协议的一部分。
    /// RowVersion 是这些字段的派生值，因此不重复纳入。
    /// v1 保持既有 StringBuilder 实现（WPF/旧客户端共用，必须字节级不变）；
    /// v2 走流式 IncrementalHash，避免为整页商品构建巨型 canonical 字符串。
    /// </summary>
    private static string CreatePageChecksum(
        IReadOnlyList<CatalogLookupItemDto> items,
        int checksumVersion)
    {
        if (checksumVersion == 2)
        {
            return CreatePageChecksumV2Streaming(items);
        }

        var builder = new StringBuilder();
        AppendCanonical(
            builder,
            PageChecksumV1AlgorithmMarker);
        AppendCanonical(
            builder,
            items.Count.ToString(CultureInfo.InvariantCulture));

        foreach (var item in items)
        {
            AppendCanonical(builder, item.StoreCode);
            AppendCanonical(builder, item.ProductCode);
            AppendCanonical(builder, item.ReferenceCode ?? string.Empty);
            AppendCanonical(builder, item.DisplayName);
            AppendCanonical(builder, item.LookupCode);
            AppendCanonical(builder, item.LookupCodeNormalized);
            AppendCanonical(builder, item.ItemNumber ?? string.Empty);
            AppendCanonical(builder, item.Barcode ?? string.Empty);
            AppendCanonical(
                builder,
                FormatCatalogNumber(item.RetailPrice));
            AppendCanonical(
                builder,
                ((int)item.PriceSource).ToString(CultureInfo.InvariantCulture));
            AppendCanonical(builder, item.PriceSourceLabel);
            AppendCanonical(
                builder,
                FormatCatalogNumber(item.QuantityFactor));
            AppendCanonical(builder, FormatCatalogTimestamp(item.UpdatedAt));
            AppendCanonical(builder, item.ProductImage ?? string.Empty);
            AppendCanonical(
                builder,
                FormatNullableCatalogNumber(item.DiscountRate));
            AppendCanonical(builder, item.IsSpecialProduct ? "1" : "0");
        }

        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return string.Concat(
            PageChecksumV1Prefix,
            Convert.ToHexString(hashBytes).ToLowerInvariant());
    }

    /// <summary>
    /// v2 页摘要流式构建：逐字段把 UTF-16 长度帧与 UTF-8 内容直接送入 IncrementalHash，
    /// 输出与既有 v2 格式逐字节一致（{十进制UTF-16长度}:{内容}| 首尾相接），
    /// 但不再分配整页 canonical 字符串，显著降低 34 万商品分页下载的 GC/CPU 压力。
    /// </summary>
    private static string CreatePageChecksumV2Streaming(
        IReadOnlyList<CatalogLookupItemDto> items)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendCanonical(hash, PageChecksumV2AlgorithmMarker);
        AppendCanonical(hash, FormatBinary64(items.Count));

        foreach (var item in items)
        {
            AppendCanonical(hash, item.StoreCode);
            AppendCanonical(hash, item.ProductCode);
            AppendCanonical(hash, item.ReferenceCode ?? string.Empty);
            AppendCanonical(hash, item.DisplayName);
            AppendCanonical(hash, item.LookupCode);
            AppendCanonical(hash, item.LookupCodeNormalized);
            AppendCanonical(hash, item.ItemNumber ?? string.Empty);
            AppendCanonical(hash, item.Barcode ?? string.Empty);
            AppendCanonical(hash, FormatBinary64(item.RetailPrice));
            AppendCanonical(hash, FormatBinary64((int)item.PriceSource));
            AppendCanonical(hash, item.PriceSourceLabel);
            AppendCanonical(hash, FormatBinary64(item.QuantityFactor));
            AppendCanonical(hash, FormatCatalogTimestamp(item.UpdatedAt));
            AppendCanonical(hash, item.ProductImage ?? string.Empty);
            AppendCanonical(hash, FormatNullableBinary64(item.DiscountRate));
            AppendCanonical(hash, item.IsSpecialProduct ? "1" : "0");
        }

        return string.Concat(
            PageChecksumV2Prefix,
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    /// <summary>
    /// 增量页校验与全量页分域，避免客户端把 delete 漏掉仍通过校验。字段使用 v1 的
    /// JavaScript 可观察数值格式；操作按归并后的 lookup key 顺序编码。
    /// </summary>
    private static string CreateDeltaPageChecksum(
        string baseCatalogVersion,
        string targetCatalogVersion,
        IReadOnlyList<CatalogDeltaOperation> operations)
    {
        var builder = new StringBuilder();
        AppendCanonical(builder, DeltaPageChecksumV1AlgorithmMarker);
        AppendCanonical(builder, baseCatalogVersion);
        AppendCanonical(builder, targetCatalogVersion);
        AppendCanonical(builder, operations.Count.ToString(CultureInfo.InvariantCulture));

        foreach (var operation in operations)
        {
            if (operation.Item is { } item)
            {
                AppendCanonical(builder, "U");
                AppendCanonical(builder, item.StoreCode);
                AppendCanonical(builder, item.ProductCode);
                AppendCanonical(builder, item.ReferenceCode ?? string.Empty);
                AppendCanonical(builder, item.DisplayName);
                AppendCanonical(builder, item.LookupCode);
                AppendCanonical(builder, item.LookupCodeNormalized);
                AppendCanonical(builder, item.ItemNumber ?? string.Empty);
                AppendCanonical(builder, item.Barcode ?? string.Empty);
                AppendCanonical(builder, FormatCatalogNumber(item.RetailPrice));
                AppendCanonical(builder, ((int)item.PriceSource).ToString(CultureInfo.InvariantCulture));
                AppendCanonical(builder, item.PriceSourceLabel);
                AppendCanonical(builder, FormatCatalogNumber(item.QuantityFactor));
                AppendCanonical(builder, FormatCatalogTimestamp(item.UpdatedAt));
                AppendCanonical(builder, item.ProductImage ?? string.Empty);
                AppendCanonical(builder, FormatNullableCatalogNumber(item.DiscountRate));
                AppendCanonical(builder, item.IsSpecialProduct ? "1" : "0");
                continue;
            }

            var deletedLookup = operation.DeletedLookup!;
            AppendCanonical(builder, "D");
            AppendCanonical(builder, deletedLookup.StoreCode);
            AppendCanonical(builder, deletedLookup.LookupCode);
            AppendCanonical(builder, deletedLookup.LookupCodeNormalized);
            AppendCanonical(builder, FormatCatalogTimestamp(deletedLookup.DeletedAt));
        }

        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return string.Concat(
            DeltaPageChecksumV1Prefix,
            Convert.ToHexString(hashBytes).ToLowerInvariant());
    }

    private static bool HasMatchingVersion(CatalogLocalLookupVersionDto local, CatalogLookupItemDto current)
    {
        var rowVersion = local.RowVersion?.Trim();
        if (!string.IsNullOrEmpty(rowVersion))
        {
            return string.Equals(rowVersion, current.RowVersion, StringComparison.OrdinalIgnoreCase);
        }

        return local.UpdatedAt.HasValue
            && current.UpdatedAt.HasValue
            && local.UpdatedAt.Value.ToUniversalTime() == current.UpdatedAt.Value.ToUniversalTime();
    }

    private static string GetDeleteLookupCode(CatalogLocalLookupVersionDto local, string normalizedLookup)
    {
        var lookupCode = local.LookupCode?.Trim();
        return string.IsNullOrEmpty(lookupCode) ? normalizedLookup : lookupCode;
    }

    private static string GetRequestedLookupCode(
        string? lookupCode,
        string? lookupCodeNormalized,
        string normalizedLookup)
    {
        var requestedLookupCode = lookupCode?.Trim();
        if (!string.IsNullOrEmpty(requestedLookupCode))
        {
            return requestedLookupCode;
        }

        var requestedLookupCodeNormalized = lookupCodeNormalized?.Trim();
        return !string.IsNullOrEmpty(requestedLookupCodeNormalized)
            ? requestedLookupCodeNormalized
            : normalizedLookup;
    }

    private sealed record CatalogDeltaOperation(
        string LookupCodeNormalized,
        CatalogLookupItemDto? Item,
        DeletedLookupDto? DeletedLookup)
    {
        public static CatalogDeltaOperation Upsert(CatalogLookupItemDto item) =>
            new(item.LookupCodeNormalized, item, null);

        public static CatalogDeltaOperation Delete(DeletedLookupDto deletedLookup) =>
            new(deletedLookup.LookupCodeNormalized, null, deletedLookup);
    }

    private static void AppendCanonical(StringBuilder builder, string value)
    {
        builder
            .Append(value.Length.ToString(CultureInfo.InvariantCulture))
            .Append(':')
            .Append(value)
            .Append('|');
    }

    /// <summary>
    /// 流式长度帧：写入 {十进制UTF-16长度}:{UTF-8内容}|，与 StringBuilder 版输出逐字节一致。
    /// 长度前缀为 ASCII 数字，其 UTF-8 编码即原样字节。
    /// </summary>
    private static void AppendCanonical(IncrementalHash hash, string value)
    {
        var length = value.Length.ToString(CultureInfo.InvariantCulture);
        hash.AppendData(Encoding.UTF8.GetBytes(length));
        hash.AppendData(":"u8);
        hash.AppendData(Encoding.UTF8.GetBytes(value));
        hash.AppendData("|"u8);
    }

    private static string FormatNullableDecimal(decimal? value)
    {
        return value?.ToString("0.#############################", CultureInfo.InvariantCulture) ?? string.Empty;
    }

    /// <summary>
    /// OpenAPI 把 decimal 生成为 TypeScript number；摘要必须以客户端实际能观察到的
    /// IEEE-754 值为准，避免 JSON 解析舍入后误判合法页面。
    /// </summary>
    private static string FormatCatalogNumber(decimal value)
    {
        // 先按 JSON 会输出的十进制文本解析为 double；直接 decimal -> double 在部分
        // 29 位边界值上与 JavaScript JSON.parse 的舍入结果不同。
        var serializedDecimal = value.ToString(
            "0.#############################",
            CultureInfo.InvariantCulture);
        var javascriptNumber = double.Parse(
            serializedDecimal,
            NumberStyles.Float,
            CultureInfo.InvariantCulture);
        var text = javascriptNumber.ToString("R", CultureInfo.InvariantCulture);
        if (text is "-0")
        {
            return "0";
        }

        var exponentSeparator = text.IndexOfAny(['e', 'E']);
        if (exponentSeparator < 0)
        {
            return text;
        }

        var mantissa = text[..exponentSeparator];
        var exponent = int.Parse(text[(exponentSeparator + 1)..], CultureInfo.InvariantCulture);
        var isNegative = mantissa.StartsWith("-", StringComparison.Ordinal);
        var unsignedMantissa = isNegative ? mantissa[1..] : mantissa;
        var decimalSeparator = unsignedMantissa.IndexOf('.');
        var whole = decimalSeparator < 0 ? unsignedMantissa : unsignedMantissa[..decimalSeparator];
        var fraction = decimalSeparator < 0 ? string.Empty : unsignedMantissa[(decimalSeparator + 1)..];
        var digits = string.Concat(whole, fraction);
        var decimalIndex = whole.Length + exponent;
        string expanded;

        if (decimalIndex <= 0)
        {
            expanded = string.Concat("0.", new string('0', -decimalIndex), digits);
        }
        else if (decimalIndex >= digits.Length)
        {
            expanded = string.Concat(digits, new string('0', decimalIndex - digits.Length));
        }
        else
        {
            expanded = string.Concat(digits[..decimalIndex], ".", digits[decimalIndex..]);
        }

        return isNegative ? string.Concat("-", expanded) : expanded;
    }

    private static string FormatNullableCatalogNumber(decimal? value)
    {
        return value.HasValue ? FormatCatalogNumber(value.Value) : string.Empty;
    }

    private static string FormatBinary64(decimal value)
    {
        // decimal 先走 JSON 会产生的十进制文本，再转换为客户端实际观察到的 double。
        var serializedDecimal = value.ToString(
            "0.#############################",
            CultureInfo.InvariantCulture);
        return FormatBinary64(double.Parse(
            serializedDecimal,
            NumberStyles.Float,
            CultureInfo.InvariantCulture));
    }

    private static string FormatBinary64(int value)
    {
        return FormatBinary64((double)value);
    }

    private static string FormatBinary64(double value)
    {
        // 中文注释：与 v1 路径一致，负零归一为正零后再编码；decimal/int 输入不会产生 -0，
        // 此处只为消除跨端摘要的隐性不变量差异。
        if (value == 0d)
        {
            value = 0d;
        }

        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(
            bytes,
            BitConverter.DoubleToInt64Bits(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string FormatNullableBinary64(decimal? value)
    {
        return value.HasValue ? FormatBinary64(value.Value) : string.Empty;
    }

    private static string FormatCatalogTimestamp(DateTimeOffset? value)
    {
        return value?.ToUniversalTime().ToString(
            "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
            CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static string NormalizeStoreCode(string? value)
    {
        return (value ?? string.Empty).Trim();
    }

    private static bool HasText(string? value)
    {
        return !string.IsNullOrWhiteSpace(value);
    }
}
