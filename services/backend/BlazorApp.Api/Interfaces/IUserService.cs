using BlazorApp.Shared.Models;
using BlazorApp.Shared.DTOs;

namespace BlazorApp.Api.Interfaces
{
    /// <summary>
    /// 用户管理服务接口
    /// 🧑‍💼 提供用户数据的CRUD操作和权限管理功能
    /// </summary>
    public interface IUserService
    {
        /// <summary>
        /// 获取用户列表（分页）
        /// </summary>
        /// <param name="query">查询参数</param>
        /// <returns>分页的用户数据</returns>
        Task<ApiResponse<PagedResult<UserDto>>> GetUsersAsync(UserQueryDto query);

        /// <summary>
        /// 获取用户列表（高性能版本）
        /// ⚡ 优化查询性能，解决N+1问题，使用单一复合查询
        /// </summary>
        /// <param name="query">查询参数</param>
        /// <returns>分页的用户数据</returns>
        Task<ApiResponse<PagedResult<UserDto>>> GetUsersOptimizedAsync(UserQueryDto query);

        /// <summary>
        /// 性能测试方法 - 比较不同查询方式的性能
        /// </summary>
        /// <param name="query">查询参数</param>
        /// <returns>性能测试结果</returns>
        Task<ApiResponse<object>> PerformanceTestAsync(UserQueryDto query);

        /// <summary>
        /// 根据GUID获取用户详情
        /// </summary>
        /// <param name="userGuid">用户GUID</param>
        /// <returns>用户详情</returns>
        Task<ApiResponse<UserDetailDto>> GetUserByGuidAsync(string userGuid);

        /// <summary>
        /// 获取用户登录记录
        /// </summary>
        /// <param name="userGuid">用户GUID</param>
        /// <param name="query">分页查询参数</param>
        /// <returns>登录记录分页数据</returns>
        Task<ApiResponse<PagedResult<UserLoginRecordDto>>> GetUserLoginRecordsAsync(
            string userGuid,
            UserLoginRecordQueryDto query
        );

        /// <summary>
        /// 根据用户名获取用户
        /// </summary>
        /// <param name="username">用户名</param>
        /// <returns>用户信息</returns>
        Task<ApiResponse<UserDto>> GetUserByUsernameAsync(string username);

        /// <summary>
        /// 根据邮箱获取用户
        /// </summary>
        /// <param name="email">邮箱</param>
        /// <returns>用户信息</returns>
        Task<ApiResponse<UserDto>> GetUserByEmailAsync(string email);

        /// <summary>
        /// 创建新用户
        /// </summary>
        /// <param name="dto">创建用户DTO</param>
        /// <returns>创建的用户信息</returns>
        Task<ApiResponse<UserDto>> CreateUserAsync(CreateUserDto dto);

        /// <summary>
        /// 批量创建用户（按分店代码、用户名、密码，分配默认角色与对应分店）
        /// </summary>
        /// <param name="dto">批量创建请求</param>
        /// <returns>成功/失败数量及失败明细</returns>
        Task<ApiResponse<BatchCreateUserResultDto>> BatchCreateUsersAsync(BatchCreateUserRequestDto dto);

        /// <summary>
        /// 根据GUID更新用户
        /// </summary>
        /// <param name="userGuid">用户GUID</param>
        /// <param name="dto">更新用户DTO</param>
        /// <returns>更新的用户信息</returns>
        Task<ApiResponse<UserDto>> UpdateUserByGuidAsync(string userGuid, UpdateUserDto dto);

        /// <summary>
        /// 根据GUID删除用户
        /// </summary>
        /// <param name="userGuid">用户GUID</param>
        /// <returns>删除结果</returns>
        Task<ApiResponse<bool>> DeleteUserByGuidAsync(string userGuid);

        /// <summary>
        /// 根据GUID更新用户状态
        /// </summary>
        /// <param name="userGuid">用户GUID</param>
        /// <param name="isActive">是否激活</param>
        /// <returns>更新结果</returns>
        Task<ApiResponse<bool>> UpdateUserStatusByGuidAsync(string userGuid, bool isActive);

        /// <summary>
        /// 更新用户密码
        /// </summary>
        /// <param name="userGuid">用户GUID</param>
        /// <param name="dto">密码更新DTO</param>
        /// <returns>更新结果</returns>
        Task<ApiResponse<bool>> UpdateUserPasswordByGuidAsync(string userGuid, UpdateUserPasswordDto dto);

        /// <summary>
        /// 为用户分配角色
        /// </summary>
        /// <param name="userGuid">用户GUID</param>
        /// <param name="dto">角色分配DTO</param>
        /// <returns>分配结果</returns>
        Task<ApiResponse<bool>> AssignRolesToUserAsync(string userGuid, UserRoleAssignmentDto dto);

        /// <summary>
        /// 为用户分配分店
        /// </summary>
        /// <param name="userGuid">用户GUID</param>
        /// <param name="storeAssignments">分店分配DTO列表</param>
        /// <returns>分配结果</returns>
        Task<ApiResponse<bool>> AssignStoresToUserAsync(string userGuid, List<UserStoreAssignmentDto> storeAssignments);

        /// <summary>
        /// 获取用户的角色列表
        /// </summary>
        /// <param name="userGuid">用户GUID</param>
        /// <returns>角色列表</returns>
        Task<ApiResponse<List<RoleDto>>> GetUserRolesAsync(string userGuid);

        /// <summary>
        /// 获取用户的分店列表
        /// </summary>
        /// <param name="userGuid">用户GUID</param>
        /// <returns>分店列表</returns>
        Task<ApiResponse<List<UserStoreDto>>> GetUserStoresAsync(string userGuid);

        /// <summary>
        /// 从用户移除角色
        /// </summary>
        /// <param name="userGuid">用户GUID</param>
        /// <param name="roleGuid">角色GUID</param>
        /// <returns>移除结果</returns>
        Task<ApiResponse<bool>> RemoveRoleFromUserAsync(string userGuid, string roleGuid);

        /// <summary>
        /// 从用户移除分店
        /// </summary>
        /// <param name="userGuid">用户GUID</param>
        /// <param name="storeGuid">分店GUID</param>
        /// <returns>移除结果</returns>
        Task<ApiResponse<bool>> RemoveStoreFromUserAsync(string userGuid, string storeGuid);

        /// <summary>
        /// 批量管理用户
        /// </summary>
        /// <param name="dto">批量操作DTO</param>
        /// <returns>操作结果</returns>
        Task<ApiResponse<bool>> BatchManageUsersAsync(BatchUserOperationDto dto);

        /// <summary>
        /// 批量为多个用户添加 / 移除分店关联；DryRun 时只返回影响、不写入
        /// </summary>
        Task<ApiResponse<BatchUserStoreOperationResultDto>> BatchManageUserStoresAsync(
            BatchUserStoreOperationDto dto
        );

        /// <summary>
        /// 导入用户
        /// </summary>
        /// <param name="users">用户导入数据</param>
        /// <returns>导入结果</returns>
        Task<ApiResponse<ImportUserResultDto>> ImportUsersAsync(List<ImportUserDto> users);

        /// <summary>
        /// 导出用户
        /// </summary>
        /// <param name="query">查询参数</param>
        /// <returns>用户数据</returns>
        Task<ApiResponse<List<UserDto>>> ExportUsersAsync(UserQueryDto query);

        /// <summary>
        /// 获取用户统计信息
        /// </summary>
        /// <returns>统计信息</returns>
        Task<ApiResponse<UserStatisticsDto>> GetUserStatisticsAsync();

        /// <summary>
        /// 检查用户名是否可用
        /// </summary>
        /// <param name="username">用户名</param>
        /// <param name="excludeUserGuid">排除的用户GUID（更新时使用）</param>
        /// <returns>是否可用</returns>
        Task<ApiResponse<bool>> IsUsernameAvailableAsync(string username, string? excludeUserGuid = null);

        /// <summary>
        /// 检查邮箱是否可用
        /// </summary>
        /// <param name="email">邮箱</param>
        /// <param name="excludeUserGuid">排除的用户GUID（更新时使用）</param>
        /// <returns>是否可用</returns>
        Task<ApiResponse<bool>> IsEmailAvailableAsync(string email, string? excludeUserGuid = null);

        /// <summary>
        /// 重置用户密码
        /// </summary>
        /// <param name="userGuid">用户GUID</param>
        /// <returns>新密码</returns>
        Task<ApiResponse<string>> ResetUserPasswordAsync(string userGuid);

        /// <summary>
        /// 锁定/解锁用户
        /// </summary>
        /// <param name="userGuid">用户GUID</param>
        /// <param name="isLocked">是否锁定</param>
        /// <returns>操作结果</returns>
        Task<ApiResponse<bool>> LockUserAsync(string userGuid, bool isLocked);
    }
}
