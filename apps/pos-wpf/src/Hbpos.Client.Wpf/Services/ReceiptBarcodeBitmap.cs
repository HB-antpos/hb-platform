using System.Diagnostics.CodeAnalysis;

namespace Hbpos.Client.Wpf.Services;

/// <summary>
/// 1 位位图形式的一维条码：画布宽度固定为 80mm 纸的可打印宽度，条码水平居中。
/// 每行 <see cref="RowBytes"/> 字节、高位在左、1=黑点，与 ESC/POS 光栅位图（GS v 0）的数据排布一致。
/// </summary>
public sealed class ReceiptBarcodeBitmap(int widthDots, int heightDots, int moduleWidth, byte[] data)
{
    public int WidthDots { get; } = widthDots;

    public int HeightDots { get; } = heightDots;

    /// <summary>每个 Code 128 模块占用的点数（3 或 2）。</summary>
    public int ModuleWidth { get; } = moduleWidth;

    public int RowBytes => (WidthDots + 7) / 8;

    /// <summary>位图数据，长度 = <see cref="RowBytes"/> × <see cref="HeightDots"/>。</summary>
    public byte[] Data { get; } = data;

    /// <summary>指定坐标是否为黑点（调用方须保证坐标在画布内）。</summary>
    public bool IsBlack(int x, int y)
    {
        return (Data[y * RowBytes + (x >> 3)] & (0x80 >> (x & 7))) != 0;
    }

    /// <summary>
    /// 生成 ESC/POS 光栅位图指令 <c>GS v 0 m xL xH yL yH d1…dk</c>（m=0 正常倍率）。
    /// xL/xH 是每行字节数，yL/yH 是行数，均为小端 16 位。
    /// 注意：本方法只产出标准 ESC/POS 字节，并不代表厂商 SDK 一定接受这些字节，见 <see cref="IReceiptBitmapSink"/>。
    /// </summary>
    public byte[] ToEscPosRaster()
    {
        var rowBytes = RowBytes;
        var result = new byte[8 + Data.Length];
        result[0] = 0x1D;
        result[1] = 0x76;
        result[2] = 0x30;
        result[3] = 0x00;
        result[4] = (byte)(rowBytes & 0xFF);
        result[5] = (byte)(rowBytes >> 8);
        result[6] = (byte)(HeightDots & 0xFF);
        result[7] = (byte)(HeightDots >> 8);
        Buffer.BlockCopy(Data, 0, result, 8, Data.Length);
        return result;
    }
}

/// <summary>
/// 把 Code 128 画成 1 位位图。规则：
/// 1. 模块宽先取调用方期望值（最大 3 点），放不下退到 2 点，<b>绝不使用 1 点</b>——
///    pos-receipt-core 明确禁止单点模块，热敏纸上 1 点模块扫码不稳；2 点也放不下就返回 false，由调用方降级成文字。
/// 2. 「放得下」按「条码模块数 + 左右各 10 模块静区」计算，静区不足会让扫码枪找不到条码起点。
/// </summary>
public static class ReceiptBarcodeBitmapRenderer
{
    /// <summary>80mm 热敏纸可打印宽度（点）。</summary>
    public const int PrintableWidthDots = 576;

    /// <summary>条码两侧各预留的静区模块数。</summary>
    public const int QuietZoneModules = 10;

    public const int MaxModuleWidth = 3;

    public const int MinModuleWidth = 2;

    /// <summary>
    /// 渲染条码位图。内容不是可打印 ASCII、高度非正、或连 2 点模块宽都放不下时返回 false。
    /// </summary>
    /// <param name="value">条码内容（8 位单号等）。</param>
    /// <param name="heightDots">条码高度（点）。</param>
    /// <param name="preferredModuleWidth">期望的模块宽，会被限制到 2~3 点之间。</param>
    public static bool TryRender(
        string value,
        int heightDots,
        int preferredModuleWidth,
        [NotNullWhen(true)] out ReceiptBarcodeBitmap? bitmap)
    {
        bitmap = null;
        if (heightDots <= 0 || !ReceiptCode128.CanEncode(value))
        {
            return false;
        }

        var encoding = ReceiptCode128.Encode(value);
        var moduleWidth = SelectModuleWidth(encoding.ModuleCount, preferredModuleWidth);
        if (moduleWidth is null)
        {
            return false;
        }

        var width = moduleWidth.Value;
        var rowBytes = (PrintableWidthDots + 7) / 8;
        var barsWidth = encoding.ModuleCount * width;
        // 条码整体水平居中；左右空白 ≥ 静区，因为 SelectModuleWidth 已按含静区的总宽校验过。
        var left = (PrintableWidthDots - barsWidth) / 2;

        // 先画好第一行，再复制到其余行：条码每一行完全相同。
        var firstRow = new byte[rowBytes];
        var x = left;
        foreach (var run in encoding.Runs)
        {
            var runWidth = run.Modules * width;
            if (run.IsBar)
            {
                for (var dx = 0; dx < runWidth; dx++)
                {
                    var column = x + dx;
                    firstRow[column >> 3] |= (byte)(0x80 >> (column & 7));
                }
            }

            x += runWidth;
        }

        var data = new byte[rowBytes * heightDots];
        for (var y = 0; y < heightDots; y++)
        {
            Buffer.BlockCopy(firstRow, 0, data, y * rowBytes, rowBytes);
        }

        bitmap = new ReceiptBarcodeBitmap(PrintableWidthDots, heightDots, width, data);
        return true;
    }

    /// <summary>
    /// 选模块宽：从期望值（上限 3）向下试到 2，第一个满足「(模块数 + 2×静区) × 宽 ≤ 可打印宽」的即为结果；都不满足返回 null。
    /// </summary>
    public static int? SelectModuleWidth(int moduleCount, int preferredModuleWidth)
    {
        var start = Math.Clamp(preferredModuleWidth, MinModuleWidth, MaxModuleWidth);
        for (var width = start; width >= MinModuleWidth; width--)
        {
            if ((moduleCount + 2 * QuietZoneModules) * width <= PrintableWidthDots)
            {
                return width;
            }
        }

        return null;
    }
}

/// <summary>
/// 位图输出口：把渲染好的条码位图送到打印机。做成接口是因为 printer.sdk.dll 的位图函数
/// （PrintImage / PrintImage1 / WriteData）参数签名没有文档，参数不对的原生调用可能直接让 POS 进程崩溃，
/// 所以默认实现 <see cref="UnavailableReceiptBitmapSink"/> 不接任何原生调用，
/// 待拿到 SDK 文档或在装了打印机的 Windows 上验证后，再提供真正的实现替换它。
/// </summary>
public interface IReceiptBitmapSink
{
    /// <summary>打印位图；成功返回 true，不可用或失败返回 false（调用方会降级成文字单号）。不得抛出非取消异常。</summary>
    bool TryPrint(ReceiptBarcodeBitmap bitmap);
}

/// <summary>默认位图输出：始终「不可用」，让降级链直接落到文字单号。</summary>
public sealed class UnavailableReceiptBitmapSink : IReceiptBitmapSink
{
    public static UnavailableReceiptBitmapSink Instance { get; } = new();

    public bool TryPrint(ReceiptBarcodeBitmap bitmap)
    {
        return false;
    }
}
