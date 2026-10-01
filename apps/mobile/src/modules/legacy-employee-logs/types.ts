/** 老收银（POSM.EmployeeLogs）操作日志；时间均为门店本地墙钟时间字符串，不带时区。 */
export type LegacyRiskLens = "all" | "danger" | "abnormal";
export type LegacyReviewStatus = "all" | "pending" | "reviewed" | "followUp";
export type LegacyRangePreset = "today" | "yesterday" | "last7" | "last31";
export type LegacyRuleCode =
  | "noSaleDrawer"
  | "deleteAfterCheckout"
  | "bigDiscount"
  | "burstDelete"
  | "repeatReprint"
  | "offHours";

export interface LegacyLogFlag {
  ruleCode: string;
  evidence: Record<string, string>;
  detectedAtUtc: string;
}

export interface LegacyLogReview {
  /** revoked = 撤销后回到待核查，但再次核查仍要带上版本号。 */
  result: "normal" | "followUp" | "revoked";
  note: string | null;
  reviewedByName: string;
  reviewedAtUtc: string;
  version: number;
}

export interface LegacyLogItem {
  id: string;
  employeeId: string | null;
  employeeName: string | null;
  operation: string | null;
  operationDetail: string | null;
  operationTime: string;
  deviceCode: string | null;
  storeCode: string | null;
  lastUploadTime: string;
  isDanger: boolean;
  flags: LegacyLogFlag[];
  review: LegacyLogReview | null;
  amountImpact: number | null;
}

export interface LegacyRuleCount {
  ruleCode: string;
  count: number;
}

export interface LegacyRiskSummary {
  dangerTotal: number;
  abnormalTotal: number;
  pendingReview: number;
  abnormalEmployees: number;
  abnormalByRule: LegacyRuleCount[];
}

export interface LegacyOperationCount {
  operation: string | null;
  count: number;
}

export interface LegacyLogPage {
  items: LegacyLogItem[];
  total: number;
  operationCounts: LegacyOperationCount[];
  employees: { employeeId: string | null; employeeName: string | null; count: number }[];
  devices: { deviceCode: string | null; count: number }[];
  riskSummary: LegacyRiskSummary;
}

export interface LegacyLogContext {
  target: LegacyLogItem;
  windowMinutes: number;
  neighbors: LegacyLogItem[];
  truncated: boolean;
}

export interface LegacyEmployeeSummary {
  employeeId: string | null;
  employeeName: string | null;
  storeCodes: string[];
  deviceCodes: string[];
  total: number;
  dangerCount: number;
  abnormalCount: number;
  pendingReview: number;
  abnormalByRule: LegacyRuleCount[];
  amountImpact: number;
}

export interface LegacyEmployeeSummaryResult {
  employees: LegacyEmployeeSummary[];
  total: number;
  dangerTotal: number;
}

/** 列表筛选。subOperations 是「全部 / 危险」入口下的细分；ruleCode 与 reviewStatus 只在「异常」入口下生效。 */
export interface LegacyLogFilters {
  preset: LegacyRangePreset;
  storeCodes: string[];
  employeeId: string | null;
  employeeName: string | null;
  deviceCode: string | null;
  keyword: string;
  lens: LegacyRiskLens;
  subOperations: string[];
  ruleCode: string | null;
  reviewStatus: LegacyReviewStatus;
}
