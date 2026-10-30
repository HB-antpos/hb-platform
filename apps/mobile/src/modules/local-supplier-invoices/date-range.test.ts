import {
  clearInvoiceDateRange,
  formatInvoiceDateRangeDisplay,
  getDefaultInvoiceDateFilters,
  selectInvoiceDateRange,
  toCreatedAtInstantRange,
  toInvoiceDateRangeFilters,
} from "./date-range";

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

assertDeepEqual(
  formatInvoiceDateRangeDisplay({}, { allDatesLabel: "全部日期" }),
  { kind: "all", text: "全部日期" },
  "empty range uses caller supplied all dates label"
);

assertDeepEqual(
  formatInvoiceDateRangeDisplay({ from: "2026-05-01" }),
  { kind: "from-only", text: "2026-05-01" },
  "from-only range defaults to raw date when no formatFrom provided"
);

assertDeepEqual(
  formatInvoiceDateRangeDisplay(
    { from: "2026-05-01" },
    { formatFrom: (date) => `From ${date}` }
  ),
  { kind: "from-only", text: "From 2026-05-01" },
  "from-only range can use caller supplied localized text"
);

assertDeepEqual(
  formatInvoiceDateRangeDisplay({ to: "2026-05-31" }),
  { kind: "to-only", text: "2026-05-31" },
  "to-only range defaults to raw date when no formatTo provided"
);

assertDeepEqual(
  formatInvoiceDateRangeDisplay(
    { to: "2026-05-31" },
    { formatTo: (date) => `Until ${date}` }
  ),
  { kind: "to-only", text: "Until 2026-05-31" },
  "to-only range can use caller supplied localized text"
);

assertDeepEqual(
  formatInvoiceDateRangeDisplay({ from: "2026-05-01", to: "2026-05-31" }),
  { kind: "range", text: "2026-05-01 ~ 2026-05-31" },
  "full range formats both dates"
);

assertDeepEqual(
  selectInvoiceDateRange({}, "2026-05-10"),
  { from: "2026-05-10" },
  "first date selection starts range at from"
);

assertDeepEqual(
  selectInvoiceDateRange({ from: "2026-05-10" }, "2026-05-20"),
  { from: "2026-05-10", to: "2026-05-20" },
  "second later date fills to"
);

assertDeepEqual(
  selectInvoiceDateRange({ from: "2026-05-20" }, "2026-05-10"),
  { from: "2026-05-10", to: "2026-05-20" },
  "second earlier date swaps range order automatically"
);

assertDeepEqual(
  selectInvoiceDateRange({ from: "2026-05-01", to: "2026-05-31" }, "2026-06-05"),
  { from: "2026-06-05" },
  "new selection after a full range starts over"
);

assertDeepEqual(
  clearInvoiceDateRange(),
  {},
  "clearing range resets both from and to"
);

assertDeepEqual(
  toInvoiceDateRangeFilters({ from: "2026-05-01", to: "2026-05-31" }),
  { dateFrom: "2026-05-01", dateTo: "2026-05-31" },
  "helper maps a range onto the generic dateFrom/dateTo filter fields"
);

assertEqual(
  Object.prototype.hasOwnProperty.call(toInvoiceDateRangeFilters({}), "dateFrom"),
  false,
  "empty helper mapping omits dateFrom"
);

assertEqual(
  Object.prototype.hasOwnProperty.call(toInvoiceDateRangeFilters({}), "dateTo"),
  false,
  "empty helper mapping omits dateTo"
);

// 默认口径：创建日期、含今天往前数 90 个自然日（与 Web 一致）。
assertDeepEqual(
  getDefaultInvoiceDateFilters(new Date(2026, 9, 30, 15, 20)),
  { dateField: "createdAt", dateFrom: "2026-08-02", dateTo: "2026-10-30" },
  "default invoice date filter is created date, last 90 calendar days including today"
);
assertDeepEqual(
  getDefaultInvoiceDateFilters(new Date(2026, 0, 10)),
  { dateField: "createdAt", dateFrom: "2025-10-13", dateTo: "2026-01-10" },
  "default 90-day window crosses the year boundary"
);
assertDeepEqual(
  getDefaultInvoiceDateFilters(new Date(2028, 2, 1)),
  { dateField: "createdAt", dateFrom: "2027-12-03", dateTo: "2028-03-01" },
  "default 90-day window counts the leap day"
);

// 创建时间区间：本地日期 -> 带时区时刻 [起日 0 点, 止日次日 0 点)。期望值同样用本地 Date 构造，测试与所在时区无关。
assertDeepEqual(
  toCreatedAtInstantRange({ from: "2026-10-06", to: "2026-10-08" }),
  {
    filter: new Date(2026, 9, 6).toISOString(),
    filterTo: new Date(2026, 9, 9).toISOString(),
  },
  "created-at range starts at local midnight of the first day and ends at midnight after the last day"
);
assertDeepEqual(
  toCreatedAtInstantRange({ from: "2026-10-31", to: "2026-10-31" }),
  {
    filter: new Date(2026, 9, 31).toISOString(),
    filterTo: new Date(2026, 10, 1).toISOString(),
  },
  "single-day created-at range spans exactly that local day and rolls over the month end"
);
assertDeepEqual(
  toCreatedAtInstantRange({ from: "2026-10-06" }, new Date(2026, 9, 30, 9, 0)),
  {
    filter: new Date(2026, 9, 6).toISOString(),
    filterTo: new Date(2026, 9, 31).toISOString(),
  },
  "from-only created-at range ends tomorrow midnight"
);
assertDeepEqual(
  toCreatedAtInstantRange({ to: "2026-10-06" }),
  {
    filter: new Date(2000, 0, 1).toISOString(),
    filterTo: new Date(2026, 9, 7).toISOString(),
  },
  "to-only created-at range starts at an open lower bound"
);
assertEqual(toCreatedAtInstantRange({}), null, "empty created-at range sends no filter");
assertEqual(
  toCreatedAtInstantRange({ from: "2026-02-31", to: "2026-03-05" }),
  null,
  "unparseable created-at date sends no filter instead of a shifted one"
);
