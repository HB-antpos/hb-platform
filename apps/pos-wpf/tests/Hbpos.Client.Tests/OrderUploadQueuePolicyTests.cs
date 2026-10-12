using System.Net;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.Orders;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;

namespace Hbpos.Client.Tests;

public sealed class OrderUploadQueuePolicyTests
{
    [Theory]
    [InlineData(OrderUploadFailureKind.Transient, 1, 30)]
    [InlineData(OrderUploadFailureKind.Transient, 2, 60)]
    [InlineData(OrderUploadFailureKind.Transient, 3, 120)]
    [InlineData(OrderUploadFailureKind.Transient, 4, 300)]
    [InlineData(OrderUploadFailureKind.Transient, 50, 300)]
    [InlineData(OrderUploadFailureKind.Timeout, 1, 30)]
    [InlineData(OrderUploadFailureKind.Timeout, 9, 300)]
    [InlineData(OrderUploadFailureKind.BusinessRejection, 1, 30)]
    [InlineData(OrderUploadFailureKind.BusinessRejection, 4, 300)]
    [InlineData(OrderUploadFailureKind.BusinessRejection, 5, 600)]
    [InlineData(OrderUploadFailureKind.BusinessRejection, 6, 1200)]
    [InlineData(OrderUploadFailureKind.BusinessRejection, 7, 1800)]
    [InlineData(OrderUploadFailureKind.BusinessRejection, int.MaxValue, 1800)]
    [InlineData(OrderUploadFailureKind.Transient, 0, 30)]
    public void Retry_delay_follows_transient_and_business_rejection_tables(
        OrderUploadFailureKind failureKind,
        int attemptCount,
        int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), OrderUploadRetryPolicy.GetRetryDelay(attemptCount, failureKind));
    }

    [Theory]
    [InlineData(400, null, OrderUploadFailureKind.BusinessRejection)]
    [InlineData(409, null, OrderUploadFailureKind.BusinessRejection)]
    [InlineData(413, null, OrderUploadFailureKind.BusinessRejection)]
    [InlineData(422, null, OrderUploadFailureKind.BusinessRejection)]
    [InlineData(401, null, OrderUploadFailureKind.Transient)]
    [InlineData(403, "DEVICE_SCOPE_FORBIDDEN", OrderUploadFailureKind.Transient)]
    [InlineData(404, null, OrderUploadFailureKind.Transient)]
    [InlineData(408, null, OrderUploadFailureKind.Transient)]
    [InlineData(429, null, OrderUploadFailureKind.Transient)]
    [InlineData(500, null, OrderUploadFailureKind.Transient)]
    [InlineData(503, null, OrderUploadFailureKind.Transient)]
    public void Only_reliably_identified_server_rejections_get_the_relaxed_cap(
        int statusCode,
        string? errorCode,
        OrderUploadFailureKind expected)
    {
        Assert.Equal(
            expected,
            OrderUploadRetryPolicy.Classify(new CatalogApiException("failed", (HttpStatusCode)statusCode, errorCode)));
    }

    [Fact]
    public void Unclassifiable_failures_use_the_transient_cap()
    {
        Assert.Equal(OrderUploadFailureKind.Transient, OrderUploadRetryPolicy.Classify(new HttpRequestException("down")));
        Assert.Equal(OrderUploadFailureKind.Transient, OrderUploadRetryPolicy.Classify(new System.Text.Json.JsonException("html")));
        Assert.Equal(OrderUploadFailureKind.Transient, OrderUploadRetryPolicy.Classify(new CatalogApiException("no status")));
        Assert.Equal(OrderUploadFailureKind.Transient, OrderUploadRetryPolicy.Classify(new InvalidOperationException("local")));
    }

    [Theory]
    [InlineData(401, "DEVICE_AUTH_REQUIRED", true)]
    [InlineData(401, null, true)]
    [InlineData(403, null, true)]
    [InlineData(403, "CASHIER_TICKET_EXPIRED", true)]
    [InlineData(403, "DEVICE_SCOPE_FORBIDDEN", false)]
    [InlineData(403, "device_scope_forbidden", false)]
    [InlineData(400, null, false)]
    [InlineData(500, null, false)]
    public void Global_authorization_failure_excludes_per_order_device_scope_rejection(
        int statusCode,
        string? errorCode,
        bool expected)
    {
        Assert.Equal(
            expected,
            OrderUploadRetryPolicy.IsGlobalAuthorizationFailure(
                new CatalogApiException("failed", (HttpStatusCode)statusCode, errorCode)));
    }

    [Fact]
    public async Task Failed_order_becomes_due_exactly_when_its_backoff_expires_while_retry_all_ignores_backoff()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-order-upload-due-{Guid.NewGuid():N}.db");
        try
        {
            var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-12T01:00:00Z"));
            var store = new LocalSqliteStore(databasePath);
            await new LocalSchemaService(store).InitializeAsync();
            var orders = new LocalOrderRepository(store);
            var repository = new LocalOrderUploadRepository(store, time);
            var order = CreateLocalOrder();
            await orders.SavePendingOrderAsync(order);

            await repository.MarkFailedWithBackoffAsync(order.OrderGuid, "network down", OrderUploadFailureKind.Transient);

            Assert.Empty(await repository.GetPendingOrderGuidsAsync());
            Assert.Equal([order.OrderGuid], await repository.GetRetryableOrderGuidsAsync(500));
            time.Advance(TimeSpan.FromSeconds(30) - TimeSpan.FromTicks(1));
            Assert.Empty(await repository.GetPendingOrderGuidsAsync());
            time.Advance(TimeSpan.FromTicks(1));
            Assert.Equal([order.OrderGuid], await repository.GetPendingOrderGuidsAsync());

            // 第二次失败按新的次数计时：1 分钟。
            await repository.MarkFailedWithBackoffAsync(order.OrderGuid, "network down", OrderUploadFailureKind.Transient);
            time.Advance(TimeSpan.FromSeconds(59));
            Assert.Empty(await repository.GetPendingOrderGuidsAsync());
            time.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal([order.OrderGuid], await repository.GetPendingOrderGuidsAsync());
        }
        finally
        {
            await SqliteTestDatabaseCleanup.DeleteDatabaseFilesAsync(databasePath);
        }
    }

    [Fact]
    public async Task Retry_all_ignores_backoff_but_still_excludes_orders_with_unissued_refund_voucher()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-order-upload-retry-all-{Guid.NewGuid():N}.db");
        try
        {
            var store = new LocalSqliteStore(databasePath);
            await new LocalSchemaService(store).InitializeAsync();
            var orders = new LocalOrderRepository(store);
            var repository = new LocalOrderUploadRepository(store);
            var backedOff = CreateLocalOrder();
            var held = CreateLocalOrder() with
            {
                Payments = [new LocalPayment(Guid.NewGuid(), PaymentMethodKind.Voucher, -9.00m, "VOUCHER_REFUND_PENDING")]
            };
            await orders.SavePendingOrderAsync(backedOff);
            await orders.SavePendingOrderAsync(held);
            await repository.MarkFailedWithBackoffAsync(backedOff.OrderGuid, "rejected", OrderUploadFailureKind.BusinessRejection);
            var api = new RecordingOrderSyncApiClient();
            var executor = new OrderUploadExecutionService(new OrderUploadService(orders, api, repository), repository);

            Assert.Equal(new OrderUploadExecutionResult(0, 0, 0), await executor.ExecutePendingAsync());
            Assert.Equal(new OrderUploadExecutionResult(1, 1, 0), await executor.ExecuteAllRetryableAsync());

            Assert.Equal([backedOff.OrderGuid], api.RequestedOrderGuids);
        }
        finally
        {
            await SqliteTestDatabaseCleanup.DeleteDatabaseFilesAsync(databasePath);
        }
    }

    [Fact]
    public async Task Upload_queue_queries_seek_through_indexes_instead_of_scanning_the_queue()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-order-upload-plan-{Guid.NewGuid():N}.db");
        try
        {
            var store = new LocalSqliteStore(databasePath);
            await new LocalSchemaService(store).InitializeAsync();
            await using var connection = await store.OpenConnectionAsync();

            foreach (var sql in new[]
                     {
                         LocalOrderUploadRepository.DueOrderGuidsSql,
                         LocalOrderUploadRepository.RetryableOrderGuidsSql,
                         LocalOrderUploadRepository.VoucherRefundHeldOrdersSql,
                         // 每轮扫描都会执行的暂扣原因回写，同样只能走索引。
                         LocalOrderUploadRepository.MarkVoucherRefundHoldsSql
                     })
            {
                var plan = await ExplainAsync(connection, sql);
                Assert.Contains("SEARCH q USING INDEX IX_SyncQueue_Status_EntityType_CreatedAt", plan, StringComparison.Ordinal);
                Assert.Contains("SEARCH p USING INDEX IX_LocalPayments_OrderGuid", plan, StringComparison.Ordinal);
                Assert.DoesNotContain("SCAN q", plan, StringComparison.Ordinal);
            }

            // 按订单号直接上传时查入队时间（判断暂扣窗口），按订单号定位队列行。
            var queuedAtPlan = await ExplainAsync(connection, LocalOrderUploadRepository.QueuedAtSql);
            Assert.Contains("USING INDEX IX_SyncQueue_EntityId_EntityType", queuedAtPlan, StringComparison.Ordinal);
            Assert.DoesNotContain("SCAN SyncQueue", queuedAtPlan, StringComparison.Ordinal);
        }
        finally
        {
            await SqliteTestDatabaseCleanup.DeleteDatabaseFilesAsync(databasePath);
        }
    }

    private static async Task<string> ExplainAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"EXPLAIN QUERY PLAN {sql}";
        command.Parameters.AddWithValue("$Now", "2026-10-12T01:00:00.0000000+00:00");
        command.Parameters.AddWithValue("$QueuedBefore", "2026-10-12T01:00:00.0000000+00:00");
        command.Parameters.AddWithValue("$HoldReleaseBefore", "2026-10-12T00:40:00.0000000+00:00");
        command.Parameters.AddWithValue("$OrderGuid", Guid.NewGuid().ToString());
        command.Parameters.AddWithValue("$Take", 20);
        command.Parameters.AddWithValue("$ErrorMessage", LocalOrderUploadRepository.VoucherRefundHeldErrorMessage);
        var details = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            details.Add(reader.GetString(reader.FieldCount - 1));
        }

        return string.Join(" | ", details);
    }

    private static LocalOrder CreateLocalOrder() => new(
        Guid.NewGuid(),
        "S001",
        "POS-01",
        "C001",
        "Alice",
        DateTimeOffset.UtcNow,
        9.00m,
        0m,
        9.00m,
        [
            new LocalOrderLine(
                Guid.NewGuid(),
                "SKU-101",
                null,
                "Organic Gala Apples",
                "690101",
                "ITEM-101",
                1m,
                9.00m,
                0m,
                9.00m,
                PriceSourceKind.StoreRetailPrice)
        ],
        [new LocalPayment(Guid.NewGuid(), PaymentMethodKind.Cash, 9.00m, null)]);

    private sealed class RecordingOrderSyncApiClient : IOrderSyncApiClient
    {
        public List<Guid> RequestedOrderGuids { get; } = [];

        public Task<OrderSyncResponse> SyncAsync(OrderSyncRequest request, CancellationToken cancellationToken = default)
        {
            RequestedOrderGuids.Add(request.OrderGuid);
            return Task.FromResult(new OrderSyncResponse(request.OrderGuid, true, false, "Synced"));
        }
    }
}
