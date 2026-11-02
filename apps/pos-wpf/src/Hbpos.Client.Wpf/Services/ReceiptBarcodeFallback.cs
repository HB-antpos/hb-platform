namespace Hbpos.Client.Wpf.Services;

/// <summary>条码打印最终走到了哪一级。</summary>
public enum ReceiptBarcodeOutcome
{
    /// <summary>打印机原生条码成功（自带下方可读文字）。</summary>
    Native,

    /// <summary>原生失败，位图条码成功，并已补印一行可读单号。</summary>
    Bitmap,

    /// <summary>原生与位图都不可用，只印了可读单号文字行（小票继续出）。</summary>
    TextOnly,

    /// <summary>连文字行也打印失败——打印机本身有问题，调用方应中止整张小票并上报。</summary>
    Failed
}

/// <summary>
/// 条码三级降级的编排（纯函数，三个打印动作以委托传入，驱动只负责接线）：
/// 原生条码 → 位图条码（成功后补印可读单号，因为位图没有 HRI）→ 只印文字单号。
/// 这样条码指令失败不再让整张小票中止，收银员至少能在小票上看到单号并手输。
/// </summary>
public static class ReceiptBarcodeFallback
{
    /// <param name="nativePrint">原生 SDK 条码；返回 true=成功。</param>
    /// <param name="bitmapPrint">位图条码；返回 true=成功，false=不可用或放不下。抛出的非取消异常按失败处理。</param>
    /// <param name="textPrint">打印可读单号文字行；返回 true=成功。</param>
    /// <param name="onDegraded">降级通知（位图成功或只剩文字时调用，用于记 Warning 日志）。</param>
    public static ReceiptBarcodeOutcome Print(
        Func<bool> nativePrint,
        Func<bool> bitmapPrint,
        Func<bool> textPrint,
        Action<ReceiptBarcodeOutcome>? onDegraded = null)
    {
        if (nativePrint())
        {
            return ReceiptBarcodeOutcome.Native;
        }

        if (TryBitmap(bitmapPrint))
        {
            // 位图条码没有 HRI，必须补印可读单号，否则条码读不出时店员无从手输。
            if (!textPrint())
            {
                return ReceiptBarcodeOutcome.Failed;
            }

            onDegraded?.Invoke(ReceiptBarcodeOutcome.Bitmap);
            return ReceiptBarcodeOutcome.Bitmap;
        }

        if (!textPrint())
        {
            return ReceiptBarcodeOutcome.Failed;
        }

        onDegraded?.Invoke(ReceiptBarcodeOutcome.TextOnly);
        return ReceiptBarcodeOutcome.TextOnly;
    }

    private static bool TryBitmap(Func<bool> bitmapPrint)
    {
        try
        {
            return bitmapPrint();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 位图输出口是新增、未经真机验证的路径，它出错只能降级，不能把整张小票拖垮。
            return false;
        }
    }
}
