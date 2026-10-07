using System.Globalization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.DailyClose;
using Hbpos.Contracts.Orders;
using Microsoft.Data.Sqlite;

namespace Hbpos.Client.Tests;

/// <summary>日结记录上传各测试类共享的夹具、假实现与服务端校验规则复刻，避免每个测试文件各写一份。</summary>
internal sealed class DailyCloseUploadFixture : IAsyncDisposable
{
    public const string StoreCode = "S001";
    public const string DeviceCode = "POS-01";

    private DailyCloseUploadFixture(string databasePath, LocalSqliteStore store)
    {
        DatabasePath = databasePath;
        Store = store;
        Repository = new LocalDailyCloseRepository(store);
        Authorization = Authorized(StoreCode, DeviceCode);
    }

    public string DatabasePath { get; }

    public LocalSqliteStore Store { get; }

    public LocalDailyCloseRepository Repository { get; }

    /// <summary>当前设备授权范围，默认 S001 / POS-01。</summary>
    public DeviceAuthorizationState Authorization { get; }

    public static async Task<DailyCloseUploadFixture> CreateAsync()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-daily-close-upload-{Guid.NewGuid():N}.db");
        var store = new LocalSqliteStore(databasePath);
        await new LocalSchemaService(store).InitializeAsync();
        return new DailyCloseUploadFixture(databasePath, store);
    }

    public static DeviceAuthorizationState Authorized(string storeCode, string deviceCode)
    {
        var state = new DeviceAuthorizationState();
        state.Set(new DeviceAuthorizationContext(deviceCode, storeCode, "HW-TEST", "device-authorization-code"));
        return state;
    }

    public DailyCloseUploadService CreateService(
        IDailyCloseSyncApiClient apiClient,
        TimeProvider? timeProvider = null,
        DeviceAuthorizationState? authorization = null,
        ILocalDailyCloseUploadRepository? repository = null)
    {
        return new DailyCloseUploadService(
            repository ?? Repository,
            apiClient,
            authorization ?? Authorization,
            timeProvider,
            appVersion: "1.2.3-test");
    }

    /// <summary>
    /// 直接写入一条结构完整、数值自洽的日结存档行（上传列保持列默认值：Pending、立即到期），
    /// 返回其 Guid。不同 storeCode/deviceCode 用来构造"别的设备范围"的记录。
    /// </summary>
    public async Task<Guid> InsertDailyCloseAsync(
        string storeCode = StoreCode,
        string deviceCode = DeviceCode,
        string businessDate = "2026-05-28",
        string? savedAt = null,
        Guid? dailyCloseGuid = null)
    {
        var guid = dailyCloseGuid ?? Guid.NewGuid();
        await using var connection = await Store.OpenConnectionAsync();
        await using var transaction = connection.BeginTransaction();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO LocalDailyCloses
                (DailyCloseGuid, StoreCode, DeviceCode, CashierId, CashierName, BusinessDate, PeriodFrom, PeriodTo, SavedAt,
                 OrderCount, CashSalesAmount, CashRefundAmount, CashNetAmount, CardSalesAmount, CardRefundAmount, CardNetAmount,
                 VoucherSalesAmount, VoucherRefundAmount, VoucherNetAmount, RefundAmount, ReturnQuantity, NoteSubtotal,
                 CoinSubtotal, CountedCashAmount, CashDifference)
                VALUES
                ($DailyCloseGuid, $StoreCode, $DeviceCode, 'C001', 'Alice', $BusinessDate, $PeriodFrom, $PeriodTo, $SavedAt,
                 3, $CashSales, $Zero, $CashSales, $CardSales, $CardRefund, $CardNet,
                 $VoucherSales, $Zero, $VoucherSales, $CardRefund, $ReturnQuantity, $NoteSubtotal,
                 $CoinSubtotal, $CountedCashAmount, $CashDifference);
                """;
            command.Parameters.AddWithValue("$DailyCloseGuid", guid.ToString());
            command.Parameters.AddWithValue("$StoreCode", storeCode);
            command.Parameters.AddWithValue("$DeviceCode", deviceCode);
            command.Parameters.AddWithValue("$BusinessDate", businessDate);
            command.Parameters.AddWithValue("$PeriodFrom", $"{businessDate}T00:00:00.0000000+10:00");
            command.Parameters.AddWithValue("$PeriodTo", $"{businessDate}T23:59:59.0000000+10:00");
            command.Parameters.AddWithValue("$SavedAt", savedAt ?? $"{businessDate}T22:30:15.1234567+10:00");
            command.Parameters.AddWithValue("$Zero", 0m);
            command.Parameters.AddWithValue("$CashSales", 90m);
            command.Parameters.AddWithValue("$CardSales", 50m);
            command.Parameters.AddWithValue("$CardRefund", 5m);
            command.Parameters.AddWithValue("$CardNet", 45m);
            command.Parameters.AddWithValue("$VoucherSales", 10m);
            command.Parameters.AddWithValue("$ReturnQuantity", 1m);
            // 面额：$100×1（纸币 100）+ 5c×3（硬币 0.15）→ 实点 100.15，差额 = 100.15 − 现金净额 90 = 10.15。
            command.Parameters.AddWithValue("$NoteSubtotal", 100m);
            command.Parameters.AddWithValue("$CoinSubtotal", 0.15m);
            command.Parameters.AddWithValue("$CountedCashAmount", 100.15m);
            command.Parameters.AddWithValue("$CashDifference", 10.15m);
            await command.ExecuteNonQueryAsync();
        }

        foreach (var denomination in DailyCloseService.AustralianDenominations)
        {
            var quantity = denomination.Value switch
            {
                100m => 1,
                0.05m => 3,
                _ => 0
            };
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO LocalDailyCloseCashCounts (DailyCloseGuid, DenominationValue, Label, Kind, Quantity, Amount)
                VALUES ($DailyCloseGuid, $DenominationValue, $Label, $Kind, $Quantity, $Amount);
                """;
            command.Parameters.AddWithValue("$DailyCloseGuid", guid.ToString());
            command.Parameters.AddWithValue("$DenominationValue", denomination.Value);
            command.Parameters.AddWithValue("$Label", denomination.Label);
            command.Parameters.AddWithValue("$Kind", (int)denomination.Kind);
            command.Parameters.AddWithValue("$Quantity", quantity);
            command.Parameters.AddWithValue("$Amount", decimal.Round(denomination.Value * quantity, 2));
            await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
        return guid;
    }

    public async Task<UploadRow> ReadUploadRowAsync(Guid dailyCloseGuid)
    {
        await using var connection = await Store.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT UploadStatus, UploadAttemptCount, NextUploadAt, LastUploadAttemptAt, UploadErrorCode, UploadErrorMessage, UploadedAt
            FROM LocalDailyCloses
            WHERE DailyCloseGuid = $DailyCloseGuid;
            """;
        command.Parameters.AddWithValue("$DailyCloseGuid", dailyCloseGuid.ToString());
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "日结记录不存在");
        return new UploadRow(
            reader.GetString(0),
            reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6));
    }

    /// <summary>用原始 SQL 直接改上传列，构造"已有租约/已有退避"等中间状态。</summary>
    public async Task ExecuteSqlAsync(string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = await Store.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        await command.ExecuteNonQueryAsync();
    }

    public ValueTask DisposeAsync()
    {
        return new ValueTask(SqliteTestDatabaseCleanup.DeleteDatabaseFilesAsync(DatabasePath));
    }

    public sealed record UploadRow(
        string Status,
        int AttemptCount,
        string? NextUploadAt,
        string? LastUploadAttemptAt,
        string? ErrorCode,
        string? ErrorMessage,
        string? UploadedAt);
}

/// <summary>可手动推进的时钟，用来断言退避与租约，不靠墙钟等待。</summary>
internal sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset current = now;

    public override DateTimeOffset GetUtcNow() => current;

    public void Advance(TimeSpan delta) => current += delta;
}

internal sealed class FakeDailyCloseSyncApiClient(
    Func<DailyCloseSyncRequest, Task<DailyCloseSyncResponse>> handler) : IDailyCloseSyncApiClient
{
    private readonly object gate = new();
    private readonly List<DailyCloseSyncRequest> requests = [];

    public Func<DailyCloseSyncRequest, Task<DailyCloseSyncResponse>> Handler { get; set; } = handler;

    public IReadOnlyList<DailyCloseSyncRequest> Requests
    {
        get
        {
            lock (gate)
            {
                return requests.ToList();
            }
        }
    }

    public Task<DailyCloseSyncResponse> SyncAsync(
        DailyCloseSyncRequest request,
        CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            requests.Add(request);
        }

        return Handler(request);
    }

    public static Task<DailyCloseSyncResponse> Accepted() =>
        Task.FromResult(new DailyCloseSyncResponse(true, false, false));

    public static Task<DailyCloseSyncResponse> Fail(System.Net.HttpStatusCode status, string? code = null, string message = "failed") =>
        Task.FromException<DailyCloseSyncResponse>(new DailyCloseUploadApiException(message, status, code));
}

/// <summary>构造真实订单数据，经 <see cref="DailyCloseService.SaveAsync"/> 产出"正常保存路径"的日结行。</summary>
internal static class DailyCloseTestData
{
    public static PosSessionState Session(string storeCode = "S001", string deviceCode = "POS-01") =>
        new("HB POS", storeCode, "Main Store", deviceCode, "C001", "Alice", true, 0);

    public static CashDenominationCount Count(decimal value, int quantity)
    {
        var denomination = DailyCloseService.AustralianDenominations.Single(item => item.Value == value);
        return new CashDenominationCount(denomination.Value, denomination.Label, denomination.Kind, quantity);
    }

    /// <summary>覆盖 11 档面额（含 5c/10c/20c/50c 小数面额）的盘点数量。</summary>
    public static IReadOnlyList<CashDenominationCount> AllDenominationCounts() =>
    [
        Count(100m, 1), Count(50m, 2), Count(20m, 3), Count(10m, 4), Count(5m, 5), Count(2m, 6),
        Count(1m, 7), Count(0.50m, 8), Count(0.20m, 9), Count(0.10m, 10), Count(0.05m, 11)
    ];

    /// <summary>
    /// 在 businessDate 当天写入现金/刷卡/代金券各自的销售与退款订单，再保存日结并返回存档。
    /// 金额刻意带两位小数，才能覆盖现金差额、各支付方式净额的取整口径。
    /// </summary>
    public static async Task<DailyCloseArchive> SaveRealisticDailyCloseAsync(
        LocalSqliteStore store,
        DateTime businessDate,
        IDailyCloseService? service = null,
        PosSessionState? session = null,
        IReadOnlyList<CashDenominationCount>? counts = null)
    {
        session ??= Session();
        var orderRepository = new LocalOrderRepository(store);
        var day = businessDate.Date;
        await orderRepository.SavePendingOrderAsync(CreateOrder(
            session,
            LocalTimestamp(day, 9, 30),
            28.85m,
            [CreateLine("SKU-A", "A", 2m, 18.55m), CreateLine("SKU-B", "B", 1m, 10.30m)],
            [CreatePayment(PaymentMethodKind.Cash, 18.55m), CreatePayment(PaymentMethodKind.Card, 10.30m)]));
        await orderRepository.SavePendingOrderAsync(CreateOrder(
            session,
            LocalTimestamp(day, 11, 0),
            31.10m,
            [CreateLine("SKU-C", "C", 1m, 31.10m)],
            [CreatePayment(PaymentMethodKind.Voucher, 20.10m), CreatePayment(PaymentMethodKind.Cash, 11.00m)]));
        await orderRepository.SavePendingOrderAsync(CreateOrder(
            session,
            LocalTimestamp(day, 15, 45),
            -7.35m,
            [CreateReturnLine("SKU-RET", "RET", 3m, 7.35m)],
            [CreatePayment(PaymentMethodKind.Card, -5.15m), CreatePayment(PaymentMethodKind.Voucher, -2.20m)]));

        service ??= new DailyCloseService(new LocalDailyCloseRepository(store));
        return await service.SaveAsync(session, day, counts ?? AllDenominationCounts());
    }

    private static DateTimeOffset LocalTimestamp(DateTime day, int hour, int minute)
    {
        var local = new DateTime(day.Year, day.Month, day.Day, hour, minute, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    private static LocalOrder CreateOrder(
        PosSessionState session,
        DateTimeOffset soldAt,
        decimal actualAmount,
        IReadOnlyList<LocalOrderLine> lines,
        IReadOnlyList<LocalPayment> payments)
    {
        return new LocalOrder(
            Guid.NewGuid(),
            session.StoreCode,
            session.DeviceCode,
            session.CashierId,
            session.CashierName,
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
}

/// <summary>
/// 服务端 <c>DailyCloseSyncService.ValidateAndNormalize</c>（Hbpos.Api，internal，客户端测试不可引用）校验规则的等价复刻，
/// 用来证明正常保存路径产出的日结行不会被服务端 400 永久拒绝。服务端规则变化时这里要同步。
/// 返回第一条违反规则的服务端错误码，全部通过返回 null。
/// </summary>
internal static class DailyCloseServerRuleReplica
{
    private const decimal RoundingTolerance = 0.01m;
    private const decimal MaximumAbsoluteValue = 100_000_000m;
    private const int MaximumCashCountQuantity = 100_000;

    public static string? FirstViolation(DailyCloseSyncRequest request, string authStoreCode, string authDeviceCode)
    {
        // 控制器层：请求体的门店/设备必须与认证 claims 逐字一致。
        if (!string.Equals(request.StoreCode, authStoreCode, StringComparison.Ordinal) ||
            !string.Equals(request.DeviceCode, authDeviceCode, StringComparison.Ordinal))
        {
            return "DEVICE_SCOPE_FORBIDDEN";
        }

        if (request.SchemaVersion != DailyCloseContractConstants.SchemaVersion)
        {
            return "UNSUPPORTED_SCHEMA_VERSION";
        }

        if (request.DailyCloseGuid == Guid.Empty)
        {
            return "DAILY_CLOSE_GUID_REQUIRED";
        }

        if (!DailyCloseContractConstants.ClientKinds.Contains(request.ClientKind, StringComparer.OrdinalIgnoreCase))
        {
            return "INVALID_CLIENT_KIND";
        }

        if (request.BusinessDate == default)
        {
            return "BUSINESS_DATE_REQUIRED";
        }

        if (request.PeriodFrom == default || request.PeriodTo == default)
        {
            return "PERIOD_REQUIRED";
        }

        if (request.SavedAt == default)
        {
            return "SAVED_AT_REQUIRED";
        }

        var savedUtcDate = DateOnly.FromDateTime(request.SavedAt.UtcDateTime);
        var latestBusinessDate = savedUtcDate == DateOnly.MaxValue ? savedUtcDate : savedUtcDate.AddDays(1);
        if (request.BusinessDate > latestBusinessDate)
        {
            return "INVALID_BUSINESS_DATE";
        }

        if ((request.CashierId?.Trim().Length ?? 0) > 64)
        {
            return "CASHIER_ID_TOO_LONG";
        }

        if ((request.CashierName?.Trim().Length ?? 0) > 128)
        {
            return "CASHIER_NAME_TOO_LONG";
        }

        if ((request.AppVersion?.Trim().Length ?? 0) > 64)
        {
            return "APP_VERSION_TOO_LONG";
        }

        if (request.OrderCount < 0)
        {
            return "INVALID_ORDER_COUNT";
        }

        if (Math.Abs(request.ReturnQuantity) > MaximumAbsoluteValue || Math.Abs(request.RefundAmount) > MaximumAbsoluteValue)
        {
            return "AMOUNT_OUT_OF_RANGE";
        }

        // 支付方式：恰好 Cash/Card/Voucher 各一条，Net ≈ Sales − Refund。
        var tenders = request.Tenders;
        if (tenders is null || tenders.Count != DailyCloseContractConstants.TenderMethods.Count)
        {
            return "INVALID_TENDERS";
        }

        var byMethod = new Dictionary<string, DailyCloseTenderSync>(StringComparer.Ordinal);
        foreach (var tender in tenders)
        {
            var method = DailyCloseContractConstants.TenderMethods.FirstOrDefault(candidate =>
                string.Equals(candidate, tender.Method?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (method is null || !byMethod.TryAdd(method, tender))
            {
                return "INVALID_TENDERS";
            }

            var sales = Round(tender.SalesAmount);
            var refund = Round(tender.RefundAmount);
            var net = Round(tender.NetAmount);
            if (Math.Abs(net - (sales - refund)) > RoundingTolerance)
            {
                return "TENDER_NET_MISMATCH";
            }
        }

        // 现金盘点：恰好 11 档各一次，数量 0..100000。
        var denominations = DailyCloseContractConstants.DenominationCents;
        var cashCounts = request.CashCounts;
        if (cashCounts is null || cashCounts.Count != denominations.Count)
        {
            return "INVALID_CASH_COUNTS";
        }

        var quantities = new Dictionary<int, int>();
        foreach (var count in cashCounts)
        {
            if (!denominations.Contains(count.DenominationCents) || !quantities.TryAdd(count.DenominationCents, count.Quantity))
            {
                return "INVALID_CASH_COUNTS";
            }

            if (count.Quantity < 0 || count.Quantity > MaximumCashCountQuantity)
            {
                return "INVALID_CASH_COUNT_QUANTITY";
            }
        }

        long noteCents = 0;
        long coinCents = 0;
        foreach (var denomination in denominations)
        {
            var cents = (long)denomination * quantities[denomination];
            if (denomination >= DailyCloseContractConstants.NoteMinimumDenominationCents)
            {
                noteCents += cents;
            }
            else
            {
                coinCents += cents;
            }
        }

        // 盘点金额只能由数量推出：客户端汇总值必须与之精确一致。
        if (Round(request.NoteSubtotal) != noteCents / 100m)
        {
            return "INVALID_NOTE_SUBTOTAL";
        }

        if (Round(request.CoinSubtotal) != coinCents / 100m)
        {
            return "INVALID_COIN_SUBTOTAL";
        }

        var counted = Round(request.CountedCashAmount);
        if (counted != (noteCents + coinCents) / 100m)
        {
            return "INVALID_COUNTED_CASH_AMOUNT";
        }

        var cashNet = Round(byMethod[DailyCloseContractConstants.TenderCash].NetAmount);
        if (Math.Abs(Round(request.CashDifference) - (counted - cashNet)) > RoundingTolerance)
        {
            return "INVALID_CASH_DIFFERENCE";
        }

        return null;
    }

    private static decimal Round(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);
}
