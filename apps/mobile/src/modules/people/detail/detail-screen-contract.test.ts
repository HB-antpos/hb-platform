import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

/**
 * 统一人员详情的源码契约：锁住「权限写法 / 数据来源 / 旧路由壳 / 弹层层级」这些
 * 无法用纯函数单测覆盖、但一旦被改坏就是线上事故的保证。
 */

const directory = dirname(fileURLToPath(import.meta.url));
const mobileRoot = resolve(directory, "../../../..");
const read = (relative: string) => readFileSync(resolve(mobileRoot, relative), "utf8");

const rules = read("src/modules/people/detail/detail-rules.ts");
const storeDetail = read("src/modules/people/detail/store-person-detail.tsx");
const adminDetail = read("src/modules/people/detail/admin-person-detail.tsx");
const screen = read("src/modules/people/detail/person-detail-screen.tsx");
const sheet = read("src/modules/people/detail/people-sheet.tsx");
const scaffold = read("src/modules/people/detail/detail-scaffold.tsx");
const passwordDialog = read("src/modules/users/staff-password-setup-dialog.tsx");

// ───────── 权限写法：全局码 OR 店员码（客户端不认别名，不能收敛成单个码）─────────
assert.match(
  rules,
  /const canEditUsers = actor\.isAdmin\s*\|\| actor\.hasPermission\(PERMISSIONS\.Users\.Edit\)\s*\|\| actor\.hasPermission\(PERMISSIONS\.Users\.EditStoreStaff\)/,
  "编辑 / 停用必须是 Users.Edit 或 Users.EditStoreStaff",
);
assert.match(
  rules,
  /const canResetPasswords = actor\.isAdmin\s*\|\| actor\.hasPermission\(PERMISSIONS\.Users\.ResetPassword\)\s*\|\| actor\.hasPermission\(PERMISSIONS\.Users\.ResetStoreStaffPassword\)/,
  "重置密码必须是 Users.ResetPassword 或 Users.ResetStoreStaffPassword",
);
// 管理员版走全局接口，只认全局码。
assert.match(rules, /actor\.hasPermission\(PERMISSIONS\.Users\.Edit\) && adminScopeOk/, "管理员版编辑只认 Users.Edit");
assert.match(rules, /actor\.hasPermission\(PERMISSIONS\.Users\.ResetPassword\)\s*&& adminScopeOk/, "直接设置新密码只认 Users.ResetPassword");
// 授权资格、个人码资格复用既有函数，不另起一套。
assert.match(rules, /getUserAccessEligibility\(\{/, "授权资格必须复用 getUserAccessEligibility");
assert.match(rules, /canManageStaffBarcode\(\{/, "个人码资格必须复用 canManageStaffBarcode");
// 本人 / 受保护目标
assert.match(rules, /const canToggleStatus = canEdit && !isSelf/, "本人不显示停用");
assert.match(rules, /const canOpenAccess = !isSelf && access\.canOpen/, "本人不显示授权");
assert.match(rules, /const isProtectedTarget = !actor\.isAdmin && hasPrivilegedAccessRole\(targetRoles\)/, "受保护目标对非管理员只读");

// ───────── 数据来源：页面自己按 userGuid 拉目标；路由参数只做首屏占位 ─────────
assert.match(storeDetail, /useStoreUserProfile\(userGuid, storeCode\)/, "店长版必须按 userGuid + storeCode 自己拉取目标");
assert.match(adminDetail, /fetchIdentityUserDetail\(userGuid, actorKey, signal\)/, "管理员版必须按 userGuid 自己拉取目标");
for (const [name, source] of [["店长版", storeDetail], ["管理员版", adminDetail]] as const) {
  const usages = source.match(/prefill\b[^\n]*/g) ?? [];
  const forbidden = usages.filter((line) => /prefill\.(roleNames|active)/.test(line));
  assert.deepEqual(forbidden, [], `${name}不得用路由预填的角色 / 状态做任何判断`);
  assert.match(source, /buildPrefillTitle\(prefill\)/, `${name}预填只能用于首屏标题`);
  assert.match(source, /target: \{ userGuid: person\.userGuid, active: person\.active, roleNames: person\.roleNames \}/, `${name}权限判断必须用拉回来的目标数据`);
}

// ───────── 管理员接口门禁 ─────────
assert.doesNotMatch(storeDetail, /identity-admin/, "店长版（含设备模式 / iOS 审核）不得引用管理员接口模块");
assert.match(screen, /resolveDetailVariant\(\{/, "版本由 resolveDetailVariant 统一决定");
assert.match(screen, /isIosReview: sessionKind === "iosReview"/, "iOS 审核模式必须回落到店长版");
assert.match(screen, /isDeviceMode,\s*\n\s*isIosReview/, "设备模式必须回落到店长版");
assert.match(adminDetail, /isCurrentSession\("Users\.ResetPassword"\)/, "直接设置密码前必须核对会话仍是发起账号且仍有权限");
assert.match(adminDetail, /isCurrentSession\("Users\.Edit"\)/, "停用 / 启用前必须核对会话");
assert.match(adminDetail, /updateIdentityUserStatus\(userGuid, intent === "enable", actorKey\)/, "管理员版停用 / 启用走状态接口，不碰其他资料字段");

// ───────── 路由与跨块契约 ─────────
assert.match(storeDetail, /buildEditRoute\(\{ userGuid, storeCode \}\)/, "编辑入口跳 /users/[userGuid]/edit（带 storeCode）");
assert.match(storeDetail, /buildPosRoute\(\{ \.\.\.routeTarget, storeCode \}/, "收银权限必须带 storeCode");
assert.match(adminDetail, /buildLoginRecordsRoute\(userGuid\)/, "管理员版登录记录进 /users/[userGuid]/login-records");

const detailRoute = read("app/(shell)/users/[userGuid]/index.tsx");
assert.match(detailRoute, /@\/modules\/people\/detail\/person-detail-screen/, "新详情路由渲染 PersonDetailScreen");
assert.match(read("app/(shell)/users/[userGuid]/login-records.tsx"), /identity-admin\/user-login-records-screen/, "新登录记录路由渲染现有登录记录屏");
assert.match(read("app/(shell)/staff/[userGuid].tsx"), /LegacyDetailRedirect/, "旧 staff/[userGuid] 只是重定向壳");
assert.match(read("app/(shell)/user-admin/[userGuid]/index.tsx"), /LegacyDetailRedirect/, "旧 user-admin/[userGuid] 只是重定向壳");
assert.match(read("app/(shell)/user-admin/[userGuid]/login-records.tsx"), /LegacyLoginRecordsRedirect/, "旧 user-admin 登录记录只是重定向壳");
assert.doesNotMatch(read("app/(shell)/staff/[userGuid].tsx"), /useStoreUserProfile|StaffPasswordSetupDialog/, "重定向壳里不得残留业务逻辑");

// ───────── 排班与考勤：页内切换，处理 Android 返回键，不新增路由 ─────────
assert.match(storeDetail, /BackHandler\.addEventListener\("hardwareBackPress"/, "子视图必须接管 Android 返回键");
assert.match(storeDetail, /gestureEnabled: view === "main"/, "子视图期间关闭 iOS 侧滑返回，避免整页退出");
assert.match(storeDetail, /<ScheduleRecordsView/, "排班 / 考勤完整视图在页内渲染");
assert.match(read("src/modules/people/detail/schedule-records-view.tsx"), /WeeklyScheduleTable/, "排班完整视图复用 WeeklyScheduleTable");

// ───────── 弹层层级：全部走 Paper Portal，Snackbar 不被底部弹层盖住 ─────────
for (const [name, source] of [["店长版", storeDetail], ["管理员版", adminDetail]] as const) {
  assert.doesNotMatch(source, /import\s*\{[^}]*\bAlert\b[^}]*\}\s*from "react-native"/, `${name}不得再用原生 Alert 做确认`);
  assert.match(source, /<ConfirmDialog/, `${name}停用 / 启用必须用统一的居中确认框`);
  assert.match(source, /danger=\{statusIntent === "disable"\}/, `${name}停用是危险操作（红色实心），启用是常规确认`);
}
assert.doesNotMatch(sheet, /import[^;]*BusinessSheet/,"详情弹层不得用原生 Modal 的 BusinessSheet（会压住 Portal、iOS 连续开关会被吞）");
assert.match(sheet, /<Portal>\s*<Modal/, "底部弹层必须是 Paper Portal + Modal");
assert.match(scaffold, /snackbarMessage \? \(\s*<Portal>/, "Snackbar 仅在有消息时挂到 Portal，叠在已打开的弹层之上");
assert.match(storeDetail, /errorMessage=\{passwordError\}/, "重置密码失败原因必须就地显示在弹层里");
assert.match(adminDetail, /errorMessage=\{passwordError\}/, "管理员版重置密码失败原因必须就地显示在弹层里");

// ───────── 重置密码弹层 ─────────
assert.match(passwordDialog, /export function StaffPasswordSetupDialog\(/, "导出名不变，P3 编辑页继续调用");
assert.match(passwordDialog, /modes = \{ email: true, direct: false \}/, "默认只有邮件方式（店长版）");
assert.match(passwordDialog, /modes\.direct && onSubmitDirectPassword/, "直接设置新密码必须同时有开关与处理函数才出现");
assert.match(passwordDialog, /validatePasswordValue\(draftPassword, t\)/, "直接设置新密码沿用 ≥ 6 位校验");
assert.match(passwordDialog, /validateStaffRecoveryEmail\(draftEmail, t\)/, "邮箱缺失时就地补填并沿用邮箱校验");
assert.match(passwordDialog, /if \(!props\.visible\) return null/, "关闭即卸载：每次打开都从空白开始");

// ───────── 从旧列表页迁入的保证（列表页的同类断言已改成 doesNotMatch，由这里接管）─────────
for (const [label, detail] of [["店长版", storeDetail], ["管理员版", adminDetail]] as const) {
  assert.match(detail, /setupEmailMutation\.mutateAsync\(/, `${label}重置密码必须改为发送设置密码邮件，不手动设新密码`);
  assert.match(
    detail,
    /console\.warn\("\[people-detail\] password setup email failed", toSafeStoreUserErrorLog\(error\)\)/,
    `${label}发送设置密码邮件失败只能记录脱敏后的错误元数据`,
  );
}
assert.match(
  storeDetail,
  /console\.warn\("\[people-detail\] status failed", toSafeStoreUserErrorLog\(error\)\)/,
  "店长版停用 / 启用失败只能记录脱敏后的错误元数据",
);
assert.match(storeDetail, /isStoreManageable\(storeCode, env\.manageableStores\)/, "店长版详情的操作必须受「可管理分店」范围限制");

// ───────── 管理员版「允许登录」开关（PUT /Users/guid/{guid}/login-enabled）─────────
const userHooks = read("src/modules/identity-admin/user-hooks.ts");
const identityApi = read("src/modules/identity-admin/api.ts");
const personHeader = read("src/modules/people/detail/person-header.tsx");
const personListItem = read("src/modules/people/person-list-item.ts");

// 只在管理员版出现：店长版（含设备模式 / iOS 审核回落）不得有开关、写接口或相关状态。
assert.doesNotMatch(
  storeDetail,
  /SwitchRow|setIdentityUserLoginEnabled|useSetIdentityUserLoginEnabled|resolveLoginToggle|loginToggle|person-login-/,
  "店长版没有管理员版的「允许登录」开关",
);
assert.match(adminDetail, /resolveLoginToggle\(permissions, \{ loginEnabled: person\?\.loginEnabled, isDeviceMode: env\.isDeviceMode \}\)/, "行的形态由 detail-rules 统一裁决");
assert.match(adminDetail, /loginToggle === "editable" \|\| loginToggle === "selfLocked" \? \(\s*<SwitchRow/, "可操作与本人置灰才渲染开关");
assert.match(adminDetail, /disabled=\{loginToggle === "selfLocked" \|\| loginMutation\.isPending\}/, "本人置灰、提交中置灰");
assert.match(adminDetail, /loginToggle === "readonly" \? \(\s*<KvRow/, "没有权限只读显示，不渲染可点的开关");

// 判断复用管理员版 canEdit（Users.Edit && adminScopeOk），不另写一套更宽松的权限判断。
{
  const toggleRule = rules.slice(rules.indexOf("export function resolveLoginToggle"), rules.indexOf("/** 拨动开关后的处理"));
  assert.ok(toggleRule.length > 0, "找到 resolveLoginToggle");
  assert.match(toggleRule, /permissions\.canEdit \? "editable" : "readonly"/, "可操作 = 管理员版 canEdit");
  assert.match(toggleRule, /if \(permissions\.variant !== "admin" \|\| facts\.isDeviceMode\) return "hidden"/, "非管理员版 / 设备模式不出现");
  assert.match(toggleRule, /if \(facts\.loginEnabled === undefined\) return "hidden"/, "接口没返回 loginEnabled 不出现");
  assert.doesNotMatch(toggleRule, /hasPermission|isAdmin/, "开关规则不得自带权限判断");
}

// 写接口：页面走 mutation hook，hook 走 setIdentityUserLoginEnabled，且绑定发起操作的账号。
assert.match(adminDetail, /useSetIdentityUserLoginEnabled\(actorKey\)/, "页面使用 mutation hook");
assert.match(adminDetail, /loginMutation\.mutateAsync\(\{ userGuid, loginEnabled: next \}\)/, "提交的是目标 userGuid 与目标值");
assert.match(userHooks, /setIdentityUserLoginEnabled\(userGuid, loginEnabled, actorKey\)/, "hook 调用 setIdentityUserLoginEnabled，并带操作者");
assert.match(userHooks, /\["identity-admin", actorKey, "user", userGuid\]/, "成功后刷新该用户详情");
assert.match(userHooks, /\["identity-admin", actorKey, "users"\]/, "成功后刷新管理员用户列表");
assert.match(userHooks, /\["storeUsers"\]/, "员工侧列表同步刷新");
assert.match(identityApi, /path\("Users", userGuid, "login-enabled"\),\s*\{ loginEnabled \},\s*accountBoundRequestConfig\(actorGuid\)/, "PUT /Users/guid/{guid}/login-enabled，请求体 { loginEnabled }，绑定账号");
assert.match(adminDetail, /if \(loginToggle !== "editable" \|\| !isCurrentSession\("Users\.Edit"\)\)/, "提交前核对界面形态与会话权限");

// 关闭必须先弹居中确认框（红色危险按钮），开启直接提交。
assert.match(adminDetail, /resolveLoginSwitchIntent\(next\) === "confirmDisable"\) setLoginIntent\("disable"\)/, "关闭先进入确认");
assert.match(adminDetail, /else void submitLoginEnabled\(true\)/, "开启直接提交");
assert.match(
  adminDetail,
  /loginIntent === "disable" \? \(\s*<ConfirmDialog[\s\S]*?danger\s+pending=\{loginMutation\.isPending\}[\s\S]*?onConfirm=\{\(\) => void submitLoginEnabled\(false\)\}[\s\S]*?testID="person-login-confirm"/,
  "关闭登录用统一的居中确认框：危险（红色）、确认才提交",
);

// 失败：日志只记脱敏后的错误元数据；提示只用我们自己的文案，不显示服务端原文。
{
  const submit = adminDetail.slice(adminDetail.indexOf("const submitLoginEnabled"), adminDetail.indexOf("const handleLoginSwitch"));
  assert.ok(submit.length > 0, "找到 submitLoginEnabled");
  assert.match(
    submit,
    /const meta = getIdentityAdminErrorMeta\(error\);(?:\s*\/\/[^\n]*)*\s*console\.warn\("\[people-detail\] login enabled failed", \{ status: meta\.status, code: meta\.code \}\)/,
    "失败只能记录状态码与错误码，不记录原始错误对象，也不记录可能是服务端原文的 message",
  );
  assert.doesNotMatch(submit, /console\.\w+\([^)]*\b(error|userGuid|person)\b[^)]*\)/, "日志不带原始错误、账号 GUID 或人员资料");
  assert.match(submit, /classifyLoginToggleFailure\(\{ code: meta\.code, status: meta\.status, uncertain: isUncertainIdentityWrite\(error\) \}\)/, "失败按 errorCode 归类");
  assert.match(submit, /t\(`peopleDetail:loginToggle\.errors\.\$\{failure\}`\)/, "提示用固定的中英文文案");
  assert.doesNotMatch(submit, /resolveLocalizedErrorMessage|error\.message|meta\.message|getMessage/, "不得把服务端原文显示给用户");
  assert.match(submit, /setLoginIntent\(null\);\s*if \(!isCurrentSession\("Users\.Edit"\)\) return;\s*const failure/, "先关确认框再提示，Snackbar 不被对话框盖住");
}

// 开启成功且从没登录过：卡内提示 + 「设置密码」按钮打开页面已有的重置密码弹层。
assert.match(adminDetail, /setPasswordHint\(next && shouldHintSetPassword\(\{ loginEnabled: true, lastLoginAt: person\?\.lastLoginAt \}\)\)/, "只在开启成功且没登录过时提示");
assert.match(adminDetail, /passwordHint && shouldHintSetPassword\(\{ loginEnabled: person\.loginEnabled, lastLoginAt: person\.lastLoginAt \}\)/, "提示随真实状态收起");
assert.match(adminDetail, /permissions\.canResetPassword \? \(\s*<Button[\s\S]*?onPress=\{openPasswordSetup\}[\s\S]*?testID="person-login-set-password"/, "「设置密码」按钮仅在有重置密码权限时出现，打开已有的重置密码弹层");
assert.match(adminDetail, /const openPasswordSetup = useCallback\(\(\) => \{\s*setPasswordError\(null\);\s*setPasswordVisible\(true\);/, "与「重置密码」行共用同一个弹层入口");

// 头部「不能登录」标签：amber + lock-outline（与红色「已停用」区分）；只在 loginEnabled === false 时出现。
assert.match(personHeader, /loginDisabled: "amber"/, "不能登录标签是 amber");
assert.match(personHeader, /loginDisabled: "lock-outline"/, "不能登录标签带锁图标");
assert.match(rules, /if \(input\.loginEnabled === false\) tags\.push\("loginDisabled"\)/, "只有 loginEnabled === false 才标");

// 管理员列表：只给例外（=== false），店员与管理员两处口径一致，未知（undefined）不标。
assert.equal((personListItem.match(/user\.loginEnabled === false\) tags\.push\("cannotLogin"\)/g) ?? []).length, 2, "店员与管理员列表都只在 loginEnabled === false 时标「不能登录」");

console.log("detail-screen-contract.test.ts: ok");
