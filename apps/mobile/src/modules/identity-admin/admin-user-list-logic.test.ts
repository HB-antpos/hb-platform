import type { IdentityUser } from "./types";
import {
  ADMIN_USER_PAGE_SIZE,
  DEFAULT_ADMIN_USER_FILTERS,
  buildAdminCountQuery,
  buildAdminFilterKey,
  buildAdminUserQuery,
  flattenUserPages,
  isAdminSheetDefault,
  isUnfilteredAdminList,
  resetAdminSheetFilters,
  resolveAdminSort,
  resolveNextPage,
  statusFilterToIsActive,
} from "./admin-user-list-logic";

function assertEqual(actual: unknown, expected: unknown, label: string) {
  if (JSON.stringify(actual) !== JSON.stringify(expected)) {
    throw new Error(`${label}: expected ${JSON.stringify(expected)}, got ${JSON.stringify(actual)}`);
  }
}

function user(id: string): IdentityUser {
  return {
    userGUID: id,
    username: id,
    email: `${id}@example.com`,
    isActive: true,
    createdAt: "",
    updatedAt: "",
    roleNames: [],
    storeNames: [],
    roles: [],
    stores: [],
    permissions: [],
    exactPermissions: [],
  };
}

// ── 排序预设 ──
assertEqual(resolveAdminSort("lastLoginAt"), { sortBy: "lastLoginAt", sortDirection: "desc" }, "最近登录新的在前");
assertEqual(resolveAdminSort("username"), { sortBy: "username", sortDirection: "asc" }, "账号升序");
assertEqual(resolveAdminSort("fullName"), { sortBy: "fullName", sortDirection: "asc" }, "姓名升序");

// ── 状态 → isActive ──
assertEqual(statusFilterToIsActive("all"), undefined, "全部不传 isActive");
assertEqual(statusFilterToIsActive("active"), true, "启用");
assertEqual(statusFilterToIsActive("inactive"), false, "停用");

// ── 查询参数 ──
assertEqual(
  buildAdminUserQuery(DEFAULT_ADMIN_USER_FILTERS, 1),
  { page: 1, pageSize: ADMIN_USER_PAGE_SIZE, sortBy: "lastLoginAt", sortDirection: "desc" },
  "默认筛选只带分页与排序",
);
assertEqual(
  buildAdminUserQuery({ search: "  grace ", storeGuid: "S1", roleGuid: "R1", status: "inactive", sort: "username" }, 3, 50),
  { page: 3, pageSize: 50, search: "grace", storeGuid: "S1", roleGuid: "R1", isActive: false, sortBy: "username", sortDirection: "asc" },
  "全部筛选都带上，搜索词去空白",
);
assertEqual(
  buildAdminUserQuery({ ...DEFAULT_ADMIN_USER_FILTERS, search: "   " }, 1).search,
  undefined,
  "纯空白搜索不传",
);

// ── 计数查询：忽略状态与排序，只取 total ──
assertEqual(
  buildAdminCountQuery({ search: "x", storeGuid: "S1", roleGuid: "", status: "active", sort: "fullName" }, false),
  { page: 1, pageSize: 1, search: "x", storeGuid: "S1", isActive: false },
  "计数查询不带排序与状态筛选，isActive 单独指定",
);
assertEqual(buildAdminCountQuery(DEFAULT_ADMIN_USER_FILTERS), { page: 1, pageSize: 1 }, "无筛选无 isActive");

// ── 弹层重置 / 默认判断 ──
const dirty = { search: "grace", storeGuid: "S1", roleGuid: "R1", status: "active" as const, sort: "username" as const };
assertEqual(isAdminSheetDefault(dirty), false, "有筛选不是默认");
assertEqual(resetAdminSheetFilters(dirty), { ...DEFAULT_ADMIN_USER_FILTERS, search: "grace" }, "重置保留搜索关键词");
assertEqual(isAdminSheetDefault(resetAdminSheetFilters(dirty)), true, "重置后是默认");
assertEqual(isUnfilteredAdminList(DEFAULT_ADMIN_USER_FILTERS), true, "默认且无搜索 = 总人数口径");
assertEqual(isUnfilteredAdminList({ ...DEFAULT_ADMIN_USER_FILTERS, search: "a" }), false, "有搜索不是总人数");
assertEqual(isUnfilteredAdminList({ ...DEFAULT_ADMIN_USER_FILTERS, status: "active" }), false, "有状态筛选不是总人数");

// ── 筛选键：变化即重置分页 ──
assertEqual(buildAdminFilterKey({ ...DEFAULT_ADMIN_USER_FILTERS, search: " a " }), ["a", "", "", "all", "lastLoginAt"], "搜索词去空白进 key");
assertEqual(
  JSON.stringify(buildAdminFilterKey(DEFAULT_ADMIN_USER_FILTERS)) === JSON.stringify(buildAdminFilterKey({ ...DEFAULT_ADMIN_USER_FILTERS })),
  true,
  "相同筛选 key 相同",
);

// ── 分页累加与去重 ──
assertEqual(
  flattenUserPages([{ items: [user("a"), user("b")] }, { items: [user("b"), user("c")] }]).map((item) => item.userGUID),
  ["a", "b", "c"],
  "跨页重复的用户只留一条，顺序按首次出现",
);
assertEqual(flattenUserPages([]), [], "没有页返回空数组");
{
  // 累加 / 去重不能丢掉 loginEnabled：列表行靠它标出「不能登录」；缺省（未知）原样保持 undefined。
  const restricted = { ...user("r"), loginEnabled: false };
  const allowed = { ...user("a"), loginEnabled: true };
  const merged = flattenUserPages([{ items: [restricted, user("u")] }, { items: [allowed] }]);
  assertEqual(merged.map((item) => item.loginEnabled), [false, undefined, true], "loginEnabled 逐行保留，缺省仍是 undefined");
}

// ── 不新增「不能登录」筛选：接口不支持按 loginEnabled 筛选，查询参数里绝不能出现它 ──
{
  const query = buildAdminUserQuery({ search: "kevin", storeGuid: "S1", roleGuid: "R1", status: "active", sort: "lastLoginAt" }, 1);
  assertEqual(Object.keys(query).some((key) => /login(Enabled)?$/i.test(key) && key !== "lastLoginAt"), false, "列表查询没有 loginEnabled 筛选参数");
  assertEqual(Object.keys(buildAdminCountQuery(DEFAULT_ADMIN_USER_FILTERS, true)).includes("loginEnabled"), false, "计数查询同样没有");
}

// ── 下一页 ──
assertEqual(resolveNextPage({ page: 1, pageSize: 20, total: 45, totalPages: 3, items: [user("a")] }), 2, "还有下一页");
assertEqual(resolveNextPage({ page: 3, pageSize: 20, total: 45, totalPages: 3, items: [user("a")] }), undefined, "最后一页");
assertEqual(resolveNextPage({ page: 1, pageSize: 20, total: 45, totalPages: 0, items: [user("a")] }), 2, "totalPages 缺失时按 total 推算");
assertEqual(resolveNextPage({ page: 2, pageSize: 20, total: 40, totalPages: 0, items: [user("a")] }), undefined, "按 total 推算也能判断末页");
assertEqual(resolveNextPage({ page: 2, pageSize: 20, total: 100, totalPages: 5, items: [] }), undefined, "空页不再翻页，避免无限请求");

console.log("admin-user-list-logic.test.ts: ok");
