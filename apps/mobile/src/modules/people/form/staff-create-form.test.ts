import assert from "node:assert/strict";
import {
  buildStaffCreatePayload,
  EMPTY_STAFF_CREATE_FORM,
  hasStaffCreateErrors,
  isStaffCreateFormDirty,
  validateStaffCreateForm,
  type StaffCreateFormValues,
} from "./staff-create-form";
import { resolveCreateVariant } from "./create-variant";

const messages: Record<string, string> = {
  "messages.emailRequired": "email-required",
  "messages.emailInvalid": "email-invalid",
  "messages.usernameRequired": "username-required",
  "messages.usernameLength": "username-length",
  "messages.phoneInvalid": "phone-invalid",
};
const t = (key: string) => messages[key] ?? key;

const filled: StaffCreateFormValues = {
  email: " emily.chen@gmail.com ",
  username: " emily.chen@gmail.com ",
  names: { familyName: "Chen", givenName: "Emily", middleName: "", nickname: "Em" },
  phone: "0412 345 678",
  enabled: true,
  loginEnabled: false,
};

// ── 请求体：固定本店店员 + casual；不提交密码；四段姓名有值才带，fullName 为合成值 ──
{
  const payload = buildStaffCreatePayload(filled, "S001");
  assert.deepEqual(payload, {
    username: "emily.chen@gmail.com",
    fullName: "Emily Chen",
    familyName: "Chen",
    givenName: "Emily",
    nickname: "Em",
    email: "emily.chen@gmail.com",
    phone: "0412 345 678",
    status: 1,
    storeCode: "S001",
    roleNames: ["StoreStaff"],
    passwordFormat: "raw",
    employmentType: "casual",
    loginEnabled: false,
  });
  assert.equal("password" in payload, false, "不得带密码");
  assert.equal("sendPasswordSetupEmail" in payload, false, "未开启登录不发初始密码邮件");
  assert.equal("middleName" in payload, false, "空的中间名不发");
}
// 开启登录：才发初始密码邮件；停用账号 status=0；电话为空不带值。
{
  const payload = buildStaffCreatePayload(
    { ...filled, loginEnabled: true, enabled: false, phone: "  ", names: { ...filled.names, middleName: "Rose" } },
    "S002"
  );
  assert.equal(payload.sendPasswordSetupEmail, true);
  assert.equal(payload.loginEnabled, true);
  assert.equal(payload.status, 0);
  assert.equal(payload.phone, undefined);
  assert.equal(payload.fullName, "Emily Rose Chen");
  assert.equal(payload.middleName, "Rose");
  assert.equal("password" in payload, false);
}
// 中文姓名：姓 + 名无空格。
assert.equal(
  buildStaffCreatePayload({ ...filled, names: { familyName: "王", givenName: "芳", middleName: "", nickname: "" } }, "S001").fullName,
  "王芳"
);

// ── 校验：邮箱必填、账号 3–50、姓名必填、电话格式 ──
{
  const errors = validateStaffCreateForm(EMPTY_STAFF_CREATE_FORM, t);
  assert.equal(errors.email, "email-required");
  assert.equal(errors.username, "username-required");
  assert.deepEqual(errors.names, { familyName: "required", givenName: "required" });
  assert.equal(errors.phone, undefined, "电话选填");
  assert.equal(hasStaffCreateErrors(errors), true);

  const ok = validateStaffCreateForm(filled, t);
  assert.equal(hasStaffCreateErrors(ok), false);

  const bad = validateStaffCreateForm({ ...filled, email: "no-at", username: "ab", phone: "123" }, t);
  assert.equal(bad.email, "email-invalid");
  assert.equal(bad.username, "username-length");
  assert.equal(bad.phone, "phone-invalid");
}

// ── 是否填过内容（决定返回时弹不弹「放弃修改？」）──
assert.equal(isStaffCreateFormDirty(EMPTY_STAFF_CREATE_FORM), false);
assert.equal(isStaffCreateFormDirty({ ...EMPTY_STAFF_CREATE_FORM, names: { ...EMPTY_STAFF_CREATE_FORM.names, nickname: "Em" } }), true);
assert.equal(isStaffCreateFormDirty({ ...EMPTY_STAFF_CREATE_FORM, loginEnabled: true }), true);
assert.equal(isStaffCreateFormDirty({ ...EMPTY_STAFF_CREATE_FORM, enabled: false }), true);

// ── 新增页版本：有 storeCode 或非管理员 → 店长版；管理员无 storeCode → 管理员版 ──
const adminBase = { storeCode: "", isAdmin: true, hasUsersCreate: true, adminSessionAllowed: true };
assert.equal(resolveCreateVariant(adminBase), "admin");
assert.equal(resolveCreateVariant({ ...adminBase, storeCode: "S001" }), "staff");
assert.equal(resolveCreateVariant({ ...adminBase, storeCode: "  " }), "admin");
assert.equal(resolveCreateVariant({ ...adminBase, isAdmin: false }), "staff");
assert.equal(resolveCreateVariant({ ...adminBase, adminSessionAllowed: false }), "staff", "设备 / 审核会话不能走管理员接口");
assert.equal(resolveCreateVariant({ ...adminBase, hasUsersCreate: false }), "staff");

console.log("staff-create-form.test.ts: ok");
