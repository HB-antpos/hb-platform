using System.Reflection;
using System.Runtime.CompilerServices;
using BlazorApp.Api.Data;
using BlazorApp.Api.Models;
using BlazorApp.Api.Services.LocalSupplierCategories;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 供应商分类采集、归类与商品列表集成（SQLite）。
/// </summary>
public sealed class LocalSupplierCategoryServiceTests : IDisposable
{
    private const string Dats = "240";
    private const string DatsPage = "https://www.dats.com.au/office-stationery/adhesives-and-tape";

    private readonly string _dbPath;
    private readonly SqlSugarClient _db;
    private readonly BrowserExtensionOptions _options = new();

    public LocalSupplierCategoryServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"lsc-{Guid.NewGuid():N}.db");
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
            typeof(WarehouseCategory),
            typeof(HBLocalSupplier),
            typeof(StoreRetailPrice),
            typeof(DomesticProduct),
            typeof(ChinaSupplier),
            typeof(LocalSupplierCategory),
            typeof(LocalSupplierCategoryCapture),
            typeof(LocalSupplierCategoryProductAssignment)
        );
    }

    public void Dispose()
    {
        _db.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            File.Delete(_dbPath);
        }
        catch (IOException)
        {
            // 临时库清理失败不影响断言结果。
        }
    }

    [Fact]
    public async Task Capture_首次采集建立分类链并按货号归类()
    {
        await SeedProductAsync("P-1", "69798", Dats);
        await SeedProductAsync("P-2", "72264", Dats);
        await SeedProductAsync("P-OTHER", "69798", "243");

        var result = await CreateCaptureService().CaptureAsync(
            BuildCapture(new[] { "69798", "72264", "99999" }),
            "tester"
        );

        Assert.Equal(2, result.CategoriesCreated);
        Assert.Equal(1, result.Depth);
        Assert.Equal("Office Stationery > Adhesives and Tape", result.FullPath);
        Assert.Equal(2, result.MatchedProducts);
        Assert.Equal(2, result.AssignedProducts);
        Assert.Equal(1, result.UnmatchedItemNumberCount);
        Assert.Equal(new[] { "99999" }, result.UnmatchedSamples);

        var categories = await _db.Queryable<LocalSupplierCategory>().OrderBy(item => item.Depth).ToListAsync();
        Assert.Equal(new[] { "/office-stationery", "/office-stationery/adhesives-and-tape" }, categories.Select(item => item.ExternalKey));
        Assert.Null(categories[0].ParentGUID);
        Assert.Equal(categories[0].CategoryGUID, categories[1].ParentGUID);

        var assignments = await _db.Queryable<LocalSupplierCategoryProductAssignment>().OrderBy(item => item.ProductCode).ToListAsync();
        Assert.Equal(new[] { "P-1", "P-2" }, assignments.Select(item => item.ProductCode));
        Assert.All(assignments, item =>
        {
            Assert.Equal(result.CategoryGuid, item.CategoryGUID);
            Assert.Equal(LocalSupplierCategorySources.Website, item.Source);
        });
        // 其他供应商同货号商品不受影响；未匹配货号仍保留观察记录，供日后新建商品时归类。
        Assert.Equal(3, await _db.Queryable<LocalSupplierCategoryCapture>().CountAsync());
    }

    [Fact]
    public async Task Capture_重复采集幂等且累加观察次数()
    {
        await SeedProductAsync("P-1", "69798", Dats);
        var service = CreateCaptureService();

        await service.CaptureAsync(BuildCapture(new[] { "69798" }), "tester");
        var second = await service.CaptureAsync(BuildCapture(new[] { "69798" }), "tester");

        Assert.Equal(0, second.CategoriesCreated);
        Assert.Equal(0, second.AssignedProducts);
        Assert.Equal(1, second.UnchangedProducts);
        Assert.Equal(2, await _db.Queryable<LocalSupplierCategory>().CountAsync());
        var capture = await _db.Queryable<LocalSupplierCategoryCapture>().SingleAsync();
        Assert.Equal(2, capture.SeenCount);
    }

    [Fact]
    public async Task Capture_更深的分类优先且之后看到更浅分类不回退()
    {
        await SeedProductAsync("P-1", "69798", Dats);
        var service = CreateCaptureService();

        var shallow = await service.CaptureAsync(
            BuildCapture(new[] { "69798" }, ("Office Stationery", "/office-stationery")),
            "tester"
        );
        Assert.Equal(shallow.CategoryGuid, await AssignedCategoryAsync("P-1"));

        var deep = await service.CaptureAsync(BuildCapture(new[] { "69798" }), "tester");
        Assert.Equal(deep.CategoryGuid, await AssignedCategoryAsync("P-1"));

        await service.CaptureAsync(
            BuildCapture(new[] { "69798" }, ("Office Stationery", "/office-stationery")),
            "tester"
        );
        Assert.Equal(deep.CategoryGuid, await AssignedCategoryAsync("P-1"));
    }

    [Fact]
    public async Task Capture_促销分类不参与归类()
    {
        await SeedProductAsync("P-1", "69798", Dats);
        var service = CreateCaptureService();

        var clearance = await service.CaptureAsync(
            BuildCapture(new[] { "69798" }, ("Clearance", "/clearance")),
            "tester"
        );
        Assert.True(clearance.IsPromotional);
        Assert.Null(await AssignedCategoryAsync("P-1"));

        var normal = await service.CaptureAsync(BuildCapture(new[] { "69798" }), "tester");
        Assert.Equal(normal.CategoryGuid, await AssignedCategoryAsync("P-1"));
    }

    [Fact]
    public async Task Capture_人工指定锁定不被采集覆盖_清空后恢复自动()
    {
        await SeedProductAsync("P-1", "69798", Dats);
        var service = CreateCaptureService();
        var office = await service.CaptureAsync(
            BuildCapture(new[] { "OTHER" }, ("Office Stationery", "/office-stationery")),
            "tester"
        );
        var assignments = new LocalSupplierCategoryAssignmentService(_db);
        await assignments.ApplyProductEditAsync(EditContext("P-1", Dats, Dats, requested: office.CategoryGuid));

        var capture = await service.CaptureAsync(BuildCapture(new[] { "69798" }), "tester");

        Assert.Equal(1, capture.SkippedManual);
        Assert.Equal(office.CategoryGuid, await AssignedCategoryAsync("P-1"));

        await assignments.ApplyProductEditAsync(EditContext("P-1", Dats, Dats, clear: true));
        Assert.Equal(capture.CategoryGuid, await AssignedCategoryAsync("P-1"));
        var row = await _db.Queryable<LocalSupplierCategoryProductAssignment>().SingleAsync();
        Assert.Equal(LocalSupplierCategorySources.Website, row.Source);
    }

    [Fact]
    public async Task ManualCategory_不属于商品供应商时拒绝()
    {
        await SeedProductAsync("P-1", "69798", Dats);
        var office = await CreateCaptureService().CaptureAsync(BuildCapture(new[] { "69798" }), "tester");
        var assignments = new LocalSupplierCategoryAssignmentService(_db);

        var ex = await Assert.ThrowsAsync<LocalSupplierCategoryValidationException>(() =>
            assignments.ApplyProductEditAsync(EditContext("P-1", Dats, "243", requested: office.CategoryGuid))
        );

        Assert.Equal(LocalSupplierCategoryErrorCodes.CategorySupplierMismatch, ex.ErrorCode);
    }

    [Fact]
    public async Task ProductEdit_换供应商时旧归属作废_换回200时清除()
    {
        await SeedProductAsync("P-1", "69798", Dats);
        await CreateCaptureService().CaptureAsync(BuildCapture(new[] { "69798" }), "tester");
        var assignments = new LocalSupplierCategoryAssignmentService(_db);

        await assignments.ApplyProductEditAsync(EditContext("P-1", Dats, "243"));
        Assert.Null(await AssignedCategoryAsync("P-1"));

        await assignments.ApplyProductEditAsync(EditContext("P-1", "243", Dats));
        Assert.NotNull(await AssignedCategoryAsync("P-1"));

        await assignments.ApplyProductEditAsync(EditContext("P-1", Dats, "200"));
        Assert.Null(await AssignedCategoryAsync("P-1"));
    }

    [Fact]
    public async Task ProductEdit_改码时归属随商品编码迁移()
    {
        await SeedProductAsync("P-OLD", "69798", Dats);
        var capture = await CreateCaptureService().CaptureAsync(BuildCapture(new[] { "69798" }), "tester");

        await new LocalSupplierCategoryAssignmentService(_db).ApplyProductEditAsync(
            new LocalSupplierCategoryAssignmentService.ProductEditContext(
                "P-NEW",
                OldProductCode: "P-OLD",
                OldSupplierCode: Dats,
                NewSupplierCode: Dats,
                OldItemNumber: "69798",
                NewItemNumber: "69798",
                RequestedCategoryGuid: null,
                ClearRequested: false,
                Actor: "tester"
            )
        );

        Assert.Null(await AssignedCategoryAsync("P-OLD"));
        Assert.Equal(capture.CategoryGuid, await AssignedCategoryAsync("P-NEW"));
    }

    [Fact]
    public async Task Capture_GFA按商品编码匹配()
    {
        await SeedProductAsync("ABC/123", "IGNORED", "236");
        var gfaPage = "https://gfa.opmetrix.store/products/view?category=12";

        var result = await CreateCaptureService().CaptureAsync(
            new BrowserExtensionCategoryCaptureRequestDto
            {
                SupplierCode = "236",
                PageUrl = gfaPage,
                CategoryPath = new List<BrowserExtensionCategoryPathNodeDto>
                {
                    new() { Name = "Toys", Key = "/products/view?category=12&page=3", Url = gfaPage },
                },
                ItemNumbers = new List<string> { "abc/123" },
                Mode = BrowserExtensionCategoryCaptureModes.Crawl,
            },
            "tester"
        );

        Assert.Equal(1, result.AssignedProducts);
        var category = await _db.Queryable<LocalSupplierCategory>().SingleAsync();
        Assert.Equal("/products/view?category=12", category.ExternalKey);
    }

    [Fact]
    public async Task Capture_拒绝200_非供应商页面与关闭的总开关()
    {
        var service = CreateCaptureService();
        var hotBargain = BuildCapture(new[] { "A" });
        hotBargain.SupplierCode = "200";
        var hbError = await Assert.ThrowsAsync<LocalSupplierCategoryValidationException>(() =>
            service.CaptureAsync(hotBargain, "tester")
        );
        Assert.Equal(LocalSupplierCategoryErrorCodes.SupplierNotCapturable, hbError.ErrorCode);

        var foreignPage = BuildCapture(new[] { "A" });
        foreignPage.PageUrl = "https://evil.example/office-stationery";
        await Assert.ThrowsAsync<LocalSupplierCategoryValidationException>(() =>
            service.CaptureAsync(foreignPage, "tester")
        );

        _options.CategoryCaptureEnabled = false;
        await Assert.ThrowsAsync<LocalSupplierCategoryFeatureDisabledException>(() =>
            CreateCaptureService().CaptureAsync(BuildCapture(new[] { "A" }), "tester")
        );
    }

    [Fact]
    public async Task TreeSnapshot_建立空分类_排序与孤儿节点()
    {
        var result = await CreateCaptureService().ApplyTreeSnapshotAsync(
            new BrowserExtensionCategoryTreeSnapshotRequestDto
            {
                SupplierCode = Dats,
                SourceUrl = "https://www.dats.com.au/",
                Nodes = new List<BrowserExtensionCategoryTreeNodeDto>
                {
                    new() { Key = "/office-stationery/labels", Name = "Labels", ParentKey = "/office-stationery", SortOrder = 2 },
                    new() { Key = "/office-stationery", Name = "Office Stationery", SortOrder = 1 },
                    new() { Key = "/clearance", Name = "Clearance", SortOrder = 0 },
                    new() { Key = "/party/balloons", Name = "Balloons", ParentKey = "/party", SortOrder = 3 },
                },
            },
            "tester"
        );

        Assert.Equal(4, result.Created);
        Assert.Equal(1, result.OrphanCount);
        Assert.Equal(1, result.PromotionalCount);
        var labels = await _db.Queryable<LocalSupplierCategory>().SingleAsync(item => item.ExternalKey == "/office-stationery/labels");
        Assert.Equal(1, labels.Depth);
        Assert.Equal("Office Stationery > Labels", labels.FullPath);
        Assert.Equal(2, labels.SortOrder);
    }

    [Fact]
    public async Task ReactService_切换促销标记后重算归类()
    {
        await SeedProductAsync("P-1", "69798", Dats);
        var capture = await CreateCaptureService().CaptureAsync(BuildCapture(new[] { "69798" }), "tester");
        var react = CreateReactService();

        var marked = await react.SetPromotionalAsync(capture.CategoryGuid, true, "tester");

        Assert.Equal(1, marked.Cleared);
        Assert.Null(await AssignedCategoryAsync("P-1"));
        var category = await _db.Queryable<LocalSupplierCategory>().SingleAsync(item => item.CategoryGUID == capture.CategoryGuid);
        Assert.Equal(LocalSupplierCategoryPromotionalSources.Manual, category.PromotionalSource);

        var unmarked = await react.SetPromotionalAsync(capture.CategoryGuid, false, "tester");
        Assert.Equal(1, unmarked.Reassigned);
        Assert.Equal(capture.CategoryGuid, await AssignedCategoryAsync("P-1"));
    }

    [Fact]
    public async Task ReactService_重新解析清理陈旧归属并返回概览与树()
    {
        await SeedProductAsync("P-1", "69798", Dats);
        await SeedProductAsync("P-2", "72264", Dats);
        var capture = await CreateCaptureService().CaptureAsync(BuildCapture(new[] { "69798", "72264" }), "tester");
        // 模拟 HQ 同步把 P-2 改到别的供应商：归属行仍记在 240 名下。
        await _db.Updateable<Product>()
            .SetColumns(item => item.LocalSupplierCode == "243")
            .Where(item => item.ProductCode == "P-2")
            .ExecuteCommandAsync();
        var react = CreateReactService();

        var resolved = await react.ResolveSupplierAsync(Dats, "tester");

        Assert.Equal(1, resolved.ProductsScanned);
        Assert.Equal(1, resolved.Unchanged);
        Assert.Equal(1, resolved.StaleRemoved);
        Assert.Null(await AssignedCategoryAsync("P-2"));

        var summary = await react.GetSummaryAsync();
        Assert.Equal("200", summary[0].SupplierCode);
        var dats = Assert.Single(summary, item => item.SupplierCode == Dats);
        Assert.Equal(2, dats.CategoryCount);
        Assert.Equal(1, dats.ProductCount);
        Assert.Equal(1, dats.AssignedCount);
        Assert.NotNull(dats.LastCapturedAt);
        // 时间以 UTC 语义返回，JSON 序列化会带 Z，前端才能正确换算为本地时间。
        Assert.Equal(DateTimeKind.Utc, dats.LastCapturedAt!.Value.Kind);

        var tree = await react.GetTreeAsync(Dats);
        var root = Assert.Single(tree);
        var leaf = Assert.Single(root.Children);
        Assert.Equal(capture.CategoryGuid, leaf.CategoryGuid);
        Assert.Equal(1, leaf.ProductCount);

        await Assert.ThrowsAsync<LocalSupplierCategoryValidationException>(() => react.ResolveSupplierAsync("200", "tester"));
    }

    [Fact]
    public async Task ReactService_全部供应商重新归类_为采集后新增的商品补归类并清理陈旧归属()
    {
        await SeedProductAsync("P-OLD", "69798", Dats);
        // 72264 采集时还没有对应商品，只留下观察记录。
        await CreateCaptureService().CaptureAsync(BuildCapture(new[] { "69798", "72264" }), "tester");
        // HQ 同步随后批量写入新商品，不经过商品编辑联动，所以此时仍未归类。
        await SeedProductAsync("P-NEW", "72264", Dats);
        Assert.Null(await AssignedCategoryAsync("P-NEW"));
        // 另一供应商只剩陈旧归属：商品已被改到 200。
        await SeedProductAsync("P-MOVED", "X-1", "200");
        await _db.Insertable(new LocalSupplierCategoryProductAssignment
        {
            ProductCode = "P-MOVED",
            LocalSupplierCode = "243",
            CategoryGUID = "gone",
            Source = LocalSupplierCategorySources.Website,
            AssignedAt = DateTime.UtcNow,
        }).ExecuteCommandAsync();

        var result = await CreateReactService().ResolveAllSuppliersAsync("System");

        Assert.Equal(2, result.SupplierCount);
        Assert.Equal(1, result.Assigned);
        Assert.Equal(1, result.StaleRemoved);
        Assert.Empty(result.FailedSuppliers);
        Assert.NotNull(await AssignedCategoryAsync("P-NEW"));
        Assert.Equal(await AssignedCategoryAsync("P-OLD"), await AssignedCategoryAsync("P-NEW"));
        Assert.Null(await AssignedCategoryAsync("P-MOVED"));
    }

    [Fact]
    public async Task ProductList_回填供应商分类并支持子树筛选与仅未归类()
    {
        await SeedWarehouseCategoryAsync("WC-ROOT", null, "Stationery");
        await SeedWarehouseCategoryAsync("WC-LEAF", "WC-ROOT", "Pens");
        await SeedProductAsync("P-HB", "HB-1", "200", warehouseCategoryGuid: "WC-LEAF");
        await SeedProductAsync("P-HB-NONE", "HB-2", null);
        await SeedProductAsync("P-DATS", "69798", Dats);
        await SeedProductAsync("P-DATS-NONE", "11111", Dats);
        var capture = await CreateCaptureService().CaptureAsync(BuildCapture(new[] { "69798" }), "tester");
        var rootGuid = (await _db.Queryable<LocalSupplierCategory>().SingleAsync(item => item.Depth == 0)).CategoryGUID;
        var service = CreateProductService();

        var all = await service.GetPagedListAsync(new ProductReactFilterDto { PageNumber = 1, PageSize = 50, SortBy = "productcode" });
        var hb = Assert.Single(all.Items, item => item.ProductCode == "P-HB");
        Assert.Equal("WC-LEAF", hb.SupplierCategoryGUID);
        Assert.Equal("Pens", hb.SupplierCategoryName);
        Assert.Equal("Stationery > Pens", hb.SupplierCategoryPath);
        Assert.Equal(LocalSupplierCategorySources.Warehouse, hb.SupplierCategorySource);
        var dats = Assert.Single(all.Items, item => item.ProductCode == "P-DATS");
        Assert.Equal(capture.CategoryGuid, dats.SupplierCategoryGUID);
        Assert.Equal("Adhesives and Tape", dats.SupplierCategoryName);
        Assert.Equal(LocalSupplierCategorySources.Website, dats.SupplierCategorySource);
        Assert.Null(all.Items.Single(item => item.ProductCode == "P-DATS-NONE").SupplierCategoryGUID);

        var bySupplierRoot = await service.GetPagedListAsync(new ProductReactFilterDto
        {
            PageNumber = 1,
            PageSize = 50,
            SupplierCategoryGUIDs = new List<string> { rootGuid },
        });
        Assert.Equal(new[] { "P-DATS" }, bySupplierRoot.Items.Select(item => item.ProductCode));

        var byWarehouseRoot = await service.GetPagedListAsync(new ProductReactFilterDto
        {
            PageNumber = 1,
            PageSize = 50,
            SupplierCategoryGUIDs = new List<string> { "WC-ROOT" },
        });
        Assert.Equal(new[] { "P-HB" }, byWarehouseRoot.Items.Select(item => item.ProductCode));

        var mixed = await service.GetPagedListAsync(new ProductReactFilterDto
        {
            PageNumber = 1,
            PageSize = 50,
            SortBy = "productcode",
            SupplierCategoryGUIDs = new List<string> { rootGuid, "WC-LEAF" },
        });
        Assert.Equal(new[] { "P-DATS", "P-HB" }, mixed.Items.Select(item => item.ProductCode));

        var unassigned = await service.GetPagedListAsync(new ProductReactFilterDto
        {
            PageNumber = 1,
            PageSize = 50,
            SortBy = "productcode",
            SupplierCategoryUnassignedOnly = true,
        });
        Assert.Equal(new[] { "P-DATS-NONE", "P-HB-NONE" }, unassigned.Items.Select(item => item.ProductCode));

        var unknown = await service.GetPagedListAsync(new ProductReactFilterDto
        {
            PageNumber = 1,
            PageSize = 50,
            SupplierCategoryGUIDs = new List<string> { "missing-guid" },
        });
        Assert.Empty(unknown.Items);

        var detail = await service.GetByIdAsync("P-DATS");
        Assert.Equal("Office Stationery > Adhesives and Tape", detail.Data!.SupplierCategoryPath);
    }

    private BrowserExtensionCategoryCaptureRequestDto BuildCapture(
        IEnumerable<string> itemNumbers,
        params (string Name, string Key)[] path
    )
    {
        var nodes = path.Length > 0
            ? path
            : new[] { ("Office Stationery", "/office-stationery"), ("Adhesives and Tape", "/office-stationery/adhesives-and-tape") };
        return new BrowserExtensionCategoryCaptureRequestDto
        {
            SupplierCode = Dats,
            PageUrl = DatsPage,
            CategoryPath = nodes
                .Select(node => new BrowserExtensionCategoryPathNodeDto
                {
                    Name = node.Item1,
                    Key = node.Item2,
                    Url = "https://www.dats.com.au" + node.Item2,
                })
                .ToList(),
            ItemNumbers = itemNumbers.ToList(),
            CapturedAt = DateTimeOffset.UtcNow,
            Mode = BrowserExtensionCategoryCaptureModes.Passive,
        };
    }

    private static LocalSupplierCategoryAssignmentService.ProductEditContext EditContext(
        string productCode,
        string oldSupplier,
        string newSupplier,
        string? requested = null,
        bool clear = false
    ) =>
        new(
            productCode,
            OldProductCode: null,
            OldSupplierCode: oldSupplier,
            NewSupplierCode: newSupplier,
            OldItemNumber: "69798",
            NewItemNumber: "69798",
            RequestedCategoryGuid: requested,
            ClearRequested: clear,
            Actor: "tester"
        );

    private async Task<string?> AssignedCategoryAsync(string productCode)
    {
        var row = await _db.Queryable<LocalSupplierCategoryProductAssignment>()
            .Where(item => item.ProductCode == productCode)
            .FirstAsync();
        return row?.CategoryGUID;
    }

    private async Task SeedProductAsync(
        string productCode,
        string itemNumber,
        string? supplier,
        string? warehouseCategoryGuid = null
    )
    {
        var now = DateTime.UtcNow;
        await _db.Insertable(new Product
        {
            UUID = $"product-{productCode}",
            ProductCode = productCode,
            ItemNumber = itemNumber,
            LocalSupplierCode = supplier,
            WarehouseCategoryGUID = warehouseCategoryGuid,
            ProductName = $"商品{productCode}",
            IsActive = true,
            IsDeleted = false,
            CreatedAt = now,
            UpdatedAt = now,
        }).ExecuteCommandAsync();
    }

    private async Task SeedWarehouseCategoryAsync(string guid, string? parent, string name)
    {
        await _db.Insertable(new WarehouseCategory
        {
            CategoryGUID = guid,
            ParentGUID = parent,
            CategoryName = name,
            IsActive = true,
            IsDeleted = false,
        }).ExecuteCommandAsync();
    }

    private LocalSupplierCategoryCaptureService CreateCaptureService() =>
        new(
            CreateSqlSugarContext(_db),
            CreateOptionsSnapshot(),
            NullLogger<LocalSupplierCategoryCaptureService>.Instance
        );

    private LocalSupplierCategoryReactService CreateReactService() =>
        new(CreateSqlSugarContext(_db), CreateOptionsSnapshot());

    private ProductReactService CreateProductService() =>
        new(
            CreateSqlSugarContext(_db),
            CreateHqSqlSugarContext(_db),
            new Mock<AutoMapper.IMapper>().Object,
            NullLogger<ProductReactService>.Instance,
            new Mock<Microsoft.AspNetCore.Http.IHttpContextAccessor>().Object,
            new ProductAuditNoopHistoryService(),
            new ProductAuditSystemCurrentUserService()
        );

    private IOptionsSnapshot<BrowserExtensionOptions> CreateOptionsSnapshot()
    {
        var snapshot = new Mock<IOptionsSnapshot<BrowserExtensionOptions>>();
        snapshot.Setup(item => item.Value).Returns(_options);
        return snapshot.Object;
    }

    private static SqlSugarContext CreateSqlSugarContext(ISqlSugarClient db)
    {
        var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(SqlSugarContext));
        typeof(SqlSugarContext)
            .GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(context, db);
        return context;
    }

    private static HqSqlSugarContext CreateHqSqlSugarContext(ISqlSugarClient db)
    {
        var context = (HqSqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(HqSqlSugarContext));
        typeof(HqSqlSugarContext)
            .GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(context, db);
        return context;
    }
}
