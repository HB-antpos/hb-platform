using System.Text.Json;
using BlazorApp.Shared.Models.POSM;
using Hbpos.Api.Data;
using Hbpos.Contracts.Orders;
using Microsoft.Extensions.Logging;
using SqlSugar;
using System.Diagnostics;

namespace Hbpos.Api.Services;

public interface IOrderSyncService
{
    Task<OrderSyncResponse> SyncAsync(OrderSyncRequest request, CancellationToken cancellationToken);
}

public sealed class OrderSyncService(
    IOrderRepository repository,
    IOrderSyncPlanner planner,
    IStoreVoucherReservationService reservationService,
    IStoreTimeZoneResolver storeTimeZoneResolver,
    ILogger<OrderSyncService>? logger = null,
    ICardTenderOrderVerifier? cardTenderVerifier = null) : IOrderSyncService
{
    public async Task<OrderSyncResponse> SyncAsync(
        OrderSyncRequest request,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        Log(
            $"service start orderGuid={request.OrderGuid:D} store={request.StoreCode} device={request.DeviceCode} " +
            $"lines={request.Lines.Count} payments={request.Payments.Count}");
        if (await repository.ExistsAsync(request.OrderGuid, cancellationToken))
        {
            // 普通订单（无共享挂单来源）不查询关联表；只有带 source 的重试才读稳定 disposition。
            var disposition = request.HeldOrderSource is null
                ? HeldOrderDisposition.None
                : await repository.GetHeldOrderDispositionAsync(
                    request.OrderGuid,
                    cancellationToken);
            Log($"service completed orderGuid={request.OrderGuid:D} status=already-synced elapsedMs={stopwatch.ElapsedMilliseconds}");
            return new OrderSyncResponse(
                request.OrderGuid,
                true,
                true,
                "AlreadySynced",
                disposition);
        }

        var voucherRedemptions = await BuildVoucherRedemptionsAsync(request, cancellationToken);
        Log($"voucher redemptions prepared orderGuid={request.OrderGuid:D} count={voucherRedemptions.Count}");
        var storeTimeZone = await storeTimeZoneResolver.ResolveAsync(request.StoreCode, cancellationToken);
        var plan = planner.CreatePlan(request, storeTimeZone);
        Log(
            $"plan created orderGuid={request.OrderGuid:D} saleLines={plan.Lines.Count} payments={plan.Payments.Count} " +
            $"bankTransactions={plan.BankTransactions.Count} returns={plan.ReturnRecords.Count} " +
            $"timeZone={storeTimeZone.Id} orderTime={plan.Order.OrderTime:yyyy-MM-dd HH:mm:ss}");
        var insertResult = await repository.InsertAsync(
            plan,
            voucherRedemptions,
            request.HeldOrderSource,
            cancellationToken);
        if (!insertResult.Inserted)
        {
            // 并发重复上传时不再消费预占令牌，直接向上层返回已同步结果。
            var disposition = request.HeldOrderSource is null
                ? HeldOrderDisposition.None
                : await repository.GetHeldOrderDispositionAsync(
                    request.OrderGuid,
                    cancellationToken);
            Log($"service completed orderGuid={request.OrderGuid:D} status=already-synced-after-insert elapsedMs={stopwatch.ElapsedMilliseconds}");
            return new OrderSyncResponse(
                request.OrderGuid,
                true,
                true,
                "AlreadySynced",
                disposition);
        }

        // disposition 与订单写入同事务由 AssociateAsync 算出，提交后不再查 SQL。
        var heldDisposition = insertResult.HeldDisposition;
        Log(
            $"repository insert completed orderGuid={request.OrderGuid:D} " +
            $"heldDisposition={heldDisposition}");

        foreach (var redemption in voucherRedemptions)
        {
            await reservationService.ConsumeAsync(redemption.ReservationToken, cancellationToken);
            Log(
                $"voucher reservation consumed orderGuid={request.OrderGuid:D} token={ShortToken(redemption.ReservationToken)} " +
                $"voucher={redemption.VoucherCode} amount={redemption.Amount}");
        }

        // 卡付款核对放在订单与代金券都提交之后：核对不通过只登记异常，不影响本次同步结果（见 ICardTenderOrderVerifier）。
        if (cardTenderVerifier is not null && request.Payments.Any(payment => payment.Method == PaymentMethodKind.Card))
        {
            try
            {
                await cardTenderVerifier.VerifyAsync(request);
            }
            catch (Exception ex)
            {
                // 订单已提交；核对失败不能让客户端把已入库订单当成失败重试。
                logger?.LogError(ex, "OrderSyncService card tender verification failed orderGuid={OrderGuid}", request.OrderGuid);
            }
        }

        Log($"service completed orderGuid={request.OrderGuid:D} status=synced elapsedMs={stopwatch.ElapsedMilliseconds}");
        return new OrderSyncResponse(
            request.OrderGuid,
            true,
            false,
            "Synced",
            heldDisposition);
    }

    private async Task<IReadOnlyList<StoreVoucherRedemptionCommit>> BuildVoucherRedemptionsAsync(
        OrderSyncRequest request,
        CancellationToken cancellationToken)
    {
        var voucherPayments = request.Payments
            .Where(payment => payment.Method == PaymentMethodKind.Voucher)
            .ToList();
        if (voucherPayments.Count == 0)
        {
            return [];
        }

        var redemptions = new List<StoreVoucherRedemptionCommit>(voucherPayments.Count);
        var seenTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var payment in voucherPayments)
        {
            if (payment.Amount < 0m)
            {
                Log($"voucher redemption skipped orderGuid={request.OrderGuid:D} reason=refund-payment paymentGuid={payment.PaymentGuid:D} amount={payment.Amount}");
                continue;
            }

            if (payment.Amount == 0m)
            {
                throw new InvalidOperationException("Voucher payment amount must not be zero.");
            }

            var voucherCode = NormalizeRequired(payment.Reference, "Voucher payment reference is required.");
            var reservationToken = NormalizeRequired(payment.ReservationToken, "Voucher reservation token is required.");
            if (!seenTokens.Add(reservationToken))
            {
                throw new InvalidOperationException("Voucher reservation token cannot be reused in the same order.");
            }

            var reservation = await reservationService.GetAsync(reservationToken, cancellationToken)
                ?? throw new InvalidOperationException("Voucher reservation token is invalid or expired.");
            if (!string.Equals(reservation.StoreCode, request.StoreCode.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Voucher reservation store does not match the order store.");
            }

            if (!string.Equals(reservation.VoucherCode, voucherCode, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Voucher reservation does not match the voucher code.");
            }

            if (reservation.LockedAmount < payment.Amount)
            {
                throw new InvalidOperationException("Voucher payment amount exceeds the locked amount.");
            }

            redemptions.Add(new StoreVoucherRedemptionCommit(voucherCode, reservationToken, payment.Amount));
        }

        return redemptions;
    }

    private void Log(string message)
    {
        logger?.LogInformation("OrderSyncService {Message}", message);
    }

    private static string ShortToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return "<null>";
        }

        var trimmed = token.Trim();
        return trimmed.Length <= 8 ? trimmed : $"{trimmed[..8]}...";
    }

    private static string NormalizeRequired(string? value, string message)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(message);
        }

        return value.Trim();
    }
}

public interface IOrderRepository
{
    Task<bool> ExistsAsync(Guid orderGuid, CancellationToken cancellationToken);

    Task<OrderInsertResult> InsertAsync(
        OrderSyncPlan plan,
        IReadOnlyList<StoreVoucherRedemptionCommit> voucherRedemptions,
        HeldOrderSourceDto? heldOrderSource,
        CancellationToken cancellationToken);

    Task<HeldOrderDisposition> GetHeldOrderDispositionAsync(
        Guid orderGuid,
        CancellationToken cancellationToken);
}

/// <summary>
/// 订单插入的内部结果：Inserted 表示本调用真正写入了订单；
/// HeldDisposition 与订单写入同事务计算，提交后无需再查关联表。
/// </summary>
public sealed record OrderInsertResult(
    bool Inserted,
    HeldOrderDisposition HeldDisposition);

public sealed record StoreVoucherRedemptionCommit(
    string VoucherCode,
    string ReservationToken,
    decimal Amount);

public sealed class SqlSugarOrderRepository(
    HbposSqlSugarContext dbContext,
    ILogger<SqlSugarOrderRepository> logger) : IOrderRepository
{
    private const int LogPreviewMaxLength = 80;

    public async Task<bool> ExistsAsync(Guid orderGuid, CancellationToken cancellationToken)
    {
        var orderGuidText = orderGuid.ToString("D");
        // 关键逻辑：这是上传入口的第一道幂等判断，命中即直接返回 AlreadySynced 且根本不进事务。
        // 连接级 IsWithNoLockQuery 会让它脏读到另一个事务尚未提交的订单行，客户端据此把订单
        // 标记为 Synced；对方事务一旦回滚，这笔销售就永久丢失。
        // 事务外持有 UPDLOCK 没有意义（语句结束即释放），这里只需显式回到已提交读。
        return await dbContext.PosmDb.Queryable<SalesOrder>()
            .With("WITH(READCOMMITTED)")
            .AnyAsync(x => x.OrderGuid == orderGuidText, cancellationToken);
    }

    public async Task<OrderInsertResult> InsertAsync(
        OrderSyncPlan plan,
        IReadOnlyList<StoreVoucherRedemptionCommit> voucherRedemptions,
        HeldOrderSourceDto? heldOrderSource,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        logger.LogInformation(
            "OrderSyncRepository insert start OrderGuid={OrderGuid} Lines={LineCount} Payments={PaymentCount} BankTransactions={BankTransactionCount} Returns={ReturnCount} Vouchers={VoucherCount}",
            plan.Order.OrderGuid,
            plan.Lines.Count,
            plan.Payments.Count,
            plan.BankTransactions.Count,
            plan.ReturnRecords.Count,
            voucherRedemptions.Count);
        var db = dbContext.PosmDb;
        if (plan.ReturnRecords.Count > 0 || heldOrderSource is not null)
        {
            await SalesReturnRecordPersistence.BeginSerializableTransactionAsync(db);
        }
        else
        {
            await db.Ado.BeginTranAsync();
        }

        try
        {
            // 关键逻辑：连接级 IsWithNoLockQuery 会给这条读加 WITH(NOLOCK)，而表提示会覆盖
            // 会话隔离级别，使外层 Serializable 事务对幂等判断完全失效。一旦脏读到另一个
            // 事务尚未提交的订单行，就会返回 AlreadySynced；客户端据此把订单标记为 Synced，
            // 而对方事务回滚后这笔销售会永久丢失——SyncQueue 只重试 Pending/Failed，
            // 且全仓没有任何上传后对账。
            // UPDLOCK 以更新锁串行化同一 OrderGuid 的并发判断，HOLDLOCK 补范围锁，
            // 防止另一事务在判断与插入之间插入同 OrderGuid 的行。
            var existing = await db.Queryable<SalesOrder>()
                .With("WITH(UPDLOCK, HOLDLOCK)")
                .AnyAsync(x => x.OrderGuid == plan.Order.OrderGuid, cancellationToken);

            if (existing)
            {
                logger.LogInformation(
                    "OrderSyncRepository insert skipped OrderGuid={OrderGuid} Reason=already-exists ElapsedMs={ElapsedMs}",
                    plan.Order.OrderGuid,
                    stopwatch.ElapsedMilliseconds);
                await db.Ado.CommitTranAsync();
                return new OrderInsertResult(false, HeldOrderDisposition.None);
            }

            if (voucherRedemptions.Count > 0)
            {
                // 代金券余额扣减与订单写入放在同一事务内，避免部分成功。
                await ApplyVoucherPaymentsAsync(db, plan, voucherRedemptions, logger, cancellationToken);
                logger.LogInformation(
                    "OrderSyncRepository voucher apply completed OrderGuid={OrderGuid} VoucherCount={VoucherCount}",
                    plan.Order.OrderGuid,
                    voucherRedemptions.Count);
            }

            await db.Insertable(plan.Order).ExecuteCommandAsync(cancellationToken);
            if (plan.Lines.Count > 0)
            {
                try
                {
                    await db.Insertable(plan.Lines.ToList()).ExecuteCommandAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    LogSalesOrderDetailInsertFailure(logger, ex, plan);
                    throw;
                }
            }

            if (plan.Payments.Count > 0)
            {
                await db.Insertable(plan.Payments.ToList()).ExecuteCommandAsync(cancellationToken);
            }

            if (plan.BankTransactions.Count > 0)
            {
                await db.Insertable(plan.BankTransactions.ToList()).ExecuteCommandAsync(cancellationToken);
            }

            var returnRecordPreparation = await SalesReturnRecordPersistence.PrepareValidatedInsertAsync(
                db,
                plan.ReturnRecords,
                cancellationToken,
                plan.Payments);
            if (returnRecordPreparation.RecordsToInsert.Count > 0)
            {
                await db.Insertable(returnRecordPreparation.RecordsToInsert.ToList()).ExecuteCommandAsync(cancellationToken);
            }

            var heldDisposition = HeldOrderDisposition.None;
            if (heldOrderSource is not null)
            {
                heldDisposition = await SharedHeldOrderAssociationStore.AssociateAsync(
                    db,
                    Guid.Parse(plan.Order.OrderGuid!),
                    plan.Order.BranchCode ?? string.Empty,
                    heldOrderSource,
                    DateTimeOffset.UtcNow,
                    cancellationToken);
                logger.LogInformation(
                    "OrderSyncRepository held association completed OrderGuid={OrderGuid} HoldGuid={HoldGuid} Disposition={Disposition}",
                    plan.Order.OrderGuid,
                    heldOrderSource.HoldGuid,
                    heldDisposition);
            }

            await db.Ado.CommitTranAsync();
            logger.LogInformation(
                "OrderSyncRepository insert completed OrderGuid={OrderGuid} Lines={LineCount} Payments={PaymentCount} BankTransactions={BankTransactionCount} Returns={ReturnCount} ElapsedMs={ElapsedMs}",
                plan.Order.OrderGuid,
                plan.Lines.Count,
                plan.Payments.Count,
                plan.BankTransactions.Count,
                plan.ReturnRecords.Count,
                stopwatch.ElapsedMilliseconds);
            return new OrderInsertResult(true, heldDisposition);
        }
        catch (Exception ex)
        {
            await db.Ado.RollbackTranAsync();
            logger.LogError(
                ex,
                "OrderSyncRepository insert failed OrderGuid={OrderGuid} ElapsedMs={ElapsedMs}",
                plan.Order.OrderGuid,
                stopwatch.ElapsedMilliseconds);
            throw;
        }
    }

    public async Task<HeldOrderDisposition> GetHeldOrderDispositionAsync(
        Guid orderGuid,
        CancellationToken cancellationToken)
    {
        return await SharedHeldOrderAssociationStore.GetDispositionAsync(
            dbContext.PosmDb,
            orderGuid,
            cancellationToken);
    }

    private static async Task ApplyVoucherPaymentsAsync(
        ISqlSugarClient db,
        OrderSyncPlan plan,
        IReadOnlyList<StoreVoucherRedemptionCommit> voucherRedemptions,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        foreach (var redemption in voucherRedemptions)
        {
            var storeCode = plan.Order.BranchCode ?? string.Empty;
            logger.LogInformation(
                "OrderSyncRepository voucher claim start OrderGuid={OrderGuid} Voucher={VoucherCode} Token={ReservationToken} Amount={Amount}",
                plan.Order.OrderGuid,
                redemption.VoucherCode,
                ShortToken(redemption.ReservationToken),
                redemption.Amount);
            await SqlSugarStoreVoucherReservationService.ClaimInsideTransactionAsync(
                db,
                redemption.ReservationToken,
                storeCode,
                redemption.VoucherCode,
                redemption.Amount,
                plan.Order.OrderGuid,
                DateTimeOffset.UtcNow,
                cancellationToken);
            await SqlSugarStoreVoucherRepository.RedeemInsideTransactionAsync(
                db,
                storeCode,
                redemption.VoucherCode,
                redemption.Amount,
                plan.Order.CashierId ?? plan.Order.CashierName,
                cancellationToken);
            logger.LogInformation(
                "OrderSyncRepository voucher redeemed OrderGuid={OrderGuid} Voucher={VoucherCode} Token={ReservationToken} Amount={Amount}",
                plan.Order.OrderGuid,
                redemption.VoucherCode,
                ShortToken(redemption.ReservationToken),
                redemption.Amount);
        }
    }

    private static string ShortToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return "<null>";
        }

        var trimmed = token.Trim();
        return trimmed.Length <= 8 ? trimmed : $"{trimmed[..8]}...";
    }

    internal static IReadOnlyList<SalesOrderDetailInsertDiagnostic> BuildSalesOrderDetailDiagnostics(
        IReadOnlyList<SalesOrderDetail> lines)
    {
        return lines.Select(line => new SalesOrderDetailInsertDiagnostic(
                line.OrderDetailGuid,
                BuildTextDiagnostic(line.ProductCode),
                BuildTextDiagnostic(line.ReferenceGUID),
                BuildTextDiagnostic(line.ProductName),
                BuildTextDiagnostic(line.Barcode),
                BuildTextDiagnostic(line.Remark),
                BuildTextDiagnostic(line.CreatedBy),
                BuildTextDiagnostic(line.UpdatedBy)))
            .ToList();
    }

    internal static string BuildSalesOrderDetailDiagnosticsText(IReadOnlyList<SalesOrderDetail> lines)
    {
        return JsonSerializer.Serialize(BuildSalesOrderDetailDiagnostics(lines));
    }

    private static void LogSalesOrderDetailInsertFailure(
        ILogger logger,
        Exception exception,
        OrderSyncPlan plan)
    {
        // 明细插入失败时保留字段长度和安全预览，便于定位数据库触发器字符串解析问题。
        logger.LogError(
            exception,
            "Sales order detail insert failed. OrderGuid={OrderGuid} LineCount={LineCount} Stage={Stage} Lines={LineDiagnostics}",
            plan.Order.OrderGuid,
            plan.Lines.Count,
            "sales_order_detail_insert",
            BuildSalesOrderDetailDiagnosticsText(plan.Lines));
    }

    private static TextDiagnostic BuildTextDiagnostic(string? value)
    {
        return new TextDiagnostic(value?.Length ?? 0, Preview(value));
    }

    private static string Preview(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var preview = value.Length <= LogPreviewMaxLength
            ? value
            : value[..LogPreviewMaxLength];
        return RemoveLogControlChars(preview);
    }

    private static string RemoveLogControlChars(string value)
    {
        return new string(value.Select(ch => char.IsControl(ch) ? ' ' : ch).ToArray());
    }

}

internal sealed record TextDiagnostic(int Length, string Preview);

internal sealed record SalesOrderDetailInsertDiagnostic(
    string? OrderDetailGuid,
    TextDiagnostic ProductCode,
    TextDiagnostic ReferenceGUID,
    TextDiagnostic ProductName,
    TextDiagnostic Barcode,
    TextDiagnostic Remark,
    TextDiagnostic CreatedBy,
    TextDiagnostic UpdatedBy);
