// 手机端「工资单」（管理者查看）的数据类型，对应后端 api/react/v1/myob/payslips 契约（字段 camelCase）。
// 金额一律按 MYOB 原值展示，客户端不重算任何金额。

/** 工资单所属分店的归属方式：考勤 / 推断 / 未知 / 未绑定员工。 */
export type PayslipPlacement = "Attendance" | "Inferred" | "Unknown" | "Unbound";

export interface PayslipCompany {
  connectionGuid: string;
  connectionName: string;
}

export interface PayslipStoreRef {
  storeCode: string;
  storeLabel: string;
}

/** 唯一定位一张工资单：同一支付日可能有多张，所以带上 periodStart 区分。 */
export interface PayslipKey {
  connectionGuid: string;
  employeeUid: string;
  /** yyyy-MM-dd */
  paymentDate: string;
  /** yyyy-MM-dd；缺失时后端按支付日取唯一一张。 */
  periodStart: string | null;
}

export interface PayslipListRow extends PayslipKey {
  companyName: string;
  employeeName: string;
  displayId: string;
  userGuid: string | null;
  periodEnd: string | null;
  payFrequency: string;
  grossPay: number | null;
  taxTotal: number | null;
  netPay: number | null;
  placement: PayslipPlacement;
  stores: PayslipStoreRef[];
}

export interface PayslipCompanyStatus {
  connectionGuid: string;
  connectionName: string;
  ok: boolean;
  count: number;
  errorCode: string | null;
  errorMessage: string | null;
}

export interface PayslipListResult {
  from: string;
  to: string;
  companies: PayslipCompanyStatus[];
  rows: PayslipListRow[];
  totalCount: number;
  employeeCount: number;
  grossTotal: number;
  netTotal: number;
  unassignedCount: number;
  canViewAllStores: boolean;
  fetchedAt: string | null;
}

export interface PayslipListQuery {
  connectionGuid: string | null;
  storeCode: string | null;
  from: string;
  to: string;
  keyword: string;
  onlyUnassigned: boolean;
  /** 下拉刷新时为 true，让后端绕过缓存。 */
  refresh: boolean;
}

/** MYOB 原始顺序的一行明细；Entitlement 行小时数在 hours、amount 为空。 */
export interface PayslipLine {
  description: string;
  type: string;
  typeLabel: string;
  hours: number | null;
  rate: number | null;
  amount: number | null;
  yearToDate: number | null;
}

export interface PayslipDetail extends PayslipKey {
  companyName: string;
  companyAbn: string;
  employeeName: string;
  displayId: string;
  userGuid: string | null;
  periodEnd: string | null;
  payFrequency: string;
  grossPay: number | null;
  netPay: number | null;
  superFundName: string;
  placement: PayslipPlacement;
  stores: PayslipStoreRef[];
  hourlyRate: number | null;
  referenceNumber: string | null;
  lines: PayslipLine[];
  samePaymentDateCount: number;
  canDownloadPdf: boolean;
  canSendEmail: boolean;
}

export interface PayslipHoursStore {
  storeCode: string;
  storeLabel: string;
  workHours: number | null;
  leaveHours: number | null;
  failed: boolean;
}

export interface PayslipHours {
  periodStart: string | null;
  periodEnd: string | null;
  stores: PayslipHoursStore[];
  totalWorkHours: number | null;
  totalLeaveHours: number | null;
}

export interface PayslipEmailTarget {
  email: string | null;
  maskedEmail: string | null;
  /** profile = 员工档案里有邮箱；none = 没有。 */
  source: "profile" | "none";
  subject: string;
  filename: string;
  /** 当前操作人自己的邮箱（用于「抄送给我」）。 */
  myEmail: string | null;
}

export interface PayslipEmailRequest {
  connectionGuid: string;
  employeeUid: string;
  paymentDate: string;
  periodStart?: string;
  to: string;
  copyToMe: boolean;
  note?: string;
  recipientChangeConfirmed: boolean;
}

export interface PayslipEmailResult {
  /** 已脱敏的收件地址。 */
  sentTo: string;
  copiedToMe: boolean;
}
