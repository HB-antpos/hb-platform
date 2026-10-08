import assert from "node:assert/strict";
import { getVerifiedScanPrintTarget, resolveScannedCodeLabel } from "./scan-label";
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
  kind: "set", codeId: "set-9", barcode: "9527815000021", retailPrice: 3.5, discountRate: 0,
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

console.log("scan-label.test.ts: ok");
