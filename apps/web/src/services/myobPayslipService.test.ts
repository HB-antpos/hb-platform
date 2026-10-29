import assert from 'node:assert/strict'

import {
  buildMyobPayslipEmailBody,
  buildMyobPayslipParams,
  downloadMyobPayslipPdf,
  getMyobPayslipDetail,
  getMyobPayslipEmailTarget,
  getMyobPayslipHours,
  getMyobPayslips,
  normalizeMyobPayslipCompanies,
  normalizeMyobPayslipDetail,
  normalizeMyobPayslipEmailTarget,
  normalizeMyobPayslipHours,
  normalizeMyobPayslipList,
  normalizeMyobPayslipPlacement,
  normalizeMyobPayslipRow,
  parseContentDispositionFilename,
  sendMyobPayslipEmail,
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
  hourlyRate: 24,
  referenceNumber: '1263',
  canDownloadPdf: true,
  canSendEmail: 'yes',
  lines: [
    { description: 'Base Hourly', type: 'Wage', typeLabel: 'Wages', hours: 30, rate: 24, amount: 720, yearToDate: 11520 },
    { description: 'Annual Leave Accrual', type: 'Entitlement', typeLabel: 'Entitlement', hours: 1.15, yearToDate: 18.45 },
    'junk',
  ],
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

// 纸面新增字段：时薪 / Reference Number / 扁平明细；Entitlement 行 amount 为空（小时数在 hours）。
assert.equal(detail.hourlyRate, 24)
assert.equal(detail.referenceNumber, '1263')
assert.equal(detail.lines.length, 3) // 非对象的脏数据按空行保留位置不丢（normalizeFlatLine 对任何输入都返回行）
assert.equal(detail.lines[0].typeLabel, 'Wages')
assert.equal(detail.lines[1].amount, null)
assert.equal(detail.lines[1].hours, 1.15)
assert.equal(detail.lines[1].yearToDate, 18.45)
assert.equal(detail.lines[2].description, null)
// 权限开关只认明确的 true；缺字段（旧后端）/ 类型异常一律 false。
assert.equal(detail.canDownloadPdf, true)
assert.equal(detail.canSendEmail, false)
assert.equal(normalizeMyobPayslipDetail(undefined).canDownloadPdf, false)
assert.deepEqual(normalizeMyobPayslipDetail(undefined).lines, [])
assert.equal(normalizeMyobPayslipDetail(undefined).hourlyRate, null)
assert.equal(normalizeMyobPayslipDetail(undefined).referenceNumber, null)

// 隐私：前端模型里没有年薪 / 生日 / 支票号 / 银行 / TFN 字段，后端即使误返回也不会被带进来（时薪只在单张明细里用于还原 MYOB 纸面）。
const leaked = normalizeMyobPayslipDetail({
  employeeName: 'Jane',
  annualSalary: 98765,
  dateOfBirth: '1990-02-03',
  chequeNumber: 'CHQ1',
  bsb: '063010',
  accountNumber: '12345678',
  tfn: '123456789',
})
const serialized = JSON.stringify(leaked)
for (const banned of ['98765', '1990-02-03', 'CHQ1', '063010', '12345678', '123456789']) {
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

// Content-Disposition 文件名：优先 filename*，其次 filename；去掉路径分隔符。
assert.equal(parseContentDispositionFilename('attachment; filename="Payslip_ALEX-MORGAN_2026-10-29.pdf"'), 'Payslip_ALEX-MORGAN_2026-10-29.pdf')
assert.equal(parseContentDispositionFilename("attachment; filename=a.pdf; filename*=UTF-8''%E5%B7%A5%E8%B5%84.pdf"), '工资.pdf')
assert.equal(parseContentDispositionFilename('attachment; filename="../evil.pdf"'), '.._evil.pdf')
assert.equal(parseContentDispositionFilename('attachment'), null)
assert.equal(parseContentDispositionFilename(null), null)

// 邮件目标：source 只认 profile，其余按「档案无邮箱」；缺字段不崩。
assert.deepEqual(normalizeMyobPayslipEmailTarget({ email: 'alex.morgan@example.com', maskedEmail: 'a***@example.com', source: 'profile', subject: 'S', filename: 'f.pdf', myEmail: 'boss@example.com' }), {
  email: 'alex.morgan@example.com',
  maskedEmail: 'a***@example.com',
  source: 'profile',
  subject: 'S',
  filename: 'f.pdf',
  myEmail: 'boss@example.com',
})
assert.equal(normalizeMyobPayslipEmailTarget({ source: 'weird', email: '  ' }).source, 'none')
assert.equal(normalizeMyobPayslipEmailTarget({ source: 'weird', email: '  ' }).email, null)
assert.equal(normalizeMyobPayslipEmailTarget(null).subject, '')

// 发信请求体：periodStart 空 → null，附言去空白、空 → null，收件人去空白。
assert.deepEqual(
  buildMyobPayslipEmailBody({ connectionGuid: 'c1', employeeUid: 'e1', paymentDate: '2026-10-29', to: ' a@example.com ', copyToMe: true, note: '  ', recipientChangeConfirmed: false }),
  { connectionGuid: 'c1', employeeUid: 'e1', paymentDate: '2026-10-29', periodStart: null, to: 'a@example.com', copyToMe: true, note: null, recipientChangeConfirmed: false },
)
assert.equal(buildMyobPayslipEmailBody({ connectionGuid: 'c1', employeeUid: 'e1', paymentDate: '2026-10-29', periodStart: '2026-10-19', to: 'a@example.com', copyToMe: false, note: ' hi ', recipientChangeConfirmed: true }).note, 'hi')

// 请求路径与参数（用假 fetch 拦截）。
const originalFetch = globalThis.fetch
const calls: { url: string; method: string; body: unknown }[] = []
let body: unknown = null
let pdfResponse: (() => Response) | null = null
globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
  calls.push({ url: String(input), method: init?.method ?? 'GET', body: typeof init?.body === 'string' ? JSON.parse(init.body) : null })
  if (pdfResponse && String(input).includes('/pdf?')) {
    return pdfResponse()
  }
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

  // 下载 PDF：以 Blob 取回（不被当文本破坏二进制），文件名取响应头；缺响应头用兜底名。
  const pdfBytes = new Uint8Array([0x25, 0x50, 0x44, 0x46, 0x2d, 0xff, 0x00, 0x80])
  pdfResponse = () =>
    new Response(pdfBytes, { status: 200, headers: { 'content-type': 'application/pdf', 'content-disposition': 'attachment; filename="Payslip_ALEX-MORGAN_2026-10-29.pdf"' } })
  const file = await downloadMyobPayslipPdf({ connectionGuid: 'c1', employeeUid: 'e1', paymentDate: '2026-10-29', periodStart: '2026-10-19' }, 'fallback.pdf')
  assert.ok(calls[3].url.startsWith('/api/react/v1/myob/payslips/pdf?'))
  assert.ok(calls[3].url.includes('periodStart=2026-10-19'))
  assert.equal(file.filename, 'Payslip_ALEX-MORGAN_2026-10-29.pdf')
  assert.deepEqual(new Uint8Array(await file.blob.arrayBuffer()), pdfBytes)
  pdfResponse = () => new Response(pdfBytes, { status: 200, headers: { 'content-type': 'application/pdf' } })
  assert.equal((await downloadMyobPayslipPdf({ connectionGuid: 'c1', employeeUid: 'e1', paymentDate: '2026-10-29' }, 'fallback.pdf')).filename, 'fallback.pdf')
  // 失败时后端返回 JSON ApiResponse：错误码可从异常里读到，而不是把它当 PDF。
  pdfResponse = () =>
    new Response(JSON.stringify({ success: false, code: 'MYOB_PAYSLIP_FORBIDDEN', message: '没有权限' }), { status: 403, headers: { 'content-type': 'application/json' } })
  await assert.rejects(
    () => downloadMyobPayslipPdf({ connectionGuid: 'c1', employeeUid: 'e1', paymentDate: '2026-10-29' }, 'fallback.pdf'),
    (error: unknown) => (error as { status?: number; payload?: { code?: string } }).status === 403 && (error as { payload?: { code?: string } }).payload?.code === 'MYOB_PAYSLIP_FORBIDDEN',
  )
  pdfResponse = null

  // 邮件目标 + 发送。
  body = { success: true, data: { email: 'alex.morgan@example.com', maskedEmail: 'a***@example.com', source: 'profile', subject: 'S', filename: 'f.pdf', myEmail: null } }
  const target = await getMyobPayslipEmailTarget({ connectionGuid: 'c1', employeeUid: 'e1', paymentDate: '2026-10-29' })
  assert.equal(target.source, 'profile')
  assert.ok(calls[calls.length - 1].url.startsWith('/api/react/v1/myob/payslips/email-target?'))
  body = { success: true, data: { sentTo: 'a***@example.com', copiedToMe: true } }
  const sent = await sendMyobPayslipEmail({ connectionGuid: 'c1', employeeUid: 'e1', paymentDate: '2026-10-29', to: 'alex.morgan@example.com', copyToMe: true, recipientChangeConfirmed: false })
  const last = calls[calls.length - 1]
  assert.equal(last.method, 'POST')
  assert.equal(last.url, '/api/react/v1/myob/payslips/email')
  assert.equal((last.body as { to: string }).to, 'alex.morgan@example.com')
  assert.equal((last.body as { recipientChangeConfirmed: boolean }).recipientChangeConfirmed, false)
  assert.deepEqual(sent, { sentTo: 'a***@example.com', copiedToMe: true })
} finally {
  globalThis.fetch = originalFetch
}

console.log('myobPayslipService tests passed')
