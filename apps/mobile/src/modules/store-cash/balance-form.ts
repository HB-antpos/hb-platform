// 期初现金与盘点表单的纯逻辑。
// 期初：每店至多一条有效记录，只有 summary.openingMissing 时才可录入；金额 ≥ 0，日期不晚于门店今天。
// 盘点：任意时刻可录；提交后展示与应有余额的差异。
import { CASH_NOTE_MAX_LENGTH } from "./constants";
import { isDateWithinRange, isValidDateString, type EntryDateRange } from "./dates";
import { parseMoneyInput, type MoneyParseIssue } from "./money";
import type { CashBalanceEntry, CreateCashCountRequest, SetCashOpeningRequest } from "./types";

export type BalanceKind = "opening" | "count";

export interface BalanceDraft {
  kind: BalanceKind;
  entryDate: string;
  amountText: string;
  note: string;
}

export type BalanceIssue =
  | { code: "dateInvalid" }
  | { code: "dateOutOfRange" }
  | { code: "amount"; issue: MoneyParseIssue }
  | { code: "noteTooLong" };

export function createInitialBalance(kind: BalanceKind, storeToday: string): BalanceDraft {
  return { kind, entryDate: storeToday, amountText: "", note: "" };
}

/** 期初只要不晚于门店今天；盘点沿用补录窗口（店长回溯 maxBackfillDays，有全部分店权限者不限）。 */
export function resolveBalanceDateRange(
  kind: BalanceKind,
  options: { storeToday: string; entryRange: EntryDateRange },
): EntryDateRange {
  return kind === "opening" ? { min: null, max: options.storeToday } : options.entryRange;
}

/** 期初只在没有有效期初记录时可录入。 */
export function canRecordOpening(openingMissing: boolean, canCreateDeposit: boolean): boolean {
  return openingMissing && canCreateDeposit;
}

export function validateBalanceDraft(draft: BalanceDraft, range: EntryDateRange): BalanceIssue[] {
  const issues: BalanceIssue[] = [];
  if (!isValidDateString(draft.entryDate)) {
    issues.push({ code: "dateInvalid" });
  } else if (!isDateWithinRange(draft.entryDate, range)) {
    issues.push({ code: "dateOutOfRange" });
  }
  // 现金可以恰好为 0，所以期初与盘点都允许 0
  const amount = parseMoneyInput(draft.amountText, { allowZero: true });
  if (!amount.ok) issues.push({ code: "amount", issue: amount.issue });
  if (draft.note.trim().length > CASH_NOTE_MAX_LENGTH) issues.push({ code: "noteTooLong" });
  return issues;
}

function trimToUndefined(value: string): string | undefined {
  const trimmed = value.trim();
  return trimmed ? trimmed : undefined;
}

export function buildSetOpeningRequest(
  draft: BalanceDraft,
  options: { clientRequestId: string; storeCode: string },
): SetCashOpeningRequest | null {
  const amount = parseMoneyInput(draft.amountText, { allowZero: true });
  if (draft.kind !== "opening" || !amount.ok) return null;
  return {
    clientRequestId: options.clientRequestId,
    storeCode: options.storeCode,
    entryDate: draft.entryDate,
    amount: amount.value,
    note: trimToUndefined(draft.note),
  };
}

export function buildCreateCountRequest(
  draft: BalanceDraft,
  options: { clientRequestId: string; storeCode: string },
): CreateCashCountRequest | null {
  const amount = parseMoneyInput(draft.amountText, { allowZero: true });
  if (draft.kind !== "count" || !amount.ok) return null;
  return {
    clientRequestId: options.clientRequestId,
    storeCode: options.storeCode,
    entryDate: draft.entryDate,
    amount: amount.value,
    note: trimToUndefined(draft.note),
  };
}

export type CountOutcome =
  | { kind: "balanced"; difference: number }
  | { kind: "over"; difference: number }
  | { kind: "short"; difference: number }
  | { kind: "unknown" };

/** 盘点结果的差异归类：difference 可能为 null（当时日结未接入或没有期初），此时不做对比。 */
export function describeCountOutcome(entry: Pick<CashBalanceEntry, "difference">): CountOutcome {
  const { difference } = entry;
  if (difference == null || !Number.isFinite(difference)) return { kind: "unknown" };
  const cents = Math.round(difference * 100);
  if (cents === 0) return { kind: "balanced", difference: 0 };
  return cents > 0 ? { kind: "over", difference } : { kind: "short", difference };
}
