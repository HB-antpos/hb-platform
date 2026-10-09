using System.Globalization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.Orders;
using Microsoft.Data.Sqlite;
using static Hbpos.Client.Tests.SharedHeldOrderClientTestSupport;

namespace Hbpos.Client.Tests;

public sealed class LocalOrderRepositoryTests
{
    [Fact]
    public async Task SavePendingOrderAsync_persists_voucher_refund_idempotency_key_and_updates_reference_idempotently()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalOrderRepository(store);
            var order = CreateOrder();
            var payment = Assert.Single(order.Payments);

            await schema.InitializeAsync();
            await repository.SavePendingOrderAsync(order);
            await repository.UpdatePaymentReferenceAsync(payment.PaymentGuid, "VOUCHER_REFUND:RF123");
            await repository.UpdatePaymentReferenceAsync(payment.PaymentGuid, "VOUCHER_REFUND:RF123");

            var saved = await repository.GetOrderAsync(order.OrderGuid);

            Assert.NotNull(saved);
            var savedPayment = Assert.Single(saved.Payments);
            Assert.Equal("VOUCHER_REFUND:RF123", savedPayment.Reference);
            Assert.Equal(payment.IdempotencyKey, savedPayment.IdempotencyKey);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task GetReturnOrdersForOriginalAsync_returns_only_return_orders_of_that_original_and_filters_synced()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalOrderRepository(store);
            await schema.InitializeAsync();
            var originalOrderGuid = Guid.NewGuid();
            var pendingReturn = CreateOrder() with
            {
                OrderGuid = Guid.NewGuid(),
                Lines = [CreateOrder().Lines[0] with { OrderLineGuid = Guid.NewGuid(), OriginalOrderGuid = originalOrderGuid }]
            };
            var syncedReturn = CreateOrder() with
            {
                OrderGuid = Guid.NewGuid(),
                Lines = [CreateOrder().Lines[0] with { OrderLineGuid = Guid.NewGuid(), OriginalOrderGuid = originalOrderGuid }]
            };
            var otherOriginalReturn = CreateOrder() with
            {
                OrderGuid = Guid.NewGuid(),
                Lines = [CreateOrder().Lines[0] with { OrderLineGuid = Guid.NewGuid(), OriginalOrderGuid = Guid.NewGuid() }]
            };
            await repository.SavePendingOrderAsync(pendingReturn);
            await repository.SavePendingOrderAsync(syncedReturn);
            await repository.SavePendingOrderAsync(otherOriginalReturn);
            await using (var connection = await store.OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "UPDATE LocalOrders SET SyncStatus = 'Synced' WHERE OrderGuid = $OrderGuid;";
                command.Parameters.AddWithValue("$OrderGuid", syncedReturn.OrderGuid.ToString());
                await command.ExecuteNonQueryAsync();
            }

            var all = await repository.GetReturnOrdersForOriginalAsync(originalOrderGuid, unsyncedOnly: false);
            var unsynced = await repository.GetReturnOrdersForOriginalAsync(originalOrderGuid, unsyncedOnly: true);

            Assert.Equal(
                new[] { pendingReturn.OrderGuid, syncedReturn.OrderGuid }.OrderBy(guid => guid),
                all.Select(order => order.OrderGuid).OrderBy(guid => guid));
            Assert.Equal(pendingReturn.OrderGuid, Assert.Single(unsynced).OrderGuid);
            Assert.NotEmpty(Assert.Single(unsynced).Payments);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    private static LocalOrder CreateOrder()
    {
        return new LocalOrder(
            Guid.NewGuid(),
            "S001",
            "POS-01",
            "C001",
            "Alice",
            DateTimeOffset.Parse("2026-06-02T10:30:00+10:00"),
            6m,
            0m,
            -6m,
            [
                new LocalOrderLine(
                    Guid.NewGuid(),
                    "SKU-VR-LOCAL",
                    null,
                    "Voucher Refund Local",
                    "930600",
                    "ITEM-VR-LOCAL",
                    1m,
                    6m,
                    0m,
                    -6m,
                    PriceSourceKind.StoreRetailPrice,
                    OrderLineKind.Return,
                    "RETURN:LOCAL-VR",
                    Guid.NewGuid(),
                    Guid.NewGuid())
            ],
            [
                new LocalPayment(
                    Guid.NewGuid(),
                    PaymentMethodKind.Voucher,
                    -6m,
                    "VOUCHER_REFUND_PENDING",
                    IdempotencyKey: "refund-key-001")
            ]);
    }

    private static string CreateTempDatabasePath()
    {
        return Path.Combine(Path.GetTempPath(), $"hbpos-local-order-repo-{Guid.NewGuid():N}.db");
    }

    private static void DeleteTempDatabase(string databasePath)
    {
        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { databasePath, $"{databasePath}-wal", $"{databasePath}-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task SavePendingOrderAsync_with_offline_origin_completes_claim_and_consumes_local_hold_atomically()
    {
        var databasePath = CreateTempDatabasePath();
        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            await schema.InitializeAsync();
            var orderRepository = new LocalOrderRepository(store);
            var heldRepository = new SharedHeldOrderRepository(
                store,
                new TestPayloadProtector(),
                new TestPayloadSerializer());
            var suspendedRepository = new SuspendedOrderRepository(store);

            var holdGuid = Guid.NewGuid();
            var claimId = Guid.NewGuid();
            var order = CreateOrder() with { OrderGuid = Guid.NewGuid() };
            var suspendedAt = new DateTimeOffset(2026, 7, 28, 0, 0, 0, TimeSpan.Zero);
            await suspendedRepository.SaveAsync(new SuspendedOrder(
                holdGuid,
                "S001",
                "POS-01",
                "C001",
                "Alice",
                suspendedAt,
                6m,
                0m,
                -6m,
                SuspendedOrderStatus.Pending,
                [new SuspendedOrderLine(
                    Guid.NewGuid(),
                    holdGuid,
                    "S001",
                    "SKU-VR-LOCAL",
                    null,
                    "Voucher Refund Local",
                    "930600",
                    "ITEM-VR-LOCAL",
                    null,
                    1m,
                    6m,
                    0m,
                    null,
                    -6m,
                    PriceSourceKind.StoreRetailPrice,
                    "Store Retail Price")]));
            Assert.True(await heldRepository.UpsertPublicationAsync(
                holdGuid,
                "S001",
                "POS-01",
                SharedHeldOrderPublicationStatus.NeedsEvaluation,
                null,
                "2026-07-28T00:00:00.000Z",
                "2026-07-28T00:00:00.000Z",
                "2026-07-28T00:00:00.000Z"));
            Assert.Equal(SharedHeldOrderShareRequestResult.Requested, await heldRepository.TryRequestShareAsync(
                holdGuid, "S001", "POS-01", "2026-07-28T00:00:00.000Z"));
            var payload = SampleCanonical();
            Assert.True(await heldRepository.TryStagePendingPublishAsync(
                holdGuid, 1, payload, "2026-07-28T00:00:01.000Z"));

            // 本地离线 recall：OfflineOrigin durable claim 直接激活（无服务端 revision）。
            const string prepareKey = "wpf-offline:test";
            const string activateKey = "wpf-offline-activate:test";
            Assert.True(await heldRepository.TrySavePreparedClaimAsync(new SharedHeldOrderClaimDraft(
                claimId,
                holdGuid,
                "S001",
                "POS-01",
                SharedHeldOrderClaimSource.OfflineOrigin,
                prepareKey,
                payload,
                "2026-07-28T00:00:02.000Z")));
            Assert.True(await heldRepository.TryActivateClaimAsync(
                claimId, prepareKey, activateKey, serverRevision: null, "2026-07-28T00:00:03.000Z"));

            await orderRepository.SavePendingOrderWithHeldSourceAsync(
                order,
                new LocalHeldOrderCompletionContext(
                    holdGuid,
                    claimId,
                    SharedHeldOrderClaimSource.OfflineOrigin,
                    prepareKey,
                    activateKey,
                    BoundOrderGuid: null,
                    "2026-07-28T00:05:00.000Z"));

            // 来源行：OfflineOrigin 不带 claim。
            var saved = await orderRepository.GetOrderAsync(order.OrderGuid);
            Assert.NotNull(saved);
            Assert.NotNull(saved!.HeldOrderSource);
            Assert.Equal(holdGuid, saved.HeldOrderSource.HoldGuid);
            Assert.Null(saved.HeldOrderSource.ClaimGuid);
            Assert.Equal(HeldOrderSourceKind.OfflineOrigin, saved.HeldOrderSource.Kind);

            // durable claim 完成并绑定本订单。
            var claim = await heldRepository.GetClaimAsync(claimId);
            Assert.Equal(SharedHeldOrderClaimStatus.Completed, claim!.Status);
            Assert.Equal(order.OrderGuid.ToString("D"), claim.BoundOrderGuid);
            Assert.Equal($"completed:{order.OrderGuid:D}", claim.ReleaseIdempotencyKey);

            // 本地挂单单次使用：publication 已消费、SuspendedOrders 离开 Pending、
            // 不可再评估/发布/离线 recall。
            var publication = await heldRepository.GetPublicationAsync(holdGuid);
            Assert.Equal("2026-07-28T00:05:00.000Z", publication!.ConsumedAtIso);
            Assert.Empty(await heldRepository.ListDuePublicationsAsync("2026-07-28T00:10:00.000Z"));
            Assert.Null(await heldRepository.GetPublicationPayloadAsync(holdGuid));
            Assert.Empty(await heldRepository.ListLegacyOrdersNeedingEvaluationAsync("S001"));
            Assert.DoesNotContain(
                await suspendedRepository.GetPendingAsync("S001"),
                summary => summary.SuspendedOrderGuid == holdGuid);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task SavePendingOrderAsync_with_remote_claim_roundtrips_source_and_completes_claim()
    {
        var databasePath = CreateTempDatabasePath();
        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            await schema.InitializeAsync();
            var orderRepository = new LocalOrderRepository(store);
            var heldRepository = new SharedHeldOrderRepository(
                store,
                new TestPayloadProtector(),
                new TestPayloadSerializer());

            var holdGuid = Guid.NewGuid();
            var claimId = Guid.NewGuid();
            var order = CreateOrder() with { OrderGuid = Guid.NewGuid() };
            const string prepareKey = "wpf-prepare:test";
            const string activateKey = "wpf-activate:test";
            Assert.True(await heldRepository.TrySavePreparedClaimAsync(new SharedHeldOrderClaimDraft(
                claimId,
                holdGuid,
                "S001",
                "POS-01",
                SharedHeldOrderClaimSource.RemoteClaim,
                prepareKey,
                SampleCanonical(),
                "2026-07-28T00:00:02.000Z",
                "2026-07-28T00:05:00.000Z")));
            Assert.True(await heldRepository.TryActivateClaimAsync(
                claimId, prepareKey, activateKey, serverRevision: 42, "2026-07-28T00:00:03.000Z"));

            await orderRepository.SavePendingOrderWithHeldSourceAsync(
                order,
                new LocalHeldOrderCompletionContext(
                    holdGuid,
                    claimId,
                    SharedHeldOrderClaimSource.RemoteClaim,
                    prepareKey,
                    activateKey,
                    BoundOrderGuid: null,
                    "2026-07-28T00:05:00.000Z"));

            var saved = await orderRepository.GetOrderAsync(order.OrderGuid);
            Assert.NotNull(saved!.HeldOrderSource);
            Assert.Equal(holdGuid, saved.HeldOrderSource.HoldGuid);
            Assert.Equal(claimId, saved.HeldOrderSource.ClaimGuid);
            Assert.Equal(HeldOrderSourceKind.RemoteClaim, saved.HeldOrderSource.Kind);

            var claim = await heldRepository.GetClaimAsync(claimId);
            Assert.Equal(SharedHeldOrderClaimStatus.Completed, claim!.Status);
            Assert.Equal(order.OrderGuid.ToString("D"), claim.BoundOrderGuid);
            Assert.Equal(42L, claim.ServerRevision);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task SavePendingOrderAsync_with_prepared_claim_persists_source_and_supersedes_claim()
    {
        var databasePath = CreateTempDatabasePath();
        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            await schema.InitializeAsync();
            var orderRepository = new LocalOrderRepository(store);
            var heldRepository = new SharedHeldOrderRepository(
                store,
                new TestPayloadProtector(),
                new TestPayloadSerializer());

            var holdGuid = Guid.NewGuid();
            var claimId = Guid.NewGuid();
            var order = CreateOrder() with { OrderGuid = Guid.NewGuid() };
            const string prepareKey = "wpf-prepare:prepared";
            Assert.True(await heldRepository.TrySavePreparedClaimAsync(new SharedHeldOrderClaimDraft(
                claimId,
                holdGuid,
                "S001",
                "POS-01",
                SharedHeldOrderClaimSource.RemoteClaim,
                prepareKey,
                SampleCanonical(),
                "2026-07-28T00:00:02.000Z",
                "2026-07-28T00:05:00.000Z")));

            // activate 响应未知：订单落盘后必须原子关闭本地 fence，避免崩溃恢复重复购物车。
            await orderRepository.SavePendingOrderWithHeldSourceAsync(
                order,
                new LocalHeldOrderCompletionContext(
                    holdGuid,
                    claimId,
                    SharedHeldOrderClaimSource.RemoteClaim,
                    prepareKey,
                    ActivateIdempotencyKey: null,
                    BoundOrderGuid: null,
                    "2026-07-28T00:05:00.000Z"));

            var saved = await orderRepository.GetOrderAsync(order.OrderGuid);
            Assert.NotNull(saved!.HeldOrderSource);
            Assert.Equal(HeldOrderSourceKind.RemoteClaim, saved.HeldOrderSource.Kind);
            Assert.Equal(claimId, saved.HeldOrderSource.ClaimGuid);

            var claim = await heldRepository.GetClaimAsync(claimId);
            Assert.Equal(SharedHeldOrderClaimStatus.Superseded, claim!.Status);
            Assert.Null(claim.BoundOrderGuid);
            Assert.Null(claim.ActivateIdempotencyKey);
            Assert.Equal($"completed:{order.OrderGuid:D}", claim.SupersedeIdempotencyKey);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task SavePendingOrderAsync_rolls_back_entire_transaction_when_claim_is_missing()
    {
        var databasePath = CreateTempDatabasePath();
        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            await schema.InitializeAsync();
            var orderRepository = new LocalOrderRepository(store);
            var order = CreateOrder() with { OrderGuid = Guid.NewGuid() };
            var missingClaimId = Guid.NewGuid();

            await Assert.ThrowsAsync<InvalidDataException>(() => orderRepository.SavePendingOrderWithHeldSourceAsync(
                order,
                new LocalHeldOrderCompletionContext(
                    Guid.NewGuid(),
                    missingClaimId,
                    SharedHeldOrderClaimSource.RemoteClaim,
                    "prepare-missing",
                    ActivateIdempotencyKey: null,
                    BoundOrderGuid: null,
                    "2026-07-28T00:05:00.000Z")));

            await using var connection = await store.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT
                    (SELECT COUNT(*) FROM LocalOrders WHERE OrderGuid = $OrderGuid),
                    (SELECT COUNT(*) FROM LocalOrderLines WHERE OrderGuid = $OrderGuid),
                    (SELECT COUNT(*) FROM LocalPayments WHERE OrderGuid = $OrderGuid),
                    (SELECT COUNT(*) FROM SyncQueue WHERE EntityId = $OrderGuid),
                    (SELECT COUNT(*) FROM LocalOrderHeldOrderSources WHERE OrderGuid = $OrderGuid);
                """;
            command.Parameters.AddWithValue("$OrderGuid", order.OrderGuid.ToString("D"));
            var result = await command.ExecuteScalarAsync();
            Assert.Equal(0, Convert.ToInt32(result, CultureInfo.InvariantCulture));
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task SavePendingOrderAsync_does_not_reject_order_when_claim_bound_to_another_order()
    {
        var databasePath = CreateTempDatabasePath();
        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            await schema.InitializeAsync();
            var orderRepository = new LocalOrderRepository(store);
            var heldRepository = new SharedHeldOrderRepository(
                store,
                new TestPayloadProtector(),
                new TestPayloadSerializer());

            var holdGuid = Guid.NewGuid();
            var claimId = Guid.NewGuid();
            var order = CreateOrder() with { OrderGuid = Guid.NewGuid() };
            var otherOrderGuid = Guid.NewGuid();
            const string prepareKey = "prepare-bound";
            const string activateKey = "activate-bound";
            Assert.True(await heldRepository.TrySavePreparedClaimAsync(new SharedHeldOrderClaimDraft(
                claimId,
                holdGuid,
                "S001",
                "POS-01",
                SharedHeldOrderClaimSource.RemoteClaim,
                prepareKey,
                SampleCanonical(),
                "2026-07-28T00:00:02.000Z")));
            Assert.True(await heldRepository.TryActivateClaimAsync(
                claimId, prepareKey, activateKey, serverRevision: 1, "2026-07-28T00:00:03.000Z"));
            Assert.True(await heldRepository.TryBindOrderAsync(
                claimId, activateKey, otherOrderGuid.ToString("D"), "2026-07-28T00:00:04.000Z"));

            // 竞态：claim 已被另一笔订单绑定/完成。正式订单绝不因此被拒绝，
            // 来源仍持久化，claim 保留他单绑定事实供调和。
            await orderRepository.SavePendingOrderWithHeldSourceAsync(
                order,
                new LocalHeldOrderCompletionContext(
                    holdGuid,
                    claimId,
                    SharedHeldOrderClaimSource.RemoteClaim,
                    prepareKey,
                    activateKey,
                    BoundOrderGuid: otherOrderGuid.ToString("D"),
                    "2026-07-28T00:05:00.000Z"));

            var saved = await orderRepository.GetOrderAsync(order.OrderGuid);
            Assert.NotNull(saved!.HeldOrderSource);
            Assert.Equal(HeldOrderSourceKind.RemoteClaim, saved.HeldOrderSource.Kind);
            var claim = await heldRepository.GetClaimAsync(claimId);
            Assert.Equal(SharedHeldOrderClaimStatus.Active, claim!.Status);
            Assert.Equal(otherOrderGuid.ToString("D"), claim.BoundOrderGuid);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }
}
