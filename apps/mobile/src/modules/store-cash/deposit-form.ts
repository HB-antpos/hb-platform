// 存银行表单的纯逻辑：草稿结构、合计、差异原因是否必填、校验、请求体构造。
// 不依赖 React / 原生模块，全部可在 Node 下单测。
import { CASH_MIN_IMAGES_PER_SLIP, CASH_NOTE_MAX_LENGTH, CASH_SLIP_NO_MAX_LENGTH, CASH_REASON_MIN_LENGTH } from "./constants";
import {
  compareDates,
  isDateWithinRange,
  isValidDateString,
  type EntryDateRange,
} from "./dates";
import { amountToInputText, fromCents, parseMoneyInput, toCents, type MoneyParseIssue } from "./money";
import {
  appendPhotos,
  collectAttachmentGuids,
  patchPhoto,
  removePhoto,
  resetPhotosForReupload,
  type PhotoDraft,
} from "./photo-drafts";
import type { CashDepositSlipInput, CreateCashDepositRequest } from "./types";

export interface DepositSlipDraft {
  key: string;
  amountText: string;
  slipNo: string;
  photos: PhotoDraft[];
}

export interface DepositDraft {
  depositDate: string;
  /** 覆盖营业日起止，允许两者都清空。 */
  coveredFromDate: string | null;
  coveredToDate: string | null;
  note: string;
  overrideReason: string;
  slips: DepositSlipDraft[];
}

export interface DepositLimits {
  maxSlips: number;
  maxImagesPerSlip: number;
  /** 合计与基准额相差超过该金额必须填原因。 */
  differenceThreshold: number;
  /**
   * 差异原因的比较基准（后端口径：现金池余额，负数按 0），见 resolveDepositBaseline；
   * null 表示余额不可算（日结未接入或缺期初），客户端不主动要求原因。
   */
  baselineAmount: number | null;
  /**
   * 服务端已经以 CASH_OVERRIDE_REASON_REQUIRED 拒绝过一次：不管客户端怎么算，原因必须填。
   * 用于客户端与服务端基准不一致（例如余额在填表期间变了）时兜底。
   */
  forceOverrideReason?: boolean;
  dateRange: EntryDateRange;
  storeToday: string;
}

export type DepositIssue =
  | { code: "dateInvalid" }
  | { code: "dateOutOfRange" }
  | { code: "coveredIncomplete" }
  | { code: "coveredInvalid" }
  | { code: "coveredReversed" }
  | { code: "coveredFuture" }
  | { code: "noteTooLong" }
  | { code: "slipsEmpty" }
  | { code: "slipsTooMany" }
  | { code: "slipAmount"; slipKey: string; issue: MoneyParseIssue }
  | { code: "slipPhotosMissing"; slipKey: string }
  | { code: "slipPhotosTooMany"; slipKey: string }
  | { code: "slipNoTooLong"; slipKey: string }
  | { code: "overrideReasonRequired" };

export function createEmptySlip(key: string, amountText = ""): DepositSlipDraft {
  return { key, amountText, slipNo: "", photos: [] };
}

/**
 * 新建存款草稿。存款日期默认门店今天；第一张存单金额预填建议存款额（建议额为正时），
 * 覆盖营业日默认取调用方算好的范围，两者都允许用户改或清空。
 */
export function createInitialDeposit(options: {
  storeToday: string;
  suggestedAmount: number | null;
  covered: { from: string | null; to: string | null };
  firstSlipKey: string;
}): DepositDraft {
  const { suggestedAmount } = options;
  return {
    depositDate: options.storeToday,
    coveredFromDate: options.covered.from,
    coveredToDate: options.covered.to,
    note: "",
    overrideReason: "",
    slips: [
      createEmptySlip(
        options.firstSlipKey,
        suggestedAmount != null && suggestedAmount > 0 ? amountToInputText(suggestedAmount) : "",
      ),
    ],
  };
}

/**
 * 默认覆盖范围：从 oldestUncoveredDate 起，到「最近有日结的日期」止。
 * latestCloseDate 取 summary.latestCloseDate；取不到时（日结未接入或还没有存档）
 * 退回 fallbackTo（调用方传 summary.asOfDate，即门店今天），再不行就整体留空，由用户自己填。
 */
export function resolveDefaultCoveredRange(options: {
  oldestUncoveredDate: string | null;
  latestCloseDate: string | null;
  fallbackTo: string | null;
}): { from: string | null; to: string | null } {
  const { oldestUncoveredDate } = options;
  if (!isValidDateString(oldestUncoveredDate)) return { from: null, to: null };
  const candidate = [options.latestCloseDate, options.fallbackTo].find(
    (value): value is string => isValidDateString(value) && compareDates(value, oldestUncoveredDate) >= 0,
  );
  return candidate ? { from: oldestUncoveredDate, to: candidate } : { from: null, to: null };
}

/** 各存单金额之和（无效金额按 0 计），按分累加。 */
export function computeDepositTotal(slips: readonly DepositSlipDraft[]): number {
  let cents = 0;
  for (const slip of slips) {
    const parsed = parseMoneyInput(slip.amountText);
    if (parsed.ok) cents += parsed.cents;
  }
  return fromCents(cents);
}

/**
 * 差异原因的比较基准：与后端一致取「现金池余额」，余额为负按 0。
 * - 余额可算（含 0 或负数）：基准 = max(余额, 0)。余额 ≤ 0 时 suggestedDepositAmount 是 null，
 *   这时只要存款合计 > 阈值就需要原因，不能因为「没有建议额」而漏掉。
 * - 余额不可算（poolBalance 为 null）：退回 suggestedDepositAmount（此时通常也是 null）；
 *   两者都没有就返回 null，客户端不主动要求，由服务端的 CASH_OVERRIDE_REASON_REQUIRED 兜底。
 */
export function resolveDepositBaseline(options: {
  poolBalance: number | null;
  suggestedDepositAmount: number | null;
}): number | null {
  const { poolBalance, suggestedDepositAmount } = options;
  if (poolBalance != null && Number.isFinite(poolBalance)) {
    return Math.max(0, fromCents(toCents(poolBalance)));
  }
  return suggestedDepositAmount ?? null;
}

/** 存款合计与基准额之差（合计 − 基准）；基准不可算时为 null。 */
export function computeDepositDifference(total: number, baselineAmount: number | null): number | null {
  if (baselineAmount == null) return null;
  return fromCents(toCents(total) - toCents(baselineAmount));
}

/** 相差严格大于阈值才要求填差异原因；基准为 null 时不要求。 */
export function isOverrideReasonRequired(
  total: number,
  baselineAmount: number | null,
  threshold: number,
): boolean {
  const difference = computeDepositDifference(total, baselineAmount);
  if (difference == null) return false;
  return Math.abs(toCents(difference)) > toCents(threshold);
}

/**
 * 是否显示「差异原因」输入框：客户端算出需要、服务端已要求过，或用户已经写了内容（不能把写好的原因藏掉）。
 */
export function isOverrideReasonVisible(
  draft: Pick<DepositDraft, "slips" | "overrideReason">,
  limits: Pick<DepositLimits, "baselineAmount" | "differenceThreshold" | "forceOverrideReason">,
): boolean {
  if (limits.forceOverrideReason || draft.overrideReason.trim()) return true;
  const amountsComplete =
    draft.slips.length > 0 && draft.slips.every((slip) => parseMoneyInput(slip.amountText).ok);
  return (
    amountsComplete &&
    isOverrideReasonRequired(computeDepositTotal(draft.slips), limits.baselineAmount, limits.differenceThreshold)
  );
}

export function canAddSlip(draft: DepositDraft, maxSlips: number): boolean {
  return draft.slips.length < Math.trunc(maxSlips);
}

export function addSlip(draft: DepositDraft, key: string, maxSlips: number): DepositDraft {
  if (!canAddSlip(draft, maxSlips)) return draft;
  return { ...draft, slips: [...draft.slips, createEmptySlip(key)] };
}

/** 至少保留一张存单；删除最后一张时返回原草稿。 */
export function removeSlip(draft: DepositDraft, key: string): DepositDraft {
  if (draft.slips.length <= 1) return draft;
  return { ...draft, slips: draft.slips.filter((slip) => slip.key !== key) };
}

export function updateSlip(
  draft: DepositDraft,
  key: string,
  patch: Partial<Omit<DepositSlipDraft, "key">>,
): DepositDraft {
  return { ...draft, slips: draft.slips.map((slip) => (slip.key === key ? { ...slip, ...patch } : slip)) };
}

export function validateDepositDraft(draft: DepositDraft, limits: DepositLimits): DepositIssue[] {
  const issues: DepositIssue[] = [];

  if (!isValidDateString(draft.depositDate)) {
    issues.push({ code: "dateInvalid" });
  } else if (!isDateWithinRange(draft.depositDate, limits.dateRange)) {
    issues.push({ code: "dateOutOfRange" });
  }

  const from = draft.coveredFromDate;
  const to = draft.coveredToDate;
  if (from || to) {
    if (!from || !to) {
      issues.push({ code: "coveredIncomplete" });
    } else if (!isValidDateString(from) || !isValidDateString(to)) {
      issues.push({ code: "coveredInvalid" });
    } else if (compareDates(from, to) > 0) {
      issues.push({ code: "coveredReversed" });
    } else if (compareDates(to, coveredUpperBound(draft.depositDate, limits.storeToday)) > 0) {
      // 与服务端一致：覆盖的营业日不能晚于存款日期（存款日期本身不晚于门店今天）。
      issues.push({ code: "coveredFuture" });
    }
  }

  if (draft.note.trim().length > CASH_NOTE_MAX_LENGTH) issues.push({ code: "noteTooLong" });

  if (draft.slips.length === 0) issues.push({ code: "slipsEmpty" });
  if (draft.slips.length > limits.maxSlips) issues.push({ code: "slipsTooMany" });

  for (const slip of draft.slips) {
    const amount = parseMoneyInput(slip.amountText);
    if (!amount.ok) issues.push({ code: "slipAmount", slipKey: slip.key, issue: amount.issue });
    if (slip.photos.length < CASH_MIN_IMAGES_PER_SLIP) {
      issues.push({ code: "slipPhotosMissing", slipKey: slip.key });
    }
    if (slip.photos.length > limits.maxImagesPerSlip) {
      issues.push({ code: "slipPhotosTooMany", slipKey: slip.key });
    }
    if (slip.slipNo.trim().length > CASH_SLIP_NO_MAX_LENGTH) {
      issues.push({ code: "slipNoTooLong", slipKey: slip.key });
    }
  }

  // 存单为空或有金额不合法时合计没有意义，不再叠加「差异原因」的提示，先让用户改金额；
  // 但服务端已明确要求原因（forceOverrideReason）时无论如何都要填。
  const amountsComplete =
    draft.slips.length > 0 && draft.slips.every((slip) => parseMoneyInput(slip.amountText).ok);
  const total = computeDepositTotal(draft.slips);
  const reasonNeeded =
    limits.forceOverrideReason === true ||
    (amountsComplete && isOverrideReasonRequired(total, limits.baselineAmount, limits.differenceThreshold));
  if (reasonNeeded && Array.from(draft.overrideReason.trim()).length < CASH_REASON_MIN_LENGTH) {
    issues.push({ code: "overrideReasonRequired" });
  }

  return issues;
}

/** 覆盖范围终点的上限：存款日期有效时取它，否则退回门店今天。 */
function coveredUpperBound(depositDate: string, storeToday: string): string {
  return isValidDateString(depositDate) && compareDates(depositDate, storeToday) < 0 ? depositDate : storeToday;
}

function trimToUndefined(value: string): string | undefined {
  const trimmed = value.trim();
  return trimmed ? trimmed : undefined;
}

/**
 * 构造 POST cash/deposits 请求体。要求草稿已通过校验、所有照片已上传成功；
 * 任一前置条件不满足返回 null，调用方先校验并上传完再调用，绝不带着残缺附件提交。
 */
export function buildCreateDepositRequest(
  draft: DepositDraft,
  options: { clientRequestId: string; storeCode: string },
): CreateCashDepositRequest | null {
  const slips: CashDepositSlipInput[] = [];
  for (const slip of draft.slips) {
    const amount = parseMoneyInput(slip.amountText);
    const attachmentGuids = collectAttachmentGuids(slip.photos);
    if (!amount.ok || !attachmentGuids || attachmentGuids.length === 0) return null;
    slips.push({ amount: amount.value, slipNo: trimToUndefined(slip.slipNo), attachmentGuids });
  }
  if (slips.length === 0) return null;
  const hasCovered = Boolean(draft.coveredFromDate && draft.coveredToDate);
  return {
    clientRequestId: options.clientRequestId,
    storeCode: options.storeCode,
    depositDate: draft.depositDate,
    coveredFromDate: hasCovered ? (draft.coveredFromDate as string) : undefined,
    coveredToDate: hasCovered ? (draft.coveredToDate as string) : undefined,
    note: trimToUndefined(draft.note),
    overrideReason: trimToUndefined(draft.overrideReason),
    slips,
  };
}

/** 草稿里所有照片（按存单顺序展平），用于统一上传与状态判断。 */
export function listDepositPhotos(draft: DepositDraft): PhotoDraft[] {
  return draft.slips.flatMap((slip) => slip.photos);
}

/** 往某张存单追加照片，超出 maxPerSlip 的丢弃；返回新草稿与被丢弃的张数。 */
export function appendDepositPhotos(
  draft: DepositDraft,
  slipKey: string,
  incoming: readonly PhotoDraft[],
  maxPerSlip: number,
): { draft: DepositDraft; rejected: number } {
  let rejected = 0;
  const slips = draft.slips.map((slip) => {
    if (slip.key !== slipKey) return slip;
    const result = appendPhotos(slip.photos, incoming, maxPerSlip);
    rejected = result.rejected;
    return { ...slip, photos: result.photos };
  });
  return { draft: { ...draft, slips }, rejected };
}

/** 按照片键更新状态（上传进度、失败、附件编号），不管它在哪张存单里。 */
export function patchDepositPhoto(
  draft: DepositDraft,
  photoKey: string,
  patch: Partial<Omit<PhotoDraft, "key">>,
): DepositDraft {
  return { ...draft, slips: draft.slips.map((slip) => ({ ...slip, photos: patchPhoto(slip.photos, photoKey, patch) })) };
}

export function removeDepositPhoto(draft: DepositDraft, photoKey: string): DepositDraft {
  return { ...draft, slips: draft.slips.map((slip) => ({ ...slip, photos: removePhoto(slip.photos, photoKey) })) };
}

/** 所有存单的照片退回待上传（附件失效后重新上传）。 */
export function resetDepositPhotos(draft: DepositDraft): DepositDraft {
  return { ...draft, slips: draft.slips.map((slip) => ({ ...slip, photos: resetPhotosForReupload(slip.photos) })) };
}
