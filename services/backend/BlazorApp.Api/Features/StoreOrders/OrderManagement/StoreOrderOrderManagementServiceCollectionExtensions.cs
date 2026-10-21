using BlazorApp.Api.Data;
using BlazorApp.Api.Features.StoreOrders.Common;
using BlazorApp.Api.Features.StoreOrders.OrderManagement.Application;
using BlazorApp.Api.Features.StoreOrders.OrderManagement.Infrastructure;
using BlazorApp.Api.Interfaces.React;
using Microsoft.Extensions.DependencyInjection;

namespace BlazorApp.Api.Features.StoreOrders.OrderManagement;

internal static class StoreOrderOrderManagementServiceCollectionExtensions
{
    internal static IServiceCollection AddStoreOrderOrderManagementSlice(
        this IServiceCollection services
    )
    {
        services.AddStoreOrderCommon();
        services.AddScoped(serviceProvider =>
            new StoreOrderTransactionExecutor(
                serviceProvider.GetRequiredService<SqlSugarContext>()
            )
        );
        services.AddScoped(serviceProvider =>
            new StoreOrderManagementPersistence(
                serviceProvider.GetRequiredService<SqlSugarContext>(),
                serviceProvider.GetRequiredService<IStoreOrderActorContext>(),
                serviceProvider.GetRequiredService<IStoreOrderProductCostCoordinator>(),
                // 退货权限判定由访问策略切片注册；缺失时按「无权限」处理（只会更严）。
                serviceProvider.GetService<IStoreOrderReturnPermission>()
            )
        );
        services.AddScoped<IStoreOrderLineCommandStore>(serviceProvider =>
            new SqlSugarStoreOrderLineCommandStore(
                serviceProvider.GetRequiredService<SqlSugarContext>(),
                serviceProvider.GetRequiredService<StoreOrderTransactionExecutor>(),
                serviceProvider.GetRequiredService<StoreOrderManagementPersistence>(),
                serviceProvider.GetRequiredService<IStoreOrderActorContext>(),
                serviceProvider.GetRequiredService<IStoreOrderProductCostCoordinator>(),
                serviceProvider.GetRequiredService<IWarehouseProductChangeHistoryService>()
            )
        );
        services.AddScoped<IStoreOrderHeaderCommandStore>(serviceProvider =>
            new SqlSugarStoreOrderHeaderCommandStore(
                serviceProvider.GetRequiredService<SqlSugarContext>(),
                serviceProvider.GetRequiredService<StoreOrderTransactionExecutor>(),
                serviceProvider.GetRequiredService<StoreOrderManagementPersistence>(),
                serviceProvider.GetRequiredService<IStoreOrderActorContext>()
            )
        );
        services.AddScoped<IStoreOrderProductStatusCommandStore>(serviceProvider =>
            new SqlSugarStoreOrderProductStatusCommandStore(
                serviceProvider.GetRequiredService<SqlSugarContext>(),
                serviceProvider.GetRequiredService<StoreOrderTransactionExecutor>(),
                serviceProvider.GetRequiredService<IStoreOrderActorContext>(),
                serviceProvider.GetRequiredService<IWarehouseProductChangeHistoryService>()
            )
        );

        services.AddScoped(_ => new AddOrderLineValidator());
        services.AddScoped(serviceProvider =>
            new AddOrderLineHandler(
                serviceProvider.GetRequiredService<AddOrderLineValidator>(),
                serviceProvider.GetRequiredService<IStoreOrderLineCommandStore>(),
                serviceProvider.GetRequiredService<ILogger<AddOrderLineHandler>>()
            )
        );
        services.AddScoped(_ => new BatchAddOrderLineValidator());
        services.AddScoped(serviceProvider =>
            new BatchAddOrderLineHandler(
                serviceProvider.GetRequiredService<BatchAddOrderLineValidator>(),
                serviceProvider.GetRequiredService<IStoreOrderLineCommandStore>(),
                serviceProvider.GetRequiredService<ILogger<BatchAddOrderLineHandler>>()
            )
        );
        services.AddScoped(_ => new UpdateOrderLineValidator());
        services.AddScoped(serviceProvider =>
            new UpdateOrderLineHandler(
                serviceProvider.GetRequiredService<UpdateOrderLineValidator>(),
                serviceProvider.GetRequiredService<IStoreOrderLineCommandStore>(),
                serviceProvider.GetRequiredService<IStoreOrderProductCostCoordinator>(),
                serviceProvider.GetRequiredService<ILogger<UpdateOrderLineHandler>>()
            )
        );
        services.AddScoped(_ => new RemoveOrderLineValidator());
        services.AddScoped(serviceProvider =>
            new RemoveOrderLineHandler(
                serviceProvider.GetRequiredService<RemoveOrderLineValidator>(),
                serviceProvider.GetRequiredService<IStoreOrderLineCommandStore>(),
                serviceProvider.GetRequiredService<ILogger<RemoveOrderLineHandler>>()
            )
        );
        services.AddScoped(_ => new BatchUpdateOrderLineValidator());
        services.AddScoped(serviceProvider =>
            new BatchUpdateOrderLineHandler(
                serviceProvider.GetRequiredService<BatchUpdateOrderLineValidator>(),
                serviceProvider.GetRequiredService<IStoreOrderLineCommandStore>(),
                serviceProvider.GetRequiredService<IStoreOrderProductCostCoordinator>(),
                serviceProvider.GetRequiredService<ILogger<BatchUpdateOrderLineHandler>>()
            )
        );
        services.AddScoped(_ => new RefreshOrderLineImportPricesValidator());
        services.AddScoped(serviceProvider =>
            new RefreshOrderLineImportPricesHandler(
                serviceProvider.GetRequiredService<RefreshOrderLineImportPricesValidator>(),
                serviceProvider.GetRequiredService<IStoreOrderLineCommandStore>(),
                serviceProvider.GetRequiredService<
                    ILogger<RefreshOrderLineImportPricesHandler>
                >()
            )
        );
        services.AddScoped(_ => new UpdateOrderHeaderValidator());
        services.AddScoped(serviceProvider =>
            new UpdateOrderHeaderHandler(
                serviceProvider.GetRequiredService<UpdateOrderHeaderValidator>(),
                serviceProvider.GetRequiredService<IStoreOrderHeaderCommandStore>(),
                serviceProvider.GetRequiredService<ILogger<UpdateOrderHeaderHandler>>()
            )
        );
        services.AddScoped(_ => new UpdateOrderOutboundDateValidator());
        services.AddScoped(serviceProvider =>
            new UpdateOrderOutboundDateHandler(
                serviceProvider.GetRequiredService<UpdateOrderOutboundDateValidator>(),
                serviceProvider.GetRequiredService<IStoreOrderHeaderCommandStore>(),
                serviceProvider.GetRequiredService<ILogger<UpdateOrderOutboundDateHandler>>()
            )
        );
        services.AddScoped(_ => new DeleteOrderValidator());
        services.AddScoped(serviceProvider =>
            new DeleteOrderHandler(
                serviceProvider.GetRequiredService<DeleteOrderValidator>(),
                serviceProvider.GetRequiredService<IStoreOrderHeaderCommandStore>(),
                serviceProvider.GetRequiredService<ILogger<DeleteOrderHandler>>()
            )
        );
        services.AddScoped(_ => new UpdateProductStatusValidator());
        services.AddScoped(serviceProvider =>
            new UpdateProductStatusHandler(
                serviceProvider.GetRequiredService<UpdateProductStatusValidator>(),
                serviceProvider.GetRequiredService<IStoreOrderProductStatusCommandStore>(),
                serviceProvider.GetRequiredService<ILogger<UpdateProductStatusHandler>>()
            )
        );
        services.AddScoped(_ => new BatchUpdateProductStatusValidator());
        services.AddScoped(serviceProvider =>
            new BatchUpdateProductStatusHandler(
                serviceProvider.GetRequiredService<BatchUpdateProductStatusValidator>(),
                serviceProvider.GetRequiredService<IStoreOrderProductStatusCommandStore>(),
                serviceProvider.GetRequiredService<ILogger<BatchUpdateProductStatusHandler>>()
            )
        );

        services.AddScoped<IStoreOrderOrderManagementSlice>(serviceProvider =>
            new StoreOrderOrderManagementSlice(
                serviceProvider.GetRequiredService<AddOrderLineHandler>(),
                serviceProvider.GetRequiredService<BatchAddOrderLineHandler>(),
                serviceProvider.GetRequiredService<UpdateOrderLineHandler>(),
                serviceProvider.GetRequiredService<RemoveOrderLineHandler>(),
                serviceProvider.GetRequiredService<BatchUpdateOrderLineHandler>(),
                serviceProvider.GetRequiredService<RefreshOrderLineImportPricesHandler>(),
                serviceProvider.GetRequiredService<UpdateOrderHeaderHandler>(),
                serviceProvider.GetRequiredService<UpdateOrderOutboundDateHandler>(),
                serviceProvider.GetRequiredService<DeleteOrderHandler>(),
                serviceProvider.GetRequiredService<UpdateProductStatusHandler>(),
                serviceProvider.GetRequiredService<BatchUpdateProductStatusHandler>()
            )
        );
        return services;
    }
}
