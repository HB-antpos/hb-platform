using SqlSugar;

namespace BlazorApp.Api.Interfaces.React;

/// <summary>
/// 仓库侧（Web 编辑、移动端仓库维护）覆盖分店价之后，把这些分店的价格投影到 HQ 的入队入口。
/// 只写 HBweb 的 HQ 同步 outbox，不直接连 HQ 库，因此 HQ 在 0–8 点关闭也不会影响仓库保存；
/// 实际写 HQ 由后台 outbox worker 重试完成。
/// </summary>
public interface IWarehouseStorePriceHqSyncEnqueuer
{
    /// <summary>
    /// 在调用方的事务内入队一条「分店价 → HQ」任务，与分店价改写同事务提交：
    /// 价格落库与入队要么都成功要么一起回滚，不会出现 HBweb 已改而 HQ 永远不知道的情况。
    /// </summary>
    /// <param name="transactionDb">调用方正在使用的连接（含事务）。</param>
    /// <param name="productCode">商品编码。</param>
    /// <param name="storeCodes">本次被改写价格的分店；内部会剔除 HQ 里没有的停用分店。</param>
    /// <param name="fieldMask">要投影的字段，取自 <c>ProductMaintenanceHqFieldMasks</c>。</param>
    /// <param name="updatedBy">操作人（仅在无法识别当前登录用户时用于标记系统来源）。</param>
    /// <param name="source">来源标识，写入 outbox 的 Source 与 OperationKey 前缀。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task EnqueueAsync(
        ISqlSugarClient transactionDb,
        string productCode,
        IEnumerable<string?> storeCodes,
        IReadOnlyCollection<string> fieldMask,
        string updatedBy,
        string source,
        CancellationToken cancellationToken = default
    );
}
