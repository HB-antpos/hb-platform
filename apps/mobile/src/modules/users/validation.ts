import { cleanNamePart, composeFullName } from "@/modules/people/person-name";
import type { StoreUserFormValues } from "@/modules/users/types";

type Translate = (key: string) => string;

const EMAIL_PATTERN = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;
const MIN_USERNAME_LENGTH = 3;
const MAX_USERNAME_LENGTH = 50;
const MIN_PASSWORD_LENGTH = 6;
const MAX_PASSWORD_LENGTH = 100;

export function validatePasswordValue(password: string, t: Translate) {
  const trimmed = password.trim();
  if (!trimmed) {
    return t("messages.passwordRequired");
  }

  if (trimmed.length < MIN_PASSWORD_LENGTH) {
    return t("messages.passwordTooShort");
  }

  if (trimmed.length > MAX_PASSWORD_LENGTH) {
    return t("messages.passwordTooLong");
  }

  return null;
}

/** 账号（用户名）：必填、3–50 个字符。新增整页表单把它作为单字段校验。 */
export function validateUsername(rawUsername: string, t: Translate) {
  const username = rawUsername.trim();
  if (!username) {
    return t("messages.usernameRequired");
  }

  if (username.length < MIN_USERNAME_LENGTH || username.length > MAX_USERNAME_LENGTH) {
    return t("messages.usernameLength");
  }

  return null;
}

export function validateStoreUserForm(
  values: StoreUserFormValues,
  t: Translate
) {
  const usernameMessage = validateUsername(values.username, t);
  if (usernameMessage) {
    return usernameMessage;
  }

  const email = values.email.trim();
  if (email && !EMAIL_PATTERN.test(email)) {
    return t("messages.emailInvalid");
  }

  return null;
}

/** 澳洲手机号：04 开头共 10 位，或 +61 4 开头；允许中间空格和连字符。 */
const AU_MOBILE_PATTERN = /^(?:04\d{8}|\+614\d{8})$/;

export function normalizePhoneInput(value: string) {
  return value.replace(/[\s-]/g, "");
}

/**
 * 只在新建店员时校验：编辑时历史号码可能不是这个格式，不能因此挡住改姓名等其他保存。
 * 空值允许（手机号选填）。
 */
export function validateNewStaffPhone(phone: string, t: Translate) {
  const normalized = normalizePhoneInput(phone.trim());
  if (!normalized) {
    return null;
  }
  return AU_MOBILE_PATTERN.test(normalized) ? null : t("messages.phoneInvalid");
}

/** 新建店员时邮箱必填：它是默认用户名，也是收设置 / 找回密码验证码的唯一渠道。 */
export function validateNewStaffEmail(email: string, t: Translate) {
  const trimmed = email.trim();
  if (!trimmed) {
    return t("messages.emailRequired");
  }
  return EMAIL_PATTERN.test(trimmed) ? null : t("messages.emailInvalid");
}

/** 店长给老账号补的找回密码邮箱：必填、格式正确，且不能是分店占位邮箱（收不到信）。 */
export function validateStaffRecoveryEmail(email: string, t: Translate) {
  const formatError = validateNewStaffEmail(email, t);
  if (formatError) return formatError;
  return email.trim().toLowerCase().endsWith(".store.local") ? t("messages.emailNotDeliverable") : null;
}

// ───────────────────────── 姓名四段（姓 / 名 / 中间名 / 昵称）─────────────────────────

/** 姓、名、中间名、昵称各自的长度上限；合成后的全名另有上限（与后端 FullName 100 字符一致）。 */
export const PERSON_NAME_PART_MAX_LENGTH = 50;
export const PERSON_FULL_NAME_MAX_LENGTH = 100;

export type PersonNameField = "familyName" | "givenName" | "middleName" | "nickname";
/** required：必填段为空；tooLong：单段超过 50；fullNameTooLong：合成后的全名超过 100。 */
export type PersonNameErrorCode = "required" | "tooLong" | "fullNameTooLong";
/** 按字段给出错误码；`fullName` 键表示合成全名超长（显示在姓名区下方）。 */
export type PersonNameErrors = Partial<Record<PersonNameField | "fullName", PersonNameErrorCode>>;

export interface PersonNameInput {
  familyName: string;
  givenName: string;
  middleName: string;
  nickname: string;
}

/**
 * 校验姓名四段。
 * - requireNames=true 时姓、名必填（新增；编辑时只在用户动过姓名区后才要求，避免存量只有一个词的全名挡住改电话等保存）。
 * - 长度按去首尾空白、折叠内部空白后的值计算，与实际提交的内容一致。
 * - 全名超长只在各段都合格时才报，避免同一处出现两条错误。
 */
export function validatePersonNameFields(
  values: PersonNameInput,
  options: { requireNames: boolean }
): PersonNameErrors {
  const errors: PersonNameErrors = {};
  const fields: PersonNameField[] = ["familyName", "givenName", "middleName", "nickname"];
  for (const field of fields) {
    const cleaned = cleanNamePart(values[field]) ?? "";
    if (cleaned.length > PERSON_NAME_PART_MAX_LENGTH) {
      errors[field] = "tooLong";
    } else if (options.requireNames && !cleaned && (field === "familyName" || field === "givenName")) {
      errors[field] = "required";
    }
  }

  if (!errors.familyName && !errors.givenName && !errors.middleName) {
    const fullName = composeFullName(values) ?? "";
    if (fullName.length > PERSON_FULL_NAME_MAX_LENGTH) {
      errors.fullName = "fullNameTooLong";
    }
  }
  return errors;
}

export function hasPersonNameErrors(errors: PersonNameErrors) {
  return Object.keys(errors).length > 0;
}
