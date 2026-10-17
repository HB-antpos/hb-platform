// 切换列表的展示逻辑（纯函数，便于单测）：角色颜色、「上次使用」文案、行状态与副标题。

import { describeEntry, type VaultEntry } from "./vault";

/** 角色标签颜色：管理员紫、店长蓝，其余中性——切换前一眼分清，防止切错账号。 */
export type RoleTone = "admin" | "manager" | "neutral";

export function resolveRoleTone(roleNames: readonly string[] | undefined): RoleTone {
  const names = (roleNames ?? []).map((name) => name.trim().toLowerCase());
  if (names.some((name) => name.includes("admin"))) return "admin";
  if (names.some((name) => name.includes("manager") && !name.includes("warehouse"))) return "manager";
  return "neutral";
}

const DAY_MS = 24 * 60 * 60 * 1000;

function startOfLocalDay(timestamp: number): number {
  const date = new Date(timestamp);
  return new Date(date.getFullYear(), date.getMonth(), date.getDate()).getTime();
}

/** 按本地自然日计算「几天前」：今天 0、昨天 1。 */
export function daysSince(timestamp: number, now: number): number {
  return Math.max(0, Math.round((startOfLocalDay(now) - startOfLocalDay(timestamp)) / DAY_MS));
}

export type WhenLabel = { key: "today" | "yesterday" } | { key: "daysAgo"; days: number };

export function describeWhen(timestamp: number, now: number): WhenLabel {
  const days = daysSince(timestamp, now);
  if (days === 0) return { key: "today" };
  if (days === 1) return { key: "yesterday" };
  return { key: "daysAgo", days };
}

export type RowStatus = "ok" | "needsRelogin";

export interface RowDescription {
  status: RowStatus;
  /** 副标题：可用 → 「上次使用 …」；需重登 → 原因。 */
  meta: { key: "lastUsed"; when: WhenLabel } | { key: "expiredMeta"; days: number } | { key: "rejectedMeta" } | { key: "otherHostMeta" };
  /** 重新登录页的副标题用哪一条（过期 / 失效）。 */
  reloginReason: "expired" | "rejected";
}

export function describeRow(entry: VaultEntry, now: number, currentApiHost: string): RowDescription {
  const state = describeEntry(entry, now, currentApiHost);
  switch (state) {
    case "ok":
      return { status: "ok", meta: { key: "lastUsed", when: describeWhen(entry.lastUsedAt, now) }, reloginReason: "rejected" };
    case "expired":
      return {
        status: "needsRelogin",
        meta: { key: "expiredMeta", days: daysSince(entry.tokenIssuedAt, now) },
        reloginReason: "expired",
      };
    case "otherHost":
      return { status: "needsRelogin", meta: { key: "otherHostMeta" }, reloginReason: "rejected" };
    default:
      return { status: "needsRelogin", meta: { key: "rejectedMeta" }, reloginReason: "rejected" };
  }
}
