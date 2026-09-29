using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using AutoMapper;
using BlazorApp.Api.Data;
using BlazorApp.Api.Features.ProductWarehouse;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests
{
    public sealed class ProductWarehouseReactServiceTests : IDisposable
    {
        private readonly string _dbPath;
        private readonly SqliteConnection _sqliteConnection;
        private readonly SqlSugarClient _db;

        public ProductWarehouseReactServiceTests()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
            _sqliteConnection = new SqliteConnection($"Data Source={_dbPath}");
            _sqliteConnection.Open();

            _db = new SqlSugarClient(new ConnectionConfig
            {
                ConnectionString = _sqliteConnection.ConnectionString,
                DbType = DbType.Sqlite,
                IsAutoCloseConnection = false,
                InitKeyType = InitKeyType.Attribute,
                MoreSettings = new ConnMoreSettings(),
            });

            _db.CodeFirst.InitTables(
                typeof(Product),
                typeof(WarehouseProduct),
                typeof(DomesticProduct),
                typeof(DomesticSetProduct),
                typeof(ChinaSupplier),
                typeof(StoreMultiCodeProduct),
                typeof(Store),
                typeof(StoreRetailPrice),
                typeof(ProductLocation),
                typeof(Location),
                typeof(ProductGrade),
                typeof(WarehouseCategory),
                typeof(HBLocalSupplier),
                typeof(ProductSetCode),
                typeof(LocalSupplierCategory),
                typeof(LocalSupplierCategoryCapture),
                typeof(LocalSupplierCategoryProductAssignment)
            );
            _db.Ado.ExecuteCommand(
                """
                CREATE TABLE WarehouseProductChangeHistory (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    EventGuid TEXT NOT NULL,
                    ProductCode TEXT NOT NULL,
                    Action TEXT NOT NULL,
                    Source TEXT NOT NULL,
                    SourceReference TEXT NULL,
                    BatchGuid TEXT NULL,
                    ActorUserGuid TEXT NULL,
                    ActorName TEXT NOT NULL,
                    ActorType TEXT NOT NULL,
                    OccurredAtUtc TEXT NOT NULL,
                    ChangesJson TEXT NOT NULL
                )
                """
            );
        }

        [Fact]
        public void 套装成本写入路径_必须在产品锁内统一重算且初始成本为空()
        {
            var source = string.Join(
                "\n",
                Directory
                    .EnumerateFiles(
                        ResolveProductWarehouseFeatureRoot(),
                        "*.cs",
                        SearchOption.AllDirectories
                    )
                    .OrderBy(path => path, StringComparer.Ordinal)
                    .Select(File.ReadAllText)
            );
            var containerSource = File.ReadAllText(ResolveContainerExecutorPath());

            Assert.Contains("SetChildPurchasePriceMutationLock.Acquire", source);
            Assert.Contains(".RecalculateLockedAsync(", source);
            Assert.DoesNotContain(".RecalculateAsync(", source);
            Assert.DoesNotContain("SetChildPurchasePriceAllocator.AllocateByRetailRatio(", source);
            Assert.Contains("SetPurchasePrice = null", source);
            Assert.Contains("IsCostDerivedSetType(setCode.SetType)", source);
            Assert.Contains("setCode.SetType == 1 || setCode.SetType == 2", source);

            Assert.Contains("SetChildPurchasePriceMutationLock.Acquire", containerSource);
            Assert.Contains(".RecalculateLockedAsync(", containerSource);
            Assert.DoesNotContain(
                "SetChildPurchasePriceAllocator.AllocateByRetailRatio(",
                containerSource
            );
            Assert.Contains("SetPurchasePrice = null", containerSource);
            Assert.Contains("PurchasePrice = null", containerSource);
        }

        [Fact]
        public async Task GetAntdTableDataAsync_FiltersByCategoryGuidsIncludingChildren()
        {
            await SeedWarehouseCategoryAsync("cat-root", null, "根分类");
            await SeedWarehouseCategoryAsync("cat-child", "cat-root", "子分类");
            await SeedWarehouseTableProductAsync("P-CAT-ROOT", "ITEM-CAT-ROOT", "根分类商品", "cat-root");
            await SeedWarehouseTableProductAsync("P-CAT-CHILD", "ITEM-CAT-CHILD", "子分类商品", "cat-child");
            await SeedWarehouseTableProductAsync("P-CAT-OTHER", "ITEM-CAT-OTHER", "其他分类商品", null);

            var service = CreateService();

            var result = await service.GetAntdTableDataAsync(new ReactTableRequestDto
            {
                Page = 1,
                PageSize = 20,
                CategoryGuids = new List<string> { "cat-root" },
                IncludeSubCategories = true,
            });

            Assert.Equal(2, result.Total);
            Assert.Contains(result.Items, item => item.ProductCode == "P-CAT-ROOT");
            Assert.Contains(result.Items, item => item.ProductCode == "P-CAT-CHILD");
            Assert.DoesNotContain(result.Items, item => item.ProductCode == "P-CAT-OTHER");
        }

        [Fact]
        public async Task GetAntdTableDataAsync_FiltersUncategorizedOnly()
        {
            await SeedWarehouseCategoryAsync("cat-assigned", null, "已分类");
            await SeedWarehouseTableProductAsync("P-ASSIGNED", "ITEM-ASSIGNED", "已分类商品", "cat-assigned");
            await SeedWarehouseTableProductAsync("P-UNCATEGORIZED-NULL", "ITEM-UNCAT-NULL", "未分类空值商品", null);
            await SeedWarehouseTableProductAsync("P-UNCATEGORIZED-EMPTY", "ITEM-UNCAT-EMPTY", "未分类空字符串商品", string.Empty);

            var service = CreateService();

            var result = await service.GetAntdTableDataAsync(new ReactTableRequestDto
            {
                Page = 1,
                PageSize = 20,
                UncategorizedOnly = true,
            });

            Assert.Equal(2, result.Total);
            Assert.Contains(result.Items, item => item.ProductCode == "P-UNCATEGORIZED-NULL");
            Assert.Contains(result.Items, item => item.ProductCode == "P-UNCATEGORIZED-EMPTY");
            Assert.DoesNotContain(result.Items, item => item.ProductCode == "P-ASSIGNED");
        }

        [Fact]
        public async Task GetAntdTableDataAsync_CategoryGuidsTakePriorityOverUncategorizedOnly()
        {
            await SeedWarehouseCategoryAsync("cat-priority", null, "优先分类");
            await SeedWarehouseTableProductAsync("P-PRIORITY-CAT", "ITEM-PRIORITY-CAT", "分类优先商品", "cat-priority");
            await SeedWarehouseTableProductAsync("P-PRIORITY-UNCAT", "ITEM-PRIORITY-UNCAT", "未分类商品", null);

            var service = CreateService();

            var result = await service.GetAntdTableDataAsync(new ReactTableRequestDto
            {
                Page = 1,
                PageSize = 20,
                CategoryGuids = new List<string> { "cat-priority" },
                UncategorizedOnly = true,
            });

            Assert.Single(result.Items);
            Assert.Equal("P-PRIORITY-CAT", result.Items[0].ProductCode);
        }

        [Fact]
        public async Task GetAntdTableDataAsync_BlankCategoryGuidsDoNotOverrideUncategorizedOnly()
        {
            await SeedWarehouseCategoryAsync("cat-blank-priority", null, "已分类");
            await SeedWarehouseTableProductAsync("P-BLANK-CAT", "ITEM-BLANK-CAT", "已分类商品", "cat-blank-priority");
            await SeedWarehouseTableProductAsync("P-BLANK-UNCAT", "ITEM-BLANK-UNCAT", "未分类商品", null);

            var service = CreateService();

            var result = await service.GetAntdTableDataAsync(new ReactTableRequestDto
            {
                Page = 1,
                PageSize = 20,
                CategoryGuids = new List<string> { "", "   " },
                UncategorizedOnly = true,
            });

            var item = Assert.Single(result.Items);
            Assert.Equal("P-BLANK-UNCAT", item.ProductCode);
        }

        [Fact]
        public async Task GetAntdTableDataAsync_FiltersWarehouseTextAndLookupColumns()
        {
            await SeedWarehouseCategoryAsync("cat-filter-a", null, "厨房用品");
            await SeedWarehouseCategoryAsync("cat-filter-b", null, "卧室用品");
            await SeedWarehouseTableProductAsync(
                "P-FILTER-001",
                "ITEM-MUG-001",
                "大理石马克杯",
                "cat-filter-a",
                englishName: "Marble Mug",
                barcode: "BAR-MUG-001",
                supplierCode: "CN-001",
                supplierName: "义乌杯厂",
                localSupplierCode: "AU-001",
                localSupplierName: "Sydney Local Trading"
            );
            await SeedWarehouseTableProductAsync(
                "P-FILTER-002",
                "ITEM-LAMP-001",
                "北欧台灯",
                "cat-filter-b",
                englishName: "Nordic Lamp",
                barcode: "BAR-LAMP-002",
                supplierCode: "CN-002",
                supplierName: "义乌灯饰",
                localSupplierCode: "AU-002",
                localSupplierName: "Melbourne Supply"
            );

            var service = CreateService();

            var productNameResult = await service.GetAntdTableDataAsync(new ReactTableRequestDto
            {
                Page = 1,
                PageSize = 20,
                Filters = new Dictionary<string, string[]>
                {
                    ["productName"] = new[] { "__filter:contains:马克" },
                },
            });
            var barcodeResult = await service.GetAntdTableDataAsync(new ReactTableRequestDto
            {
                Page = 1,
                PageSize = 20,
                Filters = new Dictionary<string, string[]>
                {
                    ["barcode"] = new[] { "__filter:ends:002" },
                },
            });
            var itemNumberResult = await service.GetAntdTableDataAsync(new ReactTableRequestDto
            {
                Page = 1,
                PageSize = 20,
                Filters = new Dictionary<string, string[]>
                {
                    ["itemNumber"] = new[] { "__filter:starts:ITEM-MUG" },
                },
            });
            var nameEnResult = await service.GetAntdTableDataAsync(new ReactTableRequestDto
            {
                Page = 1,
                PageSize = 20,
                Filters = new Dictionary<string, string[]>
                {
                    ["nameEn"] = new[] { "__filter:eq:Nordic Lamp" },
                },
            });
            var supplierResult = await service.GetAntdTableDataAsync(new ReactTableRequestDto
            {
                Page = 1,
                PageSize = 20,
                Filters = new Dictionary<string, string[]>
                {
                    ["domesticSupplierCode"] = new[] { "CN-001" },
                    ["domesticSupplierName"] = new[] { "杯厂" },
                    ["localSupplierCode"] = new[] { "AU-001" },
                    ["localSupplierName"] = new[] { "Sydney" },
                    ["categoryName"] = new[] { "厨房" },
                },
            });

            var productNameItem = Assert.Single(productNameResult.Items);
            Assert.Equal("P-FILTER-001", productNameItem.ProductCode);

            var barcodeItem = Assert.Single(barcodeResult.Items);
            Assert.Equal("P-FILTER-002", barcodeItem.ProductCode);

            var itemNumberItem = Assert.Single(itemNumberResult.Items);
            Assert.Equal("P-FILTER-001", itemNumberItem.ProductCode);

            var nameEnItem = Assert.Single(nameEnResult.Items);
            Assert.Equal("P-FILTER-002", nameEnItem.ProductCode);

            var supplierItem = Assert.Single(supplierResult.Items);
            Assert.Equal("P-FILTER-001", supplierItem.ProductCode);
            Assert.Equal("Sydney Local Trading", supplierItem.LocalSupplierName);
        }

        [Fact]
        public async Task GetAntdTableDataAsync_TreatsLegacyPrefixedTextAsLiteralContains()
        {
            await SeedWarehouseTableProductAsync(
                "P-LEGACY-PREFIX-001",
                "ITEM-LEGACY-PREFIX-001",
                "eq:ABC 旧值商品",
                null
            );
            await SeedWarehouseTableProductAsync(
                "P-LEGACY-PREFIX-002",
                "ITEM-LEGACY-PREFIX-002",
                "ABC",
                null
            );

            var service = CreateService();

            var legacyResult = await service.GetAntdTableDataAsync(new ReactTableRequestDto
            {
                Page = 1,
                PageSize = 20,
                Filters = new Dictionary<string, string[]>
                {
                    ["productName"] = new[] { "eq:ABC" },
                },
            });
            var exactResult = await service.GetAntdTableDataAsync(new ReactTableRequestDto
            {
                Page = 1,
                PageSize = 20,
                Filters = new Dictionary<string, string[]>
                {
                    ["productName"] = new[] { "__filter:eq:ABC" },
                },
            });

            var legacyItem = Assert.Single(legacyResult.Items);
            Assert.Equal("P-LEGACY-PREFIX-001", legacyItem.ProductCode);

            var exactItem = Assert.Single(exactResult.Items);
            Assert.Equal("P-LEGACY-PREFIX-002", exactItem.ProductCode);
        }

        [Fact]
        public async Task GetAntdTableDataAsync_SupplierCodeFiltersUseExactMatch()
        {
            await SeedWarehouseTableProductAsync(
                "P-SUPPLIER-EXACT-001",
                "ITEM-SUPPLIER-EXACT-001",
                "供应商精确命中商品",
                null,
                supplierCode: "CN-001",
                supplierName: "义乌精确厂",
                localSupplierCode: "AU-001",
                localSupplierName: "Exact Local"
            );
            await SeedWarehouseTableProductAsync(
                "P-SUPPLIER-EXACT-002",
                "ITEM-SUPPLIER-EXACT-002",
                "供应商前缀碰撞商品",
                null,
                supplierCode: "CN-001A",
                supplierName: "义乌精确厂",
                localSupplierCode: "AU-001A",
                localSupplierName: "Exact Local Branch"
            );

            var service = CreateService();

            var domesticResult = await service.GetAntdTableDataAsync(new ReactTableRequestDto
            {
                Page = 1,
                PageSize = 20,
                Filters = new Dictionary<string, string[]>
                {
                    ["domesticSupplierCode"] = new[] { "CN-001" },
                },
            });
            var localResult = await service.GetAntdTableDataAsync(new ReactTableRequestDto
            {
                Page = 1,
                PageSize = 20,
                Filters = new Dictionary<string, string[]>
                {
                    ["localSupplierCode"] = new[] { "AU-001" },
                },
            });

            var domesticItem = Assert.Single(domesticResult.Items);
            Assert.Equal("P-SUPPLIER-EXACT-001", domesticItem.ProductCode);

            var localItem = Assert.Single(localResult.Items);
            Assert.Equal("P-SUPPLIER-EXACT-001", localItem.ProductCode);
        }

        [Fact]
        public async Task GetAntdTableDataAsync_ReturnsPickingLocationCodesOnly()
        {
            await SeedWarehouseTableProductAsync(
                "P-LOCATION-LIST-001",
                "ITEM-LOCATION-LIST-001",
                "货位列表商品",
                null
            );
            await SeedLocationAsync("loc-picking-list-002", "PICK-A-02", 1);
            await SeedLocationAsync("loc-picking-list-001", "PICK-A-01", 1);
            await SeedLocationAsync("loc-storage-list-001", "STOCK-A-99", 2);
            await SeedProductLocationAsync("P-LOCATION-LIST-001", "loc-picking-list-002");
            await SeedProductLocationAsync("P-LOCATION-LIST-001", "loc-picking-list-001");
            await SeedProductLocationAsync("P-LOCATION-LIST-001", "loc-storage-list-001");

            var service = CreateService();

            var result = await service.GetAntdTableDataAsync(new ReactTableRequestDto
            {
                Page = 1,
                PageSize = 20,
            });

            var item = Assert.Single(result.Items);
            Assert.Equal(1, result.Total);
            Assert.Equal(new[] { "PICK-A-01", "PICK-A-02" }, item.LocationCodes);
            Assert.DoesNotContain("STOCK-A-99", item.LocationCodes);
        }

        [Fact]
        public async Task GetAntdTableDataAsync_FiltersByPickingLocationCodeAndBarcode()
        {
            await SeedWarehouseTableProductAsync(
                "P-LOCATION-FILTER-001",
                "ITEM-LOCATION-FILTER-001",
                "货位筛选命中商品",
                null
            );
            await SeedWarehouseTableProductAsync(
                "P-LOCATION-FILTER-002",
                "ITEM-LOCATION-FILTER-002",
                "货位筛选未命中商品",
                null
            );
            await SeedLocationAsync("loc-picking-filter-001", "PICK-FILTER-01", 1);
            await SeedLocationAsync("loc-picking-filter-002", "PICK-FILTER-02", 1);
            await SeedLocationAsync("loc-storage-filter-001", "STOCK-FILTER-01", 2);
            await SeedProductLocationAsync("P-LOCATION-FILTER-001", "loc-picking-filter-001");
            await SeedProductLocationAsync("P-LOCATION-FILTER-002", "loc-picking-filter-002");
            await SeedProductLocationAsync("P-LOCATION-FILTER-002", "loc-storage-filter-001");

            var service = CreateService();

            var codeResult = await service.GetAntdTableDataAsync(new ReactTableRequestDto
            {
                Page = 1,
                PageSize = 20,
                Filters = new Dictionary<string, string[]>
                {
                    ["locationCodes"] = new[] { "__filter:eq:PICK-FILTER-01" },
                },
            });
            var barcodeResult = await service.GetAntdTableDataAsync(new ReactTableRequestDto
            {
                Page = 1,
                PageSize = 20,
                Filters = new Dictionary<string, string[]>
                {
                    ["locationCodes"] = new[] { "PICK-FILTER-02-BAR" },
                },
            });
            var storageResult = await service.GetAntdTableDataAsync(new ReactTableRequestDto
            {
                Page = 1,
                PageSize = 20,
                Filters = new Dictionary<string, string[]>
                {
                    ["locationCodes"] = new[] { "STOCK-FILTER-01" },
                },
            });

            Assert.Equal("P-LOCATION-FILTER-001", Assert.Single(codeResult.Items).ProductCode);
            Assert.Equal("P-LOCATION-FILTER-002", Assert.Single(barcodeResult.Items).ProductCode);
            Assert.Empty(storageResult.Items);
        }

        [Fact]
        public async Task GetAntdTableDataAsync_GlobalSearchMatchesPickingLocationCodeAndBarcode()
        {
            await SeedWarehouseTableProductAsync(
                "P-LOCATION-SEARCH-001",
                "ITEM-LOCATION-SEARCH-001",
                "货位搜索命中商品",
                null
            );
            await SeedWarehouseTableProductAsync(
                "P-LOCATION-SEARCH-002",
                "ITEM-LOCATION-SEARCH-002",
                "货位搜索未命中商品",
                null
            );
            await SeedLocationAsync("loc-picking-search-001", "PICK-SEARCH-01", 1);
            await SeedLocationAsync("loc-storage-search-001", "STOCK-SEARCH-01", 2);
            await SeedProductLocationAsync("P-LOCATION-SEARCH-001", "loc-picking-search-001");
            await SeedProductLocationAsync("P-LOCATION-SEARCH-002", "loc-storage-search-001");

            var service = CreateService();

            var codeResult = await service.GetAntdTableDataAsync(new ReactTableRequestDto
            {
                Page = 1,
                PageSize = 20,
                GlobalSearch = "PICK-SEARCH",
            });
            var barcodeResult = await service.GetAntdTableDataAsync(new ReactTableRequestDto
            {
                Page = 1,
                PageSize = 20,
                GlobalSearch = "PICK-SEARCH-01-BAR",
            });
            var storageResult = await service.GetAntdTableDataAsync(new ReactTableRequestDto
            {
                Page = 1,
                PageSize = 20,
                GlobalSearch = "STOCK-SEARCH",
            });

            Assert.Equal("P-LOCATION-SEARCH-001", Assert.Single(codeResult.Items).ProductCode);
            Assert.Equal("P-LOCATION-SEARCH-001", Assert.Single(barcodeResult.Items).ProductCode);
            Assert.Empty(storageResult.Items);
        }

        [Fact]
        public async Task GetAntdTableDataAsync_GlobalSearchMatchesCodeLikeProductFields()
        {
            await SeedWarehouseTableProductAsync(
                "P-GLOBAL-CODE-001",
                "HB246-BD-001",
                "代码型搜索命中商品",
                null,
                barcode: "9525812460744",
                supplierCode: "CN246-BD",
                supplierName: "代码供应商",
                localSupplierCode: "AU246-BD",
                localSupplierName: "Local Code Supplier"
            );
            await SeedWarehouseTableProductAsync(
                "P-GLOBAL-CODE-002",
                "HB999-ZZ-002",
                "代码型搜索未命中商品",
                null,
                barcode: "9525819999999",
                supplierCode: "CN999-ZZ",
                localSupplierCode: "AU999-ZZ"
            );
            var service = CreateService();
            var searches = new[]
            {
                "P-GLOBAL-CODE-001",
                "HB246-BD",
                "246-bd",
                "9525812460744",
                "CN246-BD",
                "AU246-BD",
            };

            foreach (var search in searches)
            {
                var result = await service.GetAntdTableDataAsync(new ReactTableRequestDto
                {
                    Page = 1,
                    PageSize = 20,
                    GlobalSearch = search,
                });

                Assert.Equal("P-GLOBAL-CODE-001", Assert.Single(result.Items).ProductCode);
            }

            var omittedHbPrefixResult = await service.GetAntdTableDataAsync(new ReactTableRequestDto
            {
                Page = 1,
                PageSize = 20,
                GlobalSearch = "246-bd",
            });

            // 派生 HB 前缀只用于 HB 货号，不扩散到条码、供应商或商品名称。
            Assert.Equal("P-GLOBAL-CODE-001", Assert.Single(omittedHbPrefixResult.Items).ProductCode);
        }

        [Fact]
        public async Task GetAntdTableDataAsync_CodeLikeGlobalSearchUsesUnionCandidatesAndSafePerfLog()
        {
            const string keyword = "MATCH-123";
            await SeedWarehouseTableProductAsync(
                "P-UNION-001",
                $"{keyword}-ITEM",
                "跨分支去重商品",
                null,
                barcode: $"{keyword}-BAR",
                supplierCode: $"{keyword}-SUP",
                localSupplierCode: $"{keyword}-LOCAL"
            );
            await SeedLocationAsync("loc-union-001", $"{keyword}-LOC", 1);
            await SeedProductLocationAsync("P-UNION-001", "loc-union-001");

            var executedSql = new List<string>();
            _db.Aop.OnLogExecuting = (sql, _) => executedSql.Add(sql);
            var logMessages = new List<string>();
            var logger = new Mock<ILogger<ProductWarehouseReactService>>();
            logger
                .Setup(x =>
                    x.Log(
                        It.IsAny<LogLevel>(),
                        It.IsAny<EventId>(),
                        It.Is<It.IsAnyType>((_, _) => true),
                        It.IsAny<Exception?>(),
                        It.IsAny<Func<It.IsAnyType, Exception?, string>>()
                    )
                )
                .Callback(
                    new InvocationAction(invocation =>
                        logMessages.Add(invocation.Arguments[2]?.ToString() ?? string.Empty)
                    )
                );

            try
            {
                var result = await CreateService(logger: logger.Object)
                    .GetAntdTableDataAsync(new ReactTableRequestDto
                    {
                        Page = 1,
                        PageSize = 20,
                        GlobalSearch = keyword,
                    });

                Assert.Equal(1, result.Total);
                Assert.Equal("P-UNION-001", Assert.Single(result.Items).ProductCode);
            }
            finally
            {
                _db.Aop.OnLogExecuting = null;
            }

            var countSql = Assert.Single(
                executedSql,
                sql => sql.Contains("COUNT", StringComparison.OrdinalIgnoreCase)
            );
            Assert.Contains("UNION", countSql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("EXISTS", countSql, StringComparison.OrdinalIgnoreCase);

            var perfLog = Assert.Single(
                logMessages,
                message => message.Contains("[warehouse-product-table-perf]")
            );
            Assert.Contains("candidateMs=", perfLog);
            Assert.Contains("countMs=", perfLog);
            Assert.Contains("pageMs=", perfLog);
            Assert.Contains("locationMs=", perfLog);
            Assert.Contains("rowsMs=", perfLog);
            Assert.Contains("mapMs=", perfLog);
            Assert.Contains("totalMs=", perfLog);
            Assert.DoesNotContain(keyword, perfLog, StringComparison.Ordinal);
        }

        [Fact]
        public void CodeLikeCandidateQuery_SqlServerUsesUnionAndVarcharParameters()
        {
            const string keyword = "MQ057-";
            var sqlServerDb = new SqlSugarClient(new ConnectionConfig
            {
                ConnectionString =
                    "Server=127.0.0.1;Database=warehouse_sql_generation;User Id=sa;Password=SqlOnly_123;TrustServerCertificate=True",
                DbType = DbType.SqlServer,
                IsAutoCloseConnection = true,
                InitKeyType = InitKeyType.Attribute,
                MoreSettings = new ConnMoreSettings(),
            });
            var service = CreateService(database: sqlServerDb);
            var method = typeof(ProductWarehouseReactService).GetMethod(
                "BuildWarehouseCodeSearchCandidateQuery",
                BindingFlags.Instance | BindingFlags.NonPublic
            );
            var candidateQuery = Assert.IsAssignableFrom<
                ISugarQueryable<WarehouseProductCodeSearchCandidate>
            >(method!.Invoke(service, new object[] { keyword }));

            var sql = sqlServerDb
                .Queryable<WarehouseProduct>()
                .InnerJoin(
                    candidateQuery,
                    (warehouseProduct, candidate) =>
                        warehouseProduct.ProductCode == candidate.ProductCode
                )
                .Select((warehouseProduct, candidate) => warehouseProduct)
                .MergeTable()
                .Where(warehouseProduct => !warehouseProduct.IsDeleted)
                .Select(warehouseProduct => warehouseProduct.ProductCode)
                .ToSql()
                .Key;

            Assert.Contains("UNION", sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("VARCHAR", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("EXISTS", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(keyword, sql, StringComparison.Ordinal);
        }

        [Fact]
        public void TextCandidateQuery_SqlServerBuildsUnionBeforeItemNumberPaging()
        {
            const string keyword = "Shredded";
            var sqlServerDb = new SqlSugarClient(new ConnectionConfig
            {
                ConnectionString =
                    "Server=127.0.0.1;Database=warehouse_sql_generation;User Id=sa;Password=SqlOnly_123;TrustServerCertificate=True",
                DbType = DbType.SqlServer,
                IsAutoCloseConnection = true,
                InitKeyType = InitKeyType.Attribute,
                MoreSettings = new ConnMoreSettings(),
            });
            var service = CreateService(database: sqlServerDb);
            var method = typeof(ProductWarehouseReactService).GetMethod(
                "BuildWarehouseTextSearchCandidateQuery",
                BindingFlags.Instance | BindingFlags.NonPublic
            );
            var candidateQuery = Assert.IsAssignableFrom<
                ISugarQueryable<WarehouseProductCodeSearchCandidate>
            >(method!.Invoke(service, new object[] { keyword }));

            var warehouseQuery = sqlServerDb
                .Queryable<WarehouseProduct>()
                .InnerJoin(
                    candidateQuery,
                    (warehouseProduct, candidate) =>
                        warehouseProduct.ProductCode == candidate.ProductCode
                )
                .Select((warehouseProduct, candidate) => warehouseProduct)
                .MergeTable();
            var sql = warehouseQuery
                .InnerJoin<Product>(
                    (warehouseProduct, product) =>
                        product.ProductCode == warehouseProduct.ProductCode && !product.IsDeleted
                )
                .Where(warehouseProduct => !warehouseProduct.IsDeleted)
                .OrderBy(
                    (warehouseProduct, product) => product.ItemNumber,
                    OrderByType.Asc
                )
                .Select((warehouseProduct, product) => warehouseProduct.ProductCode)
                .Skip(0)
                .Take(100)
                .ToSql()
                .Key;

            Assert.Contains("UNION", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("UNION ALL", sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("VARCHAR", sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("ROW_NUMBER", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("EXISTS", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(keyword, sql, StringComparison.Ordinal);
        }

        [Fact]
        public async Task GetAntdTableDataAsync_OmittedHbPrefixOnlyMatchesItemNumber()
        {
            await SeedWarehouseTableProductAsync(
                "P-OMITTED-HB-001",
                "HB246-BD-001",
                "省略 HB 前缀命中货号",
                null
            );
            await SeedWarehouseTableProductAsync(
                "P-OMITTED-HB-002",
                "ITEM-GLOBAL-HB-ONLY-002",
                "HB246-BD 干扰商品",
                null,
                barcode: "HB246-BD-BAR",
                supplierCode: "HB246-BD-SUP",
                localSupplierCode: "HB246-BD-LOCAL"
            );

            var service = CreateService();

            var result = await service.GetAntdTableDataAsync(new ReactTableRequestDto
            {
                Page = 1,
                PageSize = 20,
                GlobalSearch = "246-bd",
            });

            // 省略 HB 前缀只补到货号字段；其它代码列即使以 HB246-BD 开头也不应被派生命中。
            Assert.Equal("P-OMITTED-HB-001", Assert.Single(result.Items).ProductCode);
        }

        [Fact]
        public async Task GetAntdTableDataAsync_GlobalSearchTreatsCodeLikeKeywordAsCodeOnly()
        {
            await SeedWarehouseTableProductAsync(
                "P-GLOBAL-TEXT-001",
                "ITEM-GLOBAL-TEXT-001",
                "夜光珠 5.5MM",
                null
            );
            await SeedWarehouseTableProductAsync(
                "P-GLOBAL-TEXT-002",
                "ITEM-GLOBAL-TEXT-002",
                "普通圆珠",
                null
            );

            var service = CreateService();

            var result = await service.GetAntdTableDataAsync(new ReactTableRequestDto
            {
                Page = 1,
                PageSize = 20,
                GlobalSearch = "5.5",
            });

            Assert.Empty(result.Items);
        }

        [Fact]
        public async Task GetAntdTableDataAsync_GlobalSearchKeepsTextMatchForNameKeyword()
        {
            await SeedWarehouseTableProductAsync(
                "P-GLOBAL-NAME-001",
                "ITEM-GLOBAL-NAME-001",
                "夜光珠",
                null
            );
            await SeedWarehouseTableProductAsync(
                "P-GLOBAL-NAME-002",
                "ITEM-GLOBAL-NAME-002",
                "普通圆珠",
                null
            );

            var service = CreateService();

            var result = await service.GetAntdTableDataAsync(new ReactTableRequestDto
            {
                Page = 1,
                PageSize = 20,
                GlobalSearch = "夜光珠",
            });

            Assert.Equal("P-GLOBAL-NAME-001", Assert.Single(result.Items).ProductCode);
        }

        [Theory]
        [InlineData(
            "ascend",
            "HB038-010",
            "HB038-012",
            "HB038-014",
            "HB038-033"
        )]
        [InlineData(
            "descend",
            "HB038-033",
            "HB038-014",
            "HB038-012",
            "HB038-010"
        )]
        public async Task GetAntdTableDataAsync_TextGlobalSearchSortsItemNumbersBeforePagingWithCandidates(
            string sortOrder,
            string firstItemNumber,
            string secondItemNumber,
            string thirdItemNumber,
            string fourthItemNumber
        )
        {
            await SeedWarehouseTableProductAsync(
                "P-TEXT-SORT-014",
                "HB038-014",
                "Shredded Paper Mixed Pastel 50g",
                null
            );
            await SeedWarehouseTableProductAsync(
                "P-TEXT-SORT-012",
                "HB038-012",
                "Shredded Paper Mint 50g",
                null
            );
            await SeedWarehouseTableProductAsync(
                "P-TEXT-SORT-033",
                "HB038-033",
                "Shredded Paper Yellow 50g",
                null
            );
            await SeedWarehouseTableProductAsync(
                "P-TEXT-SORT-010",
                "HB038-010",
                "Shredded Paper Baby Blue 50g",
                null
            );
            await SeedWarehouseTableProductAsync(
                "P-TEXT-SORT-OTHER",
                "HB038-001",
                "Tissue Paper White 50g",
                null
            );

            var executedSql = new List<string>();
            _db.Aop.OnLogExecuting = (sql, _) => executedSql.Add(sql);
            ReactTableResponseDto<WarehouseProductReactListDto> firstPage;
            ReactTableResponseDto<WarehouseProductReactListDto> secondPage;
            try
            {
                var service = CreateService();
                firstPage = await service.GetAntdTableDataAsync(new ReactTableRequestDto
                {
                    Page = 1,
                    PageSize = 2,
                    GlobalSearch = "Shredded",
                    SortBy = "itemNumber",
                    SortOrder = sortOrder,
                });
                secondPage = await service.GetAntdTableDataAsync(new ReactTableRequestDto
                {
                    Page = 2,
                    PageSize = 2,
                    GlobalSearch = "Shredded",
                    SortBy = "itemNumber",
                    SortOrder = sortOrder,
                });
            }
            finally
            {
                _db.Aop.OnLogExecuting = null;
            }

            Assert.Equal(4, firstPage.Total);
            Assert.Equal(
                new[] { firstItemNumber, secondItemNumber },
                firstPage.Items.Select(item => item.ItemNumber)
            );
            Assert.Equal(4, secondPage.Total);
            Assert.Equal(
                new[] { thirdItemNumber, fourthItemNumber },
                secondPage.Items.Select(item => item.ItemNumber)
            );

            var pageQueries = executedSql
                .Where(sql =>
                    sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase)
                    && sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase)
                )
                .ToList();
            Assert.Equal(2, pageQueries.Count);
            Assert.All(pageQueries, sql =>
            {
                Assert.Contains("UNION", sql, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("EXISTS", sql, StringComparison.OrdinalIgnoreCase);
            });
        }

        [Fact]
        public async Task GetAntdTableDataAsync_TextGlobalSearchItemNumberSortKeepsLocationMatchesUnique()
        {
            await SeedWarehouseTableProductAsync(
                "P-TEXT-LOCATION-002",
                "HB-TEXT-002",
                "Aisle Basket",
                null
            );
            await SeedWarehouseTableProductAsync(
                "P-TEXT-LOCATION-001",
                "HB-TEXT-001",
                "Storage Basket",
                null
            );
            await SeedWarehouseTableProductAsync(
                "P-TEXT-LOCATION-STORAGE",
                "HB-TEXT-000",
                "Storage Shelf",
                null
            );
            await SeedLocationAsync("loc-text-picking-002", "AISLE-MATCH-02", 1);
            await SeedLocationAsync("loc-text-picking-001", "AISLE-MATCH-01", 1);
            await SeedLocationAsync("loc-text-storage", "AISLE-STORAGE", 2);
            await SeedProductLocationAsync("P-TEXT-LOCATION-002", "loc-text-picking-002");
            await SeedProductLocationAsync("P-TEXT-LOCATION-001", "loc-text-picking-001");
            await SeedProductLocationAsync(
                "P-TEXT-LOCATION-STORAGE",
                "loc-text-storage"
            );

            var result = await CreateService().GetAntdTableDataAsync(new ReactTableRequestDto
            {
                Page = 1,
                PageSize = 20,
                GlobalSearch = "Aisle",
                SortBy = "itemNumber",
                SortOrder = "ascend",
            });

            Assert.Equal(2, result.Total);
            Assert.Equal(
                new[] { "P-TEXT-LOCATION-001", "P-TEXT-LOCATION-002" },
                result.Items.Select(item => item.ProductCode)
            );
            Assert.Equal(2, result.Items.Select(item => item.ProductCode).Distinct().Count());
        }

        [Fact]
        public async Task GetAntdTableDataAsync_FiltersWarehouseStatusTypeAndRanges()
        {
            var baseUpdatedAt = new DateTime(2026, 6, 16, 10, 30, 0, DateTimeKind.Utc);
            await SeedWarehouseTableProductAsync(
                "P-RANGE-001",
                "ITEM-RANGE-001",
                "范围命中商品",
                null,
                supplierCode: "CN-010",
                supplierName: "义乌范围厂",
                localSupplierCode: "AU-010",
                localSupplierName: "Adelaide Local",
                productType: 2,
                isActive: true,
                domesticPrice: 12.50m,
                oemPrice: 25.80m,
                importPrice: 9.60m,
                minOrderQuantity: 6,
                packingQuantity: 24,
                volume: 0.45m,
                updatedAt: baseUpdatedAt
            );
            await SeedWarehouseTableProductAsync(
                "P-RANGE-002",
                "ITEM-RANGE-002",
                "范围未命中商品",
                null,
                supplierCode: "CN-011",
                supplierName: "义乌范围厂二",
                localSupplierCode: "AU-011",
                localSupplierName: "Perth Local",
                productType: 1,
                isActive: false,
                domesticPrice: 45.10m,
                oemPrice: 60.00m,
                importPrice: 30.00m,
                minOrderQuantity: 30,
                packingQuantity: 120,
                volume: 2.35m,
                updatedAt: baseUpdatedAt.AddDays(-10)
            );

            var service = CreateService();

            var result = await service.GetAntdTableDataAsync(new ReactTableRequestDto
            {
                Page = 1,
                PageSize = 20,
                Filters = new Dictionary<string, string[]>
                {
                    ["isActive"] = new[] { "true" },
                    ["productType"] = new[] { "2" },
                    ["minOrderQuantity"] = new[] { "gte:5", "lte:10" },
                    ["domesticPrice"] = new[] { "gte:10", "lte:15" },
                    ["oemPrice"] = new[] { "gte:20", "lte:30" },
                    ["importPrice"] = new[] { "gte:8", "lte:12" },
                    ["packingQuantity"] = new[] { "gte:20", "lte:30" },
                    ["volume"] = new[] { "gte:0.4", "lte:0.5" },
                    ["updatedAt"] = new[] { "gte:2026-06-15", "lte:2026-06-16" },
                },
            });

            var item = Assert.Single(result.Items);
            Assert.Equal("P-RANGE-001", item.ProductCode);
            Assert.Equal("Adelaide Local", item.LocalSupplierName);
        }

        [Fact]
        public async Task GetAntdTableDataAsync_FiltersWarehouseNumberColumnsByEqualToken()
        {
            await SeedWarehouseTableProductAsync(
                "P-NUM-EQ-001",
                "ITEM-NUM-EQ-001",
                "数值等于命中商品",
                null,
                supplierCode: "CN-NUM-EQ-001",
                domesticPrice: 12.50m,
                oemPrice: 25.80m,
                importPrice: 9.60m,
                minOrderQuantity: 6,
                packingQuantity: 24,
                volume: 0.45m
            );
            await SeedWarehouseTableProductAsync(
                "P-NUM-EQ-002",
                "ITEM-NUM-EQ-002",
                "数值等于未命中商品",
                null,
                supplierCode: "CN-NUM-EQ-002",
                domesticPrice: 13.50m,
                oemPrice: 26.80m,
                importPrice: 10.60m,
                minOrderQuantity: 8,
                packingQuantity: 36,
                volume: 0.60m
            );

            var service = CreateService();

            var result = await service.GetAntdTableDataAsync(new ReactTableRequestDto
            {
                Page = 1,
                PageSize = 20,
                Filters = new Dictionary<string, string[]>
                {
                    ["minOrderQuantity"] = new[] { "__filter:eq:6" },
                    ["domesticPrice"] = new[] { "__filter:eq:12.50" },
                    ["oemPrice"] = new[] { "__filter:eq:25.80" },
                    ["importPrice"] = new[] { "__filter:eq:9.60" },
                    ["packingQuantity"] = new[] { "__filter:eq:24" },
                    ["volume"] = new[] { "__filter:eq:0.45" },
                },
            });

            var item = Assert.Single(result.Items);
            Assert.Equal("P-NUM-EQ-001", item.ProductCode);
        }

        [Fact]
        public async Task GetAntdTableDataAsync_DomesticPriceUsesActiveDomesticProductAsNullFallback()
        {
            await SeedWarehouseTableProductAsync(
                "P-DOMESTIC-PRICE-FALLBACK",
                "ITEM-DOMESTIC-PRICE-FALLBACK",
                "国内价兜底商品",
                null,
                supplierCode: "CN-DOMESTIC-PRICE-FALLBACK",
                domesticProductPrice: 8.88m
            );
            await SeedWarehouseTableProductAsync(
                "P-DOMESTIC-PRICE-WAREHOUSE",
                "ITEM-DOMESTIC-PRICE-WAREHOUSE",
                "仓库国内价优先商品",
                null,
                supplierCode: "CN-DOMESTIC-PRICE-WAREHOUSE",
                domesticPrice: 4.44m,
                domesticProductPrice: 8.88m
            );
            await SeedWarehouseTableProductAsync(
                "P-DOMESTIC-PRICE-ZERO",
                "ITEM-DOMESTIC-PRICE-ZERO",
                "仓库零国内价商品",
                null,
                supplierCode: "CN-DOMESTIC-PRICE-ZERO",
                domesticPrice: 0m,
                domesticProductPrice: 8.88m
            );
            await SeedWarehouseTableProductAsync(
                "P-DOMESTIC-PRICE-NO-DOMESTIC",
                "ITEM-DOMESTIC-PRICE-NO-DOMESTIC",
                "无国内商品记录",
                null
            );
            await SeedWarehouseTableProductAsync(
                "P-DOMESTIC-PRICE-DELETED",
                "ITEM-DOMESTIC-PRICE-DELETED",
                "已删除国内商品记录",
                null,
                supplierCode: "CN-DOMESTIC-PRICE-DELETED",
                domesticProductPrice: 9.99m,
                domesticProductIsDeleted: true
            );

            var result = await CreateService().GetAntdTableDataAsync(new ReactTableRequestDto
            {
                Page = 1,
                PageSize = 20,
            });
            var items = result.Items.ToDictionary(item => item.ProductCode);

            Assert.Equal(8.88m, items["P-DOMESTIC-PRICE-FALLBACK"].DomesticPrice);
            Assert.Equal(4.44m, items["P-DOMESTIC-PRICE-WAREHOUSE"].DomesticPrice);
            Assert.Equal(0m, items["P-DOMESTIC-PRICE-ZERO"].DomesticPrice);
            Assert.Null(items["P-DOMESTIC-PRICE-NO-DOMESTIC"].DomesticPrice);
            Assert.Null(items["P-DOMESTIC-PRICE-DELETED"].DomesticPrice);
        }

        [Fact]
        public async Task GetAntdTableDataAsync_DomesticPriceFilterUsesEffectiveDisplayedPrice()
        {
            await SeedWarehouseTableProductAsync(
                "P-DOMESTIC-FILTER-FALLBACK",
                "ITEM-DOMESTIC-FILTER-FALLBACK",
                "国内价兜底筛选命中",
                null,
                supplierCode: "CN-DOMESTIC-FILTER-FALLBACK",
                domesticProductPrice: 12.50m
            );
            await SeedWarehouseTableProductAsync(
                "P-DOMESTIC-FILTER-COLLISION",
                "ITEM-DOMESTIC-FILTER-COLLISION",
                "仓库价覆盖国内价",
                null,
                supplierCode: "CN-DOMESTIC-FILTER-COLLISION",
                domesticPrice: 35m,
                domesticProductPrice: 12.50m
            );
            await SeedWarehouseTableProductAsync(
                "P-DOMESTIC-FILTER-WAREHOUSE",
                "ITEM-DOMESTIC-FILTER-WAREHOUSE",
                "仓库价筛选命中",
                null,
                supplierCode: "CN-DOMESTIC-FILTER-WAREHOUSE",
                domesticPrice: 13.50m,
                domesticProductPrice: 99m
            );
            await SeedWarehouseTableProductAsync(
                "P-DOMESTIC-FILTER-ZERO",
                "ITEM-DOMESTIC-FILTER-ZERO",
                "仓库零价筛选命中",
                null,
                supplierCode: "CN-DOMESTIC-FILTER-ZERO",
                domesticPrice: 0m,
                domesticProductPrice: 12.50m
            );

            var exactResult = await CreateService().GetAntdTableDataAsync(
                new ReactTableRequestDto
                {
                    Page = 1,
                    PageSize = 20,
                    Filters = new Dictionary<string, string[]>
                    {
                        ["domesticPrice"] = new[] { "__filter:eq:12.50" },
                    },
                }
            );
            var rangeResult = await CreateService().GetAntdTableDataAsync(
                new ReactTableRequestDto
                {
                    Page = 1,
                    PageSize = 20,
                    Filters = new Dictionary<string, string[]>
                    {
                        ["domesticPrice"] = new[] { "gte:10", "lte:15" },
                    },
                }
            );
            var zeroResult = await CreateService().GetAntdTableDataAsync(
                new ReactTableRequestDto
                {
                    Page = 1,
                    PageSize = 20,
                    Filters = new Dictionary<string, string[]>
                    {
                        ["domesticPrice"] = new[] { "__filter:eq:0" },
                    },
                }
            );

            Assert.Equal(
                "P-DOMESTIC-FILTER-FALLBACK",
                Assert.Single(exactResult.Items).ProductCode
            );
            Assert.Equal(
                new[] { "P-DOMESTIC-FILTER-FALLBACK", "P-DOMESTIC-FILTER-WAREHOUSE" },
                rangeResult.Items.Select(item => item.ProductCode).OrderBy(code => code)
            );
            Assert.Equal("P-DOMESTIC-FILTER-ZERO", Assert.Single(zeroResult.Items).ProductCode);
        }

        [Theory]
        [InlineData(
            "ascend",
            "P-DOMESTIC-SORT-ZERO",
            "P-DOMESTIC-SORT-WAREHOUSE",
            "P-DOMESTIC-SORT-FALLBACK-MID",
            "P-DOMESTIC-SORT-FALLBACK-HIGH"
        )]
        [InlineData(
            "descend",
            "P-DOMESTIC-SORT-FALLBACK-HIGH",
            "P-DOMESTIC-SORT-FALLBACK-MID",
            "P-DOMESTIC-SORT-WAREHOUSE",
            "P-DOMESTIC-SORT-ZERO"
        )]
        public async Task GetAntdTableDataAsync_SortsEffectiveDomesticPriceBeforePaging(
            string sortOrder,
            string firstProductCode,
            string secondProductCode,
            string thirdProductCode,
            string fourthProductCode
        )
        {
            await SeedWarehouseTableProductAsync(
                "P-DOMESTIC-SORT-ZERO",
                "ITEM-DOMESTIC-SORT-ZERO",
                "国内价排序零值",
                null,
                supplierCode: "CN-DOMESTIC-SORT-ZERO",
                domesticPrice: 0m,
                domesticProductPrice: 50m
            );
            await SeedWarehouseTableProductAsync(
                "P-DOMESTIC-SORT-WAREHOUSE",
                "ITEM-DOMESTIC-SORT-WAREHOUSE",
                "国内价排序仓库值",
                null,
                supplierCode: "CN-DOMESTIC-SORT-WAREHOUSE",
                domesticPrice: 10m,
                domesticProductPrice: 99m
            );
            await SeedWarehouseTableProductAsync(
                "P-DOMESTIC-SORT-FALLBACK-MID",
                "ITEM-DOMESTIC-SORT-FALLBACK-MID",
                "国内价排序兜底中值",
                null,
                supplierCode: "CN-DOMESTIC-SORT-FALLBACK-MID",
                domesticProductPrice: 20m
            );
            await SeedWarehouseTableProductAsync(
                "P-DOMESTIC-SORT-FALLBACK-HIGH",
                "ITEM-DOMESTIC-SORT-FALLBACK-HIGH",
                "国内价排序兜底高值",
                null,
                supplierCode: "CN-DOMESTIC-SORT-FALLBACK-HIGH",
                domesticProductPrice: 30m
            );

            var firstPage = await CreateService().GetAntdTableDataAsync(new ReactTableRequestDto
            {
                Page = 1,
                PageSize = 2,
                SortBy = "domesticPrice",
                SortOrder = sortOrder,
            });
            var secondPage = await CreateService().GetAntdTableDataAsync(new ReactTableRequestDto
            {
                Page = 2,
                PageSize = 2,
                SortBy = "domesticPrice",
                SortOrder = sortOrder,
            });

            Assert.Equal(4, firstPage.Total);
            Assert.Equal(
                new[] { firstProductCode, secondProductCode },
                firstPage.Items.Select(item => item.ProductCode)
            );
            Assert.Equal(4, secondPage.Total);
            Assert.Equal(
                new[] { thirdProductCode, fourthProductCode },
                secondPage.Items.Select(item => item.ProductCode)
            );
        }

        [Theory]
        [InlineData("ascend")]
        [InlineData("descend")]
        public async Task GetAntdTableDataAsync_DomesticPriceSortUsesStableProductCodeTieBreaker(
            string sortOrder
        )
        {
            await SeedWarehouseTableProductAsync(
                "P-DOMESTIC-TIE-D",
                "ITEM-DOMESTIC-TIE-D",
                "国内价同值排序 D",
                null,
                supplierCode: "CN-DOMESTIC-TIE-D",
                domesticPrice: 10m,
                domesticProductPrice: 99m
            );
            await SeedWarehouseTableProductAsync(
                "P-DOMESTIC-TIE-B",
                "ITEM-DOMESTIC-TIE-B",
                "国内价同值排序 B",
                null,
                supplierCode: "CN-DOMESTIC-TIE-B",
                domesticProductPrice: 10m
            );
            await SeedWarehouseTableProductAsync(
                "P-DOMESTIC-TIE-C",
                "ITEM-DOMESTIC-TIE-C",
                "国内价同值排序 C",
                null,
                supplierCode: "CN-DOMESTIC-TIE-C",
                domesticPrice: 10m,
                domesticProductPrice: 5m
            );
            await SeedWarehouseTableProductAsync(
                "P-DOMESTIC-TIE-A",
                "ITEM-DOMESTIC-TIE-A",
                "国内价同值排序 A",
                null,
                supplierCode: "CN-DOMESTIC-TIE-A",
                domesticProductPrice: 10m
            );

            var executedSql = new List<string>();
            _db.Aop.OnLogExecuting = (sql, _) => executedSql.Add(sql);
            ReactTableResponseDto<WarehouseProductReactListDto> firstPage;
            ReactTableResponseDto<WarehouseProductReactListDto> secondPage;
            try
            {
                var service = CreateService();
                firstPage = await service.GetAntdTableDataAsync(new ReactTableRequestDto
                {
                    Page = 1,
                    PageSize = 2,
                    SortBy = "domesticPrice",
                    SortOrder = sortOrder,
                });
                secondPage = await service.GetAntdTableDataAsync(new ReactTableRequestDto
                {
                    Page = 2,
                    PageSize = 2,
                    SortBy = "domesticPrice",
                    SortOrder = sortOrder,
                });
            }
            finally
            {
                _db.Aop.OnLogExecuting = null;
            }

            Assert.Equal(4, firstPage.Total);
            Assert.Equal(
                new[] { "P-DOMESTIC-TIE-A", "P-DOMESTIC-TIE-B" },
                firstPage.Items.Select(item => item.ProductCode)
            );
            Assert.Equal(4, secondPage.Total);
            Assert.Equal(
                new[] { "P-DOMESTIC-TIE-C", "P-DOMESTIC-TIE-D" },
                secondPage.Items.Select(item => item.ProductCode)
            );

            var pageQueries = executedSql
                .Where(sql =>
                    sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase)
                    && sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase)
                )
                .ToList();
            Assert.Equal(2, pageQueries.Count);
            Assert.All(
                pageQueries,
                sql =>
                {
                    var orderByIndex = sql.IndexOf("ORDER BY", StringComparison.OrdinalIgnoreCase);
                    var orderByClause = sql[orderByIndex..];
                    Assert.Contains(
                        "ProductCode",
                        orderByClause,
                        StringComparison.OrdinalIgnoreCase
                    );
                }
            );
        }

        [Fact]
        public async Task GetAntdTableDataAsync_FiltersUpdatedAtByEqualDateToken()
        {
            await SeedWarehouseTableProductAsync(
                "P-DATE-EQ-001",
                "ITEM-DATE-EQ-001",
                "日期等于命中商品",
                null,
                updatedAt: new DateTime(2026, 6, 16, 10, 30, 0, DateTimeKind.Utc)
            );
            await SeedWarehouseTableProductAsync(
                "P-DATE-EQ-002",
                "ITEM-DATE-EQ-002",
                "日期等于未命中商品",
                null,
                updatedAt: new DateTime(2026, 6, 15, 23, 59, 0, DateTimeKind.Utc)
            );

            var service = CreateService();

            var result = await service.GetAntdTableDataAsync(new ReactTableRequestDto
            {
                Page = 1,
                PageSize = 20,
                Filters = new Dictionary<string, string[]>
                {
                    ["updatedAt"] = new[] { "__filter:eq:2026-06-16" },
                },
            });

            var item = Assert.Single(result.Items);
            Assert.Equal("P-DATE-EQ-001", item.ProductCode);
        }

        [Fact]
        public async Task GetAntdTableDataAsync_PackingQuantityFilterMatchesDisplayedDomesticValue()
        {
            await SeedWarehouseTableProductAsync(
                "P-PACK-VISIBLE-001",
                "ITEM-PACK-VISIBLE-001",
                "装箱数显示命中商品",
                null,
                supplierCode: "CN-PACK-001",
                packingQuantity: 24,
                warehousePackingQuantity: 120
            );
            await SeedWarehouseTableProductAsync(
                "P-PACK-VISIBLE-002",
                "ITEM-PACK-VISIBLE-002",
                "装箱数仓库值碰撞商品",
                null,
                supplierCode: "CN-PACK-002",
                packingQuantity: 120,
                warehousePackingQuantity: 24
            );

            var service = CreateService();

            var result = await service.GetAntdTableDataAsync(new ReactTableRequestDto
            {
                Page = 1,
                PageSize = 20,
                Filters = new Dictionary<string, string[]>
                {
                    ["packingQty"] = new[] { "gte:20", "lte:30" },
                },
            });

            var item = Assert.Single(result.Items);
            Assert.Equal("P-PACK-VISIBLE-001", item.ProductCode);
            Assert.Equal(24, item.PackingQuantity);
            Assert.False(item.IsPackingQuantityFallback);
        }

        [Fact]
        public async Task GetAntdTableDataAsync_PackingQuantityFallsBackToWarehouseValue()
        {
            await SeedWarehouseTableProductAsync(
                "P-PACK-FALLBACK-001",
                "ITEM-PACK-FALLBACK-001",
                "装箱数仓库回退商品",
                null,
                warehousePackingQuantity: 36
            );

            var service = CreateService();

            var result = await service.GetAntdTableDataAsync(new ReactTableRequestDto
            {
                Page = 1,
                PageSize = 20,
                Filters = new Dictionary<string, string[]>
                {
                    ["packingQty"] = new[] { "gte:36", "lte:36" },
                },
            });

            var item = Assert.Single(result.Items);
            Assert.Equal(36, item.PackingQuantity);
            Assert.True(item.IsPackingQuantityFallback);
        }

        [Fact]
        public async Task GetAntdTableDataAsync_NumberFilter_RangeAndExactUseOrSemantics()
        {
            await SeedWarehouseTableProductAsync(
                "P-MIX-001",
                "ITEM-MIX-001",
                "范围命中商品",
                null,
                domesticPrice: 12.5m
            );
            await SeedWarehouseTableProductAsync(
                "P-MIX-002",
                "ITEM-MIX-002",
                "精确命中商品",
                null,
                domesticPrice: 25m
            );
            await SeedWarehouseTableProductAsync(
                "P-MIX-003",
                "ITEM-MIX-003",
                "不命中商品",
                null,
                domesticPrice: 18m
            );

            var service = CreateService();

            var result = await service.GetAntdTableDataAsync(new ReactTableRequestDto
            {
                Page = 1,
                PageSize = 20,
                Filters = new Dictionary<string, string[]>
                {
                    ["domesticPrice"] = new[] { "gte:10", "lte:15", "25" },
                },
            });

            Assert.Equal(2, result.Total);
            Assert.Contains(result.Items, item => item.ProductCode == "P-MIX-001");
            Assert.Contains(result.Items, item => item.ProductCode == "P-MIX-002");
            Assert.DoesNotContain(result.Items, item => item.ProductCode == "P-MIX-003");
        }

        [Fact]
        public async Task GetAntdTableDataAsync_IntNumberFilter_RangeAndExactUseOrSemantics()
        {
            await SeedWarehouseTableProductAsync(
                "P-INT-MIX-001",
                "ITEM-INT-MIX-001",
                "整数范围命中商品",
                null,
                minOrderQuantity: 12
            );
            await SeedWarehouseTableProductAsync(
                "P-INT-MIX-002",
                "ITEM-INT-MIX-002",
                "整数精确命中商品",
                null,
                minOrderQuantity: 25
            );
            await SeedWarehouseTableProductAsync(
                "P-INT-MIX-003",
                "ITEM-INT-MIX-003",
                "整数不命中商品",
                null,
                minOrderQuantity: 18
            );

            var service = CreateService();

            var result = await service.GetAntdTableDataAsync(new ReactTableRequestDto
            {
                Page = 1,
                PageSize = 20,
                Filters = new Dictionary<string, string[]>
                {
                    ["minOrderQuantity"] = new[] { "gte:10", "lte:15", "25" },
                },
            });

            Assert.Equal(2, result.Total);
            Assert.Contains(result.Items, item => item.ProductCode == "P-INT-MIX-001");
            Assert.Contains(result.Items, item => item.ProductCode == "P-INT-MIX-002");
            Assert.DoesNotContain(result.Items, item => item.ProductCode == "P-INT-MIX-003");
        }

        [Fact]
        public async Task GetAntdTableDataAsync_LocalSupplierNameFallsBackToCodeWhenLookupMissing()
        {
            await SeedWarehouseTableProductAsync(
                "P-LOCAL-FALLBACK",
                "ITEM-LOCAL-FALLBACK",
                "本地供应商回退商品",
                null,
                localSupplierCode: "AU-FALLBACK",
                seedLocalSupplierRow: false
            );
            await SeedWarehouseTableProductAsync(
                "P-LOCAL-DELETED",
                "ITEM-LOCAL-DELETED",
                "本地供应商软删商品",
                null,
                localSupplierCode: "AU-DELETED",
                localSupplierName: "Deleted Supplier",
                localSupplierIsDeleted: true
            );

            var service = CreateService();

            var fallbackResult = await service.GetAntdTableDataAsync(new ReactTableRequestDto
            {
                Page = 1,
                PageSize = 20,
                Filters = new Dictionary<string, string[]>
                {
                    ["itemNumber"] = new[] { "LOCAL-FALLBACK" },
                },
            });
            var deletedResult = await service.GetAntdTableDataAsync(new ReactTableRequestDto
            {
                Page = 1,
                PageSize = 20,
                Filters = new Dictionary<string, string[]>
                {
                    ["itemNumber"] = new[] { "LOCAL-DELETED" },
                },
            });

            var fallbackItem = Assert.Single(fallbackResult.Items);
            Assert.Equal("AU-FALLBACK", fallbackItem.LocalSupplierName);

            var deletedItem = Assert.Single(deletedResult.Items);
            Assert.Equal("AU-DELETED", deletedItem.LocalSupplierName);
        }

        [Fact]
        public async Task UpdatedBy_列表投影返回仓库商品更新人()
        {
            await SeedWarehouseTableProductAsync(
                "P-UPDATED-BY-LIST",
                "ITEM-UPDATED-BY-LIST",
                "更新人列表商品",
                null,
                updatedBy: "仓库员A"
            );

            var result = await CreateService().GetAntdTableDataAsync(new ReactTableRequestDto
            {
                Page = 1,
                PageSize = 20,
            });

            var item = Assert.Single(result.Items);
            Assert.Equal("仓库员A", item.UpdatedBy);
        }

        [Fact]
        public async Task UpdatedBy_列表投影历史记录没有更新人时保持为空()
        {
            await SeedWarehouseTableProductAsync(
                "P-CREATED-BY-LIST",
                "ITEM-CREATED-BY-LIST",
                "历史审计商品",
                null,
                createdBy: "历史仓库员",
                updatedBy: "   "
            );

            var result = await CreateService().GetAntdTableDataAsync(new ReactTableRequestDto
            {
                Page = 1,
                PageSize = 20,
            });

            Assert.True(string.IsNullOrWhiteSpace(Assert.Single(result.Items).UpdatedBy));
        }

        [Theory]
        [InlineData(
            "oemPrice",
            "ascend",
            "P-WAREHOUSE-PRICE-LOW",
            "P-WAREHOUSE-PRICE-MID",
            "P-WAREHOUSE-PRICE-HIGH"
        )]
        [InlineData(
            "oemPrice",
            "descend",
            "P-WAREHOUSE-PRICE-HIGH",
            "P-WAREHOUSE-PRICE-MID",
            "P-WAREHOUSE-PRICE-LOW"
        )]
        [InlineData(
            "importPrice",
            "ascend",
            "P-WAREHOUSE-PRICE-HIGH",
            "P-WAREHOUSE-PRICE-MID",
            "P-WAREHOUSE-PRICE-LOW"
        )]
        [InlineData(
            "importPrice",
            "descend",
            "P-WAREHOUSE-PRICE-LOW",
            "P-WAREHOUSE-PRICE-MID",
            "P-WAREHOUSE-PRICE-HIGH"
        )]
        public async Task GetAntdTableDataAsync_SortsWarehousePriceBeforePaging(
            string sortBy,
            string sortOrder,
            string firstProductCode,
            string secondProductCode,
            string thirdProductCode
        )
        {
            await SeedWarehouseTableProductAsync(
                "P-WAREHOUSE-PRICE-LOW",
                "ITEM-WAREHOUSE-PRICE-LOW",
                "仓库价格低",
                null,
                oemPrice: 10m,
                importPrice: 30m
            );
            await SeedWarehouseTableProductAsync(
                "P-WAREHOUSE-PRICE-MID",
                "ITEM-WAREHOUSE-PRICE-MID",
                "仓库价格中",
                null,
                oemPrice: 20m,
                importPrice: 20m
            );
            await SeedWarehouseTableProductAsync(
                "P-WAREHOUSE-PRICE-HIGH",
                "ITEM-WAREHOUSE-PRICE-HIGH",
                "仓库价格高",
                null,
                oemPrice: 30m,
                importPrice: 10m
            );

            var firstPage = await CreateService().GetAntdTableDataAsync(new ReactTableRequestDto
            {
                Page = 1,
                PageSize = 2,
                SortBy = sortBy,
                SortOrder = sortOrder,
            });
            var secondPage = await CreateService().GetAntdTableDataAsync(new ReactTableRequestDto
            {
                Page = 2,
                PageSize = 2,
                SortBy = sortBy,
                SortOrder = sortOrder,
            });

            Assert.Equal(3, firstPage.Total);
            Assert.Equal(
                new[] { firstProductCode, secondProductCode },
                firstPage.Items.Select(item => item.ProductCode)
            );
            Assert.Equal(3, secondPage.Total);
            Assert.Equal(thirdProductCode, Assert.Single(secondPage.Items).ProductCode);
        }

        [Fact]
        public async Task UpdatedBy_批量修改写入传入操作人且缺失时回退System()
        {
            await SeedPriceSyncProductAsync(
                "P-UPDATED-BY-BATCH",
                purchasePrice: 4.28m,
                retailPrice: 11.99m,
                importPrice: 4.28m,
                oemPrice: 11.99m
            );
            var service = CreateService();

            await service.BatchUpdateAsync(
                new List<UpdateItemDto>
                {
                    new() { ProductCode = "P-UPDATED-BY-BATCH", DomesticPrice = 8.88m },
                },
                "仓库员B"
            );
            var afterUserUpdate = await _db.Queryable<WarehouseProduct>()
                .SingleAsync(x => x.ProductCode == "P-UPDATED-BY-BATCH");

            await service.BatchUpdateAsync(
                new List<UpdateItemDto>
                {
                    new() { ProductCode = "P-UPDATED-BY-BATCH", OEMPrice = 9.99m },
                }
            );
            var afterFallbackUpdate = await _db.Queryable<WarehouseProduct>()
                .SingleAsync(x => x.ProductCode == "P-UPDATED-BY-BATCH");

            Assert.Equal("仓库员B", afterUserUpdate.UpdatedBy);
            Assert.Equal("System", afterFallbackUpdate.UpdatedBy);
        }

        [Fact]
        public async Task UpdatedBy_批量修改新建仓库商品同时写入创建人与更新人()
        {
            const string productCode = "P-UPDATED-BY-BATCH-NEW";
            await _db.Insertable(new Product
            {
                UUID = $"uuid-{productCode}",
                ProductCode = productCode,
                ItemNumber = "ITEM-UPDATED-BY-BATCH-NEW",
                ProductName = "批量修改新建审计商品",
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();

            var result = await CreateService().BatchUpdateAsync(
                new List<UpdateItemDto>
                {
                    new() { ProductCode = productCode, DomesticPrice = 8.88m },
                },
                "仓库员B2"
            );
            var warehouseProduct = await _db.Queryable<WarehouseProduct>()
                .SingleAsync(x => x.ProductCode == productCode);

            Assert.True(result.Success);
            Assert.Equal("仓库员B2", warehouseProduct.CreatedBy);
            Assert.Equal("仓库员B2", warehouseProduct.UpdatedBy);
        }

        [Fact]
        public async Task UpdatedBy_单个新建仓库商品同时写入创建人与更新人()
        {
            const string productCode = "P-UPDATED-BY-CREATE-SINGLE";
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:DefaultConnection"] = _sqliteConnection.ConnectionString,
                    }
                )
                .Build();

            var result = await CreateService(configuration: configuration).CreateSingleProductAsync(
                new CreateSingleProductRequestDto
                {
                    ProductCode = productCode,
                    ItemNumber = "ITEM-UPDATED-BY-CREATE-SINGLE",
                    Barcode = "BAR-UPDATED-BY-CREATE-SINGLE",
                    ChineseName = "单个新建审计商品",
                    OEMPrice = 9.99m,
                    ImportPrice = 4.28m,
                },
                "仓库员C2"
            );
            var warehouseProduct = await _db.Queryable<WarehouseProduct>()
                .SingleAsync(x => x.ProductCode == productCode);

            Assert.True(result.Success, result.Message);
            Assert.Equal("仓库员C2", warehouseProduct.CreatedBy);
            Assert.Equal("仓库员C2", warehouseProduct.UpdatedBy);
        }

        [Fact]
        public async Task CreateSingleProductAsync_套装创建后统一分摊总部和门店子项成本()
        {
            const string productCode = "P-CREATE-SET-COST";
            await SeedStoreAsync("S01", isActive: true, isDeleted: false);
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:DefaultConnection"] = _sqliteConnection.ConnectionString,
                    }
                )
                .Build();

            var result = await CreateService(configuration: configuration).CreateSingleProductAsync(
                new CreateSingleProductRequestDto
                {
                    ProductType = ProductTypeEnum.Set,
                    ProductCode = productCode,
                    ItemNumber = "ITEM-CREATE-SET-COST",
                    Barcode = "BAR-CREATE-SET-COST",
                    ChineseName = "创建套装成本测试",
                    OEMPrice = 50m,
                    ImportPrice = 10m,
                    SetType = SetTypeEnum.Combination,
                    SetItems = new List<SetItemDto>
                    {
                        new()
                        {
                            ProductCode = "SET-CREATE-A",
                            ItemNumber = "ITEM-A",
                            Barcode = "BAR-A",
                            Quantity = 9,
                            PurchasePrice = 99m,
                            RetailPrice = 20m,
                        },
                        new()
                        {
                            ProductCode = "SET-CREATE-B",
                            ItemNumber = "ITEM-B",
                            Barcode = "BAR-B",
                            Quantity = 1,
                            PurchasePrice = 99m,
                            RetailPrice = 30m,
                        },
                    },
                },
                "创建人"
            );

            Assert.True(result.Success, result.Message);
            var setRows = await _db.Queryable<ProductSetCode>()
                .Where(x => x.ProductCode == productCode)
                .OrderBy(x => x.SetRetailPrice)
                .ToListAsync();
            Assert.Equal(new decimal?[] { 4m, 6m }, setRows.Select(x => x.SetPurchasePrice));

            var storeRows = await _db.Queryable<StoreMultiCodeProduct>()
                .Where(x => x.ProductCode == productCode && x.StoreCode == "S01")
                .OrderBy(x => x.MultiCodeRetailPrice)
                .ToListAsync();
            Assert.Equal(new decimal?[] { 4m, 6m }, storeRows.Select(x => x.PurchasePrice));
            Assert.Equal(new decimal?[] { 20m, 30m }, storeRows.Select(x => x.MultiCodeRetailPrice));
        }

        [Fact]
        public async Task CreateSingleProductAsync_Type2套装忽略客户端子项成本并同步总部和门店主成本()
        {
            const string productCode = "P-CREATE-TYPE2-SET-COST";
            await SeedStoreAsync("S01", isActive: true, isDeleted: false);
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:DefaultConnection"] = _sqliteConnection.ConnectionString,
                    }
                )
                .Build();

            var result = await CreateService(configuration: configuration).CreateSingleProductAsync(
                new CreateSingleProductRequestDto
                {
                    ProductType = ProductTypeEnum.Set,
                    ProductCode = productCode,
                    ItemNumber = "ITEM-CREATE-TYPE2-SET-COST",
                    Barcode = "BAR-CREATE-TYPE2-SET-COST",
                    ChineseName = "固定套装成本测试",
                    OEMPrice = 50m,
                    ImportPrice = 10m,
                    SetType = SetTypeEnum.Fixed,
                    SetItems = new List<SetItemDto>
                    {
                        new()
                        {
                            ProductCode = "SET-TYPE2-A",
                            ItemNumber = "ITEM-TYPE2-A",
                            Barcode = "BAR-TYPE2-A",
                            Quantity = 1,
                            PurchasePrice = 99m,
                            RetailPrice = 20m,
                        },
                        new()
                        {
                            ProductCode = "SET-TYPE2-B",
                            ItemNumber = "ITEM-TYPE2-B",
                            Barcode = "BAR-TYPE2-B",
                            Quantity = 1,
                            PurchasePrice = 88m,
                            RetailPrice = 30m,
                        },
                    },
                },
                "创建人"
            );

            Assert.True(result.Success, result.Message);
            var setRows = await _db.Queryable<ProductSetCode>()
                .Where(x => x.ProductCode == productCode)
                .OrderBy(x => x.SetRetailPrice)
                .ToListAsync();
            Assert.Equal(new decimal?[] { 10m, 10m }, setRows.Select(x => x.SetPurchasePrice));
            Assert.Equal(new decimal?[] { 20m, 30m }, setRows.Select(x => x.SetRetailPrice));

            var storeRows = await _db.Queryable<StoreMultiCodeProduct>()
                .Where(x => x.ProductCode == productCode && x.StoreCode == "S01")
                .OrderBy(x => x.MultiCodeRetailPrice)
                .ToListAsync();
            Assert.Equal(new decimal?[] { 10m, 10m }, storeRows.Select(x => x.PurchasePrice));
            Assert.Equal(new decimal?[] { 20m, 30m }, storeRows.Select(x => x.MultiCodeRetailPrice));
        }

        [Fact]
        public async Task BatchUpdateAsync_Type2主成本更新同步总部和门店子项成本()
        {
            const string productCode = "P-BATCH-TYPE2-COST";
            await SeedPriceSyncProductAsync(productCode, 4m, 20m, 4m, 20m);
            await SeedStoreAsync("S01", isActive: true, isDeleted: false);
            await SeedStoreRetailPriceAsync("S01", productCode, purchasePrice: 4m, retailPrice: 20m);
            await _db.Insertable(new ProductSetCode
            {
                SetCodeId = "TYPE2-BATCH-SET",
                ProductCode = productCode,
                SetProductCode = "TYPE2-BATCH-CHILD",
                SetItemNumber = "TYPE2-BATCH-ITEM",
                SetBarcode = "TYPE2-BATCH-BARCODE",
                SetPurchasePrice = 4m,
                SetRetailPrice = 31m,
                SetType = 2,
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();
            await _db.Insertable(new StoreMultiCodeProduct
            {
                UUID = "TYPE2-BATCH-STORE",
                StoreCode = "S01",
                ProductCode = productCode,
                MultiCodeProductCode = "TYPE2-BATCH-CHILD",
                StoreMultiCodeProductCode = "S01TYPE2-BATCH-CHILD",
                MultiBarcode = "TYPE2-BATCH-BARCODE",
                PurchasePrice = 4m,
                MultiCodeRetailPrice = 31m,
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();

            var result = await CreateService().BatchUpdateAsync(
                new List<UpdateItemDto> { new() { ProductCode = productCode, ImportPrice = 10m } },
                "仓库员-Type2"
            );

            Assert.True(result.Success, result.Message);
            Assert.Equal(10m, (await _db.Queryable<Product>()
                .SingleAsync(x => x.ProductCode == productCode)).PurchasePrice);
            Assert.Equal(10m, (await _db.Queryable<StoreRetailPrice>()
                .SingleAsync(x => x.ProductCode == productCode && x.StoreCode == "S01")).PurchasePrice);
            Assert.Equal(10m, (await _db.Queryable<ProductSetCode>()
                .SingleAsync(x => x.ProductCode == productCode)).SetPurchasePrice);
            Assert.Equal(10m, (await _db.Queryable<StoreMultiCodeProduct>()
                .SingleAsync(x => x.ProductCode == productCode && x.StoreCode == "S01")).PurchasePrice);
        }

        [Fact]
        public async Task BatchUpdateAsync_Type2成本已正确时不新增审计记录()
        {
            const string productCode = "P-BATCH-TYPE2-AUDIT";
            await SeedPriceSyncProductAsync(productCode, 10m, 20m, 10m, 20m);
            await _db.Insertable(new ProductSetCode
            {
                SetCodeId = "TYPE2-AUDIT-SET",
                ProductCode = productCode,
                SetProductCode = "TYPE2-AUDIT-CHILD",
                SetItemNumber = "TYPE2-AUDIT-ITEM",
                SetBarcode = "TYPE2-AUDIT-BARCODE",
                SetPurchasePrice = 10m,
                SetRetailPrice = 31m,
                SetType = 2,
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();

            var result = await CreateService(
                changeHistoryService: CreateRealChangeHistoryService(
                    userGuid: "type2-audit-user",
                    username: "type2-audit-user"
                )
            ).BatchUpdateAsync(
                new List<UpdateItemDto> { new() { ProductCode = productCode, ImportPrice = 10m } },
                "仓库员-Type2"
            );

            Assert.True(result.Success, result.Message);
            Assert.Equal(0, await _db.Queryable<WarehouseProductChangeHistory>()
                .Where(x => x.ProductCode == productCode)
                .CountAsync());
            Assert.Equal(10m, (await _db.Queryable<ProductSetCode>()
                .SingleAsync(x => x.ProductCode == productCode)).SetPurchasePrice);
        }

        [Fact]
        public async Task UpdatedBy_完整编辑写入传入操作人()
        {
            const string productCode = "P-UPDATED-BY-FULL";
            await SeedPriceSyncProductAsync(productCode, 4.28m, 11.99m, 4.28m, 11.99m);

            var result = await CreateService().FullUpdateAsync(
                productCode,
                new WarehouseProductFullUpdateDto { IsActive = true, ProductType = 0 },
                "仓库员C"
            );
            var warehouseProduct = await _db.Queryable<WarehouseProduct>()
                .SingleAsync(x => x.ProductCode == productCode);

            Assert.True(result.Success);
            Assert.Equal("仓库员C", warehouseProduct.UpdatedBy);
        }

        [Fact]
        public async Task UpdatedBy_批量上下架写入传入操作人()
        {
            const string productCode = "P-UPDATED-BY-TOGGLE";
            await SeedPriceSyncProductAsync(productCode, 4.28m, 11.99m, 4.28m, 11.99m);

            var result = await CreateService().BatchToggleActiveAsync(
                new BatchToggleWarehouseProductsActiveRequestDto
                {
                    ProductCodes = new List<string> { productCode },
                    IsActive = false,
                },
                "仓库员D"
            );
            var warehouseProduct = await _db.Queryable<WarehouseProduct>()
                .SingleAsync(x => x.ProductCode == productCode);

            Assert.True(result.Success);
            Assert.Equal("仓库员D", warehouseProduct.UpdatedBy);
        }

        [Fact]
        public async Task UpdatedBy_批量创建同时写入创建人与更新人()
        {
            const string productCode = "P-UPDATED-BY-BATCH-CREATE";

            var result = await CreateService().BatchCreateAsync(
                new List<CreateItemDto>
                {
                    new()
                    {
                        ProductCode = productCode,
                        ItemNumber = "ITEM-UPDATED-BY-BATCH-CREATE",
                        ChineseName = "批量创建审计商品",
                        OEMPrice = 9.99m,
                        ImportPrice = 4.28m,
                    },
                },
                useTransaction: true,
                updatedBy: "仓库员E"
            );
            var warehouseProduct = await _db.Queryable<WarehouseProduct>()
                .SingleAsync(x => x.ProductCode == productCode);

            Assert.True(result.Success);
            Assert.Equal("仓库员E", warehouseProduct.CreatedBy);
            Assert.Equal("仓库员E", warehouseProduct.UpdatedBy);
        }

        [Fact]
        public async Task UpdatedBy_国内导入新建与更新仓库商品都写入操作人()
        {
            const string productCode = "P-UPDATED-BY-DOMESTIC-IMPORT";
            await SeedDomesticImportProductAsync(productCode, "国内导入审计商品", "Domestic audit product");
            var service = CreateService();

            var createResult = await service.ImportFromDomesticAsync(
                new ImportFromDomesticRequestDto { ProductCodes = new List<string> { productCode } },
                "仓库员F"
            );
            var created = await _db.Queryable<WarehouseProduct>()
                .SingleAsync(x => x.ProductCode == productCode);

            var updateResult = await service.ImportFromDomesticAsync(
                new ImportFromDomesticRequestDto { ProductCodes = new List<string> { productCode } },
                "仓库员G"
            );
            var updated = await _db.Queryable<WarehouseProduct>()
                .SingleAsync(x => x.ProductCode == productCode);

            Assert.True(createResult.Success);
            Assert.True(updateResult.Success);
            Assert.Equal("仓库员F", created.CreatedBy);
            Assert.Equal("仓库员F", created.UpdatedBy);
            Assert.Equal("仓库员G", updated.UpdatedBy);
        }

        [Fact]
        public async Task ImportFromDomesticAsync_成功项已有Type1关系也重算且失败项不参与()
        {
            const string succeededCode = "P-IMPORT-EXISTING-TYPE1";
            const string failedCode = "P-IMPORT-FAILED-TYPE1";
            await SeedDomesticImportProductAsync(succeededCode, "成功组合套装", "Imported Type1");
            await SeedDomesticImportProductAsync(failedCode, "失败组合套装", "Failed Type1");
            await _db.Updateable<DomesticProduct>()
                .SetColumns(x => x.ProductType == 1)
                .Where(x => x.ProductCode == succeededCode || x.ProductCode == failedCode)
                .ExecuteCommandAsync();
            await _db.Updateable<DomesticProduct>()
                .SetColumns(x => x.ImportPrice == 0m)
                .Where(x => x.ProductCode == failedCode)
                .ExecuteCommandAsync();
            await _db.Insertable(new[]
            {
                new Product { ProductCode = succeededCode, ProductName = "成功组合套装", PurchasePrice = 10m, IsActive = true, IsDeleted = false },
                new Product { ProductCode = failedCode, ProductName = "失败组合套装", PurchasePrice = 10m, IsActive = true, IsDeleted = false },
            }).ExecuteCommandAsync();
            await _db.Insertable(new[]
            {
                new DomesticSetProduct { ProductCode = succeededCode, SetProductCode = "SUCCESS-A", SetProductNo = "SUCCESS-A", SetBarcode = "SUCCESS-A", OEMPrice = 20m, IsDeleted = false },
                new DomesticSetProduct { ProductCode = succeededCode, SetProductCode = "SUCCESS-B", SetProductNo = "SUCCESS-B", SetBarcode = "SUCCESS-B", OEMPrice = 30m, IsDeleted = false },
                new DomesticSetProduct { ProductCode = failedCode, SetProductCode = "FAILED-A", SetProductNo = "FAILED-A", SetBarcode = "FAILED-A", OEMPrice = 20m, IsDeleted = false },
                new DomesticSetProduct { ProductCode = failedCode, SetProductCode = "FAILED-B", SetProductNo = "FAILED-B", SetBarcode = "FAILED-B", OEMPrice = 30m, IsDeleted = false },
            }).ExecuteCommandAsync();
            await _db.Insertable(new[]
            {
                new ProductSetCode { SetCodeId = "SUCCESS-A", ProductCode = succeededCode, SetProductCode = "SUCCESS-A", SetItemNumber = "SUCCESS-A", SetRetailPrice = 20m, SetPurchasePrice = 99m, SetType = 1, IsActive = true, IsDeleted = false },
                new ProductSetCode { SetCodeId = "SUCCESS-B", ProductCode = succeededCode, SetProductCode = "SUCCESS-B", SetItemNumber = "SUCCESS-B", SetRetailPrice = 30m, SetPurchasePrice = 99m, SetType = 1, IsActive = true, IsDeleted = false },
                new ProductSetCode { SetCodeId = "FAILED-A", ProductCode = failedCode, SetProductCode = "FAILED-A", SetItemNumber = "FAILED-A", SetRetailPrice = 20m, SetPurchasePrice = 99m, SetType = 1, IsActive = true, IsDeleted = false },
                new ProductSetCode { SetCodeId = "FAILED-B", ProductCode = failedCode, SetProductCode = "FAILED-B", SetItemNumber = "FAILED-B", SetRetailPrice = 30m, SetPurchasePrice = 99m, SetType = 1, IsActive = true, IsDeleted = false },
            }).ExecuteCommandAsync();

            var result = await CreateService().ImportFromDomesticAsync(new ImportFromDomesticRequestDto
            {
                ProductCodes = new List<string> { succeededCode, failedCode },
            });

            Assert.True(result.Success);
            Assert.Equal(1, result.SuccessCount);
            Assert.Equal(1, result.FailedCount);
            Assert.Equal(new decimal?[] { 4m, 6m }, (await _db.Queryable<ProductSetCode>()
                .Where(x => x.ProductCode == succeededCode)
                .OrderBy(x => x.SetProductCode)
                .ToListAsync()).Select(x => x.SetPurchasePrice));
            Assert.Equal(new decimal?[] { 99m, 99m }, (await _db.Queryable<ProductSetCode>()
                .Where(x => x.ProductCode == failedCode)
                .OrderBy(x => x.SetProductCode)
                .ToListAsync()).Select(x => x.SetPurchasePrice));
        }

        [Fact]
        public async Task ImportFromDomesticAsync_成功项已有Type2关系时同步主成本()
        {
            const string productCode = "P-IMPORT-EXISTING-TYPE2";
            await SeedDomesticImportProductAsync(productCode, "固定套装", "Imported Type2");
            await _db.Updateable<DomesticProduct>()
                .SetColumns(x => x.ProductType == 1)
                .Where(x => x.ProductCode == productCode)
                .ExecuteCommandAsync();
            await _db.Insertable(new Product
            {
                ProductCode = productCode,
                ProductName = "固定套装",
                PurchasePrice = 10m,
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();
            await _db.Insertable(new DomesticSetProduct
            {
                ProductCode = productCode,
                SetProductCode = "TYPE2-IMPORT-CHILD",
                SetProductNo = "TYPE2-IMPORT-ITEM",
                SetBarcode = "TYPE2-IMPORT-BARCODE",
                ImportPrice = 77m,
                OEMPrice = 20m,
                IsDeleted = false,
            }).ExecuteCommandAsync();
            await _db.Insertable(new ProductSetCode
            {
                SetCodeId = "TYPE2-IMPORT-CHILD",
                ProductCode = productCode,
                SetProductCode = "TYPE2-IMPORT-CHILD",
                SetItemNumber = "TYPE2-IMPORT-ITEM",
                SetBarcode = "TYPE2-IMPORT-BARCODE",
                SetPurchasePrice = 99m,
                SetRetailPrice = 20m,
                SetType = 2,
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();

            var result = await CreateService().ImportFromDomesticAsync(
                new ImportFromDomesticRequestDto { ProductCodes = new List<string> { productCode } }
            );

            Assert.True(result.Success, result.Message);
            Assert.Equal(10m, (await _db.Queryable<Product>()
                .SingleAsync(x => x.ProductCode == productCode)).PurchasePrice);
            Assert.Equal(10m, (await _db.Queryable<ProductSetCode>()
                .SingleAsync(x => x.ProductCode == productCode)).SetPurchasePrice);
        }

        [Fact]
        public async Task ImportFromDomesticAsync_默认不建门店投影时不因活跃门店缺少套装子项而回滚()
        {
            const string productCode = "P-IMPORT-TYPE1-NO-STORE-PROJECTION";
            await SeedDomesticImportProductAsync(productCode, "无门店投影套装", "Set without store projection");
            await _db.Updateable<DomesticProduct>()
                .SetColumns(x => x.ProductType == 1)
                .Where(x => x.ProductCode == productCode)
                .ExecuteCommandAsync();
            await _db.Insertable(new Store
            {
                StoreCode = "S-ACTIVE-NO-PROJECTION",
                StoreName = "无投影活跃门店",
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();
            await _db.Insertable(new Product
            {
                ProductCode = productCode,
                ProductName = "无门店投影套装",
                PurchasePrice = 10m,
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();
            await _db.Insertable(new[]
            {
                new DomesticSetProduct { ProductCode = productCode, SetProductCode = "NO-STORE-A", SetProductNo = "NO-STORE-A", SetBarcode = "NO-STORE-A", OEMPrice = 20m, IsDeleted = false },
                new DomesticSetProduct { ProductCode = productCode, SetProductCode = "NO-STORE-B", SetProductNo = "NO-STORE-B", SetBarcode = "NO-STORE-B", OEMPrice = 30m, IsDeleted = false },
            }).ExecuteCommandAsync();
            await _db.Insertable(new[]
            {
                new ProductSetCode { SetCodeId = "NO-STORE-A", ProductCode = productCode, SetProductCode = "NO-STORE-A", SetItemNumber = "NO-STORE-A", SetRetailPrice = 20m, SetPurchasePrice = 99m, SetType = 1, IsActive = true, IsDeleted = false },
                new ProductSetCode { SetCodeId = "NO-STORE-B", ProductCode = productCode, SetProductCode = "NO-STORE-B", SetItemNumber = "NO-STORE-B", SetRetailPrice = 30m, SetPurchasePrice = 99m, SetType = 1, IsActive = true, IsDeleted = false },
            }).ExecuteCommandAsync();

            var result = await CreateService().ImportFromDomesticAsync(
                new ImportFromDomesticRequestDto
                {
                    ProductCodes = new List<string> { productCode },
                    // 默认行为：不创建门店多码投影。
                    SyncMultiCodes = false,
                }
            );

            Assert.True(result.Success);
            Assert.Equal(1, result.SuccessCount);
            Assert.Equal(new decimal?[] { 4m, 6m }, (await _db.Queryable<ProductSetCode>()
                .Where(x => x.ProductCode == productCode)
                .OrderBy(x => x.SetProductCode)
                .ToListAsync()).Select(x => x.SetPurchasePrice));
            Assert.Equal(0, await _db.Queryable<StoreMultiCodeProduct>()
                .Where(x => x.ProductCode == productCode)
                .CountAsync());
        }

        [Fact]
        public async Task UpdatedBy_非国内导入新建与软删除恢复保留当前操作人()
        {
            const string newCode = "P-UPDATED-BY-NON-HB-CREATE";
            const string restoredCode = "P-UPDATED-BY-NON-HB-RESTORE";
            var originalCreatedAt = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            await _db.Insertable(new List<Product>
            {
                new()
                {
                    UUID = $"uuid-{newCode}",
                    ProductCode = newCode,
                    ProductName = "非国内新建审计商品",
                    PurchasePrice = 4.28m,
                    IsActive = true,
                    IsDeleted = false,
                },
                new()
                {
                    UUID = $"uuid-{restoredCode}",
                    ProductCode = restoredCode,
                    ProductName = "非国内恢复审计商品",
                    PurchasePrice = 5.28m,
                    IsActive = true,
                    IsDeleted = false,
                },
            }).ExecuteCommandAsync();
            await _db.Insertable(new WarehouseProduct
            {
                ProductCode = restoredCode,
                CreatedAt = originalCreatedAt,
                CreatedBy = "旧操作人",
                UpdatedBy = "旧操作人",
                IsActive = false,
                IsDeleted = true,
            }).ExecuteCommandAsync();

            var result = await CreateService().ImportNonHotbargainProductsAsync(
                new ImportNonHotbargainRequestDto
                {
                    ProductCodes = new List<string> { newCode, restoredCode },
                },
                "仓库员H"
            );
            var created = await _db.Queryable<WarehouseProduct>()
                .SingleAsync(x => x.ProductCode == newCode);
            var restored = await _db.Queryable<WarehouseProduct>()
                .SingleAsync(x => x.ProductCode == restoredCode);

            Assert.True(result.Success);
            Assert.Equal("仓库员H", created.CreatedBy);
            Assert.Equal("仓库员H", created.UpdatedBy);
            Assert.Equal(originalCreatedAt, restored.CreatedAt);
            Assert.Equal("旧操作人", restored.CreatedBy);
            Assert.Equal("仓库员H", restored.UpdatedBy);
        }

        [Fact]
        public async Task UpdatedBy_移动端修改写入传入操作人()
        {
            const string productCode = "P-UPDATED-BY-MOBILE";
            await SeedPriceSyncProductAsync(productCode, 4.28m, 11.99m, 4.28m, 11.99m);
            await _db.Ado.ExecuteCommandAsync(
                """
                CREATE TRIGGER trg_mobile_stock_patch_must_not_write_import_price
                BEFORE UPDATE OF ImportPrice ON WarehouseProduct
                WHEN OLD.ProductCode = 'P-UPDATED-BY-MOBILE'
                BEGIN
                    SELECT RAISE(ABORT, '库存修改不应写入未请求的进口价');
                END;
                """
            );

            await CreateService().PatchMobileProductAsync(
                productCode,
                new WarehouseMobileProductPatchDto { StockQuantity = 8 },
                "仓库员I"
            );
            var warehouseProduct = await _db.Queryable<WarehouseProduct>()
                .SingleAsync(x => x.ProductCode == productCode);

            Assert.Equal("仓库员I", warehouseProduct.UpdatedBy);
        }

        [Fact]
        public async Task UpdatedBy_移动端只修改商品字段时仓库仅写入审计列()
        {
            const string productCode = "P-UPDATED-BY-MOBILE-PRODUCT-ONLY";
            await SeedPriceSyncProductAsync(productCode, 4.28m, 11.99m, 4.28m, 11.99m);
            await _db.Ado.ExecuteCommandAsync(
                """
                CREATE TRIGGER trg_mobile_audit_patch_must_not_write_stock
                BEFORE UPDATE OF StockQuantity ON WarehouseProduct
                WHEN OLD.ProductCode = 'P-UPDATED-BY-MOBILE-PRODUCT-ONLY'
                BEGIN
                    SELECT RAISE(ABORT, '审计修改不应写入库存');
                END;
                """
            );

            await CreateService().PatchMobileProductAsync(
                productCode,
                new WarehouseMobileProductPatchDto
                {
                    ProductImage = "/images/mobile-audit.jpg",
                    Grade = "A",
                },
                "仓库员J"
            );
            var warehouseProduct = await _db.Queryable<WarehouseProduct>()
                .SingleAsync(x => x.ProductCode == productCode);

            Assert.Equal("仓库员J", warehouseProduct.UpdatedBy);
        }

        [Fact]
        public async Task PatchMobileProductAsync_仅审计更新保留并发库存()
        {
            const string productCode = "P-MOBILE-AUDIT-CONCURRENT-STOCK";
            await SeedPriceSyncProductAsync(productCode, 4.28m, 11.99m, 4.28m, 11.99m);
            await _db
                .Updateable<WarehouseProduct>()
                .SetColumns(w => w.StockQuantity == 5)
                .Where(w => w.ProductCode == productCode)
                .ExecuteCommandAsync();
            await _db.Ado.ExecuteCommandAsync(
                """
                CREATE TRIGGER trg_mobile_audit_concurrent_stock
                AFTER UPDATE OF ProductImage ON Product
                WHEN NEW.ProductCode = 'P-MOBILE-AUDIT-CONCURRENT-STOCK'
                BEGIN
                    UPDATE WarehouseProduct
                    SET StockQuantity = 73
                    WHERE ProductCode = NEW.ProductCode;
                END;
                """
            );

            // 图片仅改变 Product；触发器模拟两次写入之间发生的库存并发更新。
            var result = await CreateService().PatchMobileProductAsync(
                productCode,
                new WarehouseMobileProductPatchDto { ProductImage = "/images/audit-only-concurrent.jpg" },
                "移动端仓库员"
            );
            var warehouseProduct = await _db.Queryable<WarehouseProduct>()
                .SingleAsync(w => w.ProductCode == productCode);

            Assert.NotNull(result);
            Assert.Equal(73, warehouseProduct.StockQuantity);
            Assert.Equal("移动端仓库员", warehouseProduct.UpdatedBy);
        }

        [Fact]
        public async Task PatchMobileProductAsync_修改图片时保留并发Product中包数量()
        {
            const string productCode = "P-MOBILE-PRODUCT-COLUMN-SCOPE";
            var originalUpdatedAt = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            await SeedPriceSyncProductAsync(productCode, 4.28m, 11.99m, 4.28m, 11.99m);
            await _db
                .Updateable<Product>()
                .SetColumns(p => p.MiddlePackageQuantity == 12)
                .SetColumns(p => p.UpdatedAt == originalUpdatedAt)
                .Where(p => p.ProductCode == productCode)
                .ExecuteCommandAsync();
            await _db.Ado.ExecuteCommandAsync(
                """
                CREATE TRIGGER trg_mobile_product_image_concurrent_middle_package_quantity
                BEFORE UPDATE OF ProductImage ON Product
                WHEN OLD.ProductCode = 'P-MOBILE-PRODUCT-COLUMN-SCOPE'
                BEGIN
                    UPDATE Product
                    SET MiddlePackageQuantity = 73
                    WHERE ProductCode = NEW.ProductCode;
                END;
                """
            );

            var result = await CreateService().PatchMobileProductAsync(
                productCode,
                new WarehouseMobileProductPatchDto { ProductImage = "/images/product-column-scope.jpg" }
            );
            var product = await _db.Queryable<Product>()
                .SingleAsync(p => p.ProductCode == productCode);

            Assert.NotNull(result);
            Assert.Equal("/images/product-column-scope.jpg", product.ProductImage);
            Assert.Equal(73, product.MiddlePackageQuantity);
            Assert.True(product.UpdatedAt > originalUpdatedAt);
        }

        [Fact]
        public async Task PatchMobileProductAsync_修改国内价时保留并发DomesticProduct装箱数量()
        {
            const string productCode = "P-MOBILE-DOMESTIC-COLUMN-SCOPE";
            var originalUpdatedAt = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            await SeedPriceSyncProductAsync(productCode, 4.28m, 11.99m, 4.28m, 11.99m);
            await _db.Insertable(new DomesticProduct
            {
                ProductCode = productCode,
                DomesticPrice = 6.66m,
                PackingQuantity = 24,
                UpdatedAt = originalUpdatedAt,
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();
            await _db.Ado.ExecuteCommandAsync(
                """
                CREATE TRIGGER trg_mobile_domestic_price_concurrent_packing_quantity
                BEFORE UPDATE OF DomesticPrice ON DomesticProduct
                WHEN OLD.ProductCode = 'P-MOBILE-DOMESTIC-COLUMN-SCOPE'
                BEGIN
                    UPDATE DomesticProduct
                    SET PackingQuantity = 73
                    WHERE ProductCode = NEW.ProductCode;
                END;
                """
            );

            var result = await CreateService().PatchMobileProductAsync(
                productCode,
                new WarehouseMobileProductPatchDto { DomesticPrice = 8.88m }
            );
            var domesticProduct = await _db.Queryable<DomesticProduct>()
                .SingleAsync(p => p.ProductCode == productCode);

            Assert.NotNull(result);
            Assert.Equal(8.88m, domesticProduct.DomesticPrice);
            Assert.Equal(73, domesticProduct.PackingQuantity);
            Assert.True(domesticProduct.UpdatedAt > originalUpdatedAt);
        }

        [Fact]
        public async Task DetectAsync_ReturnsWarehousePricesNamesVolumeAndPackingQuantity()
        {
            await _db.Insertable(new Product
            {
                UUID = "product-uuid-detect-1",
                ProductCode = "P-DETECT-001",
                ProductName = "检测商品一",
                EnglishName = "Detect Product One",
                ItemNumber = "ITEM-DETECT-001",
                Barcode = "BAR-DETECT-001",
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();
            await _db.Insertable(new Product
            {
                UUID = "product-uuid-detect-2",
                ProductCode = "P-DETECT-002",
                ProductName = "检测商品二",
                EnglishName = "Detect Product Two",
                ItemNumber = "ITEM-DETECT-002",
                Barcode = "BAR-DETECT-002",
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();

            await _db.Insertable(new WarehouseProduct
            {
                ProductCode = "P-DETECT-001",
                DomesticPrice = 10.25m,
                OEMPrice = 20.50m,
                Volume = 0.125m,
                PackingQuantity = 24,
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();
            await _db.Insertable(new WarehouseProduct
            {
                ProductCode = "P-DETECT-002",
                DomesticPrice = 11.25m,
                OEMPrice = 21.50m,
                Volume = 0.225m,
                PackingQuantity = null,
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();

            await _db.Insertable(new DomesticProduct
            {
                ProductCode = "P-DETECT-001",
                ProductName = "检测商品一",
                PackingQuantity = 48,
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();
            await _db.Insertable(new DomesticProduct
            {
                ProductCode = "P-DETECT-002",
                ProductName = "检测商品二",
                PackingQuantity = 36,
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();

            var service = CreateService();

            var result = await service.DetectAsync(new List<DetectionItemDto>
            {
                new() { ProductCode = "P-DETECT-001", ItemNumber = "ITEM-DETECT-001" },
                new() { ProductCode = "P-DETECT-002", ItemNumber = "ITEM-DETECT-002" },
            });

            Assert.Collection(
                result,
                first =>
                {
                    Assert.True(first.Exists);
                    Assert.Equal("检测商品一", first.ProductName);
                    Assert.Equal("Detect Product One", first.EnglishName);
                    Assert.Equal(10.25m, first.WarehouseDomesticPrice);
                    Assert.Equal(20.50m, first.WarehouseOEMPrice);
                    Assert.Equal(0.125m, first.WarehouseVolume);
                    Assert.Equal(48, first.PackingQuantity);
                },
                second =>
                {
                    Assert.True(second.Exists);
                    Assert.Equal("检测商品二", second.ProductName);
                    Assert.Equal("Detect Product Two", second.EnglishName);
                    Assert.Equal(11.25m, second.WarehouseDomesticPrice);
                    Assert.Equal(21.50m, second.WarehouseOEMPrice);
                    Assert.Equal(0.225m, second.WarehouseVolume);
                    Assert.Equal(36, second.PackingQuantity);
                }
            );
        }

        [Fact]
        public async Task DetectAsync_UsesDomesticProductFallbackForNewProductAndWarehouseOemPriceForExisting()
        {
            await _db.Insertable(new Product
            {
                UUID = "product-uuid-detect-existing",
                ProductCode = "P-DETECT-EXISTING",
                ProductName = "仓库已有商品",
                EnglishName = "Existing Warehouse Product",
                ItemNumber = "ITEM-DETECT-EXISTING",
                Barcode = "BAR-DETECT-EXISTING",
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();

            await _db.Insertable(new WarehouseProduct
            {
                ProductCode = "P-DETECT-EXISTING",
                ImportPrice = 8.8m,
                OEMPrice = 19.9m,
                DomesticPrice = 6.6m,
                Volume = 0.2m,
                PackingQuantity = 12,
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();

            await _db.Insertable(new DomesticProduct
            {
                ProductCode = "P-DETECT-EXISTING",
                HBProductNo = "ITEM-DETECT-EXISTING",
                Barcode = "BAR-DETECT-EXISTING",
                ProductName = "国内已有商品名",
                EnglishProductName = "Domestic Existing Name",
                OEMPrice = 25.5m,
                PackingQuantity = 48,
                UnitVolume = 0.33m,
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();

            await _db.Insertable(new DomesticProduct
            {
                ProductCode = "P-DOMESTIC-CODE",
                HBProductNo = "HB138-066",
                Barcode = "9527913800028",
                ProductName = "金/黑框混30X40",
                EnglishProductName = "Frame Mixed 30X40",
                OEMPrice = 15.5m,
                PackingQuantity = 24,
                UnitVolume = 0.4m,
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();

            var service = CreateService();

            var result = await service.DetectAsync(new List<DetectionItemDto>
            {
                new() { ProductCode = "P-DETECT-EXISTING", ItemNumber = "ITEM-DETECT-EXISTING", Barcode = "BAR-DETECT-EXISTING" },
                new() { ItemNumber = "HB138-066", Barcode = "9527913800028" },
            });

            Assert.Collection(
                result,
                existing =>
                {
                    Assert.True(existing.Exists);
                    Assert.Equal("国内已有商品名", existing.ProductName);
                    Assert.Equal("Domestic Existing Name", existing.EnglishName);
                    Assert.Equal(8.8m, existing.WarehouseImportPrice);
                    Assert.Equal(19.9m, existing.WarehouseOEMPrice);
                    Assert.Equal(48, existing.PackingQuantity);
                    Assert.Equal(0.33m, existing.WarehouseVolume);
                },
                newProduct =>
                {
                    Assert.False(newProduct.Exists);
                    Assert.Equal("none", newProduct.MatchType);
                    Assert.Equal("P-DOMESTIC-CODE", newProduct.ProductCode);
                    Assert.Equal("HB138-066", newProduct.ItemNumber);
                    Assert.Equal("金/黑框混30X40", newProduct.ProductName);
                    Assert.Equal(15.5m, newProduct.WarehouseOEMPrice);
                    Assert.Equal(24, newProduct.PackingQuantity);
                    Assert.Equal(0.4m, newProduct.WarehouseVolume);
                }
            );
        }

        [Fact]
        public async Task DetectAsync_货号候选编码不一致_应返回商品编码冲突字段()
        {
            await _db.Insertable(new Product
            {
                UUID = "product-uuid-local-align",
                ProductCode = "LOCAL-CODE-001",
                ProductName = "本地主档商品",
                ItemNumber = "ITEM-ALIGN-001",
                LocalSupplierCode = "200",
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();
            await _db.Insertable(new Product
            {
                UUID = "product-uuid-local-align-other",
                ProductCode = "OTHER-CODE-001",
                ProductName = "其他供应商同货号商品",
                ItemNumber = "ITEM-ALIGN-001",
                LocalSupplierCode = "999",
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();
            await _db.Insertable(new WarehouseProduct
            {
                ProductCode = "LOCAL-CODE-001",
                OEMPrice = 6.99m,
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();
            await _db.Insertable(new WarehouseProduct
            {
                ProductCode = "OTHER-CODE-001",
                OEMPrice = 9.99m,
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();
            await _db.Insertable(new DomesticProduct
            {
                ProductCode = "DOM-CODE-001",
                HBProductNo = "ITEM-ALIGN-001",
                SupplierCode = "200",
                ProductName = "国内旧编码商品",
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();
            await _db.Insertable(new DomesticProduct
            {
                ProductCode = "DOM-CODE-999",
                HBProductNo = "ITEM-ALIGN-001",
                SupplierCode = "999",
                ProductName = "其他供应商国内商品",
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();

            var service = CreateService();

            var result = await service.DetectAsync(new List<DetectionItemDto>
            {
                new() { ProductCode = "DOM-CODE-001", ItemNumber = "ITEM-ALIGN-001", SupplierCode = "200" },
            });

            var item = Assert.Single(result);
            Assert.True(item.Exists);
            Assert.Equal("item_number", item.MatchType);
            Assert.Equal("200", item.SupplierCode);
            Assert.Equal("LOCAL-CODE-001", item.LocalProductCode);
            Assert.Equal("DOM-CODE-001", item.DomesticProductCode);
            Assert.True(item.HasProductCodeConflict);
            Assert.Equal("国内商品编码与本地主档商品编码不一致", item.ConflictReason);
        }

        [Fact]
        public async Task DetectAsync_同货号不同供应商_国内候选应按供应商代码筛选()
        {
            await _db.Insertable(new Product
            {
                UUID = "product-uuid-supplier-item-200",
                ProductCode = "LOCAL-SUP-200",
                ProductName = "供应商200本地主档",
                ItemNumber = "ITEM-SHARED",
                LocalSupplierCode = "200",
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();
            await _db.Insertable(new Product
            {
                UUID = "product-uuid-supplier-item-999",
                ProductCode = "LOCAL-SUP-999",
                ProductName = "供应商999本地主档",
                ItemNumber = "ITEM-SHARED",
                LocalSupplierCode = "999",
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();
            await _db.Insertable(new List<WarehouseProduct>
            {
                new()
                {
                    ProductCode = "LOCAL-SUP-200",
                    IsActive = true,
                    IsDeleted = false,
                },
                new()
                {
                    ProductCode = "LOCAL-SUP-999",
                    IsActive = true,
                    IsDeleted = false,
                },
            }).ExecuteCommandAsync();
            await _db.Insertable(new List<DomesticProduct>
            {
                new()
                {
                    ProductCode = "DOM-SUP-999",
                    HBProductNo = "ITEM-SHARED",
                    SupplierCode = "999",
                    ProductName = "供应商999国内商品",
                    IsActive = true,
                    IsDeleted = false,
                },
                new()
                {
                    ProductCode = "DOM-SUP-200",
                    HBProductNo = "ITEM-SHARED",
                    SupplierCode = "200",
                    ProductName = "供应商200国内商品",
                    IsActive = true,
                    IsDeleted = false,
                },
            }).ExecuteCommandAsync();

            var service = CreateService();

            var result = await service.DetectAsync(new List<DetectionItemDto>
            {
                new() { ProductCode = "DOM-MISSING", ItemNumber = "ITEM-SHARED", SupplierCode = "200" },
            });

            var item = Assert.Single(result);
            Assert.True(item.Exists);
            Assert.Equal("LOCAL-SUP-200", item.LocalProductCode);
            Assert.Equal("DOM-SUP-200", item.DomesticProductCode);
            Assert.Equal("供应商200国内商品", item.ProductName);
        }

        [Fact]
        public async Task LookupMobileProductsAsync_ReturnsWarehouseFieldsUsedByMobileUi()
        {
            await _db.Insertable(new ChinaSupplier
            {
                Guid = "supplier-guid",
                SupplierCode = "SUP-001",
                SupplierName = "Supplier One",
                IsDeleted = false,
            }).ExecuteCommandAsync();

            await _db.Insertable(new Product
            {
                UUID = "product-uuid-1",
                ProductCode = "P001",
                ProductName = "Widget",
                ItemNumber = "ITEM-001",
                Barcode = "BAR-001",
                ProductImage = null,
                LocalSupplierCode = "LOCAL-01",
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();

            await _db.Insertable(new WarehouseProduct
            {
                ProductCode = "P001",
                OEMPrice = 12.5m,
                ImportPrice = 8.8m,
                StockQuantity = 33,
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();

            await _db.Insertable(new DomesticProduct
            {
                ProductCode = "P001",
                SupplierCode = "SUP-001",
                ProductName = "Widget",
                ProductImage = "https://cdn.example.com/fallback.png",
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();

            await _db.Insertable(new Location
            {
                LocationGuid = "loc-001",
                LocationCode = "A-01-01-01",
                LocationBarcode = "LOCBAR001",
                LocationType = 1,
                Status = 1,
                IsDeleted = false,
            }).ExecuteCommandAsync();

            await _db.Insertable(new ProductLocation
            {
                Guid = "product-location-001",
                ProductCode = "P001",
                LocationGuid = "loc-001",
                IsDeleted = false,
            }).ExecuteCommandAsync();

            await _db.Insertable(new ProductGrade
            {
                Id = "grade-001",
                ProductCode = "P001",
                Grade = "A",
                IsDeleted = false,
            }).ExecuteCommandAsync();

            var service = CreateService();

            var result = await service.LookupMobileProductsAsync("ITEM-001");

            var item = Assert.Single(result);
            Assert.Equal("P001", item.ProductCode);
            Assert.Equal("ITEM-001", item.ItemNumber);
            Assert.Equal("BAR-001", item.Barcode);
            Assert.Equal("Supplier One", item.SupplierName);
            Assert.Equal("A", item.Grade);
            Assert.Equal(33, item.StockQuantity);
            Assert.Equal("A-01-01-01", item.LocationCode);
            Assert.Equal("https://cdn.example.com/fallback.png", item.ProductImage);
        }

        [Fact]
        public async Task LookupAndGetMobileProductAsync_ReturnWarehouseIsActiveAndLegacyIsActiveWithSameValue()
        {
            await _db.Insertable(new Product
            {
                UUID = "product-uuid-active-1",
                ProductCode = "P-ACTIVE-001",
                ProductName = "Active Widget",
                ItemNumber = "ITEM-ACTIVE-001",
                Barcode = "BAR-ACTIVE-001",
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();

            await _db.Insertable(new WarehouseProduct
            {
                ProductCode = "P-ACTIVE-001",
                IsActive = false,
                StockQuantity = 5,
                IsDeleted = false,
            }).ExecuteCommandAsync();

            await _db.Insertable(new DomesticProduct
            {
                ProductCode = "P-ACTIVE-001",
                ProductName = "Active Widget",
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();

            var service = CreateService();

            var lookupItems = await service.LookupMobileProductsAsync("ITEM-ACTIVE-001");
            var lookupItem = Assert.Single(lookupItems);
            Assert.False(lookupItem.WarehouseIsActive);
            Assert.False(lookupItem.IsActive);

            var detailItem = await service.GetMobileProductAsync("P-ACTIVE-001");
            Assert.NotNull(detailItem);
            Assert.False(detailItem!.WarehouseIsActive);
            Assert.False(detailItem.IsActive);
        }

        [Fact]
        public async Task LookupMobileProductsAsync_MatchesChineseEnglishCaseAndLocationCodes()
        {
            await SeedMobileLookupScenarioAsync(_db);

            await AssertMobileLookupScenarioAsync(CreateService());
        }

        [Fact]
        public void LookupMobileProductsAsync_SqlServerPatternEscapesLikeWildcards()
        {
            Assert.Equal(
                "%50[%]OFF%",
                ProductWarehouseMobileSlice.BuildSqlServerContainsPattern("50%OFF")
            );
            Assert.Equal(
                "%HB[_]7%",
                ProductWarehouseMobileSlice.BuildSqlServerContainsPattern("HB_7")
            );
            Assert.Equal(
                "%[[]A]-01%",
                ProductWarehouseMobileSlice.BuildSqlServerContainsPattern("[A]-01")
            );
            Assert.Equal(
                "%不锈钢%",
                ProductWarehouseMobileSlice.BuildSqlServerContainsPattern("不锈钢")
            );
        }

        [Fact]
        public void LookupMobileProductsAsync_SqlServerFilterUsesCandidateUnionAndLiteralDeleteFlags()
        {
            var filter = ProductWarehouseMobileSlice.MobileLookupSqlServerFilter;

            // 先 UNION 候选再取货号前 50 个商品，避免宽 OR 跨 7 表逐行求值。
            Assert.Contains("SELECT TOP 50 [mc].[ProductCode]", filter, StringComparison.Ordinal);
            Assert.Equal(3, CountOccurrences(filter, " UNION SELECT "));
            Assert.DoesNotContain("UNION ALL", filter, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("ORDER BY [mcp].[ItemNumber]", filter, StringComparison.Ordinal);
            // 主数据编码为 nvarchar，候选统一转 varchar，与仓库编码比较时不转换索引列。
            Assert.Equal(2, CountOccurrences(filter, "AS varchar(255))"));
            // 判别列必须是字面量才能命中 IsDeleted=0 过滤索引。
            Assert.Equal(8, CountOccurrences(filter, "[IsDeleted] = 0"));
            Assert.DoesNotContain("LOWER(", filter, StringComparison.OrdinalIgnoreCase);
            // 候选 9 列 + 外层复核 9 列，全部走 UPPER + BIN2 且共用同一参数。
            Assert.Equal(18, CountOccurrences(filter, "COLLATE Latin1_General_100_BIN2 LIKE @mobileLookupPattern"));
            Assert.Equal(18, CountOccurrences(filter, "UPPER(CAST("));
            foreach (var column in new[]
            {
                "[w].[ProductCode]",
                "[p].[ProductName]",
                "[p].[ItemNumber]",
                "[p].[Barcode]",
                "[p].[LocalSupplierCode]",
                "[s].[SupplierName]",
                "[s].[SupplierCode]",
                "[l].[LocationCode]",
                "[l].[LocationBarcode]",
            })
            {
                Assert.Contains($"UPPER(CAST({column} AS nvarchar(4000)))", filter, StringComparison.Ordinal);
            }
        }

        [ProductWarehouseMobileLookupSqlServerFact]
        [Trait("Category", "SQL")]
        public async Task LookupMobileProductsAsync_SqlServerMatchesScenarioAndTreatsWildcardsLiterally()
        {
            var serverConnection = Environment.GetEnvironmentVariable(
                ProductWarehouseMobileLookupSqlServerFactAttribute.ConnectionEnvironmentVariable
            )!;
            var databaseName = "hb_mobile_lookup_" + Guid.NewGuid().ToString("N");
            var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(serverConnection)
            {
                InitialCatalog = "master",
            };
            using var master = CreateSqlServerClient(builder.ConnectionString);
            // 与生产同排序规则，确保中文、大小写与隐式转换行为一致。
            await master.Ado.ExecuteCommandAsync(
                $"CREATE DATABASE [{databaseName}] COLLATE Chinese_PRC_90_CI_AS"
            );
            try
            {
                builder.InitialCatalog = databaseName;
                using var db = CreateSqlServerClient(builder.ConnectionString);
                await CreateMobileLookupSqlServerSchemaAsync(db);
                await SeedMobileLookupScenarioAsync(db);

                var executedSql = new List<string>();
                db.Aop.OnLogExecuting = (sql, _) => executedSql.Add(sql);
                var service = CreateService(database: db);

                await AssertMobileLookupScenarioAsync(service);

                // SQL Server 分支按字面匹配 LIKE 通配符，诱饵商品不能被 % _ [ 误命中。
                Assert.Equal(
                    new[] { "MLK-PCT" },
                    (await service.LookupMobileProductsAsync("50%off")).Select(item => item.ProductCode)
                );
                Assert.Equal(
                    new[] { "MLK-UND" },
                    (await service.LookupMobileProductsAsync("hb_7")).Select(item => item.ProductCode)
                );
                Assert.Equal(
                    new[] { "MLK-BRK" },
                    (await service.LookupMobileProductsAsync("[a]")).Select(item => item.ProductCode)
                );

                var lookupSql = Assert.Single(
                    executedSql.Distinct(),
                    sql => sql.Contains("[mc].[ProductCode]", StringComparison.Ordinal)
                );
                Assert.Contains("@mobileLookupPattern", lookupSql, StringComparison.Ordinal);
                Assert.DoesNotContain("LOWER(", lookupSql, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                await master.Ado.ExecuteCommandAsync(
                    $"ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{databaseName}]"
                );
            }
        }

        private static SqlSugarClient CreateSqlServerClient(string connectionString) =>
            new(new ConnectionConfig
            {
                ConnectionString = connectionString,
                DbType = DbType.SqlServer,
                IsAutoCloseConnection = true,
                InitKeyType = InitKeyType.Attribute,
                MoreSettings = new ConnMoreSettings(),
            });

        private static async Task CreateMobileLookupSqlServerSchemaAsync(ISqlSugarClient db)
        {
            db.CodeFirst.InitTables(
                typeof(Product),
                typeof(WarehouseProduct),
                typeof(DomesticProduct),
                typeof(ChinaSupplier),
                typeof(ProductLocation),
                typeof(Location),
                typeof(ProductGrade)
            );
            // CodeFirst 默认建 nvarchar；按生产把仓库、货位、供应商相关列改回 varchar，
            // 覆盖 varchar 列存中文供应商名、nvarchar/varchar 编码联接两类真实差异。
            await db.Ado.ExecuteCommandAsync(
                """
                DECLARE @pk sysname = (
                    SELECT name FROM sys.key_constraints
                    WHERE parent_object_id = OBJECT_ID('WarehouseProduct') AND type = 'PK'
                );
                EXEC('ALTER TABLE [WarehouseProduct] DROP CONSTRAINT ' + @pk);
                ALTER TABLE [WarehouseProduct] ALTER COLUMN [ProductCode] varchar(255) NOT NULL;
                ALTER TABLE [WarehouseProduct] ADD CONSTRAINT [PK_WarehouseProduct_ProductCode] PRIMARY KEY ([ProductCode]);
                CREATE NONCLUSTERED INDEX [IX_WarehouseProduct_ProductCode_NotDeleted]
                    ON [WarehouseProduct]([ProductCode]) WHERE [IsDeleted] = (0);
                ALTER TABLE [ProductLocation] ALTER COLUMN [ProductCode] varchar(50) NOT NULL;
                ALTER TABLE [ChinaSupplier] ALTER COLUMN [SupplierCode] varchar(255) NULL;
                ALTER TABLE [ChinaSupplier] ALTER COLUMN [SupplierName] varchar(255) NULL;
                ALTER TABLE [Location] ALTER COLUMN [LocationCode] varchar(255) NULL;
                ALTER TABLE [Location] ALTER COLUMN [LocationBarcode] varchar(255) NULL;
                """
            );
        }

        /// <summary>
        /// 移动端查询共用场景：中文名称/供应商、英文大小写、货号、仓库编码、多货位商品、已删除商品、
        /// 通配符诱饵以及超过 50 个命中的批量商品；SQLite 与 SQL Server 两条路径使用同一份数据。
        /// </summary>
        private static async Task SeedMobileLookupScenarioAsync(ISqlSugarClient db)
        {
            await db.Insertable(new ChinaSupplier
            {
                Guid = "mlk-supplier-cn",
                SupplierCode = "HB201",
                SupplierName = "义乌针织厂",
                IsDeleted = false,
            }).ExecuteCommandAsync();

            var products = new (string Code, string Name, string ItemNumber, string Barcode, bool Deleted)[]
            {
                ("MLK-CN", "不锈钢汤勺", "HB201-01", "6900000000011", false),
                ("MLK-EN", "Towel Rail Chrome", "HB202-02", "9300000000022", false),
                ("MLK-LOC", "Loc Widget", "HB203-03", "9300000000033", false),
                ("MLK-DEL", "Towel Deleted", "HB209-09", "9300000000099", true),
                ("MLK-PCT", "Promo Sticker", "50%OFF-01", "9300000000044", false),
                ("MLK-PCT-DECOY", "Promo Decoy", "50-SALE-OFF-02", "9300000000055", false),
                ("MLK-UND", "Underscore Item", "HB_7-01", "9300000000066", false),
                ("MLK-UND-DECOY", "Underscore Decoy", "HBX7-02", "9300000000077", false),
                ("MLK-BRK", "Bracket Item", "[A]-01", "9300000000088", false),
                ("MLK-BRK-DECOY", "Bracket Decoy", "QA-02", "9300000000111", false),
            };
            var bulk = Enumerable
                .Range(1, 55)
                .Select(index =>
                    (
                        Code: $"MLK-BULK-{index:000}",
                        Name: "Bulk Carton Item",
                        ItemNumber: $"BULK-{index:000}",
                        Barcode: $"93100000{index:00000}",
                        Deleted: false
                    )
                );
            var allProducts = products.Concat(bulk).ToList();

            await db.Insertable(
                allProducts
                    .Select(item => new Product
                    {
                        UUID = "uuid-" + item.Code,
                        ProductCode = item.Code,
                        ProductName = item.Name,
                        ItemNumber = item.ItemNumber,
                        Barcode = item.Barcode,
                        IsActive = true,
                        IsDeleted = false,
                    })
                    .ToList()
            ).ExecuteCommandAsync();
            await db.Insertable(
                allProducts
                    .Select(item => new WarehouseProduct
                    {
                        ProductCode = item.Code,
                        StockQuantity = 1,
                        IsActive = true,
                        IsDeleted = item.Deleted,
                    })
                    .ToList()
            ).ExecuteCommandAsync();
            await db.Insertable(new DomesticProduct
            {
                ProductCode = "MLK-CN",
                SupplierCode = "HB201",
                ProductName = "不锈钢汤勺",
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();

            await db.Insertable(new List<Location>
            {
                new()
                {
                    LocationGuid = "mlk-loc-a",
                    LocationCode = "A-01-01-01",
                    LocationBarcode = "5180000000001",
                    LocationType = 1,
                    Status = 1,
                    IsDeleted = false,
                },
                new()
                {
                    LocationGuid = "mlk-loc-b",
                    LocationCode = "B-02-03-04",
                    LocationBarcode = "5180000000002",
                    LocationType = 1,
                    Status = 1,
                    IsDeleted = false,
                },
            }).ExecuteCommandAsync();
            await db.Insertable(new List<ProductLocation>
            {
                new() { Guid = "mlk-pl-a", ProductCode = "MLK-LOC", LocationGuid = "mlk-loc-a", IsDeleted = false },
                new() { Guid = "mlk-pl-b", ProductCode = "MLK-LOC", LocationGuid = "mlk-loc-b", IsDeleted = false },
            }).ExecuteCommandAsync();
        }

        private static async Task AssertMobileLookupScenarioAsync(ProductWarehouseReactService service)
        {
            async Task<string[]> CodesAsync(string keyword) =>
                (await service.LookupMobileProductsAsync(keyword))
                    .Select(item => item.ProductCode)
                    .ToArray();

            // 中文：商品名称与 varchar 列里的中文供应商名称都要命中。
            Assert.Equal(new[] { "MLK-CN" }, await CodesAsync("汤勺"));
            Assert.Equal(new[] { "MLK-CN" }, await CodesAsync("义乌针织"));

            // 英文大小写不敏感，已删除仓库商品不返回。
            Assert.Equal(new[] { "MLK-EN" }, await CodesAsync("towel"));
            Assert.Equal(new[] { "MLK-EN" }, await CodesAsync("TOWEL"));
            Assert.Equal(new[] { "MLK-EN" }, await CodesAsync("ToWeL rAiL"));

            // 仓库编码、货号、条码、供应商编码都参与匹配。
            Assert.Equal(new[] { "MLK-EN" }, await CodesAsync("mlk-en"));
            Assert.Equal(new[] { "MLK-CN" }, await CodesAsync("hb201"));
            Assert.Equal(new[] { "MLK-EN" }, await CodesAsync("9300000000022"));

            // 货位编码与货位条码命中时，多货位商品返回命中的那条货位。
            var byLocationCode = Assert.Single(await service.LookupMobileProductsAsync("b-02-03"));
            Assert.Equal("MLK-LOC", byLocationCode.ProductCode);
            Assert.Equal("B-02-03-04", byLocationCode.LocationCode);
            var byLocationBarcode = Assert.Single(await service.LookupMobileProductsAsync("5180000000001"));
            Assert.Equal("MLK-LOC", byLocationBarcode.ProductCode);
            Assert.Equal("A-01-01-01", byLocationBarcode.LocationCode);
            Assert.Equal("5180000000001", byLocationBarcode.LocationBarcode);

            // 按货号升序，多货位商品只返回一条。
            Assert.Equal(new[] { "MLK-CN", "MLK-EN", "MLK-LOC" }, await CodesAsync("hb20"));

            // 超过 50 个命中时只取货号最小的 50 个。
            Assert.Equal(
                Enumerable.Range(1, 50).Select(index => $"MLK-BULK-{index:000}"),
                await CodesAsync("bulk carton")
            );

            Assert.Empty(await CodesAsync("no-such-keyword"));
        }

        private static int CountOccurrences(string text, string value)
        {
            var count = 0;
            for (
                var index = text.IndexOf(value, StringComparison.Ordinal);
                index >= 0;
                index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal)
            )
            {
                count++;
            }

            return count;
        }

        [Fact]
        public async Task GetDomesticProductsNotInWarehouseAsync_ReturnsProductImageForImportModal()
        {
            await _db.Insertable(new DomesticProduct
            {
                ProductCode = "DP-IMG-001",
                HBProductNo = "HB022-109",
                Barcode = "9525810220074",
                ProductName = "圆球",
                ProductImage = null,
                ProductType = 0,
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();

            var service = CreateService();

            var result = await service.GetDomesticProductsNotInWarehouseAsync(
                new GetDomesticProductsNotInWarehouseRequestDto
                {
                    Page = 1,
                    PageSize = 20,
                    GlobalSearch = "HB022-109",
                }
            );

            var item = Assert.Single(result.Items);
            Assert.Equal("HB022-109", item.ItemNumber);
            Assert.Equal(
                "https://hotbargain-yw-2023-1300114625.cos.ap-shanghai.myqcloud.com/YW200/HB022-109.jpg",
                item.ProductImage
            );
        }

        [Fact]
        public async Task SetMobileProductLocationAsync_BindsEmptyPickingLocation()
        {
            await SeedWarehouseProductAsync("P-PICK-EMPTY", "ITEM-PICK-EMPTY", "BAR-PICK-EMPTY");
            await SeedLocationAsync("loc-pick-empty", "A-00-00-01", 1);
            var service = CreateService();

            var result = await service.SetMobileProductLocationAsync("P-PICK-EMPTY", "loc-pick-empty");

            Assert.NotNull(result);
            Assert.Equal("loc-pick-empty", result.LocationGuid);
            Assert.Equal("A-00-00-01", result.LocationCode);
        }

        [Fact]
        public async Task SetMobileProductLocationAsync_BlocksOccupiedPickingLocation()
        {
            await SeedWarehouseProductAsync("P-PICK-TARGET", "ITEM-PICK-TARGET", "BAR-PICK-TARGET");
            await SeedWarehouseProductAsync("P-PICK-OTHER", "ITEM-PICK-OTHER", "BAR-PICK-OTHER");
            await SeedLocationAsync("loc-pick-used", "A-00-00-02", 1);
            await _db.Insertable(new ProductLocation
            {
                Guid = "product-location-pick-used",
                ProductCode = "P-PICK-OTHER",
                LocationGuid = "loc-pick-used",
                IsDeleted = false,
            }).ExecuteCommandAsync();
            var service = CreateService();

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.SetMobileProductLocationAsync("P-PICK-TARGET", "loc-pick-used")
            );

            Assert.Equal("该配货位已有商品，不能继续绑定", error.Message);
        }

        [Fact]
        public async Task PatchMobileProductAsync_WhenWarehouseIsActiveIsFalse_OnlyUpdatesWarehouseProductIsActive()
        {
            var productUpdatedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            var domesticUpdatedAt = new DateTime(2026, 1, 3, 3, 4, 5, DateTimeKind.Utc);
            var gradeUpdatedAt = new DateTime(2026, 1, 4, 3, 4, 5, DateTimeKind.Utc);
            await _db.Insertable(new Product
            {
                UUID = "product-uuid-patch-1",
                ProductCode = "P-PATCH-001",
                ProductName = "Patch Widget",
                ItemNumber = "ITEM-PATCH-001",
                Barcode = "BAR-PATCH-001",
                PurchasePrice = 4.28m,
                RetailPrice = 11.99m,
                IsActive = true,
                IsDeleted = false,
                UpdatedAt = productUpdatedAt,
            }).ExecuteCommandAsync();

            await _db.Insertable(new WarehouseProduct
            {
                ProductCode = "P-PATCH-001",
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();

            await _db.Insertable(new DomesticProduct
            {
                ProductCode = "P-PATCH-001",
                ProductName = "Patch Widget",
                DomesticPrice = 6.66m,
                OEMPrice = 7.77m,
                ImportPrice = 8.88m,
                IsActive = true,
                IsDeleted = false,
                UpdatedAt = domesticUpdatedAt,
            }).ExecuteCommandAsync();

            await _db.Insertable(new ProductGrade
            {
                Id = "grade-patch-1",
                ProductCode = "P-PATCH-001",
                Grade = "D",
                IsDeleted = false,
                UpdatedAt = gradeUpdatedAt,
            }).ExecuteCommandAsync();

            var service = CreateService();

            var result = await service.PatchMobileProductAsync(
                "P-PATCH-001",
                new WarehouseMobileProductPatchDto { WarehouseIsActive = false }
            );

            Assert.NotNull(result);
            Assert.False(result!.WarehouseIsActive);
            Assert.False(result.IsActive);

            var warehouseProduct = await _db.Queryable<WarehouseProduct>()
                .Where(w => w.ProductCode == "P-PATCH-001")
                .FirstAsync();
            var product = await _db.Queryable<Product>()
                .Where(p => p.ProductCode == "P-PATCH-001")
                .FirstAsync();
            var domesticProduct = await _db.Queryable<DomesticProduct>()
                .Where(dp => dp.ProductCode == "P-PATCH-001")
                .FirstAsync();
            var productGrade = await _db.Queryable<ProductGrade>()
                .Where(pg => pg.ProductCode == "P-PATCH-001")
                .FirstAsync();

            Assert.NotNull(warehouseProduct);
            Assert.NotNull(product);
            Assert.NotNull(domesticProduct);
            Assert.NotNull(productGrade);
            Assert.False(warehouseProduct!.IsActive);
            Assert.True(product!.IsActive);
            Assert.True(domesticProduct!.IsActive);
            Assert.Equal("D", productGrade!.Grade);
            Assert.Equal(gradeUpdatedAt, productGrade.UpdatedAt);
            Assert.Equal(4.28m, product.PurchasePrice);
            Assert.Equal(11.99m, product.RetailPrice);
            Assert.Equal(productUpdatedAt, product.UpdatedAt);
            Assert.Equal(6.66m, domesticProduct.DomesticPrice);
            Assert.Equal(7.77m, domesticProduct.OEMPrice);
            Assert.Equal(8.88m, domesticProduct.ImportPrice);
            Assert.Equal(domesticUpdatedAt, domesticProduct.UpdatedAt);
        }

        [Fact]
        public async Task PatchMobileProductAsync_WhenLegacyIsActiveProvided_UpdatesWarehouseProductForCompatibility()
        {
            await SeedWarehouseProductAsync("P-LEGACY-ACTIVE", "ITEM-LEGACY-ACTIVE", "BAR-LEGACY-ACTIVE");
            var service = CreateService();

            var result = await service.PatchMobileProductAsync(
                "P-LEGACY-ACTIVE",
                new WarehouseMobileProductPatchDto { IsActive = false }
            );

            Assert.NotNull(result);
            Assert.False(result!.WarehouseIsActive);
            Assert.False(result.IsActive);
        }

        [Fact]
        public async Task PatchMobileProductAsync_WhenNewAndLegacyStatusConflict_UsesWarehouseIsActive()
        {
            await SeedWarehouseProductAsync("P-STATUS-CONFLICT", "ITEM-STATUS-CONFLICT", "BAR-STATUS-CONFLICT");
            var service = CreateService();

            var result = await service.PatchMobileProductAsync(
                "P-STATUS-CONFLICT",
                new WarehouseMobileProductPatchDto { WarehouseIsActive = false, IsActive = true }
            );

            Assert.NotNull(result);
            Assert.False(result!.WarehouseIsActive);
            Assert.False(result.IsActive);
        }

        [Fact]
        public async Task PatchMobileProductAsync_WhenImportPriceChanges_UpdatesProductAndAllActiveStorePurchasePrices()
        {
            await SeedPriceSyncProductAsync(
                "P-IMPORT-SYNC",
                purchasePrice: 4.28m,
                retailPrice: 11.99m,
                importPrice: 4.28m,
                oemPrice: 11.99m
            );
            await SeedStoreAsync("S01", isActive: true, isDeleted: false);
            await SeedStoreAsync("S02", isActive: true, isDeleted: false);
            await SeedStoreAsync("S03", isActive: false, isDeleted: false);
            await SeedStoreAsync("S04", isActive: true, isDeleted: true);
            await SeedStoreRetailPriceAsync("S01", "P-IMPORT-SYNC", purchasePrice: 4.28m, retailPrice: 11.99m);
            var service = CreateService();

            await service.PatchMobileProductAsync(
                "P-IMPORT-SYNC",
                new WarehouseMobileProductPatchDto
                {
                    PurchasePrice = 5.55m,
                }
            );

            var product = await _db.Queryable<Product>().SingleAsync(x => x.ProductCode == "P-IMPORT-SYNC");
            var warehouseProduct = await _db.Queryable<WarehouseProduct>().SingleAsync(x => x.ProductCode == "P-IMPORT-SYNC");
            var activeStorePrices = await _db.Queryable<StoreRetailPrice>()
                .Where(x => x.ProductCode == "P-IMPORT-SYNC" && !x.IsDeleted)
                .OrderBy(x => x.StoreCode)
                .ToListAsync();

            Assert.Equal(5.55m, product.PurchasePrice);
            Assert.Equal(5.55m, warehouseProduct.ImportPrice);
            Assert.Collection(
                activeStorePrices,
                s01 =>
                {
                    Assert.Equal("S01", s01.StoreCode);
                    Assert.Equal(5.55m, s01.PurchasePrice);
                    Assert.Equal(11.99m, s01.StoreRetailPriceValue);
                    Assert.Equal("MobileWarehousePricePatch", s01.UpdatedBy);
                },
                s02 =>
                {
                    Assert.Equal("S02", s02.StoreCode);
                    Assert.Equal("S02P-IMPORT-SYNC", s02.StoreProductCode);
                    Assert.Equal(5.55m, s02.PurchasePrice);
                    Assert.Equal(11.99m, s02.StoreRetailPriceValue);
                    Assert.Equal("MobileWarehousePricePatch", s02.CreatedBy);
                    Assert.Equal("MobileWarehousePricePatch", s02.UpdatedBy);
                }
            );
        }

        [Fact]
        public async Task BatchUpdateAsync_按数据库货号生成图片地址并覆盖本地商品图片()
        {
            await SeedWarehouseTableProductAsync(
                "P-BATCH-IMAGE",
                "MC 164/3",
                "图片批量测试商品",
                warehouseCategoryGuid: null,
                supplierCode: "SUP-BATCH-IMAGE"
            );
            await _db
                .Updateable<Product>()
                .SetColumns(product => new Product { ProductImage = "https://old/product.jpg" })
                .Where(product => product.ProductCode == "P-BATCH-IMAGE")
                .ExecuteCommandAsync();
            await _db
                .Updateable<DomesticProduct>()
                .SetColumns(product => new DomesticProduct
                {
                    ProductImage = "https://old/domestic.jpg",
                })
                .Where(product => product.ProductCode == "P-BATCH-IMAGE")
                .ExecuteCommandAsync();

            var service = CreateService(
                changeHistoryService: CreateRealChangeHistoryService(
                    userGuid: "batch-image-user-guid",
                    username: "仓库经理A"
                )
            );
            var result = await service.BatchUpdateAsync(
                new List<UpdateItemDto>
                {
                    new() { ProductCode = "P-BATCH-IMAGE" },
                },
                "仓库经理A",
                new WarehouseProductBatchUpdateOptionsDto
                {
                    GenerateImageUrls = true,
                    ImageBaseUrl = "https://images.example.com/catalog///",
                }
            );
            var product = await _db.Queryable<Product>()
                .SingleAsync(item => item.ProductCode == "P-BATCH-IMAGE");
            var domesticProduct = await _db.Queryable<DomesticProduct>()
                .SingleAsync(item => item.ProductCode == "P-BATCH-IMAGE");
            var history = await _db.Queryable<WarehouseProductChangeHistory>()
                .SingleAsync(item => item.ProductCode == "P-BATCH-IMAGE");
            const string expectedUrl = "https://images.example.com/catalog/MC%20164%2F3.jpg";

            Assert.True(result.Success);
            Assert.Equal(1, result.ImageUpdatedCount);
            Assert.Equal(expectedUrl, product.ProductImage);
            Assert.Equal("仓库经理A", product.UpdatedBy);
            Assert.Equal(expectedUrl, domesticProduct.ProductImage);
            Assert.Equal("仓库经理A", domesticProduct.UpdatedBy);
            Assert.Equal("BatchUpdate", history.Action);
            Assert.Contains("\"fieldKey\":\"productImage\"", history.ChangesJson);
        }

        [Fact]
        public async Task BatchUpdateAsync_生成图片时忽略同编码软删除商品并更新有效主档()
        {
            await _db.Insertable(new Product
            {
                UUID = "P-BATCH-IMAGE-ACTIVE-deleted-uuid",
                ProductCode = "P-BATCH-IMAGE-ACTIVE",
                ItemNumber = "DELETED-ITEM",
                ProductName = "已删除历史商品",
                ProductImage = "https://old/deleted-product.jpg",
                IsDeleted = true,
                CreatedAt = DateTime.UtcNow.AddDays(-2),
                UpdatedAt = DateTime.UtcNow.AddDays(1),
            }).ExecuteCommandAsync();
            await SeedWarehouseTableProductAsync(
                "P-BATCH-IMAGE-ACTIVE",
                "ACTIVE-ITEM",
                "有效商品主档",
                warehouseCategoryGuid: null,
                updatedAt: DateTime.UtcNow.AddDays(-1)
            );

            var result = await CreateService().BatchUpdateAsync(
                new List<UpdateItemDto>
                {
                    new() { ProductCode = "P-BATCH-IMAGE-ACTIVE" },
                },
                "仓库经理A",
                new WarehouseProductBatchUpdateOptionsDto
                {
                    GenerateImageUrls = true,
                    ImageBaseUrl = "https://images.example.com/catalog/",
                }
            );

            var activeProduct = await _db.Queryable<Product>()
                .SingleAsync(item =>
                    item.ProductCode == "P-BATCH-IMAGE-ACTIVE" && !item.IsDeleted
                );
            var deletedProduct = await _db.Queryable<Product>()
                .SingleAsync(item =>
                    item.ProductCode == "P-BATCH-IMAGE-ACTIVE" && item.IsDeleted
                );

            Assert.True(result.Success);
            Assert.Equal(1, result.ImageUpdatedCount);
            Assert.Equal(
                "https://images.example.com/catalog/ACTIVE-ITEM.jpg",
                activeProduct.ProductImage
            );
            Assert.Equal("https://old/deleted-product.jpg", deletedProduct.ProductImage);
        }

        [Fact]
        public async Task BatchUpdateAsync_图片基础地址无效时在事务前拒绝且不修改商品()
        {
            await SeedWarehouseTableProductAsync(
                "P-BATCH-IMAGE-INVALID",
                "ITEM-INVALID",
                "图片地址校验商品",
                warehouseCategoryGuid: null,
                domesticPrice: 2.5m
            );
            await _db
                .Updateable<Product>()
                .SetColumns(product => new Product { ProductImage = "https://old/image.jpg" })
                .Where(product => product.ProductCode == "P-BATCH-IMAGE-INVALID")
                .ExecuteCommandAsync();

            var result = await CreateService().BatchUpdateAsync(
                new List<UpdateItemDto>
                {
                    new()
                    {
                        ProductCode = "P-BATCH-IMAGE-INVALID",
                        DomesticPrice = 9.9m,
                    },
                },
                "仓库经理A",
                new WarehouseProductBatchUpdateOptionsDto
                {
                    GenerateImageUrls = true,
                    ImageBaseUrl = "https://images.example.com/catalog/?token=secret",
                }
            );

            var product = await _db.Queryable<Product>()
                .SingleAsync(item => item.ProductCode == "P-BATCH-IMAGE-INVALID");
            var warehouseProduct = await _db.Queryable<WarehouseProduct>()
                .SingleAsync(item => item.ProductCode == "P-BATCH-IMAGE-INVALID");

            Assert.False(result.Success);
            Assert.Equal(1, result.FailedCount);
            Assert.Contains("查询参数", result.Message);
            Assert.Equal("https://old/image.jpg", product.ProductImage);
            Assert.Equal(2.5m, warehouseProduct.DomesticPrice);
        }

        [Fact]
        public async Task BatchUpdateAsync_货号缺失时整项跳过其他字段()
        {
            await SeedWarehouseTableProductAsync(
                "P-BATCH-IMAGE-NO-ITEM",
                "TEMP-ITEM",
                "无货号商品",
                warehouseCategoryGuid: null,
                domesticPrice: 3.5m
            );
            await _db
                .Updateable<Product>()
                .SetColumns(product => new Product
                {
                    ItemNumber = null,
                    ProductImage = "https://old/no-item.jpg",
                })
                .Where(product => product.ProductCode == "P-BATCH-IMAGE-NO-ITEM")
                .ExecuteCommandAsync();

            var result = await CreateService().BatchUpdateAsync(
                new List<UpdateItemDto>
                {
                    new()
                    {
                        ProductCode = "P-BATCH-IMAGE-NO-ITEM",
                        DomesticPrice = 8.8m,
                    },
                },
                "仓库经理A",
                new WarehouseProductBatchUpdateOptionsDto
                {
                    GenerateImageUrls = true,
                    ImageBaseUrl = "https://images.example.com/catalog/",
                }
            );

            var product = await _db.Queryable<Product>()
                .SingleAsync(item => item.ProductCode == "P-BATCH-IMAGE-NO-ITEM");
            var warehouseProduct = await _db.Queryable<WarehouseProduct>()
                .SingleAsync(item => item.ProductCode == "P-BATCH-IMAGE-NO-ITEM");

            Assert.True(result.Success);
            Assert.Equal(0, result.SuccessCount);
            Assert.Equal(1, result.FailedCount);
            Assert.Equal(0, result.ImageUpdatedCount);
            Assert.Contains(result.Errors, error => error.Contains("货号为空"));
            Assert.Equal("https://old/no-item.jpg", product.ProductImage);
            Assert.Equal(3.5m, warehouseProduct.DomesticPrice);
        }

        [Fact]
        public async Task BatchUpdateAsync_生成图片地址超过字段长度时整项跳过()
        {
            await SeedWarehouseTableProductAsync(
                "P-BATCH-IMAGE-LONG",
                "ITEM-NUMBER-IS-TOO-LONG",
                "图片地址超长商品",
                warehouseCategoryGuid: null,
                domesticPrice: 4.5m
            );
            var longBaseUrl = $"https://images.example.com/{new string('a', 170)}/";

            var result = await CreateService().BatchUpdateAsync(
                new List<UpdateItemDto>
                {
                    new()
                    {
                        ProductCode = "P-BATCH-IMAGE-LONG",
                        DomesticPrice = 9.9m,
                    },
                },
                "仓库经理A",
                new WarehouseProductBatchUpdateOptionsDto
                {
                    GenerateImageUrls = true,
                    ImageBaseUrl = longBaseUrl,
                }
            );

            var product = await _db.Queryable<Product>()
                .SingleAsync(item => item.ProductCode == "P-BATCH-IMAGE-LONG");
            var warehouseProduct = await _db.Queryable<WarehouseProduct>()
                .SingleAsync(item => item.ProductCode == "P-BATCH-IMAGE-LONG");

            Assert.True(result.Success);
            Assert.Equal(1, result.FailedCount);
            Assert.Contains(result.Errors, error => error.Contains("超过 200"));
            Assert.Null(product.ProductImage);
            Assert.Equal(4.5m, warehouseProduct.DomesticPrice);
        }

        [Fact]
        public async Task BatchUpdateAsync_生成图片时不修改已软删除国内商品()
        {
            await SeedWarehouseTableProductAsync(
                "P-BATCH-IMAGE-DELETED-DOMESTIC",
                "ITEM-DELETED-DOMESTIC",
                "软删除国内商品",
                warehouseCategoryGuid: null,
                supplierCode: "SUP-BATCH-IMAGE-DELETED",
                domesticProductIsDeleted: true
            );
            await _db
                .Updateable<DomesticProduct>()
                .SetColumns(product => new DomesticProduct
                {
                    ProductImage = "https://old/deleted-domestic.jpg",
                })
                .Where(product => product.ProductCode == "P-BATCH-IMAGE-DELETED-DOMESTIC")
                .ExecuteCommandAsync();

            var result = await CreateService().BatchUpdateAsync(
                new List<UpdateItemDto>
                {
                    new() { ProductCode = "P-BATCH-IMAGE-DELETED-DOMESTIC" },
                },
                "仓库经理A",
                new WarehouseProductBatchUpdateOptionsDto
                {
                    GenerateImageUrls = true,
                    ImageBaseUrl = "https://images.example.com/catalog/",
                }
            );

            var product = await _db.Queryable<Product>()
                .SingleAsync(item => item.ProductCode == "P-BATCH-IMAGE-DELETED-DOMESTIC");
            var deletedDomestic = await _db.Queryable<DomesticProduct>()
                .SingleAsync(item => item.ProductCode == "P-BATCH-IMAGE-DELETED-DOMESTIC");

            Assert.True(result.Success);
            Assert.Equal(
                "https://images.example.com/catalog/ITEM-DELETED-DOMESTIC.jpg",
                product.ProductImage
            );
            Assert.Equal("https://old/deleted-domestic.jpg", deletedDomestic.ProductImage);
            Assert.True(deletedDomestic.IsDeleted);
        }

        [Fact]
        public async Task BatchUpdateAsync_WhenImportPriceChanges_UpdatesWarehouseProductProductAndStoreRetailPrices()
        {
            await SeedPriceSyncProductAsync(
                "P-BATCH-IMPORT-SYNC",
                purchasePrice: 4.28m,
                retailPrice: 11.99m,
                importPrice: 4.28m,
                oemPrice: 11.99m
            );
            await SeedStoreRetailPriceAsync("S01", "P-BATCH-IMPORT-SYNC", purchasePrice: 4.28m, retailPrice: 11.99m);
            var service = CreateService();

            var result = await service.BatchUpdateAsync(
                new List<UpdateItemDto>
                {
                    new()
                    {
                        ProductCode = "P-BATCH-IMPORT-SYNC",
                        ImportPrice = 6.66m,
                        IsActive = true,
                    },
                }
            );

            var product = await _db.Queryable<Product>().SingleAsync(x => x.ProductCode == "P-BATCH-IMPORT-SYNC");
            var warehouseProduct = await _db.Queryable<WarehouseProduct>().SingleAsync(x => x.ProductCode == "P-BATCH-IMPORT-SYNC");
            var storePrice = await _db.Queryable<StoreRetailPrice>().SingleAsync(x => x.ProductCode == "P-BATCH-IMPORT-SYNC");

            Assert.True(result.Success);
            Assert.Equal(1, result.SuccessCount);
            Assert.Equal(6.66m, warehouseProduct.ImportPrice);
            Assert.Equal(6.66m, product.PurchasePrice);
            Assert.Equal(6.66m, storePrice.PurchasePrice);
            Assert.Equal(11.99m, storePrice.StoreRetailPriceValue);
        }

        [Fact]
        public async Task BatchUpdateAsync_WhenStorePurchaseSyncDisabled_DoesNotUpdateStoreRetailPurchasePrice()
        {
            await SeedPriceSyncProductAsync(
                "P-BATCH-IMPORT-NO-STORE",
                purchasePrice: 4.28m,
                retailPrice: 11.99m,
                importPrice: 4.28m,
                oemPrice: 11.99m
            );
            await SeedStoreRetailPriceAsync("S01", "P-BATCH-IMPORT-NO-STORE", purchasePrice: 4.28m, retailPrice: 11.99m);
            await _db.Updateable<WarehouseProduct>()
                .SetColumns(x => new WarehouseProduct { IsActive = false })
                .Where(x => x.ProductCode == "P-BATCH-IMPORT-NO-STORE")
                .ExecuteCommandAsync();
            var service = CreateService();

            var result = await service.BatchUpdateAsync(
                new List<UpdateItemDto>
                {
                    new()
                    {
                        ProductCode = "P-BATCH-IMPORT-NO-STORE",
                        ImportPrice = 6.66m,
                        SyncStorePurchasePrice = false,
                    },
                }
            );

            var product = await _db.Queryable<Product>().SingleAsync(x => x.ProductCode == "P-BATCH-IMPORT-NO-STORE");
            var warehouseProduct = await _db.Queryable<WarehouseProduct>().SingleAsync(x => x.ProductCode == "P-BATCH-IMPORT-NO-STORE");
            var storePrice = await _db.Queryable<StoreRetailPrice>().SingleAsync(x => x.ProductCode == "P-BATCH-IMPORT-NO-STORE");

            Assert.True(result.Success);
            Assert.Equal(1, result.SuccessCount);
            Assert.Equal(6.66m, warehouseProduct.ImportPrice);
            Assert.False(warehouseProduct.IsActive);
            Assert.Equal(6.66m, product.PurchasePrice);
            Assert.Equal(4.28m, storePrice.PurchasePrice);
            Assert.Equal(11.99m, storePrice.StoreRetailPriceValue);
        }

        [Fact]
        public async Task BatchUpdateAsync_WhenAllSevenFieldsProvided_UpdatesWarehouseDomesticAndImportLinkedPrices()
        {
            const string productCode = "P-BATCH-ALL-FIELDS";
            await SeedPriceSyncProductAsync(
                productCode,
                purchasePrice: 4.28m,
                retailPrice: 11.99m,
                importPrice: 4.28m,
                oemPrice: 11.99m
            );
            await SeedStoreRetailPriceAsync(
                "S01",
                productCode,
                purchasePrice: 4.28m,
                retailPrice: 11.99m
            );
            await _db.Insertable(new DomesticProduct
            {
                ProductCode = productCode,
                ProductName = "七字段批量更新商品",
                PackingQuantity = 12,
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();
            var service = CreateService();

            var result = await service.BatchUpdateAsync(
                new List<UpdateItemDto>
                {
                    new()
                    {
                        ProductCode = productCode,
                        DomesticPrice = 8.88m,
                        OEMPrice = 15.55m,
                        ImportPrice = 6.66m,
                        Volume = 0.125m,
                        PackingQuantity = 24,
                        MinOrderQuantity = 3,
                        IsActive = false,
                    },
                }
            );

            var warehouseProduct = await _db.Queryable<WarehouseProduct>()
                .SingleAsync(x => x.ProductCode == productCode);
            var domesticProduct = await _db.Queryable<DomesticProduct>()
                .SingleAsync(x => x.ProductCode == productCode);
            var product = await _db.Queryable<Product>()
                .SingleAsync(x => x.ProductCode == productCode);
            var storeRetailPrice = await _db.Queryable<StoreRetailPrice>()
                .SingleAsync(x => x.ProductCode == productCode);

            Assert.True(result.Success);
            Assert.Equal(1, result.SuccessCount);
            Assert.Equal(8.88m, warehouseProduct.DomesticPrice);
            Assert.Equal(15.55m, warehouseProduct.OEMPrice);
            Assert.Equal(6.66m, warehouseProduct.ImportPrice);
            Assert.Equal(0.125m, warehouseProduct.Volume);
            Assert.Equal(24, warehouseProduct.PackingQuantity);
            Assert.Equal(3, warehouseProduct.MinOrderQuantity);
            Assert.False(warehouseProduct.IsActive);
            Assert.Equal(24, domesticProduct.PackingQuantity);
            Assert.Equal(6.66m, product.PurchasePrice);
            Assert.Equal(6.66m, storeRetailPrice.PurchasePrice);
            Assert.Equal(11.99m, storeRetailPrice.StoreRetailPriceValue);
        }

        [Fact]
        public async Task BatchUpdateAsync_SupplierCode_UpdatesDomesticSupplier()
        {
            const string productCode = "P-BATCH-SUPPLIER";
            await SeedPatchProductAsync(productCode);
            var domesticProduct = await _db.Queryable<DomesticProduct>()
                .SingleAsync(x => x.ProductCode == productCode);
            domesticProduct.SupplierCode = "SUPPLIER-OLD";
            await _db.Updateable(domesticProduct).ExecuteCommandAsync();
            var service = CreateService();

            var result = await service.BatchUpdateAsync(
                new List<UpdateItemDto>
                {
                    new()
                    {
                        ProductCode = productCode,
                        SupplierCode = " SUPPLIER-NEW ",
                    },
                }
            );

            var updatedDomesticProduct = await _db.Queryable<DomesticProduct>()
                .SingleAsync(x => x.ProductCode == productCode);
            Assert.True(result.Success, result.Message);
            Assert.Equal(1, result.SuccessCount);
            Assert.Equal("SUPPLIER-NEW", updatedDomesticProduct.SupplierCode);
        }

        [Fact]
        public async Task BatchUpdateAsync_SupplierCode_CreatesDomesticProductWhenMissing()
        {
            const string productCode = "P-BATCH-SUPPLIER-CREATE";
            await SeedPatchProductAsync(
                productCode,
                seedDomestic: false,
                domesticPrice: 2.2m,
                oemPrice: 5.5m,
                importPrice: 1.1m,
                minOrderQuantity: 12
            );
            var service = CreateService();

            var result = await service.BatchUpdateAsync(
                new List<UpdateItemDto>
                {
                    new()
                    {
                        ProductCode = productCode,
                        SupplierCode = "SUPPLIER-NEW",
                    },
                }
            );

            var domesticProduct = await _db.Queryable<DomesticProduct>()
                .SingleAsync(x => x.ProductCode == productCode);
            Assert.True(result.Success, result.Message);
            Assert.Equal(1, result.SuccessCount);
            Assert.Equal("SUPPLIER-NEW", domesticProduct.SupplierCode);
            Assert.Equal(productCode, domesticProduct.ProductName);
            Assert.Equal($"ITEM-{productCode}", domesticProduct.HBProductNo);
            Assert.Equal($"BAR-{productCode}", domesticProduct.Barcode);
            Assert.Equal(2.2m, domesticProduct.DomesticPrice);
            Assert.Equal(5.5m, domesticProduct.OEMPrice);
            Assert.Equal(1.1m, domesticProduct.ImportPrice);
            Assert.Equal(12, domesticProduct.MiddlePackQuantity);
            Assert.False(domesticProduct.IsDeleted);
        }

        [Fact]
        public async Task BatchUpdateAsync_SupplierCode_RestoresSoftDeletedDomesticProduct()
        {
            const string productCode = "P-BATCH-SUPPLIER-RESTORE";
            await SeedPatchProductAsync(
                productCode,
                domesticPrice: 2.3m,
                oemPrice: 5.6m,
                importPrice: 1.2m,
                minOrderQuantity: 18
            );
            var domesticProduct = await _db.Queryable<DomesticProduct>()
                .SingleAsync(x => x.ProductCode == productCode);
            domesticProduct.SupplierCode = "SUPPLIER-OLD";
            domesticProduct.ProductName = "软删除前旧名称";
            domesticProduct.IsDeleted = true;
            await _db.Updateable(domesticProduct).ExecuteCommandAsync();
            var service = CreateService();

            var result = await service.BatchUpdateAsync(
                new List<UpdateItemDto>
                {
                    new()
                    {
                        ProductCode = productCode,
                        SupplierCode = "SUPPLIER-RESTORED",
                    },
                }
            );

            var domesticProducts = await _db.Queryable<DomesticProduct>()
                .Where(x => x.ProductCode == productCode)
                .ToListAsync();
            var restoredDomesticProduct = Assert.Single(domesticProducts);
            Assert.True(result.Success, result.Message);
            Assert.Equal(1, result.SuccessCount);
            Assert.False(restoredDomesticProduct.IsDeleted);
            Assert.Equal("SUPPLIER-RESTORED", restoredDomesticProduct.SupplierCode);
            Assert.Equal(productCode, restoredDomesticProduct.ProductName);
            Assert.Equal(2.3m, restoredDomesticProduct.DomesticPrice);
            Assert.Equal(5.6m, restoredDomesticProduct.OEMPrice);
            Assert.Equal(1.2m, restoredDomesticProduct.ImportPrice);
            Assert.Equal(18, restoredDomesticProduct.MiddlePackQuantity);
        }

        [Fact]
        public async Task BatchUpdateAsync_UpdatesPackingAndMinOrderQuantityAndKeepsZeroOnNullPatch()
        {
            await SeedPriceSyncProductAsync(
                "P-BATCH-QUANTITY-EXISTING",
                purchasePrice: 4.28m,
                retailPrice: 11.99m,
                importPrice: 4.28m,
                oemPrice: 11.99m
            );
            await _db.Insertable(new DomesticProduct
            {
                ProductCode = "P-BATCH-QUANTITY-EXISTING",
                ProductName = "批量数量更新商品",
                PackingQuantity = 12,
                IsActive = true,
                IsDeleted = false,
                UpdatedAt = new DateTime(2020, 1, 1),
            }).ExecuteCommandAsync();
            await _db.Updateable<WarehouseProduct>()
                .SetColumns(x => new WarehouseProduct
                {
                    PackingQuantity = 12,
                    MinOrderQuantity = 3,
                })
                .Where(x => x.ProductCode == "P-BATCH-QUANTITY-EXISTING")
                .ExecuteCommandAsync();
            var service = CreateService();

            var zeroResult = await service.BatchUpdateAsync(
                new List<UpdateItemDto>
                {
                    new()
                    {
                        ProductCode = "P-BATCH-QUANTITY-EXISTING",
                        PackingQuantity = 0,
                        MinOrderQuantity = 0,
                    },
                }
            );
            var nullResult = await service.BatchUpdateAsync(
                new List<UpdateItemDto>
                {
                    new()
                    {
                        ProductCode = "P-BATCH-QUANTITY-EXISTING",
                        DomesticPrice = 9.99m,
                    },
                }
            );

            var warehouseProduct = await _db.Queryable<WarehouseProduct>()
                .SingleAsync(x => x.ProductCode == "P-BATCH-QUANTITY-EXISTING");
            var domesticProduct = await _db.Queryable<DomesticProduct>()
                .SingleAsync(x => x.ProductCode == "P-BATCH-QUANTITY-EXISTING");

            Assert.True(zeroResult.Success);
            Assert.True(nullResult.Success);
            Assert.Equal(0, warehouseProduct.PackingQuantity);
            Assert.Equal(0, warehouseProduct.MinOrderQuantity);
            Assert.Equal(0, domesticProduct.PackingQuantity);
            Assert.True(domesticProduct.UpdatedAt > new DateTime(2020, 1, 1));
            Assert.Equal(9.99m, warehouseProduct.DomesticPrice);
        }

        [Fact]
        public async Task BatchUpdateAsync_WhenWarehouseProductMissing_CreatesQuantitiesWithoutUpdatingDeletedDomesticProduct()
        {
            const string productCode = "P-BATCH-QUANTITY-NEW";
            await _db.Insertable(new Product
            {
                UUID = $"product-uuid-{productCode}",
                ProductCode = productCode,
                ProductName = "批量新建仓库商品",
                ItemNumber = "ITEM-BATCH-QUANTITY-NEW",
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();
            await _db.Insertable(new DomesticProduct
            {
                ProductCode = productCode,
                ProductName = "已删除国内商品",
                PackingQuantity = 7,
                IsActive = false,
                IsDeleted = true,
            }).ExecuteCommandAsync();
            var service = CreateService();

            var result = await service.BatchUpdateAsync(
                new List<UpdateItemDto>
                {
                    new()
                    {
                        ItemNumber = "ITEM-BATCH-QUANTITY-NEW",
                        PackingQuantity = 18,
                        MinOrderQuantity = 4,
                    },
                }
            );

            var warehouseProduct = await _db.Queryable<WarehouseProduct>()
                .SingleAsync(x => x.ProductCode == productCode);
            var deletedDomesticProduct = await _db.Queryable<DomesticProduct>()
                .SingleAsync(x => x.ProductCode == productCode);

            Assert.True(result.Success);
            Assert.Equal(1, result.SuccessCount);
            Assert.Equal(18, warehouseProduct.PackingQuantity);
            Assert.Equal(4, warehouseProduct.MinOrderQuantity);
            Assert.True(warehouseProduct.IsActive);
            Assert.Equal(7, deletedDomesticProduct.PackingQuantity);
        }

        [Fact]
        public async Task BatchUpdateAsync_WhenExistingProductCodeRepeats_ProcessesFirstAndKeepsOtherItems()
        {
            const string firstCode = "P-BATCH-DUPLICATE-EXISTING";
            const string otherCode = "P-BATCH-DUPLICATE-OTHER";
            await SeedPriceSyncProductAsync(firstCode, 4.28m, 11.99m, 4.28m, 11.99m);
            await SeedPriceSyncProductAsync(otherCode, 4.28m, 11.99m, 4.28m, 11.99m);
            var service = CreateService();

            var result = await service.BatchUpdateAsync(
                new List<UpdateItemDto>
                {
                    new() { ProductCode = firstCode, DomesticPrice = 8.88m },
                    new()
                    {
                        ProductCode = firstCode.ToLowerInvariant(),
                        OEMPrice = 99.99m,
                    },
                    new() { ProductCode = otherCode, DomesticPrice = 7.77m },
                }
            );

            var first = await _db.Queryable<WarehouseProduct>()
                .SingleAsync(x => x.ProductCode == firstCode);
            var other = await _db.Queryable<WarehouseProduct>()
                .SingleAsync(x => x.ProductCode == otherCode);

            Assert.True(result.Success);
            Assert.Equal(2, result.SuccessCount);
            Assert.Equal(1, result.FailedCount);
            Assert.Contains(result.Errors, error => error.Contains("批次内商品编码重复"));
            Assert.Equal(8.88m, first.DomesticPrice);
            Assert.Equal(11.99m, first.OEMPrice);
            Assert.Equal(7.77m, other.DomesticPrice);
        }

        [Fact]
        public async Task BatchUpdateAsync_WritesOneHistoryPerChangedProductWithSharedBatchGuid()
        {
            const string importCode = "P-BATCH-HISTORY-IMPORT";
            const string retailCode = "P-BATCH-HISTORY-RETAIL";
            const string noChangeCode = "P-BATCH-HISTORY-NO-CHANGE";
            await SeedPriceSyncProductAsync(importCode, 4.28m, 11.99m, 4.28m, 11.99m);
            await SeedPriceSyncProductAsync(retailCode, 4.28m, 11.99m, 4.28m, 11.99m);
            await SeedPriceSyncProductAsync(noChangeCode, 4.28m, 11.99m, 4.28m, 11.99m);
            var service = CreateService(
                changeHistoryService: CreateRealChangeHistoryService(
                    userGuid: "history-user-guid",
                    username: "history-current-user"
                )
            );

            var result = await service.BatchUpdateAsync(
                new List<UpdateItemDto>
                {
                    new() { ProductCode = importCode, ImportPrice = 6.66m },
                    new() { ProductCode = retailCode, OEMPrice = 15.55m },
                    new() { ProductCode = noChangeCode, ImportPrice = 4.28m },
                },
                "batch-operator"
            );

            var histories = await _db
                .Queryable<WarehouseProductChangeHistory>()
                .OrderBy(item => item.ProductCode)
                .ToListAsync();

            Assert.True(result.Success);
            Assert.Equal(3, result.SuccessCount);
            Assert.Equal(2, histories.Count);
            Assert.All(histories, history =>
            {
                Assert.Equal("BatchUpdate", history.Action);
                Assert.Equal("WarehouseProducts", history.Source);
                Assert.Equal("batch-operator", history.ActorName);
                Assert.Equal("history-user-guid", history.ActorUserGuid);
                Assert.NotNull(history.BatchGuid);
            });
            Assert.Equal(histories[0].BatchGuid, histories[1].BatchGuid);
            Assert.Contains("\"fieldKey\":\"importPrice\"", histories[0].ChangesJson);
            Assert.Contains("\"fieldKey\":\"retailPrice\"", histories[1].ChangesJson);
            Assert.DoesNotContain(histories, history => history.ProductCode == noChangeCode);
        }

        [Fact]
        public async Task BatchUpdateAsync_WhenHistoryWriteFails_RollsBackAllValidProductsAndClearsSuccessCount()
        {
            const string productCode = "P-BATCH-HISTORY-ROLLBACK";
            await SeedPriceSyncProductAsync(productCode, 4.28m, 11.99m, 4.28m, 11.99m);
            var historyService = new Mock<IWarehouseProductChangeHistoryService>();
            historyService
                .Setup(item =>
                    item.CaptureSnapshotsAsync(
                        It.IsAny<IEnumerable<string>>(),
                        It.IsAny<System.Threading.CancellationToken>()
                    )
                )
                .ReturnsAsync(
                    new Dictionary<string, WarehouseProductChangeSnapshotDto>(
                        StringComparer.OrdinalIgnoreCase
                    )
                );
            historyService
                .Setup(item =>
                    item.RecordChangesAsync(
                        It.IsAny<
                            IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto>
                        >(),
                        It.IsAny<
                            IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto>
                        >(),
                        It.IsAny<WarehouseProductChangeHistoryContextDto>(),
                        It.IsAny<System.Threading.CancellationToken>()
                    )
                )
                .ThrowsAsync(new InvalidOperationException("history insert failed"));
            var service = CreateService(changeHistoryService: historyService.Object);

            var result = await service.BatchUpdateAsync(
                new List<UpdateItemDto>
                {
                    new() { ProductCode = productCode, ImportPrice = 9.99m },
                },
                "batch-operator"
            );
            var warehouseProduct = await _db
                .Queryable<WarehouseProduct>()
                .SingleAsync(item => item.ProductCode == productCode);
            var product = await _db
                .Queryable<Product>()
                .SingleAsync(item => item.ProductCode == productCode);

            Assert.False(result.Success);
            Assert.Equal(0, result.SuccessCount);
            Assert.Equal(4.28m, warehouseProduct.ImportPrice);
            Assert.Equal(4.28m, product.PurchasePrice);
        }

        [Fact]
        public async Task BatchUpdateAsync_WhenMissingProductResolvesByItemAndCode_RejectsDuplicateCreateOnly()
        {
            const string firstCode = "P-BATCH-DUPLICATE-NEW";
            const string otherCode = "P-BATCH-DUPLICATE-NEW-OTHER";
            await _db.Insertable(new List<Product>
            {
                new()
                {
                    UUID = $"product-uuid-{firstCode}",
                    ProductCode = firstCode,
                    ProductName = "待新建重复商品",
                    ItemNumber = "ITEM-BATCH-DUPLICATE-NEW",
                    IsActive = true,
                    IsDeleted = false,
                },
                new()
                {
                    UUID = $"product-uuid-{otherCode}",
                    ProductCode = otherCode,
                    ProductName = "待新建其他商品",
                    ItemNumber = "ITEM-BATCH-DUPLICATE-NEW-OTHER",
                    IsActive = true,
                    IsDeleted = false,
                },
            }).ExecuteCommandAsync();
            var service = CreateService();

            var result = await service.BatchUpdateAsync(
                new List<UpdateItemDto>
                {
                    new()
                    {
                        ItemNumber = "ITEM-BATCH-DUPLICATE-NEW",
                        DomesticPrice = 8.88m,
                    },
                    new() { ProductCode = firstCode, OEMPrice = 99.99m },
                    new() { ProductCode = otherCode, DomesticPrice = 7.77m },
                }
            );

            var first = await _db.Queryable<WarehouseProduct>()
                .SingleAsync(x => x.ProductCode == firstCode);
            var other = await _db.Queryable<WarehouseProduct>()
                .SingleAsync(x => x.ProductCode == otherCode);

            Assert.True(result.Success);
            Assert.Equal(2, result.SuccessCount);
            Assert.Equal(1, result.FailedCount);
            Assert.Contains(result.Errors, error => error.Contains("批次内商品编码重复"));
            Assert.Equal(8.88m, first.DomesticPrice);
            Assert.Null(first.OEMPrice);
            Assert.Equal(7.77m, other.DomesticPrice);
        }

        [Theory]
        [InlineData(-1, null)]
        [InlineData(null, -1)]
        public async Task BatchUpdateAsync_WhenQuantityIsNegative_RejectsWholeItem(
            int? packingQuantity,
            int? minOrderQuantity
        )
        {
            const string productCode = "P-BATCH-NEGATIVE-QUANTITY";
            await SeedPriceSyncProductAsync(
                productCode,
                purchasePrice: 4.28m,
                retailPrice: 11.99m,
                importPrice: 4.28m,
                oemPrice: 11.99m
            );
            await _db.Insertable(new DomesticProduct
            {
                ProductCode = productCode,
                ProductName = "负数校验商品",
                PackingQuantity = 12,
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();
            await _db.Updateable<WarehouseProduct>()
                .SetColumns(x => new WarehouseProduct
                {
                    DomesticPrice = 5.55m,
                    PackingQuantity = 12,
                    MinOrderQuantity = 3,
                    IsActive = true,
                })
                .Where(x => x.ProductCode == productCode)
                .ExecuteCommandAsync();
            var service = CreateService();

            var result = await service.BatchUpdateAsync(
                new List<UpdateItemDto>
                {
                    new()
                    {
                        ProductCode = productCode,
                        DomesticPrice = 9.99m,
                        PackingQuantity = packingQuantity,
                        MinOrderQuantity = minOrderQuantity,
                        IsActive = false,
                    },
                }
            );

            var warehouseProduct = await _db.Queryable<WarehouseProduct>()
                .SingleAsync(x => x.ProductCode == productCode);
            var domesticProduct = await _db.Queryable<DomesticProduct>()
                .SingleAsync(x => x.ProductCode == productCode);

            Assert.Equal(0, result.SuccessCount);
            Assert.Equal(1, result.FailedCount);
            Assert.Contains(result.Errors, error => error.Contains("不能为负数"));
            Assert.Equal(5.55m, warehouseProduct.DomesticPrice);
            Assert.Equal(12, warehouseProduct.PackingQuantity);
            Assert.Equal(3, warehouseProduct.MinOrderQuantity);
            Assert.True(warehouseProduct.IsActive);
            Assert.Equal(12, domesticProduct.PackingQuantity);
        }

        [Fact]
        public async Task BatchUpdateAsync_WhenWarehouseProductMissing_SyncsZeroPackingToActiveDomesticProduct()
        {
            const string productCode = "P-BATCH-QUANTITY-NEW-ACTIVE";
            await _db.Insertable(new Product
            {
                UUID = $"product-uuid-{productCode}",
                ProductCode = productCode,
                ProductName = "新建仓库数量双写商品",
                ItemNumber = "ITEM-BATCH-QUANTITY-NEW-ACTIVE",
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();
            await _db.Insertable(new DomesticProduct
            {
                ProductCode = productCode,
                ProductName = "活跃国内商品",
                PackingQuantity = 7,
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();
            var service = CreateService();

            var result = await service.BatchUpdateAsync(
                new List<UpdateItemDto>
                {
                    new()
                    {
                        ItemNumber = "ITEM-BATCH-QUANTITY-NEW-ACTIVE",
                        PackingQuantity = 0,
                        MinOrderQuantity = 0,
                    },
                }
            );

            var warehouseProduct = await _db.Queryable<WarehouseProduct>()
                .SingleAsync(x => x.ProductCode == productCode);
            var domesticProduct = await _db.Queryable<DomesticProduct>()
                .SingleAsync(x => x.ProductCode == productCode);

            Assert.True(result.Success);
            Assert.Equal(1, result.SuccessCount);
            Assert.Equal(0, warehouseProduct.PackingQuantity);
            Assert.Equal(0, warehouseProduct.MinOrderQuantity);
            Assert.Equal(0, domesticProduct.PackingQuantity);
        }

        [Fact]
        public async Task BatchToggleActiveAsync_只改仓库供货状态_不联动商品主档国内商品分店价与多码()
        {
            await SeedPriceSyncProductAsync(
                "P-TOGGLE-LINKED",
                purchasePrice: 4.28m,
                retailPrice: 11.99m,
                importPrice: 4.28m,
                oemPrice: 11.99m
            );
            await _db.Insertable(new DomesticProduct
            {
                ProductCode = "P-TOGGLE-LINKED",
                ProductName = "Toggle Linked",
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();
            await SeedStoreRetailPriceAsync("S01", "P-TOGGLE-LINKED", purchasePrice: 4.28m, retailPrice: 11.99m);
            await _db.Insertable(new StoreMultiCodeProduct
            {
                UUID = "multi-code-toggle-linked",
                StoreCode = "S01",
                ProductCode = "P-TOGGLE-LINKED",
                MultiBarcode = "BAR-MULTI-TOGGLE",
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();
            var service = CreateService();

            var result = await service.BatchToggleActiveAsync(
                new BatchToggleWarehouseProductsActiveRequestDto
                {
                    ProductCodes = new List<string> { "P-TOGGLE-LINKED" },
                    IsActive = false,
                }
            );

            var warehouseProduct = await _db.Queryable<WarehouseProduct>().SingleAsync(x => x.ProductCode == "P-TOGGLE-LINKED");
            var product = await _db.Queryable<Product>().SingleAsync(x => x.ProductCode == "P-TOGGLE-LINKED");
            var domesticProduct = await _db.Queryable<DomesticProduct>().SingleAsync(x => x.ProductCode == "P-TOGGLE-LINKED");
            var storeRetailPrice = await _db.Queryable<StoreRetailPrice>().SingleAsync(x => x.ProductCode == "P-TOGGLE-LINKED");
            var storeMultiCodeProduct = await _db.Queryable<StoreMultiCodeProduct>().SingleAsync(x => x.ProductCode == "P-TOGGLE-LINKED");

            Assert.True(result.Success);
            Assert.Equal(1, result.SuccessCount);
            Assert.Equal(0, result.FailedCount);
            // 仓库下架只表示“暂停向分店供货”；门店现有库存仍要能在 POS 销售，
            // 因此商品主档、国内商品、分店零售价、分店多码的启用状态都不应被连带关闭。
            Assert.False(warehouseProduct.IsActive);
            Assert.True(product.IsActive);
            Assert.True(domesticProduct.IsActive);
            Assert.True(storeRetailPrice.IsActive);
            Assert.True(storeMultiCodeProduct.IsActive);
            Assert.Null(domesticProduct.UpdatedBy);
        }

        [Fact]
        public async Task BatchToggleActiveAsync_下架登记供货说明_上架关闭说明()
        {
            const string productCode = "P-TOGGLE-NOTICE";
            await BlazorApp.Api.Data.WarehouseProductSupplyNoticeSchemaMigrator.EnsureAsync(_db, NullLogger.Instance);
            await SeedPriceSyncProductAsync(productCode, purchasePrice: 4.28m, retailPrice: 11.99m, importPrice: 4.28m, oemPrice: 11.99m);
            var service = CreateService();

            var delisted = await service.BatchToggleActiveAsync(
                new BatchToggleWarehouseProductsActiveRequestDto
                {
                    ProductCodes = new List<string> { productCode },
                    IsActive = false,
                    SupplyNotice = new WarehouseProductSupplyNoticeInputDto
                    {
                        SupplyPlan = WarehouseProductSupplyPlans.WillRestock,
                        ExpectedPrecision = WarehouseProductSupplyExpectedPrecisions.Range,
                        ExpectedFrom = new DateOnly(2099, 10, 5),
                        ExpectedTo = new DateOnly(2099, 10, 10),
                        StoreFacingNote = "运输途中，入库后开放订货",
                    },
                },
                "仓库员P12"
            );

            Assert.True(delisted.Success, delisted.Message);
            var notice = await _db.Queryable<WarehouseProductSupplyNotice>().SingleAsync(x => x.ProductCode == productCode);
            Assert.Equal(WarehouseProductSupplyPlans.WillRestock, notice.SupplyPlan);
            Assert.Equal(new DateTime(2099, 10, 10), notice.ExpectedTo);
            Assert.Equal("仓库员P12", notice.CreatedBy);
            Assert.Equal("WarehouseProducts", notice.Source);
            Assert.Null(notice.ClosedAtUtc);

            var relisted = await service.BatchToggleActiveAsync(
                new BatchToggleWarehouseProductsActiveRequestDto
                {
                    ProductCodes = new List<string> { productCode },
                    IsActive = true,
                },
                "仓库员P12"
            );

            Assert.True(relisted.Success, relisted.Message);
            notice = await _db.Queryable<WarehouseProductSupplyNotice>().SingleAsync(x => x.ProductCode == productCode);
            Assert.NotNull(notice.ClosedAtUtc);
            Assert.Equal("仓库员P12", notice.ClosedBy);
        }

        [Fact]
        public async Task BatchToggleActiveAsync_供货说明录入无效_整单拒绝且商品保持在架()
        {
            const string productCode = "P-TOGGLE-BAD-NOTICE";
            await BlazorApp.Api.Data.WarehouseProductSupplyNoticeSchemaMigrator.EnsureAsync(_db, NullLogger.Instance);
            await SeedPriceSyncProductAsync(productCode, purchasePrice: 4.28m, retailPrice: 11.99m, importPrice: 4.28m, oemPrice: 11.99m);

            var result = await CreateService().BatchToggleActiveAsync(
                new BatchToggleWarehouseProductsActiveRequestDto
                {
                    ProductCodes = new List<string> { productCode },
                    IsActive = false,
                    SupplyNotice = new WarehouseProductSupplyNoticeInputDto { SupplyPlan = "" },
                }
            );

            Assert.False(result.Success);
            // 不能出现“已下架但说明没记上”的半截状态。
            Assert.True((await _db.Queryable<WarehouseProduct>().SingleAsync(x => x.ProductCode == productCode)).IsActive);
            Assert.Empty(await _db.Queryable<WarehouseProductSupplyNotice>().ToListAsync());
        }

        [Fact]
        public async Task FullUpdateAsync_下架只改仓库供货状态_不联动商品主档国内商品分店价与多码()
        {
            const string productCode = "P-FULL-DELIST";
            await SeedFullUpdateStatusProductAsync(productCode, storeRowsActive: true);
            var service = CreateService();

            var result = await service.FullUpdateAsync(
                productCode,
                new WarehouseProductFullUpdateDto { IsActive = false },
                "仓库员P12"
            );

            Assert.True(result.Success, result.Message);
            Assert.False((await _db.Queryable<WarehouseProduct>().SingleAsync(x => x.ProductCode == productCode)).IsActive);
            Assert.True((await _db.Queryable<Product>().SingleAsync(x => x.ProductCode == productCode)).IsActive);
            Assert.True((await _db.Queryable<DomesticProduct>().SingleAsync(x => x.ProductCode == productCode)).IsActive);
            Assert.True((await _db.Queryable<StoreRetailPrice>().SingleAsync(x => x.ProductCode == productCode)).IsActive);
            Assert.True((await _db.Queryable<StoreMultiCodeProduct>().SingleAsync(x => x.ProductCode == productCode)).IsActive);
        }

        [Fact]
        public async Task FullUpdateAsync_保存在架商品_不会重新启用已停用的分店价与多码()
        {
            const string productCode = "P-FULL-KEEP-OFF";
            await SeedFullUpdateStatusProductAsync(productCode, storeRowsActive: false);
            var service = CreateService();

            var result = await service.FullUpdateAsync(
                productCode,
                new WarehouseProductFullUpdateDto { IsActive = true },
                "仓库员P12"
            );

            Assert.True(result.Success, result.Message);
            Assert.True((await _db.Queryable<WarehouseProduct>().SingleAsync(x => x.ProductCode == productCode)).IsActive);
            // 分店价、多码的停用可能是门店或商品维护有意为之，仓库保存不得把它们重新打开。
            Assert.False((await _db.Queryable<StoreRetailPrice>().SingleAsync(x => x.ProductCode == productCode)).IsActive);
            Assert.False((await _db.Queryable<StoreMultiCodeProduct>().SingleAsync(x => x.ProductCode == productCode)).IsActive);
        }

        private async Task SeedFullUpdateStatusProductAsync(string productCode, bool storeRowsActive)
        {
            await SeedPriceSyncProductAsync(
                productCode,
                purchasePrice: 4.28m,
                retailPrice: 11.99m,
                importPrice: 4.28m,
                oemPrice: 11.99m
            );
            await _db.Insertable(new DomesticProduct
            {
                ProductCode = productCode,
                ProductName = "Full Update Status",
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();
            await SeedStoreRetailPriceAsync("S01", productCode, purchasePrice: 4.28m, retailPrice: 11.99m);
            await _db.Updateable<StoreRetailPrice>()
                .SetColumns(x => x.IsActive == storeRowsActive)
                .Where(x => x.ProductCode == productCode)
                .ExecuteCommandAsync();
            await _db.Insertable(new StoreMultiCodeProduct
            {
                UUID = $"multi-code-{productCode}",
                StoreCode = "S01",
                ProductCode = productCode,
                MultiBarcode = $"BAR-{productCode}",
                IsActive = storeRowsActive,
                IsDeleted = false,
            }).ExecuteCommandAsync();
        }

        [Fact]
        public async Task BatchToggleActiveAsync_WhenWarehouseProductMissing_ReturnsPartialFailure()
        {
            await SeedPriceSyncProductAsync(
                "P-TOGGLE-EXISTS",
                purchasePrice: 4.28m,
                retailPrice: 11.99m,
                importPrice: 4.28m,
                oemPrice: 11.99m
            );
            var service = CreateService();

            var result = await service.BatchToggleActiveAsync(
                new BatchToggleWarehouseProductsActiveRequestDto
                {
                    ProductCodes = new List<string> { "P-TOGGLE-EXISTS", "P-TOGGLE-MISSING" },
                    IsActive = false,
                }
            );

            var warehouseProduct = await _db.Queryable<WarehouseProduct>().SingleAsync(x => x.ProductCode == "P-TOGGLE-EXISTS");

            Assert.False(result.Success);
            Assert.Equal(1, result.SuccessCount);
            Assert.Equal(1, result.FailedCount);
            Assert.Contains("仓库商品不存在: P-TOGGLE-MISSING", result.Errors);
            Assert.False(warehouseProduct.IsActive);
        }

        [Fact]
        public async Task BatchToggleActiveAsync_WhenOnlyLinkedProductExists_DoesNotUpdateLinkedTables()
        {
            await _db.Insertable(new Product
            {
                UUID = "product-toggle-linked-only",
                ProductCode = "P-TOGGLE-LINKED-ONLY",
                ProductName = "Linked Only",
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();
            await _db.Insertable(new DomesticProduct
            {
                ProductCode = "P-TOGGLE-LINKED-ONLY",
                ProductName = "Linked Only Domestic",
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();
            await SeedStoreRetailPriceAsync("S01", "P-TOGGLE-LINKED-ONLY", purchasePrice: 4.28m, retailPrice: 11.99m);
            var service = CreateService();

            var result = await service.BatchToggleActiveAsync(
                new BatchToggleWarehouseProductsActiveRequestDto
                {
                    ProductCodes = new List<string> { "P-TOGGLE-LINKED-ONLY" },
                    IsActive = false,
                }
            );

            var product = await _db.Queryable<Product>().SingleAsync(x => x.ProductCode == "P-TOGGLE-LINKED-ONLY");
            var domesticProduct = await _db.Queryable<DomesticProduct>().SingleAsync(x => x.ProductCode == "P-TOGGLE-LINKED-ONLY");
            var storeRetailPrice = await _db.Queryable<StoreRetailPrice>().SingleAsync(x => x.ProductCode == "P-TOGGLE-LINKED-ONLY");

            Assert.False(result.Success);
            Assert.Equal(0, result.SuccessCount);
            Assert.Equal(1, result.FailedCount);
            Assert.Contains("仓库商品不存在: P-TOGGLE-LINKED-ONLY", result.Errors);
            Assert.True(product.IsActive);
            Assert.True(domesticProduct.IsActive);
            Assert.True(storeRetailPrice.IsActive);
        }

        [Fact]
        public async Task BatchToggleActiveAsync_WhenWarehouseProductDeleted_DoesNotUpdateLinkedTables()
        {
            await SeedPriceSyncProductAsync(
                "P-TOGGLE-DELETED",
                purchasePrice: 4.28m,
                retailPrice: 11.99m,
                importPrice: 4.28m,
                oemPrice: 11.99m
            );
            await _db.Updateable<WarehouseProduct>()
                .SetColumns(w => w.IsDeleted == true)
                .Where(w => w.ProductCode == "P-TOGGLE-DELETED")
                .ExecuteCommandAsync();
            await _db.Insertable(new DomesticProduct
            {
                ProductCode = "P-TOGGLE-DELETED",
                ProductName = "Deleted Warehouse Domestic",
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();
            await SeedStoreRetailPriceAsync("S01", "P-TOGGLE-DELETED", purchasePrice: 4.28m, retailPrice: 11.99m);
            var service = CreateService();

            var result = await service.BatchToggleActiveAsync(
                new BatchToggleWarehouseProductsActiveRequestDto
                {
                    ProductCodes = new List<string> { "P-TOGGLE-DELETED" },
                    IsActive = false,
                }
            );

            var warehouseProduct = await _db.Queryable<WarehouseProduct>().SingleAsync(x => x.ProductCode == "P-TOGGLE-DELETED");
            var product = await _db.Queryable<Product>().SingleAsync(x => x.ProductCode == "P-TOGGLE-DELETED");
            var domesticProduct = await _db.Queryable<DomesticProduct>().SingleAsync(x => x.ProductCode == "P-TOGGLE-DELETED");
            var storeRetailPrice = await _db.Queryable<StoreRetailPrice>().SingleAsync(x => x.ProductCode == "P-TOGGLE-DELETED");

            Assert.False(result.Success);
            Assert.Equal(0, result.SuccessCount);
            Assert.Equal(1, result.FailedCount);
            Assert.Contains("仓库商品不存在: P-TOGGLE-DELETED", result.Errors);
            Assert.True(warehouseProduct.IsActive);
            Assert.True(product.IsActive);
            Assert.True(domesticProduct.IsActive);
            Assert.True(storeRetailPrice.IsActive);
        }

        [Fact]
        public async Task PatchMobileProductAsync_WhenRetailPriceChangesWithoutStoreSync_DoesNotTouchStoreRetailPrices()
        {
            await SeedPriceSyncProductAsync(
                "P-RETAIL-NO-SYNC",
                purchasePrice: 4.28m,
                retailPrice: 11.99m,
                importPrice: 4.28m,
                oemPrice: 11.99m
            );
            await SeedStoreAsync("S01", isActive: true, isDeleted: false);
            await SeedStoreAsync("S02", isActive: true, isDeleted: false);
            await SeedStoreRetailPriceAsync("S01", "P-RETAIL-NO-SYNC", purchasePrice: 4.28m, retailPrice: 11.99m);
            var service = CreateService();

            await service.PatchMobileProductAsync(
                "P-RETAIL-NO-SYNC",
                new WarehouseMobileProductPatchDto
                {
                    RetailPrice = 12.99m,
                    SyncStoreRetailPrices = false,
                }
            );

            var product = await _db.Queryable<Product>().SingleAsync(x => x.ProductCode == "P-RETAIL-NO-SYNC");
            var warehouseProduct = await _db.Queryable<WarehouseProduct>().SingleAsync(x => x.ProductCode == "P-RETAIL-NO-SYNC");
            var storePrices = await _db.Queryable<StoreRetailPrice>()
                .Where(x => x.ProductCode == "P-RETAIL-NO-SYNC" && !x.IsDeleted)
                .ToListAsync();

            Assert.Equal(12.99m, product.RetailPrice);
            Assert.Equal(12.99m, warehouseProduct.OEMPrice);
            var storePrice = Assert.Single(storePrices);
            Assert.Equal("S01", storePrice.StoreCode);
            Assert.Equal(11.99m, storePrice.StoreRetailPriceValue);
        }

        [Fact]
        public async Task PatchMobileProductAsync_WhenRetailPriceChangesWithStoreSync_UpdatesAllActiveStoreRetailPrices()
        {
            await SeedPriceSyncProductAsync(
                "P-RETAIL-SYNC",
                purchasePrice: 4.28m,
                retailPrice: 11.99m,
                importPrice: 4.28m,
                oemPrice: 11.99m
            );
            await SeedStoreAsync("S01", isActive: true, isDeleted: false);
            await SeedStoreAsync("S02", isActive: true, isDeleted: false);
            await SeedStoreAsync("S03", isActive: false, isDeleted: false);
            await SeedStoreRetailPriceAsync("S01", "P-RETAIL-SYNC", purchasePrice: 4.28m, retailPrice: 11.99m);
            var service = CreateService();

            await service.PatchMobileProductAsync(
                "P-RETAIL-SYNC",
                new WarehouseMobileProductPatchDto
                {
                    OEMPrice = 12.99m,
                    SyncStoreRetailPrices = true,
                }
            );

            var product = await _db.Queryable<Product>().SingleAsync(x => x.ProductCode == "P-RETAIL-SYNC");
            var warehouseProduct = await _db.Queryable<WarehouseProduct>().SingleAsync(x => x.ProductCode == "P-RETAIL-SYNC");
            var activeStorePrices = await _db.Queryable<StoreRetailPrice>()
                .Where(x => x.ProductCode == "P-RETAIL-SYNC" && !x.IsDeleted)
                .OrderBy(x => x.StoreCode)
                .ToListAsync();

            Assert.Equal(12.99m, product.RetailPrice);
            Assert.Equal(12.99m, warehouseProduct.OEMPrice);
            Assert.Collection(
                activeStorePrices,
                s01 =>
                {
                    Assert.Equal("S01", s01.StoreCode);
                    Assert.Equal(12.99m, s01.StoreRetailPriceValue);
                    Assert.Equal(4.28m, s01.PurchasePrice);
                    Assert.Equal("MobileWarehousePricePatch", s01.UpdatedBy);
                },
                s02 =>
                {
                    Assert.Equal("S02", s02.StoreCode);
                    Assert.Equal("S02P-RETAIL-SYNC", s02.StoreProductCode);
                    Assert.Equal(12.99m, s02.StoreRetailPriceValue);
                    Assert.Equal(4.28m, s02.PurchasePrice);
                    Assert.Equal("MobileWarehousePricePatch", s02.CreatedBy);
                    Assert.Equal("MobileWarehousePricePatch", s02.UpdatedBy);
                }
            );
        }

        [Fact]
        public async Task PatchMobileProductAsync_WhenLinkedPurchasePricesConflict_Throws()
        {
            await SeedPriceSyncProductAsync(
                "P-PURCHASE-CONFLICT",
                purchasePrice: 4.28m,
                retailPrice: 11.99m,
                importPrice: 4.28m,
                oemPrice: 11.99m
            );
            var service = CreateService();

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.PatchMobileProductAsync(
                    "P-PURCHASE-CONFLICT",
                    new WarehouseMobileProductPatchDto
                    {
                        PurchasePrice = 5.55m,
                        ImportPrice = 6.66m,
                    }
                )
            );

            Assert.Equal("进货价和进口价不一致", error.Message);
        }

        [Fact]
        public async Task PatchMobileProductAsync_WhenLinkedRetailPricesConflict_Throws()
        {
            await SeedPriceSyncProductAsync(
                "P-RETAIL-CONFLICT",
                purchasePrice: 4.28m,
                retailPrice: 11.99m,
                importPrice: 4.28m,
                oemPrice: 11.99m
            );
            var service = CreateService();

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.PatchMobileProductAsync(
                    "P-RETAIL-CONFLICT",
                    new WarehouseMobileProductPatchDto
                    {
                        RetailPrice = 12.99m,
                        OEMPrice = 13.99m,
                    }
                )
            );

            Assert.Equal("零售价和RRP不一致", error.Message);
        }

        [Fact]
        public async Task SetMobileProductLocationAsync_AllowsOccupiedStorageLocation()
        {
            await SeedWarehouseProductAsync("P-STORAGE-TARGET", "ITEM-STORAGE-TARGET", "BAR-STORAGE-TARGET");
            await SeedWarehouseProductAsync("P-STORAGE-OTHER", "ITEM-STORAGE-OTHER", "BAR-STORAGE-OTHER");
            await SeedLocationAsync("loc-storage-used", "A-00-00-03", 2);
            await _db.Insertable(new ProductLocation
            {
                Guid = "product-location-storage-used",
                ProductCode = "P-STORAGE-OTHER",
                LocationGuid = "loc-storage-used",
                IsDeleted = false,
            }).ExecuteCommandAsync();
            var service = CreateService();

            var result = await service.SetMobileProductLocationAsync("P-STORAGE-TARGET", "loc-storage-used");

            Assert.NotNull(result);
            Assert.Equal("loc-storage-used", result.LocationGuid);
            var boundCount = await _db.Queryable<ProductLocation>()
                .Where(pl => !pl.IsDeleted && pl.LocationGuid == "loc-storage-used")
                .CountAsync();
            Assert.Equal(2, boundCount);
        }

        [Fact]
        public async Task SetMobileProductLocationAsync_ThrowsWhenLocationMissing()
        {
            await SeedWarehouseProductAsync("P-MISSING-LOC", "ITEM-MISSING-LOC", "BAR-MISSING-LOC");
            var service = CreateService();

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.SetMobileProductLocationAsync("P-MISSING-LOC", "loc-missing")
            );

            Assert.Equal("货位不存在", error.Message);
        }

        [Fact]
        public async Task ImportFromDomesticAsync_已有英文名_商品主档使用英文名称()
        {
            await SeedDomesticImportProductAsync(
                productCode: "P-IMPORT-EN",
                productName: "夜光麦芽糖",
                englishName: "Glow-in-the-Dark Malts"
            );
            var service = CreateService();

            var result = await service.ImportFromDomesticAsync(new ImportFromDomesticRequestDto
            {
                ProductCodes = new List<string> { "P-IMPORT-EN" },
            });

            Assert.True(result.Success);
            Assert.Equal(1, result.SuccessCount);
            var product = await _db.Queryable<Product>()
                .Where(p => p.ProductCode == "P-IMPORT-EN")
                .SingleAsync();
            Assert.Equal("Glow-in-the-Dark Malts", product.ProductName);
            Assert.Equal("Glow-in-the-Dark Malts", product.EnglishName);
        }

        [Fact]
        public async Task ImportFromDomesticAsync_缺英文名_自动翻译并写回国内商品()
        {
            await SeedDomesticImportProductAsync(
                productCode: "P-IMPORT-TRANSLATE",
                productName: "光变爆珠5.5",
                englishName: null
            );
            var translationService = new Mock<ITranslationService>();
            translationService
                .Setup(x => x.BatchTranslateToEnglishAsync(It.IsAny<List<string>>()))
                .ReturnsAsync(new Dictionary<string, string>
                {
                    ["光变爆珠5.5"] = "Light-Changing Bursting Beads 5.5",
                });
            translationService
                .Setup(x => x.ContainsChinese(It.IsAny<string>()))
                .Returns<string>(ContainsChineseForTest);
            var service = CreateService(translationService.Object);

            var result = await service.ImportFromDomesticAsync(new ImportFromDomesticRequestDto
            {
                ProductCodes = new List<string> { "P-IMPORT-TRANSLATE" },
            });

            Assert.True(result.Success);
            var product = await _db.Queryable<Product>()
                .Where(p => p.ProductCode == "P-IMPORT-TRANSLATE")
                .SingleAsync();
            Assert.Equal("Light-Changing Bursting Beads 5.5", product.ProductName);
            Assert.Equal("Light-Changing Bursting Beads 5.5", product.EnglishName);
            var domestic = await _db.Queryable<DomesticProduct>()
                .Where(p => p.ProductCode == "P-IMPORT-TRANSLATE")
                .SingleAsync();
            Assert.Equal("Light-Changing Bursting Beads 5.5", domestic.EnglishProductName);
            translationService.Verify(x => x.BatchTranslateToEnglishAsync(
                It.Is<List<string>>(texts => texts.Contains("光变爆珠5.5"))),
                Times.Once);
        }

        [Fact]
        public async Task ImportFromDomesticAsync_翻译仍含中文_不污染英文字段()
        {
            await SeedDomesticImportProductAsync(
                productCode: "P-IMPORT-CHINESE-TRANSLATION",
                productName: "大黄油",
                englishName: null
            );
            var translationService = new Mock<ITranslationService>();
            translationService
                .Setup(x => x.BatchTranslateToEnglishAsync(It.IsAny<List<string>>()))
                .ReturnsAsync(new Dictionary<string, string> { ["大黄油"] = "大黄油" });
            translationService
                .Setup(x => x.ContainsChinese(It.IsAny<string>()))
                .Returns<string>(ContainsChineseForTest);
            var service = CreateService(translationService.Object);

            var result = await service.ImportFromDomesticAsync(new ImportFromDomesticRequestDto
            {
                ProductCodes = new List<string> { "P-IMPORT-CHINESE-TRANSLATION" },
            });

            Assert.True(result.Success);
            var product = await _db.Queryable<Product>()
                .Where(p => p.ProductCode == "P-IMPORT-CHINESE-TRANSLATION")
                .SingleAsync();
            Assert.Equal("大黄油", product.ProductName);
            Assert.Null(product.EnglishName);
            var domestic = await _db.Queryable<DomesticProduct>()
                .Where(p => p.ProductCode == "P-IMPORT-CHINESE-TRANSLATION")
                .SingleAsync();
            Assert.Null(domestic.EnglishProductName);
        }

        [Fact]
        public async Task ImportFromDomesticAsync_已有商品仍是国内中文名_智能补英文()
        {
            await SeedDomesticImportProductAsync(
                productCode: "P-IMPORT-EXISTING-CHINESE",
                productName: "5.5果胶",
                englishName: "5.5 Fruit Gel"
            );
            await _db.Insertable(new Product
            {
                UUID = "product-existing-chinese",
                ProductCode = "P-IMPORT-EXISTING-CHINESE",
                ProductName = "5.5果胶",
                EnglishName = null,
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();
            var service = CreateService();

            var result = await service.ImportFromDomesticAsync(new ImportFromDomesticRequestDto
            {
                ProductCodes = new List<string> { "P-IMPORT-EXISTING-CHINESE" },
            });

            Assert.True(result.Success);
            var product = await _db.Queryable<Product>()
                .Where(p => p.ProductCode == "P-IMPORT-EXISTING-CHINESE")
                .SingleAsync();
            Assert.Equal("5.5 Fruit Gel", product.ProductName);
            Assert.Equal("5.5 Fruit Gel", product.EnglishName);
        }

        [Fact]
        public async Task ImportFromDomesticAsync_已有商品人工名称_不覆盖商品名称()
        {
            await SeedDomesticImportProductAsync(
                productCode: "P-IMPORT-CUSTOM",
                productName: "小熊",
                englishName: "Bear"
            );
            await _db.Insertable(new Product
            {
                UUID = "product-existing-custom",
                ProductCode = "P-IMPORT-CUSTOM",
                ProductName = "Custom Display Name",
                EnglishName = "Custom English",
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();
            var service = CreateService();

            var result = await service.ImportFromDomesticAsync(new ImportFromDomesticRequestDto
            {
                ProductCodes = new List<string> { "P-IMPORT-CUSTOM" },
            });

            Assert.True(result.Success);
            var product = await _db.Queryable<Product>()
                .Where(p => p.ProductCode == "P-IMPORT-CUSTOM")
                .SingleAsync();
            Assert.Equal("Custom Display Name", product.ProductName);
            Assert.Equal("Custom English", product.EnglishName);
        }

        [Fact]
        public async Task GetDomesticProductsNotInWarehouseAsync_弹窗商品名称优先显示英文()
        {
            await SeedDomesticImportProductAsync(
                productCode: "P-IMPORT-LIST",
                productName: "夜光5.5",
                englishName: "Glow-in-the-Dark 5.5"
            );
            var service = CreateService();

            var result = await service.GetDomesticProductsNotInWarehouseAsync(
                new GetDomesticProductsNotInWarehouseRequestDto
                {
                    Page = 1,
                    PageSize = 20,
                    GlobalSearch = "P-IMPORT-LIST",
                }
            );

            var item = Assert.Single(result.Items);
            Assert.Equal("Glow-in-the-Dark 5.5", item.ProductName);
            Assert.Equal("Glow-in-the-Dark 5.5", item.EnglishName);
        }

        [Fact]
        public async Task GetMobileLocationPrintPayloadAsync_ReturnsProductDescriptionAndMiddlePackage()
        {
            await _db.Insertable(new Product
            {
                UUID = "product-mobile-location-label",
                ProductCode = "P-LABEL-001",
                ProductName = "3D TOYS",
                ItemNumber = "HB313-129",
                Barcode = "9525813130129",
                MiddlePackageQuantity = 24,
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();
            await _db.Insertable(new WarehouseProduct
            {
                ProductCode = "P-LABEL-001",
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();
            await _db.Insertable(new Location
            {
                LocationGuid = "loc-label-001",
                LocationCode = "A-00-00-01",
                LocationBarcode = "5544492778828",
                LocationType = 1,
                Status = 1,
                IsDeleted = false,
            }).ExecuteCommandAsync();
            await _db.Insertable(new ProductLocation
            {
                Guid = "pl-label-001",
                ProductCode = "P-LABEL-001",
                LocationGuid = "loc-label-001",
                IsDeleted = false,
            }).ExecuteCommandAsync();
            var service = CreateService();

            var payload = await service.GetMobileLocationPrintPayloadAsync("P-LABEL-001");

            Assert.NotNull(payload);
            Assert.Equal("loc-label-001", payload!.LocationGuid);
            Assert.Equal("A-00-00-01", payload.LocationCode);
            Assert.Equal("5544492778828", payload.LocationBarcode);
            Assert.Equal("HB313-129", payload.ItemNumber);
            Assert.Equal("3D TOYS", payload.ProductName);
            Assert.Equal(24, payload.MiddlePackageQuantity);
            Assert.Equal(1, payload.ProductCount);
        }

        [Fact]
        public async Task PatchAsync_MinOrderQuantity_UpdatesWarehouseAndDomesticMiddlePackQuantity()
        {
            const string productCode = "P-PATCH-MIN";
            await SeedPatchProductAsync(
                productCode,
                minOrderQuantity: 3,
                middlePackQuantity: 7,
                productMiddlePackageQuantity: 9,
                domesticPrice: 1.1m,
                oemPrice: 2.2m,
                importPrice: 3.3m
            );
            var service = CreateService();

            var result = await service.PatchAsync(
                productCode,
                new WarehouseProductPatchDto { MinOrderQuantity = 5 },
                "仓库员P1"
            );

            Assert.NotNull(result);
            Assert.True(result!.Success, result.Message);
            var warehouseProduct = await _db.Queryable<WarehouseProduct>()
                .SingleAsync(x => x.ProductCode == productCode);
            var domesticProduct = await _db.Queryable<DomesticProduct>()
                .SingleAsync(x => x.ProductCode == productCode);
            var product = await _db.Queryable<Product>()
                .SingleAsync(x => x.ProductCode == productCode);

            Assert.Equal(5, warehouseProduct.MinOrderQuantity);
            Assert.Equal(5, domesticProduct.MiddlePackQuantity);
            Assert.Equal(9, product.MiddlePackageQuantity);
            Assert.Equal("仓库员P1", warehouseProduct.UpdatedBy);
            Assert.Equal("仓库员P1", domesticProduct.UpdatedBy);
            Assert.Equal(2.2m, warehouseProduct.OEMPrice);
            Assert.Equal(3.3m, warehouseProduct.ImportPrice);
            Assert.Equal(2.2m, domesticProduct.OEMPrice);
            Assert.Equal(3.3m, domesticProduct.ImportPrice);
        }

        [Fact]
        public async Task PatchAsync_MinOrderQuantity_ZeroAccepted_AndNoDomesticProductCreated()
        {
            const string productCode = "P-PATCH-MIN-ZERO";
            await SeedPatchProductAsync(productCode, seedDomestic: false);
            var service = CreateService();

            var result = await service.PatchAsync(
                productCode,
                new WarehouseProductPatchDto { MinOrderQuantity = 0 },
                "仓库员P2"
            );

            Assert.NotNull(result);
            Assert.True(result!.Success, result.Message);
            var warehouseProduct = await _db.Queryable<WarehouseProduct>()
                .SingleAsync(x => x.ProductCode == productCode);
            Assert.Equal(0, warehouseProduct.MinOrderQuantity);
            var domesticCount = await _db.Queryable<DomesticProduct>()
                .Where(x => x.ProductCode == productCode)
                .CountAsync();
            Assert.Equal(0, domesticCount);
        }

        [Fact]
        public async Task PatchAsync_DomesticPrice_UpdatesWarehouseAndValidDomesticOnly()
        {
            const string productCode = "P-PATCH-DOMESTIC";
            await SeedPatchProductAsync(
                productCode,
                domesticPrice: 1.1m,
                oemPrice: 2.2m,
                importPrice: 3.3m,
                productPurchasePrice: 3.3m,
                productRetailPrice: 2.2m
            );
            var service = CreateService();

            var result = await service.PatchAsync(
                productCode,
                new WarehouseProductPatchDto { DomesticPrice = 4.4m },
                "仓库员P3"
            );

            Assert.NotNull(result);
            Assert.True(result!.Success, result.Message);
            var warehouseProduct = await _db.Queryable<WarehouseProduct>()
                .SingleAsync(x => x.ProductCode == productCode);
            var domesticProduct = await _db.Queryable<DomesticProduct>()
                .SingleAsync(x => x.ProductCode == productCode);
            var product = await _db.Queryable<Product>()
                .SingleAsync(x => x.ProductCode == productCode);

            Assert.Equal(4.4m, warehouseProduct.DomesticPrice);
            Assert.Equal(4.4m, domesticProduct.DomesticPrice);
            Assert.Equal(2.2m, warehouseProduct.OEMPrice);
            Assert.Equal(3.3m, warehouseProduct.ImportPrice);
            Assert.Equal(2.2m, domesticProduct.OEMPrice);
            Assert.Equal(3.3m, domesticProduct.ImportPrice);
            Assert.Equal(3.3m, product.PurchasePrice);
            Assert.Equal(2.2m, product.RetailPrice);
        }

        [Fact]
        public async Task PatchAsync_OEMPrice_自动下发后为价格变化的分店登记待换标签_特殊商品不建任务()
        {
            const string productCode = "P-PATCH-NOTIFY";
            await SeedPatchProductAsync(
                productCode,
                domesticPrice: 1.1m,
                oemPrice: 10m,
                importPrice: 3.3m,
                productPurchasePrice: 3.3m,
                productRetailPrice: 10m
            );
            await SeedPriceNotificationStoresAsync(productCode);
            var (service, summary) = CreatePriceNotifyingService();

            var result = await service.PatchAsync(
                productCode,
                new WarehouseProductPatchDto { OEMPrice = 12m },
                "张伟"
            );

            Assert.NotNull(result);
            Assert.True(result!.Success, result.Message);
            var tasks = await _db.Queryable<StorePriceUpdateTask>()
                .Where(task => task.ProductCode == productCode)
                .ToListAsync();
            // N1：10 → 12 被自动下发覆盖，货架标签仍是 10 → 待换标签
            // N2：原本就是 12，标签没过期 → 不建任务；N3：特殊商品 → 不建任务
            var task = Assert.Single(tasks);
            Assert.Equal("N1", task.StoreCode);
            Assert.Equal(StorePriceUpdateTaskKinds.LabelOnly, task.Kind);
            Assert.Equal(10m, task.ShelfRetailPrice);
            Assert.Equal(12m, task.StoreRetailPrice);
            Assert.Equal("张伟", task.InitiatorName);
            Assert.Equal(1, task.ChangeCount);
            Assert.Equal(1, summary.GetSummary()!.LabelOnlyStores);
        }

        /// <summary>三家分店：N1 与旧价一致（会被覆盖）、N2 已是新价、N3 为特殊商品。</summary>
        private async Task SeedPriceNotificationStoresAsync(string productCode)
        {
            await BlazorApp.Api.Data.StorePriceUpdateTaskSchemaMigrator.EnsureAsync(_db, NullLogger.Instance);
            foreach (var (storeCode, retail, special) in new[] { ("N1", 10m, false), ("N2", 12m, false), ("N3", 9m, true) })
            {
                await _db.Insertable(new Store { StoreCode = storeCode, StoreName = storeCode, IsActive = true })
                    .ExecuteCommandAsync();
                await _db.Insertable(
                        new StoreRetailPrice
                        {
                            StoreCode = storeCode,
                            ProductCode = productCode,
                            StoreProductCode = storeCode + productCode,
                            StoreRetailPriceValue = retail,
                            IsSpecialProduct = special,
                            IsActive = true,
                        }
                    )
                    .ExecuteCommandAsync();
            }
        }

        /// <summary>按生产方式装配：审计服务与仓库商品服务都注入真实的价格任务服务。</summary>
        private (ProductWarehouseReactService Service, BlazorApp.Api.Services.React.PriceNotificationSummaryAccessor Summary)
            CreatePriceNotifyingService()
        {
            var configuration = new ConfigurationBuilder().Build();
            var context = CreateSqlSugarContext(_db);
            var summary = new BlazorApp.Api.Services.React.PriceNotificationSummaryAccessor();
            var taskService = new BlazorApp.Api.Services.React.StorePriceUpdateTaskService(
                context,
                NullLogger<BlazorApp.Api.Services.React.StorePriceUpdateTaskService>.Instance,
                configuration,
                summary,
                Mock.Of<IServiceProvider>()
            );
            var historyService = new WarehouseProductChangeHistoryService(
                context,
                NullLogger<WarehouseProductChangeHistoryService>.Instance,
                Mock.Of<ICurrentUserService>(),
                taskService
            );
            var service = new ProductWarehouseReactService(
                context,
                CreateHqSqlSugarContext(),
                NullLogger<ProductWarehouseReactService>.Instance,
                configuration,
                new ItemBarcodeService(context, NullLogger<ItemBarcodeService>.Instance, configuration),
                Mock.Of<IMapper>(),
                Mock.Of<IDataSyncFullService>(),
                historyService,
                CreateDefaultTranslationService(),
                taskService
            );
            return (service, summary);
        }

        [Fact]
        public async Task FullUpdateAsync_Web编辑改零售价覆盖分店后_为价格变化的分店登记待换标签()
        {
            // 回归：Web 编辑表单走 full-update，它自己覆盖全部分店零售价，不经过 UpsertActiveStoreRetailPricesAsync。
            const string productCode = "P-FULL-NOTIFY";
            await SeedPriceSyncProductAsync(productCode, 3.3m, 10m, 3.3m, 10m);
            await SeedPriceNotificationStoresAsync(productCode);
            var (service, summary) = CreatePriceNotifyingService();

            var result = await service.FullUpdateAsync(
                productCode,
                new WarehouseProductFullUpdateDto { IsActive = true, ProductType = 0, OEMPrice = 12m },
                "admin"
            );

            Assert.True(result.Success, result.Message);
            Assert.All(
                await _db.Queryable<StoreRetailPrice>().Where(x => x.ProductCode == productCode).ToListAsync(),
                price => Assert.Equal(12m, price.StoreRetailPriceValue)
            );
            var tasks = await _db.Queryable<StorePriceUpdateTask>()
                .Where(task => task.ProductCode == productCode)
                .ToListAsync();
            // N1：货架标签 10 → 现价 12 → 待换标签；N2 原本就是 12 → 不建；N3 特殊商品 → 不建
            var task = Assert.Single(tasks);
            Assert.Equal("N1", task.StoreCode);
            Assert.Equal(StorePriceUpdateTaskStatuses.Pending, task.Status);
            Assert.Equal(StorePriceUpdateTaskKinds.LabelOnly, task.Kind);
            Assert.Equal(10m, task.ShelfRetailPrice);
            Assert.Equal(12m, task.StoreRetailPrice);
            Assert.Equal("admin", task.InitiatorName);
            Assert.Equal(1, summary.GetSummary()!.LabelOnlyStores);
        }

        [Fact]
        public async Task PatchAsync_MinOrderQuantity_NarrowUpdateDoesNotWriteOtherWarehouseColumns()
        {
            const string productCode = "P-PATCH-NARROW-MIN";
            await SeedPatchProductAsync(
                productCode,
                domesticPrice: 1.1m,
                oemPrice: 2.2m,
                importPrice: 3.3m
            );
            await _db.Ado.ExecuteCommandAsync(
                $"""
                CREATE TRIGGER trg_patch_min_must_not_write_other_warehouse_columns
                BEFORE UPDATE OF DomesticPrice, OEMPrice, ImportPrice, StockQuantity, PackingQuantity, Volume ON WarehouseProduct
                WHEN OLD.ProductCode = '{productCode}'
                BEGIN
                    SELECT RAISE(ABORT, 'MinOrderQuantity PATCH 不应写入其他仓库列');
                END;
                """
            );

            var result = await CreateService().PatchAsync(
                productCode,
                new WarehouseProductPatchDto { MinOrderQuantity = 5 },
                "仓库员P4"
            );

            Assert.NotNull(result);
            Assert.True(result!.Success, result.Message);
        }

        [Fact]
        public async Task PatchAsync_DomesticPrice_NarrowUpdateDoesNotWriteOtherDomesticColumns()
        {
            const string productCode = "P-PATCH-NARROW-DOMESTIC";
            await SeedPatchProductAsync(
                productCode,
                domesticPrice: 1.1m,
                oemPrice: 2.2m,
                importPrice: 3.3m
            );
            await _db.Ado.ExecuteCommandAsync(
                $"""
                CREATE TRIGGER trg_patch_domestic_must_not_write_other_domestic_columns
                BEFORE UPDATE OF OEMPrice, ImportPrice, PackingQuantity, UnitVolume, MiddlePackQuantity ON DomesticProduct
                WHEN OLD.ProductCode = '{productCode}'
                BEGIN
                    SELECT RAISE(ABORT, 'DomesticPrice PATCH 不应写入其他国内商品列');
                END;
                """
            );

            var result = await CreateService().PatchAsync(
                productCode,
                new WarehouseProductPatchDto { DomesticPrice = 4.4m },
                "仓库员P5"
            );

            Assert.NotNull(result);
            Assert.True(result!.Success, result.Message);
        }

        [Fact]
        public async Task PatchAsync_DomesticPrice_WhenDomesticUpdateFails_RollsBackWarehouseUpdate()
        {
            const string productCode = "P-PATCH-ROLLBACK-DOMESTIC";
            await SeedPatchProductAsync(productCode, domesticPrice: 1.1m);
            await _db.Ado.ExecuteCommandAsync(
                $"""
                CREATE TRIGGER trg_patch_domestic_force_rollback
                BEFORE UPDATE OF DomesticPrice ON DomesticProduct
                WHEN OLD.ProductCode = '{productCode}'
                BEGIN
                    SELECT RAISE(ABORT, '强制国内商品更新失败');
                END;
                """
            );

            await Assert.ThrowsAnyAsync<Exception>(() =>
                CreateService().PatchAsync(
                    productCode,
                    new WarehouseProductPatchDto { DomesticPrice = 4.4m },
                    "仓库员回滚测试"
                )
            );

            var warehouseProduct = await _db.Queryable<WarehouseProduct>()
                .SingleAsync(x => x.ProductCode == productCode);
            var domesticProduct = await _db.Queryable<DomesticProduct>()
                .SingleAsync(x => x.ProductCode == productCode);
            Assert.Equal(1.1m, warehouseProduct.DomesticPrice);
            Assert.Equal(1.1m, domesticProduct.DomesticPrice);
        }

        [Fact]
        public async Task PatchAsync_ImportPrice_UpdatesPurchasePriceRangeAndCreatesMissingStoreRows()
        {
            const string productCode = "P-PATCH-IMPORT";
            await SeedPatchProductAsync(
                productCode,
                domesticPrice: 1.1m,
                oemPrice: 2.2m,
                importPrice: 3.3m,
                productPurchasePrice: 3.3m,
                productRetailPrice: 2.2m
            );
            await SeedStoreAsync("S01", isActive: true, isDeleted: false);
            await SeedStoreAsync("S02", isActive: true, isDeleted: false);
            await SeedStoreAsync("S03", isActive: false, isDeleted: false);
            await SeedStoreAsync("S04", isActive: true, isDeleted: true);
            await SeedStoreRetailPriceAsync("S01", productCode, purchasePrice: 3.3m, retailPrice: 2.2m);
            await SeedStoreRetailPriceAsync("S03", productCode, purchasePrice: 9.9m, retailPrice: 8.8m);
            await SeedStoreRetailPriceAsync("S04", productCode, purchasePrice: 9.9m, retailPrice: 8.8m);
            var service = CreateService();

            var result = await service.PatchAsync(
                productCode,
                new WarehouseProductPatchDto { ImportPrice = 5.55m },
                "仓库员P6"
            );

            Assert.NotNull(result);
            Assert.True(result!.Success, result.Message);
            var product = await _db.Queryable<Product>()
                .SingleAsync(x => x.ProductCode == productCode);
            var warehouseProduct = await _db.Queryable<WarehouseProduct>()
                .SingleAsync(x => x.ProductCode == productCode);
            var domesticProduct = await _db.Queryable<DomesticProduct>()
                .SingleAsync(x => x.ProductCode == productCode);
            var prices = await _db.Queryable<StoreRetailPrice>()
                .Where(x => x.ProductCode == productCode)
                .OrderBy(x => x.StoreCode)
                .ToListAsync();

            Assert.Equal(5.55m, product.PurchasePrice);
            Assert.Equal(2.2m, product.RetailPrice);
            Assert.Equal(5.55m, warehouseProduct.ImportPrice);
            Assert.Equal(5.55m, domesticProduct.ImportPrice);
            Assert.Equal(2.2m, warehouseProduct.OEMPrice);
            Assert.Equal(2.2m, domesticProduct.OEMPrice);

            var s01 = Assert.Single(prices, x => x.StoreCode == "S01" && !x.IsDeleted);
            Assert.Equal(5.55m, s01.PurchasePrice);
            Assert.Equal(2.2m, s01.StoreRetailPriceValue);
            Assert.Equal("仓库员P6", s01.UpdatedBy);

            var s02 = Assert.Single(prices, x => x.StoreCode == "S02" && !x.IsDeleted);
            Assert.Equal("S02" + productCode, s02.StoreProductCode);
            Assert.Equal(5.55m, s02.PurchasePrice);
            Assert.Equal(2.2m, s02.StoreRetailPriceValue);
            Assert.Equal("仓库员P6", s02.CreatedBy);
            Assert.Equal("仓库员P6", s02.UpdatedBy);

            var s03 = Assert.Single(prices, x => x.StoreCode == "S03");
            Assert.Equal(9.9m, s03.PurchasePrice);
            Assert.Equal(8.8m, s03.StoreRetailPriceValue);

            var s04 = Assert.Single(prices, x => x.StoreCode == "S04");
            Assert.Equal(9.9m, s04.PurchasePrice);
            Assert.Equal(8.8m, s04.StoreRetailPriceValue);
        }

        [Fact]
        public async Task PatchAsync_ImportPrice_DoesNotResurrectSoftDeletedStorePrice()
        {
            const string productCode = "P-PATCH-IMPORT-SOFT";
            await SeedPatchProductAsync(
                productCode,
                productPurchasePrice: 3.3m,
                productRetailPrice: 2.2m
            );
            await SeedStoreAsync("S01", isActive: true, isDeleted: false);
            await SeedStoreRetailPriceAsync(
                "S01",
                productCode,
                purchasePrice: 1.1m,
                retailPrice: 2.2m,
                isDeleted: true
            );
            var service = CreateService();

            var result = await service.PatchAsync(
                productCode,
                new WarehouseProductPatchDto { ImportPrice = 5.55m },
                "仓库员P7"
            );

            Assert.NotNull(result);
            Assert.True(result!.Success, result.Message);
            var prices = await _db.Queryable<StoreRetailPrice>()
                .Where(x => x.ProductCode == productCode)
                .OrderBy(x => x.StoreCode)
                .ToListAsync();
            var softDeleted = Assert.Single(prices, x => x.IsDeleted);
            Assert.Equal(1.1m, softDeleted.PurchasePrice);
            Assert.Equal(2.2m, softDeleted.StoreRetailPriceValue);
            var active = Assert.Single(prices, x => !x.IsDeleted);
            Assert.Equal(5.55m, active.PurchasePrice);
            Assert.Equal("仓库员P7", active.UpdatedBy);
        }

        [Fact]
        public async Task PatchAsync_OEMPrice_UpdatesRetailPriceRangeWithoutTouchingPurchasePrice()
        {
            const string productCode = "P-PATCH-OEM";
            await SeedPatchProductAsync(
                productCode,
                domesticPrice: 1.1m,
                oemPrice: 2.2m,
                importPrice: 3.3m,
                productPurchasePrice: 3.3m,
                productRetailPrice: 2.2m
            );
            await SeedStoreAsync("S01", isActive: true, isDeleted: false);
            await SeedStoreAsync("S02", isActive: true, isDeleted: false);
            await SeedStoreRetailPriceAsync("S01", productCode, purchasePrice: 3.3m, retailPrice: 2.2m);
            var service = CreateService();

            var result = await service.PatchAsync(
                productCode,
                new WarehouseProductPatchDto { OEMPrice = 6.66m },
                "仓库员P8"
            );

            Assert.NotNull(result);
            Assert.True(result!.Success, result.Message);
            var product = await _db.Queryable<Product>()
                .SingleAsync(x => x.ProductCode == productCode);
            var warehouseProduct = await _db.Queryable<WarehouseProduct>()
                .SingleAsync(x => x.ProductCode == productCode);
            var domesticProduct = await _db.Queryable<DomesticProduct>()
                .SingleAsync(x => x.ProductCode == productCode);
            var prices = await _db.Queryable<StoreRetailPrice>()
                .Where(x => x.ProductCode == productCode)
                .OrderBy(x => x.StoreCode)
                .ToListAsync();

            Assert.Equal(6.66m, product.RetailPrice);
            Assert.Equal(3.3m, product.PurchasePrice);
            Assert.Equal(6.66m, warehouseProduct.OEMPrice);
            Assert.Equal(6.66m, domesticProduct.OEMPrice);
            Assert.Equal(3.3m, warehouseProduct.ImportPrice);
            Assert.Equal(3.3m, domesticProduct.ImportPrice);

            var s01 = Assert.Single(prices, x => x.StoreCode == "S01" && !x.IsDeleted);
            Assert.Equal(6.66m, s01.StoreRetailPriceValue);
            Assert.Equal(3.3m, s01.PurchasePrice);
            Assert.Equal("仓库员P8", s01.UpdatedBy);

            var s02 = Assert.Single(prices, x => x.StoreCode == "S02" && !x.IsDeleted);
            Assert.Equal(6.66m, s02.StoreRetailPriceValue);
            Assert.Equal(3.3m, s02.PurchasePrice);
            Assert.Equal("仓库员P8", s02.CreatedBy);
            Assert.Equal("仓库员P8", s02.UpdatedBy);
        }

        [Fact]
        public async Task PatchAsync_OEMPrice_WhenMasterPurchaseChangesAfterInitialRead_UsesCurrentPurchaseForMissingStoreRow()
        {
            const string productCode = "P-PATCH-OEM-CURRENT-MASTER";
            await SeedPatchProductAsync(
                productCode,
                productPurchasePrice: 3.3m,
                productRetailPrice: 2.2m
            );
            await SeedStoreAsync("S01", isActive: true, isDeleted: false);
            await _db.Ado.ExecuteCommandAsync(
                $"""
                CREATE TRIGGER trg_patch_oem_refresh_master_price
                AFTER UPDATE OF OEMPrice ON WarehouseProduct
                WHEN OLD.ProductCode = '{productCode}'
                BEGIN
                    UPDATE Product SET PurchasePrice = 9.9 WHERE ProductCode = '{productCode}';
                END;
                """
            );

            var result = await CreateService().PatchAsync(
                productCode,
                new WarehouseProductPatchDto { OEMPrice = 6.66m },
                "仓库员并发测试"
            );

            Assert.NotNull(result);
            Assert.True(result!.Success, result.Message);
            var price = await _db.Queryable<StoreRetailPrice>()
                .SingleAsync(x => x.ProductCode == productCode && x.StoreCode == "S01" && !x.IsDeleted);
            Assert.Equal(9.9m, price.PurchasePrice);
            Assert.Equal(6.66m, price.StoreRetailPriceValue);
        }

        [Fact]
        public async Task PatchAsync_WhenProductBecomesDeletedAfterWarehouseUpdate_RollsBackAndReturnsMissing()
        {
            const string productCode = "P-PATCH-CONCURRENT-DELETE";
            await SeedPatchProductAsync(productCode, domesticPrice: 1.1m);
            await _db.Ado.ExecuteCommandAsync(
                $"""
                CREATE TRIGGER trg_patch_soft_delete_product
                AFTER UPDATE OF DomesticPrice ON WarehouseProduct
                WHEN OLD.ProductCode = '{productCode}'
                BEGIN
                    UPDATE Product SET IsDeleted = 1 WHERE ProductCode = '{productCode}';
                END;
                """
            );

            var result = await CreateService().PatchAsync(
                productCode,
                new WarehouseProductPatchDto { DomesticPrice = 4.4m },
                "仓库员删除竞态"
            );

            Assert.Null(result);
            var warehouseProduct = await _db.Queryable<WarehouseProduct>()
                .SingleAsync(x => x.ProductCode == productCode);
            var domesticProduct = await _db.Queryable<DomesticProduct>()
                .SingleAsync(x => x.ProductCode == productCode);
            var product = await _db.Queryable<Product>()
                .SingleAsync(x => x.ProductCode == productCode);
            Assert.Equal(1.1m, warehouseProduct.DomesticPrice);
            Assert.Equal(1.1m, domesticProduct.DomesticPrice);
            Assert.False(product.IsDeleted);
        }

        [Fact]
        public async Task PatchAsync_ImportPrice_NarrowUpdateDoesNotWriteStoreRetailPriceValue()
        {
            const string productCode = "P-PATCH-IMPORT-NARROW";
            await SeedPatchProductAsync(
                productCode,
                productPurchasePrice: 3.3m,
                productRetailPrice: 2.2m
            );
            await SeedStoreAsync("S01", isActive: true, isDeleted: false);
            await SeedStoreRetailPriceAsync("S01", productCode, purchasePrice: 3.3m, retailPrice: 2.2m);
            await _db.Ado.ExecuteCommandAsync(
                $"""
                CREATE TRIGGER trg_patch_import_must_not_write_retail_price
                BEFORE UPDATE OF StoreRetailPriceValue ON StoreRetailPrice
                WHEN OLD.ProductCode = '{productCode}'
                BEGIN
                    SELECT RAISE(ABORT, 'ImportPrice PATCH 不应写入分店零售价');
                END;
                """
            );

            var result = await CreateService().PatchAsync(
                productCode,
                new WarehouseProductPatchDto { ImportPrice = 5.55m },
                "仓库员P9"
            );

            Assert.NotNull(result);
            Assert.True(result!.Success, result.Message);
        }

        [Fact]
        public async Task PatchAsync_OEMPrice_NarrowUpdateDoesNotWriteStorePurchasePrice()
        {
            const string productCode = "P-PATCH-OEM-NARROW";
            await SeedPatchProductAsync(
                productCode,
                productPurchasePrice: 3.3m,
                productRetailPrice: 2.2m
            );
            await SeedStoreAsync("S01", isActive: true, isDeleted: false);
            await SeedStoreRetailPriceAsync("S01", productCode, purchasePrice: 3.3m, retailPrice: 2.2m);
            await _db.Ado.ExecuteCommandAsync(
                $"""
                CREATE TRIGGER trg_patch_oem_must_not_write_purchase_price
                BEFORE UPDATE OF PurchasePrice ON StoreRetailPrice
                WHEN OLD.ProductCode = '{productCode}'
                BEGIN
                    SELECT RAISE(ABORT, 'OEMPrice PATCH 不应写入分店进货价');
                END;
                """
            );

            var result = await CreateService().PatchAsync(
                productCode,
                new WarehouseProductPatchDto { OEMPrice = 6.66m },
                "仓库员P10"
            );

            Assert.NotNull(result);
            Assert.True(result!.Success, result.Message);
        }

        [Theory]
        [MemberData(nameof(InvalidPatchDtos))]
        public async Task PatchAsync_InvalidDto_ThrowsInvalidOperationException(WarehouseProductPatchDto dto)
        {
            const string productCode = "P-PATCH-INVALID";
            await SeedPatchProductAsync(productCode);
            var service = CreateService();

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.PatchAsync(productCode, dto)
            );
        }

        [Theory]
        [InlineData(true, false)]
        [InlineData(false, true)]
        public async Task PatchAsync_ProductOrWarehouseProductMissing_ReturnsNull(
            bool seedProduct,
            bool seedWarehouse
        )
        {
            const string productCode = "P-PATCH-MISSING";
            if (seedProduct)
            {
                await _db.Insertable(new Product
                {
                    UUID = $"patch-{productCode}-uuid",
                    ProductCode = productCode,
                    ProductName = productCode,
                    ItemNumber = $"ITEM-{productCode}",
                    Barcode = $"BAR-{productCode}",
                    IsActive = true,
                    IsDeleted = false,
                }).ExecuteCommandAsync();
            }
            if (seedWarehouse)
            {
                await _db.Insertable(new WarehouseProduct
                {
                    ProductCode = productCode,
                    IsActive = true,
                    IsDeleted = false,
                }).ExecuteCommandAsync();
            }
            var service = CreateService();

            var result = await service.PatchAsync(
                productCode,
                new WarehouseProductPatchDto { DomesticPrice = 1m },
                "仓库员P11"
            );

            Assert.Null(result);
        }

        [Fact]
        public async Task FullUpdateAsync_MinOrderQuantity_SyncsDomesticMiddlePackQuantityWithoutChangingProductMiddlePackageQuantity()
        {
            const string productCode = "P-FULL-MIN";
            await SeedPatchProductAsync(
                productCode,
                minOrderQuantity: 3,
                middlePackQuantity: 7,
                productMiddlePackageQuantity: 9
            );
            var service = CreateService();

            var result = await service.FullUpdateAsync(
                productCode,
                new WarehouseProductFullUpdateDto
                {
                    MinOrderQuantity = 4,
                    IsActive = true,
                },
                "仓库员P12"
            );

            Assert.True(result.Success, result.Message);
            var warehouseProduct = await _db.Queryable<WarehouseProduct>()
                .SingleAsync(x => x.ProductCode == productCode);
            var domesticProduct = await _db.Queryable<DomesticProduct>()
                .SingleAsync(x => x.ProductCode == productCode);
            var product = await _db.Queryable<Product>()
                .SingleAsync(x => x.ProductCode == productCode);

            Assert.Equal(4, warehouseProduct.MinOrderQuantity);
            Assert.Equal(4, domesticProduct.MiddlePackQuantity);
            Assert.Equal(9, product.MiddlePackageQuantity);
        }

        [Fact]
        public async Task FullUpdateAsync_套装子项按兄弟零售价分摊且不被主商品价格覆盖()
        {
            const string productCode = "P-FULL-SET-COST";
            await SeedPatchProductAsync(
                productCode,
                importPrice: 9m,
                oemPrice: 45m,
                productPurchasePrice: 9m,
                productRetailPrice: 45m
            );
            await SeedStoreAsync("S01", isActive: true, isDeleted: false);
            await SeedStoreRetailPriceAsync("S01", productCode, purchasePrice: 9m, retailPrice: 45m);
            await _db.Insertable(new[]
            {
                new ProductSetCode
                {
                    SetCodeId = "SET-A",
                    ProductCode = productCode,
                    SetProductCode = "CHILD-A",
                    SetItemNumber = "ITEM-A",
                    SetBarcode = "BAR-A",
                    SetPurchasePrice = 99m,
                    SetRetailPrice = 20m,
                    SetQuantity = 1,
                    SetType = 1,
                    IsActive = true,
                    IsDeleted = false,
                    CreatedAt = DateTime.UtcNow,
                },
                new ProductSetCode
                {
                    SetCodeId = "SET-B",
                    ProductCode = productCode,
                    SetProductCode = "CHILD-B",
                    SetItemNumber = "ITEM-B",
                    SetBarcode = "BAR-B",
                    SetPurchasePrice = 99m,
                    SetRetailPrice = 30m,
                    SetQuantity = 1,
                    SetType = 1,
                    IsActive = true,
                    IsDeleted = false,
                    CreatedAt = DateTime.UtcNow,
                },
            }).ExecuteCommandAsync();
            await _db.Insertable(new[]
            {
                new StoreMultiCodeProduct
                {
                    UUID = "STORE-A",
                    StoreCode = "S01",
                    ProductCode = productCode,
                    MultiCodeProductCode = "CHILD-A",
                    StoreMultiCodeProductCode = "S01CHILD-A",
                    MultiBarcode = "BAR-A",
                    PurchasePrice = 50m,
                    MultiCodeRetailPrice = 20m,
                    IsActive = true,
                    IsDeleted = false,
                    CreatedAt = DateTime.UtcNow,
                },
                new StoreMultiCodeProduct
                {
                    UUID = "STORE-B",
                    StoreCode = "S01",
                    ProductCode = productCode,
                    MultiCodeProductCode = "CHILD-B",
                    StoreMultiCodeProductCode = "S01CHILD-B",
                    MultiBarcode = "BAR-B",
                    PurchasePrice = 50m,
                    MultiCodeRetailPrice = 30m,
                    IsActive = true,
                    IsDeleted = false,
                    CreatedAt = DateTime.UtcNow,
                },
            }).ExecuteCommandAsync();

            var result = await CreateService().FullUpdateAsync(
                productCode,
                new WarehouseProductFullUpdateDto
                {
                    ImportPrice = 10m,
                    OEMPrice = 50m,
                    ProductType = 1,
                    IsActive = true,
                },
                "仓库员-套装成本"
            );

            Assert.True(result.Success, result.Message);
            var setRows = await _db.Queryable<ProductSetCode>()
                .Where(x => x.ProductCode == productCode)
                .OrderBy(x => x.SetProductCode)
                .ToListAsync();
            Assert.Equal(new decimal?[] { 4m, 6m }, setRows.Select(x => x.SetPurchasePrice));

            var storeRows = await _db.Queryable<StoreMultiCodeProduct>()
                .Where(x => x.ProductCode == productCode && x.StoreCode == "S01")
                .OrderBy(x => x.MultiCodeProductCode)
                .ToListAsync();
            Assert.Equal(new decimal?[] { 4m, 6m }, storeRows.Select(x => x.PurchasePrice));
            Assert.Equal(new decimal?[] { 20m, 30m }, storeRows.Select(x => x.MultiCodeRetailPrice));
        }

        [Fact]
        public async Task FullUpdateAsync_SupplierCode_UpdatesDomesticSupplier()
        {
            const string productCode = "P-FULL-SUPPLIER";
            await SeedPatchProductAsync(productCode);
            var domesticProduct = await _db.Queryable<DomesticProduct>()
                .SingleAsync(x => x.ProductCode == productCode);
            domesticProduct.SupplierCode = "SUPPLIER-OLD";
            await _db.Updateable(domesticProduct).ExecuteCommandAsync();
            var service = CreateService();

            var result = await service.FullUpdateAsync(
                productCode,
                new WarehouseProductFullUpdateDto
                {
                    SupplierCode = "SUPPLIER-NEW",
                    IsActive = true,
                },
                "仓库员P13"
            );

            Assert.True(result.Success, result.Message);
            var updatedDomesticProduct = await _db.Queryable<DomesticProduct>()
                .SingleAsync(x => x.ProductCode == productCode);
            Assert.Equal("SUPPLIER-NEW", updatedDomesticProduct.SupplierCode);
        }

        [Fact]
        public async Task FullUpdateAsync_SupplierCode_CreatesDomesticProductWhenMissing()
        {
            const string productCode = "P-FULL-SUPPLIER-CREATE";
            await SeedPatchProductAsync(
                productCode,
                seedDomestic: false,
                domesticPrice: 2.2m,
                oemPrice: 5.5m,
                importPrice: 1.1m,
                minOrderQuantity: 12
            );
            var service = CreateService();

            var result = await service.FullUpdateAsync(
                productCode,
                new WarehouseProductFullUpdateDto
                {
                    SupplierCode = "SUPPLIER-NEW",
                    ProductName = "新国内商品",
                    IsActive = true,
                },
                "仓库员P14"
            );

            Assert.True(result.Success, result.Message);
            var domesticProduct = await _db.Queryable<DomesticProduct>()
                .SingleAsync(x => x.ProductCode == productCode);
            Assert.Equal("SUPPLIER-NEW", domesticProduct.SupplierCode);
            Assert.Equal("新国内商品", domesticProduct.ProductName);
            Assert.Equal($"ITEM-{productCode}", domesticProduct.HBProductNo);
            Assert.Equal($"BAR-{productCode}", domesticProduct.Barcode);
            Assert.Equal(2.2m, domesticProduct.DomesticPrice);
            Assert.Equal(5.5m, domesticProduct.OEMPrice);
            Assert.Equal(1.1m, domesticProduct.ImportPrice);
            Assert.Equal(12, domesticProduct.MiddlePackQuantity);
            Assert.False(domesticProduct.IsDeleted);
            Assert.Equal("仓库员P14", domesticProduct.CreatedBy);
            Assert.Equal("仓库员P14", domesticProduct.UpdatedBy);
        }

        [Fact]
        public async Task FullUpdateAsync_SupplierCode_RestoresSoftDeletedDomesticProduct()
        {
            const string productCode = "P-FULL-SUPPLIER-RESTORE";
            await SeedPatchProductAsync(
                productCode,
                domesticPrice: 2.3m,
                oemPrice: 5.6m,
                importPrice: 1.2m,
                minOrderQuantity: 18
            );
            var domesticProduct = await _db.Queryable<DomesticProduct>()
                .SingleAsync(x => x.ProductCode == productCode);
            domesticProduct.SupplierCode = "SUPPLIER-OLD";
            domesticProduct.ProductName = "软删除前旧名称";
            domesticProduct.DomesticPrice = 99m;
            domesticProduct.OEMPrice = 99m;
            domesticProduct.ImportPrice = 99m;
            domesticProduct.MiddlePackQuantity = 99;
            domesticProduct.IsDeleted = true;
            await _db.Updateable(domesticProduct).ExecuteCommandAsync();
            var service = CreateService();

            var result = await service.FullUpdateAsync(
                productCode,
                new WarehouseProductFullUpdateDto
                {
                    SupplierCode = "SUPPLIER-RESTORED",
                    IsActive = true,
                },
                "仓库员P15"
            );

            Assert.True(result.Success, result.Message);
            var domesticProducts = await _db.Queryable<DomesticProduct>()
                .Where(x => x.ProductCode == productCode)
                .ToListAsync();
            var restoredDomesticProduct = Assert.Single(domesticProducts);
            Assert.False(restoredDomesticProduct.IsDeleted);
            Assert.Equal("SUPPLIER-RESTORED", restoredDomesticProduct.SupplierCode);
            Assert.Equal(productCode, restoredDomesticProduct.ProductName);
            Assert.Equal(2.3m, restoredDomesticProduct.DomesticPrice);
            Assert.Equal(5.6m, restoredDomesticProduct.OEMPrice);
            Assert.Equal(1.2m, restoredDomesticProduct.ImportPrice);
            Assert.Equal(18, restoredDomesticProduct.MiddlePackQuantity);
            Assert.Equal("仓库员P15", restoredDomesticProduct.UpdatedBy);
        }

        [Fact]
        public async Task FullUpdateAsync_WithoutSupplier_DoesNotCreateDomesticProduct()
        {
            const string productCode = "P-FULL-NO-SUPPLIER";
            await SeedPatchProductAsync(productCode, seedDomestic: false);
            var service = CreateService();

            var result = await service.FullUpdateAsync(
                productCode,
                new WarehouseProductFullUpdateDto
                {
                    ProductName = "仅修改名称",
                    IsActive = true,
                },
                "仓库员P16"
            );

            Assert.True(result.Success, result.Message);
            var domesticProductCount = await _db.Queryable<DomesticProduct>()
                .CountAsync(x => x.ProductCode == productCode);
            Assert.Equal(0, domesticProductCount);
        }

        public void Dispose()
        {
            _db.Dispose();
            _sqliteConnection.Dispose();

            if (File.Exists(_dbPath))
            {
                SqliteTempFileCleanup.DeleteIfExists(_dbPath);
            }
        }

        private async Task SeedWarehouseProductAsync(
            string productCode,
            string itemNumber,
            string barcode
        )
        {
            await _db.Insertable(new Product
            {
                UUID = $"product-uuid-{productCode}",
                ProductCode = productCode,
                ProductName = productCode,
                ItemNumber = itemNumber,
                Barcode = barcode,
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();

            await _db.Insertable(new WarehouseProduct
            {
                ProductCode = productCode,
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();
        }

        private async Task SeedWarehouseCategoryAsync(
            string categoryGuid,
            string? parentGuid,
            string categoryName
        )
        {
            await _db.Insertable(new WarehouseCategory
            {
                CategoryGUID = categoryGuid,
                ParentGUID = parentGuid,
                CategoryName = categoryName,
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();
        }

        private async Task SeedWarehouseTableProductAsync(
            string productCode,
            string itemNumber,
            string productName,
            string? warehouseCategoryGuid,
            string? englishName = null,
            string? barcode = null,
            string? supplierCode = null,
            string? supplierName = null,
            string? localSupplierCode = null,
            string? localSupplierName = null,
            int? productType = null,
            bool isActive = true,
            decimal? domesticPrice = null,
            decimal? oemPrice = null,
            decimal? importPrice = null,
            int? minOrderQuantity = null,
            int? packingQuantity = null,
            int? warehousePackingQuantity = null,
            decimal? volume = null,
            bool seedLocalSupplierRow = true,
            bool localSupplierIsDeleted = false,
            DateTime? createdAt = null,
            DateTime? updatedAt = null,
            string? createdBy = null,
            string? updatedBy = null,
            decimal? domesticProductPrice = null,
            bool domesticProductIsDeleted = false
        )
        {
            if (!string.IsNullOrWhiteSpace(supplierCode))
            {
                var existingSupplier = await _db.Queryable<ChinaSupplier>()
                    .AnyAsync(s => s.SupplierCode == supplierCode);
                if (!existingSupplier)
                {
                    await _db
                        .Insertable(new ChinaSupplier
                        {
                            Guid = $"{supplierCode}-guid",
                            SupplierCode = supplierCode,
                            SupplierName = supplierName ?? supplierCode,
                            Status = 1,
                            IsDeleted = false,
                        })
                        .ExecuteCommandAsync();
                }

                await _db
                    .Insertable(new DomesticProduct
                    {
                        ProductCode = productCode,
                        SupplierCode = supplierCode,
                        ProductName = productName,
                        EnglishProductName = englishName,
                        HBProductNo = itemNumber,
                        Barcode = barcode,
                        ProductType = productType ?? 0,
                        DomesticPrice = domesticProductPrice,
                        PackingQuantity = packingQuantity,
                        UnitVolume = volume,
                        IsActive = isActive,
                        IsDeleted = domesticProductIsDeleted,
                    })
                    .ExecuteCommandAsync();
            }

            if (!string.IsNullOrWhiteSpace(localSupplierCode) && seedLocalSupplierRow)
            {
                var existingLocalSupplier = await _db.Queryable<HBLocalSupplier>()
                    .AnyAsync(s => s.LocalSupplierCode == localSupplierCode);
                if (!existingLocalSupplier)
                {
                    await _db
                        .Insertable(new HBLocalSupplier
                        {
                            Guid = $"{localSupplierCode}-guid",
                            LocalSupplierCode = localSupplierCode,
                            Name = localSupplierName ?? localSupplierCode,
                            Status = 1,
                            IsDeleted = localSupplierIsDeleted,
                        })
                        .ExecuteCommandAsync();
                }
            }

            await _db.Insertable(new Product
            {
                UUID = $"{productCode}-uuid",
                ProductCode = productCode,
                ItemNumber = itemNumber,
                ProductName = productName,
                EnglishName = englishName,
                Barcode = barcode,
                LocalSupplierCode = localSupplierCode,
                WarehouseCategoryGUID = warehouseCategoryGuid,
                ProductType = productType,
                IsActive = isActive,
                CreatedAt = createdAt ?? DateTime.UtcNow,
                UpdatedAt = updatedAt ?? DateTime.UtcNow,
                IsDeleted = false,
            }).ExecuteCommandAsync();

            await _db.Insertable(new WarehouseProduct
            {
                ProductCode = productCode,
                DomesticPrice = domesticPrice,
                OEMPrice = oemPrice,
                ImportPrice = importPrice,
                MinOrderQuantity = minOrderQuantity,
                PackingQuantity = warehousePackingQuantity ?? packingQuantity,
                Volume = volume,
                IsActive = isActive,
                CreatedAt = createdAt ?? DateTime.UtcNow,
                UpdatedAt = updatedAt ?? DateTime.UtcNow,
                CreatedBy = createdBy,
                UpdatedBy = updatedBy,
                IsDeleted = false,
            }).ExecuteCommandAsync();
        }

        private async Task SeedLocationAsync(string locationGuid, string locationCode, int locationType)
        {
            await _db.Insertable(new Location
            {
                LocationGuid = locationGuid,
                LocationCode = locationCode,
                LocationBarcode = $"{locationCode}-BAR",
                LocationType = locationType,
                Status = 1,
                IsDeleted = false,
            }).ExecuteCommandAsync();
        }

        private async Task SeedProductLocationAsync(string productCode, string locationGuid)
        {
            await _db.Insertable(new ProductLocation
            {
                Guid = $"{productCode}-{locationGuid}",
                ProductCode = productCode,
                LocationGuid = locationGuid,
                IsDeleted = false,
            }).ExecuteCommandAsync();
        }

        private async Task SeedPriceSyncProductAsync(
            string productCode,
            decimal purchasePrice,
            decimal retailPrice,
            decimal importPrice,
            decimal oemPrice
        )
        {
            await _db.Insertable(new Product
            {
                UUID = $"product-uuid-{productCode}",
                ProductCode = productCode,
                ProductName = productCode,
                ItemNumber = $"ITEM-{productCode}",
                Barcode = $"BAR-{productCode}",
                LocalSupplierCode = "LOCAL-01",
                PurchasePrice = purchasePrice,
                RetailPrice = retailPrice,
                IsActive = true,
                IsAutoPricing = false,
                IsSpecialProduct = false,
                IsDeleted = false,
            }).ExecuteCommandAsync();

            await _db.Insertable(new WarehouseProduct
            {
                ProductCode = productCode,
                ImportPrice = importPrice,
                OEMPrice = oemPrice,
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();
        }

        private async Task SeedPatchProductAsync(
            string productCode,
            bool seedDomestic = true,
            decimal? domesticPrice = null,
            decimal? oemPrice = null,
            decimal? importPrice = null,
            int? minOrderQuantity = null,
            int? middlePackQuantity = null,
            int? productMiddlePackageQuantity = null,
            decimal? productPurchasePrice = null,
            decimal? productRetailPrice = null
        )
        {
            await _db.Insertable(new Product
            {
                UUID = $"patch-{productCode}-uuid",
                ProductCode = productCode,
                ProductName = productCode,
                ItemNumber = $"ITEM-{productCode}",
                Barcode = $"BAR-{productCode}",
                LocalSupplierCode = "LOCAL-01",
                PurchasePrice = productPurchasePrice,
                RetailPrice = productRetailPrice,
                MiddlePackageQuantity = productMiddlePackageQuantity,
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();

            await _db.Insertable(new WarehouseProduct
            {
                ProductCode = productCode,
                DomesticPrice = domesticPrice,
                OEMPrice = oemPrice,
                ImportPrice = importPrice,
                MinOrderQuantity = minOrderQuantity,
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();

            if (seedDomestic)
            {
                await _db.Insertable(new DomesticProduct
                {
                    ProductCode = productCode,
                    DomesticPrice = domesticPrice,
                    OEMPrice = oemPrice,
                    ImportPrice = importPrice,
                    MiddlePackQuantity = middlePackQuantity,
                    IsActive = true,
                    IsDeleted = false,
                }).ExecuteCommandAsync();
            }
        }

        public static IEnumerable<object[]> InvalidPatchDtos()
        {
            yield return new object[] { new WarehouseProductPatchDto() };
            yield return new object[]
            {
                new WarehouseProductPatchDto { DomesticPrice = 1m, ImportPrice = 2m },
            };
            yield return new object[] { new WarehouseProductPatchDto { MinOrderQuantity = -1 } };
            yield return new object[] { new WarehouseProductPatchDto { DomesticPrice = -0.01m } };
            yield return new object[] { new WarehouseProductPatchDto { ImportPrice = -1m } };
            yield return new object[] { new WarehouseProductPatchDto { OEMPrice = -1m } };
        }

        private async Task SeedStoreAsync(string storeCode, bool isActive, bool isDeleted)
        {
            await _db.Insertable(new Store
            {
                StoreGUID = $"store-guid-{storeCode}",
                StoreCode = storeCode,
                StoreName = $"Store {storeCode}",
                IsActive = isActive,
                IsDeleted = isDeleted,
            }).ExecuteCommandAsync();
        }

        private async Task SeedStoreRetailPriceAsync(
            string storeCode,
            string productCode,
            decimal purchasePrice,
            decimal retailPrice,
            bool isDeleted = false,
            bool isActive = true
        )
        {
            await _db.Insertable(new StoreRetailPrice
            {
                UUID = $"store-price-{storeCode}-{productCode}",
                StoreCode = storeCode,
                ProductCode = productCode,
                StoreProductCode = $"sp-{storeCode}-{productCode}",
                SupplierCode = "LOCAL-01",
                PurchasePrice = purchasePrice,
                StoreRetailPriceValue = retailPrice,
                DiscountRate = null,
                IsActive = isActive,
                IsAutoPricing = false,
                IsSpecialProduct = false,
                IsDeleted = isDeleted,
            }).ExecuteCommandAsync();
        }

        private async Task SeedDomesticImportProductAsync(
            string productCode,
            string productName,
            string? englishName
        )
        {
            await _db.Insertable(new DomesticProduct
            {
                ProductCode = productCode,
                HBProductNo = productCode,
                Barcode = $"BAR-{productCode}",
                ProductName = productName,
                EnglishProductName = englishName,
                ProductType = 0,
                DomesticPrice = 2.1m,
                OEMPrice = 4.99m,
                ImportPrice = 1.2m,
                UnitVolume = 0.069m,
                ProductImage = $"/{productCode}.jpg",
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();
        }

        private ProductWarehouseReactService CreateService(
            ITranslationService? translationService = null,
            IConfiguration? configuration = null,
            ILogger<ProductWarehouseReactService>? logger = null,
            ISqlSugarClient? database = null,
            IWarehouseProductChangeHistoryService? changeHistoryService = null
        )
        {
            configuration ??= new ConfigurationBuilder().Build();
            var context = CreateSqlSugarContext(database ?? _db);
            var itemBarcodeService = new ItemBarcodeService(
                context,
                NullLogger<ItemBarcodeService>.Instance,
                configuration
            );

            return new ProductWarehouseReactService(
                context,
                CreateHqSqlSugarContext(),
                logger ?? NullLogger<ProductWarehouseReactService>.Instance,
                configuration,
                itemBarcodeService,
                Mock.Of<IMapper>(),
                Mock.Of<IDataSyncFullService>(),
                changeHistoryService ?? CreateNoopChangeHistoryService(),
                translationService ?? CreateDefaultTranslationService()
            );
        }

        private static IWarehouseProductChangeHistoryService CreateNoopChangeHistoryService()
        {
            var service = new Mock<IWarehouseProductChangeHistoryService>();
            service
                .Setup(item =>
                    item.CaptureSnapshotsAsync(
                        It.IsAny<IEnumerable<string>>(),
                        It.IsAny<System.Threading.CancellationToken>()
                    )
                )
                .ReturnsAsync(
                    new Dictionary<string, WarehouseProductChangeSnapshotDto>(
                        StringComparer.OrdinalIgnoreCase
                    )
                );
            service
                .Setup(item =>
                    item.RecordChangesAsync(
                        It.IsAny<
                            IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto>
                        >(),
                        It.IsAny<
                            IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto>
                        >(),
                        It.IsAny<WarehouseProductChangeHistoryContextDto>(),
                        It.IsAny<System.Threading.CancellationToken>()
                    )
                )
                .ReturnsAsync(0);
            return service.Object;
        }

        private IWarehouseProductChangeHistoryService CreateRealChangeHistoryService(
            string userGuid,
            string username
        )
        {
            var currentUserService = new Mock<ICurrentUserService>();
            currentUserService.Setup(item => item.GetCurrentUserGuid()).Returns(userGuid);
            currentUserService.Setup(item => item.GetCurrentUsername()).Returns(username);
            return new WarehouseProductChangeHistoryService(
                CreateSqlSugarContext(_db),
                NullLogger<WarehouseProductChangeHistoryService>.Instance,
                currentUserService.Object
            );
        }

        private static ITranslationService CreateDefaultTranslationService()
        {
            var translationService = new Mock<ITranslationService>();
            translationService
                .Setup(x => x.BatchTranslateToEnglishAsync(It.IsAny<List<string>>()))
                .ReturnsAsync(new Dictionary<string, string>());
            translationService
                .Setup(x => x.ContainsChinese(It.IsAny<string>()))
                .Returns<string>(ContainsChineseForTest);
            return translationService.Object;
        }

        private static bool ContainsChineseForTest(string text)
        {
            return !string.IsNullOrWhiteSpace(text)
                && text.Any(c => c >= '\u4e00' && c <= '\u9fff');
        }

        private static SqlSugarContext CreateSqlSugarContext(ISqlSugarClient db)
        {
            var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(
                typeof(SqlSugarContext)
            );

            var dbField = typeof(SqlSugarContext).GetField(
                "_db",
                BindingFlags.Instance | BindingFlags.NonPublic
            );
            dbField!.SetValue(context, db);

            return context;
        }

        private static string ResolveProductWarehouseFeatureRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                var path = Path.Combine(
                    directory.FullName,
                    "services/backend/BlazorApp.Api/Features/ProductWarehouse"
                );
                if (Directory.Exists(path))
                {
                    return path;
                }
                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException("未找到 ProductWarehouse feature 目录");
        }

        private static string ResolveContainerExecutorPath()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                var path = Path.Combine(
                    directory.FullName,
                    "services/backend/BlazorApp.Api/Services/React/ContainerProductCreationExecutorService.cs"
                );
                if (File.Exists(path))
                {
                    return path;
                }
                directory = directory.Parent;
            }

            throw new FileNotFoundException("未找到 ContainerProductCreationExecutorService.cs");
        }

        private static HqSqlSugarContext CreateHqSqlSugarContext()
        {
            var context = (HqSqlSugarContext)RuntimeHelpers.GetUninitializedObject(
                typeof(HqSqlSugarContext)
            );

            var dbField = typeof(HqSqlSugarContext).GetField(
                "_db",
                BindingFlags.Instance | BindingFlags.NonPublic
            );
            dbField!.SetValue(context, new Mock<ISqlSugarClient>().Object);

            return context;
        }
    }

    public sealed class ProductWarehouseMobileLookupSqlServerFactAttribute : FactAttribute
    {
        internal const string ConnectionEnvironmentVariable =
            "HB_TEST_SQLSERVER_CONNECTION";

        public ProductWarehouseMobileLookupSqlServerFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable)))
            {
                Skip = $"未配置 {ConnectionEnvironmentVariable}，跳过真实 SQL Server 移动端仓库商品查询验证。";
            }
        }
    }
}
