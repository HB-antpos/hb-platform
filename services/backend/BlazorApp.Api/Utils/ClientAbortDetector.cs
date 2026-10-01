using System.Globalization;
using System.Resources;
using Microsoft.Data.SqlClient;

namespace BlazorApp.Api.Utils
{
    /// <summary>
    /// 统一判定「客户端主动中止请求」，供全局异常过滤器、管道日志中间件与业务 catch 共用。
    /// 两个条件必须同时满足：
    /// 1. 请求令牌已触发——HttpContext.RequestAborted，或由控制器原样传下来的调用方令牌；
    /// 2. 异常属于取消形态——OperationCanceledException（含 TaskCanceledException），
    ///    或 SqlClient 在命令执行 / 读取中途被令牌取消时抛出的「用户取消」SqlException。
    /// 服务端自己的超时（CTS、HttpClient.Timeout、SqlCommand 超时）不会触发请求令牌，仍按故障处理。
    /// </summary>
    public static class ClientAbortDetector
    {
        /// <summary>SqlClient 命令超时（Execution Timeout Expired）的错误号。</summary>
        private const int SqlCommandTimeoutNumber = -2;

        /// <summary>SqlClient 在收到取消应答后自行追加的错误的英文文本（生产容器为不变文化，日志即此文本）。</summary>
        private const string SqlOperationCancelledInvariantMessage = "Operation cancelled by user.";

        // 同一条文本会随 UI 文化本地化（zh-Hans 为「用户已取消操作。」），用驱动自带资源按当前文化比对；
        // 资源名是 Microsoft.Data.SqlClient 6.x 内部约定，升级后若不存在则退化为只比对英文文本。
        private static readonly ResourceManager SqlClientStrings = new(
            "Microsoft.Data.SqlClient.Resources.Strings",
            typeof(SqlException).Assembly
        );

        /// <summary>
        /// 请求令牌已触发且异常属于取消形态时返回 true。
        /// </summary>
        public static bool IsClientAbort(Exception? exception, CancellationToken requestAborted)
        {
            // 令牌是硬前提：同样形态的 SqlException 也会由服务端 CTS 超时产生（后台 worker 即如此），不能只看异常。
            return exception != null
                && requestAborted.IsCancellationRequested
                && IsCancellationShaped(exception);
        }

        /// <summary>
        /// 异常本身或其 InnerException 链上是否为取消形态；遇到第一个 OCE / SqlException 即给出结论。
        /// </summary>
        internal static bool IsCancellationShaped(Exception exception)
        {
            for (var current = exception; current != null; current = current.InnerException)
            {
                switch (current)
                {
                    case OperationCanceledException:
                        return true;
                    case SqlException sqlException:
                        return IsSqlClientUserCancellation(sqlException);
                }
            }

            return false;
        }

        /// <summary>
        /// 令牌在执行 / 读取中途触发时，SqlClient 抛出的是 SqlException 而不是 OCE：错误集合里有一条
        /// 驱动自己追加的 Number=0「Operation cancelled by user.」（可能伴随 3980 批处理已中止、3621 语句已终止等服务端错误）。
        /// 只要含 Number=-2（命令超时）就一票否决，超时必须按服务端故障记录。
        /// </summary>
        private static bool IsSqlClientUserCancellation(SqlException exception)
        {
            var cancelledByUser = false;
            foreach (SqlError error in exception.Errors)
            {
                if (error.Number == SqlCommandTimeoutNumber)
                {
                    return false;
                }

                if (error.Number == 0 && IsSqlOperationCancelledMessage(error.Message))
                {
                    cancelledByUser = true;
                }
            }

            return cancelledByUser;
        }

        private static bool IsSqlOperationCancelledMessage(string? message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return false;
            }

            if (string.Equals(message, SqlOperationCancelledInvariantMessage, StringComparison.Ordinal))
            {
                return true;
            }

            try
            {
                // 文化随 ExecutionContext 流动，判定时的 UI 文化与驱动生成消息时一致。
                var localized = SqlClientStrings.GetString("SQL_OperationCancelled", CultureInfo.CurrentUICulture);
                return !string.IsNullOrEmpty(localized)
                    && string.Equals(message, localized, StringComparison.Ordinal);
            }
            catch (MissingManifestResourceException)
            {
                return false;
            }
        }
    }
}
