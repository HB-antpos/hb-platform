using BlazorApp.Api.Models.DailyClose;
using BlazorApp.Api.Services.StoreCash;
using Microsoft.Data.Sqlite;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 现金日结真实取数口（POSM_DailyClose，SQLite 版同列名表）：只取有实点金额的记录、按分店与营业日区间过滤、
/// 应有现金与统计区间缺失时的还原口径。
/// </summary>
public sealed class PosmCashDailyCloseSourceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SqlSugarClient _db;

    public PosmCashDailyCloseSourceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = _connection.ConnectionString,
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = false,
            InitKeyType = InitKeyType.Attribute,
        });
        // 表由 Hbpos.Api 建，这里手写与生产同列名的 SQLite 版本（与 DailyCloseRecordsTests 一致）。
        _db.Ado.ExecuteCommand("""
            CREATE TABLE POSM_DailyClose (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                DailyCloseGuid TEXT NOT NULL,
                StoreCode TEXT NOT NULL,
                DeviceCode TEXT NOT NULL,
                ClientKind TEXT NOT NULL,
                DetailLevel TEXT NOT NULL,
                DataSource TEXT NOT NULL,
                BackfillBatch TEXT NULL,
                BusinessDate TEXT NOT NULL,
                BusinessDateInferred INTEGER NOT NULL,
                PeriodFromUtc TEXT NULL,
                PeriodToUtc TEXT NULL,
                CashierId TEXT NOT NULL,
                CashierName TEXT NOT NULL,
                SavedAtUtc TEXT NOT NULL,
                AppVersion TEXT NULL,
                OrderCount INTEGER NULL,
                ReturnQuantity NUMERIC NULL,
                CashSalesAmount NUMERIC NULL,
                CashRefundAmount NUMERIC NULL,
                CashNetAmount NUMERIC NULL,
                CardSalesAmount NUMERIC NULL,
                CardRefundAmount NUMERIC NULL,
                CardNetAmount NUMERIC NULL,
                VoucherSalesAmount NUMERIC NULL,
                VoucherRefundAmount NUMERIC NULL,
                VoucherNetAmount NUMERIC NULL,
                RefundAmount NUMERIC NULL,
                ExpectedCashAmount NUMERIC NULL,
                CountedCashAmount NUMERIC NULL,
                CashDifference NUMERIC NULL,
                NoteSubtotal NUMERIC NULL,
                CoinSubtotal NUMERIC NULL,
                CashCountsJson TEXT NULL,
                ReceivedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL
            );
            """);
    }

    [Fact]
    public async Task 只取有实点金额的日结_按分店与营业日区间过滤_回填行的应有与区间按口径还原()
    {
        var full = Insert("S001", "POS_1", new DateTime(2026, 10, 7), "Full", counted: 300m, expected: 310m, difference: -10m);
        var cashOnly = Insert("S001", "POS_2", new DateTime(2026, 10, 7), "CashOnly", counted: 100m, expected: null, difference: -5m, withPeriod: false);
        var bare = Insert("S001", "POS_3", new DateTime(2026, 10, 6), "CashOnly", counted: 80m, expected: null, difference: null, withPeriod: false);
        Insert("S001", "POS_1", new DateTime(2026, 10, 6), "TraceOnly", counted: null, expected: null, difference: null);
        Insert("S009", "POS_9", new DateTime(2026, 10, 7), "Full", counted: 999m, expected: 999m, difference: 0m);
        Insert("S001", "POS_1", new DateTime(2026, 10, 1), "Full", counted: 777m, expected: 777m, difference: 0m);
        var source = new PosmCashDailyCloseSource(_db);

        var archives = await source.GetArchivesAsync(new[] { "S001", " ", "S002" }, new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 7), default);

        Assert.True(source.IsConnected);
        Assert.Equal(3, archives.Count);
        var fullArchive = Assert.Single(archives, item => item.CloseId == full.DailyCloseGuid.ToString("D"));
        Assert.Equal(new DateOnly(2026, 10, 7), fullArchive.BusinessDate);
        Assert.Equal("POS_1", fullArchive.DeviceCode);
        Assert.Equal(300m, fullArchive.CountedCash);
        Assert.Equal(310m, fullArchive.ExpectedCash);
        Assert.Equal(-10m, fullArchive.Variance);
        Assert.Equal(full.PeriodFromUtc, fullArchive.PeriodFromUtc);
        Assert.Equal(full.SavedAtUtc, fullArchive.SavedAtUtc);

        // 回填行只有差异：应有 = 实点 − 差异；没有统计区间时按整个营业日。
        var cashOnlyArchive = Assert.Single(archives, item => item.CloseId == cashOnly.DailyCloseGuid.ToString("D"));
        Assert.Equal(105m, cashOnlyArchive.ExpectedCash);
        Assert.Equal(new DateTime(2026, 10, 7, 0, 0, 0), cashOnlyArchive.PeriodFromUtc);
        Assert.Equal(new DateTime(2026, 10, 8, 0, 0, 0), cashOnlyArchive.PeriodToUtc);

        // 应有与差异都没有：按无差异处理。
        var bareArchive = Assert.Single(archives, item => item.CloseId == bare.DailyCloseGuid.ToString("D"));
        Assert.Equal(80m, bareArchive.ExpectedCash);
        Assert.Equal(0m, bareArchive.Variance);
    }

    [Fact]
    public async Task 没有分店或区间颠倒时直接返回空()
    {
        Insert("S001", "POS_1", new DateTime(2026, 10, 7), "Full", counted: 1m, expected: 1m, difference: 0m);
        var source = new PosmCashDailyCloseSource(_db);

        Assert.Empty(await source.GetArchivesAsync(Array.Empty<string>(), new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 7), default));
        Assert.Empty(await source.GetArchivesAsync(new[] { "S001" }, new DateOnly(2026, 10, 8), new DateOnly(2026, 10, 7), default));
    }

    [Fact]
    public async Task 日结表还没建时按未接入处理_不报错()
    {
        // 独立的临时库（连接串不同），模拟后台先于 POS API 上线、表尚未建立。
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-no-daily-close.db");
        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            using var db = new SqlSugarClient(new ConnectionConfig
            {
                ConnectionString = connection.ConnectionString,
                DbType = DbType.Sqlite,
                IsAutoCloseConnection = false,
                InitKeyType = InitKeyType.Attribute,
            });
            var source = new PosmCashDailyCloseSource(db);

            Assert.False(source.IsConnected);
            Assert.Empty(await source.GetArchivesAsync(new[] { "S001" }, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 7), default));
        }

        SqliteConnection.ClearAllPools();
        File.Delete(path);
    }

    private PosmDailyClose Insert(
        string storeCode,
        string deviceCode,
        DateTime businessDate,
        string detailLevel,
        decimal? counted,
        decimal? expected,
        decimal? difference,
        bool withPeriod = true
    )
    {
        var saved = new DateTime(businessDate.Year, businessDate.Month, businessDate.Day, 10, 0, 0);
        var row = new PosmDailyClose
        {
            DailyCloseGuid = Guid.NewGuid(),
            StoreCode = storeCode,
            DeviceCode = deviceCode,
            ClientKind = "Wpf",
            DetailLevel = detailLevel,
            DataSource = detailLevel == "Full" ? "ClientUpload" : "AuditBackfill",
            BusinessDate = businessDate.Date,
            PeriodFromUtc = withPeriod ? saved.AddHours(-8) : null,
            PeriodToUtc = withPeriod ? saved : null,
            CashierId = "1001",
            CashierName = "Alice",
            SavedAtUtc = saved,
            ExpectedCashAmount = expected,
            CountedCashAmount = counted,
            CashDifference = difference,
            ReceivedAtUtc = saved.AddMinutes(1),
            UpdatedAtUtc = saved.AddMinutes(1),
        };
        _db.Insertable(row).ExecuteCommand();
        return row;
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
