using System.Reflection;
using BlazorApp.Api.Controllers.React;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.Containers;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace BlazorApp.Api.Tests;

public class ReactContainerControllerSyncContractTests
{
    [Fact]
    public async Task GetComingSoonContainerSummaries_多用户共享30分钟缓存()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var containerService = new Mock<IContainerReactService>();
        containerService
            .Setup(service => service.GetComingSoonSummariesAsync(It.IsAny<DateTime>()))
            .ReturnsAsync(new List<ContainerMainDto>
            {
                new ContainerMainDto { HGUID = "CACHED-SUMMARY", 货柜编号 = "CS-1" },
            });

        var firstController = CreateController(containerService: containerService.Object, cache: cache);
        var secondController = CreateController(containerService: containerService.Object, cache: cache);

        var firstResponse = await firstController.GetComingSoonContainerSummaries();
        var secondResponse = await secondController.GetComingSoonContainerSummaries();

        Assert.IsType<OkObjectResult>(firstResponse);
        Assert.IsType<OkObjectResult>(secondResponse);
        // 两个用户共用同一份缓存：服务层只被调用一次
        containerService.Verify(service => service.GetComingSoonSummariesAsync(It.IsAny<DateTime>()), Times.Once);
        Assert.Equal(TimeSpan.FromMinutes(30), ReactContainerController.ComingSoonCacheDuration);
    }

    [Fact]
    public async Task GetComingSoonContainerSummaries_单次窗口查询_以悉尼今天为基准_不再走两段56天查询()
    {
        var calls = new List<DateTime>();
        var containerService = new Mock<IContainerReactService>(MockBehavior.Strict);
        containerService
            .Setup(service => service.GetComingSoonSummariesAsync(It.IsAny<DateTime>()))
            .Callback<DateTime>(localToday => calls.Add(localToday))
            .ReturnsAsync(new List<ContainerMainDto>
            {
                new ContainerMainDto { HGUID = "G-1", 货柜编号 = "N-1" },
                new ContainerMainDto { HGUID = "G-2", 货柜编号 = "N-2" },
            });
        var controller = CreateController(containerService: containerService.Object);
        var before = ContainerWarehouseArrival.GetSydneyToday(DateTime.UtcNow);

        var response = await controller.GetComingSoonContainerSummaries();

        var after = ContainerWarehouseArrival.GetSydneyToday(DateTime.UtcNow);
        var ok = Assert.IsType<OkObjectResult>(response);
        // 严格 Mock：若还去调 GetContainersAsync（旧的两段 56 天分页查询）会直接失败
        var call = Assert.Single(calls);
        Assert.Equal(call.Date, call);
        Assert.True(call == before || call == after, $"窗口基准应为悉尼本地今天，实际 {call:yyyy-MM-dd}");
        Assert.Equal(2, Assert.IsAssignableFrom<List<ContainerMainDto>>(GetPropertyValue<object>(ok.Value!, "data")).Count);
    }

    [Fact]
    public async Task GetComingSoonContainerProducts_同一货柜共享缓存且不同货柜独立()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var containerService = new Mock<IContainerReactService>();
        containerService
            .Setup(service => service.GetContainerProductsAsync("CONTAINER-A"))
            .ReturnsAsync(new List<ContainerDetailDto> { new ContainerDetailDto { HGUID = "DETAIL-A" } });
        containerService
            .Setup(service => service.GetContainerProductsAsync("CONTAINER-B"))
            .ReturnsAsync(new List<ContainerDetailDto> { new ContainerDetailDto { HGUID = "DETAIL-B" } });

        var firstController = CreateController(containerService: containerService.Object, cache: cache);
        var secondController = CreateController(containerService: containerService.Object, cache: cache);

        await firstController.GetComingSoonContainerProducts("CONTAINER-A");
        await secondController.GetComingSoonContainerProducts("CONTAINER-A");
        await secondController.GetComingSoonContainerProducts("CONTAINER-B");

        containerService.Verify(service => service.GetContainerProductsAsync("CONTAINER-A"), Times.Once);
        containerService.Verify(service => service.GetContainerProductsAsync("CONTAINER-B"), Times.Once);
        Assert.Equal(TimeSpan.FromMinutes(30), ReactContainerController.ComingSoonCacheDuration);
    }

    [Theory]
    [InlineData(Permissions.OrderFront.View)]
    [InlineData(Permissions.Orders.View)]
    [InlineData(Permissions.Orders.Create)]
    [InlineData(Permissions.Warehouse.ManageOrders)]
    [InlineData(Permissions.Warehouse.Manage)]
    public async Task ComingSoonReadEndpoints_任一商城权限可访问(string allowedPermission)
    {
        var containerService = new Mock<IContainerReactService>();
        containerService
            .Setup(service => service.GetComingSoonSummariesAsync(It.IsAny<DateTime>()))
            .ReturnsAsync(new List<ContainerMainDto>());
        containerService
            .Setup(service => service.GetContainerProductsAsync("CONTAINER-A"))
            .ReturnsAsync(new List<ContainerDetailDto>());
        containerService
            .Setup(service => service.GetComingSoonContainersAsync())
            .ReturnsAsync(new List<ComingSoonContainerDto>());
        var authorizationService = CreateAuthorizationService(allowedPermission);
        var controller = CreateController(
            containerService: containerService.Object,
            authorizationService: authorizationService.Object
        );

        var summariesResponse = await controller.GetComingSoonContainerSummaries();
        var productsResponse = await controller.GetComingSoonContainerProducts("CONTAINER-A");
        var legacyResponse = await controller.GetComingSoonContainers();

        Assert.IsType<OkObjectResult>(summariesResponse);
        Assert.IsType<OkObjectResult>(productsResponse);
        Assert.IsType<OkObjectResult>(legacyResponse);
    }

    [Fact]
    public async Task ComingSoonReadEndpoints_无商城权限返回403且不调用服务()
    {
        var containerService = new Mock<IContainerReactService>(MockBehavior.Strict);
        var controller = CreateController(
            containerService: containerService.Object,
            authorizationService: CreateAuthorizationService().Object
        );

        var summariesResponse = await controller.GetComingSoonContainerSummaries();
        var productsResponse = await controller.GetComingSoonContainerProducts("CONTAINER-A");
        var legacyResponse = await controller.GetComingSoonContainers();

        Assert.IsType<ForbidResult>(summariesResponse);
        Assert.IsType<ForbidResult>(productsResponse);
        Assert.IsType<ForbidResult>(legacyResponse);
        containerService.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(nameof(ReactContainerController.GetComingSoonContainerSummaries))]
    [InlineData(nameof(ReactContainerController.GetComingSoonContainerProducts))]
    [InlineData(nameof(ReactContainerController.GetComingSoonContainers))]
    public void ComingSoonReadEndpoints_使用类级认证而非方法级角色(string methodName)
    {
        var method = typeof(ReactContainerController).GetMethod(methodName);

        Assert.Empty(method!.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false));
        var controllerAuthorize = Assert.IsType<AuthorizeAttribute>(Assert.Single(
            typeof(ReactContainerController).GetCustomAttributes(
                typeof(AuthorizeAttribute),
                inherit: false
            )
        ));
        Assert.Null(controllerAuthorize.Roles);
        Assert.Null(controllerAuthorize.Policy);
    }

    [Fact]
    public async Task QueryContainerProducts_应使用路由货柜GUID并返回标准响应()
    {
        ContainerDetailQueryDto? actualRequest = null;
        var actualCancellationToken = default(CancellationToken);
        var expectedResult = new ContainerDetailQueryResultDto
        {
            Items = new List<ContainerDetailDto> { new() { HGUID = "DETAIL-1" } },
            ItemsTotal = 1,
            PageNumber = 1,
            PageSize = 50,
            HasMore = false,
            TotalComputed = true,
            StatsComputed = true,
            TagStats = new ContainerDetailTagStatsDto { All = 1 },
        };
        var containerService = new Mock<IContainerReactService>();
        containerService
            .Setup(service => service.QueryContainerDetailsAsync(
                It.IsAny<ContainerDetailQueryDto>(),
                It.IsAny<CancellationToken>()
            ))
            .Callback<ContainerDetailQueryDto, CancellationToken>((request, cancellationToken) =>
            {
                actualRequest = request;
                actualCancellationToken = cancellationToken;
            })
            .ReturnsAsync(expectedResult);
        var controller = CreateController(containerService: containerService.Object);
        using var cancellation = new CancellationTokenSource();

        var response = await controller.QueryContainerProducts(
            "ROUTE-GUID",
            new ContainerDetailQueryDto
            {
                ContainerGuid = "BODY-GUID",
                PageNumber = 1,
                PageSize = 50,
                IncludeTotal = false,
                IncludeStats = false,
            },
            cancellation.Token
        );

        var ok = Assert.IsType<OkObjectResult>(response);
        Assert.NotNull(actualRequest);
        Assert.Equal("ROUTE-GUID", actualRequest!.ContainerGuid);
        Assert.Equal(cancellation.Token, actualCancellationToken);
        Assert.False(actualRequest.IncludeTotal);
        Assert.False(actualRequest.IncludeStats);
        AssertPayload(ok.Value, true, "获取货柜商品明细成功", expectedResult);
    }

    [Theory]
    [InlineData(nameof(ReactContainerController.GetContainers))]
    [InlineData(nameof(ReactContainerController.GetContainerDetail))]
    [InlineData(nameof(ReactContainerController.GetContainerProducts))]
    [InlineData(nameof(ReactContainerController.QueryContainerProducts))]
    [InlineData(nameof(ReactContainerController.ExportContainerProducts))]
    [InlineData(nameof(ReactContainerController.GetFilteredContainerProducts))]
    [InlineData(nameof(ReactContainerController.GetDateFilterOptions))]
    [InlineData(nameof(ReactContainerController.GetDomesticSetCodes))]
    [InlineData(nameof(ReactContainerController.CheckConflicts))]
    [InlineData(nameof(ReactContainerController.QueryAllocationSales))]
    [InlineData(nameof(ReactContainerController.QueryAllocationSalesBranches))]
    public void ContainerReadEndpoints_使用货柜查看权限策略(string methodName)
    {
        AssertMethodHasPolicy(methodName, Permissions.Container.View);
    }

    [Fact]
    public async Task QueryAllocationSales_应使用路由货柜GUID并返回标准响应()
    {
        ContainerAllocationSalesQueryRequest? actualRequest = null;
        var expected = new ContainerAllocationSalesReportResponse { ContainerGuid = "ROUTE-GUID", CanQuery = true };
        var reportService = new Mock<IContainerAllocationSalesReportService>();
        reportService
            .Setup(service => service.QueryAsync("ROUTE-GUID", It.IsAny<ContainerAllocationSalesQueryRequest>()))
            .Callback<string, ContainerAllocationSalesQueryRequest>((_, request) => actualRequest = request)
            .ReturnsAsync(expected);
        var controller = CreateController(reportService: reportService.Object);

        var result = await controller.QueryAllocationSales(
            "ROUTE-GUID",
            new ContainerAllocationSalesQueryRequest { PageNumber = 2 }
        );

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(actualRequest);
        Assert.Equal(2, actualRequest!.PageNumber);
        AssertPayload(ok.Value, true, "获取货柜配销数据成功", expected);
    }

    [Fact]
    public async Task QueryAllocationSalesBranches_商品不属于货柜时返回404()
    {
        var reportService = new Mock<IContainerAllocationSalesReportService>();
        reportService
            .Setup(service => service.QueryBranchesAsync("ROUTE-GUID", It.IsAny<ContainerAllocationSalesBranchesQueryRequest>()))
            .ThrowsAsync(new KeyNotFoundException("货柜中不存在该商品。"));
        var controller = CreateController(reportService: reportService.Object);

        var result = await controller.QueryAllocationSalesBranches(
            "ROUTE-GUID",
            new ContainerAllocationSalesBranchesQueryRequest { ProductCode = "P-404" }
        );

        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        AssertPayload(notFound.Value, false, "货柜中不存在该商品。", null);
    }

    [Fact]
    public async Task QueryAllocationSales_货柜不存在时返回404()
    {
        var reportService = new Mock<IContainerAllocationSalesReportService>();
        reportService
            .Setup(service => service.QueryAsync("MISSING", It.IsAny<ContainerAllocationSalesQueryRequest>()))
            .ThrowsAsync(new KeyNotFoundException("货柜不存在。"));
        var controller = CreateController(reportService: reportService.Object);

        var result = await controller.QueryAllocationSales(
            "MISSING",
            new ContainerAllocationSalesQueryRequest()
        );

        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        AssertPayload(notFound.Value, false, "货柜不存在。", null);
    }

    [Theory]
    [InlineData(nameof(ReactContainerController.CreateContainer), Permissions.Container.Create)]
    [InlineData(nameof(ReactContainerController.UpdateContainer), Permissions.Container.Edit)]
    [InlineData(nameof(ReactContainerController.UpdateDomesticSetCodePrices), Permissions.Container.Edit)]
    [InlineData(nameof(ReactContainerController.BatchUpdateDetails), Permissions.Container.Edit)]
    [InlineData(nameof(ReactContainerController.BatchUpdateDetailsScoped), Permissions.Container.Edit)]
    [InlineData(nameof(ReactContainerController.ApplyFloatRateByScope), Permissions.Container.Edit)]
    [InlineData(nameof(ReactContainerController.ApplyPricesByScope), Permissions.Container.Edit)]
    [InlineData(nameof(ReactContainerController.RecalculateCostsByScope), Permissions.Container.Edit)]
    [InlineData(nameof(ReactContainerController.BackfillLastPricesByScope), Permissions.Container.Edit)]
    [InlineData(nameof(ReactContainerController.PushContainersToHbSales), Permissions.Container.Edit)]
    [InlineData(nameof(ReactContainerController.AssignProducts), Permissions.Container.Edit)]
    [InlineData(nameof(ReactContainerController.BatchDeleteDetails), Permissions.Container.Delete)]
    public void ContainerMutationEndpoints_使用货柜写权限策略(string methodName, string policy)
    {
        AssertMethodHasPolicy(methodName, policy);
    }

    [Fact]
    public async Task BatchUpdateDetailsScoped_传递货柜范围并返回服务层部分成功明细且不修改请求()
    {
        List<UpdateContainerDetailDto>? actualUpdates = null;
        var containerService = new Mock<IContainerReactService>();
        containerService
            .Setup(service =>
                service.BatchUpdateDetailsDetailedAsync(
                    "CONTAINER-GUID",
                    It.IsAny<List<UpdateContainerDetailDto>>()
                )
            )
            .Callback<string, List<UpdateContainerDetailDto>>((_, updates) => actualUpdates = updates)
            .ReturnsAsync(
                new ContainerDetailBatchUpdateResultDto
                {
                    TotalUpdated = 1,
                    TotalRequested = 2,
                    ValidationErrors =
                    {
                        new ContainerDetailBatchUpdateValidationErrorDto
                        {
                            HGUID = "DETAIL-CHINESE",
                            Field = "英文名称",
                            Code = "CONTAINS_CHINESE",
                            Message = "英文名称不能包含中文",
                        },
                    },
                }
            );
        var controller = CreateController(containerService: containerService.Object);

        var response = await controller.BatchUpdateDetailsScoped(
            "CONTAINER-GUID",
            new List<UpdateContainerDetailDto>
            {
                new()
                {
                    HGUID = "DETAIL-CHINESE",
                    英文名称 = "Large 草莓",
                    进口价格 = 4.56m,
                    ClearEnglishName = true,
                },
                new() { HGUID = "DETAIL-ENGLISH", 英文名称 = "Large Strawberry" },
            }
        );

        var ok = Assert.IsType<OkObjectResult>(response);
        Assert.NotNull(actualUpdates);
        var chineseUpdate = Assert.Single(actualUpdates!, update => update.HGUID == "DETAIL-CHINESE");
        var englishUpdate = Assert.Single(actualUpdates!, update => update.HGUID == "DETAIL-ENGLISH");
        Assert.Equal("Large 草莓", chineseUpdate.英文名称);
        Assert.Equal(4.56m, chineseUpdate.进口价格);
        Assert.True(chineseUpdate.ClearEnglishName);
        Assert.Equal("Large Strawberry", englishUpdate.英文名称);
        containerService.Verify(
            service =>
                service.BatchUpdateDetailsDetailedAsync(
                    "CONTAINER-GUID",
                    It.IsAny<List<UpdateContainerDetailDto>>()
                ),
            Times.Once
        );
        containerService.Verify(
            service => service.BatchUpdateDetailsAsync(It.IsAny<List<UpdateContainerDetailDto>>()),
            Times.Never
        );

        Assert.True(GetPropertyValue<bool>(ok.Value!, "success"));
        var data = GetPropertyValue<object>(ok.Value!, "data");
        Assert.Equal(1, GetPropertyValue<int>(data, "totalUpdated"));
        Assert.Equal(2, GetPropertyValue<int>(data, "totalRequested"));
        var validationErrors = Assert.IsAssignableFrom<IEnumerable<object>>(
            GetPropertyValue<object>(data, "validationErrors")
        );
        var validationError = Assert.Single(validationErrors);
        Assert.Equal("DETAIL-CHINESE", GetPropertyValue<string>(validationError, "hguid"));
        Assert.Equal("英文名称", GetPropertyValue<string>(validationError, "field"));
        Assert.Equal("CONTAINS_CHINESE", GetPropertyValue<string>(validationError, "code"));
        Assert.Equal("英文名称不能包含中文", GetPropertyValue<string>(validationError, "message"));
    }

    [Fact]
    public async Task BatchUpdateDetailsScoped_重复明细字段错误保持部分成功响应契约()
    {
        var containerService = new Mock<IContainerReactService>();
        containerService
            .Setup(service =>
                service.BatchUpdateDetailsDetailedAsync(
                    "CONTAINER-GUID",
                    It.IsAny<List<UpdateContainerDetailDto>>()
                )
            )
            .ReturnsAsync(
                new ContainerDetailBatchUpdateResultDto
                {
                    TotalRequested = 2,
                    TotalUpdated = 0,
                    ValidationErrors =
                    {
                        new ContainerDetailBatchUpdateValidationErrorDto
                        {
                            HGUID = "DETAIL-DUPLICATE",
                            Field = "*",
                            Code = "DUPLICATE_DETAIL_UPDATE",
                            Message = "同一请求不能重复提交同一货柜明细",
                        },
                    },
                }
            );
        var controller = CreateController(containerService: containerService.Object);

        var response = await controller.BatchUpdateDetailsScoped(
            "CONTAINER-GUID",
            new List<UpdateContainerDetailDto>
            {
                new() { HGUID = "DETAIL-DUPLICATE", 国内价格 = 2m },
                new() { HGUID = "DETAIL-DUPLICATE", 国内价格 = 3m },
            }
        );

        var ok = Assert.IsType<OkObjectResult>(response);
        var data = GetPropertyValue<object>(ok.Value!, "data");
        Assert.Equal(2, GetPropertyValue<int>(data, "totalRequested"));
        Assert.Equal(0, GetPropertyValue<int>(data, "totalUpdated"));
        var error = Assert.Single(
            Assert.IsAssignableFrom<IEnumerable<object>>(
                GetPropertyValue<object>(data, "validationErrors")
            )
        );
        Assert.Equal("DETAIL-DUPLICATE", GetPropertyValue<string>(error, "hguid"));
        Assert.Equal("*", GetPropertyValue<string>(error, "field"));
        Assert.Equal("DUPLICATE_DETAIL_UPDATE", GetPropertyValue<string>(error, "code"));
    }

    [Fact]
    public async Task BatchUpdateDetails_旧无范围路由继续调用部分成功兼容入口()
    {
        var containerService = new Mock<IContainerReactService>();
        containerService
            .Setup(service => service.BatchUpdateDetailsDetailedAsync(It.IsAny<List<UpdateContainerDetailDto>>()))
            .ReturnsAsync(new ContainerDetailBatchUpdateResultDto
            {
                TotalUpdated = 1,
                TotalRequested = 1,
                ValidationErrors =
                {
                    new ContainerDetailBatchUpdateValidationErrorDto
                    {
                        HGUID = "DETAIL-LEGACY",
                        Field = "英文名称",
                        Code = "CONTAINS_CHINESE",
                        Message = "英文名称不能包含中文",
                    },
                },
            });
        var controller = CreateController(containerService: containerService.Object);

        var response = await controller.BatchUpdateDetails(
            new List<UpdateContainerDetailDto>
            {
                new() { HGUID = "DETAIL-LEGACY", 进口价格 = 4.56m },
            }
        );

        var ok = Assert.IsType<OkObjectResult>(response);
        containerService.Verify(
            service => service.BatchUpdateDetailsDetailedAsync(It.IsAny<List<UpdateContainerDetailDto>>()),
            Times.Once
        );
        containerService.Verify(
            service => service.BatchUpdateDetailsDetailedAsync(
                It.IsAny<string>(),
                It.IsAny<List<UpdateContainerDetailDto>>()
            ),
            Times.Never
        );
        var data = GetPropertyValue<object>(ok.Value!, "data");
        Assert.Equal(1, GetPropertyValue<int>(data, "totalUpdated"));
        Assert.Single(
            Assert.IsAssignableFrom<IEnumerable<object>>(
                GetPropertyValue<object>(data, "validationErrors")
            )
        );
    }

    [Fact]
    public async Task ExportContainerProducts_SelectedHguids_应返回Excel文件()
    {
        var requests = new List<(string ContainerGuid, int PageNumber, int PageSize)>();
        var containerService = new Mock<IContainerReactService>();
        containerService
            .Setup(service => service.GetContainerDetailAsync("ROUTE-GUID"))
            .ReturnsAsync(CreateContainer());
        containerService
            .Setup(service => service.QueryContainerDetailsAsync(
                It.IsAny<ContainerDetailQueryDto>(),
                It.IsAny<CancellationToken>()
            ))
            .Callback<ContainerDetailQueryDto, CancellationToken>((request, _) =>
                requests.Add((request.ContainerGuid, request.PageNumber, request.PageSize))
            )
            .ReturnsAsync(
                new ContainerDetailQueryResultDto
                {
                    Items = new List<ContainerDetailDto>
                    {
                        CreateDetail("DETAIL-1", "HB-001"),
                        CreateDetail("DETAIL-2", "HB-002"),
                    },
                    HasMore = false,
                }
            );
        var controller = CreateController(containerService: containerService.Object);

        var response = await controller.ExportContainerProducts(
            "ROUTE-GUID",
            new ReactContainerDetailsExportRequest
            {
                Format = "excel",
                SelectedHguids = new List<string> { "DETAIL-1" },
                Columns = new List<string> { "Index", "ItemNumber", "OEMPrice" },
                Query = new ContainerDetailQueryDto { ItemNumber = "HB" },
            }
        );

        var file = Assert.IsType<FileContentResult>(response);
        Assert.Equal(
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            file.ContentType
        );
        Assert.EndsWith(".xlsx", file.FileDownloadName);
        Assert.NotEmpty(file.FileContents);
        Assert.Equal(("ROUTE-GUID", 1, 500), Assert.Single(requests));
        using var workbook = new XLWorkbook(new MemoryStream(file.FileContents));
        var worksheet = workbook.Worksheets.First();
        Assert.Equal("序号", worksheet.Cell(6, 1).GetString());
        Assert.Equal("货号", worksheet.Cell(6, 2).GetString());
        Assert.Equal("零售价", worksheet.Cell(6, 3).GetString());
        Assert.Equal("1", worksheet.Cell(7, 1).GetString());
        Assert.Equal("HB-001", worksheet.Cell(7, 2).GetString());
        Assert.Equal("2.34", worksheet.Cell(7, 3).GetString());
        Assert.True(worksheet.Cell(6, 4).IsEmpty());
    }

    [Fact]
    public async Task ExportContainerProducts_Query_应返回Pdf文件()
    {
        var requestedIncludeFlags = new List<(bool IncludeTotal, bool IncludeStats)>();
        var containerService = new Mock<IContainerReactService>();
        containerService
            .Setup(service => service.GetContainerDetailAsync("ROUTE-GUID"))
            .ReturnsAsync(CreateContainer());
        containerService
            .Setup(service => service.QueryContainerDetailsAsync(
                It.IsAny<ContainerDetailQueryDto>(),
                It.IsAny<CancellationToken>()
            ))
            .Callback<ContainerDetailQueryDto, CancellationToken>((request, _) =>
                requestedIncludeFlags.Add((request.IncludeTotal, request.IncludeStats))
            )
            .ReturnsAsync(
                new ContainerDetailQueryResultDto
                {
                    Items = new List<ContainerDetailDto> { CreateDetail("DETAIL-1", "HB-001") },
                    HasMore = false,
                }
            );
        var controller = CreateController(containerService: containerService.Object);

        var response = await controller.ExportContainerProducts(
            "ROUTE-GUID",
            new ReactContainerDetailsExportRequest
            {
                Format = "pdf",
                SelectedHguids = null!,
                ExportColumns = new List<string> { "itemNumber", "barcode" },
                Query = new ContainerDetailQueryDto
                {
                    ProductName = "Toy",
                    IncludeTotal = true,
                    IncludeStats = true,
                },
            }
        );

        var file = Assert.IsType<FileContentResult>(response);
        Assert.Equal("application/pdf", file.ContentType);
        Assert.EndsWith(".pdf", file.FileDownloadName);
        Assert.NotEmpty(file.FileContents);
        Assert.Equal((false, false), Assert.Single(requestedIncludeFlags));
    }

    [Fact]
    public void SyncContainersFromHq_使用货柜编辑权限策略()
    {
        var method = typeof(ReactContainerController).GetMethod(
            nameof(ReactContainerController.SyncContainersFromHq)
        );

        var authorizeAttribute = Assert.Single(
            method!.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
        );

        Assert.Equal(
            Permissions.Container.Edit,
            ((AuthorizeAttribute)authorizeAttribute).Policy
        );
    }

    [Fact]
    public async Task SyncContainersFromHq_请求体为空时_仍返回成功响应并透传空开始日期()
    {
        DateTime? actualStartDate = DateTime.MinValue;
        var syncResult = new SyncResult { IsSuccess = true, Message = "同步成功" };
        var syncService = new Mock<IContainerHqSyncService>();
        syncService
            .Setup(service => service.SyncIncrementalAsync(It.IsAny<DateTime?>()))
            .Callback<DateTime?>(startDate => actualStartDate = startDate)
            .ReturnsAsync(syncResult);

        var controller = CreateController(syncService.Object);

        var response = await controller.SyncContainersFromHq(null);

        var ok = Assert.IsType<OkObjectResult>(response);
        Assert.Equal(200, ok.StatusCode ?? 200);
        Assert.Null(actualStartDate);
        AssertPayload(ok.Value, true, "同步成功", syncResult);
    }

    [Fact]
    public async Task SyncContainersFromHq_传入开始日期时_应透传给同步服务()
    {
        var expectedStartDate = new DateTime(2026, 5, 31, 9, 30, 0, DateTimeKind.Utc);
        DateTime? actualStartDate = null;
        var syncService = new Mock<IContainerHqSyncService>();
        syncService
            .Setup(service => service.SyncIncrementalAsync(It.IsAny<DateTime?>()))
            .Callback<DateTime?>(startDate => actualStartDate = startDate)
            .ReturnsAsync(new SyncResult { IsSuccess = true, Message = "同步成功" });

        var controller = CreateController(syncService.Object);

        await controller.SyncContainersFromHq(
            new SyncFromHqRequestDto { StartDate = expectedStartDate }
        );

        Assert.Equal(expectedStartDate, actualStartDate);
    }

    [Fact]
    public async Task SyncContainersFromHq_并发冲突时_返回409和标准错误码()
    {
        var syncService = new Mock<IContainerHqSyncService>();
        syncService
            .Setup(service => service.SyncIncrementalAsync(It.IsAny<DateTime?>()))
            .ReturnsAsync(
                new SyncResult
                {
                    IsSuccess = false,
                    Message = "同步任务正在执行",
                    ErrorCode = ContainerHqSyncErrorCodes.Conflict,
                }
            );

        var controller = CreateController(syncService.Object);

        var response = await controller.SyncContainersFromHq(null);

        var conflict = Assert.IsType<ConflictObjectResult>(response);
        Assert.Equal(409, conflict.StatusCode);
        AssertPayload(conflict.Value, false, "同步任务正在执行", null, "CONTAINER_SYNC_CONFLICT");
    }

    [Fact]
    public async Task SyncContainersFromHq_HQ源数据异常时_返回422和标准错误码()
    {
        var syncService = new Mock<IContainerHqSyncService>();
        syncService
            .Setup(service => service.SyncIncrementalAsync(It.IsAny<DateTime?>()))
            .ReturnsAsync(
                new SyncResult
                {
                    IsSuccess = false,
                    Message = "HQ源数据异常",
                    ErrorCode = ContainerHqSyncErrorCodes.InvalidSourceData,
                }
            );

        var controller = CreateController(syncService.Object);

        var response = await controller.SyncContainersFromHq(null);

        var unprocessable = Assert.IsType<UnprocessableEntityObjectResult>(response);
        Assert.Equal(422, unprocessable.StatusCode);
        AssertPayload(
            unprocessable.Value,
            false,
            "HQ源数据异常",
            null,
            "CONTAINER_SYNC_INVALID_SOURCE_DATA"
        );
    }

    [Fact]
    public async Task SyncContainersFromHq_未预期异常时_返回500和内部错误码()
    {
        var syncService = new Mock<IContainerHqSyncService>();
        syncService
            .Setup(service => service.SyncIncrementalAsync(It.IsAny<DateTime?>()))
            .ThrowsAsync(new Exception("boom"));

        var controller = CreateController(syncService.Object);

        var response = await controller.SyncContainersFromHq(null);

        var serverError = Assert.IsType<ObjectResult>(response);
        Assert.Equal(500, serverError.StatusCode);
        AssertPayload(serverError.Value, false, "服务器内部错误", null, "INTERNAL_ERROR");
    }

    private static ReactContainerController CreateController(
        IContainerHqSyncService? syncService = null,
        IContainerReactService? containerService = null,
        IContainerAllocationSalesReportService? reportService = null,
        ContainerExportService? exportService = null,
        IMemoryCache? cache = null,
        IAuthorizationService? authorizationService = null
    )
    {
        var controller = new ReactContainerController(
            containerService ?? Mock.Of<IContainerReactService>(),
            reportService ?? Mock.Of<IContainerAllocationSalesReportService>(),
            syncService ?? Mock.Of<IContainerHqSyncService>(),
            exportService ?? CreateExportService(),
            authorizationService
                ?? CreateAuthorizationService(
                    Permissions.OrderFront.View,
                    Permissions.Orders.View,
                    Permissions.Orders.Create,
                    Permissions.Warehouse.ManageOrders,
                    Permissions.Warehouse.Manage
                ).Object,
            cache ?? new MemoryCache(new MemoryCacheOptions()),
            Mock.Of<ILogger<ReactContainerController>>()
        );
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext(),
        };
        return controller;
    }

    private static Mock<IAuthorizationService> CreateAuthorizationService(
        params string[] allowedPermissions
    )
    {
        var allowed = allowedPermissions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var authorizationService = new Mock<IAuthorizationService>(MockBehavior.Strict);
        authorizationService
            .Setup(service => service.AuthorizeAsync(
                It.IsAny<System.Security.Claims.ClaimsPrincipal>(),
                It.IsAny<object?>(),
                It.IsAny<string>()
            ))
            .ReturnsAsync(
                (
                    System.Security.Claims.ClaimsPrincipal _,
                    object? _,
                    string policy
                ) => allowed.Contains(policy)
                    ? AuthorizationResult.Success()
                    : AuthorizationResult.Failed()
            );
        return authorizationService;
    }

    private static void AssertMethodHasPolicy(string methodName, string policy)
    {
        var method = typeof(ReactContainerController).GetMethod(methodName);
        var authorizeAttributes = method!
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>()
            .ToList();

        Assert.Contains(authorizeAttributes, attribute => attribute.Policy == policy);
        Assert.DoesNotContain(
            authorizeAttributes,
            attribute => attribute.Roles?.Contains("User", StringComparison.OrdinalIgnoreCase) == true
        );
    }

    private static ContainerExportService CreateExportService()
    {
        return new ContainerExportService(
            Mock.Of<ILogger<ContainerExportService>>(),
            new HttpClient()
        );
    }

    private static ContainerMainDto CreateContainer()
    {
        return new ContainerMainDto
        {
            HGUID = "ROUTE-GUID",
            货柜编号 = "CN-001",
            装柜日期 = new DateTime(2026, 7, 1),
            合计件数 = 1,
            合计数量 = 12,
            总体积 = 0.5m,
        };
    }

    private static ContainerDetailDto CreateDetail(string hguid, string itemNumber)
    {
        return new ContainerDetailDto
        {
            HGUID = hguid,
            主表GUID = "ROUTE-GUID",
            商品编码 = $"P-{itemNumber}",
            装柜数量 = 12,
            进口价格 = 1.23m,
            贴牌价格 = 2.34m,
            商品信息 = new ContainerProductInfoDto
            {
                商品编码 = $"P-{itemNumber}",
                货号 = itemNumber,
                条形码 = $"BC-{itemNumber}",
                商品名称 = $"商品 {itemNumber}",
                英文名称 = $"Product {itemNumber}",
            },
        };
    }

    private static void AssertPayload(
        object? payload,
        bool expectedSuccess,
        string expectedMessage,
        object? expectedData,
        string? expectedErrorCode = null
    )
    {
        Assert.NotNull(payload);

        var success = GetPropertyValue<bool>(payload!, "success");
        var message = GetPropertyValue<string>(payload!, "message");
        var data = GetPropertyValue<object?>(payload!, "data");

        Assert.Equal(expectedSuccess, success);
        Assert.Equal(expectedMessage, message);

        if (expectedErrorCode is null)
        {
            Assert.Same(expectedData, data);
            return;
        }

        var syncResult = Assert.IsType<SyncResult>(data);
        Assert.Equal(expectedErrorCode, syncResult.ErrorCode);
    }

    private static T GetPropertyValue<T>(object source, string propertyName)
    {
        var property = source
            .GetType()
            .GetProperty(
                propertyName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase
            );

        Assert.NotNull(property);
        return (T)property!.GetValue(source)!;
    }
}
