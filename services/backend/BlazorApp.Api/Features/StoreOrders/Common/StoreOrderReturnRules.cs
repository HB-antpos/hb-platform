namespace BlazorApp.Api.Features.StoreOrders.Common;

/// <summary>
/// 请求里携带的退货字段。语义：null = 本次未提供（沿用原值）；Note 传空串 = 清空备注。
/// </summary>
internal readonly record struct StoreOrderReturnLineRequest(
    string? Reason,
    int? Disposition,
    string? Note
)
{
    /// <summary>本次请求是否带了任何退货字段。</summary>
    internal bool HasAnyField =>
        !string.IsNullOrWhiteSpace(Reason) || Disposition.HasValue || !string.IsNullOrEmpty(Note);
}

/// <summary>退货行写入判定结果；Error 非空表示必须拒绝（中文文案，直接回给前端）。</summary>
internal sealed record StoreOrderReturnWriteDecision(
    bool IsReturnLine,
    string? Error,
    string? Reason,
    int? Disposition,
    string? Note
)
{
    internal static StoreOrderReturnWriteDecision Reject(string error) =>
        new(false, error, null, null, null);

    /// <summary>普通（非退货）行：退货字段一律清空。</summary>
    internal static StoreOrderReturnWriteDecision NotReturn { get; } =
        new(false, null, null, null, null);
}

/// <summary>退货规则校验失败；由行命令存储捕获并转成带中文文案的失败结果（事务回滚）。</summary>
internal sealed class StoreOrderReturnRuleException(string message) : Exception(message);

/// <summary>
/// 分店订单「负数发货 = 退货」的口径（方案 docs/plans/2026-10-21-store-order-returns-negative-qty.md §2 / §9）：
/// 退货行 = AllocQuantity &lt; 0 且 Quantity = 0；订货数量永远 ≥ 0；退货必须带原因。
/// 单据按净额切换：净额 &lt; 0 为退款单（creditNote），否则为发票（invoice）。
/// </summary>
internal static class StoreOrderReturnRules
{
    internal const string DocumentTypeInvoice = "invoice";
    internal const string DocumentTypeCreditNote = "creditNote";

    internal const int DispositionRestock = 0;
    internal const int DispositionWriteOff = 1;
    internal const int MaxNoteLength = 200;

    // 原因码 → 单据上的英文文案（Adjustments / Returned goods 段的 Reason 行）。两端码值一致，改动须同步前端。
    private static readonly IReadOnlyDictionary<string, string> ReasonLabels =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["damaged"] = "damaged",
            ["wrongItem"] = "wrong item sent",
            ["overShipped"] = "over-shipped",
            ["quality"] = "quality issue",
            ["nearExpiry"] = "near expiry",
            ["other"] = "other",
        };

    // 规范码（大小写以此为准），入库前统一成这一套。
    private static readonly string[] CanonicalReasons =
        ["damaged", "wrongItem", "overShipped", "quality", "nearExpiry", "other"];

    internal static IReadOnlyList<string> ReasonCodes => CanonicalReasons;

    /// <summary>退货行判定：发货数量为负。NULL 视为 0，不是退货行。</summary>
    internal static bool IsReturnLine(decimal? allocQuantity) => (allocQuantity ?? 0) < 0;

    /// <summary>把输入原因码规范成库里的大小写；空白或不在表内返回 null。</summary>
    internal static string? NormalizeReason(string? reason)
    {
        var trimmed = reason?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return CanonicalReasons.FirstOrDefault(code =>
            string.Equals(code, trimmed, StringComparison.OrdinalIgnoreCase)
        );
    }

    /// <summary>单据上的英文原因文案；未知码原样返回，空返回 null。</summary>
    internal static string? ToDocumentReason(string? reason)
    {
        var trimmed = reason?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return ReasonLabels.TryGetValue(trimmed, out var label) ? label : trimmed;
    }

    /// <summary>
    /// 按净额（各行 allocQuantity × importPrice 之和，ex GST）判定单据类型。
    /// 先按两位小数取整再判负：-0.004 显示为 $0.00，应仍是发票（$0.00），不能变成退款单。
    /// 后端发票接口、邮件主题、附件名、订单详情汇总必须共用这一个判定。
    /// </summary>
    internal static string ResolveDocumentType(decimal netSubTotal)
    {
        return decimal.Round(netSubTotal, 2, MidpointRounding.AwayFromZero) < 0
            ? DocumentTypeCreditNote
            : DocumentTypeInvoice;
    }

    /// <summary>
    /// 退货行写入判定（保存层规则表）。
    /// newAllocQuantity：本次写入之后该行的发货数量（未改发货数时传原值，这样无权限者也不能改价格等间接改动退货行）；
    /// existingOrderQuantity：该商品本单已有明细的订货数量（本单还没有该商品传 null）。
    /// </summary>
    internal static StoreOrderReturnWriteDecision Evaluate(
        bool canRecordReturns,
        decimal newAllocQuantity,
        decimal? existingOrderQuantity,
        decimal? existingAllocQuantity,
        string? existingReason,
        int? existingDisposition,
        string? existingNote,
        StoreOrderReturnLineRequest request
    )
    {
        var existingIsReturn = IsReturnLine(existingAllocQuantity);

        if (newAllocQuantity >= 0)
        {
            // 普通发货行：不得带退货字段（发货数量 = 0 视为清空该行，字段忽略）。
            if (newAllocQuantity > 0 && request.HasAnyField)
            {
                return StoreOrderReturnWriteDecision.Reject(
                    "只有退货行（发货数量为负）才能填写退货原因、处置或备注"
                );
            }

            // 退货行只有持权限者能动：没有权限不能把它改成 0 / 正数，也就不能借此删掉别人录的退货。
            if (existingIsReturn && !canRecordReturns)
            {
                return StoreOrderReturnWriteDecision.Reject(
                    "没有「分店订单退货」权限，不能修改或删除退货行"
                );
            }

            return StoreOrderReturnWriteDecision.NotReturn;
        }

        // 以下 newAllocQuantity < 0：退货行。
        if (!canRecordReturns)
        {
            return StoreOrderReturnWriteDecision.Reject(
                "没有「分店订单退货」权限，不能录入退货（负数发货）"
            );
        }

        // 订货数量永远 ≥ 0：该商品本单已有订货时，退货不能和订货挤在同一行（一单一商品一行，本期不改）。
        if ((existingOrderQuantity ?? 0) > 0)
        {
            return StoreOrderReturnWriteDecision.Reject("该商品本单已有订货，请退到另一张订单");
        }

        string? reason;
        if (!string.IsNullOrWhiteSpace(request.Reason))
        {
            reason = NormalizeReason(request.Reason);
            if (reason == null)
            {
                return StoreOrderReturnWriteDecision.Reject("退货原因无效，请重新选择");
            }
        }
        else
        {
            // 本次没带原因：已经是退货行就沿用原原因（例如只改数量或价格），否则必须选择原因。
            reason = existingIsReturn ? NormalizeReason(existingReason) : null;
            if (reason == null)
            {
                return StoreOrderReturnWriteDecision.Reject("请选择退货原因");
            }
        }

        var disposition = request.Disposition ?? (existingIsReturn ? existingDisposition : null);
        if (
            disposition.HasValue
            && disposition != DispositionRestock
            && disposition != DispositionWriteOff
        )
        {
            return StoreOrderReturnWriteDecision.Reject("退货处置无效");
        }

        string? note;
        if (request.Note == null)
        {
            note = existingIsReturn ? existingNote : null;
        }
        else
        {
            note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        }

        if (note is { Length: > MaxNoteLength })
        {
            return StoreOrderReturnWriteDecision.Reject(
                $"退货备注不能超过 {MaxNoteLength} 个字符"
            );
        }

        return new StoreOrderReturnWriteDecision(true, null, reason, disposition, note);
    }
}
