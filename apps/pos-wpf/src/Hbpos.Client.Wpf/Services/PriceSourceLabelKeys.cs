namespace Hbpos.Client.Wpf.Services;

/// <summary>
/// 服务端目录里的价格来源标签（set、store-retail 等）是原始英文值，收银员看不懂；
/// 这里把它映射到本地化键，供"多结果选择"卡片显示"主条码 / 套装条码"，
/// 让同一商品的主条码与套装码撞码时，收银员能分清两张卡片。
/// </summary>
public static class PriceSourceLabelKeys
{
    public const string Main = "pendingChoice.source.main";
    public const string Set = "pendingChoice.source.set";
    public const string MultiCode = "pendingChoice.source.multiCode";
    public const string Clearance = "pendingChoice.source.clearance";

    /// <summary>未知标签返回 null，调用方原样显示，不吞信息。</summary>
    public static string? ResolveKey(string? label)
    {
        var normalized = (label ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized.Length == 0)
        {
            return null;
        }

        // 套装有三种标签：set、set-follow-main、set-store-multi-code，统一按前缀归类。
        if (normalized.StartsWith("set", StringComparison.Ordinal))
        {
            return Set;
        }

        return normalized switch
        {
            "product" or "store-retail" => Main,
            "multi-code" => MultiCode,
            "clearance" => Clearance,
            _ => null
        };
    }
}
