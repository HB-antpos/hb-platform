import assert from "node:assert/strict";
import {
  buildAdminCreateInput,
  defaultUsernameFromEmail,
  EMPTY_ADMIN_CREATE_FORM,
  hasAdminCreateErrors,
  isAdminCreateFormDirty,
  needsManageAssignment,
  retryAdminStoreAssignment,
  runAdminCreate,
  summarizeAssignments,
  validateAdminCreateForm,
  type AdminCreateDeps,
  type AdminCreateFormValues,
} from "./admin-create-form";
import type { IdentityUserDetail } from "@/modules/identity-admin/types";

const messages: Record<string, string> = {
  "messages.emailRequired": "email-required",
  "messages.emailInvalid": "email-invalid",
  "messages.usernameRequired": "username-required",
  "messages.usernameLength": "username-length",
};
const t = (key: string) => messages[key] ?? key;

const filled: AdminCreateFormValues = {
  email: " grace.liu@hotbargain.com.au ",
  username: " grace.liu ",
  names: { familyName: "Liu", givenName: "Grace", middleName: "", nickname: "" },
  phone: "0433 222 901",
  birthday: "1990-11-05",
  guardian: { name: "", relationship: "", phone: "", email: "" },
  enabled: true,
  password: "secret12",
  confirmPassword: "secret12",
  roleGuids: ["role-staff", "role-finance"],
  assignments: [
    { storeGUID: "store-a", isPrimary: true },
    { storeGUID: "store-b", isPrimary: false },
  ],
};

// ── 默认账号：邮箱 @ 前的部分，小写 ──
assert.equal(defaultUsernameFromEmail(" Grace.Liu@HotBargain.com.au "), "grace.liu");
assert.equal(defaultUsernameFromEmail("no-at-sign"), "no-at-sign");
assert.equal(defaultUsernameFromEmail(`${"a".repeat(51)}@x.com`), "", "超过 50 个字符留空让用户自己填");

// ── 校验 ──
{
  const errors = validateAdminCreateForm(EMPTY_ADMIN_CREATE_FORM, t);
  assert.equal(errors.email, "email-required");
  assert.equal(errors.username, "username-required");
  assert.equal(errors.password, "required");
  assert.deepEqual(errors.names, { familyName: "required", givenName: "required" });
  assert.equal(errors.phone, "required", "电话必填");
  assert.equal(errors.birthday, "required", "生日必填");
  assert.deepEqual(errors.guardian, {}, "没有生日时不出现监护人一节");
  assert.equal(hasAdminCreateErrors(errors), true);
  assert.equal(hasAdminCreateErrors({ ...validateAdminCreateForm(filled, t), phone: "invalid" }), true, "电话错误也算有错");

  assert.equal(hasAdminCreateErrors(validateAdminCreateForm(filled, t)), false);
  assert.equal(validateAdminCreateForm({ ...filled, password: "12345", confirmPassword: "12345" }, t).password, "tooShort");
  assert.equal(validateAdminCreateForm({ ...filled, password: "x".repeat(101), confirmPassword: "x".repeat(101) }, t).password, "tooLong");
  assert.equal(validateAdminCreateForm({ ...filled, confirmPassword: "other" }, t).confirmPassword, "mismatch");
  assert.equal(validateAdminCreateForm({ ...filled, email: "bad" }, t).email, "email-invalid");
}

// ── 未成年（悉尼 2026-10-30 时 15 岁）：监护人四项必填，邮箱不能与本人相同 ──
const today = new Date(2026, 9, 30);
{
  const minor: AdminCreateFormValues = { ...filled, birthday: "2011-03-02" };
  const errors = validateAdminCreateForm(minor, t, today);
  assert.deepEqual(errors.guardian, { name: "required", relationship: "required", phone: "required", email: "required" });
  const sameEmail = validateAdminCreateForm(
    { ...minor, guardian: { name: "Linh", relationship: "Mother", phone: "0412 345 678", email: "GRACE.LIU@hotbargain.com.au" } },
    t,
    today
  );
  assert.equal(sameEmail.guardian.email, "sameAsStaff");
  const ok = { ...minor, guardian: { name: " Linh ", relationship: "Mother", phone: "0412 345 678", email: "linh@gmail.com" } };
  assert.equal(hasAdminCreateErrors(validateAdminCreateForm(ok, t, today)), false);
  assert.deepEqual(buildAdminCreateInput(ok, today).guardian, { name: "Linh", relationship: "Mother", phone: "0412 345 678", email: "linh@gmail.com" });
  assert.equal(isAdminCreateFormDirty({ ...EMPTY_ADMIN_CREATE_FORM, birthday: "2011-03-02" }), true, "填了生日也算有改动");
  assert.equal(isAdminCreateFormDirty({ ...EMPTY_ADMIN_CREATE_FORM, phone: "04" }), true, "填了电话也算有改动");
}

// ── 第一步请求体：带角色与全部已选分店（普通关联）；电话、生日必带；成年不带监护人；姓名有值才带 ──
{
  const input = buildAdminCreateInput(filled, today);
  assert.deepEqual(input, {
    username: "grace.liu",
    email: "grace.liu@hotbargain.com.au",
    password: "secret12",
    passwordFormat: "raw",
    fullName: "Grace Liu",
    familyName: "Liu",
    givenName: "Grace",
    isActive: true,
    roleGuids: ["role-staff", "role-finance"],
    storeGuids: ["store-a", "store-b"],
    phone: "0433 222 901",
    birthday: "1990-11-05",
  });
  assert.equal("guardian" in input, false, "成年不带监护人");
}

// ── 分店摘要 ──
assert.deepEqual(summarizeAssignments(filled.assignments), { total: 2, manageCount: 1 });
assert.equal(needsManageAssignment(filled.assignments), true);
assert.equal(needsManageAssignment([{ storeGUID: "store-b", isPrimary: false }]), false);
assert.equal(isAdminCreateFormDirty(EMPTY_ADMIN_CREATE_FORM), false);
assert.equal(isAdminCreateFormDirty({ ...EMPTY_ADMIN_CREATE_FORM, roleGuids: ["r"] }), true);

// ── 两步创建流程 ──
const user = { userGUID: "new-user", username: "grace.liu" } as IdentityUserDetail;

function deps(overrides: Partial<AdminCreateDeps> = {}) {
  const calls = { create: 0, assign: [] as unknown[] };
  const value: AdminCreateDeps = {
    create: async () => { calls.create += 1; return user; },
    assignStores: async (input) => { calls.assign.push(input); return true; },
    isUncertain: (error) => (error as { uncertain?: boolean })?.uncertain === true,
    ...overrides,
  };
  return { value, calls };
}

(async () => {
  const input = buildAdminCreateInput(filled);

  // 有「管理」分店：先建号，再把完整分店分配（含管理标记）写入。
  {
    const { value, calls } = deps();
    const outcome = await runAdminCreate(value, input, filled.assignments);
    assert.equal(outcome.kind, "ok");
    assert.equal(calls.create, 1);
    assert.deepEqual(calls.assign, [{ userGuid: "new-user", assignments: filled.assignments }]);
  }
  // 没有「管理」分店：不需要第二步。
  {
    const { value, calls } = deps();
    const outcome = await runAdminCreate(value, input, [{ storeGUID: "store-b", isPrimary: false }]);
    assert.equal(outcome.kind, "ok");
    assert.equal(calls.assign.length, 0);
  }
  // 建号被明确拒绝：账号没建，不进入第二步，可直接改完重试。
  {
    const failure = Object.assign(new Error("exists"), { apiBusinessError: true });
    const { value, calls } = deps({ create: async () => { throw failure; } });
    const outcome = await runAdminCreate(value, input, filled.assignments);
    assert.equal(outcome.kind, "createFailed");
    assert.equal(calls.assign.length, 0);
  }
  // 建号结果未知（超时 / 5xx）：必须冻结，不进入第二步。
  {
    const { value, calls } = deps({ create: async () => { throw Object.assign(new Error("timeout"), { uncertain: true }); } });
    const outcome = await runAdminCreate(value, input, filled.assignments);
    assert.equal(outcome.kind, "createUncertain");
    assert.equal(calls.assign.length, 0);
  }
  // 账号已建、第二步失败：返回已建账号，由界面停在表单提示「第二步失败」，只重试第二步。
  {
    const { value, calls } = deps({ assignStores: async () => { throw new Error("boom"); } });
    const outcome = await runAdminCreate(value, input, filled.assignments);
    assert.equal(outcome.kind, "storesFailed");
    assert.equal(outcome.kind === "storesFailed" && outcome.user.userGUID, "new-user");
    assert.equal(calls.create, 1);

    const retryDeps = deps();
    const retry = await retryAdminStoreAssignment(retryDeps.value, user, filled.assignments);
    assert.equal(retry.kind, "ok");
    assert.equal(retryDeps.calls.create, 0, "重试绝不再次创建账号");
    assert.deepEqual(retryDeps.calls.assign, [{ userGuid: "new-user", assignments: filled.assignments }]);

    const stillFailing = await retryAdminStoreAssignment({ assignStores: async () => { throw new Error("again"); } }, user, filled.assignments);
    assert.equal(stillFailing.kind, "storesFailed");
  }

  console.log("admin-create-form.test.ts: ok");
})().catch((error) => {
  console.error(error);
  process.exit(1);
});
