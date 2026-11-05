namespace Hbpos.Client.Wpf.Services.Zeller;

/// <summary>
/// 调用 Zeller 刷卡机的唯一入口。实现类是全程序唯一引用 Zeller.PaymentsSdk 的地方。
/// </summary>
/// <remarks>
/// 中文注释：实现必须遵守下面三条，它们来自真机验证（SDK 0.1.0），不是可选的优化。
/// 1. 严格串行：同一时刻只让一个调用进入 SDK，查询也算。上一笔没返回，下一笔在网关里等。
///    SDK 在扣款进行中再收到任何调用时，新调用返回 OperationBusy，而进行中的那笔结果会丢失、永不返回，
///    之后的调用还会收到别的调用的响应。
/// 2. 取消只对进行中的那次调用有效：调用方通过 CancellationToken 取消，实现只在该调用进行期间转成 SDK 的 cancel()。
///    在已结束的调用上调 SDK 的 cancel()，被取消的是当时正在进行或下一个调用。
/// 3. ResetAsync 作废并重建 SDK 的 Terminal：进行中的调用以 Abandoned 结束，之后的调用走新实例。
///    看门狗放弃一笔调用后，必须先 ResetAsync 再做任何查询。重建不需要重新配对。
/// </remarks>
public interface IZellerTerminalGateway
{
    /// <summary>检查这台电脑是否已经和刷卡机配对。不弹界面。</summary>
    Task<ZellerPairingResult> InitialiseAsync(CancellationToken cancellationToken = default);

    /// <summary>打开 Zeller 的配对界面，由店长登录并选择刷卡机。只在设置页调用。</summary>
    Task<ZellerPairingResult> SetupAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 扣款，使用无人值守模式（电脑上不弹 Zeller 窗口）。reference 必须是这次尝试专用的，不能复用。
    /// </summary>
    Task<ZellerCallResult> PurchaseAsync(
        ZellerPurchaseRequest request,
        IProgress<ZellerTerminalEvent>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>按原交易退款。结果未知时调用方只能按退款 reference 查询，不能重发。</summary>
    Task<ZellerCallResult> RefundAsync(
        ZellerRefundRequest request,
        IProgress<ZellerTerminalEvent>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>按 reference 查询交易。返回的列表可能有多笔，也可能为空。</summary>
    Task<ZellerQueryResult> GetTransactionsAsync(string reference, CancellationToken cancellationToken = default);

    /// <summary>作废并重建 SDK 的 Terminal。进行中的调用以 Abandoned 结束。</summary>
    Task ResetAsync(CancellationToken cancellationToken = default);
}
