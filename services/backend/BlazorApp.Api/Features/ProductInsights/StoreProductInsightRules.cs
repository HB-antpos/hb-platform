namespace BlazorApp.Api.Features.ProductInsights;

/// <summary>查询口径中不依赖数据库的事实判断，供服务与回归测试共享。</summary>
public static class StoreProductInsightRules
{
    public static bool CanAccessStore(IReadOnlyCollection<string>? allowedStoreCodes, string requestedStoreCode) =>
        allowedStoreCodes == null || allowedStoreCodes.Contains(requestedStoreCode, StringComparer.Ordinal);

    public static bool IsValidRange(DateTime startDate, DateTime endDate) => startDate.Date <= endDate.Date;

    /// <summary>不带 days 的旧客户端默认 90 天；新客户端显式传 days（移动端默认 180）。</summary>
    public const int DefaultRangeDays = 90;
    public const int MaxDefaultRangeDays = 366;

    public static bool IsValidDefaultRangeDays(int days) => days is >= 1 and <= MaxDefaultRangeDays;

    // 本地进货按进货单口径判断；入库状态不决定单据是否参与统计。
    public static bool IsValidLocalPurchase(bool isInvoiceDeleted, bool isDetailDeleted, DateTime? inboundDate, DateTime? orderDate) =>
        !isInvoiceDeleted && !isDetailDeleted && (inboundDate.HasValue || orderDate.HasValue);

    public static bool IsWarehouseDelivery(int? flowStatus, DateTime? outboundDate, decimal allocatedQuantity, DateTime startDate, DateTime endDate) =>
        flowStatus == 2 && outboundDate.HasValue && allocatedQuantity > 0
        && outboundDate.Value >= startDate.Date && outboundDate.Value < endDate.Date.AddDays(1);

    public static bool IsHistoricalRecordEligible(DateTime recordDate, DateTime endDate) =>
        recordDate < endDate.Date.AddDays(1);
}
