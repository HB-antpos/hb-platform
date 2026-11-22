import type {
  BleScannerConnectionState,
  BleScannerStatus,
} from "@/core/peripherals/scanner/ble/ble-scanner-controller";

/**
 * 顶栏只关心的 BLE 扫码枪状态投影。
 * 控制器会随 RSSI、最近扫码时间等频繁推送状态，这里只保留会影响顶栏显示的三个字段，
 * 配合 {@link isSameBleScannerIndicator} 避免无意义的重渲染。
 */
export type BleScannerIndicatorSnapshot = Readonly<{
  state: BleScannerConnectionState;
  batteryLevel: number | null;
  /** 需要人处理（重连太久未连上 / 电量低）。休眠不算。 */
  attention: boolean;
}>;

export type BleScannerIndicatorTone = "good" | "warning" | "neutral" | "danger";

export type BleScannerIndicatorView = Readonly<{
  icon:
    | "bluetooth"
    | "bluetooth-connect"
    | "bluetooth-off"
    | "bluetooth-settings"
    | "bluetooth-transfer";
  tone: BleScannerIndicatorTone;
  /** i18n 键：`status.bleScanner.<state>`。 */
  labelKey: string;
  /** 已连接且读到电量时附带的百分比，用于在文案后补充。 */
  batteryLevel: number | null;
}>;

/** 与控制器的低电量阈值（BLE_LOW_BATTERY_LEVEL = 20）保持一致。 */
const LOW_BATTERY_LEVEL = 20;

export function toBleScannerIndicatorSnapshot(
  status: BleScannerStatus,
): BleScannerIndicatorSnapshot {
  return {
    state: status.state,
    batteryLevel: status.batteryLevel,
    attention: status.attention,
  };
}

export function isSameBleScannerIndicator(
  left: BleScannerIndicatorSnapshot | null,
  right: BleScannerIndicatorSnapshot | null,
): boolean {
  if (left === right) return true;
  if (!left || !right) return false;
  return (
    left.state === right.state &&
    left.batteryLevel === right.batteryLevel &&
    left.attention === right.attention
  );
}

/**
 * 把连接状态映射成顶栏图标、颜色与文案键。
 * 返回 null 表示不显示（本机没有蓝牙能力，显示一个永远灰着的图标只会添乱）。
 *
 * 颜色约定：绿 = 已连上可直接扫；黄 = 进行中或需留意；红 = 蓝牙关闭 / 无权限 / 需人处理；
 * 灰 = 未配对、已断开、休眠（休眠是扫码枪正常省电，按扳机即可唤醒，不算异常）。
 */
export function describeBleScannerIndicator(
  snapshot: BleScannerIndicatorSnapshot,
): BleScannerIndicatorView | null {
  const labelKey = `status.bleScanner.${snapshot.state}`;
  switch (snapshot.state) {
    case "unsupported":
      return null;
    case "ready": {
      const lowBattery =
        snapshot.batteryLevel !== null && snapshot.batteryLevel <= LOW_BATTERY_LEVEL;
      return {
        icon: "bluetooth-connect",
        tone: lowBattery ? "warning" : "good",
        labelKey,
        batteryLevel: snapshot.batteryLevel,
      };
    }
    case "learning":
      // 已连上但还没学会数据通道：需要扫一次码，所以用黄色提醒。
      return { icon: "bluetooth-connect", tone: "warning", labelKey, batteryLevel: null };
    case "connecting":
      return { icon: "bluetooth-transfer", tone: "warning", labelKey, batteryLevel: null };
    case "reconnecting":
      return {
        icon: "bluetooth-transfer",
        tone: snapshot.attention ? "danger" : "warning",
        labelKey,
        batteryLevel: null,
      };
    case "sleeping":
      return { icon: "bluetooth", tone: "neutral", labelKey, batteryLevel: null };
    case "disconnected":
      return { icon: "bluetooth-off", tone: "neutral", labelKey, batteryLevel: null };
    case "not-paired":
      return { icon: "bluetooth-settings", tone: "neutral", labelKey, batteryLevel: null };
    case "bluetooth-off":
    case "unauthorized":
      return { icon: "bluetooth-off", tone: "danger", labelKey, batteryLevel: null };
  }
}

/**
 * 顶栏图标点一下就能重连的状态：休眠、重连中、已断开。
 * 扫码枪休眠省电后自动重连靠前台唤醒扫描，这里给店员一个不用进设置页的兜底入口；
 * 其余状态（已连接、连接中、未配对、蓝牙关闭等）点了重连也没有意义，保持只读。
 */
export function isBleScannerReconnectable(snapshot: BleScannerIndicatorSnapshot): boolean {
  return (
    snapshot.state === "sleeping" ||
    snapshot.state === "reconnecting" ||
    snapshot.state === "disconnected"
  );
}
