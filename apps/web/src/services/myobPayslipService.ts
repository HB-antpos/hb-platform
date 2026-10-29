import type { ApiResponse } from '../types/api'
import type {
  MyobPayslipCompany,
  MyobPayslipCompanyStatus,
  MyobPayslipDetail,
  MyobPayslipHours,
  MyobPayslipKey,
  MyobPayslipLine,
  MyobPayslipList,
  MyobPayslipPlacement,
  MyobPayslipQuery,
  MyobPayslipRow,
  MyobPayslipStore,
  MyobPayslipStoreHours,
} from '../types/myobPayslip'
import request, { unwrapApiData } from '../utils/request'

// 后端 MYOB 工资单控制器（只读）。权限：Myob.Payslip.ViewAllStores / Myob.Payslip.ViewManagedStore 任一；范围由后端按权限与管辖分店裁剪。
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
    placement: normalizeMyobPayslipPlacement(data.placement),
    stores: list(data.stores, normalizeStore),
    wages: list(data.wages, normalizeLine),
    taxes: list(data.taxes, normalizeLine),
    deductions: list(data.deductions, normalizeLine),
    superannuation: list(data.superannuation, normalizeLine),
    entitlements: list(data.entitlements, normalizeLine),
    others: list(data.others, normalizeLine),
    samePaymentDateCount: number(data.samePaymentDateCount),
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
