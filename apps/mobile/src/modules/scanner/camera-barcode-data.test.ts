import assert from "node:assert/strict";
import { restoreCameraBarcodeData } from "./camera-barcode-data";

// 现场复现：条码 0 810812 024527，iOS 返回 12 位 810812024527、类型 ean13。
assert.equal(restoreCameraBarcodeData("ean13", "810812024527"), "0810812024527");

// Android（ML Kit）把同一条码按 upc_a 返回 12 位，同样要补回前导 0。
assert.equal(restoreCameraBarcodeData("upc_a", "810812024527"), "0810812024527");

// 完整 13 位（首位非 0，原生层没有去 0）不得再补。
assert.equal(restoreCameraBarcodeData("ean13", "4006381333931"), "4006381333931");

// 其他类型即使恰好 12 位纯数字也不动（Code128 等短码就是它本身）。
assert.equal(restoreCameraBarcodeData("code128", "810812024527"), "810812024527");
assert.equal(restoreCameraBarcodeData("qr", "810812024527"), "810812024527");
assert.equal(restoreCameraBarcodeData(undefined, "810812024527"), "810812024527");

// ean13 / upc_a 类型但不是 12 位纯数字：原样返回。
assert.equal(restoreCameraBarcodeData("ean13", "81081202452"), "81081202452");
assert.equal(restoreCameraBarcodeData("upc_a", "81081202452A"), "81081202452A");

console.log("camera-barcode-data tests passed");
