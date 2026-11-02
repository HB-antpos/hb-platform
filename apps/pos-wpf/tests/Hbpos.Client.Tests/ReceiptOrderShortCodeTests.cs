using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Tests;

public sealed class ReceiptOrderShortCodeTests
{
    [Theory]
    [InlineData("7e4c91d2-0b5a-4f3e-9a21-5c8d6e0f13ab", "6E0F13AB")]
    // 全是数字时没有大小写问题，取的是「末」8 位而不是「前」8 位。
    [InlineData("12345678-1234-1234-1234-123456789012", "56789012")]
    [InlineData("00000000-0000-0000-0000-000000000001", "00000001")]
    public void Create_takes_last_eight_hex_digits_uppercase(string guid, string expected)
    {
        Assert.Equal(expected, ReceiptOrderShortCode.Create(Guid.Parse(guid)));
    }

    [Theory]
    [InlineData("6E0F13AB", "6E0F13AB")]
    [InlineData("6e0f13ab", "6E0F13AB")]
    [InlineData("  6e0f13AB \r\n", "6E0F13AB")]
    [InlineData("00000001", "00000001")]
    public void TryNormalize_accepts_exactly_eight_hex_digits(string input, string expected)
    {
        Assert.True(ReceiptOrderShortCode.TryNormalize(input, out var code));
        Assert.Equal(expected, code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("6E0F13A")]         // 7 位
    [InlineData("6E0F13ABC")]       // 9 位
    [InlineData("6E0F13AG")]        // 含非十六进制字符
    [InlineData("6E0F-13AB")]       // 带横杠
    [InlineData("７E0F13AB")]       // 全角数字：char.IsDigit 会放过，必须拒绝
    [InlineData("7e4c91d2-0b5a-4f3e-9a21-5c8d6e0f13ab")] // 完整 GUID 不是短码
    public void TryNormalize_rejects_everything_else(string? input)
    {
        Assert.False(ReceiptOrderShortCode.TryNormalize(input, out var code));
        Assert.Equal(string.Empty, code);
    }

    [Fact]
    public void Matches_compares_against_last_eight_digits_only()
    {
        var guid = Guid.Parse("7e4c91d2-0b5a-4f3e-9a21-5c8d6e0f13ab");

        Assert.True(ReceiptOrderShortCode.Matches(guid, "6E0F13AB"));
        Assert.True(ReceiptOrderShortCode.Matches(guid, "6e0f13ab"));
        // GUID 开头的 8 位不算命中：旧的 ToString("N")[..8] 口径已废弃。
        Assert.False(ReceiptOrderShortCode.Matches(guid, "7E4C91D2"));
        Assert.False(ReceiptOrderShortCode.Matches(guid, "not-a-code"));
    }
}
