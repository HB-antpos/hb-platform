using System.Reflection;
using System.Runtime.CompilerServices;
using AutoMapper;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests
{
    /// <summary>
    /// 分店进货单列表「有商品未创建」过滤：口径 = 至少一条有效明细为「新品」（检测为主档不存在且仍未关联商品编码），
    /// 与列表「新品 N」标签一致；列表「本单新品 N」（商品已由本单建好）不算。
    /// 同时验证它与「是否检测」分段、供应商、随货单号、商品关键词组合时，列表总数与分店计数口径一致。
    /// </summary>
    public sealed class LocalSupplierInvoiceUncreatedProductFilterTests : IDisposable
    {
        // 有未创建新品，且全部已检测。
        private const string PendingNewChecked = "inv-pending-new-checked";
        // 有未创建新品，同时还有未检测明细（ExistingProductCount 为 NULL）。
        private const string PendingNewUnchecked = "inv-pending-new-unchecked";
        // 未创建新品在另一个供应商、另一个分店名下。
        private const string PendingNewOtherSupplier = "inv-pending-new-other";
        // 只有「本单新品」（商品已建好、已关联编码）。
        private const string CreatedHereOnly = "inv-created-here-only";
        // 只有未检测明细（NULL 不是新品）。
        private const string UncheckedOnly = "inv-unchecked-only";
        // 新品明细已删除，不应命中。
        private const string DeletedPendingNew = "inv-deleted-pending-new";
        // 没有任何明细。
        private const string NoDetails = "inv-no-details";

        private readonly string _dbPath;
        private readonly SqliteConnection _sqliteConnection;
        private readonly SqlSugarClient _db;

        public LocalSupplierInvoiceUncreatedProductFilterTests()
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
                typeof(Store),
                typeof(UserStore),
                typeof(HBLocalSupplier),
                typeof(Product),
                typeof(StoreRetailPrice),
                typeof(StoreLocalSupplierInvoice),
                typeof(StoreLocalSupplierInvoiceDetails)
            );
            SqliteWarehouseProductChangeHistoryTable.Create(_db);
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

        [Fact]
        public async Task 开启过滤_只返回含未创建新品的有效明细的单据()
        {
            await SeedAsync();

            var result = await CreateService().GetGridDataAsync(
                new GridRequestDto { PageSize = 20, FilterModel = Filter(uncreated: "true") }
            );

            Assert.True(result.Success, result.Message);
            Assert.Equal(
                new[] { PendingNewChecked, PendingNewOtherSupplier, PendingNewUnchecked },
                result.Items!.Select(item => item.InvoiceGUID).OrderBy(guid => guid, StringComparer.Ordinal).ToArray()
            );
            // 过滤命中的单据，列表上必然带「新品 N」（NewProductDetailCount > 0），两处口径一致。
            Assert.All(result.Items!, item => Assert.True(item.NewProductDetailCount > 0, item.InvoiceGUID));
            Assert.Equal(3, result.Total);
        }

        [Theory]
        [InlineData("false")]
        [InlineData("not-a-bool")]
        [InlineData("")]
        public async Task 过滤值不是true时不收窄结果(string value)
        {
            await SeedAsync();

            var result = await CreateService().GetGridDataAsync(
                new GridRequestDto { PageSize = 20, FilterModel = Filter(uncreated: value) }
            );

            Assert.True(result.Success, result.Message);
            Assert.Equal(7, result.Total);
        }

        [Fact]
        public async Task 列名大小写不敏感()
        {
            await SeedAsync();
            var filter = new Dictionary<string, FilterModelDto>
            {
                ["HASUNCREATEDPRODUCTS"] = TextEquals("true"),
            };

            var result = await CreateService().GetGridDataAsync(new GridRequestDto { PageSize = 20, FilterModel = filter });

            Assert.Equal(3, result.Total);
        }

        [Theory]
        [InlineData("true", new[] { PendingNewChecked, PendingNewOtherSupplier })]
        [InlineData("false", new[] { PendingNewUnchecked })]
        public async Task 与是否检测分段组合_总数与列表一致(string productChecked, string[] expected)
        {
            await SeedAsync();

            var result = await CreateService().GetGridDataAsync(
                new GridRequestDto
                {
                    PageSize = 20,
                    FilterModel = Filter(uncreated: "true", productChecked: productChecked),
                }
            );

            Assert.True(result.Success, result.Message);
            Assert.Equal(expected.OrderBy(g => g, StringComparer.Ordinal), result.Items!.Select(i => i.InvoiceGUID).OrderBy(g => g, StringComparer.Ordinal));
            Assert.Equal(expected.Length, result.Total);
        }

        [Fact]
        public async Task 与供应商_随货单号_商品关键词组合()
        {
            await SeedAsync();
            var service = CreateService();

            var bySupplier = await service.GetGridDataAsync(
                new GridRequestDto { PageSize = 20, FilterModel = Filter(uncreated: "true", supplierCode: "SUP02") }
            );
            Assert.Equal(new[] { PendingNewOtherSupplier }, bySupplier.Items!.Select(i => i.InvoiceGUID).ToArray());

            var byInvoiceNo = await service.GetGridDataAsync(
                new GridRequestDto { PageSize = 20, FilterModel = Filter(uncreated: "true", invoiceNo: "unchecked") }
            );
            Assert.Equal(new[] { PendingNewUnchecked }, byInvoiceNo.Items!.Select(i => i.InvoiceGUID).ToArray());

            // 关键词命中的是「已建好」的明细（CreatedHereOnly 的商品名）时，单据本身不含未创建新品，仍应被过滤掉。
            var byKeywordOnCreatedProduct = await service.GetGridDataAsync(
                new GridRequestDto { PageSize = 20, FilterModel = Filter(uncreated: "true", keyword: "CREATED-HERE-ITEM") }
            );
            Assert.Equal(0, byKeywordOnCreatedProduct.Total);

            // 关键词命中新品明细本身（或同单的其他明细）时保留。
            var byKeywordOnPendingProduct = await service.GetGridDataAsync(
                new GridRequestDto { PageSize = 20, FilterModel = Filter(uncreated: "true", keyword: "PENDING-NEW-CHECKED") }
            );
            Assert.Equal(new[] { PendingNewChecked }, byKeywordOnPendingProduct.Items!.Select(i => i.InvoiceGUID).ToArray());
        }

        [Fact]
        public async Task 分页总数不受页大小影响_分店计数与列表同口径()
        {
            await SeedAsync();
            var service = CreateService();
            var request = new GridRequestDto
            {
                StartRow = 0,
                PageSize = 20,
                FilterModel = Filter(uncreated: "true"),
            };

            var page = await service.GetGridDataAsync(request);
            var storeCounts = await service.GetStoreCountsAsync(request, null);

            Assert.True(storeCounts.Success, storeCounts.Message);
            Assert.Equal(page.Total, storeCounts.Data!.Sum(row => row.Count));
            Assert.Equal(2, storeCounts.Data!.Single(row => row.StoreCode == "S01").Count);
            Assert.Equal(1, storeCounts.Data!.Single(row => row.StoreCode == "S02").Count);
        }

        private static Dictionary<string, FilterModelDto> Filter(
            string? uncreated = null,
            string? productChecked = null,
            string? supplierCode = null,
            string? invoiceNo = null,
            string? keyword = null
        )
        {
            var filter = new Dictionary<string, FilterModelDto>();
            if (uncreated != null) filter["hasUncreatedProducts"] = TextEquals(uncreated);
            if (productChecked != null) filter["isProductChecked"] = TextEquals(productChecked);
            if (supplierCode != null) filter["supplierCode"] = TextEquals(supplierCode);
            if (invoiceNo != null) filter["invoiceNo"] = new FilterModelDto { FilterType = "text", Type = "contains", Filter = invoiceNo };
            if (keyword != null) filter["productKeyword"] = new FilterModelDto { FilterType = "text", Filter = keyword };
            return filter;
        }

        private static FilterModelDto TextEquals(string value) =>
            new() { FilterType = "text", Type = "equals", Filter = value };

        private async Task SeedAsync()
        {
            foreach (var (code, name) in new[] { ("S01", "Sydney Store"), ("S02", "Melbourne Store") })
            {
                await _db.Insertable(new Store
                {
                    StoreGUID = "store-guid-" + code,
                    StoreCode = code,
                    StoreName = name,
                    IsActive = true,
                    IsDeleted = false,
                }).ExecuteCommandAsync();
            }
            foreach (var code in new[] { "SUP01", "SUP02" })
            {
                await _db.Insertable(new HBLocalSupplier
                {
                    Guid = "supplier-guid-" + code,
                    LocalSupplierCode = code,
                    Name = "Supplier " + code,
                    Status = 1,
                    IsDeleted = false,
                }).ExecuteCommandAsync();
            }

            static StoreLocalSupplierInvoice Invoice(string guid, string store, string supplier) => new()
            {
                InvoiceGUID = guid,
                StoreCode = store,
                SupplierCode = supplier,
                InvoiceNo = guid,
                OrderDate = new DateTime(2026, 10, 6),
                CreatedAt = DateTime.UtcNow,
                IsDeleted = false,
            };
            await _db.Insertable(new List<StoreLocalSupplierInvoice>
            {
                Invoice(PendingNewChecked, "S01", "SUP01"),
                Invoice(PendingNewUnchecked, "S01", "SUP01"),
                Invoice(PendingNewOtherSupplier, "S02", "SUP02"),
                Invoice(CreatedHereOnly, "S01", "SUP01"),
                Invoice(UncheckedOnly, "S01", "SUP01"),
                Invoice(DeletedPendingNew, "S01", "SUP01"),
                Invoice(NoDetails, "S01", "SUP01"),
            }).ExecuteCommandAsync();

            static StoreLocalSupplierInvoiceDetails Detail(
                string guid,
                string invoice,
                string name,
                string? code,
                int? existing,
                bool deleted = false
            ) => new()
            {
                DetailGUID = guid,
                InvoiceGUID = invoice,
                StoreCode = invoice == PendingNewOtherSupplier ? "S02" : "S01",
                SupplierCode = invoice == PendingNewOtherSupplier ? "SUP02" : "SUP01",
                ProductName = name,
                ProductCode = code,
                ExistingProductCount = existing,
                IsDeleted = deleted,
            };
            await _db.Insertable(new List<StoreLocalSupplierInvoiceDetails>
            {
                // 新品（0 + 无编码）+ 已存在商品：已检测、有未创建新品。
                Detail("d1", PendingNewChecked, "PENDING-NEW-CHECKED", null, 0),
                Detail("d2", PendingNewChecked, "OLD-ITEM", "P-OLD", 1),
                // 新品 + 未检测明细（NULL）。
                Detail("d3", PendingNewUnchecked, "PENDING-NEW-UNCHECKED", null, 0),
                Detail("d4", PendingNewUnchecked, "NOT-CHECKED-YET", null, null),
                // 另一供应商/分店的新品。
                Detail("d5", PendingNewOtherSupplier, "PENDING-NEW-OTHER", null, 0),
                // 检测为 0 但已关联编码（商品已建好，「本单新品」口径）：不算未创建。
                Detail("d6", CreatedHereOnly, "CREATED-HERE-ITEM", "P-HERE", 0),
                // 只有未检测明细：NULL 不是新品。
                Detail("d7", UncheckedOnly, "UNCHECKED-ONLY-ITEM", null, null),
                // 新品明细已删除，另有一条已存在商品。
                Detail("d8", DeletedPendingNew, "DELETED-NEW", null, 0, deleted: true),
                Detail("d9", DeletedPendingNew, "OLD-ITEM-2", "P-OLD", 1),
            }).ExecuteCommandAsync();
        }

        private LocalSupplierInvoicesReactService CreateService()
        {
            return new LocalSupplierInvoicesReactService(
                CreateSqlSugarContext(_db),
                CreateHqSqlSugarContext(),
                Mock.Of<IMapper>(),
                NullLogger<LocalSupplierInvoicesReactService>.Instance,
                Mock.Of<IAutoPricingService>(),
                WarehouseProductChangeHistoryTestDouble.CreateNoop(),
                null
            );
        }

        private static SqlSugarContext CreateSqlSugarContext(ISqlSugarClient db)
        {
            var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(SqlSugarContext));
            typeof(SqlSugarContext)
                .GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(context, db);
            return context;
        }

        private static HqSqlSugarContext CreateHqSqlSugarContext()
        {
            var context = (HqSqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(HqSqlSugarContext));
            typeof(HqSqlSugarContext)
                .GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(context, new Mock<ISqlSugarClient>().Object);
            return context;
        }
    }
}
