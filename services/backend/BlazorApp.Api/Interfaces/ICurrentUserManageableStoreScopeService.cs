using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace BlazorApp.Api.Interfaces
{
    public class CurrentUserManageableStoreScope
    {
        public bool IsAllowed { get; set; }
        public bool IsAuthenticated { get; set; }
        public bool IsAdmin { get; set; }
        public bool IsStoreManager { get; set; }
        /// <summary>当前账号是仓库经理（或管理员）：仓库本职业务不限分店；门店管理类功能是否收口看 StoreScopePolicy 开关。</summary>
        public bool IsWarehouseWide { get; set; }
        public string ActorLabel { get; set; } = "system";
        public string Message { get; set; } = string.Empty;
        public string UserGuid { get; set; } = string.Empty;
        public IReadOnlyList<string> StoreGuids { get; set; } = new List<string>();
        public IReadOnlyList<string> StoreCodes { get; set; } = new List<string>();

        public bool CanAccessStoreGuid(string storeGuid) =>
            IsAdmin
            || StoreGuids.Any(item => item.Equals(storeGuid, System.StringComparison.OrdinalIgnoreCase));

        public bool CanAccessStoreCode(string storeCode) =>
            IsAdmin
            || StoreCodes.Any(item => item.Equals(storeCode, System.StringComparison.OrdinalIgnoreCase));
    }

    public interface ICurrentUserManageableStoreScopeService
    {
        Task<CurrentUserManageableStoreScope> GetScopeAsync();

        // 只读查看类功能（员工操作日志）用：店长取 UserStore 中全部关联分店，而不只是主分店。
        // 默认实现沿用可管理分店口径，既有测试替身无需改动；真实服务覆盖为全部关联分店。
        Task<CurrentUserManageableStoreScope> GetAssignedStoreScopeAsync() => GetScopeAsync();
        Task<IReadOnlyList<string>> GetAccessibleStoreCodesAsync();
        Task<bool> CanAccessStoreCodeAsync(string storeCode);
        Task<bool> CanAccessOrderAsync(string orderGuid);
        Task<bool> CanManageStoreAsync(string storeGuid);
        Task<bool> CanManageUserAsync(string userGuid);
    }

    /// <summary>真实范围服务实现的仓库口径（仓库经理始终不限分店）；测试替身不实现时由扩展方法退回门店口径。</summary>
    internal interface IWarehouseStoreScopeProvider
    {
        Task<CurrentUserManageableStoreScope> GetWarehouseScopeAsync();
        Task<IReadOnlyList<string>> GetWarehouseAccessibleStoreCodesAsync();
        Task<bool> CanAccessStoreCodeForWarehouseAsync(string storeCode);
        Task<bool> CanAccessOrderForWarehouseAsync(string orderGuid);
    }

    /// <summary>
    /// 仓库本职业务（订货 / 发货、商品主数据、货柜新品等）取分店范围：仓库经理始终不限分店，不受 StoreScopePolicy 开关影响。
    /// 用扩展方法而不是接口默认实现：Moq 替身不会执行接口默认实现（返回 null），这里对未实现仓库口径的替身退回门店口径。
    /// </summary>
    public static class WarehouseStoreScopeExtensions
    {
        public static Task<CurrentUserManageableStoreScope> GetWarehouseScopeAsync(
            this ICurrentUserManageableStoreScopeService service
        ) => service is IWarehouseStoreScopeProvider provider
            ? provider.GetWarehouseScopeAsync()
            : service.GetScopeAsync();

        public static Task<IReadOnlyList<string>> GetWarehouseAccessibleStoreCodesAsync(
            this ICurrentUserManageableStoreScopeService service
        ) => service is IWarehouseStoreScopeProvider provider
            ? provider.GetWarehouseAccessibleStoreCodesAsync()
            : service.GetAccessibleStoreCodesAsync();

        public static Task<bool> CanAccessStoreCodeForWarehouseAsync(
            this ICurrentUserManageableStoreScopeService service,
            string storeCode
        ) => service is IWarehouseStoreScopeProvider provider
            ? provider.CanAccessStoreCodeForWarehouseAsync(storeCode)
            : service.CanAccessStoreCodeAsync(storeCode);

        public static Task<bool> CanAccessOrderForWarehouseAsync(
            this ICurrentUserManageableStoreScopeService service,
            string orderGuid
        ) => service is IWarehouseStoreScopeProvider provider
            ? provider.CanAccessOrderForWarehouseAsync(orderGuid)
            : service.CanAccessOrderAsync(orderGuid);
    }
}
