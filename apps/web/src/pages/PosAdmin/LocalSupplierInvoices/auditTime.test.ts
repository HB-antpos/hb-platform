import { readFileSync } from 'node:fs'
import path from 'node:path'
import { formatLocalSupplierInvoiceAuditTime, formatLocalSupplierInvoiceAuditTimeCompact } from './auditTime'

function assertEqual<T>(actual: T, expected: T, message: string) {
  if (actual !== expected) {
    throw new Error(`${message}. Expected: ${String(expected)}, received: ${String(actual)}`)
  }
}

function formatExpectedLocalTime(value: string) {
  return new Date(value).toLocaleString('zh-CN', { hour12: false })
}

const utcTimestamp = '2026-07-28T02:34:56Z'
const expectedUtcLocalTime = formatExpectedLocalTime(utcTimestamp)

assertEqual(
  formatLocalSupplierInvoiceAuditTime('2026-07-28T02:34:56'),
  expectedUtcLocalTime,
  '旧无后缀审计时间应按 UTC 解析后显示为浏览器本地时间',
)

assertEqual(
  formatLocalSupplierInvoiceAuditTime(utcTimestamp),
  expectedUtcLocalTime,
  '带 Z 的审计时间应按响应时区解析后显示为浏览器本地时间',
)

const offsetTimestamp = '2026-07-28T12:34:56+10:00'
assertEqual(
  formatLocalSupplierInvoiceAuditTime(offsetTimestamp),
  formatExpectedLocalTime(offsetTimestamp),
  '带 offset 的审计时间应保留响应时区语义后显示为浏览器本地时间',
)

assertEqual(
  formatLocalSupplierInvoiceAuditTime('not-a-date'),
  'not-a-date',
  '非法审计时间应保留原文便于排查',
)
assertEqual(formatLocalSupplierInvoiceAuditTime(undefined), '--', 'undefined 审计时间应显示占位')
assertEqual(formatLocalSupplierInvoiceAuditTime(null), '--', 'null 审计时间应显示占位')
assertEqual(formatLocalSupplierInvoiceAuditTime(''), '--', '空审计时间应显示占位')

// 紧凑格式：解析口径与完整格式一致，只改展示长度。
const compactSource = new Date('2026-07-28T02:34:56Z')
const pad = (value: number) => String(value).padStart(2, '0')
assertEqual(
  formatLocalSupplierInvoiceAuditTimeCompact('2026-07-28T02:34:56', new Date(2026, 9, 6)),
  `${pad(compactSource.getMonth() + 1)}-${pad(compactSource.getDate())} ${pad(compactSource.getHours())}:${pad(compactSource.getMinutes())}`,
  '今年的审计时间应显示月-日 时:分，且无后缀时间按 UTC 解析',
)
assertEqual(
  formatLocalSupplierInvoiceAuditTimeCompact('2025-12-09T04:02:00Z', new Date(2026, 9, 6)),
  `${new Date('2025-12-09T04:02:00Z').getFullYear()}-${pad(new Date('2025-12-09T04:02:00Z').getMonth() + 1)}-${pad(new Date('2025-12-09T04:02:00Z').getDate())}`,
  '往年的审计时间只显示日期',
)
assertEqual(formatLocalSupplierInvoiceAuditTimeCompact(undefined), '--', '紧凑格式空值应显示占位')
assertEqual(formatLocalSupplierInvoiceAuditTimeCompact('not-a-date'), 'not-a-date', '紧凑格式非法值应保留原文')

const pageFile = path.resolve(process.cwd(), 'src/pages/PosAdmin/LocalSupplierInvoices/index.tsx')
const pageSource = readFileSync(pageFile, 'utf8')

// 创建 / 最后修改列合并了操作人：时间仍用审计时间工具，操作人以弱化文字跟在后面。
for (const [field, userField] of [['createdAt', 'createdBy'], ['updatedAt', 'updatedBy']]) {
  const auditColumnPattern = new RegExp(
    `dataIndex:\\s*'${field}'[\\s\\S]*?render:\\s*\\(v:\\s*string,\\s*record\\)\\s*=>\\s*\\(\\s*<span[^>]*>\\s*\\{formatLocalSupplierInvoiceAuditTimeCompact\\(v\\)\\}\\s*\\{record\\.${userField}`,
  )
  assertEqual(
    auditColumnPattern.test(pageSource),
    true,
    `${field} 列应使用审计时间工具`,
  )
}

for (const field of ['orderDate', 'inboundDate']) {
  const naturalDateColumnPattern = new RegExp(
    `dataIndex:\\s*'${field}'[\\s\\S]*?render:\\s*\\(v:\\s*string\\)\\s*=>\\s*formatDate\\(v\\)`,
  )
  assertEqual(
    naturalDateColumnPattern.test(pageSource),
    true,
    `${field} 列应继续使用自然日期格式化`,
  )
}

console.log('LocalSupplierInvoices audit time tests: ok')
