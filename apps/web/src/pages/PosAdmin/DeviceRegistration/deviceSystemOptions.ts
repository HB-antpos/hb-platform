/** 安卓平板上运行的大屏收银 App；业务上等同 iPadOS，不是手持端。 */
export const ANDROID_TABLET_DEVICE_SYSTEM = 'AndroidTablet'

export const REGISTERED_DEVICE_SYSTEM_OPTIONS = ['Windows', 'iPadOS', 'AndroidTablet', 'Other'] as const

export const APP_DEVICE_SYSTEM_OPTIONS = ['Android', 'iOS', 'iPadOS', 'Windows', 'Mac'] as const

export const EDITABLE_DEVICE_SYSTEM_OPTIONS = ['Android', 'iOS', 'iPadOS', 'AndroidTablet', 'Windows', 'Mac'] as const

const LEGACY_IOS_CANONICALIZATION_OPTIONS = ['iOS', 'iPadOS'] as const

export function supportsTransactionGate(
  deviceSystem?: string | null,
  deviceType?: string | null,
): boolean {
  const normalizedSystem = deviceSystem?.trim().toLowerCase()
  return (
    deviceType?.trim().toLowerCase() === 'pos' &&
    (normalizedSystem === 'ipados' ||
      normalizedSystem === 'androidtablet' ||
      normalizedSystem === 'ios' ||
      normalizedSystem === 'android')
  )
}

export function isEnabledLegacyIosPos(
  status: number,
  currentSystem: string,
  deviceType: string,
): boolean {
  return (
    status === 1 &&
    currentSystem.trim().toLowerCase() === 'ios' &&
    deviceType.trim().toLowerCase() === 'pos'
  )
}

export function canEditRegisteredDeviceSystem(
  status: number,
  currentSystem: string,
  deviceType: string,
): boolean {
  return status === -1 || isEnabledLegacyIosPos(status, currentSystem, deviceType)
}

export function getRegisteredDeviceSystemEditOptions(
  status: number,
  currentSystem: string,
  deviceType: string,
): readonly string[] {
  if (isEnabledLegacyIosPos(status, currentSystem, deviceType)) {
    // 关键逻辑：已获批设备只能把等价旧值 iOS 单向规范化为 iPadOS。
    return LEGACY_IOS_CANONICALIZATION_OPTIONS
  }

  return EDITABLE_DEVICE_SYSTEM_OPTIONS
}

/**
 * 设备系统展示名：AndroidTablet 显示「安卓平板 / Android tablet」，其余值本身就是展示名，原样返回。
 */
export function formatDeviceSystemLabel(
  deviceSystem: string | null | undefined,
  t: (key: string) => string,
): string {
  const value = deviceSystem?.trim() ?? ''
  return value === ANDROID_TABLET_DEVICE_SYSTEM ? t('posAdmin.devices.deviceSystemAndroidTablet') : value
}
