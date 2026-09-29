using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Controllers.React;
using BlazorApp.Api.Models;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.POSM;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.TestHost;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class BrowserExtensionRankingTests
{
    [Fact]
    public void Controller_ExposesStoreAndSupplierRankingRoutes()
    {
        var type = typeof(ReactBrowserExtensionController);

        Assert.NotNull(type.GetMethod(nameof(ReactBrowserExtensionController.GetEnabledStores)));
        Assert.NotNull(type.GetMethod(nameof(ReactBrowserExtensionController.GetSupplierTopSales)));
        Assert.NotNull(
            type.GetMethod(nameof(ReactBrowserExtensionController.GetSupplierProductStoreSales))
        );
    }

    [Fact]
    public void StoreSalesRoute_RequiresSalesDetailPermission()
    {
        var action = typeof(ReactBrowserExtensionController).GetMethod(
            nameof(ReactBrowserExtensionController.GetSupplierProductStoreSales)
        );

        var authorize = Assert.Single(
            action!.GetCustomAttributes<AuthorizeAttribute>(inherit: true)
        );
        Assert.Equal(Permissions.SalesDashboard.SalesDetailView, authorize.Policy);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(10, 1)]
    [InlineData(11, 2)]
    [InlineData(100, 10)]
    public void CalculateTopItemCount_UsesCeilingTenPercent(int total, int expected)
    {
        Assert.Equal(expected, BrowserExtensionRankingLogic.CalculateTopItemCount(total));
    }

    [Theory]
    [InlineData(0, 30, 0)]
    [InlineData(1, 30, 1)]
    [InlineData(10, 30, 3)]
    [InlineData(11, 30, 4)]
    [InlineData(100, 30, 30)]
    public void CalculateTopItemCount_UsesRequestedPercentage(
        int total,
        int topPercent,
        int expected
    )
    {
        Assert.Equal(
            expected,
            BrowserExtensionRankingLogic.CalculateTopItemCount(total, topPercent)
        );
    }

    [Fact]
    public void RankTopDecile_SortsBySalesThenProductCode()
    {
        var rows = Enumerable.Range(1, 11)
            .Select(index => new BrowserExtensionSupplierSalesAggregate
            {
                ProductCode = $"P-{index:00}",
                SalesQuantity = index == 10 || index == 11 ? 50m : index,
            })
            .ToList();

        var ranked = BrowserExtensionRankingLogic.RankTopDecile(rows);

        Assert.Equal(2, ranked.Count);
        Assert.Equal(new[] { "P-10", "P-11" }, ranked.Select(item => item.ProductCode));
        Assert.Equal(new[] { 1, 2 }, ranked.Select(item => item.Rank));
    }

    [Fact]
    public void RankTopDecile_ComputesAverageSellingPriceFromNetSalesAndQuantity()
    {
        var rows = Enumerable.Range(1, 11)
            .Select(index => new BrowserExtensionSupplierSalesAggregate
            {
                ProductCode = $"P-{index:00}",
                SalesQuantity = index == 10 || index == 11 ? 50m : index,
                SalesAmount = index == 10 ? 100m : index == 11 ? 250m : index * 10m,
            })
            .ToList();

        var ranked = BrowserExtensionRankingLogic.RankTopDecile(rows);

        Assert.Equal(2, ranked.Count);
        Assert.Equal(2m, ranked[0].AverageSellingPrice);
        Assert.Equal(5m, ranked[1].AverageSellingPrice);
    }

    [Fact]
    public void RankTopPercent_AssignsMutuallyExclusiveBandsAndGlobalRanks()
    {
        var rows = Enumerable.Range(1, 10)
            .Select(index => new BrowserExtensionSupplierSalesAggregate
            {
                ProductCode = $"P-{index:00}",
                SalesQuantity = 11 - index,
                SalesAmount = (11 - index) * 10,
            })
            .ToList();

        var ranked = BrowserExtensionRankingLogic.RankTopPercent(rows, 30);

        Assert.Equal(new[] { 1, 2, 3 }, ranked.Select(item => item.Rank));
        Assert.Equal(
            new[]
            {
                BrowserExtensionSalesRankBands.Top10,
                BrowserExtensionSalesRankBands.Top20,
                BrowserExtensionSalesRankBands.Top30,
            },
            ranked.Select(item => item.SalesRankBand)
        );
    }

    [Fact]
    public void RankTopPercent_ExcludesZeroAndNegativeNetSales()
    {
        var ranked = BrowserExtensionRankingLogic.RankTopPercent(
            new[]
            {
                new BrowserExtensionSupplierSalesAggregate
                {
                    ProductCode = "P-1",
                    SalesQuantity = 1,
                },
                new BrowserExtensionSupplierSalesAggregate
                {
                    ProductCode = "P-2",
                    SalesQuantity = 0,
                },
                new BrowserExtensionSupplierSalesAggregate
                {
                    ProductCode = "P-3",
                    SalesQuantity = -1,
                },
            },
            30
        );

        Assert.Single(ranked);
        Assert.Equal("P-1", ranked[0].ProductCode);
        Assert.Equal(BrowserExtensionSalesRankBands.Top10, ranked[0].SalesRankBand);
    }

    [Fact]
    public void ApplySalesRankBands_DecoratesMatchedAndNoPurchaseButNotUnmatched()
    {
        var summaries = new[]
        {
            new BrowserExtensionProductSummaryDto
            {
                MatchStatus = BrowserExtensionMatchStatuses.Matched,
                ProductCode = "P-1",
            },
            new BrowserExtensionProductSummaryDto
            {
                MatchStatus = BrowserExtensionMatchStatuses.NoPurchase,
                ProductCode = "P-2",
            },
            new BrowserExtensionProductSummaryDto
            {
                MatchStatus = BrowserExtensionMatchStatuses.Unmatched,
                ProductCode = "P-3",
            },
        };
        var ranked = new[]
        {
            new BrowserExtensionSupplierSalesAggregate
            {
                ProductCode = "P-1",
                SalesRankBand = BrowserExtensionSalesRankBands.Top10,
            },
            new BrowserExtensionSupplierSalesAggregate
            {
                ProductCode = "P-2",
                SalesRankBand = BrowserExtensionSalesRankBands.Top20,
            },
            new BrowserExtensionSupplierSalesAggregate
            {
                ProductCode = "P-3",
                SalesRankBand = BrowserExtensionSalesRankBands.Top30,
            },
        };

        BrowserExtensionRankingLogic.ApplySalesRankBands(summaries, ranked);

        Assert.Equal(BrowserExtensionSalesRankBands.Top10, summaries[0].SalesRankBand);
        Assert.Equal(BrowserExtensionSalesRankBands.Top20, summaries[1].SalesRankBand);
        Assert.Null(summaries[2].SalesRankBand);
    }

    [Fact]
    public void CalculateAverageSellingPrice_ZeroQuantityReturnsNull()
    {
        Assert.Null(BrowserExtensionRankingLogic.CalculateAverageSellingPrice(100m, 0m));
    }

    [Fact]
    public void CalculateAverageSellingPrice_DividesNetSalesByQuantity()
    {
        Assert.Equal(5m, BrowserExtensionRankingLogic.CalculateAverageSellingPrice(100m, 20m));
    }

    [Theory]
    [InlineData(60)]
    [InlineData(90)]
    public void NormalizeDays_AcceptsSupportedWindow(int days)
    {
        Assert.Equal(days, BrowserExtensionRankingLogic.NormalizeDays(days));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(91)]
    public void NormalizeDays_RejectsOutOfRange(int days)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BrowserExtensionRankingLogic.NormalizeDays(days)
        );
    }

    [Theory]
    [InlineData(60)]
    [InlineData(90)]
    public void NormalizeSummaryDays_AcceptsSupportedWindows(int days)
    {
        Assert.Equal(days, BrowserExtensionRankingLogic.NormalizeSummaryDays(days));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(30)]
    [InlineData(61)]
    public void NormalizeSummaryDays_RejectsUnsupportedWindows(int days)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BrowserExtensionRankingLogic.NormalizeSummaryDays(days)
        );
    }

    [Fact]
    public void ResolveTopSalesPaging_PreservesLegacyUnpagedTopTen()
    {
        var paging = BrowserExtensionRankingLogic.ResolveTopSalesPaging(null, null, null);

        Assert.True(paging.IsLegacy);
        Assert.Equal(10, paging.TopPercent);
        Assert.Null(paging.Page);
        Assert.Null(paging.PageSize);
    }

    [Theory]
    [InlineData(30, 2, 100)]
    [InlineData(30, 3, 200)]
    public void ResolveTopSalesPaging_AcceptsExplicitPagedRequests(
        int topPercent,
        int page,
        int pageSize
    )
    {
        var paging = BrowserExtensionRankingLogic.ResolveTopSalesPaging(
            topPercent,
            page,
            pageSize
        );

        Assert.False(paging.IsLegacy);
        Assert.Equal(topPercent, paging.TopPercent);
        Assert.Equal(page, paging.Page);
        Assert.Equal(pageSize, paging.PageSize);
    }

    [Theory]
    [InlineData(30, null, null)]
    [InlineData(null, 1, 50)]
    [InlineData(10, 1, 50)]
    [InlineData(20, 1, 50)]
    [InlineData(30, 0, 50)]
    [InlineData(30, 1, 20)]
    public void ResolveTopSalesPaging_RejectsPartialOrUnsupportedRequests(
        int? topPercent,
        int? page,
        int? pageSize
    )
    {
        Assert.Throws<ArgumentException>(() =>
            BrowserExtensionRankingLogic.ResolveTopSalesPaging(topPercent, page, pageSize)
        );
    }

    [Fact]
    public void PagingAfterRanking_PreservesGlobalRankAcrossPages()
    {
        var rows = Enumerable.Range(1, 1000)
            .Select(index => new BrowserExtensionSupplierSalesAggregate
            {
                ProductCode = $"P-{index:0000}",
                SalesQuantity = 1001 - index,
            });
        var ranked = BrowserExtensionRankingLogic.RankTopPercent(rows, 30);

        var secondPage = ranked.Skip(50).Take(50).ToList();

        Assert.Equal(300, ranked.Count);
        Assert.Equal(51, secondPage[0].Rank);
        Assert.Equal(100, secondPage[^1].Rank);
    }

    [Fact]
    public void ResolvePageWindow_EmptyResultUsesPageOneAndZeroTotalPages()
    {
        var page = BrowserExtensionRankingLogic.ResolvePageWindow(9, 50, 0);

        Assert.Equal(1, page.Page);
        Assert.Equal(0, page.TotalPages);
        Assert.Equal(0, page.Skip);
    }

    [Fact]
    public void ResolvePageWindow_ClampsOverflowToLastPage()
    {
        var page = BrowserExtensionRankingLogic.ResolvePageWindow(99, 50, 301);

        Assert.Equal(7, page.Page);
        Assert.Equal(7, page.TotalPages);
        Assert.Equal(300, page.Skip);
    }

    [Fact]
    public void Dtos_ExposeSummaryBandsAndPagedTopSalesContract()
    {
        var summaryRequest = new BrowserExtensionProductSummaryBatchRequestDto
        {
            SalesRankingDays = 90,
        };
        var summary = new BrowserExtensionProductSummaryDto
        {
            SalesRankBand = BrowserExtensionSalesRankBands.Top20,
        };
        var batch = new BrowserExtensionProductSummaryBatchDto
        {
            SalesRankingAvailable = true,
            SalesRankingDays = 90,
            SalesRankingStartDate = new DateOnly(2026, 6, 6),
            SalesRankingEndDate = new DateOnly(2026, 9, 3),
        };
        var topSalesRequest = new BrowserExtensionSupplierTopSalesRequestDto
        {
            TopPercent = 30,
            Page = 2,
            PageSize = 50,
        };
        var topSales = new BrowserExtensionSupplierTopSalesDto
        {
            TopPercent = 30,
            TotalRankedCount = 300,
            Page = 2,
            PageSize = 50,
            TotalPages = 6,
        };

        Assert.Equal(90, summaryRequest.SalesRankingDays);
        Assert.Equal(BrowserExtensionSalesRankBands.Top20, summary.SalesRankBand);
        Assert.True(batch.SalesRankingAvailable);
        Assert.Equal(30, topSalesRequest.TopPercent);
        Assert.Equal(300, topSales.TotalRankedCount);
        Assert.Equal(2, topSales.Page);
        Assert.Equal(6, topSales.TotalPages);
    }

    [Fact]
    public void RankingRequestDtos_LeaveServiceValidatedFieldsWithoutRangeAttributes()
    {
        Assert.Empty(
            typeof(BrowserExtensionProductSummaryBatchRequestDto)
                .GetProperty(nameof(BrowserExtensionProductSummaryBatchRequestDto.SalesRankingDays))!
                .GetCustomAttributes(typeof(RangeAttribute), inherit: true)
        );
        Assert.Empty(
            typeof(BrowserExtensionSupplierTopSalesRequestDto)
                .GetProperty(nameof(BrowserExtensionSupplierTopSalesRequestDto.TopPercent))!
                .GetCustomAttributes(typeof(RangeAttribute), inherit: true)
        );
        Assert.Empty(
            typeof(BrowserExtensionSupplierTopSalesRequestDto)
                .GetProperty(nameof(BrowserExtensionSupplierTopSalesRequestDto.Page))!
                .GetCustomAttributes(typeof(RangeAttribute), inherit: true)
        );
        Assert.Empty(
            typeof(BrowserExtensionSupplierTopSalesRequestDto)
                .GetProperty(nameof(BrowserExtensionSupplierTopSalesRequestDto.PageSize))!
                .GetCustomAttributes(typeof(RangeAttribute), inherit: true)
        );
    }

    [Fact]
    public async Task SupplierTopSales_InvalidPagingUsesInvalidRequestEnvelope()
    {
        var service = new Mock<IBrowserExtensionService>(MockBehavior.Strict);
        service
            .Setup(item =>
                item.GetSupplierTopSalesAsync(
                    It.Is<BrowserExtensionSupplierTopSalesRequestDto>(request =>
                        request.TopPercent == 10 && request.Page == 1 && request.PageSize == 50
                    )
                )
            )
            .ThrowsAsync(new ArgumentException("显式分页请求的 topPercent 仅支持 30。"));
        var access = new Mock<IBrowserExtensionAccessService>(MockBehavior.Strict);
        access
            .Setup(item => item.CanAccessAsync(It.IsAny<ClaimsPrincipal>(), null))
            .ReturnsAsync(true);
        var controller = new ReactBrowserExtensionController(
            service.Object,
            access.Object,
            Mock.Of<ILocalSupplierCategoryCaptureService>(),
            NullLogger<ReactBrowserExtensionController>.Instance
        )
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity()),
                },
            },
        };

        var result = await controller.GetSupplierTopSales(
            new BrowserExtensionSupplierTopSalesRequestDto
            {
                SupplierCode = "SUP-1",
                TopPercent = 10,
                Page = 1,
                PageSize = 50,
            }
        );

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var body = Assert.IsType<ApiResponse<BrowserExtensionSupplierTopSalesDto>>(
            badRequest.Value
        );
        Assert.False(body.Success);
        Assert.Equal("INVALID_REQUEST", body.ErrorCode);
        service.VerifyAll();
        access.VerifyAll();
    }

    [Fact]
    public void ResolveStartDate_UsesInclusiveSixtyDayWindow()
    {
        var today = new DateOnly(2026, 8, 20);

        Assert.Equal(new DateOnly(2026, 6, 22), BrowserExtensionRankingLogic.ResolveStartDate(today, 60));
    }

    [Fact]
    public async Task RankingSnapshotCache_CoalescesConcurrentRequestsAndCachesForSixtySeconds()
    {
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var cache = new BrowserExtensionRankingSnapshotCache(memoryCache);
        var releaseFactory = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var factoryCalls = 0;

        async Task<BrowserExtensionSupplierSalesRankingSnapshot> CreateAsync()
        {
            Interlocked.Increment(ref factoryCalls);
            await releaseFactory.Task;
            return new BrowserExtensionSupplierSalesRankingSnapshot();
        }

        var first = cache.GetOrCreateAsync("same-key", CreateAsync);
        var second = cache.GetOrCreateAsync("same-key", CreateAsync);
        Assert.Equal(1, Volatile.Read(ref factoryCalls));

        releaseFactory.SetResult();
        var results = await Task.WhenAll(first, second);
        var cached = await cache.GetOrCreateAsync("same-key", CreateAsync);

        Assert.Same(results[0], results[1]);
        Assert.Same(results[0], cached);
        Assert.Equal(1, Volatile.Read(ref factoryCalls));
        Assert.Equal(
            TimeSpan.FromSeconds(60),
            BrowserExtensionRankingSnapshotCache.CacheDuration
        );
    }
}

public sealed class BrowserExtensionStoreSelectionTests
{
    [Fact]
    public void NormalizeRelatedStoreCodes_OnlyKeepsActiveRelatedStores()
    {
        var result = BrowserExtensionStoreSelection.NormalizeRelatedStoreCodes(
            new[]
            {
                new UserStoreDto { StoreCode = " 1014 ", IsActive = true },
                new UserStoreDto { StoreCode = "1014", IsActive = true },
                new UserStoreDto { StoreCode = "1013", IsActive = false },
                new UserStoreDto { StoreCode = "", IsActive = true },
            }
        );

        Assert.Equal(new[] { "1014" }, result);
    }

    [Fact]
    public async Task GetRelatedStoreCodesAsync_UsesCurrentUsersActiveStores()
    {
        var userService = new Mock<IUserService>(MockBehavior.Strict);
        userService
            .Setup(service => service.GetUserStoresAsync("user-1"))
            .ReturnsAsync(
                ApiResponse<List<UserStoreDto>>.OK(
                    new List<UserStoreDto>
                    {
                        new() { StoreCode = "1014", IsActive = true },
                        new() { StoreCode = "1013", IsActive = false },
                    }
                )
            );
        var service = new BrowserExtensionAccessService(
            Mock.Of<IAuthorizationService>(),
            Mock.Of<ICurrentUserManageableStoreScopeService>(),
            userService.Object
        );
        var user = new ClaimsPrincipal(
            new ClaimsIdentity(new[] { new Claim("userId", "user-1") }, "test")
        );

        var result = await service.GetRelatedStoreCodesAsync(user);

        Assert.Equal(new[] { "1014" }, result);
        userService.VerifyAll();
    }
}

public sealed class BrowserExtensionRankingRequestBindingTests
{
    [Fact]
    public async Task InvalidBoundTopSalesRequest_UsesInvalidRequestEnvelopeBeforeAction()
    {
        var filter = typeof(ReactBrowserExtensionController)
            .GetMethod(nameof(ReactBrowserExtensionController.GetSupplierTopSales))!
            .GetCustomAttribute<BrowserExtensionInvalidRequestFilter>();
        Assert.NotNull(filter);
        Assert.True(filter.Order < -2000);

        var httpContext = new DefaultHttpContext();
        var actionContext = new ActionContext(
            httpContext,
            new Microsoft.AspNetCore.Routing.RouteData(),
            new ActionDescriptor()
        );
        actionContext.ModelState.AddModelError("page", "The value 'not-a-number' is not valid.");
        var context = new ActionExecutingContext(
            actionContext,
            new List<IFilterMetadata>(),
            new Dictionary<string, object?>(),
            new ReactBrowserExtensionController(
                Mock.Of<IBrowserExtensionService>(),
                Mock.Of<IBrowserExtensionAccessService>(),
                Mock.Of<ILocalSupplierCategoryCaptureService>(),
                NullLogger<ReactBrowserExtensionController>.Instance
            )
        );

        await new BrowserExtensionInvalidRequestFilter().OnActionExecutionAsync(
            context,
            () => throw new Xunit.Sdk.XunitException("模型绑定失败时不应执行 action。")
        );

        var result = Assert.IsType<BadRequestObjectResult>(context.Result);
        var body = Assert.IsType<ApiResponse<BrowserExtensionSupplierTopSalesDto>>(result.Value);
        Assert.False(body.Success);
        Assert.Equal("INVALID_REQUEST", body.ErrorCode);
    }

    [Theory]
    [InlineData("\"not-a-number\"")]
    [InlineData("2147483648")]
    public async Task InvalidJsonBoundTopSalesRequest_UsesInvalidRequestEnvelopeInMvcPipeline(
        string invalidPage
    )
    {
        var service = new Mock<IBrowserExtensionService>(MockBehavior.Strict);
        var access = new Mock<IBrowserExtensionAccessService>(MockBehavior.Strict);
        using var server = new TestServer(
            new WebHostBuilder()
                .ConfigureServices(services =>
                {
                    services.AddLogging();
                    services.AddAuthorization();
                    services.AddSingleton(service.Object);
                    services.AddSingleton(access.Object);
                    services.AddSingleton(Mock.Of<ILocalSupplierCategoryCaptureService>());
                    services.AddControllers().AddApplicationPart(
                        typeof(ReactBrowserExtensionController).Assembly
                    );
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.Use(
                        async (context, next) =>
                        {
                            context.User = new ClaimsPrincipal(
                                new ClaimsIdentity(
                                    new[] { new Claim(ClaimTypes.NameIdentifier, "test-user") },
                                    "test"
                                )
                            );
                            await next();
                        }
                    );
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapControllers());
                })
        );
        using var client = server.CreateClient();

        using var response = await client.PostAsync(
            "/api/react/v1/browser-extension/supplier-top-sales",
            new StringContent(
                $"{{\"supplierCode\":\"240\",\"days\":60,\"topPercent\":30,\"page\":{invalidPage},\"pageSize\":50}}",
                Encoding.UTF8,
                "application/json"
            )
        );
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("\"errorCode\":\"INVALID_REQUEST\"", json, StringComparison.Ordinal);
        service.VerifyNoOtherCalls();
        access.VerifyNoOtherCalls();
    }
}

public sealed class BrowserExtensionServiceRankingContractTests : IDisposable
{
    private readonly string _localDbPath = Path.Combine(
        Path.GetTempPath(),
        $"browser-extension-ranking-{Guid.NewGuid():N}.db"
    );
    private readonly string _posmDbPath = Path.Combine(
        Path.GetTempPath(),
        $"browser-extension-ranking-posm-{Guid.NewGuid():N}.db"
    );
    private readonly SqliteConnection _localConnection;
    private readonly SqliteConnection _posmConnection;
    private readonly SqlSugarClient _localDb;
    private readonly SqlSugarClient _posmDb;

    public BrowserExtensionServiceRankingContractTests()
    {
        _localConnection = new SqliteConnection($"Data Source={_localDbPath}");
        _posmConnection = new SqliteConnection($"Data Source={_posmDbPath}");
        _localConnection.Open();
        _posmConnection.Open();
        _localDb = new SqlSugarClient(CreateConnectionConfig(_localConnection.ConnectionString));
        _posmDb = new SqlSugarClient(CreateConnectionConfig(_posmConnection.ConnectionString));
        _localDb.CodeFirst.InitTables(
            typeof(Store),
            typeof(Product),
            typeof(ProductStoreDailySalesStatistic),
            typeof(SalesStatisticRefreshState)
        );
        _posmDb.CodeFirst.InitTables(typeof(POSM_设备注册信息表));
    }

    [Fact]
    public async Task GetSupplierTopSalesAsync_DerivesLegacyAndPagedResultsFromOneRankingSnapshot()
    {
        var rankingDate = new DateTime(2026, 9, 3);
        await SeedRankingAsync(rankingDate, 1000);
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var service = CreateService(memoryCache, rankingDate);

        var legacy = await service.GetSupplierTopSalesAsync(
            new BrowserExtensionSupplierTopSalesRequestDto { SupplierCode = "240", Days = 60 }
        );
        var secondPage = await service.GetSupplierTopSalesAsync(
            new BrowserExtensionSupplierTopSalesRequestDto
            {
                SupplierCode = "240",
                Days = 60,
                TopPercent = 30,
                Page = 3,
                PageSize = 50,
            }
        );
        var overflowPage = await service.GetSupplierTopSalesAsync(
            new BrowserExtensionSupplierTopSalesRequestDto
            {
                SupplierCode = "240",
                Days = 60,
                TopPercent = 30,
                Page = 999,
                PageSize = 200,
            }
        );
        var largePage = await service.GetSupplierTopSalesAsync(
            new BrowserExtensionSupplierTopSalesRequestDto
            {
                SupplierCode = "240",
                Days = 60,
                TopPercent = 30,
                Page = 1,
                PageSize = 200,
            }
        );

        Assert.Equal(10, legacy.TopPercent);
        Assert.Null(legacy.Page);
        Assert.Equal(100, legacy.TotalRankedCount);
        Assert.Equal(100, legacy.Items.Count);
        Assert.Equal(1, legacy.Items[0].Rank);
        Assert.Equal(BrowserExtensionSalesRankBands.Top10, legacy.Items[0].SalesRankBand);

        Assert.Equal(300, secondPage.TotalRankedCount);
        Assert.Equal(3, secondPage.Page);
        Assert.Equal(6, secondPage.TotalPages);
        Assert.Equal(50, secondPage.Items.Count);
        Assert.Equal(101, secondPage.Items[0].Rank);
        Assert.Equal(150, secondPage.Items[^1].Rank);
        Assert.All(secondPage.Items, item => Assert.Equal(BrowserExtensionSalesRankBands.Top20, item.SalesRankBand));

        Assert.Equal(2, overflowPage.Page);
        Assert.Equal(2, overflowPage.TotalPages);
        Assert.Equal(100, overflowPage.Items.Count);
        Assert.Equal(201, overflowPage.Items[0].Rank);
        Assert.Equal(300, overflowPage.Items[^1].Rank);
        Assert.All(overflowPage.Items, item => Assert.Equal(BrowserExtensionSalesRankBands.Top30, item.SalesRankBand));

        Assert.Equal(200, largePage.Items.Count);
        Assert.True(largePage.Items.Count <= 200);
    }

    [Fact]
    public async Task GetSupplierTopSalesAsync_WithoutEnabledStores_ReturnsNegotiatedEmptyPage()
    {
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var service = CreateService(memoryCache, new DateTime(2026, 9, 3));

        var result = await service.GetSupplierTopSalesAsync(
            new BrowserExtensionSupplierTopSalesRequestDto
            {
                SupplierCode = "240",
                Days = 60,
                TopPercent = 30,
                Page = 7,
                PageSize = 50,
            }
        );

        Assert.Equal(30, result.TopPercent);
        Assert.Equal(0, result.TotalProductCount);
        Assert.Equal(0, result.TotalRankedCount);
        Assert.Equal(1, result.Page);
        Assert.Equal(0, result.TotalPages);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task GetSupplierProductStoreSalesAsync_IncludesEnabledZeroSalesStoresAndMatchesTotal()
    {
        var rankingDate = new DateTime(2026, 9, 3);
        await SeedRankingAsync(rankingDate, 1);
        await SeedEnabledStoreAsync("S-2", "Campbelltown", "POS-2");
        await SeedEnabledStoreAsync("S-3", "Springfield", "POS-3");
        await _localDb.Insertable(new ProductStoreDailySalesStatistic
        {
            Date = rankingDate,
            BranchCode = "S-2",
            SupplierCode = "240",
            ProductCode = "P-0001",
            ProductName = "商品 1",
            TotalQuantity = 12,
            TotalAmount = 120m,
            UpdateTime = rankingDate,
        }).ExecuteCommandAsync();

        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var service = CreateService(memoryCache, rankingDate);
        var ranking = await service.GetSupplierTopSalesAsync(
            new BrowserExtensionSupplierTopSalesRequestDto
            {
                SupplierCode = "240",
                Days = 60,
            }
        );

        var result = await service.GetSupplierProductStoreSalesAsync(
            new BrowserExtensionSupplierProductStoreSalesRequestDto
            {
                SupplierCode = "240",
                ProductCode = "P-0001",
                Days = 60,
                StartDate = new DateOnly(2026, 7, 6),
                EndDate = new DateOnly(2026, 9, 3),
                ExpectedTotalSalesQuantity = 13m,
                SnapshotVersion = ranking.SnapshotVersion,
            }
        );

        Assert.Equal("240", result.SupplierCode);
        Assert.Equal("P-0001", result.ProductCode);
        Assert.Equal(new DateOnly(2026, 7, 6), result.StartDate);
        Assert.Equal(new DateOnly(2026, 9, 3), result.EndDate);
        Assert.Equal(3, result.EnabledStoreCount);
        Assert.Equal(13m, result.TotalSalesQuantity);
        Assert.Equal(13m, result.Stores.Sum(store => store.SalesQuantity));
        Assert.Collection(
            result.Stores,
            store =>
            {
                Assert.Equal("S-2", store.StoreCode);
                Assert.Equal("Campbelltown", store.StoreName);
                Assert.Equal(12m, store.SalesQuantity);
            },
            store =>
            {
                Assert.Equal("S-1", store.StoreCode);
                Assert.Equal("测试门店", store.StoreName);
                Assert.Equal(1m, store.SalesQuantity);
            },
            store =>
            {
                Assert.Equal("S-3", store.StoreCode);
                Assert.Equal("Springfield", store.StoreName);
                Assert.Equal(0m, store.SalesQuantity);
            }
        );
    }

    [Fact]
    public async Task GetSupplierProductStoreSalesAsync_RejectsChangedOrExpiredRankingSnapshot()
    {
        var rankingDate = new DateTime(2026, 9, 3);
        await SeedRankingAsync(rankingDate, 1);
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var service = CreateService(memoryCache, rankingDate);
        var ranking = await service.GetSupplierTopSalesAsync(
            new BrowserExtensionSupplierTopSalesRequestDto
            {
                SupplierCode = "240",
                Days = 60,
            }
        );

        await Assert.ThrowsAsync<BrowserExtensionRankingSnapshotChangedException>(() =>
            service.GetSupplierProductStoreSalesAsync(
                new BrowserExtensionSupplierProductStoreSalesRequestDto
                {
                    SupplierCode = "240",
                    ProductCode = "P-0001",
                    Days = 60,
                    StartDate = new DateOnly(2026, 7, 6),
                    EndDate = new DateOnly(2026, 9, 3),
                    ExpectedTotalSalesQuantity = 99m,
                    SnapshotVersion = ranking.SnapshotVersion,
                }
            )
        );
        await Assert.ThrowsAsync<BrowserExtensionRankingSnapshotChangedException>(() =>
            service.GetSupplierProductStoreSalesAsync(
                new BrowserExtensionSupplierProductStoreSalesRequestDto
                {
                    SupplierCode = "240",
                    ProductCode = "P-0001",
                    Days = 60,
                    StartDate = new DateOnly(2026, 7, 5),
                    EndDate = new DateOnly(2026, 9, 2),
                    ExpectedTotalSalesQuantity = 1m,
                    SnapshotVersion = ranking.SnapshotVersion,
                }
            )
        );
    }

    [Fact]
    public async Task GetSupplierProductStoreSalesAsync_RejectsChangedStoreSetWithSameTotal()
    {
        var rankingDate = new DateTime(2026, 9, 3);
        await SeedRankingAsync(rankingDate, 1);
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var service = CreateService(memoryCache, rankingDate);
        var ranking = await service.GetSupplierTopSalesAsync(
            new BrowserExtensionSupplierTopSalesRequestDto { SupplierCode = "240", Days = 60 }
        );

        await SeedEnabledStoreAsync("S-2", "Campbelltown", "POS-2");

        await Assert.ThrowsAsync<BrowserExtensionRankingSnapshotChangedException>(() =>
            service.GetSupplierProductStoreSalesAsync(
                new BrowserExtensionSupplierProductStoreSalesRequestDto
                {
                    SupplierCode = "240",
                    ProductCode = "P-0001",
                    Days = 60,
                    StartDate = ranking.StartDate,
                    EndDate = ranking.EndDate,
                    ExpectedTotalSalesQuantity = 1m,
                    SnapshotVersion = ranking.SnapshotVersion,
                }
            )
        );
    }

    [Fact]
    public async Task GetSupplierProductStoreSalesAsync_RejectsRedistributedStoreSalesWithSameTotal()
    {
        var rankingDate = new DateTime(2026, 9, 3);
        await SeedRankingAsync(rankingDate, 1);
        await SeedEnabledStoreAsync("S-2", "Campbelltown", "POS-2");
        await _localDb.Insertable(new ProductStoreDailySalesStatistic
        {
            Date = rankingDate,
            BranchCode = "S-2",
            SupplierCode = "240",
            ProductCode = "P-0001",
            ProductName = "商品 1",
            TotalQuantity = 0,
            TotalAmount = 0m,
            UpdateTime = rankingDate,
        }).ExecuteCommandAsync();
        await _localDb.Insertable(new SalesStatisticRefreshState
        {
            StatisticType = SalesStatisticType.ProductStoreDaily,
            Date = rankingDate,
            Status = SalesStatisticRefreshStatus.Fresh,
            CompletedAtUtc = rankingDate,
        }).ExecuteCommandAsync();
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var service = CreateService(memoryCache, rankingDate);
        var ranking = await service.GetSupplierTopSalesAsync(
            new BrowserExtensionSupplierTopSalesRequestDto { SupplierCode = "240", Days = 60 }
        );

        await _localDb.Updateable<ProductStoreDailySalesStatistic>()
            .SetColumns(row => row.TotalQuantity == 0)
            .Where(row => row.BranchCode == "S-1" && row.ProductCode == "P-0001")
            .ExecuteCommandAsync();
        await _localDb.Updateable<ProductStoreDailySalesStatistic>()
            .SetColumns(row => row.TotalQuantity == 1)
            .Where(row => row.BranchCode == "S-2" && row.ProductCode == "P-0001")
            .ExecuteCommandAsync();
        await _localDb.Updateable<SalesStatisticRefreshState>()
            .SetColumns(state => state.CompletedAtUtc == rankingDate.AddMinutes(1))
            .Where(state =>
                state.StatisticType == SalesStatisticType.ProductStoreDaily
                && state.Date == rankingDate
            )
            .ExecuteCommandAsync();

        await Assert.ThrowsAsync<BrowserExtensionRankingSnapshotChangedException>(() =>
            service.GetSupplierProductStoreSalesAsync(
                new BrowserExtensionSupplierProductStoreSalesRequestDto
                {
                    SupplierCode = "240",
                    ProductCode = "P-0001",
                    Days = 60,
                    StartDate = ranking.StartDate,
                    EndDate = ranking.EndDate,
                    ExpectedTotalSalesQuantity = 1m,
                    SnapshotVersion = ranking.SnapshotVersion,
                }
            )
        );
    }

    private async Task SeedRankingAsync(DateTime rankingDate, int productCount)
    {
        await _localDb.Insertable(new Store
        {
            StoreGUID = Guid.NewGuid().ToString(),
            StoreCode = "S-1",
            StoreName = "测试门店",
            TimeZoneId = "Australia/Brisbane",
            IsActive = true,
        }).ExecuteCommandAsync();
        await _posmDb.Insertable(new POSM_设备注册信息表
        {
            设备硬件识别码 = "browser-extension-test-pos",
            系统设备编号 = "POS-1",
            分店代码 = "S-1",
            设备类型 = "POS",
            设备系统 = "Windows",
            设备状态 = 1,
            设备授权码 = "test-only",
        }).ExecuteCommandAsync();

        var products = Enumerable.Range(1, productCount)
            .Select(index => new Product
            {
                UUID = $"product-{index:0000}",
                ProductCode = $"P-{index:0000}",
                ItemNumber = $"ITEM-{index:0000}",
                ProductName = $"商品 {index}",
            })
            .ToList();
        var statistics = Enumerable.Range(1, productCount)
            .Select(index => new ProductStoreDailySalesStatistic
            {
                Date = rankingDate,
                BranchCode = "S-1",
                SupplierCode = "240",
                ProductCode = $"P-{index:0000}",
                ProductName = $"商品 {index}",
                TotalQuantity = productCount + 1 - index,
                TotalAmount = (productCount + 1 - index) * 10m,
                UpdateTime = rankingDate,
            })
            .ToList();
        await _localDb.Insertable(products).ExecuteCommandAsync();
        await _localDb.Insertable(statistics).ExecuteCommandAsync();
    }

    private async Task SeedEnabledStoreAsync(string storeCode, string storeName, string deviceNumber)
    {
        await _localDb.Insertable(new Store
        {
            StoreGUID = Guid.NewGuid().ToString(),
            StoreCode = storeCode,
            StoreName = storeName,
            TimeZoneId = "Australia/Brisbane",
            IsActive = true,
        }).ExecuteCommandAsync();
        await _posmDb.Insertable(new POSM_设备注册信息表
        {
            设备硬件识别码 = $"browser-extension-test-{deviceNumber}",
            系统设备编号 = deviceNumber,
            分店代码 = storeCode,
            设备类型 = "POS",
            设备系统 = "Windows",
            设备状态 = 1,
            设备授权码 = "test-only",
        }).ExecuteCommandAsync();
    }

    private BrowserExtensionService CreateService(IMemoryCache memoryCache, DateTime rankingDate)
    {
        var options = new Mock<IOptionsSnapshot<BrowserExtensionOptions>>(MockBehavior.Strict);
        options.SetupGet(item => item.Value).Returns(new BrowserExtensionOptions());
        return new BrowserExtensionService(
            CreateSqlSugarContext(_localDb),
            CreatePosmSqlSugarContext(_posmDb),
            options.Object,
            NullLogger<BrowserExtensionService>.Instance,
            memoryCache,
            new FixedTimeProvider(new DateTimeOffset(rankingDate, TimeSpan.Zero))
        );
    }

    private static ConnectionConfig CreateConnectionConfig(string connectionString) =>
        new()
        {
            ConnectionString = connectionString,
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = false,
            InitKeyType = InitKeyType.Attribute,
        };

    private static SqlSugarContext CreateSqlSugarContext(ISqlSugarClient db)
    {
        var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(SqlSugarContext));
        typeof(SqlSugarContext)
            .GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(context, db);
        return context;
    }

    private static POSMSqlSugarContext CreatePosmSqlSugarContext(ISqlSugarClient db)
    {
        var context = (POSMSqlSugarContext)RuntimeHelpers.GetUninitializedObject(
            typeof(POSMSqlSugarContext)
        );
        typeof(POSMSqlSugarContext)
            .GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(context, db);
        return context;
    }

    public void Dispose()
    {
        _localDb.Dispose();
        _posmDb.Dispose();
        _localConnection.Dispose();
        _posmConnection.Dispose();
        SqliteTempFileCleanup.DeleteIfExists(_localDbPath);
        SqliteTempFileCleanup.DeleteIfExists(_posmDbPath);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
