import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import Module from "node:module";
import { resolve } from "node:path";
import React, { act } from "react";

// 「分配用户」弹层的真实渲染测试：行内容（姓名 + 账号、角色 · 分店）、固定高度、二级选择页原地切换、
// 「已选不在当前结果里」提示条、无结果时的条件说明与「清除筛选」、仅看已选时筛选条置灰。
// 文案取自真实语言包（identityPermissions + people），键缺失会直接失败。
const people = JSON.parse(readFileSync(resolve(__dirname, "../../locales/zh/screens/people.json"), "utf8")) as Record<string, unknown>;
const permissionCopy = JSON.parse(readFileSync(resolve(__dirname, "../../locales/zh/identityPermissions.json"), "utf8"));

function lookup(key: string, options?: Record<string, unknown>): string {
  const value = key
    .split(".")
    .reduce<unknown>((node, part) => (node && typeof node === "object" ? (node as Record<string, unknown>)[part] : undefined), people);
  if (typeof value !== "string") throw new Error(`中文语言包缺少键：${key}`);
  return value.replace(/\{\{(\w+)\}\}/g, (_, name: string) => String(options?.[name] ?? ""));
}

type Node = { type: string; props: Record<string, any>; children: (Node | string)[] };
const textOf = (node: Node | string): string => (typeof node === "string" ? node : node.children.map(textOf).join(""));

async function run() {
  Object.assign(globalThis, { __DEV__: false, IS_REACT_ACT_ENVIRONMENT: true, React });
  const mockModule = (name: string, exports: object) => {
    const filename = require.resolve(name);
    const module = new Module(filename);
    module.filename = filename;
    module.loaded = true;
    module.exports = exports;
    require.cache[filename] = module;
  };
  const rendererReactFilename = require.resolve("react", { paths: [require.resolve("test-renderer")] });
  if (rendererReactFilename !== require.resolve("react")) mockModule(rendererReactFilename, React);

  const host = (type: string) => {
    const Stub = (props: Record<string, unknown>) => React.createElement(type, props, props.children as React.ReactNode);
    Stub.displayName = type;
    return Stub;
  };
  mockModule("react-native", { StyleSheet: { create: (styles: object) => styles, hairlineWidth: 1 }, View: host("View"), Pressable: host("Pressable") });
  mockModule("react-native-paper", { Text: host("Text"), Icon: host("Icon"), Button: host("Button"), ActivityIndicator: host("Spinner") });
  mockModule(require.resolve("../../shared/i18n/use-app-translation"), { useAppTranslation: () => ({ t: lookup, language: "zh" }) });
  mockModule(require.resolve("../people"), {
    NoticeBox: host("NoticeBox"),
    PeopleTag: ({ label }: { label: string }) => React.createElement("Tag", null, label),
  });
  mockModule(require.resolve("../users/access-permission-presentation"), { localizeAccessRoleName: (name: string) => name });
  mockModule(require.resolve("./admin-visuals"), { AdminCheckBox: ({ checked }: { checked: boolean }) => React.createElement("Check", { checked }) });
  mockModule(require.resolve("./ui"), {
    SearchField: (props: Record<string, unknown>) => React.createElement("SearchField", props),
    AdminEmpty: ({ text }: { text: string }) => React.createElement("Empty", null, text),
    AdminError: host("Error"),
  });
  // 弹层外壳换成桩：把标题、返回、固定高度和各插槽原样摆出来，只验证传给它的内容。
  mockModule(require.resolve("./admin-bottom-sheet"), {
    AdminBottomSheet: (props: Record<string, any>) => React.createElement(
      "Sheet", { title: props.title, onBack: props.onBack, fullHeight: props.fullHeight }, props.header, props.notice, props.children, props.footer,
    ),
  });
  mockModule(require.resolve("./admin-store-picker-page"), { AdminStorePickerPage: host("StorePicker") });
  // 这两个模块会间接拉进 expo-router / 会话 store，Node 下加载不了；弹层只用到其中一个纯函数，换成等价的桩。
  mockModule(require.resolve("./permission-hooks"), {
    interpolatePermissionCopy: (value: string, params: Record<string, string | number> = {}) =>
      Object.entries(params).reduce((text, [key, replacement]) => text.replace(`{{${key}}}`, String(replacement)), value),
  });
  mockModule(require.resolve("./role-detail-logic"), { formatChangeBadge: (added: number, removed: number) => (added || removed ? `+${added} −${removed}` : "") });

  const { createRoot } = await import("test-renderer");
  const { AssignUsersSheet } = await import("./permission-assign-sheets");
  const { DEFAULT_USER_PICKER_FILTERS } = await import("./user-picker-logic");

  const storeOptions = [{ value: "s-au", label: "Auburn · HB02" }];
  const roleOptions = [{ value: "r-sm", label: "Store Manager" }];
  const users = [
    { userGUID: "u1", username: "lulu", email: "lulu@x.com", fullName: "Lulu Chen", isActive: true, roleNames: ["Store Manager"], storeNames: ["Auburn"] },
    { userGUID: "u2", username: "alice.w", email: "a@x.com", fullName: "Alice Wong", isActive: true, roleNames: ["Store Manager", "Cashier"], storeNames: ["Auburn", "Parramatta", "Liverpool"] },
    { userGUID: "u3", username: "tom", email: "tom@x.com", isActive: false },
  ];
  const peopleCopy = { save: "保存", saveWithBadge: "保存（{{badge}}）", cancel: "取消", onlySelected: "仅看已选", showAll: "显示全部" };

  const baseProps = () => ({
    p: peopleCopy, copy: permissionCopy, permissionName: "审核管理分店考勤", permissionCode: "Attendance.Approval.ReviewManagedStore",
    users, selectedGuids: ["u1", "u2"], selectedCount: 2, canWrite: true, search: "", onSearch: () => undefined,
    onlySelected: false, onToggleOnlySelected: () => undefined, onToggle: () => undefined,
    filters: DEFAULT_USER_PICKER_FILTERS, onFiltersChange: () => undefined, storeOptions, roleOptions, myStoreGuids: ["s-au"],
    showStoreFilter: true, showRoleFilter: true, filterOptionsFailed: false, onRetryFilterOptions: () => undefined,
    total: 37, hiddenSelectedCount: 0, loading: false, error: null, onRetry: () => undefined, hasMore: false, loadingMore: false,
    onLoadMore: () => undefined, delta: { added: 0, removed: 0 }, saving: false, onDismiss: () => undefined, onCancel: () => undefined, onSave: () => undefined,
  });
  const render = async (props: Record<string, unknown>) => {
    const root = createRoot();
    await act(async () => { root.render(React.createElement(AssignUsersSheet as any, props)); });
    const container = root.container as unknown as { queryAll: (predicate: (node: Node) => boolean) => Node[] };
    const all = (type: string) => container.queryAll((node) => node.type === type);
    const rows = () => all("Pressable").filter((node) => node.props.accessibilityRole === "checkbox");
    const buttons = () => all("Pressable").filter((node) => node.props.accessibilityRole === "button");
    return { all, rows, buttons, sheet: () => all("Sheet")[0], text: () => textOf(all("Sheet")[0]) };
  };
  const press = (node: Node) => act(async () => { node.props.onPress(); });

  // ---------- 主页：固定高度、搜索框停手通知、「共 37 人 · 已选 2」、行内容 ----------
  {
    const view = await render(baseProps());
    assert.equal(view.sheet().props.fullHeight, true, "弹层固定高度：结果条数变化时输入框不位移");
    assert.equal(view.sheet().props.title, "分配用户");
    assert.equal(view.all("SearchField")[0].props.emitDelayMs, 300, "搜索框停手 300ms 才通知页面");
    assert.ok(view.text().includes("共 37 人 · 已选 2"), view.text());
    assert.equal(view.all("Check").filter((node) => node.props.checked).length, 2);

    const [lulu, alice, tom] = view.rows().map(textOf);
    assert.ok(lulu.includes("Lulu Chen") && lulu.includes("lulu") && lulu.includes("Store Manager") && lulu.includes("Auburn"), lulu);
    assert.ok(!lulu.includes("等"), "只有一家分店时不写「等 N 家」");
    assert.ok(alice.includes("Store Manager") && alice.includes("+1") && alice.includes("Auburn") && alice.includes("等 2 家"), alice);
    // 没有角色 / 分店信息（只有 GUID 兜底的用户）：退回显示邮箱；停用账号带「停用」标签
    assert.ok(tom.includes("tom@x.com") && tom.includes(permissionCopy.disabled), tom);
    // 没有提示条
    assert.ok(!view.text().includes("不在当前结果中"));
  }

  // ---------- 点行回传 GUID；无写权限时整行禁用 ----------
  {
    const toggled: string[] = [];
    const view = await render({ ...baseProps(), onToggle: (guid: string) => toggled.push(guid) });
    await press(view.rows()[1]);
    assert.deepEqual(toggled, ["u2"]);
    const readonly = await render({ ...baseProps(), canWrite: false });
    assert.ok(readonly.rows().every((row) => row.props.disabled === true));
  }

  // ---------- 关键字高亮：命中片段单独成节点 ----------
  {
    const view = await render({ ...baseProps(), search: "lulu" });
    const highlighted = view.all("Text").filter((node) => node.props.style?.backgroundColor === "#FEF0C7").map(textOf);
    assert.deepEqual(highlighted, ["Lulu", "lulu"], "姓名和账号里命中 lulu 的部分各高亮一次");
  }

  // ---------- 已选但不在结果里：提示条；点「查看」等于仅看已选 ----------
  {
    let toggled = 0;
    const view = await render({ ...baseProps(), hiddenSelectedCount: 3, onToggleOnlySelected: () => { toggled += 1; } });
    assert.ok(view.text().includes("另有 3 名已选用户不在当前结果中"));
    const link = view.buttons().find((node) => textOf(node) === "查看")!;
    await press(link);
    assert.equal(toggled, 1);
  }

  // ---------- 仅看已选：计数回到「已选 N 名用户」，筛选条整体禁用 ----------
  {
    const view = await render({ ...baseProps(), onlySelected: true, total: undefined });
    assert.ok(view.text().includes("已选 2 名用户"), view.text());
    const chips = view.buttons().filter((node) => typeof node.props.accessibilityLabel === "string" && /^(分店|角色|排序)/.test(node.props.accessibilityLabel));
    assert.equal(chips.length, 3);
    assert.ok(chips.every((chip) => chip.props.disabled === true));
  }

  // ---------- 二级页：点「分店」chip 原地切到「选择分店」，选一家后回到主页并带上筛选 ----------
  {
    const changes: object[] = [];
    const view = await render({ ...baseProps(), onFiltersChange: (next: object) => changes.push(next) });
    const storeChip = view.buttons().find((node) => String(node.props.accessibilityLabel).startsWith("分店"))!;
    await press(storeChip);
    assert.equal(view.sheet().props.title, "选择分店", "二级页标题");
    assert.equal(typeof view.sheet().props.onBack, "function", "二级页有返回");
    assert.equal(view.sheet().props.fullHeight, true, "二级页同样固定高度，切换时不跳动");
    assert.equal(view.all("SearchField").length, 0, "二级页没有主页的搜索框");
    await act(async () => { view.all("StorePicker")[0].props.onSelect("s-au"); });
    assert.deepEqual(changes, [{ ...DEFAULT_USER_PICKER_FILTERS, storeGuid: "s-au" }]);
    assert.equal(view.sheet().props.title, "分配用户", "选完回到主页");
    assert.equal(view.all("SearchField").length, 1);
  }

  // ---------- 点返回回到主页，不改筛选 ----------
  {
    const changes: object[] = [];
    const view = await render({ ...baseProps(), onFiltersChange: (next: object) => changes.push(next) });
    await press(view.buttons().find((node) => String(node.props.accessibilityLabel).startsWith("角色"))!);
    assert.equal(view.sheet().props.title, "选择角色");
    await act(async () => { view.sheet().props.onBack(); });
    assert.equal(view.sheet().props.title, "分配用户");
    assert.deepEqual(changes, []);
  }

  // ---------- 清除 ✕：只清对应的那一项，保留另一项与排序 ----------
  {
    const changes: object[] = [];
    const filters = { storeGuid: "s-au", roleGuid: "r-sm", sort: "username" as const };
    const view = await render({ ...baseProps(), filters, onFiltersChange: (next: object) => changes.push(next) });
    await press(view.buttons().find((node) => node.props.accessibilityLabel === "清除分店")!);
    await press(view.buttons().find((node) => node.props.accessibilityLabel === "清除角色")!);
    assert.deepEqual(changes, [{ ...filters, storeGuid: "" }, { ...filters, roleGuid: "" }]);
  }

  // ---------- 无结果：说明当前条件，「清除筛选」一键回到全部（含关键字）----------
  {
    const changes: object[] = [];
    const searches: string[] = [];
    const filters = { storeGuid: "s-au", roleGuid: "r-sm", sort: "fullName" as const };
    const view = await render({
      ...baseProps(), users: [], filters, search: "lulu", total: 0,
      onFiltersChange: (next: object) => changes.push(next), onSearch: (value: string) => searches.push(value),
    });
    assert.ok(view.text().includes("没有匹配的用户"));
    assert.ok(view.text().includes("当前条件：分店 Auburn · 角色 Store Manager · 关键字 lulu"), view.text());
    await press(view.all("Button").find((node) => textOf(node) === "清除筛选")!);
    assert.deepEqual(changes, [{ ...filters, storeGuid: "", roleGuid: "" }], "清除分店和角色，排序保留");
    assert.deepEqual(searches, [""]);
  }

  // ---------- 没有任何筛选也没结果：只显示原来的空状态，不给「清除筛选」----------
  {
    const view = await render({ ...baseProps(), users: [] });
    assert.equal(view.all("Empty").length, 1);
    assert.equal(view.all("Button").filter((node) => textOf(node) === "清除筛选").length, 0);
  }

  // ---------- 筛选选项加载失败：就地提示，点一下重试 ----------
  {
    let retried = 0;
    const view = await render({ ...baseProps(), filterOptionsFailed: true, onRetryFilterOptions: () => { retried += 1; } });
    const failed = view.buttons().find((node) => textOf(node) === "筛选选项加载失败，请重试。")!;
    await press(failed);
    assert.equal(retried, 1);
  }

  console.log("assign-users-sheet render tests passed");
}

run().catch((error) => {
  console.error(error);
  process.exit(1);
});
