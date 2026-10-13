using SqlSugar;

namespace BlazorApp.Shared.Models;

/// <summary>
/// 门店价格更新任务的“已通知”记录：每个任务一行，记录已经通知到的变更次数（<c>ChangeCount</c>）。
/// 后台合并推送据此判断“新增 / 再次变更”的任务：Pending 且没有本行，或本行的 ChangeCount 小于任务当前值，即视为未通知。
/// 不用时间水位：对账 worker 会整行重写 Pending 任务，UpdatedAtUtc 不可靠；InitiatedAtUtc 取自提交前时间，长事务可能晚于它提交。
/// 表由迁移 20261015.001-store-price-notice 创建，实体不进自动建表清单。
/// <para>
/// 特殊行：<c>TaskId = 0</c> 是“首次启用基线已完成”的标记（真实任务 Id 自增从 1 开始，不会冲突），
/// 保证表一旦启用就不再为空，清理也不会删除它。
/// </para>
/// </summary>
[SugarTable("StorePriceUpdateNotice")]
public sealed class StorePriceUpdateNotice
{
    /// <summary>对应 <see cref="StorePriceUpdateTask.Id"/>；不设外键（任务过期清理不应被本表阻塞）。</summary>
    [SugarColumn(IsPrimaryKey = true, IsIdentity = false, IsNullable = false)]
    public long TaskId { get; set; }

    /// <summary>已通知到的任务变更次数，对应 <see cref="StorePriceUpdateTask.ChangeCount"/>。</summary>
    [SugarColumn(IsNullable = false)]
    public int ChangeCount { get; set; }

    [SugarColumn(IsNullable = false)]
    public DateTime NotifiedAtUtc { get; set; }

    /// <summary>首次启用基线标记行的 TaskId。</summary>
    public const long BaselineMarkerTaskId = 0;
}
