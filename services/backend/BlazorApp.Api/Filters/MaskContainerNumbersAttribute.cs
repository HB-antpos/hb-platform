using System.Security.Claims;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.Helper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace BlazorApp.Api.Filters
{
    /// <summary>
    /// 响应里含货柜编号的接口挂这个特性：没有 <see cref="Permissions.Container.ViewFullNumber"/> 的账号，
    /// 序列化时带 <see cref="ContainerNumberJsonConverter"/> 的属性一律输出遮罩值（只露后 4 位）。
    /// 权限只在返回对象结果时查一次；遮罩在序列化阶段完成，不改动服务层/缓存里的 DTO 对象。
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
    public sealed class MaskContainerNumbersAttribute : Attribute, IAsyncResultFilter
    {
        public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
        {
            // 文件、重定向、空结果不含 JSON 货柜编号，不必查权限。
            if (context.Result is not ObjectResult)
            {
                await next();
                return;
            }

            var showFull = await context.HttpContext.CanViewFullContainerNumberAsync();
            // AsyncLocal 在本方法内设置后会流入 next()（响应序列化），方法返回时由 Dispose 恢复。
            using (ContainerNumberMasking.BeginMaskingScope(mask: !showFull))
            {
                await next();
            }
        }
    }

    public static class ContainerNumberVisibilityExtensions
    {
        /// <summary>
        /// 当前账号是否可查看完整货柜编号（管理员角色在权限处理器里自动通过）。
        /// 拿不到授权服务（如单元测试直接 new 控制器）时按无权限处理，宁可遮罩也不外泄。
        /// </summary>
        public static async Task<bool> CanViewFullContainerNumberAsync(this HttpContext? httpContext)
        {
            var authorizationService = httpContext?.RequestServices?.GetService<IAuthorizationService>();
            if (httpContext == null || authorizationService == null)
            {
                return false;
            }

            var user = httpContext.User ?? new ClaimsPrincipal();
            var result = await authorizationService.AuthorizeAsync(
                user,
                null,
                Permissions.Container.ViewFullNumber
            );
            return result.Succeeded;
        }
    }
}
