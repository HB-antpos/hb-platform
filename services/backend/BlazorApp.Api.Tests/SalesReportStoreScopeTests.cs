using System.Security.Claims;
using BlazorApp.Api.Controllers.React;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 销售报表 controller 的分店范围行为契约。
/// 通过公开 endpoint 验证授权范围最终传给报表 service 的值，不依赖源码字符串。
/// </summary>
public sealed class SalesReportStoreScopeTests
{
    private static readonly DateTime StartDate = new(2026, 9, 1);
    private const string UserGuid = "store-manager-1";

    [Theory]
    [InlineData(200, true)]
    [InlineData(500, true)]
    [InlineData(501, false)]
    public async Task 销售明细商品分页最多允许五百条(int pageSize, bool accepted)
    {
        var service = new Mock<ISalesDashboardReactService>(MockBehavior.Strict);
        var userService = CreateUserService(new[] { "S1" });
        if (accepted) SetupReportService(service, "sales-detail", (_, _) => { });

        var response = await CreateSalesController(service.Object, userService.Object)
            .GetSalesDetailReport(SalesDetailKind.Australia, StartDate, StartDate, pageSize: pageSize);

        if (accepted) Assert.IsType<OkObjectResult>(response);
        else Assert.IsType<BadRequestObjectResult>(response);
        if (accepted) service.VerifyAll();
        else service.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("sales-detail")]
    [InlineData("revenue-snapshot")]
    public async Task 普通用户关联店包含非主店且管理scope拒绝时仍查询成功(string endpoint)
    {
        var service = new Mock<ISalesDashboardReactService>(MockBehavior.Strict);
        var userService = CreateUserService(
            new[] { "S1", "S2" },
            managementResult: ApiResponse<UserDetailDto>.Error("FORBIDDEN")
        );
        List<string>? capturedBranches = null;
        List<string>? capturedFocus = null;
        SetupReportService(service, endpoint, (branches, focus) =>
        {
            capturedBranches = branches;
            capturedFocus = focus;
        });

        var response = endpoint == "sales-detail"
            ? await CreateSalesController(service.Object, userService.Object)
                .GetSalesDetailReport(SalesDetailKind.Australia, StartDate, StartDate)
            : await CreateRevenueController(service.Object, userService.Object)
                .GetRevenueReportSnapshot(StartDate, StartDate);

        Assert.IsType<OkObjectResult>(response);
        Assert.Equal(new[] { "S1", "S2" }, capturedBranches);
        if (endpoint == "revenue-snapshot")
            Assert.Equal(new[] { "S1", "S2" }, capturedFocus);
        else
            Assert.Null(capturedFocus);
        userService.Verify(x => x.GetUserByGuidAsync(UserGuid), Times.Never);
        userService.Verify(x => x.GetUserStoresAsync(UserGuid), Times.Once);
        service.VerifyAll();
    }

    [Theory]
    [InlineData("sales-detail")]
    [InlineData("revenue-snapshot")]
    public async Task 普通用户混合请求只把关联交集传给service(string endpoint)
    {
        var service = new Mock<ISalesDashboardReactService>(MockBehavior.Strict);
        var userService = CreateUserService(new[] { "S1", "S2" });
        List<string>? capturedBranches = null;
        List<string>? capturedFocus = null;
        SetupReportService(service, endpoint, (branches, focus) =>
        {
            capturedBranches = branches;
            capturedFocus = focus;
        });

        var response = endpoint == "sales-detail"
            ? await CreateSalesController(service.Object, userService.Object)
                .GetSalesDetailReport(
                    SalesDetailKind.Australia,
                    StartDate,
                    StartDate,
                    branchCodes: new List<string> { " S2 ", "S3", "S2" })
            : await CreateRevenueController(service.Object, userService.Object)
                .GetRevenueReportSnapshot(
                    StartDate,
                    StartDate,
                    branchCodes: new List<string> { " S2 ", "S3", "S2" },
                    focusBranchCodes: new List<string> { "S3", " S2 " });

        Assert.IsType<OkObjectResult>(response);
        Assert.Equal(new[] { "S2" }, capturedBranches);
        if (endpoint == "revenue-snapshot")
            Assert.Equal(new[] { "S2" }, capturedFocus);
        userService.Verify(x => x.GetUserByGuidAsync(UserGuid), Times.Never);
        userService.Verify(x => x.GetUserStoresAsync(UserGuid), Times.Once);
        service.VerifyAll();
    }

    [Theory]
    [InlineData("sales-detail")]
    [InlineData("revenue-snapshot")]
    public async Task 普通用户请求全部未关联店时拒绝且不调用service(string endpoint)
    {
        var service = new Mock<ISalesDashboardReactService>(MockBehavior.Strict);
        var userService = CreateUserService(new[] { "S1", "S2" });
        var response = endpoint == "sales-detail"
            ? await CreateSalesController(service.Object, userService.Object)
                .GetSalesDetailReport(
                    SalesDetailKind.Australia,
                    StartDate,
                    StartDate,
                    branchCodes: new List<string> { "S3" })
            : await CreateRevenueController(service.Object, userService.Object)
                .GetRevenueReportSnapshot(
                    StartDate,
                    StartDate,
                    branchCodes: new List<string> { "S3" });

        AssertDenied(endpoint, response);
        userService.Verify(x => x.GetUserStoresAsync(UserGuid), Times.Once);
        service.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("sales-detail")]
    [InlineData("revenue-snapshot")]
    public async Task 关联查询返回空范围或失败时拒绝且不调用service(string endpoint)
    {
        foreach (var userStoresResult in new[]
        {
            ApiResponse<List<UserStoreDto>>.OK(new List<UserStoreDto>()),
            ApiResponse<List<UserStoreDto>>.Error("查询分店失败"),
        })
        {
            var service = new Mock<ISalesDashboardReactService>(MockBehavior.Strict);
            var userService = CreateUserService(Array.Empty<string>(), storesResult: userStoresResult);
            var response = endpoint == "sales-detail"
                ? await CreateSalesController(service.Object, userService.Object)
                    .GetSalesDetailReport(SalesDetailKind.Australia, StartDate, StartDate)
                : await CreateRevenueController(service.Object, userService.Object)
                    .GetRevenueReportSnapshot(StartDate, StartDate);

            AssertDenied(endpoint, response);
            userService.Verify(x => x.GetUserStoresAsync(UserGuid), Times.Once);
            service.VerifyNoOtherCalls();
        }
    }

    [Theory]
    [InlineData("sales-detail")]
    [InlineData("revenue-snapshot")]
    public async Task 未提供身份时拒绝且不调用service(string endpoint)
    {
        var service = new Mock<ISalesDashboardReactService>(MockBehavior.Strict);
        var userService = new Mock<IUserService>(MockBehavior.Strict);
        var response = endpoint == "sales-detail"
            ? await CreateSalesController(service.Object, userService.Object, includeIdentity: false)
                .GetSalesDetailReport(SalesDetailKind.Australia, StartDate, StartDate)
            : await CreateRevenueController(service.Object, userService.Object, includeIdentity: false)
                .GetRevenueReportSnapshot(StartDate, StartDate);

        AssertDenied(endpoint, response);
        userService.VerifyNoOtherCalls();
        service.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("WarehouseManager")]
    public async Task 全店角色销售明细保留全店null范围语义(string role)
    {
        var service = new Mock<ISalesDashboardReactService>(MockBehavior.Strict);
        List<string>? capturedBranches = new();
        service.Setup(x => x.GetSalesDetailReportFilteredAsync(
                It.IsAny<DateRangeDto>(),
                SalesDetailKind.Australia,
                It.IsAny<List<string>?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<IReadOnlyCollection<SalesDetailSection>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<List<string>?>(),
                It.IsAny<List<string>?>(),
                It.IsAny<List<string>?>()))
            .Callback<DateRangeDto, SalesDetailKind, List<string>?, string?, string?, string?, string?, int, int, IReadOnlyCollection<SalesDetailSection>?, CancellationToken, List<string>?, List<string>?, List<string>?>(
                (_, _, branches, _, _, _, _, _, _, _, _, _, _, _) => capturedBranches = branches)
            .ReturnsAsync(new ProductReportResponseDto<SalesDetailReportDto> { Data = new() });
        var userService = new Mock<IUserService>(MockBehavior.Strict);

        var response = await CreateSalesController(service.Object, userService.Object, role)
            .GetSalesDetailReport(SalesDetailKind.Australia, StartDate, StartDate);

        Assert.IsType<OkObjectResult>(response);
        Assert.Null(capturedBranches);
        userService.VerifyNoOtherCalls();
        service.VerifyAll();
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("WarehouseManager")]
    public async Task 全店角色营业额快照保留请求分店并限制focus到请求范围(string role)
    {
        var service = new Mock<ISalesDashboardReactService>(MockBehavior.Strict);
        List<string>? capturedBranches = null;
        List<string>? capturedFocus = null;
        service.Setup(x => x.GetRevenueReportSnapshotAsync(
                It.IsAny<DateRangeDto>(),
                It.IsAny<List<string>?>(),
                It.IsAny<List<string>?>(),
                It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .Callback<DateRangeDto, List<string>?, List<string>?, int?, CancellationToken>(
                (_, branches, focus, _, _) =>
                {
                    capturedBranches = branches;
                    capturedFocus = focus;
                })
            .ReturnsAsync(new RevenueReportSnapshotDto());
        var userService = new Mock<IUserService>(MockBehavior.Strict);

        var response = await CreateRevenueController(service.Object, userService.Object, role)
            .GetRevenueReportSnapshot(
                StartDate,
                StartDate,
                branchCodes: new List<string> { " S2 ", "S1", "S2" },
                focusBranchCodes: new List<string> { "S3", " S2 " });

        Assert.IsType<OkObjectResult>(response);
        Assert.Equal(new[] { "S2", "S1" }, capturedBranches);
        Assert.Equal(new[] { "S2" }, capturedFocus);
        userService.VerifyNoOtherCalls();
        service.VerifyAll();
    }

    [Fact]
    public async Task 普通用户selected分店未关联时销售明细拒绝且不调用service()
    {
        var service = new Mock<ISalesDashboardReactService>(MockBehavior.Strict);
        var userService = CreateUserService(new[] { "S1", "S2" });

        var response = await CreateSalesController(service.Object, userService.Object)
            .GetSalesDetailReport(
                SalesDetailKind.Australia,
                StartDate,
                StartDate,
                selectedBranchCode: "S3");

        AssertDenied("sales-detail", response);
        service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task 普通用户focus分店未关联时营业额快照只传空focus()
    {
        var service = new Mock<ISalesDashboardReactService>(MockBehavior.Strict);
        var userService = CreateUserService(new[] { "S1", "S2" });
        List<string>? capturedBranches = null;
        List<string>? capturedFocus = null;
        service.Setup(x => x.GetRevenueReportSnapshotAsync(
                It.IsAny<DateRangeDto>(),
                It.IsAny<List<string>?>(),
                It.IsAny<List<string>?>(),
                It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .Callback<DateRangeDto, List<string>?, List<string>?, int?, CancellationToken>(
                (_, branches, focus, _, _) =>
                {
                    capturedBranches = branches;
                    capturedFocus = focus;
                })
            .ReturnsAsync(new RevenueReportSnapshotDto());

        var response = await CreateRevenueController(service.Object, userService.Object)
            .GetRevenueReportSnapshot(
                StartDate,
                StartDate,
                focusBranchCodes: new List<string> { "S3" });

        Assert.IsType<OkObjectResult>(response);
        Assert.Equal(new[] { "S1", "S2" }, capturedBranches);
        Assert.Empty(capturedFocus!);
        service.VerifyAll();
    }

    [Theory]
    [InlineData("sales-detail")]
    [InlineData("revenue-snapshot")]
    public async Task 空的显式分店请求拒绝且不调用service(string endpoint)
    {
        var service = new Mock<ISalesDashboardReactService>(MockBehavior.Strict);
        var userService = CreateUserService(new[] { "S1" });
        var response = endpoint == "sales-detail"
            ? await CreateSalesController(service.Object, userService.Object)
                .GetSalesDetailReport(
                    SalesDetailKind.Australia,
                    StartDate,
                    StartDate,
                    branchCodes: new List<string> { " ", "" })
            : await CreateRevenueController(service.Object, userService.Object)
                .GetRevenueReportSnapshot(
                    StartDate,
                    StartDate,
                    branchCodes: new List<string> { " ", "" });

        AssertDenied(endpoint, response);
        userService.VerifyNoOtherCalls();
        service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task 营业额快照含今天时附带最近统计发布时间()
    {
        var today = SalesStatisticsBusinessDate.Today();
        var publishedAt = new DateTime(2026, 9, 24, 6, 12, 0, DateTimeKind.Utc);
        var service = new Mock<ISalesDashboardReactService>(MockBehavior.Strict);
        SetupRevenueSnapshot(service);
        service.Setup(x => x.GetStatisticsFreshnessAsync())
            .ReturnsAsync(new StatisticsFreshnessDto { LastSuccessfulAtUtc = publishedAt, LatestRunStatus = "Success" });

        var response = await CreateRevenueController(service.Object, CreateUserService(new[] { "S1" }).Object)
            .GetRevenueReportSnapshot(today, today);

        Assert.Equal(publishedAt, ReadLastSuccessfulAt(response));
        service.VerifyAll();
    }

    [Fact]
    public async Task 营业额快照为历史日期时不读取统计发布时间()
    {
        var service = new Mock<ISalesDashboardReactService>(MockBehavior.Strict);
        SetupRevenueSnapshot(service);
        service.Setup(x => x.GetStatisticsFreshnessAsync()).ReturnsAsync(new StatisticsFreshnessDto());

        var response = await CreateRevenueController(service.Object, CreateUserService(new[] { "S1" }).Object)
            .GetRevenueReportSnapshot(StartDate, StartDate);

        Assert.Null(ReadLastSuccessfulAt(response));
        service.Verify(x => x.GetStatisticsFreshnessAsync(), Times.Never);
    }

    [Fact]
    public async Task 统计发布时间读取失败时营业额快照照常返回()
    {
        var today = SalesStatisticsBusinessDate.Today();
        var service = new Mock<ISalesDashboardReactService>(MockBehavior.Strict);
        SetupRevenueSnapshot(service);
        service.Setup(x => x.GetStatisticsFreshnessAsync()).ThrowsAsync(new InvalidOperationException("db down"));

        var response = await CreateRevenueController(service.Object, CreateUserService(new[] { "S1" }).Object)
            .GetRevenueReportSnapshot(today, today);

        Assert.Null(ReadLastSuccessfulAt(response));
        service.VerifyAll();
    }

    private static void SetupRevenueSnapshot(Mock<ISalesDashboardReactService> service)
    {
        service.Setup(x => x.GetRevenueReportSnapshotAsync(
                It.IsAny<DateRangeDto>(),
                It.IsAny<List<string>?>(),
                It.IsAny<List<string>?>(),
                It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RevenueReportSnapshotDto());
    }

    private static DateTime? ReadLastSuccessfulAt(IActionResult response)
    {
        var body = Assert.IsType<OkObjectResult>(response).Value!;
        var property = body.GetType().GetProperty("statisticsLastSuccessfulAtUtc");
        Assert.NotNull(property);
        return (DateTime?)property!.GetValue(body);
    }

    private static void SetupReportService(
        Mock<ISalesDashboardReactService> service,
        string endpoint,
        Action<List<string>?, List<string>?> capture
    )
    {
        if (endpoint == "sales-detail")
        {
            service.Setup(x => x.GetSalesDetailReportFilteredAsync(
                    It.IsAny<DateRangeDto>(),
                    SalesDetailKind.Australia,
                    It.IsAny<List<string>?>(),
                    It.IsAny<string?>(),
                    It.IsAny<string?>(),
                    It.IsAny<string?>(),
                    It.IsAny<string?>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<IReadOnlyCollection<SalesDetailSection>?>(),
                    It.IsAny<CancellationToken>(),
                    It.IsAny<List<string>?>(),
                    It.IsAny<List<string>?>(),
                    It.IsAny<List<string>?>()))
                .Callback<DateRangeDto, SalesDetailKind, List<string>?, string?, string?, string?, string?, int, int, IReadOnlyCollection<SalesDetailSection>?, CancellationToken, List<string>?, List<string>?, List<string>?>(
                    (_, _, branches, _, _, _, _, _, _, _, _, _, _, _) => capture(branches, null))
                .ReturnsAsync(new ProductReportResponseDto<SalesDetailReportDto> { Data = new() });
            return;
        }

        service.Setup(x => x.GetRevenueReportSnapshotAsync(
                It.IsAny<DateRangeDto>(),
                It.IsAny<List<string>?>(),
                It.IsAny<List<string>?>(),
                It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .Callback<DateRangeDto, List<string>?, List<string>?, int?, CancellationToken>(
                (_, branches, focus, _, _) => capture(branches, focus))
            .ReturnsAsync(new RevenueReportSnapshotDto());
    }

    private static Mock<IUserService> CreateUserService(
        IEnumerable<string> storeCodes,
        ApiResponse<UserDetailDto>? managementResult = null,
        ApiResponse<List<UserStoreDto>>? storesResult = null
    )
    {
        var userService = new Mock<IUserService>(MockBehavior.Strict);
        userService.Setup(x => x.GetUserByGuidAsync(UserGuid))
            .ReturnsAsync(managementResult ?? ApiResponse<UserDetailDto>.OK(new UserDetailDto
            {
                UserGUID = UserGuid,
                Stores = new List<UserStoreDto>(),
            }));
        userService.Setup(x => x.GetUserStoresAsync(UserGuid))
            .ReturnsAsync(storesResult ?? ApiResponse<List<UserStoreDto>>.OK(
                storeCodes.Select(code => new UserStoreDto
                {
                    StoreCode = code,
                    IsPrimary = false,
                    IsActive = true,
                }).ToList()));
        return userService;
    }

    private static SalesDetailReportController CreateSalesController(
        ISalesDashboardReactService service,
        IUserService userService,
        string? role = "StoreManager",
        bool includeIdentity = true
    )
    {
        var controller = new SalesDetailReportController(
            service,
            userService,
            NullLogger<SalesDetailReportController>.Instance);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = CreateHttpContext(role, includeIdentity),
        };
        return controller;
    }

    private static RevenueReportSnapshotController CreateRevenueController(
        ISalesDashboardReactService service,
        IUserService userService,
        string? role = "StoreManager",
        bool includeIdentity = true
    )
    {
        var controller = new RevenueReportSnapshotController(
            service,
            userService,
            NullLogger<RevenueReportSnapshotController>.Instance);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = CreateHttpContext(role, includeIdentity),
        };
        return controller;
    }

    private static DefaultHttpContext CreateHttpContext(string? role, bool includeIdentity)
    {
        var claims = new List<Claim>();
        if (includeIdentity)
            claims.Add(new Claim(ClaimTypes.NameIdentifier, UserGuid));
        if (!string.IsNullOrWhiteSpace(role))
            claims.Add(new Claim(ClaimTypes.Role, role));
        return new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth")),
        };
    }

    private static void AssertDenied(string endpoint, IActionResult result)
    {
        if (endpoint == "sales-detail")
        {
            var response = Assert.IsType<OkObjectResult>(result);
            var body = Assert.IsType<ProductReportResponseDto<SalesDetailReportDto>>(response.Value);
            Assert.Equal("no-access", body.CacheVersion);
            return;
        }

        Assert.IsType<ForbidResult>(result);
    }
}
