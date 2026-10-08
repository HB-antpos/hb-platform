using Microsoft.Data.SqlClient;

namespace Hbpos.Api.Services;

/// <summary>
/// 「VoucherTerms / InstallmentTerms」两列的降级读取。
/// 门店资料与下发快照的读取用的是写死列名的原生 SQL；生产部署顺序可能是先发 Hbpos.Api、后跑 HBweb 迁移，
/// 此时 Release 表里新列还不存在，读新列的 SQL 会抛 SqlException 207（Invalid column name）。
/// 这里统一做法：先跑含新列的 SQL，遇到 207 就回退到不含新列的旧 SQL（新字段为 null，即走默认文案），
/// 保证已上线的退货政策同步/载入接口不会因为迁移还没跑而整体 500。
/// </summary>
internal static class StoreReceiptProfileColumnFallback
{
    /// <summary>SQL Server 的「列名无效」错误号。</summary>
    internal const int InvalidColumnNameError = 207;

    /// <summary>设备每 60 秒轮询一次，回退日志同一个读取点最多每 30 分钟记一条。</summary>
    internal static readonly TimeSpan LogInterval = TimeSpan.FromMinutes(30);

    internal static ColumnFallbackLogThrottle SharedThrottle { get; } = new(TimeProvider.System, LogInterval);

    /// <summary>异常链（含 SqlSugar 包装后的 InnerException）里是否有「列名无效」的 SqlException。</summary>
    internal static bool IsInvalidColumnName(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException { Number: InvalidColumnNameError })
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 先执行 <paramref name="withNewColumns"/>；仅当它因「列名无效」失败时才改跑 <paramref name="legacy"/>。
    /// 其他异常（含取消）原样抛出；旧 SQL 自己再失败也原样抛出，不吞掉真正的库结构问题。
    /// </summary>
    internal static async Task<T> QueryAsync<T>(
        Func<Task<T>> withNewColumns,
        Func<Task<T>> legacy,
        string scope,
        ILogger? logger = null,
        ColumnFallbackLogThrottle? throttle = null)
    {
        try
        {
            return await withNewColumns();
        }
        catch (Exception exception) when (IsInvalidColumnName(exception))
        {
            // 降噪：同一读取点首次与之后每隔 LogInterval 才记一次，避免每次轮询刷日志；日志只含读取点与库报错文本。
            if (logger is not null && (throttle ?? SharedThrottle).ShouldLog(scope))
            {
                logger.LogWarning(
                    "Store receipt profile read ({Scope}) hit an invalid column and fell back to the legacy query " +
                    "without VoucherTerms/InstallmentTerms; the HBweb migration may not have run yet. Detail: {Detail}",
                    scope,
                    FindSqlMessage(exception));
            }

            return await legacy();
        }
    }

    private static string FindSqlMessage(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException sqlException)
            {
                return sqlException.Message;
            }
        }

        return exception.Message;
    }
}

/// <summary>按读取点（scope）限频的日志闸门；时钟可注入以便单测。</summary>
internal sealed class ColumnFallbackLogThrottle(TimeProvider timeProvider, TimeSpan interval)
{
    private readonly object gate = new();
    private readonly Dictionary<string, DateTimeOffset> lastLoggedAt = new(StringComparer.Ordinal);

    public bool ShouldLog(string scope)
    {
        var now = timeProvider.GetUtcNow();
        lock (gate)
        {
            if (lastLoggedAt.TryGetValue(scope, out var last) && now - last < interval)
            {
                return false;
            }

            lastLoggedAt[scope] = now;
            return true;
        }
    }
}
