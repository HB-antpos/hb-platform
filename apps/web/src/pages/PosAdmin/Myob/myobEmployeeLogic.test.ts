import assert from 'node:assert/strict'

import {
  normalizeMyobBindingCandidates,
  normalizeMyobBindingSnapshot,
  normalizeMyobConfirmResult,
  normalizeMyobPayrollEmployerDetail,
  normalizeMyobPayrollEmployerList,
} from '../../../services/myobService'
import type {
  MyobBindingCounts,
  MyobBindingRow,
  MyobEmployeeCard,
  MyobMatchCandidate,
  MyobPayrollEmployerDetail,
  MyobPayrollEmployerRow,
} from '../../../types/myob'
import {
  BASIS_FIELDS,
  BINDING_FILTERS,
  BINDING_FILTER_LABEL_KEYS,
  BINDING_STATUS_BADGES,
  buildBatchConfirmItems,
  buildCompareRows,
  buildManualConfirmItem,
  buildPayrollDraft,
  buildPayrollSaveItems,
  buildSingleConfirmItem,
  compareEmployeeType,
  describeBindFailures,
  describeEntitlementKey,
  describePayrollEmployer,
  filterBindingRows,
  findUnboundPayrollCompanies,
  formatBirthday,
  formatShortDate,
  formatShortDateTime,
  formatStoreLabel,
  getBasisResult,
  summarizeStoreLabels,
  getBindFailureKey,
  getBindingRowActions,
  getEmployeeTypeKey,
  getEvidenceWarning,
  getFilterCount,
  getInitials,
  getOtherStoresOfUser,
  getPendingBindingCount,
  getPreselectedCardUid,
  getScoreTone,
  getSignalBadge,
  getTypeIssueActionKey,
  getTypeIssueKey,
  getTypeIssueNoteKey,
  getVisibleMyobTabs,
  hasPayrollDraftChanges,
  isCandidateSelectable,
  joinSubtitle,
  normalizeAllowedEffectiveFrom,
  pickDefaultEffectiveFrom,
  resolveActiveMyobTab,
  resolveEffectiveFrom,
} from './myobEmployeeLogic'

// ───────────── 夹具 ─────────────

function card(uid: string, name: string, extra: Partial<MyobEmployeeCard> = {}): MyobEmployeeCard {
  return {
    uid,
    displayId: null,
    name,
    email: `${name.toLowerCase().replace(/\s+/g, '.')}@example.com`,
    phoneMasked: '•••• 4821',
    dateOfBirth: '1996-03-12',
    isActive: true,
    employmentStatus: 'FullTime',
    hasAnnualEntitlement: true,
    hasSickEntitlement: true,
    ...extra,
  }
}

function candidate(uid: string, name: string, score: number, extra: Partial<MyobMatchCandidate> = {}): MyobMatchCandidate {
  return {
    card: card(uid, name),
    score,
    basis: { email: 'match', birthday: 'match', name: 'match', phone: 'match' },
    notes: [],
    boundToUserGuid: null,
    boundToUserName: null,
    ...extra,
  }
}

function row(userGuid: string, userName: string, status: MyobBindingRow['status'], extra: Partial<MyobBindingRow> = {}): MyobBindingRow {
  return {
    userGuid,
    userName,
    email: `${userName.toLowerCase().replace(/\s+/g, '.')}@example.com`,
    employeeType: 'FullTime',
    storeLabels: ['1014 Kawana'],
    isCrossCompany: false,
    status,
    link: null,
    candidate: status === 'Unmatched' || status === 'Bound' ? null : candidate(`uid-${userGuid}`, userName, 90),
    ...extra,
  }
}

const rows: MyobBindingRow[] = [
  row('u1', 'Mei Lin Zhang', 'Suggested'),
  row('u2', 'Jason Nguyen', 'Suggested', { storeLabels: ['1013 Orion'], isCrossCompany: true }),
  row('u3', 'Yuki Tanaka', 'NeedsReview', { employeeType: 'Temporary', storeLabels: ['1013 Orion'] }),
  row('u4', 'Chloe Wang', 'Conflict'),
  row('u5', "Liam O'Brien", 'Unmatched', { email: null, storeLabels: ['1042 TestStore'] }),
  row('u6', 'Sarah Kim', 'Bound', {
    link: {
      linkGuid: 'l6',
      myobEmployeeUid: 'uid-6',
      myobName: 'Sarah Kim (MYOB)',
      matchMethod: 'Manual',
      matchScore: 95,
      boundByName: 'admin',
      boundAt: '2026-10-28T09:12:00',
      cardMissing: false,
    },
  }),
]

const counts: MyobBindingCounts = {
  all: 6,
  bound: 1,
  suggested: 2,
  needsReview: 2,
  unmatched: 1,
  onlyInOurs: 2,
  onlyInMyob: 3,
  typeIssues: 2,
  batchConfirmable: 2,
}

// ───────────── 页签权限 ─────────────

assert.deepEqual(getVisibleMyobTabs({ canManageConnections: true, canBindEmployees: true, canEditPayroll: true }), ['connections', 'employees', 'payroll'])
assert.deepEqual(getVisibleMyobTabs({ canManageConnections: false, canBindEmployees: true, canEditPayroll: false }), ['employees'])
assert.deepEqual(getVisibleMyobTabs({ canManageConnections: false, canBindEmployees: false, canEditPayroll: true }), ['payroll'])
assert.deepEqual(getVisibleMyobTabs({ canManageConnections: false, canBindEmployees: false, canEditPayroll: false }), [])
assert.equal(resolveActiveMyobTab('payroll', ['connections', 'payroll']), 'payroll')
assert.equal(resolveActiveMyobTab('employees', ['connections', 'payroll']), 'connections', '无权限页签回落到第一个可见页签')
assert.equal(resolveActiveMyobTab(null, ['employees']), 'employees')
assert.equal(resolveActiveMyobTab('payroll', []), null)

// ───────────── 筛选 / 搜索 / 计数 ─────────────

assert.equal(filterBindingRows(rows, 'all', '').length, 6)
assert.deepEqual(filterBindingRows(rows, 'bound', '').map((r) => r.userGuid), ['u6'])
assert.deepEqual(filterBindingRows(rows, 'suggested', '').map((r) => r.userGuid), ['u1', 'u2'])
assert.deepEqual(filterBindingRows(rows, 'review', '').map((r) => r.userGuid), ['u3', 'u4'], '需人工确认 = NeedsReview + Conflict')
assert.deepEqual(filterBindingRows(rows, 'unmatched', '').map((r) => r.userGuid), ['u5'])
// 保持后端顺序，不重排
assert.deepEqual(filterBindingRows(rows, 'all', '').map((r) => r.userGuid), ['u1', 'u2', 'u3', 'u4', 'u5', 'u6'])
// 搜索：姓名 / 邮箱 / 分店，大小写不敏感，首尾空白忽略
assert.deepEqual(filterBindingRows(rows, 'all', '  JASON ').map((r) => r.userGuid), ['u2'])
assert.deepEqual(filterBindingRows(rows, 'all', 'orion').map((r) => r.userGuid), ['u2', 'u3'])
assert.deepEqual(filterBindingRows(rows, 'all', 'sarah.kim@example').map((r) => r.userGuid), ['u6'])
assert.deepEqual(filterBindingRows(rows, 'all', 'teststore').map((r) => r.userGuid), ['u5'])
assert.deepEqual(filterBindingRows(rows, 'all', 'MYOB)').map((r) => r.userGuid), ['u6'], '已绑定的 MYOB 姓名也可被搜到')
assert.deepEqual(filterBindingRows(rows, 'suggested', 'orion').map((r) => r.userGuid), ['u2'], '筛选与搜索叠加')
assert.deepEqual(filterBindingRows(rows, 'all', 'no-such'), [])
// 邮箱为空的员工不会因 null 崩溃
assert.equal(filterBindingRows(rows, 'all', 'liam').length, 1)

// chip 计数取自 counts
assert.deepEqual(
  BINDING_FILTERS.map((filter) => getFilterCount(counts, filter)),
  [6, 1, 2, 2, 1],
)
for (const filter of BINDING_FILTERS) {
  assert.ok(BINDING_FILTER_LABEL_KEYS[filter].startsWith('myob.employees.filters.'))
}

// 页签徽章 = 高分建议 + 需人工确认（含冲突）+ 未匹配
assert.equal(getPendingBindingCount(counts), 5)
assert.equal(getPendingBindingCount(null), 0)
assert.equal(getPendingBindingCount({ ...counts, suggested: 0, needsReview: 0, unmatched: 0 }), 0)

// ───────────── 批量 / 单条确认 ─────────────

assert.deepEqual(buildBatchConfirmItems(rows), [
  { userGuid: 'u1', myobEmployeeUid: 'uid-u1', manual: false },
  { userGuid: 'u2', myobEmployeeUid: 'uid-u2', manual: false },
])
assert.deepEqual(buildBatchConfirmItems([]), [])
// Suggested 但缺候选卡的脏数据不进批量
assert.deepEqual(buildBatchConfirmItems([row('x', 'X', 'Suggested', { candidate: null })]), [])
assert.deepEqual(buildSingleConfirmItem(rows[0]), { userGuid: 'u1', myobEmployeeUid: 'uid-u1', manual: false })
assert.equal(buildSingleConfirmItem(rows[2]), null, '需人工确认的不能走一键确认')
assert.deepEqual(buildManualConfirmItem('u3', { uid: 'c9' }), { userGuid: 'u3', myobEmployeeUid: 'c9', manual: true })

// ───────────── 行操作 ─────────────

assert.deepEqual(getBindingRowActions('Suggested'), ['confirm', 'change'])
assert.deepEqual(getBindingRowActions('NeedsReview'), ['review', 'change'])
assert.deepEqual(getBindingRowActions('Conflict'), ['searchOther'])
assert.deepEqual(getBindingRowActions('Unmatched'), ['searchMyob'])
assert.deepEqual(getBindingRowActions('Bound'), ['unbind'])
// 「核对并绑定」预选最佳候选；「换一个」/ 搜索不预选
assert.equal(getPreselectedCardUid('review', rows[2]), 'uid-u3')
assert.equal(getPreselectedCardUid('change', rows[2]), null)
assert.equal(getPreselectedCardUid('searchMyob', rows[4]), null)
// 最佳候选已被别人绑定时不预选（不可选）
assert.equal(
  getPreselectedCardUid('review', row('y', 'Y', 'NeedsReview', { candidate: candidate('c', 'Y', 50, { boundToUserGuid: 'other' }) })),
  null,
)
assert.equal(isCandidateSelectable(candidate('c', 'A', 50)), true)
assert.equal(isCandidateSelectable(candidate('c', 'A', 50, { boundToUserGuid: 'g', boundToUserName: 'G' })), false)

// ───────────── 徽章 ─────────────

assert.deepEqual(getSignalBadge('match'), { symbol: '✓', tone: 'ok', stateKey: 'myob.employees.signal.match' })
assert.deepEqual(getSignalBadge('mismatch'), { symbol: '✕', tone: 'bad', stateKey: 'myob.employees.signal.mismatch' })
assert.deepEqual(getSignalBadge('unknown'), { symbol: '–', tone: 'muted', stateKey: 'myob.employees.signal.unknown' })
assert.deepEqual(BASIS_FIELDS.map((item) => item.field), ['email', 'birthday', 'name', 'phone'], '徽章顺序：邮箱 生日 姓名 手机')
assert.equal(getBasisResult({ email: 'match', birthday: 'mismatch', name: 'unknown', phone: 'match' }, 'birthday'), 'mismatch')
assert.equal(getScoreTone(98), 'high')
assert.equal(getScoreTone(85), 'high')
assert.equal(getScoreTone(84), 'mid')
assert.equal(getScoreTone(41), 'mid')
assert.equal(getScoreTone(22), 'low')
assert.equal(BINDING_STATUS_BADGES.Suggested.color, 'processing')
assert.equal(BINDING_STATUS_BADGES.Conflict.color, 'error')
assert.equal(BINDING_STATUS_BADGES.Bound.color, 'success')

// ───────────── 用工类型 ─────────────

assert.equal(getEmployeeTypeKey('FullTime'), 'myob.employees.type.FullTime')
assert.equal(getEmployeeTypeKey('Temporary'), 'myob.employees.type.Temporary')
assert.equal(getEmployeeTypeKey('Contractor'), null)
assert.equal(getEmployeeTypeKey(null), null)
assert.equal(describeEntitlementKey(true, true), 'myob.employees.entitlement.both')
assert.equal(describeEntitlementKey(true, false), 'myob.employees.entitlement.annualOnly')
assert.equal(describeEntitlementKey(false, true), 'myob.employees.entitlement.sickOnly')
assert.equal(describeEntitlementKey(false, false), 'myob.employees.entitlement.none')
assert.equal(getTypeIssueKey('MISSING_LEAVE_ENTITLEMENT'), 'myob.employees.typeIssues.codes.missingLeave')
assert.equal(getTypeIssueKey('temporary_has_leave_entitlement'), 'myob.employees.typeIssues.codes.temporaryHasLeave')
assert.equal(getTypeIssueKey('SOMETHING_NEW'), 'myob.employees.typeIssues.codes.unknown')
assert.equal(getTypeIssueKey(null), 'myob.employees.typeIssues.codes.unknown')
assert.equal(getTypeIssueActionKey('MISSING_LEAVE_ENTITLEMENT'), 'myob.employees.typeIssues.actions.fix.missingLeave')
assert.equal(getTypeIssueActionKey('TEMPORARY_HAS_LEAVE_ENTITLEMENT'), 'myob.employees.typeIssues.actions.fix.temporaryHasLeave')
assert.equal(getTypeIssueActionKey('X'), 'myob.employees.typeIssues.actions.fix.generic')
assert.equal(getTypeIssueNoteKey('MISSING_LEAVE_ENTITLEMENT'), 'myob.employees.typeIssues.notes.missingLeave')
assert.equal(getTypeIssueNoteKey('X'), 'myob.employees.typeIssues.notes.generic')

// ───────────── 逐项对比 ─────────────

const yuki = candidate('c-yuki', 'Yuki Tanaka', 55, {
  card: card('c-yuki', 'Yuki Tanaka', {
    email: 'yuki.tanaka@old-mail.com',
    dateOfBirth: '2002-02-14',
    hasAnnualEntitlement: false,
    hasSickEntitlement: false,
  }),
  basis: { email: 'mismatch', birthday: 'unknown', name: 'match', phone: 'match' },
})
const compare = buildCompareRows(yuki, 'Temporary')
assert.deepEqual(compare.map((item) => item.field), ['name', 'email', 'phone', 'birthday', 'type'])
assert.deepEqual(compare.map((item) => item.result), ['same', 'diff', 'same', 'unknown', 'same'])
assert.deepEqual(compare.map((item) => item.highlightTheirs), [false, true, false, false, false], '只有「不同」的 MYOB 值高亮')
assert.equal(compare[1].resultKey, 'myob.drawer.compare.diff')
assert.equal(compare[3].resultKey, 'myob.drawer.compare.unknown')
assert.equal(compare[4].resultKey, 'myob.drawer.compare.typeSame')

// 用工类型比对：临时工挂了假期 = 不符；全职缺其一 = 不符；类型未知 = 无法比对
assert.equal(compareEmployeeType('Temporary', card('a', 'A')), 'diff')
assert.equal(compareEmployeeType('Temporary', card('a', 'A', { hasAnnualEntitlement: false, hasSickEntitlement: false })), 'same')
assert.equal(compareEmployeeType('FullTime', card('a', 'A')), 'same')
assert.equal(compareEmployeeType('PartTime', card('a', 'A', { hasSickEntitlement: false })), 'diff')
assert.equal(compareEmployeeType(null, card('a', 'A')), 'unknown')
assert.equal(buildCompareRows(yuki, 'FullTime')[4].resultKey, 'myob.drawer.compare.typeDiff')
assert.equal(buildCompareRows(yuki, null)[4].resultKey, 'myob.drawer.compare.unknown')

// 证据不足提示
assert.deepEqual(getEvidenceWarning(yuki.basis), { show: true, emailMismatch: true, birthdayUnknown: true, messageKey: 'myob.drawer.warn.both' })
assert.equal(getEvidenceWarning({ email: 'mismatch', birthday: 'match', name: 'match', phone: 'match' }).messageKey, 'myob.drawer.warn.email')
assert.equal(getEvidenceWarning({ email: 'match', birthday: 'unknown', name: 'match', phone: 'match' }).messageKey, 'myob.drawer.warn.birthday')
assert.deepEqual(getEvidenceWarning({ email: 'match', birthday: 'match', name: 'mismatch', phone: 'unknown' }), {
  show: false,
  emailMismatch: false,
  birthdayUnknown: false,
  messageKey: null,
})

// ───────────── 文本格式化 ─────────────

assert.equal(formatBirthday('1996-03-12'), '12 Mar 1996')
assert.equal(formatBirthday('1999-08-03'), '03 Aug 1999')
assert.equal(formatBirthday('2002-02-14T00:00:00'), '14 Feb 2002')
assert.equal(formatBirthday(null), null)
assert.equal(formatBirthday(''), null)
assert.equal(formatBirthday('not a date'), null)
assert.equal(formatBirthday('1999-13-01'), null)
// UTC 09:12（不带 Z）→ 悉尼时间 10-28 20:12（夏令时 UTC+11）
assert.equal(formatShortDateTime('2026-10-28T09:12:00'), '10-28 20:12')
assert.equal(formatShortDateTime(null), '--')
assert.equal(formatShortDate('2026-10-20T01:00:00Z'), '10-20')
assert.equal(formatShortDate(''), '--')
assert.equal(getInitials('Yuki Tanaka'), 'YT')
assert.equal(getInitials('Mei Lin Zhang'), 'ML')
assert.equal(getInitials('Madonna'), 'MA')
assert.equal(getInitials(''), '?')
assert.equal(joinSubtitle(['a@b.com', '1014 Kawana']), 'a@b.com · 1014 Kawana')
assert.equal(joinSubtitle([null, '1031']), '1031')
assert.equal(joinSubtitle([null, '  ']), '—')

// ───────────── 部分失败 ─────────────

assert.equal(getBindFailureKey('MYOB_BIND_CARD_ALREADY_BOUND'), 'myob.employees.failures.cardAlreadyBound')
assert.equal(getBindFailureKey('myob_bind_not_high_score'), 'myob.employees.failures.notHighScore')
assert.equal(getBindFailureKey('SOMETHING_ELSE'), null)
assert.deepEqual(
  describeBindFailures(
    [
      { userGuid: 'u1', code: 'MYOB_BIND_NOT_HIGH_SCORE', message: '不是高分' },
      { userGuid: 'zzz', code: 'NEW_CODE', message: '后端原文' },
    ],
    rows,
  ),
  [
    { userGuid: 'u1', userName: 'Mei Lin Zhang', messageKey: 'myob.employees.failures.notHighScore', fallbackMessage: '不是高分' },
    { userGuid: 'zzz', userName: 'zzz', messageKey: null, fallbackMessage: '后端原文' },
  ],
)

// ───────────── 发薪公司 ─────────────

function payrollRow(storeCode: string, extra: Partial<MyobPayrollEmployerRow> = {}): MyobPayrollEmployerRow {
  return {
    userGuid: 'pu1',
    userName: 'Jason Nguyen',
    storeCode,
    storeName: storeCode === '1013' ? 'Orion' : '',
    defaultConnectionGuid: 'australian',
    defaultConnectionName: 'Australian',
    effectiveConnectionGuid: 'australian',
    effectiveConnectionName: 'Australian',
    isOverride: false,
    effectiveFrom: null,
    isCrossCompany: true,
    ...extra,
  }
}

const detail: MyobPayrollEmployerDetail = {
  userGuid: 'pu1',
  userName: 'Jason Nguyen',
  rows: [
    payrollRow('1013', { isOverride: true, effectiveConnectionGuid: 'investment', effectiveConnectionName: 'Investment', effectiveFrom: '2026-10-26' }),
    payrollRow('1025'),
  ],
  companies: [
    { connectionGuid: 'investment', name: 'Investment' },
    { connectionGuid: 'australian', name: 'Australian' },
  ],
  bindingStates: [
    { connectionGuid: 'investment', bound: false },
    { connectionGuid: 'australian', bound: true },
  ],
  allowedEffectiveFrom: ['2026-11-09', '2026-10-26', '2026-10-26', 'bad', '2026-11-02'],
  history: [],
}

assert.deepEqual(normalizeAllowedEffectiveFrom(detail.allowedEffectiveFrom), ['2026-10-26', '2026-11-02', '2026-11-09'], '去重、过滤非法值、升序')
assert.deepEqual(normalizeAllowedEffectiveFrom(null), [])
assert.equal(pickDefaultEffectiveFrom(detail.allowedEffectiveFrom), '2026-10-26', '默认当前期（第一项）')
assert.equal(pickDefaultEffectiveFrom([]), null)
assert.equal(resolveEffectiveFrom('2026-11-02', detail.allowedEffectiveFrom), '2026-11-02')
assert.equal(resolveEffectiveFrom('2026-01-01', detail.allowedEffectiveFrom), '2026-10-26', '不在可选范围内回落到第一项')
assert.equal(resolveEffectiveFrom(null, []), null)

const draft = buildPayrollDraft(detail)
assert.deepEqual(draft, { '1013': 'investment', '1025': null })
assert.equal(hasPayrollDraftChanges(detail, draft), false)
assert.equal(hasPayrollDraftChanges(detail, { ...draft, '1025': 'investment' }), true)
assert.equal(hasPayrollDraftChanges(detail, { ...draft, '1013': null }), true, '改回按分店也是改动')
// 只提交有改动的分店；未改动的不带（避免现有覆盖被误挪到所选生效日）
assert.deepEqual(buildPayrollSaveItems(detail, { ...draft, '1013': null, '1025': 'investment' }), [
  { storeCode: '1013', connectionGuid: null },
  { storeCode: '1025', connectionGuid: 'investment' },
])
assert.deepEqual(buildPayrollSaveItems(detail, { ...draft, '1025': 'investment' }), [{ storeCode: '1025', connectionGuid: 'investment' }])
assert.deepEqual(buildPayrollSaveItems(detail, draft), [], '没有改动时不产生 items')
// 草稿里缺的分店按「按分店」处理：1013 现有覆盖 → 视为改回按分店
assert.deepEqual(buildPayrollSaveItems(detail, {}), [{ storeCode: '1013', connectionGuid: null }])
assert.deepEqual(findUnboundPayrollCompanies(draft, detail.bindingStates), ['investment'])
assert.deepEqual(findUnboundPayrollCompanies({ '1013': 'investment', '1025': 'investment' }, detail.bindingStates), ['investment'], '去重')
assert.deepEqual(findUnboundPayrollCompanies({ '1013': null, '1025': null }, detail.bindingStates), [])
assert.deepEqual(findUnboundPayrollCompanies({ '1013': 'australian' }, detail.bindingStates), [])
assert.deepEqual(findUnboundPayrollCompanies({ '1013': 'unknown-company' }, detail.bindingStates), [], '没有绑定状态数据时不误报')

assert.deepEqual(describePayrollEmployer(detail.rows[0]), { name: 'Investment', kind: 'override' })
assert.deepEqual(describePayrollEmployer(detail.rows[1]), { name: 'Australian', kind: 'default' })
assert.deepEqual(
  describePayrollEmployer(payrollRow('9', { effectiveConnectionName: null, defaultConnectionName: null })),
  { name: '', kind: 'default' },
)
assert.equal(formatStoreLabel('1013', 'Orion'), '1013 Orion')
assert.equal(formatStoreLabel('1025', ''), '1025')
assert.equal(formatStoreLabel('1025', '1025'), '1025')
assert.deepEqual(getOtherStoresOfUser(detail.rows, detail.rows[0]), [{ label: '1025', companyName: 'Australian' }])
assert.equal(getOtherStoresOfUser(detail.rows, { ...detail.rows[0], userGuid: 'someone-else' }).length, 0, '只看同一员工的其他分店')

// ───────────── 服务层 normalize：后端缺字段 / null 不崩 ─────────────

const emptySnapshot = normalizeMyobBindingSnapshot(null)
assert.equal(emptySnapshot.rows.length, 0)
assert.equal(emptySnapshot.counts.all, 0)
assert.equal(emptySnapshot.fetchedAt, null)
assert.equal(normalizeMyobBindingSnapshot(undefined).onlyInMyob.length, 0)

const snapshot = normalizeMyobBindingSnapshot({
  connectionGuid: 'c1',
  connectionName: 'Investment',
  fetchedAt: '2026-10-28T04:32:00',
  counts: { all: 3, suggested: 1, needsReview: 1, unmatched: 1, batchConfirmable: 1 },
  rows: [
    { userGuid: 'a', userName: 'A', status: 'Suggested', candidate: { card: { uid: 'k1', name: 'A' }, score: 91, basis: { email: 'match' }, notes: ['手机不一致'] } },
    // Suggested 却没有候选：降级为未匹配，操作按钮不会拿到 null
    { userGuid: 'b', userName: 'B', status: 'Suggested', candidate: null },
    // 未知状态 → 未匹配
    { userGuid: 'c', userName: 'C', status: 'WeirdNewStatus' },
    // 已绑定却没有 link → 未匹配
    { userGuid: 'd', userName: 'D', status: 'Bound' },
    { userName: 'no guid' },
    null,
  ],
  onlyInOurs: [{ userGuid: 'o1', userName: 'O', copyText: 'O\n1' }, {}],
  onlyInMyob: [{ card: { uid: 'm1', name: 'M' } }, { card: {} }],
  typeIssues: [{ userGuid: 't1', userName: 'T', issueCode: 'MISSING_LEAVE_ENTITLEMENT', hasAnnualEntitlement: true }],
})
assert.deepEqual(snapshot.rows.map((r) => [r.userGuid, r.status]), [
  ['a', 'Suggested'],
  ['b', 'Unmatched'],
  ['c', 'Unmatched'],
  ['d', 'Unmatched'],
])
assert.equal(snapshot.rows[0].candidate?.basis.email, 'match')
assert.equal(snapshot.rows[0].candidate?.basis.phone, 'unknown', '缺失的信号按无法比对')
assert.equal(snapshot.rows[0].candidate?.card.isActive, true, '缺 isActive 按启用')
assert.deepEqual(snapshot.rows[0].candidate?.notes, ['手机不一致'])
assert.equal(snapshot.counts.needsReview, 1)
assert.equal(snapshot.counts.onlyInMyob, 0)
assert.equal(snapshot.onlyInOurs.length, 1)
assert.equal(snapshot.onlyInOurs[0].copyText, 'O\n1')
assert.equal(snapshot.onlyInMyob.length, 1, '没有 uid 的 MYOB 卡丢弃')
assert.equal(snapshot.typeIssues[0].hasSickEntitlement, false)

const candidates = normalizeMyobBindingCandidates({
  userGuid: 'u',
  userName: 'U',
  candidates: [{ card: { uid: 'x', name: 'X' }, score: 'nan', boundToUserGuid: 'g', boundToUserName: 'G' }, { card: {} }, 7],
})
assert.equal(candidates.candidates.length, 1)
assert.equal(candidates.candidates[0].score, 0)
assert.equal(candidates.candidates[0].boundToUserName, 'G')
assert.equal(candidates.email, null)
assert.deepEqual(normalizeMyobBindingCandidates(null).candidates, [])

assert.deepEqual(normalizeMyobConfirmResult(null), { confirmed: 0, failed: [] })
assert.deepEqual(normalizeMyobConfirmResult({ confirmed: 2, failed: [{ userGuid: 'u', code: 'X', message: 'm' }, null] }), {
  confirmed: 2,
  failed: [{ userGuid: 'u', code: 'X', message: 'm' }],
})

const payrollList = normalizeMyobPayrollEmployerList({
  rows: [{ userGuid: 'p', userName: 'P', storeCode: '1', isOverride: true, effectiveFrom: '2026-10-26' }, {}],
  companies: [{ connectionGuid: 'c', name: 'C' }, {}],
})
assert.equal(payrollList.rows.length, 1)
assert.equal(payrollList.rows[0].defaultConnectionGuid, null)
assert.equal(payrollList.rows[0].isOverride, true)
assert.equal(payrollList.companies.length, 1)
assert.deepEqual(normalizeMyobPayrollEmployerList(undefined), { rows: [], companies: [] })

const payrollDetail = normalizeMyobPayrollEmployerDetail({
  userGuid: 'p',
  allowedEffectiveFrom: ['2026-10-26', 5, ''],
  history: [{ storeCode: '1', effectiveFrom: '2026-10-26', connectionGuid: null }],
  bindingStates: [{ connectionGuid: 'c', bound: true }, { bound: true }],
})
assert.deepEqual(payrollDetail.allowedEffectiveFrom, ['2026-10-26'])
assert.equal(payrollDetail.history[0].connectionName, null, '改回按分店：connectionGuid 为 null')
assert.equal(payrollDetail.bindingStates.length, 1)
assert.deepEqual(normalizeMyobPayrollEmployerDetail(null).rows, [])

// 分店标签摘要：总部账号挂几十个分店，只显示前几个，其余 +N
assert.equal(summarizeStoreLabels([]), '')
assert.equal(summarizeStoreLabels(['1003 A']), '1003 A')
assert.equal(summarizeStoreLabels(['1 A', '2 B', '3 C']), '1 A / 2 B / 3 C')
assert.equal(summarizeStoreLabels(['1 A', '2 B', '3 C', '4 D', '5 E']), '1 A / 2 B / 3 C +2')
assert.equal(summarizeStoreLabels(['1 A', '2 B', '3 C'], 2), '1 A / 2 B +1')
assert.equal(summarizeStoreLabels(['1 A', '2 B'], 0), '1 A +1', 'limit 至少为 1')

console.log('myobEmployeeLogic.test: ok')
