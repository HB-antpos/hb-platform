import assert from "node:assert/strict";
import {
  channelMark,
  countFailedRecipients,
  countProcessed,
  countRecipients,
  decodeUserGuidsParam,
  encodeUserGuidsParam,
  filterBatches,
  filterRecipients,
  formatAud,
  formatNoticeTime,
  isBatchFinished,
  parseUtc,
  primaryReason,
  reconcileSelection,
  resolveDefaultSelection,
  resolveDefaultTemplate,
  resolveRowOutcome,
  shouldOfferActivationCard,
  summarizeSelection,
  templateSupportsSms,
} from "./logic";
import type { StaffNoticeBatch, StaffNoticePreflightRecipient, StaffNoticeTemplate } from "./types";

function recipient(overrides: Partial<StaffNoticePreflightRecipient>): StaffNoticePreflightRecipient {
  return {
    userGuid: "a",
    displayName: "Mia Chen",
    loginName: "mia",
    storeName: "Sunnybank",
    email: "mia@example.com",
    phone: "0491570156",
    isActive: true,
    hasLoggedIn: false,
    lastLoginAtUtc: null,
    status: "ready",
    reasons: [],
    willSendEmail: true,
    willSendSms: true,
    smsSkipReason: null,
    smsSegments: 1,
    ...overrides,
  };
}

function template(overrides: Partial<StaffNoticeTemplate>): StaffNoticeTemplate {
  return {
    key: "custom",
    name: "Custom",
    kind: "general",
    description: "",
    isEnabled: true,
    requiresEmail: false,
    issuesPassword: false,
    smsBody: "",
    ...overrides,
  };
}

const ready = recipient({ userGuid: "ready" });
const loggedIn = recipient({ userGuid: "logged", status: "confirm", reasons: ["already_logged_in"], hasLoggedIn: true, displayName: "Emma Tran" });
const suspicious = recipient({ userGuid: "odd", status: "confirm", reasons: ["suspicious_email"], willSendSms: false, smsSkipReason: "no_phone", smsSegments: 0 });
const blocked = recipient({ userGuid: "blocked", status: "blocked", reasons: ["no_email", "already_logged_in"], willSendEmail: false, email: null });
const all = [ready, loggedIn, suspicious, blocked];

// 默认模板：优先「账号已开通」，模板被停用时退回第一个，没有就是 null。
assert.equal(resolveDefaultTemplate([template({ key: "x" }), template({ key: "account-activated" })])?.key, "account-activated");
assert.equal(resolveDefaultTemplate([template({ key: "x" })])?.key, "x");
assert.equal(resolveDefaultTemplate([]), null);
assert.equal(templateSupportsSms(template({ smsBody: "  " })), false);
assert.equal(templateSupportsSms(template({ smsBody: "Hi" })), true);

// 默认勾选：只勾可发送的；已登录过、邮箱存疑默认不勾。
assert.deepEqual([...resolveDefaultSelection(all)], ["ready"]);

// 重新预检：保留手动勾的「需确认」，剔除变成不可发送的人。
assert.deepEqual(
  [...reconcileSelection(new Set(["ready", "logged", "blocked", "gone"]), all)].sort(),
  ["logged", "ready"],
);

assert.deepEqual(countRecipients(all), { all: 4, ready: 1, confirm: 2, blocked: 1 });
assert.deepEqual(filterRecipients(all, "confirm").map((item) => item.userGuid), ["logged", "odd"]);
assert.equal(filterRecipients(all, "all").length, 4);
// 不可发送的原因排在前面，行上显示它。
assert.equal(primaryReason(blocked), "no_email");
assert.equal(primaryReason(ready), null);

// 汇总只算真的会发的人：不可发送的即使在勾选集里也不计；只有发放密码的模板才点名已登录过的人。
const summary = summarizeSelection(
  { recipients: all, smsUnitCostAud: 0.0792 },
  new Set(["ready", "logged", "odd", "blocked"]),
  true,
);
assert.equal(summary.people, 3);
assert.equal(summary.emails, 3);
assert.equal(summary.smsMessages, 2);
assert.equal(formatAud(summary.costAud), "A$0.16");
assert.deepEqual(summary.reissued.map((item) => item.userGuid), ["logged"]);
assert.deepEqual(summarizeSelection({ recipients: all, smsUnitCostAud: 0 }, new Set(["logged"]), false).reissued, []);

// 情境卡片只认「从未登录」：被重置过密码但登录过的员工不出卡片。
const offer = { canSendNotice: true, active: true, loginEnabled: true, lastLoginTime: null };
assert.equal(shouldOfferActivationCard(offer), true);
assert.equal(shouldOfferActivationCard({ ...offer, lastLoginTime: undefined, loginEnabled: undefined }), true);
assert.equal(shouldOfferActivationCard({ ...offer, lastLoginTime: "2026-11-01T00:00:00Z" }), false);
assert.equal(shouldOfferActivationCard({ ...offer, canSendNotice: false }), false);
assert.equal(shouldOfferActivationCard({ ...offer, active: false }), false);
assert.equal(shouldOfferActivationCard({ ...offer, loginEnabled: false }), false);

// 路由参数：去重、去空、限制 100 个。
assert.equal(encodeUserGuidsParam(["a", "b"]), "a,b");
assert.deepEqual(decodeUserGuidsParam("a, b,,a"), ["a", "b"]);
assert.deepEqual(decodeUserGuidsParam(["a", "b"]), ["a", "b"]);
assert.deepEqual(decodeUserGuidsParam(undefined), []);
assert.equal(decodeUserGuidsParam(Array.from({ length: 120 }, (_, index) => `g${index}`).join(",")).length, 100);

// 批次状态
assert.equal(isBatchFinished({ status: "completed" }), true);
assert.equal(isBatchFinished({ status: "interrupted" }), true);
assert.equal(isBatchFinished({ status: "queued" }), false);
assert.equal(isBatchFinished(undefined), false);

const row = (emailStatus: "none" | "skipped" | "pending" | "sent" | "failed", smsStatus: "none" | "skipped" | "pending" | "sent" | "failed") =>
  ({ id: "r", userGuid: "u", displayName: "Mia", emailStatus, smsStatus, note: "" });
assert.equal(resolveRowOutcome(row("pending", "pending")), "waiting");
assert.equal(resolveRowOutcome(row("sent", "pending")), "sending");
assert.equal(resolveRowOutcome(row("sent", "sent")), "done");
assert.equal(resolveRowOutcome(row("sent", "skipped")), "done");
assert.equal(resolveRowOutcome(row("sent", "failed")), "smsFailed");
assert.equal(resolveRowOutcome(row("failed", "skipped")), "failed");
assert.equal(countProcessed({ recipients: [row("sent", "sent"), row("sent", "pending"), row("failed", "none")] }), 2);
assert.equal(countFailedRecipients({ recipients: [row("sent", "failed"), row("failed", "none"), row("sent", "sent")] }), 2);
assert.equal(channelMark("sent"), "✓");
assert.equal(channelMark("failed"), "✗");
assert.equal(channelMark("skipped"), "—");

const batch = (result: StaffNoticeBatch["result"], status: StaffNoticeBatch["status"], id: string) =>
  ({ id, result, status } as StaffNoticeBatch);
const batches = [batch("all_sent", "completed", "1"), batch("partial_failed", "completed", "2"), batch("all_failed", "completed", "3"), batch("running", "running", "4")];
assert.deepEqual(filterBatches(batches, "failed").map((item) => item.id), ["2", "3"]);
assert.deepEqual(filterBatches(batches, "running").map((item) => item.id), ["4"]);
assert.equal(filterBatches(batches, "all").length, 4);

// 后端时间可能没有时区后缀，必须按 UTC 解析，不能当成手机本地时间。
assert.equal(parseUtc("2026-11-06T06:42:00")?.toISOString(), "2026-11-06T06:42:00.000Z");
assert.equal(parseUtc("2026-11-06T06:42:00Z")?.toISOString(), "2026-11-06T06:42:00.000Z");
assert.equal(parseUtc("2026-11-06T16:42:00+10:00")?.toISOString(), "2026-11-06T06:42:00.000Z");
assert.equal(parseUtc(null), null);
assert.equal(parseUtc("bad"), null);

const labels = { today: "今天", yesterday: "昨天" };
const now = new Date(2026, 10, 6, 18, 0);
const local = (day: number, hour: number) => new Date(2026, 10, day, hour, 5).toISOString();
assert.equal(formatNoticeTime(local(6, 16), labels, now), "今天 16:05");
assert.equal(formatNoticeTime(local(5, 10), labels, now), "昨天 10:05");
assert.equal(formatNoticeTime(local(4, 15), labels, now), "11-04 15:05");
assert.equal(formatNoticeTime(null, labels, now), "");
