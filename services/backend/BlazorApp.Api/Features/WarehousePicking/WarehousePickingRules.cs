using BlazorApp.Shared.Models;

namespace BlazorApp.Api.Features.WarehousePicking;

public static class WarehousePickingErrorCodes
{
    public const string PickerRequired = "PICKER_REQUIRED";
    public const string PickerTicketInvalid = "PICKER_TICKET_INVALID";
    public const string PickerTicketExpired = "PICKER_TICKET_EXPIRED";
    public const string PickerBarcodeNotFound = "PICKER_BARCODE_NOT_FOUND";
    public const string PickerBarcodeAmbiguous = "PICKER_BARCODE_AMBIGUOUS";
    public const string PickerNotAllowed = "PICKER_NOT_ALLOWED";
    public const string OrderNotFound = "ORDER_NOT_FOUND";
    public const string OrderNotPickable = "ORDER_NOT_PICKABLE";
    public const string SessionNotStarted = "SESSION_NOT_STARTED";
    public const string SessionSubmitted = "SESSION_SUBMITTED";
    public const string LineNotFound = "LINE_NOT_FOUND";
    public const string MinOrderQuantityMissing = "MIN_ORDER_QUANTITY_MISSING";
    public const string MinOrderQuantityAlreadySet = "MIN_ORDER_QUANTITY_ALREADY_SET";
    public const string MinOrderQuantityInvalid = "MIN_ORDER_QUANTITY_INVALID";
    public const string PickedBelowZero = "PICKED_BELOW_ZERO";
    public const string PickedTotalChanged = "PICKED_TOTAL_CHANGED";
    public const string LineAlreadyComplete = "LINE_ALREADY_COMPLETE";
    public const string InvalidRequest = "INVALID_REQUEST";
    public const string CodeNotFound = "CODE_NOT_FOUND";
}

/// <summary>业务结果：StatusCode 由控制器原样映射为 HTTP 状态，Data 在冲突时携带最新行状态供客户端纠正。</summary>
public sealed record WarehousePickingResult<T>(
    bool Success,
    T? Data,
    string? ErrorCode,
    string? Message,
    int StatusCode
)
{
    public static WarehousePickingResult<T> Ok(T data) => new(true, data, null, null, 200);

    public static WarehousePickingResult<T> Fail(
        int statusCode,
        string errorCode,
        string message,
        T? data = default
    ) => new(false, data, errorCode, message, statusCode);
}

/// <summary>本次请求代表谁拣货：账号本人，或扫员工码得到的凭证持有人。</summary>
public sealed record WarehousePickerContext(
    string UserGuid,
    string Name,
    bool CanOverwriteMinOrderQuantity,
    string? AuthUserGuid,
    string? DeviceCode
);

internal readonly record struct WarehousePickDelta(int? Delta, string? ErrorCode, string? Message)
{
    public bool IsValid => Delta.HasValue;

    public static WarehousePickDelta Of(int delta) => new(delta, null, null);

    public static WarehousePickDelta Invalid(string errorCode, string message) =>
        new(null, errorCode, message);
}

internal sealed record WarehousePickingCodeSource(
    string? Code,
    string Target,
    string DetailGuid,
    int? MatchedBy,
    string? Label
);

internal static class WarehousePickingRules
{
    /// <summary>配货单二维码前缀；PDA 同一扫码入口靠它区分订单码与商品条码。</summary>
    public const string OrderQrPrefix = "HBSO:";

    public const int FlowStatusSubmitted = 1;
    public const int FlowStatusCompleted = 2;
    public const int FlowStatusPicking = 3;

    /// <summary>扫码枪可能带控制字符或首尾空格；码表与比较统一去掉并转大写。</summary>
    public static string NormalizeCode(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        var cleaned = new string(raw.Where(character => !char.IsControl(character)).ToArray());
        return cleaned.Trim().ToUpperInvariant();
    }

    /// <summary>从订单二维码（HBSO:2026-0418）或手输订单号取出订单号；空串返回 null。</summary>
    public static string? ParseOrderCode(string? raw)
    {
        var code = NormalizeCode(raw);
        if (code.StartsWith(OrderQrPrefix, StringComparison.Ordinal))
        {
            code = code[OrderQrPrefix.Length..].Trim();
        }

        return code.Length == 0 ? null : code;
    }

    /// <summary>已提交或配货中的订单才能拣货；购物车、已完成、已删除一律拒绝。</summary>
    public static bool IsPickable(int? flowStatus) =>
        flowStatus is FlowStatusSubmitted or FlowStatusPicking;

    public static bool HasMinOrderQuantity(int? minOrderQuantity) => minOrderQuantity is > 0;

    /// <summary>
    /// 计算一次拣货记录的件数变化。扫码与“加一”默认按服务端当前中包数累加（不信任客户端缓存的中包数）；
    /// 显式件数只用于“中包未设置时这次先按 1 件计入”。“减一”最多减到 0。
    /// </summary>
    public static WarehousePickDelta ResolveDelta(
        int source,
        int? pieces,
        int? minOrderQuantity,
        int currentTotal
    )
    {
        switch (source)
        {
            case WarehouseOrderPickSources.Scan:
            case WarehouseOrderPickSources.Increment:
                if (pieces.HasValue)
                {
                    return pieces.Value is > 0 and <= 100000
                        ? WarehousePickDelta.Of(pieces.Value)
                        : WarehousePickDelta.Invalid(
                            WarehousePickingErrorCodes.InvalidRequest,
                            "件数必须为正整数"
                        );
                }

                return HasMinOrderQuantity(minOrderQuantity)
                    ? WarehousePickDelta.Of(minOrderQuantity!.Value)
                    : WarehousePickDelta.Invalid(
                        WarehousePickingErrorCodes.MinOrderQuantityMissing,
                        "该商品没有中包数，请先设置中包数"
                    );
            case WarehouseOrderPickSources.Decrement:
                if (!HasMinOrderQuantity(minOrderQuantity))
                {
                    return WarehousePickDelta.Invalid(
                        WarehousePickingErrorCodes.MinOrderQuantityMissing,
                        "该商品没有中包数，请手动输入数量"
                    );
                }

                if (currentTotal <= 0)
                {
                    return WarehousePickDelta.Invalid(
                        WarehousePickingErrorCodes.PickedBelowZero,
                        "已拣数量已经为 0"
                    );
                }

                return WarehousePickDelta.Of(-Math.Min(currentTotal, minOrderQuantity!.Value));
            default:
                return WarehousePickDelta.Invalid(
                    WarehousePickingErrorCodes.InvalidRequest,
                    "未知的拣货来源"
                );
        }
    }

    /// <summary>与订单行批量改配货数同口径：订货数与配货数都 ≤0 的行软删除（仓库主动加行后没拣到）。</summary>
    public static bool ShouldSoftDelete(decimal? orderedQuantity, decimal allocatedQuantity) =>
        (orderedQuantity ?? 0) <= 0 && allocatedQuantity <= 0;

    /// <summary>
    /// 把各类码合并成扫码码表：同一个码可能指向多行（重复商品、同码多品），保留全部行交给客户端选择；
    /// 匹配方式取优先级最高的一种（主条码 &lt; 货号 &lt; 商品编码 &lt; 多码 &lt; 套装子码）。
    /// </summary>
    public static List<WarehousePickingCodeDto> BuildCodeMap(
        IEnumerable<WarehousePickingCodeSource> sources
    )
    {
        var entries = new Dictionary<(string Code, string Target), WarehousePickingCodeDto>();
        foreach (var source in sources)
        {
            var code = NormalizeCode(source.Code);
            if (code.Length == 0 || string.IsNullOrWhiteSpace(source.DetailGuid))
            {
                continue;
            }

            var key = (code, source.Target);
            if (!entries.TryGetValue(key, out var entry))
            {
                entry = new WarehousePickingCodeDto
                {
                    Code = code,
                    Target = source.Target,
                    MatchedBy = source.MatchedBy,
                    Label = source.Label,
                };
                entries[key] = entry;
            }
            else
            {
                if (source.MatchedBy.HasValue
                    && (!entry.MatchedBy.HasValue || source.MatchedBy.Value < entry.MatchedBy.Value))
                {
                    entry.MatchedBy = source.MatchedBy;
                    entry.Label = source.Label ?? entry.Label;
                }

                entry.Label ??= source.Label;
            }

            if (!entry.DetailGuids.Contains(source.DetailGuid, StringComparer.OrdinalIgnoreCase))
            {
                entry.DetailGuids.Add(source.DetailGuid);
            }
        }

        return entries.Values
            .OrderBy(entry => entry.Code, StringComparer.Ordinal)
            .ThenBy(entry => entry.Target, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>订单行与拣货数对比：短缺与超拣行数用于提交后的汇总。</summary>
    public static (int ShortLines, int OverLines) CountVariances(
        IEnumerable<(decimal Ordered, int Picked)> lines
    )
    {
        var shortLines = 0;
        var overLines = 0;
        foreach (var (ordered, picked) in lines)
        {
            if (picked < ordered)
            {
                shortLines++;
            }
            else if (picked > ordered)
            {
                overLines++;
            }
        }

        return (shortLines, overLines);
    }
}
