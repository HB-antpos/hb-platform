import {
  buildSeasonalCardBatchRequest,
  buildSeasonalCardOverviewQuery,
  buildSeasonalCardSubmissionPayload,
  buildSeasonalCardSubmissionQuery,
  normalizeSeasonalCardBatchResponse,
  normalizeSeasonalCardCatalogResponse,
  normalizeSeasonalCardOverviewResponse,
  normalizeSeasonalCardSubmissionsResponse,
  normalizeServerUtcTimestamp,
} from "./api";

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

const submissionPayload = buildSeasonalCardSubmissionPayload({
  storeCode: " STO01 ",
  catalogGuid: " catalog-1 ",
  seasonYear: 2026.8,
  remainingQuantity: 12.9,
  customUnitPrice: 4.5,
  remark: "  urgent  ",
});

assertDeepEqual(
  submissionPayload,
  {
    storeCode: "STO01",
    catalogGuid: "catalog-1",
    seasonYear: 2026,
    remainingQuantity: 12,
    customUnitPrice: 4.5,
    remark: "urgent",
  },
  "submission payload trims text fields and coerces numeric values"
);

const queryPayload = buildSeasonalCardSubmissionQuery({
  storeCode: " STO01 ",
  cardType: 2.9,
  seasonYear: 2026.2,
  pageNumber: 2.9,
  pageSize: 50.1,
});

assertDeepEqual(
  queryPayload,
  {
    storeCode: "STO01",
    cardType: 2,
    seasonYear: 2026,
    pageNumber: 2,
    pageSize: 50,
  },
  "history query sends numeric card type enum and keeps supported pagination"
);

const defaultQueryPayload = buildSeasonalCardSubmissionQuery({
  storeCode: " ",
  pageNumber: 0,
  pageSize: 999,
});

assertEqual(defaultQueryPayload.storeCode, undefined, "blank store code is omitted from query");
assertEqual(defaultQueryPayload.pageNumber, 1, "invalid query page defaults to first page");
assertEqual(defaultQueryPayload.pageSize, 20, "invalid query page size defaults to 20");

const catalog = normalizeSeasonalCardCatalogResponse({
  success: true,
  data: [
    {
      catalogGuid: "catalog-1",
      cardType: 1,
      cardTypeName: "Christmas",
      priceOption: 1,
      priceOptionName: "$1",
      priceLabel: "$1",
      fixedUnitPrice: "1.00",
      allowsCustomUnitPrice: false,
      isEnabled: true,
      sortOrder: 1,
    },
    {
      catalogGuid: "catalog-2",
      cardType: 1,
      cardTypeName: "Christmas",
      priceOption: 4,
      priceOptionName: "Other",
      priceLabel: "其他",
      fixedUnitPrice: null,
      allowsCustomUnitPrice: 1,
      isEnabled: 0,
      sortOrder: 99,
    },
  ],
});

assertEqual(catalog.length, 2, "catalog response reads array directly from envelope data");
assertEqual(catalog[0]?.catalogGuid, "catalog-1", "catalog response normalizes catalog guid");
assertEqual(catalog[0]?.cardType, 1, "catalog response keeps numeric card type enum");
assertEqual(catalog[0]?.cardTypeName, "Christmas", "catalog response keeps card type name");
assertEqual(catalog[0]?.priceOption, 1, "catalog response keeps numeric price option enum");
assertEqual(catalog[0]?.priceLabel, "$1", "catalog response keeps display price label");
assertEqual(catalog[0]?.fixedUnitPrice, 1, "catalog response normalizes fixed unit price");
assertEqual(catalog[0]?.isEnabled, true, "catalog response normalizes enabled flag");
assertEqual(catalog[1]?.allowsCustomUnitPrice, true, "catalog response normalizes custom price flag");
assertEqual(catalog[1]?.isEnabled, false, "catalog response normalizes disabled catalog entries");

const unwrappedCatalog = normalizeSeasonalCardCatalogResponse([
  {
    catalogGuid: "catalog-unwrapped",
    cardType: 5,
    priceOption: 3,
    priceLabel: "$3",
    fixedUnitPrice: 3,
    isEnabled: true,
  },
]);

assertEqual(
  unwrappedCatalog.length,
  1,
  "catalog response keeps arrays already unwrapped by apiClient"
);
assertEqual(
  unwrappedCatalog[0]?.catalogGuid,
  "catalog-unwrapped",
  "catalog response normalizes already unwrapped catalog rows"
);

const submissions = normalizeSeasonalCardSubmissionsResponse({
  success: true,
  data: {
    items: [
      {
        submissionGuid: "submission-1",
        storeCode: "STO01",
        catalogGuid: "catalog-1",
        cardType: 3,
        cardTypeName: "Easter",
        seasonYear: "2026",
        unitPrice: "3.50",
        priceLabel: "$3",
        remainingQuantity: "8",
        remark: "legacy",
        submittedByName: "Alice",
        submittedAt: "2026-05-27T10:00:00Z",
      },
    ],
    totalCount: "3",
    page: "2",
    pageSize: "50",
  },
});

assertEqual(submissions.items.length, 1, "history response keeps items");
assertEqual(
  submissions.items[0]?.submissionGuid,
  "submission-1",
  "history response normalizes submission guid"
);
assertEqual(submissions.items[0]?.cardType, 3, "history response keeps numeric card type enum");
assertEqual(submissions.items[0]?.cardTypeName, "Easter", "history response keeps card type name");
assertEqual(submissions.items[0]?.priceLabel, "$3", "history response keeps price label");
assertEqual(submissions.items[0]?.seasonYear, 2026, "history response normalizes season year");
assertEqual(submissions.items[0]?.unitPrice, 3.5, "history response normalizes unit price");
assertEqual(submissions.items[0]?.remainingQuantity, 8, "history response normalizes quantity");
assertEqual(submissions.total, 3, "history response normalizes total count");
assertEqual(submissions.pageNumber, 2, "history response normalizes page from data.page");
assertEqual(submissions.pageSize, 50, "history response normalizes page size");
assertEqual(submissions.items[0]?.batchGuid, "", "legacy history rows have no batch");
assertEqual(submissions.items[0]?.localSupplierCode, "", "legacy history rows have no supplier");

const batchedSubmissions = normalizeSeasonalCardSubmissionsResponse({
  items: [
    {
      submissionGuid: "submission-2",
      catalogGuid: "catalog-1",
      cardType: 1,
      priceOption: 1,
      seasonYear: 2026,
      remainingQuantity: 3,
      submittedAt: "2026-10-02T09:10:00.123",
      localSupplierCode: " SUP-A01 ",
      supplierName: "示例供应商 A",
      batchGuid: "batch-1",
    },
  ],
  total: 1,
});
assertEqual(batchedSubmissions.items[0]?.batchGuid, "batch-1", "history rows keep the batch id");
assertEqual(
  batchedSubmissions.items[0]?.localSupplierCode,
  "SUP-A01",
  "history rows keep the supplier code"
);
assertEqual(
  batchedSubmissions.items[0]?.supplierName,
  "示例供应商 A",
  "history rows keep the supplier name"
);
assertEqual(batchedSubmissions.items[0]?.priceOption, 1, "history rows keep the price option");
assertEqual(
  batchedSubmissions.items[0]?.submittedAt,
  "2026-10-02T09:10:00.123Z",
  "server timestamps without zone are read as UTC"
);

assertEqual(
  normalizeServerUtcTimestamp("2026-10-02T09:10:00Z"),
  "2026-10-02T09:10:00Z",
  "UTC timestamps stay as-is"
);
assertEqual(
  normalizeServerUtcTimestamp("2026-10-02T19:10:00+10:00"),
  "2026-10-02T19:10:00+10:00",
  "timestamps with offsets stay as-is"
);
assertEqual(normalizeServerUtcTimestamp(null), "", "missing timestamps normalize to empty");

assertEqual(
  buildSeasonalCardSubmissionQuery({ storeCode: "1013", localSupplierCode: " SUP-A01 " })
    .localSupplierCode,
  "SUP-A01",
  "history query can filter by supplier"
);
assertEqual(
  "localSupplierCode" in
    buildSeasonalCardSubmissionQuery({ storeCode: "1013", localSupplierCode: " " }),
  false,
  "blank supplier filter is omitted"
);

const overview = normalizeSeasonalCardOverviewResponse({
  storeCode: "1013",
  seasonYear: 2026,
  localSupplierCode: "SUP-A01",
  supplierName: "示例供应商 A",
  holidays: [
    {
      cardType: 1,
      cardTypeName: "圣诞节",
      currentBatch: {
        batchGuid: "batch-1",
        storeCode: "1013",
        seasonYear: 2026,
        cardType: 1,
        localSupplierCode: "SUP-A01",
        supplierName: "示例供应商 A",
        remark: null,
        submittedByName: "店长 王",
        submittedAt: "2026-10-02T09:10:00",
        totalQuantity: 298,
        totalAmount: "640.00",
        isCurrent: true,
        lines: [
          {
            submissionGuid: "s1",
            catalogGuid: "c1",
            priceOption: 1,
            priceLabel: "$1",
            unitPrice: 1,
            remainingQuantity: 150,
          },
          {
            submissionGuid: "s4",
            catalogGuid: "c4",
            priceOption: 4,
            priceLabel: "其他",
            unitPrice: "4.50",
            remainingQuantity: "12",
          },
        ],
      },
    },
    { cardType: 2, cardTypeName: "情人节", currentBatch: null },
    { cardType: 9, cardTypeName: "unknown", currentBatch: null },
  ],
});
assertDeepEqual(
  overview.holidays.map((holiday) => [holiday.cardType, holiday.currentBatch?.batchGuid ?? null]),
  [
    [1, "batch-1"],
    [2, null],
    [3, null],
    [4, null],
    [5, null],
  ],
  "overview always has holidays 1-5; missing or unknown entries count as not submitted"
);
const overviewBatch = overview.holidays[0]?.currentBatch;
assertEqual(overviewBatch?.submittedAt, "2026-10-02T09:10:00Z", "overview batch timestamp is UTC");
assertEqual(overviewBatch?.remark, "", "null remark normalizes to empty");
assertEqual(overviewBatch?.totalAmount, 640, "overview batch amount is numeric");
assertEqual(overviewBatch?.lines[1]?.unitPrice, 4.5, "overview batch lines keep unit price");
assertEqual(overviewBatch?.lines[1]?.remainingQuantity, 12, "overview batch lines keep quantity");
assertEqual(normalizeSeasonalCardBatchResponse(null), null, "null batch stays null");
// 旧后端没有开放窗口字段：按开放兜底，年份沿用顶层 seasonYear。
assertEqual(overview.holidays[0]?.isOpen, true, "missing isOpen counts as open (legacy backend)");
assertEqual(overview.holidays[0]?.seasonYear, 2026, "missing holiday seasonYear falls back to the top level");
assertEqual(overview.holidays[2]?.isOpen, true, "holidays missing from the response count as open");
assertEqual(overview.holidays[0]?.closesOn, "", "missing window dates normalize to empty");
assertEqual(overview.today, "", "missing today normalizes to empty");

const windowOverview = normalizeSeasonalCardOverviewResponse({
  StoreCode: "1013",
  SeasonYear: "2027",
  Today: "2027-01-10",
  Holidays: [
    {
      CardType: 1,
      IsOpen: true,
      SeasonYear: 2026,
      HolidayDate: "2026-12-25",
      OpensOn: "2026-12-25T00:00:00",
      ClosesOn: "2027-01-22",
      CurrentBatch: null,
    },
    {
      CardType: 3,
      IsOpen: false,
      SeasonYear: "2027",
      HolidayDate: "2027-05-09",
      OpensOn: "2027-05-09",
      ClosesOn: "2027-06-06",
    },
    { cardType: 2, isOpen: "false", seasonYear: 2027, opensOn: "bad-date" },
  ],
});
assertEqual(windowOverview.today, "2027-01-10", "overview keeps the store-local today");
assertDeepEqual(
  windowOverview.holidays.map((holiday) => [
    holiday.cardType,
    holiday.isOpen,
    holiday.seasonYear,
    holiday.opensOn,
    holiday.closesOn,
  ]),
  [
    [1, true, 2026, "2026-12-25", "2027-01-22"],
    [2, false, 2027, "", ""],
    [3, false, 2027, "2027-05-09", "2027-06-06"],
    [4, true, 2027, "", ""],
    [5, true, 2027, "", ""],
  ],
  "overview window fields are parsed leniently (PascalCase, string booleans/years, date-times trimmed to days)"
);

assertDeepEqual(
  buildSeasonalCardOverviewQuery({
    storeCode: " 1013 ",
    seasonYear: 2026,
    localSupplierCode: " SUP-A01 ",
  }),
  { storeCode: "1013", seasonYear: 2026, localSupplierCode: "SUP-A01" },
  "overview query trims store and supplier"
);

assertDeepEqual(
  buildSeasonalCardBatchRequest({
    storeCode: " 1013 ",
    seasonYear: 2026,
    cardType: 1,
    localSupplierCode: " SUP-A01 ",
    expectedPreviousBatchGuid: null,
    remark: "  ",
    items: [
      { catalogGuid: " c1 ", remainingQuantity: 3 },
      { catalogGuid: "c4", remainingQuantity: 2, customUnitPrice: 4.5 },
    ],
  }),
  {
    storeCode: "1013",
    seasonYear: 2026,
    cardType: 1,
    localSupplierCode: "SUP-A01",
    expectedPreviousBatchGuid: null,
    items: [
      { catalogGuid: "c1", remainingQuantity: 3 },
      { catalogGuid: "c4", remainingQuantity: 2, customUnitPrice: 4.5 },
    ],
  },
  "batch request sends an explicit null previous batch and omits a blank remark"
);
