import type { LocalSupplierInvoiceItem } from "@/modules/local-supplier-invoices/types";

/** 仅供 detail/*.test.ts 使用的断言与造数工具（文件名不以 .test 结尾，不会被 CI 当成测试收录）。 */
export function assertEqual(actual: unknown, expected: unknown, label: string) {
  if (actual !== expected) {
    throw new Error(`${label}: expected ${String(expected)}, got ${String(actual)}`);
  }
}

export function assertDeepEqual(actual: unknown, expected: unknown, label: string) {
  const actualText = JSON.stringify(actual);
  const expectedText = JSON.stringify(expected);
  if (actualText !== expectedText) {
    throw new Error(`${label}: expected ${expectedText}, got ${actualText}`);
  }
}

/**
 * 造一行明细：默认是「已检测、主档存在、条码正常、操作类型无、进货价与上次持平」的普通行。
 * overrides 允许传 null / undefined（例如 existingProductCount: null 表示未检测），所以入参放宽为 unknown。
 */
export function makeItem(overrides: Record<string, unknown> = {}): LocalSupplierInvoiceItem {
  return {
    detailGuid: "d-1",
    invoiceGuid: "inv-1",
    storeCode: "S01",
    supplierCode: "SUP01",
    productCode: "P-1",
    storeProductCode: "S01P-1",
    itemNumber: "72750",
    barcode: "9311192727509",
    productName: "Clean Angel Mop",
    specification: "",
    unit: "",
    quantity: 12,
    lastPurchasePrice: 0.44,
    purchasePrice: 0.44,
    retailPrice: 2.5,
    amount: 5.28,
    productImage: "",
    additionalBarcodes: [],
    productType: 0,
    isCreatedByThisInvoice: false,
    existingProductCount: 1,
    barcodeStatus: 1,
    barcodeMatchCount: 1,
    activityType: 0,
    discountRate: null,
    autoPricing: true,
    pricingFloatRate: 2.5,
    newAutoRetailPrice: 1.5,
    isSpecialProduct: false,
    storeIsAutoPricing: true,
    ...overrides,
  } as LocalSupplierInvoiceItem;
}
