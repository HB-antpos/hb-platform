import type {
  LegacyLogFilters,
  LegacyLogFlag,
  LegacyLogItem,
  LegacyOperationCount,
  LegacyRangePreset,
  LegacyRiskLens,
  LegacyRuleCode,
  LogSource,
} from "./types";

export const LEGACY_LOG_PAGE_SIZE = 50;
export const LEGACY_RANGE_PRESETS: LegacyRangePreset[] = ["today", "yesterday", "last7", "last31"];

/**
 * 危险操作：直接影响收款或现金。与后端 LegacyEmployeeLogRiskCatalog.DangerOperations 一致（契约测试比对）。
 * 重打印不算：生产一周约 2,500 次，属正常收银动作，反复重打印由异常规则兜底。
 */
export const LEGACY_DANGER_OPERATIONS = [
  "删除商品",
  "修改商品价格",
  "修改商品折扣",
  "修改所有商品折扣",
  "开钱箱",
  "无小票退货成功",
  "退货授权",
];

/** 异常规则，顺序即展示顺序；与后端 LegacyEmployeeLogRiskCatalog.AllRules 一致（契约测试比对）。 */
export const LEGACY_RULE_CODES: LegacyRuleCode[] = [
  "noSaleDrawer",
  "deleteAfterCheckout",
  "bigDiscount",
  "burstDelete",
  "repeatReprint",
  "offHours",
];

/** 「全部」入口的类别细分（与 Web 端一致）。 */
export const LEGACY_CATEGORY_GROUPS: { key: string; operations: string[] }[] = [
  { key: "item", operations: ["搜索商品", "搜索商品多结果", "添加商品", "添加无码商品", "增加商品数量", "减少商品数量", "修改商品数量"] },
  { key: "price", operations: ["修改商品价格", "修改商品折扣", "修改所有商品折扣"] },
  { key: "delete", operations: ["删除商品"] },
  { key: "payment", operations: ["结账", "支付完成", "挂单", "恢复挂单", "重打印"] },
  { key: "return", operations: ["退货授权", "添加退货商品", "无小票退货成功"] },
  { key: "auth", operations: ["开钱箱", "身份确认", "登录", "用户登录", "切换用户"] },
];

/** 「危险」入口的操作细分（后端再与危险清单取交集）。 */
export const LEGACY_DANGER_GROUPS: { key: string; operations: string[] }[] = [
  { key: "delete", operations: ["删除商品"] },
  { key: "drawer", operations: ["开钱箱"] },
  { key: "price", operations: ["修改商品价格"] },
  { key: "discount", operations: ["修改商品折扣", "修改所有商品折扣"] },
  { key: "return", operations: ["退货授权", "无小票退货成功"] },
];

/** 新收银的异常规则：编号与老收银相同，另加「紧急覆盖」；与后端 PosOperationAuditRiskCatalog.AllRules 一致。 */
export const POS_RULE_CODES: string[] = [...LEGACY_RULE_CODES, "emergencyOverride"];

/** 新收银「危险」入口的操作细分（与 Web 端一致；后端按危险口径过滤，开钱箱只算手动开钱箱）。 */
export const POS_DANGER_GROUPS: { key: string; operations: string[] }[] = [
  { key: "delete", operations: ["CART_ITEM_REMOVE", "CART_CLEAR"] },
  { key: "drawer", operations: ["CASH_DRAWER_OPEN"] },
  { key: "price", operations: ["CART_ITEM_PRICE_CHANGE"] },
  { key: "discount", operations: ["CART_LINE_DISCOUNT_CHANGE", "CART_ORDER_DISCOUNT_CHANGE"] },
  { key: "refund", operations: ["RETURN_REFUND_COMPLETE", "SALE_VOID", "ORDER_CANCEL"] },
  { key: "override", operations: ["CARD_PAYMENT_SUPERVISOR_RESOLUTION", "PERMISSION_OVERRIDE"] },
  { key: "system", operations: ["API_SERVER_CHANGE", "DEVICE_REREGISTER", "REMOTE_MAINTENANCE_INSTALL", "CATALOG_RESET", "TEST_SALES_DATA_RESET"] },
];

export function ruleCodesFor(source: LogSource) {
  return source === "pos" ? POS_RULE_CODES : LEGACY_RULE_CODES;
}

/** 「全部」入口的类别细分：新收银没有按操作名的计数，不显示细分。 */
export function categoryGroupsFor(source: LogSource) {
  return source === "pos" ? [] : LEGACY_CATEGORY_GROUPS;
}

export function dangerGroupsFor(source: LogSource) {
  return source === "pos" ? POS_DANGER_GROUPS : LEGACY_DANGER_GROUPS;
}

/** 操作标签配色分类（与 Web 端 CATEGORY_TAG_COLOR 同一归类）。 */
export type LegacyOperationTone = "item" | "price" | "delete" | "payment" | "return" | "auth" | "other";

/** 新收银事件类型的配色归类，与老收银同一套颜色语义。 */
const POS_OPERATION_TONE: Record<string, LegacyOperationTone> = {
  CART_ITEM_ADD: "item",
  CART_ITEM_QUANTITY_CHANGE: "item",
  CART_ITEM_PRICE_CHANGE: "price",
  CART_LINE_DISCOUNT_CHANGE: "price",
  CART_ORDER_DISCOUNT_CHANGE: "price",
  CART_ITEM_REMOVE: "delete",
  CART_CLEAR: "delete",
  ORDER_CANCEL: "delete",
  SALE_VOID: "delete",
  PAYMENT_TENDER_ADD: "payment",
  PAYMENT_TENDER_REMOVE: "payment",
  PAYMENT_CANCEL: "payment",
  SALE_COMPLETE: "payment",
  ORDER_HOLD: "payment",
  ORDER_RECALL: "payment",
  RECEIPT_REPRINT: "payment",
  RETURN_REFUND_COMPLETE: "return",
  CASH_DRAWER_OPEN: "auth",
  CASHIER_LOGIN: "auth",
  CASHIER_LOGOUT: "auth",
  PERMISSION_OVERRIDE: "auth",
  CARD_PAYMENT_SUPERVISOR_RESOLUTION: "auth",
};

export function posOperationTone(operationType: string | null | undefined): LegacyOperationTone {
  return (operationType && POS_OPERATION_TONE[operationType]) || "other";
}

/** 新收银事件类型的文案键（screens/legacyEmployeeLogs 的 posOperations 下）：CART_ITEM_ADD → cartItemAdd。 */
export function posOperationKey(operationType: string) {
  return operationType.toLowerCase().replace(/_([a-z])/g, (_, letter: string) => letter.toUpperCase());
}

export function operationTone(operation: string | null | undefined): LegacyOperationTone {
  const name = operation?.trim();
  if (!name) return "other";
  const group = LEGACY_CATEGORY_GROUPS.find((item) => item.operations.includes(name));
  return (group?.key as LegacyOperationTone | undefined) ?? "other";
}

export function isDangerOperation(operation: string | null | undefined) {
  return Boolean(operation && LEGACY_DANGER_OPERATIONS.includes(operation.trim()));
}

export function createDefaultLegacyLogFilters(storeCodes: string[] = [], source: LogSource = "legacy"): LegacyLogFilters {
  return {
    source,
    preset: "today",
    storeCodes,
    employeeId: null,
    employeeName: null,
    deviceCode: null,
    keyword: "",
    lens: "all",
    subOperations: [],
    ruleCode: null,
    reviewStatus: "all",
  };
}

/** 切换入口时清掉上一入口的细分与核查状态。 */
export function switchLens(filters: LegacyLogFilters, lens: LegacyRiskLens): LegacyLogFilters {
  return { ...filters, lens, subOperations: [], ruleCode: null, reviewStatus: "all" };
}

function pad(value: number) {
  return String(value).padStart(2, "0");
}

export function formatWallClockDate(date: Date) {
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;
}

/**
 * 预设区间转半开区间 [from, to)，按设备本地日期计算墙钟时间。
 * 门店与使用者都在澳洲东部，设备本地日期即门店营业日；后端按字面值与库内墙钟时间比较。
 */
export function resolveLegacyRange(preset: LegacyRangePreset, now: Date = new Date()) {
  const start = new Date(now.getFullYear(), now.getMonth(), now.getDate());
  const days = preset === "last7" ? 7 : preset === "last31" ? 31 : 1;
  if (preset === "yesterday") start.setDate(start.getDate() - 1);
  else start.setDate(start.getDate() - (days - 1));
  const end = new Date(start);
  end.setDate(end.getDate() + days);
  return { from: `${formatWallClockDate(start)}T00:00:00`, to: `${formatWallClockDate(end)}T00:00:00` };
}

/**
 * 新收银的查询区间：同一组「今天 / 昨天 / 近 7 天 / 近 31 天」按设备本地零点换算成 UTC（后端按 UTC 比较）。
 */
export function resolvePosRange(preset: LegacyRangePreset, now: Date = new Date()) {
  const { from, to } = resolveLegacyRange(preset, now);
  const toUtc = (wallClock: string) => {
    const [date, time] = wallClock.split("T");
    const [year, month, day] = date.split("-").map(Number);
    const [hour, minute, second] = time.split(":").map(Number);
    return new Date(year, month - 1, day, hour, minute, second).toISOString();
  };
  return { fromUtc: toUtc(from), toUtc: toUtc(to) };
}

/** UTC 时间按设备本地时区转成墙钟字符串 YYYY-MM-DDTHH:mm:ss，与老收银时间同一格式，列表按小时分组共用。 */
export function toLocalWallClock(isoUtc: string | null | undefined) {
  const time = isoUtc ? Date.parse(isoUtc) : Number.NaN;
  if (!Number.isFinite(time)) return "";
  const date = new Date(time);
  return `${formatWallClockDate(date)}T${pad(date.getHours())}:${pad(date.getMinutes())}:${pad(date.getSeconds())}`;
}

/** 新收银列表 / 汇总查询参数：员工下钻用收银员编号精确匹配，细分操作类型按重复键展开。 */
function buildPosLogQuery(filters: LegacyLogFilters, storeCodes: string[], pageNumber: number | null, now: Date) {
  const { fromUtc, toUtc } = resolvePosRange(filters.preset, now);
  const params = new URLSearchParams({ fromUtc, toUtc });
  if (pageNumber !== null) {
    params.set("pageNumber", String(pageNumber));
    params.set("pageSize", String(LEGACY_LOG_PAGE_SIZE));
    params.set("sortBy", "occurredAtUtc");
    params.set("sortOrder", "desc");
  }
  storeCodes.forEach((code) => params.append("storeCodes", code));
  if (filters.employeeId) params.set("cashierId", filters.employeeId);
  if (filters.deviceCode) params.set("deviceCode", filters.deviceCode);
  const keyword = filters.keyword.trim();
  if (keyword) params.set("keyword", keyword);
  if (filters.lens !== "all") params.set("riskLens", filters.lens);
  if (filters.lens !== "abnormal") filters.subOperations.forEach((operation) => params.append("operationTypes", operation));
  if (filters.lens === "abnormal" && filters.ruleCode) params.append("ruleCodes", filters.ruleCode);
  if (filters.lens === "abnormal" && filters.reviewStatus !== "all") params.set("reviewStatus", filters.reviewStatus);
  return params;
}

/** 列表查询参数：数组用重复键（storeCodes=a&storeCodes=b），与后端 List<string> 绑定一致；没选分店不查。 */
export function buildLegacyLogQuery(filters: LegacyLogFilters, pageNumber: number, now: Date = new Date()) {
  const storeCodes = [...new Set(filters.storeCodes.map((code) => code.trim()).filter(Boolean))];
  if (storeCodes.length === 0) return null;
  if (filters.source === "pos") return buildPosLogQuery(filters, storeCodes, pageNumber, now);
  const { from, to } = resolveLegacyRange(filters.preset, now);
  const params = new URLSearchParams({ from, to, pageNumber: String(pageNumber), pageSize: String(LEGACY_LOG_PAGE_SIZE), sortOrder: "desc" });
  storeCodes.forEach((code) => params.append("storeCodes", code));
  if (filters.employeeId) params.append("employeeIds", filters.employeeId);
  if (filters.deviceCode) params.set("deviceCode", filters.deviceCode);
  const keyword = filters.keyword.trim();
  if (keyword) params.set("keyword", keyword);
  if (filters.lens !== "all") params.set("riskLens", filters.lens);
  if (filters.lens !== "abnormal") filters.subOperations.forEach((operation) => params.append("operations", operation));
  if (filters.lens === "abnormal" && filters.ruleCode) params.append("ruleCodes", filters.ruleCode);
  if (filters.lens === "abnormal" && filters.reviewStatus !== "all") params.set("reviewStatus", filters.reviewStatus);
  return params;
}

/** 按员工汇总只用分店、时间、设备条件（新收银后端另外忽略收银员、操作类型与风险入口）。 */
export function buildLegacyEmployeeSummaryQuery(filters: LegacyLogFilters, now: Date = new Date()) {
  const storeCodes = [...new Set(filters.storeCodes.map((code) => code.trim()).filter(Boolean))];
  if (storeCodes.length === 0) return null;
  if (filters.source === "pos") {
    const { fromUtc, toUtc } = resolvePosRange(filters.preset, now);
    const params = new URLSearchParams({ fromUtc, toUtc });
    storeCodes.forEach((code) => params.append("storeCodes", code));
    if (filters.deviceCode) params.set("deviceCode", filters.deviceCode);
    return params;
  }
  const { from, to } = resolveLegacyRange(filters.preset, now);
  const params = new URLSearchParams({ from, to });
  storeCodes.forEach((code) => params.append("storeCodes", code));
  if (filters.deviceCode) params.set("deviceCode", filters.deviceCode);
  return params;
}

/** 已设置的非默认筛选数（不含风险入口与细分），用于筛选按钮角标。 */
export function countActiveLegacyFilters(filters: LegacyLogFilters) {
  return [filters.preset !== "today", Boolean(filters.employeeId), Boolean(filters.deviceCode), Boolean(filters.keyword.trim())].filter(Boolean).length;
}

export function sumOperationCounts(counts: readonly LegacyOperationCount[], operations: readonly string[]) {
  const wanted = new Set(operations);
  return counts.reduce((sum, row) => (row.operation && wanted.has(row.operation) ? sum + row.count : sum), 0);
}

export function sameOperations(expected: readonly string[], selected: readonly string[]) {
  if (selected.length !== expected.length) return false;
  const set = new Set(selected);
  return expected.every((operation) => set.has(operation));
}

/** 按整点分组（列表已按时间倒序）。 */
export function groupLegacyLogsByHour(items: readonly LegacyLogItem[]) {
  const sections: { key: string; date: string; hour: string; data: LegacyLogItem[] }[] = [];
  items.forEach((item) => {
    const date = item.operationTime.slice(0, 10);
    const hour = item.operationTime.slice(11, 13);
    const key = `${date} ${hour}`;
    let section = sections[sections.length - 1];
    if (!section || section.key !== key) {
      section = { key, date, hour, data: [] };
      sections.push(section);
    }
    section.data.push(item);
  });
  return sections;
}

export function clockOf(wallClock: string) {
  return wallClock.slice(11, 19);
}

// 腾讯云 COS 默认域名：<bucket>-<appid>.cos.<region>.myqcloud.com，桶已开通图片处理。
const COS_HOST_PATTERN = /^https?:\/\/[a-z0-9-]+\.cos\.[a-z0-9-]+\.myqcloud\.com\//i;

/**
 * 商品缩略图地址：COS 原图追加 imageMogr2 由 COS 实时缩放并转 WebP（单张 100KB+ 降到约 2KB），
 * 与 Web 的 toProductThumbnailUrl 规则一致；非 COS 或已带查询参数的地址原样返回。
 */
export function productThumbnailUri(url: string | null | undefined, size: number) {
  const trimmed = url?.trim();
  if (!trimmed) return null;
  if (trimmed.includes("?") || !COS_HOST_PATTERN.test(trimmed)) return trimmed;
  return `${trimmed}?imageMogr2/thumbnail/${size}x${size}/format/webp`;
}

export function formatAmountImpact(amount: number | null | undefined) {
  return amount && amount > 0 ? `−${amount.toFixed(2)}` : null;
}

/** 当前有效核查结论；撤销视同未核查。 */
export function activeReview<T extends { result: string }>(review: T | null | undefined) {
  return review && review.result !== "revoked" ? review : null;
}

/** 异常依据的文案片段：i18n 键（evidence 下）与参数，页面翻译后用「；」连接。与 Web 端同一口径。 */
export function describeFlagEvidence(flag: Pick<LegacyLogFlag, "ruleCode" | "evidence">) {
  const e = flag.evidence ?? {};
  const parts: { key: string; params?: Record<string, string> }[] = [];
  switch (flag.ruleCode) {
    case "noSaleDrawer":
      parts.push({ key: "noSaleWindow", params: { seconds: e.windowSeconds ?? "120" } });
      if (e.previousCheckoutAt) parts.push({ key: "noSalePrevious", params: { at: e.previousCheckoutAt, minutes: e.minutesSincePreviousCheckout ?? "-" } });
      if (e.nextCheckoutAt) parts.push({ key: "noSaleNext", params: { at: e.nextCheckoutAt } });
      // 身份确认只有老收银记录；新收银没有该字段时不显示。
      if (e.identityConfirmed !== undefined) parts.push({ key: e.identityConfirmed === "true" ? "identityConfirmed" : "identityMissing" });
      break;
    case "deleteAfterCheckout":
      // 新收银以「开始收款」为锚点（anchor = tender）。
      parts.push({
        key: e.anchor === "tender" ? "deleteAfterTender" : "deleteAfterCheckout",
        params: { at: e.checkoutAt ?? "-", seconds: e.secondsAfterCheckout ?? "-", amount: e.deletedAmount ?? "-" },
      });
      if (e.product) parts.push(e.quantity ? { key: "product", params: { product: e.product, quantity: e.quantity } } : { key: "productOnly", params: { product: e.product } });
      break;
    case "bigDiscount":
      if (e.kind === "cart") parts.push({ key: "discountCart", params: { percent: e.percent ?? "-", count: e.itemCount ?? "-", total: e.originalTotal ?? "-" } });
      else if (e.kind === "price") parts.push({ key: "discountPrice", params: { from: e.originalPrice ?? "-", to: e.newPrice ?? "-", percent: e.percent ?? "-" } });
      else parts.push({ key: "discountItem", params: { percent: e.percent ?? "-", previous: e.previousPercent ?? "0" } });
      if (e.product && e.kind !== "cart") parts.push({ key: "productOnly", params: { product: e.product } });
      if (e.amount) parts.push({ key: "discountAmount", params: { amount: e.amount } });
      break;
    case "burstDelete":
      parts.push({ key: "burstDelete", params: { from: e.firstAt ?? "-", to: e.lastAt ?? "-", count: e.count ?? "-", amount: e.totalAmount ?? "-", threshold: e.threshold ?? "-" } });
      break;
    case "repeatReprint":
      parts.push(e.orderId || !e.count
        ? { key: "repeatReprint", params: { order: shortOrder(e.orderId), count: e.count ?? "-", first: e.firstAt ?? "-" } }
        : { key: "repeatReprintDay", params: { count: e.count, first: e.firstAt ?? "-" } });
      break;
    case "offHours":
      parts.push({
        key: e.segment === "beforeOpen" ? "offHoursBeforeOpen" : "offHoursAfterClose",
        params: { count: e.count ?? "-", from: e.firstAt ?? "-", to: e.lastAt ?? "-", open: e.open ?? "07:00", close: e.close ?? "22:00" },
      });
      break;
    case "emergencyOverride":
      parts.push({ key: "emergencyOverride", params: { outcome: e.outcome ?? "-" } });
      if (e.reason) parts.push({ key: "reason", params: { reason: e.reason } });
      break;
    default:
      break;
  }
  return parts;
}

/** 卡片上的一句短依据（列表里只放得下一行）。 */
export function shortFlagEvidence(flag: Pick<LegacyLogFlag, "ruleCode" | "evidence">) {
  const e = flag.evidence ?? {};
  switch (flag.ruleCode) {
    case "noSaleDrawer":
      return { key: "short.noSaleDrawer", params: { seconds: e.windowSeconds ?? "120" } };
    case "deleteAfterCheckout":
      return { key: "short.deleteAfterCheckout", params: { seconds: e.secondsAfterCheckout ?? "-" } };
    case "bigDiscount":
      return { key: "short.bigDiscount", params: { percent: e.percent ?? "-" } };
    case "burstDelete":
      return { key: "short.burstDelete", params: { count: e.count ?? "-" } };
    case "repeatReprint":
      return { key: "short.repeatReprint", params: { count: e.count ?? "-" } };
    case "offHours":
      return { key: e.segment === "beforeOpen" ? "short.beforeOpen" : "short.afterClose", params: { at: e.firstAt ?? "-" } };
    case "emergencyOverride":
      return { key: "short.emergencyOverride", params: { outcome: e.outcome ?? "-" } };
    default:
      return null;
  }
}

function shortOrder(order?: string) {
  return order && order.length > 13 ? `${order.slice(0, 8)}…${order.slice(-4)}` : order ?? "-";
}

/** 分店展示：有名称时「名称」，否则编码。 */
export function storeDisplayName(code: string | null | undefined, names: ReadonlyMap<string, string>) {
  if (!code) return "-";
  return names.get(code) || code;
}

/** 范围按钮上的分店摘要：一家显示名称，多家显示「首家名称 等 N 家」。 */
export function summarizeStores(codes: readonly string[], names: ReadonlyMap<string, string>) {
  if (codes.length === 0) return { key: "scope.noStore" as const };
  const first = storeDisplayName(codes[0], names);
  return codes.length === 1 ? { key: "scope.oneStore" as const, params: { store: first } } : { key: "scope.manyStores" as const, params: { store: first, count: String(codes.length) } };
}

export function dangerRate(dangerCount: number, total: number) {
  return total > 0 ? dangerCount / total : 0;
}

/** 两个页签（操作记录 / 按员工汇总）之间、员工下钻时，用路由参数传递共用条件。 */
export interface LegacyRouteParams {
  source?: string;
  stores?: string;
  preset?: string;
  device?: string;
  employeeId?: string;
  employeeName?: string;
  lens?: string;
}

export function filtersToRouteParams(filters: LegacyLogFilters, extra: Partial<LegacyRouteParams> = {}): LegacyRouteParams {
  const params: LegacyRouteParams = { source: filters.source, stores: filters.storeCodes.join(","), preset: filters.preset };
  if (filters.deviceCode) params.device = filters.deviceCode;
  return { ...params, ...extra };
}

/** 来源只能是有权限的那一个：地址参数优先，其次上次的选择，都没有时默认老收银；两个都没权限返回 null。 */
export function resolveLogSource(input: { requested?: string | null; remembered?: string | null; canLegacy: boolean; canPos: boolean }): LogSource | null {
  const allowed = (source: string | null | undefined): source is LogSource =>
    (source === "legacy" && input.canLegacy) || (source === "pos" && input.canPos);
  if (allowed(input.requested)) return input.requested;
  if (allowed(input.remembered)) return input.remembered;
  if (input.canLegacy) return "legacy";
  return input.canPos ? "pos" : null;
}

export function filtersFromRouteParams(params: LegacyRouteParams, source: LogSource = "legacy"): LegacyLogFilters {
  const base = createDefaultLegacyLogFilters(
    (params.stores ?? "").split(",").map((code) => code.trim()).filter(Boolean),
    source,
  );
  const preset = LEGACY_RANGE_PRESETS.find((item) => item === params.preset) ?? base.preset;
  const lens = (["all", "danger", "abnormal"] as LegacyRiskLens[]).find((item) => item === params.lens) ?? base.lens;
  return {
    ...base,
    preset,
    lens,
    deviceCode: params.device?.trim() || null,
    employeeId: params.employeeId?.trim() || null,
    employeeName: params.employeeName?.trim() || null,
  };
}

export type LegacyEmployeeSort = "abnormal" | "rate" | "amount";

/** 员工汇总排序：异常数（并列看待核查）、危险占比、金额让利；姓名兜底保证顺序稳定。 */
export function orderLegacyEmployees<T extends { employeeName: string | null; abnormalCount: number; pendingReview: number; dangerCount: number; total: number; amountImpact: number }>(
  rows: readonly T[],
  sort: LegacyEmployeeSort,
) {
  const byName = (a: T, b: T) => (a.employeeName ?? "").localeCompare(b.employeeName ?? "");
  return [...rows].sort((a, b) => {
    if (sort === "rate") return dangerRate(b.dangerCount, b.total) - dangerRate(a.dangerCount, a.total) || byName(a, b);
    if (sort === "amount") return b.amountImpact - a.amountImpact || byName(a, b);
    return b.abnormalCount - a.abnormalCount || b.pendingReview - a.pendingReview || byName(a, b);
  });
}
