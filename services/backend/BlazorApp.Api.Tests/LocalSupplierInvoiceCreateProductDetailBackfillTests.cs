using System.Reflection;
using System.Runtime.CompilerServices;
using AutoMapper;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
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
    /// 新建商品执行后，明细必须回填商品编码，否则明细页关联不到主档、新品数一直不减。
    /// </summary>
    public sealed class LocalSupplierInvoiceCreateProductDetailBackfillTests : IDisposable
    {
        private const string InvoiceGuid = "invoice-create-backfill";

        private readonly string _dbPath;
        private readonly SqliteConnection _sqliteConnection;
        private readonly SqlSugarClient _db;

        public LocalSupplierInvoiceCreateProductDetailBackfillTests()
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
                typeof(WarehouseProduct),
                typeof(StoreRetailPrice),
                typeof(StoreMultiCodeProduct),
                typeof(ProductSetCode),
                typeof(StoreLocalSupplierInvoice),
                typeof(StoreLocalSupplierInvoiceDetails)
            );
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
        public async Task BatchExecuteActionsAsync_新建商品后明细回填商品编码分店商品编码与已有商品数()
        {
            await SeedCreateProductDetailsAsync();

            var result = await CreateService().BatchExecuteActionsAsync(
                InvoiceGuid,
                new List<string> { "detail-create-1", "detail-create-2" },
                "tester"
            );

            Assert.True(result.Success, $"{result.Code} {result.Message}");
            Assert.Equal(2, result.Data?.CreatedProducts);

            var details = await _db.Queryable<StoreLocalSupplierInvoiceDetails>()
                .Where(x => x.InvoiceGUID == InvoiceGuid)
                .OrderBy(x => x.DetailGUID)
                .ToListAsync();
            var productsByItemNumber = (await _db.Queryable<Product>().ToListAsync())
                .ToDictionary(x => x.ItemNumber!);

            Assert.Equal(2, details.Count);
            foreach (var detail in details)
            {
                var product = productsByItemNumber[detail.ItemNumber!];
                Assert.False(string.IsNullOrWhiteSpace(product.ProductCode));
                Assert.Equal(product.ProductCode, detail.ProductCode);
                // 关键断言：分店商品编码按进货单表头分店拼接，并能命中刚建的分店价格行。
                Assert.Equal("S01" + product.ProductCode, detail.StoreProductCode);
                Assert.True(
                    await _db.Queryable<StoreRetailPrice>()
                        .AnyAsync(x => x.StoreProductCode == detail.StoreProductCode && x.StoreCode == "S01")
                );
                Assert.Equal(1, detail.ExistingProductCount);
                Assert.Equal(99, detail.ActivityType);
                Assert.Equal("tester", detail.UpdatedBy);
            }

            // 明细读取侧应能关联到新主档（商品类型来自主档，未回填时为 null）。
            var read = await CreateService().GetDetailsAsync(InvoiceGuid);
            Assert.True(read.Success, read.Message);
            Assert.All(read.Data!, item => Assert.Equal(0, item.ProductType));
        }

        [Fact]
        public async Task BatchExecuteActionsAsync_新建商品后续写入失败时明细回填随事务回滚()
        {
            await SeedCreateProductDetailsAsync();
            var history = new Mock<IWarehouseProductChangeHistoryService>();
            history
                .Setup(x => x.CaptureSnapshotsAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Dictionary<string, WarehouseProductChangeSnapshotDto>());
            history
                .Setup(x =>
                    x.RecordChangesAsync(
                        It.IsAny<IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto>>(),
                        It.IsAny<IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto>>(),
                        It.IsAny<WarehouseProductChangeHistoryContextDto>(),
                        It.IsAny<CancellationToken>()
                    )
                )
                .ThrowsAsync(new InvalidOperationException("history failed"));

            var result = await CreateService(history.Object).BatchExecuteActionsAsync(
                InvoiceGuid,
                new List<string> { "detail-create-1", "detail-create-2" },
                "tester"
            );

            Assert.False(result.Success);
            Assert.Equal(0, await _db.Queryable<Product>().CountAsync());
            var details = await _db.Queryable<StoreLocalSupplierInvoiceDetails>()
                .Where(x => x.InvoiceGUID == InvoiceGuid)
                .ToListAsync();
            Assert.All(details, detail =>
            {
                Assert.Null(detail.ProductCode);
                Assert.Null(detail.StoreProductCode);
                Assert.Equal(0, detail.ExistingProductCount);
                Assert.Equal((int)DetailAction.CreateProduct, detail.ActivityType);
            });
        }

        private async Task SeedCreateProductDetailsAsync()
        {
            await _db.Insertable(new List<Store>
            {
                new() { StoreGUID = "store-guid-1", StoreCode = "S01", StoreName = "Sydney Store", IsActive = true, IsDeleted = false },
                new() { StoreGUID = "store-guid-2", StoreCode = "S02", StoreName = "Melbourne Store", IsActive = true, IsDeleted = false },
            }).ExecuteCommandAsync();
            await _db.Insertable(new HBLocalSupplier
            {
                Guid = "supplier-guid-1",
                LocalSupplierCode = "SUP01",
                Name = "Local Supplier",
                Status = 1,
                IsDeleted = false,
            }).ExecuteCommandAsync();
            await _db.Insertable(new StoreLocalSupplierInvoice
            {
                InvoiceGUID = InvoiceGuid,
                StoreCode = "S01",
                SupplierCode = "SUP01",
                InvoiceNo = "INV-CREATE-BACKFILL",
                OrderDate = new DateTime(2026, 10, 6),
                CreatedAt = DateTime.UtcNow,
                IsDeleted = false,
            }).ExecuteCommandAsync();

            // 模拟商品检测后的新品行：编码为空、已有商品数为 0、待执行新建商品。
            await _db.Insertable(new List<StoreLocalSupplierInvoiceDetails>
            {
                new()
                {
                    DetailGUID = "detail-create-1",
                    InvoiceGUID = InvoiceGuid,
                    StoreCode = "S01",
                    SupplierCode = "SUP01",
                    ItemNumber = "NEW-ITEM-1",
                    Barcode = "9300000000011",
                    ProductName = "New Product One",
                    PurchasePrice = 1.20m,
                    RetailPrice = 3.99m,
                    ExistingProductCount = 0,
                    ActivityType = (int)DetailAction.CreateProduct,
                    IsDeleted = false,
                },
                new()
                {
                    DetailGUID = "detail-create-2",
                    InvoiceGUID = InvoiceGuid,
                    StoreCode = "S01",
                    SupplierCode = "SUP01",
                    ItemNumber = "NEW-ITEM-2",
                    Barcode = "9300000000028",
                    ProductName = "New Product Two",
                    PurchasePrice = 2.40m,
                    RetailPrice = 5.99m,
                    ExistingProductCount = 0,
                    ActivityType = (int)DetailAction.CreateProduct,
                    IsDeleted = false,
                },
            }).ExecuteCommandAsync();
        }

        private LocalSupplierInvoicesReactService CreateService(
            IWarehouseProductChangeHistoryService? changeHistoryService = null
        )
        {
            var autoPricing = new Mock<IAutoPricingService>();
            autoPricing.Setup(x => x.FindStrategyForPriceAsync(It.IsAny<decimal>(), It.IsAny<string?>(), It.IsAny<string?>()))
                .ReturnsAsync((BlazorApp.Shared.Models.HBweb.PricingStrategy?)null);
            autoPricing.Setup(x => x.CalculateRetailPrice(It.IsAny<decimal>(), It.IsAny<BlazorApp.Shared.Models.HBweb.PricingStrategy?>()))
                .Returns<decimal, BlazorApp.Shared.Models.HBweb.PricingStrategy?>((price, _) => price * 2.5m);

            return new LocalSupplierInvoicesReactService(
                CreateSqlSugarContext(_db),
                CreateHqSqlSugarContext(),
                Mock.Of<IMapper>(),
                NullLogger<LocalSupplierInvoicesReactService>.Instance,
                autoPricing.Object,
                changeHistoryService ?? WarehouseProductChangeHistoryTestDouble.CreateNoop(),
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
