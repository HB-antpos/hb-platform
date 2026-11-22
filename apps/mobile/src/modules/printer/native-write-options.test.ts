import assert from "node:assert/strict";
import Module from "node:module";
import { beforeEach, test } from "node:test";

function mockModule(name: string, exports: object) {
  const filename = require.resolve(name);
  const module = new Module(filename);
  module.filename = filename;
  module.loaded = true;
  module.exports = exports;
  require.cache[filename] = module;
}

type MockNativeModule = {
  configureWrite?: (options: unknown) => Promise<boolean>;
  connect?: (address: string) => Promise<boolean>;
  connectWithTransport?: (address: string, transport: unknown) => Promise<boolean>;
};

// native.ts 加载时就捕获 NativeModules.HbPrinterModule 的引用，只加载一次，之后切换同一对象的方法。
const hbPrinterModule: MockNativeModule = {};
const platform = { OS: "ios", Version: 17 };
const calls: string[] = [];
// 安卓用例要过权限检查：任何权限都返回 granted。
const permissionsAndroid = {
  PERMISSIONS: {},
  RESULTS: { GRANTED: "granted" },
  requestMultiple: async () => new Proxy({}, { get: () => "granted" }),
};

mockModule("react-native", {
  Platform: platform,
  NativeModules: { HbPrinterModule: hbPrinterModule },
  Linking: {},
  NativeEventEmitter: class {},
  PermissionsAndroid: permissionsAndroid,
});

async function run() {
  const { connectPrinter, IOS_BLE_WRITE_OPTIONS } = await import("./native");

  beforeEach(() => {
    platform.OS = "ios";
    calls.length = 0;
    delete hbPrinterModule.configureWrite;
    delete hbPrinterModule.connectWithTransport;
    hbPrinterModule.connect = async (address) => {
      calls.push(`connect:${address}`);
      return true;
    };
  });

  test("默认策略是 fast + 2048 字节栅栏（与 Swift 原生默认值一致）", () => {
    assert.deepEqual(IOS_BLE_WRITE_OPTIONS, { mode: "fast", ackIntervalBytes: 2048 });
  });

  test("新 iOS 包：连接前先下发写入策略，再发起连接", async () => {
    let received: unknown;
    hbPrinterModule.configureWrite = async (options) => {
      received = options;
      calls.push("configureWrite");
      return true;
    };
    assert.equal(await connectPrinter("UUID-1"), true);
    assert.deepEqual(calls, ["configureWrite", "connect:UUID-1"]);
    assert.deepEqual(received, IOS_BLE_WRITE_OPTIONS);
  });

  test("旧 iOS 包没有 configureWrite：照常连接", async () => {
    assert.equal(await connectPrinter("UUID-2"), true);
    assert.deepEqual(calls, ["connect:UUID-2"]);
  });

  test("configureWrite 失败不能挡住连接", async () => {
    hbPrinterModule.configureWrite = async () => {
      throw new Error("bridge down");
    };
    assert.equal(await connectPrinter("UUID-3"), true);
    assert.deepEqual(calls, ["connect:UUID-3"]);
  });

  test("安卓不调用 iOS 写入策略接口", async () => {
    platform.OS = "android";
    let configured = false;
    hbPrinterModule.configureWrite = async () => {
      configured = true;
      return true;
    };
    hbPrinterModule.connectWithTransport = async (address) => {
      calls.push(`connectWithTransport:${address}`);
      return true;
    };
    assert.equal(await connectPrinter("AA:BB", "ble"), true);
    assert.equal(configured, false);
    assert.deepEqual(calls, ["connectWithTransport:AA:BB"]);
  });
}

void run();
