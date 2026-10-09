using System.Text.Json;
using Hbpos.Client.Wpf.Models;

namespace Hbpos.Client.Wpf.Services;

public sealed record SquareRefundSettlementCheckResult(
    int Checked,
    int Completed,
    int Failed,
    int StillPending,
    int Errors)
{
    public static SquareRefundSettlementCheckResult Empty { get; } = new(0, 0, 0, 0, 0);
}

/// <summary>
/// 跟踪“退货单已完成、Square 退款仍在结算”的退款：只用 GET 查询同一笔退款，绝不重新发起退款。
/// Square 官方说明 PENDING 多数几小时内完成、最长 14 天；转为 REJECTED/FAILED 时需主管改用其他方式退给顾客。
/// </summary>
public interface ISquareRefundSettlementService
{
    Task<SquareRefundSettlementCheckResult> CheckPendingAsync(
        PosSessionState session,
        Guid? attemptGuid = null,
        CancellationToken cancellationToken = default);

    /// <summary>主管确认已改用现金等方式把被拒的退款退给顾客；只改变本地结算状态，不调用 Square。</summary>
    Task<bool> AcknowledgeFailureAsync(
        Guid attemptGuid,
        PosSessionState session,
        string note,
        CancellationToken cancellationToken = default);

    Task<int> CountUnhandledFailuresAsync(PosSessionState session, CancellationToken cancellationToken = default);
}

public sealed class SquareRefundSettlementService(
    ILocalSquarePaymentAttemptRepository attemptRepository,
    ISquareTerminalPaymentClient squareTerminalPaymentClient,
    ICardTerminalSettingsProvider settingsProvider) : ISquareRefundSettlementService
{
    private const string LogCategory = "SquareRefundSettlement";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _checkGate = new(1, 1);

    public async Task<SquareRefundSettlementCheckResult> CheckPendingAsync(
        PosSessionState session,
        Guid? attemptGuid = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(session.StoreCode) || string.IsNullOrWhiteSpace(session.DeviceCode))
        {
            return SquareRefundSettlementCheckResult.Empty;
        }

        // 后台定时与恢复中心可能同时触发；串行执行避免同一记录并发 CAS。
        await _checkGate.WaitAsync(cancellationToken);
        try
        {
            var settings = await settingsProvider.GetSettingsAsync(cancellationToken);
            var settlements = await attemptRepository.GetRefundSettlementsAsync(
                session.StoreCode,
                session.DeviceCode,
                settings.Environment.ToString(),
                cancellationToken);
            var pending = settlements
                .Where(item => SquareRefundSettlementStatuses.IsPending(item.PaymentStatus))
                .Where(item => attemptGuid is null || item.AttemptGuid == attemptGuid.Value)
                .ToArray();
            int completed = 0, failed = 0, stillPending = 0, errors = 0;
            foreach (var attempt in pending)
            {
                cancellationToken.ThrowIfCancellationRequested();
                switch (await CheckOneAsync(settings, attempt, cancellationToken))
                {
                    case SettlementOutcome.Completed: completed++; break;
                    case SettlementOutcome.Failed: failed++; break;
                    case SettlementOutcome.Pending: stillPending++; break;
                    default: errors++; break;
                }
            }

            if (pending.Length > 0)
            {
                ConsoleLog.Write(
                    LogCategory,
                    $"check completed store={session.StoreCode} device={session.DeviceCode} checked={pending.Length} completed={completed} failed={failed} pending={stillPending} errors={errors}");
            }

            return new SquareRefundSettlementCheckResult(pending.Length, completed, failed, stillPending, errors);
        }
        finally
        {
            _checkGate.Release();
        }
    }

    public async Task<bool> AcknowledgeFailureAsync(
        Guid attemptGuid,
        PosSessionState session,
        string note,
        CancellationToken cancellationToken = default)
    {
        var attempt = await attemptRepository.GetAttemptAsync(attemptGuid, cancellationToken);
        if (attempt is null ||
            attempt.Status != LocalSquarePaymentAttemptStatus.OrderCompleted ||
            !string.Equals(attempt.OperationKind, "Refund", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(attempt.StoreCode, session.StoreCode, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(attempt.DeviceCode, session.DeviceCode, StringComparison.OrdinalIgnoreCase) ||
            !SquareRefundSettlementStatuses.IsUnhandledFailure(attempt.PaymentStatus))
        {
            return false;
        }

        var handled = await attemptRepository.TryUpdateRefundSettlementStatusAsync(
            attempt.AttemptGuid,
            attempt.PaymentStatus!,
            attempt.UpdatedAt,
            SquareRefundSettlementStatuses.HandledFor(attempt.PaymentStatus!),
            Truncate($"Refunded by other means. {note}".Trim(), 500),
            NextTimestamp(attempt.UpdatedAt),
            cancellationToken);
        ConsoleLog.Write(
            LogCategory,
            $"failure acknowledged attemptGuid={attempt.AttemptGuid} refundId={attempt.PaymentId} status={attempt.PaymentStatus} persisted={handled}");
        return handled;
    }

    public async Task<int> CountUnhandledFailuresAsync(PosSessionState session, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(session.StoreCode) || string.IsNullOrWhiteSpace(session.DeviceCode))
        {
            return 0;
        }

        var settings = await settingsProvider.GetSettingsAsync(cancellationToken);
        var settlements = await attemptRepository.GetRefundSettlementsAsync(
            session.StoreCode,
            session.DeviceCode,
            settings.Environment.ToString(),
            cancellationToken);
        return settlements.Count(item => SquareRefundSettlementStatuses.IsUnhandledFailure(item.PaymentStatus));
    }

    private async Task<SettlementOutcome> CheckOneAsync(
        CardTerminalSettings settings,
        LocalSquarePaymentAttempt attempt,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(attempt.PaymentId))
        {
            return SettlementOutcome.Error;
        }

        SquareRefundStatusResult refund;
        try
        {
            refund = await squareTerminalPaymentClient.GetRefundAsync(settings, attempt.PaymentId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            ConsoleLog.WriteWarning(
                LogCategory,
                $"refund lookup failed attemptGuid={attempt.AttemptGuid} refundId={attempt.PaymentId} error={ex.GetType().Name}",
                new ApplicationLogContext(TraceId: attempt.AttemptGuid.ToString("D")),
                ex);
            return SettlementOutcome.Error;
        }

        // 只接受与本地记录完全对应的同一笔退款，防止错误响应把别的退款结果写进来。
        var originalPaymentId = ReadOriginalSquarePaymentId(attempt.OrderDraftJson);
        if (!string.Equals(refund.RefundId, attempt.PaymentId, StringComparison.Ordinal) ||
            originalPaymentId is null ||
            !string.Equals(refund.PaymentId, originalPaymentId, StringComparison.Ordinal) ||
            refund.AmountCents != attempt.AmountCents ||
            !string.Equals(refund.Currency, attempt.Currency, StringComparison.OrdinalIgnoreCase))
        {
            ConsoleLog.WriteWarning(
                LogCategory,
                $"refund identity mismatch attemptGuid={attempt.AttemptGuid} refundId={attempt.PaymentId}",
                new ApplicationLogContext(TraceId: attempt.AttemptGuid.ToString("D")));
            return SettlementOutcome.Error;
        }

        var status = refund.Status?.Trim().ToUpperInvariant() ?? string.Empty;
        string? responseText = status switch
        {
            SquareRefundSettlementStatuses.Completed => "Square refund completed.",
            SquareRefundSettlementStatuses.Rejected or SquareRefundSettlementStatuses.Failed =>
                "Square did not complete the refund after the return was finished. Refund the customer by other means.",
            _ => null
        };
        if (responseText is null)
        {
            return status == SquareRefundSettlementStatuses.Pending ? SettlementOutcome.Pending : SettlementOutcome.Error;
        }

        var persisted = await attemptRepository.TryUpdateRefundSettlementStatusAsync(
            attempt.AttemptGuid,
            SquareRefundSettlementStatuses.Pending,
            attempt.UpdatedAt,
            status,
            responseText,
            NextTimestamp(attempt.UpdatedAt),
            cancellationToken);
        var isFailure = status != SquareRefundSettlementStatuses.Completed;
        var message =
            $"refund settled attemptGuid={attempt.AttemptGuid} refundId={attempt.PaymentId} status={status} persisted={persisted}";
        if (isFailure)
        {
            ConsoleLog.WriteWarning(LogCategory, message, new ApplicationLogContext(TraceId: attempt.AttemptGuid.ToString("D")));
        }
        else
        {
            ConsoleLog.Write(LogCategory, message);
        }

        if (!persisted)
        {
            return SettlementOutcome.Error;
        }

        return isFailure ? SettlementOutcome.Failed : SettlementOutcome.Completed;
    }

    private static string? ReadOriginalSquarePaymentId(string? orderDraftJson)
    {
        if (string.IsNullOrWhiteSpace(orderDraftJson))
        {
            return null;
        }

        try
        {
            var draft = JsonSerializer.Deserialize<CardPaymentOrderDraft>(orderDraftJson, JsonOptions);
            var reference = draft?.OriginalReference?.Trim();
            return reference is not null && reference.StartsWith("SQ:", StringComparison.OrdinalIgnoreCase)
                ? reference[3..].Trim()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // UpdatedAt 也是 CAS 版本号，新值必须严格大于旧值。
    private static DateTimeOffset NextTimestamp(DateTimeOffset previous)
    {
        var now = DateTimeOffset.UtcNow;
        return now > previous ? now : previous.AddTicks(1);
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private enum SettlementOutcome
    {
        Completed,
        Failed,
        Pending,
        Error
    }
}
