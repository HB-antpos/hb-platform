import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import Module from "node:module";
import { resolve } from "node:path";
import React, { act } from "react";

// 商品查询页打印机状态的真实渲染测试：chip（并入打印行，不占额外高度）与弹窗摘要共用同一套状态解析。
// i18n 桩直接读真实的中文语言包，且缺键即抛错，保证 8 种状态的文案键都存在。
const zhProductQuery = JSON.parse(
  readFileSync(resolve(__dirname, "../../locales/zh/screens/productQuery.json"), "utf8"),
) as Record<string, unknown>;

function lookup(key: string): string {
  const value = key.split(".").reduce<unknown>(
    (node, part) => (node && typeof node === "object" ? (node as Record<string, unknown>)[part] : undefined),
    zhProductQuery,
  );
  if (typeof value !== "string") throw new Error(`中文语言包缺少键：${key}`);
  return value;
}

const STATUSES = [
  "idle",
  "connecting",
  "connected",
  "reconnecting",
  "disconnected",
  "paused",
  "error",
] as const;

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
    Pressable: host("Pressable"),
    StyleSheet: { create: (styles: object) => styles, hairlineWidth: 1 },
    View: host("View"),
  });
  mockModule("react-native-paper", {
    Button: host("Button"),
    Icon: host("Icon"),
    Text: host("Text"),
  });
  mockModule(require.resolve("../../shared/i18n/use-app-translation"), {
    useAppTranslation: () => ({ t: lookup }),
  });

  const { createRoot } = await import("test-renderer");
  const { PrinterConnectionChip, PrinterConnectionSummary } = await import("./PrinterConnectionStatus");

  const printer = { name: " Zebra ZD421 ", address: "AA:BB:CC:DD:EE:FF" };
  const root = createRoot();
  const nodes = (type: string) => root.container.queryAll((node) => node.type === type);
  const texts = () => nodes("Text").map((node) => String(node.props.children));

  try {
    // 1. 七种连接状态：chip 显示短状态词与对应图标，朗读标签带完整状态 + 打印机名。
    const seenIcons = new Map<string, string>();
    for (const status of STATUSES) {
      await act(() => root.render(React.createElement(PrinterConnectionChip, {
        savedPrinter: printer,
        status,
        lastError: null,
        onPress: () => undefined,
      })));
      assert.deepEqual(texts(), [lookup(`print.printerStatus.short.${status}`)], `${status} chip 只显示短状态词，不再带打印机名`);
      const pressable = nodes("Pressable")[0]!;
      assert.equal(
        pressable.props.accessibilityLabel,
        `${lookup(`print.printerStatus.${status}`)} · Zebra ZD421`,
        `${status} 朗读标签应为完整状态 + 去空白后的打印机名`,
      );
      assert.equal(pressable.props.accessibilityRole, "button");
      assert.equal(pressable.props.accessibilityLiveRegion, "polite", "状态变化要主动播报");
      seenIcons.set(status, String(nodes("Icon")[0]!.props.source));
    }
    assert.equal(seenIcons.get("connected"), "printer-check");
    assert.equal(seenIcons.get("error"), "alert-circle-outline");
    assert.notEqual(seenIcons.get("connected"), seenIcons.get("disconnected"), "已连接与已断开不能只靠颜色区分，图标形状也要不同");

    // 2. 未保存打印机：无视传入的连接状态，一律是"未选择"。
    await act(() => root.render(React.createElement(PrinterConnectionChip, {
      savedPrinter: null,
      status: "connected",
      lastError: null,
      onPress: () => undefined,
    })));
    assert.deepEqual(texts(), [lookup("print.printerStatus.short.unselected")]);
    assert.equal(nodes("Pressable")[0]!.props.accessibilityLabel, lookup("print.printerStatus.unselected"));

    // 3. 没有 onPress（扫码打印进行中）：不可点、不声明按钮角色。
    await act(() => root.render(React.createElement(PrinterConnectionChip, {
      savedPrinter: printer,
      status: "connected",
      lastError: null,
    })));
    const disabled = nodes("Pressable")[0]!;
    assert.equal(disabled.props.disabled, true);
    assert.equal(disabled.props.accessibilityRole, undefined);

    // 4. 短状态词不超过 4 个字：chip 要和三个打印按钮挤在 Zebra 小屏的一行里。
    for (const key of ["unselected", ...STATUSES]) {
      const label = lookup(`print.printerStatus.short.${key}`);
      assert.ok([...label].length <= 4, `${key} 短状态词「${label}」超过 4 个字，会挤压打印按钮`);
    }

    // 5. 弹窗摘要：完整状态 + 打印机名；错误态只给处理方向，原生错误细节绝不上屏。
    const nativeError = "java.io.IOException: read failed, socket might closed at android.bluetooth";
    await act(() => root.render(React.createElement(PrinterConnectionSummary, {
      savedPrinter: printer,
      status: "error",
      lastError: nativeError,
      onManage: () => undefined,
    })));
    assert.deepEqual(texts(), [
      lookup("print.printerStatus.error"),
      "Zebra ZD421",
      lookup("print.printerStatus.errorHint"),
    ]);
    // 会上屏的文本节点与会被读屏朗读的标签，都不能含原生错误细节。
    const spoken = root.container
      .queryAll((node) => typeof node.props.accessibilityLabel === "string")
      .map((node) => String(node.props.accessibilityLabel));
    assert.ok(![...texts(), ...spoken].some((value) => value.includes("IOException")), "原生错误细节不能出现在界面或朗读标签里");

    await act(() => root.render(React.createElement(PrinterConnectionSummary, {
      savedPrinter: printer,
      status: "error",
      lastError: null,
    })));
    assert.ok(!texts().includes(lookup("print.printerStatus.errorHint")), "没有错误信息时不显示处理提示");

    // 6. 去打印机设置页的入口只在传入 onManage 时出现，并原样触发回调。
    assert.equal(nodes("Button").length, 0, "未传 onManage 不渲染设置入口");
    let managed = 0;
    await act(() => root.render(React.createElement(PrinterConnectionSummary, {
      savedPrinter: null,
      status: "idle",
      lastError: null,
      onManage: () => { managed++; },
    })));
    assert.deepEqual(texts(), [lookup("print.printerStatus.unselected")]);
    assert.equal(nodes("Button")[0]!.props.children, lookup("print.printerStatus.manage"), "入口文案是「打印机设置」");
    await act(() => nodes("Button")[0]!.props.onPress());
    assert.equal(managed, 1);
  } finally {
    await act(() => root.unmount());
  }
}

run().then(() => {
  console.log("printer-connection-status tests passed");
}).catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
