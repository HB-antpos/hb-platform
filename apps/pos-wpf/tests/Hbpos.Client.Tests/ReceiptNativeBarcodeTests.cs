using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Tests;

/// <summary>原生 Code 128 条码数据：{B 前缀、花括号转义、放不下 / 不能编码的内容交给降级。</summary>
public sealed class ReceiptNativeBarcodeTests
{
    [Fact]
    public void Code128_type_is_73_not_the_silent_type_8()
    {
        Assert.Equal(73, ReceiptNativeBarcode.Code128BarcodeType);
    }

    [Theory]
    [InlineData("9762C035", 3, "{B9762C035")]
    [InlineData("9762C035", 2, "{B9762C035")]
    [InlineData("AB{C-12/x", 2, "{BAB{{C-12/x")]
    [InlineData("{", 2, "{B{{")]
    public void Adds_the_subset_b_prefix_and_escapes_braces(string value, int moduleWidth, string expected)
    {
        Assert.True(ReceiptNativeBarcode.TryCreateCode128Data(value, moduleWidth, out var data));
        Assert.Equal(expected, data);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("订单12345")]
    [InlineData("tab\there")]
    public void Rejects_empty_or_non_printable_ascii(string? value)
    {
        Assert.False(ReceiptNativeBarcode.TryCreateCode128Data(value, 2, out var data));
        Assert.Equal(string.Empty, data);
    }

    [Fact]
    public void Rejects_a_non_positive_module_width()
    {
        Assert.False(ReceiptNativeBarcode.TryCreateCode128Data("9762C035", 0, out _));
    }

    [Fact]
    public void Full_guid_does_not_fit_the_80mm_paper_so_it_falls_back_instead_of_silently_printing_nothing()
    {
        // 2026-10-31 在 XP-80C 上实测：36 位 GUID 在 2 点模块宽下整条丢弃，SDK 仍返回 0。
        Assert.False(ReceiptNativeBarcode.TryCreateCode128Data("2336d69e-7a1c-4f3e-9a21-9762c035aabb", 2, out _));
    }

    [Theory]
    [InlineData(21, 2, true)]   // (11*21+35+20)*2 = 572 ≤ 576
    [InlineData(22, 2, false)]  // (11*22+35+20)*2 = 594 > 576
    [InlineData(12, 3, true)]   // (11*12+35+20)*3 = 561 ≤ 576
    [InlineData(13, 3, false)]  // (11*13+35+20)*3 = 594 > 576
    public void Width_check_matches_the_paper_limit(int length, int moduleWidth, bool fits)
    {
        Assert.Equal(fits, ReceiptNativeBarcode.TryCreateCode128Data(new string('7', length), moduleWidth, out _));
    }

    [Fact]
    public void Order_short_code_fits_at_the_receipt_size_of_3_dots_and_vouchers_fit_at_2()
    {
        Assert.True(ReceiptNativeBarcode.TryCreateCode128Data("9762C035", 3, out _));
        Assert.True(ReceiptNativeBarcode.TryCreateCode128Data(new string('A', 16), 2, out _));
    }
}
