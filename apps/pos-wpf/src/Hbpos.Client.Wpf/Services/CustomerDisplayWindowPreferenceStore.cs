using System.Globalization;

namespace Hbpos.Client.Wpf.Services;

/// <summary>普通窗口模式下客显窗口的位置与大小（WPF 逻辑单位 DIP，虚拟屏幕坐标）。</summary>
public readonly record struct CustomerDisplayNormalBounds(double Left, double Top, double Width, double Height);

/// <summary>本机记住的客显窗口上次设置：模式（关闭/窗口/全屏）与普通窗口位置大小。</summary>
public sealed record CustomerDisplayWindowPreference(
    CustomerDisplayWindowMode Mode,
    CustomerDisplayNormalBounds? NormalBounds)
{
    public static CustomerDisplayWindowPreference Default { get; } = new(CustomerDisplayWindowMode.Closed, null);
}

public interface ICustomerDisplayWindowPreferenceStore
{
    /// <summary>内存中的最新设置；启动加载完成前为默认值（关闭、无窗口位置）。</summary>
    CustomerDisplayWindowPreference Current { get; }

    Task<CustomerDisplayWindowPreference> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>记住收银员手动选择的模式；返回的任务只用于测试等待，写库失败只记日志不抛出。</summary>
    Task RememberModeAsync(CustomerDisplayWindowMode mode);

    /// <summary>记住收银员拖动或缩放后的普通窗口位置大小；写库失败只记日志不抛出。</summary>
    Task RememberNormalBoundsAsync(CustomerDisplayNormalBounds bounds);
}

/// <summary>
/// 客显窗口上次设置按设备保存在本地 AppSettings 表（与小票打印机端口等设备级设置同一张表），不随门店或收银员变化。
/// </summary>
public sealed class CustomerDisplayWindowPreferenceStore : ICustomerDisplayWindowPreferenceStore
{
    internal const string ModeKey = "CustomerDisplay:LastMode";
    internal const string NormalBoundsKey = "CustomerDisplay:NormalBounds";

    private readonly ILocalAppSettingsRepository _settingsRepository;
    private readonly SemaphoreSlim _persistGate = new(1, 1);
    private readonly object _sync = new();
    private CustomerDisplayWindowPreference _current = CustomerDisplayWindowPreference.Default;
    private bool _modeChanged;
    private bool _boundsChanged;

    public CustomerDisplayWindowPreferenceStore(ILocalAppSettingsRepository settingsRepository)
    {
        _settingsRepository = settingsRepository;
    }

    public CustomerDisplayWindowPreference Current
    {
        get
        {
            lock (_sync)
            {
                return _current;
            }
        }
    }

    public async Task<CustomerDisplayWindowPreference> LoadAsync(CancellationToken cancellationToken = default)
    {
        var mode = ParseMode(await _settingsRepository.GetValueAsync(ModeKey, cancellationToken));
        var bounds = ParseBounds(await _settingsRepository.GetValueAsync(NormalBoundsKey, cancellationToken));
        lock (_sync)
        {
            // 关键逻辑：加载期间收银员若已手动改过，以内存中的新值为准，避免库里的旧值把它覆盖回去。
            _current = new CustomerDisplayWindowPreference(
                _modeChanged ? _current.Mode : mode,
                _boundsChanged ? _current.NormalBounds : bounds);
            return _current;
        }
    }

    public Task RememberModeAsync(CustomerDisplayWindowMode mode)
    {
        lock (_sync)
        {
            _current = _current with { Mode = mode };
            _modeChanged = true;
        }

        return PersistAsync();
    }

    public Task RememberNormalBoundsAsync(CustomerDisplayNormalBounds bounds)
    {
        if (!IsUsable(bounds))
        {
            return Task.CompletedTask;
        }

        lock (_sync)
        {
            _current = _current with { NormalBounds = bounds };
            _boundsChanged = true;
        }

        return PersistAsync();
    }

    /// <summary>
    /// 按目标显示器工作区（DIP）校正上次的普通窗口位置：尺寸收进工作区、位置夹回屏内；
    /// 上次窗口与该显示器完全没有交集（换了显示器或分辨率）时返回 null，由调用方回落默认居中。
    /// </summary>
    internal static CustomerDisplayNormalBounds? ResolveRestoredBounds(
        CustomerDisplayNormalBounds workArea,
        CustomerDisplayNormalBounds? remembered,
        double minWidth,
        double minHeight)
    {
        if (remembered is not { } saved || !IsUsable(saved) || !IsUsable(workArea))
        {
            return null;
        }

        var intersects = saved.Left < workArea.Left + workArea.Width
            && saved.Left + saved.Width > workArea.Left
            && saved.Top < workArea.Top + workArea.Height
            && saved.Top + saved.Height > workArea.Top;
        if (!intersects)
        {
            return null;
        }

        var safeMinWidth = double.IsFinite(minWidth) ? Math.Max(0, minWidth) : 0;
        var safeMinHeight = double.IsFinite(minHeight) ? Math.Max(0, minHeight) : 0;
        if (safeMinWidth > workArea.Width || safeMinHeight > workArea.Height)
        {
            return null;
        }

        var width = Math.Min(Math.Max(saved.Width, safeMinWidth), workArea.Width);
        var height = Math.Min(Math.Max(saved.Height, safeMinHeight), workArea.Height);
        var left = Math.Clamp(saved.Left, workArea.Left, workArea.Left + workArea.Width - width);
        var top = Math.Clamp(saved.Top, workArea.Top, workArea.Top + workArea.Height - height);
        return new CustomerDisplayNormalBounds(left, top, width, height);
    }

    internal static string FormatBounds(CustomerDisplayNormalBounds bounds) =>
        string.Join(
            ",",
            new[] { bounds.Left, bounds.Top, bounds.Width, bounds.Height }
                .Select(value => value.ToString("R", CultureInfo.InvariantCulture)));

    internal static CustomerDisplayNormalBounds? ParseBounds(string? value)
    {
        var parts = value?.Split(',');
        if (parts is not { Length: 4 })
        {
            return null;
        }

        var numbers = new double[4];
        for (var index = 0; index < parts.Length; index++)
        {
            if (!double.TryParse(parts[index], NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[index]))
            {
                return null;
            }
        }

        var bounds = new CustomerDisplayNormalBounds(numbers[0], numbers[1], numbers[2], numbers[3]);
        return IsUsable(bounds) ? bounds : null;
    }

    internal static CustomerDisplayWindowMode ParseMode(string? value) =>
        Enum.TryParse<CustomerDisplayWindowMode>(value, ignoreCase: true, out var mode)
            && Enum.IsDefined(mode)
            ? mode
            : CustomerDisplayWindowMode.Closed;

    private static bool IsUsable(CustomerDisplayNormalBounds bounds) =>
        double.IsFinite(bounds.Left)
        && double.IsFinite(bounds.Top)
        && double.IsFinite(bounds.Width)
        && double.IsFinite(bounds.Height)
        && bounds.Width > 0
        && bounds.Height > 0;

    private async Task PersistAsync()
    {
        try
        {
            // 关键逻辑：串行写库且每次写入取最新快照，连续快速改动时最后落库的一定是最终值。
            await _persistGate.WaitAsync();
            try
            {
                Dictionary<string, string> values;
                lock (_sync)
                {
                    values = new Dictionary<string, string>(StringComparer.Ordinal);
                    if (_modeChanged)
                    {
                        values[ModeKey] = _current.Mode.ToString();
                    }

                    if (_boundsChanged && _current.NormalBounds is { } bounds)
                    {
                        values[NormalBoundsKey] = FormatBounds(bounds);
                    }
                }

                if (values.Count > 0)
                {
                    await _settingsRepository.SetValuesAsync(values);
                }
            }
            finally
            {
                _persistGate.Release();
            }
        }
        catch (Exception ex)
        {
            ConsoleLog.WriteError(
                "CustomerDisplay",
                $"window preference persist failed error={ex.GetType().Name} message={ex.Message}",
                exception: ex);
        }
    }
}
