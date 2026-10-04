// 首次登录 / 重置后的强制改密，以及员工自助改密的共用规则。

import type { CurrentUser } from "./types";

/** 与后端 ChangePasswordDto 的 6–100 位限制一致。 */
export const PASSWORD_MIN_LENGTH = 6;
export const PASSWORD_MAX_LENGTH = 100;

export type PasswordChangeMode = "forced" | "voluntary";

export interface PasswordChangeForm {
  currentPassword: string;
  newPassword: string;
  confirmPassword: string;
}

export interface PasswordRuleState {
  minLength: boolean;
  differentFromCurrent: boolean;
  confirmed: boolean;
}

/**
 * 只有个人账号会话才拦截：设备绑定账号是门店共用设备身份，iOS 审核会话完全离线，都不走改密流程。
 */
export function shouldForcePasswordChange(input: {
  user: Pick<CurrentUser, "mustChangePassword"> | null | undefined;
  sessionKind: string;
  isAuthenticated: boolean;
}) {
  return input.isAuthenticated
    && input.sessionKind === "account"
    && input.user?.mustChangePassword === true;
}

/** 登录时会去掉首尾空格，改密也按同一口径归一化，避免改完后用同样的输入登不上。 */
export function normalizePasswordChangeForm(form: PasswordChangeForm): PasswordChangeForm {
  return {
    currentPassword: form.currentPassword.trim(),
    newPassword: form.newPassword.trim(),
    confirmPassword: form.confirmPassword.trim(),
  };
}

export function getPasswordRuleState(form: PasswordChangeForm): PasswordRuleState {
  const normalized = normalizePasswordChangeForm(form);
  return {
    minLength: normalized.newPassword.length >= PASSWORD_MIN_LENGTH
      && normalized.newPassword.length <= PASSWORD_MAX_LENGTH,
    differentFromCurrent: normalized.newPassword.length > 0
      && normalized.newPassword !== normalized.currentPassword,
    confirmed: normalized.confirmPassword.length > 0
      && normalized.confirmPassword === normalized.newPassword,
  };
}

/** 后端以中文文案返回业务错误；映射成翻译键，英文界面也能看到准确原因。 */
export function resolvePasswordChangeErrorKey(error: unknown): string | null {
  const message = error instanceof Error ? error.message : String(error ?? "");
  if (message.includes("当前密码错误")) return "changePassword.errors.wrongCurrent";
  if (message.includes("新密码不能与当前密码相同")) return "changePassword.errors.sameAsCurrent";
  return null;
}

export function canSubmitPasswordChange(form: PasswordChangeForm) {
  const rules = getPasswordRuleState(form);
  return normalizePasswordChangeForm(form).currentPassword.length > 0
    && rules.minLength
    && rules.differentFromCurrent
    && rules.confirmed;
}
