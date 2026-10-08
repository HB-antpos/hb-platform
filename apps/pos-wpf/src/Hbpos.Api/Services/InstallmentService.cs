using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hbpos.Api.Data;
using Hbpos.Contracts.Installments;
using Hbpos.Contracts.Orders;
using Microsoft.Extensions.Options;
using SqlSugar;

namespace Hbpos.Api.Services;

public interface IInstallmentService
{
    Task<InstallmentCreateResponse> CreateAsync(
        InstallmentCreateRequest request,
        CancellationToken cancellationToken);

    Task<InstallmentAppendPaymentResponse> AppendPaymentAsync(
        InstallmentAppendPaymentRequest request,
        CancellationToken cancellationToken);

    Task<InstallmentConfirmPickupResponse> ConfirmPickupAsync(
        InstallmentConfirmPickupRequest request,
        CancellationToken cancellationToken);

    Task<InstallmentCancelResponse> CancelAsync(
        InstallmentCancelRequest request,
        CancellationToken cancellationToken);

    Task<InstallmentVoidResponse> VoidAsync(
        InstallmentVoidRequest request,
        CancellationToken cancellationToken);

    // 默认实现抛 NotSupportedException，保证既有测试替身无需改动即可继续编译。
    Task<InstallmentAmendLinesResponse> AmendLinesAsync(
        InstallmentAmendLinesRequest request,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

public interface IInstallmentHistoryService
{
    Task<InstallmentHistoryQueryResponse> QueryAsync(
        InstallmentHistoryQueryRequest request,
        CancellationToken cancellationToken);

    Task<InstallmentDetailsDto?> GetDetailsAsync(
        Guid installmentGuid,
        CancellationToken cancellationToken);
}

public sealed class InstallmentService(
    IInstallmentRepository repository,
    IStoreVoucherReservationService reservationService,
    TimeProvider? timeProvider = null,
    ILogger<InstallmentService>? logger = null,
    IOptions<InstallmentCrossDeviceLifecycleOptions>? lifecycleOptions = null) : IInstallmentService, IInstallmentHistoryService
{
    public const decimal MinimumInstallmentTotalAmount = 50m;
    public const decimal MinimumDownPaymentAmount = 20m;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly InstallmentCrossDeviceLifecycleOptions _lifecycleOptions =
        lifecycleOptions?.Value ?? new InstallmentCrossDeviceLifecycleOptions();

    public async Task<InstallmentCreateResponse> CreateAsync(
        InstallmentCreateRequest request,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeCreateRequest(request);
        logger?.LogInformation(
            "Installment create start installmentGuid={InstallmentGuid} store={StoreCode} device={DeviceCode} total={TotalAmount} downPayment={DownPaymentAmount} method={PaymentMethod} lines={LineCount}",
            normalized.InstallmentGuid,
            normalized.StoreCode,
            normalized.DeviceCode,
            normalized.TotalAmount,
            normalized.DownPaymentAmount,
            normalized.DownPayment.Method,
            normalized.Lines.Count);
        var existing = await repository.GetDetailsAsync(normalized.InstallmentGuid, cancellationToken);
        if (existing is not null)
        {
            logger?.LogInformation(
                "Installment create skipped existing installmentGuid={InstallmentGuid} status={Status} paid={PaidAmount} balance={BalanceAmount}",
                existing.InstallmentGuid,
                existing.Status,
                existing.PaidAmount,
                existing.BalanceAmount);
            return new InstallmentCreateResponse(
                existing.InstallmentGuid,
                existing.InstallmentNumber,
                existing.Status,
                existing.PaidAmount,
                existing.BalanceAmount,
                existing,
                AlreadyExists: true,
                Message: "AlreadyExists");
        }

        ValidateDownPayment(normalized.TotalAmount, normalized.DownPaymentAmount);
        if (normalized.DownPayment.Amount != normalized.DownPaymentAmount)
        {
            throw new InvalidOperationException("Down payment amount must match the payment amount.");
        }

        await ValidateVoucherPaymentAsync(
            normalized.StoreCode,
            normalized.DownPayment.Method,
            normalized.DownPayment.Reference,
            normalized.DownPayment.ReservationToken,
            normalized.DownPayment.Amount,
            cancellationToken);

        var createdAt = normalized.CreatedAt == default
            ? _timeProvider.GetUtcNow()
            : normalized.CreatedAt.ToUniversalTime();
        var paidAmount = RoundCurrency(normalized.DownPaymentAmount);
        var balanceAmount = RoundCurrency(normalized.TotalAmount - paidAmount);
        var status = balanceAmount == 0m ? InstallmentStatus.PaidOff : InstallmentStatus.Active;
        var installmentNumber = CreateInstallmentNumber(normalized.StoreCode, normalized.InstallmentGuid);
        var details = new InstallmentDetailsDto(
            normalized.InstallmentGuid,
            installmentNumber,
            normalized.StoreCode,
            normalized.DeviceCode,
            normalized.CashierId,
            normalized.CashierName,
            normalized.CustomerName,
            normalized.CustomerPhone,
            createdAt,
            normalized.TotalAmount,
            MinimumDownPaymentAmount,
            normalized.DownPaymentAmount,
            paidAmount,
            balanceAmount,
            status,
            normalized.Lines,
            [MapPayment(normalized.DownPayment, normalized.CashierId, normalized.CashierName, normalized.DeviceCode, createdAt)],
            PickupInfo: null,
            CancellationInfo: null,
            Note: normalized.Note,
            UpdatedAt: createdAt);

        await repository.CreateAsync(details, cancellationToken);
        if (normalized.DownPayment.Method == PaymentMethodKind.Voucher)
        {
            await reservationService.ConsumeAsync(normalized.DownPayment.ReservationToken!, cancellationToken);
            logger?.LogInformation(
                "Installment create voucher reservation consumed installmentGuid={InstallmentGuid} token={ReservationToken} voucher={VoucherCode} amount={Amount}",
                normalized.InstallmentGuid,
                ShortToken(normalized.DownPayment.ReservationToken),
                normalized.DownPayment.Reference,
                normalized.DownPayment.Amount);
        }

        logger?.LogInformation(
            "Installment create completed installmentGuid={InstallmentGuid} number={InstallmentNumber} status={Status} paid={PaidAmount} balance={BalanceAmount}",
            details.InstallmentGuid,
            details.InstallmentNumber,
            details.Status,
            details.PaidAmount,
            details.BalanceAmount);
        return new InstallmentCreateResponse(
            details.InstallmentGuid,
            details.InstallmentNumber,
            details.Status,
            details.PaidAmount,
            details.BalanceAmount,
            details);
    }

    public async Task<InstallmentAppendPaymentResponse> AppendPaymentAsync(
        InstallmentAppendPaymentRequest request,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizePaymentRequest(request);
        logger?.LogInformation(
            "Installment append payment start installmentGuid={InstallmentGuid} paymentGuid={PaymentGuid} store={StoreCode} device={DeviceCode} amount={Amount} method={PaymentMethod} idempotencyKeyPresent={IdempotencyKeyPresent}",
            normalized.InstallmentGuid,
            normalized.PaymentGuid,
            normalized.StoreCode,
            normalized.DeviceCode,
            normalized.Amount,
            normalized.Method,
            !string.IsNullOrWhiteSpace(normalized.IdempotencyKey));
        var existingPayment = await repository.FindPaymentAsync(normalized.PaymentGuid, cancellationToken);
        if (existingPayment is null && !string.IsNullOrWhiteSpace(normalized.IdempotencyKey))
        {
            // 幂等键只在当前分期单内复用，避免同店同设备的其他分期单误命中。
            existingPayment = await repository.FindPaymentByIdempotencyKeyAsync(
                normalized.InstallmentGuid,
                normalized.IdempotencyKey,
                cancellationToken);
        }

        if (existingPayment is not null)
        {
            var existingDetails = await repository.GetDetailsAsync(existingPayment.InstallmentGuid, cancellationToken)
                ?? throw new InvalidOperationException("Installment was not found.");
            ValidateInstallmentScope(existingDetails, normalized.StoreCode, normalized.DeviceCode);
            logger?.LogInformation(
                "Installment append payment already recorded installmentGuid={InstallmentGuid} paymentGuid={PaymentGuid} paid={PaidAmount} balance={BalanceAmount}",
                existingPayment.InstallmentGuid,
                existingPayment.Payment.PaymentGuid,
                existingDetails.PaidAmount,
                existingDetails.BalanceAmount);
            return new InstallmentAppendPaymentResponse(
                existingPayment.InstallmentGuid,
                existingPayment.Payment.PaymentGuid,
                existingDetails.PaidAmount,
                existingDetails.BalanceAmount,
                existingDetails.Status,
                existingDetails,
                AlreadyRecorded: true,
                Message: "AlreadyRecorded");
        }

        var details = await repository.GetDetailsAsync(normalized.InstallmentGuid, cancellationToken)
            ?? throw new InvalidOperationException("Installment was not found.");
        ValidateInstallmentScope(details, normalized.StoreCode, normalized.DeviceCode);
        if (details.Status == InstallmentStatus.PickedUp)
        {
            throw new InvalidOperationException("Picked up installment cannot accept payments.");
        }

        if (details.Status == InstallmentStatus.Cancelled)
        {
            throw new InvalidOperationException("Cancelled installment cannot accept payments.");
        }

        if (details.BalanceAmount <= 0m)
        {
            throw new InvalidOperationException("Installment is already paid off.");
        }

        if (normalized.Amount <= 0m)
        {
            throw new InvalidOperationException("Payment amount must be greater than zero.");
        }

        if (normalized.Method != PaymentMethodKind.Cash && normalized.Amount > details.BalanceAmount)
        {
            throw new InvalidOperationException("Non-cash payment cannot exceed the balance amount.");
        }

        var appliedAmount = RoundCurrency(Math.Min(normalized.Amount, details.BalanceAmount));
        await ValidateVoucherPaymentAsync(
            details.StoreCode,
            normalized.Method,
            normalized.Reference,
            normalized.ReservationToken,
            appliedAmount,
            cancellationToken);

        var recordedAt = _timeProvider.GetUtcNow();
        var payment = new InstallmentPaymentDto(
            normalized.PaymentGuid,
            normalized.Method,
            appliedAmount,
            normalized.Reference,
            InstallmentPaymentStatus.Recorded,
            recordedAt,
            normalized.CashierId,
            normalized.DeviceCode,
            normalized.CardTransactions,
            normalized.IdempotencyKey,
            normalized.ReservationToken,
            normalized.CashierName);

        var updated = await repository.AppendPaymentAsync(
            details.InstallmentGuid,
            payment,
            cancellationToken);
        if (normalized.Method == PaymentMethodKind.Voucher)
        {
            await reservationService.ConsumeAsync(normalized.ReservationToken!, cancellationToken);
            logger?.LogInformation(
                "Installment append payment voucher reservation consumed installmentGuid={InstallmentGuid} paymentGuid={PaymentGuid} token={ReservationToken} voucher={VoucherCode} amount={Amount}",
                details.InstallmentGuid,
                payment.PaymentGuid,
                ShortToken(normalized.ReservationToken),
                normalized.Reference,
                appliedAmount);
        }

        logger?.LogInformation(
            "Installment append payment completed installmentGuid={InstallmentGuid} paymentGuid={PaymentGuid} status={Status} paid={PaidAmount} balance={BalanceAmount}",
            updated.InstallmentGuid,
            payment.PaymentGuid,
            updated.Status,
            updated.PaidAmount,
            updated.BalanceAmount);
        return new InstallmentAppendPaymentResponse(
            updated.InstallmentGuid,
            payment.PaymentGuid,
            updated.PaidAmount,
            updated.BalanceAmount,
            updated.Status,
            updated);
    }

    public async Task<InstallmentConfirmPickupResponse> ConfirmPickupAsync(
        InstallmentConfirmPickupRequest request,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizePickupRequest(request);
        var details = await repository.GetDetailsAsync(normalized.InstallmentGuid, cancellationToken)
            ?? throw new InvalidOperationException("Installment was not found.");
        ValidateInstallmentStoreScope(details, normalized.StoreCode);
        var crossDevice = !string.Equals(details.DeviceCode, normalized.DeviceCode, StringComparison.OrdinalIgnoreCase);
        var alreadyConfirmed = details.Status == InstallmentStatus.PickedUp && details.PickupInfo is not null;
        var operation = CreateLifecycleOperation(
            action: "pickup",
            details,
            normalized.StoreCode,
            normalized.DeviceCode,
            normalized.CashierId,
            normalized.OperationGuid,
            normalized.IdempotencyKey,
            normalized.ConfirmedAt,
            normalized.Note,
            crossDevice,
            _lifecycleOptions.PickupEnabled,
            allowDisabledCrossDeviceReplay: alreadyConfirmed);
        if (alreadyConfirmed && operation is null)
        {
            return new InstallmentConfirmPickupResponse(
                details.InstallmentGuid,
                details.Status,
                details.PickupInfo!.PickedUpAt,
                details,
                AlreadyConfirmed: true);
        }

        if (!alreadyConfirmed && (details.Status != InstallmentStatus.PaidOff || details.BalanceAmount != 0m))
        {
            throw new InvalidOperationException("Installment must be paid off before pickup.");
        }

        var confirmedAt = normalized.ConfirmedAt == default
            ? _timeProvider.GetUtcNow()
            : normalized.ConfirmedAt.ToUniversalTime();
        var updated = operation is null
            ? await repository.ConfirmPickupAsync(
                normalized.InstallmentGuid,
                confirmedAt,
                normalized.CashierName,
                normalized.Note,
                cancellationToken)
            : await repository.ConfirmPickupIdempotentAsync(
                normalized.InstallmentGuid,
                confirmedAt,
                normalized.CashierName,
                normalized.Note,
                operation,
                cancellationToken);

        return new InstallmentConfirmPickupResponse(
            updated.InstallmentGuid,
            updated.Status,
            updated.PickupInfo!.PickedUpAt,
            updated,
            AlreadyConfirmed: alreadyConfirmed);
    }

    public async Task<InstallmentCancelResponse> CancelAsync(
        InstallmentCancelRequest request,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeCancelRequest(request);
        var details = await repository.GetDetailsAsync(normalized.InstallmentGuid, cancellationToken)
            ?? throw new InvalidOperationException("Installment was not found.");
        ValidateInstallmentScope(details, normalized.StoreCode, normalized.DeviceCode);
        logger?.LogInformation(
            "Installment cancel start installmentGuid={InstallmentGuid} store={StoreCode} device={DeviceCode} refundCount={RefundCount} paid={PaidAmount} balance={BalanceAmount}",
            normalized.InstallmentGuid,
            normalized.StoreCode,
            normalized.DeviceCode,
            normalized.Refunds.Count,
            details.PaidAmount,
            details.BalanceAmount);
        if (TryCreateExistingCancellationResponse(details, InstallmentCancellationKind.RefundCancel, out var existing))
        {
            logger?.LogInformation(
                "Installment cancel skipped existing installmentGuid={InstallmentGuid} status={Status}",
                details.InstallmentGuid,
                details.Status);
            return new InstallmentCancelResponse(details.InstallmentGuid, details.Status, details, AlreadyCancelled: true, existing);
        }

        ValidateRefundCancellable(details);
        var refunds = NormalizeAndValidateRefunds(details, normalized);
        var cancelledAt = normalized.CancelledAt == default
            ? _timeProvider.GetUtcNow()
            : normalized.CancelledAt.ToUniversalTime();
        var cancellationInfo = new InstallmentCancellationInfoDto(
            InstallmentCancellationKind.RefundCancel,
            cancelledAt,
            normalized.CashierName,
            normalized.Reason,
            normalized.IdempotencyKey);
        var updated = await repository.CancelWithRefundAsync(
            normalized.InstallmentGuid,
            refunds.Select(refund => MapRefundPayment(refund, normalized.CashierId, normalized.CashierName, normalized.DeviceCode, cancelledAt)).ToList(),
            cancellationInfo,
            cancellationToken);
        logger?.LogInformation(
            "Installment cancel completed installmentGuid={InstallmentGuid} status={Status} refundCount={RefundCount}",
            updated.InstallmentGuid,
            updated.Status,
            refunds.Count);
        return new InstallmentCancelResponse(updated.InstallmentGuid, updated.Status, updated);
    }

    public async Task<InstallmentVoidResponse> VoidAsync(
        InstallmentVoidRequest request,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeVoidRequest(request);
        var details = await repository.GetDetailsAsync(normalized.InstallmentGuid, cancellationToken)
            ?? throw new InvalidOperationException("Installment was not found.");
        ValidateInstallmentStoreScope(details, normalized.StoreCode);
        var crossDevice = !string.Equals(details.DeviceCode, normalized.DeviceCode, StringComparison.OrdinalIgnoreCase);
        var alreadyVoided = TryCreateExistingCancellationResponse(
            details,
            InstallmentCancellationKind.VoidCancel,
            out var existing);
        var operation = CreateLifecycleOperation(
            action: "void",
            details,
            normalized.StoreCode,
            normalized.DeviceCode,
            normalized.CashierId,
            normalized.OperationGuid,
            normalized.IdempotencyKey,
            normalized.VoidedAt,
            normalized.Reason,
            crossDevice,
            _lifecycleOptions.VoidEnabled,
            allowDisabledCrossDeviceReplay: alreadyVoided);
        logger?.LogInformation(
            "Installment void start installmentGuid={InstallmentGuid} store={StoreCode} device={DeviceCode} paid={PaidAmount} balance={BalanceAmount}",
            normalized.InstallmentGuid,
            normalized.StoreCode,
            normalized.DeviceCode,
            details.PaidAmount,
            details.BalanceAmount);
        if (alreadyVoided && operation is null)
        {
            logger?.LogInformation(
                "Installment void skipped existing installmentGuid={InstallmentGuid} status={Status}",
                details.InstallmentGuid,
                details.Status);
            return new InstallmentVoidResponse(details.InstallmentGuid, details.Status, details, AlreadyVoided: true, existing);
        }

        if (!alreadyVoided)
        {
            ValidateCancellable(details);
        }
        var voidedAt = normalized.VoidedAt == default
            ? _timeProvider.GetUtcNow()
            : normalized.VoidedAt.ToUniversalTime();
        var cancellationInfo = new InstallmentCancellationInfoDto(
            InstallmentCancellationKind.VoidCancel,
            voidedAt,
            normalized.CashierName,
            normalized.Reason,
            normalized.IdempotencyKey);
        var updated = operation is null
            ? await repository.VoidAsync(
                normalized.InstallmentGuid,
                cancellationInfo,
                cancellationToken)
            : await repository.VoidIdempotentAsync(
                normalized.InstallmentGuid,
                cancellationInfo,
                operation,
                cancellationToken);
        logger?.LogInformation(
            "Installment void completed installmentGuid={InstallmentGuid} status={Status}",
            updated.InstallmentGuid,
            updated.Status);
        return new InstallmentVoidResponse(
            updated.InstallmentGuid,
            updated.Status,
            updated,
            AlreadyVoided: alreadyVoided,
            Message: alreadyVoided ? existing : null);
    }

    public async Task<InstallmentAmendLinesResponse> AmendLinesAsync(
        InstallmentAmendLinesRequest request,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeAmendLinesRequest(request);
        var details = await repository.GetDetailsAsync(normalized.InstallmentGuid, cancellationToken)
            ?? throw new InvalidOperationException("Installment was not found.");
        // 修改商品列表按“本店”范围放行（与历史页、跨设备提货 / 作废一致），不要求同一台设备。
        ValidateInstallmentStoreScope(details, normalized.StoreCode);
        logger?.LogInformation(
            "Installment amend lines start installmentGuid={InstallmentGuid} store={StoreCode} device={DeviceCode} cashier={CashierId} lines={LineCount} reason={Reason}",
            normalized.InstallmentGuid,
            normalized.StoreCode,
            normalized.DeviceCode,
            normalized.CashierId,
            normalized.Lines.Count,
            normalized.Reason);

        // 状态、乐观并发令牌、新总额与已付款的比较都依赖加锁后的最新订单，统一在仓储事务内完成。
        var updated = await repository.AmendLinesAsync(
            normalized.InstallmentGuid,
            normalized.Lines,
            normalized.ExpectedUpdatedAt,
            cancellationToken);
        logger?.LogInformation(
            "Installment amend lines completed installmentGuid={InstallmentGuid} status={Status} total={TotalAmount} paid={PaidAmount} balance={BalanceAmount}",
            updated.InstallmentGuid,
            updated.Status,
            updated.TotalAmount,
            updated.PaidAmount,
            updated.BalanceAmount);
        return new InstallmentAmendLinesResponse(
            updated.InstallmentGuid,
            updated.Status,
            updated.TotalAmount,
            updated.PaidAmount,
            updated.BalanceAmount,
            updated);
    }

    public Task<InstallmentHistoryQueryResponse> QueryAsync(
        InstallmentHistoryQueryRequest request,
        CancellationToken cancellationToken)
    {
        var normalized = request with
        {
            StoreCode = NormalizeRequired(request.StoreCode, "Store code is required."),
            DeviceCode = NormalizeOptional(request.DeviceCode),
            Keyword = NormalizeOptional(request.Keyword),
            Take = Math.Clamp(request.Take, 1, 200),
            Skip = Math.Max(request.Skip, 0)
        };
        return repository.QueryAsync(normalized, cancellationToken);
    }

    public Task<InstallmentDetailsDto?> GetDetailsAsync(
        Guid installmentGuid,
        CancellationToken cancellationToken)
    {
        return repository.GetDetailsAsync(installmentGuid, cancellationToken);
    }

    private static InstallmentCreateRequest NormalizeCreateRequest(InstallmentCreateRequest request)
    {
        if (request.Lines.Count == 0)
        {
            throw new InvalidOperationException("Installment lines are required.");
        }

        var lines = request.Lines.Select(NormalizeLine).ToList();
        var normalizedTotal = RoundCurrency(request.TotalAmount);
        var lineTotal = RoundCurrency(lines.Sum(line => line.ActualAmount));
        if (lineTotal != normalizedTotal)
        {
            throw new InvalidOperationException("Installment line total must match total amount.");
        }

        return request with
        {
            StoreCode = NormalizeRequired(request.StoreCode, "Store code is required."),
            DeviceCode = NormalizeRequired(request.DeviceCode, "Device code is required."),
            CashierId = NormalizeRequired(request.CashierId, "Cashier id is required."),
            CashierName = NormalizeRequired(request.CashierName, "Cashier name is required."),
            CustomerName = NormalizeRequired(request.CustomerName, "Customer name is required."),
            CustomerPhone = NormalizeRequired(request.CustomerPhone, "Customer phone is required."),
            Note = NormalizeOptional(request.Note),
            TotalAmount = normalizedTotal,
            DownPaymentAmount = RoundCurrency(request.DownPaymentAmount),
            Lines = lines,
            DownPayment = request.DownPayment with
            {
                Reference = NormalizeOptional(request.DownPayment.Reference),
                ReservationToken = NormalizeOptional(request.DownPayment.ReservationToken),
                IdempotencyKey = NormalizeOptional(request.DownPayment.IdempotencyKey),
                Amount = RoundCurrency(request.DownPayment.Amount)
            }
        };
    }

    private static InstallmentLineDto NormalizeLine(InstallmentLineDto line)
    {
        if (line.Quantity <= 0m)
        {
            throw new InvalidOperationException("Installment line quantity must be greater than zero.");
        }

        if (line.UnitPrice <= 0m)
        {
            throw new InvalidOperationException("Installment line unit price must be greater than zero.");
        }

        if (line.ActualAmount <= 0m)
        {
            throw new InvalidOperationException("Installment line amount must be greater than zero.");
        }

        return line with
        {
            ProductCode = NormalizeRequired(line.ProductCode, "Product code is required."),
            DisplayName = NormalizeRequired(line.DisplayName, "Display name is required."),
            LookupCode = NormalizeRequired(line.LookupCode, "Lookup code is required."),
            ReferenceCode = NormalizeOptional(line.ReferenceCode),
            ItemNumber = NormalizeOptional(line.ItemNumber),
            UnitPrice = RoundCurrency(line.UnitPrice),
            DiscountAmount = RoundCurrency(line.DiscountAmount),
            ActualAmount = RoundCurrency(line.ActualAmount)
        };
    }

    private static void ValidateInstallmentScope(
        InstallmentDetailsDto details,
        string storeCode,
        string deviceCode)
    {
        if (!string.Equals(details.StoreCode, storeCode, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Installment does not belong to this store.");
        }

        if (!string.Equals(details.DeviceCode, deviceCode, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Installment does not belong to this device.");
        }
    }

    private static void ValidateInstallmentStoreScope(InstallmentDetailsDto details, string storeCode)
    {
        if (!string.Equals(details.StoreCode, storeCode, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Installment does not belong to this store.");
        }
    }

    private static InstallmentLifecycleOperationFacts? CreateLifecycleOperation(
        string action,
        InstallmentDetailsDto details,
        string storeCode,
        string executingDeviceCode,
        string cashierId,
        Guid operationGuid,
        string? idempotencyKey,
        DateTimeOffset requestedAt,
        string? note,
        bool crossDevice,
        bool crossDeviceEnabled,
        bool allowDisabledCrossDeviceReplay)
    {
        var hasOperationGuid = operationGuid != Guid.Empty;
        var hasIdempotencyKey = !string.IsNullOrWhiteSpace(idempotencyKey);
        if (hasOperationGuid != hasIdempotencyKey)
        {
            // 旧版同机作废客户端只发送 idempotencyKey；继续走原有非生命周期操作路径，
            // 跨设备请求及仅发送 operationGuid 的请求仍必须提供完整操作身份。
            if (string.Equals(action, "void", StringComparison.Ordinal) &&
                !crossDevice &&
                !hasOperationGuid &&
                hasIdempotencyKey)
            {
                return null;
            }

            throw new InvalidOperationException("Lifecycle operationGuid and idempotencyKey must be provided together.");
        }

        if (crossDevice && !crossDeviceEnabled && !allowDisabledCrossDeviceReplay)
        {
            throw new InvalidOperationException($"Installment cross-device {action} is disabled.");
        }

        if (crossDevice && !hasOperationGuid)
        {
            throw new InvalidOperationException($"Installment cross-device {action} requires operation identity.");
        }

        if (!hasOperationGuid)
        {
            // 旧同机客户端不带新字段时继续走原有路径；跨设备始终要求完整操作身份。
            return null;
        }

        var normalizedIdempotencyKey = idempotencyKey!.Trim();
        var canonical = string.Join(
            '\n',
            action.ToLowerInvariant(),
            details.InstallmentGuid.ToString("D"),
            details.StoreCode.ToUpperInvariant(),
            details.DeviceCode.ToUpperInvariant(),
            storeCode.ToUpperInvariant(),
            executingDeviceCode.ToUpperInvariant(),
            operationGuid.ToString("D"),
            normalizedIdempotencyKey,
            note ?? string.Empty);
        var fingerprint = $"sha256:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant()}";
        return new InstallmentLifecycleOperationFacts(
            operationGuid,
            normalizedIdempotencyKey,
            fingerprint,
            executingDeviceCode,
            cashierId);
    }

    private static InstallmentAppendPaymentRequest NormalizePaymentRequest(InstallmentAppendPaymentRequest request)
    {
        return request with
        {
            StoreCode = NormalizeRequired(request.StoreCode, "Store code is required."),
            DeviceCode = NormalizeRequired(request.DeviceCode, "Device code is required."),
            CashierId = NormalizeRequired(request.CashierId, "Cashier id is required."),
            CashierName = NormalizeRequired(request.CashierName, "Cashier name is required."),
            Reference = NormalizeOptional(request.Reference),
            ReservationToken = NormalizeOptional(request.ReservationToken),
            IdempotencyKey = NormalizeOptional(request.IdempotencyKey),
            Amount = RoundCurrency(request.Amount)
        };
    }

    private static InstallmentAmendLinesRequest NormalizeAmendLinesRequest(InstallmentAmendLinesRequest request)
    {
        if (request.Lines is null || request.Lines.Any(line => line is null))
        {
            throw new InstallmentAmendLinesException(
                InstallmentAmendLinesErrorCodes.InvalidLines,
                "Installment lines are required.");
        }

        var lines = request.Lines.Select(line => line with
        {
            ProductCode = line.ProductCode?.Trim() ?? string.Empty,
            DisplayName = line.DisplayName?.Trim() ?? string.Empty,
            LookupCode = line.LookupCode?.Trim() ?? string.Empty,
            ReferenceCode = NormalizeOptional(line.ReferenceCode),
            ItemNumber = NormalizeOptional(line.ItemNumber),
            UnitPrice = RoundCurrency(line.UnitPrice),
            DiscountAmount = RoundCurrency(line.DiscountAmount),
            ActualAmount = RoundCurrency(line.ActualAmount)
        }).ToList();

        // 共享规则覆盖数量 / 单价 / 折扣 / 行金额 / 重复 GUID / 必填文本，客户端与服务端口径一致。
        var validation = InstallmentAmendRules.ValidateLines(lines);
        if (validation != InstallmentAmendLinesValidation.Valid)
        {
            throw new InstallmentAmendLinesException(
                InstallmentAmendLinesErrorCodes.InvalidLines,
                $"Installment lines are invalid: {validation}.");
        }

        // 共享规则不管列长度；超长会在落库时被 SQL Server 截断报错，这里提前按 400 拒绝。
        foreach (var line in lines)
        {
            if (line.ProductCode.Length > 50 ||
                line.LookupCode.Length > 50 ||
                (line.ReferenceCode?.Length ?? 0) > 50 ||
                (line.ItemNumber?.Length ?? 0) > 50 ||
                line.DisplayName.Length > 255)
            {
                throw new InstallmentAmendLinesException(
                    InstallmentAmendLinesErrorCodes.InvalidLines,
                    "Installment line text exceeds the maximum length.");
            }
        }

        return request with
        {
            StoreCode = NormalizeRequired(request.StoreCode, "Store code is required."),
            DeviceCode = NormalizeRequired(request.DeviceCode, "Device code is required."),
            CashierId = NormalizeRequired(request.CashierId, "Cashier id is required."),
            CashierName = NormalizeRequired(request.CashierName, "Cashier name is required."),
            Reason = NormalizeOptional(request.Reason),
            Lines = lines
        };
    }

    private static InstallmentConfirmPickupRequest NormalizePickupRequest(InstallmentConfirmPickupRequest request)
    {
        return request with
        {
            StoreCode = NormalizeRequired(request.StoreCode, "Store code is required."),
            DeviceCode = NormalizeRequired(request.DeviceCode, "Device code is required."),
            CashierId = NormalizeRequired(request.CashierId, "Cashier id is required."),
            CashierName = NormalizeRequired(request.CashierName, "Cashier name is required."),
            Note = NormalizeOptional(request.Note),
            IdempotencyKey = NormalizeOptional(request.IdempotencyKey)
        };
    }

    private static InstallmentCancelRequest NormalizeCancelRequest(InstallmentCancelRequest request)
    {
        return request with
        {
            StoreCode = NormalizeRequired(request.StoreCode, "Store code is required."),
            DeviceCode = NormalizeRequired(request.DeviceCode, "Device code is required."),
            CashierId = NormalizeRequired(request.CashierId, "Cashier id is required."),
            CashierName = NormalizeRequired(request.CashierName, "Cashier name is required."),
            Reason = NormalizeOptional(request.Reason),
            IdempotencyKey = NormalizeOptional(request.IdempotencyKey),
            Refunds = request.Refunds.Select(NormalizeRefund).ToList()
        };
    }

    private static InstallmentVoidRequest NormalizeVoidRequest(InstallmentVoidRequest request)
    {
        return request with
        {
            StoreCode = NormalizeRequired(request.StoreCode, "Store code is required."),
            DeviceCode = NormalizeRequired(request.DeviceCode, "Device code is required."),
            CashierId = NormalizeRequired(request.CashierId, "Cashier id is required."),
            CashierName = NormalizeRequired(request.CashierName, "Cashier name is required."),
            Reason = NormalizeOptional(request.Reason),
            IdempotencyKey = NormalizeOptional(request.IdempotencyKey)
        };
    }

    private static InstallmentRefundPaymentCommandDto NormalizeRefund(InstallmentRefundPaymentCommandDto refund)
    {
        if (refund.Amount <= 0m)
        {
            throw new InvalidOperationException("Refund amount must be greater than zero.");
        }

        return refund with
        {
            Amount = RoundCurrency(refund.Amount),
            Reference = NormalizeOptional(refund.Reference),
            IdempotencyKey = NormalizeOptional(refund.IdempotencyKey)
        };
    }

    private static bool TryCreateExistingCancellationResponse(
        InstallmentDetailsDto details,
        InstallmentCancellationKind expectedKind,
        out string? message)
    {
        message = null;
        if (details.Status != InstallmentStatus.Cancelled)
        {
            return false;
        }

        if (details.CancellationInfo?.Kind == expectedKind)
        {
            message = expectedKind == InstallmentCancellationKind.RefundCancel ? "AlreadyCancelled" : "AlreadyVoided";
            return true;
        }

        throw new InvalidOperationException("Installment cancellation kind conflicts with the existing cancelled record.");
    }

    private static void ValidateCancellable(InstallmentDetailsDto details)
    {
        if (details.Status != InstallmentStatus.Active || details.BalanceAmount <= 0m)
        {
            throw new InvalidOperationException("Only active unpaid installments can be cancelled or voided.");
        }
    }

    private static void ValidateRefundCancellable(InstallmentDetailsDto details)
    {
        // 取消退款额外允许"已付清未提货"；作废不退款仍走 ValidateCancellable，只限进行中单。
        if (!InstallmentLifecycleRules.CanCancelWithRefund(details.Status, details.BalanceAmount))
        {
            throw new InvalidOperationException("Only active or paid-off installments that have not been picked up can be cancelled with refund.");
        }
    }

    internal static IReadOnlyList<InstallmentRefundPaymentCommandDto> NormalizeAndValidateRefunds(
        InstallmentDetailsDto details,
        InstallmentCancelRequest request)
    {
        var refunds = request.Refunds.Select(NormalizeRefund).ToArray();
        if (refunds.Length == 0)
        {
            throw new InvalidOperationException("Refund payments are required when cancelling an installment.");
        }

        var paidByMethod = details.Payments
            .Where(payment => payment.Status == InstallmentPaymentStatus.Recorded && payment.Amount > 0m)
            .GroupBy(payment => payment.Method)
            .ToDictionary(group => group.Key, group => RoundCurrency(group.Sum(payment => payment.Amount)));
        var refundByMethod = refunds
            .GroupBy(refund => refund.Method)
            .ToDictionary(group => group.Key, group => RoundCurrency(group.Sum(refund => refund.Amount)));
        var refundMode = InstallmentLifecycleRules.NormalizeRefundMode(request.RefundMode);
        if (!InstallmentLifecycleRules.RefundTotalsMatch(paidByMethod, refundByMethod, refundMode))
        {
            throw new InvalidOperationException(refundMode == InstallmentCancelRefundMode.Voucher
                ? "Voucher refund must cover the total of all recorded installment payments."
                : "Refund payments must cover all recorded installment payments by method.");
        }

        return refunds;
    }

    private async Task ValidateVoucherPaymentAsync(
        string storeCode,
        PaymentMethodKind method,
        string? reference,
        string? reservationToken,
        decimal amount,
        CancellationToken cancellationToken)
    {
        if (method != PaymentMethodKind.Voucher)
        {
            return;
        }

        var voucherCode = NormalizeRequired(reference, "Voucher payment reference is required.");
        var token = NormalizeRequired(reservationToken, "Voucher reservation token is required.");
        var reservation = await reservationService.GetAsync(token, cancellationToken)
            ?? throw new InvalidOperationException("Voucher reservation token is invalid or expired.");
        if (!string.Equals(reservation.StoreCode, storeCode, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Voucher reservation store does not match the installment store.");
        }

        if (!string.Equals(reservation.VoucherCode, voucherCode, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Voucher reservation does not match the voucher code.");
        }

        if (reservation.LockedAmount < amount)
        {
            throw new InvalidOperationException("Voucher payment amount exceeds the locked amount.");
        }
    }

    private static void ValidateDownPayment(decimal totalAmount, decimal downPaymentAmount)
    {
        if (totalAmount <= 0m)
        {
            throw new InvalidOperationException("Total amount must be greater than zero.");
        }

        if (totalAmount < MinimumInstallmentTotalAmount)
        {
            // 中文说明：API 层保留分期总额兜底，防止客户端校验被绕过后创建小额分期单。
            throw new InvalidOperationException("Installment order total must be at least $50.");
        }

        if (downPaymentAmount <= 0m)
        {
            throw new InvalidOperationException("Down payment amount must be greater than zero.");
        }

        if (downPaymentAmount > totalAmount)
        {
            throw new InvalidOperationException("Down payment amount cannot exceed total amount.");
        }

        if (downPaymentAmount < MinimumDownPaymentAmount)
        {
            throw new InvalidOperationException("Down payment amount must be at least $20.");
        }
    }

    private static InstallmentPaymentDto MapPayment(
        InstallmentPaymentCommandDto payment,
        string cashierId,
        string cashierName,
        string deviceCode,
        DateTimeOffset recordedAt)
    {
        return new InstallmentPaymentDto(
            payment.PaymentGuid,
            payment.Method,
            payment.Amount,
            payment.Reference,
            InstallmentPaymentStatus.Recorded,
            recordedAt,
            cashierId,
            deviceCode,
            payment.CardTransactions,
            payment.IdempotencyKey,
            payment.ReservationToken,
            cashierName);
    }

    internal static InstallmentPaymentDto MapRefundPayment(
        InstallmentRefundPaymentCommandDto payment,
        string cashierId,
        string cashierName,
        string deviceCode,
        DateTimeOffset recordedAt)
    {
        return new InstallmentPaymentDto(
            payment.PaymentGuid,
            payment.Method,
            -payment.Amount,
            payment.Reference,
            InstallmentPaymentStatus.Recorded,
            recordedAt,
            cashierId,
            deviceCode,
            payment.CardTransactions,
            payment.IdempotencyKey,
            ReservationToken: null,
            CashierName: cashierName);
    }

    private static string CreateInstallmentNumber(string storeCode, Guid installmentGuid)
    {
        return $"IP-{storeCode}-{installmentGuid:N}"[..Math.Min(40, $"IP-{storeCode}-{installmentGuid:N}".Length)].ToUpperInvariant();
    }

    private static decimal RoundCurrency(decimal amount) => decimal.Round(amount, 2, MidpointRounding.AwayFromZero);

    private static string NormalizeRequired(string? value, string message)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(message);
        }

        return value.Trim();
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string ShortToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return string.Empty;
        }

        var normalized = token.Trim();
        return normalized.Length <= 8 ? normalized : normalized[..8];
    }
}

public sealed record InstallmentLifecycleOperationFacts(
    Guid OperationGuid,
    string IdempotencyKey,
    string Fingerprint,
    string ExecutingDeviceCode,
    string CashierId);

/// <summary>
/// 修改分期商品列表的业务失败；Code 取自 <see cref="InstallmentAmendLinesErrorCodes"/>，控制器据此映射 HTTP 状态。
/// 继承 InvalidOperationException，未专门捕获的调用方仍按通用 400 处理。
/// </summary>
public sealed class InstallmentAmendLinesException(string code, string message)
    : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

public interface IInstallmentRepository
{
    Task CreateAsync(InstallmentDetailsDto details, CancellationToken cancellationToken);

    Task<InstallmentDetailsDto> AppendPaymentAsync(
        Guid installmentGuid,
        InstallmentPaymentDto payment,
        CancellationToken cancellationToken);

    Task<InstallmentDetailsDto> ConfirmPickupAsync(
        Guid installmentGuid,
        DateTimeOffset pickedUpAt,
        string pickedUpBy,
        string? note,
        CancellationToken cancellationToken);

    Task<InstallmentDetailsDto> ConfirmPickupIdempotentAsync(
        Guid installmentGuid,
        DateTimeOffset pickedUpAt,
        string pickedUpBy,
        string? note,
        InstallmentLifecycleOperationFacts operation,
        CancellationToken cancellationToken) =>
        ConfirmPickupAsync(installmentGuid, pickedUpAt, pickedUpBy, note, cancellationToken);

    Task<InstallmentDetailsDto> CancelWithRefundAsync(
        Guid installmentGuid,
        IReadOnlyList<InstallmentPaymentDto> refunds,
        InstallmentCancellationInfoDto cancellationInfo,
        CancellationToken cancellationToken);

    Task<InstallmentDetailsDto> VoidAsync(
        Guid installmentGuid,
        InstallmentCancellationInfoDto cancellationInfo,
        CancellationToken cancellationToken);

    Task<InstallmentDetailsDto> VoidIdempotentAsync(
        Guid installmentGuid,
        InstallmentCancellationInfoDto cancellationInfo,
        InstallmentLifecycleOperationFacts operation,
        CancellationToken cancellationToken) =>
        VoidAsync(installmentGuid, cancellationInfo, cancellationToken);

    // 整体替换商品行；默认抛 NotSupportedException，保证既有测试替身无需改动即可继续编译。
    Task<InstallmentDetailsDto> AmendLinesAsync(
        Guid installmentGuid,
        IReadOnlyList<InstallmentLineDto> lines,
        DateTimeOffset expectedUpdatedAt,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    Task<InstallmentPaymentLookup?> FindPaymentAsync(
        Guid paymentGuid,
        CancellationToken cancellationToken);

    Task<InstallmentPaymentLookup?> FindPaymentByIdempotencyKeyAsync(
        Guid installmentGuid,
        string idempotencyKey,
        CancellationToken cancellationToken);

    Task<InstallmentHistoryQueryResponse> QueryAsync(
        InstallmentHistoryQueryRequest request,
        CancellationToken cancellationToken);

    Task<InstallmentDetailsDto?> GetDetailsAsync(
        Guid installmentGuid,
        CancellationToken cancellationToken);
}

public sealed record InstallmentPaymentLookup(Guid InstallmentGuid, InstallmentPaymentDto Payment);

public sealed class SqlSugarInstallmentRepository(HbposSqlSugarContext dbContext) : IInstallmentRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task CreateAsync(InstallmentDetailsDto details, CancellationToken cancellationToken)
    {
        var db = dbContext.PosmDb;
        await db.Ado.BeginTranAsync();
        try
        {
            await db.Insertable(MapOrder(details)).ExecuteCommandAsync(cancellationToken);
            await db.Insertable(details.Lines.Select(line => MapLine(details.InstallmentGuid, line)).ToList())
                .ExecuteCommandAsync(cancellationToken);
            foreach (var payment in details.Payments.Where(payment => payment.Method == PaymentMethodKind.Voucher))
            {
                // 分期首付用券必须在同一事务里先占用 reservation，再扣减券余额。
                await SqlSugarStoreVoucherReservationService.ClaimInsideTransactionAsync(
                    db,
                    payment.ReservationToken ?? string.Empty,
                    details.StoreCode,
                    payment.Reference ?? string.Empty,
                    payment.Amount,
                    details.InstallmentGuid.ToString("D"),
                    payment.RecordedAt,
                    cancellationToken);
                await SqlSugarStoreVoucherRepository.RedeemInsideTransactionAsync(
                    db,
                    details.StoreCode,
                    payment.Reference ?? string.Empty,
                    payment.Amount,
                    details.CashierId,
                    cancellationToken);
            }

            await db.Insertable(details.Payments.Select(payment => MapPayment(details.InstallmentGuid, payment)).ToList())
                .ExecuteCommandAsync(cancellationToken);
            await db.Ado.CommitTranAsync();
        }
        catch
        {
            await db.Ado.RollbackTranAsync();
            throw;
        }
    }

    public async Task<InstallmentDetailsDto> AppendPaymentAsync(
        Guid installmentGuid,
        InstallmentPaymentDto payment,
        CancellationToken cancellationToken)
    {
        var db = dbContext.PosmDb;
        await using var processLock = await InstallmentMutationLock.AcquireProcessAsync(
            installmentGuid,
            cancellationToken);
        var installmentGuidText = installmentGuid.ToString("D");
        var paymentGuidText = payment.PaymentGuid.ToString("D");
        await db.Ado.BeginTranAsync(System.Data.IsolationLevel.Serializable);
        try
        {
            await InstallmentMutationLock.AcquireDatabaseAsync(db, installmentGuid);
            var lockedOrder = await InstallmentMutationLock.LockOrderAsync(db, installmentGuid, cancellationToken)
                ?? throw new InvalidOperationException("Installment was not found.");
            await InstallmentMutationLock.EnsureNoBlockingClaimAsync(db, installmentGuid, cancellationToken);
            if (lockedOrder.Status is (int)InstallmentStatus.PickedUp or (int)InstallmentStatus.Cancelled ||
                lockedOrder.BalanceAmount <= 0m)
            {
                throw new InvalidOperationException("Installment cannot accept another payment.");
            }

            if (payment.Method != PaymentMethodKind.Cash && payment.Amount > lockedOrder.BalanceAmount)
            {
                throw new InvalidOperationException("Non-cash payment cannot exceed the current balance amount.");
            }

            var paymentToRecord = payment.Method == PaymentMethodKind.Cash
                ? payment with { Amount = RoundCurrency(Math.Min(payment.Amount, lockedOrder.BalanceAmount)) }
                : payment;
            var current = await GetDetailsInsideTransactionAsync(db, installmentGuid, cancellationToken)
                ?? throw new InvalidOperationException("Installment was not found.");
            var existingPayment = await db.Queryable<InstallmentPaymentEntity>()
                .AnyAsync(x => x.PaymentGuid == paymentGuidText, cancellationToken);
            if (!existingPayment)
            {
                if (paymentToRecord.Method == PaymentMethodKind.Voucher)
                {
                    // 补款用券同样通过 reservation claim 做一次性闸门。
                    await SqlSugarStoreVoucherReservationService.ClaimInsideTransactionAsync(
                        db,
                        paymentToRecord.ReservationToken ?? string.Empty,
                        current.StoreCode,
                        paymentToRecord.Reference ?? string.Empty,
                        paymentToRecord.Amount,
                        paymentToRecord.PaymentGuid.ToString("D"),
                        paymentToRecord.RecordedAt,
                        cancellationToken);
                    await SqlSugarStoreVoucherRepository.RedeemInsideTransactionAsync(
                        db,
                        current.StoreCode,
                        paymentToRecord.Reference ?? string.Empty,
                        paymentToRecord.Amount,
                        paymentToRecord.CashierId,
                        cancellationToken);
                }

                await db.Insertable(MapPayment(installmentGuid, paymentToRecord)).ExecuteCommandAsync(cancellationToken);
            }

            var paidAmount = RoundCurrency(await db.Queryable<InstallmentPaymentEntity>()
                .Where(x => x.InstallmentGuid == installmentGuidText && x.Status == (int)InstallmentPaymentStatus.Recorded)
                .SumAsync(x => x.Amount));
            var balanceAmount = RoundCurrency(Math.Max(0m, current.TotalAmount - paidAmount));
            var status = balanceAmount == 0m ? InstallmentStatus.PaidOff : InstallmentStatus.Active;
            await db.Updateable<InstallmentOrderEntity>()
                .SetColumns(x => x.PaidAmount == paidAmount)
                .SetColumns(x => x.BalanceAmount == balanceAmount)
                .SetColumns(x => x.Status == (int)status)
                .SetColumns(x => x.UpdatedAt == DateTime.UtcNow)
                .Where(x => x.InstallmentGuid == installmentGuidText)
                .ExecuteCommandAsync(cancellationToken);
            await db.Ado.CommitTranAsync();
            return await GetDetailsAsync(installmentGuid, cancellationToken)
                ?? throw new InvalidOperationException("Installment was not found.");
        }
        catch
        {
            await db.Ado.RollbackTranAsync();
            throw;
        }
    }

    public async Task<InstallmentDetailsDto> AmendLinesAsync(
        Guid installmentGuid,
        IReadOnlyList<InstallmentLineDto> lines,
        DateTimeOffset expectedUpdatedAt,
        CancellationToken cancellationToken)
    {
        var db = dbContext.PosmDb;
        await using var processLock = await InstallmentMutationLock.AcquireProcessAsync(
            installmentGuid,
            cancellationToken);
        var installmentGuidText = installmentGuid.ToString("D");
        await db.Ado.BeginTranAsync(System.Data.IsolationLevel.Serializable);
        try
        {
            await InstallmentMutationLock.AcquireDatabaseAsync(db, installmentGuid);
            var lockedOrder = await InstallmentMutationLock.LockOrderAsync(db, installmentGuid, cancellationToken)
                ?? throw new InvalidOperationException("Installment was not found.");
            // 有进行中的补款 / 取消 claim 时订单金额不能变，抛 Busy（控制器映射 409）。
            await InstallmentMutationLock.EnsureNoBlockingClaimAsync(db, installmentGuid, cancellationToken);
            if (!InstallmentAmendRules.CanAmend((InstallmentStatus)lockedOrder.Status))
            {
                throw new InstallmentAmendLinesException(
                    InstallmentAmendLinesErrorCodes.StatusNotAllowed,
                    "Only active or paid-off installments can be amended.");
            }

            // 乐观并发：客户端必须基于最新的 UpdatedAt 修改；毫秒级容差吸收 JSON / 数据库精度差异。
            var storedUpdatedAt = ToDateTimeOffset(lockedOrder.UpdatedAt);
            if (Math.Abs((storedUpdatedAt - expectedUpdatedAt.ToUniversalTime()).TotalMilliseconds) > 1d)
            {
                throw new InstallmentAmendLinesException(
                    InstallmentAmendLinesErrorCodes.Stale,
                    "Installment was changed by another operation. Reload and try again.");
            }

            var newTotal = InstallmentAmendRules.CalculateTotal(lines);
            var totalValidation = InstallmentAmendRules.ValidateTotal(newTotal, lockedOrder.PaidAmount);
            if (totalValidation != InstallmentAmendLinesValidation.Valid)
            {
                throw new InstallmentAmendLinesException(
                    totalValidation == InstallmentAmendLinesValidation.TotalBelowPaid
                        ? InstallmentAmendLinesErrorCodes.TotalBelowPaid
                        : InstallmentAmendLinesErrorCodes.TotalBelowMinimum,
                    totalValidation == InstallmentAmendLinesValidation.TotalBelowPaid
                        ? "New total cannot be less than the amount already paid."
                        : "New installment total must be at least $50.");
            }

            // InstallmentLineGuid 是全局主键：本单已有的行 GUID 可沿用，属于其他分期单的必须拒绝，避免主键冲突。
            var lineGuidTexts = lines.Select(line => line.InstallmentLineGuid.ToString("D")).ToList();
            var foreignLineExists = await db.Queryable<InstallmentOrderLineEntity>()
                .AnyAsync(
                    x => lineGuidTexts.Contains(x.InstallmentLineGuid) && x.InstallmentGuid != installmentGuidText,
                    cancellationToken);
            if (foreignLineExists)
            {
                throw new InstallmentAmendLinesException(
                    InstallmentAmendLinesErrorCodes.InvalidLines,
                    "Installment line id already belongs to another installment.");
            }

            await db.Deleteable<InstallmentOrderLineEntity>()
                .Where(x => x.InstallmentGuid == installmentGuidText)
                .ExecuteCommandAsync(cancellationToken);
            await db.Insertable(lines.Select(line => MapLine(installmentGuid, line)).ToList())
                .ExecuteCommandAsync(cancellationToken);

            // 余额与状态按新总额重算：已付清的单总额上调会回到进行中，下调到恰好等于已付则保持已付清。
            var balanceAmount = InstallmentAmendRules.CalculateBalance(newTotal, lockedOrder.PaidAmount);
            var status = InstallmentAmendRules.ResolveStatus(newTotal, lockedOrder.PaidAmount);
            await db.Updateable<InstallmentOrderEntity>()
                .SetColumns(x => x.TotalAmount == newTotal)
                .SetColumns(x => x.BalanceAmount == balanceAmount)
                .SetColumns(x => x.Status == (int)status)
                .SetColumns(x => x.UpdatedAt == DateTime.UtcNow)
                .Where(x => x.InstallmentGuid == installmentGuidText)
                .ExecuteCommandAsync(cancellationToken);
            await db.Ado.CommitTranAsync();
        }
        catch
        {
            await db.Ado.RollbackTranAsync();
            throw;
        }

        return await GetDetailsAsync(installmentGuid, cancellationToken)
            ?? throw new InvalidOperationException("Installment was not found.");
    }

    public Task<InstallmentDetailsDto> ConfirmPickupAsync(
        Guid installmentGuid,
        DateTimeOffset pickedUpAt,
        string pickedUpBy,
        string? note,
        CancellationToken cancellationToken) =>
        ConfirmPickupCoreAsync(installmentGuid, pickedUpAt, pickedUpBy, note, operation: null, cancellationToken);

    public Task<InstallmentDetailsDto> ConfirmPickupIdempotentAsync(
        Guid installmentGuid,
        DateTimeOffset pickedUpAt,
        string pickedUpBy,
        string? note,
        InstallmentLifecycleOperationFacts operation,
        CancellationToken cancellationToken) =>
        ConfirmPickupCoreAsync(installmentGuid, pickedUpAt, pickedUpBy, note, operation, cancellationToken);

    private async Task<InstallmentDetailsDto> ConfirmPickupCoreAsync(
        Guid installmentGuid,
        DateTimeOffset pickedUpAt,
        string pickedUpBy,
        string? note,
        InstallmentLifecycleOperationFacts? operation,
        CancellationToken cancellationToken)
    {
        var db = dbContext.PosmDb;
        await using var processLock = await InstallmentMutationLock.AcquireProcessAsync(
            installmentGuid,
            cancellationToken);
        var installmentGuidText = installmentGuid.ToString("D");
        await db.Ado.BeginTranAsync(System.Data.IsolationLevel.Serializable);
        try
        {
            await InstallmentMutationLock.AcquireDatabaseAsync(db, installmentGuid);
            var lockedOrder = await InstallmentMutationLock.LockOrderAsync(db, installmentGuid, cancellationToken)
                ?? throw new InvalidOperationException("Installment was not found.");
            await InstallmentMutationLock.EnsureNoBlockingClaimAsync(db, installmentGuid, cancellationToken);
            if (lockedOrder.Status == (int)InstallmentStatus.PickedUp)
            {
                ValidatePickupReplay(lockedOrder, operation);
            }
            else
            {
                var operationGuid = operation?.OperationGuid.ToString("D");
                var idempotencyKey = operation?.IdempotencyKey;
                var fingerprint = operation?.Fingerprint;
                var executingDeviceCode = operation?.ExecutingDeviceCode;
                var cashierId = operation?.CashierId;
                if (lockedOrder.Status != (int)InstallmentStatus.PaidOff || lockedOrder.BalanceAmount != 0m)
                {
                    throw new InvalidOperationException("Installment must be paid off before pickup.");
                }

                await db.Updateable<InstallmentOrderEntity>()
                    .SetColumns(x => x.Status == (int)InstallmentStatus.PickedUp)
                    .SetColumns(x => x.PickedUpAt == pickedUpAt.UtcDateTime)
                    .SetColumns(x => x.PickedUpBy == pickedUpBy)
                    .SetColumns(x => x.PickupNote == note)
                    .SetColumns(x => x.PickupOperationGuid == operationGuid)
                    .SetColumns(x => x.PickupIdempotencyKey == idempotencyKey)
                    .SetColumns(x => x.PickupFingerprint == fingerprint)
                    .SetColumns(x => x.PickupExecutingDeviceCode == executingDeviceCode)
                    .SetColumns(x => x.PickupCashierId == cashierId)
                    .SetColumns(x => x.UpdatedAt == DateTime.UtcNow)
                    .Where(x => x.InstallmentGuid == installmentGuidText)
                    .ExecuteCommandAsync(cancellationToken);
            }

            await db.Ado.CommitTranAsync();
        }
        catch
        {
            await db.Ado.RollbackTranAsync();
            throw;
        }

        return await GetDetailsAsync(installmentGuid, cancellationToken)
            ?? throw new InvalidOperationException("Installment was not found.");
    }

    public async Task<InstallmentDetailsDto> CancelWithRefundAsync(
        Guid installmentGuid,
        IReadOnlyList<InstallmentPaymentDto> refunds,
        InstallmentCancellationInfoDto cancellationInfo,
        CancellationToken cancellationToken)
    {
        var db = dbContext.PosmDb;
        await using var processLock = await InstallmentMutationLock.AcquireProcessAsync(
            installmentGuid,
            cancellationToken);
        var installmentGuidText = installmentGuid.ToString("D");
        await db.Ado.BeginTranAsync(System.Data.IsolationLevel.Serializable);
        try
        {
            await InstallmentMutationLock.AcquireDatabaseAsync(db, installmentGuid);
            var lockedOrder = await InstallmentMutationLock.LockOrderAsync(db, installmentGuid, cancellationToken)
                ?? throw new InvalidOperationException("Installment was not found.");
            await InstallmentMutationLock.EnsureNoBlockingClaimAsync(db, installmentGuid, cancellationToken);
            if (!InstallmentLifecycleRules.CanCancelWithRefund(lockedOrder.Status, lockedOrder.BalanceAmount))
            {
                throw new InvalidOperationException("Only active or paid-off installments that have not been picked up can be cancelled with refund.");
            }

            foreach (var refund in refunds)
            {
                var refundPaymentGuidText = refund.PaymentGuid.ToString("D");
                var existingPayment = await db.Queryable<InstallmentPaymentEntity>()
                    .AnyAsync(x => x.PaymentGuid == refundPaymentGuidText, cancellationToken);
                if (!existingPayment)
                {
                    await db.Insertable(MapPayment(installmentGuid, refund)).ExecuteCommandAsync(cancellationToken);
                }
            }

            var paidAmount = RoundCurrency(await db.Queryable<InstallmentPaymentEntity>()
                .Where(x => x.InstallmentGuid == installmentGuidText && x.Status == (int)InstallmentPaymentStatus.Recorded)
                .SumAsync(x => x.Amount));
            await db.Updateable<InstallmentOrderEntity>()
                .SetColumns(x => x.PaidAmount == paidAmount)
                .SetColumns(x => x.BalanceAmount == 0m)
                .SetColumns(x => x.Status == (int)InstallmentStatus.Cancelled)
                .SetColumns(x => x.CancellationKind == (int)cancellationInfo.Kind)
                .SetColumns(x => x.CancelledAt == cancellationInfo.CancelledAt.UtcDateTime)
                .SetColumns(x => x.CancelledBy == cancellationInfo.CancelledBy)
                .SetColumns(x => x.CancellationReason == cancellationInfo.Reason)
                .SetColumns(x => x.CancellationIdempotencyKey == cancellationInfo.IdempotencyKey)
                .SetColumns(x => x.UpdatedAt == DateTime.UtcNow)
                .Where(x => x.InstallmentGuid == installmentGuidText)
                .ExecuteCommandAsync(cancellationToken);
            await db.Ado.CommitTranAsync();
            return await GetDetailsAsync(installmentGuid, cancellationToken)
                ?? throw new InvalidOperationException("Installment was not found.");
        }
        catch
        {
            await db.Ado.RollbackTranAsync();
            throw;
        }
    }

    public Task<InstallmentDetailsDto> VoidAsync(
        Guid installmentGuid,
        InstallmentCancellationInfoDto cancellationInfo,
        CancellationToken cancellationToken) =>
        VoidCoreAsync(installmentGuid, cancellationInfo, operation: null, cancellationToken);

    public Task<InstallmentDetailsDto> VoidIdempotentAsync(
        Guid installmentGuid,
        InstallmentCancellationInfoDto cancellationInfo,
        InstallmentLifecycleOperationFacts operation,
        CancellationToken cancellationToken) =>
        VoidCoreAsync(installmentGuid, cancellationInfo, operation, cancellationToken);

    private async Task<InstallmentDetailsDto> VoidCoreAsync(
        Guid installmentGuid,
        InstallmentCancellationInfoDto cancellationInfo,
        InstallmentLifecycleOperationFacts? operation,
        CancellationToken cancellationToken)
    {
        var db = dbContext.PosmDb;
        await using var processLock = await InstallmentMutationLock.AcquireProcessAsync(
            installmentGuid,
            cancellationToken);
        var installmentGuidText = installmentGuid.ToString("D");
        await db.Ado.BeginTranAsync(System.Data.IsolationLevel.Serializable);
        try
        {
            await InstallmentMutationLock.AcquireDatabaseAsync(db, installmentGuid);
            var lockedOrder = await InstallmentMutationLock.LockOrderAsync(db, installmentGuid, cancellationToken)
                ?? throw new InvalidOperationException("Installment was not found.");
            await InstallmentMutationLock.EnsureNoBlockingClaimAsync(db, installmentGuid, cancellationToken);
            if (lockedOrder.Status == (int)InstallmentStatus.Cancelled)
            {
                if (lockedOrder.CancellationKind != (int)InstallmentCancellationKind.VoidCancel)
                {
                    throw new InvalidOperationException("Installment cancellation kind conflicts with the existing cancelled record.");
                }

                ValidateVoidReplay(lockedOrder, operation);
            }
            else if (lockedOrder.Status != (int)InstallmentStatus.Active || lockedOrder.BalanceAmount <= 0m)
            {
                throw new InvalidOperationException("Only active unpaid installments can be voided.");
            }
            else
            {
                var operationGuid = operation?.OperationGuid.ToString("D");
                var fingerprint = operation?.Fingerprint;
                var executingDeviceCode = operation?.ExecutingDeviceCode;
                var cashierId = operation?.CashierId;
                await db.Updateable<InstallmentOrderEntity>()
                    .SetColumns(x => x.Status == (int)InstallmentStatus.Cancelled)
                    .SetColumns(x => x.CancellationKind == (int)cancellationInfo.Kind)
                    .SetColumns(x => x.CancelledAt == cancellationInfo.CancelledAt.UtcDateTime)
                    .SetColumns(x => x.CancelledBy == cancellationInfo.CancelledBy)
                    .SetColumns(x => x.CancellationReason == cancellationInfo.Reason)
                    .SetColumns(x => x.CancellationIdempotencyKey == cancellationInfo.IdempotencyKey)
                    .SetColumns(x => x.CancellationOperationGuid == operationGuid)
                    .SetColumns(x => x.CancellationFingerprint == fingerprint)
                    .SetColumns(x => x.CancellationExecutingDeviceCode == executingDeviceCode)
                    .SetColumns(x => x.CancellationCashierId == cashierId)
                    .SetColumns(x => x.UpdatedAt == DateTime.UtcNow)
                    .Where(x => x.InstallmentGuid == installmentGuidText)
                    .ExecuteCommandAsync(cancellationToken);
            }
            await db.Ado.CommitTranAsync();
        }
        catch
        {
            await db.Ado.RollbackTranAsync();
            throw;
        }

        return await GetDetailsAsync(installmentGuid, cancellationToken)
            ?? throw new InvalidOperationException("Installment was not found.");
    }

    private static void ValidatePickupReplay(
        InstallmentOrderEntity order,
        InstallmentLifecycleOperationFacts? operation)
    {
        if (operation is null)
        {
            return;
        }

        if (PickupOperationFactsAreAbsent(order))
        {
            return;
        }

        if (!LifecycleOperationMatches(
                order.PickupOperationGuid,
                order.PickupIdempotencyKey,
                order.PickupFingerprint,
                order.PickupExecutingDeviceCode,
                operation))
        {
            throw new InvalidOperationException("Installment pickup idempotency facts conflict with the existing operation.");
        }
    }

    private static void ValidateVoidReplay(
        InstallmentOrderEntity order,
        InstallmentLifecycleOperationFacts? operation)
    {
        if (operation is null)
        {
            return;
        }

        if (VoidOperationFactsAreAbsent(order) &&
            (string.IsNullOrWhiteSpace(order.CancellationIdempotencyKey) ||
             string.Equals(order.CancellationIdempotencyKey, operation.IdempotencyKey, StringComparison.Ordinal)))
        {
            return;
        }

        if (!LifecycleOperationMatches(
                order.CancellationOperationGuid,
                order.CancellationIdempotencyKey,
                order.CancellationFingerprint,
                order.CancellationExecutingDeviceCode,
                operation))
        {
            throw new InvalidOperationException("Installment void idempotency facts conflict with the existing operation.");
        }
    }

    private static bool LifecycleOperationMatches(
        string? operationGuid,
        string? idempotencyKey,
        string? fingerprint,
        string? executingDeviceCode,
        InstallmentLifecycleOperationFacts operation)
    {
        return string.Equals(operationGuid, operation.OperationGuid.ToString("D"), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(idempotencyKey, operation.IdempotencyKey, StringComparison.Ordinal) &&
            string.Equals(fingerprint, operation.Fingerprint, StringComparison.Ordinal) &&
            string.Equals(executingDeviceCode, operation.ExecutingDeviceCode, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PickupOperationFactsAreAbsent(InstallmentOrderEntity order) =>
        string.IsNullOrWhiteSpace(order.PickupOperationGuid) &&
        string.IsNullOrWhiteSpace(order.PickupIdempotencyKey) &&
        string.IsNullOrWhiteSpace(order.PickupFingerprint) &&
        string.IsNullOrWhiteSpace(order.PickupExecutingDeviceCode) &&
        string.IsNullOrWhiteSpace(order.PickupCashierId);

    private static bool VoidOperationFactsAreAbsent(InstallmentOrderEntity order) =>
        string.IsNullOrWhiteSpace(order.CancellationOperationGuid) &&
        string.IsNullOrWhiteSpace(order.CancellationFingerprint) &&
        string.IsNullOrWhiteSpace(order.CancellationExecutingDeviceCode) &&
        string.IsNullOrWhiteSpace(order.CancellationCashierId);

    public async Task<InstallmentPaymentLookup?> FindPaymentAsync(
        Guid paymentGuid,
        CancellationToken cancellationToken)
    {
        var db = dbContext.PosmDb;
        var paymentGuidText = paymentGuid.ToString("D");
        var entity = await db.Queryable<InstallmentPaymentEntity>()
            .FirstAsync(x => x.PaymentGuid == paymentGuidText, cancellationToken);
        return entity is null ? null : new InstallmentPaymentLookup(ParseGuid(entity.InstallmentGuid), MapPayment(entity));
    }

    public async Task<InstallmentPaymentLookup?> FindPaymentByIdempotencyKeyAsync(
        Guid installmentGuid,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var db = dbContext.PosmDb;
        var installmentGuidText = installmentGuid.ToString("D");
        var entity = await db.Queryable<InstallmentPaymentEntity>()
            .FirstAsync(x => x.InstallmentGuid == installmentGuidText && x.IdempotencyKey == idempotencyKey, cancellationToken);
        return entity is null ? null : new InstallmentPaymentLookup(ParseGuid(entity.InstallmentGuid), MapPayment(entity));
    }

    public async Task<InstallmentHistoryQueryResponse> QueryAsync(
        InstallmentHistoryQueryRequest request,
        CancellationToken cancellationToken)
    {
        var db = dbContext.PosmDb;
        var query = db.Queryable<InstallmentOrderEntity>()
            .Where(x => x.StoreCode == request.StoreCode);
        if (!string.IsNullOrWhiteSpace(request.DeviceCode))
        {
            query = query.Where(x => x.DeviceCode == request.DeviceCode);
        }

        if (request.CreatedFrom is not null)
        {
            query = query.Where(x => x.CreatedAt >= request.CreatedFrom.Value.UtcDateTime);
        }

        if (request.CreatedTo is not null)
        {
            query = query.Where(x => x.CreatedAt <= request.CreatedTo.Value.UtcDateTime);
        }

        if (request.UpdatedFrom is not null)
        {
            query = query.Where(x => x.UpdatedAt >= request.UpdatedFrom.Value.UtcDateTime);
        }

        if (request.UpdatedTo is not null)
        {
            query = query.Where(x => x.UpdatedAt <= request.UpdatedTo.Value.UtcDateTime);
        }

        if (request.Status is not null)
        {
            query = query.Where(x => x.Status == (int)request.Status.Value);
        }

        if (!string.IsNullOrWhiteSpace(request.Keyword))
        {
            var keyword = request.Keyword.Trim();
            // 商品条件保持在订单查询内，让数据库先筛选再排序分页，避免 Skip 越大就物化越多 GUID 并拼接超长 IN。
            query = query.Where(x =>
                // 模糊文本搜索也必须按字面量处理，避免 %, _ 等字符扩大成 LIKE 通配符。
                SqlFunc.CharIndexNew(x.InstallmentGuid, keyword) > 0 ||
                SqlFunc.CharIndexNew(x.InstallmentNumber, keyword) > 0 ||
                SqlFunc.CharIndexNew(x.CustomerName, keyword) > 0 ||
                SqlFunc.CharIndexNew(x.CustomerPhone, keyword) > 0 ||
                SqlFunc.Subqueryable<InstallmentOrderLineEntity>()
                    .Where(line => line.InstallmentGuid == x.InstallmentGuid &&
                        (line.ItemNumber == keyword ||
                         line.LookupCode == keyword ||
                         line.ProductCode == keyword))
                    .Any());
        }

        // 历史页按“最近更新”展示；保留默认按创建时间排序，避免改变现有 API 调用方语义。
        query = request.OrderByUpdatedAt
            ? query.OrderByDescending(x => x.UpdatedAt)
            : query.OrderByDescending(x => x.CreatedAt);
        var rows = await query
            .OrderByDescending(x => x.InstallmentGuid)
            .Skip(request.Skip)
            .Take(Math.Clamp(request.Take, 1, 200))
            .ToListAsync(cancellationToken);
        return new InstallmentHistoryQueryResponse(rows.Select(MapSummary).ToList());
    }

    public async Task<InstallmentDetailsDto?> GetDetailsAsync(
        Guid installmentGuid,
        CancellationToken cancellationToken)
    {
        var db = dbContext.PosmDb;
        return await GetDetailsInsideTransactionAsync(db, installmentGuid, cancellationToken);
    }

    private static async Task<InstallmentDetailsDto?> GetDetailsInsideTransactionAsync(
        ISqlSugarClient db,
        Guid installmentGuid,
        CancellationToken cancellationToken)
    {
        var guidText = installmentGuid.ToString("D");
        var order = await db.Queryable<InstallmentOrderEntity>()
            .FirstAsync(x => x.InstallmentGuid == guidText, cancellationToken);
        if (order is null)
        {
            return null;
        }

        var lines = await db.Queryable<InstallmentOrderLineEntity>()
            .Where(x => x.InstallmentGuid == guidText)
            .ToListAsync(cancellationToken);
        var payments = await db.Queryable<InstallmentPaymentEntity>()
            .Where(x => x.InstallmentGuid == guidText)
            .OrderBy(x => x.RecordedAt)
            .ToListAsync(cancellationToken);
        return MapDetails(order, lines, payments);
    }

    private static InstallmentOrderEntity MapOrder(InstallmentDetailsDto details)
    {
        return new InstallmentOrderEntity
        {
            InstallmentGuid = details.InstallmentGuid.ToString("D"),
            InstallmentNumber = details.InstallmentNumber,
            StoreCode = details.StoreCode,
            DeviceCode = details.DeviceCode,
            CashierId = details.CashierId,
            CashierName = details.CashierName,
            CustomerName = details.CustomerName,
            CustomerPhone = details.CustomerPhone,
            TotalAmount = details.TotalAmount,
            MinimumDownPayment = details.MinimumDownPayment,
            DownPaymentAmount = details.DownPaymentAmount,
            PaidAmount = details.PaidAmount,
            BalanceAmount = details.BalanceAmount,
            Status = (int)details.Status,
            CreatedAt = details.CreatedAt.UtcDateTime,
            UpdatedAt = details.UpdatedAt?.UtcDateTime ?? DateTime.UtcNow,
            Note = details.Note,
            CancellationKind = details.CancellationInfo is null ? null : (int)details.CancellationInfo.Kind,
            CancelledAt = details.CancellationInfo?.CancelledAt.UtcDateTime,
            CancelledBy = details.CancellationInfo?.CancelledBy,
            CancellationReason = details.CancellationInfo?.Reason,
            CancellationIdempotencyKey = details.CancellationInfo?.IdempotencyKey
        };
    }

    private static InstallmentOrderLineEntity MapLine(Guid installmentGuid, InstallmentLineDto line)
    {
        return new InstallmentOrderLineEntity
        {
            InstallmentLineGuid = line.InstallmentLineGuid.ToString("D"),
            InstallmentGuid = installmentGuid.ToString("D"),
            ProductCode = line.ProductCode,
            ReferenceCode = line.ReferenceCode,
            DisplayName = line.DisplayName,
            LookupCode = line.LookupCode,
            ItemNumber = line.ItemNumber,
            Quantity = line.Quantity,
            UnitPrice = line.UnitPrice,
            DiscountAmount = line.DiscountAmount,
            ActualAmount = line.ActualAmount
        };
    }

    private static InstallmentPaymentEntity MapPayment(Guid installmentGuid, InstallmentPaymentDto payment)
    {
        return new InstallmentPaymentEntity
        {
            PaymentGuid = payment.PaymentGuid.ToString("D"),
            InstallmentGuid = installmentGuid.ToString("D"),
            Method = (int)payment.Method,
            Amount = payment.Amount,
            Reference = payment.Reference,
            Status = (int)payment.Status,
            RecordedAt = payment.RecordedAt.UtcDateTime,
            CashierId = payment.CashierId,
            DeviceCode = payment.DeviceCode,
            CashierName = payment.CashierName,
            CardTransactionsJson = payment.CardTransactions is null ? null : JsonSerializer.Serialize(payment.CardTransactions, JsonOptions),
            IdempotencyKey = payment.IdempotencyKey
        };
    }

    private static InstallmentSummaryDto MapSummary(InstallmentOrderEntity order)
    {
        return new InstallmentSummaryDto(
            ParseGuid(order.InstallmentGuid),
            order.InstallmentNumber,
            order.StoreCode,
            order.DeviceCode,
            order.CashierName,
            order.CustomerName,
            order.CustomerPhone,
            ToDateTimeOffset(order.CreatedAt),
            order.TotalAmount,
            order.DownPaymentAmount,
            order.PaidAmount,
            order.BalanceAmount,
            (InstallmentStatus)order.Status,
            ToDateTimeOffset(order.UpdatedAt),
            order.CancellationKind is null
                ? null
                : (InstallmentCancellationKind)order.CancellationKind.Value);
    }

    private static InstallmentDetailsDto MapDetails(
        InstallmentOrderEntity order,
        IReadOnlyList<InstallmentOrderLineEntity> lines,
        IReadOnlyList<InstallmentPaymentEntity> payments)
    {
        var pickupInfo = order.PickedUpAt is null
            ? null
            : new InstallmentPickupInfoDto(
                ToDateTimeOffset(order.PickedUpAt.Value),
                order.PickedUpBy ?? string.Empty,
                order.PickupNote);
        var cancellationInfo = order.CancellationKind is null || order.CancelledAt is null
            ? null
            : new InstallmentCancellationInfoDto(
                (InstallmentCancellationKind)order.CancellationKind.Value,
                ToDateTimeOffset(order.CancelledAt.Value),
                order.CancelledBy ?? string.Empty,
                order.CancellationReason,
                order.CancellationIdempotencyKey);
        return new InstallmentDetailsDto(
            ParseGuid(order.InstallmentGuid),
            order.InstallmentNumber,
            order.StoreCode,
            order.DeviceCode,
            order.CashierId,
            order.CashierName,
            order.CustomerName,
            order.CustomerPhone,
            ToDateTimeOffset(order.CreatedAt),
            order.TotalAmount,
            order.MinimumDownPayment,
            order.DownPaymentAmount,
            order.PaidAmount,
            order.BalanceAmount,
            (InstallmentStatus)order.Status,
            lines.Select(MapLine).ToList(),
            payments.Select(MapPayment).ToList(),
            pickupInfo,
            cancellationInfo,
            order.Note,
            UpdatedAt: ToDateTimeOffset(order.UpdatedAt));
    }

    private static InstallmentLineDto MapLine(InstallmentOrderLineEntity line)
    {
        return new InstallmentLineDto(
            ParseGuid(line.InstallmentLineGuid),
            line.ProductCode,
            line.ReferenceCode,
            line.DisplayName,
            line.LookupCode,
            line.Quantity,
            line.UnitPrice,
            line.DiscountAmount,
            line.ActualAmount,
            line.ItemNumber);
    }

    private static InstallmentPaymentDto MapPayment(InstallmentPaymentEntity payment)
    {
        IReadOnlyList<CardTransactionDto>? cardTransactions = null;
        if (!string.IsNullOrWhiteSpace(payment.CardTransactionsJson))
        {
            cardTransactions = JsonSerializer.Deserialize<IReadOnlyList<CardTransactionDto>>(payment.CardTransactionsJson, JsonOptions);
        }

        return new InstallmentPaymentDto(
            ParseGuid(payment.PaymentGuid),
            (PaymentMethodKind)payment.Method,
            payment.Amount,
            payment.Reference,
            (InstallmentPaymentStatus)payment.Status,
            ToDateTimeOffset(payment.RecordedAt),
            payment.CashierId,
            payment.DeviceCode,
            cardTransactions,
            payment.IdempotencyKey,
            ReservationToken: null,
            CashierName: payment.CashierName);
    }

    private static DateTimeOffset ToDateTimeOffset(DateTime value)
    {
        return new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    }

    private static Guid ParseGuid(string value)
    {
        return Guid.TryParse(value, out var guid) ? guid : Guid.Empty;
    }

    private static decimal RoundCurrency(decimal amount) => decimal.Round(amount, 2, MidpointRounding.AwayFromZero);
}

[SugarTable("InstallmentOrder")]
public sealed class InstallmentOrderEntity
{
    [SugarColumn(IsPrimaryKey = true, Length = 36)]
    public string InstallmentGuid { get; set; } = string.Empty;

    [SugarColumn(Length = 40)]
    public string InstallmentNumber { get; set; } = string.Empty;

    [SugarColumn(Length = 50)]
    public string StoreCode { get; set; } = string.Empty;

    [SugarColumn(Length = 50)]
    public string DeviceCode { get; set; } = string.Empty;

    [SugarColumn(Length = 50)]
    public string CashierId { get; set; } = string.Empty;

    [SugarColumn(Length = 100)]
    public string CashierName { get; set; } = string.Empty;

    [SugarColumn(Length = 100)]
    public string CustomerName { get; set; } = string.Empty;

    [SugarColumn(Length = 40)]
    public string CustomerPhone { get; set; } = string.Empty;

    public decimal TotalAmount { get; set; }

    public decimal MinimumDownPayment { get; set; }

    public decimal DownPaymentAmount { get; set; }

    public decimal PaidAmount { get; set; }

    public decimal BalanceAmount { get; set; }

    public int Status { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    [SugarColumn(IsNullable = true)]
    public DateTime? PickedUpAt { get; set; }

    [SugarColumn(Length = 100, IsNullable = true)]
    public string? PickedUpBy { get; set; }

    [SugarColumn(Length = 500, IsNullable = true)]
    public string? Note { get; set; }

    [SugarColumn(Length = 500, IsNullable = true)]
    public string? PickupNote { get; set; }

    [SugarColumn(Length = 36, IsNullable = true)]
    public string? PickupOperationGuid { get; set; }

    [SugarColumn(Length = 100, IsNullable = true)]
    public string? PickupIdempotencyKey { get; set; }

    [SugarColumn(Length = 80, IsNullable = true)]
    public string? PickupFingerprint { get; set; }

    [SugarColumn(Length = 50, IsNullable = true)]
    public string? PickupExecutingDeviceCode { get; set; }

    [SugarColumn(Length = 50, IsNullable = true)]
    public string? PickupCashierId { get; set; }

    [SugarColumn(IsNullable = true)]
    public int? CancellationKind { get; set; }

    [SugarColumn(IsNullable = true)]
    public DateTime? CancelledAt { get; set; }

    [SugarColumn(Length = 100, IsNullable = true)]
    public string? CancelledBy { get; set; }

    [SugarColumn(Length = 500, IsNullable = true)]
    public string? CancellationReason { get; set; }

    [SugarColumn(Length = 100, IsNullable = true)]
    public string? CancellationIdempotencyKey { get; set; }

    [SugarColumn(Length = 36, IsNullable = true)]
    public string? CancellationOperationGuid { get; set; }

    [SugarColumn(Length = 80, IsNullable = true)]
    public string? CancellationFingerprint { get; set; }

    [SugarColumn(Length = 50, IsNullable = true)]
    public string? CancellationExecutingDeviceCode { get; set; }

    [SugarColumn(Length = 50, IsNullable = true)]
    public string? CancellationCashierId { get; set; }
}

[SugarTable("InstallmentOrderLine")]
public sealed class InstallmentOrderLineEntity
{
    [SugarColumn(IsPrimaryKey = true, Length = 36)]
    public string InstallmentLineGuid { get; set; } = string.Empty;

    [SugarColumn(Length = 36)]
    public string InstallmentGuid { get; set; } = string.Empty;

    [SugarColumn(Length = 50)]
    public string ProductCode { get; set; } = string.Empty;

    [SugarColumn(Length = 50, IsNullable = true)]
    public string? ReferenceCode { get; set; }

    [SugarColumn(Length = 255)]
    public string DisplayName { get; set; } = string.Empty;

    [SugarColumn(Length = 50)]
    public string LookupCode { get; set; } = string.Empty;

    [SugarColumn(Length = 50, IsNullable = true)]
    public string? ItemNumber { get; set; }

    public decimal Quantity { get; set; }

    public decimal UnitPrice { get; set; }

    public decimal DiscountAmount { get; set; }

    public decimal ActualAmount { get; set; }
}

[SugarTable("InstallmentPayment")]
public sealed class InstallmentPaymentEntity
{
    [SugarColumn(IsPrimaryKey = true, Length = 36)]
    public string PaymentGuid { get; set; } = string.Empty;

    [SugarColumn(Length = 36)]
    public string InstallmentGuid { get; set; } = string.Empty;

    public int Method { get; set; }

    public decimal Amount { get; set; }

    [SugarColumn(Length = 200, IsNullable = true)]
    public string? Reference { get; set; }

    public int Status { get; set; }

    public DateTime RecordedAt { get; set; }

    [SugarColumn(Length = 50)]
    public string CashierId { get; set; } = string.Empty;

    [SugarColumn(Length = 100, IsNullable = true)]
    public string? CashierName { get; set; }

    [SugarColumn(Length = 50)]
    public string DeviceCode { get; set; } = string.Empty;

    [SugarColumn(ColumnDataType = StaticConfig.CodeFirst_BigString, IsNullable = true)]
    public string? CardTransactionsJson { get; set; }

    [SugarColumn(Length = 100, IsNullable = true)]
    public string? IdempotencyKey { get; set; }
}
