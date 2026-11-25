export const DEVICE_SYSTEM_IPAD = "iPadOS" as const;
// 安卓平板上运行的同一个大屏收银 App；后端按它与 iPad 同类处理，不能报 "Android"（那是手持端）。
export const DEVICE_SYSTEM_ANDROID_TABLET = "AndroidTablet" as const;
export type DeviceSystem =
  | typeof DEVICE_SYSTEM_IPAD
  | typeof DEVICE_SYSTEM_ANDROID_TABLET;

/**
 * 按打包平台解析上报给后端的设备系统。
 * process.env.EXPO_OS 由 babel-preset-expo 按平台内联；Node 单测里没有该值，按 iPad 处理。
 */
export function resolveDeviceSystem(
  expoOs: string | undefined = process.env.EXPO_OS,
): DeviceSystem {
  return expoOs === "android"
    ? DEVICE_SYSTEM_ANDROID_TABLET
    : DEVICE_SYSTEM_IPAD;
}

export const CURRENT_DEVICE_SYSTEM: DeviceSystem = resolveDeviceSystem();

export type DeviceIdentity = Readonly<{
  installationId: string;
  deviceCode: string;
  storeCode: string;
  authorizationCode: string;
  deviceSystem: DeviceSystem;
}>;

export type CashierSession = Readonly<{
  cashierId: string;
  cashierName: string;
  userBarcode: string;
  allowedStoreCodes: readonly string[];
  permissions: readonly string[];
  authorizationTicket: string;
}>;

export type DeviceRegistrationState =
  | "unregistered"
  | "registering"
  | "pending-approval"
  | "verifying"
  | "authorized"
  | "denied"
  | "disabled";
