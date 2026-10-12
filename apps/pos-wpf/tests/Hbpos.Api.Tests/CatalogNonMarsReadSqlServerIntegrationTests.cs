using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HBweb;
using Hbpos.Api.Data;
using Hbpos.Api.Services;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hbpos.Api.Tests;

public sealed class CatalogNonMarsSqlServerFactAttribute : FactAttribute
{
    public CatalogNonMarsSqlServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(DailyCloseSqlServerFactAttribute.ConnectionVariable)))
        {
            Skip = "未配置隔离 SQL Server，跳过目录非 MARS 读取真实 SQL 测试。";
        }
    }
}

/// <summary>
/// 真实 SQL Server 上验证目录构建：开关开启时整表键集分页读取走独立非 MARS 连接（SNAPSHOT 事务开在该连接上），
/// 关闭开关回退共用 MARS 连接；两种模式构建出的目录必须完全一致。每个用例自建独立数据库并在结束时删除。
/// </summary>
[Trait("Category", "SQL")]
public sealed class CatalogNonMarsReadSqlServerIntegrationTests : IAsyncLifetime
{
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

        database = $"HbCatalogNonMars_{Guid.NewGuid():N}";
        var builder = new SqlConnectionStringBuilder(configured) { InitialCatalog = "master" };
        masterConnection = builder.ConnectionString;
        await ExecuteAtAsync(masterConnection, $"CREATE DATABASE [{database}]");
        // 目录快照读取要求库开启快照隔离，与生产一致。
        await ExecuteAtAsync(masterConnection, $"ALTER DATABASE [{database}] SET ALLOW_SNAPSHOT_ISOLATION ON");
        builder.InitialCatalog = database;
        connection = builder.ConnectionString;
        var db = CreateContext(null).MainDb;
        db.CodeFirst.InitTables<Store, Product, StoreRetailPrice>();
        db.CodeFirst.InitTables<StoreMultiCodeProduct, StoreClearancePrice, ProductSetCode>();
        await db.Insertable(new Store
        {
            StoreGUID = "STORE-S01",
            StoreCode = "S01",
            StoreName = "Store S01",
            IsActive = true,
            IsDeleted = false,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        }).ExecuteCommandAsync();
        var products = Enumerable.Range(0, 250).Select(i => new Product
        {
            UUID = $"UUID-{i:D4}",
            ProductCode = $"P{i:D4}",
            ProductName = $"Product {i}",
            ItemNumber = $"ITEM{i:D4}",
            Barcode = $"BAR{i:D4}",
            RetailPrice = 1m + i,
            // 停用商品门店仍可销售，必须读到。
            IsActive = i % 10 != 0,
            IsDeleted = i % 25 == 24,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        }).ToList();
        await db.Insertable(products).ExecuteCommandAsync();
        await db.Insertable(Enumerable.Range(0, 100).Select(i => new StoreRetailPrice
        {
            UUID = $"SRP-{i:D4}",
            StoreCode = "S01",
            ProductCode = $"P{i:D4}",
            StoreProductCode = $"S01-P{i:D4}",
            StoreRetailPriceValue = 50m + i,
            IsActive = true,
            IsDeleted = false,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        }).ToList()).ExecuteCommandAsync();
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
            $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]");
    }

    [CatalogNonMarsSqlServerFact]
    public async Task Bulk_read_client_uses_non_mars_connection_while_main_client_uses_mars()
    {
        var context = CreateContext(null);
        using var bulk = context.TryCreateMainNonMarsReadClient();

        Assert.NotNull(bulk);
        // MARS 连接允许同一连接上同时打开两个读取器；非 MARS 连接第二个命令会抛“已有打开的 DataReader”。
        Assert.True(await CanOpenTwoReadersAsync(context.MainDb));
        Assert.False(await CanOpenTwoReadersAsync(bulk!));
    }

    private static async Task<bool> CanOpenTwoReadersAsync(SqlSugar.ISqlSugarClient client)
    {
        var sqlConnection = (SqlConnection)client.Ado.Connection;
        await sqlConnection.OpenAsync();
        try
        {
            await using var first = new SqlCommand("SELECT 1 UNION ALL SELECT 2", sqlConnection);
            await using var firstReader = await first.ExecuteReaderAsync();
            await firstReader.ReadAsync();
            await using var second = new SqlCommand("SELECT 3", sqlConnection);
            await using var secondReader = await second.ExecuteReaderAsync();
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        finally
        {
            await sqlConnection.CloseAsync();
        }
    }

    [CatalogNonMarsSqlServerFact]
    public async Task Catalog_build_is_identical_with_non_mars_reads_on_or_off()
    {
        var nonMars = await BuildAsync(null);
        var shared = await BuildAsync("false");

        // 250 个商品里 10 个已删除（i % 25 == 24），停用商品（i % 10 == 0）保留。
        Assert.NotEmpty(nonMars.SellableItems);
        Assert.Equal(
            shared.SellableItems.Select(x => (x.LookupCode, x.ProductCode, x.RetailPrice, x.PriceSource)),
            nonMars.SellableItems.Select(x => (x.LookupCode, x.ProductCode, x.RetailPrice, x.PriceSource)));
        Assert.Contains(nonMars.SellableItems, x => x.ProductCode == "P0010");
        Assert.DoesNotContain(nonMars.SellableItems, x => x.ProductCode == "P0024");
        Assert.Contains(nonMars.SellableItems, x => x.ProductCode == "P0001" && x.RetailPrice == 51m);
    }

    private async Task<CatalogIndexBuildResult> BuildAsync(string? nonMarsSetting)
    {
        var service = new CatalogService(
            CreateContext(nonMarsSetting),
            new PriceIndexBuilder(),
            new CatalogIndexCache(),
            new CatalogBaseDataCache());
        var result = await service.BuildCatalogArtifactAsync("S01", CancellationToken.None);
        return Assert.IsType<CatalogIndexBuildResult>(result);
    }

    private HbposSqlSugarContext CreateContext(string? nonMarsSetting)
    {
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:MainConnection"] = connection,
            ["ConnectionStrings:PosmConnection"] = connection,
            ["Database:CommandTimeoutSeconds"] = "60",
        };
        if (nonMarsSetting is not null)
        {
            values[HbposSqlSugarContext.CatalogNonMarsReadsSettingName] = nonMarsSetting;
        }

        return new HbposSqlSugarContext(
            new ConfigurationBuilder().AddInMemoryCollection(values).Build(),
            NullLogger<HbposSqlSugarContext>.Instance);
    }

    private static async Task ExecuteAtAsync(string connectionString, string sql)
    {
        await using var sqlConnection = new SqlConnection(connectionString);
        await sqlConnection.OpenAsync();
        await using var command = new SqlCommand(sql, sqlConnection) { CommandTimeout = 60 };
        await command.ExecuteNonQueryAsync();
    }
}
