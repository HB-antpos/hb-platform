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
    public const string AssignNotAllowed = "ASSIGN_NOT_ALLOWED";
    public const string AssignPickerInvalid = "ASSIGN_PICKER_INVALID";
    public const string AssignCountsInvalid = "ASSIGN_COUNTS_INVALID";
    public const string AssignLinesInvalid = "ASSIGN_LINES_INVALID";
    public const string SlipStale = "SLIP_STALE";
    public const string SegmentsIncomplete = "SEGMENTS_INCOMPLETE";
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

    /// <summary>分单拣货单条码前缀：HBSP:订单号/段号/版本。</summary>
    public const string SlipCodePrefix = "HBSP:";

    /// <summary>分配版本起点：版本按距此时刻的秒数计，int 可用到 2094 年。</summary>
    private static readonly DateTime AssignmentVersionEpochUtc = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// 新的分配版本：取当前秒数与旧版本 +1 的较大值。按时间取值，撤销分配（删行）后再派也不会与旧分单撞号；
    /// 同一秒内连续保存靠“旧版本 +1”保证严格递增。
    /// </summary>
    public static int NextAssignmentVersion(int? previousMax, DateTime nowUtc)
    {
        var bySeconds = (int)Math.Max(1, (nowUtc - AssignmentVersionEpochUtc).TotalSeconds);
        return Math.Max(bySeconds, (previousMax ?? 0) + 1);
    }

    /// <summary>分单条码：HBSP:订单号/段号/版本（版本转大写 36 进制，缩短条码长度）。</summary>
    public static string FormatSlipCode(string orderNo, int segmentNo, int version) =>
        $"{SlipCodePrefix}{orderNo.Trim().ToUpperInvariant()}/{segmentNo}/{ToBase36(version)}";

    /// <summary>解析分单条码；订单号里不会有斜杠，从右往左取段号与版本。不是分单条码返回 null。</summary>
    public static (string OrderNo, int SegmentNo, int Version)? ParseSlipCode(string? raw)
    {
        var code = NormalizeCode(raw);
        if (!code.StartsWith(SlipCodePrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var parts = code[SlipCodePrefix.Length..].Split('/');
        if (parts.Length != 3
            || parts[0].Length == 0
            || !int.TryParse(parts[1], out var segmentNo)
            || segmentNo <= 0
            || !TryParseBase36(parts[2], out var version))
        {
            return null;
        }

        return (parts[0], segmentNo, version);
    }

    private static string ToBase36(int value)
    {
        const string digits = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        if (value <= 0)
        {
            return "0";
        }

        var chars = new Stack<char>();
        while (value > 0)
        {
            chars.Push(digits[value % 36]);
            value /= 36;
        }

        return new string(chars.ToArray());
    }

    private static bool TryParseBase36(string text, out int value)
    {
        value = 0;
        if (text.Length is 0 or > 6)
        {
            return false;
        }

        long result = 0;
        foreach (var character in text)
        {
            var digit = character is >= '0' and <= '9' ? character - '0'
                : character is >= 'A' and <= 'Z' ? character - 'A' + 10
                : -1;
            if (digit < 0)
            {
                return false;
            }

            result = result * 36 + digit;
        }

        if (result > int.MaxValue)
        {
            return false;
        }

        value = (int)result;
        return true;
    }

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

    /// <summary>
    /// 分段拣货里一个品种“处理完”的口径：已拣齐（含超拣）或标了货位没货。
    /// 拣了一部分却没标没货的品种算没处理完——缺货必须明确标出来，不能半拣就提交。
    /// </summary>
    public static bool IsLineSettled(decimal ordered, decimal picked, bool stockout) =>
        picked >= ordered || stockout;

    /// <summary>
    /// 有拣货分配的订单：找出还有品种没处理完的段（按段号升序）。没有分配的品种不参与；
    /// 段负责人取该段任一行（同段同人）。返回空表示每段都拣完了，可以提交整单。
    /// </summary>
    public static List<WarehouseIncompleteSegment> FindIncompleteSegments(
        IEnumerable<(int SegmentNo, string? PickerName, decimal Ordered, decimal Picked, bool Stockout)> lines
    )
    {
        return lines
            .GroupBy(line => line.SegmentNo)
            .Select(group => new WarehouseIncompleteSegment(
                group.Key,
                group.Select(line => line.PickerName).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)),
                group.Count(line => IsLineSettled(line.Ordered, line.Picked, line.Stockout)),
                group.Count()
            ))
            .Where(segment => segment.SettledLineCount < segment.LineCount)
            .OrderBy(segment => segment.SegmentNo)
            .ToList();
    }

    /// <summary>提交被拦时给员工看的说明：哪几段没拣完，以及放行办法。</summary>
    public static string DescribeIncompleteSegments(IReadOnlyList<WarehouseIncompleteSegment> segments)
    {
        var parts = segments.Select(segment =>
            $"第 {segment.SegmentNo} 段 {(string.IsNullOrWhiteSpace(segment.PickerName) ? "待领取" : segment.PickerName)} {segment.SettledLineCount}/{segment.LineCount}"
        );
        return $"还有 {segments.Count} 段没拣完，不能提交整单：{string.Join("、", parts)}。"
            + "没货的品种标“货位没货”后才算拣完；确实拣不完请经理在订单详情撤销分配或改派。";
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

/// <summary>还没拣完的分段：段号、负责人（待领取为空）、已处理品种数 / 段内品种数。</summary>
public sealed record WarehouseIncompleteSegment(int SegmentNo, string? PickerName, int SettledLineCount, int LineCount);
