import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import Module from "node:module";
import { resolve } from "node:path";
import React, { act } from "react";

// 打印设置弹窗的真实渲染测试：重点钉住"单次有效"与"打印数量"的关联——
// 单次有效的含义是「数量大于 1 的那次打完后恢复为 1」，数量为 1 时它没有可恢复的内容。
const zhProductQuery = JSON.parse(
  readFileSync(resolve(__dirname, "../../locales/zh/screens/productQuery.json"), "utf8"),
) as Record<string, unknown>;

function lookup(key: string): string {
  // 弹窗里还会取 common:actions.confirm，这里只关心 productQuery 命名空间，其余原样返回。
  if (key.startsWith("common:")) return key;
  const value = key.split(".").reduce<unknown>(
    (node, part) => (node && typeof node === "object" ? (node as Record<string, unknown>)[part] : undefined),
    zhProductQuery,
  );
  if (typeof value !== "string") throw new Error(`中文语言包缺少键：${key}`);
  return value;
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

  // 桩组件：渲染成同名宿主节点并保留全部 props，便于断言；命名函数满足 react/display-name。
  const host = (type: string) => {
    const Stub = (props: Record<string, unknown>) => React.createElement(type, props);
    Stub.displayName = type;
    return Stub;
  };
  mockModule("react-native", {
    StyleSheet: { create: (styles: object) => styles, hairlineWidth: 1 },
    View: host("View"),
  });
  mockModule("react-native-paper", {
    Button: host("Button"),
    IconButton: host("IconButton"),
    Modal: host("Modal"),
    Switch: host("Switch"),
    Text: host("Text"),
  });
  mockModule(require.resolve("../../shared/i18n/use-app-translation"), {
    useAppTranslation: () => ({ t: lookup }),
  });

  const { createRoot } = await import("test-renderer");
  const { PrintSettingsModal } = await import("./PrintSettingsModal");

  const events: string[] = [];
  const root = createRoot();
  const nodes = (type: string) => root.container.queryAll((node) => node.type === type);
  const texts = () => nodes("Text").map((node) => String(node.props.children));
  const singleUseSwitch = () =>
    nodes("Switch").find((node) => node.props.accessibilityLabel === lookup("print.quantitySingleUse"))!;
  const renderModal = (printQuantity: number, quantitySingleUse = true) =>
    act(() => root.render(React.createElement(PrintSettingsModal, {
      visible: true,
      continuousPrint: false,
      smallLabel: false,
      printQuantity,
      quantitySingleUse,
      printerSection: React.createElement("View", { testID: "printer-section" }),
      onToggleContinuousPrint: () => undefined,
      onToggleSmallLabel: () => undefined,
      onChangePrintQuantity: (value: number) => events.push(`quantity:${value}`),
      onToggleQuantitySingleUse: (value: boolean) => events.push(`singleUse:${value}`),
      onDismiss: () => undefined,
    })));

  try {
    // 1. 数量为 1：单次有效不可操作，提示何时可用；偏好值（默认开）保留不丢。
    await renderModal(1);
    assert.equal(singleUseSwitch().props.disabled, true, "数量为 1 时单次有效不可操作");
    assert.equal(singleUseSwitch().props.value, true, "不可操作期间保留用户偏好");
    assert.ok(texts().includes(lookup("print.quantitySingleUseIdleHint")));
    assert.ok(!texts().includes(lookup("print.quantitySingleUseHint")));
    const minus = nodes("IconButton").find((node) => node.props.icon === "minus")!;
    assert.equal(minus.props.disabled, true, "数量不能低于 1");

    // 2. 数量加到 2：单次有效立即可用，说明变为"打印后数量恢复为 1"。
    const plus = nodes("IconButton").find((node) => node.props.icon === "plus")!;
    await act(() => plus.props.onPress());
    assert.deepEqual(events, ["quantity:2"]);
    await renderModal(2);
    assert.equal(singleUseSwitch().props.disabled, false);
    assert.ok(texts().includes(lookup("print.quantitySingleUseHint")));
    assert.ok(!texts().includes(lookup("print.quantitySingleUseIdleHint")));

    // 3. 可用时切换才触发回调；关掉后保持关闭，数量回到 1 再升高时偏好不被改写。
    await act(() => singleUseSwitch().props.onValueChange(false));
    assert.deepEqual(events, ["quantity:2", "singleUse:false"]);
    await renderModal(1, false);
    assert.equal(singleUseSwitch().props.value, false);
    assert.equal(singleUseSwitch().props.disabled, true);
    await renderModal(5, false);
    assert.equal(singleUseSwitch().props.value, false, "数量再次大于 1 时沿用之前关闭的偏好");
    assert.equal(singleUseSwitch().props.disabled, false);

    // 4. 打印机区块插槽仍在最前面（标题之后、各开关之前）。
    const order = root.container
      .queryAll((node) => node.type === "Text" || node.props.testID === "printer-section")
      .map((node) => node.props.testID ?? String(node.props.children));
    assert.equal(order[0], lookup("print.settingsTitle"));
    assert.equal(order[1], "printer-section");
  } finally {
    await act(() => root.unmount());
  }
}

run().then(() => {
  console.log("print-settings-modal tests passed");
}).catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
