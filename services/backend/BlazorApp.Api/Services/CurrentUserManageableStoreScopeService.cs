using System.Security.Claims;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Http;

namespace BlazorApp.Api.Services
{
    public class CurrentUserManageableStoreScopeService : ICurrentUserManageableStoreScopeService
    {
        internal static IReadOnlyList<string> AdminRoleAliases { get; } =
            Permissions.SuperAdminRoleNames;

        internal static IReadOnlyList<string> StoreManagerRoleAliases { get; } =
            Permissions.StoreManagerRoleNames;

        internal static IReadOnlyList<string> WarehouseManagerRoleAliases { get; } =
            Permissions.WarehouseManagerRoleNames;

        private readonly SqlSugarContext _context;
        private readonly ICurrentUserService _currentUserService;
        private readonly IHttpContextAccessor _httpContextAccessor;

        public CurrentUserManageableStoreScopeService(
            SqlSugarContext context,
            ICurrentUserService currentUserService,
            IHttpContextAccessor httpContextAccessor
        )
        {
            _context = context;
            _currentUserService = currentUserService;
            _httpContextAccessor = httpContextAccessor;
        }

        public Task<CurrentUserManageableStoreScope> GetScopeAsync() => GetScopeCoreAsync(primaryOnly: true);

        // 店长可查看的分店：UserStore 全部关联分店（含非主分店）；管理员与仓库经理仍为不限分店。
        public Task<CurrentUserManageableStoreScope> GetAssignedStoreScopeAsync() => GetScopeCoreAsync(primaryOnly: false);

        // primaryOnly=true 为可管理分店口径（只认主分店），供店员管理等写操作使用，行为保持不变。
        private async Task<CurrentUserManageableStoreScope> GetScopeCoreAsync(bool primaryOnly)
        {
            var user = _httpContextAccessor.HttpContext?.User;
            var actorLabel = _currentUserService.GetCurrentUsername();
            var userGuid = ResolveUserGuid(user);

            if (user?.Identity?.IsAuthenticated != true)
            {
                return new CurrentUserManageableStoreScope
                {
                    ActorLabel = actorLabel,
                    Message = "当前账号未登录",
                };
            }

            if (HasAnyRole(user, AdminRoleAliases) || HasAnyRole(user, WarehouseManagerRoleAliases))
            {
                return new CurrentUserManageableStoreScope
                {
                    IsAllowed = true,
                    IsAuthenticated = true,
                    IsAdmin = true,
                    ActorLabel = actorLabel,
                    UserGuid = userGuid ?? string.Empty,
                };
            }

            if (!HasAnyRole(user, StoreManagerRoleAliases))
            {
                return new CurrentUserManageableStoreScope
                {
                    ActorLabel = actorLabel,
                    IsAuthenticated = true,
                    UserGuid = userGuid ?? string.Empty,
                    Message = "当前账号没有店员管理权限",
                };
            }

            if (string.IsNullOrWhiteSpace(userGuid))
            {
                return new CurrentUserManageableStoreScope
                {
                    ActorLabel = actorLabel,
                    IsAuthenticated = true,
                    IsStoreManager = true,
                    Message = "无法识别当前账号",
                };
            }

            var stores = await _context.Db.Queryable<UserStore>()
                .InnerJoin<Store>((us, s) => us.StoreGUID == s.StoreGUID)
                .Where((us, s) =>
                    us.UserGUID == userGuid && !us.IsDeleted && !s.IsDeleted
                )
                .WhereIF(primaryOnly, (us, s) => us.IsPrimary)
                .Select((us, s) => new { us.StoreGUID, s.StoreCode })
                .ToListAsync();

            if (!stores.Any())
            {
                return new CurrentUserManageableStoreScope
                {
                    ActorLabel = actorLabel,
                    IsAuthenticated = true,
                    IsStoreManager = true,
                    UserGuid = userGuid,
                    Message = "当前店长未分配任何可管理分店",
                };
            }

            return new CurrentUserManageableStoreScope
            {
                IsAllowed = true,
                IsAuthenticated = true,
                IsStoreManager = true,
                ActorLabel = actorLabel,
                UserGuid = userGuid,
                StoreGuids = stores
                    .Select(item => item.StoreGUID)
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                StoreCodes = stores
                    .Select(item => item.StoreCode)
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
            };
        }

        public async Task<bool> CanManageStoreAsync(string storeGuid)
        {
            var scope = await GetScopeAsync();
            return scope.IsAllowed && scope.CanAccessStoreGuid(storeGuid);
        }

        public async Task<IReadOnlyList<string>> GetAccessibleStoreCodesAsync()
        {
            var scope = await GetScopeAsync();
            return scope.IsAllowed ? scope.StoreCodes : Array.Empty<string>();
        }

        public async Task<bool> CanAccessStoreCodeAsync(string storeCode)
        {
            if (string.IsNullOrWhiteSpace(storeCode))
            {
                return false;
            }

            var scope = await GetScopeAsync();
            return scope.IsAllowed && scope.CanAccessStoreCode(storeCode.Trim());
        }

        public async Task<bool> CanAccessOrderAsync(string orderGuid)
        {
            if (string.IsNullOrWhiteSpace(orderGuid))
            {
                return false;
            }

            var scope = await GetScopeAsync();
            if (!scope.IsAllowed)
            {
                return false;
            }

            if (scope.IsAdmin)
            {
                return true;
            }

            var storeCode = await _context.Db.Queryable<WareHouseOrder>()
                .Where(item => item.OrderGUID == orderGuid && !item.IsDeleted)
                .Select(item => item.StoreCode)
                .FirstAsync();

            return !string.IsNullOrWhiteSpace(storeCode) && scope.CanAccessStoreCode(storeCode);
        }

        public async Task<bool> CanManageUserAsync(string userGuid)
        {
            var scope = await GetScopeAsync();
            if (!scope.IsAllowed)
            {
                return false;
            }

            if (scope.IsAdmin)
            {
                return true;
            }

            return await _context.Db.Queryable<UserStore>()
                .Where(item => item.UserGUID == userGuid && !item.IsDeleted)
                .AnyAsync(item => scope.StoreGuids.Contains(item.StoreGUID));
        }

        private static string? ResolveUserGuid(ClaimsPrincipal? user)
        {
            return user?.FindFirst("userGuid")?.Value
                ?? user?.FindFirst("userId")?.Value
                ?? user?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        }

        internal static bool HasAnyRole(ClaimsPrincipal? user, IReadOnlyCollection<string> roles)
        {
            return user?.Claims.Any(claim =>
                claim.Type == ClaimTypes.Role
                && roles.Any(role => claim.Value.Equals(role, StringComparison.OrdinalIgnoreCase))
            ) == true;
        }
    }
}
