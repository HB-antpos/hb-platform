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
  birthday: "1998-07-21",
  guardian: { name: "", relationship: "", phone: "", email: "" },
  enabled: true,
  loginEnabled: false,
};

/** 固定「悉尼今天」= 2026-10-30，年龄判断不随运行日期变化。 */
const today = new Date(2026, 9, 30);

// ── 请求体：固定本店店员 + casual；不提交密码；四段姓名有值才带，fullName 为合成值 ──
{
  const payload = buildStaffCreatePayload(filled, "S001", today);
  assert.deepEqual(payload, {
    username: "emily.chen@gmail.com",
    fullName: "Emily Chen",
    familyName: "Chen",
    givenName: "Emily",
    nickname: "Em",
    email: "emily.chen@gmail.com",
    phone: "0412 345 678",
    birthday: "1998-07-21",
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
  assert.equal("guardian" in payload, false, "成年不带监护人");
}
// 开启登录：才发初始密码邮件；停用账号 status=0；电话去首尾空白。
{
  const payload = buildStaffCreatePayload(
    { ...filled, loginEnabled: true, enabled: false, phone: " 0412 345 678 ", names: { ...filled.names, middleName: "Rose" } },
    "S002",
    today
  );
  assert.equal(payload.sendPasswordSetupEmail, true);
  assert.equal(payload.loginEnabled, true);
  assert.equal(payload.status, 0);
  assert.equal(payload.phone, "0412 345 678");
  assert.equal(payload.fullName, "Emily Rose Chen");
  assert.equal(payload.middleName, "Rose");
  assert.equal("password" in payload, false);
}
// 中文姓名：姓 + 名无空格。
assert.equal(
  buildStaffCreatePayload({ ...filled, names: { familyName: "王", givenName: "芳", middleName: "", nickname: "" } }, "S001", today).fullName,
  "王芳"
);

// ── 校验：邮箱必填、账号 3–50、姓名必填、电话必填且是澳洲手机号、生日必填 ──
{
  const errors = validateStaffCreateForm(EMPTY_STAFF_CREATE_FORM, t, today);
  assert.equal(errors.email, "email-required");
  assert.equal(errors.username, "username-required");
  assert.deepEqual(errors.names, { familyName: "required", givenName: "required" });
  assert.equal(errors.phone, "required", "电话改为必填");
  assert.equal(errors.birthday, "required", "生日必填");
  assert.deepEqual(errors.guardian, {});
  assert.equal(hasStaffCreateErrors(errors), true);

  const ok = validateStaffCreateForm(filled, t, today);
  assert.equal(hasStaffCreateErrors(ok), false);

  const bad = validateStaffCreateForm({ ...filled, email: "no-at", username: "ab", phone: "123", birthday: "2026-10-31" }, t, today);
  assert.equal(bad.email, "email-invalid");
  assert.equal(bad.username, "username-length");
  assert.equal(bad.phone, "invalid");
  assert.equal(bad.birthday, "invalid", "生日不能晚于今天");
  assert.equal(validateStaffCreateForm({ ...filled, birthday: "1939-12-31" }, t, today).birthday, "invalid", "不能早于 1940");
  assert.equal(hasStaffCreateErrors({ ...ok, guardian: { name: "required" } }), true, "监护人错误也算有错");
}

// ── 未成年（2011-03-02，悉尼 2026-10-30 时 15 岁）：监护人四项必填；手机不能与本人相同 ──
{
  const minor: StaffCreateFormValues = { ...filled, birthday: "2011-03-02" };
  assert.deepEqual(validateStaffCreateForm(minor, t, today).guardian, {
    name: "required",
    relationship: "required",
    phone: "required",
    email: "required",
  });
  const samePhone = validateStaffCreateForm(
    { ...minor, guardian: { name: "Linh Nguyen", relationship: "Mother", phone: "+61 412 345 678", email: "linh@gmail.com" } },
    t,
    today
  );
  assert.equal(samePhone.guardian.phone, "sameAsStaff", "+61 写法与 04 写法是同一个号码");

  const guardian = { name: "Linh Nguyen ", relationship: "Legal guardian", phone: "0423 111 222", email: " linh@gmail.com" };
  const payload = buildStaffCreatePayload({ ...minor, guardian }, "S001", today);
  assert.equal(payload.birthday, "2011-03-02");
  assert.deepEqual(payload.guardian, { name: "Linh Nguyen", relationship: "Legal guardian", phone: "0423 111 222", email: "linh@gmail.com" });

  // 改回成年生日：已填的监护人保留在表单里，但不校验、不提交。
  const adultAgain = { ...minor, birthday: "1998-07-21", guardian: { ...guardian, phone: "" } };
  assert.deepEqual(validateStaffCreateForm(adultAgain, t, today).guardian, {});
  assert.equal("guardian" in buildStaffCreatePayload(adultAgain, "S001", today), false);
}

// ── 是否填过内容（决定返回时弹不弹「放弃修改？」）──
assert.equal(isStaffCreateFormDirty(EMPTY_STAFF_CREATE_FORM), false);
assert.equal(isStaffCreateFormDirty({ ...EMPTY_STAFF_CREATE_FORM, names: { ...EMPTY_STAFF_CREATE_FORM.names, nickname: "Em" } }), true);
assert.equal(isStaffCreateFormDirty({ ...EMPTY_STAFF_CREATE_FORM, loginEnabled: true }), true);
assert.equal(isStaffCreateFormDirty({ ...EMPTY_STAFF_CREATE_FORM, enabled: false }), true);
assert.equal(isStaffCreateFormDirty({ ...EMPTY_STAFF_CREATE_FORM, phone: "04" }), true, "填了电话算有改动");
assert.equal(isStaffCreateFormDirty({ ...EMPTY_STAFF_CREATE_FORM, birthday: "2011-03-02" }), true, "选了生日算有改动");
assert.equal(
  isStaffCreateFormDirty({ ...EMPTY_STAFF_CREATE_FORM, guardian: { ...EMPTY_STAFF_CREATE_FORM.guardian, name: "Linh" } }),
  true,
  "填了监护人算有改动"
);

// ── 新增页版本：有 storeCode 或非管理员 → 店长版；管理员无 storeCode → 管理员版 ──
const adminBase = { storeCode: "", isAdmin: true, hasUsersCreate: true, adminSessionAllowed: true };
assert.equal(resolveCreateVariant(adminBase), "admin");
assert.equal(resolveCreateVariant({ ...adminBase, storeCode: "S001" }), "staff");
assert.equal(resolveCreateVariant({ ...adminBase, storeCode: "  " }), "admin");
assert.equal(resolveCreateVariant({ ...adminBase, isAdmin: false }), "staff");
assert.equal(resolveCreateVariant({ ...adminBase, adminSessionAllowed: false }), "staff", "设备 / 审核会话不能走管理员接口");
assert.equal(resolveCreateVariant({ ...adminBase, hasUsersCreate: false }), "staff");

console.log("staff-create-form.test.ts: ok");
