using System.Security.Claims;
using BlazorApp.Shared.Constants;

namespace BlazorApp.Api.Services
{
    /// <summary>
    /// 「谁不限分店」的集中口径。
    /// - 门店管理 / 门店数据（考勤、员工、工资、日结、操作日志、销售报表、账号授权等）：只有管理员不限分店；
    ///   开关打开后，仓库经理和店长一样按 UserStore 收口（IsPrimary = 管理，其余 = 查看）。
    /// - 仓库本职（门店订货 / 发货、拣货、仓库商品、货柜新品、本地采购等）：管理员与仓库经理都不限分店，不受开关影响。
    /// 开关 Access:WarehouseManagerStoreScoped 默认 false（旧口径：仓库经理视同管理员不限分店），
    /// 先补齐仓库经理的分店分配再打开；需要回退时关掉即可，不用回滚代码。
    /// </summary>
    public static class StoreScopePolicy
    {
        public const string ConfigurationKey = "Access:WarehouseManagerStoreScoped";

        private static bool _configuredWarehouseManagerStoreScoped;

        // 关键逻辑：测试用异步上下文覆盖开关，只影响当前测试的调用链，不会串到并行运行的其他测试。
        private static readonly AsyncLocal<bool?> TestOverride = new();

        /// <summary>开关是否打开：仓库经理在门店管理类功能里按分配的分店收口。</summary>
        public static bool WarehouseManagerStoreScoped =>
            TestOverride.Value ?? _configuredWarehouseManagerStoreScoped;

        public static void Configure(bool warehouseManagerStoreScoped) =>
            _configuredWarehouseManagerStoreScoped = warehouseManagerStoreScoped;

        /// <summary>测试专用：在 using 范围内临时设定开关。</summary>
        internal static IDisposable OverrideForTest(bool warehouseManagerStoreScoped)
        {
            var previous = TestOverride.Value;
            TestOverride.Value = warehouseManagerStoreScoped;
            return new RestoreOverride(previous);
        }

        public static bool IsSuperAdmin(IEnumerable<string>? roleNames) =>
            roleNames?.Any(Permissions.IsSuperAdminRole) == true;

        public static bool IsWarehouseManager(IEnumerable<string>? roleNames) =>
            roleNames?.Any(roleName =>
                !string.IsNullOrWhiteSpace(roleName)
                && Permissions.WarehouseManagerRoleNames.Contains(roleName.Trim(), StringComparer.OrdinalIgnoreCase)
            ) == true;

        /// <summary>门店管理 / 门店数据（A 类）是否不限分店：管理员恒为是；仓库经理只在开关关闭（旧口径）时为是。</summary>
        public static bool HasAllStoreScope(IEnumerable<string>? roleNames) =>
            IsSuperAdmin(roleNames)
            || (!WarehouseManagerStoreScoped && IsWarehouseManager(roleNames));

        /// <summary>仓库本职（B 类）是否不限分店：管理员与仓库经理都是，不受开关影响。</summary>
        public static bool HasWarehouseWideScope(IEnumerable<string>? roleNames) =>
            IsSuperAdmin(roleNames) || IsWarehouseManager(roleNames);

        /// <summary>
        /// 门店管理类功能里按「店长」口径收口的角色：店长；开关打开后仓库经理也按这条路走（看自己 UserStore 的分店）。
        /// </summary>
        public static bool IsStoreScopedManager(IEnumerable<string>? roleNames) =>
            roleNames?.Any(Permissions.IsStoreManagerRole) == true
            || (WarehouseManagerStoreScoped && IsWarehouseManager(roleNames));

        public static bool HasAllStoreScope(ClaimsPrincipal? user) => HasAllStoreScope(RoleNamesOf(user));

        public static bool HasWarehouseWideScope(ClaimsPrincipal? user) => HasWarehouseWideScope(RoleNamesOf(user));

        public static bool IsStoreScopedManager(ClaimsPrincipal? user) => IsStoreScopedManager(RoleNamesOf(user));

        private static IEnumerable<string> RoleNamesOf(ClaimsPrincipal? user) =>
            user?.Claims
                .Where(claim => claim.Type == ClaimTypes.Role && !string.IsNullOrWhiteSpace(claim.Value))
                .Select(claim => claim.Value)
            ?? Enumerable.Empty<string>();

        private sealed class RestoreOverride(bool? previous) : IDisposable
        {
            public void Dispose() => TestOverride.Value = previous;
        }
    }
}
