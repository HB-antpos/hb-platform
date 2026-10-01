import { posOperationTone, toLocalWallClock } from "./logic";
import type { LegacyEmployeeSummary, LegacyLogItem, LegacyLogReview } from "./types";

/** 接口响应归一化（纯函数，不依赖 apiClient，便于在 Node 下测试）。 */
export type Raw = Record<string, unknown>;

export function unwrap(data: unknown): Raw {
  // 后端统一 ApiResponse<T> 包装；兼容直接返回数据的情况。
  const body = (data ?? {}) as Raw;
  return ((body.data ?? body) as Raw) ?? {};
}

export const str = (value: unknown) => (typeof value === "string" && value ? value : null);
export const num = (value: unknown) => (typeof value === "number" && Number.isFinite(value) ? value : 0);
export const strings = (value: unknown) => (Array.isArray(value) ? value.filter((item): item is string => typeof item === "string" && item.length > 0) : []);
export const arr = <T>(value: unknown, map: (item: Raw) => T) => (Array.isArray(value) ? value.map((item) => map((item ?? {}) as Raw)) : []);

export function normalizeLegacyLogReview(raw: unknown): LegacyLogReview | null {
  if (!raw || typeof raw !== "object") return null;
  const r = raw as Raw;
  const result = r.result === "normal" || r.result === "followUp" || r.result === "revoked" ? r.result : null;
  if (!result) return null;
  return {
    result,
    note: str(r.note),
    reviewedByName: str(r.reviewedByName) ?? "",
    reviewedAtUtc: str(r.reviewedAtUtc) ?? "",
    version: num(r.version),
  };
}

export function normalizeLegacyLogItem(r: Raw): LegacyLogItem {
  return {
    id: str(r.id) ?? "",
    employeeId: str(r.employeeId),
    employeeName: str(r.employeeName),
    operation: str(r.operation),
    operationDetail: str(r.operationDetail),
    operationTime: str(r.operationTime) ?? "",
    deviceCode: str(r.deviceCode),
    storeCode: str(r.storeCode),
    lastUploadTime: str(r.lastUploadTime) ?? "",
    isDanger: r.isDanger === true,
    flags: normalizeFlags(r.flags),
    review: normalizeLegacyLogReview(r.review),
    amountImpact: typeof r.amountImpact === "number" ? r.amountImpact : null,
  };
}

const optionalNumber = (value: unknown) => (typeof value === "number" && Number.isFinite(value) ? value : null);

function normalizeFlags(value: unknown) {
  return arr(value, (flag) => ({
    ruleCode: str(flag.ruleCode) ?? "",
    evidence: (flag.evidence && typeof flag.evidence === "object" ? flag.evidence : {}) as Record<string, string>,
    detectedAtUtc: str(flag.detectedAtUtc) ?? "",
  })).filter((flag) => flag.ruleCode);
}

/**
 * 新收银审计事件归一成老收银列表条目的形状，列表、详情、核查共用同一套页面：
 * 编号 = eventId，员工 = 收银员，操作 = 本地化的事件名称（由调用方传入翻译），时间按设备本地时区转墙钟。
 */
export function normalizePosLogItem(r: Raw, operationLabel: (operationType: string) => string): LegacyLogItem {
  const operationType = str(r.operationType) ?? "";
  const label = operationType ? operationLabel(operationType) : null;
  const product = str(r.primaryProduct);
  const productCount = num(r.productCount);
  const more = productCount > 1 ? ` +${productCount - 1}` : "";
  // 货号与商品名相同（收银端拿货号当名称）时不重复显示。
  const rawItemNumber = str(r.primaryItemNumber)?.trim() || null;
  const itemNumber = product && rawItemNumber && rawItemNumber !== product ? rawItemNumber : null;
  // 卡片与详情标题：商品名 +N，货号单独显示（避免长商品名截断后看不到货号）。
  const title = product ? `${product}${more}` : label;
  // 时间线只有一行文字：货号紧跟主商品名、"+N" 放最后，与 Web 一致。
  const textTitle = product ? `${product}${itemNumber ? ` (${itemNumber})` : ""}${more}` : label;
  const beforeActual = optionalNumber(r.beforeActual);
  const afterActual = optionalNumber(r.afterActual);
  const paymentAmount = optionalNumber(r.paymentAmount);
  // 时间线里每条一行摘要：金额变化或付款金额，有原因代码时附上。
  const amountText = beforeActual !== null && afterActual !== null && beforeActual !== afterActual
    ? `${beforeActual.toFixed(2)} → ${afterActual.toFixed(2)}`
    : paymentAmount !== null ? paymentAmount.toFixed(2) : null;
  const detail = [product ? textTitle : null, amountText, str(r.reasonCode)].filter(Boolean).join(" · ");
  return {
    id: str(r.eventId) ?? "",
    source: "pos",
    tone: posOperationTone(operationType),
    title,
    hasProduct: Boolean(product) && productCount > 0,
    itemNumber,
    productImage: str(r.primaryProductImage)?.trim() || null,
    employeeId: str(r.cashierId),
    employeeName: str(r.cashierName) ?? str(r.cashierId),
    operation: label,
    operationDetail: detail || null,
    operationTime: toLocalWallClock(str(r.occurredAtUtc)),
    deviceCode: str(r.deviceCode),
    storeCode: str(r.storeCode),
    lastUploadTime: toLocalWallClock(str(r.receivedAtUtc)),
    isDanger: r.isDanger === true,
    flags: normalizeFlags(r.flags),
    review: normalizeLegacyLogReview(r.review),
    amountImpact: optionalNumber(r.amountImpact),
    pos: {
      operationType,
      outcome: str(r.outcome) ?? "",
      reasonCode: str(r.reasonCode),
      paymentMethod: str(r.paymentMethod),
      paymentAmount,
      beforeActual,
      afterActual,
      orderGuid: str(r.orderGuid),
      deviceSystem: str(r.deviceSystem),
      isEmergencyOverride: r.isEmergencyOverride === true,
      isOfflineCached: r.isOfflineCached === true,
      safeMessage: str(r.safeMessage),
    },
  };
}

/** 新收银按收银员汇总的一行转成员工汇总形状。 */
export function normalizePosEmployeeSummary(row: Raw): LegacyEmployeeSummary {
  return {
    employeeId: str(row.cashierId),
    employeeName: str(row.cashierName) ?? str(row.cashierId),
    storeCodes: strings(row.storeCodes),
    deviceCodes: strings(row.deviceCodes),
    total: num(row.total),
    dangerCount: num(row.dangerCount),
    abnormalCount: num(row.abnormalCount),
    pendingReview: num(row.pendingReview),
    abnormalByRule: arr(row.abnormalByRule, (rule) => ({ ruleCode: str(rule.ruleCode) ?? "", count: num(rule.count) })),
    amountImpact: num(row.amountImpact),
  };
}
