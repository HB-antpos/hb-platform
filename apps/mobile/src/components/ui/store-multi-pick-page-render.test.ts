import assert from "node:assert/strict";
import Module from "node:module";
import React, { act } from "react";

// 筛选弹层里「选择分店」多选页（日结筛选、员工操作日志筛选共用）的真实渲染测试：
// 列表按字母分组、已选项带勾选态；点击回传分店编号；搜索时隐藏分组标题和「全部分店」行；
// 「全部分店」行 / 全选 / 清空只在调用方传了对应回调时出现，勾选逻辑完全交给调用方。
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
  const TextInput = Object.assign(host("TextInput"), { Icon: host("TextInputIcon") });
  mockModule("react-native", {
    StyleSheet: { create: (styles: object) => styles, hairlineWidth: 1 },
    View: host("View"),
    Pressable: host("Pressable"),
  });
  mockModule("react-native-paper", { Text: host("Text"), Icon: host("Icon"), Button: host("Button"), TextInput });

  const { createRoot } = await import("test-renderer");
  const { StoreMultiPickPage } = await import("./StoreMultiPickPage");

  const root = createRoot();
  const container = root.container as unknown as { queryAll: (predicate: (node: Node) => boolean) => Node[] };
  const all = (type: string) => container.queryAll((node) => node.type === type);
  const texts = () => all("Text").map(textOf);
  const rows = () => all("Pressable").filter((node) => node.props.accessibilityRole === "checkbox" || node.props.accessibilityRole === "radio");
  const search = (value: string) => act(() => all("TextInput")[0].props.onChangeText(value));

  // 故意乱序传入：页面按名称排序后再分组。
  const stores = [
    { storeCode: "1038", storeName: "Star Value Taree" },
    { storeCode: "2001", storeName: "Test" },
    { storeCode: "1020", storeName: "Wallsend" },
    { storeCode: "1024", storeName: "Bankstown" },
    { storeCode: "2005", storeName: "Tommy Bondi" },
    { storeCode: "1017", storeName: "Waratah" },
    { storeCode: "1018", storeName: "Toukley" },
  ] as never[];
  const toggled: string[] = [];
  let cleared = 0;
  let selectedAll = 0;
  const labels = {
    searchPlaceholder: "搜索分店",
    emptyText: "没有匹配的分店",
    selectedText: "已选 2 家",
    allRowLabel: "全部分店",
    allRowCaption: "共 7 家",
    selectAllLabel: "全选",
    clearLabel: "清空",
  };
  const element = (props: Record<string, unknown> = {}) =>
    React.createElement(StoreMultiPickPage, {
      stores,
      selected: ["1020", "2001"],
      labels,
      onToggle: (code: string) => toggled.push(code),
      onClear: () => { cleared += 1; },
      ...props,
    } as never);

  try {
    // 1. 默认：「全部分店」行 + 按首字母分组（B / S / T / W），已选项勾选；没选时「全部分店」不是选中态。
    await act(() => root.render(element()));
    for (const letter of ["B", "S", "T", "W"]) assert.ok(texts().includes(letter), `有分组标题 ${letter}`);
    assert.ok(texts().includes("共 7 家"));
    assert.equal(rows()[0].props.accessibilityState.checked, false, "已选了分店时「全部分店」行不是选中态");
    const rowFor = (name: string) => rows().find((node) => textOf(node).startsWith(name))!;
    assert.equal(rowFor("Wallsend").props.accessibilityState.checked, true);
    assert.equal(rowFor("Test").props.accessibilityState.checked, true);
    assert.equal(rowFor("Bankstown").props.accessibilityState.checked, false);
    assert.deepEqual(
      rows().slice(1).map((node) => textOf(node)),
      ["Bankstown1024", "Star Value Taree1038", "Test2001", "Tommy Bondi2005", "Toukley1018", "Wallsend1020", "Waratah1017"],
      "整体按名称排序，且传入顺序被打乱也不影响",
    );

    // 2. 点击回传分店编号；点「全部分店」行调用 onClear。
    await act(() => rowFor("Bankstown").props.onPress());
    await act(() => rows()[0].props.onPress());
    assert.deepEqual(toggled, ["1024"]);
    assert.equal(cleared, 1);

    // 3. 没选任何分店：「全部分店」行为选中态。
    await act(() => root.render(element({ selected: [] })));
    assert.equal(rows()[0].props.accessibilityState.checked, true);

    // 4. 搜索（名称或编号）：隐藏分组标题与「全部分店」行，只给命中行。
    await search(" 1020 ");
    assert.deepEqual(rows().map((node) => textOf(node)), ["Wallsend1020"]);
    assert.ok(!texts().includes("W"));
    await search("TO"); // 不区分大小写；Bankstown 也含 "to"
    assert.deepEqual(rows().map((node) => textOf(node)), ["Bankstown1024", "Tommy Bondi2005", "Toukley1018"]);
    await search("zzz");
    assert.equal(rows().length, 0);
    assert.ok(texts().includes("没有匹配的分店"));

    // 5. 全选 / 清空按钮只在传了 onSelectAll 时出现，并回调。
    await search("");
    assert.equal(all("Button").length, 0, "没传 onSelectAll 不显示头部按钮");
    await act(() => root.render(element({ onSelectAll: () => { selectedAll += 1; } })));
    const buttons = all("Button");
    assert.deepEqual(buttons.map(textOf), ["全选", "清空"]);
    await act(() => buttons[0].props.onPress());
    await act(() => buttons[1].props.onPress());
    assert.equal(selectedAll, 1);
    assert.equal(cleared, 2);

    // 6. 分店不超过 6 家时不显示搜索框。
    await act(() => root.render(element({ stores: stores.slice(0, 3) })));
    assert.equal(all("TextInput").length, 0);
  } finally {
    await act(() => root.unmount());
  }
  console.log("store multi pick page render: passed");
}

run().catch((error) => {
  console.error(error);
  process.exit(1);
});
