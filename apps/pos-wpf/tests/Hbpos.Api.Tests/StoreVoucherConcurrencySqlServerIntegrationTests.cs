using BlazorApp.Shared.Models.POSM;
using Hbpos.Api.Data;
using Hbpos.Api.Services;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hbpos.Api.Tests;

public sealed class StoreVoucherSqlServerFactAttribute : FactAttribute
{
    public StoreVoucherSqlServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(DailyCloseSqlServerFactAttribute.ConnectionVariable)))
        {
            Skip = "未配置隔离 SQL Server，跳过代金券并发真实 SQL 测试。";
        }
    }
}

/// <summary>
/// 真实 SQL Server 上验证代金券预占与开券幂等在并发下只有一路生效。
/// 走生产同款 HbposSqlSugarContext（全局 IsWithNoLockQuery=true），每路请求独立上下文，模拟多台 POS。
/// 每个用例自建独立数据库并在结束时删除，库名只来自本用例生成的 GUID。
/// </summary>
[Trait("Category", "SQL")]
public sealed class StoreVoucherConcurrencySqlServerIntegrationTests : IAsyncLifetime
{
    private const int ConcurrentRequests = 8;
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

        database = $"HbStoreVoucher_{Guid.NewGuid():N}";
        var builder = new SqlConnectionStringBuilder(configured) { InitialCatalog = "master" };
        masterConnection = builder.ConnectionString;
        await ExecuteAtAsync(masterConnection, $"CREATE DATABASE [{database}]");
        builder.InitialCatalog = database;
        connection = builder.ConnectionString;
        CreateContext().PosmDb.CodeFirst.InitTables<StoreVoucher, StoreVoucherReservationEntity>();
    }

    public async Task DisposeAsync()
    {
        if (masterConnection is null)
        {
            return;
        }

        SqlConnection.ClearAllPools();
        await ExecuteAtAsync(
            masterConnection,
            $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}];");
    }

    [StoreVoucherSqlServerFact]
    public async Task Concurrent_reservations_of_the_same_voucher_never_over_lock_its_balance()
    {
        // 多轮放大竞态窗口：每轮一张 $50 券，8 路同时各要 $50，只能有一路拿到 token。
        for (var round = 0; round < 5; round++)
        {
            var voucherCode = $"V{round:D3}";
            await SeedVoucherAsync(voucherCode, 50m);

            var outcomes = await RunConcurrentlyAsync(() =>
                new SqlSugarStoreVoucherReservationService(CreateContext())
                    .ReserveAsync("S01", voucherCode, 50m, 50m, CancellationToken.None));

            var winner = Assert.Single(outcomes, outcome => outcome.Result is not null);
            Assert.Equal(50m, winner.Result!.LockedAmount);
            Assert.All(
                outcomes.Where(outcome => outcome.Error is not null),
                outcome => Assert.Contains("no remaining amount", outcome.Error!.Message, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(
                50m,
                await ScalarDecimalAsync(
                    $"SELECT COALESCE(SUM([LockedAmount]), 0) FROM [dbo].[StoreVoucherReservation] WHERE [VoucherCode] = N'{voucherCode}' AND [Status] = N'pending';"));
        }
    }

    [StoreVoucherSqlServerFact]
    public async Task Sequential_retry_with_the_same_issue_key_returns_the_existing_voucher()
    {
        var repository = new SqlSugarStoreVoucherRepository(CreateContext());

        var first = await repository.CreateIssuedVoucherAsync(CreateIssueModel("issue-key-1"), CancellationToken.None);
        var retry = await new SqlSugarStoreVoucherRepository(CreateContext())
            .CreateIssuedVoucherAsync(CreateIssueModel("issue-key-1"), CancellationToken.None);

        Assert.Equal(first.VoucherCode, retry.VoucherCode);
        Assert.Equal(1L, await ScalarAsync("SELECT COUNT_BIG(*) FROM [dbo].[StoreVoucher];"));
    }

    [StoreVoucherSqlServerFact]
    public async Task Concurrent_issue_retries_with_the_same_key_create_exactly_one_voucher()
    {
        var outcomes = await RunConcurrentlyAsync(() =>
            new SqlSugarStoreVoucherRepository(CreateContext())
                .CreateIssuedVoucherAsync(CreateIssueModel("issue-key-concurrent"), CancellationToken.None));

        Assert.All(outcomes, outcome => Assert.Null(outcome.Error));
        Assert.Single(outcomes.Select(outcome => outcome.Result!.VoucherCode).Distinct(StringComparer.Ordinal));
        Assert.Equal(1L, await ScalarAsync("SELECT COUNT_BIG(*) FROM [dbo].[StoreVoucher];"));
    }

    [StoreVoucherSqlServerFact]
    public async Task Concurrent_refund_retries_with_the_same_key_create_exactly_one_voucher()
    {
        var outcomes = await RunConcurrentlyAsync(() =>
            new SqlSugarStoreVoucherRepository(CreateContext())
                .CreateRefundVoucherAsync(
                    new RefundVoucherCreateModel(
                        "S01",
                        12.5m,
                        "C01",
                        DateTimeOffset.UtcNow,
                        DateTimeOffset.UtcNow.AddDays(90),
                        "refund-key-concurrent",
                        "ORDER-1",
                        null),
                    CancellationToken.None));

        Assert.All(outcomes, outcome => Assert.Null(outcome.Error));
        Assert.Single(outcomes.Select(outcome => outcome.Result!.VoucherCode).Distinct(StringComparer.Ordinal));
        Assert.Equal(1L, await ScalarAsync("SELECT COUNT_BIG(*) FROM [dbo].[StoreVoucher];"));
    }

    [StoreVoucherSqlServerFact]
    public async Task Different_issue_keys_still_create_separate_vouchers_concurrently()
    {
        var keyIndex = 0;
        var outcomes = await RunConcurrentlyAsync(() =>
        {
            var key = $"issue-key-{Interlocked.Increment(ref keyIndex)}";
            return new SqlSugarStoreVoucherRepository(CreateContext())
                .CreateIssuedVoucherAsync(CreateIssueModel(key), CancellationToken.None);
        });

        Assert.All(outcomes, outcome => Assert.Null(outcome.Error));
        Assert.Equal(
            ConcurrentRequests,
            outcomes.Select(outcome => outcome.Result!.VoucherCode).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal((long)ConcurrentRequests, await ScalarAsync("SELECT COUNT_BIG(*) FROM [dbo].[StoreVoucher];"));
    }

    private static IssuedVoucherCreateModel CreateIssueModel(string idempotencyKey) =>
        new(
            "S01",
            20m,
            "C01",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddDays(90),
            idempotencyKey,
            null,
            "promo");

    private static async Task<IReadOnlyList<Outcome<T>>> RunConcurrentlyAsync<T>(Func<Task<T>> operation)
    {
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, ConcurrentRequests)
            .Select(_ => Task.Run(async () =>
            {
                await startGate.Task;
                try
                {
                    return new Outcome<T>(await operation(), null);
                }
                catch (Exception ex)
                {
                    return new Outcome<T>(default, ex);
                }
            }))
            .ToArray();
        startGate.SetResult();
        return await Task.WhenAll(tasks);
    }

    private Task SeedVoucherAsync(string voucherCode, decimal amount) =>
        CreateContext().PosmDb.Insertable(new StoreVoucher
        {
            StoreCode = "S01",
            VoucherCode = voucherCode,
            VoucherType = 3,
            Amount = amount,
            RemainingAmount = amount,
            Status = "1",
            IsDelete = false,
            DiscountRate = 0m,
            ExpiredDate = DateTime.UtcNow.AddDays(30)
        }).ExecuteCommandAsync();

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

    private async Task<long> ScalarAsync(string sql)
    {
        await using var sqlConnection = new SqlConnection(connection);
        await sqlConnection.OpenAsync();
        await using var command = new SqlCommand(sql, sqlConnection);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private async Task<decimal> ScalarDecimalAsync(string sql)
    {
        await using var sqlConnection = new SqlConnection(connection);
        await sqlConnection.OpenAsync();
        await using var command = new SqlCommand(sql, sqlConnection);
        return Convert.ToDecimal(await command.ExecuteScalarAsync());
    }

    private static async Task ExecuteAtAsync(string connectionString, string sql)
    {
        await using var sqlConnection = new SqlConnection(connectionString);
        await sqlConnection.OpenAsync();
        await using var command = new SqlCommand(sql, sqlConnection) { CommandTimeout = 60 };
        await command.ExecuteNonQueryAsync();
    }

    private sealed record Outcome<T>(T? Result, Exception? Error);
}
