namespace BlazorApp.Shared.DTOs;

/// <summary>
/// 仓库改零售价时的两个开关，所有改价入口共用同一套语义（请求里不传 = 默认勾选）：
/// <list type="bullet">
/// <item>SyncStoreRetailPrices（同步分店零售价）：勾选 = 直接把各分店零售价改成新价；不勾 = 不动分店价。</item>
/// <item>GenerateTasks（生成价格任务）：勾选 = 给分店生成价格更新任务；不勾 = 不通知分店。</item>
/// </list>
/// 组合结果：同步 + 生成 = 直接改价并发「待换标签」任务；不同步 + 生成 = 不动分店价并发「改价格」任务；
/// 同步 + 不生成 = 直接改价、无任务（分店价改动立即同步 HQ，因为没有「任务执行」这一步可等）；
/// 不同步 + 不生成 = 只改仓库价。
/// </summary>
public readonly record struct WarehousePriceSyncPolicy(bool SyncStoreRetailPrices, bool GenerateTasks)
{
    /// <summary>默认：两个都勾选。</summary>
    public static WarehousePriceSyncPolicy Default => new(true, true);

    /// <summary>从请求里的可空布尔解析；null（旧客户端不传）视同勾选。</summary>
    public static WarehousePriceSyncPolicy From(bool? syncStoreRetailPrices, bool? generateTasks) =>
        new(syncStoreRetailPrices ?? true, generateTasks ?? true);

    /// <summary>直接改了分店价、又不生成任务时，没有任务执行可等，需要在改价当下同步 HQ。</summary>
    public bool ShouldSyncHqImmediately => SyncStoreRetailPrices && !GenerateTasks;
}
