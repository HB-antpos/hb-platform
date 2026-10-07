// 作废：必须填写原因（至少两个字）并二次确认。
import { CASH_NOTE_MAX_LENGTH, CASH_REASON_MIN_LENGTH } from "./constants";
import type { CashVoidRequest } from "./types";

export type VoidReasonIssue = "tooShort" | "tooLong";

/** 按字符（Unicode 码点）数计长，一个汉字算一个字。 */
function charLength(value: string): number {
  return Array.from(value).length;
}

export function validateVoidReason(reason: string): VoidReasonIssue | null {
  const length = charLength(reason.trim());
  if (length < CASH_REASON_MIN_LENGTH) return "tooShort";
  if (length > CASH_NOTE_MAX_LENGTH) return "tooLong";
  return null;
}

export function buildVoidRequest(reason: string): CashVoidRequest | null {
  return validateVoidReason(reason) ? null : { reason: reason.trim() };
}
