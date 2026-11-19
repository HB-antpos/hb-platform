import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import Module from "node:module";
import { resolve } from "node:path";
import React, { act } from "react";

// 打卡页「定位使用说明」的渲染测试：真实组件 + 真实中文文案，只把 react-native / paper / 翻译换成桩。
// 班中后台定位已取消，这里钉住：说明卡讲「使用 App 期间才获取位置：扫码打卡 / 登录、上班期间的位置请求（会通知并留记录），不在后台使用」，
// 详情弹层只读（没有「同意并继续」），也不再出现「已开启 / 已停止 / 补传 / 自动停止」这些旧口径；
// 所有渲染出的文字都不含「每隔几分钟记录」类的采集频率表述。
const bundles = new Map<string, Record<string, unknown>>();
function bundle(namespace: string) {
  if (!bundles.has(namespace)) {
    const file = namespace === "common" ? "../../locales/zh/common.json" : `../../locales/zh/screens/${namespace}.json`;
    bundles.set(namespace, JSON.parse(readFileSync(resolve(__dirname, file), "utf8")) as Record<string, unknown>);
  }
  return bundles.get(namespace) as Record<string, unknown>;
}

/** 简化版 t：默认命名空间 attendance，带 "ns:" 前缀的取对应命名空间；缺键返回键本身（让断言能暴露缺键）。 */
function lookup(key: string): string {
  const [namespace, path] = key.includes(":") ? (key.split(":") as [string, string]) : (["attendance", key] as const);
  const value = path
    .split(".")
    .reduce<unknown>(
      (node, part) => (node && typeof node === "object" ? (node as Record<string, unknown>)[part] : undefined),
      bundle(namespace),
    );
  return typeof value === "string" ? value : key;
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
    StyleSheet: { create: (styles: object) => styles, hairlineWidth: 1, absoluteFillObject: {} },
    View: host("View"),
    Text: host("NativeText"),
    Pressable: host("Pressable"),
    ScrollView: host("ScrollView"),
    KeyboardAvoidingView: host("KeyboardAvoidingView"),
    Modal: host("Modal"),
    AccessibilityInfo: { setAccessibilityFocus: () => undefined },
    findNodeHandle: () => null,
    useWindowDimensions: () => ({ width: 390, height: 844 }),
    Platform: { OS: "ios", select: <T>(values: { ios?: T; default?: T }) => values.ios ?? values.default },
  });
  mockModule("react-native-paper", {
    Text: host("Text"),
    Icon: host("Icon"),
    Button: host("Button"),
    IconButton: host("IconButton"),
  });
  mockModule("react-native-safe-area-context", {
    useSafeAreaInsets: () => ({ top: 0, bottom: 0, left: 0, right: 0 }),
  });
  mockModule(require.resolve("../../shared/i18n/use-app-translation"), {
    useAppTranslation: () => ({ t: lookup, language: "zh" }),
  });

  const { createRoot } = await import("test-renderer");
  const { LocationUsageNotice } = await import("./LocationUsageNotice");
  const { LocationUsageSheet } = await import("./LocationUsageSheet");

  // 采集频率表述（中文）：用户明确要求界面文案里不出现。
  const frequencyWording = /每隔|定期|每\s*\d+\s*(分钟|秒)|几分钟|分钟记录/;
  // 旧口径：班中持续 / 后台使用位置、已开启 / 已停止状态、补传、自动停止——取消后台定位后都不成立。
  const staleWording = /持续使用你的位置|含 App 在后台时|班中定位已开启|班中定位已停止|补传|自动停止|同意并扫码打卡/;

  async function mount(element: React.ReactElement) {
    const root = createRoot();
    await act(() => root.render(element));
    const container = root.container as { queryAll: (predicate: (node: Node) => boolean) => Node[] };
    const api = {
      all: (type: string) => container.queryAll((node) => node.type === type),
      allText: () => container.queryAll((node) => node.type === "Text" || node.type === "NativeText").map(textOf),
      button: (label: string) => api.all("Button").find((node) => textOf(node) === label),
      link: (label: string) => api.all("Pressable").find((node) => textOf(node) === label),
      unmount: () => act(() => root.unmount()),
    };
    return api;
  }

  // ───────────── A. 上班前说明卡 ─────────────
  {
    let opened = 0;
    const page = await mount(
      React.createElement(LocationUsageNotice, { onOpenDetails: () => { opened += 1; } }),
    );
    const text = page.allText().join("\n");
    assert.match(text, /定位使用说明/);
    assert.match(text, /扫码打卡与登录时/);
    assert.match(text, /只在使用 App 期间获取当前位置：扫码上班、下班和登录时，用于记录打卡与登录地点，完成后即停止。/);
    // 服务端位置请求：员工能从说明里知道「会先通知、打开 App 后才取、每次留记录」。
    assert.match(text, /上班期间的位置请求/);
    assert.match(text, /店长或管理员可以请求你上报一次位置：你会先收到通知，打开 App 后才会获取并上报，每次请求都会通知你，并在通知里留下记录。/);
    assert.match(text, /其他时间/);
    assert.match(text, /App 不会在后台使用你的位置，也不会持续跟踪你。/);
    assert.doesNotMatch(text, frequencyWording, "说明卡文案不得出现采集频率");
    assert.doesNotMatch(text, staleWording, "说明卡不得残留旧口径");
    const details = page.link("了解详情 ›");
    assert.ok(details, "说明卡必须有「了解详情」入口");
    await act(async () => details!.props.onPress());
    assert.equal(opened, 1, "点「了解详情」要打开详情弹层");
    await page.unmount();
  }

  // ───────────── B. 详情弹层：只读，讲清何时使用、何时不用、需要什么权限 ─────────────
  {
    let dismissed = 0;
    const page = await mount(
      React.createElement(LocationUsageSheet, { visible: true, onDismiss: () => { dismissed += 1; } }),
    );
    const text = page.allText().join("\n");
    for (const title of ["扫码上班 / 下班时", "收到位置请求时", "其他时间", "需要的权限"]) {
      assert.ok(text.includes(title), `详情弹层缺少「${title}」`);
    }
    assert.match(text, /只有你点开通知或打开 App 后，App 才会获取一次当前位置并上报，不会在后台获取。/);
    assert.match(text, /每次请求都会通知你并留下记录/);
    assert.match(text, /除上述情况外，不使用你的位置，也不会在后台运行定位，手机上不会出现常驻的定位提示。/);
    assert.match(text, /只需要.使用 App 期间.允许定位，不需要.始终允许./, "必须告诉员工不需要「始终允许」");
    assert.doesNotMatch(text, frequencyWording, "详情弹层文案不得出现采集频率");
    assert.doesNotMatch(text, staleWording, "详情弹层不得残留旧口径");
    assert.equal(page.button("取消"), undefined, "详情弹层是只读的，不能有取消 / 同意按钮");
    const gotIt = page.button("知道了");
    assert.ok(gotIt);
    await act(async () => gotIt!.props.onPress());
    assert.equal(dismissed, 1);
    await page.unmount();
  }

  // ───────────── C. 不可见时不渲染任何内容 ─────────────
  {
    const page = await mount(
      React.createElement(LocationUsageSheet, { visible: false, onDismiss: () => undefined }),
    );
    assert.equal(page.all("Modal").length, 0, "visible=false 必须卸载弹层内容");
    await page.unmount();
  }

  console.log("location-usage-notice-render.test.ts: ok");
}

run().catch((error) => {
  console.error(error);
  process.exit(1);
});
