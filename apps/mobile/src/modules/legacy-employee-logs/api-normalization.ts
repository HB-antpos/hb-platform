import type { LegacyLogItem, LegacyLogReview } from "./types";

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
    flags: arr(r.flags, (flag) => ({
      ruleCode: str(flag.ruleCode) ?? "",
      evidence: (flag.evidence && typeof flag.evidence === "object" ? flag.evidence : {}) as Record<string, string>,
      detectedAtUtc: str(flag.detectedAtUtc) ?? "",
    })).filter((flag) => flag.ruleCode),
    review: normalizeLegacyLogReview(r.review),
    amountImpact: typeof r.amountImpact === "number" ? r.amountImpact : null,
  };
}
