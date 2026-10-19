using System.Diagnostics;
using BlazorApp.Api.Data;

namespace BlazorApp.Api.Services;

/// <summary>移动端「网络测试」用的服务器端探测：数据库往返一次的耗时。</summary>
public interface INetworkDiagnosticsProbe
{
    /// <summary>执行一次最轻的查询，返回耗时毫秒；超时或失败返回 null（不抛异常）。</summary>
    Task<long?> PingDatabaseAsync(CancellationToken cancellationToken);
}

public sealed class NetworkDiagnosticsProbe(SqlSugarContext context, ILogger<NetworkDiagnosticsProbe> logger)
    : INetworkDiagnosticsProbe
{
    // 手机端整项测试的超时是 8 秒，服务器端探测必须更早结束，保证诊断接口本身能按时返回。
    internal static readonly TimeSpan DatabaseTimeout = TimeSpan.FromSeconds(5);

    public async Task<long?> PingDatabaseAsync(CancellationToken cancellationToken)
    {
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var query = context.Db.Ado.GetIntAsync("SELECT 1");
            // SqlSugar 的 Ado 查询不接受取消令牌，用 WhenAny 兜住超时，避免诊断接口被慢库拖住。
            var completed = await Task.WhenAny(query, Task.Delay(DatabaseTimeout, cancellationToken));
            if (completed != query)
            {
                return null;
            }

            await query;
            return stopwatch.ElapsedMilliseconds;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "网络测试数据库探测失败");
            return null;
        }
    }
}
