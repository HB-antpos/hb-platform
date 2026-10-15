using Hbpos.Client.Wpf.ViewModels;

namespace Hbpos.Client.Wpf.Services;

/// <summary>
/// 重启安全检查的外壳层读取实现：内存状态来自购物车与主界面（沿用 <see cref="ShellAppUpdateInstallSafetyGuard"/> 的读法），
/// 持久化状态来自各仓储。只读取，不改变任何状态（唯一例外是 <see cref="RequestUploadsAsync"/> 主动唤醒上传）。
/// </summary>
public sealed class ShellRestartSafetyProbe(
    PosCartService cart,
    MainViewModel mainViewModel,
    ISyncQueueRepository syncQueueRepository,
    ILocalDailyCloseUploadRepository dailyCloseUploadRepository,
    ILinklySettlementUploadQueueReader settlementUploadQueueReader,
    IOrderUploadExecutionService orderUploadExecutionService,
    IDailyCloseUploadScheduler dailyCloseUploadScheduler,
    ILinklySettlementUploadScheduler settlementUploadScheduler,
    ICardPaymentRecoveryService? cardPaymentRecoveryService = null,
    ISquareRefundSettlementService? squareRefundSettlementService = null,
    ILinklyUnresolvedSettlementReader? linklyUnresolvedSettlementReader = null,
    ILinklyTerminalSelectionTransitionGate? linklyTransitionGate = null,
    TimeProvider? timeProvider = null) : IRestartSafetyProbe
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public int CartLineCount => cart.Lines.Count;

    public bool IsPaymentActive
    {
        get
        {
            var payment = mainViewModel.CachedCashPaymentScreen;
            return payment is not null &&
                (payment.IsCardPaymentInProgress ||
                    payment.IsPaymentInteractionLocked ||
                    payment.PaymentTenders.Count > 0);
        }
    }

    public bool IsDailyCloseBusy =>
        mainViewModel.DailyClose is { } dailyClose &&
        (dailyClose.IsBusy || dailyClose.HasDailyCloseDraft);

    // 中文注释：只看「正在下载 / 强制更新流程」。可选更新的安装包下载好了但没点安装，不影响重启（重启后仍可安装）。
    public bool IsAppUpdateActive => mainViewModel.AppUpdate.IsUpdateFlowActive;

    public async ValueTask<bool> IsCardTerminalBusyAsync(CancellationToken cancellationToken)
    {
        if (linklyTransitionGate is null)
        {
            return false;
        }

        // 金融操作（支付 / 恢复 / 结算）持有同一把门闸；TryEnterAssignment 不等待，拿不到就说明有操作在飞。
        var lease = await linklyTransitionGate.TryEnterAssignmentAsync(cancellationToken);
        if (lease is null)
        {
            return true;
        }

        await lease.DisposeAsync();
        return false;
    }

    public async Task<int> CountOpenCardRecoveryAsync(CancellationToken cancellationToken)
    {
        var session = mainViewModel.Session;
        var open = cardPaymentRecoveryService is null
            ? 0
            : (await cardPaymentRecoveryService.ListOpenAsync(session, cancellationToken)).Count;

        // 已完成退货但 Square 拒绝的退款同样需要主管处理，和异常中心角标口径一致。
        var settlementFailures = squareRefundSettlementService is null
            ? 0
            : await squareRefundSettlementService.CountUnhandledFailuresAsync(session, cancellationToken);
        return open + settlementFailures;
    }

    public async Task<bool> HasUnresolvedLinklySettlementAsync(CancellationToken cancellationToken)
    {
        if (linklyUnresolvedSettlementReader is null)
        {
            return false;
        }

        var session = mainViewModel.Session;
        // 不确定当前生效的是哪个环境，两个环境都查：任何一个有未决结算都不该重启。
        foreach (var environment in Enum.GetNames<CardTerminalEnvironment>())
        {
            if (await linklyUnresolvedSettlementReader.HasUnresolvedAsync(
                    session.StoreCode,
                    session.DeviceCode,
                    environment,
                    cancellationToken))
            {
                return true;
            }
        }

        return false;
    }

    public async Task<RestartUploadBacklog> GetUploadBacklogAsync(CancellationToken cancellationToken)
    {
        var session = mainViewModel.Session;

        // 订单：Pending + Syncing。Failed 不算积压（要等重试才可能变化，30 秒内等不到）。
        var orders = await syncQueueRepository.GetOverviewAsync(cancellationToken);
        var dailyCloses = await dailyCloseUploadRepository.CountDueUploadsAsync(
            session.StoreCode,
            session.DeviceCode,
            _clock.GetUtcNow(),
            cancellationToken);
        var settlements = await settlementUploadQueueReader.GetOverviewAsync(cancellationToken);

        return new RestartUploadBacklog(
            orders.PendingCount + orders.SyncingCount,
            dailyCloses,
            settlements.PendingCount + settlements.UploadingCount);
    }

    public async Task RequestUploadsAsync(CancellationToken cancellationToken)
    {
        // 日结与结算有自己的后台 Worker：唤醒即可；订单没有独立 Worker，直接按同步中心「全部重试」的口径补传一轮。
        dailyCloseUploadScheduler.RequestUpload();
        settlementUploadScheduler.RequestUpload();
        await orderUploadExecutionService.ExecuteAllRetryableAsync(cancellationToken: cancellationToken);
    }
}
