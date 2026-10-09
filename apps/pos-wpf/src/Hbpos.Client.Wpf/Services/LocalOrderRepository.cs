using Hbpos.Client.Wpf.Models;
using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.Orders;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.IO;

namespace Hbpos.Client.Wpf.Services;

public interface ILocalOrderRepository
{
    Task SavePendingOrderAsync(LocalOrder order, CancellationToken cancellationToken = default);

    /// <summary>
    /// 持久化待上传订单；当订单来自共享挂单取单时，heldOrder 必须携带 durable claim
    /// 事实，并与 LocalOrder/SyncQueue 同一事务写入：held-order source、完成对应 claim、
    /// 消费本地挂单。claim 竞态/终态差异绝不回滚订单，只有来源行写失败等数据损坏才
    /// 整体回滚，防止丢正式订单。
    /// </summary>
    Task SavePendingOrderWithHeldSourceAsync(
        LocalOrder order,
        LocalHeldOrderCompletionContext heldOrder,
        CancellationToken cancellationToken = default)
    {
        // 默认实现保持旧行为（不写来源）；需要原子来源/claim/消费的仓库必须显式实现。
        return SavePendingOrderAsync(order, cancellationToken);
    }

    Task UpdatePaymentReferenceAsync(
        Guid paymentGuid,
        string? reference,
        CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    Task<IReadOnlyList<LocalOrderSummary>> GetRecentOrdersAsync(int take = 50, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LocalOrderSummary>> GetRecentOrdersAsync(
        LocalOrderHistoryQuery query,
        int take = 50,
        CancellationToken cancellationToken = default);

    Task<LocalOrder?> GetOrderAsync(Guid orderGuid, CancellationToken cancellationToken = default);

    /// <summary>
    /// 读取本机保存的、引用指定原单的退货单。退货查单用它补上服务端还没收到（或降级到本地时根本查不到）的退货，
    /// unsyncedOnly 为 true 时只返回尚未同步成功的订单（服务端记录已覆盖 Synced 的）。
    /// </summary>
    Task<IReadOnlyList<LocalOrder>> GetReturnOrdersForOriginalAsync(
        Guid originalOrderGuid,
        bool unsyncedOnly,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<LocalOrder>>([]);
}

public sealed class LocalOrderRepository(LocalSqliteStore store) : ILocalOrderRepository
{
    public async Task SavePendingOrderAsync(LocalOrder order, CancellationToken cancellationToken = default)
    {
        await SavePendingOrderCoreAsync(order, heldOrder: null, cancellationToken);
    }

    public async Task SavePendingOrderWithHeldSourceAsync(
        LocalOrder order,
        LocalHeldOrderCompletionContext heldOrder,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(heldOrder);
        await SavePendingOrderCoreAsync(order, heldOrder, cancellationToken);
    }

    private async Task SavePendingOrderCoreAsync(
        LocalOrder order,
        LocalHeldOrderCompletionContext? heldOrder,
        CancellationToken cancellationToken)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO LocalOrders
                (OrderGuid, StoreCode, DeviceCode, CashierId, CashierName, SoldAt, TotalAmount, DiscountAmount, ActualAmount, TenderedAmount, ChangeAmount, SyncStatus)
                VALUES ($OrderGuid, $StoreCode, $DeviceCode, $CashierId, $CashierName, $SoldAt, $TotalAmount, $DiscountAmount, $ActualAmount, $TenderedAmount, $ChangeAmount, 'Pending');
                """;
            command.Parameters.AddWithValue("$OrderGuid", order.OrderGuid.ToString());
            command.Parameters.AddWithValue("$StoreCode", order.StoreCode);
            command.Parameters.AddWithValue("$DeviceCode", order.DeviceCode);
            command.Parameters.AddWithValue("$CashierId", order.CashierId);
            command.Parameters.AddWithValue("$CashierName", order.CashierName);
            command.Parameters.AddWithValue("$SoldAt", order.SoldAt.ToString("O"));
            command.Parameters.AddWithValue("$TotalAmount", order.TotalAmount);
            command.Parameters.AddWithValue("$DiscountAmount", order.DiscountAmount);
            command.Parameters.AddWithValue("$ActualAmount", order.ActualAmount);
            // 收款/找零信息允许为空，保持历史订单与非现金场景兼容。
            command.Parameters.AddWithValue("$TenderedAmount", order.TenderedAmount ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$ChangeAmount", order.ChangeAmount ?? (object)DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var line in order.Lines)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO LocalOrderLines
                (OrderLineGuid, OrderGuid, ProductCode, ReferenceCode, DisplayName, LookupCode, ItemNumber, Quantity, UnitPrice, DiscountAmount, ActualAmount, PriceSource, Kind, ReturnSourceKey, OriginalOrderGuid, OriginalOrderDetailGuid)
                VALUES ($OrderLineGuid, $OrderGuid, $ProductCode, $ReferenceCode, $DisplayName, $LookupCode, $ItemNumber, $Quantity, $UnitPrice, $DiscountAmount, $ActualAmount, $PriceSource, $Kind, $ReturnSourceKey, $OriginalOrderGuid, $OriginalOrderDetailGuid);
                """;
            command.Parameters.AddWithValue("$OrderLineGuid", line.OrderLineGuid.ToString());
            command.Parameters.AddWithValue("$OrderGuid", order.OrderGuid.ToString());
            command.Parameters.AddWithValue("$ProductCode", line.ProductCode);
            command.Parameters.AddWithValue("$ReferenceCode", (object?)line.ReferenceCode ?? DBNull.Value);
            command.Parameters.AddWithValue("$DisplayName", line.DisplayName);
            command.Parameters.AddWithValue("$LookupCode", line.LookupCode);
            command.Parameters.AddWithValue("$ItemNumber", (object?)line.ItemNumber ?? DBNull.Value);
            command.Parameters.AddWithValue("$Quantity", line.Quantity);
            command.Parameters.AddWithValue("$UnitPrice", line.UnitPrice);
            command.Parameters.AddWithValue("$DiscountAmount", line.DiscountAmount);
            command.Parameters.AddWithValue("$ActualAmount", line.ActualAmount);
            command.Parameters.AddWithValue("$PriceSource", (int)line.PriceSource);
            command.Parameters.AddWithValue("$Kind", (int)line.Kind);
            command.Parameters.AddWithValue("$ReturnSourceKey", (object?)line.ReturnSourceKey ?? DBNull.Value);
            command.Parameters.AddWithValue("$OriginalOrderGuid", line.OriginalOrderGuid?.ToString() ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$OriginalOrderDetailGuid", line.OriginalOrderDetailGuid?.ToString() ?? (object)DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var payment in order.Payments)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO LocalPayments (PaymentGuid, OrderGuid, Method, Amount, Reference, IdempotencyKey)
                VALUES ($PaymentGuid, $OrderGuid, $Method, $Amount, $Reference, $IdempotencyKey);
                """;
            command.Parameters.AddWithValue("$PaymentGuid", payment.PaymentGuid.ToString());
            command.Parameters.AddWithValue("$OrderGuid", order.OrderGuid.ToString());
            command.Parameters.AddWithValue("$Method", (int)payment.Method);
            command.Parameters.AddWithValue("$Amount", payment.Amount);
            command.Parameters.AddWithValue("$Reference", (object?)payment.Reference ?? DBNull.Value);
            command.Parameters.AddWithValue("$IdempotencyKey", (object?)payment.IdempotencyKey ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);

            foreach (var cardTransaction in payment.CardTransactions ?? [])
            {
                await using var cardCommand = connection.CreateCommand();
                cardCommand.Transaction = transaction;
                cardCommand.CommandText = """
                    INSERT INTO LocalCardTransactions
                    (Id, PaymentGuid, OrderGuid, Processor, TxnRef, AuthCode, CardType, CardBin, MaskedCardNumber, MerchantId, ResponseCode, ResponseText, Stan, BankDateTime, Amount, ReceiptText, RefundReference)
                    VALUES ($Id, $PaymentGuid, $OrderGuid, $Processor, $TxnRef, $AuthCode, $CardType, $CardBin, $MaskedCardNumber, $MerchantId, $ResponseCode, $ResponseText, $Stan, $BankDateTime, $Amount, $ReceiptText, $RefundReference);
                    """;
                cardCommand.Parameters.AddWithValue("$Id", Guid.NewGuid().ToString());
                cardCommand.Parameters.AddWithValue("$PaymentGuid", payment.PaymentGuid.ToString());
                cardCommand.Parameters.AddWithValue("$OrderGuid", order.OrderGuid.ToString());
                cardCommand.Parameters.AddWithValue("$Processor", cardTransaction.Processor);
                cardCommand.Parameters.AddWithValue("$TxnRef", (object?)cardTransaction.TxnRef ?? DBNull.Value);
                cardCommand.Parameters.AddWithValue("$AuthCode", (object?)cardTransaction.AuthCode ?? DBNull.Value);
                cardCommand.Parameters.AddWithValue("$CardType", (object?)cardTransaction.CardType ?? DBNull.Value);
                cardCommand.Parameters.AddWithValue("$CardBin", (object?)cardTransaction.CardBin ?? DBNull.Value);
                cardCommand.Parameters.AddWithValue("$MaskedCardNumber", (object?)cardTransaction.MaskedCardNumber ?? DBNull.Value);
                cardCommand.Parameters.AddWithValue("$MerchantId", (object?)cardTransaction.MerchantId ?? DBNull.Value);
                cardCommand.Parameters.AddWithValue("$ResponseCode", (object?)cardTransaction.ResponseCode ?? DBNull.Value);
                cardCommand.Parameters.AddWithValue("$ResponseText", (object?)cardTransaction.ResponseText ?? DBNull.Value);
                cardCommand.Parameters.AddWithValue("$Stan", (object?)cardTransaction.Stan ?? DBNull.Value);
                cardCommand.Parameters.AddWithValue("$BankDateTime", cardTransaction.BankDateTime?.ToString("O") ?? (object)DBNull.Value);
                cardCommand.Parameters.AddWithValue("$Amount", cardTransaction.Amount);
                cardCommand.Parameters.AddWithValue("$ReceiptText", (object?)cardTransaction.ReceiptText ?? DBNull.Value);
                cardCommand.Parameters.AddWithValue("$RefundReference", (object?)cardTransaction.RefundReference ?? DBNull.Value);
                await cardCommand.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await using (var queue = connection.CreateCommand())
        {
            queue.Transaction = transaction;
            queue.CommandText = """
                INSERT INTO SyncQueue (EntityId, EntityType, Status, CreatedAt)
                VALUES ($EntityId, 'Order', 'Pending', $CreatedAt);
                """;
            queue.Parameters.AddWithValue("$EntityId", order.OrderGuid.ToString());
            queue.Parameters.AddWithValue("$CreatedAt", DateTimeOffset.Now.ToString("O"));
            await queue.ExecuteNonQueryAsync(cancellationToken);
        }

        if (heldOrder is not null)
        {
            await CompleteHeldOrderInTransactionAsync(
                connection,
                transaction,
                order,
                heldOrder,
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// 共享挂单取单完成的原子事务段（与 LocalOrder/SyncQueue 同一事务）：
    /// 1) 读取 durable claim 事实，claim 缺失视为数据损坏整体回滚（绝不静默降级）；
    /// 2) 写不可变 held-order source（RemoteClaim 带 claim、OfflineOrigin 不带）；
    /// 3) Active 且未被他单绑定的 claim 绑定本订单并完成；Prepared（activate 未知）
    ///    或已被他单绑定/终态的 claim 保留事实，供 claims/mine 对账，不阻止订单；
    /// 4) 消费本地挂单：publication ConsumedAtIso + SuspendedOrders 置 Recalled，
    ///    之后不可再次 recall/评估/发布。
    /// </summary>
    private static async Task CompleteHeldOrderInTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LocalOrder order,
        LocalHeldOrderCompletionContext heldOrder,
        CancellationToken cancellationToken)
    {
        ClaimFacts claim;
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText =
                """
                SELECT HoldGuid, StoreCode, DeviceCode, Source, Status,
                       ActivateIdempotencyKey, BoundOrderGuid, ReleaseIdempotencyKey
                FROM SharedHeldOrderClaims
                WHERE ClaimId = $ClaimId;
                """;
            read.Parameters.AddWithValue("$ClaimId", heldOrder.ClaimId.ToString("D"));
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidDataException(
                    $"共享挂单订单来源缺少 durable claim: {heldOrder.ClaimId:D}。");
            }

            claim = new ClaimFacts(
                ReadGuid(reader, "HoldGuid"),
                ReadString(reader, "StoreCode"),
                ReadString(reader, "DeviceCode"),
                ReadString(reader, "Source"),
                ReadString(reader, "Status"),
                ReadNullableString(reader, "ActivateIdempotencyKey"),
                ReadNullableString(reader, "BoundOrderGuid"),
                ReadNullableString(reader, "ReleaseIdempotencyKey"));
        }

        if (claim.HoldGuid != heldOrder.HoldGuid)
        {
            throw new InvalidDataException(
                "共享挂单订单来源与 durable claim 的 HoldGuid 不一致。");
        }

        if (!string.Equals(
                claim.StoreCode.Trim().ToUpperInvariant(),
                order.StoreCode.Trim().ToUpperInvariant(),
                StringComparison.Ordinal)
            || !string.Equals(
                claim.DeviceCode.Trim().ToUpperInvariant(),
                order.DeviceCode.Trim().ToUpperInvariant(),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "共享挂单订单来源与 durable claim 的 store/device 不一致。");
        }

        var isRemoteClaim = string.Equals(
            claim.Source,
            "RemoteClaim",
            StringComparison.Ordinal);
        var sourceKind = isRemoteClaim ? 1 : 2;

        // Active 且 activate key 匹配：绑定本订单后完成（幂等）；已绑定他单/终态则跳过完成。
        var releaseKey = $"completed:{order.OrderGuid:D}";
        if (string.Equals(claim.Status, "Active", StringComparison.Ordinal)
            && string.Equals(
                claim.ActivateIdempotencyKey,
                heldOrder.ActivateIdempotencyKey,
                StringComparison.Ordinal))
        {
            if (claim.BoundOrderGuid is null)
            {
                await using var bind = connection.CreateCommand();
                bind.Transaction = transaction;
                bind.CommandText =
                    """
                    UPDATE SharedHeldOrderClaims
                    SET BoundOrderGuid = $BoundOrderGuid,
                        UpdatedAtIso = $UpdatedAtIso
                    WHERE ClaimId = $ClaimId
                      AND Status = 'Active'
                      AND ActivateIdempotencyKey = $ActivateIdempotencyKey
                      AND BoundOrderGuid IS NULL;
                    """;
                bind.Parameters.AddWithValue("$BoundOrderGuid", order.OrderGuid.ToString("D"));
                bind.Parameters.AddWithValue("$UpdatedAtIso", heldOrder.CompletedAtIso);
                bind.Parameters.AddWithValue("$ClaimId", heldOrder.ClaimId.ToString("D"));
                bind.Parameters.AddWithValue("$ActivateIdempotencyKey", heldOrder.ActivateIdempotencyKey);
                await bind.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var complete = connection.CreateCommand();
            complete.Transaction = transaction;
            complete.CommandText =
                """
                UPDATE SharedHeldOrderClaims
                SET Status = 'Completed',
                    ReleaseIdempotencyKey = $ReleaseIdempotencyKey,
                    UpdatedAtIso = $UpdatedAtIso
                WHERE ClaimId = $ClaimId
                  AND Status = 'Active'
                  AND ActivateIdempotencyKey = $ActivateIdempotencyKey
                  AND BoundOrderGuid = $BoundOrderGuid
                  AND ReleaseIdempotencyKey IS NULL;
                """;
            complete.Parameters.AddWithValue("$ReleaseIdempotencyKey", releaseKey);
            complete.Parameters.AddWithValue("$UpdatedAtIso", heldOrder.CompletedAtIso);
            complete.Parameters.AddWithValue("$ClaimId", heldOrder.ClaimId.ToString("D"));
            complete.Parameters.AddWithValue("$ActivateIdempotencyKey", heldOrder.ActivateIdempotencyKey);
            complete.Parameters.AddWithValue("$BoundOrderGuid", order.OrderGuid.ToString("D"));
            await complete.ExecuteNonQueryAsync(cancellationToken);
        }
        else if (string.Equals(claim.Status, "Prepared", StringComparison.Ordinal)
                 && claim.ActivateIdempotencyKey is null
                 && claim.BoundOrderGuid is null
                 && claim.ReleaseIdempotencyKey is null)
        {
            // activate 结果未知但正式订单已落盘：同事务关闭本地 fence，避免恢复出重复购物车。
            await using var supersede = connection.CreateCommand();
            supersede.Transaction = transaction;
            supersede.CommandText =
                """
                UPDATE SharedHeldOrderClaims
                SET Status = 'Superseded',
                    SupersedeIdempotencyKey = $SupersedeIdempotencyKey,
                    UpdatedAtIso = $UpdatedAtIso
                WHERE ClaimId = $ClaimId
                  AND Status = 'Prepared'
                  AND ActivateIdempotencyKey IS NULL
                  AND ReleaseIdempotencyKey IS NULL
                  AND SupersedeIdempotencyKey IS NULL
                  AND BoundOrderGuid IS NULL;
                """;
            supersede.Parameters.AddWithValue(
                "$SupersedeIdempotencyKey",
                releaseKey);
            supersede.Parameters.AddWithValue("$UpdatedAtIso", heldOrder.CompletedAtIso);
            supersede.Parameters.AddWithValue("$ClaimId", heldOrder.ClaimId.ToString("D"));
            var changed = await supersede.ExecuteNonQueryAsync(cancellationToken);
            if (changed != 1)
            {
                throw new InvalidDataException(
                    "共享挂单 Prepared claim 在订单完成前已变化。");
            }
        }

        // 不可变来源行：与订单同一事务写入，任何改写都被 schema trigger 拒绝。
        await using (var source = connection.CreateCommand())
        {
            source.Transaction = transaction;
            source.CommandText =
                """
                INSERT INTO LocalOrderHeldOrderSources (
                    OrderGuid, HoldGuid, ClaimGuid, SourceKind, CreatedAtIso)
                VALUES ($OrderGuid, $HoldGuid, $ClaimGuid, $SourceKind, $CreatedAtIso);
                """;
            source.Parameters.AddWithValue("$OrderGuid", order.OrderGuid.ToString("D"));
            source.Parameters.AddWithValue("$HoldGuid", heldOrder.HoldGuid.ToString("D"));
            source.Parameters.AddWithValue(
                "$ClaimGuid",
                isRemoteClaim ? (object)heldOrder.ClaimId.ToString("D") : DBNull.Value);
            source.Parameters.AddWithValue("$SourceKind", sourceKind);
            source.Parameters.AddWithValue("$CreatedAtIso", heldOrder.CompletedAtIso);
            await source.ExecuteNonQueryAsync(cancellationToken);
        }

        // 消费本地挂单：publication 标记 + SuspendedOrders 离开 Pending，单次使用。
        await using (var consumePublication = connection.CreateCommand())
        {
            consumePublication.Transaction = transaction;
            consumePublication.CommandText =
                """
                UPDATE SharedHeldOrderPublications
                SET ConsumedAtIso = $ConsumedAtIso,
                    UpdatedAtIso = $ConsumedAtIso
                WHERE LocalHoldGuid = $HoldGuid
                  AND ConsumedAtIso IS NULL;
                """;
            consumePublication.Parameters.AddWithValue("$ConsumedAtIso", heldOrder.CompletedAtIso);
            consumePublication.Parameters.AddWithValue("$HoldGuid", heldOrder.HoldGuid.ToString("D"));
            await consumePublication.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var consumeOrder = connection.CreateCommand())
        {
            consumeOrder.Transaction = transaction;
            consumeOrder.CommandText =
                """
                UPDATE SuspendedOrders
                SET Status = $RecalledStatus
                WHERE SuspendedOrderGuid = $HoldGuid
                  AND Status = $PendingStatus;
                """;
            consumeOrder.Parameters.AddWithValue("$RecalledStatus", (int)SuspendedOrderStatus.Recalled);
            consumeOrder.Parameters.AddWithValue("$HoldGuid", heldOrder.HoldGuid.ToString("D"));
            consumeOrder.Parameters.AddWithValue("$PendingStatus", (int)SuspendedOrderStatus.Pending);
            await consumeOrder.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task UpdatePaymentReferenceAsync(
        Guid paymentGuid,
        string? reference,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE LocalPayments
            SET Reference = $Reference
            WHERE PaymentGuid = $PaymentGuid
              AND COALESCE(Reference, '') <> COALESCE($Reference, '');
            """;
        command.Parameters.AddWithValue("$PaymentGuid", paymentGuid.ToString());
        command.Parameters.AddWithValue("$Reference", (object?)reference ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<LocalOrderSummary>> GetRecentOrdersAsync(int take = 50, CancellationToken cancellationToken = default)
    {
        return await GetRecentOrdersAsync(new LocalOrderHistoryQuery(), take, cancellationToken);
    }

    public async Task<IReadOnlyList<LocalOrderSummary>> GetRecentOrdersAsync(
        LocalOrderHistoryQuery query,
        int take = 50,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var keyword = query.Keyword?.Trim() ?? string.Empty;
        var normalizedKeyword = keyword.ToUpperInvariant();
        var normalizedOrderKeyword = normalizedKeyword.Replace("-", string.Empty);
        command.CommandText = """
            SELECT
                o.OrderGuid,
                o.StoreCode,
                o.DeviceCode,
                o.CashierName,
                o.SoldAt,
                o.TotalAmount,
                o.DiscountAmount,
                o.ActualAmount,
                o.SyncStatus,
                COUNT(DISTINCT l.OrderLineGuid) AS LineCount,
                COALESCE(GROUP_CONCAT(DISTINCT p.Method), '') AS PaymentMethods
            FROM LocalOrders o
            LEFT JOIN LocalOrderLines l ON l.OrderGuid = o.OrderGuid
            LEFT JOIN LocalPayments p ON p.OrderGuid = o.OrderGuid
            WHERE ($SoldFrom IS NULL OR julianday(o.SoldAt) >= julianday($SoldFrom))
              AND ($SoldTo IS NULL OR julianday(o.SoldAt) <= julianday($SoldTo))
              AND ($DeviceCode = '' OR UPPER(o.DeviceCode) = $DeviceCode)
              AND (
                    $KeywordLike = ''
                    OR UPPER(o.OrderGuid) LIKE $KeywordLike
                    OR REPLACE(UPPER(o.OrderGuid), '-', '') LIKE $NormalizedOrderKeywordLike
                    OR EXISTS (
                        SELECT 1
                        FROM LocalOrderLines search
                        WHERE search.OrderGuid = o.OrderGuid
                          AND (
                                UPPER(search.LookupCode) LIKE $KeywordLike
                                OR UPPER(COALESCE(search.ItemNumber, '')) LIKE $KeywordLike
                              )
                    )
                  )
            GROUP BY o.OrderGuid
            ORDER BY o.SoldAt DESC
            LIMIT $Take;
            """;
        command.Parameters.AddWithValue("$SoldFrom", query.SoldFrom?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$SoldTo", query.SoldTo?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$DeviceCode", query.DeviceCode?.Trim().ToUpperInvariant() ?? string.Empty);
        command.Parameters.AddWithValue("$KeywordLike", string.IsNullOrEmpty(normalizedKeyword) ? string.Empty : $"%{normalizedKeyword}%");
        command.Parameters.AddWithValue(
            "$NormalizedOrderKeywordLike",
            string.IsNullOrEmpty(normalizedOrderKeyword) ? string.Empty : $"%{normalizedOrderKeyword}%");
        command.Parameters.AddWithValue("$Take", Math.Clamp(take, 1, 500));

        var summaries = new List<LocalOrderSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            summaries.Add(new LocalOrderSummary(
                ReadGuid(reader, "OrderGuid"),
                ReadString(reader, "StoreCode"),
                ReadString(reader, "DeviceCode"),
                ReadString(reader, "CashierName"),
                ReadDateTimeOffset(reader, "SoldAt"),
                ReadDecimal(reader, "TotalAmount"),
                ReadDecimal(reader, "DiscountAmount"),
                ReadDecimal(reader, "ActualAmount"),
                ReadString(reader, "SyncStatus"),
                reader.GetInt32(reader.GetOrdinal("LineCount")),
                FormatPaymentSummary(ReadString(reader, "PaymentMethods"))));
        }

        return summaries;
    }

    public async Task<LocalOrder?> GetOrderAsync(Guid orderGuid, CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        var header = await ReadOrderHeaderAsync(connection, orderGuid, cancellationToken);
        if (header is null)
        {
            return null;
        }

        var lines = await ReadOrderLinesAsync(connection, orderGuid, cancellationToken);
        var payments = await ReadPaymentsAsync(connection, orderGuid, cancellationToken);

        return header with
        {
            Lines = lines,
            Payments = payments
        };
    }

    public async Task<IReadOnlyList<LocalOrder>> GetReturnOrdersForOriginalAsync(
        Guid originalOrderGuid,
        bool unsyncedOnly,
        CancellationToken cancellationToken = default)
    {
        var returnOrderGuids = new List<Guid>();
        await using (var connection = await store.OpenConnectionAsync(cancellationToken))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT DISTINCT o.OrderGuid
                FROM LocalOrders o
                JOIN LocalOrderLines l ON l.OrderGuid = o.OrderGuid
                WHERE l.OriginalOrderGuid = $OriginalOrderGuid COLLATE NOCASE
                  AND l.Kind = $ReturnKind
                  AND ($UnsyncedOnly = 0 OR o.SyncStatus <> 'Synced');
                """;
            command.Parameters.AddWithValue("$OriginalOrderGuid", originalOrderGuid.ToString());
            command.Parameters.AddWithValue("$ReturnKind", (int)OrderLineKind.Return);
            command.Parameters.AddWithValue("$UnsyncedOnly", unsyncedOnly ? 1 : 0);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                returnOrderGuids.Add(ReadGuid(reader, "OrderGuid"));
            }
        }

        var orders = new List<LocalOrder>(returnOrderGuids.Count);
        foreach (var returnOrderGuid in returnOrderGuids)
        {
            if (await GetOrderAsync(returnOrderGuid, cancellationToken) is { } order)
            {
                orders.Add(order);
            }
        }

        return orders;
    }

    private static async Task<LocalOrder?> ReadOrderHeaderAsync(
        SqliteConnection connection,
        Guid orderGuid,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT o.OrderGuid, o.StoreCode, o.DeviceCode, o.CashierId, o.CashierName, o.SoldAt,
                   o.TotalAmount, o.DiscountAmount, o.ActualAmount, o.TenderedAmount, o.ChangeAmount,
                   h.HoldGuid, h.ClaimGuid, h.SourceKind
            FROM LocalOrders o
            LEFT JOIN LocalOrderHeldOrderSources h ON h.OrderGuid = o.OrderGuid
            WHERE o.OrderGuid = $OrderGuid;
            """;
        command.Parameters.AddWithValue("$OrderGuid", orderGuid.ToString());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var heldOrderSource = ReadNullableString(reader, "HoldGuid") is { } holdGuid
            ? new HeldOrderSourceDto(
                Guid.Parse(holdGuid),
                ReadNullableGuid(reader, "ClaimGuid"),
                (HeldOrderSourceKind?)ReadNullableInt(reader, "SourceKind"))
            : null;
        return new LocalOrder(
            ReadGuid(reader, "OrderGuid"),
            ReadString(reader, "StoreCode"),
            ReadString(reader, "DeviceCode"),
            ReadString(reader, "CashierId"),
            ReadString(reader, "CashierName"),
            ReadDateTimeOffset(reader, "SoldAt"),
            ReadDecimal(reader, "TotalAmount"),
            ReadDecimal(reader, "DiscountAmount"),
            ReadDecimal(reader, "ActualAmount"),
            [],
            [],
            ReadNullableDecimal(reader, "TenderedAmount"),
            ReadNullableDecimal(reader, "ChangeAmount"),
            heldOrderSource);
    }

    private static async Task<IReadOnlyList<LocalOrderLine>> ReadOrderLinesAsync(
        SqliteConnection connection,
        Guid orderGuid,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT OrderLineGuid, ProductCode, ReferenceCode, DisplayName, LookupCode, ItemNumber, Quantity, UnitPrice, DiscountAmount, ActualAmount, PriceSource, Kind, ReturnSourceKey, OriginalOrderGuid, OriginalOrderDetailGuid
            FROM LocalOrderLines
            WHERE OrderGuid = $OrderGuid
            ORDER BY rowid;
            """;
        command.Parameters.AddWithValue("$OrderGuid", orderGuid.ToString());

        var lines = new List<LocalOrderLine>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            lines.Add(new LocalOrderLine(
                ReadGuid(reader, "OrderLineGuid"),
                ReadString(reader, "ProductCode"),
                ReadNullableString(reader, "ReferenceCode"),
                ReadString(reader, "DisplayName"),
                ReadString(reader, "LookupCode"),
                ReadNullableString(reader, "ItemNumber"),
                ReadDecimal(reader, "Quantity"),
                ReadDecimal(reader, "UnitPrice"),
                ReadDecimal(reader, "DiscountAmount"),
                ReadDecimal(reader, "ActualAmount"),
                (PriceSourceKind)reader.GetInt32(reader.GetOrdinal("PriceSource")),
                (OrderLineKind)reader.GetInt32(reader.GetOrdinal("Kind")),
                ReadNullableString(reader, "ReturnSourceKey"),
                ReadNullableGuid(reader, "OriginalOrderGuid"),
                ReadNullableGuid(reader, "OriginalOrderDetailGuid")));
        }

        return lines;
    }

    private static async Task<IReadOnlyList<LocalPayment>> ReadPaymentsAsync(
        SqliteConnection connection,
        Guid orderGuid,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT PaymentGuid, Method, Amount, Reference, IdempotencyKey
            FROM LocalPayments
            WHERE OrderGuid = $OrderGuid
            ORDER BY rowid;
            """;
        command.Parameters.AddWithValue("$OrderGuid", orderGuid.ToString());

        var paymentRows = new List<(Guid PaymentGuid, PaymentMethodKind Method, decimal Amount, string? Reference, string? IdempotencyKey)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var paymentGuid = ReadGuid(reader, "PaymentGuid");
            paymentRows.Add((
                paymentGuid,
                (PaymentMethodKind)reader.GetInt32(reader.GetOrdinal("Method")),
                ReadDecimal(reader, "Amount"),
                ReadNullableString(reader, "Reference"),
                ReadNullableString(reader, "IdempotencyKey")));
        }

        var payments = new List<LocalPayment>(paymentRows.Count);
        foreach (var payment in paymentRows)
        {
            payments.Add(new LocalPayment(
                payment.PaymentGuid,
                payment.Method,
                payment.Amount,
                payment.Reference,
                await ReadCardTransactionsAsync(connection, orderGuid, payment.PaymentGuid, cancellationToken),
                payment.IdempotencyKey));
        }

        return payments;
    }

    private sealed record ClaimFacts(
        Guid HoldGuid,
        string StoreCode,
        string DeviceCode,
        string Source,
        string Status,
        string? ActivateIdempotencyKey,
        string? BoundOrderGuid,
        string? ReleaseIdempotencyKey);

    private static async Task<IReadOnlyList<CardTransactionDto>> ReadCardTransactionsAsync(
        SqliteConnection connection,
        Guid orderGuid,
        Guid paymentGuid,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Processor, TxnRef, AuthCode, CardType, CardBin, MaskedCardNumber, MerchantId, ResponseCode, ResponseText, Stan, BankDateTime, Amount, ReceiptText, RefundReference
            FROM LocalCardTransactions
            WHERE OrderGuid = $OrderGuid AND PaymentGuid = $PaymentGuid
            ORDER BY rowid;
            """;
        command.Parameters.AddWithValue("$OrderGuid", orderGuid.ToString());
        command.Parameters.AddWithValue("$PaymentGuid", paymentGuid.ToString());

        var transactions = new List<CardTransactionDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            transactions.Add(new CardTransactionDto(
                ReadString(reader, "Processor"),
                ReadNullableString(reader, "TxnRef"),
                ReadNullableString(reader, "AuthCode"),
                ReadNullableString(reader, "CardType"),
                ReadNullableInt(reader, "CardBin"),
                ReadNullableString(reader, "MaskedCardNumber"),
                ReadNullableString(reader, "MerchantId"),
                ReadNullableString(reader, "ResponseCode"),
                ReadNullableString(reader, "ResponseText"),
                ReadNullableString(reader, "Stan"),
                ReadNullableDateTimeOffset(reader, "BankDateTime"),
                ReadDecimal(reader, "Amount"),
                ReadNullableString(reader, "ReceiptText"),
                ReadNullableString(reader, "RefundReference")));
        }

        return transactions;
    }

    private static Guid ReadGuid(SqliteDataReader reader, string name)
    {
        return Guid.Parse(ReadString(reader, name));
    }

    private static Guid? ReadNullableGuid(SqliteDataReader reader, string name)
    {
        var value = ReadNullableString(reader, name);
        return string.IsNullOrWhiteSpace(value) ? null : Guid.Parse(value);
    }

    private static string ReadString(SqliteDataReader reader, string name)
    {
        return reader.GetString(reader.GetOrdinal(name));
    }

    private static string? ReadNullableString(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static int? ReadNullableInt(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    private static decimal ReadDecimal(SqliteDataReader reader, string name)
    {
        var value = reader.GetValue(reader.GetOrdinal(name));
        return value switch
        {
            decimal decimalValue => decimalValue,
            double doubleValue => Convert.ToDecimal(doubleValue, CultureInfo.InvariantCulture),
            long longValue => longValue,
            int intValue => intValue,
            string stringValue => decimal.Parse(stringValue, CultureInfo.InvariantCulture),
            _ => Convert.ToDecimal(value, CultureInfo.InvariantCulture)
        };
    }

    private static decimal? ReadNullableDecimal(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : ReadDecimal(reader, name);
    }

    private static DateTimeOffset ReadDateTimeOffset(SqliteDataReader reader, string name)
    {
        return DateTimeOffset.Parse(ReadString(reader, name));
    }

    private static DateTimeOffset? ReadNullableDateTimeOffset(SqliteDataReader reader, string name)
    {
        var value = ReadNullableString(reader, name);
        return string.IsNullOrWhiteSpace(value) ? null : DateTimeOffset.Parse(value);
    }

    private static string FormatPaymentSummary(string methodList)
    {
        if (string.IsNullOrWhiteSpace(methodList))
        {
            return "None";
        }

        return string.Join(", ", methodList
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => Enum.TryParse<PaymentMethodKind>(value, out var method) ? method.ToString() : value)
            .Distinct());
    }
}
