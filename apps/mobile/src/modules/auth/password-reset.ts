// 邮箱验证码设置 / 找回密码的纯规则：与后端 6 位验证码、6–100 位密码保持一致。

import { PASSWORD_MAX_LENGTH, PASSWORD_MIN_LENGTH } from "./password-change";

export { PASSWORD_MAX_LENGTH, PASSWORD_MIN_LENGTH };

const EMAIL_PATTERN = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;

export function isValidResetEmail(value: string) {
  const trimmed = value.trim();
  return trimmed.length <= 254 && EMAIL_PATTERN.test(trimmed);
}

/** 只保留数字并截到 6 位，兼容从邮件里连空格一起复制的验证码。 */
export function normalizeResetCode(value: string) {
  return value.replace(/\D/g, "").slice(0, 6);
}

export function isResetCodeFormatValid(value: string) {
  return /^\d{6}$/.test(normalizeResetCode(value));
}

/** 审核守卫的三个动作：页面传 auth-store 的真实实现，单测传假实现。 */
export interface PreAuthGate {
  isClosed: () => boolean;
  open: () => void;
  close: () => void;
}

/**
 * iOS 正式包启动后，所有请求默认走审核模式的本地通道，用户明确提交后才放行真实网络。
 * 设置 / 找回密码发生在登录之前，不放行就到不了服务器，所以这里只在请求期间打开，结束后恢复。
 * 守卫本来就开着时（安卓、开发包、已存登录状态）什么都不动，避免把真实会话关回本地通道。
 */
export async function runWithPreAuthGateOpen<T>(gate: PreAuthGate, request: () => Promise<T>): Promise<T> {
  if (!gate.isClosed()) return request();
  gate.open();
  try {
    return await request();
  } finally {
    gate.close();
  }
}

/** 登录时会去掉首尾空格，这里按同一口径判断。 */
export function getPasswordResetRuleState(newPassword: string, confirmPassword: string) {
  const next = newPassword.trim();
  const confirm = confirmPassword.trim();
  return {
    minLength: next.length >= PASSWORD_MIN_LENGTH && next.length <= PASSWORD_MAX_LENGTH,
    confirmed: confirm.length > 0 && confirm === next,
  };
}
