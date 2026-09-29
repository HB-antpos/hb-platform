using System.Reflection;
using System.Runtime.CompilerServices;
using AutoMapper;
using BlazorApp.Api.Data;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class RevenueReportSnapshotSqlServerFactAttribute : FactAttribute
{
    private const string ConnectionEnvironmentVariable = "HB_TEST_SQLSERVER_CONNECTION";

    public RevenueReportSnapshotSqlServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable)))
        {
            Skip = $"未配置 {ConnectionEnvironmentVariable}，跳过真实 SQL Server 营业额快照验证。";
        }
    }
}

[Trait("Category", "SQL")]
public sealed class RevenueReportSnapshotSqlServerIntegrationTests
{
    private const string SqlServerTestConnectionEnvVar = "HB_TEST_SQLSERVER_CONNECTION";
    private static readonly DateTime SeedDate = new(2026, 9, 7);
    private static readonly DateTime EmptyDate = new(2026, 9, 8);
    private static readonly string[] AuthorizedStoreCodes =
    {
        "1003", "1004", "1005", "1009", "1012", "1014", "1015", "1024",
    };

    [RevenueReportSnapshotSqlServerFact]
    public async Task 店长显式八店范围只返回授权门店并保留中文小数金额()
    {
        await using var fixture = await RevenueSnapshotSqlServerFixture.CreateAsync();
        var result = await fixture.CreateService().GetRevenueReportSnapshotAsync(
            new DateRangeDto { StartDate = SeedDate, EndDate = SeedDate },
            AuthorizedStoreCodes.ToList(),
            AuthorizedStoreCodes.ToList());

        Assert.False(result.StatisticsPending);
        Assert.Equal(AuthorizedStoreCodes.Length, result.Branches.Count);
        Assert.Equal(
            AuthorizedStoreCodes.OrderBy(code => code),
            result.Branches.Select(row => row.BranchCode).OrderBy(code => code));
        Assert.DoesNotContain(result.Branches, row => row.BranchCode == "OUTSIDE");
        var store = Assert.Single(result.Branches, row => row.BranchCode == "1003");
        Assert.Equal("一〇〇三店", store.BranchName);
        Assert.Equal(100.13m, store.Revenue);
        Assert.Equal(3, store.OrderCount);
    }

    [RevenueReportSnapshotSqlServerFact]
    public async Task 空focus保留授权排行且分时和周层级为空()
    {
        await using var fixture = await RevenueSnapshotSqlServerFixture.CreateAsync();
        var result = await fixture.CreateService().GetRevenueReportSnapshotAsync(
            new DateRangeDto { StartDate = SeedDate, EndDate = SeedDate },
            AuthorizedStoreCodes.ToList(),
            new List<string>());

        Assert.False(result.StatisticsPending);
        Assert.Equal(AuthorizedStoreCodes.Length, result.Branches.Count);
        Assert.Equal(100.13m, Assert.Single(result.Branches, row => row.BranchCode == "1003").Revenue);
        Assert.Empty(result.Hourly);
        Assert.Empty(result.Weekly);
    }

    [RevenueReportSnapshotSqlServerFact]
    public async Task 空日期无统计数据时不抛错并标记Pending()
    {
        await using var fixture = await RevenueSnapshotSqlServerFixture.CreateAsync();
        var result = await fixture.CreateService().GetRevenueReportSnapshotAsync(
            new DateRangeDto { StartDate = EmptyDate, EndDate = EmptyDate },
            AuthorizedStoreCodes.ToList(),
            AuthorizedStoreCodes.ToList());

        Assert.True(result.StatisticsPending);
        Assert.True(result.CurrentPeriodPending);
        Assert.Equal(AuthorizedStoreCodes.Length, result.Branches.Count);
        Assert.All(result.Branches, row =>
        {
            Assert.Contains(row.BranchCode, AuthorizedStoreCodes);
            Assert.Equal(0m, row.Revenue);
        });
        Assert.Empty(result.Hourly);
        Assert.Empty(result.Weekly);
    }

    [RevenueReportSnapshotSqlServerFact]
    public async Task 管理员空范围读取活动门店目录并包含OUTSIDE()
    {
        await using var fixture = await RevenueSnapshotSqlServerFixture.CreateAsync();
        var result = await fixture.CreateService().GetRevenueReportSnapshotAsync(
            new DateRangeDto { StartDate = SeedDate, EndDate = SeedDate },
            null,
            null);

        Assert.False(result.StatisticsPending);
        Assert.Equal(9, result.Branches.Count);
        var outside = Assert.Single(result.Branches, row => row.BranchCode == "OUTSIDE");
        Assert.Equal("店外测试", outside.BranchName);
        Assert.Equal(999.99m, outside.Revenue);
    }

    [RevenueReportSnapshotSqlServerFact]
    public async Task 管理员全店视图的周层级与分时排除停用门店与排行同口径()
    {
        await using var fixture = await RevenueSnapshotSqlServerFixture.CreateAsync();
        // 停用门店去年仍有日统计与小时统计：只能进排行口径之外，不能单独进周层级和分时。
        await fixture.ExecuteAsync("""
            INSERT INTO [dbo].[Store] ([StoreCode], [StoreName], [IsActive], [IsDeleted]) VALUES (N'CLOSED', N'已关店', 0, 0);
            INSERT INTO [dbo].[StoreSalesStatistic] ([Date], [BranchCode], [BranchName], [TotalAmount], [OrderCount]) VALUES
                ('2026-09-07', N'CLOSED', N'已关店', 777.77, 7);
            INSERT INTO [dbo].[HourlySalesStatistic] ([Date], [Hour], [BranchCode], [BranchName], [TotalAmount], [OrderCount]) VALUES
                ('2026-09-07', 9, N'CLOSED', N'已关店', 777.77, 7);
            """);

        var result = await fixture.CreateService().GetRevenueReportSnapshotAsync(
            new DateRangeDto { StartDate = SeedDate, EndDate = SeedDate },
            null,
            null);

        Assert.DoesNotContain(result.Branches, row => row.BranchCode == "CLOSED");
        Assert.DoesNotContain(result.Hourly, row => row.BranchCode == "CLOSED");
        var week = Assert.Single(result.Weekly);
        Assert.DoesNotContain(week.Children!, child => child.Key.EndsWith("-CLOSED", StringComparison.Ordinal));
        Assert.Equal(result.Branches.Sum(row => row.Revenue), week.Revenue);
        Assert.Equal(result.Branches.Sum(row => row.Revenue), result.Hourly.Sum(row => row.Revenue));

        // 显式请求停用门店时按请求返回，不被目录收窄。
        var explicitScope = await fixture.CreateService().GetRevenueReportSnapshotAsync(
            new DateRangeDto { StartDate = SeedDate, EndDate = SeedDate },
            null,
            new List<string> { "CLOSED" });
        Assert.Equal(777.77m, Assert.Single(explicitScope.Hourly).Revenue);
    }

    [RevenueReportSnapshotSqlServerFact]
    public async Task 多日区间最后一天是今天时批次单独返回今天与同期对应日()
    {
        await using var fixture = await RevenueSnapshotSqlServerFixture.CreateAsync();
        var today = SalesStatisticsBusinessDate.Today();
        var yesterday = today.AddDays(-1);
        var compareToday = today.AddDays(-364);
        var compareYesterday = today.AddDays(-365);
        string D(DateTime value) => value.ToString("yyyy-MM-dd");
        await fixture.ExecuteAsync($$"""
            INSERT INTO [dbo].[StoreSalesStatistic] ([Date], [BranchCode], [BranchName], [TotalAmount], [OrderCount]) VALUES
                ('{{D(yesterday)}}', N'1003', N'一〇〇三店', 100.00, 2),
                ('{{D(today)}}', N'1003', N'一〇〇三店', 200.00, 4),
                ('{{D(compareYesterday)}}', N'1003', N'一〇〇三店', 150.00, 3),
                ('{{D(compareToday)}}', N'1003', N'一〇〇三店', 300.00, 6);
            INSERT INTO [dbo].[HourlySalesStatistic] ([Date], [Hour], [BranchCode], [BranchName], [TotalAmount], [OrderCount]) VALUES
                ('{{D(yesterday)}}', 9, N'1003', N'一〇〇三店', 100.00, 2),
                ('{{D(today)}}', 9, N'1003', N'一〇〇三店', 50.00, 1),
                ('{{D(today)}}', 10, N'1003', N'一〇〇三店', 150.00, 3),
                ('{{D(compareYesterday)}}', 9, N'1003', N'一〇〇三店', 150.00, 3),
                ('{{D(compareToday)}}', 9, N'1003', N'一〇〇三店', 120.00, 2),
                ('{{D(compareToday)}}', 10, N'1003', N'一〇〇三店', 180.00, 4);
            INSERT INTO [dbo].[SalesStatisticRefreshState] ([StatisticType], [Date], [Status], [LastAggregatedAtUtc], [CompletedAtUtc])
            SELECT t.[StatisticType], d.[Date], N'Fresh', SYSUTCDATETIME(), SYSUTCDATETIME()
            FROM (VALUES (N'StoreSales'), (N'HourlySales')) t([StatisticType])
            CROSS JOIN (VALUES ('{{D(yesterday)}}'), ('{{D(today)}}'), ('{{D(compareYesterday)}}'), ('{{D(compareToday)}}')) d([Date]);
            """);
        var codes = new List<string> { "1003" };

        var result = await fixture.CreateService().GetRevenueReportSnapshotAsync(
            new DateRangeDto { StartDate = yesterday, EndDate = today, CompareStartDate = compareYesterday, CompareEndDate = compareToday },
            codes,
            codes);

        Assert.NotNull(result.LastDay);
        var dayBranch = Assert.Single(result.LastDay!.Branches);
        Assert.Equal(200m, dayBranch.Revenue);
        Assert.Equal(300m, dayBranch.RevenueLY);
        var dayTen = Assert.Single(result.LastDay.Hourly, row => row.Hour == "10:00");
        Assert.Equal(150m, dayTen.Revenue);
        Assert.Equal(180m, dayTen.RevenueLY);
        Assert.Equal(4, dayTen.OrderCountLY);
        // 期间 2/3 只进最后一天，区间合计仍是两天之和。
        var rangeNine = Assert.Single(result.Hourly, row => row.Hour == "09:00");
        Assert.Equal(150m, rangeNine.Revenue);
        Assert.Equal(270m, rangeNine.RevenueLY);

        var singleDay = await fixture.CreateService().GetRevenueReportSnapshotAsync(
            new DateRangeDto { StartDate = today, EndDate = today, CompareStartDate = compareToday, CompareEndDate = compareToday },
            codes,
            codes);
        Assert.Null(singleDay.LastDay);
        Assert.Equal(150m, Assert.Single(singleDay.Hourly, row => row.Hour == "10:00").Revenue);
    }

    private sealed class RevenueSnapshotSqlServerFixture : IAsyncDisposable
    {
        private readonly string _masterConnectionString;
        private readonly string _databaseName;
        private readonly string _databaseConnectionString;
        private readonly SqlSugarClient _db;
        private readonly SqlSugarClient _posmDb;
        private readonly MemoryCache _cache = new(new MemoryCacheOptions());

        private RevenueSnapshotSqlServerFixture(
            string masterConnectionString,
            string databaseName,
            string databaseConnectionString)
        {
            _masterConnectionString = masterConnectionString;
            _databaseName = databaseName;
            _databaseConnectionString = databaseConnectionString;
            _db = new SqlSugarClient(CreateConnectionConfig(databaseConnectionString));
            _posmDb = new SqlSugarClient(CreateConnectionConfig(databaseConnectionString));
        }

        public static async Task<RevenueSnapshotSqlServerFixture> CreateAsync()
        {
            var baseConnectionString = Environment.GetEnvironmentVariable(SqlServerTestConnectionEnvVar);
            if (string.IsNullOrWhiteSpace(baseConnectionString))
                throw new InvalidOperationException($"未配置 {SqlServerTestConnectionEnvVar}。");
            EnsureLoopbackSqlServer(baseConnectionString);

            var databaseName = $"HbRevenueSnapshot_{Guid.NewGuid():N}";
            var masterConnectionString = BuildConnectionString(baseConnectionString, "master");
            var databaseConnectionString = BuildConnectionString(baseConnectionString, databaseName);
            await ExecuteNonQueryAsync(
                masterConnectionString,
                $"CREATE DATABASE {QuoteSqlServerName(databaseName)};");

            try
            {
                await ExecuteNonQueryAsync(
                    databaseConnectionString,
                    "ALTER DATABASE CURRENT SET ALLOW_SNAPSHOT_ISOLATION ON;");
                await ExecuteNonQueryAsync(databaseConnectionString, SchemaAndSeedSql);
                return new RevenueSnapshotSqlServerFixture(
                    masterConnectionString,
                    databaseName,
                    databaseConnectionString);
            }
            catch
            {
                await DropDatabaseAsync(masterConnectionString, databaseName);
                throw;
            }
        }

        public Task ExecuteAsync(string sql) => ExecuteNonQueryAsync(_databaseConnectionString, sql);

        public SalesDashboardReactService CreateService()
        {
            return new SalesDashboardReactService(
                CreateSqlSugarContext(_db),
                CreatePosmSqlSugarContext(_posmDb),
                Mock.Of<IMapper>(),
                NullLogger<SalesDashboardReactService>.Instance,
                _cache);
        }

        public async ValueTask DisposeAsync()
        {
            _cache.Dispose();
            _db.Dispose();
            _posmDb.Dispose();
            await DropDatabaseAsync(_masterConnectionString, _databaseName);
        }

        private static readonly string SchemaAndSeedSql = """
            SET NOCOUNT ON;
            CREATE TABLE [dbo].[Store] (
                [StoreCode] nvarchar(50) NOT NULL PRIMARY KEY,
                [StoreName] nvarchar(100) NULL,
                [IsActive] bit NOT NULL,
                [IsDeleted] bit NOT NULL
            );
            CREATE TABLE [dbo].[StoreSalesStatistic] (
                [Date] datetime2(7) NOT NULL,
                [BranchCode] nvarchar(50) NOT NULL,
                [BranchName] nvarchar(100) NOT NULL,
                [TotalAmount] decimal(18,2) NOT NULL,
                [OrderCount] int NOT NULL,
                CONSTRAINT [PK_StoreSalesStatistic] PRIMARY KEY ([Date], [BranchCode])
            );
            CREATE TABLE [dbo].[HourlySalesStatistic] (
                [Date] datetime2(7) NOT NULL,
                [Hour] int NOT NULL,
                [BranchCode] nvarchar(50) NOT NULL,
                [BranchName] nvarchar(100) NULL,
                [TotalAmount] decimal(18,2) NOT NULL,
                [OrderCount] int NULL,
                CONSTRAINT [PK_HourlySalesStatistic] PRIMARY KEY ([Date], [Hour], [BranchCode])
            );
            CREATE TABLE [dbo].[SalesStatisticRefreshState] (
                [StatisticType] nvarchar(80) NOT NULL,
                [Date] datetime2(7) NOT NULL,
                [Status] nvarchar(20) NOT NULL,
                [LastAggregatedAtUtc] datetime2(7) NULL,
                [CompletedAtUtc] datetime2(7) NULL,
                CONSTRAINT [PK_SalesStatisticRefreshState] PRIMARY KEY ([StatisticType], [Date])
            );

            INSERT INTO [dbo].[Store] ([StoreCode], [StoreName], [IsActive], [IsDeleted]) VALUES
                (N'1003', N'一〇〇三店', 1, 0),
                (N'1004', N'一〇〇四店', 1, 0),
                (N'1005', N'一〇〇五店', 1, 0),
                (N'1009', N'一〇〇九店', 1, 0),
                (N'1012', N'一〇一二店', 1, 0),
                (N'1014', N'一〇一四店', 1, 0),
                (N'1015', N'一〇一五店', 1, 0),
                (N'1024', N'一〇二四店', 1, 0),
                (N'OUTSIDE', N'店外测试', 1, 0);

            INSERT INTO [dbo].[StoreSalesStatistic] ([Date], [BranchCode], [BranchName], [TotalAmount], [OrderCount]) VALUES
                ('2026-09-07', N'1003', N'一〇〇三店', 100.13, 3),
                ('2026-09-07', N'1004', N'一〇〇四店', 200.24, 4),
                ('2026-09-07', N'1005', N'一〇〇五店', 300.35, 5),
                ('2026-09-07', N'1009', N'一〇〇九店', 400.49, 6),
                ('2026-09-07', N'1012', N'一〇一二店', 500.12, 7),
                ('2026-09-07', N'1014', N'一〇一四店', 600.14, 8),
                ('2026-09-07', N'1015', N'一〇一五店', 700.15, 9),
                ('2026-09-07', N'1024', N'一〇二四店', 800.24, 10),
                ('2026-09-07', N'OUTSIDE', N'店外测试', 999.99, 11);

            INSERT INTO [dbo].[HourlySalesStatistic] ([Date], [Hour], [BranchCode], [BranchName], [TotalAmount], [OrderCount]) VALUES
                ('2026-09-07', 9, N'1003', N'一〇〇三店', 100.13, 3),
                ('2026-09-07', 9, N'1004', N'一〇〇四店', 200.24, 4),
                ('2026-09-07', 9, N'1005', N'一〇〇五店', 300.35, 5),
                ('2026-09-07', 9, N'1009', N'一〇〇九店', 400.49, 6),
                ('2026-09-07', 9, N'1012', N'一〇一二店', 500.12, 7),
                ('2026-09-07', 9, N'1014', N'一〇一四店', 600.14, 8),
                ('2026-09-07', 9, N'1015', N'一〇一五店', 700.15, 9),
                ('2026-09-07', 9, N'1024', N'一〇二四店', 800.24, 10),
                ('2026-09-07', 9, N'OUTSIDE', N'店外测试', 999.99, 11);

            INSERT INTO [dbo].[SalesStatisticRefreshState]
                ([StatisticType], [Date], [Status], [LastAggregatedAtUtc], [CompletedAtUtc]) VALUES
                (N'StoreSales', '2026-09-07', N'Fresh', '2026-09-07T03:32:59', '2026-09-07T03:32:59'),
                (N'HourlySales', '2026-09-07', N'Fresh', '2026-09-07T03:32:59', '2026-09-07T03:32:59');
            """;

        private static ConnectionConfig CreateConnectionConfig(string connectionString) => new()
        {
            ConnectionString = connectionString,
            DbType = DbType.SqlServer,
            IsAutoCloseConnection = true,
            InitKeyType = InitKeyType.Attribute,
        };

        private static SqlSugarContext CreateSqlSugarContext(ISqlSugarClient db)
        {
            var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(SqlSugarContext));
            typeof(SqlSugarContext).GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(context, db);
            return context;
        }

        private static POSMSqlSugarContext CreatePosmSqlSugarContext(ISqlSugarClient db)
        {
            var context = (POSMSqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(POSMSqlSugarContext));
            typeof(POSMSqlSugarContext).GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(context, db);
            return context;
        }

        private static string BuildConnectionString(string connectionString, string databaseName)
        {
            var builder = new SqlConnectionStringBuilder(connectionString)
            {
                InitialCatalog = databaseName,
            };
            return builder.ConnectionString;
        }

        private static void EnsureLoopbackSqlServer(string connectionString)
        {
            var dataSource = new SqlConnectionStringBuilder(connectionString).DataSource.Trim();
            if (dataSource.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase))
                dataSource = dataSource[4..];
            var parts = dataSource.Split(',', 2, StringSplitOptions.TrimEntries);
            var host = parts[0].Trim().Trim('[', ']');
            if (!host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                && !host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"{SqlServerTestConnectionEnvVar} 必须指向 localhost 或 127.0.0.1，当前 DataSource 不符合环回测试约束。");
            }
            if (parts.Length == 2
                && (!int.TryParse(parts[1], out var port) || port is < 1 or > 65535))
            {
                throw new InvalidOperationException(
                    $"{SqlServerTestConnectionEnvVar} 的 SQL Server 端口无效，当前测试仅允许本地环回连接。");
            }
        }

        private static async Task ExecuteNonQueryAsync(string connectionString, string sql)
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new SqlCommand(sql, connection) { CommandTimeout = 60 };
            await command.ExecuteNonQueryAsync();
        }

        private static async Task DropDatabaseAsync(string masterConnectionString, string databaseName)
        {
            var quotedName = QuoteSqlServerName(databaseName);
            await ExecuteNonQueryAsync(
                masterConnectionString,
                $"""
                IF DB_ID(N'{databaseName}') IS NOT NULL
                BEGIN
                    ALTER DATABASE {quotedName} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                    DROP DATABASE {quotedName};
                END;
                """);
        }

        private static string QuoteSqlServerName(string name)
        {
            return $"[{name.Replace("]", "]]", StringComparison.Ordinal)}]";
        }
    }
}
