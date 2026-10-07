using System.Data;
using Hbpos.Api.Data;
using Hbpos.Api.Services;
using Hbpos.Contracts.Linkly;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hbpos.Api.Tests;

public sealed class LinklySettlementRepositoryParameterTests
{
    [Fact]
    public void ToParameters_sends_every_timestamp_as_datetime2_including_nulls()
    {
        var requestedAt = new DateTimeOffset(2026, 10, 2, 6, 48, 52, TimeSpan.Zero).AddTicks(9574255);
        var record = new PosmLinklySettlementRecord
        {
            SettlementGuid = Guid.NewGuid(),
            StoreCode = "1042",
            DeviceCode = "POS_1042_0200",
            BusinessDate = new DateTime(2026, 10, 2),
            ConnectionMode = "LocalIp",
            Environment = "Production",
            Status = "Pending",
            RequestedAtUtc = requestedAt,
            ReceivedAtUtc = requestedAt,
            UpdatedAtUtc = requestedAt,
            ClientRevision = 1
        };

        var parameters = SqlSugarLinklySettlementRepository.ToParameters(record);

        foreach (var name in new[]
                 {
                     "@RequestedAtUtc", "@CompletedAtUtc", "@FirstPrintedAtUtc",
                     "@LastPrintedAtUtc", "@ReceivedAtUtc", "@UpdatedAtUtc"
                 })
        {
            Assert.Equal(DbType.DateTime2, Assert.Single(parameters, parameter => parameter.ParameterName == name).DbType);
        }

        Assert.Equal(
            requestedAt.UtcDateTime,
            Assert.Single(parameters, parameter => parameter.ParameterName == "@RequestedAtUtc").Value);
    }
}

public sealed class LinklySettlementSqlServerFactAttribute : FactAttribute
{
    public LinklySettlementSqlServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(DailyCloseSqlServerFactAttribute.ConnectionVariable)))
        {
            Skip = "未配置隔离 SQL Server，跳过 Linkly 结算真实 SQL 测试。";
        }
    }
}

/// <summary>
/// 真实 SQL Server 上验证结算时间戳按 DATETIME2(7) 原样落库，以及「修复前按 datetime 舍入落库的旧行」也能继续接收更高修订号。
/// 旧行由 SQL Server 自己的 CAST(... AS datetime) 制造，而不是用测试里的舍入函数，确保容差判断与引擎行为一致。
/// 每个用例自建独立数据库并在结束时删除，库名只来自本用例生成的 GUID。
/// </summary>
[Trait("Category", "SQL")]
public sealed class LinklySettlementSqlServerIntegrationTests : IAsyncLifetime
{
    private static readonly DateTimeOffset RequestedAt =
        new DateTimeOffset(2026, 10, 2, 6, 48, 52, TimeSpan.Zero).AddTicks(9574255);

    private static readonly DateTimeOffset CompletedAt =
        new DateTimeOffset(2026, 10, 2, 6, 49, 3, TimeSpan.Zero).AddTicks(375697);

    private string? masterConnection;
    private string connection = string.Empty;
    private string database = string.Empty;

    public async Task InitializeAsync()
    {
        var configured = Environment.GetEnvironmentVariable(DailyCloseSqlServerFactAttribute.ConnectionVariable);
        if (string.IsNullOrWhiteSpace(configured))
        {
            return;
        }

        database = $"HbLinklySettlement_{Guid.NewGuid():N}";
        var builder = new SqlConnectionStringBuilder(configured) { InitialCatalog = "master" };
        masterConnection = builder.ConnectionString;
        await ExecuteAtAsync(masterConnection, $"CREATE DATABASE [{database}]");
        builder.InitialCatalog = database;
        connection = builder.ConnectionString;
        await new SqlSugarLinklySettlementSchemaInitializer(
            new SqlSugarLinklySettlementSchemaSqlExecutor(CreateContext())).InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        if (masterConnection is null)
        {
            return;
        }

        await ExecuteAtAsync(
            masterConnection,
            $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}];");
    }

    [LinklySettlementSqlServerFact]
    public async Task Timestamps_round_trip_at_full_datetime2_precision()
    {
        var service = CreateService();
        var request = CreateRequest(revision: 4);

        await service.SyncAsync(request, "1042", "POS_1042_0200", CancellationToken.None);

        var stored = await CreateRepository().GetAsync("1042", "POS_1042_0200", request.SettlementGuid, CancellationToken.None);
        Assert.NotNull(stored);
        Assert.Equal(RequestedAt.UtcDateTime.Ticks, stored.RequestedAtUtc.UtcDateTime.Ticks);
        Assert.Equal(CompletedAt.UtcDateTime.Ticks, stored.CompletedAtUtc!.Value.UtcDateTime.Ticks);
    }

    [LinklySettlementSqlServerFact]
    public async Task Higher_revision_and_same_revision_retry_succeed_for_full_precision_rows()
    {
        var service = CreateService();
        var request = CreateRequest(revision: 4);
        await service.SyncAsync(request, "1042", "POS_1042_0200", CancellationToken.None);

        var retry = await service.SyncAsync(request, "1042", "POS_1042_0200", CancellationToken.None);
        var next = await service.SyncAsync(
            request with { ClientRevision = 5, LastPrintError = "Printer port could not be opened." },
            "1042",
            "POS_1042_0200",
            CancellationToken.None);

        Assert.True(retry.AlreadySynced);
        Assert.Equal(5, next.AcceptedRevision);
        Assert.False(next.AlreadySynced);
    }

    [LinklySettlementSqlServerFact]
    public async Task Higher_revision_is_accepted_for_rows_rounded_by_the_legacy_datetime_writes()
    {
        var service = CreateService();
        var request = CreateRequest(revision: 4);
        await service.SyncAsync(request, "1042", "POS_1042_0200", CancellationToken.None);
        // 用 SQL Server 自己的 datetime 换算制造修复前的旧行（线上 BD0F3F55 就是这样存的）。
        await ExecuteAtAsync(
            connection,
            """
            UPDATE [dbo].[POSM_LinklySettlement]
            SET [RequestedAtUtc] = CAST(CAST([RequestedAtUtc] AS datetime) AS datetime2(7)),
                [CompletedAtUtc] = CAST(CAST([CompletedAtUtc] AS datetime) AS datetime2(7));
            """);
        var legacy = await CreateRepository().GetAsync("1042", "POS_1042_0200", request.SettlementGuid, CancellationToken.None);
        Assert.NotEqual(RequestedAt.UtcDateTime, legacy!.RequestedAtUtc.UtcDateTime);
        Assert.True(LinklySettlementSyncService.IsOnLegacySqlDateTimeGrid(legacy.RequestedAtUtc.UtcDateTime));

        var retry = await service.SyncAsync(request, "1042", "POS_1042_0200", CancellationToken.None);
        var next = await service.SyncAsync(
            request with { ClientRevision = 5, LastPrintError = "Printer port could not be opened." },
            "1042",
            "POS_1042_0200",
            CancellationToken.None);

        Assert.True(retry.AlreadySynced);
        Assert.Equal(5, next.AcceptedRevision);
        var stored = await CreateRepository().GetAsync("1042", "POS_1042_0200", request.SettlementGuid, CancellationToken.None);
        Assert.Equal(5, stored!.ClientRevision);
        Assert.Equal("Printer port could not be opened.", stored.LastPrintError);
        // 更高修订号会用 DATETIME2 重写完成时间；请求时间是不可变字段，保留旧行的舍入值。
        Assert.Equal(CompletedAt.UtcDateTime.Ticks, stored.CompletedAtUtc!.Value.UtcDateTime.Ticks);
    }

    private LinklySettlementSyncService CreateService()
    {
        return new LinklySettlementSyncService(CreateRepository());
    }

    private SqlSugarLinklySettlementRepository CreateRepository()
    {
        return new SqlSugarLinklySettlementRepository(CreateContext());
    }

    private static LinklySettlementSyncRequest CreateRequest(long revision)
    {
        return new LinklySettlementSyncRequest(
            1,
            Guid.NewGuid(),
            "1042",
            "POS_1042_0200",
            new DateOnly(2026, 10, 2),
            "LocalIp",
            "Production",
            "POS10420200261002064852969",
            "Succeeded",
            "00",
            "APPROVED",
            "TOTAL=10.00",
            ["SETTLEMENT RECEIPT"],
            RequestedAt,
            CompletedAt,
            null,
            null,
            0,
            null,
            revision);
    }

    private HbposSqlSugarContext CreateContext()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:MainConnection"] = connection,
            ["ConnectionStrings:PosmConnection"] = connection,
            ["Database:CommandTimeoutSeconds"] = "30",
        }).Build();
        return new HbposSqlSugarContext(config, NullLogger<HbposSqlSugarContext>.Instance);
    }

    private static async Task ExecuteAtAsync(string connectionString, string sql)
    {
        await using var sqlConnection = new SqlConnection(connectionString);
        await sqlConnection.OpenAsync();
        await using var command = new SqlCommand(sql, sqlConnection) { CommandTimeout = 60 };
        await command.ExecuteNonQueryAsync();
    }
}
