using SqlSugar;

namespace BlazorApp.Shared.Models
{
    /// <summary>
    /// 角色实体类，表示系统中的角色信息
    /// </summary>
    public class Role : BaseEntity
    {
        /// <summary>
        /// 角色全局唯一标识符
        /// </summary>
        [SugarColumn(IsPrimaryKey = true, IsNullable = false)]
        public string RoleGUID { get; set; } = Guid.NewGuid().ToString();

        /// <summary>
        /// 角色名称
        /// </summary>
        [SugarColumn(IsNullable = false, Length = 50)]
        public string RoleName { get; set; } = string.Empty;

        /// <summary>
        /// 角色描述
        /// </summary>
        [SugarColumn(IsNullable = true, Length = 200)]
        public string? Description { get; set; }

        /// <summary>
        /// 是否激活状态
        /// </summary>
        [SugarColumn(IsNullable = false)]
        public bool IsActive { get; set; } = true;

        /// <summary>
        /// 角色等级，数值越大等级越高；账号等级取其所有有效角色的最高值。
        /// 等级低的账号不能修改等级不低于自己的账号，也不能授予不低于自己等级的角色。
        /// </summary>
        [SugarColumn(IsNullable = false)]
        public int Level { get; set; }

        /// <summary>
        /// 角色关联的用户列表（多对多导航属性）
        /// </summary>
        [Navigate(typeof(UserRole), nameof(UserRole.RoleGUID), nameof(UserRole.UserGUID))]
        public List<User>? Users { get; set; }
    }
}