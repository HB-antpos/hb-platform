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
}
