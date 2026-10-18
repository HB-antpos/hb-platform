import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { getLeadingZeroBarcodeVariant } from "./barcode-variants";

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
