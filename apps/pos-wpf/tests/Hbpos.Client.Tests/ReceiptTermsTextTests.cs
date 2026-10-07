using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Tests;

/// <summary>
/// 小票底部条款正文（代金券使用说明 / 分期条款）的取值规则：归一、拆行、回落默认文案、按条款种类套用定制正文。
/// 纯函数，退款券与分期小票两处渲染共用，必须与手持 / iPad / Web 的语义一致。
/// </summary>
public sealed class ReceiptTermsTextTests
{
    private static readonly string[] DefaultLines = ["Default one.", "Default two."];

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n \t \n")]
    public void Normalize_turns_null_empty_and_whitespace_into_null(string? value)
    {
        Assert.Null(ReceiptTermsText.Normalize(value));
    }

    [Fact]
    public void Normalize_trims_outer_whitespace_and_keeps_inner_line_breaks()
    {
        Assert.Equal("a\r\nb", ReceiptTermsText.Normalize("  \r\na\r\nb\t \n"));
    }

    [Fact]
    public void SplitLines_handles_crlf_cr_and_lf_trims_each_line_and_drops_blank_lines()
    {
        var lines = ReceiptTermsText.SplitLines("  First  \r\n\r\nSecond\rThird\n \t \n  Fourth line with  inner spaces ");

        Assert.Equal(["First", "Second", "Third", "Fourth line with  inner spaces"], lines);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \r\n \n\t")]
    public void SplitLines_returns_empty_when_nothing_is_printable(string? value)
    {
        Assert.Empty(ReceiptTermsText.SplitLines(value));
    }

    [Fact]
    public void SplitLines_keeps_a_tab_inside_a_line_as_is()
    {
        Assert.Equal(["a\tb"], ReceiptTermsText.SplitLines("\ta\tb\t"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n\r\n \n")]
    public void ResolveLines_falls_back_to_the_very_same_default_when_not_customized(string? custom)
    {
        Assert.Same(DefaultLines, ReceiptTermsText.ResolveLines(custom, DefaultLines));
    }

    [Fact]
    public void ResolveLines_uses_the_custom_lines_instead_of_the_default_when_present()
    {
        var lines = ReceiptTermsText.ResolveLines("Custom A\n\n  Custom B  ", DefaultLines);

        Assert.Equal(["Custom A", "Custom B"], lines);
    }

    [Fact]
    public void ApplyCustomBody_replaces_only_the_body_of_the_matching_kind_and_keeps_the_title()
    {
        var settings = ReceiptPrinterSettings.Default with
        {
            VoucherTerms = "Voucher custom",
            InstallmentTerms = "Installment custom 1\nInstallment custom 2"
        };

        var voucher = ReceiptTermsText.ApplyCustomBody(VoucherReceiptTerms.RefundVoucher, settings);
        var installment = ReceiptTermsText.ApplyCustomBody(InstallmentReceiptMapper.InstallmentTerms, settings);

        Assert.Equal("VOUCHER TERMS", voucher.Title);
        Assert.Equal(["Voucher custom"], voucher.Lines);
        Assert.Equal("INSTALLMENT TERMS", installment.Title);
        Assert.Equal(["Installment custom 1", "Installment custom 2"], installment.Lines);
    }

    [Fact]
    public void ApplyCustomBody_returns_the_same_default_block_when_not_customized()
    {
        var settings = ReceiptPrinterSettings.Default;

        // 旧快照 / 从未下发 / 全空白：必须原样返回默认块（同一个实例），打印行为与改动前完全一致。
        Assert.Same(VoucherReceiptTerms.RefundVoucher, ReceiptTermsText.ApplyCustomBody(VoucherReceiptTerms.RefundVoucher, settings));
        Assert.Same(
            InstallmentReceiptMapper.InstallmentTerms,
            ReceiptTermsText.ApplyCustomBody(
                InstallmentReceiptMapper.InstallmentTerms,
                settings with { InstallmentTerms = "  \r\n  " }));
    }

    [Fact]
    public void ApplyCustomBody_does_not_cross_apply_the_other_kinds_text()
    {
        var onlyVoucher = ReceiptPrinterSettings.Default with { VoucherTerms = "Voucher custom" };
        var onlyInstallment = ReceiptPrinterSettings.Default with { InstallmentTerms = "Installment custom" };

        Assert.Same(
            InstallmentReceiptMapper.InstallmentTerms,
            ReceiptTermsText.ApplyCustomBody(InstallmentReceiptMapper.InstallmentTerms, onlyVoucher));
        Assert.Same(
            VoucherReceiptTerms.RefundVoucher,
            ReceiptTermsText.ApplyCustomBody(VoucherReceiptTerms.RefundVoucher, onlyInstallment));
    }

    [Fact]
    public void ApplyCustomBody_never_customizes_a_fixed_terms_block()
    {
        var fixedTerms = new ReceiptTerms("FIXED", ["Fixed line."]);
        var settings = ReceiptPrinterSettings.Default with { VoucherTerms = "V", InstallmentTerms = "I" };

        Assert.Same(fixedTerms, ReceiptTermsText.ApplyCustomBody(fixedTerms, settings));
    }

    [Fact]
    public void Default_wording_constants_are_unchanged()
    {
        // 默认文案是业主审定稿，这次只新增可定制的入口，文字一个字都不能动。
        Assert.Equal(
            [
                "Use at the issuing store only.",
                "Pay with it at checkout by scanning the barcode or QR code.",
                "Can be used across several purchases until the balance is $0.00.",
                "Not redeemable for cash."
            ],
            VoucherReceiptTerms.RefundVoucher.Lines);
        Assert.Equal(
            [
                "Order total: $50.00 minimum.",
                "First payment: $20.00 minimum.",
                "Each later payment: $5.00 minimum, or the remaining balance if it is lower."
            ],
            InstallmentReceiptMapper.InstallmentTerms.Lines);
    }

    [Fact]
    public void Length_limit_is_600_and_shared_with_the_profile_validator()
    {
        Assert.Equal(600, ReceiptTermsText.MaxLength);
        Assert.Equal(ReceiptTermsText.MaxLength, StoreReceiptProfileValidator.TermsMaxLength);
    }
}
