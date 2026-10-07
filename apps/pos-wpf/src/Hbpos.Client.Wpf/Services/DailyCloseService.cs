using System.Globalization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Contracts.Orders;
using Microsoft.Data.Sqlite;

namespace Hbpos.Client.Wpf.Services;

public enum CashDenominationKind
{
    Note = 1,
    Coin = 2
}

public sealed record CashDenomination(decimal Value, string Label, CashDenominationKind Kind);

public sealed record CashDenominationCount(decimal Value, string Label, CashDenominationKind Kind, int Quantity)
{
    public decimal Amount => decimal.Round(Value * Quantity, 2, MidpointRounding.AwayFromZero);
}

public sealed record DailyClosePaymentSummary(
    PaymentMethodKind Method,
    decimal SalesAmount,
    decimal RefundAmount,
    decimal NetAmount,
    int TransactionCount)
{
    public string MethodLabel => Method switch
    {
        PaymentMethodKind.Cash => "Cash",
        PaymentMethodKind.Card => "Card",
        PaymentMethodKind.Voucher => "Voucher",
        _ => Method.ToString()
    };
}

public sealed record DailyCloseReport(
    DateTime BusinessDate,
    DateTimeOffset PeriodFrom,
    DateTimeOffset PeriodTo,
    string StoreCode,
    string DeviceCode,
    string CashierId,
    string CashierName,
    int OrderCount,
    IReadOnlyList<DailyClosePaymentSummary> PaymentSummaries,
    decimal RefundAmount,
    decimal ReturnQuantity)
{
    public decimal SalesAmount => Round(PaymentSummaries.Sum(summary => summary.SalesAmount));

    public decimal NetAmount => Round(PaymentSummaries.Sum(summary => summary.NetAmount));

    public decimal SystemCashAmount => Round(PaymentSummaries
        .FirstOrDefault(summary => summary.Method == PaymentMethodKind.Cash)
        ?.NetAmount ?? 0m);

    private static decimal Round(decimal amount)
    {
        return decimal.Round(amount, 2, MidpointRounding.AwayFromZero);
    }
}

public sealed record DailyCloseArchive(
    Guid DailyCloseGuid,
    DailyCloseReport Report,
    IReadOnlyList<CashDenominationCount> CashCounts,
    DateTimeOffset SavedAt,
    decimal NoteSubtotal,
    decimal CoinSubtotal,
    decimal CountedCashAmount,
    decimal CashDifference)
{
    public string ShortArchiveId => DailyCloseGuid.ToString("N")[..8].ToUpperInvariant();

    public string SavedAtDisplay => SavedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
}

public interface ILocalDailyCloseRepository
{
    Task<DailyCloseReport> LoadReportAsync(
        PosSessionState session,
        DateTime businessDate,
        CancellationToken cancellationToken = default);

    Task<DailyCloseArchive> SaveAsync(
        DailyCloseReport report,
        IReadOnlyList<CashDenominationCount> cashCounts,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DailyCloseArchive>> GetArchivesAsync(
        PosSessionState session,
        DateTime businessDate,
        CancellationToken cancellationToken = default);
}

public interface IDailyCloseService
{
    IReadOnlyList<CashDenomination> Denominations { get; }

    Task<DailyCloseReport> LoadReportAsync(
        PosSessionState session,
        DateTime businessDate,
        CancellationToken cancellationToken = default);

    Task<DailyCloseArchive> SaveAsync(
        PosSessionState session,
        DateTime businessDate,
        IReadOnlyList<CashDenominationCount> cashCounts,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DailyCloseArchive>> GetArchivesAsync(
        PosSessionState session,
        DateTime businessDate,
        CancellationToken cancellationToken = default);
}

internal readonly record struct DailyClosePeriod(DateTimeOffset From, DateTimeOffset To);

internal static class DailyClosePeriodFactory
{
    public static DailyClosePeriod Create(DateTime businessDate, TimeZoneInfo? timeZone = null)
    {
        var localTimeZone = timeZone ?? TimeZoneInfo.Local;
        var periodFrom = CreateBoundary(businessDate.Date, localTimeZone);
        var periodTo = CreateBoundary(businessDate.Date.AddDays(1), localTimeZone);
        return new DailyClosePeriod(periodFrom, periodTo);
    }

    private static DateTimeOffset CreateBoundary(DateTime localDateTime, TimeZoneInfo timeZone)
    {
        var unspecifiedLocalTime = DateTime.SpecifyKind(localDateTime, DateTimeKind.Unspecified);
        // 日结边界分别计算当天零点与次日零点偏移，避免夏令时切换日多算或漏算。
        return new DateTimeOffset(unspecifiedLocalTime, timeZone.GetUtcOffset(unspecifiedLocalTime));
    }
}

public sealed class DailyCloseService(
    ILocalDailyCloseRepository repository,
    ILocalSqliteCheckpointService? checkpointService = null,
    IDailyCloseUploadScheduler? uploadScheduler = null) : IDailyCloseService
{
    public static IReadOnlyList<CashDenomination> AustralianDenominations { get; } =
    [
        new(100m, "$100", CashDenominationKind.Note),
        new(50m, "$50", CashDenominationKind.Note),
        new(20m, "$20", CashDenominationKind.Note),
        new(10m, "$10", CashDenominationKind.Note),
        new(5m, "$5", CashDenominationKind.Note),
        new(2m, "$2", CashDenominationKind.Coin),
        new(1m, "$1", CashDenominationKind.Coin),
        new(0.50m, "50c", CashDenominationKind.Coin),
        new(0.20m, "20c", CashDenominationKind.Coin),
        new(0.10m, "10c", CashDenominationKind.Coin),
        new(0.05m, "5c", CashDenominationKind.Coin)
    ];

    public IReadOnlyList<CashDenomination> Denominations => AustralianDenominations;

    public Task<DailyCloseReport> LoadReportAsync(
        PosSessionState session,
        DateTime businessDate,
        CancellationToken cancellationToken = default)
    {
        var sessionSnapshot = session with { };
        var businessDateSnapshot = businessDate.Date;
        return Task.Run(
            () => repository.LoadReportAsync(sessionSnapshot, businessDateSnapshot, cancellationToken),
            cancellationToken);
    }

    public async Task<DailyCloseArchive> SaveAsync(
        PosSessionState session,
        DateTime businessDate,
        IReadOnlyList<CashDenominationCount> cashCounts,
        CancellationToken cancellationToken = default)
    {
        var sessionSnapshot = session with { };
        var businessDateSnapshot = businessDate.Date;
        var cashCountsSnapshot = NormalizeCounts(cashCounts).ToArray();

        return await Task.Run(async () =>
        {
            var report = await repository.LoadReportAsync(sessionSnapshot, businessDateSnapshot, cancellationToken);
            var archive = await repository.SaveAsync(report, cashCountsSnapshot, cancellationToken);
            // SaveAsync 已提交后不能再把调用方取消报告成“日结未保存”。
            await TryCheckpointWalAfterDailyCloseAsync(CancellationToken.None);
            // 本地提交成功后才唤醒上传 Worker；唤醒只是尽力而为，失败不能把"日结已保存"变成失败。
            TryRequestDailyCloseUpload();
            return archive;
        }, cancellationToken);
    }

    public Task<IReadOnlyList<DailyCloseArchive>> GetArchivesAsync(
        PosSessionState session,
        DateTime businessDate,
        CancellationToken cancellationToken = default)
    {
        var sessionSnapshot = session with { };
        var businessDateSnapshot = businessDate.Date;
        return Task.Run(
            () => repository.GetArchivesAsync(sessionSnapshot, businessDateSnapshot, cancellationToken),
            cancellationToken);
    }

    private void TryRequestDailyCloseUpload()
    {
        if (uploadScheduler is null)
        {
            return;
        }

        try
        {
            uploadScheduler.RequestUpload();
        }
        catch (Exception ex)
        {
            // 日结已经落库：唤醒失败时 Worker 的 30 秒轮询仍会把这条 Pending 记录补传出去。
            ConsoleLog.WriteError(
                "DailyCloseUpload",
                $"daily close upload wake-up failed error={ex.GetType().Name} message={ex.Message}",
                exception: ex);
        }
    }

    private async Task TryCheckpointWalAfterDailyCloseAsync(CancellationToken cancellationToken)
    {
        if (checkpointService is null)
        {
            return;
        }

        try
        {
            // 日结完成后是低频维护点，checkpoint 失败不能回滚已经保存的日结记录。
            await checkpointService.CheckpointWalAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            ConsoleLog.WriteError(
                "SQLite",
                $"daily close wal checkpoint failed error={ex.GetType().Name} message={ex.Message}",
                exception: ex);
        }
    }

    private static IReadOnlyList<CashDenominationCount> NormalizeCounts(IReadOnlyList<CashDenominationCount> cashCounts)
    {
        return AustralianDenominations
            .Select(denomination =>
            {
                var count = cashCounts.FirstOrDefault(item => item.Kind == denomination.Kind && item.Value == denomination.Value);
                return new CashDenominationCount(denomination.Value, denomination.Label, denomination.Kind, Math.Max(0, count?.Quantity ?? 0));
            })
            .ToList();
    }
}

// partial：上传相关的仓储方法在 LocalDailyCloseRepository.Upload.cs，复用本文件里的私有读取/规整方法。
public sealed partial class LocalDailyCloseRepository(LocalSqliteStore store, TimeZoneInfo? businessTimeZone = null) : ILocalDailyCloseRepository
{
    public async Task<DailyCloseReport> LoadReportAsync(
        PosSessionState session,
        DateTime businessDate,
        CancellationToken cancellationToken = default)
    {
        var period = DailyClosePeriodFactory.Create(businessDate, businessTimeZone);

        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        var orderGuids = await ReadOrderGuidsAsync(connection, session, period.From, period.To, cancellationToken);
        var paymentSummaries = await ReadPaymentSummariesAsync(connection, orderGuids, cancellationToken);
        var returnQuantity = await ReadReturnQuantityAsync(connection, orderGuids, cancellationToken);

        return new DailyCloseReport(
            businessDate.Date,
            period.From,
            period.To,
            session.StoreCode,
            session.DeviceCode,
            session.CashierId,
            session.CashierName,
            orderGuids.Count,
            paymentSummaries,
            Round(paymentSummaries.Sum(summary => summary.RefundAmount)),
            returnQuantity);
    }

    public async Task<DailyCloseArchive> SaveAsync(
        DailyCloseReport report,
        IReadOnlyList<CashDenominationCount> cashCounts,
        CancellationToken cancellationToken = default)
    {
        var savedAt = DateTimeOffset.Now;
        var dailyCloseGuid = Guid.NewGuid();
        var normalizedCounts = NormalizeCounts(cashCounts);
        var noteSubtotal = Round(normalizedCounts.Where(count => count.Kind == CashDenominationKind.Note).Sum(count => count.Amount));
        var coinSubtotal = Round(normalizedCounts.Where(count => count.Kind == CashDenominationKind.Coin).Sum(count => count.Amount));
        var countedCashAmount = Round(noteSubtotal + coinSubtotal);
        // 现金差额 = 钱箱实盘现金 - 系统中现金支付净额。
        var cashDifference = Round(countedCashAmount - report.SystemCashAmount);

        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();

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
                ($DailyCloseGuid, $StoreCode, $DeviceCode, $CashierId, $CashierName, $BusinessDate, $PeriodFrom, $PeriodTo, $SavedAt,
                 $OrderCount, $CashSalesAmount, $CashRefundAmount, $CashNetAmount, $CardSalesAmount, $CardRefundAmount, $CardNetAmount,
                 $VoucherSalesAmount, $VoucherRefundAmount, $VoucherNetAmount, $RefundAmount, $ReturnQuantity, $NoteSubtotal,
                 $CoinSubtotal, $CountedCashAmount, $CashDifference);
                """;
            command.Parameters.AddWithValue("$DailyCloseGuid", dailyCloseGuid.ToString());
            command.Parameters.AddWithValue("$StoreCode", report.StoreCode);
            command.Parameters.AddWithValue("$DeviceCode", report.DeviceCode);
            command.Parameters.AddWithValue("$CashierId", report.CashierId);
            command.Parameters.AddWithValue("$CashierName", report.CashierName);
            command.Parameters.AddWithValue("$BusinessDate", FormatDate(report.BusinessDate));
            command.Parameters.AddWithValue("$PeriodFrom", report.PeriodFrom.ToString("O"));
            command.Parameters.AddWithValue("$PeriodTo", report.PeriodTo.ToString("O"));
            command.Parameters.AddWithValue("$SavedAt", savedAt.ToString("O"));
            command.Parameters.AddWithValue("$OrderCount", report.OrderCount);
            AddPaymentParameters(command, report.PaymentSummaries);
            command.Parameters.AddWithValue("$RefundAmount", report.RefundAmount);
            command.Parameters.AddWithValue("$ReturnQuantity", report.ReturnQuantity);
            command.Parameters.AddWithValue("$NoteSubtotal", noteSubtotal);
            command.Parameters.AddWithValue("$CoinSubtotal", coinSubtotal);
            command.Parameters.AddWithValue("$CountedCashAmount", countedCashAmount);
            command.Parameters.AddWithValue("$CashDifference", cashDifference);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var count in normalizedCounts)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO LocalDailyCloseCashCounts
                (DailyCloseGuid, DenominationValue, Label, Kind, Quantity, Amount)
                VALUES ($DailyCloseGuid, $DenominationValue, $Label, $Kind, $Quantity, $Amount);
                """;
            command.Parameters.AddWithValue("$DailyCloseGuid", dailyCloseGuid.ToString());
            command.Parameters.AddWithValue("$DenominationValue", count.Value);
            command.Parameters.AddWithValue("$Label", count.Label);
            command.Parameters.AddWithValue("$Kind", (int)count.Kind);
            command.Parameters.AddWithValue("$Quantity", count.Quantity);
            command.Parameters.AddWithValue("$Amount", count.Amount);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        return new DailyCloseArchive(dailyCloseGuid, report, normalizedCounts, savedAt, noteSubtotal, coinSubtotal, countedCashAmount, cashDifference);
    }

    public async Task<IReadOnlyList<DailyCloseArchive>> GetArchivesAsync(
        PosSessionState session,
        DateTime businessDate,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DailyCloseGuid, StoreCode, DeviceCode, CashierId, CashierName, BusinessDate, PeriodFrom, PeriodTo, SavedAt,
                   OrderCount, CashSalesAmount, CashRefundAmount, CashNetAmount, CardSalesAmount, CardRefundAmount, CardNetAmount,
                   VoucherSalesAmount, VoucherRefundAmount, VoucherNetAmount, RefundAmount, ReturnQuantity, NoteSubtotal,
                   CoinSubtotal, CountedCashAmount, CashDifference
            FROM LocalDailyCloses
            WHERE StoreCode = $StoreCode
              AND DeviceCode = $DeviceCode
              AND BusinessDate = $BusinessDate
            ORDER BY SavedAt DESC;
            """;
        command.Parameters.AddWithValue("$StoreCode", session.StoreCode);
        command.Parameters.AddWithValue("$DeviceCode", session.DeviceCode);
        command.Parameters.AddWithValue("$BusinessDate", FormatDate(businessDate.Date));

        var rows = new List<DailyCloseArchiveRow>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(ReadArchiveRow(reader));
            }
        }

        var archives = new List<DailyCloseArchive>(rows.Count);
        foreach (var row in rows)
        {
            var cashCounts = NormalizeCounts(await ReadCashCountsAsync(connection, row.DailyCloseGuid, cancellationToken));
            archives.Add(new DailyCloseArchive(
                row.DailyCloseGuid,
                row.Report,
                cashCounts,
                row.SavedAt,
                row.NoteSubtotal,
                row.CoinSubtotal,
                row.CountedCashAmount,
                row.CashDifference));
        }

        return archives;
    }

    /// <summary>读取一行 LocalDailyCloses 的存档头（不含面额明细）；查询存档与上传取数共用同一份映射。</summary>
    private static DailyCloseArchiveRow ReadArchiveRow(SqliteDataReader reader)
    {
        var report = new DailyCloseReport(
            DateTime.ParseExact(ReadString(reader, "BusinessDate"), "yyyy-MM-dd", CultureInfo.InvariantCulture),
            ReadDateTimeOffset(reader, "PeriodFrom"),
            ReadDateTimeOffset(reader, "PeriodTo"),
            ReadString(reader, "StoreCode"),
            ReadString(reader, "DeviceCode"),
            ReadString(reader, "CashierId"),
            ReadString(reader, "CashierName"),
            reader.GetInt32(reader.GetOrdinal("OrderCount")),
            ReadPaymentSummaries(reader),
            ReadDecimal(reader, "RefundAmount"),
            ReadDecimal(reader, "ReturnQuantity"));
        return new DailyCloseArchiveRow(
            ReadGuid(reader, "DailyCloseGuid"),
            report,
            ReadDateTimeOffset(reader, "SavedAt"),
            ReadDecimal(reader, "NoteSubtotal"),
            ReadDecimal(reader, "CoinSubtotal"),
            ReadDecimal(reader, "CountedCashAmount"),
            ReadDecimal(reader, "CashDifference"));
    }

    private static async Task<IReadOnlyList<Guid>> ReadOrderGuidsAsync(
        SqliteConnection connection,
        PosSessionState session,
        DateTimeOffset periodFrom,
        DateTimeOffset periodTo,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT OrderGuid
            FROM LocalOrders
            WHERE StoreCode = $StoreCode
              AND DeviceCode = $DeviceCode
              -- 日结使用本地日期半开区间，避免 23:59:59 精度边界漏单。
              AND julianday(SoldAt) >= julianday($PeriodFrom)
              AND julianday(SoldAt) < julianday($PeriodTo);
            """;
        command.Parameters.AddWithValue("$StoreCode", session.StoreCode);
        command.Parameters.AddWithValue("$DeviceCode", session.DeviceCode);
        command.Parameters.AddWithValue("$PeriodFrom", periodFrom.ToString("O"));
        command.Parameters.AddWithValue("$PeriodTo", periodTo.ToString("O"));

        var orderGuids = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            orderGuids.Add(Guid.Parse(reader.GetString(0)));
        }

        return orderGuids;
    }

    private static async Task<IReadOnlyList<DailyClosePaymentSummary>> ReadPaymentSummariesAsync(
        SqliteConnection connection,
        IReadOnlyList<Guid> orderGuids,
        CancellationToken cancellationToken)
    {
        var payments = Enum.GetValues<PaymentMethodKind>()
            .ToDictionary(method => method, method => new PaymentAccumulator(method));

        if (orderGuids.Count > 0)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT Method, Amount
                FROM LocalPayments
                WHERE OrderGuid IN ({CreateInClause(command, orderGuids)});
                """;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var method = (PaymentMethodKind)reader.GetInt32(reader.GetOrdinal("Method"));
                var amount = ReadDecimal(reader, "Amount");
                if (!payments.TryGetValue(method, out var accumulator))
                {
                    accumulator = new PaymentAccumulator(method);
                    payments[method] = accumulator;
                }

                accumulator.TransactionCount++;
                // 正数支付计入销售，负数支付计入退款且显示绝对值。
                if (amount >= 0m)
                {
                    accumulator.SalesAmount += amount;
                }
                else
                {
                    accumulator.RefundAmount += Math.Abs(amount);
                }
            }
        }

        return payments.Values
            .OrderBy(item => (int)item.Method)
            .Select(item => new DailyClosePaymentSummary(
                item.Method,
                Round(item.SalesAmount),
                Round(item.RefundAmount),
                Round(item.SalesAmount - item.RefundAmount),
                item.TransactionCount))
            .ToList();
    }

    private static async Task<decimal> ReadReturnQuantityAsync(
        SqliteConnection connection,
        IReadOnlyList<Guid> orderGuids,
        CancellationToken cancellationToken)
    {
        if (orderGuids.Count == 0)
        {
            return 0m;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT Quantity
            FROM LocalOrderLines
            WHERE Kind = $Kind
              AND OrderGuid IN ({CreateInClause(command, orderGuids)});
            """;
        command.Parameters.AddWithValue("$Kind", (int)OrderLineKind.Return);

        var quantity = 0m;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            quantity += ReadDecimal(reader, "Quantity");
        }

        return quantity;
    }

    private static async Task<IReadOnlyList<CashDenominationCount>> ReadCashCountsAsync(
        SqliteConnection connection,
        Guid dailyCloseGuid,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DenominationValue, Label, Kind, Quantity
            FROM LocalDailyCloseCashCounts
            WHERE DailyCloseGuid = $DailyCloseGuid
            ORDER BY Kind, DenominationValue DESC;
            """;
        command.Parameters.AddWithValue("$DailyCloseGuid", dailyCloseGuid.ToString());

        var counts = new List<CashDenominationCount>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            counts.Add(new CashDenominationCount(
                ReadDecimal(reader, "DenominationValue"),
                ReadString(reader, "Label"),
                (CashDenominationKind)reader.GetInt32(reader.GetOrdinal("Kind")),
                reader.GetInt32(reader.GetOrdinal("Quantity"))));
        }

        return counts;
    }

    private static IReadOnlyList<DailyClosePaymentSummary> ReadPaymentSummaries(SqliteDataReader reader)
    {
        return
        [
            ReadPaymentSummary(reader, PaymentMethodKind.Cash, "Cash"),
            ReadPaymentSummary(reader, PaymentMethodKind.Card, "Card"),
            ReadPaymentSummary(reader, PaymentMethodKind.Voucher, "Voucher")
        ];
    }

    private static DailyClosePaymentSummary ReadPaymentSummary(SqliteDataReader reader, PaymentMethodKind method, string prefix)
    {
        return new DailyClosePaymentSummary(
            method,
            ReadDecimal(reader, $"{prefix}SalesAmount"),
            ReadDecimal(reader, $"{prefix}RefundAmount"),
            ReadDecimal(reader, $"{prefix}NetAmount"),
            0);
    }

    private static void AddPaymentParameters(SqliteCommand command, IReadOnlyList<DailyClosePaymentSummary> summaries)
    {
        AddPaymentParameter(command, summaries, PaymentMethodKind.Cash, "Cash");
        AddPaymentParameter(command, summaries, PaymentMethodKind.Card, "Card");
        AddPaymentParameter(command, summaries, PaymentMethodKind.Voucher, "Voucher");
    }

    private static void AddPaymentParameter(SqliteCommand command, IReadOnlyList<DailyClosePaymentSummary> summaries, PaymentMethodKind method, string prefix)
    {
        var summary = summaries.FirstOrDefault(item => item.Method == method) ?? new DailyClosePaymentSummary(method, 0m, 0m, 0m, 0);
        command.Parameters.AddWithValue($"${prefix}SalesAmount", summary.SalesAmount);
        command.Parameters.AddWithValue($"${prefix}RefundAmount", summary.RefundAmount);
        command.Parameters.AddWithValue($"${prefix}NetAmount", summary.NetAmount);
    }

    private static string CreateInClause(SqliteCommand command, IReadOnlyList<Guid> orderGuids)
    {
        var names = new List<string>(orderGuids.Count);
        for (var index = 0; index < orderGuids.Count; index++)
        {
            var name = $"$OrderGuid{index}";
            names.Add(name);
            command.Parameters.AddWithValue(name, orderGuids[index].ToString());
        }

        return string.Join(", ", names);
    }

    private static IReadOnlyList<CashDenominationCount> NormalizeCounts(IReadOnlyList<CashDenominationCount> cashCounts)
    {
        return DailyCloseService.AustralianDenominations
            .Select(denomination =>
            {
                var count = cashCounts.FirstOrDefault(item => item.Kind == denomination.Kind && item.Value == denomination.Value);
                return new CashDenominationCount(denomination.Value, denomination.Label, denomination.Kind, Math.Max(0, count?.Quantity ?? 0));
            })
            .ToList();
    }

    private static decimal Round(decimal amount)
    {
        return decimal.Round(amount, 2, MidpointRounding.AwayFromZero);
    }

    private static string FormatDate(DateTime value)
    {
        return value.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static Guid ReadGuid(SqliteDataReader reader, string name)
    {
        return Guid.Parse(ReadString(reader, name));
    }

    private static string ReadString(SqliteDataReader reader, string name)
    {
        return reader.GetString(reader.GetOrdinal(name));
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

    private static DateTimeOffset ReadDateTimeOffset(SqliteDataReader reader, string name)
    {
        return DateTimeOffset.Parse(ReadString(reader, name), CultureInfo.InvariantCulture);
    }

    private sealed class PaymentAccumulator(PaymentMethodKind method)
    {
        public PaymentMethodKind Method { get; } = method;

        public decimal SalesAmount { get; set; }

        public decimal RefundAmount { get; set; }

        public int TransactionCount { get; set; }
    }

    private sealed record DailyCloseArchiveRow(
        Guid DailyCloseGuid,
        DailyCloseReport Report,
        DateTimeOffset SavedAt,
        decimal NoteSubtotal,
        decimal CoinSubtotal,
        decimal CountedCashAmount,
        decimal CashDifference);
}

public sealed class NoopDailyCloseService : IDailyCloseService
{
    public static NoopDailyCloseService Instance { get; } = new();

    public IReadOnlyList<CashDenomination> Denominations => DailyCloseService.AustralianDenominations;

    private NoopDailyCloseService()
    {
    }

    public Task<DailyCloseReport> LoadReportAsync(PosSessionState session, DateTime businessDate, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(CreateEmptyReport(session, businessDate));
    }

    public Task<DailyCloseArchive> SaveAsync(
        PosSessionState session,
        DateTime businessDate,
        IReadOnlyList<CashDenominationCount> cashCounts,
        CancellationToken cancellationToken = default)
    {
        var report = CreateEmptyReport(session, businessDate);
        var counts = DailyCloseService.AustralianDenominations
            .Select(denomination => new CashDenominationCount(denomination.Value, denomination.Label, denomination.Kind, 0))
            .ToList();
        return Task.FromResult(new DailyCloseArchive(Guid.NewGuid(), report, counts, DateTimeOffset.Now, 0m, 0m, 0m, 0m));
    }

    public Task<IReadOnlyList<DailyCloseArchive>> GetArchivesAsync(
        PosSessionState session,
        DateTime businessDate,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<DailyCloseArchive>>([]);
    }

    private static DailyCloseReport CreateEmptyReport(PosSessionState session, DateTime businessDate)
    {
        var period = DailyClosePeriodFactory.Create(businessDate);
        return new DailyCloseReport(
            businessDate.Date,
            period.From,
            period.To,
            session.StoreCode,
            session.DeviceCode,
            session.CashierId,
            session.CashierName,
            0,
            Enum.GetValues<PaymentMethodKind>().Select(method => new DailyClosePaymentSummary(method, 0m, 0m, 0m, 0)).ToList(),
            0m,
            0m);
    }
}
