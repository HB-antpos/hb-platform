namespace BlazorApp.Api.Services
{
    /// <summary>
    /// 复制直接权限的纯计算：给定目标现有直接权限与可复制集合，算出每人新增/移除。
    /// 不碰数据库，便于单测覆盖合并/覆盖与受限操作者的边界。
    /// </summary>
    internal static class UserPermissionCopyPlanner
    {
        internal sealed record TargetPlan(
            string UserGuid,
            IReadOnlyList<string> Added,
            IReadOnlyList<string> Removed,
            int UnchangedCount
        );

        /// <param name="existing">目标当前全部直接权限。</param>
        /// <param name="transferable">源上可复制的权限集合（已按操作者可分配范围过滤）。</param>
        /// <param name="replace">true＝覆盖；false＝合并。</param>
        /// <param name="managedScope">
        /// 覆盖时允许被移除的范围；受限操作者（店长）只能动自己可委派的子集，null 表示不限制（管理员）。
        /// </param>
        internal static TargetPlan Plan(
            string userGuid,
            IEnumerable<string> existing,
            IReadOnlyCollection<string> transferable,
            bool replace,
            IReadOnlySet<string>? managedScope
        )
        {
            var existingSet = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
            var transferableSet = new HashSet<string>(transferable, StringComparer.OrdinalIgnoreCase);

            var added = transferableSet
                .Where(code => !existingSet.Contains(code))
                .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var removed = replace
                ? existingSet
                    .Where(code => !transferableSet.Contains(code))
                    .Where(code => managedScope == null || managedScope.Contains(code))
                    .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : new List<string>();
            var unchanged = transferableSet.Count(code => existingSet.Contains(code));

            return new TargetPlan(userGuid, added, removed, unchanged);
        }
    }
}
