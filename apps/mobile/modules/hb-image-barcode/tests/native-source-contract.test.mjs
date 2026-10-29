import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const root = new URL("../", import.meta.url);
const read = (path) => readFile(new URL(path, root), "utf8");
const [config, podspec, moduleSource, scanner, bridge] = await Promise.all([
  read("expo-module.config.json"),
  read("ios/HBImageBarcode.podspec"),
  read("ios/HBImageBarcodeModule.swift"),
  read("ios/HBImageBarcodeScanner.swift"),
  read("src/HBImageBarcodeModule.ts"),
]);

test("module is registered for apple only and matches the Swift class", () => {
  const parsed = JSON.parse(config);
  assert.deepEqual(parsed.platforms, ["apple"]);
  assert.deepEqual(parsed.apple.modules, ["HBImageBarcodeModule"]);
  assert.match(moduleSource, /class HBImageBarcodeModule: Module/);
  assert.match(moduleSource, /Name\("HBImageBarcode"\)/);
  assert.match(podspec, /s\.dependency 'ExpoModulesCore'/);
});

test("bridge is optional so old native builds degrade instead of crashing", () => {
  assert.match(bridge, /requireOptionalNativeModule/);
  assert.match(bridge, /"HBImageBarcode"/);
  assert.match(moduleSource, /AsyncFunction\("scanImage"\)/);
});

test("scanner stays local: Vision only, no network, no persistence, no logging", () => {
  assert.match(scanner, /VNDetectBarcodesRequest/);
  assert.doesNotMatch(scanner + moduleSource, /URLSession|URLRequest|\.write\(|NSLog|\bprint\(/);
});

test("scanner reports expo-camera compatible type names", () => {
  for (const type of ["ean13", "ean8", "upc_e", "code128", "code39", "qr"]) {
    assert.match(scanner, new RegExp(`"${type}"`));
  }
});
