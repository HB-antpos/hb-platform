using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Tests;

public sealed class ReceiptBarcodeBitmapTests
{
    private const string OrderCode = "6E0F13AB";

    [Fact]
    public void Order_code_renders_at_three_dots_per_module_on_a_full_width_canvas()
    {
        Assert.True(ReceiptBarcodeBitmapRenderer.TryRender(OrderCode, 80, 3, out var bitmap));

        Assert.Equal(3, bitmap.ModuleWidth);
        Assert.Equal(576, bitmap.WidthDots);
        Assert.Equal(80, bitmap.HeightDots);
        Assert.Equal(72, bitmap.RowBytes);
        Assert.Equal(72 * 80, bitmap.Data.Length);
    }

    [Fact]
    public void Barcode_is_centered_with_at_least_ten_modules_of_quiet_zone_on_both_sides()
    {
        Assert.True(ReceiptBarcodeBitmapRenderer.TryRender(OrderCode, 80, 3, out var bitmap));
        var barsWidth = 123 * 3;
        var left = (576 - barsWidth) / 2;

        // 第一段是条：左边界前全白，边界起第一个点就是黑。
        for (var x = 0; x < left; x++)
        {
            Assert.False(bitmap.IsBlack(x, 0), $"左侧静区 x={x} 应为白");
        }

        Assert.True(bitmap.IsBlack(left, 0));
        // 终止符以条结尾，最后一个条码点之后全白。
        Assert.True(bitmap.IsBlack(left + barsWidth - 1, 0));
        for (var x = left + barsWidth; x < 576; x++)
        {
            Assert.False(bitmap.IsBlack(x, 0), $"右侧静区 x={x} 应为白");
        }

        Assert.True(left >= ReceiptBarcodeBitmapRenderer.QuietZoneModules * 3);
        Assert.True(576 - left - barsWidth >= ReceiptBarcodeBitmapRenderer.QuietZoneModules * 3);
    }

    [Fact]
    public void Bitmap_modules_sampled_at_their_centers_reproduce_the_code128_runs()
    {
        // 「解码」一遍位图：在每个模块中心采样，颜色必须与编码出的条空序列一致。
        Assert.True(ReceiptBarcodeBitmapRenderer.TryRender(OrderCode, 80, 3, out var bitmap));
        var encoding = ReceiptCode128.Encode(OrderCode);
        var x = (576 - encoding.ModuleCount * 3) / 2;

        foreach (var run in encoding.Runs)
        {
            for (var m = 0; m < run.Modules; m++)
            {
                for (var dx = 0; dx < 3; dx++)
                {
                    Assert.Equal(run.IsBar, bitmap.IsBlack(x + m * 3 + dx, 40));
                }
            }

            x += run.Modules * 3;
        }
    }

    [Fact]
    public void Every_row_is_identical()
    {
        Assert.True(ReceiptBarcodeBitmapRenderer.TryRender(OrderCode, 80, 3, out var bitmap));
        var firstRow = bitmap.Data.AsSpan(0, bitmap.RowBytes).ToArray();

        for (var y = 1; y < bitmap.HeightDots; y++)
        {
            Assert.Equal(firstRow, bitmap.Data.AsSpan(y * bitmap.RowBytes, bitmap.RowBytes).ToArray());
        }
    }

    [Fact]
    public void Numeric_code_that_does_not_fit_at_three_dots_falls_back_to_two()
    {
        // 39 位纯数字 266 模块：(266+20)×3=858 > 576，(266+20)×2=572 ≤ 576。
        var value = "123456789012345678901234567890123456789";

        Assert.True(ReceiptBarcodeBitmapRenderer.TryRender(value, 80, 3, out var bitmap));
        Assert.Equal(2, bitmap.ModuleWidth);
    }

    [Fact]
    public void Code_that_does_not_fit_even_at_two_dots_is_refused_instead_of_using_single_dot_modules()
    {
        // 完整 GUID 431 模块：(431+20)×2=902 > 576，绝不退到 1 点。
        Assert.False(ReceiptBarcodeBitmapRenderer.TryRender(
            "7e4c91d2-0b5a-4f3e-9a21-5c8d6e0f13ab", 80, 3, out var bitmap));
        Assert.Null(bitmap);
    }

    [Theory]
    [InlineData(172, 3, 3)]    // (172+20)×3 = 576 正好放下
    [InlineData(173, 3, 2)]    // 再多 1 个模块，3 点放不下
    [InlineData(268, 3, 2)]    // (268+20)×2 = 576 正好放下
    [InlineData(269, 3, null)] // 2 点也放不下
    [InlineData(1000, 3, null)]
    [InlineData(100, 2, 2)]    // 期望 2 点就不会升到 3 点
    [InlineData(100, 1, 2)]    // 期望值低于 2 也按 2 点处理，绝不使用 1 点
    [InlineData(100, 9, 3)]    // 期望值高于 3 封顶为 3
    public void SelectModuleWidth_picks_the_widest_that_fits_and_never_below_two(
        int moduleCount,
        int preferred,
        int? expected)
    {
        Assert.Equal(expected, ReceiptBarcodeBitmapRenderer.SelectModuleWidth(moduleCount, preferred));
    }

    [Theory]
    [InlineData("", 80)]
    [InlineData("订单", 80)]
    [InlineData(OrderCode, 0)]
    [InlineData(OrderCode, -5)]
    public void Invalid_input_is_refused(string value, int height)
    {
        Assert.False(ReceiptBarcodeBitmapRenderer.TryRender(value, height, 3, out var bitmap));
        Assert.Null(bitmap);
    }

    [Fact]
    public void EscPos_raster_has_gs_v_0_header_with_little_endian_dimensions_followed_by_the_bitmap()
    {
        Assert.True(ReceiptBarcodeBitmapRenderer.TryRender(OrderCode, 80, 3, out var bitmap));

        var raster = bitmap.ToEscPosRaster();

        // GS v 0 m=0 xL xH yL yH：72 字节/行 = 0x0048，80 行 = 0x0050。
        Assert.Equal(new byte[] { 0x1D, 0x76, 0x30, 0x00, 0x48, 0x00, 0x50, 0x00 }, raster.Take(8).ToArray());
        Assert.Equal(8 + 72 * 80, raster.Length);
        Assert.Equal(bitmap.Data, raster.Skip(8).ToArray());
    }

    [Fact]
    public void EscPos_raster_encodes_dimensions_above_255_in_two_bytes()
    {
        Assert.True(ReceiptBarcodeBitmapRenderer.TryRender(OrderCode, 300, 3, out var bitmap));

        var raster = bitmap.ToEscPosRaster();

        Assert.Equal(300 & 0xFF, raster[6]);
        Assert.Equal(300 >> 8, raster[7]);
    }

    [Fact]
    public void Default_bitmap_sink_is_unavailable()
    {
        Assert.True(ReceiptBarcodeBitmapRenderer.TryRender(OrderCode, 80, 3, out var bitmap));

        Assert.False(UnavailableReceiptBitmapSink.Instance.TryPrint(bitmap));
    }
}
