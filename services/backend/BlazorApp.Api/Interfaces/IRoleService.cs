using BlazorApp.Shared.Models;
using BlazorApp.Shared.DTOs;

namespace BlazorApp.Api.Interfaces
{
    /// <summary>
    /// 角色管理服务接口
    /// 🔐 提供角色数据的CRUD操作和权限管理功能
    /// </summary>
    public interface IRoleService
    {
        /// <summary>
        /// 获取角色列表（分页）
        /// </summary>
        /// <param name="query">查询参数</param>
        /// <returns>分页的角色数据</returns>
        Task<ApiResponse<PagedResult<RoleDto>>> GetRolesAsync(RoleQueryDto query);

        /// <summary>
        /// 获取角色列表（高性能版本）
        /// ⚡ 优化查询性能，解决N+1问题，使用单一复合查询
        /// </summary>
        /// <param name="query">查询参数</param>
        /// <returns>分页的角色数据</returns>
        Task<ApiResponse<PagedResult<RoleDto>>> GetRolesOptimizedAsync(RoleQueryDto query);

        /// <summary>
        /// 角色服务性能测试方法 - 比较不同查询方式的性能
        /// </summary>
        /// <param name="query">查询参数</param>
        /// <returns>性能测试结果</returns>
        Task<ApiResponse<object>> PerformanceTestAsync(RoleQueryDto query);

        /// <summary>
        /// 获取所有活跃角色（不分页）
        /// </summary>
        /// <returns>活跃角色列表</returns>
        Task<ApiResponse<List<RoleDto>>> GetActiveRolesAsync();

        /// <summary>
        /// 获取有效等级严格低于指定账号等级的活跃角色（用于人员列表的角色筛选）。
        /// </summary>
        /// <param name="userGuid">当前操作者</param>
        Task<ApiResponse<List<RoleDto>>> GetActiveRolesBelowUserLevelAsync(string userGuid);

        /// <summary>
        /// 根据GUID获取角色详情
        /// </summary>
        /// <param name="roleGuid">角色GUID</param>
        /// <returns>角色详情</returns>
        Task<ApiResponse<RoleDetailDto>> GetRoleByGuidAsync(string roleGuid);

        /// <summary>
        /// 根据角色名获取角色
        /// </summary>
        /// <param name="roleName">角色名</param>
        /// <returns>角色信息</returns>
        Task<ApiResponse<RoleDto>> GetRoleByNameAsync(string roleName);

        /// <summary>
        /// 创建新角色
        /// </summary>
        /// <param name="dto">创建角色DTO</param>
        /// <returns>创建的角色信息</returns>
        Task<ApiResponse<RoleDto>> CreateRoleAsync(CreateRoleDto dto);

        /// <summary>
        /// 根据GUID更新角色
        /// </summary>
        /// <param name="roleGuid">角色GUID</param>
        /// <param name="dto">更新角色DTO</param>
        /// <returns>更新的角色信息</returns>
        Task<ApiResponse<RoleDto>> UpdateRoleByGuidAsync(string roleGuid, UpdateRoleDto dto);

        /// <summary>
        /// 根据GUID删除角色
        /// </summary>
        /// <param name="roleGuid">角色GUID</param>
        /// <returns>删除结果</returns>
        Task<ApiResponse<bool>> DeleteRoleByGuidAsync(string roleGuid);

        /// <summary>
        /// 根据GUID更新角色状态
        /// </summary>
        /// <param name="roleGuid">角色GUID</param>
        /// <param name="isActive">是否激活</param>
        /// <returns>更新结果</returns>
        Task<ApiResponse<bool>> UpdateRoleStatusByGuidAsync(string roleGuid, bool isActive);

        /// <summary>
        /// 获取角色的用户列表
        /// </summary>
        /// <param name="roleGuid">角色GUID</param>
        /// <param name="query">查询参数</param>
        /// <returns>用户列表</returns>
        Task<ApiResponse<PagedResult<RoleUserDto>>> GetRoleUsersAsync(string roleGuid, RoleQueryDto query);

        /// <summary>
        /// 为角色添加用户
        /// </summary>
        /// <param name="roleGuid">角色GUID</param>
        /// <param name="userGuids">用户GUID列表</param>
        /// <returns>添加结果</returns>
        Task<ApiResponse<bool>> AddUsersToRoleAsync(string roleGuid, List<string> userGuids);

        /// <summary>
        /// 从角色移除用户
        /// </summary>
        /// <param name="roleGuid">角色GUID</param>
        /// <param name="userGuid">用户GUID</param>
        /// <returns>移除结果</returns>
        Task<ApiResponse<bool>> RemoveUserFromRoleAsync(string roleGuid, string userGuid);

        /// <summary>
        /// 批量管理角色
        /// </summary>
        /// <param name="dto">批量操作DTO</param>
        /// <returns>操作结果</returns>
        Task<ApiResponse<bool>> BatchManageRolesAsync(BatchRoleOperationDto dto);

        /// <summary>
        /// 获取角色统计信息
        /// </summary>
        /// <returns>统计信息</returns>
        Task<ApiResponse<RoleStatisticsDto>> GetRoleStatisticsAsync();

        /// <summary>
        /// 检查角色名是否可用
        /// </summary>
        /// <param name="roleName">角色名</param>
        /// <param name="excludeRoleGuid">排除的角色GUID（更新时使用）</param>
        /// <returns>是否可用</returns>
        Task<ApiResponse<bool>> IsRoleNameAvailableAsync(string roleName, string? excludeRoleGuid = null);

        /// <summary>
        /// 获取所有权限列表
        /// </summary>
        /// <returns>权限列表</returns>
        Task<ApiResponse<List<PermissionCategoryDto>>> GetPermissionsAsync();

        /// <summary>
        /// 获取权限目录元数据
        /// </summary>
        /// <returns>权限分组、别名、角色模板与超级管理员角色名</returns>
        Task<ApiResponse<PermissionCatalogDto>> GetPermissionCatalogAsync();

        /// <summary>
        /// 获取角色的权限列表
        /// </summary>
        /// <param name="roleGuid">角色GUID</param>
        /// <returns>权限列表</returns>
        Task<ApiResponse<List<string>>> GetRolePermissionsAsync(string roleGuid);

        /// <summary>
        /// 仅供后端授权与会话聚合使用，不对应外部角色权限读取端点。
        /// </summary>
        Task<ApiResponse<List<string>>> GetRolePermissionsForAuthorizationAsync(string roleGuid);

        /// <summary>
        /// 获取角色权限状态
        /// </summary>
        /// <param name="roleGuid">角色GUID</param>
        /// <returns>显式权限、有效权限和超级管理员状态</returns>
        Task<ApiResponse<RolePermissionStateDto>> GetRolePermissionStateAsync(string roleGuid);

        /// <summary>
        /// 为角色分配权限
        /// </summary>
        /// <param name="roleGuid">角色GUID</param>
        /// <param name="dto">权限分配DTO</param>
        /// <returns>分配结果</returns>
        Task<ApiResponse<bool>> AssignPermissionsToRoleAsync(string roleGuid, RolePermissionAssignmentDto dto);

        /// <summary>
        /// 检查用户是否有指定角色
        /// </summary>
        /// <param name="userGuid">用户GUID</param>
        /// <param name="roleName">角色名</param>
        /// <returns>是否有角色</returns>
        Task<ApiResponse<bool>> UserHasRoleAsync(string userGuid, string roleName);

        /// <summary>
        /// 检查用户是否有指定权限
        /// </summary>
        /// <param name="userGuid">用户GUID</param>
        /// <param name="permission">权限名</param>
        /// <returns>是否有权限</returns>
        Task<ApiResponse<bool>> UserHasPermissionAsync(string userGuid, string permission);

        /// <summary>
        /// 检查用户是否显式拥有指定权限，不展开历史权限别名；超级管理员仍拥有全部权限。
        /// </summary>
        Task<ApiResponse<bool>> UserHasExactPermissionAsync(string userGuid, string permission);

        /// <summary>
        /// 获取用户当前激活角色聚合后的权限快照
        /// </summary>
        /// <param name="userGuid">用户GUID</param>
        /// <returns>超级管理员状态、角色名和有效权限</returns>
        Task<ApiResponse<UserPermissionSnapshotDto>> GetUserPermissionSnapshotAsync(string userGuid);

        /// <summary>
        /// 获取用户权限状态
        /// </summary>
        /// <param name="userGuid">用户GUID</param>
        /// <returns>角色继承权限、用户直接权限和最终有效权限</returns>
        Task<ApiResponse<UserPermissionStateDto>> GetUserPermissionStateAsync(string userGuid);

        /// <summary>
        /// 获取当前操作者对目标员工可见、可分配的权限状态与目录。
        /// </summary>
        Task<ApiResponse<UserAccessPermissionDto>> GetUserAccessPermissionsAsync(
            string actorUserGuid,
            string targetUserGuid
        );

        /// <summary>
        /// 为用户分配直接权限
        /// </summary>
        /// <param name="userGuid">用户GUID</param>
        /// <param name="dto">用户直接权限分配DTO</param>
        /// <returns>分配结果</returns>
        Task<ApiResponse<bool>> AssignPermissionsToUserAsync(
            string userGuid,
            UserPermissionAssignmentDto dto
        );

        /// <summary>
        /// 把源员工的直接权限复制给一个或多个目标员工（可预览差异，写入为原子操作）。
        /// 受限操作者（店长）只能复制自己持有的 POS 收银业务权限，其余自动跳过。
        /// </summary>
        Task<ApiResponse<CopyUserPermissionsResultDto>> CopyUserDirectPermissionsAsync(
            string sourceUserGuid,
            CopyUserPermissionsRequestDto dto
        );

        /// <summary>
        /// 复制角色
        /// </summary>
        /// <param name="sourceRoleGuid">源角色GUID</param>
        /// <param name="newRoleName">新角色名</param>
        /// <param name="newDescription">新角色描述</param>
        /// <returns>新角色</returns>
        Task<ApiResponse<RoleDto>> DuplicateRoleAsync(string sourceRoleGuid, string newRoleName, string? newDescription = null);

        /// <summary>
        /// 获取拥有指定权限的角色列表
        /// </summary>
        /// <param name="permissionCode">权限代码</param>
        /// <returns>角色列表</returns>
        Task<ApiResponse<List<RoleDto>>> GetPermissionRolesAsync(string permissionCode);

        /// <summary>
        /// 为权限分配角色
        /// </summary>
        /// <param name="permissionCode">权限代码</param>
        /// <param name="roleGuids">角色GUID列表</param>
        /// <returns>操作结果</returns>
        Task<ApiResponse<bool>> AssignRolesToPermissionAsync(string permissionCode, List<string> roleGuids);

        /// <summary>
        /// 获取被直接授予指定权限的用户列表（不含通过角色继承的用户）
        /// </summary>
        /// <param name="permissionCode">权限代码</param>
        /// <returns>用户列表，AssignedAt 为直接授权时间</returns>
        Task<ApiResponse<List<RoleUserDto>>> GetPermissionUsersAsync(string permissionCode);

        /// <summary>
        /// 按增量调整指定权限的直接授权用户
        /// </summary>
        /// <param name="permissionCode">权限代码</param>
        /// <param name="dto">新增与移除的用户GUID</param>
        /// <returns>操作结果</returns>
        Task<ApiResponse<bool>> AssignUsersToPermissionAsync(
            string permissionCode,
            PermissionUserAssignmentDto dto
        );

        /// <summary>
        /// 获取每个权限被显式分配的角色数量（仅统计未删除的角色-权限关联；超级管理员的隐式全权限不计入）
        /// </summary>
        /// <returns>权限代码 → 角色数量</returns>
        Task<ApiResponse<Dictionary<string, int>>> GetPermissionRoleCountsAsync();

        /// <summary>
        /// 获取所有权限（扁平列表，用于管理表格）
        /// </summary>
        /// <returns>权限列表</returns>
        Task<ApiResponse<List<SysPermission>>> GetSysPermissionsAsync();

        /// <summary>
        /// 创建新权限
        /// </summary>
        /// <param name="dto">权限信息</param>
        /// <returns>创建的权限对象列表</returns>
        Task<ApiResponse<List<SysPermission>>> CreatePermissionAsync(CreateSysPermissionDto dto);

        /// <summary>
        /// 软删除权限（按代码）
        /// </summary>
        Task<ApiResponse<bool>> DeletePermissionAsync(string code);
    }
}
