using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using BlazorApp.Api.Controllers.React;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HBweb;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class ReactLocalSupplierInvoiceAuthorizationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SqlSugarClient _db;

    public ReactLocalSupplierInvoiceAuthorizationTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
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
        _db.CodeFirst.InitTables<Store, UserStore, StoreLocalSupplierInvoice>();
    }

    [Fact]
    public async Task GetInvoice_MobileViewUserCanReadAnAssignedStoreInvoice()
    {
        await SeedScopedInvoiceAsync();
        var invoices = new Mock<ILocalSupplierInvoicesReactService>(MockBehavior.Strict);
        invoices
            .Setup(service => service.GetInvoiceAsync("invoice-1"))
            .ReturnsAsync(ApiResponse<LocalSupplierInvoiceDetailDto>.OK(new LocalSupplierInvoiceDetailDto()));
        var authorization = CreateAuthorizationService("LocalPurchase.MobileView");
        var controller = CreateController(invoices.Object, authorization.Object);

        var result = await controller.GetInvoice("invoice-1");

        Assert.IsType<OkObjectResult>(result);
        invoices.Verify(service => service.GetInvoiceAsync("invoice-1"), Times.Once);
        authorization.Verify(
            service => service.AuthorizeAsync(
                It.IsAny<ClaimsPrincipal>(),
                null,
                "LocalPurchase.MobileView"
            ),
            Times.Once
        );
    }

    [Fact]
    public async Task GetInvoice_LocalPurchaseViewUserCanStillReadAnAssignedStoreInvoice()
    {
        await SeedScopedInvoiceAsync();
        var invoices = new Mock<ILocalSupplierInvoicesReactService>(MockBehavior.Strict);
        invoices
            .Setup(service => service.GetInvoiceAsync("invoice-1"))
            .ReturnsAsync(
                ApiResponse<LocalSupplierInvoiceDetailDto>.OK(
                    new LocalSupplierInvoiceDetailDto()
                )
            );
        var controller = CreateController(
            invoices.Object,
            CreateAuthorizationService(Permissions.LocalPurchase.View).Object
        );

        var result = await controller.GetInvoice("invoice-1");

        Assert.IsType<OkObjectResult>(result);
        invoices.Verify(service => service.GetInvoiceAsync("invoice-1"), Times.Once);
    }

    [Fact]
    public async Task GetShopInvoice_OrderFrontViewUserCanReadAnAssignedStoreInvoice()
    {
        await SeedScopedInvoiceAsync();
        var invoices = new Mock<ILocalSupplierInvoicesReactService>(MockBehavior.Strict);
        invoices
            .Setup(service => service.GetInvoiceAsync("invoice-1"))
            .ReturnsAsync(
                ApiResponse<LocalSupplierInvoiceDetailDto>.OK(
                    new LocalSupplierInvoiceDetailDto()
                )
            );
        var controller = CreateController(
            invoices.Object,
            CreateAuthorizationService(Permissions.OrderFront.View).Object
        );

        var result = await controller.GetShopInvoice("invoice-1");

        Assert.IsType<OkObjectResult>(result);
        invoices.Verify(service => service.GetInvoiceAsync("invoice-1"), Times.Once);
    }

    [Theory]
    [InlineData("WarehouseStaff")]
    [InlineData("仓库员工")]
    public async Task GetShopInvoice_WarehouseStaffWithOrdersCreateCanReadAssignedStoreInvoice(
        string roleName
    )
    {
        await SeedScopedInvoiceAsync();
        var invoices = new Mock<ILocalSupplierInvoicesReactService>(MockBehavior.Strict);
        invoices
            .Setup(service => service.GetInvoiceAsync("invoice-1"))
            .ReturnsAsync(
                ApiResponse<LocalSupplierInvoiceDetailDto>.OK(
                    new LocalSupplierInvoiceDetailDto()
                )
            );
        var controller = CreateController(
            invoices.Object,
            CreateAuthorizationService(Permissions.Orders.Create).Object,
            roleNames: new[] { roleName }
        );

        var result = await controller.GetShopInvoice("invoice-1");

        Assert.IsType<OkObjectResult>(result);
        invoices.Verify(service => service.GetInvoiceAsync("invoice-1"), Times.Once);
    }

    [Fact]
    public async Task GetShopInvoice_OrdersCreateWithoutWarehouseStaffRoleIsForbidden()
    {
        await SeedScopedInvoiceAsync();
        var invoices = new Mock<ILocalSupplierInvoicesReactService>(MockBehavior.Strict);
        var controller = CreateController(
            invoices.Object,
            CreateAuthorizationService(Permissions.Orders.Create).Object
        );

        var result = await controller.GetShopInvoice("invoice-1");

        Assert.IsType<ForbidResult>(result);
        invoices.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(nameof(ReactLocalSupplierInvoicesController.ShopGrid))]
    [InlineData(nameof(ReactLocalSupplierInvoicesController.GetShopInvoice))]
    [InlineData(nameof(ReactLocalSupplierInvoicesController.GetShopDetailsGrid))]
    public async Task ShopReadEndpoints_OrderFrontUserCanReadAssignedStoreData(string methodName)
    {
        await SeedScopedInvoiceAsync();
        var invoices = new Mock<ILocalSupplierInvoicesReactService>(MockBehavior.Strict);
        SetupSuccessfulShopRead(invoices, methodName);
        var controller = CreateController(
            invoices.Object,
            CreateAuthorizationService(Permissions.OrderFront.View).Object
        );

        var result = await InvokeReadEndpointAsync(controller, methodName);

        Assert.IsType<OkObjectResult>(result);
        invoices.VerifyAll();
    }

    [Fact]
    public async Task GetInvoice_ForbidsWhenNeitherMobileNorExistingReadPermissionIsGranted()
    {
        await SeedScopedInvoiceAsync();
        var invoices = new Mock<ILocalSupplierInvoicesReactService>(MockBehavior.Strict);
        var controller = CreateController(invoices.Object, CreateAuthorizationService().Object);

        var result = await controller.GetInvoice("invoice-1");

        Assert.IsType<ForbidResult>(result);
        invoices.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetInvoice_MobileViewUserCannotReadAnUnassignedStoreInvoice()
    {
        await SeedScopedInvoiceAsync();
        await _db.Insertable(
            new StoreLocalSupplierInvoice
            {
                InvoiceGUID = "invoice-other-store",
                StoreCode = "S02",
                IsDeleted = false,
            }
        ).ExecuteCommandAsync();
        var invoices = new Mock<ILocalSupplierInvoicesReactService>(MockBehavior.Strict);
        var controller = CreateController(
            invoices.Object,
            CreateAuthorizationService(Permissions.LocalPurchase.MobileView).Object
        );

        var result = await controller.GetInvoice("invoice-other-store");

        Assert.IsType<ForbidResult>(result);
        invoices.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetShopInvoice_OrderFrontUserCannotReadAnUnassignedStoreInvoice()
    {
        await SeedScopedInvoiceAsync();
        await _db.Insertable(
            new StoreLocalSupplierInvoice
            {
                InvoiceGUID = "invoice-other-store",
                StoreCode = "S02",
                IsDeleted = false,
            }
        ).ExecuteCommandAsync();
        var invoices = new Mock<ILocalSupplierInvoicesReactService>(MockBehavior.Strict);
        var controller = CreateController(
            invoices.Object,
            CreateAuthorizationService(Permissions.OrderFront.View).Object
        );

        var result = await controller.GetShopInvoice("invoice-other-store");

        Assert.IsType<ForbidResult>(result);
        invoices.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetInvoice_WithoutAuthorizationServiceFailsClosed()
    {
        await SeedScopedInvoiceAsync();
        var invoices = new Mock<ILocalSupplierInvoicesReactService>(MockBehavior.Strict);
        var controller = CreateController(invoices.Object, authorizationService: null);

        var result = await controller.GetInvoice("invoice-1");

        Assert.IsType<ForbidResult>(result);
        invoices.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetShopInvoice_WithoutAuthorizationServiceFailsClosed()
    {
        await SeedScopedInvoiceAsync();
        var invoices = new Mock<ILocalSupplierInvoicesReactService>(MockBehavior.Strict);
        var controller = CreateController(invoices.Object, authorizationService: null);

        var result = await controller.GetShopInvoice("invoice-1");

        Assert.IsType<ForbidResult>(result);
        invoices.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(nameof(ReactLocalSupplierInvoicesController.Grid))]
    [InlineData(nameof(ReactLocalSupplierInvoicesController.GetInvoice))]
    [InlineData(nameof(ReactLocalSupplierInvoicesController.GetDetails))]
    [InlineData(nameof(ReactLocalSupplierInvoicesController.GetDetailsGrid))]
    public async Task LegacyReadEndpoints_OrderFrontPermissionRemainsForbidden(string methodName)
    {
        var invoices = new Mock<ILocalSupplierInvoicesReactService>(MockBehavior.Strict);
        var controller = CreateController(
            invoices.Object,
            CreateAuthorizationService(Permissions.OrderFront.View).Object,
            isAdmin: true
        );

        var result = await InvokeReadEndpointAsync(controller, methodName);

        Assert.IsType<ForbidResult>(result);
        invoices.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(nameof(ReactLocalSupplierInvoicesController.GetBarcodeAbnormalDetails))]
    [InlineData(nameof(ReactLocalSupplierInvoicesController.GetProductsByBarcode))]
    [InlineData(nameof(ReactLocalSupplierInvoicesController.GetProductsByProductCode))]
    public async Task SpecialReadEndpoints_OrderFrontPermissionRemainsForbidden(string methodName)
    {
        var invoices = new Mock<ILocalSupplierInvoicesReactService>(MockBehavior.Strict);
        var controller = CreateController(
            invoices.Object,
            CreateAuthorizationService(Permissions.OrderFront.View).Object,
            isAdmin: true
        );

        var result = await InvokeReadEndpointAsync(controller, methodName);

        Assert.IsType<ForbidResult>(result);
        invoices.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(nameof(ReactLocalSupplierInvoicesController.GetBarcodeAbnormalDetails))]
    [InlineData(nameof(ReactLocalSupplierInvoicesController.GetProductsByBarcode))]
    [InlineData(nameof(ReactLocalSupplierInvoicesController.GetProductsByProductCode))]
    public async Task SpecialReadEndpoints_WarehouseStaffOrdersCreateRemainsForbidden(
        string methodName
    )
    {
        var invoices = new Mock<ILocalSupplierInvoicesReactService>(MockBehavior.Strict);
        var controller = CreateController(
            invoices.Object,
            CreateAuthorizationService(Permissions.Orders.Create).Object,
            isAdmin: true,
            roleNames: new[] { "WarehouseStaff" }
        );

        var result = await InvokeReadEndpointAsync(controller, methodName);

        Assert.IsType<ForbidResult>(result);
        invoices.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task FilterOptions_OrderFrontUserScopesSuppliersToAssignedStores()
    {
        await SeedScopedInvoiceAsync();
        var expected = new LocalSupplierInvoiceFilterOptionsDto
        {
            Suppliers = new List<LocalSupplierInvoiceFilterOptionDto>
            {
                new() { Value = "SUP01", Label = "Supplier One" },
            },
        };
        var invoices = new Mock<ILocalSupplierInvoicesReactService>(MockBehavior.Strict);
        invoices
            .Setup(service =>
                service.GetFilterOptionsAsync(
                    It.Is<List<string>?>(codes =>
                        codes != null && codes.SequenceEqual(new[] { "S01" })
                    ),
                    null
                )
            )
            .ReturnsAsync(ApiResponse<LocalSupplierInvoiceFilterOptionsDto>.OK(expected));
        var controller = CreateController(
            invoices.Object,
            CreateAuthorizationService(Permissions.OrderFront.View).Object
        );

        var result = await controller.GetFilterOptions(null);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Same(expected, ok.Value?.GetType().GetProperty("data")?.GetValue(ok.Value));
        invoices.VerifyAll();
    }

    [Fact]
    public async Task FilterOptions_OrderFrontUserCannotSelectAnUnassignedStore()
    {
        await SeedScopedInvoiceAsync();
        var invoices = new Mock<ILocalSupplierInvoicesReactService>(MockBehavior.Strict);
        var controller = CreateController(
            invoices.Object,
            CreateAuthorizationService(Permissions.OrderFront.View).Object
        );

        var result = await controller.GetFilterOptions("S02");

        Assert.IsType<ForbidResult>(result);
        invoices.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(nameof(ReactLocalSupplierInvoicesController.Grid))]
    [InlineData(nameof(ReactLocalSupplierInvoicesController.GetInvoice))]
    [InlineData(nameof(ReactLocalSupplierInvoicesController.GetDetails))]
    [InlineData(nameof(ReactLocalSupplierInvoicesController.GetDetailsGrid))]
    [InlineData(nameof(ReactLocalSupplierInvoicesController.ShopGrid))]
    [InlineData(nameof(ReactLocalSupplierInvoicesController.GetShopInvoice))]
    [InlineData(nameof(ReactLocalSupplierInvoicesController.GetShopDetailsGrid))]
    [InlineData(nameof(ReactLocalSupplierInvoicesController.GetFilterOptions))]
    [InlineData(nameof(ReactLocalSupplierInvoicesController.GetBarcodeAbnormalDetails))]
    [InlineData(nameof(ReactLocalSupplierInvoicesController.GetProductsByBarcode))]
    [InlineData(nameof(ReactLocalSupplierInvoicesController.GetProductsByProductCode))]
    public async Task ReadEndpoints_WithoutEitherReadPermissionForbidBeforeBypassingStoreScope(
        string methodName
    )
    {
        var invoices = new Mock<ILocalSupplierInvoicesReactService>(MockBehavior.Strict);
        var controller = CreateController(
            invoices.Object,
            CreateAuthorizationService().Object,
            isAdmin: true
        );

        var result = await InvokeReadEndpointAsync(controller, methodName);

        Assert.IsType<ForbidResult>(result);
        invoices.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(nameof(ReactLocalSupplierInvoicesController.Grid))]
    [InlineData(nameof(ReactLocalSupplierInvoicesController.GetInvoice))]
    [InlineData(nameof(ReactLocalSupplierInvoicesController.GetDetails))]
    [InlineData(nameof(ReactLocalSupplierInvoicesController.GetDetailsGrid))]
    [InlineData(nameof(ReactLocalSupplierInvoicesController.ShopGrid))]
    [InlineData(nameof(ReactLocalSupplierInvoicesController.GetShopInvoice))]
    [InlineData(nameof(ReactLocalSupplierInvoicesController.GetShopDetailsGrid))]
    [InlineData(nameof(ReactLocalSupplierInvoicesController.GetFilterOptions))]
    [InlineData(nameof(ReactLocalSupplierInvoicesController.GetBarcodeAbnormalDetails))]
    [InlineData(nameof(ReactLocalSupplierInvoicesController.GetProductsByBarcode))]
    [InlineData(nameof(ReactLocalSupplierInvoicesController.GetProductsByProductCode))]
    public void ReadEndpoints_DoNotUseTheSingleWebViewPolicy(string methodName)
    {
        var method = typeof(ReactLocalSupplierInvoicesController).GetMethod(methodName)!;

        Assert.DoesNotContain(
            method.GetCustomAttributes<AuthorizeAttribute>(inherit: false),
            attribute => !string.IsNullOrWhiteSpace(attribute.Policy)
        );
    }

    [Fact]
    public void MobileView_DoesNotBecomeAnAliasForWebWriteHqOrAnalysisPermissions()
    {
        const string mobileView = "LocalPurchase.MobileView";

        Assert.DoesNotContain(mobileView, Permissions.GetEquivalentPermissionCodes(Permissions.LocalPurchase.View));
        Assert.DoesNotContain(mobileView, Permissions.GetEquivalentPermissionCodes(Permissions.LocalPurchase.Edit));
        Assert.DoesNotContain(mobileView, Permissions.GetEquivalentPermissionCodes(Permissions.LocalPurchase.PushToHq));
        // 分析控制器类级只要求登录；按单据分析的 action 仍声明 LocalPurchase.View，MobileView 不能成为它的别名。
        var analysisController = typeof(ReactLocalSupplierInvoiceSalesAnalysisController);
        var analysisClassAuthorize = Assert.Single(
            analysisController.GetCustomAttributes<AuthorizeAttribute>(inherit: false)
        );
        Assert.True(string.IsNullOrWhiteSpace(analysisClassAuthorize.Policy));
        var invoiceAnalysisAuthorize = Assert.Single(
            analysisController
                .GetMethod(nameof(ReactLocalSupplierInvoiceSalesAnalysisController.GetSalesAnalysis))!
                .GetCustomAttributes<AuthorizeAttribute>(inherit: false)
        );
        Assert.Equal(Permissions.LocalPurchase.View, invoiceAnalysisAuthorize.Policy);

        // 后台进货销量分析各 action 在方法内校验（销售看板新权限码或 LocalPurchase.View），
        // 特性上不再挂策略；可接受的权限码里同样不包含 MobileView（行为由分析控制器测试覆盖）。
        foreach (
            var actionName in new[]
            {
                nameof(ReactLocalSupplierInvoiceSalesAnalysisController.GetPurchaseSalesAnalysis),
                nameof(ReactLocalSupplierInvoiceSalesAnalysisController.GetPurchaseSalesAnalysisStoreOptions),
                nameof(ReactLocalSupplierInvoiceSalesAnalysisController.GetPurchaseSalesAnalysisSupplierOptions),
                nameof(ReactLocalSupplierInvoiceSalesAnalysisController.GetPurchaseSalesAnalysisCategoryTree),
            }
        )
        {
            Assert.DoesNotContain(
                analysisController
                    .GetMethod(actionName)!
                    .GetCustomAttributes<AuthorizeAttribute>(inherit: false),
                attribute => !string.IsNullOrWhiteSpace(attribute.Policy)
            );
        }
    }

    [Theory]
    [InlineData(SetChildPurchasePriceMutationLock.BusyErrorCode)]
    [InlineData("STORE_UPDATE_COST_LOCK_BUSY")]
    public async Task UpdateToStorePrices_成本业务锁冲突返回409和Busy错误码(string busyCode)
    {
        var invoices = new Mock<ILocalSupplierInvoicesReactService>(MockBehavior.Strict);
        invoices
            .Setup(service => service.UpdateDetailsToStorePricesAsync(
                It.IsAny<UpdateToStorePricesRequest>(),
                "system"
            ))
            .ReturnsAsync(ApiResponse<UpdateToStorePricesResultDto>.Error(
                "套装子项成本正在更新，请稍后重试",
                busyCode
            ));
        var controller = CreateController(invoices.Object, authorizationService: null, isAdmin: true);

        var actionResult = await controller.UpdateToStorePrices(new UpdateToStorePricesRequest
        {
            InvoiceGuid = "invoice-busy",
            DetailGuids = new List<string>(),
            TargetStoreCodes = new List<string> { "S01" },
            UpdateFields = new UpdateToStorePricesFields(),
        });

        var conflict = Assert.IsType<ObjectResult>(actionResult);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
        Assert.Equal(
            busyCode,
            conflict.Value?.GetType().GetProperty("code")?.GetValue(conflict.Value)
        );
        invoices.VerifyAll();
    }

    [Fact]
    public async Task BatchExecuteActions_成本业务锁冲突返回409和Busy错误码()
    {
        var invoices = new Mock<ILocalSupplierInvoicesReactService>(MockBehavior.Strict);
        invoices
            .Setup(service => service.BatchExecuteActionsAsync(
                "invoice-busy",
                It.Is<List<string>>(items => items.Count == 0),
                "system",
                It.Is<List<BatchExecuteNewProductProductTypeSelectionDto>?>(items =>
                    items != null && items.Count == 0
                ),
                It.Is<List<BatchExecuteExpectedActionDto>?>(items =>
                    items != null && items.Count == 0
                ),
                It.Is<IReadOnlyCollection<StoreLocalSupplierInvoiceDetails>?>(items =>
                    items != null && items.Count == 0
                )
            ))
            .ReturnsAsync(ApiResponse<BatchExecuteActionsResultDto>.Error(
                "套装子项成本正在更新，请稍后重试",
                SetChildPurchasePriceMutationLock.BusyErrorCode
            ));
        var controller = CreateController(invoices.Object, authorizationService: null, isAdmin: true);

        var actionResult = await controller.BatchExecuteActions(
            "invoice-busy",
            new BatchExecuteActionsRequestDto { DetailGuids = new List<string>() }
        );

        var conflict = Assert.IsType<ObjectResult>(actionResult);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
        Assert.Equal(
            SetChildPurchasePriceMutationLock.BusyErrorCode,
            conflict.Value?.GetType().GetProperty("code")?.GetValue(conflict.Value)
        );
        invoices.VerifyAll();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private async Task SeedScopedInvoiceAsync()
    {
        await _db.Insertable(
            new Store
            {
                StoreGUID = "store-1",
                StoreCode = "S01",
                StoreName = "Sydney",
                IsActive = true,
                IsDeleted = false,
            }
        ).ExecuteCommandAsync();
        await _db.Insertable(
            new UserStore
            {
                UserStoreGUID = "user-store-1",
                UserGUID = "user-1",
                StoreGUID = "store-1",
            }
        ).ExecuteCommandAsync();
        await _db.Insertable(
            new StoreLocalSupplierInvoice
            {
                InvoiceGUID = "invoice-1",
                StoreCode = "S01",
                IsDeleted = false,
            }
        ).ExecuteCommandAsync();
    }

    private ReactLocalSupplierInvoicesController CreateController(
        ILocalSupplierInvoicesReactService invoices,
        IAuthorizationService? authorizationService,
        bool isAdmin = false,
        IReadOnlyList<string>? roleNames = null
    )
    {
        var controller = new ReactLocalSupplierInvoicesController(
            invoices,
            CreateSqlSugarContext(_db),
            Mock.Of<ILocalSupplierInvoiceHqSyncService>(),
            Mock.Of<ILocalSupplierInvoiceImportService>(),
            authorizationService: authorizationService
        );
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(
                    new ClaimsIdentity(
                        new[] { new Claim("userId", "user-1") }
                            .Concat(
                                isAdmin
                                    ? new[] { new Claim(ClaimTypes.Role, "Admin") }
                                    : Array.Empty<Claim>()
                            )
                            .Concat(
                                (roleNames ?? Array.Empty<string>())
                                    .Select(role => new Claim(ClaimTypes.Role, role))
                            ),
                        "TestAuth"
                    )
                ),
            },
        };
        return controller;
    }

    private static Task<IActionResult> InvokeReadEndpointAsync(
        ReactLocalSupplierInvoicesController controller,
        string methodName
    )
    {
        return methodName switch
        {
            nameof(ReactLocalSupplierInvoicesController.Grid) => controller.Grid(new GridRequestDto()),
            nameof(ReactLocalSupplierInvoicesController.GetInvoice) => controller.GetInvoice("invoice-1"),
            nameof(ReactLocalSupplierInvoicesController.GetDetails) => controller.GetDetails("invoice-1"),
            nameof(ReactLocalSupplierInvoicesController.GetDetailsGrid) => controller.GetDetailsGrid(
                "invoice-1",
                new GridRequestDto()
            ),
            nameof(ReactLocalSupplierInvoicesController.ShopGrid) =>
                controller.ShopGrid(new GridRequestDto()),
            nameof(ReactLocalSupplierInvoicesController.GetShopInvoice) =>
                controller.GetShopInvoice("invoice-1"),
            nameof(ReactLocalSupplierInvoicesController.GetShopDetailsGrid) =>
                controller.GetShopDetailsGrid("invoice-1", new GridRequestDto()),
            nameof(ReactLocalSupplierInvoicesController.GetFilterOptions) =>
                controller.GetFilterOptions(null),
            nameof(ReactLocalSupplierInvoicesController.GetBarcodeAbnormalDetails) =>
                controller.GetBarcodeAbnormalDetails("invoice-1"),
            nameof(ReactLocalSupplierInvoicesController.GetProductsByBarcode) =>
                controller.GetProductsByBarcode("invoice-1", "barcode"),
            nameof(ReactLocalSupplierInvoicesController.GetProductsByProductCode) =>
                controller.GetProductsByProductCode("invoice-1", "product-code"),
            _ => throw new ArgumentOutOfRangeException(nameof(methodName), methodName, null),
        };
    }

    private static void SetupSuccessfulShopRead(
        Mock<ILocalSupplierInvoicesReactService> invoices,
        string methodName
    )
    {
        switch (methodName)
        {
            case nameof(ReactLocalSupplierInvoicesController.ShopGrid):
                invoices
                    .Setup(service =>
                        service.GetGridDataAsync(
                            It.IsAny<GridRequestDto>(),
                            It.Is<List<string>?>(codes =>
                                codes != null && codes.SequenceEqual(new[] { "S01" })
                            )
                        )
                    )
                    .ReturnsAsync(
                        GridResponseDto<LocalSupplierInvoiceListDto>.OK(
                            new List<LocalSupplierInvoiceListDto>(),
                            0
                        )
                    );
                break;
            case nameof(ReactLocalSupplierInvoicesController.GetShopInvoice):
                invoices
                    .Setup(service => service.GetInvoiceAsync("invoice-1"))
                    .ReturnsAsync(
                        ApiResponse<LocalSupplierInvoiceDetailDto>.OK(
                            new LocalSupplierInvoiceDetailDto()
                        )
                    );
                break;
            case nameof(ReactLocalSupplierInvoicesController.GetShopDetailsGrid):
                invoices
                    .Setup(service =>
                        service.GetDetailsGridAsync("invoice-1", It.IsAny<GridRequestDto>())
                    )
                    .ReturnsAsync(
                        GridResponseDto<LocalSupplierInvoiceItemDto>.OK(
                            new List<LocalSupplierInvoiceItemDto>(),
                            0
                        )
                    );
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(methodName), methodName, null);
        }
    }

    private static Mock<IAuthorizationService> CreateAuthorizationService(
        params string[] allowedPolicies
    )
    {
        var allowed = new HashSet<string>(allowedPolicies, StringComparer.OrdinalIgnoreCase);
        var authorization = new Mock<IAuthorizationService>(MockBehavior.Strict);
        authorization
            .Setup(service => service.AuthorizeAsync(
                It.IsAny<ClaimsPrincipal>(),
                It.IsAny<object?>(),
                It.IsAny<string>()))
            .ReturnsAsync((ClaimsPrincipal _, object? _, string policy) =>
                allowed.Contains(policy) ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        return authorization;
    }

    private static SqlSugarContext CreateSqlSugarContext(ISqlSugarClient db)
    {
        var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(SqlSugarContext));
        var dbField = typeof(SqlSugarContext).GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!;
        dbField.SetValue(context, db);
        return context;
    }
}
