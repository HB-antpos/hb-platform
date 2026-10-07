import type { AppVersionDistributionItem } from '../../../types/deviceRegistration'

/** 点选版本分布某一行后，对设备明细施加的精确筛选。 */
export interface AppVersionSelection {
  deviceSystem?: string
  appVersion?: string
  appBuildVersion?: string
}

/** 「1.0.7 (55)」形式的版本文案；版本号与构建号都缺失时返回 undefined，由调用方显示"未上报"。 */
export function formatAppPackageVersion(appVersion?: string, appBuildVersion?: string): string | undefined {
  if (appVersion && appBuildVersion) {
    return `${appVersion} (${appBuildVersion})`
  }
  return appVersion || appBuildVersion || undefined
}

/**
 * 把一行分布转成明细筛选条件。
 * 从未上报版本的设备没有可匹配的精确值（服务端空值不参与筛选），所以这一行不可点选。
 */
export function toAppVersionSelection(item: AppVersionDistributionItem): AppVersionSelection | undefined {
  if (!item.appVersion && !item.appBuildVersion) {
    return undefined
  }
  // 关键逻辑：系统一并带上。两端版本线不同步，同一个版本号可能同时出现在 iOS 和 Android 两行。
  return {
    deviceSystem: item.deviceSystem,
    appVersion: item.appVersion,
    appBuildVersion: item.appBuildVersion,
  }
}

export function isAppVersionSelected(item: AppVersionDistributionItem, selection?: AppVersionSelection): boolean {
  return Boolean(
    selection &&
      selection.deviceSystem === item.deviceSystem &&
      selection.appVersion === item.appVersion &&
      selection.appBuildVersion === item.appBuildVersion,
  )
}

/** 版本下拉的一项：value 是筛选条件的稳定键，label 是纯文本（供搜索与回显）。 */
export interface AppVersionOption {
  value: string
  label: string
  count: number
  selection: AppVersionSelection
}

export function getAppVersionSelectionKey(selection: AppVersionSelection): string {
  return [selection.deviceSystem ?? '', selection.appVersion ?? '', selection.appBuildVersion ?? ''].join('|')
}

function buildAppVersionOption(selection: AppVersionSelection, count: number): AppVersionOption {
  const label = [selection.deviceSystem, formatAppPackageVersion(selection.appVersion, selection.appBuildVersion)]
    .filter(Boolean)
    .join(' ')
  return { value: getAppVersionSelectionKey(selection), label, count, selection }
}

/**
 * 版本下拉的选项：沿用分布的顺序（系统、版本从新到旧），从未上报版本的行没有可筛选的值，不进下拉。
 * 当前选中的版本即使不在新范围的分布里（如换了分店）也保留（台数 0），避免下拉回显成裸键。
 */
export function buildAppVersionOptions(
  items: AppVersionDistributionItem[],
  selected?: AppVersionSelection,
): AppVersionOption[] {
  const options: AppVersionOption[] = []
  for (const item of items) {
    const selection = toAppVersionSelection(item)
    if (selection) {
      options.push(buildAppVersionOption(selection, item.total))
    }
  }

  if (selected && !options.some((option) => option.value === getAppVersionSelectionKey(selected))) {
    options.push(buildAppVersionOption(selected, 0))
  }
  return options
}

/** 占比（0–100，保留一位小数）；总数为 0 时返回 0，避免除零出现 NaN。 */
export function getAppVersionShare(count: number, total: number): number {
  if (total <= 0 || count <= 0) {
    return 0
  }
  return Math.min(100, Math.round((count / total) * 1000) / 10)
}
