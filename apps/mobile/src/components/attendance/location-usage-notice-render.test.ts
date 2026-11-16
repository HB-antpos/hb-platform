import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import Module from "node:module";
import { resolve } from "node:path";
import React, { act } from "react";

// 打卡页「定位使用说明」的渲染测试：真实组件 + 真实中文文案，只把 react-native / paper / 翻译换成桩。
// 钉住：上班前说明卡、上班中「已开启」条、下班后「已停止」条的内容与交互；
// 上班扫码前披露弹层（consent）与只读详情弹层（info）的内容、按钮回调；
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

  // 采集频率表述（中文 / 英文）：用户明确要求界面文案里不出现。
  const frequencyWording = /每隔|定期|每\s*\d+\s*(分钟|秒)|几分钟|分钟记录/;

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
      React.createElement(LocationUsageNotice, { variant: "notice", onOpenDetails: () => { opened += 1; } }),
    );
    const text = page.allText().join("\n");
    assert.match(text, /定位使用说明/);
    assert.match(text, /上班期间/);
    assert.match(text, /打卡上班后，App 会持续使用你的位置（含 App 在后台时），仅用于考勤核对。/);
    assert.match(text, /下班之后/);
    assert.match(text, /扫码下班打卡后立即停止，不再使用你的位置。/);
    assert.doesNotMatch(text, frequencyWording, "说明卡文案不得出现采集频率");
    const details = page.link("了解详情 ›");
    assert.ok(details, "说明卡必须有「了解详情」入口");
    await act(async () => details!.props.onPress());
    assert.equal(opened, 1, "点「了解详情」要打开详情弹层");
    await page.unmount();
  }

  // ───────────── B. 上班中「已开启」条：有「说明」入口 ─────────────
  {
    let opened = 0;
    const page = await mount(
      React.createElement(LocationUsageNotice, { variant: "active", onOpenDetails: () => { opened += 1; } }),
    );
    const text = page.allText().join("\n");
    assert.match(text, /班中定位已开启/);
    assert.match(text, /扫码下班打卡后将自动停止/);
    const explain = page.link("说明");
    assert.ok(explain, "已开启状态条必须有「说明」入口");
    await act(async () => explain!.props.onPress());
    assert.equal(opened, 1);
    await page.unmount();
  }

  // ───────────── C. 下班后「已停止」条：只读，没有入口 ─────────────
  {
    const page = await mount(
      React.createElement(LocationUsageNotice, { variant: "stopped", onOpenDetails: () => { throw new Error("不应触发"); } }),
    );
    const text = page.allText().join("\n");
    assert.match(text, /班中定位已停止/);
    assert.match(text, /下班打卡后，App 不再使用你的位置/);
    assert.equal(page.all("Pressable").length, 0, "已停止状态条只读，没有可点入口");
    await page.unmount();
  }

  // ───────────── D. 上班扫码前披露弹层（consent） ─────────────
  {
    let confirmed = 0;
    let dismissed = 0;
    const page = await mount(
      React.createElement(LocationUsageSheet, {
        visible: true,
        mode: "consent",
        onConfirm: () => { confirmed += 1; },
        onDismiss: () => { dismissed += 1; },
      }),
    );
    const text = page.allText().join("\n");
    assert.match(text, /班中定位说明/);
    for (const title of ["扫码时", "上班期间", "下班打卡后"]) assert.ok(text.includes(title), `三段式说明缺少「${title}」`);
    assert.match(text, /立即停止，不再使用你的位置。/);
    assert.match(text, /始终允许/, "必须提示后台定位要选择「始终允许」");
    assert.doesNotMatch(text, frequencyWording, "披露弹层文案不得出现采集频率");
    // 披露弹层是上班前的同意步骤，不应出现只在详情里的「自动停止 / 补传」两项。
    assert.ok(!text.includes("补传说明"));
    const confirmButton = page.button("同意并扫码打卡");
    const cancelButton = page.button("取消");
    assert.ok(confirmButton && cancelButton, "披露弹层必须有「取消」和「同意并扫码打卡」");
    await act(async () => confirmButton!.props.onPress());
    assert.equal(confirmed, 1);
    assert.equal(dismissed, 0, "点同意不应同时触发取消");
    await act(async () => cancelButton!.props.onPress());
    assert.equal(dismissed, 1);
    await page.unmount();
  }

  // ───────────── E. 详情弹层（info）：只读，含自动停止与补传说明 ─────────────
  {
    let dismissed = 0;
    const page = await mount(
      React.createElement(LocationUsageSheet, { visible: true, mode: "info", onDismiss: () => { dismissed += 1; } }),
    );
    const text = page.allText().join("\n");
    for (const title of ["扫码时", "上班期间", "下班打卡后", "自动停止", "补传说明"]) {
      assert.ok(text.includes(title), `详情弹层缺少「${title}」`);
    }
    assert.match(text, /超过 10 小时、跨过门店当天 24:00 或退出登录，也会自动停止。/);
    assert.match(text, /可能在下班后补传；不会再新增记录。/, "必须如实说明下班前未上传的位置可能补传");
    assert.doesNotMatch(text, frequencyWording, "详情弹层文案不得出现采集频率");
    assert.equal(page.button("同意并扫码打卡"), undefined, "详情弹层是只读的，不能有同意按钮");
    const gotIt = page.button("知道了");
    assert.ok(gotIt);
    await act(async () => gotIt!.props.onPress());
    assert.equal(dismissed, 1);
    await page.unmount();
  }

  // ───────────── F. 不可见时不渲染任何内容 ─────────────
  {
    const page = await mount(
      React.createElement(LocationUsageSheet, { visible: false, mode: "consent", onConfirm: () => undefined, onDismiss: () => undefined }),
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
