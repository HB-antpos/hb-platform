using System.Reflection;
using System.Security.Claims;
using BlazorApp.Api.Authentication;
using BlazorApp.Api.Controllers;
using BlazorApp.Api.Controllers.React;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BlazorApp.Api.Tests;

public class ControllerAuthorizationMetadataTests
{
    private const string LocalPurchasePushToHq = "LocalPurchase.PushToHq";

    public static IEnumerable<object[]> SalesDashboardPagePolicies()
    {
        yield return new object[]
        {
            typeof(RevenueReportSnapshotController),
            nameof(RevenueReportSnapshotController.GetRevenueReportSnapshot),
            "SalesDashboard.SalesData.View",
        };
        yield return new object[]
        {
            typeof(SalesDetailReportController),
            nameof(SalesDetailReportController.GetSalesDetailReport),
            "SalesDashboard.SalesDetail.View",
        };
        yield return new object[]
        {
            typeof(SalesDashboardController),
            nameof(SalesDashboardController.GetCompactSalesBoard),
            "SalesDashboard.CompactBoard.View",
        };
        yield return new object[]
        {
            typeof(ProductMovementReportController),
            nameof(ProductMovementReportController.GetReport),
            "SalesDashboard.ProductMovement.View",
        };
        yield return new object[]
        {
            typeof(WarehouseProductFlowAnalysisController),
            nameof(WarehouseProductFlowAnalysisController.GetSummary),
            "SalesDashboard.WarehouseFlow.View",
        };
        yield return new object[]
        {
            typeof(LocalSupplierProductSalesAnalysisController),
            nameof(LocalSupplierProductSalesAnalysisController.Summary),
            "SalesDashboard.LocalProductAnalysis.View",
        };
        yield return new object[]
        {
            typeof(ReactLocalPurchaseDashboardController),
            nameof(ReactLocalPurchaseDashboardController.GetDashboard),
            "SalesDashboard.PurchaseAmount.View",
        };
    }

    [Theory]
    [MemberData(nameof(SalesDashboardPagePolicies))]
    public void SalesDashboardPages_RequireIndependentPermission(
        Type controllerType,
        string methodName,
        string expectedPolicy
    )
    {
        var authorizeAttributes = controllerType
            .GetCustomAttributes<AuthorizeAttribute>(inherit: false)
            .Concat(
                GetDeclaredMethod(controllerType, methodName)
                    .GetCustomAttributes<AuthorizeAttribute>(inherit: false)
            )
            .ToList();

        Assert.Contains(authorizeAttributes, attribute => attribute.Policy == expectedPolicy);
        Assert.DoesNotContain(
            authorizeAttributes,
            attribute => attribute.Policy is Permissions.Reports.View
                or Permissions.Reports.ProductMovementView
                or Permissions.LocalPurchase.View
        );
    }

    [Theory]
    [InlineData(typeof(RevenueReportSnapshotController), "SalesDashboard.SalesData.View")]
    [InlineData(typeof(SalesDetailReportController), "SalesDashboard.SalesDetail.View")]
    [InlineData(typeof(ProductMovementReportController), "SalesDashboard.ProductMovement.View")]
    [InlineData(typeof(LocalSupplierProductSalesAnalysisController), "SalesDashboard.LocalProductAnalysis.View")]
    [InlineData(typeof(ReactLocalPurchaseDashboardController), "SalesDashboard.PurchaseAmount.View")]
    public void SalesDashboardPageControllers_DoNotRetainLegacyOrConflictingPolicies(
        Type controllerType,
        string expectedPolicy
    )
    {
        var policies = controllerType
            .GetCustomAttributes<AuthorizeAttribute>(inherit: false)
            .Concat(
                controllerType
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                    .SelectMany(method =>
                        method.GetCustomAttributes<AuthorizeAttribute>(inherit: false)
                    )
            )
            .Select(attribute => attribute.Policy)
            .Where(policy => !string.IsNullOrWhiteSpace(policy))
            .ToList();

        Assert.NotEmpty(policies);
        Assert.All(policies, policy => Assert.Equal(expectedPolicy, policy));
    }

    [Fact]
    public void WarehouseProductFlowAnalysis_AllHttpActionsRequireIndependentPermission()
    {
        var controllerType = typeof(WarehouseProductFlowAnalysisController);
        var httpActions = controllerType
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(method =>
                method.GetCustomAttributes<HttpMethodAttribute>(inherit: false).Any()
            )
            .ToList();

        Assert.NotEmpty(httpActions);
        Assert.All(
            httpActions,
            method =>
            {
                var policies = method
                    .GetCustomAttributes<AuthorizeAttribute>(inherit: false)
                    .Select(attribute => attribute.Policy)
                    .ToList();

                Assert.Contains(Permissions.SalesDashboard.WarehouseFlowView, policies);
                Assert.DoesNotContain(Permissions.Reports.View, policies);
                Assert.DoesNotContain(Permissions.Reports.ProductMovementView, policies);
                Assert.DoesNotContain(Permissions.LocalPurchase.View, policies);
            }
        );
    }

    [Fact]
    public void LegacyProductSalesAnalysisEndpoint_KeepsExistingReportPermission()
    {
        var method = GetDeclaredMethod(
            typeof(SalesDashboardController),
            nameof(SalesDashboardController.GetProductSalesAnalysisOptions)
        );

        Assert.Contains(
            method.GetCustomAttributes<AuthorizeAttribute>(inherit: false),
            attribute => attribute.Policy == Permissions.Reports.ProductMovementView
        );
    }

    public static IEnumerable<object[]> MenuBackedEndpointPolicies()
    {
        yield return Policy<ReactStoreUsersController>(
            nameof(ReactStoreUsersController.Grid),
            Permissions.Users.View
        );
        yield return Policy<ReactStoreUsersController>(
            nameof(ReactStoreUsersController.GetByUserGuid),
            Permissions.Users.View
        );
        yield return Policy<ReactStoreUsersController>(
            nameof(ReactStoreUsersController.GetProfile),
            Permissions.Users.View
        );
        yield return Policy<ReactStoreUsersController>(
            nameof(ReactStoreUsersController.Create),
            Permissions.Users.Create
        );
        yield return Policy<ReactStoreUsersController>(
            nameof(ReactStoreUsersController.Update),
            Permissions.Users.Edit
        );
        yield return Policy<ReactStoreUsersController>(
            nameof(ReactStoreUsersController.UpdateStatus),
            Permissions.Users.Edit
        );
        yield return Policy<ReactStoreUsersController>(
            nameof(ReactStoreUsersController.UpdatePassword),
            Permissions.Users.ResetPassword
        );

        yield return Policy<ReactPromotionsController>(
            nameof(ReactPromotionsController.Grid),
            Permissions.Promotions.View
        );
        yield return Policy<ReactPromotionsController>(
            nameof(ReactPromotionsController.Get),
            Permissions.Promotions.View
        );
        yield return Policy<ReactPromotionsController>(
            nameof(ReactPromotionsController.StoreGrid),
            Permissions.Promotions.View
        );
        yield return Policy<ReactPromotionsController>(
            nameof(ReactPromotionsController.GetStorePromotion),
            Permissions.Promotions.View
        );
        yield return Policy<ReactPromotionsController>(
            nameof(ReactPromotionsController.CreateStorePromotion),
            Permissions.Promotions.Edit
        );
        yield return Policy<ReactPromotionsController>(
            nameof(ReactPromotionsController.UpdateStorePromotion),
            Permissions.Promotions.Edit
        );
        yield return Policy<ReactPromotionsController>(
            nameof(ReactPromotionsController.CopyToStore),
            Permissions.Promotions.Edit
        );
        yield return Policy<ReactPromotionsController>(
            nameof(ReactPromotionsController.EnableStorePromotion),
            Permissions.Promotions.Edit
        );

        yield return Policy<ReactLocalSupplierInvoicesController>(
            nameof(ReactLocalSupplierInvoicesController.CheckProducts),
            Permissions.LocalPurchase.Edit
        );
        yield return Policy<ReactLocalSupplierInvoicesController>(
            nameof(ReactLocalSupplierInvoicesController.BatchExecuteActions),
            Permissions.LocalPurchase.Edit
        );
        yield return Policy<ReactLocalSupplierInvoicesController>(
            nameof(ReactLocalSupplierInvoicesController.BatchUpdateDetailAction),
            Permissions.LocalPurchase.Edit
        );
        yield return Policy<ReactLocalSupplierInvoicesController>(
            nameof(ReactLocalSupplierInvoicesController.BatchUpdateDetails),
            Permissions.LocalPurchase.Edit
        );
        yield return Policy<ReactLocalSupplierInvoicesController>(
            nameof(ReactLocalSupplierInvoicesController.PushInvoicesToHq),
            LocalPurchasePushToHq
        );
        yield return Policy<ReactLocalSupplierInvoicesController>(
            nameof(ReactLocalSupplierInvoicesController.UpdateHqProducts),
            Permissions.LocalPurchase.Edit
        );
        yield return Policy<ReactLocalSupplierInvoicesController>(
            nameof(ReactLocalSupplierInvoicesController.UpdateHqProducts),
            LocalPurchasePushToHq
        );
        yield return Policy<ReactLocalSupplierInvoicesController>(
            nameof(ReactLocalSupplierInvoicesController.StartUpdateToStorePricesJob),
            Permissions.LocalPurchase.Edit
        );
        yield return Policy<ReactLocalSupplierInvoicesController>(
            nameof(ReactLocalSupplierInvoicesController.GetUpdateToStorePricesJob),
            Permissions.LocalPurchase.Edit
        );
        yield return Policy<ReactLocalSupplierInvoicesController>(
            nameof(ReactLocalSupplierInvoicesController.StartUpdateHqProductsJob),
            Permissions.LocalPurchase.Edit
        );
        yield return Policy<ReactLocalSupplierInvoicesController>(
            nameof(ReactLocalSupplierInvoicesController.StartUpdateHqProductsJob),
            LocalPurchasePushToHq
        );
        yield return Policy<ReactLocalSupplierInvoicesController>(
            nameof(ReactLocalSupplierInvoicesController.GetUpdateHqProductsJob),
            Permissions.LocalPurchase.Edit
        );
        yield return Policy<ReactLocalSupplierInvoicesController>(
            nameof(ReactLocalSupplierInvoicesController.GetUpdateHqProductsJob),
            LocalPurchasePushToHq
        );
        yield return Policy<ScheduledTaskRuntimeControlController>(
            nameof(ScheduledTaskRuntimeControlController.GetStatus),
            Permissions.System.ViewLogs
        );
        yield return Policy<ScheduledTaskRuntimeControlController>(
            nameof(ScheduledTaskRuntimeControlController.Update),
            Permissions.System.ManageScheduledTasks
        );
        yield return Policy<ReactInvoiceEmailSettingsController>(
            nameof(ReactInvoiceEmailSettingsController.Get),
            Permissions.System.ManageSettings
        );
        yield return Policy<ReactInvoiceEmailSettingsController>(
            nameof(ReactInvoiceEmailSettingsController.Update),
            Permissions.System.ManageSettings
        );
        yield return Policy<ReactInvoiceEmailSettingsController>(
            nameof(ReactInvoiceEmailSettingsController.Test),
            Permissions.System.ManageSettings
        );
        yield return Policy<MobileAppBuildsController>(
            nameof(MobileAppBuildsController.Latest),
            Permissions.System.ViewAppDownloads
        );
        yield return Policy<MobileAppBuildsController>(
            nameof(MobileAppBuildsController.History),
            Permissions.System.ViewAppDownloads
        );
        yield return Policy<MobileAppBuildsController>(
            nameof(MobileAppBuildsController.GetOtaUpdates),
            Permissions.System.ViewAppDownloads
        );
        yield return Policy<MobileAppBuildsController>(
            nameof(MobileAppBuildsController.UpsertOtaUpdate),
            Permissions.System.ManageAppDownloads
        );
        yield return Policy<MobileAppBuildsController>(
            nameof(MobileAppBuildsController.CreateOtaRollbackCommand),
            Permissions.System.ManageAppDownloads
        );
        yield return Policy<MobileAppInstallLinksController>(
            nameof(MobileAppInstallLinksController.Get),
            Permissions.System.ViewMobileAppInstallLinks
        );
        yield return Policy<MobileAppDeviceStatusController>(
            nameof(MobileAppDeviceStatusController.GetPaged),
            Permissions.DeviceRegistration.View
        );
        yield return Policy<MobileAppDeviceStatusController>(
            nameof(MobileAppDeviceStatusController.GetSummary),
            Permissions.DeviceRegistration.View
        );
        yield return Policy<SalesDashboardController>(
            nameof(SalesDashboardController.GetExecutiveBranchPerformance),
            Permissions.Reports.ProductMovementView
        );
        yield return Policy<SalesDashboardController>(
            nameof(SalesDashboardController.GetSupplierSalesRank),
            Permissions.Reports.ProductMovementView
        );
        yield return Policy<SalesDashboardController>(
            nameof(SalesDashboardController.GetChinaSupplierSalesRankAsync),
            Permissions.Reports.ProductMovementView
        );
        yield return Policy<SalesDashboardController>(
            nameof(SalesDashboardController.GetChinaSupplierBranchTotalsAsync),
            Permissions.Reports.ProductMovementView
        );
        yield return Policy<SalesDashboardController>(
            nameof(SalesDashboardController.GetSupplierStoreSales),
            Permissions.Reports.ProductMovementView
        );
        yield return Policy<SalesDashboardController>(
            nameof(SalesDashboardController.GetChinaSupplierStoreSales),
            Permissions.Reports.ProductMovementView
        );
        yield return Policy<SalesDashboardController>(
            nameof(SalesDashboardController.GetAustralianSupplierStoreSalesDetails),
            Permissions.Reports.ProductMovementView
        );
        yield return Policy<SalesDashboardController>(
            nameof(SalesDashboardController.GetChinaSupplierStoreSalesDetails),
            Permissions.Reports.ProductMovementView
        );
        yield return Policy<SalesDashboardController>(
            nameof(SalesDashboardController.GetEnhancedSalesProductDetails),
            Permissions.Reports.ProductMovementView
        );
        yield return Policy<SalesDashboardController>(
            nameof(SalesDashboardController.GetCompactSalesBoard),
            Permissions.SalesDashboard.CompactBoardView
        );
        yield return Policy<SalesDashboardController>(
            nameof(SalesDashboardController.GetProductSalesByAllBranches),
            Permissions.Reports.ProductMovementView
        );
        yield return Policy<SalesDashboardController>(
            nameof(SalesDashboardController.GetExecutiveHourlyTraffic),
            Permissions.Reports.View
        );
        yield return Policy<SalesDashboardController>(
            nameof(SalesDashboardController.GetBranchDailyPerformance),
            Permissions.Reports.View
        );

        // 定价策略：读写分别落到 PricingStrategy.View / Edit，防止仅凭登录态改动分店售价规则。
        yield return Policy<ReactPricingStrategiesController>(
            nameof(ReactPricingStrategiesController.Grid),
            Permissions.PricingStrategy.View
        );
        yield return Policy<ReactPricingStrategiesController>(
            nameof(ReactPricingStrategiesController.Get),
            Permissions.PricingStrategy.View
        );
        yield return Policy<ReactPricingStrategiesController>(
            nameof(ReactPricingStrategiesController.Evaluate),
            Permissions.PricingStrategy.View
        );
        yield return Policy<ReactPricingStrategiesController>(
            nameof(ReactPricingStrategiesController.Create),
            Permissions.PricingStrategy.Edit
        );
        yield return Policy<ReactPricingStrategiesController>(
            nameof(ReactPricingStrategiesController.Update),
            Permissions.PricingStrategy.Edit
        );
        yield return Policy<ReactPricingStrategiesController>(
            nameof(ReactPricingStrategiesController.Delete),
            Permissions.PricingStrategy.Edit
        );

        // 澳洲供应商：分页列表按 View，写操作按 Edit；active / check-code 为公共下拉源不设策略。
        yield return Policy<LocalSuppliersController>(
            nameof(LocalSuppliersController.GetList),
            Permissions.AustralianSuppliers.View
        );
        yield return Policy<LocalSuppliersController>(
            nameof(LocalSuppliersController.Sync),
            Permissions.AustralianSuppliers.Edit
        );
        yield return Policy<LocalSuppliersController>(
            nameof(LocalSuppliersController.SyncToHq),
            Permissions.AustralianSuppliers.Edit
        );
        yield return Policy<LocalSuppliersController>(
            nameof(LocalSuppliersController.Create),
            Permissions.AustralianSuppliers.Edit
        );
        yield return Policy<LocalSuppliersController>(
            nameof(LocalSuppliersController.Update),
            Permissions.AustralianSuppliers.Edit
        );
        yield return Policy<LocalSuppliersController>(
            nameof(LocalSuppliersController.Delete),
            Permissions.AustralianSuppliers.Edit
        );
        yield return Policy<LocalSuppliersController>(
            nameof(LocalSuppliersController.ToggleStatus),
            Permissions.AustralianSuppliers.Edit
        );

        // Web 收银记录：list / detail / tax-invoice 与菜单权限 Orders.View 对齐。
        yield return Policy<PosmSalesOrderController>(
            nameof(PosmSalesOrderController.GetSalesOrderList),
            Permissions.Orders.View
        );
        yield return Policy<PosmSalesOrderController>(
            nameof(PosmSalesOrderController.GetSalesOrderDetail),
            Permissions.Orders.View
        );
        yield return Policy<PosmSalesOrderController>(
            nameof(PosmSalesOrderController.GetTaxInvoicePdf),
            Permissions.Orders.View
        );
        yield return Policy<ServiceApiTokensController>(
            nameof(ServiceApiTokensController.List),
            Permissions.System.ManageAppDownloads
        );
        yield return Policy<ServiceApiTokensController>(
            nameof(ServiceApiTokensController.Create),
            Permissions.System.ManageAppDownloads
        );
        yield return Policy<ServiceApiTokensController>(
            nameof(ServiceApiTokensController.Revoke),
            Permissions.System.ManageAppDownloads
        );
        yield return Policy<DomesticProductsController>(
            nameof(DomesticProductsController.GetDomesticProducts),
            Permissions.Products.View
        );
        yield return Policy<DomesticProductsController>(
            nameof(DomesticProductsController.GetDomesticProductsAdvanced),
            Permissions.Products.View
        );
        yield return Policy<DomesticProductsController>(
            nameof(DomesticProductsController.GetFieldInfo),
            Permissions.Products.View
        );
        yield return Policy<DomesticProductsController>(
            nameof(DomesticProductsController.GetDomesticProductByCode),
            Permissions.Products.View
        );
        yield return Policy<DomesticProductsController>(
            nameof(DomesticProductsController.GetProductsBySupplierCode),
            Permissions.Products.View
        );
        yield return Policy<DomesticProductsController>(
            nameof(DomesticProductsController.GetActiveProducts),
            Permissions.Products.View
        );
        yield return Policy<DomesticProductsController>(
            nameof(DomesticProductsController.GetProductTypeStatistics),
            Permissions.Products.View
        );
        yield return Policy<DomesticProductsController>(
            nameof(DomesticProductsController.GetProductPriceStatistics),
            Permissions.Products.View
        );
        yield return Policy<DomesticProductsController>(
            nameof(DomesticProductsController.GetGridData),
            Permissions.Products.View
        );
        yield return Policy<DomesticProductsController>(
            nameof(DomesticProductsController.GetSetItems),
            Permissions.Products.View
        );
        yield return Policy<DomesticProductsController>(
            nameof(DomesticProductsController.CreateDomesticProduct),
            Permissions.Products.Create
        );
        yield return Policy<DomesticProductsController>(
            nameof(DomesticProductsController.UpdateDomesticProduct),
            Permissions.Products.Edit
        );
        yield return Policy<DomesticProductsController>(
            nameof(DomesticProductsController.ToggleProductStatus),
            Permissions.Products.Edit
        );
        yield return Policy<DomesticProductsController>(
            nameof(DomesticProductsController.BatchUpdateProductStatus),
            Permissions.Products.Edit
        );
        yield return Policy<DomesticProductsController>(
            nameof(DomesticProductsController.FixDuplicateImageUrls),
            Permissions.Products.Edit
        );
        yield return Policy<DomesticProductsController>(
            nameof(DomesticProductsController.UpdateSetItems),
            Permissions.Products.Edit
        );
        yield return Policy<DomesticProductsController>(
            nameof(DomesticProductsController.DeleteDomesticProduct),
            Permissions.Products.Delete
        );
        yield return Policy<DomesticProductsController>(
            nameof(DomesticProductsController.BatchDeleteDomesticProducts),
            Permissions.Products.Delete
        );
        yield return Policy<DomesticProductsController>(
            nameof(DomesticProductsController.BatchDelete),
            Permissions.Products.Delete
        );
        yield return Policy<DomesticProductsController>(
            nameof(DomesticProductsController.CheckHBProductNoExists),
            Permissions.DomesticPurchase.ManageProducts
        );
        yield return Policy<DomesticProductsController>(
            nameof(DomesticProductsController.CheckBarcodeExists),
            Permissions.DomesticPurchase.ManageProducts
        );
        yield return Policy<DomesticProductsController>(
            nameof(DomesticProductsController.GenerateNextProductNo),
            Permissions.DomesticPurchase.ManageProducts
        );
        yield return Policy<DomesticProductsController>(
            nameof(DomesticProductsController.GenerateProductBarcode),
            Permissions.DomesticPurchase.ManageProducts
        );
        yield return Policy<DomesticProductsController>(
            nameof(DomesticProductsController.BatchCreateDomesticProducts),
            Permissions.DomesticPurchase.ManageProducts
        );
        yield return Policy<DomesticProductsController>(
            nameof(DomesticProductsController.BatchDetectProducts),
            Permissions.DomesticPurchase.ManageProducts
        );
        yield return Policy<DomesticProductsController>(
            nameof(DomesticProductsController.BatchCreateAndUpdateProducts),
            Permissions.DomesticPurchase.ManageProducts
        );

        yield return Policy<ReactDomesticProductsController>(
            nameof(ReactDomesticProductsController.GetGridData),
            Permissions.Products.View
        );
        yield return Policy<ReactDomesticProductsController>(
            nameof(ReactDomesticProductsController.GetSetItems),
            Permissions.Products.View
        );
        yield return Policy<ReactDomesticProductsController>(
            nameof(ReactDomesticProductsController.UpdateProduct),
            Permissions.Products.Edit
        );
        yield return Policy<ReactDomesticProductsController>(
            nameof(ReactDomesticProductsController.UpdateSetItems),
            Permissions.Products.Edit
        );
        yield return Policy<ReactDomesticProductsController>(
            nameof(ReactDomesticProductsController.BatchDelete),
            Permissions.Products.Delete
        );
        yield return Policy<ReactDomesticProductsController>(
            nameof(ReactDomesticProductsController.BatchCreateProducts),
            Permissions.DomesticPurchase.ManageProducts
        );
        yield return Policy<ReactDomesticProductsController>(
            nameof(ReactDomesticProductsController.BatchValidateProducts),
            Permissions.DomesticPurchase.ManageProducts
        );
        yield return Policy<ReactDomesticProductsController>(
            nameof(ReactDomesticProductsController.BatchDetectProducts),
            Permissions.DomesticPurchase.ManageProducts
        );
        yield return Policy<ReactDomesticProductsController>(
            nameof(ReactDomesticProductsController.BatchImportConfirm),
            Permissions.DomesticPurchase.ManageProducts
        );
        yield return Policy<ReactDomesticProductsController>(
            nameof(ReactDomesticProductsController.BatchUpdate),
            Permissions.DomesticPurchase.ManageProducts
        );
        yield return Policy<ReactDomesticProductsController>(
            nameof(ReactDomesticProductsController.SyncToHBSales),
            Permissions.DomesticPurchase.ManageProducts
        );
        yield return Policy<ReactDomesticProductsController>(
            nameof(ReactDomesticProductsController.BatchCreateSetProducts),
            Permissions.DomesticPurchase.ManageProducts
        );
        yield return Policy<ReactDomesticProductsController>(
            nameof(ReactDomesticProductsController.SendToHq),
            Permissions.DomesticPurchase.ManageProducts
        );

        yield return Policy<ReactProductsController>(
            nameof(ReactProductsController.CreateWithPrices),
            Permissions.StoreProducts.Create
        );
        yield return Policy<ReactProductController>(
            nameof(ReactProductController.GetPagedList),
            Permissions.PosProducts.View
        );
        yield return Policy<ReactProductController>(
            nameof(ReactProductController.GetById),
            Permissions.PosProducts.View
        );
        yield return Policy<ReactProductController>(
            nameof(ReactProductController.GetStoreRecords),
            Permissions.StoreProducts.View
        );
        yield return Policy<ReactProductController>(
            nameof(ReactProductController.BatchUpdateStoreRecords),
            Permissions.StoreProducts.Edit
        );
        yield return Policy<ReactProductController>(
            nameof(ReactProductController.SyncProductsToStores),
            Permissions.PosProducts.Manage
        );
        yield return Policy<ReactProductController>(
            nameof(ReactProductController.StartSyncProductsToStoresJob),
            Permissions.PosProducts.Manage
        );
        yield return Policy<ReactProductController>(
            nameof(ReactProductController.GetSyncProductsToStoresJob),
            Permissions.PosProducts.Manage
        );

        yield return Policy<ReactContainerProductsController>(
            nameof(ReactContainerProductsController.StartCreateNewProductsJob),
            Permissions.Container.Edit
        );
        yield return Policy<ReactContainerProductsController>(
            nameof(ReactContainerProductsController.StartCreateNewProductsJob),
            Permissions.PosProducts.Manage
        );
        yield return Policy<ReactContainerProductsController>(
            nameof(ReactContainerProductsController.GetCreateNewProductsJob),
            Permissions.Container.Edit
        );
        yield return Policy<ReactContainerProductsController>(
            nameof(ReactContainerProductsController.GetCreateNewProductsJob),
            Permissions.PosProducts.Manage
        );

        yield return Policy<DomesticProductCreationController>(
            nameof(DomesticProductCreationController.CreateBatch),
            Permissions.DomesticPurchase.ManageProducts
        );
        yield return Policy<DomesticProductCreationController>(
            nameof(DomesticProductCreationController.GetBatchList),
            Permissions.DomesticPurchase.ManageProducts
        );
        yield return Policy<DomesticProductCreationController>(
            nameof(DomesticProductCreationController.GetBatchDetail),
            Permissions.DomesticPurchase.ManageProducts
        );
        yield return Policy<DomesticProductCreationController>(
            nameof(DomesticProductCreationController.ExportBatch),
            Permissions.DomesticPurchase.ManageProducts
        );
        yield return Policy<DomesticProductCreationController>(
            nameof(DomesticProductCreationController.UpdatePrivateLabelPrice),
            Permissions.DomesticPurchase.ManageProducts
        );
        yield return Policy<DomesticProductCreationController>(
            nameof(DomesticProductCreationController.UpdateBatchItems),
            Permissions.DomesticPurchase.ManageProducts
        );

        yield return Policy<ReactCashRegisterUserController>(
            nameof(ReactCashRegisterUserController.ConfirmPrint),
            Permissions.CashRegisterUsers.MobilePrint
        );
        yield return Policy<ReactCashRegisterUserController>(
            nameof(ReactCashRegisterUserController.Delete),
            Permissions.Store.ManageOperations
        );
        yield return Policy<ReactAdvertisementsController>(
            nameof(ReactAdvertisementsController.Grid),
            Permissions.Advertisements.View
        );
        yield return Policy<ReactAdvertisementsController>(
            nameof(ReactAdvertisementsController.Get),
            Permissions.Advertisements.View
        );
        yield return Policy<ReactAdvertisementsController>(
            nameof(ReactAdvertisementsController.Create),
            Permissions.Advertisements.Edit
        );
        yield return Policy<ReactAdvertisementsController>(
            nameof(ReactAdvertisementsController.Update),
            Permissions.Advertisements.Edit
        );
        yield return Policy<ReactAdvertisementsController>(
            nameof(ReactAdvertisementsController.Delete),
            Permissions.Advertisements.Edit
        );
        yield return Policy<ReactAdvertisementsController>(
            nameof(ReactAdvertisementsController.Enable),
            Permissions.Advertisements.Edit
        );
        yield return Policy<ReactAdvertisementsController>(
            nameof(ReactAdvertisementsController.UploadSignature),
            Permissions.Advertisements.Edit
        );
        yield return Policy<ReactCashRegisterUserController>(
            nameof(ReactCashRegisterUserController.BatchDelete),
            Permissions.Store.ManageOperations
        );

        yield return Policy<ReactDeviceRegistrationController>(
            nameof(ReactDeviceRegistrationController.Grid),
            Permissions.DeviceRegistration.View
        );
        yield return Policy<ReactDeviceRegistrationController>(
            nameof(ReactDeviceRegistrationController.GetById),
            Permissions.DeviceRegistration.Manage
        );
        yield return Policy<ReactDeviceRegistrationController>(
            nameof(ReactDeviceRegistrationController.Update),
            Permissions.DeviceRegistration.Manage
        );

        yield return Policy<SeasonalCardRemainingController>(
            nameof(SeasonalCardRemainingController.GetCatalog),
            Permissions.SeasonalCards.Remaining.SubmitManagedStore
        );
        yield return Policy<SeasonalCardRemainingController>(
            nameof(SeasonalCardRemainingController.CreateSubmission),
            Permissions.SeasonalCards.Remaining.SubmitManagedStore
        );
        yield return Policy<SeasonalCardRemainingController>(
            nameof(SeasonalCardRemainingController.GetSubmissions),
            Permissions.SeasonalCards.Remaining.ViewManagedStore
        );
        yield return Policy<SeasonalCardRemainingController>(
            nameof(SeasonalCardRemainingController.GetSubmission),
            Permissions.SeasonalCards.Remaining.ViewManagedStore
        );
        yield return Policy<SeasonalProductInsightsController>(
            nameof(SeasonalProductInsightsController.Lookup),
            Permissions.SeasonalProductInsights.View
        );
        yield return Policy<SeasonalProductInsightsController>(
            nameof(SeasonalProductInsightsController.GetStoreInsight),
            Permissions.SeasonalProductInsights.View
        );
        yield return Policy<ReactAttendanceController>(
            nameof(ReactAttendanceController.GetAttendanceRecords),
            Permissions.Attendance.Punch.ViewManagedStore
        );
        yield return Policy<ReactAttendanceController>(
            nameof(ReactAttendanceController.ResolveQr),
            Permissions.Attendance.Punch.Self
        );
    }

    [Theory]
    [MemberData(nameof(MenuBackedEndpointPolicies))]
    public void MenuBackedBusinessEndpoints_RequireExpectedPolicyWithoutRoleGate(
        Type controllerType,
        string methodName,
        string expectedPolicy
    )
    {
        AssertNoRoleGate(
            controllerType.GetCustomAttributes<AuthorizeAttribute>(inherit: false),
            controllerType.Name
        );

        var method = GetDeclaredMethod(controllerType, methodName);
        var authorizeAttributes = method
            .GetCustomAttributes<AuthorizeAttribute>(inherit: false)
            .ToList();

        AssertNoRoleGate(authorizeAttributes, $"{controllerType.Name}.{methodName}");
        Assert.Contains(authorizeAttributes, attribute => attribute.Policy == expectedPolicy);
    }

    [Theory]
    [InlineData(typeof(ReactLocalSupplierInvoicesController))]
    [InlineData(typeof(ReactStoreUsersController))]
    [InlineData(typeof(DomesticProductsController))]
    [InlineData(typeof(ReactDomesticProductsController))]
    [InlineData(typeof(DomesticProductCreationController))]
    [InlineData(typeof(ReactCashRegisterUserController))]
    [InlineData(typeof(ReactDeviceRegistrationController))]
    [InlineData(typeof(ServiceApiTokensController))]
    public void TargetControllers_DoNotUseLegacyRoleGates(Type controllerType)
    {
        AssertNoRoleGate(
            controllerType.GetCustomAttributes<AuthorizeAttribute>(inherit: false),
            controllerType.Name
        );

        var publicMethods = controllerType
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(method => !method.IsSpecialName);

        foreach (var method in publicMethods)
        {
            if (controllerType == typeof(ReactLocalSupplierInvoicesController)
                && (
                    method.Name == nameof(ReactLocalSupplierInvoicesController.SyncFromHq)
                    || method.Name == nameof(ReactLocalSupplierInvoicesController.ImportPreview)
                    || method.Name == nameof(ReactLocalSupplierInvoicesController.ImportConfirm)
                    || method.Name == nameof(ReactLocalSupplierInvoicesController.UpdateLastPurchasePrices)
                ))
            {
                continue;
            }

            AssertNoRoleGate(
                method.GetCustomAttributes<AuthorizeAttribute>(inherit: false),
                $"{controllerType.Name}.{method.Name}"
            );
        }
    }

    [Theory]
    [InlineData(nameof(ReactLocalSupplierInvoicesController.ImportPreview))]
    [InlineData(nameof(ReactLocalSupplierInvoicesController.ImportConfirm))]
    [InlineData(nameof(ReactLocalSupplierInvoicesController.UpdateLastPurchasePrices))]
    public void ReactLocalSupplierInvoicesController_AdminOnlyEditEndpointsRequireEditPolicyAndAdminRole(
        string methodName
    )
    {
        var method = GetDeclaredMethod(typeof(ReactLocalSupplierInvoicesController), methodName);
        var authorizeAttributes = method
            .GetCustomAttributes<AuthorizeAttribute>(inherit: false)
            .ToList();

        Assert.Contains(authorizeAttributes, attribute => attribute.Policy == Permissions.LocalPurchase.Edit);
        Assert.Contains(authorizeAttributes, attribute => attribute.Roles == "Admin,管理员");
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("管理员")]
    [InlineData("WarehouseManager")]
    public async Task ReactLocalSupplierInvoicesController_ImportConfirmStoreGateAllowsFullStoreRoles(
        string roleName
    )
    {
        var controller = new ReactLocalSupplierInvoicesController(null!, null!, null!, null!)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(
                        new ClaimsIdentity(
                            new[] { new Claim(ClaimTypes.Role, roleName) },
                            authenticationType: "test"
                        )
                    )
                }
            }
        };

        var canAccessStoreAsync = typeof(ReactLocalSupplierInvoicesController).GetMethod(
            "CanAccessStoreAsync",
            BindingFlags.Instance | BindingFlags.NonPublic
        );

        var task = Assert.IsAssignableFrom<Task<bool>>(
            canAccessStoreAsync!.Invoke(controller, new object?[] { "ANY-STORE" })
        );

        Assert.True(await task);
    }

    [Theory]
    [InlineData(Permissions.Store.ManageOperations, true, true)]
    [InlineData(Permissions.CashRegisterUsers.MobileManage, true, true)]
    [InlineData(Permissions.CashRegisterUsers.MobilePrint, true, false)]
    [InlineData(Permissions.Users.View, false, false)]
    public async Task ReactCashRegisterUserController_移动端独立权限按读取与管理分开放行(
        string grantedPermission,
        bool canRead,
        bool canManage
    )
    {
        var service = new Mock<ICashRegisterUserReactService>();
        service
            .Setup(item => item.GetGridDataAsync(It.IsAny<GridRequestDto>()))
            .ReturnsAsync(new GridResponseDto<CashRegisterUserListDto>
            {
                Success = true,
                Items = new List<CashRegisterUserListDto>(),
            });
        service
            .Setup(item => item.GetByHGuidAsync(It.IsAny<string>()))
            .ReturnsAsync(ApiResponse<CashRegisterUserDetailDto>.OK(new CashRegisterUserDetailDto()));
        service
            .Setup(item => item.GetScopeAsync())
            .ReturnsAsync(() => ApiResponse<CashRegisterUserScopeDto>.OK(new CashRegisterUserScopeDto()));
        service
            .Setup(item => item.GetUserOptionsAsync())
            .ReturnsAsync(ApiResponse<List<CashRegisterUserUserOptionDto>>.OK(new List<CashRegisterUserUserOptionDto>()));
        service
            .Setup(item => item.CreateAsync(It.IsAny<CreateCashRegisterUserDto>(), It.IsAny<string>()))
            .ReturnsAsync(ApiResponse<CashRegisterUserDetailDto>.OK(new CashRegisterUserDetailDto()));
        service
            .Setup(item => item.UpdateAsync(It.IsAny<string>(), It.IsAny<UpdateCashRegisterUserDto>(), It.IsAny<string>()))
            .ReturnsAsync(ApiResponse<CashRegisterUserDetailDto>.OK(new CashRegisterUserDetailDto()));

        var authorizationService = new Mock<IAuthorizationService>(MockBehavior.Strict);
        authorizationService
            .Setup(item => item.AuthorizeAsync(
                It.IsAny<ClaimsPrincipal>(),
                It.IsAny<object?>(),
                It.IsAny<string>()))
            .ReturnsAsync((ClaimsPrincipal _, object? _, string policyName) =>
                string.Equals(policyName, grantedPermission, StringComparison.OrdinalIgnoreCase)
                    ? AuthorizationResult.Success()
                    : AuthorizationResult.Failed());
        var controller = new ReactCashRegisterUserController(service.Object, authorizationService.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        new[] { new Claim(ClaimTypes.Name, "tester") }, "test")),
                },
            },
        };

        AssertAllowed(canRead, await controller.Grid(new GridRequestDto { StartRow = 0, PageSize = 20 }));
        AssertAllowed(canRead, await controller.GetByHGuid("cashier-1"));
        var scopeResult = await controller.GetScope();
        AssertAllowed(canRead, scopeResult);
        if (canRead)
        {
            // 范围接口回传服务端实时判定：只有移动端独立权限才点亮管理/打印，Web 权限不带出。
            var scope = (CashRegisterUserScopeDto)((OkObjectResult)scopeResult).Value!
                .GetType().GetProperty("data")!.GetValue(((OkObjectResult)scopeResult).Value)!;
            Assert.Equal(grantedPermission == Permissions.CashRegisterUsers.MobileManage, scope.CanManage);
            Assert.Equal(grantedPermission == Permissions.CashRegisterUsers.MobilePrint, scope.CanPrint);
        }
        AssertAllowed(canManage, await controller.GetUserOptions());
        AssertAllowed(canManage, await controller.Create(new CreateCashRegisterUserDto()));
        AssertAllowed(canManage, await controller.Update("cashier-1", new UpdateCashRegisterUserDto()));

        static void AssertAllowed(bool expected, IActionResult result)
        {
            if (expected)
            {
                Assert.IsType<OkObjectResult>(result);
            }
            else
            {
                Assert.IsType<ForbidResult>(result);
            }
        }
    }

    [Fact]
    public void ReactStoreUsersController_ClassRequiresAuthenticationWithoutPolicyOrRoleGate()
    {
        var authorizeAttribute = Assert.Single(
            typeof(ReactStoreUsersController).GetCustomAttributes<AuthorizeAttribute>(
                inherit: false
            )
        );

        Assert.Null(authorizeAttribute.Policy);
        Assert.True(string.IsNullOrWhiteSpace(authorizeAttribute.Roles));
    }

    [Theory]
    [InlineData(Permissions.Stores.View)]
    [InlineData(Permissions.DeviceRegistration.View)]
    [InlineData(Permissions.DeviceRegistration.Manage)]
    public async Task StoresController_AllStoresByName_AllowsExpectedGlobalStoreConsumers(
        string allowedPermission
    )
    {
        var storeService = new Mock<IStoreService>(MockBehavior.Strict);
        storeService
            .Setup(service => service.GetAllStoresByNameAsync())
            .ReturnsAsync(ApiResponse<List<StoreDto>>.OK(new List<StoreDto>()));
        var controller = CreateStoresController(allowedPermission, storeService);

        var result = await controller.GetAllStoresByName();

        Assert.IsType<OkObjectResult>(result);
        storeService.Verify(service => service.GetAllStoresByNameAsync(), Times.Once);
    }

    [Fact]
    public async Task StoresController_AllStoresByName_AllowsPureWarehouseStaffWithOrderCreate()
    {
        var storeService = new Mock<IStoreService>(MockBehavior.Strict);
        storeService
            .Setup(service => service.GetAllStoresByNameAsync())
            .ReturnsAsync(ApiResponse<List<StoreDto>>.OK(new List<StoreDto>()));
        var controller = CreateStoresController(
            Permissions.Orders.Create,
            storeService,
            "WarehouseStaff"
        );

        var result = await controller.GetAllStoresByName();

        Assert.IsType<OkObjectResult>(result);
        storeService.Verify(service => service.GetAllStoresByNameAsync(), Times.Once);
    }

    [Fact]
    public async Task StoresController_AllStoresByName_ForbidsOrderCreateWithoutWarehouseStaffRole()
    {
        var storeService = new Mock<IStoreService>(MockBehavior.Strict);
        var controller = CreateStoresController(Permissions.Orders.Create, storeService);

        var result = await controller.GetAllStoresByName();

        Assert.IsType<ForbidResult>(result);
        storeService.Verify(service => service.GetAllStoresByNameAsync(), Times.Never);
    }

    [Fact]
    public async Task StoresController_AllStoresByName_ForbidsWarehouseStaffWithoutOrderCreate()
    {
        var storeService = new Mock<IStoreService>(MockBehavior.Strict);
        var controller = CreateStoresController(
            Permissions.Warehouse.Manage,
            storeService,
            "WarehouseStaff"
        );

        var result = await controller.GetAllStoresByName();

        Assert.IsType<ForbidResult>(result);
        storeService.Verify(service => service.GetAllStoresByNameAsync(), Times.Never);
    }

    [Fact]
    public async Task StoresController_AllStoresByName_ForbidsWithoutBusinessPermission()
    {
        var storeService = new Mock<IStoreService>(MockBehavior.Strict);
        var controller = CreateStoresController(allowedPermission: null, storeService);

        var result = await controller.GetAllStoresByName();

        Assert.IsType<ForbidResult>(result);
        storeService.Verify(service => service.GetAllStoresByNameAsync(), Times.Never);
    }

    [Fact]
    public void ServiceApiTokensController_ManagementEndpointsRequireJwtBearerOnly()
    {
        foreach (var methodName in new[]
                 {
                     nameof(ServiceApiTokensController.List),
                     nameof(ServiceApiTokensController.Create),
                     nameof(ServiceApiTokensController.Revoke),
                 })
        {
            var authorizeAttribute = Assert.Single(
                GetDeclaredMethod(typeof(ServiceApiTokensController), methodName)
                    .GetCustomAttributes<AuthorizeAttribute>(inherit: false)
            );

            Assert.Equal(Permissions.System.ManageAppDownloads, authorizeAttribute.Policy);
            Assert.Equal(JwtBearerDefaults.AuthenticationScheme, authorizeAttribute.AuthenticationSchemes);
        }
    }

    [Fact]
    public void ServiceApiTokensController_CurrentRequiresServiceApiTokenScheme()
    {
        var authorizeAttribute = Assert.Single(
            GetDeclaredMethod(typeof(ServiceApiTokensController), nameof(ServiceApiTokensController.Current))
                .GetCustomAttributes<AuthorizeAttribute>(inherit: false)
        );

        Assert.True(string.IsNullOrEmpty(authorizeAttribute.Policy));
        Assert.Equal(
            ServiceApiTokenAuthenticationDefaults.AuthenticationScheme,
            authorizeAttribute.AuthenticationSchemes
        );
    }

    [Fact]
    public void MobileAppBuildsController_OtaUpsertAllowsExplicitBearerOrServiceTokenScheme()
    {
        var authorizeAttribute = Assert.Single(
            GetDeclaredMethod(typeof(MobileAppBuildsController), nameof(MobileAppBuildsController.UpsertOtaUpdate))
                .GetCustomAttributes<AuthorizeAttribute>(inherit: false)
        );

        Assert.Equal(Permissions.System.ManageAppDownloads, authorizeAttribute.Policy);
        Assert.Equal(
            ServiceApiTokenAuthenticationDefaults.PolicyScheme,
            authorizeAttribute.AuthenticationSchemes
        );
    }

    [Theory]
    [InlineData(typeof(ContainerController), nameof(ContainerController.GetContainers))]
    [InlineData(typeof(ContainerController), nameof(ContainerController.UpdateContainer))]
    public void PlainAuthorizeBusinessEndpoints_DoNotOptIntoServiceTokenScheme(
        Type controllerType,
        string methodName
    )
    {
        var authorizeAttributes = controllerType
            .GetCustomAttributes<AuthorizeAttribute>(inherit: false)
            .Concat(
                GetDeclaredMethod(controllerType, methodName)
                    .GetCustomAttributes<AuthorizeAttribute>(inherit: false)
            )
            .ToList();

        Assert.Contains(authorizeAttributes, attribute => string.IsNullOrWhiteSpace(attribute.Policy));
        Assert.DoesNotContain(
            authorizeAttributes,
            attribute => attribute.AuthenticationSchemes == ServiceApiTokenAuthenticationDefaults.PolicyScheme
                || attribute.AuthenticationSchemes == ServiceApiTokenAuthenticationDefaults.AuthenticationScheme
        );
    }

    [Theory]
    [InlineData(nameof(ReactPromotionsController.Create))]
    [InlineData(nameof(ReactPromotionsController.Update))]
    [InlineData(nameof(ReactPromotionsController.Delete))]
    [InlineData(nameof(ReactPromotionsController.Enable))]
    public void ReactPromotionsController_GlobalWriteEndpointsRequireAdminRole(string methodName)
    {
        var method = GetDeclaredMethod(typeof(ReactPromotionsController), methodName);
        var authorizeAttributes = method
            .GetCustomAttributes<AuthorizeAttribute>(inherit: false)
            .ToList();

        Assert.Contains(authorizeAttributes, attribute => attribute.Policy == Permissions.Promotions.Edit);
        Assert.Contains(authorizeAttributes, attribute => attribute.Roles == "Admin,管理员");
    }

    [Fact]
    public void LocalPurchasePushToHqPermission_IsDefinedAndSeeded()
    {
        var field = typeof(Permissions.LocalPurchase).GetField(
            "PushToHq",
            BindingFlags.Public | BindingFlags.Static
        );

        Assert.NotNull(field);
        Assert.Equal(LocalPurchasePushToHq, field.GetRawConstantValue());
        Assert.Contains(
            PermissionSeedData.AllPermissions,
            permission => permission.Code == LocalPurchasePushToHq
        );
    }

    private static object[] Policy<TController>(string methodName, string expectedPolicy) =>
        new object[] { typeof(TController), methodName, expectedPolicy };

    private static StoresController CreateStoresController(
        string? allowedPermission,
        Mock<IStoreService> storeService,
        params string[] roleNames
    )
    {
        var allowedPermissions = string.IsNullOrWhiteSpace(allowedPermission)
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(new[] { allowedPermission }, StringComparer.OrdinalIgnoreCase);
        var authorizationService = new Mock<IAuthorizationService>(MockBehavior.Strict);
        authorizationService
            .Setup(service => service.AuthorizeAsync(
                It.IsAny<ClaimsPrincipal>(),
                It.IsAny<object?>(),
                It.IsAny<string>()))
            .ReturnsAsync(
                (ClaimsPrincipal _, object? _, string policyName) =>
                    allowedPermissions.Contains(policyName)
                        ? AuthorizationResult.Success()
                        : AuthorizationResult.Failed()
            );
        var claims = roleNames.Select(roleName => new Claim(ClaimTypes.Role, roleName));

        return new StoresController(
            storeService.Object,
            syncService: null!,
            authorizationService.Object,
            NullLogger<StoresController>.Instance
        )
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(
                        new ClaimsIdentity(claims, authenticationType: "test")
                    )
                },
            },
        };
    }

    private static MethodInfo GetDeclaredMethod(Type controllerType, string methodName) =>
        controllerType.GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly
            )
            .SingleOrDefault(method => method.Name == methodName)
        ?? throw new InvalidOperationException(
            $"{controllerType.Name}.{methodName} was not found."
        );

    private static void AssertNoRoleGate(
        IEnumerable<AuthorizeAttribute> authorizeAttributes,
        string target
    )
    {
        var roleAttributes = authorizeAttributes
            .Where(attribute => !string.IsNullOrWhiteSpace(attribute.Roles))
            .Select(attribute => attribute.Roles)
            .ToList();

        Assert.True(
            roleAttributes.Count == 0,
            $"{target} should not use Roles authorization, but found: {string.Join(", ", roleAttributes)}"
        );
    }
}
