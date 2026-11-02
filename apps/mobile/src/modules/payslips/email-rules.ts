// 发送工资单邮件的前端规则：只允许一个收件人；与员工档案邮箱不同时必须勾选「我已核对收件人」（后端同样强制）。
import { toDateOnly } from "./format";
import type { PayslipEmailRequest, PayslipEmailTarget, PayslipKey } from "./types";

/** 附言最长字数（后端也会再限制）。 */
export const PAYSLIP_EMAIL_NOTE_MAX = 300;

/** 一个邮箱地址：本地部分 + @ + 带点的域名，不含空白、逗号、分号。 */
const EMAIL_PATTERN = /^[^\s@,;<>()[\]\\"]+@[^\s@,;<>()[\]\\"]+\.[^\s@,;<>()[\]\\"]{2,}$/;

export type RecipientIssue = "empty" | "multiple" | "invalid";

export interface RecipientEvaluation {
  /** 去掉首尾空白后的地址。 */
  address: string;
  issue: RecipientIssue | null;
  /** 档案里有邮箱。 */
  hasProfileEmail: boolean;
  /** 与档案邮箱一致（不区分大小写）；档案邮箱不可见时恒为 false。 */
  matchesProfile: boolean;
  /** 需要勾选「我已核对收件人」：档案有邮箱，且收件人不是档案邮箱。 */
  requiresConfirm: boolean;
}

/** 校验并分类收件人输入。 */
export function evaluateRecipient(input: string, target: Pick<PayslipEmailTarget, "email" | "source"> | null): RecipientEvaluation {
  const address = input.trim();
  const hasProfileEmail = target?.source === "profile";
  const profileEmail = target?.email?.trim().toLowerCase() ?? "";
  const matchesProfile = Boolean(profileEmail) && address.toLowerCase() === profileEmail;

  let issue: RecipientIssue | null = null;
  if (!address) issue = "empty";
  else if (/[,;\s]/.test(address)) issue = "multiple";
  else if (!EMAIL_PATTERN.test(address)) issue = "invalid";

  return {
    address,
    issue,
    hasProfileEmail,
    matchesProfile,
    requiresConfirm: hasProfileEmail && !matchesProfile && issue === null,
  };
}

/** 能否发送：收件人合法，且需要核对时已勾选「我已核对收件人」；发送中不可重复提交。 */
export function canSubmitEmail(evaluation: RecipientEvaluation, confirmed: boolean, sending: boolean): boolean {
  if (sending || evaluation.issue !== null) return false;
  return !evaluation.requiresConfirm || confirmed;
}

/** 组装 POST email 的请求体：附言去空白，空则不带；确认标记只在确实需要核对时才为 true。 */
export function buildEmailRequest(
  key: PayslipKey,
  input: { to: string; copyToMe: boolean; note: string; confirmed: boolean },
  evaluation: RecipientEvaluation,
): PayslipEmailRequest {
  const note = input.note.trim().slice(0, PAYSLIP_EMAIL_NOTE_MAX);
  return {
    connectionGuid: key.connectionGuid,
    employeeUid: key.employeeUid,
    paymentDate: toDateOnly(key.paymentDate) || key.paymentDate,
    ...(key.periodStart ? { periodStart: toDateOnly(key.periodStart) || key.periodStart } : {}),
    to: evaluation.address,
    copyToMe: input.copyToMe,
    ...(note ? { note } : {}),
    recipientChangeConfirmed: evaluation.requiresConfirm && input.confirmed,
  };
}
