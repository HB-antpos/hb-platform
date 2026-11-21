import type { ScanSource } from "@/core/contracts/scanner";

export type PendingSalesScan = Readonly<{
  value: string;
  source: ScanSource;
  atMs: number;
}>;

/** 交接码的有效期：只覆盖「付款成功页 → 收银页」这一次页面切换，过期的码不再自动加购。 */
export const PENDING_SALES_SCAN_MAX_AGE_MS = 15_000;

let pending: PendingSalesScan | null = null;

/**
 * 付款成功页收到商品码时，把码交给即将聚焦的收银页。
 * 模块级单例而不是路由参数：收银页一直挂在导航栈底部，不会重新挂载，参数传不过去。
 * 只保留最近一个码：切页期间扫到第二个码，以最新的为准。
 */
export function offerPendingSalesScan(
  value: string,
  source: ScanSource,
  nowMs: number = Date.now(),
): void {
  pending = { value, source, atMs: nowMs };
}

/** 取走并清空交接码；超过有效期的码直接丢弃（返回 null）。 */
export function takePendingSalesScan(
  nowMs: number = Date.now(),
): PendingSalesScan | null {
  const taken = pending;
  pending = null;
  if (!taken) return null;
  return nowMs - taken.atMs <= PENDING_SALES_SCAN_MAX_AGE_MS ? taken : null;
}
