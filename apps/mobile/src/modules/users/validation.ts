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

export function validateStoreUserForm(
  values: StoreUserFormValues,
  t: Translate
) {
  const username = values.username.trim();
  if (!username) {
    return t("messages.usernameRequired");
  }

  if (username.length < MIN_USERNAME_LENGTH || username.length > MAX_USERNAME_LENGTH) {
    return t("messages.usernameLength");
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
