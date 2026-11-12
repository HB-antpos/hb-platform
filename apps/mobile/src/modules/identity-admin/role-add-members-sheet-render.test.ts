import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import Module from "node:module";
import { resolve } from "node:path";
import React, { act } from "react";

// 「角色 → 添加成员」弹层接入分店 / 角色 / 排序筛选后的渲染测试：
// 固定高度、搜索框停手通知、筛选条、二级页原地切换、有筛选且无结果时给「清除筛选」。
const people = JSON.parse(readFileSync(resolve(__dirname, "../../locales/zh/screens/people.json"), "utf8")) as Record<string, unknown>;
const roleCopy = JSON.parse(readFileSync(resolve(__dirname, "../../locales/zh/identityRoles.json"), "utf8"));

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
  mockModule("react-native", {
    StyleSheet: { create: (styles: object) => styles, hairlineWidth: 1 },
    View: host("View"), Pressable: host("Pressable"), FlatList: host("FlatList"),
  });
  mockModule("react-native-paper", { Text: host("Text"), Icon: host("Icon"), Button: host("Button"), IconButton: host("IconButton"), ActivityIndicator: host("Spinner") });
  mockModule(require.resolve("../../shared/i18n/use-app-translation"), { useAppTranslation: () => ({ t: lookup, language: "zh" }) });
  mockModule(require.resolve("../people"), {
    NoticeBox: host("NoticeBox"), PeopleTag: host("Tag"), PersonAvatar: host("Avatar"), PersonRow: host("PersonRow"),
    resolvePersonName: (input: { fullName?: string; username: string }) => ({ primary: input.fullName || input.username, nickname: "" }),
  });
  mockModule(require.resolve("./admin-visuals"), { AdminCheckBox: ({ checked }: { checked: boolean }) => React.createElement("Check", { checked }) });
  mockModule(require.resolve("./ui"), {
    SearchField: (props: Record<string, unknown>) => React.createElement("SearchField", props),
    AdminEmpty: ({ text }: { text: string }) => React.createElement("Empty", null, text),
    AdminError: host("Error"),
  });
  mockModule(require.resolve("./admin-bottom-sheet"), {
    AdminBottomSheet: (props: Record<string, any>) => React.createElement(
      "Sheet", { title: props.title, onBack: props.onBack, fullHeight: props.fullHeight }, props.header, props.notice, props.children, props.footer,
    ),
  });
  mockModule(require.resolve("./admin-store-picker-page"), { AdminStorePickerPage: host("StorePicker") });

  const { createRoot } = await import("test-renderer");
  const { RoleAddMembersSheet } = await import("./role-detail-members");
  const { DEFAULT_USER_PICKER_FILTERS } = await import("./user-picker-logic");

  const users = [
    { userGUID: "u1", username: "lulu", email: "lulu@x.com", fullName: "Lulu Chen", isActive: true },
    { userGUID: "u2", username: "alice", email: "a@x.com", fullName: "Alice Wong", isActive: true },
  ];
  const baseProps = () => ({
    legacy: roleCopy, p: {}, roleName: "Store Manager", search: "", onSearch: () => undefined, users,
    loading: false, error: null, onRetry: () => undefined, hasMore: false, loadingMore: false, onLoadMore: () => undefined,
    selected: ["u1"], onToggle: () => undefined,
    filters: DEFAULT_USER_PICKER_FILTERS, onFiltersChange: () => undefined,
    storeOptions: [{ value: "s-au", label: "Auburn · HB02" }], roleOptions: [{ value: "r-ca", label: "Cashier" }], myStoreGuids: [],
    showStoreFilter: true, showRoleFilter: true, filterOptionsFailed: false, onRetryFilterOptions: () => undefined,
    onCancel: () => undefined, onSubmit: () => undefined, submitting: false,
  });
  const render = async (props: Record<string, unknown>) => {
    const root = createRoot();
    await act(async () => { root.render(React.createElement(RoleAddMembersSheet as any, props)); });
    const container = root.container as unknown as { queryAll: (predicate: (node: Node) => boolean) => Node[] };
    const all = (type: string) => container.queryAll((node) => node.type === type);
    const buttons = () => all("Pressable").filter((node) => node.props.accessibilityRole === "button");
    return { all, buttons, sheet: () => all("Sheet")[0], text: () => textOf(all("Sheet")[0]) };
  };
  const press = (node: Node) => act(async () => { node.props.onPress(); });

  // ---------- 主页：固定高度、搜索框停手 300ms 才通知、筛选条三个 chip ----------
  {
    const view = await render(baseProps());
    assert.equal(view.sheet().props.title, "添加成员");
    assert.equal(view.sheet().props.fullHeight, true);
    assert.equal(view.all("SearchField")[0].props.emitDelayMs, 300);
    const chips = view.buttons().map((node) => String(node.props.accessibilityLabel));
    assert.deepEqual(chips, ["分店 全部", "角色 全部", "排序 最近登录"]);
  }

  // ---------- 点「角色」chip 原地切到「选择角色」，选 Cashier 后回到主页并带上筛选 ----------
  {
    const changes: object[] = [];
    const view = await render({ ...baseProps(), onFiltersChange: (next: object) => changes.push(next) });
    await press(view.buttons().find((node) => String(node.props.accessibilityLabel).startsWith("角色"))!);
    assert.equal(view.sheet().props.title, "选择角色");
    assert.equal(view.all("SearchField").length, 0);
    const cashier = view.all("Pressable").find((node) => node.props.accessibilityRole === "radio" && textOf(node).includes("Cashier"))!;
    await press(cashier);
    assert.deepEqual(changes, [{ ...DEFAULT_USER_PICKER_FILTERS, roleGuid: "r-ca" }]);
    assert.equal(view.sheet().props.title, "添加成员");
  }

  // ---------- 有筛选且无可添加用户：给「清除筛选」，一并清掉关键字 ----------
  {
    const changes: object[] = [];
    const searches: string[] = [];
    const filters = { storeGuid: "s-au", roleGuid: "", sort: "username" as const };
    const view = await render({
      ...baseProps(), users: [], filters,
      onFiltersChange: (next: object) => changes.push(next), onSearch: (value: string) => searches.push(value),
    });
    assert.ok(view.all("Empty").length === 1);
    await press(view.all("Button").find((node) => textOf(node) === "清除筛选")!);
    assert.deepEqual(changes, [{ ...filters, storeGuid: "", roleGuid: "" }]);
    assert.deepEqual(searches, [""]);
  }

  // ---------- 没有任何筛选也没用户：不出现「清除筛选」----------
  {
    const view = await render({ ...baseProps(), users: [] });
    assert.equal(view.all("Button").filter((node) => textOf(node) === "清除筛选").length, 0);
  }

  console.log("role-add-members-sheet render tests passed");
}

run().catch((error) => {
  console.error(error);
  process.exit(1);
});
