using System.Text.Json;

namespace BlazorApp.Api.Services.React;

/// <summary>
/// 读取已同步结算记录里的回单文本。后台详情按用户要求展示存库原文，不再做展示层脱敏。
/// </summary>
internal static class LinklySettlementReceiptReader
{
    public static string[] ParseReceipts(string? receiptTextsJson) => Parse(receiptTextsJson);

    public static int Count(string? receiptTextsJson) => Parse(receiptTextsJson).Length;

    private static string[] Parse(string? receiptTextsJson)
    {
        if (string.IsNullOrWhiteSpace(receiptTextsJson))
            return [];

        try
        {
            using var document = JsonDocument.Parse(receiptTextsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return [];

            var values = new List<string>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.String)
                    return [];
                values.Add(element.GetString() ?? string.Empty);
            }

            return values.ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
