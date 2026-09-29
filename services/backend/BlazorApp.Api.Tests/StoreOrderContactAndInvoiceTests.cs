using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Net.Sockets;
using AutoMapper;
using BlazorApp.Api.Data;
using BlazorApp.Api.Features.StoreOrders.Invoice;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using ClosedXML.Excel;
using iTextSharp.text.pdf;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SqlSugar;
using Xunit;
using BlazorApp.Shared.Models.HBweb;

namespace BlazorApp.Api.Tests;

public sealed class StoreOrderContactAndInvoiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnection _sqliteConnection;
    private readonly SqlSugarClient _db;

    public StoreOrderContactAndInvoiceTests()
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
            typeof(Store),
            typeof(WareHouseOrder),
            typeof(WareHouseOrderDetails),
            typeof(Product),
            typeof(WarehouseProduct),
            typeof(StoreRetailPrice),
            typeof(ProductSetCode),
            typeof(StoreMultiCodeProduct),
            typeof(DomesticProduct),
            typeof(ProductLocation),
            typeof(Location),
            typeof(ProductGrade),
            typeof(User),
            typeof(UserStore),
            typeof(InvoiceEmailConfiguration),
            typeof(StoreOrderInvoiceEmailSendRecord)
        );
    }

    [Fact]
    public async Task GetStoreByCodeAsync_ReturnsContactEmailFromStore()
    {
        await _db.Insertable(
            new Store
            {
                StoreGUID = "store-1",
                StoreCode = "S001",
                StoreName = "Test Store",
                Address = "1 Test Street",
                Phone = "123456",
                ContactEmail = "store@example.com",
                IsActive = true,
            }
        ).ExecuteCommandAsync();

        var service = CreateStoreService();

        var result = await service.GetStoreByCodeAsync("S001");

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal("store@example.com", result.Data!.ContactEmail);
    }

    [Fact]
    public async Task GetOrderDetailAsync_AndFull_ReturnStoreContactEmail()
    {
        await SeedStoreOrderGraphAsync();
        var service = CreateStoreOrderService();

        var detailResult = await service.GetOrderDetailAsync("order-1");
        var fullResult = await service.GetOrderDetailFullAsync("order-1");

        Assert.True(detailResult.Success);
        Assert.Equal("Test Store", detailResult.Data!.StoreName);
        Assert.Equal("1 Test Street", detailResult.Data!.StoreAddress);
        Assert.Equal("store@example.com", detailResult.Data.StoreContactEmail);
        Assert.True(fullResult.Success);
        Assert.Equal("Test Store", fullResult.Data!.StoreName);
        Assert.Equal("store@example.com", fullResult.Data!.StoreContactEmail);
    }

    [Fact]
    public async Task StoreOrderInvoiceDetailReader_PreservesOrderAndAllocatedImportAmounts()
    {
        await SeedStoreOrderGraphAsync();
        await _db.Updateable<WareHouseOrderDetails>()
            .SetColumns(item => item.ImportAmount == 17.5m)
            .Where(item => item.DetailGUID == "detail-1")
            .ExecuteCommandAsync();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(CreateSqlSugarContext(_db));
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor());
        services.AddStoreOrderInvoiceSlice();
        using var provider = services.BuildServiceProvider();

        var result = await provider
            .GetRequiredService<IStoreOrderInvoiceDetailReader>()
            .GetInvoiceDetailAsync("order-1");

        Assert.True(result.Success, result.Message);
        var order = Assert.IsType<StoreOrderCartDto>(result.Data);
        Assert.Equal(17.5m, order.TotalImportAmount);
        Assert.Equal(12m, order.TotalAllocatedImportAmount);
        var item = Assert.Single(order.Items);
        Assert.Equal(17.5m, item.ImportAmount);
        Assert.Equal(12m, item.AllocatedImportAmount);
    }

    [Fact]
    public async Task GetOrderDetailAsync_ReturnsLatestInvoiceEmailSentInfo()
    {
        await SeedStoreOrderGraphAsync();
        await _db.Insertable(
            new[]
            {
                new StoreOrderInvoiceEmailSendRecord
                {
                    Id = "record-1",
                    StoreOrderUuid = "order-1",
                    ToEmail = "old@example.com",
                    SentAtUtc = new DateTime(2026, 6, 5, 1, 0, 0, DateTimeKind.Utc),
                    JobId = "job-old",
                    CreatedAtUtc = new DateTime(2026, 6, 5, 1, 0, 0, DateTimeKind.Utc),
                },
                new StoreOrderInvoiceEmailSendRecord
                {
                    Id = "record-2",
                    StoreOrderUuid = "order-1",
                    ToEmail = "latest@example.com",
                    SentAtUtc = new DateTime(2026, 6, 6, 3, 30, 0, DateTimeKind.Utc),
                    JobId = "job-latest",
                    CreatedAtUtc = new DateTime(2026, 6, 6, 3, 31, 0, DateTimeKind.Utc),
                },
            }
        ).ExecuteCommandAsync();
        var service = CreateStoreOrderService();

        var detailResult = await service.GetOrderDetailAsync("order-1");
        var fullResult = await service.GetOrderDetailFullAsync("order-1");

        Assert.True(detailResult.Success);
        Assert.True(detailResult.Data!.InvoiceEmailSentInfo.HasSent);
        Assert.Equal(new DateTime(2026, 6, 6, 3, 30, 0, DateTimeKind.Utc), detailResult.Data.InvoiceEmailSentInfo.SentAt);
        Assert.Equal(DateTimeKind.Utc, detailResult.Data.InvoiceEmailSentInfo.SentAt!.Value.Kind);
        Assert.Equal("latest@example.com", detailResult.Data.InvoiceEmailSentInfo.ToEmail);
        Assert.Equal("job-latest", detailResult.Data.InvoiceEmailSentInfo.JobId);
        Assert.True(fullResult.Success);
        Assert.True(fullResult.Data!.InvoiceEmailSentInfo.HasSent);
        Assert.Equal("latest@example.com", fullResult.Data.InvoiceEmailSentInfo.ToEmail);
    }

    [Fact]
    public async Task UpdateStoreContactAsync_UpdatesStoreAddressAndEmail()
    {
        await SeedStoreOrderGraphAsync();
        var service = CreateStoreOrderService("tester");

        var result = await service.UpdateStoreContactAsync(
            new UpdateStoreOrderStoreContactDto
            {
                OrderGUID = "order-1",
                StoreCode = "S001",
                Address = "99 Updated Road",
                ContactEmail = "updated@example.com",
            }
        );

        var store = await _db.Queryable<Store>().Where(x => x.StoreGUID == "store-1").FirstAsync();

        Assert.True(result.Success);
        Assert.Equal("99 Updated Road", store!.Address);
        Assert.Equal("updated@example.com", store.ContactEmail);
        Assert.Equal("tester", store.UpdatedBy);
        Assert.NotNull(store.UpdatedAt);
    }

    [Fact]
    public async Task UpdateStoreContactAsync_WhenAddressOmitted_PreservesExistingAddress()
    {
        await SeedStoreOrderGraphAsync();
        var service = CreateStoreOrderService("tester");

        var result = await service.UpdateStoreContactAsync(
            new UpdateStoreOrderStoreContactDto
            {
                OrderGUID = "order-1",
                StoreCode = "S001",
                ContactEmail = "email-only@example.com",
            }
        );

        var store = await _db.Queryable<Store>().Where(x => x.StoreGUID == "store-1").FirstAsync();

        Assert.True(result.Success);
        Assert.Equal("1 Test Street", store!.Address);
        Assert.Equal("email-only@example.com", store.ContactEmail);
    }

    [Fact]
    public async Task UpdateOrderLineAsync_WhenSyncImportPriceFalse_OnlyUpdatesOrderDetailPrice()
    {
        await SeedStoreOrderGraphAsync();
        await SeedStoreRetailPriceAsync("S001", "P001", purchasePrice: 3m, retailPrice: 9.99m);
        var service = CreateStoreOrderService("tester");

        var result = await service.UpdateOrderLineAsync(
            new UpdateOrderLineDto
            {
                OrderGUID = "order-1",
                ProductCode = "P001",
                Quantity = 4m,
                ImportPrice = 4.44m,
                SyncImportPrice = false,
            }
        );

        var detail = await _db.Queryable<WareHouseOrderDetails>().SingleAsync(x => x.DetailGUID == "detail-1");
        var product = await _db.Queryable<Product>().SingleAsync(x => x.ProductCode == "P001");
        var warehouseProduct = await _db.Queryable<WarehouseProduct>().SingleAsync(x => x.ProductCode == "P001");
        var storePrice = await _db.Queryable<StoreRetailPrice>().SingleAsync(x => x.StoreCode == "S001" && x.ProductCode == "P001");

        Assert.True(result.Success);
        Assert.Equal(4.44m, detail.ImportPrice);
        // ImportAmount 按订货数量计算，发票金额走 AllocatedImportAmount。
        Assert.Equal(22.2m, detail.ImportAmount);
        Assert.Equal(3m, product.PurchasePrice);
        Assert.Equal(3m, warehouseProduct.ImportPrice);
        Assert.Equal(3m, storePrice.PurchasePrice);
        Assert.Equal(9.99m, storePrice.StoreRetailPriceValue);
    }

    [Fact]
    public async Task UpdateOrderLineAsync_WhenSyncImportPriceTrue_UpdatesProductWarehouseAndStorePurchasePrices()
    {
        await SeedStoreOrderGraphAsync();
        await SeedStoreAsync("store-2", "S002", "Second Store");
        await SeedStoreRetailPriceAsync("S001", "P001", purchasePrice: 3m, retailPrice: 9.99m);
        var service = CreateStoreOrderService("tester");

        var result = await service.UpdateOrderLineAsync(
            new UpdateOrderLineDto
            {
                OrderGUID = "order-1",
                ProductCode = "P001",
                Quantity = 4m,
                ImportPrice = 5.55m,
                SyncImportPrice = true,
            }
        );

        var detail = await _db.Queryable<WareHouseOrderDetails>().SingleAsync(x => x.DetailGUID == "detail-1");
        var product = await _db.Queryable<Product>().SingleAsync(x => x.ProductCode == "P001");
        var warehouseProduct = await _db.Queryable<WarehouseProduct>().SingleAsync(x => x.ProductCode == "P001");
        var storePrices = await _db.Queryable<StoreRetailPrice>()
            .Where(x => x.ProductCode == "P001" && !x.IsDeleted)
            .OrderBy(x => x.StoreCode)
            .ToListAsync();

        Assert.True(result.Success);
        Assert.Equal(5.55m, detail.ImportPrice);
        Assert.Equal(27.75m, detail.ImportAmount);
        Assert.Equal(5.55m, product.PurchasePrice);
        Assert.Equal(5.55m, warehouseProduct.ImportPrice);
        Assert.Collection(
            storePrices,
            s001 =>
            {
                Assert.Equal("S001", s001.StoreCode);
                Assert.Equal(5.55m, s001.PurchasePrice);
                Assert.Equal(9.99m, s001.StoreRetailPriceValue);
                Assert.Equal("tester", s001.UpdatedBy);
            },
            s002 =>
            {
                Assert.Equal("S002", s002.StoreCode);
                Assert.Equal("S002P001", s002.StoreProductCode);
                Assert.Equal(5.55m, s002.PurchasePrice);
                Assert.Equal(9.99m, s002.StoreRetailPriceValue);
                Assert.Equal("tester", s002.CreatedBy);
                Assert.Equal("tester", s002.UpdatedBy);
            }
        );
    }

    [Fact]
    public async Task UpdateOrderLineAsync_WhenSyncImportPriceTrue_RecalculatesGlobalAndActualStoreSetChildCosts()
    {
        await SeedStoreOrderGraphAsync();
        await SeedStoreRetailPriceAsync("S001", "P001", purchasePrice: 3m, retailPrice: 9.99m);
        await SeedSetChildPurchasePriceRowsAsync("P001", "S001");
        var service = CreateStoreOrderService("tester");

        var result = await service.UpdateOrderLineAsync(
            new UpdateOrderLineDto
            {
                OrderGUID = "order-1",
                ProductCode = "P001",
                Quantity = 4m,
                ImportPrice = 5.55m,
                SyncImportPrice = true,
            }
        );

        Assert.True(result.Success, result.Message);
        var globalCosts = await _db.Queryable<ProductSetCode>()
            .Where(item => item.ProductCode == "P001")
            .OrderBy(item => item.SetProductCode)
            .Select(item => item.SetPurchasePrice)
            .ToListAsync();
        var storeCosts = await _db.Queryable<StoreMultiCodeProduct>()
            .Where(item => item.StoreCode == "S001" && item.ProductCode == "P001")
            .OrderBy(item => item.MultiCodeProductCode)
            .Select(item => item.PurchasePrice)
            .ToListAsync();

        Assert.Equal(new decimal?[] { 2.22m, 3.33m }, globalCosts);
        Assert.Equal(new decimal?[] { 2.22m, 3.33m }, storeCosts);
    }

    [Fact]
    public async Task BatchUpdateOrderLineAsync_WhenSyncImportPriceTrue_RecalculatesSetChildCostsOnceWithinTheTransaction()
    {
        await SeedStoreOrderGraphAsync();
        await SeedStoreRetailPriceAsync("S001", "P001", purchasePrice: 3m, retailPrice: 9.99m);
        await SeedSetChildPurchasePriceRowsAsync("P001", "S001");
        var service = CreateStoreOrderService("tester");

        var result = await service.BatchUpdateOrderLineAsync(
            new BatchUpdateOrderLineDto
            {
                OrderGUID = "order-1",
                Items =
                {
                    new BatchUpdateItemDto
                    {
                        ProductCode = "P001",
                        ImportPrice = 6m,
                        SyncImportPrice = true,
                    },
                },
            }
        );

        Assert.True(result.Success, result.Message);
        var globalCosts = await _db.Queryable<ProductSetCode>()
            .Where(item => item.ProductCode == "P001")
            .OrderBy(item => item.SetProductCode)
            .Select(item => item.SetPurchasePrice)
            .ToListAsync();
        var storeCosts = await _db.Queryable<StoreMultiCodeProduct>()
            .Where(item => item.StoreCode == "S001" && item.ProductCode == "P001")
            .OrderBy(item => item.MultiCodeProductCode)
            .Select(item => item.PurchasePrice)
            .ToListAsync();

        Assert.Equal(new decimal?[] { 2.4m, 3.6m }, globalCosts);
        Assert.Equal(new decimal?[] { 2.4m, 3.6m }, storeCosts);
    }

    [Fact]
    public async Task BatchUpdateOrderLineAsync_WhenOnlyImportPriceProvided_PreservesAllocQuantity()
    {
        await SeedStoreOrderGraphAsync();
        await SeedStoreRetailPriceAsync("S001", "P001", purchasePrice: 3m, retailPrice: 9.99m);
        var service = CreateStoreOrderService("tester");

        var result = await service.BatchUpdateOrderLineAsync(
            new BatchUpdateOrderLineDto
            {
                OrderGUID = "order-1",
                Items =
                {
                    new BatchUpdateItemDto
                    {
                        ProductCode = "P001",
                        ImportPrice = 6.66m,
                        SyncImportPrice = false,
                    },
                },
            }
        );

        var detail = await _db.Queryable<WareHouseOrderDetails>().SingleAsync(x => x.DetailGUID == "detail-1");
        var product = await _db.Queryable<Product>().SingleAsync(x => x.ProductCode == "P001");
        var warehouseProduct = await _db.Queryable<WarehouseProduct>().SingleAsync(x => x.ProductCode == "P001");

        Assert.True(result.Success);
        Assert.Equal(4m, detail.AllocQuantity);
        Assert.Equal(6.66m, detail.ImportPrice);
        Assert.Equal(33.3m, detail.ImportAmount);
        Assert.Equal(3m, product.PurchasePrice);
        Assert.Equal(3m, warehouseProduct.ImportPrice);
    }

    [Fact]
    public async Task BatchUpdateOrderLineAsync_WithDetailGuidQuantity_UpdatesDuplicateProductLines()
    {
        await SeedStoreOrderGraphAsync();
        await _db.Insertable(
            new WareHouseOrderDetails
            {
                DetailGUID = "detail-duplicate",
                OrderGUID = "order-1",
                StoreCode = "S001",
                ProductCode = "P001",
                Quantity = 9,
                AllocQuantity = 2,
                OEMPrice = 7m,
                OEMAmount = 14m,
                ImportPrice = 4m,
                ImportAmount = 8m,
                IsDeleted = false,
            }
        ).ExecuteCommandAsync();
        var service = CreateStoreOrderService("tester");

        var result = await service.BatchUpdateOrderLineAsync(
            new BatchUpdateOrderLineDto
            {
                OrderGUID = "order-1",
                Items =
                {
                    new BatchUpdateItemDto
                    {
                        DetailGUID = "detail-1",
                        ProductCode = "P001",
                        Quantity = 6m,
                    },
                    new BatchUpdateItemDto
                    {
                        DetailGUID = "detail-duplicate",
                        ProductCode = "P001",
                        Quantity = 3m,
                    },
                },
            }
        );

        var details = await _db.Queryable<WareHouseOrderDetails>()
            .Where(x => x.OrderGUID == "order-1")
            .OrderBy(x => x.DetailGUID)
            .ToListAsync();
        var order = await _db.Queryable<WareHouseOrder>().SingleAsync(x => x.OrderGUID == "order-1");

        Assert.True(result.Success);
        Assert.Collection(
            details,
            detail =>
            {
                Assert.Equal("detail-1", detail.DetailGUID);
                Assert.Equal(5m, detail.Quantity);
                Assert.Equal(6m, detail.AllocQuantity);
                Assert.Equal(30m, detail.OEMAmount);
                Assert.Equal(15m, detail.ImportAmount);
                Assert.Equal("tester", detail.UpdatedBy);
            },
            detail =>
            {
                Assert.Equal("detail-duplicate", detail.DetailGUID);
                Assert.Equal(9m, detail.Quantity);
                Assert.Equal(3m, detail.AllocQuantity);
                Assert.Equal(21m, detail.OEMAmount);
                Assert.Equal(36m, detail.ImportAmount);
                Assert.Equal("tester", detail.UpdatedBy);
            }
        );
        Assert.Equal(51m, order.OEMTotalAmount);
        Assert.Equal(51m, order.ImportTotalAmount);
    }

    [Fact]
    public async Task BatchUpdateOrderLineAsync_WithMissingDetailGuid_ReturnsFailureAndPreservesLine()
    {
        await SeedStoreOrderGraphAsync();
        var service = CreateStoreOrderService("tester");

        var result = await service.BatchUpdateOrderLineAsync(
            new BatchUpdateOrderLineDto
            {
                OrderGUID = "order-1",
                Items =
                {
                    new BatchUpdateItemDto
                    {
                        DetailGUID = "missing-detail",
                        ProductCode = "P001",
                        Quantity = 6m,
                    },
                },
            }
        );

        var detail = await _db.Queryable<WareHouseOrderDetails>().SingleAsync(x => x.DetailGUID == "detail-1");

        Assert.False(result.Success);
        Assert.Equal("Some order lines were not found", result.Message);
        Assert.Equal(4m, detail.AllocQuantity);
        Assert.Equal(12m, detail.ImportAmount);
    }

    [Fact]
    public async Task BatchUpdateOrderLineAsync_WithDetailGuidQuantityZero_SoftDeletesZeroOrderLine()
    {
        await SeedStoreOrderGraphAsync();
        await _db.Insertable(
            new WareHouseOrderDetails
            {
                DetailGUID = "detail-zero",
                OrderGUID = "order-1",
                StoreCode = "S001",
                ProductCode = "P001",
                Quantity = 0,
                AllocQuantity = 8,
                OEMPrice = 2m,
                OEMAmount = 16m,
                ImportPrice = 1.5m,
                ImportAmount = 12m,
                IsDeleted = false,
            }
        ).ExecuteCommandAsync();
        var service = CreateStoreOrderService("tester");

        var result = await service.BatchUpdateOrderLineAsync(
            new BatchUpdateOrderLineDto
            {
                OrderGUID = "order-1",
                Items =
                {
                    new BatchUpdateItemDto
                    {
                        DetailGUID = "detail-zero",
                        ProductCode = "P001",
                        Quantity = 0m,
                    },
                },
            }
        );

        var detail = await _db.Queryable<WareHouseOrderDetails>().SingleAsync(x => x.DetailGUID == "detail-zero");
        var order = await _db.Queryable<WareHouseOrder>().SingleAsync(x => x.OrderGUID == "order-1");

        Assert.True(result.Success);
        Assert.True(detail.IsDeleted);
        Assert.Equal(0m, detail.AllocQuantity);
        Assert.Equal("tester", detail.UpdatedBy);
        Assert.Equal(25m, order.OEMTotalAmount);
        Assert.Equal(12m, order.ImportTotalAmount);
    }

    [Fact]
    public async Task InvoiceEmailService_WhenSmtpNotConfigured_ReturnsClearFailure()
    {
        var service = new InvoiceEmailService(
            NullLogger<InvoiceEmailService>.Instance,
            CreateSettingsService(new InvoiceEmailOptions())
        );

        var result = await service.SendInvoiceAsync(
            new StoreOrderInvoiceEmailMessage
            {
                ToEmail = "customer@example.com",
                Subject = "Invoice SO001",
                Body = "Hello",
                Attachments =
                {
                    new StoreOrderInvoiceEmailAttachment
                    {
                        FileName = "Invoice_S001_SO001.pdf",
                        ContentType = "application/pdf",
                        Bytes = new byte[] { 1, 2, 3, 4 },
                    },
                    new StoreOrderInvoiceEmailAttachment
                    {
                        FileName = "Invoice_S001_SO001.xlsx",
                        ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                        Bytes = new byte[] { 5, 6, 7, 8 },
                    },
                },
            }
        );

        Assert.False(result.Success);
        Assert.Equal("未配置发票邮件 SMTP，请先完成 InvoiceEmail 配置", result.Message);
    }

    [Fact]
    public async Task StoreOrderInvoiceEmailJobService_StartsJobAndMarksSucceeded_AndWritesSendRecord()
    {
        var request = new SendStoreOrderInvoiceEmailDto
        {
            OrderGUID = "order-1",
            ToEmail = "customer@example.com",
        };
        var pdfAttachment = new StoreOrderInvoiceEmailAttachment
        {
            FileName = "Invoice_S001_SO001.pdf",
            ContentType = "application/pdf",
            Bytes = new byte[] { 1, 2, 3, 4 },
        };
        var excelAttachment = new StoreOrderInvoiceEmailAttachment
        {
            FileName = "Invoice_S001_SO001.xlsx",
            ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            Bytes = new byte[] { 5, 6, 7, 8 },
        };
        var attachmentService = new Mock<IStoreOrderInvoiceAttachmentService>(MockBehavior.Strict);
        attachmentService
            .Setup(item => item.GenerateAttachmentsAsync("order-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResponse<StoreOrderInvoiceAttachmentBundle>.OK(
                new StoreOrderInvoiceAttachmentBundle
                {
                    OrderGUID = "order-1",
                    OrderNo = "SO001",
                    StoreCode = "S001",
                    Attachments = new List<StoreOrderInvoiceEmailAttachment>
                    {
                        pdfAttachment,
                        excelAttachment,
                    },
                }
            ));
        StoreOrderInvoiceEmailMessage? capturedMessage = null;
        var invoiceEmailService = new Mock<IInvoiceEmailService>(MockBehavior.Strict);
        invoiceEmailService
            .Setup(item => item.SendInvoiceAsync(It.IsAny<StoreOrderInvoiceEmailMessage>()))
            .Callback<StoreOrderInvoiceEmailMessage>(message => capturedMessage = message)
            .ReturnsAsync(ApiResponse<bool>.OK(true, "发票邮件发送成功"));
        var jobService = CreateInvoiceEmailJobService(attachmentService, invoiceEmailService);

        var started = await jobService.StartJobAsync(request);
        var completed = await WaitForInvoiceEmailJobAsync(jobService, started.JobId);
        var sendRecords = await _db.Queryable<StoreOrderInvoiceEmailSendRecord>()
            .Where(x => x.StoreOrderUuid == "order-1")
            .ToListAsync();

        Assert.False(string.IsNullOrWhiteSpace(started.JobId));
        Assert.Equal(StoreOrderInvoiceEmailJobStatusConstants.Succeeded, completed.Status);
        Assert.Equal("发票邮件发送成功", completed.Message);
        Assert.Equal("order-1", completed.OrderGUID);
        Assert.Equal("customer@example.com", completed.ToEmail);
        Assert.NotNull(completed.CompletedAt);
        attachmentService.Verify(
            item => item.GenerateAttachmentsAsync("order-1", It.IsAny<CancellationToken>()),
            Times.Once
        );
        invoiceEmailService.Verify(
            item => item.SendInvoiceAsync(It.IsAny<StoreOrderInvoiceEmailMessage>()),
            Times.Once
        );
        Assert.NotNull(capturedMessage);
        Assert.Equal("customer@example.com", capturedMessage!.ToEmail);
        Assert.Collection(
            capturedMessage.Attachments,
            attachment => Assert.Equal("application/pdf", attachment.ContentType),
            attachment => Assert.Equal(
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                attachment.ContentType
            )
        );
        var sendRecord = Assert.Single(sendRecords);
        Assert.Equal("customer@example.com", sendRecord.ToEmail);
        Assert.Equal(started.JobId, sendRecord.JobId);
        Assert.Equal("order-1", sendRecord.StoreOrderUuid);
    }

    [Fact]
    public async Task StoreOrderInvoiceEmailJobService_WhenAttachmentGenerationFails_MarksFailedWithMessage()
    {
        var request = new SendStoreOrderInvoiceEmailDto
        {
            OrderGUID = "order-1",
            ToEmail = "customer@example.com",
        };
        var attachmentService = new Mock<IStoreOrderInvoiceAttachmentService>(MockBehavior.Strict);
        attachmentService
            .Setup(item => item.GenerateAttachmentsAsync("order-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResponse<StoreOrderInvoiceAttachmentBundle>.Error("生成发票附件失败"));
        var invoiceEmailService = new Mock<IInvoiceEmailService>(MockBehavior.Strict);
        var jobService = CreateInvoiceEmailJobService(attachmentService, invoiceEmailService);

        var started = await jobService.StartJobAsync(request);
        var completed = await WaitForInvoiceEmailJobAsync(jobService, started.JobId);

        Assert.Equal(StoreOrderInvoiceEmailJobStatusConstants.Failed, completed.Status);
        Assert.Equal("生成发票附件失败", completed.Message);
        Assert.NotNull(completed.CompletedAt);
        invoiceEmailService.Verify(
            item => item.SendInvoiceAsync(It.IsAny<StoreOrderInvoiceEmailMessage>()),
            Times.Never
        );
        Assert.Equal(
            0,
            await _db.Queryable<StoreOrderInvoiceEmailSendRecord>()
                .Where(x => x.StoreOrderUuid == "order-1")
                .CountAsync()
        );
    }

    [Fact]
    public async Task StoreOrderInvoiceEmailJobService_WhenSendFails_DoesNotWriteSendRecord()
    {
        var request = new SendStoreOrderInvoiceEmailDto
        {
            OrderGUID = "order-1",
            ToEmail = "customer@example.com",
        };
        var attachmentService = new Mock<IStoreOrderInvoiceAttachmentService>(MockBehavior.Strict);
        attachmentService
            .Setup(item => item.GenerateAttachmentsAsync("order-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResponse<StoreOrderInvoiceAttachmentBundle>.OK(
                new StoreOrderInvoiceAttachmentBundle
                {
                    OrderGUID = "order-1",
                    OrderNo = "SO001",
                    StoreCode = "S001",
                    Attachments =
                    {
                        new StoreOrderInvoiceEmailAttachment
                        {
                            FileName = "Invoice_S001_SO001.pdf",
                            ContentType = "application/pdf",
                            Bytes = new byte[] { 1, 2, 3, 4 },
                        },
                    },
                }
            ));
        var invoiceEmailService = new Mock<IInvoiceEmailService>(MockBehavior.Strict);
        invoiceEmailService
            .Setup(item => item.SendInvoiceAsync(It.IsAny<StoreOrderInvoiceEmailMessage>()))
            .ReturnsAsync(ApiResponse<bool>.Error("SMTP 发送失败"));
        var jobService = CreateInvoiceEmailJobService(attachmentService, invoiceEmailService);

        var started = await jobService.StartJobAsync(request);
        var completed = await WaitForInvoiceEmailJobAsync(jobService, started.JobId);

        Assert.Equal(StoreOrderInvoiceEmailJobStatusConstants.Failed, completed.Status);
        Assert.Equal("SMTP 发送失败", completed.Message);
        Assert.Equal(
            0,
            await _db.Queryable<StoreOrderInvoiceEmailSendRecord>()
                .Where(x => x.StoreOrderUuid == "order-1")
                .CountAsync()
        );
    }

    [Fact]
    public async Task StoreOrderInvoiceEmailJobService_WhenSendRecordWriteFails_KeepsJobSucceeded()
    {
        var request = new SendStoreOrderInvoiceEmailDto
        {
            OrderGUID = "order-1",
            ToEmail = "customer@example.com",
        };
        var attachmentService = new Mock<IStoreOrderInvoiceAttachmentService>(MockBehavior.Strict);
        attachmentService
            .Setup(item => item.GenerateAttachmentsAsync("order-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResponse<StoreOrderInvoiceAttachmentBundle>.OK(
                new StoreOrderInvoiceAttachmentBundle
                {
                    OrderGUID = "order-1",
                    OrderNo = "SO001",
                    StoreCode = "S001",
                    Attachments =
                    {
                        new StoreOrderInvoiceEmailAttachment
                        {
                            FileName = "Invoice_S001_SO001.pdf",
                            ContentType = "application/pdf",
                            Bytes = new byte[] { 1, 2, 3, 4 },
                        },
                    },
                }
            ));
        var invoiceEmailService = new Mock<IInvoiceEmailService>(MockBehavior.Strict);
        invoiceEmailService
            .Setup(item => item.SendInvoiceAsync(It.IsAny<StoreOrderInvoiceEmailMessage>()))
            .ReturnsAsync(ApiResponse<bool>.OK(true, "发票邮件发送成功"));
        var jobService = CreateInvoiceEmailJobService(
            attachmentService,
            invoiceEmailService,
            registerSqlSugarContext: false
        );

        var started = await jobService.StartJobAsync(request);
        var completed = await WaitForInvoiceEmailJobAsync(jobService, started.JobId);

        Assert.Equal(StoreOrderInvoiceEmailJobStatusConstants.Succeeded, completed.Status);
        Assert.Equal("发票邮件发送成功", completed.Message);
        Assert.Equal(
            0,
            await _db.Queryable<StoreOrderInvoiceEmailSendRecord>()
                .Where(x => x.StoreOrderUuid == "order-1")
                .CountAsync()
        );
    }

    [Fact]
    public async Task StoreOrderInvoiceAttachmentService_GeneratesPdfAndExcel()
    {
        var orderService = new Mock<IStoreOrderInvoiceDetailReader>(MockBehavior.Strict);
        orderService
            .Setup(item => item.GetInvoiceDetailAsync("order-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResponse<StoreOrderCartDto?>.OK(CreateInvoiceOrder()));
        var service = new StoreOrderInvoiceAttachmentService(
            orderService.Object,
            NullLogger<StoreOrderInvoiceAttachmentService>.Instance
        );

        var result = await service.GenerateAttachmentsAsync("order-1");

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal("order-1", result.Data!.OrderGUID);
        Assert.Equal("SO001", result.Data.OrderNo);
        Assert.Equal("S001", result.Data.StoreCode);
        Assert.Collection(
            result.Data.Attachments,
            pdf =>
            {
                Assert.EndsWith("_2026-06-05.pdf", pdf.FileName);
                Assert.Equal("application/pdf", pdf.ContentType);
                Assert.NotEmpty(pdf.Bytes);
                Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(pdf.Bytes.Take(4).ToArray()));
                var pdfText = ExtractPdfText(pdf.Bytes);
                Assert.Contains("WAREHOUSE ADDRESS", pdfText);
                Assert.Contains("3 Rogilla close Maryland", pdfText);
                Assert.Contains("A.B.N. 35 160 589 793", pdfText);
                Assert.Contains("WAREHOUSE EMAIL", pdfText);
                Assert.Contains("INVOICE NO. SO001", pdfText);
                Assert.Contains("INVOICE DATE: 2026/6/5", pdfText);
                Assert.Contains("RRP", pdfText);
                Assert.Contains("PAYMENT DETAIL: DIRECT DEBIT", pdfText);
                Assert.Contains("NAME:", pdfText);
                Assert.Contains("HOT BARGAIN INTERNATIONAL", pdfText);
                Assert.Contains("BSB:", pdfText);
                Assert.Contains("012-532", pdfText);
                Assert.Contains("ACCOUNT:", pdfText);
                Assert.Contains("208034605", pdfText);
                Assert.Contains("All products remain the property of Hot Bargain International Pty Ltd", pdfText);
                Assert.True(HasPdfImageXObject(pdf.Bytes), "发票邮件 PDF 应嵌入 HOT BARGAIN logo 图片");
            },
            excel =>
            {
                Assert.EndsWith("_2026-06-05.xlsx", excel.FileName);
                Assert.Equal(
                    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                    excel.ContentType
                );
                using var workbook = new XLWorkbook(new MemoryStream(excel.Bytes));
                var sheet = workbook.Worksheet("Invoice");
                Assert.Equal("INVOICE", sheet.Cell(1, 1).GetString());
                Assert.Equal("INVOICE NO. SO001", sheet.Cell(2, 1).GetString());
                Assert.Equal("INVOICE DATE: 2026/6/5", sheet.Cell(2, 5).GetString());
                Assert.Equal("CUSTOMER:", sheet.Cell(3, 1).GetString());
                Assert.Equal("Test Store", sheet.Cell(3, 2).GetString());
                Assert.Equal("CUSTOMER CONTACT:", sheet.Cell(4, 1).GetString());
                Assert.Equal("customer@example.com", sheet.Cell(4, 2).GetString());
                Assert.Equal("ADDRESS:", sheet.Cell(5, 1).GetString());
                Assert.Equal("1 Test Street", sheet.Cell(5, 2).GetString());
                Assert.Equal("Item No", sheet.Cell(7, 2).GetString());
                Assert.Equal("RRP", sheet.Cell(7, 6).GetString());
                Assert.Equal("HB001", sheet.Cell(8, 2).GetString());
                Assert.Equal(12.99m, sheet.Cell(8, 6).GetValue<decimal>());
                Assert.Equal(2m, sheet.Cell(8, 7).GetValue<decimal>());
                Assert.Equal(1m, sheet.Cell(8, 8).GetValue<decimal>());
                Assert.Equal(8.5m, sheet.Cell(8, 9).GetValue<decimal>());
            }
        );
    }

    [Fact]
    public async Task StoreOrderInvoiceAttachmentService_WhenOutboundDateMissing_UsesOrderDate()
    {
        var orderService = new Mock<IStoreOrderInvoiceDetailReader>(MockBehavior.Strict);
        orderService
            .Setup(item => item.GetInvoiceDetailAsync("order-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResponse<StoreOrderCartDto?>.OK(CreateInvoiceOrder(includeOutboundDate: false)));
        var service = new StoreOrderInvoiceAttachmentService(
            orderService.Object,
            NullLogger<StoreOrderInvoiceAttachmentService>.Instance
        );

        var result = await service.GenerateAttachmentsAsync("order-1");

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        var pdf = result.Data!.Attachments.Single(item => item.ContentType == "application/pdf");
        Assert.EndsWith("_2026-06-04.pdf", pdf.FileName);
        Assert.Contains("INVOICE DATE: 2026/6/4", ExtractPdfText(pdf.Bytes));

        var excel = result.Data.Attachments.Single(
            item => item.ContentType == "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
        );
        Assert.EndsWith("_2026-06-04.xlsx", excel.FileName);
        using var workbook = new XLWorkbook(new MemoryStream(excel.Bytes));
        Assert.Equal("INVOICE DATE: 2026/6/4", workbook.Worksheet("Invoice").Cell(2, 5).GetString());
    }

    [Fact]
    public async Task StoreOrderInvoiceAttachmentService_WhenProductNameContainsEmoji_GeneratesAttachments()
    {
        const string productName = "Middle Card Small Beetle 🐞 45 Pieces";
        var order = CreateInvoiceOrder();
        order.Items.Single().ProductName = productName;
        var orderService = new Mock<IStoreOrderInvoiceDetailReader>(MockBehavior.Strict);
        orderService
            .Setup(item => item.GetInvoiceDetailAsync("order-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResponse<StoreOrderCartDto?>.OK(order));
        Exception? loggedException = null;
        var logger = new Mock<ILogger<StoreOrderInvoiceAttachmentService>>();
        logger
            .Setup(item => item.Log(
                It.IsAny<LogLevel>(),
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((_, _) => true),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()
            ))
            .Callback(new InvocationAction(invocation =>
                loggedException = invocation.Arguments[3] as Exception
            ));
        var service = new StoreOrderInvoiceAttachmentService(orderService.Object, logger.Object);

        var result = await service.GenerateAttachmentsAsync("order-1");

        Assert.True(result.Success, loggedException?.ToString() ?? result.Message);
        Assert.NotNull(result.Data);
        var pdf = result.Data!.Attachments.Single(item => item.ContentType == "application/pdf");
        Assert.NotEmpty(pdf.Bytes);
        var excel = result.Data.Attachments.Single(
            item => item.ContentType == "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
        );
        using var workbook = new XLWorkbook(new MemoryStream(excel.Bytes));
        Assert.Equal(productName, workbook.Worksheet("Invoice").Cell(8, 3).GetString());
    }

    [Fact]
    public void InvoiceEmailService_BuildsMessageWithPdfAndExcelAttachments()
    {
        var service = new TestableInvoiceEmailService(
            CreateSettingsService(new InvoiceEmailOptions
            {
                Host = "smtp.example.com",
                Port = 465,
                FromEmail = "warehouse@example.com",
            })
        );

        var email = service.BuildMimeMessageForTest(
            new StoreOrderInvoiceEmailMessage
            {
                ToEmail = "customer@example.com",
                Subject = "invoice",
                Body = "body",
                Attachments = new List<StoreOrderInvoiceEmailAttachment>
                {
                    new()
                    {
                        FileName = "invoice.pdf",
                        ContentType = "application/pdf",
                        Bytes = new byte[] { 1, 2, 3 },
                    },
                    new()
                    {
                        FileName = "invoice.xlsx",
                        ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                        Bytes = new byte[] { 4, 5, 6 },
                    },
                },
            }
        );

        var multipart = Assert.IsType<MimeKit.Multipart>(email.Body);
        Assert.Equal(3, multipart.Count);
    }

    [Fact]
    public async Task StoreOrderInvoiceEmailTextTranslationService_TranslatesEditedEmailTextBothWays()
    {
        var translationService = new Mock<ITranslationService>(MockBehavior.Strict);
        translationService
            .Setup(item => item.TranslateAsync("您好，附件请查收。", "en"))
            .ReturnsAsync("Hello, please find the attachment.");
        translationService
            .Setup(item => item.TranslateAsync("Hello, please find the attachment.", "zh"))
            .ReturnsAsync("您好，请查收附件。");
        translationService
            .Setup(item => item.TranslateAsync("Hello, please find the attachment.", "en"))
            .ReturnsAsync("Hello, please find the attachment.");
        var service = new StoreOrderInvoiceEmailTextTranslationService(
            translationService.Object,
            NullLogger<StoreOrderInvoiceEmailTextTranslationService>.Instance
        );

        var toEnglish = await service.TranslateAsync(
            new StoreOrderInvoiceEmailTextTranslationRequestDto
            {
                TargetLanguage = "en",
                Subject = "您好，附件请查收。",
                Body = "Hello, please find the attachment.",
            }
        );
        var toChinese = await service.TranslateAsync(
            new StoreOrderInvoiceEmailTextTranslationRequestDto
            {
                TargetLanguage = "zh",
                Subject = "Hello, please find the attachment.",
            }
        );

        Assert.True(toEnglish.Success);
        Assert.Equal("Hello, please find the attachment.", toEnglish.Data!.Subject);
        Assert.Equal("Hello, please find the attachment.", toEnglish.Data.Body);
        Assert.True(toChinese.Success);
        Assert.Equal("您好，请查收附件。", toChinese.Data!.Subject);
    }

    [Fact]
    public async Task StoreOrderInvoiceEmailTextTranslationService_WhenProviderFails_ReturnsClearFailure()
    {
        var translationService = new Mock<ITranslationService>(MockBehavior.Strict);
        translationService
            .Setup(item => item.TranslateAsync("custom subject", "zh"))
            .ThrowsAsync(new InvalidOperationException("provider failed"));
        var service = new StoreOrderInvoiceEmailTextTranslationService(
            translationService.Object,
            NullLogger<StoreOrderInvoiceEmailTextTranslationService>.Instance
        );

        var result = await service.TranslateAsync(
            new StoreOrderInvoiceEmailTextTranslationRequestDto
            {
                TargetLanguage = "zh",
                Subject = "custom subject",
            }
        );

        Assert.False(result.Success);
        Assert.Equal("翻译邮件内容失败，请稍后重试", result.Message);
    }

    [Fact]
    public void InvoiceEmailOptions_DefaultsToCheckingCertificateRevocation()
    {
        var options = new InvoiceEmailOptions();

        Assert.True(options.CheckCertificateRevocation);
    }

    [Fact]
    public void InvoiceEmailService_CreateSmtpClient_AppliesRevocationOption()
    {
        var service = new TestableInvoiceEmailService(
            CreateSettingsService(new InvoiceEmailOptions
            {
                CheckCertificateRevocation = false,
            })
        );

        using var client = service.CreateConfiguredClientForTest();

        Assert.False(client.CheckCertificateRevocation);
    }

    [Fact]
    public async Task InvoiceEmailService_WhenTlsHandshakeFails_ReturnsClearFailure()
    {
        var service = new TlsFailingInvoiceEmailService(
            CreateSettingsService(new InvoiceEmailOptions
            {
                Host = "mail.hotbargain.com.au",
                Port = 465,
                UseSsl = true,
                FromEmail = "sean@hotbargain.com.au",
            })
        );

        var result = await service.SendInvoiceAsync(
            new StoreOrderInvoiceEmailMessage
            {
                ToEmail = "customer@example.com",
                Subject = "invoice",
                Body = "body",
                Attachments = new List<StoreOrderInvoiceEmailAttachment>
                {
                    new()
                    {
                        FileName = "invoice.pdf",
                        ContentType = "application/pdf",
                        Bytes = new byte[] { 1, 2, 3, 4 },
                    },
                },
            }
        );

        Assert.False(result.Success);
        Assert.Equal("INVOICE_EMAIL_TLS_HANDSHAKE_FAILED", result.ErrorCode);
        Assert.Equal(
            "发票邮件 TLS 握手失败，请检查 SMTP 证书或 InvoiceEmail.CheckCertificateRevocation 配置",
            result.Message
        );
    }

    [Fact]
    public async Task InvoiceEmailService_WhenSmtpConnectionRefused_ReturnsClearFailure()
    {
        var service = new ConnectionRefusedInvoiceEmailService(
            CreateSettingsService(new InvoiceEmailOptions
            {
                Host = "mail.hotbargain.com.au",
                Port = 25,
                UseSsl = true,
                FromEmail = "sean@hotbargain.com.au",
            })
        );

        var result = await service.SendInvoiceAsync(
            new StoreOrderInvoiceEmailMessage
            {
                ToEmail = "customer@example.com",
                Subject = "invoice",
                Body = "body",
                Attachments = new List<StoreOrderInvoiceEmailAttachment>
                {
                    new()
                    {
                        FileName = "invoice.pdf",
                        ContentType = "application/pdf",
                        Bytes = new byte[] { 1, 2, 3, 4 },
                    },
                },
            }
        );

        Assert.False(result.Success);
        Assert.Equal("INVOICE_EMAIL_SMTP_CONNECTION_REFUSED", result.ErrorCode);
        Assert.Equal(
            "SMTP 连接被拒绝，请检查 SMTP 主机、端口和 SSL 设置：mail.hotbargain.com.au:25",
            result.Message
        );
    }

    [Fact]
    public async Task InvoiceEmailService_UsesStoredPlainTextPassword()
    {
        var settingsService = CreateSettingsService();
        await _db.Insertable(
            new InvoiceEmailConfiguration
            {
                Id = InvoiceEmailConfiguration.DefaultId,
                Name = "Default Sender",
                IsDefault = true,
                Host = "mail.hotbargain.com.au",
                Port = 465,
                UseSsl = true,
                Username = "sender@hotbargain.com.au",
                EncryptedPassword = "plain-secret",
                FromEmail = "sender@hotbargain.com.au",
                MaxAttachmentBytes = 5_242_880,
            }
        ).ExecuteCommandAsync();
        var service = new SendingCaptureInvoiceEmailService(settingsService);

        var result = await service.SendInvoiceAsync(
            new StoreOrderInvoiceEmailMessage
            {
                ToEmail = "customer@example.com",
                Subject = "invoice",
                Body = "body",
                Attachments = new List<StoreOrderInvoiceEmailAttachment>
                {
                    new()
                    {
                        FileName = "invoice.pdf",
                        ContentType = "application/pdf",
                        Bytes = new byte[] { 1, 2, 3, 4 },
                    },
                },
            }
        );

        Assert.True(result.Success);
        Assert.Equal("sender@hotbargain.com.au", service.AuthenticatedUsername);
        Assert.Equal("plain-secret", service.AuthenticatedPassword);
    }

    [Fact]
    public async Task InvoiceEmailService_WhenDefaultAccountInvalid_ReturnsClearFailure()
    {
        var settingsService = CreateSettingsService();
        await _db.Insertable(
            new[]
            {
                new InvoiceEmailConfiguration
                {
                    Id = "sender-a",
                    Name = "Sender A",
                    IsDefault = false,
                    Host = "a.smtp.example.com",
                    Port = 465,
                    UseSsl = true,
                    Username = "sender-a",
                    FromEmail = "sender-a@example.com",
                    MaxAttachmentBytes = 5_242_880,
                },
                new InvoiceEmailConfiguration
                {
                    Id = "sender-b",
                    Name = "Sender B",
                    IsDefault = false,
                    Host = "b.smtp.example.com",
                    Port = 465,
                    UseSsl = true,
                    Username = "sender-b",
                    FromEmail = "sender-b@example.com",
                    MaxAttachmentBytes = 5_242_880,
                },
            }
        ).ExecuteCommandAsync();
        var service = new InvoiceEmailService(
            NullLogger<InvoiceEmailService>.Instance,
            settingsService
        );

        var result = await service.SendInvoiceAsync(
            new StoreOrderInvoiceEmailMessage
            {
                ToEmail = "customer@example.com",
                Subject = "invoice",
                Body = "body",
                Attachments = new List<StoreOrderInvoiceEmailAttachment>
                {
                    new()
                    {
                        FileName = "invoice.pdf",
                        ContentType = "application/pdf",
                        Bytes = new byte[] { 1, 2, 3, 4 },
                    },
                },
            }
        );

        Assert.False(result.Success);
        Assert.Equal("INVOICE_EMAIL_DEFAULT_ACCOUNT_INVALID", result.ErrorCode);
        Assert.Equal("发票邮件默认发件账号配置异常，请在发票邮箱配置中重新设置默认账号", result.Message);
    }

    [Fact]
    public async Task InvoiceEmailService_UsesDatabaseSettingsForSmtpAndSender()
    {
        var settingsService = CreateSettingsService();
        await settingsService.UpdateSettingsAsync(
            new UpdateInvoiceEmailSettingsDto
            {
                Accounts = new List<UpdateInvoiceEmailAccountDto>
                {
                    new()
                    {
                        Id = InvoiceEmailConfiguration.DefaultId,
                        Name = "Configured Sender",
                        Host = "db.smtp.example.com",
                        Port = 587,
                        UseSsl = false,
                        CheckCertificateRevocation = false,
                        Username = "db-user",
                        Password = "db-secret",
                        FromEmail = "configured@example.com",
                        FromName = "Configured Sender",
                        MaxAttachmentBytes = 5_242_880,
                        IsDefault = true,
                    },
                },
            },
            "admin"
        );
        var service = new SendingCaptureInvoiceEmailService(settingsService);

        var result = await service.SendInvoiceAsync(
            new StoreOrderInvoiceEmailMessage
            {
                ToEmail = "customer@example.com",
                Subject = "invoice",
                Body = "body",
                Attachments = new List<StoreOrderInvoiceEmailAttachment>
                {
                    new()
                    {
                        FileName = "invoice.pdf",
                        ContentType = "application/pdf",
                        Bytes = new byte[] { 1, 2, 3, 4 },
                    },
                },
            }
        );

        Assert.True(result.Success);
        Assert.Equal("db.smtp.example.com", service.ConnectedHost);
        Assert.Equal(587, service.ConnectedPort);
        Assert.Equal(SecureSocketOptions.StartTls, service.ConnectedSocketOptions);
        Assert.Equal("db-user", service.AuthenticatedUsername);
        Assert.Equal("db-secret", service.AuthenticatedPassword);
        Assert.False(service.CreatedClientRevocationCheck);
        Assert.Equal("Configured Sender", service.SentMessage!.From.Mailboxes.Single().Name);
        Assert.Equal("configured@example.com", service.SentMessage.From.Mailboxes.Single().Address);
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

    private StoreService CreateStoreService()
    {
        return new StoreService(CreateSqlSugarContext(_db), NullLogger<StoreService>.Instance);
    }

    private StoreOrderReactService CreateStoreOrderService(string? username = null)
    {
        var httpContextAccessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = CreateUser(username),
            },
        };

        return StoreOrderReactServiceTestFactory.Create(
            CreateSqlSugarContext(_db),
            httpContextAccessor,
            Mock.Of<IOrderNumberGenerator>(),
            new ConfigurationBuilder().Build(),
            Mock.Of<IMapper>(),
            Mock.Of<IStoreOrderLocationProductLookupService>(),
            Mock.Of<IWarehouseProductChangeHistoryService>()
        );
    }

    private StoreOrderInvoiceEmailJobService CreateInvoiceEmailJobService(
        Mock<IStoreOrderInvoiceAttachmentService> attachmentService,
        Mock<IInvoiceEmailService> invoiceEmailService,
        bool registerSqlSugarContext = true
    )
    {
        var services = new ServiceCollection();
        if (registerSqlSugarContext)
        {
            services.AddSingleton(CreateSqlSugarContext(_db));
        }
        services.AddSingleton(attachmentService.Object);
        services.AddSingleton(invoiceEmailService.Object);
        var provider = services.BuildServiceProvider();

        return new StoreOrderInvoiceEmailJobService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<StoreOrderInvoiceEmailJobService>.Instance
        );
    }

    private static StoreOrderCartDto CreateInvoiceOrder(bool includeOutboundDate = true)
    {
        return new StoreOrderCartDto
        {
            OrderGUID = "order-1",
            OrderNo = "SO001",
            StoreCode = "S001",
            StoreName = "Test Store",
            StoreAddress = "1 Test Street",
            StoreContactEmail = "customer@example.com",
            OrderDate = new DateTime(2026, 6, 4),
            OutboundDate = includeOutboundDate ? new DateTime(2026, 6, 5) : null,
            TotalImportAmount = 8.5m,
            TotalAllocatedImportAmount = 8.5m,
            ShippingFee = 1.5m,
            Items = new List<StoreOrderCartItemDto>
            {
                new()
                {
                    DetailGUID = "detail-1",
                    ProductCode = "P001",
                    ItemNumber = "HB001",
                    Barcode = "930000000001",
                    ProductName = "Test Product",
                    Quantity = 2m,
                    AllocQuantity = 1m,
                    ImportPrice = 8.5m,
                    RRP = 12.99m,
                    AllocatedImportAmount = 8.5m,
                },
            },
        };
    }

    private static string ExtractPdfText(byte[] pdfBytes)
    {
        using var reader = new PdfReader(pdfBytes);
        return string.Join(
            "\n",
            Enumerable
                .Range(1, reader.NumberOfPages)
                .Select(page => System.Text.Encoding.Latin1.GetString(reader.GetPageContent(page)))
        );
    }

    private static bool HasPdfImageXObject(byte[] pdfBytes)
    {
        using var reader = new PdfReader(pdfBytes);
        for (var page = 1; page <= reader.NumberOfPages; page++)
        {
            var resources = reader.GetPageN(page).GetAsDict(new PdfName("Resources"));
            var xObjects = resources?.GetAsDict(new PdfName("XObject"));
            if (xObjects == null)
            {
                continue;
            }

            foreach (var name in xObjects.Keys)
            {
                var xObject = PdfReader.GetPdfObject(xObjects.Get(name)) as PdfDictionary;
                if (new PdfName("Image").Equals(xObject?.GetAsName(new PdfName("Subtype"))))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static async Task<StoreOrderInvoiceEmailJobDto> WaitForInvoiceEmailJobAsync(
        StoreOrderInvoiceEmailJobService jobService,
        string jobId
    )
    {
        var job = await WaitForValueAsync(
            () => jobService.GetJobAsync(jobId),
            current =>
                current?.Status == StoreOrderInvoiceEmailJobStatusConstants.Succeeded
                || current?.Status == StoreOrderInvoiceEmailJobStatusConstants.Failed,
            describeLast: current => $"发票邮件发送 job 当前状态：{current?.Status ?? "未找到"}"
        );
        return job!;
    }

    private static SqlSugarContext CreateSqlSugarContext(ISqlSugarClient db)
    {
        var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(SqlSugarContext));
        var dbField = typeof(SqlSugarContext).GetField(
            "_db",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        dbField!.SetValue(context, db);
        return context;
    }

    private IInvoiceEmailSettingsService CreateSettingsService(InvoiceEmailOptions? fallback = null)
    {
        return new InvoiceEmailSettingsService(
            CreateSqlSugarContext(_db),
            Options.Create(fallback ?? new InvoiceEmailOptions()),
            DataProtectionProvider.Create("StoreOrderContactAndInvoiceTests"),
            NullLogger<InvoiceEmailSettingsService>.Instance
        );
    }

    private sealed class TestableInvoiceEmailService : InvoiceEmailService
    {
        public TestableInvoiceEmailService(IInvoiceEmailSettingsService settingsService)
            : base(NullLogger<InvoiceEmailService>.Instance, settingsService)
        {
            SettingsService = settingsService;
        }

        public SmtpClient CreateConfiguredClientForTest()
        {
            var options = SettingsService.GetEffectiveOptionsAsync().GetAwaiter().GetResult();
            return CreateSmtpClient(options);
        }

        public MimeKit.MimeMessage BuildMimeMessageForTest(StoreOrderInvoiceEmailMessage message)
        {
            return BuildMimeMessage(message);
        }

        private IInvoiceEmailSettingsService SettingsService { get; }
    }

    private sealed class TlsFailingInvoiceEmailService : InvoiceEmailService
    {
        public TlsFailingInvoiceEmailService(IInvoiceEmailSettingsService settingsService)
            : base(NullLogger<InvoiceEmailService>.Instance, settingsService)
        {
        }

        protected override Task ConnectSmtpClientAsync(
            SmtpClient smtpClient,
            InvoiceEmailOptions options,
            SecureSocketOptions secureSocketOptions
        )
        {
            throw new SslHandshakeException("handshake failed");
        }
    }

    private sealed class ConnectionRefusedInvoiceEmailService : InvoiceEmailService
    {
        public ConnectionRefusedInvoiceEmailService(IInvoiceEmailSettingsService settingsService)
            : base(NullLogger<InvoiceEmailService>.Instance, settingsService)
        {
        }

        protected override Task ConnectSmtpClientAsync(
            SmtpClient smtpClient,
            InvoiceEmailOptions options,
            SecureSocketOptions secureSocketOptions
        )
        {
            throw new SocketException((int)SocketError.ConnectionRefused);
        }
    }

    private sealed class SendingCaptureInvoiceEmailService : InvoiceEmailService
    {
        public SendingCaptureInvoiceEmailService(IInvoiceEmailSettingsService settingsService)
            : base(NullLogger<InvoiceEmailService>.Instance, settingsService)
        {
        }

        public string? ConnectedHost { get; private set; }
        public int ConnectedPort { get; private set; }
        public SecureSocketOptions ConnectedSocketOptions { get; private set; }
        public string? AuthenticatedUsername { get; private set; }
        public string? AuthenticatedPassword { get; private set; }
        public bool? CreatedClientRevocationCheck { get; private set; }
        public MimeKit.MimeMessage? SentMessage { get; private set; }

        protected override SmtpClient CreateSmtpClient(InvoiceEmailOptions options)
        {
            var client = base.CreateSmtpClient(options);
            CreatedClientRevocationCheck = client.CheckCertificateRevocation;
            return client;
        }

        protected override Task ConnectSmtpClientAsync(
            SmtpClient smtpClient,
            InvoiceEmailOptions options,
            SecureSocketOptions secureSocketOptions
        )
        {
            ConnectedHost = options.Host;
            ConnectedPort = options.Port;
            ConnectedSocketOptions = secureSocketOptions;
            return Task.CompletedTask;
        }

        protected override Task AuthenticateSmtpClientAsync(
            SmtpClient smtpClient,
            string username,
            string password
        )
        {
            AuthenticatedUsername = username;
            AuthenticatedPassword = password;
            return Task.CompletedTask;
        }

        protected override Task SendSmtpMessageAsync(SmtpClient smtpClient, MimeKit.MimeMessage email)
        {
            SentMessage = email;
            return Task.CompletedTask;
        }

        protected override Task DisconnectSmtpClientAsync(SmtpClient smtpClient)
        {
            return Task.CompletedTask;
        }
    }

    private async Task SeedStoreOrderGraphAsync()
    {
        await _db.Insertable(
            new Store
            {
                StoreGUID = "store-1",
                StoreCode = "S001",
                StoreName = "Test Store",
                Address = "1 Test Street",
                ContactEmail = "store@example.com",
                Phone = "123456",
                IsActive = true,
            }
        ).ExecuteCommandAsync();

        await _db.Insertable(
            new WareHouseOrder
            {
                OrderGUID = "order-1",
                OrderNo = "SO-001",
                StoreCode = "S001",
                FlowStatus = 1,
                OrderDate = new DateTime(2026, 6, 4),
                OEMTotalAmount = 25m,
                ShippingFee = 2m,
                Remarks = "test",
                IsDeleted = false,
            }
        ).ExecuteCommandAsync();

        await _db.Insertable(
            new Product
            {
                ProductCode = "P001",
                ItemNumber = "ITEM-001",
                Barcode = "BAR-001",
                ProductName = "Product 1",
                ProductImage = "image.png",
                LocalSupplierCode = "SUP001",
                PurchasePrice = 3m,
                RetailPrice = 9.99m,
                IsActive = true,
                IsDeleted = false,
            }
        ).ExecuteCommandAsync();

        await _db.Insertable(
            new WarehouseProduct
            {
                ProductCode = "P001",
                IsActive = true,
                IsDeleted = false,
                MinOrderQuantity = 1,
                ImportPrice = 3m,
            }
        ).ExecuteCommandAsync();

        await _db.Insertable(
            new DomesticProduct
            {
                ProductCode = "P001",
                UnitVolume = 1.5m,
                PackingQuantity = 3,
                IsDeleted = false,
            }
        ).ExecuteCommandAsync();

        await _db.Insertable(
            new WareHouseOrderDetails
            {
                DetailGUID = "detail-1",
                OrderGUID = "order-1",
                StoreCode = "S001",
                ProductCode = "P001",
                Quantity = 5,
                AllocQuantity = 4,
                OEMPrice = 5m,
                OEMAmount = 25m,
                ImportPrice = 3m,
                ImportAmount = 12m,
                IsDeleted = false,
            }
        ).ExecuteCommandAsync();
    }

    private async Task SeedStoreAsync(string storeGuid, string storeCode, string storeName)
    {
        await _db.Insertable(
            new Store
            {
                StoreGUID = storeGuid,
                StoreCode = storeCode,
                StoreName = storeName,
                Address = $"{storeName} Address",
                ContactEmail = $"{storeCode.ToLowerInvariant()}@example.com",
                Phone = "123456",
                IsActive = true,
                IsDeleted = false,
            }
        ).ExecuteCommandAsync();
    }

    private async Task SeedStoreRetailPriceAsync(
        string storeCode,
        string productCode,
        decimal purchasePrice,
        decimal retailPrice
    )
    {
        await _db.Insertable(
            new StoreRetailPrice
            {
                UUID = $"{storeCode}-{productCode}",
                StoreCode = storeCode,
                ProductCode = productCode,
                StoreProductCode = storeCode + productCode,
                SupplierCode = "SUP001",
                PurchasePrice = purchasePrice,
                StoreRetailPriceValue = retailPrice,
                IsActive = true,
                IsDeleted = false,
            }
        ).ExecuteCommandAsync();
    }

    private async Task SeedSetChildPurchasePriceRowsAsync(string productCode, string storeCode)
    {
        _db.CodeFirst.InitTables(typeof(ProductSetCode), typeof(StoreMultiCodeProduct));
        await _db.Insertable(
            new[]
            {
                new ProductSetCode
                {
                    SetCodeId = $"{productCode}-set-a",
                    ProductCode = productCode,
                    SetProductCode = $"{productCode}-child-a",
                    SetItemNumber = "SET-A",
                    SetPurchasePrice = 0m,
                    SetRetailPrice = 4m,
                    SetType = 1,
                    IsActive = true,
                    IsDeleted = false,
                },
                new ProductSetCode
                {
                    SetCodeId = $"{productCode}-set-b",
                    ProductCode = productCode,
                    SetProductCode = $"{productCode}-child-b",
                    SetItemNumber = "SET-B",
                    SetPurchasePrice = 0m,
                    SetRetailPrice = 6m,
                    SetType = 1,
                    IsActive = true,
                    IsDeleted = false,
                },
            }
        ).ExecuteCommandAsync();
        await _db.Insertable(
            new[]
            {
                new StoreMultiCodeProduct
                {
                    UUID = $"{storeCode}-{productCode}-child-a",
                    StoreCode = storeCode,
                    ProductCode = productCode,
                    MultiCodeProductCode = $"{productCode}-child-a",
                    StoreMultiCodeProductCode = $"{storeCode}-{productCode}-child-a",
                    PurchasePrice = 0m,
                    MultiCodeRetailPrice = 4m,
                    IsActive = true,
                    IsDeleted = false,
                },
                new StoreMultiCodeProduct
                {
                    UUID = $"{storeCode}-{productCode}-child-b",
                    StoreCode = storeCode,
                    ProductCode = productCode,
                    MultiCodeProductCode = $"{productCode}-child-b",
                    StoreMultiCodeProductCode = $"{storeCode}-{productCode}-child-b",
                    PurchasePrice = 0m,
                    MultiCodeRetailPrice = 6m,
                    IsActive = true,
                    IsDeleted = false,
                },
            }
        ).ExecuteCommandAsync();
    }

    private static ClaimsPrincipal CreateUser(string? username)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return new ClaimsPrincipal(new ClaimsIdentity());
        }

        return new ClaimsPrincipal(
            new ClaimsIdentity(
                new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, "user-1"),
                    new Claim(ClaimTypes.Name, username),
                },
                "TestAuth"
            )
        );
    }
}
