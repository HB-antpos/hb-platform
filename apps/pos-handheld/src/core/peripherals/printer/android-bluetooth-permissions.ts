/** Android 12（API 31）起蓝牙扫描改用"附近的设备"权限，之前依赖精确定位权限。 */
export const ANDROID_NEARBY_DEVICES_MIN_API = 31;

const ANDROID_PERMISSION = {
  fineLocation: "android.permission.ACCESS_FINE_LOCATION",
  bluetoothScan: "android.permission.BLUETOOTH_SCAN",
  bluetoothConnect: "android.permission.BLUETOOTH_CONNECT",
} as const;

export type AndroidBluetoothPermissionPlatform = Readonly<{
  os: string;
  version: number | string;
}>;

export type AndroidBluetoothPermissionRequester = (
  permissions: readonly string[],
) => Promise<unknown>;

/**
 * 与原生模块 requiredPermissions() 保持同一分界：API 31+ 请求"附近的设备"，
 * API 29/30 请求精确定位（系统设置里显示为"位置信息"，没有单独的蓝牙项）。
 * 非 Android 平台返回空列表。
 */
export function androidBluetoothScanPermissions(
  platform: AndroidBluetoothPermissionPlatform,
): readonly string[] {
  if (platform.os !== "android") {
    return [];
  }
  return androidApiLevel(platform) >= ANDROID_NEARBY_DEVICES_MIN_API
    ? [ANDROID_PERMISSION.bluetoothScan, ANDROID_PERMISSION.bluetoothConnect]
    : [ANDROID_PERMISSION.fineLocation];
}

/**
 * 扫描前主动弹出系统授权框。结果不在这里判定：拒绝或"不再询问"时继续交给原生扫描，
 * 由原生模块统一返回 PRINTER_BLUETOOTH_PERMISSION_REQUIRED，界面再引导去系统设置。
 * 请求本身失败（例如 Activity 不可用）同样不阻断扫描。
 */
export async function requestAndroidBluetoothScanPermissions(
  platform: AndroidBluetoothPermissionPlatform,
  request: AndroidBluetoothPermissionRequester,
): Promise<void> {
  const permissions = androidBluetoothScanPermissions(platform);
  if (permissions.length === 0) {
    return;
  }
  try {
    await request(permissions);
  } catch {
    // 授权框无法弹出时保持原有行为，最终以原生权限检查为准。
  }
}

/** Platform.Version 在 Android 上是数字，这里兼容字符串并把无法解析的值视为旧系统。 */
export function androidApiLevel(
  platform: AndroidBluetoothPermissionPlatform,
): number {
  const level =
    typeof platform.version === "number"
      ? platform.version
      : Number.parseInt(platform.version, 10);
  return Number.isFinite(level) ? level : 0;
}
