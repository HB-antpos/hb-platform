using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BlazorApp.Shared.DTOs;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services.Zeller;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Contracts.Common;
using Hbpos.Contracts.Installments;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Wpf.Services;

public sealed record InstallmentOperationResult<T>(
    bool Succeeded,
    T? Response = default,
    LocalInstallmentOrder? LocalOrder = null,
    string? Message = null,
    bool RequiresReview = false,
    // 结果暂时不明、但到这个时间之后再恢复一次就能有结论（Zeller 取消或超时后的迟到复核）。调用方可以到点自动恢复。
    DateTimeOffset? AutoRecoveryDueAt = null);

/// <summary>供创建、补款、取消页面和启动恢复共用的窄服务边界。</summary>
public interface IInstallmentOperationService
{
    Task<InstallmentOperationResult<InstallmentCreateResponse>> ExecuteCreateAsync(
        PosSessionState session,
        InstallmentCreateRequest request,
        bool authorizeCard,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 手动刷卡续付第一步：先占中央 claim 并登记 Manual 通道，成功后收银员才能在独立刷卡机上收款。
    /// 请求的 Reference 必须是 MANUAL: 引用，且不带卡交易证据。
    /// </summary>
    Task<InstallmentOperationResult<bool>> BeginManualCardRepaymentAsync(
        PosSessionState session,
        InstallmentAppendPaymentRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new InstallmentOperationResult<bool>(false, Message: InstallmentText.L("installment.text.3233494a", "当前版本不支持手动刷卡续付。")));

    /// <summary>手动刷卡续付第二步：收银员确认独立刷卡机已收款，写入确认证据并提交。</summary>
    Task<InstallmentOperationResult<InstallmentAppendPaymentResponse>> CompleteManualCardRepaymentAsync(
        Guid operationGuid,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new InstallmentOperationResult<InstallmentAppendPaymentResponse>(false, Message: InstallmentText.L("installment.text.3233494a", "当前版本不支持手动刷卡续付。"), RequiresReview: true));

    /// <summary>付款页放弃了手动刷卡收款窗口：这笔操作不再算进行中，交给恢复或主管结案。</summary>
    void AbandonManualCardRepayment(Guid operationGuid)
    {
    }

    /// <summary>收银员确认没有收款：中央 claim 结案为 Declined，本地操作结案为失败。中央结案失败时返回 false，保持占用。</summary>
    Task<bool> CancelManualCardRepaymentAsync(Guid operationGuid, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    Task<InstallmentOperationResult<InstallmentAppendPaymentResponse>> ExecuteRepaymentAsync(
        PosSessionState session,
        InstallmentAppendPaymentRequest request,
        bool authorizeCard,
        CancellationToken cancellationToken = default);

    Task<InstallmentOperationResult<InstallmentCancelResponse>> ExecuteCancelAsync(
        LocalInstallmentOrder localOrder,
        PosSessionState session,
        string? reason = null,
        InstallmentCancelRefundMode refundMode = InstallmentCancelRefundMode.OriginalRoute,
        CancellationToken cancellationToken = default);

    Task<InstallmentOperationResult<InstallmentConfirmPickupResponse>> ExecutePickupAsync(
        PosSessionState session,
        InstallmentConfirmPickupRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new InstallmentOperationResult<InstallmentConfirmPickupResponse>(
            false,
            Message: InstallmentText.L("installment.text.b098412f", "安全提货确认服务未配置。"),
            RequiresReview: true));

    Task<IReadOnlyList<InstallmentOperationRecoveryResult>> RecoverAsync(
        PosSessionState session,
        CancellationToken cancellationToken = default);

    Task<bool> ResolveRefundStepAsync(
        Guid refundStepGuid,
        InstallmentRefundSupervisorResolution resolution,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LocalInstallmentRefundStep>> GetRefundStepsForReviewAsync(
        Guid installmentGuid,
        CancellationToken cancellationToken = default);

    Task<IReadOnlySet<Guid>> GetLockedInstallmentGuidsAsync(PosSessionState session, CancellationToken cancellationToken = default);

    Task<InstallmentOperationResult<InstallmentCancelResponse>> ResumeCancelAfterSupervisorAsync(
        Guid operationGuid,
        string installmentNumber,
        PosSessionState session,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 分期金融操作协调器。终端调用之外的三个边界均先由仓储开启并提交 SQLite 事务：
/// 提交前、终端批准后，以及 API 成功与快照完成时。
/// </summary>
public sealed class InstallmentOperationService(
    ILocalInstallmentOperationRepository repository,
    IInstallmentApiClient apiClient,
    ICardTerminalClient cardTerminalClient,
    IVoucherTenderClient voucherTenderClient,
    ICardTerminalSettingsProvider? cardTerminalSettingsProvider = null,
    ILocalCardPaymentAttemptRepository? cardPaymentAttemptRepository = null,
    ILinklyPaymentAttemptContextAccessor? linklyPaymentAttemptContextAccessor = null,
    ILocalSquarePaymentAttemptRepository? squarePaymentAttemptRepository = null,
    ISquarePaymentAttemptContextAccessor? squarePaymentAttemptContextAccessor = null,
    FinancialSupervisorAuditReplayService? supervisorAuditReplay = null,
    ISquareTerminalPaymentClient? squareTerminalPaymentClient = null,
    ILinklyBackendTerminalClient? linklyBackendTerminalClient = null,
    ZellerCardTerminalClient? zellerCardTerminalClient = null,
    ZellerPaymentRecoveryService? zellerPaymentRecoveryService = null,
    ILocalZellerPaymentAttemptRepository? zellerPaymentAttemptRepository = null) : IInstallmentOperationService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan ApiClaimLease = TimeSpan.FromMinutes(2);
    // 中文注释：本进程里正在执行的操作。恢复（启动、登录、分期中心、付款页自动复核）随时可能运行，
    // 它会把「已发给终端」的操作当成中断的操作改成结果不明；正在执行的操作必须跳过，否则会打断一笔进行中的收款。
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> _inFlight = new();
    // 开单、取消退款、提货在执行期间按分期单登记：它们的操作号在执行中途才确定。
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, int> _inFlightInstallments = new();
    private readonly string _apiClaimToken = Guid.NewGuid().ToString("N");
    private readonly SemaphoreSlim _createGate = new(1, 1);

    public Task<InstallmentOperationResult<InstallmentCreateResponse>> ExecuteCreateAsync(
        PosSessionState session,
        InstallmentCreateRequest request,
        bool authorizeCard,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => ExecuteCreateCoreAsync(session, request, authorizeCard, cancellationToken), CancellationToken.None);

    private async Task<InstallmentOperationResult<InstallmentCreateResponse>> ExecuteCreateCoreAsync(
        PosSessionState session,
        InstallmentCreateRequest request,
        bool authorizeCard,
        CancellationToken cancellationToken = default)
    {
        using var inFlight = EnterInFlightInstallment(request.InstallmentGuid);
        await _createGate.WaitAsync(cancellationToken);
        try
        {
            var recoverable = await repository.GetRecoverableAsync(session.StoreCode, cancellationToken);
            var unresolvedCreate = recoverable.FirstOrDefault(operation =>
                operation.Kind == LocalInstallmentOperationKind.Create &&
                string.Equals(operation.DeviceCode, session.DeviceCode, StringComparison.OrdinalIgnoreCase) &&
                operation.OperationGuid != request.DownPayment.PaymentGuid);
            if (unresolvedCreate is not null)
            {
                // 同一终端只能推进一个未结创建；新请求必须等待原操作恢复或主管结案。
                return new InstallmentOperationResult<InstallmentCreateResponse>(
                    false,
                    Message: "A previous lay-by creation is still unresolved. Recover it before collecting payment again.",
                    RequiresReview: true);
            }

            var operation = await repository.CreateOrGetAsync(CreateOperation(
                request.DownPayment.PaymentGuid,
                LocalInstallmentOperationKind.Create,
                request.InstallmentGuid,
                request.DownPayment.PaymentGuid,
                request.StoreCode,
                request.DeviceCode,
                request.CashierId,
                EnsureIdempotencyKey(request.DownPayment.IdempotencyKey, request.InstallmentGuid),
                JsonSerializer.Serialize(request, JsonOptions)), cancellationToken);

            var ready = await EnsureCreateTerminalApprovalAsync(operation, session, authorizeCard, cancellationToken);
            if (!ready.Succeeded)
            {
                return new InstallmentOperationResult<InstallmentCreateResponse>(false, Message: ready.Message, RequiresReview: ready.RequiresReview);
            }

            var approvedOperation = ready.Operation!;
            var approvedRequest = Deserialize<InstallmentCreateRequest>(approvedOperation.RequestJson);
            return await SubmitCreateAsync(approvedOperation, approvedRequest, cancellationToken);
        }
        finally
        {
            _createGate.Release();
        }
    }

    public Task<InstallmentOperationResult<InstallmentAppendPaymentResponse>> ExecuteRepaymentAsync(
        PosSessionState session,
        InstallmentAppendPaymentRequest request,
        bool authorizeCard,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => ExecuteRepaymentCoreAsync(session, request, authorizeCard, cancellationToken), CancellationToken.None);

    private IDisposable EnterInFlight(Guid operationGuid)
    {
        _inFlight[operationGuid] = 0;
        return new InFlightScope(this, operationGuid);
    }

    private sealed class InFlightScope(InstallmentOperationService owner, Guid operationGuid) : IDisposable
    {
        public void Dispose() => owner._inFlight.TryRemove(operationGuid, out _);
    }

    private IDisposable EnterInFlightInstallment(Guid installmentGuid)
    {
        _inFlightInstallments.AddOrUpdate(installmentGuid, 1, (_, count) => count + 1);
        return new InFlightInstallmentScope(this, installmentGuid);
    }

    private sealed class InFlightInstallmentScope(InstallmentOperationService owner, Guid installmentGuid) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            if (owner._inFlightInstallments.AddOrUpdate(installmentGuid, 0, (_, count) => count - 1) <= 0)
            {
                owner._inFlightInstallments.TryRemove(new KeyValuePair<Guid, int>(installmentGuid, 0));
            }
        }
    }

    /// <summary>付款页放弃了手动刷卡收款窗口（页面重入）：这笔操作不再算进行中，交给恢复或主管结案。</summary>
    public void AbandonManualCardRepayment(Guid operationGuid) => _inFlight.TryRemove(operationGuid, out _);

    private async Task<InstallmentOperationResult<InstallmentAppendPaymentResponse>> ExecuteRepaymentCoreAsync(
        PosSessionState session,
        InstallmentAppendPaymentRequest request,
        bool authorizeCard,
        CancellationToken cancellationToken = default)
    {
        using var inFlight = EnterInFlight(request.PaymentGuid);
        // 中文注释：调用方的取消只作用于刷卡终端这一步（收银员在付款页点取消）。占 claim、登记通道、提交入账都不随它取消：
        // 这些步骤被打断只会留下结果不明的操作；终端批准之后更不能因为取消而不入账。
        var terminalCancellation = cancellationToken;
        cancellationToken = CancellationToken.None;
        var operation = await repository.CreateOrGetAsync(CreateOperation(
            request.PaymentGuid,
            LocalInstallmentOperationKind.Repayment,
            request.InstallmentGuid,
            request.PaymentGuid,
            request.StoreCode,
            request.DeviceCode,
            request.CashierId,
            EnsureIdempotencyKey(request.IdempotencyKey, request.PaymentGuid),
            JsonSerializer.Serialize(request, JsonOptions)), cancellationToken);
        // 同一 operation 的所有重试只使用首次落盘的不可变请求指纹，忽略后续 UI/body 漂移。
        request = Deserialize<InstallmentAppendPaymentRequest>(operation.RequestJson);

        var claim = await EnsureRepaymentClaimAsync(operation, request, cancellationToken, session);
        if (!claim.Succeeded)
        {
            return new InstallmentOperationResult<InstallmentAppendPaymentResponse>(
                false,
                Message: claim.Message,
                RequiresReview: claim.RequiresReview);
        }

        if (claim.Response is not null)
        {
            return await CompleteCommittedRepaymentAsync(operation, claim.Response, cancellationToken);
        }

        if ((claim.Claim?.Status is InstallmentRepaymentClaimStatus.ProviderPending or InstallmentRepaymentClaimStatus.Unknown) &&
            operation.State is not LocalInstallmentOperationState.TerminalApproved and not LocalInstallmentOperationState.ApiSubmitting)
        {
            if (operation.State is LocalInstallmentOperationState.Prepared or LocalInstallmentOperationState.ResultUnknown)
            {
                var recovery = await RecoverRepaymentAsync(operation, session, cancellationToken);
                var recoveredOperation = await repository.GetAsync(operation.OperationGuid, cancellationToken);
                if (recovery.ReplayedApi &&
                    recoveredOperation?.State == LocalInstallmentOperationState.Completed &&
                    !string.IsNullOrWhiteSpace(recoveredOperation.ResponseJson))
                {
                    var response = Deserialize<InstallmentAppendPaymentResponse>(recoveredOperation.ResponseJson);
                    return new InstallmentOperationResult<InstallmentAppendPaymentResponse>(true, response, ToLocalOrder(response.Details), response.Message);
                }

                return new InstallmentOperationResult<InstallmentAppendPaymentResponse>(false, Message: recovery.Message, RequiresReview: true);
            }

            // 中文注释：中央 claim 已进入 provider 阶段时，只有本机恢复流程可以继续查询同一 attempt；此处绝不能再次授权。
            return new InstallmentOperationResult<InstallmentAppendPaymentResponse>(
                false,
                Message: InstallmentText.L("installment.text.6d34a7ab", "补款 provider 结果正在由发起设备恢复，请勿再次收款。"),
                RequiresReview: true);
        }

        var ready = await EnsureRepaymentTerminalApprovalAsync(operation, session, authorizeCard, terminalCancellation, claim.CardSettings);
        if (!ready.Succeeded)
        {
            return new InstallmentOperationResult<InstallmentAppendPaymentResponse>(
                false,
                Message: ready.Message,
                RequiresReview: ready.RequiresReview,
                AutoRecoveryDueAt: ready.RequiresReview ? await GetZellerLateRecheckDueAsync(operation.OperationGuid) : null);
        }

        var approvedOperation = ready.Operation!;
        var approvedRequest = Deserialize<InstallmentAppendPaymentRequest>(approvedOperation.RequestJson);
        return await SubmitRepaymentAsync(approvedOperation, approvedRequest, CancellationToken.None);
    }

    public Task<InstallmentOperationResult<bool>> BeginManualCardRepaymentAsync(
        PosSessionState session,
        InstallmentAppendPaymentRequest request,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => BeginManualCardRepaymentGuardedAsync(session, request, cancellationToken), CancellationToken.None);

    private async Task<InstallmentOperationResult<bool>> BeginManualCardRepaymentGuardedAsync(
        PosSessionState session,
        InstallmentAppendPaymentRequest request,
        CancellationToken cancellationToken)
    {
        // 中文注释：收款窗口打开期间这笔操作一直算进行中，直到收银员确认、取消或页面放弃；
        // 否则这期间运行的恢复会把它当成中断的操作改成结果不明。续付的操作号就是付款号。
        _inFlight[request.PaymentGuid] = 0;
        var started = false;
        try
        {
            var result = await BeginManualCardRepaymentCoreAsync(session, request, cancellationToken);
            started = result.Succeeded;
            return result;
        }
        finally
        {
            if (!started)
            {
                _inFlight.TryRemove(request.PaymentGuid, out _);
            }
        }
    }

    private async Task<InstallmentOperationResult<bool>> BeginManualCardRepaymentCoreAsync(
        PosSessionState session,
        InstallmentAppendPaymentRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Method != PaymentMethodKind.Card ||
            !ManualCardPaymentReference.IsManual(request.Reference) ||
            request.CardTransactions is { Count: > 0 })
        {
            throw new ArgumentException(InstallmentText.L("installment.text.1ae472b7", "手动刷卡续付请求必须带 MANUAL 引用且不含卡交易证据。"), nameof(request));
        }

        InstallmentRepaymentCapabilitiesResponse capabilities;
        try
        {
            capabilities = await apiClient.GetRepaymentCapabilitiesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new InstallmentOperationResult<bool>(false, Message: InstallmentText.L("installment.text.982c1b23", "无法确认服务端是否支持手动刷卡续付：{0}", exception.Message));
        }

        if (!InstallmentCardRepaymentCapability.IsManualSupported(capabilities))
        {
            return new InstallmentOperationResult<bool>(false, Message: ManualCardRepaymentUnsupportedMessage);
        }

        var operation = await repository.CreateOrGetAsync(CreateOperation(
            request.PaymentGuid,
            LocalInstallmentOperationKind.Repayment,
            request.InstallmentGuid,
            request.PaymentGuid,
            request.StoreCode,
            request.DeviceCode,
            request.CashierId,
            EnsureIdempotencyKey(request.IdempotencyKey, request.PaymentGuid),
            JsonSerializer.Serialize(request, JsonOptions)), cancellationToken);
        request = Deserialize<InstallmentAppendPaymentRequest>(operation.RequestJson);

        var claim = await EnsureRepaymentClaimAsync(operation, request, cancellationToken, session);
        if (!claim.Succeeded)
        {
            return new InstallmentOperationResult<bool>(false, Message: claim.Message, RequiresReview: claim.RequiresReview);
        }

        var attemptId = operation.OperationGuid.ToString("D");
        var begin = await BeginRepaymentProviderAsync(operation, ManualCardPaymentReference.Processor, attemptId, cancellationToken);
        if (!begin.Succeeded)
        {
            return new InstallmentOperationResult<bool>(false, Message: begin.Message, RequiresReview: begin.RequiresReview);
        }

        // 中文注释：从这里起收银员可能已经在独立刷卡机上收款，POS 无法查询；重启或中断后只能由主管结案。
        if (!await repository.TryTransitionAsync(
                operation.OperationGuid,
                [LocalInstallmentOperationState.Prepared],
                LocalInstallmentOperationState.TerminalSubmitting,
                DateTimeOffset.UtcNow,
                terminalAttemptGuid: attemptId,
                terminalProcessor: ManualCardPaymentReference.Processor,
                cancellationToken: CancellationToken.None))
        {
            return new InstallmentOperationResult<bool>(false, Message: InstallmentText.L("installment.text.7edba736", "手动刷卡续付状态已被其他入口推进，请勿收款，到分期中心恢复。"), RequiresReview: true);
        }

        return new InstallmentOperationResult<bool>(true, true);
    }

    public Task<InstallmentOperationResult<InstallmentAppendPaymentResponse>> CompleteManualCardRepaymentAsync(
        Guid operationGuid,
        CancellationToken cancellationToken = default) =>
        Task.Run(async () =>
        {
            try
            {
                return await CompleteManualCardRepaymentCoreAsync(operationGuid, cancellationToken);
            }
            finally
            {
                // 确认之后不论结果如何都不再算进行中：没走完的交给恢复或主管结案。
                _inFlight.TryRemove(operationGuid, out _);
            }
        }, CancellationToken.None);

    private async Task<InstallmentOperationResult<InstallmentAppendPaymentResponse>> CompleteManualCardRepaymentCoreAsync(
        Guid operationGuid,
        CancellationToken cancellationToken)
    {
        var operation = await repository.GetAsync(operationGuid, cancellationToken);
        if (operation is not { Kind: LocalInstallmentOperationKind.Repayment, State: LocalInstallmentOperationState.TerminalSubmitting } ||
            !string.Equals(operation.TerminalProcessor, ManualCardPaymentReference.Processor, StringComparison.Ordinal))
        {
            return new InstallmentOperationResult<InstallmentAppendPaymentResponse>(
                false,
                Message: InstallmentText.L("installment.text.41d3eda5", "手动刷卡续付状态已变化，无法提交；款项已收的话请由主管在分期中心结案。"),
                RequiresReview: true);
        }

        var request = Deserialize<InstallmentAppendPaymentRequest>(operation.RequestJson);
        // 中文注释：证据口径与销售的手动刷卡相同：处理器 Manual，TxnRef 与引用都是 MANUAL:{确认号}。
        var approved = request with
        {
            CardTransactions =
            [
                new CardTransactionDto(
                    ManualCardPaymentReference.Processor,
                    request.Reference,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    "Manually confirmed by cashier",
                    null,
                    null,
                    request.Amount,
                    null)
            ]
        };
        if (!await repository.TryTransitionAsync(
                operationGuid,
                [LocalInstallmentOperationState.TerminalSubmitting],
                LocalInstallmentOperationState.TerminalApproved,
                DateTimeOffset.UtcNow,
                requestJson: JsonSerializer.Serialize(approved, JsonOptions),
                cancellationToken: CancellationToken.None))
        {
            return new InstallmentOperationResult<InstallmentAppendPaymentResponse>(
                false,
                Message: InstallmentText.L("installment.text.16f22cf0", "手动刷卡确认未能保存，请勿重复收款，由主管在分期中心结案。"),
                RequiresReview: true);
        }

        var current = await repository.GetAsync(operationGuid, CancellationToken.None)
            ?? throw new InvalidOperationException(InstallmentText.L("installment.text.2a11ee6b", "分期操作状态未保存。"));
        return await SubmitRepaymentAsync(current, approved, cancellationToken);
    }

    public Task<bool> CancelManualCardRepaymentAsync(Guid operationGuid, CancellationToken cancellationToken = default) =>
        Task.Run(async () =>
        {
            var cancelled = await CancelManualCardRepaymentCoreAsync(operationGuid, cancellationToken);
            if (cancelled)
            {
                _inFlight.TryRemove(operationGuid, out _);
            }

            return cancelled;
        }, CancellationToken.None);

    private async Task<bool> CancelManualCardRepaymentCoreAsync(Guid operationGuid, CancellationToken cancellationToken)
    {
        var operation = await repository.GetAsync(operationGuid, cancellationToken);
        if (operation is not { Kind: LocalInstallmentOperationKind.Repayment, State: LocalInstallmentOperationState.TerminalSubmitting } ||
            !string.Equals(operation.TerminalProcessor, ManualCardPaymentReference.Processor, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            // 中文注释：先结中央 claim，成功才解开本地；反过来的话中央失败时分期单会一直被占用而本地已无记录可恢复。
            await apiClient.ResolveRepaymentClaimAsync(
                operation.InstallmentGuid,
                operationGuid,
                new InstallmentRepaymentClaimResolveRequest(InstallmentRepaymentClaimResolveOutcome.Declined),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogOperationIssue(operationGuid, "manual-card-repayment-cancel", "Kept locked", exception);
            return false;
        }

        return await repository.TryTransitionAsync(
            operationGuid,
            [LocalInstallmentOperationState.TerminalSubmitting],
            LocalInstallmentOperationState.Failed,
            DateTimeOffset.UtcNow,
            failureMessage: InstallmentText.L("installment.text.6ddb1050", "收银员确认手动刷卡未收款。"),
            cancellationToken: CancellationToken.None);
    }

    public Task<InstallmentOperationResult<InstallmentConfirmPickupResponse>> ExecutePickupAsync(
        PosSessionState session,
        InstallmentConfirmPickupRequest request,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => ExecutePickupCoreAsync(session, request, cancellationToken), CancellationToken.None);

    private async Task<InstallmentOperationResult<InstallmentConfirmPickupResponse>> ExecutePickupCoreAsync(
        PosSessionState session,
        InstallmentConfirmPickupRequest request,
        CancellationToken cancellationToken)
    {
        using var inFlight = EnterInFlightInstallment(request.InstallmentGuid);
        var operationGuid = request.OperationGuid == Guid.Empty ? request.InstallmentGuid : request.OperationGuid;
        request = request with
        {
            OperationGuid = operationGuid,
            IdempotencyKey = string.IsNullOrWhiteSpace(request.IdempotencyKey)
                ? $"{operationGuid:D}:pickup"
                : request.IdempotencyKey.Trim()
        };
        var operation = await repository.CreateOrGetAsync(CreateOperation(
            operationGuid,
            LocalInstallmentOperationKind.Pickup,
            request.InstallmentGuid,
            null,
            request.StoreCode,
            request.DeviceCode,
            request.CashierId,
            request.IdempotencyKey!,
            JsonSerializer.Serialize(request, JsonOptions)), cancellationToken);

        if (operation.Kind != LocalInstallmentOperationKind.Pickup ||
            operation.InstallmentGuid != request.InstallmentGuid ||
            !string.Equals(operation.StoreCode, session.StoreCode, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(operation.DeviceCode, session.DeviceCode, StringComparison.OrdinalIgnoreCase))
        {
            return new InstallmentOperationResult<InstallmentConfirmPickupResponse>(
                false,
                Message: InstallmentText.L("installment.text.69f4353f", "提货操作身份与本地记录不一致，保持锁定等待核对。"),
                RequiresReview: true);
        }

        if (operation.State == LocalInstallmentOperationState.Completed &&
            !string.IsNullOrWhiteSpace(operation.ResponseJson))
        {
            var completed = Deserialize<InstallmentConfirmPickupResponse>(operation.ResponseJson);
            return new InstallmentOperationResult<InstallmentConfirmPickupResponse>(
                true,
                completed,
                ToLocalOrder(completed.Details),
                InstallmentText.L("installment.text.18912330", "分期单已确认提货。"));
        }

        // API 已发出或结果未知时只能由恢复流程重放同一幂等请求，普通点击不得再次 POST。
        if (operation.State is LocalInstallmentOperationState.ApiSubmitting or LocalInstallmentOperationState.ResultUnknown)
        {
            return new InstallmentOperationResult<InstallmentConfirmPickupResponse>(
                false,
                Message: InstallmentText.L("installment.text.94836ef0", "提货确认结果未知，已锁定；请刷新恢复，勿重复确认。"),
                RequiresReview: true);
        }

        if (operation.State == LocalInstallmentOperationState.Prepared)
        {
            await repository.TryTransitionAsync(
                operation.OperationGuid,
                [LocalInstallmentOperationState.Prepared],
                LocalInstallmentOperationState.TerminalApproved,
                DateTimeOffset.UtcNow,
                cancellationToken: cancellationToken);
            operation = await repository.GetAsync(operation.OperationGuid, cancellationToken) ?? operation;
        }

        if (operation.State != LocalInstallmentOperationState.TerminalApproved)
        {
            return new InstallmentOperationResult<InstallmentConfirmPickupResponse>(
                false,
                Message: InstallmentText.L("installment.text.d0d69c0c", "提货确认正在恢复或已结束，请刷新核对。"),
                RequiresReview: true);
        }

        return await SubmitPickupAsync(
            operation,
            Deserialize<InstallmentConfirmPickupRequest>(operation.RequestJson),
            cancellationToken);
    }

    public Task<InstallmentOperationResult<InstallmentCancelResponse>> ExecuteCancelAsync(
        LocalInstallmentOrder localOrder,
        PosSessionState session,
        string? reason = null,
        InstallmentCancelRefundMode refundMode = InstallmentCancelRefundMode.OriginalRoute,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => ExecuteCancelCoreAsync(localOrder, session, reason, refundMode, cancellationToken), CancellationToken.None);

    private async Task<InstallmentOperationResult<InstallmentCancelResponse>> ExecuteCancelCoreAsync(
        LocalInstallmentOrder localOrder,
        PosSessionState session,
        string? reason,
        InstallmentCancelRefundMode refundMode,
        CancellationToken cancellationToken)
    {
        using var inFlight = EnterInFlightInstallment(localOrder.InstallmentGuid);
        refundMode = InstallmentLifecycleRules.NormalizeRefundMode(refundMode);
        if (!string.Equals(localOrder.StoreCode, session.StoreCode, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(localOrder.DeviceCode, session.DeviceCode, StringComparison.OrdinalIgnoreCase))
        {
            return new InstallmentOperationResult<InstallmentCancelResponse>(false, Message: InstallmentText.L("installment.text.6f52081f", "取消退款仅允许分期创建设备执行。"));
        }

        var recoverable = await repository.GetRecoverableAsync(session.StoreCode, cancellationToken);
        var existing = recoverable.FirstOrDefault(operation =>
            operation.Kind == LocalInstallmentOperationKind.Cancel &&
            operation.InstallmentGuid == localOrder.InstallmentGuid &&
            string.Equals(operation.DeviceCode, session.DeviceCode, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            return await ContinueCancelAsync(existing, localOrder.InstallmentNumber, session, false, cancellationToken);
        }

        // 中央 claim 的 Released/Declined 是终态；每次真正重试使用新的 operation，不能复活旧 claim。
        var operationGuid = Guid.NewGuid();
        var cancelRequest = new InstallmentCancelRequest(
            localOrder.InstallmentGuid,
            session.StoreCode,
            session.DeviceCode,
            session.CashierId,
            session.CashierName,
            DateTimeOffset.UtcNow,
            [],
            string.IsNullOrWhiteSpace(reason)
                ? refundMode == InstallmentCancelRefundMode.Voucher ? "取消分期并退代金券" : "取消分期并退款"
                : reason.Trim(),
            operationGuid.ToString("D"),
            refundMode);
        var now = DateTimeOffset.UtcNow;
        // 同批退款步骤使用递增 tick 保留原付款顺序，避免数据库退化为哈希 GUID 排序。
        var steps = localOrder.Payments
            .Where(payment => payment.Status == InstallmentPaymentStatus.Recorded && payment.Amount > 0m)
            .Select((payment, index) =>
            {
                var stepCreatedAt = now.AddTicks(index);
                // 退代金券时每笔原付款都改为发券：步骤方式即实际退款方式，不再带原卡引用/卡交易，
                // 避免被当作 Square/Linkly 卡退款去调终端；原付款方式另存，仅用于计算与服务端一致的退款计划指纹。
                var refundsAsVoucher = refundMode == InstallmentCancelRefundMode.Voucher;
                return new LocalInstallmentRefundStep(
                    DeterministicGuid($"refund:{operationGuid:D}:{payment.PaymentGuid:D}"),
                    operationGuid,
                    payment.PaymentGuid,
                    InstallmentLifecycleRules.ResolveRefundMethod(payment.Method, refundMode),
                    payment.Amount,
                    refundsAsVoucher ? null : payment.Reference,
                    $"{operationGuid:D}:refund:{payment.PaymentGuid:D}",
                    LocalInstallmentRefundStepState.Prepared,
                    null,
                    refundsAsVoucher || payment.CardTransactions is null ? null : JsonSerializer.Serialize(payment.CardTransactions, JsonOptions),
                    null, null, null, null, null, null, stepCreatedAt, stepCreatedAt,
                    OriginalMethod: payment.Method);
            })
            .ToList();
        var operation = await repository.CreateCancelOrGetAsync(CreateOperation(
            operationGuid,
            LocalInstallmentOperationKind.Cancel,
            localOrder.InstallmentGuid,
            null,
            session.StoreCode,
            session.DeviceCode,
            session.CashierId,
            cancelRequest.IdempotencyKey!,
            JsonSerializer.Serialize(cancelRequest, JsonOptions)), steps, cancellationToken);

        var claimReady = await EnsureCancelClaimAsync(operation, steps, cancellationToken);
        if (!claimReady.Succeeded)
        {
            return new InstallmentOperationResult<InstallmentCancelResponse>(
                false,
                Message: claimReady.Message,
                RequiresReview: claimReady.RequiresReview);
        }
        if (claimReady.Response is not null)
        {
            return await CompleteCommittedCancelAsync(operation, claimReady.Response, cancellationToken);
        }

        return await ContinueCancelAsync(operation, localOrder.InstallmentNumber, session, false, cancellationToken);
    }

    public Task<IReadOnlyList<InstallmentOperationRecoveryResult>> RecoverAsync(PosSessionState session, CancellationToken cancellationToken = default) =>
        Task.Run(() => RecoverCoreAsync(session, cancellationToken), CancellationToken.None);

    private async Task<IReadOnlyList<InstallmentOperationRecoveryResult>> RecoverCoreAsync(
        PosSessionState session,
        CancellationToken cancellationToken = default,
        Func<LocalInstallmentOperation, bool>? filter = null)
    {
        var recoverable = await repository.GetRecoverableAsync(session.StoreCode, cancellationToken);
        var results = new List<InstallmentOperationRecoveryResult>();
        foreach (var operation in recoverable)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_inFlight.ContainsKey(operation.OperationGuid) ||
                _inFlightInstallments.ContainsKey(operation.InstallmentGuid) ||
                filter?.Invoke(operation) == false)
            {
                // 正在执行的操作不是中断的操作，恢复不能碰它。
                continue;
            }

            if (operation.State == LocalInstallmentOperationState.TerminalSubmitting)
            {
                if (operation.Kind == LocalInstallmentOperationKind.Cancel)
                {
                    // 退款终端调用在重启前中断时，不能假定没有金融副作用；先把每个未落结果的步骤锁定。
                    var refundSteps = await repository.GetRefundStepsAsync(operation.OperationGuid, CancellationToken.None);
                    var hadSubmittingRefund = refundSteps.Any(step => step.State == LocalInstallmentRefundStepState.TerminalSubmitting);
                    foreach (var refundStep in refundSteps.Where(step => step.State == LocalInstallmentRefundStepState.TerminalSubmitting))
                    {
                        await repository.TryTransitionRefundStepAsync(
                            refundStep.RefundStepGuid,
                            [LocalInstallmentRefundStepState.TerminalSubmitting],
                            LocalInstallmentRefundStepState.ResultUnknown,
                            DateTimeOffset.UtcNow,
                            failureMessage: InstallmentText.L("installment.text.a59d838f", "重启时退款终端结果未知，等待主管结案。"),
                            cancellationToken: CancellationToken.None);
                    }

                    await repository.TryTransitionAsync(
                        operation.OperationGuid,
                        [LocalInstallmentOperationState.TerminalSubmitting],
                        LocalInstallmentOperationState.ResultUnknown,
                        DateTimeOffset.UtcNow,
                        failureMessage: InstallmentText.L("installment.text.b95e65fb", "重启时退款终端结果未知，禁止自动重试。"),
                        cancellationToken: CancellationToken.None);
                    var resumedCancel = await repository.GetAsync(operation.OperationGuid, CancellationToken.None);
                    var resumedSteps = await repository.GetRefundStepsAsync(operation.OperationGuid, CancellationToken.None);
                    if (resumedCancel is not null && !hadSubmittingRefund && !resumedSteps.Any(IsRefundApproved))
                    {
                        // operation 已抢占但尚未有任何退款步骤进入 provider；可安全终结旧 claim 并让用户生成新 operation。
                        var declined = await TryResolveCancelClaimAsync(
                            resumedCancel,
                            InstallmentCancelClaimResolveOutcome.Declined,
                            [],
                            CancellationToken.None);
                        if (declined)
                        {
                            await repository.TryTransitionAsync(
                                resumedCancel.OperationGuid,
                                [LocalInstallmentOperationState.ResultUnknown],
                                LocalInstallmentOperationState.Failed,
                                DateTimeOffset.UtcNow,
                                failureMessage: InstallmentText.L("installment.text.e736770d", "重启前尚未调用退款 provider，可重新发起取消。"),
                                cancellationToken: CancellationToken.None);
                            results.Add(new InstallmentOperationRecoveryResult(operation.OperationGuid, operation.Kind, LocalInstallmentOperationState.Failed, false, InstallmentText.L("installment.text.ef814ed5", "尚未调用退款 provider，可重新发起取消。")));
                        }
                        else
                        {
                            results.Add(new InstallmentOperationRecoveryResult(operation.OperationGuid, operation.Kind, LocalInstallmentOperationState.ResultUnknown, false, InstallmentText.L("installment.text.25f7f6b2", "中央取消 claim 结案失败，保持锁定。")));
                        }
                        continue;
                    }
                    if (resumedCancel is not null)
                    {
                        // 中文注释：Square 已落 refundId 的步骤只能查询同一笔退款；重启恢复绝不再次 POST。
                        var rejectedRefundReset = await RecoverSquareRefundStepsAsync(resumedCancel, resumedSteps, session, cancellationToken);
                        resumedSteps = await repository.GetRefundStepsAsync(operation.OperationGuid, CancellationToken.None);
                        if ((rejectedRefundReset || HasSquareRejectedRefundReset(resumedSteps)) &&
                            await TryFinalizeRejectedCancelRecoveryAsync(resumedCancel, resumedSteps))
                        {
                            results.Add(new InstallmentOperationRecoveryResult(operation.OperationGuid, operation.Kind, LocalInstallmentOperationState.Failed, false, InstallmentText.L("installment.text.526dd732", "Square 退款已明确拒绝，可重新发起取消。")));
                            continue;
                        }
                        await TryResolveCancelClaimAsync(
                            resumedCancel,
                            InstallmentCancelClaimResolveOutcome.Unknown,
                            BuildApprovedRefundCommands(resumedSteps),
                            CancellationToken.None);
                    }
                    if (resumedCancel is not null && resumedSteps.All(IsRefundApproved))
                    {
                        results.Add(await RecoverCancelAsync(resumedCancel, session, cancellationToken));
                    }
                    else
                    {
                        results.Add(new InstallmentOperationRecoveryResult(operation.OperationGuid, operation.Kind, LocalInstallmentOperationState.ResultUnknown, false, InstallmentText.L("installment.text.f939e643", "退款结果未知，保持锁定。")));
                    }
                    continue;
                }

                await repository.TryTransitionAsync(
                    operation.OperationGuid,
                    [LocalInstallmentOperationState.TerminalSubmitting],
                    LocalInstallmentOperationState.ResultUnknown,
                    DateTimeOffset.UtcNow,
                    failureMessage: InstallmentText.L("installment.text.7adf4ff0", "重启时终端结果未知，禁止自动重试。"),
                    cancellationToken: cancellationToken);
                var resumed = await repository.GetAsync(operation.OperationGuid, cancellationToken);
                if (resumed is not null && !string.IsNullOrWhiteSpace(resumed.TerminalAttemptGuid))
                {
                    var resumedResult = resumed.Kind switch
                    {
                        LocalInstallmentOperationKind.Create => await RecoverCreateAsync(resumed, session, cancellationToken),
                        LocalInstallmentOperationKind.Repayment => await RecoverRepaymentAsync(resumed, session, cancellationToken),
                        _ => new InstallmentOperationRecoveryResult(resumed.OperationGuid, resumed.Kind, LocalInstallmentOperationState.ResultUnknown, false, InstallmentText.L("installment.text.691f6fef", "终端结果未知，保持锁定。"))
                    };
                    results.Add(resumedResult);
                    continue;
                }

                results.Add(new InstallmentOperationRecoveryResult(operation.OperationGuid, operation.Kind, LocalInstallmentOperationState.ResultUnknown, false, InstallmentText.L("installment.text.691f6fef", "终端结果未知，保持锁定。")));
                continue;
            }

            if (operation.State == LocalInstallmentOperationState.Prepared)
            {
                if (operation.Kind == LocalInstallmentOperationKind.Pickup)
                {
                    results.Add(await RecoverPickupAsync(operation, cancellationToken));
                    continue;
                }

                if (operation.Kind == LocalInstallmentOperationKind.Repayment)
                {
                    results.Add(await RecoverRepaymentAsync(operation, session, cancellationToken));
                    continue;
                }

                results.Add(new InstallmentOperationRecoveryResult(operation.OperationGuid, operation.Kind, operation.State, false, InstallmentText.L("installment.text.03308b74", "未提交终端的草稿保留给用户继续。")));
                continue;
            }

            try
            {
                var result = operation.Kind switch
                {
                    LocalInstallmentOperationKind.Create => await RecoverCreateAsync(operation, session, cancellationToken),
                    LocalInstallmentOperationKind.Repayment => await RecoverRepaymentAsync(operation, session, cancellationToken),
                    LocalInstallmentOperationKind.Cancel => await RecoverCancelAsync(operation, session, cancellationToken),
                    LocalInstallmentOperationKind.Pickup => await RecoverPickupAsync(operation, cancellationToken),
                    _ => new InstallmentOperationRecoveryResult(operation.OperationGuid, operation.Kind, operation.State, false, InstallmentText.L("installment.text.79ef15cd", "退款步骤需由取消操作处理。"))
                };
                results.Add(result);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogOperationIssue(operation.OperationGuid, $"recover-{operation.Kind}", "ResultUnknown", exception);
                await repository.TryTransitionAsync(operation.OperationGuid,
                    [LocalInstallmentOperationState.TerminalApproved, LocalInstallmentOperationState.ApiSubmitting, LocalInstallmentOperationState.ResultUnknown],
                    LocalInstallmentOperationState.ResultUnknown,
                    DateTimeOffset.UtcNow,
                    failureMessage: exception.Message,
                    cancellationToken: cancellationToken);
                results.Add(new InstallmentOperationRecoveryResult(operation.OperationGuid, operation.Kind, LocalInstallmentOperationState.ResultUnknown, false, InstallmentText.L("installment.text.2ad96bbc", "恢复 API 结果未知，保持锁定。")));
            }
        }

        return results;
    }

    public Task<bool> ResolveRefundStepAsync(
        Guid refundStepGuid,
        InstallmentRefundSupervisorResolution resolution,
        CancellationToken cancellationToken = default) =>
        Task.Run(
            () => ResolveRefundStepCoreAsync(refundStepGuid, resolution, cancellationToken),
            CancellationToken.None);

    private async Task<bool> ResolveRefundStepCoreAsync(
        Guid refundStepGuid,
        InstallmentRefundSupervisorResolution resolution,
        CancellationToken cancellationToken)
    {
        var step = await repository.GetRefundStepAsync(refundStepGuid, cancellationToken);
        var operation = step is null
            ? null
            : await repository.GetAsync(step.OperationGuid, cancellationToken);
        if (step is null || operation is null)
        {
            return false;
        }

        var environment = !string.IsNullOrWhiteSpace(step.ProviderEnvironment)
            ? step.ProviderEnvironment
            : cardTerminalSettingsProvider is null
                ? "Unknown"
                : (await cardTerminalSettingsProvider.GetSettingsAsync(cancellationToken)).Environment.ToString();
        var resolvedAt = DateTimeOffset.UtcNow;
        var journal = BuildInstallmentRefundSupervisorJournal(
            operation,
            step,
            resolution,
            environment,
            resolvedAt);
        var applied = await repository.ResolveRefundStepWithJournalAsync(
            refundStepGuid,
            resolution,
            journal,
            resolvedAt,
            CancellationToken.None);
        if (applied && supervisorAuditReplay is not null)
        {
            await supervisorAuditReplay.PersistAfterCommitAsync(journal, CancellationToken.None);
        }

        return applied;
    }

    public Task<IReadOnlyList<LocalInstallmentRefundStep>> GetRefundStepsForReviewAsync(Guid installmentGuid, CancellationToken cancellationToken = default) =>
        Task.Run(() => repository.GetRefundStepsForInstallmentAsync(installmentGuid, cancellationToken), CancellationToken.None);

    public Task<IReadOnlySet<Guid>> GetLockedInstallmentGuidsAsync(PosSessionState session, CancellationToken cancellationToken = default) =>
        Task.Run(() => GetLockedInstallmentGuidsCoreAsync(session, cancellationToken), CancellationToken.None);

    private async Task<IReadOnlySet<Guid>> GetLockedInstallmentGuidsCoreAsync(PosSessionState session, CancellationToken cancellationToken = default)
    {
        var operations = await repository.GetRecoverableAsync(session.StoreCode, cancellationToken);
        return operations
            .Where(operation => operation.State is LocalInstallmentOperationState.ResultUnknown or LocalInstallmentOperationState.TerminalSubmitting or LocalInstallmentOperationState.ApiSubmitting)
            .Select(operation => operation.InstallmentGuid)
            .ToHashSet();
    }

    public Task<InstallmentOperationResult<InstallmentCancelResponse>> ResumeCancelAfterSupervisorAsync(
        Guid operationGuid,
        string installmentNumber,
        PosSessionState session,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => ResumeCancelAfterSupervisorCoreAsync(operationGuid, installmentNumber, session, cancellationToken), CancellationToken.None);

    private async Task<InstallmentOperationResult<InstallmentCancelResponse>> ResumeCancelAfterSupervisorCoreAsync(
        Guid operationGuid,
        string installmentNumber,
        PosSessionState session,
        CancellationToken cancellationToken = default)
    {
        var operation = await repository.GetAsync(operationGuid, cancellationToken);
        using var inFlight = operation is null ? null : EnterInFlightInstallment(operation.InstallmentGuid);
        if (operation is null || operation.Kind != LocalInstallmentOperationKind.Cancel)
        {
            return new InstallmentOperationResult<InstallmentCancelResponse>(false, Message: InstallmentText.L("installment.text.67190cad", "未找到待主管结案的取消操作。"));
        }

        return await ContinueCancelAsync(operation, installmentNumber, session, true, cancellationToken);
    }

    /// <param name="recoverySession">
    /// 给出时，分期单被占用（BUSY）会先恢复本机在这张分期单上没结案的续付，再重试一次。
    /// 只有收银员发起的顶层调用才传；恢复流程内部不传，避免两笔旧操作互相恢复。
    /// </param>
    private async Task<RepaymentClaimReady> EnsureRepaymentClaimAsync(
        LocalInstallmentOperation operation,
        InstallmentAppendPaymentRequest request,
        CancellationToken cancellationToken,
        PosSessionState? recoverySession = null)
    {
        try
        {
            var capabilities = await apiClient.GetRepaymentCapabilitiesAsync(cancellationToken);
            if (!capabilities.RepaymentClaimsSupported)
            {
                return RepaymentClaimReady.Failed(InstallmentText.L("installment.text.22df2843", "当前服务端不支持安全补款 claim，已在 provider 调用前停止。"), requiresReview: true);
            }

            var claim = await apiClient.CreateRepaymentClaimAsync(
                operation.InstallmentGuid,
                new InstallmentRepaymentClaimCreateRequest(
                    operation.OperationGuid,
                    request.PaymentGuid,
                    request.Amount,
                    request.Method,
                    operation.IdempotencyKey),
                cancellationToken);
            if (claim.Status == InstallmentRepaymentClaimStatus.Committed)
            {
                return claim.Commit is null
                    ? RepaymentClaimReady.Failed(InstallmentText.L("installment.text.a6daa5ba", "中央补款已提交但缺少结果快照，保持锁定等待对账。"), requiresReview: true)
                    : RepaymentClaimReady.Committed(claim, claim.Commit);
            }

            if (claim.Status is InstallmentRepaymentClaimStatus.Released or InstallmentRepaymentClaimStatus.Declined)
            {
                await repository.TryTransitionAsync(
                    operation.OperationGuid,
                    [LocalInstallmentOperationState.Prepared],
                    LocalInstallmentOperationState.Failed,
                    DateTimeOffset.UtcNow,
                    failureMessage: InstallmentText.L("installment.text.dba15a93", "中央补款 claim 已结束：{0}", claim.Status),
                    cancellationToken: CancellationToken.None);
                return RepaymentClaimReady.Failed(InstallmentText.L("installment.text.d8cd7ea7", "原补款 claim 已结束，请重新发起付款。"), requiresReview: false);
            }

            if (claim.Status == InstallmentRepaymentClaimStatus.Prepared &&
                request.Method == PaymentMethodKind.Card &&
                request.CardTransactions is not { Count: > 0 } &&
                !ManualCardPaymentReference.IsManual(request.Reference) &&
                cardTerminalSettingsProvider is not null)
            {
                // 中文注释：服务端按通道放开刷卡续付。只在中央 claim 仍是 Prepared（尚未登记任何通道、刷卡机没收到请求）时
                // 判断本机通道；不支持就当场释放 claim 并结案，否则 begin-provider 会被拒、claim 要等 120 秒过期才解锁。
                var settings = await cardTerminalSettingsProvider.GetSettingsAsync(cancellationToken);
                var rejection = !InstallmentCardRepaymentCapability.IsSupported(capabilities, settings.Processor)
                    ? UnsupportedCardRepaymentMessage
                    : settings.Processor == CardProcessorKind.Zeller && ToCents(request.Amount) < ZellerMinimumPurchaseCents
                        ? ZellerBelowMinimumMessage
                        : null;
                if (rejection is not null)
                {
                    return await FailUnsupportedCardRepaymentAsync(operation, "repayment-claim-create", rejection)
                        ? RepaymentClaimReady.Failed(rejection, requiresReview: false)
                        : RepaymentClaimReady.Failed(InstallmentText.L("installment.text.97208fb3", "分期续付刷卡未开始，但本地状态未能更新，请到分期中心恢复。"), requiresReview: true);
                }

                // 中文注释：这次读到的设置就是本次分期全程使用的快照，交给刷卡 attempt，不再重读。
                return RepaymentClaimReady.Success(claim, settings);
            }

            return RepaymentClaimReady.Success(claim);
        }
        catch (CatalogApiException exception) when (exception.ErrorCode == "INSTALLMENT_REPAYMENT_PAYMENT_METHOD_UNSUPPORTED")
        {
            // 中文注释：服务端在读取/写入任何 claim 之前就按付款方式拒绝（当前即银行卡补款），本机也尚未调用 provider；
            // 这是确定性拒绝，必须把本地操作结案为 Failed 并解锁付款页，否则收银员既删不掉 tender 也退不出页面。
            LogOperationIssue(operation.OperationGuid, "repayment-claim-create", "Failed (method unsupported, provider not called)", exception);
            await repository.TryTransitionAsync(
                operation.OperationGuid,
                [LocalInstallmentOperationState.Prepared],
                LocalInstallmentOperationState.Failed,
                DateTimeOffset.UtcNow,
                failureMessage: exception.Message,
                cancellationToken: CancellationToken.None);
            return RepaymentClaimReady.Failed(
                request.Method == PaymentMethodKind.Card
                    ? InstallmentText.L("installment.text.80ae4aad", "分期续付暂不支持刷卡，本机未调用刷卡终端，请改用现金或礼券。")
                    : InstallmentText.L("installment.text.75796655", "服务端不支持该补款方式，本机未调用 provider，请改用其他付款方式。"),
                requiresReview: false);
        }
        catch (CatalogApiException exception) when (exception.ErrorCode is
            "INSTALLMENT_REPAYMENT_BUSY" or
            "INSTALLMENT_REPAYMENT_CLAIM_MISMATCH")
        {
            LogOperationIssue(operation.OperationGuid, "repayment-claim-create", "Failed (provider not called)", exception);
            var busyMessage = InstallmentText.L("installment.text.31395ba8", "这张分期单还有一笔付款没有结束（可能在其他收银机上），本次没有收款。请稍后再试，或到分期中心查看。");
            if (exception.ErrorCode == "INSTALLMENT_REPAYMENT_BUSY" && recoverySession is not null)
            {
                // 中文注释：占用这张分期单的常常是本机自己之前没结案的续付（例如刷卡超时后关了程序）。
                // 先把它恢复出结论：确认没收款就重试这一次；仍然不明就如实告诉收银员去恢复或找主管。
                var own = await RecoverOwnBlockingRepaymentsAsync(operation, recoverySession);
                if (own.Resolved)
                {
                    return await EnsureRepaymentClaimAsync(operation, request, cancellationToken);
                }

                if (own.Paid)
                {
                    // 之前那笔其实收到了款并已入账：收银员这次多半是在重试同一笔，不能接着再收一次。
                    busyMessage = InstallmentText.L("installment.text.ce2676c8", "本机之前那笔付款已确认收款并入账，本次没有再收款。请重新打开这张分期单核对余额。");
                }
                else if (own.StillPending)
                {
                    busyMessage = InstallmentText.L("installment.text.7ae1434d", "这张分期单在本机还有一笔付款结果不明，本次没有收款。请到分期中心恢复，或请主管结案。");
                }
            }

            await repository.TryTransitionAsync(
                operation.OperationGuid,
                [LocalInstallmentOperationState.Prepared],
                LocalInstallmentOperationState.Failed,
                DateTimeOffset.UtcNow,
                failureMessage: exception.Message,
                cancellationToken: CancellationToken.None);
            return RepaymentClaimReady.Failed(
                exception.ErrorCode == "INSTALLMENT_REPAYMENT_BUSY"
                    ? busyMessage
                    : InstallmentText.L("installment.text.a7f622f1", "补款 claim 与本地付款指纹不一致，本机未调用 provider。"),
                requiresReview: false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogOperationIssue(operation.OperationGuid, "repayment-claim-create", "Stopped before provider", exception);
            return RepaymentClaimReady.Failed(InstallmentText.L("installment.text.4aa9dbc0", "创建补款 claim 失败，已在 provider 调用前停止：{0}", exception.Message), requiresReview: true);
        }
    }

    /// <summary>
    /// 恢复本机在同一张分期单上没结案的其他续付。Resolved：都确认没有收款并已结案；Paid：其中有一笔确认收款并入账；
    /// StillPending：还有结果不明的。
    /// </summary>
    private async Task<(bool Resolved, bool StillPending, bool Paid)> RecoverOwnBlockingRepaymentsAsync(
        LocalInstallmentOperation current,
        PosSessionState session)
    {
        try
        {
            bool IsOwnBlocking(LocalInstallmentOperation candidate) =>
                candidate.Kind == LocalInstallmentOperationKind.Repayment &&
                candidate.InstallmentGuid == current.InstallmentGuid &&
                candidate.OperationGuid != current.OperationGuid &&
                string.Equals(candidate.DeviceCode, current.DeviceCode, StringComparison.OrdinalIgnoreCase);

            var results = await RecoverCoreAsync(session, CancellationToken.None, IsOwnBlocking);
            var paid = results.Any(result => result.State == LocalInstallmentOperationState.Completed);
            var stillPending = results.Any(result => result.State is not (LocalInstallmentOperationState.Failed or LocalInstallmentOperationState.Completed));
            var notCharged = results.Any(result => result.State == LocalInstallmentOperationState.Failed);
            return (notCharged && !paid && !stillPending, stillPending, paid);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            LogOperationIssue(current.OperationGuid, "repayment-claim-busy-recover", "Ignored", exception);
            return (false, false, false);
        }
    }

    private async Task<TerminalReady> BeginRepaymentProviderAsync(
        LocalInstallmentOperation operation,
        string provider,
        string providerAttemptId,
        CancellationToken cancellationToken)
    {
        try
        {
            var claim = await apiClient.BeginRepaymentProviderAsync(
                operation.InstallmentGuid,
                operation.OperationGuid,
                new InstallmentRepaymentClaimBeginProviderRequest(provider, providerAttemptId),
                cancellationToken);
            return claim.Status == InstallmentRepaymentClaimStatus.ProviderPending
                ? TerminalReady.Success(operation)
                : TerminalReady.Unknown(InstallmentText.L("installment.text.14ee598d", "中央补款 claim 状态为 {0}，禁止调用 provider。", claim.Status));
        }
        catch (CatalogApiException exception) when (exception.ErrorCode == PaymentMethodUnsupportedErrorCode)
        {
            // 中文注释：服务端只在首次登记通道时按开关拒绝（已登记的恢复重放不会走到这里），此时 claim 仍是 Prepared、
            // 刷卡机没收到请求：确定性拒绝，本地结案并释放 claim，付款页随即解锁。
            LogOperationIssue(operation.OperationGuid, "repayment-begin-provider", "Failed (provider not enabled, provider not called)", exception);
            return await FailUnsupportedCardRepaymentAsync(operation, "repayment-begin-provider")
                ? TerminalReady.Failed(UnsupportedCardRepaymentMessage)
                : TerminalReady.Unknown(InstallmentText.L("installment.text.722fb6e2", "登记补款 provider 被拒，未调用 provider：{0}", exception.Message));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogOperationIssue(operation.OperationGuid, "repayment-begin-provider", "Unknown (provider not called)", exception);
            return TerminalReady.Unknown(InstallmentText.L("installment.text.8abf7609", "登记补款 provider 失败，未调用 provider：{0}", exception.Message));
        }
    }

    private const string PaymentMethodUnsupportedErrorCode = "INSTALLMENT_REPAYMENT_PAYMENT_METHOD_UNSUPPORTED";

    private static string ManualCardRepaymentUnsupportedMessage => InstallmentText.L("installment.text.6f176121", "分期续付暂不支持手动刷卡，请勿收款，请改用其他付款方式。");

    private static string UnsupportedCardRepaymentMessage => InstallmentText.L("installment.text.b6d9beb7", "本机刷卡通道暂不支持分期续付，刷卡终端未收到请求，请改用现金或礼券。");

    /// <summary>Zeller 单笔扣款最低 100 分（1.00 澳元），与付款页同口径（CashPaymentWorkflowService.ZellerMinimumPurchaseCents）。</summary>
    internal const int ZellerMinimumPurchaseCents = CashPaymentWorkflowService.ZellerMinimumPurchaseCents;

    internal static string ZellerBelowMinimumMessage => InstallmentText.L("installment.text.9ae4ed54", "Zeller 单笔刷卡最低 $1.00，刷卡终端未收到请求，请改用现金或礼券。");

    private static string CancelledBeforeTerminalMessage => InstallmentText.L("installment.text.a6bb64fc", "已取消，刷卡终端未收到请求，没有扣款。");

    /// <summary>这笔续付的 Zeller 扣款按未付款收尾、但还排着迟到复核时，返回复核时间；其余情况返回 null。</summary>
    private async Task<DateTimeOffset?> GetZellerLateRecheckDueAsync(Guid operationGuid)
    {
        if (zellerPaymentAttemptRepository is null)
        {
            return null;
        }

        try
        {
            var attempt = await zellerPaymentAttemptRepository.GetAsync(ZellerRepaymentAttemptGuid(operationGuid), CancellationToken.None);
            return attempt is { Status: ZellerAttemptStatus.Canceled, LateRecheckDueAt: not null, LateRecheckedAt: null }
                ? attempt.LateRecheckDueAt
                : null;
        }
        catch (Exception exception)
        {
            LogOperationIssue(operationGuid, "zeller-late-recheck-due", "Ignored", exception);
            return null;
        }
    }

    private static string ZellerAwaitingLateRecheckMessage =>
        InstallmentText.L("installment.text.d6cb7d45", "Zeller 暂未查到这笔扣款（顾客取消或刷卡超时）。系统约 1 分钟后会自动向 Zeller 复核一次，复核前请勿重新收款。");

    private static int ToCents(decimal amount) =>
        (int)decimal.Round(amount * 100m, 0, MidpointRounding.AwayFromZero);

    /// <summary>
    /// 刷卡终端尚未收到请求、中央 claim 尚未登记通道时的确定性拒绝：本地 Prepared → Failed，再尽力释放 claim
    /// （释放失败也无妨，Prepared claim 120 秒后自动过期）。本地状态已被其他入口推进时返回 false，交给恢复处理。
    /// </summary>
    private async Task<bool> FailUnsupportedCardRepaymentAsync(
        LocalInstallmentOperation operation,
        string stage,
        string? message = null)
    {
        message ??= UnsupportedCardRepaymentMessage;
        if (!await repository.TryTransitionAsync(
                operation.OperationGuid,
                [LocalInstallmentOperationState.Prepared],
                LocalInstallmentOperationState.Failed,
                DateTimeOffset.UtcNow,
                failureMessage: message,
                cancellationToken: CancellationToken.None))
        {
            return false;
        }

        try
        {
            await apiClient.ResolveRepaymentClaimAsync(
                operation.InstallmentGuid,
                operation.OperationGuid,
                new InstallmentRepaymentClaimResolveRequest(InstallmentRepaymentClaimResolveOutcome.Released),
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            LogOperationIssue(operation.OperationGuid, $"{stage}-release", "Release failed (claim expires automatically)", exception);
        }

        return true;
    }

    private async Task TryResolveRepaymentClaimAsync(
        LocalInstallmentOperation operation,
        InstallmentRepaymentClaimResolveOutcome outcome,
        CancellationToken cancellationToken)
    {
        try
        {
            await apiClient.ResolveRepaymentClaimAsync(
                operation.InstallmentGuid,
                operation.OperationGuid,
                new InstallmentRepaymentClaimResolveRequest(outcome),
                cancellationToken);
        }
        catch (Exception exception)
        {
            // 中文注释：provider 已产生或可能产生副作用时，resolve 失败不能解锁本地 operation；后续仅允许本机按相同 claim 恢复。
            LogOperationIssue(operation.OperationGuid, $"repayment-claim-resolve-{outcome}", "ResultUnknown", exception);
            await repository.TryTransitionAsync(
                operation.OperationGuid,
                [LocalInstallmentOperationState.TerminalSubmitting, LocalInstallmentOperationState.ResultUnknown],
                LocalInstallmentOperationState.ResultUnknown,
                DateTimeOffset.UtcNow,
                failureMessage: InstallmentText.L("installment.text.a481b4bf", "中央补款 claim 结案失败：{0}", exception.Message),
                cancellationToken: CancellationToken.None);
        }
    }

    private static string? ResolveRepaymentProvider(InstallmentAppendPaymentRequest request) =>
        request.Method == PaymentMethodKind.Card
            ? NormalizeRepaymentCardProvider(request.CardTransactions?.FirstOrDefault()?.Processor)
            : request.Method.ToString();

    private static string? NormalizeRepaymentCardProvider(string? processor)
    {
        if (string.IsNullOrWhiteSpace(processor))
        {
            return null;
        }

        var value = processor.Trim();
        if (value.StartsWith("Square", StringComparison.OrdinalIgnoreCase))
        {
            return "Square";
        }
        if (value.StartsWith("Linkly", StringComparison.OrdinalIgnoreCase))
        {
            return "Linkly";
        }
        if (value.StartsWith("ANZ", StringComparison.OrdinalIgnoreCase))
        {
            return "ANZ";
        }
        if (value.StartsWith("Zeller", StringComparison.OrdinalIgnoreCase))
        {
            return "Zeller";
        }
        if (value.StartsWith(ManualCardPaymentReference.Processor, StringComparison.OrdinalIgnoreCase))
        {
            return ManualCardPaymentReference.Processor;
        }

        return null;
    }

    private static string ResolveRepaymentProviderAttemptId(LocalInstallmentOperation operation, InstallmentAppendPaymentRequest request) =>
        operation.TerminalAttemptGuid ??
        (request.Method == PaymentMethodKind.Card
            ? request.CardTransactions?.FirstOrDefault()?.TxnRef
            : null) ??
        operation.OperationGuid.ToString("D");

    private async Task<InstallmentOperationResult<InstallmentAppendPaymentResponse>> CompleteCommittedRepaymentAsync(
        LocalInstallmentOperation operation,
        InstallmentAppendPaymentResponse response,
        CancellationToken cancellationToken)
    {
        var local = ToLocalOrder(response.Details);
        var current = await repository.GetAsync(operation.OperationGuid, cancellationToken) ?? operation;
        if (current.State != LocalInstallmentOperationState.Completed &&
            !await repository.CompleteWithSnapshotAsync(
                operation.OperationGuid,
                [current.State],
                local,
                JsonSerializer.Serialize(response, JsonOptions),
                false,
                DateTimeOffset.UtcNow,
                cancellationToken))
        {
            return new InstallmentOperationResult<InstallmentAppendPaymentResponse>(false, Message: InstallmentText.L("installment.text.ba3b5ce5", "中央补款已提交，本地快照正在恢复。"), RequiresReview: true);
        }

        await TryAcknowledgeLinklyBackendSessionAsync(operation.OperationGuid);
        await TryMarkZellerRepaymentCompletedAsync(operation.OperationGuid);
        return new InstallmentOperationResult<InstallmentAppendPaymentResponse>(true, response, local, response.Message);
    }

    private async Task<CancelClaimReady> EnsureCancelClaimAsync(
        LocalInstallmentOperation operation,
        IReadOnlyList<LocalInstallmentRefundStep> steps,
        CancellationToken cancellationToken)
    {
        try
        {
            var fingerprint = CreateCancelRefundPlanFingerprint(operation.InstallmentGuid, steps);
            var capabilities = await apiClient.GetRepaymentCapabilitiesAsync(cancellationToken);
            if (!capabilities.CancelClaimsSupported)
            {
                return CancelClaimReady.Failed(InstallmentText.L("installment.text.111b3e0d", "当前服务端不支持安全取消 claim，已在退款前停止。"), requiresReview: true);
            }

            InstallmentCancelClaimDto claim;
            try
            {
                claim = await apiClient.GetCancelClaimAsync(operation.InstallmentGuid, operation.OperationGuid, cancellationToken);
            }
            catch (CatalogApiException exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                if (!CanRecreateMissingCancelClaim(operation, steps))
                {
                    var message = InstallmentText.L("installment.text.4e4ed3c9", "远端取消 claim 缺失，但本地已越过退款 provider 前边界；保持锁定并等待人工对账。");
                    await repository.TryTransitionAsync(
                        operation.OperationGuid,
                        [operation.State],
                        LocalInstallmentOperationState.ResultUnknown,
                        DateTimeOffset.UtcNow,
                        failureMessage: message,
                        cancellationToken: CancellationToken.None);
                    return CancelClaimReady.Failed(message, requiresReview: true);
                }

                var request = Deserialize<InstallmentCancelRequest>(operation.RequestJson);
                claim = await apiClient.CreateCancelClaimAsync(
                    operation.InstallmentGuid,
                    new InstallmentCancelClaimCreateRequest(
                        operation.OperationGuid,
                        operation.IdempotencyKey,
                        request.Reason,
                        fingerprint,
                        InstallmentLifecycleRules.NormalizeRefundMode(request.RefundMode)),
                    cancellationToken);
            }

            if (!IsMatchingCancelClaim(operation, fingerprint, claim))
            {
                return CancelClaimReady.Failed(InstallmentText.L("installment.text.c96243d0", "取消 claim 与本地耐久退款计划不一致，未调用退款 provider。"), requiresReview: true);
            }

            if (claim.Status == InstallmentCancelClaimStatus.Committed)
            {
                return claim.Commit is null
                    ? CancelClaimReady.Failed(InstallmentText.L("installment.text.b05221ba", "中央取消已提交但缺少结果快照，保持锁定等待对账。"), requiresReview: true)
                    : CancelClaimReady.Committed(claim, ToCancelResponse(operation.InstallmentGuid, claim.Commit));
            }

            if (claim.Status is InstallmentCancelClaimStatus.Released or InstallmentCancelClaimStatus.Declined)
            {
                await repository.TryTransitionAsync(
                    operation.OperationGuid,
                    [operation.State],
                    LocalInstallmentOperationState.Failed,
                    DateTimeOffset.UtcNow,
                    failureMessage: InstallmentText.L("installment.text.62ef41b7", "中央取消 claim 已结束：{0}", claim.Status),
                    cancellationToken: CancellationToken.None);
                return CancelClaimReady.Failed(InstallmentText.L("installment.text.1973944d", "原取消 claim 已结束，请重新发起取消。"), requiresReview: false);
            }

            return CancelClaimReady.Success(claim);
        }
        catch (CatalogApiException exception) when (exception.ErrorCode is
            "INSTALLMENT_MUTATION_BUSY" or
            "INSTALLMENT_CANCEL_CLAIM_MISMATCH" or
            "INSTALLMENT_CANCEL_REFUND_METHOD_UNSUPPORTED")
        {
            LogOperationIssue(operation.OperationGuid, "cancel-claim-ensure", "Failed (refund provider not called)", exception);
            var safeToRestart = CanSafelyTerminateCancelBeforeRefund(operation, steps);
            var terminated = safeToRestart && await TryMarkCancelFailedAsync(operation, exception.Message);
            if (!terminated)
            {
                await LockCancelForReviewAsync(operation, exception.Message);
            }

            var message = exception.ErrorCode switch
            {
                "INSTALLMENT_MUTATION_BUSY" => InstallmentText.L("installment.text.1a82c84c", "该分期正在执行付款或取消，本机未调用退款 provider。"),
                "INSTALLMENT_CANCEL_REFUND_METHOD_UNSUPPORTED" => InstallmentText.L("installment.text.2fa17d59", "当前服务端不支持该退款方式，本机未调用退款 provider。"),
                _ => InstallmentText.L("installment.text.757be352", "取消 claim 与服务端权威退款计划不一致，本机未调用退款 provider。")
            };
            return CancelClaimReady.Failed(
                message,
                requiresReview: !terminated);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogOperationIssue(operation.OperationGuid, "cancel-claim-ensure", "Stopped before refund", exception);
            return CancelClaimReady.Failed(InstallmentText.L("installment.text.192cf011", "创建或读取取消 claim 失败，已在退款前停止：{0}", exception.Message), requiresReview: true);
        }
    }

    private async Task<CancelClaimReady> BeginCancelRefundAsync(
        LocalInstallmentOperation operation,
        InstallmentCancelClaimDto claim,
        CancellationToken cancellationToken)
    {
        if (claim.Status == InstallmentCancelClaimStatus.RefundPending)
        {
            return CancelClaimReady.Success(claim);
        }

        if (claim.Status is not InstallmentCancelClaimStatus.Prepared and not InstallmentCancelClaimStatus.Unknown)
        {
            return CancelClaimReady.Failed(InstallmentText.L("installment.text.df538790", "中央取消 claim 状态为 {0}，禁止调用退款 provider。", claim.Status), requiresReview: true);
        }

        try
        {
            var begun = await apiClient.BeginCancelRefundAsync(operation.InstallmentGuid, operation.OperationGuid, cancellationToken);
            return begun.Status == InstallmentCancelClaimStatus.RefundPending
                ? CancelClaimReady.Success(begun)
                : CancelClaimReady.Failed(InstallmentText.L("installment.text.df538790", "中央取消 claim 状态为 {0}，禁止调用退款 provider。", begun.Status), requiresReview: true);
        }
        catch (CatalogApiException exception) when (exception.ErrorCode == "INSTALLMENT_CANCEL_REFUND_METHOD_UNSUPPORTED")
        {
            LogOperationIssue(operation.OperationGuid, "cancel-begin-refund", "Failed (refund provider not called)", exception);
            var steps = await repository.GetRefundStepsAsync(operation.OperationGuid, CancellationToken.None);
            if (claim.Status != InstallmentCancelClaimStatus.Prepared ||
                !CanSafelyTerminateCancelBeforeRefund(operation, steps))
            {
                await LockCancelForReviewAsync(operation, exception.Message);
                return CancelClaimReady.Failed(InstallmentText.L("installment.text.e5777112", "取消退款方式不受支持，但本地已越过安全终结边界；保持锁定等待人工对账。"), requiresReview: true);
            }

            var released = await TryResolveCancelClaimAsync(
                operation,
                InstallmentCancelClaimResolveOutcome.Released,
                [],
                CancellationToken.None);
            if (!released)
            {
                await LockCancelForReviewAsync(operation, InstallmentText.L("installment.text.124af4f8", "中央取消 claim 释放失败，保持锁定等待人工对账。"));
                return CancelClaimReady.Failed(InstallmentText.L("installment.text.124af4f8", "中央取消 claim 释放失败，保持锁定等待人工对账。"), requiresReview: true);
            }

            if (!await TryMarkCancelFailedAsync(operation, InstallmentText.L("installment.text.b0704eb7", "当前服务端不支持该退款方式，中央 claim 已安全释放。")))
            {
                await LockCancelForReviewAsync(operation, InstallmentText.L("installment.text.6b43e4bc", "中央取消 claim 已释放，但本地终态落盘失败。"));
                return CancelClaimReady.Failed(InstallmentText.L("installment.text.6b43e4bc", "中央取消 claim 已释放，但本地终态落盘失败。"), requiresReview: true);
            }

            return CancelClaimReady.Failed(InstallmentText.L("installment.text.c3bfe1a3", "当前服务端不支持该退款方式，本机未调用退款 provider，可重新发起取消。"), requiresReview: false);
        }
        catch (CatalogApiException exception) when (exception.ErrorCode == "INSTALLMENT_CANCEL_CLAIM_EXPIRED")
        {
            LogOperationIssue(operation.OperationGuid, "cancel-begin-refund", "Claim expired, re-reading", exception);
            try
            {
                var steps = await repository.GetRefundStepsAsync(operation.OperationGuid, CancellationToken.None);
                var current = await apiClient.GetCancelClaimAsync(operation.InstallmentGuid, operation.OperationGuid, CancellationToken.None);
                var fingerprint = CreateCancelRefundPlanFingerprint(operation.InstallmentGuid, steps);
                if (!IsMatchingCancelClaim(operation, fingerprint, current) ||
                    current.Status is not InstallmentCancelClaimStatus.Released and not InstallmentCancelClaimStatus.Declined ||
                    !CanSafelyTerminateCancelBeforeRefund(operation, steps))
                {
                    await LockCancelForReviewAsync(operation, InstallmentText.L("installment.text.93d725ff", "取消 claim 过期后的远端终态无法安全确认。"));
                    return CancelClaimReady.Failed(InstallmentText.L("installment.text.4243b08a", "取消 claim 过期后的远端终态无法安全确认，保持锁定等待人工对账。"), requiresReview: true);
                }

                if (!await TryMarkCancelFailedAsync(operation, InstallmentText.L("installment.text.62ef41b7", "中央取消 claim 已结束：{0}", current.Status)))
                {
                    await LockCancelForReviewAsync(operation, InstallmentText.L("installment.text.2dd4fea8", "中央取消 claim 已结束，但本地终态落盘失败。"));
                    return CancelClaimReady.Failed(InstallmentText.L("installment.text.2dd4fea8", "中央取消 claim 已结束，但本地终态落盘失败。"), requiresReview: true);
                }

                return CancelClaimReady.Failed(InstallmentText.L("installment.text.4a598eaa", "原取消 claim 已过期结束，请重新发起取消。"), requiresReview: false);
            }
            catch (Exception refreshException)
            {
                LogOperationIssue(operation.OperationGuid, "cancel-claim-refresh", "Locked for review", refreshException);
                await LockCancelForReviewAsync(operation, InstallmentText.L("installment.text.51edd211", "取消 claim 过期后重新读取失败：{0}", refreshException.Message));
                return CancelClaimReady.Failed(InstallmentText.L("installment.text.8debe136", "取消 claim 过期后的远端状态读取失败，保持锁定等待人工对账。"), requiresReview: true);
            }
        }
        catch (Exception exception)
        {
            LogOperationIssue(operation.OperationGuid, "cancel-begin-refund", "Failed (refund provider not called)", exception);
            return CancelClaimReady.Failed(InstallmentText.L("installment.text.8811e19b", "登记取消退款阶段失败，未调用退款 provider：{0}", exception.Message), requiresReview: true);
        }
    }

    private async Task<bool> TryResolveCancelClaimAsync(
        LocalInstallmentOperation operation,
        InstallmentCancelClaimResolveOutcome outcome,
        IReadOnlyList<InstallmentRefundPaymentCommandDto> approvedRefunds,
        CancellationToken cancellationToken)
    {
        try
        {
            var resolved = await apiClient.ResolveCancelClaimAsync(
                operation.InstallmentGuid,
                operation.OperationGuid,
                new InstallmentCancelClaimResolveRequest(outcome, approvedRefunds),
                cancellationToken);
            return outcome switch
            {
                InstallmentCancelClaimResolveOutcome.Released => resolved.Status == InstallmentCancelClaimStatus.Released,
                InstallmentCancelClaimResolveOutcome.Declined => resolved.Status == InstallmentCancelClaimStatus.Declined,
                _ => resolved.Status == InstallmentCancelClaimStatus.Unknown
            };
        }
        catch (Exception exception)
        {
            // provider 已产生或可能产生副作用时，中央结案失败不能解锁本地操作。
            LogOperationIssue(operation.OperationGuid, $"cancel-claim-resolve-{outcome}", "ResultUnknown", exception);
            await repository.TryTransitionAsync(
                operation.OperationGuid,
                [LocalInstallmentOperationState.TerminalSubmitting, LocalInstallmentOperationState.ResultUnknown],
                LocalInstallmentOperationState.ResultUnknown,
                DateTimeOffset.UtcNow,
                failureMessage: InstallmentText.L("installment.text.206bc23a", "中央取消 claim 结案失败：{0}", exception.Message),
                cancellationToken: CancellationToken.None);
            return false;
        }
    }

    private async Task<InstallmentOperationResult<InstallmentCancelResponse>> CompleteCommittedCancelAsync(
        LocalInstallmentOperation operation,
        InstallmentCancelResponse response,
        CancellationToken cancellationToken)
    {
        var local = ToLocalOrder(response.Details);
        var current = await repository.GetAsync(operation.OperationGuid, cancellationToken) ?? operation;
        if (current.State != LocalInstallmentOperationState.Completed &&
            !await repository.CompleteWithSnapshotAsync(
                operation.OperationGuid,
                [current.State],
                local,
                JsonSerializer.Serialize(response, JsonOptions),
                true,
                DateTimeOffset.UtcNow,
                cancellationToken))
        {
            return new InstallmentOperationResult<InstallmentCancelResponse>(false, Message: InstallmentText.L("installment.text.1842b64b", "中央取消已提交，本地快照正在恢复。"), RequiresReview: true);
        }

        return new InstallmentOperationResult<InstallmentCancelResponse>(true, response, local, response.Message);
    }

    private async Task<TerminalReady> EnsureCreateTerminalApprovalAsync(LocalInstallmentOperation operation, PosSessionState session, bool authorizeCard, CancellationToken cancellationToken)
    {
        var request = Deserialize<InstallmentCreateRequest>(operation.RequestJson);
        if (!authorizeCard || request.DownPayment.Method != PaymentMethodKind.Card || request.DownPayment.CardTransactions is { Count: > 0 })
        {
            return await MarkTerminalApprovedAsync(operation, operation.RequestJson, cancellationToken);
        }

        var claim = await repository.TryTransitionAsync(operation.OperationGuid, [LocalInstallmentOperationState.Prepared], LocalInstallmentOperationState.TerminalSubmitting, DateTimeOffset.UtcNow, cancellationToken: cancellationToken);
        if (!claim)
        {
            return await ReadReadyOrUnknownAsync(operation.OperationGuid, cancellationToken);
        }

        return await ApproveCardAsync(
            operation,
            request.DownPayment.Amount,
            session,
            authorization => JsonSerializer.Serialize(request with
            {
                DownPayment = request.DownPayment with
                {
                    Amount = authorization.AuthorizedAmount ?? request.DownPayment.Amount,
                    Reference = authorization.Reference ?? request.DownPayment.Reference,
                    CardTransactions = authorization.CardTransactions
                }
            }, JsonOptions),
            cancellationToken);
    }

    private async Task<TerminalReady> EnsureRepaymentTerminalApprovalAsync(LocalInstallmentOperation operation, PosSessionState session, bool authorizeCard, CancellationToken cancellationToken, CardTerminalSettings? settingsSnapshot = null)
    {
        var request = Deserialize<InstallmentAppendPaymentRequest>(operation.RequestJson);
        if (request.Method == PaymentMethodKind.Voucher && string.IsNullOrWhiteSpace(request.ReservationToken))
        {
            return await EnsureVoucherRepaymentApprovalAsync(operation, request, session, cancellationToken);
        }

        if (!authorizeCard || request.Method != PaymentMethodKind.Card || request.CardTransactions is { Count: > 0 })
        {
            var provider = ResolveRepaymentProvider(request);
            if (string.IsNullOrWhiteSpace(provider))
            {
                return TerminalReady.Unknown(InstallmentText.L("installment.text.df8c2574", "无法确定具体银行卡处理器，未登记 claim begin 且未调用付款 provider。"));
            }

            var begin = await BeginRepaymentProviderAsync(
                operation,
                provider,
                ResolveRepaymentProviderAttemptId(operation, request),
                cancellationToken);
            if (!begin.Succeeded)
            {
                return begin;
            }

            return await MarkTerminalApprovedAsync(operation, operation.RequestJson, cancellationToken);
        }

        return await ApproveCardAsync(
            operation,
            request.Amount,
            session,
            authorization => JsonSerializer.Serialize(request with
            {
                Amount = authorization.AuthorizedAmount ?? request.Amount,
                Reference = authorization.Reference ?? request.Reference,
                CardTransactions = authorization.CardTransactions
            }, JsonOptions),
            cancellationToken,
            beginRepaymentClaim: true,
            settingsSnapshot: settingsSnapshot);
    }

    private async Task<TerminalReady> EnsureVoucherRepaymentApprovalAsync(
        LocalInstallmentOperation operation,
        InstallmentAppendPaymentRequest request,
        PosSessionState session,
        CancellationToken cancellationToken)
    {
        var providerAttemptId = operation.OperationGuid.ToString("D");
        var begin = await BeginRepaymentProviderAsync(operation, PaymentMethodKind.Voucher.ToString(), providerAttemptId, cancellationToken);
        if (!begin.Succeeded)
        {
            return begin;
        }

        if (!await repository.TryTransitionAsync(
                operation.OperationGuid,
                [LocalInstallmentOperationState.Prepared],
                LocalInstallmentOperationState.TerminalSubmitting,
                DateTimeOffset.UtcNow,
                terminalAttemptGuid: providerAttemptId,
                terminalProcessor: PaymentMethodKind.Voucher.ToString(),
                cancellationToken: CancellationToken.None))
        {
            // 另一恢复入口可能已进入 provider；本地竞争失败不能把中央 claim 错误解锁为 Declined。
            return TerminalReady.Unknown(InstallmentText.L("installment.text.24022f87", "礼券补款正在恢复，请勿重复预占。"));
        }

        try
        {
            var authorization = await voucherTenderClient.RedeemAsync(request.Amount, session, request.Reference, cancellationToken);
            if (authorization.ResultUnknown)
            {
                await repository.TryTransitionAsync(
                    operation.OperationGuid,
                    [LocalInstallmentOperationState.TerminalSubmitting],
                    LocalInstallmentOperationState.ResultUnknown,
                    DateTimeOffset.UtcNow,
                    failureMessage: authorization.Message ?? InstallmentText.L("installment.text.42c72ff1", "礼券预占结果未知。"),
                    cancellationToken: CancellationToken.None);
                await TryResolveRepaymentClaimAsync(operation, InstallmentRepaymentClaimResolveOutcome.Unknown, CancellationToken.None);
                return TerminalReady.Unknown(InstallmentText.L("installment.text.f74a5270", "礼券预占结果未知，请勿重复操作。"));
            }

            if (!authorization.Approved || !HasExactAuthorizedAmount(authorization, request.Amount))
            {
                var (lockedVoucherCode, lockedReservationToken) = OrderUploadService.ParseVoucherReference(authorization.Reference);
                if (!string.IsNullOrWhiteSpace(lockedVoucherCode) && !string.IsNullOrWhiteSpace(lockedReservationToken))
                {
                    // Voucher redeem 是预占。即使批准金额不足，只要返回了 token，就必须先确认释放后才能把 claim 结为 Declined。
                    var released = await voucherTenderClient.ReleaseAsync(
                        session,
                        lockedVoucherCode,
                        lockedReservationToken,
                        CancellationToken.None);
                    if (!released)
                    {
                        await repository.TryTransitionAsync(
                            operation.OperationGuid,
                            [LocalInstallmentOperationState.TerminalSubmitting],
                            LocalInstallmentOperationState.ResultUnknown,
                            DateTimeOffset.UtcNow,
                            failureMessage: InstallmentText.L("installment.text.93971090", "礼券不足额预占未确认释放。"),
                            cancellationToken: CancellationToken.None);
                        await TryResolveRepaymentClaimAsync(operation, InstallmentRepaymentClaimResolveOutcome.Unknown, CancellationToken.None);
                        return TerminalReady.Unknown(InstallmentText.L("installment.text.57096cf9", "礼券预占释放结果未知，保持锁定等待对账。"));
                    }
                }
                else if (authorization.Approved)
                {
                    await repository.TryTransitionAsync(
                        operation.OperationGuid,
                        [LocalInstallmentOperationState.TerminalSubmitting],
                        LocalInstallmentOperationState.ResultUnknown,
                        DateTimeOffset.UtcNow,
                        failureMessage: InstallmentText.L("installment.text.370367c3", "礼券已部分批准但缺少可释放的 reservation token。"),
                        cancellationToken: CancellationToken.None);
                    await TryResolveRepaymentClaimAsync(operation, InstallmentRepaymentClaimResolveOutcome.Unknown, CancellationToken.None);
                    return TerminalReady.Unknown(InstallmentText.L("installment.text.55e56500", "礼券部分批准证据不完整，保持锁定等待对账。"));
                }

                await repository.TryTransitionAsync(
                    operation.OperationGuid,
                    [LocalInstallmentOperationState.TerminalSubmitting],
                    LocalInstallmentOperationState.Failed,
                    DateTimeOffset.UtcNow,
                    failureMessage: authorization.Message ?? InstallmentText.L("installment.text.4f337346", "礼券未获批准。"),
                    cancellationToken: CancellationToken.None);
                await TryResolveRepaymentClaimAsync(operation, InstallmentRepaymentClaimResolveOutcome.Declined, CancellationToken.None);
                return TerminalReady.Failed(authorization.Message ?? InstallmentText.L("installment.text.4f337346", "礼券未获批准。"));
            }

            var (voucherCode, reservationToken) = OrderUploadService.ParseVoucherReference(authorization.Reference);
            if (string.IsNullOrWhiteSpace(voucherCode) || string.IsNullOrWhiteSpace(reservationToken))
            {
                await repository.TryTransitionAsync(
                    operation.OperationGuid,
                    [LocalInstallmentOperationState.TerminalSubmitting],
                    LocalInstallmentOperationState.ResultUnknown,
                    DateTimeOffset.UtcNow,
                    failureMessage: InstallmentText.L("installment.text.a7b3b9e7", "礼券已批准但缺少可恢复的 reservation token。"),
                    cancellationToken: CancellationToken.None);
                await TryResolveRepaymentClaimAsync(operation, InstallmentRepaymentClaimResolveOutcome.Unknown, CancellationToken.None);
                return TerminalReady.Unknown(InstallmentText.L("installment.text.1b4926a6", "礼券批准证据不完整，保持锁定等待对账。"));
            }

            var approvedRequest = request with
            {
                Amount = authorization.AuthorizedAmount ?? request.Amount,
                Reference = voucherCode,
                ReservationToken = reservationToken
            };
            if (!await repository.TryTransitionAsync(
                    operation.OperationGuid,
                    [LocalInstallmentOperationState.TerminalSubmitting],
                    LocalInstallmentOperationState.TerminalApproved,
                    DateTimeOffset.UtcNow,
                    requestJson: JsonSerializer.Serialize(approvedRequest, JsonOptions),
                    cancellationToken: CancellationToken.None))
            {
                await TryResolveRepaymentClaimAsync(operation, InstallmentRepaymentClaimResolveOutcome.Unknown, CancellationToken.None);
                return TerminalReady.Unknown(InstallmentText.L("installment.text.03724916", "礼券批准结果未能持久化，保持锁定等待恢复。"));
            }

            var approved = await repository.GetAsync(operation.OperationGuid, CancellationToken.None)
                ?? throw new InvalidOperationException(InstallmentText.L("installment.text.3bbc2593", "礼券补款批准状态未保存。"));
            return TerminalReady.Success(approved);
        }
        catch (Exception exception)
        {
            LogOperationIssue(operation.OperationGuid, "repayment-voucher-redeem", "ResultUnknown", exception);
            await repository.TryTransitionAsync(
                operation.OperationGuid,
                [LocalInstallmentOperationState.TerminalSubmitting],
                LocalInstallmentOperationState.ResultUnknown,
                DateTimeOffset.UtcNow,
                failureMessage: exception.Message,
                cancellationToken: CancellationToken.None);
            await TryResolveRepaymentClaimAsync(operation, InstallmentRepaymentClaimResolveOutcome.Unknown, CancellationToken.None);
            return TerminalReady.Unknown(InstallmentText.L("installment.text.f74a5270", "礼券预占结果未知，请勿重复操作。"));
        }
    }

    private async Task<TerminalReady> ApproveCardAsync(LocalInstallmentOperation operation, decimal amount, PosSessionState session, Func<PaymentAuthorizationResult, string> approvedRequestFactory, CancellationToken cancellationToken, bool beginRepaymentClaim = false, CardTerminalSettings? settingsSnapshot = null)
    {
        TerminalAttemptScope? terminalAttempt = null;
        try
        {
            // 中文注释：续付时，取消只作用于终端调用本身；读设置、落 attempt、登记通道这些准备步骤照常做完，
            // 这样取消后的状态是确定的（要么没发给终端，要么交给终端结果）。
            var setupCancellation = beginRepaymentClaim ? CancellationToken.None : cancellationToken;
            terminalAttempt = await CreateTerminalAttemptScopeAsync(operation, amount, session, setupCancellation, settingsSnapshot);
            if (terminalAttempt.IsUnsupportedProcessor)
            {
                // 中文注释：Zeller 暂不支持分期。刷卡机不会收到任何请求，也还没登记还款 claim。
                // 开单路径进到这里时操作已经是 TerminalSubmitting（EnsureCreateTerminalApprovalAsync 先推进再调用），
                // 必须和银行拒绝一样落成 Failed：否则重启后会变成「结果未知」，并挡住这台机器后续所有分期开单。
                // 还款路径这时仍是 Prepared，没有推进过，不用回退。
                var unsupportedMessage = InstallmentText.L("installment.text.07ce2c61", "Zeller 刷卡暂不支持分期付款，请改用其他付款方式。");
                if (!beginRepaymentClaim &&
                    !await repository.TryTransitionAsync(
                        operation.OperationGuid,
                        [LocalInstallmentOperationState.TerminalSubmitting],
                        LocalInstallmentOperationState.Failed,
                        DateTimeOffset.UtcNow,
                        terminalProcessor: CardProcessorKind.Zeller.ToString(),
                        failureMessage: unsupportedMessage,
                        cancellationToken: CancellationToken.None))
                {
                    // 本地状态没写成功（另一个入口已经推进了它）：交给恢复处理。这笔确实没有发给刷卡机。
                    return TerminalReady.Unknown(InstallmentText.L("installment.text.fa40e7f0", "Zeller 刷卡暂不支持分期付款，刷卡机没有收到请求；分期操作状态未能更新，请到分期中心恢复。"));
                }

                return TerminalReady.Failed(unsupportedMessage);
            }
            if (terminalAttempt.RequiresReview)
            {
                var reviewMessage = terminalAttempt.ReviewMessage ?? InstallmentText.L("installment.text.125be6d0", "银行卡 attempt 无法按当前终端模式安全复用，保持锁定等待主管恢复。");
                var expectedReviewStates = beginRepaymentClaim
                    ? new[] { LocalInstallmentOperationState.Prepared }
                    : new[] { LocalInstallmentOperationState.TerminalSubmitting };
                var locked = await repository.TryTransitionAsync(
                    operation.OperationGuid,
                    expectedReviewStates,
                    LocalInstallmentOperationState.ResultUnknown,
                    DateTimeOffset.UtcNow,
                    failureMessage: reviewMessage,
                    cancellationToken: CancellationToken.None);
                if (beginRepaymentClaim && locked)
                {
                    await TryResolveRepaymentClaimAsync(operation, InstallmentRepaymentClaimResolveOutcome.Unknown, CancellationToken.None);
                }
                return TerminalReady.Unknown(reviewMessage);
            }

            var providerAttemptId = terminalAttempt.AttemptGuid?.ToString("D") ?? operation.OperationGuid.ToString("D");
            var provider = NormalizeRepaymentCardProvider(terminalAttempt.Processor);
            if (beginRepaymentClaim)
            {
                if (string.IsNullOrWhiteSpace(provider))
                {
                    return TerminalReady.Unknown(InstallmentText.L("installment.text.df8c2574", "无法确定具体银行卡处理器，未登记 claim begin 且未调用付款 provider。"));
                }

                var begin = await BeginRepaymentProviderAsync(operation, provider, providerAttemptId, setupCancellation);
                if (!begin.Succeeded)
                {
                    return begin;
                }
            }

            var expectedStates = beginRepaymentClaim
                ? new[] { LocalInstallmentOperationState.Prepared }
                : new[] { LocalInstallmentOperationState.TerminalSubmitting };
            if (!await repository.TryTransitionAsync(
                    operation.OperationGuid,
                    expectedStates,
                    LocalInstallmentOperationState.TerminalSubmitting,
                    DateTimeOffset.UtcNow,
                    terminalAttemptGuid: providerAttemptId,
                    terminalProcessor: provider,
                    cancellationToken: CancellationToken.None))
            {
                // 另一恢复入口可能已进入 provider；本地竞争失败不能把中央 claim 错误解锁为 Declined。
                return TerminalReady.Unknown(InstallmentText.L("installment.text.9d04b65f", "终端操作正在恢复，请勿重复收款。"));
            }

            if (beginRepaymentClaim && cancellationToken.IsCancellationRequested)
            {
                // 中文注释：收银员在请求发给刷卡终端之前就点了取消：确定没有扣款，按明确取消结案，可以马上重新收款。
                var cancelledBeforeTerminal = new PaymentAuthorizationResult(
                    false,
                    Message: CancelledBeforeTerminalMessage,
                    Processor: terminalAttempt.Processor,
                    ResponseText: "CANCELLED");
                await terminalAttempt.RecordOutcomeAsync(cancelledBeforeTerminal, CancellationToken.None);
                if (!await repository.TryTransitionAsync(
                        operation.OperationGuid,
                        [LocalInstallmentOperationState.TerminalSubmitting],
                        LocalInstallmentOperationState.Failed,
                        DateTimeOffset.UtcNow,
                        terminalProcessor: provider,
                        failureMessage: CancelledBeforeTerminalMessage,
                        cancellationToken: CancellationToken.None))
                {
                    return TerminalReady.Unknown(InstallmentText.L("installment.text.9d04b65f", "终端操作正在恢复，请勿重复收款。"));
                }

                await TryResolveRepaymentClaimAsync(operation, InstallmentRepaymentClaimResolveOutcome.Declined, CancellationToken.None);
                return TerminalReady.Failed(CancelledBeforeTerminalMessage);
            }

            using var terminalContext = terminalAttempt.BeginContext();
            var authorization = await (terminalAttempt.AuthorizeAsync(amount, session, cancellationToken)
                ?? cardTerminalClient.AuthorizeAsync(amount, session, cancellationToken));
            var terminalAttemptGuid = terminalAttempt.AttemptGuid?.ToString("D");
            if (authorization.ResultUnknown)
            {
                await terminalAttempt.RecordOutcomeAsync(authorization, CancellationToken.None);
                await repository.TryTransitionAsync(
                    operation.OperationGuid,
                    [LocalInstallmentOperationState.TerminalSubmitting],
                    LocalInstallmentOperationState.ResultUnknown,
                    DateTimeOffset.UtcNow,
                    terminalAttemptGuid: terminalAttemptGuid,
                    terminalProcessor: authorization.Processor ?? terminalAttempt.Processor,
                    cancellationToken: CancellationToken.None);
                if (beginRepaymentClaim)
                {
                    await TryResolveRepaymentClaimAsync(operation, InstallmentRepaymentClaimResolveOutcome.Unknown, CancellationToken.None);
                }
                return TerminalReady.Unknown(authorization.Message ?? InstallmentText.L("installment.text.e6a65c34", "终端结果未知，请勿重复收款。"));
            }

            if (!authorization.Approved)
            {
                await terminalAttempt.RecordOutcomeAsync(authorization, CancellationToken.None);
                if (!await repository.TryTransitionAsync(operation.OperationGuid, [LocalInstallmentOperationState.TerminalSubmitting], LocalInstallmentOperationState.Failed, DateTimeOffset.UtcNow, terminalProcessor: authorization.Processor, failureMessage: authorization.Message, cancellationToken: CancellationToken.None))
                {
                    return TerminalReady.Unknown("Terminal rejection could not be durably recorded; payment remains locked.");
                }
                if (beginRepaymentClaim)
                {
                    await TryResolveRepaymentClaimAsync(operation, InstallmentRepaymentClaimResolveOutcome.Declined, CancellationToken.None);
                }
                // 明确拒绝同样会留下未确认的终态会话，不 ack 会让下一笔刷卡被 409 拦下。
                await TryAcknowledgeLinklyBackendSessionAsync(operation.OperationGuid);
                return TerminalReady.Failed(authorization.Message ?? InstallmentText.L("installment.text.af388ae7", "银行卡未获批准。"));
            }

            if (!HasExactAuthorizedAmount(authorization, amount) || !terminalAttempt.HasVerifiedApprovalEvidence(authorization))
            {
                // 中文注释：批准证据不完整时禁止把 attempt 标为 Approved，重启只能继续查证，不能据请求金额补造成功。
                await terminalAttempt.RecordOutcomeAsync(authorization with { Approved = false, ResultUnknown = true }, CancellationToken.None);
                await repository.TryTransitionAsync(
                    operation.OperationGuid,
                    [LocalInstallmentOperationState.TerminalSubmitting],
                    LocalInstallmentOperationState.ResultUnknown,
                    DateTimeOffset.UtcNow,
                    terminalAttemptGuid: terminalAttemptGuid,
                    terminalProcessor: authorization.Processor ?? terminalAttempt.Processor,
                    failureMessage: "Terminal approved amount did not match the persisted lay-by amount.",
                    cancellationToken: CancellationToken.None);
                if (beginRepaymentClaim)
                {
                    await TryResolveRepaymentClaimAsync(operation, InstallmentRepaymentClaimResolveOutcome.Unknown, CancellationToken.None);
                }
                return TerminalReady.Unknown(InstallmentText.L("installment.text.371eab73", "终端批准金额与分期金额不一致，保持锁定，请勿重复收款。"));
            }

            await terminalAttempt.RecordOutcomeAsync(authorization, CancellationToken.None);
            var requestJson = approvedRequestFactory(authorization);
            if (!await repository.TryTransitionAsync(operation.OperationGuid, [LocalInstallmentOperationState.TerminalSubmitting], LocalInstallmentOperationState.TerminalApproved, DateTimeOffset.UtcNow, requestJson: requestJson, terminalAttemptGuid: terminalAttemptGuid ?? authorization.SessionId ?? authorization.TxnRef, terminalProcessor: authorization.Processor ?? terminalAttempt.Processor, cancellationToken: CancellationToken.None))
            {
                return TerminalReady.Unknown("Terminal approval could not be durably recorded; payment remains locked.");
            }
            var approved = await repository.GetAsync(operation.OperationGuid, CancellationToken.None)
                ?? throw new InvalidOperationException(InstallmentText.L("installment.text.17ac8fb3", "终端批准状态未保存。"));
            return TerminalReady.Success(approved);
        }
        catch (OperationCanceledException)
        {
            await repository.TryTransitionAsync(
                operation.OperationGuid,
                [LocalInstallmentOperationState.TerminalSubmitting],
                LocalInstallmentOperationState.ResultUnknown,
                DateTimeOffset.UtcNow,
                terminalAttemptGuid: terminalAttempt?.AttemptGuid?.ToString("D"),
                terminalProcessor: terminalAttempt?.Processor,
                cancellationToken: CancellationToken.None);
            if (beginRepaymentClaim)
            {
                await TryResolveRepaymentClaimAsync(operation, InstallmentRepaymentClaimResolveOutcome.Unknown, CancellationToken.None);
            }
            return TerminalReady.Unknown(InstallmentText.L("installment.text.e6a65c34", "终端结果未知，请勿重复收款。"));
        }
        catch (Exception exception)
        {
            LogOperationIssue(operation.OperationGuid, "card-authorize", "ResultUnknown", exception);
            await repository.TryTransitionAsync(
                operation.OperationGuid,
                [LocalInstallmentOperationState.TerminalSubmitting],
                LocalInstallmentOperationState.ResultUnknown,
                DateTimeOffset.UtcNow,
                terminalAttemptGuid: terminalAttempt?.AttemptGuid?.ToString("D"),
                terminalProcessor: terminalAttempt?.Processor,
                failureMessage: exception.Message,
                cancellationToken: CancellationToken.None);
            if (beginRepaymentClaim)
            {
                await TryResolveRepaymentClaimAsync(operation, InstallmentRepaymentClaimResolveOutcome.Unknown, CancellationToken.None);
            }
            return TerminalReady.Unknown(InstallmentText.L("installment.text.e6a65c34", "终端结果未知，请勿重复收款。"));
        }
        finally
        {
            terminalAttempt?.Dispose();
        }
    }

    private async Task<TerminalAttemptScope> CreateTerminalAttemptScopeAsync(
        LocalInstallmentOperation operation,
        decimal amount,
        PosSessionState session,
        CancellationToken cancellationToken,
        CardTerminalSettings? settingsSnapshot = null)
    {
        if (cardTerminalSettingsProvider is null)
        {
            return TerminalAttemptScope.Empty;
        }

        var settings = settingsSnapshot ?? await cardTerminalSettingsProvider.GetSettingsAsync(cancellationToken);
        if (settings.Processor == CardProcessorKind.Zeller)
        {
            // 中文注释：Zeller 只接分期续付（分期中心开单收定金仍不支持）。续付由 operationGuid 派生尝试号，
            // 先登记到中央 claim 再调用 SDK；结果未知时只按同一条记录查询，绝不重发（Zeller 不对 reference 去重）。
            if (operation.Kind != LocalInstallmentOperationKind.Repayment ||
                zellerCardTerminalClient is null ||
                zellerPaymentAttemptRepository is null)
            {
                return TerminalAttemptScope.UnsupportedZeller;
            }

            return CreateZellerRepaymentScope(operation);
        }

        var now = DateTimeOffset.UtcNow;
        if (settings.Processor == CardProcessorKind.Linkly && cardPaymentAttemptRepository is not null && linklyPaymentAttemptContextAccessor is not null)
        {
            var mode = CardTerminalSettings.NormalizeLinklyConnectionMode(settings.LinklyConnectionMode);
            var attemptGuid = DeterministicGuid($"installment-linkly:{operation.OperationGuid:D}");
            var attempt = await cardPaymentAttemptRepository.GetAttemptAsync(attemptGuid, cancellationToken);
            if (attempt is null)
            {
                attempt = new LocalCardPaymentAttempt(
                    attemptGuid,
                    null,
                    // 中文注释：LocalIp 与 CloudBackendAsync 都必须在发请求前确定引用并随 attempt 落库。
                    // 后端模式过去为 null，POST 已受理而响应丢失（或断电）时，本地 attempt 既无 SessionId 也无 TxnRef，
                    // 恢复无法按引用认领会话；现在与销售一致，由 LinklyBackendTerminalClient 把 attempt 身份作为
                    // AttemptGuid 交给后端，后端用同一算法派生出相同引用。CloudDirectSync 的引用仍由直连客户端按
                    // attempt 身份稳定派生并在拿到 session 后绑定，这里保持不变。
                    mode is LinklyConnectionMode.LocalIp or LinklyConnectionMode.CloudBackendAsync
                        ? LinklyLocalTxnRef.Create('P', attemptGuid.ToString("D"))
                        : null,
                    CardProcessorKind.Linkly.ToString(),
                    settings.Environment.ToString(),
                    CardTerminalSettings.FormatLinklyConnectionMode(mode),
                    "P",
                    amount,
                    LocalCardPaymentAttemptStatus.Pending,
                    operation.RequestJson,
                    session.StoreCode,
                    session.DeviceCode,
                    session.CashierId,
                    null,
                    null,
                    null,
                    now,
                    now,
                    null,
                    null,
                    operation.Kind.ToString(),
                    operation.OperationGuid);
                await cardPaymentAttemptRepository.CreateAsync(attempt, cancellationToken);
            }
            else if (RequiresLinklyPurchaseRecoveryForCurrentMode(attempt, mode))
            {
                // 中文注释：重启复用必须信任同一 settings 快照；模式或 LocalIp 引用不安全时，不得让适配器临时补号。
                return TerminalAttemptScope.ForReview(
                    attempt.AttemptGuid,
                    attempt.Processor,
                    InstallmentText.L("installment.text.8d14b04e", "持久化的银行卡 attempt 与当前 Linkly 连接模式或 Local IP 引用不一致，保持锁定等待主管恢复。"));
            }

            var attemptContext = new LinklyPaymentAttemptContext(
                attempt.AttemptGuid,
                // 中文注释：终端已返回 session 后，UI 取消不能丢失恢复所需的绑定证据。
                (sessionId, txnRef, updatedAt, _) =>
                    cardPaymentAttemptRepository.UpdateSessionAsync(attempt.AttemptGuid, sessionId, txnRef, updatedAt, CancellationToken.None),
                attempt.TxnRef)
            {
                SettingsSnapshot = settings
            };
            return new TerminalAttemptScope(
                attempt.AttemptGuid,
                attempt.Processor,
                () => linklyPaymentAttemptContextAccessor.Begin(attemptContext),
                authorization => mode != LinklyConnectionMode.LocalIp ||
                    !string.IsNullOrWhiteSpace(attempt.TxnRef) &&
                    string.Equals(authorization.TxnRef ?? authorization.CardTransactions?.FirstOrDefault()?.TxnRef, attempt.TxnRef, StringComparison.Ordinal),
                async (authorization, _) =>
                {
                    if (authorization.ResultUnknown)
                    {
                        var currentAttempt = await cardPaymentAttemptRepository.GetAttemptAsync(
                            attempt.AttemptGuid,
                            CancellationToken.None);
                        var recoveringAt = DateTimeOffset.UtcNow;
                        if (currentAttempt is null ||
                            !await cardPaymentAttemptRepository.TryMarkRecoveringAsync(
                                currentAttempt.AttemptGuid,
                                currentAttempt.Status,
                                currentAttempt.UpdatedAt,
                                recoveringAt > currentAttempt.UpdatedAt
                                    ? recoveringAt
                                    : currentAttempt.UpdatedAt.AddTicks(1),
                                CancellationToken.None))
                        {
                            throw new InvalidOperationException(InstallmentText.L("installment.text.c2825938", "分期付款 attempt 已被其他任务推进、终态化或主管结案。"));
                        }

                        return;
                    }

                    await cardPaymentAttemptRepository.UpdateOutcomeAsync(
                        attempt.AttemptGuid,
                        authorization.Approved ? LocalCardPaymentAttemptStatus.Approved : LocalCardPaymentAttemptStatus.Declined,
                        authorization.ResponseCode,
                        authorization.ResponseText ?? authorization.Message,
                        authorization.Reference,
                        DateTimeOffset.UtcNow,
                        CancellationToken.None);
                });
        }

        if (settings.Processor == CardProcessorKind.Square && squarePaymentAttemptRepository is not null && squarePaymentAttemptContextAccessor is not null &&
            !string.IsNullOrWhiteSpace(settings.SquareDeviceId) && !string.IsNullOrWhiteSpace(settings.SquareLocationId))
        {
            var attemptGuid = DeterministicGuid($"installment-square:{operation.OperationGuid:D}");
            var attempt = await squarePaymentAttemptRepository.GetAttemptAsync(attemptGuid, cancellationToken);
            if (attempt is null)
            {
                attempt = new LocalSquarePaymentAttempt(
                    attemptGuid,
                    null,
                    operation.OperationGuid.ToString("N"),
                    SquareDeviceIdNormalizer.NormalizeForTerminalCheckout(settings.SquareDeviceId) ?? settings.SquareDeviceId,
                    settings.SquareLocationId,
                    settings.Environment.ToString(),
                    amount,
                    (long)Math.Round(amount * 100m, MidpointRounding.AwayFromZero),
                    "AUD",
                    LocalSquarePaymentAttemptStatus.Pending,
                    null,
                    null,
                    operation.RequestJson,
                    session.StoreCode,
                    session.DeviceCode,
                    session.CashierId,
                    null,
                    null,
                    null,
                    null,
                    now,
                    now,
                    null,
                    null,
                    null,
                    operation.Kind.ToString(),
                    operation.OperationGuid);
                await squarePaymentAttemptRepository.CreateAsync(attempt, cancellationToken);
            }

            var attemptContext = new SquarePaymentAttemptContext(
                attempt.AttemptGuid,
                attempt.IdempotencyKey,
                (checkoutId, checkoutStatus, updatedAt, _) =>
                    squarePaymentAttemptRepository.MarkCheckoutCreatedAsync(
                        attempt.AttemptGuid,
                        checkoutId,
                        checkoutStatus,
                        updatedAt,
                        CancellationToken.None));
            return new TerminalAttemptScope(
                attempt.AttemptGuid,
                CardProcessorKind.Square.ToString(),
                () => squarePaymentAttemptContextAccessor.Begin(attemptContext),
                null,
                async (authorization, _) =>
                {
                    if (authorization.ResultUnknown)
                    {
                        await squarePaymentAttemptRepository.MarkFailedAsync(
                            attempt.AttemptGuid,
                            LocalSquarePaymentAttemptStatus.Unknown,
                            null,
                            null,
                            authorization.ResponseCode,
                            authorization.ResponseText ?? authorization.Message,
                            DateTimeOffset.UtcNow,
                            CancellationToken.None);
                    }
                });
        }

        return TerminalAttemptScope.Empty;
    }

    private TerminalAttemptScope CreateZellerRepaymentScope(LocalInstallmentOperation operation)
    {
        var attemptGuid = ZellerRepaymentAttemptGuid(operation.OperationGuid);
        return new TerminalAttemptScope(
            attemptGuid,
            CardProcessorKind.Zeller.ToString(),
            beginContext: null,
            // 中文注释：批准必须带 Zeller 交易号（transactionUuid），否则不能把这笔当成已扣款入账。
            hasVerifiedApprovalEvidence: authorization =>
                authorization.CardTransactions is { Count: > 0 } transactions &&
                transactions.All(transaction => !string.IsNullOrWhiteSpace(transaction.TxnRef)),
            // 中文注释：Zeller 客户端自己把每一步写进尝试表，这里不用另记。
            recordOutcomeAsync: null,
            authorizeAsync: (amount, session, cancellationToken) =>
                AuthorizeZellerRepaymentAsync(operation, attemptGuid, amount, session, cancellationToken));
    }

    private async Task<PaymentAuthorizationResult> AuthorizeZellerRepaymentAsync(
        LocalInstallmentOperation operation,
        Guid attemptGuid,
        decimal amount,
        PosSessionState session,
        CancellationToken cancellationToken)
    {
        var amountCents = ToCents(amount);
        if (amountCents < ZellerMinimumPurchaseCents)
        {
            // 中文注释：建 claim 后已经拦过一次；这里是最后一道防线，请求没有发出，按明确拒绝结案。
            return new PaymentAuthorizationResult(
                false,
                Message: ZellerBelowMinimumMessage,
                Processor: ZellerPaymentReference.Processor,
                ResponseCode: "MINIMUM_AMOUNT",
                ResponseText: "DECLINED");
        }

        var outcome = await zellerCardTerminalClient!.PurchaseAsync(
            new ZellerPurchaseContext(
                amountCents,
                operation.RequestJson,
                session.StoreCode,
                session.DeviceCode,
                session.CashierId,
                operation.OperationGuid)
            {
                AttemptGuid = attemptGuid,
                OperationKind = ZellerAttemptOperation.InstallmentRepayment
            },
            progress: null,
            cancellationToken);
        var authorization = ZellerAuthorizationMapper.ToAuthorizationResult(outcome);
        if (outcome.State != ZellerPaymentState.NotPaid)
        {
            return outcome.State == ZellerPaymentState.Declined
                ? authorization with { ResponseText = authorization.ResponseText ?? "DECLINED" }
                : authorization;
        }

        var stored = await zellerPaymentAttemptRepository!.GetAsync(attemptGuid, CancellationToken.None);
        if (stored is { LateRecheckDueAt: not null, LateRecheckedAt: null })
        {
            // 中文注释：Zeller 查到 0 笔但还要在 60 秒后复核（迟到的批准会被复核翻回结果未知）。
            // 销售可以先放行、迟到批准再按草稿补单；分期 claim 一旦按拒绝结案就无法再入账，所以复核前保持结果未知。
            return authorization with { ResultUnknown = true, Message = ZellerAwaitingLateRecheckMessage };
        }

        // 中文注释：请求没有发出（刷卡机没连上、未配对等），确定没有扣款，按明确拒绝结案。
        return authorization with { ResponseText = "CANCELLED" };
    }

    private async Task<TerminalAttemptRecovery> RecoverZellerRepaymentAttemptAsync(
        LocalZellerPaymentAttempt attempt,
        CancellationToken cancellationToken)
    {
        // 中文注释：最多推进两步（例如 Pending → 关闭为未付款，或 Unknown → 查询后批准），每一步都只查询、不重发。
        for (var round = 0; round < 3; round++)
        {
            switch (attempt.Status)
            {
                case ZellerAttemptStatus.Approved:
                case ZellerAttemptStatus.OrderCompleted:
                    if (string.IsNullOrWhiteSpace(attempt.TransactionUuid))
                    {
                        return TerminalAttemptRecovery.Unknown;
                    }

                    return new TerminalAttemptRecovery(
                        true,
                        false,
                        ZellerPaymentRecoveryService.OrderReferenceFor(attempt),
                        [ZellerCardRecoveryProvider.BuildCardTransaction(attempt, attempt.AmountCents / 100m)]);

                case ZellerAttemptStatus.Declined:
                    return TerminalAttemptRecovery.RejectedResult;

                case ZellerAttemptStatus.Canceled:
                    if (attempt.LateRecheckDueAt is null || attempt.LateRecheckedAt is not null)
                    {
                        return TerminalAttemptRecovery.RejectedResult;
                    }

                    if (attempt.LateRecheckDueAt > DateTimeOffset.UtcNow || zellerCardTerminalClient is null)
                    {
                        return TerminalAttemptRecovery.Unknown;
                    }

                    await zellerCardTerminalClient.RecheckAsync(attempt.AttemptGuid, cancellationToken);
                    break;

                case ZellerAttemptStatus.Pending:
                    if (zellerPaymentRecoveryService is null)
                    {
                        return TerminalAttemptRecovery.Unknown;
                    }

                    await zellerPaymentRecoveryService.ClosePendingAttemptAsync(attempt.AttemptGuid);
                    break;

                default:
                    if (zellerPaymentRecoveryService is null)
                    {
                        return TerminalAttemptRecovery.Unknown;
                    }

                    var recovered = await zellerPaymentRecoveryService.RecoverAttemptAsync(attempt.AttemptGuid, cancellationToken);
                    if (recovered.Status is not ZellerRecoveryStatus.Resolved)
                    {
                        return TerminalAttemptRecovery.Unknown;
                    }

                    break;
            }

            var reloaded = await zellerPaymentAttemptRepository!.GetAsync(attempt.AttemptGuid, cancellationToken);
            if (reloaded is null || reloaded.Status == attempt.Status && reloaded.Version == attempt.Version)
            {
                return TerminalAttemptRecovery.Unknown;
            }

            attempt = reloaded;
        }

        return TerminalAttemptRecovery.Unknown;
    }

    private static Guid ZellerRepaymentAttemptGuid(Guid operationGuid) =>
        DeterministicGuid($"installment-zeller:{operationGuid:D}");

    // 中文注释：分期续付入账（本地快照也已完成）后把 Zeller 尝试标为已完成，与付款页建单后的标记同口径。失败只记日志。
    private async Task TryMarkZellerRepaymentCompletedAsync(Guid operationGuid)
    {
        if (zellerCardTerminalClient is null)
        {
            return;
        }

        try
        {
            await zellerCardTerminalClient.MarkOrderCompletedAsync(ZellerRepaymentAttemptGuid(operationGuid), CancellationToken.None);
        }
        catch (Exception exception)
        {
            LogOperationIssue(operationGuid, "zeller-mark-completed", "Ignored", exception);
        }
    }

    // 中文注释：后端模式下终端结果落定后，会话仍会占用该设备/终端，直到 POS 确认（ack）。销售在落单后 ack，
    // 分期过去从不 ack：每笔分期刷卡之后，下一笔刷卡都会先被 409 拦下，并被迫接管出一条来源不明的待复核记录。
    // 只在本地已持久化终态（完成，或终端明确拒绝）之后调用；ack 失败只记日志，不改变分期操作状态，
    // 下一次刷卡的活动会话接管仍是兜底。
    private async Task TryAcknowledgeLinklyBackendSessionAsync(Guid operationGuid)
    {
        if (linklyBackendTerminalClient is null ||
            cardTerminalSettingsProvider is null ||
            cardPaymentAttemptRepository is null)
        {
            return;
        }

        try
        {
            var attemptGuid = DeterministicGuid($"installment-linkly:{operationGuid:D}");
            var attempt = await cardPaymentAttemptRepository.GetAttemptAsync(attemptGuid, CancellationToken.None);
            if (attempt is null ||
                attempt.AcknowledgedAt is not null ||
                string.IsNullOrWhiteSpace(attempt.SessionId) ||
                !Enum.TryParse<CardTerminalEnvironment>(attempt.Environment, ignoreCase: true, out var environment))
            {
                return;
            }

            var settings = await cardTerminalSettingsProvider.GetSettingsAsync(CancellationToken.None);
            var mode = CardTerminalSettings.NormalizeLinklyConnectionMode(
                attempt.ConnectionMode,
                CardTerminalSettings.NormalizeLinklyConnectionMode(settings.LinklyConnectionMode));
            // LocalIp 本身没有后端会话；已绑定 SessionId 说明实际回退到了后端异步链路（与销售的判断一致）。
            if (mode == LinklyConnectionMode.LocalIp)
            {
                mode = LinklyConnectionMode.CloudBackendAsync;
            }

            if (settings.Processor != CardProcessorKind.Linkly || mode != LinklyConnectionMode.CloudBackendAsync)
            {
                return;
            }

            await linklyBackendTerminalClient.AcknowledgeSessionAsync(
                settings with { Environment = environment },
                attempt.SessionId,
                CancellationToken.None);
            await cardPaymentAttemptRepository.MarkAcknowledgedAsync(attempt.AttemptGuid, DateTimeOffset.UtcNow, CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            LogOperationIssue(operationGuid, "linkly-backend-ack", "Not acknowledged", exception);
        }
    }

    private static bool RequiresLinklyPurchaseRecoveryForCurrentMode(
        LocalCardPaymentAttempt attempt,
        LinklyConnectionMode mode)
    {
        var expectedMode = CardTerminalSettings.FormatLinklyConnectionMode(mode);
        if (!string.Equals(attempt.ConnectionMode?.Trim(), expectedMode, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return mode == LinklyConnectionMode.LocalIp &&
            (!string.Equals(attempt.TxnType, "P", StringComparison.Ordinal) ||
             !LinklyLocalTxnRef.TryNormalizeHistoricalReference(attempt.TxnRef, out _));
    }

    private async Task<TerminalReady> MarkTerminalApprovedAsync(LocalInstallmentOperation operation, string requestJson, CancellationToken cancellationToken)
    {
        if (operation.State == LocalInstallmentOperationState.TerminalApproved || operation.State == LocalInstallmentOperationState.ApiSubmitting)
        {
            return TerminalReady.Success(operation);
        }

        if (operation.State == LocalInstallmentOperationState.ResultUnknown)
        {
            return TerminalReady.Unknown(InstallmentText.L("installment.text.99e44eda", "终端或 API 结果未知，请先恢复或由主管处理。"));
        }

        var transitioned = await repository.TryTransitionAsync(operation.OperationGuid, [LocalInstallmentOperationState.Prepared], LocalInstallmentOperationState.TerminalApproved, DateTimeOffset.UtcNow, requestJson: requestJson, cancellationToken: cancellationToken);
        if (!transitioned)
        {
            return await ReadReadyOrUnknownAsync(operation.OperationGuid, cancellationToken);
        }

        var updated = await repository.GetAsync(operation.OperationGuid, cancellationToken)
            ?? throw new InvalidOperationException(InstallmentText.L("installment.text.2a11ee6b", "分期操作状态未保存。"));
        return TerminalReady.Success(updated);
    }

    private async Task<TerminalReady> ReadReadyOrUnknownAsync(Guid operationGuid, CancellationToken cancellationToken)
    {
        var current = await repository.GetAsync(operationGuid, cancellationToken);
        return current?.State is LocalInstallmentOperationState.TerminalApproved or LocalInstallmentOperationState.ApiSubmitting
            ? TerminalReady.Success(current)
            : current?.State == LocalInstallmentOperationState.Completed
                ? TerminalReady.Failed(InstallmentText.L("installment.text.e7a1eeef", "操作已完成，请刷新分期列表。"))
                : TerminalReady.Unknown(InstallmentText.L("installment.text.1e7b7775", "同一分期操作正在恢复或结果未知，请勿重试。"));
    }

    private async Task<InstallmentOperationResult<InstallmentCreateResponse>> SubmitCreateAsync(LocalInstallmentOperation operation, InstallmentCreateRequest request, CancellationToken cancellationToken, bool allowStaleApiSubmittingClaim = false)
    {
        if (!await ClaimApiAsync(operation.OperationGuid, allowStaleApiSubmittingClaim, cancellationToken))
        {
            return new InstallmentOperationResult<InstallmentCreateResponse>(false, Message: InstallmentText.L("installment.text.6521b035", "分期操作正在恢复或已完成。"), RequiresReview: true);
        }

        try
        {
            var response = await apiClient.CreateAsync(request, cancellationToken);
            var local = ToLocalOrder(response.Details);
            if (!await repository.CompleteWithSnapshotAsync(operation.OperationGuid, [LocalInstallmentOperationState.ApiSubmitting], local, JsonSerializer.Serialize(response, JsonOptions), false, DateTimeOffset.UtcNow, cancellationToken))
            {
                return new InstallmentOperationResult<InstallmentCreateResponse>(false, Message: InstallmentText.L("installment.text.5c72284d", "创建结果已提交，正在安全对账，请勿重复收款。"), RequiresReview: true);
            }
            await TryAcknowledgeLinklyBackendSessionAsync(operation.OperationGuid);
            return new InstallmentOperationResult<InstallmentCreateResponse>(true, response, local, response.Message);
        }
        catch (OperationCanceledException exception)
        {
            await MarkApiUnknownAsync(operation.OperationGuid, "create", InstallmentText.L("installment.text.2632dc02", "创建 API 调用已取消，结果未知。"), exception, CancellationToken.None);
            return new InstallmentOperationResult<InstallmentCreateResponse>(false, Message: InstallmentText.L("installment.text.73ba4db6", "创建结果未知，请勿再次收款。"), RequiresReview: true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await MarkApiUnknownAsync(operation.OperationGuid, "create", exception.Message, exception, CancellationToken.None);
            return new InstallmentOperationResult<InstallmentCreateResponse>(false, Message: InstallmentText.L("installment.text.1d2b54f5", "分期创建结果未知，请勿再次收款。"), RequiresReview: true);
        }
    }

    private async Task<InstallmentOperationResult<InstallmentAppendPaymentResponse>> SubmitRepaymentAsync(LocalInstallmentOperation operation, InstallmentAppendPaymentRequest request, CancellationToken cancellationToken, bool allowStaleApiSubmittingClaim = false)
    {
        if (!await ClaimApiAsync(operation.OperationGuid, allowStaleApiSubmittingClaim, cancellationToken))
        {
            return new InstallmentOperationResult<InstallmentAppendPaymentResponse>(false, Message: InstallmentText.L("installment.text.846fc4f6", "补款操作正在恢复或已完成。"), RequiresReview: true);
        }

        try
        {
            var committedClaim = await apiClient.CommitRepaymentClaimAsync(
                operation.InstallmentGuid,
                operation.OperationGuid,
                new InstallmentRepaymentClaimCommitRequest(
                    request.Reference,
                    request.ReservationToken,
                    request.CardTransactions),
                cancellationToken);
            var response = committedClaim.Commit;
            if (committedClaim.Status != InstallmentRepaymentClaimStatus.Committed || response is null)
            {
                await MarkApiUnknownAsync(operation.OperationGuid, "repayment-commit", InstallmentText.L("installment.text.81831fb5", "中央补款 claim 返回状态 {0}，缺少提交结果。", committedClaim.Status), null, CancellationToken.None);
                return new InstallmentOperationResult<InstallmentAppendPaymentResponse>(false, Message: InstallmentText.L("installment.text.794d97e9", "补款提交结果不完整，保持锁定等待恢复。"), RequiresReview: true);
            }
            var local = ToLocalOrder(response.Details);
            if (!await repository.CompleteWithSnapshotAsync(operation.OperationGuid, [LocalInstallmentOperationState.ApiSubmitting], local, JsonSerializer.Serialize(response, JsonOptions), false, DateTimeOffset.UtcNow, cancellationToken))
            {
                return new InstallmentOperationResult<InstallmentAppendPaymentResponse>(false, Message: InstallmentText.L("installment.text.8043f56e", "补款结果已提交，正在安全对账，请勿重复收款。"), RequiresReview: true);
            }
            await TryAcknowledgeLinklyBackendSessionAsync(operation.OperationGuid);
            await TryMarkZellerRepaymentCompletedAsync(operation.OperationGuid);
            return new InstallmentOperationResult<InstallmentAppendPaymentResponse>(true, response, local, response.Message);
        }
        catch (OperationCanceledException exception)
        {
            await MarkApiUnknownAsync(operation.OperationGuid, "repayment-commit", InstallmentText.L("installment.text.8857aa11", "补款 API 调用已取消，结果未知。"), exception, CancellationToken.None);
            return new InstallmentOperationResult<InstallmentAppendPaymentResponse>(false, Message: InstallmentText.L("installment.text.ba0b712d", "补款结果未知，请勿再次收款。"), RequiresReview: true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await MarkApiUnknownAsync(operation.OperationGuid, "repayment-commit", exception.Message, exception, CancellationToken.None);
            return new InstallmentOperationResult<InstallmentAppendPaymentResponse>(false, Message: InstallmentText.L("installment.text.ba0b712d", "补款结果未知，请勿再次收款。"), RequiresReview: true);
        }
    }

    private async Task<InstallmentOperationResult<InstallmentCancelResponse>> ContinueCancelAsync(LocalInstallmentOperation operation, string installmentNumber, PosSessionState session, bool supervisorResolved, CancellationToken cancellationToken)
    {
        if (!string.Equals(operation.StoreCode, session.StoreCode, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(operation.DeviceCode, session.DeviceCode, StringComparison.OrdinalIgnoreCase))
        {
            return new InstallmentOperationResult<InstallmentCancelResponse>(false, Message: InstallmentText.L("installment.text.c7e0d488", "取消退款仅允许原设备恢复。"), RequiresReview: true);
        }

        var steps = await repository.GetRefundStepsAsync(operation.OperationGuid, cancellationToken);
        var claimReady = await EnsureCancelClaimAsync(operation, steps, cancellationToken);
        if (!claimReady.Succeeded)
        {
            if (claimReady.RequiresReview)
            {
                await LockCancelForReviewAsync(operation, claimReady.Message ?? InstallmentText.L("installment.text.85a2ce9e", "取消 claim 状态无法安全确认。"));
            }
            return new InstallmentOperationResult<InstallmentCancelResponse>(false, Message: claimReady.Message, RequiresReview: claimReady.RequiresReview);
        }
        if (claimReady.Response is not null)
        {
            return await CompleteCommittedCancelAsync(operation, claimReady.Response, cancellationToken);
        }

        if ((operation.State is LocalInstallmentOperationState.ApiSubmitting or LocalInstallmentOperationState.ResultUnknown) && steps.All(IsRefundApproved))
        {
            return await PersistApprovedRefundSnapshotAndSubmitAsync(operation, steps, cancellationToken);
        }

        var hasUnknownStep = steps.Any(step => step.State == LocalInstallmentRefundStepState.ResultUnknown);
        var partialApprovedRetry = operation.State == LocalInstallmentOperationState.ResultUnknown &&
            !hasUnknownStep &&
            steps.Any(IsRefundApproved);
        if ((operation.State == LocalInstallmentOperationState.ResultUnknown && !supervisorResolved && !partialApprovedRetry) || hasUnknownStep)
        {
            return new InstallmentOperationResult<InstallmentCancelResponse>(false, Message: InstallmentText.L("installment.text.f599e835", "退款结果未知，已锁定等待主管三态结案。"), RequiresReview: true);
        }

        var begun = await BeginCancelRefundAsync(operation, claimReady.Claim!, cancellationToken);
        if (!begun.Succeeded)
        {
            if (begun.RequiresReview)
            {
                await LockCancelForReviewAsync(operation, begun.Message ?? InstallmentText.L("installment.text.f4d121fb", "取消退款阶段无法安全确认。"));
            }
            return new InstallmentOperationResult<InstallmentCancelResponse>(false, Message: begun.Message, RequiresReview: begun.RequiresReview);
        }

        var expected = supervisorResolved || partialApprovedRetry
            ? new[] { LocalInstallmentOperationState.Prepared, LocalInstallmentOperationState.TerminalApproved, LocalInstallmentOperationState.ResultUnknown }
            : new[] { LocalInstallmentOperationState.Prepared, LocalInstallmentOperationState.TerminalApproved };
        var claimed = await repository.TryTransitionAsync(operation.OperationGuid, expected, LocalInstallmentOperationState.TerminalSubmitting, DateTimeOffset.UtcNow, cancellationToken: cancellationToken);
        if (!claimed)
        {
            return new InstallmentOperationResult<InstallmentCancelResponse>(false, Message: InstallmentText.L("installment.text.891631ed", "取消操作正在恢复。"), RequiresReview: true);
        }

        foreach (var step in steps.Where(step => step.State == LocalInstallmentRefundStepState.Prepared))
        {
            // 中文注释：Linkly 被主管确认“银行明确未退款”后必须先保存新 TxnRef，断电恢复才能继续查询同一笔重试。
            // LocalIp 退款不得复用原销售 TxnRef；唯一退款号需在调用终端前已落盘，避免 GetLast 命中原扣款。
            var retryReference = await ShouldGenerateLocalLinklyRetryReferenceAsync(step) && string.IsNullOrWhiteSpace(step.RefundReference)
                ? LinklyLocalTxnRef.Create('R', step.IdempotencyKey)
                : null;
            if (!await repository.TryTransitionRefundStepAsync(
                    step.RefundStepGuid,
                    [LocalInstallmentRefundStepState.Prepared],
                    LocalInstallmentRefundStepState.TerminalSubmitting,
                    DateTimeOffset.UtcNow,
                    refundReference: retryReference,
                    cancellationToken: CancellationToken.None))
            {
                continue;
            }

            var terminalStep = retryReference is null ? step : step with { RefundReference = retryReference };
            var result = await RefundStepAsync(terminalStep, installmentNumber, session, cancellationToken);
            if (result.RequiresReview)
            {
                await repository.TryTransitionAsync(operation.OperationGuid, [LocalInstallmentOperationState.TerminalSubmitting], LocalInstallmentOperationState.ResultUnknown, DateTimeOffset.UtcNow, failureMessage: result.Message, cancellationToken: CancellationToken.None);
                var currentSteps = await repository.GetRefundStepsAsync(operation.OperationGuid, CancellationToken.None);
                await TryResolveCancelClaimAsync(operation, InstallmentCancelClaimResolveOutcome.Unknown, BuildApprovedRefundCommands(currentSteps), CancellationToken.None);
                return new InstallmentOperationResult<InstallmentCancelResponse>(false, Message: result.Message, RequiresReview: true);
            }

            if (!result.Succeeded)
            {
                var currentSteps = await repository.GetRefundStepsAsync(operation.OperationGuid, cancellationToken);
                var approvedRefunds = BuildApprovedRefundCommands(currentSteps);
                var hasApproved = approvedRefunds.Count > 0;
                var outcome = hasApproved
                    ? InstallmentCancelClaimResolveOutcome.Unknown
                    : InstallmentCancelClaimResolveOutcome.Declined;
                var resolved = await TryResolveCancelClaimAsync(operation, outcome, approvedRefunds, CancellationToken.None);
                if (!resolved)
                {
                    return new InstallmentOperationResult<InstallmentCancelResponse>(false, Message: InstallmentText.L("installment.text.bbfca9f3", "中央取消 claim 结案失败，保持锁定等待恢复。"), RequiresReview: true);
                }
                // 零成功的明确拒绝可终结本次 claim；部分退款已成功则保持 Unknown，只允许本机继续同一 operation。
                await repository.TryTransitionAsync(
                    operation.OperationGuid,
                    [LocalInstallmentOperationState.TerminalSubmitting],
                    hasApproved ? LocalInstallmentOperationState.ResultUnknown : LocalInstallmentOperationState.Failed,
                    DateTimeOffset.UtcNow,
                    failureMessage: result.Message,
                    cancellationToken: CancellationToken.None);
                return new InstallmentOperationResult<InstallmentCancelResponse>(false, Message: result.Message, RequiresReview: hasApproved);
            }
        }

        steps = await repository.GetRefundStepsAsync(operation.OperationGuid, cancellationToken);
        if (!steps.All(IsRefundApproved))
        {
            return new InstallmentOperationResult<InstallmentCancelResponse>(false, Message: InstallmentText.L("installment.text.56447b28", "退款尚未全部完成，取消保持锁定。"), RequiresReview: true);
        }

        return await PersistApprovedRefundSnapshotAndSubmitAsync(operation, steps, cancellationToken);
    }

    private async Task<InstallmentOperationResult<InstallmentCancelResponse>> PersistApprovedRefundSnapshotAndSubmitAsync(
        LocalInstallmentOperation operation,
        IReadOnlyList<LocalInstallmentRefundStep> steps,
        CancellationToken cancellationToken)
    {
        var claimReady = await EnsureCancelClaimAsync(operation, steps, cancellationToken);
        if (!claimReady.Succeeded)
        {
            await repository.TryTransitionAsync(
                operation.OperationGuid,
                [LocalInstallmentOperationState.TerminalSubmitting, LocalInstallmentOperationState.TerminalApproved],
                LocalInstallmentOperationState.ResultUnknown,
                DateTimeOffset.UtcNow,
                failureMessage: claimReady.Message,
                cancellationToken: CancellationToken.None);
            return new InstallmentOperationResult<InstallmentCancelResponse>(false, Message: claimReady.Message, RequiresReview: claimReady.RequiresReview);
        }
        if (claimReady.Response is not null)
        {
            return await CompleteCommittedCancelAsync(operation, claimReady.Response, cancellationToken);
        }
        var begun = await BeginCancelRefundAsync(operation, claimReady.Claim!, cancellationToken);
        if (!begun.Succeeded)
        {
            await repository.TryTransitionAsync(
                operation.OperationGuid,
                [LocalInstallmentOperationState.TerminalSubmitting, LocalInstallmentOperationState.TerminalApproved],
                LocalInstallmentOperationState.ResultUnknown,
                DateTimeOffset.UtcNow,
                failureMessage: begun.Message,
                cancellationToken: CancellationToken.None);
            return new InstallmentOperationResult<InstallmentCancelResponse>(false, Message: begun.Message, RequiresReview: begun.RequiresReview);
        }

        var original = Deserialize<InstallmentCancelRequest>(operation.RequestJson);
        var request = original with
        {
            Refunds = steps.Select(step => new InstallmentRefundPaymentCommandDto(
                DeterministicGuid($"refund-command:{step.RefundStepGuid:D}"),
                step.Method,
                step.Amount,
                step.RefundReference ?? step.OriginalReference,
                DeserializeTransactions(step.CardTransactionsJson),
                RefundStepIdempotencyKey(step),
                step.OriginalPaymentGuid)).ToList()
        };

        // 退款全部获批后，先用 CAS 固化最终退款快照；读取确认成功后才调用取消 API。
        var snapshotPersisted = await repository.TryTransitionAsync(
            operation.OperationGuid,
            [LocalInstallmentOperationState.TerminalSubmitting, LocalInstallmentOperationState.TerminalApproved, LocalInstallmentOperationState.ResultUnknown],
            LocalInstallmentOperationState.TerminalApproved,
            DateTimeOffset.UtcNow,
            requestJson: JsonSerializer.Serialize(request, JsonOptions),
            cancellationToken: CancellationToken.None);
        var persisted = await repository.GetAsync(operation.OperationGuid, CancellationToken.None);
        if (persisted is null || persisted.State is not (LocalInstallmentOperationState.TerminalApproved or LocalInstallmentOperationState.ApiSubmitting or LocalInstallmentOperationState.ResultUnknown))
        {
            return new InstallmentOperationResult<InstallmentCancelResponse>(false, Message: InstallmentText.L("installment.text.a871152f", "退款快照正在由其他恢复流程处理，取消保持锁定。"), RequiresReview: true);
        }

        // CAS 未命中说明其他恢复入口已经接管；只能使用其已保存的请求快照，不能用内存中的旧版本覆盖或重发不同退款明细。
        if (!snapshotPersisted)
        {
            request = Deserialize<InstallmentCancelRequest>(persisted.RequestJson);
        }

        // ApiSubmitting 是先前已发出取消 API 的边界；仅争抢过期租约，绝不再次遍历退款步骤。
        return await SubmitCancelAsync(persisted, request, cancellationToken, persisted.State == LocalInstallmentOperationState.ApiSubmitting);
    }

    private async Task<InstallmentOperationResult<InstallmentCancelResponse>> SubmitCancelAsync(LocalInstallmentOperation operation, InstallmentCancelRequest request, CancellationToken cancellationToken, bool allowStaleApiSubmittingClaim = false)
    {
        if (!await ClaimApiAsync(operation.OperationGuid, allowStaleApiSubmittingClaim, cancellationToken))
        {
            return new InstallmentOperationResult<InstallmentCancelResponse>(false, Message: InstallmentText.L("installment.text.4e2bee9a", "取消 API 正在恢复或结果未知。"), RequiresReview: true);
        }

        try
        {
            InstallmentCancelClaimDto committedClaim;
            try
            {
                committedClaim = await apiClient.CommitCancelClaimAsync(
                    operation.InstallmentGuid,
                    operation.OperationGuid,
                    new InstallmentCancelClaimCommitRequest(request.Refunds),
                    cancellationToken);
            }
            catch (Exception firstException)
            {
                // commit 可能已成功而响应丢失；只 GET/重提同一 commit，绝不重新遍历退款步骤。
                try
                {
                    committedClaim = await apiClient.GetCancelClaimAsync(operation.InstallmentGuid, operation.OperationGuid, CancellationToken.None);
                    if (committedClaim.Status == InstallmentCancelClaimStatus.RefundPending)
                    {
                        committedClaim = await apiClient.CommitCancelClaimAsync(
                            operation.InstallmentGuid,
                            operation.OperationGuid,
                            new InstallmentCancelClaimCommitRequest(request.Refunds),
                            CancellationToken.None);
                    }
                }
                catch (Exception recoveryException)
                {
                    LogOperationIssue(operation.OperationGuid, "cancel-commit", "First commit failed", firstException);
                    await MarkApiUnknownAsync(operation.OperationGuid, "cancel-commit-recovery", InstallmentText.L("installment.text.ae6a42e4", "{0}; commit 恢复失败：{1}", firstException.Message, recoveryException.Message), recoveryException, CancellationToken.None);
                    return new InstallmentOperationResult<InstallmentCancelResponse>(false, Message: InstallmentText.L("installment.text.c5fc2fd8", "取消提交结果未知，退款不会重复执行。"), RequiresReview: true);
                }
            }

            if (committedClaim.Status != InstallmentCancelClaimStatus.Committed || committedClaim.Commit is null)
            {
                await MarkApiUnknownAsync(operation.OperationGuid, "cancel-commit", InstallmentText.L("installment.text.a1e0c563", "中央取消 claim 返回状态 {0}，缺少提交结果。", committedClaim.Status), null, CancellationToken.None);
                return new InstallmentOperationResult<InstallmentCancelResponse>(false, Message: InstallmentText.L("installment.text.a26d53f7", "取消提交结果不完整，退款不会重复执行。"), RequiresReview: true);
            }

            return await CompleteCommittedCancelAsync(
                operation,
                ToCancelResponse(operation.InstallmentGuid, committedClaim.Commit),
                cancellationToken);
        }
        catch (Exception exception)
        {
            await MarkApiUnknownAsync(operation.OperationGuid, "cancel-commit", exception.Message, exception, CancellationToken.None);
            return new InstallmentOperationResult<InstallmentCancelResponse>(false, Message: InstallmentText.L("installment.text.c5fc2fd8", "取消提交结果未知，退款不会重复执行。"), RequiresReview: true);
        }
    }

    private async Task<InstallmentOperationResult<bool>> RefundStepAsync(LocalInstallmentRefundStep step, string installmentNumber, PosSessionState session, CancellationToken cancellationToken)
    {
        try
        {
            using var squareRefundScope = BeginSquareRefundAttempt(step);
            PaymentAuthorizationResult authorization = step.Method switch
            {
                PaymentMethodKind.Card => cardTerminalClient is IIdempotentCardRefundClient idempotentRefundClient
                    ? await idempotentRefundClient.RefundAsync(step.Amount, session, step.OriginalReference, ResolveRefundIdempotencyKey(step), cancellationToken)
                    : await cardTerminalClient.RefundAsync(step.Amount, session, step.OriginalReference, cancellationToken),
                PaymentMethodKind.Voucher => await voucherTenderClient.IssueRefundAsync(step.Amount, session, installmentNumber, step.IdempotencyKey, "取消分期退款", cancellationToken),
                _ => new PaymentAuthorizationResult(true, step.OriginalReference, AuthorizedAmount: step.Amount)
            };
            if (authorization.ResultUnknown)
            {
                LogRefundStepIssue(step, "ResultUnknown", null, "provider-result-unknown");
                await repository.TryTransitionRefundStepAsync(
                    step.RefundStepGuid,
                    [LocalInstallmentRefundStepState.TerminalSubmitting],
                    LocalInstallmentRefundStepState.ResultUnknown,
                    DateTimeOffset.UtcNow,
                    refundReference: authorization.Reference,
                    cardTransactionsJson: SerializeTransactions(authorization.CardTransactions),
                    failureMessage: authorization.Message ?? InstallmentText.L("installment.text.1a112e9f", "退款结果未知。"),
                    cancellationToken: CancellationToken.None);
                return new InstallmentOperationResult<bool>(false, Message: InstallmentText.L("installment.text.fd92734e", "退款结果未知，等待主管结案。"), RequiresReview: true);
            }

            if (!authorization.Approved)
            {
                // 明确拒绝意味着该退款没有金融副作用，回到 Prepared 供下一次取消安全重试。
                if (TryParseSquarePaymentId(step.OriginalReference, out _))
                {
                    // Square 会在明确 FAILED/REJECTED 前先绑定 refundId；重试前必须原子清除这笔终结证据。
                    await repository.TryResetRefundStepAfterDeclineAsync(
                        step.RefundStepGuid,
                        [LocalInstallmentRefundStepState.TerminalSubmitting, LocalInstallmentRefundStepState.ResultUnknown],
                        Guid.NewGuid().ToString("N"),
                        DateTimeOffset.UtcNow,
                        authorization.Message,
                        CancellationToken.None);
                }
                else
                {
                    await repository.TryTransitionRefundStepAsync(step.RefundStepGuid, [LocalInstallmentRefundStepState.TerminalSubmitting], LocalInstallmentRefundStepState.Prepared, DateTimeOffset.UtcNow, failureMessage: authorization.Message, cancellationToken: CancellationToken.None);
                }
                return new InstallmentOperationResult<bool>(false, Message: authorization.Message ?? InstallmentText.L("installment.text.ba92ca1c", "退款未获批准。"));
            }

            if (!HasExactAuthorizedAmount(authorization, step.Amount) ||
                (step.Method == PaymentMethodKind.Card && !HasCardRefundEvidence(authorization)))
            {
                LogRefundStepIssue(step, "ResultUnknown", null, "approval-evidence-mismatch");
                await repository.TryTransitionRefundStepAsync(
                    step.RefundStepGuid,
                    [LocalInstallmentRefundStepState.TerminalSubmitting],
                    LocalInstallmentRefundStepState.ResultUnknown,
                    DateTimeOffset.UtcNow,
                    failureMessage: "Refund approval evidence or amount did not match the persisted step.",
                    cancellationToken: CancellationToken.None);
                return new InstallmentOperationResult<bool>(false, Message: InstallmentText.L("installment.text.dda4ce18", "退款批准信息无法安全核验，等待主管结案。"), RequiresReview: true);
            }

            var approvedPersisted = await repository.TryTransitionRefundStepAsync(step.RefundStepGuid, [LocalInstallmentRefundStepState.TerminalSubmitting], LocalInstallmentRefundStepState.Approved, DateTimeOffset.UtcNow, authorization.Reference, authorization.CardTransactions is null ? null : JsonSerializer.Serialize(authorization.CardTransactions, JsonOptions), cancellationToken: CancellationToken.None);
            if (!approvedPersisted)
            {
                var current = (await repository.GetRefundStepsAsync(step.OperationGuid, CancellationToken.None)).FirstOrDefault(item => item.RefundStepGuid == step.RefundStepGuid);
                return current is not null && IsRefundApproved(current)
                    ? new InstallmentOperationResult<bool>(true, true)
                    : new InstallmentOperationResult<bool>(false, Message: InstallmentText.L("installment.text.b3cd327f", "退款结果正在恢复或未知，保持锁定。"), RequiresReview: true);
            }

            return new InstallmentOperationResult<bool>(true, true);
        }
        catch (OperationCanceledException exception)
        {
            LogRefundStepIssue(step, "ResultUnknown", exception, "canceled");
            await repository.TryTransitionRefundStepAsync(step.RefundStepGuid, [LocalInstallmentRefundStepState.TerminalSubmitting], LocalInstallmentRefundStepState.ResultUnknown, DateTimeOffset.UtcNow, failureMessage: InstallmentText.L("installment.text.8c2a453b", "退款终端调用被取消，结果未知。"), cancellationToken: CancellationToken.None);
            return new InstallmentOperationResult<bool>(false, Message: InstallmentText.L("installment.text.fd92734e", "退款结果未知，等待主管结案。"), RequiresReview: true);
        }
        catch (Exception exception)
        {
            LogRefundStepIssue(step, "ResultUnknown", exception, "exception");
            await repository.TryTransitionRefundStepAsync(
                step.RefundStepGuid,
                [LocalInstallmentRefundStepState.TerminalSubmitting],
                LocalInstallmentRefundStepState.ResultUnknown,
                DateTimeOffset.UtcNow,
                failureMessage: exception.Message,
                cancellationToken: CancellationToken.None);
            return new InstallmentOperationResult<bool>(false, Message: InstallmentText.L("installment.text.fd92734e", "退款结果未知，等待主管结案。"), RequiresReview: true);
        }
    }

    private async Task<TerminalAttemptRecovery> ReadPersistedTerminalApprovalAsync(LocalInstallmentOperation operation, PosSessionState session, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(operation.TerminalAttemptGuid, out var attemptGuid))
        {
            return TerminalAttemptRecovery.Unknown;
        }

        var cardAttempt = cardPaymentAttemptRepository is null ? null : await cardPaymentAttemptRepository.GetAttemptAsync(attemptGuid, cancellationToken);
        if (cardAttempt is not null)
        {
            if (cardAttempt.Status is LocalCardPaymentAttemptStatus.Declined or LocalCardPaymentAttemptStatus.Cancelled)
            {
                return TerminalAttemptRecovery.RejectedResult;
            }

            if (cardTerminalClient is IInstallmentTerminalRecoveryClient recoveryClient)
            {
                return ToTerminalAttemptRecovery(await recoveryClient.RecoverLinklyAsync(cardAttempt, session, CancellationToken.None), cardAttempt.Amount);
            }

            return TerminalAttemptRecovery.Unknown;
        }

        var zellerAttempt = zellerPaymentAttemptRepository is null ? null : await zellerPaymentAttemptRepository.GetAsync(attemptGuid, cancellationToken);
        if (zellerAttempt is not null)
        {
            return zellerAttempt.OperationKind == ZellerAttemptOperation.InstallmentRepayment
                ? await RecoverZellerRepaymentAttemptAsync(zellerAttempt, cancellationToken)
                : TerminalAttemptRecovery.Unknown;
        }

        var squareAttempt = squarePaymentAttemptRepository is null ? null : await squarePaymentAttemptRepository.GetAttemptAsync(attemptGuid, cancellationToken);
        if (squareAttempt is not null)
        {
            if (squareAttempt.Status == LocalSquarePaymentAttemptStatus.PaymentVerified)
            {
                if (string.IsNullOrWhiteSpace(squareAttempt.PaymentId))
                {
                    return TerminalAttemptRecovery.Unknown;
                }

                return new TerminalAttemptRecovery(true, false, $"SQ:{squareAttempt.PaymentId}",
                    [new CardTransactionDto("Square", squareAttempt.PaymentId, null, null, null, null, null, squareAttempt.ResponseCode, squareAttempt.ResponseText, null, squareAttempt.CompletedAt, squareAttempt.Amount, null)]);
            }

            if (squareAttempt.Status == LocalSquarePaymentAttemptStatus.Canceled)
            {
                return TerminalAttemptRecovery.RejectedResult;
            }

            if (cardTerminalClient is IInstallmentTerminalRecoveryClient recoveryClient)
            {
                return ToTerminalAttemptRecovery(await recoveryClient.RecoverSquareAsync(squareAttempt, session, CancellationToken.None), squareAttempt.Amount);
            }

            return TerminalAttemptRecovery.Unknown;
        }

        return TerminalAttemptRecovery.Unknown;
    }

    private static TerminalAttemptRecovery ToTerminalAttemptRecovery(PaymentAuthorizationResult authorization, decimal expectedAmount)
    {
        if (authorization.Approved && authorization.AuthorizedAmount is decimal amount && Math.Abs(amount - expectedAmount) < 0.001m && authorization.CardTransactions is { Count: > 0 })
        {
            return new TerminalAttemptRecovery(true, false, authorization.Reference, authorization.CardTransactions);
        }

        // 中文注释：GetLast/网络查询失败也会返回未批准；没有终端最终证据绝不能把已扣款操作解锁。
        return !authorization.ResultUnknown && !authorization.Approved && HasExplicitTerminalRejection(authorization)
            ? TerminalAttemptRecovery.RejectedResult
            : TerminalAttemptRecovery.Unknown;
    }

    private static bool HasExplicitTerminalRejection(PaymentAuthorizationResult authorization)
    {
        if (authorization.ResultUnknown || authorization.Approved)
        {
            return false;
        }

        return string.Equals(authorization.ResponseCode, "05", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(authorization.ResponseCode, "C0", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(authorization.ResponseCode, "CN", StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(authorization.ResponseText) &&
             (authorization.ResponseText.Contains("DECLINED", StringComparison.OrdinalIgnoreCase) ||
              authorization.ResponseText.Contains("REJECTED", StringComparison.OrdinalIgnoreCase) ||
              authorization.ResponseText.Contains("CANCELLED", StringComparison.OrdinalIgnoreCase) ||
              authorization.ResponseText.Contains("CANCELED", StringComparison.OrdinalIgnoreCase)));
    }

    private async Task<InstallmentOperationRecoveryResult> RecoverCreateAsync(LocalInstallmentOperation operation, PosSessionState session, CancellationToken cancellationToken)
    {
        var request = Deserialize<InstallmentCreateRequest>(operation.RequestJson);
        if (request.DownPayment.Method == PaymentMethodKind.Card && request.DownPayment.CardTransactions is not { Count: > 0 })
        {
            var terminalRecovery = await ReadPersistedTerminalApprovalAsync(operation, session, cancellationToken);
            if (terminalRecovery.Rejected)
            {
                await repository.TryTransitionAsync(operation.OperationGuid, [LocalInstallmentOperationState.ResultUnknown], LocalInstallmentOperationState.Failed, DateTimeOffset.UtcNow, failureMessage: InstallmentText.L("installment.text.6566fa85", "终端明确拒绝或取消。"), cancellationToken: CancellationToken.None);
                await TryAcknowledgeLinklyBackendSessionAsync(operation.OperationGuid);
                return new InstallmentOperationRecoveryResult(operation.OperationGuid, operation.Kind, LocalInstallmentOperationState.Failed, false, InstallmentText.L("installment.text.c6888ee5", "终端已明确拒绝或取消，可重新操作。"));
            }

            if (terminalRecovery.Approved)
            {
                request = request with { DownPayment = request.DownPayment with { Reference = terminalRecovery.Reference, CardTransactions = terminalRecovery.Transactions } };
                if (!await repository.TryTransitionAsync(operation.OperationGuid, [LocalInstallmentOperationState.ResultUnknown], LocalInstallmentOperationState.TerminalApproved, DateTimeOffset.UtcNow, requestJson: JsonSerializer.Serialize(request, JsonOptions), cancellationToken: CancellationToken.None))
                {
                    return new InstallmentOperationRecoveryResult(operation.OperationGuid, operation.Kind, LocalInstallmentOperationState.ResultUnknown, false, InstallmentText.L("installment.text.b28489d2", "终端批准正在由其他恢复入口对账。"));
                }

                operation = (await repository.GetAsync(operation.OperationGuid, cancellationToken))!;
            }
            else
            return new InstallmentOperationRecoveryResult(operation.OperationGuid, operation.Kind, LocalInstallmentOperationState.ResultUnknown, false, InstallmentText.L("installment.text.0218a562", "未确认终端批准，禁止自动重扣。"));
        }

        var result = await SubmitCreateAsync(operation, request, cancellationToken, allowStaleApiSubmittingClaim: true);
        return new InstallmentOperationRecoveryResult(operation.OperationGuid, operation.Kind, result.Succeeded ? LocalInstallmentOperationState.Completed : LocalInstallmentOperationState.ResultUnknown, result.Succeeded, result.Message);
    }

    private async Task<InstallmentOperationRecoveryResult> RecoverPickupAsync(
        LocalInstallmentOperation operation,
        CancellationToken cancellationToken)
    {
        if (operation.State == LocalInstallmentOperationState.Prepared)
        {
            await repository.TryTransitionAsync(
                operation.OperationGuid,
                [LocalInstallmentOperationState.Prepared],
                LocalInstallmentOperationState.TerminalApproved,
                DateTimeOffset.UtcNow,
                cancellationToken: cancellationToken);
            operation = await repository.GetAsync(operation.OperationGuid, cancellationToken) ?? operation;
        }

        var request = Deserialize<InstallmentConfirmPickupRequest>(operation.RequestJson);
        var result = await SubmitPickupAsync(
            operation,
            request,
            cancellationToken,
            allowStaleApiSubmittingClaim: operation.State == LocalInstallmentOperationState.ApiSubmitting);
        return new InstallmentOperationRecoveryResult(
            operation.OperationGuid,
            operation.Kind,
            result.Succeeded ? LocalInstallmentOperationState.Completed : LocalInstallmentOperationState.ResultUnknown,
            result.Succeeded,
            result.Message);
    }

    private async Task<InstallmentOperationRecoveryResult> RecoverRepaymentAsync(LocalInstallmentOperation operation, PosSessionState session, CancellationToken cancellationToken)
    {
        var request = Deserialize<InstallmentAppendPaymentRequest>(operation.RequestJson);
        InstallmentRepaymentClaimDto claim;
        try
        {
            claim = await apiClient.GetRepaymentClaimAsync(operation.InstallmentGuid, operation.OperationGuid, cancellationToken);
        }
        catch (CatalogApiException exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            if (!CanRecreateMissingRepaymentClaim(operation, request))
            {
                var message = InstallmentText.L("installment.text.6b6244e3", "远端补款 claim 缺失，但本地已有 provider attempt 或批准证据；保持锁定并等待人工对账。");
                await repository.TryTransitionAsync(
                    operation.OperationGuid,
                    [operation.State],
                    LocalInstallmentOperationState.ResultUnknown,
                    DateTimeOffset.UtcNow,
                    failureMessage: message,
                    cancellationToken: CancellationToken.None);
                return new InstallmentOperationRecoveryResult(
                    operation.OperationGuid,
                    operation.Kind,
                    LocalInstallmentOperationState.ResultUnknown,
                    false,
                    message);
            }

            if (request.Method == PaymentMethodKind.Card)
            {
                // 中文注释：刷卡续付的本地操作还停在 Prepared、没有任何终端 attempt 或证据，说明刷卡终端从没收到过请求；
                // 中央也没有这笔 claim。直接结案，不再为它重建 claim（重建只会白白占住这张分期单）。
                var cardNotStarted = InstallmentText.L("installment.text.781062ca", "刷卡尚未开始，这笔续付已结案，可重新发起。");
                var closed = await repository.TryTransitionAsync(
                    operation.OperationGuid,
                    [LocalInstallmentOperationState.Prepared],
                    LocalInstallmentOperationState.Failed,
                    DateTimeOffset.UtcNow,
                    failureMessage: cardNotStarted,
                    cancellationToken: CancellationToken.None);
                return new InstallmentOperationRecoveryResult(
                    operation.OperationGuid,
                    operation.Kind,
                    closed ? LocalInstallmentOperationState.Failed : LocalInstallmentOperationState.ResultUnknown,
                    false,
                    closed ? cardNotStarted : InstallmentText.L("installment.text.f4945dee", "这笔刷卡续付正由另一恢复入口处理。"));
            }

            // 中文注释：只有纯 Prepared 且无任何 provider 证据的本地 action，才能证明 create claim 可能尚未到达服务端。
            var recreated = await EnsureRepaymentClaimAsync(operation, request, cancellationToken);
            if (!recreated.Succeeded || recreated.Claim is null)
            {
                return new InstallmentOperationRecoveryResult(operation.OperationGuid, operation.Kind, operation.State, false, recreated.Message);
            }

            if (recreated.Response is not null)
            {
                var completed = await CompleteCommittedRepaymentAsync(operation, recreated.Response, cancellationToken);
                return new InstallmentOperationRecoveryResult(operation.OperationGuid, operation.Kind, completed.Succeeded ? LocalInstallmentOperationState.Completed : LocalInstallmentOperationState.ResultUnknown, completed.Succeeded, completed.Message);
            }

            claim = recreated.Claim;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogOperationIssue(operation.OperationGuid, "recover-repayment-read-claim", "Kept locked", exception);
            return new InstallmentOperationRecoveryResult(operation.OperationGuid, operation.Kind, LocalInstallmentOperationState.ResultUnknown, false, InstallmentText.L("installment.text.22bd8803", "读取中央补款 claim 失败，保持锁定：{0}", exception.Message));
        }

        if (claim.Status == InstallmentRepaymentClaimStatus.Committed)
        {
            if (claim.Commit is null)
            {
                return new InstallmentOperationRecoveryResult(operation.OperationGuid, operation.Kind, LocalInstallmentOperationState.ResultUnknown, false, InstallmentText.L("installment.text.0c58896e", "中央补款已提交但缺少结果快照，保持锁定。"));
            }

            var completed = await CompleteCommittedRepaymentAsync(operation, claim.Commit, cancellationToken);
            return new InstallmentOperationRecoveryResult(operation.OperationGuid, operation.Kind, completed.Succeeded ? LocalInstallmentOperationState.Completed : LocalInstallmentOperationState.ResultUnknown, completed.Succeeded, completed.Message);
        }

        if (claim.Status is InstallmentRepaymentClaimStatus.Released or InstallmentRepaymentClaimStatus.Declined)
        {
            await repository.TryTransitionAsync(
                operation.OperationGuid,
                [operation.State],
                LocalInstallmentOperationState.Failed,
                DateTimeOffset.UtcNow,
                failureMessage: InstallmentText.L("installment.text.dba15a93", "中央补款 claim 已结束：{0}", claim.Status),
                cancellationToken: CancellationToken.None);
            return new InstallmentOperationRecoveryResult(operation.OperationGuid, operation.Kind, LocalInstallmentOperationState.Failed, false, InstallmentText.L("installment.text.32df73b3", "原补款 claim 已结束，可重新发起付款。"));
        }

        if (claim.Status == InstallmentRepaymentClaimStatus.Unknown &&
            !string.IsNullOrWhiteSpace(claim.Provider) &&
            !string.IsNullOrWhiteSpace(claim.ProviderAttemptId))
        {
            var resumed = await BeginRepaymentProviderAsync(operation, claim.Provider, claim.ProviderAttemptId, cancellationToken);
            if (!resumed.Succeeded)
            {
                return new InstallmentOperationRecoveryResult(operation.OperationGuid, operation.Kind, LocalInstallmentOperationState.ResultUnknown, false, resumed.Message);
            }
        }

        if (request.Method == PaymentMethodKind.Card &&
            request.CardTransactions is not { Count: > 0 } &&
            operation.State == LocalInstallmentOperationState.Prepared &&
            claim.Status is InstallmentRepaymentClaimStatus.ProviderPending or InstallmentRepaymentClaimStatus.Unknown)
        {
            if (string.IsNullOrWhiteSpace(claim.Provider) || string.IsNullOrWhiteSpace(claim.ProviderAttemptId))
            {
                return new InstallmentOperationRecoveryResult(operation.OperationGuid, operation.Kind, LocalInstallmentOperationState.ResultUnknown, false, InstallmentText.L("installment.text.04000372", "中央补款缺少卡 provider attempt 绑定，保持锁定。"));
            }

            // begin-provider 已成功但本地状态尚未来得及推进：先把中央耐久 attempt 绑定回本地，再且仅再做 provider 查询。
            var bound = await repository.TryTransitionAsync(
                operation.OperationGuid,
                [LocalInstallmentOperationState.Prepared],
                LocalInstallmentOperationState.ResultUnknown,
                DateTimeOffset.UtcNow,
                terminalAttemptGuid: claim.ProviderAttemptId,
                terminalProcessor: claim.Provider,
                failureMessage: InstallmentText.L("installment.text.4d0e09d4", "begin-provider 后本地状态中断，正在查询原卡 attempt。"),
                cancellationToken: CancellationToken.None);
            operation = await repository.GetAsync(operation.OperationGuid, cancellationToken) ?? operation;
            if (!bound && operation.State is not LocalInstallmentOperationState.ResultUnknown and not LocalInstallmentOperationState.TerminalApproved and not LocalInstallmentOperationState.ApiSubmitting)
            {
                return new InstallmentOperationRecoveryResult(operation.OperationGuid, operation.Kind, operation.State, false, InstallmentText.L("installment.text.a5c95e8f", "原卡 attempt 正由另一恢复入口处理。"));
            }
        }

        if (request.Method == PaymentMethodKind.Voucher &&
            string.IsNullOrWhiteSpace(request.ReservationToken) &&
            claim.Status is InstallmentRepaymentClaimStatus.ProviderPending or InstallmentRepaymentClaimStatus.Unknown)
        {
            if (operation.State != LocalInstallmentOperationState.Prepared)
            {
                // 本地已越过 provider 前置状态却没有 reservation token，结果只能保持 Unknown，绝不能再次兑换。
                await TryResolveRepaymentClaimAsync(operation, InstallmentRepaymentClaimResolveOutcome.Unknown, CancellationToken.None);
                return new InstallmentOperationRecoveryResult(operation.OperationGuid, operation.Kind, LocalInstallmentOperationState.ResultUnknown, false, InstallmentText.L("installment.text.89e4d93a", "礼券 reservation 结果未知，禁止重复兑换。"));
            }

            // 礼券调用严格位于 Prepared -> TerminalSubmitting 之后；仍为 Prepared 证明 provider 尚未执行，可安全推进一次。
            var ready = await EnsureVoucherRepaymentApprovalAsync(operation, request, session, cancellationToken);
            if (!ready.Succeeded || ready.Operation is null)
            {
                var current = await repository.GetAsync(operation.OperationGuid, CancellationToken.None);
                return new InstallmentOperationRecoveryResult(
                    operation.OperationGuid,
                    operation.Kind,
                    current?.State ?? LocalInstallmentOperationState.ResultUnknown,
                    false,
                    ready.Message);
            }

            operation = ready.Operation;
            request = Deserialize<InstallmentAppendPaymentRequest>(operation.RequestJson);
        }

        if (operation.State == LocalInstallmentOperationState.Prepared &&
            claim.Status is InstallmentRepaymentClaimStatus.ProviderPending or InstallmentRepaymentClaimStatus.Unknown &&
            (request.Method == PaymentMethodKind.Cash ||
             request.Method == PaymentMethodKind.Voucher && !string.IsNullOrWhiteSpace(request.ReservationToken) ||
             request.Method == PaymentMethodKind.Card && request.CardTransactions is { Count: > 0 }))
        {
            // 现金没有外部 provider；已有礼券/卡证据也不再授权，只补齐 begin 后未落下的本地批准状态。
            var ready = await MarkTerminalApprovedAsync(operation, operation.RequestJson, cancellationToken);
            if (!ready.Succeeded || ready.Operation is null)
            {
                return new InstallmentOperationRecoveryResult(operation.OperationGuid, operation.Kind, LocalInstallmentOperationState.ResultUnknown, false, ready.Message);
            }

            operation = ready.Operation;
        }

        if (operation.State == LocalInstallmentOperationState.Prepared &&
            claim.Status == InstallmentRepaymentClaimStatus.Prepared &&
            request.Method == PaymentMethodKind.Card &&
            request.CardTransactions is not { Count: > 0 })
        {
            // 中文注释：中央 claim 还没登记任何通道、本地也没推进过：刷卡终端没收到请求（手动刷卡则是收款窗口没打开过）。
            // 确定没有收款：释放 claim 并结案，可重新发起。
            var notStartedMessage = InstallmentText.L("installment.text.781062ca", "刷卡尚未开始，这笔续付已结案，可重新发起。");
            return await FailUnsupportedCardRepaymentAsync(operation, "recover-card-not-started", notStartedMessage)
                ? new InstallmentOperationRecoveryResult(operation.OperationGuid, operation.Kind, LocalInstallmentOperationState.Failed, false, notStartedMessage)
                : new InstallmentOperationRecoveryResult(operation.OperationGuid, operation.Kind, LocalInstallmentOperationState.ResultUnknown, false, InstallmentText.L("installment.text.f4945dee", "这笔刷卡续付正由另一恢复入口处理。"));
        }

        if (request.Method == PaymentMethodKind.Card && request.CardTransactions is not { Count: > 0 })
        {
            var terminalRecovery = await ReadPersistedTerminalApprovalAsync(operation, session, cancellationToken);
            if (terminalRecovery.Rejected)
            {
                await repository.TryTransitionAsync(operation.OperationGuid, [LocalInstallmentOperationState.ResultUnknown], LocalInstallmentOperationState.Failed, DateTimeOffset.UtcNow, failureMessage: InstallmentText.L("installment.text.6566fa85", "终端明确拒绝或取消。"), cancellationToken: CancellationToken.None);
                await TryResolveRepaymentClaimAsync(operation, InstallmentRepaymentClaimResolveOutcome.Declined, CancellationToken.None);
                await TryAcknowledgeLinklyBackendSessionAsync(operation.OperationGuid);
                return new InstallmentOperationRecoveryResult(operation.OperationGuid, operation.Kind, LocalInstallmentOperationState.Failed, false, InstallmentText.L("installment.text.c6888ee5", "终端已明确拒绝或取消，可重新操作。"));
            }

            if (terminalRecovery.Approved)
            {
                request = request with { Reference = terminalRecovery.Reference, CardTransactions = terminalRecovery.Transactions };
                if (!await repository.TryTransitionAsync(operation.OperationGuid, [LocalInstallmentOperationState.ResultUnknown], LocalInstallmentOperationState.TerminalApproved, DateTimeOffset.UtcNow, requestJson: JsonSerializer.Serialize(request, JsonOptions), cancellationToken: CancellationToken.None))
                {
                    return new InstallmentOperationRecoveryResult(operation.OperationGuid, operation.Kind, LocalInstallmentOperationState.ResultUnknown, false, InstallmentText.L("installment.text.b28489d2", "终端批准正在由其他恢复入口对账。"));
                }

                operation = (await repository.GetAsync(operation.OperationGuid, cancellationToken))!;
            }
            else
            {
                if (claim.Status is InstallmentRepaymentClaimStatus.ProviderPending or InstallmentRepaymentClaimStatus.Unknown)
                {
                    // Unknown 可能已通过同 provider/attempt 的 begin 恢复成 ProviderPending；未确认时必须再次归档为 Unknown。
                    await TryResolveRepaymentClaimAsync(operation, InstallmentRepaymentClaimResolveOutcome.Unknown, CancellationToken.None);
                }
                return new InstallmentOperationRecoveryResult(operation.OperationGuid, operation.Kind, LocalInstallmentOperationState.ResultUnknown, false, InstallmentText.L("installment.text.0218a562", "未确认终端批准，禁止自动重扣。"));
            }
        }

        if (claim.Status == InstallmentRepaymentClaimStatus.Prepared)
        {
            var ready = await EnsureRepaymentTerminalApprovalAsync(operation, session, authorizeCard: false, cancellationToken);
            if (!ready.Succeeded || ready.Operation is null)
            {
                return new InstallmentOperationRecoveryResult(operation.OperationGuid, operation.Kind, LocalInstallmentOperationState.ResultUnknown, false, ready.Message);
            }

            operation = ready.Operation;
            request = Deserialize<InstallmentAppendPaymentRequest>(operation.RequestJson);
        }

        var result = await SubmitRepaymentAsync(operation, request, cancellationToken, allowStaleApiSubmittingClaim: true);
        return new InstallmentOperationRecoveryResult(operation.OperationGuid, operation.Kind, result.Succeeded ? LocalInstallmentOperationState.Completed : LocalInstallmentOperationState.ResultUnknown, result.Succeeded, result.Message);
    }

    private async Task<InstallmentOperationRecoveryResult> RecoverCancelAsync(LocalInstallmentOperation operation, PosSessionState session, CancellationToken cancellationToken)
    {
        var steps = await repository.GetRefundStepsAsync(operation.OperationGuid, cancellationToken);
        var rejectedRefundReset = await RecoverSquareRefundStepsAsync(operation, steps, session, cancellationToken);
        steps = await repository.GetRefundStepsAsync(operation.OperationGuid, cancellationToken);
        if ((rejectedRefundReset || HasSquareRejectedRefundReset(steps)) &&
            await TryFinalizeRejectedCancelRecoveryAsync(operation, steps))
        {
            return new InstallmentOperationRecoveryResult(operation.OperationGuid, operation.Kind, LocalInstallmentOperationState.Failed, false, InstallmentText.L("installment.text.526dd732", "Square 退款已明确拒绝，可重新发起取消。"));
        }
        if (!steps.All(IsRefundApproved))
        {
            return new InstallmentOperationRecoveryResult(operation.OperationGuid, operation.Kind, operation.State, false, InstallmentText.L("installment.text.32e9991e", "退款未全部确认，保持锁定。") );
        }

        var result = await PersistApprovedRefundSnapshotAndSubmitAsync(operation, steps, cancellationToken);
        return new InstallmentOperationRecoveryResult(operation.OperationGuid, operation.Kind, result.Succeeded ? LocalInstallmentOperationState.Completed : LocalInstallmentOperationState.ResultUnknown, result.Succeeded, result.Message);
    }

    private async Task<bool> ClaimApiAsync(Guid operationGuid, bool allowStaleApiSubmittingClaim, CancellationToken cancellationToken)
    {
        return await repository.TryClaimApiAsync(
            operationGuid,
            _apiClaimToken,
            allowStaleApiSubmittingClaim,
            DateTimeOffset.UtcNow,
            ApiClaimLease,
            cancellationToken);
    }

    private async Task MarkApiUnknownAsync(
        Guid operationGuid,
        string operation,
        string message,
        Exception? exception,
        CancellationToken cancellationToken)
    {
        // 结果未知会锁住分期单等待对账：先在中心日志留痕，再写本地状态。
        LogOperationIssue(operationGuid, operation, "ResultUnknown", exception, exception is null ? message : null);
        await repository.TryTransitionAsync(operationGuid, [LocalInstallmentOperationState.ApiSubmitting], LocalInstallmentOperationState.ResultUnknown, DateTimeOffset.UtcNow, failureMessage: message, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// 分期联网/终端操作失败统一留痕（此前只写本地库）：operationGuid 作 TraceId，与服务端 claim 日志串联。
    /// 结果未知、中止均为 Warning；退款类真实资金失败由 <see cref="LogRefundStepIssue"/> 记 Error。
    /// </summary>
    private static void LogOperationIssue(
        Guid operationGuid,
        string operation,
        string outcome,
        Exception? exception,
        string? detail = null,
        bool isError = false)
    {
        var apiException = exception as CatalogApiException;
        int? statusCode = apiException?.StatusCode is { } status ? (int)status : null;
        var message =
            $"op={operation} operationGuid={operationGuid:D} → {outcome}" +
            (apiException?.ErrorCode is { } code ? $" errorCode={code}" : string.Empty) +
            (statusCode is { } http ? $" http={http}" : string.Empty) +
            (exception is null ? string.Empty : $" error={exception.GetType().Name} message={exception.Message}") +
            (string.IsNullOrWhiteSpace(detail) ? string.Empty : $" detail={detail}");
        var context = new ApplicationLogContext(
            TraceId: operationGuid.ToString("D"),
            StatusCode: statusCode,
            Properties: new Dictionary<string, object?>
            {
                ["errorCode"] = apiException?.ErrorCode,
                ["operation"] = operation
            });
        if (isError)
        {
            ConsoleLog.WriteError("Installment", message, context, exception);
        }
        else
        {
            ConsoleLog.WriteWarning("Installment", message, context, exception);
        }
    }

    /// <summary>退款步骤（退款券/刷卡退款）结果未知涉及真实资金且需主管结案，记 Error。</summary>
    private static void LogRefundStepIssue(
        LocalInstallmentRefundStep step,
        string outcome,
        Exception? exception,
        string reason)
    {
        LogOperationIssue(
            step.OperationGuid,
            $"refund-{step.Method}",
            outcome,
            exception,
            $"refundStepGuid={step.RefundStepGuid:D} amount={step.Amount:0.00} reason={reason}",
            isError: true);
    }

    private static LocalFinancialSupervisorResolution BuildInstallmentRefundSupervisorJournal(
        LocalInstallmentOperation operation,
        LocalInstallmentRefundStep step,
        InstallmentRefundSupervisorResolution resolution,
        string environment,
        DateTimeOffset resolvedAt)
    {
        var resolutionGuid = Guid.NewGuid();
        var auditEventId = Guid.NewGuid();
        var processor = string.IsNullOrWhiteSpace(operation.TerminalProcessor)
            ? step.Method.ToString()
            : operation.TerminalProcessor;
        var auditEvent = new OperationAuditEventDto
        {
            EventId = auditEventId,
            OccurredAtUtc = resolvedAt,
            OperationType = "INSTALLMENT_REFUND_SUPERVISOR_RESOLUTION",
            Outcome = resolution.Decision.ToString(),
            CashierId = resolution.OperatorId,
            UserGuid = resolution.OperatorUserGuid,
            CashierName = resolution.OperatorName,
            StoreCode = operation.StoreCode,
            DeviceCode = operation.DeviceCode,
            CorrelationId = operation.OperationGuid.ToString("D"),
            PaymentMethod = processor,
            ReasonCode = resolution.Decision.ToString(),
            SafeMessage = resolution.Reason,
            PaymentAmount = Math.Abs(step.Amount),
            Properties = new Dictionary<string, string?>
            {
                ["operationGuid"] = operation.OperationGuid.ToString("D"),
                ["refundStepGuid"] = step.RefundStepGuid.ToString("D"),
                ["evidence"] = resolution.Evidence,
                ["financialReference"] = resolution.RefundReference,
                ["retryReference"] = step.IdempotencyKey
            }
        };
        return new LocalFinancialSupervisorResolution(
            resolutionGuid,
            LocalFinancialSupervisorResolutionTarget.InstallmentRefund,
            processor,
            environment,
            operation.StoreCode,
            operation.DeviceCode,
            null,
            step.RefundStepGuid,
            operation.OperationGuid,
            null,
            resolution.Decision.ToString(),
            resolution.OperatorId,
            resolution.OperatorUserGuid,
            resolution.OperatorName,
            resolution.Reason,
            resolution.Evidence,
            resolution.RefundReference,
            step.IdempotencyKey,
            resolvedAt,
            auditEventId,
            JsonSerializer.Serialize(auditEvent, JsonOptions));
    }

    private static LocalInstallmentOperation CreateOperation(Guid operationGuid, LocalInstallmentOperationKind kind, Guid installmentGuid, Guid? paymentGuid, string storeCode, string deviceCode, string cashierId, string idempotencyKey, string requestJson)
    {
        var now = DateTimeOffset.UtcNow;
        return new LocalInstallmentOperation(operationGuid, kind, installmentGuid, paymentGuid, storeCode, deviceCode, cashierId, idempotencyKey, requestJson, LocalInstallmentOperationState.Prepared, null, null, null, null, now, now);
    }

    private static LocalInstallmentOrder ToLocalOrder(InstallmentDetailsDto details) => new(
        details.InstallmentGuid, details.InstallmentGuid, details.InstallmentNumber, details.StoreCode, details.DeviceCode,
        details.CashierId, details.CashierName, details.CustomerName, details.CustomerPhone, details.CreatedAt, DateTimeOffset.UtcNow,
        details.TotalAmount, details.MinimumDownPayment, details.DownPaymentAmount, details.PaidAmount, details.BalanceAmount,
        details.Status, details.Lines, details.Payments, details.PickupInfo, details.Note, details.CancellationInfo);

    private static string EnsureIdempotencyKey(string? value, Guid scope) => string.IsNullOrWhiteSpace(value) ? $"{scope:D}:installment" : value.Trim();
    private static bool IsRefundApproved(LocalInstallmentRefundStep step) => step.State is LocalInstallmentRefundStepState.Approved or LocalInstallmentRefundStepState.Completed or LocalInstallmentRefundStepState.SupervisorConfirmedRefunded;
    private static bool CanRecreateMissingRepaymentClaim(
        LocalInstallmentOperation operation,
        InstallmentAppendPaymentRequest request) =>
        operation.State == LocalInstallmentOperationState.Prepared &&
        string.IsNullOrWhiteSpace(operation.TerminalAttemptGuid) &&
        string.IsNullOrWhiteSpace(operation.TerminalProcessor) &&
        string.IsNullOrWhiteSpace(operation.ResponseJson) &&
        request.CardTransactions is not { Count: > 0 } &&
        string.IsNullOrWhiteSpace(request.ReservationToken) &&
        (request.Method != PaymentMethodKind.Card ||
         string.IsNullOrWhiteSpace(request.Reference) ||
         // 手动刷卡续付的 MANUAL 引用在收款前就写好了，本身不是批准证据。
         ManualCardPaymentReference.IsManual(request.Reference));

    private static bool CanRecreateMissingCancelClaim(
        LocalInstallmentOperation operation,
        IReadOnlyList<LocalInstallmentRefundStep> steps) =>
        operation.State == LocalInstallmentOperationState.Prepared &&
        string.IsNullOrWhiteSpace(operation.TerminalAttemptGuid) &&
        string.IsNullOrWhiteSpace(operation.TerminalProcessor) &&
        string.IsNullOrWhiteSpace(operation.ResponseJson) &&
        steps.Count > 0 &&
        steps.All(step =>
            step.State == LocalInstallmentRefundStepState.Prepared &&
            string.IsNullOrWhiteSpace(step.RefundReference) &&
            step.SupervisorDecision is null);

    private static bool CanSafelyTerminateCancelBeforeRefund(
        LocalInstallmentOperation operation,
        IReadOnlyList<LocalInstallmentRefundStep> steps) =>
        operation.State == LocalInstallmentOperationState.Prepared &&
        steps.Count > 0 &&
        steps.All(step => step.State == LocalInstallmentRefundStepState.Prepared);

    private static bool IsMatchingCancelClaim(
        LocalInstallmentOperation operation,
        string fingerprint,
        InstallmentCancelClaimDto claim) =>
        claim.InstallmentGuid == operation.InstallmentGuid &&
        claim.OperationGuid == operation.OperationGuid &&
        string.Equals(claim.IdempotencyKey, operation.IdempotencyKey, StringComparison.Ordinal) &&
        string.Equals(claim.RefundPlanFingerprint, fingerprint, StringComparison.Ordinal) &&
        claim.RefundMode == ReadCancelRefundMode(operation);

    // 旧版本落盘的取消请求没有 RefundMode，一律视为原路退，与服务端旧 claim 的空列口径一致。
    private static InstallmentCancelRefundMode ReadCancelRefundMode(LocalInstallmentOperation operation) =>
        InstallmentLifecycleRules.NormalizeRefundMode(
            Deserialize<InstallmentCancelRequest>(operation.RequestJson).RefundMode);

    private async Task<bool> TryMarkCancelFailedAsync(LocalInstallmentOperation operation, string failureMessage)
    {
        if (await repository.TryTransitionAsync(
                operation.OperationGuid,
                [operation.State],
                LocalInstallmentOperationState.Failed,
                DateTimeOffset.UtcNow,
                failureMessage: failureMessage,
                cancellationToken: CancellationToken.None))
        {
            return true;
        }

        return (await repository.GetAsync(operation.OperationGuid, CancellationToken.None))?.State == LocalInstallmentOperationState.Failed;
    }

    private Task<bool> LockCancelForReviewAsync(LocalInstallmentOperation operation, string failureMessage) =>
        repository.TryTransitionAsync(
            operation.OperationGuid,
            [
                LocalInstallmentOperationState.Prepared,
                LocalInstallmentOperationState.TerminalSubmitting,
                LocalInstallmentOperationState.TerminalApproved,
                LocalInstallmentOperationState.ApiSubmitting,
                LocalInstallmentOperationState.ResultUnknown
            ],
            LocalInstallmentOperationState.ResultUnknown,
            DateTimeOffset.UtcNow,
            failureMessage: failureMessage,
            cancellationToken: CancellationToken.None);

    private static IReadOnlyList<InstallmentRefundPaymentCommandDto> BuildApprovedRefundCommands(IReadOnlyList<LocalInstallmentRefundStep> steps) =>
        steps.Where(IsRefundApproved)
            .Select(step => new InstallmentRefundPaymentCommandDto(
                DeterministicGuid($"refund-command:{step.RefundStepGuid:D}"),
                step.Method,
                step.Amount,
                step.RefundReference ?? step.OriginalReference,
                DeserializeTransactions(step.CardTransactionsJson),
                RefundStepIdempotencyKey(step),
                step.OriginalPaymentGuid))
            .ToList();

    private static string RefundStepIdempotencyKey(LocalInstallmentRefundStep step) =>
        $"{step.OperationGuid:D}:refund:{step.OriginalPaymentGuid:D}";

    private static string CreateCancelRefundPlanFingerprint(Guid installmentGuid, IReadOnlyList<LocalInstallmentRefundStep> steps)
    {
        var payments = steps
            .Select(step => new
            {
                PaymentGuid = step.OriginalPaymentGuid.ToString("D"),
                // 指纹描述的是原付款计划（服务端按原付款方式计算），退代金券时步骤方式已改为 Voucher。
                Method = (step.OriginalMethod ?? step.Method) switch
                {
                    PaymentMethodKind.Cash => "cash",
                    PaymentMethodKind.Card => "card",
                    PaymentMethodKind.Voucher => "voucher",
                    _ => throw new InvalidOperationException(InstallmentText.L("installment.text.59162947", "退款计划包含无效付款方式。"))
                },
                AmountCents = ToExactCents(step.Amount)
            })
            .OrderBy(payment => payment.PaymentGuid, StringComparer.Ordinal)
            .ToArray();
        if (payments.Length == 0)
        {
            throw new InvalidOperationException(InstallmentText.L("installment.text.3cc3b228", "分期没有可退款付款。"));
        }

        var material = new StringBuilder();
        material.Append("{\"installmentGuid\":\"")
            .Append(installmentGuid.ToString("D"))
            .Append("\",\"payments\":[");
        for (var index = 0; index < payments.Length; index++)
        {
            if (index > 0) material.Append(',');
            var payment = payments[index];
            material.Append("[\"")
                .Append(payment.PaymentGuid)
                .Append("\",\"")
                .Append(payment.Method)
                .Append("\",")
                .Append(payment.AmountCents)
                .Append(']');
        }
        material.Append("]}");
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material.ToString()))).ToLowerInvariant();
        return $"sha256:{digest}";
    }

    private static long ToExactCents(decimal amount)
    {
        var cents = amount * 100m;
        if (cents != decimal.Truncate(cents) || cents <= 0m || cents > long.MaxValue)
        {
            throw new InvalidOperationException(InstallmentText.L("installment.text.e33eb850", "退款金额无法转换为精确整数分。"));
        }
        return decimal.ToInt64(cents);
    }

    private static InstallmentCancelResponse ToCancelResponse(Guid installmentGuid, InstallmentCancelClaimCommitResponse commit) =>
        new(installmentGuid, commit.Details.Status, commit.Details, commit.AlreadyCancelled, commit.AlreadyCancelled ? InstallmentText.L("installment.text.9faa6b4b", "分期已取消。") : InstallmentText.L("installment.text.ec0c9a1e", "分期取消并退款完成。"));
    private static bool HasExactAuthorizedAmount(PaymentAuthorizationResult authorization, decimal expectedAmount)
    {
        return authorization.AuthorizedAmount is decimal authorizedAmount &&
            authorizedAmount > 0m &&
            decimal.Round(authorizedAmount, 2, MidpointRounding.AwayFromZero) == decimal.Round(expectedAmount, 2, MidpointRounding.AwayFromZero);
    }

    private static bool HasCardRefundEvidence(PaymentAuthorizationResult authorization) =>
        !string.IsNullOrWhiteSpace(authorization.Reference) && authorization.CardTransactions is { Count: > 0 };

    private IDisposable? BeginSquareRefundAttempt(LocalInstallmentRefundStep step)
    {
        if (squarePaymentAttemptContextAccessor is null ||
            !TryParseSquarePaymentId(step.OriginalReference, out _))
        {
            return null;
        }

        var context = new SquarePaymentAttemptContext(
            step.RefundStepGuid,
            ResolveRefundIdempotencyKey(step),
            BindRefundEvidenceAsync: async (refundId, status, updatedAt, environment, _) =>
            {
                // 中文注释：POST 一旦返回 refundId，先用独立取消令牌落盘，再允许终态查询或方法返回。
                var refundReference = $"SQRF:{refundId}";
                var persisted = await repository.TryRecordRefundEvidenceAsync(
                    step.RefundStepGuid,
                    [LocalInstallmentRefundStepState.TerminalSubmitting, LocalInstallmentRefundStepState.ResultUnknown],
                    refundReference,
                    environment.ToString(),
                    SerializeSquareRefundEvidence(refundId, status, step.Amount, updatedAt),
                    updatedAt,
                    cancellationToken: CancellationToken.None);
                if (!persisted)
                {
                    var current = await repository.GetRefundStepAsync(step.RefundStepGuid, CancellationToken.None);
                    if (current is null ||
                        !string.Equals(current.RefundReference, refundReference, StringComparison.Ordinal) ||
                        !string.Equals(current.ProviderEnvironment, environment.ToString(), StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException("Square refund identity could not be persisted safely.");
                    }
                }
            });
        return squarePaymentAttemptContextAccessor.Begin(context);
    }

    private async Task<InstallmentOperationResult<InstallmentConfirmPickupResponse>> SubmitPickupAsync(
        LocalInstallmentOperation operation,
        InstallmentConfirmPickupRequest request,
        CancellationToken cancellationToken,
        bool allowStaleApiSubmittingClaim = false)
    {
        if (!await ClaimApiAsync(operation.OperationGuid, allowStaleApiSubmittingClaim, cancellationToken))
        {
            return new InstallmentOperationResult<InstallmentConfirmPickupResponse>(
                false,
                Message: InstallmentText.L("installment.text.b98ae87c", "提货确认正在恢复或结果未知。"),
                RequiresReview: true);
        }

        try
        {
            var response = await apiClient.ConfirmPickupAsync(request, cancellationToken);
            var local = ToLocalOrder(response.Details);
            if (!await repository.CompleteWithSnapshotAsync(
                    operation.OperationGuid,
                    [LocalInstallmentOperationState.ApiSubmitting],
                    local,
                    JsonSerializer.Serialize(response, JsonOptions),
                    false,
                    DateTimeOffset.UtcNow,
                    cancellationToken))
            {
                return new InstallmentOperationResult<InstallmentConfirmPickupResponse>(
                    false,
                    Message: InstallmentText.L("installment.text.d0aeda1f", "提货确认已提交，本地快照正在安全恢复。"),
                    RequiresReview: true);
            }

            return new InstallmentOperationResult<InstallmentConfirmPickupResponse>(
                true,
                response,
                local,
                InstallmentText.L("installment.text.18912330", "分期单已确认提货。"));
        }
        catch (OperationCanceledException exception)
        {
            await MarkApiUnknownAsync(operation.OperationGuid, "pickup", InstallmentText.L("installment.text.6dcb684f", "提货确认 API 调用已取消，结果未知。"), exception, CancellationToken.None);
            return new InstallmentOperationResult<InstallmentConfirmPickupResponse>(
                false,
                Message: InstallmentText.L("installment.text.79f6184e", "提货确认请求超时，结果可能已提交；已锁定，请刷新恢复，勿重复确认提货。"),
                RequiresReview: true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await MarkApiUnknownAsync(operation.OperationGuid, "pickup", exception.Message, exception, CancellationToken.None);
            return new InstallmentOperationResult<InstallmentConfirmPickupResponse>(
                false,
                Message: InstallmentText.L("installment.text.d34f2164", "提货确认结果未知，已锁定；请刷新恢复，勿重复确认提货。"),
                RequiresReview: true);
        }
    }

    private async Task<bool> RecoverSquareRefundStepsAsync(
        LocalInstallmentOperation operation,
        IReadOnlyList<LocalInstallmentRefundStep> steps,
        PosSessionState session,
        CancellationToken cancellationToken)
    {
        if (cardTerminalSettingsProvider is null || squareTerminalPaymentClient is null ||
            !string.Equals(operation.StoreCode, session.StoreCode, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(operation.DeviceCode, session.DeviceCode, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var candidates = steps.Where(step =>
            step.State is LocalInstallmentRefundStepState.TerminalSubmitting or LocalInstallmentRefundStepState.ResultUnknown &&
            TryParseSquarePaymentId(step.OriginalReference, out _) &&
            TryParseSquareRefundId(step.RefundReference, out _)).ToList();
        if (candidates.Count == 0)
        {
            return false;
        }

        CardTerminalSettings settings;
        try
        {
            settings = await cardTerminalSettingsProvider.GetSettingsAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ConsoleLog.Write("InstallmentRecovery", $"Square settings lookup failed operationGuid={operation.OperationGuid} error={exception.GetType().Name}");
            foreach (var step in candidates)
            {
                await MarkSquareRefundUnknownAsync(step, InstallmentText.L("installment.text.daf67696", "Square 环境配置读取失败，保持锁定。"), cancellationToken: CancellationToken.None);
            }
            return false;
        }

        var rejectedRefundReset = false;
        foreach (var step in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TryParseSquarePaymentId(step.OriginalReference, out var paymentId);
            TryParseSquareRefundId(step.RefundReference, out var refundId);

            var recoverySettings = settings;
            if (!string.IsNullOrWhiteSpace(step.ProviderEnvironment))
            {
                if (!Enum.TryParse<CardTerminalEnvironment>(step.ProviderEnvironment, ignoreCase: true, out var providerEnvironment))
                {
                    await MarkSquareRefundUnknownAsync(step, InstallmentText.L("installment.text.15be2f34", "Square 原退款环境无效，保持锁定。"), cancellationToken: CancellationToken.None);
                    continue;
                }

                // 后端按 environment 选择对应 Square 凭据；恢复必须使用创建退款时落盘的环境。
                recoverySettings = settings with
                {
                    Environment = providerEnvironment,
                    SquareApiBaseUrl = CardTerminalSettings.GetSquareApiBaseUrl(providerEnvironment)
                };
            }

            SquareRefundStatusResult refund;
            try
            {
                refund = await squareTerminalPaymentClient.GetRefundAsync(recoverySettings, refundId!, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                ConsoleLog.Write("InstallmentRecovery", $"Square refund lookup failed refundStepGuid={step.RefundStepGuid} refundId={refundId} error={exception.GetType().Name}");
                await MarkSquareRefundUnknownAsync(step, InstallmentText.L("installment.text.df87593f", "Square 退款查询失败，保持锁定。"), cancellationToken: CancellationToken.None);
                continue;
            }

            if (!string.Equals(refund.RefundId, refundId, StringComparison.Ordinal) ||
                !string.Equals(refund.PaymentId, paymentId, StringComparison.Ordinal) ||
                refund.AmountCents != ToExactCents(step.Amount) ||
                !string.Equals(refund.Currency, "AUD", StringComparison.OrdinalIgnoreCase))
            {
                // 响应身份不可信时不得覆盖已落盘的权威 refundId。
                await MarkSquareRefundUnknownAsync(step, InstallmentText.L("installment.text.7ea18c0f", "Square 退款身份、原付款、金额或币种不匹配，保持锁定。"), cancellationToken: CancellationToken.None);
                continue;
            }

            if (string.Equals(refund.Status, "COMPLETED", StringComparison.OrdinalIgnoreCase))
            {
                await repository.TryTransitionRefundStepAsync(
                    step.RefundStepGuid,
                    [LocalInstallmentRefundStepState.TerminalSubmitting, LocalInstallmentRefundStepState.ResultUnknown],
                    LocalInstallmentRefundStepState.Approved,
                    refund.UpdatedAt ?? DateTimeOffset.UtcNow,
                    refundReference: $"SQRF:{refund.RefundId}",
                    cardTransactionsJson: SerializeSquareRefundEvidence(refund.RefundId, refund.Status, step.Amount, refund.UpdatedAt),
                    cancellationToken: CancellationToken.None);
                continue;
            }

            if (string.Equals(refund.Status, "FAILED", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(refund.Status, "REJECTED", StringComparison.OrdinalIgnoreCase))
            {
                // 身份与金额均已核验的拒绝终态没有退款副作用；清除旧身份并轮换幂等键后才允许人工重试。
                rejectedRefundReset |= await repository.TryResetRefundStepAfterDeclineAsync(
                    step.RefundStepGuid,
                    [LocalInstallmentRefundStepState.TerminalSubmitting, LocalInstallmentRefundStepState.ResultUnknown],
                    Guid.NewGuid().ToString("N"),
                    refund.UpdatedAt ?? DateTimeOffset.UtcNow,
                    InstallmentText.L("installment.text.51dfe95a", "Square 退款状态为 {0}。", refund.Status),
                    CancellationToken.None);
                continue;
            }

            var message = string.Equals(refund.Status, "PENDING", StringComparison.OrdinalIgnoreCase)
                ? InstallmentText.L("installment.text.3d872fba", "Square 退款仍在处理中，保持锁定并稍后恢复。")
                : InstallmentText.L("installment.text.eb20ae5f", "Square 退款状态为 {0}，仅 COMPLETED 可自动确认，保持锁定。", refund.Status);
            await MarkSquareRefundUnknownAsync(step, message, refund, CancellationToken.None);
        }

        return rejectedRefundReset;
    }

    private async Task<bool> TryFinalizeRejectedCancelRecoveryAsync(
        LocalInstallmentOperation operation,
        IReadOnlyList<LocalInstallmentRefundStep> steps)
    {
        if (steps.Count == 0 || steps.Any(IsRefundApproved) ||
            steps.Any(step => step.State != LocalInstallmentRefundStepState.Prepared))
        {
            return false;
        }

        var declined = await TryResolveCancelClaimAsync(
            operation,
            InstallmentCancelClaimResolveOutcome.Declined,
            [],
            CancellationToken.None);
        if (!declined)
        {
            return false;
        }

        var failed = await repository.TryTransitionAsync(
            operation.OperationGuid,
            [LocalInstallmentOperationState.TerminalSubmitting, LocalInstallmentOperationState.ResultUnknown],
            LocalInstallmentOperationState.Failed,
            DateTimeOffset.UtcNow,
            failureMessage: InstallmentText.L("installment.text.526dd732", "Square 退款已明确拒绝，可重新发起取消。"),
            cancellationToken: CancellationToken.None);
        return failed ||
            (await repository.GetAsync(operation.OperationGuid, CancellationToken.None))?.State == LocalInstallmentOperationState.Failed;
    }

    private static bool HasSquareRejectedRefundReset(IReadOnlyList<LocalInstallmentRefundStep> steps) =>
        steps.Any(step =>
            step.State == LocalInstallmentRefundStepState.Prepared &&
            TryParseSquarePaymentId(step.OriginalReference, out _) &&
            (string.Equals(step.FailureMessage, InstallmentText.L("installment.text.50a45e3f", "Square 退款状态为 FAILED。"), StringComparison.OrdinalIgnoreCase) ||
             string.Equals(step.FailureMessage, InstallmentText.L("installment.text.fd8e0390", "Square 退款状态为 REJECTED。"), StringComparison.OrdinalIgnoreCase)));

    private Task<bool> MarkSquareRefundUnknownAsync(
        LocalInstallmentRefundStep step,
        string message,
        SquareRefundStatusResult? refund = null,
        CancellationToken cancellationToken = default) =>
        repository.TryTransitionRefundStepAsync(
            step.RefundStepGuid,
            [LocalInstallmentRefundStepState.TerminalSubmitting, LocalInstallmentRefundStepState.ResultUnknown],
            LocalInstallmentRefundStepState.ResultUnknown,
            refund?.UpdatedAt ?? DateTimeOffset.UtcNow,
            refundReference: refund is null ? null : $"SQRF:{refund.RefundId}",
            cardTransactionsJson: refund is null ? null : SerializeSquareRefundEvidence(refund.RefundId, refund.Status, step.Amount, refund.UpdatedAt),
            failureMessage: message,
            cancellationToken: cancellationToken);

    private static string SerializeSquareRefundEvidence(string refundId, string status, decimal amount, DateTimeOffset? occurredAt = null) =>
        JsonSerializer.Serialize<IReadOnlyList<CardTransactionDto>>(
            [new CardTransactionDto("Square", refundId, null, null, null, null, null, null, status, null, occurredAt ?? DateTimeOffset.UtcNow, amount, null)],
            JsonOptions);

    private static string? SerializeTransactions(IReadOnlyList<CardTransactionDto>? transactions) =>
        transactions is null ? null : JsonSerializer.Serialize(transactions, JsonOptions);

    private static bool TryParseSquarePaymentId(string? reference, out string? paymentId) =>
        TryParseSquareReference(reference, "SQ:", out paymentId);

    private static bool TryParseSquareRefundId(string? reference, out string? refundId) =>
        TryParseSquareReference(reference, "SQRF:", out refundId);

    private static bool TryParseSquareReference(string? reference, string prefix, out string? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(reference))
        {
            return false;
        }

        var trimmed = reference.Trim();
        if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        value = trimmed[prefix.Length..].Trim();
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool IsLinklyRefundStep(LocalInstallmentRefundStep step) =>
        step.Method == PaymentMethodKind.Card &&
        (DeserializeTransactions(step.CardTransactionsJson)?.Any(transaction =>
            string.Equals(transaction.Processor, "ANZ", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(transaction.Processor, "Linkly", StringComparison.OrdinalIgnoreCase)) == true ||
         step.OriginalReference?.StartsWith("ANZ:", StringComparison.OrdinalIgnoreCase) == true);

    private async Task<bool> ShouldGenerateLocalLinklyRetryReferenceAsync(LocalInstallmentRefundStep step)
    {
        if (!IsLinklyRefundStep(step) || cardTerminalSettingsProvider is null)
        {
            return false;
        }

        var settings = await cardTerminalSettingsProvider.GetSettingsAsync(CancellationToken.None);
        return settings.Processor == CardProcessorKind.Linkly &&
            CardTerminalSettings.NormalizeLinklyConnectionMode(settings.LinklyConnectionMode) == LinklyConnectionMode.LocalIp;
    }

    // 中文注释：Square 复用业务幂等键；LocalIp Linkly 复用已落盘的退款 TxnRef，原交易引用仍独立传递。
    private static string ResolveRefundIdempotencyKey(LocalInstallmentRefundStep step) =>
        IsLinklyRefundStep(step) && !string.IsNullOrWhiteSpace(step.RefundReference)
            ? step.RefundReference
            : step.IdempotencyKey;

    private static IReadOnlyList<CardTransactionDto>? DeserializeTransactions(string? json) => string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<IReadOnlyList<CardTransactionDto>>(json, JsonOptions);
    private static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, JsonOptions) ?? throw new InvalidOperationException(InstallmentText.L("installment.text.c6c0d619", "分期操作快照无效。"));
    private static Guid DeterministicGuid(string value)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(value));
        return new Guid(hash);
    }

    private sealed record TerminalReady(bool Succeeded, LocalInstallmentOperation? Operation, string? Message, bool RequiresReview)
    {
        public static TerminalReady Success(LocalInstallmentOperation operation) => new(true, operation, null, false);
        public static TerminalReady Failed(string message) => new(false, null, message, false);
        public static TerminalReady Unknown(string message) => new(false, null, message, true);
    }

    private sealed record RepaymentClaimReady(
        bool Succeeded,
        InstallmentRepaymentClaimDto? Claim,
        InstallmentAppendPaymentResponse? Response,
        string? Message,
        bool RequiresReview,
        CardTerminalSettings? CardSettings = null)
    {
        public static RepaymentClaimReady Success(InstallmentRepaymentClaimDto claim, CardTerminalSettings? cardSettings = null) =>
            new(true, claim, null, null, false, cardSettings);
        public static RepaymentClaimReady Committed(InstallmentRepaymentClaimDto claim, InstallmentAppendPaymentResponse response) => new(true, claim, response, null, false);
        public static RepaymentClaimReady Failed(string message, bool requiresReview) => new(false, null, null, message, requiresReview);
    }

    private sealed record CancelClaimReady(
        bool Succeeded,
        InstallmentCancelClaimDto? Claim,
        InstallmentCancelResponse? Response,
        string? Message,
        bool RequiresReview)
    {
        public static CancelClaimReady Success(InstallmentCancelClaimDto claim) => new(true, claim, null, null, false);
        public static CancelClaimReady Committed(InstallmentCancelClaimDto claim, InstallmentCancelResponse response) => new(true, claim, response, null, false);
        public static CancelClaimReady Failed(string message, bool requiresReview) => new(false, null, null, message, requiresReview);
    }

    private sealed record TerminalAttemptRecovery(
        bool Approved,
        bool Rejected,
        string? Reference,
        IReadOnlyList<CardTransactionDto>? Transactions)
    {
        public static TerminalAttemptRecovery Unknown { get; } = new(false, false, null, null);
        public static TerminalAttemptRecovery RejectedResult { get; } = new(false, true, null, null);
    }

    private sealed class TerminalAttemptScope(
        Guid? attemptGuid,
        string? processor,
        Func<IDisposable?>? beginContext,
        Func<PaymentAuthorizationResult, bool>? hasVerifiedApprovalEvidence,
        Func<PaymentAuthorizationResult, CancellationToken, Task>? recordOutcomeAsync,
        bool requiresReview = false,
        string? reviewMessage = null,
        Func<decimal, PosSessionState, CancellationToken, Task<PaymentAuthorizationResult>>? authorizeAsync = null) : IDisposable
    {
        public static TerminalAttemptScope Empty { get; } = new(null, null, null, null, null);

        /// <summary>当前设置是暂不支持分期的处理器（Zeller）。调用方据此在登记尝试、推进状态之前拦下。</summary>
        public static TerminalAttemptScope UnsupportedZeller { get; } = new(null, CardProcessorKind.Zeller.ToString(), null, null, null);

        public bool IsUnsupportedProcessor => ReferenceEquals(this, UnsupportedZeller);
        public Guid? AttemptGuid { get; } = attemptGuid;
        public string? Processor { get; } = processor;
        public bool RequiresReview { get; } = requiresReview;
        public string? ReviewMessage { get; } = reviewMessage;

        public static TerminalAttemptScope ForReview(Guid attemptGuid, string? processor, string message) =>
            new(attemptGuid, processor, null, null, null, true, message);

        public IDisposable? BeginContext() => beginContext?.Invoke();

        /// <summary>通道自带的授权调用（Zeller 走本机 SDK，不经过 ICardTerminalClient）；为空时用通用刷卡客户端。</summary>
        public Task<PaymentAuthorizationResult>? AuthorizeAsync(decimal amount, PosSessionState session, CancellationToken cancellationToken) =>
            authorizeAsync?.Invoke(amount, session, cancellationToken);

        public bool HasVerifiedApprovalEvidence(PaymentAuthorizationResult authorization) =>
            hasVerifiedApprovalEvidence?.Invoke(authorization) ?? true;

        public Task RecordOutcomeAsync(PaymentAuthorizationResult authorization, CancellationToken cancellationToken) =>
            recordOutcomeAsync?.Invoke(authorization, cancellationToken) ?? Task.CompletedTask;

        public void Dispose()
        {
        }
    }
}
