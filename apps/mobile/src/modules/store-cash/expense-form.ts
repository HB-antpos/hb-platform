// 现金支出表单的纯逻辑：类别规则、校验、请求体构造。
// 类别界面文案固定为「现金工资」「现金购物」「T2」「其他」，由 i18n 提供；这里只处理类别码。
import {
  CASH_NOTE_MAX_LENGTH,
  CASH_PAYEE_CATEGORY,
  CASH_PAYEE_NAME_MAX_LENGTH,
  CASH_RECEIPT_REQUIRED_CATEGORY,
} from "./constants";
import { isDateWithinRange, isValidDateString, type EntryDateRange } from "./dates";
import { parseMoneyInput, type MoneyParseIssue } from "./money";
import {
  appendPhotos,
  collectAttachmentGuids,
  patchPhoto,
  removePhoto,
  resetPhotosForReupload,
  type PhotoDraft,
} from "./photo-drafts";
import type { CashExpenseCategory, CreateCashExpenseRequest } from "./types";

export interface ExpenseDraft {
  expenseDate: string;
  /** 不预选类别，避免默认值造成静默错记。 */
  category: CashExpenseCategory | null;
  amountText: string;
  payeeName: string;
  note: string;
  photos: PhotoDraft[];
}

export interface ExpenseLimits {
  maxImages: number;
  dateRange: EntryDateRange;
}

export type ExpenseIssue =
  | { code: "dateInvalid" }
  | { code: "dateOutOfRange" }
  | { code: "categoryMissing" }
  | { code: "amount"; issue: MoneyParseIssue }
  | { code: "receiptRequired" }
  | { code: "photosTooMany" }
  | { code: "payeeTooLong" }
  | { code: "noteTooLong" };

export function createInitialExpense(storeToday: string): ExpenseDraft {
  return {
    expenseDate: storeToday,
    category: null,
    amountText: "",
    payeeName: "",
    note: "",
    photos: [],
  };
}

/** 购物类别必须至少 1 张收据；其他类别图片可选。 */
export function isReceiptRequired(category: CashExpenseCategory | null): boolean {
  return category === CASH_RECEIPT_REQUIRED_CATEGORY;
}

/** 仅工资类别展示并提交收款人姓名。 */
export function isPayeeApplicable(category: CashExpenseCategory | null): boolean {
  return category === CASH_PAYEE_CATEGORY;
}

export function validateExpenseDraft(draft: ExpenseDraft, limits: ExpenseLimits): ExpenseIssue[] {
  const issues: ExpenseIssue[] = [];

  if (!isValidDateString(draft.expenseDate)) {
    issues.push({ code: "dateInvalid" });
  } else if (!isDateWithinRange(draft.expenseDate, limits.dateRange)) {
    issues.push({ code: "dateOutOfRange" });
  }

  if (!draft.category) issues.push({ code: "categoryMissing" });

  const amount = parseMoneyInput(draft.amountText);
  if (!amount.ok) issues.push({ code: "amount", issue: amount.issue });

  if (isReceiptRequired(draft.category) && draft.photos.length === 0) {
    issues.push({ code: "receiptRequired" });
  }
  if (draft.photos.length > limits.maxImages) issues.push({ code: "photosTooMany" });

  if (isPayeeApplicable(draft.category) && draft.payeeName.trim().length > CASH_PAYEE_NAME_MAX_LENGTH) {
    issues.push({ code: "payeeTooLong" });
  }
  if (draft.note.trim().length > CASH_NOTE_MAX_LENGTH) issues.push({ code: "noteTooLong" });

  return issues;
}

function trimToUndefined(value: string): string | undefined {
  const trimmed = value.trim();
  return trimmed ? trimmed : undefined;
}

/** 构造 POST cash/expenses 请求体；校验未过或照片未全部上传成功时返回 null。 */
export function buildCreateExpenseRequest(
  draft: ExpenseDraft,
  options: { clientRequestId: string; storeCode: string },
): CreateCashExpenseRequest | null {
  const amount = parseMoneyInput(draft.amountText);
  const attachmentGuids = collectAttachmentGuids(draft.photos);
  if (!draft.category || !amount.ok || !attachmentGuids) return null;
  return {
    clientRequestId: options.clientRequestId,
    storeCode: options.storeCode,
    expenseDate: draft.expenseDate,
    category: draft.category,
    amount: amount.value,
    // 收款人只对工资类别有意义，切换类别后残留的姓名不能带出去
    payeeName: isPayeeApplicable(draft.category) ? trimToUndefined(draft.payeeName) : undefined,
    note: trimToUndefined(draft.note),
    attachmentGuids,
  };
}

export function appendExpensePhotos(
  draft: ExpenseDraft,
  incoming: readonly PhotoDraft[],
  maxImages: number,
): { draft: ExpenseDraft; rejected: number } {
  const result = appendPhotos(draft.photos, incoming, maxImages);
  return { draft: { ...draft, photos: result.photos }, rejected: result.rejected };
}

export function patchExpensePhoto(
  draft: ExpenseDraft,
  photoKey: string,
  patch: Partial<Omit<PhotoDraft, "key">>,
): ExpenseDraft {
  return { ...draft, photos: patchPhoto(draft.photos, photoKey, patch) };
}

export function removeExpensePhoto(draft: ExpenseDraft, photoKey: string): ExpenseDraft {
  return { ...draft, photos: removePhoto(draft.photos, photoKey) };
}

export function resetExpensePhotos(draft: ExpenseDraft): ExpenseDraft {
  return { ...draft, photos: resetPhotosForReupload(draft.photos) };
}
