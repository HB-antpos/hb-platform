import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import Module from "node:module";
import { resolve } from "node:path";
import React, { act } from "react";

// 人员筛选「选择分店」二级页的真实渲染测试：
// 默认显示「全部分店 + 我的主分店 + 按首字母分组」；搜索时隐藏快捷项与分组标题，只给命中行；
// 点一行 / 一个快捷 chip 回传对应 GUID，点「全部分店」回传空串；已选项带选中状态。
const zh = JSON.parse(readFileSync(resolve(__dirname, "../../locales/zh/screens/people.json"), "utf8")) as Record<string, unknown>;

function lookup(key: string, options?: Record<string, unknown>): string {
  const value = key
    .split(".")
    .reduce<unknown>((node, part) => (node && typeof node === "object" ? (node as Record<string, unknown>)[part] : undefined), zh);
  if (typeof value !== "string") throw new Error(`中文语言包缺少键：${key}`);
  return value.replace(/\{\{(\w+)\}\}/g, (_, name: string) => String(options?.[name] ?? ""));
}

type Node = { type: string; props: Record<string, any>; children: (Node | string)[] };

function textOf(node: Node | string): string {
  if (typeof node === "string") return node;
  return node.children.map(textOf).join("");
}

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
  // 搜索框换成可直接驱动的桩，避免把整个人员页框架拉进来。
  mockModule(require.resolve("../people/people-frame"), { PeopleSearchBox: host("SearchBox") });

  const { createRoot } = await import("test-renderer");
  const { AdminStorePickerPage } = await import("./admin-store-picker-page");

  const root = createRoot();
  const container = root.container as unknown as { queryAll: (predicate: (node: Node) => boolean) => Node[] };
  const all = (type: string) => container.queryAll((node) => node.type === type);
  const texts = () => all("Text").map(textOf);
  const radios = () => all("Pressable").filter((node) => node.props.accessibilityRole === "radio");
  const search = (value: string) => act(() => all("SearchBox")[0].props.onChangeText(value));

  const options = [
    { value: "g-star", label: "Star Value Taree · 1038" },
    { value: "g-test", label: "Test · 2001" },
    { value: "g-tommy", label: "Tommy Bondi · 2005" },
    { value: "g-wall", label: "Wallsend · 1020" },
  ];
  const picked: string[] = [];
  const element = (selected = "") =>
    React.createElement(AdminStorePickerPage, {
      options,
      myStoreGuids: ["g-wall"],
      selected,
      onSelect: (guid: string) => picked.push(guid),
    });

  try {
    // 1. 默认：全部分店（带总数）、我的主分店、字母分组标题，没选分店时「全部分店」带选中状态。
    await act(() => root.render(element()));
    assert.ok(texts().includes("全部"), "第一行是全部分店");
    assert.ok(texts().includes(lookup("sheet.storeCount", { count: 4 })), "显示总家数");
    assert.ok(texts().includes(lookup("sheet.myStores")), "显示「我的主分店」");
    for (const letter of ["S", "T", "W"]) assert.ok(texts().includes(letter), `有分组标题 ${letter}`);
    assert.equal(radios()[0].props.accessibilityState.checked, true, "未选分店时「全部分店」为选中");

    // 2. 点分店行 / 快捷 chip / 全部分店：回传对应 GUID。
    const rowFor = (name: string) => radios().find((node) => textOf(node).startsWith(name))!;
    await act(() => rowFor("Tommy Bondi").props.onPress());
    await act(() => rowFor("Wallsend · 1020").props.onPress()); // 快捷 chip 的文字是完整标签
    await act(() => radios()[0].props.onPress());
    assert.deepEqual(picked, ["g-tommy", "g-wall", ""]);

    // 3. 已选项：行带选中状态。
    await act(() => root.render(element("g-test")));
    assert.equal(rowFor("Test").props.accessibilityState.checked, true);
    assert.equal(radios()[0].props.accessibilityState.checked, false, "选了分店后「全部分店」不再选中");

    // 4. 搜索（名称或编号，忽略大小写）：隐藏全部分店行、快捷项和分组标题，只给命中行。
    await search(" tom ");
    assert.deepEqual(radios().map((node) => textOf(node)), ["Tommy Bondi2005"]);
    assert.ok(!texts().includes(lookup("sheet.myStores")));
    assert.ok(!texts().includes("T"), "搜索时不显示分组标题");
    await search("1020");
    assert.deepEqual(radios().map((node) => textOf(node)), ["Wallsend1020"]);

    // 5. 没有命中：提示空结果。
    await search("zzz");
    assert.equal(radios().length, 0);
    assert.ok(texts().includes(lookup("sheet.storeEmpty")));
  } finally {
    await act(() => root.unmount());
  }
  console.log("admin store picker page render: passed");
}

run().catch((error) => {
  console.error(error);
  process.exit(1);
});
