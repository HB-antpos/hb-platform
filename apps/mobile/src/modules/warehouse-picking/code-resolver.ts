import type { PickCodeEntry } from "./types";

/** 配货单二维码前缀：PDA 同一个扫码入口靠它区分“订单码”和“商品条码”。 */
export const ORDER_QR_PREFIX = "HBSO:";

export type ScanResolution =
  | { kind: "order"; code: string; orderNo: string }
  | { kind: "location"; code: string; detailGuids: string[] }
  | { kind: "line"; code: string; detailGuid: string; matchedBy: number | null; label: string | null }
  | { kind: "multiple"; code: string; detailGuids: string[]; matchedBy: number | null; label: string | null }
  | { kind: "none"; code: string };

/** 扫码枪可能带回车、制表符或首尾空格；比较时统一去掉并转大写。 */
export function normalizeScanCode(raw: string): string {
  return raw.replace(/[\u0000-\u001f\u007f]/g, "").trim().toUpperCase();
}

export type PickCodeIndex = Map<string, PickCodeEntry[]>;

export function buildPickCodeIndex(codes: readonly PickCodeEntry[]): PickCodeIndex {
  const index: PickCodeIndex = new Map();
  for (const entry of codes) {
    const key = normalizeScanCode(entry.code);
    if (!key) continue;
    const bucket = index.get(key);
    if (bucket) {
      bucket.push(entry);
    } else {
      index.set(key, [entry]);
    }
  }
  return index;
}

/**
 * 把一次扫码解析成动作。优先级：订单二维码 → 商品码（主码/货号/多码/套装子码）→ 货位码。
 * 同一个码命中本单多行时交给界面让拣货员选择；其中包含当前行则直接落在当前行，避免重复弹窗。
 */
export function resolvePickScan(
  raw: string,
  index: PickCodeIndex,
  currentDetailGuid?: string | null,
): ScanResolution {
  const code = normalizeScanCode(raw);
  if (code.startsWith(ORDER_QR_PREFIX)) {
    const orderNo = code.slice(ORDER_QR_PREFIX.length).trim();
    if (orderNo) {
      return { kind: "order", code, orderNo };
    }
  }

  const entries = index.get(code) ?? [];
  const lineEntries = entries.filter((entry) => entry.target === "line");
  const lineGuids = unique(lineEntries.flatMap((entry) => entry.detailGuids));
  if (lineGuids.length > 0) {
    // 同一码可能既是某行主码又是另一行的子码，取第一条命中的匹配方式作提示即可。
    const primary = lineEntries[0];
    if (lineGuids.length === 1) {
      return { kind: "line", code, detailGuid: lineGuids[0], matchedBy: primary.matchedBy, label: primary.label };
    }
    if (currentDetailGuid && lineGuids.includes(currentDetailGuid)) {
      const current = lineEntries.find((entry) => entry.detailGuids.includes(currentDetailGuid)) ?? primary;
      return { kind: "line", code, detailGuid: currentDetailGuid, matchedBy: current.matchedBy, label: current.label };
    }
    return { kind: "multiple", code, detailGuids: lineGuids, matchedBy: primary.matchedBy, label: primary.label };
  }

  const locationGuids = unique(
    entries.filter((entry) => entry.target === "location").flatMap((entry) => entry.detailGuids),
  );
  if (locationGuids.length > 0) {
    return { kind: "location", code, detailGuids: locationGuids };
  }

  return { kind: "none", code };
}

function unique(values: string[]): string[] {
  return Array.from(new Set(values));
}
