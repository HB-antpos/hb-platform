import assert from "node:assert/strict";
import {
  buildIdentityUpdateInput,
  buildPersonEditBaseline,
  buildStoreUserUpdatePayload,
  hasPersonEditErrors,
  listPersonEditChanges,
  resolvePersonEditSource,
  shouldPromptPasswordSetup,
  validatePersonEdit,
  type PersonEditValues,
} from "./person-edit-form";

const structured = buildPersonEditBaseline({
  fullName: "Emily Rose Chen",
  givenName: "Emily",
  middleName: "Rose",
  familyName: "Chen",
  nickname: "Em",
  email: "emily.chen@gmail.com",
  phone: "0412 345 678",
  loginEnabled: true,
});
const base: PersonEditValues = structured.values;
const flags = { phoneAvailable: true, loginAvailable: true };

// ── 预填 ──
assert.deepEqual(base, {
  names: { familyName: "Chen", givenName: "Emily", middleName: "Rose", nickname: "Em" },
  phone: "0412 345 678",
  email: "emily.chen@gmail.com",
  loginEnabled: true,
});
assert.equal(structured.legacySplit, false);
{
  const legacy = buildPersonEditBaseline({ fullName: "Old Staff", email: "old@x.store.local", loginEnabled: false });
  assert.equal(legacy.legacySplit, true, "只有全名：自动拆分并提示核对");
  assert.deepEqual(legacy.values.names, { familyName: "Staff", givenName: "Old", middleName: "", nickname: "" });
  assert.equal(legacy.values.email, "", "分店占位邮箱不展示");
  assert.equal(legacy.values.loginEnabled, false);
  assert.equal(buildPersonEditBaseline({ fullName: "A B" }).values.loginEnabled, true, "旧后端缺省按允许登录");
}

// ── 改动计数：每个变化字段算一项；没改动为 0 ──
assert.deepEqual(listPersonEditChanges(base, base, flags), []);
assert.deepEqual(listPersonEditChanges(base, { ...base, names: { ...base.names, givenName: "Emma" } }, flags), ["givenName"]);
assert.deepEqual(
  listPersonEditChanges(
    base,
    { ...base, names: { ...base.names, nickname: "" }, phone: "0400 000 000", email: "new@x.com", loginEnabled: false },
    flags
  ),
  ["nickname", "phone", "email", "loginEnabled"]
);
assert.deepEqual(listPersonEditChanges(base, { ...base, email: " EMILY.CHEN@gmail.com " }, flags), [], "邮箱仅大小写 / 空白不同不算改动");
// 管理员数据源：电话与登录开关页面上不存在，不参与计数。
assert.deepEqual(
  listPersonEditChanges(base, { ...base, phone: "1", loginEnabled: false }, { phoneAvailable: false, loginAvailable: false }),
  []
);

// ── 校验：没动姓名区不要求姓、名必填；动过才要求 ──
{
  const legacyOneWord = buildPersonEditBaseline({ fullName: "Staff" }).values;
  assert.equal(hasPersonEditErrors(validatePersonEdit(legacyOneWord, { ...legacyOneWord, phone: "0400" }, { requireEmail: false })), false);
  const touched = validatePersonEdit(legacyOneWord, { ...legacyOneWord, names: { ...legacyOneWord.names, middleName: "X" } }, { requireEmail: false });
  assert.equal(touched.names.familyName, "required");

  assert.equal(validatePersonEdit(base, { ...base, email: "bad" }, { requireEmail: false }).email, "invalid");
  assert.equal(validatePersonEdit(base, { ...base, email: "" }, { requireEmail: false }).email, undefined, "店员接口邮箱留空 = 沿用原邮箱");
  assert.equal(validatePersonEdit(base, { ...base, email: "" }, { requireEmail: true }).email, "required", "管理员接口邮箱必填");
  assert.equal(
    validatePersonEdit(base, { ...base, names: { ...base.names, givenName: "" } }, { requireEmail: false }).names.givenName,
    "required"
  );
}

// ── 店员接口请求体：没改姓名 → 不发四段，fullName 沿用；status 沿用最新详情；登录开关只在变化时发 ──
{
  const payload = buildStoreUserUpdatePayload({
    userGuid: "u1",
    storeCode: "S001",
    current: { username: "emily.chen@gmail.com", status: 0, fullName: "Emily Rose Chen" },
    baseline: base,
    values: { ...base, phone: "0499 999 999" },
  });
  assert.deepEqual(payload, {
    userGuid: "u1",
    storeCode: "S001",
    username: "emily.chen@gmail.com",
    fullName: "Emily Rose Chen",
    email: "emily.chen@gmail.com",
    phone: "0499 999 999",
    status: 0,
    roleNames: ["StoreStaff"],
  });
  assert.equal(payload.status, 0, "保存资料不能把停用账号重新启用");
  for (const key of ["givenName", "middleName", "familyName", "nickname", "loginEnabled"]) {
    assert.equal(key in payload, false, `${key} 未变化不发送`);
  }
}
// 改了名：只发 givenName + 合成 fullName；清空昵称发空串。
{
  const payload = buildStoreUserUpdatePayload({
    userGuid: "u1",
    storeCode: "S001",
    current: { username: "e", status: 1, fullName: "Emily Rose Chen" },
    baseline: base,
    values: { ...base, names: { ...base.names, givenName: "Emma", nickname: "" } },
  });
  assert.equal(payload.fullName, "Emma Rose Chen");
  assert.equal(payload.givenName, "Emma");
  assert.equal(payload.nickname, "", "空串 = 清空");
  assert.equal("familyName" in payload, false);
  assert.equal("middleName" in payload, false);
}
// 登录开关变化：带 loginEnabled；邮箱 / 电话清空：不带值（电话由后端按缺省清空，邮箱沿用）。
{
  const payload = buildStoreUserUpdatePayload({
    userGuid: "u1",
    storeCode: "S001",
    current: { username: "e", status: 1, fullName: "Emily Rose Chen" },
    baseline: base,
    values: { ...base, loginEnabled: false, phone: "", email: "" },
  });
  assert.equal(payload.loginEnabled, false);
  assert.equal(payload.phone, undefined);
  assert.equal(payload.email, undefined);
}
// 存量账号（只有全名）：没动姓名就原样带回全名，不把自动拆分的结果写回去。
{
  const legacy = buildPersonEditBaseline({ fullName: "Old Staff" });
  const payload = buildStoreUserUpdatePayload({
    userGuid: "u2",
    storeCode: "S001",
    current: { username: "old", status: 1, fullName: "Old Staff" },
    baseline: legacy.values,
    values: { ...legacy.values, phone: "0400" },
  });
  assert.equal(payload.fullName, "Old Staff");
  for (const key of ["givenName", "middleName", "familyName", "nickname"]) assert.equal(key in payload, false);
}

// ── 管理员接口请求体：整体覆盖语义，username / email / fullName / isActive 始终带当前值 ──
{
  const input = buildIdentityUpdateInput({
    current: { username: "emily", isActive: false, fullName: "Emily Rose Chen" },
    baseline: base,
    values: { ...base, names: { ...base.names, familyName: "Lin" }, email: " new@x.com " },
  });
  assert.deepEqual(input, {
    username: "emily",
    email: "new@x.com",
    fullName: "Emily Rose Lin",
    familyName: "Lin",
    isActive: false,
  });
}

// ── 保存后是否提示发设置密码邮件 ──
assert.equal(shouldPromptPasswordSetup({ loginWasEnabled: false, loginNow: true, canResetPassword: true }), true);
assert.equal(shouldPromptPasswordSetup({ loginWasEnabled: false, loginNow: true, canResetPassword: false }), false, "无重置密码权限不弹");
assert.equal(shouldPromptPasswordSetup({ loginWasEnabled: true, loginNow: true, canResetPassword: true }), false);
assert.equal(shouldPromptPasswordSetup({ loginWasEnabled: true, loginNow: false, canResetPassword: true }), false);

// ── 数据源 ──
assert.equal(resolvePersonEditSource({ storeCode: "S001", adminSessionAllowed: true }), "store");
assert.equal(resolvePersonEditSource({ storeCode: "", adminSessionAllowed: true }), "admin");
assert.equal(resolvePersonEditSource({ storeCode: " ", adminSessionAllowed: false }), "none");

console.log("person-edit-form.test.ts: ok");
