import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import {
  getCaseCodeItemBarcode,
  getLeadingZeroBarcodeVariant,
  getLookupFallbackBarcodes,
} from "./barcode-variants";

const currentDir = dirname(fileURLToPath(import.meta.url));

test("12 位数字条码补一个前导 0，13 位以 0 开头的去掉它", () => {
  // 生产实例：库里存 0810812024527，iPhone 相机扫出去掉前导 0 的 810812024527。
  assert.equal(getLeadingZeroBarcodeVariant("810812024527"), "0810812024527");
  assert.equal(getLeadingZeroBarcodeVariant("0810812024527"), "810812024527");
});

test("其它长度、非 0 开头的 13 位、含非数字字符都不产生变体", () => {
  for (const code of ["", "1810812024527", "81081202452", "08108120245271", "81081202452A", "ABC", "8108 12024527"]) {
    assert.equal(getLeadingZeroBarcodeVariant(code), null, code);
  }
});

test("箱码（ITF-14）换算成单件 EAN-13，与后端 GetCaseCodeItemBarcode 一致", () => {
  // 生产实例：KFC Uncle Bills 外箱 19333527854091，单件 XK0000052 条码 9333527854094。
  assert.equal(getCaseCodeItemBarcode("19333527854091"), "9333527854094");
  assert.equal(getCaseCodeItemBarcode("09333527854094"), "9333527854094");
  assert.equal(getCaseCodeItemBarcode("10810812024524"), "0810812024527");
  // 校验位不对、指示符 9（变量计量）、非 14 位、含非数字都不换算。
  for (const code of ["19333527854092", "99333527854098", "9333527854094", "1933352785409A", ""]) {
    assert.equal(getCaseCodeItemBarcode(code), null, code);
  }
});

test("回退顺序：前导 0 变体 → 箱码换算的单件条码 → 其前导 0 变体", () => {
  assert.deepEqual(getLookupFallbackBarcodes("810812024527"), ["0810812024527"]);
  assert.deepEqual(getLookupFallbackBarcodes("19333527854091"), ["9333527854094"]);
  assert.deepEqual(getLookupFallbackBarcodes("10810812024524"), ["0810812024527", "810812024527"]);
  assert.deepEqual(getLookupFallbackBarcodes("XK0000052"), []);
});

test("离线查询：精确匹配无结果才回退变体，且回退只认条码类来源", () => {
  const source = readFileSync(
    resolve(currentDir, "../product-maintenance/offline-catalog/offline-catalog-store.ts"),
    "utf8",
  );
  const exact = source.indexOf("runtime.repository.lookup(storeCode, normalizedKeyword)");
  const guard = source.indexOf("rows.length === 0", exact);
  const fallback = source.indexOf("runtime.repository.lookup(storeCode, variant)", guard);
  assert.ok(exact >= 0 && guard > exact && fallback > guard, "变体回退必须在精确查询为空之后");
  assert.match(source.slice(fallback, fallback + 200), /matchSource\.endsWith\("Barcode"\)/);
});
