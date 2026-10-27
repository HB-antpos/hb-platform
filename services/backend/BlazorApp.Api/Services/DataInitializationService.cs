using BlazorApp.Api.Data;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Services
{
    public interface IDataInitializationService
    {
        Task InitializeRolesAsync();
        Task AssignUserRolesAsync();
        Task<bool> CheckAndInitializeDataAsync();
    }

    public class DataInitializationService : IDataInitializationService
    {
        private readonly SqlSugarContext _dbContext;

        public DataInitializationService(SqlSugarContext dbContext)
        {
            _dbContext = dbContext;
        }

        /// <summary>
        /// 检查并初始化数据
        /// </summary>
        public async Task<bool> CheckAndInitializeDataAsync()
        {
            try
            {
                Console.WriteLine("🔍 检查数据库数据状态...");

                // 检查角色数据
                var rolesCount = await _dbContext.Db.Queryable<Role>().CountAsync();
                Console.WriteLine($"角色表数据量: {rolesCount}");

                // 检查用户角色关联数据
                var userRolesCount = await _dbContext.Db.Queryable<UserRole>().CountAsync();
                Console.WriteLine($"用户角色关联表数据量: {userRolesCount}");

                // 如果数据不足，进行初始化
                if (rolesCount == 0)
                {
                    Console.WriteLine("📝 初始化角色数据...");
                    await InitializeRolesAsync();
                }

                if (userRolesCount == 0)
                {
                    Console.WriteLine("📝 初始化用户角色关联...");
                    await AssignUserRolesAsync();
                }

                Console.WriteLine("✅ 数据检查完成");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ 数据初始化失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 初始化角色数据
        /// </summary>
        public async Task InitializeRolesAsync()
        {
            try
            {
                // 检查是否已存在角色
                var existingRoles = await _dbContext.Db.Queryable<Role>().ToListAsync();
                if (existingRoles.Any())
                {
                    Console.WriteLine("角色数据已存在，跳过初始化");
                    return;
                }

                // 创建基础角色
                var roles = new List<Role>
                {
                    new Role
                    {
                        RoleGUID = Guid.NewGuid().ToString(),
                        RoleName = "Admin",
                        Description = "系统管理员 - 拥有所有权限",
                        IsActive = true,
                        Level = BlazorApp.Shared.Constants.Permissions.RoleLevels.SuperAdmin,
                        CreatedAt = DateTime.UtcNow,
                    },
                    new Role
                    {
                        RoleGUID = Guid.NewGuid().ToString(),
                        RoleName = "Manager",
                        Description = "店铺管理员 - 管理店铺数据",
                        IsActive = true,
                        Level = BlazorApp.Shared.Constants.Permissions.RoleLevels.Manager,
                        CreatedAt = DateTime.UtcNow,
                    },
                    new Role
                    {
                        RoleGUID = Guid.NewGuid().ToString(),
                        RoleName = "User",
                        Description = "普通用户 - 查看权限",
                        IsActive = true,
                        Level = BlazorApp.Shared.Constants.Permissions.RoleLevels.User,
                        CreatedAt = DateTime.UtcNow,
                    },
                };

                // 批量插入角色
                await _dbContext.Db.Insertable(roles).ExecuteCommandAsync();
                Console.WriteLine($"✅ 成功创建 {roles.Count} 个角色");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ 初始化角色失败: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// 分配用户角色
        /// </summary>
        public async Task AssignUserRolesAsync()
        {
            try
            {
                // 获取所有用户
                var users = await _dbContext.Db.Queryable<User>().ToListAsync();
                if (!users.Any())
                {
                    Console.WriteLine("没有找到用户，跳过角色分配");
                    return;
                }

                // 获取角色
                var roles = await _dbContext.Db.Queryable<Role>().ToListAsync();
                if (!roles.Any())
                {
                    Console.WriteLine("没有找到角色，请先初始化角色数据");
                    return;
                }

                var adminRole = roles.FirstOrDefault(r => r.RoleName == "Admin");
                var managerRole = roles.FirstOrDefault(r => r.RoleName == "Manager");
                var userRole = roles.FirstOrDefault(r => r.RoleName == "User");

                var userRoles = new List<UserRole>();

                foreach (var user in users)
                {
                    // 检查用户是否已有角色分配
                    var existingUserRole = await _dbContext
                        .Db.Queryable<UserRole>()
                        .FirstAsync(ur => ur.UserGUID == user.UserGUID);

                    if (existingUserRole != null)
                    {
                        Console.WriteLine($"用户 {user.Username} 已有角色分配，跳过");
                        continue;
                    }

                    // 根据用户名分配角色
                    Role? assignedRole = null;
                    if (user.Username.Equals("admin", StringComparison.OrdinalIgnoreCase))
                    {
                        assignedRole = adminRole;
                    }
                    else if (user.Username.Contains("manager", StringComparison.OrdinalIgnoreCase))
                    {
                        assignedRole = managerRole;
                    }
                    else
                    {
                        assignedRole = userRole;
                    }

                    if (assignedRole != null)
                    {
                        var userRoleEntity = new UserRole
                        {
                            UserRoleGUID = Guid.NewGuid().ToString(),
                            UserGUID = user.UserGUID,
                            RoleGUID = assignedRole.RoleGUID,
                            AssignedAt = DateTime.UtcNow,
                            CreatedAt = DateTime.UtcNow,
                        };

                        userRoles.Add(userRoleEntity);
                        Console.WriteLine(
                            $"为用户 {user.Username} 分配角色 {assignedRole.RoleName}"
                        );
                    }
                }

                // 批量插入用户角色关联
                if (userRoles.Any())
                {
                    await _dbContext.Db.Insertable(userRoles).ExecuteCommandAsync();
                    Console.WriteLine($"✅ 成功分配 {userRoles.Count} 个用户角色");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ 分配用户角色失败: {ex.Message}");
                throw;
            }
        }
    }
}
