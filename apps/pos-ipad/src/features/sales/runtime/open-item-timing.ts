/**
 * 「加入无码商品」手点链路的分段计时（诊断用，旁路）。
 *
 * 背景：真机反馈点「加入无码商品」到弹窗关闭偏慢，但代码里没有它比扫码加购多出来的重步骤，
 * 需要设备上的实测数据才能定位是授权、目录查询、购物车写入还是审计写入。
 *
 * 设计要点：
 * - 展示层的 manualProductAddInFlight 保证同一时刻最多一次手点加购，所以用「单次在途」单例，
 *   不需要给每一层传 id；未 begin 时 mark / finish 全部是空操作，扫码等其他入口不受影响。
 * - 只记录毫秒数与结果码，不含价格、商品、订单、员工信息。
 * - 任何异常（含 sink）都在这里吞掉，诊断旁路不能改变加购结果。
 */

import {
  beginNativeCallTrace,
  endNativeCallTrace,
} from "@/core/performance/native-call-trace";

export type OpenItemTimingPhase = "authorized" | "catalog" | "cart" | "audited";

export type OpenItemTimingReport = Readonly<{
  outcome: "success" | "failure";
  /** 失败时的固定错误码（presenter 的 errorCode），成功为 null。 */
  errorCode: string | null;
  /** 点击 → 授权通过并进入操作回调（含授权检查、可能的主管授权弹窗）。 */
  authorizeMs: number | null;
  /** 本地目录精确查询 OPENITEM。 */
  catalogMs: number | null;
  /** 写入内存购物车。 */
  cartMs: number | null;
  /** 购物车写入完成 → runCartMutation 返回（审计写入 + 授权收尾）。 */
  auditMs: number | null;
  /** 工作流返回 → presenter 把结果合并进状态（弹窗此时才会关闭）。 */
  presenterMs: number | null;
  totalMs: number;
  /**
   * 计时期间每次原生 SQLite 调用的时间线（`类型:动词:表名@起点偏移ms+时长ms`，以 " | " 分隔）。
   * 调用时长是纯原生执行；相邻调用之间的空隙是排队或 JS 线程忙。无调用时为 null。
   */
  nativeCalls: string | null;
}>;

export type OpenItemTimingSink = (report: OpenItemTimingReport) => void;

type ActiveTiming = {
  readonly startedAt: number;
  readonly marks: Partial<Record<OpenItemTimingPhase, number>>;
};

let sink: OpenItemTimingSink | null = null;
let active: ActiveTiming | null = null;

/** 注册上报出口；返回的函数只在 sink 仍是自己时才注销，避免卸载顺序错乱误清别人。 */
export function setOpenItemTimingSink(next: OpenItemTimingSink | null): () => void {
  sink = next;
  return () => {
    if (sink === next) sink = null;
  };
}

export function beginOpenItemTiming(now: () => number = monotonicNow): void {
  active = { startedAt: now(), marks: {} };
  beginNativeCallTrace(now);
}

export function markOpenItemTiming(
  phase: OpenItemTimingPhase,
  now: () => number = monotonicNow,
): void {
  if (!active) return;
  active.marks[phase] = now();
}

export function finishOpenItemTiming(
  outcome: "success" | "failure",
  errorCode: string | null = null,
  now: () => number = monotonicNow,
): void {
  const timing = active;
  active = null;
  if (!timing) return;
  const nativeCalls = endNativeCallTrace();
  try {
    const finishedAt = now();
    const { authorized, catalog, cart, audited } = timing.marks;
    // 某一段缺标记（如授权被拒、目录查询失败）就不算该段，避免把「没跑到」当成 0ms。
    const span = (from: number | undefined, to: number | undefined) =>
      from === undefined || to === undefined ? null : round(to - from);
    sink?.({
      outcome,
      errorCode: sanitizeErrorCode(errorCode),
      authorizeMs: span(timing.startedAt, authorized),
      catalogMs: span(authorized, catalog),
      cartMs: span(catalog, cart),
      auditMs: span(cart, audited),
      presenterMs: span(audited, finishedAt),
      totalMs: round(finishedAt - timing.startedAt),
      nativeCalls,
    });
  } catch {
    // 诊断旁路失败不能反向影响加购。
  }
}

function round(value: number): number {
  return Math.max(0, Math.round(value));
}

/** 只放行固定格式的错误码（如 product-add-failed / NEW_TRANSACTIONS_DISABLED），避免把含业务信息的文本写进日志。 */
function sanitizeErrorCode(code: string | null): string | null {
  return code && /^[A-Za-z][A-Za-z0-9_-]{1,63}$/u.test(code) ? code : null;
}

function monotonicNow(): number {
  return typeof performance === "undefined" ? Date.now() : performance.now();
}
