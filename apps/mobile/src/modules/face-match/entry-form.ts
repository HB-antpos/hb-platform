// 提交名单条目表单的纯逻辑：校验（与后端硬条件一致）、请求体构造、弹窗里的必填说明校验。
import {
  CATEGORY_NOTE_MAX_LENGTH,
  CATEGORY_ORGANISED_THEFT,
  CATEGORY_OTHER,
  CATEGORY_VIOLENCE,
  DISPUTE_NOTE_MAX_LENGTH,
  EVIDENCE_REF_MAX_LENGTH,
  PHOTO_MAX_BYTES,
  REASON_MAX_LENGTH,
  UNWELCOME_MAX_EXPIRY_MONTHS,
  UNWELCOME_PHOTO_MAX,
  UNWELCOME_PHOTO_MIN,
} from "./constants";
import { expiryToUtcIso, maxExpiryDay, minExpiryDay, parseYmd } from "./dates";
import type { FacePhotoDraft } from "./types";

/** 一条校验结果：文案键（faceMatch 命名空间）+ 插值参数。 */
export interface ValidationIssue {
  key: string;
  params?: Record<string, unknown>;
}

/** 去首尾空白；空串（含全空白）规范成 null，后端把 null 当作「未填写」。 */
export function normalizeText(value: string | null | undefined): string | null {
  const trimmed = (value ?? "").trim();
  return trimmed ? trimmed : null;
}

export interface EntryFormValues {
  incidentCategory: number | null;
  categoryNote: string;
  evidenceRef: string;
  storeScope: string[];
  /** 到期日 yyyy-MM-dd（手机本地时区）。 */
  expiresOn: string;
}

export type EntryFormField = keyof EntryFormValues | "photos";
export type EntryFormErrors = Partial<Record<EntryFormField, ValidationIssue>>;

export function createEmptyEntryForm(): EntryFormValues {
  return { incidentCategory: null, categoryNote: "", evidenceRef: "", storeScope: [], expiresOn: "" };
}

/**
 * 提交条目校验（与后端硬条件一致）：类别必须是枚举（没有一般偷窃）；Other 必须说明（≤200）；
 * 证据编号必填（≤100）；至少一家生效门店；到期日必填且晚于今天、不超过 12 个月；参考照片 1–5 张。
 * 管理员的批准人数等规则由服务端强制，这里不涉及。
 */
export function validateEntryForm(
  values: EntryFormValues,
  photos: readonly Pick<FacePhotoDraft, "byteSize">[],
  now: Date,
): EntryFormErrors {
  const errors: EntryFormErrors = {};
  const category = values.incidentCategory;
  if (category !== CATEGORY_VIOLENCE && category !== CATEGORY_ORGANISED_THEFT && category !== CATEGORY_OTHER) {
    errors.incidentCategory = { key: "validation.categoryRequired" };
  }

  const note = values.categoryNote.trim();
  if (category === CATEGORY_OTHER && !note) {
    errors.categoryNote = { key: "validation.categoryNoteRequired" };
  } else if (note.length > CATEGORY_NOTE_MAX_LENGTH) {
    errors.categoryNote = { key: "validation.categoryNoteTooLong", params: { max: CATEGORY_NOTE_MAX_LENGTH } };
  }

  const evidence = values.evidenceRef.trim();
  if (!evidence) errors.evidenceRef = { key: "validation.evidenceRefRequired" };
  else if (evidence.length > EVIDENCE_REF_MAX_LENGTH) {
    errors.evidenceRef = { key: "validation.evidenceRefTooLong", params: { max: EVIDENCE_REF_MAX_LENGTH } };
  }

  if (values.storeScope.length === 0) errors.storeScope = { key: "validation.storeScopeRequired" };

  const expiresIssue = validateExpiry(values.expiresOn, now);
  if (expiresIssue) errors.expiresOn = expiresIssue;

  const photoIssue = validatePhotoSet(photos);
  if (photoIssue) errors.photos = photoIssue;
  return errors;
}

export function validateExpiry(expiresOn: string, now: Date): ValidationIssue | null {
  if (!parseYmd(expiresOn)) return { key: "validation.expiresRequired" };
  if (expiresOn < minExpiryDay(now)) return { key: "validation.expiresPast" };
  if (expiresOn > maxExpiryDay(now)) {
    return { key: "validation.expiresTooFar", params: { months: UNWELCOME_MAX_EXPIRY_MONTHS } };
  }
  return null;
}

/** 照片集合：张数范围 + 逐张大小（压缩后的 JPEG 必须 ≤ 5MB 且非空）。 */
export function validatePhotoSet(photos: readonly Pick<FacePhotoDraft, "byteSize">[]): ValidationIssue | null {
  if (photos.length < UNWELCOME_PHOTO_MIN || photos.length > UNWELCOME_PHOTO_MAX) {
    return { key: "validation.photoCountRange", params: { min: UNWELCOME_PHOTO_MIN, max: UNWELCOME_PHOTO_MAX } };
  }
  for (const photo of photos) {
    if (photo.byteSize <= 0) return { key: "validation.photoEmpty" };
    if (photo.byteSize > PHOTO_MAX_BYTES) {
      return { key: "validation.photoTooLarge", params: { maxMb: PHOTO_MAX_BYTES / (1024 * 1024) } };
    }
  }
  return null;
}

export function hasEntryFormErrors(errors: EntryFormErrors): boolean {
  return Object.keys(errors).length > 0;
}

/** 弹出「放弃填写」确认的条件：表单里任一字段或照片已被改动。 */
export function isEntryFormDirty(values: EntryFormValues, photoCount: number): boolean {
  return (
    values.incidentCategory !== null ||
    values.categoryNote.trim() !== "" ||
    values.evidenceRef.trim() !== "" ||
    values.storeScope.length > 0 ||
    values.expiresOn !== "" ||
    photoCount > 0
  );
}

export interface CreateEntryInput {
  incidentCategory: number;
  /** 只有 Other 才需要说明；其余类别即使残留说明也不提交。 */
  categoryNote: string | null;
  evidenceRef: string;
  storeScope: string[];
  expiresAtUtc: string;
  photos: FacePhotoDraft[];
}

/** 校验通过后构造提交内容（调用前必须先 validateEntryForm）。 */
export function buildCreateEntryInput(
  values: EntryFormValues,
  photos: FacePhotoDraft[],
  now: Date,
): CreateEntryInput {
  return {
    incidentCategory: values.incidentCategory ?? CATEGORY_OTHER,
    categoryNote: values.incidentCategory === CATEGORY_OTHER ? normalizeText(values.categoryNote) : null,
    evidenceRef: values.evidenceRef.trim(),
    storeScope: [...new Set(values.storeScope.map((code) => code.trim()).filter(Boolean))].sort(),
    expiresAtUtc: expiryToUtcIso(values.expiresOn, now),
    photos,
  };
}

// ───────────── 撤回原因 / 申诉备注 ─────────────

/** 撤回原因必填（≤200）。 */
export function validateWithdrawReason(value: string | null | undefined): ValidationIssue | null {
  const text = (value ?? "").trim();
  if (!text) return { key: "validation.textRequired" };
  if (text.length > REASON_MAX_LENGTH) return { key: "validation.textTooLong", params: { max: REASON_MAX_LENGTH } };
  return null;
}

/** 申诉备注可留空（清空备注），最长 500。 */
export function validateDisputeNote(value: string | null | undefined): ValidationIssue | null {
  const text = (value ?? "").trim();
  if (text.length > DISPUTE_NOTE_MAX_LENGTH) {
    return { key: "validation.textTooLong", params: { max: DISPUTE_NOTE_MAX_LENGTH } };
  }
  return null;
}
