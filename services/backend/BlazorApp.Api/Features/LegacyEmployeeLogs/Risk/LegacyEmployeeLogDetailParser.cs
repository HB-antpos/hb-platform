using System.Globalization;
using System.Text.RegularExpressions;

namespace BlazorApp.Api.Features.LegacyEmployeeLogs.Risk;

/// <summary>
/// 旧收银详情文本解析。格式是全角逗号分隔的「键:值」片段，例如
/// 「从购物车删除商品:Kids Luigi hat，编码:PP97640，数量:1，单价:6.99，总金额:6.99」、
/// 「商品:Piggy Bank，原折扣:0.0%，新折扣:10%」、「将所有商品折扣率设置为:10%，购物车商品数:6，原总金额:17.55」。
/// 只按全角逗号切分（商品名里可能有半角逗号），键取第一个冒号之前的文字；与 Web 端 parseLegacyDetail 同一口径。
/// </summary>
public static partial class LegacyEmployeeLogDetailParser
{
    [GeneratedRegex("[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", RegexOptions.IgnoreCase)]
    private static partial Regex GuidPattern();

    [GeneratedRegex(@"^([^:：]{1,20})[:：]\s*(.*)$", RegexOptions.Singleline)]
    private static partial Regex KeyValuePattern();

    public static IReadOnlyDictionary<string, string> Parse(string? detail)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(detail))
        {
            return result;
        }
        foreach (var raw in detail.Split('，'))
        {
            var match = KeyValuePattern().Match(raw.Trim());
            if (match.Success)
            {
                // 同名键只取第一次出现，避免后面的说明片段覆盖关键字段。
                result.TryAdd(match.Groups[1].Value.Trim(), match.Groups[2].Value.Trim());
            }
        }
        return result;
    }

    /// <summary>取数值；兼容「10%」「-1」「2.50」等写法，解析不了返回 null。</summary>
    public static decimal? Number(IReadOnlyDictionary<string, string> fields, string key)
    {
        if (!fields.TryGetValue(key, out var value))
        {
            return null;
        }
        var trimmed = value.Trim().TrimEnd('%').Trim();
        return decimal.TryParse(trimmed, NumberStyles.Number, CultureInfo.InvariantCulture, out var number) ? number : null;
    }

    public static string? Text(IReadOnlyDictionary<string, string> fields, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (fields.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }
        return null;
    }

    /// <summary>重打印、挂单等详情里的订单号（GUID，统一大写）。</summary>
    public static string? OrderId(string? detail)
    {
        if (string.IsNullOrEmpty(detail))
        {
            return null;
        }
        var match = GuidPattern().Match(detail);
        return match.Success ? match.Value.ToUpperInvariant() : null;
    }

    /// <summary>
    /// 应收减少金额（正数）。删除取总金额；改价取原价与新价之差（详情没有数量，按单件计）；
    /// 整单折扣取原总金额 × 折扣率；无小票退货取单价 × 退货数量。单品折扣详情没有价格，算不出金额。
    /// </summary>
    public static decimal? AmountImpact(string? operation, string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return null;
        }
        var fields = Parse(detail);
        decimal? amount = operation?.Trim() switch
        {
            LegacyEmployeeLogRiskCatalog.DeleteItem => Number(fields, "总金额"),
            LegacyEmployeeLogRiskCatalog.ChangePrice =>
                Number(fields, "原价格") is { } original && Number(fields, "新价格") is { } updated ? original - updated : null,
            LegacyEmployeeLogRiskCatalog.ChangeAllDiscount =>
                Number(fields, "原总金额") is { } total && Number(fields, "将所有商品折扣率设置为") is { } rate ? total * rate / 100m : null,
            LegacyEmployeeLogRiskCatalog.NoReceiptReturn =>
                Number(fields, "单价") is { } price && Number(fields, "新数量") is { } quantity ? price * Math.Abs(quantity) : null,
            _ => null,
        };
        return amount is > 0 ? Math.Round(amount.Value, 2, MidpointRounding.AwayFromZero) : null;
    }
}
