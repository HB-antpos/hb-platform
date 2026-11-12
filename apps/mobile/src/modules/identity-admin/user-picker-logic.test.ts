import assert from "node:assert/strict";
import {
  DEFAULT_USER_PICKER_FILTERS,
  USER_PICKER_PAGE_SIZE,
  USER_PICKER_SORT_ORDER,
  buildUserPickerFilterKey,
  buildUserPickerQuery,
  countSelectedOutsideResults,
  hasActiveUserPickerFilters,
  highlightParts,
  resolveUserPickerName,
  summarizeNames,
} from "./user-picker-logic";

// 选人弹层的筛选 → 查询参数：与「人员管理」列表同一套参数名，状态固定为 all（不传 isActive）。
{
  const query = buildUserPickerQuery("  lulu ", { storeGuid: "s-1", roleGuid: "r-1", sort: "fullName" }, 2);
  assert.deepEqual(query, {
    page: 2,
    pageSize: USER_PICKER_PAGE_SIZE,
    search: "lulu",
    storeGuid: "s-1",
    roleGuid: "r-1",
    sortBy: "fullName",
    sortDirection: "asc",
  });
  assert.equal("isActive" in query, false, "选人弹层不按启用状态过滤，停用账号照旧显示并标「已禁用」");
}

// 默认筛选：不传 storeGuid / roleGuid / search，排序 = 最近登录（新 → 旧）；每页条数可覆盖（添加成员弹层用 100）。
{
  const query = buildUserPickerQuery("", DEFAULT_USER_PICKER_FILTERS, 1, 100);
  assert.deepEqual(query, { page: 1, pageSize: 100, sortBy: "lastLoginAt", sortDirection: "desc" });
  assert.deepEqual(USER_PICKER_SORT_ORDER, ["lastLoginAt", "username", "fullName"], "排序选项与人员管理列表一致");
}

// 查询键：搜索词两端空白不影响；分店 / 角色 / 排序任一变化键都不同（react-query 据此从第一页重取）。
{
  const base = buildUserPickerFilterKey("lulu", DEFAULT_USER_PICKER_FILTERS);
  assert.deepEqual(buildUserPickerFilterKey(" lulu ", DEFAULT_USER_PICKER_FILTERS), base);
  const variants = [
    buildUserPickerFilterKey("lulu", { ...DEFAULT_USER_PICKER_FILTERS, storeGuid: "s-1" }),
    buildUserPickerFilterKey("lulu", { ...DEFAULT_USER_PICKER_FILTERS, roleGuid: "r-1" }),
    buildUserPickerFilterKey("lulu", { ...DEFAULT_USER_PICKER_FILTERS, sort: "username" }),
    buildUserPickerFilterKey("lulu2", DEFAULT_USER_PICKER_FILTERS),
  ];
  for (const key of variants) assert.notDeepEqual(key, base);
  assert.equal(new Set(variants.map((key) => key.join("|"))).size, variants.length, "不同筛选的键互不相同");
}

// 「是否在筛选」：只看分店 / 角色，排序不算（它只改顺序不改结果集）。
{
  assert.equal(hasActiveUserPickerFilters(DEFAULT_USER_PICKER_FILTERS), false);
  assert.equal(hasActiveUserPickerFilters({ ...DEFAULT_USER_PICKER_FILTERS, sort: "username" }), false);
  assert.equal(hasActiveUserPickerFilters({ ...DEFAULT_USER_PICKER_FILTERS, storeGuid: "s-1" }), true);
  assert.equal(hasActiveUserPickerFilters({ ...DEFAULT_USER_PICKER_FILTERS, roleGuid: "r-1" }), true);
}

// 已选但不在当前结果里的人数：结果没加载完时不提示（可能只是在没加载的页里）。
{
  const selected = ["a", "b", "c", "d"];
  assert.equal(countSelectedOutsideResults(selected, ["a", "x"], true), 3);
  assert.equal(countSelectedOutsideResults(selected, ["a", "b", "c", "d", "x"], true), 0);
  assert.equal(countSelectedOutsideResults(selected, [], true), 4);
  assert.equal(countSelectedOutsideResults(selected, ["a"], false), 0, "还有下一页时不提示");
  assert.equal(countSelectedOutsideResults([], ["a"], true), 0);
}

// 行内摘要：只取第一项，其余记个数；空 / 全空白 / 缺省都当没有。
{
  assert.deepEqual(summarizeNames(["Auburn", "Parramatta", "Liverpool"]), { first: "Auburn", more: 2 });
  assert.deepEqual(summarizeNames(["Cashier"]), { first: "Cashier", more: 0 });
  assert.deepEqual(summarizeNames([" ", "Cashier"]), { first: "Cashier", more: 0 }, "空白项被忽略");
  assert.equal(summarizeNames([]), null);
  assert.equal(summarizeNames(undefined), null);
}

// 行内名字：姓名与账号不同 → 姓名为主、账号为副；相同（忽略大小写）或没有姓名 → 只显示账号。
{
  assert.deepEqual(resolveUserPickerName({ username: "lulu", fullName: "Lulu Chen" }), { primary: "Lulu Chen", secondary: "lulu" });
  assert.deepEqual(resolveUserPickerName({ username: "lulu", fullName: "LULU" }), { primary: "lulu", secondary: "" });
  assert.deepEqual(resolveUserPickerName({ username: "lulu" }), { primary: "lulu", secondary: "" });
  assert.deepEqual(resolveUserPickerName({ username: "lulu", fullName: "  " }), { primary: "lulu", secondary: "" });
}

// 命中高亮：不区分大小写；关键字里的正则符号按字面处理；没有关键字 / 没命中时整段不高亮。
{
  assert.deepEqual(highlightParts("Lulu Chen", "lulu"), [{ text: "Lulu", match: true }, { text: " Chen", match: false }]);
  assert.deepEqual(highlightParts("lulu.s", "LU"), [
    { text: "lu", match: true }, { text: "lu", match: true }, { text: ".s", match: false },
  ]);
  assert.deepEqual(highlightParts("a.b", "."), [{ text: "a", match: false }, { text: ".", match: true }, { text: "b", match: false }], "点号不是通配符");
  assert.deepEqual(highlightParts("a(b", "("), [{ text: "a", match: false }, { text: "(", match: true }, { text: "b", match: false }], "括号不会让正则报错");
  assert.deepEqual(highlightParts("Alice", ""), [{ text: "Alice", match: false }]);
  assert.deepEqual(highlightParts("Alice", "  "), [{ text: "Alice", match: false }]);
  assert.deepEqual(highlightParts("Alice", "zz"), [{ text: "Alice", match: false }]);
  assert.deepEqual(highlightParts("陈小明", "小"), [{ text: "陈", match: false }, { text: "小", match: true }, { text: "明", match: false }]);
}

console.log("user-picker-logic tests passed");
