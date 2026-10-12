using System.Diagnostics;
using Hbpos.Contracts.Catalog;

namespace Hbpos.Client.Wpf.Services;

public interface IRemoteLookupRefreshService
{
    Task<RemoteLookupRefreshResult> RefreshLookupAsync(
        string storeCode,
        string lookupCode,
        CancellationToken cancellationToken = default);
}

public sealed record RemoteLookupRefreshResult(
    string StoreCode,
    string LookupCode,
    bool Found,
    SellableItemDto? Item,
    int DeletedCount)
{
    public bool Updated => Found && Item is not null;

    public bool Deleted => !Found;
}

public sealed class RemoteLookupRefreshService(
    ILocalCatalogRepository localCatalogRepository,
    ICatalogApiClient catalogApiClient) : IRemoteLookupRefreshService
{
    public async Task<RemoteLookupRefreshResult> RefreshLookupAsync(
        string storeCode,
        string lookupCode,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(lookupCode);

        var stopwatch = Stopwatch.StartNew();
        Log($"refresh start storeCode={storeCode} lookupCode={lookupCode}");
        try
        {
            Log($"catalog api lookup dispatch storeCode={storeCode} lookupCode={lookupCode}");
            var response = await catalogApiClient.LookupSellableItemAsync(storeCode, lookupCode, cancellationToken);
            if (response is { Found: true })
            {
                if (response.Item is null)
                {
                    throw new CatalogApiException("Catalog lookup response was found but did not include an item.");
                }

                var item = response.Item.ToSellableItemDto();
                // 中文注释：HTTP 返回后续体回到调用方上下文（扫码加购链路是 UI 线程），而目录写事务（BEGIN IMMEDIATE）
                // 遇上目录同步提交最长会等锁 5 秒（busy_timeout）。本地写库放进线程池；返回值不变，
                // 调用方 PosTerminalWorkflowService 在它自己 await 之后的上下文（UI 线程）回写购物车。
                await Task.Run(() => localCatalogRepository.UpsertSellableItemsAsync([item], cancellationToken));
                stopwatch.Stop();
                Log($"refresh completed storeCode={storeCode} lookupCode={lookupCode} found=True upserted=1 deleted=0 productCode={item.ProductCode} referenceCode={item.ReferenceCode ?? "<null>"} elapsedMs={stopwatch.ElapsedMilliseconds}");
                return new RemoteLookupRefreshResult(storeCode, lookupCode, true, item, 0);
            }

            var deleteLookupCode = response is null
                ? lookupCode
                : GetDeleteLookupCode(response);
            // 删除本地失效条目同样是写事务，理由同上，放进线程池。
            var deletedCount = await Task.Run(() => localCatalogRepository.DeleteByLookupCodesAsync(
                storeCode,
                [deleteLookupCode],
                cancellationToken));

            stopwatch.Stop();
            Log($"refresh completed storeCode={storeCode} lookupCode={lookupCode} found=False upserted=0 deleted={deletedCount} deleteLookupCode={deleteLookupCode} elapsedMs={stopwatch.ElapsedMilliseconds}");
            return new RemoteLookupRefreshResult(storeCode, lookupCode, false, null, deletedCount);
        }
        catch (OperationCanceledException ex)
        {
            stopwatch.Stop();
            Log($"refresh canceled storeCode={storeCode} lookupCode={lookupCode} elapsedMs={stopwatch.ElapsedMilliseconds} error={ex.Message}");
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            Log($"refresh failed storeCode={storeCode} lookupCode={lookupCode} elapsedMs={stopwatch.ElapsedMilliseconds} error={ex.Message}");
            throw;
        }
    }

    private static string GetDeleteLookupCode(CatalogLookupResponse response)
    {
        if (!string.IsNullOrWhiteSpace(response.LookupCodeNormalized))
        {
            return response.LookupCodeNormalized;
        }

        return response.LookupCode;
    }

    private static void Log(string message)
    {
        ConsoleLog.Write("RemoteLookupRefresh", message);
    }
}
