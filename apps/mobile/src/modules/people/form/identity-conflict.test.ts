import assert from "node:assert/strict";
import { unwrapApiEnvelope } from "@/shared/api/api-envelope";
import enPeopleForm from "@/locales/en/screens/peopleForm.json";
import zhPeopleForm from "@/locales/zh/screens/peopleForm.json";
import { resolveIdentityConflictField } from "./identity-conflict";

function businessError(payload: unknown): unknown {
  try {
    unwrapApiEnvelope(payload);
  } catch (error) {
    return error;
  }
  throw new Error("测试前置条件失败：success:false 必须转换为异常");
}

// ── 真实链路：后端 200 + success:false，经 unwrapApiEnvelope 变成带 code 的业务错误 ──
assert.equal(
  resolveIdentityConflictField(businessError({ success: false, errorCode: "USERNAME_EXISTS", message: "该用户名已被使用" })),
  "username",
);
assert.equal(
  resolveIdentityConflictField(businessError({ success: false, errorCode: "EMAIL_EXISTS", message: "该邮箱已被其他账号使用" })),
  "email",
);
// 大小写、首尾空白不影响判定。
assert.equal(resolveIdentityConflictField({ code: " email_exists " }), "email");

// ── 兼容 4xx：Axios 错误的 response.data 里带错误码，而 error.code 是 Axios 自己的 ERR_BAD_REQUEST ──
assert.equal(
  resolveIdentityConflictField({ code: "ERR_BAD_REQUEST", response: { status: 409, data: { errorCode: "USERNAME_EXISTS" } } }),
  "username",
);
assert.equal(
  resolveIdentityConflictField({ code: "ERR_BAD_REQUEST", response: { status: 400, data: { ErrorCode: "EMAIL_EXISTS" } } }),
  "email",
);

// ── 其他错误不归到字段，交还表单走原来的通用提示 ──
assert.equal(resolveIdentityConflictField(businessError({ success: false, errorCode: "FORBIDDEN" })), null);
assert.equal(resolveIdentityConflictField({ code: "ERR_NETWORK", message: "Network Error" }), null);
assert.equal(resolveIdentityConflictField(new Error("boom")), null);
assert.equal(resolveIdentityConflictField(null), null);
assert.equal(resolveIdentityConflictField("USERNAME_EXISTS"), null);

// ── 文案：中英文都要有，英文用 Username / email 术语 ──
for (const locale of [zhPeopleForm, enPeopleForm]) {
  assert.ok(locale.form.usernameTaken.trim());
  assert.ok(locale.form.emailTaken.trim());
  assert.ok(locale.edit.emailErrors.taken.trim());
}
assert.match(enPeopleForm.form.usernameTaken, /username/i);
assert.match(enPeopleForm.form.emailTaken, /email/i);
assert.match(zhPeopleForm.form.usernameTaken, /账号/);
assert.match(zhPeopleForm.edit.emailErrors.taken, /邮箱/);

console.log("人员表单用户名 / 邮箱重复错误映射测试通过");
