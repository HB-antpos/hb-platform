import type { AppVersionDistributionItem } from '../../../types/deviceRegistration'

import {
  buildAppVersionOptions,
  formatAppPackageVersion,
  getAppVersionSelectionKey,
  getAppVersionShare,
  isAppVersionSelected,
  toAppVersionSelection,
} from './appVersionDistributionLogic'

function assertEqual<T>(actual: T, expected: T, message: string) {
  if (actual !== expected) {
    throw new Error(`${message}: expected ${String(expected)}, got ${String(actual)}`)
  }
}

function item(overrides: Partial<AppVersionDistributionItem>): AppVersionDistributionItem {
  return { total: 1, online: 0, ota: 0, embedded: 0, ...overrides }
}

assertEqual(formatAppPackageVersion('1.0.7', '55'), '1.0.7 (55)', '版本号与构建号都有时带括号')
assertEqual(formatAppPackageVersion('1.0.7', undefined), '1.0.7', '只有版本号')
assertEqual(formatAppPackageVersion(undefined, '55'), '55', '只有构建号')
assertEqual(formatAppPackageVersion(undefined, undefined), undefined, '都缺失返回 undefined')
assertEqual(formatAppPackageVersion('', ''), undefined, '空串视为缺失')

const ios = item({ deviceSystem: 'iOS', appVersion: '1.0.7', appBuildVersion: '58' })
const android = item({ deviceSystem: 'Android', appVersion: '1.0.7', appBuildVersion: '58' })
const unreported = item({ deviceSystem: 'Android' })

const selection = toAppVersionSelection(ios)
assertEqual(selection?.deviceSystem, 'iOS', '选择条件带上系统')
assertEqual(selection?.appVersion, '1.0.7', '选择条件带上版本号')
assertEqual(selection?.appBuildVersion, '58', '选择条件带上构建号')
assertEqual(toAppVersionSelection(unreported), undefined, '未上报版本的行不可点选')

assertEqual(isAppVersionSelected(ios, selection), true, '选中行自身判定为选中')
assertEqual(isAppVersionSelected(android, selection), false, '版本号相同但系统不同的行不算选中')
assertEqual(isAppVersionSelected(ios, undefined), false, '没有选择时都不算选中')
assertEqual(
  isAppVersionSelected(item({ deviceSystem: 'iOS', appVersion: '1.0.7', appBuildVersion: '57' }), selection),
  false,
  '构建号不同不算选中',
)

const options = buildAppVersionOptions([ios, android, unreported])
assertEqual(options.length, 2, '未上报版本的行不进版本下拉')
assertEqual(options[0]?.label, 'iOS 1.0.7 (58)', '下拉文案是「系统 版本 (构建号)」纯文本')
assertEqual(options[0]?.count, 1, '下拉带该版本的台数')
assertEqual(options[0]?.value, 'iOS|1.0.7|58', '下拉值是系统|版本|构建号')
assertEqual(options[0]?.value === options[1]?.value, false, '同版本号不同系统必须是两个选项')

const outside = { deviceSystem: 'Android', appVersion: '1.0.9', appBuildVersion: '50' }
const withOutside = buildAppVersionOptions([ios], outside)
assertEqual(withOutside.length, 2, '当前选中的版本不在新范围里时仍保留')
assertEqual(withOutside[1]?.count, 0, '保留项台数为 0')
assertEqual(buildAppVersionOptions([ios], selection).length, 1, '选中项已在分布里时不重复追加')
assertEqual(
  getAppVersionSelectionKey({ appVersion: '2.0.0' }),
  '|2.0.0|',
  '缺失系统与构建号时键里留空，仍可区分',
)

assertEqual(getAppVersionShare(1, 3), 33.3, '占比保留一位小数')
assertEqual(getAppVersionShare(3, 3), 100, '全部设备占比 100')
assertEqual(getAppVersionShare(0, 3), 0, '零台占比 0')
assertEqual(getAppVersionShare(5, 0), 0, '总数为 0 不出现 NaN')

console.log('appVersionDistributionLogic.test: ok')
