namespace Hbpos.Client.Wpf.Services;

/// <summary>
/// 扫码按键的时间换算与中心日志。字符间隔必须按按键实际发生的时间判断：
/// 用处理时刻的话，界面线程卡顿超过 120ms 就会清掉已收到的开头，条码只剩结尾（如 9525810220077 → 5810220077）。
/// </summary>
internal static class ScannerInputTiming
{
    // 比这更早或晚于当前的事件时间视为无效（如合成事件的时间戳为 0），退回处理时刻。
    private const int MaxEventAgeMs = 60_000;

    // 丢掉的字符太少时多半是误触，不值得上报。
    private const int MinReportedDiscardLength = 3;

    /// <summary>把消息时间（与 <see cref="Environment.TickCount"/> 同基准）换算成按键发生的时刻。</summary>
    public static DateTimeOffset ResolveEventTime(int eventTick, int nowTick, DateTimeOffset now)
    {
        // TickCount 约 24.9 天回绕一次，按差值计算不受影响。
        var ageMs = unchecked(nowTick - eventTick);
        return ageMs is >= 0 and <= MaxEventAgeMs ? now.AddMilliseconds(-ageMs) : now;
    }

    /// <summary>
    /// 按事件时间仍然超时清空了缓冲：按键之间确实有停顿，不是界面卡顿。
    /// 绑定的扫码枪上频繁出现时，优先查扫码枪或无线、USB 链路。
    /// </summary>
    public static void ReportGapReset(string source, int discardedLength, double gapMs)
    {
        if (discardedLength < MinReportedDiscardLength)
        {
            return;
        }

        ConsoleLog.WriteWarning(
            "RawScanner",
            $"scan input gap reset source={source} discardedLength={discardedLength} gapMs={gapMs:0.###}",
            CreateContext("scan-input-gap-reset", source, gapMs));
    }

    /// <summary>界面卡顿拉长了处理间隔，但按事件时间是连续的：改用事件时间之前，这次扫码会被截断。</summary>
    public static void ReportStallAbsorbed(string source, string barcode, double handledGapMs)
    {
        ConsoleLog.WriteInformation(
            "RawScanner",
            $"scan input stall absorbed source={source} barcodeInfo={BarcodeLogFormatter.FormatBarcodeInfo(barcode)} handledGapMs={handledGapMs:0.###}",
            CreateContext("scan-input-stall-absorbed", source, handledGapMs));
    }

    // 中心日志只保留白名单属性（见 ClientLogOutboxWriter），条码长度等写在消息里。
    public static ApplicationLogContext CreateContext(string action, string source, double elapsedMs) =>
        new(Properties: new Dictionary<string, object?>
        {
            ["action"] = action,
            ["source"] = source,
            ["elapsedMs"] = (long)Math.Round(elapsedMs)
        });
}

/// <summary>
/// 记录一次扫码里界面卡顿造成的最大处理间隔，用来上报 <see cref="ScannerInputTiming.ReportStallAbsorbed"/>。
/// 所有入口都在 UI 线程。
/// </summary>
internal sealed class ScanInputLagTracker
{
    private DateTimeOffset? _lastEventAt;
    private DateTimeOffset _lastHandledAt;
    private double _maxAbsorbedGapMs;

    public void RecordCharacter(DateTimeOffset eventAt, DateTimeOffset handledAt)
    {
        if (_lastEventAt is { } lastEventAt)
        {
            if (eventAt - lastEventAt > RawScannerInputProcessor.DefaultScanTimeout)
            {
                // 真实停顿：缓冲已被清空，前面的卡顿不属于这次扫码。
                _maxAbsorbedGapMs = 0;
            }
            else
            {
                var handledGap = handledAt - _lastHandledAt;
                if (handledGap > RawScannerInputProcessor.DefaultScanTimeout)
                {
                    _maxAbsorbedGapMs = Math.Max(_maxAbsorbedGapMs, handledGap.TotalMilliseconds);
                }
            }
        }

        _lastEventAt = eventAt;
        _lastHandledAt = handledAt;
    }

    /// <summary>取出本次扫码被吸收的最大卡顿间隔并重置；没有卡顿时返回 null。</summary>
    public double? TakeAbsorbedGapMs()
    {
        double? result = _maxAbsorbedGapMs > 0 ? _maxAbsorbedGapMs : null;
        Reset();
        return result;
    }

    public void Reset()
    {
        _lastEventAt = null;
        _maxAbsorbedGapMs = 0;
    }
}
