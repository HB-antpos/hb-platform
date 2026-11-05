namespace Hbpos.Client.Wpf.Services.Zeller;

public enum ZellerPairingState
{
    /// <summary>这次打开设置页还没有检查过。</summary>
    NotChecked,

    Paired,

    /// <summary>SDK 说这台收银机还没有和刷卡机配对。</summary>
    NotPaired,

    /// <summary>店长关掉了 Zeller 的配对窗口，或者取消了配对。</summary>
    Cancelled,

    /// <summary>检查或配对没能完成：Zeller 组件启动失败、连不上网、SDK 没有应答等。</summary>
    Failed
}

/// <summary>一次配对检查的结论。Detail 是 SDK 或网关给的原因，只用于显示和日志。</summary>
public sealed record ZellerPairingCheck(ZellerPairingState State, ZellerErrorType? ErrorType = null, string? Detail = null)
{
    public static ZellerPairingCheck NotChecked { get; } = new(ZellerPairingState.NotChecked);

    public bool IsPaired => State == ZellerPairingState.Paired;
}

/// <summary>
/// 设置页的 Zeller 部分：检查配对、配对刷卡机、启用 Zeller。
/// </summary>
/// <remarks>
/// 中文注释：配对在现场做：刷卡机和收银机各自登录 Zeller 账号，在收银机上由 SDK 自己的窗口选择对应的刷卡机，
/// 不经过我们的服务端，也不上报。配对信息由 SDK 保存在本机（DPAPI），我们只存「启用了 Zeller」这一个设置，
/// 所以启用时不动 Square / Linkly 的任何字段，换回去时它们还在。
/// 配对窗口是 SDK 的 WinForms 窗口，自带置顶：配对期间暂停主窗口的扫码输入和抢焦点。
/// </remarks>
public sealed class ZellerSettingsCoordinator
{
    private readonly IZellerTerminalGateway _gateway;
    private readonly Func<CardTerminalConfiguration, CancellationToken, Task> _saveConfigurationAsync;
    private readonly IScannerInputSuspension? _scannerSuspension;
    private readonly Action<string>? _log;

    public ZellerSettingsCoordinator(
        IZellerTerminalGateway gateway,
        Func<CardTerminalConfiguration, CancellationToken, Task> saveConfigurationAsync,
        IScannerInputSuspension? scannerSuspension = null,
        Action<string>? log = null)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _saveConfigurationAsync = saveConfigurationAsync ?? throw new ArgumentNullException(nameof(saveConfigurationAsync));
        _scannerSuspension = scannerSuspension;
        _log = log;
    }

    /// <summary>检查是否已配对：不弹窗，已配对约几秒返回。</summary>
    public async Task<ZellerPairingCheck> CheckAsync(CancellationToken cancellationToken = default)
    {
        Log("checking pairing");
        var check = Classify(await _gateway.InitialiseAsync(cancellationToken).ConfigureAwait(false));
        Log($"pairing check finished state={check.State} error={check.ErrorType}");
        return check;
    }

    /// <summary>
    /// 配对：弹出 SDK 自己的窗口，店长在里面登录并选择刷卡机。没有总时长，只响应取消。
    /// 窗口打开期间暂停扫码输入；已经配对过时窗口不出现，几秒后直接返回成功。
    /// </summary>
    public async Task<ZellerPairingCheck> PairAsync(CancellationToken cancellationToken = default)
    {
        Log("pairing started");
        using var suspension = _scannerSuspension?.Suspend("zeller pairing");
        var check = Classify(await _gateway.SetupAsync(cancellationToken).ConfigureAwait(false));
        Log($"pairing finished state={check.State} error={check.ErrorType}");
        return check;
    }

    /// <summary>
    /// 启用 Zeller：把处理器存成 Zeller，其余设置原样保留。必须是刚检查或配对成功之后，否则抛出异常。
    /// 返回保存后的配置，调用方据此刷新界面。
    /// </summary>
    public async Task<CardTerminalConfiguration> EnableAsync(
        CardTerminalConfiguration current,
        ZellerPairingCheck pairing,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(pairing);
        if (!pairing.IsPaired)
        {
            throw new InvalidOperationException("Zeller can only be enabled after the terminal is paired.");
        }

        var configuration = current with { Processor = CardProcessorKind.Zeller };
        await _saveConfigurationAsync(configuration, cancellationToken).ConfigureAwait(false);
        Log("zeller enabled");
        return configuration;
    }

    internal static ZellerPairingCheck Classify(ZellerPairingResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.IsPaired)
        {
            return new ZellerPairingCheck(ZellerPairingState.Paired);
        }

        return result.ErrorType switch
        {
            ZellerErrorType.SetupRequired => new ZellerPairingCheck(ZellerPairingState.NotPaired, result.ErrorType, result.Message),
            ZellerErrorType.Cancelled => new ZellerPairingCheck(ZellerPairingState.Cancelled, result.ErrorType, result.Message),
            _ => new ZellerPairingCheck(ZellerPairingState.Failed, result.ErrorType, result.Message)
        };
    }

    private void Log(string message) => _log?.Invoke(message);
}
