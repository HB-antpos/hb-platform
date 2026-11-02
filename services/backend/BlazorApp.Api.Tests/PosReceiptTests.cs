using BlazorApp.Api.Features.PosReceipts;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 在线小票的纯逻辑：服务端按收银机 42 列规则排版、分享令牌、公开基址与有效期配置。
/// 排版断言用固定数据逐行锁住，改动任何一处版式都会在这里红。
/// </summary>
public sealed class PosReceiptTests
{
    private const string OrderGuid = "7e4c91d2-0b5a-4f3e-9a21-5c8d6e0f13ab";
    private const string ShareUrl = "https://hotbargain.vip/r/7K2M9QXD4TBH";

    private static PosReceiptSource Sale(Action<List<PosReceiptPaymentSource>>? tweakPayments = null)
    {
        var payments = new List<PosReceiptPaymentSource>
        {
            new(2, 20.00m, "VISA ****4321"),
            new(1, 3.85m, null),
        };
        tweakPayments?.Invoke(payments);
        return new PosReceiptSource(
            new PosReceiptStoreProfile(
                "1004", "HotBargain", "Bankstown", "Shop 12, 34 Rickard Rd Bankstown NSW 2200",
                "(02) 9000 0000", "12 345 678 901",
                "Refunds or exchanges within 14 days with receipt and unopened packaging. Clearance items are not refundable."),
            OrderGuid,
            new DateTime(2026, 10, 30, 14, 32, 8),
            "Amy L",
            "POS-04",
            24.75m,
            0.90m,
            [
                new("Kids Watercolour Paint Set 12pc", "HB196-022", 1, 3.50m, 0m),
                new("Red Plastic Forks 50pk", "HB210-105", 3, 8.85m, 0.90m),
                new("Gift Wrap Roll Assorted 5m", "6900123456", 2, 5.00m, 0m),
                new("Scotch Magic Tape 19mm x 33m Value 3 Pack", "HB305-018", 1, 6.50m, 0m),
            ],
            payments,
            ["PURCHASE                         AUD 20.00\nVISA CREDIT                 4111111111114321\nRRN 612345678901"]);
    }

    private static string[] TextOf(PosReceiptDocumentDto document) =>
        document.Lines.Select(line => line.Kind switch
        {
            "blank" => "",
            "barcode" => $"[barcode {line.Text}]",
            "qr" => $"[qr {line.Text}]",
            _ => line.Text,
        }).ToArray();

    [Fact]
    public void 小票逐行与收银机版式一致_单号条码取代Order行_底部只留二维码()
    {
        var document = PosReceiptBuilder.Build(Sale(), ShareUrl, includeBankReceipt: true);

        Assert.Equal("6E0F13AB", document.ShortCode);
        Assert.Equal("paid", document.StatusKind);
        Assert.Equal(
            [
                "HotBargain",
                "Bankstown",
                "Shop 12, 34 Rickard Rd Bankstown",
                "NSW 2200",
                "Tel: (02) 9000 0000",
                "ABN: 12 345 678 901",
                "",
                "===== TAX INVOICE =====",
                "",
                "*** Paid ***",
                "",
                "[barcode 6E0F13AB]",
                "6E0F13AB",
                "Date: 2026-10-30 14:32:08",
                "Cashier: Amy L",
                "Store: Bankstown (1004)",
                "Device: POS-04",
                "------------------------------------------",
                "ITEM                       QTY       PRICE",
                "------------------------------------------",
                "Kids Watercolour Paint Set 12pc",
                "HB196-022                    1       $3.50",
                "Red Plastic Forks 50pk",
                "HB210-105                    3       $8.85",
                "Dis                                 -$0.90",
                "Gift Wrap Roll Assorted 5m",
                "6900123456                   2       $5.00",
                "Scotch Magic Tape 19mm x 33m Value 3 Pack",
                "HB305-018                    1       $6.50",
                "------------------------------------------",
                "Subtotal                            $24.75",
                "Discount                            -$0.90",
                "GST                                  $2.17",
                "Total(inc GST)                      $23.85",
                "------------------------------------------",
                "Payment:",
                "Card                                $20.00",
                "  VISA ****4321",
                "Cash                                 $3.85",
                "------------------------------------------",
                "PURCHASE                         AUD 20.00",
                "VISA CREDIT                 ****4321",
                "RRN 612345678901",
                "",
                "------------------------------------------",
                "Refunds and returns",
                "Refunds or exchanges within 14 days with",
                "receipt and unopened packaging. Clearance",
                "items are not refundable.",
                "------------------------------------------",
                "[qr https://hotbargain.vip/r/7K2M9QXD4TBH]",
                "Scan to view this receipt online",
                "Print Time: 2026-10-30 14:32:08",
                "Device: POS-04",
                "",
                "Thank you for your purchase!",
                "",
            ],
            TextOf(document));

        // 纸面不出现完整 GUID：单号只用末 8 位。
        Assert.DoesNotContain(document.Lines, line => line.Text.Contains(OrderGuid, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void 每个文本行不超过42列_居中与加粗标记正确()
    {
        var document = PosReceiptBuilder.Build(Sale(), ShareUrl, includeBankReceipt: true);

        Assert.All(
            document.Lines.Where(line => line.Kind is "text" or "separator"),
            line => Assert.True(line.Text.Length <= PosReceiptBuilder.LineWidth, line.Text));

        var brand = document.Lines[0];
        Assert.True(brand.Bold);
        Assert.Equal("center", brand.Align);
        var total = document.Lines.Single(line => line.Text.StartsWith("Total(inc GST)", StringComparison.Ordinal));
        Assert.True(total.Bold);
        Assert.Equal("left", total.Align);
        Assert.Equal("center", document.Lines.Single(line => line.Text == "*** Paid ***").Align);
    }

    [Fact]
    public void 公开小票不含银行回单_员工小票含回单且整串卡号只留末四位()
    {
        var staff = TextOf(PosReceiptBuilder.Build(Sale(), ShareUrl, includeBankReceipt: true));
        var publicView = TextOf(PosReceiptBuilder.Build(Sale(), ShareUrl, includeBankReceipt: false));

        Assert.Contains("VISA CREDIT                 ****4321", staff);
        Assert.DoesNotContain(staff, line => line.Contains("4111111111114321", StringComparison.Ordinal));
        // 交易参考号行原样保留，便于对账。
        Assert.Contains("RRN 612345678901", staff);

        Assert.DoesNotContain(publicView, line => line.Contains("PURCHASE", StringComparison.Ordinal));
        Assert.DoesNotContain(publicView, line => line.Contains("RRN", StringComparison.Ordinal));
        Assert.DoesNotContain(publicView, line => line.Contains("VISA CREDIT", StringComparison.Ordinal));
        // 卡摘要（类型 + 末四位）属于付款行，两种视图都有。
        Assert.Contains("  VISA ****4321", publicView);
    }

    [Fact]
    public void 净额为负是退款单_状态与GST按远离零舍入_负数写成_美元符号加负号()
    {
        var source = Sale() with
        {
            TotalAmount = -2.95m,
            DiscountAmount = 0m,
            Lines = [new("Red Plastic Forks 50pk", "HB210-105", -1, -2.95m, 0m)],
            Payments = [new(2, -2.95m, "VISA ****4321")],
            BankReceiptTexts = [],
        };

        var document = PosReceiptBuilder.Build(source, ShareUrl, includeBankReceipt: true);
        var text = TextOf(document);

        Assert.Equal("refund", document.StatusKind);
        Assert.Contains("*** Refund ***", text);
        // 标题与收银机一致，退款单也是 TAX INVOICE，只有状态行变化。
        Assert.Contains("===== TAX INVOICE =====", text);
        Assert.Contains("HB210-105                   -1      $-2.95", text);
        Assert.Contains("GST                                 $-0.27", text);
        Assert.Contains("Total(inc GST)                      $-2.95", text);
        Assert.Contains("Card                                $-2.95", text);
    }

    [Fact]
    public void 没有分享链接时不出二维码_没有退货政策和银行回单时对应区块整体省略()
    {
        var source = Sale() with
        {
            Store = new PosReceiptStoreProfile("1004", "", "Bankstown", "", "", "", ""),
            BankReceiptTexts = [],
        };

        var text = TextOf(PosReceiptBuilder.Build(source, shareUrl: null, includeBankReceipt: true));

        Assert.DoesNotContain(text, line => line.StartsWith("[qr", StringComparison.Ordinal));
        Assert.DoesNotContain("Scan to view this receipt online", text);
        Assert.DoesNotContain("Refunds and returns", text);
        // 品牌名为空时回退到店名，且店名与品牌相同不重复印。
        Assert.Equal("Bankstown", text[0]);
        Assert.Equal("", text[1]);
        Assert.DoesNotContain("Tel:", string.Join('\n', text));
        Assert.DoesNotContain("ABN:", string.Join('\n', text));
    }

    [Theory]
    [InlineData("7e4c91d2-0b5a-4f3e-9a21-5c8d6e0f13ab", "6E0F13AB")]
    [InlineData("7E4C91D20B5A4F3E9A215C8D6E0F13AB", "6E0F13AB")]
    [InlineData("abc", "ABC")]
    public void 单号短码是GUID去横杠后末8位大写(string input, string expected)
    {
        Assert.Equal(expected, PosReceiptBuilder.CreateShortCode(input));
    }

    [Theory]
    [InlineData("VISA", "4111111111114321", "VISA ****4321")]
    [InlineData(null, "4111111111114321", "****4321")]
    [InlineData("VISA", null, "VISA")]
    [InlineData("VISA", "12", "VISA")]
    [InlineData(null, null, null)]
    public void 卡摘要只含类型与末四位(string? type, string? number, string? expected)
    {
        Assert.Equal(expected, PosReceiptBuilder.BuildCardSummary(type, number));
    }

    [Fact]
    public void 分享令牌_12位Crockford字母表_哈希稳定_规范化拒绝非法输入()
    {
        var tokens = Enumerable.Range(0, 200).Select(_ => PosReceiptShareTokens.Generate()).ToList();

        Assert.All(tokens, token =>
        {
            Assert.Equal(PosReceiptShareTokens.Length, token.Length);
            Assert.True(PosReceiptShareTokens.TryNormalize(token, out var normalized));
            Assert.Equal(token, normalized);
        });
        Assert.Equal(tokens.Count, tokens.Distinct().Count());

        var hash = PosReceiptShareTokens.Hash("7K2M9QXD4TBH");
        Assert.Equal(64, hash.Length);
        Assert.Equal(hash.ToLowerInvariant(), hash);
        Assert.Equal(hash, PosReceiptShareTokens.Hash("7K2M9QXD4TBH"));
        Assert.NotEqual(hash, PosReceiptShareTokens.Hash("7K2M9QXD4TBJ"));

        // 小写与前后空白按同一令牌处理；长度不对、含 I L O U 或符号的一律当不存在。
        Assert.True(PosReceiptShareTokens.TryNormalize("  7k2m9qxd4tbh ", out var lower));
        Assert.Equal("7K2M9QXD4TBH", lower);
        Assert.False(PosReceiptShareTokens.TryNormalize("7K2M9QXD4TB", out _));
        Assert.False(PosReceiptShareTokens.TryNormalize("7K2M9QXD4TBHH", out _));
        Assert.False(PosReceiptShareTokens.TryNormalize("7K2M9QXD4TBI", out _));
        Assert.False(PosReceiptShareTokens.TryNormalize("7K2M9QXD4TB!", out _));
        Assert.False(PosReceiptShareTokens.TryNormalize("", out _));
        Assert.False(PosReceiptShareTokens.TryNormalize(null, out _));

        Assert.Equal("https://hotbargain.vip/r/7K2M9QXD4TBH", PosReceiptShareTokens.BuildUrl("https://hotbargain.vip/", "7K2M9QXD4TBH"));
    }

    [Theory]
    [InlineData("https://hotbargain.vip", "https://hotbargain.vip")]
    [InlineData("https://hotbargain.vip/", "https://hotbargain.vip")]
    [InlineData("https://shop.example.com:8443/", "https://shop.example.com:8443")]
    // 非 https、带路径 / query、带账号、不是 URL：一律回退默认基址，防止配置错误把二维码指到别处。
    [InlineData("http://hotbargain.vip", PosReceiptOptions.DefaultPublicBaseUrl)]
    [InlineData("https://hotbargain.vip/app", PosReceiptOptions.DefaultPublicBaseUrl)]
    [InlineData("https://hotbargain.vip/?x=1", PosReceiptOptions.DefaultPublicBaseUrl)]
    [InlineData("https://user:pw@hotbargain.vip", PosReceiptOptions.DefaultPublicBaseUrl)]
    [InlineData("not a url", PosReceiptOptions.DefaultPublicBaseUrl)]
    [InlineData("", PosReceiptOptions.DefaultPublicBaseUrl)]
    public void 公开基址只接受干净的https根地址(string configured, string expected)
    {
        Assert.Equal(expected, new PosReceiptOptions { PublicBaseUrl = configured }.ResolveBaseUrl());
    }

    [Theory]
    [InlineData(90, 90)]
    [InlineData(1, 1)]
    [InlineData(365, 365)]
    [InlineData(0, PosReceiptOptions.DefaultLinkLifetimeDays)]
    [InlineData(-5, PosReceiptOptions.DefaultLinkLifetimeDays)]
    [InlineData(366, PosReceiptOptions.DefaultLinkLifetimeDays)]
    public void 链接有效期越界回到默认90天(int configured, int expected)
    {
        Assert.Equal(expected, new PosReceiptOptions { LinkLifetimeDays = configured }.ResolveLifetimeDays());
    }
}
