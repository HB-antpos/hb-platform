import assert from 'node:assert/strict'

import { normalizeMyobCompanies, normalizeMyobTimesheetPreview } from '../../../services/myobService'

import { buildPayrollPushCells, getPayrollPushButton, pickCompanyForStore } from './payrollMyobPushLogic'

const companies = normalizeMyobCompanies([
  { connectionGuid: 'c1', name: 'Investment', storeCodes: ['1014', '1002'] },
  { connectionGuid: 'c2', name: 'Australian', storeCodes: ['1013'] },
])

// 分店 → 公司（忽略大小写与空白；没映射返回 null）
assert.equal(pickCompanyForStore(companies, '1002')?.connectionGuid, 'c1')
assert.equal(pickCompanyForStore(companies, ' 1013 ')?.connectionGuid, 'c2')
assert.equal(pickCompanyForStore(companies, '1042'), null)
assert.equal(pickCompanyForStore(companies, undefined), null)
assert.equal(pickCompanyForStore([], '1002'), null)

// 按钮状态
const base = { allowed: true, locked: true, status: 'ready' as const, pushEnabled: true }
assert.deepEqual(getPayrollPushButton({ ...base, allowed: false }), { kind: 'hidden' }, '没权限不显示')
assert.deepEqual(getPayrollPushButton({ ...base, locked: false, status: 'idle', pushEnabled: undefined }), { kind: 'needLock' }, '未锁定：灰色提示，不依赖预览')
assert.deepEqual(getPayrollPushButton({ ...base, status: 'loading', pushEnabled: undefined }), { kind: 'loading' })
assert.deepEqual(getPayrollPushButton({ ...base, status: 'idle', pushEnabled: undefined }), { kind: 'loading' }, '已锁定但还没开始读：按加载中')
assert.deepEqual(getPayrollPushButton({ ...base, status: 'unmapped', pushEnabled: undefined }), { kind: 'unmapped' })
assert.deepEqual(getPayrollPushButton({ ...base, status: 'error', pushEnabled: undefined }), { kind: 'error' })
assert.deepEqual(getPayrollPushButton({ ...base, pushEnabled: false }), { kind: 'hidden' }, '服务器没对该公司开放：不显示')
assert.deepEqual(getPayrollPushButton(base), { kind: 'ready' })

// 行级状态：应该推送的人显示状态，全现金 / 没转账工时的人为空
const rowOf = (userGuid: string, status: string, push: Record<string, unknown> = {}) => ({
  userGuid,
  userName: userGuid,
  status,
  push: { state: 'NotPushed', needsRepush: false, lastStatus: null, lastBatchGuid: null, lastPushedAt: null, ...push },
})
const preview = normalizeMyobTimesheetPreview({
  connectionGuid: 'c1',
  connectionName: 'Investment',
  periodStart: '2026-11-09',
  periodEnd: '2026-11-15',
  weeks: 1,
  periods: [],
  rows: [
    rowOf('ready-new', 'Ready'),
    rowOf('ready-pushed', 'Ready', { state: 'Pushed', lastStatus: 'Pushed', lastPushedAt: '2026-11-19T01:00:00Z' }),
    rowOf('blocked', 'Blocked'),
    rowOf('cash', 'NoTransfer'),
    rowOf('cash-old', 'NoTransfer', { state: 'Pushed', lastStatus: 'Pushed', lastPushedAt: '2026-11-18T01:00:00Z' }),
  ],
})
const cells = buildPayrollPushCells(preview)
assert.equal(cells['ready-new']?.view.labelKey, 'myob.push.pushState.NotPushed')
assert.equal(cells['ready-pushed']?.view.labelKey, 'myob.push.pushState.Pushed')
assert.equal(cells['blocked']?.view.labelKey, 'myob.push.pushState.NotPushed', '被拦截的人也显示未推送（原因在弹窗里）')
assert.equal(cells['cash'], undefined, '没有转账工时不推送：列里不显示')
assert.equal(cells['cash-old']?.view.labelKey, 'myob.push.pushState.Pushed', '有推送记录的一定显示，哪怕现在没有转账工时')
assert.deepEqual(buildPayrollPushCells(null), {})

console.log('payrollMyobPushLogic.test: ok')
