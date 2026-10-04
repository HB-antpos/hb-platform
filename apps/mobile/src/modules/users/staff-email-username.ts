// 新建店员默认用邮箱作为用户名：员工用同一个邮箱登录、收验证码、找回密码，不用另记账号。

/** 后端用户名上限 50 个字符；超长邮箱不自动带入，交给店长手动填写。 */
export const MAX_USERNAME_LENGTH = 50;

export function resolveUsernameFromEmail(email: string) {
  const normalized = email.trim().toLowerCase();
  return normalized.length <= MAX_USERNAME_LENGTH ? normalized : "";
}

/** 未填邮箱时后端会生成 xxx@<分店>.store.local 的内部占位邮箱，收不到信。 */
export function hasDeliverableEmail(email: string | null | undefined) {
  const normalized = email?.trim().toLowerCase() ?? "";
  return normalized.includes("@") && !normalized.endsWith(".store.local");
}

/** 界面展示用：占位邮箱一律当作「未设置」，不把系统生成的地址展示给店长和员工。 */
export function getDisplayableEmail(email: string | null | undefined) {
  return hasDeliverableEmail(email) ? (email ?? "").trim() : "";
}
