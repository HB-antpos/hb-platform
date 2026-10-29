// MYOB 历史工资单（只读）。契约以后端 services/backend/BlazorApp.Api/Services/Myob/MyobPayslipDtos.cs 为准（camelCase）。
// 隐私：年薪、时薪、生日、支票号、银行 / TFN 后端从不返回，这里也没有对应字段。

/** 工资单归类：有考勤分店 / 按当前分店推断 / 无法确定分店 / 员工卡未绑定。 */
export type MyobPayslipPlacement = 'Attendance' | 'Inferred' | 'Unknown' | 'Unbound'

export interface MyobPayslipStore {
  storeCode: string
  storeLabel: string
}

export interface MyobPayslipCompany {
  connectionGuid: string
  connectionName: string
}

/** 某公司文件这次读取的状态：一家失败不拖垮另一家。 */
export interface MyobPayslipCompanyStatus {
  connectionGuid: string
  connectionName: string
  ok: boolean
  count: number
  errorCode: string | null
  errorMessage: string | null
}

export interface MyobPayslipRow {
  connectionGuid: string
  companyName: string
  /** MYOB 员工卡 UID：只用于打开抽屉，页面不显示。 */
  employeeUid: string
  employeeName: string
  displayId: string | null
  /** 我方员工 GUID；未绑定时为 null。 */
  userGuid: string | null
  paymentDate: string
  periodStart: string | null
  periodEnd: string | null
  payFrequency: string | null
  grossPay: number | null
  /** 税行金额合计（MYOB 里为负数）。 */
  taxTotal: number
  netPay: number | null
  placement: MyobPayslipPlacement
  stores: MyobPayslipStore[]
}

export interface MyobPayslipList {
  from: string
  to: string
  companies: MyobPayslipCompanyStatus[]
  rows: MyobPayslipRow[]
  totalCount: number
  employeeCount: number
  grossTotal: number
  netTotal: number
  /** 无法归到分店的工资单张数；仅「全部分店」权限才有值，否则恒为 0。 */
  unassignedCount: number
  canViewAllStores: boolean
  fetchedAt: string | null
}

export interface MyobPayslipLine {
  description: string | null
  hours: number | null
  rate: number | null
  amount: number | null
  yearToDate: number | null
}

export interface MyobPayslipDetail {
  connectionGuid: string
  companyName: string
  companyAbn: string | null
  employeeName: string
  displayId: string | null
  userGuid: string | null
  paymentDate: string
  periodStart: string | null
  periodEnd: string | null
  payFrequency: string | null
  grossPay: number | null
  netPay: number | null
  superFundName: string | null
  placement: MyobPayslipPlacement
  stores: MyobPayslipStore[]
  wages: MyobPayslipLine[]
  taxes: MyobPayslipLine[]
  deductions: MyobPayslipLine[]
  superannuation: MyobPayslipLine[]
  entitlements: MyobPayslipLine[]
  others: MyobPayslipLine[]
  samePaymentDateCount: number
}

export interface MyobPayslipStoreHours {
  storeCode: string
  storeLabel: string
  workHours: number
  leaveHours: number
  failed: boolean
}

export interface MyobPayslipHours {
  periodStart: string | null
  periodEnd: string | null
  stores: MyobPayslipStoreHours[]
  totalWorkHours: number
  totalLeaveHours: number
}

export interface MyobPayslipQuery {
  connectionGuid?: string
  storeCode?: string
  /** yyyy-MM-dd，含首尾；都不传时后端默认最近 90 天。 */
  from?: string
  to?: string
  keyword?: string
  onlyUnassigned?: boolean
  /** 绕过后端 10 分钟缓存重新读 MYOB。 */
  refresh?: boolean
}

/** 打开单张工资单所需的键。periodStart 只在同一支付日有多张时用来区分。 */
export interface MyobPayslipKey {
  connectionGuid: string
  employeeUid: string
  paymentDate: string
  periodStart?: string | null
}
