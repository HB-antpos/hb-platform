import type { ApiResponse } from '../types/api'
import type {
  MyobPayslipCompany,
  MyobPayslipCompanyStatus,
  MyobPayslipDetail,
  MyobPayslipEmailPayload,
  MyobPayslipEmailResult,
  MyobPayslipEmailTarget,
  MyobPayslipFlatLine,
  MyobPayslipHours,
  MyobPayslipKey,
  MyobPayslipLine,
  MyobPayslipList,
  MyobPayslipPdfFile,
  MyobPayslipPlacement,
  MyobPayslipQuery,
  MyobPayslipRow,
  MyobPayslipStore,
  MyobPayslipStoreHours,
} from '../types/myobPayslip'
import request, { unwrapApiData } from '../utils/request'

// 后端 MYOB 工资单控制器。查看权限：Myob.Payslip.ViewAllStores / Myob.Payslip.ViewManagedStore 任一（范围由后端按权限与管辖分店裁剪）；
// 下载 PDF / 邮件发送另需 Myob.Payslip.Download / Myob.Payslip.Email，是否可用以明细返回的 canDownloadPdf / canSendEmail 为准。
const PAYSLIP_BASE = '/api/react/v1/myob/payslips'

function text(value: unknown) {
  return typeof value === 'string' ? value : ''
}

function nullableText(value: unknown) {
  return typeof value === 'string' && value.trim() ? value : null
}

function record(value: unknown): Record<string, unknown> {
  return value && typeof value === 'object' ? (value as Record<string, unknown>) : {}
}

function list<T>(value: unknown, mapper: (raw: unknown) => T | null): T[] {
  return Array.isArray(value) ? value.map(mapper).filter((item): item is T => item !== null) : []
}

/** 金额 / 小时：只认有限数字，其余（缺失、字符串、NaN）按 null，界面显示「—」而不是 0。 */
function nullableNumber(value: unknown): number | null {
  return typeof value === 'number' && Number.isFinite(value) ? value : null
}

function number(value: unknown, fallback = 0): number {
  return nullableNumber(value) ?? fallback
}

const PLACEMENTS: readonly MyobPayslipPlacement[] = ['Attendance', 'Inferred', 'Unknown', 'Unbound']

/** 未知值按「无法确定分店」处理：偏保守，不会把不认识的归类当成已归到分店。 */
export function normalizeMyobPayslipPlacement(value: unknown): MyobPayslipPlacement {
  return PLACEMENTS.find((item) => item === value) ?? 'Unknown'
}

function normalizeStore(raw: unknown): MyobPayslipStore | null {
  const item = record(raw)
  const storeCode = text(item.storeCode)
  return storeCode ? { storeCode, storeLabel: text(item.storeLabel) || storeCode } : null
}

export function normalizeMyobPayslipCompanies(payload: unknown): MyobPayslipCompany[] {
  return list(payload, (raw) => {
    const item = record(raw)
    const connectionGuid = text(item.connectionGuid)
    return connectionGuid ? { connectionGuid, connectionName: text(item.connectionName) } : null
  })
}

function normalizeCompanyStatus(raw: unknown): MyobPayslipCompanyStatus | null {
  const item = record(raw)
  const connectionGuid = text(item.connectionGuid)
  if (!connectionGuid) {
    return null
  }
  return {
    connectionGuid,
    connectionName: text(item.connectionName),
    ok: item.ok === true,
    count: number(item.count),
    errorCode: nullableText(item.errorCode),
    errorMessage: nullableText(item.errorMessage),
  }
}

export function normalizeMyobPayslipRow(raw: unknown): MyobPayslipRow | null {
  const item = record(raw)
  // 没有员工卡或支付日期的行无法打开明细，丢弃。
  if (!text(item.employeeUid) || !text(item.paymentDate)) {
    return null
  }
  return {
    connectionGuid: text(item.connectionGuid),
    companyName: text(item.companyName),
    employeeUid: text(item.employeeUid),
    employeeName: text(item.employeeName),
    displayId: nullableText(item.displayId),
    userGuid: nullableText(item.userGuid),
    paymentDate: text(item.paymentDate),
    periodStart: nullableText(item.periodStart),
    periodEnd: nullableText(item.periodEnd),
    payFrequency: nullableText(item.payFrequency),
    grossPay: nullableNumber(item.grossPay),
    taxTotal: number(item.taxTotal),
    netPay: nullableNumber(item.netPay),
    placement: normalizeMyobPayslipPlacement(item.placement),
    stores: list(item.stores, normalizeStore),
  }
}

export function normalizeMyobPayslipList(payload: unknown): MyobPayslipList {
  const data = record(payload)
  return {
    from: text(data.from),
    to: text(data.to),
    companies: list(data.companies, normalizeCompanyStatus),
    rows: list(data.rows, normalizeMyobPayslipRow),
    totalCount: number(data.totalCount),
    employeeCount: number(data.employeeCount),
    grossTotal: number(data.grossTotal),
    netTotal: number(data.netTotal),
    unassignedCount: number(data.unassignedCount),
    canViewAllStores: data.canViewAllStores === true,
    fetchedAt: nullableText(data.fetchedAt),
  }
}

function normalizeLine(raw: unknown): MyobPayslipLine | null {
  const item = record(raw)
  return {
    description: nullableText(item.description),
    hours: nullableNumber(item.hours),
    rate: nullableNumber(item.rate),
    amount: nullableNumber(item.amount),
    yearToDate: nullableNumber(item.yearToDate),
  }
}

function normalizeFlatLine(raw: unknown): MyobPayslipFlatLine | null {
  const item = record(raw)
  return {
    description: nullableText(item.description),
    type: nullableText(item.type),
    typeLabel: nullableText(item.typeLabel),
    hours: nullableNumber(item.hours),
    rate: nullableNumber(item.rate),
    amount: nullableNumber(item.amount),
    yearToDate: nullableNumber(item.yearToDate),
  }
}

export function normalizeMyobPayslipDetail(payload: unknown): MyobPayslipDetail {
  const data = record(payload)
  return {
    connectionGuid: text(data.connectionGuid),
    companyName: text(data.companyName),
    companyAbn: nullableText(data.companyAbn),
    employeeName: text(data.employeeName),
    displayId: nullableText(data.displayId),
    userGuid: nullableText(data.userGuid),
    paymentDate: text(data.paymentDate),
    periodStart: nullableText(data.periodStart),
    periodEnd: nullableText(data.periodEnd),
    payFrequency: nullableText(data.payFrequency),
    grossPay: nullableNumber(data.grossPay),
    netPay: nullableNumber(data.netPay),
    superFundName: nullableText(data.superFundName),
    hourlyRate: nullableNumber(data.hourlyRate),
    referenceNumber: nullableText(data.referenceNumber),
    placement: normalizeMyobPayslipPlacement(data.placement),
    stores: list(data.stores, normalizeStore),
    wages: list(data.wages, normalizeLine),
    taxes: list(data.taxes, normalizeLine),
    deductions: list(data.deductions, normalizeLine),
    superannuation: list(data.superannuation, normalizeLine),
    entitlements: list(data.entitlements, normalizeLine),
    others: list(data.others, normalizeLine),
    lines: list(data.lines, normalizeFlatLine),
    samePaymentDateCount: number(data.samePaymentDateCount),
    // 权限开关只认明确的 true：字段缺失（旧后端）或类型异常一律不显示按钮。
    canDownloadPdf: data.canDownloadPdf === true,
    canSendEmail: data.canSendEmail === true,
  }
}

function normalizeStoreHours(raw: unknown): MyobPayslipStoreHours | null {
  const item = record(raw)
  const storeCode = text(item.storeCode)
  return storeCode
    ? {
        storeCode,
        storeLabel: text(item.storeLabel) || storeCode,
        workHours: number(item.workHours),
        leaveHours: number(item.leaveHours),
        failed: item.failed === true,
      }
    : null
}

export function normalizeMyobPayslipHours(payload: unknown): MyobPayslipHours {
  const data = record(payload)
  return {
    periodStart: nullableText(data.periodStart),
    periodEnd: nullableText(data.periodEnd),
    stores: list(data.stores, normalizeStoreHours),
    totalWorkHours: number(data.totalWorkHours),
    totalLeaveHours: number(data.totalLeaveHours),
  }
}

/** 当前账号可见的公司文件（用于下拉）。 */
export async function getMyobPayslipCompanies(): Promise<MyobPayslipCompany[]> {
  const response = await request.get<ApiResponse<unknown>>(`${PAYSLIP_BASE}/companies`)
  return normalizeMyobPayslipCompanies(unwrapApiData(response))
}

/** 把查询条件整理成请求参数：空值一律不传，布尔只在为 true 时传。 */
export function buildMyobPayslipParams(query: MyobPayslipQuery) {
  return {
    connectionGuid: query.connectionGuid || undefined,
    storeCode: query.storeCode || undefined,
    from: query.from || undefined,
    to: query.to || undefined,
    keyword: query.keyword?.trim() || undefined,
    onlyUnassigned: query.onlyUnassigned ? true : undefined,
    refresh: query.refresh ? true : undefined,
  }
}

export async function getMyobPayslips(query: MyobPayslipQuery = {}): Promise<MyobPayslipList> {
  const response = await request.get<ApiResponse<unknown>>(PAYSLIP_BASE, { params: buildMyobPayslipParams(query) })
  return normalizeMyobPayslipList(unwrapApiData(response))
}

function keyParams(key: MyobPayslipKey) {
  return {
    connectionGuid: key.connectionGuid,
    employeeUid: key.employeeUid,
    paymentDate: key.paymentDate,
    periodStart: key.periodStart || undefined,
  }
}

/** 单张工资单明细；成功打开会在后端留下一条查看记录（只记谁看了谁哪一期，不含金额）。 */
export async function getMyobPayslipDetail(key: MyobPayslipKey): Promise<MyobPayslipDetail> {
  const response = await request.get<ApiResponse<unknown>>(`${PAYSLIP_BASE}/detail`, { params: keyParams(key) })
  return normalizeMyobPayslipDetail(unwrapApiData(response))
}

/** 这张工资单周期里「我方考勤」按分店的计薪工时（仅供参考，点击后才请求；后端约 1~3 秒，不写查看记录）。 */
export async function getMyobPayslipHours(key: MyobPayslipKey): Promise<MyobPayslipHours> {
  const response = await request.get<ApiResponse<unknown>>(`${PAYSLIP_BASE}/hours`, { params: keyParams(key) })
  return normalizeMyobPayslipHours(unwrapApiData(response))
}

/**
 * 从 Content-Disposition 取文件名：优先 RFC 5987 的 filename*=UTF-8''...，其次 filename="..."。
 * 去掉路径分隔符与控制字符，取不到返回 null（调用方用兜底名）。
 */
export function parseContentDispositionFilename(header: string | null | undefined): string | null {
  if (!header) {
    return null
  }
  let name: string | null = null
  const extended = /filename\*\s*=\s*(?:UTF-8|utf-8)''([^;]+)/.exec(header)
  if (extended) {
    try {
      name = decodeURIComponent(extended[1].trim())
    } catch {
      name = null
    }
  }
  if (!name) {
    const plain = /filename\s*=\s*(?:"([^"]+)"|([^;]+))/.exec(header)
    name = (plain?.[1] ?? plain?.[2] ?? '').trim() || null
  }
  // eslint-disable-next-line no-control-regex
  const cleaned = name?.replace(/[\\/:*?"<>|\u0000-\u001f]/g, '_').trim()
  return cleaned || null
}

/**
 * 下载工资单 PDF（会在后端留下一条下载记录，不含金额）。
 * 需要登录态，所以走统一的 request 以 Blob 取回，再由调用方触发浏览器保存（不能用 window.open 直链）。
 */
export async function downloadMyobPayslipPdf(key: MyobPayslipKey, fallbackFilename: string): Promise<MyobPayslipPdfFile> {
  let disposition: string | null = null
  const blob = await request.get<Blob>(`${PAYSLIP_BASE}/pdf`, {
    params: keyParams(key),
    responseType: 'blob',
    onResponse: (response) => {
      disposition = response.headers.get('content-disposition')
    },
  })
  return { blob, filename: parseContentDispositionFilename(disposition) ?? fallbackFilename }
}

export function normalizeMyobPayslipEmailTarget(payload: unknown): MyobPayslipEmailTarget {
  const data = record(payload)
  return {
    email: nullableText(data.email),
    maskedEmail: nullableText(data.maskedEmail),
    source: typeof data.source === 'string' && data.source.toLowerCase() === 'profile' ? 'profile' : 'none',
    subject: text(data.subject),
    filename: text(data.filename),
    myEmail: nullableText(data.myEmail),
  }
}

/** 发送邮件弹窗打开时取：档案邮箱（及是否存在）、主题、附件名、当前登录人邮箱。 */
export async function getMyobPayslipEmailTarget(key: MyobPayslipKey): Promise<MyobPayslipEmailTarget> {
  const response = await request.get<ApiResponse<unknown>>(`${PAYSLIP_BASE}/email-target`, { params: keyParams(key) })
  return normalizeMyobPayslipEmailTarget(unwrapApiData(response))
}

/** 整理发信请求体：periodStart 空值给 null，附言去首尾空白、空则给 null。 */
export function buildMyobPayslipEmailBody(payload: MyobPayslipEmailPayload) {
  return {
    connectionGuid: payload.connectionGuid,
    employeeUid: payload.employeeUid,
    paymentDate: payload.paymentDate,
    periodStart: payload.periodStart || null,
    to: payload.to.trim(),
    copyToMe: payload.copyToMe,
    note: payload.note?.trim() || null,
    recipientChangeConfirmed: payload.recipientChangeConfirmed,
  }
}

/** 发送工资单 PDF 邮件：每次一个员工、一个收件人；档案无邮箱时手填地址只用于本次，不回写档案。 */
export async function sendMyobPayslipEmail(payload: MyobPayslipEmailPayload): Promise<MyobPayslipEmailResult> {
  const response = await request.post<ApiResponse<unknown>>(`${PAYSLIP_BASE}/email`, buildMyobPayslipEmailBody(payload))
  const data = record(unwrapApiData(response))
  return { sentTo: text(data.sentTo), copiedToMe: data.copiedToMe === true }
}
