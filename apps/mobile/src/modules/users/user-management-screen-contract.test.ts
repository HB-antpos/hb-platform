import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const currentDirectory = dirname(fileURLToPath(import.meta.url));
const source = readFileSync(
  resolve(currentDirectory, "../../../app/(shell)/users/index.tsx"),
  "utf8"
);
const createSource = readFileSync(
  resolve(currentDirectory, "../../../app/(shell)/users/new.tsx"),
  "utf8"
);

// 店长只持有本店店员专用权限；全局用户权限同样放行，两者任一即可。
for (const screenSource of [source, createSource]) {
  assert.match(
    screenSource,
    /const canCreateUsers = access\.isAdmin\s*\|\| access\.hasPermission\(PERMISSIONS\.Users\.Create\)\s*\|\| access\.hasPermission\(PERMISSIONS\.Users\.CreateStoreStaff\)/,
    "新增店员必须使用 Users.Create 或 Users.CreateStoreStaff 权限门槛"
  );
  assert.match(
    screenSource,
    /const canEditUsers = access\.isAdmin\s*\|\| access\.hasPermission\(PERMISSIONS\.Users\.Edit\)\s*\|\| access\.hasPermission\(PERMISSIONS\.Users\.EditStoreStaff\)/,
    "编辑店员必须使用 Users.Edit 或 Users.EditStoreStaff 权限门槛"
  );
}
assert.match(
  source,
  /const canResetPasswords = access\.isAdmin\s*\|\| access\.hasPermission\(PERMISSIONS\.Users\.ResetPassword\)\s*\|\| access\.hasPermission\(PERMISSIONS\.Users\.ResetStoreStaffPassword\)/,
  "重置店员密码必须使用 Users.ResetPassword 或 Users.ResetStoreStaffPassword 权限门槛"
);
assert.match(
  source,
  /const selectedStoreCanManageUsers =\s*\(canCreateUsers \|\| canEditUsers \|\| canResetPasswords\) &&/,
  "只具备创建或重置权限时，可管理分店不应显示为只读"
);
assert.match(
  source,
  /const canResetUserPassword = useCallback/,
  "密码重置必须使用独立的分店权限判断"
);
assert.match(
  source,
  /disabled=\{!moreUser \|\| !canResetUserPassword\(moreUser\)\}/,
  "密码重置按钮必须按 Users.ResetPassword 独立禁用"
);
assert.match(createSource, /createMutation/, "新建页必须接入创建 mutation");
assert.match(source, /onPress=\{openCreatePage\}/, "标题区必须提供新增店员入口");
assert.match(source, /pathname: "\/users\/new"/, "新增入口必须进入整页新建员工");
assert.match(
  source,
  /disabled=\{!selectedStoreCanCreate \|\| isBusy\}/,
  "未选择可管理分店时必须禁用新增入口"
);
assert.match(source, /t\("actions\.create"\)/, "新增入口必须使用本地化文案");
assert.match(createSource, /t\("dialogs\.createTitle"\)/, "新建页必须使用创建标题");
assert.doesNotMatch(createSource, /password:/, "新建页不得提交任何密码，员工用邮件验证码自己设置");
assert.match(createSource, /sendPasswordSetupEmail: true/, "新建后必须给员工邮箱发设置密码验证码");
assert.match(createSource, /validateNewStaffEmail\(email, t\)/, "新建时邮箱必填");
assert.match(createSource, /resolveUsernameFromEmail\(value\)/, "用户名默认跟随邮箱");
assert.match(createSource, /createMutation\.mutateAsync/, "保存创建表单必须调用创建接口");
assert.match(createSource, /t\("create\.submit"\)/, "新建页主按钮必须显示创建文案");
assert.match(createSource, /t\("create\.successHeadline"\)/, "创建成功必须给出明确反馈");
assert.match(createSource, /t\("create\.inviteSent"/, "创建成功页必须显示设置密码邮件的发送状态");
assert.match(createSource, /resendInvite/, "邮件没发出时店长必须能重发");
assert.match(source, /setupEmailMutation\.mutateAsync/, "店长重置密码必须改为发送设置密码邮件");
assert.match(createSource, /validateNewStaffPhone\(phone, t\)/, "新建时必须校验澳洲手机号");
assert.doesNotMatch(source, /createMutation/, "列表页不再承载创建表单");
for (const screenSource of [source, createSource]) {
  assert.match(
    screenSource,
    /console\.warn\("\[store-users\] save failed", toSafeStoreUserErrorLog\(error\)\)/,
    "创建或编辑失败只能记录脱敏后的错误元数据"
  );
}
assert.match(
  source,
  /console\.warn\("\[store-users\] password reset failed", toSafeStoreUserErrorLog\(error\)\)/,
  "密码重置失败只能记录脱敏后的错误元数据"
);
assert.doesNotMatch(
  source,
  /console\.warn\("\[store-users\] (?:save|password reset) failed", error\)/,
  "含密码请求失败时不得直接记录 Axios 错误对象"
);
assert.match(source, /testID="compact-staff-row"/, "员工列表必须使用紧凑行布局");
assert.doesNotMatch(source, /lastLoginIpValue/, "低频登录 IP 不应继续占用员工列表首屏");
assert.match(source, /statusAllCount/, "状态筛选必须显示计数");
assert.match(source, /statusPendingFirstLoginCount/, "状态筛选必须包含待首次登录");
assert.match(source, /StaffBarcodeDialog/, "员工行必须接入单人个人码弹层");
assert.match(source, /StaffBarcodeBatchDialog/, "可管理的明确分店必须接入批量打印弹层");
assert.match(source, /!isDeviceMode && canEditUsers && managedStoreCode/, "纯设备模式或无编辑权限不得显示批量写入口");
assert.match(source, /canManageStaffBarcode\(/, "个人码入口必须复用统一资格判断，停用员工不得获得写入口");
assert.match(source, /setSelectedUserGuids\(new Set\(\)\)[\s\S]{0,120}setBatchSelecting\(false\)/, "换店必须清空批量选择");

const barcodeDialogSource = readFileSync(
  resolve(currentDirectory, "staff-barcode/StaffBarcodeDialogs.tsx"),
  "utf8"
);
assert.match(barcodeDialogSource, /\["staffCashierBarcode", actorGuid, storeCode, userGuid, generation\]/, "个人码缓存必须按操作者、分店、目标和打开轮次隔离");
assert.match(barcodeDialogSource, /!pendingLoaded \|\| restoreFailed/, "安全记录恢复完成前或失败后不得打印");
assert.match(barcodeDialogSource, /if \(!printed\) throw new Error\("STAFF_BARCODE_PRINT_NOT_ACCEPTED"\)/, "打印机未接受任务不得进入确认流程");
assert.match(barcodeDialogSource, /barcodeQuery\.isError \? undefined : barcodeQuery\.data/, "接口失败时不得显示旧缓存个人码");
assert.match(barcodeDialogSource, /while \(isCurrentSession\(sessionRef, generation\)\)/, "批量打印必须串行且持续校验当前作用域");
assert.match(barcodeDialogSource, /invalidateStaffBarcodeOperationSession/, "组件卸载必须使旧打印session失效");
assert.match(barcodeDialogSource, /runStaffBarcodeActionExclusive\(actionGateRef\.current/, "点击动作必须用同步CAS覆盖完整异步链");
assert.match(barcodeDialogSource, /hasIncompleteStaffBarcodeConfirmation\(batch\)/, "结束批量时不得清理待确认attempt");
assert.match(barcodeDialogSource, /hydrateSavedPrinter\(\)/, "个人码弹层打开时必须恢复已保存打印机配置");
assert.match(barcodeDialogSource, /router\.navigate\("\/\(shell\)\/settings"\)/, "个人码弹层必须提供打印机设置入口");
assert.match(barcodeDialogSource, /pendingConfirmation\?\.phase === "printed"/, "已出纸待确认必须显示独立恢复状态");
assert.match(barcodeDialogSource, /fontScale > 1\.2 \|\| width < 380/, "窄屏或大字号时弹层操作必须改为纵向布局");

console.log("user-management-screen-contract.test.ts: ok");
