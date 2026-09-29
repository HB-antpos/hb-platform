using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using AutoMapper;
using BlazorApp.Api.Data;
using BlazorApp.Api.Controllers.React;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services;
using BlazorApp.Api.Mappings.Profiles.React;
using BlazorApp.Api.Services.LocalSupplierCategories;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HqEntities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class ProductStoreRecordsTests : IDisposable
{
    private readonly string _localDbPath;
    private readonly string _hqDbPath;
    private readonly SqliteConnection _localConnection;
    private readonly SqliteConnection _hqConnection;
    private readonly SqlSugarClient _localDb;
    private readonly SqlSugarScope _hqDb;
    private readonly IMapper _mapper;

    public ProductStoreRecordsTests()
    {
        _localDbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        _hqDbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        _localConnection = new SqliteConnection($"Data Source={_localDbPath}");
        _hqConnection = new SqliteConnection($"Data Source={_hqDbPath}");
        _localConnection.Open();
        _hqConnection.Open();

        _localDb = new SqlSugarClient(CreateConnectionConfig(_localConnection.ConnectionString));
        _hqDb = new SqlSugarScope(CreateConnectionConfig(_hqConnection.ConnectionString));
        _mapper = CreateMapper();

        _localDb.CodeFirst.InitTables(
            typeof(Product),
            typeof(Store),
            typeof(StoreRetailPrice),
            typeof(StoreMultiCodeProduct),
            typeof(ProductSetCode),
            typeof(DomesticProduct),
            typeof(ChinaSupplier),
            typeof(UserStore),
            typeof(WarehouseCategory),
            typeof(LocalSupplierCategory),
            typeof(LocalSupplierCategoryCapture),
            typeof(LocalSupplierCategoryProductAssignment)
        );
    }

    [Fact]
    public async Task GetPagedListAsync_返回当前页商品已有分店价格记录数量且排除软删记录()
    {
        await SeedProductAsync("P001", "A001");
        await SeedProductAsync("P002", "A002");
        await SeedStoreAsync("S01", "分店一");
        await SeedStoreAsync("S02", "分店二");
        await SeedStoreRetailPriceAsync("price-1", "P001", "S01", false);
        await SeedStoreRetailPriceAsync("price-2", "P001", "S02", false);
        await SeedStoreRetailPriceAsync("price-deleted", "P001", "S03", true);

        var result = await CreateService().GetPagedListAsync(new ProductReactFilterDto
        {
            PageNumber = 1,
            PageSize = 20,
            SortBy = "productcode",
            SortOrder = "asc"
        });

        Assert.Equal(2, result.Items.Single(item => item.ProductCode == "P001").StoreRecordCount);
        Assert.Equal(0, result.Items.Single(item => item.ProductCode == "P002").StoreRecordCount);
    }

    [Fact]
    public async Task CreateWithPrices_本地自动条码已占用时拒绝重复创建()
    {
        await SeedProductAsync("existing-local", "SUP01", barcode: "9529260910009");
        var controller = new ReactProductsController(
            CreateSqlSugarContext(_localDb), NullLogger<ReactProductsController>.Instance,
            WarehouseProductChangeHistoryTestDouble.CreateNoop(),
            Mock.Of<ICurrentUserService>(), Mock.Of<IProductMaintenanceHqProjectionWriter>());
        var result = await controller.CreateWithPrices(new CreateProductWithPricesDto
        {
            ProductName = "重复条码", LocalSupplierCode = "SUP01", Barcode = "9529260910009",
            PurchasePrice = 1, RetailPrice = 2,
        });
        Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal(1, await _localDb.Queryable<Product>().CountAsync());
    }

    [Theory]
    [InlineData("200")]
    [InlineData(" 200 ")]
    [InlineData("")]
    public async Task GenerateLocalBarcode_限制供应商不能生成条码(string supplierCode)
    {
        var controller = new ReactProductsController(
            CreateSqlSugarContext(_localDb), NullLogger<ReactProductsController>.Instance,
            WarehouseProductChangeHistoryTestDouble.CreateNoop(),
            Mock.Of<ICurrentUserService>(), Mock.Of<IProductMaintenanceHqProjectionWriter>());
        Assert.IsType<BadRequestObjectResult>(await controller.GenerateLocalBarcode(supplierCode, null!));
    }

    [Fact]
    public async Task GetPagedListAsync_StoreRecordCountMin为1时仅返回有未删除分店记录的商品()
    {
        await SeedProductAsync("P001", "A001");
        await SeedProductAsync("P002", "A002");
        await SeedProductAsync("P003", "A003");
        await SeedStoreRetailPriceAsync("price-1", "P001", "S01", false);
        await SeedStoreRetailPriceAsync("price-2", "P003", "S03", true);

        var result = await CreateService().GetPagedListAsync(new ProductReactFilterDto
        {
            PageNumber = 1,
            PageSize = 20,
            StoreRecordCountMin = 1,
        });

        Assert.Equal(
            new[] { "P001" },
            result.Items.Select(item => item.ProductCode).OrderBy(code => code).ToArray()
        );
        Assert.Equal(1, result.Items.Single().StoreRecordCount);
    }

    [Fact]
    public async Task GetPagedListAsync_StoreRecordCountMinMax为0时仅返回无未删除分店记录的商品()
    {
        await SeedProductAsync("P001", "A001");
        await SeedProductAsync("P002", "A002");
        await SeedProductAsync("P003", "A003");
        await SeedStoreRetailPriceAsync("price-1", "P001", "S01", false);
        await SeedStoreRetailPriceAsync("price-2", "P003", "S03", true);

        var result = await CreateService().GetPagedListAsync(new ProductReactFilterDto
        {
            PageNumber = 1,
            PageSize = 20,
            StoreRecordCountMin = 0,
            StoreRecordCountMax = 0,
        });

        Assert.Equal(
            new[] { "P002", "P003" },
            result.Items.Select(item => item.ProductCode).OrderBy(code => code).ToArray()
        );
        Assert.All(result.Items, item => Assert.Equal(0, item.StoreRecordCount));
    }

    [Fact]
    public async Task GetPagedListAsync_按StoreRecordCount范围筛选时只返回命中区间的商品()
    {
        await SeedProductAsync("P001", "A001");
        await SeedProductAsync("P002", "A002");
        await SeedProductAsync("P003", "A003");
        await SeedProductAsync("P004", "A004");
        await SeedStoreRetailPriceAsync("price-1", "P001", "S01", false);
        await SeedStoreRetailPriceAsync("price-2", "P002", "S01", false);
        await SeedStoreRetailPriceAsync("price-3", "P002", "S02", false);
        await SeedStoreRetailPriceAsync("price-4", "P003", "S01", false);
        await SeedStoreRetailPriceAsync("price-5", "P003", "S02", false);
        await SeedStoreRetailPriceAsync("price-6", "P003", "S03", false);
        await SeedStoreRetailPriceAsync("price-7", "P004", "S04", true);

        var result = await CreateService().GetPagedListAsync(new ProductReactFilterDto
        {
            PageNumber = 1,
            PageSize = 20,
            StoreRecordCountMin = 2,
            StoreRecordCountMax = 3,
        });

        Assert.Equal(
            new[] { "P002", "P003" },
            result.Items.Select(item => item.ProductCode).OrderBy(code => code).ToArray()
        );
        Assert.Equal(
            new[] { 2, 3 },
            result.Items.OrderBy(item => item.ProductCode).Select(item => item.StoreRecordCount).ToArray()
        );
    }

    [Fact]
    public async Task GetPagedListAsync_按StoreRecordCount升降序排序时在分页前生效()
    {
        await SeedProductAsync("P001", "A001");
        await SeedProductAsync("P002", "A002");
        await SeedProductAsync("P003", "A003");
        await SeedStoreRetailPriceAsync("price-1", "P001", "S01", false);
        await SeedStoreRetailPriceAsync("price-2", "P001", "S02", false);
        await SeedStoreRetailPriceAsync("price-3", "P002", "S01", false);
        await SeedStoreRetailPriceAsync("price-4", "P003", "S03", true);

        var ascResult = await CreateService().GetPagedListAsync(new ProductReactFilterDto
        {
            PageNumber = 1,
            PageSize = 1,
            SortBy = "storerecordcount",
            SortOrder = "asc",
        });

        var descResult = await CreateService().GetPagedListAsync(new ProductReactFilterDto
        {
            PageNumber = 1,
            PageSize = 1,
            SortBy = "storerecordcount",
            SortOrder = "desc",
        });

        Assert.Equal("P003", ascResult.Items.Single().ProductCode);
        Assert.Equal(0, ascResult.Items.Single().StoreRecordCount);
        Assert.Equal("P001", descResult.Items.Single().ProductCode);
        Assert.Equal(2, descResult.Items.Single().StoreRecordCount);
    }

    [Fact]
    public async Task GetPagedListAsync_文本列头筛选支持包含等于开头和结尾()
    {
        await SeedProductAsync("P-TEXT-1", "ABC-001", productName: "Blue Cup", barcode: "930000000001");
        await SeedProductAsync("P-TEXT-2", "XYZ-002", productName: "Red Bowl", barcode: "930000000002");
        await SeedProductAsync("P-TEXT-3", "ABC-003", productName: "Green Plate", barcode: "940000000003");

        var contains = await CreateService().GetPagedListAsync(new ProductReactFilterDto
        {
            PageNumber = 1,
            PageSize = 20,
            ProductName = "cup",
            ProductNameFilterType = TextFilterType.contains,
        });
        var equals = await CreateService().GetPagedListAsync(new ProductReactFilterDto
        {
            PageNumber = 1,
            PageSize = 20,
            Barcode = "930000000002",
            BarcodeFilterType = TextFilterType.equals,
        });
        var startsWith = await CreateService().GetPagedListAsync(new ProductReactFilterDto
        {
            PageNumber = 1,
            PageSize = 20,
            ItemNumber = "ABC",
            ItemNumberFilterType = TextFilterType.startsWith,
            SortBy = "itemnumber",
            SortOrder = "asc",
        });
        var endsWith = await CreateService().GetPagedListAsync(new ProductReactFilterDto
        {
            PageNumber = 1,
            PageSize = 20,
            ProductCode = "TEXT-3",
            ProductCodeFilterType = TextFilterType.endsWith,
        });

        Assert.Equal(new[] { "P-TEXT-1" }, contains.Items.Select(item => item.ProductCode).ToArray());
        Assert.Equal(new[] { "P-TEXT-2" }, equals.Items.Select(item => item.ProductCode).ToArray());
        Assert.Equal(new[] { "P-TEXT-1", "P-TEXT-3" }, startsWith.Items.Select(item => item.ProductCode).ToArray());
        Assert.Equal(new[] { "P-TEXT-3" }, endsWith.Items.Select(item => item.ProductCode).ToArray());
    }

    [Fact]
    public async Task GetPagedListAsync_数字列头筛选支持等于范围大于等于和小于等于()
    {
        await SeedProductAsync("P-NUM-1", "N001", purchasePrice: 1.25m, retailPrice: 2.50m);
        await SeedProductAsync("P-NUM-2", "N002", purchasePrice: 5.00m, retailPrice: 8.00m);
        await SeedProductAsync("P-NUM-3", "N003", purchasePrice: 9.99m, retailPrice: 12.00m);

        var equals = await CreateService().GetPagedListAsync(new ProductReactFilterDto
        {
            PageNumber = 1,
            PageSize = 20,
            PurchasePriceMin = 5.00m,
            PurchasePriceFilterType = NumberFilterType.equals,
        });
        var between = await CreateService().GetPagedListAsync(new ProductReactFilterDto
        {
            PageNumber = 1,
            PageSize = 20,
            RetailPriceMin = 2.50m,
            RetailPriceMax = 8.00m,
            RetailPriceFilterType = NumberFilterType.between,
            SortBy = "retailprice",
            SortOrder = "asc",
        });
        var greaterThanOrEqual = await CreateService().GetPagedListAsync(new ProductReactFilterDto
        {
            PageNumber = 1,
            PageSize = 20,
            PurchasePriceMin = 5.00m,
            PurchasePriceFilterType = NumberFilterType.greaterThanOrEqual,
        });
        var lessThanOrEqual = await CreateService().GetPagedListAsync(new ProductReactFilterDto
        {
            PageNumber = 1,
            PageSize = 20,
            RetailPriceMin = 8.00m,
            RetailPriceFilterType = NumberFilterType.lessThanOrEqual,
        });

        Assert.Equal(new[] { "P-NUM-2" }, equals.Items.Select(item => item.ProductCode).ToArray());
        Assert.Equal(new[] { "P-NUM-1", "P-NUM-2" }, between.Items.Select(item => item.ProductCode).ToArray());
        Assert.Equal(new[] { "P-NUM-2", "P-NUM-3" }, greaterThanOrEqual.Items.Select(item => item.ProductCode).OrderBy(code => code).ToArray());
        Assert.Equal(new[] { "P-NUM-1", "P-NUM-2" }, lessThanOrEqual.Items.Select(item => item.ProductCode).OrderBy(code => code).ToArray());
    }

    [Fact]
    public async Task GetPagedListAsync_日期列头筛选按自然日和范围生效()
    {
        await SeedProductAsync("P-DATE-1", "D001", createdAt: new DateTime(2026, 6, 14, 23, 30, 0), updatedAt: new DateTime(2026, 6, 15, 8, 0, 0));
        await SeedProductAsync("P-DATE-2", "D002", createdAt: new DateTime(2026, 6, 15, 9, 0, 0), updatedAt: new DateTime(2026, 6, 16, 12, 0, 0));
        await SeedProductAsync("P-DATE-3", "D003", createdAt: new DateTime(2026, 6, 16, 10, 0, 0), updatedAt: new DateTime(2026, 6, 17, 18, 0, 0));

        var createdOnJune15 = await CreateService().GetPagedListAsync(new ProductReactFilterDto
        {
            PageNumber = 1,
            PageSize = 20,
            CreatedAtFrom = new DateTime(2026, 6, 15),
            CreatedAtToExclusive = new DateTime(2026, 6, 16),
        });
        var updatedRange = await CreateService().GetPagedListAsync(new ProductReactFilterDto
        {
            PageNumber = 1,
            PageSize = 20,
            UpdatedAtFrom = new DateTime(2026, 6, 16),
            UpdatedAtToExclusive = new DateTime(2026, 6, 18),
            SortBy = "updatedat",
            SortOrder = "asc",
        });

        Assert.Equal(new[] { "P-DATE-2" }, createdOnJune15.Items.Select(item => item.ProductCode).ToArray());
        Assert.Equal(new[] { "P-DATE-2", "P-DATE-3" }, updatedRange.Items.Select(item => item.ProductCode).ToArray());
        Assert.All(createdOnJune15.Items, item => Assert.NotNull(item.CreatedAt));
    }

    [Fact]
    public async Task GetPagedListAsync_枚举和布尔列头筛选使用多选精确匹配()
    {
        await SeedProductAsync("P-ENUM-1", "E001", localSupplierCode: "SUP-A", isActive: true, isAutoPricing: true, productType: null);
        await SeedProductAsync("P-ENUM-2", "E002", localSupplierCode: "SUP-B", isActive: false, isAutoPricing: false, productType: 1);
        await SeedProductAsync("P-ENUM-3", "E003", localSupplierCode: "SUP-C", isActive: true, isAutoPricing: true, productType: 2);

        var suppliers = await CreateService().GetPagedListAsync(new ProductReactFilterDto
        {
            PageNumber = 1,
            PageSize = 20,
            LocalSupplierCodes = new List<string> { "SUP-A", "SUP-C" },
        });
        var activeAndAutoPricing = await CreateService().GetPagedListAsync(new ProductReactFilterDto
        {
            PageNumber = 1,
            PageSize = 20,
            IsActiveValues = new List<bool> { true },
            IsAutoPricingValues = new List<bool> { true },
        });
        var normalAndMultiCode = await CreateService().GetPagedListAsync(new ProductReactFilterDto
        {
            PageNumber = 1,
            PageSize = 20,
            ProductTypeValues = new List<int> { 0, 2 },
        });

        Assert.Equal(new[] { "P-ENUM-1", "P-ENUM-3" }, suppliers.Items.Select(item => item.ProductCode).OrderBy(code => code).ToArray());
        Assert.Equal(new[] { "P-ENUM-1", "P-ENUM-3" }, activeAndAutoPricing.Items.Select(item => item.ProductCode).OrderBy(code => code).ToArray());
        Assert.Equal(new[] { "P-ENUM-1", "P-ENUM-3" }, normalAndMultiCode.Items.Select(item => item.ProductCode).OrderBy(code => code).ToArray());
    }

    [Fact]
    public async Task GetPagedListAsync_顶部单品筛选应包含历史空商品类型()
    {
        await SeedProductAsync("P-TYPE-NULL", "T001", productType: null);
        await SeedProductAsync("P-TYPE-ZERO", "T002", productType: 0);
        await SeedProductAsync("P-TYPE-SET", "T003", productType: 1);

        var normalProducts = await CreateService().GetPagedListAsync(new ProductReactFilterDto
        {
            PageNumber = 1,
            PageSize = 20,
            ProductType = 0,
        });

        Assert.Equal(
            new[] { "P-TYPE-NULL", "P-TYPE-ZERO" },
            normalProducts.Items.Select(item => item.ProductCode).OrderBy(code => code).ToArray()
        );
    }

    [Fact]
    public async Task GetPagedListAsync_新增排序字段不会落回默认更新时间排序()
    {
        await SeedProductAsync("P-SORT-2", "S002", barcode: "222", updatedAt: new DateTime(2026, 6, 16));
        await SeedProductAsync("P-SORT-1", "S001", barcode: "111", updatedAt: new DateTime(2026, 6, 17));

        var byBarcode = await CreateService().GetPagedListAsync(new ProductReactFilterDto
        {
            PageNumber = 1,
            PageSize = 20,
            SortBy = "barcode",
            SortOrder = "asc",
        });
        var byProductCode = await CreateService().GetPagedListAsync(new ProductReactFilterDto
        {
            PageNumber = 1,
            PageSize = 20,
            SortBy = "productcode",
            SortOrder = "asc",
        });

        Assert.Equal(new[] { "P-SORT-1", "P-SORT-2" }, byBarcode.Items.Select(item => item.ProductCode).ToArray());
        Assert.Equal(new[] { "P-SORT-1", "P-SORT-2" }, byProductCode.Items.Select(item => item.ProductCode).ToArray());
    }

    [Fact]
    public async Task GetPagedListAsync_分店记录数量使用预聚合查询避免逐行相关计数()
    {
        await SeedProductAsync("P001", "A001");
        await SeedProductAsync("P002", "A002");
        await SeedStoreRetailPriceAsync("price-active-1", "P001", "S01", false);
        await SeedStoreRetailPriceAsync("price-active-2", "P001", "S02", false);
        await SeedStoreRetailPriceAsync("price-deleted-1", "P002", "S03", true);
        await SeedStoreRetailPriceAsync("price-deleted-2", "P002", "S04", true);

        var executedSql = new List<string>();
        _localDb.Aop.OnLogExecuting = (sql, _) => executedSql.Add(sql);

        try
        {
            var result = await CreateService().GetPagedListAsync(new ProductReactFilterDto
            {
                PageNumber = 1,
                PageSize = 20,
                StoreRecordCountMin = 0,
                StoreRecordCountMax = 0,
            });

            Assert.Equal(new[] { "P002" }, result.Items.Select(item => item.ProductCode).ToArray());
            Assert.Equal(0, result.Items.Single().StoreRecordCount);
        }
        finally
        {
            _localDb.Aop.OnLogExecuting = null;
        }

        var storeRecordSql = string.Join(
            "\n",
            executedSql.Where(sql => sql.Contains("StoreRetailPrice", StringComparison.OrdinalIgnoreCase))
        );
        Assert.Contains("JOIN", storeRecordSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GROUP BY", storeRecordSql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "`record`.`ProductCode` = `p`.`ProductCode`",
            storeRecordSql,
            StringComparison.OrdinalIgnoreCase
        );
        Assert.DoesNotContain(
            "[record].[ProductCode] = [p].[ProductCode]",
            storeRecordSql,
            StringComparison.OrdinalIgnoreCase
        );
    }

    [Fact]
    public async Task GetPagedListAsync_未按聚合字段筛选排序时只为当前页商品聚合分店记录()
    {
        await SeedProductAsync("P001", "A001", updatedAt: new DateTime(2026, 1, 3));
        await SeedProductAsync("P002", "A002", updatedAt: new DateTime(2026, 1, 2));
        await SeedProductAsync("P003", "A003", updatedAt: new DateTime(2026, 1, 1));
        await SeedStoreRetailPriceAsync("price-p1-1", "P001", "S01", false);
        await SeedStoreRetailPriceAsync("price-p1-2", "P001", "S02", false);
        await SeedStoreRetailPriceAsync("price-p3-1", "P003", "S01", false);
        await SeedChinaSupplierAsync("SUP-CN-1", "国内供应商一");
        await SeedDomesticProductAsync("P002", "SUP-CN-1");

        var executedSql = new List<string>();
        _localDb.Aop.OnLogExecuting = (sql, _) => executedSql.Add(sql);

        PagedListReactDto<ProductDto> result;
        try
        {
            result = await CreateService().GetPagedListAsync(new ProductReactFilterDto
            {
                PageNumber = 1,
                PageSize = 2,
            });
        }
        finally
        {
            _localDb.Aop.OnLogExecuting = null;
        }

        // 总数仍是全部命中商品，分页与默认更新时间倒序不受快路径影响。
        Assert.Equal(3, result.Total);
        Assert.Equal(new[] { "P001", "P002" }, result.Items.Select(item => item.ProductCode).ToArray());
        Assert.Equal(new[] { 2, 0 }, result.Items.Select(item => item.StoreRecordCount).ToArray());
        Assert.Equal("SUP-CN-1", result.Items[1].DomesticSupplierCode);
        Assert.Equal("国内供应商一", result.Items[1].DomesticSupplierName);
        Assert.Null(result.Items[0].DomesticSupplierCode);

        // 分店价格表只应按当前页编码聚合一次，不得再与商品表联接做全表预聚合。
        var storeRecordSql = executedSql
            .Where(sql => sql.Contains("StoreRetailPrice", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var storeRecordStatement = Assert.Single(storeRecordSql);
        Assert.Contains("GROUP BY", storeRecordStatement, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("'P001'", storeRecordStatement, StringComparison.Ordinal);
        Assert.DoesNotContain("'P003'", storeRecordStatement, StringComparison.Ordinal);
        Assert.DoesNotContain("JOIN", storeRecordStatement, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetPagedListAsync_关键词搜索覆盖英文名称()
    {
        await SeedProductAsync("P-EN-1", "E001", productName: "陶瓷杯");
        await SeedProductAsync("P-EN-2", "E002", productName: "玻璃杯");
        await _localDb.Updateable<Product>()
            .SetColumns(product => product.EnglishName == "Ceramic Mug")
            .Where(product => product.ProductCode == "P-EN-1")
            .ExecuteCommandAsync();

        var result = await CreateService().GetPagedListAsync(new ProductReactFilterDto
        {
            PageNumber = 1,
            PageSize = 20,
            Search = "ceramic",
        });

        Assert.Equal(new[] { "P-EN-1" }, result.Items.Select(item => item.ProductCode).ToArray());
    }

    [Fact]
    public async Task GetPagedListAsync_带关键词时按商品编码作次级排序键()
    {
        var sameUpdatedAt = new DateTime(2026, 2, 1);
        await SeedProductAsync("P-KW-B", "K002", productName: "Candle B", updatedAt: sameUpdatedAt);
        await SeedProductAsync("P-KW-A", "K001", productName: "Candle A", updatedAt: sameUpdatedAt);
        await SeedProductAsync("P-KW-C", "K003", productName: "Candle C", updatedAt: new DateTime(2026, 1, 1));

        var executedSql = new List<string>();
        _localDb.Aop.OnLogExecuting = (sql, _) => executedSql.Add(sql);

        PagedListReactDto<ProductDto> result;
        try
        {
            result = await CreateService().GetPagedListAsync(new ProductReactFilterDto
            {
                PageNumber = 1,
                PageSize = 20,
                Search = "candle",
            });
        }
        finally
        {
            _localDb.Aop.OnLogExecuting = null;
        }

        // 更新时间相同的商品按商品编码升序，分页顺序确定。
        Assert.Equal(
            new[] { "P-KW-A", "P-KW-B", "P-KW-C" },
            result.Items.Select(item => item.ProductCode).ToArray()
        );
        var pageSql = Assert.Single(
            executedSql,
            sql => sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase)
                && !sql.Contains("COUNT(", StringComparison.OrdinalIgnoreCase)
        );
        var orderBy = pageSql[pageSql.LastIndexOf("ORDER BY", StringComparison.OrdinalIgnoreCase)..];
        Assert.Contains("ProductCode", orderBy, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetPagedListAsync_国内供应商代码名称来自未删除映射且未映射商品保留()
    {
        await SeedProductAsync("P-MAPPED", "M001");
        await SeedProductAsync("P-UNMAPPED", "U001");
        await SeedProductAsync("P-DELETED-MAP", "D001");
        await SeedProductAsync("P-DELETED-SUPPLIER", "S001");
        await SeedProductAsync("P-MISSING-SUPPLIER", "X001");
        await SeedDomesticProductAsync("P-MAPPED", "SUP-CN-1");
        await SeedDomesticProductAsync("P-DELETED-MAP", "SUP-CN-DEL", isDeleted: true);
        await SeedDomesticProductAsync("P-DELETED-SUPPLIER", "SUP-CN-2");
        await SeedDomesticProductAsync("P-MISSING-SUPPLIER", "SUP-CN-MISSING");
        await SeedChinaSupplierAsync("SUP-CN-1", "国内供应商甲");
        await SeedChinaSupplierAsync("SUP-CN-2", "国内供应商乙", isDeleted: true);

        var result = await CreateService().GetPagedListAsync(new ProductReactFilterDto
        {
            PageNumber = 1,
            PageSize = 20,
            SortBy = "productcode",
            SortOrder = "asc",
        });

        var mapped = result.Items.Single(item => item.ProductCode == "P-MAPPED");
        Assert.Equal("SUP-CN-1", mapped.DomesticSupplierCode);
        Assert.Equal("国内供应商甲", mapped.DomesticSupplierName);

        var unmapped = result.Items.Single(item => item.ProductCode == "P-UNMAPPED");
        Assert.Null(unmapped.DomesticSupplierCode);
        Assert.Null(unmapped.DomesticSupplierName);

        var deletedMap = result.Items.Single(item => item.ProductCode == "P-DELETED-MAP");
        Assert.Null(deletedMap.DomesticSupplierCode);
        Assert.Null(deletedMap.DomesticSupplierName);

        var deletedSupplier = result.Items.Single(item => item.ProductCode == "P-DELETED-SUPPLIER");
        Assert.Null(deletedSupplier.DomesticSupplierCode);
        Assert.Null(deletedSupplier.DomesticSupplierName);

        var missingSupplier = result.Items.Single(item => item.ProductCode == "P-MISSING-SUPPLIER");
        Assert.Null(missingSupplier.DomesticSupplierCode);
        Assert.Null(missingSupplier.DomesticSupplierName);
    }

    [Fact]
    public async Task GetByIdAsync_国内供应商字段仅来自未删除完整映射()
    {
        await SeedProductAsync("P-DETAIL-MAPPED", "D001");
        await SeedDomesticProductAsync("P-DETAIL-MAPPED", "SUP-DETAIL");
        await SeedChinaSupplierAsync("SUP-DETAIL", "详情供应商");

        await SeedProductAsync("P-DETAIL-DELETED", "D002");
        await SeedDomesticProductAsync("P-DETAIL-DELETED", "SUP-DELETED");
        await SeedChinaSupplierAsync("SUP-DELETED", "已删除供应商", isDeleted: true);

        var mapped = await CreateService().GetByIdAsync("P-DETAIL-MAPPED");
        Assert.True(mapped.Success, mapped.Message);
        Assert.Equal("SUP-DETAIL", mapped.Data?.DomesticSupplierCode);
        Assert.Equal("详情供应商", mapped.Data?.DomesticSupplierName);

        var deleted = await CreateService().GetByIdAsync("P-DETAIL-DELETED");
        Assert.True(deleted.Success, deleted.Message);
        Assert.Null(deleted.Data?.DomesticSupplierCode);
        Assert.Null(deleted.Data?.DomesticSupplierName);
    }

    [Fact]
    public async Task GetPagedListAsync_国内供应商多选过滤只返回映射命中商品()
    {
        await SeedProductAsync("P-SUP-A", "A001");
        await SeedProductAsync("P-SUP-B", "B001");
        await SeedProductAsync("P-SUP-C", "C001");
        await SeedProductAsync("P-SUP-NONE", "N001");
        await SeedDomesticProductAsync("P-SUP-A", "SUP-A");
        await SeedDomesticProductAsync("P-SUP-B", "SUP-B");
        await SeedDomesticProductAsync("P-SUP-C", "SUP-C");
        await SeedChinaSupplierAsync("SUP-A", "供应商A");
        await SeedChinaSupplierAsync("SUP-B", "供应商B");
        await SeedChinaSupplierAsync("SUP-C", "供应商C");

        var result = await CreateService().GetPagedListAsync(new ProductReactFilterDto
        {
            PageNumber = 1,
            PageSize = 20,
            DomesticSupplierCodes = new List<string> { "SUP-A", "SUP-C" },
            SortBy = "productcode",
            SortOrder = "asc",
        });

        Assert.Equal(
            new[] { "P-SUP-A", "P-SUP-C" },
            result.Items.Select(item => item.ProductCode).ToArray()
        );
        Assert.Equal(2, result.Total);
    }

    [Fact]
    public async Task GetPagedListAsync_仓库分类多选过滤且复数优先于单值()
    {
        await SeedProductAsync("P-WH-A", "A001", warehouseCategoryGuid: "WH-A");
        await SeedProductAsync("P-WH-B", "B001", warehouseCategoryGuid: "WH-B");
        await SeedProductAsync("P-WH-C", "C001", warehouseCategoryGuid: "WH-C");
        await SeedProductAsync("P-WH-NONE", "N001");

        var plural = await CreateService().GetPagedListAsync(new ProductReactFilterDto
        {
            PageNumber = 1,
            PageSize = 20,
            WarehouseCategoryGUIDs = new List<string> { "WH-A", "WH-B" },
            SortBy = "productcode",
            SortOrder = "asc",
        });
        Assert.Equal(
            new[] { "P-WH-A", "P-WH-B" },
            plural.Items.Select(item => item.ProductCode).ToArray()
        );

        var pluralWins = await CreateService().GetPagedListAsync(new ProductReactFilterDto
        {
            PageNumber = 1,
            PageSize = 20,
            WarehouseCategoryGUID = "WH-C",
            WarehouseCategoryGUIDs = new List<string> { "WH-A" },
            SortBy = "productcode",
            SortOrder = "asc",
        });
        Assert.Equal(
            new[] { "P-WH-A" },
            pluralWins.Items.Select(item => item.ProductCode).ToArray()
        );

        var single = await CreateService().GetPagedListAsync(new ProductReactFilterDto
        {
            PageNumber = 1,
            PageSize = 20,
            WarehouseCategoryGUID = "WH-C",
            SortBy = "productcode",
            SortOrder = "asc",
        });
        Assert.Equal(
            new[] { "P-WH-C" },
            single.Items.Select(item => item.ProductCode).ToArray()
        );
    }

    [Fact]
    public async Task GetPagedListAsync_商品分类与仓库分类多选独立且AND组合过滤()
    {
        await SeedProductAsync("P-PC-A-WH-1", "A001", categoryGuid: "PC-A", warehouseCategoryGuid: "WH-1");
        await SeedProductAsync("P-PC-A-WH-2", "A002", categoryGuid: "PC-A", warehouseCategoryGuid: "WH-2");
        await SeedProductAsync("P-PC-B-WH-1", "B001", categoryGuid: "PC-B", warehouseCategoryGuid: "WH-1");
        await SeedProductAsync("P-PC-B-WH-2", "B002", categoryGuid: "PC-B", warehouseCategoryGuid: "WH-2");

        var byProductCategory = await CreateService().GetPagedListAsync(new ProductReactFilterDto
        {
            PageNumber = 1,
            PageSize = 20,
            ProductCategoryGUIDs = new List<string> { "PC-A" },
            SortBy = "productcode",
            SortOrder = "asc",
        });
        Assert.Equal(
            new[] { "P-PC-A-WH-1", "P-PC-A-WH-2" },
            byProductCategory.Items.Select(item => item.ProductCode).ToArray()
        );

        var andCombination = await CreateService().GetPagedListAsync(new ProductReactFilterDto
        {
            PageNumber = 1,
            PageSize = 20,
            ProductCategoryGUIDs = new List<string> { "PC-A", "PC-B" },
            WarehouseCategoryGUIDs = new List<string> { "WH-1" },
            SortBy = "productcode",
            SortOrder = "asc",
        });
        Assert.Equal(
            new[] { "P-PC-A-WH-1", "P-PC-B-WH-1" },
            andCombination.Items.Select(item => item.ProductCode).ToArray()
        );
    }

    [Fact]
    public async Task GetPagedListAsync_国内供应商与仓库分类筛选在分页和计数前生效()
    {
        await SeedProductAsync("P01", "A001", warehouseCategoryGuid: "WH-1");
        await SeedProductAsync("P02", "A002", warehouseCategoryGuid: "WH-1");
        await SeedProductAsync("P03", "A003", warehouseCategoryGuid: "WH-2");
        await SeedProductAsync("P04", "A004", warehouseCategoryGuid: "WH-2");
        await SeedProductAsync("P05", "A005", warehouseCategoryGuid: "WH-2");
        await SeedDomesticProductAsync("P01", "SUP-A");
        await SeedDomesticProductAsync("P02", "SUP-B");
        await SeedDomesticProductAsync("P03", "SUP-A");
        await SeedDomesticProductAsync("P04", "SUP-B");
        await SeedDomesticProductAsync("P05", "SUP-C");
        await SeedChinaSupplierAsync("SUP-A", "供应商A");
        await SeedChinaSupplierAsync("SUP-B", "供应商B");
        await SeedChinaSupplierAsync("SUP-C", "供应商C");

        var domestic = await CreateService().GetPagedListAsync(new ProductReactFilterDto
        {
            PageNumber = 1,
            PageSize = 1,
            DomesticSupplierCodes = new List<string> { "SUP-A", "SUP-B" },
            SortBy = "productcode",
            SortOrder = "asc",
        });
        Assert.Equal(4, domestic.Total);
        Assert.Equal(new[] { "P01" }, domestic.Items.Select(item => item.ProductCode).ToArray());

        var warehouse = await CreateService().GetPagedListAsync(new ProductReactFilterDto
        {
            PageNumber = 2,
            PageSize = 1,
            WarehouseCategoryGUIDs = new List<string> { "WH-2" },
            SortBy = "productcode",
            SortOrder = "asc",
        });
        Assert.Equal(3, warehouse.Total);
        Assert.Equal(new[] { "P04" }, warehouse.Items.Select(item => item.ProductCode).ToArray());
    }

    [Fact]
    public async Task GetPagedListAsync_国内供应商映射唯一性不产生重复行()
    {
        await SeedProductAsync("P-DUP", "D001");
        await SeedDomesticProductAsync("P-DUP", "SUP-DUP");
        await SeedChinaSupplierAsync("SUP-DUP", "同一供应商");
        await SeedChinaSupplierAsync("SUP-DUP", "同一供应商", guid: "supplier-guid-dup-2");

        var result = await CreateService().GetPagedListAsync(new ProductReactFilterDto
        {
            PageNumber = 1,
            PageSize = 20,
            DomesticSupplierCodes = new List<string> { "SUP-DUP" },
        });

        var item = Assert.Single(result.Items);
        Assert.Equal("P-DUP", item.ProductCode);
        Assert.Equal("SUP-DUP", item.DomesticSupplierCode);
        Assert.Equal("同一供应商", item.DomesticSupplierName);
    }

    [Fact]
    public async Task GetPagedListAsync_国内供应商代码和仓库分类排序不落回默认更新时间排序()
    {
        await SeedProductAsync("P-SORT-CN-2", "C002", warehouseCategoryGuid: "WH-2", updatedAt: new DateTime(2026, 6, 16));
        await SeedProductAsync("P-SORT-CN-1", "C001", warehouseCategoryGuid: "WH-1", updatedAt: new DateTime(2026, 6, 18));
        await SeedProductAsync("P-SORT-CN-3", "C003", warehouseCategoryGuid: "WH-3", updatedAt: new DateTime(2026, 6, 17));
        await SeedDomesticProductAsync("P-SORT-CN-1", "SUP-Z");
        await SeedDomesticProductAsync("P-SORT-CN-2", "SUP-A");
        await SeedDomesticProductAsync("P-SORT-CN-3", "SUP-M");
        await SeedChinaSupplierAsync("SUP-Z", "供应商Z");
        await SeedChinaSupplierAsync("SUP-A", "供应商A");
        await SeedChinaSupplierAsync("SUP-M", "供应商M");

        var bySupplierAsc = await CreateService().GetPagedListAsync(new ProductReactFilterDto
        {
            PageNumber = 1,
            PageSize = 20,
            SortBy = "domesticsuppliercode",
            SortOrder = "asc",
        });
        Assert.Equal(
            new[] { "P-SORT-CN-2", "P-SORT-CN-3", "P-SORT-CN-1" },
            bySupplierAsc.Items.Select(item => item.ProductCode).ToArray()
        );

        var bySupplierDesc = await CreateService().GetPagedListAsync(new ProductReactFilterDto
        {
            PageNumber = 1,
            PageSize = 20,
            SortBy = "domesticsuppliercode",
            SortOrder = "desc",
        });
        Assert.Equal(
            new[] { "P-SORT-CN-1", "P-SORT-CN-3", "P-SORT-CN-2" },
            bySupplierDesc.Items.Select(item => item.ProductCode).ToArray()
        );

        var byWarehouseAsc = await CreateService().GetPagedListAsync(new ProductReactFilterDto
        {
            PageNumber = 1,
            PageSize = 20,
            SortBy = "warehousecategoryguid",
            SortOrder = "asc",
        });
        Assert.Equal(
            new[] { "P-SORT-CN-1", "P-SORT-CN-2", "P-SORT-CN-3" },
            byWarehouseAsc.Items.Select(item => item.ProductCode).ToArray()
        );
    }

    [Fact]
    public async Task GetPagedListAsync_新增排序字段同值时按商品代码稳定分页()
    {
        await SeedProductAsync("P-STABLE-2", "S002", warehouseCategoryGuid: "WH-SAME");
        await SeedProductAsync("P-STABLE-1", "S001", warehouseCategoryGuid: "WH-SAME");
        await SeedDomesticProductAsync("P-STABLE-2", "SUP-SAME");
        await SeedDomesticProductAsync("P-STABLE-1", "SUP-SAME");
        await SeedChinaSupplierAsync("SUP-SAME", "相同供应商");

        var supplierPage1 = await CreateService().GetPagedListAsync(new ProductReactFilterDto
        {
            PageNumber = 1,
            PageSize = 1,
            SortBy = "domesticsuppliercode",
            SortOrder = "desc",
        });
        var supplierPage2 = await CreateService().GetPagedListAsync(new ProductReactFilterDto
        {
            PageNumber = 2,
            PageSize = 1,
            SortBy = "domesticsuppliercode",
            SortOrder = "desc",
        });
        Assert.Equal("P-STABLE-1", Assert.Single(supplierPage1.Items).ProductCode);
        Assert.Equal("P-STABLE-2", Assert.Single(supplierPage2.Items).ProductCode);

        var warehousePage1 = await CreateService().GetPagedListAsync(new ProductReactFilterDto
        {
            PageNumber = 1,
            PageSize = 1,
            SortBy = "warehousecategoryguid",
            SortOrder = "asc",
        });
        var warehousePage2 = await CreateService().GetPagedListAsync(new ProductReactFilterDto
        {
            PageNumber = 2,
            PageSize = 1,
            SortBy = "warehousecategoryguid",
            SortOrder = "asc",
        });
        Assert.Equal("P-STABLE-1", Assert.Single(warehousePage1.Items).ProductCode);
        Assert.Equal("P-STABLE-2", Assert.Single(warehousePage2.Items).ProductCode);
    }

    [Fact]
    public async Task GetStoreRecordsAsync_只返回指定商品当前用户可访问的未删除分店记录并补充分店名称()
    {
        await SeedProductAsync("P001", "A001");
        await SeedStoreAsync("S01", "分店一");
        await SeedStoreAsync("S02", "分店二");
        await SeedStoreRetailPriceAsync("price-1", "P001", "S02", false, 1.2m, 2.5m);
        await SeedStoreRetailPriceAsync("price-2", "P001", "S01", false, 1.1m, 2.4m);
        await SeedStoreRetailPriceAsync("price-deleted", "P001", "S03", true);
        await SeedStoreRetailPriceAsync("price-other", "P002", "S01", false);

        var response = await CreateService().GetStoreRecordsAsync("P001", new[] { "S01" });

        Assert.True(response.Success, response.Message);
        var records = response.Data ?? new List<ProductStoreRecordDto>();
        Assert.Equal(new[] { "S01" }, records.Select(item => item.StoreCode).ToArray());
        Assert.Equal("分店一", records[0].StoreName);
        Assert.Equal("S01-P001", records[0].StoreProductCode);
        Assert.Equal(1.1m, records[0].PurchasePrice);
        Assert.Equal(2.4m, records[0].StoreRetailPriceValue);
    }

    [Fact]
    public async Task GetStoreRecordsAsync_按分店名称升序返回且空名称按分店代码兜底排序()
    {
        await SeedProductAsync("P001", "A001");
        await SeedStoreAsync("S01", "Beta");
        await SeedStoreAsync("S02", "Gamma");
        await SeedStoreAsync("S03", "Alpha");
        await SeedStoreAsync("S04", "Alpha");
        await SeedStoreAsync("S99", "");
        await SeedStoreRetailPriceAsync("price-beta", "P001", "S01", false);
        await SeedStoreRetailPriceAsync("price-gamma", "P001", "S02", false);
        await SeedStoreRetailPriceAsync("price-alpha-3", "P001", "S03", false);
        await SeedStoreRetailPriceAsync("price-alpha-4", "P001", "S04", false);
        await SeedStoreRetailPriceAsync("price-empty", "P001", "S99", false);

        var response = await CreateService().GetStoreRecordsAsync("P001", null);

        Assert.True(response.Success, response.Message);
        var records = response.Data ?? new List<ProductStoreRecordDto>();
        Assert.Equal(new[] { "S03", "S04", "S01", "S02", "S99" }, records.Select(item => item.StoreCode).ToArray());
    }

    [Fact]
    public async Task GetStoreRecordsAsync_当前用户没有可访问分店时返回空列表()
    {
        await SeedProductAsync("P001", "A001");
        await SeedStoreAsync("S01", "分店一");
        await SeedStoreRetailPriceAsync("price-1", "P001", "S01", false);

        var response = await CreateService().GetStoreRecordsAsync("P001", Array.Empty<string>());

        Assert.True(response.Success, response.Message);
        Assert.Empty(response.Data ?? new List<ProductStoreRecordDto>());
    }

    [Fact]
    public async Task BatchUpdateStoreRecordsAsync_只更新勾选分店()
    {
        await SeedProductAsync("P001", "A001");
        await SeedStoreAsync("S01", "分店一");
        await SeedStoreAsync("S02", "分店二");
        await SeedStoreAsync("S03", "分店三");
        await SeedStoreRetailPriceAsync("price-1", "P001", "S01", false, 1.1m, 2.1m, discountRate: 0.91m);
        await SeedStoreRetailPriceAsync("price-2", "P001", "S02", false, 1.2m, 2.2m, discountRate: 0.92m);
        await SeedStoreRetailPriceAsync("price-3", "P001", "S03", false, 1.3m, 2.3m, discountRate: 0.93m);
        var beforeS01 = await GetStoreRetailPriceAsync("P001", "S01");
        var beforeS02 = await GetStoreRetailPriceAsync("P001", "S02");
        var beforeS03 = await GetStoreRetailPriceAsync("P001", "S03");

        var response = await CreateService("batch-editor").BatchUpdateStoreRecordsAsync(
            "P001",
            new BatchUpdateProductStoreRecordsRequest
            {
                StoreCodes = new[] { "S01", "S03" },
                Changes = new BatchUpdateProductStoreRecordChangesDto
                {
                    PurchasePrice = 5.5m,
                    StoreRetailPriceValue = 8.8m,
                    DiscountRate = 0.77m,
                    IsAutoPricing = true,
                    IsSpecialProduct = true,
                    IsActive = false,
                },
            },
            null
        );

        var afterS01 = await GetStoreRetailPriceAsync("P001", "S01");
        var afterS02 = await GetStoreRetailPriceAsync("P001", "S02");
        var afterS03 = await GetStoreRetailPriceAsync("P001", "S03");

        Assert.True(response.Success, response.Message);
        Assert.NotNull(response.Data);
        Assert.Equal(2, response.Data!.SuccessCount);
        Assert.Equal(0, response.Data.FailedCount);
        Assert.Empty(response.Data.Errors);

        Assert.Equal(5.5m, afterS01!.PurchasePrice);
        Assert.Equal(8.8m, afterS01.StoreRetailPriceValue);
        Assert.Equal(0.77m, afterS01.DiscountRate);
        Assert.True(afterS01.IsAutoPricing);
        Assert.True(afterS01.IsSpecialProduct);
        Assert.False(afterS01.IsActive);
        Assert.Equal("batch-editor", afterS01.UpdatedBy);
        Assert.True(afterS01.UpdatedAt >= beforeS01!.UpdatedAt);

        Assert.Equal(beforeS02!.PurchasePrice, afterS02!.PurchasePrice);
        Assert.Equal(beforeS02.StoreRetailPriceValue, afterS02.StoreRetailPriceValue);
        Assert.Equal(beforeS02.DiscountRate, afterS02.DiscountRate);
        Assert.Equal(beforeS02.IsAutoPricing, afterS02.IsAutoPricing);
        Assert.Equal(beforeS02.IsSpecialProduct, afterS02.IsSpecialProduct);
        Assert.Equal(beforeS02.IsActive, afterS02.IsActive);
        Assert.Equal(beforeS02.UpdatedBy, afterS02.UpdatedBy);

        Assert.Equal(5.5m, afterS03!.PurchasePrice);
        Assert.Equal(8.8m, afterS03.StoreRetailPriceValue);
        Assert.Equal(0.77m, afterS03.DiscountRate);
        Assert.True(afterS03.IsAutoPricing);
        Assert.True(afterS03.IsSpecialProduct);
        Assert.False(afterS03.IsActive);
        Assert.Equal("batch-editor", afterS03.UpdatedBy);
        Assert.True(afterS03.UpdatedAt >= beforeS03!.UpdatedAt);
    }

    [Fact]
    public async Task BatchUpdateStoreRecordsAsync_字段缺省时不修改未提供字段()
    {
        await SeedProductAsync("P001", "A001");
        await SeedStoreAsync("S01", "分店一");
        await SeedStoreRetailPriceAsync("price-1", "P001", "S01", false, 1.2m, 2.4m, discountRate: 0.85m, isAutoPricing: false, isSpecialProduct: true, isActive: true);
        var before = await GetStoreRetailPriceAsync("P001", "S01");

        var response = await CreateService("partial-editor").BatchUpdateStoreRecordsAsync(
            "P001",
            new BatchUpdateProductStoreRecordsRequest
            {
                StoreCodes = new[] { "S01" },
                Changes = new BatchUpdateProductStoreRecordChangesDto
                {
                    DiscountRate = 0.66m,
                },
            },
            null
        );

        var after = await GetStoreRetailPriceAsync("P001", "S01");

        Assert.True(response.Success, response.Message);
        Assert.NotNull(response.Data);
        Assert.Equal(1, response.Data!.SuccessCount);
        Assert.Equal(0, response.Data.FailedCount);
        Assert.Equal(before!.PurchasePrice, after!.PurchasePrice);
        Assert.Equal(before.StoreRetailPriceValue, after.StoreRetailPriceValue);
        Assert.Equal(0.66m, after.DiscountRate);
        Assert.Equal(before.IsAutoPricing, after.IsAutoPricing);
        Assert.Equal(before.IsSpecialProduct, after.IsSpecialProduct);
        Assert.Equal(before.IsActive, after.IsActive);
        Assert.Equal("partial-editor", after.UpdatedBy);
    }

    [Fact]
    public async Task BatchUpdateStoreRecordsAsync_不可访问分店不更新并记录失败()
    {
        await SeedProductAsync("P001", "A001");
        await SeedStoreAsync("S01", "分店一");
        await SeedStoreAsync("S02", "分店二");
        await SeedStoreRetailPriceAsync("price-1", "P001", "S01", false, 1.1m, 2.1m);
        await SeedStoreRetailPriceAsync("price-2", "P001", "S02", false, 1.2m, 2.2m);
        var beforeDenied = await GetStoreRetailPriceAsync("P001", "S02");

        var response = await CreateService("scope-editor").BatchUpdateStoreRecordsAsync(
            "P001",
            new BatchUpdateProductStoreRecordsRequest
            {
                StoreCodes = new[] { "S01", "S02" },
                Changes = new BatchUpdateProductStoreRecordChangesDto
                {
                    PurchasePrice = 9.9m,
                },
            },
            new[] { "S01" }
        );

        var allowed = await GetStoreRetailPriceAsync("P001", "S01");
        var denied = await GetStoreRetailPriceAsync("P001", "S02");

        Assert.True(response.Success, response.Message);
        Assert.NotNull(response.Data);
        Assert.Equal(1, response.Data!.SuccessCount);
        Assert.Equal(1, response.Data.FailedCount);
        Assert.Contains(response.Data.Errors, error => error.Contains("S02"));
        Assert.Equal(9.9m, allowed!.PurchasePrice);
        Assert.Equal(beforeDenied!.PurchasePrice, denied!.PurchasePrice);
        Assert.Equal(beforeDenied.StoreRetailPriceValue, denied.StoreRetailPriceValue);
        Assert.Equal(beforeDenied.UpdatedBy, denied.UpdatedBy);
    }

    [Fact]
    public async Task BatchUpdateStoreRecordsAsync_软删记录不更新并记录失败()
    {
        await SeedProductAsync("P001", "A001");
        await SeedStoreAsync("S01", "分店一");
        await SeedStoreAsync("S02", "分店二");
        await SeedStoreRetailPriceAsync("price-1", "P001", "S01", false, 1.1m, 2.1m);
        await SeedStoreRetailPriceAsync("price-2", "P001", "S02", true, 1.2m, 2.2m);
        var beforeDeleted = await GetStoreRetailPriceAsync("P001", "S02");

        var response = await CreateService("delete-editor").BatchUpdateStoreRecordsAsync(
            "P001",
            new BatchUpdateProductStoreRecordsRequest
            {
                StoreCodes = new[] { "S01", "S02" },
                Changes = new BatchUpdateProductStoreRecordChangesDto
                {
                    IsActive = false,
                },
            },
            null
        );

        var active = await GetStoreRetailPriceAsync("P001", "S01");
        var deleted = await GetStoreRetailPriceAsync("P001", "S02");

        Assert.True(response.Success, response.Message);
        Assert.NotNull(response.Data);
        Assert.Equal(1, response.Data!.SuccessCount);
        Assert.Equal(1, response.Data.FailedCount);
        Assert.Contains(response.Data.Errors, error => error.Contains("S02"));
        Assert.False(active!.IsActive);
        Assert.True(beforeDeleted!.IsDeleted);
        Assert.Equal(beforeDeleted.PurchasePrice, deleted!.PurchasePrice);
        Assert.Equal(beforeDeleted.UpdatedBy, deleted.UpdatedBy);
    }

    [Fact]
    public void BatchUpdateStoreRecordsAsync_应只写入显式字段避免整实体覆盖未传字段()
    {
        var source = File.ReadAllText(ResolveProductReactServicePath());

        Assert.Contains("ExecuteStoreRecordPartialUpdateAsync", source);
        Assert.Contains("只写入请求显式勾选的业务字段", source);
        Assert.DoesNotContain("_db.Updateable(record).ExecuteCommandAsync()", source);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateAsync_LocalSupplierCode为空时默认写入200到商品和分店价格(string? localSupplierCode)
    {
        await SeedStoreAsync("S01", "分店一");

        var response = await CreateService("creator").CreateAsync(new CreateProductDto
        {
            ProductCode = $"P-CREATE-{Guid.NewGuid():N}",
            ProductName = "默认供应商商品",
            LocalSupplierCode = localSupplierCode,
            PurchasePrice = 1.2m,
            RetailPrice = 2.3m,
            IsActive = true,
        });

        Assert.True(response.Success, response.Message);
        var product = await _localDb.Queryable<Product>().SingleAsync(item => item.ProductCode == response.Data!.ProductCode);
        var storePrice = await _localDb.Queryable<StoreRetailPrice>().SingleAsync(item => item.ProductCode == response.Data!.ProductCode);
        Assert.Equal("200", product.LocalSupplierCode);
        Assert.Equal("200", storePrice.SupplierCode);
    }

    [Fact]
    public async Task UpdateAsync_LocalSupplierCode为空时默认写入200()
    {
        await SeedProductAsync("P-UPDATE", "A-UPDATE", localSupplierCode: "SUP01");

        var response = await CreateService("updater").UpdateAsync("P-UPDATE", new UpdateProductDto
        {
            ProductCode = "P-UPDATE",
            ProductName = "更新默认供应商",
            LocalSupplierCode = "   ",
            IsActive = true,
        });

        Assert.True(response.Success, response.Message);
        var product = await _localDb.Queryable<Product>().SingleAsync(item => item.ProductCode == "P-UPDATE");
        Assert.Equal("200", product.LocalSupplierCode);
    }

    [Fact]
    public async Task UpdateAsync_改码时LocalSupplierCode为空默认写入200()
    {
        await SeedProductAsync("P-OLD", "A-OLD", localSupplierCode: "SUP01");

        var response = await CreateService("updater").UpdateAsync("P-OLD", new UpdateProductDto
        {
            ProductCode = "P-NEW",
            ProductName = "改码默认供应商",
            LocalSupplierCode = "",
            IsActive = true,
        });

        Assert.True(response.Success, response.Message);
        var product = await _localDb.Queryable<Product>().SingleAsync(item => item.ProductCode == "P-NEW");
        Assert.Equal("200", product.LocalSupplierCode);
    }

    [Fact]
    public async Task UpdateAsync_人工指定供应商分类后锁定并在详情返回()
    {
        await SeedProductAsync("P-SC", "A-SC", localSupplierCode: "240");
        var categoryGuid = await SeedSupplierCategoryAsync("240", "/office-stationery", "Office Stationery");

        var response = await CreateService("updater").UpdateAsync("P-SC", new UpdateProductDto
        {
            ProductCode = "P-SC",
            ProductName = "指定供应商分类",
            LocalSupplierCode = "240",
            ItemNumber = "A-SC",
            IsActive = true,
            SupplierCategoryGUID = categoryGuid,
        });

        Assert.True(response.Success, response.Message);
        Assert.Equal(categoryGuid, response.Data!.SupplierCategoryGUID);
        Assert.Equal("manual", response.Data.SupplierCategorySource);
        Assert.Equal("Office Stationery", response.Data.SupplierCategoryPath);
    }

    [Fact]
    public async Task UpdateAsync_指定其他供应商的分类时返回错误码且不写入商品()
    {
        await SeedProductAsync("P-SC-MISMATCH", "A-SC", localSupplierCode: "240", productName: "原名称");
        var otherSupplierCategory = await SeedSupplierCategoryAsync("243", "/kitchen", "Kitchen");

        var response = await CreateService("updater").UpdateAsync("P-SC-MISMATCH", new UpdateProductDto
        {
            ProductCode = "P-SC-MISMATCH",
            ProductName = "不应写入",
            LocalSupplierCode = "240",
            ItemNumber = "A-SC",
            IsActive = true,
            SupplierCategoryGUID = otherSupplierCategory,
        });

        Assert.False(response.Success);
        Assert.Equal(LocalSupplierCategoryErrorCodes.CategorySupplierMismatch, response.ErrorCode);
        var product = await _localDb.Queryable<Product>().SingleAsync(item => item.ProductCode == "P-SC-MISMATCH");
        Assert.Equal("原名称", product.ProductName);
        Assert.Equal(0, await _localDb.Queryable<LocalSupplierCategoryProductAssignment>().CountAsync());
    }

    [Fact]
    public async Task BatchUpdateAsync_换供应商时清除旧供应商分类归属()
    {
        await SeedProductAsync("P-SC-BATCH", "A-SC", localSupplierCode: "240");
        var categoryGuid = await SeedSupplierCategoryAsync("240", "/office-stationery", "Office Stationery");
        await _localDb.Insertable(new LocalSupplierCategoryProductAssignment
        {
            ProductCode = "P-SC-BATCH",
            LocalSupplierCode = "240",
            CategoryGUID = categoryGuid,
            Source = "manual",
            AssignedAt = DateTime.UtcNow,
        }).ExecuteCommandAsync();

        var response = await CreateService("batcher").BatchUpdateAsync(new List<BatchUpdateProductReactDto>
        {
            new() { ProductCode = "P-SC-BATCH", LocalSupplierCode = "243" },
        });

        Assert.True(response.Success, response.Message);
        Assert.Equal(1, response.Data!.SuccessCount);
        Assert.Equal(0, await _localDb.Queryable<LocalSupplierCategoryProductAssignment>().CountAsync());
    }

    private async Task<string> SeedSupplierCategoryAsync(string supplierCode, string key, string name)
    {
        var category = new LocalSupplierCategory
        {
            LocalSupplierCode = supplierCode,
            ExternalKey = key,
            CategoryName = name,
            FullPath = name,
            Depth = 0,
            PromotionalSource = LocalSupplierCategoryPromotionalSources.Pattern,
            IsActive = true,
            IsDeleted = false,
        };
        await _localDb.Insertable(category).ExecuteCommandAsync();
        return category.CategoryGUID;
    }

    [Fact]
    public async Task UpdateAsync_仅改商品编码时向历史服务保留旧编码快照()
    {
        const string oldCode = "P-CODE-OLD";
        const string newCode = "P-CODE-NEW";
        await SeedProductAsync(oldCode, "A-CODE", localSupplierCode: "200");

        var beforeSnapshots = new Dictionary<string, WarehouseProductChangeSnapshotDto>(
            StringComparer.OrdinalIgnoreCase
        )
        {
            [oldCode] = new WarehouseProductChangeSnapshotDto
            {
                ProductCode = oldCode,
                ProductName = $"商品{oldCode}",
            },
        };
        var afterSnapshots = new Dictionary<string, WarehouseProductChangeSnapshotDto>(
            StringComparer.OrdinalIgnoreCase
        )
        {
            [newCode] = new WarehouseProductChangeSnapshotDto
            {
                ProductCode = newCode,
                ProductName = $"商品{oldCode}",
            },
        };
        IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto>? recordedBefore = null;
        var history = new Mock<IWarehouseProductChangeHistoryService>(MockBehavior.Strict);
        history
            .SetupSequence(service => service.CaptureSnapshotsAsync(
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<CancellationToken>()
            ))
            .ReturnsAsync(beforeSnapshots)
            .ReturnsAsync(afterSnapshots);
        history
            .Setup(service => service.RecordChangesAsync(
                It.IsAny<IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto>>(),
                It.IsAny<IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto>>(),
                It.IsAny<WarehouseProductChangeHistoryContextDto>(),
                It.IsAny<CancellationToken>()
            ))
            .Callback((
                IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto> before,
                IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto> _,
                WarehouseProductChangeHistoryContextDto _,
                CancellationToken _
            ) => recordedBefore = before)
            .ReturnsAsync(1);
        var currentUser = new Mock<ICurrentUserService>(MockBehavior.Strict);
        currentUser.Setup(service => service.GetCurrentUsername()).Returns("updater");
        currentUser.Setup(service => service.GetCurrentUserGuid()).Returns("updater-guid");

        var response = await CreateService("updater", history.Object, currentUser.Object)
            .UpdateAsync(oldCode, new UpdateProductDto
            {
                ProductCode = newCode,
                ProductName = $"商品{oldCode}",
                LocalSupplierCode = "200",
                ItemNumber = "A-CODE",
                Barcode = $"barcode-{oldCode}",
                PurchasePrice = 1m,
                RetailPrice = 2m,
                IsActive = true,
            });

        Assert.True(response.Success, response.Message);
        Assert.NotNull(recordedBefore);
        Assert.True(recordedBefore!.TryGetValue(newCode, out var rebound));
        Assert.Equal(oldCode, rebound!.ProductCode);
        history.VerifyAll();
    }

    [Fact]
    public async Task BatchUpdateAsync_LocalSupplierCode显式空写200且未传字段保持原值()
    {
        await SeedProductAsync("P-BATCH-EMPTY", "A-BATCH-EMPTY", localSupplierCode: "SUP01");
        await SeedProductAsync("P-BATCH-KEEP", "A-BATCH-KEEP", localSupplierCode: "KEEP01");

        var response = await CreateService("batcher").BatchUpdateAsync(new List<BatchUpdateProductReactDto>
        {
            new()
            {
                ProductCode = "P-BATCH-EMPTY",
                LocalSupplierCode = "   ",
            },
            new()
            {
                ProductCode = "P-BATCH-KEEP",
                ProductName = "只改名称",
            },
        });

        Assert.True(response.Success, response.Message);
        var emptySupplierProduct = await _localDb.Queryable<Product>().SingleAsync(item => item.ProductCode == "P-BATCH-EMPTY");
        var keepSupplierProduct = await _localDb.Queryable<Product>().SingleAsync(item => item.ProductCode == "P-BATCH-KEEP");
        Assert.Equal("200", emptySupplierProduct.LocalSupplierCode);
        Assert.Equal("KEEP01", keepSupplierProduct.LocalSupplierCode);
    }

    [Fact]
    public async Task BatchUpdateAsync_批量翻译应同时写入商品名称和英文名称()
    {
        await SeedProductAsync("P-BATCH-TRANSLATE", "A-BATCH-TRANSLATE");

        var response = await CreateService("translator").BatchUpdateAsync(new List<BatchUpdateProductReactDto>
        {
            new()
            {
                ProductCode = "P-BATCH-TRANSLATE",
                ProductName = "250g Shaping Clay Reddish Brown",
                EnglishName = "250g Shaping Clay Reddish Brown",
            },
        });

        Assert.True(response.Success, response.Message);
        var product = await _localDb.Queryable<Product>().SingleAsync(item => item.ProductCode == "P-BATCH-TRANSLATE");
        Assert.Equal("250g Shaping Clay Reddish Brown", product.ProductName);
        Assert.Equal("250g Shaping Clay Reddish Brown", product.EnglishName);
    }

    [Fact]
    public async Task CreateWithPrices_LocalSupplierCode为空时默认写入200到商品和分店价格()
    {
        await SeedStoreAsync("S01", "分店一");
        ProductMaintenanceHqMutationRequest? capturedRequest = null;
        var expectedStatus = new ProductHqSyncOperationStatusDto
        {
            OperationId = "create-operation",
            Status = ProductHqSyncOutboxStatuses.Pending,
            Retryable = true,
        };
        var writer = new Mock<IProductMaintenanceHqProjectionWriter>(MockBehavior.Strict);
        writer.Setup(item => item.EnqueueAsync(
                _localDb,
                It.IsAny<ProductMaintenanceHqMutationRequest>(),
                It.IsAny<CancellationToken>()
            ))
            .Callback<ISqlSugarClient, ProductMaintenanceHqMutationRequest, CancellationToken>(
                (_, request, _) => capturedRequest = request
            )
            .ReturnsAsync(expectedStatus);
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.Setup(item => item.GetCurrentUsername()).Returns("controller-user");
        currentUser.Setup(item => item.GetCurrentUserGuid()).Returns("controller-user-guid");
        var controller = new ReactProductsController(
            CreateSqlSugarContext(_localDb),
            NullLogger<ReactProductsController>.Instance,
            WarehouseProductChangeHistoryTestDouble.CreateNoop(),
            currentUser.Object,
            writer.Object
        )
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim(ClaimTypes.Name, "controller-user"),
                    }, "TestAuth")),
                },
            },
        };

        var actionResult = await controller.CreateWithPrices(new CreateProductWithPricesDto
        {
            ProductName = "控制器默认供应商商品",
            LocalSupplierCode = "   ",
            PurchasePrice = 1.2m,
            RetailPrice = 2.3m,
            IsAutoPricing = false,
        });

        var okResult = Assert.IsType<OkObjectResult>(actionResult);
        var payload = Assert.IsType<CreateProductWithPricesResultDto>(
            okResult.Value!.GetType().GetProperty("data")!.GetValue(okResult.Value)
        );
        Assert.NotNull(payload.HqSync);
        Assert.Same(expectedStatus, payload.HqSync);
        Assert.NotNull(capturedRequest);
        Assert.Equal(ProductMaintenanceHqOperationKinds.ProductCreated, capturedRequest!.OperationKind);
        Assert.Null(capturedRequest.TargetStoreCodes);
        Assert.Null(capturedRequest.AuthorizedStoreCodes);
        Assert.Equal(new[] { ProductMaintenanceHqFieldMasks.All }, capturedRequest.FieldMask);
        Assert.Empty(capturedRequest.Tombstones);
        Assert.Equal("controller-user-guid", capturedRequest.RequestedByUserGuid);
        Assert.Null(capturedRequest.RequestedByDeviceId);
        var product = await _localDb.Queryable<Product>().SingleAsync(item => item.ProductName == "控制器默认供应商商品");
        var storePrice = await _localDb.Queryable<StoreRetailPrice>().SingleAsync(item => item.ProductCode == product.ProductCode);
        Assert.Equal("200", product.LocalSupplierCode);
        Assert.Equal("200", storePrice.SupplierCode);
        writer.VerifyAll();
    }

    [Fact]
    public async Task CreateWithPrices_HQ入队失败时回滚商品与全部分店价格()
    {
        await SeedStoreAsync("S01", "分店一");
        await SeedStoreAsync("S02", "分店二");
        var writer = new Mock<IProductMaintenanceHqProjectionWriter>(MockBehavior.Strict);
        writer.Setup(item => item.EnqueueAsync(
                _localDb,
                It.IsAny<ProductMaintenanceHqMutationRequest>(),
                It.IsAny<CancellationToken>()
            ))
            .ThrowsAsync(new ProductMaintenanceHqEnqueueException("HQ 同步任务创建失败，请稍后重试"));
        var controller = new ReactProductsController(
            CreateSqlSugarContext(_localDb),
            NullLogger<ReactProductsController>.Instance,
            WarehouseProductChangeHistoryTestDouble.CreateNoop(),
            Mock.Of<ICurrentUserService>(),
            writer.Object
        )
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim(ClaimTypes.Name, "controller-user"),
                    }, "TestAuth")),
                },
            },
        };

        var actionResult = await controller.CreateWithPrices(new CreateProductWithPricesDto
        {
            ProductName = "必须整体回滚的商品",
            PurchasePrice = 1.2m,
            RetailPrice = 2.3m,
            IsAutoPricing = false,
        });

        var failure = Assert.IsType<ObjectResult>(actionResult);
        Assert.Equal(500, failure.StatusCode);
        Assert.Equal(0, await _localDb.Queryable<Product>()
            .Where(item => item.ProductName == "必须整体回滚的商品")
            .CountAsync());
        Assert.Equal(0, await _localDb.Queryable<StoreRetailPrice>().CountAsync());
        writer.VerifyAll();
    }

    [Fact]
    public async Task CreateWithPrices_只为启用且未删除分店创建分店价格并默认供应商为200()
    {
        await SeedStoreAsync("S01", "启用分店");
        await SeedStoreAsync("S02", "禁用分店", isActive: false);
        await SeedStoreAsync("S03", "已删分店", isDeleted: true);

        var controller = new ReactProductsController(
            CreateSqlSugarContext(_localDb),
            NullLogger<ReactProductsController>.Instance,
            WarehouseProductChangeHistoryTestDouble.CreateNoop(),
            Mock.Of<ICurrentUserService>(),
            CreateSuccessfulProjectionWriter()
        )
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim(ClaimTypes.Name, "controller-user"),
                    }, "TestAuth")),
                },
            },
        };

        var actionResult = await controller.CreateWithPrices(new CreateProductWithPricesDto
        {
            ProductName = "控制器分店过滤商品",
            ProductImage = "https://img.example.com/create-product.jpg",
            LocalSupplierCode = null,
            PurchasePrice = 3.2m,
            RetailPrice = 5.6m,
            IsAutoPricing = false,
            ProductType = 0,
        });

        var okResult = Assert.IsType<OkObjectResult>(actionResult);
        Assert.NotNull(okResult.Value);

        var product = await _localDb.Queryable<Product>()
            .SingleAsync(item => item.ProductName == "控制器分店过滤商品");
        var storePrices = await _localDb.Queryable<StoreRetailPrice>()
            .Where(item => item.ProductCode == product.ProductCode)
            .OrderBy(item => item.StoreCode)
            .ToListAsync();

        Assert.Equal("200", product.LocalSupplierCode);
        Assert.Equal("https://img.example.com/create-product.jpg", product.ProductImage);
        Assert.Equal(0, product.ProductType);
        Assert.Single(storePrices);
        Assert.Equal("S01", storePrices[0].StoreCode);
        Assert.Equal("200", storePrices[0].SupplierCode);
        Assert.DoesNotContain(storePrices, item => item.StoreCode == "S02");
        Assert.DoesNotContain(storePrices, item => item.StoreCode == "S03");
    }

    [Fact]
    public async Task UpdatePurchase_本地更新与当前门店HQ入队共用事务并返回公开操作状态()
    {
        await SeedStoreRetailPriceAsync("price-update-purchase", "P001", "S01", false, 1m, 2m);
        var expectedStatus = new ProductHqSyncOperationStatusDto
        {
            OperationId = "operation-1",
            Status = ProductHqSyncOutboxStatuses.Pending,
            ProductCode = "P001",
            StoreCode = "S01",
            Retryable = true,
        };
        var writer = new Mock<IProductMaintenanceHqProjectionWriter>(MockBehavior.Strict);
        writer.Setup(item => item.EnqueueAsync(
                _localDb,
                It.Is<ProductMaintenanceHqMutationRequest>(request =>
                    request.OperationKind == ProductMaintenanceHqOperationKinds.StorePriceUpdated
                    && request.ProductCode == "P001"
                    && request.TargetStoreCodes!.SequenceEqual(new[] { "S01" })
                    && request.AuthorizedStoreCodes == null
                    && request.FieldMask.SequenceEqual(
                        ProductMaintenanceHqFieldMasks.StorePriceAndMultiCode
                    )
                    && request.RequestedByUserGuid == "controller-user-guid"
                ),
                It.IsAny<CancellationToken>()
            ))
            .ReturnsAsync(expectedStatus);
        var controller = new ReactProductsController(
            CreateSqlSugarContext(_localDb),
            NullLogger<ReactProductsController>.Instance,
            WarehouseProductChangeHistoryTestDouble.CreateNoop(),
            Mock.Of<ICurrentUserService>(),
            writer.Object
        )
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim(ClaimTypes.Name, "controller-user"),
                        new Claim(ClaimTypes.NameIdentifier, "controller-user-guid"),
                        new Claim(ClaimTypes.Role, "WarehouseManager"),
                    }, "TestAuth")),
                },
            },
        };

        var action = await controller.UpdatePurchase(new UpdatePurchaseRequestDto
        {
            StoreCode = "S01",
            ProductCode = "P001",
            NewPurchasePrice = 6.25m,
        });

        var ok = Assert.IsType<OkObjectResult>(action);
        var data = ok.Value!.GetType().GetProperty("data")!.GetValue(ok.Value);
        Assert.NotNull(data);
        Assert.Same(expectedStatus, data!.GetType().GetProperty("hqSync")!.GetValue(data));
        Assert.Null(ok.Value.GetType().GetProperty("hqSync"));
        Assert.Equal(
            6.25m,
            (await _localDb.Queryable<StoreRetailPrice>()
                .SingleAsync(item => item.UUID == "price-update-purchase"))
                .PurchasePrice
        );
        writer.VerifyAll();
    }

    [Fact]
    public void UpdatePurchase_要求StoreProductsEdit权限而不是角色白名单()
    {
        var method = typeof(ReactProductsController).GetMethod(
            nameof(ReactProductsController.UpdatePurchase),
            BindingFlags.Instance | BindingFlags.Public
        );

        var authorize = Assert.Single(method!.GetCustomAttributes<AuthorizeAttribute>());
        Assert.Equal(Permissions.StoreProducts.Edit, authorize.Policy);
        Assert.Null(authorize.Roles);
    }

    [Fact]
    public async Task UpdatePurchase_普通用户跨分店时拒绝且不写本地也不入队()
    {
        await SeedStoreAsync("S01", "分店一");
        await SeedStoreAsync("S02", "分店二");
        await _localDb.Insertable(new UserStore
        {
            UserStoreGUID = "user-store-controller-user-s01",
            UserGUID = "controller-user-guid",
            StoreGUID = "store-S01",
            IsDeleted = false,
        }).ExecuteCommandAsync();
        await SeedStoreRetailPriceAsync("price-cross-store", "P001", "S02", false, 1m, 2m);

        var writer = new Mock<IProductMaintenanceHqProjectionWriter>(MockBehavior.Strict);
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.Setup(item => item.GetCurrentUserGuid()).Returns("controller-user-guid");
        var controller = new ReactProductsController(
            CreateSqlSugarContext(_localDb),
            NullLogger<ReactProductsController>.Instance,
            WarehouseProductChangeHistoryTestDouble.CreateNoop(),
            currentUser.Object,
            writer.Object
        )
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim(ClaimTypes.Name, "controller-user"),
                        new Claim(ClaimTypes.NameIdentifier, "controller-user-guid"),
                    }, "TestAuth")),
                },
            },
        };

        var action = await controller.UpdatePurchase(new UpdatePurchaseRequestDto
        {
            StoreCode = "S02",
            ProductCode = "P001",
            NewPurchasePrice = 9.25m,
        });

        Assert.IsType<ForbidResult>(action);
        Assert.Equal(
            1m,
            (await _localDb.Queryable<StoreRetailPrice>()
                .SingleAsync(item => item.UUID == "price-cross-store"))
                .PurchasePrice
        );
        writer.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UpdatePurchase_普通用户当前分店成功且冻结授权范围()
    {
        await SeedStoreAsync("S01", "分店一");
        await _localDb.Insertable(new UserStore
        {
            UserStoreGUID = "user-store-controller-user-current",
            UserGUID = "controller-user-guid",
            StoreGUID = "store-S01",
            IsDeleted = false,
        }).ExecuteCommandAsync();
        await SeedStoreRetailPriceAsync("price-current-store", "P001", "S01", false, 1m, 2m);

        var expectedStatus = new ProductHqSyncOperationStatusDto
        {
            OperationId = "operation-current-store",
            Status = ProductHqSyncOutboxStatuses.Pending,
            ProductCode = "P001",
            StoreCode = "S01",
            Retryable = true,
        };
        var writer = new Mock<IProductMaintenanceHqProjectionWriter>(MockBehavior.Strict);
        writer.Setup(item => item.EnqueueAsync(
                _localDb,
                It.Is<ProductMaintenanceHqMutationRequest>(request =>
                    request.ProductCode == "P001"
                    && request.TargetStoreCodes!.SequenceEqual(new[] { "S01" })
                    && request.AuthorizedStoreCodes!.SequenceEqual(new[] { "S01" })
                ),
                It.IsAny<CancellationToken>()
            ))
            .ReturnsAsync(expectedStatus);
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.Setup(item => item.GetCurrentUserGuid()).Returns("controller-user-guid");
        var controller = new ReactProductsController(
            CreateSqlSugarContext(_localDb),
            NullLogger<ReactProductsController>.Instance,
            WarehouseProductChangeHistoryTestDouble.CreateNoop(),
            currentUser.Object,
            writer.Object
        )
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim(ClaimTypes.Name, "controller-user"),
                        new Claim(ClaimTypes.NameIdentifier, "controller-user-guid"),
                    }, "TestAuth")),
                },
            },
        };

        var action = await controller.UpdatePurchase(new UpdatePurchaseRequestDto
        {
            StoreCode = "S01",
            ProductCode = "P001",
            NewPurchasePrice = 6.75m,
        });

        Assert.IsType<OkObjectResult>(action);
        Assert.Equal(
            6.75m,
            (await _localDb.Queryable<StoreRetailPrice>()
                .SingleAsync(item => item.UUID == "price-current-store"))
                .PurchasePrice
        );
        writer.VerifyAll();
    }

    [Fact]
    public async Task UpdatePurchase_只写进货价与审计列避免覆盖并发门店策略()
    {
        await SeedStoreRetailPriceAsync(
            "price-narrow-update",
            "P001",
            "S01",
            false,
            1m,
            2m,
            discountRate: 0.82m,
            isAutoPricing: true,
            isSpecialProduct: true,
            isActive: false
        );
        string? updateSql = null;
        _localDb.Aop.OnLogExecuting = (sql, _) =>
        {
            if (
                sql.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
                && sql.Contains("StoreRetailPrice", StringComparison.OrdinalIgnoreCase)
            )
            {
                updateSql = sql;
            }
        };
        var controller = new ReactProductsController(
            CreateSqlSugarContext(_localDb),
            NullLogger<ReactProductsController>.Instance,
            WarehouseProductChangeHistoryTestDouble.CreateNoop(),
            Mock.Of<ICurrentUserService>(),
            CreateSuccessfulProjectionWriter()
        )
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim(ClaimTypes.Name, "warehouse-manager"),
                        new Claim(ClaimTypes.Role, "WarehouseManager"),
                    }, "TestAuth")),
                },
            },
        };

        var action = await controller.UpdatePurchase(new UpdatePurchaseRequestDto
        {
            StoreCode = "S01",
            ProductCode = "P001",
            NewPurchasePrice = 6.5m,
        });

        Assert.IsType<OkObjectResult>(action);
        Assert.NotNull(updateSql);
        var setClause = updateSql![..updateSql.IndexOf("WHERE", StringComparison.OrdinalIgnoreCase)];
        Assert.Contains("PurchasePrice", setClause, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UpdatedAt", setClause, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UpdatedBy", setClause, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("StoreRetailPriceValue", setClause, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DiscountRate", setClause, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("IsAutoPricing", setClause, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("IsSpecialProduct", setClause, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("IsActive", setClause, StringComparison.OrdinalIgnoreCase);
    }

    private static IProductMaintenanceHqProjectionWriter CreateSuccessfulProjectionWriter()
    {
        var writer = new Mock<IProductMaintenanceHqProjectionWriter>();
        writer.Setup(item => item.EnqueueAsync(
                It.IsAny<ISqlSugarClient>(),
                It.IsAny<ProductMaintenanceHqMutationRequest>(),
                It.IsAny<CancellationToken>()
            ))
            .ReturnsAsync(new ProductHqSyncOperationStatusDto
            {
                OperationId = Guid.NewGuid().ToString("N"),
                Status = ProductHqSyncOutboxStatuses.Pending,
                Retryable = true,
            });
        return writer.Object;
    }

    public void Dispose()
    {
        _localDb.Dispose();
        _hqDb.Dispose();
        _localConnection.Dispose();
        _hqConnection.Dispose();

        SqliteTempFileCleanup.DeleteIfExists(_localDbPath);
        SqliteTempFileCleanup.DeleteIfExists(_hqDbPath);
    }

    private ProductReactService CreateService(
        string? identityName = null,
        IWarehouseProductChangeHistoryService? historyService = null,
        ICurrentUserService? currentUserService = null
    )
    {
        return new ProductReactService(
            CreateSqlSugarContext(_localDb),
            CreateHqSqlSugarContext(_hqDb, CreateHqConfiguration(_hqConnection.ConnectionString)),
            _mapper,
            NullLogger<ProductReactService>.Instance,
            CreateHttpContextAccessor(identityName),
            historyService ?? new ProductAuditNoopHistoryService(),
            currentUserService ?? new ProductAuditSystemCurrentUserService()
        );
    }

    private async Task SeedProductAsync(
        string productCode,
        string itemNumber,
        string? localSupplierCode = null,
        string? productName = null,
        string? barcode = null,
        decimal purchasePrice = 1,
        decimal retailPrice = 2,
        bool isActive = true,
        bool isAutoPricing = false,
        int? productType = null,
        string? categoryGuid = null,
        string? warehouseCategoryGuid = null,
        DateTime? createdAt = null,
        DateTime? updatedAt = null)
    {
        var now = DateTime.UtcNow;
        await _localDb.Insertable(new Product
        {
            UUID = $"product-{productCode}",
            ProductCode = productCode,
            ProductCategoryGUID = categoryGuid,
            WarehouseCategoryGUID = warehouseCategoryGuid,
            LocalSupplierCode = localSupplierCode,
            ItemNumber = itemNumber,
            Barcode = barcode ?? $"barcode-{productCode}",
            ProductName = productName ?? $"商品{productCode}",
            ProductType = productType,
            PurchasePrice = purchasePrice,
            RetailPrice = retailPrice,
            IsAutoPricing = isAutoPricing,
            IsActive = isActive,
            CreatedAt = createdAt ?? now,
            UpdatedAt = updatedAt ?? now,
            IsDeleted = false,
        }).ExecuteCommandAsync();
    }

    private async Task SeedDomesticProductAsync(
        string productCode,
        string? supplierCode,
        bool isDeleted = false)
    {
        await _localDb.Insertable(new DomesticProduct
        {
            ProductCode = productCode,
            SupplierCode = supplierCode,
            ProductName = $"国内商品{productCode}",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            IsDeleted = isDeleted,
        }).ExecuteCommandAsync();
    }

    private async Task SeedChinaSupplierAsync(
        string supplierCode,
        string supplierName,
        bool isDeleted = false,
        string? guid = null)
    {
        await _localDb.Insertable(new ChinaSupplier
        {
            Guid = guid ?? $"supplier-guid-{supplierCode}",
            SupplierCode = supplierCode,
            SupplierName = supplierName,
            Status = isDeleted ? 0 : 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            IsDeleted = isDeleted,
        }).ExecuteCommandAsync();
    }

    private async Task SeedStoreAsync(
        string storeCode,
        string storeName,
        bool isActive = true,
        bool isDeleted = false)
    {
        await _localDb.Insertable(new Store
        {
            StoreGUID = $"store-{storeCode}",
            StoreCode = storeCode,
            StoreName = storeName,
            IsActive = isActive,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            IsDeleted = isDeleted,
        }).ExecuteCommandAsync();
    }

    private async Task SeedStoreRetailPriceAsync(
        string uuid,
        string productCode,
        string storeCode,
        bool isDeleted,
        decimal purchasePrice = 1,
        decimal retailPrice = 2,
        decimal discountRate = 0.9m,
        bool isAutoPricing = false,
        bool isSpecialProduct = false,
        bool isActive = true)
    {
        await _localDb.Insertable(new StoreRetailPrice
        {
            UUID = uuid,
            StoreCode = storeCode,
            ProductCode = productCode,
            StoreProductCode = $"{storeCode}-{productCode}",
            PurchasePrice = purchasePrice,
            StoreRetailPriceValue = retailPrice,
            DiscountRate = discountRate,
            IsActive = isActive,
            IsAutoPricing = isAutoPricing,
            IsSpecialProduct = isSpecialProduct,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            UpdatedBy = "tester",
            IsDeleted = isDeleted,
        }).ExecuteCommandAsync();
    }

    private async Task<StoreRetailPrice?> GetStoreRetailPriceAsync(string productCode, string storeCode)
    {
        return await _localDb.Queryable<StoreRetailPrice>()
            .Where(item => item.ProductCode == productCode && item.StoreCode == storeCode)
            .FirstAsync();
    }

    private static ConnectionConfig CreateConnectionConfig(string connectionString) =>
        new()
        {
            ConnectionString = connectionString,
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = false,
            InitKeyType = InitKeyType.Attribute,
        };

    private static IMapper CreateMapper()
    {
        var configuration = new MapperConfiguration(
            cfg => cfg.AddProfile<ReactProductMappingProfile>(),
            NullLoggerFactory.Instance
        );
        return configuration.CreateMapper();
    }

    private static IConfiguration CreateHqConfiguration(string connectionString)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:StoreHzgHQConnection"] = connectionString,
            })
            .Build();
    }

    private static SqlSugarContext CreateSqlSugarContext(ISqlSugarClient db)
    {
        var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(SqlSugarContext));
        var dbField = typeof(SqlSugarContext).GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic);
        dbField!.SetValue(context, db);
        return context;
    }

    private static HqSqlSugarContext CreateHqSqlSugarContext(ISqlSugarClient db, IConfiguration configuration)
    {
        var context = (HqSqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(HqSqlSugarContext));
        var dbField = typeof(HqSqlSugarContext).GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic);
        dbField!.SetValue(context, db);
        var configurationField = typeof(HqSqlSugarContext).GetField(
            "<Configuration>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        configurationField!.SetValue(context, configuration);
        return context;
    }

    private static HttpContextAccessor CreateHttpContextAccessor(string? identityName)
    {
        if (string.IsNullOrWhiteSpace(identityName))
        {
            return new HttpContextAccessor();
        }

        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.Name, identityName),
        }, "TestAuth");

        return new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(identity),
            },
        };
    }

    private static string ResolveProductReactServicePath([CallerFilePath] string testFilePath = "")
    {
        var testDirectory = Path.GetDirectoryName(testFilePath)
            ?? throw new InvalidOperationException("无法解析测试文件目录");
        return Path.GetFullPath(
            Path.Combine(testDirectory, "..", "BlazorApp.Api", "Services", "React", "ProductReactService.cs")
        );
    }
}
