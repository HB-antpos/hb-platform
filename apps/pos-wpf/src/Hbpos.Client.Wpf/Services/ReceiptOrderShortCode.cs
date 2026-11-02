using System.Globalization;

namespace Hbpos.Client.Wpf.Services;

/// <summary>
/// 小票单号短码：订单 GUID 去横杠后的末 8 位、转大写，与后端 <c>SalesOrder.ShortOrderNo</c> 同口径。
/// 例：7e4c91d2-0b5a-4f3e-9a21-5c8d6e0f13ab → 6E0F13AB。
/// 取「末」而不是「前」8 位，是因为 GUID 末段是随机位，前缀在同一台机器连续生成时更容易撞。
/// 纯逻辑，不依赖 WPF，便于单独测试。
/// </summary>
public static class ReceiptOrderShortCode
{
    public const int Length = 8;

    /// <summary>由订单 GUID 生成小票上的 8 位单号。</summary>
    public static string Create(Guid orderGuid)
    {
        return orderGuid.ToString("N", CultureInfo.InvariantCulture)[^Length..].ToUpperInvariant();
    }

    /// <summary>
    /// 判断输入是否恰好是 8 位十六进制（扫码枪扫到小票条码的结果），是则输出大写规范形式。
    /// 前后空白会被忽略；7 位、9 位、带横杠、含非十六进制字符一律不是短码，
    /// 这样完整 GUID、商品条码、货号都不会被误当成单号。
    /// </summary>
    public static bool TryNormalize(string? text, out string code)
    {
        code = string.Empty;
        var trimmed = text?.Trim();
        if (trimmed is null || trimmed.Length != Length)
        {
            return false;
        }

        foreach (var ch in trimmed)
        {
            // 只认 ASCII 十六进制；char.IsDigit 会放过全角数字等，不能用。
            var isHex = ch is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
            if (!isHex)
            {
                return false;
            }
        }

        code = trimmed.ToUpperInvariant();
        return true;
    }

    /// <summary>订单 GUID 的短码是否等于给定短码（比较前会把给定短码规范化）。</summary>
    public static bool Matches(Guid orderGuid, string code)
    {
        return TryNormalize(code, out var normalized) &&
               string.Equals(Create(orderGuid), normalized, StringComparison.Ordinal);
    }
}
