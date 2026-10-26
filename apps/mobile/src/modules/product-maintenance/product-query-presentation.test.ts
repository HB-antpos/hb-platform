import assert from "node:assert/strict";
import {
  calcGrossMarginPercent,
  getDirtyCodeIds,
  getMarginTrend,
  getMarginTrendArrow,
  getMarginTrendColor,
  getSetUnitEquivalent,
  isCodeAddDraftValid,
  parseOptionalPrice,
  resolveBarcodeDisclosure,
  resolveCodeSections,
  resolveOriginalDisplay,
} from "./product-query-presentation";
import type { MultiCodeEditableItem, ProductSetCodeItem } from "./types";

// 毛利：与页面显示口径一致（整数百分比），非法输入不给值。
assert.equal(calcGrossMarginPercent(10, 6), 40);
assert.equal(calcGrossMarginPercent(6.99, 3.5), 50);
assert.equal(calcGrossMarginPercent(0, 3), null, "售价为 0 不计算毛利");
assert.equal(calcGrossMarginPercent(10, -1), null, "进价为负不计算毛利");
assert.equal(calcGrossMarginPercent(null, 1), null);
assert.equal(calcGrossMarginPercent(10, null), null);

// 毛利方向：只比较显示口径的整数值。
assert.equal(getMarginTrend(45, 40), "up");
assert.equal(getMarginTrend(30, 40), "down");
assert.equal(getMarginTrend(40, 40), null, "没有变化不显示箭头");
assert.equal(getMarginTrend(null, 40), null);
assert.equal(getMarginTrend(40, null), null);
assert.equal(getMarginTrendColor("up", "#000"), "#067647");
assert.equal(getMarginTrendColor("down", "#000"), "#B42318");
assert.equal(getMarginTrendColor(null, "#0958D9"), "#0958D9");
assert.equal(getMarginTrendArrow("up"), "↑");
assert.equal(getMarginTrendArrow("down"), "↓");
assert.equal(getMarginTrendArrow(null), "");

// 原值：只有与修改前不同才显示；原来为空时显示占位。
assert.equal(resolveOriginalDisplay("7.99", "6.99"), "6.99");
assert.equal(resolveOriginalDisplay("6.99", "6.99"), null);
assert.equal(resolveOriginalDisplay("5.00", ""), "--");
assert.equal(resolveOriginalDisplay("5.00", null), null, "没有基线时不显示原值");

// 套装换算：保留 1 位小数；主零售价为空或 0 不给参考。
assert.equal(getSetUnitEquivalent(10, 2.99), 3.3);
assert.equal(getSetUnitEquivalent(12, 4), 3);
assert.equal(getSetUnitEquivalent(10, 0), null);
assert.equal(getSetUnitEquivalent(10, null), null);
assert.equal(getSetUnitEquivalent(null, 2), null);
assert.equal(getSetUnitEquivalent(0, 2), null);

// 条码折叠：无条码不提供切换；折叠时不渲染条码图。
assert.deepEqual(resolveBarcodeDisclosure(" 9300001 ", false), { value: "9300001", canToggle: true, showImage: false });
assert.deepEqual(resolveBarcodeDisclosure("9300001", true), { value: "9300001", canToggle: true, showImage: true });
assert.deepEqual(resolveBarcodeDisclosure("  ", true), { value: "", canToggle: false, showImage: false });
assert.deepEqual(resolveBarcodeDisclosure(null, true), { value: "", canToggle: false, showImage: false });

// 编码页签：与原页面条件逐项等价。
assert.deepEqual(resolveCodeSections(null), { hasCodeSection: false, showSet: false, showMulti: false, count: 0 });
assert.deepEqual(
  resolveCodeSections({ productType: 0, setCodeCount: 0, multiCodeCount: 0 }),
  { hasCodeSection: false, showSet: false, showMulti: false, count: 0 },
);
assert.deepEqual(
  resolveCodeSections({ productType: 1, setCodeCount: 3, multiCodeCount: 0 }),
  { hasCodeSection: true, showSet: true, showMulti: false, count: 3 },
);
assert.deepEqual(
  resolveCodeSections({ productType: 2, setCodeCount: 4, multiCodeCount: 5 }),
  { hasCodeSection: true, showSet: false, showMulti: true, count: 5 },
  "多码类型不显示套装列表",
);
assert.deepEqual(
  resolveCodeSections({ productType: 0, setCodeCount: 2, multiCodeCount: 0 }),
  { hasCodeSection: true, showSet: true, showMulti: false, count: 2 },
);
assert.deepEqual(
  resolveCodeSections({ productType: 0, setCodeCount: 2, multiCodeCount: 1 }),
  { hasCodeSection: true, showSet: false, showMulti: true, count: 1 },
);
assert.deepEqual(
  resolveCodeSections({ productType: 1, setCodeCount: 2, multiCodeCount: 1 }),
  { hasCodeSection: true, showSet: true, showMulti: true, count: 3 },
);

// 编码行差异：按 setCodeId（历史多码按 UUID）与基线比对。
const setItem = (id: string, barcode: string, price: number): ProductSetCodeItem => ({
  setCodeId: id, productCode: "P", setProductCode: "SP", setItemNumber: "I", setBarcode: barcode,
  setRetailPrice: price, setQuantity: 1, setType: 1, isActive: true,
});
const multiItem = (setCodeId: string, uuid: string, barcode: string, price: number | null): MultiCodeEditableItem => ({
  uuid, setCodeId, barcode, retailPrice: price, isAutoPricing: false, isSpecialProduct: false, isActive: true,
});
const base = {
  setCodes: [setItem("s1", "111", 10), setItem("s2", "222", 12)],
  multiCodes: [multiItem("m1", "u1", "333", null), multiItem("", "u2", "444", 5)],
};
assert.deepEqual([...getDirtyCodeIds(base, base)], []);
assert.deepEqual(
  [...getDirtyCodeIds({
    setCodes: [setItem("s1", "111", 11), setItem("s2", "222", 12)],
    multiCodes: [multiItem("m1", "u1", "333", null), multiItem("", "u2", "445", 5)],
  }, base)].sort(),
  ["s1", "u2"],
);
assert.deepEqual([...getDirtyCodeIds(base, null)], [], "没有基线不判定差异");
assert.deepEqual(
  [...getDirtyCodeIds({ setCodes: [setItem("s9", "999", 1)], multiCodes: [] }, base)],
  [],
  "分页新加载、基线里没有的行不算改动",
);

// 新增编码面板：条码必填，套装价须为正数，多码不要求零售价。
assert.equal(isCodeAddDraftValid("set", "123", "9.99"), true);
assert.equal(isCodeAddDraftValid("set", "123", ""), false);
assert.equal(isCodeAddDraftValid("set", "123", "0"), false);
assert.equal(isCodeAddDraftValid("set", "123", "abc"), false);
assert.equal(isCodeAddDraftValid("set", "  ", "9.99"), false);
assert.equal(isCodeAddDraftValid("multi", "123", ""), true);
assert.equal(isCodeAddDraftValid("multi", " ", ""), false);
assert.equal(parseOptionalPrice(" 3.5 "), 3.5);
assert.equal(parseOptionalPrice(""), null);
assert.equal(parseOptionalPrice("x"), null);

console.log("product-query-presentation.test.ts: ok");
