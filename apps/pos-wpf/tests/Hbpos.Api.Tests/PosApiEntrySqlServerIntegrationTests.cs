using Hbpos.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hbpos.Api.Tests;

/// <summary>
/// 真实 SQL Server 上验证收银入口的原生 SQL：Store.PosApiEntryMode 的读取（缺列降级、脏值归一、停用 / 删除分店、缓存 TTL）、
/// 设备行 LastApiEntry / LastApiEntryAt 的定位写入，以及两个启动补列脚本的幂等。
/// </summary>
[Collection(DeviceActivationSqlServerCollection.Name)]
[Trait("Category", "SQL")]
public sealed class PosApiEntrySqlServerIntegrationTests(DeviceActivationSqlServerFixture fixture)
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 24, 1, 0, 0, TimeSpan.Zero);

    private const string DropEntryColumnsSql = """
        IF COL_LENGTH(N'dbo.Store', N'PosApiEntryMode') IS NOT NULL ALTER TABLE [dbo].[Store] DROP COLUMN [PosApiEntryMode];
        IF COL_LENGTH(N'dbo.Store', N'PosApiEntryUpdatedAt') IS NOT NULL ALTER TABLE [dbo].[Store] DROP COLUMN [PosApiEntryUpdatedAt];
        IF COL_LENGTH(N'dbo.Store', N'PosApiEntryUpdatedBy') IS NOT NULL ALTER TABLE [dbo].[Store] DROP COLUMN [PosApiEntryUpdatedBy];
        """;

    [DeviceActivationSqlServerFact]
    public async Task Reader_treats_a_missing_column_as_unset_and_never_throws()
    {
        await fixture.ResetAsync();
        await fixture.ExecuteMainAsync(DropEntryColumnsSql);
        var clock = new PosApiEntryTests.MutableTimeProvider(T0);
        var reader = new SqlSugarStoreApiEntryModeReader(
            fixture.CreateContext(), new StoreApiEntryModeCache(clock),
            NullLogger<SqlSugarStoreApiEntryModeReader>.Instance);

        // 部署顺序出错（列还没补）：按未设置处理，不能让三端轮询的同步接口 500。
        Assert.Null(await reader.GetModeAsync("S001", CancellationToken.None));
    }

    [DeviceActivationSqlServerFact]
    public async Task Reader_returns_the_whitelisted_mode_only_and_respects_active_and_deleted_flags()
    {
        await fixture.ResetAsync();
        await fixture.ExecuteMainAsync(DropEntryColumnsSql);
        await new SqlSugarStoreSchemaInitializer(new SqlSugarStoreSchemaSqlExecutor(fixture.CreateContext()))
            .InitializeAsync();

        var clock = new PosApiEntryTests.MutableTimeProvider(T0);
        var reader = new SqlSugarStoreApiEntryModeReader(
            fixture.CreateContext(), new StoreApiEntryModeCache(clock),
            NullLogger<SqlSugarStoreApiEntryModeReader>.Instance);

        // NULL → 未设置；白名单值 → 原样（大小写归一）；脏值 → 未设置。
        Assert.Null(await reader.GetModeAsync("S001", CancellationToken.None));

        await SetModeAsync("S001", "AWS");
        clock.Advance(StoreApiEntryModeCache.Ttl + TimeSpan.FromSeconds(1));
        Assert.Equal("aws", await reader.GetModeAsync("S001", CancellationToken.None));

        await SetModeAsync("S001", "tencent-dirty");
        clock.Advance(StoreApiEntryModeCache.Ttl + TimeSpan.FromSeconds(1));
        Assert.Null(await reader.GetModeAsync("S001", CancellationToken.None));

        // 停用 / 已删除的分店与门店资料接口同口径：不下发。
        await SetModeAsync("S002", "direct");
        Assert.Equal("direct", await reader.GetModeAsync("S002", CancellationToken.None));
        await fixture.ExecuteMainAsync("UPDATE [dbo].[Store] SET [IsActive] = 0 WHERE [StoreCode] = 'S002';");
        clock.Advance(StoreApiEntryModeCache.Ttl + TimeSpan.FromSeconds(1));
        Assert.Null(await reader.GetModeAsync("S002", CancellationToken.None));
        await fixture.ExecuteMainAsync("UPDATE [dbo].[Store] SET [IsActive] = 1, [IsDeleted] = 1 WHERE [StoreCode] = 'S002';");
        clock.Advance(StoreApiEntryModeCache.Ttl + TimeSpan.FromSeconds(1));
        Assert.Null(await reader.GetModeAsync("S002", CancellationToken.None));

        // 不存在的分店 / 空代码：未设置。
        Assert.Null(await reader.GetModeAsync("NOPE", CancellationToken.None));
        Assert.Null(await reader.GetModeAsync(" ", CancellationToken.None));
    }

    [DeviceActivationSqlServerFact]
    public async Task Reader_serves_from_cache_inside_the_ttl_and_rereads_after_it()
    {
        await fixture.ResetAsync();
        await fixture.ExecuteMainAsync(DropEntryColumnsSql);
        await new SqlSugarStoreSchemaInitializer(new SqlSugarStoreSchemaSqlExecutor(fixture.CreateContext()))
            .InitializeAsync();
        await SetModeAsync("S001", "aws");

        var clock = new PosApiEntryTests.MutableTimeProvider(T0);
        var cache = new StoreApiEntryModeCache(clock);
        var reader = new SqlSugarStoreApiEntryModeReader(
            fixture.CreateContext(), cache, NullLogger<SqlSugarStoreApiEntryModeReader>.Instance);

        Assert.Equal("aws", await reader.GetModeAsync("S001", CancellationToken.None));

        // TTL 内后台改了值：本进程仍返回缓存值（改设置后最长「轮询 60 秒 + 缓存 TTL」内生效）。
        await SetModeAsync("S001", "direct");
        clock.Advance(StoreApiEntryModeCache.Ttl - TimeSpan.FromSeconds(1));
        Assert.Equal("aws", await reader.GetModeAsync("S001", CancellationToken.None));

        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal("direct", await reader.GetModeAsync("S001", CancellationToken.None));
    }

    [DeviceActivationSqlServerFact]
    public async Task Writer_updates_only_the_matching_device_row_and_only_the_two_columns()
    {
        await fixture.ResetAsync();
        var context = fixture.CreateContext();
        await new SqlSugarDeviceRuntimeStatusSchemaInitializer(new SqlSugarDeviceRuntimeStatusSchemaSqlExecutor(context))
            .InitializeAsync();
        await fixture.ExecutePosmAsync(
            """
            INSERT [dbo].[POSM_设备注册信息表]
                ([设备硬件识别码], [系统设备编号], [分店代码], [设备类型], [设备系统], [设备状态], [设备授权码], [创建时间], [应用版本])
            VALUES
                ('HW-1', 'POS-1', 'S001', N'POS', 'Windows', 1, 'a', SYSUTCDATETIME(), '1.0.89'),
                ('HW-2', 'POS-2', 'S001', N'POS', 'Windows', 1, 'b', SYSUTCDATETIME(), '1.0.89'),
                ('HW-3', 'POS-1', 'S002', N'POS', 'Windows', 1, 'c', SYSUTCDATETIME(), '1.0.89');
            """);
        var writer = new SqlSugarDeviceApiEntryWriter(context);
        var now = new DateTime(2026, 10, 24, 1, 2, 3, DateTimeKind.Utc);

        Assert.Equal(1, await writer.WriteAsync("POS-1", "S001", "HW-1", "aws", now, CancellationToken.None));
        // 硬件码不匹配 / 门店不匹配：不写。
        Assert.Equal(0, await writer.WriteAsync("POS-1", "S001", "HW-OTHER", "aws", now, CancellationToken.None));
        Assert.Equal(0, await writer.WriteAsync("POS-9", "S001", "HW-1", "aws", now, CancellationToken.None));
        // 不带硬件码时按 (设备编号, 分店) 定位，同编号不同分店的行不受影响。
        Assert.Equal(1, await writer.WriteAsync("POS-2", "S001", null, "direct", now.AddMinutes(1), CancellationToken.None));

        var rows = await context.PosmDb.Ado.SqlQueryAsync<DeviceRow>(
            """
            SELECT [系统设备编号] AS DeviceCode, [分店代码] AS StoreCode, [LastApiEntry] AS Entry,
                   [LastApiEntryAt] AS EntryAt, [应用版本] AS AppVersion
            FROM [dbo].[POSM_设备注册信息表] ORDER BY [ID]
            """);
        Assert.Equal(3, rows.Count);
        Assert.Equal(("POS-1", "S001", "aws", now, "1.0.89"), (rows[0].DeviceCode, rows[0].StoreCode, rows[0].Entry, rows[0].EntryAt, rows[0].AppVersion));
        Assert.Equal(("direct", now.AddMinutes(1)), (rows[1].Entry, rows[1].EntryAt));
        Assert.Null(rows[2].Entry);
        Assert.Null(rows[2].EntryAt);
    }

    [DeviceActivationSqlServerFact]
    public async Task Recorder_survives_a_missing_column_on_real_sql_without_throwing()
    {
        await fixture.ResetAsync();
        var context = fixture.CreateContext();
        // 先确保设备表缺这两列。
        await fixture.ExecutePosmAsync(
            """
            IF COL_LENGTH(N'dbo.POSM_设备注册信息表', N'LastApiEntry') IS NOT NULL ALTER TABLE [dbo].[POSM_设备注册信息表] DROP COLUMN [LastApiEntry];
            IF COL_LENGTH(N'dbo.POSM_设备注册信息表', N'LastApiEntryAt') IS NOT NULL ALTER TABLE [dbo].[POSM_设备注册信息表] DROP COLUMN [LastApiEntryAt];
            """);
        var clock = new PosApiEntryTests.MutableTimeProvider(T0);
        var logger = new PosApiEntryTests.CapturingLogger<DeviceApiEntryRecorder>();
        var recorder = new DeviceApiEntryRecorder(
            new SqlSugarDeviceApiEntryWriter(context), new DeviceApiEntryThrottle(clock), clock, logger);

        await recorder.RecordAsync("POS-1", "S001", "HW-1", "edge", CancellationToken.None);

        // 写库失败只记日志，不抛出（sync / runtime-status 的响应不受影响）。
        Assert.Single(logger.Warnings);
    }

    [DeviceActivationSqlServerFact]
    public async Task Both_startup_column_scripts_are_idempotent()
    {
        await fixture.ResetAsync();
        var context = fixture.CreateContext();
        var storeInitializer = new SqlSugarStoreSchemaInitializer(new SqlSugarStoreSchemaSqlExecutor(context));
        var deviceInitializer = new SqlSugarDeviceRuntimeStatusSchemaInitializer(
            new SqlSugarDeviceRuntimeStatusSchemaSqlExecutor(context));

        await storeInitializer.InitializeAsync();
        await storeInitializer.InitializeAsync();
        await deviceInitializer.InitializeAsync();
        await deviceInitializer.InitializeAsync();

        var storeColumns = await context.MainDb.Ado.SqlQuerySingleAsync<int>(
            "SELECT COUNT(*) FROM sys.columns WHERE [object_id] = OBJECT_ID(N'dbo.Store') AND [name] IN (N'PosApiEntryMode', N'PosApiEntryUpdatedAt', N'PosApiEntryUpdatedBy')");
        var deviceColumns = await context.PosmDb.Ado.SqlQuerySingleAsync<int>(
            "SELECT COUNT(*) FROM sys.columns WHERE [object_id] = OBJECT_ID(N'dbo.POSM_设备注册信息表') AND [name] IN (N'LastApiEntry', N'LastApiEntryAt')");
        Assert.Equal(3, storeColumns);
        Assert.Equal(2, deviceColumns);
    }

    private Task SetModeAsync(string storeCode, string? mode) =>
        fixture.ExecuteMainAsync(
            mode is null
                ? $"UPDATE [dbo].[Store] SET [PosApiEntryMode] = NULL WHERE [StoreCode] = '{storeCode}';"
                : $"UPDATE [dbo].[Store] SET [PosApiEntryMode] = N'{mode}' WHERE [StoreCode] = '{storeCode}';");

    private sealed class DeviceRow
    {
        public string DeviceCode { get; set; } = string.Empty;
        public string StoreCode { get; set; } = string.Empty;
        public string? Entry { get; set; }
        public DateTime? EntryAt { get; set; }
        public string? AppVersion { get; set; }
    }
}
