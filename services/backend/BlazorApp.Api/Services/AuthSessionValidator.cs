using System.Security.Claims;
using BlazorApp.Api.Data;
using BlazorApp.Api.Services.MobileDeviceActivation;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Services
{
    public interface IAuthSessionValidator
    {
        Task<bool> IsAccessSessionActiveAsync(string userGuid, ClaimsPrincipal principal);

        Task<AuthMobileDeviceValidationResult> ValidateMobileDeviceAccessAsync(
            string userGuid,
            ClaimsPrincipal principal,
            CancellationToken cancellationToken = default);

        Task<AuthWebSessionValidationResult> ValidateWebAccessSessionAsync(
            string userGuid,
            string? sessionId,
            CancellationToken cancellationToken = default);
    }

    public sealed record AuthWebSessionValidationResult(
        bool IsValid,
        IReadOnlyList<string> ActiveRoleNames);

    public sealed record AuthMobileDeviceValidationResult(
        bool IsValid,
        IReadOnlyList<string> ActiveRoleNames,
        IReadOnlyList<string>? AccessibleStoreCodes = null,
        string? UserGuid = null);

    public sealed class AuthSessionValidator(
        SqlSugarContext dbContext,
        IMobileDeviceActivationService? mobileDeviceActivationService = null
    ) : IAuthSessionValidator
    {
        public async Task<bool> IsAccessSessionActiveAsync(string userGuid, ClaimsPrincipal principal)
        {
            if (string.IsNullOrWhiteSpace(userGuid))
            {
                return false;
            }

            if (string.Equals(
                    principal.FindFirst("token_use")?.Value,
                    MobileDeviceAccountTokenIssuer.TokenUse,
                    StringComparison.Ordinal))
            {
                if (mobileDeviceActivationService == null
                    || !MobileDeviceBindingContextResolver.TryResolve(principal, out var binding)
                    || !string.Equals(binding.UserGuid, userGuid, StringComparison.Ordinal))
                {
                    return false;
                }

                var validation = await mobileDeviceActivationService.ValidateTokenBindingAsync(
                    binding,
                    CancellationToken.None);

                // 设备账号令牌不依赖 RefreshToken，但每次请求都必须实时命中同一有效绑定。
                return validation.IsValid
                    && string.Equals(validation.UserGuid, userGuid, StringComparison.Ordinal);
            }

            var sessionId = principal.FindFirst("sessionId")?.Value;
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return false;
            }

            var now = DateTime.UtcNow;
            var activeSession = await dbContext.Db.Queryable<RefreshToken>()
                .FirstAsync(token =>
                    token.RefreshTokenGUID == sessionId
                    && token.UserGUID == userGuid
                    && !token.IsRevoked
                    && !token.IsDeleted
                    && token.ExpiresAt >= now
                );

            // access token 必须绑定仍有效的 RefreshToken 会话；被挤下线后这里立即失效。
            return activeSession != null;
        }

        public async Task<AuthMobileDeviceValidationResult> ValidateMobileDeviceAccessAsync(
            string userGuid,
            ClaimsPrincipal principal,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(userGuid)
                || mobileDeviceActivationService == null
                || !MobileDeviceBindingContextResolver.TryResolve(principal, out var binding)
                || !string.Equals(binding.UserGuid, userGuid, StringComparison.Ordinal))
            {
                return new AuthMobileDeviceValidationResult(false, Array.Empty<string>(), Array.Empty<string>());
            }

            // 两个数据库各执行一次实时聚合查询；并行发起，认证路径不再串行等待五次往返。
            var bindingTask = mobileDeviceActivationService
                .ValidateTokenBindingStateAsync(binding, cancellationToken);
            var accountTask = dbContext.Db
                .Queryable<User, UserStore, Store, UserRole, Role>(
                    (user, userStore, store, userRole, role) => new JoinQueryInfos(
                        JoinType.Left,
                        user.UserGUID == userStore.UserGUID && !userStore.IsDeleted,
                        JoinType.Left,
                        userStore.StoreGUID == store.StoreGUID
                            && !store.IsDeleted,
                        JoinType.Left,
                        user.UserGUID == userRole.UserGUID && !userRole.IsDeleted,
                        JoinType.Left,
                        userRole.RoleGUID == role.RoleGUID
                            && role.IsActive
                            && !role.IsDeleted))
                .Where((user, userStore, store, userRole, role) =>
                    user.UserGUID == userGuid
                    && user.IsActive
                    && !user.IsDeleted
                    // 被标记「不允许登录」的账号，已签发的设备会话也立即失效。
                    && SqlFunc.Subqueryable<UserLoginRestriction>()
                        .Where(restriction => restriction.UserGUID == userGuid)
                        .NotAny())
                .Select((user, userStore, store, userRole, role) => new AuthMobileDeviceRoleRow
                {
                    UserGuid = user.UserGUID,
                    StoreCode = store.StoreCode,
                    StoreIsActive = store.IsActive,
                    RoleName = role.RoleName,
                })
                .ToListAsync(cancellationToken);

            await Task.WhenAll(bindingTask, accountTask);
            var bindingValidation = await bindingTask;
            var accountRows = await accountTask;
            var hasStoreAccess = accountRows.Any(row =>
                string.Equals(row.UserGuid, userGuid, StringComparison.Ordinal)
                && row.StoreIsActive == true
                && string.Equals(row.StoreCode, bindingValidation.StoreCode, StringComparison.OrdinalIgnoreCase));
            if (!bindingValidation.IsValid
                || !string.Equals(bindingValidation.UserGuid, userGuid, StringComparison.Ordinal)
                || !hasStoreAccess)
            {
                return new AuthMobileDeviceValidationResult(false, Array.Empty<string>(), Array.Empty<string>());
            }

            return new AuthMobileDeviceValidationResult(
                true,
                accountRows
                    .Select(row => row.RoleName)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Select(name => name!)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
                accountRows
                    .Where(row => string.Equals(row.UserGuid, userGuid, StringComparison.Ordinal))
                    .Select(row => row.StoreCode)
                    .Where(code => !string.IsNullOrWhiteSpace(code))
                    .Select(code => code!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                userGuid);
        }

        public async Task<AuthWebSessionValidationResult> ValidateWebAccessSessionAsync(
            string userGuid,
            string? sessionId,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(userGuid) || string.IsNullOrWhiteSpace(sessionId))
            {
                return new AuthWebSessionValidationResult(false, Array.Empty<string>());
            }

            var now = DateTime.UtcNow;
            // 一次查询实时校验用户、会话和角色；角色条件留在 LEFT JOIN 内，无角色不等于会话失效。
            var rows = await dbContext.Db
                .Queryable<User, RefreshToken, UserRole, Role>(
                    (user, session, userRole, role) => new JoinQueryInfos(
                        JoinType.Inner,
                        user.UserGUID == session.UserGUID
                            && session.RefreshTokenGUID == sessionId
                            && !session.IsRevoked
                            && !session.IsDeleted
                            && session.ExpiresAt >= now,
                        JoinType.Left,
                        user.UserGUID == userRole.UserGUID
                            && !userRole.IsDeleted,
                        JoinType.Left,
                        userRole.RoleGUID == role.RoleGUID
                            && role.IsActive
                            && !role.IsDeleted))
                .Where((user, session, userRole, role) =>
                    user.UserGUID == userGuid
                    && user.IsActive
                    && !user.IsDeleted
                    // 被标记「不允许登录」的账号，已签发的 Web 会话也立即失效。
                    && SqlFunc.Subqueryable<UserLoginRestriction>()
                        .Where(restriction => restriction.UserGUID == userGuid)
                        .NotAny())
                .Select((user, session, userRole, role) => new AuthWebSessionRoleRow
                {
                    UserGuid = user.UserGUID,
                    RoleName = role.RoleName,
                })
                .ToListAsync(cancellationToken);

            if (rows.Count == 0)
            {
                return new AuthWebSessionValidationResult(false, Array.Empty<string>());
            }

            var activeRoleNames = rows
                .Select(row => row.RoleName)
                .Where(roleName => !string.IsNullOrWhiteSpace(roleName))
                .Select(roleName => roleName!)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            return new AuthWebSessionValidationResult(true, activeRoleNames);
        }

        private sealed class AuthWebSessionRoleRow
        {
            public string UserGuid { get; set; } = string.Empty;

            public string? RoleName { get; set; }
        }

        private sealed class AuthMobileDeviceRoleRow
        {
            public string UserGuid { get; set; } = string.Empty;
            public string? StoreCode { get; set; }
            public bool? StoreIsActive { get; set; }
            public string? RoleName { get; set; }
        }
    }
}
