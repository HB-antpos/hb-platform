using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Hbpos.Client.Wpf.Services;

/// <summary>
/// 本地 Linkly（LocalIp）刷卡过程中收到的顾客/商户小票里，能证明“本笔已批准”的字段。
/// 生产实测（2026-10-22 1002 店）：刷卡机先打出带 APPROVED 00 的小票，随后最终响应却回 PF「PINpad Offline」、
/// TxnRef 为空、金额 0，GetLastTransaction 又回 JH「NOT FOUND」，导致已扣款的交易落成结果未知。
/// 这里只做纯解析与序列化，不接触数据库或终端；是否据此建单由调用方决定。
/// </summary>
public sealed record LinklyLocalReceiptApprovalEvidence(
    string TxnRef,
    string Rrn,
    decimal Amount,
    string ResponseCode,
    string? ApprovalCode,
    string? Stan,
    string? Invoice)
{
    // ANZ 小票上的 RRN 就是本笔 TxnRef 的前 12 位（例：TxnRef P29R5YNFN4XMNN50 → RRN P29R5YNFN4XM）。
    internal const int RrnLength = 12;

    private const string ApprovedResponseCode = "00";

    private static readonly Regex ApprovedLine = new(@"^\s*APPROVED\s+00\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex RrnLine = new(@"^\s*RRN\s*:?\s*([A-Z0-9]+)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SaleLine = new(@"^\s*(?:SALE|PURCHASE)\s+AUD\s+\$?\s*([0-9][0-9,]*\.[0-9]{2})\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ApprovalCodeLine = new(@"^\s*(?:APPROVAL\s+CODE|AUTH(?:ORI[SZ]ATION)?\s+CODE)\s*:?\s*([A-Z0-9]+)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex StanLine = new(@"^\s*STAN\s*:?\s*([0-9]+)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex InvoiceLine = new(@"^\s*INV(?:OICE)?\s*:?\s*([0-9]+)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // 小票里出现这些字样说明不是一笔干净的批准（拒绝、取消、冲正、撤销），一律不作为批准证据。
    private static readonly string[] NegativeMarkers =
    [
        "DECLINED",
        "CANCELLED",
        "CANCELED",
        "REVERSAL",
        "REVERSED",
        "VOID"
    ];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// 小票上能直接对照的短参考号，供恢复中心“确认已付款”自动预填：RRN + 批准码。
    /// </summary>
    public string DisplayReference => ApprovalCode is null
        ? $"RRN {Rrn}"
        : $"RRN {Rrn} / APPROVAL CODE {ApprovalCode}";

    /// <summary>
    /// 只在同一张小票同时满足以下条件时才认定为本笔的批准证据：
    /// 含 <c>APPROVED 00</c>；RRN 与本笔 TxnRef 前 12 位一致；<c>SALE AUD $金额</c> 与请求金额一致；
    /// 且所有小票都没有拒绝/取消/冲正字样。任一条件不满足都返回 false，交易仍按原规则走身份核对。
    /// </summary>
    public static bool TryParse(
        IEnumerable<string>? receipts,
        string? txnRef,
        decimal amount,
        out LinklyLocalReceiptApprovalEvidence? evidence)
    {
        evidence = null;
        var normalizedTxnRef = txnRef?.Trim();
        if (receipts is null ||
            string.IsNullOrEmpty(normalizedTxnRef) ||
            normalizedTxnRef.Length < RrnLength ||
            amount <= 0m)
        {
            return false;
        }

        var receiptBlocks = receipts.Where(receipt => !string.IsNullOrWhiteSpace(receipt)).ToArray();
        if (receiptBlocks.Any(ContainsNegativeMarker))
        {
            return false;
        }

        var expectedRrn = normalizedTxnRef[..RrnLength];
        foreach (var receipt in receiptBlocks)
        {
            if (TryParseReceipt(receipt, normalizedTxnRef, expectedRrn, amount, out evidence))
            {
                return true;
            }
        }

        evidence = null;
        return false;
    }

    /// <summary>
    /// 校验已落库的证据仍属于这笔 attempt（TxnRef、金额一致），防止串单。
    /// </summary>
    public bool Matches(string? txnRef, decimal amount)
    {
        var normalizedTxnRef = txnRef?.Trim();
        return !string.IsNullOrEmpty(normalizedTxnRef) &&
            normalizedTxnRef.Length >= RrnLength &&
            string.Equals(TxnRef, normalizedTxnRef, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Rrn, normalizedTxnRef[..RrnLength], StringComparison.OrdinalIgnoreCase) &&
            string.Equals(ResponseCode, ApprovedResponseCode, StringComparison.Ordinal) &&
            Amount == decimal.Round(amount, 2, MidpointRounding.AwayFromZero);
    }

    public string Serialize() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>
    /// 读取落库的证据；内容损坏或与本笔不匹配时返回 null（等同没有证据）。
    /// </summary>
    public static LinklyLocalReceiptApprovalEvidence? TryDeserializeFor(string? json, string? txnRef, decimal amount)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var evidence = JsonSerializer.Deserialize<LinklyLocalReceiptApprovalEvidence>(json, JsonOptions);
            return evidence is not null && evidence.Matches(txnRef, amount) ? evidence : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryParseReceipt(
        string receipt,
        string txnRef,
        string expectedRrn,
        decimal amount,
        out LinklyLocalReceiptApprovalEvidence? evidence)
    {
        evidence = null;
        var approved = false;
        string? rrn = null;
        decimal? saleAmount = null;
        string? approvalCode = null;
        string? stan = null;
        string? invoice = null;
        foreach (var line in receipt.Split(["\r\n", "\n", "\r"], StringSplitOptions.None))
        {
            approved |= ApprovedLine.IsMatch(line);
            rrn ??= MatchGroup(RrnLine, line);
            approvalCode ??= MatchGroup(ApprovalCodeLine, line);
            stan ??= MatchGroup(StanLine, line);
            invoice ??= MatchGroup(InvoiceLine, line);
            if (saleAmount is null &&
                MatchGroup(SaleLine, line) is { } amountText &&
                decimal.TryParse(
                    amountText.Replace(",", string.Empty, StringComparison.Ordinal),
                    NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture,
                    out var parsedAmount))
            {
                saleAmount = parsedAmount;
            }
        }

        if (!approved ||
            rrn is null ||
            !string.Equals(rrn, expectedRrn, StringComparison.OrdinalIgnoreCase) ||
            saleAmount != decimal.Round(amount, 2, MidpointRounding.AwayFromZero))
        {
            return false;
        }

        evidence = new LinklyLocalReceiptApprovalEvidence(
            txnRef,
            rrn.ToUpperInvariant(),
            saleAmount.Value,
            ApprovedResponseCode,
            approvalCode,
            stan,
            invoice);
        return true;
    }

    private static bool ContainsNegativeMarker(string receipt) =>
        NegativeMarkers.Any(marker => receipt.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static string? MatchGroup(Regex regex, string line)
    {
        var match = regex.Match(line);
        return match.Success ? match.Groups[1].Value : null;
    }
}
