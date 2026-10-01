/**
 * 员工操作日志：老收银（POSM.EmployeeLogs）与新收银（pos_operation_audit）共用一套页面。
 * 时间均为墙钟时间字符串（不带时区）：老收银是门店墙钟原样，新收银由 UTC 按设备本地时区换算。
 */
export type LogSource = "legacy" | "pos";
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

/** 新收银事件特有的字段，详情页按来源展示。 */
export interface PosLogExtra {
  operationType: string;
  outcome: string;
  reasonCode: string | null;
  paymentMethod: string | null;
  paymentAmount: number | null;
  beforeActual: number | null;
  afterActual: number | null;
  orderGuid: string | null;
  deviceSystem: string | null;
  isEmergencyOverride: boolean;
  isOfflineCached: boolean;
  safeMessage: string | null;
}

export interface LegacyLogItem {
  id: string;
  /** 新收银条目才有：操作配色分类、标题（主商品或操作名）与原始字段。老收银不传。 */
  source?: LogSource;
  tone?: "item" | "price" | "delete" | "payment" | "return" | "auth" | "other";
  title?: string | null;
  /** 新收银条目才有：是否涉及商品、主商品货号（与名称相同时为空）与主档原图地址。 */
  hasProduct?: boolean;
  itemNumber?: string | null;
  productImage?: string | null;
  pos?: PosLogExtra;
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
  /** 新收银「全部」入口的总数（汇总接口）；老收银由 operationCounts 求和。 */
  allTotal?: number;
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
  source: LogSource;
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
