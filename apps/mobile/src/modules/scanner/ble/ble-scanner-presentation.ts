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

/** 扫码页「蓝牙枪」状态按钮的展示（图标 / 颜色 / 文案 / 点按行为）。 */
export type BleScannerIndicator = Readonly<{
  icon: "bluetooth" | "bluetooth-connect" | "bluetooth-off" | "bluetooth-transfer";
  tone: RowTone;
  /** 状态文案 key（settings 命名空间）。 */
  labelKey: string;
  /** 已连接时显示的电量；其余状态为 null。 */
  batteryLevel: number | null;
  /** 点按行为：休眠 / 重连中 / 已断开直接重连，其余打开扫码枪设置。 */
  action: "reconnect" | "open-settings";
}>;

/** 与控制器的低电量阈值（BLE_LOW_BATTERY_LEVEL = 20）一致。 */
const LOW_BATTERY_LEVEL = 20;

/**
 * 扫码页的状态按钮。返回 null 表示不显示：本机没有配对过扫码枪（大多数人用键盘模式扫码枪，
 * 不该在扫码页多出一个灰图标），或本机不支持蓝牙。
 * 颜色与设置页一致：绿 = 已连上；黄 = 连接中 / 重连中；灰 = 休眠 / 已断开；红 = 蓝牙关闭 / 未授权 / 需处理。
 */
export function describeBleScannerIndicator(status: BleScannerStatus): BleScannerIndicator | null {
  if (!status.saved) return null;
  // 文案按连接状态取；「需处理」只用于重连太久（低电量时仍显示「已连接」并标红，不改文案）。
  const stuckReconnecting = status.state === "reconnecting" && status.attention;
  const labelKey = `bleScanner.state.${stuckReconnecting ? "attention" : STATE_BADGE[status.state].key}`;
  switch (status.state) {
    case "not-paired":
    case "unsupported":
      return null;
    case "ready": {
      const lowBattery = status.batteryLevel !== null && status.batteryLevel < LOW_BATTERY_LEVEL;
      return {
        icon: "bluetooth-connect",
        tone: lowBattery ? "danger" : "success",
        labelKey,
        batteryLevel: status.batteryLevel,
        action: "open-settings",
      };
    }
    case "learning":
      return { icon: "bluetooth-connect", tone: "warning", labelKey, batteryLevel: null, action: "open-settings" };
    case "connecting":
      return { icon: "bluetooth-transfer", tone: "warning", labelKey, batteryLevel: null, action: "open-settings" };
    case "reconnecting":
      return {
        icon: "bluetooth-transfer",
        tone: stuckReconnecting ? "danger" : "warning",
        labelKey,
        batteryLevel: null,
        action: "reconnect",
      };
    case "sleeping":
      return { icon: "bluetooth", tone: "neutral", labelKey, batteryLevel: null, action: "reconnect" };
    case "disconnected":
      return { icon: "bluetooth-off", tone: "neutral", labelKey, batteryLevel: null, action: "reconnect" };
    case "bluetooth-off":
    case "unauthorized":
      return { icon: "bluetooth-off", tone: "danger", labelKey, batteryLevel: null, action: "open-settings" };
  }
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
