using BlazorApp.Api.Data;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace BlazorApp.Api.Filters
{
    public enum LegacyDatabaseKind
    {
        Hq,
        HBSales,
    }

    /// <summary>
    /// 只依赖旧库的独立入口：旧库断开后，通过认证授权的请求直接返回 410，不进入 action。
    /// 业务保存里顺带写 HQ 的串联入口不用本特性，由服务层跳过 HQ 部分，保存仍然成功。
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
    public sealed class LegacyDatabaseDisabledAttribute : ActionFilterAttribute
    {
        public const string DisabledErrorCode = "LEGACY_DATABASE_DISCONNECTED";

        public LegacyDatabaseDisabledAttribute(LegacyDatabaseKind database)
        {
            Database = database;
            // 先于参数校验运行：已停用的入口不管请求体是否合法，都统一返回 410。
            Order = int.MinValue;
        }

        public LegacyDatabaseKind Database { get; }

        public override void OnActionExecuting(ActionExecutingContext context)
        {
            var enabled = Database == LegacyDatabaseKind.Hq
                ? LegacyDatabaseGate.HqEnabled
                : LegacyDatabaseGate.HBSalesEnabled;
            if (enabled)
            {
                return;
            }

            var message = Database == LegacyDatabaseKind.Hq
                ? "HQ 数据库已断开，此功能已停用"
                : "HBSales 数据库已断开，此功能已停用";
            context.Result = new ObjectResult(ApiResponse<object>.Error(message, DisabledErrorCode))
            {
                StatusCode = StatusCodes.Status410Gone,
            };
        }
    }
}
