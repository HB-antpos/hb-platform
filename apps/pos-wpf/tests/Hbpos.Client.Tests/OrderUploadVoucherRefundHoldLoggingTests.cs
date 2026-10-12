using System.Globalization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Tests;

// 会替换 ConsoleLog 的中心日志接收器（全局状态），与其它日志测试一样独占运行。
[Collection(ConsoleLogGlobalStateTestCollection.Name)]
public sealed class OrderUploadVoucherRefundHoldLoggingTests
{
    [Fact]
    public async Task Refund_voucher_order_held_past_threshold_is_reported_to_center_log_without_being_uploaded()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-order-upload-voucher-hold-{Guid.NewGuid():N}.db");
        var sink = new CapturingApplicationLogSink();
        ConsoleLog.ConfigureCenterSink(sink);
        try
        {
            var store = new LocalSqliteStore(databasePath);
            await new LocalSchemaService(store).InitializeAsync();
            var orders = new LocalOrderRepository(store);
            var uploadRepository = new LocalOrderUploadRepository(store);
            var api = new RecordingOrderSyncApiClient();
            var executor = new OrderUploadExecutionService(
                new OrderUploadService(orders, api, uploadRepository),
                uploadRepository);
            var stuck = CreateRefundOrder();
            var justSaved = CreateRefundOrder();
            await orders.SavePendingOrderAsync(stuck);
            await orders.SavePendingOrderAsync(justSaved);
            // 入队 20 分钟仍未签券（已过 10 分钟告警阈值、未到 30 分钟放行）：签券失败后收银员离开了付款页，不会再有人签发。
            // 用 +14:00 的本地时间串：按字符串比较会显得比现在还晚，只有按 julianday 换算才判得出已卡住。
            var queuedAt = DateTimeOffset.UtcNow.AddMinutes(-20).ToOffset(TimeSpan.FromHours(14));
            await using (var connection = await store.OpenConnectionAsync())
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "UPDATE SyncQueue SET CreatedAt = $CreatedAt WHERE EntityId = $EntityId;";
                command.Parameters.AddWithValue("$CreatedAt", queuedAt.ToString("O", CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("$EntityId", stuck.OrderGuid.ToString());
                await command.ExecuteNonQueryAsync();
            }

            Assert.Equal(new OrderUploadExecutionResult(0, 0, 0), await executor.ExecutePendingAsync());

            Assert.Empty(api.RequestedOrderGuids);
            var warnings = sink.Entries
                .Where(entry =>
                    string.Equals(entry.Level, "Warning", StringComparison.OrdinalIgnoreCase) &&
                    entry.Message.Contains("refund voucher not issued", StringComparison.Ordinal))
                .ToArray();
            var warning = Assert.Single(warnings);
            Assert.Equal(stuck.OrderGuid.ToString("D"), warning.TraceId);
            Assert.DoesNotContain(justSaved.OrderGuid.ToString("D"), warning.Message, StringComparison.Ordinal);
        }
        finally
        {
            ConsoleLog.ConfigureCenterSink(null);
            await SqliteTestDatabaseCleanup.DeleteDatabaseFilesAsync(databasePath);
        }
    }

    [Fact]
    public async Task Refund_voucher_order_past_hold_window_is_uploaded_and_reported_to_center_log()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-order-upload-voucher-release-{Guid.NewGuid():N}.db");
        var sink = new CapturingApplicationLogSink();
        ConsoleLog.ConfigureCenterSink(sink);
        try
        {
            var store = new LocalSqliteStore(databasePath);
            await new LocalSchemaService(store).InitializeAsync();
            var orders = new LocalOrderRepository(store);
            var uploadRepository = new LocalOrderUploadRepository(store);
            var api = new RecordingOrderSyncApiClient();
            var executor = new OrderUploadExecutionService(
                new OrderUploadService(orders, api, uploadRepository),
                uploadRepository);
            var expired = CreateRefundOrder();
            await orders.SavePendingOrderAsync(expired);
            // 入队 31 分钟仍是占位引用（例如卡退款经恢复服务落单、从不签券）：超过暂扣窗口，照旧上传，不能永远留在本机。
            var queuedAt = DateTimeOffset.UtcNow.AddMinutes(-31).ToOffset(TimeSpan.FromHours(14));
            await using (var connection = await store.OpenConnectionAsync())
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "UPDATE SyncQueue SET CreatedAt = $CreatedAt WHERE EntityId = $EntityId;";
                command.Parameters.AddWithValue("$CreatedAt", queuedAt.ToString("O", CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("$EntityId", expired.OrderGuid.ToString());
                await command.ExecuteNonQueryAsync();
            }

            Assert.Equal(new OrderUploadExecutionResult(1, 1, 0), await executor.ExecutePendingAsync());

            Assert.Equal([expired.OrderGuid], api.RequestedOrderGuids);
            // 放行时必须告警：总部收到的这笔退货缺少退款券号，要人工补券。
            var released = Assert.Single(
                sink.Entries,
                entry =>
                    string.Equals(entry.Level, "Warning", StringComparison.OrdinalIgnoreCase) &&
                    entry.Message.Contains("upload released: refund voucher still not issued", StringComparison.Ordinal));
            Assert.Equal(expired.OrderGuid.ToString("D"), released.TraceId);
            // 已过暂扣窗口的订单不再按"暂扣中"告警。
            Assert.DoesNotContain(
                sink.Entries,
                entry => entry.Message.Contains("upload held: refund voucher not issued", StringComparison.Ordinal));
        }
        finally
        {
            ConsoleLog.ConfigureCenterSink(null);
            await SqliteTestDatabaseCleanup.DeleteDatabaseFilesAsync(databasePath);
        }
    }

    private static LocalOrder CreateRefundOrder() => new(
        Guid.NewGuid(),
        "S001",
        "POS-01",
        "C001",
        "Alice",
        DateTimeOffset.UtcNow,
        -6.00m,
        0m,
        -6.00m,
        [
            new LocalOrderLine(
                Guid.NewGuid(),
                "SKU-101",
                null,
                "Organic Gala Apples",
                "690101",
                "ITEM-101",
                -1m,
                6.00m,
                0m,
                -6.00m,
                PriceSourceKind.StoreRetailPrice,
                OrderLineKind.Return)
        ],
        [new LocalPayment(Guid.NewGuid(), PaymentMethodKind.Voucher, -6.00m, "VOUCHER_REFUND_PENDING")]);

    private sealed class RecordingOrderSyncApiClient : IOrderSyncApiClient
    {
        public List<Guid> RequestedOrderGuids { get; } = [];

        public Task<OrderSyncResponse> SyncAsync(OrderSyncRequest request, CancellationToken cancellationToken = default)
        {
            RequestedOrderGuids.Add(request.OrderGuid);
            return Task.FromResult(new OrderSyncResponse(request.OrderGuid, true, false, "Synced"));
        }
    }

    private sealed class CapturingApplicationLogSink : IApplicationLogSink
    {
        private readonly List<ApplicationLogEntry> _entries = [];

        public IReadOnlyList<ApplicationLogEntry> Entries
        {
            get
            {
                lock (_entries)
                {
                    return _entries.ToArray();
                }
            }
        }

        public void Enqueue(ApplicationLogEntry entry)
        {
            lock (_entries)
            {
                _entries.Add(entry);
            }
        }
    }
}
