using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BlazorApp.Shared.DTOs;
using Hbpos.Client.Wpf.Models;
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
    bool RequiresReview = false);

/// <summary>供创建、补款、取消页面和启动恢复共用的窄服务边界。</summary>
public interface IInstallmentOperationService
{
    Task<InstallmentOperationResult<InstallmentCreateResponse>> ExecuteCreateAsync(
        PosSessionState session,
        InstallmentCreateRequest request,
        bool authorizeCard,
        CancellationToken cancellationToken = default);

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
            Message: "安全提货确认服务未配置。",
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
    ILinklyBackendTerminalClient? linklyBackendTerminalClient = null) : IInstallmentOperationService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan ApiClaimLease = TimeSpan.FromMinutes(2);
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
                    Message: "A previous installment creation is still unresolved. Recover it before collecting payment again.",
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

    private async Task<InstallmentOperationResult<InstallmentAppendPaymentResponse>> ExecuteRepaymentCoreAsync(
        PosSessionState session,
        InstallmentAppendPaymentRequest request,
        bool authorizeCard,
        CancellationToken cancellationToken = default)
    {
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

        var claim = await EnsureRepaymentClaimAsync(operation, request, cancellationToken);
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
                Message: "补款 provider 结果正在由发起设备恢复，请勿再次收款。",
                RequiresReview: true);
        }

        var ready = await EnsureRepaymentTerminalApprovalAsync(operation, session, authorizeCard, cancellationToken);
        if (!ready.Succeeded)
        {
            return new InstallmentOperationResult<InstallmentAppendPaymentResponse>(false, Message: ready.Message, RequiresReview: ready.RequiresReview);
        }

        var approvedOperation = ready.Operation!;
        var approvedRequest = Deserialize<InstallmentAppendPaymentRequest>(approvedOperation.RequestJson);
        return await SubmitRepaymentAsync(approvedOperation, approvedRequest, cancellationToken);
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
                Message: "提货操作身份与本地记录不一致，保持锁定等待核对。",
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
                "分期单已确认提货。");
        }

        // API 已发出或结果未知时只能由恢复流程重放同一幂等请求，普通点击不得再次 POST。
        if (operation.State is LocalInstallmentOperationState.ApiSubmitting or LocalInstallmentOperationState.ResultUnknown)
        {
            return new InstallmentOperationResult<InstallmentConfirmPickupResponse>(
                false,
                Message: "提货确认结果未知，已锁定；请刷新恢复，勿重复确认。",
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
                Message: "提货确认正在恢复或已结束，请刷新核对。",
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
        refundMode = InstallmentLifecycleRules.NormalizeRefundMode(refundMode);
        if (!string.Equals(localOrder.StoreCode, session.StoreCode, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(localOrder.DeviceCode, session.DeviceCode, StringComparison.OrdinalIgnoreCase))
        {
            return new InstallmentOperationResult<InstallmentCancelResponse>(false, Message: "取消退款仅允许分期创建设备执行。");
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

    private async Task<IReadOnlyList<InstallmentOperationRecoveryResult>> RecoverCoreAsync(PosSessionState session, CancellationToken cancellationToken = default)
    {
        var recoverable = await repository.GetRecoverableAsync(session.StoreCode, cancellationToken);
        var results = new List<InstallmentOperationRecoveryResult>();
        foreach (var operation in recoverable)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                            failureMessage: "重启时退款终端结果未知，等待主管结案。",
                            cancellationToken: CancellationToken.None);
                    }

                    await repository.TryTransitionAsync(
                        operation.OperationGuid,
                        [LocalInstallmentOperationState.TerminalSubmitting],
                        LocalInstallmentOperationState.ResultUnknown,
                        DateTimeOffset.UtcNow,
                        failureMessage: "重启时退款终端结果未知，禁止自动重试。",
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
                                failureMessage: "重启前尚未调用退款 provider，可重新发起取消。",
                                cancellationToken: CancellationToken.None);
                            results.Add(new InstallmentOperationRecoveryResult(operation.OperationGuid, operation.Kind, LocalInstallmentOperationState.Failed, false, "尚未调用退款 provider，可重新发起取消。"));
                        }
                        else
                        {
                            results.Add(new InstallmentOperationRecoveryResult(operation.OperationGuid, operation.Kind, LocalInstallmentOperationState.ResultUnknown, false, "中央取消 claim 结案失败，保持锁定。"));
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
                            results.Add(new InstallmentOperationRecoveryResult(operation.OperationGuid, operation.Kind, LocalInstallmentOperationState.Failed, false, "Square 退款已明确拒绝，可重新发起取消。"));
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
                        results.Add(new InstallmentOperationRecoveryResult(operation.OperationGuid, operation.Kind, LocalInstallmentOperationState.ResultUnknown, false, "退款结果未知，保持锁定。"));
                    }
                    continue;
                }

                await repository.TryTransitionAsync(
                    operation.OperationGuid,
                    [LocalInstallmentOperationState.TerminalSubmitting],
                    LocalInstallmentOperationState.ResultUnknown,
                    DateTimeOffset.UtcNow,
                    failureMessage: "重启时终端结果未知，禁止自动重试。",
                    cancellationToken: cancellationToken);
                var resumed = await repository.GetAsync(operation.OperationGuid, cancellationToken);
                if (resumed is not null && !string.IsNullOrWhiteSpace(resumed.TerminalAttemptGuid))
                {
                    var resumedResult = resumed.Kind switch
                    {
                        LocalInstallmentOperationKind.Create => await RecoverCreateAsync(resumed, session, cancellationToken),
                        LocalInstallmentOperationKind.Repayment => await RecoverRepaymentAsync(resumed, session, cancellationToken),
                        _ => new InstallmentOperationRecoveryResult(resumed.OperationGuid, resumed.Kind, LocalInstallmentOperationState.ResultUnknown, false, "终端结果未知，保持锁定。")
                    };
                    results.Add(resumedResult);
                    continue;
                }

                results.Add(new InstallmentOperationRecoveryResult(operation.OperationGuid, operation.Kind, LocalInstallmentOperationState.ResultUnknown, false, "终端结果未知，保持锁定。"));
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

                results.Add(new InstallmentOperationRecoveryResult(operation.OperationGuid, operation.Kind, operation.State, false, "未提交终端的草稿保留给用户继续。"));
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
                    _ => new InstallmentOperationRecoveryResult(operation.OperationGuid, operation.Kind, operation.State, false, "退款步骤需由取消操作处理。")
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
                results.Add(new InstallmentOperationRecoveryResult(operation.OperationGuid, operation.Kind, LocalInstallmentOperationState.ResultUnknown, false, "恢复 API 结果未知，保持锁定。"));
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
        if (operation is null || operation.Kind != LocalInstallmentOperationKind.Cancel)
        {
            return new InstallmentOperationResult<InstallmentCancelResponse>(false, Message: "未找到待主管结案的取消操作。");
        }

        return await ContinueCancelAsync(operation, installmentNumber, session, true, cancellationToken);
    }

    private async Task<RepaymentClaimReady> EnsureRepaymentClaimAsync(
        LocalInstallmentOperation operation,
        InstallmentAppendPaymentRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var capabilities = await apiClient.GetRepaymentCapabilitiesAsync(cancellationToken);
            if (!capabilities.RepaymentClaimsSupported)
            {
                return RepaymentClaimReady.Failed("当前服务端不支持安全补款 claim，已在 provider 调用前停止。", requiresReview: true);
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
                    ? RepaymentClaimReady.Failed("中央补款已提交但缺少结果快照，保持锁定等待对账。", requiresReview: true)
                    : RepaymentClaimReady.Committed(claim, claim.Commit);
            }

            if (claim.Status is InstallmentRepaymentClaimStatus.Released or InstallmentRepaymentClaimStatus.Declined)
            {
                await repository.TryTransitionAsync(
                    operation.OperationGuid,
                    [LocalInstallmentOperationState.Prepared],
                    LocalInstallmentOperationState.Failed,
                    DateTimeOffset.UtcNow,
                    failureMessage: $"中央补款 claim 已结束：{claim.Status}",
                    cancellationToken: CancellationToken.None);
                return RepaymentClaimReady.Failed("原补款 claim 已结束，请重新发起付款。", requiresReview: false);
            }

            return RepaymentClaimReady.Success(claim);
        }
        catch (CatalogApiException exception) when (exception.ErrorCode is
            "INSTALLMENT_REPAYMENT_BUSY" or
            "INSTALLMENT_REPAYMENT_CLAIM_MISMATCH")
        {
            LogOperationIssue(operation.OperationGuid, "repayment-claim-create", "Failed (provider not called)", exception);
            await repository.TryTransitionAsync(
                operation.OperationGuid,
                [LocalInstallmentOperationState.Prepared],
                LocalInstallmentOperationState.Failed,
                DateTimeOffset.UtcNow,
                failureMessage: exception.Message,
                cancellationToken: CancellationToken.None);
            return RepaymentClaimReady.Failed(
                exception.ErrorCode == "INSTALLMENT_REPAYMENT_BUSY"
                    ? "该分期正在另一台收银机付款，本机未调用 provider。"
                    : "补款 claim 与本地付款指纹不一致，本机未调用 provider。",
                requiresReview: false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogOperationIssue(operation.OperationGuid, "repayment-claim-create", "Stopped before provider", exception);
            return RepaymentClaimReady.Failed($"创建补款 claim 失败，已在 provider 调用前停止：{exception.Message}", requiresReview: true);
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
                : TerminalReady.Unknown($"中央补款 claim 状态为 {claim.Status}，禁止调用 provider。");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogOperationIssue(operation.OperationGuid, "repayment-begin-provider", "Unknown (provider not called)", exception);
            return TerminalReady.Unknown($"登记补款 provider 失败，未调用 provider：{exception.Message}");
        }
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
                failureMessage: $"中央补款 claim 结案失败：{exception.Message}",
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
            return new InstallmentOperationResult<InstallmentAppendPaymentResponse>(false, Message: "中央补款已提交，本地快照正在恢复。", RequiresReview: true);
        }

        await TryAcknowledgeLinklyBackendSessionAsync(operation.OperationGuid);
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
                return CancelClaimReady.Failed("当前服务端不支持安全取消 claim，已在退款前停止。", requiresReview: true);
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
                    const string message = "远端取消 claim 缺失，但本地已越过退款 provider 前边界；保持锁定并等待人工对账。";
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
                return CancelClaimReady.Failed("取消 claim 与本地耐久退款计划不一致，未调用退款 provider。", requiresReview: true);
            }

            if (claim.Status == InstallmentCancelClaimStatus.Committed)
            {
                return claim.Commit is null
                    ? CancelClaimReady.Failed("中央取消已提交但缺少结果快照，保持锁定等待对账。", requiresReview: true)
                    : CancelClaimReady.Committed(claim, ToCancelResponse(operation.InstallmentGuid, claim.Commit));
            }

            if (claim.Status is InstallmentCancelClaimStatus.Released or InstallmentCancelClaimStatus.Declined)
            {
                await repository.TryTransitionAsync(
                    operation.OperationGuid,
                    [operation.State],
                    LocalInstallmentOperationState.Failed,
                    DateTimeOffset.UtcNow,
                    failureMessage: $"中央取消 claim 已结束：{claim.Status}",
                    cancellationToken: CancellationToken.None);
                return CancelClaimReady.Failed("原取消 claim 已结束，请重新发起取消。", requiresReview: false);
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
                "INSTALLMENT_MUTATION_BUSY" => "该分期正在执行付款或取消，本机未调用退款 provider。",
                "INSTALLMENT_CANCEL_REFUND_METHOD_UNSUPPORTED" => "当前服务端不支持该退款方式，本机未调用退款 provider。",
                _ => "取消 claim 与服务端权威退款计划不一致，本机未调用退款 provider。"
            };
            return CancelClaimReady.Failed(
                message,
                requiresReview: !terminated);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogOperationIssue(operation.OperationGuid, "cancel-claim-ensure", "Stopped before refund", exception);
            return CancelClaimReady.Failed($"创建或读取取消 claim 失败，已在退款前停止：{exception.Message}", requiresReview: true);
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
            return CancelClaimReady.Failed($"中央取消 claim 状态为 {claim.Status}，禁止调用退款 provider。", requiresReview: true);
        }

        try
        {
            var begun = await apiClient.BeginCancelRefundAsync(operation.InstallmentGuid, operation.OperationGuid, cancellationToken);
            return begun.Status == InstallmentCancelClaimStatus.RefundPending
                ? CancelClaimReady.Success(begun)
                : CancelClaimReady.Failed($"中央取消 claim 状态为 {begun.Status}，禁止调用退款 provider。", requiresReview: true);
        }
        catch (CatalogApiException exception) when (exception.ErrorCode == "INSTALLMENT_CANCEL_REFUND_METHOD_UNSUPPORTED")
        {
            LogOperationIssue(operation.OperationGuid, "cancel-begin-refund", "Failed (refund provider not called)", exception);
            var steps = await repository.GetRefundStepsAsync(operation.OperationGuid, CancellationToken.None);
            if (claim.Status != InstallmentCancelClaimStatus.Prepared ||
                !CanSafelyTerminateCancelBeforeRefund(operation, steps))
            {
                await LockCancelForReviewAsync(operation, exception.Message);
                return CancelClaimReady.Failed("取消退款方式不受支持，但本地已越过安全终结边界；保持锁定等待人工对账。", requiresReview: true);
            }

            var released = await TryResolveCancelClaimAsync(
                operation,
                InstallmentCancelClaimResolveOutcome.Released,
                [],
                CancellationToken.None);
            if (!released)
            {
                await LockCancelForReviewAsync(operation, "中央取消 claim 释放失败，保持锁定等待人工对账。");
                return CancelClaimReady.Failed("中央取消 claim 释放失败，保持锁定等待人工对账。", requiresReview: true);
            }

            if (!await TryMarkCancelFailedAsync(operation, "当前服务端不支持该退款方式，中央 claim 已安全释放。"))
            {
                await LockCancelForReviewAsync(operation, "中央取消 claim 已释放，但本地终态落盘失败。");
                return CancelClaimReady.Failed("中央取消 claim 已释放，但本地终态落盘失败。", requiresReview: true);
            }

            return CancelClaimReady.Failed("当前服务端不支持该退款方式，本机未调用退款 provider，可重新发起取消。", requiresReview: false);
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
                    await LockCancelForReviewAsync(operation, "取消 claim 过期后的远端终态无法安全确认。");
                    return CancelClaimReady.Failed("取消 claim 过期后的远端终态无法安全确认，保持锁定等待人工对账。", requiresReview: true);
                }

                if (!await TryMarkCancelFailedAsync(operation, $"中央取消 claim 已结束：{current.Status}"))
                {
                    await LockCancelForReviewAsync(operation, "中央取消 claim 已结束，但本地终态落盘失败。");
                    return CancelClaimReady.Failed("中央取消 claim 已结束，但本地终态落盘失败。", requiresReview: true);
                }

                return CancelClaimReady.Failed("原取消 claim 已过期结束，请重新发起取消。", requiresReview: false);
            }
            catch (Exception refreshException)
            {
                LogOperationIssue(operation.OperationGuid, "cancel-claim-refresh", "Locked for review", refreshException);
                await LockCancelForReviewAsync(operation, $"取消 claim 过期后重新读取失败：{refreshException.Message}");
                return CancelClaimReady.Failed("取消 claim 过期后的远端状态读取失败，保持锁定等待人工对账。", requiresReview: true);
            }
        }
        catch (Exception exception)
        {
            LogOperationIssue(operation.OperationGuid, "cancel-begin-refund", "Failed (refund provider not called)", exception);
            return CancelClaimReady.Failed($"登记取消退款阶段失败，未调用退款 provider：{exception.Message}", requiresReview: true);
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
                failureMessage: $"中央取消 claim 结案失败：{exception.Message}",
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
            return new InstallmentOperationResult<InstallmentCancelResponse>(false, Message: "中央取消已提交，本地快照正在恢复。", RequiresReview: true);
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

    private async Task<TerminalReady> EnsureRepaymentTerminalApprovalAsync(LocalInstallmentOperation operation, PosSessionState session, bool authorizeCard, CancellationToken cancellationToken)
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
                return TerminalReady.Unknown("无法确定具体银行卡处理器，未登记 claim begin 且未调用付款 provider。");
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
            beginRepaymentClaim: true);
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
            return TerminalReady.Unknown("礼券补款正在恢复，请勿重复预占。");
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
                    failureMessage: authorization.Message ?? "礼券预占结果未知。",
                    cancellationToken: CancellationToken.None);
                await TryResolveRepaymentClaimAsync(operation, InstallmentRepaymentClaimResolveOutcome.Unknown, CancellationToken.None);
                return TerminalReady.Unknown("礼券预占结果未知，请勿重复操作。");
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
                            failureMessage: "礼券不足额预占未确认释放。",
                            cancellationToken: CancellationToken.None);
                        await TryResolveRepaymentClaimAsync(operation, InstallmentRepaymentClaimResolveOutcome.Unknown, CancellationToken.None);
                        return TerminalReady.Unknown("礼券预占释放结果未知，保持锁定等待对账。");
                    }
                }
                else if (authorization.Approved)
                {
                    await repository.TryTransitionAsync(
                        operation.OperationGuid,
                        [LocalInstallmentOperationState.TerminalSubmitting],
                        LocalInstallmentOperationState.ResultUnknown,
                        DateTimeOffset.UtcNow,
                        failureMessage: "礼券已部分批准但缺少可释放的 reservation token。",
                        cancellationToken: CancellationToken.None);
                    await TryResolveRepaymentClaimAsync(operation, InstallmentRepaymentClaimResolveOutcome.Unknown, CancellationToken.None);
                    return TerminalReady.Unknown("礼券部分批准证据不完整，保持锁定等待对账。");
                }

                await repository.TryTransitionAsync(
                    operation.OperationGuid,
                    [LocalInstallmentOperationState.TerminalSubmitting],
                    LocalInstallmentOperationState.Failed,
                    DateTimeOffset.UtcNow,
                    failureMessage: authorization.Message ?? "礼券未获批准。",
                    cancellationToken: CancellationToken.None);
                await TryResolveRepaymentClaimAsync(operation, InstallmentRepaymentClaimResolveOutcome.Declined, CancellationToken.None);
                return TerminalReady.Failed(authorization.Message ?? "礼券未获批准。");
            }

            var (voucherCode, reservationToken) = OrderUploadService.ParseVoucherReference(authorization.Reference);
            if (string.IsNullOrWhiteSpace(voucherCode) || string.IsNullOrWhiteSpace(reservationToken))
            {
                await repository.TryTransitionAsync(
                    operation.OperationGuid,
                    [LocalInstallmentOperationState.TerminalSubmitting],
                    LocalInstallmentOperationState.ResultUnknown,
                    DateTimeOffset.UtcNow,
                    failureMessage: "礼券已批准但缺少可恢复的 reservation token。",
                    cancellationToken: CancellationToken.None);
                await TryResolveRepaymentClaimAsync(operation, InstallmentRepaymentClaimResolveOutcome.Unknown, CancellationToken.None);
                return TerminalReady.Unknown("礼券批准证据不完整，保持锁定等待对账。");
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
                return TerminalReady.Unknown("礼券批准结果未能持久化，保持锁定等待恢复。");
            }

            var approved = await repository.GetAsync(operation.OperationGuid, CancellationToken.None)
                ?? throw new InvalidOperationException("礼券补款批准状态未保存。");
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
            return TerminalReady.Unknown("礼券预占结果未知，请勿重复操作。");
        }
    }

    private async Task<TerminalReady> ApproveCardAsync(LocalInstallmentOperation operation, decimal amount, PosSessionState session, Func<PaymentAuthorizationResult, string> approvedRequestFactory, CancellationToken cancellationToken, bool beginRepaymentClaim = false)
    {
        TerminalAttemptScope? terminalAttempt = null;
        try
        {
            terminalAttempt = await CreateTerminalAttemptScopeAsync(operation, amount, session, cancellationToken);
            if (terminalAttempt.RequiresReview)
            {
                var reviewMessage = terminalAttempt.ReviewMessage ?? "银行卡 attempt 无法按当前终端模式安全复用，保持锁定等待主管恢复。";
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
                    return TerminalReady.Unknown("无法确定具体银行卡处理器，未登记 claim begin 且未调用付款 provider。");
                }

                var begin = await BeginRepaymentProviderAsync(operation, provider, providerAttemptId, cancellationToken);
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
                return TerminalReady.Unknown("终端操作正在恢复，请勿重复收款。");
            }
            using var terminalContext = terminalAttempt.BeginContext();
            var authorization = await cardTerminalClient.AuthorizeAsync(amount, session, cancellationToken);
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
                return TerminalReady.Unknown(authorization.Message ?? "终端结果未知，请勿重复收款。");
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
                return TerminalReady.Failed(authorization.Message ?? "银行卡未获批准。");
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
                    failureMessage: "Terminal approved amount did not match the persisted installment amount.",
                    cancellationToken: CancellationToken.None);
                if (beginRepaymentClaim)
                {
                    await TryResolveRepaymentClaimAsync(operation, InstallmentRepaymentClaimResolveOutcome.Unknown, CancellationToken.None);
                }
                return TerminalReady.Unknown("终端批准金额与分期金额不一致，保持锁定，请勿重复收款。");
            }

            await terminalAttempt.RecordOutcomeAsync(authorization, CancellationToken.None);
            var requestJson = approvedRequestFactory(authorization);
            if (!await repository.TryTransitionAsync(operation.OperationGuid, [LocalInstallmentOperationState.TerminalSubmitting], LocalInstallmentOperationState.TerminalApproved, DateTimeOffset.UtcNow, requestJson: requestJson, terminalAttemptGuid: terminalAttemptGuid ?? authorization.SessionId ?? authorization.TxnRef, terminalProcessor: authorization.Processor ?? terminalAttempt.Processor, cancellationToken: CancellationToken.None))
            {
                return TerminalReady.Unknown("Terminal approval could not be durably recorded; payment remains locked.");
            }
            var approved = await repository.GetAsync(operation.OperationGuid, CancellationToken.None)
                ?? throw new InvalidOperationException("终端批准状态未保存。");
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
            return TerminalReady.Unknown("终端结果未知，请勿重复收款。");
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
            return TerminalReady.Unknown("终端结果未知，请勿重复收款。");
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
        CancellationToken cancellationToken)
    {
        if (cardTerminalSettingsProvider is null)
        {
            return TerminalAttemptScope.Empty;
        }

        var settings = await cardTerminalSettingsProvider.GetSettingsAsync(cancellationToken);
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
                    "持久化的银行卡 attempt 与当前 Linkly 连接模式或 Local IP 引用不一致，保持锁定等待主管恢复。");
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
                            throw new InvalidOperationException("分期付款 attempt 已被其他任务推进、终态化或主管结案。");
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
            return TerminalReady.Unknown("终端或 API 结果未知，请先恢复或由主管处理。");
        }

        var transitioned = await repository.TryTransitionAsync(operation.OperationGuid, [LocalInstallmentOperationState.Prepared], LocalInstallmentOperationState.TerminalApproved, DateTimeOffset.UtcNow, requestJson: requestJson, cancellationToken: cancellationToken);
        if (!transitioned)
        {
            return await ReadReadyOrUnknownAsync(operation.OperationGuid, cancellationToken);
        }

        var updated = await repository.GetAsync(operation.OperationGuid, cancellationToken)
            ?? throw new InvalidOperationException("分期操作状态未保存。");
        return TerminalReady.Success(updated);
    }

    private async Task<TerminalReady> ReadReadyOrUnknownAsync(Guid operationGuid, CancellationToken cancellationToken)
    {
        var current = await repository.GetAsync(operationGuid, cancellationToken);
        return current?.State is LocalInstallmentOperationState.TerminalApproved or LocalInstallmentOperationState.ApiSubmitting
            ? TerminalReady.Success(current)
            : current?.State == LocalInstallmentOperationState.Completed
                ? TerminalReady.Failed("操作已完成，请刷新分期列表。")
                : TerminalReady.Unknown("同一分期操作正在恢复或结果未知，请勿重试。");
    }

    private async Task<InstallmentOperationResult<InstallmentCreateResponse>> SubmitCreateAsync(LocalInstallmentOperation operation, InstallmentCreateRequest request, CancellationToken cancellationToken, bool allowStaleApiSubmittingClaim = false)
    {
        if (!await ClaimApiAsync(operation.OperationGuid, allowStaleApiSubmittingClaim, cancellationToken))
        {
            return new InstallmentOperationResult<InstallmentCreateResponse>(false, Message: "分期操作正在恢复或已完成。", RequiresReview: true);
        }

        try
        {
            var response = await apiClient.CreateAsync(request, cancellationToken);
            var local = ToLocalOrder(response.Details);
            if (!await repository.CompleteWithSnapshotAsync(operation.OperationGuid, [LocalInstallmentOperationState.ApiSubmitting], local, JsonSerializer.Serialize(response, JsonOptions), false, DateTimeOffset.UtcNow, cancellationToken))
            {
                return new InstallmentOperationResult<InstallmentCreateResponse>(false, Message: "创建结果已提交，正在安全对账，请勿重复收款。", RequiresReview: true);
            }
            await TryAcknowledgeLinklyBackendSessionAsync(operation.OperationGuid);
            return new InstallmentOperationResult<InstallmentCreateResponse>(true, response, local, response.Message);
        }
        catch (OperationCanceledException exception)
        {
            await MarkApiUnknownAsync(operation.OperationGuid, "create", "创建 API 调用已取消，结果未知。", exception, CancellationToken.None);
            return new InstallmentOperationResult<InstallmentCreateResponse>(false, Message: "创建结果未知，请勿再次收款。", RequiresReview: true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await MarkApiUnknownAsync(operation.OperationGuid, "create", exception.Message, exception, CancellationToken.None);
            return new InstallmentOperationResult<InstallmentCreateResponse>(false, Message: "分期创建结果未知，请勿再次收款。", RequiresReview: true);
        }
    }

    private async Task<InstallmentOperationResult<InstallmentAppendPaymentResponse>> SubmitRepaymentAsync(LocalInstallmentOperation operation, InstallmentAppendPaymentRequest request, CancellationToken cancellationToken, bool allowStaleApiSubmittingClaim = false)
    {
        if (!await ClaimApiAsync(operation.OperationGuid, allowStaleApiSubmittingClaim, cancellationToken))
        {
            return new InstallmentOperationResult<InstallmentAppendPaymentResponse>(false, Message: "补款操作正在恢复或已完成。", RequiresReview: true);
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
                await MarkApiUnknownAsync(operation.OperationGuid, "repayment-commit", $"中央补款 claim 返回状态 {committedClaim.Status}，缺少提交结果。", null, CancellationToken.None);
                return new InstallmentOperationResult<InstallmentAppendPaymentResponse>(false, Message: "补款提交结果不完整，保持锁定等待恢复。", RequiresReview: true);
            }
            var local = ToLocalOrder(response.Details);
            if (!await repository.CompleteWithSnapshotAsync(operation.OperationGuid, [LocalInstallmentOperationState.ApiSubmitting], local, JsonSerializer.Serialize(response, JsonOptions), false, DateTimeOffset.UtcNow, cancellationToken))
            {
                return new InstallmentOperationResult<InstallmentAppendPaymentResponse>(false, Message: "补款结果已提交，正在安全对账，请勿重复收款。", RequiresReview: true);
            }
            await TryAcknowledgeLinklyBackendSessionAsync(operation.OperationGuid);
            return new InstallmentOperationResult<InstallmentAppendPaymentResponse>(true, response, local, response.Message);
        }
        catch (OperationCanceledException exception)
        {
            await MarkApiUnknownAsync(operation.OperationGuid, "repayment-commit", "补款 API 调用已取消，结果未知。", exception, CancellationToken.None);
            return new InstallmentOperationResult<InstallmentAppendPaymentResponse>(false, Message: "补款结果未知，请勿再次收款。", RequiresReview: true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await MarkApiUnknownAsync(operation.OperationGuid, "repayment-commit", exception.Message, exception, CancellationToken.None);
            return new InstallmentOperationResult<InstallmentAppendPaymentResponse>(false, Message: "补款结果未知，请勿再次收款。", RequiresReview: true);
        }
    }

    private async Task<InstallmentOperationResult<InstallmentCancelResponse>> ContinueCancelAsync(LocalInstallmentOperation operation, string installmentNumber, PosSessionState session, bool supervisorResolved, CancellationToken cancellationToken)
    {
        if (!string.Equals(operation.StoreCode, session.StoreCode, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(operation.DeviceCode, session.DeviceCode, StringComparison.OrdinalIgnoreCase))
        {
            return new InstallmentOperationResult<InstallmentCancelResponse>(false, Message: "取消退款仅允许原设备恢复。", RequiresReview: true);
        }

        var steps = await repository.GetRefundStepsAsync(operation.OperationGuid, cancellationToken);
        var claimReady = await EnsureCancelClaimAsync(operation, steps, cancellationToken);
        if (!claimReady.Succeeded)
        {
            if (claimReady.RequiresReview)
            {
                await LockCancelForReviewAsync(operation, claimReady.Message ?? "取消 claim 状态无法安全确认。");
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
            return new InstallmentOperationResult<InstallmentCancelResponse>(false, Message: "退款结果未知，已锁定等待主管三态结案。", RequiresReview: true);
        }

        var begun = await BeginCancelRefundAsync(operation, claimReady.Claim!, cancellationToken);
        if (!begun.Succeeded)
        {
            if (begun.RequiresReview)
            {
                await LockCancelForReviewAsync(operation, begun.Message ?? "取消退款阶段无法安全确认。");
            }
            return new InstallmentOperationResult<InstallmentCancelResponse>(false, Message: begun.Message, RequiresReview: begun.RequiresReview);
        }

        var expected = supervisorResolved || partialApprovedRetry
            ? new[] { LocalInstallmentOperationState.Prepared, LocalInstallmentOperationState.TerminalApproved, LocalInstallmentOperationState.ResultUnknown }
            : new[] { LocalInstallmentOperationState.Prepared, LocalInstallmentOperationState.TerminalApproved };
        var claimed = await repository.TryTransitionAsync(operation.OperationGuid, expected, LocalInstallmentOperationState.TerminalSubmitting, DateTimeOffset.UtcNow, cancellationToken: cancellationToken);
        if (!claimed)
        {
            return new InstallmentOperationResult<InstallmentCancelResponse>(false, Message: "取消操作正在恢复。", RequiresReview: true);
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
                    return new InstallmentOperationResult<InstallmentCancelResponse>(false, Message: "中央取消 claim 结案失败，保持锁定等待恢复。", RequiresReview: true);
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
            return new InstallmentOperationResult<InstallmentCancelResponse>(false, Message: "退款尚未全部完成，取消保持锁定。", RequiresReview: true);
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
            return new InstallmentOperationResult<InstallmentCancelResponse>(false, Message: "退款快照正在由其他恢复流程处理，取消保持锁定。", RequiresReview: true);
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
            return new InstallmentOperationResult<InstallmentCancelResponse>(false, Message: "取消 API 正在恢复或结果未知。", RequiresReview: true);
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
                    await MarkApiUnknownAsync(operation.OperationGuid, "cancel-commit-recovery", $"{firstException.Message}; commit 恢复失败：{recoveryException.Message}", recoveryException, CancellationToken.None);
                    return new InstallmentOperationResult<InstallmentCancelResponse>(false, Message: "取消提交结果未知，退款不会重复执行。", RequiresReview: true);
                }
            }

            if (committedClaim.Status != InstallmentCancelClaimStatus.Committed || committedClaim.Commit is null)
            {
                await MarkApiUnknownAsync(operation.OperationGuid, "cancel-commit", $"中央取消 claim 返回状态 {committedClaim.Status}，缺少提交结果。", null, CancellationToken.None);
                return new InstallmentOperationResult<InstallmentCancelResponse>(false, Message: "取消提交结果不完整，退款不会重复执行。", RequiresReview: true);
            }

            return await CompleteCommittedCancelAsync(
                operation,
                ToCancelResponse(operation.InstallmentGuid, committedClaim.Commit),
                cancellationToken);
        }
        catch (Exception exception)
        {
            await MarkApiUnknownAsync(operation.OperationGuid, "cancel-commit", exception.Message, exception, CancellationToken.None);
            return new InstallmentOperationResult<InstallmentCancelResponse>(false, Message: "取消提交结果未知，退款不会重复执行。", RequiresReview: true);
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
                    failureMessage: authorization.Message ?? "退款结果未知。",
                    cancellationToken: CancellationToken.None);
                return new InstallmentOperationResult<bool>(false, Message: "退款结果未知，等待主管结案。", RequiresReview: true);
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
                return new InstallmentOperationResult<bool>(false, Message: authorization.Message ?? "退款未获批准。");
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
                return new InstallmentOperationResult<bool>(false, Message: "退款批准信息无法安全核验，等待主管结案。", RequiresReview: true);
            }

            var approvedPersisted = await repository.TryTransitionRefundStepAsync(step.RefundStepGuid, [LocalInstallmentRefundStepState.TerminalSubmitting], LocalInstallmentRefundStepState.Approved, DateTimeOffset.UtcNow, authorization.Reference, authorization.CardTransactions is null ? null : JsonSerializer.Serialize(authorization.CardTransactions, JsonOptions), cancellationToken: CancellationToken.None);
            if (!approvedPersisted)
            {
                var current = (await repository.GetRefundStepsAsync(step.OperationGuid, CancellationToken.None)).FirstOrDefault(item => item.RefundStepGuid == step.RefundStepGuid);
                return current is not null && IsRefundApproved(current)
                    ? new InstallmentOperationResult<bool>(true, true)
                    : new InstallmentOperationResult<bool>(false, Message: "退款结果正在恢复或未知，保持锁定。", RequiresReview: true);
            }

            return new InstallmentOperationResult<bool>(true, true);
        }
        catch (OperationCanceledException exception)
        {
            LogRefundStepIssue(step, "ResultUnknown", exception, "canceled");
            await repository.TryTransitionRefundStepAsync(step.RefundStepGuid, [LocalInstallmentRefundStepState.TerminalSubmitting], LocalInstallmentRefundStepState.ResultUnknown, DateTimeOffset.UtcNow, failureMessage: "退款终端调用被取消，结果未知。", cancellationToken: CancellationToken.None);
            return new InstallmentOperationResult<bool>(false, Message: "退款结果未知，等待主管结案。", RequiresReview: true);
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
            return new InstallmentOperationResult<bool>(false, Message: "退款结果未知，等待主管结案。", RequiresReview: true);
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
                await repository.TryTransitionAsync(operation.OperationGuid, [LocalInstallmentOperationState.ResultUnknown], LocalInstallmentOperationState.Failed, DateTimeOffset.UtcNow, failureMessage: "终端明确拒绝或取消。", cancellationToken: CancellationToken.None);
                await TryAcknowledgeLinklyBackendSessionAsync(operation.OperationGuid);
                return new InstallmentOperationRecoveryResult(operation.OperationGuid, operation.Kind, LocalInstallmentOperationState.Failed, false, "终端已明确拒绝或取消，可重新操作。");
            }

            if (terminalRecovery.Approved)
            {
                request = request with { DownPayment = request.DownPayment with { Reference = terminalRecovery.Reference, CardTransactions = terminalRecovery.Transactions } };
                if (!await repository.TryTransitionAsync(operation.OperationGuid, [LocalInstallmentOperationState.ResultUnknown], LocalInstallmentOperationState.TerminalApproved, DateTimeOffset.UtcNow, requestJson: JsonSerializer.Serialize(request, JsonOptions), cancellationToken: CancellationToken.None))
                {
                    return new InstallmentOperationRecoveryResult(operation.OperationGuid, operation.Kind, LocalInstallmentOperationState.ResultUnknown, false, "终端批准正在由其他恢复入口对账。");
                }

                operation = (await repository.GetAsync(operation.OperationGuid, cancellationToken))!;
            }
            else
            return new InstallmentOperationRecoveryResult(operation.OperationGuid, operation.Kind, LocalInstallmentOperationState.ResultUnknown, false, "未确认终端批准，禁止自动重扣。");
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
                const string message = "远端补款 claim 缺失，但本地已有 provider attempt 或批准证据；保持锁定并等待人工对账。";
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
            return new InstallmentOperationRecoveryResult(operation.OperationGuid, operation.Kind, LocalInstallmentOperationState.ResultUnknown, false, $"读取中央补款 claim 失败，保持锁定：{exception.Message}");
        }

        if (claim.Status == InstallmentRepaymentClaimStatus.Committed)
        {
            if (claim.Commit is null)
            {
                return new InstallmentOperationRecoveryResult(operation.OperationGuid, operation.Kind, LocalInstallmentOperationState.ResultUnknown, false, "中央补款已提交但缺少结果快照，保持锁定。");
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
                failureMessage: $"中央补款 claim 已结束：{claim.Status}",
                cancellationToken: CancellationToken.None);
            return new InstallmentOperationRecoveryResult(operation.OperationGuid, operation.Kind, LocalInstallmentOperationState.Failed, false, "原补款 claim 已结束，可重新发起付款。");
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
                return new InstallmentOperationRecoveryResult(operation.OperationGuid, operation.Kind, LocalInstallmentOperationState.ResultUnknown, false, "中央补款缺少卡 provider attempt 绑定，保持锁定。");
            }

            // begin-provider 已成功但本地状态尚未来得及推进：先把中央耐久 attempt 绑定回本地，再且仅再做 provider 查询。
            var bound = await repository.TryTransitionAsync(
                operation.OperationGuid,
                [LocalInstallmentOperationState.Prepared],
                LocalInstallmentOperationState.ResultUnknown,
                DateTimeOffset.UtcNow,
                terminalAttemptGuid: claim.ProviderAttemptId,
                terminalProcessor: claim.Provider,
                failureMessage: "begin-provider 后本地状态中断，正在查询原卡 attempt。",
                cancellationToken: CancellationToken.None);
            operation = await repository.GetAsync(operation.OperationGuid, cancellationToken) ?? operation;
            if (!bound && operation.State is not LocalInstallmentOperationState.ResultUnknown and not LocalInstallmentOperationState.TerminalApproved and not LocalInstallmentOperationState.ApiSubmitting)
            {
                return new InstallmentOperationRecoveryResult(operation.OperationGuid, operation.Kind, operation.State, false, "原卡 attempt 正由另一恢复入口处理。");
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
                return new InstallmentOperationRecoveryResult(operation.OperationGuid, operation.Kind, LocalInstallmentOperationState.ResultUnknown, false, "礼券 reservation 结果未知，禁止重复兑换。");
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

        if (request.Method == PaymentMethodKind.Card && request.CardTransactions is not { Count: > 0 })
        {
            var terminalRecovery = await ReadPersistedTerminalApprovalAsync(operation, session, cancellationToken);
            if (terminalRecovery.Rejected)
            {
                await repository.TryTransitionAsync(operation.OperationGuid, [LocalInstallmentOperationState.ResultUnknown], LocalInstallmentOperationState.Failed, DateTimeOffset.UtcNow, failureMessage: "终端明确拒绝或取消。", cancellationToken: CancellationToken.None);
                await TryResolveRepaymentClaimAsync(operation, InstallmentRepaymentClaimResolveOutcome.Declined, CancellationToken.None);
                await TryAcknowledgeLinklyBackendSessionAsync(operation.OperationGuid);
                return new InstallmentOperationRecoveryResult(operation.OperationGuid, operation.Kind, LocalInstallmentOperationState.Failed, false, "终端已明确拒绝或取消，可重新操作。");
            }

            if (terminalRecovery.Approved)
            {
                request = request with { Reference = terminalRecovery.Reference, CardTransactions = terminalRecovery.Transactions };
                if (!await repository.TryTransitionAsync(operation.OperationGuid, [LocalInstallmentOperationState.ResultUnknown], LocalInstallmentOperationState.TerminalApproved, DateTimeOffset.UtcNow, requestJson: JsonSerializer.Serialize(request, JsonOptions), cancellationToken: CancellationToken.None))
                {
                    return new InstallmentOperationRecoveryResult(operation.OperationGuid, operation.Kind, LocalInstallmentOperationState.ResultUnknown, false, "终端批准正在由其他恢复入口对账。");
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
                return new InstallmentOperationRecoveryResult(operation.OperationGuid, operation.Kind, LocalInstallmentOperationState.ResultUnknown, false, "未确认终端批准，禁止自动重扣。");
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
            return new InstallmentOperationRecoveryResult(operation.OperationGuid, operation.Kind, LocalInstallmentOperationState.Failed, false, "Square 退款已明确拒绝，可重新发起取消。");
        }
        if (!steps.All(IsRefundApproved))
        {
            return new InstallmentOperationRecoveryResult(operation.OperationGuid, operation.Kind, operation.State, false, "退款未全部确认，保持锁定。" );
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
        (request.Method != PaymentMethodKind.Card || string.IsNullOrWhiteSpace(request.Reference));

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
                    _ => throw new InvalidOperationException("退款计划包含无效付款方式。")
                },
                AmountCents = ToExactCents(step.Amount)
            })
            .OrderBy(payment => payment.PaymentGuid, StringComparer.Ordinal)
            .ToArray();
        if (payments.Length == 0)
        {
            throw new InvalidOperationException("分期没有可退款付款。");
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
            throw new InvalidOperationException("退款金额无法转换为精确整数分。");
        }
        return decimal.ToInt64(cents);
    }

    private static InstallmentCancelResponse ToCancelResponse(Guid installmentGuid, InstallmentCancelClaimCommitResponse commit) =>
        new(installmentGuid, commit.Details.Status, commit.Details, commit.AlreadyCancelled, commit.AlreadyCancelled ? "分期已取消。" : "分期取消并退款完成。");
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
                Message: "提货确认正在恢复或结果未知。",
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
                    Message: "提货确认已提交，本地快照正在安全恢复。",
                    RequiresReview: true);
            }

            return new InstallmentOperationResult<InstallmentConfirmPickupResponse>(
                true,
                response,
                local,
                "分期单已确认提货。");
        }
        catch (OperationCanceledException exception)
        {
            await MarkApiUnknownAsync(operation.OperationGuid, "pickup", "提货确认 API 调用已取消，结果未知。", exception, CancellationToken.None);
            return new InstallmentOperationResult<InstallmentConfirmPickupResponse>(
                false,
                Message: "提货确认请求超时，结果可能已提交；已锁定，请刷新恢复，勿重复确认提货。",
                RequiresReview: true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await MarkApiUnknownAsync(operation.OperationGuid, "pickup", exception.Message, exception, CancellationToken.None);
            return new InstallmentOperationResult<InstallmentConfirmPickupResponse>(
                false,
                Message: "提货确认结果未知，已锁定；请刷新恢复，勿重复确认提货。",
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
                await MarkSquareRefundUnknownAsync(step, "Square 环境配置读取失败，保持锁定。", cancellationToken: CancellationToken.None);
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
                    await MarkSquareRefundUnknownAsync(step, "Square 原退款环境无效，保持锁定。", cancellationToken: CancellationToken.None);
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
                await MarkSquareRefundUnknownAsync(step, "Square 退款查询失败，保持锁定。", cancellationToken: CancellationToken.None);
                continue;
            }

            if (!string.Equals(refund.RefundId, refundId, StringComparison.Ordinal) ||
                !string.Equals(refund.PaymentId, paymentId, StringComparison.Ordinal) ||
                refund.AmountCents != ToExactCents(step.Amount) ||
                !string.Equals(refund.Currency, "AUD", StringComparison.OrdinalIgnoreCase))
            {
                // 响应身份不可信时不得覆盖已落盘的权威 refundId。
                await MarkSquareRefundUnknownAsync(step, "Square 退款身份、原付款、金额或币种不匹配，保持锁定。", cancellationToken: CancellationToken.None);
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
                    $"Square 退款状态为 {refund.Status}。",
                    CancellationToken.None);
                continue;
            }

            var message = string.Equals(refund.Status, "PENDING", StringComparison.OrdinalIgnoreCase)
                ? "Square 退款仍在处理中，保持锁定并稍后恢复。"
                : $"Square 退款状态为 {refund.Status}，仅 COMPLETED 可自动确认，保持锁定。";
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
            failureMessage: "Square 退款已明确拒绝，可重新发起取消。",
            cancellationToken: CancellationToken.None);
        return failed ||
            (await repository.GetAsync(operation.OperationGuid, CancellationToken.None))?.State == LocalInstallmentOperationState.Failed;
    }

    private static bool HasSquareRejectedRefundReset(IReadOnlyList<LocalInstallmentRefundStep> steps) =>
        steps.Any(step =>
            step.State == LocalInstallmentRefundStepState.Prepared &&
            TryParseSquarePaymentId(step.OriginalReference, out _) &&
            (string.Equals(step.FailureMessage, "Square 退款状态为 FAILED。", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(step.FailureMessage, "Square 退款状态为 REJECTED。", StringComparison.OrdinalIgnoreCase)));

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
    private static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, JsonOptions) ?? throw new InvalidOperationException("分期操作快照无效。");
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
        bool RequiresReview)
    {
        public static RepaymentClaimReady Success(InstallmentRepaymentClaimDto claim) => new(true, claim, null, null, false);
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
        string? reviewMessage = null) : IDisposable
    {
        public static TerminalAttemptScope Empty { get; } = new(null, null, null, null, null);
        public Guid? AttemptGuid { get; } = attemptGuid;
        public string? Processor { get; } = processor;
        public bool RequiresReview { get; } = requiresReview;
        public string? ReviewMessage { get; } = reviewMessage;

        public static TerminalAttemptScope ForReview(Guid attemptGuid, string? processor, string message) =>
            new(attemptGuid, processor, null, null, null, true, message);

        public IDisposable? BeginContext() => beginContext?.Invoke();

        public bool HasVerifiedApprovalEvidence(PaymentAuthorizationResult authorization) =>
            hasVerifiedApprovalEvidence?.Invoke(authorization) ?? true;

        public Task RecordOutcomeAsync(PaymentAuthorizationResult authorization, CancellationToken cancellationToken) =>
            recordOutcomeAsync?.Invoke(authorization, cancellationToken) ?? Task.CompletedTask;

        public void Dispose()
        {
        }
    }
}
