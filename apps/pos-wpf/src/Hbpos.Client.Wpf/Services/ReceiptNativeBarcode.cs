namespace Hbpos.Client.Wpf.Services;

/// <summary>
/// 芯烨（Xprinter）ESC/POS SDK 原生一维条码的参数约定。
/// 2026-10-31 在 XP-80C 上实测：
/// <list type="bullet">
/// <item><c>PrintBarCode</c> 的 bcType=8 无论参数怎么调都返回 0（成功），但打印机什么条纹都不出，只印可读文字；</item>
/// <item>bcType=73（ESC/POS <c>GS k 73</c>，Code 128）才会出条码，内容必须以字符集选择前缀 <c>{B</c> 开头（可读文字里不印前缀），内容里的 <c>{</c> 要写成 <c>{{</c>；</item>
/// <item>条码放不下 80mm 纸（576 点）时打印机直接丢掉整条条码，SDK 仍返回 0——例如 36 位 GUID 在 2 点模块宽下约需 860 点，一个字也印不出。</item>
/// </list>
/// 所以放不下的内容要在发指令前就判为失败，交给降级链去印文字。纯逻辑，不依赖 WPF 与打印机，便于单独测试。
/// </summary>
public static class ReceiptNativeBarcode
{
    /// <summary>SDK 里 Code 128 的 bcType（对应 ESC/POS <c>GS k</c> 的 m=73）。</summary>
    public const int Code128BarcodeType = 73;

    // 中文注释：GS k 73 的数据长度字节 n 最大 255，前缀占 2 个字节。
    private const int MaxDataLength = 255;

    private const string SubsetBPrefix = "{B";

    // 中文注释：与位图渲染一致——整条条码左右各留 10 个模块的静区，扫码枪才找得到起点。
    private const int QuietZoneModules = 10;

    /// <summary>
    /// 把条码内容转成 SDK 需要的 Code 128 B 集合数据：加 <c>{B</c> 前缀，内容里的 <c>{</c> 要写成 <c>{{</c>，
    /// 否则打印机会把它当成字符集切换指令。内容为空、含非可打印 ASCII、命令太长，
    /// 或按 <paramref name="moduleWidth"/> 点的模块宽在 <see cref="ReceiptBarcodeBitmapRenderer.PrintableWidthDots"/> 点的纸上放不下时返回 false，由调用方降级。
    /// </summary>
    public static bool TryCreateCode128Data(string? value, int moduleWidth, out string data)
    {
        data = string.Empty;
        if (moduleWidth <= 0 || !ReceiptCode128.CanEncode(value))
        {
            return false;
        }

        var escaped = value!.Replace("{", "{{", StringComparison.Ordinal);
        if (SubsetBPrefix.Length + escaped.Length > MaxDataLength)
        {
            return false;
        }

        if ((CountModules(value.Length) + 2 * QuietZoneModules) * moduleWidth > ReceiptBarcodeBitmapRenderer.PrintableWidthDots)
        {
            return false;
        }

        data = SubsetBPrefix + escaped;
        return true;
    }

    /// <summary>
    /// B 集合下的条码模块数（不含静区）：起始符 + 每个字符 + 校验符各 11 个模块，终止符 13 个。
    /// 打印机固件在 <c>{B</c> 前缀下不会像位图编码器那样把数字段压成 C 集合，所以按字符数估算，不用 <see cref="ReceiptCode128"/> 的最优编码。
    /// </summary>
    internal static int CountModules(int characterCount) => 11 * (characterCount + 2) + 13;
}
