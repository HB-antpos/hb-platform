import assert from "node:assert/strict";
import Module from "node:module";
import { beforeEach, test } from "node:test";

import { fakeMetrics, fakeRender } from "./label-layout/testing/fake-renderer";
import type { LabelRenderTextItem, LabelTextItem } from "./label-render";

function mockModule(name: string, exports: object) {
  const filename = require.resolve(name);
  const module = new Module(filename);
  module.filename = filename;
  module.loaded = true;
  module.exports = exports;
  require.cache[filename] = module;
}

type Mock = Record<string, ((...args: never[]) => unknown) | undefined>;
const hbPrinterModule: Mock = {};
const platform = { OS: "android", Version: 33 };
const calls: string[] = [];

mockModule("react-native", {
  Platform: platform,
  NativeModules: { HbPrinterModule: hbPrinterModule },
  Linking: {},
  NativeEventEmitter: class {},
  PermissionsAndroid: {
    PERMISSIONS: {},
    RESULTS: { GRANTED: "granted" },
    requestMultiple: async () => new Proxy({}, { get: () => "granted" }),
  },
});

const product = { productName: "Gift Wrap", itemNumber: "VD028", barcode: "9313559558795", retailPrice: 12.99, discountRate: 0.25 };

function installNewNativePackage() {
  hbPrinterModule.getLabelRenderInfo = async () => ({ version: 1, fonts: ["roboto-black", "roboto-regular"], maxBatch: 64 });
  hbPrinterModule.measureTexts = async (items: LabelTextItem[]) => items.map(fakeMetrics);
  hbPrinterModule.renderTexts = async (items: LabelRenderTextItem[]) =>
    items.map((item) => {
      const rendered = fakeRender(item);
      return {
        width: rendered.width,
        height: rendered.height,
        originX: rendered.originX,
        originY: rendered.originY,
        hasInk: rendered.ink !== null,
        inkLeft: rendered.ink?.left ?? 0,
        inkTop: rendered.ink?.top ?? 0,
        inkRight: rendered.ink?.right ?? 0,
        inkBottom: rendered.ink?.bottom ?? 0,
        bits: rendered.base64,
      };
    });
}

async function run() {
  const native = await import("./native");
  const { setJsLabelOverrides } = await import("./label-layout/config");

  beforeEach(() => {
    calls.length = 0;
    platform.OS = "android";
    setJsLabelOverrides({});
    for (const key of Object.keys(hbPrinterModule)) delete hbPrinterModule[key];
    hbPrinterModule.print = async (command: string) => {
      calls.push(`print:${command.split("\r\n")[0]}`);
      return true;
    };
    hbPrinterModule.printProductLabel = async () => {
      calls.push("native:product");
      return true;
    };
    hbPrinterModule.printDiscountLabel = async () => {
      calls.push("native:discount");
      return true;
    };
  });

  test("开关默认全关：即使是新原生包也走原生排版", async () => {
    installNewNativePackage();
    assert.equal(await native.printNativeProductLabel(product, null), true);
    assert.deepEqual(calls, ["native:product"]);
  });

  test("开关打开 + 新原生包：JS 排版后用 EG 经 print 下发，不再调用原生排版", async () => {
    installNewNativePackage();
    setJsLabelOverrides({ layout: { product: true } });
    assert.equal(await native.printNativeProductLabel(product, null), true);
    assert.deepEqual(calls, ["print:! 0 200 200 400 1"]);
  });

  test("开关打开但旧原生包（没有渲染能力）：继续走原生排版", async () => {
    setJsLabelOverrides({ layout: { product: true } });
    assert.equal(await native.printNativeProductLabel(product, null), true);
    assert.deepEqual(calls, ["native:product"]);
  });

  test("CG 模式且有 writeBytes：二进制下发；排版出错时回退原生排版且没发任何数据", async () => {
    installNewNativePackage();
    let sent: string | null = null;
    hbPrinterModule.writeBytes = async (base64: string) => {
      sent = base64;
      calls.push("writeBytes");
      return true;
    };
    setJsLabelOverrides({ layout: { discount: true }, graphicsMode: "CG" });
    assert.equal(await native.printNativeDiscountLabel(product, null), true);
    assert.deepEqual(calls, ["writeBytes"]);
    assert.match(Buffer.from(sent!, "base64").toString("latin1"), /^! 0 200 200 400 1\r\nPAGE-WIDTH 570\r\nCG /);

    calls.length = 0;
    hbPrinterModule.renderTexts = async () => {
      throw new Error("render failed");
    };
    assert.equal(await native.printNativeDiscountLabel(product, null), true);
    assert.deepEqual(calls, ["native:discount"]);
  });

  test("下发阶段失败直接抛出，不回退原生（数据可能已到打印机，避免重复出纸）", async () => {
    installNewNativePackage();
    setJsLabelOverrides({ layout: { product: true } });
    hbPrinterModule.print = async () => {
      calls.push("print");
      throw new Error("socket closed");
    };
    await assert.rejects(native.printNativeProductLabel(product, null), /socket closed/);
    assert.deepEqual(calls, ["print"]);
  });
}

void run();
