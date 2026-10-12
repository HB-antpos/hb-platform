using System.Reflection;
using System.Runtime.CompilerServices;
using Hbpos.Api.Data;
using Hbpos.Api.Services;
using Hbpos.Contracts.Catalog;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SqlSugar;

namespace Hbpos.Api.Tests;

/// <summary>
/// 目录读取路径优化：sync-plan 差异阈值提前终止、补建门店价分段锁、非 MARS 独立读取连接的开关。
/// </summary>
public sealed class CatalogReadPathTests
{
    private static readonly DateTimeOffset GeneratedAt = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    // ---- sync-plan 阈值 ----

    [Theory]
    [InlineData(5000, true)]
    [InlineData(5001, false)]
    public void Limited_delta_stops_collecting_once_over_limit_but_keeps_true_count(int changed, bool withinLimit)
    {
        var baseline = CreateIndex("base", Enumerable.Range(0, 6000).Select(i => Item(i, 1m)));
        // 前 changed 个商品改价，其余不变。
        var target = CreateIndex("target", Enumerable.Range(0, 6000).Select(i => Item(i, i < changed ? 2m : 1m)));

        var (operations, count) = target.GetDeltaOperationsLimited(baseline, CatalogService.MaxDeltaOperations);

        Assert.Equal(changed, count);
        Assert.Equal(withinLimit, operations is not null);
        if (operations is not null)
        {
            // 未超限时结果必须与不设上限的归并逐项一致。
            Assert.Equal(
                target.GetDeltaOperations(baseline).Select(x => x.Item?.LookupCodeNormalized),
                operations.Select(x => x.Item?.LookupCodeNormalized));
        }
    }

    [Fact]
    public void Limited_delta_counts_adds_and_deletes_after_the_limit()
    {
        var baseline = CreateIndex("base", Enumerable.Range(0, 100).Select(i => Item(i, 1m)));
        // 目标：删除原有 100 项并新增 200 项，共 300 个差异，上限 50。
        var target = CreateIndex("target", Enumerable.Range(1000, 200).Select(i => Item(i, 1m)));

        var (operations, count) = target.GetDeltaOperationsLimited(baseline, 50);

        Assert.Null(operations);
        Assert.Equal(300, count);
        Assert.Equal(300, target.GetDeltaOperations(baseline).Count);
    }

    [Theory]
    [InlineData(5000, CatalogSyncModes.Delta)]
    [InlineData(5001, CatalogSyncModes.Full)]
    public async Task Sync_plan_switches_to_full_only_above_threshold(int changed, string expectedMode)
    {
        var cache = new CatalogIndexCache();
        var baseline = CreateIndex("catalog-v1:base", Enumerable.Range(0, 6000).Select(i => Item(i, 1m)));
        await cache.GetOrBuildAsync(
            "S01",
            null,
            _ => Task.FromResult<CatalogIndexBuildResult?>(new CatalogIndexBuildResult("S01", GeneratedAt, [], baseline)),
            CancellationToken.None);
        cache.InvalidateStore("S01");
        var target = CreateIndex(
            "catalog-v1:target",
            Enumerable.Range(0, 6000).Select(i => Item(i, i < changed ? 2m : 1m)));
        await cache.GetOrBuildAsync(
            "S01",
            null,
            _ => Task.FromResult<CatalogIndexBuildResult?>(new CatalogIndexBuildResult("S01", GeneratedAt, [], target)),
            CancellationToken.None);
        var service = new CatalogService(UninitializedContext(), new PriceIndexBuilder(), cache);

        var plan = await service.GetCatalogSyncPlanWithLeaseAsync("S01", "catalog-v1:base", CancellationToken.None);

        Assert.Equal(expectedMode, plan!.Mode);
        // 超过阈值时仍返回真实差异数，和先全量再判断时一致。
        Assert.Equal(changed, plan.DeltaOperationCount);
        Assert.NotNull(plan.DownloadLeaseId);
    }

    // ---- 分段锁 ----

    [Fact]
    public void Ensure_lock_index_is_stable_and_within_stripe_range()
    {
        var keys = Enumerable.Range(0, 5000).Select(i => $"S{i % 50:D2}|P{i:D6}").ToArray();
        var indexes = keys.Select(CatalogService.StoreRetailPriceEnsureLockIndex).ToArray();

        // 同 key 永远同一把锁。
        Assert.Equal(indexes, keys.Select(CatalogService.StoreRetailPriceEnsureLockIndex));
        Assert.All(indexes, index => Assert.InRange(index, 0, 255));
        // 不同 key 基本分散：5000 个 key 至少用到 200 把以上。
        Assert.True(indexes.Distinct().Count() > 200);
    }

    // ---- 非 MARS 读取连接 ----

    [Fact]
    public void Non_mars_client_removes_mars_and_keeps_global_settings()
    {
        var context = CreateContext(
            "Server=127.0.0.1;Database=HBweb;User Id=sa;Password=x;TrustServerCertificate=True;MultipleActiveResultSets=True",
            null);

        using var client = context.TryCreateMainNonMarsReadClient();

        Assert.NotNull(client);
        Assert.False(new SqlConnectionStringBuilder(client!.CurrentConnectionConfig.ConnectionString).MultipleActiveResultSets);
        Assert.True(new SqlConnectionStringBuilder(context.MainDb.CurrentConnectionConfig.ConnectionString).MultipleActiveResultSets);
        Assert.Equal(DbType.SqlServer, client.CurrentConnectionConfig.DbType);
        Assert.True(client.CurrentConnectionConfig.IsAutoCloseConnection);
        Assert.True(client.CurrentConnectionConfig.MoreSettings.IsWithNoLockQuery);
        Assert.Equal(30, client.Ado.CommandTimeOut);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("False")]
    public void Non_mars_client_can_be_switched_off_by_setting(string value)
    {
        var context = CreateContext(
            "Server=127.0.0.1;Database=HBweb;User Id=sa;Password=x;TrustServerCertificate=True",
            value);

        Assert.Null(context.TryCreateMainNonMarsReadClient());
    }

    [Fact]
    public void Non_mars_client_is_not_used_for_sqlite_or_uninitialized_context()
    {
        Assert.Null(UninitializedContext().TryCreateMainNonMarsReadClient());
    }

    private static HbposSqlSugarContext UninitializedContext()
    {
        // 与 CatalogServiceTests 同法：绕过构造函数，字段为默认值（开关关闭），MainDb 用 SQLite 客户端。
        var client = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = "Data Source=:memory:",
            DbType = DbType.Sqlite,
            InitKeyType = InitKeyType.Attribute,
            IsAutoCloseConnection = true
        });
        var context = (HbposSqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(HbposSqlSugarContext));
        var field = typeof(HbposSqlSugarContext).GetField(
            "<MainDb>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(context, client);
        return context;
    }

    private static HbposSqlSugarContext CreateContext(string connectionString, string? switchValue)
    {
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:MainConnection"] = connectionString,
            ["ConnectionStrings:PosmConnection"] = connectionString,
            ["Database:CommandTimeoutSeconds"] = "30",
        };
        if (switchValue is not null)
        {
            values[HbposSqlSugarContext.CatalogNonMarsReadsSettingName] = switchValue;
        }

        return new HbposSqlSugarContext(
            new ConfigurationBuilder().AddInMemoryCollection(values).Build(),
            NullLogger<HbposSqlSugarContext>.Instance);
    }

    private static CatalogSellableIndex CreateIndex(string version, IEnumerable<SellableItemDto> items) =>
        new("S01", GeneratedAt, items.ToArray(), version);

    private static SellableItemDto Item(int number, decimal price) =>
        new("S01", $"P{number:D6}", null, $"Item {number}", $"K{number:D6}", null, $"K{number:D6}",
            price, PriceSourceKind.ProductBase, "product", 1m, GeneratedAt);
}
