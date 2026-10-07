/**
 * 日结记录（只读）：WPF、手持、iPad 保存日结后上传到服务端的记录与现金盘点明细。
 * 接口：GET /api/react/v1/pos-daily-closes（列表）、GET /api/react/v1/pos-daily-closes/{guid}（详情）。
 * 金额单位为澳元（AUD），时间为 UTC ISO 字符串，营业日为 yyyy-MM-dd 字符串（不做时区换算）。
 */

/** 现金差额三态 + 无金额：差额 = 实点现金 − 系统应有现金。 */
export type DailyCloseDifferenceKind = "short" | "over" | "even" | "none";

/** 列表状态页签：移动端只有四个，「无金额」只在「全部」里出现，不单独成页签。 */
export type DailyCloseStatusTab = "all" | "short" | "over" | "even";

/** 营业日预设：自定义时用 customFrom / customTo。 */
export type DailyCloseRangePreset = "today" | "yesterday" | "last7" | "thisMonth" | "custom";

export type DailyCloseClientKind = "Wpf" | "Handheld" | "Ipad";

/** Full = 客户端完整上传；CashOnly = 回填且有现金三项；TraceOnly = 回填且只有保存记录（无金额）。 */
export type DailyCloseDetailLevel = "Full" | "CashOnly" | "TraceOnly";

/** ClientUpload = 收银端上传；AuditBackfill = 从旧操作日志回填的占位记录。 */
export type DailyCloseDataSource = "ClientUpload" | "AuditBackfill";

export interface DailyCloseFilters {
  /** 空数组 = 全部分店（后端按账号可见分店收口）。 */
  storeCodes: string[];
  preset: DailyCloseRangePreset;
  customFrom: string;
  customTo: string;
  clientKind: DailyCloseClientKind | null;
  /** 终端：只在恰好选了一家分店时才有意义。 */
  deviceCode: string | null;
  /** 收银员姓名或编号包含。 */
  keyword: string;
  status: DailyCloseStatusTab;
}

export interface DailyCloseListItem {
  dailyCloseGuid: string;
  storeCode: string;
  storeName: string | null;
  storeTimeZoneId: string | null;
  deviceCode: string;
  /** 已知端类型规整为 Wpf / Handheld / Ipad；未知值原样保留，界面直接显示原文。 */
  clientKind: string;
  detailLevel: DailyCloseDetailLevel;
  dataSource: DailyCloseDataSource;
  businessDate: string;
  businessDateInferred: boolean;
  cashierId: string;
  cashierName: string;
  savedAtUtc: string;
  orderCount: number | null;
  expectedCashAmount: number | null;
  countedCashAmount: number | null;
  cashDifference: number | null;
  cardNetAmount: number | null;
  /** 由 cashDifference 的符号推出（与后端口径一致），不直接信任接口字符串，避免金额与颜色不一致。 */
  differenceKind: DailyCloseDifferenceKind;
  saveSequence: number;
  saveCountInDay: number;
}

export interface DailyCloseCounts {
  all: number;
  short: number;
  over: number;
  even: number;
  none: number;
}

export interface DailyCloseTotals {
  expectedCash: number;
  countedCash: number;
  difference: number;
}

export interface DailyCloseListPage {
  items: DailyCloseListItem[];
  total: number;
  page: number;
  pageSize: number;
  /** 不受 status 影响，用来画页签计数。 */
  counts: DailyCloseCounts;
  /** 当前筛选（含 status）下差额非空记录的合计。 */
  totals: DailyCloseTotals;
}

export interface DailyCloseTender {
  /** Cash | Card | Voucher；未知方式原样保留，界面直接显示原文。 */
  method: string;
  salesAmount: number;
  refundAmount: number;
  netAmount: number;
}

export interface DailyCloseCashCount {
  denominationCents: number;
  quantity: number;
  subtotalAmount: number;
  kind: "Note" | "Coin";
}

export interface DailyCloseDetail extends DailyCloseListItem {
  periodFromUtc: string | null;
  periodToUtc: string | null;
  appVersion: string | null;
  returnQuantity: number | null;
  refundAmount: number | null;
  tenders: DailyCloseTender[];
  cashCounts: DailyCloseCashCount[];
  noteSubtotal: number | null;
  coinSubtotal: number | null;
  receivedAtUtc: string;
}

/** 列表请求参数（已是接口字段名）。 */
export type DailyCloseListParams = Record<string, string | number>;
