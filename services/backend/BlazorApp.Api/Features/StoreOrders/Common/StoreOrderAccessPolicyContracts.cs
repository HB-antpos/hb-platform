namespace BlazorApp.Api.Features.StoreOrders.Common;

/// <summary>
/// StoreOrder 访问检查结果。当前控制器的授权失败统一映射为 Forbid（HTTP 403），
/// 资源不存在、未登录和业务错误仍由协议适配层按原有顺序映射。
/// </summary>
public readonly record struct StoreOrderAccessDecision(bool IsAllowed)
{
    public bool IsForbidden => !IsAllowed;

    public static StoreOrderAccessDecision Allowed => new(true);

    public static StoreOrderAccessDecision Forbidden => new(false);
}

/// <summary>
/// 同步入口的门店范围结果。PreserveRequestedSelection=true 表示保持调用方原 DTO 不变；
/// 否则调用方只把 ScopedStoreCodes 写回自己的协议 DTO。
/// </summary>
public readonly record struct StoreOrderStoreSelectionDecision(
    bool IsAllowed,
    bool PreserveRequestedSelection,
    IReadOnlyList<string>? ScopedStoreCodes
)
{
    public bool IsForbidden => !IsAllowed;

    public static StoreOrderStoreSelectionDecision Preserve => new(true, true, null);

    public static StoreOrderStoreSelectionDecision RestrictTo(
        IReadOnlyList<string> storeCodes
    ) => new(true, false, storeCodes);

    public static StoreOrderStoreSelectionDecision Forbidden => new(false, true, null);
}

internal static class StoreOrderAccessCheckTypes
{
    internal const string Global = "global";
    internal const string CartFlow = "cart-flow";
    internal const string ScanOrderFlow = "scan-order-flow";
}

/// <summary>
/// StoreOrder 控制器可依赖的窄访问策略。这里只处理角色、权限和门店/订单范围，
/// 不执行事务、业务写入、Preorder 门禁或 StoreGate 预检。
/// </summary>
public interface IStoreOrderAccessPolicy
{
    bool IsWarehouseStaffOnly();

    bool IsStoreScopedUser();

    bool IsRealAdmin();

    bool IsLocationProductLookupEnabled();

    string? GetCurrentUserId();

    StoreOrderAccessDecision RequireRealAdmin();

    Task<bool> HasGlobalWarehouseOrderScopeAsync();

    Task<bool> CanBypassPreorderCompletionAsync();

    Task<StoreOrderAccessDecision> RequireOrderReadAsync();

    Task<StoreOrderAccessDecision> RequireProductPickerReadAsync(
        string? storeCode,
        string? excludedOrderGuid,
        string checkType
    );

    Task<StoreOrderAccessDecision> RequireOrderListReadAsync(
        string? storeCode,
        IEnumerable<string?>? storeCodes
    );

    Task<StoreOrderAccessDecision> RequireOrderDetailReadAsync(string orderGuid);

    Task<StoreOrderAccessDecision> RequireOrderDetailProductCodesReadAsync(
        string orderGuid
    );

    Task<StoreOrderAccessDecision> RequireCartReadAsync(
        string? storeCode,
        string checkType
    );

    Task<StoreOrderAccessDecision> RequireCartWriteAsync(
        string? storeCode,
        string checkType
    );

    Task<StoreOrderAccessDecision> RequireCreateOrderAsync(string? storeCode);

    Task<StoreOrderAccessDecision> RequireOrderLineMutationAsync(string orderGuid);

    Task<StoreOrderAccessDecision> RequireOrderManagementEditAsync();

    /// <summary>
    /// 「开始配货」（已提交 → 配货中）的授权：纯仓库员工只认显式的 Warehouse.StartPicking，
    /// 其余账号沿用订单编辑授权，行为不变。
    /// </summary>
    Task<StoreOrderAccessDecision> RequireStartPickingAsync(string orderGuid);

    Task<StoreOrderAccessDecision> RequireOrderEditAsync(
        string orderGuid,
        string? storeCode = null
    );

    Task<StoreOrderAccessDecision> RequireOrderEditForStoresAsync(
        IEnumerable<string?> storeCodes
    );

    Task<StoreOrderAccessDecision> RequireOrderEditsAsync(
        IEnumerable<string?> orderGuids
    );

    Task<StoreOrderAccessDecision> RequireOrderDeleteAsync(string orderGuid);

    Task<StoreOrderAccessDecision> RequireCopyOrderAsync(
        string sourceOrderGuid,
        string? targetStoreCode
    );

    Task<StoreOrderAccessDecision> RequireWarehouseSyncAsync();

    Task<StoreOrderAccessDecision> RequireImportPriceRefreshAsync(string orderGuid);

    Task<StoreOrderAccessDecision> RequireStoreScopeAsync(string? storeCode);

    Task<StoreOrderAccessDecision> RequireAssignedStoreScopeAsync(string? storeCode);

    Task<StoreOrderAccessDecision> RequireOrderScopeAsync(string orderGuid);

    Task<StoreOrderAccessDecision> RequireOrderReadScopeAsync(string orderGuid);

    Task<StoreOrderAccessDecision> RequireOrderScopesAsync(
        IEnumerable<string?> orderGuids
    );

    Task<IReadOnlyList<string>> GetAccessibleStoreCodesAsync();

    Task<StoreOrderStoreSelectionDecision> ResolveMissingOrdersSyncScopeAsync(
        IEnumerable<string?>? storeCodes,
        string? legacyStoreCode
    );

    Task<StoreOrderStoreSelectionDecision> ResolveHqIncrementalSyncScopeAsync(
        IEnumerable<string?>? storeCodes,
        string? legacyStoreCode
    );

    Task<StoreOrderAccessDecision> RequireScopedJobStoresAsync(
        IEnumerable<string?>? storeCodes
    );
}
