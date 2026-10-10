using System.Diagnostics;
using Hbpos.Contracts.Catalog;

namespace Hbpos.Client.Wpf.Services;

public interface ISpecialProductService
{
    Task<SpecialProductMarkResult> MarkSpecialProductAsync(
        string storeCode,
        string productCode,
        bool isSpecialProduct,
        CancellationToken cancellationToken = default);

    Task<SpecialProductDownloadResult> DownloadSpecialProductsAsync(
        string storeCode,
        CancellationToken cancellationToken = default,
        IProgress<SpecialProductDownloadProgress>? progress = null);

    // 中文注释：修改特殊商品的门店零售价，必须在线；默认实现按"不支持"处理，既有测试替身无需实现。
    Task<SpecialProductMarkResult> UpdateSpecialProductPriceAsync(
        string storeCode,
        string productCode,
        decimal retailPrice,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("Special product price update is not supported by this service.");
    }
}

public sealed record SpecialProductDownloadResult(
    string StoreCode,
    int PageCount,
    int TotalCount,
    int DownloadedCount,
    int UpsertedCount,
    int UnmarkedCount);

public sealed record SpecialProductMarkResult(
    IReadOnlyList<SellableItemDto> ChangedItems,
    IReadOnlyList<SellableItemDto> SpecialItems);

public enum SpecialProductDownloadProgressStage
{
    Preparing,
    Downloading,
    Completed,
    Failed
}

public sealed record SpecialProductDownloadProgress(
    string StoreCode,
    SpecialProductDownloadProgressStage Stage,
    int TotalCount,
    int DownloadedCount,
    int Percent,
    int PageCount,
    int UpsertedCount,
    int UnmarkedCount,
    long ElapsedMilliseconds,
    string? ErrorMessage = null);

public sealed class SpecialProductService(
    ILocalCatalogRepository localCatalogRepository,
    ICatalogApiClient catalogApiClient) : ISpecialProductService
{
    private const int PageSize = 5000;

    public async Task<SpecialProductMarkResult> MarkSpecialProductAsync(
        string storeCode,
        string productCode,
        bool isSpecialProduct,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(productCode);

        var totalStopwatch = Stopwatch.StartNew();
        Log($"mark start store={storeCode} productCode={productCode} isSpecialProduct={isSpecialProduct}");
        try
        {
            var apiStopwatch = Stopwatch.StartNew();
            var response = await catalogApiClient.MarkSpecialProductAsync(
                new CatalogSpecialProductMarkRequest(storeCode, productCode, isSpecialProduct),
                cancellationToken);
            apiStopwatch.Stop();
            Log($"mark api response store={response.StoreCode} productCode={response.ProductCode} isSpecialProduct={response.IsSpecialProduct} items={response.Items.Count} apiElapsedMs={apiStopwatch.ElapsedMilliseconds}");

            var upsertItems = response.Items
                .Select(item => item.ToSellableItemDto())
                .ToArray();
            var upsertElapsedMs = 0L;
            if (upsertItems.Length > 0)
            {
                var upsertStopwatch = Stopwatch.StartNew();
                await localCatalogRepository.UpsertSellableItemsAsync(upsertItems, cancellationToken);
                upsertStopwatch.Stop();
                upsertElapsedMs = upsertStopwatch.ElapsedMilliseconds;
            }

            var flagStopwatch = Stopwatch.StartNew();
            await localCatalogRepository.UpdateSpecialProductFlagAsync(
                response.StoreCode,
                response.ProductCode,
                response.IsSpecialProduct,
                cancellationToken);
            flagStopwatch.Stop();

            var loadStopwatch = Stopwatch.StartNew();
            var specialItems = await localCatalogRepository.LoadSpecialProductItemsAsync(response.StoreCode, cancellationToken);
            loadStopwatch.Stop();
            totalStopwatch.Stop();
            Log($"mark completed store={response.StoreCode} productCode={response.ProductCode} isSpecialProduct={response.IsSpecialProduct} items={specialItems.Count} upserted={upsertItems.Length} apiElapsedMs={apiStopwatch.ElapsedMilliseconds} upsertElapsedMs={upsertElapsedMs} flagElapsedMs={flagStopwatch.ElapsedMilliseconds} loadElapsedMs={loadStopwatch.ElapsedMilliseconds} totalElapsedMs={totalStopwatch.ElapsedMilliseconds}");
            return new SpecialProductMarkResult(upsertItems, specialItems);
        }
        catch (Exception ex)
        {
            totalStopwatch.Stop();
            Log($"mark failed store={storeCode} productCode={productCode} isSpecialProduct={isSpecialProduct} totalElapsedMs={totalStopwatch.ElapsedMilliseconds} error={ex.Message}");
            throw;
        }
    }

    public async Task<SpecialProductMarkResult> UpdateSpecialProductPriceAsync(
        string storeCode,
        string productCode,
        decimal retailPrice,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(productCode);

        var totalStopwatch = Stopwatch.StartNew();
        Log($"price start store={storeCode} productCode={productCode} price={retailPrice}");
        try
        {
            // 关键逻辑：价格只以服务端为准，必须先在线写入成功，才允许覆盖本地目录；失败时本地价格保持不变。
            var response = await catalogApiClient.UpdateSpecialProductPriceAsync(
                new CatalogSpecialProductPriceRequest(storeCode, productCode, retailPrice),
                cancellationToken);

            var upsertItems = response.Items
                .Select(item => item.ToSellableItemDto())
                .ToArray();
            if (upsertItems.Length > 0)
            {
                await localCatalogRepository.UpsertSellableItemsAsync(upsertItems, cancellationToken);
            }

            var specialItems = await localCatalogRepository.LoadSpecialProductItemsAsync(response.StoreCode, cancellationToken);
            totalStopwatch.Stop();
            Log($"price completed store={response.StoreCode} productCode={response.ProductCode} price={response.RetailPrice} items={specialItems.Count} upserted={upsertItems.Length} totalElapsedMs={totalStopwatch.ElapsedMilliseconds}");
            return new SpecialProductMarkResult(upsertItems, specialItems);
        }
        catch (Exception ex)
        {
            totalStopwatch.Stop();
            Log($"price failed store={storeCode} productCode={productCode} price={retailPrice} totalElapsedMs={totalStopwatch.ElapsedMilliseconds} error={ex.Message}");
            throw;
        }
    }

    public async Task<SpecialProductDownloadResult> DownloadSpecialProductsAsync(
        string storeCode,
        CancellationToken cancellationToken = default,
        IProgress<SpecialProductDownloadProgress>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeCode);

        var totalStopwatch = Stopwatch.StartNew();
        var pageCount = 0;
        var totalCount = 0;
        var downloadedCount = 0;
        var upsertedCount = 0;
        var unmarkedCount = 0;
        string? cursor = null;
        var specialProductCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            Log($"download start store={storeCode} pageSize={PageSize}");
            ReportProgress(
                progress,
                storeCode,
                SpecialProductDownloadProgressStage.Preparing,
                totalCount,
                downloadedCount,
                pageCount,
                upsertedCount,
                unmarkedCount,
                totalStopwatch);

            while (true)
            {
                Log($"download page request store={storeCode} page={pageCount + 1} cursor={cursor ?? "<start>"} pageSize={PageSize}");
                var apiStopwatch = Stopwatch.StartNew();
                var response = await catalogApiClient.GetSpecialProductsPageAsync(
                    storeCode,
                    cursor,
                    PageSize,
                    cancellationToken);
                apiStopwatch.Stop();

                totalCount = Math.Max(totalCount, response.TotalCount);
                Log($"download page response store={storeCode} page={pageCount + 1} items={response.Items.Count} total={response.TotalCount} hasMore={response.HasMore} next={response.NextCursor ?? "<end>"} apiElapsedMs={apiStopwatch.ElapsedMilliseconds}");
                var upsertItems = response.Items
                    .Select(item => item.ToSellableItemDto())
                    .ToArray();
                var upsertElapsedMs = 0L;
                if (upsertItems.Length > 0)
                {
                    var upsertStopwatch = Stopwatch.StartNew();
                    await localCatalogRepository.UpsertSellableItemsAsync(upsertItems, cancellationToken);
                    upsertStopwatch.Stop();
                    upsertElapsedMs = upsertStopwatch.ElapsedMilliseconds;
                    upsertedCount += upsertItems.Length;
                    foreach (var productCode in upsertItems.Select(item => item.ProductCode))
                    {
                        if (!string.IsNullOrWhiteSpace(productCode))
                        {
                            specialProductCodes.Add(productCode.Trim());
                        }
                    }
                }

                pageCount++;
                downloadedCount += response.Items.Count;
                Log($"download page applied store={storeCode} page={pageCount} upserted={upsertItems.Length} downloaded={downloadedCount} total={totalCount} apiElapsedMs={apiStopwatch.ElapsedMilliseconds} upsertElapsedMs={upsertElapsedMs} totalElapsedMs={totalStopwatch.ElapsedMilliseconds}");
                ReportProgress(
                    progress,
                    storeCode,
                    SpecialProductDownloadProgressStage.Downloading,
                    totalCount,
                    downloadedCount,
                    pageCount,
                    upsertedCount,
                    unmarkedCount,
                    totalStopwatch,
                    forceComplete: !response.HasMore);

                if (!response.HasMore)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(response.NextCursor))
                {
                    throw new CatalogApiException("Special product API indicated more pages but did not return a next cursor.");
                }

                cursor = response.NextCursor;
            }

            var clearStopwatch = Stopwatch.StartNew();
            unmarkedCount = await localCatalogRepository.ClearSpecialProductFlagsExceptAsync(
                storeCode,
                specialProductCodes,
                cancellationToken);
            clearStopwatch.Stop();
            Log($"download clear stale flags store={storeCode} keepProducts={specialProductCodes.Count} unmarked={unmarkedCount} clearElapsedMs={clearStopwatch.ElapsedMilliseconds}");
            totalStopwatch.Stop();

            ReportProgress(
                progress,
                storeCode,
                SpecialProductDownloadProgressStage.Completed,
                totalCount,
                totalCount == 0 ? 0 : Math.Max(downloadedCount, totalCount),
                pageCount,
                upsertedCount,
                unmarkedCount,
                totalStopwatch,
                forceComplete: true);

            Log($"download completed store={storeCode} pages={pageCount} total={totalCount} downloaded={downloadedCount} upserted={upsertedCount} unmarked={unmarkedCount} totalElapsedMs={totalStopwatch.ElapsedMilliseconds}");
            return new SpecialProductDownloadResult(
                storeCode,
                pageCount,
                totalCount,
                downloadedCount,
                upsertedCount,
                unmarkedCount);
        }
        catch (Exception ex)
        {
            totalStopwatch.Stop();
            Log($"download failed store={storeCode} pages={pageCount} downloaded={downloadedCount} upserted={upsertedCount} unmarked={unmarkedCount} totalElapsedMs={totalStopwatch.ElapsedMilliseconds} error={ex.Message}");
            ReportProgress(
                progress,
                storeCode,
                SpecialProductDownloadProgressStage.Failed,
                totalCount,
                downloadedCount,
                pageCount,
                upsertedCount,
                unmarkedCount,
                totalStopwatch,
                errorMessage: ex.Message);
            throw;
        }
    }

    private static void ReportProgress(
        IProgress<SpecialProductDownloadProgress>? progress,
        string storeCode,
        SpecialProductDownloadProgressStage stage,
        int totalCount,
        int downloadedCount,
        int pageCount,
        int upsertedCount,
        int unmarkedCount,
        Stopwatch stopwatch,
        bool forceComplete = false,
        string? errorMessage = null)
    {
        if (progress is null)
        {
            return;
        }

        var percent = CalculatePercent(totalCount, downloadedCount, forceComplete);
        progress.Report(new SpecialProductDownloadProgress(
            storeCode,
            stage,
            totalCount,
            totalCount == 0 && forceComplete ? 0 : Math.Min(downloadedCount, totalCount),
            percent,
            pageCount,
            upsertedCount,
            unmarkedCount,
            stopwatch.ElapsedMilliseconds,
            errorMessage));
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
        ConsoleLog.Write("SpecialProducts", message);
    }
}
