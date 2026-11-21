/**
 * BLE 扫码枪在设置页上的展示逻辑（纯函数，不依赖 React，便于单测）。
 * 文案 key 都在 settings.json 的 bleScanner.* 下。
 */
import type { RowTone } from "@/modules/settings/ui";
import type { BleScannerConnectionState, BleScannerStatus } from "./ble-scanner-controller";
import { bleDeviceSuffix, scanValueTail, type SavedBleScanner } from "./ble-scanner-core";

type Translate = (key: string, options?: Record<string, unknown>) => string;

const STATE_BADGE: Record<BleScannerConnectionState, Readonly<{ key: string; tone: RowTone }>> = {
  ready: { key: "ready", tone: "success" },
  learning: { key: "learning", tone: "warning" },
  connecting: { key: "connecting", tone: "warning" },
  reconnecting: { key: "reconnecting", tone: "warning" },
  // 休眠是扫码枪的正常省电行为：灰色，不算异常。
  sleeping: { key: "sleeping", tone: "neutral" },
  disconnected: { key: "disconnected", tone: "neutral" },
  "bluetooth-off": { key: "bluetoothOff", tone: "danger" },
  unauthorized: { key: "unauthorized", tone: "danger" },
  unsupported: { key: "unsupported", tone: "neutral" },
  "not-paired": { key: "notPaired", tone: "neutral" },
};

/** 行尾状态点：需处理（重连超时 / 电量低）优先显示红色「需处理」。 */
export function resolveBleScannerBadge(status: BleScannerStatus): Readonly<{ key: string; tone: RowTone }> {
  if (status.attention && status.saved) {
    return { key: "attention", tone: "danger" };
  }
  return STATE_BADGE[status.state];
}

/** 列表 / 标题里的名字：有备注名用备注名，否则「广播名 · 尾号」（同型号广播名都一样）。 */
export function bleScannerDisplayName(saved: SavedBleScanner, unnamedLabel: string): string {
  if (saved.alias) return saved.alias;
  return `${saved.name || unnamedLabel} · ${bleDeviceSuffix(saved.peripheralId)}`;
}

/** 设置首页那一行的副标题：未配对 / 名字 · 休眠提示或电量 · 最近一次扫码尾 6 位。 */
export function buildBleScannerRowSubtitle(status: BleScannerStatus, t: Translate): string {
  const saved = status.saved;
  if (!saved) return t("bleScanner.rowNotPaired");
  const parts = [bleScannerDisplayName(saved, t("bleScanner.unnamed"))];
  if (status.state === "sleeping") {
    parts.push(t("bleScanner.sleepingHint"));
  } else if (status.state === "reconnecting" && status.attention) {
    parts.push(t("bleScanner.reconnectingTooLong"));
  } else if (status.batteryLevel !== null) {
    parts.push(t("bleScanner.batteryShort", { level: status.batteryLevel }));
  }
  if (status.lastScanValue) {
    parts.push(t("bleScanner.lastScanShort", { tail: scanValueTail(status.lastScanValue) }));
  }
  return parts.join(" · ");
}

/** 信号强度分档（dBm）：只用于界面文字。 */
export function describeBleRssi(rssi: number | null): "strong" | "good" | "weak" | "unknown" {
  if (rssi === null) return "unknown";
  if (rssi >= -60) return "strong";
  if (rssi >= -75) return "good";
  return "weak";
}

const SENSITIVE_PREFIXES = ["HBDEV1-"];

/** 测试页对敏感码（设备开通码）只显示前缀，避免截图外泄。 */
export function maskSensitiveScanValue(value: string): string {
  const prefix = SENSITIVE_PREFIXES.find((item) => value.toUpperCase().startsWith(item));
  return prefix ? `${prefix}••••••` : value;
}
