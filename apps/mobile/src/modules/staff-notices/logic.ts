// 「员工通知」的纯逻辑：不依赖 React Native，可在 Node 下测试；所有文案由调用方传入。
import type {
  StaffNoticeBatch,
  StaffNoticeBatchDetail,
  StaffNoticeChannelStatus,
  StaffNoticePreflight,
  StaffNoticePreflightRecipient,
  StaffNoticeRecipientReason,
  StaffNoticeRecipientResult,
  StaffNoticeSmsSender,
  StaffNoticeTemplate,
} from "./types";

/** 与后端 StaffNotificationRules.MaxRecipientsPerBatch 一致，一批最多这么多人。 */
export const STAFF_NOTICE_MAX_RECIPIENTS = 100;
/** 发送进度轮询间隔。 */
export const STAFF_NOTICE_POLL_MS = 1500;
/** 系统模板「账号已开通」的 key，默认选它。 */
export const ACCOUNT_ACTIVATED_TEMPLATE_KEY = "account-activated";

export function resolveDefaultTemplate(templates: readonly StaffNoticeTemplate[]): StaffNoticeTemplate | null {
  return templates.find((template) => template.key === ACCOUNT_ACTIVATED_TEMPLATE_KEY) ?? templates[0] ?? null;
}

/**
 * 本次短信实际使用的发件名（与 Web 同一规则）：
 * 用户选的仍在预检返回的名单里就用它；已不在（重新预检后白名单变了）回落默认项，没标默认项取第一项；
 * 名单为空（短信不可用 / 旧后端）返回 undefined，发送时就不传，由服务端用默认发件名。
 */
export function resolveSmsSenderId(
  senders: readonly StaffNoticeSmsSender[] | undefined,
  chosenId: string | undefined,
): string | undefined {
  if (!senders || senders.length === 0) return undefined;
  if (chosenId !== undefined && senders.some((sender) => sender.id === chosenId)) return chosenId;
  return (senders.find((sender) => sender.isDefault) ?? senders[0]).id;
}

export function templateSupportsSms(template: StaffNoticeTemplate | null | undefined): boolean {
  return Boolean(template?.smsBody?.trim());
}

// ───────────────────────── 收件人与勾选 ─────────────────────────

export function isRecipientSelectable(recipient: StaffNoticePreflightRecipient): boolean {
  return recipient.status !== "blocked";
}

/** 可发送的默认勾选；已登录过、邮箱存疑的默认不勾（用户手动勾上才发）；不可发送的永远不勾。 */
export function resolveDefaultSelection(recipients: readonly StaffNoticePreflightRecipient[]): Set<string> {
  return new Set(recipients.filter((item) => item.status === "ready").map((item) => item.userGuid));
}

/** 切换短信开关或重新预检后，保留用户已有的勾选，只剔除现在不能发的人。 */
export function reconcileSelection(
  current: ReadonlySet<string>,
  recipients: readonly StaffNoticePreflightRecipient[],
): Set<string> {
  const selectable = new Set(recipients.filter(isRecipientSelectable).map((item) => item.userGuid));
  return new Set([...current].filter((guid) => selectable.has(guid)));
}

export type StaffNoticeRecipientFilter = "all" | "ready" | "confirm" | "blocked";

export function countRecipients(recipients: readonly StaffNoticePreflightRecipient[]): Record<StaffNoticeRecipientFilter, number> {
  const counts = { all: recipients.length, ready: 0, confirm: 0, blocked: 0 };
  for (const recipient of recipients) counts[recipient.status] += 1;
  return counts;
}

export function filterRecipients(
  recipients: readonly StaffNoticePreflightRecipient[],
  filter: StaffNoticeRecipientFilter,
): StaffNoticePreflightRecipient[] {
  return filter === "all" ? [...recipients] : recipients.filter((item) => item.status === filter);
}

/** 这一行最该让人看到的原因：不可发送的优先于需确认的（后端已按这个顺序排）。 */
export function primaryReason(recipient: StaffNoticePreflightRecipient): StaffNoticeRecipientReason | null {
  return recipient.reasons[0] ?? null;
}

export interface StaffNoticeSendSummary {
  people: number;
  emails: number;
  smsMessages: number;
  costAud: number;
  /** 被勾选的人里登录过的：发放密码会让旧密码失效、全部设备下线，要单独点名并确认。 */
  reissued: StaffNoticePreflightRecipient[];
}

export function summarizeSelection(
  preflight: Pick<StaffNoticePreflight, "recipients" | "smsUnitCostAud">,
  selected: ReadonlySet<string>,
  issuesPassword: boolean,
): StaffNoticeSendSummary {
  const chosen = preflight.recipients.filter((item) => selected.has(item.userGuid) && isRecipientSelectable(item));
  const smsMessages = chosen.reduce((sum, item) => sum + (item.willSendSms ? item.smsSegments : 0), 0);
  return {
    people: chosen.length,
    emails: chosen.filter((item) => item.willSendEmail).length,
    smsMessages,
    costAud: smsMessages * preflight.smsUnitCostAud,
    reissued: issuesPassword ? chosen.filter((item) => item.reasons.includes("already_logged_in")) : [],
  };
}

export function formatAud(value: number): string {
  return `A$${value.toFixed(2)}`;
}

// ───────────────────────── 入口 ─────────────────────────

/**
 * 详情页「还没登录过这个账号」情境卡片：只认「从未登录」。
 * 不用「待首次登录」标签（mustChangePassword）判断：登录过的员工被重置密码后也有这个标签，
 * 给他发通知要二次确认，不该用一键入口引导。口径与后端预检的 already_logged_in 相同。
 */
export function shouldOfferActivationCard(input: {
  canSendNotice: boolean;
  active: boolean;
  loginEnabled?: boolean;
  lastLoginTime?: string | null;
}): boolean {
  return input.canSendNotice && input.active && input.loginEnabled !== false && !input.lastLoginTime;
}

/** 批量核对页的路由参数：GUID 用逗号拼接（最多 100 个，放得下）。 */
export function encodeUserGuidsParam(userGuids: readonly string[]): string {
  return userGuids.join(",");
}

export function decodeUserGuidsParam(value: string | string[] | undefined): string[] {
  const raw = Array.isArray(value) ? value.join(",") : value ?? "";
  const guids = raw.split(",").map((item) => item.trim()).filter(Boolean);
  return [...new Set(guids)].slice(0, STAFF_NOTICE_MAX_RECIPIENTS);
}

// ───────────────────────── 批次与结果 ─────────────────────────

export function isBatchFinished(batch: Pick<StaffNoticeBatch, "status"> | null | undefined): boolean {
  return batch?.status === "completed" || batch?.status === "interrupted";
}

export function isRecipientProcessed(row: Pick<StaffNoticeRecipientResult, "emailStatus" | "smsStatus">): boolean {
  return row.emailStatus !== "pending" && row.smsStatus !== "pending";
}

export function countProcessed(detail: Pick<StaffNoticeBatchDetail, "recipients">): number {
  return detail.recipients.filter(isRecipientProcessed).length;
}

export function countFailedRecipients(detail: Pick<StaffNoticeBatchDetail, "recipients">): number {
  return detail.recipients.filter((row) => row.emailStatus === "failed" || row.smsStatus === "failed").length;
}

export type StaffNoticeRowOutcome = "waiting" | "sending" | "done" | "smsFailed" | "failed";

/** 逐人结果的图标：邮件失败最严重（密码没发出去）；只有短信失败是提醒，密码已经到邮箱了。 */
export function resolveRowOutcome(row: Pick<StaffNoticeRecipientResult, "emailStatus" | "smsStatus">): StaffNoticeRowOutcome {
  if (row.emailStatus === "failed") return "failed";
  if (row.smsStatus === "failed") return "smsFailed";
  if (isRecipientProcessed(row)) return "done";
  // 邮件已出、短信还在排队，说明正处理到这个人；两边都没动才算等待中。
  const started = row.emailStatus === "sent" || row.smsStatus === "sent";
  return started ? "sending" : "waiting";
}

export function channelMark(status: StaffNoticeChannelStatus): string {
  if (status === "sent") return "✓";
  if (status === "failed") return "✗";
  if (status === "pending") return "…";
  return "—";
}

export type StaffNoticeRecordFilter = "all" | "failed" | "running";

export function filterBatches(batches: readonly StaffNoticeBatch[], filter: StaffNoticeRecordFilter): StaffNoticeBatch[] {
  if (filter === "failed") return batches.filter((item) => item.result === "partial_failed" || item.result === "all_failed");
  if (filter === "running") return batches.filter((item) => !isBatchFinished(item));
  return [...batches];
}

// ───────────────────────── 时间 ─────────────────────────

/** 后端时间是 UTC；SqlSugar 读出来的 DateTime 序列化后可能没有时区后缀，不补 Z 会被当成手机本地时间。 */
export function parseUtc(value: string | null | undefined): Date | null {
  if (!value) return null;
  const hasZone = /(?:Z|[+-]\d{2}:?\d{2})$/i.test(value);
  const date = new Date(hasZone ? value : `${value}Z`);
  return Number.isNaN(date.getTime()) ? null : date;
}

const pad = (value: number) => String(value).padStart(2, "0");

function isSameDay(left: Date, right: Date) {
  return left.getFullYear() === right.getFullYear()
    && left.getMonth() === right.getMonth()
    && left.getDate() === right.getDate();
}

/** 今天 16:42 / 昨天 10:18 / 11-04 15:05（手机本地时间）。 */
export function formatNoticeTime(
  value: string | null | undefined,
  labels: { today: string; yesterday: string },
  now: Date = new Date(),
): string {
  const date = parseUtc(value);
  if (!date) return "";
  const time = `${pad(date.getHours())}:${pad(date.getMinutes())}`;
  if (isSameDay(date, now)) return `${labels.today} ${time}`;
  const yesterday = new Date(now);
  yesterday.setDate(now.getDate() - 1);
  if (isSameDay(date, yesterday)) return `${labels.yesterday} ${time}`;
  return `${pad(date.getMonth() + 1)}-${pad(date.getDate())} ${time}`;
}
