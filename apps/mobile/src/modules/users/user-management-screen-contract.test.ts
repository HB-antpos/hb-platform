import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const currentDirectory = dirname(fileURLToPath(import.meta.url));
// 「人员管理」合并后，原 users/index.tsx 的店员列表逻辑迁到 store-staff-list.tsx（本测试里的 source 指它），
// users/index.tsx 只剩渲染 Hub 的薄路由文件。
const source = readFileSync(
  resolve(currentDirectory, "store-staff-list.tsx"),
  "utf8"
);
const indexRouteSource = readFileSync(
  resolve(currentDirectory, "../../../app/(shell)/users/index.tsx"),
  "utf8"
);
const hubSource = readFileSync(
  resolve(currentDirectory, "../people/people-hub-screen.tsx"),
  "utf8"
);
const personRowSource = readFileSync(
  resolve(currentDirectory, "../people/person-list-row.tsx"),
  "utf8"
);
const storeSheetSource = readFileSync(
  resolve(currentDirectory, "store-switch-sheet.tsx"),
  "utf8"
);
// 新增页已拆成「路由入口 + 店长版屏幕 + 纯逻辑」（见 people/form）；下面对「新建页」的业务保证
// 针对这三份源码合并后的文本断言，保证和拆分前同样严格。
const createSource = [
  resolve(currentDirectory, "../../../app/(shell)/users/new.tsx"),
  resolve(currentDirectory, "../people/form/staff-create-screen.tsx"),
  resolve(currentDirectory, "../people/form/staff-create-form.ts"),
].map((file) => readFileSync(file, "utf8")).join("\n");
const editSource = readFileSync(resolve(currentDirectory, "../people/form/person-edit-screen.tsx"), "utf8");

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
// 重置密码 / 编辑 / 停用 已随「更多操作」弹窗迁到详情页（P2）与编辑页（P3）：
// 列表页不再有 canResetUserPassword、「更多操作」弹窗、编辑弹窗、重置密码弹窗、停用 Alert。
// 这些保证（Users.ResetPassword 独立判断、setupEmailMutation 发邮件、脱敏日志等）由详情页 / 编辑页的契约接管。
assert.doesNotMatch(source, /moreUser|setMoreUser|dots-vertical/, "列表页不再有「更多操作」弹窗");
assert.doesNotMatch(source, /StaffPasswordSetupDialog|resetPasswordVisible/, "重置密码弹窗已迁到详情页");
assert.doesNotMatch(source, /Alert\.alert|statusMutation/, "停用确认已迁到详情页");
assert.doesNotMatch(source, /updateMutation|Dialog\.Title|dialogs\.editTitle/, "编辑弹窗已迁到编辑页");
assert.match(createSource, /createMutation/, "新建页必须接入创建 mutation");
assert.match(source, /onPress=\{openCreatePage\}/, "必须提供悬浮的新增店员入口");
assert.match(source, /pathname: "\/users\/new"/, "新增入口必须进入整页新建员工");
assert.match(
  source,
  /canCreateUsers && selectedStoreCanCreate \?[\s\S]{0,260}<FloatingAction label=\{t\("actions\.create"\)\}/,
  "只有可管理的明确分店才显示新增入口（全部已分配分店 / 仅查看的分店隐藏）"
);
assert.match(
  source,
  /if \(!managedStoreCode \|\| !selectedStoreCanCreate\) \{[\s\S]{0,120}messages\.selectManageableStoreFirst/,
  "新增前仍要二次检查：必须选中且可管理的分店，否则提示"
);
assert.match(source, /params: \{ storeCode: managedStoreCode \}/, "新增必须带上当前可管理分店");
assert.match(source, /t\("actions\.create"\)/, "新增入口必须使用本地化文案");
assert.match(createSource, /t\("staff\.title"\)/, "新建页必须使用创建标题");
assert.doesNotMatch(createSource, /password:/, "新建页不得提交任何密码，员工用邮件验证码自己设置");
assert.match(createSource, /sendPasswordSetupEmail: true/, "新建后必须给员工邮箱发初始密码，不提交任何密码");
assert.match(createSource, /initialPassword: true/, "新建页重发必须仍然发初始密码，不能退回验证码");
assert.match(createSource, /validateNewStaffEmail\(values\.email, t\)/, "新建时邮箱必填");
assert.match(createSource, /resolveUsernameFromEmail\(value\)/, "用户名默认跟随邮箱");
assert.match(createSource, /createMutation\.mutateAsync/, "保存创建表单必须调用创建接口");
assert.match(createSource, /t\("staff\.submit"\)/, "新建页主按钮必须显示创建文案");
assert.match(createSource, /t\("created\.headline"\)/, "创建成功必须给出明确反馈");
assert.match(createSource, /t\("created\.inviteSent"/, "创建成功页必须显示初始密码邮件的发送状态");
assert.match(createSource, /resendInvite/, "邮件没发出时店长必须能重发");
assert.match(createSource, /validateNewStaffPhone\(values\.phone, t\)/, "新建时必须校验澳洲手机号");
assert.doesNotMatch(source, /createMutation/, "列表页不再承载创建表单");
// 「店长重置密码改为发送设置密码邮件（setupEmailMutation）」随重置密码入口迁到详情页，由详情页的契约测试锁定。
// 创建在新增页、编辑在编辑整页（列表页不再承载保存表单）。
for (const screenSource of [createSource, editSource]) {
  assert.match(
    screenSource,
    /console\.warn\("\[store-users\] save failed", toSafeStoreUserErrorLog\(error\)\)/,
    "创建或编辑失败只能记录脱敏后的错误元数据"
  );
}
assert.match(
  createSource,
  /console\.warn\("\[store-users\] save failed", toSafeStoreUserErrorLog\(error\)\)/,
  "创建失败只能记录脱敏后的错误元数据"
);
// 编辑已从列表弹窗搬到整页（people/form/person-edit-screen）：权限写法与脱敏日志要求不变。
assert.match(
  editSource,
  /const canEditUsers = access\.isAdmin\s*\|\| access\.hasPermission\(PERMISSIONS\.Users\.Edit\)\s*\|\| access\.hasPermission\(PERMISSIONS\.Users\.EditStoreStaff\)/,
  "编辑店员必须使用 Users.Edit 或 Users.EditStoreStaff 权限门槛"
);
assert.match(
  editSource,
  /const canResetPasswords = access\.isAdmin\s*\|\| access\.hasPermission\(PERMISSIONS\.Users\.ResetPassword\)\s*\|\| access\.hasPermission\(PERMISSIONS\.Users\.ResetStoreStaffPassword\)/,
  "编辑后提示发送设置密码邮件必须使用 Users.ResetPassword 或 Users.ResetStoreStaffPassword 权限门槛"
);
assert.doesNotMatch(
  source,
  /console\.warn\("\[store-users\] (?:save|password reset) failed", error\)/,
  "含密码请求失败时不得直接记录 Axios 错误对象"
);
assert.match(source, /testID="compact-staff-row"/, "员工列表必须使用紧凑行布局");
assert.doesNotMatch(source, /lastLoginIpValue/, "低频登录 IP 不应继续占用员工列表首屏");
assert.match(source, /countStaffStatuses\(users\)/, "状态筛选必须显示计数");
assert.match(source, /count: statusCounts\[key\]/, "每个状态芯片都要带计数");
assert.match(source, /statusChips\.pendingFirstLogin/, "状态筛选必须包含待首次登录");
assert.match(source, /statusChips\.cannotLogin/, "状态筛选必须包含不能登录");
assert.match(source, /statusChips\.noEmail/, "状态筛选必须保留无邮箱");
assert.match(
  source,
  /useStoreUsers\(\s*canViewUsers \? \(isDeviceMode \? deviceBoundStoreCode : managedStoreCode\) : undefined,\s*""\s*\)/,
  "搜索在客户端做：服务端永远取该分店全部店员，不再把关键词发给服务端（昵称、姓名各段才搜得到）"
);
assert.match(source, /StaffBarcodeDialog/, "员工行必须接入单人个人码弹层");
assert.match(source, /StaffBarcodeBatchDialog/, "可管理的明确分店必须接入批量打印弹层");
assert.match(source, /!isDeviceMode && canEditUsers && managedStoreCode/, "纯设备模式或无编辑权限不得显示批量写入口");
assert.match(source, /canManageStaffBarcode\(/, "个人码入口必须复用统一资格判断，停用员工不得获得写入口");
assert.match(source, /setSelectedUserGuids\(new Set\(\)\)[\s\S]{0,120}setBatchSelecting\(false\)/, "换店必须清空批量选择");

// 员工管理入口改认 Users.ViewStoreStaff；Users.View（canReadUser）持有者同样放行，user-admin 不受影响。
assert.match(
  source,
  /const canViewUsers = access\.isAdmin\s*\|\|\s*access\.canReadUser\s*\|\|\s*access\.hasPermission\(PERMISSIONS\.Users\.ViewStoreStaff\)/,
  "员工列表可见性必须是 ViewStoreStaff 或 View"
);
const roleMenuCatalogSource = readFileSync(resolve(currentDirectory, "../identity-admin/role-menu-catalog.ts"), "utf8");
assert.match(roleMenuCatalogSource, /\["users", "Staff management", "人员管理", \["Users\.ViewStoreStaff"\]\]/, "users 路由（人员管理）必须认 Users.ViewStoreStaff");
assert.match(roleMenuCatalogSource, /\["user-admin", "User management", "用户管理", \["Users\.View"\]\]/, "user-admin 仍然是 Users.View");
const iosReviewMenuSource = readFileSync(resolve(currentDirectory, "../ios-review/menu.ts"), "utf8");
assert.match(iosReviewMenuSource, /routeName: "users"[^}]*permission: "Users\.ViewStoreStaff"/, "审核模式菜单的 users 必须同步新权限码");

// 「我」分组：本人行置顶、带「我」标签，且不提供编辑/停用/重置密码/个人码等入口（后端会拒绝）。
assert.match(source, /t\("list\.me"\)/, "列表必须有「我」分组");
assert.match(source, /t\("list\.staffSection"/, "列表必须有「店员」分组标题");
assert.match(source, /t\("list\.onlyStaffNote"\)/, "列表底部必须说明不显示其他店长");
assert.match(personRowSource, /item\.isSelf \? <PeopleTag label=\{t\("row\.you"\)\}/, "本人行必须带「你」标签");
assert.match(source, /onPress=\{isSelf \? undefined :/, "本人行不得进入员工详情");
assert.match(source, /canUseBarcode = !isSelf && canManageUserBarcode\(user\)/, "本人行不得出现个人码入口");
assert.match(source, /right=\{\s*isSelf \? undefined : \(/, "本人行右侧不得出现个人码 / 进入详情的箭头");
assert.match(source, /pathname: "\/users\/\[userGuid\]"/, "点整行必须进入统一详情页");
assert.match(source, /storeCode: targetStoreCode/, "进入详情必须带上所属分店，供详情页按 userGuid 拉取目标与复制权限列出同事");
assert.match(
  source,
  /userName: resolvePersonName\(user\)\.primary,[\s\S]{0,160}roleNames: user\.roleNames\.join\("\|"\)/,
  "旧的预填参数（userName / username / targetStatus / roleNames）继续传，只作首屏占位"
);
assert.match(source, /toggleSelection\(user\.userGUID\)/, "批量打印多选必须保留");
assert.match(source, /setBarcodeUser\(user\)/, "右侧二维码图标必须打开单人个人码弹层");

// Hub：页签门禁、URL 驱动页签、审核模式不挂载管理员列表。
assert.match(indexRouteSource, /PeopleHubScreen/, "users/index.tsx 只渲染 Hub");
assert.doesNotMatch(indexRouteSource, /useStoreUsers|StaffBarcode/, "users/index.tsx 不再承载列表逻辑");
assert.match(hubSource, /useIdentitySession\("Roles\.View"\)/, "角色 / 权限页签必须使用 Roles.View 的入口门禁");
assert.match(hubSource, /useIdentitySession\("Users\.View"\)/, "管理员版人员列表必须使用 Users.View + account 会话门禁");
assert.match(hubSource, /canViewRolesAndPermissions: rolesGate\.allowed/, "只有通过门禁的账号才显示角色 / 权限页签");
assert.match(hubSource, /router\.setParams\(\{ tab: next \}\)/, "页签状态必须写进 URL，详情页返回时保持页签");
assert.match(hubSource, /normalizeHubTab\(tab, tabs\)/, "非法或无权限的 tab 必须回落");
assert.match(hubSource, /access\.hasPermission\(PERMISSIONS\.Users\.ViewStoreStaff\)/, "人员页签仍认 Users.ViewStoreStaff（与 Users.View 并列，不收敛成单个码）");
assert.match(hubSource, /listMode === "admin" \?[\s\S]{0,120}<AdminUserList[\s\S]{0,120}<StoreStaffList/, "管理员走全局用户列表，其余走本店店员列表");
assert.match(hubSource, /renders\("roles"\)[\s\S]{0,200}<RolesList[\s\S]{0,200}renders\("permissions"\)[\s\S]{0,200}<PermissionsList/, "角色 / 权限面板只在页签可见且访问过时才挂载（审核模式不会挂载，也就不发管理员请求）");

// 切换分店弹层：POS 启用候选 + 设备绑定模式不给「全部」，可管理判断来自原始分店集合。
assert.match(
  source,
  /<StoreSwitchSheet[\s\S]{0,260}stores=\{pickerStores\}[\s\S]{0,180}includeAllOption=\{!deviceBoundStoreCode\}/,
  "切换分店弹层必须使用候选分店（POS 启用 + 店长只含管理分店），并在设备绑定模式禁用全部分店选项"
);
// 店长只能看自己「管理分店」里角色是店员的账号：候选里不能出现「仅查看」的分店（后端对非管理分店直接拒绝）。
assert.match(
  source,
  /const pickerStores = useMemo\([\s\S]{0,260}access\.isAdmin[\s\S]{0,120}\? posEnabledStores[\s\S]{0,160}isStoreManageable\(store\.storeCode, manageableStores\)/,
  "店长的分店候选只能是管理分店，管理员不受限"
);
assert.match(source, /pickerStores\.some\(\(store\) => store\.storeCode === current\)/, "默认 / 记忆的分店也必须落在候选里，不能自动选中非管理分店");
// 店长（即使持有 Users.View）永远走店员口径：列表 / 详情 / 编辑三处都要带上这个标记。
assert.match(hubSource, /isStoreLevelManager: access\.isStoreLevelManager/, "Hub 必须把店长标记传给列表模式判断，店长不能进管理员全局列表");
assert.match(source, /manageableStores=\{manageableStores\}/, "可管理 / 仅查看必须按原始可管理分店集合判断");
assert.match(storeSheetSource, /isStoreManageable\(store\.storeCode, manageableStores\)/, "每家分店标出可管理 / 仅查看");
assert.match(storeSheetSource, /currentStore\.manageableBadge/, "弹层必须有「可管理」标记");
assert.match(storeSheetSource, /currentStore\.viewOnlyBadge/, "弹层必须有「仅查看」标记");

// 复制权限：入口、保护与确认流程。
// 授权页拆成 页面 / 编辑器 / 四个分段，业务保证分别锁在对应文件里。
const accessScreenSource = readFileSync(resolve(currentDirectory, "access-management-screen.tsx"), "utf8");
const accessEditorSource = readFileSync(resolve(currentDirectory, "access-editor.ts"), "utf8");
const accessPermissionsSectionSource = readFileSync(resolve(currentDirectory, "access-permissions-section.tsx"), "utf8");
const accessRolesSectionSource = readFileSync(resolve(currentDirectory, "access-roles-section.tsx"), "utf8");
const accessStoresSectionSource = readFileSync(resolve(currentDirectory, "access-stores-section.tsx"), "utf8");
const accessPageLogicSource = readFileSync(resolve(currentDirectory, "access-page-logic.ts"), "utf8");
const copySheetSource = readFileSync(resolve(currentDirectory, "permission-copy-sheet.tsx"), "utf8");
assert.match(accessPermissionsSectionSource, /onCopy\("toOthers"\)/, "权限页必须有「复制给他人」入口");
assert.match(accessPermissionsSectionSource, /onCopy\("fromColleague"\)/, "权限页必须有「从同事复制」入口");
assert.match(accessScreenSource, /onCopy=\{openCopySheet\}/, "分段的复制入口必须走页面的 openCopySheet（统一做未保存草稿保护）");
assert.match(
  accessScreenSource,
  /if \(editor\.permissionsDirty\) \{[\s\S]{0,200}copy\.dirtyBlocked/,
  "存在未保存的权限草稿时必须先拦截，避免被复制结果静默覆盖"
);
assert.match(accessEditorSource, /previewCopyMutation\.isPending \|\|\s*copyPermissionsMutation\.isPending/, "复制进行中必须计入忙碌状态，阻止离页与重复保存");
assert.match(accessPermissionsSectionSource, /\{!implicitAll \? \(\s*<View style=\{styles\.copyButtons\}>/, "管理员默认拥有全部权限时不显示复制入口");
assert.match(accessScreenSource, /copyParam/, "路由 copy=1 必须能直接打开复制权限");
assert.match(accessScreenSource, /copyAutoOpenedRef/, "copy=1 只自动打开一次");
assert.match(copySheetSource, /onPreview\(request\)/, "先预览差异");
assert.match(copySheetSource, /onCommit\(input\)/, "确认后才写入");
assert.match(copySheetSource, /!previewCurrent \|\| !summary\?\.hasChanges/, "预览过期或没有变化时不得确认复制");
assert.match(copySheetSource, /dismissable=\{!busy\}/, "提交 / 预览中不得关闭弹层");
assert.match(
  copySheetSource,
  /const queryStoreCode = storeCode\.trim\(\)\s*\? storeCode\s*:\s*storeFilter\.allowAll\s*\?\s*null\s*:\s*undefined;/,
  "只有管理员（allowAll）才能查全部分店；店长无分店时不得发请求"
);
assert.match(copySheetSource, /useStoreUsers\(queryStoreCode, ""\)/, "候选同事必须经过 queryStoreCode 收窄");
assert.match(copySheetSource, /next === mode && previewCurrent/, "切换合并 / 覆盖会作废旧预览并重新预览");

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
// 更换打印机改为弹层内就地完成（LabelPrinterSetupSheet），不再关掉弹层跳去设置页；单个与批量弹层都必须有入口。
assert.match(barcodeDialogSource, /<LabelPrinterSetupSheet visible=\{printerSheetVisible\}/, "个人码弹层必须提供就地更换打印机入口");
assert.equal(barcodeDialogSource.match(/<LabelPrinterSetupSheet\b/g)?.length, 2, "单个与批量个人码弹层都要接入就地更换打印机");
assert.doesNotMatch(barcodeDialogSource, /router\.navigate\("\/\(shell\)\/settings"\)/, "个人码弹层不得再跳去设置页丢失上下文");
assert.match(barcodeDialogSource, /testID="staff-barcode-change-printer"/, "打印机状态行必须带「更换」入口");
assert.match(barcodeDialogSource, /<PeopleSheet[^>]*testID="staff-barcode-dialog"/, "单个个人码弹层必须是底部弹层");
assert.match(barcodeDialogSource, /<PeopleSheet[^>]*testID="staff-barcode-batch-dialog"/, "批量个人码弹层必须是底部弹层");
assert.match(barcodeDialogSource, /pendingConfirmation\?\.phase === "printed"/, "已出纸待确认必须显示独立恢复状态");
assert.match(barcodeDialogSource, /fontScale > 1\.2 \|\| width < 380/, "窄屏或大字号时弹层操作必须改为纵向布局");


// 创建员工页「账号归属」里的分店标签：新员工与这家店的关系是「仅查看」（后端 IsPrimary=false），
// 「可管理」是列表页表示操作者能管这家店的徽标，不能在这里出现（会被误读成新员工是管理者）。
assert.match(createSource, /currentStore\.viewOnlyBadge/, "创建页的分店标签必须是「仅查看」");
assert.doesNotMatch(createSource, /currentStore\.manageableBadge/, "创建页不得出现「可管理」徽标");

// 权限管理页：店长是由「管理」分店派生的角色。管理员点它要被引导去「分店」，而不是无反应；非管理员保持锁定。
{
  assert.match(accessEditorSource, /planStoreManagerGrant\(\{/, "点店长行必须走 planStoreManagerGrant");
  assert.match(
    accessPageLogicSource,
    /selection\.derived && input\.canGrantStoreManagement && input\.storesEditable/,
    "只有管理员且分店分段可编辑才允许引导"
  );
  assert.match(
    accessRolesSectionSource,
    /storesEditable=|storesEditable,/,
    "角色分段必须把「分店分段是否可编辑」传进角色行判定"
  );
  assert.match(accessScreenSource, /storesEditable=\{eligibility\.storesMode === "editable"\}/, "页面按分店分段的真实可编辑状态传入");
  assert.match(accessPageLogicSource, /\(selection\.locked && !canGuideManagerGrant\)/, "非管理员的店长行仍是锁定");
  assert.match(accessRolesSectionSource, /disabled=\{row\.disabled \|\| row\.derived\}/, "派生角色的复选框本身不可勾选");
  assert.match(accessEditorSource, /setUserAccessStoreState\(current, plan\.storeGUID, "manage"\)/, "只有一家分店时在草稿里设为管理");
  assert.match(accessScreenSource, /setSelectedSection\("stores"\)/, "引导后切到「分店」分段，让管理员看到并保存");
  // 选角色保存时不会把店长角色提交给角色接口（它由后端按管理分店派生）
  assert.match(accessEditorSource, /roleGuids: submitted,\s*roleCatalog: roleOptions/, "保存角色仍只提交可分配角色（目录经 buildAssignableRoleGuids 剔除店长）");
  assert.match(
    readFileSync(resolve(currentDirectory, "access-management-api.ts"), "utf8"),
    /buildAssignableRoleGuids\(/,
    "角色接口必须剔除店长这个派生角色"
  );
}

// 统一授权页（一页分段 + 一个吸底保存）：分店 / 角色 / 直接权限的改动一起保存，失败不吞错。
{
  assert.match(accessEditorSource, /runAccessSaveSteps\(steps/, "保存必须按顺序依次提交各分段");
  assert.match(accessEditorSource, /planAccessSaveSteps\(\{/, "只提交有改动的分段");
  assert.match(accessScreenSource, /buildAccessSaveReportLines\(/, "保存失败要逐项说明哪些成功、哪一步失败");
  assert.match(accessScreenSource, /Alert\.alert\(\s*t\(report\.verdict === "partial"/, "部分失败必须弹出明确提示，不能只靠一闪而过的 Snackbar");
  assert.match(accessScreenSource, /<StickyActionBar summary=\{barSummary\} hint=\{barHint\}>/, "吸底保存栏常驻，写明改了什么");
  assert.match(accessScreenSource, /disabled=\{!dirty \|\| editor\.busy\}/, "没有改动时保存灰掉");
  assert.match(accessScreenSource, /usePreventRemove\(/, "有未保存草稿时离页必须确认");
  // 目标用户自取：路由参数只作首屏预填，受保护角色 / 本人 / 设备模式校验基于服务端数据。
  assert.match(accessScreenSource, /useAccessIdentityTarget\(/, "管理员会话按 userGuid 自取目标");
  assert.match(accessScreenSource, /useAccessStoreUserTarget\(/, "店长 / 设备 / 审核会话按 userGuid 自取目标");
  assert.match(accessScreenSource, /mergeAccessTarget\(/, "服务端数据到位后以服务端为准");
  assert.match(accessEditorSource, /targetRoleNames: target\.roleNames|targetRoleNames,/, "资格判断使用目标的真实角色");
  assert.match(accessEditorSource, /getUserAccessEligibility\(/, "受保护目标授权资格必须用 getUserAccessEligibility");
  // 收银权限入口：跳收银权限页，storeCode 必填。
  assert.match(accessScreenSource, /pathname: "\/users\/\[userGuid\]\/pos-terminal-permissions"/, "收银权限入口指向收银权限页");
  assert.match(accessScreenSource, /storeCode: store\.storeCode/, "收银权限页要求 storeCode 必填");
  // 店长收窄：只列自己管理的分店、没有「管理」档。
  assert.match(accessStoresSectionSource, /resolveStoreStateOptions\(\{/, "三档按钮由 resolveStoreStateOptions 收窄（店长没有「管理」档）");
  assert.match(accessEditorSource, /managedStoreCodes: managedStoreCodeSet/, "店长只列自己管理的分店");
  // 直接权限：继承来的不可改；收银权限不在这里。
  assert.match(accessEditorSource, /excludePosPermissionCategories\(permissionCategories\)/, "收银权限统一去收银权限页，不在直接权限里");
  assert.match(accessPermissionsSectionSource, /const disabled = busy \|\| !editable \|\| row\.locked;/, "继承来的权限开关置灰");
}

console.log("user-management-screen-contract.test.ts: ok");
