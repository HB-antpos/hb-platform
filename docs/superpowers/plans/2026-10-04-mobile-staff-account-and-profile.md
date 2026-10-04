# 移动端员工账号创建与个人信息 Implementation Plan

**Goal:** 店长能在移动端为本店创建店员账号并把登录信息交给员工；员工首次登录先改密，能自助维护个人信息；生日与银行、养老金、证件一起作为敏感资料走店长审核。

**Approach:** 分 4 个可独立验证、按顺序合并的 PR。每个 PR 都同时改后端与移动端（必要时含 Web），新列一律走版本号 schema 迁移，权限变更另附幂等 SQL。

**Constraints:**
- 设计稿：https://claude.ai/artifact/3Y73qsJbEb3NEtsdWyfd6Z（画板 A1–A4、B1–B5、C1–C2，每块便签写明了改动点），计划与设计稿冲突时以设计稿为准。
- 生日口径（2026-10-04 用户定）：员工自助改生日必须审核；管理员后台直改生日免审；历史数据不追溯。
- 店长只能建本店（`UserStore.IsPrimary`）`StoreStaff` 账号，前后端两道限制保持不变。
- 生产 schema 不随启动迁移，要显式 `--schema=migrate`；新权限、角色授权要手工执行 SQL。这两件事都须用户单独授权后再做。
- 不引入新原生依赖（日期选择用纯 JS 实现），全部改动可走 OTA，不需重建原生包。
- 提交、推送、开 PR、部署都须用户另行同意。

---

## PR1 生日改为敏感资料（审核后生效）

后端 `services/backend/`，移动端 `apps/mobile/`，Web `apps/web/`。

### 1.1 Schema：申请表加 Birthday 列
- [ ] `BlazorApp.Shared/Models/HBweb/EmployeeProfileSensitiveChangeRequest.cs` 加 `DateTime? Birthday`（SQL `date NULL`）。
- [ ] 新迁移 `BlazorApp.Api/Data/SchemaMigrations/EmployeeProfileSensitiveBirthdaySchema.cs`：`ApplySql` 用 `COL_LENGTH` 判空后 `ALTER TABLE ... ADD Birthday date NULL`，`VerifySql` 校验列签名。照 `AttendanceScheduleMealBreakSchema.cs` 的写法。
- [ ] 登记四处：`SchemaMigrationCoordinator.cs` 加 Id 常量 `20261004.001-employee-profile-sensitive-birthday`，并加进 `MainMigrationSteps`；`SchemaMigrationRuntime.cs` 的接口与实现；`SchemaMigrationCoordinatorTests.cs` 的方法清单、步骤顺序和 fake runtime。

### 1.2 审核链路纳入 Birthday（`EmployeeProfileSensitiveChangeService.cs`）
- [ ] `SensitiveFieldNames` 加 `birthday`。
- [ ] 以下各处补上 Birthday：提交快照构造、`BuildCurrentSnapshotAsync` 的两个分支、`GetChangedFields`（日期按 `.Date` 比较，另写比较函数）、`HasSensitiveValueChanges`、`ApproveAsync` 写回正式表的 `SetColumns`、`MapDetail`、`MapCurrentSnapshot`。
- [ ] DTO（`EmployeeProfileDtos.cs`）：`EmployeeProfileSensitiveChangeUpsertDto`、Snapshot、Detail 三个都加 `Birthday`。

### 1.3 本人保存不再直接写生日（`EmployeeProfileService.cs`）
- [ ] `EmployeeProfileUpsertDto.Birthday` 改成 `HasBirthday` 追踪属性，照 `HasEmail` 的写法。
- [ ] 本人路径（`UpsertForUserAsync` 中 `isAdmin=false` 的分支）：
  - 删掉更新分支的 `.SetColumns(item => item.Birthday == birthday)`。
  - 新建资料时 `ApplyChanges` 写入 Birthday 的代码移到 `allowSensitiveChanges` 条件内。
  - 请求带了 `Birthday`（`HasBirthday=true`）且与正式值不同时，返回错误 `BIRTHDAY_REQUIRES_REVIEW`（「生日需要提交审核，请更新 App 后在敏感资料中修改」）；与正式值相同则忽略。这样旧版 App 改其他字段照常能保存，只有改生日会收到明确提示，不会出现「显示保存成功、生日却变回去」。
- [ ] 管理员路径：照常写 Birthday；`HasSensitiveChanges` 加入 Birthday，管理员改生日时递增 `SensitiveRevision` 并作废员工的待审申请（沿用现有行为）。

### 1.4 测试（`BlazorApp.Api.Tests`）
- [ ] `EmployeeProfileSensitiveChangeServiceTests`：提交含生日会算作变更字段；通过后正式表生日更新；驳回后正式表不变；只改生日也能单独提交。
- [ ] `EmployeeProfileSelfServiceTests` / `EmployeeProfileServiceTests`：本人传入不同生日返回 `BIRTHDAY_REQUIRES_REVIEW` 且不写库；传入相同生日或不传都能正常保存；管理员改生日直接生效，并让待审申请变为 Superseded。
- [ ] SQL Server 迁移实测：本机 14343 容器 + `HBWEB_SCHEMA_SQLSERVER_TEST_CONNECTION`，跑迁移相关的测试。

### 1.5 移动端
- [ ] `src/modules/employee-profile/types.ts`、`api-contract.ts`、`sensitive-profile.ts`：`birthday` 进 `SENSITIVE_FIELDS` 和敏感草稿、diff；基本资料保存（`sensitive-profile.ts` 中组装基本资料提交数据处）不再带 `birthday`。
- [ ] `app/(shell)/employee-profile.tsx`：
  - 概览：生日从基本资料卡移到敏感资料卡，显示「YYYY-MM-DD · N 岁」。
  - 编辑基本资料：生日改为只读，带锁图标和「去填报」入口。
  - 敏感资料编辑：新增「个人信息 · 生日」分区，输入时校验日期，实时显示年龄；与已确认值不同的字段标「已修改」。
- [ ] 年龄计算放进 `src/modules/employee-profile/` 的纯函数，单测覆盖闰年和生日当天。
- [ ] 审核端：`src/modules/employee-profile-review/types.ts` 的字段常量、`api.ts` 的白名单与解析、详情页对比行加生日（不遮挡），同时显示前后年龄。
- [ ] 文案：`src/locales/{zh,en}/screens/employeeProfile.json`、`employeeProfileReview.json`。
- [ ] 测试：`employee-profile/api.test.ts`、`sensitive-profile.test.ts`、`employee-profile-review/api.test.ts`，以及新增的年龄函数测试。

### 1.6 Web
- [ ] `apps/web/src/pages/System/EmployeeProfiles/logic.ts` 字段常量、`types/employeeProfile.ts` 字段联合与详情类型、`SensitiveChangeReviewPanel.tsx` 对比行加生日。管理员直改页 `index.tsx` 不用改。
- [ ] 测试：`logic.test.ts`、`services/employeeProfileService.sensitiveChange.test.ts`。

**验证：** 后端定向 `dotnet test --filter` 加全量（不含 SQL / Performance）；移动端 `npm run test:ci` 与 `npx tsc --noEmit`；Web `npm run typecheck` 与相关 `test:*`。

**上线顺序：**
1. 生产执行 `--schema=migrate`（或单独执行该迁移的 ApplySql）。
2. 部署后端。
3. 发移动端 OTA。

后端先上线后，旧 App 只在改生日时收到提示，其余保存照常。

---

## PR2 首次登录强制改密 + 自助修改密码

### 2.1 Schema 与后端
- [ ] `[User]` 表加 `MustChangePassword bit NOT NULL DEFAULT 0`（迁移 `20261004.002-user-must-change-password`，登记方式同 1.1）。默认 0，历史账号不受影响。
- [ ] `UserDto`（`/api/Auth/current` 返回值）加 `MustChangePassword`。
- [ ] `StoreUserCreateDto` 加 `RequirePasswordChange`（默认 true）；`StoreUserReactService.CreateAsync` 据此写入标记。
- [ ] `StoreUserReactService.UpdatePasswordAsync`（店长重置密码）把标记置 1。管理员重置密码时，`ResetPasswordDto.RequireChangePassword` 原本不生效，现在接通。
- [ ] `AuthService` 改密成功后把标记清 0，并吊销该用户其他会话的 RefreshToken（保留当前会话）。
- [ ] `StoreUserListDto`（`GetGridDataAsync` 的投影与映射两处）加 `MustChangePassword`，供 A1 显示「待首次登录」标签。
- [ ] 测试：`StoreUsersReactServiceTests`（创建、重置时置标记）、Auth 改密测试（清标记、吊销其他会话）。

### 2.2 移动端
- [ ] `src/modules/auth/api.ts` 的 `normalizeCurrentUser` 带出 `mustChangePassword`；新增 `changePassword` 接口调用。
- [ ] 新页面 `app/(auth)/change-password.tsx`，有两种模式：
  - 强制：设计稿 B1，不能返回，只能「保存并进入」或「退出登录」。
  - 自助：从个人信息「账户与安全 → 修改密码」进入，可以返回。
- [ ] 拦截：`app/(shell)/_layout.tsx` 恢复会话后、应用默认路由前，以及 `app/(auth)/login.tsx` 的四处跳转前，检查 `user.mustChangePassword`，为真则跳到强制改密页。
- [ ] 路由登记：如果 `app-routes-contract.test.ts` 等契约测试要求新路由登记，按 `mobile-menu-registration-checklist` 同步。
- [ ] 测试：拦截判定写成纯函数并加单测，页面结构用契约测试覆盖。

**非目标：** 后端不按标记拦截其他接口（改密是使用流程约束，不是鉴权边界）；旧版 App 看不到这个标记，照常使用。

---

## PR3 店长建员工流程改版

依赖 PR2（列表标记、创建时的 `RequirePasswordChange`）。

### 3.1 后端
- [ ] 修复编辑员工时清空分店关联：`StoreUserReactService.UpdateAsync` 不再删除该员工的全部 `UserStore`，只确保目标分店那一行存在。补上 UpdateAsync 的测试（多分店员工编辑后其他分店关联仍在）。
- [ ] 店长权限模板（`PermissionSeedData.cs` 中 `StoreManagerPermissionCodes` 和中文「店长」「经理」两份）加 `Users.Create`、`Users.Edit`、`Users.ResetPassword`；同步 `SeedDataServiceTests` 的断言。
- [ ] 附幂等 SQL `BlazorApp.Api/Data/Migrations/20261004_GrantStoreManagerUserAccountPermissions.sql`：按角色名向 `HBwebSysRolePermissions` 补授这三个权限，先查重，并输出影响行数。**生产执行须用户授权。**

### 3.2 移动端（`app/(shell)/users/`）
- [ ] A1 员工列表：顶部固定分店卡；状态筛选（全部 / 启用 / 已停用 / 待首次登录 / 资料待审核）；行内状态标签。
- [ ] A2 新建员工从弹窗改为整页：
  - 归属信息改成只读卡片。
  - 初始密码可自动生成，也可手改，可显示 / 隐藏。
  - 「首次登录须修改密码」开关，默认开。
  - 手机号按澳洲规则校验：04 开头共 10 位，允许空格。
  - 校验规则放在 `modules/users/validation.ts`。
- [ ] A3 创建完成页：初始密码只在本页显示，复制登录信息用 `expo-clipboard`（已安装），可打印个人码。
- [ ] A4 员工详情（`staff/[userGuid].tsx`）：
  - 新增「资料状态」卡（首次改密、基本资料完整度、敏感资料待审）。
  - 重置密码、停用按钮按 `Users.ResetPassword` / `Users.Edit` 显示。
  - 重置密码后展示新密码一次。
- [ ] 测试：`validation` 单测、密码生成单测（长度、字符集、排除易混字符）、页面契约测试。

---

## PR4 审核体验：撤回、历史、审核列表

依赖 PR1。

### 4.1 后端
- [ ] 状态枚举加 `Withdrawn = 4`。新增 `POST /me/sensitive-change-request/withdraw`：照 `RejectAsync` 的写法加锁，条件更新 `Status==Pending`，并清理待审证件照。
- [ ] 新增 `GET /me/sensitive-change-requests?take=20`，返回本人申请历史（状态、变更字段、提交 / 审核时间、驳回原因），不返回敏感值。
- [ ] 审核端列表、计数、Web 审核面板正确识别 Withdrawn。
- [ ] 测试：撤回成功、撤回非 Pending 返回冲突、撤回后可重新提交、历史按时间倒序。

### 4.2 移动端
- [ ] B5 审核进度页：时间线、本次变更、撤回、修改后重提、历史申请与驳回原因。
- [ ] B2 概览：资料完整度卡片；「账户与安全」分组（修改密码入口来自 PR2，加上个人码）。
- [ ] B3 / B4：纯 JS 日期选择器（年 / 月 / 日三列），替换生日文本输入；提交按钮显示变更项数。
- [ ] C1 审核列表：「待审核 / 已处理」分段和搜索（后端已支持 `status` / `search`）。
- [ ] C2 审核详情：未变更字段折叠，新旧证件照并排。

**非目标（另立项）：** 审核结果推送通知（仓库没有推送基础设施）、敏感字段落库加密、审核页防截屏。

---

## 自检
- 设计稿每块画板都有对应任务：A1–A4 → PR3；B1 → PR2；B2–B5 → PR1 / PR4；C1–C2 → PR1 / PR4。
- 生日三条口径分别落在 1.3（本人需审核、管理员直改）和「历史不追溯」（迁移只加列，不回填）。
- 生产操作（schema 迁移、权限 SQL、部署、OTA）都标明须单独授权。
