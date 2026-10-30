import {
  filterSalesAnalysisRows,
  getSalesAnalysisTotals,
  getSellThroughRatio,
  sortSalesAnalysisRows,
} from "./sales-analysis";
import { normalizeInvoiceSalesAnalysis } from "./api";
import type { InvoiceSalesAnalysisRow } from "./types";

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

function row(overrides: Partial<InvoiceSalesAnalysisRow>): InvoiceSalesAnalysisRow {
  return {
    detailGuid: "d",
    productCode: "P",
    itemNumber: "",
    barcode: "",
    productName: "",
    quantity: 10,
    purchasePrice: 2,
    retailPrice: 4,
    amount: 20,
    salesQty30: 0,
    salesQty60: 0,
    salesQty90: 0,
    previousPurchaseDate: null,
    previousToCurrentDays: null,
    salesSincePreviousPurchase: null,
    salesStatisticLastUpdate: null,
    ...overrides,
  };
}

const rows = [
  row({ detailGuid: "a", itemNumber: "A-100", barcode: "9300001", productName: "Red Cup", salesQty30: 5, salesQty90: 9 }),
  row({ detailGuid: "b", itemNumber: "B-200", barcode: "9300002", productName: "Blue Plate", salesQty30: 12, salesQty90: 12 }),
  row({ detailGuid: "c", itemNumber: "C-300", barcode: "9300003", productName: "Green Bowl", salesQty30: 0, salesQty90: 0 }),
  row({ detailGuid: "d", itemNumber: "D-400", barcode: "9300004", productName: "Zero Two", salesQty30: 0, salesQty90: 0 }),
];

const ids = (list: InvoiceSalesAnalysisRow[]) => list.map((item) => item.detailGuid).join("");

// 搜索：货号 / 条码 / 商品编码 / 商品名，忽略大小写与首尾空白。
assertEqual(ids(filterSalesAnalysisRows(rows, "  red ")), "a", "search matches product name case-insensitively");
assertEqual(ids(filterSalesAnalysisRows(rows, "9300003")), "c", "search matches barcode");
assertEqual(ids(filterSalesAnalysisRows(rows, "b-200")), "b", "search matches item number");
assertEqual(filterSalesAnalysisRows(rows, "   "), rows, "blank keyword returns the original list");
assertEqual(filterSalesAnalysisRows(rows, "nope").length, 0, "no match returns empty");

// 排序：默认不动；其余返回新数组且不改入参；同销量保持原有相对顺序。
assertEqual(sortSalesAnalysisRows(rows, "default"), rows, "default sort keeps the backend order");
assertEqual(ids(sortSalesAnalysisRows(rows, "sales30Desc")), "bacd", "30-day sales descending");
assertEqual(ids(sortSalesAnalysisRows(rows, "sales90Desc")), "bacd", "90-day sales descending is stable for ties");
assertEqual(ids(sortSalesAnalysisRows(rows, "sales90Asc")), "cdab", "90-day sales ascending lists slow movers first");
assertEqual(ids(rows), "abcd", "sorting never mutates the input");

// 合计。
assertDeepEqual(
  getSalesAnalysisTotals(rows),
  { rowCount: 4, quantity: 40, amount: 80, salesQty30: 17, salesQty60: 0, salesQty90: 21, zeroSales90Count: 2 },
  "totals sum the visible rows and count zero-sales products"
);
assertDeepEqual(
  getSalesAnalysisTotals([]),
  { rowCount: 0, quantity: 0, amount: 0, salesQty30: 0, salesQty60: 0, salesQty90: 0, zeroSales90Count: 0 },
  "empty totals are all zero"
);
assertEqual(
  getSalesAnalysisTotals([row({ quantity: null, amount: null, salesQty90: 3 })]).quantity,
  0,
  "null quantity counts as zero in totals"
);

// 售罄比：进货数量缺失或非正时不显示，避免除零。
assertEqual(getSellThroughRatio(5, 10), 0.5, "sell-through ratio");
assertEqual(getSellThroughRatio(5, 0), null, "zero purchase quantity has no ratio");
assertEqual(getSellThroughRatio(5, null), null, "missing purchase quantity has no ratio");
assertEqual(getSellThroughRatio(15, 10), 1.5, "ratio above one is returned as-is");

// 归一化：兼容 PascalCase、缺失字段给默认值、丢弃没有 detailGUID 的行、信封未解开也能读。
const normalized = normalizeInvoiceSalesAnalysis({
  success: true,
  data: {
    InvoiceGUID: "inv-1",
    InvoiceNo: "NO-1",
    StoreName: "Store A",
    SupplierName: "Sup A",
    OrderDate: "2026-10-20T00:00:00",
    AnalysisDate: "2026-10-30T00:00:00",
    Items: [
      { DetailGUID: "d1", ItemNumber: "X1", ProductName: "Cup", Quantity: 6, SalesQty30: 2, SalesQty60: 3, SalesQty90: 4, PreviousToCurrentDays: 14 },
      { ItemNumber: "no-guid" },
    ],
  },
});
assertEqual(normalized.invoiceNo, "NO-1", "envelope-wrapped payload is read");
assertEqual(normalized.items.length, 1, "rows without detailGUID are dropped");
assertEqual(normalized.items[0].salesQty90, 4, "PascalCase sales fields are read");
assertEqual(normalized.items[0].previousToCurrentDays, 14, "interval days are read");
assertEqual(normalized.items[0].previousPurchaseDate, null, "missing previous purchase date is null");
assertEqual(normalized.inboundDate, null, "missing inbound date is null");
assertEqual(normalized.calculationNote.includes("30/60/90"), true, "missing note falls back to the default text");
assertEqual(normalizeInvoiceSalesAnalysis(null).items.length, 0, "null payload yields an empty analysis");
