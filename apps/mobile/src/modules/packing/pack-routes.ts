import type { Href } from "expo-router";

// 打包各页的路由（纯函数，不依赖打印 / 原生模块，方便单测）。

/** 打包入口（首页）。 */
export const PACKING_HOME = "/(shell)/packing" as Href;
export const PACKING_LOCATE = "/(shell)/packing/locate" as Href;
export const PACKING_RECEIVE = "/(shell)/packing/receive" as Href;

/** 批量创建空箱标页 / 最近批次列表页。 */
export const PACKING_BATCH_NEW = "/(shell)/packing/batch/new" as Href;
export const PACKING_BATCH_LIST = "/(shell)/packing/batch" as Href;

/**
 * 一批空箱标的连续打印页。autoPrint：刚点「创建并打印」进来，页面加载后自动开始打印（copies 是每个箱号的份数）；
 * duplicate：创建请求被识别为重放（这一批之前已建好）。
 */
export function batchRoute(batchGuid: string, options?: { autoPrint?: boolean; copies?: number; duplicate?: boolean }) {
  const base = `/packing/batch/${encodeURIComponent(batchGuid)}`;
  const query: string[] = [];
  if (options?.autoPrint) query.push("autoprint=1");
  if (options?.autoPrint && options.copies && options.copies > 1) query.push(`copies=${Math.trunc(options.copies)}`);
  if (options?.duplicate) query.push("duplicate=1");
  return (query.length ? `${base}?${query.join("&")}` : base) as Href;
}

/** activated：刚扫空箱标启用后进入，装箱页顶部提示「已启用这张空箱标」。 */
export function cartonRoute(cartonGuid: string, options?: { activated?: boolean }) {
  const base = `/packing/carton/${encodeURIComponent(cartonGuid)}`;
  return (options?.activated ? `${base}?activated=1` : base) as Href;
}

export function palletRoute(palletGuid: string) {
  return `/packing/pallet/${encodeURIComponent(palletGuid)}` as Href;
}

/** 签收页；带 code 时进入后直接按这个码查找（首页扫到发给本店的箱 / 托盘）。 */
export function receiveRoute(code?: string | null) {
  return (code
    ? { pathname: "/(shell)/packing/receive", params: { code } }
    : PACKING_RECEIVE) as unknown as Href;
}
