import assert from 'node:assert/strict'

import { normalizeMyobPayCategories, normalizeMyobPreviewRow, normalizeMyobTimesheetPreview } from '../../../services/myobService'
import type { MyobPayCategories, MyobPreviewRow, MyobTimesheetPreview } from '../../../types/myob'
import {
  BLOCKER_LABEL_KEYS,
  CSV_BOM,
  HINT_CODE_LABEL_KEYS,
  KIND_LABEL_KEYS,
  PAY_CATEGORY_KEYS,
  PAY_CATEGORY_OPTIONAL_KEYS,
  PAY_CATEGORY_ROWS,
  PAY_CATEGORY_TOTAL,
  PUSHED_LABEL_KEYS,
  WEEKDAY_LABEL_KEYS,
  buildDayHeader,
  buildDrawerSubtitle,
  buildExplanation,
  buildPayCategoryMappingRequest,
  buildPeriodOptions,
  buildPreviewCsv,
  buildPreviewCsvFileName,
  buildReadinessCards,
  canPushPreview,
  countMappedValues,
  describeCrossShare,
  diffDays,
  escapeCsvField,
  formatFixedHours,
  formatHours,
  formatHoursCell,
  formatPeriodRange,
  formatRatio,
  getBasePayCategoryUid,
  getBlockerLabelKey,
  getCompanyMappingBadge,
  getHintLabelKey,
  getKindLabelKey,
  getPayCategoryRowState,
  getPayCategorySaveState,
  getPayModeTag,
  getPeriodWeeks,
  getPushedLabelKey,
  getRowClassName,
  getRowNotes,
  getRowStatusView,
  getWeekdayIndex,
  groupHints,
  guardCsvFormula,
  hasPayCategoryEdits,
  isOptionalPayCategory,
  isPayCategoryMissing,
  parseIsoDate,
  reconcilePayCategoryEdits,
  resolvePayCategoryValues,
  setPayCategoryEdit,
  shortCompanyNames,
  splitHintMessage,
  stripCheckPrefix,
} from './myobTimesheetLogic'

// ───────────── 工资类别映射 ─────────────

function categories(overrides: Record<string, unknown> = {}): MyobPayCategories {
  return normalizeMyobPayCategories({
    connectionGuid: 'c1',
    connectionName: 'Hot Bargain Investment Pty Ltd',
    categories: [
      { uid: 'base', name: 'Base Hourly', wageType: 'Hourly' },
      { uid: 'sat', name: 'Saturday Hourly', wageType: 'Hourly' },
      { uid: 'sun', name: 'Sunday Hourly', wageType: 'Hourly' },
      { uid: 'pers', name: 'Personal Leave Pay', wageType: 'Hourly' },
      { uid: 'hol', name: 'Holiday Leave Pay', wageType: 'Hourly' },
      { uid: 'other', name: 'Other', wageType: 'Salary' },
    ],
    mapping: {},
    suggestions: {},
    ...overrides,
  })
}

assert.deepEqual(
  [...PAY_CATEGORY_KEYS],
  ['weekday', 'saturday', 'sunday', 'saturdayCasual', 'sundayCasual', 'personalLeave', 'holidayLeave'],
  '7 个键：全职 / 兼职的周六周日 + 临时工的周六周日（可选）',
)
assert.deepEqual(PAY_CATEGORY_ROWS.map((row) => row.key), [...PAY_CATEGORY_KEYS], '页面 7 行与类别键一一对应、顺序一致')
assert.deepEqual(
  PAY_CATEGORY_ROWS.filter((row) => row.optional).map((row) => row.key),
  ['saturdayCasual', 'sundayCasual'],
  '只有临时工周末两行是可选的',
)
assert.equal(PAY_CATEGORY_TOTAL, 5, '必填项数仍是 5：临时工两项不计入')
assert.deepEqual([...PAY_CATEGORY_OPTIONAL_KEYS], ['saturdayCasual', 'sundayCasual'])
assert.equal(isOptionalPayCategory('saturdayCasual'), true)
assert.equal(isOptionalPayCategory('saturday'), false)

// 全新公司：什么都没保存、也没有建议 → 7 项都「未设置」，不可保存，还差 5 项必填
const fresh = categories()
assert.equal(PAY_CATEGORY_KEYS.every((key) => getPayCategoryRowState(fresh, {}, key) === 'unset'), true)
assert.deepEqual(getPayCategorySaveState(fresh, {}), { remaining: 5, changed: false, canSave: false })
assert.equal(buildPayCategoryMappingRequest(fresh, {}), null)
assert.equal(hasPayCategoryEdits({}), false)

// 有建议：预填并标「按名称相近推荐」；有 1 项没建议 → 该项未设置，还差 1 项
const suggested = categories({
  mapping: { weekdayUid: 'base' },
  suggestions: { saturdayUid: 'sat', sundayUid: 'sun', personalLeaveUid: 'pers' },
})
assert.equal(getPayCategoryRowState(suggested, {}, 'weekday'), 'set')
assert.equal(getPayCategoryRowState(suggested, {}, 'saturday'), 'suggested')
assert.equal(getPayCategoryRowState(suggested, {}, 'holidayLeave'), 'unset')
assert.equal(getBasePayCategoryUid(suggested, 'saturday'), 'sat', '建议预填为基线值')
assert.deepEqual(getPayCategorySaveState(suggested, {}), { remaining: 1, changed: true, canSave: false })
assert.equal(hasPayCategoryEdits({}), false, '只靠推荐预填、没人改过，不算「未保存改动」（离开页面不拦）')

// 选好最后一项 → 可保存；请求体带全 5 项（含预填的建议）
const picked = setPayCategoryEdit(suggested, {}, 'holidayLeave', 'hol')
assert.deepEqual(picked, { holidayLeave: 'hol' })
assert.equal(getPayCategoryRowState(suggested, picked, 'holidayLeave'), 'edited')
assert.equal(hasPayCategoryEdits(picked), true)
assert.deepEqual(getPayCategorySaveState(suggested, picked), { remaining: 0, changed: true, canSave: true })
assert.deepEqual(buildPayCategoryMappingRequest(suggested, picked), {
  weekdayUid: 'base',
  saturdayUid: 'sat',
  sundayUid: 'sun',
  saturdayCasualUid: null,
  sundayCasualUid: null,
  personalLeaveUid: 'pers',
  holidayLeaveUid: 'hol',
})

// 改回与基线一致 → 草稿被删掉，不会误报「有未保存改动」
assert.deepEqual(setPayCategoryEdit(suggested, picked, 'holidayLeave', null), {})
assert.deepEqual(setPayCategoryEdit(suggested, { saturdayUid: 'x' } as never, 'saturday', 'sat'), { saturdayUid: 'x' }, '键名写错不影响其他键')
const changedSat = setPayCategoryEdit(suggested, {}, 'saturday', 'other')
assert.deepEqual(changedSat, { saturday: 'other' })
assert.deepEqual(setPayCategoryEdit(suggested, changedSat, 'saturday', 'sat'), {}, '改回推荐值 → 草稿清空')

// 已完整：5 项都已保存且有效 → 无差异不可保存；改一项后可保存
const complete = categories({
  mapping: { weekdayUid: 'base', saturdayUid: 'sat', sundayUid: 'sun', personalLeaveUid: 'pers', holidayLeaveUid: 'hol' },
  mappedCount: 5,
  complete: true,
})
assert.deepEqual(getPayCategorySaveState(complete, {}), { remaining: 0, changed: false, canSave: false })
assert.equal(
  PAY_CATEGORY_KEYS.filter((key) => !isOptionalPayCategory(key)).every((key) => getPayCategoryRowState(complete, {}, key) === 'set'),
  true,
)
assert.equal(getPayCategoryRowState(complete, {}, 'saturdayCasual'), 'unset', '必填齐全后，没设的临时工项仍是「未设置」（可选，不拦保存）')
const changed = setPayCategoryEdit(complete, {}, 'sunday', 'other')
assert.deepEqual(getPayCategorySaveState(complete, changed), { remaining: 0, changed: true, canSave: true })
assert.equal(resolvePayCategoryValues(complete, changed).sunday, 'other')
assert.equal(resolvePayCategoryValues(complete, changed).saturday, 'sat')

// 已保存的类别在 MYOB 里找不到了：后端 missingKeys 说了算；后端漏报时用类别列表兜底；失效项视为未选
const missing = categories({
  mapping: { weekdayUid: 'base', saturdayUid: 'gone', sundayUid: 'sun', personalLeaveUid: 'pers', holidayLeaveUid: 'hol' },
  missingKeys: ['saturday'],
  mappedCount: 5,
  complete: false,
})
assert.equal(isPayCategoryMissing(missing, 'saturday'), true)
assert.equal(getPayCategoryRowState(missing, {}, 'saturday'), 'missing')
assert.equal(resolvePayCategoryValues(missing, {}).saturday, null, '失效类别不再当作已选，Select 不会显示裸 uid')
assert.deepEqual(getPayCategorySaveState(missing, {}), { remaining: 1, changed: true, canSave: false })
const underReported = categories({ mapping: { weekdayUid: 'deleted-in-myob' }, missingKeys: [] })
assert.equal(isPayCategoryMissing(underReported, 'weekday'), true, '后端漏报时用类别列表再核对一次')
// 重新选择后：已修改，可保存
const refixed = setPayCategoryEdit(missing, {}, 'saturday', 'sat')
assert.equal(getPayCategoryRowState(missing, refixed, 'saturday'), 'edited')
assert.equal(getPayCategorySaveState(missing, refixed).canSave, true)
// 建议不会覆盖已保存（哪怕失效）的项：已设置的项后端不给建议；就算给了也不采用
const missingWithSuggestion = categories({ mapping: { weekdayUid: 'gone' }, missingKeys: ['weekday'], suggestions: { weekdayUid: 'base' } })
assert.equal(getBasePayCategoryUid(missingWithSuggestion, 'weekday'), null)
// 建议指向的类别已不在 MYOB 列表里 → 不预填
assert.equal(getBasePayCategoryUid(categories({ suggestions: { weekdayUid: 'ghost' } }), 'weekday'), null)

// 临时工周末两项（可选）：不影响必填计数 / 完整度；设了或清空都算改动，保存请求体带上
const casualPicked = setPayCategoryEdit(complete, {}, 'saturdayCasual', 'sat')
assert.deepEqual(casualPicked, { saturdayCasual: 'sat' })
assert.equal(getPayCategoryRowState(complete, casualPicked, 'saturdayCasual'), 'edited')
assert.deepEqual(getPayCategorySaveState(complete, casualPicked), { remaining: 0, changed: true, canSave: true })
assert.equal(buildPayCategoryMappingRequest(complete, casualPicked)?.saturdayCasualUid, 'sat')
assert.equal(buildPayCategoryMappingRequest(complete, casualPicked)?.sundayCasualUid, null)
assert.equal(countMappedValues(resolvePayCategoryValues(complete, casualPicked)), 5, '临时工两项不计入必填已选数')
// 已保存临时工项后清空：草稿记为 null；请求体里该项为 null；再改回原值 → 草稿清空
const casualSaved = categories({
  mapping: {
    weekdayUid: 'base', saturdayUid: 'sat', sundayUid: 'sun', saturdayCasualUid: 'other',
    personalLeaveUid: 'pers', holidayLeaveUid: 'hol',
  },
  mappedCount: 5,
  complete: true,
})
assert.equal(getPayCategoryRowState(casualSaved, {}, 'saturdayCasual'), 'set')
const casualCleared = setPayCategoryEdit(casualSaved, {}, 'saturdayCasual', null)
assert.deepEqual(casualCleared, { saturdayCasual: null })
assert.equal(getPayCategoryRowState(casualSaved, casualCleared, 'saturdayCasual'), 'unset')
assert.equal(buildPayCategoryMappingRequest(casualSaved, casualCleared)?.saturdayCasualUid, null)
assert.deepEqual(setPayCategoryEdit(casualSaved, casualCleared, 'saturdayCasual', 'other'), {})
// 临时工类别名称相近建议也走预填；MYOB 里这个类别被删：标失效，但必填依然完整
const casualSuggested = categories({ suggestions: { saturdayCasualUid: 'sat' } })
assert.equal(getPayCategoryRowState(casualSuggested, {}, 'saturdayCasual'), 'suggested')
const casualMissing = categories({
  mapping: { weekdayUid: 'base', saturdayUid: 'sat', sundayUid: 'sun', sundayCasualUid: 'gone', personalLeaveUid: 'pers', holidayLeaveUid: 'hol' },
  missingKeys: ['sundayCasual'],
  mappedCount: 5,
  complete: true,
})
assert.equal(getPayCategoryRowState(casualMissing, {}, 'sundayCasual'), 'missing')
assert.equal(getPayCategorySaveState(casualMissing, {}).remaining, 0)

// 重新读取 MYOB 后对账草稿：选的类别没了 → 丢弃；与新基线一致 → 丢弃
const afterRefresh = categories({ categories: [{ uid: 'base', name: 'Base Hourly' }, { uid: 'sat', name: 'Sat' }], suggestions: { sundayUid: 'sat' } })
assert.deepEqual(reconcilePayCategoryEdits(afterRefresh, { weekday: 'base', saturday: 'ghost', sunday: 'sat' }), { weekday: 'base' })
assert.deepEqual(reconcilePayCategoryEdits(afterRefresh, {}), {})

// 公司卡片徽章：用已保存数据；没读到按 0 / 5 未完整
assert.deepEqual(getCompanyMappingBadge(complete), { mapped: 5, total: 5, complete: true })
assert.deepEqual(getCompanyMappingBadge(suggested), { mapped: 1, total: 5, complete: false })
assert.deepEqual(getCompanyMappingBadge(null), { mapped: 0, total: 5, complete: false })
assert.deepEqual(getCompanyMappingBadge(undefined), { mapped: 0, total: 5, complete: false })

// ───────────── 纯日期（不经本地时区） ─────────────

assert.deepEqual(parseIsoDate('2026-10-26'), { year: 2026, month: 10, day: 26 })
assert.deepEqual(parseIsoDate('2026-10-26T00:00:00'), { year: 2026, month: 10, day: 26 })
assert.equal(parseIsoDate('2026-02-31'), null, '不存在的日期')
assert.equal(parseIsoDate('2026-13-01'), null)
assert.equal(parseIsoDate('26-10-26'), null)
assert.equal(parseIsoDate(null), null)
assert.equal(getWeekdayIndex('2026-10-26'), 1, '2026-10-26 是周一')
assert.equal(getWeekdayIndex('2026-11-01'), 0, '2026-11-01 是周日')
assert.equal(getWeekdayIndex('2026-10-31'), 6)
assert.equal(getWeekdayIndex('nope'), null)
assert.deepEqual(buildDayHeader('2026-10-26'), { day: '2026-10-26', weekdayKey: 'myob.push.weekdays.mon', dayOfMonth: 26 })
assert.deepEqual(buildDayHeader('2026-11-01'), { day: '2026-11-01', weekdayKey: 'myob.push.weekdays.sun', dayOfMonth: 1 })
assert.deepEqual(buildDayHeader('bad'), { day: 'bad', weekdayKey: null, dayOfMonth: null })
assert.equal(WEEKDAY_LABEL_KEYS.length, 7)
assert.equal(diffDays('2026-10-26', '2026-11-01'), 6)
assert.equal(diffDays('2026-10-31', '2026-11-01'), 1, '跨月')
assert.equal(diffDays('2026-12-31', '2027-01-01'), 1, '跨年')
assert.equal(diffDays('bad', '2026-11-01'), null)
assert.equal(getPeriodWeeks({ start: '2026-10-26', end: '2026-11-01' }), 1)
assert.equal(getPeriodWeeks({ start: '2026-10-19', end: '2026-11-01' }), 2)
assert.equal(getPeriodWeeks({ start: 'x', end: 'y' }), 1)
assert.equal(formatPeriodRange('2026-10-26', '2026-11-01'), '2026-10-26 ~ 2026-11-01')
assert.equal(formatPeriodRange('2026-10-26', '2026-11-01', true), '2026-10-26 ~ 11-01')
assert.equal(formatPeriodRange('2026-12-28', '2027-01-03', true), '2026-12-28 ~ 2027-01-03', '跨年时保留年份')
assert.equal(formatPeriodRange('2026-10-26', ''), '2026-10-26')

// ───────────── 小时格式化 ─────────────

assert.equal(formatHours(7.6), '7.60')
assert.equal(formatHours(0), '0.00')
assert.equal(formatHours(Number.NaN), '0.00')
assert.equal(formatHoursCell(0), '—')
assert.equal(formatHoursCell(0.001), '—')
assert.equal(formatHoursCell(8), '8.00')
assert.equal(formatHoursCell(-1.5), '-1.50')
assert.equal(formatFixedHours(20), '20')
assert.equal(formatFixedHours(7.5), '7.5')
assert.equal(formatFixedHours(7.126), '7.13')

// ───────────── 推送预览：夹具 ─────────────

function row(overrides: Record<string, unknown> = {}): MyobPreviewRow {
  const parsed = normalizeMyobPreviewRow({
    userGuid: 'u1',
    userName: 'Mei Lin Zhang',
    payMode: 'Transfer',
    fixedTransferHoursPerWeek: 0,
    isCrossCompany: false,
    allocatedTransferHours: 34,
    actualHours: 34,
    storeLabels: ['1014 Kawana'],
    status: 'Ready',
    pushed: { weekday: 30, saturday: 4 },
    pushedTotal: 34,
    cashOutHours: 0,
    blockers: [],
    myobName: 'Mei Lin Zhang',
    myobState: 'Unknown',
    detail: { days: ['2026-10-26'], actual: [], pushed: [], cash: [], shares: [], checkOk: true, checkMessage: '' },
    ...overrides,
  })
  assert.ok(parsed)
  return parsed
}

function previewOf(rows: MyobPreviewRow[], overrides: Record<string, unknown> = {}): MyobTimesheetPreview {
  return normalizeMyobTimesheetPreview({
    connectionGuid: 'c1',
    connectionName: 'Hot Bargain Investment Pty Ltd',
    periodStart: '2026-10-26',
    periodEnd: '2026-11-01',
    weeks: 1,
    periods: [{ start: '2026-10-26', end: '2026-11-01' }, { start: '2026-10-19', end: '2026-10-25' }],
    readiness: {
      lockedStores: ['1014 Kawana', '1013 Orion'],
      unlockedStores: [],
      categoryMappedCount: 5,
      categoryComplete: true,
      myobChecked: false,
      myobProcessedCount: 0,
      myobCheckFailedCount: 0,
    },
    hints: [],
    rows,
    pushEnabled: false,
    ...overrides,
  })
}

// ───────────── 行状态 / 拦截标签 ─────────────

const readyView = getRowStatusView(row())
assert.deepEqual(
  { kind: readyView.kind, tone: readyView.tone, labelKey: readyView.labelKey, blockerCode: readyView.blockerCode },
  { kind: 'ready', tone: 'success', labelKey: 'myob.push.status.ready', blockerCode: null },
)
const cashOutView = getRowStatusView(row({ cashOutHours: 8 }))
assert.equal(cashOutView.kind, 'readyCashOut')
assert.equal(cashOutView.tone, 'warning')
assert.equal(cashOutView.labelKey, 'myob.push.status.readyCashOut')
const noTransferView = getRowStatusView(row({ status: 'NoTransfer', pushedTotal: 0, pushed: {} }))
assert.equal(noTransferView.kind, 'noTransfer')
assert.equal(noTransferView.tone, 'default')
assert.equal(noTransferView.labelKey, 'myob.push.status.noTransfer')

// 被拦截：按第一条拦截项给短标签，多条时带「另有 N 项」，悬停说明列出全部原因
const blockedView = getRowStatusView(
  row({
    blockers: [
      { code: 'NOT_BOUND', message: '还没绑定 MYOB 员工卡' },
      { code: 'STORE_NOT_LOCKED', message: '1042 未锁定', ref: '1042' },
    ],
  }),
)
assert.equal(blockedView.kind, 'blocked')
assert.equal(blockedView.tone, 'error')
assert.equal(blockedView.labelKey, 'myob.push.blocker.NOT_BOUND')
assert.equal(blockedView.blockerCode, 'NOT_BOUND')
assert.equal(blockedView.extraBlockers, 1)
assert.equal(blockedView.detail, '还没绑定 MYOB 员工卡\n1042 未锁定')
// 每种拦截码都有短标签；未知码用通用「被拦截」，不展示后端原始码
for (const code of ['NOT_BOUND', 'STORE_NOT_LOCKED', 'STORE_NOT_MAPPED', 'CATEGORY_NOT_MAPPED', 'NO_RATE', 'NO_STORE_ACCESS', 'MYOB_PROCESSED']) {
  assert.equal(getBlockerLabelKey(code), BLOCKER_LABEL_KEYS[code])
  assert.notEqual(getBlockerLabelKey(code), 'myob.push.blocker.unknown')
}
assert.equal(getBlockerLabelKey('SOMETHING_NEW'), 'myob.push.blocker.unknown')
assert.equal(getBlockerLabelKey(null), 'myob.push.blocker.unknown')
assert.equal(getBlockerLabelKey(' not_bound '), 'myob.push.blocker.NOT_BOUND', '大小写与空白不敏感')
// 状态未知（被归一成 Blocked 但没有拦截项）：通用「被拦截」，没有第一条拦截码
const unknownBlocked = getRowStatusView(row({ status: 'Weird' }))
assert.equal(unknownBlocked.kind, 'blocked')
assert.equal(unknownBlocked.labelKey, 'myob.push.blocker.unknown')
assert.equal(unknownBlocked.blockerCode, null)
assert.equal(unknownBlocked.detail, null)

assert.equal(getRowClassName(row()), '')
assert.equal(getRowClassName(row({ status: 'NoTransfer' })), 'myob-push-row-muted')
assert.equal(getRowClassName(row({ blockers: [{ code: 'NOT_BOUND' }] })), 'myob-push-row-blocked')

// ───────────── 员工单元格 ─────────────

assert.deepEqual(getPayModeTag(row({ payMode: 'Transfer' })), { labelKey: 'myob.push.payMode.Transfer' })
assert.deepEqual(getPayModeTag(row({ payMode: 'Cash' })), { labelKey: 'myob.push.payMode.Cash' })
assert.deepEqual(getPayModeTag(row({ payMode: 'Mixed', fixedTransferHoursPerWeek: 20 })), { labelKey: 'myob.push.payMode.Mixed', hours: '20' })
assert.equal(getPayModeTag(row({ payMode: null })), null)

assert.equal(formatRatio([30, 10]), '3 : 1')
assert.equal(formatRatio([10, 10]), '1 : 1')
assert.equal(formatRatio([12.5, 7.5]), '5 : 3')
assert.equal(formatRatio([30, 0]), null, '有一家为 0 工时没有比例')
assert.equal(formatRatio([30]), null)
assert.equal(formatRatio([33.33, 66.67]), null, '约分后数字太大，没有易读的比例')

const crossDetail = {
  days: ['2026-10-26'],
  shares: [
    { connectionGuid: 'c1', connectionName: 'Hot Bargain Investment Pty Ltd', actualHours: 30, allocatedTransferHours: 15 },
    { connectionGuid: 'c2', connectionName: 'Hot Bargain Australian Pty Ltd', actualHours: 10, allocatedTransferHours: 5 },
  ],
}
const crossRow = row({ userName: 'Jason Nguyen', payMode: 'Mixed', fixedTransferHoursPerWeek: 20, isCrossCompany: true, allocatedTransferHours: 15, actualHours: 30, detail: crossDetail })
assert.deepEqual(describeCrossShare(crossRow), { allocated: '15.00', total: '20.00', ratio: '3 : 1' })
assert.equal(describeCrossShare(row()), null)
assert.equal(describeCrossShare(row({ isCrossCompany: true })), null, '标了跨公司但没有份额数据：不显示份额')
assert.deepEqual(getRowNotes(crossRow), [
  { key: 'myob.push.notes.crossShare', values: { allocated: '15.00', total: '20.00', ratio: '3 : 1' } },
])
assert.deepEqual(getRowNotes(row()), [{ key: 'myob.push.notes.bound', values: { name: 'Mei Lin Zhang' } }])
assert.deepEqual(getRowNotes(row({ myobName: null })), [])
assert.deepEqual(
  getRowNotes(row({ payMode: 'Mixed', pushed: { weekday: 8, personalLeave: 8 } })).map((item) => item.key),
  ['myob.push.notes.bound', 'myob.push.notes.leaveFirst'],
)
assert.deepEqual(
  getRowNotes(row({ myobState: 'HasEntries' })).map((item) => [item.key, item.warn ?? false]),
  [['myob.push.notes.bound', false], ['myob.push.notes.myobHasEntries', true]],
)
const failedNotes = getRowNotes(row({ myobState: 'CheckFailed' }))
assert.equal(failedNotes[failedNotes.length - 1]?.key, 'myob.push.notes.myobCheckFailed')

// ───────────── 就绪卡 ─────────────

const okPreview = previewOf([row()])
const okCards = buildReadinessCards(okPreview)
assert.deepEqual(okCards.map((card) => card.id), ['locked', 'unlocked', 'category', 'processed'])
assert.deepEqual(okCards.map((card) => card.tone), ['ok', 'ok', 'ok', 'muted'], '没检查 MYOB 时第四张是灰色「未检查」，不假装没有已发薪记录')
assert.equal(okCards[0].subText, '1014 Kawana、1013 Orion')
assert.equal(okCards[2].subText, 'Hot Bargain Investment Pty Ltd')
assert.equal(okCards[3].subKey, 'myob.push.readiness.processed.unchecked')
assert.equal(okCards.every((card) => card.symbol.length > 0), true, '每张卡都有符号，状态不只靠颜色')

const badPreview = previewOf([row()], {
  readiness: {
    lockedStores: [],
    unlockedStores: [{ storeCode: '1042', storeName: 'TestStore', employeeCount: 1 }],
    categoryMappedCount: 4,
    categoryComplete: false,
    myobChecked: true,
    myobProcessedCount: 2,
  },
})
const badCards = buildReadinessCards(badPreview)
assert.equal(badCards[0].tone, 'muted', '没有已锁定分店')
assert.equal(badCards[1].tone, 'bad')
assert.deepEqual(badCards[1].titleValues, { count: 1 })
assert.deepEqual(badCards[1].subItems, [{ label: '1042 TestStore', count: 1 }])
assert.equal(badCards[2].tone, 'bad')
assert.deepEqual(badCards[2].titleValues, { mapped: 4, total: 5 })
assert.equal(badCards[2].action, 'goMapping', '映射不完整时带「去映射」')
assert.equal(okCards[2].action, undefined)
assert.equal(badCards[3].tone, 'bad')
assert.deepEqual(badCards[3].subValues, { count: 2 })
// 检查过：没有已发薪 → 绿；有读取失败 → 琥珀
assert.equal(buildReadinessCards(previewOf([], { readiness: { myobChecked: true, categoryComplete: true } }))[3].tone, 'ok')
const failedCard = buildReadinessCards(previewOf([], { readiness: { myobChecked: true, myobCheckFailedCount: 3, categoryComplete: true } }))[3]
assert.equal(failedCard.tone, 'warn')
assert.deepEqual(failedCard.subValues, { count: 3 })

// ───────────── 提示（hints） ─────────────

const hints = normalizeMyobTimesheetPreview({
  hints: [
    { code: 'ANNUAL_CASH_OUT_NOT_PUSHED', userGuid: 'a', userName: 'Yuki Tanaka', message: 'Yuki Tanaka 本期有年假折现 8.00 h：不会推送' },
    { code: 'BIRTHDAY_REVIEW', userGuid: 'b', userName: 'Tom Anderson', message: '已于 10-28 满 18 岁' },
    { code: 'SOMETHING_NEW', userGuid: 'c', userName: 'Z', message: '新提示' },
    { code: 'BIRTHDAY_REVIEW', userGuid: 'd', userName: 'Amy', message: '也满 18 岁' },
  ],
}).hints
const groups = groupHints(hints)
assert.deepEqual(
  groups.map((group) => [group.code, group.items.map((item) => item.userGuid)]),
  [['BIRTHDAY_REVIEW', ['b', 'd']], ['ANNUAL_CASH_OUT_NOT_PUSHED', ['a']], ['UNKNOWN', ['c']]],
  '已知码按固定顺序在前，未知码合并到最后，组内保持原顺序',
)
assert.equal(groups[0].labelKey, 'myob.push.hints.codes.BIRTHDAY_REVIEW')
assert.equal(groups[2].labelKey, 'myob.push.hints.codes.unknown')
assert.deepEqual(groupHints([]), [])
for (const code of Object.keys(HINT_CODE_LABEL_KEYS)) {
  assert.equal(getHintLabelKey(code), HINT_CODE_LABEL_KEYS[code])
}
assert.equal(getHintLabelKey('nope'), 'myob.push.hints.codes.unknown')
// 员工名：说明里本来就有就原位拆分加粗，没有就前面补「姓名：」
assert.deepEqual(splitHintMessage(hints[0]), { before: '', name: 'Yuki Tanaka', after: ' 本期有年假折现 8.00 h：不会推送', separated: false })
assert.deepEqual(splitHintMessage(hints[1]), { before: '', name: 'Tom Anderson', after: '已于 10-28 满 18 岁', separated: true })
assert.deepEqual(splitHintMessage({ code: 'X', userGuid: '', userName: '', message: '只有说明' }), { before: '只有说明', name: '', after: '', separated: false })

// ───────────── 推送按钮 ─────────────

assert.equal(canPushPreview(null, true), false)
assert.equal(canPushPreview(previewOf([row()], { totals: { readyCount: 1 }, pushEnabled: false }), true), false, '本批后端恒不开放：永远禁用')
const enabled = previewOf([row()], { totals: { readyCount: 1, readyHours: 34 }, pushEnabled: true })
assert.equal(canPushPreview(enabled, false), true, '没有提示时不需要勾选')
const enabledWithHints = previewOf([row()], { totals: { readyCount: 1 }, pushEnabled: true, hints: [{ code: 'BIRTHDAY_REVIEW', message: 'x' }] })
assert.equal(canPushPreview(enabledWithHints, false), false, '有提示未勾选「我已知晓」时禁用')
assert.equal(canPushPreview(enabledWithHints, true), true)
assert.equal(canPushPreview(previewOf([], { totals: { readyCount: 0 }, pushEnabled: true }), true), false, '没有可推送的人时禁用')

// ───────────── 周期选项 ─────────────

assert.deepEqual(buildPeriodOptions(null), [])
assert.deepEqual(buildPeriodOptions(okPreview).map((item) => item.start), ['2026-10-26', '2026-10-19'])
const notListed = previewOf([], { periodStart: '2026-09-01', periodEnd: '2026-09-07' })
assert.deepEqual(buildPeriodOptions(notListed).map((item) => item.start), ['2026-09-01', '2026-10-26', '2026-10-19'], '后端返回的周期不在列表里时补在最前面')

// ───────────── 按日明细抽屉 ─────────────

for (const kind of ['weekday', 'saturday', 'sunday', 'publicHolidayWork', 'publicHolidayPay', 'sick', 'annual', 'annualCashOut']) {
  assert.equal(getKindLabelKey(kind), KIND_LABEL_KEYS[kind])
  assert.ok(getKindLabelKey(kind))
}
assert.equal(getKindLabelKey('mystery'), null)
for (const key of PAY_CATEGORY_KEYS) {
  assert.equal(getPushedLabelKey(key), PUSHED_LABEL_KEYS[key])
}
assert.equal(getPushedLabelKey('mystery'), null)

assert.deepEqual(shortCompanyNames(['Hot Bargain Investment Pty Ltd', 'Hot Bargain Australian Pty Ltd']), ['Investment', 'Australian'])
assert.deepEqual(shortCompanyNames(['Hot Bargain Investment Pty Ltd']), ['Hot Bargain Investment'])
assert.deepEqual(shortCompanyNames(['Alpha Pty Ltd', 'Beta Pty. Ltd.']), ['Alpha', 'Beta'])
assert.deepEqual(shortCompanyNames(['Hot Bargain', 'Hot Bargain Two']), ['Hot Bargain', 'Hot Bargain Two'], '去前缀后会变空 / 重名时放弃去前缀')
assert.deepEqual(shortCompanyNames(['Solo']), ['Solo'])
assert.deepEqual(shortCompanyNames([]), [])

const cross = buildExplanation(crossRow)
assert.deepEqual(cross, {
  kind: 'cross',
  pairs: 'Investment 30.00 h : Australian 10.00 h',
  ratioPart: ' = 3 : 1',
  allocated: '15.00 / 5.00',
})
assert.equal(buildExplanation(row({ payMode: 'Mixed' })).kind, 'order', '非跨公司的转账 + 现金：按装入顺序')
assert.equal(buildExplanation(row({ payMode: 'Transfer' })).kind, 'transfer')
assert.equal(buildExplanation(row({ payMode: 'Cash' })).kind, 'cash')
assert.equal(buildExplanation(row({ payMode: null })).kind, 'order')
assert.equal(buildExplanation(row({ isCrossCompany: true, payMode: 'Mixed' })).kind, 'order', '标了跨公司但没有份额：回落成装入顺序说明')

const subtitle = buildDrawerSubtitle(crossRow, { connectionName: 'Hot Bargain Investment Pty Ltd', periodStart: '2026-10-26', periodEnd: '2026-11-01' })
assert.equal(subtitle.company, 'Investment')
assert.equal(subtitle.period, '2026-10-26 ~ 11-01')
assert.equal(subtitle.payModeKey, 'myob.push.drawer.subtitle.mixed')
assert.deepEqual(subtitle.payModeValues, { fixed: '20', allocated: '15.00' })
const soloSubtitle = buildDrawerSubtitle(row(), { connectionName: 'Hot Bargain Investment Pty Ltd', periodStart: '2026-10-26', periodEnd: '2026-11-01' })
assert.equal(soloSubtitle.company, 'Hot Bargain Investment', '只有一家公司时只去掉法律后缀')
assert.equal(soloSubtitle.payModeKey, 'myob.push.drawer.subtitle.transfer')
assert.equal(buildDrawerSubtitle(row({ payMode: null }), { connectionName: 'X', periodStart: '', periodEnd: '' }).payModeKey, null)

assert.equal(stripCheckPrefix('校验通过：推送合计 15.00 h'), '推送合计 15.00 h')
assert.equal(stripCheckPrefix('校验未通过: 差 1.00 h'), '差 1.00 h')
assert.equal(stripCheckPrefix('推送合计 15.00 h'), '推送合计 15.00 h')
assert.equal(stripCheckPrefix(''), '')

// ───────────── CSV ─────────────

assert.equal(guardCsvFormula('=SUM(A1)'), "'=SUM(A1)")
assert.equal(guardCsvFormula('-1'), "'-1")
assert.equal(guardCsvFormula('Normal'), 'Normal')
assert.equal(escapeCsvField('a,b'), '"a,b"')
assert.equal(escapeCsvField('say "hi"'), '"say ""hi"""')
assert.equal(escapeCsvField('line1\nline2'), '"line1\nline2"')
assert.equal(escapeCsvField('plain'), 'plain')

const csvPreview = previewOf(
  [
    row({ userName: 'Mei Lin Zhang' }),
    row({ userGuid: 'u2', userName: '=cmd|"x"', payMode: 'Mixed', fixedTransferHoursPerWeek: 20, storeLabels: ['1013 Orion', '1014 Kawana'], cashOutHours: 8, pushed: { weekday: 15 }, pushedTotal: 15, myobName: null }),
    row({ userGuid: 'u3', userName: "Liam O'Brien", blockers: [{ code: 'STORE_NOT_LOCKED', message: '1042 TestStore, 本期未锁定' }], pushedTotal: 12, pushed: { weekday: 12 }, myobName: null }),
  ],
  { hints: [] },
)
const csv = buildPreviewCsv(csvPreview, (key, values) => (values && 'hours' in values ? `${key}|${values.hours}` : key))
assert.ok(csv.startsWith(CSV_BOM), 'UTF-8 带 BOM')
assert.ok(csv.endsWith('\r\n'))
const lines = csv.slice(CSV_BOM.length).split('\r\n')
assert.equal(lines.length, 5, '表头 + 3 行 + 结尾空行')
assert.equal(lines[0].split(',').length, 13)
assert.ok(lines[1].startsWith('Mei Lin Zhang,1014 Kawana,myob.push.payMode.Transfer|,30.00,4.00,0.00,0.00,0.00,34.00,0.00,myob.push.status.ready,,Mei Lin Zhang'), lines[1])
assert.ok(lines[2].startsWith(`"'=cmd|""x""",1013 Orion / 1014 Kawana,myob.push.payMode.Mixed|20,15.00`), `公式前缀与引号转义：${lines[2]}`)
assert.ok(lines[2].includes(',8.00,myob.push.status.readyCashOut,'), '折现工时单列，状态为「可推送 · 折现不推」')
assert.ok(lines[3].includes(`"1042 TestStore, 本期未锁定"`), '被拦截原因进 CSV，含逗号时加引号')
assert.ok(lines[3].includes('myob.push.blocker.STORE_NOT_LOCKED'))
assert.equal(buildPreviewCsvFileName('Hot Bargain Investment Pty Ltd', '2026-10-26'), 'myob-timesheet-preview-Hot-Bargain-Investment-Pty-Ltd-2026-10-26.csv')
assert.equal(buildPreviewCsvFileName('中文公司', ''), 'myob-timesheet-preview-company-period.csv')

console.log('myobTimesheetLogic.test: ok')
