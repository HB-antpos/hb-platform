using System.Reflection;
using BlazorApp.Api.Controllers.React;
using BlazorApp.Api.Filters;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 2026-09-29 停用全部 HQ → HBweb 同步入口的路由契约：
/// 清单内的入口必须带 <see cref="HqToHbwebSyncDisabledAttribute"/>，清单外（尤其反方向、POSM、只读查询）不得误带。
/// 通过反射枚举所有已注册控制器 action，新增或漏标入口都会让测试失败。
/// </summary>
public class HqToHbwebSyncDisabledContractTests
{
    private const string SyncPrefix = "POST api/react/v1/sync/";
    private const string LegacySyncPrefix = "POST api/DataSync/";

    /// <summary>
    /// 整个 action 停用的 HQ / HBSales → HBweb 入口。
    /// </summary>
    private static readonly string[] FullyDisabledRoutes =
    [
        // DataSyncReactController：除 POSM 映射外全部是 HQ / HBSales → HBweb
        SyncPrefix + "products",
        SyncPrefix + "products-incremental",
        SyncPrefix + "store-retail-prices",
        SyncPrefix + "store-retail-prices-incremental",
        SyncPrefix + "store-multi-code-products",
        SyncPrefix + "store-multi-code-products-incremental",
        SyncPrefix + "product-set-codes",
        SyncPrefix + "product-set-codes-incremental",
        SyncPrefix + "store-clearance-prices",
        SyncPrefix + "store-clearance-prices-incremental",
        SyncPrefix + "domestic-products",
        SyncPrefix + "domestic-products-incremental",
        SyncPrefix + "domestic-set-products",
        SyncPrefix + "domestic-set-products-incremental",
        SyncPrefix + "product-prefix-codes",
        SyncPrefix + "product-prefix-codes-incremental",
        SyncPrefix + "china-suppliers",
        SyncPrefix + "china-suppliers-incremental",
        SyncPrefix + "warehouse-categories",
        SyncPrefix + "warehouse-categories-incremental",
        SyncPrefix + "product-categories",
        SyncPrefix + "product-categories-incremental",
        SyncPrefix + "containers",
        SyncPrefix + "containers-incremental",
        SyncPrefix + "container-details",
        SyncPrefix + "container-details-incremental",
        SyncPrefix + "warehouse-products",
        SyncPrefix + "warehouse-products-incremental",
        SyncPrefix + "store-local-supplier-invoices",
        SyncPrefix + "store-local-supplier-invoices-incremental",
        SyncPrefix + "store-local-supplier-invoice-details",
        SyncPrefix + "store-local-supplier-invoice-details-incremental",
        SyncPrefix + "store-local-supplier-invoices-all",
        SyncPrefix + "warehouse-orders",
        SyncPrefix + "warehouse-orders-incremental",
        SyncPrefix + "warehouse-order-details",
        SyncPrefix + "warehouse-order-details-incremental",
        SyncPrefix + "warehouse-orders-all",
        SyncPrefix + "locations",
        SyncPrefix + "locations-incremental",
        SyncPrefix + "product-locations",
        SyncPrefix + "product-locations-incremental",
        SyncPrefix + "cash-register-users",
        SyncPrefix + "cash-register-users-incremental",
        SyncPrefix + "special-product-from-hq",

        // 旧 DataSyncController 中的 HQ → HBweb 入口
        LegacySyncPrefix + "sync-suppliers",
        LegacySyncPrefix + "sync-categories",
        LegacySyncPrefix + "sync-products",
        LegacySyncPrefix + "sync-products-incremental",
        LegacySyncPrefix + "sync-product-stocks",
        LegacySyncPrefix + "sync-product-stocks-incremental",
        LegacySyncPrefix + "sync-locations",
        LegacySyncPrefix + "sync-product-locations",
        LegacySyncPrefix + "sync-all",
        LegacySyncPrefix + "sync-domestic-products",
        LegacySyncPrefix + "sync-product-prefix-codes",
        LegacySyncPrefix + "sync-domestic-set-products",
        LegacySyncPrefix + "sync-containers",
        LegacySyncPrefix + "sync-containers-incremental",
        LegacySyncPrefix + "store-retail-prices",
        LegacySyncPrefix + "store-clearance-prices",
        LegacySyncPrefix + "store-multicode-products",

        // 各业务页面上的「从 HQ 同步」入口
        "POST api/react/v1/products/sync-from-hq",
        "POST api/react/v1/products/sync-selected-from-hq",
        "POST api/react/v1/product-warehouse/sync-from-hq",
        "POST api/react/v1/product-warehouse/sync-from-hq/jobs",
        "POST api/react/v1/containers/sync-from-hq",
        "POST api/react/v1/store-product-prices/sync-from-hq",
        "POST api/react/v1/local-supplier-invoices/sync-from-hq",
        "POST api/react/v1/local-suppliers/sync",
        "POST api/Stores/sync",
        "POST api/react/v1/store-order/sync-missing-orders",
        "POST api/react/v1/store-order/sync-missing-orders/jobs",
        "POST api/react/v1/store-order/hq-sync/full/jobs",
        "POST api/react/v1/store-order/hq-sync/incremental/jobs",
    ];

    /// <summary>
    /// 兼容两个方向的入口：只停用 HQ → HBweb 方向，本地 → HQ 继续放行。
    /// </summary>
    private const string StorePriceTransferJobRoute =
        "POST api/react/v1/store-product-prices/store-price-transfer-jobs";

    /// <summary>
    /// 必须保持可用的反方向（HBweb → HQ / HBSales）、HBweb → POSM、HBweb 内部与只读查询入口。
    /// </summary>
    private static readonly string[] MustStayEnabledRoutes =
    [
        SyncPrefix + "posm-product-supplier-mappings",
        SyncPrefix + "posm-product-supplier-mappings-incremental",
        "POST api/react/v1/products/push-to-hq",
        "POST api/react/v1/products/push-to-hq/jobs",
        "GET api/react/v1/products/push-to-hq/jobs/{jobId}",
        "GET api/react/v1/products/push-to-hq/store-options",
        "POST api/react/v1/products/batch-update-supplier-images",
        "POST api/react/v1/products/sync-to-stores",
        "POST api/react/v1/product-warehouse/store-price-sync/jobs",
        "GET api/react/v1/product-warehouse/sync-from-hq/jobs/{jobId}",
        "POST api/react/v1/local-supplier-invoices/push-to-hq",
        "POST api/react/v1/local-supplier-invoices/{invoiceGuid}/details/ensure-hq-products",
        "POST api/react/v1/local-supplier-invoices/{invoiceGuid}/details/update-hq-products",
        "POST api/react/v1/local-supplier-invoices/{invoiceGuid}/details/update-hq-products/jobs",
        "POST api/react/v1/local-suppliers/sync-to-hq",
        "POST api/Stores/guid/{guid}/sync-hq",
        "GET api/Stores/sync/history",
        "POST api/react/v1/domestic-products/send-to-hq",
        "POST api/react/v1/domestic-products/sync-to-hbsales",
        "POST api/v1/ChinaSuppliers/sync-to-hbsales",
        "POST api/react/v1/containers/push-to-hbsales",
        "POST api/react/v1/hq-products/translate-names",
        "PUT api/react/v1/product-grades/batch-price",
        "POST api/react/v1/store-product-prices/copy-store-data",
        "GET api/react/v1/store-product-prices/store-price-transfer-jobs/{jobId}",
        "GET api/react/v1/store-order/sync-missing-orders/jobs/{jobId}",
        "GET api/react/v1/store-order/hq-sync/jobs/{jobId}",
        "POST api/react/v1/store-product-maintenance/store-prices/{uuid}/sync-warehouse",
        "GET api/react/v1/store-product-maintenance/hq-sync/{operationId}",
        "POST api/react/v1/store-product-maintenance/hq-sync/{operationId}/retry",
        "GET api/react/v1/store-product-maintenance/offline-catalog/sync-plan",
        LegacySyncPrefix + "domestic-products-to-hq",
        LegacySyncPrefix + "translate-all-product-names",
        LegacySyncPrefix + "translate-product-names",
    ];

    [Fact]
    public void 带停用特性的Action集合必须与HQ到HBweb停用清单完全一致()
    {
        var expected = FullyDisabledRoutes
            .Append(StorePriceTransferJobRoute)
            .OrderBy(route => route, StringComparer.Ordinal)
            .ToArray();
        var actual = EnumerateActionRoutes()
            .Where(route => route.DisabledAttribute is not null)
            .Select(route => route.Route)
            .OrderBy(route => route, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(76, expected.Length);
        Assert.Equal(expected.Length, expected.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void 整体停用的入口不得带放行条件()
    {
        var routes = EnumerateActionRoutes().ToLookup(route => route.Route, StringComparer.Ordinal);

        foreach (var route in FullyDisabledRoutes)
        {
            var action = Assert.Single(routes[route]);
            Assert.NotNull(action.DisabledAttribute);
            Assert.False(
                action.DisabledAttribute!.IsConditional,
                $"{route} 应整体停用，不应配置方向放行条件"
            );
        }
    }

    [Fact]
    public void 分店价格同步Job只放行本地到HQ方向()
    {
        var action = Assert.Single(
            EnumerateActionRoutes(),
            route => route.Route == StorePriceTransferJobRoute
        );
        var attribute = Assert.IsType<HqToHbwebSyncDisabledAttribute>(action.DisabledAttribute);

        Assert.True(attribute.IsConditional);
        Assert.Equal(StorePriceTransferDirectionConstants.LocalToHq, attribute.AllowedDirection);

        // 条件里引用的参数与属性必须真实存在，避免改名后过滤器静默失效（按「参数缺失」一律放行）。
        var parameter = Assert.Single(
            action.Method.GetParameters(),
            item => item.Name == attribute.ArgumentName
        );
        Assert.Equal(typeof(StorePriceTransferRequest), parameter.ParameterType);
        Assert.NotNull(parameter.ParameterType.GetProperty(attribute.DirectionProperty!));
    }

    [Fact]
    public void 反方向_POSM与只读入口不得带停用特性()
    {
        var routes = EnumerateActionRoutes().ToLookup(route => route.Route, StringComparer.Ordinal);

        foreach (var route in MustStayEnabledRoutes)
        {
            // 先确认路由真实存在，防止清单写错导致断言空转
            Assert.True(routes.Contains(route), $"未找到路由 {route}");
            Assert.All(
                routes[route],
                action => Assert.True(
                    action.DisabledAttribute is null,
                    $"{route} 不属于 HQ → HBweb，不能停用"
                )
            );
        }
    }

    [Fact]
    public void 特殊商品标记同步不再允许匿名访问()
    {
        var method = typeof(DataSyncReactController).GetMethod(
            nameof(DataSyncReactController.SyncSpecialProductFromHq)
        );
        Assert.NotNull(method);

        Assert.Empty(method!.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));
        var authorize = Assert.Single(method.GetCustomAttributes<AuthorizeAttribute>(inherit: false));
        Assert.Equal("Admin", authorize.Roles);
        Assert.NotNull(method.GetCustomAttribute<HqToHbwebSyncDisabledAttribute>(inherit: true));
    }

    private sealed record ActionRoute(
        string Route,
        MethodInfo Method,
        HqToHbwebSyncDisabledAttribute? DisabledAttribute
    );

    /// <summary>
    /// 枚举会被 MVC 注册的全部控制器 action，拼出「HTTP 方法 + 完整路由模板」。
    /// [NonController] 兼容门面不会注册路由，因此跳过。
    /// </summary>
    private static IEnumerable<ActionRoute> EnumerateActionRoutes()
    {
        var controllerTypes = typeof(HqToHbwebSyncDisabledAttribute).Assembly
            .GetTypes()
            .Where(type =>
                type.IsClass
                && !type.IsAbstract
                && typeof(ControllerBase).IsAssignableFrom(type)
                && !type.IsDefined(typeof(NonControllerAttribute), inherit: true)
            );

        foreach (var controllerType in controllerTypes)
        {
            var controllerName = controllerType.Name.EndsWith("Controller", StringComparison.Ordinal)
                ? controllerType.Name[..^"Controller".Length]
                : controllerType.Name;
            var prefixes = controllerType
                .GetCustomAttributes<RouteAttribute>(inherit: true)
                .Select(attribute => attribute.Template)
                .DefaultIfEmpty(string.Empty)
                .ToArray();

            var actions = controllerType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(method => !method.IsDefined(typeof(NonActionAttribute), inherit: true));
            foreach (var method in actions)
            {
                var disabled = method.GetCustomAttribute<HqToHbwebSyncDisabledAttribute>(
                    inherit: true
                );
                foreach (var httpAttribute in method.GetCustomAttributes<HttpMethodAttribute>(inherit: true))
                {
                    foreach (var httpMethod in httpAttribute.HttpMethods)
                    {
                        foreach (var prefix in prefixes)
                        {
                            var route = CombineRoute(prefix, httpAttribute.Template)
                                .Replace("[controller]", controllerName, StringComparison.Ordinal)
                                .Replace("[action]", method.Name, StringComparison.Ordinal);
                            yield return new ActionRoute($"{httpMethod} {route}", method, disabled);
                        }
                    }
                }
            }
        }
    }

    private static string CombineRoute(string prefix, string? template)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return prefix.Trim('/');
        }

        // 以 / 或 ~/ 开头的 action 模板会覆盖控制器前缀
        if (template.StartsWith('/') || template.StartsWith("~/", StringComparison.Ordinal))
        {
            return template.TrimStart('~').Trim('/');
        }

        return string.IsNullOrWhiteSpace(prefix)
            ? template.Trim('/')
            : $"{prefix.Trim('/')}/{template.Trim('/')}";
    }
}
