using BlazorApp.Shared.DTOs;
using SqlSugar;

namespace BlazorApp.Api.Interfaces.React;

/// <summary>
/// 把分店价投影到 HQ 的入队入口。调用时机由业务口径决定，不由本接口决定：
/// <list type="bullet">
/// <item>分店店员「标记已换标签 / 打印完成」时，给该分店+商品入队（价格变更任务口径：执行过才同步，未执行的分店 HQ 不变）。</item>
/// <item>仓库改价时不生成任务、又直接改了分店价（没有「任务执行」可等）：改价当下入队。</item>
/// </list>
/// 只写 HBweb 的 HQ 同步 outbox，不直接连 HQ 库，因此 HQ 在 0–8 点关闭也不会影响调用方；
/// 实际写 HQ 由后台 outbox worker 重试完成。
/// </summary>
public interface IWarehouseStorePriceHqSyncEnqueuer
{
    /// <summary>
    /// 入队一条「分店价 → HQ」任务。调用方已在事务内时与其同事务提交：
    /// 价格落库与入队要么都成功要么一起回滚，不会出现 HBweb 已改而 HQ 永远不知道的情况。
    /// </summary>
    /// <param name="transactionDb">调用方正在使用的连接（含事务）。</param>
    /// <param name="productCode">商品编码。</param>
    /// <param name="storeCodes">本次被改写价格的分店；内部会剔除 HQ 里没有的停用分店。</param>
    /// <param name="fieldMask">要投影的字段，取自 <c>ProductMaintenanceHqFieldMasks</c>。</param>
    /// <param name="updatedBy">操作人（仅在无法识别当前登录用户时用于标记系统来源）。</param>
    /// <param name="source">来源标识，写入 outbox 的 Source 与 OperationKey 前缀。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>outbox 操作状态（OperationId 可记到价格任务上）；同步开关关闭或没有可同步的分店时返回 null。</returns>
    Task<ProductHqSyncOperationStatusDto?> EnqueueAsync(
        ISqlSugarClient transactionDb,
        string productCode,
        IEnumerable<string?> storeCodes,
        IReadOnlyCollection<string> fieldMask,
        string updatedBy,
        string source,
        CancellationToken cancellationToken = default
    );
}
