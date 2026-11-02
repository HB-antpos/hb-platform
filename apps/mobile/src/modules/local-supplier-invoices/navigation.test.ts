import {
  LOCAL_SUPPLIER_INVOICES_SOURCE,
  buildLocalSupplierInvoicesRestoreHref,
  buildLocalSupplierInvoicesReturnParams,
  decodeLocalSupplierInvoicesReturnParams,
} from "./navigation";

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

const params = buildLocalSupplierInvoicesReturnParams({
  returnInvoiceGuid: "invoice-1",
  returnDetailGuid: "detail-7",
  returnDetailsPage: 3,
  returnDetailsPageSize: 100,
  returnDetailPriceChangeFilter: "up",
  returnDetailSearch: " ITEM-7 ",
  returnListPage: 2,
  returnListPageSize: 50,
  filters: {
    storeCode: " S01 ",
    supplierCode: "SUP01",
    invoiceNo: " INV-9 ",
    inboundStatus: 2,
    dateField: "orderDate",
    dateFrom: "2026-05-01",
    dateTo: "2026-05-18",
    productChecked: false,
    hasUncreatedProducts: true,
    productKeyword: " 9328644 ",
  },
  sort: {
    colId: "InvoiceNo",
    direction: "asc",
  },
});

assertEqual(
  params.source,
  LOCAL_SUPPLIER_INVOICES_SOURCE,
  "return params mark the local supplier invoices source"
);
assertEqual(params.returnListPage, "2", "return list page is encoded");
assertEqual(params.returnDetailsPageSize, "100", "return detail page size is encoded");
assertEqual(params.returnDetailGuid, "detail-7", "return detail row anchor is encoded");
assertEqual(params.returnDetailPriceChangeFilter, "up", "detail price filter is encoded");
assertEqual(params.returnDetailSearch, "ITEM-7", "detail search is trimmed when encoded");
assertEqual(params.returnFilterStoreCode, "S01", "filter values are trimmed when encoded");
assertEqual(params.returnFilterInboundStatus, "2", "inbound status is encoded");
assertEqual(params.returnFilterProductKeyword, "9328644", "product keyword is trimmed and encoded");
assertEqual(params.returnFilterDateField, "orderDate", "date field is encoded");
assertEqual(params.returnFilterDateFrom, "2026-05-01", "date range start is encoded");
assertEqual(params.returnFilterProductChecked, "false", "pending-check filter is encoded as false, not dropped");
assertEqual(params.returnFilterHasUncreatedProducts, "true", "uncreated-products filter is encoded");
assertEqual(params.returnSortColId, "InvoiceNo", "sort column is encoded");

const decoded = decodeLocalSupplierInvoicesReturnParams({
  source: LOCAL_SUPPLIER_INVOICES_SOURCE,
  returnInvoiceGuid: "invoice-1",
  returnDetailGuid: "detail-7",
  returnDetailsPage: "3",
  returnDetailsPageSize: "100",
  returnDetailPriceChangeFilter: "up",
  returnDetailSearch: "ITEM-7",
  returnListPage: "2",
  returnListPageSize: "50",
  returnFilterStoreCode: "S01",
  returnFilterSupplierCode: "SUP01",
  returnFilterInvoiceNo: "INV-9",
  returnFilterInboundStatus: "2",
  returnFilterDateField: "orderDate",
  returnFilterDateFrom: "2026-05-01",
  returnFilterDateTo: "2026-05-18",
  returnFilterProductChecked: "false",
  returnFilterHasUncreatedProducts: "true",
  returnFilterProductKeyword: "9328644",
  returnSortColId: "InvoiceNo",
  returnSortDirection: "asc",
});

assertDeepEqual(
  decoded,
  {
    source: LOCAL_SUPPLIER_INVOICES_SOURCE,
    returnInvoiceGuid: "invoice-1",
    returnDetailGuid: "detail-7",
    returnDetailsPage: 3,
    returnDetailsPageSize: 100,
    returnDetailPriceChangeFilter: "up",
    returnDetailSearch: "ITEM-7",
    returnListPage: 2,
    returnListPageSize: 50,
    filters: {
      storeCode: "S01",
      supplierCode: "SUP01",
      invoiceNo: "INV-9",
      inboundStatus: 2,
      dateField: "orderDate",
      dateFrom: "2026-05-01",
      dateTo: "2026-05-18",
      productChecked: false,
      hasUncreatedProducts: true,
      productKeyword: "9328644",
    },
    sort: {
      colId: "InvoiceNo",
      direction: "asc",
    },
  },
  "decode restores invoice state from route params"
);

assertEqual(
  decodeLocalSupplierInvoicesReturnParams({
    source: LOCAL_SUPPLIER_INVOICES_SOURCE,
    returnInvoiceGuid: "invoice-1",
    returnDetailsPageSize: "20",
  }),
  null,
  "decode rejects invalid detail page sizes"
);

assertEqual(
  decodeLocalSupplierInvoicesReturnParams({
    source: "other-screen",
    returnInvoiceGuid: "invoice-1",
  }),
  null,
  "decode ignores unrelated sources"
);

const restoreHref = buildLocalSupplierInvoicesRestoreHref({
  source: LOCAL_SUPPLIER_INVOICES_SOURCE,
  returnInvoiceGuid: "invoice-1",
  returnDetailGuid: "detail-7",
  returnDetailsPage: 3,
  returnDetailsPageSize: 100,
  returnDetailPriceChangeFilter: "down",
  returnDetailSearch: "barcode-7",
  returnListPage: 2,
  returnListPageSize: 50,
  filters: {
    storeCode: "S01",
  },
  sort: {
    colId: "InvoiceNo",
    direction: "asc",
  },
});

assertEqual(
  restoreHref.pathname,
  "/(shell)/local-supplier-invoices",
  "restore href targets the invoice screen"
);
assertEqual(
  restoreHref.params.returnInvoiceGuid,
  "invoice-1",
  "restore href includes encoded params"
);

// 默认状态（创建日期 + 创建时间倒序 + 无检测筛选）往返后必须与原筛选对象一致，否则回跳恢复会因为对象不等而卡住。
const defaultState = {
  returnInvoiceGuid: "invoice-2",
  returnDetailsPage: 1,
  returnDetailsPageSize: 50 as const,
  returnDetailPriceChangeFilter: "all" as const,
  returnDetailSearch: "",
  returnListPage: 1,
  returnListPageSize: 20 as const,
  filters: {
    dateField: "createdAt" as const,
    dateFrom: "2026-08-02",
    dateTo: "2026-10-30",
  },
  sort: { colId: "CreatedAt" as const, direction: "desc" as const },
};
const defaultParams = buildLocalSupplierInvoicesReturnParams(defaultState);
assertEqual(defaultParams.returnSortColId, "CreatedAt", "created time sort survives the round trip");
assertEqual(defaultParams.returnFilterProductChecked, "", "no check-status filter is encoded as empty");
assertEqual(defaultParams.returnFilterHasUncreatedProducts, "", "disabled uncreated filter is encoded as empty");
const defaultDecoded = decodeLocalSupplierInvoicesReturnParams(defaultParams);
assertDeepEqual(
  defaultDecoded?.filters,
  {
    storeCode: undefined,
    supplierCode: undefined,
    invoiceNo: undefined,
    inboundStatus: undefined,
    dateField: "createdAt",
    dateFrom: "2026-08-02",
    dateTo: "2026-10-30",
    productChecked: undefined,
    hasUncreatedProducts: undefined,
    productKeyword: undefined,
  },
  "default filters decode back to the same normalized shape"
);
assertEqual(defaultDecoded?.sort.colId, "CreatedAt", "created time sort column is accepted on decode");

// 缺少日期字段参数（例如手工拼的回跳）按默认的创建日期处理，未知取值也不能落成别的字段。
assertEqual(
  decodeLocalSupplierInvoicesReturnParams({ ...defaultParams, returnFilterDateField: "" })?.filters.dateField,
  "createdAt",
  "missing date field falls back to created date"
);
assertEqual(
  decodeLocalSupplierInvoicesReturnParams({ ...defaultParams, returnFilterDateField: "bogus" })?.filters.dateField,
  "createdAt",
  "unknown date field falls back to created date"
);
// 已检测（true）与待检测（false）都必须能还原，不能被当成「未筛选」。
assertEqual(
  decodeLocalSupplierInvoicesReturnParams({ ...defaultParams, returnFilterProductChecked: "true" })?.filters.productChecked,
  true,
  "checked filter is restored"
);
assertEqual(
  decodeLocalSupplierInvoicesReturnParams({ ...defaultParams, returnFilterProductChecked: "false" })?.filters.productChecked,
  false,
  "pending-check filter is restored"
);
