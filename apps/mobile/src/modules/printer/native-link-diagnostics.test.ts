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

type MockNativeModule = { drainLinkDiagnostics?: () => unknown };

// native.ts 在加载时就捕获 NativeModules.HbPrinterModule 的引用，所以只加载一次，
// 之后在测试之间切换同一个对象的方法与 Platform.OS（这两个都是调用时才读取的）。
const hbPrinterModule: MockNativeModule = {};
const platform = { OS: "android", Version: 29 };

mockModule("react-native", {
  Platform: platform,
  NativeModules: { HbPrinterModule: hbPrinterModule },
  Linking: {},
  NativeEventEmitter: class {},
  PermissionsAndroid: { PERMISSIONS: {}, RESULTS: {}, requestMultiple: async () => ({}) },
});

const validEvent = { ev: "acl.disconnected", atMs: 1_700_000_000_000, address: "AA:BB", tracked: true, socketConnected: null };

async function run() {
  const { drainNativeLinkDiagnostics } = await import("./native");

  beforeEach(() => {
    platform.OS = "android";
    delete hbPrinterModule.drainLinkDiagnostics;
  });

  test("新安卓原生包：返回原生缓冲的事件，过滤掉格式不对的条目", async () => {
    hbPrinterModule.drainLinkDiagnostics = async () => [
      validEvent,
      { ev: "connect.error", atMs: Number.NaN },
      { ev: 5, atMs: 1 },
      { atMs: 1 },
      null,
      "oops",
      { ev: "connect.ok", atMs: 1_700_000_000_500, elapsedMs: 366 },
    ];
    const events = await drainNativeLinkDiagnostics();
    assert.deepEqual(events.map((event) => event.ev), ["acl.disconnected", "connect.ok"]);
    assert.equal(events[0].tracked, true);
    assert.equal(events[0].socketConnected, null);
    assert.equal(events[1].elapsedMs, 366);
  });

  test("旧安卓原生包没有这个方法：直接返回空，不报错", async () => {
    assert.equal(hbPrinterModule.drainLinkDiagnostics, undefined);
    assert.deepEqual(await drainNativeLinkDiagnostics(), []);
  });

  test("iOS 不走这条原生接口：返回空，且不会调用原生方法", async () => {
    let called = false;
    hbPrinterModule.drainLinkDiagnostics = async () => {
      called = true;
      return [validEvent];
    };
    platform.OS = "ios";
    assert.deepEqual(await drainNativeLinkDiagnostics(), []);
    assert.equal(called, false);
  });

  test("原生方法 reject、同步抛错、返回非数组：都返回空，诊断不能让连接或打印失败", async () => {
    hbPrinterModule.drainLinkDiagnostics = async () => {
      throw new Error("bridge down");
    };
    assert.deepEqual(await drainNativeLinkDiagnostics(), []);

    hbPrinterModule.drainLinkDiagnostics = () => {
      throw new Error("sync boom");
    };
    assert.deepEqual(await drainNativeLinkDiagnostics(), []);

    hbPrinterModule.drainLinkDiagnostics = async () => ({ ev: "x", atMs: 1 });
    assert.deepEqual(await drainNativeLinkDiagnostics(), []);
  });

  test("原生方法迟迟不返回：超时后返回空，连接流程不会被诊断拖住", async () => {
    hbPrinterModule.drainLinkDiagnostics = () => new Promise(() => undefined);
    const startedAt = Date.now();
    assert.deepEqual(await drainNativeLinkDiagnostics(30), []);
    assert.ok(Date.now() - startedAt < 1000, "应按传入的超时返回，而不是一直等");
  });
}

void run();
