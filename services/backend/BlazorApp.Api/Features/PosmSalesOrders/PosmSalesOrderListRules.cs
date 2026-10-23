using BlazorApp.Shared.DTOs;

namespace BlazorApp.Api.Features.PosmSalesOrders;

/// <summary>
/// Web 收银记录列表的查询口径：日期区间上限、逐单汇总明细类条件的范围约束、关键词解析上限。
/// 这些上限保证生产上单次查询在 3 秒内返回（2026-09-19 按生产原句实测定出）。
/// </summary>
public static class PosmSalesOrderListRules
{
    /// <summary>日期区间上限（含首尾）。全部分店 92 天约 72 万单，汇总与取页实测均在 0.5 秒内。</summary>
    public const int MaxRangeDays = 92;

    /// <summary>
    /// 件数、种数条件与排序要对范围内每一单汇总明细。订单号索引包含数量后按订单号范围连续扫描汇总，
    /// 生产冷读实测：全店 7 天约 1.2 秒、全店 31 天 3–5 秒；单店 92 天约 1.3 秒。
    /// 因此只约束全部分店最长 7 天，指定单个分店沿用 92 天上限。
    /// </summary>
    public const int DetailAggregateAllStoresMaxDays = 7;

    /// <summary>关键词在商品主档解析出的商品编码上限；更宽泛的关键词（如单个字母）几乎等于不过滤，直接提示用户收窄。</summary>
    public const int MaxKeywordProductCodes = 20000;

    public const string ErrorDateRangeRequired = "DATE_RANGE_REQUIRED";
    public const string ErrorDateRangeTooLong = "DATE_RANGE_TOO_LONG";
    public const string ErrorDetailFilterRangeTooLong = "DETAIL_FILTER_RANGE_TOO_LONG";
    public const string ErrorKeywordTooBroad = "KEYWORD_TOO_BROAD";

    /// <summary>区间天数含首尾：同一天为 1 天。</summary>
    public static int CountDays(DateTime startDate, DateTime endDate) =>
        (endDate.Date - startDate.Date).Days + 1;

    /// <summary>
    /// 关键词像订单号片段（至少 4 位，只含十六进制字符和连字符）时才按订单号匹配。
    /// 页面显示订单号后 6 位，用户照抄即可命中；商品名等其他关键词不再逐单比较订单号，省下全范围扫描。
    /// </summary>
    public static bool IsOrderNumberFragment(string keyword)
    {
        var trimmed = keyword.Trim();
        return trimmed.Length >= 4 && trimmed.All(ch => Uri.IsHexDigit(ch) || ch == '-');
    }

    public const string RefundFilterAny = "any";
    public const string RefundFilterPartial = "partial";
    public const string RefundFilterFull = "full";

    /// <summary>退款单的订单状态（旧 POS 的退款是一张独立的 Status=3 订单，明细与支付都是负数）。</summary>
    public const int RefundOrderStatus = 3;

    /// <summary>只认 any / partial / full（不区分大小写），其余一律当作不筛选，避免把任意字符串带进 SQL 分支。</summary>
    public static string? NormalizeRefundFilter(string? raw) =>
        raw?.Trim().ToLowerInvariant() switch
        {
            RefundFilterAny => RefundFilterAny,
            RefundFilterPartial => RefundFilterPartial,
            RefundFilterFull => RefundFilterFull,
            _ => null,
        };

    /// <summary>
    /// 整单金额的取值口径：以订单卡片显示的实付金额为准，实付为 0 / 空时回落到订单总金额。
    /// 部分退款（橙）= 含退货且该金额不为负；整单退款（红）= 含退货且该金额为负。
    /// </summary>
    public static decimal ResolveWholeOrderAmount(decimal? actualAmount, decimal? totalAmount) =>
        actualAmount is { } actual && actual != 0m ? actual : totalAmount ?? 0m;

    /// <summary>整单金额为负视为整单退款，其余（含 0）视为部分退款。</summary>
    public static bool IsFullRefundAmount(decimal? actualAmount, decimal? totalAmount) =>
        ResolveWholeOrderAmount(actualAmount, totalAmount) < 0m;

    /// <summary>件数、种数的筛选或排序需要逐单汇总明细。</summary>
    public static bool NeedsDetailAggregates(PosmSalesOrderQueryParams query)
    {
        if (query.SkuCountMin.HasValue || query.SkuCountMax.HasValue
            || query.QuantityMin.HasValue || query.QuantityMax.HasValue)
        {
            return true;
        }
        var sortField = query.SortField?.Trim().ToLowerInvariant();
        return sortField is "skucount" or "quantity";
    }

    /// <summary>生效分店只有一家：显式指定分店，或授权范围本身只有一家。</summary>
    public static bool IsSingleBranch(PosmSalesOrderQueryParams query) =>
        !string.IsNullOrWhiteSpace(query.BranchCode)
        || (query.BranchCodes != null
            && query.BranchCodes
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Select(code => code.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() == 1);

    /// <summary>
    /// Web 列表请求校验：区间必填、有序且不超过 92 天；件数/种数条件在全部分店时不超过 7 天。
    /// 前端同样把关，但拼接请求绕过前端也不能放大扫描范围。
    /// </summary>
    public static bool TryValidateWebQuery(
        PosmSalesOrderQueryParams query,
        out string? error,
        out string? errorCode
    )
    {
        error = null;
        errorCode = null;
        if (!query.StartDate.HasValue || !query.EndDate.HasValue || query.StartDate.Value.Date > query.EndDate.Value.Date)
        {
            error = "请选择有效的日期范围。";
            errorCode = ErrorDateRangeRequired;
            return false;
        }

        var days = CountDays(query.StartDate.Value, query.EndDate.Value);
        if (days > MaxRangeDays)
        {
            error = $"日期范围不能超过 {MaxRangeDays} 天。";
            errorCode = ErrorDateRangeTooLong;
            return false;
        }

        if (NeedsDetailAggregates(query) && !IsSingleBranch(query) && days > DetailAggregateAllStoresMaxDays)
        {
            error = $"按件数、种数筛选或排序时，全部分店最长 {DetailAggregateAllStoresMaxDays} 天；请选择分店或缩短日期。";
            errorCode = ErrorDetailFilterRangeTooLong;
            return false;
        }

        return true;
    }
}

/// <summary>查询条件会让扫描失控时抛出，由控制器转成 400 并把原因告诉用户。</summary>
public sealed class PosmSalesOrderQueryRejectedException : Exception
{
    public PosmSalesOrderQueryRejectedException(string errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}
