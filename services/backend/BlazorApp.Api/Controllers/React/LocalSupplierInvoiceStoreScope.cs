using System.Security.Claims;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Controllers.React
{
    /// <summary>
    /// 分店进货单的分店范围口径：进货单控制器与澳洲供应商列表（最近进货）共用，避免两处判定漂移。
    /// </summary>
    internal static class LocalSupplierInvoiceStoreScope
    {
        /// <summary>超级管理员或仓库经理看全部分店。</summary>
        public static bool IsFullStoreAccessUser(ClaimsPrincipal? user)
        {
            if (user == null) return false;
            return user.Claims.Any(c =>
                c.Type == ClaimTypes.Role
                // 超级管理员角色统一走共享权限常量，避免“管理员”通过授权元数据却被分店 gate 拦截。
                && (Permissions.IsSuperAdminRole(c.Value)
                    || c.Value.Equals("WarehouseManager", StringComparison.OrdinalIgnoreCase))
            );
        }

        public static string GetCurrentUserGuid(ClaimsPrincipal? user)
        {
            return user?.FindFirst("userId")?.Value
                ?? user?.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? string.Empty;
        }

        /// <summary>当前用户在 UserStore 里绑定的分店代码；未登录或未绑定时为空列表。</summary>
        public static async Task<List<string>> GetCurrentUserStoreCodesAsync(
            ISqlSugarClient db,
            ClaimsPrincipal? user
        )
        {
            var userGuid = GetCurrentUserGuid(user);
            if (string.IsNullOrEmpty(userGuid))
                return new List<string>();

            var storeGuids = await db.Queryable<UserStore>()
                .Where(us => us.UserGUID == userGuid)
                .Select(us => us.StoreGUID)
                .ToListAsync();

            if (!storeGuids.Any())
                return new List<string>();

            var codes = await db.Queryable<Store>()
                .Where(s => storeGuids.Contains(s.StoreGUID))
                .Select(s => s.StoreCode)
                .ToListAsync();

            return codes.Where(c => !string.IsNullOrEmpty(c)).ToList();
        }
    }
}
