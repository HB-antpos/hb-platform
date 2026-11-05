using Hbpos.Contracts.Linkly;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Wpf.Services;

/// <summary>
/// 刷卡退款的通道选择：默认沿用收银机当前设置的通道，只有当前通道根本无法处理这笔原付款时才改走原通道。
/// </summary>
/// <remarks>
/// 中文注释：
/// - Linkly 本地退款是“顾客当场刷卡”的退款，不依赖原交易引用，所以 Linkly 收银机可以退任何通道的原付款，保持不变。
/// - Square 退款只能按 <c>SQ:{paymentId}</c> 退回原付款；Square 收银机遇到 ANZ 系列引用时原本必然失败，此时改走 Linkly。
/// - 不做“一律按原通道退”：门店从 Square 换到 Linkly 后 Square 令牌可能已撤，强行改走 Square 会让原本能用的刷卡退款失败。
/// - Zeller 退款同样只能按原交易号（<c>ZELLER:{reference}:{transactionUuid}</c>）退回原付款：
///   Zeller 收银机遇到 Linkly / Square 的原付款改走原通道；Square 收银机遇到 Zeller 原付款改走 Zeller（方案 D7）。
///   Linkly 收银机遇到 Zeller 原付款仍按上面的规则当场刷卡退款。
/// </remarks>
internal static class CardRefundProcessorRouting
{
    public static CardTerminalSettings Resolve(CardTerminalSettings settings, string? originalReference)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.Processor == CardProcessorKind.Square && IsLinklyReference(originalReference))
        {
            return settings with { Processor = CardProcessorKind.Linkly };
        }

        if (settings.Processor == CardProcessorKind.Square && ZellerPaymentReference.IsZellerRefundSource(originalReference))
        {
            return settings with { Processor = CardProcessorKind.Zeller };
        }

        if (settings.Processor == CardProcessorKind.Zeller)
        {
            if (IsLinklyReference(originalReference))
            {
                return settings with { Processor = CardProcessorKind.Linkly };
            }

            if (IsSquareReference(originalReference))
            {
                return settings with { Processor = CardProcessorKind.Square };
            }
        }

        return settings;
    }

    // 中文注释：Square 付款引用是 SQ:{paymentId}。
    internal static bool IsSquareReference(string? reference) =>
        reference?.Trim().StartsWith("SQ:", StringComparison.OrdinalIgnoreCase) == true;

    // 中文注释：ANZ（本地 IP）、ANZCLOUD（云直连）、ANZBACKEND（云后端）三种前缀都是 Linkly 付款。
    internal static bool IsLinklyReference(string? reference)
    {
        var trimmed = reference?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return false;
        }

        return trimmed.StartsWith("ANZ:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("ANZCLOUD:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith($"{LinklyBackendPaymentReference.Prefix}:", StringComparison.OrdinalIgnoreCase);
    }
}
