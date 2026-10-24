using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BlazorApp.Api.Features.StoreOrders.Common;

internal static class StoreOrderAccessPolicyServiceCollectionExtensions
{
    internal static IServiceCollection AddStoreOrderAccessPolicy(
        this IServiceCollection services
    )
    {
        services.AddStoreOrderCommon();
        services.TryAddScoped<IStoreOrderAccessOrderReader, SqlSugarStoreOrderAccessOrderReader>();
        services.TryAddScoped<IStoreOrderAccessPolicy, StoreOrderAccessPolicy>();
        services.TryAddScoped<IStoreOrderReturnPermission, StoreOrderReturnPermission>();
        return services;
    }
}
