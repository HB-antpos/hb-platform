import assert from 'node:assert/strict'
import type { PosSettingDefinition, PosSettingStoreSummary } from '../../../types/posSettings'
import { RequestError } from '../../../utils/request'
import {
  applyProgress,
  buildDraft,
  buildSaveValues,
  chooseEffective,
  choosePosSettingsErrorText,
  countOverriddenStores,
  describeChangeScope,
  deviceStatusTone,
  deviceVisibleDefinitions,
  diffDraft,
  filterStores,
  formatDateTime,
  globalEffective,
  layerEffective,
  reportedState,
  resolvePosSettingsError,
  storeOverrideEntries,
  triOf,
  triToValue,
} from './logic'

const def = (key: string, clientKinds: string[], defaultWhenUnset: boolean | null = null, isNew = false): PosSettingDefinition => ({
  key,
  clientKinds,
  defaultWhenUnset,
  isNew,
})
const definitions = [
  def('cashMovement.enabled', ['wpf', 'ipad', 'handheld'], false, true),
  def('returns.rescan', ['wpf']),
  def('payment.voucher', ['wpf', 'ipad']),
]

// ---------------------------------------------------------------------------
// 三态：没出现 = 未设置 / 跟随；保存时 inherit = null（清除）
// ---------------------------------------------------------------------------

assert.equal(triOf(true), 'on')
assert.equal(triOf(false), 'off')
assert.equal(triOf(undefined), 'inherit')
assert.equal(triOf(null), 'inherit')
assert.equal(triToValue('on'), true)
assert.equal(triToValue('off'), false)
assert.equal(triToValue('inherit'), null)

// 草稿只含目录里的项：目录之外的旧键不展示也不修改；没出现的项是 inherit，不是 off。
assert.deepEqual(buildDraft(definitions, { 'returns.rescan': false, 'legacy.gone': true }), {
  'cashMovement.enabled': 'inherit',
  'returns.rescan': 'off',
  'payment.voucher': 'inherit',
})

// 变更：只列改动的项，顺序与目录一致；「关 → 未设置」也是一条改动（清除），不能被忽略。
const original = buildDraft(definitions, { 'returns.rescan': false, 'payment.voucher': true })
assert.deepEqual(diffDraft(definitions, original, original), [])
const edited = { ...original, 'cashMovement.enabled': 'on' as const, 'returns.rescan': 'inherit' as const }
assert.deepEqual(diffDraft(definitions, original, edited), [
  { key: 'cashMovement.enabled', from: 'inherit', to: 'on' },
  { key: 'returns.rescan', from: 'off', to: 'inherit' },
])
// 保存请求只带改动的项：设置 true / false，清除 null；没改的项不出现（服务端保持不变）。
assert.deepEqual(buildSaveValues(diffDraft(definitions, original, edited)), {
  'cashMovement.enabled': true,
  'returns.rescan': null,
})

// ---------------------------------------------------------------------------
// 生效值：设备 → 门店 → 全局 → 缺省 → 本机
// ---------------------------------------------------------------------------

const cash = definitions[0]
const returns = definitions[1]
assert.deepEqual(globalEffective(cash, {}), { value: false, source: 'default' }, '新功能全局没设置按缺省关')
assert.deepEqual(globalEffective(returns, {}), { value: null, source: 'none' }, '没缺省的项全局没设置 = 沿用本机，不是关')
assert.deepEqual(globalEffective(returns, { 'returns.rescan': true }), { value: true, source: 'global' })
assert.deepEqual(globalEffective(cash, { 'cashMovement.enabled': false }), { value: false, source: 'global' }, '全局显式关与缺省关来源不同')

const fromGlobal = globalEffective(returns, { 'returns.rescan': false })
assert.deepEqual(layerEffective('inherit', 'store', fromGlobal), { value: false, source: 'global' })
assert.deepEqual(layerEffective('on', 'store', fromGlobal), { value: true, source: 'store' })
assert.deepEqual(layerEffective('off', 'device', globalEffective(returns, {})), { value: false, source: 'device' })
// 门店跟随、全局未设置：沿用本机。
assert.deepEqual(layerEffective('inherit', 'store', globalEffective(returns, {})), { value: null, source: 'none' })

// 没改的项以服务端为准；改了的项用本地推算。
const server = { value: true, source: 'store' }
const computed = { value: false, source: 'global' }
assert.deepEqual(chooseEffective('on', 'on', server, computed), { value: true, source: 'store' })
assert.deepEqual(chooseEffective('off', 'on', server, computed), computed)
assert.deepEqual(chooseEffective('inherit', 'inherit', undefined, computed), computed, '服务端没给值时退回本地推算')
assert.deepEqual(chooseEffective('on', 'on', { value: null, source: 'none' }, computed), { value: null, source: 'none' })

// ---------------------------------------------------------------------------
// 设备回报与「待同步」
// ---------------------------------------------------------------------------

const eff = (value: boolean | null, source = 'store') => ({ value, source })
assert.deepEqual(reportedState(eff(true), { 'returns.rescan': true }, 'returns.rescan'), { state: 'synced', value: true })
assert.deepEqual(reportedState(eff(true), { 'returns.rescan': false }, 'returns.rescan'), { state: 'pending', value: false })
// 回报过别的项但没回报这一项，且应生效有值：待同步。
assert.deepEqual(reportedState(eff(true), { 'payment.voucher': true }, 'returns.rescan'), { state: 'pending', value: undefined })
// 设备从没回报过任何值：不逐项标待同步。
assert.deepEqual(reportedState(eff(true), {}, 'returns.rescan'), { state: 'unreported', value: undefined })
// 没有下发值（沿用本机）：没有可比对的「应生效」，不标待同步，但回报的值照常显示。
assert.deepEqual(reportedState(eff(null, 'none'), { 'returns.rescan': true }, 'returns.rescan'), { state: 'native', value: true })
assert.deepEqual(reportedState(undefined, {}, 'returns.rescan'), { state: 'native', value: undefined })

assert.equal(deviceStatusTone('applied'), 'green')
assert.equal(deviceStatusTone('pending'), 'orange')
assert.equal(deviceStatusTone('never'), 'gray')
assert.equal(deviceStatusTone('something-else'), 'gray', '未知状态按尚未同步处理')

// 设备抽屉只列该端适用的项；服务端已给出生效值的项（即使目录没标适用）也保留。
assert.deepEqual(deviceVisibleDefinitions(definitions, 'ipad', {}).map((item) => item.key), ['cashMovement.enabled', 'payment.voucher'])
assert.deepEqual(deviceVisibleDefinitions(definitions, 'other', {}).map((item) => item.key), [])
assert.deepEqual(
  deviceVisibleDefinitions(definitions, 'handheld', { 'returns.rescan': eff(true) }).map((item) => item.key),
  ['cashMovement.enabled', 'returns.rescan'],
)

// ---------------------------------------------------------------------------
// 门店列表
// ---------------------------------------------------------------------------

const store = (storeCode: string, storeName: string, overrides: Record<string, boolean> = {}, deviceTotal = 0, deviceApplied = 0): PosSettingStoreSummary => ({
  storeCode,
  storeName,
  overrides,
  deviceOverrideCount: 0,
  deviceTotal,
  deviceApplied,
})
const stores = [
  store('ORI', 'Orion', { 'cashMovement.enabled': true }, 3, 3),
  store('MD2', 'Mount Druitt', {}, 2, 1),
  store('LVP', 'Liverpool'),
]
assert.deepEqual(filterStores(stores, '  ori ').map((item) => item.storeCode), ['ORI'])
assert.deepEqual(filterStores(stores, 'druitt').map((item) => item.storeCode), ['MD2'])
assert.deepEqual(filterStores(stores, 'lvp').map((item) => item.storeCode), ['LVP'], '按门店代码搜索')
assert.equal(filterStores(stores, '').length, 3)
assert.equal(countOverriddenStores(stores), 1)

// 覆盖项按目录顺序，目录里没有的键排在后面（界面回退显示键名）。
assert.deepEqual(
  storeOverrideEntries(definitions, { 'payment.voucher': false, 'legacy.gone': true, 'cashMovement.enabled': true }),
  [
    { key: 'cashMovement.enabled', value: true },
    { key: 'payment.voucher', value: false },
    { key: 'legacy.gone', value: true },
  ],
)

assert.deepEqual(applyProgress(stores[0]), { kind: 'all', applied: 3, total: 3 })
assert.deepEqual(applyProgress(stores[1]), { kind: 'partial', applied: 1, total: 2 })
assert.deepEqual(applyProgress(stores[2]), { kind: 'none' }, '没有设备不显示进度')
assert.deepEqual(applyProgress({ deviceApplied: 5, deviceTotal: 3 }), { kind: 'all', applied: 3, total: 3 }, '已应用数不超过总数')

// ---------------------------------------------------------------------------
// 修改记录范围
// ---------------------------------------------------------------------------

assert.equal(describeChangeScope({ scope: 'global', scopeKey: '*' }, {}).scope, 'global')
assert.deepEqual(describeChangeScope({ scope: 'store', scopeKey: 'ORI' }, { ORI: 'Orion' }), { scope: 'store', label: 'Orion (ORI)' })
assert.deepEqual(describeChangeScope({ scope: 'store', scopeKey: 'XYZ' }, {}), { scope: 'store', label: 'XYZ' }, '门店不在可见列表时只显示代码')
assert.deepEqual(describeChangeScope({ scope: 'device', scopeKey: 'ORI-01' }, {}), { scope: 'device', label: 'ORI-01' })

// ---------------------------------------------------------------------------
// 时间：UTC → 悉尼
// ---------------------------------------------------------------------------

assert.equal(formatDateTime('2026-11-14T00:02:00Z'), '2026-11-14 11:02', '11 月悉尼夏令时 UTC+11')
assert.equal(formatDateTime('2026-11-14T00:02:00'), '2026-11-14 11:02', '不带时区后缀按 UTC 解释')
assert.equal(formatDateTime('2026-07-01T00:00:00Z'), '2026-07-01 10:00', '7 月悉尼标准时间 UTC+10')
assert.equal(formatDateTime('2026-11-13T13:30:00+00:00'), '2026-11-14 00:30', '跨日')
assert.equal(formatDateTime(null), '—')
assert.equal(formatDateTime('not-a-date'), '—')

// ---------------------------------------------------------------------------
// 错误：403 显示后端 message
// ---------------------------------------------------------------------------

const abort = Object.assign(new Error('aborted'), { name: 'AbortError' })
assert.deepEqual(resolvePosSettingsError(abort), { kind: 'abort' })

const forbidden = resolvePosSettingsError(new RequestError('只有管理员可以修改全局设置', 403, { success: false, message: '只有管理员可以修改全局设置' }))
if (forbidden.kind !== 'error') throw new Error('应为 error')
assert.equal(forbidden.key, 'posSettings.errors.forbidden')
assert.equal(forbidden.status, 403)
// 403 无论界面语言都显示后端 message；英文界面的其他错误用本地英文文案。
assert.equal(choosePosSettingsErrorText(forbidden, 'en', (key) => `T:${key}`), '只有管理员可以修改全局设置')
assert.equal(choosePosSettingsErrorText(forbidden, 'zh-CN', (key) => `T:${key}`), '只有管理员可以修改全局设置')

const serverError = resolvePosSettingsError(new RequestError('boom', 500, { message: '内部错误' }))
if (serverError.kind !== 'error') throw new Error('应为 error')
assert.equal(serverError.key, 'posSettings.errors.server')
assert.equal(choosePosSettingsErrorText(serverError, 'en', (key) => `T:${key}`), 'T:posSettings.errors.server')
assert.equal(choosePosSettingsErrorText(serverError, 'zh-CN', (key) => `T:${key}`), '内部错误')

const noMessage = resolvePosSettingsError(new RequestError('x', 403, {}))
if (noMessage.kind !== 'error') throw new Error('应为 error')
assert.equal(choosePosSettingsErrorText(noMessage, 'zh-CN', (key) => `T:${key}`), 'T:posSettings.errors.forbidden', '后端没给 message 时用本地文案')

const network = resolvePosSettingsError(new TypeError('Failed to fetch'))
if (network.kind !== 'error') throw new Error('应为 error')
assert.equal(network.key, 'posSettings.errors.network')
assert.equal(resolvePosSettingsError('weird').kind, 'error')

console.log('posSettings logic.test: ok')
