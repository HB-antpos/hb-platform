using System.Reflection;
using System.Runtime.CompilerServices;
using BlazorApp.Api.Data;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class LocalProductSalesAnalysisSqlServerFactAttribute : FactAttribute
{
    internal const string ConnectionEnvironmentVariable = "LSPA_SQLSERVER_TEST_CONNECTION";

    public LocalProductSalesAnalysisSqlServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable)))
        {
            Skip = $"未配置 {ConnectionEnvironmentVariable}，跳过本地商品分析 SQL Server 验证。";
        }
    }
}

/// <summary>
/// SQLite 测试走不到 SQL Server 专用的关键词谓词（UPPER + BIN2）与过滤索引直连路径，
/// 这里在一次性临时库里按生产形态建索引后验证语法、参数复用与匹配语义。
/// </summary>
[Trait("Category", "SQL")]
public sealed class LocalSupplierProductSalesAnalysisSqlServerTests : IDisposable
{
    private static readonly DateTime EndDate = DateTime.UtcNow.AddHours(10).Date.AddDays(-1);
    private static readonly DateTime StartDate = EndDate.AddDays(-30);

    private readonly string? _baseConnection;
    private readonly string _databaseName = $"lspa_it_{Guid.NewGuid():N}";
    private readonly SqlSugarClient? _db;

    public LocalSupplierProductSalesAnalysisSqlServerTests()
    {
        _baseConnection = Environment.GetEnvironmentVariable(
            LocalProductSalesAnalysisSqlServerFactAttribute.ConnectionEnvironmentVariable
        );
        if (string.IsNullOrWhiteSpace(_baseConnection))
        {
            return;
        }

        // 只在一次性临时库内读写；排序规则取不区分大小写的中文规则，贴近生产的 CI 语义。
        ExecuteOnMaster($"CREATE DATABASE [{_databaseName}] COLLATE Chinese_PRC_CI_AS");
        try
        {
            var builder = new SqlConnectionStringBuilder(_baseConnection) { InitialCatalog = _databaseName };
            _db = new SqlSugarClient(
                new ConnectionConfig
                {
                    ConnectionString = builder.ConnectionString,
                    DbType = DbType.SqlServer,
                    IsAutoCloseConnection = true,
                    InitKeyType = InitKeyType.Attribute,
                }
            );
            _db.CodeFirst.InitTables(
                typeof(Store),
                typeof(UserStore),
                typeof(HBLocalSupplier),
                typeof(Product),
                typeof(WarehouseCategory),
                typeof(LocalSupplierCategoryProductAssignment),
                typeof(StoreLocalSupplierInvoice),
                typeof(StoreLocalSupplierInvoiceDetails),
                typeof(ProductStoreDailySalesStatistic)
            );
            // 与生产脚本同名的过滤索引：健康探测据此启用“无重复编码直连”路径。
            // CodeFirst 建出的编码列是 varchar，过滤条件里的空串常量不能写成 N''（类型优先级更高会被拒绝）。
            _db.Ado.ExecuteCommand(
                @"SET QUOTED_IDENTIFIER ON; SET ANSI_NULLS ON;
    CREATE NONCLUSTERED INDEX [IX_LSPSA_Product_ProductCode_UUID]
        ON [dbo].[Product] ([ProductCode], [UUID])
        INCLUDE ([LocalSupplierCode], [ItemNumber], [Barcode], [ProductName], [EnglishName], [ProductImage], [WarehouseCategoryGUID])
        WHERE [IsDeleted] = 0 AND [IsActive] = 1
          AND [LocalSupplierCode] IS NOT NULL AND [LocalSupplierCode] <> ''
          AND [ProductCode] IS NOT NULL AND [ProductCode] <> '';"
            );
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    [LocalProductSalesAnalysisSqlServerFact]
    public async Task 关键词查询在SqlServer上使用BIN2谓词且匹配语义不变()
    {
        var db = _db!;
        await SeedAsync(db);
        var sql = new List<string>();
        db.Aop.OnLogExecuting = (statement, _) => sql.Add(statement);
        var service = CreateService(db);

        // 小写关键词命中货号（大小写不敏感）
        var byItemNumber = await service.GetCandidatesAsync(CreateRequest("ki116996"), new List<string> { "B1" });
        Assert.True(byItemNumber.Success, byItemNumber.Message);
        Assert.Equal(new[] { "P001" }, byItemNumber.Data!.Items.Select(item => item.ProductCode));
        Assert.Equal(1, byItemNumber.Data.Total);

        // 中文名称与英文名称
        var byChineseName = await service.GetCandidatesAsync(CreateRequest("餐巾"), new List<string> { "B1" });
        Assert.Equal(new[] { "P001" }, byChineseName.Data!.Items.Select(item => item.ProductCode));
        var byEnglishName = await service.GetCandidatesAsync(CreateRequest("straws"), new List<string> { "B1" });
        Assert.Equal(new[] { "P002" }, byEnglishName.Data!.Items.Select(item => item.ProductCode));

        // 下划线必须按字面匹配，不能当作单字符通配
        var byUnderscore = await service.GetCandidatesAsync(CreateRequest("A_B"), new List<string> { "B1" });
        Assert.Equal(new[] { "P003" }, byUnderscore.Data!.Items.Select(item => item.ProductCode));

        // 已删除与非本地供应商商品不得出现
        var excluded = await service.GetCandidatesAsync(CreateRequest("GHOST"), new List<string> { "B1" });
        Assert.Empty(excluded.Data!.Items);

        var productStatements = sql
            .Where(statement => statement.Contains("[Product]", StringComparison.OrdinalIgnoreCase)
                && statement.Contains("@lspaKeywordPattern", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.NotEmpty(productStatements);
        Assert.All(
            productStatements,
            statement =>
            {
                Assert.Contains("AS nvarchar(4000))) COLLATE Latin1_General_100_BIN2 LIKE", statement);
                Assert.Contains("[IsDeleted] = 0 AND [IsActive] = 1", statement);
                Assert.DoesNotContain("@IsDeleted", statement, StringComparison.OrdinalIgnoreCase);
            }
        );
    }

    [LocalProductSalesAnalysisSqlServerFact]
    public async Task 关键词Bootstrap与全选汇总在SqlServer上返回完整分段()
    {
        var db = _db!;
        await SeedAsync(db);
        var service = CreateService(db);

        var request = CreateRequest("napkin");
        request.AutoSelectFirst = true;
        request.CandidatePageNumber = 1;
        request.CandidatePageSize = 20;
        request.SummaryPageNumber = 1;
        request.SummaryPageSize = 50;
        var bootstrap = await service.BootstrapAsync(request, new List<string> { "B1" });

        Assert.True(bootstrap.Success, bootstrap.Message);
        Assert.False(bootstrap.Data!.Partial, string.Join("; ", bootstrap.Data.SectionErrors.Select(item => $"{item.Key}={item.Value}")));
        Assert.Equal(new[] { "P001", "P004" }, bootstrap.Data.Candidates.Items.Select(item => item.ProductCode));
        Assert.Equal("P001", bootstrap.Data.CurrentProduct?.ProductCode);
        // 首屏自动选中首项时，选择范围是“全部筛选结果”，汇总覆盖两个命中商品。
        Assert.Equal(2, bootstrap.Data.Summary.Total);
        Assert.Equal(24m, bootstrap.Data.Summary.Totals.PurchaseQuantity);
        Assert.Equal(10m, bootstrap.Data.Summary.Totals.NetSalesQuantity);
        var currentRow = Assert.Single(bootstrap.Data.Summary.Items, item => item.ProductCode == "P001");
        Assert.Equal(7m, currentRow.NetSalesQuantity);

        // 关键词 + 全选筛选结果：同一参数化谓词会在进货、销量与主档三处派生表内复用
        var summary = await service.GetSummaryAsync(CreateRequest("napkin"), new List<string> { "B1" });
        Assert.True(summary.Success, summary.Message);
        Assert.Equal(2, summary.Data!.Total);
        Assert.Equal(24m, summary.Data.Totals.PurchaseQuantity);
        Assert.Equal(10m, summary.Data.Totals.NetSalesQuantity);
    }

    [LocalProductSalesAnalysisSqlServerFact]
    public async Task 供应商与仓库分类筛选的全选汇总走快速路径且结果正确()
    {
        var db = _db!;
        await SeedAsync(db);
        CreateFastSchema(db);
        await SeedSecondSupplierAsync(db);

        var logger = new CapturingLogger<LocalSupplierProductSalesAnalysisService>();
        foreach (var scope in new List<string>?[] { new() { "B1" }, null })
        {
            var service = CreateService(db, logger);

            // 供应商编码用小写：生产库为 CI 排序规则，与通用路径的 ToUpper 比较等价。
            var bySupplier = await service.GetSummaryAsync(CreateFilterRequest(supplierCodes: new() { "sup2" }), scope);
            Assert.True(bySupplier.Success, bySupplier.Message + string.Join("\n", logger.Messages));
            Assert.Equal(2, bySupplier.Data!.Total);
            Assert.Equal(new[] { "P008", "P009" }, bySupplier.Data.Items.Select(item => item.ProductCode));
            Assert.Equal(8m, bySupplier.Data.Totals.PurchaseQuantity);
            Assert.Equal(5m, bySupplier.Data.Totals.NetSalesQuantity);

            // 供应商 + 仓库分类取交集：只剩 C1 下的 P008。
            var byBoth = await service.GetSummaryAsync(
                CreateFilterRequest(supplierCodes: new() { "SUP2" }, warehouseCategoryGuids: new() { "c1" }),
                scope
            );
            Assert.True(byBoth.Success, byBoth.Message);
            Assert.Equal(1, byBoth.Data!.Total);
            Assert.Equal("P008", Assert.Single(byBoth.Data.Items).ProductCode);

            // 两个供应商同选：覆盖标准样例（SUP：P001/P002/P003/P004/P005）加上 SUP2 的两个商品。
            var bySuppliers = await service.GetSummaryAsync(CreateFilterRequest(supplierCodes: new() { "SUP", "SUP2" }), scope);
            Assert.True(bySuppliers.Success, bySuppliers.Message);
            Assert.Equal(7, bySuppliers.Data!.Total);
            Assert.Equal(32m, bySuppliers.Data.Totals.PurchaseQuantity);
            Assert.Equal(24m, bySuppliers.Data.Totals.NetSalesQuantity);

            // 无筛选仍走原有的主档覆盖索引读取：与「两个供应商同选」覆盖同一批商品，数字应一致。
            var unfiltered = await service.GetSummaryAsync(CreateFilterRequest(), scope);
            Assert.True(unfiltered.Success, unfiltered.Message + string.Join("\n", logger.Messages));
            Assert.Equal(7, unfiltered.Data!.Total);
            Assert.Equal(32m, unfiltered.Data.Totals.PurchaseQuantity);
            Assert.Equal(24m, unfiltered.Data.Totals.NetSalesQuantity);
        }

        // 三类筛选都应命中快速汇总日志，而不是退回通用路径。
        Assert.True(logger.Messages.Count(message => message.Contains("快速汇总")) >= 8, string.Join("\n", logger.Messages));
    }

    [LocalProductSalesAnalysisSqlServerFact]
    public async Task 供应商分类筛选的全选汇总走快速路径且与归属语义一致()
    {
        var db = _db!;
        await SeedAsync(db);
        CreateFastSchema(db);
        await SeedSecondSupplierAsync(db);
        // 供应商 200 的分类就是仓库分类：W1 是父分类，W1A 是其子分类，P010 挂在子分类下。
        await db.Insertable(
            new List<WarehouseCategory>
            {
                new() { CategoryGUID = "W1", CategoryName = "Parent", IsActive = true },
                new() { CategoryGUID = "W1A", ParentGUID = "W1", CategoryName = "Child", IsActive = true },
            }
        ).ExecuteCommandAsync();
        await db.Insertable(NewProduct("P010", "WH-100", "9328645000000", "供应商200商品", "Supplier 200 Item", localSupplierCode: "200", warehouseCategoryGuid: "W1A")).ExecuteCommandAsync();
        await db.Insertable(
            new List<LocalSupplierCategoryProductAssignment>
            {
                new() { ProductCode = "P008", LocalSupplierCode = "SUP2", CategoryGUID = "SC1" },
                new() { ProductCode = "P009", LocalSupplierCode = "SUP2", CategoryGUID = "SC2" },
                // 归属写入时的供应商与商品当前供应商不一致：视为未归类，不得命中。
                new() { ProductCode = "P001", LocalSupplierCode = "OTHER", CategoryGUID = "SC1" },
            }
        ).ExecuteCommandAsync();

        var logger = new CapturingLogger<LocalSupplierProductSalesAnalysisService>();
        foreach (var scope in new List<string>?[] { new() { "B1" }, null })
        {
            var service = CreateService(db, logger);

            async Task<LocalSupplierProductSalesSummaryResponseDto> Summary(List<string> supplierCategoryGuids, List<string>? supplierCodes = null)
            {
                var request = CreateFilterRequest(supplierCodes: supplierCodes);
                request.Filter.SupplierCategoryGuids = supplierCategoryGuids;
                var response = await service.GetSummaryAsync(request, scope);
                Assert.True(response.Success, response.Message + string.Join("\n", logger.Messages));
                return response.Data!;
            }

            // 单个供应商分类（小写，CI 比较）：P001 因供应商不一致被排除，只剩 P008。
            var single = await Summary(new() { "sc1" });
            Assert.Equal(1, single.Total);
            var row = Assert.Single(single.Items);
            Assert.Equal("P008", row.ProductCode);
            Assert.Equal(8m, single.Totals.PurchaseQuantity);
            Assert.Equal(5m, single.Totals.NetSalesQuantity);

            Assert.Equal(2, (await Summary(new() { "SC1", "SC2" })).Total);

            // 供应商 200：按仓库分类子树匹配，父分类 W1 命中挂在 W1A 下的 P010。
            var warehouseTree = await Summary(new() { "W1" });
            Assert.Equal(1, warehouseTree.Total);
            Assert.Equal("P010", Assert.Single(warehouseTree.Items).ProductCode);

            // 普通供应商分类与仓库分类并存时取并集：P008 + P010。
            Assert.Equal(2, (await Summary(new() { "SC1", "W1" })).Total);

            // 与供应商筛选取交集：分类属于 SUP2，限定 SUP 后为空。
            var empty = await Summary(new() { "SC1", "SC2" }, new() { "SUP" });
            Assert.Equal(0, empty.Total);
            Assert.Empty(empty.Items);
        }

        Assert.True(logger.Messages.Count(message => message.Contains("快速汇总")) >= 10, string.Join("\n", logger.Messages));
    }

    /// <summary>在标准样例上追加第二个供应商：P008 有进货与销量并属于分类 C1，P009 无任何事实，属于分类 C2。</summary>
    private static async Task SeedSecondSupplierAsync(SqlSugarClient db)
    {
        await db.Insertable(
            new List<Product>
            {
                NewProduct("P008", "SP-800", "9328644800000", "二号供应商商品", "Second Supplier Item", localSupplierCode: "SUP2", warehouseCategoryGuid: "C1"),
                NewProduct("P009", "SP-900", "9328644900000", "二号供应商零销量", "Second Supplier Idle", localSupplierCode: "SUP2", warehouseCategoryGuid: "C2"),
            }
        ).ExecuteCommandAsync();
        await db.Insertable(new StoreLocalSupplierInvoiceDetails { DetailGUID = "d8", InvoiceGUID = "h1", StoreCode = "B1", ProductCode = "P008", Quantity = 8m, Amount = 16m, IsDeleted = false }).ExecuteCommandAsync();
        await db.Insertable(new ProductStoreDailySalesStatistic { Date = EndDate.AddDays(-1), BranchCode = "B1", SupplierCode = "SUP2", ProductCode = "P008", TotalQuantity = 5, TotalAmount = 25m }).ExecuteCommandAsync();
    }

    private static void CreateFastSchema(SqlSugarClient db)
    {
        // 与生产性能索引脚本同名同定义。生产编码列是 nvarchar，快速 SQL 里的 <> N'' 才能匹配过滤索引；
        // CodeFirst 建出的是 varchar，这里先转成 nvarchar 并按生产写法 N'' 重建过滤索引。
        db.Ado.ExecuteCommand(
            @"DROP INDEX [IX_LSPSA_Product_ProductCode_UUID] ON [dbo].[Product];
ALTER TABLE [dbo].[Product] ALTER COLUMN [ProductCode] nvarchar(50) NULL;
ALTER TABLE [dbo].[Product] ALTER COLUMN [LocalSupplierCode] nvarchar(50) NULL;
ALTER TABLE [dbo].[StoreLocalSupplierInvoiceDetails] ALTER COLUMN [ProductCode] nvarchar(50) NULL;"
        );
        // 列类型变更必须先于过滤索引的创建提交：同一批次内会按旧列类型校验过滤表达式。
        db.Ado.ExecuteCommand(
            @"SET QUOTED_IDENTIFIER ON; SET ANSI_NULLS ON;
CREATE NONCLUSTERED INDEX [IX_LSPSA_Product_ProductCode_UUID]
    ON [dbo].[Product] ([ProductCode], [UUID])
    INCLUDE ([LocalSupplierCode], [ItemNumber], [Barcode], [ProductName], [EnglishName], [ProductImage], [WarehouseCategoryGUID])
    WHERE [IsDeleted] = 0 AND [IsActive] = 1
      AND [LocalSupplierCode] IS NOT NULL AND [LocalSupplierCode] <> N''
      AND [ProductCode] IS NOT NULL AND [ProductCode] <> N'';
ALTER TABLE [dbo].[StoreLocalSupplierInvoice]
    ADD [EffectivePurchaseDate] AS CONVERT(date, COALESCE([InboundDate], [OrderDate], [CreatedAt])) PERSISTED;
DECLARE @pk nvarchar(300) = (SELECT N'dbo.' + name FROM sys.key_constraints
    WHERE parent_object_id = OBJECT_ID(N'dbo.StoreLocalSupplierInvoice') AND type = 'PK');
EXEC sp_rename @pk, N'PK_StoreLocalSupplierInvoice_InvoiceGUID', N'OBJECT';
CREATE NONCLUSTERED INDEX [IX_LSPSA_Invoice_EffectiveDate_Store_Invoice]
    ON [dbo].[StoreLocalSupplierInvoice] ([EffectivePurchaseDate], [StoreCode], [InvoiceGUID])
    INCLUDE ([SupplierCode], [InvoiceNo], [InboundDate], [OrderDate], [CreatedAt]) WHERE [IsDeleted] = 0;
CREATE NONCLUSTERED INDEX [IX_LSPSA_InvoiceDetails_Product_Invoice]
    ON [dbo].[StoreLocalSupplierInvoiceDetails] ([ProductCode], [InvoiceGUID])
    INCLUDE ([StoreCode], [SupplierCode], [Quantity], [PurchasePrice], [Amount], [ProductName])
    WHERE [IsDeleted] = 0 AND [ProductCode] IS NOT NULL AND [ProductCode] <> N'';
CREATE NONCLUSTERED INDEX [IX_LSPSA_InvoiceDetails_Invoice_Product]
    ON [dbo].[StoreLocalSupplierInvoiceDetails] ([InvoiceGUID], [ProductCode])
    INCLUDE ([StoreCode], [Quantity], [PurchasePrice], [Amount])
    WHERE [IsDeleted] = 0 AND [ProductCode] IS NOT NULL AND [ProductCode] <> N'';
CREATE NONCLUSTERED INDEX [IX_LSPSA_Sales_Product_Date]
    ON [dbo].[ProductStoreDailySalesStatistic] ([ProductCode], [Date]) INCLUDE ([BranchCode], [TotalQuantity], [TotalAmount]);
CREATE NONCLUSTERED INDEX [IX_LSPSA_Sales_Date_Product]
    ON [dbo].[ProductStoreDailySalesStatistic] ([Date], [ProductCode]) INCLUDE ([BranchCode], [TotalQuantity], [TotalAmount]);
CREATE NONCLUSTERED INDEX [IX_ProductStoreDailySalesStatistic_Branch_Product_Date]
    ON [dbo].[ProductStoreDailySalesStatistic] ([BranchCode], [ProductCode], [Date]) INCLUDE ([TotalQuantity], [TotalAmount]);"
        );
    }

    private static LocalSupplierProductSalesAnalysisRequest CreateFilterRequest(
        List<string>? supplierCodes = null,
        List<string>? warehouseCategoryGuids = null
    ) => new()
    {
        Filter = new LocalSupplierProductSalesAnalysisFilterDto
        {
            StartDate = StartDate,
            EndDate = EndDate,
            SupplierCodes = supplierCodes,
            WarehouseCategoryGuids = warehouseCategoryGuids,
        },
        Selection = new LocalSupplierProductSalesSelectionDto { Mode = "allFiltered" },
        SummaryPageNumber = 1,
        SummaryPageSize = 50,
    };

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(exception is null ? formatter(state, exception) : $"{formatter(state, exception)} {exception}");
    }

    public void Dispose()
    {
        _db?.Dispose();
        if (string.IsNullOrWhiteSpace(_baseConnection))
        {
            return;
        }

        try
        {
            SqlConnection.ClearAllPools();
            ExecuteOnMaster(
                $"IF DB_ID(N'{_databaseName}') IS NOT NULL BEGIN ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_databaseName}]; END"
            );
        }
        catch
        {
            // 临时库清理失败不影响断言结果；库名带随机后缀，可人工清理。
        }
    }

    private void ExecuteOnMaster(string sql)
    {
        var builder = new SqlConnectionStringBuilder(_baseConnection) { InitialCatalog = "master" };
        using var connection = new SqlConnection(builder.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static async Task SeedAsync(SqlSugarClient db)
    {
        await db.Insertable(new Store { StoreGUID = "guid-B1", StoreCode = "B1", StoreName = "Branch One", IsDeleted = false, IsActive = true }).ExecuteCommandAsync();
        await db.Insertable(new HBLocalSupplier { Guid = "guid-SUP", LocalSupplierCode = "SUP", Name = "Supplier One", Status = 1, IsDeleted = false }).ExecuteCommandAsync();
        await db.Insertable(
            new List<Product>
            {
                NewProduct("P001", "KI116996", "9328644116996", "环保餐巾纸 40x40", "Eco Napkin 40x40cm Pk50"),
                NewProduct("P002", "KI200000", "9328644200000", "纸吸管", "Paper Straws 6mm"),
                NewProduct("P003", "ZZ-A_B-01", "9328644300000", "下划线货号", "Underscore Item"),
                NewProduct("P004", "KI116990", "9328644116990", "环保纸巾 33x33", "Eco Napkin 33x33cm Pk100"),
                NewProduct("P005", "ZZ-AXB-01", "9328644500000", "干扰项", "Wildcard Decoy"),
                NewProduct("P006", "GHOST-1", "9328644600000", "已删除", "Ghost Deleted", isDeleted: true),
                NewProduct("P007", "GHOST-2", "9328644700000", "非本地供应商", "Ghost No Supplier", localSupplierCode: null),
            }
        ).ExecuteCommandAsync();
        await db.Insertable(new StoreLocalSupplierInvoice { InvoiceGUID = "h1", StoreCode = "B1", SupplierCode = "SUP", InboundDate = EndDate.AddDays(-5), IsDeleted = false }).ExecuteCommandAsync();
        await db.Insertable(new StoreLocalSupplierInvoiceDetails { DetailGUID = "d1", InvoiceGUID = "h1", StoreCode = "B1", ProductCode = "P001", Quantity = 24m, Amount = 26.64m, IsDeleted = false }).ExecuteCommandAsync();
        await db.Insertable(
            new List<ProductStoreDailySalesStatistic>
            {
                new() { Date = EndDate.AddDays(-3), BranchCode = "B1", SupplierCode = "SUP", ProductCode = "P001", TotalQuantity = 7, TotalAmount = 20.93m },
                new() { Date = EndDate.AddDays(-2), BranchCode = "B1", SupplierCode = "SUP", ProductCode = "P004", TotalQuantity = 3, TotalAmount = 11.97m },
                new() { Date = EndDate.AddDays(-2), BranchCode = "B1", SupplierCode = "SUP", ProductCode = "P002", TotalQuantity = 9, TotalAmount = 17.91m },
            }
        ).ExecuteCommandAsync();
    }

    private static Product NewProduct(
        string productCode,
        string itemNumber,
        string barcode,
        string name,
        string englishName,
        bool isDeleted = false,
        string? localSupplierCode = "SUP",
        string? warehouseCategoryGuid = null
    ) => new()
    {
        UUID = $"uuid-{productCode}",
        ProductCode = productCode,
        ItemNumber = itemNumber,
        Barcode = barcode,
        ProductName = name,
        EnglishName = englishName,
        ProductImage = $"{productCode}.jpg",
        LocalSupplierCode = localSupplierCode,
        WarehouseCategoryGUID = warehouseCategoryGuid,
        IsDeleted = isDeleted,
        IsActive = true,
    };

    private static LocalSupplierProductSalesAnalysisRequest CreateRequest(string keyword) => new()
    {
        Filter = new LocalSupplierProductSalesAnalysisFilterDto
        {
            StartDate = StartDate,
            EndDate = EndDate,
            Keyword = keyword,
        },
        Selection = new LocalSupplierProductSalesSelectionDto { Mode = "allFiltered" },
    };

    private static LocalSupplierProductSalesAnalysisService CreateService(
        ISqlSugarClient db,
        ILogger<LocalSupplierProductSalesAnalysisService>? logger = null
    )
    {
        var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(SqlSugarContext));
        typeof(SqlSugarContext)
            .GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(context, db);
        return new LocalSupplierProductSalesAnalysisService(
            context,
            new MemoryCache(new MemoryCacheOptions()),
            logger ?? NullLogger<LocalSupplierProductSalesAnalysisService>.Instance
        );
    }
}
