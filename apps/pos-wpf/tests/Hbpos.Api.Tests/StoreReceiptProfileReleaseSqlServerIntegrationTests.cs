using Hbpos.Api.Services;
using Hbpos.Contracts.Devices;

namespace Hbpos.Api.Tests;

/// <summary>
/// 真实 SQL Server 上验证下发快照的原生 SQL：两张表由 HBweb 迁移创建，这里按契约的表结构建表。
/// 重点是业务规则里依赖数据库行为的部分：表不存在时降级、MERGE 的单调不降与换店覆盖、
/// 并发首次回执不撞主键、门店资料接口叠加最新快照。
/// </summary>
[Collection(DeviceActivationSqlServerCollection.Name)]
[Trait("Category", "SQL")]
public sealed class StoreReceiptProfileReleaseSqlServerIntegrationTests(
    DeviceActivationSqlServerFixture fixture)
{
    // 与 HBweb 迁移（StoreReceiptProfileRelease / PosReceiptProfileAck）的契约结构一致。
    private const string CreateTablesSql = """
        CREATE TABLE [dbo].[StoreReceiptProfileRelease]
        (
            [StoreCode] nvarchar(50) NOT NULL,
            [Version] int NOT NULL,
            [StoreName] nvarchar(100) NOT NULL,
            [BrandName] nvarchar(100) NULL,
            [Address] nvarchar(500) NULL,
            [Phone] nvarchar(200) NULL,
            [ABN] nvarchar(20) NULL,
            [ReturnPolicy] nvarchar(500) NULL,
            [PublishedAtUtc] datetime2 NOT NULL,
            [PublishedBy] nvarchar(100) NULL,
            CONSTRAINT [PK_StoreReceiptProfileRelease] PRIMARY KEY CLUSTERED ([StoreCode], [Version])
        );
        CREATE TABLE [dbo].[PosReceiptProfileAck]
        (
            [DeviceCode] nvarchar(100) NOT NULL,
            [StoreCode] nvarchar(50) NOT NULL,
            [AppliedVersion] int NOT NULL,
            [AppliedAtUtc] datetime2 NOT NULL,
            [ClientKind] nvarchar(16) NOT NULL,
            CONSTRAINT [PK_PosReceiptProfileAck] PRIMARY KEY CLUSTERED ([DeviceCode])
        );
        """;

    private const string DropTablesSql = """
        IF OBJECT_ID(N'[dbo].[PosReceiptProfileAck]', N'U') IS NOT NULL DROP TABLE [dbo].[PosReceiptProfileAck];
        IF OBJECT_ID(N'[dbo].[StoreReceiptProfileRelease]', N'U') IS NOT NULL DROP TABLE [dbo].[StoreReceiptProfileRelease];
        """;

    private static readonly DateTime FixedPublishedAtUtc = new(2026, 10, 7, 1, 2, 3, DateTimeKind.Unspecified);

    [DeviceActivationSqlServerFact]
    public async Task Tables_missing_everything_degrades_to_never_published()
    {
        await fixture.ResetAsync();
        await fixture.ExecuteMainAsync(DropTablesSql);
        var context = fixture.CreateContext();
        var repository = new SqlSugarStoreReceiptProfileReleaseRepository(context);
        var release = new StoreReceiptProfileReleaseService(repository);

        Assert.Equal(0, await repository.GetLatestVersionAsync("S001", CancellationToken.None));
        Assert.Null(await repository.GetLatestAsync("S001", CancellationToken.None));

        var sync = await release.GetSyncAsync("S001", 0, CancellationToken.None);
        var syncDto = Assert.IsType<Hbpos.Contracts.Stores.StoreReceiptProfileSyncDto>(sync.Sync);
        Assert.False(syncDto.Changed);
        Assert.Equal(0, syncDto.Version);

        // 回执：表不存在时任何版本都无效，是 400 业务错误而不是 SQL 异常。
        var ack = await release.AckAsync("S001", "POS-01", "Windows", 1, CancellationToken.None);
        Assert.Equal(StoreReceiptProfileReleaseService.VersionInvalidCode, ack.ErrorCode);

        // 门店资料接口：沿用门店当前值，Version=0。
        var profile = await new StoreReceiptProfileService(context).GetCurrentAsync("S001", CancellationToken.None);
        Assert.NotNull(profile.Profile);
        Assert.Equal("Source store", profile.Profile.StoreName);
        Assert.Equal(0, profile.Profile.Version);
        Assert.Null(profile.Profile.PublishedAt);
    }

    [DeviceActivationSqlServerFact]
    public async Task Tables_present_but_store_never_published_is_also_never_published()
    {
        await WithTablesAsync(async () =>
        {
            var context = fixture.CreateContext();
            var repository = new SqlSugarStoreReceiptProfileReleaseRepository(context);

            Assert.Equal(0, await repository.GetLatestVersionAsync("S001", CancellationToken.None));
            Assert.Null(await repository.GetLatestAsync("S001", CancellationToken.None));
            var profile = await new StoreReceiptProfileService(context).GetCurrentAsync("S001", CancellationToken.None);
            Assert.Equal(0, profile.Profile!.Version);
        });
    }

    [DeviceActivationSqlServerFact]
    public async Task Latest_snapshot_wins_over_current_store_values()
    {
        await WithTablesAsync(async () =>
        {
            await InsertReleaseAsync("S001", 1, "旧抬头", "1 Old St");
            await InsertReleaseAsync("S001", 2, "新抬头", "2 New St\r\nBrisbane");
            // 总部之后又改了门店当前值，但还没下发：收银端必须仍读到 v2 快照。
            await fixture.ExecuteMainAsync(
                "UPDATE [dbo].[Store] SET [StoreName] = N'未下发的新店名', [Address] = N'3 Unpublished St' WHERE [StoreCode] = 'S001';");
            var context = fixture.CreateContext();
            var repository = new SqlSugarStoreReceiptProfileReleaseRepository(context);

            Assert.Equal(2, await repository.GetLatestVersionAsync("S001", CancellationToken.None));
            var latest = await repository.GetLatestAsync("S001", CancellationToken.None);
            Assert.NotNull(latest);
            Assert.Equal(2, latest.Version);
            Assert.Equal("新抬头", latest.StoreName);
            Assert.Equal("2 New St\r\nBrisbane", latest.Address);
            Assert.Equal("12 345 678 901", latest.Abn);
            // 库里存的是 UTC 墙钟时间，对外必须带 UTC 偏移。
            Assert.Equal(TimeSpan.Zero, latest.PublishedAt!.Value.Offset);
            Assert.Equal(FixedPublishedAtUtc, latest.PublishedAt.Value.UtcDateTime);

            var profile = await new StoreReceiptProfileService(context).GetCurrentAsync("S001", CancellationToken.None);
            Assert.Equal("新抬头", profile.Profile!.StoreName);
            Assert.Equal(2, profile.Profile.Version);
            Assert.Equal("S001", profile.Profile.StoreCode);

            // 同步接口：旧版本→拿到快照；同版本→不变。
            var release = new StoreReceiptProfileReleaseService(repository);
            var changed = await release.GetSyncAsync("S001", 1, CancellationToken.None);
            Assert.True(changed.Sync!.Changed);
            Assert.Equal("新抬头", changed.Sync.Profile!.StoreName);
            var same = await release.GetSyncAsync("S001", 2, CancellationToken.None);
            Assert.False(same.Sync!.Changed);
            Assert.Null(same.Sync.Profile);
        });
    }

    [DeviceActivationSqlServerFact]
    public async Task Release_does_not_resurrect_a_disabled_store()
    {
        await WithTablesAsync(async () =>
        {
            await InsertReleaseAsync("S001", 1, "抬头", "1 Queen St");
            await fixture.ExecuteMainAsync("UPDATE [dbo].[Store] SET [IsActive] = 0 WHERE [StoreCode] = 'S001';");

            var profile = await new StoreReceiptProfileService(fixture.CreateContext())
                .GetCurrentAsync("S001", CancellationToken.None);

            // 门店停用后接口仍按「不存在或已停用」处理，快照不能绕过这道检查。
            Assert.Null(profile.Profile);
            Assert.Equal(StoreReceiptProfileService.StoreNotFoundCode, profile.ErrorCode);
        });
    }

    [DeviceActivationSqlServerFact]
    public async Task Ack_is_monotonic_and_only_bumps_time_when_version_rises()
    {
        await WithTablesAsync(async () =>
        {
            await InsertReleaseAsync("S001", 1, "抬头 1", "1 Queen St");
            await InsertReleaseAsync("S001", 2, "抬头 2", "2 Queen St");
            var times = new MutableTimeProvider(new DateTimeOffset(2026, 10, 7, 2, 0, 0, TimeSpan.Zero));
            var release = new StoreReceiptProfileReleaseService(
                new SqlSugarStoreReceiptProfileReleaseRepository(fixture.CreateContext()),
                times);

            Assert.Equal(1, (await release.AckAsync("S001", "POS-01", "Windows", 1, CancellationToken.None)).Result!.AppliedVersion);
            times.Now = times.Now.AddMinutes(5);
            Assert.Equal(2, (await release.AckAsync("S001", "POS-01", "Windows", 2, CancellationToken.None)).Result!.AppliedVersion);
            var raisedAt = await AckAppliedAtAsync("POS-01");
            times.Now = times.Now.AddMinutes(5);
            // 晚到/重复的低版本回执：记录保持 2，时间也不动。
            Assert.Equal(2, (await release.AckAsync("S001", "POS-01", "Windows", 1, CancellationToken.None)).Result!.AppliedVersion);
            Assert.Equal(2, (await release.AckAsync("S001", "POS-01", "Windows", 2, CancellationToken.None)).Result!.AppliedVersion);

            Assert.Equal(raisedAt, await AckAppliedAtAsync("POS-01"));
            Assert.Equal(new DateTime(2026, 10, 7, 2, 5, 0), raisedAt);
            Assert.Equal(1, await CountAckRowsAsync());
        });
    }

    [DeviceActivationSqlServerFact]
    public async Task Ack_after_store_change_overwrites_with_the_new_store_version()
    {
        await WithTablesAsync(async () =>
        {
            // S001 已发到 v3，S002 只发到 v1：版本号是各店各自递增的，换店后不能沿用旧店的高版本。
            await InsertReleaseAsync("S001", 1, "抬头", "1 Queen St");
            await InsertReleaseAsync("S001", 2, "抬头", "1 Queen St");
            await InsertReleaseAsync("S001", 3, "抬头", "1 Queen St");
            await InsertReleaseAsync("S002", 1, "二店抬头", "9 Ann St");
            var release = new StoreReceiptProfileReleaseService(
                new SqlSugarStoreReceiptProfileReleaseRepository(fixture.CreateContext()));

            Assert.Equal(3, (await release.AckAsync("S001", "POS-01", "Windows", 3, CancellationToken.None)).Result!.AppliedVersion);
            var moved = await release.AckAsync("S002", "POS-01", "iPadOS", 1, CancellationToken.None);

            Assert.Equal(1, moved.Result!.AppliedVersion);
            Assert.Equal("S002", await AckStringAsync("POS-01", "StoreCode"));
            Assert.Equal("ipad", await AckStringAsync("POS-01", "ClientKind"));
            Assert.Equal(1, await CountAckRowsAsync());
        });
    }

    [DeviceActivationSqlServerFact]
    public async Task Concurrent_first_acks_from_one_device_do_not_collide_on_the_primary_key()
    {
        await WithTablesAsync(async () =>
        {
            for (var version = 1; version <= 6; version++)
            {
                await InsertReleaseAsync("S001", version, $"抬头 {version}", "1 Queen St");
            }

            // 每个任务一个独立连接的上下文，模拟同一台设备并发打进来的多个请求。
            var results = await Task.WhenAll(Enumerable.Range(1, 6).Select(version => Task.Run(async () =>
            {
                var release = new StoreReceiptProfileReleaseService(
                    new SqlSugarStoreReceiptProfileReleaseRepository(fixture.CreateContext()));
                var result = await release.AckAsync("S001", "POS-RACE", "Android", version, CancellationToken.None);
                return result.Result!.AppliedVersion;
            })));

            Assert.All(results, applied => Assert.InRange(applied, 1, 6));
            Assert.Equal(1, await CountAckRowsAsync());
            // 无论到达顺序如何，最终记录的必然是最高版本（单调不降）。
            Assert.Equal("6", await AckStringAsync("POS-RACE", "AppliedVersion"));
            Assert.Equal("handheld", await AckStringAsync("POS-RACE", "ClientKind"));
        });
    }

    // ---------------------------------------------------------------- 辅助

    private async Task WithTablesAsync(Func<Task> test)
    {
        await fixture.ResetAsync();
        await fixture.ExecuteMainAsync(DropTablesSql);
        await fixture.ExecuteMainAsync(CreateTablesSql);
        // 契约字段齐全的门店，便于断言 ABN 等字段随快照带出。
        await fixture.ExecuteMainAsync(
            """
            UPDATE [dbo].[Store]
            SET [Address] = N'1 Queen St', [Phone] = N'07 3000 0000', [ABN] = N'12 345 678 901',
                [BrandName] = N'Hot Bargain', [ReturnPolicy] = N'30 天无理由退换'
            WHERE [StoreCode] IN ('S001', 'S002');
            """);
        try
        {
            await test();
        }
        finally
        {
            await fixture.ExecuteMainAsync(DropTablesSql);
        }
    }

    private Task InsertReleaseAsync(string storeCode, int version, string storeName, string address) =>
        fixture.ExecuteMainAsync(
            """
            INSERT INTO [dbo].[StoreReceiptProfileRelease]
                ([StoreCode], [Version], [StoreName], [BrandName], [Address], [Phone], [ABN], [ReturnPolicy], [PublishedAtUtc], [PublishedBy])
            VALUES
                (@StoreCode, @Version, @StoreName, N'Hot Bargain', @Address, N'07 3000 0000', N'12 345 678 901', N'30 天无理由退换', @PublishedAtUtc, N'tester');
            """,
            new Microsoft.Data.SqlClient.SqlParameter("@StoreCode", storeCode),
            new Microsoft.Data.SqlClient.SqlParameter("@Version", version),
            new Microsoft.Data.SqlClient.SqlParameter("@StoreName", storeName),
            new Microsoft.Data.SqlClient.SqlParameter("@Address", address),
            new Microsoft.Data.SqlClient.SqlParameter("@PublishedAtUtc", FixedPublishedAtUtc));

    private async Task<int> CountAckRowsAsync() =>
        await fixture.CreateContext().MainDb.Ado.SqlQuerySingleAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[PosReceiptProfileAck]");

    private async Task<DateTime> AckAppliedAtAsync(string deviceCode) =>
        await fixture.CreateContext().MainDb.Ado.SqlQuerySingleAsync<DateTime>(
            "SELECT [AppliedAtUtc] FROM [dbo].[PosReceiptProfileAck] WHERE [DeviceCode] = @DeviceCode",
            new SqlSugar.SugarParameter("@DeviceCode", deviceCode));

    private async Task<string?> AckStringAsync(string deviceCode, string column) =>
        await fixture.CreateContext().MainDb.Ado.SqlQuerySingleAsync<string>(
            $"SELECT CAST([{column}] AS nvarchar(100)) FROM [dbo].[PosReceiptProfileAck] WHERE [DeviceCode] = @DeviceCode",
            new SqlSugar.SugarParameter("@DeviceCode", deviceCode));

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
