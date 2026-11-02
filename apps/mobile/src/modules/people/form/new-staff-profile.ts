/**
 * 新建员工时的「电话 / 生日 / 监护人」纯规则：店长「新增员工」与管理员「新增账号」两个整页共用。
 *
 * 与后端 NewStaffProfileRules.cs 保持一致：
 * - 电话、生日必填（邮箱由各表单原有规则校验，原来就必填）；
 * - 生日不能晚于今天、不能早于 1940-01-01；年龄按悉尼当天算；
 * - 未满 18 岁时监护人姓名、关系、手机、邮箱四项必填；监护人手机必须是澳洲手机号（04 开头），
 *   邮箱格式正确，且手机 / 邮箱都不能和员工本人相同。
 * 前端电话沿用现有的澳洲手机号校验（比后端「8–15 位数字」更严），提交前就能拦住。
 * 本文件不引用 React Native，可在 Node 下直接测试。
 */
import { calculateAge, normalizeBirthday, validateBirthday } from "@/modules/employee-profile/birthday";
import { todayInSydney } from "@/modules/daily-closes/logic";
import { normalizePhoneInput, validateNewStaffEmail, validateNewStaffPhone } from "@/modules/users/validation";

/** 最早允许的生日（与后端 EarliestBirthday 一致）；日期选择器的年份下限也取它。 */
export const NEW_STAFF_MIN_BIRTHDAY = "1940-01-01";
export const NEW_STAFF_MIN_BIRTH_YEAR = 1940;
/** 未满这个年龄算未成年，需要填写监护人。 */
export const NEW_STAFF_ADULT_AGE = 18;

/** 关系选项：界面显示本地化文案，提交给后端的始终是英文值（会印在英文同意书上）。 */
export const NEW_STAFF_GUARDIAN_RELATIONSHIPS = ["Mother", "Father", "Legal guardian", "Other"] as const;
export type NewStaffGuardianRelationship = (typeof NEW_STAFF_GUARDIAN_RELATIONSHIPS)[number];

export interface NewStaffGuardianInput {
  name: string;
  /** 英文值（见 NEW_STAFF_GUARDIAN_RELATIONSHIPS）；未选为空串。 */
  relationship: string;
  phone: string;
  email: string;
}

/** 两个新建表单里与本规则相关的字段（表单值对象直接满足这个结构）。 */
export interface NewStaffProfileInput {
  phone: string;
  /** YYYY-MM-DD；未选为空串。 */
  birthday: string;
  guardian: NewStaffGuardianInput;
}

export const EMPTY_NEW_STAFF_GUARDIAN: NewStaffGuardianInput = { name: "", relationship: "", phone: "", email: "" };

export type NewStaffPhoneError = "required" | "invalid";
export type NewStaffBirthdayError = "required" | "invalid";

export interface NewStaffGuardianErrors {
  name?: "required";
  relationship?: "required";
  phone?: "required" | "invalid" | "sameAsStaff";
  email?: "required" | "invalid" | "sameAsStaff";
}

export interface NewStaffProfileErrors {
  phone?: NewStaffPhoneError;
  birthday?: NewStaffBirthdayError;
  guardian: NewStaffGuardianErrors;
}

/** 提交给后端的字段：phone / birthday 始终带；guardian 只在未成年时带。 */
export interface NewStaffProfileBody {
  phone: string;
  birthday: string;
  guardian?: { name: string; relationship: string; phone: string; email: string };
}

// ───────────────────────── 日期与年龄 ─────────────────────────

/**
 * 悉尼「今天」，转成本地时区的同一个年月日（只用年月日部分）。
 * calculateAge / validateBirthday 读的是 Date 的本地年月日，这样无论设备在哪个时区，都按悉尼当天算。
 */
export function sydneyToday(now: Date = new Date()): Date {
  const [year, month, day] = todayInSydney(now).split("-").map(Number);
  return new Date(year, month - 1, day);
}

/** 生日是否合法：格式正确、不晚于今天、不早于 1940-01-01。空值不算合法。 */
function isValidNewStaffBirthday(value: string, today: Date) {
  const normalized = normalizeBirthday(value);
  if (!normalized || normalized !== value.trim()) return false;
  if (validateBirthday(normalized, today)) return false;
  return normalized >= NEW_STAFF_MIN_BIRTHDAY;
}

export function validateNewStaffBirthday(value: string, today: Date): NewStaffBirthdayError | undefined {
  if (!value.trim()) return "required";
  return isValidNewStaffBirthday(value, today) ? undefined : "invalid";
}

/** 生日旁边的年龄标签：生日无效时为 null。 */
export function resolveNewStaffAge(birthday: string, today: Date): { age: number; minor: boolean } | null {
  if (!isValidNewStaffBirthday(birthday, today)) return null;
  const age = calculateAge(birthday, today);
  return age === null ? null : { age, minor: age < NEW_STAFF_ADULT_AGE };
}

/** 是否未成年：只有生日有效且未满 18 岁才算（生日没填 / 无效时不出现监护人一节）。 */
export function isNewStaffMinor(birthday: string, today: Date) {
  return resolveNewStaffAge(birthday, today)?.minor ?? false;
}

// ───────────────────────── 电话 ─────────────────────────

/** 澳洲手机号统一成 +614xxxxxxxx，用于判断「监护人手机是否和员工本人相同」；不是澳洲手机号返回 null。 */
export function normalizeAustralianMobile(value: string) {
  const compact = normalizePhoneInput(value.trim());
  if (/^04\d{8}$/.test(compact)) return `+61${compact.slice(1)}`;
  if (/^\+614\d{8}$/.test(compact)) return compact;
  return null;
}

const digitsOf = (value: string) => value.replace(/\D/g, "");

/** 电话必填，且沿用现有的澳洲手机号校验（validateNewStaffPhone）。 */
export function validateRequiredStaffPhone(phone: string): NewStaffPhoneError | undefined {
  if (!phone.trim()) return "required";
  // validateNewStaffPhone 返回翻译后的文案；这里只关心「是否有错」，翻译交给界面按错误码处理。
  return validateNewStaffPhone(phone, (key) => key) ? "invalid" : undefined;
}

// ───────────────────────── 整体校验 ─────────────────────────

function validateGuardian(guardian: NewStaffGuardianInput, staffPhone: string, staffEmail: string): NewStaffGuardianErrors {
  const errors: NewStaffGuardianErrors = {};
  if (!guardian.name.trim()) errors.name = "required";
  if (!guardian.relationship.trim()) errors.relationship = "required";

  const guardianMobile = normalizeAustralianMobile(guardian.phone);
  if (!guardian.phone.trim()) errors.phone = "required";
  else if (!guardianMobile) errors.phone = "invalid";
  else {
    // 与后端一致：员工电话能规范化就比较规范化后的号码，否则比较纯数字。
    const staffMobile = normalizeAustralianMobile(staffPhone);
    const same = staffMobile ? staffMobile === guardianMobile : digitsOf(staffPhone) === digitsOf(guardian.phone);
    if (same) errors.phone = "sameAsStaff";
  }

  const emailError = validateNewStaffEmail(guardian.email, (key) => key);
  if (emailError === "messages.emailRequired") errors.email = "required";
  else if (emailError) errors.email = "invalid";
  else if (guardian.email.trim().toLowerCase() === staffEmail.trim().toLowerCase()) errors.email = "sameAsStaff";
  return errors;
}

/**
 * 校验电话、生日与（未成年时的）监护人。`staffEmail` 用于判断监护人邮箱是否和员工本人相同。
 * 成年或生日未填 / 无效时，监护人一节不显示，也不校验。
 */
export function validateNewStaffProfile(values: NewStaffProfileInput, staffEmail: string, today: Date): NewStaffProfileErrors {
  return {
    phone: validateRequiredStaffPhone(values.phone),
    birthday: validateNewStaffBirthday(values.birthday, today),
    guardian: isNewStaffMinor(values.birthday, today) ? validateGuardian(values.guardian, values.phone, staffEmail) : {},
  };
}

export function hasNewStaffGuardianErrors(errors: NewStaffGuardianErrors) {
  return Boolean(errors.name || errors.relationship || errors.phone || errors.email);
}

export function hasNewStaffProfileErrors(errors: NewStaffProfileErrors) {
  return Boolean(errors.phone || errors.birthday) || hasNewStaffGuardianErrors(errors.guardian);
}

/** 电话、生日、监护人任一项填过内容就算「已填写」（返回时要弹「放弃修改？」）。 */
export function isNewStaffProfileDirty(values: NewStaffProfileInput) {
  return values.phone.trim() !== ""
    || values.birthday.trim() !== ""
    || Object.values(values.guardian).some((value) => value.trim() !== "");
}

/**
 * 请求体里的电话 / 生日 / 监护人。关键逻辑：成年时即使监护人一节里填过内容也不带 guardian，
 * 改回成年生日后已填的监护人信息只是收起，不会提交。
 */
export function buildNewStaffProfileBody(values: NewStaffProfileInput, today: Date): NewStaffProfileBody {
  const body: NewStaffProfileBody = { phone: values.phone.trim(), birthday: values.birthday.trim() };
  if (isNewStaffMinor(values.birthday, today)) {
    body.guardian = {
      name: values.guardian.name.trim(),
      relationship: values.guardian.relationship.trim(),
      phone: values.guardian.phone.trim(),
      email: values.guardian.email.trim(),
    };
  }
  return body;
}

// ───────────────────────── 服务端错误码 ─────────────────────────

export type NewStaffServerErrorKey =
  | "birthdayRequired"
  | "birthdayInvalid"
  | "phoneRequired"
  | "emailRequired"
  | "guardianRequired"
  | "guardianPhoneInvalid"
  | "guardianEmailInvalid"
  | "guardianDuplicate";

/** 后端 NewStaffProfileRules 的错误码 → peopleForm `newStaff.serverErrors.*` 文案键。 */
const SERVER_ERROR_KEY_BY_CODE: Record<string, NewStaffServerErrorKey> = {
  NEW_STAFF_BIRTHDAY_REQUIRED: "birthdayRequired",
  NEW_STAFF_BIRTHDAY_INVALID: "birthdayInvalid",
  NEW_STAFF_PHONE_REQUIRED: "phoneRequired",
  NEW_STAFF_EMAIL_REQUIRED: "emailRequired",
  NEW_STAFF_GUARDIAN_REQUIRED: "guardianRequired",
  NEW_STAFF_GUARDIAN_PHONE_INVALID: "guardianPhoneInvalid",
  NEW_STAFF_GUARDIAN_EMAIL_INVALID: "guardianEmailInvalid",
  NEW_STAFF_GUARDIAN_DUPLICATE: "guardianDuplicate",
};

/** 识别「新建员工必填规则」的服务端错误码（错误码由 identity-conflict 的 readApiErrorCode 读取）。 */
export function resolveNewStaffServerErrorKey(code: string | null): NewStaffServerErrorKey | null {
  return code ? SERVER_ERROR_KEY_BY_CODE[code] ?? null : null;
}

/**
 * 按服务端错误码把对应字段标红（邮箱是各表单自己的字符串错误，由页面处理）。
 * 监护人整节缺项 / 与本人重复时只给顶部提示：本地校验与后端同规则，正常不会走到这里。
 */
export function markNewStaffServerError<T extends NewStaffProfileErrors>(errors: T, key: NewStaffServerErrorKey): T {
  switch (key) {
    case "birthdayRequired":
      return { ...errors, birthday: "required" };
    case "birthdayInvalid":
      return { ...errors, birthday: "invalid" };
    case "phoneRequired":
      return { ...errors, phone: "invalid" };
    case "guardianPhoneInvalid":
      return { ...errors, guardian: { ...errors.guardian, phone: "invalid" } };
    case "guardianEmailInvalid":
      return { ...errors, guardian: { ...errors.guardian, email: "invalid" } };
    default:
      return errors;
  }
}
