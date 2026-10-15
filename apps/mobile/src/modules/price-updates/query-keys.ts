const ROOT = "priceUpdates";

/** 不传参数返回前缀键，用于一次性失效/重置所有分店与筛选条件下的任务列表。 */
export function priceUpdateTasksQueryKey(
  storeCode?: string | null,
  tab?: string,
  kind?: string,
  hqSyncFailedOnly?: boolean
) {
  return storeCode === undefined
    ? ([ROOT, "tasks"] as const)
    : ([ROOT, "tasks", storeCode, tab, kind, Boolean(hqSyncFailedOnly)] as const);
}

/** 工作台角标与列表页共用；mutation 成功后按前缀失效。 */
export function priceUpdateCountQueryKey(storeCode?: string | null) {
  return storeCode === undefined ? ([ROOT, "count"] as const) : ([ROOT, "count", storeCode] as const);
}

/**
 * 「按分店汇总」查询键。故意挂在 count 前缀下：现有各处（同步其它分店、仓库改价通知、列表页）
 * 在改价后都会失效 priceUpdateCountQueryKey()，这样汇总（角标、锁定、提醒）自动一起刷新。
 */
export function priceUpdateSummaryQueryKey() {
  return [ROOT, "count", "summary"] as const;
}
