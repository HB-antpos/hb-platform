using BlazorApp.Shared.DTOs;

namespace BlazorApp.Api.Features.StoreOrders.Common;

internal sealed record StoreOrderCartScope(
    string StoreCode,
    string? CartOwnerUserGuid
);

internal sealed record StoreOrderCartSubmissionSnapshot(
    string OrderGuid,
    int? FlowStatus,
    DateTime? UpdatedAt = null
);

/// <summary>
/// 提交时购物车里已暂停供货（仓库下架）的一行。SupplyPlan 只在有未关闭供货说明时有值。
/// </summary>
internal sealed record StoreOrderSupplyPausedLine(
    string DetailGuid,
    string ProductCode,
    string? ItemNumber,
    string? ProductName,
    decimal Quantity,
    string? SupplyPlan
);

internal sealed record StoreOrderCartSplitResult(
    string NewCartOrderGuid,
    int MovedCount,
    long NewCartRevision
);

internal interface IStoreOrderCartOwnerScope
{
    bool IsWarehouseStaffOnly { get; }

    StoreOrderCartScope Resolve(string? storeCode);
}

internal interface IStoreOrderCartCommandCoordinator
{
    Task<ApiResponse<T>> ExecuteAsync<T>(
        StoreOrderCartScope scope,
        Func<Task<ApiResponse<T>>> command
    );
}

/// <summary>
/// Cart 扫码入口与 ProductPicker 之间的共享窄查询端口。
/// </summary>
public interface IStoreOrderCartProductLookup
{
    Task<ApiResponse<StoreOrderScanLookupResultDto>> LookupAsync(
        StoreOrderScanLookupRequestDto request
    );
}

/// <summary>
/// OrderPlacement 提交购物车时使用的共享窄持久化端口。
/// </summary>
internal interface IStoreOrderCartPlacementPort
{
    Task<StoreOrderCartSubmissionSnapshot?> GetActiveForSubmissionAsync(
        StoreOrderCartScope scope
    );

    Task<int> CountActiveItemsAsync(string orderGuid);

    /// <summary>
    /// 购物车里已暂停供货（仓库下架）的行，提交时这些行留在购物车、其余行进单。
    /// 仓库侧代下单不受限，此时恒返回空。
    /// </summary>
    Task<IReadOnlyList<StoreOrderSupplyPausedLine>> GetSupplyPausedLinesAsync(string orderGuid);

    /// <summary>
    /// 把指定明细行从待提交购物车挪到同店同 owner 新建的购物车，并重算两边合计。
    /// 只在提交事务内调用；影响行数与期望不符时抛异常，由事务整体回滚。
    /// </summary>
    Task<StoreOrderCartSplitResult> MoveLinesToNewCartAsync(
        StoreOrderCartScope scope,
        StoreOrderCartSubmissionSnapshot source,
        IReadOnlyCollection<string> detailGuids,
        DateTime now,
        string actor
    );

    Task<int> CompareExchangeSubmitAsync(
        StoreOrderCartSubmissionSnapshot snapshot,
        string orderNo,
        string? remarks,
        DateTime submittedAt,
        string submittedBy
    );
}
