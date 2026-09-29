using System.Globalization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.Orders;
using Microsoft.Data.Sqlite;

namespace Hbpos.Client.Tests;

public sealed class DailyCloseServiceTests
{
    [Fact]
    public async Task LoadReportAsync_offloads_slow_local_read_and_waits_for_its_result()
    {
        var repository = new BlockingDailyCloseRepository();
        var service = new DailyCloseService(repository);
        var callerThreadId = Environment.CurrentManagedThreadId;

        var loadTask = service.LoadReportAsync(CreateSession(), new DateTime(2026, 5, 28));

        // 慢读由闸门挡住而不是 Task.Delay 计时：负载高时测试线程可能晚于固定延时才走到断言，
        // 只有闸门能保证"放行前任务一定未完成"，也能证明调用方确实等到了慢读结果。
        await repository.Started.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        Assert.NotEqual(callerThreadId, repository.ExecutionThreadId);
        Assert.False(loadTask.IsCompleted);

        repository.Release.TrySetResult();
        var report = await loadTask.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);

        Assert.Equal(1, report.OrderCount);
    }

    [Fact]
    public async Task LoadReportAsync_filters_by_store_device_and_local_business_date()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var orderRepository = new LocalOrderRepository(store);
            var repository = new LocalDailyCloseRepository(store);
            var service = new DailyCloseService(repository);
            var session = CreateSession();
            var businessDate = new DateTime(2026, 5, 28);

            await schema.InitializeAsync();
            await orderRepository.SavePendingOrderAsync(CreateOrder(
                session.StoreCode,
                session.DeviceCode,
                CreateLocalTimestamp(2026, 5, 28, 0, 0, 0),
                30m,
                [CreateLine("SKU-SALE-1", "SALE-1", 2m, 20m), CreateLine("SKU-SALE-2", "SALE-2", 1m, 10m)],
                [CreatePayment(PaymentMethodKind.Cash, 20m), CreatePayment(PaymentMethodKind.Card, 10m)]));
            await orderRepository.SavePendingOrderAsync(CreateOrder(
                session.StoreCode,
                session.DeviceCode,
                CreateLocalTimestamp(2026, 5, 28, 23, 59, 0),
                -7m,
                [CreateReturnLine("SKU-RET-1", "RET-1", 3m, 7m)],
                [CreatePayment(PaymentMethodKind.Card, -5m), CreatePayment(PaymentMethodKind.Voucher, -2m)]));
            await orderRepository.SavePendingOrderAsync(CreateOrder(
                session.StoreCode,
                session.DeviceCode,
                CreateLocalTimestamp(2026, 5, 29, 0, 0, 0),
                99m,
                [CreateLine("SKU-NEXT", "NEXT", 1m, 99m)],
                [CreatePayment(PaymentMethodKind.Cash, 99m)]));
            await orderRepository.SavePendingOrderAsync(CreateOrder(
                session.StoreCode,
                "POS-02",
                CreateLocalTimestamp(2026, 5, 28, 12, 0, 0),
                50m,
                [CreateLine("SKU-OTHER-DEVICE", "OTHER-DEVICE", 1m, 50m)],
                [CreatePayment(PaymentMethodKind.Cash, 50m)]));

            var report = await service.LoadReportAsync(session, businessDate);

            Assert.Equal(businessDate, report.BusinessDate);
            Assert.Equal(2, report.OrderCount);
            Assert.Equal(30m, report.SalesAmount);
            Assert.Equal(7m, report.RefundAmount);
            Assert.Equal(23m, report.NetAmount);
            Assert.Equal(3m, report.ReturnQuantity);
            Assert.Equal(20m, report.SystemCashAmount);

            Assert.Collection(
                report.PaymentSummaries.OrderBy(summary => summary.Method),
                summary =>
                {
                    Assert.Equal(PaymentMethodKind.Cash, summary.Method);
                    Assert.Equal(20m, summary.SalesAmount);
                    Assert.Equal(0m, summary.RefundAmount);
                    Assert.Equal(20m, summary.NetAmount);
                },
                summary =>
                {
                    Assert.Equal(PaymentMethodKind.Card, summary.Method);
                    Assert.Equal(10m, summary.SalesAmount);
                    Assert.Equal(5m, summary.RefundAmount);
                    Assert.Equal(5m, summary.NetAmount);
                },
                summary =>
                {
                    Assert.Equal(PaymentMethodKind.Voucher, summary.Method);
                    Assert.Equal(0m, summary.SalesAmount);
                    Assert.Equal(2m, summary.RefundAmount);
                    Assert.Equal(-2m, summary.NetAmount);
                });
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task LoadReportAsync_uses_next_local_midnight_when_daylight_saving_offset_changes()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var timeZone = GetSydneyTimeZone();
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var orderRepository = new LocalOrderRepository(store);
            var service = new DailyCloseService(new LocalDailyCloseRepository(store, timeZone));
            var session = CreateSession();
            var businessDate = new DateTime(2026, 10, 4);
            var periodFrom = CreateTimeZoneTimestamp(timeZone, 2026, 10, 4, 0, 0, 0);
            var periodTo = CreateTimeZoneTimestamp(timeZone, 2026, 10, 5, 0, 0, 0);

            Assert.NotEqual(periodFrom.Offset, periodTo.Offset);

            await schema.InitializeAsync();
            await orderRepository.SavePendingOrderAsync(CreateOrder(
                session.StoreCode,
                session.DeviceCode,
                CreateTimeZoneTimestamp(timeZone, 2026, 10, 4, 23, 30, 0),
                10m,
                [CreateLine("SKU-DST-IN", "DST-IN", 1m, 10m)],
                [CreatePayment(PaymentMethodKind.Cash, 10m)]));
            await orderRepository.SavePendingOrderAsync(CreateOrder(
                session.StoreCode,
                session.DeviceCode,
                CreateTimeZoneTimestamp(timeZone, 2026, 10, 5, 0, 30, 0),
                99m,
                [CreateLine("SKU-DST-OUT", "DST-OUT", 1m, 99m)],
                [CreatePayment(PaymentMethodKind.Cash, 99m)]));

            var report = await service.LoadReportAsync(session, businessDate);

            Assert.Equal(periodFrom, report.PeriodFrom);
            Assert.Equal(periodTo, report.PeriodTo);
            Assert.Equal(1, report.OrderCount);
            Assert.Equal(10m, report.SystemCashAmount);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task SaveAsync_persists_multiple_archives_and_cash_counts()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var orderRepository = new LocalOrderRepository(store);
            var service = new DailyCloseService(new LocalDailyCloseRepository(store));
            var session = CreateSession();
            var businessDate = new DateTime(2026, 5, 28);

            await schema.InitializeAsync();
            await orderRepository.SavePendingOrderAsync(CreateOrder(
                session.StoreCode,
                session.DeviceCode,
                CreateLocalTimestamp(2026, 5, 28, 9, 30, 0),
                18.5m,
                [CreateLine("SKU-ARCHIVE", "ARCHIVE", 1m, 18.5m)],
                [CreatePayment(PaymentMethodKind.Cash, 18.5m)]));

            var firstArchive = await service.SaveAsync(
                session,
                businessDate,
                [
                    Count(50m, 1),
                    Count(20m, 2),
                    Count(0.50m, 3)
                ]);
            var secondArchive = await service.SaveAsync(
                session,
                businessDate,
                [
                    Count(100m, 1),
                    Count(5m, 4)
                ]);
            await service.SaveAsync(
                session with { DeviceCode = "POS-02" },
                businessDate,
                [
                    Count(50m, 1)
                ]);

            Assert.NotEqual(firstArchive.DailyCloseGuid, secondArchive.DailyCloseGuid);
            Assert.Equal(90m, firstArchive.NoteSubtotal);
            Assert.Equal(1.5m, firstArchive.CoinSubtotal);
            Assert.Equal(91.5m, firstArchive.CountedCashAmount);
            Assert.Equal(73m, firstArchive.CashDifference);
            Assert.Equal(120m, secondArchive.CountedCashAmount);
            Assert.Equal(DailyCloseService.AustralianDenominations.Count, firstArchive.CashCounts.Count);

            await using var connection = await store.OpenConnectionAsync();
            await DeleteCashCountAsync(connection, secondArchive.DailyCloseGuid, 2m);

            var archives = await service.GetArchivesAsync(session, businessDate);
            Assert.Equal(2, archives.Count);
            Assert.Equal(secondArchive.DailyCloseGuid, archives[0].DailyCloseGuid);
            Assert.Equal(DailyCloseService.AustralianDenominations.Count, archives[0].CashCounts.Count);
            Assert.Contains(archives[0].CashCounts, count => count.Value == 2m && count.Label == "$2" && count.Quantity == 0 && count.Amount == 0m);
            Assert.Contains(archives[1].CashCounts, count => count.Value == 50m && count.Label == "$50" && count.Quantity == 1);

            Assert.Equal(3, await ReadScalarIntAsync(connection, "SELECT COUNT(*) FROM LocalDailyCloses;"));
            Assert.Equal(DailyCloseService.AustralianDenominations.Count * 3 - 1, await ReadScalarIntAsync(connection, "SELECT COUNT(*) FROM LocalDailyCloseCashCounts;"));
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task SaveAsync_returns_committed_archive_when_post_commit_checkpoint_is_canceled()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            using var callerCancellation = new CancellationTokenSource();
            var checkpoint = new CancelingCheckpointService(callerCancellation);
            var service = new DailyCloseService(new LocalDailyCloseRepository(store), checkpoint);
            var session = CreateSession();

            await schema.InitializeAsync();

            var archive = await service.SaveAsync(
                session,
                new DateTime(2026, 5, 28),
                [],
                callerCancellation.Token);

            Assert.NotEqual(Guid.Empty, archive.DailyCloseGuid);
            Assert.True(callerCancellation.IsCancellationRequested);
            await using var connection = await store.OpenConnectionAsync();
            Assert.Equal(1, await ReadScalarIntAsync(connection, "SELECT COUNT(*) FROM LocalDailyCloses;"));
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    private static CashDenominationCount Count(decimal value, int quantity)
    {
        var denomination = DailyCloseService.AustralianDenominations.Single(item => item.Value == value);
        return new CashDenominationCount(denomination.Value, denomination.Label, denomination.Kind, quantity);
    }

    private static PosSessionState CreateSession()
    {
        return new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
    }

    private static LocalOrder CreateOrder(
        string storeCode,
        string deviceCode,
        DateTimeOffset soldAt,
        decimal actualAmount,
        IReadOnlyList<LocalOrderLine> lines,
        IReadOnlyList<LocalPayment> payments)
    {
        return new LocalOrder(
            Guid.NewGuid(),
            storeCode,
            deviceCode,
            "C001",
            "Alice",
            soldAt,
            Math.Abs(actualAmount),
            0m,
            actualAmount,
            lines,
            payments);
    }

    private static LocalOrderLine CreateLine(string productCode, string lookupCode, decimal quantity, decimal lineAmount)
    {
        return new LocalOrderLine(
            Guid.NewGuid(),
            productCode,
            null,
            productCode,
            lookupCode,
            productCode,
            quantity,
            lineAmount / quantity,
            0m,
            lineAmount,
            PriceSourceKind.StoreRetailPrice);
    }

    private static LocalOrderLine CreateReturnLine(string productCode, string lookupCode, decimal quantity, decimal refundAmount)
    {
        return new LocalOrderLine(
            Guid.NewGuid(),
            productCode,
            null,
            productCode,
            lookupCode,
            productCode,
            quantity,
            refundAmount / quantity,
            0m,
            -refundAmount,
            PriceSourceKind.StoreRetailPrice,
            OrderLineKind.Return,
            $"RETURN:{productCode}",
            Guid.NewGuid(),
            Guid.NewGuid());
    }

    private static LocalPayment CreatePayment(PaymentMethodKind method, decimal amount)
    {
        return new LocalPayment(Guid.NewGuid(), method, amount, null);
    }

    private static DateTimeOffset CreateLocalTimestamp(int year, int month, int day, int hour, int minute, int second)
    {
        var localDateTime = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified);
        return new DateTimeOffset(localDateTime, TimeZoneInfo.Local.GetUtcOffset(localDateTime));
    }

    private static DateTimeOffset CreateTimeZoneTimestamp(TimeZoneInfo timeZone, int year, int month, int day, int hour, int minute, int second)
    {
        var localDateTime = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified);
        return new DateTimeOffset(localDateTime, timeZone.GetUtcOffset(localDateTime));
    }

    private static TimeZoneInfo GetSydneyTimeZone()
    {
        foreach (var timeZoneId in new[] { "AUS Eastern Standard Time", "Australia/Sydney" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        throw new InvalidOperationException("找不到用于夏令时边界测试的悉尼时区。");
    }

    private static string CreateTempDatabasePath()
    {
        return Path.Combine(Path.GetTempPath(), $"hbpos-daily-close-{Guid.NewGuid():N}.db");
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

    private static async Task<int> ReadScalarIntAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task DeleteCashCountAsync(SqliteConnection connection, Guid dailyCloseGuid, decimal denominationValue)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM LocalDailyCloseCashCounts
            WHERE DailyCloseGuid = $DailyCloseGuid
              AND DenominationValue = $DenominationValue;
            """;
        command.Parameters.AddWithValue("$DailyCloseGuid", dailyCloseGuid.ToString());
        command.Parameters.AddWithValue("$DenominationValue", denominationValue);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class BlockingDailyCloseRepository : ILocalDailyCloseRepository
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ExecutionThreadId { get; private set; }

        public async Task<DailyCloseReport> LoadReportAsync(
            PosSessionState session,
            DateTime businessDate,
            CancellationToken cancellationToken = default)
        {
            ExecutionThreadId = Environment.CurrentManagedThreadId;
            Started.TrySetResult();
            // 带超时等待放行：若服务改成同步阻塞调用方，测试会超时失败而不是永久挂住。
            await Release.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout, cancellationToken);
            return new DailyCloseReport(
                businessDate,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                session.StoreCode,
                session.DeviceCode,
                session.CashierId,
                session.CashierName,
                1,
                [],
                0m,
                0m);
        }

        public Task<DailyCloseArchive> SaveAsync(
            DailyCloseReport report,
            IReadOnlyList<CashDenominationCount> cashCounts,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<DailyCloseArchive>> GetArchivesAsync(
            PosSessionState session,
            DateTime businessDate,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class CancelingCheckpointService(CancellationTokenSource callerCancellation)
        : ILocalSqliteCheckpointService
    {
        public Task CheckpointWalAsync(CancellationToken cancellationToken = default)
        {
            Assert.False(cancellationToken.CanBeCanceled);
            callerCancellation.Cancel();
            throw new OperationCanceledException("simulated post-commit checkpoint cancellation");
        }
    }
}
