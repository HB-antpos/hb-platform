using System.Globalization;
using System.Text.Json;
using BlazorApp.Api.Models.Linkly;
using BlazorApp.Shared.DTOs;

namespace BlazorApp.Api.Services.React;

internal interface ILinklySettlementAmountParser
{
    LinklySettlementAmountParseResult Parse(string? value);
}

internal sealed class LinklySettlementAmountParser : ILinklySettlementAmountParser
{
    private const int CardRecordLength = 69;
    private const string MaskedMarker = "****";
    private const string TotalCardName = "TOTAL";
    // 存量脱敏记录：头部 8 字符（****dddd）+ 每条 65 字符（名称 20 + 6 组金额/笔数 36 + 符号 1 + ****dddd 8）。
    private const int MaskedHeaderLength = 8;
    private const int MaskedRecordLength = 65;

    public LinklySettlementAmountParseResult Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return LinklySettlementAmountParseResult.Missing;

        var first = value.AsSpan().TrimStart()[0];
        if (first is '{' or '[')
            return ParseJson(value);

        // 旧版 WPF 脱敏器把整段定长数据里的 12 位数字串误当卡号，写成了 ****dddd 形态（存量记录）。
        if (value.StartsWith(MaskedMarker, StringComparison.Ordinal))
            return ParseMaskedFixedWidth(value);

        if (value.Length < 12 || !AllDigits(value.AsSpan(0, Math.Min(12, value.Length))))
            return LinklySettlementAmountParseResult.Unsupported;

        return ParseFixedWidth(value);
    }

    private LinklySettlementAmountParseResult ParseJson(string value)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(value);
        }
        catch (JsonException)
        {
            return LinklySettlementAmountParseResult.Invalid;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return LinklySettlementAmountParseResult.Unsupported;

            if (TryFindOfficialProperty(root, "SettlementTotalsData", out var totals))
                return ParseJsonTotals(totals);

            if (TryFindOfficialProperty(root, "SettlementData", out var fixedWidth))
            {
                if (fixedWidth.ValueKind != JsonValueKind.String)
                    return LinklySettlementAmountParseResult.Invalid;
                return Parse(fixedWidth.GetString());
            }

            return LinklySettlementAmountParseResult.Unsupported;
        }
    }

    private static bool TryFindOfficialProperty(
        JsonElement root,
        string name,
        out JsonElement value)
    {
        if (TryGetProperty(root, name, out value))
            return true;

        if (TryGetProperty(root, "Response", out var response)
            && response.ValueKind == JsonValueKind.Object
            && TryGetProperty(response, name, out value))
            return true;

        value = default;
        return false;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static LinklySettlementAmountParseResult ParseJsonTotals(JsonElement totals)
    {
        if (totals.ValueKind != JsonValueKind.Array)
            return LinklySettlementAmountParseResult.Invalid;

        var rows = new List<LinklySettlementCardTotalDto>();
        foreach (var element in totals.EnumerateArray())
        {
            if (!TryParseJsonRecord(element, out var row))
                return LinklySettlementAmountParseResult.Invalid;
            rows.Add(row);
        }

        return BuildResult(rows);
    }

    private static bool TryParseJsonRecord(
        JsonElement element,
        out LinklySettlementCardTotalDto row)
    {
        row = new LinklySettlementCardTotalDto();
        if (element.ValueKind != JsonValueKind.Object
            || !TryGetProperty(element, "CardName", out var cardName)
            || cardName.ValueKind != JsonValueKind.String)
            return false;

        var normalizedName = cardName.GetString()?.Trim();
        if (string.IsNullOrEmpty(normalizedName)
            || !TryGetBoundedUnsigned(element, "PurchaseAmount", 999_999_999, out var purchaseAmount)
            || !TryGetBoundedUnsigned(element, "PurchaseCount", 999, out var purchaseCount)
            || !TryGetBoundedUnsigned(element, "CashOutAmount", 999_999_999, out var cashOutAmount)
            || !TryGetBoundedUnsigned(element, "CashOutCount", 999, out var cashOutCount)
            || !TryGetBoundedUnsigned(element, "RefundAmount", 999_999_999, out var refundAmount)
            || !TryGetBoundedUnsigned(element, "RefundCount", 999, out var refundCount)
            || !TryGetBoundedTotalAmount(element, "TotalAmount", out var totalAmount)
            || !TryGetBoundedUnsigned(element, "TotalCount", 999, out var totalCount))
            return false;

        row = new LinklySettlementCardTotalDto
        {
            CardName = normalizedName,
            PurchaseAmountMinor = purchaseAmount,
            PurchaseCount = purchaseCount,
            CashOutAmountMinor = cashOutAmount,
            CashOutCount = cashOutCount,
            RefundAmountMinor = refundAmount,
            RefundCount = refundCount,
            TotalAmountMinor = totalAmount,
            TotalCount = totalCount,
        };
        return true;
    }

    private static bool TryGetBoundedUnsigned(
        JsonElement element,
        string name,
        long maximum,
        out long value)
    {
        value = 0;
        return TryGetProperty(element, name, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt64(out value)
            && value >= 0
            && value <= maximum;
    }

    private static bool TryGetBoundedTotalAmount(
        JsonElement element,
        string name,
        out long value)
    {
        value = 0;
        return TryGetProperty(element, name, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt64(out value)
            && value is >= -999_999_999 and <= 999_999_999;
    }

    private static LinklySettlementAmountParseResult ParseFixedWidth(string value)
    {
        if (!TryReadUnsigned(value.AsSpan(0, 9), out var cardCountLong)
            || cardCountLong > int.MaxValue
            || !TryReadUnsigned(value.AsSpan(9, 3), out var cardDataLengthLong)
            || cardDataLengthLong > int.MaxValue)
            return LinklySettlementAmountParseResult.Invalid;

        var cardCount = (int)cardCountLong;
        var cardDataLength = (int)cardDataLengthLong;
        if (cardCount > 999_999
            || cardDataLength != cardCount * CardRecordLength
            || value.Length < 12 + cardDataLength + 3)
            return LinklySettlementAmountParseResult.Invalid;

        var offset = 12;
        var rows = new List<LinklySettlementCardTotalDto>(cardCount + 1);
        for (var index = 0; index < cardCount; index++)
        {
            if (!TryParseFixedRecord(value.AsSpan(offset, CardRecordLength), out var row))
                return LinklySettlementAmountParseResult.Invalid;
            rows.Add(row);
            offset += CardRecordLength;
        }

        if (!TryReadUnsigned(value.AsSpan(offset, 3), out var totalsLength)
            || totalsLength != CardRecordLength
            || value.Length < offset + 3 + CardRecordLength)
            return LinklySettlementAmountParseResult.Invalid;

        offset += 3;
        if (!TryParseFixedRecord(value.AsSpan(offset, CardRecordLength), out var total))
            return LinklySettlementAmountParseResult.Invalid;
        rows.Add(total);
        offset += CardRecordLength;

        // 官方响应允许一个可选尾段；有尾段时声明长度必须与剩余字符精确相等。
        if (offset != value.Length)
        {
            if (value.Length - offset < 3
                || !TryReadUnsigned(value.AsSpan(offset, 3), out var tailLength)
                || tailLength != value.Length - offset - 3)
                return LinklySettlementAmountParseResult.Invalid;
        }

        return BuildResult(rows);
    }

    private static bool TryParseFixedRecord(
        ReadOnlySpan<char> record,
        out LinklySettlementCardTotalDto row)
    {
        row = new LinklySettlementCardTotalDto();
        if (record.Length != CardRecordLength)
            return false;

        var name = record[..20].Trim().ToString();
        if (name.Length == 0
            || !TryReadUnsigned(record.Slice(20, 9), out var purchaseAmount)
            || !TryReadUnsigned(record.Slice(29, 3), out var purchaseCount)
            || !TryReadUnsigned(record.Slice(32, 9), out var cashOutAmount)
            || !TryReadUnsigned(record.Slice(41, 3), out var cashOutCount)
            || !TryReadUnsigned(record.Slice(44, 9), out var refundAmount)
            || !TryReadUnsigned(record.Slice(53, 3), out var refundCount)
            || record[56] is not ('+' or '-' or ' ')
            || !TryReadUnsigned(record.Slice(57, 9), out var totalAmount)
            || !TryReadUnsigned(record.Slice(66, 3), out var totalCount))
            return false;

        row = new LinklySettlementCardTotalDto
        {
            CardName = name,
            PurchaseAmountMinor = purchaseAmount,
            PurchaseCount = purchaseCount,
            CashOutAmountMinor = cashOutAmount,
            CashOutCount = cashOutCount,
            RefundAmountMinor = refundAmount,
            RefundCount = refundCount,
            TotalAmountMinor = record[56] == '-' ? -totalAmount : totalAmount,
            TotalCount = totalCount,
        };
        return true;
    }

    private static LinklySettlementAmountParseResult BuildResult(
        IReadOnlyList<LinklySettlementCardTotalDto> rows)
    {
        var totals = rows
            .Where(row => IsTotalName(row.CardName))
            .ToList();
        if (totals.Count != 1)
            return LinklySettlementAmountParseResult.Invalid;

        var total = totals[0];
        return new LinklySettlementAmountParseResult
        {
            Status = LinklySettlementAmountParseStatus.Parsed,
            Summary = new LinklySettlementAmountDto
            {
                CurrencyCode = "AUD",
                PurchaseAmountMinor = total.PurchaseAmountMinor,
                PurchaseCount = total.PurchaseCount,
                CashOutAmountMinor = total.CashOutAmountMinor,
                CashOutCount = total.CashOutCount,
                RefundAmountMinor = total.RefundAmountMinor,
                RefundCount = total.RefundCount,
                TotalAmountMinor = total.TotalAmountMinor,
                TotalCount = total.TotalCount,
            },
            CardTotals = rows
                .Where(row => !IsTotalName(row.CardName))
                .ToList(),
        };
    }

    // 真实终端的汇总行名称是 TOTALS（官方文档写作 TOTAL），两者都按汇总行处理。
    private static bool IsTotalName(string name) =>
        name.Equals(TotalCardName, StringComparison.OrdinalIgnoreCase)
        || name.Equals(TotalCardName + "S", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 解析被旧版脱敏器破坏的存量数据。每条记录的购买/Cash Out/退款金额与笔数完整保留，
    /// 只有「合计金额+合计笔数」12 位被替换成 ****dddd；这里用保留字段重算合计，
    /// 并用残留的末 4 位（合计金额末位 + 3 位笔数）校验，任何不一致都按无效处理，绝不猜测金额。
    /// </summary>
    private static LinklySettlementAmountParseResult ParseMaskedFixedWidth(string value)
    {
        var body = value.Length - MaskedHeaderLength;
        if (body <= 0 || body % MaskedRecordLength != 0)
            return LinklySettlementAmountParseResult.Unsupported;

        var recordCount = body / MaskedRecordLength;
        var cardCount = recordCount - 1;
        // 头部是 9 位卡种数 + 3 位数据长度的末 4 位：卡种数个位 + 3 位（卡种数 × 69）。
        if (!IsMaskedTail(value.AsSpan(0, MaskedHeaderLength), out var headerDigits)
            || headerDigits != cardCount % 10 * 1000 + cardCount * CardRecordLength)
            return LinklySettlementAmountParseResult.Unsupported;

        var rows = new List<LinklySettlementCardTotalDto>(recordCount);
        for (var index = 0; index < recordCount; index++)
        {
            var record = value.AsSpan(MaskedHeaderLength + index * MaskedRecordLength, MaskedRecordLength);
            // 汇总行前面还有 3 位长度「069」，它与上一条记录的合计 12 位连成 15 位数字被一起掩码。
            var followedByTotalLength = index == recordCount - 2;
            if (!TryParseMaskedRecord(record, followedByTotalLength, out var row))
                return LinklySettlementAmountParseResult.Invalid;
            rows.Add(row);
        }

        return BuildResult(rows);
    }

    private static bool TryParseMaskedRecord(
        ReadOnlySpan<char> record,
        bool followedByTotalLength,
        out LinklySettlementCardTotalDto row)
    {
        row = new LinklySettlementCardTotalDto();
        var name = record[..20].Trim().ToString();
        if (name.Length == 0
            || !TryReadUnsigned(record.Slice(20, 9), out var purchaseAmount)
            || !TryReadUnsigned(record.Slice(29, 3), out var purchaseCount)
            || !TryReadUnsigned(record.Slice(32, 9), out var cashOutAmount)
            || !TryReadUnsigned(record.Slice(41, 3), out var cashOutCount)
            || !TryReadUnsigned(record.Slice(44, 9), out var refundAmount)
            || !TryReadUnsigned(record.Slice(53, 3), out var refundCount)
            || record[56] is not ('+' or '-' or ' ')
            || !IsMaskedTail(record.Slice(57, MaskedHeaderLength), out var tailDigits))
            return false;

        // 合计 = 购买 + Cash Out - 退款；笔数 = 三类笔数之和。
        var net = purchaseAmount + cashOutAmount - refundAmount;
        var totalAmount = net;
        var totalCount = purchaseCount + cashOutCount + refundCount;
        // 非负合计不应带负号，负合计必须带负号，否则说明数据不是预期形态。
        if ((net < 0) != (record[56] == '-') && net != 0)
            return false;
        // 常规记录残留「金额末位 + 3 位笔数」；汇总行之前的那条残留「笔数末位 + 069」。
        var expectedTail = followedByTotalLength
            ? totalCount % 10 * 1000 + CardRecordLength
            : Math.Abs(net) % 10 * 1000 + totalCount;
        if (tailDigits != expectedTail)
            return false;

        row = new LinklySettlementCardTotalDto
        {
            CardName = name,
            PurchaseAmountMinor = purchaseAmount,
            PurchaseCount = purchaseCount,
            CashOutAmountMinor = cashOutAmount,
            CashOutCount = cashOutCount,
            RefundAmountMinor = refundAmount,
            RefundCount = refundCount,
            TotalAmountMinor = totalAmount,
            TotalCount = totalCount,
        };
        return true;
    }

    // 读取 ****dddd 形态的 8 字符片段，返回末 4 位数字。
    private static bool IsMaskedTail(ReadOnlySpan<char> segment, out long digits)
    {
        digits = 0;
        return segment.Length == MaskedHeaderLength
            && segment[..4].SequenceEqual(MaskedMarker)
            && TryReadUnsigned(segment[4..], out digits);
    }

    private static bool TryReadUnsigned(ReadOnlySpan<char> value, out long parsed) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out parsed);

    private static bool AllDigits(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (character is < '0' or > '9')
                return false;
        }

        return true;
    }
}
