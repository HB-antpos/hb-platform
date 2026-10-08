using Hbpos.Api.Auth;
using Hbpos.Api.Services;
using BlazorApp.Shared.Constants;
using Hbpos.Contracts.Common;
using Hbpos.Contracts.Installments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Hbpos.Api.Controllers;

[ApiController]
[Route("api/v1/installments")]
public sealed class InstallmentsController(
    IInstallmentService installmentService,
    IInstallmentHistoryService historyService,
    IInstallmentRepaymentClaimService? repaymentClaimService = null,
    IInstallmentRepaymentClaimIdentityResolver? repaymentClaimIdentityResolver = null,
    IInstallmentCancelClaimService? cancelClaimService = null) : ControllerBase
{
    [Authorize]
    [HttpGet("capabilities")]
    public ActionResult<ApiResult<InstallmentRepaymentCapabilitiesResponse>> Capabilities()
    {
        var response = RequireRepaymentClaimService().GetCapabilities();
        return Ok(ApiResult<InstallmentRepaymentCapabilitiesResponse>.Ok(response));
    }

    [Authorize(Policy = CashierAuthorizationPolicies.InstallmentCreate)]
    [HttpPost]
    public async Task<ActionResult<ApiResult<InstallmentCreateResponse>>> Create(
        [FromBody] InstallmentCreateRequest request,
        CancellationToken cancellationToken)
    {
        if (!this.IsDeviceScopeAllowed(request.StoreCode, request.DeviceCode))
        {
            return DeviceAuthorizationExtensions.DeviceScopeForbidden<InstallmentCreateResponse>("Device is not authorized for this store.");
        }

        try
        {
            var response = await installmentService.CreateAsync(request, cancellationToken);
            return Ok(ApiResult<InstallmentCreateResponse>.Ok(response));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResult<InstallmentCreateResponse>.Fail("INSTALLMENT_CREATE_INVALID", ex.Message));
        }
    }

    [Authorize(Policy = CashierAuthorizationPolicies.InstallmentPayment)]
    [HttpPost("{installmentGuid:guid}/payments")]
    public async Task<ActionResult<ApiResult<InstallmentAppendPaymentResponse>>> AppendPayment(
        Guid installmentGuid,
        [FromBody] InstallmentAppendPaymentRequest request,
        CancellationToken cancellationToken)
    {
        if (installmentGuid != request.InstallmentGuid)
        {
            return BadRequest(ApiResult<InstallmentAppendPaymentResponse>.Fail("INSTALLMENT_GUID_MISMATCH", "Installment id does not match the route."));
        }

        if (!this.IsDeviceScopeAllowed(request.StoreCode, request.DeviceCode))
        {
            return DeviceAuthorizationExtensions.DeviceScopeForbidden<InstallmentAppendPaymentResponse>("Device is not authorized for this store.");
        }

        try
        {
            await EnsureNoBlockingCancelClaimAsync(installmentGuid, cancellationToken);
            await RequireRepaymentClaimService().EnsureLegacyAppendAllowedAsync(installmentGuid, cancellationToken);
            var response = await installmentService.AppendPaymentAsync(request, cancellationToken);
            return Ok(ApiResult<InstallmentAppendPaymentResponse>.Ok(response));
        }
        catch (InstallmentRepaymentClaimException ex)
        {
            return ClaimError<InstallmentAppendPaymentResponse>(ex);
        }
        catch (InstallmentCancelClaimException ex)
        {
            return CancelClaimError<InstallmentAppendPaymentResponse>(ex);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResult<InstallmentAppendPaymentResponse>.Fail("INSTALLMENT_PAYMENT_INVALID", ex.Message));
        }
    }

    [Authorize(Policy = CashierAuthorizationPolicies.InstallmentPickup)]
    [HttpPost("{installmentGuid:guid}/pickup")]
    public async Task<ActionResult<ApiResult<InstallmentConfirmPickupResponse>>> ConfirmPickup(
        Guid installmentGuid,
        [FromBody] InstallmentConfirmPickupRequest request,
        CancellationToken cancellationToken)
    {
        if (installmentGuid != request.InstallmentGuid)
        {
            return BadRequest(ApiResult<InstallmentConfirmPickupResponse>.Fail("INSTALLMENT_GUID_MISMATCH", "Installment id does not match the route."));
        }

        if (!this.IsDeviceScopeAllowed(request.StoreCode, request.DeviceCode))
        {
            return DeviceAuthorizationExtensions.DeviceScopeForbidden<InstallmentConfirmPickupResponse>("Device is not authorized for this store.");
        }

        try
        {
            await EnsureNoBlockingCancelClaimAsync(installmentGuid, cancellationToken);
            var details = await historyService.GetDetailsAsync(installmentGuid, cancellationToken);
            if (details is not null &&
                !string.Equals(details.StoreCode, request.StoreCode, StringComparison.OrdinalIgnoreCase))
            {
                return DeviceAuthorizationExtensions.DeviceScopeForbidden<InstallmentConfirmPickupResponse>(
                    "Installment does not belong to this store.");
            }

            var identity = await ResolveRepaymentClaimIdentityAsync(cancellationToken);
            var crossDevice = details is not null &&
                !string.Equals(details.DeviceCode, request.DeviceCode, StringComparison.OrdinalIgnoreCase);
            if (crossDevice && identity is null)
            {
                return CashierIdentityRequired<InstallmentConfirmPickupResponse>();
            }

            if (crossDevice && !HasPermission(identity, Permissions.PosTerminal.Installments.ConfirmPickup))
            {
                return CashierPermissionRequired<InstallmentConfirmPickupResponse>(
                    "Verified cashier lacks installment pickup permission.");
            }

            if (identity is not null)
            {
                request = request with
                {
                    StoreCode = identity.StoreCode,
                    DeviceCode = identity.DeviceCode,
                    CashierId = identity.CashierId,
                    CashierName = identity.CashierName
                };
            }

            await RequireRepaymentClaimService().EnsureNoBlockingClaimAsync(installmentGuid, cancellationToken);
            var response = await installmentService.ConfirmPickupAsync(request, cancellationToken);
            return Ok(ApiResult<InstallmentConfirmPickupResponse>.Ok(response));
        }
        catch (InstallmentRepaymentClaimException ex)
        {
            return ClaimError<InstallmentConfirmPickupResponse>(ex);
        }
        catch (InstallmentCancelClaimException ex)
        {
            return CancelClaimError<InstallmentConfirmPickupResponse>(ex);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResult<InstallmentConfirmPickupResponse>.Fail("INSTALLMENT_PICKUP_INVALID", ex.Message));
        }
    }

    [Authorize(Policy = CashierAuthorizationPolicies.InstallmentCancel)]
    [HttpPost("{installmentGuid:guid}/cancel")]
    public async Task<ActionResult<ApiResult<InstallmentCancelResponse>>> Cancel(
        Guid installmentGuid,
        [FromBody] InstallmentCancelRequest request,
        CancellationToken cancellationToken)
    {
        if (installmentGuid != request.InstallmentGuid)
        {
            return BadRequest(ApiResult<InstallmentCancelResponse>.Fail("INSTALLMENT_GUID_MISMATCH", "Installment id does not match the route."));
        }

        if (!this.IsDeviceScopeAllowed(request.StoreCode, request.DeviceCode))
        {
            return DeviceAuthorizationExtensions.DeviceScopeForbidden<InstallmentCancelResponse>("Device is not authorized for this store.");
        }

        try
        {
            await EnsureLegacyCancelAllowedAsync(installmentGuid, cancellationToken);
            await RequireRepaymentClaimService().EnsureNoBlockingClaimAsync(installmentGuid, cancellationToken);
            var response = await installmentService.CancelAsync(request, cancellationToken);
            return Ok(ApiResult<InstallmentCancelResponse>.Ok(response));
        }
        catch (InstallmentRepaymentClaimException ex)
        {
            return ClaimError<InstallmentCancelResponse>(ex);
        }
        catch (InstallmentCancelClaimException ex)
        {
            return CancelClaimError<InstallmentCancelResponse>(ex);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResult<InstallmentCancelResponse>.Fail("INSTALLMENT_CANCEL_INVALID", ex.Message));
        }
    }

    [Authorize(Policy = CashierAuthorizationPolicies.InstallmentCancel)]
    [HttpPost("{installmentGuid:guid}/void")]
    public async Task<ActionResult<ApiResult<InstallmentVoidResponse>>> Void(
        Guid installmentGuid,
        [FromBody] InstallmentVoidRequest request,
        CancellationToken cancellationToken)
    {
        if (installmentGuid != request.InstallmentGuid)
        {
            return BadRequest(ApiResult<InstallmentVoidResponse>.Fail("INSTALLMENT_GUID_MISMATCH", "Installment id does not match the route."));
        }

        if (!this.IsDeviceScopeAllowed(request.StoreCode, request.DeviceCode))
        {
            return DeviceAuthorizationExtensions.DeviceScopeForbidden<InstallmentVoidResponse>("Device is not authorized for this store.");
        }

        try
        {
            await EnsureNoBlockingCancelClaimAsync(installmentGuid, cancellationToken);
            var details = await historyService.GetDetailsAsync(installmentGuid, cancellationToken);
            if (details is not null &&
                !string.Equals(details.StoreCode, request.StoreCode, StringComparison.OrdinalIgnoreCase))
            {
                return DeviceAuthorizationExtensions.DeviceScopeForbidden<InstallmentVoidResponse>(
                    "Installment does not belong to this store.");
            }

            var identity = await ResolveRepaymentClaimIdentityAsync(cancellationToken);
            var crossDevice = details is not null &&
                !string.Equals(details.DeviceCode, request.DeviceCode, StringComparison.OrdinalIgnoreCase);
            if (crossDevice && identity is null)
            {
                return CashierIdentityRequired<InstallmentVoidResponse>();
            }

            if (crossDevice && !HasPermission(identity, Permissions.PosTerminal.Installments.Cancel))
            {
                return CashierPermissionRequired<InstallmentVoidResponse>(
                    "Verified cashier lacks installment void permission.");
            }

            if (identity is not null)
            {
                request = request with
                {
                    StoreCode = identity.StoreCode,
                    DeviceCode = identity.DeviceCode,
                    CashierId = identity.CashierId,
                    CashierName = identity.CashierName
                };
            }

            await RequireRepaymentClaimService().EnsureNoBlockingClaimAsync(installmentGuid, cancellationToken);
            var response = await installmentService.VoidAsync(request, cancellationToken);
            return Ok(ApiResult<InstallmentVoidResponse>.Ok(response));
        }
        catch (InstallmentRepaymentClaimException ex)
        {
            return ClaimError<InstallmentVoidResponse>(ex);
        }
        catch (InstallmentCancelClaimException ex)
        {
            return CancelClaimError<InstallmentVoidResponse>(ex);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResult<InstallmentVoidResponse>.Fail("INSTALLMENT_VOID_INVALID", ex.Message));
        }
    }

    /// <summary>
    /// 修改分期订单商品列表（整体替换）。仅在线可用；服务端强制校验 AmendLines 权限，
    /// 因为 Audit 模式下策略不会因缺票据而拒绝，必须再以验证过的收银员身份兜底。
    /// </summary>
    [Authorize(Policy = CashierAuthorizationPolicies.InstallmentAmendLines)]
    [HttpPost("{installmentGuid:guid}/amend-lines")]
    public async Task<ActionResult<ApiResult<InstallmentAmendLinesResponse>>> AmendLines(
        Guid installmentGuid,
        [FromBody] InstallmentAmendLinesRequest request,
        CancellationToken cancellationToken)
    {
        if (installmentGuid != request.InstallmentGuid)
        {
            return BadRequest(ApiResult<InstallmentAmendLinesResponse>.Fail("INSTALLMENT_GUID_MISMATCH", "Installment id does not match the route."));
        }

        if (!this.IsDeviceScopeAllowed(request.StoreCode, request.DeviceCode))
        {
            return DeviceAuthorizationExtensions.DeviceScopeForbidden<InstallmentAmendLinesResponse>("Device is not authorized for this store.");
        }

        try
        {
            var identity = await ResolveRepaymentClaimIdentityAsync(cancellationToken);
            if (identity is null)
            {
                return CashierIdentityRequired<InstallmentAmendLinesResponse>();
            }

            if (!HasPermission(identity, Permissions.PosTerminal.Installments.AmendLines))
            {
                return CashierPermissionRequired<InstallmentAmendLinesResponse>(
                    "Verified cashier lacks installment amend lines permission.");
            }

            // 门店 / 设备 / 收银员一律以票据身份为准，忽略请求体里可伪造的值。
            request = request with
            {
                StoreCode = identity.StoreCode,
                DeviceCode = identity.DeviceCode,
                CashierId = identity.CashierId,
                CashierName = identity.CashierName
            };

            await EnsureNoBlockingCancelClaimAsync(installmentGuid, cancellationToken);
            await RequireRepaymentClaimService().EnsureNoBlockingClaimAsync(installmentGuid, cancellationToken);
            var details = await historyService.GetDetailsAsync(installmentGuid, cancellationToken);
            if (details is not null &&
                !string.Equals(details.StoreCode, request.StoreCode, StringComparison.OrdinalIgnoreCase))
            {
                return DeviceAuthorizationExtensions.DeviceScopeForbidden<InstallmentAmendLinesResponse>(
                    "Installment does not belong to this store.");
            }

            var response = await installmentService.AmendLinesAsync(request, cancellationToken);
            return Ok(ApiResult<InstallmentAmendLinesResponse>.Ok(response));
        }
        catch (InstallmentAmendLinesException ex)
        {
            return AmendLinesError<InstallmentAmendLinesResponse>(ex);
        }
        // 以下两个 claim 异常都派生自 InvalidOperationException，必须排在通用处理之前。
        catch (InstallmentRepaymentClaimException ex)
        {
            return ClaimError<InstallmentAmendLinesResponse>(ex);
        }
        catch (InstallmentCancelClaimException ex)
        {
            return CancelClaimError<InstallmentAmendLinesResponse>(ex);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResult<InstallmentAmendLinesResponse>.Fail("INSTALLMENT_AMEND_INVALID", ex.Message));
        }
    }

    [Authorize(Policy = CashierAuthorizationPolicies.InstallmentView)]
    [HttpGet("history")]
    public async Task<ActionResult<ApiResult<InstallmentHistoryQueryResponse>>> History(
        [FromQuery] string storeCode,
        [FromQuery] string? deviceCode,
        [FromQuery] DateTimeOffset? createdFrom,
        [FromQuery] DateTimeOffset? createdTo,
        [FromQuery] string? keyword,
        [FromQuery] InstallmentStatus? status,
        [FromQuery] int take,
        CancellationToken cancellationToken,
        [FromQuery] int skip = 0,
        [FromQuery] DateTimeOffset? updatedFrom = null,
        [FromQuery] DateTimeOffset? updatedTo = null,
        [FromQuery] bool orderByUpdatedAt = false)
    {
        if (string.IsNullOrWhiteSpace(storeCode))
        {
            return BadRequest(ApiResult<InstallmentHistoryQueryResponse>.Fail("STORE_CODE_REQUIRED", "storeCode is required."));
        }

        if (!this.IsDeviceScopeAllowed(storeCode, deviceCode))
        {
            return DeviceAuthorizationExtensions.DeviceScopeForbidden<InstallmentHistoryQueryResponse>("Device is not authorized for this store.");
        }

        if (skip < 0)
        {
            return BadRequest(ApiResult<InstallmentHistoryQueryResponse>.Fail(
                "INSTALLMENT_HISTORY_SKIP_INVALID",
                "skip must be zero or greater."));
        }

        var response = await historyService.QueryAsync(
            new InstallmentHistoryQueryRequest(
                storeCode,
                deviceCode,
                createdFrom,
                createdTo,
                keyword,
                status,
                take <= 0 ? 100 : take,
                skip,
                updatedFrom,
                updatedTo,
                orderByUpdatedAt),
            cancellationToken);
        return Ok(ApiResult<InstallmentHistoryQueryResponse>.Ok(response));
    }

    [Authorize(Policy = CashierAuthorizationPolicies.InstallmentView)]
    [HttpGet("{installmentGuid:guid}")]
    public async Task<ActionResult<ApiResult<InstallmentDetailsDto?>>> Details(
        Guid installmentGuid,
        CancellationToken cancellationToken)
    {
        var details = await historyService.GetDetailsAsync(installmentGuid, cancellationToken);
        // 分期历史支持“本店”范围，读取列表中的他机记录时也必须保持同一门店边界。
        if (details is not null && !this.IsDeviceScopeAllowed(details.StoreCode))
        {
            return DeviceAuthorizationExtensions.DeviceScopeForbidden<InstallmentDetailsDto?>("Device is not authorized for this store.");
        }

        return Ok(ApiResult<InstallmentDetailsDto?>.Ok(details));
    }

    [Authorize(Policy = CashierAuthorizationPolicies.InstallmentPayment)]
    [HttpPost("{installmentGuid:guid}/repayment-claims")]
    public async Task<ActionResult<ApiResult<InstallmentRepaymentClaimDto>>> CreateRepaymentClaim(
        Guid installmentGuid,
        [FromBody] InstallmentRepaymentClaimCreateRequest request,
        CancellationToken cancellationToken)
    {
        var identity = await ResolveRepaymentClaimIdentityAsync(cancellationToken);
        if (identity is null)
        {
            return CashierIdentityRequired<InstallmentRepaymentClaimDto>();
        }

        try
        {
            var response = await RequireRepaymentClaimService().CreateAsync(
                installmentGuid,
                request,
                identity,
                cancellationToken);
            return Ok(ApiResult<InstallmentRepaymentClaimDto>.Ok(response));
        }
        catch (InstallmentRepaymentClaimException ex)
        {
            return ClaimError<InstallmentRepaymentClaimDto>(ex);
        }
    }

    [Authorize(Policy = CashierAuthorizationPolicies.InstallmentPayment)]
    [HttpPost("{installmentGuid:guid}/repayment-claims/{operationGuid:guid}/begin-provider")]
    public async Task<ActionResult<ApiResult<InstallmentRepaymentClaimDto>>> BeginRepaymentProvider(
        Guid installmentGuid,
        Guid operationGuid,
        [FromBody] InstallmentRepaymentClaimBeginProviderRequest request,
        CancellationToken cancellationToken)
    {
        var identity = await ResolveRepaymentClaimIdentityAsync(cancellationToken);
        if (identity is null)
        {
            return CashierIdentityRequired<InstallmentRepaymentClaimDto>();
        }

        try
        {
            var response = await RequireRepaymentClaimService().BeginProviderAsync(
                installmentGuid,
                operationGuid,
                request,
                identity,
                cancellationToken);
            return Ok(ApiResult<InstallmentRepaymentClaimDto>.Ok(response));
        }
        catch (InstallmentRepaymentClaimException ex)
        {
            return ClaimError<InstallmentRepaymentClaimDto>(ex);
        }
    }

    [Authorize]
    [HttpPost("{installmentGuid:guid}/repayment-claims/{operationGuid:guid}/prepare-provider")]
    public async Task<ActionResult<ApiResult<InstallmentRepaymentClaimDto>>> PrepareRepaymentProvider(
        Guid installmentGuid,
        Guid operationGuid,
        [FromBody] InstallmentRepaymentClaimPrepareProviderRequest request,
        CancellationToken cancellationToken)
    {
        var identity = await ResolveRepaymentClaimIdentityAsync(cancellationToken);
        if (identity is null)
        {
            return CashierIdentityRequired<InstallmentRepaymentClaimDto>();
        }

        try
        {
            var response = await RequireRepaymentClaimService().PrepareProviderAsync(
                installmentGuid,
                operationGuid,
                request,
                identity,
                cancellationToken);
            return Ok(ApiResult<InstallmentRepaymentClaimDto>.Ok(response));
        }
        catch (InstallmentRepaymentClaimException ex)
        {
            return ClaimError<InstallmentRepaymentClaimDto>(ex);
        }
    }

    [Authorize]
    [HttpGet("{installmentGuid:guid}/repayment-claims/{operationGuid:guid}")]
    public async Task<ActionResult<ApiResult<InstallmentRepaymentClaimDto>>> GetRepaymentClaim(
        Guid installmentGuid,
        Guid operationGuid,
        CancellationToken cancellationToken)
    {
        var identity = await ResolveRepaymentClaimIdentityAsync(cancellationToken);
        if (identity is null)
        {
            return CashierIdentityRequired<InstallmentRepaymentClaimDto>();
        }

        try
        {
            var response = await RequireRepaymentClaimService().GetAsync(
                installmentGuid,
                operationGuid,
                identity,
                cancellationToken);
            return Ok(ApiResult<InstallmentRepaymentClaimDto>.Ok(response));
        }
        catch (InstallmentRepaymentClaimException ex)
        {
            return ClaimError<InstallmentRepaymentClaimDto>(ex);
        }
    }

    [Authorize]
    [HttpPost("{installmentGuid:guid}/repayment-claims/{operationGuid:guid}/resolve")]
    public async Task<ActionResult<ApiResult<InstallmentRepaymentClaimDto>>> ResolveRepaymentClaim(
        Guid installmentGuid,
        Guid operationGuid,
        [FromBody] InstallmentRepaymentClaimResolveRequest request,
        CancellationToken cancellationToken)
    {
        var identity = await ResolveRepaymentClaimIdentityAsync(cancellationToken);
        if (identity is null)
        {
            return CashierIdentityRequired<InstallmentRepaymentClaimDto>();
        }

        try
        {
            var response = await RequireRepaymentClaimService().ResolveAsync(
                installmentGuid,
                operationGuid,
                request,
                identity,
                cancellationToken);
            return Ok(ApiResult<InstallmentRepaymentClaimDto>.Ok(response));
        }
        catch (InstallmentRepaymentClaimException ex)
        {
            return ClaimError<InstallmentRepaymentClaimDto>(ex);
        }
    }

    [Authorize]
    [HttpPost("{installmentGuid:guid}/repayment-claims/{operationGuid:guid}/commit")]
    public async Task<ActionResult<ApiResult<InstallmentRepaymentClaimDto>>> CommitRepaymentClaim(
        Guid installmentGuid,
        Guid operationGuid,
        [FromBody] InstallmentRepaymentClaimCommitRequest request,
        CancellationToken cancellationToken)
    {
        var identity = await ResolveRepaymentClaimIdentityAsync(cancellationToken);
        if (identity is null)
        {
            return CashierIdentityRequired<InstallmentRepaymentClaimDto>();
        }

        try
        {
            var response = await RequireRepaymentClaimService().CommitAsync(
                installmentGuid,
                operationGuid,
                request,
                identity,
                cancellationToken);
            return Ok(ApiResult<InstallmentRepaymentClaimDto>.Ok(response));
        }
        catch (InstallmentRepaymentClaimException ex)
        {
            return ClaimError<InstallmentRepaymentClaimDto>(ex);
        }
    }

    [Authorize(Policy = CashierAuthorizationPolicies.InstallmentCancel)]
    [HttpPost("{installmentGuid:guid}/cancel-claims")]
    public async Task<ActionResult<ApiResult<InstallmentCancelClaimDto>>> CreateCancelClaim(
        Guid installmentGuid,
        [FromBody] InstallmentCancelClaimCreateRequest request,
        CancellationToken cancellationToken)
    {
        var identity = await ResolveRepaymentClaimIdentityAsync(cancellationToken);
        if (identity is null)
        {
            return CashierIdentityRequired<InstallmentCancelClaimDto>();
        }

        try
        {
            var response = await RequireCancelClaimService().CreateAsync(
                installmentGuid,
                request,
                identity,
                cancellationToken);
            return Ok(ApiResult<InstallmentCancelClaimDto>.Ok(response));
        }
        catch (InstallmentCancelClaimException ex)
        {
            return CancelClaimError<InstallmentCancelClaimDto>(ex);
        }
        catch (InstallmentRepaymentClaimException ex)
            when (ex.Code == InstallmentRepaymentClaimErrorCodes.Busy)
        {
            return CancelClaimError<InstallmentCancelClaimDto>(new InstallmentCancelClaimException(
                InstallmentCancelClaimErrorCodes.Busy,
                ex.Message));
        }
    }

    [Authorize(Policy = CashierAuthorizationPolicies.InstallmentCancel)]
    [HttpPost("{installmentGuid:guid}/cancel-claims/{operationGuid:guid}/begin-refund")]
    public async Task<ActionResult<ApiResult<InstallmentCancelClaimDto>>> BeginCancelClaimRefund(
        Guid installmentGuid,
        Guid operationGuid,
        CancellationToken cancellationToken)
    {
        var identity = await ResolveRepaymentClaimIdentityAsync(cancellationToken);
        if (identity is null)
        {
            return CashierIdentityRequired<InstallmentCancelClaimDto>();
        }

        try
        {
            var response = await RequireCancelClaimService().BeginRefundAsync(
                installmentGuid,
                operationGuid,
                identity,
                cancellationToken);
            return Ok(ApiResult<InstallmentCancelClaimDto>.Ok(response));
        }
        catch (InstallmentCancelClaimException ex)
        {
            return CancelClaimError<InstallmentCancelClaimDto>(ex);
        }
    }

    [Authorize(Policy = CashierAuthorizationPolicies.InstallmentCancel)]
    [HttpGet("{installmentGuid:guid}/cancel-claims/{operationGuid:guid}")]
    public async Task<ActionResult<ApiResult<InstallmentCancelClaimDto>>> GetCancelClaim(
        Guid installmentGuid,
        Guid operationGuid,
        CancellationToken cancellationToken)
    {
        var identity = await ResolveRepaymentClaimIdentityAsync(cancellationToken);
        if (identity is null)
        {
            return CashierIdentityRequired<InstallmentCancelClaimDto>();
        }

        try
        {
            var response = await RequireCancelClaimService().GetAsync(
                installmentGuid,
                operationGuid,
                identity,
                cancellationToken);
            return Ok(ApiResult<InstallmentCancelClaimDto>.Ok(response));
        }
        catch (InstallmentCancelClaimException ex)
        {
            return CancelClaimError<InstallmentCancelClaimDto>(ex);
        }
    }

    [Authorize(Policy = CashierAuthorizationPolicies.InstallmentCancel)]
    [HttpPost("{installmentGuid:guid}/cancel-claims/{operationGuid:guid}/resolve")]
    public async Task<ActionResult<ApiResult<InstallmentCancelClaimDto>>> ResolveCancelClaim(
        Guid installmentGuid,
        Guid operationGuid,
        [FromBody] InstallmentCancelClaimResolveRequest request,
        CancellationToken cancellationToken)
    {
        var identity = await ResolveRepaymentClaimIdentityAsync(cancellationToken);
        if (identity is null)
        {
            return CashierIdentityRequired<InstallmentCancelClaimDto>();
        }

        try
        {
            var response = await RequireCancelClaimService().ResolveAsync(
                installmentGuid,
                operationGuid,
                request,
                identity,
                cancellationToken);
            return Ok(ApiResult<InstallmentCancelClaimDto>.Ok(response));
        }
        catch (InstallmentCancelClaimException ex)
        {
            return CancelClaimError<InstallmentCancelClaimDto>(ex);
        }
    }

    [Authorize(Policy = CashierAuthorizationPolicies.InstallmentCancel)]
    [HttpPost("{installmentGuid:guid}/cancel-claims/{operationGuid:guid}/commit")]
    public async Task<ActionResult<ApiResult<InstallmentCancelClaimDto>>> CommitCancelClaim(
        Guid installmentGuid,
        Guid operationGuid,
        [FromBody] InstallmentCancelClaimCommitRequest request,
        CancellationToken cancellationToken)
    {
        var identity = await ResolveRepaymentClaimIdentityAsync(cancellationToken);
        if (identity is null)
        {
            return CashierIdentityRequired<InstallmentCancelClaimDto>();
        }

        try
        {
            var response = await RequireCancelClaimService().CommitAsync(
                installmentGuid,
                operationGuid,
                request,
                identity,
                cancellationToken);
            return Ok(ApiResult<InstallmentCancelClaimDto>.Ok(response));
        }
        catch (InstallmentCancelClaimException ex)
        {
            return CancelClaimError<InstallmentCancelClaimDto>(ex);
        }
        catch (InstallmentRepaymentClaimException ex)
            when (ex.Code == InstallmentRepaymentClaimErrorCodes.Busy)
        {
            return CancelClaimError<InstallmentCancelClaimDto>(new InstallmentCancelClaimException(
                InstallmentCancelClaimErrorCodes.Busy,
                ex.Message));
        }
    }

    private IInstallmentRepaymentClaimService RequireRepaymentClaimService()
    {
        return repaymentClaimService
            ?? throw new InvalidOperationException("Installment repayment claim service is not configured.");
    }

    private IInstallmentCancelClaimService RequireCancelClaimService()
    {
        return cancelClaimService
            ?? throw new InvalidOperationException("Installment cancellation claim service is not configured.");
    }

    private Task EnsureLegacyCancelAllowedAsync(Guid installmentGuid, CancellationToken cancellationToken) =>
        RequireCancelClaimService().EnsureLegacyCancelAllowedAsync(installmentGuid, cancellationToken);

    private Task EnsureNoBlockingCancelClaimAsync(Guid installmentGuid, CancellationToken cancellationToken) =>
        RequireCancelClaimService().EnsureNoBlockingClaimAsync(installmentGuid, cancellationToken);

    private Task<InstallmentRepaymentClaimIdentity?> ResolveRepaymentClaimIdentityAsync(
        CancellationToken cancellationToken)
    {
        return repaymentClaimIdentityResolver is null
            ? Task.FromResult<InstallmentRepaymentClaimIdentity?>(null)
            : repaymentClaimIdentityResolver.ResolveAsync(HttpContext, cancellationToken);
    }

    private static ActionResult<ApiResult<T>> CashierIdentityRequired<T>()
    {
        return new ObjectResult(ApiResult<T>.Fail(
            "CASHIER_AUTH_REQUIRED",
            "A verified cashier authorization ticket is required."))
        {
            StatusCode = StatusCodes.Status401Unauthorized
        };
    }

    private static bool HasPermission(InstallmentRepaymentClaimIdentity? identity, string permission) =>
        identity?.PermissionCodes?.Contains(permission, StringComparer.OrdinalIgnoreCase) == true;

    private static ActionResult<ApiResult<T>> CashierPermissionRequired<T>(string message)
    {
        return new ObjectResult(ApiResult<T>.Fail(
            "INSTALLMENT_LIFECYCLE_PERMISSION_DENIED",
            message))
        {
            StatusCode = StatusCodes.Status403Forbidden
        };
    }

    private static ActionResult<ApiResult<T>> AmendLinesError<T>(InstallmentAmendLinesException exception)
    {
        // 过期令牌是并发冲突（409）；其余均为请求本身不满足规则（400）。
        var statusCode = exception.Code == InstallmentAmendLinesErrorCodes.Stale
            ? StatusCodes.Status409Conflict
            : StatusCodes.Status400BadRequest;
        return new ObjectResult(ApiResult<T>.Fail(exception.Code, exception.Message))
        {
            StatusCode = statusCode
        };
    }

    private static ActionResult<ApiResult<T>> ClaimError<T>(InstallmentRepaymentClaimException exception)
    {
        var statusCode = exception.Code switch
        {
            InstallmentRepaymentClaimErrorCodes.NotFound => StatusCodes.Status404NotFound,
            InstallmentRepaymentClaimErrorCodes.Invalid => StatusCodes.Status400BadRequest,
            InstallmentRepaymentClaimErrorCodes.PaymentMethodUnsupported => StatusCodes.Status400BadRequest,
            InstallmentRepaymentClaimErrorCodes.PermissionDenied => StatusCodes.Status403Forbidden,
            _ => StatusCodes.Status409Conflict
        };
        return new ObjectResult(ApiResult<T>.Fail(exception.Code, exception.Message))
        {
            StatusCode = statusCode
        };
    }

    private static ActionResult<ApiResult<T>> CancelClaimError<T>(InstallmentCancelClaimException exception)
    {
        var statusCode = exception.Code switch
        {
            InstallmentCancelClaimErrorCodes.NotFound => StatusCodes.Status404NotFound,
            InstallmentCancelClaimErrorCodes.Invalid => StatusCodes.Status400BadRequest,
            InstallmentCancelClaimErrorCodes.PermissionDenied => StatusCodes.Status403Forbidden,
            _ => StatusCodes.Status409Conflict
        };
        return new ObjectResult(ApiResult<T>.Fail(exception.Code, exception.Message))
        {
            StatusCode = statusCode
        };
    }
}
