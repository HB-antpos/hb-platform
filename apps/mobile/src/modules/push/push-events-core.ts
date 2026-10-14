// 收到 / 点击推送后的处理（依赖全部由外部注入）。不依赖 React Native。
//
// - 前台收到推送：不弹横幅（策略见 PUSH_FOREGROUND_BEHAVIOR，由适配层落实），只触发未读数刷新；
// - 点击推送：只有「已登录且会话就绪」才导航；
//     · 未登录（signed-out）→ 直接丢弃，不暂存、不落盘；
//     · 已登录但主界面还没就绪（busy，如启动恢复会话、菜单加载中）→ 仅在内存里暂存最近一次，就绪后再导航，超时作废；
// - 同一次点击可能同时被监听器与冷启动读取交付，按通知标识在短时间窗内去重（仅内存）；
// - 本文件只读推送的 type 与 data 用于选路由，不记录、不上报其内容。
import { resolvePushTarget, type PushTarget } from "./push-routing";
import type { PushResponse } from "./push-types";

/** signed-out：没有可用的个人账号会话；busy：已登录但主界面未就绪；ready：可以导航。 */
export type PushSessionPhase = "signed-out" | "busy" | "ready";

/** 由布局算出的两个布尔值得到会话阶段：个人账号会话才可能处理推送；主界面就绪才允许导航。 */
export function resolvePushSessionPhase({ eligible, ready }: { eligible: boolean; ready: boolean }): PushSessionPhase {
  if (!eligible) return "signed-out";
  return ready ? "ready" : "busy";
}

/** 暂存的点击超过这个时长还没等到就绪就作废（防止很久以后突然跳转）。 */
export const PUSH_PENDING_MAX_AGE_MS = 2 * 60 * 1000;
/** 冷启动读取「最近一次点击」的有效期：自进程启动起超过这个时长，说明该点击早已过时（例如登录页停留很久后才登录）。 */
export const PUSH_COLD_START_MAX_AGE_MS = 2 * 60 * 1000;
/** 同一通知标识在这个时间内再次交付视为重复（监听器与冷启动读取给出的是同一次点击）。 */
export const PUSH_DUPLICATE_WINDOW_MS = 30 * 1000;
const HANDLED_IDS_MAX = 32;

export interface PushEventsDeps {
  getPhase(): PushSessionPhase;
  /** 刷新未读数（命中未读数 + 通知收件箱）；只标记 / 重取，不读推送内容。 */
  refreshUnreadCounts(): void;
  navigate(target: PushTarget): void;
  /** 清掉原生侧「最近一次点击」，避免之后重新挂载时再次处理。 */
  clearLastResponse(): void;
  now(): number;
}

export function createPushEventController(deps: PushEventsDeps) {
  const handled: { id: string; atMs: number }[] = [];
  let pending: { target: PushTarget; atMs: number } | null = null;

  const alreadyHandled = (id: string | null): boolean => {
    if (!id) return false;
    const nowMs = deps.now();
    if (handled.some((entry) => entry.id === id && nowMs - entry.atMs <= PUSH_DUPLICATE_WINDOW_MS)) return true;
    handled.push({ id, atMs: nowMs });
    if (handled.length > HANDLED_IDS_MAX) handled.shift();
    return false;
  };

  const safe = (action: () => void) => {
    try {
      action();
    } catch {
      // 推送处理是锦上添花，任何异常都不能影响 App 本身
    }
  };

  /** 前台收到一条推送：已登录才刷新未读数。 */
  const onForegroundNotification = (): void => {
    if (deps.getPhase() === "signed-out") return;
    safe(() => deps.refreshUnreadCounts());
  };

  /** 用户点击了推送。 */
  const handleResponse = (response: PushResponse): void => {
    // 只处理「点击通知本身」；关闭 / 自定义动作不跳转。
    if (!response.isDefaultAction) return;
    const phase = deps.getPhase();
    if (phase === "signed-out") {
      // 未登录：丢弃，且清掉原生侧记录，防止之后登录时被当成新点击处理。
      safe(() => deps.clearLastResponse());
      return;
    }
    if (alreadyHandled(response.id)) return;
    const target = resolvePushTarget(response.type, response.data);
    safe(() => deps.clearLastResponse());
    if (phase === "busy") {
      pending = { target, atMs: deps.now() };
      return;
    }
    safe(() => deps.navigate(target));
  };

  /** 冷启动时读到的「最近一次点击」：自进程启动已超过有效期则视为过时，直接丢弃。 */
  const handleColdStartResponse = (response: PushResponse, msSinceProcessStart: number): void => {
    if (msSinceProcessStart > PUSH_COLD_START_MAX_AGE_MS) {
      safe(() => deps.clearLastResponse());
      return;
    }
    handleResponse(response);
  };

  /** 会话就绪时调用：把「已登录但当时未就绪」的那次点击补上。 */
  const flushPending = (): void => {
    const current = pending;
    if (!current || deps.getPhase() !== "ready") return;
    pending = null;
    if (deps.now() - current.atMs > PUSH_PENDING_MAX_AGE_MS) return;
    safe(() => deps.navigate(current.target));
  };

  /** 登出 / 会话结束：丢弃暂存与去重记录。 */
  const reset = (): void => {
    pending = null;
    handled.length = 0;
  };

  return { onForegroundNotification, handleResponse, handleColdStartResponse, flushPending, reset };
}

export type PushEventController = ReturnType<typeof createPushEventController>;
