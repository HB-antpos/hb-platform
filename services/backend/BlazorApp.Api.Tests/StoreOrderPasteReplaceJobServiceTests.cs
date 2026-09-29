using System.Reflection;
using System.Runtime.CompilerServices;
using AutoMapper;
using BlazorApp.Api.Data;
using BlazorApp.Api.Features.StoreOrders;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class StoreOrderPasteReplaceJobServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnection _sqliteConnection;
    private readonly SqlSugarClient _db;
    private readonly ServiceProvider _serviceProvider;

    public StoreOrderPasteReplaceJobServiceTests()
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
            typeof(Product),
            typeof(WarehouseProduct),
            typeof(WareHouseOrder),
            typeof(WareHouseOrderDetails),
            typeof(Store)
        );

        var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(SqlSugarContext));
        var dbField = typeof(SqlSugarContext).GetField(
            "_db",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        dbField!.SetValue(context, _db);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(context);
        services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();
        services.AddStoreOrderFeatures();
        services.AddSingleton<IStoreOrderPasteReplaceJobService, StoreOrderPasteReplaceJobService>();
        _serviceProvider = services.BuildServiceProvider();
    }

    [Fact]
    public async Task StartJobAsync_RunsPasteReplaceInBackgroundAndReportsCounts()
    {
        await SeedStoreOrderAsync("ORDER-JOB");
        await SeedProductAsync("P-REPLACE", "ITEM-REPLACE");
        await SeedWarehouseProductAsync("P-REPLACE");
        await SeedOrderDetailAsync("ORDER-JOB", "P-REPLACE", quantity: 4m, allocQuantity: 2m);
        await SeedProductAsync("P-SKIP", "ITEM-SKIP");
        await SeedWarehouseProductAsync("P-SKIP");
        await SeedOrderDetailAsync("ORDER-JOB", "P-SKIP", quantity: 8m, allocQuantity: 3m);
        await SeedProductAsync("P-ZERO", "ITEM-ZERO");
        await SeedWarehouseProductAsync("P-ZERO");
        await SeedOrderDetailAsync("ORDER-JOB", "P-ZERO", quantity: 9m, allocQuantity: 4m);
        await SeedProductAsync("P-NEW", "ITEM-NEW");
        await SeedWarehouseProductAsync("P-NEW");

        var jobService = _serviceProvider.GetRequiredService<IStoreOrderPasteReplaceJobService>();
        var started = await jobService.StartJobAsync(new PasteReplaceOrderLinesDto
        {
            OrderGUID = "ORDER-JOB",
            TargetField = StoreOrderPasteTargetFields.Quantity,
            Items = new List<ProductQuantityDto>
            {
                new()
                {
                    ProductCode = "P-REPLACE",
                    Quantity = 6m,
                    Action = StoreOrderPasteActions.Replace,
                },
                new()
                {
                    ProductCode = "P-SKIP",
                    Quantity = 5m,
                    Action = StoreOrderPasteActions.Skip,
                },
                new()
                {
                    ProductCode = "P-ZERO",
                    Quantity = 0m,
                    Action = StoreOrderPasteActions.Replace,
                },
                new()
                {
                    ProductCode = "P-NEW",
                    Quantity = -1m,
                    Action = StoreOrderPasteActions.Append,
                },
            },
        });

        Assert.False(string.IsNullOrWhiteSpace(started.JobId));
        Assert.Contains(
            started.Status,
            new[] { StoreOrderPasteReplaceJobStatusConstants.Queued, StoreOrderPasteReplaceJobStatusConstants.Running }
        );

        var completed = await WaitForTerminalJobAsync(jobService, started.JobId);

        Assert.Equal(StoreOrderPasteReplaceJobStatusConstants.Succeeded, completed.Status);
        Assert.Equal(4, completed.TotalCount);
        Assert.Equal(2, completed.ImportedCount);
        Assert.Equal(2, completed.SkippedCount);

        var replaced = await _db.Queryable<WareHouseOrderDetails>()
            .Where(item => item.OrderGUID == "ORDER-JOB" && item.ProductCode == "P-REPLACE")
            .FirstAsync();
        var skipped = await _db.Queryable<WareHouseOrderDetails>()
            .Where(item => item.OrderGUID == "ORDER-JOB" && item.ProductCode == "P-SKIP")
            .FirstAsync();
        var zeroed = await _db.Queryable<WareHouseOrderDetails>()
            .Where(item => item.OrderGUID == "ORDER-JOB" && item.ProductCode == "P-ZERO")
            .FirstAsync();
        var newLine = await _db.Queryable<WareHouseOrderDetails>()
            .Where(item => item.OrderGUID == "ORDER-JOB" && item.ProductCode == "P-NEW")
            .FirstAsync();

        Assert.Equal(6m, replaced.Quantity);
        Assert.Equal(8m, skipped.Quantity);
        Assert.Equal(0m, zeroed.Quantity);
        Assert.Null(newLine);
    }

    [Fact]
    public async Task GetJobAsync_ReturnsNullForMissingJob()
    {
        var jobService = _serviceProvider.GetRequiredService<IStoreOrderPasteReplaceJobService>();

        var result = await jobService.GetJobAsync("missing-job");

        Assert.Null(result);
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _db.Dispose();
        _sqliteConnection.Dispose();
        SqliteTempFileCleanup.DeleteIfExists(_dbPath);
    }

    private static async Task<StoreOrderPasteReplaceJobDto> WaitForTerminalJobAsync(
        IStoreOrderPasteReplaceJobService jobService,
        string jobId
    )
    {
        return await WaitForValueAsync(
            async () =>
            {
                var job = await jobService.GetJobAsync(jobId);
                Assert.NotNull(job);
                return job;
            },
            job =>
                job.Status == StoreOrderPasteReplaceJobStatusConstants.Succeeded
                || job.Status == StoreOrderPasteReplaceJobStatusConstants.Failed,
            describeLast: job => $"paste replace job 当前状态：{job.Status}"
        );
    }

    private async Task SeedProductAsync(string productCode, string itemNumber)
    {
        await _db.Insertable(new Product
        {
            UUID = $"{productCode}-uuid",
            ProductCode = productCode,
            ProductName = $"商品 {productCode}",
            ItemNumber = itemNumber,
            Barcode = $"{itemNumber}-BAR",
            IsActive = true,
            IsDeleted = false,
        }).ExecuteCommandAsync();
    }

    private async Task SeedWarehouseProductAsync(string productCode)
    {
        await _db.Insertable(new WarehouseProduct
        {
            ProductCode = productCode,
            OEMPrice = 10m,
            ImportPrice = 7m,
            StockQuantity = 20,
            MinOrderQuantity = 1,
            IsActive = true,
            IsDeleted = false,
        }).ExecuteCommandAsync();
    }

    private async Task SeedStoreOrderAsync(string orderGuid)
    {
        await _db.Insertable(new Store
        {
            StoreGUID = "STORE-GUID-001",
            StoreCode = "S001",
            StoreName = "测试门店",
            IsActive = true,
            IsDeleted = false,
        }).ExecuteCommandAsync();

        await _db.Insertable(new WareHouseOrder
        {
            OrderGUID = orderGuid,
            StoreCode = "S001",
            OrderNo = $"{orderGuid}-NO",
            OrderDate = new DateTime(2026, 6, 1),
            FlowStatus = 1,
            IsDeleted = false,
        }).ExecuteCommandAsync();
    }

    private async Task SeedOrderDetailAsync(
        string orderGuid,
        string productCode,
        decimal quantity,
        decimal allocQuantity
    )
    {
        await _db.Insertable(new WareHouseOrderDetails
        {
            DetailGUID = $"{orderGuid}-{productCode}",
            OrderGUID = orderGuid,
            StoreCode = "S001",
            ProductCode = productCode,
            Quantity = quantity,
            AllocQuantity = allocQuantity,
            OEMPrice = 10m,
            OEMAmount = 10m * quantity,
            ImportPrice = 7m,
            ImportAmount = 7m * quantity,
            IsDeleted = false,
        }).ExecuteCommandAsync();
    }
}
