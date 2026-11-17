// MYOB 历史工资单（只读）。契约以后端 services/backend/BlazorApp.Api/Services/Myob/MyobPayslipDtos.cs 为准（camelCase）。
// 2026-11-17 起列表 / 明细读后端本地快照（定时从 MYOB 同步，页面上有「同步」按钮），不再实时请求 MYOB。
// 隐私：年薪、生日、银行 / TFN 后端从不返回，这里也没有对应字段；时薪与 Reference Number 只在单张明细里返回（用于还原 MYOB 默认 Pay Slip），不进列表、不缓存。

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

/**
 * 某公司文件这次读取的状态：一家失败不拖垮另一家。
 * ok = 本地有这家公司的数据可读；从没同步成功过时 ok=false、errorCode=MYOB_PAYSLIP_SYNC_PENDING。
 */
export interface MyobPayslipCompanyStatus {
  connectionGuid: string
  connectionName: string
  ok: boolean
  count: number
  errorCode: string | null
  errorMessage: string | null
  /** 最近一次同步成功的时间（UTC ISO 8601）；从没成功过为 null。 */
  lastSyncedAt: string | null
  /** 此刻有进程正在同步这家公司。 */
  syncing: boolean
  /** 所选区间早于本地已完整同步的最早日期：区间前段可能还不全。 */
  historyIncomplete: boolean
  /** 最近一次同步失败的原因（只含状态码级别的描述）；本地仍是上次成功同步的数据。 */
  syncError: string | null
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
  /** 本地数据最近一次从 MYOB 同步成功的时间（多家公司取最早的一家）。 */
  fetchedAt: string | null
}

export interface MyobPayslipLine {
  description: string | null
  hours: number | null
  rate: number | null
  amount: number | null
  yearToDate: number | null
}

/** MYOB 原始顺序的扁平明细行（Pay Slip 通栏表的一行）：Entitlement 行的小时数在 hours 列，amount 为空。 */
export interface MyobPayslipFlatLine {
  description: string | null
  /** MYOB 原始类型，如 Wage / Tax / Superannuation / Entitlement。 */
  type: string | null
  /** 纸面 TYPE 列显示用，如 Wages / Tax / Superannuation / Entitlement。 */
  typeLabel: string | null
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
  hourlyRate: number | null
  referenceNumber: string | null
  placement: MyobPayslipPlacement
  stores: MyobPayslipStore[]
  wages: MyobPayslipLine[]
  taxes: MyobPayslipLine[]
  deductions: MyobPayslipLine[]
  superannuation: MyobPayslipLine[]
  entitlements: MyobPayslipLine[]
  others: MyobPayslipLine[]
  /** 纸面通栏表用：MYOB 原始顺序的扁平明细（分区数组仍保留，供兼容）。 */
  lines: MyobPayslipFlatLine[]
  samePaymentDateCount: number
  /** 当前账号是否有「下载工资单 PDF」权限（Myob.Payslip.Download）；没有则不渲染按钮。 */
  canDownloadPdf: boolean
  /** 当前账号是否有「邮件发送工资单 PDF」权限（Myob.Payslip.Email）。 */
  canSendEmail: boolean
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
}

/** 手动同步每家公司的结果状态：Synced 已同步；Skipped 刚同步过（60 秒冷却内）；Busy 别处正在同步；Failed 失败（本地数据保持不变）。 */
export type MyobPayslipSyncStatus = 'Synced' | 'Skipped' | 'Busy' | 'Failed'

export interface MyobPayslipSyncCompanyResult {
  connectionGuid: string
  connectionName: string
  status: MyobPayslipSyncStatus
  errorCode: string | null
  errorMessage: string | null
  /** 本次是首次回填（整段 24 个月），不是只对账最近 8 周。 */
  backfilled: boolean
  inserted: number
  updated: number
  removed: number
}

export interface MyobPayslipSyncResult {
  companies: MyobPayslipSyncCompanyResult[]
}

/** 打开单张工资单所需的键。periodStart 只在同一支付日有多张时用来区分。 */
export interface MyobPayslipKey {
  connectionGuid: string
  employeeUid: string
  paymentDate: string
  periodStart?: string | null
}

/** 下载结果：Blob + 文件名（取响应头 Content-Disposition，取不到用调用方给的兜底名）。 */
export interface MyobPayslipPdfFile {
  blob: Blob
  filename: string
}

/** 发送邮件弹窗打开时取到的信息。source：profile = 员工档案里有邮箱，none = 没有。 */
export interface MyobPayslipEmailTarget {
  email: string | null
  maskedEmail: string | null
  source: 'profile' | 'none'
  subject: string
  filename: string
  /** 当前登录人的邮箱，供「抄送给我」；没有则为 null。 */
  myEmail: string | null
}

export interface MyobPayslipEmailPayload extends MyobPayslipKey {
  to: string
  copyToMe: boolean
  note?: string | null
  /** 档案邮箱存在且 to 与它不同时必须为 true。 */
  recipientChangeConfirmed: boolean
}

export interface MyobPayslipEmailResult {
  /** 已脱敏的收件地址，如 a***@example.com。 */
  sentTo: string
  copiedToMe: boolean
}
