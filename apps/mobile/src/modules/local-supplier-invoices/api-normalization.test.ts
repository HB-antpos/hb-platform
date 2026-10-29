import {
  buildInvoiceDetailsGridRequest,
  buildInvoiceGridRequest,
  normalizeActiveLocalSuppliersResponse,
  normalizeBarcodeMatchedProduct,
  normalizeCheckProductsResponse,
  normalizeInvoiceDetailsGridResponse,
  normalizeInvoiceDetailsResponse,
  normalizeInvoiceGridResponse,
  normalizeProductsByBarcodeResponse,
} from "./api";
import { getInvoiceInboundStatusLabel } from "./types";

function assertEqual(actual: unknown, expected: unknown, label: string) {
  if (actual !== expected) {
    throw new Error(`${label}: expected ${String(expected)}, got ${String(actual)}`);
  }
}

function assertDeepEqual(actual: unknown, expected: unknown, label: string) {
  const actualText = JSON.stringify(actual);
  const expectedText = JSON.stringify(expected);
  if (actualText !== expectedText) {
    throw new Error(`${label}: expected ${expectedText}, got ${actualText}`);
  }
}

assertEqual(getInvoiceInboundStatusLabel(0), "notReceived", "inbound status 0 is not received");
assertEqual(getInvoiceInboundStatusLabel(1), "partial", "inbound status 1 is partial");
assertEqual(getInvoiceInboundStatusLabel(2), "received", "inbound status 2 is received");
assertEqual(getInvoiceInboundStatusLabel(99), "unknown", "unknown inbound status stays neutral");

const defaultListRequest = buildInvoiceGridRequest({});
assertEqual(defaultListRequest.startRow, 0, "default list starts from first row");
assertEqual(defaultListRequest.endRow, 20, "default list end row uses page size 20");
assertEqual(defaultListRequest.pageSize, 20, "default list page size is 20");
assertDeepEqual(
  defaultListRequest.sortModel,
  [{ colId: "OrderDate", sort: "desc" }],
  "default list sort is order date descending"
);

const filteredListRequest = buildInvoiceGridRequest({
  page: 2,
  pageSize: 50,
  filters: {
    storeCode: " S01 ",
    supplierCode: " SUP ",
    invoiceNo: " INV ",
    inboundStatus: 1,
    orderDateFrom: "2026-01-02",
    orderDateTo: "2026-01-05",
    productKeyword: " 9328644 ",
  },
  sort: { colId: "InvoiceNo", direction: "asc" },
});
assertEqual(filteredListRequest.startRow, 50, "second list page starts at row 50");
assertEqual(filteredListRequest.endRow, 100, "second list page ends at row 100");
assertEqual(filteredListRequest.pageSize, 50, "list page size accepts 50");
assertDeepEqual(
  filteredListRequest.filterModel,
  {
    storeCode: { filterType: "text", type: "equals", filter: "S01" },
    supplierCode: { filterType: "text", type: "contains", filter: "SUP" },
    invoiceNo: { filterType: "text", type: "contains", filter: "INV" },
    ProductKeyword: { filterType: "text", type: "contains", filter: "9328644" },
    inboundStatus: { filterType: "number", type: "equals", filter: "1" },
    OrderDate: {
      filterType: "date",
      type: "inRange",
      filter: "2026-01-02",
      filterTo: "2026-01-05",
    },
  },
  "list filters trim text fields, send OrderDate range and ProductKeyword"
);

const blankDateRequest = buildInvoiceGridRequest({
  filters: {
    storeCode: " STO ",
    supplierCode: " LOC ",
    orderDateFrom: "   ",
    orderDateTo: "",
    productKeyword: "   ",
  },
});
assertDeepEqual(
  blankDateRequest.filterModel,
  {
    storeCode: { filterType: "text", type: "equals", filter: "STO" },
    supplierCode: { filterType: "text", type: "contains", filter: "LOC" },
  },
  "blank order date and product keyword values are not sent while store and supplier filters remain correct"
);

const dateFromOnlyRequest = buildInvoiceGridRequest({
  filters: {
    orderDateFrom: "2026-02-01",
  },
});
assertDeepEqual(
  dateFromOnlyRequest.filterModel,
  {
    OrderDate: {
      filterType: "date",
      type: "greaterThanOrEqual",
      filter: "2026-02-01",
    },
  },
  "from-only order date uses greaterThanOrEqual grid filter"
);

const dateToOnlyRequest = buildInvoiceGridRequest({
  filters: {
    orderDateTo: "2026-02-28",
  },
});
assertDeepEqual(
  dateToOnlyRequest.filterModel,
  {
    OrderDate: {
      filterType: "date",
      type: "lessThanOrEqual",
      filter: "2026-02-28",
    },
  },
  "to-only order date uses lessThanOrEqual grid filter"
);

const invalidListRequest = buildInvoiceGridRequest({ page: -1, pageSize: 999 });
assertEqual(invalidListRequest.startRow, 0, "invalid list page falls back to first page");
assertEqual(invalidListRequest.pageSize, 20, "invalid list page size falls back to 20");

const detailsRequest = buildInvoiceDetailsGridRequest({ page: 3, pageSize: 100 });
assertEqual(detailsRequest.startRow, 200, "third detail page starts at row 200");
assertEqual(detailsRequest.endRow, 300, "third detail page ends at row 300");
assertEqual(detailsRequest.pageSize, 100, "detail page size accepts 100");

const priceDownDetailsRequest = buildInvoiceDetailsGridRequest({
  priceChange: "down",
  keyword: " BAR-7 ",
});
assertDeepEqual(
  priceDownDetailsRequest.filterModel,
  {
    productKeyword: { filterType: "text", type: "contains", filter: "BAR-7" },
    priceChange: { filterType: "text", type: "equals", filter: "down" },
  },
  "detail price change filter is sent to grid request"
);

const invalidDetailsRequest = buildInvoiceDetailsGridRequest({ pageSize: 20 });
assertEqual(invalidDetailsRequest.pageSize, 50, "invalid detail page size falls back to 50");

const listPayload = normalizeInvoiceGridResponse({
  Items: [
    {
      InvoiceGUID: "invoice-1",
      StoreCode: "S01",
      StoreName: "Sydney",
      SupplierCode: "SUP01",
      SupplierName: "Supplier",
      InvoiceNo: "INV-1",
      OrderDate: "2026-01-05T00:00:00",
      TotalAmount: "12.50",
      PriceIncreaseItemCount: "2",
      PriceDecreaseItemCount: "1",
    },
  ],
  Total: "3",
});
assertEqual(listPayload.items.length, 1, "list payload keeps items");
assertEqual(listPayload.items[0]?.invoiceGuid, "invoice-1", "list normalizes invoice guid");
assertEqual(listPayload.items[0]?.totalAmount, 12.5, "list normalizes numeric amount");
assertEqual(listPayload.items[0]?.priceIncreaseItemCount, 2, "list normalizes price increase count");
assertEqual(listPayload.items[0]?.priceDecreaseItemCount, 1, "list normalizes price decrease count");
assertEqual(listPayload.total, 3, "list normalizes total");

const detailsPayload = normalizeInvoiceDetailsGridResponse({
  items: [
    {
      detailGUID: "detail-1",
      productCode: "P001",
      itemNumber: "IT001",
      barcode: "BAR001",
      productName: "Tea",
      LastPurchasePrice: "3.80",
      purchasePrice: "4.20",
      quantity: "6",
      productImage: "https://example.com/p.png",
    },
  ],
  total: "8",
});
assertEqual(detailsPayload.items.length, 1, "detail payload keeps items");
assertEqual(detailsPayload.items[0]?.detailGuid, "detail-1", "detail normalizes guid");
assertEqual(detailsPayload.items[0]?.lastPurchasePrice, 3.8, "detail normalizes last purchase price");
assertEqual(detailsPayload.items[0]?.purchasePrice, 4.2, "detail normalizes purchase price");
assertEqual(detailsPayload.items[0]?.quantity, 6, "detail normalizes quantity");
assertEqual(detailsPayload.total, 8, "detail normalizes total");

const activeSuppliersEnvelope = normalizeActiveLocalSuppliersResponse({
  success: true,
  data: [
    {
      LocalSupplierCode: "LOC-01",
      LocalSupplierName: "Local Supplier One",
    },
    {
      supplierCode: "LOC-02",
      Name: "Local Supplier Two",
    },
    {
      LocalSupplierName: "Missing Code",
    },
  ],
  message: "ok",
});
assertEqual(activeSuppliersEnvelope.length, 2, "active suppliers keep only rows with supplier code");
assertDeepEqual(
  activeSuppliersEnvelope,
  [
    {
      supplierCode: "LOC-01",
      supplierName: "Local Supplier One",
    },
    {
      supplierCode: "LOC-02",
      supplierName: "Local Supplier Two",
    },
  ],
  "active suppliers normalize LocalSupplier, camelCase, and backend Name fields from envelope data"
);

const activeSuppliersArray = normalizeActiveLocalSuppliersResponse([
  {
    LocalSupplierCode: "LOC-03",
    supplierName: "Mixed Case Supplier",
  },
]);
assertDeepEqual(
  activeSuppliersArray,
  [
    {
      supplierCode: "LOC-03",
      supplierName: "Mixed Case Supplier",
    },
  ],
  "active suppliers also normalize direct array payloads"
);

// ---- 一期新增：与 Web 对齐的明细字段、全量明细、匹配商品、检测结果 ----
const fullDetails = normalizeInvoiceDetailsResponse([
  {
    DetailGUID: "d-1",
    ItemNumber: "SB-5001",
    Barcode: "9300001234567",
    AdditionalBarcodes: ["9300001234574", "", 5],
    ProductType: 2,
    IsCreatedByThisInvoice: true,
    ExistingProductCount: "1",
    BarcodeStatus: 1,
    BarcodeMatchCount: "1",
    ActivityType: 2,
    DiscountRate: "0.1",
    AutoPricing: true,
    PricingFloatRate: "2",
    NewAutoRetailPrice: "24.99",
    IsSpecialProduct: 0,
    StoreIsAutoPricing: null,
  },
  { detailGUID: "d-2" },
]);
assertEqual(fullDetails.length, 2, "full details payload keeps all rows");
assertDeepEqual(fullDetails[0]?.additionalBarcodes, ["9300001234574"], "additional barcodes drop blanks and non-strings");
assertEqual(fullDetails[0]?.productType, 2, "product type normalized");
assertEqual(fullDetails[0]?.isCreatedByThisInvoice, true, "created-by-this-invoice flag normalized");
assertEqual(fullDetails[0]?.existingProductCount, 1, "existing product count normalized");
assertEqual(fullDetails[0]?.barcodeMatchCount, 1, "barcode match count normalized");
assertEqual(fullDetails[0]?.activityType, 2, "activity type normalized");
assertEqual(fullDetails[0]?.discountRate, 0.1, "discount rate stays decimal");
assertEqual(fullDetails[0]?.autoPricing, true, "auto pricing normalized");
assertEqual(fullDetails[0]?.newAutoRetailPrice, 24.99, "new auto retail price normalized");
assertEqual(fullDetails[0]?.isSpecialProduct, false, "numeric 0 means not special product");
assertEqual(fullDetails[0]?.storeIsAutoPricing, null, "store pricing state stays null when absent");
assertEqual(fullDetails[1]?.productType, null, "missing product type is null");
assertEqual(fullDetails[1]?.autoPricing, null, "missing auto pricing is null, not false");
assertEqual(fullDetails[1]?.activityType, 0, "missing activity type defaults to none");
assertEqual(fullDetails[1]?.existingProductCount, null, "missing existing product count stays null (unchecked), not 0");
assertEqual(normalizeInvoiceDetailsResponse({ data: [{ detailGUID: "d-3" }] }).length, 1, "details accept envelope data");
assertEqual(normalizeInvoiceDetailsResponse(null).length, 0, "details tolerate empty payload");

const matched = normalizeProductsByBarcodeResponse({
  barcode: "9300002233445",
  matchedProducts: [
    { ProductCode: "P9", ProductName: "Board", SupplierCode: "S1", SupplierName: "Sunrise", ItemNumber: "BB-210A", Barcode: "9300002233445", IsMultiCode: 1, IsBundle: false, ProductType: 2 },
  ],
});
assertEqual(matched.length, 1, "products-by-barcode keeps matched products");
assertEqual(matched[0]?.productCode, "P9", "matched product code");
assertEqual(matched[0]?.isMultiCode, true, "matched product multi-code flag");
assertEqual(normalizeBarcodeMatchedProduct(null).productCode, "", "matched product tolerates empty input");

const checked = normalizeCheckProductsResponse({
  results: [
    { detailGuid: "d-1", productStatus: 1, barcodeStatus: 1, existingProductCount: 1, barcodeMatchCount: 1, productInfo: { productCode: "P9" } },
  ],
  summary: { total: 1 },
});
assertEqual(checked[0]?.detailGuid, "d-1", "check result detail guid");
assertEqual(checked[0]?.productCode, "P9", "check result takes product code from productInfo");
assertEqual(normalizeCheckProductsResponse({}).length, 0, "check result tolerates missing results");
