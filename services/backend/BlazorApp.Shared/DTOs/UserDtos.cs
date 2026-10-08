using System.ComponentModel.DataAnnotations;

namespace BlazorApp.Shared.DTOs
{
    /// <summary>
    /// 用户查询参数DTO
    /// </summary>
    public class UserQueryDto
    {
        /// <summary>
        /// 页码，从1开始
        /// </summary>
        public int Page { get; set; } = 1;

        /// <summary>
        /// 页码（兼容性属性）
        /// </summary>
        public int PageNumber
        {
            get => Page;
            set => Page = value;
        }

        /// <summary>
        /// 每页大小
        /// </summary>
        public int PageSize { get; set; } = 10;

        /// <summary>
        /// 搜索关键字（用户名、邮箱、全名）
        /// </summary>
        public string? Search { get; set; }

        /// <summary>
        /// 搜索关键字（兼容性属性）
        /// </summary>
        public string? SearchKeyword
        {
            get => Search;
            set => Search = value;
        }

        /// <summary>
        /// 角色GUID过滤
        /// </summary>
        public string? RoleGuid { get; set; }

        /// <summary>
        /// 分店GUID过滤
        /// </summary>
        public string? StoreGuid { get; set; }

        /// <summary>
        /// 状态过滤（null=全部，true=活跃，false=禁用）
        /// </summary>
        public bool? IsActive { get; set; }

        /// <summary>
        /// 排序字段
        /// </summary>
        public string? SortBy { get; set; } = "CreatedAt";

        /// <summary>
        /// 排序方向（asc/desc）
        /// </summary>
        public string? SortDirection { get; set; } = "desc";
    }

    /// <summary>
    /// 用户登录记录查询参数
    /// </summary>
    public class UserLoginRecordQueryDto
    {
        /// <summary>
        /// 页码，从1开始
        /// </summary>
        public int Page { get; set; } = 1;

        /// <summary>
        /// 每页大小
        /// </summary>
        public int PageSize { get; set; } = 10;
    }

    /// <summary>
    /// 用户登录记录DTO
    /// </summary>
    public class UserLoginRecordDto
    {
        public string SessionId { get; set; } = string.Empty;
        public DateTime LoginAt { get; set; }
        public string? IpAddress { get; set; }
        public string? UserAgent { get; set; }
        public DateTime ExpiresAt { get; set; }
        public bool IsRevoked { get; set; }
        public bool IsExpired { get; set; }
        public string Status { get; set; } = string.Empty;
    }

    /// <summary>
    /// 用户基础信息DTO
    /// </summary>
    public class UserDto
    {
        public string UserGUID { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string? FullName { get; set; }
        public string? Phone { get; set; } // 兼容性
        public DateTime? LastLoginAt { get; set; }
        public string? LastLoginIp { get; set; }
        public bool IsActive { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public string? CurrentStore { get; set; } // 兼容性
        public List<string> RoleNames { get; set; } = new();
        public List<string> StoreNames { get; set; } = new();
        public List<RoleDto>? Roles { get; set; } = new();
        public List<UserStoreDto>? Stores { get; set; } = new();
        /// <summary>
        /// 用户权限列表（从所有角色中聚合）
        /// </summary>
        public List<string> Permissions { get; set; } = new();
        /// <summary>
        /// 用户精确权限代码（角色权限 + 用户直接权限的原始代码，未展开别名）；超级管理员返回所有权限。
        /// JSON 序列化为 exactPermissions，供前端精确权限契约使用。
        /// </summary>
        public List<string> ExactPermissions { get; set; } = new();
        /// <summary>
        /// 账号须先修改密码才能继续使用（店长新建或重置密码后）；客户端据此进入强制改密页。
        /// </summary>
        public bool MustChangePassword { get; set; }
    }

    /// <summary>
    /// 用户详细信息DTO
    /// </summary>
    public class UserDetailDto
    {
        public string UserGUID { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string? FullName { get; set; }
        public string? Phone { get; set; } // 兼容性
        public DateTime? LastLoginAt { get; set; }
        public string? LastLoginIp { get; set; }
        public bool IsActive { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public string? CurrentStore { get; set; } // 兼容性
        public List<string> RoleNames { get; set; } = new();
        public List<string> StoreNames { get; set; } = new();
        public List<string> Permissions { get; set; } = new();
        public List<RoleDto>? Roles { get; set; } = new();
        public List<UserStoreDto>? Stores { get; set; } = new();
    }

    /// <summary>
    /// 创建用户DTO
    /// </summary>
    public class CreateUserDto
    {
        [Required(ErrorMessage = "用户名不能为空")]
        [StringLength(50, MinimumLength = 3, ErrorMessage = "用户名长度必须在3-50个字符之间")]
        public string Username { get; set; } = string.Empty;

        [Required(ErrorMessage = "邮箱不能为空")]
        [EmailAddress(ErrorMessage = "邮箱格式不正确")]
        public string Email { get; set; } = string.Empty;

        [Required(ErrorMessage = "密码不能为空")]
        [StringLength(100, MinimumLength = 6, ErrorMessage = "密码长度必须在6-100个字符之间")]
        public string Password { get; set; } = string.Empty;

        /// <summary>
        /// 密码格式：raw 表示 HTTPS 原始密码；clientSha256 表示旧客户端 SHA256。
        /// </summary>
        public string PasswordFormat { get; set; } = string.Empty;

        [StringLength(100, ErrorMessage = "全名长度不能超过100个字符")]
        public string? FullName { get; set; }

        public bool IsActive { get; set; } = true;

        /// <summary>
        /// 分配的角色GUID列表
        /// </summary>
        public List<string> RoleGuids { get; set; } = new();

        /// <summary>
        /// 分配的分店GUID列表
        /// </summary>
        public List<string> StoreGuids { get; set; } = new();
    }

    /// <summary>
    /// 批量创建用户单条项DTO
    /// </summary>
    public class BatchCreateUserItemDto
    {
        [Required(ErrorMessage = "分店代码不能为空")]
        public string StoreCode { get; set; } = string.Empty;

        [Required(ErrorMessage = "用户名不能为空")]
        [StringLength(50, MinimumLength = 3, ErrorMessage = "用户名长度必须在3-50个字符之间")]
        public string Username { get; set; } = string.Empty;

        [Required(ErrorMessage = "密码不能为空")]
        [StringLength(100, MinimumLength = 6, ErrorMessage = "密码长度必须在6-100个字符之间")]
        public string Password { get; set; } = string.Empty;
    }

    /// <summary>
    /// 批量创建用户请求DTO
    /// </summary>
    public class BatchCreateUserRequestDto
    {
        [Required(ErrorMessage = "默认角色不能为空")]
        public string DefaultRoleGuid { get; set; } = string.Empty;

        public List<BatchCreateUserItemDto> Items { get; set; } = new();
    }

    /// <summary>
    /// 批量创建用户失败明细
    /// </summary>
    public class BatchCreateUserFailureItemDto
    {
        public int RowIndex { get; set; }
        public string Username { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
    }

    /// <summary>
    /// 批量创建用户结果DTO
    /// </summary>
    public class BatchCreateUserResultDto
    {
        public int SuccessCount { get; set; }
        public int FailureCount { get; set; }
        public List<BatchCreateUserFailureItemDto> Failures { get; set; } = new();
    }

    /// <summary>
    /// 更新用户DTO
    /// </summary>
    public class UpdateUserDto
    {
        [Required(ErrorMessage = "用户名不能为空")]
        [StringLength(50, MinimumLength = 3, ErrorMessage = "用户名长度必须在3-50个字符之间")]
        public string Username { get; set; } = string.Empty;

        [Required(ErrorMessage = "邮箱不能为空")]
        [EmailAddress(ErrorMessage = "邮箱格式不正确")]
        public string Email { get; set; } = string.Empty;

        [StringLength(100, ErrorMessage = "全名长度不能超过100个字符")]
        public string? FullName { get; set; }

        public bool IsActive { get; set; } = true;
    }

    /// <summary>
    /// 更新用户密码DTO
    /// </summary>
    public class UpdateUserPasswordDto
    {
        [Required(ErrorMessage = "新密码不能为空")]
        [StringLength(100, MinimumLength = 6, ErrorMessage = "密码长度必须在6-100个字符之间")]
        public string NewPassword { get; set; } = string.Empty;

        /// <summary>
        /// 新密码格式：raw 表示 HTTPS 原始密码；clientSha256 表示旧客户端 SHA256。
        /// </summary>
        public string PasswordFormat { get; set; } = string.Empty;

        /// <summary>
        /// 是否强制用户下次登录时更改密码
        /// </summary>
        public bool ForcePasswordChange { get; set; } = false;
    }

    /// <summary>
    /// 更新用户状态DTO
    /// </summary>
    public class UpdateUserStatusDto
    {
        public bool IsActive { get; set; }
    }

    /// <summary>
    /// 用户角色分配DTO
    /// </summary>
    public class UserRoleAssignmentDto
    {
        [Required(ErrorMessage = "角色GUID列表不能为空")]
        public List<string> RoleGuids { get; set; } = new();
    }

    /// <summary>
    /// 用户直接权限分配DTO
    /// </summary>
    public class UserPermissionAssignmentDto
    {
        [Required(ErrorMessage = "权限列表不能为空")]
        public List<string> Permissions { get; set; } = new();
    }

    /// <summary>
    /// 用户权限继承来源DTO
    /// </summary>
    public class UserPermissionInheritedSourceDto
    {
        public string RoleName { get; set; } = string.Empty;
        public List<string> PermissionCodes { get; set; } = new();
    }

    /// <summary>
    /// 用户权限状态DTO
    /// </summary>
    public class UserPermissionStateDto
    {
        public string UserGuid { get; set; } = string.Empty;
        public bool IsSuperAdmin { get; set; }
        public bool ImplicitAllPermissions { get; set; }
        public List<string> InheritedPermissionCodes { get; set; } = new();
        public List<string> DirectPermissionCodes { get; set; } = new();
        public List<string> EffectivePermissionCodes { get; set; } = new();
        public List<UserPermissionInheritedSourceDto> InheritedSources { get; set; } = new();
    }

    /// <summary>
    /// 用户访问权限编辑所需的作用域状态与可分配权限目录。
    /// </summary>
    public class UserAccessPermissionDto
    {
        public UserPermissionStateDto State { get; set; } = new();
        public List<PermissionCategoryDto> Categories { get; set; } = new();
    }

    /// <summary>
    /// 用户分店分配DTO（批量）
    /// </summary>
    public class UserStoreAssignmentBatchDto
    {
        [Required(ErrorMessage = "分店GUID列表不能为空")]
        public List<string> StoreGuids { get; set; } = new();
    }

    /// <summary>
    /// 用户分店信息DTO
    /// </summary>
    public class UserStoreDto
    {
        public string StoreGUID { get; set; } = string.Empty;
        public string StoreName { get; set; } = string.Empty;
        public string StoreCode { get; set; } = string.Empty;
        public bool IsActive { get; set; }
        public bool IsPrimary { get; set; }
        public DateTime AssignedAt { get; set; }
    }

    /// <summary>
    /// 批量用户操作DTO
    /// </summary>
    public class BatchUserOperationDto
    {
        [Required(ErrorMessage = "用户GUID列表不能为空")]
        public List<string> UserGuids { get; set; } = new();

        /// <summary>
        /// 用户GUID列表（兼容性属性）
        /// </summary>
        public List<string> UserGUIDs
        {
            get => UserGuids;
            set => UserGuids = value;
        }

        [Required(ErrorMessage = "操作类型不能为空")]
        public string Operation { get; set; } = string.Empty; // activate, deactivate, delete, assign_role, remove_role

        /// <summary>
        /// 操作类型（兼容性属性）
        /// </summary>
        public string Action
        {
            get => Operation;
            set => Operation = value;
        }

        /// <summary>
        /// 操作相关的数据（如角色GUID、分店GUID等）
        /// </summary>
        public Dictionary<string, object>? OperationData { get; set; }
    }

    /// <summary>
    /// 批量为多个用户添加 / 移除分店关联（一个事务内完成，任一目标不合法则整批不写入）。
    /// </summary>
    public class BatchUserStoreOperationDto
    {
        /// <summary>最多一次处理的用户数。</summary>
        public const int MaxUserCount = 100;

        /// <summary>最多一次处理的分店数。</summary>
        public const int MaxStoreCount = 50;

        [Required(ErrorMessage = "用户GUID列表不能为空")]
        public List<string> UserGuids { get; set; } = new();

        [Required(ErrorMessage = "分店GUID列表不能为空")]
        public List<string> StoreGuids { get; set; } = new();

        /// <summary>add 或 remove。</summary>
        [Required(ErrorMessage = "操作类型不能为空")]
        public string Operation { get; set; } = string.Empty;

        /// <summary>仅 add：同时设为可管理分店（只有管理员可以授予）；已有的普通关联会升级为可管理。</summary>
        public bool AsManageable { get; set; }

        /// <summary>只计算影响、不写入，用于确认前预览。</summary>
        public bool DryRun { get; set; }
    }

    /// <summary>
    /// 批量分店操作的结果（预演与正式执行返回同一结构）。
    /// </summary>
    public class BatchUserStoreOperationResultDto
    {
        public bool DryRun { get; set; }

        /// <summary>新建的分店关联数。</summary>
        public int AddedCount { get; set; }

        /// <summary>由普通关联升级为可管理的关联数。</summary>
        public int UpgradedCount { get; set; }

        /// <summary>删除的分店关联数（含可管理关联）。</summary>
        public int RemovedCount { get; set; }

        /// <summary>删除的关联中属于可管理关联的数量。</summary>
        public int RemovedManageableCount { get; set; }

        /// <summary>已经是目标状态、无需变动的用户×分店组合数。</summary>
        public int UnchangedCount { get; set; }

        /// <summary>店长无权移除的可管理关联数（原样保留）。</summary>
        public int ProtectedManageableCount { get; set; }

        /// <summary>至少有一处变动的用户数。</summary>
        public int AffectedUserCount { get; set; }

        /// <summary>因移除最后一个可管理分店而失去店长角色的用户名。</summary>
        public List<string> UsersLosingStoreManagerRole { get; set; } = new();

        /// <summary>因获得首个可管理分店而新获得店长角色的用户名。</summary>
        public List<string> UsersGainingStoreManagerRole { get; set; } = new();
    }

    /// <summary>
    /// 用户导入DTO
    /// </summary>
    public class ImportUserDto
    {
        public string Username { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string? FullName { get; set; }
        public string Password { get; set; } = string.Empty;
        public string PasswordFormat { get; set; } = string.Empty;
        public List<string> RoleNames { get; set; } = new();
        public List<string> StoreCodes { get; set; } = new();
    }

    /// <summary>
    /// 用户导入结果DTO
    /// </summary>
    public class ImportUserResultDto
    {
        public int TotalCount { get; set; }
        public int SuccessCount { get; set; }
        public int FailureCount { get; set; }
        public List<string> Errors { get; set; } = new();
        public List<UserDto> ImportedUsers { get; set; } = new();
    }

    /// <summary>
    /// 重置密码DTO
    /// </summary>
    public class ResetPasswordDto
    {
        [Required(ErrorMessage = "新密码不能为空")]
        [StringLength(100, MinimumLength = 6, ErrorMessage = "密码长度必须在6-100个字符之间")]
        public string NewPassword { get; set; } = string.Empty;

        /// <summary>
        /// 是否强制用户下次登录时更改密码
        /// </summary>
        public bool RequireChangePassword { get; set; } = false;
    }

    /// <summary>
    /// 更改密码DTO
    /// </summary>
    public class ChangePasswordDto
    {
        [Required(ErrorMessage = "当前密码不能为空")]
        public string CurrentPassword { get; set; } = string.Empty;

        [Required(ErrorMessage = "新密码不能为空")]
        [StringLength(100, MinimumLength = 6, ErrorMessage = "密码长度必须在6-100个字符之间")]
        public string NewPassword { get; set; } = string.Empty;

        [Required(ErrorMessage = "确认密码不能为空")]
        [Compare(nameof(NewPassword), ErrorMessage = "确认密码与新密码不匹配")]
        public string ConfirmPassword { get; set; } = string.Empty;
    }

    /// <summary>
    /// 用户角色信息DTO
    /// </summary>
    public class UserRoleDto
    {
        public string UserGUID { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public string? FullName { get; set; }
        public string Email { get; set; } = string.Empty;
        public string RoleGUID { get; set; } = string.Empty;
        public string RoleName { get; set; } = string.Empty;
        public bool IsActive { get; set; }
        public DateTime AssignedAt { get; set; }
    }

    /// <summary>
    /// 用户导出查询DTO
    /// </summary>
    public class UserExportQueryDto
    {
        /// <summary>
        /// 搜索关键字（用户名、邮箱、全名）
        /// </summary>
        public string? SearchKeyword { get; set; }

        /// <summary>
        /// 角色GUID过滤
        /// </summary>
        public string? RoleGuid { get; set; }

        /// <summary>
        /// 分店GUID过滤
        /// </summary>
        public string? StoreGuid { get; set; }

        /// <summary>
        /// 状态过滤（null=全部，true=活跃，false=禁用）
        /// </summary>
        public bool? IsActive { get; set; }

        /// <summary>
        /// 导出格式（xlsx, csv）
        /// </summary>
        public string Format { get; set; } = "xlsx";

        /// <summary>
        /// 包含的字段
        /// </summary>
        public List<string> IncludeFields { get; set; } = new();
    }

    /// <summary>
    /// 用户导入结果DTO
    /// </summary>
    public class UserImportResultDto
    {
        public int TotalCount { get; set; }
        public int SuccessCount { get; set; }
        public int ErrorCount { get; set; }
        public bool HasErrors => ErrorCount > 0;
        public List<ImportErrorDto> Errors { get; set; } = new();
        public List<UserDto> ImportedUsers { get; set; } = new();
    }

    /// <summary>
    /// 导入错误DTO
    /// </summary>
    public class ImportErrorDto
    {
        public int RowNumber { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
        public string? FieldName { get; set; }
        public string? FieldValue { get; set; }
    }

    /// <summary>
    /// 用户分店分配扩展DTO
    /// </summary>
    public class UserStoreAssignmentDto
    {
        [Required(ErrorMessage = "分店GUID不能为空")]
        public string StoreGUID { get; set; } = string.Empty;

        /// <summary>
        /// 权限级别（ReadOnly, ReadWrite, Admin）
        /// </summary>
        public string AccessLevel { get; set; } = "ReadOnly";

        /// <summary>
        /// 是否允许该用户管理此分店
        /// </summary>
        public bool IsPrimary { get; set; } = false;
    }

    /// <summary>
    /// 用户统计DTO
    /// </summary>
    public class UserStatisticsDto
    {
        public int TotalUsers { get; set; }
        public int ActiveUsers { get; set; }
        public int InactiveUsers { get; set; }
        public int RecentLoginUsers { get; set; }
        public int UsersWithoutRoles { get; set; }
        public int UsersWithoutStores { get; set; }
        public Dictionary<string, int> UsersByRole { get; set; } = new();
        public Dictionary<string, int> UsersByStore { get; set; } = new();
    }
}
