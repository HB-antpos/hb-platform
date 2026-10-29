// 详情页「MYOB 纸面」的行构造：字段与顺序沿用 MYOB 默认 Pay Slip，窄屏下把六列明细表重排成「两行一条」：
// 第一行 描述 + 类型标签 + 本期金额；第二行 小时 × 费率 + YTD。数值逐项取自后端，不在客户端计算。
import { formatAbn, formatDate, formatHourlyRate, formatHours, formatMoney, formatRate } from "./format";
import type { PayslipDetail, PayslipLine } from "./types";

const EM_DASH = "—";

/** 类型的大类，只用于样式与排序无关的判断（Entitlement 行的数字是小时不是金额）。 */
export type PayslipLineKind = "wages" | "tax" | "super" | "entitlement" | "deduction" | "other";

export interface PayslipLineView {
  key: string;
  description: string;
  /** 类型标签文字（Wages / Tax / Super / Entitlement …），后端 typeLabel 缺失时用 type。 */
  typeLabel: string;
  kind: PayslipLineKind;
  /** 本期金额；Entitlement 行没有金额，显示「—」。 */
  amountText: string;
  amountNegative: boolean;
  /** 第二行左侧：30.00 h × $24.00 / 1.15 h；都没有时为空串。 */
  calcText: string;
  /** 第二行右侧：YTD $11,520.00 / YTD 18.45 h；没有时为空串。 */
  ytdText: string;
}

export function resolveLineKind(type: string): PayslipLineKind {
  const normalized = type.trim().toLowerCase();
  if (normalized.startsWith("entitlement")) return "entitlement";
  if (normalized.startsWith("wage")) return "wages";
  if (normalized.startsWith("tax")) return "tax";
  if (normalized.startsWith("super")) return "super";
  if (normalized.startsWith("deduction")) return "deduction";
  return "other";
}

export function buildLineView(line: PayslipLine, index: number): PayslipLineView {
  const kind = resolveLineKind(line.type);
  const isEntitlement = kind === "entitlement";
  const hoursText = line.hours !== null ? formatHours(line.hours) : "";
  const rateText = line.rate !== null ? formatRate(line.rate) : "";
  let calcText = "";
  if (hoursText && rateText) calcText = `${hoursText} × ${rateText}`;
  else calcText = hoursText || rateText;

  let ytdText = "";
  if (line.yearToDate !== null) {
    // Entitlement 的 YTD 是累计小时，其余是累计金额。
    ytdText = `YTD ${isEntitlement ? formatHours(line.yearToDate) : formatMoney(line.yearToDate)}`;
  }

  return {
    key: `${index}|${line.description}|${line.type}`,
    description: line.description,
    typeLabel: line.typeLabel || line.type,
    kind,
    amountText: isEntitlement || line.amount === null ? EM_DASH : formatMoney(line.amount),
    amountNegative: !isEntitlement && line.amount !== null && line.amount < 0,
    calcText,
    ytdText,
  };
}

export function buildLineViews(lines: readonly PayslipLine[]): PayslipLineView[] {
  return lines.map(buildLineView);
}

export interface PayslipPaperField {
  /** MYOB 原文标签（含冒号），不翻译。 */
  label: string;
  value: string;
}

/** 纸面抬头字段：缺值的字段（没有 Reference Number / Hourly Rate / 退休金基金）整行不出现，与 MYOB 纸面一致。 */
export function buildPaperFields(detail: PayslipDetail): PayslipPaperField[] {
  const fields: PayslipPaperField[] = [];
  const abn = formatAbn(detail.companyAbn);
  if (abn) fields.push({ label: "ABN:", value: abn });
  fields.push({ label: "Pay Slip For:", value: detail.employeeName });
  if (detail.referenceNumber) fields.push({ label: "Reference Number:", value: detail.referenceNumber });
  fields.push({ label: "Payment Date:", value: formatDate(detail.paymentDate) });
  if (detail.hourlyRate !== null) fields.push({ label: "Hourly Rate:", value: formatHourlyRate(detail.hourlyRate) });
  const periodStart = formatDate(detail.periodStart);
  const periodEnd = formatDate(detail.periodEnd);
  if (periodStart || periodEnd) {
    fields.push({ label: "Pay Period From:", value: `${periodStart} To: ${periodEnd}` });
  }
  if (detail.superFundName) fields.push({ label: "Superannuation Fund:", value: detail.superFundName });
  return fields;
}
