import assert from "node:assert/strict";
import { readFileSync, readdirSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

// 新增 / 编辑整页的源码契约：页面本身依赖 React Native 无法在 Node 里渲染，
// 这里用源码断言锁住重要的业务保证（权限写法、不提交密码、不改启停状态、两步创建的失败处理等）。
const here = dirname(fileURLToPath(import.meta.url));
const mobileRoot = resolve(here, "../../../..");
const read = (relative: string) => readFileSync(resolve(mobileRoot, relative), "utf8");

const newRoute = read("app/(shell)/users/new.tsx");
const editRoute = read("app/(shell)/users/[userGuid]/edit.tsx");
const staffScreen = read("src/modules/people/form/staff-create-screen.tsx");
const staffForm = read("src/modules/people/form/staff-create-form.ts");
const adminScreen = read("src/modules/people/form/admin-create-screen.tsx");
const adminForm = read("src/modules/people/form/admin-create-form.ts");
const editScreen = read("src/modules/people/form/person-edit-screen.tsx");
const editForm = read("src/modules/people/form/person-edit-form.ts");
const formUi = read("src/modules/people/form/form-ui.tsx");
const newStaffProfile = read("src/modules/people/form/new-staff-profile.ts");
const newStaffFields = read("src/modules/people/form/new-staff-fields.tsx");

// ── 路由入口 ──
assert.match(newRoute, /resolveCreateVariant\(/, "新增页必须按 storeCode 与身份在店长版 / 管理员版之间切换");
assert.match(newRoute, /StaffCreateScreen/, "新增页必须有店长版");
assert.match(newRoute, /AdminCreateScreen/, "新增页必须有管理员版");
assert.match(newRoute, /useIdentitySession\("Users\.Create"\)/, "管理员版只在 account 会话且有 Users.Create 时才可用");
assert.match(editRoute, /@\/modules\/people\/form\/person-edit-screen/, "编辑路由必须指向整页编辑屏幕");

// ── 店长版新增：固定本店店员，绝不提交密码 ──
assert.match(staffForm, /roleNames: \[STORE_STAFF_ROLE\]/, "店长版新增固定为店员角色");
assert.match(staffForm, /employmentType: "casual"/, "店长版新增固定为临时工");
assert.doesNotMatch(staffForm + staffScreen, /password:/, "店长版新增不得提交任何密码");
assert.match(staffScreen, /isStoreManageable\(requestedStoreCode, manageableStores\)/, "必须是可管理分店才能新增");
assert.match(staffScreen, /useLeaveGuard\(/, "已填写内容时返回要先确认「放弃修改？」");
assert.match(staffScreen, /canManageStaffBarcode\(/, "打印个人码必须复用统一资格判断");
assert.match(staffScreen, /queryKey: \["identity-admin"\]/, "建号后管理员侧缓存也要失效");
assert.match(staffScreen, /<StaffBarcodeDialog/, "创建完成页必须能打开个人码弹层");

// ── 管理员版新增：account 会话 + 管理员，分两步且第二步失败不重复创建 ──
assert.match(
  adminScreen,
  /const canCreate = allowed && access\.isAdmin && access\.hasPermission\("Users\.Create"\)/,
  "管理员版必须是 account 会话、管理员且有 Users.Create"
);
assert.match(adminScreen, /state\.sessionKind === "account"/, "异步结果落地前必须复核仍是同一 account 会话");
assert.match(adminScreen, /runAdminCreate\(/, "管理员版必须走两步创建流程");
assert.match(adminScreen, /retryAdminStoreAssignment\(/, "第二步失败只能重试第二步");
assert.match(adminScreen, /kind: "uncertain"/, "建号结果未知时必须冻结表单");
assert.match(adminScreen, /isUncertainIdentityWrite/, "必须区分明确失败与结果未知");
assert.doesNotMatch(adminScreen, /console\.warn\([^)]*,\s*(?:error|outcome\.error|result\.error)\)/, "请求带密码：不得把 Axios 错误对象写进日志");
assert.match(adminForm, /validateNewStaffProfile\(values, values\.email, today\)/, "管理员版新增：电话、生日、未成年监护人必填");
assert.match(adminForm, /buildNewStaffProfileBody\(values, today\)/, "管理员版新增必须带电话、生日（未成年带监护人）");

// ── 新建员工必填规则（设计图 7、9）：两个新增页共用同一套规则与组件 ──
assert.match(staffForm, /validateNewStaffProfile\(values, values\.email, today\)/, "店长版新增：电话、生日、未成年监护人必填");
assert.match(staffForm, /buildNewStaffProfileBody\(values, today\)/, "店长版新增必须带电话、生日（未成年带监护人）");
assert.match(newStaffProfile, /validateNewStaffPhone\(phone, /, "电话沿用现有的澳洲手机号校验");
assert.match(newStaffProfile, /todayInSydney\(/, "年龄必须按悉尼当天算");
assert.match(newStaffProfile, /if \(isNewStaffMinor\(values\.birthday, today\)\) \{\s*body\.guardian/, "只有未成年才提交监护人");
assert.match(newStaffFields, /BirthdayPickerDialog/, "生日复用员工资料的纯 JS 日期弹窗，不新增原生依赖");
for (const [name, source] of [["staff", staffScreen], ["admin", adminScreen]] as const) {
  assert.match(source, /<NewStaffPhoneField/, `${name}：电话用共享组件`);
  assert.match(source, /<NewStaffBirthdayField/, `${name}：生日用共享组件`);
  assert.match(source, /\{minor \? \(\s*<View onLayout=\{anchor\("guardian"\)\}>\s*<NewStaffGuardianSection/, `${name}：监护人一节只在未成年时出现`);
  assert.match(source, /resolveNewStaffServerErrorKey\(readApiErrorCode\(/, `${name}：服务端必填规则错误要按错误码处理`);
  assert.match(source, /scrollToFirst\(/, `${name}：校验失败滚到第一个错误`);
}
assert.match(adminForm, /passwordFormat: "raw"/, "直接设置的初始密码按 raw 提交");

// ── 编辑整页：不改启停状态，保存后按需提示发设置密码邮件，未保存改动先确认 ──
assert.match(editForm, /status: input\.current\.status/, "店员接口更新必须始终带回当前状态，不能把停用账号重新启用");
assert.match(editForm, /isActive: input\.current\.isActive/, "管理员接口更新必须始终带回当前启用状态");
assert.doesNotMatch(editScreen, /userManagement:fields\.enabled/, "编辑页不再有「启用状态」开关");
assert.match(editScreen, /shouldPromptPasswordSetup\(/, "从不能登录改为能登录后必须提示发设置密码邮件");
assert.match(editScreen, /<StaffPasswordSetupDialog/, "提示发送设置密码邮件必须复用现有弹窗");
assert.match(editScreen, /canResetUserPassword/, "无重置密码权限不弹设置密码邮件");
assert.match(editScreen, /useLeaveGuard\(dirty\)/, "有未保存改动时返回先确认");
assert.match(editScreen, /isSelf/, "本人行不提供编辑");
assert.match(editScreen, /canModifyIdentityUser\(/, "管理员接口编辑必须复核目标是否可修改");
assert.match(formUi, /usePreventRemove\(/, "离开拦截必须覆盖返回键与侧滑");
assert.match(formUi, /<Dialog visible=\{visible\} onDismiss=\{onKeepEditing\}>/, "放弃修改确认是居中对话框");

// ── i18n：t("...") 引用的键必须在 zh 文案里存在（tsc 不检查文案键）──
const bundles: Record<string, unknown> = {
  peopleForm: JSON.parse(read("src/locales/zh/screens/peopleForm.json")),
  userManagement: JSON.parse(read("src/locales/zh/screens/userManagement.json")),
  common: JSON.parse(read("src/locales/zh/common.json")),
};
const lookup = (bundle: unknown, key: string) =>
  key.split(".").reduce<unknown>((node, part) => (node && typeof node === "object" ? (node as Record<string, unknown>)[part] : undefined), bundle);

const formDirectory = resolve(mobileRoot, "src/modules/people/form");
const formSources = readdirSync(formDirectory)
  .filter((name) => /\.tsx?$/.test(name) && !/\.test\./.test(name))
  .map((name) => ({ name, source: readFileSync(resolve(formDirectory, name), "utf8") }));
formSources.push({ name: "users/new.tsx", source: newRoute });
for (const { name, source } of formSources) {
  for (const match of source.matchAll(/\bt\(\s*(["`])([^"`]+)\1/g)) {
    const raw = match[2];
    if (raw.includes("${")) continue; // 动态键在下面单独核对
    const colon = raw.indexOf(":");
    const namespace = colon > 0 ? raw.slice(0, colon) : "peopleForm";
    const key = colon > 0 ? raw.slice(colon + 1) : raw;
    assert.notEqual(lookup(bundles[namespace], key), undefined, `${name} 引用了不存在的文案键 ${namespace}:${key}`);
  }
}
for (const key of [
  "adminCreate.storeState.unassigned",
  "adminCreate.storeState.view",
  "adminCreate.storeState.manage",
  "adminCreate.passwordErrors.required",
  "adminCreate.passwordErrors.tooShort",
  "adminCreate.passwordErrors.tooLong",
  "adminCreate.passwordErrors.mismatch",
  "edit.emailErrors.invalid",
  "edit.emailErrors.required",
  "name.familyName",
  "name.givenName",
  // 新建员工：监护人关系（提交英文值，显示本地化文案）、字段错误码、服务端错误码。
  "newStaff.relationships.Mother",
  "newStaff.relationships.Father",
  "newStaff.relationships.Legal guardian",
  "newStaff.relationships.Other",
  ...[...newStaffFields.matchAll(/"(newStaff\.errors\.[A-Za-z]+)"/g)].map((match) => match[1]),
  ...[...newStaffProfile.matchAll(/: "([a-zA-Z]+)",?\n/g)]
    .map((match) => match[1])
    .filter((key) => /^(?:birthday|phone|email|guardian)[A-Z]/.test(key))
    .map((key) => `newStaff.serverErrors.${key}`),
]) {
  assert.notEqual(lookup(bundles.peopleForm, key), undefined, `缺少动态文案键 peopleForm:${key}`);
}

console.log("form-screens-contract.test.ts: ok");
