using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using BlazorApp.Api.Data;
using BlazorApp.Api.Utils;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.Tokens;
using SqlSugar;

namespace BlazorApp.Api.Services
{
    public interface IAuthService
    {
        Task<LoginResponse> LoginAsync(LoginRequest request);
        Task<User?> RegisterAsync(User user);
        string GenerateJwtToken(User user);
        string GenerateJwtToken(User user, List<string>? permissions);
        Task<TokenResponse> GenerateTokensAsync(User user, string ipAddress, string userAgent);
        Task<BrowserExtensionTokenResponse?> IssueBrowserExtensionAccessTokenAsync(
            string userGuid,
            string parentSessionId,
            DateTime issuedAtUtc
        );
        Task<TokenResponse?> RefreshTokensAsync(
            string accessToken,
            string refreshToken,
            string ipAddress,
            string userAgent
        );
        Task<TokenResponse?> RefreshTokensAsync(HttpContext context, string ipAddress, string userAgent);
        Task<bool> RevokeRefreshTokenAsync(string refreshToken);
        Task<bool> ChangePasswordAsync(string userGuid, ChangePasswordDto dto, string? currentSessionId = null);
    }

    public class AuthService : IAuthService
    {
        private readonly SqlSugarContext _dbContext;
        private readonly IConfiguration _configuration;
        private readonly IHttpContextAccessor _httpContextAccessor;

        public AuthService(
            SqlSugarContext dbContext,
            IConfiguration configuration,
            IHttpContextAccessor httpContextAccessor
        )
        {
            _dbContext = dbContext;
            _configuration = configuration;
            _httpContextAccessor = httpContextAccessor;
        }

        /// <summary>
        /// 用户登录认证方法
        /// 🔐 验证用户凭据并生成JWT令牌
        /// </summary>
        /// <param name="request">登录请求对象，包含用户名和密码</param>
        /// <returns>登录响应，包含认证结果和JWT令牌</returns>
        public async Task<LoginResponse> LoginAsync(LoginRequest request)
        {
            try
            {
                // 🔍 从数据库查询用户信息（用户名大小写不敏感）
                var usernameLower = (request.Username ?? string.Empty).Trim().ToLower();
                var list = await _dbContext
                    .Db.Queryable<User>()
                    .Includes(u => u.Roles)
                    .Where(u =>
                        u.Username.ToLower() == usernameLower
                        && u.IsActive
                        && !u.IsDeleted
                    )
                    .Take(1)
                    .ToListAsync();
                var user = list.FirstOrDefault();

                // ❌ 用户不存在或密码验证失败；旧客户端未传格式时按 clientSha256 兼容处理。
                if (user == null || !VerifyPassword(request.Password, user.PasswordHash, request.PasswordFormat, out var needsPasswordRehash))
                {
                    return new LoginResponse { Success = false, Message = "用户名或密码错误" };
                }

                // 🔍 调试：检查用户角色信息
                Console.WriteLine($"用户 {user.Username} 登录成功");
                Console.WriteLine($"用户GUID: {user.UserGUID}");
                Console.WriteLine($"角色数量: {user.Roles?.Count ?? 0}");

                if (user.Roles != null && user.Roles.Any())
                {
                    foreach (var role in user.Roles)
                    {
                        Console.WriteLine($"角色: {role.RoleName} (GUID: {role.RoleGUID})");
                    }
                }
                else
                {
                    Console.WriteLine("⚠️ 用户没有分配角色，将使用默认角色策略");
                }

                // ⏰ 更新用户最后登录时间
                // 记录用户活动，可用于安全审计和会话管理
                user.LastLoginAt = DateTime.UtcNow;
                user.UpdatedAt = DateTime.UtcNow;
                if (needsPasswordRehash)
                {
                    // 登录时拿到了原始密码，旧 SHA256 哈希可安全迁移为 PBKDF2。
                    user.PasswordHash = HashPassword(request.Password);
                }
                await _dbContext
                    .Db.Updateable(user)
                    .UpdateColumns(u => new { u.LastLoginAt, u.UpdatedAt, u.PasswordHash }) // 只更新指定字段，提高性能
                    .ExecuteCommandAsync();

                var permissions = await GetEffectivePermissionCodesAsync(user);

                var token = GenerateJwtToken(user, permissions);

                // ✅ 返回登录成功响应
                return new LoginResponse
                {
                    Success = true,
                    Token = token, // JWT令牌
                    Message = "登录成功",
                    User = new LoginUserDto // 用户基本信息（不包含敏感数据）
                    {
                        UserGUID = user.UserGUID.ToString(),
                        Username = user.Username,
                        Email = user.Email,
                        FullName = user.FullName,
                    },
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"登录过程中出现错误: {ex.Message}");
                Console.WriteLine($"错误详情: {ex.StackTrace}");

                return new LoginResponse
                {
                    Success = false,
                    Message = "登录过程中出现错误，请稍后重试",
                };
            }
        }

        /// <summary>
        /// 用户注册方法
        /// 📝 创建新用户账号，包含重复检查和密码加密
        /// </summary>
        /// <param name="user">用户注册信息</param>
        /// <returns>注册成功返回用户对象，失败返回null</returns>
        public async Task<User?> RegisterAsync(User user)
        {
            // 🔍 检查用户名和邮箱是否已存在
            // 防止重复注册，确保用户名和邮箱的唯一性
            var existingUser = await _dbContext
                .Db.Queryable<User>()
                .FirstAsync(u =>
                    (u.Username == user.Username || u.Email == user.Email) && !u.IsDeleted
                );
            // 查询条件：
            // - 用户名或邮箱已存在
            // - 排除已删除的用户

            // ❌ 用户已存在，注册失败
            if (existingUser != null)
            {
                return null;
            }

            // 🆔 生成用户唯一标识符
            user.UserGUID = Guid.NewGuid().ToString();

            // 🔐 对密码进行哈希加密
            // 使用统一的密码加密工具类，确保密码安全存储
            // 注意：传入的user.PasswordHash包含原始密码，需要重新哈希
            var originalPassword = user.PasswordHash;
            user.PasswordHash = HashPassword(originalPassword);

            // ⏰ 设置创建时间
            user.CreatedAt = DateTime.UtcNow;

            // 💾 将用户信息保存到数据库
            await _dbContext.Db.Insertable(user).ExecuteCommandAsync();

            // ✅ 返回创建成功的用户对象
            return user;
        }

        /// <summary>
        /// 生成JWT访问令牌
        /// 🔐 这是认证系统的核心方法，负责创建包含用户身份和权限信息的JWT令牌
        /// </summary>
        /// <param name="user">用户对象，包含用户信息和角色</param>
        /// <returns>JWT令牌字符串</returns>
        public string GenerateJwtToken(User user)
        {
            return GenerateJwtToken(user, null);
        }

        public string GenerateJwtToken(User user, List<string>? permissions)
        {
            // 📋 从配置文件读取JWT设置（密钥、签发者、受众等）
            var jwtSettings = _configuration.GetSection("Jwt").Get<Models.JwtSettings>();

            // 🔑 创建对称安全密钥，用于JWT签名和验证
            // 使用HMAC-SHA256算法进行签名，确保令牌的完整性和真实性
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSettings!.Key));
            var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            // 🏷️ 构建JWT声明（Claims）列表
            // 声明是JWT中存储用户信息的基本单位，每个声明都是一个键值对
            var claims = new List<Claim>
            {
                // 📝 标准JWT声明（JWT规范定义的声明）
                new Claim(JwtRegisteredClaimNames.Sub, user.UserGUID.ToString()), // 主题：用户唯一标识符
                new Claim(JwtRegisteredClaimNames.UniqueName, user.Username), // 唯一名称：用户名
                new Claim(JwtRegisteredClaimNames.Email, user.Email), // 邮箱：用户邮箱地址
                // 🏷️ 标准.NET声明（ASP.NET Core识别的声明）
                new Claim(ClaimTypes.Name, user.Username), // 用户名：用于HttpContext.User.Identity.Name
                new Claim(ClaimTypes.NameIdentifier, user.UserGUID.ToString()),
                // 🔧 自定义声明（项目特定的声明）
                new Claim("uid", user.UserGUID.ToString()), // 用户GUID：用于前端识别
                new Claim("userId", user.UserGUID.ToString()), // 用户GUID：统一标识符
                new Claim("fullName", user.FullName ?? user.Username), // 用户全名：用于显示
            };

            // 👥 添加用户角色声明 - 这是授权系统的关键部分
            // 角色声明决定了用户能访问哪些API端点和功能
            if (user.Roles != null && user.Roles.Any())
            {
                // 🎭 如果用户有明确的角色分配，添加所有角色到声明中
                foreach (var role in user.Roles)
                {
                    // 每个角色都会创建一个ClaimTypes.Role类型的声明
                    // 这允许使用[Authorize(Roles="Admin,Manager")]进行角色授权
                    claims.Add(new Claim(ClaimTypes.Role, role.RoleName));
                }
            }
            else
            {
                // 🎯 默认角色分配策略：根据用户名分配默认角色
                if (user.Username.Equals("admin", StringComparison.OrdinalIgnoreCase))
                {
                    // 👑 admin用户默认获得Admin角色（最高权限）
                    // Admin角色可以执行所有操作：创建、修改、删除、同步等
                    claims.Add(new Claim(ClaimTypes.Role, "Admin"));
                }
                else
                {
                    // 👤 其他用户默认获得Manager角色（只读权限）
                    // Manager角色只能查看数据，不能修改系统配置
                    claims.Add(new Claim(ClaimTypes.Role, "Manager"));
                }
            }

            if (permissions != null && permissions.Any())
            {
                foreach (var perm in Permissions.ExpandPermissionCodes(permissions))
                {
                    claims.Add(new Claim("permission", perm));
                }
            }

            // 🏗️ 创建JWT安全令牌对象
            var token = new JwtSecurityToken(
                issuer: jwtSettings.Issuer, // 签发者：标识谁创建了这个令牌
                audience: jwtSettings.Audience, // 受众：标识令牌的目标接收者
                claims: claims, // 声明：包含用户身份和权限信息
                expires: DateTime.UtcNow.AddMinutes(jwtSettings.ExpireMinutes), // 过期时间：令牌的有效期
                signingCredentials: credentials // 签名凭据：用于验证令牌的真实性
            );

            // 📄 将JWT令牌对象序列化为字符串格式
            // 这个字符串将发送给客户端，客户端在后续请求中需要携带此令牌
            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        /// <summary>
        /// 密码哈希加密方法
        /// 🔐 使用统一的密码加密工具类
        /// </summary>
        /// <param name="password">原始密码</param>
        /// <returns>Base64编码的哈希值</returns>
        private static string HashPassword(string password)
        {
            return PasswordHasher.HashPassword(password);
        }

        /// <summary>
        /// 密码验证方法
        /// 🔍 验证用户输入的密码是否与存储的哈希值匹配
        /// </summary>
        /// <param name="password">用户输入的原始密码</param>
        /// <param name="storedHash">数据库中存储的密码哈希值</param>
        /// <param name="passwordFormat">客户端提交密码的格式</param>
        /// <param name="needsRehash">旧哈希使用原始密码验证成功时需要迁移</param>
        /// <returns>true表示密码正确，false表示密码错误</returns>
        private static bool VerifyPassword(
            string password,
            string storedHash,
            string? passwordFormat,
            out bool needsRehash
        )
        {
            // 🔐 使用统一的密码验证工具类，并让登录流程负责旧哈希渐进迁移。
            return PasswordHasher.VerifyPassword(password, storedHash, passwordFormat, out needsRehash);
        }

        /// <summary>
        /// 生成令牌对（访问令牌 + 刷新令牌）
        /// 🔑 实现双令牌机制，提高安全性和用户体验
        /// </summary>
        /// <param name="user">用户对象</param>
        /// <param name="ipAddress">客户端IP地址，用于安全审计</param>
        /// <param name="userAgent">客户端用户代理，用于安全审计</param>
        /// <returns>包含访问令牌和刷新令牌的响应对象</returns>
        public async Task<TokenResponse> GenerateTokensAsync(
            User user,
            string ipAddress,
            string userAgent
        )
        {
            var jwtSettings = _configuration.GetSection("Jwt").Get<Models.JwtSettings>();

            // 🔄 生成刷新令牌（长令牌，7天有效）
            // 刷新令牌用于获取新的访问令牌，有效期长以改善用户体验
            var refreshToken = GenerateRefreshToken();
            var refreshTokenGuid = Guid.NewGuid().ToString();
            var now = DateTime.UtcNow;

            await RevokeOtherPublicIpSessionsAsync(user.UserGUID, ipAddress, now);

            // 💾 保存刷新令牌到数据库
            // 存储刷新令牌便于撤销和审计
            var refreshTokenEntity = new RefreshToken
            {
                RefreshTokenGUID = refreshTokenGuid, // 刷新令牌唯一标识，也是 access token 的 sessionId
                UserGUID = user.UserGUID, // 关联用户
                Token = refreshToken, // 刷新令牌值
                ExpiresAt = now.AddDays(7), // 过期时间（7天）
                IsRevoked = false, // 是否已撤销
                IpAddress = ipAddress, // 客户端IP（安全审计）
                UserAgent = userAgent, // 客户端信息（安全审计）
            };

            // 📊 将刷新令牌实体保存到数据库
            await _dbContext.Db.Insertable(refreshTokenEntity).ExecuteCommandAsync();

            // access JWT 只保留身份、角色和会话声明；权限由授权链路实时查库，避免大权限账号的 Cookie 超过 4KB 限制。
            var accessToken = GenerateAccessToken(user, jwtSettings!, refreshTokenGuid);

            // 📤 返回令牌响应对象
            return new TokenResponse
            {
                AccessToken = accessToken, // 访问令牌
                RefreshToken = refreshToken, // 刷新令牌
                AccessTokenExpiry = now.AddMinutes(30), // 访问令牌过期时间
                RefreshTokenExpiry = now.AddDays(7), // 刷新令牌过期时间
                Success = true, // 操作成功标志
            };
        }

        /// <summary>
        /// 为已通过一次性 PKCE grant 验证的网站父会话签发最长五分钟的扩展访问令牌。
        /// 此路径不创建或返回刷新令牌，也不会旋转现有网站会话。
        /// </summary>
        public async Task<BrowserExtensionTokenResponse?> IssueBrowserExtensionAccessTokenAsync(
            string userGuid,
            string parentSessionId,
            DateTime issuedAtUtc
        )
        {
            var now = issuedAtUtc.Kind == DateTimeKind.Utc
                ? issuedAtUtc
                : issuedAtUtc.ToUniversalTime();
            var parentSessionActive = await _dbContext.Db.Queryable<RefreshToken>()
                .AnyAsync(token =>
                    token.RefreshTokenGUID == parentSessionId
                    && token.UserGUID == userGuid
                    && !token.IsRevoked
                    && !token.IsDeleted
                    && token.ExpiresAt >= now
                );
            if (!parentSessionActive)
            {
                return null;
            }

            var user = await _dbContext.Db.Queryable<User>()
                .Includes(item => item.Roles)
                .FirstAsync(item =>
                    item.UserGUID == userGuid
                    && item.IsActive
                    && !item.IsDeleted
                );
            if (user == null)
            {
                return null;
            }

            var jwtSettings = _configuration.GetSection("Jwt").Get<Models.JwtSettings>();
            if (jwtSettings == null)
            {
                return null;
            }

            var expiresAtUtc = now.AddMinutes(5);
            return new BrowserExtensionTokenResponse
            {
                AccessToken = GenerateAccessToken(
                    user,
                    jwtSettings,
                    parentSessionId,
                    expiresAtUtc,
                    "browser_extension"
                ),
                AccessTokenExpiry = expiresAtUtc,
                UserGuid = user.UserGUID,
                Username = user.Username,
                FullName = user.FullName ?? user.Username,
            };
        }

        /// <summary>
        /// 刷新令牌方法
        /// 🔄 使用过期的访问令牌和有效的刷新令牌获取新的令牌对
        /// 实现无感知的令牌刷新，提升用户体验
        /// </summary>
        /// <param name="accessToken">过期的访问令牌</param>
        /// <param name="refreshToken">有效的刷新令牌</param>
        /// <param name="ipAddress">客户端IP地址</param>
        /// <param name="userAgent">客户端用户代理</param>
        /// <returns>新的令牌对，验证失败返回null</returns>
        public async Task<TokenResponse?> RefreshTokensAsync(
            string accessToken,
            string refreshToken,
            string ipAddress,
            string userAgent
        )
        {
            string? userGuidString = null;

            // 🔍 尝试从访问令牌中提取用户信息（支持多种场景）
            if (!string.IsNullOrEmpty(accessToken))
            {
                var principal = GetPrincipalFromExpiredToken(accessToken);
                if (principal != null)
                {
                    userGuidString = principal.FindFirst("userGuid")?.Value;
                }
            }

            // 🔍 验证刷新令牌是否有效（统一查询）
            // 检查刷新令牌是否存在于数据库且未被撤销
            var storedRefreshToken = await _dbContext
                .Db.Queryable<RefreshToken>()
                .FirstAsync(rt => rt.Token == refreshToken && !rt.IsRevoked && !rt.IsDeleted);

            // 🔄 如果无法从AccessToken获取用户信息，从RefreshToken获取
            if (string.IsNullOrEmpty(userGuidString))
            {
                if (storedRefreshToken != null && storedRefreshToken.ExpiresAt >= DateTime.UtcNow)
                {
                    userGuidString = storedRefreshToken.UserGUID;
                }
                else
                {
                    await TryRevokeRefreshTokenAsync(storedRefreshToken);
                    return null; // RefreshToken无效或已过期
                }
            }

            // 📋 验证用户GUID有效性
            if (
                string.IsNullOrEmpty(userGuidString)
                || !Guid.TryParse(userGuidString, out var userGuid)
            )
            {
                await TryRevokeRefreshTokenAsync(storedRefreshToken);
                return null; // 用户标识无效
            }

            // 🔍 验证RefreshToken与用户GUID的匹配性，以及过期时间
            if (
                storedRefreshToken == null
                || storedRefreshToken.UserGUID != userGuidString
                || storedRefreshToken.ExpiresAt < DateTime.UtcNow
            )
            {
                await TryRevokeRefreshTokenAsync(storedRefreshToken);
                return null; // RefreshToken无效、不匹配或已过期
            }

            // 🗑️ 以数据库 CAS 原子消费旧刷新令牌；并发刷新只有一个请求可以继续签发新会话。
            var revokedAt = DateTime.UtcNow;
            var revokedRows = await _dbContext.Db.Updateable<RefreshToken>()
                .SetColumns(token => token.IsRevoked == true)
                .SetColumns(token => token.UpdatedAt == revokedAt)
                .Where(token =>
                    token.RefreshTokenGUID == storedRefreshToken.RefreshTokenGUID
                    && token.Token == refreshToken
                    && token.UserGUID == userGuidString
                    && !token.IsRevoked
                    && !token.IsDeleted
                    && token.ExpiresAt >= revokedAt
                )
                .ExecuteCommandAsync();
            if (revokedRows != 1)
            {
                return null;
            }

            // 👤 获取用户完整信息（包括角色）
            var user = await _dbContext
                .Db.Queryable<User>()
                .Includes(u => u.Roles) // 📋 预加载用户角色信息，确保刷新token时也有角色声明
                .FirstAsync(u => u.UserGUID == userGuidString && u.IsActive && !u.IsDeleted);
            if (user == null)
            {
                return null; // 用户不存在或已被删除
            }

            // 🔄 生成新的令牌对
            return await GenerateTokensAsync(user, ipAddress, userAgent);
        }

        /// <summary>
        /// 从 HttpContext 刷新令牌方法（Cookie 认证方案）
        /// 🔄 从 Cookie 中读取令牌并刷新，支持 Cookie 认证
        /// </summary>
        /// <param name="context">HTTP 上下文，用于读取 Cookie</param>
        /// <param name="ipAddress">客户端IP地址</param>
        /// <param name="userAgent">客户端用户代理</param>
        /// <returns>新的令牌对，验证失败返回null</returns>
        public async Task<TokenResponse?> RefreshTokensAsync(
            HttpContext context,
            string ipAddress,
            string userAgent
        )
        {
            // 🍪 从 Cookie 中读取令牌
            var accessToken = CookieHelper.GetAccessToken(context);
            var refreshToken = CookieHelper.GetRefreshToken(context);

            // ❌ 如果 Cookie 中没有令牌，返回 null
            if (string.IsNullOrEmpty(accessToken) && string.IsNullOrEmpty(refreshToken))
            {
                return null;
            }

            // 🔄 调用原有的刷新令牌方法
            return await RefreshTokensAsync(
                accessToken ?? string.Empty,
                refreshToken ?? string.Empty,
                ipAddress,
                userAgent
            );
        }

        /// <summary>
        /// 撤销刷新令牌方法
        /// 🚫 使指定的刷新令牌失效，用于用户登出或安全审计
        /// </summary>
        /// <param name="refreshToken">要撤销的刷新令牌</param>
        /// <returns>true表示撤销成功，false表示令牌不存在或已撤销</returns>
        public async Task<bool> RevokeRefreshTokenAsync(string refreshToken)
        {
            // 🔍 查找指定的刷新令牌
            var storedRefreshToken = await _dbContext
                .Db.Queryable<RefreshToken>()
                .FirstAsync(rt => rt.Token == refreshToken && !rt.IsRevoked && !rt.IsDeleted);
            // 查询条件：
            // - 令牌值匹配
            // - 令牌未被撤销
            // - 令牌未被删除

            // ❌ 令牌不存在或已撤销
            if (storedRefreshToken == null)
            {
                return false;
            }

            // 🚫 标记令牌为已撤销
            storedRefreshToken.IsRevoked = true;
            storedRefreshToken.UpdatedAt = DateTime.UtcNow;

            // 💾 更新数据库中的令牌状态
            await _dbContext
                .Db.Updateable(storedRefreshToken)
                .UpdateColumns(rt => new { rt.IsRevoked, rt.UpdatedAt })
                .ExecuteCommandAsync();

            // ✅ 撤销成功
            return true;
        }

        private async Task<List<string>> GetEffectivePermissionCodesAsync(User user)
        {
            var roleGuids = user.Roles?
                .Select(r => r.RoleGUID)
                .Where(roleGuid => !string.IsNullOrWhiteSpace(roleGuid))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string>();

            var permissions = new List<string>();
            if (roleGuids.Any())
            {
                var rolePermissions = await _dbContext.Db.Queryable<SysRolePermission>()
                    .Where(srp => roleGuids.Contains(srp.RoleGuid) && !srp.IsDeleted)
                    .Select(srp => srp.PermissionCode)
                    .ToListAsync();
                permissions.AddRange(rolePermissions);
            }

            // 直接授权是权限管理页的真实保存结果之一；legacy LoginAsync/GenerateJwtToken 仍需聚合这份权限快照。
            if (HasUserPermissionTable())
            {
                var directPermissions = await _dbContext.Db.Queryable<SysUserPermission>()
                    .Where(item => item.UserGuid == user.UserGUID && !item.IsDeleted)
                    .Select(item => item.PermissionCode)
                    .ToListAsync();
                permissions.AddRange(directPermissions);
            }

            return permissions
                .Where(permission => !string.IsNullOrWhiteSpace(permission))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private bool HasUserPermissionTable()
        {
            return _dbContext.Db.DbMaintenance.GetTableInfoList(false)
                .Any(table => string.Equals(
                    table.Name,
                    "HBwebSysUserPermissions",
                    StringComparison.OrdinalIgnoreCase
                ));
        }

        /// <summary>
        /// 修改用户密码
        /// </summary>
        public async Task<bool> ChangePasswordAsync(
            string userGuid,
            ChangePasswordDto dto,
            string? currentSessionId = null
        )
        {
            var db = _dbContext.Db;
            var user = await db.Queryable<User>()
                .FirstAsync(u => u.UserGUID == userGuid && !u.IsDeleted);

            if (user == null) return false;

            // 验证旧密码
            if (!VerifyPassword(dto.CurrentPassword, user.PasswordHash, PasswordHasher.PasswordFormatRaw, out _))
            {
                throw new Exception("当前密码错误");
            }
            // 强制改密的目的是换掉店长掌握的密码，新旧相同等于没改。
            if (VerifyPassword(dto.NewPassword, user.PasswordHash, PasswordHasher.PasswordFormatRaw, out _))
            {
                throw new Exception("新密码不能与当前密码相同");
            }

            var now = DateTime.UtcNow;
            user.PasswordHash = HashPassword(dto.NewPassword);
            user.UpdatedAt = now;

            await db.Ado.BeginTranAsync();
            try
            {
                var updated = await db.Updateable(user)
                    .UpdateColumns(u => new { u.PasswordHash, u.UpdatedAt })
                    .ExecuteCommandAsync();
                if (updated <= 0)
                {
                    await db.Ado.RollbackTranAsync();
                    return false;
                }
                // 关键逻辑：改密、清除须改密标记、吊销其他会话同事务完成，避免标记残留或旧会话继续可用。
                await UserPasswordChangeRequirements.ClearAsync(db, userGuid);
                await db.Updateable<RefreshToken>()
                    .SetColumns(token => token.IsRevoked == true)
                    .SetColumns(token => token.UpdatedAt == now)
                    .Where(token => token.UserGUID == userGuid && !token.IsRevoked)
                    .WhereIF(
                        !string.IsNullOrWhiteSpace(currentSessionId),
                        token => token.RefreshTokenGUID != currentSessionId
                    )
                    .ExecuteCommandAsync();
                await db.Ado.CommitTranAsync();
                return true;
            }
            catch
            {
                await db.Ado.RollbackTranAsync();
                throw;
            }
        }

        /// <summary>
        /// 生成访问令牌（短令牌）
        /// 🔑 创建有效期较短的JWT令牌，用于API调用
        /// 与GenerateJwtToken方法类似，但过期时间更短（15分钟）
        /// </summary>
        /// <param name="user">用户对象</param>
        /// <param name="jwtSettings">JWT配置设置</param>
        /// <param name="sessionId">刷新令牌会话 ID，用于 JWT 中绑定当前会话</param>
        /// <param name="expiresAtUtc">可选的显式 UTC 过期时间；默认保持 15 分钟。</param>
        /// <param name="tokenUse">可选令牌用途声明。</param>
        /// <returns>JWT访问令牌字符串</returns>
        private string GenerateAccessToken(
            User user,
            Models.JwtSettings jwtSettings,
            string? sessionId = null,
            DateTime? expiresAtUtc = null,
            string? tokenUse = null
        )
        {
            // 🔑 创建对称安全密钥
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSettings.Key));
            var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            // 🏷️ 构建JWT声明列表
            var claims = new List<Claim>
            {
                new Claim(JwtRegisteredClaimNames.Sub, user.UserGUID.ToString()), // 主题：用户唯一标识
                new Claim(JwtRegisteredClaimNames.UniqueName, user.Username), // 唯一名称：用户名
                new Claim(JwtRegisteredClaimNames.Email, user.Email), // 邮箱：用户邮箱
                new Claim(ClaimTypes.Name, user.Username), // 用户名：用于身份识别
                new Claim(ClaimTypes.NameIdentifier, user.UserGUID.ToString()),
                new Claim("userGuid", user.UserGUID.ToString()), // 用户GUID：用于刷新令牌
                new Claim("userId", user.UserGUID.ToString()), // 用户GUID：统一标识符
                new Claim("fullName", user.FullName ?? user.Username), // 用户全名：用于显示
            };

            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                // sessionId 绑定 RefreshToken 记录，使被挤下线的 access token 下一次请求立即失效。
                claims.Add(new Claim("sessionId", sessionId));
            }

            if (!string.IsNullOrWhiteSpace(tokenUse))
            {
                claims.Add(new Claim("token_use", tokenUse));
            }

            // 👥 添加用户角色声明（与GenerateJwtToken保持一致）
            if (user.Roles != null && user.Roles.Any())
            {
                // 🎭 如果用户有明确的角色分配，添加所有角色到声明中
                foreach (var role in user.Roles)
                {
                    // 每个角色都会创建一个ClaimTypes.Role类型的声明
                    // 这允许使用[Authorize(Roles="Admin,Manager")]进行角色授权
                    claims.Add(new Claim(ClaimTypes.Role, role.RoleName));
                }
            }
            else
            {
                // 🎯 默认角色分配策略：根据用户名分配默认角色
                if (user.Username.Equals("admin", StringComparison.OrdinalIgnoreCase))
                {
                    // 👑 admin用户默认获得Admin角色（最高权限）
                    // Admin角色可以执行所有操作：创建、修改、删除、同步等
                    claims.Add(new Claim(ClaimTypes.Role, "Admin"));
                }
                else
                {
                    // 👤 其他用户默认获得Manager角色（只读权限）
                    // Manager角色只能查看数据，不能修改系统配置
                    claims.Add(new Claim(ClaimTypes.Role, "Manager"));
                }
            }

            // 现有登录与刷新调用不传 expiresAtUtc，继续保持原来的 15 分钟行为。
            var tokenExpiresAtUtc = expiresAtUtc ?? DateTime.UtcNow.AddMinutes(15);

            // 🏗️ 创建JWT安全令牌对象
            var token = new JwtSecurityToken(
                issuer: jwtSettings.Issuer, // 签发者
                audience: jwtSettings.Audience, // 受众
                claims: claims, // 声明列表
                expires: tokenExpiresAtUtc,
                signingCredentials: credentials // 签名凭据
            );

            // 📄 序列化为字符串
            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        private async Task TryRevokeRefreshTokenAsync(RefreshToken? refreshToken)
        {
            if (refreshToken == null || refreshToken.IsRevoked)
            {
                return;
            }

            refreshToken.IsRevoked = true;
            refreshToken.UpdatedAt = DateTime.UtcNow;

            await _dbContext
                .Db.Updateable(refreshToken)
                .UpdateColumns(rt => new { rt.IsRevoked, rt.UpdatedAt })
                .ExecuteCommandAsync();
        }

        private async Task RevokeOtherPublicIpSessionsAsync(
            string userGuid,
            string ipAddress,
            DateTime now
        )
        {
            var newPublicIp = ClientIpResolver.NormalizeKnownPublicIpv4(ipAddress);
            if (string.IsNullOrWhiteSpace(newPublicIp))
            {
                return;
            }

            var sessions = await _dbContext.Db.Queryable<RefreshToken>()
                .Where(token =>
                    token.UserGUID == userGuid
                    && !token.IsRevoked
                    && !token.IsDeleted
                    && token.ExpiresAt >= now
                )
                .ToListAsync();

            var sessionsToRevoke = sessions
                .Where(token =>
                {
                    var existingPublicIp = ClientIpResolver.NormalizeKnownPublicIpv4(token.IpAddress);
                    return !string.IsNullOrWhiteSpace(existingPublicIp)
                        && !string.Equals(existingPublicIp, newPublicIp, StringComparison.OrdinalIgnoreCase);
                })
                .ToList();

            if (sessionsToRevoke.Count == 0)
            {
                return;
            }

            foreach (var session in sessionsToRevoke)
            {
                session.IsRevoked = true;
                session.UpdatedAt = now;
            }

            // 不同公网 IP 的旧会话被新登录挤下线；同公网 IP 会话保留。
            await _dbContext.Db.Updateable(sessionsToRevoke)
                .UpdateColumns(token => new { token.IsRevoked, token.UpdatedAt })
                .ExecuteCommandAsync();
        }

        /// <summary>
        /// 生成刷新令牌
        /// 🔄 创建高安全性的随机刷新令牌
        /// 使用加密安全的随机数生成器，确保令牌的唯一性和不可预测性
        /// </summary>
        /// <returns>Base64编码的刷新令牌字符串</returns>
        private static string GenerateRefreshToken()
        {
            // 🔢 创建32字节的随机数数组
            // 32字节 = 256位，提供足够的安全性
            var randomNumber = new byte[32];

            // 🔧 使用加密安全的随机数生成器
            // 比System.Random更安全，适合生成安全令牌
            using var rng = RandomNumberGenerator.Create();

            // 📝 填充随机字节数组
            rng.GetBytes(randomNumber);

            // 📄 转换为Base64字符串
            // Base64编码便于存储和传输
            return Convert.ToBase64String(randomNumber);
        }

        /// <summary>
        /// 从过期令牌中提取用户主体信息
        /// 🔍 验证过期令牌的签名和结构，提取用户信息用于令牌刷新
        /// ⚠️ 注意：此方法专门处理过期令牌，跳过生命周期验证 (ValidateLifetime = false)
        /// 🔒 仍然验证签名、发行者、受众等安全参数，确保令牌的合法性
        /// </summary>
        /// <param name="token">过期的JWT令牌</param>
        /// <returns>用户主体信息，验证失败返回null</returns>
        private ClaimsPrincipal? GetPrincipalFromExpiredToken(string token)
        {
            // 📋 从配置文件读取JWT设置
            var jwtSettings = _configuration.GetSection("Jwt").Get<Models.JwtSettings>();
            var key = Encoding.UTF8.GetBytes(jwtSettings!.Key);

            // 🔧 配置令牌验证参数（针对过期令牌的特殊配置）
            var tokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = false, // ⚠️ 跳过生命周期验证，因为我们要处理过期令牌
                ValidateIssuerSigningKey = true,
                ValidIssuer = jwtSettings.Issuer,
                ValidAudience = jwtSettings.Audience,
                IssuerSigningKey = new SymmetricSecurityKey(key),
                ClockSkew = TimeSpan.Zero, // 🕒 设置时钟偏移为0，确保精确的时间验证
            };

            // 🔧 创建JWT令牌处理器
            var tokenHandler = new JwtSecurityTokenHandler();
            try
            {
                // 🔍 验证令牌并提取主体信息
                var principal = tokenHandler.ValidateToken(
                    token,
                    tokenValidationParameters,
                    out SecurityToken securityToken
                );
                var jwtSecurityToken = securityToken as JwtSecurityToken;

                // 🔒 验证算法安全性
                // 确保使用安全的HMAC-SHA256算法，防止算法混淆攻击
                if (
                    jwtSecurityToken == null
                    || !jwtSecurityToken.Header.Alg.Equals(
                        SecurityAlgorithms.HmacSha256,
                        StringComparison.InvariantCultureIgnoreCase
                    )
                )
                {
                    return null; // 算法不匹配或令牌格式错误
                }

                // ✅ 返回有效的用户主体信息
                return principal;
            }
            catch
            {
                // 💥 令牌验证失败（格式错误、签名无效等）
                return null;
            }
        }
    }
}
