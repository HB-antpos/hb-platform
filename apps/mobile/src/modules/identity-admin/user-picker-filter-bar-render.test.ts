import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import Module from "node:module";
import { resolve } from "node:path";
import React, { act } from "react";

// 选人弹层「分店 / 角色 / 排序」筛选条与二级选择页的真实渲染测试（中文案取自真实语言包，键缺失会直接失败）：
// 没选：灰框带「分店 全部」；选了：只显示所选值并带清除；仅看已选时整条禁用；角色 / 排序页点一行回传对应值。
const zh = JSON.parse(readFileSync(resolve(__dirname, "../../locales/zh/screens/people.json"), "utf8")) as Record<string, unknown>;

function lookup(key: string, options?: Record<string, unknown>): string {
  const value = key
    .split(".")
    .reduce<unknown>((node, part) => (node && typeof node === "object" ? (node as Record<string, unknown>)[part] : undefined), zh);
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
    const Stub = (props: Record<string, unknown>) => React.createElement(type, props);
    Stub.displayName = type;
    return Stub;
  };
  mockModule("react-native", {
    StyleSheet: { create: (styles: object) => styles, hairlineWidth: 1 },
    View: host("View"),
    Pressable: host("Pressable"),
  });
  mockModule("react-native-paper", { Text: host("Text"), Icon: host("Icon") });
  mockModule(require.resolve("../../shared/i18n/use-app-translation"), {
    useAppTranslation: () => ({ t: lookup, language: "zh" }),
  });
  // 分店选择页自己有渲染测试，这里换成桩，只验证父级传给它的 props 与回传。
  mockModule(require.resolve("./admin-store-picker-page"), { AdminStorePickerPage: host("StorePicker") });

  const { createRoot } = await import("test-renderer");
  const { UserPickerFilterBar, UserPickerPage } = await import("./user-picker-filter-bar");
  const { DEFAULT_USER_PICKER_FILTERS } = await import("./user-picker-logic");

  const storeOptions = [{ value: "s-au", label: "Auburn · HB02" }, { value: "s-pa", label: "Parramatta · HB11" }];
  const roleOptions = [{ value: "r-sm", label: "Store Manager" }, { value: "r-ca", label: "Cashier" }];

  const render = async (element: React.ReactElement) => {
    const root = createRoot();
    await act(async () => { root.render(element); });
    const container = root.container as unknown as { queryAll: (predicate: (node: Node) => boolean) => Node[] };
    const all = (type: string) => container.queryAll((node) => node.type === type);
    return { all, texts: () => all("Text").map(textOf) };
  };
  const press = (node: Node) => act(async () => { node.props.onPress(); });

  // ---------- 没选：「分店 全部」「角色 全部」，排序 chip 只写当前排序（不写「排序」二字）----------
  {
    const opened: string[] = [];
    const cleared: string[] = [];
    const view = await render(React.createElement(UserPickerFilterBar, {
      filters: DEFAULT_USER_PICKER_FILTERS, storeOptions, roleOptions, showStore: true, showRole: true, disabled: false,
      onOpen: (page: string) => opened.push(page), onClear: (kind: string) => cleared.push(kind),
    }));
    assert.deepEqual(view.texts(), ["分店", "全部", "角色", "全部", "最近登录"]);
    const chips = view.all("Pressable");
    assert.equal(chips.length, 3, "没选值时没有清除按钮");
    await press(chips[0]);
    await press(chips[1]);
    await press(chips[2]);
    assert.deepEqual(opened, ["store", "role", "sort"], "三个 chip 分别打开分店 / 角色 / 排序页");
    assert.deepEqual(cleared, []);
  }

  // ---------- 选了分店和角色：chip 只显示所选值（分店去掉编号），带清除；排序 chip 显示所选排序 ----------
  {
    const cleared: string[] = [];
    const view = await render(React.createElement(UserPickerFilterBar, {
      filters: { storeGuid: "s-au", roleGuid: "r-sm", sort: "fullName" }, storeOptions, roleOptions, showStore: true, showRole: true, disabled: false,
      onOpen: () => undefined, onClear: (kind: string) => cleared.push(kind),
    }));
    assert.deepEqual(view.texts(), ["Auburn", "Store Manager", "姓名"], "选中后只显示值，不再重复「分店 / 角色」字样");
    const clears = view.all("Pressable").filter((node) => typeof node.props.accessibilityLabel === "string" && node.props.accessibilityLabel.startsWith("清除"));
    assert.deepEqual(clears.map((node) => node.props.accessibilityLabel), ["清除分店", "清除角色"]);
    await press(clears[0]);
    await press(clears[1]);
    assert.deepEqual(cleared, ["store", "role"]);
  }

  // ---------- 仅看已选：整条禁用；showStore / showRole 为 false 时对应 chip 不出现 ----------
  {
    const view = await render(React.createElement(UserPickerFilterBar, {
      filters: DEFAULT_USER_PICKER_FILTERS, storeOptions, roleOptions, showStore: false, showRole: true, disabled: true,
      onOpen: () => undefined, onClear: () => undefined,
    }));
    assert.deepEqual(view.texts(), ["角色", "全部", "最近登录"], "没有分店权限时不显示分店 chip");
    for (const chip of view.all("Pressable")) {
      assert.equal(chip.props.disabled, true);
      assert.equal(chip.props.accessibilityState.disabled, true);
    }
  }

  // ---------- 角色页：「全部角色」+ 各角色，选中项带对勾状态，点一行回传 roleGuid（全部 = 空串）----------
  {
    const changes: object[] = [];
    const view = await render(React.createElement(UserPickerPage, {
      kind: "role", filters: { ...DEFAULT_USER_PICKER_FILTERS, roleGuid: "r-ca" }, storeOptions, roleOptions, myStoreGuids: [],
      onChange: (next: object) => changes.push(next),
    }));
    assert.deepEqual(view.texts(), ["全部角色", "Store Manager", "Cashier"]);
    const rows = view.all("Pressable");
    assert.deepEqual(rows.map((row) => row.props.accessibilityState.selected), [false, false, true]);
    await press(rows[1]);
    await press(rows[0]);
    assert.deepEqual(changes, [{ roleGuid: "r-sm" }, { roleGuid: "" }]);
  }

  // ---------- 排序页：三种排序带说明，点一行回传 sort ----------
  {
    const changes: object[] = [];
    const view = await render(React.createElement(UserPickerPage, {
      kind: "sort", filters: DEFAULT_USER_PICKER_FILTERS, storeOptions, roleOptions, myStoreGuids: [],
      onChange: (next: object) => changes.push(next),
    }));
    assert.deepEqual(view.texts(), [
      "最近登录", "最近登录的排在前面（默认）", "账号", "按账号字母升序", "姓名", "按姓名字母升序",
    ]);
    const rows = view.all("Pressable");
    assert.deepEqual(rows.map((row) => row.props.accessibilityState.selected), [true, false, false]);
    await press(rows[2]);
    assert.deepEqual(changes, [{ sort: "fullName" }]);
  }

  // ---------- 分店页：交给现成的分店选择页，选中分店回传 storeGuid ----------
  {
    const changes: object[] = [];
    const view = await render(React.createElement(UserPickerPage, {
      kind: "store", filters: { ...DEFAULT_USER_PICKER_FILTERS, storeGuid: "s-pa" }, storeOptions, roleOptions, myStoreGuids: ["s-au"],
      onChange: (next: object) => changes.push(next),
    }));
    const picker = view.all("StorePicker")[0];
    assert.equal(picker.props.selected, "s-pa");
    assert.deepEqual(picker.props.myStoreGuids, ["s-au"]);
    assert.equal(picker.props.options.length, 2);
    await act(async () => { picker.props.onSelect("s-au"); });
    assert.deepEqual(changes, [{ storeGuid: "s-au" }]);
  }

  console.log("user-picker-filter-bar render tests passed");
}

run().catch((error) => {
  console.error(error);
  process.exit(1);
});
