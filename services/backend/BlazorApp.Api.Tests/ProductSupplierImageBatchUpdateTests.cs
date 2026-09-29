using System.Reflection;
using System.Runtime.CompilerServices;
using AutoMapper;
using BlazorApp.Api.Controllers.React;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Mappings.Profiles.React;
using BlazorApp.Api.Services;
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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class ProductSupplierImageBatchUpdateTests : IDisposable
{
    private readonly string _localDbPath;
    private readonly string _hqDbPath;
    private readonly SqliteConnection _localConnection;
    private readonly SqliteConnection _hqConnection;
    private readonly SqlSugarClient _localDb;
    private readonly SqlSugarScope _hqDb;
    private readonly IMapper _mapper;

    public ProductSupplierImageBatchUpdateTests()
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
            typeof(HBLocalSupplier),
            typeof(WarehouseCategory),
            typeof(LocalSupplierCategory),
            typeof(LocalSupplierCategoryCapture),
            typeof(LocalSupplierCategoryProductAssignment)
        );
        _hqDb.CodeFirst.InitTables(typeof(DIC_商品信息字典表), typeof(DIC_供应商信息表));
    }

    [Fact]
    public async Task BatchUpdateSupplierImagesAsync_同时更新Hbweb和Hq并可保存供应商图片基础Url()
    {
        await SeedSupplierAsync("DATS", null);
        await SeedProductAsync("P001", "DATS", "72653", productImage: null);
        await SeedProductAsync("P002", "DATS", "72891", productImage: "");
        await _hqDb.Insertable(new[]
        {
            CreateHqProduct(1, "P001", ""),
            CreateHqProduct(2, "P002", " "),
        }).ExecuteCommandAsync();

        var response = await CreateProductService().BatchUpdateSupplierImagesAsync(new BatchUpdateSupplierImagesRequest
        {
            LocalSupplierCode = "DATS",
            UrlTemplate = "https://www.dats.com.au/images/ProductImages/500/{itemNumber}.jpg",
            UpdateHbweb = true,
            UpdateHq = true,
            SaveSupplierImageBaseUrl = true,
        });

        Assert.True(response.Success, response.Message);
        Assert.Equal(2, response.Data?.TotalCount);
        Assert.Equal(2, response.Data?.HbwebUpdatedCount);
        Assert.Equal(2, response.Data?.HqUpdatedCount);
        Assert.Equal(0, response.Data?.HbwebSkippedExistingImageCount);
        Assert.Equal(0, response.Data?.HqSkippedExistingImageCount);

        var localProduct = await _localDb.Queryable<Product>().SingleAsync(row => row.ProductCode == "P001");
        Assert.Equal("https://www.dats.com.au/images/ProductImages/500/72653.jpg", localProduct.ProductImage);

        var hqProduct = await _hqDb.Queryable<DIC_商品信息字典表>().SingleAsync(row => row.H商品编码 == "P002");
        Assert.Equal("https://www.dats.com.au/images/ProductImages/500/72891.jpg", hqProduct.H商品图片);

        var supplier = await _localDb.Queryable<HBLocalSupplier>().SingleAsync(row => row.LocalSupplierCode == "DATS");
        Assert.Equal("https://www.dats.com.au/images/ProductImages/500/{itemNumber}.jpg", supplier.ImageBaseUrl);
    }

    [Fact]
    public async Task BatchUpdateSupplierImagesAsync_Hbweb已有图片时不覆盖仅补空图并统计跳过数()
    {
        await SeedSupplierAsync("DATS", null);
        var existingUpdatedAt = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        await SeedProductAsync("P001", "DATS", "72653", productImage: "https://existing.example.com/p001.jpg", updatedAt: existingUpdatedAt, updatedBy: "ExistingUser");
        await SeedProductAsync("P002", "DATS", "72891", productImage: " ");

        var response = await CreateProductService().BatchUpdateSupplierImagesAsync(new BatchUpdateSupplierImagesRequest
        {
            LocalSupplierCode = "DATS",
            UrlTemplate = "https://cdn.example.com/{itemNumber}.jpg",
            UpdateHbweb = true,
            UpdateHq = false,
        });

        Assert.True(response.Success, response.Message);
        Assert.Equal(1, response.Data?.HbwebUpdatedCount);
        Assert.Equal(1, response.Data?.HbwebSkippedExistingImageCount);

        var existingProduct = await _localDb.Queryable<Product>().SingleAsync(row => row.ProductCode == "P001");
        Assert.Equal("https://existing.example.com/p001.jpg", existingProduct.ProductImage);
        Assert.Equal(existingUpdatedAt, existingProduct.UpdatedAt);
        Assert.Equal("ExistingUser", existingProduct.UpdatedBy);

        var emptyProduct = await _localDb.Queryable<Product>().SingleAsync(row => row.ProductCode == "P002");
        Assert.Equal("https://cdn.example.com/72891.jpg", emptyProduct.ProductImage);
    }

    [Fact]
    public async Task BatchUpdateSupplierImagesAsync_Hq已有图片时不覆盖仅补空图并统计跳过数()
    {
        await SeedSupplierAsync("DATS", null);
        await SeedProductAsync("P001", "DATS", "72653");
        await SeedProductAsync("P002", "DATS", "72891");
        var existingModifyDate = new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        await _hqDb.Insertable(new[]
        {
            CreateHqProduct(1, "P001", "https://existing.example.com/p001.jpg", lastModifyDate: existingModifyDate, lastModifier: "ExistingHqUser"),
            CreateHqProduct(2, "P002", ""),
        }).ExecuteCommandAsync();

        var response = await CreateProductService().BatchUpdateSupplierImagesAsync(new BatchUpdateSupplierImagesRequest
        {
            LocalSupplierCode = "DATS",
            UrlTemplate = "https://cdn.example.com/{itemNumber}.jpg",
            UpdateHbweb = false,
            UpdateHq = true,
        });

        Assert.True(response.Success, response.Message);
        Assert.Equal(1, response.Data?.HqUpdatedCount);
        Assert.Equal(1, response.Data?.HqSkippedExistingImageCount);

        var existingHqProduct = await _hqDb.Queryable<DIC_商品信息字典表>().SingleAsync(row => row.ID == 1);
        Assert.Equal("https://existing.example.com/p001.jpg", existingHqProduct.H商品图片);
        Assert.Equal(existingModifyDate, existingHqProduct.FGC_LastModifyDate);
        Assert.Equal("ExistingHqUser", existingHqProduct.FGC_LastModifier);

        var emptyHqProduct = await _hqDb.Queryable<DIC_商品信息字典表>().SingleAsync(row => row.ID == 2);
        Assert.Equal("https://cdn.example.com/72891.jpg", emptyHqProduct.H商品图片);
    }

    [Fact]
    public async Task BatchUpdateSupplierImagesAsync_缺少货号跳过且不回退ProductCode()
    {
        await SeedSupplierAsync("DATS", null);
        await SeedProductAsync("P001", "DATS", "");

        var response = await CreateProductService().BatchUpdateSupplierImagesAsync(new BatchUpdateSupplierImagesRequest
        {
            LocalSupplierCode = "DATS",
            UrlTemplate = "https://cdn.example.com/{itemNumber}.jpg",
            UpdateHbweb = true,
            UpdateHq = false,
        });

        Assert.True(response.Success, response.Message);
        Assert.Equal(1, response.Data?.SkippedCount);
        Assert.Contains(response.Data?.Errors ?? new List<string>(), item => item.Contains("P001") && item.Contains("货号"));

        var localProduct = await _localDb.Queryable<Product>().SingleAsync(row => row.ProductCode == "P001");
        Assert.Null(localProduct.ProductImage);
    }

    [Fact]
    public async Task BatchUpdateSupplierImagesAsync_图片地址超过本地字段长度时跳过()
    {
        await SeedSupplierAsync("DATS", null);
        await SeedProductAsync("P001", "DATS", "72653");

        var response = await CreateProductService().BatchUpdateSupplierImagesAsync(new BatchUpdateSupplierImagesRequest
        {
            LocalSupplierCode = "DATS",
            UrlTemplate = $"https://cdn.example.com/{new string('a', 180)}/{{itemNumber}}.jpg",
            UpdateHbweb = true,
            UpdateHq = false,
        });

        Assert.True(response.Success, response.Message);
        Assert.Equal(1, response.Data?.SkippedCount);
        Assert.Contains(response.Data?.Errors ?? new List<string>(), item => item.Contains("超过 200 字符"));

        var localProduct = await _localDb.Queryable<Product>().SingleAsync(row => row.ProductCode == "P001");
        Assert.Null(localProduct.ProductImage);
    }

    [Fact]
    public async Task BatchUpdateSupplierImagesAsync_Hq异常但Hbweb已提交时返回部分成功()
    {
        await SeedSupplierAsync("DATS", null);
        await SeedProductAsync("P001", "DATS", "72653");
        _hqDb.DbMaintenance.DropTable<DIC_商品信息字典表>();

        var response = await CreateProductService().BatchUpdateSupplierImagesAsync(new BatchUpdateSupplierImagesRequest
        {
            LocalSupplierCode = "DATS",
            UrlTemplate = "https://cdn.example.com/{itemNumber}.jpg",
            UpdateHbweb = true,
            UpdateHq = true,
        });

        Assert.True(response.Success, response.Message);
        Assert.Equal(1, response.Data?.HbwebUpdatedCount);
        Assert.Equal(1, response.Data?.HqFailedCount);
        Assert.Contains("部分完成", response.Message);

        var localProduct = await _localDb.Queryable<Product>().SingleAsync(row => row.ProductCode == "P001");
        Assert.Equal("https://cdn.example.com/72653.jpg", localProduct.ProductImage);
    }

    [Fact]
    public async Task BatchUpdateSupplierImagesAsync_Hq商品编码重复时跳过避免多行误写()
    {
        await SeedSupplierAsync("DATS", null);
        await SeedProductAsync("P001", "DATS", "72653");
        await _hqDb.Insertable(new[]
        {
            CreateHqProduct(1, "P001", "old-1.jpg"),
            CreateHqProduct(2, "P001", "old-duplicate.jpg"),
        }).ExecuteCommandAsync();

        var response = await CreateProductService().BatchUpdateSupplierImagesAsync(new BatchUpdateSupplierImagesRequest
        {
            LocalSupplierCode = "DATS",
            UrlTemplate = "https://cdn.example.com/{itemNumber}.jpg",
            UpdateHbweb = false,
            UpdateHq = true,
        });

        Assert.True(response.Success, response.Message);
        Assert.Equal(0, response.Data?.HqUpdatedCount);
        Assert.Equal(1, response.Data?.HqFailedCount);
        Assert.Contains(response.Data?.Errors ?? new List<string>(), item => item.Contains("P001") && item.Contains("重复"));

        var hqImages = await _hqDb.Queryable<DIC_商品信息字典表>()
            .Where(row => row.H商品编码 == "P001")
            .Select(row => row.H商品图片)
            .ToListAsync();
        Assert.Contains("old-1.jpg", hqImages);
        Assert.Contains("old-duplicate.jpg", hqImages);
    }

    [Fact]
    public async Task BatchUpdateSupplierImagesAsync_大量错误时限制明细但统计保持完整()
    {
        await SeedSupplierAsync("DATS", null);
        for (var index = 1; index <= 105; index++)
        {
            await SeedProductAsync($"P{index:000}", "DATS", $"ITEM{index:000}");
        }

        var response = await CreateProductService().BatchUpdateSupplierImagesAsync(new BatchUpdateSupplierImagesRequest
        {
            LocalSupplierCode = "DATS",
            UrlTemplate = "https://cdn.example.com/{itemNumber}.jpg",
            UpdateHbweb = false,
            UpdateHq = true,
        });

        Assert.True(response.Success, response.Message);
        Assert.Equal(105, response.Data?.HqFailedCount);
        Assert.Equal(100, response.Data?.Errors.Count);
        Assert.Contains(response.Data?.Errors ?? new List<string>(), item => item.Contains("还有 6 条错误未显示"));
    }

    [Fact]
    public async Task LocalSupplierReactService_创建更新查询时保留图片基础Url()
    {
        var create = await CreateSupplierService().CreateAsync(new CreateLocalSupplierDto
        {
            Name = "测试供应商",
            Status = 1,
            ImageBaseUrl = "https://cdn.example.com/{itemNumber}.jpg",
        });

        Assert.True(create.Success, create.Message);
        Assert.Equal("https://cdn.example.com/{itemNumber}.jpg", create.Data?.ImageBaseUrl);

        var update = await CreateSupplierService().UpdateAsync(create.Data!.LocalSupplierCode, new UpdateLocalSupplierDto
        {
            Name = "测试供应商2",
            Status = 1,
            ImageBaseUrl = "https://cdn2.example.com/{itemNumber}.jpg",
        });

        Assert.True(update.Success, update.Message);
        var active = await CreateSupplierService().GetActiveSuppliersAsync();
        Assert.Equal("https://cdn2.example.com/{itemNumber}.jpg", active.Single().ImageBaseUrl);
    }

    [Fact]
    public async Task BatchUpdateSupplierImagesAsync_供应商没有商品时仍可保存图片基础Url()
    {
        await SeedSupplierAsync("EMPTY", null);

        var response = await CreateProductService().BatchUpdateSupplierImagesAsync(new BatchUpdateSupplierImagesRequest
        {
            LocalSupplierCode = "EMPTY",
            UrlTemplate = "https://cdn.example.com/{itemNumber}.jpg",
            UpdateHbweb = false,
            UpdateHq = false,
            SaveSupplierImageBaseUrl = true,
        });

        Assert.True(response.Success, response.Message);
        Assert.Equal(0, response.Data?.TotalCount);

        var supplier = await _localDb.Queryable<HBLocalSupplier>().SingleAsync(row => row.LocalSupplierCode == "EMPTY");
        Assert.Equal("https://cdn.example.com/{itemNumber}.jpg", supplier.ImageBaseUrl);
    }

    [Fact]
    public async Task LocalSupplierReactService_从Hq同步时不覆盖本地图片基础Url()
    {
        await SeedSupplierAsync("DATS", "https://local.example.com/{itemNumber}.jpg");
        await _hqDb.Insertable(new DIC_供应商信息表
        {
            H供应商编码 = "DATS",
            H供应商名称 = "Dats From HQ",
            H联系人 = "HQ Contact",
            HEMAIL地址 = "hq@example.com",
            FGC_CreateDate = DateTime.UtcNow.AddDays(-10),
            FGC_LastModifyDate = DateTime.UtcNow,
        }).ExecuteCommandAsync();

        var response = await CreateSupplierService().SyncFromDicAsync(null, overwrite: true);

        Assert.True(response.Success, response.Message);
        var supplier = await _localDb.Queryable<HBLocalSupplier>().SingleAsync(row => row.LocalSupplierCode == "DATS");
        Assert.Equal("Dats From HQ", supplier.Name);
        Assert.Equal("https://local.example.com/{itemNumber}.jpg", supplier.ImageBaseUrl);
    }

    [Fact]
    public void SqlSugarContext_既有本地供应商表缺少图片基础Url列时自动补齐()
    {
        _localDb.DbMaintenance.DropTable<HBLocalSupplier>();
        _localDb.Ado.ExecuteCommand("""
            CREATE TABLE LocalSupplier (
                Guid TEXT NOT NULL,
                LocalSupplierCode TEXT NOT NULL,
                Name TEXT NULL
            )
            """);

        var context = CreateSqlSugarContext(_localDb);
        typeof(SqlSugarContext)
            .GetMethod("EnsureLocalSupplierImageBaseUrlColumn", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(context, null);

        var columns = _localDb.DbMaintenance.GetColumnInfosByTableName("LocalSupplier", false);
        Assert.Contains(columns, column => string.Equals(column.DbColumnName, "ImageBaseUrl", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SqlSugarContext_既有销售统计状态表缺少任务跟踪列时自动补齐()
    {
        _localDb.Ado.ExecuteCommand("DROP TABLE IF EXISTS SalesStatisticRefreshState");
        _localDb.Ado.ExecuteCommand("""
            CREATE TABLE SalesStatisticRefreshState (
                StatisticType TEXT NOT NULL,
                Date TEXT NOT NULL,
                Status TEXT NOT NULL,
                LastSourceUploadTime TEXT NULL,
                SourceTimeZone TEXT NOT NULL,
                LastAggregatedAtUtc TEXT NULL,
                LastCheckedAtUtc TEXT NULL,
                ErrorMessage TEXT NULL
            )
            """);

        var context = CreateSqlSugarContext(_localDb);
        typeof(SqlSugarContext)
            .GetMethod("EnsureSalesStatisticRefreshStateJobColumns", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(context, null);

        var columns = _localDb.DbMaintenance.GetColumnInfosByTableName("SalesStatisticRefreshState", false);
        Assert.Contains(columns, column => string.Equals(column.DbColumnName, "JobId", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(columns, column => string.Equals(column.DbColumnName, "RequestedBy", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(columns, column => string.Equals(column.DbColumnName, "RequestedAtUtc", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(columns, column => string.Equals(column.DbColumnName, "StartedAtUtc", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(columns, column => string.Equals(column.DbColumnName, "CompletedAtUtc", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task BatchUpdateSupplierImages_兼容入口使用Pos商品管理权限并委托Job服务()
    {
        var authorize = typeof(ReactProductController)
            .GetMethod(nameof(ReactProductController.BatchUpdateSupplierImages))!
            .GetCustomAttribute<AuthorizeAttribute>();
        Assert.Equal(Permissions.PosProducts.Manage, authorize?.Policy);

        var productService = new Mock<IProductReactService>(MockBehavior.Strict);
        var jobService = new Mock<IProductSupplierImageBatchUpdateJobService>(MockBehavior.Strict);
        var currentUserService = new Mock<ICurrentUserService>(MockBehavior.Strict);
        currentUserService.Setup(service => service.GetCurrentUserGuid()).Returns("supplier-user-guid");
        currentUserService.Setup(service => service.GetCurrentUsername()).Returns("供应商操作员");
        jobService
            .Setup(item => item.StartJobAsync(
                It.Is<BatchUpdateSupplierImagesJobRequest>(request =>
                    request.LocalSupplierCode == "DATS"
                    && request.UrlTemplate == "https://cdn.example.com/{itemNumber}.jpg"
                    && request.UpdateHbweb
                    && request.UpdateHq
                    && request.OperationId == null
                ),
                "supplier-user-guid",
                "供应商操作员",
                It.IsAny<CancellationToken>()
            ))
            .ReturnsAsync(new BatchUpdateSupplierImagesJobDto
            {
                JobId = "job-direct-compatible",
                OperationId = "supplier-images|DATS",
                Status = BatchUpdateSupplierImagesJobStatusConstants.Queued,
                CreatedAt = DateTime.UtcNow,
            });

        var controller = new ReactProductController(
            productService.Object,
            Mock.Of<IProductStoreSyncService>(),
            Mock.Of<IProductHqSyncService>(),
            Mock.Of<ICurrentUserManageableStoreScopeService>(),
            Mock.Of<ILogger<ReactProductController>>(),
            currentUserService.Object,
            supplierImageJobService: jobService.Object
        );

        var response = await controller.BatchUpdateSupplierImages(new BatchUpdateSupplierImagesRequest
        {
            LocalSupplierCode = "DATS",
            UrlTemplate = "https://cdn.example.com/{itemNumber}.jpg",
            UpdateHbweb = true,
            UpdateHq = true,
        });

        Assert.IsType<OkObjectResult>(response);
        jobService.VerifyAll();
        productService.Verify(
            item => item.BatchUpdateSupplierImagesAsync(It.IsAny<BatchUpdateSupplierImagesRequest>()),
            Times.Never
        );
    }

    [Fact]
    public async Task BatchUpdateSupplierImagesJobService_创建任务后立即返回JobId并可查询完成结果()
    {
        await SeedSupplierAsync("DATS", null);
        await SeedProductAsync("P001", "DATS", "72653");
        await SeedProductAsync("P002", "DATS", "72891");
        await _hqDb.Insertable(new[]
        {
            CreateHqProduct(1, "P001", ""),
            CreateHqProduct(2, "P002", " "),
        }).ExecuteCommandAsync();

        using var provider = CreateSupplierImageJobServiceProvider();
        var jobService = new ProductSupplierImageBatchUpdateJobService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ProductSupplierImageBatchUpdateJobService>.Instance
        );

        var started = await jobService.StartJobAsync(new BatchUpdateSupplierImagesJobRequest
        {
            OperationId = "supplier-images-dats",
            LocalSupplierCode = "DATS",
            UrlTemplate = "https://cdn.example.com/{itemNumber}.jpg",
            UpdateHbweb = true,
            UpdateHq = true,
            SaveSupplierImageBaseUrl = true,
        });

        Assert.Equal(BatchUpdateSupplierImagesJobStatusConstants.Queued, started.Status);
        Assert.False(string.IsNullOrWhiteSpace(started.JobId));
        Assert.Equal("supplier-images-dats", started.OperationId);
        Assert.NotNull(started.Request);

        var completed = await WaitForSupplierImageJobAsync(jobService, started.JobId);

        Assert.Equal(BatchUpdateSupplierImagesJobStatusConstants.Succeeded, completed.Status);
        Assert.NotNull(completed.StartedAt);
        Assert.NotNull(completed.CompletedAt);
        Assert.Equal(2, completed.Result?.HbwebUpdatedCount);
        Assert.Equal(2, completed.Result?.HqUpdatedCount);
        Assert.Equal(0, completed.Result?.SkippedCount);
    }

    [Fact]
    public async Task BatchUpdateSupplierImagesJobService_保存入队用户并传递给商品服务()
    {
        var productService = new Mock<IProductReactService>(MockBehavior.Strict);
        productService
            .Setup(service => service.BatchUpdateSupplierImagesAsync(
                It.IsAny<BatchUpdateSupplierImagesRequest>(),
                "user-guid-2",
                "操作员乙"
            ))
            .ReturnsAsync(ApiResponse<BatchUpdateSupplierImagesResult>.OK(
                new BatchUpdateSupplierImagesResult { HbwebUpdatedCount = 1 },
                "完成"
            ));

        using var provider = CreateSupplierImageJobServiceProvider(productService.Object);
        var jobService = new ProductSupplierImageBatchUpdateJobService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ProductSupplierImageBatchUpdateJobService>.Instance
        );

        var started = await jobService.StartJobAsync(
            new BatchUpdateSupplierImagesJobRequest
            {
                OperationId = "supplier-images-user",
                LocalSupplierCode = "DATS",
                UrlTemplate = "https://cdn.example.com/{itemNumber}.jpg",
                UpdateHbweb = true,
            },
            "user-guid-2",
            "操作员乙"
        );

        var completed = await WaitForSupplierImageJobAsync(jobService, started.JobId);

        Assert.Equal(BatchUpdateSupplierImagesJobStatusConstants.Succeeded, completed.Status);
        productService.Verify(service => service.BatchUpdateSupplierImagesAsync(
            It.IsAny<BatchUpdateSupplierImagesRequest>(),
            "user-guid-2",
            "操作员乙"
        ), Times.Once);
    }

    [Fact]
    public async Task BatchUpdateSupplierImagesJobService_相同OperationId运行中时复用同一个任务()
    {
        var release = new TaskCompletionSource<ApiResponse<BatchUpdateSupplierImagesResult>>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var productService = new Mock<IProductReactService>();
        productService
            .Setup(service => service.BatchUpdateSupplierImagesAsync(It.IsAny<BatchUpdateSupplierImagesRequest>()))
            .Returns(release.Task);

        using var provider = CreateSupplierImageJobServiceProvider(productService.Object);
        var jobService = new ProductSupplierImageBatchUpdateJobService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ProductSupplierImageBatchUpdateJobService>.Instance
        );

        var first = await jobService.StartJobAsync(new BatchUpdateSupplierImagesJobRequest
        {
            OperationId = "supplier-images-dats",
            LocalSupplierCode = "DATS",
            UrlTemplate = "https://cdn.example.com/{itemNumber}.jpg",
            UpdateHbweb = true,
        });
        var duplicate = await jobService.StartJobAsync(new BatchUpdateSupplierImagesJobRequest
        {
            OperationId = "supplier-images-dats",
            LocalSupplierCode = "DATS",
            UrlTemplate = "https://cdn.example.com/{itemNumber}.jpg",
            UpdateHbweb = true,
        });

        Assert.Equal(first.JobId, duplicate.JobId);
        Assert.True(duplicate.IsDuplicateRequest);

        release.SetResult(ApiResponse<BatchUpdateSupplierImagesResult>.OK(
            new BatchUpdateSupplierImagesResult { HbwebUpdatedCount = 1 },
            "ok"
        ));

        await WaitForSupplierImageJobAsync(jobService, first.JobId);
        productService.Verify(
            service => service.BatchUpdateSupplierImagesAsync(It.IsAny<BatchUpdateSupplierImagesRequest>()),
            Times.Once
        );
    }

    [Fact]
    public async Task BatchUpdateSupplierImagesJobService_同一供应商不同模板运行中时复用同一个任务()
    {
        var release = new TaskCompletionSource<ApiResponse<BatchUpdateSupplierImagesResult>>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var productService = new Mock<IProductReactService>();
        productService
            .Setup(service => service.BatchUpdateSupplierImagesAsync(It.IsAny<BatchUpdateSupplierImagesRequest>()))
            .Returns(release.Task);

        using var provider = CreateSupplierImageJobServiceProvider(productService.Object);
        var jobService = new ProductSupplierImageBatchUpdateJobService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ProductSupplierImageBatchUpdateJobService>.Instance
        );

        var first = await jobService.StartJobAsync(new BatchUpdateSupplierImagesJobRequest
        {
            OperationId = "supplier-images-dats-template-1",
            LocalSupplierCode = "DATS",
            UrlTemplate = "https://cdn1.example.com/{itemNumber}.jpg",
            UpdateHbweb = true,
        });
        var duplicate = await jobService.StartJobAsync(new BatchUpdateSupplierImagesJobRequest
        {
            OperationId = "supplier-images-dats-template-2",
            LocalSupplierCode = "DATS",
            UrlTemplate = "https://cdn2.example.com/{itemNumber}.jpg",
            UpdateHbweb = true,
            UpdateHq = true,
        });

        Assert.Equal(first.JobId, duplicate.JobId);
        Assert.True(duplicate.IsDuplicateRequest);

        release.SetResult(ApiResponse<BatchUpdateSupplierImagesResult>.OK(
            new BatchUpdateSupplierImagesResult { HbwebUpdatedCount = 1 },
            "ok"
        ));

        await WaitForSupplierImageJobAsync(jobService, first.JobId);
        productService.Verify(
            service => service.BatchUpdateSupplierImagesAsync(It.IsAny<BatchUpdateSupplierImagesRequest>()),
            Times.Once
        );
    }

    [Fact]
    public async Task BatchUpdateSupplierImagesJobService_不同供应商运行中时分别创建任务并各自调用服务()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedRequests = new List<string>();
        var productService = new Mock<IProductReactService>();
        productService
            .Setup(service => service.BatchUpdateSupplierImagesAsync(It.IsAny<BatchUpdateSupplierImagesRequest>()))
            .Returns<BatchUpdateSupplierImagesRequest>(async request =>
            {
                lock (startedRequests)
                {
                    startedRequests.Add(request.LocalSupplierCode ?? string.Empty);
                }

                await release.Task;
                return ApiResponse<BatchUpdateSupplierImagesResult>.OK(
                    new BatchUpdateSupplierImagesResult { HbwebUpdatedCount = 1 },
                    "ok"
                );
            });

        using var provider = CreateSupplierImageJobServiceProvider(productService.Object);
        var jobService = new ProductSupplierImageBatchUpdateJobService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ProductSupplierImageBatchUpdateJobService>.Instance
        );

        var first = await jobService.StartJobAsync(new BatchUpdateSupplierImagesJobRequest
        {
            OperationId = "supplier-images-dats",
            LocalSupplierCode = "DATS",
            UrlTemplate = "https://cdn.example.com/{itemNumber}.jpg",
            UpdateHbweb = true,
        });
        var second = await jobService.StartJobAsync(new BatchUpdateSupplierImagesJobRequest
        {
            OperationId = "supplier-images-abcd",
            LocalSupplierCode = "ABCD",
            UrlTemplate = "https://cdn.example.com/{itemNumber}.jpg",
            UpdateHbweb = true,
        });

        Assert.NotEqual(first.JobId, second.JobId);
        Assert.False(second.IsDuplicateRequest);

        await WaitUntilAsync(() =>
        {
            lock (startedRequests)
            {
                return startedRequests.Count == 2;
            }
        });

        release.SetResult(true);

        await WaitForSupplierImageJobAsync(jobService, first.JobId);
        await WaitForSupplierImageJobAsync(jobService, second.JobId);

        productService.Verify(
            service => service.BatchUpdateSupplierImagesAsync(It.IsAny<BatchUpdateSupplierImagesRequest>()),
            Times.Exactly(2)
        );
    }

    [Fact]
    public async Task BatchUpdateSupplierImagesJob_控制器使用Pos商品管理权限并委托任务服务()
    {
        var startAuthorize = typeof(ReactProductController)
            .GetMethod(nameof(ReactProductController.StartBatchUpdateSupplierImagesJob))!
            .GetCustomAttribute<AuthorizeAttribute>();
        Assert.Equal(Permissions.PosProducts.Manage, startAuthorize?.Policy);

        var jobService = new Mock<IProductSupplierImageBatchUpdateJobService>(MockBehavior.Strict);
        var currentUserService = new Mock<ICurrentUserService>(MockBehavior.Strict);
        currentUserService.Setup(service => service.GetCurrentUserGuid()).Returns("supplier-user-guid");
        currentUserService.Setup(service => service.GetCurrentUsername()).Returns("供应商操作员");
        jobService
            .Setup(item => item.StartJobAsync(
                It.Is<BatchUpdateSupplierImagesJobRequest>(request =>
                    request.OperationId == "supplier-images-dats"
                    && request.LocalSupplierCode == "DATS"
                    && request.UpdateHbweb
                    && request.UpdateHq
                ),
                "supplier-user-guid",
                "供应商操作员",
                It.IsAny<CancellationToken>()
            ))
            .ReturnsAsync(new BatchUpdateSupplierImagesJobDto
            {
                JobId = "job-1",
                OperationId = "supplier-images-dats",
                Status = BatchUpdateSupplierImagesJobStatusConstants.Queued,
                CreatedAt = DateTime.UtcNow,
                Request = new BatchUpdateSupplierImagesRequest
                {
                    LocalSupplierCode = "DATS",
                    UrlTemplate = "https://cdn.example.com/{itemNumber}.jpg",
                    UpdateHbweb = true,
                    UpdateHq = true,
                },
            });
        jobService
            .Setup(item => item.GetJobAsync("job-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BatchUpdateSupplierImagesJobDto
            {
                JobId = "job-1",
                OperationId = "supplier-images-dats",
                Status = BatchUpdateSupplierImagesJobStatusConstants.Succeeded,
                CreatedAt = DateTime.UtcNow,
                StartedAt = DateTime.UtcNow,
                CompletedAt = DateTime.UtcNow,
                Result = new BatchUpdateSupplierImagesResult
                {
                    HbwebUpdatedCount = 2,
                    HqUpdatedCount = 1,
                    SkippedCount = 1,
                },
            });

        var controller = new ReactProductController(
            Mock.Of<IProductReactService>(),
            Mock.Of<IProductStoreSyncService>(),
            Mock.Of<IProductHqSyncService>(),
            Mock.Of<ICurrentUserManageableStoreScopeService>(),
            Mock.Of<ILogger<ReactProductController>>(),
            currentUserService.Object,
            supplierImageJobService: jobService.Object
        );

        var startResponse = await controller.StartBatchUpdateSupplierImagesJob(
            new BatchUpdateSupplierImagesJobRequest
            {
                OperationId = "supplier-images-dats",
                LocalSupplierCode = "DATS",
                UrlTemplate = "https://cdn.example.com/{itemNumber}.jpg",
                UpdateHbweb = true,
                UpdateHq = true,
            },
            CancellationToken.None
        );

        Assert.IsType<OkObjectResult>(startResponse);

        var getResponse = await controller.GetBatchUpdateSupplierImagesJob("job-1", CancellationToken.None);

        Assert.IsType<OkObjectResult>(getResponse);
        jobService.VerifyAll();
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

    private ProductReactService CreateProductService()
    {
        return new ProductReactService(
            CreateSqlSugarContext(_localDb),
            CreateHqSqlSugarContext(_hqDb, CreateHqConfiguration(_hqConnection.ConnectionString)),
            _mapper,
            NullLogger<ProductReactService>.Instance,
            new HttpContextAccessor(),
            new ProductAuditNoopHistoryService(),
            new ProductAuditSystemCurrentUserService()
        );
    }

    private LocalSupplierReactService CreateSupplierService()
    {
        return new LocalSupplierReactService(
            CreateSqlSugarContext(_localDb),
            CreateHqSqlSugarContext(_hqDb, CreateHqConfiguration(_hqConnection.ConnectionString)),
            NullLogger<LocalSupplierReactService>.Instance
        );
    }

    private ServiceProvider CreateSupplierImageJobServiceProvider(
        IProductReactService? productService = null
    )
    {
        var services = new ServiceCollection();
        services.AddScoped<IProductReactService>(_ => productService ?? CreateProductService());
        return services.BuildServiceProvider();
    }

    private async Task SeedSupplierAsync(string supplierCode, string? imageBaseUrl)
    {
        await _localDb.Insertable(new HBLocalSupplier
        {
            Guid = $"supplier-{supplierCode}",
            LocalSupplierCode = supplierCode,
            Name = supplierCode,
            Status = 1,
            ImageBaseUrl = imageBaseUrl,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            IsDeleted = false,
        }).ExecuteCommandAsync();
    }

    private async Task SeedProductAsync(
        string productCode,
        string supplierCode,
        string? itemNumber,
        string? productImage = null,
        DateTime? updatedAt = null,
        string? updatedBy = null
    )
    {
        await _localDb.Insertable(new Product
        {
            UUID = $"product-{productCode}",
            ProductCode = productCode,
            LocalSupplierCode = supplierCode,
            ItemNumber = itemNumber,
            Barcode = $"barcode-{productCode}",
            ProductName = productCode,
            ProductImage = productImage,
            PurchasePrice = 1,
            RetailPrice = 2,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = updatedAt ?? DateTime.UtcNow,
            UpdatedBy = updatedBy,
            IsDeleted = false,
        }).ExecuteCommandAsync();
    }

    private static DIC_商品信息字典表 CreateHqProduct(
        int id,
        string productCode,
        string? image,
        DateTime? lastModifyDate = null,
        string? lastModifier = null
    )
    {
        return new DIC_商品信息字典表
        {
            ID = id,
            HGUID = $"hq-{productCode}",
            H商品标签GUID = "",
            H商品分类码GUID = "",
            H供货商编码 = "DATS",
            H商品编码 = productCode,
            H货号 = productCode,
            H主条形码 = "",
            H商品名称 = productCode,
            H商品类型 = 0,
            H大写名称 = productCode,
            H规格 = "",
            H单位 = "",
            H进货价 = 0,
            H零售价 = 0,
            H是否自动定价 = false,
            H商品图片 = image,
            中包数量 = 0,
            H腾讯云图地址 = "",
            H使用状态 = true,
            H是否特殊商品 = false,
            H进货单主表GUID = "",
            H进货单详情GUID = "",
            CBP商品中文名称 = "",
            CBP供应商编码 = "",
            CBP商品分类码GUID = "",
            FGC_Creator = "test",
            FGC_CreateDate = DateTime.UtcNow,
            FGC_LastModifier = lastModifier ?? "test",
            FGC_LastModifyDate = lastModifyDate ?? DateTime.UtcNow,
            FGC_UpdateHelp = "",
        };
    }

    private static ConnectionConfig CreateConnectionConfig(string connectionString)
    {
        return new ConnectionConfig
        {
            ConnectionString = connectionString,
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = true,
            InitKeyType = InitKeyType.Attribute,
            MoreSettings = new ConnMoreSettings
            {
                IsAutoRemoveDataCache = true,
            },
        };
    }

    private static IMapper CreateMapper()
    {
        var config = new MapperConfiguration(
            cfg => cfg.AddProfile<ReactProductMappingProfile>(),
            NullLoggerFactory.Instance
        );
        return config.CreateMapper();
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
        typeof(SqlSugarContext)
            .GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(context, db);
        return context;
    }

    private static HqSqlSugarContext CreateHqSqlSugarContext(ISqlSugarClient db, IConfiguration configuration)
    {
        var context = (HqSqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(HqSqlSugarContext));
        typeof(HqSqlSugarContext)
            .GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(context, db);
        typeof(HqSqlSugarContext)
            .GetField("<Configuration>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(context, configuration);
        return context;
    }

    private static async Task<BatchUpdateSupplierImagesJobDto> WaitForSupplierImageJobAsync(
        IProductSupplierImageBatchUpdateJobService jobService,
        string jobId
    )
    {
        var job = await WaitForValueAsync(
            () => jobService.GetJobAsync(jobId),
            current => current?.Status is BatchUpdateSupplierImagesJobStatusConstants.Succeeded or BatchUpdateSupplierImagesJobStatusConstants.Failed,
            describeLast: current => $"供应商商品图片批量更新 job 当前状态：{current?.Status ?? "未找到"}"
        );
        return job!;
    }
}
