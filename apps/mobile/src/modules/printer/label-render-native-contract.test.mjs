import assert from "node:assert/strict";
import { existsSync, readFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

// JS 排版的原生接口在 Android / iOS / 配置插件 / JS 四处各写一份，任何一处漂移都会让某一端排版错位或调用失败。
// 这里只做源码层面的口径比对；真实渲染要在真机上对纸（见 docs/plans/2026-11-23-mobile-label-js-layout.md）。
const mobileRoot = resolve(dirname(fileURLToPath(import.meta.url)), "../../..");
const read = (path) => readFileSync(join(mobileRoot, path), "utf8");

const kotlinRenderer = read("android/app/src/main/java/com/hbweb/expo/HbPrinterTextRenderer.kt");
const kotlinModule = read("android/app/src/main/java/com/hbweb/expo/HbPrinterModule.kt");
const swiftRenderer = read("plugins/ios/HbPrinterTextRenderer.swift");
const swiftModule = read("plugins/ios/HbPrinterModule.swift");
const plugin = read("plugins/with-ios-printer-module.js");
const gradle = read("android/app/build.gradle");
const jsRender = read("src/modules/printer/label-render.ts");

const FONT_DIR = "plugins/printer-assets/printer-fonts";

function kotlinFonts() {
  return [...kotlinRenderer.matchAll(/"(roboto-[a-z-]+)" to "([^"]+)\.ttf"/g)].map(([, key, file]) => ({ key, file }));
}

function swiftFonts() {
  return [...swiftRenderer.matchAll(/\("(roboto-[a-z-]+)", "([^"]+)"\)/g)].map(([, key, file]) => ({ key, file }));
}

function jsFonts() {
  const block = jsRender.match(/export const LABEL_FONTS = \[([\s\S]*?)\] as const/);
  assert.ok(block, "label-render.ts 缺少 LABEL_FONTS");
  return [...block[1].matchAll(/"([^"]+)"/g)].map(([, key]) => key);
}

test("内置字体键在 Android / iOS / JS 三处一致（含顺序），且字体文件都在共用目录里", () => {
  const kotlin = kotlinFonts();
  assert.equal(kotlin.length, 6);
  assert.deepEqual(swiftFonts(), kotlin);
  assert.deepEqual(jsFonts(), kotlin.map((font) => font.key));
  for (const { file } of kotlin) {
    assert.ok(existsSync(join(mobileRoot, FONT_DIR, `${file}.ttf`)), `缺少字体文件 ${file}.ttf`);
  }
  assert.ok(existsSync(join(mobileRoot, FONT_DIR, "LICENSE-Roboto.txt")), "字体许可文件必须随字体一起分发");
});

test("渲染版本与各项上限两端一致", () => {
  const pairs = [
    ["RENDER_VERSION", "renderVersion"],
    ["MAX_BATCH", "maxBatch"],
    ["MAX_BITMAP_WIDTH", "maxBitmapWidth"],
    ["MAX_BITMAP_HEIGHT", "maxBitmapHeight"],
    ["MAX_TEXT_LENGTH", "maxTextLength"],
    ["MAX_PADDING", "maxPadding"],
  ];
  for (const [kotlinName, swiftName] of pairs) {
    const kotlinValue = kotlinRenderer.match(new RegExp(`const val ${kotlinName} = (\\d+)`))?.[1];
    const swiftValue = swiftRenderer.match(new RegExp(`static let ${swiftName} = (\\d+)`))?.[1];
    assert.ok(kotlinValue, `Kotlin 缺少 ${kotlinName}`);
    assert.equal(swiftValue, kotlinValue, `${kotlinName} 两端不一致`);
  }
  const kotlinFontSize = kotlinRenderer.match(/const val MAX_FONT_SIZE = (\d+)f/)?.[1];
  const swiftFontSize = swiftRenderer.match(/static let maxFontSize: CGFloat = (\d+)/)?.[1];
  assert.ok(kotlinFontSize);
  assert.equal(swiftFontSize, kotlinFontSize, "MAX_FONT_SIZE 两端不一致");
});

test("黑白阈值两端都是 < 200", () => {
  assert.match(kotlinRenderer, /luminance < 200/);
  assert.match(swiftRenderer, /\] < 200/);
});

test("四个新方法在 Android @ReactMethod、Swift @objc、插件导出三处都在", () => {
  const methods = {
    getLabelRenderInfo: "getLabelRenderInfo:rejecter:",
    measureTexts: "measureTexts:resolver:rejecter:",
    renderTexts: "renderTexts:resolver:rejecter:",
    writeBytes: "writeBytes:resolver:rejecter:",
  };
  for (const [name, selector] of Object.entries(methods)) {
    assert.match(kotlinModule, new RegExp(`@ReactMethod\\s+fun ${name}\\(`), `Android 缺少 @ReactMethod ${name}`);
    assert.ok(swiftModule.includes(`@objc(${selector})`), `Swift 缺少 @objc(${selector})`);
    assert.ok(plugin.includes(`RCT_EXTERN_METHOD(${name}:`), `插件缺少 RCT_EXTERN_METHOD(${name}:`);
  }
});

test("原生结果字段名两端一致（JS normalize 依赖这些键）", () => {
  const keys = ["advance", "ascent", "descent", "inkLeft", "inkTop", "inkRight", "inkBottom", "width", "height", "originX", "originY", "hasInk", "bits"];
  for (const key of keys) {
    assert.ok(kotlinModule.includes(`"${key}"`), `Android 结果缺少 ${key}`);
    assert.ok(swiftModule.includes(`"${key}":`), `iOS 结果缺少 ${key}`);
    assert.ok(jsRender.includes(`raw.${key}`), `JS 未读取 ${key}`);
  }
});

test("字体打包：Android 从共用目录打进 assets，iOS 插件写入渲染器并把字体加为包资源", () => {
  assert.ok(gradle.includes('assets.srcDirs += ["$projectDir/../../plugins/printer-assets"]'), "build.gradle 未引用共用字体目录");
  assert.match(kotlinRenderer, /"printer-fonts\/\$assetName"/);
  assert.ok(plugin.includes('path.join(__dirname, "printer-assets", "printer-fonts")'));
  assert.ok(plugin.includes('"HbPrinterTextRenderer.swift"'));
  assert.match(plugin, /addResourceFileToGroup\(/);
  assert.match(swiftRenderer, /Bundle\.main\.url\(forResource: file, withExtension: "ttf"\)/);
});

test("文本 print 与 writeBytes 共用同一条写入路径（失败处理一致，不自动重放）", () => {
  assert.match(kotlinModule, /private fun writePrinterCommand\(command: String, encoding: String\) \{\s+writePrinterBytes\(/);
  assert.match(kotlinModule, /fun writeBytes\([\s\S]*?writePrinterBytes\(bytes\)/);
  assert.match(swiftModule, /private func writePrinterCommand\([\s\S]*?writePrinterData\(encoding:/);
  assert.match(swiftModule, /func writeBytes\([\s\S]*?self\.writePrinterData\(encoding: \{ data \}/);
});
