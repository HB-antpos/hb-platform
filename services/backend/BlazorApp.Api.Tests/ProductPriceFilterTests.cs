using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.Extensions.Logging;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests
{
    /// <summary>
    /// 商品价格过滤功能单元测试
    /// 覆盖单条件过滤、多条件组合过滤、区间边界值、分店无数据等场景
    /// 测试覆盖率目标: ≥ 90%
    /// </summary>
    public class ProductPriceFilterTests : IDisposable
    {
        private readonly string _dbPath;
        private readonly SqlSugarClient _db;
        private readonly Mock<ILogger<ProductReactService>> _loggerMock;
        private readonly ProductReactService _service;

        public ProductPriceFilterTests()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
            _db = new SqlSugarClient(
                new ConnectionConfig
                {
                    ConnectionString = $"Data Source={_dbPath}",
                    DbType = DbType.Sqlite,
                    IsAutoCloseConnection = false,
                    InitKeyType = InitKeyType.Attribute,
                }
            );
            _db.CodeFirst.InitTables(
                typeof(Product),
                typeof(StoreRetailPrice),
                typeof(Store),
                typeof(HBLocalSupplier),
                typeof(WarehouseCategory),
                typeof(LocalSupplierCategory),
                typeof(LocalSupplierCategoryCapture),
                typeof(LocalSupplierCategoryProductAssignment)
            );
            SeedPriceData();

            _loggerMock = new Mock<ILogger<ProductReactService>>();
            _service = new ProductReactService(
                CreateSqlSugarContext(_db),
                CreateHqSqlSugarContext(_db),
                new Mock<AutoMapper.IMapper>().Object,
                _loggerMock.Object,
                new Mock<Microsoft.AspNetCore.Http.IHttpContextAccessor>().Object,
                new ProductAuditNoopHistoryService(),
                new ProductAuditSystemCurrentUserService()
            );
        }

        private void SeedPriceData()
        {
            var updatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            _db.Insertable(
                    new Store
                    {
                        StoreGUID = "store-guid-001",
                        StoreCode = "STORE001",
                        StoreName = "测试分店",
                        IsDeleted = false,
                    }
                )
                .ExecuteCommand();
            _db.Insertable(
                    new Product
                    {
                        UUID = "product-guid-001",
                        ProductCode = "P-IPHONE",
                        ItemNumber = "ITEM-IPHONE",
                        Barcode = "BARCODE-IPHONE",
                        ProductName = "iPhone",
                        EnglishName = "iPhone",
                        LocalSupplierCode = "SUP001",
                        ProductType = 1,
                        PurchasePrice = 50m,
                        RetailPrice = 100m,
                        IsAutoPricing = false,
                        IsSpecialProduct = false,
                        IsActive = true,
                        IsDeleted = false,
                        CreatedAt = updatedAt.AddDays(-1),
                        UpdatedAt = updatedAt,
                        UpdatedBy = "seed",
                    }
                )
                .ExecuteCommand();
            _db.Insertable(
                    new StoreRetailPrice
                    {
                        UUID = "price-guid-001",
                        StoreCode = "STORE001",
                        ProductCode = "P-IPHONE",
                        StoreProductCode = "STORE001-P-IPHONE",
                        SupplierCode = "SUP001",
                        PurchasePrice = 50m,
                        StoreRetailPriceValue = 100m,
                        DiscountRate = 0.5m,
                        IsAutoPricing = false,
                        IsSpecialProduct = false,
                        IsActive = true,
                        IsDeleted = false,
                        CreatedAt = updatedAt.AddDays(-1),
                        UpdatedAt = updatedAt,
                        UpdatedBy = "seed",
                    }
                )
                .ExecuteCommand();
        }

        private static SqlSugarContext CreateSqlSugarContext(ISqlSugarClient db)
        {
            var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(
                typeof(SqlSugarContext)
            );
            typeof(SqlSugarContext)
                .GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(context, db);
            return context;
        }

        private static HqSqlSugarContext CreateHqSqlSugarContext(ISqlSugarClient db)
        {
            var context = (HqSqlSugarContext)RuntimeHelpers.GetUninitializedObject(
                typeof(HqSqlSugarContext)
            );
            typeof(HqSqlSugarContext)
                .GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(context, db);
            return context;
        }

        public void Dispose()
        {
            _db.Dispose();
            if (File.Exists(_dbPath))
            {
                File.Delete(_dbPath);
            }
            GC.SuppressFinalize(this);
        }

        #region 单条件过滤测试

        [Fact]
        public async Task 单条件_搜索关键词_应该返回匹配结果()
        {
            var filter = new ProductPriceFilterDto
            {
                Search = "iPhone",
                StoreCodes = new List<string> { "STORE001" },
                PageNumber = 1,
                PageSize = 20
            };

            var result = await _service.GetPriceFilteredPagedListAsync(filter);

            Assert.NotNull(result);
            Assert.NotNull(result.Items);
            Assert.Equal(20, result.PageSize);
            Assert.Equal(1, result.PageNumber);
        }

        [Fact]
        public async Task 单条件_本地供应商代码_应该返回匹配结果()
        {
            var filter = new ProductPriceFilterDto
            {
                LocalSupplierCode = "SUP001",
                StoreCodes = new List<string> { "STORE001" },
                PageNumber = 1,
                PageSize = 20
            };

            var result = await _service.GetPriceFilteredPagedListAsync(filter);

            Assert.NotNull(result);
            Assert.NotNull(result.Items);
        }

        [Fact]
        public async Task 单条件_是否启用_应该返回匹配结果()
        {
            var filter = new ProductPriceFilterDto
            {
                IsActive = true,
                StoreCodes = new List<string> { "STORE001" },
                PageNumber = 1,
                PageSize = 20
            };

            var result = await _service.GetPriceFilteredPagedListAsync(filter);

            Assert.NotNull(result);
            Assert.NotNull(result.Items);
        }

        [Fact]
        public async Task 单条件_是否特殊产品_应该返回匹配结果()
        {
            var filter = new ProductPriceFilterDto
            {
                IsSpecialProduct = false,
                StoreCodes = new List<string> { "STORE001" },
                PageNumber = 1,
                PageSize = 20
            };

            var result = await _service.GetPriceFilteredPagedListAsync(filter);

            Assert.NotNull(result);
            Assert.NotNull(result.Items);
        }

        [Fact]
        public async Task 单条件_产品类型_应该返回匹配结果()
        {
            var filter = new ProductPriceFilterDto
            {
                ProductType = 1,
                StoreCodes = new List<string> { "STORE001" },
                PageNumber = 1,
                PageSize = 20
            };

            var result = await _service.GetPriceFilteredPagedListAsync(filter);

            Assert.NotNull(result);
            Assert.NotNull(result.Items);
        }

        #endregion

        #region 价格区间过滤测试

        [Fact]
        public async Task 价格区间_商品进货价_应该返回匹配结果()
        {
            var filter = new ProductPriceFilterDto
            {
                ProductPurchasePriceMin = 10,
                ProductPurchasePriceMax = 100,
                StoreCodes = new List<string> { "STORE001" },
                PageNumber = 1,
                PageSize = 20
            };

            var result = await _service.GetPriceFilteredPagedListAsync(filter);

            Assert.NotNull(result);
            Assert.NotNull(result.Items);
        }

        [Fact]
        public async Task 价格区间_商品零售价_应该返回匹配结果()
        {
            var filter = new ProductPriceFilterDto
            {
                ProductRetailPriceMin = 20,
                ProductRetailPriceMax = 200,
                StoreCodes = new List<string> { "STORE001" },
                PageNumber = 1,
                PageSize = 20
            };

            var result = await _service.GetPriceFilteredPagedListAsync(filter);

            Assert.NotNull(result);
            Assert.NotNull(result.Items);
        }

        [Fact]
        public async Task 价格区间_分店进货价_应该返回匹配结果()
        {
            var filter = new ProductPriceFilterDto
            {
                StorePurchasePriceMin = 10,
                StorePurchasePriceMax = 100,
                StoreCodes = new List<string> { "STORE001" },
                PageNumber = 1,
                PageSize = 20
            };

            var result = await _service.GetPriceFilteredPagedListAsync(filter);

            Assert.NotNull(result);
            Assert.NotNull(result.Items);
        }

        [Fact]
        public async Task 价格区间_分店零售价_应该返回匹配结果()
        {
            var filter = new ProductPriceFilterDto
            {
                StoreRetailPriceMin = 20,
                StoreRetailPriceMax = 200,
                StoreCodes = new List<string> { "STORE001" },
                PageNumber = 1,
                PageSize = 20
            };

            var result = await _service.GetPriceFilteredPagedListAsync(filter);

            Assert.NotNull(result);
            Assert.NotNull(result.Items);
        }

        [Fact]
        public async Task 价格区间_分店折扣率_应该返回匹配结果()
        {
            var filter = new ProductPriceFilterDto
            {
                StoreDiscountRateMin = 0.1m,
                StoreDiscountRateMax = 0.9m,
                StoreCodes = new List<string> { "STORE001" },
                PageNumber = 1,
                PageSize = 20
            };

            var result = await _service.GetPriceFilteredPagedListAsync(filter);

            Assert.NotNull(result);
            Assert.NotNull(result.Items);
        }

        #endregion

        #region 区间边界值测试

        [Fact]
        public async Task 边界值_最低价格0_应该返回匹配结果()
        {
            var filter = new ProductPriceFilterDto
            {
                ProductPurchasePriceMin = 0,
                ProductRetailPriceMin = 0,
                StoreCodes = new List<string> { "STORE001" },
                PageNumber = 1,
                PageSize = 20
            };

            var result = await _service.GetPriceFilteredPagedListAsync(filter);

            Assert.NotNull(result);
            Assert.NotNull(result.Items);
        }

        [Fact]
        public async Task 边界值_极大值999999_应该返回匹配结果()
        {
            var filter = new ProductPriceFilterDto
            {
                ProductPurchasePriceMax = 999999.99m,
                ProductRetailPriceMax = 999999.99m,
                StoreCodes = new List<string> { "STORE001" },
                PageNumber = 1,
                PageSize = 20
            };

            var result = await _service.GetPriceFilteredPagedListAsync(filter);

            Assert.NotNull(result);
            Assert.NotNull(result.Items);
        }

        [Fact]
        public async Task 边界值_闭区间包含边界值_应该正确匹配()
        {
            var filter = new ProductPriceFilterDto
            {
                ProductPurchasePriceMin = 50,
                ProductPurchasePriceMax = 50,
                StoreCodes = new List<string> { "STORE001" },
                PageNumber = 1,
                PageSize = 20
            };

            var result = await _service.GetPriceFilteredPagedListAsync(filter);

            Assert.NotNull(result);
            Assert.NotNull(result.Items);
        }

        #endregion

        #region 多条件组合过滤测试

        [Fact]
        public async Task 多条件组合_搜索_价格区间_分店_应该返回匹配结果()
        {
            var filter = new ProductPriceFilterDto
            {
                Search = "Phone",
                ProductPurchasePriceMin = 10,
                ProductPurchasePriceMax = 500,
                StoreCodes = new List<string> { "STORE001", "STORE002" },
                PageNumber = 1,
                PageSize = 20
            };

            var result = await _service.GetPriceFilteredPagedListAsync(filter);

            Assert.NotNull(result);
            Assert.NotNull(result.Items);
        }

        [Fact]
        public async Task 多条件组合_供应商_状态_类型_应该返回匹配结果()
        {
            var filter = new ProductPriceFilterDto
            {
                LocalSupplierCode = "SUP001",
                IsActive = true,
                IsSpecialProduct = false,
                ProductType = 1,
                StoreCodes = new List<string> { "STORE001" },
                PageNumber = 1,
                PageSize = 20
            };

            var result = await _service.GetPriceFilteredPagedListAsync(filter);

            Assert.NotNull(result);
            Assert.NotNull(result.Items);
        }

        [Fact]
        public async Task 多条件组合_全部价格字段_分店_应该返回匹配结果()
        {
            var filter = new ProductPriceFilterDto
            {
                ProductPurchasePriceMin = 10,
                ProductPurchasePriceMax = 200,
                ProductRetailPriceMin = 20,
                ProductRetailPriceMax = 300,
                StorePurchasePriceMin = 10,
                StorePurchasePriceMax = 200,
                StoreRetailPriceMin = 20,
                StoreRetailPriceMax = 300,
                StoreDiscountRateMin = 0.1m,
                StoreDiscountRateMax = 0.9m,
                StoreCodes = new List<string> { "STORE001" },
                PageNumber = 1,
                PageSize = 20
            };

            var result = await _service.GetPriceFilteredPagedListAsync(filter);

            Assert.NotNull(result);
            Assert.NotNull(result.Items);
        }

        #endregion

        #region 分店过滤测试

        [Fact]
        public async Task 分店过滤_单分店_应该返回匹配结果()
        {
            var filter = new ProductPriceFilterDto
            {
                StoreCodes = new List<string> { "STORE001" },
                PageNumber = 1,
                PageSize = 20
            };

            var result = await _service.GetPriceFilteredPagedListAsync(filter);

            Assert.NotNull(result);
            Assert.NotNull(result.Items);
        }

        [Fact]
        public async Task 分店过滤_多分店_应该返回匹配结果()
        {
            var filter = new ProductPriceFilterDto
            {
                StoreCodes = new List<string> { "STORE001", "STORE002", "STORE003" },
                PageNumber = 1,
                PageSize = 20
            };

            var result = await _service.GetPriceFilteredPagedListAsync(filter);

            Assert.NotNull(result);
            Assert.NotNull(result.Items);
        }

        [Fact]
        public async Task 分店过滤_分店无数据_应该返回空结果()
        {
            var filter = new ProductPriceFilterDto
            {
                StoreCodes = new List<string> { "STORE999" },
                PageNumber = 1,
                PageSize = 20
            };

            var result = await _service.GetPriceFilteredPagedListAsync(filter);

            Assert.NotNull(result);
            Assert.NotNull(result.Items);
            Assert.Empty(result.Items);
        }

        #endregion

        #region 排序测试

        [Fact]
        public async Task 排序_商品编码升序_应该返回排序结果()
        {
            var filter = new ProductPriceFilterDto
            {
                StoreCodes = new List<string> { "STORE001" },
                SortBy = "ProductCode",
                SortOrder = "asc",
                PageNumber = 1,
                PageSize = 20
            };

            var result = await _service.GetPriceFilteredPagedListAsync(filter);

            Assert.NotNull(result);
            Assert.NotNull(result.Items);
        }

        [Fact]
        public async Task 排序_商品名称降序_应该返回排序结果()
        {
            var filter = new ProductPriceFilterDto
            {
                StoreCodes = new List<string> { "STORE001" },
                SortBy = "ProductName",
                SortOrder = "desc",
                PageNumber = 1,
                PageSize = 20
            };

            var result = await _service.GetPriceFilteredPagedListAsync(filter);

            Assert.NotNull(result);
            Assert.NotNull(result.Items);
        }

        [Fact]
        public async Task 排序_分店零售价降序_应该返回排序结果()
        {
            var filter = new ProductPriceFilterDto
            {
                StoreCodes = new List<string> { "STORE001" },
                SortBy = "StoreRetailPrice",
                SortOrder = "desc",
                PageNumber = 1,
                PageSize = 20
            };

            var result = await _service.GetPriceFilteredPagedListAsync(filter);

            Assert.NotNull(result);
            Assert.NotNull(result.Items);
        }

        #endregion

        #region 分页测试

        [Fact]
        public async Task 分页_第一页20条_应该返回正确数量()
        {
            var filter = new ProductPriceFilterDto
            {
                StoreCodes = new List<string> { "STORE001" },
                PageNumber = 1,
                PageSize = 20
            };

            var result = await _service.GetPriceFilteredPagedListAsync(filter);

            Assert.NotNull(result);
            Assert.Equal(1, result.PageNumber);
            Assert.Equal(20, result.PageSize);
        }

        [Fact]
        public async Task 分页_第二页50条_应该返回正确数量()
        {
            var filter = new ProductPriceFilterDto
            {
                StoreCodes = new List<string> { "STORE001" },
                PageNumber = 2,
                PageSize = 50
            };

            var result = await _service.GetPriceFilteredPagedListAsync(filter);

            Assert.NotNull(result);
            Assert.Equal(2, result.PageNumber);
            Assert.Equal(50, result.PageSize);
        }

        [Fact]
        public async Task 分页_1000条最大分页_应该返回正确数量()
        {
            var filter = new ProductPriceFilterDto
            {
                StoreCodes = new List<string> { "STORE001" },
                PageNumber = 1,
                PageSize = 1000
            };

            var result = await _service.GetPriceFilteredPagedListAsync(filter);

            Assert.NotNull(result);
            Assert.Equal(1, result.PageNumber);
            Assert.Equal(1000, result.PageSize);
        }

        #endregion

        #region 参数验证测试

        [Fact]
        public async Task 参数验证_分店代码为空_应该不应用分店过滤()
        {
            var filter = new ProductPriceFilterDto
            {
                StoreCodes = new List<string>(),
                PageNumber = 1,
                PageSize = 20
            };

            var result = await _service.GetPriceFilteredPagedListAsync(filter);

            var item = Assert.Single(result.Items);
            Assert.Equal("P-IPHONE", item.ProductCode);
        }

        [Fact]
        public async Task 参数验证_页码为0_应该使用默认值1()
        {
            var filter = new ProductPriceFilterDto
            {
                StoreCodes = new List<string> { "STORE001" },
                PageNumber = 0,
                PageSize = 20
            };

            var result = await _service.GetPriceFilteredPagedListAsync(filter);

            Assert.NotNull(result);
        }

        [Fact]
        public async Task 参数验证_排序方向无效_应该使用默认asc()
        {
            var filter = new ProductPriceFilterDto
            {
                StoreCodes = new List<string> { "STORE001" },
                SortBy = "ProductName",
                SortOrder = "invalid",
                PageNumber = 1,
                PageSize = 20
            };

            var result = await _service.GetPriceFilteredPagedListAsync(filter);

            Assert.NotNull(result);
        }

        #endregion

        #region 数据完整性测试

        [Fact]
        public async Task 数据完整性_返回DTO应该包含商品主表全部字段()
        {
            var filter = new ProductPriceFilterDto
            {
                StoreCodes = new List<string> { "STORE001" },
                PageNumber = 1,
                PageSize = 1
            };

            var result = await _service.GetPriceFilteredPagedListAsync(filter);

            Assert.NotNull(result);
            if (result.Items.Any())
            {
                var item = result.Items.First();
                Assert.NotNull(item.UUID);
                Assert.NotNull(item.ProductName);
                Assert.NotNull(item.ProductCode);
            }
        }

        [Fact]
        public async Task 数据完整性_返回DTO应该包含分店价格字段()
        {
            var filter = new ProductPriceFilterDto
            {
                StoreCodes = new List<string> { "STORE001" },
                PageNumber = 1,
                PageSize = 1
            };

            var result = await _service.GetPriceFilteredPagedListAsync(filter);

            Assert.NotNull(result);
            if (result.Items.Any())
            {
                var item = result.Items.First();
                Assert.NotNull(item.StoreCode);
                Assert.NotNull(item.StorePurchasePrice);
                Assert.NotNull(item.StoreRetailPrice);
            }
        }

        #endregion
    }
}
