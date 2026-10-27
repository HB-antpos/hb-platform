using System.ComponentModel.DataAnnotations;

namespace BlazorApp.Shared.DTOs
{
    /// <summary>
    /// 角色查询参数DTO
    /// </summary>
    public class RoleQueryDto
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
        /// 搜索关键字（角色名、描述）
        /// </summary>
        public string? SearchKeyword { get; set; }

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
    /// 角色基础信息DTO
    /// </summary>
    public class RoleDto
    {
        public string RoleGUID { get; set; } = string.Empty;
        public string RoleName { get; set; } = string.Empty;
        public string? Description { get; set; }
        public bool IsActive { get; set; }
        /// <summary>角色等级，数值越大等级越高。</summary>
        public int Level { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public int UserCount { get; set; }
    }

    /// <summary>
    /// 角色详细信息DTO
    /// </summary>
    public class RoleDetailDto
    {
        public string RoleGUID { get; set; } = string.Empty;
        public string RoleName { get; set; } = string.Empty;
        public string? Description { get; set; }
        public bool IsActive { get; set; }
        /// <summary>角色等级，数值越大等级越高。</summary>
        public int Level { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public int UserCount { get; set; }
        public List<RoleUserDto> Users { get; set; } = new();
        public List<string> Permissions { get; set; } = new();
    }

    /// <summary>
    /// 创建角色DTO
    /// </summary>
    public class CreateRoleDto
    {
        [Required(ErrorMessage = "角色名不能为空")]
        [StringLength(50, MinimumLength = 2, ErrorMessage = "角色名长度必须在2-50个字符之间")]
        public string RoleName { get; set; } = string.Empty;

        [StringLength(200, ErrorMessage = "描述长度不能超过200个字符")]
        public string? Description { get; set; }

        public bool IsActive { get; set; } = true;

        /// <summary>
        /// 角色等级；不得高于操作者自身等级减一（Admin 除外），默认 0。
        /// </summary>
        [Range(0, 100, ErrorMessage = "角色等级必须在0-100之间")]
        public int Level { get; set; }

        /// <summary>
        /// 权限列表
        /// </summary>
        public List<string> Permissions { get; set; } = new();
    }

    /// <summary>
    /// 更新角色DTO
    /// </summary>
    public class UpdateRoleDto
    {
        [Required(ErrorMessage = "角色名不能为空")]
        [StringLength(50, MinimumLength = 2, ErrorMessage = "角色名长度必须在2-50个字符之间")]
        public string RoleName { get; set; } = string.Empty;

        [StringLength(200, ErrorMessage = "描述长度不能超过200个字符")]
        public string? Description { get; set; }

        public bool IsActive { get; set; } = true;

        /// <summary>
        /// 角色等级；为空表示保持原值（兼容不传该字段的旧客户端）。
        /// </summary>
        [Range(0, 100, ErrorMessage = "角色等级必须在0-100之间")]
        public int? Level { get; set; }
    }

    /// <summary>
    /// 更新角色状态DTO
    /// </summary>
    public class UpdateRoleStatusDto
    {
        public bool IsActive { get; set; }
    }

    /// <summary>
    /// 角色用户信息DTO
    /// </summary>
    public class RoleUserDto
    {
        public string UserGUID { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string? FullName { get; set; }
        public string? GivenName { get; set; }
        public string? MiddleName { get; set; }
        public string? FamilyName { get; set; }
        public string? Nickname { get; set; }
        public bool IsActive { get; set; }
        public DateTime AssignedAt { get; set; }
    }

    /// <summary>
    /// 权限直接授权用户的增量调整DTO
    /// 采用增量而非整表替换：用户规模远大于角色，客户端可能只加载了部分用户，
    /// 增量写入可避免误删未加载用户的授权，也不会覆盖其他管理员的并发修改。
    /// </summary>
    public class PermissionUserAssignmentDto
    {
        public List<string> AddUserGuids { get; set; } = new();
        public List<string> RemoveUserGuids { get; set; } = new();
    }

    /// <summary>
    /// 角色权限分配DTO
    /// </summary>
    public class RolePermissionAssignmentDto
    {
        [Required(ErrorMessage = "权限列表不能为空")]
        public List<string> Permissions { get; set; } = new();
    }

    /// <summary>
    /// 批量角色操作DTO
    /// </summary>
    public class BatchRoleOperationDto
    {
        [Required(ErrorMessage = "角色GUID列表不能为空")]
        public List<string> RoleGuids { get; set; } = new();

        [Required(ErrorMessage = "操作类型不能为空")]
        public string Operation { get; set; } = string.Empty; // activate, deactivate, delete

        /// <summary>
        /// 操作相关的数据
        /// </summary>
        public Dictionary<string, object>? OperationData { get; set; }
    }

    /// <summary>
    /// 角色统计DTO
    /// </summary>
    public class RoleStatisticsDto
    {
        public int TotalRoles { get; set; }
        public int ActiveRoles { get; set; }
        public int InactiveRoles { get; set; }
        public Dictionary<string, int> UsersByRole { get; set; } = new();
        public List<RoleUsageDto> RoleUsage { get; set; } = new();
    }

    /// <summary>
    /// 角色使用情况DTO
    /// </summary>
    public class RoleUsageDto
    {
        public string RoleGUID { get; set; } = string.Empty;
        public string RoleName { get; set; } = string.Empty;
        public int UserCount { get; set; }
        public DateTime LastAssigned { get; set; }
        public bool IsSystemRole { get; set; }
    }

    /// <summary>
    /// 权限定义DTO
    /// </summary>
    public class PermissionDto
    {
        public string Name { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string Category { get; set; } = string.Empty;
        public bool IsSystemPermission { get; set; }

        /// <summary>
        /// 使用端：pos（收银端）/ web（后台）/ mobile（移动端），可同时包含多个。
        /// 由 PermissionSurfaceCatalog 按权限码登记表解析，不落库。
        /// </summary>
        public List<string> Surfaces { get; set; } = new();

        // 审计字段
        public DateTime CreatedAt { get; set; }
        public string? CreatedBy { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public string? UpdatedBy { get; set; }
    }

    /// <summary>
    /// 权限分类DTO
    /// </summary>
    public class PermissionCategoryDto
    {
        public string Category { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string? Description { get; set; }
        public List<PermissionDto> Permissions { get; set; } = new();
    }

    public class PermissionAliasDto
    {
        public string CanonicalCode { get; set; } = string.Empty;
        public List<string> AliasCodes { get; set; } = new();
    }

    public class RolePermissionTemplateDto
    {
        public string RoleName { get; set; } = string.Empty;
        public List<string> PermissionCodes { get; set; } = new();
    }

    public class PermissionCatalogDto
    {
        public List<PermissionCategoryDto> Categories { get; set; } = new();
        public List<PermissionAliasDto> PermissionAliases { get; set; } = new();
        public List<RolePermissionTemplateDto> RoleTemplates { get; set; } = new();
        public List<string> SuperAdminRoleNames { get; set; } = new();
    }

    public class RolePermissionStateDto
    {
        public string RoleGuid { get; set; } = string.Empty;
        public string RoleName { get; set; } = string.Empty;
        public bool IsSuperAdmin { get; set; }
        public bool ImplicitAllPermissions { get; set; }
        public List<string> ExplicitPermissionCodes { get; set; } = new();
        public List<string> EffectivePermissionCodes { get; set; } = new();
    }

    public class UserPermissionSnapshotDto
    {
        public string UserGuid { get; set; } = string.Empty;
        public bool IsSuperAdmin { get; set; }
        public List<string> RoleNames { get; set; } = new();
        public List<string> PermissionCodes { get; set; } = new();
        /// <summary>
        /// 用户精确权限代码（未展开别名）。供必须保持精确权限边界的接口使用。
        /// </summary>
        public List<string> ExactPermissionCodes { get; set; } = new();
    }

    /// <summary>
    /// 创建系统权限DTO
    /// </summary>
    public class CreateSysPermissionDto
    {
        /// <summary>
        /// 权限代码 (如 User.Create)
        /// </summary>
        [Required(ErrorMessage = "权限代码不能为空")]
        [StringLength(100, ErrorMessage = "权限代码长度不能超过100个字符")]
        public string Code { get; set; } = string.Empty;

        /// <summary>
        /// 权限名称 (显示用)
        /// </summary>
        [Required(ErrorMessage = "权限名称不能为空")]
        [StringLength(100, ErrorMessage = "权限名称长度不能超过100个字符")]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// 权限分类
        /// </summary>
        [Required(ErrorMessage = "权限分类不能为空")]
        [StringLength(50, ErrorMessage = "权限分类长度不能超过50个字符")]
        public string Category { get; set; } = string.Empty;

        /// <summary>
        /// 权限描述
        /// </summary>
        [StringLength(200, ErrorMessage = "描述长度不能超过200个字符")]
        public string? Description { get; set; }

        /// <summary>
        /// 自动生成的操作列表 (Create, Delete, Edit, View)
        /// </summary>
        public List<string>? Actions { get; set; }
    }
}
