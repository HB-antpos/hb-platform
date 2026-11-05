using System.Security.Claims;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.Helper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Caching.Memory;

namespace BlazorApp.Api.Filters
{
    /// <summary>
    /// 全局过滤器：没有 <see cref="Permissions.DomesticPurchase.ViewSupplierName"/> 的账号，
    /// 响应里带 <see cref="DomesticSupplierInfoJsonConverter"/> 的属性一律输出 null（只留供应商代码），
    /// 并且接口方法执行期间不能用供应商名称做搜索 / 排序条件。
    /// 做成全局而不是逐个接口挂特性：国内供应商名称散在四十多组接口里，漏挂一个就是泄露。
    /// 遮罩在序列化阶段完成，不改动服务层 / 缓存里的对象。
    /// </summary>
    public sealed class DomesticSupplierInfoMaskingFilter : IAsyncActionFilter, IAsyncResultFilter
    {
        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            var canView = await context.HttpContext.CanViewDomesticSupplierNameAsync();
            // 这一段只关「按名称搜索」，不开遮罩：接口方法里写缓存、同步 HQ、记审计的序列化必须拿到原值。
            // AsyncLocal 在本方法内设置后会流入 next()（接口方法及其调用的查询），方法返回时由 Dispose 恢复。
            using (DomesticSupplierInfoMasking.BeginQueryScope(canView))
            {
                await next();
            }
        }

        public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
        {
            // 文件、重定向、空结果不经过 JSON 转换器；导出文件里的名称由各导出代码自己按权限处理。
            if (context.Result is not (ObjectResult or JsonResult))
            {
                await next();
                return;
            }

            var canView = await context.HttpContext.CanViewDomesticSupplierNameAsync();
            // 遮罩只包住响应序列化这一段。
            using (DomesticSupplierInfoMasking.BeginMaskingScope(mask: !canView))
            {
                await next();
            }
        }
    }

    public static class DomesticSupplierInfoVisibilityExtensions
    {
        private static readonly object RequestItemKey = new();

        /// <summary>同一账号的权限结果缓存时长：授权或收回最长这么久后生效，换来每个请求不必多查一次库。</summary>
        internal static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(60);

        /// <summary>
        /// 当前账号是否可查看国内供应商名称（管理员与「管理国内供应商」持有者在权限处理器里自动通过）。
        /// 未登录、设备头放行、拿不到授权服务（如单元测试直接 new 控制器）一律按无权限处理，宁可遮罩也不外泄。
        /// 同一请求内只算一次；跨请求按账号缓存 <see cref="CacheDuration"/>。
        /// </summary>
        public static async Task<bool> CanViewDomesticSupplierNameAsync(this HttpContext? httpContext)
        {
            if (httpContext == null)
            {
                return false;
            }

            if (httpContext.Items.TryGetValue(RequestItemKey, out var resolved) && resolved is bool known)
            {
                return known;
            }

            var canView = await ResolveAsync(httpContext);
            httpContext.Items[RequestItemKey] = canView;
            return canView;
        }

        private static async Task<bool> ResolveAsync(HttpContext httpContext)
        {
            var user = httpContext.User;
            if (user?.Identity?.IsAuthenticated != true)
            {
                return false;
            }

            var authorizationService = httpContext.RequestServices?.GetService<IAuthorizationService>();
            if (authorizationService == null)
            {
                return false;
            }

            // 与权限处理器取账号标识的顺序一致；取不到标识的主体（如服务令牌）不缓存，每次直接走授权。
            var principalId =
                user.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? user.FindFirst("userId")?.Value
                ?? user.FindFirst("userGuid")?.Value
                ?? user.FindFirst(ClaimTypes.Name)?.Value
                ?? user.FindFirst("sub")?.Value;
            var cache = string.IsNullOrWhiteSpace(principalId)
                ? null
                : httpContext.RequestServices?.GetService<IMemoryCache>();
            // 认证方式进缓存键：同一标识的用户令牌与设备 / 服务令牌权限来源不同，不能共用结果。
            var cacheKey = $"domestic-supplier-name-view:{user.Identity.AuthenticationType}:{principalId}";
            if (cache != null && cache.TryGetValue(cacheKey, out bool cached))
            {
                return cached;
            }

            try
            {
                var result = await authorizationService.AuthorizeAsync(
                    user,
                    null,
                    Permissions.DomesticPurchase.ViewSupplierName
                );
                cache?.Set(cacheKey, result.Succeeded, CacheDuration);
                return result.Succeeded;
            }
            catch (Exception ex)
            {
                // 本过滤器是全局的：权限查询出错不能拖垮所有接口，按无权限处理（宁可遮罩），且不缓存这次失败。
                httpContext.RequestServices
                    ?.GetService<ILoggerFactory>()
                    ?.CreateLogger(typeof(DomesticSupplierInfoVisibilityExtensions))
                    .LogError(ex, "查询「查看国内供应商名称」权限失败，本次请求按无权限处理");
                return false;
            }
        }
    }
}
