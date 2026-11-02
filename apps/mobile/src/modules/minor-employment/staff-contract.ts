/**
 * 店长 / HR 在「人员管理 → 员工详情」给家长发送签字链接的纯函数层：
 * 响应映射、请求体组装、发送弹层默认值与校验、澳洲手机号判断、悉尼时间格式化、错误码 → 文案键。
 * 不引用 apiClient / React Native，便于在 Node 下做契约测试。
 */

export type MinorStaffStage =
  | "not_sent"
  | "awaiting_guardian"
  | "link_expired"
  | "pending_review"
  | "returned"
  | "approved";

export type MinorStaffStateCode = "NSW" | "QLD" | "UNKNOWN";
export type MinorStaffFormType = "QLD_CE1" | "NSW_COMPANY_CONSENT" | "UNSUPPORTED_STATE";

export interface MinorStaffComplianceItem {
  userGUID: string;
  employeeName: string;
  username?: string;
  storeCode?: string;
  storeName?: string;
  stateCode: MinorStaffStateCode;
  formType: MinorStaffFormType;
  birthday?: string;
  age: number;
  complianceId?: number;
  version?: number;
  revision?: number;
  complianceStatus?: string;
  stage: MinorStaffStage;
  needsAction: boolean;
  canSend: boolean;
  canRevoke: boolean;
  guardianName?: string;
  guardianRelationship?: string;
  /** 未打码，只用于发送弹层预填；界面展示用打码字段。 */
  guardianEmail?: string;
  guardianPhone?: string;
  maskedGuardianEmail?: string;
  maskedGuardianPhone?: string;
  linkExpiresAtUtc?: string;
  lastSentAtUtc?: string;
  /** 最近一次发送：null = 没选这个渠道，true = 已送达，false = 发送失败。 */
  lastSentEmail: boolean | null;
  lastSentSms: boolean | null;
  guardianSignedAtUtc?: string;
  reviewComment?: string;
  returnFields: string[];
}

export interface MinorStaffComplianceDetail {
  /** 不是未成年 / 没有出生日期 / 没有主门店时为 null：整节不显示。 */
  item: MinorStaffComplianceItem | null;
  smsAvailable: boolean;
}

export type GuardianLinkExpiryDays = 1 | 3 | 7;
export const GUARDIAN_LINK_EXPIRY_OPTIONS: readonly GuardianLinkExpiryDays[] = [1, 3, 7];
export const DEFAULT_GUARDIAN_LINK_EXPIRY_DAYS: GuardianLinkExpiryDays = 3;

/** 关系选项：界面显示本地化文案，提交给后端的始终是英文值（家长看到的签署页全是英文）。 */
export const GUARDIAN_RELATIONSHIPS = ["Mother", "Father", "Legal guardian", "Other"] as const;
export type GuardianRelationshipOption = (typeof GUARDIAN_RELATIONSHIPS)[number];

export interface GuardianLinkForm {
  guardianName: string;
  guardianRelationship: string;
  guardianEmail: string;
  guardianPhone: string;
  sendEmail: boolean;
  sendSms: boolean;
  expiryDays: GuardianLinkExpiryDays;
}

export interface GuardianLinkSendPayload {
  guardianName: string;
  guardianRelationship: string | null;
  guardianEmail: string | null;
  guardianPhone: string | null;
  sendEmail: boolean;
  sendSms: boolean;
  expiryDays: GuardianLinkExpiryDays;
  expectedVersion: number | null;
  expectedRevision: number | null;
}

export interface GuardianLinkSendResult {
  item: MinorStaffComplianceItem;
  expiresAtUtc?: string;
  emailRequested: boolean;
  emailSent: boolean;
  emailError?: string;
  smsRequested: boolean;
  smsSent: boolean;
  smsError?: string;
  codeChannel: "sms" | "email";
  /** 只在有渠道失败时返回，供复制后手动转发。 */
  signingUrl?: string;
}

// ───────────────────────── 读取工具 ─────────────────────────

type RawRecord = Record<string, unknown>;

function record(value: unknown): RawRecord {
  return value && typeof value === "object" && !Array.isArray(value) ? (value as RawRecord) : {};
}

/** 兼容 camelCase 与 PascalCase（与 contract.ts 的写法一致）。 */
function pick(raw: RawRecord, key: string): unknown {
  if (key in raw) return raw[key];
  const pascal = key.charAt(0).toUpperCase() + key.slice(1);
  if (pascal in raw) return raw[pascal];
  // userGUID 这类全大写结尾的键再按忽略大小写兜底一次。
  const lower = key.toLowerCase();
  const match = Object.keys(raw).find((candidate) => candidate.toLowerCase() === lower);
  return match ? raw[match] : undefined;
}

function text(value: unknown): string | undefined {
  if (value == null) return undefined;
  const result = String(value).trim();
  return result ? result : undefined;
}

function int(value: unknown): number | undefined {
  if (value == null || value === "") return undefined;
  const parsed = Number(value);
  return Number.isFinite(parsed) ? Math.trunc(parsed) : undefined;
}

function bool(value: unknown): boolean {
  return value === true || value === "true" || value === 1;
}

function nullableBool(value: unknown): boolean | null {
  if (value === true || value === "true") return true;
  if (value === false || value === "false") return false;
  return null;
}

const STAGES: readonly MinorStaffStage[] = [
  "not_sent",
  "awaiting_guardian",
  "link_expired",
  "pending_review",
  "returned",
  "approved",
];

function normalizeStage(value: unknown): MinorStaffStage {
  const raw = text(value)?.toLowerCase();
  return STAGES.find((stage) => stage === raw) ?? "not_sent";
}

function normalizeState(value: unknown): MinorStaffStateCode {
  const raw = text(value)?.toUpperCase();
  return raw === "NSW" || raw === "QLD" ? raw : "UNKNOWN";
}

function normalizeFormType(value: unknown): MinorStaffFormType {
  const raw = text(value)?.toUpperCase();
  return raw === "QLD_CE1" || raw === "NSW_COMPANY_CONSENT" ? raw : "UNSUPPORTED_STATE";
}

// ───────────────────────── 响应映射 ─────────────────────────

export function mapStaffItem(value: unknown): MinorStaffComplianceItem {
  const raw = record(value);
  const returnFields = pick(raw, "returnFields");
  return {
    userGUID: text(pick(raw, "userGUID")) ?? "",
    employeeName: text(pick(raw, "employeeName")) ?? "",
    username: text(pick(raw, "username")),
    storeCode: text(pick(raw, "storeCode")),
    storeName: text(pick(raw, "storeName")),
    stateCode: normalizeState(pick(raw, "stateCode")),
    formType: normalizeFormType(pick(raw, "formType")),
    birthday: text(pick(raw, "birthday")),
    age: int(pick(raw, "age")) ?? 0,
    complianceId: int(pick(raw, "complianceId")),
    version: int(pick(raw, "version")),
    revision: int(pick(raw, "revision")),
    complianceStatus: text(pick(raw, "complianceStatus")),
    stage: normalizeStage(pick(raw, "stage")),
    needsAction: bool(pick(raw, "needsAction")),
    canSend: bool(pick(raw, "canSend")),
    canRevoke: bool(pick(raw, "canRevoke")),
    guardianName: text(pick(raw, "guardianName")),
    guardianRelationship: text(pick(raw, "guardianRelationship")),
    guardianEmail: text(pick(raw, "guardianEmail")),
    guardianPhone: text(pick(raw, "guardianPhone")),
    maskedGuardianEmail: text(pick(raw, "maskedGuardianEmail")),
    maskedGuardianPhone: text(pick(raw, "maskedGuardianPhone")),
    linkExpiresAtUtc: text(pick(raw, "linkExpiresAtUtc")),
    lastSentAtUtc: text(pick(raw, "lastSentAtUtc")),
    lastSentEmail: nullableBool(pick(raw, "lastSentEmail")),
    lastSentSms: nullableBool(pick(raw, "lastSentSms")),
    guardianSignedAtUtc: text(pick(raw, "guardianSignedAtUtc")),
    reviewComment: text(pick(raw, "reviewComment")),
    returnFields: Array.isArray(returnFields) ? returnFields.map((field) => String(field)) : [],
  };
}

export function mapStaffDetail(value: unknown): MinorStaffComplianceDetail {
  const raw = record(value);
  const item = pick(raw, "item");
  const mapped = item && typeof item === "object" ? mapStaffItem(item) : null;
  return {
    // 后端约定 item 为 null 表示「这一节不显示」；缺 userGUID 的异常数据同样按不显示处理。
    item: mapped && mapped.userGUID ? mapped : null,
    smsAvailable: bool(pick(raw, "smsAvailable")),
  };
}

export function mapSendResult(value: unknown): GuardianLinkSendResult {
  const raw = record(value);
  return {
    item: mapStaffItem(pick(raw, "item")),
    expiresAtUtc: text(pick(raw, "expiresAtUtc")),
    emailRequested: bool(pick(raw, "emailRequested")),
    emailSent: bool(pick(raw, "emailSent")),
    emailError: text(pick(raw, "emailError")),
    smsRequested: bool(pick(raw, "smsRequested")),
    smsSent: bool(pick(raw, "smsSent")),
    smsError: text(pick(raw, "smsError")),
    codeChannel: text(pick(raw, "codeChannel"))?.toLowerCase() === "sms" ? "sms" : "email",
    signingUrl: text(pick(raw, "signingUrl")),
  };
}

/** 有渠道失败（需要显示「复制链接」）。 */
export function hasDeliveryFailure(result: GuardianLinkSendResult) {
  return (result.emailRequested && !result.emailSent) || (result.smsRequested && !result.smsSent);
}

// ───────────────────────── 请求体 ─────────────────────────

/** 乐观锁：带上界面读到的版本与修改序号；没有记录时都为 null。 */
export function buildSendPayload(
  item: Pick<MinorStaffComplianceItem, "version" | "revision">,
  form: GuardianLinkForm,
): GuardianLinkSendPayload {
  const blankToNull = (value: string) => (value.trim() ? value.trim() : null);
  return {
    guardianName: form.guardianName.trim(),
    guardianRelationship: blankToNull(form.guardianRelationship),
    guardianEmail: blankToNull(form.guardianEmail),
    guardianPhone: blankToNull(form.guardianPhone),
    sendEmail: form.sendEmail,
    sendSms: form.sendSms,
    expiryDays: form.expiryDays,
    expectedVersion: item.version ?? null,
    expectedRevision: item.revision ?? null,
  };
}

export function buildRevokePayload(item: Pick<MinorStaffComplianceItem, "version" | "revision">) {
  return { expectedVersion: item.version ?? 0, expectedRevision: item.revision ?? 0 };
}

// ───────────────────────── 澳洲手机号（与后端 AustralianPhoneNumber.TryNormalize 同规则）─────────────────────────

/** 规范化为 +614xxxxxxxx；不是澳洲手机号（含座机）返回 null。 */
export function normalizeAustralianMobile(raw: string | null | undefined): string | null {
  const value = (raw ?? "").trim();
  if (!value) return null;
  if (/[^0-9 \-().+]/.test(value)) return null;
  if (value.slice(1).includes("+")) return null;
  const hasPlus = value.startsWith("+");
  const digits = value.replace(/\D/g, "");
  let national: string;
  if (hasPlus || digits.startsWith("0061")) {
    const withoutPrefix = digits.startsWith("0061") ? digits.slice(2) : digits;
    if (!withoutPrefix.startsWith("61")) return null;
    national = withoutPrefix.slice(2);
    if (national.startsWith("0")) national = national.slice(1);
  } else if (digits.startsWith("61") && digits.length === 11) {
    national = digits.slice(2);
  } else if (digits.startsWith("0")) {
    national = digits.slice(1);
  } else {
    return null;
  }
  return national.length === 9 && national.startsWith("4") ? `+61${national}` : null;
}

export function isAustralianMobile(raw: string | null | undefined) {
  return normalizeAustralianMobile(raw) !== null;
}

// ───────────────────────── 发送弹层：默认值、短信可用性、校验 ─────────────────────────

/** 短信开关为什么不可用：服务没开 / 号码不是澳洲手机；可用时为 null。空号码不算原因（电话必填另行提示）。 */
export type SmsBlockReason = "serviceOff" | "notMobile" | null;

export function resolveSmsBlockReason(smsAvailable: boolean, phone: string): SmsBlockReason {
  if (!smsAvailable) return "serviceOff";
  if (phone.trim() && !isAustralianMobile(phone)) return "notMobile";
  return null;
}

/**
 * 弹层默认值：用档案里已有的监护人资料预填；有邮箱默认开邮件；短信可用且是澳洲手机默认开短信。
 * 关系保留原值（不在选项里也原样保留，提交时原样回传）。
 */
export function resolveGuardianLinkDefaults(
  item: Pick<MinorStaffComplianceItem, "guardianName" | "guardianRelationship" | "guardianEmail" | "guardianPhone">,
  smsAvailable: boolean,
): GuardianLinkForm {
  const guardianEmail = item.guardianEmail ?? "";
  const guardianPhone = item.guardianPhone ?? "";
  return {
    guardianName: item.guardianName ?? "",
    guardianRelationship: item.guardianRelationship ?? "",
    guardianEmail,
    guardianPhone,
    sendEmail: Boolean(guardianEmail.trim()),
    sendSms: smsAvailable && isAustralianMobile(guardianPhone),
    expiryDays: DEFAULT_GUARDIAN_LINK_EXPIRY_DAYS,
  };
}

export type GuardianLinkFormError =
  | "nameRequired"
  | "phoneRequired"
  | "phoneInvalid"
  | "emailRequired"
  | "emailInvalid"
  | "channelRequired";

export interface GuardianLinkFormErrors {
  name?: GuardianLinkFormError;
  phone?: GuardianLinkFormError;
  email?: GuardianLinkFormError;
  channel?: GuardianLinkFormError;
}

const EMAIL_PATTERN = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;

/** 与后端同口径的前端校验：姓名、电话必填；至少开一种方式；开邮件时邮箱必填；填了邮箱必须有效。 */
export function validateGuardianLinkForm(form: GuardianLinkForm, smsAvailable: boolean): GuardianLinkFormErrors {
  const errors: GuardianLinkFormErrors = {};
  if (!form.guardianName.trim()) errors.name = "nameRequired";
  const phoneDigits = form.guardianPhone.replace(/\D/g, "");
  if (!form.guardianPhone.trim()) errors.phone = "phoneRequired";
  else if (phoneDigits.length < 8 || phoneDigits.length > 15) errors.phone = "phoneInvalid";
  const email = form.guardianEmail.trim();
  if (form.sendEmail && !email) errors.email = "emailRequired";
  else if (email && !EMAIL_PATTERN.test(email)) errors.email = "emailInvalid";
  const smsUsable = form.sendSms && resolveSmsBlockReason(smsAvailable, form.guardianPhone) === null;
  if (!form.sendEmail && !smsUsable) errors.channel = "channelRequired";
  return errors;
}

export function hasGuardianLinkFormErrors(errors: GuardianLinkFormErrors) {
  return Object.values(errors).some(Boolean);
}

// ───────────────────────── 展示规则 ─────────────────────────

export type MinorStageTone = "amber" | "blue" | "red" | "green";

/** 状态 Tag 颜色（与设计图一致）。 */
export const MINOR_STAGE_TONES: Record<MinorStaffStage, MinorStageTone> = {
  not_sent: "amber",
  link_expired: "amber",
  awaiting_guardian: "blue",
  pending_review: "amber",
  returned: "red",
  approved: "green",
};

/** 主按钮文案：已有链接在等签字或被退回时是「重新发送」，否则「发送家长签字链接」。 */
/** 发过链接的阶段（含已过期）按钮都叫「重新发送」，和下方提示用词一致。 */
export function isResendStage(stage: MinorStaffStage) {
  return stage === "awaiting_guardian" || stage === "returned" || stage === "link_expired";
}

/** 把关系英文值映射为选项键；不在选项里返回 null（界面原样显示）。 */
export function resolveRelationshipOption(value: string | undefined): GuardianRelationshipOption | null {
  const normalized = (value ?? "").trim().toLowerCase();
  return GUARDIAN_RELATIONSHIPS.find((option) => option.toLowerCase() === normalized) ?? null;
}

/** 后端 DateTime 可能不带时区后缀（SqlSugar 读出来是 Unspecified）：这些字段都是 UTC，缺后缀时按 UTC 解析。 */
export function parseUtc(value: string | undefined): Date | null {
  if (!value) return null;
  const normalized = /[zZ]|[+-]\d{2}:?\d{2}$/.test(value) ? value : `${value}Z`;
  const date = new Date(normalized);
  return Number.isNaN(date.getTime()) ? null : date;
}

/** 悉尼时间短格式：中文 MM-dd HH:mm，英文 dd/MM HH:mm。 */
export function formatSydneyShort(value: string | undefined, language: string): string | null {
  const date = parseUtc(value);
  if (!date) return null;
  try {
    const parts: Record<string, string> = {};
    const formatter = new Intl.DateTimeFormat("en-AU", {
      timeZone: "Australia/Sydney",
      month: "2-digit",
      day: "2-digit",
      hour: "2-digit",
      minute: "2-digit",
      hour12: false,
    });
    for (const part of formatter.formatToParts(date)) parts[part.type] = part.value;
    const hour = parts.hour === "24" ? "00" : parts.hour;
    return language.toLowerCase().startsWith("zh")
      ? `${parts.month}-${parts.day} ${hour}:${parts.minute}`
      : `${parts.day}/${parts.month} ${hour}:${parts.minute}`;
  } catch {
    return date.toISOString().slice(5, 16).replace("T", " ");
  }
}

// ───────────────────────── 错误码 → 文案键 ─────────────────────────

/** 后端业务错误码 → peopleDetail:minor.errors.* 的键（英文界面用；中文界面直接显示后端中文原文）。 */
export const GUARDIAN_LINK_ERROR_KEYS: Record<string, string> = {
  MINOR_GUARDIAN_CHANNEL_REQUIRED: "channelRequired",
  MINOR_COMPLIANCE_GUARDIAN_CONTACT_REQUIRED: "contactRequired",
  GUARDIAN_SMS_NOT_CONFIGURED: "smsNotConfigured",
  GUARDIAN_SMS_PHONE_INVALID: "smsPhoneInvalid",
  MINOR_COMPLIANCE_VERSION_CONFLICT: "versionConflict",
  INVALID_STATUS: "invalidStatus",
  DOB_REQUIRED: "dobRequired",
  NOT_MINOR: "notMinor",
  FORBIDDEN: "forbidden",
  MINOR_COMPLIANCE_STATE_UNCONFIRMED: "stateUnconfirmed",
  EMPLOYEE_STORE_REQUIRED: "storeRequired",
};

export const MINOR_VERSION_CONFLICT_CODE = "MINOR_COMPLIANCE_VERSION_CONFLICT";

export function readErrorCode(error: unknown): string | undefined {
  const raw = record(error);
  return typeof raw.code === "string" && raw.code.trim() ? raw.code.trim() : undefined;
}

/** 403 或业务错误码 FORBIDDEN：这一节静默不显示。 */
export function isForbiddenError(error: unknown): boolean {
  if (readErrorCode(error) === "FORBIDDEN") return true;
  const response = record(record(error).response);
  return response.status === 403;
}
