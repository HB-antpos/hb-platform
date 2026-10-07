using System.Data;
using System.Reflection;
using System.Text;
using BlazorApp.Service.Models.HBPOSM_POSM;
using BlazorApp.Shared.Models.POSM;
using Hbpos.Api.Data;
using Hbpos.Contracts.Vouchers;
using SqlSugar;

namespace Hbpos.Api.Services;

public interface IStoreVoucherService
{
    Task<StoreVoucherQueryResponse> QueryAsync(
        string storeCode,
        string voucherCode,
        CancellationToken cancellationToken);

    Task<StoreVoucherLockResponse> LockAsync(
        StoreVoucherLockRequest request,
        CancellationToken cancellationToken);

    Task<StoreVoucherReleaseResponse> ReleaseAsync(
        StoreVoucherReleaseRequest request,
        CancellationToken cancellationToken);

    Task<StoreVoucherIssueRefundResponse> IssueRefundAsync(
        StoreVoucherIssueRefundRequest request,
        CancellationToken cancellationToken);

    Task<StoreVoucherIssueResponse> IssueAsync(
        StoreVoucherIssueRequest request,
        CancellationToken cancellationToken);
}

public sealed class StoreVoucherService(
    IStoreVoucherRepository repository,
    IStoreVoucherReservationService reservationService,
    TimeProvider? timeProvider = null,
    ILogger<StoreVoucherService>? logger = null,
    IStoreTimeZoneResolver? timeZoneResolver = null) : IStoreVoucherService
{
    // 中文注释：新发代金券（含退款券）的默认有效期，统一为 90 天；只影响此后新发的券，已发出的券到期日不变。
    private const int VoucherValidityDays = 90;

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// 默认到期时刻 = 发券日（门店当地日历日）+ 90 天，取整到该日门店当地 23:59:59。
    /// 券面印的「Valid until yyyy-MM-dd」是日历日，顾客会认为当天结束前都能用，
    /// 所以不能按「发券时刻 + 90 天」精确到秒（否则上午发的券，到期日下午就失效）。
    /// 结果是绝对时刻（含门店当天的 UTC 偏移，夏令时跨越按到期日当天的偏移算）；库里照旧存 UTC。
    /// 未注入时区解析器（仅单元测试）时按 UTC 日历日处理；生产由 DI 注入，门店没配时区时回退悉尼。
    /// </summary>
    private async Task<DateTimeOffset> ResolveDefaultExpiryAsync(
        string storeCode,
        DateTimeOffset issuedAt,
        CancellationToken cancellationToken)
    {
        var storeTimeZone = timeZoneResolver is null
            ? TimeZoneInfo.Utc
            : await timeZoneResolver.ResolveAsync(storeCode, cancellationToken);
        var issuedLocalDate = TimeZoneInfo.ConvertTime(issuedAt, storeTimeZone).Date;
        var lastValidLocalSecond = issuedLocalDate.AddDays(VoucherValidityDays + 1).AddSeconds(-1);
        return StoreWallClock.ToDateTimeOffset(lastValidLocalSecond, storeTimeZone);
    }

    public async Task<StoreVoucherQueryResponse> QueryAsync(
        string storeCode,
        string voucherCode,
        CancellationToken cancellationToken)
    {
        var normalizedStoreCode = NormalizeRequired(storeCode, nameof(storeCode));
        var normalizedVoucherCode = NormalizeRequired(voucherCode, nameof(voucherCode));
        logger?.LogInformation(
            "Voucher query start store={StoreCode} voucher={VoucherCode}",
            normalizedStoreCode,
            normalizedVoucherCode);
        var voucher = await repository.FindAvailableAsync(normalizedStoreCode, normalizedVoucherCode, cancellationToken);

        if (voucher is null)
        {
            logger?.LogWarning(
                "Voucher query not found store={StoreCode} voucher={VoucherCode}",
                normalizedStoreCode,
                normalizedVoucherCode);
            return new StoreVoucherQueryResponse(false, null, "VoucherNotFound");
        }

        logger?.LogInformation(
            "Voucher query found store={StoreCode} voucher={VoucherCode} remaining={RemainingAmount} status={Status}",
            normalizedStoreCode,
            normalizedVoucherCode,
            voucher.RemainingAmount,
            voucher.Status);
        return new StoreVoucherQueryResponse(true, Map(voucher));
    }

    public async Task<StoreVoucherLockResponse> LockAsync(
        StoreVoucherLockRequest request,
        CancellationToken cancellationToken)
    {
        var normalizedStoreCode = NormalizeRequired(request.StoreCode, nameof(request.StoreCode));
        var normalizedVoucherCode = NormalizeRequired(request.VoucherCode, nameof(request.VoucherCode));
        if (request.RequestedAmount <= 0)
        {
            throw new InvalidOperationException("Requested amount must be greater than zero.");
        }

        logger?.LogInformation(
            "Voucher lock start store={StoreCode} voucher={VoucherCode} requestedAmount={RequestedAmount}",
            normalizedStoreCode,
            normalizedVoucherCode,
            request.RequestedAmount);
        var voucher = await repository.FindAvailableAsync(normalizedStoreCode, normalizedVoucherCode, cancellationToken)
            ?? throw new InvalidOperationException("Voucher is unavailable.");
        var reservation = await reservationService.ReserveAsync(
            normalizedStoreCode,
            normalizedVoucherCode,
            request.RequestedAmount,
            voucher.RemainingAmount ?? 0m,
            cancellationToken);

        logger?.LogInformation(
            "Voucher lock completed store={StoreCode} voucher={VoucherCode} lockedAmount={LockedAmount} token={ReservationToken} expiresAt={ExpiresAt:O}",
            normalizedStoreCode,
            normalizedVoucherCode,
            reservation.LockedAmount,
            ShortToken(reservation.Token),
            reservation.ExpiresAt);
        return new StoreVoucherLockResponse(
            normalizedVoucherCode,
            reservation.LockedAmount,
            reservation.Token,
            reservation.ExpiresAt,
            reservation.RemainingAmountAfterLock);
    }

    public async Task<StoreVoucherReleaseResponse> ReleaseAsync(
        StoreVoucherReleaseRequest request,
        CancellationToken cancellationToken)
    {
        var normalizedStoreCode = NormalizeRequired(request.StoreCode, nameof(request.StoreCode));
        var normalizedVoucherCode = NormalizeRequired(request.VoucherCode, nameof(request.VoucherCode));
        var normalizedToken = NormalizeRequired(request.ReservationToken, nameof(request.ReservationToken));
        logger?.LogInformation(
            "Voucher release start store={StoreCode} voucher={VoucherCode} token={ReservationToken}",
            normalizedStoreCode,
            normalizedVoucherCode,
            ShortToken(normalizedToken));

        var released = await reservationService.ReleaseAsync(
            normalizedToken,
            normalizedStoreCode,
            normalizedVoucherCode,
            cancellationToken);

        logger?.LogInformation(
            "Voucher release completed store={StoreCode} voucher={VoucherCode} token={ReservationToken} released={Released}",
            normalizedStoreCode,
            normalizedVoucherCode,
            ShortToken(normalizedToken),
            released);
        return new StoreVoucherReleaseResponse(normalizedVoucherCode, normalizedToken, released);
    }

    public async Task<StoreVoucherIssueRefundResponse> IssueRefundAsync(
        StoreVoucherIssueRefundRequest request,
        CancellationToken cancellationToken)
    {
        var normalizedStoreCode = NormalizeRequired(request.StoreCode, nameof(request.StoreCode));
        var normalizedCashierId = NormalizeRequired(request.CashierId, nameof(request.CashierId));
        var normalizedIdempotencyKey = NormalizeRequired(request.IdempotencyKey ?? string.Empty, nameof(request.IdempotencyKey));
        EnsureReasonCannotInjectRefundMarker(request.Reason);
        if (request.Amount <= 0m)
        {
            throw new InvalidOperationException("Amount must be greater than zero.");
        }

        logger?.LogInformation(
            "Voucher refund issue start store={StoreCode} amount={Amount} cashier={CashierId} idempotencyKeyPresent={IdempotencyKeyPresent} orderReference={OrderReference}",
            normalizedStoreCode,
            request.Amount,
            normalizedCashierId,
            !string.IsNullOrWhiteSpace(normalizedIdempotencyKey),
            request.OrderReference);
        var now = _timeProvider.GetUtcNow();
        var defaultExpiry = await ResolveDefaultExpiryAsync(normalizedStoreCode, now, cancellationToken);
        var voucher = await repository.CreateRefundVoucherAsync(
            new RefundVoucherCreateModel(
                normalizedStoreCode,
                decimal.Round(request.Amount, 2, MidpointRounding.AwayFromZero),
                normalizedCashierId,
                now,
                defaultExpiry,
                normalizedIdempotencyKey,
            request.OrderReference?.Trim(),
            request.Reason?.Trim()),
            cancellationToken);

        logger?.LogInformation(
            "Voucher refund issue completed store={StoreCode} voucher={VoucherCode} amount={Amount} remaining={RemainingAmount} expiresAt={ExpiresAt:O}",
            normalizedStoreCode,
            voucher.VoucherCode,
            voucher.Amount,
            voucher.RemainingAmount,
            voucher.ExpiredDate);
        return new StoreVoucherIssueRefundResponse(
            voucher.VoucherCode ?? string.Empty,
            voucher.Amount ?? decimal.Round(request.Amount, 2, MidpointRounding.AwayFromZero),
            voucher.RemainingAmount ?? decimal.Round(request.Amount, 2, MidpointRounding.AwayFromZero),
            voucher.Status ?? "1",
            voucher.ExpiredDate is null
                ? defaultExpiry
                : DateTime.SpecifyKind(voucher.ExpiredDate.Value, DateTimeKind.Utc));
    }

    public async Task<StoreVoucherIssueResponse> IssueAsync(
        StoreVoucherIssueRequest request,
        CancellationToken cancellationToken)
    {
        var normalizedStoreCode = NormalizeRequired(request.StoreCode ?? string.Empty, nameof(request.StoreCode));
        var normalizedCashierId = NormalizeRequired(request.CashierId, nameof(request.CashierId));
        var normalizedIdempotencyKey = NormalizeRequired(request.IdempotencyKey, nameof(request.IdempotencyKey));
        if (request.Amount <= 0m)
        {
            throw new InvalidOperationException("Amount must be greater than zero.");
        }

        var now = _timeProvider.GetUtcNow();
        var expiredAt = request.ExpiredAt ?? await ResolveDefaultExpiryAsync(normalizedStoreCode, now, cancellationToken);
        if (expiredAt <= now)
        {
            throw new InvalidOperationException("ExpiredAt must be in the future.");
        }

        logger?.LogInformation(
            "Voucher issue start store={StoreCode} amount={Amount} cashier={CashierId} customer={CustomerCode} idempotencyKeyPresent={IdempotencyKeyPresent} expiresAt={ExpiresAt:O}",
            normalizedStoreCode,
            request.Amount,
            normalizedCashierId,
            NormalizeOptional(request.CustomerCode),
            !string.IsNullOrWhiteSpace(normalizedIdempotencyKey),
            expiredAt);
        var voucher = await repository.CreateIssuedVoucherAsync(
            new IssuedVoucherCreateModel(
                normalizedStoreCode,
                decimal.Round(request.Amount, 2, MidpointRounding.AwayFromZero),
                normalizedCashierId,
                now,
                expiredAt,
                normalizedIdempotencyKey,
                NormalizeOptional(request.CustomerCode),
            request.Reason?.Trim()),
            cancellationToken);

        var amount = voucher.Amount ?? decimal.Round(request.Amount, 2, MidpointRounding.AwayFromZero);
        logger?.LogInformation(
            "Voucher issue completed store={StoreCode} voucher={VoucherCode} amount={Amount} remaining={RemainingAmount} expiresAt={ExpiresAt:O}",
            normalizedStoreCode,
            voucher.VoucherCode,
            amount,
            voucher.RemainingAmount,
            voucher.ExpiredDate);
        return new StoreVoucherIssueResponse(
            voucher.VoucherCode ?? string.Empty,
            amount,
            voucher.RemainingAmount ?? amount,
            voucher.Status ?? "1",
            voucher.ExpiredDate is null
                ? expiredAt
                : DateTime.SpecifyKind(voucher.ExpiredDate.Value, DateTimeKind.Utc),
            NormalizeOptional(voucher.StoreCode),
            NormalizeOptional(voucher.CustomerCode));
    }

    private static StoreVoucherDto Map(StoreVoucher voucher)
    {
        return new StoreVoucherDto(
            voucher.VoucherCode ?? string.Empty,
            voucher.StoreCode,
            voucher.VoucherType ?? 0,
            voucher.Amount ?? 0m,
            voucher.RemainingAmount ?? 0m,
            voucher.Status ?? string.Empty,
            voucher.ExpiredDate is null
                ? null
                : DateTime.SpecifyKind(voucher.ExpiredDate.Value, DateTimeKind.Utc),
            voucher.CustomerCode,
            voucher.DiscountRate ?? 0m,
            voucher.Remark);
    }

    private static string NormalizeRequired(string value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{paramName} is required.");
        }

        return value.Trim();
    }

    private static void EnsureReasonCannotInjectRefundMarker(string? reason)
    {
        if (!string.IsNullOrWhiteSpace(reason) &&
            reason.Contains(StoreVoucherRefundMarker.Prefix, StringComparison.OrdinalIgnoreCase))
        {
            // 退款归属标记只允许由服务端生成，不能让自由文本伪造另一个 claim 的凭据。
            throw new InvalidOperationException("Reason must not contain a refund idempotency marker.");
        }
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

public interface IStoreVoucherRepository
{
    Task<StoreVoucher?> FindAvailableAsync(
        string storeCode,
        string voucherCode,
        CancellationToken cancellationToken);

    Task<StoreVoucher> CreateRefundVoucherAsync(
        RefundVoucherCreateModel request,
        CancellationToken cancellationToken);

    Task<StoreVoucher> CreateIssuedVoucherAsync(
        IssuedVoucherCreateModel request,
        CancellationToken cancellationToken);
}

public sealed record RefundVoucherCreateModel(
    string StoreCode,
    decimal Amount,
    string CashierId,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiredAt,
    string IdempotencyKey,
    string? OrderReference,
    string? Reason);

public sealed record IssuedVoucherCreateModel(
    string? StoreCode,
    decimal Amount,
    string CashierId,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiredAt,
    string IdempotencyKey,
    string? CustomerCode,
    string? Reason);

internal static class StoreVoucherQueryableExtensions
{
    internal static ISugarQueryable<StoreVoucher> OrderByExactStoreFirst(
        this ISugarQueryable<StoreVoucher> query,
        string storeCode)
    {
        // SQL Server 不支持在 ORDER BY 中直接使用比较谓词，必须转换为可排序的标量值。
        return query.OrderBy(
            x => SqlFunc.IIF(x.StoreCode == storeCode, 1, 0),
            OrderByType.Desc);
    }
}

public sealed class SqlSugarStoreVoucherRepository(HbposSqlSugarContext dbContext) : IStoreVoucherRepository
{
    public async Task<StoreVoucher?> FindAvailableAsync(
        string storeCode,
        string voucherCode,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var voucher = await dbContext.PosmDb.Queryable<StoreVoucher>()
            .Where(x => x.VoucherCode == voucherCode)
            .Where(x => x.Status == "1")
            .Where(x => x.IsDelete == null || x.IsDelete == false)
            .Where(x => x.RemainingAmount != null && x.RemainingAmount > 0)
            .Where(x => x.ExpiredDate == null || x.ExpiredDate > now)
            .Where(x => x.StoreCode == null || x.StoreCode == string.Empty || x.StoreCode == storeCode)
            .OrderByExactStoreFirst(storeCode)
            .FirstAsync(cancellationToken);
        return voucher;
    }

    public async Task<StoreVoucher> CreateRefundVoucherAsync(
        RefundVoucherCreateModel request,
        CancellationToken cancellationToken)
    {
        return await CreateVoucherAsync(
            new VoucherCreatePersistenceModel(
                "RF",
                request.StoreCode,
                request.Amount,
                request.CashierId,
                request.CreatedAt,
                request.ExpiredAt,
                request.IdempotencyKey,
                null,
                request.OrderReference,
                request.Reason,
                BuildRefundRemark,
                BuildRefundIdempotencyMarker),
            cancellationToken);
    }

    public async Task<StoreVoucher> CreateIssuedVoucherAsync(
        IssuedVoucherCreateModel request,
        CancellationToken cancellationToken)
    {
        return await CreateVoucherAsync(
            new VoucherCreatePersistenceModel(
                "VC",
                request.StoreCode,
                request.Amount,
                request.CashierId,
                request.CreatedAt,
                request.ExpiredAt,
                request.IdempotencyKey,
                request.CustomerCode,
                null,
                request.Reason,
                BuildIssueRemark,
                BuildIssueIdempotencyMarker),
            cancellationToken);
    }

    internal static async Task RedeemInsideTransactionAsync(
        ISqlSugarClient db,
        string storeCode,
        string voucherCode,
        decimal amount,
        string? updatedBy,
        CancellationToken cancellationToken)
    {
        if (amount <= 0m)
        {
            throw new InvalidOperationException("Voucher redemption amount must be greater than zero.");
        }

        var now = DateTime.UtcNow;
        var voucher = await db.Queryable<StoreVoucher>()
            .Where(x => x.VoucherCode == voucherCode)
            .Where(x => x.StoreCode == null || x.StoreCode == string.Empty || x.StoreCode == storeCode)
            .OrderByExactStoreFirst(storeCode)
            .FirstAsync(cancellationToken)
            ?? throw new InvalidOperationException($"Voucher {voucherCode} was not found.");
        ValidateVoucherForRedemption(voucher, storeCode);

        var remaining = voucher.RemainingAmount ?? 0m;
        if (remaining < amount)
        {
            throw new InvalidOperationException($"Voucher {voucherCode} balance is not enough.");
        }

        // 余额扣减必须由数据库条件更新完成，避免并发请求先读后写覆盖彼此结果。
        var affectedRows = await db.Ado.ExecuteCommandAsync(
            """
            UPDATE [StoreVoucher]
            SET
                [RemainingAmount] = ROUND([RemainingAmount] - @Amount, 2),
                [Status] = CASE WHEN ROUND([RemainingAmount] - @Amount, 2) <= 0 THEN '0' ELSE '1' END,
                [UpdateTime] = @UpdateTime,
                [UpdateUser] = @UpdateUser
            WHERE [ID] = @ID
                AND [Status] = '1'
                AND ([IsDelete] IS NULL OR [IsDelete] = 0)
                AND ([ExpiredDate] IS NULL OR [ExpiredDate] > @UpdateTime)
                AND [RemainingAmount] IS NOT NULL
                AND [RemainingAmount] >= @Amount
            """,
            new List<SugarParameter>
            {
                new SugarParameter("@Amount", amount),
                new SugarParameter("@UpdateTime", now),
                new SugarParameter("@UpdateUser", updatedBy),
                new SugarParameter("@ID", voucher.ID)
            });
        if (affectedRows > 0)
        {
            return;
        }

        var latest = await db.Queryable<StoreVoucher>()
            .Where(x => x.ID == voucher.ID)
            .FirstAsync(cancellationToken)
            ?? throw new InvalidOperationException($"Voucher {voucherCode} was not found.");
        ValidateVoucherForRedemption(latest, storeCode);
        if ((latest.RemainingAmount ?? 0m) < amount)
        {
            throw new InvalidOperationException($"Voucher {voucherCode} balance is not enough.");
        }

        throw new InvalidOperationException($"Voucher {voucherCode} is unavailable.");
    }

    private static void ValidateVoucherForRedemption(StoreVoucher voucher, string storeCode)
    {
        if (voucher.IsDelete == true)
        {
            throw new InvalidOperationException("Voucher has been deleted.");
        }

        if (!string.Equals(voucher.Status?.Trim(), "1", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Voucher is not active.");
        }

        if (!string.IsNullOrWhiteSpace(voucher.StoreCode) &&
            !string.Equals(voucher.StoreCode.Trim(), storeCode.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Voucher does not belong to this store.");
        }

        if (voucher.ExpiredDate is not null && voucher.ExpiredDate.Value <= DateTime.UtcNow)
        {
            throw new InvalidOperationException("Voucher has expired.");
        }
    }

    private async Task<StoreVoucher> CreateVoucherAsync(
        VoucherCreatePersistenceModel request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            await dbContext.PosmDb.Ado.BeginTranAsync(IsolationLevel.Serializable);
            try
            {
                var existing = await FindVoucherByIdempotencyKeyAsync(request, cancellationToken);
                if (existing is not null)
                {
                    await dbContext.PosmDb.Ado.CommitTranAsync();
                    return existing;
                }

                var voucherCode = CreateVoucherCode(request.CodePrefix);
                var entity = new StoreVoucher();
                SetIfExists(entity, "VoucherCode", voucherCode);
                SetIfExists(entity, "StoreCode", request.StoreCode);
                SetIfExists(entity, "VoucherType", 3);
                SetIfExists(entity, "Amount", request.Amount);
                SetIfExists(entity, "RemainingAmount", request.Amount);
                SetIfExists(entity, "Status", "1");
                SetIfExists(entity, "ExpiredDate", request.ExpiredAt.UtcDateTime);
                // 备注承载发放来源与幂等标记，第一版不新增流水表时用于审计和重试去重。
                SetIfExists(entity, "Remark", request.BuildRemark(request));
                SetIfExists(entity, "DiscountRate", 0m);
                SetIfExists(entity, "IsDelete", false);
                SetIfExists(entity, "CustomerCode", request.CustomerCode);
                SetIfExists(entity, "CreatedBy", request.CashierId);
                SetIfExists(entity, "CreateUser", request.CashierId);
                SetIfExists(entity, "CreatedAt", request.CreatedAt.UtcDateTime);
                SetIfExists(entity, "CreateTime", request.CreatedAt.UtcDateTime);
                SetIfExists(entity, "UpdateTime", request.CreatedAt.UtcDateTime);
                SetIfExists(entity, "UpdatedAt", request.CreatedAt.UtcDateTime);
                await dbContext.PosmDb.Insertable(entity).ExecuteCommandAsync(cancellationToken);
                await dbContext.PosmDb.Ado.CommitTranAsync();
                return entity;
            }
            catch (SqlSugarException ex) when (LooksLikeDuplicateVoucherCode(ex))
            {
                await dbContext.PosmDb.Ado.RollbackTranAsync();
            }
            catch
            {
                await dbContext.PosmDb.Ado.RollbackTranAsync();
                throw;
            }
        }

        throw new InvalidOperationException("Unable to generate a unique voucher code.");
    }

    private async Task<StoreVoucher?> FindVoucherByIdempotencyKeyAsync(
        VoucherCreatePersistenceModel request,
        CancellationToken cancellationToken)
    {
        var marker = request.BuildIdempotencyMarker(request.IdempotencyKey);
        var query = dbContext.PosmDb.Queryable<StoreVoucher>()
            .Where(x => x.IsDelete == null || x.IsDelete == false);
        // 退款券以唯一的首段 marker 归属；普通发券保留既有备注格式，避免扩大本次安全变更。
        query = request.CodePrefix == "RF"
            ? query.Where(x => x.Remark != null && x.Remark.StartsWith(marker + StoreVoucherRefundMarker.Separator))
            : query.Where(x => x.Remark != null && x.Remark.Contains(marker));
        query = string.IsNullOrWhiteSpace(request.StoreCode)
            ? query.Where(x => x.StoreCode == null || x.StoreCode == string.Empty)
            : query.Where(x => x.StoreCode == request.StoreCode);
        return await query.FirstAsync(cancellationToken);
    }

    private static string CreateVoucherCode(string prefix)
    {
        return $"{prefix}{Guid.NewGuid():N}"[..14].ToUpperInvariant();
    }

    private static string BuildRefundRemark(VoucherCreatePersistenceModel request)
    {
        var parts = new List<string> { BuildRefundIdempotencyMarker(request.IdempotencyKey), "Refund voucher" };
        if (!string.IsNullOrWhiteSpace(request.OrderReference))
        {
            parts.Add($"Order {request.OrderReference}");
        }

        if (!string.IsNullOrWhiteSpace(request.Reason))
        {
            parts.Add(request.Reason!);
        }

        return string.Join(" | ", parts);
    }

    private static string BuildIssueRemark(VoucherCreatePersistenceModel request)
    {
        var parts = new List<string> { "Issued voucher" };
        parts.Add(BuildIssueIdempotencyMarker(request.IdempotencyKey));
        if (!string.IsNullOrWhiteSpace(request.Reason))
        {
            parts.Add(request.Reason!);
        }

        return string.Join(" | ", parts);
    }

    private static string BuildRefundIdempotencyMarker(string idempotencyKey)
    {
        return StoreVoucherRefundMarker.Create(idempotencyKey);
    }

    private static string BuildIssueIdempotencyMarker(string idempotencyKey)
    {
        var encodedKey = Convert.ToBase64String(Encoding.UTF8.GetBytes(idempotencyKey.Trim()));
        return $"IssueKey[{encodedKey}]";
    }

    private sealed record VoucherCreatePersistenceModel(
        string CodePrefix,
        string? StoreCode,
        decimal Amount,
        string CashierId,
        DateTimeOffset CreatedAt,
        DateTimeOffset ExpiredAt,
        string IdempotencyKey,
        string? CustomerCode,
        string? OrderReference,
        string? Reason,
        Func<VoucherCreatePersistenceModel, string> BuildRemark,
        Func<string, string> BuildIdempotencyMarker);

    private static void SetIfExists(StoreVoucher entity, string propertyName, object? value)
    {
        var property = typeof(StoreVoucher).GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
        if (property is null || !property.CanWrite)
        {
            return;
        }

        if (value is null)
        {
            property.SetValue(entity, null);
            return;
        }

        var targetType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        var normalizedValue = targetType.IsInstanceOfType(value)
            ? value
            : Convert.ChangeType(value, targetType, System.Globalization.CultureInfo.InvariantCulture);
        property.SetValue(entity, normalizedValue);
    }

    private static bool LooksLikeDuplicateVoucherCode(SqlSugarException exception)
    {
        return exception.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) ||
            exception.Message.Contains("unique", StringComparison.OrdinalIgnoreCase);
    }
}

public interface IStoreVoucherReservationService
{
    Task<StoreVoucherReservation?> GetAsync(string token, CancellationToken cancellationToken);

    Task<StoreVoucherReservation> ClaimAsync(
        string token,
        string storeCode,
        string voucherCode,
        decimal amount,
        string? consumedByReference,
        CancellationToken cancellationToken);

    Task<StoreVoucherReservation> ReserveAsync(
        string storeCode,
        string voucherCode,
        decimal requestedAmount,
        decimal currentRemainingAmount,
        CancellationToken cancellationToken);

    Task ConsumeAsync(string token, CancellationToken cancellationToken);

    Task<bool> ReleaseAsync(
        string token,
        string storeCode,
        string voucherCode,
        CancellationToken cancellationToken);
}

public sealed record StoreVoucherReservation(
    string Token,
    string StoreCode,
    string VoucherCode,
    decimal LockedAmount,
    DateTimeOffset ExpiresAt,
    decimal? RemainingAmountAfterLock = null);

public sealed class SqlSugarStoreVoucherReservationService(
    HbposSqlSugarContext dbContext,
    TimeProvider? timeProvider = null,
    ILogger<SqlSugarStoreVoucherReservationService>? logger = null) : IStoreVoucherReservationService
{
    private static readonly TimeSpan ReservationLifetime = TimeSpan.FromMinutes(5);
    private const string PendingStatus = "pending";
    private const string ClaimedStatus = "claimed";
    private const string ConsumedStatus = "consumed";
    private const string ReleasedStatus = "released";
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<StoreVoucherReservation?> GetAsync(string token, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedToken = NormalizeRequired(token, nameof(token));

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var entity = await dbContext.PosmDb.Queryable<StoreVoucherReservationEntity>()
            .Where(x => x.Token == normalizedToken)
            .Where(x => x.Status == PendingStatus)
            .Where(x => x.ExpiresAtUtc > now)
            .FirstAsync(cancellationToken);

        logger?.LogInformation(
            "Voucher reservation get token={ReservationToken} found={Found}",
            ShortToken(normalizedToken),
            entity is not null);
        return entity is null ? null : Map(entity);
    }

    public async Task<StoreVoucherReservation> ClaimAsync(
        string token,
        string storeCode,
        string voucherCode,
        decimal amount,
        string? consumedByReference,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        logger?.LogInformation(
            "Voucher reservation claim start token={ReservationToken} store={StoreCode} voucher={VoucherCode} amount={Amount} reference={Reference}",
            ShortToken(token),
            storeCode,
            voucherCode,
            amount,
            consumedByReference);
        var reservation = await ClaimInsideTransactionAsync(
            dbContext.PosmDb,
            token,
            storeCode,
            voucherCode,
            amount,
            consumedByReference,
            _timeProvider.GetUtcNow(),
            cancellationToken);
        logger?.LogInformation(
            "Voucher reservation claim completed token={ReservationToken} store={StoreCode} voucher={VoucherCode} lockedAmount={LockedAmount}",
            ShortToken(reservation.Token),
            reservation.StoreCode,
            reservation.VoucherCode,
            reservation.LockedAmount);
        return reservation;
    }

    public async Task<StoreVoucherReservation> ReserveAsync(
        string storeCode,
        string voucherCode,
        decimal requestedAmount,
        decimal currentRemainingAmount,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = currentRemainingAmount;
        var normalizedStoreCode = NormalizeRequired(storeCode, nameof(storeCode));
        var normalizedVoucherCode = NormalizeRequired(voucherCode, nameof(voucherCode));
        if (requestedAmount <= 0m)
        {
            throw new InvalidOperationException("Requested amount must be greater than zero.");
        }

        var db = dbContext.PosmDb;
        await db.Ado.BeginTranAsync(IsolationLevel.Serializable);
        try
        {
            logger?.LogInformation(
                "Voucher reservation reserve start store={StoreCode} voucher={VoucherCode} requestedAmount={RequestedAmount}",
                normalizedStoreCode,
                normalizedVoucherCode,
                requestedAmount);
            var now = _timeProvider.GetUtcNow();
            var nowUtc = now.UtcDateTime;
            var voucher = await db.Queryable<StoreVoucher>()
                .Where(x => x.VoucherCode == normalizedVoucherCode)
                .Where(x => x.Status == "1")
                .Where(x => x.IsDelete == null || x.IsDelete == false)
                .Where(x => x.RemainingAmount != null && x.RemainingAmount > 0)
                .Where(x => x.ExpiredDate == null || x.ExpiredDate > nowUtc)
                .Where(x => x.StoreCode == null || x.StoreCode == string.Empty || x.StoreCode == normalizedStoreCode)
                .OrderByExactStoreFirst(normalizedStoreCode)
                .FirstAsync(cancellationToken)
                ?? throw new InvalidOperationException("Voucher is unavailable.");
            var reservedAmount = await db.Queryable<StoreVoucherReservationEntity>()
                .Where(x => x.StoreCode == normalizedStoreCode)
                .Where(x => x.VoucherCode == normalizedVoucherCode)
                .Where(x => x.Status == PendingStatus)
                .Where(x => x.ExpiresAtUtc > nowUtc)
                .SumAsync(x => x.LockedAmount);
            var lockableAmount = Math.Min(
                requestedAmount,
                Math.Max(0m, (voucher.RemainingAmount ?? 0m) - reservedAmount));
            if (lockableAmount <= 0m)
            {
                throw new InvalidOperationException("Voucher has no remaining amount available to lock.");
            }

            var entity = new StoreVoucherReservationEntity
            {
                Token = Guid.NewGuid().ToString("N"),
                StoreCode = normalizedStoreCode,
                VoucherCode = normalizedVoucherCode,
                LockedAmount = decimal.Round(lockableAmount, 2, MidpointRounding.AwayFromZero),
                Status = PendingStatus,
                ExpiresAtUtc = now.Add(ReservationLifetime).UtcDateTime,
                CreatedAtUtc = nowUtc
            };
            await db.Insertable(entity).ExecuteCommandAsync(cancellationToken);
            await db.Ado.CommitTranAsync();
            var remainingAmountAfterLock = decimal.Round(
                Math.Max(0m, (voucher.RemainingAmount ?? 0m) - reservedAmount - entity.LockedAmount),
                2,
                MidpointRounding.AwayFromZero);
            logger?.LogInformation(
                "Voucher reservation reserve completed store={StoreCode} voucher={VoucherCode} lockedAmount={LockedAmount} reservedAmount={ReservedAmount} token={ReservationToken} expiresAt={ExpiresAt:O}",
                normalizedStoreCode,
                normalizedVoucherCode,
                entity.LockedAmount,
                reservedAmount,
                ShortToken(entity.Token),
                entity.ExpiresAtUtc);
            return Map(entity) with { RemainingAmountAfterLock = remainingAmountAfterLock };
        }
        catch (Exception ex)
        {
            await db.Ado.RollbackTranAsync();
            logger?.LogWarning(
                ex,
                "Voucher reservation reserve failed store={StoreCode} voucher={VoucherCode} requestedAmount={RequestedAmount}",
                normalizedStoreCode,
                normalizedVoucherCode,
                requestedAmount);
            throw;
        }
    }

    internal static async Task<StoreVoucherReservation> ClaimInsideTransactionAsync(
        ISqlSugarClient db,
        string token,
        string storeCode,
        string voucherCode,
        decimal amount,
        string? consumedByReference,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedToken = NormalizeRequired(token, nameof(token));
        var normalizedStoreCode = NormalizeRequired(storeCode, nameof(storeCode));
        var normalizedVoucherCode = NormalizeRequired(voucherCode, nameof(voucherCode));
        if (amount <= 0m)
        {
            throw new InvalidOperationException("Voucher reservation claim amount must be greater than zero.");
        }

        var nowUtc = now.UtcDateTime;
        var normalizedReference = string.IsNullOrWhiteSpace(consumedByReference)
            ? null
            : consumedByReference.Trim();

        // claim 是 token 的一次性闸门：只有 pending 且未过期的匹配记录能被原子更新。
        var affectedRows = await db.Updateable<StoreVoucherReservationEntity>()
            .SetColumns(x => x.Status == ClaimedStatus)
            .SetColumns(x => x.ConsumedAtUtc == nowUtc)
            .SetColumns(x => x.ConsumedByReference == normalizedReference)
            .Where(x => x.Token == normalizedToken)
            .Where(x => x.Status == PendingStatus)
            .Where(x => x.ExpiresAtUtc > nowUtc)
            .Where(x => x.StoreCode == normalizedStoreCode)
            .Where(x => x.VoucherCode == normalizedVoucherCode)
            .Where(x => x.LockedAmount >= amount)
            .ExecuteCommandAsync(cancellationToken);
        if (affectedRows != 1)
        {
            throw new InvalidOperationException("Voucher reservation token is invalid, expired, or already claimed.");
        }

        var entity = await db.Queryable<StoreVoucherReservationEntity>()
            .Where(x => x.Token == normalizedToken)
            .FirstAsync(cancellationToken)
            ?? throw new InvalidOperationException("Voucher reservation token is invalid, expired, or already claimed.");
        return Map(entity);
    }

    public async Task ConsumeAsync(string token, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedToken = NormalizeRequired(token, nameof(token));

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        await dbContext.PosmDb.Updateable<StoreVoucherReservationEntity>()
            .SetColumns(x => x.Status == ConsumedStatus)
            .SetColumns(x => x.ConsumedAtUtc == now)
            .Where(x => x.Token == normalizedToken)
            .Where(x => x.Status != ConsumedStatus)
            .ExecuteCommandAsync(cancellationToken);
        logger?.LogInformation(
            "Voucher reservation consumed token={ReservationToken}",
            ShortToken(normalizedToken));
    }

    public async Task<bool> ReleaseAsync(
        string token,
        string storeCode,
        string voucherCode,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedToken = NormalizeRequired(token, nameof(token));
        var normalizedStoreCode = NormalizeRequired(storeCode, nameof(storeCode));
        var normalizedVoucherCode = NormalizeRequired(voucherCode, nameof(voucherCode));

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        // release 只回退仍在 pending 的匹配 reservation，避免误释放已核销或其他门店的锁定。
        var affectedRows = await dbContext.PosmDb.Updateable<StoreVoucherReservationEntity>()
            .SetColumns(x => x.Status == ReleasedStatus)
            .SetColumns(x => x.ConsumedAtUtc == now)
            .Where(x => x.Token == normalizedToken)
            .Where(x => x.Status == PendingStatus)
            .Where(x => x.StoreCode == normalizedStoreCode)
            .Where(x => x.VoucherCode == normalizedVoucherCode)
            .ExecuteCommandAsync(cancellationToken);
        // 同一锁定已被本次或上一次相同请求释放时，安全地确认成功；绝不将已核销、错门店或错券码视为已释放。
        var released = affectedRows == 1 || await dbContext.PosmDb.Queryable<StoreVoucherReservationEntity>()
            .Where(x => x.Token == normalizedToken)
            .Where(x => x.StoreCode == normalizedStoreCode)
            .Where(x => x.VoucherCode == normalizedVoucherCode)
            .Where(x => x.Status == ReleasedStatus)
            .AnyAsync();
        logger?.LogInformation(
            "Voucher reservation release token={ReservationToken} store={StoreCode} voucher={VoucherCode} released={Released}",
            ShortToken(normalizedToken),
            normalizedStoreCode,
            normalizedVoucherCode,
            released);
        return released;
    }

    private static StoreVoucherReservation Map(StoreVoucherReservationEntity entity)
    {
        return new StoreVoucherReservation(
            entity.Token,
            entity.StoreCode,
            entity.VoucherCode,
            entity.LockedAmount,
            new DateTimeOffset(DateTime.SpecifyKind(entity.ExpiresAtUtc, DateTimeKind.Utc)));
    }

    private static string NormalizeRequired(string value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{paramName} is required.");
        }

        return value.Trim();
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

[SugarTable("StoreVoucherReservation")]
public sealed class StoreVoucherReservationEntity
{
    [SugarColumn(IsPrimaryKey = true, Length = 32)]
    public string Token { get; set; } = string.Empty;

    [SugarColumn(Length = 50)]
    public string StoreCode { get; set; } = string.Empty;

    [SugarColumn(Length = 50)]
    public string VoucherCode { get; set; } = string.Empty;

    public decimal LockedAmount { get; set; }

    [SugarColumn(Length = 20)]
    public string Status { get; set; } = string.Empty;

    public DateTime ExpiresAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    [SugarColumn(IsNullable = true)]
    public DateTime? ConsumedAtUtc { get; set; }

    [SugarColumn(Length = 100, IsNullable = true)]
    public string? ConsumedByReference { get; set; }
}
