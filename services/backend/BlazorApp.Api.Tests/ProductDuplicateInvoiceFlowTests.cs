using System.Reflection;
using System.Runtime.CompilerServices;
using AutoMapper;
using BlazorApp.Api.Data;
using BlazorApp.Api.Features.LocalSupplierInvoices;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services.Products;
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
    /// 发票链路的跨供应商防重：新建商品的主条码不能被别的供应商的存活商品占用。
    /// 2026-10-28 全库 90 组「货号+条码相同」重复全是跨供应商：检测按供应商匹配货号，校验也只按本单供应商查条码，
    /// 别的供应商已占用的条码被放行，建出了第二条商品。
    /// </summary>
    public sealed class ProductDuplicateInvoiceFlowTests : IDisposable
    {
        private const string InvoiceGuid = "invoice-dup-guard";
        private const string OccupiedBarcode = "9300000000011";
        private const string FreeBarcode = "9300000000028";

        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        private readonly SqliteConnection _connection;
        private readonly SqlSugarClient _db;

        public ProductDuplicateInvoiceFlowTests()
        {
            _connection = new SqliteConnection($"Data Source={_dbPath}");
            _connection.Open();
            _db = new SqlSugarClient(new ConnectionConfig
            {
                ConnectionString = _connection.ConnectionString,
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
            SqliteWarehouseProductChangeHistoryTable.Create(_db);
        }

        [Fact]
        public async Task 检测_货号相同且条码只被其他供应商一个商品占用_自动关联为已有商品并默认更新进货价()
        {
            await SeedInvoiceAsync();
            // 同一商品挂在别的供应商名下：货号、条码都相同，本供应商下找不到。
            await SeedProductAsync("EXIST-1", "SUP02", "NEW-ITEM-1", "Existing Under Other Supplier", OccupiedBarcode);

            var response = await CreateService().CheckProductsAsync(new CheckProductsRequest { InvoiceGuid = InvoiceGuid });

            Assert.True(response.Success, response.Message);
            var linked = response.Data!.Results.Single(item => item.DetailGuid == "detail-1");
            Assert.Equal(1, linked.ProductStatus); // 自动关联后按已有商品处理
            Assert.Equal("EXIST-1", linked.ProductInfo?.ProductCode);
            Assert.Equal((int)DetailAction.UpdatePurchasePrice, linked.DefaultAction); // 不再等待操作，也不新建重复商品
            Assert.Empty(linked.SuggestedExistingProducts);

            // 关联结果要落库：商品编码回填到明细，后续执行「更新进货价」才找得到商品。
            var detail = await _db.Queryable<StoreLocalSupplierInvoiceDetails>().SingleAsync(x => x.DetailGUID == "detail-1");
            Assert.Equal("EXIST-1", detail.ProductCode);
            Assert.Equal((int)DetailAction.UpdatePurchasePrice, detail.ActivityType);
        }

        [Fact]
        public async Task 检测_货号不同但条码被其他供应商商品占用_只给建议选用且仍等待操作()
        {
            await SeedInvoiceAsync();
            await SeedProductAsync("EXIST-1", "SUP02", "OTHER-ITEM", "Existing Under Other Supplier", OccupiedBarcode);

            var response = await CreateService().CheckProductsAsync(new CheckProductsRequest { InvoiceGuid = InvoiceGuid });

            Assert.True(response.Success, response.Message);
            var conflicted = response.Data!.Results.Single(item => item.DetailGuid == "detail-1");
            Assert.Equal(2, conflicted.ProductStatus); // 货号不同信心不够：不自动关联
            Assert.Equal((int)DetailAction.WaitForOperation, conflicted.DefaultAction); // 不默认新建，由人确认
            var suggestion = Assert.Single(conflicted.SuggestedExistingProducts);
            Assert.Equal("EXIST-1", suggestion.ProductCode);
            Assert.Equal("OTHER-ITEM", suggestion.ItemNumber);
            Assert.Equal("SUP02", suggestion.LocalSupplierCode);
            Assert.False(suggestion.SameItemNumber);
        }

        [Fact]
        public async Task 检测_条码被多个商品占用_即使货号相同也不自动关联()
        {
            await SeedInvoiceAsync();
            await SeedProductAsync("EXIST-1", "SUP02", "NEW-ITEM-1", "Existing Under Supplier 2", OccupiedBarcode);
            await SeedProductAsync("EXIST-2", "SUP03", "NEW-ITEM-1", "Existing Under Supplier 3", OccupiedBarcode);

            var response = await CreateService().CheckProductsAsync(new CheckProductsRequest { InvoiceGuid = InvoiceGuid });

            Assert.True(response.Success, response.Message);
            var conflicted = response.Data!.Results.Single(item => item.DetailGuid == "detail-1");
            Assert.Equal(2, conflicted.ProductStatus);
            Assert.Equal((int)DetailAction.WaitForOperation, conflicted.DefaultAction);
            Assert.Equal(2, conflicted.SuggestedExistingProducts.Count);
        }

        [Fact]
        public async Task 检测_条码没人占用时没有建议且默认新建()
        {
            await SeedInvoiceAsync();
            await SeedProductAsync("EXIST-1", "SUP02", "NEW-ITEM-1", "Existing Under Other Supplier", OccupiedBarcode);

            var response = await CreateService().CheckProductsAsync(new CheckProductsRequest { InvoiceGuid = InvoiceGuid });

            var free = response.Data!.Results.Single(item => item.DetailGuid == "detail-2");
            Assert.Equal(2, free.ProductStatus);
            Assert.Empty(free.SuggestedExistingProducts);
            Assert.Equal((int)DetailAction.CreateProduct, free.DefaultAction);
        }

        [Fact]
        public async Task 执行_新建商品的条码被其他供应商商品占用_整单拦截并提示选用已有商品()
        {
            await SeedInvoiceAsync();
            await SeedProductAsync("EXIST-1", "SUP02", "OTHER-ITEM", "Existing Under Other Supplier", OccupiedBarcode);

            var result = await CreateService().BatchExecuteActionsAsync(
                InvoiceGuid,
                new List<string> { "detail-1", "detail-2" },
                "tester"
            );

            Assert.False(result.Success);
            Assert.Equal("VALIDATION_ERROR", result.ErrorCode);
            var errors = Assert.IsType<BatchExecuteActionsResultDto>(result.Details).Errors;
            var error = Assert.Single(errors);
            Assert.Contains("detail-1", error);
            Assert.Contains($"条码 {OccupiedBarcode} 已属于商品", error);
            Assert.Contains("货号 OTHER-ITEM", error);
            Assert.Contains("供应商 SUP02", error);
            Assert.Contains("选用已有商品", error);

            // 整单零写入：没有新商品，明细仍待新建且没有回填编码。
            Assert.Equal(1, await _db.Queryable<Product>().CountAsync());
            var details = await _db.Queryable<StoreLocalSupplierInvoiceDetails>().Where(x => x.InvoiceGUID == InvoiceGuid).ToListAsync();
            Assert.All(details, detail =>
            {
                Assert.Null(detail.ProductCode);
                Assert.Equal((int)DetailAction.CreateProduct, detail.ActivityType);
            });
        }

        [Fact]
        public async Task 执行_条码只被已软删商品占用_允许新建()
        {
            await SeedInvoiceAsync();
            await SeedProductAsync("GONE-1", "SUP02", "OTHER-ITEM", "Deleted Product", OccupiedBarcode, isDeleted: true);

            var result = await CreateService().BatchExecuteActionsAsync(
                InvoiceGuid,
                new List<string> { "detail-1", "detail-2" },
                "tester"
            );

            Assert.True(result.Success, $"{result.ErrorCode} {result.Message}");
            Assert.Equal(2, result.Data?.CreatedProducts);
        }

        [Fact]
        public async Task 执行_条码被其他商品的分店多码占用_拦截()
        {
            await SeedInvoiceAsync();
            await SeedProductAsync("MAIN-1", "SUP02", "OTHER-ITEM", "Has Multi Code", "9300000009999");
            await _db.Insertable(new StoreMultiCodeProduct
            {
                UUID = "mc-1",
                StoreCode = "S01",
                ProductCode = "MAIN-1",
                MultiCodeProductCode = "multi-1",
                StoreMultiCodeProductCode = "S01multi-1",
                MultiBarcode = OccupiedBarcode,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                IsDeleted = false,
            }).ExecuteCommandAsync();

            var result = await CreateService().BatchExecuteActionsAsync(
                InvoiceGuid,
                new List<string> { "detail-1" },
                "tester"
            );

            Assert.False(result.Success);
            var error = Assert.Single(Assert.IsType<BatchExecuteActionsResultDto>(result.Details).Errors);
            Assert.Contains("已属于商品", error);
            Assert.Contains("货号 OTHER-ITEM", error);
            Assert.Equal(1, await _db.Queryable<Product>().CountAsync());
        }

        [Fact]
        public async Task 写入层兜底_即使绕过校验器也不会为已占用条码建出第二条商品()
        {
            await SeedInvoiceAsync();
            await SeedProductAsync("EXIST-1", "SUP02", "OTHER-ITEM", "Existing Under Other Supplier", OccupiedBarcode);
            var details = await _db.Queryable<StoreLocalSupplierInvoiceDetails>()
                .Where(x => x.InvoiceGUID == InvoiceGuid)
                .OrderBy(x => x.DetailGUID)
                .ToListAsync();
            var header = await _db.Queryable<StoreLocalSupplierInvoice>().FirstAsync(x => x.InvoiceGUID == InvoiceGuid);

            var result = await CreateStore().BatchCreateProductsAsync(
                details,
                header,
                "tester",
                new Dictionary<string, int>()
            );

            // 冲突行计失败（调用方据此整单回滚），无冲突行不受影响。
            Assert.Equal(1, result.FailedCount);
            Assert.Equal(1, result.SuccessCount);
            Assert.Contains(result.Errors, error => error.Contains("已属于商品") && error.Contains("货号 OTHER-ITEM"));
            Assert.False(await _db.Queryable<Product>().AnyAsync(x => x.ItemNumber == "NEW-ITEM-1"));
        }

        public void Dispose()
        {
            _db.Dispose();
            _connection.Dispose();
            SqliteTempFileCleanup.DeleteIfExists(_dbPath);
        }

        private async Task SeedInvoiceAsync()
        {
            await _db.Insertable(new List<Store>
            {
                new() { StoreGUID = "store-guid-1", StoreCode = "S01", StoreName = "Sydney Store", IsActive = true, IsDeleted = false },
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
                InvoiceNo = "INV-DUP-GUARD",
                OrderDate = new DateTime(2026, 10, 28),
                CreatedAt = DateTime.UtcNow,
                IsDeleted = false,
            }).ExecuteCommandAsync();

            // 检测后的新品行：detail-1 的条码可能被别的供应商占用，detail-2 的条码始终没人占用。
            await _db.Insertable(new List<StoreLocalSupplierInvoiceDetails>
            {
                NewDetail("detail-1", "NEW-ITEM-1", OccupiedBarcode, "New Product One", 1.20m),
                NewDetail("detail-2", "NEW-ITEM-2", FreeBarcode, "New Product Two", 2.40m),
            }).ExecuteCommandAsync();
        }

        private static StoreLocalSupplierInvoiceDetails NewDetail(
            string guid,
            string itemNumber,
            string barcode,
            string name,
            decimal purchasePrice
        ) => new()
        {
            DetailGUID = guid,
            InvoiceGUID = InvoiceGuid,
            StoreCode = "S01",
            SupplierCode = "SUP01",
            ItemNumber = itemNumber,
            Barcode = barcode,
            ProductName = name,
            PurchasePrice = purchasePrice,
            RetailPrice = purchasePrice * 2.5m,
            ExistingProductCount = 0,
            ActivityType = (int)DetailAction.CreateProduct,
            IsDeleted = false,
        };

        private async Task SeedProductAsync(
            string productCode,
            string supplierCode,
            string itemNumber,
            string name,
            string barcode,
            bool isDeleted = false
        )
        {
            var now = DateTime.UtcNow;
            await _db.Insertable(new Product
            {
                UUID = $"uuid-{productCode}",
                ProductCode = productCode,
                LocalSupplierCode = supplierCode,
                ItemNumber = itemNumber,
                ProductName = name,
                Barcode = barcode,
                ProductType = 0,
                PurchasePrice = 1m,
                RetailPrice = 2m,
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now,
                IsDeleted = isDeleted,
            }).ExecuteCommandAsync();
        }

        private static Mock<IAutoPricingService> CreateAutoPricing()
        {
            var autoPricing = new Mock<IAutoPricingService>();
            // 检测会先读取全部启用的定价策略；本用例不需要策略，返回空列表即可。
            autoPricing.Setup(x => x.GetAllActiveStrategiesAsync())
                .ReturnsAsync(new List<BlazorApp.Shared.Models.HBweb.PricingStrategy>());
            autoPricing.Setup(x => x.FindStrategyForPriceAsync(It.IsAny<decimal>(), It.IsAny<string?>(), It.IsAny<string?>()))
                .ReturnsAsync((BlazorApp.Shared.Models.HBweb.PricingStrategy?)null);
            autoPricing.Setup(x => x.CalculateRetailPrice(It.IsAny<decimal>(), It.IsAny<BlazorApp.Shared.Models.HBweb.PricingStrategy?>()))
                .Returns<decimal, BlazorApp.Shared.Models.HBweb.PricingStrategy?>((price, _) => price * 2.5m);
            return autoPricing;
        }

        private LocalSupplierInvoicesReactService CreateService() =>
            new(
                CreateSqlSugarContext(_db),
                CreateHqSqlSugarContext(),
                Mock.Of<IMapper>(),
                NullLogger<LocalSupplierInvoicesReactService>.Instance,
                CreateAutoPricing().Object,
                WarehouseProductChangeHistoryTestDouble.CreateNoop(),
                null
            );

        private LocalSupplierInvoicesProductExecutionStore CreateStore() =>
            new(new LocalSupplierInvoicesDependencies(
                CreateSqlSugarContext(_db),
                CreateHqSqlSugarContext(),
                Mock.Of<IMapper>(),
                NullLogger.Instance,
                CreateAutoPricing().Object,
                WarehouseProductChangeHistoryTestDouble.CreateNoop(),
                null
            ));

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
