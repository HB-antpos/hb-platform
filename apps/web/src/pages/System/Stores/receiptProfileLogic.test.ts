import assert from 'node:assert/strict'
import type {
  StoreReceiptProfileDevice,
  StoreReceiptProfileFields,
  StoreReceiptProfileStatusItem,
} from '../../../types/storeReceiptProfile'
import { RequestError } from '../../../utils/request'
import {
  RECEIPT_PROFILE_FIELD_KEYS,
  RECEIPT_PROFILE_PUBLISH_CONFLICT,
  ReceiptProfilePublishRequestError,
  applyStatusResponse,
  buildPublishPreview,
  buildPublishRequest,
  classifyPublishItem,
  collectPendingGuids,
  createStatusIssueTracker,
  diffReceiptProfile,
  formatHeartbeatAt,
  formatLocalMinute,
  formatPublishedAt,
  isReceiptProfileEndpointMissing,
  mergeSelectionWithLimit,
  normalizeReceiptField,
  parseInstant,
  parsePublishFailure,
  sortReceiptProfileDevices,
  summarizeDeviceApply,
} from './receiptProfileLogic'

// 示例数据一律用一眼就是假的名字与号码。
const fields = (overrides: Partial<StoreReceiptProfileFields> = {}): StoreReceiptProfileFields => ({
  brandName: 'Example Brand',
  storeName: '示例分店 A',
  address: '1 Example St',
  phone: '0200000000',
  abn: '11111111111',
  returnPolicy: '14 days',
  // 代金券使用说明 / 分期条款默认未定制（null＝收银端按内置默认文案打印）。
  voucherTerms: null,
  installmentTerms: null,
  ...overrides,
})

const statusItem = (overrides: Partial<StoreReceiptProfileStatusItem> = {}): StoreReceiptProfileStatusItem => ({
  storeGuid: 'guid-a',
  storeCode: '9001',
  storeName: '示例分店 A',
  status: 'synced',
  latestVersion: 3,
  publishedAtUtc: '2026-10-07T04:32:10',
  publishedBy: 'tester',
  current: fields(),
  latest: fields(),
  deviceTotal: 4,
  deviceApplied: 3,
  ...overrides,
})

const device = (overrides: Partial<StoreReceiptProfileDevice> = {}): StoreReceiptProfileDevice => ({
  deviceCode: 'POS_9001_0001',
  deviceSystem: 'Windows',
  clientKind: 'wpf',
  deviceStatus: 1,
  isOnline: true,
  lastHeartbeatAt: null,
  appliedVersion: 3,
  appliedAtUtc: null,
  upToDate: true,
  ...overrides,
})

// ── 字段口径：与后端一致，null 与空串/纯空白相同，其余区分大小写 ──
assert.equal(normalizeReceiptField(null), '')
assert.equal(normalizeReceiptField(undefined), '')
assert.equal(normalizeReceiptField('  x  '), 'x')
assert.deepEqual(
  [...RECEIPT_PROFILE_FIELD_KEYS],
  ['brandName', 'storeName', 'address', 'phone', 'abn', 'returnPolicy', 'voucherTerms', 'installmentTerms'],
  '8 个字段及顺序（与后端 StoreReceiptProfileFieldsDto 一致）',
)

const same = diffReceiptProfile(fields({ brandName: null, phone: '  ' }), fields({ brandName: '', phone: null }))
assert.equal(same.length, 8)
assert.ok(same.every((diff) => !diff.changed), 'null / 空串 / 纯空白视为相同')

const changed = diffReceiptProfile(fields({ address: '2 Example St', abn: 'abc' }), fields({ abn: 'ABC' }))
assert.deepEqual(changed.filter((diff) => diff.changed).map((diff) => diff.key), ['address', 'abn'], '只有真正不同的字段被标记，大小写敏感')
assert.equal(changed.find((diff) => diff.key === 'address')?.oldValue, '1 Example St')
assert.equal(changed.find((diff) => diff.key === 'address')?.newValue, '2 Example St')

const firstDiff = diffReceiptProfile(fields({ returnPolicy: ' ' }), null)
assert.ok(firstDiff.every((diff) => diff.oldValue === null), '首次下发没有旧值')
assert.deepEqual(
  firstDiff.filter((diff) => !diff.changed).map((diff) => diff.key),
  ['returnPolicy', 'voucherTerms', 'installmentTerms'],
  '首次下发只有空内容的字段不算「将下发」（两个说明字段未定制也是空内容）',
)

// ── 代金券使用说明 / 分期条款：新字段进入新旧对比，口径与退货政策相同 ──
{
  // 只有新字段变化：被标记为有差异，其余 6 个字段不受影响。
  const termsOnly = diffReceiptProfile(
    fields({ voucherTerms: 'Use at the issuing store only.\nNot redeemable for cash.' }),
    fields(),
  )
  assert.deepEqual(termsOnly.filter((diff) => diff.changed).map((diff) => diff.key), ['voucherTerms'])
  assert.equal(termsOnly.find((diff) => diff.key === 'voucherTerms')?.oldValue, null)

  // 清空定制（有内容 → null）也是实质修改。
  const cleared = diffReceiptProfile(fields({ installmentTerms: null }), fields({ installmentTerms: 'Order total: $50.00 minimum.' }))
  assert.deepEqual(cleared.filter((diff) => diff.changed).map((diff) => diff.key), ['installmentTerms'])

  // null / 空串 / 纯空白 / 首尾空白视为相同；换行风格与大小写是实质差异。
  const noise = diffReceiptProfile(
    fields({ voucherTerms: '  Line A\nLine B \n', installmentTerms: '   ' }),
    fields({ voucherTerms: 'Line A\nLine B', installmentTerms: null }),
  )
  assert.ok(noise.every((diff) => !diff.changed), '新字段同样忽略首尾空白，空白等同未定制')
  const style = diffReceiptProfile(fields({ voucherTerms: 'Line A\r\nLine B' }), fields({ voucherTerms: 'Line A\nLine B' }))
  assert.deepEqual(style.filter((diff) => diff.changed).map((diff) => diff.key), ['voucherTerms'])

  // 旧服务端 / 旧快照响应里没有这两个字段（undefined）：按未定制处理，不抛错、不误报差异。
  const legacy = { ...fields(), voucherTerms: undefined, installmentTerms: undefined } as unknown as StoreReceiptProfileFields
  assert.ok(diffReceiptProfile(legacy, fields()).every((diff) => !diff.changed))
  assert.ok(diffReceiptProfile(fields(), legacy).every((diff) => !diff.changed))

  // 预览里仅新字段变化的分店：分类 changed，高亮键只含新字段（分类以服务端 status 为准）。
  const termsPreview = buildPublishPreview([
    statusItem({ storeGuid: 'g-terms', status: 'pending', current: fields({ installmentTerms: 'Order total: $50.00 minimum.' }) }),
  ])
  assert.equal(termsPreview.rows[0].kind, 'changed')
  assert.deepEqual(termsPreview.rows[0].changedKeys, ['installmentTerms'])
  assert.equal(termsPreview.publishCount, 1)
}

// ── 下发预览分类：是否有变化以服务端 status 为准 ──
assert.equal(classifyPublishItem(statusItem({ status: 'never', latestVersion: 0, latest: null })), 'first')
assert.equal(classifyPublishItem(statusItem({ status: 'pending' })), 'changed')
assert.equal(classifyPublishItem(statusItem({ status: 'synced' })), 'unchanged')
assert.equal(
  classifyPublishItem(statusItem({ status: 'synced', current: fields({ address: 'moved' }) })),
  'unchanged',
  '分类不重复实现后端口径：服务端说一致就是一致',
)
assert.equal(classifyPublishItem(statusItem({ status: 'pending', latest: null })), 'first', '缺少最新快照按首次下发处理，避免对比时取空')

const preview = buildPublishPreview([
  statusItem({ storeGuid: 'g1', status: 'never', latestVersion: 0, latest: null }),
  statusItem({ storeGuid: 'g2', status: 'synced' }),
  statusItem({ storeGuid: 'g3', status: 'pending', current: fields({ phone: '0299999999' }) }),
])
assert.deepEqual(preview.rows.map((row) => row.item.storeGuid), ['g1', 'g2', 'g3'], '保持请求顺序')
assert.equal(preview.firstCount, 1)
assert.equal(preview.changedCount, 1)
assert.equal(preview.unchangedCount, 1)
assert.equal(preview.publishCount, 2)
assert.deepEqual(preview.publishRows.map((row) => row.item.storeGuid), ['g1', 'g3'])
assert.deepEqual(preview.unchangedRows.map((row) => row.item.storeGuid), ['g2'])
assert.deepEqual(preview.rows[2].changedKeys, ['phone'], '有修改的店只高亮有差异的字段')

const nothing = buildPublishPreview([statusItem({ storeGuid: 'g1' }), statusItem({ storeGuid: 'g2' })])
assert.equal(nothing.publishCount, 0, '全部无变化时没有可下发的店（确认按钮据此禁用）')
assert.equal(buildPublishPreview([]).publishCount, 0)

// ── 下发请求：1–100 家、不重复、非空白 ──
const guids = (count: number) => Array.from({ length: count }, (_, index) => `guid-${index}`)
assert.deepEqual(buildPublishRequest(['a', 'b']), { storeGuids: ['a', 'b'] })
assert.equal(buildPublishRequest(guids(100)).storeGuids.length, 100)
for (const bad of [[], guids(101), ['a', 'a'], ['a', '  ']]) {
  assert.throws(
    () => buildPublishRequest(bad),
    (error) => error instanceof ReceiptProfilePublishRequestError && error.code === 'INVALID_TARGETS',
    `非法目标应被拒绝: ${JSON.stringify(bad).slice(0, 40)}`,
  )
}
const sourceGuids = ['a']
assert.notEqual(buildPublishRequest(sourceGuids).storeGuids, sourceGuids, '返回副本，避免外部数组被后续修改')

// ── 状态请求竞态：同一分店最近一次发起的请求胜出 ──
{
  const tracker = createStatusIssueTracker()
  const pageToken = tracker.begin(['g1', 'g2'])
  const publishToken = tracker.begin(['g1'])
  assert.equal(tracker.isCurrent(pageToken, 'g1'), false, 'g1 已被更新的请求接管')
  assert.equal(tracker.isCurrent(pageToken, 'g2'), true, 'g2 仍归第一个请求')
  assert.equal(tracker.isCurrent(publishToken, 'g1'), true)

  const newer = statusItem({ storeGuid: 'g1', status: 'synced', latestVersion: 4 })
  const older = statusItem({ storeGuid: 'g1', status: 'pending', latestVersion: 3 })
  const g2 = statusItem({ storeGuid: 'g2' })

  // 较新的响应（下发后的刷新）先到，较旧的整页响应后到：g1 不能被旧值覆盖，g2 照常写入。
  let table = applyStatusResponse({}, ['g1'], [newer], (guid) => tracker.isCurrent(publishToken, guid))
  table = applyStatusResponse(table, ['g1', 'g2'], [older, g2], (guid) => tracker.isCurrent(pageToken, guid))
  assert.equal(table.g1.latestVersion, 4, '旧请求的响应不能覆盖新请求的结果')
  assert.equal(table.g2.storeGuid, 'g2')
}
{
  const previous = { g1: statusItem({ storeGuid: 'g1' }), g2: statusItem({ storeGuid: 'g2' }) }
  const removed = applyStatusResponse(previous, ['g1', 'g2'], [previous.g2], () => true)
  assert.ok(!('g1' in removed), '请求过但响应里没有的分店（已删除）从状态表移除')
  assert.ok('g2' in removed)
  assert.ok('g1' in previous, '不修改传入的状态表')
  const unchanged = applyStatusResponse(previous, ['g3'], [], () => true)
  assert.equal(unchanged, previous, '没有任何变化时返回原对象，避免无谓重渲染')
  assert.equal(applyStatusResponse(previous, ['g1'], [], () => false), previous, '全部被新请求接管时不写入')
}

// ── 本页「有未下发修改」与一键勾选 ──
{
  const rows = [{ storeGUID: 'g1' }, { storeGUID: 'g2' }, { storeGUID: 'g3' }, { storeGUID: 'g4' }]
  const table = {
    g1: statusItem({ storeGuid: 'g1', status: 'pending' }),
    g2: statusItem({ storeGuid: 'g2', status: 'synced' }),
    g4: statusItem({ storeGuid: 'g4', status: 'pending' }),
  }
  assert.deepEqual(collectPendingGuids(rows, table), ['g1', 'g4'], '只统计当前页行里 pending 的分店，没有状态的行忽略')
  assert.deepEqual(collectPendingGuids([], table), [])

  const merged = mergeSelectionWithLimit(['x', 'g1'], ['g1', 'g4'])
  assert.deepEqual(merged.next, ['x', 'g1', 'g4'], '追加并去重，保持已有顺序')
  assert.equal(merged.added, 1)
  assert.equal(merged.truncated, false)

  const full = mergeSelectionWithLimit(guids(99), ['new-1', 'new-2', 'new-3'])
  assert.equal(full.next.length, 100, '总数不超过单批上限 100')
  assert.equal(full.added, 1)
  assert.equal(full.truncated, true)
  assert.equal(mergeSelectionWithLimit(guids(100), ['guid-5']).truncated, false, '追加的全是已选中的，不算被截断')
}

// ── 设备应用汇总 ──
assert.deepEqual(summarizeDeviceApply({ latestVersion: 0, deviceApplied: 0, deviceTotal: 3 }), { tone: 'none', applied: 0, total: 3 }, '从未下发不展示台数')
assert.deepEqual(summarizeDeviceApply({ latestVersion: 2, deviceApplied: 0, deviceTotal: 0 }), { tone: 'noDevices', applied: 0, total: 0 })
assert.deepEqual(summarizeDeviceApply({ latestVersion: 2, deviceApplied: 3, deviceTotal: 3 }), { tone: 'allApplied', applied: 3, total: 3 })
assert.deepEqual(summarizeDeviceApply({ latestVersion: 2, deviceApplied: 1, deviceTotal: 3 }), { tone: 'partial', applied: 1, total: 3 })
assert.deepEqual(summarizeDeviceApply({ latestVersion: 2, deviceApplied: 0, deviceTotal: 3 }), { tone: 'partial', applied: 0, total: 3 }, '一台都没应用也属于未全部应用')
assert.equal(summarizeDeviceApply({ latestVersion: 2, deviceApplied: 5, deviceTotal: 3 }).tone, 'allApplied', '已应用数超过总数（设备中途停用）仍视为全部应用')

{
  const sorted = sortReceiptProfileDevices([
    device({ deviceCode: 'POS_9001_0003', upToDate: true }),
    device({ deviceCode: 'POS_9001_0002', upToDate: false, appliedVersion: 2 }),
    device({ deviceCode: 'POS_9001_0001', upToDate: true }),
    device({ deviceCode: 'POS_9001_0004', upToDate: false, appliedVersion: null }),
  ])
  assert.deepEqual(
    sorted.map((entry) => entry.deviceCode),
    ['POS_9001_0002', 'POS_9001_0004', 'POS_9001_0001', 'POS_9001_0003'],
    '未更新到最新的排前面，同组按设备编号',
  )
}

// ── 下发失败解析 ──
{
  const conflict = parsePublishFailure(new RequestError('x', 409, { success: false, errorCode: RECEIPT_PROFILE_PUBLISH_CONFLICT, message: '冲突' }))
  assert.equal(conflict.kind, 'conflict')
  assert.equal(parsePublishFailure(new RequestError('x', 409, {})).kind, 'conflict', '409 但响应体缺少错误码时仍按并发冲突处理')

  const rejected = parsePublishFailure(new RequestError('下发失败', 400, {
    success: false,
    errorCode: 'RECEIPT_PROFILE_NOT_PUBLISHABLE',
    message: '部分分店不可下发',
    details: [
      { storeGuid: 'g1', storeCode: '9001', errorCode: 'STORE_NAME_REQUIRED', message: '分店名称为空' },
      { storeGuid: 'g2', errorCode: 'STORE_INACTIVE' },
      'garbage',
      null,
    ],
  }))
  assert.equal(rejected.kind, 'rejected')
  assert.equal(rejected.errorCode, 'RECEIPT_PROFILE_NOT_PUBLISHABLE')
  assert.equal(rejected.message, '部分分店不可下发')
  assert.deepEqual(rejected.items, [
    { storeGuid: 'g1', storeCode: '9001', errorCode: 'STORE_NAME_REQUIRED', message: '分店名称为空' },
    { storeGuid: 'g2', storeCode: undefined, errorCode: 'STORE_INACTIVE', message: 'STORE_INACTIVE' },
  ], '逐店原因取 details；缺 message 时退回错误码；非对象条目忽略')

  const bareBadRequest = parsePublishFailure(new RequestError('请求无效', 400, { success: false, errorCode: 'INVALID_RECEIPT_PROFILE_REQUEST', message: '请求无效' }))
  assert.equal(bareBadRequest.kind, 'rejected')
  assert.equal(bareBadRequest.items.length, 0)

  const serverError = parsePublishFailure(new RequestError('请求失败 (500)', 500, undefined))
  assert.equal(serverError.kind, 'other')
  assert.equal(serverError.message, '请求失败 (500)')
  const networkError = parsePublishFailure(new TypeError('Failed to fetch'))
  assert.equal(networkError.kind, 'other')
  assert.equal(networkError.message, 'Failed to fetch')
  assert.equal(parsePublishFailure('boom').kind, 'other')

  // HTTP 200 + success=false 经 unwrapApiData 抛出的 RequestError 同样带 payload。
  const softFailure = parsePublishFailure(new RequestError('X: y', 200, { success: false, errorCode: RECEIPT_PROFILE_PUBLISH_CONFLICT }))
  assert.equal(softFailure.kind, 'conflict')
}

assert.equal(isReceiptProfileEndpointMissing(new RequestError('x', 404)), true, '404 = 后端接口未部署')
assert.equal(isReceiptProfileEndpointMissing(new RequestError('x', 500)), false)
assert.equal(isReceiptProfileEndpointMissing(new Error('x')), false)

// ── 时间：PublishedAtUtc 无时区标记按 UTC 解析；心跳按本地解析 ──
{
  const utcMs = Date.UTC(2026, 9, 7, 4, 32, 10)
  assert.equal(parseInstant('2026-10-07T04:32:10', true), utcMs, '无时区标记 + assumeUtc → UTC')
  assert.equal(parseInstant('2026-10-07T04:32:10Z', true), utcMs)
  assert.equal(parseInstant('2026-10-07T04:32:10.1234567', true), utcMs + 123, '带 7 位小数的 .NET 时间戳')
  assert.equal(parseInstant('2026-10-07T14:32:10+10:00', true), utcMs, '带偏移量的时间不再追加 Z')
  assert.equal(parseInstant('', true), null)
  assert.equal(parseInstant(null, true), null)
  assert.equal(parseInstant('not a date', true), null)

  const published = formatPublishedAt('2026-10-07T04:32:10')
  assert.ok(published)
  assert.equal(published.utc, '2026-10-07 04:32')
  assert.equal(published.local, formatLocalMinute(utcMs), '展示为浏览器本地时间')
  assert.match(published.local, /^\d{4}-\d{2}-\d{2} \d{2}:\d{2}$/)
  assert.equal(formatPublishedAt(null), null)
  assert.equal(formatPublishedAt('garbage'), null)

  // 心跳没有时区标记时与「设备管理」页一致按本地时间解析，格式化后墙钟不变（与测试机时区无关）。
  assert.equal(formatHeartbeatAt('2026-10-07T14:32:10'), '2026-10-07 14:32')
  assert.equal(formatHeartbeatAt(null), null)
  assert.equal(formatHeartbeatAt('garbage'), null)
}

console.log('receiptProfileLogic.test: ok')
