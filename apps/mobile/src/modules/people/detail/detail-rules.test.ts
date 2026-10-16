import {
  buildDetailLayout,
  buildHeaderStatusTags,
  buildNameDisplayParts,
  classifyLoginToggleFailure,
  formatLocalDate,
  formatLocalDateTime,
  isStoreManagerRoleDerived,
  resolveDetailPermissions,
  resolveDetailVariant,
  resolveEmailStoreCode,
  resolveLoginSwitchIntent,
  resolveLoginToggle,
  resolveScheduleEntries,
  resolveStatusAction,
  selectPosStores,
  shortenGuid,
  shouldHintSetPassword,
  summarizeActiveSessions,
  summarizeRecordCount,
  summarizeStoreRelations,
  summarizeWeekSchedule,
  weekdayIndexFromDate,
  type DetailActorFacts,
  type DetailPermissionInput,
  type DetailScopeFacts,
  type DetailTargetFacts,
} from "./detail-rules";
import { isStoreManagerRoleName } from "@/modules/users/access-management";
import { PERMISSIONS } from "@/shared/utils/access";

function assertEqual(actual: unknown, expected: unknown, label: string) {
  if (JSON.stringify(actual) !== JSON.stringify(expected)) {
    throw new Error(`${label}: expected ${JSON.stringify(expected)}, got ${JSON.stringify(actual)}`);
  }
}

// ───────────── 构造操作者 / 目标 / 范围 ─────────────

const U = PERMISSIONS.Users;

function actor(kind: "admin" | "manager" | "viewer", extra: string[] = []): DetailActorFacts {
  if (kind === "admin") {
    return { userGuid: "admin-1", roleNames: ["Admin"], isAdmin: true, isStoreManager: false, isStoreLevelManager: false, hasPermission: () => true };
  }
  const granted = new Set(
    kind === "manager"
      ? [U.ViewStoreStaff, U.EditStoreStaff, U.ResetStoreStaffPassword, U.ManageStores, U.ManageRoles, U.ManagePos, ...extra]
      : [U.ViewStoreStaff, ...extra],
  );
  // viewer = 只有查看权限的普通账号（不是店长角色）；manager = 店长角色且持有本店店员管理权限。
  const isManager = kind === "manager";
  return {
    userGuid: "mgr-1",
    roleNames: [isManager ? "StoreManager" : "StoreStaff"],
    isAdmin: false,
    isStoreManager: isManager,
    isStoreLevelManager: isManager,
    hasPermission: (code) => granted.has(code),
  };
}

function target(overrides: Partial<DetailTargetFacts> = {}): DetailTargetFacts {
  return { userGuid: "staff-1", active: true, roleNames: ["StoreStaff"], ...overrides };
}

function scope(overrides: Partial<DetailScopeFacts> = {}): DetailScopeFacts {
  return {
    authenticated: true,
    isDeviceMode: false,
    storeManageable: true,
    barcodeStoreAllowed: true,
    hasManageableStores: true,
    hasStoreContext: true,
    ...overrides,
  };
}

function permissionsFor(input: Partial<DetailPermissionInput> & Pick<DetailPermissionInput, "actor">) {
  return resolveDetailPermissions({
    variant: "store",
    target: target(),
    scope: scope(),
    ...input,
  });
}

// ───────────── 变体选择 ─────────────

assertEqual(resolveDetailVariant({ identityAllowed: true, isDeviceMode: false, isIosReview: false }), "admin", "全局用户权限 + account 会话 = 管理员版");
assertEqual(resolveDetailVariant({ identityAllowed: false, isDeviceMode: false, isIosReview: false }), "store", "无全局权限 = 店长版");
assertEqual(resolveDetailVariant({ identityAllowed: true, isDeviceMode: true, isIosReview: false }), "store", "设备模式屏蔽管理员接口");
assertEqual(
  resolveDetailVariant({ identityAllowed: true, isDeviceMode: false, isIosReview: false, isStoreLevelManager: true }),
  "store",
  "店长即使持有全局用户权限，详情也只用店长版（不走全局用户接口读别的角色）",
);
assertEqual(resolveDetailVariant({ identityAllowed: true, isDeviceMode: false, isIosReview: true }), "store", "iOS 审核模式屏蔽管理员接口");

// ───────────── 店长版：有权限的店长 ─────────────

{
  const p = permissionsFor({ actor: actor("manager") });
  assertEqual(p.isSelf, false, "店长看店员不是本人");
  assertEqual(p.isProtectedTarget, false, "店员不是受保护目标");
  assertEqual(p.canEdit, true, "店长可编辑本店店员");
  assertEqual(p.canToggleStatus, true, "店长可停用本店店员");
  assertEqual(p.passwordModes, { email: true, direct: false }, "店长只有「发设置密码邮件」，没有直接设置新密码");
  assertEqual(p.canPrintBarcode, true, "店长可打印本店店员个人码");
  assertEqual(p.canOpenAccess, true, "店长可进入授权");
  assertEqual(p.canOpenPos, true, "持有 ManagePos 的店长可管理收银权限");
  assertEqual(p.canCopyPermissions, true, "店长对店员可复制权限");
  assertEqual(p.canViewLoginRecords, false, "登录记录只属于管理员版");

  const layout = buildDetailLayout(p, true);
  assertEqual(layout.quickActions.map((item) => item.key), ["edit", "password", "barcode", "status"], "店长版四个快捷操作按序出现");
  assertEqual(layout.quickActions[3], { key: "status", intent: "disable", danger: true }, "启用中的员工，状态快捷操作是红色停用");
  assertEqual(layout.headerEdit, false, "店长版头部没有 ✎（编辑在快捷操作里）");
  assertEqual(layout.moreItems, ["barcode", "access", "pos", "copyPermissions"], "更多弹层顺序：个人码 / 授权 / 收银权限 / 复制权限，不含登录记录");
  assertEqual(layout.moreStatus, "disable", "更多弹层底部红色停用");
  assertEqual(layout.showMore, true, "有可用项时显示 ⋮");
  assertEqual(layout.footerStatus, null, "店长版没有页面底部整行停用按钮");
}

// ───────────── 没权限的操作直接不显示（不是灰着） ─────────────

{
  const p = permissionsFor({ actor: actor("viewer") });
  assertEqual(p.canEdit, false, "只有查看权限：不能编辑");
  assertEqual(p.canToggleStatus, false, "只有查看权限：不能停用");
  assertEqual(p.canResetPassword, false, "只有查看权限：不能重置密码");
  assertEqual(p.canPrintBarcode, false, "只有查看权限：没有个人码");
  assertEqual(p.canOpenAccess, false, "只有查看权限：没有授权");
  const layout = buildDetailLayout(p, true);
  assertEqual(layout.quickActions, [], "没权限的快捷操作一个都不出现");
  assertEqual(layout.moreItems, [], "没权限的更多项一个都不出现");
  assertEqual(layout.moreStatus, null, "没权限没有停用项");
  assertEqual(layout.showMore, false, "弹层没有任何可用项时整个 ⋮ 隐藏");
}

{
  // 只持有重置密码权限（没有编辑权限）：只出现「设置密码」。
  const p = permissionsFor({ actor: actor("viewer", [U.ResetStoreStaffPassword]) });
  const layout = buildDetailLayout(p, true);
  assertEqual(layout.quickActions.map((item) => item.key), ["password"], "只有重置密码权限时只显示设置密码");
}

{
  // 全局码同样放行（显式 OR）：Users.Edit / Users.ResetPassword。
  const p = permissionsFor({ actor: actor("viewer", [U.Edit, U.ResetPassword]) });
  assertEqual(p.canEdit, true, "Users.Edit 同样可编辑");
  assertEqual(p.passwordModes.email, true, "Users.ResetPassword 同样可发邮件");
}

// ───────────── 本人：不提供停用、授权等操作 ─────────────

{
  const self = permissionsFor({
    actor: actor("manager"),
    target: target({ userGuid: "MGR-1", roleNames: ["StoreManager"] }),
  });
  assertEqual(self.isSelf, true, "GUID 大小写不同也识别为本人");
  assertEqual(self.canEdit, false, "店长版本人不可编辑");
  assertEqual(self.canToggleStatus, false, "本人不显示停用");
  assertEqual(self.canResetPassword, false, "店长版本人不可重置密码");
  assertEqual(self.canOpenAccess, false, "本人不显示授权");
  assertEqual(self.canOpenPos, false, "本人不显示收银权限");
  assertEqual(self.canPrintBarcode, false, "本人不显示个人码");
  const layout = buildDetailLayout(self, true);
  assertEqual(layout.quickActions, [], "本人详情没有快捷操作");
  assertEqual(layout.showMore, false, "本人详情没有更多操作");
}

// ───────────── 受保护角色 ─────────────

{
  for (const roleName of ["Admin", "StoreManager", "WarehouseManager", "店长", "仓库管理员"]) {
    const p = permissionsFor({ actor: actor("manager"), target: target({ roleNames: [roleName] }) });
    assertEqual(p.isProtectedTarget, true, `${roleName} 对店长是受保护目标`);
    assertEqual(
      [p.canEdit, p.canToggleStatus, p.canResetPassword, p.canPrintBarcode, p.canOpenAccess, p.canOpenPos, p.canCopyPermissions],
      [false, false, false, false, false, false, false],
      `${roleName}：对店长一律只读`,
    );
  }
  // 管理员操作者不受「受保护」限制（后端仍会校验）。
  const adminOnAdmin = permissionsFor({ actor: actor("admin"), target: target({ roleNames: ["Admin"] }) });
  assertEqual(adminOnAdmin.isProtectedTarget, false, "管理员操作者不受保护角色限制");
  assertEqual(adminOnAdmin.canEdit, true, "管理员可编辑其他管理员");
}

{
  // 目标信息缺失（深链、路由参数缺失）：roleNames 为空 + 目标自己拉到的数据才是依据；
  // 空角色不会被当成受保护，但页面在数据到达前根本不渲染任何操作（见页面实现），这里确认规则本身不依赖路由预填。
  const p = permissionsFor({ actor: actor("manager"), target: target({ roleNames: [] }) });
  assertEqual(p.isProtectedTarget, false, "无角色信息时按未受保护计算");
  assertEqual(p.canPrintBarcode, false, "无店员角色的目标不能打印个人码（canManageStaffBarcode 要求店员角色）");
}

// ───────────── 设备模式 ─────────────

{
  const p = permissionsFor({ actor: actor("manager"), scope: scope({ isDeviceMode: true }) });
  assertEqual(p.canEdit, true, "设备模式下按权限仍可编辑设备绑定分店的店员");
  assertEqual(p.canOpenAccess, false, "设备模式不开放账号授权");
  assertEqual(p.canOpenPos, false, "设备模式不开放收银权限");
  assertEqual(p.canCopyPermissions, false, "设备模式不开放复制权限");
  assertEqual(p.canPrintBarcode, false, "设备模式不开放个人码");
  assertEqual(p.access.reason, "deviceMode", "授权资格原因为设备模式");
}

// ───────────── 分店范围 ─────────────

{
  const p = permissionsFor({ actor: actor("manager"), scope: scope({ storeManageable: false, barcodeStoreAllowed: false }) });
  assertEqual(p.canEdit, false, "仅查看分店不能编辑");
  assertEqual(p.canResetPassword, false, "仅查看分店不能重置密码");
  assertEqual(p.canPrintBarcode, false, "仅查看分店不能打印个人码");
}

// ───────────── 已停用目标 ─────────────

{
  const p = permissionsFor({ actor: actor("manager"), target: target({ active: false }) });
  assertEqual(p.canToggleStatus, true, "已停用员工仍可重新启用");
  assertEqual(p.canPrintBarcode, false, "已停用员工不能打印个人码");
  assertEqual(p.canOpenPos, false, "已停用员工没有收银权限入口");
  const layout = buildDetailLayout(p, false);
  assertEqual(layout.quickActions.find((item) => item.key === "status"), { key: "status", intent: "enable", danger: false }, "已停用时快捷操作是常规「启用」，不是红色");
  assertEqual(layout.moreStatus, "enable", "更多弹层底部变成启用");
}

assertEqual(resolveStatusAction(true), { intent: "disable", nextActive: false, danger: true }, "启用中 → 停用（危险）");
assertEqual(resolveStatusAction(false), { intent: "enable", nextActive: true, danger: false }, "已停用 → 启用（常规）");

// ───────────── 管理员版 ─────────────

{
  const p = permissionsFor({ variant: "admin", actor: actor("admin") });
  assertEqual(p.canEdit, true, "管理员可编辑");
  assertEqual(p.passwordModes, { email: true, direct: true }, "管理员版两种重置方式都有（目标是店员且有分店）");
  assertEqual(p.canViewLoginRecords, true, "管理员版有登录记录");
  const layout = buildDetailLayout(p, true);
  assertEqual(layout.quickActions, [], "管理员版没有头部快捷操作行");
  assertEqual(layout.headerEdit, true, "管理员版头部 ✎ 编辑");
  assertEqual(layout.footerStatus, "disable", "管理员版页面底部是整行停用按钮");
  assertEqual(layout.moreItems, ["barcode", "access", "pos", "copyPermissions", "loginRecords"], "管理员版更多弹层含登录记录");
}

{
  // 目标不是店员 / 没有分店：只剩直接设置新密码。
  const notStaff = permissionsFor({ variant: "admin", actor: actor("admin"), target: target({ roleNames: ["Warehouse"] }) });
  assertEqual(notStaff.passwordModes, { email: false, direct: true }, "非店员目标没有邮件方式");
  const noStore = permissionsFor({ variant: "admin", actor: actor("admin"), scope: scope({ hasStoreContext: false }) });
  assertEqual(noStore.passwordModes, { email: false, direct: true }, "没有分店可用时没有邮件方式");
}

{
  // 管理员本人：可编辑资料 / 重置自己的密码，但不能停用、授权、打印个人码。
  const self = permissionsFor({ variant: "admin", actor: actor("admin"), target: target({ userGuid: "admin-1", roleNames: ["Admin"] }) });
  assertEqual(self.isSelf, true, "管理员本人");
  assertEqual(self.canEdit, true, "管理员本人可改资料（用户名 / 状态由编辑页与后端限制）");
  assertEqual(self.canToggleStatus, false, "管理员本人不显示停用");
  assertEqual(self.canOpenAccess, false, "管理员本人不显示授权");
  assertEqual(self.canPrintBarcode, false, "管理员本人不显示个人码");
  const layout = buildDetailLayout(self, true);
  assertEqual(layout.footerStatus, null, "管理员本人页面底部没有停用按钮");
  assertEqual(layout.moreItems, ["loginRecords"], "管理员本人更多弹层只剩登录记录");
}

{
  // 店长级管理者持有全局用户权限进入管理员版：不能动本人与更高权限账号。
  const mgr = actor("manager", [U.View, U.Edit, U.ResetPassword]);
  const onStaff = permissionsFor({ variant: "admin", actor: mgr });
  assertEqual(onStaff.canEdit, true, "店长级持有 Users.Edit 可编辑店员");
  const onSelf = permissionsFor({ variant: "admin", actor: mgr, target: target({ userGuid: "mgr-1", roleNames: ["StoreManager"] }) });
  assertEqual([onSelf.canEdit, onSelf.passwordModes.direct], [false, false], "店长级管理者不能通过全局页面改本人");
  const onAdmin = permissionsFor({ variant: "admin", actor: mgr, target: target({ roleNames: ["Admin"] }) });
  assertEqual([onAdmin.canEdit, onAdmin.passwordModes.direct, onAdmin.canToggleStatus], [false, false, false], "店长级管理者不能动管理员账号");
  // 缺少全局编辑码时，管理员版不显示编辑 / 停用（店员码只对店长版接口有效）。
  const noGlobal = permissionsFor({ variant: "admin", actor: actor("manager", [U.View]) });
  assertEqual([noGlobal.canEdit, noGlobal.canToggleStatus, noGlobal.passwordModes.direct], [false, false, false], "管理员版只认全局码");
}

// ───────────── 头部标签 ─────────────

assertEqual(buildHeaderStatusTags({ active: true }), ["active"], "正常启用只有「启用中」");
assertEqual(buildHeaderStatusTags({ active: true, mustChangePassword: true }), ["active", "pendingFirstLogin"], "待首次登录");
assertEqual(buildHeaderStatusTags({ active: false, mustChangePassword: true }), ["disabled"], "已停用不再显示待首次登录");
assertEqual(buildHeaderStatusTags({ active: true, loginEnabled: false }), ["active", "loginDisabled"], "不能登录是例外标签");
assertEqual(buildHeaderStatusTags({ active: true, loginEnabled: undefined }), ["active"], "旧后端缺省视为允许登录");

// ───────────── 管理员版「允许登录」开关 ─────────────

{
  const toggle = (
    input: Partial<DetailPermissionInput> & Pick<DetailPermissionInput, "actor">,
    loginEnabled: boolean | undefined,
    isDeviceMode = false,
  ) => resolveLoginToggle(permissionsFor({ variant: "admin", ...input }), { loginEnabled, isDeviceMode });

  // 可操作：管理员（含 Users.Edit）对他人账号，开着或已被限制都能切。
  assertEqual(toggle({ actor: actor("admin") }, true), "editable", "管理员对他人：可操作（已允许）");
  assertEqual(toggle({ actor: actor("admin") }, false), "editable", "管理员对他人：可操作（已被限制，要能解开 kevin 这类账号）");
  // 持有全局 Users.Edit 的店长级管理者对店员：与「编辑 / 停用」同一个判断，可操作（分店范围由后端 USER_SCOPE_DENIED 兜底）。
  assertEqual(toggle({ actor: actor("manager", [U.View, U.Edit]) }, false), "editable", "店长级持 Users.Edit 对店员：可操作");

  // 接口没返回 loginEnabled（旧后端 / 未查询）：未知不等于允许，整行不出现。
  assertEqual(toggle({ actor: actor("admin") }, undefined), "hidden", "loginEnabled 缺省：不显示开关");

  // 没有 Users.Edit：只读显示当前值，不是可点的开关（店员码 EditStoreStaff 只对店长版接口有效，不放行）。
  assertEqual(toggle({ actor: actor("manager") }, true), "readonly", "只有店员编辑码：只读");
  assertEqual(toggle({ actor: actor("viewer", [U.View]) }, false), "readonly", "只有查看权限：只读");
  // 受保护目标：店长级管理者不能动管理员账号，只读。
  assertEqual(toggle({ actor: actor("manager", [U.View, U.Edit]), target: target({ roleNames: ["Admin"] }) }, true), "readonly", "店长级对管理员账号：只读");

  // 本人：开关置灰并说明（后端 SELF_PROFILE_FIELDS_DENIED），不管有没有编辑权限。
  assertEqual(toggle({ actor: actor("admin"), target: target({ userGuid: "admin-1", roleNames: ["Admin"] }) }, true), "selfLocked", "管理员本人：置灰");
  assertEqual(toggle({ actor: actor("manager", [U.View, U.Edit]), target: target({ userGuid: "mgr-1", roleNames: ["StoreManager"] }) }, true), "selfLocked", "店长级本人：置灰");

  // 店长版不出现（店长版走 store-users 接口，没有这个写接口）。
  assertEqual(
    resolveLoginToggle(permissionsFor({ variant: "store", actor: actor("manager") }), { loginEnabled: false, isDeviceMode: false }),
    "hidden",
    "店长版：不出现",
  );
  // 设备模式：即使误传了管理员版权限也不出现。
  assertEqual(toggle({ actor: actor("admin") }, false, true), "hidden", "设备模式：不出现");
  // 设备模式 / iOS 审核模式在 resolveDetailVariant 里回落到店长版，于是同样不出现。
  for (const env of [{ isDeviceMode: true, isIosReview: false }, { isDeviceMode: false, isIosReview: true }]) {
    const variant = resolveDetailVariant({ identityAllowed: true, ...env });
    assertEqual(
      resolveLoginToggle(permissionsFor({ variant, actor: actor("admin") }), { loginEnabled: false, isDeviceMode: env.isDeviceMode }),
      "hidden",
      `环境 ${JSON.stringify(env)} 下不出现`,
    );
  }
}

assertEqual(resolveLoginSwitchIntent(false), "confirmDisable", "关闭先确认");
assertEqual(resolveLoginSwitchIntent(true), "enableNow", "开启直接提交");

assertEqual(shouldHintSetPassword({ loginEnabled: true, lastLoginAt: undefined }), true, "从没登录过：提示设置密码");
assertEqual(shouldHintSetPassword({ loginEnabled: true, lastLoginAt: "  " }), true, "空白登录时间同样视为没登录过");
assertEqual(shouldHintSetPassword({ loginEnabled: true, lastLoginAt: "2026-10-01T00:00:00Z" }), false, "登录过的账号不提示");
assertEqual(shouldHintSetPassword({ loginEnabled: false, lastLoginAt: undefined }), false, "仍是限制状态不提示");
assertEqual(shouldHintSetPassword({ loginEnabled: undefined, lastLoginAt: undefined }), false, "未知状态不提示");

assertEqual(classifyLoginToggleFailure({ code: "SELF_PROFILE_FIELDS_DENIED", status: 403, uncertain: false }), "self", "不能关闭本人");
assertEqual(classifyLoginToggleFailure({ code: "USER_NOT_FOUND", status: 404, uncertain: false }), "notFound", "账号不存在");
assertEqual(classifyLoginToggleFailure({ code: "USER_SCOPE_DENIED", status: 403, uncertain: false }), "scopeDenied", "超出管理范围");
assertEqual(classifyLoginToggleFailure({ code: "HIGH_PRIVILEGE_TARGET_DENIED", status: 403, uncertain: false }), "highPrivilege", "高权限账号");
assertEqual(classifyLoginToggleFailure({ code: " user_scope_denied ", status: 403, uncertain: false }), "scopeDenied", "错误码忽略大小写与空白");
assertEqual(classifyLoginToggleFailure({ code: "UPDATE_USER_LOGIN_ENABLED_FAILED", uncertain: false }), "generic", "200 + success=false 的未知错误码：通用失败");
assertEqual(classifyLoginToggleFailure({ status: 403, uncertain: false }), "forbidden", "403 无错误码：没有权限");
assertEqual(classifyLoginToggleFailure({ status: 404, uncertain: false }), "notFound", "404 无错误码：不存在");
assertEqual(classifyLoginToggleFailure({ code: "INTERNAL_SERVER_ERROR", status: 500, uncertain: true }), "uncertain", "5xx：结果未知，先核对");
assertEqual(classifyLoginToggleFailure({ code: "ERR_NETWORK", uncertain: true }), "uncertain", "无响应：结果未知");
assertEqual(classifyLoginToggleFailure({ status: 401, uncertain: false }), "generic", "其它：通用失败");

// ───────────── 姓名展示 ─────────────

assertEqual(
  buildNameDisplayParts({ givenName: "Emily", middleName: "Rose", familyName: "Chen", fullName: "Emily Rose Chen" }),
  [{ kind: "family", value: "Chen" }, { kind: "given", value: "Emily" }, { kind: "middle", value: "Rose" }],
  "姓 · 名 · 中间名",
);
assertEqual(buildNameDisplayParts({ givenName: "芳", familyName: "王" }), [{ kind: "family", value: "王" }, { kind: "given", value: "芳" }], "中文只有姓和名");
assertEqual(buildNameDisplayParts({ fullName: "Emily Chen", username: "emily" }), [{ kind: "full", value: "Emily Chen" }], "没有结构化姓名回落到全名");
assertEqual(buildNameDisplayParts({ username: "emily.chen" }), [{ kind: "full", value: "emily.chen" }], "再回落到账号");
assertEqual(buildNameDisplayParts({ givenName: "  ", fullName: " " }), [], "全空则没有内容");

// ───────────── 排班入口与汇总 ─────────────

assertEqual(resolveScheduleEntries({ variant: "store", hasStoreCode: true }), ["schedule", "records"], "店长版两行入口");
assertEqual(resolveScheduleEntries({ variant: "store", hasStoreCode: false }), [], "没有分店不显示排班入口");
assertEqual(resolveScheduleEntries({ variant: "admin", hasStoreCode: true }), [], "管理员版没有排班与记录");

assertEqual(weekdayIndexFromDate("2026-10-12"), 0, "2026-10-12 是周一");
assertEqual(weekdayIndexFromDate("2026-10-17"), 5, "2026-10-17 是周六");
assertEqual(weekdayIndexFromDate("2026-10-18"), 6, "2026-10-18 是周日");
assertEqual(weekdayIndexFromDate("bad"), null, "非法日期");

{
  const now = new Date("2026-10-14T12:00:00");
  const week = {
    days: [
      { workDate: "2026-10-12", schedules: [{ workDate: "2026-10-12", startTime: "09:00:00" }] },
      { workDate: "2026-10-14", schedules: [{ workDate: "2026-10-14", startTime: "2026-10-14T15:30:00" }] },
      { workDate: "2026-10-17", schedules: [{ workDate: "2026-10-17", startTime: "09:00" }, { workDate: "2026-10-17", startTime: "13:00" }] },
    ],
  };
  assertEqual(summarizeWeekSchedule(week, now), { shiftCount: 4, next: { weekdayIndex: 2, time: "15:30" } }, "本周 4 班，下一班是周三 15:30");
  assertEqual(summarizeWeekSchedule(week, new Date("2026-10-18T00:00:00")), { shiftCount: 4, next: null }, "本周已无班次");
  assertEqual(summarizeWeekSchedule(undefined, now), { shiftCount: 0, next: null }, "没有数据");
}

{
  const now = new Date("2026-10-16T10:00:00");
  const records = [
    { workDate: "2026-10-15" },
    { workDate: "2026-10-01" },
    { workDate: "2026-09-16" },
    { workDate: "2026-09-10" },
  ];
  assertEqual(summarizeRecordCount(records, now, { limit: 50 }), { count: 3, capped: false }, "近 30 天 3 次（含恰好 30 天前）");
  assertEqual(summarizeRecordCount(records.slice(0, 2), now, { limit: 2 }), { count: 2, capped: true }, "取满上限且全在窗口内：N+");
  assertEqual(summarizeRecordCount(records, now, { limit: 4 }), { count: 3, capped: false }, "取满上限但已跨出窗口：数量准确");
  assertEqual(summarizeRecordCount(undefined, now, { limit: 50 }), { count: 0, capped: false }, "没有数据");
}

// ───────────── 管理员版汇总工具 ─────────────

{
  const stores = [
    { storeGUID: "g1", storeCode: "1001", storeName: "Auburn", isManageable: false },
    { storeGUID: "g2", storeCode: "1008", storeName: "Bankstown", isManageable: true },
  ];
  assertEqual(summarizeStoreRelations(stores), { count: 2, manageableCount: 1, firstName: "Bankstown", firstIsManageable: true }, "管理分店排前面作代表");
  assertEqual(summarizeStoreRelations([]), { count: 0, manageableCount: 0, firstName: "", firstIsManageable: false }, "没有分店");
  assertEqual(isStoreManagerRoleDerived(["店长"], stores, isStoreManagerRoleName), true, "店长角色 + 管理分店 = 由管理分店派生");
  assertEqual(isStoreManagerRoleDerived(["店长"], [stores[0]], isStoreManagerRoleName), false, "没有管理分店不显示派生标签");
  assertEqual(isStoreManagerRoleDerived(["店员"], stores, isStoreManagerRoleName), false, "店员角色不显示派生标签");
  assertEqual(resolveEmailStoreCode("1001", stores), "1001", "路由 storeCode 优先");
  assertEqual(resolveEmailStoreCode(undefined, stores), "1008", "其次管理分店");
  assertEqual(resolveEmailStoreCode(" ", [stores[0]]), "1001", "再次任意关联分店");
  assertEqual(resolveEmailStoreCode(undefined, []), null, "没有分店则不能发邮件");
  assertEqual(selectPosStores(stores, { restrictToGuids: null }).map((store) => store.storeCode), ["1001", "1008"], "管理员可选全部关联分店");
  assertEqual(selectPosStores(stores, { restrictToGuids: ["G2"] }).map((store) => store.storeCode), ["1008"], "店长级只能选自己管理的分店（GUID 不分大小写）");
}

assertEqual(formatLocalDateTime("2026-10-16T13:42:09"), "2026-10-16 13:42", "本地时间到分钟");
assertEqual(formatLocalDate("2026-11-04T08:00:00"), "2026-11-04", "本地日期");
assertEqual(formatLocalDateTime(undefined), null, "空值");
assertEqual(formatLocalDateTime("not-a-date"), null, "非法值");
assertEqual(shortenGuid("7f3c9a10-1111-4222-8333-0123456e91a0"), "7f3c…91a0", "GUID 只露首尾");
assertEqual(shortenGuid("abc"), "abc", "很短的值原样显示");

assertEqual(summarizeActiveSessions([{ status: "active" }, { status: "revoked" }, { status: "active" }], 3), { active: 2, capped: false }, "有效会话 2 个");
assertEqual(summarizeActiveSessions([{ status: "active" }], 80), { active: 1, capped: true }, "只读了一页：N+");

console.log("detail-rules.test.ts: ok");
