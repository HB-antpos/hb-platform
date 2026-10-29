import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import {
  DEFAULT_MANAGED_PROFILE_FILTERS,
  buildManagedProfileEditBaseline,
  buildManagedProfileFilterKey,
  buildManagedProfileListParams,
  buildManagedProfileRow,
  buildManagedProfileUpdatePayload,
  buildSensitiveSummary,
  classifyManagedProfileFailure,
  filterPendingItems,
  flattenManagedProfilePages,
  listManagedProfileChanges,
  normalizeManagedProfileDetail,
  normalizeManagedProfileFilterOptions,
  normalizeManagedProfilePage,
  readManagedProfileErrorMeta,
  resolveDetailMissing,
  resolveEditableFields,
  resolveListItemMissing,
  resolveManagedProfileNextPage,
  resolveStoreChip,
  shouldAutoLoadMoreForPending,
  validateManagedProfileEdit,
  type ManagedProfileDetail,
  type ManagedProfileListItem,
} from "./managed-profile-logic";

// ───────── 夹具 ─────────
function listItem(overrides: Partial<ManagedProfileListItem> = {}): ManagedProfileListItem {
  return {
    employeeInfoId: 1,
    userGUID: "u-1",
    username: "kevin",
    email: "kevin@example.com",
    fullName: "Kevin Zhang",
    hasProfile: true,
    phone: "0412345678",
    bankBsb: "062-000",
    bankAccountNumber: "****1234",
    superannuationCompanyName: "AustralianSuper",
    superannuationCompanyCode: "",
    superannuationAccountNumber: "****5678",
    gender: "male",
    employeeType: "fullTime",
    birthday: "1998-06-21T00:00:00",
    avatarUrl: "",
    updatedAt: "",
    storeNames: ["Wallsend"],
    roleNames: ["StoreStaff"],
    ...overrides,
  };
}

function detail(overrides: Partial<ManagedProfileDetail> = {}): ManagedProfileDetail {
  return {
    employeeInfoId: 1,
    userGUID: "u-1",
    username: "kevin",
    email: "kevin@example.com",
    fullName: "Kevin Zhang",
    hasProfile: true,
    storeNames: ["Wallsend"],
    roleNames: ["StoreStaff"],
    phone: "0412345678",
    gender: "male",
    employmentType: "casual",
    address: "12 Cowper St",
    avatarUrl: "",
    bankBsb: "062-000",
    bankAccountNumber: "****1234",
    superannuationCompanyName: "AustralianSuper",
    superannuationCompanyCode: "",
    superannuationAccountNumber: "****5678",
    identityType: "",
    identityId: "",
    hasBirthday: true,
    canEditEmploymentType: false,
    updatedAt: "",
    updatedBy: "",
    ...overrides,
  };
}

// ───────── 响应归一化 ─────────
{
  const page = normalizeManagedProfilePage({
    items: [{ userGUID: "u-1", username: " kevin ", hasProfile: true, storeNames: ["A", 3, " "], roleNames: ["StoreStaff"], bankAccountNumber: "****1234" }],
    total: 31,
    page: 2,
    pageSize: 20,
  });
  assert.equal(page.items[0].username, "kevin", "字符串去首尾空白");
  assert.deepEqual(page.items[0].storeNames, ["A"], "非字符串 / 空白分店名被丢弃");
  assert.equal(page.items[0].hasProfile, true);
  assert.equal(page.total, 31);
  assert.equal(page.page, 2);
  // PascalCase 响应同样可读。
  const pascal = normalizeManagedProfilePage({ Items: [{ UserGUID: "u-2", Username: "amy" }], Total: 1, Page: 1, PageSize: 20 });
  assert.equal(pascal.items[0].userGUID, "u-2");
  assert.throws(() => normalizeManagedProfilePage(null), /RESPONSE_INVALID/, "非对象响应视为无效");
  assert.throws(() => normalizeManagedProfilePage({ items: [{ username: "no-guid" }] }), /RESPONSE_INVALID/, "缺 userGUID 视为无效");

  const options = normalizeManagedProfileFilterOptions({
    stores: [{ storeGuid: "s-1", storeCode: "1008", storeName: "Wallsend" }, { storeCode: "x" }],
    roles: [{ roleGuid: "r-1", roleName: "StoreStaff" }],
  });
  assert.equal(options.stores.length, 1, "缺 storeGuid 的分店被丢弃");
  assert.deepEqual(options.roles, [{ roleGuid: "r-1", roleName: "StoreStaff" }]);

  const d = normalizeManagedProfileDetail({ userGUID: "u-1", username: "kevin", hasBirthday: true });
  assert.equal(d.canEditEmploymentType, false, "缺省按不可改员工类型处理");
  assert.equal(d.hasBirthday, true);
  assert.equal(normalizeManagedProfileDetail({ userGUID: "u-1", canEditEmploymentType: true }).canEditEmploymentType, true);
}

// ───────── 列表查询、分页 ─────────
{
  assert.deepEqual(buildManagedProfileListParams(DEFAULT_MANAGED_PROFILE_FILTERS, 1), { page: 1, pageSize: 20 }, "无筛选只带分页");
  assert.deepEqual(
    buildManagedProfileListParams({ search: " kev ", storeGuid: "s-1", roleGuid: "r-1", pendingOnly: true }, 3),
    { page: 3, pageSize: 20, search: "kev", storeGuid: "s-1", roleGuid: "r-1" },
    "搜索去空白；待补全是本地过滤，不进查询",
  );
  assert.deepEqual(
    buildManagedProfileFilterKey({ search: "a", storeGuid: "s", roleGuid: "r", pendingOnly: true }),
    buildManagedProfileFilterKey({ search: "a", storeGuid: "s", roleGuid: "r", pendingOnly: false }),
    "待补全开关不换查询 key（不重新取数）",
  );

  const first = { items: [listItem({ userGUID: "a" }), listItem({ userGUID: "b" })], total: 3, page: 1, pageSize: 2 };
  const second = { items: [listItem({ userGUID: "B" }), listItem({ userGUID: "c" })], total: 3, page: 2, pageSize: 2 };
  assert.equal(resolveManagedProfileNextPage(first), 2, "还有数据时给下一页");
  assert.equal(resolveManagedProfileNextPage(second), undefined, "取完不再翻页");
  assert.equal(resolveManagedProfileNextPage({ items: [], total: 9, page: 1, pageSize: 2 }), undefined, "空页不再翻页（防死循环）");
  assert.deepEqual(flattenManagedProfilePages([first, second]).map((item) => item.userGUID), ["a", "b", "c"], "跨页累加并按 userGUID 去重（不分大小写）");
}

// ───────── 待补全 ─────────
{
  assert.deepEqual(resolveListItemMissing(listItem()), [], "银行 / Super / 手机 / 生日都有：已完善");
  assert.deepEqual(resolveListItemMissing(listItem({ bankBsb: "", bankAccountNumber: "" })), ["banking"], "缺银行");
  assert.deepEqual(resolveListItemMissing(listItem({ bankAccountNumber: "" })), ["banking"], "银行只填一半也算缺");
  assert.deepEqual(
    resolveListItemMissing(listItem({ bankBsb: "", bankAccountNumber: "", superannuationCompanyName: "", superannuationAccountNumber: "" })),
    ["banking", "superannuation"],
    "缺银行 + 缺 Super，顺序固定",
  );
  assert.deepEqual(resolveListItemMissing(listItem({ superannuationAccountNumber: "" })), ["superannuation"], "Super 只填机构也算缺");
  assert.deepEqual(resolveListItemMissing(listItem({ phone: "  " })), ["phone"], "空白手机号算缺");
  assert.deepEqual(resolveListItemMissing(listItem({ birthday: "" })), ["birthday"], "缺生日");
  assert.deepEqual(resolveListItemMissing(listItem({ birthday: "not-a-date" })), ["birthday"], "生日无法识别算缺");
  assert.deepEqual(resolveDetailMissing(detail({ hasBirthday: false })), ["birthday"], "详情用 hasBirthday 判断生日");
  assert.deepEqual(resolveDetailMissing(detail()), [], "详情完整");

  const items = [listItem({ userGUID: "ok" }), listItem({ userGUID: "no-bank", bankBsb: "", bankAccountNumber: "" })];
  assert.deepEqual(filterPendingItems(items, true).map((item) => item.userGUID), ["no-bank"], "待补全 chip 只留缺资料的行");
  assert.equal(filterPendingItems(items, false).length, 2, "关闭待补全显示全部");

  assert.equal(shouldAutoLoadMoreForPending({ pendingOnly: true, visibleCount: 2, hasNextPage: true, isFetching: false }), true, "命中太少且还有页：继续翻页");
  assert.equal(shouldAutoLoadMoreForPending({ pendingOnly: true, visibleCount: 8, hasNextPage: true, isFetching: false }), false, "已凑够：不再自动翻页");
  assert.equal(shouldAutoLoadMoreForPending({ pendingOnly: true, visibleCount: 0, hasNextPage: false, isFetching: false }), false, "没有下一页：不翻");
  assert.equal(shouldAutoLoadMoreForPending({ pendingOnly: true, visibleCount: 0, hasNextPage: true, isFetching: true }), false, "取数中：不重复触发");
  assert.equal(shouldAutoLoadMoreForPending({ pendingOnly: false, visibleCount: 0, hasNextPage: true, isFetching: false }), false, "没开待补全：不自动翻页");
}

// ───────── 列表行模型 ─────────
{
  const row = buildManagedProfileRow(listItem({ roleNames: ["StoreStaff", "StoreManager"], phone: "0412345678" }));
  assert.equal(row.name, "Kevin Zhang");
  assert.equal(row.initials, "KZ");
  assert.equal(row.role?.roleName, "StoreManager", "名字旁放权限最高的角色");
  assert.equal(row.phone, "0412 345 678", "澳洲手机号分段显示");
  assert.equal(row.pending, false);
  const bare = buildManagedProfileRow(listItem({ fullName: "", hasProfile: false, bankBsb: "", bankAccountNumber: "", phone: "", birthday: "", superannuationCompanyName: "", superannuationAccountNumber: "" }));
  assert.equal(bare.name, "kevin", "没有全名回落到账号");
  assert.equal(bare.noProfile, true);
  assert.equal(bare.pending, true);
  assert.deepEqual(bare.missing, ["banking", "superannuation", "phone", "birthday"]);
}

// ───────── 分店 chip：只有一家时默认选中并锁定 ─────────
{
  const one = [{ storeGuid: "s-1", storeCode: "1008", storeName: "Wallsend" }];
  const two = [...one, { storeGuid: "s-2", storeCode: "1009", storeName: "Kotara" }];
  assert.deepEqual(resolveStoreChip(one, ""), { storeGuid: "s-1", locked: true }, "单店：默认选中并锁定");
  assert.deepEqual(resolveStoreChip(one, "other"), { storeGuid: "s-1", locked: true }, "单店：忽略旧选择");
  assert.deepEqual(resolveStoreChip(two, ""), { storeGuid: "", locked: false }, "多店：默认全部");
  assert.deepEqual(resolveStoreChip(two, "S-2"), { storeGuid: "S-2", locked: false }, "多店：保留有效选择（不分大小写）");
  assert.deepEqual(resolveStoreChip(two, "gone"), { storeGuid: "", locked: false }, "选项里已不存在的值回落到全部");
  assert.deepEqual(resolveStoreChip([], ""), { storeGuid: "", locked: false }, "没有选项（尚未加载 / 无分店）：不锁定");
}

// ───────── 编辑：可编辑字段、改动、保存载荷 ─────────
{
  const base = buildManagedProfileEditBaseline(detail());
  assert.deepEqual(base, { phone: "0412345678", gender: "male", employmentType: "casual", address: "12 Cowper St" });
  assert.equal(buildManagedProfileEditBaseline(detail({ gender: "", employmentType: "" })).gender, "unknown", "未设置性别按 unknown");
  assert.equal(buildManagedProfileEditBaseline(detail({ gender: "Female" })).gender, "female", "性别大小写不敏感");
  assert.equal(buildManagedProfileEditBaseline(detail({ employmentType: "weird" })).employmentType, "", "未知员工类型按未设置");

  const fullAccess = { canEdit: true, canEditEmploymentType: true };
  const noPositionAccess = { canEdit: true, canEditEmploymentType: false };
  assert.deepEqual(resolveEditableFields(fullAccess), ["phone", "gender", "address", "employmentType"]);
  assert.deepEqual(resolveEditableFields(noPositionAccess), ["phone", "gender", "address"], "没有职位类型权限：员工类型不可改");
  assert.deepEqual(resolveEditableFields({ canEdit: false, canEditEmploymentType: true }), [], "没有 EditManagedStore：全部只读");

  // 没改动：载荷为空对象（保存按钮也据此禁用）。
  assert.deepEqual(buildManagedProfileUpdatePayload(base, { ...base }, fullAccess), {}, "没改动不提交任何字段");
  // 只提交改动过的字段。
  assert.deepEqual(
    buildManagedProfileUpdatePayload(base, { ...base, phone: " 0400 000 000 " }, fullAccess),
    { phone: "0400 000 000" },
    "只带改动过的键，值去首尾空白",
  );
  // 清空地址 / 手机号：显式传空字符串（后端靠「带没带」区分未改与清空）。
  assert.deepEqual(buildManagedProfileUpdatePayload(base, { ...base, address: "" }, fullAccess), { address: "" }, "清空地址显式传空字符串");
  assert.deepEqual(buildManagedProfileUpdatePayload(base, { ...base, address: "   " }, fullAccess), { address: "" }, "全空白等同清空");
  assert.deepEqual(buildManagedProfileUpdatePayload(base, { ...base, phone: "" }, fullAccess), { phone: "" }, "清空手机号显式传空字符串");
  // 员工类型：有权限才提交；没权限即使被改也不提交；没选（空串）不提交。
  assert.deepEqual(buildManagedProfileUpdatePayload(base, { ...base, employmentType: "fullTime" }, fullAccess), { employmentType: "fullTime" });
  assert.deepEqual(buildManagedProfileUpdatePayload(base, { ...base, employmentType: "fullTime" }, noPositionAccess), {}, "没有职位类型权限不提交员工类型");
  assert.deepEqual(buildManagedProfileUpdatePayload(base, { ...base, employmentType: "" }, fullAccess), {}, "不能把员工类型清成空");
  assert.deepEqual(buildManagedProfileUpdatePayload(base, { ...base, gender: "female" }, fullAccess), { gender: "female" });
  // 只读账号：任何改动都不提交。
  assert.deepEqual(buildManagedProfileUpdatePayload(base, { ...base, phone: "1" }, { canEdit: false, canEditEmploymentType: false }), {});
  assert.deepEqual(listManagedProfileChanges(base, { ...base, phone: "1", address: "x" }, fullAccess), ["phone", "address"], "改动清单按字段顺序");

  assert.deepEqual(validateManagedProfileEdit({ ...base, phone: "1".repeat(51) }), { phone: "tooLong" }, "手机号超过 50");
  assert.deepEqual(validateManagedProfileEdit({ ...base, address: "a".repeat(501) }), { address: "tooLong" }, "地址超过 500");
  assert.deepEqual(validateManagedProfileEdit({ ...base, address: "a".repeat(500) }), {}, "500 正好允许");
}

// ───────── 错误归类 ─────────
{
  assert.deepEqual(
    readManagedProfileErrorMeta({ response: { status: 403, data: { errorCode: "EMPLOYEE_PROFILE_MANAGED_SCOPE_FORBIDDEN", message: "无权" } } }),
    { status: 403, code: "EMPLOYEE_PROFILE_MANAGED_SCOPE_FORBIDDEN" },
    "axios 4xx：取 response.data.errorCode",
  );
  assert.deepEqual(readManagedProfileErrorMeta(Object.assign(new Error("x"), { code: "USER_NOT_FOUND" })), { code: "USER_NOT_FOUND" }, "业务信封错误：取 error.code");
  assert.deepEqual(readManagedProfileErrorMeta(null), {}, "非对象");
  assert.equal(classifyManagedProfileFailure({ status: 403, code: "EMPLOYEE_PROFILE_MANAGED_SCOPE_FORBIDDEN" }), "scope");
  assert.equal(classifyManagedProfileFailure({ status: 403, code: "EMPLOYEE_PROFILE_POSITION_TYPE_FORBIDDEN" }), "positionType");
  assert.equal(classifyManagedProfileFailure({ status: 404, code: "USER_NOT_FOUND" }), "notFound");
  assert.equal(classifyManagedProfileFailure({ status: 404 }), "notFound", "只有 404 状态也归为不存在");
  assert.equal(classifyManagedProfileFailure({ status: 403 }), "forbidden", "无业务码的 403");
  assert.equal(classifyManagedProfileFailure({ status: 500 }), "generic");
  assert.equal(classifyManagedProfileFailure({}), "generic");
}

// ───────── 敏感资料只读摘要 ─────────
{
  const rows = buildSensitiveSummary(detail({ identityId: "****9012", hasBirthday: true }));
  assert.deepEqual(rows, [
    { key: "bank", value: "062-000 ****1234" },
    { key: "super", value: "AustralianSuper ****5678" },
    { key: "birthdayIdentity", value: "**** ****9012" },
  ]);
  const empty = buildSensitiveSummary(detail({ bankBsb: "", bankAccountNumber: "", superannuationCompanyName: "", superannuationAccountNumber: "", hasBirthday: false, identityId: "" }));
  assert.deepEqual(empty.map((row) => row.value), [null, null, null], "没有数据：显示「未填写」（null）");
}

// ───────── 源码契约：接口路径、不暴露保存敏感资料的口子 ─────────
{
  const directory = dirname(fileURLToPath(import.meta.url));
  const read = (name: string) => readFileSync(resolve(directory, name), "utf8");
  const api = read("managed-profile-api.ts");
  assert.match(api, /\/employee-profiles\/managed/, "接口前缀");
  assert.match(api, /accountBoundRequestConfig\(actorGuid\)/, "写请求带 account 会话绑定头");
  assert.match(api, /apiClient\.put\(detailPath\(userGuid\), payload/, "保存走 PUT /managed/{userGuid}");
  const edit = read("managed-profile-edit-screen.tsx");
  assert.doesNotMatch(edit, /bankBsb\s*:|superannuation\w*\s*:|identityId\s*:/, "编辑页不提交任何敏感字段");
  assert.match(edit, /EmployeeProfiles\.EditManagedStore/, "保存前核对 EditManagedStore");
  assert.match(edit, /hasEditPermission \? \(\s*<FormFooter/, "没有 EditManagedStore 不显示保存按钮");
  const list = read("managed-profile-list.tsx");
  assert.match(list, /useIdentitySession\(PERMISSIONS\.EmployeeProfiles\.ViewManagedStore\)/, "列表只在 account 会话 + ViewManagedStore 时取数");
  const entry = read("managed-profile-entry.tsx");
  assert.match(entry, /if \(!allowed\) return null/, "入口在非 account 会话 / 无权限时整行隐藏");
}

console.log("managed-profile-logic.test.ts: ok");
