import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import Module from "node:module";
import { resolve } from "node:path";
import React, { act } from "react";

// 分类列表的「上移 / 下移」兜底与点击进入：不依赖手势，拖不动或使用读屏时靠它排序。
// PanResponder 的真实拖动无法在 Node 里模拟（需要设备目测），这里验证按钮 / 无障碍动作 / 点击回调与边界禁用。
const zh = JSON.parse(readFileSync(resolve(__dirname, "../../locales/zh/screens/specialProductCategories.json"), "utf8")) as Record<string, unknown>;

function lookup(key: string, options?: Record<string, unknown>): string {
  const value = key
    .split(".")
    .reduce<unknown>((node, part) => (node && typeof node === "object" ? (node as Record<string, unknown>)[part] : undefined), zh);
  if (typeof value !== "string") throw new Error(`中文语言包缺少键：${key}`);
  return value.replace(/\{\{(\w+)\}\}/g, (_, name: string) => String(options?.[name] ?? ""));
}

type Node = { type: string; props: Record<string, any>; children: (Node | string)[] };

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
  class AnimatedValue {
    constructor(public value: number) {}
    setValue(next: number) {
      this.value = next;
    }
  }
  mockModule("react-native", {
    StyleSheet: { create: (styles: object) => styles, hairlineWidth: 1 },
    View: host("View"),
    Pressable: host("Pressable"),
    PanResponder: { create: () => ({ panHandlers: {} }) },
    Animated: {
      Value: AnimatedValue,
      View: host("AnimatedView"),
      timing: () => ({ start: () => undefined }),
    },
  });
  mockModule("react-native-paper", { Text: host("Text"), Icon: host("Icon"), IconButton: host("IconButton") });
  mockModule(require.resolve("../../shared/i18n/use-app-translation"), {
    useAppTranslation: () => ({ t: lookup, language: "zh" }),
  });
  // 共用小组件只用到 ColorDot / SC_COLORS，换成桩避免拉入权限与 store。
  mockModule(require.resolve("./shared"), { ColorDot: host("ColorDot"), SC_COLORS: { selectedBg: "#fff" } });

  const { createRoot } = await import("test-renderer");
  const { DraggableCategoryList } = await import("./DraggableCategoryList");
  const root = createRoot();
  const container = root.container as unknown as { queryAll: (predicate: (node: Node) => boolean) => Node[] };
  const all = (type: string) => container.queryAll((node) => node.type === type);

  const categories = [
    { id: "a", name: "购物袋", color: null, sortOrder: 1, itemCount: 8 },
    { id: "b", name: "气球", color: "#1677FF", sortOrder: 2, itemCount: 22 },
    { id: "c", name: "贺卡", color: null, sortOrder: 3, itemCount: 17 },
  ];
  const reorders: string[][] = [];
  const opened: string[] = [];
  const element = (reorderMode: boolean, list = categories) =>
    React.createElement(DraggableCategoryList, {
      categories: list,
      reorderMode,
      onOpen: (item: { id: string }) => opened.push(item.id),
      onReorder: (ids: string[]) => reorders.push(ids),
    });
  const button = (label: string) => all("IconButton").find((node) => node.props.accessibilityLabel === label);
  const rows = () => all("Pressable").filter((node) => node.props.accessibilityRole === "button" && node.props.accessibilityLabel?.includes("个商品"));

  try {
    // 1. 普通模式：没有上移 / 下移按钮；点行进入分类；行带「上移 / 下移」无障碍动作（读屏用户不需要手势）。
    await act(() => root.render(element(false)));
    assert.equal(all("IconButton").length, 0);
    assert.equal(rows().length, 3);
    await act(() => rows()[1].props.onPress());
    assert.deepEqual(opened, ["b"]);
    assert.deepEqual(rows()[1].props.accessibilityActions.map((action: { name: string }) => action.name), ["moveUp", "moveDown"]);
    await act(() => rows()[1].props.onAccessibilityAction({ nativeEvent: { actionName: "moveDown" } }));
    assert.deepEqual(reorders.pop(), ["a", "c", "b"]);
    await act(() => rows()[1].props.onAccessibilityAction({ nativeEvent: { actionName: "moveUp" } }));
    assert.deepEqual(reorders.pop(), ["b", "a", "c"]);
    await act(() => rows()[0].props.onAccessibilityAction({ nativeEvent: { actionName: "moveUp" } }));
    assert.equal(reorders.length, 0, "第一行上移是空操作，不提交");

    // 2. 排序模式：每行有上移 / 下移按钮；首行不能上移、末行不能下移；点按钮回传完整新顺序；点行不再进入分类。
    await act(() => root.render(element(true)));
    assert.equal(all("IconButton").length, 6);
    assert.equal(button("上移「购物袋」")?.props.disabled, true);
    assert.equal(button("下移「贺卡」")?.props.disabled, true);
    assert.equal(button("上移「气球」")?.props.disabled, false);
    await act(() => button("下移「购物袋」")!.props.onPress());
    assert.deepEqual(reorders.pop(), ["b", "a", "c"]);
    await act(() => button("上移「贺卡」")!.props.onPress());
    assert.deepEqual(reorders.pop(), ["a", "c", "b"]);
    assert.equal(rows()[0].props.disabled, true, "排序模式下行本身不可点");

    // 3. 只有一个分类：没有可移动的方向（按钮都禁用）。
    await act(() => root.render(element(true, categories.slice(0, 1))));
    assert.equal(button("上移「购物袋」")?.props.disabled, true);
    assert.equal(button("下移「购物袋」")?.props.disabled, true);
  } finally {
    await act(() => root.unmount());
  }
}

run().then(
  () => process.exit(0),
  (error) => {
    console.error(error);
    process.exit(1);
  },
);
