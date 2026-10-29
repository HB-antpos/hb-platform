import assert from "node:assert/strict";
import { buildEmailRequest, canSubmitEmail, evaluateRecipient, PAYSLIP_EMAIL_NOTE_MAX } from "./email-rules";

const profile = { email: "alex.morgan@example.com", source: "profile" as const };
const none = { email: null, source: "none" as const };
const key = { connectionGuid: "c1", employeeUid: "e1", paymentDate: "2026-10-29", periodStart: "2026-10-19" };

// 与档案一致（不区分大小写、忽略首尾空白）：无需确认
const same = evaluateRecipient("  Alex.Morgan@Example.com ", profile);
assert.equal(same.issue, null);
assert.equal(same.matchesProfile, true);
assert.equal(same.requiresConfirm, false);
assert.equal(same.address, "Alex.Morgan@Example.com");
assert.equal(canSubmitEmail(same, false, false), true);

// 与档案不同：必须勾选「我已核对收件人」才可发送
const other = evaluateRecipient("someone@example.com", profile);
assert.equal(other.requiresConfirm, true);
assert.equal(canSubmitEmail(other, false, false), false);
assert.equal(canSubmitEmail(other, true, false), true);
// 发送中不可重复提交
assert.equal(canSubmitEmail(other, true, true), false);

// 档案没有邮箱：手填无需确认
const manual = evaluateRecipient("manual@example.com", none);
assert.equal(manual.hasProfileEmail, false);
assert.equal(manual.requiresConfirm, false);
assert.equal(canSubmitEmail(manual, false, false), true);

// 档案有邮箱但接口没给明文（只有脱敏）：任何输入都视为「不同」，要求确认
const hidden = evaluateRecipient("alex.morgan@example.com", { email: null, source: "profile" });
assert.equal(hidden.hasProfileEmail, true);
assert.equal(hidden.matchesProfile, false);
assert.equal(hidden.requiresConfirm, true);

// 校验：空 / 多个 / 格式不对；非法输入不要求确认也不能发送
assert.equal(evaluateRecipient("", profile).issue, "empty");
assert.equal(evaluateRecipient("   ", profile).issue, "empty");
assert.equal(evaluateRecipient("a@example.com, b@example.com", none).issue, "multiple");
assert.equal(evaluateRecipient("a@example.com;b@example.com", none).issue, "multiple");
assert.equal(evaluateRecipient("a@example.com b@example.com", none).issue, "multiple");
assert.equal(evaluateRecipient("not-an-email", none).issue, "invalid");
assert.equal(evaluateRecipient("a@b", none).issue, "invalid");
assert.equal(evaluateRecipient("@example.com", none).issue, "invalid");
const bad = evaluateRecipient("nope", profile);
assert.equal(bad.requiresConfirm, false);
assert.equal(canSubmitEmail(bad, true, false), false);

// 请求体：附言去空白、空则不带；确认标记只在确实需要核对且已勾选时为 true
const request = buildEmailRequest(key, { to: "someone@example.com", copyToMe: true, note: "  Thanks  ", confirmed: true }, other);
assert.deepEqual(request, {
  connectionGuid: "c1",
  employeeUid: "e1",
  paymentDate: "2026-10-29",
  periodStart: "2026-10-19",
  to: "someone@example.com",
  copyToMe: true,
  note: "Thanks",
  recipientChangeConfirmed: true,
});
const plain = buildEmailRequest({ ...key, periodStart: null }, { to: "alex.morgan@example.com", copyToMe: false, note: "   ", confirmed: true }, same);
assert.equal("note" in plain, false);
assert.equal("periodStart" in plain, false);
assert.equal(plain.recipientChangeConfirmed, false, "收件人与档案一致时不带确认标记");
assert.equal(buildEmailRequest(key, { to: "someone@example.com", copyToMe: false, note: "", confirmed: false }, other).recipientChangeConfirmed, false);
assert.equal(buildEmailRequest(key, { to: "x@example.com", copyToMe: false, note: "n".repeat(PAYSLIP_EMAIL_NOTE_MAX + 50), confirmed: false }, manual).note?.length, PAYSLIP_EMAIL_NOTE_MAX);
