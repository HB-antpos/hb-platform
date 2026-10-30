using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Tests;

public sealed class ReceiptCode128Tests
{
    // 基准值由 packages/pos-receipt-core 的 receiptCode128（TS 原版）对同一输入算出，C# 移植必须逐位一致。
    [Theory]
    [InlineData("6E0F13AB", 123)]
    [InlineData("7e4c91d2-0b5a-4f3e-9a21-5c8d6e0f13ab", 431)]
    [InlineData("123456789012345678901234567890123456789", 266)]
    [InlineData("VC200", 90)]
    [InlineData("0123456789", 90)]
    [InlineData("A1", 57)]
    public void Encode_module_count_matches_the_typescript_reference(string value, int expectedModules)
    {
        var encoding = ReceiptCode128.Encode(value);

        Assert.Equal(expectedModules, encoding.ModuleCount);
        Assert.Equal(expectedModules, encoding.Runs.Sum(run => run.Modules));
    }

    [Theory]
    // 全 B 集合（8 位单号中间的 "13" 压成 C 集合反而更长）。
    [InlineData("6E0F13AB", "2112142231121321131231221323111232212211321113231311231224112331112")]
    // 纯数字走起始符 C，两位一码字。
    [InlineData("0123456789", "2112322221223121311131231411222121411421122331112")]
    // 数字对 "12" 与 "34" 夹着字母：B 集合内逐字符编码，不插切换。
    [InlineData("12ab34", "2112141232212232111211241214212211322212311223122331112")]
    public void Encode_run_widths_match_the_typescript_reference(string value, string expectedWidths)
    {
        var widths = string.Concat(ReceiptCode128.Encode(value).Runs.Select(run => run.Modules));

        Assert.Equal(expectedWidths, widths);
    }

    [Fact]
    public void Encode_alternates_bar_and_space_starting_with_a_bar()
    {
        var runs = ReceiptCode128.Encode("6E0F13AB").Runs;

        for (var i = 0; i < runs.Count; i++)
        {
            Assert.Equal(i % 2 == 0, runs[i].IsBar);
        }

        // 每个码字 6 段、终止符 7 段：8 个字符 + 起始符 + 校验符 = 10 个码字，再加终止符。
        Assert.Equal(10 * 6 + 7, runs.Count);
        Assert.True(runs[^1].IsBar, "终止符以条结尾");
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("订单", false)]
    [InlineData("tab\there", false)]
    [InlineData("6E0F13AB", true)]
    [InlineData(" ~", true)]
    public void CanEncode_only_accepts_non_empty_printable_ascii(string? value, bool expected)
    {
        Assert.Equal(expected, ReceiptCode128.CanEncode(value));
    }

    [Fact]
    public void Encode_rejects_unencodable_input()
    {
        Assert.Throws<ArgumentException>(() => ReceiptCode128.Encode(string.Empty));
        Assert.Throws<ArgumentException>(() => ReceiptCode128.Encode("订单"));
    }
}
