import assert from "node:assert/strict";
import Module from "node:module";
import React, { act } from "react";

/**
 * useHidBarcodeScanner 的 BLE 归属：两个 hook 同时 enabled 时只投给最后一个；停用 / 卸载后回到前一个；
 * textInputOnly 不影响 BLE；HID 路径经 hub 跨来源去重。
 */
function mockModule(name: string, exports: object) {
  const filename = require.resolve(name);
  const module = new Module(filename);
  module.filename = filename;
  module.loaded = true;
  module.exports = exports;
  require.cache[filename] = module;
}

type HookResult = ReturnType<typeof import("./use-hid-barcode-scanner").useHidBarcodeScanner>;

async function run() {
  Object.assign(globalThis, { IS_REACT_ACT_ENVIRONMENT: true });
  const rendererReact = require.resolve("react", { paths: [require.resolve("test-renderer")] });
  if (rendererReact !== require.resolve("react")) mockModule(rendererReact, React);
  const { createRoot } = await import("test-renderer");

  mockModule("react-native", {
    AppState: {
      currentState: "active",
      addEventListener: () => ({ remove: () => undefined }),
    },
  });
  mockModule("expo-secure-store", {
    getItemAsync: async () => null,
    setItemAsync: async () => undefined,
  });
  // 没有 ExpoKeyEvent 原生模块：hook 走隐藏输入框模式，便于直接调用提交回调。
  mockModule("expo-modules-core", {
    requireNativeModule: () => {
      throw new Error("no native module");
    },
  });

  let clock = 10_000;
  const hub = await import("./ble/ble-scan-hub");
  hub.__resetBleScanHubForTests(() => clock);
  const originalNow = Date.now;
  Date.now = () => clock;
  const { useHidBarcodeScanner } = await import("./use-hid-barcode-scanner");

  const received: string[] = [];
  const results: Record<string, HookResult> = {};

  function Harness(props: { name: string; enabled: boolean; textInputOnly?: boolean }) {
    results[props.name] = useHidBarcodeScanner({
      enabled: props.enabled,
      textInputOnly: props.textInputOnly,
      onScan: (barcode) => {
        received.push(`${props.name}:${barcode}`);
      },
    });
    return null;
  }

  function App(props: { pageEnabled: boolean; sheetEnabled: boolean; sheetMounted: boolean }) {
    return React.createElement(
      React.Fragment,
      null,
      React.createElement(Harness, { name: "page", enabled: props.pageEnabled }),
      props.sheetMounted
        ? React.createElement(Harness, { name: "sheet", enabled: props.sheetEnabled, textInputOnly: true })
        : null,
    );
  }

  const root = createRoot();
  try {
    await act(async () => {
      root.render(React.createElement(App, { pageEnabled: true, sheetEnabled: false, sheetMounted: false }));
    });
    assert.equal(hub.__getBleScanLeaseCountForTests(), 1, "启用的 hook 必须持有一份 BLE 租约");
    assert.equal(hub.deliverBleScan("PAGE001"), true);
    assert.deepEqual(received, ["page:PAGE001"]);

    // 弹层（textInputOnly）后启用：BLE 码只给它。
    await act(async () => {
      root.render(React.createElement(App, { pageEnabled: true, sheetEnabled: true, sheetMounted: true }));
    });
    clock += 1_000;
    hub.deliverBleScan("SHEET01");
    assert.deepEqual(received, ["page:PAGE001", "sheet:SHEET01"], "两个 hook 同时启用时只投给最后一个");

    // 弹层停用：回到页面。
    await act(async () => {
      root.render(React.createElement(App, { pageEnabled: true, sheetEnabled: false, sheetMounted: true }));
    });
    assert.equal(hub.__getBleScanLeaseCountForTests(), 1, "停用必须释放租约");
    clock += 1_000;
    hub.deliverBleScan("PAGE002");
    assert.deepEqual(received.slice(-1), ["page:PAGE002"]);

    // 过短的码按 minLength 忽略，但算已处理。
    clock += 1_000;
    assert.equal(hub.deliverBleScan("AB"), true);
    assert.deepEqual(received.slice(-1), ["page:PAGE002"]);

    // HID 提交：BLE 300ms 内已来过同码 → 跳过；超过窗口照常。
    clock += 1_000;
    hub.deliverBleScan("DUP0001");
    assert.deepEqual(received.slice(-1), ["page:DUP0001"]);
    clock += 100;
    const page = results.page;
    assert.equal(page.mode, "textInput");
    await act(async () => {
      page.textInputProps?.onSubmitEditing({ nativeEvent: { text: "DUP0001" } } as never);
    });
    assert.equal(received.filter((item) => item === "page:DUP0001").length, 1, "HID 同码 300ms 内不得重复触发");
    clock += 1_000;
    await act(async () => {
      results.page.textInputProps?.onSubmitEditing({ nativeEvent: { text: "HID0001" } } as never);
    });
    assert.deepEqual(received.slice(-1), ["page:HID0001"]);

    // 全部停用 / 卸载：没人接。
    await act(async () => {
      root.render(React.createElement(App, { pageEnabled: false, sheetEnabled: false, sheetMounted: false }));
    });
    assert.equal(hub.__getBleScanLeaseCountForTests(), 0);
    clock += 1_000;
    assert.equal(hub.deliverBleScan("NOBODY1"), false);
  } finally {
    await act(async () => {
      root.unmount();
    });
    Date.now = originalNow;
  }
  console.log("use-hid-barcode-scanner-ble.test.ts: ok");
}

void run().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
