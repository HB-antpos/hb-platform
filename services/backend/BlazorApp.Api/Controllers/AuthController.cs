using System.Security.Claims;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Services;
using BlazorApp.Api.Utils;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers
{
    /// <summary>
    /// 认证控制器 - 处理用户登录、注册、令牌刷新和登出功能
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    public class AuthController : ControllerBase
    {
        private readonly IAuthService _authService;
        private readonly SqlSugarContext _dbContext;
        private readonly IRoleService _roleService;
        private readonly IUserService _userService;
        private readonly ILogger<AuthController> _logger;
        private readonly IClientIpResolver _clientIpResolver;
        private readonly UserLoginDeviceAuditService? _loginDeviceAuditService;

        /// <summary>
        /// 构造函数 - 依赖注入认证服务、数据库上下文和角色服务
        /// </summary>
        /// <param name="authService">认证服务</param>
        /// <param name="dbContext">数据库上下文</param>
        /// <param name="roleService">角色服务</param>
        /// <param name="userService">用户服务</param>
        /// <param name="logger">日志记录器</param>
        /// <param name="clientIpResolver">客户端公网 IP 解析器</param>
        /// <param name="loginDeviceAuditService">App 登录设备与定位审计服务</param>
        public AuthController(
            IAuthService authService,
            SqlSugarContext dbContext,
            IRoleService roleService,
            IUserService userService,
            ILogger<AuthController> logger,
            IClientIpResolver? clientIpResolver = null,
            UserLoginDeviceAuditService? loginDeviceAuditService = null
        )
        {
            _authService = authService;
            _dbContext = dbContext;
            _roleService = roleService;
            _userService = userService;
            _logger = logger;
            _clientIpResolver = clientIpResolver ?? new ClientIpResolver(new ConfigurationBuilder().Build());
            _loginDeviceAuditService = loginDeviceAuditService;
        }

        /// <summary>
        /// Bearer 用户登录接口
        /// </summary>
        /// <param name="request">登录请求参数，包含用户名和密码</param>
        /// <returns>登录结果，包含访问令牌和刷新令牌</returns>
        /// <remarks>
        /// 登录流程：
        /// 1. 验证请求数据格式
        /// 2. 调用认证服务验证用户名密码
        /// 3. 获取用户IP地址和用户代理信息
        /// 4. 查询用户完整信息（包含角色）
        /// 5. 生成JWT令牌和刷新令牌
        /// 6. 返回令牌响应
        ///
        /// 🔑 JWT令牌使用说明：
        /// - 在Swagger中测试API时，点击"Authorize"按钮
        /// - 输入格式：Bearer + 空格 + 令牌值
        /// - 示例：Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...
        /// - 注意：必须包含"Bearer"前缀和空格，不能使用大括号{}
        /// </remarks>
        [HttpPost("login")]
        [AllowAnonymous] // 🔓 允许匿名访问，登录不需要认证
        public async Task<ApiResponse<TokenResponse>> Login([FromBody] LoginRequest request)
        {
            if (!ModelState.IsValid)
            {
                return ApiResponse<TokenResponse>.Error("请求数据格式无效");
            }

            NormalizeOptionalLoginLocation(request);

            var tokenResponse = await AuthenticateAndIssueTokensAsync(request);
            if (tokenResponse == null)
            {
                return ApiResponse<TokenResponse>.Error("用户名或密码错误");
            }

            return ApiResponse<TokenResponse>.OK(tokenResponse, "登录成功");
        }

        /// <summary>
        /// 浏览器 Cookie 会话登录接口
        /// </summary>
        [HttpPost("session/login")]
        [AllowAnonymous]
        public async Task<ApiResponse<SessionResponse>> SessionLogin([FromBody] LoginRequest request)
        {
            if (!ModelState.IsValid)
            {
                return ApiResponse<SessionResponse>.Error("请求数据格式无效");
            }

            var tokenResponse = await AuthenticateAndIssueTokensAsync(request);
            if (tokenResponse == null)
            {
                return ApiResponse<SessionResponse>.Error("用户名或密码错误");
            }

            CookieHelper.SetTokens(Response, tokenResponse.AccessToken, tokenResponse.RefreshToken);
            return ApiResponse<SessionResponse>.OK(CreateSessionResponse(tokenResponse), "登录成功");
        }

        /// <summary>
        /// Bearer 刷新访问令牌接口
        /// </summary>
        /// <param name="request">刷新令牌请求，包含当前访问令牌和刷新令牌（可选，向后兼容）</param>
        /// <returns>新的访问令牌和刷新令牌</returns>
        /// <remarks>
        /// 令牌刷新流程：
        /// 1. 优先从 Cookie 读取 accessToken 和 refreshToken（如果存在）
        /// 2. 如果 Cookie 中没有令牌，则从请求体参数读取（向后兼容）
        /// 3. 获取客户端IP地址和用户代理信息
        /// 4. 验证当前访问令牌和刷新令牌的有效性
        /// 5. 生成新的访问令牌和刷新令牌
        /// 6. 更新 Cookie 中的令牌
        /// 7. 返回新的令牌对
        ///
        /// 支持两种令牌传递方式：
        /// - Cookie 认证：令牌自动从 Cookie 读取（推荐）
        /// - 请求体认证：在请求体中显式传递 accessToken 和 refreshToken（向后兼容）
        /// </remarks>
        [HttpPost("refresh")]
        [AllowAnonymous] // 🔓 允许匿名访问，令牌刷新不需要认证
        public async Task<ApiResponse<TokenResponse>> RefreshToken(
            [FromBody] RefreshTokenRequest request
        )
        {
            var accessToken = request?.AccessToken ?? string.Empty;
            var refreshToken = request?.RefreshToken ?? string.Empty;
            var ipAddress = _clientIpResolver.Resolve(Request.HttpContext);
            var userAgent = Request.Headers["User-Agent"].ToString();

            var tokenResponseFromBody = await _authService.RefreshTokensAsync(
                accessToken,
                refreshToken,
                ipAddress,
                userAgent
            );

            if (tokenResponseFromBody == null)
            {
                return ApiResponse<TokenResponse>.Error("刷新令牌无效或已过期");
            }

            return ApiResponse<TokenResponse>.OK(tokenResponseFromBody, "令牌刷新成功");
        }

        /// <summary>
        /// 浏览器 Cookie 会话刷新接口
        /// </summary>
        [HttpPost("session/refresh")]
        [AllowAnonymous]
        public async Task<ApiResponse<SessionResponse>> SessionRefresh()
        {
            var ipAddress = _clientIpResolver.Resolve(Request.HttpContext);
            var userAgent = Request.Headers["User-Agent"].ToString();

            var tokenResponse = await _authService.RefreshTokensAsync(
                Request.HttpContext,
                ipAddress,
                userAgent
            );

            if (tokenResponse == null)
            {
                // 并发刷新输家不能写过期 Set-Cookie，否则可能覆盖赢家刚旋转的新会话。
                // 失效 Cookie 本身无法通过认证，登录或显式登出时会再覆盖/清理。
                return ApiResponse<SessionResponse>.Error("刷新令牌无效或已过期");
            }

            CookieHelper.SetTokens(Response, tokenResponse.AccessToken, tokenResponse.RefreshToken);
            return ApiResponse<SessionResponse>.OK(CreateSessionResponse(tokenResponse), "令牌刷新成功");
        }

        /// <summary>
        /// 已登录网站用 Cookie 会话申请浏览器扩展一次性 PKCE 授权码。
        /// </summary>
        [HttpPost("extension/authorize")]
        [Authorize]
        [EnableRateLimiting(BrowserExtensionSessionGrantRateLimits.AuthorizePolicyName)]
        public async Task<ApiResponse<BrowserExtensionAuthorizeResponse>> ExtensionAuthorize(
            [FromBody] BrowserExtensionAuthorizeRequest request,
            [FromServices] BrowserExtensionSessionGrantService grantService
        )
        {
            // 必须明确拒绝 Authorization header，避免扩展 bearer 自己续签新的扩展 bearer。
            if (
                !string.IsNullOrWhiteSpace(Request.Headers.Authorization.FirstOrDefault())
                || string.IsNullOrWhiteSpace(CookieHelper.GetAccessToken(Request.HttpContext))
                || User.Identity?.IsAuthenticated != true
                || User.HasClaim("token_use", "browser_extension")
            )
            {
                return ApiResponse<BrowserExtensionAuthorizeResponse>.Error(
                    "请先在 HB SHOP 网站登录",
                    BrowserExtensionSessionGrantService.CookieSessionRequiredErrorCode
                );
            }

            var userGuid =
                User.FindFirst("userId")?.Value
                ?? User.FindFirst("sub")?.Value
                ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var sessionId = User.FindFirst("sessionId")?.Value;
            if (string.IsNullOrWhiteSpace(userGuid) || string.IsNullOrWhiteSpace(sessionId))
            {
                return ApiResponse<BrowserExtensionAuthorizeResponse>.Error(
                    "网站登录会话无效或已过期",
                    BrowserExtensionSessionGrantService.InvalidGrantErrorCode
                );
            }

            return await grantService.AuthorizeAsync(userGuid, sessionId, request);
        }

        /// <summary>
        /// 浏览器扩展用一次性授权码和 PKCE verifier 兑换短期访问令牌。
        /// </summary>
        [HttpPost("extension/token")]
        [AllowAnonymous]
        [EnableRateLimiting(BrowserExtensionSessionGrantRateLimits.ExchangePolicyName)]
        public Task<ApiResponse<BrowserExtensionTokenResponse>> ExtensionToken(
            [FromBody] BrowserExtensionTokenRequest request,
            [FromServices] BrowserExtensionSessionGrantService grantService
        ) => grantService.ExchangeAsync(request);

        /// <summary>
        /// 获取当前用户信息接口
        /// </summary>
        /// <returns>当前登录用户的详细信息</returns>
        [HttpGet("current")]
        [Authorize] // 🔐 需要认证才能访问
        public async Task<ApiResponse<UserDto>> GetCurrentUser()
        {
            try
            {
                // 从JWT令牌中获取用户ID（JWT 仅用于身份，不用于角色/权限判定）
                var userIdClaim = User.FindFirst("userId") ?? User.FindFirst("sub");
                if (userIdClaim == null)
                {
                    return ApiResponse<UserDto>.Error("无法获取用户信息");
                }

                var userId = userIdClaim.Value;

                // 查询用户基础信息（角色仅用于构建 Roles DTO，权限以实时快照为准）
                var user = await _dbContext
                    .Db.Queryable<User>()
                    .Includes(u => u.Roles) // 包含角色信息
                    .FirstAsync(u => u.UserGUID == userId);

                // 活动用户检查 fail closed：停用或软删除用户不得读取任何会话载荷。
                if (user == null || !user.IsActive || user.IsDeleted)
                {
                    return ApiResponse<UserDto>.Error("用户不存在");
                }

                // 会话角色/权限以实时快照为唯一来源，避免停用/软删除角色被聚合进菜单权限。
                var snapshotResult = await _roleService.GetUserPermissionSnapshotAsync(userId);
                if (snapshotResult?.Success != true || snapshotResult.Data == null)
                {
                    return ApiResponse<UserDto>.Error("获取用户权限失败");
                }

                var snapshot = snapshotResult.Data;
                var snapshotRoleNames = new HashSet<string>(
                    snapshot.RoleNames ?? new List<string>(),
                    StringComparer.OrdinalIgnoreCase
                );

                // Roles DTO 只保留快照中的活动角色，并再次过滤停用/软删除，防止过期导航关系泄漏。
                var roles = (user.Roles ?? new List<Role>())
                    .Where(role =>
                        role.IsActive
                        && !role.IsDeleted
                        && snapshotRoleNames.Contains(role.RoleName)
                    )
                    .Select(role => new RoleDto
                    {
                        RoleGUID = role.RoleGUID,
                        RoleName = role.RoleName,
                        Description = role.Description,
                        IsActive = role.IsActive,
                        CreatedAt = role.CreatedAt,
                        UpdatedAt = role.UpdatedAt ?? role.CreatedAt,
                        UserCount = 0, // 这里不需要计算用户数
                    })
                    .ToList();

                var storesResult = await _userService.GetUserStoresAsync(userId);
                var userStores = storesResult.Success
                    ? storesResult.Data ?? new List<UserStoreDto>()
                    : new List<UserStoreDto>();

                // 构建返回数据
                var userDto = new UserDto
                {
                    UserGUID = user.UserGUID,
                    Username = user.Username,
                    Email = user.Email,
                    FullName = user.FullName,
                    LastLoginIp = user.LastLoginIp,
                    IsActive = user.IsActive,
                    CreatedAt = user.CreatedAt,
                    UpdatedAt = user.UpdatedAt ?? user.CreatedAt,
                    Roles = roles,
                    RoleNames = snapshot.RoleNames?.ToList() ?? new List<string>(),
                    Permissions = snapshot.PermissionCodes?.ToList() ?? new List<string>(),
                    ExactPermissions = snapshot.ExactPermissionCodes?.ToList() ?? new List<string>(),
                    StoreNames = userStores.Select(s => s.StoreName).ToList(),
                    Stores = userStores,
                    MustChangePassword = await UserPasswordChangeRequirements.IsRequiredAsync(
                        _dbContext.Db,
                        user.UserGUID
                    ),
                };

                return ApiResponse<UserDto>.OK(userDto, "获取用户信息成功");
            }
            catch (Exception ex)
            {
                return ApiResponse<UserDto>.Error($"获取用户信息失败：{ex.Message}");
            }
        }

        /// <summary>
        /// Bearer 用户登出接口
        /// </summary>
        /// <param name="request">登出请求，包含要撤销的刷新令牌</param>
        /// <returns>登出操作结果</returns>
        /// <remarks>
        /// 登出流程：
        /// 1. 验证刷新令牌是否存在
        /// 2. 调用认证服务撤销刷新令牌（使其失效）
        /// 3. 清除 Cookie 中的访问令牌和刷新令牌
        /// 4. 返回登出成功响应
        ///
        /// 注意：此操作会使当前的刷新令牌失效，用户需要重新登录才能获取新的令牌
        /// </remarks>
        [HttpPost("logout")]
        public async Task<ApiResponse<object>> Logout([FromBody] RefreshTokenRequest request)
        {
            // 如果提供了刷新令牌，则撤销该令牌
            if (!string.IsNullOrEmpty(request?.RefreshToken))
            {
                await _authService.RevokeRefreshTokenAsync(request.RefreshToken);
            }

            return ApiResponse<object>.CreateSuccess("登出成功");
        }

        /// <summary>
        /// 浏览器 Cookie 会话登出接口
        /// </summary>
        [HttpPost("session/logout")]
        [AllowAnonymous]
        public async Task<ApiResponse<object>> SessionLogout()
        {
            var refreshToken = CookieHelper.GetRefreshToken(Request.HttpContext);
            if (!string.IsNullOrEmpty(refreshToken))
            {
                await _authService.RevokeRefreshTokenAsync(refreshToken);
            }

            CookieHelper.ClearAuthCookies(Response);
            return ApiResponse<object>.CreateSuccess("登出成功");
        }

        /// <summary>
        /// 用户注册接口
        /// </summary>
        /// <param name="user">用户注册信息</param>
        /// <returns>注册结果，包含访问令牌和刷新令牌</returns>
        /// <remarks>
        /// 注册流程：
        /// 1. 验证请求数据格式
        /// 2. 调用认证服务注册新用户
        /// 3. 检查注册是否成功（用户名是否已存在）
        /// 4. 获取客户端IP地址和用户代理信息
        /// 5. 为新注册用户生成JWT令牌
        /// 6. 返回令牌响应
        ///
        /// 注意：注册成功后用户会自动登录，无需再次输入用户名密码
        /// </remarks>
        [HttpPost("register")]
        [AllowAnonymous] // 🔓 允许匿名访问，注册不需要认证
        public async Task<ApiResponse<TokenResponse>> Register([FromBody] User user)
        {
            // 验证请求模型状态
            if (!ModelState.IsValid)
            {
                return ApiResponse<TokenResponse>.Error("请求数据格式无效");
            }

            // 调用认证服务注册新用户
            var registeredUser = await _authService.RegisterAsync(user);

            // 检查注册是否成功
            if (registeredUser == null)
            {
                return ApiResponse<TokenResponse>.Error("用户名已存在");
            }

            // 获取客户端IP地址和用户代理信息（用于安全审计）
            var ipAddress = _clientIpResolver.Resolve(Request.HttpContext);
            var userAgent = Request.Headers["User-Agent"].ToString();

            registeredUser.LastLoginAt = DateTime.UtcNow;
            registeredUser.LastLoginIp = ipAddress;
            registeredUser.UpdatedAt = DateTime.UtcNow;
            await _dbContext.Db.Updateable(registeredUser)
                .UpdateColumns(user => new { user.LastLoginAt, user.LastLoginIp, user.UpdatedAt })
                .ExecuteCommandAsync();

            // 为新注册用户生成JWT访问令牌和刷新令牌
            var tokenResponse = await _authService.GenerateTokensAsync(
                registeredUser,
                ipAddress,
                userAgent
            );

            // 🍪 将令牌存储到 Cookie 中
            Response.Cookies.Append(
                "access_token",
                tokenResponse.AccessToken,
                CookieOptionsHelper.CreateAccessTokenCookieOptions()
            );

            Response.Cookies.Append(
                "refresh_token",
                tokenResponse.RefreshToken,
                CookieOptionsHelper.CreateRefreshTokenCookieOptions()
            );

            return ApiResponse<TokenResponse>.OK(tokenResponse, "注册成功");
        }

        /// <summary>
        /// 修改当前用户密码
        /// </summary>
        [HttpPost("change-password")]
        [Authorize]
        public async Task<ApiResponse<bool>> ChangePassword([FromBody] ChangePasswordDto dto)
        {
            if (!ModelState.IsValid)
            {
                return ApiResponse<bool>.Error("请求参数验证失败", "VALIDATION_ERROR", ModelState);
            }

            try
            {
                var userIdClaim = User.FindFirst("userId") ?? User.FindFirst("sub");
                if (userIdClaim == null)
                {
                    return ApiResponse<bool>.Error("无法获取用户信息");
                }

                // 当前会话保留，其余会话吊销：改密后旧设备上的登录随即失效。
                var success = await _authService.ChangePasswordAsync(
                    userIdClaim.Value,
                    dto,
                    User.FindFirst("sessionId")?.Value
                );
                if (success)
                {
                    return ApiResponse<bool>.OK(true, "密码修改成功");
                }
                return ApiResponse<bool>.Error("密码修改失败");
            }
            catch (Exception ex)
            {
                return ApiResponse<bool>.Error(ex.Message);
            }
        }

        private async Task<TokenResponse?> AuthenticateAndIssueTokensAsync(LoginRequest request)
        {
            var loginResponse = await _authService.LoginAsync(request);
            if (!loginResponse.Success)
            {
                return null;
            }

            var ipAddress = _clientIpResolver.Resolve(Request.HttpContext);
            var userAgent = Request.Headers["User-Agent"].ToString();

            var usernameLower = (request.Username ?? string.Empty).Trim().ToLower();
            var userList = await _dbContext
                .Db.Queryable<User>()
                .Includes(u => u.Roles)
                .Where(u => u.Username.ToLower() == usernameLower && u.IsActive && !u.IsDeleted)
                .Take(1)
                .ToListAsync();
            var user = userList.FirstOrDefault();

            if (user == null)
            {
                return null;
            }

            user.LastLoginIp = ipAddress;
            user.LastLoginAt = DateTime.UtcNow;
            user.UpdatedAt = DateTime.UtcNow;
            // 登录 IP 写入 User 便于后台用户管理直接查看最近一次登录来源。
            await _dbContext.Db.Updateable(user)
                .UpdateColumns(item => new { item.LastLoginAt, item.LastLoginIp, item.UpdatedAt })
                .ExecuteCommandAsync();

            var tokenResponse = await _authService.GenerateTokensAsync(user, ipAddress, userAgent);
            if (_loginDeviceAuditService != null)
            {
                var audit = await _loginDeviceAuditService.RecordAsync(user, new LoginDeviceAuditInput(
                    LoginSource: "AppLogin",
                    HardwareId: request.HardwareId,
                    SystemDeviceNumber: request.SystemDeviceNumber,
                    DeviceSystem: request.DeviceSystem,
                    StoreCode: request.StoreCode,
                    LoginIp: ipAddress,
                    UserAgent: userAgent,
                    LocationLatitude: request.LocationLatitude,
                    LocationLongitude: request.LocationLongitude,
                    LocationAccuracy: request.LocationAccuracy,
                    LocationCapturedAtUtc: request.LocationCapturedAtUtc
                ));
                tokenResponse.IsDeviceSwitched = audit.IsDeviceSwitched;
                tokenResponse.IsCommonDevice = audit.IsCommonDevice;
            }
            return tokenResponse;
        }

        private void NormalizeOptionalLoginLocation(LoginRequest request)
        {
            if (!HasLoginLocationPayload(request))
            {
                return;
            }

            var locationValidation = RequiredLocationValidator.Validate(
                request.LocationLatitude,
                request.LocationLongitude,
                request.LocationPermissionStatus,
                request.LocationCapturedAtUtc,
                "登录需要位置信息",
                TimeSpan.FromMinutes(5)
            );
            if (locationValidation.Success)
            {
                return;
            }

            // 普通账号密码登录不能因为 Android 定位服务不可用失败；无效定位只是不写入登录审计。
            _logger.LogWarning(
                "App账号登录定位审计无效，已忽略。ErrorCode={ErrorCode}, Message={Message}, Username={Username}",
                locationValidation.ErrorCode,
                locationValidation.Message,
                request.Username
            );
            request.LocationLatitude = null;
            request.LocationLongitude = null;
            request.LocationAccuracy = null;
            request.LocationPermissionStatus = null;
            request.LocationCapturedAtUtc = null;
        }

        private static bool HasLoginLocationPayload(LoginRequest request)
        {
            return request.LocationLatitude.HasValue
                || request.LocationLongitude.HasValue
                || request.LocationCapturedAtUtc.HasValue
                || !string.IsNullOrWhiteSpace(request.LocationPermissionStatus);
        }

        private async Task<List<string>> GetDirectUserPermissionCodesAsync(string userGuid)
        {
            if (string.IsNullOrWhiteSpace(userGuid) || !HasUserPermissionTable())
            {
                return new List<string>();
            }

            return await _dbContext.Db.Queryable<SysUserPermission>()
                .Where(item => item.UserGuid == userGuid && !item.IsDeleted)
                .Select(item => item.PermissionCode)
                .ToListAsync();
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

        private static SessionResponse CreateSessionResponse(TokenResponse tokenResponse)
        {
            return new SessionResponse
            {
                AccessTokenExpiry = tokenResponse.AccessTokenExpiry,
                RefreshTokenExpiry = tokenResponse.RefreshTokenExpiry,
                IsDeviceSwitched = tokenResponse.IsDeviceSwitched,
                IsCommonDevice = tokenResponse.IsCommonDevice,
            };
        }
    }
}
