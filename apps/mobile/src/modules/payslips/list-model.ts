// 列表页的纯展示逻辑：卡片上的分店 / 归属文案、行 key、汇总格。无 React 依赖，便于单测。
import { formatDate, formatMoney, formatPeriodShort } from "./format";
import type { PayslipListRow, PayslipListResult } from "./types";

export function buildRowKey(row: Pick<PayslipListRow, "connectionGuid" | "employeeUid" | "paymentDate" | "periodStart">): string {
  return [row.connectionGuid, row.employeeUid, row.paymentDate, row.periodStart ?? ""].join("|");
}

/** 卡片第二行左侧的归属：未绑定 / 分店未知用标签，其余显示分店名（多店显示第一家 + 「+N」）。 */
export type RowPlacementView =
  | { kind: "unbound" }
  | { kind: "unknown" }
  | { kind: "stores"; label: string; inferred: boolean };

export function resolveRowPlacement(row: Pick<PayslipListRow, "placement" | "stores">): RowPlacementView {
  if (row.placement === "Unbound") return { kind: "unbound" };
  if (row.stores.length === 0) return { kind: "unknown" };
  const [first, ...rest] = row.stores;
  const label = rest.length > 0 ? `${first.storeLabel} +${rest.length}` : first.storeLabel;
  return { kind: "stores", label, inferred: row.placement === "Inferred" };
}

export interface RowTexts {
  netText: string;
  grossText: string;
  paymentDateText: string;
  periodText: string;
}

export function buildRowTexts(row: PayslipListRow): RowTexts {
  return {
    netText: formatMoney(row.netPay),
    grossText: formatMoney(row.grossPay),
    paymentDateText: formatDate(row.paymentDate),
    periodText: [formatPeriodShort(row.periodStart, row.periodEnd), row.payFrequency].filter(Boolean).join(" · "),
  };
}

export interface SummaryTexts {
  count: number;
  grossText: string;
  netText: string;
}

export function buildSummary(result: Pick<PayslipListResult, "totalCount" | "grossTotal" | "netTotal">): SummaryTexts {
  return { count: result.totalCount, grossText: formatMoney(result.grossTotal), netText: formatMoney(result.netTotal) };
}

/** 有公司读取失败时，汇总数字不完整，页面要给出提示。 */
export function failedCompanies(result: Pick<PayslipListResult, "companies">) {
  return result.companies.filter((company) => !company.ok);
}
