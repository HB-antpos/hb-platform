using BlazorApp.Api.Data;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;

namespace BlazorApp.Api.Services.React;

/// <summary>
/// 新品到店查询的无鉴权入口，只给后台任务用（每日新品推送）：后台没有请求上下文，
/// 不能走 <see cref="Interfaces.React.IContainerNewProductsReactService"/>（它的第一步是按当前用户校验门店范围，后台恒为无权限）。
/// 刻意不挂在控制器使用的接口上，避免被误当成对外入口；调用方负责保证「该门店允许被读取」。
/// </summary>
internal interface IContainerNewProductsQuery
{
    /// <summary>
    /// 与 GetAsync 鉴权之后同一份逻辑：<paramref name="nowUtc"/> 决定门店本地「今天」与窗口；
    /// 门店所在州不是 NSW / QLD 时抛 <see cref="ContainerNewProductsStateUnknownException"/>。
    /// </summary>
    Task<ContainerNewProductsResponseDto> GetForStoreAsync(
        Store store,
        bool includeExisting,
        DateTime nowUtc,
        CancellationToken cancellationToken = default
    );
}

internal sealed class ContainerNewProductsQuery(SqlSugarContext context) : IContainerNewProductsQuery
{
    public Task<ContainerNewProductsResponseDto> GetForStoreAsync(
        Store store,
        bool includeExisting,
        DateTime nowUtc,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(store);
        // 与 GetAsync 一致：门店编码裁剪后参与分店价查询，并原样回写到响应。
        return ContainerNewProductsReactService.QueryForStoreAsync(
            context.Db, store, store.StoreCode.Trim(), includeExisting, nowUtc, cancellationToken);
    }
}
