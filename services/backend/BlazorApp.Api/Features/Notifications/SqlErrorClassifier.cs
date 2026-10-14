using Microsoft.Data.SqlClient;

namespace BlazorApp.Api.Features.Notifications;

/// <summary>
/// 通知平台里需要按「异常类别」分支处理的几个数据库错误判断。只看类型与错误号，不读异常 Message
/// （SqlSugar 包装后的 Message 可能带 SQL 文本与参数值，其中可能有推送令牌）。
/// </summary>
internal static class SqlErrorClassifier
{
    /// <summary>唯一索引 / 唯一约束冲突（SQL Server 2601 / 2627；SQLite 测试库用文本识别）。</summary>
    internal static bool IsUniqueViolation(Exception exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is SqlException { Number: 2601 or 2627 })
            {
                return true;
            }

            // SQLite（单元测试）没有错误号常量，沿用通知发布器原来的文本识别；只匹配固定的驱动文案，不外传。
            if (current.Message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>对象不存在（SQL Server 208：迁移尚未执行，表还不存在；SQLite 测试库用文本识别）。</summary>
    internal static bool IsMissingObject(Exception exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is SqlException { Number: 208 })
            {
                return true;
            }

            if (current.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
