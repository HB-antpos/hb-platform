using Microsoft.Extensions.Logging;
using Zeller.PaymentsSdk;
using Zeller.PaymentsSdk.Responses;

namespace Hbpos.Client.Wpf.Services.Zeller.Sdk;

/// <summary>
/// 告诉 Zeller 是哪个收银软件在连刷卡机。真机验证过：这几项改了不影响已有的配对。
/// </summary>
public sealed record ZellerSdkVendorInfo(string ApplicationName, string ApplicationVersion, string DeviceType, string VendorName)
{
    public const string DefaultApplicationName = "HBPOS";
    public const string DefaultDeviceType = "POS";
    public const string DefaultVendorName = "HotBargain";

    /// <summary>用收银端自己的版本号（主.次.修订）。本地调试版是 0.0.0。</summary>
    public static ZellerSdkVendorInfo ForCurrentApplication()
    {
        var version = typeof(ZellerSdkVendorInfo).Assembly.GetName().Version;
        var text = version is null ? "0.0.0" : $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";
        return new ZellerSdkVendorInfo(DefaultApplicationName, text, DefaultDeviceType, DefaultVendorName);
    }
}

/// <summary>创建真正的 Zeller SDK Terminal。</summary>
/// <remarks>
/// 中文注释：SDK 的日志工厂是静态属性，文档要求在构造 Terminal 之前设置，所以放在构造函数里。
/// Terminal 的构造本身不连网、不弹窗；缺少 WebView2 运行时这类问题会在第一次调用时以异常的形式出现，
/// 网关把它记成「调用没有进入 SDK」，上层提示 Zeller 不可用。
/// </remarks>
public sealed class ZellerSdkTerminalFactory : IZellerSdkTerminalFactory
{
    private readonly ZellerSdkVendorInfo _vendor;

    public ZellerSdkTerminalFactory(ZellerSdkVendorInfo vendor, ILoggerFactory? sdkLoggerFactory = null)
    {
        _vendor = vendor ?? throw new ArgumentNullException(nameof(vendor));
        if (sdkLoggerFactory is not null)
        {
            Terminal.LoggerFactory = sdkLoggerFactory;
        }
    }

    public IZellerSdkTerminal Create() =>
        new ZellerSdkTerminal(new Terminal(_vendor.ApplicationName, _vendor.ApplicationVersion, _vendor.DeviceType, _vendor.VendorName));
}

/// <summary>对一个 SDK Terminal 的薄包装：只转换参数和结果。排队、超时、重建都在 <see cref="ZellerTerminalGateway"/> 里。</summary>
internal sealed class ZellerSdkTerminal : IZellerSdkTerminal
{
    private readonly Terminal _terminal;

    public ZellerSdkTerminal(Terminal terminal)
    {
        _terminal = terminal ?? throw new ArgumentNullException(nameof(terminal));
    }

    public IZellerSdkOperation<ZellerPairingResult> Initialise() =>
        ZellerSdkOperation<InitialiseResponse, ZellerPairingResult>.From(
            _terminal.initialise(),
            ZellerSdkMapper.ToPairingResult);

    /// <summary>配对。没配对时 SDK 会弹出自己的登录窗口；已配对时约 8 秒直接返回成功。</summary>
    public IZellerSdkOperation<ZellerPairingResult> Setup() =>
        ZellerSdkOperation<SetupResponse, ZellerPairingResult>.From(
            _terminal.setup(),
            ZellerSdkMapper.ToPairingResult);

    /// <summary>
    /// 扣款，固定用无人值守模式：电脑上不弹 Zeller 窗口，所有提示都在刷卡机上；
    /// 顾客约 30 秒不刷卡就返回 InactivityTimeout，不会停在 SDK 的 Try Again 页面等操作员。
    /// </summary>
    public IZellerSdkOperation<ZellerCallResult> Purchase(ZellerPurchaseRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ZellerSdkOperation<PurchaseResponse, ZellerCallResult>.From(
            _terminal.purchase(request.AmountCents, request.Reference, unattended: true),
            ZellerSdkMapper.ToCallResult);
    }

    /// <summary>
    /// 按原交易的 transactionUuid 退款，不需要刷卡。
    /// SDK 0.1.0 的 refund() 没有无人值守参数，是否会弹 Zeller 窗口要在真机上确认。
    /// </summary>
    public IZellerSdkOperation<ZellerCallResult> Refund(ZellerRefundRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ZellerSdkOperation<RefundResponse, ZellerCallResult>.From(
            _terminal.refund(request.AmountCents, purchase: request.OriginalTransactionUuid, reference: request.Reference),
            ZellerSdkMapper.ToCallResult);
    }

    public IZellerSdkOperation<ZellerQueryResult> GetTransactions(string reference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        return ZellerSdkOperation<GetTransactionsResponse, ZellerQueryResult>.From(
            _terminal.getTransactions(reference),
            ZellerSdkMapper.ToQueryResult);
    }

    public void Dispose() => _terminal.Dispose();
}
