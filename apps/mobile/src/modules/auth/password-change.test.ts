import assert from "node:assert/strict";
import {
  canSubmitPasswordChange,
  getPasswordRuleState,
  normalizePasswordChangeForm,
  resolvePasswordChangeErrorKey,
  shouldForcePasswordChange,
} from "./password-change";

// 只有个人账号会话且服务端标记须改密时才拦截。
assert.equal(shouldForcePasswordChange({ user: { mustChangePassword: true }, sessionKind: "account", isAuthenticated: true }), true);
assert.equal(shouldForcePasswordChange({ user: { mustChangePassword: false }, sessionKind: "account", isAuthenticated: true }), false);
assert.equal(shouldForcePasswordChange({ user: {}, sessionKind: "account", isAuthenticated: true }), false, "旧后端不返回字段时不拦截");
assert.equal(shouldForcePasswordChange({ user: { mustChangePassword: true }, sessionKind: "deviceAccount", isAuthenticated: true }), false, "设备绑定账号不拦截");
assert.equal(shouldForcePasswordChange({ user: { mustChangePassword: true }, sessionKind: "iosReview", isAuthenticated: true }), false, "审核会话不拦截");
assert.equal(shouldForcePasswordChange({ user: { mustChangePassword: true }, sessionKind: "account", isAuthenticated: false }), false);
assert.equal(shouldForcePasswordChange({ user: null, sessionKind: "account", isAuthenticated: true }), false);

// 首尾空格与登录口径一致地去掉。
assert.deepEqual(
  normalizePasswordChangeForm({ currentPassword: " old ", newPassword: " new123 ", confirmPassword: "new123 " }),
  { currentPassword: "old", newPassword: "new123", confirmPassword: "new123" }
);

// 规则逐条判定。
assert.deepEqual(
  getPasswordRuleState({ currentPassword: "Init123", newPassword: "abc", confirmPassword: "" }),
  { minLength: false, differentFromCurrent: true, confirmed: false }
);
assert.deepEqual(
  getPasswordRuleState({ currentPassword: "Init123", newPassword: " Init123 ", confirmPassword: "Init123" }),
  { minLength: true, differentFromCurrent: false, confirmed: true },
  "去掉空格后与当前密码相同不算修改"
);
assert.deepEqual(
  getPasswordRuleState({ currentPassword: "Init123", newPassword: "MyOwn456", confirmPassword: "MyOwn45" }),
  { minLength: true, differentFromCurrent: true, confirmed: false }
);
assert.deepEqual(
  getPasswordRuleState({ currentPassword: "", newPassword: "", confirmPassword: "" }),
  { minLength: false, differentFromCurrent: false, confirmed: false },
  "空表单不显示任何规则已满足"
);

assert.equal(canSubmitPasswordChange({ currentPassword: "Init123", newPassword: "MyOwn456", confirmPassword: "MyOwn456" }), true);
assert.equal(canSubmitPasswordChange({ currentPassword: " ", newPassword: "MyOwn456", confirmPassword: "MyOwn456" }), false, "当前密码必填");
assert.equal(canSubmitPasswordChange({ currentPassword: "Init123", newPassword: "x".repeat(101), confirmPassword: "x".repeat(101) }), false, "超过 100 位与后端一致拒绝");

// 后端业务错误映射到翻译键。
assert.equal(resolvePasswordChangeErrorKey(new Error("当前密码错误")), "changePassword.errors.wrongCurrent");
assert.equal(resolvePasswordChangeErrorKey(new Error("新密码不能与当前密码相同")), "changePassword.errors.sameAsCurrent");
assert.equal(resolvePasswordChangeErrorKey(new Error("Network Error")), null);

console.log("password-change tests passed");
