using System.Reflection;
using System.Runtime.CompilerServices;
using BlazorApp.Api.Data;
using BlazorApp.Api.Data.SchemaMigrations;
using BlazorApp.Api.Features.LegacyEmployeeLogs;
using BlazorApp.Api.Interfaces;
using BlazorApp.Shared.DTOs;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class LegacyEmployeeLogSqlServerFactAttribute : FactAttribute
{
    public LegacyEmployeeLogSqlServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(LegacyEmployeeLogSqlServerIntegrationTests.ConnectionEnvironmentVariable)))
        {
            Skip = $"未配置 {LegacyEmployeeLogSqlServerIntegrationTests.ConnectionEnvironmentVariable}，跳过真实 SQL Server 老系统操作日志查询验证。";
        }
    }
}

/// <summary>
/// 在真实 SQL Server 上执行老系统操作日志查询：库排序规则与生产 POSM 一致（Chinese_PRC_90_CI_AS），
/// 表结构照抄生产 EmployeeLogs（全 varchar、随机 GUID 聚集主键），索引由仓库里的 POSMSqlSugarContext.CreateIndexes 创建，
/// 从而验证语句里的索引提示与真实索引名一致。
/// </summary>
public sealed partial class LegacyEmployeeLogSqlServerIntegrationTests
{
    public const string ConnectionEnvironmentVariable = "HB_TEST_SQLSERVER_CONNECTION";

    private const string Tianjiao = "19915D4B-569E-4A66-A9CF-D4CB43BEA7E0";
    private const string Sienna = "86BA2969-F917-49A0-BDB1-99D5EFFD5C7C";
    private const string Oscar = "1EB94E35-4360-4B83-9E9A-0F89A0541647";

    [LegacyEmployeeLogSqlServerFact]
    [Trait("Category", "SQL")]
    public async Task 列表按分店与墙钟时间过滤并返回计数和下拉选项()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CreateService("1013");

        var result = await service.QueryAsync(Day());

        Assert.Equal(LegacyEmployeeLogResultStatus.Ok, result.Status);
        var data = result.Data!;
        // 1013 当天 6 条：另一分店、前一天、次日零点整（半开区间）都不算。
        Assert.Equal(6, data.Total);
        Assert.Equal(
            new[] { "L-1013-6", "L-1013-5", "L-1013-4", "L-1013-3", "L-1013-2", "L-1013-1" },
            data.Items.Select(item => item.Id)
        );
        var first = data.Items[0];
        Assert.Equal(new DateTime(2026, 9, 30, 23, 59, 59), first.OperationTime);
        Assert.Equal(DateTimeKind.Unspecified, first.OperationTime.Kind);
        Assert.Equal("从购物车删除商品: XMAS ＄2 CARDS，编码:xmascard2，数量:5，单价:2.00，总金额:10.00", data.Items.Single(item => item.Id == "L-1013-3").OperationDetail);
        Assert.Equal(2, data.OperationCounts.Single(row => row.Operation == "添加商品").Count);
        Assert.Equal(
            new[] { "Oscar", "Sienna", "Tianjiao Z" },
            data.Employees.Select(row => row.EmployeeName)
        );
        Assert.Equal(new[] { "POS_1013_1047", "POS_1013_2210" }, data.Devices.Select(row => row.DeviceCode));
    }

    [LegacyEmployeeLogSqlServerFact]
    [Trait("Category", "SQL")]
    public async Task 操作类型只过滤列表而计数保留全部类型()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CreateService("1013");
        var request = Day();
        request.Operations = ["删除商品", "开钱箱"];

        var data = (await service.QueryAsync(request)).Data!;

        Assert.Equal(2, data.Total);
        Assert.Equal(new[] { "L-1013-5", "L-1013-3" }, data.Items.Select(item => item.Id));
        Assert.Equal(6, data.OperationCounts.Sum(row => row.Count));
    }

    [LegacyEmployeeLogSqlServerFact]
    [Trait("Category", "SQL")]
    public async Task 员工设备与详情关键字组合过滤且关键字不区分大小写()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CreateService("1013");
        var request = Day();
        request.EmployeeIds = [Tianjiao];
        request.DeviceCode = "POS_1013_1047";
        request.Keyword = "XMASCARD2";

        var data = (await service.QueryAsync(request)).Data!;

        Assert.Equal(new[] { "L-1013-3", "L-1013-2", "L-1013-1" }, data.Items.Select(item => item.Id));
        Assert.Equal(3, data.Total);
        // 下拉选项不受员工、设备、关键字影响。
        Assert.Equal(3, data.Employees.Count);

        var chinese = Day();
        chinese.Keyword = "钱箱";
        Assert.Equal("L-1013-5", Assert.Single((await service.QueryAsync(chinese)).Data!.Items).Id);

        var wildcard = Day();
        wildcard.Keyword = "%";
        // % 按字面匹配：只有详情里真有百分号的那条命中，未转义时会命中全部 6 条。
        Assert.Equal("L-1013-6", Assert.Single((await service.QueryAsync(wildcard)).Data!.Items).Id);
    }

    [LegacyEmployeeLogSqlServerFact]
    [Trait("Category", "SQL")]
    public async Task 多店按时间交错排序且计数与选项覆盖所选分店()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CreateService("1013", LegacyEmployeeLogSqlServerQuery.DefaultKeywordScanRowLimit, "1022");
        var request = Day();
        request.StoreCode = null;
        request.StoreCodes = ["1013", "1022"];

        var data = (await service.QueryAsync(request)).Data!;

        Assert.Equal(7, data.Total);
        // 1022 的 10:00 排在 1013 的 15:20 与 00:06:30 之间。
        Assert.Equal(
            new[] { "L-1013-6", "L-1013-5", "L-1022-1", "L-1013-4", "L-1013-3", "L-1013-2", "L-1013-1" },
            data.Items.Select(item => item.Id)
        );
        Assert.Equal(2, data.OperationCounts.Single(row => row.Operation == "删除商品").Count);
        Assert.Contains(data.Devices, row => row.DeviceCode == "POS_1022_1133");

        var page2 = Day();
        page2.StoreCodes = ["1013", "1022"];
        page2.PageSize = 3;
        page2.PageNumber = 2;
        Assert.Equal(new[] { "L-1013-4", "L-1013-3", "L-1013-2" }, (await service.QueryAsync(page2)).Data!.Items.Select(item => item.Id));

        // 多选中有越权分店时整次拒绝。
        var onlyOne = fixture.CreateService("1013");
        Assert.Equal(LegacyEmployeeLogResultStatus.Forbidden, (await onlyOne.QueryAsync(request)).Status);
    }

    [LegacyEmployeeLogSqlServerFact]
    [Trait("Category", "SQL")]
    public async Task 关键字扫描范围超限时拒绝并提示缩小范围()
    {
        await using var fixture = await Fixture.CreateAsync();
        // 上限 3 行：1013 当天 6 行超限；只看 Oscar（2 行）就在上限内。
        var service = fixture.CreateService("1013", keywordScanRowLimit: 3);
        var request = Day();
        request.Keyword = "钱箱";

        var rejected = await service.QueryAsync(request);
        Assert.Equal(LegacyEmployeeLogResultStatus.Invalid, rejected.Status);
        Assert.Contains("6 条", rejected.Message);

        request.EmployeeIds = [Oscar];
        var narrowed = await service.QueryAsync(request);
        Assert.Equal(LegacyEmployeeLogResultStatus.Ok, narrowed.Status);
        Assert.Equal("L-1013-5", Assert.Single(narrowed.Data!.Items).Id);

        // 不带关键字时不受该上限限制。
        Assert.Equal(6, (await service.QueryAsync(Day())).Data!.Total);
    }

    [LegacyEmployeeLogSqlServerFact]
    [Trait("Category", "SQL")]
    public async Task 分页与升序()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CreateService("1013");
        var request = Day();
        request.PageSize = 4;
        request.PageNumber = 2;
        request.SortOrder = "asc";

        var data = (await service.QueryAsync(request)).Data!;

        Assert.Equal(new[] { "L-1013-5", "L-1013-6" }, data.Items.Select(item => item.Id));
        Assert.Equal(6, data.Total);
    }

    [LegacyEmployeeLogSqlServerFact]
    [Trait("Category", "SQL")]
    public async Task 上下文返回同设备前后五分钟且越权分店被拒()
    {
        await using var fixture = await Fixture.CreateAsync();

        var context = await fixture.CreateService("1013").GetContextAsync("L-1013-3");

        Assert.Equal(LegacyEmployeeLogResultStatus.Ok, context.Status);
        Assert.Equal("L-1013-3", context.Data!.Target.Id);
        // L-1013-2（前 1 分钟，同设备）在窗口内；L-1013-4 是另一台设备；L-1013-1 早 6 分钟在窗口外。
        Assert.Equal(new[] { "L-1013-2", "L-1013-3" }, context.Data.Neighbors.Select(item => item.Id));
        Assert.False(context.Data.Truncated);

        var forbidden = await fixture.CreateService("1022").GetContextAsync("L-1013-3");
        Assert.Equal(LegacyEmployeeLogResultStatus.Forbidden, forbidden.Status);
        Assert.Null(forbidden.Data);

        var missing = await fixture.CreateService("1013").GetContextAsync("not-exists");
        Assert.Equal(LegacyEmployeeLogResultStatus.NotFound, missing.Status);
    }

    private static LegacyEmployeeLogQueryDto Day() => new()
    {
        StoreCode = "1013",
        From = new DateTime(2026, 9, 30),
        To = new DateTime(2026, 10, 1),
    };

    private sealed class Fixture : IAsyncDisposable
    {
        public SqlSugarClient Db => _db;

        private readonly string _masterConnectionString;
        private readonly string _databaseName;
        private readonly SqlSugarClient _db;

        private Fixture(string masterConnectionString, string databaseName, string databaseConnectionString)
        {
            _masterConnectionString = masterConnectionString;
            _databaseName = databaseName;
            _db = new SqlSugarClient(new ConnectionConfig
            {
                ConnectionString = databaseConnectionString,
                DbType = SqlSugar.DbType.SqlServer,
                IsAutoCloseConnection = true,
                InitKeyType = InitKeyType.Attribute,
            });
        }

        public static async Task<Fixture> CreateAsync()
        {
            var baseConnectionString = Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable)!;
            EnsureLoopbackSqlServer(baseConnectionString);
            var databaseName = $"HbLegacyLogs_{Guid.NewGuid():N}";
            var master = new SqlConnectionStringBuilder(baseConnectionString) { InitialCatalog = "master" }.ConnectionString;
            var database = new SqlConnectionStringBuilder(baseConnectionString) { InitialCatalog = databaseName }.ConnectionString;
            await ExecuteAsync(master, $"CREATE DATABASE [{databaseName}] COLLATE Chinese_PRC_90_CI_AS;");
            var fixture = new Fixture(master, databaseName, database);
            try
            {
                await ExecuteAsync(database, SchemaSql);
                CreateContext<POSMSqlSugarContext>(fixture._db).CreateIndexes();
                var indexCount = await fixture._db.Ado.GetIntAsync(
                    $"SELECT COUNT(*) FROM sys.indexes WHERE name = '{LegacyEmployeeLogSqlServerQuery.IndexName}' AND object_id = OBJECT_ID('EmployeeLogs')");
                Assert.Equal(1, indexCount);
                // 风险标记 / 金额 / 核查表与生产同一份迁移脚本；列表查询会读这些表补充风险信息。
                await ExecuteAsync(database, LegacyEmployeeLogRiskSchema.ApplySql);
                await fixture.SeedAsync();
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        public LegacyEmployeeLogQueryService CreateService(
            string accessibleStoreCode,
            int keywordScanRowLimit = LegacyEmployeeLogSqlServerQuery.DefaultKeywordScanRowLimit,
            params string[] moreAccessibleStoreCodes
        )
        {
            var accessible = new[] { accessibleStoreCode }.Concat(moreAccessibleStoreCodes).ToArray();
            var scope = new Mock<ICurrentUserManageableStoreScopeService>();
            scope.Setup(service => service.CanAccessStoreCodeAsync(It.IsAny<string>()))
                .ReturnsAsync((string code) => accessible.Contains(code));
            scope.Setup(service => service.GetScopeAsync()).ReturnsAsync(new CurrentUserManageableStoreScope
            {
                IsAllowed = true,
                IsAuthenticated = true,
                IsStoreManager = true,
                StoreCodes = accessible,
            });
            return new LegacyEmployeeLogQueryService(
                _db, scope.Object, NullLogger<LegacyEmployeeLogQueryService>.Instance, keywordScanRowLimit);
        }

        private async Task SeedAsync()
        {
            await Log("L-1013-0", Tianjiao, "Tianjiao Z", "添加商品", "前一天", "2026-09-29 23:59:59", "POS_1013_1047", "1013");
            await Log("L-1013-1", Tianjiao, "Tianjiao Z", "添加商品", "商品:XMAS ＄2 CARDS，编码:xmascard2，单价:2.00", "2026-09-30 00:00:00", "POS_1013_1047", "1013");
            await Log("L-1013-2", Tianjiao, "Tianjiao Z", "添加商品", "商品:XMAS ＄2 CARDS，编码:xmascard2，单价:2.00", "2026-09-30 00:05:00", "POS_1013_1047", "1013");
            await Log("L-1013-3", Tianjiao, "Tianjiao Z", "删除商品", "从购物车删除商品: XMAS ＄2 CARDS，编码:xmascard2，数量:5，单价:2.00，总金额:10.00", "2026-09-30 00:06:00", "POS_1013_1047", "1013");
            await Log("L-1013-4", Sienna, "Sienna", "修改商品价格", "商品:World Greetings Card Blank，原价格:2.50，新价格:2.00", "2026-09-30 00:06:30", "POS_1013_2210", "1013");
            await Log("L-1013-5", Oscar, "Oscar", "开钱箱", "员工Oscar操作开钱箱", "2026-09-30 15:20:44", "POS_1013_1047", "1013");
            await Log("L-1013-6", Oscar, "Oscar", "支付完成", "订单:019B4349-1260-760E-B547-8693FD8A4FB3支付成功，金额:50%", "2026-09-30 23:59:59", "POS_1013_1047", "1013");
            await Log("L-1013-7", Oscar, "Oscar", "添加商品", "次日零点", "2026-10-01 00:00:00", "POS_1013_1047", "1013");
            await Log("L-1022-1", Sienna, "Sienna", "删除商品", "另一分店 xmascard2", "2026-09-30 10:00:00", "POS_1022_1133", "1022");
        }

        public Task Log(string id, string employeeId, string employeeName, string operation, string detail, string time, string device, string store) =>
            _db.Ado.ExecuteCommandAsync(
                "INSERT INTO EmployeeLogs (Id, EmployeeId, EmployeeName, Operation, OperationDetail, OperationTime, DeviceCode, StoreCode, LastUploadTime) VALUES (@id, @eid, @en, @op, @od, @t, @d, @s, DATEADD(minute, 20, @t))",
                new { id, eid = employeeId, en = employeeName, op = operation, od = detail, t = DateTime.Parse(time), d = device, s = store });

        public async ValueTask DisposeAsync()
        {
            _db.Dispose();
            SqlConnection.ClearAllPools();
            await ExecuteAsync(_masterConnectionString, $"""
                IF DB_ID(N'{_databaseName}') IS NOT NULL
                BEGIN
                    ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                    DROP DATABASE [{_databaseName}];
                END;
                """);
        }

        // 与生产 POSM.dbo.EmployeeLogs 一致（2026-09-30 只读核验 sys.columns）。
        private const string SchemaSql = """
            CREATE TABLE dbo.EmployeeLogs (
                Id varchar(255) NOT NULL CONSTRAINT PK_EmployeeLogs_Id PRIMARY KEY CLUSTERED,
                EmployeeId varchar(50) NULL,
                EmployeeName varchar(50) NULL,
                Operation varchar(200) NULL,
                OperationDetail varchar(200) NULL,
                OperationTime datetime NOT NULL,
                DeviceCode varchar(200) NULL,
                StoreCode varchar(200) NULL,
                LastUploadTime datetime NOT NULL
            );
            """;

        private static async Task ExecuteAsync(string connectionString, string sql)
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new SqlCommand(sql, connection) { CommandTimeout = 60 };
            await command.ExecuteNonQueryAsync();
        }

        private static void EnsureLoopbackSqlServer(string connectionString)
        {
            var dataSource = new SqlConnectionStringBuilder(connectionString).DataSource.Trim();
            if (dataSource.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase)) dataSource = dataSource[4..];
            var host = dataSource.Split(',', 2, StringSplitOptions.TrimEntries)[0].Trim('[', ']');
            if (!host.Equals("localhost", StringComparison.OrdinalIgnoreCase) && !host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"{ConnectionEnvironmentVariable} 必须指向 localhost 或 127.0.0.1。");
        }
    }

    private static T CreateContext<T>(ISqlSugarClient db)
    {
        var context = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
        typeof(T).GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(context, db);
        return context;
    }
}
