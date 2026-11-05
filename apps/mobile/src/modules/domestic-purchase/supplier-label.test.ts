import assert from "node:assert/strict";

import { normalizeDomesticProduct } from "./api";
import { formatDomesticSupplierLabel } from "./supplier-label";

// 有名称：代码 + 分隔符 + 名称，分隔符由调用处决定（列表用「 - 」，创建向导用「 · 」）。
assert.equal(formatDomesticSupplierLabel("HB196", "义乌玩具厂"), "HB196 - 义乌玩具厂");
assert.equal(formatDomesticSupplierLabel("HB196", "义乌玩具厂", " · "), "HB196 · 义乌玩具厂");
assert.equal(formatDomesticSupplierLabel(" HB196 ", "  义乌玩具厂  "), "HB196 - 义乌玩具厂", "首尾空白不带进显示文本");

// 没有「查看国内供应商名称」权限：后端名称为 null，只显示代码，不留分隔符。
for (const missingName of [null, undefined, "", "   "]) {
  assert.equal(formatDomesticSupplierLabel("HB196", missingName), "HB196");
  assert.equal(formatDomesticSupplierLabel("HB196", missingName, " · "), "HB196");
}

// 名称就是代码（接口把名称回退成代码）时只显示一次。
assert.equal(formatDomesticSupplierLabel("HB196", "HB196"), "HB196");
assert.equal(formatDomesticSupplierLabel("HB196", "hb196 "), "HB196");

// 代码缺失时只显示名称；两者都缺返回空串，由调用处决定占位符。
assert.equal(formatDomesticSupplierLabel("", "义乌玩具厂"), "义乌玩具厂");
assert.equal(formatDomesticSupplierLabel(null, null), "");
assert.equal(formatDomesticSupplierLabel(undefined, undefined, " · "), "");

// 归一化：后端的 null 不能变成字符串 "null" / "undefined"，再经显示函数后只剩代码。
for (const payload of [
  { productCode: "P1", supplierCode: "HB196", supplierName: null },
  { ProductCode: "P1", SupplierCode: "HB196", SupplierName: null },
  { productCode: "P1", supplierCode: "HB196" },
]) {
  const product = normalizeDomesticProduct(payload);
  assert.equal(product.supplierName, "");
  assert.equal(formatDomesticSupplierLabel(product.supplierCode, product.supplierName), "HB196");
}

console.log("supplier-label.test.ts: ok");
