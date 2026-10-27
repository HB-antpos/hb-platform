using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Tests;

/// <summary>
/// 本地 Linkly 小票批准证据解析：只有同一张小票含 APPROVED 00、RRN 等于本笔 TxnRef 前 12 位、
/// SALE AUD 金额与请求一致，且没有拒绝/取消/冲正字样，才算本笔的批准证据。
/// 样例按 2026-10-22 1002 店生产小票的版式构造（卡号、商户号等为虚构值）。
/// </summary>
public sealed class LinklyLocalReceiptApprovalEvidenceTests
{
    internal const string TxnRef = "P29R5YNFN4XMNN50";

    internal static string ApprovedReceipt(string rrn = "P29R5YNFN4XM", string amount = "$13.99", string result = "APPROVED            00  ") =>
        string.Join(
            Environment.NewLine,
            "------------------------",
            "HOT BARGAIN TEST STORE  ",
            "MID: 0                  ",
            "TID: 00000000000        ",
            "22/10/26           18:50",
            "BATCH:            000809",
            "INV:               69615",
            "STAN:             147950",
            "ACCOUNT TYPE      CREDIT",
            $"RRN:        {rrn}",
            "VISA                    ",
            "............1234(t)     ",
            $"SALE    AUD     {amount}  ",
            "------------------------",
            result,
            "APPROVAL CODE     267836",
            "     CUSTOMER COPY      ",
            "------------------------");

    [Fact]
    public void Production_shaped_approved_receipt_yields_evidence()
    {
        // 生产实测的第一张小票是只有一个空行的回单，必须被忽略而不是让解析失败。
        var receipts = new[] { string.Empty, ApprovedReceipt() };

        Assert.True(LinklyLocalReceiptApprovalEvidence.TryParse(receipts, TxnRef, 13.99m, out var evidence));

        Assert.NotNull(evidence);
        Assert.Equal(TxnRef, evidence.TxnRef);
        Assert.Equal("P29R5YNFN4XM", evidence.Rrn);
        Assert.Equal(13.99m, evidence.Amount);
        Assert.Equal("00", evidence.ResponseCode);
        Assert.Equal("267836", evidence.ApprovalCode);
        Assert.Equal("147950", evidence.Stan);
        Assert.Equal("69615", evidence.Invoice);
        Assert.Equal("RRN P29R5YNFN4XM / APPROVAL CODE 267836", evidence.DisplayReference);
    }

    [Theory]
    // RRN 不是本笔 TxnRef 的前缀：可能是上一笔的小票。
    [InlineData("P29R5YNFN4XX", "$13.99", "APPROVED            00  ")]
    // 金额对不上。
    [InlineData("P29R5YNFN4XM", "$13.90", "APPROVED            00  ")]
    // 不是 APPROVED 00。
    [InlineData("P29R5YNFN4XM", "$13.99", "DECLINED            51  ")]
    [InlineData("P29R5YNFN4XM", "$13.99", "APPROVED            08  ")]
    public void Mismatched_or_unapproved_receipt_yields_no_evidence(string rrn, string amount, string result)
    {
        var receipts = new[] { ApprovedReceipt(rrn, amount, result) };

        Assert.False(LinklyLocalReceiptApprovalEvidence.TryParse(receipts, TxnRef, 13.99m, out var evidence));
        Assert.Null(evidence);
    }

    [Fact]
    public void Any_reversal_or_cancel_marker_rejects_the_evidence()
    {
        var receipts = new[] { ApprovedReceipt(), "TRANSACTION CANCELLED" };

        Assert.False(LinklyLocalReceiptApprovalEvidence.TryParse(receipts, TxnRef, 13.99m, out _));
    }

    [Fact]
    public void No_receipt_yields_no_evidence()
    {
        Assert.False(LinklyLocalReceiptApprovalEvidence.TryParse([], TxnRef, 13.99m, out _));
        Assert.False(LinklyLocalReceiptApprovalEvidence.TryParse(null, TxnRef, 13.99m, out _));
        Assert.False(LinklyLocalReceiptApprovalEvidence.TryParse([ApprovedReceipt()], "P29R5", 13.99m, out _));
    }

    [Fact]
    public void Persisted_evidence_round_trips_only_for_the_same_attempt()
    {
        Assert.True(LinklyLocalReceiptApprovalEvidence.TryParse([ApprovedReceipt()], TxnRef, 13.99m, out var evidence));
        var json = evidence!.Serialize();

        Assert.Equal(evidence, LinklyLocalReceiptApprovalEvidence.TryDeserializeFor(json, TxnRef, 13.99m));
        Assert.Null(LinklyLocalReceiptApprovalEvidence.TryDeserializeFor(json, "P29R5YNFN4XMNN51", 13.99m));
        Assert.Null(LinklyLocalReceiptApprovalEvidence.TryDeserializeFor(json, TxnRef, 10m));
        Assert.Null(LinklyLocalReceiptApprovalEvidence.TryDeserializeFor("{not json", TxnRef, 13.99m));
        Assert.Null(LinklyLocalReceiptApprovalEvidence.TryDeserializeFor(null, TxnRef, 13.99m));
    }
}
