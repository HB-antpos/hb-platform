import type { AppDownloadsApp, AppDownloadsChannel } from "./logic";
import type { AppDownloadPlatform } from "./types";

/**
 * 版本发布中心的终端：投放总览 + 三类 App + WPF 收银端。
 * 取值与 Web 版本发布中心的 ?view= 对齐（overview / mobile / ipad / handheld / wpf）。
 */
export type ReleaseCenterTerminal = "overview" | AppDownloadsApp | "wpf";

/** WPF 只有一种安装包，生产 / 预览两个通道就是它的两条发布线路。 */
export type WpfChannel = "production" | "preview";

/** 线路状态：已激活 / 待处理（有新版本未投放、灰度中、策略绑定失效）/ 未启用 / 读取失败。 */
export type ReleaseLaneStatus = "active" | "pending" | "inactive" | "error";

/** 从总览点进某条线路时的落点。 */
export type ReleaseCenterTarget =
  | {
      terminal: AppDownloadsApp;
      channel: AppDownloadsChannel;
      platform?: AppDownloadPlatform;
    }
  | { terminal: "wpf"; wpfChannel: WpfChannel };

export const RELEASE_CENTER_TERMINALS: readonly ReleaseCenterTerminal[] = [
  "overview",
  "mobile",
  "ipad",
  "handheld",
  "wpf",
];

/** 深链 ?view= 解析；未知或缺省一律回到总览，旧 WPF 入口用 view=wpf 落到 WPF 终端。 */
export function parseReleaseCenterView(value: unknown): ReleaseCenterTerminal {
  const raw = Array.isArray(value) ? value[0] : value;
  return typeof raw === "string" &&
    (RELEASE_CENTER_TERMINALS as readonly string[]).includes(raw)
    ? (raw as ReleaseCenterTerminal)
    : "overview";
}

/** 深链 ?channel= 解析（仅 WPF 终端使用）：只认 preview，其余都按生产处理。 */
export function parseWpfChannel(value: unknown): WpfChannel {
  const raw = Array.isArray(value) ? value[0] : value;
  return raw === "preview" ? "preview" : "production";
}

/** 终端状态取该终端下最需要关注的线路：读取失败 > 待处理 > 已激活 > 未启用。 */
export function worstLaneStatus(
  statuses: readonly ReleaseLaneStatus[],
): ReleaseLaneStatus | null {
  const rank: Record<ReleaseLaneStatus, number> = {
    error: 3,
    pending: 2,
    active: 1,
    inactive: 0,
  };
  let worst: ReleaseLaneStatus | null = null;
  for (const status of statuses) {
    if (worst === null || rank[status] > rank[worst]) worst = status;
  }
  return worst;
}
