using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Contracts.Common;
using Hbpos.Contracts.Installments;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Wpf.Services;

public interface IInstallmentOrderService
{
    Task<IReadOnlyList<InstallmentOrderSummary>> GetOrdersAsync(PosSessionState session, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<InstallmentOrderSummary>> SearchAsync(PosSessionState session, string? keyword, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<InstallmentOrderSummary>> QueryHistoryAsync(
        PosSessionState session,
        InstallmentHistorySearchQuery query,
        CancellationToken cancellationToken = default) =>
        SearchAsync(session, query.Keyword, cancellationToken);

    Task<LocalInstallmentOrder?> GetOrderDetailsAsync(
        PosSessionState session,
        Guid installmentGuid,
        CancellationToken cancellationToken = default) =>
        session.IsOnline
            ? Task.FromException<LocalInstallmentOrder?>(new NotSupportedException("在线分期详情服务尚未接入。"))
            : GetLocalOrderAsync(installmentGuid, cancellationToken);

    Task<LocalInstallmentOrder?> GetLocalOrderAsync(Guid installmentGuid, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<InstallmentOperationRecoveryResult>> RecoverPendingOperationsAsync(PosSessionState session, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<InstallmentOperationRecoveryResult>>([]);

    Task<IReadOnlySet<Guid>> GetLockedInstallmentGuidsAsync(PosSessionState session, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());

    Task<IReadOnlyList<LocalInstallmentRefundStep>> GetRefundStepsForReviewAsync(Guid installmentGuid, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<LocalInstallmentRefundStep>>([]);

    Task<bool> ResolveRefundStepAsync(Guid refundStepGuid, InstallmentRefundSupervisorResolution resolution, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    Task<InstallmentOrderActionResult> ResumeCancelAfterSupervisorAsync(Guid operationGuid, string installmentNumber, PosSessionState session, CancellationToken cancellationToken = default) =>
        Task.FromResult(new InstallmentOrderActionResult(false, "未配置主管结案恢复服务。", RequiresReview: true));

    Task<InstallmentWriteResult<InstallmentCreateResponse>> CreateAsync(PosSessionState session, InstallmentCreateRequest request, CancellationToken cancellationToken = default);

    Task<InstallmentWriteResult<InstallmentAppendPaymentResponse>> AppendPaymentAsync(PosSessionState session, InstallmentAppendPaymentRequest request, CancellationToken cancellationToken = default);

    Task<InstallmentWriteResult<InstallmentConfirmPickupResponse>> ConfirmPickupAsync(PosSessionState session, InstallmentConfirmPickupRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// 修改分期单商品列表（仅在线）。<paramref name="baseline"/> 是编辑器加载时的订单详情：
    /// 其 UpdatedAt 作为乐观并发令牌，PaidAmount / Status 用于客户端预校验。默认实现表示“不支持”。
    /// </summary>
    Task<InstallmentAmendLinesResult> AmendLinesAsync(
        PosSessionState session,
        LocalInstallmentOrder baseline,
        IReadOnlyList<InstallmentLineDto> lines,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(InstallmentAmendLinesResult.Create(
            session.IsOnline ? InstallmentAmendLinesOutcome.Rejected : InstallmentAmendLinesOutcome.OnlineRequired,
            session.IsOnline ? "分期服务尚未接入修改商品。" : "OnlineRequired"));

    /// <summary>服务端是否声明支持修改商品（capabilities.AmendLinesSupported）；无法确认一律按不支持处理。</summary>
    Task<bool> IsAmendLinesSupportedAsync(PosSessionState session, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    Task<InstallmentWriteResult<InstallmentCancelResponse>> CancelWithRefundAsync(PosSessionState session, InstallmentCancelRequest request, CancellationToken cancellationToken = default);

    Task<InstallmentWriteResult<InstallmentVoidResponse>> VoidCancelAsync(PosSessionState session, InstallmentVoidRequest request, CancellationToken cancellationToken = default);

    Task<InstallmentOrderCreateResult> CreateOrderAsync(InstallmentOrderCreateRequest request, CancellationToken cancellationToken = default);

    Task<InstallmentOrderActionResult> AddRepaymentAsync(InstallmentOrderRepaymentRequest request, CancellationToken cancellationToken = default);

    Task<InstallmentOrderActionResult> CancelWithRefundAsync(Guid orderId, PosSessionState session, InstallmentCancelRefundMode refundMode = InstallmentCancelRefundMode.OriginalRoute, CancellationToken cancellationToken = default);

    Task<InstallmentOrderActionResult> VoidCancelAsync(Guid orderId, PosSessionState session, string? reason = null, CancellationToken cancellationToken = default);

    Task<InstallmentOrderActionResult> ConfirmPickupAsync(Guid orderId, PosSessionState session, CancellationToken cancellationToken = default);
}

public interface IInstallmentApiClient
{
    Task<InstallmentHistoryQueryResponse> QueryHistoryAsync(
        InstallmentHistoryQueryRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromException<InstallmentHistoryQueryResponse>(new NotSupportedException("当前分期 API 客户端未实现历史查询。"));

    Task<InstallmentDetailsDto> GetDetailsAsync(
        Guid installmentGuid,
        CancellationToken cancellationToken = default) =>
        Task.FromException<InstallmentDetailsDto>(new NotSupportedException("当前分期 API 客户端未实现详情查询。"));

    Task<InstallmentRepaymentCapabilitiesResponse> GetRepaymentCapabilitiesAsync(CancellationToken cancellationToken = default) =>
        Task.FromException<InstallmentRepaymentCapabilitiesResponse>(new NotSupportedException("当前分期 API 客户端未实现补款 claim 协议。"));

    Task<InstallmentRepaymentClaimDto> CreateRepaymentClaimAsync(Guid installmentGuid, InstallmentRepaymentClaimCreateRequest request, CancellationToken cancellationToken = default) =>
        Task.FromException<InstallmentRepaymentClaimDto>(new NotSupportedException("当前分期 API 客户端未实现补款 claim 协议。"));

    Task<InstallmentRepaymentClaimDto> BeginRepaymentProviderAsync(Guid installmentGuid, Guid operationGuid, InstallmentRepaymentClaimBeginProviderRequest request, CancellationToken cancellationToken = default) =>
        Task.FromException<InstallmentRepaymentClaimDto>(new NotSupportedException("当前分期 API 客户端未实现补款 claim 协议。"));

    Task<InstallmentRepaymentClaimDto> GetRepaymentClaimAsync(Guid installmentGuid, Guid operationGuid, CancellationToken cancellationToken = default) =>
        Task.FromException<InstallmentRepaymentClaimDto>(new NotSupportedException("当前分期 API 客户端未实现补款 claim 协议。"));

    Task<InstallmentRepaymentClaimDto> ResolveRepaymentClaimAsync(Guid installmentGuid, Guid operationGuid, InstallmentRepaymentClaimResolveRequest request, CancellationToken cancellationToken = default) =>
        Task.FromException<InstallmentRepaymentClaimDto>(new NotSupportedException("当前分期 API 客户端未实现补款 claim 协议。"));

    Task<InstallmentRepaymentClaimDto> CommitRepaymentClaimAsync(Guid installmentGuid, Guid operationGuid, InstallmentRepaymentClaimCommitRequest request, CancellationToken cancellationToken = default) =>
        Task.FromException<InstallmentRepaymentClaimDto>(new NotSupportedException("当前分期 API 客户端未实现补款 claim 协议。"));

    Task<InstallmentCancelClaimDto> CreateCancelClaimAsync(Guid installmentGuid, InstallmentCancelClaimCreateRequest request, CancellationToken cancellationToken = default) =>
        Task.FromException<InstallmentCancelClaimDto>(new NotSupportedException("当前分期 API 客户端未实现取消 claim 协议。"));

    Task<InstallmentCancelClaimDto> BeginCancelRefundAsync(Guid installmentGuid, Guid operationGuid, CancellationToken cancellationToken = default) =>
        Task.FromException<InstallmentCancelClaimDto>(new NotSupportedException("当前分期 API 客户端未实现取消 claim 协议。"));

    Task<InstallmentCancelClaimDto> GetCancelClaimAsync(Guid installmentGuid, Guid operationGuid, CancellationToken cancellationToken = default) =>
        Task.FromException<InstallmentCancelClaimDto>(new NotSupportedException("当前分期 API 客户端未实现取消 claim 协议。"));

    Task<InstallmentCancelClaimDto> ResolveCancelClaimAsync(Guid installmentGuid, Guid operationGuid, InstallmentCancelClaimResolveRequest request, CancellationToken cancellationToken = default) =>
        Task.FromException<InstallmentCancelClaimDto>(new NotSupportedException("当前分期 API 客户端未实现取消 claim 协议。"));

    Task<InstallmentCancelClaimDto> CommitCancelClaimAsync(Guid installmentGuid, Guid operationGuid, InstallmentCancelClaimCommitRequest request, CancellationToken cancellationToken = default) =>
        Task.FromException<InstallmentCancelClaimDto>(new NotSupportedException("当前分期 API 客户端未实现取消 claim 协议。"));

    Task<InstallmentAmendLinesResponse> AmendLinesAsync(InstallmentAmendLinesRequest request, CancellationToken cancellationToken = default) =>
        Task.FromException<InstallmentAmendLinesResponse>(new NotSupportedException("当前分期 API 客户端未实现修改商品。"));

    Task<InstallmentCreateResponse> CreateAsync(InstallmentCreateRequest request, CancellationToken cancellationToken = default);

    Task<InstallmentAppendPaymentResponse> AppendPaymentAsync(InstallmentAppendPaymentRequest request, CancellationToken cancellationToken = default);

    Task<InstallmentConfirmPickupResponse> ConfirmPickupAsync(InstallmentConfirmPickupRequest request, CancellationToken cancellationToken = default);

    Task<InstallmentCancelResponse> CancelAsync(InstallmentCancelRequest request, CancellationToken cancellationToken = default);

    Task<InstallmentVoidResponse> VoidAsync(InstallmentVoidRequest request, CancellationToken cancellationToken = default);
}

public sealed class InstallmentOrderService(
    ILocalInstallmentOrderRepository localRepository,
    IInstallmentApiClient apiClient,
    PosCartService? cart = null,
    ICardTerminalClient? cardTerminalClient = null,
    IVoucherTenderClient? voucherTenderClient = null,
    IInstallmentOperationService? installmentOperations = null) : IInstallmentOrderService
{
    private readonly ICardTerminalClient _cardTerminalClient = cardTerminalClient ?? UnavailableCardTerminalClient.Instance;
    // 保留注入边界供旧组合根兼容；取消退款已强制由 durable operation service 使用该依赖。
    private readonly IVoucherTenderClient _voucherTenderClient = voucherTenderClient ?? UnavailableVoucherTenderClient.Instance;

    public async Task<IReadOnlyList<InstallmentOrderSummary>> GetOrdersAsync(PosSessionState session, CancellationToken cancellationToken = default)
    {
        var orders = await localRepository.GetRecentByStoreAsync(session.StoreCode, cancellationToken: cancellationToken);
        return orders.Select(MapSummary).ToList();
    }

    public async Task<IReadOnlyList<InstallmentOrderSummary>> SearchAsync(PosSessionState session, string? keyword, CancellationToken cancellationToken = default)
    {
        var orders = await localRepository.GetRecentByStoreAsync(session.StoreCode, 200, cancellationToken);
        var normalized = string.IsNullOrWhiteSpace(keyword) ? string.Empty : keyword.Trim();
        return orders
            .Where(order => string.IsNullOrWhiteSpace(normalized) ||
                order.InstallmentNumber.Contains(normalized, StringComparison.OrdinalIgnoreCase) ||
                order.CustomerName.Contains(normalized, StringComparison.OrdinalIgnoreCase) ||
                order.CustomerPhone.Contains(normalized, StringComparison.OrdinalIgnoreCase) ||
                GetStatusText(order).Contains(normalized, StringComparison.OrdinalIgnoreCase) ||
                order.DeviceCode.Contains(normalized, StringComparison.OrdinalIgnoreCase) ||
                // 分期商品标识与服务端搜索契约一致：三种标识只接受完整匹配，不接受子串误命中。
                order.Lines.Any(line =>
                    string.Equals(line.ProductCode, normalized, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(line.ItemNumber, normalized, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(line.LookupCode, normalized, StringComparison.OrdinalIgnoreCase)))
            .Select(MapSummary)
            .ToList();
    }

    public async Task<IReadOnlyList<InstallmentOrderSummary>> QueryHistoryAsync(
        PosSessionState session,
        InstallmentHistorySearchQuery query,
        CancellationToken cancellationToken = default)
    {
        if (!session.IsOnline)
        {
            return await SearchAsync(session, query.Keyword, cancellationToken);
        }

        var response = await apiClient.QueryHistoryAsync(
            new InstallmentHistoryQueryRequest(
                session.StoreCode,
                DeviceCode: query.DeviceCode,
                Keyword: query.Keyword,
                Take: Math.Clamp(query.Take, 1, 200),
                UpdatedFrom: query.UpdatedFrom,
                UpdatedTo: query.UpdatedTo,
                OrderByUpdatedAt: true),
            cancellationToken);
        return response.Orders.Select(MapSummary).ToList();
    }

    public async Task<LocalInstallmentOrder?> GetOrderDetailsAsync(
        PosSessionState session,
        Guid installmentGuid,
        CancellationToken cancellationToken = default)
    {
        if (!session.IsOnline)
        {
            return await localRepository.GetAsync(installmentGuid, cancellationToken);
        }

        // 在线历史的金融明细必须以服务端为准；请求失败直接交给上层 fail closed，禁止读取陈旧快照。
        var details = await apiClient.GetDetailsAsync(installmentGuid, cancellationToken);
        if (details.InstallmentGuid != installmentGuid)
        {
            throw new InvalidOperationException("分期详情与查询订单不一致，已停止显示。 / Installment detail does not match the requested order.");
        }

        return await SaveSnapshotAsync(details, cancellationToken);
    }

    public Task<LocalInstallmentOrder?> GetLocalOrderAsync(Guid installmentGuid, CancellationToken cancellationToken = default)
    {
        return localRepository.GetAsync(installmentGuid, cancellationToken);
    }

    public Task<IReadOnlyList<InstallmentOperationRecoveryResult>> RecoverPendingOperationsAsync(PosSessionState session, CancellationToken cancellationToken = default) =>
        installmentOperations?.RecoverAsync(session, cancellationToken) ?? Task.FromResult<IReadOnlyList<InstallmentOperationRecoveryResult>>([]);

    public Task<IReadOnlySet<Guid>> GetLockedInstallmentGuidsAsync(PosSessionState session, CancellationToken cancellationToken = default) =>
        installmentOperations?.GetLockedInstallmentGuidsAsync(session, cancellationToken) ?? Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());

    public Task<IReadOnlyList<LocalInstallmentRefundStep>> GetRefundStepsForReviewAsync(Guid installmentGuid, CancellationToken cancellationToken = default) =>
        installmentOperations?.GetRefundStepsForReviewAsync(installmentGuid, cancellationToken) ?? Task.FromResult<IReadOnlyList<LocalInstallmentRefundStep>>([]);

    public Task<bool> ResolveRefundStepAsync(Guid refundStepGuid, InstallmentRefundSupervisorResolution resolution, CancellationToken cancellationToken = default) =>
        installmentOperations?.ResolveRefundStepAsync(refundStepGuid, resolution, cancellationToken) ?? Task.FromResult(false);

    public async Task<InstallmentOrderActionResult> ResumeCancelAfterSupervisorAsync(Guid operationGuid, string installmentNumber, PosSessionState session, CancellationToken cancellationToken = default)
    {
        if (installmentOperations is null)
        {
            return new InstallmentOrderActionResult(false, "未配置主管结案恢复服务。", RequiresReview: true);
        }

        var result = await installmentOperations.ResumeCancelAfterSupervisorAsync(operationGuid, installmentNumber, session, cancellationToken);
        return new InstallmentOrderActionResult(result.Succeeded, result.Message ?? "取消恢复未完成。", result.LocalOrder is null ? null : MapSummary(result.LocalOrder), result.RequiresReview);
    }

    public async Task<InstallmentWriteResult<InstallmentCreateResponse>> CreateAsync(PosSessionState session, InstallmentCreateRequest request, CancellationToken cancellationToken = default)
    {
        if (!session.IsOnline)
        {
            return InstallmentWriteResult<InstallmentCreateResponse>.OnlineRequired("OnlineRequired");
        }

        if (installmentOperations is not null)
        {
            var operation = await installmentOperations.ExecuteCreateAsync(session, request, authorizeCard: false, cancellationToken);
            if (!operation.Succeeded || operation.Response is null || operation.LocalOrder is null)
            {
                return InstallmentWriteResult<InstallmentCreateResponse>.OnlineRequired(operation.Message ?? "分期创建结果未知，请勿重复收款。");
            }

            cart?.Clear();
            return InstallmentWriteResult<InstallmentCreateResponse>.Success(operation.Response, operation.LocalOrder, operation.Message);
        }

        var response = await apiClient.CreateAsync(request, cancellationToken);
        var localOrder = await SaveSnapshotAsync(response.Details, cancellationToken);
        cart?.Clear();
        return InstallmentWriteResult<InstallmentCreateResponse>.Success(response, localOrder, response.Message);
    }

    public async Task<InstallmentWriteResult<InstallmentAppendPaymentResponse>> AppendPaymentAsync(PosSessionState session, InstallmentAppendPaymentRequest request, CancellationToken cancellationToken = default)
    {
        if (!session.IsOnline)
        {
            return InstallmentWriteResult<InstallmentAppendPaymentResponse>.OnlineRequired("OnlineRequired");
        }

        if (installmentOperations is not null)
        {
            var operation = await installmentOperations.ExecuteRepaymentAsync(session, request, authorizeCard: false, cancellationToken);
            if (!operation.Succeeded || operation.Response is null || operation.LocalOrder is null)
            {
                return InstallmentWriteResult<InstallmentAppendPaymentResponse>.OnlineRequired(operation.Message ?? "补款结果未知，请勿重复收款。");
            }

            return InstallmentWriteResult<InstallmentAppendPaymentResponse>.Success(operation.Response, operation.LocalOrder, operation.Message);
        }

        // 中文注释：新 WPF 补款必须经过 durable operation + 中央 claim；未注入协调器时在任何 provider/API 副作用前失败。
        return InstallmentWriteResult<InstallmentAppendPaymentResponse>.OnlineRequired("安全补款服务未配置，已停止付款；禁止降级调用旧 /payments。");
    }

    public async Task<InstallmentWriteResult<InstallmentConfirmPickupResponse>> ConfirmPickupAsync(PosSessionState session, InstallmentConfirmPickupRequest request, CancellationToken cancellationToken = default)
    {
        if (!session.IsOnline)
        {
            return InstallmentWriteResult<InstallmentConfirmPickupResponse>.OnlineRequired("OnlineRequired");
        }

        if (installmentOperations is not null)
        {
            var operation = await installmentOperations.ExecutePickupAsync(session, request, cancellationToken);
            return operation.Succeeded && operation.Response is not null && operation.LocalOrder is not null
                ? InstallmentWriteResult<InstallmentConfirmPickupResponse>.Success(operation.Response, operation.LocalOrder, operation.Message)
                : InstallmentWriteResult<InstallmentConfirmPickupResponse>.OnlineRequired(operation.Message ?? "提货确认结果未知，请刷新核对。");
        }

        var response = await apiClient.ConfirmPickupAsync(request, cancellationToken);
        var localOrder = await SaveSnapshotAsync(response.Details, cancellationToken);
        return InstallmentWriteResult<InstallmentConfirmPickupResponse>.Success(response, localOrder);
    }

    public async Task<bool> IsAmendLinesSupportedAsync(PosSessionState session, CancellationToken cancellationToken = default)
    {
        if (!session.IsOnline)
        {
            return false;
        }

        try
        {
            var capabilities = await apiClient.GetRepaymentCapabilitiesAsync(cancellationToken);
            return capabilities.AmendLinesSupported;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // 旧服务端没有该能力位或网络不通：一律按“不支持”处理，不放行写操作。
            ConsoleLog.WriteWarning(
                "Installment",
                $"installment amend capability check failed error={ex.GetType().Name} message={ex.Message}");
            return false;
        }
    }

    public async Task<InstallmentAmendLinesResult> AmendLinesAsync(
        PosSessionState session,
        LocalInstallmentOrder baseline,
        IReadOnlyList<InstallmentLineDto> lines,
        CancellationToken cancellationToken = default)
    {
        // 仅在线：没有 durable 队列，离线时不允许改单。
        if (!session.IsOnline)
        {
            return InstallmentAmendLinesResult.Create(InstallmentAmendLinesOutcome.OnlineRequired, "OnlineRequired");
        }

        // 客户端预校验与服务端共用同一份规则，能在发请求前挡住的错误不打扰服务端。
        if (!InstallmentAmendRules.CanAmend(baseline.Status))
        {
            return InstallmentAmendLinesResult.Create(
                InstallmentAmendLinesOutcome.Rejected,
                "当前订单状态不允许修改商品。",
                InstallmentAmendLinesErrorCodes.StatusNotAllowed);
        }

        if (InstallmentAmendRules.ValidateLines(lines) != InstallmentAmendLinesValidation.Valid)
        {
            return InstallmentAmendLinesResult.Create(
                InstallmentAmendLinesOutcome.Rejected,
                "商品明细不完整或金额不合法。",
                InstallmentAmendLinesErrorCodes.InvalidLines);
        }

        var newTotal = InstallmentAmendRules.CalculateTotal(lines);
        var totalValidation = InstallmentAmendRules.ValidateTotal(newTotal, baseline.PaidAmount);
        if (totalValidation != InstallmentAmendLinesValidation.Valid)
        {
            return InstallmentAmendLinesResult.Create(
                InstallmentAmendLinesOutcome.Rejected,
                totalValidation == InstallmentAmendLinesValidation.TotalBelowPaid
                    ? "新总额不得低于已付金额。"
                    : "新总额低于分期订单最低总额。",
                totalValidation == InstallmentAmendLinesValidation.TotalBelowPaid
                    ? InstallmentAmendLinesErrorCodes.TotalBelowPaid
                    : InstallmentAmendLinesErrorCodes.TotalBelowMinimum);
        }

        var installmentGuid = baseline.InstallmentGuid;
        var request = new InstallmentAmendLinesRequest(
            installmentGuid,
            session.StoreCode,
            session.DeviceCode,
            session.CashierId,
            session.CashierName,
            lines,
            // 乐观并发令牌：必须是编辑器加载时看到的订单版本，服务端不一致即返回 409。
            baseline.UpdatedAt);

        try
        {
            var response = await apiClient.AmendLinesAsync(request, cancellationToken);
            if (response.Details.InstallmentGuid != installmentGuid)
            {
                // 响应与请求订单不一致时不能写快照，按结果未知处理并交给对账。
                return await ReconcileAmendLinesAsync(baseline, lines, "修改商品响应与订单不一致。", cancellationToken);
            }

            var localOrder = await SaveSnapshotAsync(response.Details, cancellationToken);
            return InstallmentAmendLinesResult.Success(localOrder, MapSummary(localOrder));
        }
        catch (CatalogApiException ex) when (string.Equals(ex.ErrorCode, InstallmentAmendLinesErrorCodes.Stale, StringComparison.Ordinal))
        {
            return await RefreshAfterStaleAsync(installmentGuid, cancellationToken);
        }
        catch (CatalogApiException ex) when (IsAmendBusinessErrorCode(ex.ErrorCode))
        {
            if (string.Equals(ex.ErrorCode, InstallmentAmendLinesErrorCodes.StatusNotAllowed, StringComparison.Ordinal))
            {
                // 状态已变（如被取消 / 已提货）：刷新快照，让界面反映真实状态。
                var refreshed = await TryRefreshSnapshotAsync(installmentGuid, cancellationToken);
                return InstallmentAmendLinesResult.Create(
                    InstallmentAmendLinesOutcome.Rejected,
                    ex.Message,
                    ex.ErrorCode,
                    refreshed,
                    refreshed is null ? null : MapSummary(refreshed));
            }

            return InstallmentAmendLinesResult.Create(InstallmentAmendLinesOutcome.Rejected, ex.Message, ex.ErrorCode);
        }
        catch (CatalogApiException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            // 409 但不是 Stale（如订单正忙于其他操作）：服务端明确未执行，保留编辑稍后重试。
            return InstallmentAmendLinesResult.Create(InstallmentAmendLinesOutcome.Rejected, ex.Message, ex.ErrorCode ?? "CONFLICT");
        }
        catch (CatalogApiException ex) when (ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest or HttpStatusCode.NotFound)
        {
            return InstallmentAmendLinesResult.Create(
                InstallmentAmendLinesOutcome.Rejected,
                ex.Message,
                ex.StatusCode == HttpStatusCode.Forbidden ? "FORBIDDEN" : ex.ErrorCode);
        }
        catch (Exception ex) when (
            ex is not OutOfMemoryException &&
            (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested))
        {
            // 超时 / 断网 / 5xx / 响应无法解析：服务端可能已经落库，必须先对账再下结论。
            ConsoleLog.WriteWarning(
                "Installment",
                $"installment amend lines outcome unknown installmentGuid={installmentGuid:D} error={ex.GetType().Name} message={ex.Message}");
            return await ReconcileAmendLinesAsync(baseline, lines, ex.Message, cancellationToken);
        }
    }

    private static bool IsAmendBusinessErrorCode(string? errorCode) =>
        errorCode is InstallmentAmendLinesErrorCodes.InvalidLines or
            InstallmentAmendLinesErrorCodes.TotalBelowPaid or
            InstallmentAmendLinesErrorCodes.TotalBelowMinimum or
            InstallmentAmendLinesErrorCodes.StatusNotAllowed;

    // 409 Stale：别的设备已改过订单。拉最新详情写进快照并交给界面提示“已刷新”。
    private async Task<InstallmentAmendLinesResult> RefreshAfterStaleAsync(
        Guid installmentGuid,
        CancellationToken cancellationToken)
    {
        var refreshed = await TryRefreshSnapshotAsync(installmentGuid, cancellationToken);
        return InstallmentAmendLinesResult.Create(
            InstallmentAmendLinesOutcome.Stale,
            "订单已被其他设备修改，已刷新，请重新修改。",
            InstallmentAmendLinesErrorCodes.Stale,
            refreshed,
            refreshed is null ? null : MapSummary(refreshed));
    }

    private async Task<LocalInstallmentOrder?> TryRefreshSnapshotAsync(Guid installmentGuid, CancellationToken cancellationToken)
    {
        try
        {
            var details = await apiClient.GetDetailsAsync(installmentGuid, cancellationToken);
            return details.InstallmentGuid == installmentGuid
                ? await SaveSnapshotAsync(details, cancellationToken)
                : null;
        }
        catch (Exception ex) when (
            ex is not OutOfMemoryException &&
            (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested))
        {
            return null;
        }
    }

    /// <summary>
    /// 结果未知时的对账：重新读取服务端详情。商品内容与目标一致 = 已生效；订单版本变了且内容不同 = 被他人改过；
    /// 版本未变 = 本次没有生效，可以放心重试（重试带同一个 ExpectedUpdatedAt，不会重复生效）。
    /// </summary>
    private async Task<InstallmentAmendLinesResult> ReconcileAmendLinesAsync(
        LocalInstallmentOrder baseline,
        IReadOnlyList<InstallmentLineDto> targetLines,
        string reason,
        CancellationToken cancellationToken)
    {
        var refreshed = await TryRefreshSnapshotAsync(baseline.InstallmentGuid, cancellationToken);
        if (refreshed is null)
        {
            return InstallmentAmendLinesResult.Create(
                InstallmentAmendLinesOutcome.Unknown,
                $"保存结果未能确认（{reason}）。请核对订单后再保存；重复保存不会重复生效。");
        }

        if (AmendLinesContentEqual(refreshed.Lines, targetLines))
        {
            return InstallmentAmendLinesResult.Success(refreshed, MapSummary(refreshed));
        }

        if (refreshed.UpdatedAt != baseline.UpdatedAt)
        {
            return InstallmentAmendLinesResult.Create(
                InstallmentAmendLinesOutcome.Stale,
                "订单已被其他设备修改，已刷新，请重新修改。",
                InstallmentAmendLinesErrorCodes.Stale,
                refreshed,
                MapSummary(refreshed));
        }

        return InstallmentAmendLinesResult.Create(
            InstallmentAmendLinesOutcome.Failed,
            $"保存未成功（{reason}），订单未被修改，请重试。",
            order: refreshed,
            summary: MapSummary(refreshed));
    }

    // 内容比较忽略行 Guid 与顺序，只比较商品、数量、单价、折扣、实收（服务端可能重排或重发行 Guid）。
    private static bool AmendLinesContentEqual(IReadOnlyList<InstallmentLineDto> left, IReadOnlyList<InstallmentLineDto> right)
    {
        static string Key(InstallmentLineDto line) =>
            string.Join(
                '|',
                line.ProductCode,
                line.ReferenceCode ?? string.Empty,
                line.LookupCode,
                line.Quantity.ToString("0.###", CultureInfo.InvariantCulture),
                line.UnitPrice.ToString("0.00", CultureInfo.InvariantCulture),
                line.DiscountAmount.ToString("0.00", CultureInfo.InvariantCulture),
                line.ActualAmount.ToString("0.00", CultureInfo.InvariantCulture));

        return left.Count == right.Count &&
            left.Select(Key).Order(StringComparer.Ordinal).SequenceEqual(right.Select(Key).Order(StringComparer.Ordinal));
    }

    public Task<InstallmentWriteResult<InstallmentCancelResponse>> CancelWithRefundAsync(PosSessionState session, InstallmentCancelRequest request, CancellationToken cancellationToken = default)
    {
        if (!session.IsOnline)
        {
            return Task.FromResult(InstallmentWriteResult<InstallmentCancelResponse>.OnlineRequired("OnlineRequired"));
        }

        // 取消必须从本机分期快照建立 durable operation 与中央 claim，禁止直接提交已组装的退款结果。
        return Task.FromResult(InstallmentWriteResult<InstallmentCancelResponse>.OnlineRequired("安全取消服务要求从分期详情发起，已停止旧 /cancel 调用。"));
    }

    public async Task<InstallmentWriteResult<InstallmentVoidResponse>> VoidCancelAsync(PosSessionState session, InstallmentVoidRequest request, CancellationToken cancellationToken = default)
    {
        if (!session.IsOnline)
        {
            return InstallmentWriteResult<InstallmentVoidResponse>.OnlineRequired("OnlineRequired");
        }

        var response = await apiClient.VoidAsync(request, cancellationToken);
        var localOrder = await SaveSnapshotAsync(response.Details, cancellationToken);
        return InstallmentWriteResult<InstallmentVoidResponse>.Success(response, localOrder, response.Message);
    }

    public async Task<InstallmentOrderCreateResult> CreateOrderAsync(InstallmentOrderCreateRequest request, CancellationToken cancellationToken = default)
    {
        if (!request.Session.IsOnline)
        {
            return new InstallmentOrderCreateResult(false, "OnlineRequired");
        }

        // 支付草稿 GUID 在同一页面生命周期内稳定，可作为创建与恢复的幂等锚点。
        var installmentGuid = request.DownPayment.PaymentGuid;
        // 分期 GUID、付款 GUID 与幂等键由页面会话稳定持有，失败重试与重启恢复都只能复用原身份。
        var stableInstallmentGuid = request.InstallmentGuid == Guid.Empty
            ? request.DownPayment.PaymentGuid
            : request.InstallmentGuid;
        var payment = request.DownPayment with
        {
            Amount = Math.Min(request.DownPayment.Amount, request.CartSnapshot.ActualAmount),
            IdempotencyKey = string.IsNullOrWhiteSpace(request.DownPayment.IdempotencyKey)
                ? $"{stableInstallmentGuid:D}:create"
                : request.DownPayment.IdempotencyKey.Trim()
        };
        var apiRequest = new InstallmentCreateRequest(
            stableInstallmentGuid,
            request.Session.StoreCode,
            request.Session.DeviceCode,
            request.Session.CashierId,
            request.Session.CashierName,
            DateTimeOffset.Now,
            request.CartSnapshot.ActualAmount,
            payment.Amount,
            request.CartSnapshot.Lines.Select(line => new InstallmentLineDto(
                Guid.NewGuid(),
                line.ProductCode,
                line.ReferenceCode,
                line.DisplayName,
                line.LookupCode,
                line.Quantity,
                line.UnitPrice,
                line.DiscountAmount,
                line.ActualAmount,
                line.ItemNumber)).ToList(),
            new InstallmentPaymentCommandDto(payment.PaymentGuid, payment.Method, payment.Amount, payment.Reference, payment.ReservationToken, payment.CardTransactions, payment.IdempotencyKey),
            request.CustomerName.Trim(),
            request.CustomerPhone.Trim(),
            request.Note.Trim());

        if (installmentOperations is not null)
        {
            var operation = await installmentOperations.ExecuteCreateAsync(
                request.Session,
                apiRequest,
                payment.Method == PaymentMethodKind.Card && !HasExistingCardAuthorization(payment),
                cancellationToken);
            if (!operation.Succeeded && operation.RequiresReview)
            {
                return new InstallmentOrderCreateResult(
                    false,
                    operation.Message ?? "Installment creation result is unknown. Do not collect payment again.",
                    RequiresReview: true);
            }

            if (operation.Succeeded && operation.Response is not null && operation.LocalOrder is not null)
            {
                cart?.Clear();
                return new InstallmentOrderCreateResult(true, operation.Message ?? $"已创建分期单 {operation.LocalOrder.InstallmentNumber}。", MapSummary(operation.LocalOrder));
            }

            return new InstallmentOrderCreateResult(false, operation.Message ?? "分期创建结果未知，请勿重复收款。");
        }

        if (payment.Method == PaymentMethodKind.Card && !HasExistingCardAuthorization(payment))
        {
            // 银行卡首付必须先由终端授权；普通支付页传入已授权 tender 时不可再次请求终端。
            var authorization = await _cardTerminalClient.AuthorizeAsync(payment.Amount, request.Session, cancellationToken);
            if (!authorization.Approved)
            {
                return new InstallmentOrderCreateResult(false, authorization.Message ?? "银行卡首付未授权，分期单未创建。");
            }

            payment = payment with
            {
                Amount = authorization.AuthorizedAmount ?? payment.Amount,
                Reference = authorization.Reference ?? payment.Reference,
                CardTransactions = authorization.CardTransactions ?? payment.CardTransactions
            };
        }

        apiRequest = apiRequest with
        {
            DownPaymentAmount = payment.Amount,
            DownPayment = new InstallmentPaymentCommandDto(payment.PaymentGuid, payment.Method, payment.Amount, payment.Reference, payment.ReservationToken, payment.CardTransactions, payment.IdempotencyKey)
        };
        var result = await CreateAsync(request.Session, apiRequest, cancellationToken);
        return result.Status == InstallmentWriteStatus.Succeeded && result.LocalOrder is not null
            ? new InstallmentOrderCreateResult(true, result.Message ?? $"已创建分期单 {result.LocalOrder.InstallmentNumber}。", MapSummary(result.LocalOrder))
            : new InstallmentOrderCreateResult(false, result.Message ?? result.Status.ToString());
    }

    public async Task<InstallmentOrderActionResult> AddRepaymentAsync(InstallmentOrderRepaymentRequest request, CancellationToken cancellationToken = default)
    {
        var local = await localRepository.GetAsync(request.InstallmentGuid, cancellationToken);
        if (local is null)
        {
            return new InstallmentOrderActionResult(false, "未找到本机缓存的分期单。");
        }

        var apiRequest = new InstallmentAppendPaymentRequest(
            request.InstallmentGuid,
            request.Payment.PaymentGuid,
            request.Session.StoreCode,
            request.Session.DeviceCode,
            request.Session.CashierId,
            request.Session.CashierName,
            Math.Min(request.Payment.Amount, local.BalanceAmount),
            request.Payment.Method,
            request.Payment.Reference,
            request.Payment.ReservationToken,
            request.Payment.CardTransactions,
            EnsureIdempotencyKey(request.Payment.IdempotencyKey, request.InstallmentGuid));
        if (installmentOperations is not null)
        {
            var operation = await installmentOperations.ExecuteRepaymentAsync(
                request.Session,
                apiRequest,
                request.Payment.Method == PaymentMethodKind.Card && !HasExistingCardAuthorization(request.Payment),
                cancellationToken);
            return new InstallmentOrderActionResult(
                operation.Succeeded,
                operation.Message ?? (operation.Succeeded ? "补款已记录。" : "补款结果未知，请勿重复收款。"),
                operation.LocalOrder is null ? null : MapSummary(operation.LocalOrder),
                operation.RequiresReview);
        }

        var result = await AppendPaymentAsync(request.Session, apiRequest, cancellationToken);
        return new InstallmentOrderActionResult(result.Status == InstallmentWriteStatus.Succeeded, result.Message ?? "补款已记录。", result.LocalOrder is null ? null : MapSummary(result.LocalOrder));
    }

    public Task<InstallmentOrderActionResult> AddRepaymentAsync(
        Guid orderId,
        PosSessionState session,
        decimal amount,
        PaymentMethodKind method,
        string? reference,
        string? reservationToken,
        CancellationToken cancellationToken = default)
    {
        return AddRepaymentAsync(new InstallmentOrderRepaymentRequest(orderId, session, new InstallmentPaymentDraft(Guid.NewGuid(), method, amount, reference, reservationToken)), cancellationToken);
    }

    public async Task<InstallmentOrderActionResult> CancelWithRefundAsync(Guid orderId, PosSessionState session, InstallmentCancelRefundMode refundMode = InstallmentCancelRefundMode.OriginalRoute, CancellationToken cancellationToken = default)
    {
        var local = await localRepository.GetAsync(orderId, cancellationToken);
        if (local is null)
        {
            return new InstallmentOrderActionResult(false, "未找到本机缓存的分期单。");
        }

        if (installmentOperations is not null)
        {
            var operation = await installmentOperations.ExecuteCancelAsync(local, session, refundMode: refundMode, cancellationToken: cancellationToken);
            return new InstallmentOrderActionResult(
                operation.Succeeded,
                operation.Message ?? (operation.Succeeded
                    ? refundMode == InstallmentCancelRefundMode.Voucher
                        ? FormatVoucherRefundCompletedMessage(operation.LocalOrder)
                        : "分期单已取消并退款。"
                    : "退款结果未知，已锁定等待处理。"),
                operation.LocalOrder is null ? null : MapSummary(operation.LocalOrder),
                operation.RequiresReview);
        }

        return new InstallmentOrderActionResult(false, "安全取消服务未配置，已在退款 provider 调用前停止。", RequiresReview: true);
    }

    // 完成提示同时列出券码与金额：即使退款券凭证打印失败，收银员也能当场看到券码。
    private static string FormatVoucherRefundCompletedMessage(LocalInstallmentOrder? order)
    {
        var vouchers = order is null
            ? []
            : InstallmentReceiptMapper.GetRefundVouchers(order)
                .Select(voucher => $"{voucher.VoucherCode} {voucher.Amount.ToString("C2", CultureInfo.GetCultureInfo("en-AU"))}")
                .ToList();
        return vouchers.Count == 0
            ? "分期单已取消，已发退款代金券。"
            : $"分期单已取消，退款代金券：{string.Join("、", vouchers)}";
    }

    public Task<InstallmentOrderActionResult> CancelWithRefundAsync(Guid orderId, PosSessionState session, string? reason, CancellationToken cancellationToken = default)
    {
        return CancelWithRefundAsync(orderId, session, cancellationToken: cancellationToken);
    }

    public async Task<InstallmentOrderActionResult> VoidCancelAsync(Guid orderId, PosSessionState session, string? reason = null, CancellationToken cancellationToken = default)
    {
        var result = await VoidCancelAsync(
            session,
            new InstallmentVoidRequest(
                orderId,
                session.StoreCode,
                session.DeviceCode,
                session.CashierId,
                session.CashierName,
                DateTimeOffset.Now,
                string.IsNullOrWhiteSpace(reason) ? "作废分期单" : reason.Trim(),
                $"{orderId:D}:void",
                orderId),
            cancellationToken);
        return new InstallmentOrderActionResult(result.Status == InstallmentWriteStatus.Succeeded, result.Message ?? "分期单已作废。", result.LocalOrder is null ? null : MapSummary(result.LocalOrder));
    }

    public Task<InstallmentOrderActionResult> VoidAsync(Guid orderId, PosSessionState session, string? reason = null, CancellationToken cancellationToken = default)
    {
        return VoidCancelAsync(orderId, session, reason, cancellationToken);
    }

    public async Task<InstallmentOrderActionResult> ConfirmPickupAsync(Guid orderId, PosSessionState session, CancellationToken cancellationToken = default)
    {
        if (!session.IsOnline)
        {
            return new InstallmentOrderActionResult(false, "OnlineRequired");
        }

        var request = new InstallmentConfirmPickupRequest(
            orderId,
            session.StoreCode,
            session.DeviceCode,
            session.CashierId,
            session.CashierName,
            DateTimeOffset.Now,
            OperationGuid: orderId,
            IdempotencyKey: $"{orderId:D}:pickup");

        if (installmentOperations is not null)
        {
            var operation = await installmentOperations.ExecutePickupAsync(session, request, cancellationToken);
            return new InstallmentOrderActionResult(
                operation.Succeeded,
                operation.Message ?? (operation.Succeeded ? "分期单已确认提货。" : "提货确认未完成。"),
                operation.LocalOrder is null ? null : MapSummary(operation.LocalOrder),
                operation.RequiresReview);
        }

        try
        {
            var result = await ConfirmPickupAsync(session, request, cancellationToken);
            return new InstallmentOrderActionResult(result.Status == InstallmentWriteStatus.Succeeded, result.Message ?? result.Status.ToString(), result.LocalOrder is null ? null : MapSummary(result.LocalOrder));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // HTTP 超时发生时服务端可能已经确认提货，必须进入人工核对而不是允许重复提交。
            return new InstallmentOrderActionResult(
                false,
                "提货确认请求超时，结果可能已提交；请刷新核对，勿重复确认提货。",
                RequiresReview: true);
        }
    }

    private async Task<LocalInstallmentOrder> SaveSnapshotAsync(InstallmentDetailsDto details, CancellationToken cancellationToken)
    {
        var localOrder = new LocalInstallmentOrder(details.InstallmentGuid, details.InstallmentGuid, details.InstallmentNumber, details.StoreCode, details.DeviceCode, details.CashierId, details.CashierName, details.CustomerName, details.CustomerPhone, details.CreatedAt, details.UpdatedAt ?? DateTimeOffset.UtcNow, details.TotalAmount, details.MinimumDownPayment, details.DownPaymentAmount, details.PaidAmount, details.BalanceAmount, details.Status, details.Lines, details.Payments, details.PickupInfo, details.Note, details.CancellationInfo);
        await localRepository.UpsertAsync(localOrder, cancellationToken);
        return localOrder;
    }

    private static InstallmentOrderSummary MapSummary(LocalInstallmentOrder order)
    {
        return new InstallmentOrderSummary(
            order.InstallmentGuid,
            order.InstallmentNumber,
            order.CustomerName,
            order.CustomerPhone,
            order.TotalAmount,
            order.DownPaymentAmount,
            order.PaidAmount,
            order.BalanceAmount,
            0,
            order.Status == InstallmentStatus.Active && order.BalanceAmount > 0m,
            order.Status == InstallmentStatus.PaidOff,
            // 取消退款与 API 闸门共用规则：已付清未提货也可取消退款；作废仍只限进行中单。
            InstallmentLifecycleRules.CanCancelWithRefund(order.Status, order.BalanceAmount),
            order.Status == InstallmentStatus.Active && order.BalanceAmount > 0m,
            GetStatusText(order),
            order.DeviceCode,
            order.UpdatedAt);
    }

    private static InstallmentOrderSummary MapSummary(InstallmentSummaryDto order)
    {
        return new InstallmentOrderSummary(
            order.InstallmentGuid,
            order.InstallmentNumber,
            order.CustomerName,
            order.CustomerPhone,
            order.TotalAmount,
            order.DownPaymentAmount,
            order.PaidAmount,
            order.BalanceAmount,
            0,
            order.Status == InstallmentStatus.Active && order.BalanceAmount > 0m,
            order.Status == InstallmentStatus.PaidOff,
            InstallmentLifecycleRules.CanCancelWithRefund(order.Status, order.BalanceAmount),
            order.Status == InstallmentStatus.Active && order.BalanceAmount > 0m,
            GetStatusText(order.Status, order.CancellationKind),
            order.DeviceCode,
            order.UpdatedAt);
    }

    private static string EnsureIdempotencyKey(string? value, Guid scope) => string.IsNullOrWhiteSpace(value) ? $"{scope:D}:{Guid.NewGuid():D}" : value.Trim();

    private static bool HasExistingCardAuthorization(InstallmentPaymentDraft payment)
    {
        // 只有终端交易明细能证明银行卡已收款；幂等键不能替代授权。
        return payment.CardTransactions is { Count: > 0 };
    }

    private static string GetStatusText(LocalInstallmentOrder order)
    {
        return order.Status switch
        {
            InstallmentStatus.Active => "待补款",
            InstallmentStatus.PaidOff => "待提货",
            InstallmentStatus.PickedUp => "已提货",
            InstallmentStatus.Cancelled when order.CancellationInfo?.Kind == InstallmentCancellationKind.VoidCancel => "已作废",
            InstallmentStatus.Cancelled => "已取消",
            _ => order.Status.ToString()
        };
    }

    private static string GetStatusText(
        InstallmentStatus status,
        InstallmentCancellationKind? cancellationKind = null)
    {
        return status switch
        {
            InstallmentStatus.Active => "待补款",
            InstallmentStatus.PaidOff => "待提货",
            InstallmentStatus.PickedUp => "已提货",
            InstallmentStatus.Cancelled when cancellationKind == InstallmentCancellationKind.VoidCancel => "已作废",
            InstallmentStatus.Cancelled => "已取消",
            _ => status.ToString()
        };
    }
}

public enum InstallmentAmendLinesOutcome
{
    /// <summary>已生效（含对账确认已生效）。</summary>
    Succeeded = 1,
    /// <summary>当前离线，修改商品只能在线进行。</summary>
    OnlineRequired,
    /// <summary>服务端或客户端预校验明确拒绝，订单未改动。</summary>
    Rejected,
    /// <summary>订单已被其他设备修改（409），服务层已刷新快照。</summary>
    Stale,
    /// <summary>对账确认本次没有生效，订单未改动，可重试。</summary>
    Failed,
    /// <summary>结果无法确认（网络中断且对账也失败）；重试安全（乐观并发）。</summary>
    Unknown
}

/// <summary>
/// 修改商品的结果。<see cref="ErrorCode"/> 供界面映射本地化文案，<see cref="Message"/> 是中文兜底文案（仅用于日志 / 兜底显示）。
/// </summary>
public sealed record InstallmentAmendLinesResult(
    InstallmentAmendLinesOutcome Outcome,
    string Message,
    string? ErrorCode = null,
    LocalInstallmentOrder? Order = null,
    InstallmentOrderSummary? Summary = null)
{
    public bool Succeeded => Outcome == InstallmentAmendLinesOutcome.Succeeded;

    public static InstallmentAmendLinesResult Create(
        InstallmentAmendLinesOutcome outcome,
        string message,
        string? errorCode = null,
        LocalInstallmentOrder? order = null,
        InstallmentOrderSummary? summary = null) =>
        new(outcome, message, errorCode, order, summary);

    public static InstallmentAmendLinesResult Success(LocalInstallmentOrder order, InstallmentOrderSummary summary) =>
        new(InstallmentAmendLinesOutcome.Succeeded, "商品已修改。", null, order, summary);
}

public sealed record InstallmentHistorySearchQuery(
    DateTimeOffset? UpdatedFrom = null,
    DateTimeOffset? UpdatedTo = null,
    string? DeviceCode = null,
    string? Keyword = null,
    int Take = 100);

public sealed record InstallmentOrderSummary(Guid OrderId, string OrderNumber, string CustomerName, string CustomerPhone, decimal TotalAmount, decimal DownPaymentAmount, decimal PaidAmount, decimal OutstandingAmount, int InstallmentMonths, bool CanAddRepayment, bool CanConfirmPickup, bool CanCancelRefund, bool CanVoid, string Status, string DeviceCode, DateTimeOffset UpdatedAt)
{
    public bool CanCancelWithRefund => CanCancelRefund;

    public bool CanVoidCancel => CanVoid;

    public string DownPaymentMethod => string.Empty;
}

public sealed record InstallmentPaymentDraft(Guid PaymentGuid, PaymentMethodKind Method, decimal Amount, string? Reference = null, string? ReservationToken = null, IReadOnlyList<CardTransactionDto>? CardTransactions = null, string? IdempotencyKey = null);

public sealed record InstallmentOrderCreateRequest(
    PosSessionState Session,
    PosCartServiceSnapshot CartSnapshot,
    string CustomerName,
    string CustomerPhone,
    decimal DownPaymentAmount,
    InstallmentPaymentDraft DownPayment,
    string Note,
    Guid InstallmentGuid = default)
{
    public InstallmentOrderCreateRequest(PosSessionState session, PosCartServiceSnapshot cartSnapshot, string customerName, string customerPhone, int installmentMonths, decimal downPaymentAmount, PaymentMethodKind method, string? reference, string? reservationToken, string note)
        : this(session, cartSnapshot, customerName, customerPhone, downPaymentAmount, new InstallmentPaymentDraft(Guid.NewGuid(), method, downPaymentAmount, reference, reservationToken), note, Guid.NewGuid())
    {
    }
}

public sealed record InstallmentOrderRepaymentRequest(Guid InstallmentGuid, PosSessionState Session, InstallmentPaymentDraft Payment);

public sealed record InstallmentOrderCreateResult(
    bool Succeeded,
    string Message,
    InstallmentOrderSummary? Order = null,
    bool RequiresReview = false);

public sealed record InstallmentOrderActionResult(
    bool Succeeded,
    string Message,
    InstallmentOrderSummary? Order = null,
    bool RequiresReview = false);

public sealed class InstallmentApiClient(HttpClient httpClient) : IInstallmentApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    internal static readonly TimeSpan HistoryQueryTimeout = TimeSpan.FromSeconds(2);

    public async Task<InstallmentHistoryQueryResponse> QueryHistoryAsync(
        InstallmentHistoryQueryRequest request,
        CancellationToken cancellationToken = default)
    {
        var requestUri = BuildUri(
            "api/v1/installments/history",
            ("storeCode", request.StoreCode),
            ("deviceCode", request.DeviceCode),
            ("createdFrom", request.CreatedFrom?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)),
            ("createdTo", request.CreatedTo?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)),
            ("updatedFrom", request.UpdatedFrom?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)),
            ("updatedTo", request.UpdatedTo?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)),
            ("keyword", request.Keyword),
            ("status", request.Status?.ToString()),
            ("take", request.Take.ToString(CultureInfo.InvariantCulture)),
            ("skip", request.Skip.ToString(CultureInfo.InvariantCulture)),
            ("orderByUpdatedAt", request.OrderByUpdatedAt ? "true" : "false"));

        using var queryTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        queryTimeout.CancelAfter(HistoryQueryTimeout);
        try
        {
            return await GetAsync<InstallmentHistoryQueryResponse>(requestUri, queryTimeout.Token);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // 自建 2 秒超时触发（调用方未取消）：单独记 Warning，便于区分慢查询与断网。
            ConsoleLog.WriteWarning(
                "Installment",
                $"installment history query timed out reason=timeout timeoutMs={(int)HistoryQueryTimeout.TotalMilliseconds}",
                new ApplicationLogContext(
                    RequestPath: "api/v1/installments/history",
                    RequestMethod: "GET",
                    Properties: new Dictionary<string, object?>
                    {
                        ["storeCode"] = request.StoreCode,
                        ["deviceCode"] = request.DeviceCode,
                        ["reason"] = "timeout"
                    }),
                ex);
            throw new CatalogApiException(
                "分期查询超过 2 秒，请缩小日期范围后重试。 / Installment search exceeded 2 seconds. Narrow the date range and retry.",
                HttpStatusCode.RequestTimeout,
                "INSTALLMENT_HISTORY_QUERY_TIMEOUT",
                ex);
        }
    }

    public Task<InstallmentDetailsDto> GetDetailsAsync(
        Guid installmentGuid,
        CancellationToken cancellationToken = default) =>
        GetAsync<InstallmentDetailsDto>($"api/v1/installments/{installmentGuid:D}", cancellationToken);

    public Task<InstallmentCreateResponse> CreateAsync(InstallmentCreateRequest request, CancellationToken cancellationToken = default) => PostAsync<InstallmentCreateRequest, InstallmentCreateResponse>("api/v1/installments", request, cancellationToken);

    public Task<InstallmentRepaymentCapabilitiesResponse> GetRepaymentCapabilitiesAsync(CancellationToken cancellationToken = default) =>
        GetAsync<InstallmentRepaymentCapabilitiesResponse>("api/v1/installments/capabilities", cancellationToken);

    public Task<InstallmentRepaymentClaimDto> CreateRepaymentClaimAsync(Guid installmentGuid, InstallmentRepaymentClaimCreateRequest request, CancellationToken cancellationToken = default) =>
        PostAsync<InstallmentRepaymentClaimCreateRequest, InstallmentRepaymentClaimDto>($"api/v1/installments/{installmentGuid:D}/repayment-claims", request, cancellationToken);

    public Task<InstallmentRepaymentClaimDto> BeginRepaymentProviderAsync(Guid installmentGuid, Guid operationGuid, InstallmentRepaymentClaimBeginProviderRequest request, CancellationToken cancellationToken = default) =>
        PostAsync<InstallmentRepaymentClaimBeginProviderRequest, InstallmentRepaymentClaimDto>($"api/v1/installments/{installmentGuid:D}/repayment-claims/{operationGuid:D}/begin-provider", request, cancellationToken);

    public Task<InstallmentRepaymentClaimDto> GetRepaymentClaimAsync(Guid installmentGuid, Guid operationGuid, CancellationToken cancellationToken = default) =>
        GetAsync<InstallmentRepaymentClaimDto>($"api/v1/installments/{installmentGuid:D}/repayment-claims/{operationGuid:D}", cancellationToken);

    public Task<InstallmentRepaymentClaimDto> ResolveRepaymentClaimAsync(Guid installmentGuid, Guid operationGuid, InstallmentRepaymentClaimResolveRequest request, CancellationToken cancellationToken = default) =>
        PostAsync<InstallmentRepaymentClaimResolveRequest, InstallmentRepaymentClaimDto>($"api/v1/installments/{installmentGuid:D}/repayment-claims/{operationGuid:D}/resolve", request, cancellationToken);

    public Task<InstallmentRepaymentClaimDto> CommitRepaymentClaimAsync(Guid installmentGuid, Guid operationGuid, InstallmentRepaymentClaimCommitRequest request, CancellationToken cancellationToken = default) =>
        PostAsync<InstallmentRepaymentClaimCommitRequest, InstallmentRepaymentClaimDto>($"api/v1/installments/{installmentGuid:D}/repayment-claims/{operationGuid:D}/commit", request, cancellationToken);

    public Task<InstallmentCancelClaimDto> CreateCancelClaimAsync(Guid installmentGuid, InstallmentCancelClaimCreateRequest request, CancellationToken cancellationToken = default) =>
        PostAsync<InstallmentCancelClaimCreateRequest, InstallmentCancelClaimDto>($"api/v1/installments/{installmentGuid:D}/cancel-claims", request, cancellationToken);

    public Task<InstallmentCancelClaimDto> BeginCancelRefundAsync(Guid installmentGuid, Guid operationGuid, CancellationToken cancellationToken = default) =>
        PostAsync<InstallmentCancelClaimDto>($"api/v1/installments/{installmentGuid:D}/cancel-claims/{operationGuid:D}/begin-refund", cancellationToken);

    public Task<InstallmentCancelClaimDto> GetCancelClaimAsync(Guid installmentGuid, Guid operationGuid, CancellationToken cancellationToken = default) =>
        GetAsync<InstallmentCancelClaimDto>($"api/v1/installments/{installmentGuid:D}/cancel-claims/{operationGuid:D}", cancellationToken);

    public Task<InstallmentCancelClaimDto> ResolveCancelClaimAsync(Guid installmentGuid, Guid operationGuid, InstallmentCancelClaimResolveRequest request, CancellationToken cancellationToken = default) =>
        PostAsync<InstallmentCancelClaimResolveRequest, InstallmentCancelClaimDto>($"api/v1/installments/{installmentGuid:D}/cancel-claims/{operationGuid:D}/resolve", request, cancellationToken);

    public Task<InstallmentCancelClaimDto> CommitCancelClaimAsync(Guid installmentGuid, Guid operationGuid, InstallmentCancelClaimCommitRequest request, CancellationToken cancellationToken = default) =>
        PostAsync<InstallmentCancelClaimCommitRequest, InstallmentCancelClaimDto>($"api/v1/installments/{installmentGuid:D}/cancel-claims/{operationGuid:D}/commit", request, cancellationToken);

    public Task<InstallmentAppendPaymentResponse> AppendPaymentAsync(InstallmentAppendPaymentRequest request, CancellationToken cancellationToken = default) => PostAsync<InstallmentAppendPaymentRequest, InstallmentAppendPaymentResponse>($"api/v1/installments/{request.InstallmentGuid:D}/payments", request, cancellationToken);

    public Task<InstallmentConfirmPickupResponse> ConfirmPickupAsync(InstallmentConfirmPickupRequest request, CancellationToken cancellationToken = default) => PostAsync<InstallmentConfirmPickupRequest, InstallmentConfirmPickupResponse>($"api/v1/installments/{request.InstallmentGuid:D}/pickup", request, cancellationToken);

    public Task<InstallmentAmendLinesResponse> AmendLinesAsync(InstallmentAmendLinesRequest request, CancellationToken cancellationToken = default) => PostAsync<InstallmentAmendLinesRequest, InstallmentAmendLinesResponse>($"api/v1/installments/{request.InstallmentGuid:D}/amend-lines", request, cancellationToken);

    public Task<InstallmentCancelResponse> CancelAsync(InstallmentCancelRequest request, CancellationToken cancellationToken = default) => PostAsync<InstallmentCancelRequest, InstallmentCancelResponse>($"api/v1/installments/{request.InstallmentGuid:D}/cancel", request, cancellationToken);

    public Task<InstallmentVoidResponse> VoidAsync(InstallmentVoidRequest request, CancellationToken cancellationToken = default) => PostAsync<InstallmentVoidRequest, InstallmentVoidResponse>($"api/v1/installments/{request.InstallmentGuid:D}/void", request, cancellationToken);

    private async Task<TResponse> GetAsync<TResponse>(string path, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        using var response = await httpClient.GetAsync(path, cancellationToken);
        return await ReadResponseAsync<TResponse>(response, "GET", path, stopwatch, cancellationToken);
    }

    private async Task<TResponse> PostAsync<TRequest, TResponse>(string path, TRequest request, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        using var response = await httpClient.PostAsJsonAsync(path, request, JsonOptions, cancellationToken);
        return await ReadResponseAsync<TResponse>(response, "POST", path, stopwatch, cancellationToken);
    }

    private async Task<TResponse> PostAsync<TResponse>(string path, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return await ReadResponseAsync<TResponse>(response, "POST", path, stopwatch, cancellationToken);
    }

    private static async Task<TResponse> ReadResponseAsync<TResponse>(
        HttpResponseMessage response,
        string method,
        string path,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        // 先读字符串再解析：网关 502 HTML、空体 401 不能以 JsonException 丢掉状态码。
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        ApiResult<TResponse>? payload = null;
        JsonException? parseException = null;
        if (!string.IsNullOrWhiteSpace(content))
        {
            try
            {
                payload = JsonSerializer.Deserialize<ApiResult<TResponse>>(content, JsonOptions);
            }
            catch (JsonException ex)
            {
                parseException = ex;
            }
        }

        if (parseException is not null || string.IsNullOrWhiteSpace(content) ||
            !response.IsSuccessStatusCode || payload?.Success != true || payload.Data is null)
        {
            LogFailure(response, method, path, payload?.ErrorCode, payload?.Message, content, parseException, stopwatch);
            if (parseException is not null)
            {
                // 异常形状必须保持 JsonException：调用方的 `CatalogApiException when NotFound` 会重建远端 claim，
                // 网关 HTML 404（部署切换期间）若被当成真实 404，会把「读取失败、保持锁定」变成重建 claim。
                ExceptionDispatchInfo.Throw(parseException);
            }

            if (string.IsNullOrWhiteSpace(content))
            {
                // 与原 ReadFromJsonAsync 对空响应体的行为一致（抛 JsonException），同样避免空体 404 命中重建分支。
                throw new JsonException($"Installment API returned an empty response with HTTP {(int)response.StatusCode}.");
            }

            throw new CatalogApiException(payload?.Message ?? $"Installment API request failed with HTTP {(int)response.StatusCode}.", response.StatusCode, payload?.ErrorCode);
        }

        return payload.Data;
    }

    private static void LogFailure(
        HttpResponseMessage response,
        string method,
        string path,
        string? errorCode,
        string? serverMessage,
        string content,
        JsonException? parseException,
        Stopwatch stopwatch)
    {
        var statusCode = (int)response.StatusCode;
        // 路径只到问号前（查询串含门店/关键词）；分期 Guid 本身就在路径里，可直接串联服务端日志。
        var requestPath = path.Split('?', 2)[0];
        var body = response.IsSuccessStatusCode || string.IsNullOrWhiteSpace(content)
            ? null
            : content.Trim() is { Length: > 256 } longBody ? longBody[..256] : content.Trim();
        ConsoleLog.WriteWarning(
            "Installment",
            $"installment api failed method={method} path={requestPath} http={statusCode} " +
            $"errorCode={errorCode ?? "<null>"} message={serverMessage ?? "<null>"} " +
            (parseException is null ? string.Empty : "reason=invalid-json ") +
            $"elapsedMs={stopwatch.ElapsedMilliseconds}" +
            (body is null ? string.Empty : $" body={body}"),
            new ApplicationLogContext(
                TraceId: ResolveTraceId(requestPath),
                RequestPath: requestPath,
                RequestMethod: method,
                StatusCode: statusCode,
                Properties: new Dictionary<string, object?>
                {
                    ["errorCode"] = errorCode,
                    ["elapsedMs"] = stopwatch.ElapsedMilliseconds
                }),
            parseException);
    }

    /// <summary>
    /// 路径里最后一个 Guid：claim 接口是 operationGuid（与分期操作日志的 TraceId 一致），其余是 installmentGuid。
    /// </summary>
    private static string? ResolveTraceId(string requestPath)
    {
        var segments = requestPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var index = segments.Length - 1; index >= 0; index--)
        {
            if (Guid.TryParse(segments[index], out var guid))
            {
                return guid.ToString("D");
            }
        }

        return null;
    }

    private static string BuildUri(string path, params (string Name, string? Value)[] query)
    {
        var queryString = string.Join(
            "&",
            query
                .Where(item => !string.IsNullOrWhiteSpace(item.Value))
                .Select(item => $"{Uri.EscapeDataString(item.Name)}={Uri.EscapeDataString(item.Value!)}"));

        return string.IsNullOrEmpty(queryString)
            ? path
            : $"{path}?{queryString}";
    }
}

public sealed class NoopInstallmentOrderService : IInstallmentOrderService
{
    public static NoopInstallmentOrderService Instance { get; } = new();

    private NoopInstallmentOrderService() { }

    public Task<IReadOnlyList<InstallmentOrderSummary>> GetOrdersAsync(PosSessionState session, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<InstallmentOrderSummary>>([]);
    public Task<IReadOnlyList<InstallmentOrderSummary>> SearchAsync(PosSessionState session, string? keyword, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<InstallmentOrderSummary>>([]);
    public Task<LocalInstallmentOrder?> GetLocalOrderAsync(Guid installmentGuid, CancellationToken cancellationToken = default) => Task.FromResult<LocalInstallmentOrder?>(null);
    public Task<InstallmentWriteResult<InstallmentCreateResponse>> CreateAsync(PosSessionState session, InstallmentCreateRequest request, CancellationToken cancellationToken = default) => Task.FromResult(InstallmentWriteResult<InstallmentCreateResponse>.OnlineRequired(session.IsOnline ? "分期服务尚未接入。" : "OnlineRequired"));
    public Task<InstallmentWriteResult<InstallmentAppendPaymentResponse>> AppendPaymentAsync(PosSessionState session, InstallmentAppendPaymentRequest request, CancellationToken cancellationToken = default) => Task.FromResult(InstallmentWriteResult<InstallmentAppendPaymentResponse>.OnlineRequired(session.IsOnline ? "分期服务尚未接入。" : "OnlineRequired"));
    public Task<InstallmentWriteResult<InstallmentConfirmPickupResponse>> ConfirmPickupAsync(PosSessionState session, InstallmentConfirmPickupRequest request, CancellationToken cancellationToken = default) => Task.FromResult(InstallmentWriteResult<InstallmentConfirmPickupResponse>.OnlineRequired(session.IsOnline ? "分期服务尚未接入。" : "OnlineRequired"));
    public Task<InstallmentWriteResult<InstallmentCancelResponse>> CancelWithRefundAsync(PosSessionState session, InstallmentCancelRequest request, CancellationToken cancellationToken = default) => Task.FromResult(InstallmentWriteResult<InstallmentCancelResponse>.OnlineRequired(session.IsOnline ? "分期服务尚未接入。" : "OnlineRequired"));
    public Task<InstallmentWriteResult<InstallmentVoidResponse>> VoidCancelAsync(PosSessionState session, InstallmentVoidRequest request, CancellationToken cancellationToken = default) => Task.FromResult(InstallmentWriteResult<InstallmentVoidResponse>.OnlineRequired(session.IsOnline ? "分期服务尚未接入。" : "OnlineRequired"));
    public Task<InstallmentOrderCreateResult> CreateOrderAsync(InstallmentOrderCreateRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new InstallmentOrderCreateResult(false, "分期服务尚未接入。"));
    public Task<InstallmentOrderActionResult> AddRepaymentAsync(InstallmentOrderRepaymentRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new InstallmentOrderActionResult(false, "分期服务尚未接入。"));
    public Task<InstallmentOrderActionResult> CancelWithRefundAsync(Guid orderId, PosSessionState session, InstallmentCancelRefundMode refundMode = InstallmentCancelRefundMode.OriginalRoute, CancellationToken cancellationToken = default) => Task.FromResult(new InstallmentOrderActionResult(false, "分期服务尚未接入。"));
    public Task<InstallmentOrderActionResult> VoidCancelAsync(Guid orderId, PosSessionState session, string? reason = null, CancellationToken cancellationToken = default) => Task.FromResult(new InstallmentOrderActionResult(false, "分期服务尚未接入。"));
    public Task<InstallmentOrderActionResult> ConfirmPickupAsync(Guid orderId, PosSessionState session, CancellationToken cancellationToken = default) => Task.FromResult(new InstallmentOrderActionResult(false, "分期服务尚未接入。"));
}
