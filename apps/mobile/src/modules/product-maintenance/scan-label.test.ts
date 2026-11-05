import assert from "node:assert/strict";
import {
  getVerifiedScanPrintTarget,
  resolveFocusLabelTarget,
  resolveScannedCodeFocus,
  resolveScannedCodeLabel,
  shouldAutoPrintDiscountLabel,
} from "./scan-label";
import type { ProductDetail, ScanLabelResult } from "./types";

const result: ScanLabelResult = {
  candidates: [{
    productCode: "P1",
    productName: "Tissue",
    matchSource: "ProductBarcode",
    matchValue: "9528503822107",
  }],
  detail: {
    productCode: "P1",
    productName: "Tissue",
    barcode: "9528503822107",
    storePrice: {
      uuid: "price-1",
      storeCode: "1042",
      retailPrice: 1.5,
      discountRate: 0.2,
      isAutoPricing: false,
      isSpecialProduct: false,
      isActive: true,
    },
    clearancePrice: {
      uuid: "clearance-1",
      storeCode: "1042",
      clearanceBarcode: "9528000000001",
      clearancePrice: 0.5,
    },
    setCodes: [],
    multiCodes: [],
    setCodeCount: 1,
    multiCodeCount: 0,
    codesIncluded: false,
  },
  printTarget: {
    kind: "product",
    barcode: "9528503822107",
    retailPrice: 1.5,
    discountRate: 0.2,
    codeId: "price-1",
    productCode: "P1",
    storeCode: "1042",
  },
};

assert.equal(getVerifiedScanPrintTarget(result, "9528503822107", "1042"), result.printTarget);
assert.equal(getVerifiedScanPrintTarget(result, "9528503822107", "other"), null);
assert.equal(getVerifiedScanPrintTarget({ ...result, candidates: [...result.candidates, result.candidates[0]] }, "9528503822107", "1042"), null);
assert.equal(getVerifiedScanPrintTarget({ ...result, printTarget: { ...result.printTarget!, productCode: "P2" } }, "9528503822107", "1042"), null);
assert.equal(getVerifiedScanPrintTarget({ ...result, printTarget: { ...result.printTarget!, retailPrice: 0 } }, "9528503822107", "1042"), null);

const setScan: ScanLabelResult = {
  ...result,
  candidates: [{ ...result.candidates[0], matchSource: "SetBarcode", matchValue: "SET-1" }],
  printTarget: { ...result.printTarget!, kind: "set", barcode: "SET-1", codeId: "set-1", retailPrice: 4, discountRate: null },
};
assert.equal(getVerifiedScanPrintTarget(setScan, "SET-1", "1042"), setScan.printTarget);
assert.equal(getVerifiedScanPrintTarget(setScan, "OTHER", "1042"), null);
// 套装标签只用套装价：主档在本店没有门店价行（快路径不补建）时仍可打套装码；多码 / 主条码仍要求门店价行。
const noStorePriceDetail = { ...result.detail!, storePrice: null };
assert.equal(
  getVerifiedScanPrintTarget({ ...setScan, detail: noStorePriceDetail }, "SET-1", "1042"),
  setScan.printTarget,
);
assert.equal(getVerifiedScanPrintTarget({ ...result, detail: noStorePriceDetail }, "9528503822107", "1042"), null);
const multiScanNoStorePrice: ScanLabelResult = {
  ...setScan,
  detail: noStorePriceDetail,
  printTarget: { ...setScan.printTarget!, kind: "multi", barcode: "SET-1", codeId: "multi-1", discountRate: 0 },
};
assert.equal(getVerifiedScanPrintTarget(multiScanNoStorePrice, "SET-1", "1042"), null);
const multiScan: ScanLabelResult = {
  ...setScan,
  printTarget: { ...setScan.printTarget!, kind: "multi", codeId: "multi-1", discountRate: 0.1 },
};
assert.equal(getVerifiedScanPrintTarget(multiScan, "SET-1", "1042"), multiScan.printTarget);

const clearanceScan: ScanLabelResult = {
  ...result,
  candidates: [{ ...result.candidates[0], matchSource: "ClearanceBarcode", matchValue: "9528000000001" }],
  detail: { ...result.detail!, storePrice: null },
  printTarget: { ...result.printTarget!, kind: "clearance", barcode: "9528000000001", codeId: "clearance-1", retailPrice: 0.5, discountRate: null },
};
assert.equal(getVerifiedScanPrintTarget(clearanceScan, "9528000000001", "1042"), clearanceScan.printTarget);
assert.equal(getVerifiedScanPrintTarget({ ...clearanceScan, printTarget: { ...clearanceScan.printTarget!, retailPrice: 0.6 } }, "9528000000001", "1042"), null);

// 本地匹配（无服务器 printTarget）：套装 / 多码子项不能落回主档标签。
const baseDetail: ProductDetail = { ...result.detail!, codesIncluded: true };
const setDetail: ProductDetail = {
  ...baseDetail,
  productType: 1,
  setCodes: [{
    setCodeId: "set-9", setProductCode: "SP9", setItemNumber: "", setBarcode: "9527815000021",
    setPurchasePrice: 1, setRetailPrice: 3.5, setQuantity: 2, setType: 1, setTypeDescription: "", isActive: true,
  } as ProductDetail["setCodes"][number]],
};
assert.deepEqual(resolveScannedCodeLabel(setDetail, " 9527815000021 "), {
  // 套装子码与多码同口径：折扣跟主档门店价（setDetail 的门店折扣 0.2）。
  kind: "set", codeId: "set-9", barcode: "9527815000021", retailPrice: 3.5, discountRate: 0.2,
});
const multiDetail: ProductDetail = {
  ...baseDetail,
  productType: 2,
  multiCodes: [{
    uuid: "multi-uuid", setCodeId: "", barcode: "MULTI-1", retailPrice: 9.99, discountRate: 0.5,
    isAutoPricing: false, isSpecialProduct: false, isActive: true,
  }],
};
// 多码：多码条码 + 主档门店价 1.5 + 主档折扣 0.2，忽略多码行自身的 9.99 / 0.5；忽略大小写；无 setCodeId 回落 uuid。
assert.deepEqual(resolveScannedCodeLabel(multiDetail, "multi-1"), {
  kind: "multi", codeId: "multi-uuid", barcode: "MULTI-1", retailPrice: 1.5, discountRate: 0.2,
});
assert.deepEqual(
  resolveScannedCodeLabel({ ...multiDetail, storePrice: null }, "MULTI-1"),
  { kind: "multi", codeId: "multi-uuid", barcode: "MULTI-1", retailPrice: null, discountRate: 0 },
);
assert.deepEqual(resolveScannedCodeLabel(baseDetail, "9528503822107"), { kind: "product" });
assert.deepEqual(resolveScannedCodeLabel(baseDetail, "9528000000001"), { kind: "clearance" });
// 码表分页未加载到的码：返回 null，调用方不得回退打印主档。
assert.equal(resolveScannedCodeLabel(baseDetail, "9527815000099"), null);
assert.equal(resolveScannedCodeLabel(baseDetail, "  "), null);

// 自动打印补打折扣标签的判定：与手动「折扣标签」按钮同口径（折扣率 > 0），其余一律不补打。
assert.equal(shouldAutoPrintDiscountLabel(0.2), true);
assert.equal(shouldAutoPrintDiscountLabel(1), true);
assert.equal(shouldAutoPrintDiscountLabel(0), false);
assert.equal(shouldAutoPrintDiscountLabel(-0.1), false);
assert.equal(shouldAutoPrintDiscountLabel(null), false);
assert.equal(shouldAutoPrintDiscountLabel(undefined), false);
assert.equal(shouldAutoPrintDiscountLabel(Number.NaN), false);
assert.equal(shouldAutoPrintDiscountLabel(Number.POSITIVE_INFINITY), false);

// 扫码焦点：服务端目标优先（source=server），套装折扣跟主档门店价；没有服务端目标时按已加载码表本地匹配（source=local）。
assert.deepEqual(
  resolveScannedCodeFocus(baseDetail, "SET-1", setScan.printTarget),
  { kind: "set", codeId: "set-1", barcode: "SET-1", retailPrice: 4, discountRate: 0.2, source: "server" },
);
assert.deepEqual(
  resolveScannedCodeFocus({ ...baseDetail, storePrice: null }, "SET-1", setScan.printTarget)?.discountRate,
  0,
  "主档没有门店价行时套装折扣按 0",
);
assert.deepEqual(
  resolveScannedCodeFocus(setDetail, " 9527815000021 ", null),
  { kind: "set", codeId: "set-9", barcode: "9527815000021", retailPrice: 3.5, discountRate: 0.2, source: "local" },
);
assert.deepEqual(
  resolveScannedCodeFocus(multiDetail, "MULTI-1", null),
  { kind: "multi", codeId: "multi-uuid", barcode: "MULTI-1", retailPrice: 1.5, discountRate: 0.2, source: "local" },
);
assert.deepEqual(
  resolveScannedCodeFocus(baseDetail, "9528503822107", null),
  { kind: "product", codeId: "price-1", barcode: "9528503822107", retailPrice: 1.5, discountRate: 0.2, source: "local" },
);
assert.equal(resolveScannedCodeFocus(baseDetail, "9528000000001", null)?.kind, "clearance");
// 商品名之类的手动关键字不会精确命中，不设焦点；码表分页未含的码同样为 null，由调用方在码表加载后再试。
assert.equal(resolveScannedCodeFocus(baseDetail, "Tissue", null), null);
assert.equal(resolveScannedCodeFocus(baseDetail, "9527815000099", null), null);

// 焦点码打印目标按「当前」详情取价：套装优先码表同行（用户可能刚改过套装价），多码跟随当前主档门店价。
const setFocus = resolveScannedCodeFocus(baseDetail, "SET-1", setScan.printTarget)!;
assert.deepEqual(
  resolveFocusLabelTarget({ storePrice: baseDetail.storePrice, setCodes: [] }, setFocus),
  { kind: "set", codeId: "set-1", barcode: "SET-1", retailPrice: 4, discountRate: 0.2 },
);
assert.deepEqual(
  resolveFocusLabelTarget(
    { storePrice: baseDetail.storePrice, setCodes: [{ ...setDetail.setCodes[0], setBarcode: "SET-1", setRetailPrice: 4.5 }] },
    setFocus,
  ),
  // 按条码匹配到码表行后以该行为准：行 id、行价都取码表里的值。
  { kind: "set", codeId: "set-9", barcode: "SET-1", retailPrice: 4.5, discountRate: 0.2 },
);
const multiFocus = resolveScannedCodeFocus(multiDetail, "MULTI-1", null)!;
assert.deepEqual(
  resolveFocusLabelTarget(
    { storePrice: { ...multiDetail.storePrice!, retailPrice: 1.8, discountRate: 0 }, setCodes: [] },
    multiFocus,
  ),
  { kind: "multi", codeId: "multi-uuid", barcode: "MULTI-1", retailPrice: 1.8, discountRate: 0 },
);
assert.equal(resolveFocusLabelTarget(baseDetail, resolveScannedCodeFocus(baseDetail, "9528503822107", null)!), null);

console.log("scan-label.test.ts: ok");
