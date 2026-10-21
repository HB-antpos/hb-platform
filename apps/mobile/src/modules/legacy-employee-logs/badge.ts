// 「员工操作日志」导航角标（红 / 橙圆点，不带数字）的共享约定：缓存键、取数条件、级别判定。
// - 红点 = 近 7 天、当前账号可见分店内存在「未核查的危险异常」；橙点 = 存在待核查的异常；红优先于橙；都没有则不显示。
// - 取数在 app/(shell)/_layout.tsx（与「人脸命中未读」同一模式：前台 60 秒轮询、回前台立即刷新）。
// - 缓存键带账号编号，切换账号不会读到上一个账号的结果。
import type { LogBadgeLevel, LogBadgeSummary } from "./types";

/** 角标统计的回看天数（后端 badge-summary 的 days 参数）。 */
export const LOG_BADGE_DAYS = 7;

export const legacyLogBadgeQueryKey = (userGuid: string | null | undefined) =>
  ["legacyEmployeeLogs", userGuid ?? null, "badgeSummary"] as const;

/** 多个来源（老收银 / 新收银）的摘要相加。 */
export function mergeLogBadgeSummaries(summaries: readonly LogBadgeSummary[]): LogBadgeSummary {
  return summaries.reduce<LogBadgeSummary>(
    (total, item) => ({
      dangerPending: total.dangerPending + item.dangerPending,
      abnormalPending: total.abnormalPending + item.abnormalPending,
    }),
    { dangerPending: 0, abnormalPending: 0 },
  );
}

/** 摘要转圆点级别：危险待核查 > 0 → 红；否则异常待核查 > 0 → 橙；否则 null。缺失 / 非法值按 0。 */
export function resolveLogBadgeLevel(summary: Partial<LogBadgeSummary> | null | undefined): LogBadgeLevel | null {
  const positive = (value: unknown) => typeof value === "number" && Number.isFinite(value) && value > 0;
  if (positive(summary?.dangerPending)) return "danger";
  if (positive(summary?.abnormalPending)) return "warning";
  return null;
}

/** 多处同时有圆点时取最高级别（红 > 橙）；用于折叠分组头、底部页签。 */
export function highestLogBadgeLevel(levels: readonly (LogBadgeLevel | null | undefined)[]): LogBadgeLevel | null {
  if (levels.includes("danger")) return "danger";
  return levels.includes("warning") ? "warning" : null;
}

export interface LogBadgeQueryGate {
  isAuthenticated: boolean;
  /** iOS 审核离线会话：不请求。 */
  isReviewSession: boolean;
  /** 设备模式没有用户账号，不取。 */
  isDeviceMode: boolean;
  navigationReady: boolean;
  /** legacy-employee-logs 是否在菜单可见集合里。 */
  logsRouteVisible: boolean;
  /** LegacyEmployeeLogs.View。 */
  canViewLegacy: boolean;
  /** Permissions.PosTerminal.Audit.View。 */
  canViewPos: boolean;
}

/** 是否要取角标摘要：导航就绪 + 日志入口可见 + 任一来源有查看权限（账号会话、非审核）。 */
export function shouldFetchLogBadge(gate: LogBadgeQueryGate): boolean {
  return (
    gate.navigationReady &&
    gate.isAuthenticated &&
    !gate.isReviewSession &&
    !gate.isDeviceMode &&
    gate.logsRouteVisible &&
    (gate.canViewLegacy || gate.canViewPos)
  );
}

/** 圆点颜色：红 = 危险待核查，橙 = 异常待核查（与设计口径一致，不带数字）。 */
export const LOG_BADGE_COLORS: Record<LogBadgeLevel, string> = {
  danger: "#D92D20",
  warning: "#F79009",
};
