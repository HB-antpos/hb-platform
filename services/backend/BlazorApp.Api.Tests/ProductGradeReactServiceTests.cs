using System.Reflection;
using System.Runtime.CompilerServices;
using BlazorApp.Api.Controllers.React;
using AutoMapper;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Mappings.Profiles.React;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class ProductGradeReactServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnection _sqliteConnection;
    private readonly SqlSugarClient _db;

    public ProductGradeReactServiceTests()
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
        });

        _db.CodeFirst.InitTables(
            typeof(ProductGrade),
            typeof(DomesticProduct),
            typeof(ChinaSupplier),
            typeof(WarehouseProduct),
            typeof(Product),
            typeof(StoreRetailPrice),
            typeof(ProductSetCode),
            typeof(StoreMultiCodeProduct),
            typeof(WarehouseCategory)
        );
    }

    [Fact]
    public async Task GetProductGradesByProductCodesAsync_EmptyCodes_ReturnsEmptyList()
    {
        var service = CreateService();

        var result = await service.GetProductGradesByProductCodesAsync(new List<string>());

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Empty(result.Data!);
    }

    [Fact]
    public async Task BatchUpdateGradePriceAsync_本地主成本更新同步Type2总部和门店成本()
    {
        const string productCode = "P-GRADE-TYPE2";
        await _db.Insertable(new Product
        {
            UUID = "P-GRADE-TYPE2-UUID",
            ProductCode = productCode,
            PurchasePrice = 10m,
            IsDeleted = false,
        }).ExecuteCommandAsync();
        await _db.Insertable(new WarehouseProduct
        {
            ProductCode = productCode,
            ImportPrice = 10m,
            IsDeleted = false,
        }).ExecuteCommandAsync();
        await _db.Insertable(new StoreRetailPrice
        {
            UUID = "SRP-GRADE-TYPE2",
            StoreCode = "S01",
            ProductCode = productCode,
            PurchasePrice = 10m,
            IsActive = true,
            IsDeleted = false,
        }).ExecuteCommandAsync();
        await _db.Insertable(new ProductSetCode
        {
            SetCodeId = "SET-GRADE-TYPE2",
            ProductCode = productCode,
            SetProductCode = "CHILD-GRADE-TYPE2",
            SetItemNumber = "ITEM-GRADE-TYPE2",
            SetPurchasePrice = 99m,
            SetType = 2,
            IsActive = true,
            IsDeleted = false,
        }).ExecuteCommandAsync();
        await _db.Insertable(new StoreMultiCodeProduct
        {
            UUID = "SMC-GRADE-TYPE2",
            StoreCode = "S01",
            ProductCode = productCode,
            MultiCodeProductCode = "CHILD-GRADE-TYPE2",
            PurchasePrice = 99m,
            IsActive = true,
            IsDeleted = false,
        }).ExecuteCommandAsync();

        var result = await CreateService().BatchUpdateGradePriceAsync(new BatchUpdateGradePriceDto
        {
            TargetDatabase = "Local",
            ProductCodes = new List<string> { productCode },
            ImportPrice = 12m,
        });

        Assert.True(result.Success, result.Message);
        Assert.Equal(12m, (await _db.Queryable<ProductSetCode>().SingleAsync()).SetPurchasePrice);
        Assert.Equal(12m, (await _db.Queryable<StoreMultiCodeProduct>().SingleAsync()).PurchasePrice);
    }

    [Fact]
    public async Task GetProductGradesByProductCodesAsync_ReturnsFullExportFields()
    {
        const string productCode = "product-1";
        const string supplierCode = "SUP001";

        await _db.Insertable(new ChinaSupplier
        {
            Guid = "supplier-guid-1",
            SupplierCode = supplierCode,
            SupplierName = "义乌供应商",
        }).ExecuteCommandAsync();

        await _db.Insertable(new DomesticProduct
        {
            ProductCode = productCode,
            SupplierCode = supplierCode,
            HBProductNo = "HB100-001",
            Barcode = "930000000001",
            ProductName = "测试商品",
            ProductImage = "https://img.example.com/HB100-001.jpg",
            DomesticPrice = 12.34m,
        }).ExecuteCommandAsync();

        await _db.Insertable(new WarehouseProduct
        {
            ProductCode = productCode,
            ImportPrice = 2.45m,
            OEMPrice = 4.56m,
            IsActive = true,
            MinOrderQuantity = 0,
        }).ExecuteCommandAsync();

        await _db.Insertable(new Product
        {
            UUID = $"{productCode}-uuid",
            ProductCode = productCode,
            ItemNumber = "HB100-001",
            Barcode = "930000000001",
            ProductName = "测试商品",
            RetailPrice = 6.78m,
        }).ExecuteCommandAsync();

        await _db.Insertable(new ProductGrade
        {
            Id = "grade-1",
            ProductCode = productCode,
            Grade = "A",
            CreatedBy = "tester",
        }).ExecuteCommandAsync();

        var service = CreateService();

        var result = await service.GetProductGradesByProductCodesAsync(
            new List<string> { productCode, productCode, " " }
        );

        Assert.True(result.Success);
        var item = Assert.Single(result.Data!);
        Assert.Equal(productCode, item.ProductCode);
        Assert.Equal("A", item.Grade);
        Assert.Equal(supplierCode, item.SupplierCode);
        Assert.Equal("义乌供应商", item.SupplierName);
        Assert.Equal("HB100-001", item.HbProductNo);
        Assert.Equal("930000000001", item.Barcode);
        Assert.Equal("测试商品", item.ProductName);
        Assert.Equal("https://img.example.com/HB100-001.jpg", item.ProductImage);
        Assert.Equal(12.34m, item.DomesticPrice);
        Assert.Equal(2.45m, item.ImportPrice);
        Assert.Equal(4.56m, item.OemPrice);
        Assert.Equal(6.78m, item.RetailPrice);
        Assert.True(item.WarehouseIsActive);
        Assert.Equal(0, item.MinOrderQuantity);
        Assert.Equal("tester", item.CreatedBy);
    }

    [Fact]
    public async Task GetProductGradesByProductCodesAsync_ReturnsCategoryFields()
    {
        const string parentGuid = "category-parent";
        const string childGuid = "category-child";
        await SeedWarehouseCategoryAsync(parentGuid, "Stationery", "文具");
        await SeedWarehouseCategoryAsync(childGuid, "Pens", "笔", parentGuid);
        await SeedProductGradeAsync(
            productCode: "product-category-export",
            supplierCode: "SUP001",
            supplierName: "一号供应商",
            hbProductNo: "HB-CAT-EXPORT",
            productName: "分类导出商品",
            grade: "A",
            domesticPrice: 10m,
            importPrice: 2m,
            oemPrice: 4m,
            createdAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            categoryGuid: childGuid
        );

        var result = await CreateService().GetProductGradesByProductCodesAsync(
            new List<string> { "product-category-export" }
        );

        Assert.True(result.Success);
        var item = Assert.Single(result.Data!);
        Assert.Equal(childGuid, item.CategoryGuid);
        Assert.Equal("Pens", item.CategoryName);
        Assert.Equal("笔", item.CategoryChineseName);
    }

    [Fact]
    public async Task GetProductGradesAsync_ReturnsWarehouseFieldsWithOriginalMinOrderQuantity()
    {
        await SeedProductGradeAsync(
            productCode: "product-inactive",
            supplierCode: "SUP001",
            supplierName: "一号供应商",
            hbProductNo: "HB-INACTIVE",
            productName: "下架商品",
            grade: "C",
            domesticPrice: 10m,
            importPrice: 2m,
            oemPrice: 4m,
            createdAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            warehouseIsActive: false,
            minOrderQuantity: 0
        );

        var service = CreateService();

        var result = await service.GetProductGradesAsync(new ProductGradeListQueryDto
        {
            Page = 1,
            PageSize = 10,
        });

        Assert.True(result.Success);
        var item = Assert.Single(result.Data!.Items!);
        Assert.False(item.WarehouseIsActive);
        Assert.Equal(0, item.MinOrderQuantity);
    }

    [Fact]
    public async Task GetProductGradesAsync_ReturnsCategoryFields()
    {
        await SeedWarehouseCategoryAsync("category-office", "Office", "办公");
        await SeedProductGradeAsync(
            productCode: "product-category",
            supplierCode: "SUP001",
            supplierName: "一号供应商",
            hbProductNo: "HB-CAT-001",
            productName: "分类商品",
            grade: "A",
            domesticPrice: 10m,
            importPrice: 2m,
            oemPrice: 4m,
            createdAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            categoryGuid: "category-office"
        );

        var result = await CreateService().GetProductGradesAsync(new ProductGradeListQueryDto
        {
            Page = 1,
            PageSize = 10,
        });

        Assert.True(result.Success);
        var item = Assert.Single(result.Data!.Items!);
        Assert.Equal("category-office", item.CategoryGuid);
        Assert.Equal("Office", item.CategoryName);
        Assert.Equal("办公", item.CategoryChineseName);
    }

    [Fact]
    public async Task GetProductGradesAsync_CategoryFilterIncludesChildrenBeforePaging()
    {
        await SeedWarehouseCategoryAsync("category-root", "Root", "根分类");
        await SeedWarehouseCategoryAsync("category-child", "Child", "子分类", "category-root");
        await SeedWarehouseCategoryAsync("category-other", "Other", "其他");

        await SeedProductGradeAsync(
            productCode: "product-root-old",
            supplierCode: "SUP001",
            supplierName: "一号供应商",
            hbProductNo: "HB-ROOT-OLD",
            productName: "父分类旧商品",
            grade: "A",
            domesticPrice: 10m,
            importPrice: 2m,
            oemPrice: 4m,
            createdAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            categoryGuid: "category-root"
        );
        await SeedProductGradeAsync(
            productCode: "product-child-new",
            supplierCode: "SUP001",
            supplierName: "一号供应商",
            hbProductNo: "HB-CHILD-NEW",
            productName: "子分类新商品",
            grade: "A",
            domesticPrice: 11m,
            importPrice: 3m,
            oemPrice: 5m,
            createdAt: new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc),
            categoryGuid: "category-child"
        );
        await SeedProductGradeAsync(
            productCode: "product-other-latest",
            supplierCode: "SUP001",
            supplierName: "一号供应商",
            hbProductNo: "HB-OTHER",
            productName: "其他分类商品",
            grade: "A",
            domesticPrice: 12m,
            importPrice: 4m,
            oemPrice: 6m,
            createdAt: new DateTime(2026, 1, 4, 0, 0, 0, DateTimeKind.Utc),
            categoryGuid: "category-other"
        );

        var result = await CreateService().GetProductGradesAsync(new ProductGradeListQueryDto
        {
            Page = 1,
            PageSize = 1,
            CategoryGuid = "category-root",
        });

        Assert.True(result.Success);
        Assert.Equal(2, result.Data!.Total);
        var item = Assert.Single(result.Data.Items!);
        Assert.Equal("product-child-new", item.ProductCode);
    }

    [Fact]
    public async Task GetProductGradesAsync_UncategorizedOnlyFiltersBeforePaging()
    {
        await SeedWarehouseCategoryAsync("category-office", "Office", "办公");
        await SeedProductGradeAsync(
            productCode: "product-categorized",
            supplierCode: "SUP001",
            supplierName: "一号供应商",
            hbProductNo: "HB-CATEGORIZED",
            productName: "有分类商品",
            grade: "A",
            domesticPrice: 10m,
            importPrice: 2m,
            oemPrice: 4m,
            createdAt: new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc),
            categoryGuid: "category-office"
        );
        await SeedProductGradeAsync(
            productCode: "product-uncategorized",
            supplierCode: "SUP001",
            supplierName: "一号供应商",
            hbProductNo: "HB-UNCATEGORIZED",
            productName: "未分类商品",
            grade: "A",
            domesticPrice: 12m,
            importPrice: 4m,
            oemPrice: 6m,
            createdAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        );

        var result = await CreateService().GetProductGradesAsync(new ProductGradeListQueryDto
        {
            Page = 1,
            PageSize = 10,
            UncategorizedOnly = true,
        });

        Assert.True(result.Success);
        var item = Assert.Single(result.Data!.Items!);
        Assert.Equal("product-uncategorized", item.ProductCode);
        Assert.Null(item.CategoryGuid);
    }

    [Fact]
    public async Task GetProductGradesAsync_FiltersByColumnFieldsBeforePaging_ReturnsMatchingTotal()
    {
        await SeedProductGradeAsync(
            productCode: "product-a",
            supplierCode: "SUP001",
            supplierName: "一号供应商",
            hbProductNo: "HB-A-001",
            productName: "红色笔",
            grade: "A",
            domesticPrice: 10m,
            importPrice: 2m,
            oemPrice: 4m,
            createdAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        );
        await SeedProductGradeAsync(
            productCode: "product-b",
            supplierCode: "SUP002",
            supplierName: "二号供应商",
            hbProductNo: "HB-B-002",
            productName: "蓝色笔",
            grade: "B",
            domesticPrice: 20m,
            importPrice: 5m,
            oemPrice: 8m,
            createdAt: new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)
        );

        var service = CreateService();

        var result = await service.GetProductGradesAsync(new ProductGradeListQueryDto
        {
            Page = 1,
            PageSize = 10,
            Grade = "B",
            SupplierCode = "SUP002",
            HbProductNo = "B-002",
            DomesticPriceMin = 15m,
            DomesticPriceMax = 25m,
        });

        Assert.True(result.Success);
        var data = result.Data;
        Assert.NotNull(data);
        Assert.Equal(1, data!.Total);
        Assert.NotNull(data.Items);
        var item = Assert.Single(data.Items!);
        Assert.Equal("product-b", item.ProductCode);
        Assert.Equal("HB-B-002", item.HbProductNo);
    }

    [Fact]
    public async Task GetProductGradesAsync_FiltersByImportAndOemPriceRangeBeforePaging()
    {
        await SeedProductGradeAsync(
            productCode: "product-low",
            supplierCode: "SUP001",
            supplierName: "一号供应商",
            hbProductNo: "HB-L-001",
            productName: "低价商品",
            grade: "A",
            domesticPrice: 5m,
            importPrice: 1m,
            oemPrice: 2m,
            createdAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        );
        await SeedProductGradeAsync(
            productCode: "product-mid",
            supplierCode: "SUP001",
            supplierName: "一号供应商",
            hbProductNo: "HB-M-001",
            productName: "中价商品",
            grade: "A",
            domesticPrice: 9m,
            importPrice: 3m,
            oemPrice: 6m,
            createdAt: new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)
        );
        await SeedProductGradeAsync(
            productCode: "product-high",
            supplierCode: "SUP001",
            supplierName: "一号供应商",
            hbProductNo: "HB-H-001",
            productName: "高价商品",
            grade: "A",
            domesticPrice: 15m,
            importPrice: 8m,
            oemPrice: 12m,
            createdAt: new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc)
        );

        var service = CreateService();

        var result = await service.GetProductGradesAsync(new ProductGradeListQueryDto
        {
            Page = 1,
            PageSize = 10,
            ImportPriceMin = 2m,
            ImportPriceMax = 5m,
            OemPriceMin = 5m,
            OemPriceMax = 7m,
        });

        Assert.True(result.Success);
        var data = result.Data;
        Assert.NotNull(data);
        Assert.Equal(1, data!.Total);
        Assert.NotNull(data.Items);
        Assert.Equal("product-mid", Assert.Single(data.Items!).ProductCode);
    }

    [Fact]
    public async Task GetProductGradesAsync_SortsByImportPriceBeforePaging()
    {
        await SeedProductGradeAsync(
            productCode: "product-expensive",
            supplierCode: "SUP001",
            supplierName: "一号供应商",
            hbProductNo: "HB-002",
            productName: "高进口价",
            grade: "A",
            domesticPrice: 20m,
            importPrice: 9m,
            oemPrice: 12m,
            createdAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        );
        await SeedProductGradeAsync(
            productCode: "product-cheap",
            supplierCode: "SUP002",
            supplierName: "二号供应商",
            hbProductNo: "HB-001",
            productName: "低进口价",
            grade: "A",
            domesticPrice: 10m,
            importPrice: 1m,
            oemPrice: 3m,
            createdAt: new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)
        );

        var service = CreateService();

        var result = await service.GetProductGradesAsync(new ProductGradeListQueryDto
        {
            Page = 1,
            PageSize = 10,
            SortField = "importPrice",
            SortDirection = "asc",
        });

        Assert.True(result.Success);
        var data = result.Data;
        Assert.NotNull(data);
        Assert.NotNull(data!.Items);
        Assert.Equal(new[] { "product-cheap", "product-expensive" }, data.Items!.Select(item => item.ProductCode));
    }

    [Fact]
    public async Task GetProductGradesAsync_AppliesFilterAndSortBeforeSkipTake()
    {
        await SeedProductGradeAsync(
            productCode: "product-c",
            supplierCode: "SUP001",
            supplierName: "一号供应商",
            hbProductNo: "HB-C",
            productName: "C 商品",
            grade: "A",
            domesticPrice: 30m,
            importPrice: 3m,
            oemPrice: 6m,
            createdAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        );
        await SeedProductGradeAsync(
            productCode: "product-a",
            supplierCode: "SUP001",
            supplierName: "一号供应商",
            hbProductNo: "HB-A",
            productName: "A 商品",
            grade: "A",
            domesticPrice: 10m,
            importPrice: 1m,
            oemPrice: 2m,
            createdAt: new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)
        );
        await SeedProductGradeAsync(
            productCode: "product-b",
            supplierCode: "SUP001",
            supplierName: "一号供应商",
            hbProductNo: "HB-B",
            productName: "B 商品",
            grade: "A",
            domesticPrice: 20m,
            importPrice: 2m,
            oemPrice: 4m,
            createdAt: new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc)
        );
        await SeedProductGradeAsync(
            productCode: "product-d",
            supplierCode: "SUP001",
            supplierName: "一号供应商",
            hbProductNo: "HB-D",
            productName: "D 商品",
            grade: "B",
            domesticPrice: 40m,
            importPrice: 4m,
            oemPrice: 8m,
            createdAt: new DateTime(2026, 1, 4, 0, 0, 0, DateTimeKind.Utc)
        );

        var service = CreateService();

        var result = await service.GetProductGradesAsync(new ProductGradeListQueryDto
        {
            Page = 2,
            PageSize = 1,
            Grade = "A",
            SortField = "domesticPrice",
            SortDirection = "asc",
        });

        Assert.True(result.Success);
        var data = result.Data;
        Assert.NotNull(data);
        Assert.Equal(3, data!.Total);
        Assert.NotNull(data.Items);
        var item = Assert.Single(data.Items!);
        Assert.Equal("product-b", item.ProductCode);
        Assert.Equal(2, data.Page);
        Assert.Equal(1, data.PageSize);
    }

    [Fact]
    public async Task GetProductGradesAsync_FiltersByWarehouseIsActiveBeforePaging()
    {
        await SeedProductGradeAsync(
            productCode: "product-active",
            supplierCode: "SUP001",
            supplierName: "一号供应商",
            hbProductNo: "HB-ACTIVE",
            productName: "上架商品",
            grade: "A",
            domesticPrice: 10m,
            importPrice: 1m,
            oemPrice: 2m,
            createdAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            warehouseIsActive: true
        );
        await SeedProductGradeAsync(
            productCode: "product-inactive-1",
            supplierCode: "SUP001",
            supplierName: "一号供应商",
            hbProductNo: "HB-INACTIVE-1",
            productName: "下架商品1",
            grade: "A",
            domesticPrice: 20m,
            importPrice: 2m,
            oemPrice: 4m,
            createdAt: new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
            warehouseIsActive: false
        );
        await SeedProductGradeAsync(
            productCode: "product-inactive-2",
            supplierCode: "SUP001",
            supplierName: "一号供应商",
            hbProductNo: "HB-INACTIVE-2",
            productName: "下架商品2",
            grade: "A",
            domesticPrice: 30m,
            importPrice: 3m,
            oemPrice: 6m,
            createdAt: new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc),
            warehouseIsActive: false
        );

        var service = CreateService();

        var result = await service.GetProductGradesAsync(new ProductGradeListQueryDto
        {
            Page = 2,
            PageSize = 1,
            WarehouseIsActive = false,
            SortField = "createdAt",
            SortDirection = "asc",
        });

        Assert.True(result.Success);
        var data = result.Data;
        Assert.NotNull(data);
        Assert.Equal(2, data!.Total);
        var item = Assert.Single(data.Items!);
        Assert.Equal("product-inactive-2", item.ProductCode);
        Assert.False(item.WarehouseIsActive);
    }

    [Fact]
    public async Task GetProductGradesAsync_SortsByWarehouseIsActiveBeforePaging()
    {
        await SeedProductGradeAsync(
            productCode: "product-active",
            supplierCode: "SUP001",
            supplierName: "一号供应商",
            hbProductNo: "HB-ACTIVE",
            productName: "上架商品",
            grade: "A",
            domesticPrice: 10m,
            importPrice: 1m,
            oemPrice: 2m,
            createdAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            warehouseIsActive: true
        );
        await SeedProductGradeAsync(
            productCode: "product-inactive",
            supplierCode: "SUP001",
            supplierName: "一号供应商",
            hbProductNo: "HB-INACTIVE",
            productName: "下架商品",
            grade: "A",
            domesticPrice: 20m,
            importPrice: 2m,
            oemPrice: 4m,
            createdAt: new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
            warehouseIsActive: false
        );

        var service = CreateService();

        var result = await service.GetProductGradesAsync(new ProductGradeListQueryDto
        {
            Page = 1,
            PageSize = 1,
            SortField = "warehouseIsActive",
            SortDirection = "asc",
        });

        Assert.True(result.Success);
        var data = result.Data;
        Assert.NotNull(data);
        Assert.Equal(2, data!.Total);
        var item = Assert.Single(data.Items!);
        Assert.Equal("product-inactive", item.ProductCode);
        Assert.False(item.WarehouseIsActive);
    }

    [Fact]
    public async Task GetProductGradesAsync_ClampsInvalidPaging()
    {
        await SeedProductGradeAsync(
            productCode: "product-a",
            supplierCode: "SUP001",
            supplierName: "一号供应商",
            hbProductNo: "HB-A",
            productName: "A 商品",
            grade: "A",
            domesticPrice: 10m,
            importPrice: 1m,
            oemPrice: 2m,
            createdAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        );

        var service = CreateService();

        var result = await service.GetProductGradesAsync(new ProductGradeListQueryDto
        {
            Page = 0,
            PageSize = 5001,
        });

        Assert.True(result.Success);
        Assert.Equal(1, result.Data!.Page);
        Assert.Equal(1000, result.Data.PageSize);
        Assert.Equal("product-a", Assert.Single(result.Data.Items!).ProductCode);
    }

    [Fact]
    public async Task GetProductGradesAsync_FiltersSortsAndClampsBeforePaging()
    {
        await SeedProductGradeAsync(
            productCode: "product-active-expensive",
            supplierCode: "SUP001",
            supplierName: "一号供应商",
            hbProductNo: "HB-ACTIVE-EXPENSIVE",
            productName: "上架高价",
            grade: "A",
            domesticPrice: 30m,
            importPrice: 3m,
            oemPrice: 6m,
            createdAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            warehouseIsActive: true
        );
        await SeedProductGradeAsync(
            productCode: "product-active-cheap",
            supplierCode: "SUP001",
            supplierName: "一号供应商",
            hbProductNo: "HB-ACTIVE-CHEAP",
            productName: "上架低价",
            grade: "A",
            domesticPrice: 10m,
            importPrice: 1m,
            oemPrice: 2m,
            createdAt: new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
            warehouseIsActive: true
        );
        await SeedProductGradeAsync(
            productCode: "product-inactive-cheap",
            supplierCode: "SUP001",
            supplierName: "一号供应商",
            hbProductNo: "HB-INACTIVE-CHEAP",
            productName: "下架低价",
            grade: "A",
            domesticPrice: 1m,
            importPrice: 1m,
            oemPrice: 2m,
            createdAt: new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc),
            warehouseIsActive: false
        );

        var service = CreateService();

        var result = await service.GetProductGradesAsync(new ProductGradeListQueryDto
        {
            Page = -10,
            PageSize = 0,
            WarehouseIsActive = true,
            SortField = "domesticPrice",
            SortDirection = "asc",
        });

        Assert.True(result.Success);
        Assert.Equal(2, result.Data!.Total);
        Assert.Equal(1, result.Data.Page);
        Assert.Equal(1, result.Data.PageSize);
        var item = Assert.Single(result.Data.Items!);
        Assert.Equal("product-active-cheap", item.ProductCode);
        Assert.True(item.WarehouseIsActive);
    }

    [Fact]
    public async Task GetGradeOptionsAsync_超过一千个同等级商品时仍返回全部等级()
    {
        // 生产上 A 级超过 1000 个商品；旧做法先按等级排序取前 1000 行再去重，结果只剩 A。
        var rows = Enumerable.Range(0, 1001)
            .Select(index => new ProductGrade { ProductCode = $"P-A-{index:D4}", Grade = "A" })
            .ToList();
        rows.Add(new ProductGrade { ProductCode = "P-B", Grade = "B" });
        rows.Add(new ProductGrade { ProductCode = "P-B-LOWER", Grade = "b" });
        rows.Add(new ProductGrade { ProductCode = "P-C", Grade = "C" });
        rows.Add(new ProductGrade { ProductCode = "P-E", Grade = " e " });
        rows.Add(new ProductGrade { ProductCode = "P-F-DELETED", Grade = "F", IsDeleted = true });
        await _db.Insertable(rows).ExecuteCommandAsync();

        var result = await CreateService().GetGradeOptionsAsync();

        // 固定四档 A–D 始终在前（与 Web 订货前台 PRODUCT_GRADE_CONFIG 一致，D 级暂无商品也要能选），
        // 库里出现的其他等级追加在后；已删除行不产生选项。
        Assert.True(result.Success);
        Assert.Equal(new[] { "A", "B", "C", "D", "E" }, result.Data);
    }

    [Fact]
    public async Task GetGradeOptionsAsync_没有任何等级数据时仍返回固定四档()
    {
        var result = await CreateService().GetGradeOptionsAsync();

        Assert.True(result.Success);
        Assert.Equal(new[] { "A", "B", "C", "D" }, result.Data);
    }

    [Fact]
    public async Task GetProductGradeOptions_使用去重等级查询而不是分页列表()
    {
        var serviceMock = new Mock<IProductGradeReactService>(MockBehavior.Strict);
        serviceMock
            .Setup(service => service.GetGradeOptionsAsync())
            .ReturnsAsync(ApiResponse<List<string>>.OK(new List<string> { "A", "B", "C" }));

        var controller = new ReactProductGradesController(
            serviceMock.Object,
            NullLogger<ReactProductGradesController>.Instance
        );

        var actionResult = await controller.GetProductGradeOptions();

        var ok = Assert.IsType<OkObjectResult>(actionResult);
        var data = ok.Value!.GetType().GetProperty("data")!.GetValue(ok.Value) as System.Collections.IEnumerable;
        var values = data!.Cast<object>()
            .Select(option => (string)option.GetType().GetProperty("value")!.GetValue(option)!)
            .ToList();
        Assert.Equal(new[] { "A", "B", "C" }, values);
        // Strict mock：若控制器仍走分页列表 GetProductGradesAsync 会直接抛异常。
        serviceMock.VerifyAll();
    }

    [Fact]
    public async Task GetProductGrades_ClampsPagingAndPassesWarehouseIsActiveQuery()
    {
        ProductGradeListQueryDto? capturedQuery = null;
        var serviceMock = new Mock<IProductGradeReactService>();
        serviceMock
            .Setup(service => service.GetProductGradesAsync(It.IsAny<ProductGradeListQueryDto>()))
            .Callback<ProductGradeListQueryDto>(query => capturedQuery = query)
            .ReturnsAsync(ApiResponse<PagedResult<ProductGradeDto>>.OK(new PagedResult<ProductGradeDto>
            {
                Items = new List<ProductGradeDto>(),
                Total = 0,
                Page = 1,
                PageSize = 1000,
            }));

        var controller = new ReactProductGradesController(
            serviceMock.Object,
            NullLogger<ReactProductGradesController>.Instance
        );

        var actionResult = await controller.GetProductGrades(
            page: -3,
            pageSize: 5001,
            warehouseIsActive: false
        );

        Assert.IsType<OkObjectResult>(actionResult);
        Assert.NotNull(capturedQuery);
        Assert.Equal(1, capturedQuery!.Page);
        Assert.Equal(1000, capturedQuery.PageSize);
        Assert.False(capturedQuery.WarehouseIsActive);
    }

    private ProductGradeReactService CreateService()
    {
        return new ProductGradeReactService(
            CreateSqlSugarContext(_db),
            CreateHqSqlSugarContext(),
            CreateMapper(),
            NullLogger<ProductGradeReactService>.Instance,
            WarehouseProductChangeHistoryTestDouble.CreateNoop(),
            Mock.Of<ICurrentUserService>()
        );
    }

    private async Task SeedProductGradeAsync(
        string productCode,
        string supplierCode,
        string supplierName,
        string hbProductNo,
        string productName,
        string grade,
        decimal domesticPrice,
        decimal importPrice,
        decimal oemPrice,
        DateTime createdAt,
        bool warehouseIsActive = true,
        int? minOrderQuantity = null,
        string? categoryGuid = null,
        bool isDeleted = false
    )
    {
        var supplierExists = await _db.Queryable<ChinaSupplier>()
            .AnyAsync(s => s.SupplierCode == supplierCode);
        if (!supplierExists)
        {
            await _db.Insertable(new ChinaSupplier
            {
                Guid = $"{supplierCode}-guid",
                SupplierCode = supplierCode,
                SupplierName = supplierName,
            }).ExecuteCommandAsync();
        }

        await _db.Insertable(new DomesticProduct
        {
            ProductCode = productCode,
            SupplierCode = supplierCode,
            HBProductNo = hbProductNo,
            ProductName = productName,
            ProductImage = $"https://img.example.com/{hbProductNo}.jpg",
            DomesticPrice = domesticPrice,
        }).ExecuteCommandAsync();

        await _db.Insertable(new WarehouseProduct
        {
            ProductCode = productCode,
            ImportPrice = importPrice,
            OEMPrice = oemPrice,
            IsActive = warehouseIsActive,
            MinOrderQuantity = minOrderQuantity,
        }).ExecuteCommandAsync();

        await _db.Insertable(new Product
        {
            UUID = $"{productCode}-uuid",
            ProductCode = productCode,
            ItemNumber = hbProductNo,
            ProductName = productName,
            RetailPrice = oemPrice,
            WarehouseCategoryGUID = categoryGuid,
        }).ExecuteCommandAsync();

        await _db.Insertable(new ProductGrade
        {
            Id = $"{productCode}-grade",
            ProductCode = productCode,
            Grade = grade,
            CreatedAt = createdAt,
            IsDeleted = isDeleted,
            CreatedBy = "tester",
        }).ExecuteCommandAsync();
    }

    private async Task SeedWarehouseCategoryAsync(
        string categoryGuid,
        string categoryName,
        string chineseName,
        string? parentGuid = null
    )
    {
        await _db.Insertable(new WarehouseCategory
        {
            CategoryGUID = categoryGuid,
            CategoryName = categoryName,
            ChineseName = chineseName,
            ParentGUID = parentGuid,
            IsActive = true,
            IsDeleted = false,
        }).ExecuteCommandAsync();
    }

    private static IMapper CreateMapper()
    {
        return new MapperConfiguration(
            cfg => cfg.AddProfile<ReactProductGradeProfile>(),
            NullLoggerFactory.Instance
        ).CreateMapper();
    }

    private static SqlSugarContext CreateSqlSugarContext(ISqlSugarClient db)
    {
        var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(SqlSugarContext));
        var dbField = typeof(SqlSugarContext).GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic);
        dbField!.SetValue(context, db);
        return context;
    }

    private static HqSqlSugarContext CreateHqSqlSugarContext()
    {
        var context = (HqSqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(HqSqlSugarContext));
        var dbField = typeof(HqSqlSugarContext).GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic);
        dbField!.SetValue(context, new Mock<ISqlSugarClient>().Object);
        return context;
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
}
