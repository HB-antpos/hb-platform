using System.Globalization;

namespace Hbpos.Client.Wpf.Services;

/// <summary>
/// 扫码枪的设备级本地设置：目前只有「重复读码过滤窗口」——Raw Input 通道里同一条码在这段时间内的第二次读码按重复丢弃。
/// 存在本机 SQLite 的 AppSettings 键值表，不涉及表结构变更；读取走内存缓存，扫码热路径不碰数据库。
/// </summary>
public interface IScannerSettingsStore
{
    /// <summary>当前生效的过滤窗口（毫秒），范围 100～1000。启动加载前为默认值。</summary>
    int RepeatWindowMs { get; }

    /// <summary>从本机读取并刷新缓存；读失败或值非法时回落默认值。</summary>
    Task LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>保存并立即生效（先落库，成功后才更新缓存）；超出范围的值会被夹到允许范围内。</summary>
    Task SetRepeatWindowMsAsync(int milliseconds, CancellationToken cancellationToken = default);
}

public sealed class ScannerSettingsStore(ILocalAppSettingsRepository settings) : IScannerSettingsStore
{
    internal const string RepeatWindowKey = "Scanner:RawRepeatWindowMs";

    /// <summary>
    /// 默认 600ms（用户定）：生产日志里 Raw 通道同商品连续加购的间隔在 100～500ms 有一个明显的重复读码小峰，
    /// 500ms 之后才是正常的人工重扫曲线；600ms 在此基础上多留一点余量。
    /// </summary>
    public const int DefaultRepeatWindowMs = 600;

    public const int MinRepeatWindowMs = 100;

    public const int MaxRepeatWindowMs = 1000;

    /// <summary>设置页每次点 -/+ 调整的步长。</summary>
    public const int RepeatWindowStepMs = 100;

    private int _repeatWindowMs = DefaultRepeatWindowMs;

    public int RepeatWindowMs => Volatile.Read(ref _repeatWindowMs);

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var raw = await settings.GetValueAsync(RepeatWindowKey, cancellationToken);
        Volatile.Write(ref _repeatWindowMs, Parse(raw));
    }

    public async Task SetRepeatWindowMsAsync(int milliseconds, CancellationToken cancellationToken = default)
    {
        var clamped = Clamp(milliseconds);
        await settings.SetValueAsync(
            RepeatWindowKey,
            clamped.ToString(CultureInfo.InvariantCulture),
            cancellationToken);
        Volatile.Write(ref _repeatWindowMs, clamped);
    }

    public static int Clamp(int milliseconds) => Math.Clamp(milliseconds, MinRepeatWindowMs, MaxRepeatWindowMs);

    /// <summary>从未保存过、或值无法解析 / 超出范围时按默认值处理。</summary>
    internal static int Parse(string? raw)
    {
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) &&
               value >= MinRepeatWindowMs &&
               value <= MaxRepeatWindowMs
            ? value
            : DefaultRepeatWindowMs;
    }
}
