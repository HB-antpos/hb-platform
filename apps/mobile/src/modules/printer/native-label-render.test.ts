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
  getLabelRenderInfo?: () => Promise<unknown>;
  measureTexts?: (items: unknown[]) => Promise<unknown>;
  renderTexts?: (items: unknown[]) => Promise<unknown>;
  writeBytes?: (base64: string) => Promise<boolean>;
};

// native.ts 加载时就捕获 NativeModules.HbPrinterModule 的引用，只加载一次，之后切换同一对象的方法。
const hbPrinterModule: MockNativeModule = {};
const platform = { OS: "ios", Version: 17 };
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

const metrics = { advance: 10, ascent: 20, descent: 5, inkLeft: 0, inkTop: -18, inkRight: 10, inkBottom: 0 };
// 8×1 全黑位图：1 字节 0xFF → base64 "/w=="。
const bitmap = {
  width: 8,
  height: 1,
  originX: 0,
  originY: 1,
  hasInk: true,
  inkLeft: 0,
  inkTop: 0,
  inkRight: 8,
  inkBottom: 1,
  bits: "/w==",
};

async function run() {
  const native = await import("./native");

  beforeEach(() => {
    platform.OS = "ios";
    // 默认模拟新包；能力结果一经成功就被 native.ts 缓存（首个成功值为 maxBatch=2，见第二个用例）。
    hbPrinterModule.getLabelRenderInfo = async () => ({ version: 1, fonts: ["roboto-black"], maxBatch: 2 });
    delete hbPrinterModule.measureTexts;
    delete hbPrinterModule.renderTexts;
    delete hbPrinterModule.writeBytes;
  });

  test("旧原生包没有 getLabelRenderInfo：能力为 null，量字直接报错（调用方继续走旧原生排版）", async () => {
    delete hbPrinterModule.getLabelRenderInfo;
    assert.equal(await native.getLabelRenderInfo(), null);
    await assert.rejects(
      native.measureLabelTexts([{ text: "A", font: "roboto-black", size: 30 }]),
      /does not support JS label layout/
    );
  });

  test("能力查询失败不缓存：恢复后再次查询能拿到结果", async () => {
    let calls = 0;
    hbPrinterModule.getLabelRenderInfo = async () => {
      calls += 1;
      if (calls === 1) throw new Error("bridge down");
      return { version: 1, fonts: ["roboto-black"], maxBatch: 2 };
    };
    assert.equal(await native.getLabelRenderInfo(), null);
    const info = await native.getLabelRenderInfo();
    assert.equal(info?.maxBatch, 2);
    // 成功结果缓存，不再重复调用原生。
    await native.getLabelRenderInfo();
    assert.equal(calls, 2);
  });

  test("量字与画字按原生单批上限分批，结果顺序与入参一致", async () => {
    const batches: number[] = [];
    hbPrinterModule.measureTexts = async (items) => {
      batches.push(items.length);
      return items.map((_, index) => ({ ...metrics, advance: batches.length * 100 + index }));
    };
    hbPrinterModule.renderTexts = async (items) => items.map(() => bitmap);
    const items = [1, 2, 3, 4, 5].map((n) => ({ text: String(n), font: "roboto-black" as const, size: 30 }));

    const measured = await native.measureLabelTexts(items);
    assert.deepEqual(batches, [2, 2, 1]);
    assert.deepEqual(
      measured.map((item) => item.advance),
      [100, 101, 200, 201, 300]
    );

    const rendered = await native.renderLabelTexts(items);
    assert.equal(rendered.length, 5);
    assert.deepEqual(Array.from(rendered[0].bits), [0xff]);
  });

  test("原生返回条数与请求不符时报错，不拿错位结果去排版", async () => {
    hbPrinterModule.measureTexts = async () => [metrics];
    hbPrinterModule.renderTexts = async () => [bitmap];
    await assert.rejects(
      native.measureLabelTexts([
        { text: "A", font: "roboto-black", size: 30 },
        { text: "B", font: "roboto-black", size: 30 },
      ]),
      /does not match/
    );
  });

  test("printRawBytes：新包按 base64 下发原始字节；旧包明确报错", async () => {
    await assert.rejects(native.printRawBytes(Uint8Array.from([1, 2])), /raw byte printing/);
    let received = "";
    hbPrinterModule.writeBytes = async (base64) => {
      received = base64;
      return true;
    };
    assert.equal(await native.printRawBytes(Uint8Array.from([0x21, 0x0d, 0x0a, 0xff])), true);
    assert.equal(received, Buffer.from([0x21, 0x0d, 0x0a, 0xff]).toString("base64"));
  });
}

void run();
