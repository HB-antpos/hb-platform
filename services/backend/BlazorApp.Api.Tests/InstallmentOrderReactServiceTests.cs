using System.Reflection;
using System.Runtime.CompilerServices;
using BlazorApp.Api.Data;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.POSM;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class InstallmentOrderReactServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnection _connection;
    private readonly SqlSugarClient _db;

    public InstallmentOrderReactServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        _connection = new SqliteConnection($"Data Source={_dbPath}");
        _connection.Open();
        _db = new SqlSugarClient(
            new ConnectionConfig
            {
                ConnectionString = _connection.ConnectionString,
                DbType = DbType.Sqlite,
                IsAutoCloseConnection = false,
                InitKeyType = InitKeyType.Attribute,
            }
        );
        _db.CodeFirst.InitTables(
            typeof(InstallmentOrder),
            typeof(InstallmentOrderLine),
            typeof(InstallmentPayment),
            typeof(Store)
        );
    }

    [Fact]
    public void InstallmentOrderStatus_与Wpf状态值保持一致()
    {
        Assert.Equal(1, (int)InstallmentOrderStatus.Active);
        Assert.Equal(2, (int)InstallmentOrderStatus.PaidOff);
        Assert.Equal(3, (int)InstallmentOrderStatus.PickedUp);
        Assert.Equal(4, (int)InstallmentOrderStatus.Cancelled);
    }

    [Fact]
    public async Task GetOrderListAsync_从分期专表读取并组合全部查询条件()
    {
        await SeedStoreAsync("STORE-A", "Store A", "11 111 111 111", "Hot Bargain A");
        await SeedOrderAsync(
            "11111111-1111-1111-1111-111111111111",
            "INST-0001",
            "STORE-A",
            new DateTime(2026, 7, 2, 10, 0, 0),
            InstallmentOrderStatus.Active,
            " Alice Chen ",
            "0400 123 456"
        );
        await SeedOrderAsync(
            "22222222-2222-2222-2222-222222222222",
            "INST-0002",
            "STORE-A",
            new DateTime(2026, 7, 2, 11, 0, 0),
            InstallmentOrderStatus.PaidOff,
            "Alice Chen",
            "0400 123 456"
        );
        await SeedOrderAsync(
            "33333333-3333-3333-3333-333333333333",
            "INST-0003",
            "STORE-B",
            new DateTime(2026, 7, 2, 12, 0, 0),
            InstallmentOrderStatus.Active,
            "Alice Chen",
            "0400 123 456"
        );

        var result = await CreateService().GetOrderListAsync(
            new InstallmentOrderQueryParams
            {
                StartDate = new DateOnly(2026, 7, 2),
                EndDate = new DateOnly(2026, 7, 2),
                StoreCodes = ["STORE-A"],
                Status = InstallmentOrderStatus.Active,
                CustomerName = " alice ",
                CustomerPhone = "123 456",
                PageNumber = 1,
                PageSize = 20,
            }
        );

        var item = Assert.Single(result.Items);
        Assert.Equal(1, result.Total);
        Assert.Equal("INST-0001", item.InstallmentNumber);
        Assert.Equal("STORE-A", item.StoreCode);
        Assert.Equal("Store A", item.StoreName);
        Assert.Equal("11 111 111 111", item.ABN);
        Assert.Equal("Hot Bargain A", item.BrandName);
        Assert.Equal("Alice Chen", item.CustomerName.Trim());
        Assert.Equal(100m, item.TotalAmount);
        Assert.Equal(20m, item.MinimumDownPayment);
        Assert.Equal(30m, item.DownPaymentAmount);
        Assert.Equal(45m, item.PaidAmount);
        Assert.Equal(55m, item.BalanceAmount);
        Assert.Equal(InstallmentOrderStatus.Active, item.Status);
    }

    [Fact]
    public async Task GetOrderListAsync_按创建时间倒序稳定分页()
    {
        var sameTime = new DateTime(2026, 7, 3, 9, 0, 0);
        await SeedOrderAsync("AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA", "INST-A", "STORE-A", sameTime, InstallmentOrderStatus.Active);
        await SeedOrderAsync("BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB", "INST-B", "STORE-A", sameTime, InstallmentOrderStatus.Active);
        await SeedOrderAsync("CCCCCCCC-CCCC-CCCC-CCCC-CCCCCCCCCCCC", "INST-C", "STORE-A", sameTime.AddHours(1), InstallmentOrderStatus.Active);

        var result = await CreateService().GetOrderListAsync(
            new InstallmentOrderQueryParams { PageNumber = 2, PageSize = 1 }
        );

        Assert.Equal(3, result.Total);
        Assert.Equal(2, result.PageNumber);
        Assert.Equal(1, result.PageSize);
        Assert.Equal("INST-A", Assert.Single(result.Items).InstallmentNumber);
    }

    [Fact]
    public async Task GetOrderDetailAsync_返回分期主单商品行和支付记录()
    {
        const string installmentGuid = "11111111-1111-1111-1111-111111111111";
        await SeedStoreAsync("STORE-A", "Store A", "11 111 111 111", "Hot Bargain A");
        await SeedOrderAsync(
            installmentGuid,
            "INST-DETAIL",
            "STORE-A",
            new DateTime(2026, 7, 4, 10, 0, 0),
            InstallmentOrderStatus.PaidOff
        );
        await _db.Insertable(
                new InstallmentOrderLine
                {
                    InstallmentLineGuid = "21111111-1111-1111-1111-111111111111",
                    InstallmentGuid = installmentGuid,
                    ProductCode = "P-001",
                    ReferenceCode = "REF-001",
                    DisplayName = "测试商品",
                    LookupCode = "940000000001",
                    ItemNumber = "ITEM-01",
                    Quantity = 1.5m,
                    UnitPrice = 20m,
                    DiscountAmount = 2m,
                    ActualAmount = 28m,
                }
            )
            .ExecuteCommandAsync();
        await _db.Insertable(
                new InstallmentPayment
                {
                    PaymentGuid = "31111111-1111-1111-1111-111111111111",
                    InstallmentGuid = installmentGuid,
                    Method = 2,
                    Amount = 15m,
                    Reference = "EFT-001",
                    Status = 1,
                    RecordedAt = new DateTime(2026, 7, 4, 10, 5, 0),
                    CashierId = "CASHIER-1",
                    DeviceCode = "POS-1",
                }
            )
            .ExecuteCommandAsync();

        var result = await CreateService().GetOrderDetailAsync(installmentGuid, null);

        Assert.True(result.Success);
        var detail = Assert.IsType<InstallmentOrderDetailResponse>(result.Data);
        Assert.Equal("INST-DETAIL", detail.Order.InstallmentNumber);
        Assert.Equal("Store A", detail.Order.StoreName);
        var line = Assert.Single(detail.Lines);
        Assert.Equal(1.5m, line.Quantity);
        Assert.Equal("REF-001", line.ReferenceCode);
        var payment = Assert.Single(detail.Payments);
        Assert.Equal(15m, payment.Amount);
        Assert.Equal("EFT-001", payment.Reference);
        Assert.Equal("CASHIER-1", payment.CashierId);
    }

    [Fact]
    public async Task GetOrderDetailAsync_不存在的分期订单返回失败响应()
    {
        var result = await CreateService().GetOrderDetailAsync(
            "99999999-9999-9999-9999-999999999999",
            null
        );

        Assert.False(result.Success);
        Assert.Null(result.Data);
    }

    [Fact]
    public async Task 映射SQL_Unspecified时间_全部恢复为UTC_DateTimeOffset()
    {
        const string installmentGuid = "11111111-1111-1111-1111-111111111111";
        var createdAt = new DateTime(2026, 7, 4, 10, 0, 0, DateTimeKind.Unspecified);
        await SeedOrderAsync(
            installmentGuid,
            "INST-UTC",
            "STORE-A",
            createdAt,
            InstallmentOrderStatus.Cancelled
        );
        await _db.Updateable<InstallmentOrder>()
            .SetColumns(order => new InstallmentOrder
            {
                PickedUpAt = createdAt.AddHours(1),
                CancelledAt = createdAt.AddHours(2),
            })
            .Where(order => order.InstallmentGuid == installmentGuid)
            .ExecuteCommandAsync();
        await _db.Insertable(
                new InstallmentPayment
                {
                    PaymentGuid = Guid.NewGuid().ToString(),
                    InstallmentGuid = installmentGuid,
                    Method = 1,
                    Amount = 10m,
                    Status = 1,
                    RecordedAt = createdAt.AddMinutes(30),
                    CashierId = "CASHIER-1",
                    DeviceCode = "POS-1",
                }
            )
            .ExecuteCommandAsync();

        var list = await CreateService().GetOrderListAsync(new InstallmentOrderQueryParams());
        var summary = Assert.Single(list.Items);
        AssertUtcOffset(summary, "CreatedAt");
        AssertUtcOffset(summary, "UpdatedAt");

        var detailResult = await CreateService().GetOrderDetailAsync(installmentGuid, null);
        var detail = Assert.IsType<InstallmentOrderDetailResponse>(detailResult.Data);
        AssertUtcOffset(detail.Order, "CreatedAt");
        AssertUtcOffset(detail.Order, "UpdatedAt");
        AssertUtcOffset(GetProperty(detail, "PickupInfo")!, "PickedUpAt");
        AssertUtcOffset(GetProperty(detail, "CancellationInfo")!, "CancelledAt");
        AssertUtcOffset(Assert.Single(detail.Payments), "RecordedAt");
    }

    [Theory]
    [InlineData("BRI", "Brisbane QLD 4000", "2026-01-01T14:00:00", "2026-01-02T14:00:00")]
    [InlineData("SYD", "Sydney NSW 2000", "2026-01-01T13:00:00", "2026-01-02T13:00:00")]
    [InlineData("UNKNOWN", "Unknown location", "2026-01-01T13:00:00", "2026-01-02T13:00:00")]
    public async Task GetOrderListAsync_按门店本地自然日转换UTC边界且结束边界排除(
        string storeCode,
        string address,
        string expectedStartText,
        string expectedEndText
    )
    {
        await SeedStoreAsync(storeCode, storeCode, "", "", address);
        var expectedStart = DateTime.SpecifyKind(DateTime.Parse(expectedStartText), DateTimeKind.Utc);
        var expectedEnd = DateTime.SpecifyKind(DateTime.Parse(expectedEndText), DateTimeKind.Utc);
        await SeedOrderAsync(Guid.NewGuid().ToString(), "BEFORE", storeCode, expectedStart.AddTicks(-1), InstallmentOrderStatus.Active);
        await SeedOrderAsync(Guid.NewGuid().ToString(), "FIRST", storeCode, expectedStart, InstallmentOrderStatus.Active);
        await SeedOrderAsync(Guid.NewGuid().ToString(), "LAST", storeCode, expectedEnd.AddTicks(-1), InstallmentOrderStatus.Active);
        await SeedOrderAsync(Guid.NewGuid().ToString(), "AFTER", storeCode, expectedEnd, InstallmentOrderStatus.Active);
        var query = new InstallmentOrderQueryParams
        {
            StoreCodes = [storeCode],
            PageSize = 20,
        };
        SetBusinessDate(query, nameof(query.StartDate), new DateOnly(2026, 1, 2));
        SetBusinessDate(query, nameof(query.EndDate), new DateOnly(2026, 1, 2));

        var result = await CreateService().GetOrderListAsync(query);

        Assert.Equal(2, result.Total);
        Assert.Equal(["LAST", "FIRST"], result.Items.Select(item => item.InstallmentNumber));
    }

    [Theory]
    [InlineData("DELETED-QLD", "Brisbane QLD 4000", "2026-01-01T14:00:00", "2026-01-02T14:00:00")]
    [InlineData("DELETED-VIC", "Melbourne VIC 3000", "2026-01-01T13:00:00", "2026-01-02T13:00:00")]
    public async Task GetOrderListAsync_软删历史门店仍按原所在地时区归日(
        string storeCode,
        string address,
        string expectedStartText,
        string expectedEndText
    )
    {
        await SeedStoreAsync(storeCode, storeCode, "", "", address, isDeleted: true);
        var expectedStart = Utc(expectedStartText);
        var expectedEnd = Utc(expectedEndText);
        await SeedOrderAsync(Guid.NewGuid().ToString(), "FIRST", storeCode, expectedStart, InstallmentOrderStatus.Active);
        await SeedOrderAsync(Guid.NewGuid().ToString(), "LAST", storeCode, expectedEnd.AddTicks(-1), InstallmentOrderStatus.Active);
        await SeedOrderAsync(Guid.NewGuid().ToString(), "AFTER", storeCode, expectedEnd, InstallmentOrderStatus.Active);

        var result = await CreateService().GetOrderListAsync(
            new InstallmentOrderQueryParams
            {
                StoreCodes = [storeCode],
                StartDate = new DateOnly(2026, 1, 2),
                EndDate = new DateOnly(2026, 1, 2),
            }
        );

        Assert.Equal(2, result.Total);
        Assert.Equal(["LAST", "FIRST"], result.Items.Select(item => item.InstallmentNumber));
    }

    [Fact]
    public async Task GetOrderListAsync_Melbourne夏令时午夜使用十三小时UTC偏移()
    {
        const string storeCode = "MEL";
        await SeedStoreAsync(storeCode, "Melbourne", "", "", "Melbourne VIC 3000");
        await SeedOrderAsync(Guid.NewGuid().ToString(), "BEFORE", storeCode, Utc("2026-01-01T12:59:59.9999999"), InstallmentOrderStatus.Active);
        await SeedOrderAsync(Guid.NewGuid().ToString(), "MIDNIGHT", storeCode, Utc("2026-01-01T13:00:00"), InstallmentOrderStatus.Active);
        await SeedOrderAsync(Guid.NewGuid().ToString(), "NEXT", storeCode, Utc("2026-01-02T13:00:00"), InstallmentOrderStatus.Active);

        var result = await CreateService().GetOrderListAsync(
            new InstallmentOrderQueryParams
            {
                StoreCodes = [storeCode],
                StartDate = new DateOnly(2026, 1, 2),
                EndDate = new DateOnly(2026, 1, 2),
            }
        );

        Assert.Equal("MIDNIGHT", Assert.Single(result.Items).InstallmentNumber);
    }

    [Fact]
    public void GetOrderDetailAsync_契约强制显式传入门店范围()
    {
        var interfaceMethods = typeof(BlazorApp.Api.Interfaces.React.IInstallmentOrderReactService)
            .GetMethods()
            .Where(method => method.Name == nameof(BlazorApp.Api.Interfaces.React.IInstallmentOrderReactService.GetOrderDetailAsync))
            .ToList();
        var implementationMethods = typeof(InstallmentOrderReactService)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(method => method.Name == nameof(InstallmentOrderReactService.GetOrderDetailAsync))
            .ToList();

        Assert.All(interfaceMethods, method => Assert.Equal(2, method.GetParameters().Length));
        Assert.All(implementationMethods, method => Assert.Equal(2, method.GetParameters().Length));
        Assert.Single(interfaceMethods);
        Assert.Single(implementationMethods);
    }

    [Fact]
    public async Task GetOrderListAsync_跨时区分店分别使用各自UTC自然日边界()
    {
        await SeedStoreAsync("BRI", "Brisbane", "", "", "Brisbane QLD 4000");
        await SeedStoreAsync("SYD", "Sydney", "", "", "Sydney NSW 2000");
        await SeedOrderAsync(Guid.NewGuid().ToString(), "BRI-IN", "BRI", Utc("2026-01-01T14:00:00"), InstallmentOrderStatus.Active);
        await SeedOrderAsync(Guid.NewGuid().ToString(), "BRI-OUT", "BRI", Utc("2026-01-02T14:00:00"), InstallmentOrderStatus.Active);
        await SeedOrderAsync(Guid.NewGuid().ToString(), "SYD-IN", "SYD", Utc("2026-01-01T13:00:00"), InstallmentOrderStatus.Active);
        await SeedOrderAsync(Guid.NewGuid().ToString(), "SYD-OUT", "SYD", Utc("2026-01-02T13:00:00"), InstallmentOrderStatus.Active);
        var query = new InstallmentOrderQueryParams { StoreCodes = ["BRI", "SYD"] };
        SetBusinessDate(query, nameof(query.StartDate), new DateOnly(2026, 1, 2));
        SetBusinessDate(query, nameof(query.EndDate), new DateOnly(2026, 1, 2));

        var result = await CreateService().GetOrderListAsync(query);

        Assert.Equal(2, result.Total);
        Assert.Equal(
            ["BRI-IN", "SYD-IN"],
            result.Items.Select(item => item.InstallmentNumber).OrderBy(value => value)
        );
    }

    [Fact]
    public async Task GetOrderDetailAsync_越权时在读取商品和付款前统一返回不存在()
    {
        const string installmentGuid = "11111111-1111-1111-1111-111111111111";
        await SeedOrderAsync(
            installmentGuid,
            "INST-SCOPED",
            "STORE-B",
            Utc("2026-07-04T10:00:00"),
            InstallmentOrderStatus.Active
        );
        // 关键断言：删掉子表后越权查询仍应安全失败，证明服务没有先读取敏感明细。
        _db.Ado.ExecuteCommand("DROP TABLE InstallmentOrderLine; DROP TABLE InstallmentPayment;");

        var result = await InvokeScopedDetailAsync(
            CreateService(),
            installmentGuid,
            ["STORE-A"]
        );

        Assert.False(GetBooleanProperty(result, "Success"));
        Assert.Null(GetProperty(result, "Data"));
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        SqliteTempFileCleanup.DeleteIfExists(_dbPath);
    }

    private InstallmentOrderReactService CreateService() =>
        new(
            CreateContext<POSMSqlSugarContext>(),
            CreateContext<SqlSugarContext>(),
            NullLogger<InstallmentOrderReactService>.Instance
        );

    private TContext CreateContext<TContext>()
    {
        var context = (TContext)RuntimeHelpers.GetUninitializedObject(typeof(TContext));
        typeof(TContext)
            .GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(context, _db);
        return context;
    }

    private Task SeedStoreAsync(
        string code,
        string name,
        string abn,
        string brandName,
        string? address = null,
        bool isDeleted = false
    ) =>
        _db.Insertable(
                new Store
                {
                    StoreGUID = Guid.NewGuid().ToString(),
                    StoreCode = code,
                    StoreName = name,
                    ABN = abn,
                    BrandName = brandName,
                    Address = address,
                    IsDeleted = isDeleted,
                }
            )
            .ExecuteCommandAsync();

    private Task SeedOrderAsync(
        string installmentGuid,
        string installmentNumber,
        string storeCode,
        DateTime createdAt,
        InstallmentOrderStatus status,
        string customerName = "Customer",
        string customerPhone = "0400000000"
    ) =>
        _db.Insertable(
                new InstallmentOrder
                {
                    InstallmentGuid = installmentGuid,
                    InstallmentNumber = installmentNumber,
                    StoreCode = storeCode,
                    DeviceCode = "POS-1",
                    CashierId = "CASHIER-1",
                    CashierName = "Cashier One",
                    CustomerName = customerName,
                    CustomerPhone = customerPhone,
                    TotalAmount = 100m,
                    MinimumDownPayment = 20m,
                    DownPaymentAmount = 30m,
                    PaidAmount = 45m,
                    BalanceAmount = 55m,
                    Status = (int)status,
                    CreatedAt = createdAt,
                    UpdatedAt = createdAt.AddMinutes(5),
                }
            )
            .ExecuteCommandAsync();

    private static void SetBusinessDate(
        InstallmentOrderQueryParams query,
        string propertyName,
        DateOnly value
    )
    {
        var property = typeof(InstallmentOrderQueryParams).GetProperty(propertyName)!;
        // RED 兼容：旧契约仍是 DateTime，测试先继续执行到真正的时区边界断言。
        object boxedValue = property.PropertyType == typeof(DateOnly?)
            ? value
            : value.ToDateTime(TimeOnly.MinValue);
        property.SetValue(query, boxedValue);
    }

    private static DateTime Utc(string value) =>
        DateTime.SpecifyKind(DateTime.Parse(value), DateTimeKind.Utc);

    private static async Task<object> InvokeScopedDetailAsync(
        InstallmentOrderReactService service,
        string installmentGuid,
        IReadOnlyCollection<string>? allowedStoreCodes
    )
    {
        var method = typeof(InstallmentOrderReactService)
            .GetMethods()
            .SingleOrDefault(candidate =>
                candidate.Name == nameof(InstallmentOrderReactService.GetOrderDetailAsync)
                && candidate.GetParameters().Length == 2
            );
        Assert.NotNull(method);
        var task = Assert.IsAssignableFrom<Task>(
            method!.Invoke(service, [installmentGuid, allowedStoreCodes])
        );
        await task;
        return task.GetType().GetProperty("Result")!.GetValue(task)!;
    }

    [Theory]
    // 配置的时区优先于地址（即使地址邮编指向别的州）
    [InlineData("Australia/Brisbane", "SHOP 1 65-69 CRONULLA ST, CRONULLA, NSW, 2230", "Australia/Brisbane")]
    [InlineData("Australia/Melbourne", "Bankstown", "Australia/Melbourne")]
    // 地址只写区名时靠配置时区
    [InlineData("Australia/Sydney", "Bankstown", "Australia/Sydney")]
    // 未配置或无效时按地址推导
    [InlineData(null, "Shop 11, Southport QLD 4215", "Australia/Brisbane")]
    [InlineData("Invalid/TimeZone", "Westfield Southland, Cheltenham 3192, VIC", "Australia/Melbourne")]
    public void StoreTimeZoneResolver_PrefersConfiguredTimeZoneThenAddress(
        string? timeZoneId,
        string address,
        string expected
    )
    {
        Assert.Equal(
            expected,
            InstallmentOrderStoreTimeZoneResolver.Resolve(new Store
            {
                StoreCode = "S001",
                StoreName = "Store",
                TimeZoneId = timeZoneId,
                Address = address,
            })
        );
    }

    private static object? GetProperty(object value, string propertyName) =>
        value.GetType().GetProperty(propertyName)!.GetValue(value);

    private static bool GetBooleanProperty(object value, string propertyName) =>
        Assert.IsType<bool>(GetProperty(value, propertyName));

    private static void AssertUtcOffset(object value, string propertyName)
    {
        var timestamp = Assert.IsType<DateTimeOffset>(GetProperty(value, propertyName));
        Assert.Equal(TimeSpan.Zero, timestamp.Offset);
    }
}
