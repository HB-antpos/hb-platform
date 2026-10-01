using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Json;
using BlazorApp.Api.Data;
using BlazorApp.Api.Features.StoreOrders.Common;
using BlazorApp.Api.Features.StoreOrders.ProductHistory;
using BlazorApp.Api.Features.StoreOrders.ProductPicker.Domain;
using BlazorApp.Api.Features.StoreOrders.ProductPicker.Infrastructure;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class StoreOrderProductPickerSqlServerFactAttribute : FactAttribute
{
    private const string ConnectionEnvironmentVariable = "HB_TEST_SQLSERVER_CONNECTION";

    public StoreOrderProductPickerSqlServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable)))
        {
            Skip = $"未配置 {ConnectionEnvironmentVariable}，跳过真实 SQL Server 订货商品查询验证。";
        }
    }
}

/// <summary>
/// 订货商品分页原生 SQL 与改造前 SqlSugar 写法在真实 SQL Server 上逐字段对照，
/// 以及动态数据一次扫描、商品编码参数化的验证。表结构按生产关键列类型建（WarehouseProduct.ProductCode
/// 为 varchar(255)，与 Product.ProductCode nvarchar(50) 不同型），排序规则与生产一致（Chinese_PRC_90_CI_AS）。
/// </summary>
[Trait("Category", "SQL")]
public sealed class StoreOrderProductPickerSqlServerTests
{
    private const string SqlServerTestConnectionEnvVar = "HB_TEST_SQLSERVER_CONNECTION";
    private const string CategoryRoot = "CAT-ROOT";
    private const string CategoryA = "CAT-A";
    private const string CategoryB = "CAT-B";
    private const string LocationIdentifier = "LOC-01";

    private static readonly (string Name, Func<StoreOrderFilterDto> Filter, bool Location)[] Cases =
    {
        ("首页", () => Filter(), false),
        ("货号 hb246", () => Filter(item: "hb246"), false),
        ("货号大写 HB246", () => Filter(item: "HB246"), false),
        ("条码片段", () => Filter(item: "95258124"), false),
        ("通配符 hb2_6", () => Filter(item: "hb2_6"), false),
        ("中文名称", () => Filter(name: "气球"), false),
        ("名称 balloon", () => Filter(name: "balloon"), false),
        ("统一关键字", () => Filter(item: "Balloon", name: "balloon"), false),
        ("拆分关键字", () => Filter(item: "FB", name: "balloon"), false),
        ("大分类含子分类", () => Filter(category: CategoryRoot), false),
        ("小分类", () => Filter(category: "CAT-A1"), false),
        ("分类含已删除子分类", () => Filter(category: CategoryA), false),
        ("分类不存在", () => Filter(category: "CAT-NOPE"), false),
        ("等级 A", () => Filter(grade: "A"), false),
        ("等级 a,C", () => Filter(grade: "a,C"), false),
        ("分类+关键字+等级", () => Filter(category: CategoryRoot, item: "hb", grade: "A,B"), false),
        ("价格升序", () => Filter(sort: "PriceAsc"), false),
        ("价格降序", () => Filter(sort: "PriceDesc"), false),
        ("名称排序", () => Filter(sort: "Name"), false),
        ("商品名降序", () => Filter(sort: "productName", descending: true), false),
        ("条码降序", () => Filter(sort: "barcode", descending: true), false),
        ("库存降序", () => Filter(sort: "stockQuantity", descending: true), false),
        ("起订量升序", () => Filter(sort: "minOrderQuantity"), false),
        ("进口价降序", () => Filter(sort: "importPrice", descending: true), false),
        ("货号降序", () => Filter(sort: "itemNumber", descending: true, category: CategoryB), false),
        ("澳洲供应商", () => Filter(localSupplier: "LS1"), false),
        ("国内供应商", () => Filter(supplier: "HB246"), false),
        ("列筛选文本与数值", () => Filter(columns: new StoreOrderProductColumnFiltersDto
        {
            ItemNumber = "HB",
            Barcode = "95",
            StockQuantityMin = 1,
            StockQuantityMax = 50,
            MinOrderQuantityMax = 6,
        }), false),
        ("列筛选供应商关键字中文", () => Filter(columns: new StoreOrderProductColumnFiltersDto { SupplierKeyword = "礼品" }), false),
        ("列筛选供应商关键字编号", () => Filter(columns: new StoreOrderProductColumnFiltersDto { SupplierKeyword = "hb215" }), false),
        ("列筛选供应商关键字店号", () => Filter(columns: new StoreOrderProductColumnFiltersDto { SupplierKeyword = "z-01" }), false),
        ("列筛选进口价区间", () => Filter(columns: new StoreOrderProductColumnFiltersDto { ImportPriceMin = 1m, ImportPriceMax = 3m, ProductName = "bag" }), false),
        ("快速加入含下架", () => Filter(item: "HB", includeInactive: true), false),
        ("货位命中", () => Filter(item: LocationIdentifier), true),
        ("货位+关键字", () => Filter(item: "FB", name: "fb"), true),
        ("货位+拆分关键字", () => Filter(item: "hb", name: "balloon"), true),
    };

    [StoreOrderProductPickerSqlServerFact]
    public async Task 原生分页查询与SqlSugar写法在各筛选形态逐页逐字段一致()
    {
        await using var fixture = await PickerSqlServerFixture.CreateAsync();
        var failures = new List<string>();
        var comparedPages = 0;
        var emptyCases = new List<string>();

        foreach (var (name, createFilter, location) in Cases)
        {
            var store = fixture.CreatePageQueryStore(location);
            var firstPage = createFilter();
            firstPage.PageSize = 4;
            var legacyFirst = await store.GetPagedListWithSqlSugarAsync(ToInput(firstPage));
            if (legacyFirst.Total == 0)
            {
                emptyCases.Add(name);
            }

            // 逐页翻到越界后一页，覆盖越界页的计数兜底。
            var pageCount = Math.Max(1, (int)Math.Ceiling(legacyFirst.Total / 4d)) + 1;
            for (var page = 1; page <= pageCount; page++)
            {
                var filter = createFilter();
                filter.PageSize = 4;
                filter.PageNumber = page;
                var legacy = await store.GetPagedListWithSqlSugarAsync(ToInput(filter));
                fixture.ExecutedSql.Clear();
                var native = await store.GetPagedListAsync(ToInput(filter));
                comparedPages++;

                if (!fixture.ExecutedSql.Any(sql => sql.Contains("INNER LOOP JOIN [Product] p", StringComparison.Ordinal)))
                {
                    failures.Add($"{name} p{page}: 未走原生 SQL");
                }

                var expected = Describe(legacy);
                var actual = Describe(native);
                if (expected != actual)
                {
                    failures.Add($"{name} p{page}:\n  expected {expected}\n  actual   {actual}");
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
        Assert.True(comparedPages > Cases.Length * 2, $"只比对了 {comparedPages} 页");
        // 造数要让每种形态都有命中，空结果对照没有区分度；只有“分类不存在”应为空。
        Assert.Equal(new[] { "分类不存在" }, emptyCases);
    }

    [StoreOrderProductPickerSqlServerFact]
    public async Task 原生分页查询参数化列表且删除条件写字面量()
    {
        await using var fixture = await PickerSqlServerFixture.CreateAsync();
        var store = fixture.CreatePageQueryStore(location: false);

        var leafASql = await CaptureNativeSqlAsync(fixture, store, Filter(category: "CAT-A1", grade: "A"));
        var leafBSql = await CaptureNativeSqlAsync(fixture, store, Filter(category: "CAT-B1", grade: "B"));
        // 不同分类、等级只要个数落在同一档位，SQL 文本完全相同，SQL Server 复用同一份计划。
        Assert.Equal(leafASql, leafBSql);
        Assert.Contains("p.[WarehouseCategoryGUID] IN (@CategoryId0)", leafASql, StringComparison.Ordinal);
        Assert.Contains("g.[Grade] IN (@Grade0)", leafASql, StringComparison.Ordinal);
        Assert.DoesNotContain("CAT-A1", leafASql, StringComparison.Ordinal);

        // CAT-A 含 3 个分类（自身 + 2 个子分类），按 2 的幂补齐到 4 个参数。
        var categoryASql = await CaptureNativeSqlAsync(fixture, store, Filter(category: CategoryA));
        Assert.Contains("@CategoryId3)", categoryASql, StringComparison.Ordinal);
        Assert.DoesNotContain("@CategoryId4", categoryASql, StringComparison.Ordinal);

        Assert.Contains("FROM [WarehouseProduct] wp WITH(NOLOCK)", leafASql, StringComparison.Ordinal);
        Assert.Contains(
            "INNER LOOP JOIN [Product] p WITH(NOLOCK) ON p.[ProductCode] = CAST(wp.[ProductCode] AS nvarchar(255))",
            leafASql,
            StringComparison.Ordinal
        );
        // 过滤索引要求字面量 IsDeleted = 0，不能是参数或 NOT(IsDeleted = 1)。
        Assert.Contains("wp.[IsDeleted] = 0", leafASql, StringComparison.Ordinal);
        Assert.Contains("p.[IsDeleted] = 0", leafASql, StringComparison.Ordinal);
        Assert.DoesNotContain("@IsDeleted", leafASql, StringComparison.Ordinal);
        Assert.DoesNotContain("NOT (", leafASql, StringComparison.Ordinal);

        // 查其它表的条件一律是排在 Product 之后的 HASH JOIN 派生表，不能写成会被下推进 LOOP JOIN 内侧的 EXISTS。
        var joinedFilterSql = await CaptureNativeSqlAsync(
            fixture,
            store,
            Filter(
                category: CategoryRoot,
                grade: "A,B",
                supplier: "HB246",
                columns: new StoreOrderProductColumnFiltersDto { SupplierKeyword = "礼品" }
            )
        );
        Assert.DoesNotContain("EXISTS", joinedFilterSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("INNER HASH JOIN (SELECT DISTINCT dp.[ProductCode] FROM [DomesticProduct] dp", joinedFilterSql, StringComparison.Ordinal);
        Assert.Contains(") supplierByBarcode ON supplierByBarcode.[Barcode] = p.[Barcode]", joinedFilterSql, StringComparison.Ordinal);
    }

    private static async Task<string> CaptureNativeSqlAsync(
        PickerSqlServerFixture fixture,
        ProductPickerPageQueryStore store,
        StoreOrderFilterDto filter
    )
    {
        fixture.ExecutedSql.Clear();
        await store.GetPagedListAsync(ToInput(filter));
        return Assert.Single(
            fixture.ExecutedSql,
            sql => sql.Contains("COUNT(1) OVER ()", StringComparison.Ordinal)
        );
    }

    [StoreOrderProductPickerSqlServerFact]
    public async Task 首页预热与准确首页缓存走原生查询且与旧首页一致()
    {
        await using var fixture = await PickerSqlServerFixture.CreateAsync();
        var store = fixture.CreatePageQueryStore(location: false);
        var legacyFilter = Filter();
        legacyFilter.PageSize = 5;
        var legacy = await store.GetPagedListWithSqlSugarAsync(ToInput(legacyFilter));

        var accurate = await store.GetHomePageAsync(
            new ProductPickerHomePageInput(5, ProductPickerHomePageMode.AccurateCache)
        );
        var lightweight = await store.GetHomePageAsync(
            new ProductPickerHomePageInput(5, ProductPickerHomePageMode.LightweightWarmUp)
        );

        Assert.Equal(Describe(legacy), Describe(accurate));
        Assert.Equal(
            legacy.Items.Select(item => item.ProductCode),
            lightweight.Items.Select(item => item.ProductCode)
        );
        // 轻量预热键保持旧语义：Total 取本页条数，不代表全量。
        Assert.Equal(5, lightweight.Total);
    }

    [StoreOrderProductPickerSqlServerFact]
    public async Task 动态数据一次扫描取最近订货且商品编码参数化()
    {
        await using var fixture = await PickerSqlServerFixture.CreateAsync();
        await fixture.SeedOrdersAsync();
        var request = new StoreOrderDynamicDataRequestDto
        {
            StoreCode = "S1",
            ProductCodes = new List<string> { "P01", "P02", "P03", "P04" },
            IncludeSales = false,
        };

        fixture.ExecutedSql.Clear();
        var storeResult = await fixture.CreateHistorySlice().GetProductsDynamicDataAsync(request);

        Assert.True(storeResult.Success, storeResult.Message);
        var byCode = storeResult.Data!.ToDictionary(item => item.ProductCode);
        // P01：6-01 有两单，按 CreatedAt 取较晚的 O2，同单两行数量合并；已删除订单/明细、其它门店不参与。
        Assert.Equal(new DateTime(2026, 6, 1), byCode["P01"].LastOrderDate);
        Assert.Equal(7m, byCode["P01"].LastQuantity);
        Assert.Equal(3m, byCode["P01"].LastAllocQuantity);
        Assert.Equal(5m, byCode["P01"].CartQuantity);
        // P02：唯一历史订单没有订货日期时，旧逻辑按 null == null 仍返回该单数量。
        Assert.Null(byCode["P02"].LastOrderDate);
        Assert.Equal(5m, byCode["P02"].LastQuantity);
        Assert.Equal(1.5m, byCode["P02"].CartQuantity);
        // P03：有日期的订单优先于日期为空的订单。
        Assert.Equal(new DateTime(2026, 4, 1), byCode["P03"].LastOrderDate);
        Assert.Equal(2m, byCode["P03"].LastQuantity);
        Assert.Null(byCode["P04"].LastOrderDate);
        Assert.Equal(0m, byCode["P04"].CartQuantity);
        Assert.All(storeResult.Data!, item => Assert.Null(item.SalesQuantitySinceLastArrival));

        // 购物车 + 历史各一条 SQL；商品编码走参数列表（4 个编码正好一档），最近日期用窗口函数在同一次扫描里求。
        var orderSql = fixture.ExecutedSql
            .Where(sql => sql.Contains("[WareHouseOrderDetails]", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(2, orderSql.Count);
        Assert.All(orderSql, sql =>
        {
            Assert.Contains(
                "d.[ProductCode] IN (@ProductCode0, @ProductCode1, @ProductCode2, @ProductCode3)",
                sql,
                StringComparison.Ordinal
            );
            Assert.DoesNotContain("'P01'", sql, StringComparison.Ordinal);
        });
        Assert.Contains(orderSql, sql => sql.Contains("MAX(o.[OrderDate]) OVER (PARTITION BY d.[ProductCode])", StringComparison.Ordinal));
        // IncludeSales=false 时不触碰门店与销售统计表。
        Assert.DoesNotContain(fixture.ExecutedSql, sql => sql.Contains("[Store]", StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.ExecutedSql, sql => sql.Contains("ProductStoreDailySalesStatistic", StringComparison.Ordinal));

        var staffResult = await fixture.CreateHistorySlice(warehouseStaffUserGuid: "staff-1")
            .GetProductsDynamicDataAsync(request);
        var staffByCode = staffResult.Data!.ToDictionary(item => item.ProductCode);
        // 纯仓库员工只看自己的独立购物车。
        Assert.Equal(10m, staffByCode["P01"].CartQuantity);
        Assert.Equal(0m, staffByCode["P02"].CartQuantity);
        Assert.Equal(7m, staffByCode["P01"].LastQuantity);
    }

    private static ProductPickerPageInput ToInput(StoreOrderFilterDto filter) =>
        new(filter, ProductPickerRules.NormalizeGrades(filter.Grade));

    private static StoreOrderFilterDto Filter(
        string? item = null,
        string? name = null,
        string? category = null,
        string? grade = null,
        string sort = "Default",
        bool descending = false,
        string? localSupplier = null,
        string? supplier = null,
        StoreOrderProductColumnFiltersDto? columns = null,
        bool includeInactive = false
    ) => new()
    {
        StoreCode = "S1",
        PageNumber = 1,
        PageSize = 18,
        ItemNumber = item,
        ProductName = name,
        CategoryGUID = category,
        Grade = grade,
        SortBy = sort,
        SortDescending = descending,
        LocalSupplierCode = localSupplier,
        SupplierCode = supplier,
        ColumnFilters = columns,
        IncludeInactiveWarehouseProducts = includeInactive,
    };

    private static string Describe(PagedListReactDto<StoreOrderProductDto> page) =>
        $"total={page.Total} page={page.PageNumber}/{page.PageSize} items={JsonSerializer.Serialize(page.Items)}";

    private sealed class PickerSqlServerFixture : IAsyncDisposable
    {
        private readonly string _masterConnectionString;
        private readonly string _databaseName;
        private readonly string _databaseConnectionString;
        private readonly SqlSugarClient _db;
        private readonly SqlSugarContext _context;

        private PickerSqlServerFixture(
            string masterConnectionString,
            string databaseName,
            string databaseConnectionString
        )
        {
            _masterConnectionString = masterConnectionString;
            _databaseName = databaseName;
            _databaseConnectionString = databaseConnectionString;
            _db = new SqlSugarClient(new ConnectionConfig
            {
                ConnectionString = databaseConnectionString,
                DbType = DbType.SqlServer,
                IsAutoCloseConnection = true,
                InitKeyType = InitKeyType.Attribute,
                // 与生产 SqlSugarContext 一致：读查询带 NOLOCK，CodeFirst 字符串列用 nvarchar。
                MoreSettings = new ConnMoreSettings
                {
                    IsWithNoLockQuery = true,
                    DisableWithNoLockWithTran = true,
                    SqlServerCodeFirstNvarchar = true,
                },
            });
            _db.Aop.OnLogExecuting = (sql, _) => ExecutedSql.Add(sql);
            _context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(SqlSugarContext));
            typeof(SqlSugarContext)
                .GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(_context, _db);
        }

        public List<string> ExecutedSql { get; } = new();

        public static async Task<PickerSqlServerFixture> CreateAsync()
        {
            var baseConnectionString = Environment.GetEnvironmentVariable(SqlServerTestConnectionEnvVar);
            if (string.IsNullOrWhiteSpace(baseConnectionString))
            {
                throw new InvalidOperationException($"未配置 {SqlServerTestConnectionEnvVar}。");
            }

            EnsureLoopbackSqlServer(baseConnectionString);
            var databaseName = $"HbStoreOrderPicker_{Guid.NewGuid():N}";
            var masterConnectionString = BuildConnectionString(baseConnectionString, "master");
            var databaseConnectionString = BuildConnectionString(baseConnectionString, databaseName);
            await ExecuteNonQueryAsync(
                masterConnectionString,
                $"CREATE DATABASE {QuoteSqlServerName(databaseName)} COLLATE Chinese_PRC_90_CI_AS;"
            );

            PickerSqlServerFixture? fixture = null;
            try
            {
                fixture = new PickerSqlServerFixture(
                    masterConnectionString,
                    databaseName,
                    databaseConnectionString
                );
                await fixture.SeedProductsAsync();
                fixture.ExecutedSql.Clear();
                return fixture;
            }
            catch
            {
                if (fixture is not null)
                {
                    await fixture.DisposeAsync();
                }
                else
                {
                    await DropDatabaseAsync(masterConnectionString, databaseName);
                }

                throw;
            }
        }

        public ProductPickerPageQueryStore CreatePageQueryStore(bool location)
        {
            var lookup = new FixedLocationLookup(location);
            return new ProductPickerPageQueryStore(
                _context,
                new ProductPickerProductEnricher(_context),
                lookup,
                NullLogger<ProductPickerPageQueryStore>.Instance
            );
        }

        public IStoreOrderProductHistorySlice CreateHistorySlice(string? warehouseStaffUserGuid = null)
        {
            var claims = new List<Claim>();
            if (warehouseStaffUserGuid != null)
            {
                claims.Add(new Claim(ClaimTypes.Role, "WarehouseStaff"));
                claims.Add(new Claim("userId", warehouseStaffUserGuid));
            }

            var accessor = new HttpContextAccessor
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth")),
                },
            };
            return StoreOrderProductHistoryLegacyFactory.Create(_context, accessor);
        }

        public async ValueTask DisposeAsync()
        {
            _db.Dispose();
            using (var connection = new SqlConnection(_databaseConnectionString))
            {
                SqlConnection.ClearPool(connection);
            }

            await DropDatabaseAsync(_masterConnectionString, _databaseName);
        }

        private async Task SeedProductsAsync()
        {
            _db.CodeFirst.InitTables(
                typeof(Product),
                typeof(HBLocalSupplier),
                typeof(ProductGrade),
                typeof(DomesticProduct),
                typeof(ChinaSupplier),
                typeof(WareHouseOrder),
                typeof(WareHouseOrderDetails)
            );
            // 与生产一致的关键列类型：两表商品编码不同型、分类编码为 varchar；Product.ProductCode 唯一。
            await ExecuteNonQueryAsync(
                _databaseConnectionString,
                """
                CREATE TABLE [WarehouseProduct] (
                    [ProductCode] varchar(255) NOT NULL PRIMARY KEY,
                    [DomesticPrice] decimal(18,2) NULL, [OEMPrice] decimal(18,2) NULL, [ImportPrice] decimal(18,2) NULL,
                    [StockQuantity] int NULL, [MinOrderQuantity] int NULL, [StockValue] decimal(18,2) NULL,
                    [StockAlertQuantity] int NULL, [IsActive] bit NOT NULL, [Volume] decimal(18,4) NULL,
                    [PackingQuantity] int NULL, [CreatedAt] datetime NOT NULL, [CreatedBy] nvarchar(50) NULL,
                    [UpdatedAt] datetime NULL, [UpdatedBy] nvarchar(50) NULL, [IsDeleted] bit NULL
                );
                CREATE TABLE [WarehouseCategory] (
                    [CategoryGUID] varchar(255) NOT NULL PRIMARY KEY, [ParentGUID] varchar(255) NULL,
                    [CategoryName] varchar(100) NOT NULL, [ChineseName] nvarchar(100) NULL, [IsActive] bit NOT NULL,
                    [SortOrder] int NULL, [Remarks] nvarchar(500) NULL, [CreatedAt] datetime NOT NULL,
                    [CreatedBy] nvarchar(50) NULL, [UpdatedAt] datetime NULL, [UpdatedBy] nvarchar(50) NULL,
                    [IsDeleted] bit NULL
                );
                CREATE UNIQUE INDEX [UX_Product_ProductCode] ON [Product]([ProductCode]);
                """
            );

            await _db.Insertable(new List<WarehouseCategory>
            {
                Category(CategoryRoot, null, "Root"),
                Category(CategoryA, CategoryRoot, "Craft"),
                Category("CAT-A1", CategoryA, "Beads"),
                // 旧写法取子分类时不过滤删除标记，已删除子分类下的商品仍算在父分类里。
                Category("CAT-A2", CategoryA, "Deleted child", isDeleted: true),
                Category(CategoryB, CategoryRoot, "Party"),
                Category("CAT-B1", CategoryB, "Balloon"),
            }).ExecuteCommandAsync();

            await _db.Insertable(new List<HBLocalSupplier>
            {
                new() { Guid = "LS-G1", LocalSupplierCode = "LS1", Name = "Supplier One" },
                new() { Guid = "LS-G2", LocalSupplierCode = "LS2", Name = "Deleted Supplier", IsDeleted = true },
            }).ExecuteCommandAsync();

            var products = new List<Product>();
            var warehouseProducts = new List<WarehouseProduct>();
            void Add(
                string code,
                string? item,
                string? barcode,
                string name,
                string? category,
                decimal? oemPrice,
                int? stock,
                int? minOrder,
                decimal? importPrice,
                string? localSupplier = "LS1",
                bool productActive = true,
                bool warehouseActive = true,
                bool productDeleted = false,
                bool warehouseDeleted = false,
                string? warehouseCode = null
            )
            {
                products.Add(new Product
                {
                    UUID = "U-" + code,
                    ProductCode = code,
                    ItemNumber = item,
                    Barcode = barcode,
                    ProductName = name,
                    WarehouseCategoryGUID = category,
                    LocalSupplierCode = localSupplier,
                    MiddlePackageQuantity = stock.HasValue ? stock % 7 : null,
                    ProductImage = $"img/{code}.jpg",
                    IsActive = productActive,
                    IsDeleted = productDeleted,
                });
                warehouseProducts.Add(new WarehouseProduct
                {
                    ProductCode = warehouseCode ?? code,
                    OEMPrice = oemPrice,
                    StockQuantity = stock,
                    MinOrderQuantity = minOrder,
                    ImportPrice = importPrice,
                    IsActive = warehouseActive,
                    IsDeleted = warehouseDeleted,
                });
            }

            Add("P01", "HB246-001", "9525812460179", "Foil Balloon Gold", "CAT-A1", 2.5m, 10, 6, 1.2m);
            Add("P02", "hb246-002", "9525812460186", "BALLOON silver", "CAT-A1", 2.5m, null, null, null);
            Add("P03", "HB2X6-003", "6926393377694", "Paper Bag", "CAT-A2", 2.5m, 0, 12, 2.4m);
            Add("P04", "FB100", "8058617785768", "气球 金色 Balloon", "cat-b1", null, 5, 1, 3m);
            Add("P05", "FB101", null, "Party Hat", CategoryB, 1m, 60, 3, 0.5m);
            Add("P06", "MQ131-3", "6926393377670", "Button Bag", CategoryA, 4m, 3, 24, 2m, localSupplier: "LS2");
            Add("P07", "8200518", "8052533205188", "Shoelaces", null, 0.8m, 20, 6, 0.3m, localSupplier: null);
            Add("P08", "HB051-067", "9525810510033", "car charger", "CAT-A1", 5m, 1, 2, 4m);
            Add("P09", "FB0008", "8058617589083", "silver balloon number 7", "CAT-B1", 3m, 7, 6, 1.5m);
            Add("P10", null, "9525800000010", "No item number", CategoryRoot, 6m, 2, 6, 3m);
            Add("P11", "HB300-001", "9525830000011", "kitchen bag", "CAT-A1", 1.5m, 40, 6, 1m);
            Add("P12", "HB300-002", "9525830000012", "kitchen balloon", "CAT-B1", 1.5m, 41, 6, 1m);
            Add("P13", "ZZ-013", "1113", "Tail product", null, 9m, 90, 90, 9m);
            // 下架 / 删除 / 缺另一侧：下架只在货号快速加入时可见，删除永远不可见。
            Add("P20", "HB246-020", "9525000000020", "Inactive product", "CAT-A1", 2m, 1, 1, 1m, productActive: false);
            Add("P21", "HB246-021", "9525000000021", "Inactive warehouse", "CAT-A1", 2m, 1, 1, 1m, warehouseActive: false);
            Add("P22", "HB246-022", "9525000000022", "Deleted product", "CAT-A1", 2m, 1, 1, 1m, productDeleted: true);
            Add("P23", "HB246-023", "9525000000023", "Deleted warehouse", "CAT-A1", 2m, 1, 1, 1m, warehouseDeleted: true);
            // 仓库侧编码大小写不同，两表按库排序规则（不区分大小写）连接。
            Add("P25", "HB246-025", "9525000000025", "Case join balloon", "CAT-A1", 2m, 3, 1, 1m, warehouseCode: "p25");
            products.Add(new Product { UUID = "U-P24", ProductCode = "P24", ItemNumber = "HB246-024", ProductName = "No warehouse row" });
            warehouseProducts.Add(new WarehouseProduct { ProductCode = "ORPHAN", OEMPrice = 1m, IsActive = true });

            await _db.Insertable(products).ExecuteCommandAsync();
            await _db.Insertable(warehouseProducts).ExecuteCommandAsync();

            await _db.Insertable(new List<ProductGrade>
            {
                new() { ProductCode = "P01", Grade = "A" },
                new() { ProductCode = "P02", Grade = "B" },
                new() { ProductCode = "P03", Grade = "C" },
                new() { ProductCode = "P04", Grade = "A", IsDeleted = true },
                new() { ProductCode = "P05", Grade = "a" },
                new() { ProductCode = "P12", Grade = "B" },
            }).ExecuteCommandAsync();

            await _db.Insertable(new List<ChinaSupplier>
            {
                new() { Guid = "CS1", SupplierCode = "HB246", SupplierName = "仕豪礼品", ShopNumber = "S-7109", Status = 1 },
                new() { Guid = "CS2", SupplierCode = "HB215", SupplierName = "Craft Co", ShopNumber = "B2", Status = 1 },
                new() { Guid = "CS3", SupplierCode = "HB999", SupplierName = "礼品停用", ShopNumber = "X", Status = 0 },
                new() { Guid = "CS4", SupplierCode = "HB888", SupplierName = "礼品删除", ShopNumber = "Y", Status = 1, IsDeleted = true },
                new() { Guid = "CS5", SupplierCode = "HB777", SupplierName = "条码供应商", ShopNumber = "Z-01", Status = 1 },
            }).ExecuteCommandAsync();
            await _db.Insertable(new List<DomesticProduct>
            {
                new() { ProductCode = "P01", SupplierCode = "HB246" },
                new() { ProductCode = "P02", SupplierCode = "HB246", IsDeleted = true },
                new() { ProductCode = "DP-HBNO", SupplierCode = "HB215", HBProductNo = "HB2X6-003" },
                new() { ProductCode = "DP-BARCODE", SupplierCode = "HB999", Barcode = "8058617785768" },
                new() { ProductCode = "DP-DELETED-SUPPLIER", SupplierCode = "HB888", Barcode = "8058617589083" },
                // 只能经条码关联到 P09 的国内商品，覆盖供应商关键字的条码匹配分支。
                new() { ProductCode = "DP-BARCODE-OK", SupplierCode = "HB777", Barcode = "8058617589083" },
            }).ExecuteCommandAsync();
        }

        public async Task SeedOrdersAsync()
        {
            async Task Order(string guid, string store, int flowStatus, DateTime? orderDate, DateTime createdAt, string? owner = null, bool deleted = false)
            {
                await _db.Insertable(new WareHouseOrder
                {
                    OrderGUID = guid,
                    StoreCode = store,
                    OrderNo = guid,
                    FlowStatus = flowStatus,
                    OrderDate = orderDate,
                    CreatedAt = createdAt,
                    CartOwnerUserGuid = owner,
                    IsDeleted = deleted,
                }).ExecuteCommandAsync();
            }

            async Task Line(string order, string product, decimal quantity, decimal alloc, bool deleted = false)
            {
                await _db.Insertable(new WareHouseOrderDetails
                {
                    OrderGUID = order,
                    StoreCode = "S1",
                    ProductCode = product,
                    Quantity = quantity,
                    AllocQuantity = alloc,
                    IsDeleted = deleted,
                }).ExecuteCommandAsync();
            }

            await Order("O1", "S1", 1, new DateTime(2026, 5, 1), new DateTime(2026, 5, 1, 8, 0, 0));
            await Line("O1", "P01", 2m, 1m);
            await Order("O2", "S1", 2, new DateTime(2026, 6, 1), new DateTime(2026, 6, 1, 9, 0, 0));
            await Line("O2", "P01", 3m, 1m);
            await Line("O2", "P01", 4m, 2m);
            await Line("O2", "P01", 100m, 100m, deleted: true);
            await Order("O3", "S1", 1, new DateTime(2026, 6, 1), new DateTime(2026, 6, 1, 7, 0, 0));
            await Line("O3", "P01", 9m, 9m);
            await Order("O4", "S1", 1, null, new DateTime(2026, 6, 2));
            await Line("O4", "P02", 5m, 4m);
            await Order("O5", "S1", 1, null, new DateTime(2026, 6, 3));
            await Line("O5", "P03", 1m, 1m);
            await Order("O6", "S1", 1, new DateTime(2026, 4, 1), new DateTime(2026, 4, 1));
            await Line("O6", "P03", 2m, 2m);
            await Order("O7", "S1", 1, new DateTime(2026, 7, 1), new DateTime(2026, 7, 1), deleted: true);
            await Line("O7", "P01", 50m, 50m);
            await Order("O8", "S2", 1, new DateTime(2026, 8, 1), new DateTime(2026, 8, 1));
            await Line("O8", "P01", 60m, 60m);
            await Order("C1", "S1", 0, null, new DateTime(2026, 6, 5));
            await Line("C1", "P01", 3m, 0m);
            await Line("C1", "P02", 1.5m, 0m);
            await Order("C2", "S1", 0, null, new DateTime(2026, 6, 6), owner: string.Empty);
            await Line("C2", "P01", 2m, 0m);
            await Order("C3", "S1", 0, null, new DateTime(2026, 6, 7), owner: "staff-1");
            await Line("C3", "P01", 10m, 0m);
        }

        private static WarehouseCategory Category(string guid, string? parent, string name, bool isDeleted = false) => new()
        {
            CategoryGUID = guid,
            ParentGUID = parent,
            CategoryName = name,
            IsDeleted = isDeleted,
        };

        private static string BuildConnectionString(string connectionString, string databaseName)
        {
            var builder = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = databaseName };
            return builder.ConnectionString;
        }

        private static void EnsureLoopbackSqlServer(string connectionString)
        {
            // 测试会创建并删除数据库，只允许指向本机环回地址的隔离 SQL Server。
            var dataSource = new SqlConnectionStringBuilder(connectionString).DataSource.Trim();
            if (dataSource.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase))
            {
                dataSource = dataSource[4..];
            }

            var host = dataSource.Split(',', 2, StringSplitOptions.TrimEntries)[0].Trim('[', ']');
            if (!host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                && !host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"{SqlServerTestConnectionEnvVar} 必须指向 localhost 或 127.0.0.1。"
                );
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
                """
            );
        }

        private static string QuoteSqlServerName(string name) =>
            $"[{name.Replace("]", "]]", StringComparison.Ordinal)}]";
    }

    /// <summary>货位解析替身：LOC-01 命中含小写、下架、不存在的编码；FB/hb 命中少量商品。</summary>
    private sealed class FixedLocationLookup(bool enabled) : IProductPickerLocationLookup
    {
        private static readonly Dictionary<string, string[]> Map = new(StringComparer.Ordinal)
        {
            [LocationIdentifier] = new[] { "P06", "p07", "P21", "NOT-EXIST" },
            ["FB"] = new[] { "P08" },
            ["hb"] = new[] { "P13", "p05" },
        };

        public bool IsEnabled => enabled;

        public Task<StoreOrderLocationProductLookupResult?> LookupAsync(
            string identifier,
            CancellationToken cancellationToken = default
        )
        {
            return Task.FromResult(
                Map.TryGetValue(identifier, out var codes)
                    ? new StoreOrderLocationProductLookupResult { MatchType = "locationCode", ProductCodes = codes }
                    : null
            );
        }
    }
}
