using System.Security.Claims;
using BlazorApp.Api.Authentication;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.MobileDeviceActivation;
using BlazorApp.Shared.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace BlazorApp.Api.Authorization
{
    public class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
    {
        private readonly IServiceScopeFactory _serviceScopeFactory;
        private readonly ILogger<PermissionAuthorizationHandler> _logger;
        private readonly bool _allowLegacyManageTokenForAppUpdateDecisions;

        public PermissionAuthorizationHandler(
            IServiceScopeFactory serviceScopeFactory,
            IMemoryCache cache,
            ILogger<PermissionAuthorizationHandler> logger,
            IOptions<AppUpdatePolicyOptions> appUpdatePolicyOptions
        )
        {
            _serviceScopeFactory = serviceScopeFactory;
            _logger = logger;
            _allowLegacyManageTokenForAppUpdateDecisions =
                appUpdatePolicyOptions.Value.AllowLegacyManageTokenForAppUpdateDecisions;
        }

        protected override async Task HandleRequirementAsync(
            AuthorizationHandlerContext context,
            PermissionRequirement requirement
        )
        {
            if (requirement.Permission == EmployeeCashierBarcodeSelfServicePolicy.Name)
            {
                // 仅接受与条码服务一致的本人身份；设备、扩展和服务令牌不能借自助策略取得员工码。
                var userGuid = context.User.FindFirst("userId")?.Value
                    ?? context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (context.User.Identity?.IsAuthenticated == true
                    && !string.IsNullOrWhiteSpace(userGuid)
                    && !IsServiceApiToken(context.User)
                    && !context.User.HasClaim("token_use", MobileDeviceAccountTokenIssuer.TokenUse)
                    && !context.User.HasClaim("token_use", "browser_extension"))
                {
                    context.Succeed(requirement);
                }

                return;
            }

            if (IsServiceApiToken(context.User))
            {
                // 关键位置：service token 只认认证 handler 写入的专用 scope，不能回落到用户角色或普通 permission claim。
                if (HasServiceApiScope(context.User, requirement.Permission))
                {
                    context.Succeed(requirement);
                }
                else if (
                    _allowLegacyManageTokenForAppUpdateDecisions
                    && string.Equals(
                        requirement.Permission,
                        ServiceApiScopes.ReadAppUpdateDecisions,
                        StringComparison.Ordinal
                    )
                    && HasServiceApiScope(
                        context.User,
                        Permissions.System.ManageAppDownloads
                    )
                )
                {
                    // 仅 decision-read policy 临时接受旧 scope，避免形成全局权限别名。
                    context.Succeed(requirement);
                }

                return;
            }

            // 大屏收银设备令牌不代表任何员工，任何按权限码放行的接口都不给（例如订货助手的跨店销量）。
            if (BlazorApp.Api.Services.React.BrowserExtensionPosDevicePrincipal.IsPosDevice(context.User))
            {
                return;
            }

            var userId =
                context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? context.User.FindFirst("userId")?.Value
                ?? context.User.FindFirst("userGuid")?.Value
                ?? context.User.FindFirst(ClaimTypes.Name)?.Value
                ?? context.User.FindFirst("sub")?.Value;

            if (string.IsNullOrEmpty(userId))
            {
                return;
            }

            if (Permissions.IsAttendanceSelfServiceGranted(requirement.Permission))
            {
                context.Succeed(requirement);
                return;
            }

            try
            {
                using var scope = _serviceScopeFactory.CreateScope();
                var roleService = scope.ServiceProvider.GetRequiredService<IRoleService>();
                // 权限别名和管理员角色在 RoleService 的同一条 EXISTS 查询中解析，避免每个别名和每个管理员角色各发一次 SQL。
                var result = await roleService.UserHasPermissionAsync(userId, requirement.Permission);
                if (result.Data)
                {
                    context.Succeed(requirement);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "权限验证失败: User={UserId}, Permission={Permission}",
                    userId,
                    requirement.Permission
                );
            }
        }

        private static bool IsServiceApiToken(ClaimsPrincipal user)
        {
            return user.HasClaim(
                ServiceApiTokenAuthenticationDefaults.TokenTypeClaim,
                "true"
            );
        }

        private static bool HasServiceApiScope(ClaimsPrincipal user, string permission)
        {
            return user
                .FindAll(ServiceApiTokenAuthenticationDefaults.ScopeClaim)
                .Any(claim => string.Equals(claim.Value, permission, StringComparison.OrdinalIgnoreCase));
        }
    }
}
