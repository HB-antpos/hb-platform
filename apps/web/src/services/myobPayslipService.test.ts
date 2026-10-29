import assert from 'node:assert/strict'

import {
  buildMyobPayslipParams,
  getMyobPayslipDetail,
  getMyobPayslipHours,
  getMyobPayslips,
  normalizeMyobPayslipCompanies,
  normalizeMyobPayslipDetail,
  normalizeMyobPayslipHours,
  normalizeMyobPayslipList,
  normalizeMyobPayslipPlacement,
  normalizeMyobPayslipRow,
} from './myobPayslipService'

// 契约：MyobPayslipDtos.cs，camelCase；后端字段缺失 / 类型异常时前端不崩，金额缺失显示为 null（界面 —），不是 0。

// 归类：未知值按「无法确定分店」，不会把不认识的归类当成已归到分店。
assert.equal(normalizeMyobPayslipPlacement('Attendance'), 'Attendance')
assert.equal(normalizeMyobPayslipPlacement('Inferred'), 'Inferred')
assert.equal(normalizeMyobPayslipPlacement('Unbound'), 'Unbound')
assert.equal(normalizeMyobPayslipPlacement('Whatever'), 'Unknown')
assert.equal(normalizeMyobPayslipPlacement(undefined), 'Unknown')

// 行：缺员工卡 UID 或支付日期的丢弃（无法打开明细）。
assert.equal(normalizeMyobPayslipRow({ paymentDate: '2026-10-23' }), null)
assert.equal(normalizeMyobPayslipRow({ employeeUid: 'e1' }), null)
const row = normalizeMyobPayslipRow({
  connectionGuid: 'c1',
  companyName: 'Investment',
  employeeUid: 'e1',
  employeeName: 'Jane',
  displayId: 'E1',
  userGuid: 'u1',
  paymentDate: '2026-10-23',
  periodStart: '2026-10-06',
  periodEnd: '2026-10-19',
  payFrequency: 'Fortnightly',
  grossPay: 1812.4,
  taxTotal: -246,
  netPay: 1566.4,
  placement: 'Inferred',
  stores: [{ storeCode: '1002', storeLabel: '1002 Wallsend' }, { storeLabel: 'no code' }, 'junk'],
})
assert.ok(row)
assert.equal(row.grossPay, 1812.4)
assert.equal(row.taxTotal, -246)
assert.equal(row.placement, 'Inferred')
assert.deepEqual(row.stores, [{ storeCode: '1002', storeLabel: '1002 Wallsend' }]) // 没有店号的丢弃

// 金额类型异常（字符串 / NaN / 缺失）→ null，不是 0。
const sloppy = normalizeMyobPayslipRow({ employeeUid: 'e1', paymentDate: '2026-10-23', grossPay: '1000', netPay: Number.NaN })
assert.ok(sloppy)
assert.equal(sloppy.grossPay, null)
assert.equal(sloppy.netPay, null)
assert.equal(sloppy.taxTotal, 0) // 税合计后端恒有值，缺失按 0
assert.equal(sloppy.placement, 'Unknown')
assert.deepEqual(sloppy.stores, [])

// 列表：整体缺字段不崩；canViewAllStores 只认 true。
const empty = normalizeMyobPayslipList(null)
assert.deepEqual(empty.rows, [])
assert.deepEqual(empty.companies, [])
assert.equal(empty.canViewAllStores, false)
assert.equal(empty.unassignedCount, 0)
assert.equal(empty.fetchedAt, null)
const full = normalizeMyobPayslipList({
  from: '2026-08-01',
  to: '2026-10-29',
  companies: [
    { connectionGuid: 'c1', connectionName: 'Investment', ok: true, count: 23 },
    { connectionGuid: 'c2', connectionName: 'Australian', ok: false, count: 0, errorCode: 'MYOB_API_CALL_FAILED', errorMessage: '返回 503' },
    { connectionName: 'no guid' },
  ],
  rows: [{ employeeUid: 'e1', paymentDate: '2026-10-23' }, {}],
  totalCount: 1,
  employeeCount: 1,
  grossTotal: 1000.5,
  netTotal: 900,
  unassignedCount: 3,
  canViewAllStores: true,
  fetchedAt: '2026-10-29T03:32:00.0000000Z',
})
assert.equal(full.companies.length, 2) // 没有 connectionGuid 的丢弃
assert.equal(full.companies[1].ok, false)
assert.equal(full.companies[1].errorMessage, '返回 503')
assert.equal(full.rows.length, 1)
assert.equal(full.unassignedCount, 3)
assert.equal(full.canViewAllStores, true)
assert.equal(normalizeMyobPayslipList({ canViewAllStores: 'true' }).canViewAllStores, false)

// 公司下拉。
assert.deepEqual(normalizeMyobPayslipCompanies([{ connectionGuid: 'c1', connectionName: 'A' }, {}, null]), [{ connectionGuid: 'c1', connectionName: 'A' }])
assert.deepEqual(normalizeMyobPayslipCompanies('oops'), [])

// 明细：各区块缺失按空数组；金额缺失为 null。
const detail = normalizeMyobPayslipDetail({
  connectionGuid: 'c1',
  companyName: 'Investment',
  companyAbn: '12345678901',
  employeeName: 'Jane',
  paymentDate: '2026-10-23',
  grossPay: 1812.4,
  netPay: 1566.4,
  superFundName: 'Sample Fund',
  placement: 'Attendance',
  stores: [{ storeCode: '1002', storeLabel: '1002 Wallsend' }],
  wages: [{ description: 'Base Hourly', hours: 56.5, rate: 28.4, amount: 1604.6, yearToDate: 19820.1 }],
  taxes: [{ description: 'PAYG Withholding', amount: -246, yearToDate: -3012 }],
  entitlements: [{ description: 'Annual Leave Accrual', hours: 4.35, amount: 4.35 }],
  others: [{}],
  samePaymentDateCount: 2,
})
assert.equal(detail.wages[0].rate, 28.4)
assert.equal(detail.taxes[0].hours, null) // 税行没有小时
assert.equal(detail.taxes[0].amount, -246)
assert.deepEqual(detail.deductions, [])
assert.deepEqual(detail.superannuation, [])
assert.equal(detail.entitlements[0].amount, 4.35)
assert.equal(detail.others.length, 1)
assert.equal(detail.samePaymentDateCount, 2)
assert.equal(detail.superFundName, 'Sample Fund')
assert.equal(normalizeMyobPayslipDetail(undefined).placement, 'Unknown')
assert.deepEqual(normalizeMyobPayslipDetail(undefined).wages, [])

// 隐私：前端模型里没有年薪 / 时薪 / 生日 / 支票号 / 银行 / TFN 字段，后端即使误返回也不会被带进来。
const leaked = normalizeMyobPayslipDetail({
  employeeName: 'Jane',
  annualSalary: 98765,
  hourlyRate: 28.4,
  dateOfBirth: '1990-02-03',
  chequeNumber: 'CHQ1',
  bsb: '063010',
  accountNumber: '12345678',
  tfn: '123456789',
})
const serialized = JSON.stringify(leaked)
for (const banned of ['98765', '28.4', '1990-02-03', 'CHQ1', '063010', '12345678', '123456789']) {
  assert.equal(serialized.includes(banned), false, `不应带出 ${banned}`)
}

// 工时。
const hours = normalizeMyobPayslipHours({
  periodStart: '2026-10-06',
  periodEnd: '2026-10-19',
  stores: [
    { storeCode: '1002', storeLabel: '1002 Wallsend', workHours: 38.5, leaveHours: 0 },
    { storeCode: '1005', storeLabel: '1005 Newcastle', failed: true },
    { storeLabel: 'no code' },
  ],
  totalWorkHours: 38.5,
  totalLeaveHours: 0,
})
assert.equal(hours.stores.length, 2)
assert.equal(hours.stores[0].workHours, 38.5)
assert.equal(hours.stores[1].failed, true)
assert.equal(hours.stores[1].workHours, 0)
assert.deepEqual(normalizeMyobPayslipHours(null).stores, [])

// 请求参数：空值一律不传；布尔只在为 true 时传。
assert.deepEqual(buildMyobPayslipParams({}), {
  connectionGuid: undefined,
  storeCode: undefined,
  from: undefined,
  to: undefined,
  keyword: undefined,
  onlyUnassigned: undefined,
  refresh: undefined,
})
assert.deepEqual(buildMyobPayslipParams({ connectionGuid: 'c1', storeCode: '1002', from: '2026-08-01', to: '2026-10-29', keyword: '  jane  ', onlyUnassigned: true, refresh: true }), {
  connectionGuid: 'c1',
  storeCode: '1002',
  from: '2026-08-01',
  to: '2026-10-29',
  keyword: 'jane',
  onlyUnassigned: true,
  refresh: true,
})
assert.equal(buildMyobPayslipParams({ keyword: '   ', onlyUnassigned: false, refresh: false }).keyword, undefined)
assert.equal(buildMyobPayslipParams({ onlyUnassigned: false }).onlyUnassigned, undefined)

// 请求路径与参数（用假 fetch 拦截）。
const originalFetch = globalThis.fetch
const calls: { url: string; method: string }[] = []
let body: unknown = null
globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
  calls.push({ url: String(input), method: init?.method ?? 'GET' })
  return new Response(JSON.stringify(body), { status: 200, headers: { 'content-type': 'application/json' } })
}) as typeof fetch
try {
  body = { success: true, data: { rows: [{ employeeUid: 'e1', paymentDate: '2026-10-23' }], companies: [], canViewAllStores: true } }
  const result = await getMyobPayslips({ connectionGuid: 'c1', from: '2026-08-01', to: '2026-10-29', onlyUnassigned: true })
  assert.equal(result.rows.length, 1)
  assert.equal(calls[0].method, 'GET')
  assert.ok(calls[0].url.startsWith('/api/react/v1/myob/payslips?'))
  assert.ok(calls[0].url.includes('connectionGuid=c1'))
  assert.ok(calls[0].url.includes('onlyUnassigned=true'))
  assert.equal(calls[0].url.includes('refresh'), false)
  assert.equal(calls[0].url.includes('keyword'), false)

  body = { success: true, data: { employeeName: 'Jane' } }
  await getMyobPayslipDetail({ connectionGuid: 'c 1', employeeUid: 'e1', paymentDate: '2026-10-23', periodStart: '2026-10-06' })
  assert.ok(calls[1].url.startsWith('/api/react/v1/myob/payslips/detail?'))
  assert.ok(calls[1].url.includes('employeeUid=e1'))
  assert.ok(calls[1].url.includes('periodStart=2026-10-06'))
  assert.ok(calls[1].url.includes('connectionGuid=c+1') || calls[1].url.includes('connectionGuid=c%201'))

  body = { success: true, data: { stores: [] } }
  await getMyobPayslipHours({ connectionGuid: 'c1', employeeUid: 'e1', paymentDate: '2026-10-23' })
  assert.ok(calls[2].url.startsWith('/api/react/v1/myob/payslips/hours?'))
  assert.equal(calls[2].url.includes('periodStart'), false)
  assert.equal(calls.length, 3)
} finally {
  globalThis.fetch = originalFetch
}

console.log('myobPayslipService tests passed')
