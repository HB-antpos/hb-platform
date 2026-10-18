namespace Hbpos.Client.Wpf.Services;

/// <summary>
/// 条码前导 0 变体。扫码枪常把「以 0 开头的 EAN-13」当作 UPC-A 去掉前导 0 变成 12 位，
/// 而库里按价签上的 13 位存放（反之亦然）。
/// 语义与后端 BarcodeVariants.GetLeadingZeroVariant、移动端 getLeadingZeroBarcodeVariant 保持一致。
/// 调用方必须「精确匹配无结果才回退」：生产库存在 12 位 X 与 13 位 0+X 分属不同商品的情况，
/// 不能把两种写法当作等价一起查。
/// </summary>
internal static class BarcodeVariants
{
    /// <summary>
    /// 纯数字条码的前导 0 变体：12 位补一个 0 → 13 位；13 位且以 0 开头则去掉它 → 12 位。
    /// 其它长度（含 11/14 位）或含非数字字符返回 null，不参与变体匹配。
    /// </summary>
    public static string? GetLeadingZeroVariant(string? barcode)
    {
        // 只认 ASCII 数字：char.IsDigit 会放过全角数字等 Unicode 数字，补 0 后查不到也不该参与变体。
        if (barcode is null || barcode.Length is not (12 or 13) || !barcode.All(char.IsAsciiDigit))
        {
            return null;
        }

        if (barcode.Length == 12)
        {
            return "0" + barcode;
        }

        return barcode[0] == '0' ? barcode[1..] : null;
    }
}
